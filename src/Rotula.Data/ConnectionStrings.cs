using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Rotula.Data;

/// <summary>
/// Validates structured settings and turns them into provider connection strings exclusively through each provider's
/// <c>ConnectionStringBuilder</c>, so a value can never add or override a keyword.
/// </summary>
internal static class ConnectionStrings
{
    /// <summary>Application name sent to servers so a DBA can recognise Rotula sessions.</summary>
    public const string ApplicationName = "Rotula";

    /// <summary>Checks every field; throws <see cref="DatabaseException"/> (<see cref="DataKeys.InvalidSettings"/>, arg = field) on the first problem.</summary>
    public static void Validate(DatabaseConnectionSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100 || HasControl(s.Name)) throw Invalid(nameof(s.Name));
        if (!Enum.IsDefined(s.Provider)) throw Invalid(nameof(s.Provider));
        if (!Enum.IsDefined(s.Authentication)) throw Invalid(nameof(s.Authentication));
        if (s.CommandTimeoutSeconds is < 1 or > 3600) throw Invalid(nameof(s.CommandTimeoutSeconds));
        if (s.ConnectTimeoutSeconds is < 1 or > 300) throw Invalid(nameof(s.ConnectTimeoutSeconds));
        if (s.Port is < 1 or > 65535) throw Invalid(nameof(s.Port));

