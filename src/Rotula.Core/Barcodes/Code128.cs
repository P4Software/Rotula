using System.Text;

namespace Rotula.Core.Barcodes;

/// <summary>
/// Code 128 symbol tables, encoder and decoder working on module widths. Core uses it to recognise a barcode
/// that an old label drew as individual bars, and to check that a real Code 128 element would print the same bars.
/// </summary>
public static class Code128
{
    /// <summary>Value of the Start Code A symbol.</summary>
    public const int StartA = 103;
    /// <summary>Value of the Start Code B symbol.</summary>
    public const int StartB = 104;
    /// <summary>Value of the Start Code C symbol.</summary>
    public const int StartC = 105;
    /// <summary>Value of the Stop symbol (7 elements, 13 modules).</summary>
    public const int Stop = 106;

    private const int CodeC = 99, CodeB = 100, CodeA = 101;

    // Element widths (bar, space, bar, space, bar, space) of symbol values 0..105, then the stop (bar,space,bar,space,bar,space,bar).
    private static readonly string[] PatternText =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    private static readonly int[][] Patterns = PatternText.Select(t => t.Select(c => c - '0').ToArray()).ToArray();

    /// <summary>The element widths (in modules) of a symbol value 0-106.</summary>
    public static IReadOnlyList<int> PatternOf(int symbolValue) => Patterns[symbolValue];

    /// <summary>
    /// Chooses an efficient symbol sequence for <paramref name="data"/> (printable ASCII 32-126) the way Zebra's
    /// automatic mode (^BC mode A) is documented: "a string of four or more numeric digits causes an automatic shift to
    /// Subset C", otherwise subset B. Returns null when the data
    /// cannot be encoded. The returned values include start code, check symbol and stop.
    /// </summary>
    public static int[]? Encode(string data)
    {
        if (data.Length == 0 || data.Any(c => c < 32 || c > 126)) return null;
        var values = new List<int>();
        int i = 0;
        int digits = DigitRun(data, 0);
        char set;
        if (digits >= 4 || (digits == data.Length && digits == 2)) { values.Add(StartC); set = 'C'; }
        else { values.Add(StartB); set = 'B'; }

        while (i < data.Length)
        {
            int run = DigitRun(data, i);
            if (set != 'C' && i > 0 && run >= 4)
            {
                // An odd run keeps its first digit in the current subset so the rest pairs up.
                if (run % 2 == 1) { values.Add(data[i] - 32); i++; }
                values.Add(CodeC);
                set = 'C';
                continue;
            }
            if (set == 'C' && run >= 2)
            {
                values.Add((data[i] - '0') * 10 + (data[i + 1] - '0'));
                i += 2;
                continue;
            }
            if (set == 'C')
            {
                values.Add(CodeB);
                set = 'B';
                continue;
            }
            values.Add(data[i] - 32);
            i++;
        }

        values.Add(CheckValue(values));
        values.Add(Stop);
        return values.ToArray();
    }

    private static int DigitRun(string s, int start)
    {
        int n = 0;
        while (start + n < s.Length && s[start + n] is >= '0' and <= '9') n++;
        return n;
    }

    private static int CheckValue(IReadOnlyList<int> valuesWithoutCheck)
    {
        int sum = valuesWithoutCheck[0];
        for (int i = 1; i < valuesWithoutCheck.Count; i++) sum += i * valuesWithoutCheck[i];
        return sum % 103;
    }

    /// <summary>Expands symbol values into alternating bar/space widths in modules (bar first).</summary>
    public static int[] ToRuns(IReadOnlyList<int> symbolValues)
    {
        var runs = new List<int>();
        foreach (int v in symbolValues) runs.AddRange(Patterns[v]);
        return runs.ToArray();
    }

    /// <summary>
    /// Decodes alternating bar/space widths in modules (starting and ending with a bar, no quiet zones).
    /// Fails when a symbol is unknown, the check symbol is wrong, or the data uses features this decoder
    /// does not translate (FNC characters, shifts, control characters).
    /// </summary>
    /// <param name="runs">Element widths in modules.</param>
    /// <param name="value">The decoded text when successful.</param>
    /// <param name="symbolValues">The decoded symbol values (start..stop) when successful.</param>
    public static bool TryDecode(IReadOnlyList<int> runs, out string value, out int[] symbolValues)
    {
        value = string.Empty;
        symbolValues = Array.Empty<int>();
        // n data-ish symbols of 6 elements plus the 7-element stop.
        if (runs.Count < 6 * 3 + 7 || (runs.Count - 7) % 6 != 0) return false;

        var values = new List<int>();
        for (int pos = 0; pos < runs.Count - 7; pos += 6)
        {
            int v = Lookup(runs, pos, 6);
            if (v < 0) return false;
            values.Add(v);
        }
        if (Lookup(runs, runs.Count - 7, 7) != Stop) return false;

        if (values.Count < 3 || values[0] < StartA || values[0] > StartC) return false;
        if (values[^1] != CheckValue(values.Take(values.Count - 1).ToList())) return false;

        char set = values[0] switch { StartA => 'A', StartB => 'B', _ => 'C' };
        var sb = new StringBuilder();
        for (int i = 1; i < values.Count - 1; i++)
        {
            int v = values[i];
            if (set == 'C')
            {
                if (v <= 99) { sb.Append(v.ToString("00", System.Globalization.CultureInfo.InvariantCulture)); continue; }
                if (v == CodeB) { set = 'B'; continue; }
                if (v == CodeA) { set = 'A'; continue; }
                return false; // FNC1 and friends: not representable as plain text
            }
            if (v <= 95)
            {
                // Subset A maps 64-95 to control characters and subset B maps 95 to DEL; neither is plain text.
                if ((set == 'A' && v >= 64) || v == 95) return false;
                sb.Append((char)(v + 32));
                continue;
            }
            if (v == CodeC) { set = 'C'; continue; }
            if (v == CodeB && set == 'A') { set = 'B'; continue; }
            if (v == CodeA && set == 'B') { set = 'A'; continue; }
            return false;
        }
        if (sb.Length == 0) return false;
        value = sb.ToString();
        symbolValues = values.Append(Stop).ToArray();
        return true;
    }

    private static int Lookup(IReadOnlyList<int> runs, int start, int length)
    {
        for (int v = 0; v < Patterns.Length; v++)
        {
            var p = Patterns[v];
            if (p.Length != length) continue;
            bool same = true;
            for (int k = 0; k < length && same; k++) same = p[k] == runs[start + k];
            if (same) return v;
        }
        return -1;
    }
}
