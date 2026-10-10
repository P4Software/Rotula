using System.Text;

namespace Rotula.Data;

/// <summary>
/// Structured, password-free description of a database connection. The password is deliberately NOT a member:
/// it is passed separately to <see cref="DatabaseSource.Create"/> or kept by an <see cref="IConnectionStore"/>,
/// so settings can be logged, compared, serialised or shown without leaking it.
/// </summary>
public sealed record DatabaseConnectionSettings
{
    /// <summary>Default seconds a command may run.</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>Default seconds to wait while connecting.</summary>
    public const int DefaultConnectTimeoutSeconds = 10;

    /// <summary>Friendly name chosen by the user. It is the only thing a .rotula file may reference.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The engine.</summary>
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Sqlite;

    /// <summary>Server host name or address (SQL Server also accepts <c>host\instance</c> and <c>(localdb)\name</c>). Not used by SQLite or ODBC.</summary>
    public string? Host { get; init; }

    /// <summary>TCP port, or null for the engine default.</summary>
    public int? Port { get; init; }

    /// <summary>Database (catalog) name. Optional for ODBC.</summary>
    public string? Database { get; init; }

    /// <summary>Windows account or user/password.</summary>
    public DatabaseAuthentication Authentication { get; init; } = DatabaseAuthentication.WindowsIntegrated;

    /// <summary>User name when <see cref="Authentication"/> is <see cref="DatabaseAuthentication.UserPassword"/>.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Require an encrypted channel (SQL Server Encrypt, PostgreSQL/MySQL SSL). On by default; turning it off is an explicit,
    /// UI-warned choice for trusted local servers only.
    /// </summary>
    public bool Encrypt { get; init; } = true;

    /// <summary>
    /// Accept a server certificate that cannot be validated (self-signed). Off by default; the UI should warn before enabling
    /// it because it permits man-in-the-middle attacks.
    /// </summary>
    public bool TrustServerCertificate { get; init; }

    /// <summary>Path of the SQLite database file. SQLite files are always opened read-only.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// A raw ODBC connection string, accepted only because the user typed it explicitly. Any <c>PWD=</c>/<c>Password=</c>
    /// part is stripped before saving and kept in the protected password slot instead (see <see cref="IConnectionStore"/>).
    /// </summary>
    public string? OdbcConnectionString { get; init; }

    /// <summary>An ODBC data source name, used when no <see cref="OdbcConnectionString"/> is given.</summary>
    public string? Dsn { get; init; }

    /// <summary>Seconds a command may run before it is cancelled (1 to 3600).</summary>
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    /// <summary>Seconds to wait for the connection to open (1 to 300).</summary>
    public int ConnectTimeoutSeconds { get; init; } = DefaultConnectTimeoutSeconds;

    /// <summary>Safe single-line summary for diagnostics. Never contains secrets.</summary>
    public string Describe() => ToString();

    // A raw ODBC string may carry PWD=...; the compiler-generated printer would echo it, so print a redacted copy.
    private bool PrintMembers(StringBuilder sb)
    {
        sb.Append("Name = ").Append(Name).Append(", Provider = ").Append(Provider);
        if (Host is not null) sb.Append(", Host = ").Append(Host);
        if (Port is not null) sb.Append(", Port = ").Append(Port);
        if (Database is not null) sb.Append(", Database = ").Append(Database);
        sb.Append(", Authentication = ").Append(Authentication);
        if (UserName is not null) sb.Append(", UserName = ").Append(UserName);
        if (FilePath is not null) sb.Append(", FilePath = ").Append(FilePath);
        if (Dsn is not null) sb.Append(", Dsn = ").Append(Dsn);
        if (OdbcConnectionString is not null) sb.Append(", OdbcConnectionString = ").Append(SecretRedactor.RedactConnectionString(OdbcConnectionString));
        return true;
    }
}
