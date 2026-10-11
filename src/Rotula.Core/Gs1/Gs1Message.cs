using System.Text;
using Rotula.Core.Validation;

namespace Rotula.Core.Gs1;

/// <summary>One GS1 element string: an application identifier and its value.</summary>
/// <param name="Ai">The identifier digits, for example <c>10</c>.</param>
/// <param name="Value">The value, without separators or parentheses.</param>
public sealed record Gs1Element(string Ai, string Value)
{
    /// <summary>The element in the form used on labels, for example <c>(10)ABC123</c>.</summary>
    public override string ToString() => "(" + Ai + ")" + Value;
}

/// <summary>
/// A series of GS1 element strings: what a GS1-128, GS1 DataMatrix or GS1 DataBar Expanded symbol carries. It can be read from the
/// text a person types (<c>(01)09506000134352(17)251231</c>, "bracketed") or from the character string a scanner reports
/// (<c>010950600013435217251231</c>, with the group separator after variable-length elements), and written in both forms. Reading does
/// not check the content: that is <see cref="Gs1Validator"/>'s job.
/// </summary>
public sealed class Gs1Message
{
    /// <summary>The FNC1 element separator as it appears in the data a scanner reports (ASCII group separator).</summary>
    public const char GroupSeparator = '\u001d';

    /// <summary>Creates a message from elements (the order is kept).</summary>
    public Gs1Message(IEnumerable<Gs1Element> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        Elements = elements.ToList();
    }

    /// <summary>The elements in the order they are encoded.</summary>
    public IReadOnlyList<Gs1Element> Elements { get; }

    /// <summary>The value of the first element with identifier <paramref name="ai"/>, or null.</summary>
    public string? ValueOf(string ai) => Elements.FirstOrDefault(e => e.Ai == ai)?.Value;

    // ---- writing ---------------------------------------------------------------------------------

    /// <summary>
    /// The form people read and type: every identifier in parentheses, for example <c>(01)09506000134352(17)251231(10)ABC</c>.
    /// A parenthesis inside a value is written as <c>\(</c> so that the text can be read back.
    /// </summary>
    public string ToBracketed()
    {
        var sb = new StringBuilder();
        foreach (var e in Elements) sb.Append('(').Append(e.Ai).Append(')').Append(e.Value.Replace("(", "\\(", StringComparison.Ordinal));
        return sb.ToString();
    }

    /// <summary>The human-readable interpretation printed under a symbol (the same as <see cref="ToBracketed"/>, without the escape).</summary>
    public string ToHumanReadable() => string.Concat(Elements.Select(e => e.ToString()));

