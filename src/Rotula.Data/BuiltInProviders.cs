using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Rotula.Data;

/// <summary>Ready-made option definitions with plain-language label keys, shared by the built-in providers and available to packs.</summary>
public static class StandardOptions
{
    /// <summary>"Server address".</summary>
    public static ProviderOption Host(bool required = true) => new(ProviderOptionKeys.Host, "Data.Option.Host", ProviderOptionKind.Host) { Label = "Server address", HelpKey = "Data.Option.Host.Help", Required = required };
    /// <summary>"Port" (leave empty for the default).</summary>
    public static ProviderOption Port() => new(ProviderOptionKeys.Port, "Data.Option.Port", ProviderOptionKind.Number) { Label = "Port", Minimum = 1, Maximum = 65535, Advanced = true };
    /// <summary>"Database name".</summary>
    public static ProviderOption Database(bool required = false) => new(ProviderOptionKeys.Database, "Data.Option.Database", ProviderOptionKind.Text) { Label = "Database name", Required = required };
    /// <summary>"User name".</summary>
    public static ProviderOption UserName() => new(ProviderOptionKeys.UserName, "Data.Option.UserName", ProviderOptionKind.Text) { Label = "User name" };
    /// <summary>"Database file".</summary>
    public static ProviderOption FilePath() => new(ProviderOptionKeys.FilePath, "Data.Option.FilePath", ProviderOptionKind.FilePath) { Label = "Database file", Required = true };
    /// <summary>"Encrypt the connection" (on by default).</summary>
    public static ProviderOption Encrypt() => new(ProviderOptionKeys.Encrypt, "Data.Option.Encrypt", ProviderOptionKind.Bool) { Label = "Encrypt the connection", DefaultValue = "true", HelpKey = "Data.Option.Encrypt.Help" };
    /// <summary>"Trust this server's certificate" (off by default; weakens security).</summary>
    public static ProviderOption TrustServerCertificate() => new(ProviderOptionKeys.TrustServerCertificate, "Data.Option.TrustServerCertificate", ProviderOptionKind.Bool) { Label = "Trust this server's certificate", DefaultValue = "false", Advanced = true, HelpKey = "Data.Option.TrustServerCertificate.Help" };
    /// <summary>"Certificate authority file".</summary>
    public static ProviderOption CaCertificate() => new(ProviderOptionKeys.CaCertificatePath, "Data.Option.CaCertificate", ProviderOptionKind.FilePath) { Label = "Certificate authority file (optional)", Advanced = true, HelpKey = ProviderKeys.HintCaCertificate };
    /// <summary>A yes/no extra option.</summary>
    public static ProviderOption Switch(string key, string labelKey, string label, bool advanced = true) => new(key, labelKey, ProviderOptionKind.Bool) { Label = label, Advanced = advanced };
}

/// <summary>Base class with the shared plumbing; every built-in descriptor derives from it.</summary>
internal abstract class BuiltInDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    public abstract string Id { get; }
    public abstract string DisplayNameKey { get; }
    public abstract string DisplayName { get; }
    public abstract ProviderCategory Category { get; }
    public virtual int? DefaultPort => null;
    public abstract IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; }
    public abstract IReadOnlyList<ProviderOption> Options { get; }
    public virtual bool RequiresServer => true;
    public abstract SqlDialect Dialect { get; }
    public abstract ReadOnlyPolicy ReadOnly { get; }
    public IProviderErrorMapper Errors => this;
    public abstract void Validate(DatabaseConnectionSettings settings);
    public abstract string BuildConnectionString(DatabaseConnectionSettings settings, string? password);
    public abstract DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings);

    // The native-error maps live in each descriptor; Extract is shared because exceptions of any built-in driver may be wrapped in another.
    public virtual (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SqlException sql: return (sql.Number, null);
                case PostgresException pg: return (null, pg.SqlState);
                case MySqlException my: return ((int)my.ErrorCode, my.SqlState);
                case SqliteException lite: return (lite.SqliteErrorCode, null);
                case OdbcException odbc when odbc.Errors.Count > 0: return (odbc.Errors[0].NativeError, odbc.Errors[0].SQLState);
            }
        }
        return (null, null);
    }

    public virtual string? ClassifyNumber(int? number, bool connecting) => null;

    /// <summary>Implements <see cref="IProviderErrorMapper.RejectedLoginShowsAsReset"/>; false unless the engine's server drops the connection after a refused login.</summary>
    public virtual bool RejectedLoginShowsAsReset => false;

    /// <summary>
    /// Implements <see cref="IProviderDescriptor.FormatCell"/> for the built-in engines. It is declared here (not only on the derived
    /// class) because the interface member is a default method: only a member of a class that lists the interface is picked up.
    /// </summary>
    public virtual string? FormatCell(DbDataReader reader, int ordinal) => null;

    public virtual void Preflight(DatabaseConnectionSettings settings) { }

}

