using System.Data.Common;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace Rotula.Data.Pack.Oracle;

/// <summary>The entry class named in pack.json: contributes the <c>oracle</c> provider.</summary>
public sealed class OraclePack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "oracle";

    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new OracleDescriptor() };
}

/// <summary>
/// Oracle Database 12c and later through Oracle's own managed driver (ODP.NET Core): no Oracle client installation is needed. Every
/// query runs in a <c>SET TRANSACTION READ ONLY</c> transaction behind the query guard; the account Rotula connects with should hold only
/// SELECT rights, because a read-only transaction does not stop autonomous transactions of called code.
/// </summary>
public sealed class OracleDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    /// <summary>The provider id persisted in connections.json.</summary>
    public const string ProviderId = "oracle";

    /// <summary>Extra: what the "Database" field holds. <c>service</c> (default), <c>sid</c> or <c>alias</c> (a name from tnsnames.ora).</summary>
    public const string ConnectBy = "connectBy";

    /// <summary>Extra: folder with tnsnames.ora, for <c>alias</c>.</summary>
    public const string TnsAdmin = "tnsAdmin";

    /// <summary>Extra: folder with the Oracle wallet that holds the trusted certificates for TLS.</summary>
    public const string WalletDirectory = "walletDirectory";

    /// <summary>Default listener port without TLS.</summary>
    public const int TcpPort = 1521;

    /// <summary>Default listener port with TLS (Oracle's documented TCPS default).</summary>
    public const int TcpsPort = 2484;

    // ORA-nnnnn numbers (Oracle Database Error Messages).
    private const int DriverConnectFailed = 50201;   // ODP.NET: "Failed to connect to server or failed to parse connect string"
    private const int InvalidUserOrPassword = 1017;
    private const int NullPassword = 1005;
    private const int AccountLocked = 28000;
    private const int PasswordExpired = 28001;
    private const int PasswordGrace = 28002;
    private const int ListenerUnknownService = 12514;
    private const int ListenerUnknownSid = 12505;
    private const int CannotResolveAlias = 12154;
    private const int NoListener = 12541;
    private const int ConnectTimeout = 12170;
    private const int TnsTimeout = 12535;
    private const int ProtocolAdapter = 12560;
    private const int TnsConnectFailed = 12537;
    private const int ConnectionClosed = 12543;
    private const int NoRouteToHost = 12545;
    private const int EndOfFile = 3113;
    private const int NotConnected = 3114;
    private const int UserCancelled = 1013;
    private const int InsufficientPrivileges = 1031;
    private const int NoCreateSession = 1045;
    private const int WriteInReadOnly = 1456;
    private const int ResourceBusy = 54;
    private const int ResourceTimeout = 51;
    private const int CertificateFailure = 29024;
    private const int SslFatal = 28860;
    private const int SslFailure = 29106;

    /// <inheritdoc />
    public string Id => ProviderId;
    /// <inheritdoc />
    public string DisplayNameKey => "Conn.Provider.Oracle";
    /// <inheritdoc />
    public string DisplayName => "Oracle Database";
    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Server;
    /// <inheritdoc />
    public int? DefaultPort => TcpPort;

    /// <inheritdoc />
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } =
        new[] { DatabaseAuthentication.UserPassword, DatabaseAuthentication.WindowsIntegrated };

    /// <inheritdoc />
    public IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(),
        StandardOptions.Port(),
        new ProviderOption(ProviderOptionKeys.Database, "Data.Option.Oracle.Database", ProviderOptionKind.Text)
            { Label = "Service name", Required = true, HelpKey = "Data.Option.Oracle.Database.Help" },
        StandardOptions.UserName(),
        new ProviderOption(ProviderOptionKeys.Encrypt, "Data.Option.Encrypt", ProviderOptionKind.Bool)
            { Label = "Encrypt the connection (TLS)", DefaultValue = "true", HelpKey = "Data.Option.Oracle.Encrypt.Help" },
        new ProviderOption(ProviderOptionKeys.TrustServerCertificate, "Data.Option.Oracle.TrustServerCertificate", ProviderOptionKind.Bool)
            { Label = "Do not check the server name in the certificate", DefaultValue = "false", Advanced = true, HelpKey = "Data.Option.Oracle.TrustServerCertificate.Help" },
        new ProviderOption(ConnectBy, "Data.Option.Oracle.ConnectBy", ProviderOptionKind.Choice)
        {
            Label = "The name above is a", Advanced = true, DefaultValue = "service",
            Choices = new[]
            {
                new ProviderChoice("service", "Data.Option.Oracle.ConnectBy.Service", "Service name"),
                new ProviderChoice("sid", "Data.Option.Oracle.ConnectBy.Sid", "SID (old style)"),
                new ProviderChoice("alias", "Data.Option.Oracle.ConnectBy.Alias", "Name from tnsnames.ora"),
            },
        },
        new ProviderOption(TnsAdmin, "Data.Option.Oracle.TnsAdmin", ProviderOptionKind.Text)
            { Label = "Folder with tnsnames.ora", Advanced = true, HelpKey = "Data.Option.Oracle.TnsAdmin.Help" },
        new ProviderOption(WalletDirectory, "Data.Option.Oracle.WalletDirectory", ProviderOptionKind.Text)
            { Label = "Wallet folder (TLS certificates)", Advanced = true, HelpKey = "Data.Option.Oracle.WalletDirectory.Help" },
    };

    /// <inheritdoc />
    public bool RequiresServer => true;
    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialects.Oracle;

    /// <inheritdoc />
    public ReadOnlyPolicy ReadOnly { get; } = new()
    {
        TransactionStatements = new[] { "SET TRANSACTION READ ONLY" },
        Description = "SET TRANSACTION READ ONLY as the first statement of every transaction (rolled back afterwards), behind the query guard that refuses UTL_*/DBMS_* packages, " +
                      "WITH FUNCTION, SYS routines and database links. A read-only transaction does not stop autonomous transactions in called code: use an account with SELECT rights only.",
    };

    /// <inheritdoc />
    public IProviderErrorMapper Errors => this;

    /// <inheritdoc />
    public void Validate(DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string mode = Mode(settings);
        if (mode != "alias") RequireNetworkName(settings.Host, nameof(settings.Host), 255, allowColon: true);
        RequireOracleName(settings.Database, nameof(settings.Database), mode == "alias" ? 128 : 255, allowAt: false);
        if (settings.Authentication == DatabaseAuthentication.UserPassword) ProviderValidation.RequireIdentifierLike(settings.UserName, nameof(settings.UserName), 128);
        if (settings.Extras.Get(ConnectBy) is { } m && m is not ("service" or "sid" or "alias")) throw ProviderValidation.Invalid("Extras." + ConnectBy);
        foreach (string key in new[] { TnsAdmin, WalletDirectory })
        {
            if (settings.Extras.Get(key) is not { } path) continue;
            ProviderValidation.RequireLocalPath(path, "Extras." + key);
            // These end up inside a connect descriptor: no parentheses, quotes or separators.
            if (path.IndexOfAny(new[] { '(', ')', '"', '\'', ';', '=' }) >= 0) throw ProviderValidation.Invalid("Extras." + key);
        }
        if (mode == "alias" && settings.Extras.Get(TnsAdmin) is null) { /* the standard TNS_ADMIN search applies */ }
    }

    private static string Mode(DatabaseConnectionSettings s) => s.Extras.Get(ConnectBy) is { Length: > 0 } m ? m : "service";

    /// <summary>Host names and addresses only: letters, digits, dot, dash, underscore (and colon for an IPv6 address). Nothing a connect descriptor could treat as syntax.</summary>
    private static void RequireNetworkName(string? value, string field, int max, bool allowColon)
    {
        ProviderValidation.RequireText(value, field, max);
        foreach (char c in value!)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' || (allowColon && c == ':'))) throw ProviderValidation.Invalid(field);
    }

    /// <summary>Service names, SIDs and TNS aliases: letters, digits and . - _ $ # only, so the value cannot close or extend a connect descriptor.</summary>
    private static void RequireOracleName(string? value, string field, int max, bool allowAt)
    {
        ProviderValidation.RequireText(value, field, max);
        foreach (char c in value!)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '$' or '#' || (allowAt && c == '@'))) throw ProviderValidation.Invalid(field);
    }

    /// <inheritdoc />
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = new OracleConnectionStringBuilder
        {
            DataSource = BuildDataSource(settings),
            Pooling = false,
            ConnectionTimeout = settings.ConnectTimeoutSeconds,
        };
        if (settings.Authentication == DatabaseAuthentication.UserPassword)
        {
            b.UserID = settings.UserName!;
            b.Password = password ?? string.Empty;
        }
        else
        {
            // "/" is Oracle's external (operating-system) authentication: the database trusts the account Windows signed in.
            b.UserID = "/";
        }
        return b.ConnectionString;
    }

    /// <summary>
    /// The connect descriptor (or alias). Every part was restricted to a safe character set by <see cref="Validate"/>, and this builds the
    /// text with those parts only, so no value can add a keyword to the descriptor.
    /// </summary>
    internal static string BuildDataSource(DatabaseConnectionSettings s)
    {
        string mode = Mode(s);
        if (mode == "alias") return s.Database!;
        bool tls = s.Encrypt;
        int port = s.Port ?? (tls ? TcpsPort : TcpPort);
        var sb = new StringBuilder("(DESCRIPTION=");
        sb.Append("(CONNECT_TIMEOUT=").Append(s.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture)).Append(')');
        sb.Append("(ADDRESS=(PROTOCOL=").Append(tls ? "TCPS" : "TCP").Append(")(HOST=").Append(s.Host).Append(")(PORT=").Append(port.ToString(CultureInfo.InvariantCulture)).Append("))");
        sb.Append("(CONNECT_DATA=(").Append(mode == "sid" ? "SID" : "SERVICE_NAME").Append('=').Append(s.Database).Append("))");
        if (tls)
        {
            // Check that the certificate was issued to the host we asked for, unless the user turned that off.
            sb.Append("(SECURITY=(SSL_SERVER_DN_MATCH=").Append(s.TrustServerCertificate ? "no" : "yes").Append(')');
            if (s.Extras.Get(WalletDirectory) is { Length: > 0 } wallet) sb.Append("(MY_WALLET_DIRECTORY=").Append(wallet).Append(')');
            sb.Append(')');
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static readonly object TnsLock = new();

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Mode(settings) == "alias" && settings.Extras.Get(TnsAdmin) is { Length: > 0 } dir)
        {
            // OracleConfiguration is process-wide in ODP.NET: setting it here applies to the connection opened right after.
            lock (TnsLock) OracleConfiguration.TnsAdmin = dir;
        }
        return new OracleConnection(connectionString);
    }

    /// <summary>Oracle takes <c>name</c> (with or without the colon); Rotula's <c>@name</c> is rewritten to <c>:name</c> in the text.</summary>
    public string ParameterName(string name) => name;

    /// <summary>Bind by name: the order of <c>:name</c> markers in the text then does not matter.</summary>
    public void ConfigureCommand(DbCommand command)
    {
        if (command is OracleCommand oracle) oracle.BindByName = true;
    }

    // ---------------------------------------------------------------- errors

    /// <inheritdoc />
    public (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not OracleException ora) continue;
            // The managed driver reports every network failure as ORA-50201 ("failed to connect or to parse the connect string") and keeps
            // the real reason (ORA-12541 no listener, ORA-12170 timeout ...) in the message of an inner exception. Use that one.
            if (ora.Number == DriverConnectFailed && InnerOraNumber(ora) is int inner) return (inner, null);
            return (ora.Number == 0 ? null : ora.Number, null);
        }
        return (null, null);
    }

    private static readonly System.Text.RegularExpressions.Regex OraCode = new(@"ORA-(\d{4,5})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The ORA number in the message of the innermost exception that has one other than the driver's own wrapper code.</summary>
    internal static int? InnerOraNumber(Exception ex)
    {
        int? found = null;
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
        {
            var m = OraCode.Match(e.Message ?? string.Empty);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n != DriverConnectFailed) found = n;
        }
        return found;
    }

    /// <inheritdoc />
    public string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        InvalidUserOrPassword or NullPassword or AccountLocked or PasswordExpired or PasswordGrace => DataKeys.AuthenticationFailed,
        ListenerUnknownService or ListenerUnknownSid or CannotResolveAlias => DataKeys.DatabaseNotFound,
        NoListener or ProtocolAdapter or TnsConnectFailed or ConnectionClosed or NoRouteToHost => connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed,
        EndOfFile or NotConnected => connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed,
        DriverConnectFailed => connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed,
        ConnectTimeout or TnsTimeout or ResourceBusy or ResourceTimeout => DataKeys.Timeout,
        UserCancelled => DataKeys.Cancelled,
        InsufficientPrivileges or NoCreateSession or WriteInReadOnly => DataKeys.PermissionDenied,
        CertificateFailure or SslFatal or SslFailure => DataKeys.CertificateNotTrusted,
        _ => null,
    };
}
