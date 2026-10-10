using Rotula.Data;

namespace Rotula.Tests;

/// <summary>The preset catalogue (data, not code): it parses, validates, recognises hosts and builds safe connections.</summary>
public class DataPresetTests
{
    private static readonly string[] Expected =
    {
        "mariadb", "amazon-rds-mysql", "amazon-aurora-mysql", "amazon-rds-postgresql", "amazon-aurora-postgresql", "amazon-redshift",
        "cockroachdb", "yugabytedb", "supabase", "supabase-pooler", "neon", "neon-pooler", "planetscale", "tidb", "singlestore",
        "azure-sql-database", "azure-synapse", "microsoft-fabric", "google-cloud-sql-mysql", "google-cloud-sql-postgresql",
        "google-cloud-sql-sqlserver", "microsoft-access", "microsoft-excel", "text-csv",
    };

    [Fact]
    public void TheEmbeddedCatalogue_ParsesWithoutProblems_AndHasEveryRequestedPreset()
    {
        string json;
        using (var s = typeof(ProviderPresets).Assembly.GetManifestResourceStream("Rotula.Data.Presets.presets.json")!)
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var list = ProviderPresets.Parse(json, out var problems);
        Assert.Empty(problems);
        Assert.Equal(Expected.OrderBy(x => x), list.Select(p => p.Id).OrderBy(x => x));
        Assert.Equal(list.Count, ProviderPresets.Builtin.Count);
    }

    [Fact]
    public void EveryPreset_IsValid_AgainstTheRegistry_AndSecureByDefault()
    {
        foreach (var p in ProviderPresets.Builtin)
        {
            Assert.Empty(ProviderPresets.Validate(p, ProviderRegistry.Default));
            Assert.True(p.Encrypt, p.Id);
            Assert.False(p.TrustServerCertificate, p.Id);
            Assert.StartsWith("Data.Preset.", p.NameKey);
        }
        Assert.Equal(ProviderPresets.Builtin.Count, ProviderPresets.Builtin.Select(p => p.Id).Distinct().Count());
        Assert.Equal(ProviderPresets.Builtin.Count, ProviderRegistry.Default.Presets.Count);
    }

    [Fact]
    public void Presets_FillPortsProvidersAndOptions()
    {
        ProviderPreset P(string id) => ProviderPresets.Find(id)!;
        Assert.Equal(("mysql", 3306), (P("mariadb").ProviderId, P("mariadb").DefaultPort));
        Assert.Equal("mariadb", P("mariadb").Extras.Get("flavor"));
        Assert.Equal(5439, P("amazon-redshift").DefaultPort);
        Assert.True(P("amazon-redshift").Extras.GetBool("noStartupOptions"));
        Assert.Equal(26257, P("cockroachdb").DefaultPort);
        Assert.Equal(5433, P("yugabytedb").DefaultPort);
        Assert.Equal(6543, P("supabase-pooler").DefaultPort);
        Assert.Equal(4000, P("tidb").DefaultPort);
        Assert.Equal(1433, P("azure-sql-database").DefaultPort);
        Assert.True(P("microsoft-fabric").RequiresEntra);
        Assert.Contains(ProviderKeys.HintEntraOnly, P("microsoft-fabric").HintKeys);
        Assert.False(P("azure-sql-database").RequiresEntra);
        Assert.Equal("sqlserver", P("azure-synapse").ProviderId);
    }

    [Fact]
    public void CreateSettings_BuildsAValidSecureConnection_ForEveryServerPreset()
    {
        foreach (var p in ProviderPresets.Builtin.Where(p => p.Odbc is null))
        {
            var s = p.CreateSettings("conn", "host.example.com", "db", "reader");
            Assert.Equal(p.Id, s.PresetId);
            Assert.True(s.Encrypt);
            Assert.False(s.TrustServerCertificate);
            Assert.Equal(p.DefaultPort, s.Port);
            // Must validate and build (MySql and the others accept user + password; SQL Server too).
            string cs = ConnectionStrings.Build(s, "pw");
            Assert.Contains("host.example.com", cs);
        }
    }

