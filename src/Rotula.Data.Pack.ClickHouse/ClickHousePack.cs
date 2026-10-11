using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Json;

namespace Rotula.Data.Pack.ClickHouse;

/// <summary>The entry class named in pack.json: contributes the <c>clickhouse</c> provider.</summary>
public sealed class ClickHousePack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "clickhouse";

    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new ClickHouseDescriptor() };
}

/// <summary>
/// ClickHouse over its HTTP(S) interface. Read-only is enforced by the server itself: every request carries <c>readonly=1</c>, placed
/// last in the URL by <see cref="ReadOnlyHandler"/> whatever the driver does, so a query can neither write nor change that setting.
/// </summary>
public sealed class ClickHouseDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    /// <summary>The provider id persisted in connections.json.</summary>
    public const string ProviderId = "clickhouse";

    /// <summary>
    /// Extra: ask the server to stop a query after the command timeout (<c>max_execution_time</c>). OFF by default: an account whose profile is
    /// <c>readonly=1</c> may not change a setting to a different value (ClickHouse answers READONLY, code 164), so sending it would make
    /// every query of the recommended account fail. Turn it on for accounts that may change settings.
    /// </summary>
    public const string ServerTimeLimit = "serverTimeLimit";

    /// <summary>Port of the HTTPS interface (the default here, because the connection is encrypted by default).</summary>
    public const int HttpsPort = 8443;

    /// <summary>Port of the plain HTTP interface.</summary>
    public const int HttpPort = 8123;

    // ClickHouse error codes (ErrorCodes.cpp). The driver parses "Code: N." from the server's reply.
    private const int UnknownUser = 192;
    private const int WrongPassword = 193;
    private const int RequiredPassword = 194;
    private const int AuthenticationFailed = 516;
    private const int AccessDenied = 497;
    private const int Readonly = 164;
    private const int UnknownDatabase = 81;
    private const int TimeoutExceeded = 159;
    private const int TooSlow = 160;
    private const int SocketTimeout = 209;
    private const int NetworkError = 210;
    private const int AllConnectionTriesFailed = 279;
    private const int QueryWasCancelled = 394;

    /// <summary>HTTP status codes are reported as <c>HttpStatusBase + status</c> so they cannot collide with ClickHouse error codes.</summary>
    internal const int HttpStatusBase = 1_000_000;

    /// <summary>Private code for "the socket could not reach the server".</summary>
    internal const int SocketFailure = 2_000_000;

    private readonly Func<HttpMessageHandler>? _innerHandler;

    /// <summary>Creates the descriptor the pack registers.</summary>
    public ClickHouseDescriptor() { }

    /// <summary>For tests: the requests go to <paramref name="innerHandler"/> (still behind <see cref="ReadOnlyHandler"/>) instead of the network.</summary>
    internal ClickHouseDescriptor(Func<HttpMessageHandler> innerHandler) => _innerHandler = innerHandler;

    /// <inheritdoc />
    public string Id => ProviderId;
    /// <inheritdoc />
    public string DisplayNameKey => "Conn.Provider.ClickHouse";
    /// <inheritdoc />
    public string DisplayName => "ClickHouse";
    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.Server;
    /// <inheritdoc />
    public int? DefaultPort => HttpsPort;

    /// <inheritdoc />
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.UserPassword };

    /// <inheritdoc />
    public IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(),
        StandardOptions.Port(),
        StandardOptions.Database(),
        StandardOptions.UserName(),
        StandardOptions.Encrypt(),
        StandardOptions.TrustServerCertificate(),
        StandardOptions.CaCertificate(),
        new ProviderOption(ServerTimeLimit, "Data.Option.ClickHouse.ServerTimeLimit", ProviderOptionKind.Bool)
            { Label = "Let the server stop long queries (needs an account that may change settings)", Advanced = true, DefaultValue = "false", HelpKey = "Data.Option.ClickHouse.ServerTimeLimit.Help" },
    };

    /// <inheritdoc />
    public bool RequiresServer => true;
    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialects.ClickHouse;

    /// <inheritdoc />
    public ReadOnlyPolicy ReadOnly { get; } = new()
    {
        // ClickHouse has no transactions to start; the control is a setting sent with every request.
        StartTransaction = false,
        Description = "readonly=1 is sent with every HTTP request; the server then refuses writes, DDL, changing that setting and most table functions. " +
                      "The account's own profile should be readonly=1 or 0 (a profile with readonly=2 forbids changing the setting and every query is refused). " +
                      "Plus the query guard (table functions and SETTINGS are refused) and, optionally, max_execution_time.",
    };

    /// <inheritdoc />
    public IProviderErrorMapper Errors => this;

    /// <inheritdoc />
    public void Validate(DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProviderValidation.RequireHost(settings.Host);
        if (!string.IsNullOrEmpty(settings.Database)) ProviderValidation.RequireIdentifierLike(settings.Database, nameof(settings.Database), 256);
        if (settings.Authentication != DatabaseAuthentication.UserPassword) throw ProviderValidation.Invalid(nameof(settings.Authentication));
        ProviderValidation.RequireIdentifierLike(settings.UserName, nameof(settings.UserName), 256);
    }

    /// <inheritdoc />
    public void Preflight(DatabaseConnectionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CaCertificatePath) && !File.Exists(settings.CaCertificatePath))
            throw ProviderValidation.Invalid(nameof(settings.CaCertificatePath));
    }

    /// <inheritdoc />
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = new ClickHouseConnectionStringBuilder
        {
            Host = settings.Host!,
            Port = (ushort)(settings.Port ?? (settings.Encrypt ? HttpsPort : HttpPort)),
            // Encrypt is on by default: https. The plain http interface is an explicit choice for trusted local servers.
            Protocol = settings.Encrypt ? "https" : "http",
            Username = settings.UserName!,
            Password = password ?? string.Empty,
            // No sessions (a session would keep server state between requests) and no compression (it is a setting the request would have to change).
            UseSession = false,
            Compression = false,
            Timeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds + settings.CommandTimeoutSeconds),
            // The driver would add "output_format_binary_write_json_as_string" and its twin to every request; Rotula reads the default text form.
            JsonReadMode = JsonReadMode.None,
            JsonWriteMode = JsonWriteMode.None,
        };
        if (!string.IsNullOrEmpty(settings.Database)) b.Database = settings.Database;
        // set_<name> becomes a URL setting of every request. The read-only setting is repeated by ReadOnlyHandler, which also moves it last.
        b["set_readonly"] = "1";
        // Optional, because a profile with readonly=1 refuses a setting that changes (see ServerTimeLimit). Rotula's own timeout always applies.
        if (settings.Extras.GetBool(ServerTimeLimit))
            b["set_max_execution_time"] = settings.CommandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return b.ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var http = _innerHandler is null
            ? HttpClients.Get(settings.CaCertificatePath, settings.TrustServerCertificate)
            : new HttpClient(new ReadOnlyHandler(_innerHandler()), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return new ClickHouseConnection(new ClickHouseClientSettings(connectionString) { HttpClient = http });
    }

    /// <summary>
    /// The driver turns <c>@name</c> in the text into a typed server parameter <c>{name:Type}</c> and looks the value up under the bare
    /// name, so the parameter is called <c>name</c> (not <c>@name</c>).
    /// </summary>
    public string ParameterName(string name) => name;

    /// <summary>Last chance to tune a command: keep the read-only setting on the command as well.</summary>
    public void ConfigureCommand(DbCommand command)
    {
        if (command is ClickHouseCommand ch) ch.CustomSettings["readonly"] = 1;
    }

    // ---------------------------------------------------------------- errors

    /// <inheritdoc />
    public (int? Number, string? SqlState) Extract(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClickHouseServerException server) return (server.ErrorCode == -1 ? null : server.ErrorCode, null);
            if (e is HttpRequestException { StatusCode: { } status }) return (HttpStatusBase + (int)status, null);
            // HTTP has no separate "connecting" phase (the driver opens nothing until the first request), so a refused or unreachable
            // socket is reported as unreachable whenever it happens.
            if (e is System.Net.Sockets.SocketException) return (SocketFailure, null);
        }
        return (null, null);
    }

    /// <inheritdoc />
    public string? ClassifyNumber(int? number, bool connecting) => number switch
    {
        UnknownUser or WrongPassword or RequiredPassword or AuthenticationFailed or HttpStatusBase + 401 => DataKeys.AuthenticationFailed,
        AccessDenied or Readonly or HttpStatusBase + 403 => DataKeys.PermissionDenied,
        UnknownDatabase => DataKeys.DatabaseNotFound,
        TimeoutExceeded or TooSlow or SocketTimeout or HttpStatusBase + 408 or HttpStatusBase + 504 => DataKeys.Timeout,
        SocketFailure => DataKeys.HostUnreachable,
        NetworkError or AllConnectionTriesFailed or HttpStatusBase + 502 or HttpStatusBase + 503 => connecting ? DataKeys.HostUnreachable : DataKeys.ConnectionFailed,
        QueryWasCancelled => DataKeys.Cancelled,
        _ => null,
    };
}

