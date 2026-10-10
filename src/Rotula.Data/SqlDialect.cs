using System.Text;

namespace Rotula.Data;

/// <summary>How a dialect quotes an identifier.</summary>
public enum IdentifierQuoteStyle
{
    /// <summary>ANSI <c>"name"</c> (PostgreSQL, SQLite, Oracle, Firebird, Db2, DuckDB, Snowflake, ClickHouse accepts it too).</summary>
    DoubleQuote,
    /// <summary>T-SQL <c>[name]</c> (SQL Server, Jet).</summary>
    Bracket,
    /// <summary>MySQL <c>`name`</c> (also BigQuery).</summary>
    Backtick,
    /// <summary>The engine behind the driver is unknown (ODBC): plain names stay unquoted, anything else uses ANSI double quotes.</summary>
    DoubleQuoteUnlessPlain,
}

/// <summary>Which marker the driver expects for a bound parameter. Users always write <c>@name</c>; Rotula rewrites it to this style.</summary>
public enum ParameterStyle
{
    /// <summary><c>@name</c> (SQL Server, PostgreSQL via Npgsql, MySQL, SQLite, Firebird, BigQuery): no rewrite.</summary>
    AtName,
    /// <summary><c>:name</c> (Oracle).</summary>
    ColonName,
    /// <summary>Positional <c>?</c> in order of appearance (ODBC, Db2, Informix).</summary>
    Positional,
    /// <summary><c>$name</c> (DuckDB).</summary>
    DollarName,
}

/// <summary>How a dialect limits the number of rows of a SELECT.</summary>
public enum RowLimitStyle
{
    /// <summary>No syntax is known; the row cap is applied by Rotula while reading.</summary>
    None,
    /// <summary><c>... LIMIT n</c> (PostgreSQL, MySQL, SQLite, DuckDB, ClickHouse, Snowflake, BigQuery).</summary>
    Limit,
    /// <summary><c>SELECT TOP n ...</c> (SQL Server, Jet).</summary>
    Top,
    /// <summary><c>... FETCH FIRST n ROWS ONLY</c> (Oracle 12c+, Db2).</summary>
    FetchFirst,
    /// <summary><c>SELECT FIRST n ...</c> (Firebird, Informix).</summary>
    First,
    /// <summary><c>WHERE ROWNUM &lt;= n</c> in a wrapping query (older Oracle).</summary>
    Rownum,
}

/// <summary>
/// Everything the read-only guard, the query planner and the catalogue browser need to know about a SQL dialect. It is data: a new
/// engine adds one instance (and tests), not new branches in the engine code. The base keyword and function lists of
/// <see cref="SqlGuard"/> apply to every dialect; the sets below only ADD to them.
/// </summary>
public sealed record SqlDialect
{
    /// <summary>Stable id (see <see cref="SqlDialects"/>).</summary>
    public required string Id { get; init; }

    /// <summary>English name for diagnostics; user-facing names come from the provider descriptor's message key.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Lexing rule sets. One entry for a known engine; several for an unknown engine behind ODBC, where the text must be clean
    /// under all of them.
    /// </summary>
    public required IReadOnlyList<SqlLexRules> LexRules { get; init; }

    /// <summary>Identifier quoting.</summary>
    public IdentifierQuoteStyle IdentifierQuote { get; init; } = IdentifierQuoteStyle.DoubleQuote;

    /// <summary>Native parameter marker the driver wants.</summary>
    public ParameterStyle ParameterStyle { get; init; } = ParameterStyle.AtName;

    /// <summary>True when the engine matches parameter names case-sensitively (SQLite), so <c>@a</c> and <c>@A</c> are two parameters.</summary>
    public bool ParameterNamesCaseSensitive { get; init; }

    /// <summary>Row-limit syntax (see <see cref="ApplyRowLimit"/>).</summary>
    public RowLimitStyle RowLimit { get; init; } = RowLimitStyle.None;

