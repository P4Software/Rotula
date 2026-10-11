using System.Data;
using System.Data.Common;

namespace Rotula.Data;

/// <summary>Broad family of a provider; the UI groups the "Where is your data?" list with it.</summary>
public enum ProviderCategory
{
    /// <summary>A relational server reached over the network (SQL Server, PostgreSQL, MySQL, Oracle...).</summary>
    Server,
    /// <summary>A cloud data warehouse or hosted service (Snowflake, BigQuery, Redshift...).</summary>
    Cloud,
    /// <summary>A local file database (SQLite, DuckDB, Access, dBase...).</summary>
    File,
    /// <summary>A document or key-value store (MongoDB, Cosmos, DynamoDB).</summary>
    Document,
    /// <summary>A web service (REST, OData).</summary>
    Web,
    /// <summary>Any vendor driver the user installed (ODBC).</summary>
    Odbc,
}

/// <summary>How the UI should render one option and how its value is validated.</summary>
public enum ProviderOptionKind
{
    /// <summary>Free text.</summary>
    Text,
    /// <summary>A whole number (see <see cref="ProviderOption.Minimum"/>/<see cref="ProviderOption.Maximum"/>).</summary>
    Number,
    /// <summary>A yes/no switch; stored as <c>true</c>/<c>false</c>.</summary>
    Bool,
    /// <summary>A path to an existing file, picked with a file dialog.</summary>
    FilePath,
    /// <summary>One of <see cref="ProviderOption.Choices"/>.</summary>
    Choice,
    /// <summary>A host name or address.</summary>
    Host,
}

/// <summary>One selectable value of a <see cref="ProviderOptionKind.Choice"/> option.</summary>
/// <param name="Value">Stored value (language neutral).</param>
/// <param name="LabelKey">Message key of the label shown to the user.</param>
/// <param name="Label">English fallback of the label (for logs and tools without a resource file).</param>
public sealed record ProviderChoice(string Value, string LabelKey, string Label);

/// <summary>
/// One field of a provider's connection form. The UI renders the form from this schema; labels are message keys so the
/// application supplies the (es/en) text. Standard keys (<see cref="ProviderOptionKeys"/>) map to dedicated properties of
/// <see cref="DatabaseConnectionSettings"/>; any other key lives in <see cref="DatabaseConnectionSettings.Extras"/>.
/// </summary>
/// <param name="Key">Option key.</param>
/// <param name="LabelKey">Message key of the field label, in plain language ("Server address", not "Host").</param>
/// <param name="Kind">Control type.</param>
public sealed record ProviderOption(string Key, string LabelKey, ProviderOptionKind Kind)
{
    /// <summary>English fallback of the label.</summary>
    public string Label { get; init; } = string.Empty;
    /// <summary>Message key of a one-sentence help text, or null.</summary>
    public string? HelpKey { get; init; }
    /// <summary>True when the form cannot be saved without a value.</summary>
    public bool Required { get; init; }
    /// <summary>True for options hidden behind an "Advanced" expander.</summary>
    public bool Advanced { get; init; }
    /// <summary>Default value as text, or null.</summary>
    public string? DefaultValue { get; init; }
    /// <summary>Smallest accepted number for <see cref="ProviderOptionKind.Number"/>.</summary>
    public int? Minimum { get; init; }
    /// <summary>Largest accepted number for <see cref="ProviderOptionKind.Number"/>.</summary>
    public int? Maximum { get; init; }
    /// <summary>Allowed values for <see cref="ProviderOptionKind.Choice"/>.</summary>
    public IReadOnlyList<ProviderChoice> Choices { get; init; } = Array.Empty<ProviderChoice>();
}

/// <summary>Keys of the options that map to dedicated properties of <see cref="DatabaseConnectionSettings"/>.</summary>
public static class ProviderOptionKeys
{
    /// <summary>Server address.</summary>
    public const string Host = "host";
    /// <summary>TCP port.</summary>
    public const string Port = "port";
    /// <summary>Database name.</summary>
    public const string Database = "database";
    /// <summary>User name.</summary>
    public const string UserName = "userName";
    /// <summary>Database file.</summary>
    public const string FilePath = "filePath";
    /// <summary>CA certificate file.</summary>
    public const string CaCertificatePath = "caCertificatePath";
    /// <summary>ODBC data source name.</summary>
    public const string Dsn = "dsn";
    /// <summary>Raw ODBC connection string.</summary>
    public const string OdbcConnectionString = "odbcConnectionString";
    /// <summary>Encrypt the connection (bool).</summary>
    public const string Encrypt = "encrypt";
    /// <summary>Trust an unverifiable certificate (bool).</summary>
    public const string TrustServerCertificate = "trustServerCertificate";

