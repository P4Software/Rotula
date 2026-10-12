using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Text;

/// <summary>
/// Estimates whether a text printed with a printer font fits its block. Core cannot know the printer's real letter widths (the fonts are in the
/// printer and several are scalable), so this is an estimate from nominal character sizes: good enough for a warning, never a promise. A text in a
/// Windows font does not use it, because that picture is measured exactly by the rasterizer.
/// </summary>
public static class TextFitEstimator
{
    // Nominal character cell of the built-in bitmap fonts: height and width in dots at the font's own size, from the ZPL II guide's font table.
    // The cell includes no gap; one dot of gap is assumed between characters.
    private static readonly Dictionary<char, (int Height, int Width)> Cells = new()
    {
        ['A'] = (9, 5), ['B'] = (11, 7), ['C'] = (18, 10), ['D'] = (18, 10), ['E'] = (28, 15), ['F'] = (26, 13), ['G'] = (60, 40), ['H'] = (21, 13),
    };

    /// <summary>Average width of a character of the scalable font as a share of its height. A typical value for a proportional sans-serif face.</summary>
    private const double ScalableWidthShare = 0.55;

    /// <summary>
    /// Estimated width in dots of one character of the text's printer font, from the element's own sizes or the label default.
    /// </summary>
    public static double CharacterWidth(TextElement element, FontSpec? labelDefault) => CharacterWidth(element, labelDefault, null, null);

    /// <summary>
    /// Same as <see cref="CharacterWidth(TextElement, FontSpec?)"/> for a candidate size: <paramref name="fontHeight"/> and <paramref name="fontWidth"/>
    /// (dots) replace the element's own sizes when given. The text fit uses it to try sizes without copying the element.
    /// </summary>
    public static double CharacterWidth(TextElement element, FontSpec? labelDefault, int? fontHeight, int? fontWidth)
    {
        char id = element.FontId ?? labelDefault?.FontId ?? '0';
        int height = fontHeight ?? element.FontHeight ?? labelDefault?.Height ?? 30;
        int width = fontWidth ?? element.FontWidth ?? labelDefault?.Width ?? 30;
        if (Cells.TryGetValue(char.ToUpperInvariant(id), out var cell))
        {
            // The printer scales a bitmap font by whole multiples of its cell.
            int multiple = Math.Max(1, (int)Math.Round((double)height / cell.Height));
            return (cell.Width + 1) * multiple;
        }
        // Scalable font: a width of 0 means "proportional to the height".
        return (width > 0 ? width : height) * ScalableWidthShare;
    }

    /// <summary>
    /// Estimates what does not fit when <paramref name="text"/> is printed in the block (<c>^FB</c>) or text box (<c>^TB</c>) of
    /// <paramref name="element"/>. Returns <see cref="TextOverflow.None"/> for text without a block, which has nothing to overflow.
    /// </summary>
    /// <param name="element">The text with its block or box.</param>
    /// <param name="text">The final text (variables replaced).</param>
    /// <param name="labelDefault">The label's default font, for sizes the element does not set.</param>
    public static TextOverflow Estimate(TextElement element, string text, FontSpec? labelDefault) =>
        Estimate(element, text, labelDefault, null, null, null);

    /// <summary>
    /// Same estimate for a candidate size: the font height and width (dots) and the line limit replace the element's own values when given. The text
    /// fit (<see cref="TextFitResolver"/>) tries sizes this way, so it measures with exactly the estimate the overflow warning uses.
    /// </summary>
    public static TextOverflow Estimate(TextElement element, string text, FontSpec? labelDefault, int? fontHeight, int? fontWidth, int? maxLines)
    {
        ArgumentNullException.ThrowIfNull(element);
        var flags = LayOut(element, text, labelDefault, fontHeight, fontWidth, out int lines);
        if (lines == 0) return TextOverflow.None;
        int height = fontHeight ?? element.FontHeight ?? labelDefault?.Height ?? 30;
        if (element.Block is { } block && lines > (maxLines ?? block.MaxLines)) flags |= TextOverflow.Lines;
        if (element.TextBox is { } box)
        {
            double lineHeight = height;
            if (lines * lineHeight > box.HeightDots) flags |= TextOverflow.Height;
        }
        return flags;
    }

    /// <summary>
    /// Estimates how many lines <paramref name="text"/> needs in the block of <paramref name="element"/> at a candidate size, whatever the block's
    /// own line limit. Returns 0 for a text without a block or without content.
    /// </summary>
    public static int CountLines(TextElement element, string text, FontSpec? labelDefault, int? fontHeight, int? fontWidth)
    {
        ArgumentNullException.ThrowIfNull(element);
        LayOut(element, text, labelDefault, fontHeight, fontWidth, out int lines);
        return lines;
    }

    // The one wrapping routine behind both public questions, so "does it fit" and "how many lines" can never disagree.
    private static TextOverflow LayOut(TextElement element, string text, FontSpec? labelDefault, int? fontHeight, int? fontWidth, out int lines)
    {
        lines = 0;
        int boxWidth = element.Block?.WidthDots ?? element.TextBox?.WidthDots ?? 0;
        if (boxWidth <= 0 || text.Length == 0) return TextOverflow.None;

        double charWidth = Math.Max(1, CharacterWidth(element, labelDefault, fontHeight, fontWidth));
        int perLine = Math.Max(1, (int)Math.Floor(boxWidth / charWidth));

        var flags = TextOverflow.None;
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\\&", "\n").Split('\n'))
        {
            int used = 0;
            lines++;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length > perLine) flags |= TextOverflow.Width;
                // A word that does not fit on the rest of the line starts the next one (the space before it counts).
                int need = used == 0 ? word.Length : used + 1 + word.Length;
                if (need <= perLine) { used = need; continue; }
                if (used > 0) lines++;
                // A word longer than a line is broken by the printer; count the extra lines it takes.
                lines += Math.Max(0, (word.Length - 1) / perLine);
                used = word.Length % perLine == 0 ? perLine : word.Length % perLine;
            }
        }
        return flags;
    }
}
