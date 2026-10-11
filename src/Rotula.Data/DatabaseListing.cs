namespace Rotula.Data;

/// <summary>
/// The catalogue query a provider offers so the connection form can show the databases a login may open. It is plain data: the engine
/// runs it through the normal guarded, read-only source, so a pack never gets a back door around the query guard.
/// </summary>
/// <param name="Sql">One constant, parameterless SELECT whose first column is the database name. It must pass the read-only query guard.</param>
/// <param name="ConnectDatabase">
/// The database to connect to for the listing (SQL Server <c>master</c>, PostgreSQL <c>postgres</c>), or null to connect without naming
/// one (MySQL), so a wrong database typed by the user cannot make the listing fail.
/// </param>
/// <param name="SystemNames">Names flagged as system databases (compared without regard to case).</param>
public sealed record DatabaseListingQuery(string Sql, string? ConnectDatabase, IReadOnlySet<string> SystemNames);

/// <summary>
/// Optional capability of a provider descriptor: it can list the databases a login may access. A provider pack implements it next to
/// <see cref="IProviderDescriptor"/> and the connection form then offers a drop-down for the <c>database</c> option; a provider that
/// does not implement it keeps a plain text box. The built-in SQL Server, PostgreSQL and MySQL providers are handled by the engine itself.
/// </summary>
public interface IDatabaseListingProvider
{
    /// <summary>The catalogue query for these settings, or null when this particular connection cannot list databases.</summary>
    DatabaseListingQuery? GetDatabaseListingQuery(DatabaseConnectionSettings settings);
}

/// <summary>One database name returned by <see cref="DatabaseLister"/>.</summary>
/// <param name="Name">The name as the server reports it.</param>
/// <param name="IsSystem">True for the engine's own databases (master, mysql...), which the form shows after the user's databases.</param>
public sealed record DatabaseListEntry(string Name, bool IsSystem);

/// <summary>The outcome of a listing: the names, or the fact that this provider cannot list databases.</summary>
public sealed class DatabaseListResult
{
    internal DatabaseListResult(IReadOnlyList<DatabaseListEntry> databases, bool truncated, bool supported)
    {
        Databases = databases;
        Truncated = truncated;
        Supported = supported;
    }

    /// <summary>The "not applicable" result (SQLite, ODBC, a provider without a listing): not an error, the form falls back to a text box.</summary>
    public static DatabaseListResult NotSupported { get; } = new(Array.Empty<DatabaseListEntry>(), false, false);

    /// <summary>Databases, sorted by name without regard to case.</summary>
    public IReadOnlyList<DatabaseListEntry> Databases { get; }

    /// <summary>True when the server returned more than <see cref="DatabaseLister.MaxNames"/> names and the rest were left out.</summary>
    public bool Truncated { get; }

    /// <summary>False when the provider has no way to list databases.</summary>
    public bool Supported { get; }
}

/// <summary>
/// Lists the databases a login may access, for the "Database name" drop-down. It runs a fixed catalogue query through the same source
/// every other query uses, so the connection string builder, secure defaults, timeouts, error mapping, secret redaction and read-only
/// session apply unchanged. Nothing here is called on its own: the application calls it after an explicit user action.
/// </summary>
public static class DatabaseLister
{
    /// <summary>Most names ever returned; a server with more is reported as <see cref="DatabaseListResult.Truncated"/>.</summary>
    public const int MaxNames = 5000;

    private static readonly IReadOnlySet<string> SqlServerSystem = Names("master", "model", "msdb", "tempdb");
    private static readonly IReadOnlySet<string> MySqlSystem = Names("information_schema", "mysql", "performance_schema", "sys");
    private static readonly IReadOnlySet<string> NoSystem = Names();

    // SQL Server: HAS_DBACCESS hides databases the login cannot open; state = 0 keeps only ONLINE ones (offline and restoring ones
    // would fail the moment the user picked them).
    internal const int ListingCommandTimeoutSeconds = 180;

    internal const string SqlServerSql = "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 AND state = 0 ORDER BY name";

    // PostgreSQL: templates are not meant to be connected to, and a database that does not allow connections cannot be used.
    internal const string PostgreSqlSql = "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY 1";

    // MySQL/MariaDB: SHOW DATABASES is refused by the SELECT-only guard. information_schema.SCHEMATA answers the same question and is
    // filtered by the same privileges (a user sees only schemas they hold some privilege on).
    internal const string MySqlSql = "SELECT schema_name FROM information_schema.schemata ORDER BY 1";