    /// <summary>
    /// SQL that lists tables and views as three columns: schema (may be NULL), name, type (contains "view" for views). Null means
    /// the driver's schema collections are used instead (ODBC) or the provider lists objects itself (APIs without SQL catalogue).
    /// </summary>
    public string? CatalogueSql { get; init; }

    /// <summary>A cheap statement run by the connection test to prove the target really is a database (SQLite opens lazily). Null for none.</summary>
    public string? ConnectivityProbeSql { get; init; }

    /// <summary>
    /// Lexical features whose behaviour depends on server settings (MySQL <c>NO_BACKSLASH_ESCAPES</c> / <c>ANSI_QUOTES</c>, PostgreSQL
    /// <c>standard_conforming_strings</c>) or is documented loosely. The guard lexes the text again with each of them flipped, and with
    /// all of them on and all off; if the token streams disagree (the engine could see a string, comment or statement boundary
    /// somewhere else than we do) the query is refused as ambiguous. A quoted identifier of a checked kind that contains a backslash is
    /// refused outright.
    /// </summary>
    public LexFeature AmbiguityChecks { get; init; }

    /// <summary>Extra words refused anywhere in the statement, on top of the base list.</summary>
    public IReadOnlySet<string> ForbiddenKeywords { get; init; } = EmptySet;

    /// <summary>Functions or rowsets refused wherever the word appears (exact name, case-insensitive).</summary>
    public IReadOnlySet<string> ForbiddenFunctions { get; init; } = EmptySet;

    /// <summary>Words starting with one of these are refused wherever they appear (Oracle <c>UTL_</c>, <c>DBMS_</c>).</summary>
    public IReadOnlySet<string> ForbiddenFunctionPrefixes { get; init; } = EmptySet;

    /// <summary>Names exempt from <see cref="ForbiddenFunctionPrefixes"/> and <see cref="ForbiddenCallPrefixes"/> (for example <c>DBMS_LOB</c>).</summary>
    public IReadOnlySet<string> AllowedFunctions { get; init; } = EmptySet;

    /// <summary>Names refused only when called, that is directly followed by <c>(</c>. Used for names that are also common column names (ClickHouse <c>file</c>, <c>url</c>).</summary>
    public IReadOnlySet<string> ForbiddenCalls { get; init; } = EmptySet;

    /// <summary>Call-only prefixes (DuckDB <c>read_</c>).</summary>
    public IReadOnlySet<string> ForbiddenCallPrefixes { get; init; } = EmptySet;

    /// <summary>Schemas whose routines may not be called: <c>QUALIFIER.name(</c> is refused (Oracle <c>SYS</c>, Db2 <c>SYSPROC</c>). Reading their tables stays allowed.</summary>
    public IReadOnlySet<string> ForbiddenCallQualifiers { get; init; } = EmptySet;

    /// <summary>Words that may not directly follow <c>WITH</c> (Oracle <c>WITH FUNCTION</c>/<c>PROCEDURE</c> embeds PL/SQL in the statement).</summary>
    public IReadOnlySet<string> ForbiddenAfterWith { get; init; } = EmptySet;

    /// <summary>Word sequences refused (a statement that changes state although it starts with SELECT, such as <c>NEXT VALUE FOR</c>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> ForbiddenPhrases { get; init; } = Array.Empty<IReadOnlyList<string>>();

    /// <summary>DuckDB: refuse a file name where a table is expected (<c>FROM 'x.csv'</c>, <c>FROM "x.parquet"</c>), which reads a file from inside a SELECT.</summary>
    public bool RejectFileTableReferences { get; init; }

    /// <summary>Oracle: refuse <c>table@dblink</c>, which reaches another database.</summary>
    public bool RejectDatabaseLinks { get; init; }

