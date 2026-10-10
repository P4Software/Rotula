using Rotula.Core.Model;
using ZXing;
using ZXing.Common;

namespace Rotula.Rendering.Barcodes;

/// <summary>
/// A barcode as a grid of dark/light modules, with no quiet zone.
/// 1D symbols have a single row (<see cref="Height"/> == 1) that the renderer stretches to the requested bar height.
/// </summary>
public sealed class BarcodeMatrix
{
    private readonly bool[,] _dark;

    internal BarcodeMatrix(bool[,] dark, bool isLinear)
    {
        _dark = dark;
        IsLinear = isLinear;
    }

    /// <summary>Number of modules across.</summary>
    public int Width => _dark.GetLength(0);

    /// <summary>Number of module rows (1 for linear symbologies).</summary>
    public int Height => _dark.GetLength(1);

    /// <summary>True for 1D symbologies (one repeated row).</summary>
    public bool IsLinear { get; }

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

    /// <summary>Append the symbology's optional check digit (Code 39 mod 43, Interleaved 2 of 5 mod 10).</summary>
    public bool CheckDigit { get; init; }
}

/// <summary>Raised by <see cref="BarcodeEncoder.Encode"/> when the data cannot be represented in the symbology.</summary>
public sealed class BarcodeEncodingException : Exception
{
    /// <summary>Creates the exception with a plain-language message.</summary>
    public BarcodeEncodingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Turns text into a <see cref="BarcodeMatrix"/> using ZXing.Net's encoders (Apache-2.0).
/// Only encoding is used; all drawing is done by <see cref="LabelRenderer"/>.
/// </summary>
public static class BarcodeEncoder
{
    private const string Code39Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>True for symbologies drawn as one row of bars.</summary>
    public static bool IsLinear(BarcodeSymbology symbology) =>
        symbology is not (BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix or BarcodeSymbology.Pdf417);

    /// <summary>Encodes <paramref name="data"/>; throws <see cref="BarcodeEncodingException"/> when the data is invalid for the symbology.</summary>
    public static BarcodeMatrix Encode(BarcodeSymbology symbology, string data, BarcodeEncodeOptions? options = null)
    {
        options ??= new BarcodeEncodeOptions();
        if (string.IsNullOrEmpty(data))
            throw new BarcodeEncodingException("There is no data to encode in the barcode.");

        data = WithCheckDigit(symbology, data, options);
        var linear = IsLinear(symbology);

        // Margin 0: printers do not add a quiet zone, the label background provides it.
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
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

        return ToMatrix(bits, linear);
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

    /// <summary>The text printed under a linear barcode: EAN-13 and UPC-A show their computed check digit.</summary>
    public static string HumanReadableText(BarcodeSymbology symbology, string data, BarcodeEncodeOptions? options = null) =>
        symbology switch
        {
            BarcodeSymbology.Ean13 when data.Length == 12 && data.All(char.IsAsciiDigit) => data + Gs1CheckDigit(data),
            BarcodeSymbology.UpcA when data.Length == 11 && data.All(char.IsAsciiDigit) => data + Gs1CheckDigit(data),
            _ => WithCheckDigit(symbology, data, options ?? new BarcodeEncodeOptions()),
        };

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
                int sum = 0;
                foreach (var c in data)
                {
                    int idx = Code39Alphabet.IndexOf(char.ToUpperInvariant(c));
                    if (idx < 0) return data; // invalid character: let the encoder report it
                    sum += idx;
                }
                return data + Code39Alphabet[sum % 43];
            default:
                return data;
        }
    }

    private static BarcodeFormat ToZXingFormat(BarcodeSymbology symbology) => symbology switch
    {
        BarcodeSymbology.Code128 => BarcodeFormat.CODE_128,
        BarcodeSymbology.Code39 => BarcodeFormat.CODE_39,
        BarcodeSymbology.Code93 => BarcodeFormat.CODE_93,
        BarcodeSymbology.Ean13 => BarcodeFormat.EAN_13,
        BarcodeSymbology.UpcA => BarcodeFormat.UPC_A,
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
