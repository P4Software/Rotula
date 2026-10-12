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

    /// <summary>
    /// Print only when this rule holds for the record being printed, or null to always print. An element whose rule is false is left
    /// out of that record's ZPL entirely (see <see cref="ElementPrinting.IsPrinted"/>).
    /// </summary>
    public PrintCondition? PrintCondition { get; set; }

    /// <summary>
    /// The object stays in the file and is shown in the designer (as a note or a guide) but is never written to ZPL and never drawn in
    /// a print, PNG, JPEG or PDF.
    /// </summary>
    public bool NotPrintable { get; set; }

    /// <summary>
    /// Design-time only: a locked object cannot be moved, resized, rotated, aligned or deleted until it is unlocked. It can still be
    /// selected and its properties edited, and it never changes the ZPL.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Copies the printing and locking settings (<see cref="PrintCondition"/>, <see cref="NotPrintable"/>, <see cref="Locked"/>) onto another element, so every copy path keeps them.</summary>
    /// <param name="target">The element that receives the settings.</param>
    public void CopyObjectSettingsTo(LabelElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.PrintCondition = PrintCondition?.Clone();
        target.NotPrintable = NotPrintable;
        target.Locked = Locked;
    }
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
    /// <summary>
    /// How the block reacts to content longer than the box (shrink the font, or add lines), or null for the plain block. Rotula-only: the
    /// ZPL carries the computed values, so this does not come back when ZPL is imported.
    /// </summary>
    public Rotula.Core.Text.TextFitOptions? Fit { get; set; }
}

/// <summary>Fixed-size text box (ZPL <c>^TB</c>): word-wrapped text that is cut off at the box height.</summary>
public sealed class TextBoxOptions
{
    /// <summary>Box width in dots (1-32000).</summary>
    public int WidthDots { get; set; } = 400;
    /// <summary>Box height in dots (1-32000); lines that do not fit are not printed.</summary>
    public int HeightDots { get; set; } = 100;
}

/// <summary>
/// Printer-side serialization of a field (ZPL <c>^SN</c> or <c>^SF</c>): the printer, not Rotula, changes the value on every
/// label of a print run. This is separate from the host-side counters of the merge feature, which compute a different
/// literal value for each merged record before any ZPL is written.
/// </summary>
public sealed class PrinterSerialOptions
{
    /// <summary>Which command is used.</summary>
    public PrinterSerialKind Kind { get; set; } = PrinterSerialKind.Number;

    /// <summary>
    /// <see cref="PrinterSerialKind.Number"/> only: the step added on every label; negative counts down (ZPL default 1).
    /// </summary>
    public long Increment { get; set; } = 1;

    /// <summary><see cref="PrinterSerialKind.Number"/> only: keep leading zeros of the start value (ZPL default: no).</summary>
    public bool LeadingZeros { get; set; }

    /// <summary>
    /// <see cref="PrinterSerialKind.Mask"/> only: one placeholder per serialized character, counted from the end of the data.
    /// <c>D</c> decimal, <c>H</c> hexadecimal, <c>O</c> octal, <c>A</c> letter, <c>N</c> letter or digit, <c>%</c> leave this character alone.
    /// </summary>
    public string Mask { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="PrinterSerialKind.Mask"/> only: the step as a string written in the same alphabet as the mask
    /// (empty means the ZPL default, one).
    /// </summary>
    public string IncrementText { get; set; } = string.Empty;

    /// <summary>True for a start value ^SN can carry: 1-40 characters of letters, digits, space, '-', '/' or '.', with at least one digit.</summary>
    public static bool IsValidStartValue(string? value) =>
        value is { Length: >= 1 and <= 40 } && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '/' or '.') && value.Any(char.IsAsciiDigit);

    /// <summary>True for a ^SF mask: one or more of D, H, O, A, N (either case) or %.</summary>
    public static bool IsValidMask(string? mask) => mask is { Length: > 0 } && mask.All(ch => "DdHhOoAaNn%".Contains(ch));

    /// <summary>True for a ^SF increment string: letters, digits and %, possibly empty (the default step of one).</summary>
    public static bool IsValidIncrementText(string? text) => text is not null && text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '%');
}

