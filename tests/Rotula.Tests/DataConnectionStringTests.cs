using Rotula.Data;

namespace Rotula.Tests;

/// <summary>Connection strings built from structured settings: correctness, secure defaults, injection resistance, redaction.</summary>
public class DataConnectionStringTests
{
    private const string Secret = "S3cr3t;Pwd=evil!";

    private static DatabaseConnectionSettings Sql(Func<DatabaseConnectionSettings, DatabaseConnectionSettings>? tweak = null)
    {
        var s = new DatabaseConnectionSettings
        {
            Name = "prod", Provider = DatabaseProvider.SqlServer, Host = "db.example.com", Database = "Labels",
            Authentication = DatabaseAuthentication.UserPassword, UserName = "reader",
        };
        return tweak is null ? s : tweak(s);
    }

    private static IReadOnlyDictionary<string, string> Parse(string cs) => ConnectionStrings.Parse(cs);

    /// <summary>ODBC strings use {braces} for values containing semicolons, which only the ODBC builder understands.</summary>
    private static IReadOnlyDictionary<string, string> ParseOdbc(string cs)
    {
        var b = new System.Data.Odbc.OdbcConnectionStringBuilder(cs);
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k]) ?? "", StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_DefaultsAreEncryptedAndStrict()
    {
        var kv = Parse(ConnectionStrings.Build(Sql(), Secret));
        Assert.Equal("db.example.com", kv["Data Source"]);
        Assert.Equal("Labels", kv["Initial Catalog"]);
        Assert.Equal("reader", kv["User ID"]);
        Assert.Equal(Secret, kv["Password"]);                 // a ';' inside the password must stay inside the value
        Assert.Equal("True", kv["Encrypt"], ignoreCase: true);
        Assert.Equal("False", kv["Trust Server Certificate"], ignoreCase: true);
        Assert.Equal("10", kv["Connect Timeout"]);
        Assert.Equal("Rotula", kv["Application Name"]);
        Assert.Equal("False", kv["Pooling"], ignoreCase: true);
    }

    [Fact]
    public void SqlServer_WindowsIntegrated_HasNoCredentials_AndPortIsAppended()
    {
        var s = Sql(x => x with { Authentication = DatabaseAuthentication.WindowsIntegrated, UserName = null, Port = 14330 });
        var kv = Parse(ConnectionStrings.Build(s, null));
        Assert.Equal("db.example.com,14330", kv["Data Source"]);
        Assert.Equal("True", kv["Integrated Security"], ignoreCase: true);
        Assert.False(kv.ContainsKey("Password"));
        Assert.False(kv.ContainsKey("User ID"));
    }

    [Fact]
    public void SqlServer_RelaxingTlsIsExplicit()
    {
        var kv = Parse(ConnectionStrings.Build(Sql(x => x with { TrustServerCertificate = true }), "p"));
        Assert.Equal("True", kv["Trust Server Certificate"], ignoreCase: true);
        var off = Parse(ConnectionStrings.Build(Sql(x => x with { Encrypt = false }), "p"));
        Assert.Equal("False", off["Encrypt"], ignoreCase: true);
    }

    [Fact]
    public void PostgreSql_DefaultsRequireVerifiedTls_ReadOnlySession()
    {
        var s = Sql(x => x with { Provider = DatabaseProvider.PostgreSql, Port = 5433 });
        var kv = Parse(ConnectionStrings.Build(s, "pw"));
        Assert.Equal("db.example.com", kv["Host"]);
        Assert.Equal("5433", kv["Port"]);
        Assert.Equal("VerifyFull", kv["SSL Mode"]);
        Assert.Contains("default_transaction_read_only=on", kv["Options"]);
        Assert.Equal("reader", kv["Username"]);

        var trust = Parse(ConnectionStrings.Build(s with { TrustServerCertificate = true }, "pw"));
        Assert.Equal("Require", trust["SSL Mode"]);
        var plain = Parse(ConnectionStrings.Build(s with { Encrypt = false }, "pw"));
        Assert.Equal("Prefer", plain["SSL Mode"]);
    }

    [Fact]
    public void MySql_DefaultsRequireVerifiedTls_AndDisableRiskyFeatures()
    {
        var s = Sql(x => x with { Provider = DatabaseProvider.MySql });
        var kv = Parse(ConnectionStrings.Build(s, "pw"));
        Assert.Equal("VerifyFull", kv["SSL Mode"]);
        Assert.Equal("False", kv["Allow User Variables"], ignoreCase: true);
        Assert.Equal("False", kv["Allow Load Local Infile"], ignoreCase: true);
        Assert.Equal("30", kv["Default Command Timeout"]);
        Assert.Equal("Required", Parse(ConnectionStrings.Build(s with { TrustServerCertificate = true }, "pw"))["SSL Mode"]);
    }

    [Fact]
    public void MySql_RejectsWindowsAuthentication()
    {
        var s = Sql(x => x with { Provider = DatabaseProvider.MySql, Authentication = DatabaseAuthentication.WindowsIntegrated });
        var ex = Assert.Throws<DatabaseException>(() => ConnectionStrings.Build(s, null));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
        Assert.Equal("Authentication", ex.Args[0]);
    }

    [Fact]
    public void Sqlite_IsAlwaysReadOnly_AndPathIsValidated()
    {
        var s = new DatabaseConnectionSettings { Name = "f", Provider = DatabaseProvider.Sqlite, FilePath = @"C:\data\labels.db" };
        var kv = Parse(ConnectionStrings.Build(s, null));
        Assert.Equal("ReadOnly", kv["Mode"]);
        Assert.Equal(@"C:\data\labels.db", kv["Data Source"]);
        foreach (string bad in new[] { "file:x.db?mode=rwc", ":memory:", "x.db?vfs=y", "", "  " })
            Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => ConnectionStrings.Build(s with { FilePath = bad }, null)).Key);
    }

    [Theory]
    [InlineData("Host", "evil.com;Password=x")]
    [InlineData("Host", "evil.com;Encrypt=false")]
    [InlineData("Host", "a b")]
    [InlineData("Host", "evil'.com")]
    [InlineData("Host", "evil=1")]
    [InlineData("Host", "")]
    [InlineData("Database", "db;Trust Server Certificate=true")]
    [InlineData("Database", "db\nx")]
    [InlineData("Database", "a=b")]
    [InlineData("UserName", "sa;Server=evil")]
    [InlineData("UserName", "sa\0")]
    [InlineData("UserName", "x{y}")]
    public void InjectionAttempts_AreRejected_OnEveryNetworkProvider(string field, string value)
    {
        foreach (var provider in new[] { DatabaseProvider.SqlServer, DatabaseProvider.PostgreSql, DatabaseProvider.MySql })
        {
            var s = Sql(x => x with { Provider = provider });
            s = field switch
            {
                "Host" => s with { Host = value },
                "Database" => s with { Database = value },
                _ => s with { UserName = value },
            };
            var ex = Assert.Throws<DatabaseException>(() => ConnectionStrings.Build(s, "pw"));
            Assert.Equal(DataKeys.InvalidSettings, ex.Key);
            Assert.Equal(field, ex.Args[0]);
        }
    }

    [Theory]
    [InlineData("p;Server=evil")]
    [InlineData("p\";Server=evil;x=\"")]
    [InlineData("p'; Server='evil")]
    [InlineData("a=b=c")]
    [InlineData("  spaces  ")]
    [InlineData("{brace}")]
    public void PasswordsAreQuotedByTheBuilder_NeverInjected(string password)
    {
        foreach (var provider in new[] { DatabaseProvider.SqlServer, DatabaseProvider.PostgreSql, DatabaseProvider.MySql })
        {
            var s = Sql(x => x with { Provider = provider });
            var kv = Parse(ConnectionStrings.Build(s, password));
            string hostKey = provider switch { DatabaseProvider.SqlServer => "Data Source", DatabaseProvider.PostgreSql => "Host", _ => "Server" };
            Assert.Equal("db.example.com", kv[hostKey]);
            Assert.False(kv.ContainsKey("evil"));
            Assert.DoesNotContain(kv.Values, v => v == "evil");
            string pwdKey = kv.ContainsKey("Password") ? "Password" : "Pwd";
            Assert.Equal(password, kv[pwdKey]);
        }
    }

    [Fact]
    public void NameAndTimeoutsAndPortAreValidated()
    {
        Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(Sql(x => x with { Name = " " }))).Key);
        Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(Sql(x => x with { CommandTimeoutSeconds = 0 })));
        Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(Sql(x => x with { ConnectTimeoutSeconds = 301 })));
        Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(Sql(x => x with { Port = 70000 })));
        ConnectionStrings.Validate(Sql());
    }

    [Fact]
    public void SqlServerInstanceAndLocalDbHostsAreAccepted()
    {
        ConnectionStrings.Validate(Sql(x => x with { Host = @"(localdb)\MSSQLLocalDB" }));
        ConnectionStrings.Validate(Sql(x => x with { Host = @"SERVER01\SQLEXPRESS" }));
        ConnectionStrings.Validate(Sql(x => x with { Host = "[::1]" }));
        var kv = Parse(ConnectionStrings.Build(Sql(x => x with { Host = @"SERVER01\SQLEXPRESS", Port = 1433 }), "p"));
        Assert.Equal(@"SERVER01\SQLEXPRESS", kv["Data Source"]); // instance names resolve via SQL Browser, a port would conflict
    }

    // ---- ODBC -------------------------------------------------------------------------------------------

    [Fact]
    public void Odbc_RawString_IsAcceptedAsTypedAndPasswordAppendedOnlyWhenMissing()
    {
        var s = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, OdbcConnectionString = "Driver={ODBC Driver 17 for SQL Server};Server=x;Database=y;Uid=u" };
        var kv = Parse(ConnectionStrings.Build(s, "pw"));
        Assert.Equal("pw", kv["Pwd"]);
        Assert.Equal("u", kv["Uid"]);

        var withPwd = s with { OdbcConnectionString = s.OdbcConnectionString + ";Pwd=typed" };
        Assert.Equal("typed", Parse(ConnectionStrings.Build(withPwd, "other"))["Pwd"]);
    }

    [Fact]
    public void Odbc_Dsn_BuildsFromStructuredFields()
    {
        var s = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, Dsn = "LabelsDsn", Authentication = DatabaseAuthentication.UserPassword, UserName = "u" };
        var kv = ParseOdbc(ConnectionStrings.Build(s, "pw;x=1"));
        Assert.Equal("LabelsDsn", kv["Dsn"]);
        Assert.Equal("{pw;x=1}", kv["Pwd"]); // the ODBC builder brace-quotes values containing ;
        Assert.Equal("Yes", ParseOdbc(ConnectionStrings.Build(s with { Authentication = DatabaseAuthentication.WindowsIntegrated }, null))["Trusted_Connection"]);
        Assert.Throws<DatabaseException>(() => ConnectionStrings.Build(s with { Dsn = "a;Pwd=b" }, null));
    }

    [Theory]
    [InlineData("Driver={X};SAVEFILE=C:\\x.dsn")]
    [InlineData("FILEDSN=C:\\x.dsn")]
    [InlineData("")]
    public void Odbc_DangerousOrEmptyRawStrings_AreRejected(string raw)
    {
        var s = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, OdbcConnectionString = raw };
        Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => ConnectionStrings.Build(s, null)).Key);
    }

    // ---- redaction --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Server=x;Password=hunter2;Database=y", "Server=x;Password=***;Database=y")]
    [InlineData("Server=x;PWD=hunter2", "Server=x;PWD=***")]
    [InlineData("Server=x;pwd = 'hun;ter2';Database=y", "Server=x;pwd = ***;Database=y")]
    [InlineData("Server=x;Password=\"hun;ter2\";Database=y", "Server=x;Password=***;Database=y")]
    [InlineData("Driver={X};PWD={a;b}}c};Uid=u", "Driver={X};PWD=***;Uid=u")]
    [InlineData("Server=x;UserPassword=keep", "Server=x;UserPassword=keep")]
    public void RedactConnectionString_MasksSecrets(string input, string expected)
        => Assert.Equal(expected, SecretRedactor.RedactConnectionString(input));

    [Fact]
    public void RedactText_RemovesKnownPasswordAndConnectionString()
    {
        string cs = ConnectionStrings.Build(Sql(), Secret);
        string msg = $"Login failed for user 'reader' using {cs} (password {Secret})";
        string clean = SecretRedactor.RedactText(msg, Secret, cs);
        Assert.DoesNotContain(Secret, clean);
        Assert.DoesNotContain("S3cr3t", clean);
        Assert.DoesNotContain("db.example.com", clean);
        Assert.Contains("Login failed", clean);
    }

    [Fact]
    public void ExtractPassword_SplitsOutPwd()
    {
        var (clean, pw) = SecretRedactor.ExtractPassword("Driver={X};Server=a;PWD={p;w};Uid=u");
        Assert.Equal("p;w", pw);
        Assert.DoesNotContain("PWD", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Uid=u", clean);
        Assert.Null(SecretRedactor.ExtractPassword("Dsn=x").Password);
    }

    [Fact]
    public void Settings_ToString_NeverContainsSecrets()
    {
        var s = new DatabaseConnectionSettings
        {
            Name = "o", Provider = DatabaseProvider.Odbc, OdbcConnectionString = "Driver={X};Server=s;Uid=u;Pwd=topsecret",
        };
        Assert.DoesNotContain("topsecret", s.ToString());
        Assert.DoesNotContain("topsecret", s.Describe());
        Assert.Contains("Pwd=***", s.ToString());
    }

    [Fact]
    public void RedactedConnectionStringHelper_HidesThePassword()
    {
        string shown = ConnectionStrings.BuildRedacted(Sql(), Secret);
        Assert.DoesNotContain("S3cr3t", shown);
        Assert.Contains("db.example.com", shown);
    }

    [Fact]
    public void DatabaseQueryReference_CanOnlyHoldANameAndAQuery()
    {
        // A .rotula document stores this type only; there is no member that could carry credentials.
        var props = typeof(DatabaseQueryReference).GetProperties().Select(p => (p.Name, p.PropertyType)).OrderBy(p => p.Name).ToArray();
        Assert.Equal(new[] { ("ConnectionName", typeof(string)), ("Query", typeof(string)) }, props);
    }

    [Fact]
    public void ConnectionSettings_HasNoPasswordMember()
    {
        Assert.DoesNotContain(typeof(DatabaseConnectionSettings).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) && p.Name != nameof(DatabaseConnectionSettings.OdbcConnectionString));
    }
}
