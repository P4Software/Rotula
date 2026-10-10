using Microsoft.Data.Sqlite;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Validation;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>Real end-to-end tests against a temporary SQLite file: browse, query, parameters, caps, streaming, merge goldens.</summary>
public sealed class DataSqliteEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;

    public DataSqliteEndToEndTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "labels.db");
        // Pooling=False so the file is released the moment the connection closes (a pooled handle would block deletion on Windows).
        using var c = new SqliteConnection($"Data Source={_db};Pooling=False");
        c.Open();
        Exec(c, """
            CREATE TABLE Products (
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Price REAL, Qty INTEGER, Added TEXT, Note TEXT, Active INTEGER, Photo BLOB);
            INSERT INTO Products VALUES (1, 'Caf' || char(233) || ' Ni' || char(241) || 'o', 12.5, 3, '2024-03-05', NULL, 1, x'00AB');
            INSERT INTO Products VALUES (2, 'Tuerca ' || char(241) || ' ' || char(191) || '?', 0.1, 10, '2024-03-06 14:30:00', 'urgent', 0, NULL);
            INSERT INTO Products VALUES (3, 'Bolt, "M8"', 7, 1, NULL, '', 1, NULL);
            CREATE VIEW ExpensiveProducts AS SELECT Id, Name FROM Products WHERE Price > 5;
            CREATE TABLE "we""ird name" (a TEXT);
            INSERT INTO "we""ird name" VALUES ('strange');
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp cleanup is best effort */ }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DatabaseConnectionSettings Settings() => new() { Name = "t", Provider = DatabaseProvider.Sqlite, FilePath = _db };

    private IDatabaseSource Source() => DatabaseSource.Create(Settings());

    // ---- browse ---------------------------------------------------------------------------------

    [Fact]
    public async Task TestConnection_Succeeds_ForAValidFile()
        => await Source().TestConnectionAsync();

    [Fact]
    public async Task TestConnection_MissingFile_ReportsDatabaseNotFound()
    {
        var src = DatabaseSource.Create(Settings() with { FilePath = Path.Combine(_dir, "nope.db") });
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.TestConnectionAsync());
        Assert.Equal(DataKeys.DatabaseNotFound, ex.Key);
    }

    [Fact]
    public async Task TestConnection_NotADatabase_ReportsAPlainError()
    {
        string junk = Path.Combine(_dir, "junk.db");
        await File.WriteAllTextAsync(junk, new string('x', 4096));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => DatabaseSource.Create(Settings() with { FilePath = junk }).TestConnectionAsync());
        Assert.Equal(DataKeys.ConnectionFailed, ex.Key);
    }

    [Fact]
    public async Task ListTables_ReturnsTablesAndViews_NotSystemObjects()
    {
        var list = await Source().ListTablesAsync();
        Assert.Equal(new[] { "ExpensiveProducts", "Products", "we\"ird name" }, list.Select(o => o.Name).ToArray());
        Assert.Equal(DatabaseObjectKind.View, list.Single(o => o.Name == "ExpensiveProducts").Kind);
        Assert.Equal(DatabaseObjectKind.Table, list.Single(o => o.Name == "Products").Kind);
        Assert.All(list, o => Assert.Null(o.Schema));
        Assert.Equal("Products", list.Single(o => o.Name == "Products").QualifiedName);
    }

    [Fact]
    public async Task Describe_ReturnsColumnsWithTypesAndNullability()
    {
        var cols = await Source().DescribeAsync("Products");
        Assert.Equal(new[] { "Id", "Name", "Price", "Qty", "Added", "Note", "Active", "Photo" }, cols.Select(c => c.Name).ToArray());
        Assert.Equal("TEXT", cols[1].DataType, ignoreCase: true);
        Assert.Equal(false, cols[1].AllowsNull);
        Assert.Equal(true, cols[5].AllowsNull);
        Assert.Equal(cols.Count, (await Source().DescribeAsync("[Products]")).Count);
        Assert.Equal(DataKeys.InvalidSettings, (await Assert.ThrowsAsync<DatabaseException>(() => Source().DescribeAsync("a.b.c"))).Key);
    }

    [Fact]
    public async Task Describe_UnknownTable_IsAQueryFailure()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().DescribeAsync("Missing"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
    }

    [Fact]
    public async Task Preview_QuotesHostileObjectNames()
    {
        var src = Source();
        var weird = (await src.ListTablesAsync()).Single(o => o.Name.StartsWith("we"));
        Assert.Equal("SELECT * FROM \"we\"\"ird name\"", src.BuildSelectAll(weird));
        var r = await src.PreviewAsync(weird);
        Assert.Equal(new[] { "strange" }, r.Table.Rows.Single());
        var evil = new DatabaseObject(null, "x\"; DROP TABLE Products; --", DatabaseObjectKind.Table);
        await Assert.ThrowsAsync<DatabaseException>(() => src.PreviewAsync(evil)); // just "no such table", never executed as SQL
        Assert.Equal("3", (await src.QueryAsync("SELECT COUNT(*) FROM Products")).Table.Rows[0][0]);
    }

    // ---- values -----------------------------------------------------------------------------------

    [Fact]
    public async Task Query_FormatsValuesByTheDocumentedRules()
    {
        var r = await Source().QueryAsync("SELECT Id, Name, Price, Qty, Added, Note, Active, Photo FROM Products ORDER BY Id");
        Assert.False(r.Truncated);
        Assert.Equal(new[] { "Id", "Name", "Price", "Qty", "Added", "Note", "Active", "Photo" }, r.Table.Columns);
        Assert.Equal(new[] { "1", "Café Niño", "12.5", "3", "2024-03-05", "", "1", "0x00AB" }, r.Table.Rows[0]);   // NULL -> "", BLOB -> hex
        Assert.Equal(new[] { "2", "Tuerca ñ ¿?", "0.1", "10", "2024-03-06 14:30:00", "urgent", "0", "" }, r.Table.Rows[1]);
        Assert.Equal(new[] { "3", "Bolt, \"M8\"", "7", "1", "", "", "1", "" }, r.Table.Rows[2]);
        Assert.Empty(r.Table.Issues);
    }

    [Fact]
    public async Task Query_ColumnNamesFollowTheReaderConventions()
    {
        var r = await Source().QueryAsync("SELECT Name AS \"  Name  \", Name, Id AS \"\", Qty AS name FROM Products LIMIT 1");
        Assert.Equal(new[] { "Name", "Name_2", "Column3", "name_3" }, r.Table.Columns);
    }

    [Fact]
    public async Task Query_Computed_NumbersAreCultureInvariant()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            var r = await Source().QueryAsync("SELECT 1234.5 AS a, 1.0/3 AS b, 1e21 AS c");
            Assert.Equal(new[] { "1234.5", "0.3333333333333333", "1E+21" }, r.Table.Rows[0]);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public async Task Query_EmptyResult_KeepsColumns()
    {
        var r = await Source().QueryAsync("SELECT Id, Name FROM Products WHERE 1 = 0");
        Assert.Equal(new[] { "Id", "Name" }, r.Table.Columns);
        Assert.Equal(0, r.Table.RowCount);
    }

    // ---- parameters -------------------------------------------------------------------------------

    [Fact]
    public async Task Parameters_AreBoundCaseInsensitively_AndReusableByName()
    {
        var p = new Dictionary<string, object?> { ["MINQTY"] = 2, ["@unused"] = "ignored" };
        var r = await Source().QueryAsync("SELECT Id FROM Products WHERE Qty >= @minQty AND Qty >= @MinQty ORDER BY Id", p);
        Assert.Equal(new[] { "1", "2" }, r.Table.Rows.Select(x => x[0]).ToArray());
    }

    [Fact]
    public async Task Parameters_ValuesAreNeverSql()
    {
        var src = Source();
        var attack = new Dictionary<string, object?> { ["n"] = "x'; DROP TABLE Products; --" };
        Assert.Equal(0, (await src.QueryAsync("SELECT * FROM Products WHERE Name = @n", attack)).Table.RowCount);
        var always = new Dictionary<string, object?> { ["n"] = "' OR '1'='1" };
        Assert.Equal(0, (await src.QueryAsync("SELECT * FROM Products WHERE Name = @n", always)).Table.RowCount);
        Assert.Equal(3, (await src.QueryAsync("SELECT * FROM Products")).Table.RowCount);
        var nullValue = new Dictionary<string, object?> { ["n"] = null };
        Assert.Equal(0, (await src.QueryAsync("SELECT * FROM Products WHERE Note = @n", nullValue)).Table.RowCount); // = NULL never matches
    }

    [Fact]
    public async Task Parameters_MissingValue_IsRejectedBeforeConnecting()
    {
        var src = DatabaseSource.Create(Settings() with { FilePath = Path.Combine(_dir, "does-not-exist.db") });
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("SELECT @a, @b", new Dictionary<string, object?> { ["a"] = 1 }));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal(DataKeys.RejectMissingParameter, ex.ReasonKey);
        Assert.Equal(new[] { "b" }, ex.Args);
    }

    // ---- read-only safety -----------------------------------------------------------------------

    [Theory]
    [InlineData("INSERT INTO Products (Id, Name) VALUES (9, 'x')")]
    [InlineData("DELETE FROM Products")]
    [InlineData("DROP TABLE Products")]
    [InlineData("SELECT 1; DROP TABLE Products")]
    [InlineData("PRAGMA writable_schema = ON")]
    [InlineData("ATTACH DATABASE 'other.db' AS o")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO Products (Id, Name) SELECT 9, 'x' FROM x")]
    public async Task WritesAreRejected_AndTheFileIsUntouched(string sql)
    {
        byte[] before = await File.ReadAllBytesAsync(_db);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().QueryAsync(sql));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.NotNull(ex.ReasonKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(_db));
        Assert.Equal(3, (await Source().QueryAsync("SELECT * FROM Products")).Table.RowCount);
    }

    [Fact]
    public void ConnectionString_OpensTheFileReadOnly_EvenIfTheGuardWereBypassed()
    {
        using var c = new SqliteConnection(ConnectionStrings.Build(Settings(), null));
        c.Open();
        var ex = Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO Products (Id, Name) VALUES (99, 'x')"));
        Assert.Equal(8, ex.SqliteErrorCode); // SQLITE_READONLY
    }

    // ---- caps, cancellation, streaming -----------------------------------------------------------

    private const string Numbers = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {0}) SELECT i FROM n";

    [Fact]
    public async Task RowCap_TruncatesAndSaysSo()
    {
        var r = await Source().QueryAsync(string.Format(Numbers, 5000), null, maxRows: 100);
        Assert.True(r.Truncated);
        Assert.Equal(100, r.MaxRows);
        Assert.Equal(100, r.Table.RowCount);
        Assert.Equal("100", r.Table.Rows[^1][0]);
        var issue = Assert.Single(r.Table.Issues);
        Assert.Equal(DataKeys.QueryTruncated, issue.MessageKey);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(new[] { "100" }, issue.Args);
    }

    [Fact]
    public async Task RowCap_ExactlyAtTheLimit_IsNotTruncated()
    {
        var r = await Source().QueryAsync(string.Format(Numbers, 100), null, maxRows: 100);
        Assert.False(r.Truncated);
        Assert.Equal(100, r.Table.RowCount);
        Assert.Empty(r.Table.Issues);
    }

    [Fact]
    public async Task RowCap_DefaultIs100000_AndHardLimitIsEnforced()
    {
        Assert.Equal(100_000, DatabaseSource.DefaultMaxRows);
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().QueryAsync("SELECT 1", null, DatabaseSource.HardMaxRows + 1));
        Assert.Equal(DataKeys.TooManyRows, ex.Key);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Source().QueryAsync("SELECT 1", null, 0));
        var big = await Source().QueryAsync(string.Format(Numbers, 100_050));
        Assert.True(big.Truncated);
        Assert.Equal(100_000, big.Table.RowCount);
    }

    [Fact]
    public async Task Cancellation_BeforeStart_ReportsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().QueryAsync("SELECT 1", null, 10, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
        Assert.True(ex.IsCancellation);
        await Assert.ThrowsAsync<DatabaseException>(() => Source().TestConnectionAsync(cts.Token));
        await Assert.ThrowsAsync<DatabaseException>(() => Source().ListTablesAsync(cts.Token));
    }

    [Fact]
    public async Task Cancellation_WhileStreaming_StopsAndReleasesTheFile()
    {
        using var cts = new CancellationTokenSource();
        await using (var stream = await Source().OpenStreamAsync(string.Format(Numbers, 5_000_000), null, cts.Token))
        {
            int seen = 0;
            var ex = await Assert.ThrowsAsync<DatabaseException>(async () =>
            {
                await foreach (var row in stream.ReadRowsAsync(cts.Token))
                {
                    if (++seen == 10) cts.Cancel();
                }
            });
            Assert.Equal(DataKeys.Cancelled, ex.Key);
            Assert.True(seen is >= 10 and < 1000, $"stopped after {seen} rows");
        }
        File.Delete(_db); // would throw if a connection leaked
        Assert.False(File.Exists(_db));
    }

    [Fact]
    public async Task Stream_YieldsAllRows_WithNormalizedColumns()
    {
        await using var stream = await Source().OpenStreamAsync("SELECT Name AS x, Name AS X, Id FROM Products WHERE Id >= @min ORDER BY Id", new Dictionary<string, object?> { ["min"] = 2 });
        Assert.Equal(new[] { "x", "X_2", "Id" }, stream.Columns);
        var rows = new List<string[]>();
        await foreach (var r in stream.ReadRowsAsync()) rows.Add(r);
        Assert.Equal(2, rows.Count);
        Assert.Equal("3", rows[1][2]);
    }

    [Fact]
    public async Task Stream_RejectsWritesToo()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().OpenStreamAsync("DELETE FROM Products"));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
    }

    [Fact]
    public async Task BadSql_IsAQueryFailure_WithoutLeakingTheConnectionString()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => Source().QueryAsync("SELECT nope FROM Products"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
        Assert.NotNull(ex.Detail);
        Assert.DoesNotContain("Mode=", ex.ToString());
    }

    [Fact]
    public async Task ParallelQueries_DoNotInterfere_AndNothingStaysLocked()
    {
        var src = Source();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => src.QueryAsync("SELECT COUNT(*) FROM Products")));
        Assert.All(results, r => Assert.Equal("3", r.Table.Rows[0][0]));
        File.Delete(_db);
    }

    // ---- feeding the data merge ---------------------------------------------------------------------

    private static Rotula.Core.Model.LabelDocument Doc() => CoreTestSupport.NewDoc(
        new DataFieldElement { Id = "name", VariableName = "Name", X = 20, Y = 20 },
        new DataFieldElement { Id = "price", VariableName = "Price", X = 20, Y = 80 },
        new DataFieldElement { Id = "note", VariableName = "Note", X = 20, Y = 140 });

    [Fact]
    public async Task QueryResult_FeedsMergeDataSet_Unchanged()
    {
        var r = await Source().QueryAsync("SELECT Name, Price, Note, Qty AS Copies FROM Products ORDER BY Id");
        var set = new MergeDataSet(Doc(), r.Table);
        Assert.Equal(3, set.RecordCount);
        Assert.Equal(new[] { "Name", "Price", "Note" }, set.Placeholders);
        var rec = set.GetRecordData(0);
        Assert.Equal("Café Niño", rec["Name"]);
        Assert.Equal("12.5", rec["Price"]);
        Assert.Equal("", rec["Note"]);
    }

    [Fact]
    public async Task Merge_GoldenZpl_FromDatabaseRows()
    {
        var r = await Source().QueryAsync("SELECT Name, Price, Note FROM Products ORDER BY Id");
        var doc = Doc();
        var set = new MergeDataSet(doc, r.Table);
        var report = new MergeReport();
        string zpl = MergeBatch.GenerateSingleStream(doc, set, report);
        Assert.False(report.HasErrors, string.Join(", ", report.Issues.Select(i => i.Issue)));
        Assert.Equal(3, report.Emitted);
        CoreTestSupport.AssertGolden("data/sqlite_merge_single_stream", zpl.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Merge_ParameterisedQuery_GoldenZpl()
    {
        var r = await Source().QueryAsync("SELECT Name, Price, Note FROM Products WHERE Qty >= @min ORDER BY Id",
            new Dictionary<string, object?> { ["min"] = 3 });
        var doc = Doc();
        var set = new MergeDataSet(doc, r.Table);
        var report = new MergeReport();
        string zpl = MergeBatch.GenerateSingleStream(doc, set, report);
        Assert.Equal(2, report.Emitted);
        CoreTestSupport.AssertGolden("data/sqlite_merge_parameterised", zpl.Replace("\r\n", "\n"));
    }
}