internal sealed class SqlServerDescriptor : BuiltInDescriptor
{
    public override string Id => ProviderIds.SqlServer;
    public override string DisplayNameKey => "Conn.Provider.SqlServer";
    public override string DisplayName => "Microsoft SQL Server";
    public override ProviderCategory Category => ProviderCategory.Server;
    public override int? DefaultPort => 1433;
    public override IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.WindowsIntegrated, DatabaseAuthentication.UserPassword };
    public override IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(), StandardOptions.Port(), StandardOptions.Database(), StandardOptions.UserName(),
        StandardOptions.Encrypt(), StandardOptions.TrustServerCertificate(),
    };
    public override SqlDialect Dialect => SqlDialects.SqlServer;
    // No server-side read-only session exists: ApplicationIntent=ReadOnly is only a routing hint, so the guard and the account are the control.
    public override ReadOnlyPolicy ReadOnly { get; } = new() { GuardIsPrimaryControl = true, Description = "Query guard and a SELECT-only account; ApplicationIntent=ReadOnly is a routing hint only." };

    public override void Validate(DatabaseConnectionSettings s)
    {
        ProviderValidation.RequireHost(s.Host);
        if (!string.IsNullOrEmpty(s.Database)) ProviderValidation.RequireIdentifierLike(s.Database, nameof(s.Database), 256);
        if (s.Authentication == DatabaseAuthentication.UserPassword) ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
        else if (!string.IsNullOrEmpty(s.UserName)) ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
    }

    public override string BuildConnectionString(DatabaseConnectionSettings s, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.Port is int p && !s.Host!.Contains('\\') ? $"{s.Host},{p}" : s.Host!,
            ConnectTimeout = s.ConnectTimeoutSeconds,
            ApplicationName = ConnectionStrings.ApplicationName,
            Encrypt = s.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = s.TrustServerCertificate,
            // A routing hint only; it is NOT a write guard (see SqlGuard remarks).
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Pooling = false,
        };
        if (!string.IsNullOrEmpty(s.Database)) b.InitialCatalog = s.Database;
        if (s.Authentication == DatabaseAuthentication.UserPassword) { b.UserID = s.UserName!; b.Password = password ?? string.Empty; }
        else b.IntegratedSecurity = true;
        return b.ConnectionString;
    }

    public override DbConnection CreateConnection(string cs, DatabaseConnectionSettings s) => new SqlConnection(cs);

    public override string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        18456 or 18452 or 18451 or 18487 or 18488 => DataKeys.AuthenticationFailed,
        4060 or 911 => DataKeys.DatabaseNotFound,
        -2 => DataKeys.Timeout,
        53 or 2 or 40 or 1326 or 10060 or 10061 or 11001 or 10065 or 258 => DataKeys.HostUnreachable,
        229 or 230 or 262 or 297 or 300 or 916 => DataKeys.PermissionDenied,
        _ => null,
    };
}

internal sealed class PostgreSqlDescriptor : BuiltInDescriptor
{
    /// <summary>Extra: do not send the startup <c>options</c> parameter (poolers may refuse it); the in-transaction SET and the guard remain.</summary>
    public const string NoStartupOptions = "noStartupOptions";