    internal static readonly IReadOnlySet<string> Standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Host, Port, Database, UserName, FilePath, CaCertificatePath, Dsn, OdbcConnectionString, Encrypt, TrustServerCertificate,
    };

    /// <summary>True when <paramref name="key"/> is one of the standard keys (a dedicated settings property).</summary>
    public static bool IsStandard(string key) => Standard.Contains(key);

    /// <summary>Reads the value of an option from settings (standard property or extra) as text; null when unset.</summary>
    public static string? GetValue(DatabaseConnectionSettings s, string key)
    {
        ArgumentNullException.ThrowIfNull(s);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (key.Equals(Host, StringComparison.OrdinalIgnoreCase)) return s.Host;
        if (key.Equals(Port, StringComparison.OrdinalIgnoreCase)) return s.Port?.ToString(inv);
        if (key.Equals(Database, StringComparison.OrdinalIgnoreCase)) return s.Database;
        if (key.Equals(UserName, StringComparison.OrdinalIgnoreCase)) return s.UserName;
        if (key.Equals(FilePath, StringComparison.OrdinalIgnoreCase)) return s.FilePath;
        if (key.Equals(CaCertificatePath, StringComparison.OrdinalIgnoreCase)) return s.CaCertificatePath;
        if (key.Equals(Dsn, StringComparison.OrdinalIgnoreCase)) return s.Dsn;
        if (key.Equals(OdbcConnectionString, StringComparison.OrdinalIgnoreCase)) return s.OdbcConnectionString;
        if (key.Equals(Encrypt, StringComparison.OrdinalIgnoreCase)) return s.Encrypt ? "true" : "false";
        if (key.Equals(TrustServerCertificate, StringComparison.OrdinalIgnoreCase)) return s.TrustServerCertificate ? "true" : "false";
        return s.Extras.Get(key);
    }

    /// <summary>Returns a copy of <paramref name="s"/> with one option set (empty or null clears it). Does not validate.</summary>
    public static DatabaseConnectionSettings WithValue(DatabaseConnectionSettings s, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? v = string.IsNullOrEmpty(value) ? null : value;
        bool Is(string k) => key.Equals(k, StringComparison.OrdinalIgnoreCase);
        if (Is(Host)) return s with { Host = v };
        if (Is(Port)) return s with { Port = int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) ? p : null };
        if (Is(Database)) return s with { Database = v };
        if (Is(UserName)) return s with { UserName = v };
        if (Is(FilePath)) return s with { FilePath = v };
        if (Is(CaCertificatePath)) return s with { CaCertificatePath = v };
        if (Is(Dsn)) return s with { Dsn = v };
        if (Is(OdbcConnectionString)) return s with { OdbcConnectionString = v };
        if (Is(Encrypt)) return s with { Encrypt = v is null || v.Equals("true", StringComparison.OrdinalIgnoreCase) };
        if (Is(TrustServerCertificate)) return s with { TrustServerCertificate = v is not null && v.Equals("true", StringComparison.OrdinalIgnoreCase) };
        return s with { Extras = s.Extras.With(key, v) };
    }
}

/// <summary>
/// The steps that make a session read-only, in the order the engine applies them, plus how much to trust them. The query guard
/// is always on; these are the server-side belts next to it.
/// </summary>
public sealed record ReadOnlyPolicy
{
    /// <summary>Statements run on the open connection before the transaction starts (MySQL <c>SET SESSION TRANSACTION READ ONLY</c>, SQLite <c>PRAGMA query_only</c>, DuckDB <c>SET enable_external_access = false</c>).</summary>
    public IReadOnlyList<string> SessionStatements { get; init; } = Array.Empty<string>();

    /// <summary>Statements run inside the transaction right after it starts (PostgreSQL <c>SET TRANSACTION READ ONLY</c>, Oracle <c>SET TRANSACTION READ ONLY</c>).</summary>
    public IReadOnlyList<string> TransactionStatements { get; init; } = Array.Empty<string>();

