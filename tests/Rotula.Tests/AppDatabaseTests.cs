using System.Reflection;
using System.Text.Json;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Data;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// The database half of the Data merge window and the connection manager, driven through the view-models with a fake
/// <see cref="IDatabaseSource"/> and a fake <see cref="IConnectionStore"/>: selection flow, guard messages, parameters,
/// the row cap, cancellation, "opening a label never connects", and "a .rotula file never holds credentials".
/// </summary>
[Collection("App")]
public class AppDatabaseTests : IDisposable
{
    // Distinctive strings that must never reach a .rotula file.
    private const string SecretHost = "secret-host.corp.example";
    private const string SecretUser = "svc_user_77";
    private const string SecretPassword = "P@ssw0rd-XYZ-4321";

    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;
    private readonly FakeConnectionStore _store;
    private readonly FakeDatabaseSource _source;

    public AppDatabaseTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
        _store = _fake.Database.Store;
        _source = _store.Add(new DatabaseConnectionSettings
        {
            Name = "Warehouse", Provider = DatabaseProvider.SqlServer, Host = SecretHost, Database = "WH",
            Authentication = DatabaseAuthentication.UserPassword, UserName = SecretUser,
        }, SecretPassword);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private DataMergeViewModel Data => _main.Data;
    private DatabaseSourceViewModel Db => _main.Data.Database;

    private static async Task Idle(DatabaseSourceViewModel d)
    {
        for (int i = 0; i < 500 && d.IsBusy; i++) await Task.Delay(10);
        Assert.False(d.IsBusy);
    }

    private async Task Pick(string name = "Warehouse")
    {
        await Db.RefreshConnectionsAsync();
        await Db.SelectConnectionAsync(name);
        Assert.NotNull(Db.SelectedConnection);
    }

    private static DataMergeSettings WithQuery(string connection, string query, params QueryParameterSetting[] parameters)
    {
        var s = new DataMergeSettings { Database = new DatabaseMergeSettings { Reference = new DatabaseQueryReference(connection, query), MaxRows = 500 } };
        s.Database.Parameters.AddRange(parameters);
        return s;
    }

    private string SaveLabel(DataMergeSettings merge)
    {
        string path = Path.Combine(_dir, "label.rotula");
        RotulaFileSerializer.Save(_main.Session.Document, path, merge);
        return path;
    }

    // ------------------------------------------------------------------------------------ selection flow