    /// <summary>Jet/ACE: refuse <c>IN 'other.mdb'</c>, which reads another file.</summary>
    public bool RejectInFileClause { get; init; }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    /// <summary>Quotes one identifier part, doubling the closing quote so the name cannot break out.</summary>
    /// <exception cref="DatabaseException"><see cref="DataKeys.InvalidSettings"/> for an empty name or one with control characters.</exception>
    public string QuoteIdentifier(string part)
    {
        if (string.IsNullOrEmpty(part) || part.Any(char.IsControl)) throw new DatabaseException(DataKeys.InvalidSettings, null, new[] { "table" });
        switch (IdentifierQuote)
        {
            case IdentifierQuoteStyle.Bracket: return "[" + part.Replace("]", "]]", StringComparison.Ordinal) + "]";
            case IdentifierQuoteStyle.Backtick: return "`" + part.Replace("`", "``", StringComparison.Ordinal) + "`";
            case IdentifierQuoteStyle.DoubleQuoteUnlessPlain:
                // The backing engine's quote character is unknown; plain names need none, anything else uses ANSI double quotes.
                return part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(part[0])
                    ? part : "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            default: return "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }

    /// <summary>
    /// Adds a row limit to a <c>SELECT ...</c> statement built by Rotula (never to user text). Returns the statement unchanged
    /// when the dialect has no known syntax.
    /// </summary>
    public string ApplyRowLimit(string selectSql, int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectSql);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        string n = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string select = "SELECT";
        switch (RowLimit)
        {
            case RowLimitStyle.Limit: return selectSql + " LIMIT " + n;
            case RowLimitStyle.FetchFirst: return selectSql + " FETCH FIRST " + n + " ROWS ONLY";
            case RowLimitStyle.Top when selectSql.StartsWith(select, StringComparison.OrdinalIgnoreCase):
                return select + " TOP " + n + selectSql[select.Length..];
            case RowLimitStyle.First when selectSql.StartsWith(select, StringComparison.OrdinalIgnoreCase):
                return select + " FIRST " + n + selectSql[select.Length..];
            case RowLimitStyle.Rownum: return new StringBuilder("SELECT * FROM (").Append(selectSql).Append(") WHERE ROWNUM <= ").Append(n).ToString();
            default: return selectSql;
        }
    }
}

/// <summary>
/// The dialect catalogue: the four classic dialects the engine always shipped with, plus the dialects that provider packs and
/// ODBC "underlying engine" hints use. Each entry's lexical rules were checked against the vendor's language reference; see
/// docs/data-providers.md for the sources and what is still unverified.
/// </summary>
public static class SqlDialects
{
    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<IReadOnlyList<string>> Phrases(params string[][] items) => items;

    /// <summary>Microsoft SQL Server.</summary>
    public static readonly SqlDialect SqlServer = new()
    {
        Id = "sqlserver", DisplayName = "SQL Server (T-SQL)", LexRules = new[] { SqlLexRules.SqlServer },
        IdentifierQuote = IdentifierQuoteStyle.Bracket, RowLimit = RowLimitStyle.Top,
        CatalogueSql =
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') AND TABLE_SCHEMA NOT IN ('sys','INFORMATION_SCHEMA') ORDER BY 1, 2",
    };

    // Sources (checked 2026-10): PostgreSQL manual "Lexical Structure" - string constants, escape string constants (E'...'), dollar-quoted
    // strings, nested /* */ comments. With standard_conforming_strings=off a plain '...' honours backslashes, hence the differential check.
    /// <summary>PostgreSQL (also used for Redshift, CockroachDB, YugabyteDB, Supabase, Neon, Aurora).</summary>
    public static readonly SqlDialect PostgreSql = new()
    {
        AmbiguityChecks = LexFeature.SingleBackslash, Id = "postgresql", DisplayName = "PostgreSQL", LexRules = new[] { SqlLexRules.PostgreSql }, RowLimit = RowLimitStyle.Limit,
        CatalogueSql =
            "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
            "WHERE table_type IN ('BASE TABLE','VIEW') AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2",
    };

