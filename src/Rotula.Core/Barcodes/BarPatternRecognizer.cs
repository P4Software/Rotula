using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Barcodes;

/// <summary>
/// A group of vertical bars (imported <c>^GB</c> lines) that line up like a barcode: same top, same height, spaced on a module grid.
/// </summary>
public sealed class BarPatternMatch
{
    internal BarPatternMatch(IReadOnlyList<LineElement> bars, int firstIndex, int x, int y, int height, int totalWidth,
        int module, IReadOnlyList<int>? runs, string? value, bool faithful)
    {
        Bars = bars;
        FirstElementIndex = firstIndex;
        X = x;
        Y = y;
        HeightDots = height;
        TotalWidthDots = totalWidth;
        ModuleDots = module;
        Runs = runs;
        DecodedValue = value;
        IsFaithful = faithful;
    }

    /// <summary>The bar elements, left to right.</summary>
    public IReadOnlyList<LineElement> Bars { get; }

    /// <summary>Index in <see cref="LabelDocument.Elements"/> of the left-most bar (where a replacement barcode is inserted).</summary>
    public int FirstElementIndex { get; }

    /// <summary>Left edge of the first bar, in dots.</summary>
    public int X { get; }

    /// <summary>Top of the bars, in dots.</summary>
    public int Y { get; }

    /// <summary>Height of every bar, in dots.</summary>
    public int HeightDots { get; }

    /// <summary>Distance from the left edge of the first bar to the right edge of the last, in dots.</summary>
    public int TotalWidthDots { get; }

    /// <summary>Width of the narrowest element (one module) in dots, or 0 when the widths do not share a grid.</summary>
    public int ModuleDots { get; }

    /// <summary>Bar and space widths in modules (bar first), or null when the widths do not share a grid.</summary>
    public IReadOnlyList<int>? Runs { get; }

    /// <summary>The Code 128 value the bars spell, or null when the pattern is not a recognisable Code 128 symbol.</summary>
    public string? DecodedValue { get; }

    /// <summary>The symbology that was recognised, or null.</summary>
    public BarcodeSymbology? Symbology => DecodedValue is null ? null : BarcodeSymbology.Code128;

    /// <summary>
    /// True when a Code 128 element in automatic mode would print exactly the same bars. When false the value was
    /// read correctly but the original used a different subset sequence, so a conversion could shift the bars.
    /// </summary>
    public bool IsFaithful { get; }

    /// <summary>True when <see cref="BarPatternRecognizer.CreateBarcode"/> will produce an element.</summary>
    public bool CanConvert => DecodedValue is not null && IsFaithful && ModuleDots is >= 1 and <= 10;
}

/// <summary>
/// Finds barcodes that older labels drew with many <c>^GB</c> bars and offers to turn them into one real, editable barcode element.
/// Detection is exact: it never guesses, and conversion is only offered when the real barcode would print the same bars.
/// </summary>
public static class BarPatternRecognizer
{
    /// <summary>
    /// Finds bar groups in <paramref name="doc"/>.
    /// </summary>
    /// <param name="doc">The document (typically freshly imported).</param>
    /// <param name="minimumBars">Smallest number of bars that counts as a pattern (a Code 128 symbol has at least 28).</param>
    public static IReadOnlyList<BarPatternMatch> Detect(LabelDocument doc, int minimumBars = 10)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var result = new List<BarPatternMatch>();

        var candidates = new List<(LineElement Bar, int Index)>();
        for (int i = 0; i < doc.Elements.Count; i++)
        {
            if (doc.Elements[i] is LineElement { Direction: LineDirection.Vertical, Color: GraphicColor.Black, OriginKind: FieldOriginKind.Origin, ReverseField: false } l
                && (l.Rotation is null or FieldRotation.Normal))
                candidates.Add((l, i));
        }

