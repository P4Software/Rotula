using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Barcodes;

/// <summary>GS1 / UPC modulo-10 check digit helpers.</summary>
public static class CheckDigits
{
    /// <summary>
    /// Computes the GS1 check digit for a payload of digits (EAN-13: 12 digits, UPC-A: 11 digits).
    /// Weights alternate 3,1,3,1 starting with 3 at the rightmost payload digit.
    /// </summary>
    public static int Gs1(string payload)
    {
        int sum = 0;
        bool three = true;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            sum += (payload[i] - '0') * (three ? 3 : 1);
            three = !three;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>True when the last digit of <paramref name="full"/> is the correct GS1 check digit for the rest.</summary>
    public static bool IsValidGs1(string full) =>
        full.Length >= 2 && Gs1(full[..^1]) == full[^1] - '0';
}

/// <summary>
/// Validates barcode data and options per symbology. Results are structured issues with message keys
/// (never UI text) so the UI can localize them.
/// </summary>
public static class BarcodeValidator
{
    private const string Code39Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-. $/+%";

    /// <summary>Validates the options of <paramref name="barcode"/> and the already variable-resolved <paramref name="data"/>.</summary>
    public static List<ValidationIssue> Validate(BarcodeElement barcode, string data)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        var issues = new List<ValidationIssue>();
        string id = barcode.Id;
        void Err(string key, params string[] args) => issues.Add(new ValidationIssue(ValidationSeverity.Error, key, id, args));

        if (data.Length == 0) Err("Barcode.Data.Empty");

        bool linear = barcode.Symbology is BarcodeSymbology.Code128 or BarcodeSymbology.Code39 or BarcodeSymbology.Code93
            or BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA or BarcodeSymbology.Interleaved2Of5;
        bool usesModuleWidth = linear || barcode.Symbology == BarcodeSymbology.Pdf417;

        if (usesModuleWidth && (barcode.ModuleWidth < 1 || barcode.ModuleWidth > 10)) Err("Barcode.ModuleWidth.Range", "1", "10");
        if (linear && (barcode.WideBarRatio < 2.0 || barcode.WideBarRatio > 3.0)) Err("Barcode.WideBarRatio.Range", "2.0", "3.0");
        if ((linear || barcode.Symbology == BarcodeSymbology.Pdf417) && (barcode.Height < 1 || barcode.Height > 32000)) Err("Barcode.Height.Range", "1", "32000");

        if (data.Length > 0)
        {
            switch (barcode.Symbology)
            {
                case BarcodeSymbology.Code128:
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
                    ValidateGtin(data, 12, 13, "Ean13", Err);
                    break;
                case BarcodeSymbology.UpcA:
                    ValidateGtin(data, 11, 12, "UpcA", Err);
                    break;
                case BarcodeSymbology.Interleaved2Of5:
                    if (!AllDigits(data)) Err("Barcode.Digits.Only");
                    else
                    {
                        // Digits are encoded in pairs; the printer appends the check digit, so the user data must be odd then.
                        bool needOdd = barcode.CheckDigit;
                        if ((data.Length % 2 == 1) != needOdd) Err("Barcode.I25.Length", needOdd ? "odd" : "even");
                    }
                    break;
                case BarcodeSymbology.QrCode:
                    if (barcode.QrModel is not (1 or 2)) Err("Barcode.Qr.Model");
                    if (barcode.ModuleSize < 1 || barcode.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                    if (data.Length > 4296) Err("Barcode.Qr.TooLong", "4296");
                    break;
                case BarcodeSymbology.DataMatrix:
                    if (barcode.ModuleSize < 1 || barcode.ModuleSize > 10) Err("Barcode.ModuleSize.Range", "1", "10");
                    if (barcode.Columns < 0 || barcode.Columns > 144) Err("Barcode.Columns.Range", "0", "144");
                    if (barcode.Rows < 0 || barcode.Rows > 144) Err("Barcode.Rows.Range", "0", "144");
                    if (data.Length > 2335) Err("Barcode.DataMatrix.TooLong", "2335");
                    break;
                case BarcodeSymbology.Pdf417:
                    if (barcode.Pdf417SecurityLevel < 1 || barcode.Pdf417SecurityLevel > 8) Err("Barcode.Pdf417.Security", "1", "8");
                    if (barcode.Columns < 0 || barcode.Columns > 30) Err("Barcode.Columns.Range", "0", "30");
                    if (barcode.Rows != 0 && (barcode.Rows < 3 || barcode.Rows > 90)) Err("Barcode.Rows.Range", "3", "90");
                    if (data.Length > 1850) Err("Barcode.Pdf417.TooLong", "1850");
                    break;
                default:
                    Err("Barcode.Symbology.Unknown");
                    break;
            }
        }
        return issues;
    }

    /// <summary>
    /// EAN-13 / UPC-A rules: digits only; either the payload length (printer computes the check digit)
    /// or payload + 1 with a correct check digit.
    /// </summary>
    private static void ValidateGtin(string data, int payloadLength, int fullLength, string name, Action<string, string[]> err)
    {
        if (!AllDigits(data)) { err("Barcode.Digits.Only", Array.Empty<string>()); return; }
        if (data.Length != payloadLength && data.Length != fullLength)
        {
            err($"Barcode.{name}.Length", new[] { payloadLength.ToString(System.Globalization.CultureInfo.InvariantCulture), fullLength.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return;
        }
        if (data.Length == fullLength && !CheckDigits.IsValidGs1(data))
            err($"Barcode.{name}.CheckDigit", new[] { CheckDigits.Gs1(data[..^1]).ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    private static bool AllDigits(string s) => s.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// Returns the data exactly as it must be put into ^FD: EAN-13 / UPC-A lose their check digit
    /// (the printer recomputes it) so that output is identical whether the user typed it or not.
    /// </summary>
    public static string NormalizeForPrinter(BarcodeSymbology symbology, string data) => symbology switch
    {
        BarcodeSymbology.Ean13 when data.Length == 13 => data[..12],
        BarcodeSymbology.UpcA when data.Length == 12 => data[..11],
        _ => data,
    };
}
