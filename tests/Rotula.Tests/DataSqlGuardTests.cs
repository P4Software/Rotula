using Rotula.Data;

namespace Rotula.Tests;

/// <summary>The read-only gate: accepted statements, refused statements (with reason keys) and tricky lexing cases.</summary>
public class DataSqlGuardTests
{
    private const DatabaseProvider Ms = DatabaseProvider.SqlServer;
    private const DatabaseProvider Pg = DatabaseProvider.PostgreSql;
    private const DatabaseProvider My = DatabaseProvider.MySql;
    private const DatabaseProvider Lite = DatabaseProvider.Sqlite;
    private const DatabaseProvider Odbc = DatabaseProvider.Odbc;

    private static readonly DatabaseProvider[] All = { Ms, Pg, My, Lite, Odbc };

    public static IEnumerable<object[]> AcceptedEverywhere() => new[]
    {
        "SELECT 1",
        "select * from t",
        "  SELECT a, b FROM dbo.Customers WHERE name = 'x' ;  ",
        "SELECT 1;",
        "SELECT 1; -- trailing comment after the one allowed semicolon",
        "SELECT 1; /* block */",
        "WITH x AS (SELECT 1 AS a) SELECT a FROM x",
        "(SELECT 1) UNION ALL (SELECT 2)",
        "SELECT 'a;b' AS semi",
        "SELECT 'it''s; fine'",
        "SELECT \"col;umn\" FROM t",
        "SELECT 'DROP TABLE x' AS text",
        "SELECT 1 /* ; DROP TABLE x */",
        "SELECT 1 -- ; DROP TABLE x\n",
        "SELECT REPLACE(name, 'a', 'b') FROM t",
        "SELECT a FROM t WHERE b = @p AND c = @Other",
        "SELECT UPDATED_AT, DELETED, Inserted_By FROM t",
        "SELECT created FROM t",
        "SELECT \"Call\" FROM t",
        "SELECT a FROM t ORDER BY a LIMIT 10",
        "SELECT x FROM t WHERE y LIKE '%--%'",
        "SELECT x FROM t WHERE y = N'café; ñ'",
        "SELECT 1 AS \"select\"",
        "SELECT \"Name\", \"Price\" FROM \"Products\"",
        "SELECT \"my_func\"(1) FROM t",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AcceptedEverywhere))]
    public void AcceptsPlainSelects_OnEveryProvider(string sql)
    {
        foreach (var p in All)
        {
            var r = SqlGuard.Check(sql, p);
            Assert.True(r.IsAllowed, $"{p}: {sql} -> {r.ReasonKey} {r.Arg}");
        }
    }

    public static IEnumerable<object[]> RejectedEverywhere() => new (string Sql, string Reason)[]
    {
        ("", DataKeys.RejectEmpty),
        ("   \n ", DataKeys.RejectEmpty),
        ("-- only a comment", DataKeys.RejectEmpty),
        ("INSERT INTO t VALUES (1)", DataKeys.RejectNotSelect),
        ("UPDATE t SET a = 1", DataKeys.RejectNotSelect),
        ("DELETE FROM t", DataKeys.RejectNotSelect),
        ("DROP TABLE t", DataKeys.RejectNotSelect),
        ("CREATE TABLE t (a int)", DataKeys.RejectNotSelect),
        ("ALTER TABLE t ADD b int", DataKeys.RejectNotSelect),
        ("TRUNCATE TABLE t", DataKeys.RejectNotSelect),
        ("EXEC sp_who", DataKeys.RejectNotSelect),
        ("EXECUTE sp_who", DataKeys.RejectNotSelect),
        ("CALL do_it()", DataKeys.RejectNotSelect),
        ("PRAGMA writable_schema = 1", DataKeys.RejectNotSelect),
        ("ATTACH DATABASE 'x.db' AS x", DataKeys.RejectNotSelect),
        ("VACUUM", DataKeys.RejectNotSelect),
        ("MERGE INTO t USING s ON 1=1 WHEN MATCHED THEN DELETE", DataKeys.RejectNotSelect),
        ("GRANT ALL ON t TO u", DataKeys.RejectNotSelect),
        ("SET ROLE admin", DataKeys.RejectNotSelect),
        ("BEGIN", DataKeys.RejectNotSelect),
        ("EXPLAIN SELECT 1", DataKeys.RejectNotSelect),
        ("SHOW TABLES", DataKeys.RejectNotSelect),
        ("VALUES (1)", DataKeys.RejectNotSelect),
        ("SELECT 1; SELECT 2", DataKeys.RejectMultipleStatements),
        ("SELECT 1; DROP TABLE t", DataKeys.RejectMultipleStatements),
        ("SELECT 1;; ", DataKeys.RejectMultipleStatements),
        ("SELECT 1; -- c\nSELECT 2", DataKeys.RejectMultipleStatements),
        ("SELECT 1 /* c */; DELETE FROM t", DataKeys.RejectMultipleStatements),
        ("SELECT 1 DROP TABLE t", DataKeys.RejectForbiddenKeyword),
        ("SELECT * INTO newtable FROM t", DataKeys.RejectSelectInto),
        ("SELECT * INTO OUTFILE '/tmp/x' FROM t", DataKeys.RejectSelectInto),
        ("SELECT * INTO DUMPFILE '/tmp/x' FROM t", DataKeys.RejectSelectInto),
        ("SELECT a INTO @v FROM t", DataKeys.RejectSelectInto),
        ("SELECT * FROM t FOR UPDATE", DataKeys.RejectLocking),
        ("SELECT * FROM t FOR SHARE", DataKeys.RejectLocking),
        ("SELECT * FROM t FOR NO KEY UPDATE", DataKeys.RejectLocking),
        ("SELECT * FROM t LOCK IN SHARE MODE", DataKeys.RejectLocking),
        ("WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x", DataKeys.RejectForbiddenKeyword),
        ("WITH x AS (INSERT INTO t VALUES (1) RETURNING *) SELECT * FROM x", DataKeys.RejectForbiddenKeyword),
        ("WITH x AS (SELECT 1) DELETE FROM t", DataKeys.RejectForbiddenKeyword),
        ("WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x", DataKeys.RejectForbiddenKeyword),
        ("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')", DataKeys.RejectForbiddenFunction),
        ("SELECT * FROM OPENQUERY(srv, 'SELECT 1')", DataKeys.RejectForbiddenFunction),
        ("SELECT LOAD_FILE('/etc/passwd')", DataKeys.RejectForbiddenFunction),
        ("SELECT pg_read_file('/etc/passwd')", DataKeys.RejectForbiddenFunction),
        ("SELECT lo_import('/etc/passwd')", DataKeys.RejectForbiddenFunction),
        ("SELECT load_extension('x')", DataKeys.RejectForbiddenFunction),
        ("SELECT xp_cmdshell('dir')", DataKeys.RejectForbiddenFunction),
        ("SELECT nextval('seq')", DataKeys.RejectForbiddenFunction),
        // Quoted function names are still calls (PostgreSQL and SQLite accept "name"(...)); the quotes must not hide them.
        ("SELECT \"pg_read_file\"('/etc/passwd')", DataKeys.RejectForbiddenFunction),
        ("SELECT pg_catalog.\"dblink\"('host=evil', 'select 1')", DataKeys.RejectForbiddenFunction),
        ("SELECT `load_file`('x')", DataKeys.RejectForbiddenFunction),
        ("SELECT [OPENROWSET]('x')", DataKeys.RejectForbiddenFunction),
        ("SELECT [xp_cmdshell]('dir')", DataKeys.RejectForbiddenFunction),
        ("SELECT \"LOAD_EXTENSION\" ('x')", DataKeys.RejectForbiddenFunction),
        ("SELECT 'unterminated", DataKeys.RejectUnterminated),
        ("SELECT 1 /* never closed", DataKeys.RejectUnterminated),
        ("SELECT \"never closed", DataKeys.RejectUnterminated),
        ("SELECT 1 /*! DROP TABLE t */", DataKeys.RejectExecutableComment),
        ("SELECT 1 /*!50000 DROP TABLE t */", DataKeys.RejectExecutableComment),
        ("SELECT 1 /*M! DROP TABLE t */", DataKeys.RejectExecutableComment),
        ("SELECT 1 REPLACE INTO t VALUES (1)", DataKeys.RejectForbiddenKeyword),
        ("SELECT 1 WAITFOR DELAY '00:01'", DataKeys.RejectForbiddenKeyword),
        ("SELECT 1 USE other", DataKeys.RejectForbiddenKeyword),
    }.Select(x => new object[] { x.Sql, x.Reason });

    [Theory]
    [MemberData(nameof(RejectedEverywhere))]
    public void RejectsWritesAndTricks_OnEveryProvider(string sql, string reason)
    {
        foreach (var p in All)
        {
            var r = SqlGuard.Check(sql, p);
            // The MySQL-only tokens (/*!, LOCK IN SHARE MODE) are caught on every provider because they are refused up front.
            Assert.False(r.IsAllowed, $"{p} should refuse: {sql}");
            Assert.Equal(reason, r.ReasonKey);
        }
    }

    [Fact]
    public void RejectsOverlongQuery()
    {
        var r = SqlGuard.Check("SELECT '" + new string('a', SqlGuard.MaxQueryLength) + "'", Ms);
        Assert.False(r.IsAllowed);
        Assert.Equal(DataKeys.RejectTooLong, r.ReasonKey);
    }

    [Fact]
    public void RejectionCarriesOffendingKeywordAsArgument()
    {
        var r = SqlGuard.Check("SELECT 1 DELETE FROM t", Ms);
        Assert.Equal("DELETE", r.Arg);
        var ex = r.ToException();
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal(DataKeys.RejectForbiddenKeyword, ex.ReasonKey);
        Assert.Equal(new[] { "DELETE" }, ex.Args);
    }

    // ---- dialect-specific lexing: where a string/comment ends decides what the guard sees -------------------

    [Fact]
    public void MySql_BackslashEscapedQuote_DoesNotEndTheString()
    {
        // MySQL: the string is  a\'; DROP TABLE x; --  (one literal). Other engines see a statement after the quote.
        const string sql = "SELECT 'a\\'; DROP TABLE x; --'";
        // This text used to be accepted for MySQL. A server running with NO_BACKSLASH_ESCAPES reads it the other way and would execute the
        // DROP, so the guard now lexes both readings and refuses the text as ambiguous (see DataLexAmbiguityTests).
        var my = SqlGuard.Check(sql, My);
        Assert.False(my.IsAllowed);
        Assert.Equal(DataKeys.RejectAmbiguous, my.ReasonKey);
        Assert.False(SqlGuard.Check(sql, Ms).IsAllowed);
        // ODBC cannot know the engine, so it must refuse anything that is dangerous under any reading.
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed);
    }

    [Fact]
    public void Standard_BackslashIsLiteral_AndDoesNotHideStatements()
    {
        // SQL Server/PostgreSQL/SQLite: the string ends at the second quote; a MySQL-style reading would hide the DELETE.
        const string sql = "SELECT 'a\\' ; DELETE FROM t; --'";
        Assert.False(SqlGuard.Check(sql, Ms).IsAllowed);
        Assert.False(SqlGuard.Check(sql, Pg).IsAllowed);
        Assert.False(SqlGuard.Check(sql, Lite).IsAllowed);
        Assert.False(SqlGuard.Check(sql, Odbc).IsAllowed);
    }

    [Fact]
    public void MySql_HashIsALineComment_ButNotElsewhere()
    {
        Assert.True(SqlGuard.Check("SELECT 1 # ; whatever\n", My).IsAllowed);
        // Elsewhere '#' is part of a name (#temp), so the DELETE after it is visible.
        Assert.False(SqlGuard.Check("SELECT 1 # ; whatever\n; DELETE FROM t", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT 1 #\nDELETE FROM t", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT 1 #\nDELETE FROM t", My).IsAllowed);
    }

    [Fact]
    public void MySql_DoubleDashWithoutSpace_IsNotAComment()
    {
        // In MySQL "--x" is two minus signs, so the text after it is live SQL.
        Assert.False(SqlGuard.Check("SELECT 1 --x\n; DELETE FROM t", My).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT 1 --x\nDELETE FROM t", My).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT 1 -- x\n", My).IsAllowed);
    }

    [Fact]
    public void Postgres_NestedCommentsAndDollarQuotes()
    {
        Assert.True(SqlGuard.Check("SELECT 1 /* a /* nested */ still comment */", Pg).IsAllowed);
        // Non-nesting engines end the comment at the first */ and see the DELETE.
        Assert.False(SqlGuard.Check("SELECT 1 /* a /* nested */ DELETE FROM t */", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT $$ ; DROP TABLE t; $$", Pg).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT $tag$ ; DROP TABLE t; $tag$ AS x", Pg).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT $tag$ never closed", Pg).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT E'it\\'s; fine'", Pg).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT $1", Pg).IsAllowed);
    }

    [Fact]
    public void SqlServerAndSqlite_BracketQuotedNames()
    {
        Assert.True(SqlGuard.Check("SELECT [a;b], [c]]d] FROM [t]", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT [a;b] FROM t", Lite).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT [Copy], [Use] FROM t", Ms).IsAllowed);
        // Quoting a reserved verb works only in the engine's own syntax; the strict ODBC reading refuses it.
        Assert.False(SqlGuard.Check("SELECT [Copy] FROM t", Pg).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT [never closed", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT arr[1] FROM t", Pg).IsAllowed);
    }

    [Fact]
    public void MySql_BacktickNames()
    {
        Assert.True(SqlGuard.Check("SELECT `a;b`, `drop` FROM `delete`", My).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT `never closed", My).IsAllowed);
    }

    [Fact]
    public void CommentsCannotHideKeywordsOrSplitThem()
    {
        Assert.False(SqlGuard.Check("SELECT 1 /* x */ DELETE /* y */ FROM t", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT 1\n-- x\nDROP TABLE t", Ms).IsAllowed);
        Assert.True(SqlGuard.Check("SELECT 1 /* DROP */ FROM t", Ms).IsAllowed);
        Assert.False(SqlGuard.Check("SELECT * FROM t WHERE a = 1 -- ok\n; DROP TABLE t", Ms).IsAllowed);
    }

    // ---- parameters ---------------------------------------------------------------------------------------

    [Fact]
    public void ParameterNames_AreExtractedOnce_InOrder_AndIgnoreStringsAndSystemVariables()
    {
        var r = SqlGuard.Check("SELECT @@ROWCOUNT, '@notparam', a FROM t WHERE x = @Name /* @comment */ AND y = @id OR z = @name", Ms);
        Assert.True(r.IsAllowed);
        Assert.Equal(new[] { "Name", "id" }, r.ParameterNames);
    }

    [Fact]
    public void Odbc_ParametersBecomePositional_WithDuplicates()
    {
        const string sql = "SELECT * FROM t WHERE a = @x OR b = @y OR c = @x -- @z";
        var r = SqlGuard.Check(sql, Odbc);
        var (rewritten, order) = OdbcParameters.Rewrite(sql, r.ParameterTokens);
        Assert.Equal("SELECT * FROM t WHERE a = ? OR b = ? OR c = ? -- @z", rewritten);
        Assert.Equal(new[] { "x", "y", "x" }, order);
    }
}