        foreach (var group in candidates.GroupBy(c => (c.Bar.Y, c.Bar.LengthDots)))
        {
            var ordered = group.OrderBy(c => c.Bar.X).ToList();
            int minThickness = ordered.Min(c => c.Bar.ThicknessDots);
            var cluster = new List<(LineElement Bar, int Index)>();
            int clusterEnd = int.MinValue;

            void Flush()
            {
                if (cluster.Count >= minimumBars) result.Add(Analyse(cluster));
                cluster.Clear();
            }

            foreach (var c in ordered)
            {
                int gap = c.Bar.X - clusterEnd;
                // A Code 128 space is at most 4 modules, so a wider gap means the next bar belongs to something else.
                // Overlapping bars (negative gap) are not a barcode either.
                if (cluster.Count > 0 && (gap > 4 * minThickness || gap < 0)) Flush();
                cluster.Add(c);
                clusterEnd = c.Bar.X + c.Bar.ThicknessDots;
            }
            Flush();
        }

        return result.OrderBy(m => m.FirstElementIndex).ToList();
    }

    private static BarPatternMatch Analyse(List<(LineElement Bar, int Index)> cluster)
    {
        var bars = cluster.Select(c => c.Bar).ToList();
        int x = bars[0].X, y = bars[0].Y, height = bars[0].LengthDots;
        int end = bars[^1].X + bars[^1].ThicknessDots;
        int first = cluster.Min(c => c.Index);

        // Merge bars that touch: they print as one wide bar.
        var widths = new List<int>(); // alternating bar, space, bar ... in dots
        int cursor = x;
        foreach (var b in bars)
        {
            int gap = b.X - cursor;
            if (widths.Count == 0) widths.Add(b.ThicknessDots);
            else if (gap == 0) widths[^1] += b.ThicknessDots;
            else { widths.Add(gap); widths.Add(b.ThicknessDots); }
            cursor = b.X + b.ThicknessDots;
        }

        int module = widths.Aggregate(0, Gcd);
        if (module < 1) return new BarPatternMatch(bars, first, x, y, height, end - x, 0, null, null, false);

        var runs = widths.Select(w => w / module).ToArray();
        if (Code128.TryDecode(runs, out string value, out int[] symbols))
        {
            var again = Code128.Encode(value);
            bool faithful = again is not null && Code128.ToRuns(again).SequenceEqual(Code128.ToRuns(symbols));
            return new BarPatternMatch(bars, first, x, y, height, end - x, module, runs, value, faithful);
        }
        return new BarPatternMatch(bars, first, x, y, height, end - x, module, runs, null, false);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    /// <summary>
    /// Builds the barcode element that replaces <paramref name="match"/>, or null when the match is not a faithful, convertible
    /// Code 128 symbol. The document is not modified. The human-readable line is off because the original drew none.
    /// </summary>
    public static BarcodeElement? CreateBarcode(BarPatternMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!match.CanConvert) return null;
        return new BarcodeElement
        {
            Symbology = BarcodeSymbology.Code128,
            Data = match.DecodedValue!,
            X = match.X,
            Y = match.Y,
            Height = match.HeightDots,
            ModuleWidth = match.ModuleDots,
            HumanReadable = false,
            Code128Mode = Code128Mode.Automatic,
        };
    }

    /// <summary>
    /// Replaces the bars of <paramref name="match"/> in <paramref name="doc"/> by one Code 128 element, placed where the first bar was.
    /// </summary>
    /// <returns>The new element, or null (and no change) when the match cannot be converted.</returns>
    public static BarcodeElement? Convert(LabelDocument doc, BarPatternMatch match)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var barcode = CreateBarcode(match);
        if (barcode is null) return null;

        // Remove from the back so the stored first index stays valid for the insertion.
        int insertAt = Math.Min(match.FirstElementIndex, doc.Elements.Count);
        var remove = new HashSet<LabelElement>(match.Bars);
        int before = doc.Elements.Take(insertAt).Count(remove.Contains);
        doc.Elements.RemoveAll(remove.Contains);
        doc.Elements.Insert(Math.Max(0, insertAt - before), barcode);
        return barcode;
    }
}