    /// <summary>False for engines that need no transaction (SQLite is opened read-only and rejects write transactions on a read-only file).</summary>
    public bool StartTransaction { get; init; } = true;

    /// <summary>True when failing to start a transaction is tolerated and the query runs without one (ODBC drivers that cannot do transactions).</summary>
    public bool TransactionIsOptional { get; init; }

    /// <summary>
    /// True when the query guard plus the database account's own rights are the real read-only control, because the engine offers no
    /// read-only session (SQL Server, ODBC, Snowflake...). The UI should then recommend a SELECT-only account.
    /// </summary>
    public bool GuardIsPrimaryControl { get; init; }

    /// <summary>Short English description of what enforces read-only on the server side, for documentation and diagnostics.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>Classifies provider-specific failures into <see cref="DataKeys"/> error keys without needing a server.</summary>
public interface IProviderErrorMapper
{
    /// <summary>Pulls a native error number and SQLSTATE out of a driver exception (walk <see cref="Exception.InnerException"/> yourself); both null when it is not yours.</summary>
    (int? Number, string? SqlState) Extract(Exception ex);

    /// <summary>
    /// Maps a native number to a <see cref="DataKeys"/> error key, or returns null to fall back to the generic connection/query failure.
    /// SQLSTATE classes understood by every driver are handled before this is called.
    /// </summary>
    string? ClassifyNumber(int? number, bool connecting);

    /// <summary>
    /// True when this engine's server closes the connection right after refusing a login, so the driver sometimes reports the reset
    /// instead of the refusal (PostgreSQL and MariaDB on Windows). Rotula then retries the connect once and, if it fails the same way,
    /// reports a probable authentication failure (<see cref="DataKeys.AuthenticationProbable"/>) instead of "server unreachable".
    /// Added in provider API 1.1; defaults to false.
    /// </summary>
    bool RejectedLoginShowsAsReset => false;
}

/// <summary>
/// Everything Rotula needs to know about one database engine: how to describe it to the user (schema-driven form), how to
/// connect, how to make the session read-only, how to lex and guard its SQL, and how to explain its errors. Built-in engines and
/// pack engines implement exactly this; the engine code never branches on a provider name.
/// </summary>
public interface IProviderDescriptor
{
    /// <summary>Stable lower-case id persisted in connections.json (letters, digits, dash).</summary>
    string Id { get; }

    /// <summary>Message key of the display name.</summary>
    string DisplayNameKey { get; }

    /// <summary>English display name (fallback for tools without resources).</summary>
    string DisplayName { get; }

    /// <summary>Family, for grouping in the UI.</summary>
    ProviderCategory Category { get; }

    /// <summary>Default TCP port, or null when the engine has no port (files, HTTPS-only services).</summary>
    int? DefaultPort { get; }

    /// <summary>Sign-in methods the engine supports (empty for engines without accounts, such as files).</summary>
    IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; }

    /// <summary>The form schema.</summary>
    IReadOnlyList<ProviderOption> Options { get; }

    /// <summary>True when a database server must be running to test it (false for in-process files); informational for the test plan.</summary>
    bool RequiresServer { get; }

    /// <summary>The SQL dialect: lexing, quoting, parameters, row limit, catalogue SQL, forbidden functions.</summary>
    SqlDialect Dialect { get; }

    /// <summary>The read-only enforcement steps.</summary>
    ReadOnlyPolicy ReadOnly { get; }

    /// <summary>Error classification.</summary>
    IProviderErrorMapper Errors { get; }

    /// <summary>
    /// Provider-specific part of settings validation. Throw <see cref="DatabaseException"/> with <see cref="DataKeys.InvalidSettings"/>
    /// and the field name. The common checks (name, timeouts, port, extras against <see cref="Options"/>) run before this.
    /// </summary>
    void Validate(DatabaseConnectionSettings settings);

    /// <summary>
    /// Builds the (secret-bearing) connection string exclusively through the driver's builder so a value can never add or override a
    /// keyword. Never log the result.
    /// </summary>
    string BuildConnectionString(DatabaseConnectionSettings settings, string? password);

