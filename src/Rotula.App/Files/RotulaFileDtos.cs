using System.Text.Json.Serialization;

namespace Rotula.App.Files;

// Data-transfer objects of the .rotula file. They are deliberately separate from the Core model:
// Core collections are get-only, ZplEncoding has a private constructor and images are raw pixel
// arrays, none of which System.Text.Json can round-trip directly. The DTO layer also gives the
// file format a stable shape that does not change when Core refactors its classes.

/// <summary>Root object of a .rotula file.</summary>
public sealed class RotulaFileDto
{
    /// <summary>Schema version of the file. Bumped when a change cannot be read by older versions.</summary>
    [JsonPropertyOrder(0)]
    public int FormatVersion { get; set; }

    /// <summary>Free-text name of the program that wrote the file (informational only).</summary>
    [JsonPropertyOrder(1)]
    public string? Generator { get; set; }

    /// <summary>Label-level settings.</summary>
    [JsonPropertyOrder(2)]
    public LabelDto Label { get; set; } = new();

    /// <summary>Elements in z-order (first = back).</summary>
    [JsonPropertyOrder(3)]
    public List<ElementDto> Elements { get; set; } = new();

    /// <summary>Data-merge settings (format version 2 and later). Absent in version 1 files.</summary>
    [JsonPropertyOrder(4)]
    public MergeDto? Merge { get; set; }
}

/// <summary>A length with its unit.</summary>
public sealed class MeasureDto
{
    /// <summary>Numeric value.</summary>
    public double Value { get; set; }

    /// <summary>Unit name: <c>Dots</c>, <c>Millimeters</c> or <c>Inches</c>.</summary>
    public string Unit { get; set; } = "Millimeters";
}

/// <summary>Label margins.</summary>
public sealed class MarginsDto
{
    /// <summary>Left margin.</summary>
    public MeasureDto Left { get; set; } = new() { Unit = "Dots" };
    /// <summary>Top margin.</summary>
    public MeasureDto Top { get; set; } = new() { Unit = "Dots" };
    /// <summary>Right margin.</summary>
    public MeasureDto Right { get; set; } = new() { Unit = "Dots" };
    /// <summary>Bottom margin.</summary>
    public MeasureDto Bottom { get; set; } = new() { Unit = "Dots" };
}

/// <summary>Default font (^CF).</summary>
public sealed class FontDto
{
    /// <summary>ZPL font id.</summary>
    public string FontId { get; set; } = "0";
    /// <summary>Height in dots.</summary>
    public int Height { get; set; } = 30;
    /// <summary>Width in dots.</summary>
    public int Width { get; set; } = 30;
}

/// <summary>Character encoding: the <c>^CI</c> number and, for legacy encodings, the .NET code page.</summary>
public sealed class EncodingDto
{
    /// <summary>The ^CI number (28 = UTF-8).</summary>
    public int CiCode { get; set; } = 28;
    /// <summary>.NET code page, or null for UTF-8.</summary>
    public int? CodePage { get; set; }
}

/// <summary>Print job options.</summary>
public sealed class PrintOptionsDto
{
    /// <summary>Quantity.</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>Pause every N labels.</summary>
    public int PauseEvery { get; set; }
    /// <summary>Serial number replicates.</summary>
    public int Replicates { get; set; } = 1;
    /// <summary>Override pause count.</summary>
    public bool OverridePause { get; set; }
}

/// <summary>A declared variable.</summary>
public sealed class VariableDto
{
    /// <summary>Variable name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Sample value.</summary>
    public string SampleValue { get; set; } = string.Empty;
}