    /// <summary>The catalogue query for a built-in or pack provider, or null when it cannot list databases.</summary>
    public static DatabaseListingQuery? GetQuery(IProviderDescriptor descriptor, DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);
        if (descriptor is IDatabaseListingProvider pack) return pack.GetDatabaseListingQuery(settings);
        return descriptor.Id switch
        {
            ProviderIds.SqlServer => new DatabaseListingQuery(SqlServerSql, "master", SqlServerSystem),
            ProviderIds.PostgreSql => new DatabaseListingQuery(PostgreSqlSql, "postgres", NoSystem),
            ProviderIds.MySql => new DatabaseListingQuery(MySqlSql, null, MySqlSystem),
            _ => null,
        };
    }

    /// <summary>True when the provider of <paramref name="settings"/> can list databases (the form shows a drop-down instead of a text box).</summary>
    public static bool IsSupported(DatabaseConnectionSettings settings, ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var descriptor = (registry ?? ProviderRegistry.Default).TryGet(settings.GetEffectiveProviderId());
        return descriptor is not null && GetQuery(descriptor, settings) is not null;
    }

    /// <summary>
    /// The settings to connect with for the listing, best choice first: the engine's maintenance database (<c>master</c>,
    /// <c>postgres</c>) and then, when the user typed one, that database (hosted servers sometimes refuse the maintenance database).
    /// Empty when the provider cannot list databases. Callers that open the source themselves (to apply the saved-password rules of
    /// the connection store) use these and pass each opened source to <see cref="ListDatabasesAsync(IDatabaseSource, CancellationToken, ProviderRegistry?)"/>.
    /// </summary>
    public static IReadOnlyList<DatabaseConnectionSettings> GetListingSettings(DatabaseConnectionSettings settings, ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var descriptor = (registry ?? ProviderRegistry.Default).TryGet(settings.GetEffectiveProviderId());
        var query = descriptor is null ? null : GetQuery(descriptor, settings);
        if (query is null) return Array.Empty<DatabaseConnectionSettings>();
        var list = new List<DatabaseConnectionSettings>();
        string? typed = string.IsNullOrWhiteSpace(settings.Database) ? null : settings.Database;
        // HAS_DBACCESS opens every database that is closed (auto-close), which takes seconds each on a server with many databases, so the
        // listing gets a generous command time; the Stop button and the caller's token still end it at any moment.
        var patient = settings with { CommandTimeoutSeconds = Math.Max(settings.CommandTimeoutSeconds, ListingCommandTimeoutSeconds) };
        if (query.ConnectDatabase is { } maintenance) list.Add(patient with { Database = maintenance });
        else list.Add(patient with { Database = null });
        if (typed is not null && !string.Equals(typed, query.ConnectDatabase, StringComparison.OrdinalIgnoreCase)) list.Add(patient with { Database = typed });
        return list;
    }

    /// <summary>
    /// Lists the databases <paramref name="settings"/> may access. <paramref name="password"/> is used in memory for this call only (null
    /// for Windows sign-in). Returns <see cref="DatabaseListResult.NotSupported"/> for SQLite, ODBC and providers without a listing.
    /// </summary>
    /// <exception cref="DatabaseException">A stable <c>Data.Error.*</c> key: wrong login, unreachable server, timeout, cancelled...</exception>
    public static Task<DatabaseListResult> ListDatabasesAsync(DatabaseConnectionSettings settings, string? password, CancellationToken cancellationToken = default,
        ProviderRegistry? registry = null)
        => ListDatabasesAsync(settings, (s, _) => Task.FromResult(DatabaseSource.CreateUsing(s, password, registry ?? ProviderRegistry.Default)), cancellationToken, registry);

    /// <summary>
    /// Lists the databases, opening each source through <paramref name="openSource"/>. This is how the connection manager keeps the saved
    /// password rules: it passes a function that calls <c>IConnectionStore.OpenWithSettingsAsync</c>, which reuses a saved password only
    /// for an edit the policy allows, so this class never sees or decides about a saved password.
    /// </summary>
    /// <exception cref="DatabaseException">See the other overload.</exception>
    public static async Task<DatabaseListResult> ListDatabasesAsync(DatabaseConnectionSettings settings,
        Func<DatabaseConnectionSettings, CancellationToken, Task<IDatabaseSource>> openSource, CancellationToken cancellationToken = default,
        ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openSource);
        var candidates = GetListingSettings(settings, registry);
        if (candidates.Count == 0) return DatabaseListResult.NotSupported;

        DatabaseException? last = null;
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested) throw new DatabaseException(DataKeys.Cancelled);
            try
            {
                var source = await openSource(candidate, cancellationToken).ConfigureAwait(false);
                return await ListDatabasesAsync(source, cancellationToken, registry).ConfigureAwait(false);
            }
            catch (DatabaseException ex) when (ex.Key is DataKeys.DatabaseNotFound or DataKeys.PermissionDenied && !ReferenceEquals(candidate, candidates[^1]))
            {
                // Only "that database is not there / not allowed" justifies trying the next one; a wrong password or a dead server would
                // fail the same way again, so those are reported at once.
                last = ex;
            }
        }
        throw last ?? new DatabaseException(DataKeys.ConnectionFailed);
    }

    /// <summary>
    /// Runs the catalogue query on an already opened <paramref name="source"/> (connected to the maintenance database) and returns the
    /// names. A source of a provider without a listing gives <see cref="DatabaseListResult.NotSupported"/>.
    /// </summary>
    /// <exception cref="DatabaseException">A stable <c>Data.Error.*</c> key.</exception>
    public static async Task<DatabaseListResult> ListDatabasesAsync(IDatabaseSource source, CancellationToken cancellationToken = default,
        ProviderRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var descriptor = (registry ?? ProviderRegistry.Default).TryGet(source.Settings.GetEffectiveProviderId());
        var query = descriptor is null ? null : GetQuery(descriptor, source.Settings);
        if (query is null) return DatabaseListResult.NotSupported;

        // One row past the cap proves the list was cut without reading an unbounded result.
        var result = await source.QueryAsync(query.Sql, null, MaxNames + 1, cancellationToken).ConfigureAwait(false);
        var names = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Table.Rows)
        {
            if (row.Length == 0) continue;
            string name = row[0].Trim();
            // A name with control characters cannot be a real database name; showing it could confuse the form.
            if (name.Length == 0 || name.Any(char.IsControl) || names.ContainsKey(name)) continue;
            names[name] = name;
        }
        bool truncated = result.Truncated || names.Count > MaxNames;
        var entries = names.Values.Take(MaxNames).Select(n => new DatabaseListEntry(n, query.SystemNames.Contains(n))).ToList();
        return new DatabaseListResult(entries, truncated, true);
    }

    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
}
