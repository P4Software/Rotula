using System.Globalization;
using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Core.Barcodes;

/// <summary>
/// Validates barcode data and options per symbology. Results are structured issues with message keys
/// (never UI text) so the UI can localize them. Keys follow <c>Barcode.&lt;Name&gt;.&lt;Rule&gt;</c>; the arguments, when
/// present, are culture-neutral strings that fill the placeholders of the localized message.
/// </summary>
public static class BarcodeValidator
{
    private const string Code39Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-. $/+%";
    private const string CodabarChars = "0123456789-$:/.+";

    /// <summary>Validates the options of <paramref name="barcode"/> and the already variable-resolved <paramref name="data"/>.</summary>
    public static List<ValidationIssue> Validate(BarcodeElement barcode, string data)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        var issues = new List<ValidationIssue>();
        string id = barcode.Id;
        void Err(string key, params string[] args) => issues.Add(new ValidationIssue(ValidationSeverity.Error, key, id, args));

        BarcodeSymbologyInfo? info = Enum.IsDefined(barcode.Symbology) ? BarcodeCatalog.Get(barcode.Symbology) : null;
        if (info is null)
        {
            Err("Barcode.Symbology.Unknown");
            return issues;
        }

        if (data.Length == 0) Err("Barcode.Data.Empty");

        bool bars = info.Category is BarcodeCategory.Linear or BarcodeCategory.Postal;
        if (info.Supports(BarcodeOption.ModuleWidth) && (barcode.ModuleWidth < 1 || barcode.ModuleWidth > 10)) Err("Barcode.ModuleWidth.Range", "1", "10");
        // ^BY always carries the ratio for bar codes (even when the symbology ignores it), so it must be a value ZPL accepts.
        if ((bars || barcode.Symbology == BarcodeSymbology.Tlc39) && (barcode.WideBarRatio < 2.0 || barcode.WideBarRatio > 3.0)) Err("Barcode.WideBarRatio.Range", "2.0", "3.0");
        if (info.Supports(BarcodeOption.Height))
        {
            (int min, int max) = barcode.Symbology switch
            {
                BarcodeSymbology.MicroPdf417 or BarcodeSymbology.Tlc39 => (1, 9999),
                BarcodeSymbology.Codablock => (2, 32000),
                _ => (1, 32000),
            };
            if (barcode.Height < min || barcode.Height > max) Err("Barcode.Height.Range", Num(min), Num(max));
        }

        if (data.Length > 0)
            ValidateData(barcode, data, Err);
        ValidateOptions(barcode, Err);
        if (data.Length > 0) ValidateGs1(barcode, data, issues);

        // Settings that the ZPL command has no parameter for: they are not an error, but they will not be printed as chosen.
        if (!info.SupportsRotation && barcode.Rotation is not null)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Barcode.MaxiCode.RotationIgnored", id));
        if (info.AlwaysPrintsHumanReadable && !barcode.HumanReadable)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Barcode.Logmars.TextAlways", id));
        return issues;
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static void ValidateData(BarcodeElement b, string data, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);

