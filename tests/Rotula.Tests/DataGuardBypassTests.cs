using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// Bypass attempts against the read-only SQL guard. Opening a .rotula label automatically runs its saved SELECT through the guard on the
/// user's own saved connection, so a text that slips through executes just from opening someone else's file. Every case here is either a
/// reproduced bypass (kept failing until fixed) or a sibling the guard already handled (kept as a regression lock).
/// </summary>
public class DataGuardBypassTests
{
    private const DatabaseProvider Ms = DatabaseProvider.SqlServer;
    private const DatabaseProvider Pg = DatabaseProvider.PostgreSql;
    private const DatabaseProvider My = DatabaseProvider.MySql;
    private const DatabaseProvider Lite = DatabaseProvider.Sqlite;
    private const DatabaseProvider Odbc = DatabaseProvider.Odbc;

    private static readonly DatabaseProvider[] Providers = { Ms, Pg, My, Lite, Odbc };

    public static IEnumerable<object[]> AllDialects => SqlDialects.All.Select(d => new object[] { d.Id });

    private static void RejectOnEveryProvider(string sql, string? reason = null)
    {
        foreach (var p in Providers)
        {
            var r = SqlGuard.Check(sql, p);
            Assert.False(r.IsAllowed, $"{p} should refuse: {sql}");
            if (reason is not null) Assert.Equal(reason, r.ReasonKey);
        }
    }

    private static void RejectOnEveryDialect(string sql, string? reason = null)
    {
        foreach (var d in SqlDialects.All)
        {
            var r = SqlGuard.Check(sql, d);
            Assert.False(r.IsAllowed, $"{d.Id} should refuse: {sql}");
            if (reason is not null) Assert.Equal(reason, r.ReasonKey);
        }
    }

    private static void AcceptOnEveryProvider(string sql)
    {
        foreach (var p in Providers)
        {
            var r = SqlGuard.Check(sql, p);
            Assert.True(r.IsAllowed, $"{p} should accept: {sql} -> {r.ReasonKey} {r.Arg}");
        }
    }

    // ---- finding 1: PostgreSQL U&"..." unicode-escaped names -----------------------------------------------

    [Theory]
    [InlineData("SELECT U&\"pg_r\\0065ad_file\"('/etc/passwd')")]
    [InlineData("SELECT u&\"pg_r\\0065ad_file\"('/etc/passwd')")]
    [InlineData("SELECT pg_catalog.U&\"pg_r\\0065ad_file\"('/etc/passwd')")]
    [InlineData("SELECT U&\"d\\+000062link\"('host=evil', 'select 1')")]
    [InlineData("SELECT U&\"x!0065\" UESCAPE '!' FROM t")]
    [InlineData("SELECT U&'d\\0061ta' FROM t")]
    [InlineData("SELECT u&'x'")]
    public void UnicodeEscapePrefix_IsRefused_InEveryDialect(string sql)
        => RejectOnEveryDialect(sql, DataKeys.RejectUnclearText);

