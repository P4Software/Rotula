using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// The database tools with databases switched ON: only saved connections, only read-only queries, capped rows. A throw-away
/// SQLite file stands in for a real server (no network, no credentials).
/// </summary>
public sealed class McpDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-mcp-db-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    private readonly McpTestEnv _env;

    public McpDatabaseTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "shop.db");
        using (var c = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE Orders (Id INTEGER PRIMARY KEY, Customer TEXT, Sku TEXT);");
            using var tx = c.BeginTransaction();
            for (int i = 1; i <= 1500; i++)
                Exec(c, $"INSERT INTO Orders VALUES ({i}, 'Customer {i}', 'SKU-{i:D5}');", tx);
            tx.Commit();
        }

        var store = new JsonConnectionStore(Path.Combine(_dir, "connections.json"));
        store.SaveAsync(new DatabaseConnectionSettings { Name = "shop", Provider = DatabaseProvider.Sqlite, FilePath = _db }, null).GetAwaiter().GetResult();
        _env = new McpTestEnv(s => s with { AllowDatabase = true }, connections: () => store);
    }

    public void Dispose()
    {
        _env.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Count()
    {
        using var c = new SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Orders";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task ConnectionsAreListedByNameOnly()
    {
        var list = await _env.Data.ListDatabaseConnections();
        var c = Assert.Single(list.Connections);
        Assert.Equal("shop", c.Name);
        Assert.Equal("Sqlite", c.Provider);
        Assert.DoesNotContain(_db, System.Text.Json.JsonSerializer.Serialize(list)); // no file path, host or user leaks
    }

    [Fact]
    public async Task ASelectReturnsRows()
    {
        var r = await _env.Data.RunQueryTool("shop", "SELECT Id, Customer FROM Orders WHERE Id <= 3 ORDER BY Id");
        Assert.Equal(["Id", "Customer"], r.Columns);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal("Customer 2", r.Rows[1][1]);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task AConnectionWhoseAddOnIsNotInstalled_IsRefusedWithAPlainSentence_BeforeAnyConnectionIsOpened()
    {
        var fake = new FakeConnectionStore();
        fake.Add(new DatabaseConnectionSettings { Name = "ledger", Provider = DatabaseProvider.Custom, ProviderId = "no-such-pack", Host = "db.example.com" });
        using var env = new McpTestEnv(s => s with { AllowDatabase = true }, connections: () => fake);
        var ex = await Assert.ThrowsAsync<McpException>(() => env.Data.RunQueryTool("ledger", "SELECT 1"));
        Assert.Contains("no-such-pack", ex.Message);
        Assert.Empty(fake.Opened);
    }

    [Fact]
    public async Task AnUnknownConnectionIsRefused()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _env.Data.RunQueryTool("nope", "SELECT 1"));
        Assert.Contains("no saved connection", ex.Message);
        await Assert.ThrowsAsync<McpException>(() => _env.Data.RunQueryTool("", "SELECT 1"));
        await Assert.ThrowsAsync<McpException>(() => _env.Data.RunQueryTool("shop", ""));
    }

    [Theory]
    [InlineData("DELETE FROM Orders")]
    [InlineData("DROP TABLE Orders")]
    [InlineData("UPDATE Orders SET Customer = 'x'")]
    [InlineData("INSERT INTO Orders VALUES (9999, 'a', 'b')")]
    [InlineData("SELECT 1; DELETE FROM Orders")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM Orders")]
    [InlineData("PRAGMA writable_schema = 1")]
    [InlineData("ATTACH DATABASE 'other.db' AS o")]
    [InlineData("SELECT * FROM Orders WHERE Id = @id")]
    [InlineData("SELECT load_extension('x')")]
    [InlineData("-- just a comment")]
    public async Task AnythingButASingleReadOnlySelectIsRefused_AndTheTableIsUntouched(string sql)
    {
        long before = Count();
        await Assert.ThrowsAsync<McpException>(() => _env.Data.RunQueryTool("shop", sql));
        Assert.Equal(before, Count());
    }

    [Fact]
    public async Task RowsAreCappedAndTheAnswerSaysSo()
    {
        var r = await _env.Data.RunQueryTool("shop", "SELECT Id FROM Orders ORDER BY Id");
        Assert.Equal(1000, r.Rows.Count);
        Assert.True(r.Truncated);
        Assert.Equal(1000, r.RowLimit);
        Assert.Contains("1000", r.Note);
    }

    [Fact]
    public async Task AskingForMoreRowsThanTheCapIsClampedDown()
    {
        var r = await _env.Data.RunQueryTool("shop", "SELECT Id FROM Orders", maxRows: 5000);
        Assert.Equal(1000, r.Rows.Count);
        var small = await _env.Data.RunQueryTool("shop", "SELECT Id FROM Orders", maxRows: 5);
        Assert.Equal(5, small.Rows.Count);
        Assert.True(small.Truncated);
    }

    [Fact]
    public async Task ALowerCapInTheSettingsWins()
    {
        using var env = new McpTestEnv(s => s with { AllowDatabase = true, MaxDatabaseRows = 7 },
            connections: () => new JsonConnectionStore(Path.Combine(_dir, "connections.json")));
        var r = await env.Data.RunQueryTool("shop", "SELECT Id FROM Orders", maxRows: 500);
        Assert.Equal(7, r.Rows.Count);
    }

    [Fact]
    public void MergePreviewFillsADataFieldFromRowNOfAQuery()
    {
        _env.Label.CreateLabel(100, 50);
        _env.Elements.AddDataField("Customer", 5, 5);
        _env.Elements.AddBarcode("Code128", "{{Sku}}", 5, 20);
        var r = _env.Preview.MergePreview(connection: "shop", query: "SELECT Customer, Sku FROM Orders ORDER BY Id", record: 42);
        var merge = r.StructuredContent!.Value.GetProperty("merge");
        Assert.Equal("Customer 42", merge.GetProperty("record").GetProperty("Customer").GetString());
        Assert.Contains("SKU-00042", merge.GetProperty("zpl").GetString());
        Assert.Equal(1000, merge.GetProperty("recordCount").GetInt32()); // the row cap applies to previews too
        Assert.Throws<McpException>(() => _env.Preview.MergePreview(connection: "shop", query: "DELETE FROM Orders"));
        Assert.Equal(1500, Count());
    }
}