    // Sources: MySQL reference "Literal Values / String Literals" (backslash escapes in '...' and "..."; doubled quote; NO_BACKSLASH_ESCAPES and
    // ANSI_QUOTES sql_modes change this per server, hence the differential check) and "Comments" (# and -- needing a space after).
    /// <summary>MySQL.</summary>
    public static readonly SqlDialect MySql = new()
    {
        AmbiguityChecks = LexFeature.AnyBackslash, Id = "mysql", DisplayName = "MySQL", LexRules = new[] { SqlLexRules.MySql }, IdentifierQuote = IdentifierQuoteStyle.Backtick,
        RowLimit = RowLimitStyle.Limit,
        CatalogueSql =
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES " +
            "WHERE TABLE_TYPE IN ('BASE TABLE','VIEW') AND TABLE_SCHEMA NOT IN ('mysql','information_schema','performance_schema','sys') " +
            "AND (DATABASE() IS NULL OR TABLE_SCHEMA = DATABASE()) ORDER BY 1, 2",
    };

    /// <summary>MariaDB: MySQL lexing; sequences (<c>NEXT VALUE FOR</c>, <c>NEXTVAL</c>) are refused like everywhere else.</summary>
    public static readonly SqlDialect MariaDb = MySql with { Id = "mariadb", DisplayName = "MariaDB" };

    /// <summary>SQLite.</summary>
    public static readonly SqlDialect Sqlite = new()
    {
        Id = "sqlite", DisplayName = "SQLite", LexRules = new[] { SqlLexRules.Sqlite }, RowLimit = RowLimitStyle.Limit,
        ParameterNamesCaseSensitive = true,
        ConnectivityProbeSql = "SELECT COUNT(*) FROM sqlite_master",
        CatalogueSql = "SELECT NULL, name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name",
    };

    /// <summary>
    /// An unknown engine behind ODBC: the text must be clean under all four classic lexings. Parameters become positional <c>?</c>,
    /// objects come from the driver's schema collections.
    /// </summary>
    public static readonly SqlDialect OdbcUnknown = new()
    {
        AmbiguityChecks = LexFeature.AnyBackslash, Id = "odbc", DisplayName = "ODBC (unknown engine)",
        LexRules = new[] { SqlLexRules.SqlServer, SqlLexRules.PostgreSql, SqlLexRules.MySql, SqlLexRules.Sqlite },
        IdentifierQuote = IdentifierQuoteStyle.DoubleQuoteUnlessPlain, ParameterStyle = ParameterStyle.Positional,
    };

    // Sources: Oracle SQL Language Reference, "Text Literals" (alternative quoting: q'<d>...<d>' and nq'..', any delimiter except space, tab and
    // return; [ { < ( close with their mirror; doubled single quote otherwise) and "Comments" (-- and /* */, hints are comments).
    /// <summary>
    /// Oracle. Strings: <c>'..'</c> and q-quotes; identifiers <c>"..."</c>; comments <c>--</c> and <c>/* */</c> (hints are comments). Inside a
    /// SELECT, packages such as <c>UTL_HTTP</c> and <c>DBMS_*</c> can reach the network or write (autonomous transactions are not stopped by
    /// READ ONLY), <c>WITH FUNCTION</c> embeds PL/SQL, and <c>@dblink</c> reaches other databases.
    /// </summary>
    public static readonly SqlDialect Oracle = new()
    {
        Id = "oracle", DisplayName = "Oracle", LexRules = new[] { new SqlLexRules { QQuotes = true, ColonParameters = true } },
        ParameterStyle = ParameterStyle.ColonName, RowLimit = RowLimitStyle.FetchFirst,
        ForbiddenFunctionPrefixes = Set("UTL_", "DBMS_", "OWA_"),
        AllowedFunctions = Set("DBMS_LOB", "DBMS_RANDOM"),
        ForbiddenCalls = Set("HTTPURITYPE", "DBURITYPE", "XDBURITYPE"),
        ForbiddenCallQualifiers = Set("SYS", "HTP"),
        ForbiddenAfterWith = Set("FUNCTION", "PROCEDURE"),
        RejectDatabaseLinks = true,
        CatalogueSql =
            "SELECT owner, table_name, 'TABLE' FROM all_tables WHERE owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','CTXSYS','ORDSYS','OUTLN','WMSYS','LBACSYS','DVSYS','AUDSYS','GSMADMIN_INTERNAL','APPQOSSYS','DBSNMP','OJVMSYS','ORDDATA','ORDPLUGINS','SI_INFORMTN_SCHEMA') " +
            "UNION ALL SELECT owner, view_name, 'VIEW' FROM all_views WHERE owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','CTXSYS','ORDSYS','OUTLN','WMSYS','LBACSYS','DVSYS','AUDSYS','GSMADMIN_INTERNAL','APPQOSSYS','DBSNMP','OJVMSYS','ORDDATA','ORDPLUGINS','SI_INFORMTN_SCHEMA') ORDER BY 1, 2",
    };

