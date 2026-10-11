using System.Data.Common;
using MySqlConnector;
using Xunit.Abstractions;

namespace Rotula.Data.Integration;

/// <summary>The shared suite against a real MariaDB server (flavor "mariadb"), plus the MariaDB-only checks.</summary>
[Engine(ServerCatalog.MariaDb)]
public sealed class MariaDbTests : ServerSuite
{
    /// <summary>Creates the test instance.</summary>
    public MariaDbTests(ITestOutputHelper output) : base(output) { }

    internal override string Engine => ServerCatalog.MariaDb;
    internal override DatabaseProvider Provider => DatabaseProvider.MySql;
    internal override ProviderExtras BaseExtras => ProviderExtras.Of(("flavor", "mariadb"));
    internal override string DatabaseName => MariaDbSeed.Database;
    internal override string WriterUser => MariaDbSeed.WriterUser;
    internal override string WriterPassword => MariaDbSeed.WriterPassword;
    internal override Task SeedAsync() => MariaDbSeed.EnsureAsync(Info);

    internal override async Task<DbConnection> OpenAdminAsync(string? database)
    {
        var conn = new MySqlConnection(MariaDbSeed.AdminConnectionString(Info, database));
        await conn.OpenAsync();
        return conn;
    }

    internal override string NullSafeEquals(string column, string parameter) => $"{column} <=> {parameter}";
    internal override string NumbersSql(int count) => $"SELECT seq AS n FROM seq_1_to_{count}";
    internal override string WideRowsSql(long count) => $"SELECT seq AS n, CONCAT(MD5(seq), REPEAT('x', 200)) AS pad FROM seq_1_to_{count}";
    internal override string SleepSql(int seconds) => $"SELECT SLEEP({seconds})";
    internal override string MidStreamErrorSql => "SELECT IF(seq = 3, (SELECT 1 UNION SELECT 2), seq) AS q FROM seq_1_to_5";
    internal override string LargeValueSql(int textLength) => $"SELECT REPEAT('x', {textLength}) AS t, REPEAT(X'AB', 100000) AS b";
    internal override string QuoteAlias(string alias) => "`" + alias + "`";
    internal override string IsEncryptedSql => "SELECT IF(VARIABLE_VALUE <> '', '1', '0') FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME = 'Ssl_cipher'";
    internal override string OrdersTable => "sales_orders";
    // 1792 = ER_CANT_EXECUTE_IN_READ_ONLY_TRANSACTION
    internal override bool IsReadOnlyRefusal(Exception error) => error is MySqlException { ErrorCode: (MySqlErrorCode)1792 };
    internal override string SecretSelectSql => "SELECT * FROM rotula_private.secret_notes";
    internal override string OptionalFilterSql => "SELECT id FROM customers WHERE (@x IS NULL OR city = @x) ORDER BY id";
    internal override string? AdminListDatabase => "";
    internal override bool UnknownUserIsAnsweredPromptly => false;
    // MariaDB 11.4+ vouches for the certificate through the password handshake, so an account WITH a password connects to a
    // self-signed server even under VerifyFull (see Password_accounts_...). Only an account without a password is left to the
    // certificate check, which is what the secure default must be judged by.
    internal override (string User, string Password) TlsRefusalAccount => (MariaDbSeed.NoPasswordUser, "");

    internal override async Task<int> RunningSleepersAsync(int seconds)
        => int.Parse(await AdminScalarAsync(
            $"SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE ID <> CONNECTION_ID() AND INFO LIKE '%SLEEP({seconds})%'", ""));

