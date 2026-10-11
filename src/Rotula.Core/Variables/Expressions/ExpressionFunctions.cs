using System.Globalization;
using System.Text;

namespace Rotula.Core.Variables.Expressions;

/// <summary>Description of one built-in function (used by the parser to check names and argument counts, and by documentation and user interfaces).</summary>
/// <param name="Name">Lower-case name.</param>
/// <param name="MinArgs">Fewest arguments.</param>
/// <param name="MaxArgs">Most arguments.</param>
/// <param name="Signature">Human-readable signature.</param>
/// <param name="Description">One-sentence description (English; the UI may show its own text).</param>
public sealed record ExpressionFunction(string Name, int MinArgs, int MaxArgs, string Signature, string Description);

/// <summary>The fixed list of functions an expression can call. Nothing else is reachable from an expression.</summary>
public static class ExpressionFunctions
{
    private const int Many = 32;

    /// <summary>All functions, in documentation order.</summary>
    public static IReadOnlyList<ExpressionFunction> All { get; } = new ExpressionFunction[]
    {
        new("concat", 1, Many, "concat(a, b, ...)", "Joins the texts one after the other. The same as a & b."),
        new("join", 2, Many, "join(separator, a, b, ...)", "Joins the texts with the separator between them, leaving out empty ones."),
        new("upper", 1, 1, "upper(text)", "Upper case."),
        new("lower", 1, 1, "lower(text)", "Lower case."),
        new("trim", 1, 1, "trim(text)", "Removes spaces at both ends."),
        new("length", 1, 1, "length(text)", "Number of characters."),
        new("left", 2, 2, "left(text, count)", "The first count characters."),
        new("right", 2, 2, "right(text, count)", "The last count characters."),
        new("substring", 2, 3, "substring(text, start, [length])", "Part of the text; start counts from 0."),
        new("padleft", 2, 3, "padleft(text, width, [character])", "Adds the character (default space) on the left until the text is width long."),
        new("padright", 2, 3, "padright(text, width, [character])", "Adds the character (default space) on the right until the text is width long."),
        new("replace", 3, 3, "replace(text, find, replacement)", "Replaces every occurrence of find (case sensitive)."),
        new("ifempty", 2, 2, "ifempty(text, fallback)", "The text, or the fallback when the text is empty."),
        new("coalesce", 2, Many, "coalesce(a, b, ...)", "The first one that is not empty."),
        new("number", 2, 3, "number(value, format, [culture])", "Writes a number with a .NET numeric format such as 0000.00 or N2."),
        new("date", 2, 3, "date(value, format, [culture])", "Rewrites a date given as yyyy-MM-dd or yyyyMMdd with a date preset or .NET pattern."),
    };

    private static readonly Dictionary<string, ExpressionFunction> ByName = All.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a function by name (not case sensitive), or null.</summary>
    public static ExpressionFunction? Find(string name) => ByName.GetValueOrDefault(name);
}