    public override string Id => ProviderIds.PostgreSql;
    public override string DisplayNameKey => "Conn.Provider.PostgreSql";
    public override string DisplayName => "PostgreSQL";
    public override ProviderCategory Category => ProviderCategory.Server;
    public override int? DefaultPort => 5432;
    public override IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.UserPassword, DatabaseAuthentication.WindowsIntegrated };
    public override IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(), StandardOptions.Port(), StandardOptions.Database(), StandardOptions.UserName(),
        StandardOptions.Encrypt(), StandardOptions.TrustServerCertificate(), StandardOptions.CaCertificate(),
        StandardOptions.Switch(NoStartupOptions, "Data.Option.NoStartupOptions", "The server sits behind a connection pooler"),
    };
    public override SqlDialect Dialect => SqlDialects.PostgreSql;
    public override ReadOnlyPolicy ReadOnly { get; } = new()
    {
        TransactionStatements = new[] { "SET TRANSACTION READ ONLY" },
        Description = "Startup option default_transaction_read_only=on (unless a pooler refuses it) and SET TRANSACTION READ ONLY.",
    };

    public override void Validate(DatabaseConnectionSettings s)
    {
        ProviderValidation.RequireHost(s.Host);
        if (!string.IsNullOrEmpty(s.Database)) ProviderValidation.RequireIdentifierLike(s.Database, nameof(s.Database), 256);
        if (s.Authentication == DatabaseAuthentication.UserPassword) ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
        else if (!string.IsNullOrEmpty(s.UserName)) ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
    }

    public override string BuildConnectionString(DatabaseConnectionSettings s, string? password)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = s.Host,
            Timeout = s.ConnectTimeoutSeconds,
            CommandTimeout = s.CommandTimeoutSeconds,
            ApplicationName = ConnectionStrings.ApplicationName,
            // Read-only for the whole session; the query runner also opens a READ ONLY transaction (see below for poolers).
            Options = "-c default_transaction_read_only=on",
            Pooling = false,
            // Encrypt: verify the certificate chain AND host name unless the user explicitly trusts the server.
            SslMode = !s.Encrypt ? SslMode.Prefer : s.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull,
        };
        // Connection poolers (PgBouncer in transaction mode: Neon -pooler, Supabase port 6543, Redshift) may refuse unknown startup
        // parameters, so for them we rely on SET TRANSACTION READ ONLY plus the guard instead.
        if (s.Extras.GetBool(NoStartupOptions) || ProviderPresets.IsPoolerHost(s.Host)) b.Remove("Options");
        if (!string.IsNullOrWhiteSpace(s.CaCertificatePath)) b.RootCertificate = s.CaCertificatePath;
        if (s.Port is int p) b.Port = p;
        if (!string.IsNullOrEmpty(s.Database)) b.Database = s.Database;
        // Windows-integrated: Npgsql negotiates SSPI/Kerberos when no password is configured.
        if (s.Authentication == DatabaseAuthentication.UserPassword) { b.Username = s.UserName; b.Password = password ?? string.Empty; }
        else if (!string.IsNullOrEmpty(s.UserName)) b.Username = s.UserName;
        return b.ConnectionString;
    }

    public override DbConnection CreateConnection(string cs, DatabaseConnectionSettings s) => new NpgsqlConnection(cs);

    public override void Preflight(DatabaseConnectionSettings s) => ProviderPreflight.RequireCaFile(s);

    // PostgreSQL on Windows closes the connection right after refusing a login; Npgsql then sometimes reports the reset instead of the refusal.
    public override bool RejectedLoginShowsAsReset => true;

    // numeric NaN / beyond System.Decimal, intervals with months, BC dates, infinity, 24:00:00, timetz and bit strings.
    public override string? FormatCell(DbDataReader reader, int ordinal) => PostgresValueDecoder.TryFormat(reader, ordinal);
}