/// <summary>Clock indicator characters of a field (ZPL <c>^FC</c>): they mark the real-time-clock codes inside the field data.</summary>
public sealed class FieldClockOptions
{
    /// <summary>Primary indicator (ZPL default <c>%</c>).</summary>
    public char Primary { get; set; } = '%';
    /// <summary>Secondary indicator, or null for none. Must differ from the other indicators.</summary>
    public char? Secondary { get; set; }
    /// <summary>Third indicator, or null for none. Must differ from the other indicators.</summary>
    public char? Third { get; set; }
}

/// <summary>
/// Base of the elements that print data supplied as field data: text and barcodes. It carries the options that only make
/// sense for a data field (field numbers, embedding, clock codes, printer-side serialization).
/// </summary>
public abstract class FieldDataElement : LabelElement
{
    /// <summary>Field number (ZPL <c>^FN</c>, 0-9999), or null. Fields with the same number share their data on the printer.</summary>
    public int? FieldNumber { get; set; }

    /// <summary>Optional prompt text stored with <see cref="FieldNumber"/> (letters, digits and spaces; ZPL <c>^FN</c> quoted text).</summary>
    public string? FieldPrompt { get; set; }

    /// <summary>
    /// Character that marks references to field numbers inside the data (ZPL <c>^FE</c>), or null for none. Rotula writes
    /// the data unchanged, so references such as <c>#2#</c> are expanded by the printer.
    /// </summary>
    public char? EmbedCharacter { get; set; }

    /// <summary>Real-time-clock indicators (ZPL <c>^FC</c>), or null when the field has none.</summary>
    public FieldClockOptions? Clock { get; set; }

    /// <summary>Printer-side serialization (ZPL <c>^SN</c> / <c>^SF</c>), or null. With <see cref="PrinterSerialKind.Number"/> the data is the start value.</summary>
    public PrinterSerialOptions? Serial { get; set; }
}

/// <summary>Text, either a single line, a wrapped ^FB block or a ^TB text box. Content may contain <c>{{variable}}</c> placeholders.</summary>
public class TextElement : FieldDataElement
{
    /// <summary>The text (newlines only mean something inside a block).</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Font id, or null to inherit the ^CF default.</summary>
    public char? FontId { get; set; }
    /// <summary>Character height in dots, or null to inherit.</summary>
    public int? FontHeight { get; set; }
    /// <summary>Character width in dots (0 = proportional), or null to inherit.</summary>
    public int? FontWidth { get; set; }
    /// <summary>
    /// A Windows (TrueType) font, or null for a printer font. A printer cannot have a Windows font, so such a text is drawn to a bitmap at the
    /// printer's resolution when the label is printed (it needs an <see cref="Rotula.Core.Text.ITextRasterizer"/>); the font id and the dot sizes of a
    /// printer font are then unused. Printer fonts keep using <see cref="FontId"/>, <see cref="FontHeight"/> and <see cref="FontWidth"/>.
    /// See <see cref="Rotula.Core.Text.FontChoice"/>, which presents both kinds as one value.
    /// </summary>
    public Rotula.Core.Text.FontChoice? Font { get; set; }
    /// <summary>
    /// Path of a font stored on the printer (for example <c>E:ARIAL.TTF</c>), printed with <c>^A@</c>, or null. It replaces <see cref="FontId"/>.
    /// The file must already be on the printer; Rotula does not load it.
    /// </summary>
    public string? StoredFontPath { get; set; }
    /// <summary>Block layout, or null for a single line.</summary>
    public TextBlockOptions? Block { get; set; }
    /// <summary>Text box (^TB), or null. A text element has either a <see cref="Block"/> or a text box, never both.</summary>
    public TextBoxOptions? TextBox { get; set; }
    /// <summary>Direction in which the characters advance (^FP); only text uses it.</summary>
    public FieldDirection Direction { get; set; } = FieldDirection.Horizontal;
    /// <summary>Extra gap between characters in dots (^FP, 0-9999).</summary>
    public int InterCharacterGap { get; set; }

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
public sealed class BarcodeElement : FieldDataElement
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