    [Fact]
    public async Task PickingAConnectionAndATable_ShowsAPreview_AndUseThisDataFeedsTheMergePipeline()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{Name}} x{{Qty}}" }));
        Data.IsDatabaseMode = true;
        await Pick();

        Assert.Equal(3, Db.Tables.Count);
        Db.TableFilter = "prod";
        Assert.Equal(new[] { "ActiveProducts", "Products" }, Db.Tables.Select(t => t.Name).OrderBy(n => n).ToArray());

        Db.SelectedTable = Db.Tables.Single(t => t.Name == "Products");
        await Idle(Db);
        Assert.Equal(100, _source.Previews.Single().MaxRows);   // the preview is capped at 100 rows
        Assert.Equal(new[] { "Name", "Qty" }, Db.GridColumns);
        Assert.Equal(2, Db.GridRows.Count);

        await Db.UseDataAsync();

        // "Use this data" re-reads the whole table up to the cap (the preview alone would stop at 100 records).
        var query = Assert.Single(_source.Queries);
        Assert.Equal("SELECT * FROM \"Products\"", query.Sql);
        Assert.Equal(DatabaseSource.DefaultMaxRows, query.MaxRows);
        Assert.True(Data.HasData);
        Assert.True(Data.IsDatabaseMode);
        Assert.Null(Data.SourcePath);
        Assert.True(Data.ShowTabs);
        Assert.False(Data.ShowDatabasePanel);
        Assert.Equal(2, Data.RecordCount);
        Assert.Equal("Tuerca", Data.CurrentData!["Name"]);
        Data.CurrentIndex = 1;
        Assert.Equal("Perno", Data.CurrentData!["Name"]);
        Assert.Contains("Warehouse", Data.ResultText);
        var saved = Data.ToSettings().Database!;
        Assert.Equal("Warehouse", saved.Reference.ConnectionName);
        Assert.Equal("SELECT * FROM \"Products\"", saved.Reference.Query);
    }

    [Fact]
    public async Task ChangingTheDataSource_KeepsTheLoadedDataUntilRowsAreUsed()
    {
        Data.IsDatabaseMode = true;
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name, Qty FROM Products";
        await Db.UseDataAsync();
        Assert.True(Data.HasData);

        Data.ChangeDatabaseSourceCommand.Execute(null);
        Assert.True(Data.ShowDatabasePicker);
        Assert.True(Data.ShowDatabasePanel);
        Assert.False(Data.ShowTabs);
        Assert.Equal(2, Data.RecordCount);          // still there behind the picker
        Data.HideDatabasePickerCommand.Execute(null);
        Assert.True(Data.ShowTabs);
    }

    [Fact]
    public async Task APasswordIsNeverKeptByTheViewModels()
    {
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT 1";
        await Db.UseDataAsync();
        // The store hands out a ready source; nothing in the view-models can read the password back.
        Assert.Equal("Warehouse", Assert.Single(_store.Opened));
        foreach (var vm in new object[] { Db, Data })
            foreach (var p in vm.GetType().GetProperties())
                Assert.DoesNotContain("password", p.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------ guard

    [Fact]
    public async Task TheGuardExplainsEachRefusalInPlainLanguage_InBothLanguages()
    {
        await Pick();
        Db.ModeIndex = 1;

        Db.QueryText = "DELETE FROM Products";
        Assert.False(Db.GuardOk);
        Assert.True(Db.HasGuardMessage);
        Assert.Contains("SELECT", Db.GuardMessage);           // not a SELECT at all
        Assert.False(Db.RunQueryCommand.CanExecute(null));
        Assert.False(Db.UseDataCommand.CanExecute(null));

        Db.QueryText = "SELECT 1; DROP TABLE Products";
        Assert.True(Db.GuardMessage.Contains("one statement", StringComparison.OrdinalIgnoreCase) || Db.GuardMessage.Contains("DROP"), Db.GuardMessage);

        Db.QueryText = "SELECT * INTO copy FROM Products";
        Assert.Contains("INTO", Db.GuardMessage);

        Db.QueryText = "SELECT Name FROM Products";
        Assert.True(Db.GuardOk);
        Assert.False(Db.HasGuardMessage);
        Assert.True(Db.RunQueryCommand.CanExecute(null));

        // A data-changing word hidden inside a SELECT is named in the message.
        Db.QueryText = "WITH gone AS (DELETE FROM Products RETURNING *) SELECT * FROM gone";
        Assert.Contains("DELETE", Db.GuardMessage);
        Assert.Contains("can change data", Db.GuardMessage);

        Loc.Instance.SetLanguage("es");
        Assert.Contains("no está permitida", Db.GuardMessage);
        Assert.Contains("DELETE", Db.GuardMessage);
        Db.QueryText = "DELETE FROM Products";
        Assert.Contains("SELECT", Db.GuardMessage);
    }

    [Fact]
    public async Task AnEmptyQueryBoxIsNotAnError_ButCannotRun()
    {
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = string.Empty;
        Assert.False(Db.HasGuardMessage);
        Assert.False(Db.GuardOk);
        Assert.False(Db.RunQueryCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------------------------ parameters

    [Fact]
    public async Task QueryParameters_AreListed_ConvertedByType_AndRemembered()
    {
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name, Qty FROM Products WHERE Qty > @min AND Added > @since AND Name <> @skip";
        Assert.Equal(new[] { "min", "since", "skip" }, Db.Parameters.Select(p => p.Name).ToArray());
        Assert.True(Db.HasParameters);

        Db.Parameters[0].Type = "number";
        Db.Parameters[0].Value = "3.5";
        Db.Parameters[1].Type = "date";
        Db.Parameters[1].Value = "2024-03-05";
        Db.Parameters[2].Value = "Tuerca";
        await Db.RunQueryAsync();

        var q = Assert.Single(_source.Queries);
        Assert.Equal(3.5m, q.Parameters!["min"]);
        Assert.Equal(new DateTime(2024, 3, 5), q.Parameters["since"]);
        Assert.Equal("Tuerca", q.Parameters["skip"]);

        // Editing the query keeps the values already typed for the parameters that remain.
        Db.QueryText = "SELECT Name FROM Products WHERE Qty > @min";
        Assert.Equal("3.5", Assert.Single(Db.Parameters).Value);
        Assert.Equal("number", Db.Parameters[0].Type);
    }

    [Fact]
    public async Task ABadNumberOrDate_IsReportedBeforeAnythingIsSent()
    {
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name FROM Products WHERE Qty > @min";
        Db.Parameters[0].Type = "number";
        Db.Parameters[0].Value = "lots";
        await Db.RunQueryAsync();

        Assert.Empty(_source.Queries);
        Assert.True(Db.HasError);
        Assert.Contains("@min", Db.ResultText);
        Assert.Contains("not a number", Db.ResultText);

        Db.Parameters[0].Type = "date";
        await Db.RunQueryAsync();
        Assert.Contains("not a date", Db.ResultText);
        Assert.Empty(_source.Queries);
    }

    [Fact]
    public async Task TheRowCap_MustBeAValidNumber_AndIsPassedToTheQuery()
    {
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name FROM Products";

        Db.MaxRowsText = "0";
        await Db.RunQueryAsync();
        Assert.True(Db.HasError);
        Assert.Empty(_source.Queries);

        Db.MaxRowsText = "2500";
        await Db.RunQueryAsync();
        Assert.Equal(2500, Assert.Single(_source.Queries).MaxRows);
    }

    // ------------------------------------------------------------------------------------ truncation

    [Fact]
    public async Task ATruncatedResult_ShowsTheWarning_AndKeepsItAfterTheRowsAreUsed()
    {
        _source.ReportTruncated = true;
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name, Qty FROM Products";
        Db.MaxRowsText = "50";
        await Db.RunQueryAsync();

        Assert.True(Db.HasTruncatedWarning);
        Assert.Contains("first 50 rows", Db.TruncatedWarning);
        Assert.DoesNotContain("!Issue", Db.TruncatedWarning);

        await Db.UseDataAsync();
        Assert.Single(_source.Queries);                  // nothing changed, so the run is reused and the server is not asked twice
        Assert.True(Data.HasLoadWarnings);
        Assert.Contains("first 50 rows", Data.LoadWarnings.Single());
    }

    // ------------------------------------------------------------------------------------ cancellation and errors

    [Fact]
    public async Task Stop_CancelsARunningQuery_WithACalmMessage()
    {
        _source.Gate = new TaskCompletionSource();
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name FROM Products";

        var run = Db.RunQueryAsync();
        Assert.True(Db.IsBusy);
        Assert.False(Db.RunQueryCommand.CanExecute(null));   // one operation at a time
        Db.CancelOperationCommand.Execute(null);
        await run;

        Assert.False(Db.IsBusy);
        Assert.False(Db.HasError);
        Assert.Equal("Stopped. Nothing was changed.", Db.ResultText);
        Assert.False(Data.HasData);
    }

    [Fact]
    public async Task ADatabaseFailure_IsShownAsAPlainSentence_NotARawKey()
    {
        await Pick();
        _source.Failure = new DatabaseException(DataKeys.AuthenticationFailed, null, null, "Login failed for user");
        await Db.LoadTablesAsync();

        Assert.True(Db.HasError);
        Assert.Contains("user name or password", Db.ResultText);
        Assert.DoesNotContain("Data.Error", Db.ResultText);
        Assert.Equal("Login failed for user", Db.ErrorDetail);
        Assert.False(Db.IsBusy);

        Loc.Instance.SetLanguage("es");
        _source.Failure = new DatabaseException(DataKeys.QueryRejected, DataKeys.RejectForbiddenKeyword, new[] { "DROP" });
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT 1";
        await Db.RunQueryAsync();
        Assert.Contains("solo lee datos", Db.ResultText);
        Assert.DoesNotContain("Data.", Db.ResultText);
    }

    [Fact]
    public async Task EveryErrorKeyOfTheLibrary_HasAMessage_InBothLanguages()
    {
        var keys = typeof(DataKeys).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).Where(k => k.StartsWith("Data.Error.") || k.StartsWith("Data.Store.")).ToList();
        Assert.True(keys.Count >= 18);
        foreach (string lang in new[] { "en", "es" })
        {
            Loc.Instance.SetLanguage(lang);
            foreach (string key in keys)
            {
                string text = DataMessages.Describe(new DatabaseException(key, key == DataKeys.QueryRejected ? DataKeys.RejectNotSelect : null, new[] { "x", "y" }));
                if (key.StartsWith("Data.Store.")) text = DataMessages.DescribeStore(key);
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                Assert.DoesNotContain("!Data.", text);
                Assert.DoesNotContain("{0}", text);
            }
        }
    }

    // ------------------------------------------------------------------------------------ opening a label never connects

    [Fact]
    public async Task OpeningALabelThatRefersToAConnection_ReconnectsByNameAndShowsTheDataWithoutAClick()
    {
        string path = SaveLabel(WithQuery("warehouse", "SELECT Name, Qty FROM Products WHERE Qty > @min", new QueryParameterSetting("min", "3", "number")));
        Assert.True(_main.LoadFile(path));
        await _main.DataReload;

        // The label stores only the connection NAME; it is found in the user's own saved list and the query ran through the guard.
        Assert.Equal("Warehouse", Assert.Single(_store.Opened));
        var q = Assert.Single(_source.Queries);
        Assert.Equal(3m, q.Parameters!["min"]);
        Assert.Equal(500, q.MaxRows);
        Assert.True(Data.HasData);
        Assert.False(Db.IsReferencePending);
        Assert.Equal(2, Data.RecordCount);
        Assert.NotNull(Data.CurrentData);
        Assert.False(_main.Session.IsDirty, "Reading the data again is not an edit.");
    }

    [Fact]
    public async Task AMissingConnection_ExplainsHowToCreateItWithThatName()
    {
        _main.LoadFile(SaveLabel(WithQuery("Plant 9", "SELECT 1")));
        await Db.RefreshConnectionsAsync();

        Assert.True(Db.ReferenceMissing);
        Assert.Contains("\"Plant 9\"", Db.ReferenceMissingText);
        Assert.Contains("Manage connections", Db.ReferenceMissingText);
        await Db.ConnectReferenceAsync();
        Assert.Empty(_store.Opened);
        Assert.False(Data.HasData);
        Assert.True(Db.IsReferencePending);

        Loc.Instance.SetLanguage("es");
        Assert.Contains("exactamente el nombre \"Plant 9\"", Db.ReferenceMissingText);
    }

    [Fact]
    public async Task QueryTextFromAFile_GoesThroughTheGuard_BeforeAnythingRuns()
    {
        _main.LoadFile(SaveLabel(WithQuery("Warehouse", "DELETE FROM Products")));
        Assert.True(Db.HasGuardMessage);                    // the refusal is visible as soon as the label is opened
        await Db.RefreshConnectionsAsync();
        await Db.ConnectReferenceAsync();

        Assert.Empty(_source.Queries);
        Assert.False(Data.HasData);
        Assert.True(Db.HasError);
        Assert.Contains("only reads data", Db.ResultText);
        Assert.True(Db.IsReferencePending);
    }

    [Fact]
    public async Task LoadingAFile_ReplacesTheDatabaseReference()
    {
        _main.LoadFile(SaveLabel(WithQuery("Warehouse", "SELECT 1")));
        string csv = Path.Combine(_dir, "a.csv");
        File.WriteAllText(csv, "Name,Qty\nA,1\n");
        Assert.True(await Data.LoadAsync(csv));

        Assert.True(Data.IsFileMode);
        Assert.False(Db.IsReferencePending);
        var s = Data.ToSettings();
        Assert.Null(s.Database);
        Assert.Equal(csv, s.SourcePath);
    }

    // ------------------------------------------------------------------------------------ the .rotula file never holds credentials

    [Fact]
    public async Task ASavedLabel_ContainsTheConnectionName_ButNeverHostUserPasswordOrConnectionString()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{Name}}" }));
        await Pick();
        Db.ModeIndex = 1;
        Db.QueryText = "SELECT Name, Qty FROM Products WHERE Qty > @min";
        Db.Parameters[0].Type = "number";
        Db.Parameters[0].Value = "3";
        await Db.UseDataAsync();

        string json = RotulaFileSerializer.Serialize(_main.Session.Document, Data.ToSettings());
        Assert.Contains("\"connectionName\": \"Warehouse\"", json);
        // JSON escapes characters such as ">" (\u003E), so read the query back instead of searching the raw text.
        Assert.Equal("SELECT Name, Qty FROM Products WHERE Qty > @min",
            JsonDocument.Parse(json).RootElement.GetProperty("merge").GetProperty("database").GetProperty("query").GetString());
        Assert.Equal(RotulaFileSerializer.DatabaseFormatVersion, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        foreach (string secret in new[] { SecretHost, SecretUser, SecretPassword, "\"WH\"", "Pwd=", "Password=", "Server=", "Data Source", "Integrated Security", "sqlserver" })
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);

        // The file shape has no place to put credentials at all.
        string[] forbidden = { "host", "server", "user", "pass", "pwd", "secret", "credential", "token", "connectionstring", "dsn", "filepath", "port" };
        foreach (var type in new[] { typeof(DatabaseDto), typeof(ParameterDto), typeof(DatabaseMergeSettings), typeof(QueryParameterSetting), typeof(DatabaseQueryReference) })
            foreach (var p in type.GetProperties())
                foreach (string bad in forbidden)
                    Assert.DoesNotContain(bad, p.Name, StringComparison.OrdinalIgnoreCase);

        // A hand-edited file that tries to smuggle such fields in has them dropped on the next save.
        string hostile = json.Replace("\"connectionName\"", "\"host\": \"evil.example\", \"password\": \"hunter2\", \"connectionName\"");
        var content = RotulaFileSerializer.DeserializeFile(hostile);
        string again = RotulaFileSerializer.Serialize(content.Document, content.Merge);
        Assert.DoesNotContain("evil.example", again);
        Assert.DoesNotContain("hunter2", again);
    }

    [Fact]
    public void TheFormatVersion_IsFourOnlyWhenADatabaseReferenceIsPresent_AndOlderFilesOpenUnchanged()
    {
        var doc = _main.Session.Document;
        string FormatOf(DataMergeSettings? m) => JsonDocument.Parse(RotulaFileSerializer.Serialize(doc, m)).RootElement.GetProperty("formatVersion").ToString();

        Assert.Equal("1", FormatOf(null));
        Assert.Equal("2", FormatOf(new DataMergeSettings { MaxRecordsPerJob = 5 }));
        Assert.Equal("4", FormatOf(WithQuery("Warehouse", "SELECT 1")));
        Assert.Equal(RotulaFileSerializer.TextFitFormatVersion, RotulaFileSerializer.CurrentFormatVersion);

        // Raw ZPL needs version 3; together with a database reference the file must still say 4, never be lowered to 3.
        doc.Elements.Add(new RawZplElement { Zpl = "^FO10,10^FDx^FS" });
        Assert.Equal("3", FormatOf(null));
        Assert.Equal("4", FormatOf(WithQuery("Warehouse", "SELECT 1")));

        // Files written by older builds have no database section and load as before.
        var v2 = RotulaFileSerializer.DeserializeFile("{\"formatVersion\":2,\"label\":{},\"elements\":[],\"merge\":{\"maxRecordsPerJob\":5}}");
        Assert.Null(v2.Merge!.Database);
        Assert.Equal(5, v2.Merge.MaxRecordsPerJob);
        Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\":" + (RotulaFileSerializer.CurrentFormatVersion + 1) + "}"));
    }

    [Fact]
    public void ADatabaseSectionFromAFile_IsValidatedAsUntrustedInput()
    {
        string Section(string inner) => "{\"formatVersion\":4,\"label\":{},\"elements\":[],\"merge\":{\"database\":" + inner + "}}";
        Assert.Null(RotulaFileSerializer.DeserializeFile(Section("{\"connectionName\":\"\",\"query\":\"SELECT 1\"}")).Merge!.Database);
        Assert.Null(RotulaFileSerializer.DeserializeFile(Section("{\"connectionName\":\"x\",\"query\":\"\"}")).Merge!.Database);
        Assert.Null(RotulaFileSerializer.DeserializeFile(Section("{\"connectionName\":\"" + new string('a', 101) + "\",\"query\":\"SELECT 1\"}")).Merge!.Database);
        Assert.Null(RotulaFileSerializer.DeserializeFile(Section("{\"connectionName\":\"x\",\"query\":\"" + new string('a', SqlGuard.MaxQueryLength + 1) + "\"}")).Merge!.Database);

        var ok = RotulaFileSerializer.DeserializeFile(Section("{\"connectionName\":\"x\",\"query\":\"SELECT @a\",\"maxRows\":99999999,\"parameters\":[{\"name\":\"a\",\"value\":\"1\",\"type\":\"bogus\"},{\"name\":\"\",\"value\":\"2\"}]}")).Merge!.Database!;
        Assert.Equal(DatabaseSource.HardMaxRows, ok.MaxRows);
        var p = Assert.Single(ok.Parameters);
        Assert.Equal("text", p.Type);
    }

    // ------------------------------------------------------------------------------------ the connection manager

    private ConnectionManagerViewModel Manager() => new(_fake.Database, _fake.Build().Messages, _fake.Build().Files);

    [Fact]
    public async Task Manager_SavesANewConnection_WithThePasswordGoingOnlyToTheStore()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Plant 2";
        m.Provider = DatabaseProvider.PostgreSql;
        Assert.Equal(DatabaseAuthentication.UserPassword, m.Authentication);   // the usual way to sign in to PostgreSQL
        m.Host = "pg.local";
        m.PortText = "5433";
        m.Database = "labels";
        m.UserName = "reader";
        m.Password = "pw-1";
        await m.SaveAsync();

        var saved = _store.Entries["Plant 2"];
        Assert.Equal("pg.local", saved.Settings.Host);
        Assert.Equal(5433, saved.Settings.Port);
        Assert.Equal("reader", saved.Settings.UserName);
        Assert.Equal("pw-1", saved.Password);
        Assert.Equal("Plant 2", m.LastSavedName);
        Assert.False(m.IsNew);
        Assert.Equal(string.Empty, m.Password);                                // never kept after saving
        Assert.True(m.HasSavedPassword);
        Assert.False(m.CanEditName);                                          // labels refer to the name
    }

    [Fact]
    public async Task Manager_AnEmptyPasswordKeepsTheSavedOne_AndSwitchingToWindowsSignInRemovesIt()
    {
        var m = Manager();
        await m.LoadAsync("Warehouse");
        Assert.True(m.HasSavedPassword);
        Assert.Equal(string.Empty, m.Password);                                // the saved password is never shown
        Assert.Equal(SecretUser, m.UserName);

        m.Host = "other-host";
        await m.SaveAsync();
        Assert.Null(_store.Saves.Last().Password);                             // null = keep
        Assert.Equal(SecretPassword, _store.Entries["Warehouse"].Password);

        m.Authentication = DatabaseAuthentication.WindowsIntegrated;
        await m.SaveAsync();
        Assert.Equal(string.Empty, _store.Saves.Last().Password);              // empty = remove: no stale secret lingers
        Assert.Null(_store.Entries["Warehouse"].Password);
    }

    [Fact]
    public async Task Manager_ChecksTheFormBeforeSaving_WithPlainMessages()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        await m.SaveAsync();
        Assert.Contains("Type a name", m.FormError);

        m.Name = "warehouse";
        await m.SaveAsync();
        Assert.Contains("already exists", m.FormError);

        m.Name = "Plant 3";
        m.PortText = "70000";
        await m.SaveAsync();
        Assert.Contains("port", m.FormError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plant 3", _store.Entries.Keys);
    }

    [Fact]
    public async Task Manager_TestUsesTheTypedPassword_AndSavesNothing()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Try";
        m.Host = "h";
        m.Authentication = DatabaseAuthentication.UserPassword;
        m.UserName = "u";
        m.Password = "typed";
        await m.TestAsync();

        var (settings, password) = Assert.Single(_fake.Database.Created);
        Assert.Equal("typed", password);
        Assert.Equal("h", settings.Host);
        Assert.Equal(1, _fake.Database.CreatedSource.Tests);
        Assert.True(m.HasTestSuccess);
        Assert.Equal("The connection works.", m.TestText);
        Assert.DoesNotContain("Try", _store.Entries.Keys);
    }

    [Fact]
    public async Task Manager_TestOfAnUnchangedSavedConnection_UsesTheStore_ButAnEditedOneAsksForThePasswordAgain()
    {
        var m = Manager();
        await m.LoadAsync("Warehouse");
        await m.TestAsync();
        Assert.Equal("Warehouse", Assert.Single(_store.Opened));
        Assert.Empty(_fake.Database.Created);
        Assert.Equal(1, _source.Tests);

        m.Host = "changed";
        await m.TestAsync();
        Assert.True(m.HasTestError);
        Assert.Contains("Type the password in the Password box", m.TestText);
        Assert.Single(_store.Opened);
        Assert.Empty(_fake.Database.Created);
    }

    [Fact]
    public async Task Manager_ACertificateError_OffersToTrustTheServer_AndSavesIt()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Lab";
        m.Host = "lab-sql";
        m.Authentication = DatabaseAuthentication.WindowsIntegrated;
        _fake.Database.CreatedSource.Failure = new DatabaseException(DataKeys.CertificateNotTrusted, null, null, "chain error");
        await m.TestAsync();

        Assert.True(m.HasTestError);
        Assert.True(m.ShowTrustOffer);
        Assert.Contains("not trusted", m.TestText);
        Assert.Equal("chain error", m.TestDetail);
        Assert.False(m.TrustServerCertificate);                                // nothing is trusted until the user chooses it

        _fake.Database.CreatedSource.Failure = null;
        await m.TrustAndSaveCommand.ExecuteAsync(null);
        Assert.True(_store.Entries["Lab"].Settings.TrustServerCertificate);
        Assert.True(m.HasTestSuccess);
        Assert.False(m.ShowTrustOffer);
    }

    [Fact]
    public async Task Manager_WarnsBeforeEncryptionIsTurnedOff_AndWhileACertificateIsTrusted()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        Assert.True(m.Encrypt);
        Assert.False(m.ShowEncryptWarning);
        Assert.False(m.ShowTrustWarning);
        m.Encrypt = false;
        Assert.True(m.ShowEncryptWarning);
        m.TrustServerCertificate = true;
        Assert.True(m.ShowTrustWarning);

        m.Provider = DatabaseProvider.Sqlite;                                  // not applicable to a file
        Assert.False(m.ShowEncryptWarning);
        Assert.False(m.ShowTrustWarning);
        foreach (string lang in new[] { "en", "es" })
        {
            Loc.Instance.SetLanguage(lang);
            Assert.False(Loc.Get("Conn.Encrypt.Warning").StartsWith('!'));
            Assert.False(Loc.Get("Conn.Trust.Warning").StartsWith('!'));
            Assert.False(Loc.Get("Conn.Encrypt.Off").StartsWith('!'));
            Assert.False(Loc.Get("Conn.Trust.On").StartsWith('!'));
        }
    }

    [Fact]
    public async Task Manager_ShowsTheFieldsOfTheChosenEngine()
    {
        var m = Manager();
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Provider = DatabaseProvider.SqlServer;
        Assert.True(m.IsServer);
        Assert.True(m.CanWindowsAuth);
        Assert.False(m.ShowUserFields);                                        // Windows sign-in is the default there
        m.Authentication = DatabaseAuthentication.UserPassword;
        Assert.True(m.ShowUserFields);

        m.Provider = DatabaseProvider.MySql;
        Assert.False(m.CanWindowsAuth);                                        // MySQL / MariaDB has no Windows sign-in
        Assert.True(m.ShowUserFields);

        m.Provider = DatabaseProvider.Sqlite;
        Assert.True(m.IsSqlite);
        Assert.False(m.ShowAuth);
        _fake.DbFilePath = @"C:\data\labels.db";
        m.Fields.Single(f => f.Key == "filePath").Browse();     // the Browse button of the data-driven file field
        Assert.Equal(@"C:\data\labels.db", m.FilePath);

        m.Provider = DatabaseProvider.Odbc;
        Assert.True(m.IsOdbc);
        Assert.True(m.CanWindowsAuth);
    }

    [Fact]
    public async Task Manager_DeleteAsksFirst_ThenRemovesTheConnection()
    {
        var m = Manager();
        await m.LoadAsync("Warehouse");
        _fake.ConfirmAnswer = false;
        await m.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("Warehouse", _store.Entries.Keys);
        Assert.Contains("Warehouse", Assert.Single(_fake.Confirms));

        _fake.ConfirmAnswer = true;
        await m.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(_store.Entries);
        Assert.True(m.HasNoItems);
        Assert.True(m.IsNew);
    }

    [Fact]
    public async Task Manager_FromTheDataWindow_SelectsTheConnectionThatWasSaved()
    {
        await Db.RefreshConnectionsAsync();
        // The manager is only recorded by the fake dialog service; simulate what the user does in it, then closing.
        _fake.ToolDialogs.OnManager = async vm =>
        {
            vm.NewConnectionCommand.Execute(null);
            vm.Name = "Added";
            vm.Provider = DatabaseProvider.Sqlite;
            vm.FilePath = @"C:\x\a.db";
            await vm.SaveAsync();
        };
        var data = new DataMergeViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => "x", _fake.Build().Files, _fake.Build().Messages, _fake.Database, _fake.ToolDialogs);
        await data.Database.RefreshConnectionsAsync();
        await data.Database.ManageConnectionsCommand.ExecuteAsync(null);

        Assert.Equal("Added", data.Database.SelectedConnection?.Name);
        Assert.Equal(2, data.Database.Connections.Count);
    }

    [Fact]
    public async Task AStoreProblem_IsShownInPlainLanguage()
    {
        _store.ErrorKey = DataKeys.StoreDamaged;
        await Db.RefreshConnectionsAsync();
        Assert.True(Db.HasError);
        Assert.Contains("damaged", Db.ResultText);
        Assert.DoesNotContain("Data.Store", Db.ResultText);
    }
}
