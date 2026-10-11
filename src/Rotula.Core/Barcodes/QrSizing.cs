using System.Globalization;
using System.Text;
using Rotula.Core.Model;

namespace Rotula.Core.Barcodes;

/// <summary>The character mode a QR payload is encoded in for the capacity estimate.</summary>
public enum QrDataMode
{
    /// <summary>Digits only.</summary>
    Numeric,
    /// <summary>Digits, capital letters, space and <c>$ % * + - . / :</c>.</summary>
    Alphanumeric,
    /// <summary>Anything else, counted as UTF-8 bytes (what the printer receives under ^CI28).</summary>
    Byte,
}

/// <summary>One value of a QR option, with the key the application translates and an English name for tools and documentation.</summary>
/// <param name="Value">The value as stored (a letter, a number or an enum name).</param>
/// <param name="LabelKey">Resource key of the display name.</param>
/// <param name="English">English display name.</param>
/// <param name="Detail">A number that goes with the choice (the recovery percentage of a level), or 0.</param>
public sealed record QrChoice(string Value, string LabelKey, string English, int Detail = 0);

/// <summary>Result of <see cref="QrSizing.Analyze"/>.</summary>
/// <param name="Fits">True when the payload fits in the largest symbol of the model at the level.</param>
/// <param name="Version">Smallest version (1-40) that holds the payload; 0 when it does not fit, or for model 1 where only the largest symbol (version 14) is tabulated.</param>
/// <param name="Modules">Modules per side of that symbol (17 + 4 x version), or 0.</param>
/// <param name="UsedPercent">Share of the capacity of the largest symbol that the payload uses, 0-100 (above 100 when it does not fit).</param>
/// <param name="Mode">The character mode the estimate used.</param>
/// <param name="Units">Characters (numeric, alphanumeric) or bytes (byte mode) of the payload.</param>
/// <param name="MaxUnits">What the largest symbol holds in this mode at this level.</param>
/// <param name="Level">The error correction level analysed.</param>
/// <param name="Model">1 or 2.</param>
public sealed record QrFit(bool Fits, int Version, int Modules, double UsedPercent, QrDataMode Mode, int Units, int MaxUnits, QrErrorCorrection Level, int Model);

/// <summary>
/// Everything the QR options panel needs besides the values themselves: the choices with their names, how much a payload needs, how large
/// the printed symbol is, and which error correction level to suggest. Capacities are for the whole payload in one character mode; a
/// printer may mix modes inside one symbol and so fit slightly more than the estimate says, never less.
/// </summary>
/// <remarks>
/// Numbers relied on (ISO/IEC 18004 model 2, the same tables ZXing.Net uses): data codewords per version and level (below), 4 mode bits,
/// character count bits 10/12/14 (numeric), 9/11/13 (alphanumeric), 8/16/16 (byte) for versions 1-9, 10-26, 27-40, and 10, 11 and 8 bits for
/// 3 digits, 2 alphanumeric characters and 1 byte. At version 40 this gives 7089/5596/3993/3057 digits, 4296/3391/2420/1852 alphanumeric
/// characters and 2953/2331/1663/1273 bytes for L/M/Q/H. Model 1 (version 14 at most) uses the tabulated limits of JIS X 0510.
/// </remarks>
public static class QrSizing
{
    private static readonly int[][] DataCodewords =
    {
        new[] { 19, 34, 55, 80, 108, 136, 156, 194, 232, 274, 324, 370, 428, 461, 523, 589, 647, 721, 795, 861, 932, 1006, 1094, 1174, 1276, 1370, 1468, 1531, 1631, 1735, 1843, 1955, 2071, 2191, 2306, 2434, 2566, 2702, 2812, 2956 },
        new[] { 16, 28, 44, 64, 86, 108, 124, 154, 182, 216, 254, 290, 334, 365, 415, 453, 507, 563, 627, 669, 714, 782, 860, 914, 1000, 1062, 1128, 1193, 1267, 1373, 1455, 1541, 1631, 1725, 1812, 1914, 1992, 2102, 2216, 2334 },
        new[] { 13, 22, 34, 48, 62, 76, 88, 110, 132, 154, 180, 206, 244, 261, 295, 325, 367, 397, 445, 485, 512, 568, 614, 664, 718, 754, 808, 871, 911, 985, 1033, 1115, 1171, 1231, 1286, 1354, 1426, 1502, 1582, 1666 },
        new[] { 9, 16, 26, 36, 46, 60, 66, 86, 100, 122, 140, 158, 180, 197, 223, 253, 283, 313, 341, 385, 406, 442, 464, 514, 538, 596, 628, 661, 701, 745, 793, 845, 901, 961, 986, 1054, 1096, 1142, 1222, 1276 },
    };

