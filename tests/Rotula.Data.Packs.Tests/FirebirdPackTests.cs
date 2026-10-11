using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Rotula.Data;
using Rotula.Data.Pack.Firebird;

namespace Rotula.Data.Packs.Tests;

/// <summary>The Firebird pack without a server: schema, connection string, validation, guard rules, error mapping.</summary>
public class FirebirdPackTests
{
    private static readonly FirebirdDescriptor D = new();

    private static DatabaseConnectionSettings Settings() => new()
    {
        Name = "fb", Provider = DatabaseProvider.Custom, ProviderId = "firebird", Host = "db.example.com", Database = @"C:\data\sales.fdb",
        Authentication = DatabaseAuthentication.UserPassword, UserName = "reader",
    };

    private static Dictionary<string, string> Parse(string cs)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k], CultureInfo.InvariantCulture)!, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- schema

    [Fact]
    public void TheSchema_AsksForServerPortDatabaseAndSignIn()
    {
        Assert.Equal(new[] { "host", "port", "database", "userName", "encrypt", "role" }, D.Options.Select(o => o.Key));
        Assert.Equal(3050, D.DefaultPort);
        Assert.Equal(ProviderCategory.Server, D.Category);
        Assert.Equal(new[] { DatabaseAuthentication.UserPassword, DatabaseAuthentication.WindowsIntegrated }, D.SupportedAuthentication);
        Assert.True(D.Options.Single(o => o.Key == "database").Required);
        Assert.Equal("true", D.Options.Single(o => o.Key == "encrypt").DefaultValue);
        Assert.Same(SqlDialects.Firebird, D.Dialect);
        Assert.True(D.RequiresServer);
        Assert.All(D.Options, o => Assert.StartsWith("Data.Option.", o.LabelKey));
    }

    [Fact]
    public void TheReadOnlyPolicy_NamesTheReadOnlyTransaction_AndAdvisesASelectOnlyRole()
    {
        Assert.True(D.ReadOnly.StartTransaction);
        Assert.Contains("READ ONLY", D.ReadOnly.Description);
        Assert.Contains("SELECT", D.ReadOnly.Description);
    }

    // ---------------------------------------------------------------- connection string

    [Fact]
    public void TheConnectionString_IsBuiltByTheDriverBuilder_WithUtf8_NoPooling_AndRequiredWireEncryption()
    {
        var p = Parse(D.BuildConnectionString(Settings(), "pw"));
        Assert.Equal("db.example.com", p["Data Source"]);
        Assert.Equal("3050", p["Port Number"]);
        Assert.Equal(@"C:\data\sales.fdb", p["Initial Catalog"]);
        Assert.Equal("reader", p["User ID"]);
        Assert.Equal("pw", p["Password"]);
        Assert.Equal("UTF8", p["Character Set"]);
        Assert.Equal("False", p["Pooling"]);
        Assert.Equal("Required", p["Wire Crypt"]);
        Assert.Equal("10", p["Connection Timeout"]);
        Assert.Equal("30", p["Command Timeout"]);
    }

    [Fact]
    public void Encrypt_Off_AsksForEncryptionOnlyWhenTheServerOffersIt()
    {
        Assert.Equal("Enabled", Parse(D.BuildConnectionString(Settings() with { Encrypt = false }, "pw"))["Wire Crypt"]);
    }

    [Fact]
    public void WindowsSignIn_SendsNoUserOrPassword_AndDoesNotDemandEncryption()
    {
        var s = Settings() with { Authentication = DatabaseAuthentication.WindowsIntegrated, UserName = null };
        var p = Parse(D.BuildConnectionString(s, "ignored"));
        Assert.False(p.ContainsKey("User ID"));
        Assert.False(p.ContainsKey("Password"));
        Assert.Equal("Enabled", p["Wire Crypt"]);
    }

    [Fact]
    public void ARole_PortAndTimeouts_AreCarried()
    {
        var s = Settings() with { Port = 3051, ConnectTimeoutSeconds = 7, CommandTimeoutSeconds = 99, Extras = ProviderExtras.Of(("role", "READER")) };
        var p = Parse(D.BuildConnectionString(s, "pw"));
        Assert.Equal("3051", p["Port Number"]);
        Assert.Equal("READER", p["Role Name"]);
        Assert.Equal("7", p["Connection Timeout"]);
        Assert.Equal("99", p["Command Timeout"]);
    }

    [Theory]
    [InlineData("pw;Wire Crypt=Disabled")]
    [InlineData("pw;User ID=SYSDBA")]
    [InlineData("a'b\"c;d=e")]
    public void AHostilePassword_IsOneValue_NeverAnExtraKeyword(string password)
    {
        var p = Parse(D.BuildConnectionString(Settings(), password));
        Assert.Equal(password, p["Password"]);
        Assert.Equal("Required", p["Wire Crypt"]);
        Assert.Equal("reader", p["User ID"]);
    }

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("host;Wire Crypt=Disabled")]
    [InlineData("host name")]
    [InlineData("host=x")]
    public void ABadHost_IsRefused(string? host)
    {
        var ex = Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Host = host }));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    public void ABadDatabase_IsRefused(string? database)
        => Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { Database = database }));

    [Fact]
    public void AUserNameIsRequired_ForPasswordSignIn()
        => Assert.Throws<DatabaseException>(() => D.Validate(Settings() with { UserName = null }));

    [Theory]
    [InlineData("READER", true)]
    [InlineData("rea\"der", false)]
    [InlineData("rea'der", false)]
    [InlineData("a;b", false)]
    public void TheRole_IsValidated(string role, bool ok)
    {
        var s = Settings() with { Extras = ProviderExtras.Of(("role", role)) };
        if (ok) D.Validate(s); else Assert.Throws<DatabaseException>(() => D.Validate(s));
    }

    [Fact]
    public void ValidSettings_PassTheSharedValidation_ThroughTheRegistry()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FirebirdPack());
        DatabaseSource.CreateUsing(Settings(), "pw", registry);
        // An option that is not in the schema cannot be smuggled in through the extras.
        Assert.Throws<DatabaseException>(() => DatabaseSource.CreateUsing(Settings() with { Extras = ProviderExtras.Of(("wireCrypt", "Disabled")) }, "pw", registry));
    }

    // ---------------------------------------------------------------- identifiers, parameters, catalogue

    [Fact]
    public void Identifiers_AreQuotedWithDoubleQuotes_AndTheClosingQuoteIsDoubled()
    {
        Assert.Equal("\"ORDERS\"", D.Dialect.QuoteIdentifier("ORDERS"));
        Assert.Equal("\"we\"\"ird\"", D.Dialect.QuoteIdentifier("we\"ird"));
        // Firebird has no backslash escape: a name ending in a backslash stays inside its quotes.
        Assert.Equal("\"a\\\"", D.Dialect.QuoteIdentifier("a\\"));
    }

    [Fact]
    public void Parameters_KeepTheAtName_AndTheRowLimitIsFirst()
    {
        Assert.Equal(ParameterStyle.AtName, D.Dialect.ParameterStyle);
        Assert.Equal("@id", ((IProviderDescriptor)D).ParameterName("id"));
        Assert.Equal("SELECT FIRST 5 * FROM T", D.Dialect.ApplyRowLimit("SELECT * FROM T", 5));
    }

    [Fact]
    public void TheCatalogueSql_ListsUserTablesAndViews_FromTheSystemTables()
    {
        string sql = D.Dialect.CatalogueSql!;
        Assert.Contains("RDB$RELATIONS", sql);
        Assert.Contains("RDB$SYSTEM_FLAG", sql);
        Assert.Contains("RDB$VIEW_SOURCE", sql);
        Assert.True(SqlGuard.Check(sql, D.Dialect).IsAllowed);
    }

    // ---------------------------------------------------------------- guard (Firebird 5.0 Language Reference)

    [Theory]
    [InlineData("SELECT FIRST 10 SKIP 5 * FROM T")]
    [InlineData("SELECT * FROM T ROWS 10")]
    [InlineData("SELECT * FROM T OFFSET 5 ROWS FETCH FIRST 10 ROWS ONLY")]
    [InlineData("SELECT q'{it's}' FROM RDB$DATABASE")]
    [InlineData("SELECT Q'[a]b]' FROM RDB$DATABASE")]
    [InlineData("SELECT 'it''s' FROM RDB$DATABASE")]
    [InlineData("SELECT \"a\"\"b\" FROM T")]
    [InlineData("SELECT 1 FROM RDB$DATABASE -- comment")]
    [InlineData("SELECT /* a */ 1 FROM RDB$DATABASE")]
    [InlineData("SELECT RDB$GET_CONTEXT('SYSTEM','ENGINE_VERSION') FROM RDB$DATABASE")]
    [InlineData("WITH X AS (SELECT 1 A FROM RDB$DATABASE) SELECT * FROM X")]
    [InlineData("SELECT * FROM SOME_VIEW WHERE A = @a")]
    public void ReadingStatements_AreAccepted(string sql) => Assert.True(SqlGuard.Check(sql, D.Dialect).IsAllowed, sql);

    [Theory]
    [InlineData("SELECT GEN_ID(G, 1) FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT gen_id(g, 0) FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT NEXT VALUE FOR G FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT RDB$SET_CONTEXT('USER_SESSION','K','V') FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction)]
    [InlineData("SELECT * FROM T WITH LOCK", DataKeys.RejectLocking)]
    [InlineData("SELECT * FROM T FOR UPDATE", DataKeys.RejectLocking)]
    [InlineData("SELECT * FROM T FOR UPDATE WITH LOCK", DataKeys.RejectLocking)]
    [InlineData("EXECUTE BLOCK AS BEGIN END", DataKeys.RejectNotSelect)]
    [InlineData("SELECT 1 FROM RDB$DATABASE; DELETE FROM T", DataKeys.RejectMultipleStatements)]
    [InlineData("INSERT INTO T VALUES (1)", DataKeys.RejectNotSelect)]
    [InlineData("UPDATE OR INSERT INTO T VALUES (1) MATCHING (A)", DataKeys.RejectNotSelect)]
    [InlineData("SELECT * FROM T INTO :X", DataKeys.RejectSelectInto)]
    [InlineData("SET GENERATOR G TO 0", DataKeys.RejectNotSelect)]
    [InlineData("COMMIT", DataKeys.RejectNotSelect)]
    public void WritingOrStateChangingStatements_AreRefused(string sql, string reason)
    {
        var r = SqlGuard.Check(sql, D.Dialect);
        Assert.False(r.IsAllowed, sql);
        Assert.Equal(reason, r.ReasonKey);
    }

    [Fact]
    public void ABlockCommentDoesNotNest_InFirebird()
    {
        // Firebird closes /* */ at the first */ (no nesting): the DROP after it is a second statement and must be refused.
        var r = SqlGuard.Check("SELECT 1 FROM RDB$DATABASE /* a /* b */ ; DROP TABLE T; /* c */", D.Dialect);
        Assert.False(r.IsAllowed);
        Assert.Equal(DataKeys.RejectMultipleStatements, r.ReasonKey);
    }

    [Fact]
    public void AQQuote_HidesNothing()
    {
        // Inside a q-quote the semicolon is text; after it, it is not.
        Assert.True(SqlGuard.Check("SELECT q'{; DROP TABLE T;}' FROM RDB$DATABASE", D.Dialect).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT q'{x}' FROM RDB$DATABASE; DROP TABLE T", D.Dialect).IsAllowed);
    }

    // ---------------------------------------------------------------- error mapping

    [Theory]
    [InlineData(335544472, DataKeys.AuthenticationFailed, true)]
    [InlineData(335545106, DataKeys.AuthenticationFailed, true)]
    [InlineData(335544344, DataKeys.DatabaseNotFound, true)]
    [InlineData(335544352, DataKeys.PermissionDenied, false)]
    [InlineData(335544721, DataKeys.HostUnreachable, true)]
    [InlineData(335544721, DataKeys.ConnectionFailed, false)]
    [InlineData(335544421, DataKeys.HostUnreachable, true)]
    [InlineData(335544741, DataKeys.ConnectionFailed, false)]
    [InlineData(335544510, DataKeys.Timeout, false)]
    [InlineData(335545127, DataKeys.Timeout, false)]
    [InlineData(335545129, DataKeys.Timeout, false)]
    [InlineData(335544831, DataKeys.PermissionDenied, true)]
    [InlineData(335544794, DataKeys.Cancelled, false)]
    public void TheIscCodes_MapToTheStableKeys(int code, string key, bool connecting)
        => Assert.Equal(key, D.Errors.ClassifyNumber(code, connecting));

    [Fact]
    public void AnUnknownCode_FallsBackToTheGenericKey()
        => Assert.Null(D.Errors.ClassifyNumber(335544569, false));

    [Fact]
    public async Task ARealDriverException_FromAClosedPort_IsHostUnreachable()
    {
        // No server needed: a real FbException comes from connecting to a port nobody listens on.
        int port = FreePort();
        var settings = Settings() with { Host = "127.0.0.1", Port = port, ConnectTimeoutSeconds = 3, Encrypt = false };
        var registry = new ProviderRegistry();
        registry.Register(new FirebirdPack());
        var source = DatabaseSource.CreateUsing(settings, "pw", registry);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
        Assert.Equal(DataKeys.HostUnreachable, ex.Key);
        Assert.DoesNotContain("pw", ex.Message);
        Assert.DoesNotContain("pw", ex.Detail ?? string.Empty);
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