    // ---- options of the symbologies added after the first nine -------------------------------
    // Each one maps to exactly one ZPL parameter, so ZPL import and export keep it. The defaults below
    // are the values ZPL assumes when the parameter is left out.

    /// <summary>Code 11: number of check digits the printer appends, 1 or 2 (ZPL ^B1 default is 2).</summary>
    public int Code11CheckDigitCount { get; set; } = 2;
    /// <summary>MSI: which check digits the printer appends (ZPL ^BM default is one modulo 10).</summary>
    public MsiCheckDigitMode MsiCheckDigit { get; set; } = MsiCheckDigitMode.Mod10;
    /// <summary>MSI: also show the check digit(s) in the human-readable line.</summary>
    public bool MsiCheckDigitInText { get; set; }
    /// <summary>Codabar start character, A to D (data is written without start and stop characters).</summary>
    public char CodabarStart { get; set; } = 'A';
    /// <summary>Codabar stop character, A to D.</summary>
    public char CodabarStop { get; set; } = 'A';
    /// <summary>GS1 DataBar variant.</summary>
    public DataBarType DataBarType { get; set; } = DataBarType.Omnidirectional;
    /// <summary>GS1 DataBar: height of the separator between stacked rows, 1 or 2 modules.</summary>
    public int DataBarSeparatorHeight { get; set; } = 1;
    /// <summary>GS1 DataBar Expanded: segments per row (even, 2 to 22); fewer than 22 stacks the symbol.</summary>
    public int DataBarSegmentsPerRow { get; set; } = 22;
    /// <summary>Aztec: the data contains Extended Channel Interpretation codes.</summary>
    public bool AztecEci { get; set; }
    /// <summary>
    /// Aztec error control and symbol size/type: 0 = default, 1-99 = minimum error correction percent,
    /// 101-104 = compact symbol of 1-4 layers, 201-232 = full-range symbol of 1-32 layers, 300 = Aztec Rune.
    /// </summary>
    public int AztecErrorControl { get; set; }
    /// <summary>Aztec: the symbol is a menu (reader initialisation) symbol.</summary>
    public bool AztecMenuSymbol { get; set; }
    /// <summary>Aztec structured append: number of symbols in the set (1 = not structured).</summary>
    public int AztecStructuredAppendCount { get; set; } = 1;
    /// <summary>Aztec structured append: optional message id (at most 24 characters).</summary>
    public string AztecStructuredAppendId { get; set; } = string.Empty;
    /// <summary>MicroPDF417 mode 0-33; it fixes columns, rows and error correction (see the ZPL guide).</summary>
    public int MicroPdf417Mode { get; set; }
    /// <summary>Codablock variant.</summary>
    public CodablockMode CodablockMode { get; set; } = CodablockMode.F;
    /// <summary>Codablock: generate the symbol check characters (ZPL "security level", default yes).</summary>
    public bool CodablockSecurity { get; set; } = true;
    /// <summary>MaxiCode mode: 2 (US structured carrier message), 3 (non-US), 4, 5 or 6.</summary>
    public int MaxiCodeMode { get; set; } = 2;
    /// <summary>MaxiCode structured append: number of this symbol, 1-8.</summary>
    public int MaxiCodeSymbolNumber { get; set; } = 1;
    /// <summary>MaxiCode structured append: total number of symbols, 1-8.</summary>
    public int MaxiCodeSymbolTotal { get; set; } = 1;
    /// <summary>TLC39: narrow bar width of the MicroPDF417 part in dots, 1-10.</summary>
    public int Tlc39MicroModuleWidth { get; set; } = 2;
    /// <summary>TLC39: row height of the MicroPDF417 part in dots, 1-255.</summary>
    public int Tlc39MicroRowHeight { get; set; } = 4;
    /// <summary>Code 49 starting mode: '0' to '5', or 'A' for automatic.</summary>
    public char Code49StartMode { get; set; } = 'A';