internal sealed class MySqlDescriptor : BuiltInDescriptor, IProviderDescriptor
{
    /// <summary>Extra: <c>mariadb</c> selects the MariaDB flavour of the dialect.</summary>
    public const string Flavor = "flavor";

    public override string Id => ProviderIds.MySql;
    public override string DisplayNameKey => "Conn.Provider.MySql";
    public override string DisplayName => "MySQL / MariaDB";
    public override ProviderCategory Category => ProviderCategory.Server;
    public override int? DefaultPort => 3306;
    public override IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.UserPassword };
    public override IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(), StandardOptions.Port(), StandardOptions.Database(), StandardOptions.UserName(),
        // The shared help text says what trusting means; the MariaDB variant adds that a password account verifies the certificate by fingerprint.
        StandardOptions.Encrypt(), StandardOptions.TrustServerCertificate() with { HelpKey = DataKeys.MariaDbCertificateFingerprintHelp }, StandardOptions.CaCertificate(),
        new ProviderOption(Flavor, "Data.Option.Flavor", ProviderOptionKind.Choice)
        {
            Label = "Server family", Advanced = true,
            Choices = new[] { new ProviderChoice("", "Data.Option.Flavor.MySql", "MySQL"), new ProviderChoice("mariadb", "Data.Option.Flavor.MariaDb", "MariaDB") },
        },
    };
    public override SqlDialect Dialect => SqlDialects.MySql;
    public override ReadOnlyPolicy ReadOnly { get; } = new()
    {
        SessionStatements = new[] { "SET SESSION TRANSACTION READ ONLY" },
        Description = "SET SESSION TRANSACTION READ ONLY and START TRANSACTION READ ONLY.",
    };

    SqlDialect IProviderDescriptor.ResolveDialect(DatabaseConnectionSettings s)
        => string.Equals(s.Extras.Get(Flavor), "mariadb", StringComparison.OrdinalIgnoreCase) ? SqlDialects.MariaDb : SqlDialects.MySql;

    public override void Validate(DatabaseConnectionSettings s)
    {
        ProviderValidation.RequireHost(s.Host);
        if (!string.IsNullOrEmpty(s.Database)) ProviderValidation.RequireIdentifierLike(s.Database, nameof(s.Database), 256);
        if (s.Authentication == DatabaseAuthentication.WindowsIntegrated) throw ProviderValidation.Invalid(nameof(s.Authentication));
        ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
    }

    public override string BuildConnectionString(DatabaseConnectionSettings s, string? password)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = s.Host!,
            UserID = s.UserName!,
            Password = password ?? string.Empty,
            ConnectionTimeout = (uint)s.ConnectTimeoutSeconds,
            DefaultCommandTimeout = (uint)s.CommandTimeoutSeconds,
            Pooling = false,
            AllowUserVariables = false,
            AllowLoadLocalInfile = false,
            // Zero dates (0000-00-00) are legal in MySQL/MariaDB. ConvertZeroDateTime would silently turn them into 0001-01-01; with
            // AllowZeroDateTime the driver hands over a MySqlDateTime and FormatCell (MySqlValueFormatter) prints them as they are stored.
            AllowZeroDateTime = true,
            ConvertZeroDateTime = false,
            SslMode = !s.Encrypt ? MySqlSslMode.None : s.TrustServerCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyFull,
        };
        if (!string.IsNullOrWhiteSpace(s.CaCertificatePath)) b.SslCa = s.CaCertificatePath;
        if (s.Port is int p) b.Port = (uint)p;
        if (!string.IsNullOrEmpty(s.Database)) b.Database = s.Database;
        return b.ConnectionString;
    }

    public override DbConnection CreateConnection(string cs, DatabaseConnectionSettings s) => new MySqlConnection(cs);

    // MySqlConnector can issue START TRANSACTION READ ONLY, the strongest server-side guard MySQL offers.
    async Task<DbTransaction> IProviderDescriptor.BeginTransactionAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection is MySqlConnection my)
            return await my.BeginTransactionAsync(IsolationLevel.ReadCommitted, isReadOnly: true, ct).ConfigureAwait(false);
        return await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
    }

    public override void Preflight(DatabaseConnectionSettings s) => ProviderPreflight.RequireCaFile(s);

    // The MariaDB server resets the connection of some refused logins (an unknown user); MySqlConnector keeps retrying until the connect timeout.
    public override bool RejectedLoginShowsAsReset => true;

    // DECIMAL wider than System.Decimal, zero dates and TIME beyond 24 hours.
    public override string? FormatCell(DbDataReader reader, int ordinal) => reader is MySqlDataReader my ? MySqlValueFormatter.TryFormat(my, ordinal) : null;

    /// <summary>
    /// MySqlConnector wraps errors: a connect timeout is 1042 "Connect Timeout expired" around a MySqlException -1 (CommandTimeoutExpired), a
    /// slow rejected login is 1042 around 1045 (Access denied), and a command timeout is -1 around 1317 (query interrupted). So the whole
    /// chain of MySqlExceptions is read, not just the outermost one: a login refusal wins, then a timeout, then the outermost number.
    /// </summary>
    public override (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException or PostgresException or SqliteException or OdbcException) return base.Extract(e);
            if (e is not MySqlException outer) continue;
            MySqlException? timeout = null, login = null;
            for (Exception? c = outer; c is MySqlException my; c = c.InnerException)
            {
                if (login is null && (int)my.ErrorCode is 1045 or 1698 or 1251) login = my;
                if (timeout is null && (int)my.ErrorCode == -1) timeout = my;
            }
            var chosen = login ?? timeout ?? outer;
            return ((int)chosen.ErrorCode, chosen.SqlState);
        }
        return (null, null);
    }

    public override string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        1045 or 1698 or 1251 => DataKeys.AuthenticationFailed,
        1044 or 1142 or 1143 or 1227 or 1370 => DataKeys.PermissionDenied,
        1049 => DataKeys.DatabaseNotFound,
        1042 or 2002 or 2003 or 2005 or 2013 => DataKeys.HostUnreachable,
        // -1 is MySqlErrorCode.CommandTimeoutExpired, which MySqlConnector also uses for an expired connect timeout.
        -1 or 1205 or 3024 => DataKeys.Timeout,
        _ => null,
    };
}