        switch (b.Symbology)
        {
            case BarcodeSymbology.Code128:
                // GS1-128 data (an element string with parentheses) is checked by ValidateGs1, which also knows how to send a '>' in a value.
                if (IsGs128Data(b, data)) break;
                // '>' starts a ZPL invocation sequence inside Code 128 data, so a literal one cannot be sent.
                if (data.Any(c => c > 126 || c < 32)) Err("Barcode.Code128.Charset");
                else if (data.Contains('>')) Err("Barcode.Code128.ReservedChar", ">");
                break;
            case BarcodeSymbology.Code39:
                if (data.Any(c => !Code39Chars.Contains(c))) Err("Barcode.Code39.Charset");
                break;
            case BarcodeSymbology.Code93:
                if (data.Any(c => c > 126 || c < 32)) Err("Barcode.Code93.Charset");
                break;
            case BarcodeSymbology.Ean13:
                ValidateGtin(data, 12, 13, "Ean13", err);
                break;
            case BarcodeSymbology.UpcA:
                ValidateGtin(data, 11, 12, "UpcA", err);
                break;
            case BarcodeSymbology.Interleaved2Of5:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else
                {
                    // Digits are encoded in pairs; the printer appends the check digit, so the user data must be odd then.
                    bool needOdd = b.CheckDigit;
                    if ((data.Length % 2 == 1) != needOdd) Err("Barcode.I25.Length", needOdd ? "odd" : "even");
                }
                break;
            case BarcodeSymbology.QrCode:
                if (b.QrModel is not (1 or 2)) Err("Barcode.Qr.Model");
                if (b.ModuleSize < 1 || b.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                if (data.Length > 4296) Err("Barcode.Qr.TooLong", "4296");
                else ValidateQr(b, data, err);
                break;
            case BarcodeSymbology.DataMatrix:
                if (b.ModuleSize < 1 || b.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                if (b.Columns < 0 || b.Columns > 144) Err("Barcode.Columns.Range", "0", "144");
                if (b.Rows < 0 || b.Rows > 144) Err("Barcode.Rows.Range", "0", "144");
                if (data.Length > 2335) Err("Barcode.DataMatrix.TooLong", "2335");
                break;
            case BarcodeSymbology.Pdf417:
                if (b.Pdf417SecurityLevel < 1 || b.Pdf417SecurityLevel > 8) Err("Barcode.Pdf417.Security", "1", "8");
                if (b.Columns < 0 || b.Columns > 30) Err("Barcode.Columns.Range", "0", "30");
                if (b.Rows != 0 && (b.Rows < 3 || b.Rows > 90)) Err("Barcode.Rows.Range", "3", "90");
                if (data.Length > 1850) Err("Barcode.Pdf417.TooLong", "1850");
                break;

            case BarcodeSymbology.Code11:
                if (data.Any(c => c is not ((>= '0' and <= '9') or '-'))) Err("Barcode.Code11.Charset");
                if (b.Code11CheckDigitCount is not (1 or 2)) Err("Barcode.Code11.CheckDigits");
                break;
            case BarcodeSymbology.Industrial2Of5:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                break;
            case BarcodeSymbology.Standard2Of5:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                break;
            case BarcodeSymbology.Logmars:
                // ZPL converts lower case to upper case for LOGMARS, so both are accepted.
                if (data.Any(c => !Code39Chars.Contains(char.ToUpperInvariant(c)))) Err("Barcode.Logmars.Charset");
                break;
            case BarcodeSymbology.Codabar:
                if (data.Any(c => !CodabarChars.Contains(c))) Err("Barcode.Codabar.Charset");
                if (b.CodabarStart is < 'A' or > 'D' || b.CodabarStop is < 'A' or > 'D') Err("Barcode.Codabar.StartStop");
                break;
            case BarcodeSymbology.Msi:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else
                {
                    // The ZPL guide limits the data to 14 digits when check digits are added and 13 when they are not.
                    int max = b.MsiCheckDigit == MsiCheckDigitMode.None ? 13 : 14;
                    if (data.Length > max) Err("Barcode.Msi.Length", Num(max));
                }
                if (!Enum.IsDefined(b.MsiCheckDigit)) Err("Barcode.Msi.CheckDigit");
                break;
            case BarcodeSymbology.Plessey:
                if (data.Any(c => c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))) Err("Barcode.Plessey.Charset");
                break;
            case BarcodeSymbology.Ean8:
                ValidateGtin(data, 7, 8, "Ean8", err);
                break;
            case BarcodeSymbology.UpcE:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else if (UpcE.TryNormalize(data, out var upcEten) is { } upcEProblem)
                {
                    switch (upcEProblem)
                    {
                        case UpcEProblem.Length: Err("Barcode.UpcE.Length"); break;
                        case UpcEProblem.NumberSystem: Err("Barcode.UpcE.NumberSystem"); break;
                        case UpcEProblem.NotCompressible: Err("Barcode.UpcE.NotCompressible"); break;
                        default: Err("Barcode.UpcE.CheckDigit", Num(UpcE.CheckDigit(upcEten))); break;
                    }
                }
                break;
            case BarcodeSymbology.UpcEanExtension:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else if (data.Length is not (2 or 5)) Err("Barcode.UpcEanExtension.Length");
                break;
            case BarcodeSymbology.Postnet:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else if (data.Length is not (5 or 9 or 11)) Err("Barcode.Postnet.Length", "5, 9, 11");
                break;
            case BarcodeSymbology.Planet:
                if (!AllDigits(data)) Err("Barcode.Digits.Only");
                else if (data.Length is not (11 or 13)) Err("Barcode.Planet.Length", "11, 13");
                break;
            case BarcodeSymbology.DataBar:
                ValidateDataBar(b, data, err);
                break;
            case BarcodeSymbology.Aztec:
                if (data.Length > 3000) Err("Barcode.Aztec.TooLong", "3000");
                break;
            case BarcodeSymbology.MicroPdf417:
                if (data.Length > 366) Err("Barcode.MicroPdf417.TooLong", "366");
                break;
            case BarcodeSymbology.Codablock:
                if (b.CodablockMode == CodablockMode.A && data.Any(c => !Code39Chars.Contains(c))) Err("Barcode.Codablock.Charset");
                else if (b.CodablockMode != CodablockMode.A && data.Any(c => c < 32 || c > 126)) Err("Barcode.Codablock.Charset128");
                break;
            case BarcodeSymbology.MaxiCode:
                ValidateMaxiCode(b, data, err);
                break;
            case BarcodeSymbology.Tlc39:
                ValidateTlc39(data, err);
                break;
            case BarcodeSymbology.Code49:
                if (data.Any(c => c < 32 || c > 126)) Err("Barcode.Code49.Charset");
                else if (data.Length > 81) Err("Barcode.Code49.TooLong", "81");
                break;
            default:
                Err("Barcode.Symbology.Unknown");
                break;
        }
    }

