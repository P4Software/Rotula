using System.Data.Common;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Data;
using Rotula.Data.Packs;

namespace Rotula.Tests;

/// <summary>An engine as an add-on would provide it: only the descriptor, no code in the application knows about it.</summary>
internal sealed class FakeLedgerDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    public string Id => "ledger-db";
    public string DisplayNameKey => "Pack.Ledger.Name";            // deliberately not in the resource files: the English name is the fallback
    public string DisplayName => "Ledger DB";
    public ProviderCategory Category => ProviderCategory.Cloud;
    public int? DefaultPort => 7000;
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = new[] { DatabaseAuthentication.UserPassword };
    public IReadOnlyList<ProviderOption> Options { get; } = new[]
    {
        StandardOptions.Host(), StandardOptions.Port(), StandardOptions.UserName(),
        new ProviderOption("account", "Pack.Ledger.Account", ProviderOptionKind.Text) { Label = "Account name", Required = true },
        new ProviderOption("poolSize", "Pack.Ledger.Pool", ProviderOptionKind.Number) { Label = "Pool size", Minimum = 1, Maximum = 50, Advanced = true },
        new ProviderOption("region", "Pack.Ledger.Region", ProviderOptionKind.Choice)
        {
            Label = "Region", Advanced = true,
            Choices = new[] { new ProviderChoice("", "Pack.Ledger.R0", "Automatic"), new ProviderChoice("eu", "Pack.Ledger.R1", "Europe") },
        },
        new ProviderOption("compress", "Pack.Ledger.Compress", ProviderOptionKind.Bool) { Label = "Compress traffic", Advanced = true },
        new ProviderOption("keyFile", "Pack.Ledger.Key", ProviderOptionKind.FilePath) { Label = "Key file", Advanced = true },
    };
    public bool RequiresServer => true;
    public SqlDialect Dialect => SqlDialects.PostgreSql;
    public ReadOnlyPolicy ReadOnly { get; } = new() { GuardIsPrimaryControl = true, Description = "test" };
    public IProviderErrorMapper Errors => this;
    public void Validate(DatabaseConnectionSettings settings) { }
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password) => "x";
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => throw new NotSupportedException();
    public (int? Number, string? SqlState) Extract(Exception ex) => (null, null);
    public string? ClassifyNumber(int? number, bool connecting) => null;
}

internal sealed class FakeLedgerPack : IDatabaseProviderPack
{
    public string PackId => "ledger";
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new FakeLedgerDescriptor() };
}

