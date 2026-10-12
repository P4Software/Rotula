using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Text;

/// <summary>
/// What the text fit decided for one text and one record: the font size and the number of lines to print with. Sizes the fit does not change are null.
/// </summary>
/// <param name="FontHeight">Printer font only: the character height in dots to write in <c>^A</c>; null when the size is not changed.</param>
/// <param name="FontWidth">Printer font only: the character width in dots to write in <c>^A</c> (0 stays 0, which means proportional); null when not changed.</param>
/// <param name="PointSize">Windows font only: the size in points to draw with; null when not changed.</param>
/// <param name="MaxLines">The line limit to write in <c>^FB</c> or to draw with.</param>
/// <param name="Overflow">What still does not fit at these values (an estimate for a printer font, exact for a Windows font).</param>
/// <param name="LimitReached">True when the minimum size (shrink) or the line cap (grow) was reached and the text still does not fit.</param>
public sealed record TextFitResult(int? FontHeight, int? FontWidth, double? PointSize, int MaxLines, TextOverflow Overflow, bool LimitReached);

/// <summary>
/// Works out the size a wrapped text needs so it fits its block (see <see cref="TextFitOptions"/>). It is the single place that decides: the ZPL
/// generator (and with it every record of a data merge), the picture printed for a Windows font, the canvas preview and the overflow warning of the
/// panel all call it, so what the canvas shows is what is written. It is deterministic: the same document, text and rasterizer give the same answer.
/// </summary>
/// <remarks>
/// It measures with the two measurers that already exist and adds none: <see cref="TextFitEstimator"/> for a printer font (the same estimate
/// the overflow warning uses) and <see cref="ITextRasterizer.Measure"/> for a Windows font (the same layout the preview and the printed picture use).
/// </remarks>
public static class TextFitResolver
{
    // Bounds the number of measurements per text. The panel asks on every change, so a wide range must not become hundreds of layouts.
    private const int MaxCandidates = 200;

    // A Windows font is tried in steps of half a point, which is as fine as the size box offers.
    private const double PointStep = 0.5;

    /// <summary>
    /// Decides the size or the line count of <paramref name="text"/> in the block of <paramref name="element"/>, or returns null when the fit does not
    /// apply (no block, mode None, no text, a value the printer itself decides such as a serial number, or a Windows font without a rasterizer).
    /// </summary>
    /// <param name="doc">The label (resolution and default font).</param>
    /// <param name="element">The text with its block.</param>
    /// <param name="text">The final text of the record (variables replaced).</param>
    /// <param name="rasterizer">Measures a Windows font; may be null when the text uses a printer font.</param>
    public static TextFitResult? Resolve(LabelDocument doc, TextElement element, string text, ITextRasterizer? rasterizer)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(text);
        if (element.Block is not { Fit: { Mode: not TextFitMode.None } fit } block || text.Length == 0) return null;
        // The printer, not Rotula, produces the content of these fields, so the real text length is unknown at generate time.
        if (element.Serial is not null || element.Clock is not null) return null;