    // ---- GS1 ---------------------------------------------------------------------------------------

    /// <summary>
    /// True when a Code 128 element is a GS1-128 symbol: mode D (UCC/EAN, which starts with FNC1) and data written as an element string
    /// with parentheses. Data in mode D that does not start with a parenthesis keeps the older meaning (free text with an FNC1 start),
    /// so labels made before GS1 support keep printing as they did.
    /// </summary>
    public static bool IsGs128Data(BarcodeElement b, string data) =>
        b.Symbology == BarcodeSymbology.Code128 && b.Code128Mode == Code128Mode.Ean && data.TrimStart().StartsWith('(');

    /// <summary>True when the element prints GS1 DataMatrix (the GS1 option is on and the data is an element string).</summary>
    public static bool IsGs1DataMatrix(BarcodeElement b) => b.Symbology == BarcodeSymbology.DataMatrix && b.DataMatrixGs1;

    private static ValidationIssue ForElement(ValidationIssue i, string id) => new(i.Severity, i.MessageKey, id, i.Args.ToArray());

    private static void ValidateGs1(BarcodeElement b, string data, List<ValidationIssue> issues)
    {
        string id = b.Id;
        void Add(IEnumerable<ValidationIssue> found) => issues.AddRange(found.Select(i => ForElement(i, id)));

        if (IsGs128Data(b, data))
        {
            var message = Gs1Message.ParseBracketed(data, out var parse);
            Add(parse);
            if (message is null) return;
            Add(Gs1Validator.ValidateForGs128(message));
            // The printer drops parentheses and spaces in mode D, so a value that has one cannot be sent as it is.
            if (message.Elements.Any(e => e.Value.Contains('(') || e.Value.Contains(')')))
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Gs128.Parentheses", id));
            // Mode D computes the check digit itself; asking for another one (parameter e) would only confuse the interpretation line.
            if (b.CheckDigit)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Gs1.Gs128.CheckDigitOption", id));
            return;
        }

        if (IsGs1DataMatrix(b))
        {
            if (b.DigitalLink) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Gs1.Option.Conflict", id));
            var message = Gs1Message.ParseBracketed(data, out var parse);
            Add(parse);
            if (message is not null) Add(Gs1Validator.Validate(message));
            return;
        }

        if (b.DigitalLink && b.Symbology is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix)
        {
            Gs1DigitalLink.Parse(data, out var uriIssues);
            Add(uriIssues);
            return;
        }

        // GS1 DataBar Expanded: the existing checks cover the shape of the data; the dictionary adds identifier formats and relations.
        // Only when those checks passed, so that one mistake is not reported twice.
        if (b.Symbology == BarcodeSymbology.DataBar && b.DataBarType == DataBarType.Expanded && !data.Contains('|')
            && !issues.Any(i => i.Severity == ValidationSeverity.Error && i.MessageKey.StartsWith("Barcode.DataBar.", StringComparison.Ordinal))
            && Gs1ElementString.Parse(data) is { Count: > 0 } elements)
        {
            var message = new Gs1Message(elements.Select(e => new Gs1Element(e.Ai.Trim(), e.Value)));
            // Only errors: a DataBar often carries part of the data of an item, so "requires" and "repeated" warnings would nag.
            // A space is not in the GS1 character set, but the DataBar encoder has always accepted it (ISO/IEC 646), so it stays accepted here.
            Add(Gs1Validator.Validate(message).Where(i => i.Severity == ValidationSeverity.Error && !(i.MessageKey == "Gs1.Lint.Cset82" && i.Args.Count > 1 && i.Args[1] == " ")));
        }
    }