    internal override async Task<string> FingerprintAsync()
        => await AdminScalarAsync(
            "SELECT CONCAT((SELECT COUNT(*) FROM customers), '/', (SELECT COUNT(*) FROM victim), '/', (SELECT COUNT(*) FROM rotula_private.audit), '/', " +
            "(SELECT GROUP_CONCAT(v ORDER BY id) FROM victim), '/', (SELECT next_not_cached_value FROM seq_probe), '/', " +
            "(SELECT GROUP_CONCAT(TABLE_NAME ORDER BY TABLE_NAME) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'rotula_it'), '/', " +
            "(SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'rotula_it' AND TABLE_NAME = 'victim'), '/', " +
            "(SELECT GROUP_CONCAT(SCHEMA_NAME ORDER BY SCHEMA_NAME) FROM information_schema.SCHEMATA))");

    internal override IReadOnlyList<(string Label, string Sql)> WriteAttempts { get; } = new (string, string)[]
    {
        ("INSERT", "INSERT INTO victim VALUES (99, 'x')"),
        ("UPDATE", "UPDATE victim SET v = 'changed'"),
        ("DELETE", "DELETE FROM victim"),
        ("TRUNCATE", "TRUNCATE TABLE victim"),
        ("CREATE TABLE", "CREATE TABLE bypass_t (x INT)"),
        ("CREATE TEMPORARY TABLE", "CREATE TEMPORARY TABLE bypass_tmp (x INT)"),
        ("CREATE TABLE AS", "CREATE TABLE bypass_as AS SELECT * FROM customers"),
        ("ALTER TABLE", "ALTER TABLE victim ADD COLUMN z INT"),
        ("CREATE INDEX", "CREATE INDEX bypass_ix ON victim (v)"),
        ("RENAME TABLE", "RENAME TABLE victim TO victim_renamed"),
        ("CREATE DATABASE", "CREATE DATABASE bypass_db"),
        ("DROP TABLE", "DROP TABLE victim"),
        ("NEXTVAL", "SELECT NEXTVAL(seq_probe)"),
        ("NEXT VALUE FOR", "SELECT NEXT VALUE FOR seq_probe"),
        ("SETVAL", "SELECT SETVAL(seq_probe, 100)"),
        ("function with a side effect", "SELECT side_effect()"),
        ("INSERT ... SELECT", "INSERT INTO victim SELECT id + 100, v FROM victim"),
        ("REPLACE", "REPLACE INTO victim VALUES (1, 'replaced')"),
        ("multi-table UPDATE", "UPDATE victim v JOIN customers c ON c.id = v.id SET v.v = c.name"),
    };

    internal override IReadOnlyList<DatabaseObject> ExpectedReaderObjects { get; } = new[]
    {
        new DatabaseObject("rotula_it", "customers", DatabaseObjectKind.Table),
        new DatabaseObject("rotula_it", "customers_v", DatabaseObjectKind.View),
        new DatabaseObject("rotula_it", "odd.name", DatabaseObjectKind.Table),
        new DatabaseObject("rotula_it", "victim", DatabaseObjectKind.Table),
        new DatabaseObject("rotula_it", "sales_orders", DatabaseObjectKind.Table),
    };

    internal override IReadOnlyList<string> HiddenFromReader { get; } = new[] { "secret_notes", "audit" };
    internal override IReadOnlyList<string> ForbiddenSchemas { get; } = new[] { "mysql", "information_schema", "performance_schema", "sys" };

    internal override IReadOnlyList<(string Label, string Sql)> ExtraGuardAttempts => base.ExtraGuardAttempts.Where(a => a.Label != "set").Concat(new (string, string)[]
    {
        ("set", "SET SESSION TRANSACTION READ WRITE"),
        ("set global", "SET GLOBAL max_connections = 1"),
        ("INTO OUTFILE", "SELECT 1 INTO OUTFILE 'rotula_guard_probe.txt'"),
        ("INTO DUMPFILE", "SELECT 1 INTO DUMPFILE 'rotula_guard_probe2.txt'"),
        ("user variable", "SELECT 1 INTO @x"),
        ("LOAD DATA", "LOAD DATA LOCAL INFILE 'x.csv' INTO TABLE victim"),
        ("FOR UPDATE", "SELECT * FROM victim FOR UPDATE"),
        ("LOCK IN SHARE MODE", "SELECT * FROM victim LOCK IN SHARE MODE"),
        ("HANDLER", "HANDLER victim OPEN"),
        ("DO", "DO SLEEP(0)"),
        ("KILL", "KILL QUERY 0"),
        ("executable comment", "SELECT /*! 1; DELETE FROM victim */ 1"),
    }).ToArray();

