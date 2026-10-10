using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// Differential lexing: where an engine and the guard could disagree about the end of a string or quoted name (server settings such as
/// NO_BACKSLASH_ESCAPES, or loosely documented rules), the text is lexed under every conservative reading and refused when the readings
/// disagree. Each crafted attempt must be refused (Ambiguous or another reason), never accepted; the legitimate queries still pass.
/// </summary>
public class DataLexAmbiguityTests
{
    private static SqlGuardResult Check(SqlDialect d, string sql) => SqlGuard.Check(sql, d);

    private static void Ambiguous(SqlDialect d, string sql)
    {
        var r = Check(d, sql);
        Assert.False(r.IsAllowed, $"{d.Id}: must be refused: {sql}");
        Assert.Equal(DataKeys.RejectAmbiguous, r.ReasonKey);
    }

    private static void Refused(SqlDialect d, string sql)
        => Assert.False(Check(d, sql).IsAllowed, $"{d.Id}: must be refused: {sql}");

    private static void Fine(SqlDialect d, string sql)
    {
        var r = Check(d, sql);
        Assert.True(r.IsAllowed, $"{d.Id}: expected accepted but got {r.ReasonKey}: {sql}");
    }

    // ---------------------------------------------------------------- MySQL / MariaDB (NO_BACKSLASH_ESCAPES, ANSI_QUOTES)

    [Theory]
    [InlineData("SELECT 'a\\' ; DROP TABLE x -- '")]            // backslash before the closing single quote
    [InlineData("SELECT \"a\\\" ; DROP TABLE x -- \"")]         // ... double quote (a string, or an identifier under ANSI_QUOTES)
    [InlineData("SELECT `a\\` FROM t")]                         // ... backtick
    public void MySql_BackslashBeforeAClosingQuote_IsAmbiguous(string sql)
    {
        Ambiguous(SqlDialects.MySql, sql);
        Ambiguous(SqlDialects.MariaDb, sql);
    }

    [Fact]
    public void MySql_AStatementHiddenAfterABackslashQuote_IsRefused()
    {
        // Here the escaping reading already shows a second statement, so the plain reason is given.
        Refused(SqlDialects.MySql, "SELECT 'x' FROM t WHERE a = 'b\\' OR c = ' ; DROP TABLE y -- '");
    }

    [Theory]
    [InlineData("SELECT 'C:\\temp\\file' FROM t")]               // backslashes that do not touch a quote
    [InlineData("SELECT 'a\\\\' FROM t")]                        // an escaped backslash at the end ends the string in both readings
    [InlineData("SELECT 'it''s' FROM t")]
    [InlineData("SELECT \"C:\\temp\" AS p FROM t")]               // a MySQL double-quoted string with a harmless backslash
    [InlineData("SELECT `a b`, `c` FROM `t`")]
    [InlineData("SELECT * FROM t WHERE a = 'x' # trailing comment")]
    public void MySql_LegitimateQueries_StillPass(string sql)
    {
        Fine(SqlDialects.MySql, sql);
        Fine(SqlDialects.MariaDb, sql);
    }

    // ---------------------------------------------------------------- PostgreSQL (standard_conforming_strings)

    [Fact]
    public void PostgreSql_PlainStringEndingInABackslash_IsAmbiguous_ButEscapeStringsAreNot()
    {
        var pg = SqlDialects.PostgreSql;
        Ambiguous(pg, "SELECT 'a\\' FROM t WHERE x = '1'");
        Fine(pg, "SELECT 'C:\\temp' FROM t");
        Fine(pg, "SELECT 'it''s' FROM t");
        Fine(pg, "SELECT E'a\\'b' FROM t");                     // E'...' always honours backslashes: one reading
        Fine(pg, "SELECT \"a\\b\" FROM t");                       // double-quoted names never escape in PostgreSQL
        Fine(pg, "SELECT $$a\\$$ FROM t");
        Fine(SqlDialects.SqlServer, "SELECT 'a\\' FROM t WHERE x = '1'");   // T-SQL has no backslash escapes at all
        Fine(SqlDialects.Sqlite, "SELECT 'a\\' FROM t WHERE x = '1'");
    }

    // ---------------------------------------------------------------- Snowflake, BigQuery, ClickHouse

    [Theory]
    [InlineData("SELECT 'a\\' ; DROP TABLE x -- '")]
    [InlineData("SELECT \"a\\\" FROM t")]                        // Snowflake: no backslash escapes in double-quoted names
    public void Snowflake_Backslashes_AreAmbiguous(string sql) => Ambiguous(SqlDialects.Snowflake, sql);

    [Fact]
    public void Snowflake_AHiddenStatementAfterABackslashInDoubleQuotes_IsRefused()
        => Refused(SqlDialects.Snowflake, "SELECT \"a\\\" ; DROP TABLE x -- \" FROM t");