internal sealed class SqliteDescriptor : BuiltInDescriptor
{
    public override string Id => ProviderIds.Sqlite;
    public override string DisplayNameKey => "Conn.Provider.Sqlite";
    public override string DisplayName => "SQLite file";
    public override ProviderCategory Category => ProviderCategory.File;
    public override IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = Array.Empty<DatabaseAuthentication>();
    public override IReadOnlyList<ProviderOption> Options { get; } = new[] { StandardOptions.FilePath() };
    public override bool RequiresServer => false;
    public override SqlDialect Dialect => SqlDialects.Sqlite;
    public override ReadOnlyPolicy ReadOnly { get; } = new()
    {
        SessionStatements = new[] { "PRAGMA query_only = ON" },
        // SQLite needs no transaction: the file is opened Mode=ReadOnly with query_only on, and Microsoft.Data.Sqlite cannot start any
        // transaction but ReadUncommitted on a read-only file (the others issue BEGIN IMMEDIATE and fail).
        StartTransaction = false,
        Description = "File opened Mode=ReadOnly plus PRAGMA query_only.",
    };

    public override void Validate(DatabaseConnectionSettings s)
    {
        ProviderValidation.RequireText(s.FilePath, nameof(s.FilePath), 1024);
        // A "file:" URI or ":memory:" can carry options (vfs, mode=rwc, cache) that defeat the read-only mode.
        if (s.FilePath!.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || s.FilePath.StartsWith(':') || s.FilePath.Contains('?'))
            throw ProviderValidation.Invalid(nameof(s.FilePath));
    }

