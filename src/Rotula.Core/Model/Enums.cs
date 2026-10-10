namespace Rotula.Core.Model;

/// <summary>Field rotation (ZPL orientation letter).</summary>
public enum FieldRotation
{
    /// <summary>Normal (0 degrees), ZPL "N".</summary>
    Normal,
    /// <summary>Rotated 90 degrees clockwise, ZPL "R".</summary>
    Rotated90,
    /// <summary>Inverted (180 degrees), ZPL "I".</summary>
    Inverted180,
    /// <summary>Read from bottom up (270 degrees clockwise), ZPL "B".</summary>
    Rotated270,
}

/// <summary>Whole-label print orientation (^PO).</summary>
public enum LabelOrientation
{
    /// <summary>Normal, ^PON.</summary>
    Normal,
    /// <summary>Label printed rotated 180 degrees, ^POI.</summary>
    Inverted,
}

/// <summary>Media type (^MT).</summary>
public enum MediaType
{
    /// <summary>Thermal transfer ribbon media, ^MTT.</summary>
    ThermalTransfer,
    /// <summary>Direct thermal media, ^MTD.</summary>
    DirectThermal,
}

/// <summary>Media tracking mode (^MN).</summary>
public enum MediaTracking
{
    /// <summary>Continuous media, ^MNN.</summary>
    Continuous,
    /// <summary>Non-continuous media tracked by web/gap sensing, ^MNY.</summary>
    Web,
    /// <summary>Non-continuous media tracked by black mark sensing, ^MNM.</summary>
    Mark,
}

/// <summary>Whether a field position is the top-left corner (^FO) or the typesetting origin (^FT).</summary>
public enum FieldOriginKind
{
    /// <summary>Field origin: top-left corner, ^FO.</summary>
    Origin,
    /// <summary>Field typeset: text baseline / bottom-left, ^FT.</summary>
    Typeset,
}

/// <summary>Paragraph alignment inside a text block (^FB).</summary>
public enum TextAlignment
{
    /// <summary>Left, "L".</summary>
    Left,
    /// <summary>Centre, "C".</summary>
    Center,
    /// <summary>Right, "R".</summary>
    Right,
    /// <summary>Justified, "J".</summary>
    Justified,
}

/// <summary>Colour of a graphic primitive.</summary>
public enum GraphicColor
{
    /// <summary>Black, "B".</summary>
    Black,
    /// <summary>White, "W".</summary>
    White,
}

/// <summary>Direction of a <see cref="Elements.LineElement"/>.</summary>
public enum LineDirection
{
    /// <summary>Horizontal line.</summary>
    Horizontal,
    /// <summary>Vertical line.</summary>
    Vertical,
}

/// <summary>Supported barcode symbologies.</summary>
public enum BarcodeSymbology
{
    /// <summary>Code 128, ^BC.</summary>
    Code128,
    /// <summary>Code 39, ^B3.</summary>
    Code39,
    /// <summary>Code 93, ^BA.</summary>
    Code93,
    /// <summary>EAN-13, ^BE.</summary>
    Ean13,
    /// <summary>UPC-A, ^BU.</summary>
    UpcA,
    /// <summary>Interleaved 2 of 5, ^B2.</summary>
    Interleaved2Of5,
    /// <summary>QR Code, ^BQ.</summary>
    QrCode,
    /// <summary>Data Matrix (ECC 200), ^BX.</summary>
    DataMatrix,
    /// <summary>PDF417, ^B7.</summary>
    Pdf417,
    // Members below were added later. They are appended (never inserted) so that the numeric values
    // of the members above stay stable for anything that stored them as numbers.
    /// <summary>Code 11 (USD-8), ^B1.</summary>
    Code11,
    /// <summary>Industrial 2 of 5, ^BI.</summary>
    Industrial2Of5,
    /// <summary>Standard 2 of 5, ^BJ.</summary>
    Standard2Of5,
    /// <summary>LOGMARS (Code 39 with a mandatory mod 43 check character), ^BL.</summary>
    Logmars,
    /// <summary>ANSI Codabar (NW-7), ^BK.</summary>
    Codabar,
    /// <summary>MSI (modified Plessey), ^BM.</summary>
    Msi,
    /// <summary>Plessey, ^BP.</summary>
    Plessey,
    /// <summary>EAN-8, ^B8.</summary>
    Ean8,
    /// <summary>UPC-E, ^B9.</summary>
    UpcE,
    /// <summary>UPC/EAN two- or five-digit add-on, ^BS.</summary>
    UpcEanExtension,
    /// <summary>POSTNET, ^BZ with postal type 0.</summary>
    Postnet,
    /// <summary>PLANET, ^B5 (also ^BZ with postal type 1).</summary>
    Planet,
    /// <summary>GS1 DataBar family, ^BR; the variant is <see cref="Elements.BarcodeElement.DataBarType"/>.</summary>
    DataBar,
    /// <summary>Aztec Code, ^BO.</summary>
    Aztec,
    /// <summary>MicroPDF417, ^BF.</summary>
    MicroPdf417,
    /// <summary>Codablock A, E or F, ^BB.</summary>
    Codablock,
    /// <summary>UPS MaxiCode, ^BD.</summary>
    MaxiCode,
    /// <summary>TLC39 (Code 39 plus MicroPDF417), ^BT.</summary>
    Tlc39,
    /// <summary>Code 49, ^B4.</summary>
    Code49,
}