    [Theory]
    [InlineData("SELECT 'a\\' ; DROP TABLE x -- '")]
    [InlineData("SELECT \"a\\\" ; DROP TABLE x -- \"")]
    [InlineData("SELECT 'a''b' FROM t")]                         // a doubled quote: an escape, or two adjacent literals
    [InlineData("SELECT \"a\"\"b\" FROM t")]
    [InlineData("SELECT `a``b` FROM t")]
    public void BigQuery_BackslashesAndDoubledQuotes_AreAmbiguous(string sql) => Ambiguous(SqlDialects.BigQuery, sql);

    [Fact]
    public void BigQuery_ABacktickNameEndingInABackslash_IsRefused()
        => Refused(SqlDialects.BigQuery, "SELECT `a\\` FROM t");

    [Theory]
    [InlineData("SELECT `a\\` FROM t")]
    [InlineData("SELECT \"a\\\" FROM t")]
    [InlineData("SELECT 'a\\' ; DROP TABLE x -- '")]
    public void ClickHouse_Backslashes_AreAmbiguous(string sql) => Ambiguous(SqlDialects.ClickHouse, sql);

    [Fact]
    public void Snowflake_BigQuery_ClickHouse_LegitimateQueries_StillPass()
    {
        Fine(SqlDialects.Snowflake, "SELECT 'C:\\temp' AS p, \"Col\" FROM t");
        Fine(SqlDialects.Snowflake, "SELECT 'a\\\\' FROM t");
        Fine(SqlDialects.Snowflake, "SELECT 'it''s' FROM t");
        Fine(SqlDialects.BigQuery, "SELECT 'C:\\temp' AS p FROM `p.d.t`");
        Fine(SqlDialects.BigQuery, "SELECT r'\\d+' FROM `p.d.t`");
        Fine(SqlDialects.BigQuery, "SELECT '''a''' , \"\"\"b\"\"\" FROM `p.d.t`");
        Fine(SqlDialects.BigQuery, "SELECT '' , \"\" FROM `p.d.t`");
        Fine(SqlDialects.ClickHouse, "SELECT 'C:\\temp', `a` FROM t");
        Fine(SqlDialects.ClickHouse, "SELECT 'it''s' FROM t");
    }

    // ---------------------------------------------------------------- the ODBC union

    [Theory]
    [InlineData("SELECT 'a\\' ; DROP TABLE x -- '")]
    [InlineData("SELECT \"a\\\" ; DROP TABLE x -- \"")]
    [InlineData("SELECT `a\\` ; DROP TABLE x -- `")]
    public void OdbcWithoutAHint_RefusesEveryBackslashReading(string sql)
        => Refused(SqlDialects.OdbcUnknown, sql);

    // ---------------------------------------------------------------- other engines: no ambiguity to resolve

    [Fact]
    public void EnginesWithOneUnambiguousReading_AreNotSlowedOrRefused()
    {
        foreach (var d in new[] { SqlDialects.SqlServer, SqlDialects.Sqlite, SqlDialects.Oracle, SqlDialects.Db2, SqlDialects.Firebird, SqlDialects.Jet })
        {
            Fine(d, "SELECT 'a\\' FROM t WHERE b = 'c'");
            Fine(d, "SELECT 'it''s' FROM t");
        }
        Refused(SqlDialects.Oracle, "SELECT 'a' ; DROP TABLE x");
    }

    // ---------------------------------------------------------------- structure of the checks

    [Fact]
    public void EveryDialectWithEscapeRules_DeclaresWhichFeaturesAreChecked()
    {
        foreach (var d in SqlDialects.All)
        {
            foreach (var rules in d.LexRules)
            {
                bool anyBackslash = rules.SingleQuoteBackslash || rules.DoubleQuoteBackslash || rules.BacktickBackslash;
                if (anyBackslash) Assert.True((d.AmbiguityChecks & LexFeature.AnyBackslash) != 0, d.Id + " lexes backslash escapes but does not check the reading");
            }
        }
        // Engines whose strings never use backslashes stay on a single reading.
        Assert.Equal(LexFeature.None, SqlDialects.SqlServer.AmbiguityChecks);
        Assert.Equal(LexFeature.None, SqlDialects.Sqlite.AmbiguityChecks);
        Assert.Equal(LexFeature.None, SqlDialects.Oracle.AmbiguityChecks);
    }

    [Fact]
    public void ARefusalByDifferentialLexing_IsAPlainReason_WithoutArguments()
    {
        var r = Check(SqlDialects.MySql, "SELECT 'a\\' ; DROP TABLE x -- '");
        Assert.Equal(DataKeys.RejectAmbiguous, r.ReasonKey);
        var ex = r.ToException();
        Assert.Equal(DataKeys.QueryRejected, ex.Key);
        Assert.Equal(DataKeys.RejectAmbiguous, ex.ReasonKey);
        Assert.Empty(ex.Args);
    }

    [Fact]
    public void BigTexts_AreStillChecked_QuicklyUnderEveryReading()
    {
        string big = "SELECT " + string.Concat(Enumerable.Repeat("'a\\\\b', ", 8000)) + "1";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(big.Length < SqlGuard.MaxQueryLength);
        Assert.True(Check(SqlDialects.BigQuery, big).IsAllowed);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, "differential lexing took " + sw.ElapsedMilliseconds + " ms");
    }
}
