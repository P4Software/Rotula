namespace Rotula.Data;

/// <summary>Outcome of <see cref="SqlGuard.Check(string?, SqlDialect)"/>.</summary>
public sealed class SqlGuardResult
{
    internal SqlGuardResult(bool allowed, string? reasonKey, string? arg, IReadOnlyList<string> parameters, IReadOnlyList<SqlToken>? parameterTokens = null)
    {
        ParameterTokens = parameterTokens ?? Array.Empty<SqlToken>();
        IsAllowed = allowed;
        ReasonKey = reasonKey;
        Arg = arg;
        ParameterNames = parameters;
    }

    /// <summary>True when the text is a single read-only SELECT / WITH ... SELECT.</summary>
    public bool IsAllowed { get; }

    /// <summary>When refused, one of the <c>Data.Query.Rejected.*</c> keys of <see cref="DataKeys"/>.</summary>
    public string? ReasonKey { get; }

    /// <summary>Optional argument for the reason (the offending keyword or function name).</summary>
    public string? Arg { get; }

    /// <summary>Distinct <c>@name</c> parameters the query uses (without the @), in order of first use. Empty when refused.</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    /// <summary>Every parameter occurrence with its position (used to rewrite <c>@name</c> into the driver's marker, for example positional <c>?</c> for ODBC).</summary>
    internal IReadOnlyList<SqlToken> ParameterTokens { get; }

    /// <summary>Converts a refusal into the exception the query methods throw.</summary>
    public DatabaseException ToException() =>
        new(DataKeys.QueryRejected, ReasonKey, Arg is null ? null : new[] { Arg });
}

/// <summary>
/// The read-only gate in front of every user query. It accepts exactly one <c>SELECT</c> or <c>WITH ... SELECT</c> statement
/// (one optional trailing semicolon) and refuses everything that could change data, run code, read files or lock rows.
/// It works on tokens produced by a lexer that understands strings, comments, quoted names and each dialect's quirks, and on the
/// rules of a <see cref="SqlDialect"/>: the base lists below apply to every dialect, the dialect adds its own.
/// </summary>
/// <remarks>
/// The guard is a defence in depth, not the only wall: connections are additionally opened read-only where the engine
/// supports it and every query runs inside a transaction that is rolled back. For SQL Server the database account itself
/// should be read-only (ApplicationIntent=ReadOnly is a routing hint, not a write guard). The guard errs on the side of
/// refusing: a column that is literally named like a reserved verb (Copy, Call, Lock, Use...) must be quoted ("Copy" or [Copy]).
/// </remarks>
public static class SqlGuard
{
    /// <summary>Longest accepted query text, in characters.</summary>
    public const int MaxQueryLength = DatabaseLimits.MaxQueryLength;

