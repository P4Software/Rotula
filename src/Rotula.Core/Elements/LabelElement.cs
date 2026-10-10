using Rotula.Core.Model;

namespace Rotula.Core.Elements;

/// <summary>Base class of everything placed on a label. Coordinates are dots relative to the label home (margins).</summary>
public abstract class LabelElement
{
    /// <summary>Stable id used to attach validation issues to elements.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Optional designer-facing name.</summary>
    public string? Name { get; set; }
    /// <summary>Horizontal position in dots.</summary>
    public int X { get; set; }
    /// <summary>Vertical position in dots.</summary>
    public int Y { get; set; }
    /// <summary>^FO (top-left) or ^FT (baseline) positioning.</summary>
    public FieldOriginKind OriginKind { get; set; } = FieldOriginKind.Origin;
    /// <summary>Explicit rotation, or null to inherit the document ^FW default.</summary>
    public FieldRotation? Rotation { get; set; }
    /// <summary>Print the field in reverse (^FR): inverts the pixels it overlaps.</summary>
    public bool ReverseField { get; set; }
}

/// <summary>Block (^FB) layout of a text element: wrapping, alignment and line limits.</summary>
public sealed class TextBlockOptions
{
    /// <summary>Block width in dots (1-9999).</summary>
    public int WidthDots { get; set; } = 400;
    /// <summary>Maximum number of lines; extra text is dropped by the printer (1-9999).</summary>
    public int MaxLines { get; set; } = 1;
    /// <summary>Extra space between lines in dots (may be negative).</summary>
    public int LineSpacing { get; set; }
    /// <summary>Paragraph alignment.</summary>
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    /// <summary>Hanging indent of the second and following lines, in dots.</summary>
    public int HangingIndent { get; set; }
}

/// <summary>Text, either a single line or a wrapped ^FB block. Content may contain <c>{{variable}}</c> placeholders.</summary>
public class TextElement : LabelElement
{
    /// <summary>The text (newlines only mean something inside a block).</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Font id, or null to inherit the ^CF default.</summary>
    public char? FontId { get; set; }
    /// <summary>Character height in dots, or null to inherit.</summary>
    public int? FontHeight { get; set; }
    /// <summary>Character width in dots (0 = proportional), or null to inherit.</summary>
    public int? FontWidth { get; set; }
    /// <summary>Block layout, or null for a single line.</summary>
    public TextBlockOptions? Block { get; set; }

    /// <summary>The raw template text before variable substitution.</summary>
    public virtual string GetTemplate() => Text;
}

