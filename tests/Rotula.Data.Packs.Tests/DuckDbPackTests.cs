using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;
using Rotula.Data;
using Rotula.Data.Pack.DuckDb;

namespace Rotula.Data.Packs.Tests;

/// <summary>The DuckDB pack against the real in-process engine.</summary>
public class DuckDbPackTests : IClassFixture<DuckDbFixture>
{
    private readonly DuckDbFixture _f;

    public DuckDbPackTests(DuckDbFixture f) => _f = f;

    private static string Cell(DatabaseQueryResult r, int row, string column) => r.Table.Rows[row][r.Table.IndexOfColumn(column)];

    private static Dictionary<string, string> Parse(string connectionString)
    {
        var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return b.Keys.Cast<string>().ToDictionary(k => k, k => Convert.ToString(b[k], CultureInfo.InvariantCulture)!, StringComparer.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- descriptor

    [Fact]
    public void TheDescriptor_DescribesAFileEngine()
    {
        var d = _f.ProviderRegistry.Get("duckdb");
        Assert.Equal(ProviderCategory.File, d.Category);
        Assert.False(d.RequiresServer);
        Assert.Null(d.DefaultPort);
        Assert.Empty(d.SupportedAuthentication);
        Assert.Equal(new[] { "filePath", "memoryLimitMb" }, d.Options.Select(o => o.Key));
        Assert.True(d.Options[0].Required);
        Assert.All(d.Options, o => Assert.StartsWith("Data.Option.", o.LabelKey));
        Assert.Same(SqlDialects.DuckDb, d.Dialect);
        Assert.False(d.ReadOnly.GuardIsPrimaryControl);
        Assert.Contains("READ_ONLY", d.ReadOnly.Description);
    }

    [Fact]
    public void TheConnectionString_OpensReadOnly_WithEveryRouteOutSwitchedOff_AndLocksIt()
    {
        var d = (DuckDbDescriptor)_f.ProviderRegistry.Get("duckdb");
        string cs = d.BuildConnectionString(_f.Settings(), null);
        var parsed = Parse(cs);
        Assert.Equal("READ_ONLY", parsed["access_mode"]);
        Assert.Equal("false", parsed["enable_external_access"]);
        Assert.Equal("false", parsed["autoinstall_known_extensions"]);
        Assert.Equal("false", parsed["autoload_known_extensions"]);
        Assert.Equal("false", parsed["allow_community_extensions"]);
        Assert.Equal("false", parsed["allow_unsigned_extensions"]);
        Assert.Equal("true", parsed["lock_configuration"]);
        // lock_configuration must come last, otherwise it would freeze the settings that follow it half-applied.
        Assert.Equal("lock_configuration", parsed.Keys.Last());
        Assert.Equal(_f.DatabasePath, parsed["DataSource"]);
    }

    [Fact]
    public void AMemoryLimit_IsPassedAsASetting()
    {
        var d = (DuckDbDescriptor)_f.ProviderRegistry.Get("duckdb");
        var s = _f.Settings() with { Extras = ProviderExtras.Of(("memoryLimitMb", "256")) };
        var parsed = Parse(d.BuildConnectionString(s, null));
        Assert.Equal("256MB", parsed["memory_limit"]);
        Assert.Equal("lock_configuration", parsed.Keys.Last());
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("md:mydb")]
    [InlineData("motherduck:mydb")]
    [InlineData(@"\\server\share\x.duckdb")]
    [InlineData("//server/share/x.duckdb")]
    [InlineData("https://example.com/x.duckdb")]
    [InlineData("s3://bucket/x.duckdb")]
    [InlineData("x.duckdb?access_mode=READ_WRITE")]
    [InlineData("x.duckdb;lock_configuration=false")]
    [InlineData("x=y.duckdb")]
    [InlineData("")]
    public void APathThatIsNotAPlainLocalFile_IsRefused(string path)
    {
        var d = _f.ProviderRegistry.Get("duckdb");
        var ex = Assert.Throws<DatabaseException>(() => d.Validate(_f.Settings(path)));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
    }

    [Fact]
    public void ABadMemoryLimit_IsRefused()
    {
        var s = _f.Settings() with { Extras = ProviderExtras.Of(("memoryLimitMb", "lots")) };
        Assert.Throws<DatabaseException>(() => _f.Source(s));
    }

    // ---------------------------------------------------------------- queries

    [Fact]
    public async Task ARealQuery_ReturnsRows_WithUnicodeIntact()
    {
        var r = await _f.Source().QueryAsync("SELECT id, name, city FROM people ORDER BY id");
        Assert.Equal(5, r.Table.RowCount);
        Assert.False(r.Truncated);
        Assert.Equal(new[] { "id", "name", "city" }, r.Table.Columns);
        Assert.Equal("Niño", Cell(r, 1, "name"));
        Assert.Equal("Peña", Cell(r, 1, "city"));
        Assert.Equal("日本語", Cell(r, 3, "name"));
        Assert.Equal("東京", Cell(r, 3, "city"));
        Assert.Equal("Ünï 😀", Cell(r, 4, "name"));
        Assert.Equal("Łódź", Cell(r, 4, "city"));
    }

    [Fact]
    public async Task Parameters_AreBound_NotPastedIn()
    {
        var src = _f.Source();
        var r = await src.QueryAsync("SELECT name FROM people WHERE id = @id AND city = @city",
            new Dictionary<string, object?> { ["id"] = 2, ["city"] = "Peña" });
        Assert.Equal("Niño", Cell(r, 0, "name"));

        // A hostile value is just data.
        var hostile = await src.QueryAsync("SELECT COUNT(*) AS n FROM people WHERE name = @n",
            new Dictionary<string, object?> { ["n"] = "x'; DROP TABLE people; --" });
        Assert.Equal("0", Cell(hostile, 0, "n"));
        Assert.Equal("5", Cell(await src.QueryAsync("SELECT COUNT(*) AS n FROM people"), 0, "n"));

        // The same name twice, a null, and a unicode value.
        var twice = await src.QueryAsync("SELECT @v AS a, @v AS b, @z AS z", new Dictionary<string, object?> { ["v"] = "日本語", ["z"] = null });
        Assert.Equal("日本語", Cell(twice, 0, "a"));
        Assert.Equal("日本語", Cell(twice, 0, "b"));
        Assert.Equal("", Cell(twice, 0, "z"));
    }

    [Fact]
    public async Task AMissingParameter_IsReportedBeforeAnythingRuns()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync("SELECT @nope"));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal(DataKeys.RejectMissingParameter, ex.ReasonKey);
    }

