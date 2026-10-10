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
