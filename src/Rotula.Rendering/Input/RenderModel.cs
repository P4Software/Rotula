namespace Rotula.Rendering.Input;

// ---------------------------------------------------------------------------------------------
// Render-input layer.
//
// The renderer deliberately does NOT depend on the Rotula.Core document model. It consumes this
// small, immutable, already-resolved description of a label (all values in printer dots, all
// variable data already substituted). A thin adapter in the app/Core layer maps the real document
// model onto these records. That keeps the renderer testable on its own and lets the document
// model evolve without touching drawing code.
// ---------------------------------------------------------------------------------------------

/// <summary>Quarter-turn rotation of an element, clockwise, around its top-left origin.</summary>
public enum RenderRotation
{
    /// <summary>No rotation.</summary>
    None = 0,
    /// <summary>90 degrees clockwise.</summary>
    Rotate90 = 90,
    /// <summary>180 degrees.</summary>
    Rotate180 = 180,
    /// <summary>270 degrees clockwise.</summary>
    Rotate270 = 270,
}

/// <summary>Horizontal text alignment inside a text block.</summary>
public enum RenderTextAlignment
{
    /// <summary>Align to the left edge.</summary>
    Left,
    /// <summary>Center inside the block width.</summary>
    Center,
    /// <summary>Align to the right edge.</summary>
    Right,
    /// <summary>Spread words so every line except the last fills the block width.</summary>
    Justified,
}

/// <summary>Which bundled stand-in typeface a text element uses.</summary>
public enum RenderFontFamily
{
    /// <summary>Proportional sans-serif (Liberation Sans).</summary>
    Sans,
    /// <summary>Monospaced (Liberation Mono).</summary>
    Mono,
}

/// <summary>Everything the renderer needs to draw one label. All lengths are in printer dots.</summary>
/// <param name="WidthDots">Label width in dots.</param>
/// <param name="HeightDots">Label height in dots.</param>
/// <param name="Dpi">Printer resolution (dots per inch), for example 203, 300 or 600.</param>
/// <param name="Elements">Elements in paint order (first is painted first, so last is on top).</param>
public sealed record RenderLabel(int WidthDots, int HeightDots, int Dpi, IReadOnlyList<RenderElement> Elements)
{
    /// <summary>Label width in millimetres.</summary>
    public double WidthMm => WidthDots * 25.4 / Dpi;

    /// <summary>Label height in millimetres.</summary>
    public double HeightMm => HeightDots * 25.4 / Dpi;

    /// <summary>Builds a label from a physical size in millimetres, rounding to whole dots.</summary>
    public static RenderLabel FromMillimetres(double widthMm, double heightMm, int dpi, IReadOnlyList<RenderElement> elements) =>
        new((int)Math.Round(widthMm / 25.4 * dpi), (int)Math.Round(heightMm / 25.4 * dpi), dpi, elements);
}

/// <summary>Base type of every drawable element. Position is the element's top-left corner in dots.</summary>
public abstract record RenderElement
{
    /// <summary>Left edge in dots, measured from the label's left edge.</summary>
    public double X { get; init; }

    /// <summary>Top edge in dots, measured from the label's top edge.</summary>
    public double Y { get; init; }

    /// <summary>Rotation around (X, Y). The element's own box is rotated, not the label.</summary>
    public RenderRotation Rotation { get; init; } = RenderRotation.None;

    /// <summary>
    /// True when (X, Y) is the typesetting origin (ZPL ^FT): the text baseline, or the bottom-left corner of
    /// graphics and barcodes. False (^FO) means (X, Y) is the top-left corner of the element's bounding box.
    /// </summary>
    public bool Typeset { get; init; }

    /// <summary>
    /// Reverse print (ZPL ^FR): the element inverts whatever is already under it instead of painting black.
    /// </summary>
    public bool Reverse { get; init; }
}

/// <summary>A block of text.</summary>
public sealed record RenderText : RenderElement
{
    /// <summary>The already-resolved text (variables substituted). May contain line breaks.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Character height in dots (the em size). Preview text is approximate; see <see cref="LabelRenderer"/>.</summary>
    public double FontHeightDots { get; init; } = 30;

