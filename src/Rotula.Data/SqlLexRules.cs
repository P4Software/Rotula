namespace Rotula.Data;

/// <summary>
/// The lexical quirks of one SQL dialect that decide where a string, comment or quoted name ends. Getting these wrong is
/// how a guard is bypassed (a statement hidden in what the guard thinks is a comment, or the reverse), so they are explicit.
/// </summary>
/// <remarks>
/// Rule of thumb when a dialect is uncertain: never enable a feature the engine might not have if enabling it makes the guard
/// SKIP more text (nested comments, longer strings). Skipping text the engine will execute is the bypass; failing to skip text
/// the engine ignores merely rejects a harmless query.
/// </remarks>
public sealed record SqlLexRules
{
    /// <summary>A backslash escapes the next character inside <c>'...'</c> (MySQL, BigQuery, ClickHouse, Snowflake; PostgreSQL only with <c>E'...'</c>).</summary>
    public bool SingleQuoteBackslash { get; init; }
    /// <summary>A backslash escapes the next character inside <c>"..."</c> (MySQL strings, BigQuery strings, ClickHouse identifiers).</summary>
    public bool DoubleQuoteBackslash { get; init; }
    /// <summary>A backslash escapes the next character inside <c>`...`</c> (BigQuery and ClickHouse identifiers; never in MySQL).</summary>
    public bool BacktickBackslash { get; init; }
    /// <summary>A doubled quote inside <c>'...'</c> is a literal quote (standard SQL; not in BigQuery).</summary>
    public bool SingleQuoteDoubling { get; init; } = true;
    /// <summary>A doubled quote inside <c>"..."</c> is a literal quote.</summary>
    public bool DoubleQuoteDoubling { get; init; } = true;
    /// <summary>A doubled backtick inside <c>`...`</c> is a literal backtick.</summary>
    public bool BacktickDoubling { get; init; } = true;
    /// <summary><c>#</c> starts a line comment (MySQL, BigQuery, ClickHouse). Otherwise it is a word character (SQL Server temp tables, Jet dates).</summary>
    public bool HashComment { get; init; }
    /// <summary><c>/* */</c> comments nest (PostgreSQL, ClickHouse).</summary>
    public bool NestedComments { get; init; }
    /// <summary><c>$tag$ ... $tag$</c> dollar quoting and <c>E'...'</c> escape strings (PostgreSQL).</summary>
    public bool PostgresStrings { get; init; }
    /// <summary><c>[name]</c> is a quoted identifier (SQL Server, SQLite, Jet).</summary>
    public bool Brackets { get; init; }
    /// <summary><c>--</c> is a comment only when followed by whitespace (MySQL).</summary>
    public bool DashNeedsSpace { get; init; }
    /// <summary>Backtick quoted identifiers (MySQL, SQLite, BigQuery, ClickHouse).</summary>
    public bool Backticks { get; init; }

    /// <summary><c>$tag$ ... $tag$</c> dollar-quoted strings WITHOUT PostgreSQL's <c>E'...'</c> strings (Snowflake <c>$$</c>, ClickHouse heredocs, DuckDB).</summary>
    public bool DollarQuotes { get; init; }
    /// <summary>Oracle and Firebird alternative quoting: <c>q'[text]'</c> and <c>nq'!text!'</c>. The delimiter is any character except white space; brackets close with their mirror.</summary>
    public bool QQuotes { get; init; }
    /// <summary>BigQuery triple-quoted strings <c>'''text'''</c> and <c>"""text"""</c>.</summary>
    public bool TripleQuotes { get; init; }
    /// <summary><c>//</c> starts a line comment (Snowflake, ClickHouse).</summary>
    public bool SlashSlashComment { get; init; }
    /// <summary><c>{ ... }</c> is a (non-nesting) block comment (Informix).</summary>
    public bool BraceComments { get; init; }
    /// <summary><c>:name</c> is a bind variable (Oracle).</summary>
    public bool ColonParameters { get; init; }
    /// <summary><c>$name</c> is a bind variable (DuckDB).</summary>
    public bool DollarParameters { get; init; }

