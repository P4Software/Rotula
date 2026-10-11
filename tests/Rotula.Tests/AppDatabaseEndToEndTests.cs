using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Data;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>Stands in for Windows DPAPI so the real store runs anywhere; SQLite connections have no password anyway.</summary>
internal sealed class PlainProtector : ISecretProtector
{
    public byte[] Protect(byte[] data, byte[] entropy) => data.Reverse().ToArray();
    public byte[] Unprotect(byte[] protectedData, byte[] entropy) => protectedData.Reverse().ToArray();
}

/// <summary>
/// One real run through the view-models: a temporary SQLite file, the real connection store and the real database source
/// (no fakes, no network). Browse a table, query with a parameter, feed the merge pipeline, check the records and the ZPL,
/// then save the label, reopen it, and reload from the saved reference.
/// </summary>
[Collection("App")]
public class AppDatabaseEndToEndTests : IDisposable
{
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly string _db;
    private readonly FakeServices _fake = new();

    public AppDatabaseEndToEndTests()
    {
        _fake.Settings.Current.Language = "en";
        _db = Path.Combine(_dir, "stock.db");
        // Pooling=False so the file is released the moment the connection closes (a pooled handle would block the cleanup on Windows).
        using var c = new SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Qty INTEGER, Price REAL);
            INSERT INTO Products VALUES (1, 'Tuerca', 5, 0.25);
            INSERT INTO Products VALUES (2, 'Perno', 7, 1.5);
            INSERT INTO Products VALUES (3, 'Arandela', 2, 0.1);
            INSERT INTO Products VALUES (4, 'Tornillo ' || char(241), 9, 2);
            CREATE VIEW Busy AS SELECT Name, Qty FROM Products WHERE Qty > 4;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Loc.Instance.SetLanguage("en");
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp cleanup is best effort */ }
    }

    private MainViewModel NewMain(IConnectionStore store)
    {
        var services = _fake.Build() with { Database = new DatabaseServices(store) };
        return new MainViewModel(services);
    }

    private static async Task Idle(DatabaseSourceViewModel d)
    {
        for (int i = 0; i < 1000 && d.IsBusy; i++) await Task.Delay(10);
        Assert.False(d.IsBusy);
    }

    [Fact]
    public async Task BrowseATable_QueryWithAParameter_FeedTheMerge_AndSaveAndReloadTheLabel()
    {
        var store = new JsonConnectionStore(Path.Combine(_dir, "connections.json"), new PlainProtector());
        await store.SaveAsync(new DatabaseConnectionSettings { Name = "Local stock", Provider = DatabaseProvider.Sqlite, FilePath = _db }, null);

        var main = NewMain(store);
        main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{Name}} x{{Qty}}" }));
        var data = main.Data;
        var db = data.Database;

        // --- pick the saved connection and browse
        data.IsDatabaseMode = true;
        await db.RefreshConnectionsAsync();
        Assert.Equal("Local stock", Assert.Single(db.Connections).Name);
        await db.SelectConnectionAsync("Local stock");
        Assert.Equal(new[] { "Busy", "Products" }, db.Tables.Select(t => t.Name).ToArray());
        db.TableFilter = "prod";
        db.SelectedTable = Assert.Single(db.Tables);
        await Idle(db);
        Assert.Equal(new[] { "Id", "Name", "Qty", "Price" }, db.GridColumns);
        Assert.Equal(4, db.GridRows.Count);

        // --- use the whole table
        await db.UseDataAsync();
        Assert.True(data.HasData);
        Assert.Equal(4, data.RecordCount);
        Assert.Equal("Tornillo ñ", data.Table!.Rows[3][1]);
        Assert.Equal("Tuerca", data.CurrentData!["Name"]);

        // --- now a query with a number parameter
        data.ChangeDatabaseSourceCommand.Execute(null);
        db.ModeIndex = 1;
        db.QueryText = "SELECT Name, Qty FROM Products WHERE Qty >= @min ORDER BY Id";
        var p = Assert.Single(db.Parameters);
        Assert.Equal("min", p.Name);
        p.Type = "number";
        p.Value = "5";
        await db.RunQueryAsync();
        Assert.False(db.HasError, db.ResultText);
        Assert.Equal(3, db.GridRows.Count);
        await db.UseDataAsync();

        Assert.Equal(3, data.RecordCount);
        Assert.Equal(new[] { "Name", "Qty" }, data.PreviewColumns);
        Assert.Equal("Tuerca", data.CurrentData!["Name"]);
        data.CurrentIndex = 1;
        Assert.Equal("Perno", data.CurrentData!["Name"]);
        Assert.Equal("7", data.CurrentData["Qty"]);

        // --- the generated ZPL carries the database values
        var report = new MergeReport();
        string zpl = MergeBatch.GenerateSingleStream(main.Session.Document, data.DataSet!, report);
        Assert.Contains("Tuerca x5", zpl);
        Assert.Contains("Perno x7", zpl);
        Assert.Contains("Tornillo", zpl);
        Assert.DoesNotContain("Arandela", zpl);   // filtered out by the query (Qty 2 < 5)
        Assert.Equal(3, report.Emitted);

        // --- save the label: the file knows the connection NAME and the query, never the file path of the database
        string label = Path.Combine(_dir, "label.rotula");
        RotulaFileSerializer.Save(main.Session.Document, label, data.ToSettings());
        string json = File.ReadAllText(label);
        Assert.Contains("Local stock", json);
        Assert.Equal("SELECT Name, Qty FROM Products WHERE Qty >= @min ORDER BY Id",
            System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("merge").GetProperty("database").GetProperty("query").GetString());
        Assert.DoesNotContain(_db.Replace("\\", "\\\\"), json);
        Assert.DoesNotContain("stock.db", json);

        // --- reopen in a fresh app: nothing is read until the button is clicked, then the same records come back
        var again = NewMain(store);
        Assert.True(again.LoadFile(label));
        Assert.False(again.Data.HasData);
        Assert.True(again.Data.Database.IsReferencePending);
        await again.Data.Database.RefreshConnectionsAsync();
        Assert.False(again.Data.Database.ReferenceMissing);
        await again.Data.Database.ConnectReferenceAsync();
        Assert.True(again.Data.HasData, again.Data.Database.ResultText);
        Assert.Equal(3, again.Data.RecordCount);
        Assert.Equal("Tuerca", again.Data.CurrentData!["Name"]);
        Assert.Equal("5", Assert.Single(again.Data.Database.Parameters).Value);
    }

    /// <summary>The real store with a recording factory, so the test can see which path "Test connection" took.</summary>
    private sealed class StoreBackedServices : IDatabaseServices
    {
        public StoreBackedServices(IConnectionStore store) => Connections = new RecordingStore(store, Created);

        /// <summary>Tests of edited settings with an explicit password (typed, or "" for none). A test that reuses the saved password is not listed.</summary>
        public readonly List<(DatabaseConnectionSettings Settings, string? Password)> Created = new();
        public IConnectionStore Connections { get; }

        public IDatabaseSource Create(DatabaseConnectionSettings settings, string? password)
        {
            Created.Add((settings, password));
            return DatabaseSource.Create(settings, password);
        }
    }

    private sealed class RecordingStore(IConnectionStore inner, List<(DatabaseConnectionSettings Settings, string? Password)> created) : IConnectionStore
    {
        public Task<ConnectionStoreContent> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public Task SaveAsync(DatabaseConnectionSettings settings, string? password, CancellationToken cancellationToken = default) => inner.SaveAsync(settings, password, cancellationToken);
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);
        public Task<IDatabaseSource> OpenAsync(string name, CancellationToken cancellationToken = default) => inner.OpenAsync(name, cancellationToken);

        public Task<IDatabaseSource> OpenWithSettingsAsync(DatabaseConnectionSettings edited, string? newPassword, CancellationToken cancellationToken = default)
        {
            if (newPassword is not null) created.Add((edited, newPassword));
            return inner.OpenWithSettingsAsync(edited, newPassword, cancellationToken);
        }
    }

    [Fact]
    public async Task Manager_AnOdbcPasswordTypedInTheString_SurvivesEverySaveAndIsUsedByTheTest()
    {
        string path = Path.Combine(_dir, "connections.json");
        var store = new JsonConnectionStore(path, new PlainProtector());
        var services = new StoreBackedServices(store);
        var m = new ConnectionManagerViewModel(services, _fake.Build().Messages, _fake.Build().Files);
        await m.LoadAsync();
        m.NewConnectionCommand.Execute(null);
        m.Name = "Legacy";
        m.Provider = DatabaseProvider.Odbc;
        m.OdbcConnectionString = "DSN=RotulaNoSuchSource;Uid=reader;Pwd=s3cret-pw";

        // Testing a string that carries its own PWD= must not need anything from the store.
        await m.TestAsync();
        Assert.Equal(DatabaseProvider.Odbc, Assert.Single(services.Created).Settings.Provider);
        Assert.DoesNotContain("Type the password again", m.TestText);

        await m.SaveAsync();
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("s3cret-pw", json);           // the password never reaches the file in clear
        Assert.DoesNotContain("Pwd=", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single((await store.LoadAsync()).Connections).HasPassword);
        Assert.True(m.HasSavedPassword);

        // Saving again, changing only an unrelated field, must keep the password (the stripped string has nothing to re-extract).
        m.Database = "inventory";
        await m.SaveAsync();
        Assert.True(Assert.Single((await store.LoadAsync()).Connections).HasPassword);
        await m.SaveAsync();
        Assert.True(Assert.Single((await store.LoadAsync()).Connections).HasPassword);

        // An unchanged saved ODBC entry is tested through the store (which holds the password), not with no password at all.
        int created = services.Created.Count;
        await m.TestAsync();
        Assert.Equal(created, services.Created.Count);
        Assert.DoesNotContain("Type the password again", m.TestText);

        // Editing it and leaving the password out is reported, not silently tested without one.
        m.Dsn = "SomethingElse";
        await m.TestAsync();
        Assert.Contains("Type the password in the Password box", m.TestText);

        // A new PWD= typed into the string replaces the saved password.
        m.OdbcConnectionString = "DSN=RotulaNoSuchSource;Uid=reader;Pwd=another-pw";
        await m.SaveAsync();
        Assert.DoesNotContain("another-pw", File.ReadAllText(path));
        Assert.True(Assert.Single((await store.LoadAsync()).Connections).HasPassword);
    }

    [Fact]
    public async Task ARowCapSmallerThanTheTable_WarnsThatTheResultWasCut()
    {
        var store = new JsonConnectionStore(Path.Combine(_dir, "connections.json"), new PlainProtector());
        await store.SaveAsync(new DatabaseConnectionSettings { Name = "Local", Provider = DatabaseProvider.Sqlite, FilePath = _db }, null);
        var main = NewMain(store);
        var db = main.Data.Database;
        await db.RefreshConnectionsAsync();
        await db.SelectConnectionAsync("Local");
        db.ModeIndex = 1;
        db.QueryText = "SELECT Name FROM Products ORDER BY Id";
        db.MaxRowsText = "2";
        await db.UseDataAsync();

        Assert.Equal(2, main.Data.RecordCount);
        Assert.True(db.HasTruncatedWarning);
        Assert.Contains("first 2 rows", main.Data.LoadWarnings.Single());
    }

    [Fact]
    public async Task AWriteQueryIsRefused_AndTheFileIsUntouched()
    {
        var store = new JsonConnectionStore(Path.Combine(_dir, "connections.json"), new PlainProtector());
        await store.SaveAsync(new DatabaseConnectionSettings { Name = "Local", Provider = DatabaseProvider.Sqlite, FilePath = _db }, null);
        var main = NewMain(store);
        var db = main.Data.Database;
        await db.RefreshConnectionsAsync();
        await db.SelectConnectionAsync("Local");
        db.ModeIndex = 1;
        db.QueryText = "DELETE FROM Products";
        Assert.True(db.HasGuardMessage);
        await db.RunQueryAsync();
        Assert.True(db.HasError);
        Assert.False(main.Data.HasData);

        db.QueryText = "SELECT COUNT(*) AS N FROM Products";
        await db.UseDataAsync();
        Assert.Equal("4", main.Data.Table!.Rows[0][0]);   // all four rows are still there
    }
}

