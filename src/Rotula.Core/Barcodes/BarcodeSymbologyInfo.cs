using Rotula.Core.Model;

namespace Rotula.Core.Barcodes;

/// <summary>Broad family of a symbology; the UI uses it to group the symbology picker.</summary>
public enum BarcodeCategory
{
    /// <summary>One row of bars and spaces (Code 128, EAN-13, ...).</summary>
    Linear,
    /// <summary>Square or rectangular grid of modules (QR Code, Data Matrix, Aztec, MaxiCode).</summary>
    Matrix2D,
    /// <summary>Several linear rows stacked on top of each other (PDF417, Codablock, DataBar Expanded Stacked, ...).</summary>
    Stacked,
    /// <summary>Postal height-coded bars (POSTNET, PLANET).</summary>
    Postal,
}

/// <summary>
/// The properties of <see cref="Elements.BarcodeElement"/> that are meaningful for a given symbology. A property that is not
/// listed for a symbology is ignored by the ZPL generator and must not be offered in the UI.
/// </summary>
public enum BarcodeOption
{
    /// <summary>Bar height (or row height of a stacked code), <c>Height</c>.</summary>
    Height,
    /// <summary>Narrow bar / module width set by ^BY, <c>ModuleWidth</c>.</summary>
    ModuleWidth,
    /// <summary>Wide-to-narrow bar ratio set by ^BY, <c>WideBarRatio</c>.</summary>
    WideBarRatio,
    /// <summary>Show the human-readable line, <c>HumanReadable</c>.</summary>
    HumanReadable,
    /// <summary>Human-readable line above or below, <c>HumanReadablePosition</c>.</summary>
    HumanReadablePosition,
    /// <summary>Optional check digit switch, <c>CheckDigit</c>.</summary>
    CheckDigit,
    /// <summary>Code 128 subset mode, <c>Code128Mode</c>.</summary>
    Code128Mode,
    /// <summary>QR error correction level, <c>QrErrorCorrection</c>.</summary>
    QrErrorCorrection,
    /// <summary>QR model, <c>QrModel</c>.</summary>
    QrModel,
    /// <summary>Module size / magnification of a 2D symbol in dots, <c>ModuleSize</c>.</summary>
    ModuleSize,
    /// <summary>Columns (PDF417, Data Matrix) or characters per row (Codablock), <c>Columns</c>.</summary>
    Columns,
    /// <summary>Rows, <c>Rows</c>.</summary>
    Rows,
    /// <summary>PDF417 security level, <c>Pdf417SecurityLevel</c>.</summary>
    Pdf417SecurityLevel,
    /// <summary>PDF417 truncation, <c>Pdf417Truncate</c>.</summary>
    Pdf417Truncate,
    /// <summary>Code 11 check digit count, <c>Code11CheckDigitCount</c>.</summary>
    Code11CheckDigitCount,
    /// <summary>MSI check digit scheme, <c>MsiCheckDigit</c>.</summary>
    MsiCheckDigit,
    /// <summary>MSI check digit in the human-readable line, <c>MsiCheckDigitInText</c>.</summary>
    MsiCheckDigitInText,
    /// <summary>Codabar start character, <c>CodabarStart</c>.</summary>
    CodabarStart,
    /// <summary>Codabar stop character, <c>CodabarStop</c>.</summary>
    CodabarStop,
    /// <summary>GS1 DataBar variant, <c>DataBarType</c>.</summary>
    DataBarType,
    /// <summary>GS1 DataBar separator height, <c>DataBarSeparatorHeight</c>.</summary>
    DataBarSeparatorHeight,
    /// <summary>GS1 DataBar Expanded segments per row, <c>DataBarSegmentsPerRow</c>.</summary>
    DataBarSegmentsPerRow,
    /// <summary>Aztec ECI flag, <c>AztecEci</c>.</summary>
    AztecEci,
    /// <summary>Aztec error control / size, <c>AztecErrorControl</c>.</summary>
    AztecErrorControl,
    /// <summary>Aztec menu symbol flag, <c>AztecMenuSymbol</c>.</summary>
    AztecMenuSymbol,
    /// <summary>Aztec structured append count, <c>AztecStructuredAppendCount</c>.</summary>
    AztecStructuredAppendCount,
    /// <summary>Aztec structured append id, <c>AztecStructuredAppendId</c>.</summary>
    AztecStructuredAppendId,
    /// <summary>MicroPDF417 mode, <c>MicroPdf417Mode</c>.</summary>
    MicroPdf417Mode,
    /// <summary>Codablock variant, <c>CodablockMode</c>.</summary>
    CodablockMode,
    /// <summary>Codablock security checks, <c>CodablockSecurity</c>.</summary>
    CodablockSecurity,
    /// <summary>MaxiCode mode, <c>MaxiCodeMode</c>.</summary>
    MaxiCodeMode,
    /// <summary>MaxiCode symbol number, <c>MaxiCodeSymbolNumber</c>.</summary>
    MaxiCodeSymbolNumber,
    /// <summary>MaxiCode total symbols, <c>MaxiCodeSymbolTotal</c>.</summary>
    MaxiCodeSymbolTotal,
    /// <summary>TLC39 MicroPDF417 module width, <c>Tlc39MicroModuleWidth</c>.</summary>
    Tlc39MicroModuleWidth,
    /// <summary>TLC39 MicroPDF417 row height, <c>Tlc39MicroRowHeight</c>.</summary>
    Tlc39MicroRowHeight,
    /// <summary>Code 49 starting mode, <c>Code49StartMode</c>.</summary>
    Code49StartMode,
    /// <summary>Data Matrix carries a GS1 element string (GS1 DataMatrix, FNC1 first), <c>DataMatrixGs1</c>.</summary>
    DataMatrixGs1,
    /// <summary>The data is a GS1 Digital Link URI and is checked as one, <c>DigitalLink</c>.</summary>
    DigitalLink,
    /// <summary>QR mask pattern 0-7, or -1 for the printer's own choice (the parameter is left out), <c>QrMask</c>.</summary>
    QrMask,
    /// <summary>QR data input (automatic or manual with a character mode), <c>QrInputMode</c>.</summary>
    QrInputMode,
    /// <summary>The structured definition of a QR content (URL, Wi-Fi, vCard ...) as JSON, <c>QrContentSpec</c>.</summary>
    QrContentSpec,
}