    /// <summary>Microsoft SQL Server (T-SQL).</summary>
    public static readonly SqlLexRules SqlServer = new() { Brackets = true };
    /// <summary>PostgreSQL and its wire-compatible relatives.</summary>
    public static readonly SqlLexRules PostgreSql = new() { NestedComments = true, PostgresStrings = true };
    /// <summary>MySQL and MariaDB.</summary>
    public static readonly SqlLexRules MySql = new() { SingleQuoteBackslash = true, DoubleQuoteBackslash = true, HashComment = true, DashNeedsSpace = true, Backticks = true };
    /// <summary>SQLite.</summary>
    public static readonly SqlLexRules Sqlite = new() { Brackets = true, Backticks = true };

    /// <summary>
    /// The rule sets to apply for a built-in provider. ODBC hides an unknown engine, so it must satisfy ALL four classic dialects:
    /// a text that is a clean single SELECT under every lexing is a clean single SELECT whatever the backend.
    /// </summary>
    public static IReadOnlyList<SqlLexRules> For(DatabaseProvider provider) => SqlDialects.For(provider).LexRules;

    /// <summary>Reads one uncertain feature (see <see cref="LexFeature"/>).</summary>
    public bool Get(LexFeature f) => f switch
    {
        LexFeature.SingleBackslash => SingleQuoteBackslash,
        LexFeature.DoubleBackslash => DoubleQuoteBackslash,
        LexFeature.BacktickBackslash => BacktickBackslash,
        LexFeature.SingleDoubling => SingleQuoteDoubling,
        LexFeature.DoubleDoubling => DoubleQuoteDoubling,
        LexFeature.BacktickDoubling => BacktickDoubling,
        LexFeature.NestedComments => NestedComments,
        _ => false,
    };

    /// <summary>A copy with one uncertain feature set to <paramref name="value"/>.</summary>
    public SqlLexRules With(LexFeature f, bool value) => f switch
    {
        LexFeature.SingleBackslash => this with { SingleQuoteBackslash = value },
        LexFeature.DoubleBackslash => this with { DoubleQuoteBackslash = value },
        LexFeature.BacktickBackslash => this with { BacktickBackslash = value },
        LexFeature.SingleDoubling => this with { SingleQuoteDoubling = value },
        LexFeature.DoubleDoubling => this with { DoubleQuoteDoubling = value },
        LexFeature.BacktickDoubling => this with { BacktickDoubling = value },
        LexFeature.NestedComments => this with { NestedComments = value },
        _ => this,
    };
}

/// <summary>
/// The lexical details whose exact behaviour depends on server settings or is documented loosely, so the guard also lexes the text
/// with each of them flipped and refuses the query when the readings disagree (see <see cref="SqlDialect.AmbiguityChecks"/>).
/// </summary>
[Flags]
public enum LexFeature
{
    /// <summary>No feature is checked.</summary>
    None = 0,
    /// <summary>Backslash escapes inside single quotes.</summary>
    SingleBackslash = 1,
    /// <summary>Backslash escapes inside double quotes.</summary>
    DoubleBackslash = 2,
    /// <summary>Backslash escapes inside backticks.</summary>
    BacktickBackslash = 4,
    /// <summary>Doubled single quote is an escape.</summary>
    SingleDoubling = 8,
    /// <summary>Doubled double quote is an escape.</summary>
    DoubleDoubling = 16,
    /// <summary>Doubled backtick is an escape.</summary>
    BacktickDoubling = 32,
    /// <summary>Block comments nest.</summary>
    NestedComments = 64,
    /// <summary>The three backslash features.</summary>
    AnyBackslash = SingleBackslash | DoubleBackslash | BacktickBackslash,
    /// <summary>The three doubled-quote features.</summary>
    AnyDoubling = SingleDoubling | DoubleDoubling | BacktickDoubling,
}
