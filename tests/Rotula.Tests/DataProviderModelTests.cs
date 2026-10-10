using Rotula.Data;

namespace Rotula.Tests;

/// <summary>The provider model: stable ids, the registry, descriptors, settings identity and the option schema.</summary>
public class DataProviderModelTests
{
    private static DatabaseConnectionSettings Pg(string host = "db.example.com") => new()
    {
        Name = "pg", Provider = DatabaseProvider.PostgreSql, Host = host, Database = "d",
        Authentication = DatabaseAuthentication.UserPassword, UserName = "u",
    };

    [Fact]
    public void TheFiveOriginalProviders_KeepStableIds_AndEnumValues()
    {
        Assert.Equal("sqlserver", ProviderIds.For(DatabaseProvider.SqlServer));
        Assert.Equal("postgresql", ProviderIds.For(DatabaseProvider.PostgreSql));
        Assert.Equal("mysql", ProviderIds.For(DatabaseProvider.MySql));
        Assert.Equal("sqlite", ProviderIds.For(DatabaseProvider.Sqlite));
        Assert.Equal("odbc", ProviderIds.For(DatabaseProvider.Odbc));
        // The enum values are persisted by name and must never move; Custom is appended after them.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[]
        {
            (int)DatabaseProvider.SqlServer, (int)DatabaseProvider.PostgreSql, (int)DatabaseProvider.MySql,
            (int)DatabaseProvider.Sqlite, (int)DatabaseProvider.Odbc, (int)DatabaseProvider.Custom,
        });
        foreach (var p in new[] { DatabaseProvider.SqlServer, DatabaseProvider.PostgreSql, DatabaseProvider.MySql, DatabaseProvider.Sqlite, DatabaseProvider.Odbc })
            Assert.Equal(p, ProviderIds.ToEnum(ProviderIds.For(p)));
        Assert.Equal(DatabaseProvider.Custom, ProviderIds.ToEnum("oracle"));
    }

    [Fact]
    public void TheDefaultRegistry_ListsTheFiveBuiltInProviders_FirstAndInOrder()
    {
        var entries = ProviderRegistry.Default.GetDisplayEntries();
        Assert.Equal(new[] { "sqlserver", "postgresql", "mysql", "sqlite", "odbc" }, entries.Take(5).Select(e => e.Id));
        Assert.All(entries.Take(5), e => { Assert.Equal(ProviderEntryKind.Provider, e.Kind); Assert.True(e.IsBuiltIn); Assert.Equal("builtin", e.PackId); });
        Assert.Equal("Conn.Provider.SqlServer", entries[0].DisplayNameKey);
        Assert.Contains(entries, e => e.Kind == ProviderEntryKind.Preset && e.Id == "supabase" && e.ProviderId == "postgresql");
        Assert.Equal(1433, entries[0].DefaultPort);
        Assert.Null(entries[3].DefaultPort);
    }

    [Fact]
    public void EveryBuiltInDescriptor_DescribesItself_WithValidOptionSchema()
    {
        foreach (var d in ProviderRegistry.Default.Providers)
        {
            Assert.True(ProviderRegistry.IsValidId(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
            Assert.StartsWith("Conn.Provider.", d.DisplayNameKey);
            Assert.NotNull(d.Dialect);
            Assert.NotEmpty(d.Options);
            Assert.Equal(d.Options.Count, d.Options.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var o in d.Options)
            {
                Assert.StartsWith("Data.Option.", o.LabelKey);
                if (o.Kind == ProviderOptionKind.Choice) Assert.NotEmpty(o.Choices);
            }
        }
        Assert.False(ProviderRegistry.Default.Get("sqlite").RequiresServer);
        Assert.True(ProviderRegistry.Default.Get("postgresql").RequiresServer);
        Assert.True(ProviderRegistry.Default.Get("sqlserver").ReadOnly.GuardIsPrimaryControl);
        Assert.Equal(new[] { "SET TRANSACTION READ ONLY" }, ProviderRegistry.Default.Get("postgresql").ReadOnly.TransactionStatements);
        Assert.Equal(new[] { "SET SESSION TRANSACTION READ ONLY" }, ProviderRegistry.Default.Get("mysql").ReadOnly.SessionStatements);
        Assert.False(ProviderRegistry.Default.Get("sqlite").ReadOnly.StartTransaction);
        Assert.True(ProviderRegistry.Default.Get("odbc").ReadOnly.TransactionIsOptional);
    }

    [Fact]
    public void Resolve_UsesTheEnum_OrTheStringId_AndFailsPlainlyForAMissingPack()
    {
        Assert.Equal("postgresql", ProviderRegistry.Default.Resolve(Pg()).Id);
        var custom = new DatabaseConnectionSettings { Name = "x", Provider = DatabaseProvider.Custom, ProviderId = "oracle", Host = "h" };
        var ex = Assert.Throws<DatabaseException>(() => ProviderRegistry.Default.Resolve(custom));
        Assert.Equal(ProviderKeys.ProviderUnavailable, ex.Key);
        Assert.Equal("oracle", Assert.Single(ex.Args));
        Assert.Throws<DatabaseException>(() => DatabaseSource.Create(custom));
    }

    [Fact]
    public void SettingsIdentity_IsValidated()
    {
        static void Invalid(DatabaseConnectionSettings s, string field)
        {
            var ex = Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(s));
            Assert.Equal(DataKeys.InvalidSettings, ex.Key);
            Assert.Equal(field, ex.Args[0]);
        }
        Invalid(Pg() with { Provider = DatabaseProvider.Custom }, "ProviderId");                    // Custom needs an id
        Invalid(Pg() with { ProviderId = "mysql" }, "ProviderId");                                  // id and enum must agree
        Invalid(Pg() with { ProviderId = "Not Valid!" }, "ProviderId");
        Invalid(Pg() with { PresetId = "NO PE" }, "PresetId");
        Invalid(Pg() with { CaCertificatePath = @"\\evil\share\ca.pem" }, "CaCertificatePath");     // UNC would leak credentials
        Invalid(Pg() with { CaCertificatePath = "https://evil/ca.pem" }, "CaCertificatePath");
        Invalid(new DatabaseConnectionSettings { Name = "s", Provider = DatabaseProvider.SqlServer, Host = "h", CaCertificatePath = "C:\\ca.pem" }, "CaCertificatePath"); // SQL Server has no CA option
        ConnectionStrings.Validate(Pg() with { ProviderId = "postgresql" });                        // an explicit built-in id is fine
        ConnectionStrings.Validate(Pg() with { CaCertificatePath = "C:\\certs\\ca.pem", PresetId = "supabase" });
    }

    [Fact]
    public void Extras_MustBeDeclaredOptions_WithFittingValues()
    {
        static DatabaseException Fail(DatabaseConnectionSettings s) => Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(s));
        Assert.Equal("Extras.bogus", Fail(Pg() with { Extras = ProviderExtras.Of(("bogus", "1")) }).Args[0]);
        Assert.Equal("Extras.noStartupOptions", Fail(Pg() with { Extras = ProviderExtras.Of(("noStartupOptions", "maybe")) }).Args[0]);
        Assert.Equal("Extras.flavor", Fail(new DatabaseConnectionSettings { Name = "m", Provider = DatabaseProvider.MySql, Host = "h", UserName = "u", Extras = ProviderExtras.Of(("flavor", "oracle")) }).Args[0]);
        // A standard key is not an extra (it has its own property), so it cannot smuggle a second value in.
        Assert.Equal("Extras.host", Fail(Pg() with { Extras = ProviderExtras.Of(("host", "evil")) }).Args[0]);
        ConnectionStrings.Validate(Pg() with { Extras = ProviderExtras.Of(("noStartupOptions", "true")) });
        Assert.Equal(DataKeys.InvalidSettings, Fail(Pg() with { Extras = new ProviderExtras(Enumerable.Range(0, 40).Select(i => new KeyValuePair<string, string>("k" + i, "v"))) }).Key);
    }

    [Fact]
    public void ProviderExtras_HaveValueEquality_AndKeepSettingsComparable()
    {
        var a = ProviderExtras.Of(("A", "1"), ("b", "2"));
        var b = ProviderExtras.Of(("a", "1"), ("B", "2"));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a.With("c", "3"));
        Assert.Equal(a, a.With("c", "3").With("c", null));
        Assert.Equal(Pg() with { Extras = a }, Pg() with { Extras = b });
        Assert.True(ProviderExtras.Of(("x", "True")).GetBool("x"));
        Assert.False(ProviderExtras.Empty.GetBool("x"));
        Assert.DoesNotContain("pw", (Pg() with { Extras = a }).ToString());
    }

    [Fact]
    public void OptionKeys_ReadAndWriteTheSettingsProperties()
    {
        var s = ProviderOptionKeys.WithValue(Pg(), ProviderOptionKeys.Port, "6543");
        Assert.Equal(6543, s.Port);
        Assert.Equal("6543", ProviderOptionKeys.GetValue(s, "port"));
        s = ProviderOptionKeys.WithValue(s, "noStartupOptions", "true");
        Assert.Equal("true", s.Extras.Get("noStartupOptions"));
        s = ProviderOptionKeys.WithValue(s, ProviderOptionKeys.TrustServerCertificate, "true");
        Assert.True(s.TrustServerCertificate);
        s = ProviderOptionKeys.WithValue(s, ProviderOptionKeys.CaCertificatePath, "C:\\ca.pem");
        Assert.Equal("C:\\ca.pem", ProviderOptionKeys.GetValue(s, "caCertificatePath"));
        Assert.Null(ProviderOptionKeys.GetValue(ProviderOptionKeys.WithValue(s, "noStartupOptions", null), "noStartupOptions"));
    }

    // ---------------------------------------------------------------- registration

    private sealed class FakePack : IDatabaseProviderPack
    {
        private readonly string[] _ids;
        public FakePack(string id, params string[] providerIds) { PackId = id; _ids = providerIds; }
        public string PackId { get; }
        public IReadOnlyList<IProviderDescriptor> GetProviders() => _ids.Select(i => (IProviderDescriptor)new FakeDescriptor(i)).ToList();
    }

    private sealed class FakeDescriptor : IProviderDescriptor, IProviderErrorMapper
    {
        public FakeDescriptor(string id) => Id = id;
        public string Id { get; }
        public string DisplayNameKey => "Fake.Name";
        public string DisplayName => "Fake";
        public ProviderCategory Category => ProviderCategory.Document;
        public int? DefaultPort => 1;
        public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = Array.Empty<DatabaseAuthentication>();
        public IReadOnlyList<ProviderOption> Options { get; } = new[] { StandardOptions.Host() };
        public bool RequiresServer => true;
        public SqlDialect Dialect => SqlDialects.PostgreSql;
        public ReadOnlyPolicy ReadOnly { get; } = new();
        public IProviderErrorMapper Errors => this;
        public (int? Number, string? SqlState) Extract(Exception ex) => (null, null);
        public string? ClassifyNumber(int? number, bool connecting) => null;
        public void Validate(DatabaseConnectionSettings settings) => ProviderValidation.RequireHost(settings.Host);
        public string BuildConnectionString(DatabaseConnectionSettings settings, string? password) => "x";
        public System.Data.Common.DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => throw new NotSupportedException();
    }

    [Fact]
    public void Registering_AddsProviders_AndShowsThemInTheDisplayList()
    {
        var r = new ProviderRegistry();
        r.Register(new FakePack("fake", "fake-db"));
        Assert.Equal("fake-db", r.Get("FAKE-DB").Id);
        Assert.Contains("fake", r.PackIds);
        var entry = r.GetDisplayEntries().Single(e => e.Id == "fake-db");
        Assert.Equal("fake", entry.PackId);
        Assert.False(entry.IsBuiltIn);
        Assert.Equal(6, r.Providers.Count);
        Assert.Equal("fake-db", r.Resolve(new DatabaseConnectionSettings { Name = "n", Provider = DatabaseProvider.Custom, ProviderId = "fake-db", Host = "h" }).Id);
    }

    [Fact]
    public void ACorePackIdCannotBeTaken_AndARejectedPackLeavesNothingBehind()
    {
        var r = new ProviderRegistry();
        var ex = Assert.Throws<ProviderRegistrationException>(() => r.Register(new FakePack("evil", "good-one", "sqlserver")));
        Assert.Equal(PackKeys.DuplicateProvider, ex.Key);
        Assert.Equal("sqlserver", ex.Argument);
        Assert.Null(r.TryGet("good-one"));            // atomic: the valid first provider was not kept
        Assert.DoesNotContain("evil", r.PackIds);
        Assert.Equal(5, r.Providers.Count);
        Assert.Equal(PackKeys.DuplicatePack, Assert.Throws<ProviderRegistrationException>(() => { r.Register(new FakePack("p", "p1")); r.Register(new FakePack("P", "p2")); }).Key);
        Assert.Equal(PackKeys.InvalidProviderId, Assert.Throws<ProviderRegistrationException>(() => r.Register(new FakePack("q", "Bad Id"))).Key);
        Assert.Equal(PackKeys.DuplicateProvider, Assert.Throws<ProviderRegistrationException>(() => r.Register(new FakePack("r", "dup", "dup"))).Key);
    }

    [Fact]
    public void ValidIds_AreLowerCaseLettersDigitsAndDashes()
    {
        Assert.True(ProviderRegistry.IsValidId("oracle-23"));
        Assert.False(ProviderRegistry.IsValidId("Oracle"));
        Assert.False(ProviderRegistry.IsValidId("-x"));
        Assert.False(ProviderRegistry.IsValidId(""));
        Assert.False(ProviderRegistry.IsValidId(new string('a', 41)));
        Assert.False(ProviderRegistry.IsValidId("a_b"));
    }

    // ---------------------------------------------------------------- the built-in builders keep their special cases

    [Fact]
    public void PostgreSql_SendsTheReadOnlyStartupOption_ExceptForPoolersAndWhenAskedNot()
    {
        string direct = ConnectionStrings.Build(Pg("db.abc.supabase.co"), "pw");
        Assert.Contains("default_transaction_read_only", direct);
        string pooler = ConnectionStrings.Build(Pg("ep-cool-1-pooler.eu-central-1.aws.neon.tech"), "pw");
        Assert.DoesNotContain("default_transaction_read_only", pooler);
        string asked = ConnectionStrings.Build(Pg() with { Extras = ProviderExtras.Of(("noStartupOptions", "true")) }, "pw");
        Assert.DoesNotContain("default_transaction_read_only", asked);
    }

    [Fact]
    public void ACaCertificate_IsPassedToTheDriverBuilders()
    {
        var pg = ConnectionStrings.Parse(ConnectionStrings.Build(Pg() with { CaCertificatePath = "C:\\ca.pem" }, "pw"));
        Assert.Equal("C:\\ca.pem", pg["Root Certificate"]);
        var my = ConnectionStrings.Parse(ConnectionStrings.Build(new DatabaseConnectionSettings
        {
            Name = "m", Provider = DatabaseProvider.MySql, Host = "h", UserName = "u", Authentication = DatabaseAuthentication.UserPassword,
            CaCertificatePath = "C:\\ca.pem",
        }, "pw"));
        Assert.Equal("C:\\ca.pem", my["SSL CA"]);
        // Secure by default stays: full verification unless the user chose otherwise.
        Assert.Equal("VerifyFull", pg["SSL Mode"]);
        Assert.Equal("VerifyFull", my["SSL Mode"]);
    }

    [Fact]
    public void ThePathOfACaFile_MustExist_BeforeConnecting()
    {
        var d = ProviderRegistry.Default.Get("postgresql");
        var ex = Assert.Throws<DatabaseException>(() => d.Preflight(Pg() with { CaCertificatePath = "C:\\definitely\\not\\there.pem" }));
        Assert.Equal("CaCertificatePath", ex.Args[0]);
        d.Preflight(Pg());
    }

    [Fact]
    public void MariaDbFlavour_AndTheOdbcEngineHint_ChooseTheDialect()
    {
        var mysql = new DatabaseConnectionSettings { Name = "m", Provider = DatabaseProvider.MySql, Host = "h", UserName = "u" };
        Assert.Equal("mysql", ProviderRegistry.Default.ResolveDialect(mysql).Id);
        Assert.Equal("mariadb", ProviderRegistry.Default.ResolveDialect(mysql with { Extras = ProviderExtras.Of(("flavor", "mariadb")) }).Id);

        var odbc = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, Dsn = "x" };
        Assert.Equal("odbc", ProviderRegistry.Default.ResolveDialect(odbc).Id);
        var oracle = ProviderRegistry.Default.ResolveDialect(odbc with { Extras = ProviderExtras.Of(("underlyingEngine", "oracle")) });
        Assert.Equal("oracle", oracle.Id);
        // Whatever the engine, ODBC binds positionally and lists objects through the driver.
        Assert.Equal(ParameterStyle.Positional, oracle.ParameterStyle);
        Assert.Null(oracle.CatalogueSql);
        Assert.Throws<DatabaseException>(() => ConnectionStrings.Validate(odbc with { Extras = ProviderExtras.Of(("underlyingEngine", "nonsense")) }));
    }

    [Fact]
    public void DialectData_QuotesNamesAndLimitsRows()
    {
        Assert.Equal("[a]]b]", SqlDialects.SqlServer.QuoteIdentifier("a]b"));
        Assert.Equal("`a``b`", SqlDialects.MySql.QuoteIdentifier("a`b"));
        Assert.Equal("\"a\"\"b\"", SqlDialects.Oracle.QuoteIdentifier("a\"b"));
        Assert.Equal("abc", SqlDialects.OdbcUnknown.QuoteIdentifier("abc"));
        Assert.Throws<DatabaseException>(() => SqlDialects.MySql.QuoteIdentifier("a\nb"));

        Assert.Equal("SELECT * FROM t LIMIT 5", SqlDialects.PostgreSql.ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal("SELECT TOP 5 * FROM t", SqlDialects.SqlServer.ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal("SELECT * FROM t FETCH FIRST 5 ROWS ONLY", SqlDialects.Oracle.ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal("SELECT FIRST 5 * FROM t", SqlDialects.Firebird.ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal("SELECT * FROM (SELECT * FROM t) WHERE ROWNUM <= 5", (SqlDialects.Oracle with { RowLimit = RowLimitStyle.Rownum }).ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal("SELECT * FROM t", SqlDialects.OdbcUnknown.ApplyRowLimit("SELECT * FROM t", 5));
        Assert.Equal(SqlDialects.All.Count, SqlDialects.All.Select(d => d.Id).Distinct().Count());
        Assert.Same(SqlDialects.Oracle, SqlDialects.ById("ORACLE"));
        Assert.Null(SqlDialects.ById("nope"));
    }

    [Fact]
    public void Parameters_AreRewrittenToTheDriverStyle()
    {
        const string sql = "SELECT * FROM t WHERE a = @a AND b = @b AND c = @a";
        var tokens = SqlGuard.Check(sql, DatabaseProvider.SqlServer).ParameterTokens;
        var positional = Rewrite(sql, tokens, ParameterStyle.Positional);
        Assert.Equal("SELECT * FROM t WHERE a = ? AND b = ? AND c = ?", positional.Item1);
        Assert.Equal(new[] { "a", "b", "a" }, positional.Item2);
        Assert.Equal("SELECT * FROM t WHERE a = :a AND b = :b AND c = :a", Rewrite(sql, tokens, ParameterStyle.ColonName).Item1);
        Assert.Equal("SELECT * FROM t WHERE a = $a AND b = $b AND c = $a", Rewrite(sql, tokens, ParameterStyle.DollarName).Item1);
        Assert.Equal(sql, Rewrite(sql, tokens, ParameterStyle.AtName).Item1);
    }

    private static (string, string[]) Rewrite(string sql, IReadOnlyList<SqlToken> tokens, ParameterStyle style)
    {
        var (s, order) = ParameterRewriter.Rewrite(sql, tokens, style);
        return (s, order.ToArray());
    }
}