/// <summary>Evaluates a <see cref="ParsedExpression"/> against variable values.</summary>
public static class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates the expression. Values are looked up with <paramref name="lookup"/> (null = the variable does not exist). The first error stops
    /// evaluation and is returned in <paramref name="error"/>; the result is then an empty string. When <paramref name="warnings"/> is given, a
    /// variable that does not exist is not an error: it counts as empty text and a warning is added to the list (the same leniency as a missing
    /// <c>{{placeholder}}</c> on a label).
    /// </summary>
    public static string Evaluate(ParsedExpression expression, Func<string, string?> lookup, out ExpressionError? error, List<ExpressionError>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(lookup);
        try
        {
            error = null;
            return Eval(expression.Root, lookup, warnings);
        }
        catch (ExpressionParser.ExpressionException ex)
        {
            error = ex.Error;
            return string.Empty;
        }
    }

    private static ExpressionParser.ExpressionException Fail(string key, int position, params string[] args) => new(new ExpressionError(key, position, args));

    private static string Eval(ExpressionNode node, Func<string, string?> lookup, List<ExpressionError>? warnings)
    {
        switch (node)
        {
            case LiteralNode l:
                return l.Value;
            case VariableNode v:
            {
                string? value = lookup(v.Name);
                if (value is not null) return value;
                if (warnings is null) throw Fail(VariableMessageKeys.ExpressionUnknownVariable, v.Position, v.Name);
                warnings.Add(new ExpressionError(VariableMessageKeys.ExpressionUnknownVariable, v.Position, new[] { v.Name }));
                return string.Empty;
            }
            case CallNode c:
            {
                var args = new string[c.Arguments.Count];
                for (int i = 0; i < args.Length; i++) args[i] = Eval(c.Arguments[i], lookup, warnings);
                string result = Call(c, args);
                if (result.Length > VariableLimits.MaxValueLength)
                    throw Fail(VariableMessageKeys.ExpressionResultTooLong, c.Position, VariableLimits.MaxValueLength.ToString(CultureInfo.InvariantCulture));
                return result;
            }
            default:
                throw new InvalidOperationException("Unknown expression node.");
        }
    }

    private static string Call(CallNode c, string[] a)
    {
        string fn = c.Function;
        switch (fn)
        {
            case "concat":
            {
                long total = 0;
                foreach (var s in a) total += s.Length;
                if (total > VariableLimits.MaxValueLength) throw TooLong(c);
                return string.Concat(a);
            }
            case "join":
            {
                var parts = a.Skip(1).Where(s => s.Length > 0).ToList();
                long total = parts.Sum(s => (long)s.Length) + (long)Math.Max(0, parts.Count - 1) * a[0].Length;
                if (total > VariableLimits.MaxValueLength) throw TooLong(c);
                return string.Join(a[0], parts);
            }
            case "upper": return a[0].ToUpperInvariant();
            case "lower": return a[0].ToLowerInvariant();
            case "trim": return a[0].Trim();
            case "length": return a[0].Length.ToString(CultureInfo.InvariantCulture);
            case "left": { int n = Count(c, a, 1); return a[0][..Math.Min(n, a[0].Length)]; }
            case "right": { int n = Count(c, a, 1); return a[0][Math.Max(0, a[0].Length - n)..]; }
            case "substring":
            {
                int start = Count(c, a, 1);
                if (start >= a[0].Length) return string.Empty;
                int len = a.Length > 2 ? Count(c, a, 2) : a[0].Length - start;
                return a[0].Substring(start, Math.Min(len, a[0].Length - start));
            }
            case "padleft":
            case "padright":
            {
                int width = Count(c, a, 1);
                if (width > VariableLimits.MaxValueLength) throw TooLong(c);
                char pad = ' ';
                if (a.Length > 2)
                {
                    if (a[2].Length != 1) throw Fail(VariableMessageKeys.ExpressionBadArgument, c.Position, fn, "3");
                    pad = a[2][0];
                }
                return fn == "padleft" ? a[0].PadLeft(width, pad) : a[0].PadRight(width, pad);
            }
            case "replace":
            {
                if (a[1].Length == 0) throw Fail(VariableMessageKeys.ExpressionBadArgument, c.Position, fn, "2");
                // Count first: a short search text with a long replacement could otherwise build a huge string before any limit is checked.
                int hits = 0;
                for (int i = a[0].IndexOf(a[1], StringComparison.Ordinal); i >= 0; i = a[0].IndexOf(a[1], i + a[1].Length, StringComparison.Ordinal)) hits++;
                long size = a[0].Length + (long)hits * (a[2].Length - a[1].Length);
                if (size > VariableLimits.MaxValueLength) throw TooLong(c);
                return a[0].Replace(a[1], a[2], StringComparison.Ordinal);
            }
            case "ifempty": return a[0].Length > 0 ? a[0] : a[1];
            case "coalesce": return a.FirstOrDefault(s => s.Length > 0) ?? string.Empty;
            case "number": return FormatNumber(c, a);
            case "date": return FormatDate(c, a);
            default:
                throw new InvalidOperationException("Function without implementation: " + fn);
        }
    }

    private static ExpressionParser.ExpressionException TooLong(CallNode c) =>
        Fail(VariableMessageKeys.ExpressionResultTooLong, c.Position, VariableLimits.MaxValueLength.ToString(CultureInfo.InvariantCulture));

    private static int Count(CallNode c, string[] a, int index)
    {
        if (!int.TryParse(a[index].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0)
            throw Fail(VariableMessageKeys.ExpressionBadArgument, c.Position, c.Function, (index + 1).ToString(CultureInfo.InvariantCulture));
        return n;
    }

    private static CultureInfo CultureArg(CallNode c, string[] a, int index)
    {
        if (a.Length <= index || a[index].Length == 0) return CultureInfo.InvariantCulture;
        try { return CultureInfo.GetCultureInfo(a[index], predefinedOnly: true); }
        catch (CultureNotFoundException) { throw Fail(VariableMessageKeys.ExpressionBadArgument, c.Position, c.Function, (index + 1).ToString(CultureInfo.InvariantCulture)); }
    }

    private static string FormatNumber(CallNode c, string[] a)
    {
        if (!decimal.TryParse(a[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            throw Fail(VariableMessageKeys.ExpressionNotNumber, c.Position, c.Function, Clip(a[0]));
        if (a[1].Length == 0 || a[1].Length > 32) throw Fail(VariableMessageKeys.ExpressionBadFormat, c.Position, c.Function, Clip(a[1]));
        var culture = CultureArg(c, a, 2);
        try { return value.ToString(a[1], culture); }
        catch (FormatException) { throw Fail(VariableMessageKeys.ExpressionBadFormat, c.Position, c.Function, Clip(a[1])); }
    }

    private static string FormatDate(CallNode c, string[] a)
    {
        string text = a[0].Trim();
        if (!DateTime.TryParseExact(text, DateInputFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Fail(VariableMessageKeys.ExpressionNotDate, c.Position, c.Function, Clip(a[0]));
        var culture = CultureArg(c, a, 2);
        string? formatted = DateFormats.TryFormat(date, a[1], culture);
        return formatted ?? throw Fail(VariableMessageKeys.ExpressionBadFormat, c.Position, c.Function, Clip(a[1]));
    }

    /// <summary>The input shapes the <c>date</c> function reads.</summary>
    internal static readonly string[] DateInputFormats = { "yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm" };

    private static string Clip(string s) => s.Length > 40 ? s[..40] : s;
}
