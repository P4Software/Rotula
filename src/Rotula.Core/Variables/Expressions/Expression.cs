using System.Globalization;
using System.Text;
using Rotula.Core.Validation;

namespace Rotula.Core.Variables.Expressions;

/// <summary>
/// A problem found while reading or evaluating an expression: a message key (see <see cref="VariableMessageKeys"/>), the 1-based character
/// position it refers to (0 when it concerns the whole expression) and extra arguments.
/// </summary>
/// <param name="Key">Stable message key.</param>
/// <param name="Position">1-based position in the expression text, or 0 when not tied to one place.</param>
/// <param name="Args">Arguments after the variable name and position.</param>
public sealed record ExpressionError(string Key, int Position, IReadOnlyList<string> Args)
{
    /// <summary>Turns the error into a validation issue whose arguments are: variable name, position, then the extra arguments.</summary>
    public ValidationIssue ToIssue(string variableName, ValidationSeverity severity = ValidationSeverity.Error)
    {
        var args = new List<string>(Args.Count + 2) { variableName, Position.ToString(CultureInfo.InvariantCulture) };
        args.AddRange(Args);
        return new ValidationIssue(severity, Key, null, args.ToArray());
    }
}

/// <summary>One node of a parsed expression.</summary>
public abstract record ExpressionNode(int Position);

/// <summary>A quoted text or a number.</summary>
public sealed record LiteralNode(int Position, string Value) : ExpressionNode(Position);

/// <summary>A reference to another variable (a leading <c>#</c> names a merge counter column).</summary>
public sealed record VariableNode(int Position, string Name) : ExpressionNode(Position);

/// <summary>A call of one of the built-in functions.</summary>
public sealed record CallNode(int Position, string Function, IReadOnlyList<ExpressionNode> Arguments) : ExpressionNode(Position);

/// <summary>A parsed, validated expression ready to evaluate any number of times.</summary>
public sealed class ParsedExpression
{
    internal ParsedExpression(ExpressionNode root, IReadOnlyList<string> references)
    {
        Root = root;
        References = references;
    }

    /// <summary>Root of the syntax tree.</summary>
    public ExpressionNode Root { get; }

    /// <summary>Names of the variables the expression reads, in order of appearance (duplicates removed, case-insensitive).</summary>
    public IReadOnlyList<string> References { get; }
}

/// <summary>
/// Reads the text of a computed variable. The language is deliberately tiny and cannot run code: it has text and number literals, variable
/// names, the <c>&amp;</c> join operator and a fixed list of functions. There is no reflection, no loops, no assignment and no access to files
/// or the network. Hard limits apply to the length, nesting depth and number of parts (see <see cref="MaxLength"/>, <see cref="MaxDepth"/>,
/// <see cref="MaxNodes"/>).
/// </summary>
/// <remarks>
/// <code>
/// expression := term ( '&amp;' term )*
/// term       := 'text' | "text" | number | name | '#'name | name '(' [ expression ( ',' expression )* ] ')' | '(' expression ')'
/// </code>
/// Inside quotes a doubled quote or a backslash escape (<c>\\ \' \" \n \t</c>) writes the character itself. Names are letters, digits and
/// underscore, not starting with a digit; function names are not case sensitive.
/// </remarks>
public static class ExpressionParser
{
    /// <summary>Longest accepted expression text.</summary>
    public const int MaxLength = VariableLimits.MaxExpressionLength;
    /// <summary>Deepest allowed nesting of parentheses and calls.</summary>
    public const int MaxDepth = 16;
    /// <summary>Most syntax-tree nodes an expression may have.</summary>
    public const int MaxNodes = 200;