    /// <summary>Which stand-in typeface to use.</summary>
    public RenderFontFamily Font { get; init; } = RenderFontFamily.Sans;

    /// <summary>Use the bold face.</summary>
    public bool Bold { get; init; }

    /// <summary>Block width in dots used for wrapping and alignment; 0 means no wrapping.</summary>
    public double WidthDots { get; init; }

    /// <summary>Alignment inside <see cref="WidthDots"/> (ignored when width is 0).</summary>
    public RenderTextAlignment Alignment { get; init; } = RenderTextAlignment.Left;

    /// <summary>Maximum number of lines drawn; 0 means unlimited.</summary>
    public int MaxLines { get; init; }

    /// <summary>Extra space between lines in dots (may be negative), like ZPL's ^FB line spacing.</summary>
    public double LineSpacingDots { get; init; }

    /// <summary>
    /// Height of a fixed-size text box in dots (ZPL ^TB): lines that do not fit are not drawn. 0 means no height limit.
    /// </summary>
    public double HeightDots { get; init; }
}

/// <summary>
/// A hatched, captioned box standing in for something the preview cannot draw (an object stored on the printer,
/// or imported ZPL the designer does not model). It is a preview aid only and is never printed.
/// </summary>
public sealed record RenderPlaceholder : RenderElement
{
    /// <summary>Box width in dots.</summary>
    public double WidthDots { get; init; } = 120;

    /// <summary>Box height in dots.</summary>
    public double HeightDots { get; init; } = 80;

    /// <summary>Short caption drawn inside the box (for example the recalled object name). May be empty.</summary>
    public string Caption { get; init; } = string.Empty;
}

/// <summary>A rectangle outline or filled box.</summary>
public sealed record RenderBox : RenderElement
{
    /// <summary>Box width in dots.</summary>
    public double WidthDots { get; init; }

    /// <summary>Box height in dots.</summary>
    public double HeightDots { get; init; }

    /// <summary>Border thickness in dots; a value at or above half the smaller side makes a solid box.</summary>
    public double ThicknessDots { get; init; } = 2;

    /// <summary>Corner radius in dots (0 for square corners).</summary>
    public double CornerRadiusDots { get; init; }

    /// <summary>Paint the box in white instead of black (useful on top of black areas).</summary>
    public bool White { get; init; }
}

/// <summary>A circle outline (ZPL ^GC) whose bounding box starts at (X, Y).</summary>
public sealed record RenderCircle : RenderElement
{
    /// <summary>Outer diameter in dots.</summary>
    public double DiameterDots { get; init; }

    /// <summary>Border thickness in dots; at or above half the diameter makes a solid disc.</summary>
    public double ThicknessDots { get; init; } = 2;

    /// <summary>Paint in white instead of black.</summary>
    public bool White { get; init; }
}

/// <summary>An ellipse outline (ZPL ^GE) whose bounding box starts at (X, Y).</summary>
public sealed record RenderEllipse : RenderElement
{
    /// <summary>Outer width in dots.</summary>
    public double WidthDots { get; init; }

    /// <summary>Outer height in dots.</summary>
    public double HeightDots { get; init; }

    /// <summary>Border thickness in dots; at or above half the smaller side makes a solid ellipse.</summary>
    public double ThicknessDots { get; init; } = 2;

    /// <summary>Paint in white instead of black.</summary>
    public bool White { get; init; }
}

/// <summary>
/// A diagonal bar (ZPL ^GD) across a box of the given size. The thickness is measured horizontally, the way the printer
/// draws it, so a thick diagonal looks like a slanted bar rather than a rotated one.
/// </summary>
public sealed record RenderDiagonal : RenderElement
{
    /// <summary>Box width in dots.</summary>
    public double WidthDots { get; init; }

    /// <summary>Box height in dots.</summary>
    public double HeightDots { get; init; }

    /// <summary>Horizontal thickness of the bar in dots.</summary>
    public double ThicknessDots { get; init; } = 2;

    /// <summary>True when the line rises to the right (bottom-left to top-right), false when it falls to the right.</summary>
    public bool LeansRight { get; init; } = true;

    /// <summary>Paint in white instead of black.</summary>
    public bool White { get; init; }
}

