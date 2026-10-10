using System.Text;

namespace Rotula.Data;

/// <summary>Kinds of token the guard cares about. Literals and comments are consumed and never become words.</summary>
internal enum SqlTokenKind
{
    /// <summary>An unquoted identifier, keyword or number.</summary>
    Word,
    /// <summary>A quoted identifier or a string literal (see <see cref="SqlToken.IsString"/>).</summary>
    Quoted,
    /// <summary>A <c>@name</c> (or dialect-native <c>:name</c>, <c>$name</c>) parameter placeholder (not <c>@@system_variable</c>).</summary>
    Parameter,
    /// <summary>Any other single character: punctuation and operators.</summary>
    Symbol,
}

/// <summary>One lexical token with its position in the source text.</summary>
/// <param name="Kind">Token kind.</param>
/// <param name="Start">Index of the first character.</param>
/// <param name="Length">Number of characters.</param>
/// <param name="Text">Word text, parameter name or unquoted identifier name; empty for string literals.</param>
/// <param name="IsString">True for a string literal (as opposed to a quoted identifier).</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length, string Text, bool IsString = false);

/// <summary>Result of tokenizing: the tokens, or the reason key if the text cannot be tokenized safely.</summary>
internal sealed record SqlTokenizeResult(IReadOnlyList<SqlToken> Tokens, string? ErrorKey);

/// <summary>A real tokenizer (strings, comments, quoted names, dollar quotes, q-quotes), not a regex.</summary>
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
            if (c == '/' && rules.SlashSlashComment && i + 1 < n && sql[i + 1] == '/') { i = SkipLine(sql, i); continue; }

            // Informix: { ... } is a comment that does not nest. Unterminated means the engine sees the rest as comment too, but we refuse.
            if (c == '{' && rules.BraceComments)
            {
                int close = sql.IndexOf('}', i + 1);
                if (close < 0) return new(tokens, DataKeys.RejectUnterminated);
                i = close + 1;
                continue;
            }

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

            // BigQuery triple-quoted strings. Checked before the ordinary quote so '' (empty string) stays an empty string.
            if (rules.TripleQuotes && (c == '\'' || c == '"') && i + 2 < n && sql[i + 1] == c && sql[i + 2] == c)
            {
                int end = SkipTripleQuoted(sql, i, c, c == '\'' ? rules.SingleQuoteBackslash : rules.DoubleQuoteBackslash);
                if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty, IsString: true));
                i = end;
                continue;
            }

            if (c == '\'' || c == '"' || (c == '`' && rules.Backticks))
            {
                int end = SkipQuoted(sql, i, c, BackslashFor(rules, c), DoublingFor(rules, c));
                if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                // Keep the name of a quoted identifier ("fn", `fn`): PostgreSQL and SQLite accept "pg_read_file"(...) as a call, so the
                // guard must see through the quotes. String literals ('...') carry no text; they are data, never names.
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, c == '\'' ? string.Empty : Unquote(sql, i, end, c), IsString: c == '\''));
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
            if (c == '$' && (rules.PostgresStrings || rules.DollarQuotes) && TryDollarTag(sql, i, out string? tag))
            {
                int close = sql.IndexOf(tag!, i + tag!.Length, StringComparison.Ordinal);
                if (close < 0) return new(tokens, DataKeys.RejectUnterminated);
                int end = close + tag.Length;
                tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty, IsString: true));
                i = end;
                continue;
            }
            // DuckDB $name bind variable (only when it is not a $tag$ opener, which was handled above).
            if (c == '$' && rules.DollarParameters && i + 1 < n && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_')
                && !(i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_' || sql[i - 1] == '$')))
            {
                int e = i + 1;
                while (e < n && (char.IsLetterOrDigit(sql[e]) || sql[e] == '_')) e++;
                tokens.Add(new(SqlTokenKind.Parameter, i, e - i, sql[(i + 1)..e]));
                i = e;
                continue;
            }
            // Oracle :name bind variable. '::' (a cast elsewhere) and ':=' never start one.
            if (c == ':' && rules.ColonParameters && i + 1 < n && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_')
                && !(i > 0 && sql[i - 1] == ':'))
            {
                int e = i + 1;
                while (e < n && (char.IsLetterOrDigit(sql[e]) || sql[e] == '_')) e++;
                tokens.Add(new(SqlTokenKind.Parameter, i, e - i, sql[(i + 1)..e]));
                i = e;
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
                    int end = SkipQuoted(sql, e, '\'', backslash: true, doubling: true);
                    if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                    tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty, IsString: true));
                    i = end;
                    continue;
                }
                // Oracle/Firebird q'<d>...<d>' and nq'<d>...<d>' (prefix q/Q or nq/NQ directly followed by a quote).
                if (rules.QQuotes && e < n && sql[e] == '\'' && IsQPrefix(sql, i, e))
                {
                    int end = SkipQQuoted(sql, e + 1);
                    if (end < 0) return new(tokens, DataKeys.RejectUnterminated);
                    tokens.Add(new(SqlTokenKind.Quoted, i, end - i, string.Empty, IsString: true));
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

    private static bool BackslashFor(SqlLexRules r, char quote)
        => quote == '\'' ? r.SingleQuoteBackslash : quote == '"' ? r.DoubleQuoteBackslash : r.BacktickBackslash;

    private static bool DoublingFor(SqlLexRules r, char quote)
        => quote == '\'' ? r.SingleQuoteDoubling : quote == '"' ? r.DoubleQuoteDoubling : r.BacktickDoubling;

    private static bool IsQPrefix(string s, int start, int end)
    {
        int len = end - start;
        if (len == 1) return s[start] is 'q' or 'Q';
        return len == 2 && (s[start] is 'n' or 'N') && (s[start + 1] is 'q' or 'Q');
    }

    /// <summary>
    /// <paramref name="open"/> is the index just after the opening quote, i.e. the delimiter. Returns the index after the closing
    /// <c>delimiter'</c>, or -1. Oracle: the delimiter is any character except white space; <c>[ { ( &lt;</c> close with their mirror.
    /// </summary>
    private static int SkipQQuoted(string s, int open)
    {
        if (open >= s.Length || char.IsWhiteSpace(s[open])) return -1;
        char d = s[open];
        char closer = d switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => d };
        for (int i = open + 1; i + 1 < s.Length; i++)
            if (s[i] == closer && s[i + 1] == '\'') return i + 2;
        return -1;
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
    private static int SkipQuoted(string s, int i, char quote, bool backslash, bool doubling)
    {
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == quote)
            {
                if (doubling && i + 1 < s.Length && s[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return -1;
    }

    /// <summary>
    /// BigQuery: ends at three unescaped quotes in a row that match the opening ones. If a fourth quote follows, where the string
    /// really ends is ambiguous between lexers, so the text is refused (-1) rather than guessed. Backslash always skips the next
    /// character, which is also how raw (<c>r'...'</c>) strings find their end.
    /// </summary>
    private static int SkipTripleQuoted(string s, int i, char quote, bool backslash)
    {
        i += 3;
        while (i < s.Length)
        {
            char c = s[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == quote && i + 2 < s.Length && s[i + 1] == quote && s[i + 2] == quote)
            {
                if (i + 3 < s.Length && s[i + 3] == quote) return -1;
                return i + 3;
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