    [Fact]
    public async Task TheRowCap_StopsTheResult_AndSaysSo()
    {
        var r = await _f.Source().QueryAsync("SELECT * FROM range(100000) t(i)", null, 10);
        Assert.Equal(10, r.Table.RowCount);
        Assert.True(r.Truncated);
        Assert.Contains(r.Table.Issues, i => i.MessageKey == DataKeys.QueryTruncated);
    }

    [Fact]
    public async Task TheStreamingApi_ReadsEveryRow()
    {
        await using var stream = await _f.Source().OpenStreamAsync("SELECT i FROM range(2500) t(i)");
        int n = 0;
        await foreach (var row in stream.ReadRowsAsync()) { Assert.Single(row); n++; }
        Assert.Equal(2500, n);
    }

    [Fact]
    public async Task TheCatalogue_ListsTablesAndViews_AndDescribesColumns()
    {
        var src = _f.Source();
        var objects = await src.ListTablesAsync();
        Assert.Contains(objects, o => o.Name == "people" && o.Kind == DatabaseObjectKind.Table);
        Assert.Contains(objects, o => o.Name == "adults" && o.Kind == DatabaseObjectKind.View);
        var odd = Assert.Single(objects, o => o.Name == "we\"ird name");

        var cols = await src.DescribeAsync(odd);
        Assert.Equal("a b", Assert.Single(cols).Name);
        var people = await src.DescribeAsync("people");
        Assert.Equal(new[] { "id", "name", "city", "price" }, people.Select(c => c.Name));

        var preview = await src.PreviewAsync(odd, 5);
        Assert.Equal(0, preview.Table.RowCount);
        // Quoting doubles the closing quote, so an object name can never break out of the identifier.
        Assert.Equal("SELECT * FROM \"main\".\"we\"\"ird name\"", src.BuildSelectAll(new DatabaseObject("main", "we\"ird name", DatabaseObjectKind.Table)));
    }