/// <summary>The connection manager drawn from the provider registry: engines and presets from the registry, fields from the option schema.</summary>
[Collection("App")]
public class AppConnectionRegistryTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly FakeConnectionStore _store;
    private readonly FakeListPicker _files = new();

    public AppConnectionRegistryTests()
    {
        Loc.Instance.SetLanguage("en");
        _store = _fake.Database.Store;
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private ConnectionManagerViewModel Manager(ProviderRegistry? registry = null, IReadOnlyCollection<string>? drivers = null)
        => new(_fake.Database, _fake.Build().Messages, _files, registry ?? ProviderRegistry.Default, () => drivers ?? new[] { "Microsoft Access Driver (*.mdb, *.accdb)", "Microsoft Excel Driver (*.xls, *.xlsx, *.xlsm, *.xlsb)" });

    private static ProviderRegistry WithLedger()
    {
        var r = new ProviderRegistry();
        r.Register(new FakeLedgerPack());
        return r;
    }

    private static void Choose(ConnectionManagerViewModel m, string id) => m.SelectedEngine = m.Engines.First(e => e.Id == id);

    // ------------------------------------------------------------------------------------ the engine list

    [Fact]
    public void TheEngineList_KeepsTheFiveOriginalEnginesInOrder_AndListsPresetsUnderTheirProvider()
    {
        var m = Manager();
        var providers = m.Engines.Where(e => !e.IsPreset).Select(e => e.Id).ToList();
        Assert.Equal(new[] { "sqlserver", "postgresql", "mysql", "sqlite", "odbc" }, providers);
        for (int i = 0; i < m.Engines.Count; i++)
        {
            if (!m.Engines[i].IsPreset) continue;
            // Walk up to the nearest provider row: it must be the provider of the preset.
            int j = i;
            while (m.Engines[j].IsPreset) j--;
            Assert.Equal(m.Engines[j].Id, m.Engines[i].ProviderId);
        }
        Assert.Equal("azure-sql-database", m.Engines.SkipWhile(e => e.Id != "sqlserver").Skip(1).First().Id);
        Assert.Contains(m.Engines, e => e.Id == "supabase-pooler");
        Assert.Equal(ProviderRegistry.Default.GetDisplayEntries().Count, m.Engines.Count);
    }

    [Fact]
    public void AnAddOnEngine_AppearsInTheListWithoutAnyChangeToTheApplication_AndShowsItsEnglishNameWhenNotTranslated()
    {
        var m = Manager(WithLedger());
        var ledger = m.Engines.Single(e => e.Id == "ledger-db");
        Assert.Equal("Ledger DB", ledger.Name);
        Assert.Equal("Cloud services", ledger.GroupName);
        // Groups follow a fixed order (servers, cloud, files, ... ODBC), so an add-on lands in its own group between the built-in ones.
        var all = m.Engines.ToList();
        Assert.True(all.IndexOf(ledger) > all.FindIndex(e => e.Id == "mysql"));
        Assert.True(all.IndexOf(ledger) < all.FindIndex(e => e.Id == "sqlite"));
    }

    // ------------------------------------------------------------------------------------ the fields

    [Fact]
    public async Task TheFieldsOfAnAddOnEngine_AreDrawnFromItsSchema_AndSavedWithItsIdAndExtras()
    {
        var m = Manager(WithLedger());
        await m.LoadAsync();
        Choose(m, "ledger-db");
        Assert.Equal(DatabaseProvider.Custom, m.Provider);
        Assert.Equal("ledger-db", m.ProviderId);
        Assert.Equal(new[] { "host", "port", "account" }, m.Fields.Select(f => f.Key));
        Assert.Equal(new[] { "poolSize", "region", "compress", "keyFile" }, m.AdvancedFields.Select(f => f.Key));
        Assert.Equal("Account name", m.Fields[2].Label);                                   // English fallback of the add-on
        Assert.True(m.Fields[1].IsShort);
        Assert.Equal("7000", m.PortHint);
        Assert.True(m.ShowUserFields);                                                     // user and password is the only sign-in it offers
        Assert.False(m.ShowAuthChoice);
        Assert.Contains(m.Hints, h => h.Key == "Data.Hint.ReadOnlyAccount");

        m.Name = "Books";
        m.Fields[0].Text = "ledger.example.com";
        m.Fields[2].Text = "acme";
        m.UserName = "reader";
        m.Password = "pw";
        m.AdvancedFields.Single(f => f.Key == "poolSize").Text = "12";
        m.AdvancedFields.Single(f => f.Key == "region").SelectedChoice = "eu";
        m.AdvancedFields.Single(f => f.Key == "compress").Flag = true;
        await m.SaveAsync();

        var saved = _store.Entries["Books"].Settings;
        Assert.Equal(DatabaseProvider.Custom, saved.Provider);
        Assert.Equal("ledger-db", saved.ProviderId);
        Assert.Equal("ledger.example.com", saved.Host);
        Assert.Equal("reader", saved.UserName);
        Assert.Equal("acme", saved.Extras.Get("account"));
        Assert.Equal("12", saved.Extras.Get("poolSize"));
        Assert.Equal("eu", saved.Extras.Get("region"));
        Assert.True(saved.Extras.GetBool("compress"));

        // Opening it again fills the same fields.
        await m.LoadAsync("Books");
        Assert.Equal("acme", m.Fields.Single(f => f.Key == "account").Text);
        Assert.Equal("12", m.AdvancedFields.Single(f => f.Key == "poolSize").Text);
        Assert.Equal("eu", m.AdvancedFields.Single(f => f.Key == "region").SelectedChoice);
        Assert.True(m.AdvancedFields.Single(f => f.Key == "compress").Flag);
    }

    [Fact]
    public async Task ANumberOutsideItsLimits_IsRefusedWithAMessageThatNamesTheField()
    {
        var m = Manager(WithLedger());
        await m.LoadAsync();
        Choose(m, "ledger-db");
        m.Name = "X";
        m.AdvancedFields.Single(f => f.Key == "poolSize").Text = "99";
        await m.SaveAsync();
        Assert.True(m.HasFormError);
        Assert.Contains("Pool size", m.FormError);
        Assert.Contains("1 to 50", m.FormError);
        Assert.DoesNotContain("X", _store.Entries.Keys);
        m.Fields.Single(f => f.Key == "port").Text = "70000";
        m.AdvancedFields.Single(f => f.Key == "poolSize").Text = "5";
        await m.SaveAsync();
        Assert.Contains("65535", m.FormError);
    }

    [Fact]
    public async Task ThePasswordIsNotAField_ItStaysInTheProtectedBox()
    {
        var m = Manager(WithLedger());
        await m.LoadAsync();
        Choose(m, "ledger-db");
        Assert.DoesNotContain(m.Fields.Concat(m.AdvancedFields), f => f.Key.Contains("assword", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(m.Fields.Concat(m.AdvancedFields), f => f.Key == ProviderOptionKeys.UserName);
    }

    // ------------------------------------------------------------------------------------ presets

    [Fact]
    public async Task APreset_FillsItsDefaults_ShowsItsHints_AndSurvivesEverySave()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "supabase-pooler");
        Assert.Equal(DatabaseProvider.PostgreSql, m.Provider);
        Assert.Equal("supabase-pooler", m.PresetId);
        Assert.Equal("6543", m.PortText);
        Assert.True(m.Encrypt);
        Assert.False(m.TrustServerCertificate);
        Assert.Contains(m.Hints, h => h.Key == "Data.Hint.PoolerHost" && h.Text.Contains("pooler"));
        Assert.True(m.AdvancedFields.Single(f => f.Key == "noStartupOptions").Flag);

        m.Name = "Cloud";
        m.Host = "aws-0-eu.pooler.supabase.com";
        m.Database = "postgres";
        m.UserName = "u";
        m.Password = "pw";
        await m.SaveAsync();
        var first = _store.Entries["Cloud"].Settings;
        Assert.Equal("supabase-pooler", first.PresetId);
        Assert.Equal(DatabaseProvider.PostgreSql, first.Provider);
        Assert.Null(first.ProviderId);                                                       // built-in providers keep the old file format
        Assert.True(first.Extras.GetBool("noStartupOptions"));
        Assert.Equal(6543, first.Port);

        // Open it, change only the database, save: the preset and its option must not be dropped.
        await m.LoadAsync("Cloud");
        Assert.Equal("supabase-pooler", m.SelectedEngine!.Id);
        m.Database = "other";
        await m.SaveAsync();
        var second = _store.Entries["Cloud"].Settings;
        Assert.Equal("other", second.Database);
        Assert.Equal("supabase-pooler", second.PresetId);
        Assert.True(second.Extras.GetBool("noStartupOptions"));
    }

    [Fact]
    public async Task AnUnverifiedPreset_SaysSo_AndAnEntraOnlyPresetExplainsItself()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "amazon-redshift");
        Assert.Contains(m.Hints, h => h.Key == "Conn.Preset.Unverified" && h.IsWarning && h.Text.Length > 20);
        Choose(m, "microsoft-fabric");
        Assert.Contains(m.Hints, h => h.Key == "Data.Hint.EntraOnly" && h.IsWarning);
    }

    [Fact]
    public async Task ATypedHost_ThatLooksLikeAKnownService_OffersItsPreset_KeepingWhatWasTyped()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "postgresql");
        Assert.False(m.HasDetectedPreset);
        m.Host = "aws-0-eu-central-1.pooler.supabase.com";
        Assert.True(m.HasDetectedPreset);
        Assert.Contains("Supabase", m.DetectedPresetText);
        m.Database = "postgres";
        m.UseDetectedPresetCommand.Execute(null);
        Assert.Equal("supabase-pooler", m.SelectedEngine!.Id);
        Assert.Equal("aws-0-eu-central-1.pooler.supabase.com", m.Host);
        Assert.Equal("postgres", m.Database);
        Assert.Equal("6543", m.PortText);
    }

    [Fact]
    public async Task AnOdbcFilePreset_AsksForAFile_BuildsTheConnectionWithTheDriver_AndWarnsWhenTheDriverIsMissing()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "microsoft-access");
        Assert.Equal("filePath", Assert.Single(m.Fields).Key);
        Assert.DoesNotContain(m.Hints, h => h.Key == "Data.Hint.InstallAce64");           // the driver is "installed" in this fake
        m.Name = "Old books";
        m.FilePath = @"C:\data\books.accdb";
        await m.SaveAsync();
        var saved = _store.Entries["Old books"].Settings;
        Assert.Equal(DatabaseProvider.Odbc, saved.Provider);
        Assert.Equal("microsoft-access", saved.PresetId);
        Assert.Contains("Dbq=", saved.OdbcConnectionString);
        Assert.Contains("books.accdb", saved.OdbcConnectionString);
        Assert.Equal("jet", saved.Extras.Get("underlyingEngine"));

        // Editing it shows the file again, read back from the connection string.
        await m.LoadAsync("Old books");
        Assert.Equal(@"C:\data\books.accdb", m.FilePath);
        Assert.Equal("microsoft-access", m.SelectedEngine!.Id);

        // A wrong extension is refused in plain language.
        m.FilePath = @"C:\data\books.txt";
        m.Name = "Old books";
        await m.SaveAsync();
        Assert.True(m.HasFormError);

        var noDriver = Manager(drivers: Array.Empty<string>());
        await noDriver.LoadAsync();
        Choose(noDriver, "microsoft-access");
        Assert.Contains(noDriver.Hints, h => h.Key == "Data.Hint.InstallAce64" && h.IsWarning && h.Text.Contains("Access Database Engine"));
    }

    [Fact]
    public async Task ATextFilePreset_UsesTheFolderPicker()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "text-csv");
        var field = Assert.Single(m.Fields);
        Assert.True(field.IsFolder);
        _files.FolderPath = @"C:\csv-files";
        field.Browse();
        Assert.Equal(@"C:\csv-files", m.FilePath);
        Assert.Equal("Folder", field.Label);
    }


    [Fact]
    public async Task TheCertificateSwitch_StaysVisibleWithTheSecuritySwitches_NotHiddenInTheCollapsedSection()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "sqlserver");
        Assert.Contains(m.SwitchFields, f => f.Key == "trustServerCertificate");
        Assert.DoesNotContain(m.AdvancedFields, f => f.Key == "trustServerCertificate");
        var trust = m.SwitchFields.Single(f => f.Key == "trustServerCertificate");
        Assert.False(trust.HasWarning);
        m.TrustServerCertificate = true;
        Assert.True(trust.Flag);
        Assert.True(trust.HasWarning);
        Assert.Contains("impersonate", trust.Help);

        // A saved connection that trusts the certificate opens with the warning showing.
        _store.Add(new DatabaseConnectionSettings { Name = "Lab", Provider = DatabaseProvider.SqlServer, Host = "h", TrustServerCertificate = true });
        await m.LoadAsync("Lab");
        Assert.True(m.SwitchFields.Single(f => f.Key == "trustServerCertificate").HasWarning);
    }

    [Fact]
    public async Task APreset_SaysWhatTheAddressesOfItsServiceLookLike()
    {
        var m = Manager();
        await m.LoadAsync();
        Choose(m, "supabase-pooler");
        var host = m.Fields.Single(f => f.Key == "host");
        Assert.True(host.HasPatternHelp);
        Assert.Contains("pooler.supabase.com", host.PatternHelp);
        Choose(m, "postgresql");
        Assert.False(m.Fields.Single(f => f.Key == "host").HasPatternHelp);
        Loc.Instance.SetLanguage("es");
        Choose(m, "supabase-pooler");
        Assert.Contains("Las direcciones de este servicio", m.Fields.Single(f => f.Key == "host").PatternHelp);
    }

    // ------------------------------------------------------------------------------------ add-on missing

    [Fact]
    public async Task ASavedConnectionOfAnAddOnThatIsNotInstalled_IsShownAsSuch_AndNeverRewritten()
    {
        var original = new DatabaseConnectionSettings { Name = "Ghost", Provider = DatabaseProvider.Custom, ProviderId = "ghost-db", Host = "h.example.com", Port = 1234, Extras = ProviderExtras.Of(("account", "a")) };
        _store.Add(original);
        var m = Manager();
        await m.LoadAsync("Ghost");
        Assert.True(m.IsPackMissing);
        Assert.False(m.IsFormAvailable);
        Assert.Contains("ghost-db", m.MissingPackText);
        Assert.Contains("not installed", m.MissingPackText);
        Assert.Empty(m.Fields);
        Assert.False(m.SaveCommand.CanExecute(null));
        Assert.False(m.TestCommand.CanExecute(null));
        Assert.Equal("ghost-db", m.Items.Single().ProviderName);
        await m.SaveAsync();
        Assert.Equal(original, _store.Entries["Ghost"].Settings);
        Assert.Empty(_store.Saves);

        // Once the add-on is registered the same connection is editable again, with its extras.
        var registry = new ProviderRegistry();
        registry.Register(new FakeLedgerPack());
        _store.Entries.Remove("Ghost");
        _store.Add(original with { Name = "Books", ProviderId = "ledger-db" });
        var ok = Manager(registry);
        await ok.LoadAsync("Books");
        Assert.False(ok.IsPackMissing);
        Assert.Equal("a", ok.Fields.Single(f => f.Key == "account").Text);
    }

    // ------------------------------------------------------------------------------------ test and passwords

    [Fact]
    public async Task Test_ForANewConnection_NeverBorrowsASavedPassword_AndSaysExplicitlyNoPasswordForWindowsSignIn()
    {
        _store.Add(new DatabaseConnectionSettings { Name = "test", Provider = DatabaseProvider.SqlServer, Host = "real", Authentication = DatabaseAuthentication.UserPassword, UserName = "u" }, "secret");
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Host = "somewhere";
        m.Authentication = DatabaseAuthentication.WindowsIntegrated;
        await m.TestAsync();                                                                 // unnamed: the placeholder name "test" must not pick the saved one
        Assert.Equal("", Assert.Single(_store.OpenedWithSettings).PasswordArgument);
        Assert.True(m.HasTestSuccess);

        m.Authentication = DatabaseAuthentication.UserPassword;
        m.UserName = "x";
        m.Name = "test";                                                                     // a NEW connection with the name of a saved one: still no borrowing
        await m.TestAsync();
        Assert.Equal("", _store.OpenedWithSettings[^1].PasswordArgument);
    }

    [Fact]
    public async Task Test_OfASavedConnectionWithThePasswordBoxEmpty_ReusesTheSavedPassword_ButAnEditedAddressAsksAgain()
    {
        _store.Add(new DatabaseConnectionSettings { Name = "Warehouse", Provider = DatabaseProvider.SqlServer, Host = "h", Authentication = DatabaseAuthentication.UserPassword, UserName = "u", Encrypt = true }, "secret");
        var m = Manager();
        await m.LoadAsync("Warehouse");
        int focus = 0;
        m.PasswordFocusRequested += (_, _) => focus++;
        await m.TestAsync();
        Assert.Null(_store.OpenedWithSettings[^1].PasswordArgument);                         // "reuse the saved one": the store decides
        Assert.True(m.HasTestSuccess);

        m.Host = "somewhere-else";
        await m.TestAsync();
        Assert.True(m.HasTestError);
        Assert.Contains("Password box", m.TestText);
        Assert.Equal(1, focus);

        m.Password = "typed";
        await m.TestAsync();
        Assert.Equal("typed", _store.OpenedWithSettings[^1].PasswordArgument);
        Assert.True(m.HasTestSuccess);
    }

    // ------------------------------------------------------------------------------------ language

    [Fact]
    public async Task TheListAndTheFields_FollowTheLanguage()
    {
        var m = Manager(WithLedger());
        await m.LoadAsync();
        Choose(m, "supabase-pooler");
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Supabase (agrupador de conexiones)", m.Engines.Single(e => e.Id == "supabase-pooler").Name);
        Assert.Equal("Servidores de bases de datos", m.Engines.First(e => e.Id == "postgresql").GroupName);
        Assert.Equal("Dirección del servidor", m.Fields.First(f => f.Key == "host").Label);
        Assert.Contains("agrupador", m.Hints.First(h => h.Key == "Data.Hint.PoolerHost").Text);
        Assert.Equal("supabase-pooler", m.SelectedEngine!.Id);                               // the selection survives the rebuild
    }

    [Fact]
    public void TheDataWindowListName_OfAConnection_UsesThePresetNameOrTheProviderId()
    {
        var preset = new DatabaseConnectionSettings { Name = "a", Provider = DatabaseProvider.PostgreSql, PresetId = "neon" };
        Assert.Equal("Neon (direct connection)", DataMessages.ProviderName(preset));
        Assert.Equal("PostgreSQL", DataMessages.ProviderName(new DatabaseConnectionSettings { Name = "b", Provider = DatabaseProvider.PostgreSql }));
        Assert.Equal("nope-db", DataMessages.ProviderName(new DatabaseConnectionSettings { Name = "c", Provider = DatabaseProvider.Custom, ProviderId = "nope-db" }));
    }
}
