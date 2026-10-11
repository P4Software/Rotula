using System.Diagnostics;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Rotula.Data;
using Rotula.Data.Pack.Firebird;

namespace Rotula.Data.Packs.Tests;

/// <summary>Creates the tables, views, procedures and the SELECT-only account the Firebird tests use, and removes them afterwards.</summary>
public sealed class FirebirdFixture : IDisposable
{
    public const string ReaderUser = "ROTULA_READER";
    public const string ReaderPassword = "Reader-Pw-1";

    public FirebirdFixture()
    {
        Server = FirebirdServerInfo.Current;
        if (Server is null) return;
        Registry = new ProviderRegistry();
        Registry.Register(new FirebirdPack());
        Cleanup();
        Server.Execute(
            "CREATE TABLE RT_PEOPLE (ID INTEGER NOT NULL PRIMARY KEY, NAME VARCHAR(100) CHARACTER SET UTF8, CITY VARCHAR(100) CHARACTER SET UTF8, PRICE DECIMAL(10,2))",
            "CREATE TABLE RT_LOG (ID INTEGER)",
            "CREATE VIEW RT_ADULTS AS SELECT * FROM RT_PEOPLE WHERE ID > 1",
            "CREATE GENERATOR RT_GEN",
            "CREATE TABLE RT_TYPES (I128 INT128, DEC38 DECIMAL(18,4), D DATE, T TIME, TS TIMESTAMP, B BOOLEAN, BL BLOB SUB_TYPE BINARY, TX BLOB SUB_TYPE TEXT CHARACTER SET UTF8, F DOUBLE PRECISION, TSTZ TIMESTAMP WITH TIME ZONE, DF DECFLOAT(16))",
            "CREATE PROCEDURE RT_WRITE_PLAIN RETURNS (X INTEGER) AS BEGIN INSERT INTO RT_LOG VALUES (1); X = 1; SUSPEND; END",
            "CREATE PROCEDURE RT_WRITE_AUTONOMOUS RETURNS (X INTEGER) AS BEGIN IN AUTONOMOUS TRANSACTION DO INSERT INTO RT_LOG VALUES (2); X = 1; SUSPEND; END");
        Server.Execute(
            "INSERT INTO RT_PEOPLE VALUES (1, 'Ana', 'Madrid', 12.50)",
            "INSERT INTO RT_PEOPLE VALUES (2, 'Niño', 'Peña', 1.5)",
            "INSERT INTO RT_PEOPLE VALUES (3, 'Zoë', 'Zürich', 0)",
            "INSERT INTO RT_PEOPLE VALUES (4, '日本語', '東京', 100)",
            "INSERT INTO RT_PEOPLE VALUES (5, 'Ünï 😀', 'Łódź', NULL)",
            "INSERT INTO RT_TYPES VALUES (170141183460469231731687303715884105727, 123456789012.3456, DATE '2024-02-03', TIME '10:11:12.5', TIMESTAMP '2024-01-02 03:04:05', TRUE, x'4142', 'ñandú', 1.5, TIMESTAMP '2024-01-02 03:04:05 +02:00', 1.25)");
        Server.Execute(
            "CREATE USER " + ReaderUser + " PASSWORD '" + ReaderPassword + "'",
            "GRANT SELECT ON RT_PEOPLE TO " + ReaderUser,
            "GRANT SELECT ON RT_ADULTS TO " + ReaderUser,
            "GRANT SELECT ON RT_TYPES TO " + ReaderUser);
    }

    public FirebirdServerInfo? Server { get; }
    public ProviderRegistry Registry { get; } = new();

    public DatabaseConnectionSettings Admin(Func<DatabaseConnectionSettings, DatabaseConnectionSettings>? tweak = null)
        => tweak?.Invoke(Server!.AdminSettings()) ?? Server!.AdminSettings();

    public DatabaseConnectionSettings Reader() => Server!.AdminSettings() with { Name = "fb-reader", UserName = ReaderUser };