    // Model 1, version 14 (the largest): numeric, alphanumeric, byte for L, M, Q, H.
    private static readonly int[][] Model1Limits = { new[] { 1167, 707, 486 }, new[] { 897, 543, 373 }, new[] { 639, 387, 266 }, new[] { 507, 307, 211 } };

    private static int Row(QrErrorCorrection level) => level switch { QrErrorCorrection.Low => 0, QrErrorCorrection.Medium => 1, QrErrorCorrection.Quartile => 2, _ => 3 };

    /// <summary>The four error correction levels, from the lowest to the highest, with the share of the symbol the code can recover.</summary>
    public static IReadOnlyList<QrChoice> ErrorCorrectionLevels { get; } = new[]
    {
        new QrChoice("Low", "Qr.Level.L", "L - Low (7%)", 7),
        new QrChoice("Medium", "Qr.Level.M", "M - Medium (15%)", 15),
        new QrChoice("Quartile", "Qr.Level.Q", "Q - Quartile (25%)", 25),
        new QrChoice("High", "Qr.Level.H", "H - High (30%)", 30),
    };

    /// <summary>QR Code models (^BQ parameter b): 2 is the enhanced model and the one to use.</summary>
    public static IReadOnlyList<QrChoice> Models { get; } = new[]
    {
        new QrChoice("1", "Qr.Model.1", "Model 1 (original)"),
        new QrChoice("2", "Qr.Model.2", "Model 2 (enhanced, recommended)"),
    };

    /// <summary>Mask patterns (^BQ parameter e): "-1" leaves the parameter out, so the printer uses its default, 7. The guide offers no automatic selection.</summary>
    public static IReadOnlyList<QrChoice> Masks { get; } =
        new[] { new QrChoice("-1", "Qr.Mask.Default", "Printer default (7)") }
            .Concat(Enumerable.Range(0, 8).Select(m => new QrChoice(m.ToString(CultureInfo.InvariantCulture), "Qr.Mask." + m.ToString(CultureInfo.InvariantCulture), "Mask " + m.ToString(CultureInfo.InvariantCulture))))
            .ToList();

    /// <summary>Data input choices, with the header each writes after the error correction letter in <c>^FD</c> (shown in the English name).</summary>
    public static IReadOnlyList<QrChoice> InputModes { get; } = new[]
    {
        new QrChoice("Automatic", "Qr.Input.Automatic", "Automatic (A,)"),
        new QrChoice("ManualNumeric", "Qr.Input.ManualNumeric", "Manual, numeric (M,N)"),
        new QrChoice("ManualAlphanumeric", "Qr.Input.ManualAlphanumeric", "Manual, alphanumeric (M,A)"),
        new QrChoice("ManualByte", "Qr.Input.ManualByte", "Manual, 8-bit bytes (M,B + count)"),
        new QrChoice("ManualKanji", "Qr.Input.ManualKanji", "Manual, Kanji (M,K)"),
    };