        if (element.Font is { IsWindows: true } windows)
        {
            if (rasterizer is null || windows.Validate() is not null) return null;
            return fit.Mode == TextFitMode.Shrink ? ShrinkWindows(doc, element, block, fit, windows, text, rasterizer) : GrowWindows(doc, element, block, fit, windows, text, rasterizer);
        }
        return fit.Mode == TextFitMode.Shrink ? ShrinkPrinter(doc, element, block, fit, text) : GrowPrinter(doc, element, block, fit, text);
    }

    // ---- printer fonts: sizes are whole dots ----------------------------------------------------

    private static TextFitResult ShrinkPrinter(LabelDocument doc, TextElement t, TextBlockOptions block, TextFitOptions fit, string text)
    {
        var (minPt, maxPt) = fit.NormalizedRange();
        int dpi = (int)doc.Dpi;
        // ^A accepts 10 to 32000 dots; stay inside it so the generator never reports a size error caused by the fit.
        int max = Math.Clamp((int)Math.Round(FontChoice.PointsToDots(maxPt, dpi)), 10, 32000);
        int min = Math.Clamp((int)Math.Round(FontChoice.PointsToDots(minPt, dpi)), 10, max);
        int baseHeight = Math.Max(1, t.FontHeight ?? doc.DefaultFont?.Height ?? 30);
        int baseWidth = t.FontWidth ?? doc.DefaultFont?.Width ?? 30;
        int step = Math.Max(1, (int)Math.Ceiling((max - min) / (double)MaxCandidates));

        // A linear scan from the largest size: bitmap fonts scale in whole multiples of their cell and word wrap is not monotonic, so a
        // binary search could skip the largest size that fits. The scan stops at the first size that fits.
        for (int h = max; ; h = Math.Max(min, h - step))
        {
            var result = TryPrinter(doc, t, block, text, h, baseHeight, baseWidth);
            if (result.Overflow == TextOverflow.None) return result;
            if (h == min) return result with { LimitReached = true };
        }
    }

    private static TextFitResult TryPrinter(LabelDocument doc, TextElement t, TextBlockOptions block, string text, int height, int baseHeight, int baseWidth)
    {
        // An explicit width follows the height so the letters keep their shape (0 means "proportional" and stays 0). Shrinking only the height
        // would leave the estimate unchanged for a font with a fixed width.
        int width = baseWidth > 0 ? Math.Clamp((int)Math.Round(baseWidth * (double)height / baseHeight), 1, 32000) : 0;
        var overflow = TextFitEstimator.Estimate(t, text, doc.DefaultFont, height, width, block.MaxLines);
        return new TextFitResult(height, width, null, block.MaxLines, overflow, false);
    }

    private static TextFitResult GrowPrinter(LabelDocument doc, TextElement t, TextBlockOptions block, TextFitOptions fit, string text)
    {
        int needed = TextFitEstimator.CountLines(t, text, doc.DefaultFont, null, null);
        int cap = Math.Clamp(Math.Max(fit.MaxLinesCap, block.MaxLines), 1, 9999);
        int lines = Math.Clamp(needed, block.MaxLines, cap);
        var overflow = TextFitEstimator.Estimate(t, text, doc.DefaultFont, null, null, lines);
        return new TextFitResult(null, null, null, lines, overflow, needed > cap);
    }

    // ---- Windows fonts: sizes are points -------------------------------------------------------

    private static TextRasterRequest Request(LabelDocument doc, TextBlockOptions block, FontChoice font, string text, int maxLines) =>
        new(text, font, (int)doc.Dpi, block.WidthDots, 0, maxLines, block.LineSpacing, block.Alignment, block.HangingIndent);

    private static TextFitResult ShrinkWindows(LabelDocument doc, TextElement t, TextBlockOptions block, TextFitOptions fit, FontChoice font, string text, ITextRasterizer rasterizer)
    {
        var (min, max) = fit.NormalizedRange();
        double step = Math.Max(PointStep, Math.Ceiling((max - min) / MaxCandidates / PointStep) * PointStep);
        var trial = font.Clone();
        for (double size = max; ; size = Math.Max(min, size - step))
        {
            trial.PointSize = size;
            var m = rasterizer.Measure(Request(doc, block, trial, text, block.MaxLines));
            if (!m.Overflows) return new TextFitResult(null, null, size, block.MaxLines, TextOverflow.None, false);
            if (size <= min) return new TextFitResult(null, null, size, block.MaxLines, m.Overflow, true);
        }
    }

    private static TextFitResult GrowWindows(LabelDocument doc, TextElement t, TextBlockOptions block, TextFitOptions fit, FontChoice font, string text, ITextRasterizer rasterizer)
    {
        // With no line limit the measurement reports every line the text needs at the font's own size.
        int needed = rasterizer.Measure(Request(doc, block, font, text, 0)).LineCount;
        int cap = Math.Clamp(Math.Max(fit.MaxLinesCap, block.MaxLines), 1, 9999);
        int lines = Math.Clamp(needed, block.MaxLines, cap);
        var m = rasterizer.Measure(Request(doc, block, font, text, lines));
        return new TextFitResult(null, null, null, lines, m.Overflow, needed > cap);
    }
}