    // ---------------------------------------------------------------- MariaDB only

    /// <summary>Without a database in the settings the catalogue lists every user database, never the system ones.</summary>
    [RealFact]
    public async Task ListTables_without_a_database_lists_user_databases_only()
    {
        await ReadyAsync();
        var list = await Open(Make(user: Info.AdminUser, database: "")).ListTablesAsync();
        Assert.Contains(list, o => o.Schema == "rotula_it" && o.Name == "customers");
        foreach (string schema in ForbiddenSchemas)
            Assert.DoesNotContain(list, o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Documents the driver behaviour behind the TLS tests: with a password account, MySqlConnector accepts a self-signed certificate
    /// even under VerifyFull when the server is MariaDB 11.4 or newer, because the certificate fingerprint is bound into the password handshake.
    /// </summary>
    [RealFact]
    public async Task Password_accounts_connect_to_a_self_signed_mariadb_because_the_password_vouches_for_the_certificate()
    {
        await ReadyAsync();
        var r = await Open(Make(trust: false)).QueryAsync(IsEncryptedSql);
        Assert.Equal("1", r.Table.Rows[0][0]);
    }

    /// <summary>A system-versioned table is a normal table to the user; it must be listed.</summary>
    [RealFact]
    public async Task A_system_versioned_table_is_listed()
    {
        await ReadyAsync();
        var list = await Open().ListTablesAsync();
        Assert.Contains(list, o => o.Name == "hist" && o.Kind == DatabaseObjectKind.Table);
    }

    /// <summary>The session really is read only, and the sequence engine used by the other tests is the real one.</summary>
    [RealFact]
    public async Task The_session_is_read_only_inside_the_query_transaction()
    {
        await ReadyAsync();
        var r = await Open().QueryAsync("SELECT @@tx_read_only, @@session.tx_read_only, @@transaction_read_only");
        Log(string.Join(", ", r.Table.Rows[0]));
        Assert.All(r.Table.Rows[0], v => Assert.Equal("1", v));
    }

    /// <summary>Session settings that would let a statement escape (user variables, LOCAL INFILE) stay off.</summary>
    [RealFact]
    public async Task Local_infile_and_user_variables_are_off_for_the_session()
    {
        await ReadyAsync();
        var settings = Make();
        var descriptor = ProviderRegistry.Default.Resolve(settings);
        string cs = descriptor.BuildConnectionString(settings, Info.ReaderPassword);
        var b = new MySqlConnectionStringBuilder(cs);
        Assert.False(b.AllowLoadLocalInfile);
        Assert.False(b.AllowUserVariables);
    }

    /// <summary>The write attempts must be refused by the server with MariaDB's own read-only error, not for another reason.</summary>
    [RealFact]
    public async Task Blocked_writes_report_the_read_only_error()
    {
        await ReadyAsync();
        var settings = Make(user: WriterUser);
        var error = await ReadOnlyBypass.RunAsync(settings, WriterPassword, "INSERT INTO victim VALUES (96, 'x')");
        var my = Assert.IsType<MySqlException>(error);
        Log(my.ErrorCode + " " + my.Message);
        Assert.Equal(1792, (int)my.ErrorCode);   // ER_CANT_EXECUTE_IN_READ_ONLY_TRANSACTION
    }

    /// <summary>The guard refuses file, lock and administration functions that a read-only session would allow.</summary>
    [RealFact]
    public async Task The_guard_refuses_what_read_only_alone_would_allow()
    {
        await ReadyAsync();
        var src = Open(Make(user: Info.AdminUser));
        var allowed = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT GET_LOCK('x', 0)", "SELECT RELEASE_LOCK('x')", "SELECT RELEASE_ALL_LOCKS()", "SELECT LOAD_FILE('C:/Windows/win.ini')",
            "SELECT 1 INTO OUTFILE 'rotula_probe.txt'", "SELECT BENCHMARK(1, 1)", "SELECT SYS_EXEC('x')", "SELECT MASTER_POS_WAIT('a', 1)",
        })
        {
            try { await src.QueryAsync(sql); allowed.Add(sql + " -> executed"); }
            catch (DatabaseException ex) when (ex.Key == DataKeys.QueryRejected) { }
            catch (DatabaseException ex) { allowed.Add(sql + " -> reached the server (" + ex.Key + ")"); }
        }
        Assert.True(allowed.Count == 0, string.Join("; ", allowed));
    }