    /// <summary>Picks the cheapest single character mode for the payload.</summary>
    public static QrDataMode ModeOf(string payload)
    {
        if (payload.Length > 0 && payload.All(char.IsAsciiDigit)) return QrDataMode.Numeric;
        if (payload.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or ' ' or '$' or '%' or '*' or '+' or '-' or '.' or '/' or ':')) return QrDataMode.Alphanumeric;
        return QrDataMode.Byte;
    }

    private static int Units(string payload, QrDataMode mode) => mode == QrDataMode.Byte ? Encoding.UTF8.GetByteCount(payload) : payload.Length;

    private static int CountBits(int version, QrDataMode mode) => (version, mode) switch
    {
        ( <= 9, QrDataMode.Numeric) => 10, ( <= 26, QrDataMode.Numeric) => 12, (_, QrDataMode.Numeric) => 14,
        ( <= 9, QrDataMode.Alphanumeric) => 9, ( <= 26, QrDataMode.Alphanumeric) => 11, (_, QrDataMode.Alphanumeric) => 13,
        ( <= 9, _) => 8, _ => 16,
    };

    private static int DataBits(int units, QrDataMode mode) => mode switch
    {
        QrDataMode.Numeric => 10 * (units / 3) + (units % 3 == 2 ? 7 : units % 3 == 1 ? 4 : 0),
        QrDataMode.Alphanumeric => 11 * (units / 2) + 6 * (units % 2),
        _ => 8 * units,
    };

    private static int MaxUnits(int version, QrErrorCorrection level, QrDataMode mode)
    {
        // The most units whose bits (4 mode bits + count + data) fit in the data codewords of the version.
        int capacityBits = DataCodewords[Row(level)][version - 1] * 8;
        int lo = 0, hi = 8000;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (4 + CountBits(version, mode) + DataBits(mid, mode) <= capacityBits) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>How the payload fits at the given level and model.</summary>
    public static QrFit Analyze(string payload, QrErrorCorrection level, int model = 2)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var mode = ModeOf(payload);
        int units = Units(payload, mode);
        if (model == 1)
        {
            int max1 = Model1Limits[Row(level)][(int)mode];
            return new QrFit(units <= max1, 0, 0, 100.0 * units / max1, mode, units, max1, level, 1);
        }
        int max = MaxUnits(40, level, mode);
        double used = 100.0 * DataBits(units, mode) / (DataCodewords[Row(level)][39] * 8 - 4 - CountBits(40, mode));
        for (int v = 1; v <= 40; v++)
        {
            if (4 + CountBits(v, mode) + DataBits(units, mode) <= DataCodewords[Row(level)][v - 1] * 8)
                return new QrFit(true, v, 17 + 4 * v, used, mode, units, max, level, 2);
        }
        return new QrFit(false, 0, 0, used, mode, units, max, level, 2);
    }

    /// <summary>
    /// The smallest module (one dot size of the symbol) that is still about <paramref name="minimumModuleMm"/> wide on paper, in whole dots.
    /// The usual rule of thumb for QR Codes read by phones is 0.25 mm, which is 2 dots at 203 dpi, 3 at 300 dpi and 6 at 600 dpi.
    /// </summary>
    public static int MinimumModuleDots(int dpi, double minimumModuleMm = 0.25)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        // 1e-9: 0.25 mm at 203.2 dpi is exactly 2 dots; floating point must not make it 3.
        return Math.Max(1, (int)Math.Ceiling(minimumModuleMm * dpi / 25.4 - 1e-9));
    }

    /// <summary>Printed side of the symbol in millimetres, without quiet zone: (17 + 4 x version) modules of <paramref name="moduleDots"/> dots.</summary>
    public static double SymbolSizeMm(int version, int moduleDots, int dpi)
    {
        if (version is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(version));
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        return (17 + 4 * version) * moduleDots * 25.4 / dpi;
    }

    /// <summary>
    /// Suggests an error correction level for the payload: the highest level whose symbol is at most <paramref name="maxExtraVersions"/>
    /// versions larger than the one the lowest level needs. A short text gets H or Q; a long one gets M or L so the symbol stays readable.
    /// </summary>
    public static QrErrorCorrection SuggestErrorCorrection(string payload, int maxExtraVersions = 2)
    {
        var low = Analyze(payload, QrErrorCorrection.Low);
        if (!low.Fits) return QrErrorCorrection.Low;
        foreach (var level in new[] { QrErrorCorrection.High, QrErrorCorrection.Quartile, QrErrorCorrection.Medium })
        {
            var fit = Analyze(payload, level);
            if (fit.Fits && fit.Version <= low.Version + maxExtraVersions) return level;
        }
        return QrErrorCorrection.Low;
    }

    /// <summary>
    /// The longest payload of a set, in the sense of the capacity check (the one that needs the most bits); null when the set is empty.
    /// Use it to check a whole data set against the symbol with one call to <see cref="Analyze"/>.
    /// </summary>
    public static string? Longest(IEnumerable<string> payloads)
    {
        string? best = null;
        double bestBits = -1;
        foreach (var p in payloads)
        {
            var mode = ModeOf(p);
            double bits = DataBits(Units(p, mode), mode);
            if (bits > bestBits) { bestBits = bits; best = p; }
        }
        return best;
    }
}