    [Fact]
    public void PoolerPresets_DropTheStartupOptions_AndDirectPresetsKeepThem()
    {
        string Build(string id, string host) => ConnectionStrings.Build(ProviderPresets.Find(id)!.CreateSettings("c", host, "db", "u"), "pw");
        Assert.DoesNotContain("default_transaction_read_only", Build("neon-pooler", "ep-cool-1-pooler.eu-central-1.aws.neon.tech"));
        Assert.DoesNotContain("default_transaction_read_only", Build("supabase-pooler", "aws-0-eu-west-1.pooler.supabase.com"));
        Assert.DoesNotContain("default_transaction_read_only", Build("amazon-redshift", "c.abc.eu-west-1.redshift.amazonaws.com"));
        Assert.Contains("default_transaction_read_only", Build("neon", "ep-cool-1.eu-central-1.aws.neon.tech"));
        Assert.Contains("default_transaction_read_only", Build("supabase", "db.abcdefgh.supabase.co"));
        Assert.Contains("default_transaction_read_only", Build("cockroachdb", "x.cockroachlabs.cloud"));
        // The transaction still becomes read-only on a pooler: the in-transaction statement is part of the provider, not of the preset.
        Assert.Contains("SET TRANSACTION READ ONLY", ProviderRegistry.Default.Get("postgresql").ReadOnly.TransactionStatements);
    }