    /// <summary>Parses <paramref name="text"/>. Returns null and sets <paramref name="error"/> when it is not valid.</summary>
    public static ParsedExpression? Parse(string? text, out ExpressionError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = new ExpressionError(VariableMessageKeys.ExpressionEmpty, 0, Array.Empty<string>());
            return null;
        }
        if (text.Length > MaxLength)
        {
            error = new ExpressionError(VariableMessageKeys.ExpressionTooLong, 0, new[] { MaxLength.ToString(CultureInfo.InvariantCulture) });
            return null;
        }
        try
        {
            var parser = new Parser(text);
            var root = parser.ParseAll();
            return new ParsedExpression(root, parser.References);
        }
        catch (ExpressionException ex)
        {
            error = ex.Error;
            return null;
        }
    }

    internal sealed class ExpressionException(ExpressionError error) : Exception(error.Key)
    {
        public ExpressionError Error { get; } = error;
    }

    private enum T { End, Text, Number, Name, Open, Close, Comma, Amp }

    private readonly record struct Token(T Type, string Value, int Position);

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        private Token _cur;
        private int _nodes;
        private readonly List<string> _refs = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public Parser(string s)
        {
            _s = s;
            Next();
        }

        public IReadOnlyList<string> References => _refs;

        public ExpressionNode ParseAll()
        {
            var node = ParseExpression(0);
            if (_cur.Type != T.End) throw Unexpected(_cur);
            return node;
        }

        private ExpressionNode ParseExpression(int depth)
        {
            var first = ParseTerm(depth);
            if (_cur.Type != T.Amp) return first;
            // a & b & c becomes one concat call, so a long chain does not count as deep nesting.
            var parts = new List<ExpressionNode> { first };
            int pos = _cur.Position;
            while (_cur.Type == T.Amp)
            {
                Next();
                parts.Add(ParseTerm(depth));
            }
            Count(pos);
            return new CallNode(pos, "concat", parts);
        }

        private ExpressionNode ParseTerm(int depth)
        {
            if (depth > MaxDepth)
                throw Fail(VariableMessageKeys.ExpressionTooDeep, _cur.Position, MaxDepth.ToString(CultureInfo.InvariantCulture));
            var t = _cur;
            switch (t.Type)
            {
                case T.Text:
                case T.Number:
                    Next();
                    Count(t.Position);
                    return new LiteralNode(t.Position, t.Value);
                case T.Open:
                {
                    Next();
                    var inner = ParseExpression(depth + 1);
                    if (_cur.Type != T.Close) throw Unexpected(_cur);
                    Next();
                    return inner;
                }
                case T.Name:
                {
                    Next();
                    if (_cur.Type != T.Open)
                    {
                        Count(t.Position);
                        if (_seen.Add(t.Value)) _refs.Add(t.Value);
                        return new VariableNode(t.Position, t.Value);
                    }
                    if (t.Value[0] == '#') throw Unexpected(_cur);
                    Next();
                    var args = new List<ExpressionNode>();
                    if (_cur.Type != T.Close)
                    {
                        while (true)
                        {
                            args.Add(ParseExpression(depth + 1));
                            if (_cur.Type == T.Comma) { Next(); continue; }
                            break;
                        }
                    }
                    if (_cur.Type != T.Close) throw Unexpected(_cur);
                    Next();
                    Count(t.Position);
                    var fn = ExpressionFunctions.Find(t.Value) ?? throw Fail(VariableMessageKeys.ExpressionUnknownFunction, t.Position, t.Value);
                    if (args.Count < fn.MinArgs || args.Count > fn.MaxArgs)
                        throw Fail(VariableMessageKeys.ExpressionArgumentCount, t.Position, fn.Name,
                            fn.MinArgs.ToString(CultureInfo.InvariantCulture), fn.MaxArgs.ToString(CultureInfo.InvariantCulture));
                    return new CallNode(t.Position, fn.Name, args);
                }
                default:
                    throw Unexpected(t);
            }
        }

        private void Count(int pos)
        {
            if (++_nodes > MaxNodes)
                throw Fail(VariableMessageKeys.ExpressionTooComplex, pos, MaxNodes.ToString(CultureInfo.InvariantCulture));
        }

        private static ExpressionException Fail(string key, int position, params string[] args) => new(new ExpressionError(key, position, args));

        private ExpressionException Unexpected(Token t) =>
            t.Type == T.End
                ? Fail(VariableMessageKeys.ExpressionUnexpectedEnd, t.Position)
                : Fail(VariableMessageKeys.ExpressionUnexpectedToken, t.Position, t.Type == T.Text ? "'" + t.Value + "'" : t.Value);

        // ---- tokenizer ----------------------------------------------------------------------

        private void Next()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            int start = _i;
            if (_i >= _s.Length) { _cur = new Token(T.End, string.Empty, _s.Length + 1); return; }
            char c = _s[_i];
            switch (c)
            {
                case '(': _i++; _cur = new Token(T.Open, "(", start + 1); return;
                case ')': _i++; _cur = new Token(T.Close, ")", start + 1); return;
                case ',': _i++; _cur = new Token(T.Comma, ",", start + 1); return;
                case '&': _i++; _cur = new Token(T.Amp, "&", start + 1); return;
                case '\'':
                case '"':
                    _cur = ReadText(c);
                    return;
            }
            if (char.IsAsciiDigit(c) || (c == '-' && _i + 1 < _s.Length && char.IsAsciiDigit(_s[_i + 1])))
            {
                _i++;
                while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    int digits = 0;
                    while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) { _i++; digits++; }
                    if (digits == 0) throw Fail(VariableMessageKeys.ExpressionBadNumber, start + 1, _s[start.._i]);
                }
                // A number glued to letters ("12abc") is a typo, not two tokens.
                if (_i < _s.Length && (char.IsAsciiLetter(_s[_i]) || _s[_i] == '_'))
                    throw Fail(VariableMessageKeys.ExpressionBadNumber, start + 1, _s.Substring(start, _i - start + 1));
                _cur = new Token(T.Number, _s[start.._i], start + 1);
                return;
            }
            if (char.IsAsciiLetter(c) || c == '_' || (c == '#' && _i + 1 < _s.Length && (char.IsAsciiLetter(_s[_i + 1]) || _s[_i + 1] == '_')))
            {
                _i++;
                while (_i < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                _cur = new Token(T.Name, _s[start.._i], start + 1);
                return;
            }
            throw Fail(VariableMessageKeys.ExpressionUnexpectedChar, start + 1, c.ToString());
        }

        private Token ReadText(char quote)
        {
            int start = _i;
            _i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Fail(VariableMessageKeys.ExpressionUnterminatedString, start + 1);
                char c = _s[_i];
                if (c == quote)
                {
                    if (_i + 1 < _s.Length && _s[_i + 1] == quote) { sb.Append(quote); _i += 2; continue; }
                    _i++;
                    break;
                }
                if (c == '\\')
                {
                    if (_i + 1 >= _s.Length) throw Fail(VariableMessageKeys.ExpressionUnterminatedString, start + 1);
                    char e = _s[_i + 1];
                    switch (e)
                    {
                        case '\\': case '\'': case '"': sb.Append(e); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: throw Fail(VariableMessageKeys.ExpressionUnexpectedChar, _i + 1, e.ToString());
                    }
                    _i += 2;
                    continue;
                }
                sb.Append(c);
                _i++;
            }
            return new Token(T.Text, sb.ToString(), start + 1);
        }
    }
}