    public IDatabaseSource Source(DatabaseConnectionSettings settings, string? password = null)
        => DatabaseSource.CreateUsing(settings, password ?? (settings.UserName == ReaderUser ? ReaderPassword : Server!.AdminPassword), Registry);

    private void Cleanup()
    {
        Server!.TryExecute(
            "DROP USER " + ReaderUser,
            "DROP PROCEDURE RT_WRITE_AUTONOMOUS", "DROP PROCEDURE RT_WRITE_PLAIN", "DROP TABLE RT_TYPES", "DROP GENERATOR RT_GEN",
            "DROP VIEW RT_ADULTS", "DROP TABLE RT_LOG", "DROP TABLE RT_PEOPLE");
    }

    public void Dispose()
    {
        if (Server is not null) Cleanup();
    }
}

/// <summary>The Firebird pack against a real Firebird 5 server on this computer; skipped when none is running.</summary>
public class FirebirdServerTests : IClassFixture<FirebirdFixture>
{
    private readonly FirebirdFixture _f;

    public FirebirdServerTests(FirebirdFixture f) => _f = f;

    private static string Cell(DatabaseQueryResult r, int row, string column) => r.Table.Rows[row][r.Table.IndexOfColumn(column)];

    [FirebirdFact]
    public async Task ASelectOnlyAccount_ReadsRows_WithUnicodeIntact()
    {
        var r = await _f.Source(_f.Reader()).QueryAsync("SELECT ID, NAME, CITY, PRICE FROM RT_PEOPLE ORDER BY ID");
        Assert.Equal(5, r.Table.RowCount);
        Assert.Equal(new[] { "ID", "NAME", "CITY", "PRICE" }, r.Table.Columns);
        Assert.Equal("Niño", Cell(r, 1, "NAME"));
        Assert.Equal("Peña", Cell(r, 1, "CITY"));
        Assert.Equal("日本語", Cell(r, 3, "NAME"));
        Assert.Equal("東京", Cell(r, 3, "CITY"));
        Assert.Equal("Ünï 😀", Cell(r, 4, "NAME"));
        Assert.Equal("Łódź", Cell(r, 4, "CITY"));
        Assert.Equal("12.50", Cell(r, 0, "PRICE"));
        Assert.Equal("", Cell(r, 4, "PRICE"));
    }