    /// <summary>The QR options that depend on the data: mask, manual input modes, and whether the payload fits the model and level.</summary>
    private static void ValidateQr(BarcodeElement b, string data, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);
        if (b.QrMask is < -1 or > 7) Err("Barcode.Qr.Mask", "0", "7");
        if (!Enum.IsDefined(b.QrInputMode)) Err("Barcode.Qr.InputMode");
        switch (b.QrInputMode)
        {
            case QrInputMode.ManualNumeric when !AllDigits(data): Err("Barcode.Qr.ManualNumeric"); break;
            case QrInputMode.ManualAlphanumeric when QrSizing.ModeOf(data) == QrDataMode.Byte: Err("Barcode.Qr.ManualAlphanumeric"); break;
            case QrInputMode.ManualByte when System.Text.Encoding.UTF8.GetByteCount(data) > 9999: Err("Barcode.Qr.ManualByteLength", "9999"); break;
        }
        if (b.QrModel is 1 or 2)
        {
            var fit = QrSizing.Analyze(data, b.QrErrorCorrection, b.QrModel);
            // The largest symbol of the model holds only so much at this level; the message gives the limit for the character mode in use.
            if (!fit.Fits) Err("Barcode.Qr.Capacity", Num(fit.Units), Num(fit.MaxUnits), b.QrErrorCorrection.ToString());
        }
    }

    private static void ValidateOptions(BarcodeElement b, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);
        switch (b.Symbology)
        {
            case BarcodeSymbology.DataBar:
                if (!Enum.IsDefined(b.DataBarType)) Err("Barcode.DataBar.Type");
                if (b.ModuleSize < 1 || b.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                if (b.DataBarSeparatorHeight is < 1 or > 2) Err("Barcode.DataBar.Separator", "1", "2");
                if (b.DataBarSegmentsPerRow is < 2 or > 22 || b.DataBarSegmentsPerRow % 2 != 0) Err("Barcode.DataBar.Segments", "2", "22");
                break;
            case BarcodeSymbology.Aztec:
                if (b.ModuleSize < 1 || b.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                if (!IsValidAztecErrorControl(b.AztecErrorControl)) Err("Barcode.Aztec.ErrorControl");
                if (b.AztecStructuredAppendCount is < 1 or > 26) Err("Barcode.Aztec.StructuredAppend", "1", "26");
                if (b.AztecStructuredAppendId.Length > 24) Err("Barcode.Aztec.StructuredAppendId", "24");
                // The id is a ^BO parameter: a comma would split it, and ^ or ~ would start a new command.
                if (b.AztecStructuredAppendId.Any(c => c is ',' or '^' or '~' || c < ' ' || c > '~')) Err("Barcode.Aztec.StructuredAppendIdChars");
                break;
            case BarcodeSymbology.MicroPdf417:
                if (b.MicroPdf417Mode is < 0 or > 33) Err("Barcode.MicroPdf417.Mode", "0", "33");
                break;
            case BarcodeSymbology.Codablock:
                if (!Enum.IsDefined(b.CodablockMode)) Err("Barcode.Codablock.Mode");
                if (b.Columns != 0 && (b.Columns < 2 || b.Columns > 62)) Err("Barcode.Codablock.Columns", "2", "62");
                int maxRows = b.CodablockMode == CodablockMode.A ? 22 : 4;
                int minRows = b.CodablockMode == CodablockMode.A ? 1 : 2;
                if (b.Rows != 0 && (b.Rows < minRows || b.Rows > maxRows)) Err("Barcode.Codablock.Rows", Num(minRows), Num(maxRows));
                break;
            case BarcodeSymbology.MaxiCode:
                if (b.MaxiCodeMode is < 2 or > 6) Err("Barcode.MaxiCode.Mode", "2", "6");
                if (b.MaxiCodeSymbolTotal is < 1 or > 8 || b.MaxiCodeSymbolNumber < 1 || b.MaxiCodeSymbolNumber > b.MaxiCodeSymbolTotal) Err("Barcode.MaxiCode.Symbol", "1", "8");
                break;
            case BarcodeSymbology.Tlc39:
                if (b.Tlc39MicroModuleWidth is < 1 or > 10) Err("Barcode.Tlc39.MicroModuleWidth", "1", "10");
                if (b.Tlc39MicroRowHeight is < 1 or > 255) Err("Barcode.Tlc39.MicroRowHeight", "1", "255");
                break;
            case BarcodeSymbology.Code49:
                if (b.Code49StartMode is not ((>= '0' and <= '5') or 'A')) Err("Barcode.Code49.StartMode");
                break;
        }
    }

    /// <summary>True for the error control and symbol size values the ^BO command accepts.</summary>
    public static bool IsValidAztecErrorControl(int v) => v == 0 || v is >= 1 and <= 99 || v is >= 101 and <= 104 || v is >= 201 and <= 232 || v == 300;

    private static void ValidateDataBar(BarcodeElement b, string data, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);
        if (data.Contains('|'))
        {
            // The part after '|' is a composite (2D) component, which Rotula does not model.
            Err("Barcode.DataBar.Composite", Array.Empty<string>());
            return;
        }
        if (b.DataBarType == DataBarType.Expanded)
        {
            switch (Gs1ElementString.TryReduce(data, out _))
            {
                case Gs1ElementStringProblem.Malformed: Err("Barcode.DataBar.ElementString"); return;
                case Gs1ElementStringProblem.Character: Err("Barcode.DataBar.Charset"); return;
                case Gs1ElementStringProblem.TooLong: Err("Barcode.DataBar.TooLong", Num(Gs1ElementString.MaxReducedLength)); return;
            }
            foreach (var (ai, value) in Gs1ElementString.Parse(data))
            {
                // AI 01 carries a GTIN-14 whose last digit is the check digit, so a wrong one is certainly a typing mistake.
                if (ai == "01")
                {
                    if (value.Length != 14 || !AllDigits(value)) { Err("Barcode.DataBar.Gtin"); return; }
                    if (!CheckDigits.IsValidGs1(value)) { Err("Barcode.DataBar.CheckDigit", Num(CheckDigits.Gs1(value[..^1]))); return; }
                }
            }
            return;
        }
        if (!AllDigits(data)) { Err("Barcode.Digits.Only", Array.Empty<string>()); return; }
        // ZPL pads a short item number with zeros on the left (confirmed by the guide's printed sample); 14 digits mean GTIN-14.
        if (data.Length > 14) { Err("Barcode.DataBar.Length", new[] { "13", "14" }); return; }
        if (data.Length == 14 && !CheckDigits.IsValidGs1(data)) { Err("Barcode.DataBar.CheckDigit", new[] { Num(CheckDigits.Gs1(data[..^1])) }); return; }
        // The limited symbol only encodes values up to 1999999999999 (first digit 0 or 1).
        if (b.DataBarType == DataBarType.Limited && data.PadLeft(13, '0')[0] > '1') Err("Barcode.DataBar.LimitedRange");
    }

    private static void ValidateMaxiCode(BarcodeElement b, string data, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);
        if (b.MaxiCodeMode == 2)
        {
            // Mode 2: three-digit class of service, three-digit country, then a nine-digit US ZIP (5 + 4).
            if (data.Length < 15 || !AllDigits(data[..15])) Err("Barcode.MaxiCode.Message", new[] { "2" });
        }
        else if (b.MaxiCodeMode == 3)
        {
            // Mode 3: class of service, country, then a six character postal code of A-Z and 0-9.
            if (data.Length < 12 || !AllDigits(data[..6]) || data[6..12].Any(c => c is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z'))))
                Err("Barcode.MaxiCode.Message", new[] { "3" });
        }
        if (data.Length > 138) Err("Barcode.MaxiCode.TooLong", new[] { "138" });
    }

    private static void ValidateTlc39(string data, Action<string, string[]> err)
    {
        void Err(string key, params string[] args) => err(key, args);
        if (data.Length < 6 || !AllDigits(data[..6]) || (data.Length > 6 && data[6] != ',')) { Err("Barcode.Tlc39.Format", Array.Empty<string>()); return; }
        if (data.Length > 150) { Err("Barcode.Tlc39.TooLong", new[] { "150" }); return; }
        if (data.Length > 7)
        {
            var fields = data[7..].Split(',');
            if (fields.Any(f => f.Length > 25 || f.Any(c => !char.IsAsciiLetterOrDigit(c))))
                Err("Barcode.Tlc39.Fields", new[] { "25" });
        }
    }

    /// <summary>
    /// GTIN rules for EAN-13, UPC-A and EAN-8: digits only; either the payload length (printer computes the check digit)
    /// or payload + 1 with a correct check digit.
    /// </summary>
    private static void ValidateGtin(string data, int payloadLength, int fullLength, string name, Action<string, string[]> err)
    {
        if (!AllDigits(data)) { err("Barcode.Digits.Only", Array.Empty<string>()); return; }
        if (data.Length != payloadLength && data.Length != fullLength)
        {
            err($"Barcode.{name}.Length", new[] { Num(payloadLength), Num(fullLength) });
            return;
        }
        if (data.Length == fullLength && !CheckDigits.IsValidGs1(data))
            err($"Barcode.{name}.CheckDigit", new[] { Num(CheckDigits.Gs1(data[..^1])) });
    }

    private static bool AllDigits(string s) => s.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// Returns the data exactly as it must be put into ^FD: symbologies whose printer recomputes or fixes a part of the data
    /// (EAN-13, UPC-A, EAN-8 lose a typed check digit; UPC-E becomes the 10-digit manufacturer + product code; GS1 DataBar loses
    /// its GTIN check digit) get it in that form, so output is identical whichever spelling the user typed.
    /// </summary>
    public static string NormalizeForPrinter(BarcodeSymbology symbology, string data) => symbology switch
    {
        BarcodeSymbology.Ean13 when data.Length == 13 => data[..12],
        BarcodeSymbology.UpcA when data.Length == 12 => data[..11],
        BarcodeSymbology.Ean8 when data.Length == 8 => data[..7],
        BarcodeSymbology.UpcE when data.All(char.IsAsciiDigit) && UpcE.TryNormalize(data, out var ten) is null => ten,
        _ => data,
    };

    /// <summary>
    /// Like <see cref="NormalizeForPrinter(BarcodeSymbology, string)"/> but aware of the element's options, which matter for
    /// GS1 DataBar (only the non-expanded types drop a 14th digit).
    /// </summary>
    public static string NormalizeForPrinter(BarcodeElement barcode, string data)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        // GS1 data is stored with parentheses and no separators; each command wants its own spelling of FNC1 (see Gs1ZplData).
        if (IsGs128Data(barcode, data) && Gs1Message.ParseBracketed(data, out _) is { } gs128) return Gs1ZplData.ToCode128ModeD(gs128);
        if (IsGs1DataMatrix(barcode) && Gs1Message.ParseBracketed(data, out _) is { } gs1Dm) return Gs1ZplData.ToDataMatrix(gs1Dm);
        if (barcode.Symbology == BarcodeSymbology.DataBar && barcode.DataBarType != DataBarType.Expanded && data.Length == 14 && data.All(char.IsAsciiDigit))
            return data[..13];
        return NormalizeForPrinter(barcode.Symbology, data);
    }
}
