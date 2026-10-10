using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Rotula.Core.Merge;
using Rotula.Core.Validation;

namespace Rotula.Data;

/// <summary>
/// The one implementation of <see cref="IDatabaseSource"/>, built on ADO.NET base classes with a few per-provider switches
/// (connection type, read-only session setup, catalog queries, identifier quoting).
/// </summary>
internal sealed class AdoDatabaseSource : IDatabaseSource
{
    private readonly string? _password;
    private readonly string _connectionString;

    public AdoDatabaseSource(DatabaseConnectionSettings settings, string? password)
    {
        Settings = settings;
        _password = password;
        _connectionString = ConnectionStrings.Build(settings, password);
    }

    public DatabaseConnectionSettings Settings { get; }

    private DatabaseProvider Provider => Settings.Provider;

    // ---------------------------------------------------------------- public API

    public Task TestConnectionAsync(CancellationToken cancellationToken = default)
        => Offload(() => RunAsync<object?>(async (conn, tx, ct) =>
        {
            // SQLite opens lazily: touching the catalog proves the file really is a database.
            if (Provider == DatabaseProvider.Sqlite) await ExecuteInternalAsync(conn, tx, "SELECT COUNT(*) FROM sqlite_master", ct).ConfigureAwait(false);
            return null;
        }, cancellationToken));

