using System.Data.Common;
using Npgsql;
using Xunit.Abstractions;

namespace Rotula.Data.Integration;

/// <summary>The shared suite against a real PostgreSQL server, plus the PostgreSQL-only checks.</summary>
[Engine(ServerCatalog.PostgreSql)]
public sealed class PostgreSqlTests : ServerSuite
{
    /// <summary>Creates the test instance.</summary>
    public PostgreSqlTests(ITestOutputHelper output) : base(output) { }

    internal override string Engine => ServerCatalog.PostgreSql;
    internal override DatabaseProvider Provider => DatabaseProvider.PostgreSql;
    internal override ProviderExtras BaseExtras => ProviderExtras.Empty;
    internal override string DatabaseName => PostgreSqlSeed.Database;
    internal override string WriterUser => PostgreSqlSeed.WriterUser;
    internal override string WriterPassword => PostgreSqlSeed.WriterPassword;
    internal override Task SeedAsync() => PostgreSqlSeed.EnsureAsync(Info);

    internal override async Task<DbConnection> OpenAdminAsync(string? database)
    {
        var conn = new NpgsqlConnection(PostgreSqlSeed.AdminConnectionString(Info, database ?? "postgres"));
        await conn.OpenAsync();
        return conn;
    }

    internal override string NullSafeEquals(string column, string parameter) => $"{column} IS NOT DISTINCT FROM {parameter}";
    internal override string NumbersSql(int count) => $"SELECT g AS n FROM generate_series(1, {count}) g";
    // The set-returning function sits in the select list of a sub-query on purpose: in FROM position PostgreSQL computes the whole
    // series before returning the first row, which would turn the "huge result" tests into a test of the server's memory.
    internal override string WideRowsSql(long count) => $"SELECT s.g AS n, md5(s.g::text) || repeat('x', 200) AS pad FROM (SELECT generate_series(1, {count}::bigint) AS g) s";
    internal override string SleepSql(int seconds) => $"SELECT pg_sleep({seconds})";
    internal override string MidStreamErrorSql => "SELECT 10 / (3 - g) AS q FROM generate_series(1, 5) g";
    internal override string LargeValueSql(int textLength) => $"SELECT repeat('x', {textLength}) AS t, decode(repeat('ab', 100000), 'hex') AS b";
    internal override string QuoteAlias(string alias) => "\"" + alias + "\"";
    internal override string IsEncryptedSql => "SELECT CASE WHEN ssl THEN '1' ELSE '0' END FROM pg_stat_ssl WHERE pid = pg_backend_pid()";
    internal override string OrdersTable => "sales.orders";
    // 25006 = read_only_sql_transaction
    internal override bool IsReadOnlyRefusal(Exception error) => error is PostgresException { SqlState: "25006" };
    internal override string SecretSelectSql => "SELECT * FROM secret_notes";
    internal override string SeenText => "2024-03-01 10:20:30Z";
    // The cast tells PostgreSQL the parameter type at its first appearance, in "@x IS NULL".
    internal override string OptionalFilterSql => "SELECT id FROM customers WHERE (@x::text IS NULL OR city = @x) ORDER BY id";

    internal override async Task<int> RunningSleepersAsync(int seconds)
        => int.Parse(await AdminScalarAsync(
            $"SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND pid <> pg_backend_pid() AND query LIKE '%pg_sleep({seconds})%'", "postgres"));

