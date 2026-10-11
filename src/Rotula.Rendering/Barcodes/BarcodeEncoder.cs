using System.Globalization;
using Rotula.Core.Barcodes;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using ZXing;
using ZXing.Common;

namespace Rotula.Rendering.Barcodes;

/// <summary>How the rows of a <see cref="BarcodeMatrix"/> are turned into dots.</summary>
public enum BarcodeRowLayout
{
    /// <summary>One row, stretched to the bar height (linear symbologies).</summary>
    Stretched,
    /// <summary>Every module is square (QR Code, Data Matrix, Aztec).</summary>
    Square,
    /// <summary>PDF417: each matrix row is a quarter of the requested row height (ZXing repeats every row four times).</summary>
    Pdf417Rows,
    /// <summary>Each row has its own height in modules (<see cref="BarcodeMatrix.RowHeights"/>), for example stacked DataBar.</summary>
    ModuleRows,
    /// <summary>Hexagonal modules on a staggered grid (every odd row shifted half a module) around a central bullseye: MaxiCode.</summary>
    Hexagonal,
    /// <summary>The rows share the requested bar height in the proportions of <see cref="BarcodeMatrix.RowHeights"/> (POSTNET, PLANET).</summary>
    Proportional,
}

/// <summary>
/// A barcode as a grid of dark/light modules, with no quiet zone.
/// 1D symbols have a single row (<see cref="Height"/> == 1) that the renderer stretches to the requested bar height.
/// </summary>
public sealed class BarcodeMatrix
{
    private readonly bool[,] _dark;

    internal BarcodeMatrix(bool[,] dark, bool isLinear)
        : this(dark, isLinear ? BarcodeRowLayout.Stretched : BarcodeRowLayout.Square, null)
    {
    }

    internal BarcodeMatrix(bool[,] dark, BarcodeRowLayout layout, int[]? rowHeights)
    {
        _dark = dark;
        Layout = layout;
        RowHeights = rowHeights;
    }

    /// <summary>Number of modules across.</summary>
    public int Width => _dark.GetLength(0);

    /// <summary>Number of module rows (1 for linear symbologies).</summary>
    public int Height => _dark.GetLength(1);

    /// <summary>True for 1D symbologies (one repeated row).</summary>
    public bool IsLinear => Layout == BarcodeRowLayout.Stretched;

    /// <summary>How the rows map to dots.</summary>
    public BarcodeRowLayout Layout { get; }

    /// <summary>Row heights (modules, or relative weights for <see cref="BarcodeRowLayout.Proportional"/>), or null when every row is equal.</summary>
    public IReadOnlyList<int>? RowHeights { get; }

    /// <summary>True when the layout leaves room for a human-readable line above or below the bars.</summary>
    public bool HasTextBand => Layout is BarcodeRowLayout.Stretched or BarcodeRowLayout.Proportional;

    /// <summary>Returns true when the module at (x, y) is dark.</summary>
    public bool IsDark(int x, int y) => _dark[x, y];
}

/// <summary>Optional encoding parameters that only apply to some symbologies.</summary>
public sealed record BarcodeEncodeOptions
{
    /// <summary>QR error correction level.</summary>
    public QrErrorCorrection QrErrorCorrection { get; init; } = QrErrorCorrection.Quartile;

    /// <summary>PDF417 security (error correction) level, 0-8.</summary>
    public int Pdf417SecurityLevel { get; init; } = 2;

    /// <summary>PDF417 truncated (compact) variant.</summary>
    public bool Pdf417Truncate { get; init; }

    /// <summary>
    /// The symbology's optional check digit: Code 39 mod 43 and Interleaved 2 of 5 mod 10 are appended to the bars;
    /// for UPC-A, UPC-E and Plessey it only decides whether the check digit is written in the text line.
    /// </summary>
    public bool CheckDigit { get; init; }

    /// <summary>Code 11: number of check digits (1 or 2).</summary>
    public int Code11CheckDigits { get; init; } = 2;

    /// <summary>MSI check digit scheme.</summary>
    public MsiCheckDigitMode MsiCheckDigit { get; init; } = MsiCheckDigitMode.Mod10;

    /// <summary>MSI: show the check digits in the text line.</summary>
    public bool MsiCheckDigitInText { get; init; }

