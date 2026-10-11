using System.Globalization;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Zpl.Import;

// Barcode commands. Parameter positions and defaults follow the ZPL II Programming Guide.
internal sealed partial class ZplImporter
{
    private readonly record struct Rejection(string Key, string[] Args);

    /// <summary>
    /// Decides whether a barcode field uses something Rotula cannot represent without changing what prints
    /// (GS1 DataBar types 7-12 and composite data, USPS Intelligent Mail). Such a field is kept verbatim as raw ZPL.
    /// </summary>
    private Rejection? RejectBarcode(Field f)
    {
        string[] p = f.BarcodeParams;
        switch (f.BarcodeCommand)
        {
            case "BC":
                // Mode D field data with parentheses is GS1-128; it is only taken over when Rotula would write exactly the same ZPL back.
                if (UpperChar(p, 5) == 'D' && (f.Data ?? string.Empty).TrimStart().StartsWith('(') && !Gs1ZplData.TryFromCode128ModeD(f.Data!, out _))
                    return new Rejection("Import.Barcode.Gs1.Data", new[] { "^BC" });
                break;
            case "BR":
                int type = ParseIntQuiet(p, 1) ?? 1;
                if (type is < 1 or > 6) return new Rejection("Import.Barcode.DataBar.Type", new[] { type.ToString(CultureInfo.InvariantCulture) });
                if ((f.Data ?? string.Empty).Contains('|')) return new Rejection("Import.Barcode.DataBar.Composite", Array.Empty<string>());
                break;
            case "BZ":
                int postal = ParseIntQuiet(p, 4) ?? 0;
                if (postal is not (0 or 1)) return new Rejection("Import.Barcode.Postal.Type", new[] { postal.ToString(CultureInfo.InvariantCulture) });
                break;
        }
        return null;
    }

    private static int? ParseIntQuiet(string[] p, int i) =>
        Opt(p, i) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>Printer resolution as the ZPL guide's "dots per millimetre" classes decide several defaults.</summary>
    private int ByDpi(int at203, int at300, int at600, int at150 = -1) => (int)_options.Dpi switch
    {
        <= 150 => at150 < 0 ? at203 : at150,
        <= 203 => at203,
        <= 300 => at300,
        _ => at600,
    };

