using System.Text;

namespace Rotula.Data;

/// <summary>Kinds of token the guard cares about. Literals and comments are consumed and never become words.</summary>
internal enum SqlTokenKind
{
    /// <summary>An unquoted identifier, keyword or number.</summary>
    Word,
    /// <summary>A quoted identifier or a string literal (content irrelevant to the guard).</summary>
    Quoted,
    /// <summary>A <c>@name</c> parameter placeholder (not <c>@@system_variable</c>).</summary>
    Parameter,
    /// <summary>Any other single character: punctuation and operators.</summary>
    Symbol,
}

/// <summary>One lexical token with its position in the source text.</summary>
internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, string Text);

/// <summary>
/// The lexical quirks of one SQL dialect that decide where a string, comment or quoted name ends. Getting these wrong is
/// how a guard is bypassed (a statement hidden in what the guard thinks is a comment, or the reverse), so they are explicit.
/// </summary>
internal sealed record SqlLexRules
{
    /// <summary>Backslash escapes inside single and double quoted text (MySQL).</summary>
    public bool BackslashEscapes { get; init; }
    /// <summary><c>#</c> starts a line comment (MySQL). Otherwise it is a word character (SQL Server temp tables).</summary>
    public bool HashComment { get; init; }
    /// <summary><c>/* */</c> comments nest (PostgreSQL).</summary>
    public bool NestedComments { get; init; }
    /// <summary><c>$tag$ ... $tag$</c> dollar quoting and <c>E'...'</c> escape strings (PostgreSQL).</summary>
    public bool PostgresStrings { get; init; }
    /// <summary><c>[name]</c> is a quoted identifier (SQL Server, SQLite).</summary>
    public bool Brackets { get; init; }
    /// <summary><c>--</c> is a comment only when followed by whitespace (MySQL).</summary>
    public bool DashNeedsSpace { get; init; }
    /// <summary>Backtick quoted identifiers (MySQL, SQLite).</summary>
    public bool Backticks { get; init; }

    public static readonly SqlLexRules SqlServer = new() { Brackets = true };
    public static readonly SqlLexRules PostgreSql = new() { NestedComments = true, PostgresStrings = true };
    public static readonly SqlLexRules MySql = new() { BackslashEscapes = true, HashComment = true, DashNeedsSpace = true, Backticks = true };
    public static readonly SqlLexRules Sqlite = new() { Brackets = true, Backticks = true };

    /// <summary>
    /// The rule sets to apply for a provider. ODBC hides an unknown engine, so it must satisfy ALL dialects: a text that
    /// is a clean single SELECT under every lexing is a clean single SELECT whatever the backend.
    /// </summary>
    public static IReadOnlyList<SqlLexRules> For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => new[] { SqlServer },
        DatabaseProvider.PostgreSql => new[] { PostgreSql },
        DatabaseProvider.MySql => new[] { MySql },
        DatabaseProvider.Sqlite => new[] { Sqlite },
        _ => new[] { SqlServer, PostgreSql, MySql, Sqlite },
    };
}

/// <summary>Result of tokenizing: the tokens, or the reason key if the text cannot be tokenized safely.</summary>
internal sealed record SqlTokenizeResult(IReadOnlyList<SqlToken> Tokens, string? ErrorKey);