    /// <summary>Awkward MariaDB values must not turn a preview into an error.</summary>
    [RealFact]
    public async Task Awkward_values_are_shown_instead_of_failing_the_query()
    {
        await ReadyAsync();
        var src = Open();
        var failures = new List<string>();
        foreach (string expr in new[]
        {
            "CAST('12345678901234567890123456789012345.123456789012345678901234567890' AS DECIMAL(65,30))",
            "CAST('99999999999999999999999999999999999999999999999999999999999999999' AS DECIMAL(65,0))",
            "CAST('0.000000000000000000000000000001' AS DECIMAL(65,30))",
            "CAST('0000-00-00' AS DATE)", "CAST('0000-00-00 00:00:00' AS DATETIME)", "'9999-12-31 23:59:59.999999'",
            "CAST('9999-12-31 23:59:59.999999' AS DATETIME(6))", "CAST('1000-01-01' AS DATE)", "CAST('838:59:59' AS TIME)",
            "CAST('-838:59:59.000001' AS TIME(6))", "CAST('2024-02-29' AS DATE) + INTERVAL 1 YEAR", "YEAR(NOW())", "CAST(2024 AS UNSIGNED)",
            "18446744073709551615", "-9223372036854775808", "1.5e300", "CAST(1 AS CHAR)", "b'1010'", "CAST('x' AS BINARY)",
            "CAST(X'' AS BINARY)", "JSON_OBJECT('a', 1)", "JSON_ARRAY(1, 2)", "UUID()", "CAST('::1' AS INET6)", "CAST('1.2.3.4' AS INET4)",
            "ST_GeomFromText('POINT(1 2)')", "NULL", "''", "CONVERT('é' USING latin1)",
            "NOW(6)", "CURTIME(6)", "SEC_TO_TIME(100000)", "LAST_INSERT_ID()", "ROW_COUNT()",
        })
        {
            try
            {
                var r = await src.QueryAsync("SELECT " + expr + " AS v");
                Log(expr + " => '" + r.Table.Rows[0][0] + "'");
            }
            catch (DatabaseException ex) { failures.Add(expr + " -> " + ex.Key + " " + ex.Detail); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The awkward values come out as exact, readable text (the rules are documented in DatabaseValueFormatter).</summary>
    [RealFact]
    public async Task Awkward_values_have_exact_readable_text()
    {
        await ReadyAsync();
        var src = Open();
        var expected = new (string Expr, string Text)[]
        {
            ("CAST('12345678901234567890123456789012345.123456789012345678901234567890' AS DECIMAL(65,30))", "12345678901234567890123456789012345.123456789012345678901234567890"),
            ("CAST('99999999999999999999999999999999999999999999999999999999999999999' AS DECIMAL(65,0))", "99999999999999999999999999999999999999999999999999999999999999999"),
            ("CAST('0.000000000000000000000000000001' AS DECIMAL(65,30))", "0.000000000000000000000000000001"),
            ("CAST('12.50' AS DECIMAL(10,2))", "12.50"), ("CAST('-5.25' AS DECIMAL(10,2))", "-5.25"), ("CAST('0' AS DECIMAL(10,0))", "0"),
            ("CAST('0000-00-00' AS DATE)", "0000-00-00"), ("CAST('0000-00-00 00:00:00' AS DATETIME)", "0000-00-00"),
            ("CAST('2024-03-05' AS DATE)", "2024-03-05"), ("CAST('2024-03-05 01:02:03' AS DATETIME)", "2024-03-05 01:02:03"),
            ("CAST('2024-03-05 01:02:03.5' AS DATETIME(6))", "2024-03-05 01:02:03.5"), ("CAST('9999-12-31 23:59:59.999999' AS DATETIME(6))", "9999-12-31 23:59:59.999999"),
            ("CAST('838:59:59' AS TIME)", "838:59:59"), ("CAST('-838:59:59.000001' AS TIME(6))", "-838:59:59.000001"),
            ("CAST('10:20:30' AS TIME)", "10:20:30"), ("CAST('00:00:00' AS TIME)", "00:00:00"), ("SEC_TO_TIME(100000)", "27:46:40"),
        };
        var wrong = new List<string>();
        foreach (var (expr, text) in expected)
        {
            var r = await src.QueryAsync("SELECT " + expr + " AS v");
            if (r.Table.Rows[0][0] != text) wrong.Add(expr + " => '" + r.Table.Rows[0][0] + "' (expected '" + text + "')");
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>A stored zero date reads back as 0000-00-00 (also a partly zero one), and the rest of the row is intact.</summary>
    [RealFact]
    public async Task A_stored_zero_date_is_shown_as_stored()
    {
        await ReadyAsync();
        await using (var conn = await OpenAdminAsync(DatabaseName))
        {
            foreach (string sql in new[]
            {
                "DROP TABLE IF EXISTS zero_dates",
                "CREATE TABLE zero_dates (id INT, d DATE, t DATETIME, ts TIMESTAMP NULL)",
                "SET SESSION sql_mode = ''",
                "INSERT INTO zero_dates VALUES (1, '0000-00-00', '0000-00-00 00:00:00', NULL), (2, '2024-03-05', '2024-03-05 01:02:03', '2024-03-05 01:02:03'), (3, '2024-00-05', '2024-03-00 04:05:06', NULL)",
            })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var r = await Open(Make(user: Info.AdminUser)).QueryAsync("SELECT id, d, t, ts FROM zero_dates ORDER BY id");
        Assert.Equal(new[] { "1", "0000-00-00", "0000-00-00", "" }, r.Table.Rows[0]);
        Assert.Equal(new[] { "2", "2024-03-05", "2024-03-05 01:02:03", "2024-03-05 01:02:03" }, r.Table.Rows[1]);
        Assert.Equal(new[] { "3", "2024-00-05", "2024-03-00 04:05:06", "" }, r.Table.Rows[2]);

        // The table was only for this test; the other tests compare the list of objects.
        await using var cleanup = await OpenAdminAsync(DatabaseName);
        await using var drop = cleanup.CreateCommand();
        drop.CommandText = "DROP TABLE zero_dates";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>"CAST(x AS CHAR CHARACTER SET latin1)" is an ordinary read; the word SET inside it must not make the guard refuse it.</summary>
    [RealFact]
    public async Task The_guard_accepts_CHARACTER_SET_in_a_cast()
    {
        await ReadyAsync();
        var r = await Open().QueryAsync("SELECT CAST('a' AS CHAR CHARACTER SET latin1) AS v");
        Assert.Equal("a", r.Table.Rows[0][0]);
    }

    /// <summary>The ENUM, SET and BOOLEAN column types read back as plain text, numbers and TRUE/FALSE.</summary>
    [RealFact]
    public async Task Enum_set_and_boolean_columns_read_back_as_text()
    {
        await ReadyAsync();
        var r = await Open().QueryAsync("SELECT CAST('b' AS CHAR) AS e, active FROM customers WHERE id = 1");
        Assert.Equal(new[] { "b", "TRUE" }, r.Table.Rows[0]);
    }
}