/// <summary>
/// Puts <c>readonly=1</c> on EVERY request the driver sends, once, as the last URL parameter. ClickHouse applies URL settings in order and
/// refuses to change settings once <c>readonly=1</c> is in force, so placing it last keeps the driver's own settings (query id, format)
/// working while guaranteeing the request cannot write. Anything the driver or a caller put under the name <c>readonly</c> is replaced.
/// </summary>
public sealed class ReadOnlyHandler : DelegatingHandler
{
    /// <summary>Creates the handler in front of <paramref name="inner"/>.</summary>
    public ReadOnlyHandler(HttpMessageHandler inner) : base(inner) { }

    /// <summary>Rewrites the query string of <paramref name="uri"/> so that it ends with <c>readonly=1</c> and holds no other <c>readonly</c>.</summary>
    public static Uri Enforce(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string query = uri.Query.TrimStart('?');
        var kept = new List<string>();
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string name = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            if (name.Equals("readonly", StringComparison.OrdinalIgnoreCase)) continue;
            // The driver always writes enable_http_compression; "false" is the server default, and every setting that is sent is one more thing
            // a restricted profile can refuse. Leaving it out changes nothing for the server.
            if (name.Equals("enable_http_compression", StringComparison.OrdinalIgnoreCase)
                && (eq >= 0 && part[(eq + 1)..] is "false" or "0")) continue;
            kept.Add(part);
        }
        kept.Add("readonly=1");
        return new UriBuilder(uri) { Query = string.Join('&', kept) }.Uri;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri) request.RequestUri = Enforce(uri);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Shared <see cref="HttpClient"/> instances (one per trust setup): an HttpClient is meant to live for the whole process.</summary>
