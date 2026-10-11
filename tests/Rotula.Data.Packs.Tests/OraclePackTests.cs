using System.Data.Common;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Rotula.Data;
using Rotula.Data.Pack.Oracle;

namespace Rotula.Data.Packs.Tests;

/// <summary>
/// The Oracle pack WITHOUT a server (an Oracle Database cannot be installed silently on a developer machine): the connection string and
/// connect descriptor, validation against descriptor injection, parameter style, guard rules, and error mapping, including one real
/// driver exception (connecting to a closed port). Behaviour against a real Oracle server is UNVERIFIED and listed in the pack's README.
/// </summary>
public class OraclePackTests
{
    private static readonly OracleDescriptor D = new();

    private static DatabaseConnectionSettings Settings() => new()
    {
        Name = "ora", Provider = DatabaseProvider.Custom, ProviderId = "oracle", Host = "db.example.com", Database = "SALES",
        Authentication = DatabaseAuthentication.UserPassword, UserName = "READER",
    };

    private static Dictionary<string, string> Parse(string cs)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k], CultureInfo.InvariantCulture)!, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- schema

    [Fact]
    public void TheSchema_AsksForHostPortServiceAccountAndTls()
    {
        Assert.Equal(new[] { "host", "port", "database", "userName", "encrypt", "trustServerCertificate", "connectBy", "tnsAdmin", "walletDirectory" }, D.Options.Select(o => o.Key));
        Assert.Equal(1521, D.DefaultPort);
        Assert.Equal(new[] { DatabaseAuthentication.UserPassword, DatabaseAuthentication.WindowsIntegrated }, D.SupportedAuthentication);
        Assert.Equal("true", D.Options.Single(o => o.Key == "encrypt").DefaultValue);
        var connectBy = D.Options.Single(o => o.Key == "connectBy");
        Assert.Equal(new[] { "service", "sid", "alias" }, connectBy.Choices.Select(c => c.Value));
        Assert.All(D.Options, o => Assert.StartsWith("Data.Option.", o.LabelKey));
        Assert.Same(SqlDialects.Oracle, D.Dialect);
        Assert.Equal(new[] { "SET TRANSACTION READ ONLY" }, D.ReadOnly.TransactionStatements);
        Assert.Contains("SELECT rights only", D.ReadOnly.Description);
    }

    [Fact]
    public void ValidSettings_PassThroughTheRegistry_AndUnknownExtrasAreRefused()
    {
        var registry = new ProviderRegistry();
        registry.Register(new OraclePack());
        DatabaseSource.CreateUsing(Settings(), "pw", registry);
        Assert.Throws<DatabaseException>(() => DatabaseSource.CreateUsing(Settings() with { Extras = ProviderExtras.Of(("dbaPrivilege", "SYSDBA")) }, "pw", registry));
        Assert.Throws<DatabaseException>(() => DatabaseSource.CreateUsing(Settings() with { Extras = ProviderExtras.Of(("connectBy", "tns")) }, "pw", registry));
    }

    // ---------------------------------------------------------------- the connect descriptor

    [Fact]
    public void ByDefault_TheDescriptorUsesTls_OnThe2484Port_AndChecksTheServerName()
    {
        string ds = OracleDescriptor.BuildDataSource(Settings());
        Assert.Equal("(DESCRIPTION=(CONNECT_TIMEOUT=10)(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example.com)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=SALES))(SECURITY=(SSL_SERVER_DN_MATCH=yes)))", ds);
    }

    [Fact]
    public void WithEncryptOff_ItIsPlainTcpOn1521_WithNoSecuritySection()
    {
        string ds = OracleDescriptor.BuildDataSource(Settings() with { Encrypt = false });
        Assert.Equal("(DESCRIPTION=(CONNECT_TIMEOUT=10)(ADDRESS=(PROTOCOL=TCP)(HOST=db.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=SALES)))", ds);
    }

    [Fact]
    public void TrustThisServer_OnlyTurnsOffTheNameCheck()
    {
        string ds = OracleDescriptor.BuildDataSource(Settings() with { TrustServerCertificate = true });
        Assert.Contains("(SSL_SERVER_DN_MATCH=no)", ds);
        Assert.Contains("PROTOCOL=TCPS", ds);
    }

    [Fact]
    public void ASid_AnAlias_APortAndAWallet_AreHandled()
    {
        Assert.Contains("(CONNECT_DATA=(SID=ORCL))", OracleDescriptor.BuildDataSource(Settings() with { Database = "ORCL", Extras = ProviderExtras.Of(("connectBy", "sid")) }));
        Assert.Equal("SALES_HIGH", OracleDescriptor.BuildDataSource(Settings() with { Database = "SALES_HIGH", Extras = ProviderExtras.Of(("connectBy", "alias")) }));
        Assert.Contains("(PORT=1522)", OracleDescriptor.BuildDataSource(Settings() with { Port = 1522 }));
        string withWallet = OracleDescriptor.BuildDataSource(Settings() with { Extras = ProviderExtras.Of(("walletDirectory", @"C:\wallets\sales")) });
        Assert.Contains(@"(MY_WALLET_DIRECTORY=C:\wallets\sales)", withWallet);
    }

    [Fact]
    public void TheConnectionString_IsBuiltByTheDriverBuilder()
    {
        var p = Parse(D.BuildConnectionString(Settings(), "pw"));
        Assert.Equal("READER", p["User Id"]);
        Assert.Equal("pw", p["Password"]);
        Assert.StartsWith("(DESCRIPTION=", p["Data Source"]);
        Assert.Equal("False", p["Pooling"]);
        Assert.Equal("10", p["Connection Timeout"]);
    }

    [Fact]
    public void OsAuthentication_UsesTheSlashUser_AndNoPassword()
    {
        var p = Parse(D.BuildConnectionString(Settings() with { Authentication = DatabaseAuthentication.WindowsIntegrated, UserName = null }, "ignored"));
        Assert.Equal("/", p["User Id"]);
        Assert.False(p.ContainsKey("Password"));
    }

    [Theory]
    [InlineData("pw\" ; Data Source=evil")]
    [InlineData("pw;User Id=SYS;DBA Privilege=SYSDBA")]
    [InlineData("a'b\"c;d=e")]
    public void AHostilePassword_IsOneValue_NeverAnExtraKeyword(string password)
    {
        var p = Parse(D.BuildConnectionString(Settings(), password));
        Assert.Equal(password, p["Password"]);
        Assert.Equal("READER", p["User Id"]);
        Assert.False(p.ContainsKey("DBA Privilege"));
        Assert.StartsWith("(DESCRIPTION=(CONNECT_TIMEOUT=10)", p["Data Source"]);
    }

    // ---------------------------------------------------------------- injection into the connect descriptor

    [Theory]
    [InlineData("db.example.com)(PORT=1)(HOST=evil")]
    [InlineData("db.example.com)")]
    [InlineData("db (x)")]
    [InlineData("db;x")]
    [InlineData("db=x")]
    [InlineData("db,host2")]
    [InlineData("db\\inst")]
    [InlineData("db'x")]
    [InlineData("")]
    public void AHostThatCouldCloseOrExtendTheDescriptor_IsRefused(string host)
        => Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Host = host })).Key);

    [Theory]
    [InlineData("SALES))(SECURITY=(SSL_SERVER_DN_MATCH=no")]
    [InlineData("SALES)")]
    [InlineData("SALES (X)")]
    [InlineData("SALES;X")]
    [InlineData("SA LES")]
    [InlineData("SALES\"")]
    [InlineData("")]
    public void AServiceNameThatCouldCloseOrExtendTheDescriptor_IsRefused(string service)
        => Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Database = service })).Key);

    [Theory]
    [InlineData("sales.example.com")]
    [InlineData("SALES_PDB1")]
    [InlineData("sales-1.eu_west")]
    [InlineData("XEPDB1")]
    [InlineData("a$b#c")]
    public void RealisticServiceNames_AreAccepted(string service) => D.Validate(Settings() with { Database = service });

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("10.0.0.5")]
    [InlineData("db-01.corp.example.com")]
    public void RealisticHosts_AreAccepted(string host) => D.Validate(Settings() with { Host = host });

    [Theory]
    [InlineData(@"C:\wallets\x)(HOST=evil")]
    [InlineData(@"C:\wallets\x""y")]
    [InlineData(@"\\server\share\wallet")]
    [InlineData("https://example.com/wallet")]
    public void AWalletOrTnsFolderThatCouldExtendTheDescriptor_IsRefused(string path)
    {
        Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Extras = ProviderExtras.Of(("walletDirectory", path)) }));
        Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Extras = ProviderExtras.Of(("tnsAdmin", path)) }));
    }

    [Fact]
    public void AnAliasNeedsNoHost_ButStillOnlySafeCharacters()
    {
        var alias = Settings() with { Host = null, Database = "SALES_HIGH", Extras = ProviderExtras.Of(("connectBy", "alias")) };
        D.Validate(alias);
        Assert.Throws<DatabaseException>(() => D.Validate(alias with { Database = "SALES_HIGH)(x" }));
    }

    [Fact]
    public void APasswordSignInNeedsAUserName()
        => Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { UserName = null }));

    // ---------------------------------------------------------------- commands and parameters

    [Fact]
    public void Parameters_AreColonNamed_AndBoundByName()
    {
        Assert.Equal(ParameterStyle.ColonName, D.Dialect.ParameterStyle);
        var command = new OracleCommand();
        Assert.False(command.BindByName);
        D.ConfigureCommand(command);
        Assert.True(command.BindByName);
        Assert.Equal("id", D.ParameterName("id"));
        var (sql, order) = ParameterRewriter.Rewrite("SELECT * FROM T WHERE A = @id AND B = @name AND C = @id", SqlGuard.Check("SELECT * FROM T WHERE A = @id AND B = @name AND C = @id", D.Dialect).ParameterTokens, ParameterStyle.ColonName);
        Assert.Equal("SELECT * FROM T WHERE A = :id AND B = :name AND C = :id", sql);
        Assert.Equal(new[] { "id", "name", "id" }, order);
    }

    [Fact]
    public void TheRowLimit_IsFetchFirst_AndTheCatalogueReadsAllTablesAndViews()
    {
        Assert.Equal("SELECT * FROM T FETCH FIRST 5 ROWS ONLY", D.Dialect.ApplyRowLimit("SELECT * FROM T", 5));
        string sql = D.Dialect.CatalogueSql!;
        Assert.Contains("all_tables", sql);
        Assert.Contains("all_views", sql);
        Assert.True(SqlGuard.Check(sql, D.Dialect).IsAllowed);
    }

    [Theory]
    [InlineData("people", "\"people\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    [InlineData("back\\slash", "\"back\\slash\"")]
    public void IdentifierQuoting_UsesDoubleQuotes_AndCaseIsKept(string name, string expected)
        => Assert.Equal(expected, D.Dialect.QuoteIdentifier(name));

    // ---------------------------------------------------------------- guard (Oracle SQL Language Reference)

    [Theory]
    [InlineData("SELECT * FROM T FETCH FIRST 10 ROWS ONLY")]
    [InlineData("SELECT * FROM (SELECT * FROM T) WHERE ROWNUM <= 5")]
    [InlineData("SELECT /*+ INDEX(t i) */ * FROM T")]
    [InlineData("SELECT * FROM DUAL -- c")]
    [InlineData("SELECT q'[it's]' FROM DUAL")]
    [InlineData("SELECT q'{a}b}' FROM DUAL")]
    [InlineData("SELECT Q'<a>' FROM DUAL")]
    [InlineData("SELECT nq'(x)' FROM DUAL")]
    [InlineData("SELECT q'!a;b!' FROM DUAL")]
    [InlineData("SELECT q'#a#' FROM DUAL")]
    [InlineData("SELECT 'it''s' FROM DUAL")]
    [InlineData("SELECT \"My Col\" FROM T")]
    [InlineData("SELECT DBMS_LOB.GETLENGTH(c) FROM T")]
    [InlineData("SELECT DBMS_RANDOM.VALUE FROM DUAL")]
    [InlineData("SELECT * FROM T WHERE A = :id")]
    [InlineData("SELECT * FROM T WHERE A = @id")]
    [InlineData("WITH X AS (SELECT 1 A FROM DUAL) SELECT * FROM X")]
    [InlineData("SELECT * FROM all_tables WHERE owner = 'SCOTT'")]
    public void ReadingStatements_AreAccepted(string sql) => Assert.True(SqlGuard.Check(sql, D.Dialect).IsAllowed, sql);

    [Theory]
    [InlineData("SELECT UTL_HTTP.REQUEST('http://x') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT utl_inaddr.get_host_address('x') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT DBMS_XMLGEN.GETXML('delete from t') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT DBMS_SCHEDULER.CREATE_JOB('x') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT OWA_UTIL.GET_CGI_ENV('x') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT HTTPURITYPE('http://x').GETCLOB() FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT SYS.DBMS_X.RUN('x') FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT * FROM T@REMOTE", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT * FROM T@\"REMOTE\"", DataKeys.RejectForbiddenFunction)]
    [InlineData("WITH FUNCTION F RETURN NUMBER IS BEGIN RETURN 1; END; SELECT F FROM DUAL", DataKeys.RejectMultipleStatements)]
    [InlineData("WITH FUNCTION F RETURN NUMBER IS BEGIN RETURN 1 END SELECT F FROM DUAL", DataKeys.RejectForbiddenKeyword)]
    [InlineData("WITH PROCEDURE P IS BEGIN NULL END SELECT 1 FROM DUAL", DataKeys.RejectForbiddenKeyword)]
    [InlineData("with function f return number is begin return 1 end select f from dual", DataKeys.RejectForbiddenKeyword)]
    [InlineData("SELECT * FROM T FOR UPDATE", DataKeys.RejectLocking)]
    [InlineData("SELECT * FROM T FOR UPDATE NOWAIT", DataKeys.RejectLocking)]
    [InlineData("SELECT SEQ.NEXTVAL FROM DUAL", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT * INTO V FROM T", DataKeys.RejectSelectInto)]
    [InlineData("BEGIN NULL END", DataKeys.RejectNotSelect)]
    [InlineData("DECLARE X NUMBER BEGIN NULL END", DataKeys.RejectNotSelect)]
    [InlineData("BEGIN NULL; END;", DataKeys.RejectMultipleStatements)]
    [InlineData("SELECT * FROM T @ REMOTE", DataKeys.RejectForbiddenFunction)]
    [InlineData("CALL P()", DataKeys.RejectNotSelect)]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM DUAL", DataKeys.RejectNotSelect)]
    [InlineData("MERGE INTO T USING DUAL ON (1=1) WHEN MATCHED THEN UPDATE SET A = 1", DataKeys.RejectNotSelect)]
    [InlineData("SELECT 1 FROM DUAL; DROP TABLE T", DataKeys.RejectMultipleStatements)]
    [InlineData("LOCK TABLE T IN EXCLUSIVE MODE", DataKeys.RejectNotSelect)]
    [InlineData("SELECT 1 FROM DUAL /* ; */ ; DELETE FROM T", DataKeys.RejectMultipleStatements)]
    public void DangerousStatements_AreRefused(string sql, string reason)
    {
        var r = SqlGuard.Check(sql, D.Dialect);
        Assert.False(r.IsAllowed, sql);
        Assert.Equal(reason, r.ReasonKey);
    }

    [Fact]
    public void AQQuoteDelimiter_HidesNothing()
    {
        // Inside q'[...]' a semicolon and a quote are text; right after it the statement continues normally.
        Assert.True(SqlGuard.Check("SELECT q'[; DROP TABLE T; ']' FROM DUAL", D.Dialect).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT q'[x]' FROM DUAL; DROP TABLE T", D.Dialect).IsAllowed);
        // The closing delimiter of a bracket pair is its mirror, so q'(a)b)' ends at the first ")'" .
        Assert.False(SqlGuard.Check("SELECT q'(a)' FROM DUAL; DROP TABLE T; SELECT q'(b)' FROM DUAL", D.Dialect).IsAllowed);
    }

    [Fact]
    public void ABlockCommentDoesNotNest_InOracle()
    {
        // Oracle ends /* */ at the first */ : the DELETE after it is a second statement.
        var r = SqlGuard.Check("SELECT 1 FROM DUAL /* a /* b */ ; DELETE FROM T ; /* c */", D.Dialect);
        Assert.False(r.IsAllowed);
        Assert.Equal(DataKeys.RejectMultipleStatements, r.ReasonKey);
    }

    // ---------------------------------------------------------------- error mapping

    [Theory]
    [InlineData(1017, DataKeys.AuthenticationFailed, true)]
    [InlineData(1005, DataKeys.AuthenticationFailed, true)]
    [InlineData(28000, DataKeys.AuthenticationFailed, true)]
    [InlineData(28001, DataKeys.AuthenticationFailed, true)]
    [InlineData(28002, DataKeys.AuthenticationFailed, true)]
    [InlineData(12514, DataKeys.DatabaseNotFound, true)]
    [InlineData(12505, DataKeys.DatabaseNotFound, true)]
    [InlineData(12154, DataKeys.DatabaseNotFound, true)]
    [InlineData(12541, DataKeys.HostUnreachable, true)]
    [InlineData(12541, DataKeys.ConnectionFailed, false)]
    [InlineData(12545, DataKeys.HostUnreachable, true)]
    [InlineData(12543, DataKeys.HostUnreachable, true)]
    [InlineData(12560, DataKeys.HostUnreachable, true)]
    [InlineData(3113, DataKeys.ConnectionFailed, false)]
    [InlineData(3114, DataKeys.ConnectionFailed, false)]
    [InlineData(12170, DataKeys.Timeout, true)]
    [InlineData(12535, DataKeys.Timeout, true)]
    [InlineData(51, DataKeys.Timeout, false)]
    [InlineData(1013, DataKeys.Cancelled, false)]
    [InlineData(1031, DataKeys.PermissionDenied, false)]
    [InlineData(1045, DataKeys.PermissionDenied, true)]
    [InlineData(1456, DataKeys.PermissionDenied, false)]
    [InlineData(29024, DataKeys.CertificateNotTrusted, true)]
    [InlineData(28860, DataKeys.CertificateNotTrusted, true)]
    public void TheOraNumbers_MapToTheStableKeys(int number, string key, bool connecting)
        => Assert.Equal(key, D.Errors.ClassifyNumber(number, connecting));

    [Theory]
    [InlineData(942)]
    [InlineData(904)]
    [InlineData(933)]
    public void ASqlError_FallsBackToTheGenericKey(int number) => Assert.Null(D.Errors.ClassifyNumber(number, false));

    [Fact]
    public async Task ARealDriverException_FromAClosedPort_IsUnpackedAndMapped()
    {
        // No server needed: the real ODP.NET driver raises a real OracleException when nobody listens.
        int port = FreePort();
        var settings = Settings() with { Host = "127.0.0.1", Port = port, Encrypt = false, ConnectTimeoutSeconds = 5 };
        var registry = new ProviderRegistry();
        registry.Register(new OraclePack());
        var source = DatabaseSource.CreateUsing(settings, "pw-secret", registry);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
        Assert.True(ex.Key == DataKeys.HostUnreachable, ex.Key + " | " + ex.Detail);
        Assert.DoesNotContain("pw-secret", ex.Message + ex.Detail);

        // And the exception itself is what Extract understands.
        var oracle = Assert.ThrowsAny<OracleException>(() =>
        {
            using var c = new OracleConnection(D.BuildConnectionString(settings, "pw-secret"));
            c.Open();
        });
        var (number, state) = D.Errors.Extract(new InvalidOperationException("wrapped", oracle));
        Assert.Equal(50201, oracle.Number);
        Assert.Equal(12541, number);
        Assert.Null(state);
        Assert.Equal(DataKeys.HostUnreachable, D.Errors.ClassifyNumber(number, true));
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