/// <summary>A straight line from (X, Y) to (X + DeltaX, Y + DeltaY). Used for diagonals; axis-aligned lines are boxes.</summary>
public sealed record RenderLine : RenderElement
{
    /// <summary>Horizontal extent in dots (may be negative).</summary>
    public double DeltaX { get; init; }

    /// <summary>Vertical extent in dots (may be negative).</summary>
    public double DeltaY { get; init; }

    /// <summary>Line thickness in dots.</summary>
    public double ThicknessDots { get; init; } = 2;
}

/// <summary>A barcode or 2D symbol.</summary>
public sealed record RenderBarcode : RenderElement
{
    /// <summary>Symbology to draw.</summary>
    public Core.Model.BarcodeSymbology Symbology { get; init; } = Core.Model.BarcodeSymbology.Code128;

    /// <summary>The data to encode (already resolved).</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>
    /// Width of the narrowest bar (1D) or of one module (2D) in whole dots, like ZPL's ^BY. Minimum 1.
    /// </summary>
    public int ModuleDots { get; init; } = 2;

    /// <summary>Bar height in dots for 1D symbols and the height of one PDF417 row; ignored for QR and Data Matrix.</summary>
    public int HeightDots { get; init; } = 80;

    /// <summary>Print the human-readable text next to a 1D symbol.</summary>
    public bool ShowText { get; init; } = true;

    /// <summary>Put the human-readable text above the bars instead of below.</summary>
    public bool TextAbove { get; init; }

    /// <summary>Printer-side optional check digit (Code 39, Interleaved 2 of 5).</summary>
    public bool CheckDigit { get; init; }

    /// <summary>QR error correction level.</summary>
    public Core.Model.QrErrorCorrection QrErrorCorrection { get; init; } = Core.Model.QrErrorCorrection.Quartile;

    /// <summary>PDF417 security level (0-8).</summary>
    public int Pdf417SecurityLevel { get; init; } = 2;

    /// <summary>PDF417 truncated variant.</summary>
    public bool Pdf417Truncate { get; init; }

    /// <summary>Code 11: number of check digits (1 or 2).</summary>
    public int Code11CheckDigits { get; init; } = 2;

    /// <summary>MSI check digit scheme.</summary>
    public Core.Model.MsiCheckDigitMode MsiCheckDigit { get; init; } = Core.Model.MsiCheckDigitMode.Mod10;

    /// <summary>MSI: write the check digits in the text line.</summary>
    public bool MsiCheckDigitInText { get; init; }

    /// <summary>Codabar start character (A-D).</summary>
    public char CodabarStart { get; init; } = 'A';

    /// <summary>Codabar stop character (A-D).</summary>
    public char CodabarStop { get; init; } = 'A';

    /// <summary>GS1 DataBar variant.</summary>
    public Core.Model.DataBarType DataBarType { get; init; } = Core.Model.DataBarType.Omnidirectional;

    /// <summary>GS1 DataBar Expanded: segments per row.</summary>
    public int DataBarSegmentsPerRow { get; init; } = 22;

    /// <summary>GS1 DataBar: separator height between stacked rows in modules.</summary>
    public int DataBarSeparatorHeight { get; init; } = 1;

    /// <summary>Aztec error control and symbol size/type.</summary>
    public int AztecErrorControl { get; init; }

    /// <summary>MaxiCode mode, 2 to 6.</summary>
    public int MaxiCodeMode { get; init; } = 2;

    /// <summary>MaxiCode structured append: number of this symbol.</summary>
    public int MaxiCodeSymbolNumber { get; init; } = 1;

    /// <summary>MaxiCode structured append: total number of symbols.</summary>
    public int MaxiCodeSymbolTotal { get; init; } = 1;
}

/// <summary>A bitmap image, already decoded to an encoded PNG/JPEG byte array.</summary>
public sealed record RenderImage : RenderElement
{
    /// <summary>Encoded image bytes (PNG, JPEG, BMP ...). Drawn as given; the adapter supplies the already dithered dots.</summary>
    public byte[] EncodedBytes { get; init; } = [];

    /// <summary>Target width in dots.</summary>
    public double WidthDots { get; init; }

    /// <summary>Target height in dots.</summary>
    public double HeightDots { get; init; }
}