/// <summary>Label-level settings.</summary>
public sealed class LabelDto
{
    /// <summary>Width.</summary>
    public MeasureDto Width { get; set; } = new() { Value = 100 };
    /// <summary>Height.</summary>
    public MeasureDto Height { get; set; } = new() { Value = 150 };
    /// <summary>Printer resolution in DPI (203, 300 or 600).</summary>
    public int Dpi { get; set; } = 203;
    /// <summary>Whole-label orientation name.</summary>
    public string Orientation { get; set; } = "Normal";
    /// <summary>Margins.</summary>
    public MarginsDto Margins { get; set; } = new();
    /// <summary>Media type name.</summary>
    public string MediaType { get; set; } = "ThermalTransfer";
    /// <summary>Media tracking name.</summary>
    public string MediaTracking { get; set; } = "Web";
    /// <summary>Default field rotation name.</summary>
    public string DefaultRotation { get; set; } = "Normal";
    /// <summary>Default font, or null.</summary>
    public FontDto? DefaultFont { get; set; }
    /// <summary>Character encoding.</summary>
    public EncodingDto Encoding { get; set; } = new();
    /// <summary>Print options.</summary>
    public PrintOptionsDto Print { get; set; } = new();
    /// <summary>Declared variables.</summary>
    public List<VariableDto> Variables { get; set; } = new();
}

/// <summary>Block (^FB) options of a text element.</summary>
public sealed class TextBlockDto
{
    /// <summary>Block width in dots.</summary>
    public int WidthDots { get; set; } = 400;
    /// <summary>Maximum lines.</summary>
    public int MaxLines { get; set; } = 1;
    /// <summary>Line spacing.</summary>
    public int LineSpacing { get; set; }
    /// <summary>Alignment name.</summary>
    public string Alignment { get; set; } = "Left";
    /// <summary>Hanging indent.</summary>
    public int HangingIndent { get; set; }
}

/// <summary>Base of all serialized elements. The <c>type</c> property selects the concrete class.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextElementDto), "text")]
[JsonDerivedType(typeof(DataFieldElementDto), "dataField")]
[JsonDerivedType(typeof(BarcodeElementDto), "barcode")]
[JsonDerivedType(typeof(BoxElementDto), "box")]
[JsonDerivedType(typeof(LineElementDto), "line")]
[JsonDerivedType(typeof(CircleElementDto), "circle")]
[JsonDerivedType(typeof(ImageElementDto), "image")]
[JsonDerivedType(typeof(RawZplElementDto), "rawZpl")]
[JsonDerivedType(typeof(RecallPlaceholderElementDto), "recall")]
public abstract class ElementDto
{
    /// <summary>Stable element id (preserved so references survive a save/load cycle).</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Designer-facing name.</summary>
    public string? Name { get; set; }
    /// <summary>X in dots.</summary>
    public int X { get; set; }
    /// <summary>Y in dots.</summary>
    public int Y { get; set; }
    /// <summary>Origin kind name.</summary>
    public string OriginKind { get; set; } = "Origin";
    /// <summary>Rotation name, or null to inherit the label default.</summary>
    public string? Rotation { get; set; }
    /// <summary>Reverse field.</summary>
    public bool ReverseField { get; set; }
}

/// <summary>Serialized text element.</summary>
public class TextElementDto : ElementDto
{
    /// <summary>Text content.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Font id as a one-character string, or null.</summary>
    public string? FontId { get; set; }
    /// <summary>Font height, or null.</summary>
    public int? FontHeight { get; set; }
    /// <summary>Font width, or null.</summary>
    public int? FontWidth { get; set; }
    /// <summary>Block options, or null for a single line.</summary>
    public TextBlockDto? Block { get; set; }
}

/// <summary>Serialized data field (text bound to a variable).</summary>
public sealed class DataFieldElementDto : TextElementDto
{
    /// <summary>Bound variable name.</summary>
    public string VariableName { get; set; } = string.Empty;
}