    private LabelElement BuildBarcode(DocCtx d, Field f)
    {
        var c = new ZplCommand('^', f.BarcodeCommand!, string.Empty, f.StartPosition, f.StartPosition);
        string[] p = f.BarcodeParams;
        string data = f.Data ?? string.Empty;
        var b = new BarcodeElement { Data = data };

        int HeightAt(int i) => Math.Clamp(Int(p, i, c, scale: true) ?? _byHeight, 1, 32000);
        void Linear(BarcodeSymbology symbology)
        {
            b.Symbology = symbology;
            b.ModuleWidth = _byWidth;
            b.WideBarRatio = _byRatio;
        }
        // Stacked codes take only the module width from ^BY; their ratio is fixed.
        void ModuleOnly(BarcodeSymbology symbology)
        {
            b.Symbology = symbology;
            b.ModuleWidth = _byWidth;
        }
        // The common "height, human readable, above" triple; each command has its own defaults for the two switches.
        void Lines(int heightIndex, bool readable, bool above)
        {
            b.Height = HeightAt(heightIndex);
            b.HumanReadable = YesNo(p, heightIndex + 1, readable);
            b.HumanReadablePosition = YesNo(p, heightIndex + 2, above) ? HumanReadablePosition.Above : HumanReadablePosition.Below;
        }
        void Line(int heightIndex) => Lines(heightIndex, true, false);
        void Rot() => b.Rotation = ResolveRotation(d, UpperChar(p, 0));
        int Clamped(int i, int min, int max, int fallback) => Math.Clamp(Int(p, i, c) ?? fallback, min, max);
        char Letter(int i, string allowed, char fallback) => allowed.Contains(UpperChar(p, i)) ? UpperChar(p, i) : fallback;

        switch (f.BarcodeCommand)
        {
            case "BC":
                Linear(BarcodeSymbology.Code128);
                Rot();
                Line(1);
                b.Code128Mode = UpperChar(p, 5) switch { 'U' => Code128Mode.Ucc, 'A' => Code128Mode.Automatic, 'D' => Code128Mode.Ean, _ => Code128Mode.None };
                if (b.Code128Mode == Code128Mode.Ean && Gs1ZplData.TryFromCode128ModeD(data.TrimStart(), out var gs128))
                    b.Data = gs128!.ToBracketed();
                else
                    b.Data = NormalizeCode128(b, data, f);
                break;
            case "B3":
                Linear(BarcodeSymbology.Code39);
                Rot();
                b.CheckDigit = YesNo(p, 1, false);
                Line(2);
                break;
            case "BA":
                Linear(BarcodeSymbology.Code93);
                Rot();
                Line(1);
                b.CheckDigit = YesNo(p, 4, false);
                break;
            case "BE":
                Linear(BarcodeSymbology.Ean13);
                Rot();
                Line(1);
                break;
            case "BU":
                Linear(BarcodeSymbology.UpcA);
                Rot();
                Line(1);
                b.CheckDigit = YesNo(p, 4, true);
                break;
            case "B2":
                Linear(BarcodeSymbology.Interleaved2Of5);
                Rot();
                Line(1);
                b.CheckDigit = YesNo(p, 4, false);
                break;
            case "BQ":
                b.Symbology = BarcodeSymbology.QrCode;
                Rot();
                b.QrModel = Int(p, 1, c) is 1 ? 1 : 2;
                b.ModuleSize = Math.Clamp(Int(p, 2, c) ?? 1, 1, 10);
                b.QrErrorCorrection = UpperChar(p, 3) switch { 'L' => QrErrorCorrection.Low, 'M' => QrErrorCorrection.Medium, 'H' => QrErrorCorrection.High, _ => QrErrorCorrection.Quartile };
                b.QrMask = Int(p, 4, c) is { } mask and >= 0 and <= 7 ? mask : -1;
                var qr = QrHeader.Match(data);
                if (qr.Success)
                {
                    b.QrErrorCorrection = qr.Groups[1].Value switch { "L" => QrErrorCorrection.Low, "M" => QrErrorCorrection.Medium, "H" => QrErrorCorrection.High, _ => QrErrorCorrection.Quartile };
                    string rest = qr.Groups[3].Value;
                    b.Data = rest;
                    if (qr.Groups[2].Value == "M")
                    {
                        // Manual input: the character mode letter (and for bytes a four-digit count) precedes the data.
                        if (rest.Length > 0 && rest[0] == 'N') { b.QrInputMode = QrInputMode.ManualNumeric; b.Data = rest[1..]; }
                        else if (rest.Length > 0 && rest[0] == 'A') { b.QrInputMode = QrInputMode.ManualAlphanumeric; b.Data = rest[1..]; }
                        else if (rest.Length > 0 && rest[0] == 'K') { b.QrInputMode = QrInputMode.ManualKanji; b.Data = rest[1..]; }
                        else if (rest.Length >= 5 && rest[0] == 'B' && rest.Skip(1).Take(4).All(char.IsAsciiDigit)) { b.QrInputMode = QrInputMode.ManualByte; b.Data = rest[5..]; }
                        else Warn("Import.Barcode.Qr.ManualMode", f.StartPosition, "^BQ", b.Id);
                    }
                }
                break;
            case "BX":
                b.Symbology = BarcodeSymbology.DataMatrix;
                Rot();
                b.ModuleSize = Math.Clamp(Int(p, 1, c) ?? 1, 1, 10);
                if (Int(p, 2, c) is { } quality && quality != 200)
                    Warn("Import.Barcode.DataMatrix.Quality", f.StartPosition, "^BX", b.Id, quality.ToString(CultureInfo.InvariantCulture));
                b.Columns = Math.Clamp(Int(p, 3, c) ?? 0, 0, 144);
                b.Rows = Math.Clamp(Int(p, 4, c) ?? 0, 0, 144);
                // GS1 DataMatrix: FNC1 first (written as the escape character of parameter g, default tilde, followed by 1). Only taken over when
                // Rotula would write the same field data back; anything else stays plain text as before.
                char escape = Opt(p, 6) is { Length: 1 } g ? g[0] : '~';
                if ((Int(p, 2, c) ?? 0) == 200 && Gs1ZplData.TryFromDataMatrix(data, escape, out var gs1Dm))
                {
                    b.DataMatrixGs1 = true;
                    b.Data = gs1Dm!.ToBracketed();
                }
                break;
            case "B7":
                ModuleOnly(BarcodeSymbology.Pdf417);
                Rot();
                b.Height = HeightAt(1);
                // ZPL's "0" security level means automatic; the model's smallest is 1.
                b.Pdf417SecurityLevel = Int(p, 2, c) is { } sec and >= 1 ? Math.Min(sec, 8) : 2;
                b.Columns = Math.Clamp(Int(p, 3, c) ?? 0, 0, 30);
                b.Rows = Math.Clamp(Int(p, 4, c) ?? 0, 0, 90);
                b.Pdf417Truncate = YesNo(p, 5, false);
                break;

            // ---- symbologies added later ---------------------------------------------------
            case "B1": // o,e,h,f,g ; e: Y = 1 check digit, N = 2 (default)
                Linear(BarcodeSymbology.Code11);
                Rot();
                b.Code11CheckDigitCount = YesNo(p, 1, false) ? 1 : 2;
                Line(2);
                break;
            case "BI": // o,h,f,g
                Linear(BarcodeSymbology.Industrial2Of5);
                Rot();
                Line(1);
                break;
            case "BJ": // o,h,f,g
                Linear(BarcodeSymbology.Standard2Of5);
                Rot();
                Line(1);
                break;
            case "BK": // o,e,h,f,g,k,l ; e is fixed at N
                Linear(BarcodeSymbology.Codabar);
                Rot();
                Line(2);
                b.CodabarStart = Letter(5, "ABCD", 'A');
                b.CodabarStop = Letter(6, "ABCD", 'A');
                break;
            case "BL": // o,h,g ; the interpretation line is always on
                Linear(BarcodeSymbology.Logmars);
                Rot();
                b.Height = HeightAt(1);
                b.HumanReadable = true;
                b.HumanReadablePosition = YesNo(p, 2, false) ? HumanReadablePosition.Above : HumanReadablePosition.Below;
                break;
            case "BM": // o,e,h,f,g,e2
                Linear(BarcodeSymbology.Msi);
                Rot();
                b.MsiCheckDigit = UpperChar(p, 1) switch { 'A' => MsiCheckDigitMode.None, 'C' => MsiCheckDigitMode.DoubleMod10, 'D' => MsiCheckDigitMode.Mod11Mod10, _ => MsiCheckDigitMode.Mod10 };
                Line(2);
                b.MsiCheckDigitInText = YesNo(p, 5, false);
                break;
            case "BP": // o,e,h,f,g
                Linear(BarcodeSymbology.Plessey);
                Rot();
                b.CheckDigit = YesNo(p, 1, false);
                Line(2);
                break;
            case "B8": // o,h,f,g
                Linear(BarcodeSymbology.Ean8);
                Rot();
                Line(1);
                break;
            case "B9": // o,h,f,g,e
                Linear(BarcodeSymbology.UpcE);
                Rot();
                Line(1);
                b.CheckDigit = YesNo(p, 4, true);
                break;
            case "BS": // o,h,f,g ; both switches default to yes
                Linear(BarcodeSymbology.UpcEanExtension);
                Rot();
                Lines(1, true, true);
                break;
            case "BZ": // o,h,f,g,t ; t is 0 (POSTNET) or 1 (PLANET), see RejectBarcode
                Linear(ParseIntQuiet(p, 4) == 1 ? BarcodeSymbology.Planet : BarcodeSymbology.Postnet);
                Rot();
                Lines(1, false, false);
                break;
            case "B5": // o,h,f,g ; both switches default to no
                Linear(BarcodeSymbology.Planet);
                Rot();
                Lines(1, false, false);
                break;
            case "BR": // a,b,c,d,e,f ; orientation defaults to R, not to ^FW
                b.Symbology = BarcodeSymbology.DataBar;
                b.Rotation = ResolveRotation(d, RotationFrom(UpperChar(p, 0)) is null ? 'R' : UpperChar(p, 0));
                b.DataBarType = (DataBarType)Clamped(1, 1, 6, 1);
                b.ModuleSize = Clamped(2, 1, 10, ByDpi(2, 3, 6));
                b.DataBarSeparatorHeight = Clamped(3, 1, 2, 1);
                b.Height = Math.Clamp(Int(p, 4, c, scale: true) ?? 25, 1, 32000);
                b.DataBarSegmentsPerRow = Clamped(5, 2, 22, 22) & ~1;
                if (b.DataBarSegmentsPerRow < 2) b.DataBarSegmentsPerRow = 2;
                break;
            case "BO": // a,b,c,d,e,f,g
                b.Symbology = BarcodeSymbology.Aztec;
                Rot();
                b.ModuleSize = Clamped(1, 1, 10, ByDpi(2, 3, 6, at150: 1));
                b.AztecEci = YesNo(p, 2, false);
                b.AztecErrorControl = Int(p, 3, c) is { } ec && BarcodeValidatorAztec(ec) ? ec : 0;
                b.AztecMenuSymbol = YesNo(p, 4, false);
                b.AztecStructuredAppendCount = Clamped(5, 1, 26, 1);
                b.AztecStructuredAppendId = Opt(p, 6) ?? string.Empty;
                break;
            case "BF": // o,h,m
                ModuleOnly(BarcodeSymbology.MicroPdf417);
                Rot();
                b.Height = HeightAt(1);
                b.MicroPdf417Mode = Clamped(2, 0, 33, 0);
                break;
            case "BB": // o,h,s,c,r,m
                ModuleOnly(BarcodeSymbology.Codablock);
                Rot();
                b.Height = HeightAt(1);
                b.CodablockSecurity = YesNo(p, 2, true);
                b.Columns = Clamped(3, 0, 62, 0);
                b.Rows = Clamped(4, 0, 22, 0);
                b.CodablockMode = UpperChar(p, 5) switch { 'A' => CodablockMode.A, 'E' => CodablockMode.E, _ => CodablockMode.F };
                break;
            case "BD": // m,n,t ; there is no orientation parameter
                b.Symbology = BarcodeSymbology.MaxiCode;
                b.MaxiCodeMode = Clamped(0, 2, 6, 2);
                b.MaxiCodeSymbolTotal = Clamped(2, 1, 8, 1);
                b.MaxiCodeSymbolNumber = Math.Min(Clamped(1, 1, 8, 1), b.MaxiCodeSymbolTotal);
                break;
            case "BT": // o,w1,r1,h1,w2,h2
                b.Symbology = BarcodeSymbology.Tlc39;
                Rot();
                b.ModuleWidth = Clamped(1, 1, 10, ByDpi(2, 2, 4));
                b.WideBarRatio = Opt(p, 2) is { } ratioText && double.TryParse(ratioText, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio) && double.IsFinite(ratio)
                    ? Math.Clamp(ratio, 2.0, 3.0) : 2.0;
                b.Height = Math.Clamp(Int(p, 3, c, scale: true) ?? ByDpi(40, 60, 120), 1, 9999);
                b.Tlc39MicroModuleWidth = Clamped(4, 1, 10, ByDpi(2, 2, 4));
                b.Tlc39MicroRowHeight = Clamped(5, 1, 255, ByDpi(4, 4, 8));
                break;
            case "B4": // o,h,f,m
                ModuleOnly(BarcodeSymbology.Code49);
                Rot();
                b.Height = HeightAt(1);
                (b.HumanReadable, b.HumanReadablePosition) = UpperChar(p, 2) switch
                {
                    'A' => (true, HumanReadablePosition.Above),
                    'B' => (true, HumanReadablePosition.Below),
                    _ => (false, HumanReadablePosition.Below),
                };
                b.Code49StartMode = Letter(3, "012345A", 'A');
                break;
        }
        return b;
    }

    private static bool BarcodeValidatorAztec(int value) => Barcodes.BarcodeValidator.IsValidAztecErrorControl(value);
}