    [Theory]
    [InlineData("SELECT \"a\\b\" FROM t")]
    [InlineData("SELECT \"pg_r\\0065ad_file\"('/etc/passwd')")]
    [InlineData("SELECT \"trailing\\\" FROM t")]
    public void PostgreSql_RefusesABackslashInsideAQuotedName(string sql)
    {
        foreach (var dialect in new[] { SqlDialects.PostgreSql, SqlDialects.DuckDb })
        {
            var r = SqlGuard.Check(sql, dialect);
            Assert.False(r.IsAllowed, $"{dialect.Id} should refuse: {sql}");
        }
        Assert.False(SqlGuard.Check(sql, Pg).IsAllowed);
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed);
    }

    [Fact]
    public void PostgreSql_StillAcceptsEscapeStringsAndAmpersands()
    {
        // E'...' is lexed with its backslash escapes, so the guard sees exactly what the engine sees.
        Assert.True(SqlGuard.Check("SELECT E'it\\'s; fine' FROM t", Pg).IsAllowed);
        // A bitwise AND with spaces, and a column that merely ends in u, are ordinary SQL.
        Assert.True(SqlGuard.Check("SELECT a & 1, flu & 2, u & 3 FROM t", Pg).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT flu&\"x\" FROM t", Pg).IsAllowed); // glued to a longer word: not the U& prefix
        Assert.True(SqlGuard.Check("SELECT \"Name\", \"Unit\" FROM \"Products\"", Pg).IsAllowed);
    }

    // ---- finding 2: PostgreSQL functions that run text as SQL, read server state or files ---------------------

    [Theory]
    [InlineData("SELECT query_to_xml('select pg_read_file(''x'')', true, false, '')")]
    [InlineData("SELECT query_to_xml_and_xmlschema('select 1', true, false, '')")]
    [InlineData("SELECT query_to_xmlschema('select 1', true, false, '')")]
    [InlineData("SELECT cursor_to_xml('c'::refcursor, 1, true, false, '')")]
    [InlineData("SELECT cursor_to_xmlschema('c'::refcursor, true, false, '')")]
    [InlineData("SELECT table_to_xml('t'::regclass, true, false, '')")]
    [InlineData("SELECT table_to_xml_and_xmlschema('t'::regclass, true, false, '')")]
    [InlineData("SELECT table_to_xmlschema('t'::regclass, true, false, '')")]
    [InlineData("SELECT schema_to_xml('public', true, false, '')")]
    [InlineData("SELECT schema_to_xml_and_xmlschema('public', true, false, '')")]
    [InlineData("SELECT database_to_xml(true, false, '')")]
    [InlineData("SELECT * FROM ts_stat('select to_tsvector(a) from t')")]
    [InlineData("SELECT pg_catalog.query_to_xml('select 1', true, false, '')")]
    [InlineData("SELECT \"query_to_xml\"('select 1', true, false, '')")]
    [InlineData("SELECT QUERY_TO_XML('select 1', true, false, '')")]
    [InlineData("SELECT lo_import('/etc/passwd')")]
    [InlineData("SELECT lo_export(1, '/tmp/x')")]
    [InlineData("SELECT lo_get(1)")]
    [InlineData("SELECT lo_open(1, 262144)")]
    [InlineData("SELECT lo_creat(-1)")]
    [InlineData("SELECT lo_unlink(1)")]
    [InlineData("SELECT lo_put(1, 0, 'x')")]
    [InlineData("SELECT loread(0, 10)")]
    [InlineData("SELECT lowrite(0, 'x')")]
    [InlineData("SELECT pg_read_file('/etc/passwd')")]
    [InlineData("SELECT pg_read_binary_file('/etc/passwd')")]
    [InlineData("SELECT * FROM pg_ls_dir('/')")]
    [InlineData("SELECT * FROM pg_ls_logicalsnapdir()")]
    [InlineData("SELECT * FROM pg_stat_file('/etc/passwd')")]
    [InlineData("SELECT * FROM pg_ls_waldir()")]
    [InlineData("SELECT dblink_open('c', 'select 1')")]
    [InlineData("SELECT dblink_fetch('c', 1)")]
    [InlineData("SELECT dblink_send_query('c', 'select 1')")]
    [InlineData("SELECT * FROM dblink_get_result('c') AS t(a int)")]
    [InlineData("SELECT dblink_connect_u('host=evil')")]
    [InlineData("SELECT * FROM dblink('host=evil', 'select 1') AS t(a int)")]
    [InlineData("SELECT set_config('statement_timeout', '0', false)")]
    [InlineData("SELECT pg_terminate_backend(1)")]
    [InlineData("SELECT pg_cancel_backend(1)")]
    [InlineData("SELECT pg_advisory_lock(1)")]
    [InlineData("SELECT * FROM pg_logical_slot_get_changes('s', NULL, NULL)")]
    [InlineData("SELECT * FROM pg_logical_slot_peek_changes('s', NULL, NULL)")]
    [InlineData("SELECT pg_log_backend_memory_contexts(1)")]
    [InlineData("SELECT pg_file_write('/tmp/x', 'y', false)")]
    [InlineData("SELECT pg_current_xact_id()")]
    [InlineData("SELECT txid_current()")]
    [InlineData("SELECT nextval('s')")]
    [InlineData("SELECT setval('s', 1)")]
    public void PostgreSql_RefusesFunctionsThatRunTextReadFilesOrChangeServerState(string sql)
    {
        var r = SqlGuard.Check(sql, SqlDialects.PostgreSql);
        Assert.False(r.IsAllowed, sql);
        Assert.Equal(DataKeys.RejectForbiddenFunction, r.ReasonKey);
        // The built-in provider overload resolves to the same dialect and must agree.
        Assert.False(SqlGuard.Check(sql, Pg).IsAllowed, sql);
        // An ODBC connection to a PostgreSQL server has no PostgreSQL dialect (the engine hint is only a claim), and the SQL such a function
        // runs sits inside a string, so the shared lists must refuse it too: with no hint, with a postgresql hint and with a wrong hint.
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed, "ODBC: " + sql);
        foreach (string engine in new[] { "postgresql", "mysql", "sqlserver", "oracle" })
        {
            var hinted = new DatabaseConnectionSettings
            {
                Name = "o", Provider = DatabaseProvider.Odbc, Dsn = "x",
                Extras = ProviderExtras.Of(("underlyingEngine", engine)),
            };
            Assert.False(SqlGuard.Check(sql, hinted).IsAllowed, $"ODBC hinted {engine}: {sql}");
        }
    }

    [Theory]
    [InlineData("SELECT * FROM ts_rewrite('a & b'::tsquery, 'select t, s from aliases')")]
    [InlineData("SELECT ts_rewrite(q, 'select t, s from aliases') FROM t")]
    [InlineData("SELECT * FROM crosstab('select a, b, c from t')")]
    [InlineData("SELECT * FROM crosstab('select 1', 'select 2') AS ct(a int)")]
    [InlineData("SELECT * FROM connectby('t', 'id', 'parent', 'root', 0) AS c(id int)")]
    public void PostgreSql_RefusesFunctionsThatRunTheirArgumentAsSql_OnEveryDialectIncludingOdbc(string sql)
    {
        Assert.False(SqlGuard.Check(sql, SqlDialects.PostgreSql).IsAllowed, sql);
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed, sql);
    }

    [Fact]
    public void Oracle_StillAcceptsBinaryFloatSuffixes()
        => Assert.True(SqlGuard.Check("SELECT 1.5f, 2d, 3.0D FROM dual", SqlDialects.Oracle).IsAllowed);

    [Theory]
    [InlineData("SELECT xmlelement(name a, 1), xpath('/a', x::xml) FROM t")]
    [InlineData("SELECT lo_total, lo_count, local, lower(a) FROM t")]            // lookalikes that are not calls of lo_*
    [InlineData("SELECT query_total, table_total, cursor_pos FROM t")]
    [InlineData("SELECT to_tsvector('english', a), ts_rank(v, q) FROM t")]
    [InlineData("SELECT pg_sleep(1)")]                                           // bounded by the command timeout; tests of cancellation use it
    [InlineData("SELECT pg_size_pretty(pg_database_size('x')), current_setting('server_version')")]
    [InlineData("SELECT count(*), sum(a), max(b) FROM t GROUP BY c ORDER BY 1 DESC LIMIT 10")]
    [InlineData("SELECT 'query_to_xml(x)' AS note FROM t")]                      // inside a string it is data
    public void PostgreSql_StillAcceptsAnalyticQueries(string sql)
        => Assert.True(SqlGuard.Check(sql, SqlDialects.PostgreSql).IsAllowed, sql);

    // ---- finding 3: words that start with a digit -------------------------------------------------------------

    [Theory]
    [InlineData("SELECT 1DROP TABLE x")]
    [InlineData("SELECT 1UPDATE t SET a = 1")]
    [InlineData("SELECT 1DELETE FROM t")]
    [InlineData("SELECT 1INSERT INTO t VALUES (1)")]
    [InlineData("SELECT 1EXEC sp_who")]
    [InlineData("SELECT 1AS x")]                                   // SQL Server accepts this, but the readings differ: refused
    [InlineData("SELECT 1.5DROP TABLE x")]
    [InlineData("SELECT .5DROP TABLE x")]
    [InlineData("SELECT 1e1DROP TABLE x")]
    [InlineData("SELECT 1E1DROP TABLE x")]
    [InlineData("SELECT 0x1DROP TABLE x")]
    [InlineData("SELECT 1_DROP TABLE x")]
    [InlineData("SELECT 12abc FROM t")]
    [InlineData("SELECT * FROM 2024sales")]
    [InlineData("SELECT $1DROP TABLE x")]
    [InlineData("SELECT \u0663DROP TABLE x")]                      // Arabic-Indic digit three
    [InlineData("SELECT 1pg_read_file('/etc/passwd')")]
    public void DigitLedWords_ThatAreNotNumbers_AreRefused_OnEveryProvider(string sql)
        => RejectOnEveryProvider(sql, DataKeys.RejectUnclearText);

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void DigitLedWords_ThatAreNotNumbers_AreRefused_InEveryDialect(string id)
    {
        var d = SqlDialects.ById(id)!;
        foreach (string sql in new[] { "SELECT 1DROP TABLE x", "SELECT 1UPDATE t SET a = 1", "SELECT 1e1DELETE FROM t" })
        {
            var r = SqlGuard.Check(sql, d);
            Assert.False(r.IsAllowed, $"{id}: {sql}");
        }
    }

    [Theory]
    [InlineData("SELECT 1 DROP TABLE x")]
    [InlineData("SELECT 1/**/DROP TABLE x")]
    [InlineData("SELECT 1\tDROP TABLE x")]
    [InlineData("SELECT 1\nDROP TABLE x")]
    [InlineData("SELECT 1\u000BDROP TABLE x")]
    [InlineData("SELECT 1\u000CDROP TABLE x")]
    [InlineData("SELECT 1\u00A0DROP TABLE x")]                     // no-break space
    [InlineData("SELECT 1\u2003DROP TABLE x")]                     // em space
    [InlineData("SELECT 1\u3000DROP TABLE x")]                     // ideographic space
    [InlineData("SELECT 1\u2028DROP TABLE x")]                     // line separator
    [InlineData("SELECT 1\u0085DROP TABLE x")]                     // next line
    [InlineData("SELECT 1.DROP TABLE x")]
    [InlineData("SELECT 1 -- c\nDROP TABLE x")]
    [InlineData("SELECT 1 /* c */ DROP TABLE x")]
    [InlineData("SELECT (1)DROP TABLE x")]
    [InlineData("SELECT 'a'DROP TABLE x")]
    [InlineData("SELECT \"a\"DROP TABLE x")]
    public void SeparatorFreeStatements_AreRefused_OnEveryProvider(string sql)
        => RejectOnEveryProvider(sql, DataKeys.RejectForbiddenKeyword);

    [Theory]
    [InlineData("SELECT 1, 2.5, .5, 1e10, 1E-5, 1.5e3, 5E3, 0x1F, 0XdeadBEEF, 1_000 FROM t")]
    [InlineData("SELECT a FROM t WHERE x = 10 AND y >= 3.14 AND z < 1e-3")]
    [InlineData("SELECT -1, +2, 3-4, 5*6, 7/8, 9%2 FROM t")]
    [InlineData("SELECT a1, b2c3, _x9, t1.c2 FROM t1")]
    [InlineData("SELECT a FROM t WHERE id IN (1,2,3) AND n BETWEEN 10 AND 20")]
    public void NumbersAndNamesWithDigits_StillWork_OnEveryProvider(string sql)
        => AcceptOnEveryProvider(sql);

    [Fact]
    public void HashAfterANumber_OnlyClosesADateLiteral_ItNeverGluesAKeyword()
    {
        // Jet/ACE date literals: the closing # is glued to the last number.
        Assert.True(SqlGuard.Check("SELECT * FROM t WHERE d = #1/31/2020# AND e > #2020-01-01 10:30#", SqlDialects.Jet).IsAllowed);
        // "1#DROP" is one word where # is a name character (SQL Server, PostgreSQL, SQLite) and a comment in MySQL.
        foreach (var p in new[] { Ms, Pg, Lite, Odbc })
            Assert.False(SqlGuard.Check("SELECT 1#DROP TABLE x", p).IsAllowed, p.ToString());
        Assert.False(SqlGuard.Check("SELECT 1#DROP TABLE x", SqlDialects.Jet).IsAllowed);
    }

    // ---- siblings: hidden characters, NUL, full-width forms --------------------------------------------------

    [Theory]
    [InlineData("SELECT 1 DR\0OP TABLE x")]
    [InlineData("SELECT 1\0")]
    [InlineData("SELECT 'a\0b'")]
    [InlineData("SELECT 1 /* \0 */")]
    [InlineData("SELECT 1 -- \0\n")]
    public void NulBytes_AreRefused_InEveryDialect(string sql)
        => RejectOnEveryDialect(sql, DataKeys.RejectUnclearText);

    [Theory]
    [InlineData("SELECT 1 DR\u200BOP TABLE x")]                    // zero-width space
    [InlineData("SELECT 1 DR\u200DOP TABLE x")]                    // zero-width joiner
    [InlineData("SELECT 1 DR\u2060OP TABLE x")]                    // word joiner
    [InlineData("SELECT 1 DR\uFEFFOP TABLE x")]                    // zero-width no-break space / BOM
    [InlineData("SELECT 1 DR\u00ADOP TABLE x")]                    // soft hyphen
    [InlineData("SELECT 1 DR\u202EOP TABLE x")]                    // right-to-left override
    [InlineData("SELECT 1 DR\u0001OP TABLE x")]                    // C0 control
    [InlineData("SELECT 1 DR\u007FOP TABLE x")]                    // DEL
    [InlineData("SELECT 1 DR\u0090OP TABLE x")]                    // C1 control
    [InlineData("\uFEFFSELECT 1")]
    public void HiddenAndControlCharacters_OutsideLiterals_AreRefused_InEveryDialect(string sql)
        => RejectOnEveryDialect(sql, DataKeys.RejectUnclearText);

    [Theory]
    [InlineData("SELECT 'a\u200Bb', \"c\u200Bd\" FROM t")]         // inside a string or a quoted name they are plain data
    [InlineData("SELECT 1 /* \u200B */")]
    [InlineData("SELECT 1 -- \u202E\n")]
    public void HiddenCharacters_InsideLiteralsAndComments_AreData(string sql)
        => AcceptOnEveryProvider(sql);

    [Theory]
    [InlineData("SELECT 1 \uFF24\uFF32\uFF2F\uFF30 TABLE x")]       // full-width DROP
    [InlineData("SELECT 1 \uFF44\uFF52\uFF4F\uFF50 TABLE x")]       // full-width drop
    [InlineData("SELECT 1 \u00F1DROP TABLE x")]                    // non-ASCII letter glued to the keyword
    [InlineData("SELECT 1 \u0870DROP TABLE x")]                    // a letter newer than some engines' Unicode tables
    [InlineData("SELECT 1 DROP\u00F1 TABLE x")]
    [InlineData("SELECT 1 \u00F1UPDATE t SET a = 1")]
    [InlineData("SELECT \u00F1pg_read_file('/etc/passwd')")]
    public void NonAsciiWords_CannotHideAKeyword_OnEveryProvider(string sql)
    {
        foreach (var p in Providers)
            Assert.False(SqlGuard.Check(sql, p).IsAllowed, $"{p} should refuse: {sql}");
    }

    [Fact]
    public void AccentedNames_StillWork_OnEveryProvider()
    {
        AcceptOnEveryProvider("SELECT descripci\u00F3n, a\u00F1o, \u00D1and\u00FA FROM tabla_\u00F1 WHERE nombre = 'Jos\u00E9' ORDER BY a\u00F1o");
        AcceptOnEveryProvider("SELECT a.coraz\u00F3n AS \u00E9xito FROM t a");
    }

    // ---- siblings: comments ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT 1 /*! DROP TABLE t */")]
    [InlineData("SELECT 1 /*!50000 DROP TABLE t */")]
    [InlineData("SELECT 1 /*!50000 UNION SELECT 2 */")]
    [InlineData("SELECT 1 /*M! DROP TABLE t */")]
    [InlineData("SELECT 1 /*M!100100 DROP TABLE t */")]
    [InlineData("SELECT 1/*!*/")]
    public void ExecutableComments_AreRefused_InEveryDialect(string sql)
        => RejectOnEveryDialect(sql, DataKeys.RejectExecutableComment);

    [Theory]
    [InlineData("SELECT 1 --\u00A0; DROP TABLE x")]                // NBSP is not the whitespace MySQL needs after "--"
    [InlineData("SELECT 1 --\u2003; DROP TABLE x")]
    [InlineData("SELECT 1 --\u3000; DROP TABLE x")]
    [InlineData("SELECT 1 --x; DROP TABLE x")]
    [InlineData("SELECT 1 --; DROP TABLE x")]
    public void MySql_DoubleDashNeedsRealWhitespace(string sql)
    {
        Assert.False(SqlGuard.Check(sql, My).IsAllowed, sql);
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed, sql);
    }

    [Theory]
    [InlineData("SELECT 1 -- c\u2028DROP TABLE x")]                // an engine may end a line comment at these; the guard ends it there too
    [InlineData("SELECT 1 -- c\u2029DROP TABLE x")]
    [InlineData("SELECT 1 -- c\u0085DROP TABLE x")]
    [InlineData("SELECT 1 -- c\u000BDROP TABLE x")]
    [InlineData("SELECT 1 -- c\u000CDROP TABLE x")]
    [InlineData("SELECT 1 -- c\rDROP TABLE x")]
    [InlineData("SELECT 1 # c\u2028DROP TABLE x")]
    public void LineComments_EndAtEveryLineBreakAnEngineMightUse(string sql)
        => RejectOnEveryProvider(sql, DataKeys.RejectForbiddenKeyword);

    [Fact]
    public void NestedComments_PostgreSqlNestsMySqlDoesNot()
    {
        const string hidden = "SELECT 1 /* a /* b */ DROP TABLE t */";
        // PostgreSQL really nests, so the DROP is inside the comment; every engine that does not nest sees it.
        Assert.True(SqlGuard.Check(hidden, Pg).IsAllowed);
        Assert.False(SqlGuard.Check(hidden, My).IsAllowed);
        Assert.False(SqlGuard.Check(hidden, Ms).IsAllowed);
        Assert.False(SqlGuard.Check(hidden, Lite).IsAllowed);
        Assert.False(SqlGuard.Check(hidden, Odbc).IsAllowed);
        // After the comment closes the statement is live in every engine.
        RejectOnEveryProvider("SELECT 1 /* a /* b */ */ DROP TABLE t", DataKeys.RejectForbiddenKeyword);
    }

    // ---- siblings: SQL Server brackets and N'...' strings --------------------------------------------------------

    [Fact]
    public void SqlServer_BracketNamesAndNationalStrings()
    {
        // A doubled ] stays inside the name, so this is ONE identifier.
        Assert.True(SqlGuard.Check("SELECT [a]]; DROP TABLE x --] FROM t", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT [DROP], [Use] FROM [delete]", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT [a] DROP TABLE x", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT [a]; DROP TABLE x", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT [a]]; DROP TABLE x", Ms).IsAllowed);   // never closed
        // N'...' is a string; a doubled quote stays inside it.
        Assert.True(SqlGuard.Check("SELECT N'x''; DROP TABLE y; --' AS s", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT n'x' FROM t", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT N'x'; DROP TABLE y", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT N'x' DROP TABLE y", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT N'never closed", Ms).IsAllowed);
        // The strict ODBC reading refuses the construct that only means something in T-SQL.
        Assert.False(SqlGuard.Check("SELECT [a]]; DROP TABLE x --] FROM t", Odbc).IsAllowed);
    }
}