    /// <summary>Creates an unopened connection for <paramref name="connectionString"/>.</summary>
    DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings);

    /// <summary>The dialect for these particular settings (a preset flavour or ODBC engine hint can refine it). Defaults to <see cref="Dialect"/>.</summary>
    SqlDialect ResolveDialect(DatabaseConnectionSettings settings) => Dialect;

    /// <summary>Cheap local checks before connecting (does the file exist?). Throw <see cref="DatabaseException"/> with a plain key.</summary>
    void Preflight(DatabaseConnectionSettings settings) { }

    /// <summary>Starts the transaction every query runs in (always rolled back). Override to request a read-only transaction natively.</summary>
    Task<DbTransaction> BeginTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
        => connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).AsTask();

    /// <summary>The name a bound parameter gets on the command (Rotula writes <c>@name</c>; Oracle wants <c>name</c> or <c>:name</c>).</summary>
    string ParameterName(string name) => "@" + name;

    /// <summary>
    /// Called (API 1.1) for every non-null cell before Rotula reads it with <c>reader.GetValue</c>. Return the value as culture-neutral
    /// text when the driver's .NET type cannot hold it exactly (DuckDB <c>DECIMAL(38)</c> becomes a System.Decimal of 28 digits and
    /// silently loses the rest), or null to use the shared reading. Keep it cheap: it runs once per cell.
    /// </summary>
    string? FormatCell(DbDataReader reader, int ordinal) => null;

    /// <summary>Last chance to tune a command (Oracle <c>BindByName</c>, ClickHouse settings).</summary>
    void ConfigureCommand(DbCommand command) { }

    /// <summary>
    /// For engines that are not reachable through ADO.NET (MongoDB, REST) or want full control: return a complete source. Return null to
    /// use the shared ADO.NET implementation, which applies the guard and the read-only transaction for you.
    /// </summary>
    IDatabaseSource? CreateCustomSource(DatabaseConnectionSettings settings, string? password) => null;
}

/// <summary>
/// The entry point of a provider pack assembly (and of the built-in providers). A pack has a public parameterless constructor; the
/// loader creates it once and asks for its providers and presets.
/// </summary>
public interface IDatabaseProviderPack
{
    /// <summary>Pack id; must equal the <c>id</c> in pack.json. The built-in pack uses <c>builtin</c>.</summary>
    string PackId { get; }

    /// <summary>The providers this pack contributes. Ids must be new (a pack can never replace an existing provider).</summary>
    IReadOnlyList<IProviderDescriptor> GetProviders();

    /// <summary>Optional presets (data over the providers above or over existing ones).</summary>
    IReadOnlyList<ProviderPreset> GetPresets() => Array.Empty<ProviderPreset>();
}

/// <summary>Reusable value checks for descriptor authors (the same rules the built-in providers use).</summary>
public static class ProviderValidation
{
    /// <summary>The standard failure: invalid setting, args[0] is the field name.</summary>
    public static DatabaseException Invalid(string field) => new(DataKeys.InvalidSettings, null, new[] { field });

    /// <summary>Non-empty text without control characters and within <paramref name="max"/> characters.</summary>
    public static void RequireText(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw Invalid(field);
    }

    /// <summary>Names (database, user, DSN) may contain spaces and punctuation but never the connection-string separators (; = braces) or control characters. The builders would quote them anyway; refusing is the second wall.</summary>
    public static void RequireIdentifierLike(string? value, string field, int max)
    {
        RequireText(value, field, max);
        if (value!.Contains(';') || value.Contains('=') || value.Contains('{') || value.Contains('}')) throw Invalid(field);
    }

    /// <summary>A host is a name, an address or a SQL Server instance path: no separators, quotes, spaces or '='.</summary>
    public static void RequireHost(string? host)
    {
        RequireText(host, nameof(DatabaseConnectionSettings.Host), 255);
        foreach (char c in host!)
        {
            bool ok = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']' or '%' or '\\' or '(' or ')' or ',';
            if (!ok) throw Invalid(nameof(DatabaseConnectionSettings.Host));
        }
    }

    /// <summary>
    /// A local file path only: UNC (<c>\\host\share</c>), device paths and URLs are refused, because merely touching them makes Windows
    /// send the user's credentials to another machine.
    /// </summary>
    public static void RequireLocalPath(string? path, string field, int max = 1024)
    {
        RequireText(path, field, max);
        if (path!.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw Invalid(field);
    }
}