    /// <summary>Codabar start character, A-D.</summary>
    public char CodabarStart { get; init; } = 'A';

    /// <summary>Codabar stop character, A-D.</summary>
    public char CodabarStop { get; init; } = 'A';

    /// <summary>GS1 DataBar variant.</summary>
    public DataBarType DataBarType { get; init; } = DataBarType.Omnidirectional;

    /// <summary>GS1 DataBar Expanded: segments per row.</summary>
    public int DataBarSegmentsPerRow { get; init; } = 22;

    /// <summary>GS1 DataBar: separator height between stacked rows, in modules.</summary>
    public int DataBarSeparatorHeight { get; init; } = 1;

    /// <summary>Aztec error control and symbol size/type (see <c>BarcodeElement.AztecErrorControl</c>).</summary>
    public int AztecErrorControl { get; init; }

    /// <summary>MaxiCode mode, 2 to 6.</summary>
    public int MaxiCodeMode { get; init; } = 2;

    /// <summary>MaxiCode structured append: number of this symbol (1-8).</summary>
    public int MaxiCodeSymbolNumber { get; init; } = 1;

    /// <summary>MaxiCode structured append: total number of symbols (1-8).</summary>
    public int MaxiCodeSymbolTotal { get; init; } = 1;

    /// <summary>
    /// The data is a GS1 element string written with parentheses, and the symbol is a GS1 symbol: Code 128 becomes GS1-128 (FNC1 after the
    /// start character), Data Matrix gets FNC1 in the first position, QR Code gets the FNC1 first-position mode indicator. Ignored for
    /// other symbologies.
    /// </summary>
    public bool Gs1 { get; init; }
}

/// <summary>Raised by <see cref="BarcodeEncoder.Encode"/> when the data cannot be represented in the symbology.</summary>
public sealed class BarcodeEncodingException : Exception
{
    /// <summary>Creates the exception with a plain-language message.</summary>
    public BarcodeEncodingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Turns text into a <see cref="BarcodeMatrix"/>. ZXing.Net's encoders (Apache-2.0) are used where they exist and match ZPL;
/// the other symbologies are encoded by <see cref="LinearEncoders"/> and the DataBar encoder. Only encoding is done here;
/// all drawing is done by <see cref="LabelRenderer"/>.
/// </summary>
public static class BarcodeEncoder
{
    private const string Code39Alphabet = CheckDigits.Code39Alphabet;

    /// <summary>True for symbologies drawn as one row of bars.</summary>
    public static bool IsLinear(BarcodeSymbology symbology) => BarcodeCatalog.Get(symbology).Category == BarcodeCategory.Linear;

    /// <summary>
    /// True when this class can encode <paramref name="symbology"/> for the preview. When false, <see cref="Encode"/> throws and the
    /// designer shows a labelled placeholder instead; the printer still prints the symbol from the generated ZPL.
    /// </summary>
    public static bool HasPreviewEncoder(BarcodeSymbology symbology) => BarcodeCatalog.Get(symbology).HasPreviewEncoder;