    // ---- GS1 options ---------------------------------------------------------------------------
    // GS1-128 needs no option of its own: it is Code 128 in mode Ean (^BC mode D), which starts with FNC1, and its data is a GS1 element
    // string written with parentheses. GS1 DataBar Expanded takes the same kind of data. The two options below cover the rest.

    /// <summary>
    /// Data Matrix: the data is a GS1 element string written with parentheses, e.g. <c>(01)09506000134352(10)ABC</c>. The symbol is
    /// printed as GS1 DataMatrix (FNC1 in the first position, FNC1 between variable-length elements).
    /// </summary>
    public bool DataMatrixGs1 { get; set; }
    /// <summary>
    /// QR Code and Data Matrix: the data is a GS1 Digital Link URI such as <c>https://id.gs1.org/01/09506000134352/10/ABC</c>, and is
    /// checked as one. The symbol itself is an ordinary QR Code or Data Matrix: the URI is plain text, so nothing changes in the ZPL.
    /// </summary>
    public bool DigitalLink { get; set; }
    /// <summary>QR Code mask pattern, 0-7 (^BQ parameter e); -1 leaves the parameter out so the printer uses its default (7).</summary>
    public int QrMask { get; set; } = -1;
    /// <summary>QR Code data input declared in the ^FD header: automatic, or manual with a character mode.</summary>
    public QrInputMode QrInputMode { get; set; } = QrInputMode.Automatic;
    /// <summary>
    /// QR Code: the structured definition made by the content builder (kind and named fields, any of which may hold <c>{{field}}</c>
    /// placeholders), as JSON; empty when the data was typed by hand. <see cref="Data"/> keeps the generated template.
    /// </summary>
    public string QrContentSpec { get; set; } = string.Empty;