    [Theory]
    [InlineData("db.abcdefgh.supabase.co", null, "supabase")]
    [InlineData("aws-0-eu-west-1.pooler.supabase.com", null, "supabase-pooler")]
    [InlineData("ep-cool-1-pooler.eu-central-1.aws.neon.tech", null, "neon-pooler")]
    [InlineData("ep-cool-1.eu-central-1.aws.neon.tech", null, "neon")]
    [InlineData("mydb.abc123.us-east-1.rds.amazonaws.com", 5432, "amazon-rds-postgresql")]
    [InlineData("mydb.abc123.us-east-1.rds.amazonaws.com", 3306, "amazon-rds-mysql")]
    [InlineData("mycluster.cluster-abc123.eu-west-1.rds.amazonaws.com", 5432, "amazon-aurora-postgresql")]
    [InlineData("mycluster.cluster-ro-abc123.eu-west-1.rds.amazonaws.com", 3306, "amazon-aurora-mysql")]
    [InlineData("c.abc.eu-west-1.redshift.amazonaws.com", null, "amazon-redshift")]
    [InlineData("srv.database.windows.net", null, "azure-sql-database")]
    [InlineData("ws.sql.azuresynapse.net", null, "azure-synapse")]
    [InlineData("abc.datawarehouse.fabric.microsoft.com", null, "microsoft-fabric")]
    [InlineData("gateway01.eu-central-1.prod.aws.tidbcloud.com", null, "tidb")]
    [InlineData("SVC-1.SVC.EU.SINGLESTORE.COM", null, "singlestore")]
    [InlineData("aws.connect.psdb.cloud", null, "planetscale")]
    public void HostNames_AreRecognised_MostSpecificFirst(string host, int? port, string expectedFirst)
    {
        var found = ProviderPresets.Detect(host, port);
        Assert.NotEmpty(found);
        Assert.Equal(expectedFirst, found[0].Id);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("db.internal.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownHosts_MatchNothing(string? host) => Assert.Empty(ProviderPresets.Detect(host));

    [Fact]
    public void IsPoolerHost_OnlyForPoolerPresets()
    {
        Assert.True(ProviderPresets.IsPoolerHost("ep-a-1-pooler.eu.aws.neon.tech"));
        Assert.True(ProviderPresets.IsPoolerHost("x.pooler.supabase.com"));
        Assert.False(ProviderPresets.IsPoolerHost("ep-a-1.eu.aws.neon.tech"));
        Assert.False(ProviderPresets.IsPoolerHost(null));
    }

    [Theory]
    [InlineData("*.neon.tech", "ep-a.neon.tech", true)]
    [InlineData("*.neon.tech", "neon.tech", false)]
    [InlineData("ep-*-pooler.*.neon.tech", "EP-x-POOLER.eu.NEON.tech", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "anything", true)]
    [InlineData("abc", "abcd", false)]
    public void GlobMatching_IsCaseInsensitive_AndLiteral(string pattern, string text, bool expected)
        => Assert.Equal(expected, ProviderPresets.GlobMatch(pattern, text));

    [Fact]
    public void GlobMatching_CannotBeMadeSlow()
    {
        string pattern = string.Concat(Enumerable.Repeat("a*", 200)) + "b";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(ProviderPresets.GlobMatch(pattern, new string('a', 5000)));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    // ---------------------------------------------------------------- catalogue validation

    [Fact]
    public void ABrokenCatalogue_IsReported_NotCrashed()
    {
        var list = ProviderPresets.Parse("""{ "presets": [ { "id": "x" }, { "id": "ok", "nameKey": "Data.Preset.Ok", "name": "Ok", "providerId": "mysql" } ] }""", out var problems);
        Assert.Single(list);
        Assert.Single(problems);
        ProviderPresets.Parse("not json", out problems);
        Assert.Single(problems);
        ProviderPresets.Parse("""{ "presets": 5 }""", out problems);
        Assert.Single(problems);
        ProviderPresets.Parse("""{ "presets": [ { "id": "x", "nameKey": "k", "name": "n", "providerId": "mysql", "category": "Nonsense" } ] }""", out problems);
        Assert.Single(problems);
    }

    [Fact]
    public void Validation_CatchesInsecureBroadAndUnknownThings()
    {
        var ok = new ProviderPreset { Id = "t", NameKey = "Data.Preset.T", Name = "T", ProviderId = "postgresql", DefaultPort = 5432, HostPatterns = new[] { "*.example.com" } };
        Assert.Empty(ProviderPresets.Validate(ok, ProviderRegistry.Default));
        static bool Has(IReadOnlyList<string> l, string text) => l.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase));
        Assert.True(Has(ProviderPresets.Validate(ok with { Encrypt = false }, ProviderRegistry.Default), "secure"));
        Assert.True(Has(ProviderPresets.Validate(ok with { TrustServerCertificate = true }, ProviderRegistry.Default), "secure"));
        Assert.True(Has(ProviderPresets.Validate(ok with { HostPatterns = new[] { "*" } }, ProviderRegistry.Default), "too broad"));
        Assert.True(Has(ProviderPresets.Validate(ok with { HostPatterns = new[] { "*.ex ample.com" } }, ProviderRegistry.Default), "characters"));
        Assert.True(Has(ProviderPresets.Validate(ok with { ProviderId = "nope" }, ProviderRegistry.Default), "not registered"));
        Assert.True(Has(ProviderPresets.Validate(ok with { DefaultPort = 70000 }, ProviderRegistry.Default), "port"));
        Assert.True(Has(ProviderPresets.Validate(ok with { Extras = ProviderExtras.Of(("bogus", "1")) }, ProviderRegistry.Default), "not an option"));
        Assert.True(Has(ProviderPresets.Validate(ok with { NameKey = "Wrong.Key" }, ProviderRegistry.Default), "nameKey"));
        Assert.True(Has(ProviderPresets.Validate(ok with { HintKeys = new[] { "Oops" } }, ProviderRegistry.Default), "hint"));
        Assert.True(Has(ProviderPresets.Validate(ok with { Id = "Bad Id" }, ProviderRegistry.Default), "id must"));
        Assert.True(Has(ProviderPresets.Validate(ok with { PoolerHost = true, HostPatterns = Array.Empty<string>() }, ProviderRegistry.Default), "pooler"));
        Assert.True(Has(ProviderPresets.Validate(ok with { Odbc = new OdbcPresetInfo() }, ProviderRegistry.Default), "odbc"));
    }

    [Fact]
    public void ARegistry_RejectsAPresetOverAnUnknownProvider()
    {
        var r = new ProviderRegistry();
        var pack = new PresetPack();
        var ex = Assert.Throws<ProviderRegistrationException>(() => r.Register(pack));
        Assert.Equal(PackKeys.InvalidProviderId, ex.Key);
    }

    private sealed class PresetPack : IDatabaseProviderPack
    {
        public string PackId => "presetonly";
        public IReadOnlyList<IProviderDescriptor> GetProviders() => Array.Empty<IProviderDescriptor>();
        public IReadOnlyList<ProviderPreset> GetPresets() => new[]
        {
            new ProviderPreset { Id = "orphan", NameKey = "Data.Preset.Orphan", Name = "Orphan", ProviderId = "nothing-here" },
        };
    }

    // ---------------------------------------------------------------- ODBC file presets (Access, Excel, text)

    private static readonly string[] NoDrivers = Array.Empty<string>();

    [Fact]
    public void Access_BuildsAReadOnlyOdbcString_WithTheJetDialect()
    {
        var p = ProviderPresets.Find("microsoft-access")!;
        var s = p.CreateOdbcSettings("books", @"C:\data\books.accdb", NoDrivers);
        Assert.Equal(DatabaseProvider.Odbc, s.Provider);
        Assert.Equal("microsoft-access", s.PresetId);
        Assert.Contains("Microsoft Access Driver (*.mdb, *.accdb)", s.OdbcConnectionString);
        Assert.Contains(@"Dbq=C:\data\books.accdb", s.OdbcConnectionString);
        Assert.Contains("ReadOnly=1", s.OdbcConnectionString);
        Assert.Equal("jet", s.Extras.Get("underlyingEngine"));
        Assert.Equal("jet", ProviderRegistry.Default.ResolveDialect(s).Id);
        ConnectionStrings.Validate(s);
        Assert.Equal(ProviderKeys.HintInstallAce64, p.DriverMissingHintKey);
    }

    [Fact]
    public void Excel_AndText_Presets_UseTheirDriversAndPathKinds()
    {
        var x = ProviderPresets.Find("microsoft-excel")!.CreateOdbcSettings("book", @"C:\data\sheet.XLSX", NoDrivers);
        Assert.Contains("Microsoft Excel Driver", x.OdbcConnectionString);
        var xls = ProviderPresets.Find("microsoft-excel")!.CreateOdbcSettings("book", @"C:\data\old.xls", NoDrivers);
        Assert.Contains("old.xls", xls.OdbcConnectionString);

        var t = ProviderPresets.Find("text-csv")!.CreateOdbcSettings("folder", @"C:\data\csvs", NoDrivers);
        Assert.Contains("Microsoft Access Text Driver", t.OdbcConnectionString);
        Assert.Contains("Extensions=asc,csv,tab,txt", t.OdbcConnectionString);
        Assert.Equal("folder", ProviderPresets.Find("text-csv")!.Odbc!.PathKind);
    }

    [Theory]
    [InlineData(@"\\server\share\books.accdb")]          // UNC: merely opening it sends the user's credentials to another machine
    [InlineData("//server/share/books.accdb")]
    [InlineData("https://example.com/books.accdb")]
    [InlineData(@"C:\data\books.txt")]                    // wrong extension
    [InlineData(@"C:\data\books.accdb;Provider=x")]       // cannot add connection-string keywords
    [InlineData(@"C:\data\{evil}.accdb")]
    [InlineData("")]
    public void OdbcFilePresets_RefuseUnsafePaths(string path)
    {
        var p = ProviderPresets.Find("microsoft-access")!;
        Assert.Equal(DataKeys.InvalidSettings, Assert.Throws<DatabaseException>(() => p.CreateOdbcSettings("c", path, NoDrivers)).Key);
    }

    [Fact]
    public void AServerPreset_IsNotAnOdbcFilePreset()
    {
        Assert.Throws<DatabaseException>(() => ProviderPresets.Find("supabase")!.CreateOdbcSettings("c", @"C:\a.accdb", NoDrivers));
    }

    [Fact]
    public void AnInstalledDriver_IsPreferred()
    {
        var p = ProviderPresets.Find("microsoft-access")!;
        var s = p.CreateOdbcSettings("books", @"C:\data\books.mdb", new[] { "Microsoft Access Driver (*.mdb, *.accdb)", "Other" });
        Assert.Contains("Microsoft Access Driver", s.OdbcConnectionString);
    }

    [Fact]
    public async Task AMissingAccessDriver_GivesTheInstallHint_AsTheReason()
    {
        // A driver name that cannot exist stands in for "the 64-bit Access Database Engine is not installed".
        var s = new DatabaseConnectionSettings
        {
            Name = "books", Provider = DatabaseProvider.Odbc, PresetId = "microsoft-access",
            OdbcConnectionString = "Driver={Rotula Test Driver That Is Not Installed};Dbq=C:\\nowhere\\books.accdb;ReadOnly=1",
            Extras = ProviderExtras.Of(("underlyingEngine", "jet")),
        };
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(s).TestConnectionAsync());
        Assert.Equal(DataKeys.DriverNotInstalled, ex.Key);
        Assert.Equal(ProviderKeys.HintInstallAce64, ex.ReasonKey);

        // Without the preset the plain "driver not installed" is reported, no hint.
        var plain = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(s with { PresetId = null }).TestConnectionAsync());
        Assert.Equal(DataKeys.DriverNotInstalled, plain.Key);
        Assert.Null(plain.ReasonKey);
    }

    [Fact]
    public void ListingDrivers_NeverThrows()
    {
        Assert.NotNull(OdbcDriverCatalogue.GetInstalledDriverNames());
    }
}
