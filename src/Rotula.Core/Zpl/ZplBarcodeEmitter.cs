using System.Globalization;
using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using static System.FormattableString;

namespace Rotula.Core.Zpl;

/// <summary>
/// Writes the ZPL command (and ^BY) for one <see cref="BarcodeElement"/>. The parameter order and the defaults of every command
/// follow the ZPL II Programming Guide; trailing parameters that are empty are dropped so the output stays short and stable.
/// </summary>
internal static class ZplBarcodeEmitter
{
    /// <summary>Placeholder in a QR header for the four-digit byte count of manual byte input; the generator fills it in.</summary>
    internal const string QrByteCountToken = "{QrByteCount}";

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string YN(bool v) => v ? "Y" : "N";

    /// <summary>Joins command parameters and drops trailing empty ones.</summary>
    internal static string Params(params string[] ps)
    {
        int n = ps.Length;
        while (n > 0 && ps[n - 1].Length == 0) n--;
        return string.Join(',', ps.Take(n));
    }

    /// <summary>
    /// Returns what goes between the field origin and ^FD: the optional ^BY and the barcode command.
    /// <paramref name="fdPrefix"/> receives text that must precede the data inside ^FD (the QR header).
    /// </summary>
    /// <param name="b">The barcode.</param>
    /// <param name="rot">The orientation letter, or empty to inherit ^FW.</param>
    /// <param name="documentRotation">The document default rotation letter, used where ZPL's own default is not ^FW.</param>
    /// <param name="fdPrefix">Header text for the field data.</param>
    internal static string Command(BarcodeElement b, string rot, string documentRotation, out string fdPrefix)
    {
        fdPrefix = string.Empty;
        string above = YN(b.HumanReadablePosition == HumanReadablePosition.Above);
        string hr = YN(b.HumanReadable);
        string h = Int(b.Height);
        string by = Invariant($"^BY{b.ModuleWidth},{b.WideBarRatio:0.0}");
        string byWidthOnly = Invariant($"^BY{b.ModuleWidth}");

        switch (b.Symbology)
        {
            case BarcodeSymbology.Code128:
                char mode = b.Code128Mode switch { Code128Mode.Ucc => 'U', Code128Mode.Automatic => 'A', Code128Mode.Ean => 'D', _ => 'N' };
                return by + "^BC" + Params(rot, h, hr, above, "N", mode.ToString());
            case BarcodeSymbology.Code39:
                return by + "^B3" + Params(rot, YN(b.CheckDigit), h, hr, above);
            case BarcodeSymbology.Code93:
                return by + "^BA" + Params(rot, h, hr, above, YN(b.CheckDigit));
            case BarcodeSymbology.Ean13:
                return by + "^BE" + Params(rot, h, hr, above);
            case BarcodeSymbology.UpcA:
                return by + "^BU" + Params(rot, h, hr, above, YN(b.CheckDigit));
            case BarcodeSymbology.Interleaved2Of5:
                return by + "^B2" + Params(rot, h, hr, above, YN(b.CheckDigit));
            case BarcodeSymbology.QrCode:
                // QR data carries its own header: error correction letter + "A" (automatic input mode) + comma.
                // The header is the error correction letter, then the data input: "A," (automatic) or "M," and a character mode. For the byte
                // mode the count (four digits) is only known with the data, so the generator replaces the byte count token.
                fdPrefix = (b.QrErrorCorrection switch { QrErrorCorrection.Low => "L", QrErrorCorrection.Medium => "M", QrErrorCorrection.High => "H", _ => "Q" })
                    + b.QrInputMode switch
                    {
                        QrInputMode.ManualNumeric => "M,N", QrInputMode.ManualAlphanumeric => "M,A", QrInputMode.ManualByte => "M,B" + QrByteCountToken,
                        QrInputMode.ManualKanji => "M,K", _ => "A,",
                    };
                // ^BQ a,b,c,d,e: d (error correction) is left empty because the header above carries it; e is the mask, 0-7.
                return "^BQ" + Params(rot, Int(b.QrModel), Int(b.ModuleSize), string.Empty, b.QrMask is >= 0 and <= 7 ? Int(b.QrMask) : string.Empty);
            case BarcodeSymbology.DataMatrix:
                // Parameter g (the escape character, 7th) is only written for GS1 DataMatrix, where the field data uses ~1 for FNC1.
                return "^BX" + Params(rot, Int(b.ModuleSize), "200",
                    b.Columns > 0 ? Int(b.Columns) : string.Empty,
                    b.Rows > 0 ? Int(b.Rows) : string.Empty,
                    string.Empty,
                    b.DataMatrixGs1 ? Gs1ZplData.DataMatrixEscape.ToString() : string.Empty);
            case BarcodeSymbology.Pdf417:
                return byWidthOnly + "^B7" + Params(rot, h, Int(b.Pdf417SecurityLevel),
                    b.Columns > 0 ? Int(b.Columns) : string.Empty,
                    b.Rows > 0 ? Int(b.Rows) : string.Empty,
                    YN(b.Pdf417Truncate));

            // ^B1 e: Y = one check digit, N = two (the guide's wording; the default is two).
            case BarcodeSymbology.Code11:
                return by + "^B1" + Params(rot, YN(b.Code11CheckDigitCount == 1), h, hr, above);
            case BarcodeSymbology.Industrial2Of5:
                return by + "^BI" + Params(rot, h, hr, above);
            case BarcodeSymbology.Standard2Of5:
                return by + "^BJ" + Params(rot, h, hr, above);
            // ^BL has no interpretation-line switch: it takes only orientation, height and "line above".
            case BarcodeSymbology.Logmars:
                return by + "^BL" + Params(rot, h, above);
            // ^BK e is fixed at N; k and l choose the start and stop characters.
            case BarcodeSymbology.Codabar:
                return by + "^BK" + Params(rot, "N", h, hr, above, b.CodabarStart.ToString(), b.CodabarStop.ToString());
            case BarcodeSymbology.Msi:
                string msi = b.MsiCheckDigit switch { MsiCheckDigitMode.None => "A", MsiCheckDigitMode.DoubleMod10 => "C", MsiCheckDigitMode.Mod11Mod10 => "D", _ => "B" };
                return by + "^BM" + Params(rot, msi, h, hr, above, YN(b.MsiCheckDigitInText));
            case BarcodeSymbology.Plessey:
                return by + "^BP" + Params(rot, YN(b.CheckDigit), h, hr, above);
            case BarcodeSymbology.Ean8:
                return by + "^B8" + Params(rot, h, hr, above);
            case BarcodeSymbology.UpcE:
                return by + "^B9" + Params(rot, h, hr, above, YN(b.CheckDigit));
            case BarcodeSymbology.UpcEanExtension:
                return by + "^BS" + Params(rot, h, hr, above);
            case BarcodeSymbology.Postnet:
                return by + "^BZ" + Params(rot, h, hr, above, "0");
            case BarcodeSymbology.Planet:
                return by + "^B5" + Params(rot, h, hr, above);
            case BarcodeSymbology.DataBar:
                // ^BR defaults to rotated (R), not to ^FW, so the orientation is always written out.
                return "^BR" + Params(rot.Length > 0 ? rot : documentRotation, Int((int)b.DataBarType), Int(b.ModuleSize), Int(b.DataBarSeparatorHeight), h,
                    b.DataBarType == DataBarType.Expanded ? Int(b.DataBarSegmentsPerRow) : string.Empty);
            case BarcodeSymbology.Aztec:
                return "^BO" + Params(rot, Int(b.ModuleSize), YN(b.AztecEci), Int(b.AztecErrorControl), YN(b.AztecMenuSymbol),
                    Int(b.AztecStructuredAppendCount), b.AztecStructuredAppendId);
            case BarcodeSymbology.MicroPdf417:
                return byWidthOnly + "^BF" + Params(rot, h, Int(b.MicroPdf417Mode));
            case BarcodeSymbology.Codablock:
                string cbMode = b.CodablockMode switch { CodablockMode.A => "A", CodablockMode.E => "E", _ => "F" };
                return byWidthOnly + "^BB" + Params(rot, h, YN(b.CodablockSecurity), b.Columns > 0 ? Int(b.Columns) : string.Empty,
                    b.Rows > 0 ? Int(b.Rows) : string.Empty, cbMode);
            // ^BD has no orientation parameter: the first parameter is the mode.
            case BarcodeSymbology.MaxiCode:
                return "^BD" + Params(Int(b.MaxiCodeMode), Int(b.MaxiCodeSymbolNumber), Int(b.MaxiCodeSymbolTotal));
            case BarcodeSymbology.Tlc39:
                return "^BT" + Params(rot, Int(b.ModuleWidth), Invariant($"{b.WideBarRatio:0.0}"), h, Int(b.Tlc39MicroModuleWidth), Int(b.Tlc39MicroRowHeight));
            case BarcodeSymbology.Code49:
                string c49Line = !b.HumanReadable ? "N" : b.HumanReadablePosition == HumanReadablePosition.Above ? "A" : "B";
                return byWidthOnly + "^B4" + Params(rot, h, c49Line, b.Code49StartMode.ToString());
            default:
                throw new ArgumentOutOfRangeException(nameof(b), b.Symbology, "No ZPL command is known for this symbology.");
        }
    }
}