/// <summary>Serialized barcode.</summary>
public sealed class BarcodeElementDto : ElementDto
{
    /// <summary>Symbology name.</summary>
    public string Symbology { get; set; } = "Code128";
    /// <summary>Data.</summary>
    public string Data { get; set; } = string.Empty;
    /// <summary>Height.</summary>
    public int Height { get; set; } = 100;
    /// <summary>Module width.</summary>
    public int ModuleWidth { get; set; } = 2;
    /// <summary>Wide bar ratio.</summary>
    public double WideBarRatio { get; set; } = 3.0;
    /// <summary>Human-readable line.</summary>
    public bool HumanReadable { get; set; } = true;
    /// <summary>Human-readable position name.</summary>
    public string HumanReadablePosition { get; set; } = "Below";
    /// <summary>Check digit.</summary>
    public bool CheckDigit { get; set; }
    /// <summary>Code 128 mode name.</summary>
    public string Code128Mode { get; set; } = "Automatic";
    /// <summary>QR error correction name.</summary>
    public string QrErrorCorrection { get; set; } = "Quartile";
    /// <summary>QR model.</summary>
    public int QrModel { get; set; } = 2;
    /// <summary>Module size.</summary>
    public int ModuleSize { get; set; } = 4;
    /// <summary>Columns.</summary>
    public int Columns { get; set; }
    /// <summary>Rows.</summary>
    public int Rows { get; set; }
    /// <summary>PDF417 security level.</summary>
    public int Pdf417SecurityLevel { get; set; } = 2;
    /// <summary>PDF417 truncated.</summary>
    public bool Pdf417Truncate { get; set; }
}

/// <summary>Serialized box.</summary>
public sealed class BoxElementDto : ElementDto
{
    /// <summary>Width.</summary>
    public int WidthDots { get; set; } = 100;
    /// <summary>Height.</summary>
    public int HeightDots { get; set; } = 100;
    /// <summary>Thickness.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Colour name.</summary>
    public string Color { get; set; } = "Black";
    /// <summary>Corner rounding.</summary>
    public int CornerRounding { get; set; }
}

/// <summary>Serialized line.</summary>
public sealed class LineElementDto : ElementDto
{
    /// <summary>Length.</summary>
    public int LengthDots { get; set; } = 100;
    /// <summary>Thickness.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Direction name.</summary>
    public string Direction { get; set; } = "Horizontal";
    /// <summary>Colour name.</summary>
    public string Color { get; set; } = "Black";
}

/// <summary>Serialized circle.</summary>
public sealed class CircleElementDto : ElementDto
{
    /// <summary>Diameter.</summary>
    public int DiameterDots { get; set; } = 100;
    /// <summary>Thickness.</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Colour name.</summary>
    public string Color { get; set; } = "Black";
}

/// <summary>Serialized image; pixels are stored as base64 luminance bytes.</summary>
public sealed class ImageElementDto : ElementDto
{
    /// <summary>Source width in pixels (0 when there is no source).</summary>
    public int SourceWidth { get; set; }
    /// <summary>Source height in pixels (0 when there is no source).</summary>
    public int SourceHeight { get; set; }
    /// <summary>Base64 of the row-major luminance bytes, or null when there is no source.</summary>
    public string? SourcePixels { get; set; }
    /// <summary>Target width.</summary>
    public int WidthDots { get; set; }
    /// <summary>Target height.</summary>
    public int HeightDots { get; set; }
    /// <summary>Dither mode name.</summary>
    public string Dither { get; set; } = "FloydSteinberg";
    /// <summary>Threshold.</summary>
    public int Threshold { get; set; } = 128;
    /// <summary>Invert.</summary>
    public bool Invert { get; set; }
}

/// <summary>Serialized raw ZPL (imported commands the designer does not interpret, kept verbatim). Needs format version 3.</summary>
public sealed class RawZplElementDto : ElementDto
{
    /// <summary>The original ZPL text (bytes above 0x7F are held as the Latin-1 char with the same value).</summary>
    public string Zpl { get; set; } = string.Empty;
    /// <summary>True when the block draws something on the label.</summary>
    public bool IsVisual { get; set; }
}

/// <summary>Serialized placeholder for an object stored on the printer (^XG, ^IL, ^XF recalls). Needs format version 3.</summary>
public sealed class RecallPlaceholderElementDto : ElementDto
{
    /// <summary>Command without prefix, upper case.</summary>
    public string Command { get; set; } = string.Empty;
    /// <summary>The recalled object.</summary>
    public string Reference { get; set; } = string.Empty;
    /// <summary>The full original command.</summary>
    public string Zpl { get; set; } = string.Empty;
    /// <summary>True when the command lives inside a positioned field.</summary>
    public bool EmitAsField { get; set; }
    /// <summary>Placeholder width in dots.</summary>
    public int WidthDots { get; set; } = 120;
    /// <summary>Placeholder height in dots.</summary>
    public int HeightDots { get; set; } = 80;
}