    // Verbs that change data/schema/state or run code. Checked at any depth because PostgreSQL allows data-modifying CTEs
    // and T-SQL allows statements without a separator ("SELECT 1 DROP TABLE x").
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "CREATE", "ALTER", "TRUNCATE", "RENAME",
        "GRANT", "REVOKE", "DENY", "EXEC", "EXECUTE", "CALL", "PRAGMA", "ATTACH", "DETACH", "VACUUM", "REINDEX",
        "COPY", "LOAD", "DO", "HANDLER", "SHUTDOWN", "BACKUP", "RESTORE", "KILL", "USE", "SET", "DECLARE", "BULK",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "UNLOCK", "WAITFOR", "DBCC", "CHECKPOINT", "PREPARE",
        "DEALLOCATE", "LISTEN", "NOTIFY",
    };

    // Functions/rowsets that read files, reach other servers or execute code, even from inside a SELECT.
    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML", "LOAD_FILE", "PG_READ_FILE", "PG_READ_BINARY_FILE",
        "PG_LS_DIR", "PG_STAT_FILE", "LO_IMPORT", "LO_EXPORT", "DBLINK", "DBLINK_EXEC", "DBLINK_CONNECT", "LOAD_EXTENSION",
        "READFILE", "WRITEFILE", "PG_TERMINATE_BACKEND", "PG_CANCEL_BACKEND", "SET_CONFIG", "NEXTVAL", "SETVAL",
        "SYS_EXEC", "SYS_EVAL",
    };

    // Phrases that look like a plain SELECT but advance server state. NEXT and VALUE are common column names, so only the
    // three-word sequence is refused (a lone NEXTVAL is already in the function list). Applies to every dialect.
    private static readonly string[][] BasePhrases = { new[] { "NEXT", "VALUE", "FOR" } };

    // Words that end the FROM list of a SELECT (used only to know whether a comma still separates table references).
    private static readonly HashSet<string> FromListEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "QUALIFY", "WINDOW", "UNION", "INTERSECT", "EXCEPT", "SELECT", "OFFSET", "FETCH",
    };

    private static readonly string[] FileExtensions =
    {
        ".csv", ".tsv", ".txt", ".parquet", ".json", ".jsonl", ".ndjson", ".xlsx", ".xls", ".gz", ".zst", ".db", ".duckdb", ".sqlite", ".arrow", ".orc", ".avro",
    };

    /// <summary>Validates <paramref name="sql"/> for a built-in <paramref name="provider"/> and extracts its parameter names.</summary>
    public static SqlGuardResult Check(string? sql, DatabaseProvider provider) => Check(sql, SqlDialects.For(provider));

    /// <summary>
    /// Validates <paramref name="sql"/> for the dialect the connection really talks (provider, preset flavour, ODBC engine hint,
    /// or a pack provider). Prefer this overload whenever settings are at hand: the enum overload cannot know about packs.
    /// </summary>
    public static SqlGuardResult Check(string? sql, DatabaseConnectionSettings settings, ProviderRegistry? registry = null)
        => Check(sql, (registry ?? ProviderRegistry.Default).ResolveDialect(settings));

    /// <summary>Validates <paramref name="sql"/> for <paramref name="dialect"/> and extracts its parameter names.</summary>
    public static SqlGuardResult Check(string? sql, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        if (string.IsNullOrWhiteSpace(sql)) return Reject(DataKeys.RejectEmpty);
        if (sql.Length > MaxQueryLength) return Reject(DataKeys.RejectTooLong);

        List<string>? parameters = null;
        List<SqlToken>? paramTokens = null;
        // One pass per lexing rule set; ODBC runs all four so the verdict holds whatever engine the driver talks to.
        foreach (var rules in dialect.LexRules)
        {
            var lexed = SqlTokenizer.Tokenize(sql, rules);
            if (lexed.ErrorKey is not null) return Reject(lexed.ErrorKey);
            var verdict = CheckTokens(sql, lexed.Tokens, dialect, out var found);
            if (verdict is not null) return verdict;
            if (IsAmbiguous(sql, rules, lexed.Tokens, dialect.AmbiguityChecks)) return Reject(DataKeys.RejectAmbiguous);
            var occurrences = lexed.Tokens.Where(t => t.Kind == SqlTokenKind.Parameter).ToList();
            if (paramTokens is not null && !paramTokens.SequenceEqual(occurrences))
                // The dialects disagree about what is a parameter, i.e. about where a string or comment ends: refuse.
                return Reject(DataKeys.RejectNotSelect);
            parameters ??= found;
            paramTokens ??= occurrences;
        }
        return new SqlGuardResult(true, null, null, parameters ?? new List<string>(), paramTokens);
    }

    /// <summary>Throws the <see cref="DatabaseException"/> for a refused query; returns the parameter names otherwise.</summary>
    public static IReadOnlyList<string> EnsureAllowed(string? sql, DatabaseProvider provider) => EnsureAllowed(sql, SqlDialects.For(provider));

    /// <summary>Throws the <see cref="DatabaseException"/> for a refused query; returns the parameter names otherwise.</summary>
    public static IReadOnlyList<string> EnsureAllowed(string? sql, SqlDialect dialect)
    {
        var r = Check(sql, dialect);
        if (!r.IsAllowed) throw r.ToException();
        return r.ParameterNames;
    }

    /// <summary>
    /// Differential lexing. Lexes <paramref name="sql"/> again with every checked feature flipped (one at a time), all on and all off.
    /// If any reading fails to tokenize or puts a token boundary somewhere else, the engine might see a different statement than the
    /// guard did, so the text is refused instead of guessed. Only readings that can matter for this text are tried.
    /// </summary>
    private static bool IsAmbiguous(string sql, SqlLexRules rules, IReadOnlyList<SqlToken> main, LexFeature checks)
    {
        if (checks == LexFeature.None) return false;

        // A quoted identifier of a checked kind may not contain a backslash at all: engines disagree on whether it escapes the closing quote.
        foreach (var t in main)
        {
            if (t.Kind != SqlTokenKind.Quoted || t.IsString) continue;
            char q = sql[t.Start];
            // Only where the main reading says "no escapes here" (so the alternate reading is the dangerous one). Where the dialect already
            // lexes backslash escapes in that kind of quote (MySQL strings, BigQuery), differential lexing alone decides.
            bool checkedKind = q == '"' ? checks.HasFlag(LexFeature.DoubleBackslash) && !rules.DoubleQuoteBackslash
                : q == '`' && checks.HasFlag(LexFeature.BacktickBackslash) && !rules.BacktickBackslash;
            if (checkedKind && sql.AsSpan(t.Start, t.Length).Contains('\\')) return true;
        }

        bool hasBackslash = sql.Contains('\\');
        bool hasNest = sql.IndexOf("/*", StringComparison.Ordinal) != sql.LastIndexOf("/*", StringComparison.Ordinal);
        var relevant = new List<LexFeature>();
        foreach (LexFeature f in new[] { LexFeature.SingleBackslash, LexFeature.DoubleBackslash, LexFeature.BacktickBackslash,
                     LexFeature.SingleDoubling, LexFeature.DoubleDoubling, LexFeature.BacktickDoubling, LexFeature.NestedComments })
        {
            if (!checks.HasFlag(f)) continue;
            bool matters = f switch
            {
                LexFeature.SingleBackslash or LexFeature.DoubleBackslash or LexFeature.BacktickBackslash => hasBackslash,
                LexFeature.SingleDoubling => sql.Contains("''", StringComparison.Ordinal),
                LexFeature.DoubleDoubling => sql.Contains("\"\"", StringComparison.Ordinal),
                LexFeature.BacktickDoubling => sql.Contains("``", StringComparison.Ordinal),
                _ => hasNest,
            };
            if (matters) relevant.Add(f);
        }
        if (relevant.Count == 0) return false;

        var readings = new List<SqlLexRules>();
        foreach (var f in relevant) readings.Add(rules.With(f, !rules.Get(f)));
        var allOn = rules; var allOff = rules;
        foreach (var f in relevant) { allOn = allOn.With(f, true); allOff = allOff.With(f, false); }
        readings.Add(allOn);
        readings.Add(allOff);

        foreach (var alt in readings.Distinct())
        {
            if (alt == rules) continue;
            var lexed = SqlTokenizer.Tokenize(sql, alt);
            if (lexed.ErrorKey is not null || !SameShape(main, lexed.Tokens)) return true;
        }
        return false;
    }

    private static bool SameShape(IReadOnlyList<SqlToken> a, IReadOnlyList<SqlToken> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Kind != b[i].Kind || a[i].Start != b[i].Start || a[i].Length != b[i].Length || a[i].IsString != b[i].IsString) return false;
        return true;
    }

    private static bool IsSym(IReadOnlyList<SqlToken> t, int i, string s) => i >= 0 && i < t.Count && t[i].Kind == SqlTokenKind.Symbol && t[i].Text == s;

    private static bool IsWord(IReadOnlyList<SqlToken> t, int i, string s)
        => i >= 0 && i < t.Count && t[i].Kind == SqlTokenKind.Word && t[i].Text.Equals(s, StringComparison.OrdinalIgnoreCase);

    private static SqlGuardResult? CheckTokens(string sql, IReadOnlyList<SqlToken> tokens, SqlDialect dialect, out List<string> parameters)
    {
        parameters = new List<string>();
        int count = tokens.Count;

        // A single trailing ';' is tolerated (people paste it); anything after a ';' is a second statement.
        for (int i = 0; i < count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.Symbol && tokens[i].Text == ";")
            {
                if (i != count - 1) return Reject(DataKeys.RejectMultipleStatements);
                count = i;
                break;
            }
        }
        if (count == 0) return Reject(DataKeys.RejectEmpty);

        int first = 0;
        while (first < count && tokens[first].Kind == SqlTokenKind.Symbol && tokens[first].Text == "(") first++;
        if (first >= count || tokens[first].Kind != SqlTokenKind.Word) return Reject(DataKeys.RejectNotSelect);
        string head = tokens[first].Text;
        bool isWith = head.Equals("WITH", StringComparison.OrdinalIgnoreCase);
        if (!isWith && !head.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) return Reject(DataKeys.RejectNotSelect);

        bool sawSelect = !isWith;
        int depth = 0;
        int fromDepth = -1;         // paren depth of the FROM list we are inside, or -1
        bool tablePosition = false; // the previous token was FROM, JOIN, or a comma of the FROM list: this one names a table
        for (int i = first; i < count; i++)
        {
            var t = tokens[i];
            bool wasTablePosition = tablePosition;
            tablePosition = false;

            if (t.Kind == SqlTokenKind.Symbol)
            {
                if (t.Text == "(") depth++;
                else if (t.Text == ")") { depth--; if (fromDepth > depth) fromDepth = -1; }
                else if (t.Text == "," && fromDepth >= 0 && depth == fromDepth) tablePosition = true;
                continue;
            }
            if (t.Kind == SqlTokenKind.Parameter)
            {
                // Oracle: table@link reaches another database. Rotula parameters are @name, so an '@' glued to the previous word is a link.
                if (dialect.RejectDatabaseLinks && sql[t.Start] == '@' && i > 0
                    && tokens[i - 1].Kind is SqlTokenKind.Word or SqlTokenKind.Quoted && tokens[i - 1].Start + tokens[i - 1].Length == t.Start)
                    return Reject(DataKeys.RejectForbiddenFunction, "@" + t.Text);
                if (!parameters.Contains(t.Text, StringComparer.OrdinalIgnoreCase)) parameters.Add(t.Text);
                continue;
            }
            if (t.Kind == SqlTokenKind.Quoted)
            {
                if (wasTablePosition && dialect.RejectFileTableReferences && (t.IsString || LooksLikePath(t.Text)))
                    return Reject(DataKeys.RejectForbiddenFunction, "FROM 'file'");
                // PostgreSQL and SQLite accept "pg_read_file"(...) as a call, so a quoted name followed by '(' is judged like a word.
                if (t.Text.Length > 0 && IsSym(tokens, i + 1, "(") && (IsForbiddenFunction(t.Text, dialect) || IsForbiddenCall(t.Text, dialect)))
                    return Reject(DataKeys.RejectForbiddenFunction, t.Text);
                continue;
            }

            string w = t.Text;
            if (w.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) { sawSelect = true; fromDepth = -1; continue; }
            if (w.Equals("FROM", StringComparison.OrdinalIgnoreCase) || w.Equals("JOIN", StringComparison.OrdinalIgnoreCase))
            {
                tablePosition = true;
                if (w.Equals("FROM", StringComparison.OrdinalIgnoreCase)) fromDepth = depth;
                continue;
            }
            if (fromDepth == depth && FromListEnders.Contains(w)) fromDepth = -1;

            if (w.Equals("INTO", StringComparison.OrdinalIgnoreCase)) return Reject(DataKeys.RejectSelectInto);
            if (w.Equals("LOCK", StringComparison.OrdinalIgnoreCase)) return Reject(DataKeys.RejectLocking);
            if (w.Equals("FOR", StringComparison.OrdinalIgnoreCase) && i + 1 < count && tokens[i + 1].Kind == SqlTokenKind.Word)
            {
                string next = tokens[i + 1].Text;
                if (next.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || next.Equals("SHARE", StringComparison.OrdinalIgnoreCase)
                    || next.Equals("NO", StringComparison.OrdinalIgnoreCase) || next.Equals("KEY", StringComparison.OrdinalIgnoreCase))
                    return Reject(DataKeys.RejectLocking);
            }
            // REPLACE() is a harmless string function everywhere; REPLACE INTO is MySQL/SQLite's write verb.
            if (w.Equals("REPLACE", StringComparison.OrdinalIgnoreCase) && i + 1 < count
                && tokens[i + 1].Kind == SqlTokenKind.Word && tokens[i + 1].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase))
                return Reject(DataKeys.RejectForbiddenKeyword, "REPLACE");
            if (ForbiddenKeywords.Contains(w) || dialect.ForbiddenKeywords.Contains(w)) return Reject(DataKeys.RejectForbiddenKeyword, w.ToUpperInvariant());
            if (IsForbiddenFunction(w, dialect)) return Reject(DataKeys.RejectForbiddenFunction, w);
            if (IsSym(tokens, i + 1, "(") && IsForbiddenCall(w, dialect)) return Reject(DataKeys.RejectForbiddenFunction, w);

            // SYSPROC.name( / SYS.pkg.fn(: a routine of a schema that is off limits. Reading its tables (sys.objects) stays allowed.
            if (dialect.ForbiddenCallQualifiers.Contains(w) && IsSym(tokens, i + 1, ".") && CallFollowsQualifier(tokens, i + 2, count))
                return Reject(DataKeys.RejectForbiddenFunction, w + ".");

            if (w.Equals("WITH", StringComparison.OrdinalIgnoreCase) && i + 1 < count && tokens[i + 1].Kind == SqlTokenKind.Word
                && dialect.ForbiddenAfterWith.Contains(tokens[i + 1].Text))
                return Reject(DataKeys.RejectForbiddenKeyword, "WITH " + tokens[i + 1].Text.ToUpperInvariant());

            // Jet: FROM table IN 'other.mdb' reads another file.
            if (dialect.RejectInFileClause && w.Equals("IN", StringComparison.OrdinalIgnoreCase) && i + 1 < count
                && tokens[i + 1].Kind == SqlTokenKind.Quoted && tokens[i + 1].IsString)
                return Reject(DataKeys.RejectForbiddenFunction, "IN 'file'");

            var phrase = MatchPhrase(tokens, i, count, dialect);
            if (phrase is not null) return Reject(DataKeys.RejectForbiddenFunction, phrase);
        }
        if (!sawSelect) return Reject(DataKeys.RejectNotSelect);
        return null;
    }

    /// <summary>After <c>QUAL .</c>: either <c>name (</c> or <c>pkg . name (</c>.</summary>
    private static bool CallFollowsQualifier(IReadOnlyList<SqlToken> t, int i, int count)
    {
        static bool Name(SqlToken x) => x.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
        if (i >= count || !Name(t[i])) return false;
        if (IsSym(t, i + 1, "(")) return true;
        return IsSym(t, i + 1, ".") && i + 2 < count && Name(t[i + 2]) && IsSym(t, i + 3, "(");
    }

    private static string? MatchPhrase(IReadOnlyList<SqlToken> t, int i, int count, SqlDialect dialect)
    {
        foreach (var phrase in BasePhrases.Concat(dialect.ForbiddenPhrases.Select(p => p.ToArray())))
        {
            if (i + phrase.Length > count) continue;
            bool all = true;
            for (int k = 0; k < phrase.Length && all; k++) all = IsWord(t, i + k, phrase[k]);
            if (all) return string.Join(' ', phrase);
        }
        return null;
    }

    /// <summary>A quoted table name that is really a file (DuckDB replacement scans accept <c>"data.csv"</c> and <c>"s3://b/x"</c>).</summary>
    private static bool LooksLikePath(string name)
    {
        if (name.Length == 0) return false;
        if (name.IndexOfAny(new[] { '/', '\\', '*' }) >= 0 || name.Contains("://", StringComparison.Ordinal)) return true;
        return FileExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsForbiddenFunction(string name, SqlDialect d)
    {
        if (ForbiddenFunctions.Contains(name) || d.ForbiddenFunctions.Contains(name)
            || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)) return true;
        if (d.AllowedFunctions.Contains(name)) return false;
        return d.ForbiddenFunctionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsForbiddenCall(string name, SqlDialect d)
    {
        if (d.ForbiddenCalls.Contains(name)) return true;
        if (d.AllowedFunctions.Contains(name)) return false;
        return d.ForbiddenCallPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static SqlGuardResult Reject(string reason, string? arg = null) => new(false, reason, arg, Array.Empty<string>());
}
