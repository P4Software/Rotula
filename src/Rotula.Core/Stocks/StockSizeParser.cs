using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rotula.Core.Stocks;

/// <summary>Unit written next to a number in a size text.</summary>
public enum StockSizeUnit
{
    /// <summary>No unit was written.</summary>
    Unknown,
    /// <summary>Millimetres (also centimetres, converted).</summary>
    Millimeters,
    /// <summary>Inches (<c>in</c>, <c>inch</c>, <c>"</c>).</summary>
    Inches,
}

/// <summary>
/// One <c>A x B</c> size found in a text. The values are kept as written; <see cref="Candidates"/> gives the
/// readings in millimetres. A size without any unit has no reliable reading (see <see cref="UnitWritten"/>).
/// </summary>
public sealed class ParsedSize
{
    internal ParsedSize(int index, int length, double first, double second, StockSizeUnit firstUnit, StockSizeUnit secondUnit, double firstScale, double secondScale)
    {
        Index = index;
        Length = length;
        First = first;
        Second = second;
        FirstUnit = firstUnit;
        SecondUnit = secondUnit;
        FirstScale = firstScale;
        SecondScale = secondScale;
    }

    /// <summary>Start of the match in the searched text.</summary>
    public int Index { get; }
    /// <summary>Length of the match.</summary>
    public int Length { get; }
    /// <summary>First number as written.</summary>
    public double First { get; }
    /// <summary>Second number as written.</summary>
    public double Second { get; }
    /// <summary>Unit of the first number (inherited from the second when only that one has a unit).</summary>
    public StockSizeUnit FirstUnit { get; }
    /// <summary>Unit of the second number (inherited from the first when only that one has a unit).</summary>
    public StockSizeUnit SecondUnit { get; }
    internal double FirstScale { get; }
    internal double SecondScale { get; }

    /// <summary>True when the text states a unit (so the reading is not a guess).</summary>
    public bool UnitWritten => FirstUnit != StockSizeUnit.Unknown && SecondUnit != StockSizeUnit.Unknown;

    /// <summary>
    /// Readings of the size in millimetres as (width, height, unit). With a written unit there is exactly one; without a
    /// unit there is one per plausible unit (inches when both numbers are at most 24, millimetres when both are at least 5).
    /// </summary>
    public IReadOnlyList<(double WidthMm, double HeightMm, StockSizeUnit Unit)> Candidates
    {
        get
        {
            var list = new List<(double, double, StockSizeUnit)>();
            if (UnitWritten)
            {
                list.Add((ToMm(First * FirstScale, FirstUnit), ToMm(Second * SecondScale, SecondUnit), FirstUnit == SecondUnit ? FirstUnit : StockSizeUnit.Unknown));
                return list;
            }
            double max = Math.Max(First, Second), min = Math.Min(First, Second);
            if (max <= 24 && min > 0) list.Add((First * 25.4, Second * 25.4, StockSizeUnit.Inches));
            if (min >= 5) list.Add((First, Second, StockSizeUnit.Millimeters));
            return list;
        }
    }

    internal static double ToMm(double value, StockSizeUnit unit) => unit == StockSizeUnit.Inches ? value * 25.4 : value;
}

/// <summary>
/// Finds sizes written in many spellings: <c>4x6</c>, <c>4 x 6 in</c>, <c>4"x6"</c>, <c>102x152</c>, <c>100x150mm</c>,
/// <c>10 cm x 15 cm</c>, <c>4in x 6in</c>, <c>2,25 x 1,25 in</c>, <c>4 por 6 pulgadas</c>. A roll length such as
/// <c>4 in x 30.5 m</c> is not a label size and is skipped.
/// </summary>
public static class StockSizeParser
{
    private const string Num = @"\d+(?:[.,]\d+)?";
    private const string Unit = @"mm|cm|inches|inch|pulgadas|pulgada|pulg|in|""|”|″|''|’’";

    private static readonly Regex Pattern = new(
        $@"(?<![\d.,])(?<a>{Num})\s*(?<ua>{Unit})?\s*(?:x|×|\*|by|por)\s*(?<b>{Num})(?![.,]?\d)\s*(?<ub>{Unit})?(?![a-z\d])(?!\s*(?:m|meters?|metres?|ft|feet|yd|mil)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        // A guard against runaway matching, not a speed target: the very first match also pays for compiling the pattern, and on a busy
        // machine (the first run right after a build) that alone exceeded 250 ms and made a valid size look unparsable.
        TimeSpan.FromSeconds(2));

    /// <summary>Returns every size found in <paramref name="text"/>, left to right. Never throws.</summary>
    public static IReadOnlyList<ParsedSize> FindAll(string? text)
    {
        var result = new List<ParsedSize>();
        if (string.IsNullOrEmpty(text) || text.Length > 2000) return result;
        try
        {
            foreach (Match m in Pattern.Matches(text))
            {
                if (!TryNumber(m.Groups["a"].Value, out double a) || !TryNumber(m.Groups["b"].Value, out double b)) continue;
                var (ua, sa) = ParseUnit(m.Groups["ua"].Value);
                var (ub, sb) = ParseUnit(m.Groups["ub"].Value);
                // "4 x 6 in" and "4in x 6" both apply the one written unit to both numbers.
                if (ua == StockSizeUnit.Unknown) { ua = ub; sa = sb; }
                if (ub == StockSizeUnit.Unknown) { ub = ua; sb = sa; }
                result.Add(new ParsedSize(m.Index, m.Length, a, b, ua, ub, sa, sb));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological text simply yields no size.
        }
        return result;
    }

    /// <summary>Returns the first size in <paramref name="text"/>, or false.</summary>
    public static bool TryParse(string? text, out ParsedSize size)
    {
        var all = FindAll(text);
        size = all.Count > 0 ? all[0] : null!;
        return all.Count > 0;
    }

    /// <summary>The text with the given sizes cut out (used to leave only the words of a query).</summary>
    internal static string Remove(string text, IEnumerable<ParsedSize> sizes)
    {
        var sb = new StringBuilder(text);
        foreach (var s in sizes.OrderByDescending(s => s.Index)) sb.Remove(s.Index, s.Length).Insert(s.Index, ' ');
        return sb.ToString();
    }

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value > 0;

    private static (StockSizeUnit Unit, double Scale) ParseUnit(string s)
    {
        if (s.Length == 0) return (StockSizeUnit.Unknown, 1);
        return s.ToLowerInvariant() switch
        {
            "mm" => (StockSizeUnit.Millimeters, 1),
            "cm" => (StockSizeUnit.Millimeters, 10),
            _ => (StockSizeUnit.Inches, 1),
        };
    }
}