/// <summary>How a <see cref="BarcodeOption"/> relates to the printer resolution; the DPI converter decides what to scale from it.</summary>
public enum BarcodeDotKind
{
    /// <summary>Not a dot measure (flags, modes, counts, or modules-based values such as the DataBar separator height); never scaled.</summary>
    NotDots,
    /// <summary>A module / narrow-bar width in whole dots (1-10): scaled with rounding to whole dots and a warning when the symbol width would change noticeably.</summary>
    Module,
    /// <summary>A bar, row or symbol height in dots: scaled proportionally.</summary>
    Length,
}

/// <summary>
/// Everything the UI, the importer and tools need to know about one barcode symbology, as data: names, the ZPL command,
/// the options that apply, and the rules for the data. It is descriptive; the authoritative checks are in <see cref="BarcodeValidator"/>.
/// </summary>
/// <param name="Symbology">The enum value.</param>
/// <param name="DisplayName">English name for pickers and documentation (the UI localizes its own).</param>
/// <param name="ZplCommand">The ZPL command that prints it, for example <c>^BC</c>.</param>
/// <param name="Category">Family used to group symbologies.</param>
/// <param name="Options">The <see cref="Elements.BarcodeElement"/> properties that apply.</param>
/// <param name="CharacterSet">Plain-language description of the characters the data may contain.</param>
/// <param name="LengthRule">Plain-language description of the allowed data length.</param>
/// <param name="MinLength">Smallest data length, or null when there is no fixed lower limit.</param>
/// <param name="MaxLength">Largest data length, or null when it depends on the label width.</param>
/// <param name="AllowedLengths">Exact lengths that are accepted (empty when any length between the limits is fine).</param>
/// <param name="ExampleData">Data that passes validation with the default options.</param>
/// <param name="HasPreviewEncoder">True when the designer can draw an exact, scannable preview; false when it shows a labelled placeholder.</param>
/// <param name="PreviewNote">What differs in the preview (or why there is none); null when the preview is exact.</param>
/// <param name="AlwaysPrintsHumanReadable">True when the printer always prints the human-readable line and it cannot be switched off.</param>
/// <param name="SupportsRotation">False when the ZPL command has no orientation parameter (MaxiCode), so a rotation chosen in the designer cannot be printed.</param>
public sealed record BarcodeSymbologyInfo(
    BarcodeSymbology Symbology,
    string DisplayName,
    string ZplCommand,
    BarcodeCategory Category,
    IReadOnlyList<BarcodeOption> Options,
    string CharacterSet,
    string LengthRule,
    int? MinLength,
    int? MaxLength,
    IReadOnlyList<int> AllowedLengths,
    string ExampleData,
    bool HasPreviewEncoder,
    string? PreviewNote,
    bool AlwaysPrintsHumanReadable,
    bool SupportsRotation = true)
{
    /// <summary>True when <paramref name="option"/> applies to this symbology.</summary>
    public bool Supports(BarcodeOption option) => Options.Contains(option);

    /// <summary>True when the element has a human-readable line (switchable or fixed).</summary>
    public bool SupportsHumanReadable => AlwaysPrintsHumanReadable || Supports(BarcodeOption.HumanReadable);

    /// <summary>True when the symbology has an optional check digit setting of any kind.</summary>
    public bool SupportsCheckDigit =>
        Supports(BarcodeOption.CheckDigit) || Supports(BarcodeOption.Code11CheckDigitCount) || Supports(BarcodeOption.MsiCheckDigit);

    /// <summary>True when the module size comes from the symbology's own magnification instead of ^BY.</summary>
    public bool UsesModuleSize => Supports(BarcodeOption.ModuleSize);

    /// <summary>The options of this symbology that are measured in printer dots, with how each one scales (never <see cref="BarcodeDotKind.NotDots"/>).</summary>
    public IEnumerable<(BarcodeOption Option, BarcodeDotKind Kind)> DotOptions =>
        Options.Select(o => (Option: o, Kind: BarcodeCatalog.GetDotKind(o))).Where(t => t.Kind != BarcodeDotKind.NotDots);
}