    internal override async Task<string> FingerprintAsync()
        => await AdminScalarAsync(
            "SELECT (SELECT count(*) FROM customers) || '/' || (SELECT count(*) FROM victim) || '/' || (SELECT count(*) FROM audit) || '/' || " +
            "(SELECT string_agg(v, ',' ORDER BY id) FROM victim) || '/' || (SELECT last_value || ':' || is_called FROM seq_probe) || '/' || " +
            "(SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            " WHERE n.nspname IN ('public', 'sales') AND c.relkind IN ('r', 'v', 'S')) || '/' || " +
            "(SELECT string_agg(attname, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'victim'::regclass AND attnum > 0 AND NOT attisdropped)");

    internal override IReadOnlyList<(string Label, string Sql)> WriteAttempts { get; } = new (string, string)[]
    {
        ("INSERT", "INSERT INTO victim VALUES (99, 'x')"),
        ("UPDATE", "UPDATE victim SET v = 'changed'"),
        ("DELETE", "DELETE FROM victim"),
        ("TRUNCATE", "TRUNCATE victim"),
        ("CREATE TABLE", "CREATE TABLE bypass_t (x int)"),
        ("CREATE TEMP TABLE", "CREATE TEMP TABLE bypass_tmp (x int)"),
        ("CREATE TABLE AS", "CREATE TABLE bypass_as AS SELECT * FROM customers"),
        ("SELECT INTO", "SELECT * INTO bypass_into FROM customers"),
        ("ALTER TABLE", "ALTER TABLE victim ADD COLUMN z int"),
        ("DROP TABLE", "DROP TABLE victim"),
        ("CREATE INDEX", "CREATE INDEX bypass_ix ON victim (v)"),
        ("nextval", "SELECT nextval('seq_probe')"),
        ("setval", "SELECT setval('seq_probe', 100)"),
        ("function with a side effect", "SELECT side_effect()"),
        ("DELETE in a CTE", "WITH d AS (DELETE FROM victim RETURNING *) SELECT * FROM d"),
        ("large object", "SELECT lo_create(0)"),
        ("GRANT", "GRANT ALL ON victim TO PUBLIC"),
        ("COMMENT", "COMMENT ON TABLE victim IS 'changed'"),
    };

    internal override IReadOnlyList<DatabaseObject> ExpectedReaderObjects { get; } = new[]
    {
        new DatabaseObject("public", "customers", DatabaseObjectKind.Table),
        new DatabaseObject("public", "customers_v", DatabaseObjectKind.View),
        new DatabaseObject("public", "odd.name", DatabaseObjectKind.Table),
        new DatabaseObject("public", "victim", DatabaseObjectKind.Table),
        new DatabaseObject("sales", "orders", DatabaseObjectKind.Table),
    };

    internal override IReadOnlyList<string> HiddenFromReader { get; } = new[] { "secret_notes", "audit" };
    internal override IReadOnlyList<string> ForbiddenSchemas { get; } = new[] { "pg_catalog", "information_schema", "pg_toast" };

    // ---------------------------------------------------------------- PostgreSQL only

    /// <summary>The session starts read-only because of the startup option; a pooler-style source relies on the in-transaction SET alone.</summary>
    [RealFact]
    public async Task Startup_option_and_pooler_mode_both_end_in_a_read_only_transaction()
    {
        await ReadyAsync();
        const string sql = "SELECT current_setting('default_transaction_read_only') AS session_default, current_setting('transaction_read_only') AS in_transaction";
        var normal = await Open().QueryAsync(sql);
        Assert.Equal(new[] { "on", "on" }, normal.Table.Rows[0]);

        var pooler = await Open(Make(extras: ProviderExtras.Of(("noStartupOptions", "true")))).QueryAsync(sql);
        // Without the startup option the session default is the server's ("off"), but the query still runs in a READ ONLY transaction.
        Assert.Equal(new[] { "off", "on" }, pooler.Table.Rows[0]);
    }

    /// <summary>With the startup option present, even a write sent in autocommit mode (no transaction at all) is refused.</summary>
    [RealFact]
    public async Task The_startup_option_alone_blocks_a_write_outside_any_transaction()
    {
        await ReadyAsync();
        var settings = Make(user: WriterUser);
        var descriptor = ProviderRegistry.Default.Resolve(settings);
        await using var conn = descriptor.CreateConnection(descriptor.BuildConnectionString(settings, WriterPassword), settings);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO victim VALUES (98, 'x')";
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("25006", ex.SqlState);
    }

    /// <summary>In pooler mode the READ ONLY transaction alone must still stop every write.</summary>
    [RealFact]
    public async Task Pooler_mode_transaction_alone_blocks_the_writes()
    {
        await ReadyAsync();
        var failures = new List<string>();
        foreach (var (label, sql) in WriteAttempts)
        {
            var error = await ReadOnlyBypass.RunAsync(Make(user: WriterUser, extras: ProviderExtras.Of(("noStartupOptions", "true"))), WriterPassword, sql);
            if (error is null || !IsReadOnlyRefusal(error)) failures.Add(label);
        }
        Assert.True(failures.Count == 0, "Not blocked in pooler mode: " + string.Join(", ", failures));
    }

    /// <summary>Trying to switch the read-only mode off from inside the session does not work.</summary>
    [RealFact]
    public async Task A_session_cannot_switch_read_only_off_from_inside()
    {
        await ReadyAsync();
        foreach (string attack in new[]
        {
            "SELECT set_config('transaction_read_only', 'off', false)",
            "SELECT set_config('default_transaction_read_only', 'off', false)",
        })
        {
            var settings = Make(user: WriterUser);
            var descriptor = ProviderRegistry.Default.Resolve(settings);
            await using var conn = descriptor.CreateConnection(descriptor.BuildConnectionString(settings, WriterPassword), settings);
            await conn.OpenAsync();
            await using var tx = await descriptor.BeginTransactionAsync(conn, CancellationToken.None);
            foreach (string s in descriptor.ReadOnly.TransactionStatements) await Exec(conn, tx, s);
            var attackError = await Record.ExceptionAsync(() => Exec(conn, tx, attack));
            Log(attack + " -> " + (attackError is null ? "accepted" : attackError.GetType().Name + ": " + attackError.Message.Split('\n')[0]));
            if (attackError is not null)
            {
                // The server itself refused the attempt (the transaction is now aborted, so nothing more can run in it).
                Assert.IsType<PostgresException>(attackError);
            }
            else
            {
                // The attempt was accepted: the write that follows must STILL be refused by the read-only session.
                var ex = await Record.ExceptionAsync(() => Exec(conn, tx, "INSERT INTO victim VALUES (97, 'x')"));
                Assert.True(ex is not null && IsReadOnlyRefusal(ex), "the write after '" + attack + "' must still be refused by the read-only session, got: " + ex);
            }
            await tx.RollbackAsync();
        }
        Assert.Equal("2", await AdminScalarAsync("SELECT count(*) FROM victim"));
    }

    /// <summary>The guard refuses advisory locks and file/command functions that a read-only session would allow.</summary>
    [RealFact]
    public async Task The_guard_refuses_what_read_only_alone_would_allow()
    {
        await ReadyAsync();
        var src = Open(Make(user: Info.AdminUser));
        var allowed = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT pg_advisory_lock(1)", "SELECT pg_advisory_xact_lock(1)", "SELECT pg_try_advisory_lock(1)",
            "COPY (SELECT 1) TO PROGRAM 'exit 0'", "SELECT pg_terminate_backend(0)", "SELECT pg_cancel_backend(0)",
            "SELECT pg_reload_conf()", "SELECT pg_read_file('postgresql.conf')", "SELECT pg_ls_dir('.')",
            "SELECT lo_import('x')", "SELECT dblink_exec('x', 'y')", "SELECT pg_sleep(0) , pg_switch_wal()",
        })
        {
            try { await src.QueryAsync(sql); allowed.Add(sql + " -> executed"); }
            catch (DatabaseException ex) when (ex.Key == DataKeys.QueryRejected) { }
            catch (DatabaseException ex) { allowed.Add(sql + " -> reached the server (" + ex.Key + ")"); }
        }
        Assert.True(allowed.Count == 0, string.Join("; ", allowed));
    }

    /// <summary>Awkward PostgreSQL values must not turn a preview into an error.</summary>
    [RealFact]
    public async Task Awkward_values_are_shown_instead_of_failing_the_query()
    {
        await ReadyAsync();
        var src = Open();
        var failures = new List<string>();
        foreach (string expr in new[]
        {
            "'NaN'::numeric", "'Infinity'::numeric", "'infinity'::timestamp", "'-infinity'::date", "'infinity'::timestamptz",
            "1e30::numeric", "123456789012345678901234567890123456789::numeric", "0.1234567890123456789012345678901234567::numeric",
            "'1 day 2 hours'::interval", "ARRAY[1,2,3]", "ARRAY['a','b']::text[]", "'{}'::int[]", "'192.168.0.1'::inet", "'12.50'::money",
            "'(1,2)'::point", "to_tsvector('hello world')", "'<a/>'::xml", "B'1010'", "'Infinity'::float8", "'NaN'::float4",
            "now()::timestamptz", "current_time", "'1 year'::interval", "'2024-01-01 BC'::date", "'00:00:00'::time", "'24:00:00'::time",
            "'a'::\"char\"", "'x'::name", "'{\"a\":1}'::json", "int4range(1, 5)", "gen_random_uuid()", "'\\x'::bytea",
        })
        {
            try
            {
                var r = await src.QueryAsync("SELECT " + expr + " AS v");
                Log(expr + " => '" + First(r) + "'");
            }
            catch (DatabaseException ex) { failures.Add(expr + " -> " + ex.Key + " " + ex.Detail); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The awkward values come out as readable, exact text (the rules are documented in DatabaseValueFormatter).</summary>
    [RealFact]
    public async Task Awkward_values_have_exact_readable_text()
    {
        await ReadyAsync();
        var src = Open();
        var expected = new (string Expr, string Text)[]
        {
            ("'NaN'::numeric", "NaN"), ("'Infinity'::numeric", "Infinity"), ("'-Infinity'::numeric", "-Infinity"),
            ("1e30::numeric", "1000000000000000000000000000000"),
            ("123456789012345678901234567890123456789::numeric", "123456789012345678901234567890123456789"),
            ("0.1234567890123456789012345678901234567::numeric", "0.1234567890123456789012345678901234567"),
            ("12.50::numeric(10,2)", "12.50"), ("-0.005::numeric", "-0.005"), ("0::numeric", "0"), ("100000::numeric", "100000"),
            ("'1 year 2 mons 3 days 04:05:06.7'::interval", "1 year 2 mons 3 days 04:05:06.7"), ("'26 hours'::interval", "26:00:00"),
            ("'-1 day'::interval", "-1 days"), ("'0'::interval", "00:00:00"), ("'18 months'::interval", "1 year 6 mons"),
            ("'infinity'::date", "infinity"), ("'-infinity'::date", "-infinity"), ("'infinity'::timestamp", "infinity"),
            ("'-infinity'::timestamp", "-infinity"), ("'infinity'::timestamptz", "infinity"), ("'-infinity'::timestamptz", "-infinity"),
            ("'2024-01-01 BC'::date", "2024-01-01 BC"), ("'0044-03-15 12:00:00 BC'::timestamp", "0044-03-15 12:00:00 BC"),
            ("'12345-06-07'::date", "12345-06-07"),
            ("'24:00:00'::time", "24:00:00"), ("'10:20:30'::time", "10:20:30"), ("'10:20:30.25'::time", "10:20:30.25"),
            ("'10:20:30+02'::timetz", "10:20:30+02:00"), ("'10:20:30-05:30'::timetz", "10:20:30-05:30"),
            ("'2024-03-05 14:30:00+02'::timestamptz", "2024-03-05 12:30:00Z"), ("'2024-03-05 00:00:00+00'::timestamptz", "2024-03-05 00:00:00Z"),
            ("'2024-03-05 00:00:00'::timestamp", "2024-03-05"), ("'2024-03-05 14:30:00'::timestamp", "2024-03-05 14:30:00"),
            ("'2024-03-05'::date", "2024-03-05"),
            ("B'1010'", "1010"), ("'101'::varbit", "101"), ("B'1'", "1"),
        };
        var wrong = new List<string>();
        foreach (var (expr, text) in expected)
        {
            var r = await src.QueryAsync("SELECT " + expr + " AS v");
            if (r.Table.Rows[0][0] != text) wrong.Add(expr + " => '" + r.Table.Rows[0][0] + "' (expected '" + text + "')");
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>One awkward cell in a row of ordinary ones neither fails the query nor disturbs the other cells; an array of awkward values is shown too.</summary>
    [RealFact]
    public async Task One_awkward_cell_does_not_disturb_its_row()
    {
        await ReadyAsync();
        var r = await Open().QueryAsync("SELECT 1 AS a, 'NaN'::numeric AS b, 'x' AS c, '1 year'::interval AS d, 3.5 AS f");
        Assert.Equal(new[] { "1", "NaN", "x", "1 year", "3.5" }, r.Table.Rows[0]);
        var array = await Open().QueryAsync("SELECT ARRAY['NaN'::numeric, 1] AS e, 7 AS g");
        Log(string.Join(" | ", array.Table.Rows[0]));
        Assert.Equal("7", array.Table.Rows[0][1]);
    }

    /// <summary>"could not determine data type of parameter" is explained with its workaround.</summary>
    [RealFact]
    public async Task An_untyped_null_parameter_is_explained_with_a_cast_hint()
    {
        await ReadyAsync();
        var src = Open();
        var ex = await Fails(() => src.QueryAsync("SELECT id FROM customers WHERE (@x IS NULL OR city = @x)", new Dictionary<string, object?> { ["x"] = null }));
        Log(ex.Key + " (" + ex.ReasonKey + ") | " + ex.Detail);
        Assert.Equal(DataKeys.ParameterTypeUnknown, ex.Key);
        Assert.Equal(DataKeys.ParameterTypeUnknownHint, ex.ReasonKey);
    }

    /// <summary>A text parameter compared with a number column is a parameter type mismatch, while a plain unknown function stays a failed query.</summary>
    [RealFact]
    public async Task A_text_parameter_against_a_number_is_a_type_mismatch_hint()
    {
        await ReadyAsync();
        var src = Open();
        var ex = await Fails(() => src.QueryAsync("SELECT id FROM customers WHERE id = @x", new Dictionary<string, object?> { ["x"] = "abc" }));
        Log(ex.Key + " (" + ex.ReasonKey + ") | " + ex.Detail);
        Assert.Equal(DataKeys.ParameterTypeMismatch, ex.Key);
        Assert.Equal(DataKeys.ParameterTypeMismatchHint, ex.ReasonKey);

        var unknown = await Fails(() => src.QueryAsync("SELECT no_such_function(1)"));
        Assert.Equal(DataKeys.QueryFailed, unknown.Key);
    }

    private static async Task Exec(DbConnection conn, DbTransaction tx, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string First(DatabaseQueryResult r) => r.Table.Rows[0][0];
}
