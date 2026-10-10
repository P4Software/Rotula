using Rotula.Core.Barcodes;

namespace Rotula.Rendering.Barcodes;

/// <summary>
/// Hand-written encoders for the linear symbologies that ZXing.Net cannot write (or writes with a different bar ratio than
/// ZPL): Code 11, Industrial and Standard 2 of 5, ANSI Codabar, MSI, Plessey, the UPC/EAN add-ons and POSTNET/PLANET.
/// Every result is a row of modules (true = bar) without quiet zone. Wide bars are three modules wide, which is ZPL's default
/// ratio of 3.0; the preview does not follow a smaller ^BY ratio.
/// </summary>
/// <remarks>
/// How each table was checked: the patterns of Code 11, Standard 2 of 5, Codabar, MSI, Plessey, the add-ons, POSTNET and PLANET
/// are compared in the test suite with the printed example symbols of the Zebra ZPL II Programming Guide (the bar widths of
/// the sample images were measured). Industrial 2 of 5 has no sample in the guide; its start and stop patterns follow the
/// common published definition.
/// </remarks>
internal static class LinearEncoders
{
    private const int Wide = 3;

    /// <summary>Collects bars and spaces as modules.</summary>
    private sealed class Row
    {
        private readonly List<bool> _modules = new();

        public Row Bar(int n) { for (int i = 0; i < n; i++) _modules.Add(true); return this; }
        public Row Space(int n) { for (int i = 0; i < n; i++) _modules.Add(false); return this; }

        /// <summary>Appends alternating bar/space elements from a string of 'n' (narrow) and 'w' (wide), starting with a bar.</summary>
        public Row Elements(string widths)
        {
            bool bar = true;
            foreach (char c in widths)
            {
                int n = c == 'w' || c == '1' ? Wide : 1;
                if (bar) Bar(n); else Space(n);
                bar = !bar;
            }
            return this;
        }

        /// <summary>Appends a string of '1' (bar) and '0' (space) modules.</summary>
        public Row Modules(string bits)
        {
            foreach (char c in bits) _modules.Add(c == '1');
            return this;
        }

        public bool[] ToArray() => _modules.ToArray();
    }

    // ---- Code 11 ---------------------------------------------------------------------------

    // Five elements per character (bar, space, bar, space, bar); 1 = wide. Source of the table: the public Code 11 (USD-8)
    // definition, confirmed against the ZPL guide's printed sample of "123456" with two check digits.
    private static readonly Dictionary<char, string> Code11Table = new()
    {
        ['0'] = "00001", ['1'] = "10001", ['2'] = "01001", ['3'] = "11000", ['4'] = "00101", ['5'] = "10100",
        ['6'] = "01100", ['7'] = "00011", ['8'] = "10010", ['9'] = "10000", ['-'] = "00100",
    };
    private const string Code11StartStop = "00110";

    /// <summary>Code 11 for <paramref name="data"/> with the check characters the printer appends.</summary>
    public static bool[] Code11(string data, int checkDigits)
    {
        string full = data + CheckDigits.Code11(data, checkDigits);
        var row = new Row().Elements(Code11StartStop);
        foreach (char c in full)
            row.Space(1).Elements(Code11Table[c]);
        return row.Space(1).Elements(Code11StartStop).ToArray();
    }

    // ---- 2 of 5 -----------------------------------------------------------------------------

    // Five bars per digit, two of them wide (weights 1-2-4-7-0); all spaces are narrow.
    private static readonly string[] TwoOfFive = { "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw", "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn" };

    private static bool[] TwoOfFiveRow(string digits, string start, string stop)
    {
        var widths = new List<char>(start);
        foreach (char d in digits) widths.AddRange(TwoOfFive[d - '0']);
        widths.AddRange(stop);
        var row = new Row();
        for (int i = 0; i < widths.Count; i++)
        {
            if (i > 0) row.Space(1); // every space of the two-width 2 of 5 family is narrow
            row.Bar(widths[i] == 'w' ? Wide : 1);
        }
        return row.ToArray();
    }

    /// <summary>
    /// Standard 2 of 5 (^BJ): two narrow bars to start and a wide then a narrow bar to stop. Measured from the ZPL guide's
    /// printed sample of "123456".
    /// </summary>
    public static bool[] Standard2Of5(string digits) => TwoOfFiveRow(digits, "nn", "wn");

    /// <summary>
    /// Industrial 2 of 5 (^BI): wide, wide, narrow bars to start and wide, narrow, wide to stop, as in the usual published
    /// definition of the symbology. The ZPL guide has no printed sample, so this one is not checked against Zebra output.
    /// </summary>
    public static bool[] Industrial2Of5(string digits) => TwoOfFiveRow(digits, "wwn", "wnw");

    // ---- Codabar ----------------------------------------------------------------------------

    // Seven elements per character (bar, space, bar, space, bar, space, bar); 1 = wide. Checked against the ZPL guide's sample.
    private static readonly Dictionary<char, string> CodabarTable = new()
    {
        ['0'] = "0000011", ['1'] = "0000110", ['2'] = "0001001", ['3'] = "1100000", ['4'] = "0010010", ['5'] = "1000010",
        ['6'] = "0100001", ['7'] = "0100100", ['8'] = "0110000", ['9'] = "1001000", ['-'] = "0001100", ['$'] = "0011000",
        [':'] = "1000101", ['/'] = "1010001", ['.'] = "1010100", ['+'] = "0010101",
        ['A'] = "0011010", ['B'] = "0101001", ['C'] = "0001011", ['D'] = "0001110",
    };

