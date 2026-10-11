using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using Rotula.Data;
using Rotula.Data.Pack.ClickHouse;

namespace Rotula.Data.Packs.Tests;

/// <summary>
/// The ClickHouse pack WITHOUT a server (ClickHouse has no native Windows build): the connection string, the read-only request rewriting,
/// the real HTTP requests the driver produces (captured by a fake handler), error mapping from canned server replies, and the TLS rules.
/// What a real server does with these requests is UNVERIFIED and listed as such in the pack's README.
/// </summary>
public class ClickHousePackTests
{
    private static readonly ClickHouseDescriptor D = new();

    private static DatabaseConnectionSettings Settings() => new()
    {
        Name = "ch", Provider = DatabaseProvider.Custom, ProviderId = "clickhouse", Host = "ch.example.com", Authentication = DatabaseAuthentication.UserPassword,
        UserName = "reader", Database = "sales",
    };

    private static Dictionary<string, string> Parse(string cs)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k], CultureInfo.InvariantCulture)!, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- schema and connection string

    [Fact]
    public void TheSchema_AsksForServerPortDatabaseAccountAndTls()
    {
        Assert.Equal(new[] { "host", "port", "database", "userName", "encrypt", "trustServerCertificate", "caCertificatePath", "serverTimeLimit" }, D.Options.Select(o => o.Key));
        Assert.Equal(8443, D.DefaultPort);
        Assert.Equal(new[] { DatabaseAuthentication.UserPassword }, D.SupportedAuthentication);
        Assert.Equal("true", D.Options.Single(o => o.Key == "encrypt").DefaultValue);
        Assert.Equal("false", D.Options.Single(o => o.Key == "trustServerCertificate").DefaultValue);
        Assert.Same(SqlDialects.ClickHouse, D.Dialect);
        Assert.False(D.ReadOnly.StartTransaction);
        Assert.Contains("readonly=1", D.ReadOnly.Description);
    }

    [Fact]
    public void TheConnectionString_IsHttps8443ByDefault_WithReadOnlySentExplicitly()
    {
        var p = Parse(D.BuildConnectionString(Settings(), "pw"));
        Assert.Equal("ch.example.com", p["Host"]);
        Assert.Equal("8443", p["Port"]);
        Assert.Equal("https", p["Protocol"]);
        Assert.Equal("reader", p["Username"]);
        Assert.Equal("pw", p["Password"]);
        Assert.Equal("sales", p["Database"]);
        Assert.Equal("1", p["set_readonly"]);
        // Off by default: a profile with readonly=1 refuses a setting that changes (source: SettingsConstraints.cpp, "Cannot modify ... in readonly mode").
        Assert.False(p.ContainsKey("set_max_execution_time"));
        Assert.Equal("False", p["Compression"]);
        Assert.Equal("False", p["UseSession"]);
    }

    [Fact]
    public void WithEncryptOff_ItIsPlainHttpOn8123()
    {
        var p = Parse(D.BuildConnectionString(Settings() with { Encrypt = false }, "pw"));
        Assert.Equal("http", p["Protocol"]);
        Assert.Equal("8123", p["Port"]);
    }

    [Fact]
    public void AnExplicitPort_IsKept()
        => Assert.Equal("9443", Parse(D.BuildConnectionString(Settings() with { Port = 9443 }, "pw"))["Port"]);

    [Theory]
    [InlineData("pw;set_readonly=0")]
    [InlineData("pw;Protocol=http")]
    [InlineData("a'b\"c;Host=evil")]
    public void AHostilePassword_IsOneValue_NeverAnExtraKeyword(string password)
    {
        var p = Parse(D.BuildConnectionString(Settings(), password));
        Assert.Equal(password, p["Password"]);
        Assert.Equal("1", p["set_readonly"]);
        Assert.Equal("https", p["Protocol"]);
        Assert.Equal("ch.example.com", p["Host"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("host;x=y")]
    [InlineData("ho st")]
    public void ABadHost_IsRefused(string? host)
        => Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Host = host }));

    [Fact]
    public void AUserNameIsRequired_AndWindowsSignInIsNotOffered()
    {
        Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { UserName = null }));
        Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Authentication = DatabaseAuthentication.WindowsIntegrated }));
    }

    [Fact]
    public void AMissingCaFile_IsReportedBeforeConnecting()
    {
        var ex = Assert.Throws<DatabaseException>(() => D.Preflight(Settings() with { CaCertificatePath = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".pem") }));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
    }

    // ---------------------------------------------------------------- the read-only handler

    [Theory]
    [InlineData("http://h:8123/?query=SELECT%201", "http://h:8123/?query=SELECT%201&readonly=1")]
    [InlineData("http://h:8123/", "http://h:8123/?readonly=1")]
    [InlineData("http://h:8123/?a=1&readonly=0&b=2", "http://h:8123/?a=1&b=2&readonly=1")]
    [InlineData("http://h:8123/?READONLY=2&a=1", "http://h:8123/?a=1&readonly=1")]
    [InlineData("http://h:8123/?%72eadonly=0&a=1", "http://h:8123/?a=1&readonly=1")]
    [InlineData("http://h:8123/?readonly=1&readonly=0&readonly=2", "http://h:8123/?readonly=1")]
    [InlineData("http://h:8123/?readonly&a=1", "http://h:8123/?a=1&readonly=1")]
    [InlineData("http://h:8123/?param_readonly=5&a=1", "http://h:8123/?param_readonly=5&a=1&readonly=1")]
    [InlineData("http://h:8123/?query=SELECT%20readonly%3D0", "http://h:8123/?query=SELECT%20readonly%3D0&readonly=1")]
    [InlineData("http://h:8123/?enable_http_compression=false&a=1", "http://h:8123/?a=1&readonly=1")]
    [InlineData("http://h:8123/?enable_http_compression=0&a=1", "http://h:8123/?a=1&readonly=1")]
    [InlineData("http://h:8123/?enable_http_compression=true&a=1", "http://h:8123/?enable_http_compression=true&a=1&readonly=1")]
    public void Enforce_PutsReadOnlyLast_AndRemovesAnyOther(string input, string expected)
        => Assert.Equal(expected, ReadOnlyHandler.Enforce(new Uri(input)).AbsoluteUri);

    [Fact]
    public async Task TheHandler_RewritesEveryRequest()
    {
        var seen = new ConcurrentQueue<Uri>();
        var inner = new FakeHandler(r => { seen.Enqueue(r.RequestUri!); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(new ReadOnlyHandler(inner));
        await client.GetAsync("http://h/?readonly=0&x=1");
        await client.PostAsync("http://h/?readonly=2", new StringContent("SELECT 1"));
        Assert.Equal(new[] { "http://h/?x=1&readonly=1", "http://h/?readonly=1" }, seen.Select(u => u.ToString()));
    }

    // ---------------------------------------------------------------- the real driver, a fake network

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private sealed record Captured(Uri Uri, string Method, string Body, IReadOnlyDictionary<string, string> Headers);

    private sealed class Pack(ClickHouseDescriptor descriptor) : IDatabaseProviderPack
    {
        public string PackId => "clickhouse";
        public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { descriptor };
    }

    /// <summary>A source whose HTTP goes to <paramref name="respond"/>; every request is captured.</summary>
    private static (IDatabaseSource Source, ConcurrentQueue<Captured> Requests) Fake(Func<HttpRequestMessage, HttpResponseMessage> respond, DatabaseConnectionSettings? settings = null)
    {
        var requests = new ConcurrentQueue<Captured>();
        var handler = new FakeHandler(r =>
        {
            string body = r.Content is null ? string.Empty : r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var headers = r.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            requests.Enqueue(new Captured(r.RequestUri!, r.Method.Method, body, headers));
            return respond(r);
        });
        var registry = new ProviderRegistry();
        registry.Register(new Pack(new ClickHouseDescriptor(() => handler)));
        return (DatabaseSource.CreateUsing(settings ?? Settings(), "pw", registry), requests);
    }

    /// <summary>A RowBinaryWithNamesAndTypes reply with one String column and the given rows.</summary>
    private static HttpResponseMessage Rows(string column, params string[] values)
    {
        var ms = new MemoryStream();
        void Varint(int v) { while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; } ms.WriteByte((byte)v); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); Varint(b.Length); ms.Write(b); }
        Varint(1); Str(column); Str("String");
        foreach (string v in values) Str(v);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ms.ToArray()) };
        response.Headers.TryAddWithoutValidation("X-ClickHouse-Format", "RowBinaryWithNamesAndTypes");
        return response;
    }

    private static HttpResponseMessage ServerError(HttpStatusCode status, string text)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private static IReadOnlyList<KeyValuePair<string, string>> Query(Uri uri)
    {
        var nv = HttpUtility.ParseQueryString(uri.Query);
        return nv.AllKeys.SelectMany(k => nv.GetValues(k)!.Select(v => new KeyValuePair<string, string>(k!, v))).ToList();
    }

    [Fact]
    public async Task EveryRequestTheDriverSends_CarriesReadOnlyOnce_AsTheLastParameter()
    {
        var (source, requests) = Fake(_ => Rows("name", "Niño", "日本語"));
        var result = await source.QueryAsync("SELECT name FROM people WHERE id = @id", new Dictionary<string, object?> { ["id"] = 7 });

        Assert.Equal(2, result.Table.RowCount);
        Assert.Equal("Niño", result.Table.Rows[0][0]);
        Assert.Equal("日本語", result.Table.Rows[1][0]);
        Assert.NotEmpty(requests);
        foreach (var req in requests)
        {
            var q = Query(req.Uri);
            Assert.Single(q, kv => kv.Key.Equals("readonly", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new KeyValuePair<string, string>("readonly", "1"), q[^1]);
        }
    }

    [Fact]
    public async Task TheDriversOwnExtraSettings_AreKeptToTheMinimum()
    {
        var (source, requests) = Fake(_ => Rows("x", "1"));
        await source.QueryAsync("SELECT 1 AS x");
        var q = Query(requests.Single().Uri);
        var names = q.Select(kv => kv.Key).ToList();
        // Only settings an account with a readonly=1 profile may still send (source: SettingsConstraints.cpp isAlwaysChangeableInReadonly: database and
        // default_format; readonly itself is unchanged for such a profile; query_id and param_* are not settings). No session, no JSON toggles, no time limit.
        Assert.DoesNotContain("session_id", names);
        Assert.DoesNotContain("output_format_binary_write_json_as_string", names);
        Assert.DoesNotContain("input_format_binary_read_json_as_string", names);
        Assert.DoesNotContain("max_execution_time", names);
        Assert.DoesNotContain("enable_http_compression", names);
        Assert.Contains(new KeyValuePair<string, string>("database", "sales"), q);
        // Everything that is a setting is listed here, so adding one by accident fails this test.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "default_format", "database", "query_id", "readonly", "query" };
        Assert.All(names, n => Assert.True(allowed.Contains(n) || n.StartsWith("param_", StringComparison.Ordinal), "unexpected URL parameter " + n));
    }

    [Fact]
    public async Task TheServerTimeLimit_IsSentOnlyWhenAskedFor()
    {
        var withLimit = Settings() with { Extras = ProviderExtras.Of(("serverTimeLimit", "true")), CommandTimeoutSeconds = 12 };
        Assert.Equal("12", Parse(D.BuildConnectionString(withLimit, "pw"))["set_max_execution_time"]);
        var (source, requests) = Fake(_ => Rows("x", "1"), withLimit);
        await source.QueryAsync("SELECT 1 AS x");
        Assert.Contains(new KeyValuePair<string, string>("max_execution_time", "12"), Query(requests.Single().Uri));
        // readonly=1 is still the last parameter, so the limit is applied before it.
        Assert.Equal(new KeyValuePair<string, string>("readonly", "1"), Query(requests.Single().Uri)[^1]);
    }

    [Fact]
    public async Task ParametersTravelAsTypedServerParameters_NotInTheText()
    {
        var (source, requests) = Fake(_ => Rows("x", "1"));
        await source.QueryAsync("SELECT name FROM people WHERE city = @city AND note = '@city'", new Dictionary<string, object?> { ["city"] = "O'Brien; DROP TABLE people" });
        var req = requests.Single();
        string sql = Query(req.Uri).SingleOrDefault(kv => kv.Key == "query").Value ?? req.Body;
        Assert.Contains("{city:String}", sql);
        // The '@city' inside the string literal is data, not a placeholder.
        Assert.Contains("note = '@city'", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        var all = Query(req.Uri).Concat(req.Body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p.Split('=')[0]), Uri.UnescapeDataString(p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : string.Empty)))).ToList();
        // The driver writes the value the way the ClickHouse HTTP interface expects it (a quote is backslash-escaped); the server reads it back as data.
        Assert.Contains(all, kv => kv.Key == "param_city" && kv.Value == "O\\'Brien; DROP TABLE people");
    }

    [Fact]
    public async Task Credentials_AreSentAsAuthorizationHeaders_NotInTheUrl()
    {
        var (source, requests) = Fake(_ => Rows("x", "1"));
        await source.QueryAsync("SELECT 1 AS x");
        var req = requests.Single();
        Assert.DoesNotContain("pw", req.Uri.ToString());
        Assert.StartsWith("https://ch.example.com:8443/", req.Uri.ToString());
        Assert.True(req.Headers.ContainsKey("Authorization") || req.Headers.ContainsKey("X-ClickHouse-User"), string.Join(",", req.Headers.Keys));
    }

    [Fact]
    public async Task TheConnectionTest_MakesARealRequest()
    {
        var (source, requests) = Fake(_ => Rows("1", "1"));
        await source.TestConnectionAsync();
        Assert.NotEmpty(requests);
        Assert.Equal("SELECT 1", Query(requests.First().Uri).SingleOrDefault(kv => kv.Key == "query").Value ?? requests.First().Body.Trim());
    }

    [Fact]
    public async Task AWriteTheServerRefuses_StaysAnErrorForTheCaller()
    {
        // Even if the guard were bypassed, the server answers readonly errors with code 164; it maps to a permission error.
        var (source, _) = Fake(_ => ServerError(HttpStatusCode.Forbidden, "Code: 164. DB::Exception: Cannot execute query in readonly mode. (READONLY)"));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.QueryAsync("SELECT 1"));
        Assert.Equal(DataKeys.PermissionDenied, ex.Key);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Code: 516. DB::Exception: reader: Authentication failed: password is incorrect, or there is no user with such name. (AUTHENTICATION_FAILED)", DataKeys.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, "Code: 497. DB::Exception: reader: Not enough privileges. (ACCESS_DENIED)", DataKeys.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, "Code: 81. DB::Exception: Database nope does not exist. (UNKNOWN_DATABASE)", DataKeys.DatabaseNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, "Code: 159. DB::Exception: Timeout exceeded: elapsed 31 seconds, maximum: 30. (TIMEOUT_EXCEEDED)", DataKeys.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, "Code: 60. DB::Exception: Table sales.nope does not exist. (UNKNOWN_TABLE)", DataKeys.QueryFailed)]
    [InlineData(HttpStatusCode.InternalServerError, "Code: 62. DB::Exception: Syntax error. (SYNTAX_ERROR)", DataKeys.QueryFailed)]
    [InlineData(HttpStatusCode.InternalServerError, "Code: 394. DB::Exception: Query was cancelled. (QUERY_WAS_CANCELLED)", DataKeys.Cancelled)]
    public async Task ServerReplies_AreMappedToTheStableKeys(HttpStatusCode status, string body, string key)
    {
        var (source, _) = Fake(_ => ServerError(status, body));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.QueryAsync("SELECT 1"));
        Assert.Equal(key, ex.Key);
        Assert.DoesNotContain("pw", ex.Message + ex.Detail);
    }

    [Fact]
    public async Task ANetworkFailure_IsAConnectionProblem_NotAQueryError()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Pack(new ClickHouseDescriptor(() => new ThrowingHandler(new HttpRequestException("connection refused", new SocketException(10061))))));
        var source = DatabaseSource.CreateUsing(Settings(), "pw", registry);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
        // HTTP opens nothing until the first request, so the shared mapper sees it after "connecting" and may call it ConnectionFailed; never QueryFailed.
        Assert.Contains(ex.Key, new[] { DataKeys.HostUnreachable, DataKeys.ConnectionFailed });
        Assert.Equal(DataKeys.HostUnreachable, D.Errors.ClassifyNumber(D.Errors.Extract(new HttpRequestException("x", new SocketException(10061))).Number, false));
    }

    [Fact]
    public async Task ATlsFailure_IsCertificateNotTrusted()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Pack(new ClickHouseDescriptor(() => new ThrowingHandler(new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid."))))));
        var source = DatabaseSource.CreateUsing(Settings(), "pw", registry);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
        Assert.Equal(DataKeys.CertificateNotTrusted, ex.Key);
    }

    [Fact]
    public async Task AClientSideTimeout_IsATimeout()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Pack(new ClickHouseDescriptor(() => new ThrowingHandler(new TaskCanceledException("timeout", new TimeoutException())))));
        var source = DatabaseSource.CreateUsing(Settings(), "pw", registry);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
        Assert.Equal(DataKeys.Timeout, ex.Key);
    }

    [Fact]
    public async Task ACancelledToken_StopsTheRequest()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Pack(new ClickHouseDescriptor(() => new HangingHandler())));
        var source = DatabaseSource.CreateUsing(Settings(), "pw", registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.QueryAsync("SELECT 1", null, 10, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw ex;
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    // ---------------------------------------------------------------- error numbers

    [Theory]
    [InlineData(192, DataKeys.AuthenticationFailed, true)]
    [InlineData(193, DataKeys.AuthenticationFailed, true)]
    [InlineData(194, DataKeys.AuthenticationFailed, true)]
    [InlineData(516, DataKeys.AuthenticationFailed, true)]
    [InlineData(1_000_401, DataKeys.AuthenticationFailed, true)]
    [InlineData(497, DataKeys.PermissionDenied, false)]
    [InlineData(164, DataKeys.PermissionDenied, false)]
    [InlineData(1_000_403, DataKeys.PermissionDenied, false)]
    [InlineData(81, DataKeys.DatabaseNotFound, true)]
    [InlineData(159, DataKeys.Timeout, false)]
    [InlineData(160, DataKeys.Timeout, false)]
    [InlineData(209, DataKeys.Timeout, false)]
    [InlineData(1_000_504, DataKeys.Timeout, false)]
    [InlineData(210, DataKeys.HostUnreachable, true)]
    [InlineData(210, DataKeys.ConnectionFailed, false)]
    [InlineData(279, DataKeys.HostUnreachable, true)]
    [InlineData(1_000_503, DataKeys.HostUnreachable, true)]
    [InlineData(394, DataKeys.Cancelled, false)]
    public void TheClickHouseCodes_MapToTheStableKeys(int code, string key, bool connecting)
        => Assert.Equal(key, D.Errors.ClassifyNumber(code, connecting));

    [Fact]
    public void AnUnknownCode_FallsBackToTheGenericKey() => Assert.Null(D.Errors.ClassifyNumber(62, false));

    [Fact]
    public void TheDriverExceptionTypes_AreUnpacked()
    {
        var server = new global::ClickHouse.Driver.ClickHouseServerException("Code: 516. DB::Exception: x", "SELECT 1", 516);
        Assert.Equal((516, (string?)null), D.Errors.Extract(new InvalidOperationException("wrapper", server)));
        var http = new HttpRequestException("no", null, HttpStatusCode.ServiceUnavailable);
        Assert.Equal((1_000_503, (string?)null), D.Errors.Extract(http));
        Assert.Equal(((int?)null, (string?)null), D.Errors.Extract(new Exception("other")));
    }

    // ---------------------------------------------------------------- TLS rules

    private static (X509Certificate2 Ca, X509Certificate2 Leaf) MakeChain(string name = "CN=Rotula test CA")
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(name, caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=ch.example.com", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("ch.example.com");
        leafReq.CertificateExtensions.Add(san.Build());
        var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), Guid.NewGuid().ToByteArray());
        return (X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert)), X509CertificateLoader.LoadCertificate(leaf.Export(X509ContentType.Cert)));
    }

    [Fact]
    public void ACertificateTheSystemTrusts_IsAccepted() => Assert.True(HttpClients.ValidateServerCertificate(null, SslPolicyErrors.None, null, false));

    [Fact]
    public void AnUntrustedCertificate_IsRefused_ByDefault()
    {
        var (_, leaf) = MakeChain();
        Assert.False(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors, null, false));
        Assert.False(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateNameMismatch, null, false));
        Assert.False(HttpClients.ValidateServerCertificate(null, SslPolicyErrors.RemoteCertificateNotAvailable, null, false));
    }

    [Fact]
    public void TheUsersCaFile_MakesItsCertificatesTrusted_ButNotAnotherCas()
    {
        var (ca, leaf) = MakeChain();
        var (otherCa, otherLeaf) = MakeChain("CN=Other CA");
        Assert.True(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors, ca, false));
        Assert.False(HttpClients.ValidateServerCertificate(otherLeaf, SslPolicyErrors.RemoteCertificateChainErrors, ca, false));
        Assert.False(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors, otherCa, false));
    }

    [Fact]
    public void AWrongName_IsNeverAcceptedThroughTheCaFile()
    {
        var (ca, leaf) = MakeChain();
        Assert.False(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, ca, false));
    }

    [Fact]
    public void TrustThisServer_AcceptsAnything_OnlyWhenExplicitlyChosen()
    {
        var (_, leaf) = MakeChain();
        Assert.True(HttpClients.ValidateServerCertificate(leaf, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, null, true));
    }

    // ---------------------------------------------------------------- guard and quoting (ClickHouse "Syntax" page)

    [Theory]
    [InlineData("SELECT 1 -- comment")]
    [InlineData("SELECT 1 # comment")]
    [InlineData("SELECT 1 #! comment")]
    [InlineData("SELECT 1 // comment")]
    [InlineData("SELECT 1 /* a /* nested */ b */")]
    [InlineData("SELECT 'it''s'")]
    [InlineData("SELECT $$text$$")]
    [InlineData("SELECT `a b` FROM t")]
    [InlineData("SELECT \"a b\" FROM t")]
    [InlineData("SELECT * FROM numbers(10)")]
    [InlineData("SELECT name FROM people WHERE id = @id")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    public void ReadingStatements_AreAccepted(string sql)
    {
        var r = SqlGuard.Check(sql, D.Dialect);
        Assert.True(r.IsAllowed, sql + " => " + r.ReasonKey);
    }

    [Theory]
    [InlineData("SELECT * FROM url('http://example.com/x', CSV)")]
    [InlineData("SELECT * FROM file('/etc/passwd', CSV)")]
    [InlineData("SELECT * FROM s3('https://b/x.csv')")]
    [InlineData("SELECT * FROM remote('other:9000', db, t)")]
    [InlineData("SELECT * FROM remoteSecure('other:9440', db, t)")]
    [InlineData("SELECT * FROM mysql('h:3306', 'd', 't', 'u', 'p')")]
    [InlineData("SELECT * FROM postgresql('h:5432', 'd', 't', 'u', 'p')")]
    [InlineData("SELECT * FROM jdbc('x', 't')")]
    [InlineData("SELECT * FROM odbc('x', 't')")]
    [InlineData("SELECT * FROM hdfs('hdfs://x/y', CSV)")]
    [InlineData("SELECT * FROM executable('x', TSV)")]
    [InlineData("SELECT * FROM input('a Int')")]
    [InlineData("SELECT * FROM cluster('c', db, t)")]
    [InlineData("SELECT * FROM clusterAllReplicas('c', db, t)")]
    [InlineData("SELECT * FROM icebergS3('x')")]
    [InlineData("SELECT * FROM FILE('x')")]
    [InlineData("SELECT * FROM t SETTINGS readonly = 0")]
    [InlineData("SELECT 1 SETTINGS max_execution_time = 0")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("OPTIMIZE TABLE t")]
    [InlineData("SYSTEM FLUSH LOGS")]
    [InlineData("SELECT 1 INTO OUTFILE 'x.csv'")]
    [InlineData("SELECT NEXT VALUE FOR s")]
    public void DangerousStatements_AreRefused(string sql) => Assert.False(SqlGuard.Check(sql, D.Dialect).IsAllowed, sql);

    [Fact]
    public void AColumnNamedLikeATableFunction_IsStillFine()
    {
        Assert.True(SqlGuard.Check("SELECT file, url, s3, input FROM t", D.Dialect).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT t.file FROM t", D.Dialect).IsAllowed);
    }

    [Theory]
    [InlineData("SELECT 'a\\' FROM t")]
    [InlineData("SELECT \"a\\\" FROM t")]
    [InlineData("SELECT `a\\` FROM t")]
    public void ABackslashBeforeAClosingQuote_IsAmbiguous_AndRefused(string sql)
    {
        // Backslash escapes work in ClickHouse strings AND quoted names ("the same rules ... also apply for string literals"), so the
        // quote after the backslash is text, the literal is unterminated, and the guard must not guess where the statement ends.
        var r = SqlGuard.Check(sql, D.Dialect);
        Assert.False(r.IsAllowed, sql);
    }

    [Fact]
    public void ABackslashInsideAQuotedName_HidesNothing()
    {
        // `a\`` is one name that contains a backtick; what follows is code. The DROP after the semicolon must be seen.
        var r = SqlGuard.Check("SELECT `a\\`` FROM t; DROP TABLE t", D.Dialect);
        Assert.False(r.IsAllowed);
    }

    [Theory]
    [InlineData("people", "`people`")]
    [InlineData("we`ird", "`we``ird`")]
    [InlineData("back\\slash", "`back\\\\slash`")]
    [InlineData("ends\\", "`ends\\\\`")]
    [InlineData("a\\`b", "`a\\\\``b`")]
    public void IdentifierQuoting_EscapesTheBackslashAndTheQuote(string name, string expected)
        => Assert.Equal(expected, D.Dialect.QuoteIdentifier(name));

    [Theory]
    [InlineData("ends\\")]
    [InlineData("a\\`; DROP TABLE t; --")]
    [InlineData("x`y\\")]
    public void AQuotedIdentifier_NeverBreaksOut(string name)
    {
        string sql = "SELECT * FROM " + D.Dialect.QuoteIdentifier(name);
        // The quoted name must lex as ONE identifier token followed by the end of the statement: nothing else is visible to the guard.
        var tokens = SqlTokenizer.Tokenize(sql, D.Dialect.LexRules[0]);
        Assert.Null(tokens.ErrorKey);
        Assert.Equal(1, tokens.Tokens.Count(t => t.Kind == SqlTokenKind.Quoted));
        // The token holds the text between the quotes with the escapes still in place; undoing them gives the name back.
        string inner = tokens.Tokens.Single(t => t.Kind == SqlTokenKind.Quoted).Text;
        Assert.Equal(name, inner.Replace("\\\\", "\\").Replace("``", "`"));
    }

    [Fact]
    public void TheCatalogueSql_ListsTablesAndViews_AndPassesTheGuard()
    {
        string sql = D.Dialect.CatalogueSql!;
        Assert.Contains("system.tables", sql);
        Assert.Contains("engine", sql);
        Assert.True(SqlGuard.Check(sql, D.Dialect).IsAllowed);
        Assert.Equal("SELECT 1", D.Dialect.ConnectivityProbeSql);
        Assert.Equal("SELECT * FROM t LIMIT 5", D.Dialect.ApplyRowLimit("SELECT * FROM t", 5));
    }

    [Fact]
    public async Task ListTables_UsesTheCatalogueSql_AndReadsTheThreeColumns()
    {
        string? seen = null;
        var (source, _) = Fake(r =>
        {
            seen = Query(r.RequestUri!).SingleOrDefault(kv => kv.Key == "query").Value ?? r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return Rows("database", "sales");
        });
        // The fake returns a single column, so only the request is checked here.
        try { await source.ListTablesAsync(); } catch (DatabaseException) { }
        Assert.Contains("system.tables", seen);
    }
}
