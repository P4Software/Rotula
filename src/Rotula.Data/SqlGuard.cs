namespace Rotula.Data;

/// <summary>Outcome of <see cref="SqlGuard.Check"/>.</summary>
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

    /// <summary>Every parameter occurrence with its position (used to rewrite <c>@name</c> into positional <c>?</c> for ODBC).</summary>
    internal IReadOnlyList<SqlToken> ParameterTokens { get; }

    /// <summary>Converts a refusal into the exception the query methods throw.</summary>
    public DatabaseException ToException() =>
        new(DataKeys.QueryRejected, ReasonKey, Arg is null ? null : new[] { Arg });
}

/// <summary>
/// The read-only gate in front of every user query. It accepts exactly one <c>SELECT</c> or <c>WITH ... SELECT</c> statement
/// (one optional trailing semicolon) and refuses everything that could change data, run code, read files or lock rows.
/// It works on tokens produced by a lexer that understands strings, comments, quoted names and each dialect's quirks.
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
    public const int MaxQueryLength = 100_000;

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

    /// <summary>Validates <paramref name="sql"/> for <paramref name="provider"/> and extracts its parameter names.</summary>
    public static SqlGuardResult Check(string? sql, DatabaseProvider provider)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Reject(DataKeys.RejectEmpty);
        if (sql.Length > MaxQueryLength) return Reject(DataKeys.RejectTooLong);

        List<string>? parameters = null;
        List<SqlToken>? paramTokens = null;
        // One pass per lexing rule set; ODBC runs all four so the verdict holds whatever engine the driver talks to.
        foreach (var rules in SqlLexRules.For(provider))
        {
            var lexed = SqlTokenizer.Tokenize(sql, rules);
            if (lexed.ErrorKey is not null) return Reject(lexed.ErrorKey);
            var verdict = CheckTokens(lexed.Tokens, out var found);
            if (verdict is not null) return verdict;
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
    public static IReadOnlyList<string> EnsureAllowed(string? sql, DatabaseProvider provider)
    {
        var r = Check(sql, provider);
        if (!r.IsAllowed) throw r.ToException();
        return r.ParameterNames;
    }

    private static SqlGuardResult? CheckTokens(IReadOnlyList<SqlToken> tokens, out List<string> parameters)
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
        for (int i = first; i < count; i++)
        {
            var t = tokens[i];
            if (t.Kind == SqlTokenKind.Parameter)
            {
                if (!parameters.Contains(t.Text, StringComparer.OrdinalIgnoreCase)) parameters.Add(t.Text);
                continue;
            }
            if (t.Kind == SqlTokenKind.Quoted && t.Text.Length > 0 && i + 1 < count
                && tokens[i + 1].Kind == SqlTokenKind.Symbol && tokens[i + 1].Text == "(" && IsForbiddenFunction(t.Text))
                return Reject(DataKeys.RejectForbiddenFunction, t.Text);
            if (t.Kind != SqlTokenKind.Word) continue;

            string w = t.Text;
            if (w.Equals("SELECT", StringComparison.OrdinalIgnoreCase)) { sawSelect = true; continue; }
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
            if (ForbiddenKeywords.Contains(w)) return Reject(DataKeys.RejectForbiddenKeyword, w.ToUpperInvariant());
            if (IsForbiddenFunction(w))
                return Reject(DataKeys.RejectForbiddenFunction, w);
        }
        if (!sawSelect) return Reject(DataKeys.RejectNotSelect);
        return null;
    }

    private static bool IsForbiddenFunction(string name)
        => ForbiddenFunctions.Contains(name) || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase);

    private static SqlGuardResult Reject(string reason, string? arg = null) => new(false, reason, arg, Array.Empty<string>());
}