    [FirebirdFact]
    public async Task Parameters_AreBound_NotPastedIn()
    {
        var src = _f.Source(_f.Reader());
        var r = await src.QueryAsync("SELECT NAME FROM RT_PEOPLE WHERE ID = @id AND CITY = @city", new Dictionary<string, object?> { ["id"] = 2, ["city"] = "Peña" });
        Assert.Equal("Niño", Cell(r, 0, "NAME"));
        var hostile = await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_PEOPLE WHERE NAME = @n", new Dictionary<string, object?> { ["n"] = "x'; DROP TABLE RT_PEOPLE; --" });
        Assert.Equal("0", Cell(hostile, 0, "N"));
        // The same name twice, with a unicode value.
        var twice = await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_PEOPLE WHERE NAME = @v OR CITY = @v", new Dictionary<string, object?> { ["v"] = "日本語" });
        Assert.Equal("1", Cell(twice, 0, "N"));
        Assert.Equal("5", Cell(await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_PEOPLE"), 0, "N"));
    }

    [FirebirdFact]
    public async Task TheRowCap_StopsTheResult_AndSaysSo()
    {
        var r = await _f.Source(_f.Reader()).QueryAsync("WITH RECURSIVE R(N) AS (SELECT 1 FROM RDB$DATABASE UNION ALL SELECT N + 1 FROM R WHERE N < 100000) SELECT N FROM R", null, 10);
        Assert.Equal(10, r.Table.RowCount);
        Assert.True(r.Truncated);
    }

    [FirebirdFact]
    public async Task TheCatalogue_ListsUserTablesAndViews_NotSystemOnes()
    {
        var src = _f.Source(_f.Reader());
        var objects = await src.ListTablesAsync();
        Assert.Contains(objects, o => o.Name == "RT_PEOPLE" && o.Kind == DatabaseObjectKind.Table);
        Assert.Contains(objects, o => o.Name == "RT_ADULTS" && o.Kind == DatabaseObjectKind.View);
        Assert.DoesNotContain(objects, o => o.Name.StartsWith("RDB$", StringComparison.Ordinal) || o.Name.StartsWith("MON$", StringComparison.Ordinal));
        var cols = await src.DescribeAsync("RT_PEOPLE");
        Assert.Equal(new[] { "ID", "NAME", "CITY", "PRICE" }, cols.Select(c => c.Name));
        var preview = await src.PreviewAsync(objects.Single(o => o.Name == "RT_PEOPLE"), 2);
        Assert.Equal(2, preview.Table.RowCount);
        await src.TestConnectionAsync();
    }

    [FirebirdFact]
    public async Task ValuesOfEveryFirebirdType_AreFormatted_AndDoNotDependOnTheCulture()
    {
        const string sql = "SELECT I128, DEC38, D, T, TS, B, BL, TX, F, DF FROM RT_TYPES";
        var baseline = await _f.Source(_f.Reader()).QueryAsync(sql);
        var row = baseline.Table.Rows[0];
        Assert.Equal("170141183460469231731687303715884105727", row[0]);
        Assert.Equal("123456789012.3456", row[1]);
        Assert.Equal("2024-02-03", row[2]);
        Assert.Equal("10:11:12.5", row[3]);
        Assert.Equal("2024-01-02 03:04:05", row[4]);
        Assert.Equal("TRUE", row[5]);
        Assert.Equal("0x4142", row[6]);
        Assert.Equal("ñandú", row[7]);
        Assert.Equal("1.5", row[8]);
        Assert.Equal("1.25", row[9]);

        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "de-DE", "es-ES", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var r = await _f.Source(_f.Reader()).QueryAsync(sql);
                Assert.Equal(row, r.Table.Rows[0]);
            }
        }
        finally { CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = saved; }
    }

    // ---------------------------------------------------------------- read-only