    // Sources: DuckDB docs "SQL Introduction / Literal Types" ($$ strings, E'...') and "Securing DuckDB" (enable_external_access, lock_configuration).
    /// <summary>
    /// DuckDB (PostgreSQL-derived parser). Comments are NOT treated as nested on purpose (see <see cref="SqlLexRules"/> remarks).
    /// File reading from inside a SELECT (<c>read_csv(...)</c>, <c>FROM 'x.csv'</c>) cannot be fully blocked lexically; the real
    /// control is <c>SET enable_external_access = false</c> (a read-only step of the provider). These rules are defence in depth.
    /// </summary>
    public static readonly SqlDialect DuckDb = new()
    {
        AmbiguityChecks = LexFeature.SingleBackslash, Id = "duckdb", DisplayName = "DuckDB",
        LexRules = new[] { new SqlLexRules { PostgresStrings = true, DollarParameters = true } },
        ParameterStyle = ParameterStyle.DollarName, RowLimit = RowLimitStyle.Limit,
        ForbiddenCallPrefixes = Set("read_", "parquet_", "iceberg_", "delta_", "json_scan", "csv_scan"),
        ForbiddenCalls = Set("glob", "query", "query_table", "sniff_csv", "st_read", "st_drivers", "read_blob", "duckdb_secrets", "which_secret",
            "pragma_database_list", "getenv", "current_setting"),
        RejectFileTableReferences = true,
        CatalogueSql =
            "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
            "WHERE table_schema NOT IN ('information_schema','pg_catalog') ORDER BY 1, 2",
    };

    // Source: GoogleSQL "Lexical structure and syntax" (read 2026-10): quoted, triple-quoted (three single or three double quotes) and raw (r/R) string literals;
    // backslash escape sequences also in quoted identifiers; # -- and /* */ comments; "end the string with three unescaped quotes in a row".
    // The docs do not define a doubled-quote escape, so both readings are lexed (AnyDoubling).
    /// <summary>
    /// BigQuery GoogleSQL: <c>#</c>, <c>--</c> and <c>/* */</c> comments; strings in single or double quotes with backslash escapes, triple
    /// quotes and raw (<c>r</c>) prefixes; identifiers in backticks. Native parameters are <c>@name</c>.
    /// </summary>
    public static readonly SqlDialect BigQuery = new()
    {
        AmbiguityChecks = LexFeature.AnyBackslash | LexFeature.AnyDoubling, Id = "bigquery", DisplayName = "BigQuery",
        LexRules = new[] { new SqlLexRules { SingleQuoteBackslash = true, DoubleQuoteBackslash = true, BacktickBackslash = true, SingleQuoteDoubling = false, DoubleQuoteDoubling = false, BacktickDoubling = false, HashComment = true, Backticks = true, TripleQuotes = true } },
        IdentifierQuote = IdentifierQuoteStyle.Backtick, RowLimit = RowLimitStyle.Limit,
        ForbiddenCalls = Set("EXTERNAL_QUERY", "EXTERNAL_OBJECT_TRANSFORM"),
    };