/// <summary>A text field bound to a named variable; its content is whatever the merge data supplies.</summary>
public sealed class DataFieldElement : TextElement
{
    /// <summary>Name of the bound <see cref="LabelVariable"/>.</summary>
    public string VariableName { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string GetTemplate() => "{{" + VariableName + "}}";
}

/// <summary>A barcode of any supported symbology. Which options apply depends on <see cref="Symbology"/>.</summary>
public sealed class BarcodeElement : LabelElement
{
    /// <summary>The symbology.</summary>
    public BarcodeSymbology Symbology { get; set; } = BarcodeSymbology.Code128;
    /// <summary>The data to encode; may contain <c>{{variable}}</c> placeholders.</summary>
    public string Data { get; set; } = string.Empty;
    /// <summary>Bar height in dots (linear codes) or row height in dots (PDF417).</summary>
    public int Height { get; set; } = 100;
    /// <summary>Narrow bar / module width in dots (^BY, 1-10); linear codes and PDF417.</summary>
    public int ModuleWidth { get; set; } = 2;
    /// <summary>Wide-to-narrow bar ratio (^BY, 2.0-3.0); linear codes.</summary>
    public double WideBarRatio { get; set; } = 3.0;
    /// <summary>Print the human-readable line (linear codes).</summary>
    public bool HumanReadable { get; set; } = true;
    /// <summary>Where the human-readable line goes.</summary>
    public HumanReadablePosition HumanReadablePosition { get; set; } = HumanReadablePosition.Below;
    /// <summary>
    /// Check digit option: Code 39 (mod 43), Code 93 and Interleaved 2 of 5 (mod 10) ask the printer to
    /// append one; for UPC-A it prints the check digit. EAN-13 always computes its own.
    /// </summary>
    public bool CheckDigit { get; set; }
    /// <summary>Code 128 mode.</summary>
    public Code128Mode Code128Mode { get; set; } = Code128Mode.Automatic;
    /// <summary>QR error correction level.</summary>
    public QrErrorCorrection QrErrorCorrection { get; set; } = QrErrorCorrection.Quartile;
    /// <summary>QR model (1 or 2).</summary>
    public int QrModel { get; set; } = 2;
    /// <summary>Module size in dots: QR magnification (1-10) or Data Matrix element height (1-10).</summary>
    public int ModuleSize { get; set; } = 4;
    /// <summary>Data Matrix / PDF417 columns (0 = automatic).</summary>
    public int Columns { get; set; }
    /// <summary>Data Matrix / PDF417 rows (0 = automatic).</summary>
    public int Rows { get; set; }
    /// <summary>PDF417 security (error correction) level, 1-8.</summary>
    public int Pdf417SecurityLevel { get; set; } = 2;
    /// <summary>PDF417 truncated (compact) variant.</summary>
    public bool Pdf417Truncate { get; set; }
}

/// <summary>A rectangle (^GB).</summary>
public sealed class BoxElement : LabelElement
{
    /// <summary>Width in dots.</summary>
    public int WidthDots { get; set; } = 100;
    /// <summary>Height in dots.</summary>
    public int HeightDots { get; set; } = 100;
    /// <summary>Border thickness in dots.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Line colour.</summary>
    public GraphicColor Color { get; set; } = GraphicColor.Black;
    /// <summary>Corner rounding, 0 (square) to 8.</summary>
    public int CornerRounding { get; set; }
}

/// <summary>A horizontal or vertical line (^GB with one dimension equal to the thickness).</summary>
public sealed class LineElement : LabelElement
{
    /// <summary>Length in dots.</summary>
    public int LengthDots { get; set; } = 100;
    /// <summary>Thickness in dots.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Direction.</summary>
    public LineDirection Direction { get; set; } = LineDirection.Horizontal;
    /// <summary>Line colour.</summary>
    public GraphicColor Color { get; set; } = GraphicColor.Black;
}

/// <summary>A circle (^GC).</summary>
public sealed class CircleElement : LabelElement
{
    /// <summary>Outer diameter in dots (3-4095).</summary>
    public int DiameterDots { get; set; } = 100;
    /// <summary>Border thickness in dots.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Line colour.</summary>
    public GraphicColor Color { get; set; } = GraphicColor.Black;
}

/// <summary>A monochrome image (^GF) produced from a grey source by scaling and dithering.</summary>
public sealed class ImageElement : LabelElement
{
    /// <summary>Source luminance bitmap (the caller flattens alpha onto white). Null means no image yet.</summary>
    public Imaging.GrayscaleBitmap? Source { get; set; }
    /// <summary>Target width in dots; 0 keeps the source size (or the aspect ratio when only the height is set).</summary>
    public int WidthDots { get; set; }
    /// <summary>Target height in dots; 0 keeps the source size (or the aspect ratio when only the width is set).</summary>
    public int HeightDots { get; set; }
    /// <summary>Reduction method.</summary>
    public ImageDitherMode Dither { get; set; } = ImageDitherMode.FloydSteinberg;
    /// <summary>Luminance below this (0-255) prints black; for Floyd-Steinberg it is the quantisation point.</summary>
    public int Threshold { get; set; } = 128;
    /// <summary>Swap black and white before reduction.</summary>
    public bool Invert { get; set; }
}
