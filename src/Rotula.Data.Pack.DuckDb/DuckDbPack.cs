using System.Data.Common;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace Rotula.Data.Pack.DuckDb;

/// <summary>The entry class named in pack.json: contributes the <c>duckdb</c> provider.</summary>
public sealed class DuckDbPack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "duckdb";

    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new DuckDbDescriptor() };
}

/// <summary>
/// DuckDB database files. The engine runs inside Rotula (no server), so the control that matters is how the file is opened: read-only,
/// with every route to the outside world switched off and the configuration locked, all at open time so a query can never undo them.
/// </summary>
public sealed class DuckDbDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    /// <summary>Extra: soft memory limit in megabytes (DuckDB's default is most of the machine's RAM).</summary>
    public const string MemoryLimitMb = "memoryLimitMb";

    /// <summary>The provider id persisted in connections.json.</summary>
    public const string ProviderId = "duckdb";

    /// <summary>
    /// The open-time configuration, in order. These are applied by DuckDB while it opens the database (before any query exists), which
    /// is why they cannot be changed afterwards: <c>lock_configuration</c> freezes them for the life of the connection.
    /// </summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> SecureConfiguration = new[]
    {
        // The file itself: the engine refuses every write, checkpoint and schema change.
        new KeyValuePair<string, string>("access_mode", "READ_ONLY"),
        // Blocks ATTACH, COPY, read_csv/read_parquet/read_json, replacement scans of file names, INSTALL/LOAD from disk and the network.
        new KeyValuePair<string, string>("enable_external_access", "false"),
        // No extension may be downloaded or loaded on demand (that would be a network call), and no unsigned or community code.
        new KeyValuePair<string, string>("autoinstall_known_extensions", "false"),
        new KeyValuePair<string, string>("autoload_known_extensions", "false"),
        new KeyValuePair<string, string>("allow_community_extensions", "false"),
        new KeyValuePair<string, string>("allow_unsigned_extensions", "false"),
        // Last, so it freezes everything above: a query cannot SET the protections off again.
        new KeyValuePair<string, string>("lock_configuration", "true"),
    };

    /// <inheritdoc />
    public string Id => ProviderId;
    /// <inheritdoc />
    public string DisplayNameKey => "Conn.Provider.DuckDb";
    /// <inheritdoc />
    public string DisplayName => "DuckDB file";
    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.File;
    /// <inheritdoc />
    public int? DefaultPort => null;
    /// <inheritdoc />
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = Array.Empty<DatabaseAuthentication>();

    /// <inheritdoc />
    public IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        new ProviderOption(ProviderOptionKeys.FilePath, "Data.Option.DuckDb.FilePath", ProviderOptionKind.FilePath)
            { Label = "DuckDB database file", Required = true, HelpKey = "Data.Option.DuckDb.FilePath.Help" },
        new ProviderOption(MemoryLimitMb, "Data.Option.MemoryLimitMb", ProviderOptionKind.Number)
            { Label = "Memory limit (MB)", Advanced = true, Minimum = 64, Maximum = 1_048_576, HelpKey = "Data.Option.MemoryLimitMb.Help" },
    };

    /// <inheritdoc />
    public bool RequiresServer => false;
    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialects.DuckDb;

    /// <inheritdoc />
    public ReadOnlyPolicy ReadOnly { get; } = new()
    {
        // No SessionStatements on purpose: the protections are open-time configuration and lock_configuration refuses any later SET.
        Description = "File opened with access_mode=READ_ONLY, enable_external_access=false (no file, network or extension access from SQL), " +
                      "extension auto-install/auto-load off and lock_configuration=true, all applied while opening; every query also runs in a transaction that is rolled back.",
    };

    /// <inheritdoc />
    public IProviderErrorMapper Errors => this;

    /// <inheritdoc />
    public void Validate(DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProviderValidation.RequireLocalPath(settings.FilePath, nameof(settings.FilePath));
        string path = settings.FilePath!;
        // In-memory databases hold nothing to read, "md:" / "motherduck:" reach the MotherDuck cloud, and "?" or "=" would let a value
        // smuggle connection options. Only an ordinary file path is accepted.
        if (path.StartsWith(':') || path.StartsWith("md:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("motherduck:", StringComparison.OrdinalIgnoreCase)
            || path.Contains('?') || path.Contains(';') || path.Contains('='))
            throw ProviderValidation.Invalid(nameof(settings.FilePath));
        if (settings.Extras.Get(MemoryLimitMb) is { } m && !int.TryParse(m, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            throw ProviderValidation.Invalid("Extras." + MemoryLimitMb);
    }

    /// <inheritdoc />
    public void Preflight(DatabaseConnectionSettings settings)
    {
        // A missing file would otherwise be reported as an unspecific "could not open" by the engine.
        if (!File.Exists(settings.FilePath)) throw new DatabaseException(DataKeys.DatabaseNotFound);
    }

    /// <inheritdoc />
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = new DuckDBConnectionStringBuilder { DataSource = settings.FilePath! };
        foreach (var kv in SecureConfiguration.Where(kv => kv.Key != "lock_configuration")) b[kv.Key] = kv.Value;
        if (settings.Extras.Get(MemoryLimitMb) is { } mb) b["memory_limit"] = mb + "MB";
        // Written last so it takes effect after everything else it protects.
        b["lock_configuration"] = "true";
        return b.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => new DuckDBConnection(connectionString);

    /// <summary>DuckDB runs snapshot isolation only; its driver refuses the default ReadCommitted level, so ask for the real one.</summary>
    public Task<DbTransaction> BeginTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
        => connection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, cancellationToken).AsTask();

    /// <summary>
    /// <c>DECIMAL(38,s)</c> can exceed System.Decimal (28 digits); the driver offers its own decimal type, whose digits we print exactly
    /// (never rounded through a double), with the invariant decimal point.
    /// </summary>
    public string? FormatCell(DbDataReader reader, int ordinal)
    {
        if (reader.GetFieldType(ordinal) != typeof(decimal)) return null;
        var d = reader.GetFieldValue<DuckDBDecimal>(ordinal);
        // Up to 28 digits a System.Decimal is exact and the shared formatter keeps the scale; beyond that it would round silently.
        return d.Width <= 28 ? null : FormatDecimal(d.Value.ToBigInteger(), d.Scale);
    }

    /// <summary>DuckDB binds <c>$name</c> parameters by their bare name.</summary>
    public string ParameterName(string name) => name;

    /// <summary>Writes an unscaled integer with <paramref name="scale"/> decimal places as exact text (12345 with scale 2 is 123.45).</summary>
    internal static string FormatDecimal(System.Numerics.BigInteger unscaled, int scale)
    {
        string digits = System.Numerics.BigInteger.Abs(unscaled).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits[..^scale] + "." + digits[^scale..];
        }
        return (unscaled.Sign < 0 ? "-" : string.Empty) + digits;
    }

    // ---------------------------------------------------------------- errors

    /// <summary>Error classes this pack tells apart, carried through <see cref="Extract"/> as a number.</summary>
    internal static class ErrorClass
    {
        /// <summary>The file is missing.</summary>
        public const int NotFound = 1;
        /// <summary>The file is not a DuckDB database (or is from an incompatible version).</summary>
        public const int NotADatabase = 2;
        /// <summary>The file is open in another program (lock conflict).</summary>
        public const int Locked = 3;
        /// <summary>The engine refused an operation the read-only setup forbids.</summary>
        public const int Permission = 4;
        /// <summary>The query was interrupted.</summary>
        public const int Interrupted = 5;
    }

    /// <summary>
    /// DuckDB reports almost every failure with the same generic error type, so the class comes from the message prefix the engine
    /// writes ("IO Error:", "Permission Error:", ...). The numbers are private to this pack; <see cref="ClassifyNumber"/> maps them.
    /// </summary>
    public (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not DuckDBException) continue;
            string m = e.Message ?? string.Empty;
            if (m.Contains("Permission Error", StringComparison.OrdinalIgnoreCase)) return (ErrorClass.Permission, null);
            if (m.Contains("INTERRUPT Error", StringComparison.OrdinalIgnoreCase)) return (ErrorClass.Interrupted, null);
            if (m.Contains("Could not set lock", StringComparison.OrdinalIgnoreCase) || m.Contains("Conflicting lock", StringComparison.OrdinalIgnoreCase)) return (ErrorClass.Locked, null);
            if (m.Contains("not a valid DuckDB database file", StringComparison.OrdinalIgnoreCase)
                || m.Contains("incompatible with the current version", StringComparison.OrdinalIgnoreCase)) return (ErrorClass.NotADatabase, null);
            if (m.Contains("database does not exist", StringComparison.OrdinalIgnoreCase)
                || m.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return (ErrorClass.NotFound, null);
        }
        return (null, null);
    }

    /// <inheritdoc />
    public string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        ErrorClass.NotFound => DataKeys.DatabaseNotFound,
        ErrorClass.NotADatabase => DataKeys.ConnectionFailed,
        ErrorClass.Locked => DataKeys.Timeout,
        ErrorClass.Permission => DataKeys.PermissionDenied,
        ErrorClass.Interrupted => DataKeys.Cancelled,
        _ => null,
    };
}