    // Sources: Snowflake docs "String & binary data types" (single-quoted constants use backslash escape sequences; dollar-quoted $$ constants
    // are literal) and "Comments" (--, // and /* */). Snowflake does NOT use backslash escapes inside double-quoted identifiers, so the
    // guard lexes both readings of every backslash.
    /// <summary>Snowflake: <c>--</c>, <c>//</c> and <c>/* */</c> comments, backslash escapes, <c>$$</c> strings. Server-side procedures (<c>SYSTEM$...</c>) are refused.</summary>
    public static readonly SqlDialect Snowflake = new()
    {
        AmbiguityChecks = LexFeature.AnyBackslash, Id = "snowflake", DisplayName = "Snowflake",
        LexRules = new[] { new SqlLexRules { SingleQuoteBackslash = true, SlashSlashComment = true, DollarQuotes = true } },
        RowLimit = RowLimitStyle.Limit,
        ForbiddenFunctionPrefixes = Set("SYSTEM$"), AllowedFunctions = Set("SYSTEM$TYPEOF"),
        ForbiddenCalls = Set("GET_PRESIGNED_URL", "GET_STAGE_LOCATION"),
        CatalogueSql =
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA <> 'INFORMATION_SCHEMA' ORDER BY 1, 2",
    };

    // Source: ClickHouse "Syntax" (read 2026-10): comments --, #, #! and // (and more than two slashes), nestable C-style /* */; string
    // literals in single quotes only, '' or backslash escapes (backslash is literal before characters that are not listed escapes);
    // double quotes and backticks quote identifiers; $heredoc$ strings. Identifier escape rules are not spelled out, so the guard lexes both readings.
    /// <summary>
    /// ClickHouse: comments <c>--</c>, <c>#</c>, <c>#!</c>, <c>//</c> and NESTING <c>/* */</c>; strings in single quotes with backslash escapes;
    /// <c>$heredoc$</c> strings. Table functions that reach files, the network or other servers are refused, and so is <c>SETTINGS</c>
    /// (a query could otherwise try to change session limits). The server-side control is the <c>readonly=1</c> setting.
    /// </summary>
    public static readonly SqlDialect ClickHouse = new()
    {
        AmbiguityChecks = LexFeature.AnyBackslash, Id = "clickhouse", DisplayName = "ClickHouse",
        LexRules = new[] { new SqlLexRules { SingleQuoteBackslash = true, HashComment = true, SlashSlashComment = true, NestedComments = true, Backticks = true, DollarQuotes = true } },
        IdentifierQuote = IdentifierQuoteStyle.Backtick, RowLimit = RowLimitStyle.Limit,
        ForbiddenKeywords = Set("SETTINGS"),
        ForbiddenCalls = Set("url", "urlCluster", "file", "fileCluster", "s3", "s3Cluster", "gcs", "oss", "cosn", "hdfs", "hdfsCluster", "azureBlobStorage",
            "azureBlobStorageCluster", "remote", "remoteSecure", "cluster", "clusterAllReplicas", "mysql", "postgresql", "mongodb", "redis", "sqlite", "jdbc", "odbc",
            "executable", "input", "deltaLake", "iceberg", "hudi", "dictionary", "mergeTreeIndex"),
        CatalogueSql =
            "SELECT database, name, engine FROM system.tables WHERE database NOT IN ('system','INFORMATION_SCHEMA','information_schema') ORDER BY 1, 2",
    };

    /// <summary>Informix: <c>{ }</c> and <c>--</c> comments, <c>SELECT FIRST n</c>. Administration is reachable through <c>TASK()</c> / <c>ADMIN()</c>, which are refused.</summary>
    public static readonly SqlDialect Informix = new()
    {
        AmbiguityChecks = LexFeature.SingleBackslash, Id = "informix", DisplayName = "Informix",
        LexRules = new[] { new SqlLexRules { BraceComments = true } },
        ParameterStyle = ParameterStyle.Positional, RowLimit = RowLimitStyle.First,
        ForbiddenCalls = Set("TASK", "ADMIN"),
        CatalogueSql = "SELECT owner, tabname, CASE tabtype WHEN 'V' THEN 'VIEW' ELSE 'TABLE' END FROM systables WHERE tabid > 99 AND tabtype IN ('T','V') ORDER BY 1, 2",
    };