/// <summary>Every <c>Data.*</c> key that Rotula.Data can raise has a plain-language text in both languages.</summary>
[Collection("App")]
public class AppDatabaseLocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rotula.sln not found above the test folder.");
    }

    private static Dictionary<string, string> ReadResx(string language) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "Rotula.App", "Resources", $"Strings.{language}.resx")).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    [Fact]
    public void EveryDataKeyEmittedByTheLibrary_HasEnglishAndSpanishText()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        var keys = new HashSet<string>();
        string data = Path.Combine(RepoRoot(), "src", "Rotula.Data");
        foreach (var file in Directory.EnumerateFiles(data, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(Data\\.(?:Error|Query|Store)\\.[A-Za-z0-9.]+)\""))
                keys.Add(m.Groups[1].Value);

        Assert.True(keys.Count >= 30, "expected to find the Data keys, found " + keys.Count);
        Assert.Contains("Data.Error.CertificateNotTrusted", keys);
        Assert.Contains("Data.Query.Truncated", keys);
        Assert.Contains("Data.Store.ProtectionUnavailable", keys);
        Assert.Empty(keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k));
        Assert.Empty(keys.Where(k => !es.ContainsKey(k)).OrderBy(k => k));
        // The texts that take an argument keep the same holes in both languages (the general test checks all keys too).
        foreach (string k in keys)
            Assert.Equal(Regex.Matches(en[k], @"\{\d\}").Count, Regex.Matches(es[k], @"\{\d\}").Count);
    }

    [Fact]
    public void TheConnectionFieldNames_AreTranslated_ForEveryPropertyTheEngineCanReport()
    {
        var en = ReadResx("en");
        var es = ReadResx("es");
        foreach (var p in typeof(DatabaseConnectionSettings).GetProperties())
        {
            string key = "Conn.Field." + p.Name;
            Assert.True(en.ContainsKey(key) && es.ContainsKey(key), key);
        }
    }
}