    public override string BuildConnectionString(DatabaseConnectionSettings s, string? password)
    {
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = s.FilePath!,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = s.CommandTimeoutSeconds,
        };
        return b.ConnectionString;
    }

    public override DbConnection CreateConnection(string cs, DatabaseConnectionSettings s) => new SqliteConnection(cs);

    public override void Preflight(DatabaseConnectionSettings s)
    {
        // Mode=ReadOnly would report a vague "unable to open"; a missing file deserves its own key.
        if (!File.Exists(s.FilePath)) throw new DatabaseException(DataKeys.DatabaseNotFound);
    }

    public override string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        14 => DataKeys.DatabaseNotFound,           // SQLITE_CANTOPEN
        8 or 23 or 3 => DataKeys.PermissionDenied, // READONLY, AUTH, PERM
        5 or 6 => DataKeys.Timeout,                // BUSY, LOCKED
        26 => DataKeys.ConnectionFailed,           // NOTADB: not a database file
        _ => null,
    };
}

internal sealed class OdbcDescriptor : BuiltInDescriptor, IProviderDescriptor
{
    /// <summary>Extra: id of the SQL dialect of the engine behind the driver (see <see cref="SqlDialects.All"/>); empty means unknown.</summary>
    public const string UnderlyingEngine = "underlyingEngine";

