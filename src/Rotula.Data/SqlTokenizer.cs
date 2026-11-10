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
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comments. MySQL wants real (ASCII) whitespace or a control character after "--": it does NOT treat a no-break space or
            // another Unicode space as one, so "--<NBSP>; DROP ..." is live SQL there. char.IsWhiteSpace would call it a comment and hide it.
            if (c == '-' && i + 1 < n && sql[i + 1] == '-' &&
                (!rules.DashNeedsSpace || i + 2 >= n || IsAsciiSpaceOrControl(sql[i + 2])))
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

            // PostgreSQL (and DuckDB) unicode escapes: U&"pg_r\0065ad_file" is the name pg_read_file, U&'...' a string with escapes. The guard
            // would see a quoted name that still contains the raw backslash escape and never match it against the forbidden functions.
            // Nothing a label needs uses them, and in other engines the text is a bitwise AND on a column called u, so refuse everywhere.
            if ((c == 'U' || c == 'u') && i + 2 < n && sql[i + 1] == '&' && (sql[i + 2] == '"' || sql[i + 2] == '\'')
                && !(i > 0 && IsWordChar(sql[i - 1], rules)))
                return new(tokens, DataKeys.RejectUnclearText);

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
                string word = sql[i..e];
                // A word that starts with a digit is a number, or the lexers disagree. "1DROP" is ONE identifier in MySQL and PostgreSQL but
                // "1" followed by DROP in SQL Server ("SELECT 1AS x" works there), so as a single word it would skip the keyword check.
                // Only plain numeric literals are accepted; anything else must be written with a space or in quotes.
                // One exception: where '#' is a word character (Jet/ACE, SQL Server) the closing '#' of a date literal such as #1/31/2020# is glued to its last number.
                string number = !rules.HashComment && word.EndsWith('#') ? word[..^1] : word;
                if (char.IsDigit(c) && !IsPlainNumber(number)) return new(tokens, DataKeys.RejectUnclearText);
                if (!IsAscii(word))
                {
                    if (!AddNonAsciiWord(tokens, i, word)) return new(tokens, DataKeys.RejectUnclearText);
                }
                else tokens.Add(new(SqlTokenKind.Word, i, e - i, word));
                i = e;
                continue;
            }

            // Whatever is left is punctuation or an operator. Hidden characters are not: a zero-width or control character inside a keyword
            // ("DR<ZWSP>OP") is invisible to the reader and lexed differently by each engine, and no honest query contains one outside a
            // string, a quoted name or a comment. Refuse instead of guessing.
            if (IsHiddenCharacter(c)) return new(tokens, DataKeys.RejectUnclearText);

            tokens.Add(new(SqlTokenKind.Symbol, i, 1, c.ToString()));
            i++;
        }
        return new(tokens, null);
    }

    /// <summary>True for ASCII white space and control characters (what MySQL accepts after <c>--</c>); NOT for Unicode spaces such as U+00A0.</summary>
    private static bool IsAsciiSpaceOrControl(char c) => c <= ' ' || c == '\u007F';

    private static bool IsAscii(string s)
    {
        foreach (char ch in s) if (ch > '\u007F') return false;
        return true;
    }

    /// <summary>
    /// Format (zero-width, soft hyphen, bidi marks, BOM), control characters that are not white space, surrogates, private-use and
    /// unassigned code points. Visible symbols and punctuation (including curly quotes) are not hidden and stay ordinary symbols.
    /// </summary>
    private static bool IsHiddenCharacter(char c) => char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control
        or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.PrivateUse
        or System.Globalization.UnicodeCategory.OtherNotAssigned;

    /// <summary>
    /// A digit-led word that is a number in every engine: digits with optional <c>_</c> separators, an exponent (<c>1e10</c>; <c>1e</c> before
    /// a sign is a word of its own), <c>0x</c>/<c>0b</c>/<c>0o</c> literals, and the Oracle binary float suffixes <c>f</c>/<c>d</c>. ASCII only:
    /// a Unicode digit is not a number to every engine.
    /// </summary>
    private static bool IsPlainNumber(string w)
    {
        int k = 0;
        while (k < w.Length && w[k] is >= '0' and <= '9') k++;
        if (k == 0) return false;
        if (k == w.Length) return true;
        char next = w[k];
        // 1_000 (PostgreSQL 16, DuckDB, ClickHouse digit separators): only digits and underscores.
        if (next == '_') return w.AsSpan(k).IndexOfAnyExcept("_0123456789") < 0;
        // 1e, 1e10, 1E5
        if (next is 'e' or 'E') return AllIn(w, k + 1, "0123456789");
        // 1f, 1d (Oracle BINARY_FLOAT / BINARY_DOUBLE)
        if (next is 'f' or 'F' or 'd' or 'D') return k + 1 == w.Length;
        // 0x1F, 0b101, 0o17: the prefix must be a lone zero.
        if (k == 1 && w[0] == '0' && next is 'x' or 'X' or 'b' or 'B' or 'o' or 'O') return w.Length > 2 && AllIn(w, 2, "0123456789abcdefABCDEF_");
        return false;
    }

    private static bool AllIn(string s, int from, string allowed)
    {
        for (int k = from; k < s.Length; k++) if (!allowed.Contains(s[k])) return false;
        return true;
    }

    /// <summary>
    /// Adds a word that contains non-ASCII letters. Engines disagree on those: some fold full-width forms, some treat letters newer than their
    /// Unicode tables as separators, so a keyword glued to one ("\u00F1DROP") might be a keyword to the engine. The word is therefore added
    /// as its compatibility-normalized form when that is plain ASCII (full-width "DROP" becomes DROP), and otherwise split into its
    /// ASCII and non-ASCII runs so each ASCII run is judged on its own. Returns false if the text cannot be normalized.
    /// </summary>
    private static bool AddNonAsciiWord(List<SqlToken> tokens, int start, string word)
    {
        try
        {
            string folded = word.Normalize(NormalizationForm.FormKC);
            if (IsAscii(folded))
            {
                tokens.Add(new(SqlTokenKind.Word, start, word.Length, folded));
                return true;
            }
            int k = 0;
            while (k < word.Length)
            {
                bool ascii = word[k] <= '\u007F';
                int e = k + 1;
                while (e < word.Length && (word[e] <= '\u007F') == ascii) e++;
                string run = word[k..e];
                tokens.Add(new(SqlTokenKind.Word, start + k, e - k, ascii ? run : run.Normalize(NormalizationForm.FormKC)));
                k = e;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
        string name = inner.Replace(new string(closer, 2), closer.ToString(), StringComparison.Ordinal);
        // A full-width spelling of pg_read_file must not slip past the function lists, so non-ASCII names are compared in their compatibility form.
        if (!IsAscii(name))
        {
            try { name = name.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { /* ill-formed text (lone surrogate): keep it as is, it cannot equal a function name */ }
        }
        return name;
    }

    private static bool IsWordStart(char c, SqlLexRules r) => char.IsLetterOrDigit(c) || c == '_' || (c == '#' && !r.HashComment);

    private static bool IsWordChar(char c, SqlLexRules r) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || (c == '#' && !r.HashComment);

    /// <summary>
    /// Skips to the end of a line comment. Engines disagree on what ends one: LF and CR everywhere, but some also honour vertical tab, form
    /// feed, NEL or the Unicode line/paragraph separators. Ending the comment at ANY of them is the safe side: if the engine keeps
    /// going the guard has only looked at extra text (worst case a harmless query is refused), but a comment the guard keeps open
    /// while the engine has closed it would hide live SQL.
    /// </summary>
    private static int SkipLine(string s, int i)
    {
        while (i < s.Length && s[i] is not ('\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')) i++;
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