/// <summary>Variant of the GS1 DataBar family (the ^BR symbology type parameter, values 1 to 6).</summary>
public enum DataBarType
{
    /// <summary>GS1 DataBar Omnidirectional, type 1.</summary>
    Omnidirectional = 1,
    /// <summary>GS1 DataBar Truncated, type 2.</summary>
    Truncated = 2,
    /// <summary>GS1 DataBar Stacked, type 3.</summary>
    Stacked = 3,
    /// <summary>GS1 DataBar Stacked Omnidirectional, type 4.</summary>
    StackedOmnidirectional = 4,
    /// <summary>GS1 DataBar Limited, type 5.</summary>
    Limited = 5,
    /// <summary>GS1 DataBar Expanded, type 6 (stacked over several rows when the segment width is below 22).</summary>
    Expanded = 6,
}

/// <summary>Check digit scheme of MSI (the ^BM check digit selection parameter).</summary>
public enum MsiCheckDigitMode
{
    /// <summary>No check digit, "A".</summary>
    None,
    /// <summary>One modulo 10 check digit, "B" (the ZPL default).</summary>
    Mod10,
    /// <summary>Two modulo 10 check digits, "C".</summary>
    DoubleMod10,
    /// <summary>One modulo 11 and one modulo 10 check digit, "D".</summary>
    Mod11Mod10,
}

/// <summary>Codablock variant (the ^BB mode parameter).</summary>
public enum CodablockMode
{
    /// <summary>Codablock A (Code 39 character set), "A".</summary>
    A,
    /// <summary>Codablock E (Code 128 character set with automatic FNC1), "E".</summary>
    E,
    /// <summary>Codablock F (Code 128 character set), "F" (the ZPL default).</summary>
    F,
}

/// <summary>Position of the human-readable line of a linear barcode.</summary>
public enum HumanReadablePosition
{
    /// <summary>Printed below the bars.</summary>
    Below,
    /// <summary>Printed above the bars.</summary>
    Above,
}

/// <summary>Code 128 subset selection mode (^BC mode parameter).</summary>
public enum Code128Mode
{
    /// <summary>No selected mode, "N".</summary>
    None,
    /// <summary>UCC case mode, "U".</summary>
    Ucc,
    /// <summary>Automatic mode (the printer picks the shortest encoding), "A".</summary>
    Automatic,
    /// <summary>EAN-128 mode, "D".</summary>
    Ean,
}

/// <summary>QR Code error correction level.</summary>
public enum QrErrorCorrection
{
    /// <summary>~7% recovery, "L".</summary>
    Low,
    /// <summary>~15% recovery, "M".</summary>
    Medium,
    /// <summary>~25% recovery, "Q".</summary>
    Quartile,
    /// <summary>~30% recovery, "H".</summary>
    High,
}

/// <summary>How a grey image is reduced to black and white.</summary>
public enum ImageDitherMode
{
    /// <summary>Hard threshold: darker than the threshold is black.</summary>
    Threshold,
    /// <summary>Floyd-Steinberg error diffusion (best for photos).</summary>
    FloydSteinberg,
    /// <summary>4x4 Bayer ordered dithering (stable, no error propagation).</summary>
    Ordered,
}

/// <summary>What the printer does with a label after printing it (the ^MM mode letter).</summary>
public enum PrintMode
{
    /// <summary>Tear-off, "T".</summary>
    Tear,
    /// <summary>Peel-off, "P".</summary>
    Peel,
    /// <summary>Rewind, "R".</summary>
    Rewind,
    /// <summary>Applicator, "A".</summary>
    Applicator,
    /// <summary>Cutter, "C".</summary>
    Cutter,
    /// <summary>Delayed cutter, "D".</summary>
    DelayedCutter,
    /// <summary>RFID, "F".</summary>
    Rfid,
    /// <summary>Kiosk, "K".</summary>
    Kiosk,
}

/// <summary>Direction in which the characters of a text field advance (the ^FP direction parameter).</summary>
public enum FieldDirection
{
    /// <summary>Left to right, "H" (the default).</summary>
    Horizontal,
    /// <summary>Top to bottom, "V".</summary>
    Vertical,
    /// <summary>Right to left, "R".</summary>
    Reverse,
}

/// <summary>Which way a diagonal line leans (the ^GD orientation parameter).</summary>
public enum DiagonalLean
{
    /// <summary>Rises to the right, like a slash: bottom-left to top-right. ZPL "R" or "/" (the default).</summary>
    Right,
    /// <summary>Falls to the right, like a backslash: top-left to bottom-right. ZPL "L" or "\".</summary>
    Left,
}

/// <summary>The symbols ^GS can draw. The letter is what the field data holds.</summary>
public enum GraphicSymbol
{
    /// <summary>Registered trademark, data "A".</summary>
    Registered,
    /// <summary>Copyright, data "B".</summary>
    Copyright,
    /// <summary>Trademark, data "C".</summary>
    Trademark,
    /// <summary>Underwriters Laboratories approval mark, data "D".</summary>
    UnderwritersLaboratories,
    /// <summary>Canadian Standards Association approval mark, data "E".</summary>
    CanadianStandards,
}

/// <summary>Which printer-side serialization command a field uses.</summary>
public enum PrinterSerialKind
{
    /// <summary>^SN: the field data is a start value whose trailing number counts up or down on every label.</summary>
    Number,
    /// <summary>^SF: the field data is serialized according to a character-by-character mask.</summary>
    Mask,
}