    public override string Id => ProviderIds.Odbc;
    public override string DisplayNameKey => "Conn.Provider.Odbc";
    public override string DisplayName => "ODBC data source";
    public override ProviderCategory Category => ProviderCategory.Odbc;
    public override IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.WindowsIntegrated, DatabaseAuthentication.UserPassword };
    public override IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        new ProviderOption(ProviderOptionKeys.Dsn, "Data.Option.Dsn", ProviderOptionKind.Text) { Label = "Data source name" },
        new ProviderOption(ProviderOptionKeys.OdbcConnectionString, "Data.Option.OdbcConnectionString", ProviderOptionKind.Text) { Label = "Connection string", Advanced = true },
        StandardOptions.UserName(),
        new ProviderOption(UnderlyingEngine, "Data.Option.UnderlyingEngine", ProviderOptionKind.Choice)
        {
            Label = "Database behind the driver", Advanced = true, HelpKey = "Data.Option.UnderlyingEngine.Help",
            Choices = new[] { new ProviderChoice("", "Data.Option.UnderlyingEngine.Unknown", "Not sure") }
                .Concat(SqlDialects.All.Where(d => d.Id != "odbc").Select(d => new ProviderChoice(d.Id, "Data.Dialect." + d.Id, d.DisplayName))).ToArray(),
        },
    };
    public override SqlDialect Dialect => SqlDialects.OdbcUnknown;
    // The driver decides; many have no transactions at all. The guard and a SELECT-only account are the control.
    public override ReadOnlyPolicy ReadOnly { get; } = new()
    {
        TransactionIsOptional = true, GuardIsPrimaryControl = true,
        Description = "Query guard under every dialect (or the named engine's); an ordinary transaction when the driver has one.",
    };

    SqlDialect IProviderDescriptor.ResolveDialect(DatabaseConnectionSettings s)
    {
        var engine = SqlDialects.ById(s.Extras.Get(UnderlyingEngine));
        if (engine is null || engine.Id == "odbc") return SqlDialects.OdbcUnknown;
        // The hint is only a claim: Rotula cannot see what is behind the driver, and the query may come from an untrusted file. So it can
        // only ADD rules. The text must still be clean under the four classic lexings AND under the engine's, with the strictest reading of
        // backslashes, and the engine's forbidden functions and keywords apply on top. A wrong hint therefore never loosens the guard.
        // ODBC always binds positionally and lists objects through the driver's schema collections, whatever the engine is.
        var rules = SqlDialects.OdbcUnknown.LexRules.Concat(engine.LexRules).Distinct().ToList();
        return engine with
        {
            LexRules = rules,
            AmbiguityChecks = engine.AmbiguityChecks | LexFeature.AnyBackslash,
            // Never loosen: "CHARACTER SET" is only a type clause when the engine really is MySQL, which a hint cannot prove.
            AllowsCharacterSetClause = false,
            ParameterStyle = ParameterStyle.Positional,
            CatalogueSql = null,
            ParameterNamesCaseSensitive = false,
        };
    }

    public override void Validate(DatabaseConnectionSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.OdbcConnectionString) && string.IsNullOrWhiteSpace(s.Dsn))
            throw ProviderValidation.Invalid(nameof(s.OdbcConnectionString));
        if (!string.IsNullOrWhiteSpace(s.OdbcConnectionString))
        {
            if (s.OdbcConnectionString.Length > 4096 || s.OdbcConnectionString.Contains('\0')) throw ProviderValidation.Invalid(nameof(s.OdbcConnectionString));
            ValidateRawOdbc(s.OdbcConnectionString);
        }
        else
        {
            ProviderValidation.RequireIdentifierLike(s.Dsn, nameof(s.Dsn), 256);
            if (s.Authentication == DatabaseAuthentication.UserPassword) ProviderValidation.RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
        }
    }

    public override string BuildConnectionString(DatabaseConnectionSettings s, string? password)
    {
        var b = new OdbcConnectionStringBuilder();
        if (!string.IsNullOrWhiteSpace(s.OdbcConnectionString))
        {
            b.ConnectionString = s.OdbcConnectionString;
        }
        else
        {
            b.Dsn = s.Dsn!;
            if (s.Authentication == DatabaseAuthentication.UserPassword) b["Uid"] = s.UserName!;
            else b["Trusted_Connection"] = "Yes";
        }
        // A password supplied out of band (from the protected store) is added only if the user's string has none.
        if (!string.IsNullOrEmpty(password) && !b.ContainsKey("pwd") && !b.ContainsKey("password")) b["Pwd"] = password;
        return b.ConnectionString;
    }

    private static void ValidateRawOdbc(string raw)
    {
        OdbcConnectionStringBuilder b;
        try { b = new OdbcConnectionStringBuilder(raw); }
        catch (ArgumentException) { throw ProviderValidation.Invalid(nameof(DatabaseConnectionSettings.OdbcConnectionString)); }
        // SAVEFILE makes the driver manager write a file DSN to disk; FILEDSN pulls settings from an arbitrary file.
        foreach (string k in new[] { "savefile", "filedsn" })
            if (b.ContainsKey(k)) throw ProviderValidation.Invalid(nameof(DatabaseConnectionSettings.OdbcConnectionString));
    }

    public override DbConnection CreateConnection(string cs, DatabaseConnectionSettings s) => new OdbcConnection(cs) { ConnectionTimeout = s.ConnectTimeoutSeconds };

    // Optional transaction: drivers that cannot do transactions fall back to the guard alone (handled by the caller via ReadOnly.TransactionIsOptional).

    public override string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        // Native codes of SQL Server ODBC drivers are the SQL Server numbers.
        18456 => DataKeys.AuthenticationFailed,
        4060 => DataKeys.DatabaseNotFound,
        229 or 230 => DataKeys.PermissionDenied,
        _ => null,
    };
}

/// <summary>Cheap local checks shared by descriptors.</summary>
internal static class ProviderPreflight
{
    /// <summary>The CA file named in the settings must exist; a missing file is the user's typo, not a network problem.</summary>
    public static void RequireCaFile(DatabaseConnectionSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.CaCertificatePath) && !File.Exists(s.CaCertificatePath))
            throw ProviderValidation.Invalid(nameof(s.CaCertificatePath));
    }
}

/// <summary>The five providers that ship inside Rotula.Data.</summary>
internal sealed class BuiltInPack : IDatabaseProviderPack
{
    public string PackId => "builtin";

    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[]
    {
        new SqlServerDescriptor(), new PostgreSqlDescriptor(), new MySqlDescriptor(), new SqliteDescriptor(), new OdbcDescriptor(),
    };

    public IReadOnlyList<ProviderPreset> GetPresets() => ProviderPresets.Builtin;
}