    /// <summary>
    /// The options introduced for the later symbologies as culture-neutral text, listing only values that differ from
    /// their default. This lets a file format store them in one open-ended field instead of one field per option.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExportExtraOptions()
    {
        var fresh = new BarcodeElement();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in ExtraOptionTable)
        {
            string mine = option.Get(this), theirs = option.Get(fresh);
            if (!string.Equals(mine, theirs, StringComparison.Ordinal)) result[option.Name] = mine;
        }
        return result;
    }

    /// <summary>
    /// Applies options produced by <see cref="ExportExtraOptions"/>. Names that are not known (for example written by a
    /// newer version) and values that cannot be read are ignored, so an old file never fails to open.
    /// </summary>
    public void ImportExtraOptions(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) return;
        foreach (var option in ExtraOptionTable)
            if (options.TryGetValue(option.Name, out var text) && text is not null)
                option.TrySet(this, text);
    }

    private sealed record ExtraOption(string Name, Func<BarcodeElement, string> Get, Func<BarcodeElement, string, bool> TrySet);

    private static ExtraOption IntOption(string name, Func<BarcodeElement, int> get, Action<BarcodeElement, int> set) =>
        new(name, e => get(e).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (e, t) => { if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)) { set(e, v); return true; } return false; });

    private static ExtraOption BoolOption(string name, Func<BarcodeElement, bool> get, Action<BarcodeElement, bool> set) =>
        new(name, e => get(e) ? "true" : "false",
            (e, t) => { if (bool.TryParse(t, out var v)) { set(e, v); return true; } return false; });

    private static ExtraOption CharOption(string name, Func<BarcodeElement, char> get, Action<BarcodeElement, char> set) =>
        new(name, e => get(e).ToString(), (e, t) => { if (t.Length == 1) { set(e, t[0]); return true; } return false; });

    private static ExtraOption EnumOption<T>(string name, Func<BarcodeElement, T> get, Action<BarcodeElement, T> set) where T : struct, Enum =>
        new(name, e => get(e).ToString(),
            // Only names are accepted (not numbers) so a stray value cannot become an undefined member.
            (e, t) => { if (t.Length > 0 && !char.IsDigit(t[0]) && Enum.TryParse<T>(t, false, out var v) && Enum.IsDefined(v)) { set(e, v); return true; } return false; });

    private static readonly ExtraOption[] ExtraOptionTable =
    {
        IntOption(nameof(Code11CheckDigitCount), e => e.Code11CheckDigitCount, (e, v) => e.Code11CheckDigitCount = v),
        EnumOption(nameof(MsiCheckDigit), e => e.MsiCheckDigit, (e, v) => e.MsiCheckDigit = v),
        BoolOption(nameof(MsiCheckDigitInText), e => e.MsiCheckDigitInText, (e, v) => e.MsiCheckDigitInText = v),
        CharOption(nameof(CodabarStart), e => e.CodabarStart, (e, v) => e.CodabarStart = v),
        CharOption(nameof(CodabarStop), e => e.CodabarStop, (e, v) => e.CodabarStop = v),
        EnumOption(nameof(DataBarType), e => e.DataBarType, (e, v) => e.DataBarType = v),
        IntOption(nameof(DataBarSeparatorHeight), e => e.DataBarSeparatorHeight, (e, v) => e.DataBarSeparatorHeight = v),
        IntOption(nameof(DataBarSegmentsPerRow), e => e.DataBarSegmentsPerRow, (e, v) => e.DataBarSegmentsPerRow = v),
        BoolOption(nameof(AztecEci), e => e.AztecEci, (e, v) => e.AztecEci = v),
        IntOption(nameof(AztecErrorControl), e => e.AztecErrorControl, (e, v) => e.AztecErrorControl = v),
        BoolOption(nameof(AztecMenuSymbol), e => e.AztecMenuSymbol, (e, v) => e.AztecMenuSymbol = v),
        IntOption(nameof(AztecStructuredAppendCount), e => e.AztecStructuredAppendCount, (e, v) => e.AztecStructuredAppendCount = v),
        new(nameof(AztecStructuredAppendId), e => e.AztecStructuredAppendId, (e, t) => { e.AztecStructuredAppendId = t; return true; }),
        IntOption(nameof(MicroPdf417Mode), e => e.MicroPdf417Mode, (e, v) => e.MicroPdf417Mode = v),
        EnumOption(nameof(CodablockMode), e => e.CodablockMode, (e, v) => e.CodablockMode = v),
        BoolOption(nameof(CodablockSecurity), e => e.CodablockSecurity, (e, v) => e.CodablockSecurity = v),
        IntOption(nameof(MaxiCodeMode), e => e.MaxiCodeMode, (e, v) => e.MaxiCodeMode = v),
        IntOption(nameof(MaxiCodeSymbolNumber), e => e.MaxiCodeSymbolNumber, (e, v) => e.MaxiCodeSymbolNumber = v),
        IntOption(nameof(MaxiCodeSymbolTotal), e => e.MaxiCodeSymbolTotal, (e, v) => e.MaxiCodeSymbolTotal = v),
        IntOption(nameof(Tlc39MicroModuleWidth), e => e.Tlc39MicroModuleWidth, (e, v) => e.Tlc39MicroModuleWidth = v),
        IntOption(nameof(Tlc39MicroRowHeight), e => e.Tlc39MicroRowHeight, (e, v) => e.Tlc39MicroRowHeight = v),
        CharOption(nameof(Code49StartMode), e => e.Code49StartMode, (e, v) => e.Code49StartMode = v),
        BoolOption(nameof(DataMatrixGs1), e => e.DataMatrixGs1, (e, v) => e.DataMatrixGs1 = v),
        BoolOption(nameof(DigitalLink), e => e.DigitalLink, (e, v) => e.DigitalLink = v),
        IntOption(nameof(QrMask), e => e.QrMask, (e, v) => e.QrMask = v),
        EnumOption(nameof(QrInputMode), e => e.QrInputMode, (e, v) => e.QrInputMode = v),
        new(nameof(QrContentSpec), e => e.QrContentSpec, (e, t) => { if (t.Length > 20000) return false; e.QrContentSpec = t; return true; }),
    };
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