    /// <summary>
    /// The character string that is encoded: identifiers and values without parentheses, with <paramref name="separator"/> (FNC1) after
    /// every element of variable length that is not the last (GS1 General Specifications 5.4.8.3). The FNC1 that starts the symbol is not
    /// part of the string. An element is "of variable length" unless the dictionary says its length is predefined.
    /// </summary>
    public string ToElementString(char separator = GroupSeparator)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            sb.Append(e.Ai).Append(e.Value);
            if (i < Elements.Count - 1 && NeedsSeparator(e.Ai)) sb.Append(separator);
        }
        return sb.ToString();
    }

    /// <summary>
    /// How many data characters a GS1-128 symbol needs: identifiers, values and the FNC1 separators between elements. The start
    /// character, the leading FNC1, the check character and the stop character are not counted (GS1 General Specifications 5.4.4.3,
    /// which limits the count to 48).
    /// </summary>
    public int DataCharacterCount => ToElementString().Length;

    /// <summary>True when an FNC1 separator must follow the value of <paramref name="ai"/> if another element comes after it.</summary>
    public static bool NeedsSeparator(string ai) => !Gs1Dictionary.IsPredefinedLength(ai);

    // ---- reading ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads either form: text that starts with <c>(</c> is bracketed, anything else is a scanner string (a leading symbology identifier
    /// such as <c>]C1</c> is dropped). Returns null and fills <paramref name="issues"/> when the text cannot be split into elements.
    /// </summary>
    public static Gs1Message? Parse(string text, out IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.TrimStart().StartsWith('(') ? ParseBracketed(text, out issues) : ParseElementString(text, out issues);
    }

    /// <summary>
    /// Reads the bracketed form. White space between elements is ignored (it is not part of any value: space is not in the GS1 character
    /// set), and <c>\(</c> stands for a parenthesis inside a value.
    /// </summary>
    public static Gs1Message? ParseBracketed(string text, out IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<ValidationIssue>();
        issues = found;
        var elements = new List<Gs1Element>();
        int i = 0;
        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            if (text[i] != '(') { found.Add(Err("Gs1.Parse.Malformed", i)); return null; }
            int close = text.IndexOf(')', i + 1);
            if (close < 0) { found.Add(Err("Gs1.Parse.Malformed", i)); return null; }
            string ai = text.Substring(i + 1, close - i - 1);
            if (ai.Length is < 2 or > 4 || ai.Any(c => c is < '0' or > '9')) { found.Add(Err("Gs1.Parse.Malformed", i)); return null; }
            var value = new StringBuilder();
            int j = close + 1;
            while (j < text.Length && text[j] != '(')
            {
                if (text[j] == '\\' && j + 1 < text.Length && text[j + 1] == '(') { value.Append('('); j += 2; }
                else value.Append(text[j++]);
            }
            // Trailing white space belongs to the gap before the next element, not to the value.
            elements.Add(new Gs1Element(ai, value.ToString().TrimEnd()));
            i = j;
        }
        if (elements.Count == 0) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.Empty")); return null; }
        return new Gs1Message(elements);
    }

    /// <summary>
    /// Reads the scanner form: identifiers and values run together, a group separator (or the FNC1 written as such) follows every element of
    /// variable length that is not the last. Identifiers are told apart with the dictionary.
    /// </summary>
    public static Gs1Message? ParseElementString(string text, out IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<ValidationIssue>();
        issues = found;
        int i = 0;
        // A reader prefixes the data with a symbology identifier: ]C1 (GS1-128), ]d2 (DataMatrix), ]Q3 (QR Code), ]e0 (DataBar).
        if (text.Length >= 3 && text[0] == ']' && text[1] is 'C' or 'd' or 'Q' or 'e') i = 3;
        if (i < text.Length && text[i] == GroupSeparator) i++;
        var elements = new List<Gs1Element>();
        while (i < text.Length)
        {
            var def = Gs1Dictionary.FindAtStart(text.AsSpan(i));
            if (def is null)
            {
                found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.UnknownAi", null, text.Substring(i, Math.Min(4, text.Length - i))));
                return null;
            }
            int start = i + def.Ai.Length;
            int end;
            if (def.PredefinedLength)
            {
                end = start + def.MaxValueLength;
                if (end > text.Length) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.TooShort", null, def.Ai)); return null; }
            }
            else
            {
                end = text.IndexOf(GroupSeparator, start);
                if (end < 0) end = text.Length;
            }
            elements.Add(new Gs1Element(def.Ai, text[start..end]));
            i = end;
            if (i < text.Length && text[i] == GroupSeparator)
            {
                // A separator after an element of predefined length is a mistake in the data (5.4.8.3 says there is none).
                if (def.PredefinedLength) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.SeparatorAfterFixed", null, def.Ai)); return null; }
                i++;
                if (i >= text.Length) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.TrailingSeparator")); return null; }
            }
        }
        if (elements.Count == 0) { found.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Parse.Empty")); return null; }
        return new Gs1Message(elements);
    }

    private static ValidationIssue Err(string key, int position) =>
        new(ValidationSeverity.Error, key, null, (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() => ToBracketed();
}