/// <summary>The catalogue of all barcode symbologies Rotula can write to and read from ZPL.</summary>
public static class BarcodeCatalog
{
    private static readonly BarcodeOption H = BarcodeOption.Height, W = BarcodeOption.ModuleWidth, R = BarcodeOption.WideBarRatio,
        HR = BarcodeOption.HumanReadable, HP = BarcodeOption.HumanReadablePosition, CD = BarcodeOption.CheckDigit;

    private const string Digits = "Digits 0-9";
    private const string Ascii = "Printable ASCII (space to ~)";

    private static BarcodeSymbologyInfo E(BarcodeSymbology s, string name, string cmd, BarcodeCategory cat, BarcodeOption[] options,
        string chars, string length, int? min, int? max, int[]? exact, string example, bool preview = true, string? note = null, bool alwaysText = false,
        bool rotation = true) =>
        new(s, name, cmd, cat, options, chars, length, min, max, exact ?? Array.Empty<int>(), example, preview, note, alwaysText, rotation);

    private static readonly BarcodeSymbologyInfo[] Entries =
    {
        E(BarcodeSymbology.Code128, "Code 128", "^BC", BarcodeCategory.Linear, new[] { H, W, HR, HP, BarcodeOption.Code128Mode },
            Ascii + ", except '>' (reserved for ZPL subset codes). With the mode Ean (GS1-128): a GS1 element string with each identifier in parentheses, such as (01)09506000134352(17)251231(10)ABC123",
            "Any length that fits the label; at most 48 data characters (identifiers and separators included) for GS1-128", 1, null, null, "ROTULA-12345"),
        E(BarcodeSymbology.Code39, "Code 39", "^B3", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, CD },
            "A-Z, 0-9, space and - . $ / + %", "Any length that fits the label", 1, null, null, "ABC-123",
            note: "The bar ratio is drawn as 2:1 in the preview."),
        E(BarcodeSymbology.Code93, "Code 93", "^BA", BarcodeCategory.Linear, new[] { H, W, HR, HP, CD },
            Ascii, "Any length that fits the label", 1, null, null, "TEST93"),
        E(BarcodeSymbology.Ean13, "EAN-13", "^BE", BarcodeCategory.Linear, new[] { H, W, HR, HP },
            Digits, "12 digits (check digit added) or 13 digits with a correct check digit", 12, 13, new[] { 12, 13 }, "590123412345"),
        E(BarcodeSymbology.UpcA, "UPC-A", "^BU", BarcodeCategory.Linear, new[] { H, W, HR, HP, CD },
            Digits, "11 digits (check digit added) or 12 digits with a correct check digit", 11, 12, new[] { 11, 12 }, "03600029145"),
        E(BarcodeSymbology.Interleaved2Of5, "Interleaved 2 of 5", "^B2", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, CD },
            Digits, "An even number of digits (odd when the printer adds the check digit)", 1, null, null, "1234567890"),
        E(BarcodeSymbology.QrCode, "QR Code", "^BQ", BarcodeCategory.Matrix2D, new[] { BarcodeOption.QrModel, BarcodeOption.ModuleSize, BarcodeOption.QrErrorCorrection, BarcodeOption.QrMask, BarcodeOption.QrInputMode, BarcodeOption.DigitalLink, BarcodeOption.QrContentSpec },
            "Any text. A GS1 Digital Link web address is a plain QR Code (option DigitalLink checks it)", "Up to 4296 characters", 1, 4296, null, "https://example.com"),
        E(BarcodeSymbology.DataMatrix, "Data Matrix", "^BX", BarcodeCategory.Matrix2D, new[] { BarcodeOption.ModuleSize, BarcodeOption.Columns, BarcodeOption.Rows, BarcodeOption.DataMatrixGs1, BarcodeOption.DigitalLink },
            "Any text; with the GS1 option, a GS1 element string written with parentheses such as (01)09506000134352(10)ABC", "Up to 2335 characters", 1, 2335, null, "ROTULA 123"),
        E(BarcodeSymbology.Pdf417, "PDF417", "^B7", BarcodeCategory.Stacked, new[] { H, W, BarcodeOption.Pdf417SecurityLevel, BarcodeOption.Columns, BarcodeOption.Rows, BarcodeOption.Pdf417Truncate },
            "Any text", "Up to 1850 characters", 1, 1850, null, "PDF417 sample payload"),

        // ---- later additions -------------------------------------------------------------------
        E(BarcodeSymbology.Code11, "Code 11", "^B1", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, BarcodeOption.Code11CheckDigitCount },
            "Digits 0-9 and the hyphen -", "Any length that fits the label", 1, null, null, "123-45",
            note: "The bar ratio is drawn as 3:1 in the preview."),
        E(BarcodeSymbology.Industrial2Of5, "Industrial 2 of 5", "^BI", BarcodeCategory.Linear, new[] { H, W, R, HR, HP },
            Digits, "Any length that fits the label", 1, null, null, "12345678",
            note: "The bar ratio is drawn as 3:1 in the preview."),
        E(BarcodeSymbology.Standard2Of5, "Standard 2 of 5", "^BJ", BarcodeCategory.Linear, new[] { H, W, R, HR, HP },
            Digits, "Any length that fits the label", 1, null, null, "12345678",
            note: "The bar ratio is drawn as 3:1 in the preview."),
        E(BarcodeSymbology.Logmars, "LOGMARS", "^BL", BarcodeCategory.Linear, new[] { H, W, R, HP },
            "A-Z (lower case is converted), 0-9, space and - . $ / + %", "Any length that fits the label; the mod 43 check character is always added", 1, null, null, "LOGMARS-123",
            note: "The bar ratio is drawn as 2:1 in the preview.", alwaysText: true),
        E(BarcodeSymbology.Codabar, "ANSI Codabar", "^BK", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, BarcodeOption.CodabarStart, BarcodeOption.CodabarStop },
            "Digits 0-9 and - $ : / . + (start and stop characters A-D are set with the options, not typed in the data)", "Any length that fits the label", 1, null, null, "123456",
            note: "The bar ratio is drawn as 2:1 in the preview."),
        E(BarcodeSymbology.Msi, "MSI", "^BM", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, BarcodeOption.MsiCheckDigit, BarcodeOption.MsiCheckDigitInText },
            Digits, "1 to 14 digits (1 to 13 when no check digit is added)", 1, 14, null, "1234567",
            note: "The bar ratio is drawn as 3:1 in the preview."),
        E(BarcodeSymbology.Plessey, "Plessey", "^BP", BarcodeCategory.Linear, new[] { H, W, R, HR, HP, CD },
            "Digits 0-9 and letters A-F", "Any length that fits the label", 1, null, null, "12345678",
            note: "The bar ratio is drawn as 3:1 in the preview."),
        E(BarcodeSymbology.Ean8, "EAN-8", "^B8", BarcodeCategory.Linear, new[] { H, W, HR, HP },
            Digits, "7 digits (check digit added) or 8 digits with a correct check digit", 7, 8, new[] { 7, 8 }, "9638507"),
        E(BarcodeSymbology.UpcE, "UPC-E", "^B9", BarcodeCategory.Linear, new[] { H, W, HR, HP, CD },
            Digits, "The 10-digit manufacturer + product code that ZPL expects, which must be zero-suppressible; also accepted: the 6-digit UPC-E code, with or without number system 0 and check digit",
            6, 12, new[] { 6, 7, 8, 10, 11, 12 }, "1234500005"),
        E(BarcodeSymbology.UpcEanExtension, "UPC/EAN extension", "^BS", BarcodeCategory.Linear, new[] { H, W, HR, HP },
            Digits, "Exactly 2 or 5 digits", 2, 5, new[] { 2, 5 }, "12345"),
        E(BarcodeSymbology.Postnet, "POSTNET", "^BZ", BarcodeCategory.Postal, new[] { H, W, R, HR, HP },
            Digits, "5, 9 or 11 digits; the check digit is added by the printer", 5, 11, new[] { 5, 9, 11 }, "12345",
            note: "Bar and gap widths follow the module width; the real printer uses fixed USPS dimensions."),
        E(BarcodeSymbology.Planet, "PLANET", "^B5", BarcodeCategory.Postal, new[] { H, W, R, HR, HP },
            Digits, "11 or 13 digits; the check digit is added by the printer", 11, 13, new[] { 11, 13 }, "12345678901",
            note: "Bar and gap widths follow the module width; the real printer uses fixed USPS dimensions."),
        E(BarcodeSymbology.DataBar, "GS1 DataBar", "^BR", BarcodeCategory.Stacked,
            new[] { BarcodeOption.DataBarType, BarcodeOption.ModuleSize, BarcodeOption.DataBarSeparatorHeight, H, BarcodeOption.DataBarSegmentsPerRow },
            "Digits for the omnidirectional, truncated, stacked and limited types; GS1 element strings such as (01)12345678901231(10)ABC for Expanded",
            "Up to 13 digits (padded with zeros on the left; a 14th digit must be the correct check digit) for types 1-5; an element string for Expanded", 1, null, null, "1234567890123",
            note: "The composite (2D) component and the symbology types 7-12 of ^BR are not supported; the bar height of stacked types is the whole symbol."),
        E(BarcodeSymbology.Aztec, "Aztec Code", "^BO", BarcodeCategory.Matrix2D,
            new[] { BarcodeOption.ModuleSize, BarcodeOption.AztecEci, BarcodeOption.AztecErrorControl, BarcodeOption.AztecMenuSymbol, BarcodeOption.AztecStructuredAppendCount, BarcodeOption.AztecStructuredAppendId },
            "Any text", "Up to 3000 characters", 1, 3000, null, "Rotula Aztec 123",
            note: "Rune, menu, structured append and ECI symbols are drawn by the printer; the designer shows a placeholder for them."),
        E(BarcodeSymbology.MicroPdf417, "MicroPDF417", "^BF", BarcodeCategory.Stacked, new[] { H, W, BarcodeOption.MicroPdf417Mode },
            "Any text", "Up to 250 characters (150 for 8-bit data, 366 for digits)", 1, 366, null, "MICRO PDF417 SAMPLE",
            preview: false, note: "The printer draws it; the designer shows a placeholder."),
        E(BarcodeSymbology.Codablock, "Codablock", "^BB", BarcodeCategory.Stacked,
            new[] { H, W, BarcodeOption.CodablockMode, BarcodeOption.CodablockSecurity, BarcodeOption.Columns, BarcodeOption.Rows },
            "Codablock A: Code 39 characters; E and F: printable ASCII", "Any length that fits the chosen rows and columns", 1, null, null, "CODABLOCK F 12345",
            preview: false, note: "The printer draws it; the designer shows a placeholder."),
        E(BarcodeSymbology.MaxiCode, "MaxiCode", "^BD", BarcodeCategory.Matrix2D,
            new[] { BarcodeOption.MaxiCodeMode, BarcodeOption.MaxiCodeSymbolNumber, BarcodeOption.MaxiCodeSymbolTotal },
            "Modes 2 and 3: a structured carrier message (class of service, country, postal code, then the message); modes 4-6: any text", "Up to 93 characters", 1, 138, null, "001840123456789",
            preview: false, note: "The printer draws it; the designer shows a placeholder.", rotation: false),
        E(BarcodeSymbology.Tlc39, "TLC39", "^BT", BarcodeCategory.Stacked,
            new[] { W, R, H, BarcodeOption.Tlc39MicroModuleWidth, BarcodeOption.Tlc39MicroRowHeight },
            "A 6-digit ECI number, optionally followed by , and alphanumeric serial and additional data fields", "6 to 150 characters", 6, 150, null, "123456,ABCD12345678901234",
            preview: false, note: "The printer draws it; the designer shows a placeholder."),
        E(BarcodeSymbology.Code49, "Code 49", "^B4", BarcodeCategory.Stacked,
            new[] { H, W, HR, HP, BarcodeOption.Code49StartMode },
            "Printable ASCII (Code 49 control characters : ; < = > ? in modes 0-5)", "Up to 81 characters", 1, 81, null, "CODE49 TEST",
            preview: false, note: "The printer draws it; the designer shows a placeholder."),
    };

    private static readonly Dictionary<BarcodeSymbology, BarcodeSymbologyInfo> BySymbology = Entries.ToDictionary(e => e.Symbology);

    /// <summary>
    /// How <paramref name="option"/> depends on the printer resolution. Every option is classified explicitly (a test fails when a
    /// new <see cref="BarcodeOption"/> is added without a decision), so a new dot-based option cannot silently skip DPI conversion.
    /// </summary>
    public static BarcodeDotKind GetDotKind(BarcodeOption option) => option switch
    {
        BarcodeOption.ModuleWidth or BarcodeOption.ModuleSize or BarcodeOption.Tlc39MicroModuleWidth => BarcodeDotKind.Module,
        BarcodeOption.Height or BarcodeOption.Tlc39MicroRowHeight => BarcodeDotKind.Length,
        // DataBarSeparatorHeight is 1 or 2 modules (not dots), so it follows the module size by itself.
        _ => BarcodeDotKind.NotDots,
    };

    /// <summary>Every symbology, in display order (the original nine first).</summary>
    public static IReadOnlyList<BarcodeSymbologyInfo> All => Entries;

    /// <summary>The catalogue entry of <paramref name="symbology"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined symbology.</exception>
    public static BarcodeSymbologyInfo Get(BarcodeSymbology symbology) =>
        BySymbology.TryGetValue(symbology, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(symbology));

    /// <summary>Finds the entry that a ZPL command prints (<c>^BC</c>, case-insensitive); null when no symbology uses it.</summary>
    public static BarcodeSymbologyInfo? FindByZplCommand(string command) =>
        Entries.FirstOrDefault(e => string.Equals(e.ZplCommand, command, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Explains why the designer cannot draw an exact preview of <paramref name="element"/>, or returns null when it can. A symbology
    /// that is normally drawn can still be unavailable for an element that uses an option the preview cannot reproduce.
    /// </summary>
    public static string? GetPreviewLimitation(Elements.BarcodeElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var info = Get(element.Symbology);
        if (!info.HasPreviewEncoder) return info.PreviewNote ?? "The printer draws this symbology; the designer shows a placeholder.";
        switch (element.Symbology)
        {
            case BarcodeSymbology.Aztec when element.AztecErrorControl == 300 || element.AztecMenuSymbol || element.AztecStructuredAppendCount > 1 || element.AztecEci:
                return "Aztec Rune, menu, structured append and ECI symbols are drawn by the printer; the designer shows a placeholder.";
        }
        return null;
    }
}