    /// <summary>ANSI Codabar with the given start and stop characters (A-D) around <paramref name="data"/>.</summary>
    public static bool[] Codabar(char start, string data, char stop)
    {
        var row = new Row();
        string full = start + data + stop;
        for (int i = 0; i < full.Length; i++)
        {
            if (i > 0) row.Space(1);
            row.Elements(CodabarTable[full[i]]);
        }
        return row.ToArray();
    }

    // ---- MSI and Plessey (pulse width modulated) ----------------------------------------------

    /// <summary>One MSI/Plessey data bit: a wide bar and narrow space for 1, a narrow bar and wide space for 0.</summary>
    private static Row Bit(Row row, bool one) => one ? row.Bar(Wide).Space(1) : row.Bar(1).Space(Wide);

    /// <summary>
    /// MSI: a "1" start bit, four bits per digit (most significant first), then a narrow bar, wide space and narrow bar as stop.
    /// <paramref name="digits"/> already contains the check digits.
    /// </summary>
    public static bool[] Msi(string digits)
    {
        var row = Bit(new Row(), true);
        foreach (char d in digits)
        {
            int v = d - '0';
            for (int b = 3; b >= 0; b--) Bit(row, ((v >> b) & 1) == 1);
        }
        return row.Bar(1).Space(Wide).Bar(1).ToArray();
    }

    /// <summary>The check digits MSI appends for the chosen scheme (empty for none).</summary>
    public static string MsiChecks(string digits, Core.Model.MsiCheckDigitMode mode)
    {
        switch (mode)
        {
            case Core.Model.MsiCheckDigitMode.None: return string.Empty;
            case Core.Model.MsiCheckDigitMode.Mod10:
                return CheckDigits.MsiMod10(digits).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case Core.Model.MsiCheckDigitMode.DoubleMod10:
                int first = CheckDigits.MsiMod10(digits);
                return first.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + CheckDigits.MsiMod10(digits + first.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                // One modulo 11 check (which can be the two digits "10"), then modulo 10 over data plus that check.
                string eleven = CheckDigits.MsiMod11Text(digits);
                return eleven + CheckDigits.MsiMod10(digits + eleven).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Plessey: start bits 1101, four bits per hex character least significant first, the 8-bit CRC, a full-width termination
    /// bar and the mirrored end bits 0011. The CRC is always part of the symbol; ZPL's check digit option only decides whether it
    /// is also written in the text line (confirmed by the ZPL guide's sample, which has the CRC bits with the option off).
    /// </summary>
    public static bool[] Plessey(string hexData)
    {
        var row = new Row();
        foreach (char b in "1101") Bit(row, b == '1');
        foreach (char c in hexData)
        {
            int v = Convert.ToInt32(c.ToString(), 16);
            for (int b = 0; b < 4; b++) Bit(row, ((v >> b) & 1) == 1);
        }
        foreach (bool bit in CheckDigits.PlesseyCrcBits(hexData)) Bit(row, bit);
        row.Bar(1 + Wide);
        // End pattern: the mirror image of bits 0, 0, 1, 1 (space first, then the bar).
        foreach (char b in "0011")
            if (b == '1') row.Space(1).Bar(Wide); else row.Space(Wide).Bar(1);
        return row.ToArray();
    }

    // ---- UPC / EAN add-ons -------------------------------------------------------------------

    private static readonly string[] CodeA = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
    private static readonly string[] CodeB = { "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111" };
    // Parity of the five digits is chosen by (3*(d1+d3+d5) + 9*(d2+d4)) mod 10; 'A' = odd parity (L code), 'B' = even (G code).
    private static readonly string[] FiveDigitParity = { "BBAAA", "BABAA", "BAABA", "BAAAB", "ABBAA", "AABBA", "AAABB", "ABABA", "ABAAB", "AABAB" };
    private static readonly string[] TwoDigitParity = { "AA", "AB", "BA", "BB" };

    /// <summary>The two- or five-digit add-on (EAN/UPC supplemental symbol). The parity pattern carries the check value.</summary>
    public static bool[] Extension(string digits)
    {
        string parity;
        if (digits.Length == 2)
        {
            // Two digits: the parity pattern follows the value modulo 4.
            parity = TwoDigitParity[int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) % 4];
        }
        else
        {
            int d(int i) => digits[i] - '0';
            parity = FiveDigitParity[(3 * (d(0) + d(2) + d(4)) + 9 * (d(1) + d(3))) % 10];
        }

        var row = new Row().Modules("1011");
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0) row.Modules("01"); // delineator between digits
            row.Modules((parity[i] == 'A' ? CodeA : CodeB)[digits[i] - '0']);
        }
        return row.ToArray();
    }

    // ---- POSTNET / PLANET --------------------------------------------------------------------

    // 1 = tall bar, 0 = short bar. PLANET uses the same table with tall and short swapped.
    private static readonly string[] PostnetDigits = { "11000", "00011", "00101", "00110", "01001", "01010", "01100", "10001", "10010", "10100" };

    /// <summary>The bar heights of a POSTNET (or PLANET) symbol, true = tall, including the frame bars and the check digit.</summary>
    public static bool[] PostalBars(string digits, bool planet)
    {
        int sum = 0;
        foreach (char d in digits) sum += d - '0';
        string all = digits + (char)('0' + (10 - sum % 10) % 10);
        var bars = new List<bool> { true };
        foreach (char d in all)
            foreach (char bit in PostnetDigits[d - '0']) bars.Add((bit == '1') != planet);
        bars.Add(true);
        return bars.ToArray();
    }
}