    /// <summary>Db2 (LUW): sequences (<c>NEXT VALUE FOR</c>, <c>NEXTVAL FOR</c>) and <c>SYSPROC</c> routines are refused.</summary>
    public static readonly SqlDialect Db2 = new()
    {
        Id = "db2", DisplayName = "IBM Db2",
        LexRules = new[] { new SqlLexRules() },
        ParameterStyle = ParameterStyle.Positional, RowLimit = RowLimitStyle.FetchFirst,
        ForbiddenCallQualifiers = Set("SYSPROC", "SYSFUN"),
        ForbiddenPhrases = Phrases(new[] { "NEXTVAL", "FOR" }),
        CatalogueSql =
            "SELECT TABSCHEMA, TABNAME, TYPE FROM SYSCAT.TABLES WHERE TYPE IN ('T','V') AND TABSCHEMA NOT LIKE 'SYS%' ORDER BY 1, 2",
    };

    /// <summary>
    /// Firebird: q-quotes (3.0+), <c>--</c> and <c>/* */</c> comments, <c>SELECT FIRST n</c>. Generators change state when read with
    /// <c>GEN_ID</c> / <c>NEXT VALUE FOR</c>. Selectable stored procedures cannot be detected lexically: use a SELECT-only role.
    /// </summary>
    public static readonly SqlDialect Firebird = new()
    {
        Id = "firebird", DisplayName = "Firebird",
        LexRules = new[] { new SqlLexRules { QQuotes = true } },
        RowLimit = RowLimitStyle.First,
        ForbiddenFunctions = Set("GEN_ID"),
        CatalogueSql =
            "SELECT NULL, TRIM(RDB$RELATION_NAME), CASE WHEN RDB$VIEW_SOURCE IS NULL THEN 'TABLE' ELSE 'VIEW' END FROM RDB$RELATIONS WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 ORDER BY 2",
    };

    /// <summary>Microsoft Access / Excel / text through Jet or ACE: bracket names, <c>#date#</c> literals, <c>TOP n</c>, <c>IN 'file'</c> refused.</summary>
    public static readonly SqlDialect Jet = new()
    {
        Id = "jet", DisplayName = "Microsoft Jet / ACE",
        LexRules = new[] { SqlLexRules.SqlServer },
        IdentifierQuote = IdentifierQuoteStyle.Bracket, ParameterStyle = ParameterStyle.Positional, RowLimit = RowLimitStyle.Top,
        ForbiddenCalls = Set("Shell", "CreateObject", "GetObject", "Environ", "CurDir", "Dir", "Kill", "FileLen", "FileDateTime", "Eval"),
        RejectInFileClause = true,
    };

    /// <summary>Every dialect in this catalogue.</summary>
    public static IReadOnlyList<SqlDialect> All { get; } = new[]
    {
        SqlServer, PostgreSql, MySql, MariaDb, Sqlite, OdbcUnknown, Oracle, DuckDb, BigQuery, Snowflake, ClickHouse, Informix, Db2, Firebird, Jet,
    };

    /// <summary>The dialect of a built-in provider.</summary>
    public static SqlDialect For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => SqlServer,
        DatabaseProvider.PostgreSql => PostgreSql,
        DatabaseProvider.MySql => MySql,
        DatabaseProvider.Sqlite => Sqlite,
        _ => OdbcUnknown,
    };

    /// <summary>Finds a dialect by id (case-insensitive); null when unknown.</summary>
    public static SqlDialect? ById(string? id)
        => id is null ? null : All.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