internal static class HttpClients
{
    private static readonly ConcurrentDictionary<(string? Ca, bool Trust), HttpClient> Cache = new();

    public static HttpClient Get(string? caPath, bool trustAnyCertificate)
        => Cache.GetOrAdd((string.IsNullOrWhiteSpace(caPath) ? null : Path.GetFullPath(caPath), trustAnyCertificate), key => Create(key.Ca, key.Trust));

    internal static HttpClient Create(string? caPath, bool trustAnyCertificate)
    {
        X509Certificate2? ca = caPath is null ? null : X509Certificate2.CreateFromPem(File.ReadAllText(caPath));
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // None: the driver decodes compressed answers itself and a mask here would widen what it asks the server for.
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) => ValidateServerCertificate(certificate, errors, ca, trustAnyCertificate),
            },
        };
        // Rotula's own timeout and the server's max_execution_time stop a long query; the client must not cut a slow download first.
        return new HttpClient(new ReadOnlyHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// The default Windows validation, plus the user's CA file as an extra root, plus an explicit "trust this server" switch. A name that
    /// does not match the host is never accepted unless the user chose to trust the server.
    /// </summary>
    internal static bool ValidateServerCertificate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2? ca, bool trustAnyCertificate)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (trustAnyCertificate) return true;
        if (ca is null || certificate is null) return false;
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}