        switch (s.Provider)
        {
            case DatabaseProvider.Sqlite:
                RequireText(s.FilePath, nameof(s.FilePath), 1024);
                // A "file:" URI or ":memory:" can carry options (vfs, mode=rwc, cache) that defeat the read-only mode.
                if (s.FilePath!.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || s.FilePath.StartsWith(':') || s.FilePath.Contains('?'))
                    throw Invalid(nameof(s.FilePath));
                break;

            case DatabaseProvider.Odbc:
                if (string.IsNullOrWhiteSpace(s.OdbcConnectionString) && string.IsNullOrWhiteSpace(s.Dsn))
                    throw Invalid(nameof(s.OdbcConnectionString));
                if (!string.IsNullOrWhiteSpace(s.OdbcConnectionString))
                {
                    if (s.OdbcConnectionString.Length > 4096 || s.OdbcConnectionString.Contains('\0')) throw Invalid(nameof(s.OdbcConnectionString));
                    ValidateRawOdbc(s.OdbcConnectionString);
                }
                else
                {
                    RequireIdentifierLike(s.Dsn, nameof(s.Dsn), 256);
                    if (s.Authentication == DatabaseAuthentication.UserPassword) RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
                }
                break;

            default:
                RequireHost(s.Host);
                if (!string.IsNullOrEmpty(s.Database)) RequireIdentifierLike(s.Database, nameof(s.Database), 256);
                if (s.Provider == DatabaseProvider.MySql && s.Authentication == DatabaseAuthentication.WindowsIntegrated)
                    throw Invalid(nameof(s.Authentication));
                if (s.Authentication == DatabaseAuthentication.UserPassword) RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
                else if (!string.IsNullOrEmpty(s.UserName)) RequireIdentifierLike(s.UserName, nameof(s.UserName), 256);
                break;
        }
    }

    /// <summary>Builds the (secret-bearing) connection string. Never log the result; use <see cref="SecretRedactor"/>.</summary>
    public static string Build(DatabaseConnectionSettings s, string? password)
    {
        Validate(s);
        bool sql = s.Authentication == DatabaseAuthentication.UserPassword;

        switch (s.Provider)
        {
            case DatabaseProvider.SqlServer:
            {
                var b = new SqlConnectionStringBuilder
                {
                    DataSource = s.Port is int p && !s.Host!.Contains('\\') ? $"{s.Host},{p}" : s.Host!,
                    ConnectTimeout = s.ConnectTimeoutSeconds,
                    ApplicationName = ApplicationName,
                    Encrypt = s.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                    TrustServerCertificate = s.TrustServerCertificate,
                    // A routing hint only; it is NOT a write guard (see SqlGuard remarks).
                    ApplicationIntent = ApplicationIntent.ReadOnly,
                    Pooling = false,
                };
                if (!string.IsNullOrEmpty(s.Database)) b.InitialCatalog = s.Database;
                if (sql) { b.UserID = s.UserName!; b.Password = password ?? string.Empty; }
                else b.IntegratedSecurity = true;
                return b.ConnectionString;
            }
            case DatabaseProvider.PostgreSql:
            {
                var b = new NpgsqlConnectionStringBuilder
                {
                    Host = s.Host,
                    Timeout = s.ConnectTimeoutSeconds,
                    CommandTimeout = s.CommandTimeoutSeconds,
                    ApplicationName = ApplicationName,
                    // Read-only for the whole session; the query runner also opens a READ ONLY transaction.
                    Options = "-c default_transaction_read_only=on",
                    Pooling = false,
                    // Encrypt: verify the certificate chain AND host name unless the user explicitly trusts the server.
                    SslMode = !s.Encrypt ? SslMode.Prefer : s.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull,
                };
                if (s.Port is int p) b.Port = p;
                if (!string.IsNullOrEmpty(s.Database)) b.Database = s.Database;
                // Windows-integrated: Npgsql negotiates SSPI/Kerberos when no password is configured.
                if (sql) { b.Username = s.UserName; b.Password = password ?? string.Empty; }
                else if (!string.IsNullOrEmpty(s.UserName)) b.Username = s.UserName;
                return b.ConnectionString;
            }
            case DatabaseProvider.MySql:
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
                    ConvertZeroDateTime = true,
                    SslMode = !s.Encrypt ? MySqlSslMode.None : s.TrustServerCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyFull,
                };
                if (s.Port is int p) b.Port = (uint)p;
                if (!string.IsNullOrEmpty(s.Database)) b.Database = s.Database;
                return b.ConnectionString;
            }
            case DatabaseProvider.Sqlite:
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
            default:
                return BuildOdbc(s, password);
        }
    }

    /// <summary>A connection string safe to show: every password value masked.</summary>
    public static string BuildRedacted(DatabaseConnectionSettings s, string? password = null)
        => SecretRedactor.RedactConnectionString(Build(s, password ?? "x"));

    private static string BuildOdbc(DatabaseConnectionSettings s, string? password)
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
        catch (ArgumentException) { throw Invalid(nameof(DatabaseConnectionSettings.OdbcConnectionString)); }
        // SAVEFILE makes the driver manager write a file DSN to disk; FILEDSN pulls settings from an arbitrary file.
        foreach (string k in new[] { "savefile", "filedsn" })
            if (b.ContainsKey(k)) throw Invalid(nameof(DatabaseConnectionSettings.OdbcConnectionString));
    }

    private static void RequireText(string? v, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(v) || v.Length > max || HasControl(v)) throw Invalid(field);
    }

    /// <summary>Names (database, user, DSN) may contain spaces and punctuation but never the connection-string separators (; = braces) or control characters. The builders would quote them anyway; refusing is the second wall.</summary>
    private static void RequireIdentifierLike(string? v, string field, int max)
    {
        RequireText(v, field, max);
        if (v!.Contains(';') || v.Contains('=') || v.Contains('{') || v.Contains('}')) throw Invalid(field);
    }

    /// <summary>A host is a name, an address or a SQL Server instance path: no separators, quotes, spaces or '='.</summary>
    private static void RequireHost(string? host)
    {
        RequireText(host, nameof(DatabaseConnectionSettings.Host), 255);
        foreach (char c in host!)
        {
            bool ok = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']' or '%' or '\\' or '(' or ')' or ',';
            if (!ok) throw Invalid(nameof(DatabaseConnectionSettings.Host));
        }
    }

    private static bool HasControl(string v) => v.Any(char.IsControl);

    private static DatabaseException Invalid(string field) => new(DataKeys.InvalidSettings, null, new[] { field });

    /// <summary>Parses a built connection string back and returns its keys; used by tests to prove no keyword was injected.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in b.Keys) d[k] = Convert.ToString(b[k], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return d;
    }
}
