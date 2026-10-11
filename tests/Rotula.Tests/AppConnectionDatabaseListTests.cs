using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// The "Database name" drop-down of the connection manager, driven through the view-model with a fake lister: when it may load, what it
/// shows while and after loading, when it is cleared, and that typing a name always works.
/// </summary>
[Collection("App")]
public class AppConnectionDatabaseListTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly FakeConnectionStore _store;

    public AppConnectionDatabaseListTests()
    {
        _fake.Settings.Current.Language = "en";
        Loc.Instance.SetLanguage("en");
        _store = _fake.Database.Store;
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private FakeDatabaseServices Db => _fake.Database;

    private ConnectionManagerViewModel Manager(IReadOnlyList<OdbcDataSourceInfo>? dataSources = null)
        => new(Db, _fake.Build().Messages, _fake.Build().Files, dataSources: dataSources is null ? null : () => dataSources);

    private static OptionFieldViewModel DatabaseField(ConnectionManagerViewModel m) => m.Fields.Single(f => f.Key == ProviderOptionKeys.Database);

    private static async Task Settle(OptionFieldViewModel f)
    {
        for (int i = 0; i < 500 && f.IsLoadingList; i++) await Task.Delay(10);
        Assert.False(f.IsLoadingList);
    }

    /// <summary>A new SQL Server connection with the server and Windows sign-in filled in.</summary>
    private async Task<ConnectionManagerViewModel> WindowsFormAsync()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Plant";
        m.Host = "sql.local";
        m.Authentication = DatabaseAuthentication.WindowsIntegrated;
        return m;
    }

    private async Task<ConnectionManagerViewModel> SqlLoginFormAsync(string? password = "pw")
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Plant";
        m.Host = "sql.local";
        m.Authentication = DatabaseAuthentication.UserPassword;
        m.UserName = "reader";
        if (password is not null) m.Password = password;
        return m;
    }

    // ------------------------------------------------------------------------------------ which fields get a list

    [Fact]
    public async Task ServerEngines_GetAnEditableDatabaseList_ButFileEnginesAndPlainFieldsDoNot()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        foreach (var provider in new[] { DatabaseProvider.SqlServer, DatabaseProvider.PostgreSql, DatabaseProvider.MySql })
        {
            m.Provider = provider;
            Assert.True(DatabaseField(m).HasLookup, provider.ToString());
            Assert.False(DatabaseField(m).IsTextLike);
            Assert.False(m.Fields.Single(f => f.Key == ProviderOptionKeys.Host).HasLookup);
        }
        m.Provider = DatabaseProvider.Sqlite;
        Assert.DoesNotContain(m.Fields, f => f.HasLookup);            // SQLite keeps its file picker
        Assert.Contains(m.Fields, f => f.IsPath && f.CanBrowse);
        m.Provider = DatabaseProvider.Odbc;
        Assert.Equal(OptionLookupKind.OdbcDataSource, m.Fields.Single(f => f.Key == ProviderOptionKeys.Dsn).Lookup);
    }

    // ------------------------------------------------------------------------------------ never on its own

    [Fact]
    public async Task NothingIsLoaded_OnOpeningTheWindow_OnSelectingAConnection_OrOnTyping()
    {
        _store.Add(new DatabaseConnectionSettings { Name = "Saved", Provider = DatabaseProvider.SqlServer, Host = "h", Authentication = DatabaseAuthentication.WindowsIntegrated });
        var m = Manager();
        await m.LoadAsync("Saved");
        m.Host = "other";
        m.Database = "Sal";
        m.Database = "Sales";
        m.UserName = "x";
        m.Authentication = DatabaseAuthentication.WindowsIntegrated;
        Assert.Empty(Db.ListCalls);
        Assert.Empty(DatabaseField(m).LookupItems);
        Assert.False(DatabaseField(m).LookupAttempted);
    }

    // ------------------------------------------------------------------------------------ the button

    [Fact]
    public async Task LoadList_NeedsTheServer_AndTheUserForASqlLogin_AndShowsTheHintMeanwhile()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        var f = DatabaseField(m);
        Assert.False(f.CanLoadList);
        Assert.Equal("Fill in the server address and sign-in first, then load the list. You can also just type the name.", f.LookupText);

        m.Host = "sql.local";
        Assert.True(f.CanLoadList);                                   // Windows sign-in: nothing else to type
        Assert.Equal("Pick a database from the server, or type its name.", f.LookupText);

        m.Authentication = DatabaseAuthentication.UserPassword;
        Assert.False(f.CanLoadList);                                  // a user name is still missing
        m.UserName = "reader";
        Assert.True(f.CanLoadList);
        Assert.Equal("Type the password to load the list, or just type the database name.", f.LookupText);
        m.Password = "pw";
        Assert.Equal("Pick a database from the server, or type its name.", f.LookupText);
    }

    [Fact]
    public async Task LoadList_ShowsTheUsersDatabasesFirst_ThenTheSystemOnes_AndTheCount()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListAnswer = FakeLists.Of("master", "Sales", "model", "Stock");
        await f.LoadListCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Sales", "Stock", "master", "model" }, f.LookupItems.Select(i => i.Name).ToArray());
        Assert.Equal(new[] { false, false, true, true }, f.LookupItems.Select(i => i.IsSystem).ToArray());
        Assert.Equal("system", f.LookupItems[2].Tag);
        Assert.Equal("Databases found: 4. Pick one, or type its name.", f.LookupText);
        Assert.False(f.LookupIsError);
        Assert.False(f.IsLoadingList);
        var (settings, _) = Assert.Single(Db.ListCalls);
        Assert.Equal("sql.local", settings.Host);
    }

    [Fact]
    public async Task LoadList_IsBusyWhileLoading_AndSaveAndTestStayAvailable()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListGate = new TaskCompletionSource();
        var running = f.LoadListCommand.ExecuteAsync(null);
        Assert.True(f.IsLoadingList);
        Assert.False(f.CanLoadList);                                  // a second press does nothing
        Assert.True(f.StopListCommand.CanExecute(null));
        Assert.False(m.IsBusy);                                       // the manager's own busy state is not used, so Test and Save are not blocked
        Assert.True(m.TestCommand.CanExecute(null));

        Db.ListGate.SetResult();
        await running;
        Assert.False(f.IsLoadingList);
        Assert.False(f.StopListCommand.CanExecute(null));
        Assert.Equal(3, f.LookupItems.Count);
    }

    [Fact]
    public async Task Stop_CancelsTheLoad_KeepsWhatWasTyped_AndSaysSo()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        m.Database = "Typed";
        Db.ListGate = new TaskCompletionSource();
        var running = f.LoadListCommand.ExecuteAsync(null);
        f.StopListCommand.Execute(null);
        await running;

        Assert.False(f.IsLoadingList);
        Assert.Empty(f.LookupItems);
        Assert.Equal("Stopped. Press Load list to try again, or type the name.", f.LookupText);
        Assert.Equal("Typed", m.Database);
        Assert.Equal("Typed", f.Text);
        // The drop-down does not start another load by itself after a stop; the button does.
        f.RequestAutoLoad();
        Assert.Single(Db.ListCalls);
    }

    // ------------------------------------------------------------------------------------ opening the drop-down

    [Fact]
    public async Task OpeningTheDropDown_LoadsOnce_WhenTheFieldsAreFilled()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        m.OnLookupOpened(f);
        await Settle(f);
        Assert.Equal(3, f.LookupItems.Count);
        m.OnLookupOpened(f);                                          // opened again: the list is there already
        m.OnLookupOpened(f);
        await Settle(f);
        Assert.Single(Db.ListCalls);
    }

    [Fact]
    public async Task OpeningTheDropDown_DoesNotLoad_WhenSomethingIsMissing_OrAfterAFailure()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        var f = DatabaseField(m);
        m.OnLookupOpened(f);                                          // no server yet
        Assert.Empty(Db.ListCalls);

        m.Host = "sql.local";
        m.Authentication = DatabaseAuthentication.UserPassword;
        m.UserName = "reader";
        m.OnLookupOpened(f);                                          // a SQL login with no password typed
        Assert.Empty(Db.ListCalls);

        m.Password = "pw";
        Db.ListFailure = new DatabaseException(DataKeys.HostUnreachable);
        m.OnLookupOpened(f);
        await Settle(f);
        Assert.Single(Db.ListCalls);
        m.OnLookupOpened(f);                                          // a failed attempt is not repeated on every opening
        m.OnLookupOpened(f);
        Assert.Single(Db.ListCalls);
        Assert.True(f.CanLoadList);                                   // but the button can retry
    }

    // ------------------------------------------------------------------------------------ typing, clearing

    [Fact]
    public async Task TypingAName_AlwaysWorks_AndDoesNotClearTheLoadedList()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        f.Text = "Sale";                                              // what the combo box does while the user types
        f.Text = "SomethingNotInTheList";
        Assert.Equal("SomethingNotInTheList", m.Database);
        Assert.Equal(3, f.LookupItems.Count);
        f.Text = "Stock";                                             // picking an item writes the same text
        Assert.Equal("Stock", m.Database);
    }

    [Fact]
    public async Task TheList_IsClearedWhenHostPortUserSignInPasswordOrEngineChange()
    {
        var m = await SqlLoginFormAsync();
        var f = DatabaseField(m);
        m.Database = "Typed";

        async Task Reload()
        {
            f = DatabaseField(m);
            await f.LoadListCommand.ExecuteAsync(null);
            Assert.NotEmpty(f.LookupItems);
        }

        await Reload(); m.Host = "other.local";                       Assert.Empty(f.LookupItems); Assert.False(f.LookupAttempted);
        await Reload(); m.PortText = "1444";                          Assert.Empty(f.LookupItems);
        await Reload(); m.UserName = "someone";                       Assert.Empty(f.LookupItems);
        await Reload(); m.Authentication = DatabaseAuthentication.WindowsIntegrated; Assert.Empty(f.LookupItems);
        m.Authentication = DatabaseAuthentication.UserPassword;
        await Reload(); m.Password = "another";                       Assert.Empty(f.LookupItems);
        await Reload(); m.Encrypt = false;                            Assert.Empty(f.LookupItems);
        await Reload(); m.Provider = DatabaseProvider.PostgreSql;     Assert.Empty(DatabaseField(m).LookupItems);
        Assert.Equal("Typed", m.Database);                            // the name the user typed survives every one of these
    }

    [Fact]
    public async Task ChangingTheDatabaseBox_DoesNotClearTheList()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        m.Database = "Sales";
        Assert.Equal(3, f.LookupItems.Count);
    }

    [Fact]
    public async Task SelectingAnotherSavedConnection_ClearsTheList()
    {
        var a = _store.Add(new DatabaseConnectionSettings { Name = "A", Provider = DatabaseProvider.SqlServer, Host = "h", Database = "d1" });
        _store.Add(new DatabaseConnectionSettings { Name = "B", Provider = DatabaseProvider.SqlServer, Host = "h", Database = "d2" });
        _ = a;
        var m = Manager();
        await m.LoadAsync("A");
        var f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.NotEmpty(f.LookupItems);
        m.SelectedItem = m.Items.Single(i => i.Name == "B");
        Assert.Empty(DatabaseField(m).LookupItems);
        Assert.Equal("d2", m.Database);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheServerChanged_IsDiscarded()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListGate = new TaskCompletionSource();
        var running = f.LoadListCommand.ExecuteAsync(null);
        m.Host = "another.local";                                     // while the first server is still thinking
        Db.ListGate.SetResult();
        await running;

        Assert.Empty(f.LookupItems);
        Assert.False(f.IsLoadingList);
        Assert.False(f.LookupAttempted);
        Assert.Equal("Pick a database from the server, or type its name.", f.LookupText);
    }

    // ------------------------------------------------------------------------------------ errors in plain language

    [Fact]
    public async Task APermissionProblem_SaysTheLoginMayNotSeeOtherDatabases_AndToTypeTheName()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListFailure = new DatabaseException(DataKeys.PermissionDenied);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.True(f.LookupIsError);
        Assert.Equal("Could not read the list: the login may not be allowed to see other databases. Type the name instead.", f.LookupText);
        Assert.Empty(f.LookupItems);
        Assert.False(m.HasTestError);                                 // the Test banner is not used for the list
    }

    [Fact]
    public async Task OtherFailures_UseTheStandardSentence_WithoutAnySecret()
    {
        var m = await SqlLoginFormAsync("hunter2-secret");
        var f = DatabaseField(m);
        Db.ListFailure = new DatabaseException(DataKeys.AuthenticationFailed, null, null, "Login failed for user 'reader' pwd hunter2-secret");
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.True(f.LookupIsError);
        Assert.StartsWith("Could not read the list.", f.LookupText);
        Assert.EndsWith("You can still type the name.", f.LookupText);
        Assert.Contains(Loc.Get(DataKeys.AuthenticationFailed), f.LookupText);
        Assert.DoesNotContain("hunter2", f.LookupText);
    }

    [Fact]
    public async Task ANotApplicableAnswer_FallsBackToATextBoxMessage()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListAnswer = DatabaseListResult.NotSupported;
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.False(f.LookupIsError);
        Assert.Equal("This kind of server cannot list its databases. Type the name instead.", f.LookupText);
    }

    [Fact]
    public async Task EmptyAndTruncatedAnswers_AreExplained()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        Db.ListAnswer = FakeLists.Of();
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.Equal("This login cannot open any database on the server. Type the name instead.", f.LookupText);
        Db.ListAnswer = FakeLists.Truncated("a", "b");
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.Equal("The server has more databases than can be shown; these are the first 2. Pick one, or type its name.", f.LookupText);
    }

    [Fact]
    public async Task TheMessages_FollowTheLanguageSwitch()
    {
        var m = await WindowsFormAsync();
        var f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.StartsWith("Databases found: 3.", f.LookupText);
        Loc.Instance.SetLanguage("es");
        Assert.StartsWith("Bases de datos encontradas: 3.", f.LookupText);
        Assert.Equal("sistema", f.LookupItems.Single(i => i.Name == "master").Tag);
        Db.ListFailure = new DatabaseException(DataKeys.PermissionDenied);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.StartsWith("No se pudo leer la lista: puede que este usuario", f.LookupText);
        Loc.Instance.SetLanguage("en");
        Assert.StartsWith("Could not read the list: the login", f.LookupText);
    }

    // ------------------------------------------------------------------------------------ the password rules

    [Fact]
    public async Task ANewForm_NeverBorrowsASavedPassword_AndATypedOneIsPassedAlong()
    {
        var m = await WindowsFormAsync();
        await DatabaseField(m).LoadListCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, Db.ListCalls.Last().PasswordArgument);        // Windows sign-in: explicitly no password

        m.Authentication = DatabaseAuthentication.UserPassword;
        m.UserName = "reader";
        m.Password = "typed-pw";
        await DatabaseField(m).LoadListCommand.ExecuteAsync(null);
        Assert.Equal("typed-pw", Db.ListCalls.Last().PasswordArgument);
    }

    [Fact]
    public async Task ASavedConnectionEditedInPlace_ReusesItsPasswordOnlyUnderTheStoresRules()
    {
        _store.Add(new DatabaseConnectionSettings
        {
            Name = "Warehouse", Provider = DatabaseProvider.SqlServer, Host = "wh.local", Database = "WH",
            Authentication = DatabaseAuthentication.UserPassword, UserName = "svc",
        }, "saved-pw");
        var m = Manager();
        await m.LoadAsync("Warehouse");
        var f = DatabaseField(m);

        // The database is on the store's allowlist, so changing or typing it keeps the saved password usable; no password is typed.
        m.Database = "Other";
        m.OnLookupOpened(f);                                          // a saved password exists, so the drop-down may load
        await Settle(f);
        Assert.Null(Db.ListCalls.Single().PasswordArgument);                     // null = "use the saved one", decided by the store
        Assert.Equal(3, f.LookupItems.Count);

        // Pointing the connection at another server must ask for the password again: the store refuses, and the list says so.
        m.Host = "evil.example";
        f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.True(f.LookupIsError);
        Assert.Equal("Type the password again to load the list: the saved one is only used while the server and user stay the same.", f.LookupText);
        Assert.Empty(f.LookupItems);
    }

    [Fact]
    public async Task TestConnection_StillWorks_NextToTheList()
    {
        var m = await SqlLoginFormAsync("typed");
        var f = DatabaseField(m);
        await f.LoadListCommand.ExecuteAsync(null);
        await m.TestAsync();
        Assert.True(m.HasTestSuccess);
        var (settings, password) = Db.Created.Last();
        Assert.Equal("typed", password);
        Assert.Equal("sql.local", settings.Host);
        Assert.Equal(3, f.LookupItems.Count);                         // testing does not clear the list
    }

    // ------------------------------------------------------------------------------------ ODBC data sources

    [Fact]
    public async Task TheOdbcDataSourceBox_OffersTheInstalledDsns_WithoutAnyServerCall()
    {
        var found = new[] { new OdbcDataSourceInfo("Sales DSN", "SQL Server", false), new OdbcDataSourceInfo("Legacy", "Other Driver", true) };
        var m = Manager(found);
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Provider = DatabaseProvider.Odbc;
        var f = m.Fields.Single(x => x.Key == ProviderOptionKeys.Dsn);
        Assert.Equal("Pick a data source installed on this computer, or type its name.", f.LookupText);
        Assert.True(f.CanLoadList);
        m.OnLookupOpened(f);
        await Settle(f);
        Assert.Equal(new[] { "Sales DSN", "Legacy" }, f.LookupItems.Select(i => i.Name).ToArray());
        Assert.Equal("SQL Server", f.LookupItems[0].Tag);
        Assert.Empty(Db.ListCalls);
        f.Text = "Typed DSN";
        Assert.Equal("Typed DSN", m.Dsn);
    }

    [Fact]
    public async Task NoOdbcDataSources_IsExplained_AndTypingStillWorks()
    {
        var m = Manager(Array.Empty<OdbcDataSourceInfo>());
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Provider = DatabaseProvider.Odbc;
        var f = m.Fields.Single(x => x.Key == ProviderOptionKeys.Dsn);
        await f.LoadListCommand.ExecuteAsync(null);
        Assert.Equal("No ODBC data sources are installed on this computer. Type the name instead.", f.LookupText);
    }

    // ------------------------------------------------------------------------------------ language files

    [Theory]
    [InlineData("Conn.Db.LoadList")]
    [InlineData("Conn.Db.Loading")]
    [InlineData("Conn.Db.Hint")]
    [InlineData("Conn.Db.Hint.NeedServer")]
    [InlineData("Conn.Db.Hint.NeedPassword")]
    [InlineData("Conn.Db.Loaded")]
    [InlineData("Conn.Db.Truncated")]
    [InlineData("Conn.Db.None")]
    [InlineData("Conn.Db.NotSupported")]
    [InlineData("Conn.Db.Stopped")]
    [InlineData("Conn.Db.RetypePassword")]
    [InlineData("Conn.Db.ListFailed.Permission")]
    [InlineData("Conn.Db.ListFailed")]
    [InlineData("Conn.Dsn.Hint")]
    [InlineData("Conn.Dsn.None")]
    [InlineData("Conn.Lookup.SystemTag")]
    public void EveryListText_ExistsInBothLanguages_AndDiffers(string key)
    {
        Loc.Instance.SetLanguage("en");
        string en = Loc.Get(key, "x");
        Loc.Instance.SetLanguage("es");
        string es = Loc.Get(key, "x");
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.False(string.IsNullOrWhiteSpace(es));
        Assert.NotEqual(en, es);
        Assert.True(Loc.Instance.HasKey(key));
    }
}