    /// <summary>Encodes <paramref name="data"/>; throws <see cref="BarcodeEncodingException"/> when the data is invalid for the symbology.</summary>
    public static BarcodeMatrix Encode(BarcodeSymbology symbology, string data, BarcodeEncodeOptions? options = null)
    {
        options ??= new BarcodeEncodeOptions();
        if (string.IsNullOrEmpty(data))
            throw new BarcodeEncodingException("There is no data to encode in the barcode.");
        if (!HasPreviewEncoder(symbology))
            throw new BarcodeEncodingException($"{BarcodeCatalog.Get(symbology).DisplayName} has no preview in the designer; the printer draws it from the ZPL.");
        if (options.Gs1 && symbology is BarcodeSymbology.Code128 or BarcodeSymbology.DataMatrix or BarcodeSymbology.QrCode)
            return EncodeGs1(symbology, data, options);

        try
        {
            switch (symbology)
            {
                case BarcodeSymbology.Code11:
                    RequireDigits(data, allowed: "-");
                    return Linear(LinearEncoders.Code11(data, options.Code11CheckDigits is 1 ? 1 : 2));
                case BarcodeSymbology.Industrial2Of5:
                    RequireDigits(data);
                    return Linear(LinearEncoders.Industrial2Of5(data));
                case BarcodeSymbology.Standard2Of5:
                    RequireDigits(data);
                    return Linear(LinearEncoders.Standard2Of5(data));
                case BarcodeSymbology.Codabar:
                    foreach (char c in data) if ("0123456789-$:/.+".IndexOf(c) < 0) throw new BarcodeEncodingException($"The character \"{c}\" cannot be encoded in Codabar.");
                    return Linear(LinearEncoders.Codabar(StartStop(options.CodabarStart), data, StartStop(options.CodabarStop)));
                case BarcodeSymbology.Msi:
                    RequireDigits(data);
                    return Linear(LinearEncoders.Msi(data + LinearEncoders.MsiChecks(data, options.MsiCheckDigit)));
                case BarcodeSymbology.Plessey:
                    foreach (char c in data) if (!(c is >= '0' and <= '9' or >= 'A' and <= 'F')) throw new BarcodeEncodingException($"The character \"{c}\" cannot be encoded in Plessey (digits and A-F only).");
                    return Linear(LinearEncoders.Plessey(data));
                case BarcodeSymbology.UpcEanExtension:
                    RequireDigits(data);
                    if (data.Length is not (2 or 5)) throw new BarcodeEncodingException("A UPC/EAN extension has exactly 2 or 5 digits.");
                    return Linear(LinearEncoders.Extension(data));
                case BarcodeSymbology.Postnet:
                case BarcodeSymbology.Planet:
                    RequireDigits(data);
                    return Postal(LinearEncoders.PostalBars(data, symbology == BarcodeSymbology.Planet));
                case BarcodeSymbology.UpcE:
                    RequireDigits(data);
                    if (UpcE.TryNormalize(data, out string ten) is { } upcEProblem)
                        throw new BarcodeEncodingException($"The data \"{data}\" cannot be encoded as UPC-E ({upcEProblem}).");
                    data = "0" + UpcE.Compress(ten);
                    break;
                case BarcodeSymbology.Logmars:
                    // LOGMARS is Code 39 with a mandatory mod 43 check character; ZPL accepts lower case.
                    data = data.ToUpperInvariant();
                    data += CheckDigits.Mod43(data) ?? throw new BarcodeEncodingException("The data contains a character that LOGMARS cannot encode.");
                    break;
                case BarcodeSymbology.DataBar:
                case BarcodeSymbology.Aztec:
                case BarcodeSymbology.MicroPdf417:
                case BarcodeSymbology.Codablock:
                case BarcodeSymbology.MaxiCode:
                case BarcodeSymbology.Tlc39:
                case BarcodeSymbology.Code49:
                    return EncodeAdvanced(symbology, data, options);
                default:
                    data = WithCheckDigit(symbology, data, options);
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.FormatException or OverflowException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new BarcodeEncodingException($"The data \"{data}\" cannot be encoded as {symbology}: {ex.Message}", ex);
        }

        return EncodeWithZXing(symbology, data, options);
    }

    /// <summary>
    /// GS1 element strings in Code 128 (GS1-128), Data Matrix and QR Code. The data is parsed once; each carrier then gets the FNC1 it
    /// needs: ZXing's FNC1 character U+00F1 inside Code 128, the group separator inside QR Code, and codeword 232 in the Data Matrix.
    /// </summary>
    private static BarcodeMatrix EncodeGs1(BarcodeSymbology symbology, string data, BarcodeEncodeOptions options)
    {
        var message = Gs1Message.ParseBracketed(data, out _)
            ?? throw new BarcodeEncodingException("The GS1 data must be written like (01)09506000134352(10)ABC123: each identifier in parentheses, followed by its value.");
        switch (symbology)
        {
            case BarcodeSymbology.DataMatrix:
                return Trim(DataMatrixGs1Encoder.Encode(message));
            case BarcodeSymbology.Code128:
                // ZXing writes the FNC1 that follows the start character itself when GS1_FORMAT is set; the one between elements is U+00F1.
                return EncodeWithZXing(symbology, message.ToElementString('ñ'), options, gs1: true);
            default:
                return EncodeWithZXing(symbology, message.ToElementString(), options, gs1: true);
        }
    }

    private static char StartStop(char c) => c is >= 'A' and <= 'D' ? c : 'A';

    private static void RequireDigits(string data, string allowed = "")
    {
        foreach (char c in data)
            if (!(c is >= '0' and <= '9') && allowed.IndexOf(c) < 0)
                throw new BarcodeEncodingException($"The character \"{c}\" cannot be encoded in this barcode.");
    }

    private static BarcodeMatrix Linear(bool[] modules)
    {
        var dark = new bool[modules.Length, 1];
        for (int x = 0; x < modules.Length; x++) dark[x, 0] = modules[x];
        return new BarcodeMatrix(dark, true);
    }

    /// <summary>
    /// POSTNET/PLANET: every bar is one module wide followed by two modules of gap, so with ^BY3 there are about 22 bars per inch
    /// at 203 dpi, as the USPS specification asks. The tall bars use both rows, the short bars (40% of the height) only the lower one.
    /// </summary>
    private static BarcodeMatrix Postal(bool[] tall)
    {
        int width = tall.Length * 3 - 2;
        var dark = new bool[width, 2];
        for (int i = 0; i < tall.Length; i++)
        {
            dark[i * 3, 1] = true;
            dark[i * 3, 0] = tall[i];
        }
        return new BarcodeMatrix(dark, BarcodeRowLayout.Proportional, new[] { 3, 2 });
    }

    private static BarcodeMatrix EncodeAdvanced(BarcodeSymbology symbology, string data, BarcodeEncodeOptions options) => symbology switch
    {
        BarcodeSymbology.Aztec => EncodeAztec(data, options),
        BarcodeSymbology.DataBar => DataBarEncoder.Encode(options.DataBarType, data, options.DataBarSegmentsPerRow, options.DataBarSeparatorHeight),
        BarcodeSymbology.MaxiCode => new BarcodeMatrix(MaxiCodeEncoder.Encode(options.MaxiCodeMode, data, options.MaxiCodeSymbolNumber, options.MaxiCodeSymbolTotal),
            BarcodeRowLayout.Hexagonal, null),
        _ => throw new BarcodeEncodingException($"{BarcodeCatalog.Get(symbology).DisplayName} has no preview in the designer; the printer draws it from the ZPL."),
    };

    /// <summary>
    /// Aztec Code through ZXing.Net. ZPL's error control value maps to ZXing's two hints: a percentage, or a layer count
    /// (negative for compact symbols). The Rune, menu, structured append and ECI variants are not reproduced.
    /// </summary>
    private static BarcodeMatrix EncodeAztec(string data, BarcodeEncodeOptions options)
    {
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        int control = options.AztecErrorControl;
        // The Aztec specification's default is 23% error correction plus three codewords; ZXing's own default is 33%.
        int ecc = 23;
        if (control is >= 1 and <= 99) ecc = control;
        hints[EncodeHintType.ERROR_CORRECTION] = ecc;
        if (control is >= 101 and <= 104) hints[EncodeHintType.AZTEC_LAYERS] = -(control - 100);
        else if (control is >= 201 and <= 232) hints[EncodeHintType.AZTEC_LAYERS] = control - 200;
        // Printers in ^CI28 mode send UTF-8 bytes, so non-ASCII text must be encoded the same way here.
        if (data.Any(c => c > 127)) hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
        var bits = new MultiFormatWriter().encode(data, BarcodeFormat.AZTEC, 0, 0, hints);
        // Not trimmed: unlike PDF417 or Data Matrix, an Aztec symbol has no guaranteed dark corner, and ZXing already returns it
        // without a margin.
        var dark = new bool[bits.Width, bits.Height];
        for (int y = 0; y < bits.Height; y++)
            for (int x = 0; x < bits.Width; x++) dark[x, y] = bits[x, y];
        return new BarcodeMatrix(dark, BarcodeRowLayout.Square, null);
    }

    private static BarcodeMatrix EncodeWithZXing(BarcodeSymbology symbology, string data, BarcodeEncodeOptions options, bool gs1 = false)
    {
        var linear = IsLinear(symbology);

        // Margin 0: printers do not add a quiet zone, the label background provides it.
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        if (gs1) hints[EncodeHintType.GS1_FORMAT] = true;
        // Printers in ^CI28 mode send UTF-8 bytes without an ECI header, so text beyond Latin-1 is encoded the same way here.
        if (symbology == BarcodeSymbology.QrCode && data.Any(c => c > 127))
        {
            hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
            hints[EncodeHintType.DISABLE_ECI] = true;
        }
        if (symbology == BarcodeSymbology.QrCode)
            hints[EncodeHintType.ERROR_CORRECTION] = options.QrErrorCorrection switch
            {
                QrErrorCorrection.Low => ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
                QrErrorCorrection.Medium => ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
                QrErrorCorrection.High => ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
                _ => ZXing.QrCode.Internal.ErrorCorrectionLevel.Q,
            };
        // ZPL ^BX prints square ECC 200 symbols unless rows/columns are forced, but ZXing would otherwise
        // happily pick a smaller rectangular one.
        if (symbology == BarcodeSymbology.DataMatrix)
            hints[EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_SQUARE;
        if (symbology == BarcodeSymbology.Pdf417)
        {
            hints[EncodeHintType.PDF417_COMPACT] = options.Pdf417Truncate;
            hints[EncodeHintType.ERROR_CORRECTION] = Math.Clamp(options.Pdf417SecurityLevel, 0, 8);
        }

        BitMatrix bits;
        try
        {
            // Width/height 0 asks for the natural size (1 pixel per module) for the symbologies that honour it.
            bits = new MultiFormatWriter().encode(data, ToZXingFormat(symbology), 0, 0, hints);
        }
        catch (Exception ex) when (ex is ArgumentException or WriterException or System.FormatException or ZXing.FormatException)
        {
            throw new BarcodeEncodingException($"The data \"{data}\" cannot be encoded as {symbology}: {ex.Message}", ex);
        }

        var matrix = ToMatrix(bits, linear);
        return symbology == BarcodeSymbology.Pdf417
            ? new BarcodeMatrix(Copy(matrix), BarcodeRowLayout.Pdf417Rows, null)
            : matrix;
    }

    private static bool[,] Copy(BarcodeMatrix m)
    {
        var copy = new bool[m.Width, m.Height];
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++) copy[x, y] = m.IsDark(x, y);
        return copy;
    }

    /// <summary>Like <see cref="Encode"/> but returns null instead of throwing, and gives the reason.</summary>
    public static BarcodeMatrix? TryEncode(BarcodeSymbology symbology, string data, BarcodeEncodeOptions? options, out string? error)
    {
        try
        {
            error = null;
            return Encode(symbology, data, options);
        }
        catch (BarcodeEncodingException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The text printed next to a linear barcode. It follows what the ZPL guide's samples show: EAN/UPC symbols and
    /// LOGMARS include the digit or character the printer computes, Codabar shows its start and stop letters, and
    /// POSTNET/PLANET show the digits as typed.
    /// </summary>
    public static string HumanReadableText(BarcodeSymbology symbology, string data, BarcodeEncodeOptions? options = null)
    {
        options ??= new BarcodeEncodeOptions();
        bool digits = data.All(char.IsAsciiDigit);
        switch (symbology)
        {
            // GS1-128 shows the identifiers in parentheses, as ^BC mode D prints them.
            case BarcodeSymbology.Code128 when options.Gs1 && Gs1Message.ParseBracketed(data, out _) is { } gs1Message:
                return gs1Message.ToHumanReadable();
            case BarcodeSymbology.Ean13 when data.Length == 12 && digits:
                return data + Gs1CheckDigit(data);
            case BarcodeSymbology.UpcA when data.Length == 11 && digits:
                return data + Gs1CheckDigit(data);
            case BarcodeSymbology.Ean8 when data.Length == 7 && digits:
                return Group8(data + Gs1CheckDigit(data));
            case BarcodeSymbology.Ean8 when data.Length == 8:
                return Group8(data);
            case BarcodeSymbology.UpcE when digits && UpcE.TryNormalize(data, out string ten) is null:
                // Number system, the six printed digits, and (when asked for) the check digit.
                string text = "0 " + UpcE.Compress(ten);
                return options.CheckDigit ? text + " " + UpcE.CheckDigit(ten).ToString(CultureInfo.InvariantCulture) : text;
            case BarcodeSymbology.Code11 when data.All(c => c is >= '0' and <= '9' or '-'):
                return data + CheckDigits.Code11(data, options.Code11CheckDigits is 1 ? 1 : 2);
            case BarcodeSymbology.Codabar:
                return StartStop(options.CodabarStart) + data + StartStop(options.CodabarStop);
            case BarcodeSymbology.Msi when digits:
                return options.MsiCheckDigitInText ? data + LinearEncoders.MsiChecks(data, options.MsiCheckDigit) : data;
            case BarcodeSymbology.Plessey when data.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'):
                return options.CheckDigit ? data + CheckDigits.PlesseyCheckText(data) : data;
            case BarcodeSymbology.Logmars:
                string upper = data.ToUpperInvariant();
                return CheckDigits.Mod43(upper) is { } mod43 ? upper + mod43 : upper;
            default:
                return WithCheckDigit(symbology, data, options);
        }
    }

    private static string Group8(string eight) => eight[..4] + " " + eight[4..];

    /// <summary>Standard GS1 mod-10 check digit for the digits of an EAN/UPC/ITF body.</summary>
    public static char Gs1CheckDigit(string body)
    {
        int sum = 0;
        for (int i = 0; i < body.Length; i++)
        {
            int digit = body[i] - '0';
            // Weights alternate 3,1,3,... counted from the rightmost body digit.
            sum += ((body.Length - i) % 2 == 1) ? digit * 3 : digit;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>The printer appends optional check digits itself; the preview mimics that so the bars match.</summary>
    private static string WithCheckDigit(BarcodeSymbology symbology, string data, BarcodeEncodeOptions options)
    {
        if (!options.CheckDigit) return data;
        switch (symbology)
        {
            case BarcodeSymbology.Interleaved2Of5 when data.All(char.IsAsciiDigit):
                return data + Gs1CheckDigit(data);
            case BarcodeSymbology.Code39:
                // An invalid character: leave the data alone and let the encoder report it.
                return CheckDigits.Mod43(data) is { } check ? data + check : data;
            default:
                return data;
        }
    }

    private static BarcodeFormat ToZXingFormat(BarcodeSymbology symbology) => symbology switch
    {
        BarcodeSymbology.Code128 => BarcodeFormat.CODE_128,
        BarcodeSymbology.Code39 or BarcodeSymbology.Logmars => BarcodeFormat.CODE_39,
        BarcodeSymbology.Code93 => BarcodeFormat.CODE_93,
        BarcodeSymbology.Ean13 => BarcodeFormat.EAN_13,
        BarcodeSymbology.Ean8 => BarcodeFormat.EAN_8,
        BarcodeSymbology.UpcA => BarcodeFormat.UPC_A,
        BarcodeSymbology.UpcE => BarcodeFormat.UPC_E,
        BarcodeSymbology.Interleaved2Of5 => BarcodeFormat.ITF,
        BarcodeSymbology.QrCode => BarcodeFormat.QR_CODE,
        BarcodeSymbology.DataMatrix => BarcodeFormat.DATA_MATRIX,
        BarcodeSymbology.Pdf417 => BarcodeFormat.PDF_417,
        _ => throw new ArgumentOutOfRangeException(nameof(symbology)),
    };

    private static BarcodeMatrix ToMatrix(BitMatrix bits, bool linear)
    {
        int w = bits.Width;
        int h = linear ? 1 : bits.Height;
        var dark = new bool[w, h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dark[x, y] = bits[x, y];

        if (linear)
            return new BarcodeMatrix(dark, true);

        // Some 2D writers (Data Matrix, PDF417) may still return a blank border or scaled output.
        // Symbol corners are always dark (finder / start patterns), so trimming blank rows and columns
        // recovers the exact symbol without quiet zone.
        return Trim(dark);
    }

    private static BarcodeMatrix Trim(bool[,] src)
    {
        int w = src.GetLength(0), h = src.GetLength(1);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (src[x, y])
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0)
            throw new BarcodeEncodingException("The barcode encoder produced an empty symbol.");

        var trimmed = new bool[maxX - minX + 1, maxY - minY + 1];
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                trimmed[x - minX, y - minY] = src[x, y];
        return new BarcodeMatrix(trimmed, false);
    }
}
