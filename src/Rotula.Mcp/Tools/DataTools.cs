using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Rotula.Data;
using Rotula.Mcp.Hosting;

namespace Rotula.Mcp.Tools;

/// <summary>A saved database connection, without any secret.</summary>
/// <param name="Name">The name to pass to run_query and merge_preview.</param>
/// <param name="Provider">The database engine.</param>
public sealed record ConnectionEntry(string Name, string Provider);

/// <summary>Result of list_database_connections.</summary>
public sealed record ConnectionListResult(IReadOnlyList<ConnectionEntry> Connections, string Note);

/// <summary>Read-only database access, off unless the settings switch it on.</summary>
[McpServerToolType]
public sealed class DataTools
{
    private const int MaxCellChars = 500;

    private readonly McpContext _ctx;

    /// <summary>Creates the tool set over the shared context.</summary>
    public DataTools(McpContext ctx) => _ctx = ctx;

    /// <summary>Lists saved connection names.</summary>
    [McpServerTool(Name = "list_database_connections", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists the database connections saved in the Rotula app (names and engine only; hosts, users and passwords are never shown). Requires databases to be enabled in rotula-mcp.settings.json.")]
    public async Task<ConnectionListResult> ListDatabaseConnections(CancellationToken cancellationToken = default)
    {
        RequireEnabled(_ctx);
        var content = await _ctx.Connections().LoadAsync(cancellationToken).ConfigureAwait(false);
        string note = content.ErrorKey is null
            ? "Connections are created in the Rotula app; this server can only use the ones listed here."
            : "The saved connections file could not be read. Open the Rotula app to check it.";
        return new ConnectionListResult(content.Connections.Select(c => new ConnectionEntry(c.Settings.Name, c.Settings.Provider.ToString())).ToList(), note);
    }

    /// <summary>Runs a read-only query.</summary>
    [McpServerTool(Name = "run_query", ReadOnly = true, UseStructuredContent = true)]
    [Description("Runs one read-only SELECT (or WITH ... SELECT) on a saved database connection and returns the rows, capped (1000 rows by default; maxDatabaseRows in the settings). Anything that could change data, run code or read files is refused before it reaches the database. Requires databases to be enabled in rotula-mcp.settings.json. Use the rows to fill labels through merge_preview.")]
    public async Task<QueryResult> RunQueryTool(
        [Description("A saved connection name from list_database_connections.")] string connection,
        [Description("The SELECT statement.")] string query,
        [Description("Most rows to return; capped by the settings.")] int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled(_ctx);
        int cap = Math.Clamp(maxRows ?? _ctx.Settings.MaxDatabaseRows, 1, _ctx.Settings.MaxDatabaseRows);
        return await RunQueryAsync(_ctx, connection, query, cap, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous wrapper used by merge_preview.</summary>
    internal static QueryResult RunQuery(McpContext ctx, string connection, string query, int maxRows, CancellationToken ct)
    {
        RequireEnabled(ctx);
        return RunQueryAsync(ctx, connection, query, Math.Clamp(maxRows, 1, ctx.Settings.MaxDatabaseRows), ct).GetAwaiter().GetResult();
    }

    internal static void RequireEnabled(McpContext ctx)
    {
        if (!ctx.Settings.AllowDatabase)
            throw new McpException("Database access is switched off. The person who installed the server can allow it by setting allowDatabase to true in rotula-mcp.settings.json.");
    }

    private static async Task<QueryResult> RunQueryAsync(McpContext ctx, string connection, string query, int cap, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection)) throw new McpException("A connection name is required. Call list_database_connections.");
        if (string.IsNullOrWhiteSpace(query)) throw new McpException("A query is required.");
        try
        {
            var store = ctx.Connections();
            var saved = await store.LoadAsync(ct).ConfigureAwait(false);
            var entry = saved.Connections.FirstOrDefault(c => string.Equals(c.Settings.Name, connection.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new McpException($"There is no saved connection called \"{connection}\". Call list_database_connections for the names.");

            // The guard runs here as well as inside the database layer: the refusal then comes back before any connection is opened.
            var guard = SqlGuard.Check(query, entry.Settings.Provider);
            if (!guard.IsAllowed)
                throw new McpException("That query was refused because it is not a single read-only SELECT. " +
                                       Messages.Format(guard.ReasonKey ?? DataKeys.QueryRejected, "rejected", guard.Arg is null ? Array.Empty<string>() : [guard.Arg]));
            if (guard.ParameterNames.Count > 0)
                throw new McpException("Queries with @parameters are not supported here. Write the values into the query text instead.");

            IDatabaseSource source = await store.OpenAsync(entry.Settings.Name, ct).ConfigureAwait(false);
            var result = await source.QueryAsync(query, null, cap, ct).ConfigureAwait(false);
            var rows = result.Table.Rows
                .Take(cap)
                .Select(r => (IReadOnlyList<string>)r.Select(Clip).ToList())
                .ToList();
            return new QueryResult(entry.Settings.Name, result.Table.Columns, rows, result.Truncated, cap,
                result.Truncated ? $"Only the first {cap} rows are shown; narrow the query (WHERE, LIMIT) to see others." : "All rows are shown.");
        }
        catch (DatabaseException ex)
        {
            string reason = ex.ReasonKey is null ? "" : " " + Messages.Format(ex.ReasonKey, ex.ReasonKey, ex.Args);
            throw new McpException(Messages.Format(ex.Key, ex.Key, ex.Args) + reason);
        }
    }

    private static string Clip(string s) => s.Length > MaxCellChars ? s[..MaxCellChars] + "..." : s;
}
