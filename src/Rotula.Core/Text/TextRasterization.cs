using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.Core.Text;

/// <summary>What did not fit when a text was laid out in its box. The flags can be combined.</summary>
[Flags]
public enum TextOverflow
{
    /// <summary>Everything fits.</summary>
    None = 0,

    /// <summary>The text needs more lines than the maximum number of lines allows; the extra lines are not drawn.</summary>
    Lines = 1,

    /// <summary>The text needs more height than the box has; the lines that do not fit are not drawn.</summary>
    Height = 2,

    /// <summary>A single word is wider than the box and sticks out of it.</summary>
    Width = 4,
}

/// <summary>
/// What a rasterizer needs to draw one text: the already substituted text, the font, the resolution and the box. All lengths are in printer
/// dots. The rotation of the field is not part of the request: the bitmap is always drawn upright and Core rotates it.
/// </summary>
/// <param name="Text">The final text (variables replaced). Line breaks start a new line only when <paramref name="BoxWidthDots"/> is above 0.</param>
/// <param name="Font">The Windows font; its size is in points and is converted with <paramref name="Dpi"/>.</param>
/// <param name="Dpi">Printer resolution in dots per inch (203, 300, 600...).</param>
/// <param name="BoxWidthDots">Width of the box the text wraps in and aligns within; 0 means one line as wide as the text.</param>
/// <param name="BoxHeightDots">Height of a fixed box (a <c>^TB</c> text box): lines that do not fit are not drawn. 0 means no height limit.</param>
/// <param name="MaxLines">Most lines drawn; 0 means no limit.</param>
/// <param name="LineSpacingDots">Extra space between lines in dots; may be negative.</param>
/// <param name="Alignment">Alignment inside the box width (ignored when the width is 0).</param>
/// <param name="HangingIndentDots">Indent of every line after the first, in dots.</param>
public sealed record TextRasterRequest(string Text, FontChoice Font, int Dpi, int BoxWidthDots = 0, int BoxHeightDots = 0, int MaxLines = 0,
    int LineSpacingDots = 0, TextAlignment Alignment = TextAlignment.Left, int HangingIndentDots = 0);

/// <summary>How a text was laid out: its size, number of lines and whether it fits. Produced without drawing, so a screen can show it live.</summary>
/// <param name="WidthDots">Width of the picture in dots (the box width, or the width of the text when there is no box).</param>
/// <param name="HeightDots">Height of the picture in dots (all lines drawn).</param>
/// <param name="LineCount">Number of lines drawn.</param>
/// <param name="BaselineDots">Distance from the top of the picture to the baseline of the first line.</param>
/// <param name="Overflow">What did not fit; <see cref="TextOverflow.None"/> when everything does.</param>
/// <param name="FontSubstituted">True when the computer has no font with the requested family name and another one was used instead.</param>
/// <param name="ActualFamily">The family really used (the requested one unless <paramref name="FontSubstituted"/>).</param>
public sealed record TextMeasurement(int WidthDots, int HeightDots, int LineCount, int BaselineDots, TextOverflow Overflow, bool FontSubstituted, string ActualFamily)
{
    /// <summary>True when the text does not fit its box.</summary>
    public bool Overflows => Overflow != TextOverflow.None;
}

/// <summary>A text drawn to a one-bit picture, with how it was laid out.</summary>
/// <param name="Bitmap">The upright picture, one bit per dot, 1 = black. Never rotated.</param>
/// <param name="Measurement">Size, lines, baseline and overflow of the picture.</param>
public sealed record TextRasterResult(MonochromeBitmap Bitmap, TextMeasurement Measurement);

/// <summary>
/// Draws the text of a Windows font to a one-bit picture. Core has no drawing code of its own: an implementation (the SkiaSharp one lives in
/// <c>Rotula.Rendering</c>) is given to the ZPL generator, which prints the picture in place of the text because a printer cannot have the font.
/// </summary>
/// <remarks>
/// Implementations must be thread safe (a data merge may rasterize records on a background thread), must not touch the clock, files or the
/// network, and must give the same picture for the same request on the same computer so that a label is reproducible. The preview should
/// draw with the same layout code, so what is shown is what prints.
/// </remarks>
public interface ITextRasterizer
{
    /// <summary>Lays the text out without drawing it: size, lines and overflow.</summary>
    TextMeasurement Measure(TextRasterRequest request);

    /// <summary>Draws the text to a one-bit picture at the printer's resolution. The text must not be empty.</summary>
    TextRasterResult Rasterize(TextRasterRequest request);

    /// <summary>The family names of the Windows fonts on this computer, sorted, for the font list of the user interface.</summary>
    IReadOnlyList<string> GetFontFamilies();
}
