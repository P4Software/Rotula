using System.Security.Cryptography;
using System.Text;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>The connection store: DPAPI round trip, no clear-text secrets on disk, hostile or damaged files.</summary>
public class DataConnectionStoreTests : IDisposable
{
    private const string Pw = "Tr0ub4dor&3-unique-marker";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "Rotula", "connections.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp cleanup is best effort */ }
    }

    private static DatabaseConnectionSettings Sql(string name = "prod") => new()
    {
        Name = name, Provider = DatabaseProvider.SqlServer, Host = "db.example.com", Database = "Labels",
        Authentication = DatabaseAuthentication.UserPassword, UserName = "reader",
    };

    /// <summary>Wraps real DPAPI and remembers what was decrypted, so tests can prove the password came back.</summary>
    private sealed class SpyProtector : ISecretProtector
    {
        private readonly DpapiSecretProtector _inner = new();
        public List<string> Unprotected { get; } = new();
        public List<string> Entropies { get; } = new();
        public byte[] Protect(byte[] data, byte[] entropy) { Entropies.Add(Encoding.UTF8.GetString(entropy)); return _inner.Protect(data, entropy); }
        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            var plain = _inner.Unprotect(protectedData, entropy);
            Unprotected.Add(Encoding.UTF8.GetString(plain));
            return plain;
        }
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, byte[] entropy) => data;
        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => throw new CryptographicException("different user");
    }

    [Fact]
    public void Dpapi_RoundTrips_AndBindsToEntropy()
    {
        var p = new DpapiSecretProtector();
        byte[] blob = p.Protect(Encoding.UTF8.GetBytes(Pw), StoreEntropy.For("a"));
        Assert.DoesNotContain(Pw, Convert.ToBase64String(blob));
        Assert.Equal(Pw, Encoding.UTF8.GetString(p.Unprotect(blob, StoreEntropy.For("a"))));
        Assert.Equal(Pw, Encoding.UTF8.GetString(p.Unprotect(blob, StoreEntropy.For("A")))); // names compare case-insensitively
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(blob, StoreEntropy.For("b")));
    }

    [Fact]
    public async Task Save_Load_RoundTrips_WithoutAnyClearTextPassword()
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        await store.SaveAsync(Sql(), Pw);
        await store.SaveAsync(Sql("second") with { Provider = DatabaseProvider.Sqlite, FilePath = @"C:\x.db", Host = null, Database = null }, null);

        string text = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain(Pw, text);
        Assert.DoesNotContain("Tr0ub4dor", text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Pw)), text);
        Assert.Contains("protectedPassword", text, StringComparison.OrdinalIgnoreCase);

        var content = await store.LoadAsync();
        Assert.Null(content.ErrorKey);
        Assert.Equal(2, content.Connections.Count);
        var prod = content.Connections.Single(c => c.Settings.Name == "prod");
        Assert.True(prod.HasPassword);
        Assert.Equal(Sql(), prod.Settings);
        Assert.False(content.Connections.Single(c => c.Settings.Name == "second").HasPassword);

        // Opening unprotects the password internally and hands back a usable source.
        var source = await store.OpenAsync("PROD");
        Assert.Equal("prod", source.Settings.Name);
        Assert.Equal(new[] { Pw }, spy.Unprotected);
        Assert.Contains("Rotula.Connections.v1|prod", spy.Entropies);
    }

    [Fact]
    public async Task Save_NullPasswordKeepsExisting_EmptyRemoves_ReplaceOverwrites()
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        await store.SaveAsync(Sql(), Pw);
        await store.SaveAsync(Sql() with { Port = 1444 }, null);
        Assert.True((await store.LoadAsync()).Connections.Single().HasPassword);
        await store.OpenAsync("prod");
        Assert.Equal(Pw, spy.Unprotected.Last());

        await store.SaveAsync(Sql(), "newer");
        await store.OpenAsync("prod");
        Assert.Equal("newer", spy.Unprotected.Last());

        await store.SaveAsync(Sql(), "");
        Assert.False((await store.LoadAsync()).Connections.Single().HasPassword);
        Assert.Single((await store.LoadAsync()).Connections); // same name replaced, not duplicated
    }

    [Fact]
    public async Task OdbcPasswordInRawString_IsMovedToTheProtectedSlot()
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        var s = new DatabaseConnectionSettings
        {
            Name = "odbc", Provider = DatabaseProvider.Odbc,
            OdbcConnectionString = "Driver={SQL Server};Server=s;Uid=u;Pwd=" + Pw,
        };
        await store.SaveAsync(s, null);
        string text = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain(Pw, text);
        Assert.DoesNotContain("Pwd", text, StringComparison.OrdinalIgnoreCase);
        var saved = (await store.LoadAsync()).Connections.Single();
        Assert.True(saved.HasPassword);
        Assert.DoesNotContain("Pwd", saved.Settings.OdbcConnectionString!, StringComparison.OrdinalIgnoreCase);
        await store.OpenAsync("odbc");
        Assert.Equal(Pw, spy.Unprotected.Single());
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var store = new JsonConnectionStore(FilePath, new SpyProtector());
        await store.SaveAsync(Sql(), "x");
        Assert.False(await store.DeleteAsync("nope"));
        Assert.True(await store.DeleteAsync("PROD"));
        Assert.Empty((await store.LoadAsync()).Connections);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => store.OpenAsync("prod"));
        Assert.Equal(DataKeys.ConnectionNotFound, ex.Key);
    }

    [Fact]
    public async Task MissingFile_IsAnEmptyStore()
    {
        var content = await new JsonConnectionStore(FilePath, new SpyProtector()).LoadAsync();
        Assert.Empty(content.Connections);
        Assert.Null(content.ErrorKey);
    }

    [Fact]
    public async Task PasswordFromAnotherUser_GivesAPlainKey_NotACrash()
    {
        var good = new JsonConnectionStore(FilePath, new SpyProtector());
        await good.SaveAsync(Sql(), Pw);
        var other = new JsonConnectionStore(FilePath, new BrokenProtector());
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => other.OpenAsync("prod"));
        Assert.Equal(DataKeys.StorePasswordUnreadable, ex.Key);
        Assert.DoesNotContain(Pw, ex.ToString());
    }

    // ---- hostile files ------------------------------------------------------------------------------------

    private async Task<ConnectionStoreContent> LoadRaw(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, content);
        return await new JsonConnectionStore(FilePath, new SpyProtector()).LoadAsync();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":99,\"connections\":[]}")]
    [InlineData("{\"version\":\"1\",\"connections\":[]}")]
    [InlineData("{\"version\":1,\"connections\":{}}")]
    [InlineData("{\"version\":1,\"connections\":[ // comment\n]}")]
    public async Task DamagedFile_ReturnsKey_AndEmptyList(string text)
    {
        var c = await LoadRaw(text);
        Assert.Equal(DataKeys.StoreDamaged, c.ErrorKey);
        Assert.Empty(c.Connections);
    }

    [Fact]
    public async Task OversizedFile_IsIgnoredWithoutBeingParsed()
    {
        var c = await LoadRaw("{\"version\":1,\"connections\":[],\"pad\":\"" + new string('x', (int)JsonConnectionStore.MaxFileBytes + 10) + "\"}");
        Assert.Equal(DataKeys.StoreTooLarge, c.ErrorKey);
        Assert.Empty(c.Connections);
    }

    [Fact]
    public async Task DeeplyNestedJson_IsRejected()
    {
        var c = await LoadRaw("{\"version\":1,\"connections\":[" + string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200)) + "]}");
        Assert.Equal(DataKeys.StoreDamaged, c.ErrorKey);
    }

    [Fact]
    public async Task TooManyEntries_IsDamaged()
    {
        string entries = string.Join(",", Enumerable.Range(0, JsonConnectionStore.MaxEntries + 1)
            .Select(i => $"{{\"name\":\"n{i}\",\"provider\":\"Sqlite\",\"filePath\":\"x.db\"}}"));
        var c = await LoadRaw("{\"version\":1,\"connections\":[" + entries + "]}");
        Assert.Equal(DataKeys.StoreDamaged, c.ErrorKey);
    }

    [Fact]
    public async Task InvalidEntries_AreSkipped_ValidOnesKept()
    {
        string json = """
        {"version":1,"connections":[
          {"name":"ok","provider":"Sqlite","filePath":"C:\\x.db"},
          {"name":"inject","provider":"SqlServer","host":"evil;Encrypt=false","authentication":"WindowsIntegrated"},
          {"name":"badprovider","provider":"Oracle","host":"h"},
          {"name":"badenum","provider":3,"filePath":"x"},
          {"name":"ok","provider":"Sqlite","filePath":"duplicate.db"},
          {"provider":"Sqlite","filePath":"noname.db"},
          {"name":"timeout","provider":"Sqlite","filePath":"x.db","commandTimeoutSeconds":0},
          {"name":"badblob","provider":"Sqlite","filePath":"x.db","protectedPassword":"%%%not base64%%%"},
          {"name":"clearpwd","provider":"Odbc","odbcConnectionString":"Driver={X};Pwd=plain"},
          "just a string",
          42
        ]}
        """;
        var c = await LoadRaw(json);
        Assert.Null(c.ErrorKey);
        Assert.Equal("ok", Assert.Single(c.Connections).Settings.Name);
        Assert.Equal(@"C:\x.db", c.Connections[0].Settings.FilePath);
        Assert.Equal(10, c.SkippedEntries);
    }

    [Fact]
    public async Task SavingOverADamagedFile_KeepsACopy_AndStartsFresh()
    {
        var damaged = await LoadRaw("this is not json");
        Assert.Equal(DataKeys.StoreDamaged, damaged.ErrorKey);
        var store = new JsonConnectionStore(FilePath, new SpyProtector());
        await store.SaveAsync(Sql(), Pw);
        Assert.Equal("this is not json", await File.ReadAllTextAsync(FilePath + ".damaged"));
        Assert.Single((await store.LoadAsync()).Connections);
    }

    [Fact]
    public async Task SaveRejectsInvalidSettings_AndWritesNothing()
    {
        var store = new JsonConnectionStore(FilePath, new SpyProtector());
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => store.SaveAsync(Sql() with { Host = "a;b" }, Pw));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Writes_AreAtomic_NoTempFilesLeftBehind()
    {
        var store = new JsonConnectionStore(FilePath, new SpyProtector());
        for (int i = 0; i < 5; i++) await store.SaveAsync(Sql("c" + i), "p" + i);
        Assert.Equal(new[] { "connections.json" }, Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(Path.GetFileName).ToArray());
        Assert.Equal(5, (await store.LoadAsync()).Connections.Count);
    }

    [Fact]
    public void DefaultPath_IsUnderAppData()
    {
        Assert.EndsWith(Path.Combine("Rotula", "connections.json"), JsonConnectionStore.DefaultPath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), JsonConnectionStore.DefaultPath);
    }
}