/// <summary>A diagonal line (^GD) drawn inside a box of the given size.</summary>
public sealed class DiagonalLineElement : LabelElement
{
    /// <summary>Width of the bounding box in dots (3-32000).</summary>
    public int WidthDots { get; set; } = 100;
    /// <summary>Height of the bounding box in dots (3-32000).</summary>
    public int HeightDots { get; set; } = 100;
    /// <summary>Line thickness in dots (1-32000).</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Line colour.</summary>
    public GraphicColor Color { get; set; } = GraphicColor.Black;
    /// <summary>Which way the line leans.</summary>
    public DiagonalLean Lean { get; set; } = DiagonalLean.Right;
}

/// <summary>An ellipse outline (^GE).</summary>
public sealed class EllipseElement : LabelElement
{
    /// <summary>Width in dots (3-4095).</summary>
    public int WidthDots { get; set; } = 100;
    /// <summary>Height in dots (3-4095).</summary>
    public int HeightDots { get; set; } = 60;
    /// <summary>Border thickness in dots (1-4095).</summary>
    public int ThicknessDots { get; set; } = 3;
    /// <summary>Line colour.</summary>
    public GraphicColor Color { get; set; } = GraphicColor.Black;
}

/// <summary>A built-in symbol such as the copyright sign (^GS). The printer draws it from its own symbol font.</summary>
public sealed class SymbolElement : LabelElement
{
    /// <summary>Which symbol.</summary>
    public GraphicSymbol Symbol { get; set; } = GraphicSymbol.Copyright;
    /// <summary>Character height in dots (0-32000).</summary>
    public int HeightDots { get; set; } = 40;
    /// <summary>Character width in dots (0-32000).</summary>
    public int WidthDots { get; set; } = 40;
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

/// <summary>
/// ZPL that the importer could not interpret, kept verbatim so nothing is lost on a re-export.
/// It is either a standalone command (for example <c>^LS20</c>) or a whole field that uses an unsupported feature.
/// </summary>
public sealed class RawZplElement : LabelElement
{
    /// <summary>
    /// The original ZPL text, emitted unchanged by the generator. When the file was imported from bytes,
    /// bytes above 0x7F are held as the Latin-1 character with the same value.
    /// </summary>
    public string Zpl { get; set; } = string.Empty;

    /// <summary>True when the block draws something on the label (a field); false for configuration commands.</summary>
    public bool IsVisual { get; set; }
}

/// <summary>
/// Stand-in for something stored on the printer that a recall command (<c>^XG</c>, <c>^IL</c>, <c>^XF</c>)
/// would print. The stored object is not part of the file, so the designer only shows a placeholder, and the
/// generator writes the original recall command back so the printed result does not change.
/// </summary>
public sealed class RecallPlaceholderElement : LabelElement
{
    /// <summary>The command without its prefix, upper case: <c>XG</c>, <c>IL</c> or <c>XF</c>.</summary>
    public string Command { get; set; } = string.Empty;
    /// <summary>The recalled object, for example <c>R:LOGO.GRF</c>; may be empty.</summary>
    public string Reference { get; set; } = string.Empty;
    /// <summary>The full original command including its parameters, for example <c>^XGR:LOGO.GRF,1,1</c>.</summary>
    public string Zpl { get; set; } = string.Empty;
    /// <summary>True when the command lives inside a positioned field (<c>^FO..^FS</c>), false when it stands alone.</summary>
    public bool EmitAsField { get; set; }
    /// <summary>Width the designer draws the placeholder with, in dots (the real size is unknown).</summary>
    public int WidthDots { get; set; } = 120;
    /// <summary>Height the designer draws the placeholder with, in dots (the real size is unknown).</summary>
    public int HeightDots { get; set; } = 80;

    /// <summary>
    /// Magnification of a <c>^XG</c> recall (its <c>mx,my</c> parameters, 1-10, default 1). Other recalls, and parameters that
    /// cannot be read, give 1 x 1. Used to draw a graphic downloaded in the same file at the size the printer prints it.
    /// </summary>
    public (int X, int Y) GetMagnification()
    {
        if (!string.Equals(Command, "XG", StringComparison.OrdinalIgnoreCase)) return (1, 1);
        var parts = Zpl.Split(',');
        int Read(int i) => i < parts.Length && int.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, 1, 10) : 1;
        return (Read(1), Read(2));
    }
}