    public Task<IReadOnlyList<DatabaseObject>> ListTablesAsync(CancellationToken cancellationToken = default)
        => Offload(() => RunAsync<IReadOnlyList<DatabaseObject>>(async (conn, tx, ct) =>
        {
            var list = new List<DatabaseObject>();
            if (Provider == DatabaseProvider.Odbc)
            {
                // Drivers disagree: some list views inside "Tables", others only under "Views", and SQL Server's lists sys.* tables.
                AddOdbcObjects(list, conn, "Tables", viewsCollection: false);
                try { AddOdbcObjects(list, conn, "Views", viewsCollection: true); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DbException or InvalidOperationException)
                {
                    // The "Views" collection is optional for ODBC drivers; tables alone are still useful.
                }
            }
            else
            {
                using var cmd = CreateCommand(conn, tx, ListObjectsSql(Provider), Array.Empty<string>(), null, ct);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string? sch = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                    string name = Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    string type = Convert.ToString(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    list.Add(new DatabaseObject(sch, name, type.Contains("view", StringComparison.OrdinalIgnoreCase) ? DatabaseObjectKind.View : DatabaseObjectKind.Table));
                }
            }
            list.Sort((a, b) =>
            {
                int c = string.Compare(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }, cancellationToken));

    private static void AddOdbcObjects(List<DatabaseObject> list, DbConnection conn, string collection, bool viewsCollection)
    {
        using DataTable schema = conn.GetSchema(collection);
        foreach (DataRow r in schema.Rows)
        {
            string type = schema.Columns.Contains("TABLE_TYPE")
                ? Convert.ToString(r["TABLE_TYPE"], System.Globalization.CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant() ?? ""
                : (viewsCollection ? "VIEW" : "TABLE");
            if (viewsCollection) type = "VIEW";
            if (type != "TABLE" && type != "VIEW") continue;
            string? sch = schema.Columns.Contains("TABLE_SCHEM") ? r["TABLE_SCHEM"] as string : null;
            if (string.Equals(sch, "sys", StringComparison.OrdinalIgnoreCase) || string.Equals(sch, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)) continue;
            string name = (string)r["TABLE_NAME"];
            sch = string.IsNullOrEmpty(sch) ? null : sch;
            if (list.Any(o => o.Schema == sch && o.Name == name)) continue;
            list.Add(new DatabaseObject(sch, name, type == "VIEW" ? DatabaseObjectKind.View : DatabaseObjectKind.Table));
        }
    }

    public Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(string table, CancellationToken cancellationToken = default)
    {
        var parts = SqlNames.Split(table);
        if (parts.Count is < 1 or > 2) throw new DatabaseException(DataKeys.InvalidSettings, null, new[] { "table" });
        var obj = parts.Count == 1
            ? new DatabaseObject(null, parts[0], DatabaseObjectKind.Table)
            : new DatabaseObject(parts[0], parts[1], DatabaseObjectKind.Table);
        return DescribeAsync(obj, cancellationToken);
    }

    public Task<IReadOnlyList<DatabaseColumn>> DescribeAsync(DatabaseObject table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        string sql = BuildSelectAll(table) + " WHERE 1 = 0";
        return Offload(() => RunAsync<IReadOnlyList<DatabaseColumn>>(async (conn, tx, ct) =>
        {
            using var cmd = CreateCommand(conn, tx, sql, Array.Empty<string>(), null, ct);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var schema = reader.GetSchemaTable();
            var cols = new List<DatabaseColumn>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                bool? allowNull = null;
                if (schema is not null && schema.Columns.Contains("AllowDBNull") && i < schema.Rows.Count && schema.Rows[i]["AllowDBNull"] is bool b) allowNull = b;
                string type;
                try { type = reader.GetDataTypeName(i); } catch (Exception) { type = reader.GetFieldType(i).Name; }
                cols.Add(new DatabaseColumn(i, reader.GetName(i), type, allowNull));
            }
            return cols;
        }, cancellationToken));
    }

    public string BuildSelectAll(DatabaseObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string name = SqlNames.Quote(Provider, table.Name);
        return "SELECT * FROM " + (string.IsNullOrEmpty(table.Schema) ? name : SqlNames.Quote(Provider, table.Schema) + "." + name);
    }

    public Task<DatabaseQueryResult> PreviewAsync(DatabaseObject table, int maxRows = 100, CancellationToken cancellationToken = default)
        => QueryAsync(BuildSelectAll(table), null, maxRows, cancellationToken);

    public Task<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int maxRows = DatabaseSource.DefaultMaxRows, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        if (maxRows > DatabaseSource.HardMaxRows)
            throw new DatabaseException(DataKeys.TooManyRows, null, new[] { maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture), DatabaseSource.HardMaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var plan = Plan(sql, parameters);
        return Offload(() => RunAsync(async (conn, tx, ct) =>
        {
            using var cmd = CreateCommand(conn, tx, plan.Sql, plan.Parameters, plan.Values, ct);
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            try
            {
                var table = new MergeTable(ColumnNames(reader));
                int width = reader.FieldCount;
                bool truncated = false;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (table.RowCount >= maxRows)
                    {
                        // One row beyond the cap proves there was more; stop the server sending the rest.
                        truncated = true;
                        TryCancel(cmd);
                        break;
                    }
                    table.AddRow(ReadRow(reader, width));
                }
                if (truncated)
                    table.AddIssue(new ValidationIssue(ValidationSeverity.Warning, DataKeys.QueryTruncated, null,
                        maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return new DatabaseQueryResult(table, truncated, maxRows);
            }
            finally
            {
                // Closing a cancelled reader may raise "operation cancelled" (SqlClient, Npgsql, MySqlConnector). The rows are
                // already in hand, so a failure to close must never turn a good result into an error.
                try { await reader.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* see above */ }
            }
        }, cancellationToken));
    }

    public Task<DatabaseRowStream> OpenStreamAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(sql, parameters);
        return Offload(() => OpenStreamCoreAsync(plan, cancellationToken));
    }

    // ---------------------------------------------------------------- query planning

    private sealed record QueryPlan(string Sql, IReadOnlyList<string> Parameters, IReadOnlyDictionary<string, object?> Values);

    /// <summary>Guard, parameter check and (for ODBC) positional rewrite, all before any connection is opened.</summary>
    private QueryPlan Plan(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var verdict = SqlGuard.Check(sql, Provider);
        if (!verdict.IsAllowed) throw verdict.ToException();

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null) foreach (var kv in parameters) values[kv.Key.TrimStart('@')] = kv.Value;
        foreach (string name in verdict.ParameterNames)
            if (!values.ContainsKey(name))
                throw new DatabaseException(DataKeys.QueryRejected, DataKeys.RejectMissingParameter, new[] { name });

        if (Provider != DatabaseProvider.Odbc) return new QueryPlan(sql, verdict.ParameterTokens.Select(t => t.Text).ToList(), values);

        var (rewritten, order) = OdbcParameters.Rewrite(sql, verdict.ParameterTokens);
        return new QueryPlan(rewritten, order, values);
    }

    // ---------------------------------------------------------------- execution plumbing

    private static Task<T> Offload<T>(Func<Task<T>> work) => Task.Run(work);

    private DbConnection CreateConnection() => Provider switch
    {
        DatabaseProvider.SqlServer => new SqlConnection(_connectionString),
        DatabaseProvider.PostgreSql => new NpgsqlConnection(_connectionString),
        DatabaseProvider.MySql => new MySqlConnection(_connectionString),
        DatabaseProvider.Sqlite => new SqliteConnection(_connectionString),
        _ => new OdbcConnection(_connectionString) { ConnectionTimeout = Settings.ConnectTimeoutSeconds },
    };

    private DatabaseException Map(Exception ex, bool connecting, CancellationToken caller, CancellationTokenSource? ours)
    {
        bool timedOut = ours is { IsCancellationRequested: true } && !caller.IsCancellationRequested;
        return DatabaseErrorMapper.Map(Provider, ex, connecting, caller, timedOut, _password, _connectionString);
    }

    /// <summary>Opens, prepares the read-only session, runs <paramref name="body"/> in a transaction that is always rolled back.</summary>
    private async Task<T> RunAsync<T>(Func<DbConnection, DbTransaction?, CancellationToken, Task<T>> body, CancellationToken caller)
    {
        if (caller.IsCancellationRequested) throw new DatabaseException(DataKeys.Cancelled);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        // Backstop for providers whose own timeout does not cover the whole operation (SQLite, slow row transfer).
        cts.CancelAfter(TimeSpan.FromSeconds(Settings.ConnectTimeoutSeconds + Settings.CommandTimeoutSeconds + 5));
        bool connecting = true;
        try
        {
            EnsureFileExists();
            await using var conn = CreateConnection();
            await conn.OpenAsync(cts.Token).ConfigureAwait(false);
            connecting = false;
            var tx = await BeginReadOnlyAsync(conn, cts.Token).ConfigureAwait(false);
            try
            {
                return await body(conn, tx, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                await RollbackQuietlyAsync(tx).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw Map(ex, connecting, caller, cts);
        }
    }

    private async Task<DatabaseRowStream> OpenStreamCoreAsync(QueryPlan plan, CancellationToken caller)
    {
        if (caller.IsCancellationRequested) throw new DatabaseException(DataKeys.Cancelled);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        connectCts.CancelAfter(TimeSpan.FromSeconds(Settings.ConnectTimeoutSeconds + Settings.CommandTimeoutSeconds + 5));
        DbConnection? conn = null;
        DbTransaction? tx = null;
        DbCommand? cmd = null;
        DbDataReader? reader = null;
        bool connecting = true;
        try
        {
            EnsureFileExists();
            conn = CreateConnection();
            await conn.OpenAsync(connectCts.Token).ConfigureAwait(false);
            connecting = false;
            tx = await BeginReadOnlyAsync(conn, connectCts.Token).ConfigureAwait(false);
            cmd = CreateCommand(conn, tx, plan.Sql, plan.Parameters, plan.Values, connectCts.Token);
            reader = await cmd.ExecuteReaderAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var mapped = Map(ex, connecting, caller, connectCts);
            await DisposeAllAsync(reader, cmd, tx, conn).ConfigureAwait(false);
            throw mapped;
        }

        var columns = MergeTable.NormalizeHeaders(ColumnNames(reader));
        int width = reader.FieldCount;
        var r = reader; var c = cmd; var t = tx; var cn = conn;
        return new DatabaseRowStream(columns,
            ct => Iterate(r, width, ct, caller),
            () => DisposeAllAsync(r, c, t, cn));
    }

    private async IAsyncEnumerable<string[]> Iterate(DbDataReader reader, int width, [EnumeratorCancellation] CancellationToken ct, CancellationToken openToken)
    {
        while (true)
        {
            string[]? row;
            try
            {
                // C# forbids 'yield' inside try/catch, so the read and the formatting happen here and the yield below.
                row = await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader, width) : null;
            }
            catch (Exception ex)
            {
                throw Map(ex, false, ct.IsCancellationRequested ? ct : openToken, null);
            }
            if (row is null) yield break;
            yield return row;
        }
    }

    private static async ValueTask DisposeAllAsync(DbDataReader? reader, DbCommand? cmd, DbTransaction? tx, DbConnection? conn)
    {
        if (cmd is not null) TryCancel(cmd);
        if (reader is not null) { try { await reader.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* closing a broken connection must not mask the real error */ } }
        if (cmd is not null) { try { await cmd.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* same */ } }
        await RollbackQuietlyAsync(tx).ConfigureAwait(false);
        if (conn is not null) { try { await conn.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* same */ } }
    }

    private static async Task RollbackQuietlyAsync(DbTransaction? tx)
    {
        if (tx is null) return;
        try { await tx.RollbackAsync().ConfigureAwait(false); }
        catch (Exception) { /* the connection may already be gone; the transaction never committed, which is all that matters */ }
        try { await tx.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* same */ }
    }

    private static void TryCancel(DbCommand cmd)
    {
        try { cmd.Cancel(); }
        catch (Exception) { /* best effort: stops the server from sending rows nobody will read */ }
    }

    private void EnsureFileExists()
    {
        // Mode=ReadOnly would report a vague "unable to open"; a missing file deserves its own key.
        if (Provider == DatabaseProvider.Sqlite && !File.Exists(Settings.FilePath))
            throw new DatabaseException(DataKeys.DatabaseNotFound);
    }

    /// <summary>
    /// Starts the transaction every query runs in (always rolled back, never committed) and asks the engine to treat it as
    /// read-only where it can. ODBC drivers that cannot do transactions fall back to the guard alone.
    /// </summary>
    private async Task<DbTransaction?> BeginReadOnlyAsync(DbConnection conn, CancellationToken ct)
    {
        switch (Provider)
        {
            case DatabaseProvider.Sqlite:
                // Belt and braces next to Mode=ReadOnly in the connection string.
                await ExecuteInternalAsync(conn, null, "PRAGMA query_only = ON", ct).ConfigureAwait(false);
                break;
            case DatabaseProvider.MySql:
                await ExecuteInternalAsync(conn, null, "SET SESSION TRANSACTION READ ONLY", ct).ConfigureAwait(false);
                break;
        }

        // SQLite needs no transaction: the file is opened Mode=ReadOnly with query_only on, and Microsoft.Data.Sqlite cannot
        // start any transaction but ReadUncommitted on a read-only file (the others issue BEGIN IMMEDIATE and fail).
        if (Provider == DatabaseProvider.Sqlite) return null;

        DbTransaction tx;
        try
        {
            // MySqlConnector can issue START TRANSACTION READ ONLY, the strongest server-side guard MySQL offers.
            tx = conn is MySqlConnection my
                ? await my.BeginTransactionAsync(IsolationLevel.ReadCommitted, isReadOnly: true, ct).ConfigureAwait(false)
                : await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        }
        catch (Exception) when (Provider == DatabaseProvider.Odbc) { return null; }

        if (Provider == DatabaseProvider.PostgreSql)
            await ExecuteInternalAsync(conn, tx, "SET TRANSACTION READ ONLY", ct).ConfigureAwait(false);
        return tx;
    }

    private static async Task ExecuteInternalAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private DbCommand CreateCommand(DbConnection conn, DbTransaction? tx, string sql, IReadOnlyList<string> parameterOrder,
        IReadOnlyDictionary<string, object?>? values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = Settings.CommandTimeoutSeconds;
        if (values is not null)
        {
            // ODBC lists one entry per '?' (duplicates included); everyone else binds each distinct name once.
            // SQLite matches parameter names case-sensitively, so @a and @A in one query are two parameters there.
            var bound = Provider == DatabaseProvider.Odbc ? parameterOrder
                : parameterOrder.Distinct(Provider == DatabaseProvider.Sqlite ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (string name in bound)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "@" + name;
                p.Value = values[name] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }
        return cmd;
    }

    private static List<string> ColumnNames(DbDataReader reader)
    {
        var names = new List<string>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
        return names;
    }

    private static string[] ReadRow(DbDataReader reader, int width)
    {
        var row = new string[width];
        for (int i = 0; i < width; i++) row[i] = reader.IsDBNull(i) ? string.Empty : DatabaseValueFormatter.Format(reader.GetValue(i));
        return row;
    }

    private static string ListObjectsSql(DatabaseProvider p) => p switch
    {
        DatabaseProvider.SqlServer =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') AND TABLE_SCHEMA NOT IN ('sys','INFORMATION_SCHEMA') ORDER BY 1, 2",
        DatabaseProvider.PostgreSql =>
            "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
            "WHERE table_type IN ('BASE TABLE','VIEW') AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2",
        DatabaseProvider.MySql =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES " +
            "WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') AND TABLE_SCHEMA NOT IN ('mysql','information_schema','performance_schema','sys') " +
            "AND (DATABASE() IS NULL OR TABLE_SCHEMA = DATABASE()) ORDER BY 1, 2",
        _ =>
            "SELECT NULL, name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name",
    };
}

/// <summary>Identifier quoting and splitting (the only place user-supplied object names reach SQL text).</summary>
internal static class SqlNames
{
    /// <summary>Quotes one identifier part for the dialect, doubling the closing quote so the name cannot break out.</summary>
    public static string Quote(DatabaseProvider provider, string part)
    {
        if (string.IsNullOrEmpty(part) || part.Any(char.IsControl)) throw new DatabaseException(DataKeys.InvalidSettings, null, new[] { "table" });
        switch (provider)
        {
            case DatabaseProvider.SqlServer: return "[" + part.Replace("]", "]]", StringComparison.Ordinal) + "]";
            case DatabaseProvider.MySql: return "`" + part.Replace("`", "``", StringComparison.Ordinal) + "`";
            case DatabaseProvider.Odbc:
                // The backing engine's quote character is unknown; plain names need none, anything else uses ANSI double quotes.
                return part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(part[0])
                    ? part : "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            default: return "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }

    /// <summary>Splits <c>a.b</c> on dots outside quotes; understands "x", [x] and `x` (doubled closers are literals).</summary>
    public static IReadOnlyList<string> Split(string name)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int i = 0;
        while (i < name.Length)
        {
            char c = name[i];
            char closer = c switch { '"' => '"', '`' => '`', '[' => ']', _ => '\0' };
            if (closer != '\0' && sb.Length == 0)
            {
                i++;
                while (true)
                {
                    if (i >= name.Length) throw new DatabaseException(DataKeys.InvalidSettings, null, new[] { "table" });
                    if (name[i] == closer)
                    {
                        if (i + 1 < name.Length && name[i + 1] == closer) { sb.Append(closer); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(name[i++]);
                }
                continue;
            }
            if (c == '.') { parts.Add(sb.ToString()); sb.Clear(); i++; continue; }
            sb.Append(c);
            i++;
        }
        parts.Add(sb.ToString());
        if (parts.Any(p => p.Length == 0)) throw new DatabaseException(DataKeys.InvalidSettings, null, new[] { "table" });
        return parts;
    }
}

/// <summary>ODBC only knows positional <c>?</c> markers; this rewrites <c>@name</c> occurrences found by the tokenizer.</summary>
internal static class OdbcParameters
{
    /// <summary>Replaces every parameter token with <c>?</c> and returns the names in binding order (duplicates included).</summary>
    public static (string Sql, List<string> Order) Rewrite(string sql, IReadOnlyList<SqlToken> parameterTokens)
    {
        var sb = new StringBuilder();
        var order = new List<string>();
        int pos = 0;
        foreach (var t in parameterTokens)
        {
            sb.Append(sql, pos, t.Start - pos).Append('?');
            order.Add(t.Text);
            pos = t.Start + t.Length;
        }
        sb.Append(sql, pos, sql.Length - pos);
        return (sb.ToString(), order);
    }
}