    [Fact]
    public async Task TheConnectionTest_Passes()
    {
        await _f.Source().TestConnectionAsync();
    }

    // ---------------------------------------------------------------- value formatting

    private const string AllTypes =
        "SELECT CAST(170141183460469231731687303715884105727 AS HUGEINT) AS hugeint, " +
        "CAST(12345678901234567890123456789012345678 AS DECIMAL(38,0)) AS dec38, " +
        "CAST(-1234567890123456789012345678.9012345678 AS DECIMAL(38,10)) AS dec38s, " +
        "CAST(12.50 AS DECIMAL(10,2)) AS dec, " +
        "TIMESTAMPTZ '2024-01-02 03:04:05+00' AS tstz, INTERVAL 3 DAY AS iv, " +
        "CAST('b364a8e2-cae8-4e71-86ec-c8894f93a23b' AS UUID) AS id, [1,2,3] AS lst, {'x': 1, 'y': 'ñ'} AS st, " +
        "DATE '2024-02-03' AS d, TIME '10:11:12.5' AS t, 1.5::DOUBLE AS dbl, 'AB'::BLOB AS blob, NULL AS nul, " +
        "TIMESTAMP '2024-01-02 03:04:05' AS ts, true AS b, 3::UBIGINT AS ub";

    [Fact]
    public async Task EveryDuckDbType_IsFormatted_WithoutLoss()
    {
        var row = await _f.Source().QueryAsync(AllTypes);
        Assert.Equal("170141183460469231731687303715884105727", Cell(row, 0, "hugeint"));
        Assert.Equal("12345678901234567890123456789012345678", Cell(row, 0, "dec38"));
        Assert.Equal("-1234567890123456789012345678.9012345678", Cell(row, 0, "dec38s"));
        Assert.Equal("12.50", Cell(row, 0, "dec"));
        Assert.Equal("2024-01-02 03:04:05", Cell(row, 0, "tstz"));
        Assert.Equal("3.00:00:00", Cell(row, 0, "iv"));
        Assert.Equal("b364a8e2-cae8-4e71-86ec-c8894f93a23b", Cell(row, 0, "id"));
        Assert.Equal("1, 2, 3", Cell(row, 0, "lst"));
        Assert.Equal("[x, 1], [y, ñ]", Cell(row, 0, "st"));
        Assert.Equal("2024-02-03", Cell(row, 0, "d"));
        Assert.Equal("10:11:12.5", Cell(row, 0, "t"));
        Assert.Equal("1.5", Cell(row, 0, "dbl"));
        Assert.Equal("0x4142", Cell(row, 0, "blob"));
        Assert.Equal("", Cell(row, 0, "nul"));
        Assert.Equal("2024-01-02 03:04:05", Cell(row, 0, "ts"));
        Assert.Equal("TRUE", Cell(row, 0, "b"));
        Assert.Equal("3", Cell(row, 0, "ub"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public async Task TheFormattedValues_DoNotDependOnTheCurrentCulture(string culture)
    {
        var baseline = string.Join("|", (await _f.Source().QueryAsync(AllTypes)).Table.Rows[0]);
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var r = await _f.Source().QueryAsync(AllTypes);
            Assert.Equal(baseline, string.Join("|", r.Table.Rows[0]));
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    // ---------------------------------------------------------------- read-only and containment (real engine)

    [Theory]
    [InlineData("INSERT INTO people VALUES (9,'x','y',1)")]
    [InlineData("UPDATE people SET name = 'x'")]
    [InlineData("DELETE FROM people")]
    [InlineData("DROP TABLE people")]
    [InlineData("CREATE TABLE t2(a INT)")]
    [InlineData("SET enable_external_access = true")]
    [InlineData("PRAGMA enable_external_access")]
    [InlineData("ATTACH 'other.duckdb' AS o")]
    [InlineData("COPY people TO 'out.csv'")]
    [InlineData("INSTALL httpfs")]
    [InlineData("LOAD httpfs")]
    [InlineData("SELECT 1; DROP TABLE people")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO people SELECT * FROM x")]
    public async Task EverythingThatWrites_IsRefusedByTheGuard_AndTheDataIsUntouched(string sql)
    {
        var src = _f.Source();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync(sql));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal("5", Cell(await src.QueryAsync("SELECT COUNT(*) AS n FROM people"), 0, "n"));
    }

    [Theory]
    [InlineData("INSERT INTO people VALUES (9,'x','y',1)")]
    [InlineData("UPDATE people SET name = 'x'")]
    [InlineData("DELETE FROM people")]
    [InlineData("DROP TABLE people")]
    [InlineData("CREATE TABLE t2(a INT)")]
    public void TheEngineItself_RefusesWrites_EvenWhenTheGuardIsBypassed(string sql)
    {
        using var c = _f.RawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var ex = Assert.Throws<DuckDBException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SET enable_external_access = true")]
    [InlineData("SET lock_configuration = false")]
    [InlineData("SET autoinstall_known_extensions = true")]
    [InlineData("SET autoload_known_extensions = true")]
    [InlineData("SET allow_community_extensions = true")]
    [InlineData("SET allow_unsigned_extensions = true")]
    [InlineData("RESET enable_external_access")]
    [InlineData("SET memory_limit = '1TB'")]
    public void TheProtections_CannotBeSwitchedOffFromSql(string sql)
    {
        using var c = _f.RawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var ex = Assert.Throws<DuckDBException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("lock", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Still switched off afterwards.
        cmd.CommandText = "SELECT current_setting('enable_external_access')";
        Assert.False((bool)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("SELECT * FROM read_csv('{csv}')")]
    [InlineData("SELECT * FROM read_csv_auto('{csv}')")]
    [InlineData("SELECT * FROM read_text('{csv}')")]
    [InlineData("SELECT * FROM read_blob('{csv}')")]
    [InlineData("SELECT * FROM read_json('{csv}')")]
    [InlineData("SELECT * FROM read_parquet('{csv}')")]
    [InlineData("SELECT * FROM '{csv}'")]
    [InlineData("SELECT * FROM glob('{dir}/*')")]
    [InlineData("SELECT * FROM read_csv('https://example.com/x.csv')")]
    [InlineData("SELECT * FROM read_parquet('s3://bucket/x.parquet')")]
    [InlineData("COPY people FROM '{csv}'")]
    [InlineData("COPY (SELECT 1) TO '{dir}/leak.csv'")]
    [InlineData("ATTACH '{dir}/other.duckdb' AS other")]
    [InlineData("EXPORT DATABASE '{dir}/export'")]
    [InlineData("INSTALL json")]
    [InlineData("INSTALL httpfs")]
    [InlineData("LOAD httpfs")]
    [InlineData("LOAD '{dir}/evil.duckdb_extension'")]
    public void TheEngineItself_CannotReadOrWriteFilesOrReachTheNetwork(string template)
    {
        string sql = template.Replace("{csv}", _f.CsvPath.Replace('\\', '/')).Replace("{dir}", _f.Folder.Replace('\\', '/'));
        DuckDbFixture.Execute(Path.Combine(_f.Folder, "other.duckdb"), "CREATE TABLE IF NOT EXISTS o(a INT)");
        using var c = _f.RawConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Assert.Throws<DuckDBException>(() => { using var r = cmd.ExecuteReader(); while (r.Read()) { } });
        Assert.False(File.Exists(Path.Combine(_f.Folder, "leak.csv")));
        Assert.False(Directory.Exists(Path.Combine(_f.Folder, "export")));
    }

    [Theory]
    [InlineData("SELECT * FROM read_csv('{csv}')")]
    [InlineData("SELECT * FROM read_text('{csv}')")]
    [InlineData("SELECT * FROM '{csv}'")]
    [InlineData("SELECT * FROM \"{csv}\"")]
    [InlineData("SELECT * FROM glob('{dir}/*')")]
    [InlineData("SELECT * FROM READ_CSV('{csv}')")]
    [InlineData("SELECT * FROM people, '{csv}'")]
    [InlineData("SELECT getenv('PATH')")]
    [InlineData("SELECT * FROM duckdb_secrets()")]
    public async Task FileReading_IsAlsoRefusedByTheGuard_BeforeTheEngineIsAsked(string template)
    {
        string sql = template.Replace("{csv}", _f.CsvPath.Replace('\\', '/')).Replace("{dir}", _f.Folder.Replace('\\', '/'));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync(sql));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
    }

    [Fact]
    public async Task ASelectThatMentionsAFileNameInsideAString_IsStillFine()
    {
        var r = await _f.Source().QueryAsync("SELECT 'secret.csv' AS f, \"name\" FROM people WHERE name = @n", new Dictionary<string, object?> { ["n"] = "Ana" });
        Assert.Equal("secret.csv", Cell(r, 0, "f"));
    }

    [Fact]
    public async Task NestedBlockComments_FollowTheEngine()
    {
        // Verified against DuckDB 1.5: its block comments nest, like PostgreSQL's. The guard must read them the same way.
        var r = await _f.Source().QueryAsync("SELECT 1 AS one /* a /* b */ c */");
        Assert.Equal("1", Cell(r, 0, "one"));
        // A second statement after the inner close is, for the engine, still comment text, so it is harmless and allowed ...
        var hidden = await _f.Source().QueryAsync("SELECT 2 AS two /* x /* y */ ; DROP TABLE people; */");
        Assert.Equal("2", Cell(hidden, 0, "two"));
        // ... while an unterminated nested comment is refused.
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync("SELECT 1 /* a /* b */"));
        Assert.Equal(DataKeys.RejectUnterminated, ex.ReasonKey);
        Assert.Equal("5", Cell(await _f.Source().QueryAsync("SELECT COUNT(*) AS n FROM people"), 0, "n"));
    }

    [Theory]
    [InlineData("SELECT 1 /* a /* b */ ; DROP TABLE people; */", true)]
    [InlineData("SELECT 1 /* a */ ; DROP TABLE people; /* b */", false)]
    [InlineData("SELECT 1 /* a /* b */ */ ; DROP TABLE people", false)]
    [InlineData("SELECT 1 /* a /* b */ */", true)]
    [InlineData("SELECT 1 /* /* /* */ */ */", true)]
    [InlineData("SELECT 1 /* /* */ ; DROP TABLE people", false)]
    [InlineData("SELECT '/*' ; DROP TABLE people; --'", false)]
    [InlineData("SELECT 1 -- /* \n ; DROP TABLE people", false)]
    [InlineData("SELECT $$ /* $$ ; DROP TABLE people; --", false)]
    public void TheGuardAndTheEngine_AgreeOnWhereCommentsEnd_ProvenOnACopyWithFullRights(string sql, bool guardAllows)
    {
        Assert.Equal(guardAllows, SqlGuard.Check(sql, SqlDialects.DuckDb).IsAllowed);
        if (!guardAllows) return;
        // The strongest proof that no statement hides in text the guard skipped: run what the guard allowed on a COPY of the database
        // opened with full rights, and check that nothing was dropped.
        string copy = Path.Combine(_f.Folder, "copy-" + Guid.NewGuid().ToString("N") + ".duckdb");
        File.Copy(_f.DatabasePath, copy);
        using var c = new DuckDBConnection("DataSource=" + copy);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT COUNT(*) FROM people";
        Assert.Equal(5L, cmd.ExecuteScalar());
    }

    // ---------------------------------------------------------------- cancellation, timeouts, errors

    private const string Forever = "SELECT COUNT(*) FROM range(1000000000) a, range(1000000) b";

    [Fact]
    public async Task ALongQuery_IsStoppedByTheTimeout()
    {
        var settings = _f.Settings() with { CommandTimeoutSeconds = 1, ConnectTimeoutSeconds = 1 };
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(settings).QueryAsync(Forever));
        Assert.Equal(DataKeys.Timeout, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
    }

    [Fact]
    public async Task ALongQuery_IsStoppedByTheCaller()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync(Forever, null, 100, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
        // The engine is usable again straight away (the interrupted query really ended).
        Assert.Equal("1", Cell(await _f.Source().QueryAsync("SELECT 1 AS one"), 0, "one"));
    }

    [Fact]
    public async Task ACancelledToken_NeverOpensTheFile()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync("SELECT 1", null, 10, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
    }

    [Fact]
    public async Task AMissingFile_IsDatabaseNotFound()
    {
        var src = _f.Source(_f.Settings(Path.Combine(_f.Folder, "nope.duckdb")));
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("SELECT 1"));
        Assert.Equal(DataKeys.DatabaseNotFound, ex.Key);
    }

    [Fact]
    public async Task AFileThatIsNotADatabase_IsAPlainConnectionFailure()
    {
        string bad = Path.Combine(_f.Folder, "bad.duckdb");
        File.WriteAllText(bad, "this is not a database file at all, just text, long enough to be read as a header");
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Settings(bad)).QueryAsync("SELECT 1"));
        Assert.Equal(DataKeys.ConnectionFailed, ex.Key);
    }

    [Fact]
    public async Task AQueryError_IsQueryFailed()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync("SELECT * FROM does_not_exist"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
        var ex2 = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source().QueryAsync("SELECT nope FROM people"));
        Assert.Equal(DataKeys.QueryFailed, ex2.Key);
    }

    [Theory]
    [InlineData("IO Error: Cannot open database \"x\" in read-only mode: database does not exist", DataKeys.DatabaseNotFound)]
    [InlineData("IO Error: The file \"x\" exists, but it is not a valid DuckDB database file!", DataKeys.ConnectionFailed)]
    [InlineData("Permission Error: Cannot access file \"x.csv\" - file system operations are disabled by configuration", DataKeys.PermissionDenied)]
    [InlineData("INTERRUPT Error: Interrupted!", DataKeys.Cancelled)]
    [InlineData("IO Error: Could not set lock on file \"x\": Conflicting lock is held", DataKeys.Timeout)]
    [InlineData("Catalog Error: Table with name nope does not exist!", null)]
    public void TheErrorMapper_ClassifiesTheEngineMessages(string message, string? expectedKey)
    {
        var d = _f.ProviderRegistry.Get("duckdb");
        var ex = CreateDuckDbException(message);
        var (number, state) = d.Errors.Extract(ex);
        Assert.Null(state);
        Assert.Equal(expectedKey, d.Errors.ClassifyNumber(number, connecting: true));
    }

    [Theory]
    [InlineData("12345", 2, "123.45")]
    [InlineData("5", 3, "0.005")]
    [InlineData("-5", 3, "-0.005")]
    [InlineData("-1234", 0, "-1234")]
    [InlineData("0", 2, "0.00")]
    public void DecimalDigits_AreWrittenExactly(string unscaled, int scale, string expected)
        => Assert.Equal(expected, DuckDbDescriptor.FormatDecimal(System.Numerics.BigInteger.Parse(unscaled, CultureInfo.InvariantCulture), scale));

    private static DuckDBException CreateDuckDbException(string message)
    {
        // DuckDBException is created by the driver only; build one with the message we want so the mapper sees the real type.
        var ctor = typeof(DuckDBException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == typeof(string));
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? message : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (DuckDBException)ctor.Invoke(args);
    }
}
