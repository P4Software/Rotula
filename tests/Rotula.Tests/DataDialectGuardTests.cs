using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// The dialect-driven guard: every rule has an accepting and a rejecting case. Lexical rules follow the vendors' language references
/// (Oracle literals, GoogleSQL lexical structure, ClickHouse syntax, Snowflake string constants); see docs/data-providers.md.
/// </summary>
public class DataDialectGuardTests
{
    private static SqlGuardResult Check(SqlDialect d, string sql) => SqlGuard.Check(sql, d);

    private static void Accept(SqlDialect d, string sql)
    {
        var r = Check(d, sql);
        Assert.True(r.IsAllowed, $"{d.Id}: expected accepted but got {r.ReasonKey} {r.Arg}: {sql}");
    }

    private static void Reject(SqlDialect d, string sql, string? reason = null, string? arg = null)
    {
        var r = Check(d, sql);
        Assert.False(r.IsAllowed, $"{d.Id}: expected rejected: {sql}");
        if (reason is not null) Assert.Equal(reason, r.ReasonKey);
        if (arg is not null) Assert.Equal(arg, r.Arg, ignoreCase: true);
    }

    // ---------------------------------------------------------------- everywhere: sequences that advance state

    public static IEnumerable<object[]> AllDialects => SqlDialects.All.Select(d => new object[] { d.Id });

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void NextValueFor_IsRefused_InEveryDialect(string id)
    {
        var d = SqlDialects.ById(id)!;
        Reject(d, "SELECT NEXT VALUE FOR seq1", DataKeys.RejectForbiddenFunction, "NEXT VALUE FOR");
        Reject(d, "SELECT a, next   value\tfor s FROM t", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT seq1.NEXTVAL FROM t", DataKeys.RejectForbiddenFunction, "NEXTVAL");
        Reject(d, "SELECT nextval('s')", DataKeys.RejectForbiddenFunction);
        // NEXT and VALUE are ordinary column names; only the three-word sequence is the sequence call.
        Accept(d, "SELECT next, value FROM t");
        Accept(d, "SELECT next_value, next FROM t WHERE value = 1");
        Accept(d, "SELECT * FROM t ORDER BY next, value");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TheBaseRules_StillApply_InEveryDialect(string id)
    {
        var d = SqlDialects.ById(id)!;
        Accept(d, "SELECT a, b FROM t WHERE c = 1");
        Accept(d, "WITH x AS (SELECT 1 AS a) SELECT a FROM x");
        Reject(d, "DROP TABLE t", DataKeys.RejectNotSelect);
        Reject(d, "SELECT 1; SELECT 2", DataKeys.RejectMultipleStatements);
        Reject(d, "SELECT * INTO t2 FROM t", DataKeys.RejectSelectInto);
        Reject(d, "SELECT * FROM t FOR UPDATE", DataKeys.RejectLocking);
        Reject(d, "SELECT 1 DROP TABLE t", DataKeys.RejectForbiddenKeyword, "DROP");
        Reject(d, "SELECT sp_who()", DataKeys.RejectForbiddenFunction);
        // Statements that only some vendors have never get past "must start with SELECT or WITH".
        foreach (string verb in new[] { "UNLOAD ('select 1') TO 's3://b/'", "PUT file:///x @stage", "GET @stage file:///x", "CREATE TABLE a CLONE b", "UNDROP TABLE t", "EXPORT DATA OPTIONS(uri='x') AS SELECT 1", "EXECUTE IMMEDIATE 'x'", "COPY INTO t FROM @s" })
            Reject(d, verb, DataKeys.RejectNotSelect);
    }

    // ---------------------------------------------------------------- Oracle

    [Fact]
    public void Oracle_QQuotedStrings_AreStrings_NotCode()
    {
        var o = SqlDialects.Oracle;
        Accept(o, "SELECT q'[it's a ; DROP TABLE x]' FROM dual");
        Accept(o, "SELECT Q'{a -- b}' FROM dual");
        Accept(o, "SELECT q'(x)' , q'<y>' FROM dual");
        Accept(o, "SELECT q'!a'b!' FROM dual");          // the delimiter may be any character, here '!'
        Accept(o, "SELECT nq'[ünï]' FROM dual");
        Accept(o, "SELECT NQ'#x#' FROM dual");
        Accept(o, "SELECT q'[a]' || q'[b]' FROM dual");
        // The close is delimiter + quote, so a bracket alone does not end it.
        Accept(o, "SELECT q'[a] ; DROP TABLE x]' FROM dual");
        Reject(o, "SELECT q'[a]' ; DROP TABLE x", DataKeys.RejectMultipleStatements);
        Reject(o, "SELECT q'[abc FROM dual", DataKeys.RejectUnterminated);
        Reject(o, "SELECT q'[abc]x FROM dual", DataKeys.RejectUnterminated);
        Reject(o, "SELECT q' abc ' FROM dual", DataKeys.RejectUnterminated);   // white space cannot be a delimiter
        Reject(o, "SELECT q' ", DataKeys.RejectUnterminated);
        // A lone q is just a name.
        Accept(o, "SELECT q FROM t WHERE q = 'x'");
        // Without q-quote lexing the same text hides a statement: that is why the rule matters.
        Reject(SqlDialects.PostgreSql, "SELECT q'[a ' ; DROP TABLE x ; ']' FROM dual");
        Accept(o, "SELECT q'[a ' ; DROP TABLE x ; ']' FROM dual");
    }

    [Fact]
    public void Oracle_PackagesTheNetworkAndPlSql_AreRefused()
    {
        var o = SqlDialects.Oracle;
        Reject(o, "SELECT UTL_HTTP.REQUEST('http://x') FROM dual", DataKeys.RejectForbiddenFunction, "UTL_HTTP");
        Reject(o, "SELECT utl_file.fopen('a','b','w') FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "SELECT DBMS_XMLGEN.GETXML('delete from t') FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "SELECT dbms_scheduler.create_job(1) FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "SELECT SYS.DBMS_LOCK.SLEEP(1) FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "SELECT SYS.some_pkg.fn(1) FROM dual", DataKeys.RejectForbiddenFunction, "SYS.");
        Reject(o, "SELECT HTTPURITYPE('http://x').getclob() FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "SELECT seq.NEXTVAL FROM dual", DataKeys.RejectForbiddenFunction);
        Reject(o, "WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1 END SELECT f FROM dual", reason: null);
        Reject(o, "WITH function f(x NUMBER) RETURN NUMBER SELECT 1 FROM dual", DataKeys.RejectForbiddenKeyword, "WITH FUNCTION");
        Reject(o, "WITH PROCEDURE p SELECT 1 FROM dual", DataKeys.RejectForbiddenKeyword);
        // Allow-listed packages and plain reads of the catalogue stay possible.
        Accept(o, "SELECT DBMS_LOB.GETLENGTH(c) FROM t");
        Accept(o, "SELECT DBMS_RANDOM.VALUE FROM dual");
        Accept(o, "SELECT * FROM sys.dba_users");
        Accept(o, "SELECT * FROM all_tables WHERE owner = 'HR' FETCH FIRST 5 ROWS ONLY");
        Accept(o, "WITH f AS (SELECT 1 a FROM dual) SELECT a FROM f");
    }

    [Fact]
    public void Oracle_DatabaseLinks_AreRefused_ButParametersAreNot()
    {
        var o = SqlDialects.Oracle;
        Reject(o, "SELECT * FROM t@remote", DataKeys.RejectForbiddenFunction, "@remote");
        Reject(o, "SELECT * FROM \"T\"@remote", DataKeys.RejectForbiddenFunction);
        var r = Check(o, "SELECT * FROM t WHERE a = @a AND b = :b AND c = @a");
        Assert.True(r.IsAllowed);
        Assert.Equal(new[] { "a", "b" }, r.ParameterNames);
        Accept(o, "SELECT a FROM t WHERE x = 'user@host.com'");
        Accept(o, "SELECT a FROM t -- t@link in a comment\n");
    }

    // ---------------------------------------------------------------- DuckDB

    [Fact]
    public void DuckDb_FileReadingTableFunctions_AndFileReferences_AreRefused()
    {
        var d = SqlDialects.DuckDb;
        foreach (string f in new[] { "read_csv('x.csv')", "read_csv_auto('x')", "read_parquet('x.parquet')", "read_json('x')", "read_ndjson('x')", "read_text('x')", "read_blob('x')", "parquet_scan('x')", "glob('*')", "query('select 1')", "iceberg_scan('x')", "sniff_csv('x')" })
            Reject(d, "SELECT * FROM " + f, DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM 'data.csv'", DataKeys.RejectForbiddenFunction, "FROM 'file'");
        Reject(d, "SELECT * FROM 's3://bucket/key.parquet'", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM \"data.parquet\"", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM \"C:\\data\\x\"", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM a, 'b.csv'", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM a JOIN 'b.csv' ON 1 = 1", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM (SELECT * FROM 'x.csv')", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM t WHERE a IN (SELECT a FROM 'x.csv')", DataKeys.RejectForbiddenFunction);
        // Ordinary reads.
        Accept(d, "SELECT * FROM my_table");
        Accept(d, "SELECT * FROM \"my table\"");
        Accept(d, "SELECT * FROM main.\"Orders 2024\"");
        Accept(d, "SELECT * FROM range(10)");
        Accept(d, "SELECT 'a', 'b.csv' FROM t");
        Accept(d, "SELECT * FROM t WHERE name = 'x.csv'");
        Accept(d, "SELECT * FROM t WHERE a IN ('x', 'y') AND b = 'z.parquet'");
        Accept(d, "SELECT * FROM t, u WHERE t.a = u.a AND u.f = 'a.csv'");
        Accept(d, "SELECT $$a;b$$ AS s");
        var withParam = Check(d, "SELECT * FROM t WHERE a = $a AND b = @b");
        Assert.True(withParam.IsAllowed);
        Assert.Equal(new[] { "a", "b" }, withParam.ParameterNames);
    }

    // ---------------------------------------------------------------- BigQuery (GoogleSQL lexical structure)

    [Fact]
    public void BigQuery_TripleQuotedAndRawStrings_AreLexedAsStrings()
    {
        var b = SqlDialects.BigQuery;
        Accept(b, "SELECT '''it's ; DROP TABLE x''' AS a");
        Accept(b, "SELECT \"\"\"two\nlines ; DROP\"\"\" AS a");
        Accept(b, "SELECT r'\\d+' AS a");
        Accept(b, "SELECT R\"\"\"\\d+\"\"\" AS a");
        Accept(b, "SELECT '' AS empty, \"\" AS other");
        Accept(b, "SELECT * FROM `proj.ds.tbl` WHERE a = @a");
        Accept(b, "SELECT 1 # a comment ; DROP TABLE x");
        Accept(b, "SELECT 1 -- another ; DROP TABLE x");
        Accept(b, "SELECT 1 /* c */ , 2");
        Reject(b, "SELECT '''abc", DataKeys.RejectUnterminated);
        Reject(b, "SELECT \"\"\"abc\"\"", DataKeys.RejectUnterminated);
        Reject(b, "SELECT '''a''' ; DROP TABLE x", DataKeys.RejectMultipleStatements);
        // Where a triple-quoted string ends before a fourth quote is not clear-cut, so it is refused.
        Reject(b, "SELECT '''a''''; DROP TABLE x; --", DataKeys.RejectUnterminated);
        // Without triple-quote lexing the same text would hide a statement; the dialect makes it a string.
        Reject(SqlDialects.PostgreSql, "SELECT '''a ' ; DROP TABLE x ; ''' AS a");
        Accept(b, "SELECT '''a ' ; DROP TABLE x ; ''' AS a");
    }

    [Fact]
    public void BigQuery_ExternalQuery_AndHashComments()
    {
        var b = SqlDialects.BigQuery;
        Reject(b, "SELECT * FROM EXTERNAL_QUERY('conn', 'select 1')", DataKeys.RejectForbiddenFunction);
        Reject(b, "SELECT * FROM external_query('c', 'q')", DataKeys.RejectForbiddenFunction);
        Accept(b, "SELECT external_query_name FROM t");
        Reject(b, "SELECT 1 FROM t; DROP TABLE x", DataKeys.RejectMultipleStatements);
        // '#' is a comment for BigQuery, a word character for SQL Server.
        Accept(b, "SELECT 1 # ; DROP TABLE x");
        Reject(SqlDialects.SqlServer, "SELECT 1 # ; DROP TABLE x");
    }

    // ---------------------------------------------------------------- Snowflake

    [Fact]
    public void Snowflake_SlashSlashComments_DollarStrings_AndSystemFunctions()
    {
        var s = SqlDialects.Snowflake;
        Accept(s, "SELECT 1 // ; DROP TABLE x");
        Accept(s, "SELECT 1 // c\nFROM t");
        Accept(s, "SELECT 1 -- c\n, 2");
        Accept(s, "SELECT $$a;b ' \" $$ AS x");
        Accept(s, "SELECT 'it''s' AS x");
        Accept(s, "SELECT * FROM t WHERE a = @p");
        Reject(SqlDialects.PostgreSql, "SELECT 1 // ; DROP TABLE x", DataKeys.RejectMultipleStatements);
        Reject(SqlDialects.SqlServer, "SELECT 1 // ; DROP TABLE x", DataKeys.RejectMultipleStatements);
        Reject(s, "SELECT SYSTEM$CANCEL_ALL_QUERIES(1)", DataKeys.RejectForbiddenFunction);
        Reject(s, "SELECT system$abort_session(1)", DataKeys.RejectForbiddenFunction);
        Accept(s, "SELECT SYSTEM$TYPEOF(1)");
        Reject(s, "SELECT GET_PRESIGNED_URL(@stage, 'f')", DataKeys.RejectForbiddenFunction);
        Reject(s, "SELECT $$unterminated", DataKeys.RejectUnterminated);
        Reject(s, "SELECT 1 /* open", DataKeys.RejectUnterminated);
    }

    // ---------------------------------------------------------------- ClickHouse

    [Fact]
    public void ClickHouse_Comments_Heredocs_AndTableFunctions()
    {
        var c = SqlDialects.ClickHouse;
        Accept(c, "SELECT 1 # comment ; DROP TABLE x");
        Accept(c, "SELECT 1 #! comment ; DROP TABLE x");
        Accept(c, "SELECT 1 -- comment ; DROP TABLE x");
        Accept(c, "SELECT 1 // comment ; DROP TABLE x");
        Accept(c, "SELECT 1 /* a /* nested */ b ; DROP TABLE x */");   // ClickHouse C-style comments nest (its syntax reference)
        Accept(c, "SELECT $h$a;b$h$ AS s");
        Accept(c, "SELECT 'it''s' AS s");
        Accept(c, "SELECT file, url FROM t");                            // plain columns that share a function name
        Accept(c, "SELECT * FROM numbers(10)");
        Accept(c, "SELECT `weird name` FROM t");
        foreach (string f in new[] { "url('http://x', CSV)", "file('x')", "s3('x')", "s3Cluster('c','x')", "remote('h', db.t)", "remoteSecure('h', db.t)", "mysql('h','d','t','u','p')", "postgresql('h','d','t','u','p')", "jdbc('x')", "odbc('x')", "executable('x', 'CSV', 'a UInt8')", "hdfs('x')", "azureBlobStorage('x')", "input('a UInt8')", "cluster('c', db.t)", "clusterAllReplicas('c', db.t)", "mongodb('h')", "sqlite('f', 't')" })
            Reject(c, "SELECT * FROM " + f, DataKeys.RejectForbiddenFunction);
        Reject(c, "SELECT * FROM FILE ('x')", DataKeys.RejectForbiddenFunction);
        Reject(c, "SELECT 1 SETTINGS readonly = 0", DataKeys.RejectForbiddenKeyword, "SETTINGS");
        Reject(c, "SELECT 1 INTO OUTFILE 'x'", DataKeys.RejectSelectInto);
        Reject(c, "SELECT 1 /* open", DataKeys.RejectUnterminated);
        Reject(c, "SELECT $h$ open", DataKeys.RejectUnterminated);
    }

    // ---------------------------------------------------------------- Informix, Db2, Firebird, Jet

    [Fact]
    public void Informix_BraceComments_AndAdminFunctions()
    {
        var i = SqlDialects.Informix;
        Accept(i, "SELECT FIRST 5 * FROM t { a comment ; DROP TABLE x }");
        Accept(i, "SELECT 1 FROM t {c} WHERE a = 1 -- tail");
        Accept(i, "SELECT task FROM t");
        Reject(i, "SELECT 1 FROM t { open", DataKeys.RejectUnterminated);
        Reject(i, "SELECT TASK('drop') FROM sysmaster:sysdual", DataKeys.RejectForbiddenFunction);
        Reject(i, "SELECT admin('x') FROM t", DataKeys.RejectForbiddenFunction);
        // For other engines braces are ordinary characters.
        Reject(SqlDialects.PostgreSql, "SELECT 1 { ; DROP TABLE x }", DataKeys.RejectMultipleStatements);
    }

    [Fact]
    public void Db2_Sequences_AndSystemRoutines()
    {
        var d = SqlDialects.Db2;
        Accept(d, "SELECT * FROM SYSCAT.TABLES FETCH FIRST 5 ROWS ONLY WITH UR");
        Reject(d, "SELECT NEXT VALUE FOR s FROM sysibm.sysdummy1", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT NEXTVAL FOR s FROM sysibm.sysdummy1", DataKeys.RejectForbiddenFunction);
        Reject(d, "SELECT * FROM TABLE(SYSPROC.ADMIN_GET_MSGS('x')) AS t", DataKeys.RejectForbiddenFunction, "SYSPROC.");
        Reject(d, "SELECT SYSFUN.something(1) FROM t", DataKeys.RejectForbiddenFunction);
        Accept(d, "SELECT * FROM SYSPROC.mytable");   // reading a table of that schema is not a call
    }

    [Fact]
    public void Firebird_Generators_AndQQuotes()
    {
        var f = SqlDialects.Firebird;
        Reject(f, "SELECT GEN_ID(g, 1) FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction, "GEN_ID");
        Reject(f, "SELECT NEXT VALUE FOR g FROM RDB$DATABASE", DataKeys.RejectForbiddenFunction);
        Accept(f, "SELECT q'{it's}' FROM RDB$DATABASE");
        Accept(f, "SELECT FIRST 5 SKIP 2 * FROM t");
        Reject(f, "SELECT q'{abc FROM t", DataKeys.RejectUnterminated);
    }

    [Fact]
    public void Jet_DateLiterals_InFileClause_AndVbaFunctions()
    {
        var j = SqlDialects.Jet;
        Accept(j, "SELECT TOP 5 * FROM [My Table] WHERE d = #1/31/2020# AND e > #2020-01-01#");
        Accept(j, "SELECT [a b], c FROM [Orders] WHERE x IN (1, 2)");
        Reject(j, "SELECT * FROM t IN 'other.mdb'", DataKeys.RejectForbiddenFunction, "IN 'file'");
        Reject(j, "SELECT Shell('calc.exe') FROM t", DataKeys.RejectForbiddenFunction);
        Reject(j, "SELECT CreateObject('WScript.Shell') FROM t", DataKeys.RejectForbiddenFunction);
        Accept(j, "SELECT shell FROM t");
    }

    // ---------------------------------------------------------------- MariaDB and the older dialects are unchanged

    [Fact]
    public void MariaDb_UsesTheMySqlLexing_AndRefusesSequences()
    {
        var m = SqlDialects.MariaDb;
        Accept(m, "SELECT `a` FROM t # comment");
        Reject(m, "SELECT /*M! 1 */ 1", DataKeys.RejectExecutableComment);
        Reject(m, "SELECT NEXT VALUE FOR s", DataKeys.RejectForbiddenFunction);
        Reject(m, "SELECT SETVAL(s, 1)", DataKeys.RejectForbiddenFunction);
        Reject(m, "SELECT NEXTVAL(s)", DataKeys.RejectForbiddenFunction);
    }

    // ---------------------------------------------------------------- the ODBC engine hint

    [Fact]
    public void OdbcWithAnEngineHint_UsesThatEnginesRules_AndPositionalParameters()
    {
        var reg = ProviderRegistry.Default;
        var odbc = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, Dsn = "x" };
        var oracle = odbc with { Extras = ProviderExtras.Of(("underlyingEngine", "oracle")) };

        // The hint ADDS the engine's rules (forbidden functions, its own lexing) to the four classic lexings; it never replaces them.
        Assert.True(SqlGuard.Check("SELECT q'[a;b]' FROM dual", oracle, reg).IsAllowed);          // clean under every reading
        Assert.False(SqlGuard.Check("SELECT UTL_HTTP.REQUEST('x') FROM dual", oracle, reg).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT seq.NEXTVAL FROM dual", oracle, reg).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT 1", odbc, reg).IsAllowed);
        // A wrong hint must not open a hole: this text hides a DROP for SQL Server, PostgreSQL and MySQL, and ODBC cannot know which
        // engine is really behind the driver. It is refused with the hint and without it.
        const string hidden = "SELECT q'[a' ; DROP TABLE x ; ']' FROM dual";
        Assert.False(SqlGuard.Check(hidden, odbc, reg).IsAllowed);
        Assert.False(SqlGuard.Check(hidden, oracle, reg).IsAllowed);
        Assert.True(SqlGuard.Check(hidden, SqlDialects.Oracle).IsAllowed);                       // only a real Oracle connection may read it as a string

        var jet = odbc with { Extras = ProviderExtras.Of(("underlyingEngine", "jet")) };
        Assert.False(SqlGuard.Check("SELECT * FROM t IN 'o.mdb'", jet, reg).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT 'a\\' ; DROP TABLE x -- '", jet, reg).IsAllowed);     // a MySQL behind a jet hint is not a bypass
        Assert.True(SqlGuard.Check("SELECT * FROM [My Table] WHERE d = #1/31/2020#", jet, reg).IsAllowed);
        Assert.Equal(ParameterStyle.Positional, reg.ResolveDialect(jet).ParameterStyle);
        Assert.Equal(5, reg.ResolveDialect(oracle).LexRules.Count);
        Assert.True((reg.ResolveDialect(oracle).AmbiguityChecks & LexFeature.AnyBackslash) == LexFeature.AnyBackslash);
    }

    [Fact]
    public void TheSettingsOverload_PicksTheDialectOfTheConnection()
    {
        var mariadb = new DatabaseConnectionSettings { Name = "m", Provider = DatabaseProvider.MySql, Host = "h", UserName = "u", Extras = ProviderExtras.Of(("flavor", "mariadb")) };
        Assert.False(SqlGuard.Check("SELECT NEXT VALUE FOR s", mariadb).IsAllowed);
        var custom = new DatabaseConnectionSettings { Name = "c", Provider = DatabaseProvider.Custom, ProviderId = "not-loaded" };
        Assert.Equal(ProviderKeys.ProviderUnavailable, Assert.Throws<DatabaseException>(() => SqlGuard.Check("SELECT 1", custom)).Key);
    }

    // ---------------------------------------------------------------- classic lexing still behaves

    [Fact]
    public void NestedComments_AreOnlyNested_WhereTheEngineNestsThem()
    {
        const string hidden = "SELECT 1 /* a /* b */ ; DROP TABLE x */";
        Accept(SqlDialects.PostgreSql, hidden);
        Accept(SqlDialects.ClickHouse, hidden);
        Reject(SqlDialects.SqlServer, hidden, DataKeys.RejectMultipleStatements);
        Reject(SqlDialects.MySql, hidden, DataKeys.RejectMultipleStatements);
        Reject(SqlDialects.Oracle, hidden, DataKeys.RejectMultipleStatements);
        Accept(SqlDialects.DuckDb, hidden);   // verified against DuckDB 1.5: SELECT 1 /* a /* b */ c */ runs, so the comment nests
        Reject(SqlDialects.OdbcUnknown, hidden);
    }

    [Fact]
    public void DollarQuotes_AreStrings_OnlyWhereTheEngineHasThem()
    {
        Accept(SqlDialects.PostgreSql, "SELECT $$ ; DROP TABLE x $$");
        Accept(SqlDialects.PostgreSql, "SELECT $a$ x $b$ ; DROP $a$");
        Accept(SqlDialects.PostgreSql, "SELECT E'a\\'b ; x'");
        Reject(SqlDialects.PostgreSql, "SELECT $$ open", DataKeys.RejectUnterminated);
        Accept(SqlDialects.PostgreSql, "SELECT a FROM t WHERE b = $1");   // $1 is a positional parameter, never a tag
        Reject(SqlDialects.SqlServer, "SELECT $$ ; DROP TABLE x $$", DataKeys.RejectMultipleStatements);
        Accept(SqlDialects.Snowflake, "SELECT $$ ; DROP TABLE x $$");
        Accept(SqlDialects.ClickHouse, "SELECT $$ ; DROP TABLE x $$");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void UnterminatedTokens_AreRefused_InEveryDialect(string id)
    {
        var d = SqlDialects.ById(id)!;
        foreach (string sql in new[] { "SELECT 'abc", "SELECT 1 /* open", "SELECT \"abc" })
            Reject(d, sql, DataKeys.RejectUnterminated);
    }

    // ---------------------------------------------------------------- side-effect functions a read-only session does not block

    [Theory]
    [InlineData("SELECT pg_advisory_lock(1)")]
    [InlineData("SELECT pg_advisory_lock_shared(1)")]
    [InlineData("SELECT pg_advisory_xact_lock(1)")]
    [InlineData("SELECT pg_advisory_xact_lock_shared(1, 2)")]
    [InlineData("SELECT pg_try_advisory_lock(1)")]
    [InlineData("SELECT pg_try_advisory_xact_lock_shared(1)")]
    [InlineData("SELECT pg_advisory_unlock(1)")]
    [InlineData("SELECT pg_advisory_unlock_all()")]
    [InlineData("SELECT pg_reload_conf()")]
    [InlineData("SELECT pg_switch_wal()")]
    [InlineData("SELECT pg_rotate_logfile()")]
    [InlineData("SELECT pg_create_restore_point('x')")]
    [InlineData("SELECT pg_backup_start('x')")]
    [InlineData("SELECT pg_start_backup('x')")]
    [InlineData("SELECT pg_notify('c', 'p')")]
    [InlineData("SELECT pg_stat_reset()")]
    [InlineData("SELECT pg_create_logical_replication_slot('s', 'test_decoding')")]
    [InlineData("SELECT PG_ADVISORY_LOCK(1)")]
    [InlineData("SELECT \"pg_advisory_lock\"(1)")]
    public void PostgreSql_RefusesLocksAndAdministrationFunctions(string sql)
        => Reject(SqlDialects.PostgreSql, sql, DataKeys.RejectForbiddenFunction);

    [Theory]
    [InlineData("SELECT pg_sleep(1)")]
    [InlineData("SELECT pg_backend_pid(), current_setting('server_version'), pg_is_in_recovery()")]
    [InlineData("SELECT pg_size_pretty(pg_database_size('x'))")]
    [InlineData("SELECT * FROM pg_stats")]
    [InlineData("SELECT pg_advisory FROM t")]
    [InlineData("SELECT * FROM pg_locks WHERE locktype = 'advisory'")]
    public void PostgreSql_StillAcceptsHarmlessFunctionsAndLookAlikes(string sql)
        => Accept(SqlDialects.PostgreSql, sql);

    [Theory]
    [InlineData("SELECT GET_LOCK('x', 0)")]
    [InlineData("SELECT get_lock ('x', 0)")]
    [InlineData("SELECT RELEASE_LOCK('x')")]
    [InlineData("SELECT RELEASE_ALL_LOCKS()")]
    [InlineData("SELECT BENCHMARK(1000000, MD5('x'))")]
    [InlineData("SELECT MASTER_POS_WAIT('a', 1)")]
    [InlineData("SELECT SOURCE_POS_WAIT('a', 1)")]
    [InlineData("SELECT MASTER_GTID_WAIT('0-1-1', 1)")]
    [InlineData("SELECT WAIT_FOR_EXECUTED_GTID_SET('x')")]
    [InlineData("SELECT `GET_LOCK`('x', 0)")]
    public void MySqlAndMariaDb_RefuseLocksAndWaits(string sql)
    {
        Reject(SqlDialects.MySql, sql, DataKeys.RejectForbiddenFunction);
        Reject(SqlDialects.MariaDb, sql, DataKeys.RejectForbiddenFunction);
    }

    [Theory]
    [InlineData("SELECT SLEEP(1)")]                                   // bounded by the command timeout; the tests of cancellation and timeouts use it
    [InlineData("SELECT IS_FREE_LOCK('x'), IS_USED_LOCK('x')")]       // read-only questions about locks
    [InlineData("SELECT benchmark, get_lock FROM t")]                 // columns named like the functions are not calls
    [InlineData("SELECT UUID(), NOW(6), LAST_INSERT_ID()")]
    public void MySqlAndMariaDb_StillAcceptHarmlessFunctionsAndLookAlikes(string sql)
    {
        Accept(SqlDialects.MySql, sql);
        Accept(SqlDialects.MariaDb, sql);
    }

    // ---------------------------------------------------------------- CHARACTER SET is a type clause, SET is a statement

    [Theory]
    [InlineData("SELECT CAST('a' AS CHAR CHARACTER SET latin1)")]
    [InlineData("SELECT CAST(x AS CHAR(10) CHARACTER SET utf8mb4) FROM t")]
    [InlineData("SELECT CONVERT('a', CHAR CHARACTER SET latin1)")]
    [InlineData("select cast(x as char character\tset latin1) from t")]
    public void MySqlAndMariaDb_AcceptCharacterSetInsideACast(string sql)
    {
        Accept(SqlDialects.MySql, sql);
        Accept(SqlDialects.MariaDb, sql);
    }

    [Theory]
    [InlineData("SET @x = 1")]
    [InlineData("SELECT 1 SET @x = 1")]
    [InlineData("SELECT character SET @x = 1")]                         // not inside a cast: still the SET statement
    [InlineData("SELECT (1) character SET @x = 1")]
    [InlineData("SELECT CAST(1 AS CHAR) SET @x = 1")]
    [InlineData("SELECT CAST(1 AS CHAR CHARSET latin1), (SET @x = 1)")]
    [InlineData("SELECT CAST(1 AS CHAR); SET @x = 1")]
    public void MySqlAndMariaDb_StillRefuseTheSetStatement(string sql)
    {
        Assert.False(Check(SqlDialects.MySql, sql).IsAllowed, sql);
        Assert.False(Check(SqlDialects.MariaDb, sql).IsAllowed, sql);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void CharacterSetClause_IsNeverAcceptedOutsideMySql(string id)
    {
        if (id is "mysql" or "mariadb") return;
        var d = SqlDialects.ById(id)!;
        // In T-SQL this is two statements (an alias, then SET); other engines have no such clause either.
        Assert.False(Check(d, "SELECT (1 character SET NOCOUNT ON)").IsAllowed, id);
        Assert.False(Check(d, "SELECT CAST(1 AS CHAR CHARACTER SET latin1)").IsAllowed, id);
    }

    [Fact]
    public void AnOdbcHintOfMySql_DoesNotAcceptTheCharacterSetClause()
    {
        // The hint is only a claim; it may add rules but never loosen them.
        var settings = new DatabaseConnectionSettings
        {
            Name = "o", Provider = DatabaseProvider.Odbc, Dsn = "x", UserName = "u", Authentication = DatabaseAuthentication.UserPassword,
            Extras = ProviderExtras.Of(("underlyingEngine", "mysql")),
        };
        var dialect = ProviderRegistry.Default.ResolveDialect(settings);
        Assert.False(dialect.AllowsCharacterSetClause);
        Assert.False(Check(dialect, "SELECT CAST(1 AS CHAR CHARACTER SET latin1)").IsAllowed);
        Assert.Contains("GET_LOCK", dialect.ForbiddenCalls);
    }

    [Fact]
    public void OnlyPostgreSql_ReadsTheSchemaWithKeyInfo()
        => Assert.Equal(new[] { "postgresql" }, SqlDialects.All.Where(d => d.DescribeWithKeyInfo).Select(d => d.Id).ToArray());

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public void MySqlCatalogue_ListsSystemVersionedTables(string id)
        => Assert.Contains("'SYSTEM VERSIONED'", SqlDialects.ById(id)!.CatalogueSql);
}