/// <summary>A real tokenizer (strings, comments, quoted names, dollar quotes), not a regex.</summary>
internal static class SqlTokenizer
{
    public static SqlTokenizeResult Tokenize(string sql, SqlLexRules rules)
    {
        var tokens = new List<SqlToken>();
        int i = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c) || c == '\0') { i++; continue; }

            // Line comments.
            if (c == '-' && i + 1 < n && sql[i + 1] == '-' &&
                (!rules.DashNeedsSpace || i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2])))
            {
                i = SkipLine(sql, i);
                continue;
            }
            if (c == '#' && rules.HashComment) { i = SkipLine(sql, i); continue; }

            // Block comments. /*! and /*M! are MySQL/MariaDB "executable" comments: their content runs, so never allow them.
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                if (i + 2 < n && sql[i + 2] == '!') return new(tokens, DataKeys.RejectExecutableComment);
                if (i + 3 < n && sql[i + 2] == 'M' && sql[i + 3] == '!') return new(tokens, DataKeys.RejectExecutableComment);
                int end = SkipBlockComment(sql, i, rules.NestedComments);
                if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                i = end;
                continue;
            }

            if (c == '\'' || c == '"' || (c == '`' && rules.Backticks))
            {
                int end = SkipQuoted(sql, i, c, rules.BackslashEscapes && c != '`');
                if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                // Keep the name of a quoted identifier ("fn", `fn`): PostgreSQL and SQLite accept "pg_read_file"(...) as a call, so the
                // guard must see through the quotes. String literals ('...') carry no text; they are data, never names.
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, c == '\'' ? string.Empty : Unquote(sql, i, end, c)));
                i = end;
                continue;
            }
            if (c == '[' && rules.Brackets)
            {
                int end = SkipBracketName(sql, i);
                if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, Unquote(sql, i, end, ']')));
                i = end;
                continue;
            }
            if (c == '$' && rules.PostgresStrings && TryDollarTag(sql, i, out string? tag))
            {
                int close = sql.IndexOf(tag!, i + tag!.Length, StringComparison.Ordinal);
                if (close < 0) return new(tokens, DataKeys.RejectUnterminated);
                int end = close + tag.Length;
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty));
                i = end;
                continue;
            }

            if (c == '@')
            {
                if (i + 1 < n && sql[i + 1] == '@')
                {
                    int e = i + 2;
                    while (e < n && IsWordChar(sql[e], rules)) e++;
                    tokens.Add(new(SqlTokenKind.Word, i, e - i, sql[i..e]));
                    i = e;
                    continue;
                }
                if (i + 1 < n && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    int e = i + 1;
                    while (e < n && (char.IsLetterOrDigit(sql[e]) || sql[e] == '_')) e++;
                    tokens.Add(new(SqlTokenKind.Parameter, i, e - i, sql[(i + 1)..e]));
                    i = e;
                    continue;
                }
            }

            if (IsWordStart(c, rules))
            {
                int e = i + 1;
                while (e < n && IsWordChar(sql[e], rules)) e++;
                // PostgreSQL E'...' escape string: the prefix is not a word, the string honours backslashes.
                if (rules.PostgresStrings && e == i + 1 && (c == 'E' || c == 'e') && e < n && sql[e] == '\'')
                {
                    int end = SkipQuoted(sql, e, '\'', backslash: true);
                    if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                    tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty));
                    i = end;
                    continue;
                }
                tokens.Add(new(SqlTokenKind.Word, i, e - i, sql[i..e]));
                i = e;
                continue;
            }

            tokens.Add(new(SqlTokenKind.Symbol, i, 1, c.ToString()));
            i++;
        }
        return new(tokens, null);
    }

    /// <summary>Content of a quoted identifier with doubled closing quotes collapsed (backslash escapes are not interpreted).</summary>
    private static string Unquote(string s, int start, int end, char closer)
    {
        string inner = s[(start + 1)..(end - 1)];
        return inner.Replace(new string(closer, 2), closer.ToString(), StringComparison.Ordinal);
    }

    private static bool IsWordStart(char c, SqlLexRules r) => char.IsLetterOrDigit(c) || c == '_' || (c == '#' && !r.HashComment);

    private static bool IsWordChar(char c, SqlLexRules r) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || (c == '#' && !r.HashComment);

    private static int SkipLine(string s, int i)
    {
        while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
        return i;
    }

    /// <summary>Returns the index after the closing <c>*/</c>, or -1 if unterminated.</summary>
    private static int SkipBlockComment(string s, int i, bool nested)
    {
        int depth = 1;
        i += 2;
        while (i < s.Length)
        {
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0) return i;
                continue;
            }
            if (nested && s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { depth++; i += 2; continue; }
            i++;
        }
        return -1;
    }

    /// <summary>Returns the index after the closing quote (doubled quote = literal quote), or -1.</summary>
    private static int SkipQuoted(string s, int i, char quote, bool backslash)
    {
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == quote)
            {
                if (i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return -1;
    }

    private static int SkipBracketName(string s, int i)
    {
        i++;
        while (i < s.Length)
        {
            if (s[i] == ']')
            {
                if (i + 1 < s.Length && s[i + 1] == ']') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return -1;
    }

    /// <summary>Detects <c>$$</c> or <c>$tag$</c> that is not part of a word or a positional parameter like <c>$1</c>.</summary>
    private static bool TryDollarTag(string s, int i, out string? tag)
    {
        tag = null;
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_' || s[i - 1] == '$')) return false;
        int e = i + 1;
        if (e < s.Length && (char.IsLetter(s[e]) || s[e] == '_'))
            while (e < s.Length && (char.IsLetterOrDigit(s[e]) || s[e] == '_')) e++;
        if (e < s.Length && s[e] == '$') { tag = s[i..(e + 1)]; return true; }
        return false;
    }

    /// <summary>Joins token texts for diagnostics (tests).</summary>
    public static string Dump(IEnumerable<SqlToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens) sb.Append(t.Kind).Append(':').Append(t.Text).Append(' ');
        return sb.ToString();
    }
}
