using System.Text;
using System.Text.RegularExpressions;

namespace Rotula.Core.Barcodes;

/// <summary>Why a GS1 element string was rejected by <see cref="Gs1ElementString.TryReduce"/>.</summary>
public enum Gs1ElementStringProblem
{
    /// <summary>The text is not a series of <c>(AI)value</c> elements.</summary>
    Malformed,
    /// <summary>A value contains a character that GS1 DataBar Expanded cannot encode.</summary>
    Character,
    /// <summary>The element string is longer than the 77 characters DataBar Expanded can carry.</summary>
    TooLong,
}

/// <summary>
/// A GS1 element string written with the application identifiers in parentheses, as ZPL's <c>^BR</c> expects it for GS1 DataBar
/// Expanded, reduced to the character string that is actually encoded (identifiers and values without parentheses, with an FNC1
/// separator after every element of variable length that is not the last).
/// </summary>
public static class Gs1ElementString
{
    /// <summary>The FNC1 separator character (ASCII group separator) used inside the reduced string.</summary>
    public const char Fnc1 = '\u001d';

    /// <summary>Most characters a reduced element string may have (ISO/IEC 24724:2011 4.2).</summary>
    public const int MaxReducedLength = 77;

    private static readonly Regex Whole = new(@"^(\(\d{2,4}\)[^()]+)+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Piece = new(@"\((\d{2,4})\)([^()]*)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Application identifiers with a predefined length need no FNC1 after them (GS1 General Specifications, 5.4.8.3).
    private static readonly HashSet<string> FixedLengthPrefixes = new()
    {
        "00", "01", "02", "03", "04", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "31", "32", "33", "34", "35", "36", "41",
    };

    private const string AlphanumericPunctuation = "*,-./";
    private const string IsoIecPunctuation = "!\"%&'()*+,-./:;<=>?_ ";

    /// <summary>True when GS1 DataBar Expanded can encode <paramref name="c"/> in a value (digits, letters and the characters of ISO/IEC 646 set 82 plus the space).</summary>
    public static bool IsEncodable(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' || AlphanumericPunctuation.Contains(c) || IsoIecPunctuation.Contains(c);

    /// <summary>The elements (identifier, value) in order; empty when the text is not an element string.</summary>
    public static IReadOnlyList<(string Ai, string Value)> Parse(string data) =>
        Whole.IsMatch(data) ? Piece.Matches(data).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList() : Array.Empty<(string, string)>();

    /// <summary>Reduces <paramref name="data"/>; returns null on success or the reason it cannot be encoded.</summary>
    public static Gs1ElementStringProblem? TryReduce(string data, out string reduced)
    {
        reduced = string.Empty;
        var elements = Parse(data);
        if (elements.Count == 0) return Gs1ElementStringProblem.Malformed;
        var sb = new StringBuilder();
        for (int i = 0; i < elements.Count; i++)
        {
            var (ai, value) = elements[i];
            if (value.Any(c => !IsEncodable(c))) return Gs1ElementStringProblem.Character;
            sb.Append(ai).Append(value);
            if (i < elements.Count - 1 && !FixedLengthPrefixes.Contains(ai[..2])) sb.Append(Fnc1);
        }
        reduced = sb.ToString();
        return reduced.Length > MaxReducedLength ? Gs1ElementStringProblem.TooLong : null;
    }
}