    [FirebirdTheory]
    [InlineData("INSERT INTO RT_LOG VALUES (9)")]
    [InlineData("DELETE FROM RT_PEOPLE")]
    [InlineData("UPDATE RT_PEOPLE SET NAME = 'x'")]
    [InlineData("DROP TABLE RT_LOG")]
    [InlineData("SELECT GEN_ID(RT_GEN, 1) FROM RDB$DATABASE")]
    [InlineData("SELECT NEXT VALUE FOR RT_GEN FROM RDB$DATABASE")]
    [InlineData("SELECT * FROM RT_PEOPLE WITH LOCK")]
    [InlineData("EXECUTE BLOCK AS BEGIN INSERT INTO RT_LOG VALUES (9); END")]
    public async Task WithFullRights_TheGuardStillRefusesWrites(string sql)
    {
        var src = _f.Source(_f.Admin());
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync(sql));
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal("0", Cell(await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_LOG"), 0, "N"));
        Assert.Equal("5", Cell(await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_PEOPLE"), 0, "N"));
    }

    [FirebirdFact]
    public async Task TheServer_RefusesWrites_InThePacksTransaction_EvenForSysdba()
    {
        // Bypass the guard completely: open the connection and the transaction exactly as the pack does, as the administrator.
        var d = (FirebirdDescriptor)_f.Registry.Get("firebird");
        string cs = d.BuildConnectionString(_f.Admin(), _f.Server!.AdminPassword);
        await using var conn = (FirebirdSql.Data.FirebirdClient.FbConnection)d.CreateConnection(cs, _f.Admin());
        await conn.OpenAsync();
        await using var tx = await d.BeginTransactionAsync(conn, default);
        foreach (string sql in new[] { "INSERT INTO RT_LOG VALUES (9)", "DELETE FROM RT_PEOPLE", "UPDATE RT_PEOPLE SET NAME = 'x'", "SELECT * FROM RT_WRITE_PLAIN" })
        {
            await using System.Data.Common.DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            var ex = await Assert.ThrowsAnyAsync<FbException>(async () => { await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) { } });
            Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        await tx.RollbackAsync();
        Assert.Equal("0", Cell(await _f.Source(_f.Admin()).QueryAsync("SELECT COUNT(*) AS N FROM RT_LOG"), 0, "N"));
    }

    [FirebirdFact]
    public async Task ASelectableProcedureThatWrites_IsStoppedByTheReadOnlyTransaction_ThroughTheWholePipeline()
    {
        var src = _f.Source(_f.Admin());
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.QueryAsync("SELECT * FROM RT_WRITE_PLAIN"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
        Assert.Equal("0", Cell(await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_LOG"), 0, "N"));
    }

    [FirebirdFact]
    public async Task ASelectableProcedure_NeedsTheRight_ToRun_ForTheSelectOnlyAccount()
    {
        // The guard cannot see what a procedure does, so the account matters: the reader has no EXECUTE right.
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader()).QueryAsync("SELECT * FROM RT_WRITE_AUTONOMOUS"));
        Assert.Equal(DataKeys.PermissionDenied, ex.Key);
        Assert.Equal("0", Cell(await _f.Source(_f.Admin()).QueryAsync("SELECT COUNT(*) AS N FROM RT_LOG"), 0, "N"));
    }

    [FirebirdFact]
    public async Task AProcedureThatStartsAnAutonomousTransaction_CannotWrite_OnFirebird5()
    {
        var src = _f.Source(_f.Admin());
        bool refused;
        try { await src.QueryAsync("SELECT * FROM RT_WRITE_AUTONOMOUS"); refused = false; }
        catch (DatabaseException) { refused = true; }
        string written = Cell(await src.QueryAsync("SELECT COUNT(*) AS N FROM RT_LOG"), 0, "N");
        // Verified on Firebird 5.0.4: an autonomous transaction started by a procedure inherits READ ONLY, so the write is refused.
        Assert.True(refused && written == "0", $"refused={refused} written={written}");
        _f.Server!.Execute("DELETE FROM RT_LOG");
    }

    // ---------------------------------------------------------------- security of the connection

    [FirebirdFact]
    public async Task WithEncryptOn_TheWireIsReallyEncrypted()
    {
        var src = _f.Source(_f.Reader() with { Encrypt = true });
        var r = await src.QueryAsync("SELECT MON$WIRE_CRYPT_PLUGIN AS PLUGIN FROM MON$ATTACHMENTS WHERE MON$ATTACHMENT_ID = CURRENT_CONNECTION");
        string plugin = Cell(r, 0, "PLUGIN").Trim();
        Assert.True(plugin.Length > 0, "no wire encryption plugin in use");
    }

    [FirebirdFact]
    public async Task AWrongPassword_IsAuthenticationFailed_AndNeverEchoesThePassword()
    {
        var src = _f.Source(_f.Reader(), "wrong-Pw-9");
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => src.TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
        Assert.DoesNotContain("wrong-Pw-9", ex.Message + ex.Detail);
    }

    [FirebirdFact]
    public async Task AnUnknownUser_IsAuthenticationFailed()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader() with { UserName = "NOBODY" }, "x").TestConnectionAsync());
        Assert.Equal(DataKeys.AuthenticationFailed, ex.Key);
    }

    [FirebirdFact]
    public async Task AMissingDatabase_IsDatabaseNotFound()
    {
        string missing = Path.Combine(Path.GetDirectoryName(_f.Server!.Database)!, "does-not-exist.fdb");
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader() with { Database = missing }).TestConnectionAsync());
        Assert.Equal(DataKeys.DatabaseNotFound, ex.Key);
    }

    [FirebirdFact]
    public async Task ADatabaseOutsideTheServersAllowedFolders_IsPermissionDenied()
    {
        // The test server only allows its own data folder (DatabaseAccess = Restrict ...); asking for another place is refused by the server.
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader() with { Database = "C:/Windows/not-allowed.fdb" }).TestConnectionAsync());
        // A server that restricts database locations answers "not allowed" (permission); one that does not answers "file not found".
        Assert.Contains(ex.Key, new[] { DataKeys.PermissionDenied, DataKeys.DatabaseNotFound });
    }

    [FirebirdFact]
    public async Task AWrongPort_IsHostUnreachable()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader() with { Port = 1, ConnectTimeoutSeconds = 3 }).TestConnectionAsync());
        Assert.Equal(DataKeys.HostUnreachable, ex.Key);
    }

    [FirebirdFact]
    public async Task APermissionError_OnATable_IsPermissionDenied()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader()).QueryAsync("SELECT * FROM RT_LOG"));
        Assert.Equal(DataKeys.PermissionDenied, ex.Key);
    }

    [FirebirdFact]
    public async Task AQueryError_IsQueryFailed()
    {
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader()).QueryAsync("SELECT NOPE FROM RT_PEOPLE"));
        Assert.Equal(DataKeys.QueryFailed, ex.Key);
    }

    // ---------------------------------------------------------------- time

    private const string Forever = "SELECT COUNT(*) FROM RDB$FIELDS A CROSS JOIN RDB$FIELDS B CROSS JOIN RDB$FIELDS C CROSS JOIN RDB$FIELDS D CROSS JOIN RDB$FIELDS E";

    [FirebirdFact]
    public async Task ALongQuery_IsStoppedByTheTimeout()
    {
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader() with { CommandTimeoutSeconds = 1, ConnectTimeoutSeconds = 5 }).QueryAsync(Forever));
        Assert.Equal(DataKeys.Timeout, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
    }

    [FirebirdFact]
    public async Task ALongQuery_IsStoppedByTheCaller()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => _f.Source(_f.Reader()).QueryAsync(Forever, null, 100, cts.Token));
        Assert.Equal(DataKeys.Cancelled, ex.Key);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "took " + sw.Elapsed);
        Assert.Equal("1", Cell(await _f.Source(_f.Reader()).QueryAsync("SELECT 1 AS ONE FROM RDB$DATABASE"), 0, "ONE"));
    }

    // ---------------------------------------------------------------- the guard and the engine read the text alike

    [FirebirdTheory]
    [InlineData("SELECT q'{ ; DELETE FROM RT_LOG ; }' AS S FROM RDB$DATABASE", true)]
    [InlineData("SELECT q'[a]b]' AS S FROM RDB$DATABASE", true)]
    [InlineData("SELECT q'!x!!y!' AS S FROM RDB$DATABASE", true)]
    [InlineData("SELECT 'it''s ; DELETE FROM RT_LOG' AS S FROM RDB$DATABASE", true)]
    [InlineData("SELECT 1 AS ONE FROM RDB$DATABASE /* a /* b */", true)]
    [InlineData("SELECT 1 AS ONE FROM RDB$DATABASE /* a /* b */ ; DELETE FROM RT_LOG ; /* c */", false)]
    [InlineData("SELECT 1 AS ONE FROM RDB$DATABASE -- ; DELETE FROM RT_LOG", true)]
    [InlineData("SELECT 'a\\' AS S FROM RDB$DATABASE", true)]
    [InlineData("SELECT \"NAME\" FROM RT_PEOPLE WHERE ID = 1", true)]
    public async Task WhatTheGuardAllows_RunsAsOneStatement_OnTheRealEngine(string sql, bool guardAllows)
    {
        Assert.Equal(guardAllows, SqlGuard.Check(sql, SqlDialects.Firebird).IsAllowed);
        if (!guardAllows) return;
        // Run it with FULL rights, outside Rotula's pipeline, on a table the statement could destroy: nothing may change.
        _f.Server!.Execute("INSERT INTO RT_LOG VALUES (42)");
        try
        {
            var d = (FirebirdDescriptor)_f.Registry.Get("firebird");
            string cs = d.BuildConnectionString(_f.Admin(), _f.Server.AdminPassword);
            await using var conn = new FbConnection(cs);
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) { }
            }
            await using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM RT_LOG";
            Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally { _f.Server.Execute("DELETE FROM RT_LOG"); }
    }
}
