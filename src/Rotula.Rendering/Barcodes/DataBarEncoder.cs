// GS1 DataBar encoder for the designer preview.
//
// The element width tables, the checksum weights, the finder patterns and the Annex B routines follow ISO/IEC 24724:2011 (GS1 DataBar
// Omnidirectional, Truncated, Stacked, Stacked Omnidirectional, Limited, Expanded and Expanded Stacked) as they are transcribed in the
// libzint library (backend/rss.c, rss.h and general_field.c), which this file was ported from and is checked against. libzint is
// distributed under the BSD-3-Clause licence:
//
//     libzint - the open source barcode library
//     Copyright (C) 2008-2026 Robin Stuart <rstuart114@gmail.com>
//
//     Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following
//     conditions are met:
//     1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
//        disclaimer in the documentation and/or other materials provided with the distribution.
//     3. Neither the name of the project nor the names of its contributors may be used to endorse or promote products derived from
//        this software without specific prior written permission.
//     THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,
//     BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT
//     SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
//     DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
//     INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
//     NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//
// The functions combins() and getRSSwidths() of ISO/IEC 24724 Annex B are Copyright BSI and are used with BSI's permission to use
// "necessary details such as symbols, and size, type or grade designations" in the course of implementing the standard.

using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Model;

namespace Rotula.Rendering.Barcodes;

/// <summary>
/// Encodes the GS1 DataBar family into module rows. The non-expanded types take a 13 digit item number (shorter input is padded on
/// the left with zeros, as the ZPL guide's sample shows; a 14th digit is a check digit and is dropped). Expanded takes a GS1 element
/// string written with the application identifiers in parentheses, for example <c>(01)90614141999999(3103)001750</c>.
/// </summary>
internal static class DataBarEncoder
{
    private const int FncOne = Gs1ElementString.Fnc1;

    /// <summary>Encodes <paramref name="data"/> as the given DataBar variant.</summary>
    /// <param name="type">The variant.</param>
    /// <param name="data">Digits for the non-expanded types, an element string for Expanded.</param>
    /// <param name="segmentsPerRow">Expanded only: symbol characters per row (even, 2-22); fewer than 22 stacks the symbol.</param>
    /// <param name="separatorHeight">Height of the separator rows of the stacked types, 1 or 2 modules.</param>
    public static BarcodeMatrix Encode(DataBarType type, string data, int segmentsPerRow, int separatorHeight)
    {
        int separator = Math.Clamp(separatorHeight, 1, 2);
        switch (type)
        {
            case DataBarType.Omnidirectional:
            case DataBarType.Truncated:
                return SingleRow(OmniElements(ParseItem(data, limited: false), out _));
            case DataBarType.Stacked:
                return Stacked(OmniElements(ParseItem(data, limited: false), out _), separator);
            case DataBarType.StackedOmnidirectional:
                return StackedOmni(OmniElements(ParseItem(data, limited: false), out int cRight), cRight, separator);
            case DataBarType.Limited:
                return SingleRow(LimitedElements(ParseItem(data, limited: true)));
            case DataBarType.Expanded:
                return Expanded(data, segmentsPerRow, separator);
            default:
                throw new BarcodeEncodingException("Unknown GS1 DataBar variant.");
        }
    }

    /// <summary>Omnidirectional symbol for a raw value, which may include the composite linkage flag (10^13); used by the tests.</summary>
    internal static BarcodeMatrix EncodeOmniValue(long value) => SingleRow(OmniElements(value, out _));

    // ---- shared helpers -------------------------------------------------------------------------

    /// <summary>The 13 digit item value; a 14th digit (check digit) is ignored, shorter input is padded with zeros on the left.</summary>
    private static long ParseItem(string data, bool limited)
    {
        if (data.Length is 0 or > 14 || data.Any(c => c is < '0' or > '9'))
            throw new BarcodeEncodingException("GS1 DataBar needs up to 13 digits (a 14th digit is the check digit).");
        string digits = data.Length == 14 ? data[..13] : data.PadLeft(13, '0');
        long value = long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        if (limited && value > 1999999999999L)
            throw new BarcodeEncodingException("GS1 DataBar Limited encodes values up to 1999999999999 only.");
        return value;
    }

    private static readonly int[] LimitedGSum = { 0, 183064, 820064, 1000776, 1491021, 1979845, 1996939 };

    private static readonly int[,] Combinations =
    {
        { 1, 1, 1, 1, 1, 1 }, { 1, 1, 1, 1, 1, 1 }, { 1, 2, 1, 1, 1, 1 }, { 1, 3, 3, 1, 1, 1 }, { 1, 4, 6, 4, 1, 1 }, { 1, 5, 10, 10, 5, 1 },
        { 1, 6, 15, 20, 15, 6 }, { 1, 7, 21, 35, 35, 21 }, { 1, 8, 28, 56, 70, 56 }, { 1, 9, 36, 84, 126, 126 }, { 1, 10, 45, 120, 210, 252 },
        { 1, 11, 55, 165, 330, 462 }, { 1, 12, 66, 220, 495, 792 }, { 1, 13, 78, 286, 715, 1287 }, { 1, 14, 91, 364, 1001, 2002 },
        { 1, 15, 105, 455, 1365, 3003 }, { 1, 16, 120, 560, 1820, 4368 }, { 1, 17, 136, 680, 2380, 6188 },
    };

    /// <summary>combins(n, r) of ISO/IEC 24724 Annex B: the number of combinations of r elements selected from n.</summary>
    private static int Combins(int n, int r) => Combinations[n, r];

    /// <summary>getRSSwidths() of Annex B: the element widths of a value (<paramref name="elements"/> per set, n modules in total).</summary>
    private static int[] GetWidths(int val, int n, int elements, int maxWidth, bool noNarrow)
    {
        var widths = new int[elements];
        int narrowMask = 0;
        int bar;
        for (bar = 0; bar < elements - 1; bar++)
        {
            int elmWidth, subVal;
            for (elmWidth = 1, narrowMask |= 1 << bar; ; elmWidth++, narrowMask &= ~(1 << bar))
            {
                // Get all combinations.
                subVal = Combins(n - elmWidth - 1, elements - bar - 2);
                // Fewer combinations when no element may be a single module.
                if (noNarrow && narrowMask == 0 && n - elmWidth - (elements - bar - 1) >= elements - bar - 1)
                    subVal -= Combins(n - elmWidth - (elements - bar), elements - bar - 2);
                // Fewer combinations with elements wider than the maximum.
                if (elements - bar - 1 > 1)
                {
                    int lessVal = 0;
                    for (int mxwElement = n - elmWidth - (elements - bar - 2); mxwElement > maxWidth; mxwElement--)
                        lessVal += Combins(n - elmWidth - mxwElement - 1, elements - bar - 3);
                    subVal -= lessVal * (elements - 1 - bar);
                }
                else if (n - elmWidth > maxWidth)
                {
                    subVal--;
                }
                val -= subVal;
                if (val < 0) break;
            }
            val += subVal;
            n -= elmWidth;
            widths[bar] = elmWidth;
        }
        widths[bar] = n;
        return widths;
    }

    /// <summary>The odd and even element sets of one data character, interleaved (odd first).</summary>
    private static int[] InterleavedWidths(int vOdd, int vEven, int nOdd, int nEven, int elements, int maxWidth, bool noNarrow)
    {
        var odd = GetWidths(vOdd, nOdd, elements, maxWidth, noNarrow);
        var even = GetWidths(vEven, nEven, elements, 9 - maxWidth, !noNarrow);
        var result = new int[elements * 2];
        for (int i = 0; i < elements; i++)
        {
            result[i << 1] = odd[i];
            result[(i << 1) + 1] = even[i];
        }
        return result;
    }

    /// <summary>A growing grid of modules; row r, column x.</summary>
    private sealed class Grid
    {
        private readonly bool[,] _modules;
        public Grid(int width, int rows) { _modules = new bool[width, rows]; Rows = rows; }
        public int Rows { get; }
        public int WrittenWidth { get; private set; }
        public bool IsSet(int row, int x) => _modules[x, row];
        public void Set(int row, int x) { _modules[x, row] = true; WrittenWidth = Math.Max(WrittenWidth, x + 1); }
        public void Unset(int row, int x) { _modules[x, row] = false; WrittenWidth = Math.Max(WrittenWidth, x + 1); }

        /// <summary>Expands element widths into modules, starting with a bar when <paramref name="latch"/> is true. Returns the next column.</summary>
        public int Expand(int row, int writer, bool latch, IReadOnlyList<int> widths, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                for (int j = 0; j < widths[i]; j++)
                {
                    if (latch) Set(row, writer); else Unset(row, writer);
                    writer++;
                }
                latch = !latch;
            }
            return writer;
        }

        public BarcodeMatrix ToMatrix(int[]? weights)
        {
            var dark = new bool[WrittenWidth, Rows];
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < WrittenWidth; x++) dark[x, y] = _modules[x, y];
            return Rows == 1
                ? new BarcodeMatrix(dark, BarcodeRowLayout.Stretched, null)
                : new BarcodeMatrix(dark, BarcodeRowLayout.Proportional, weights);
        }
    }

    private static BarcodeMatrix SingleRow(int[] elements)
    {
        var grid = new Grid(elements.Sum() + 2, 1);
        grid.Expand(0, 0, false, elements, 0, elements.Length);
        return grid.ToMatrix(null);
    }

    // ---- Omnidirectional, Truncated, Stacked, Stacked Omnidirectional ------------------------------------

    private static int OmniGroup(int val, bool outside)
    {
        int end = outside ? 4 : 8;
        int i = outside ? 0 : 5;
        for (; i < end; i++)
            if (val < DataBarTables.OmniGSum[i + 1]) return i;
        return i;
    }

    /// <summary>The 46 element widths of a DataBar-14 symbol; <paramref name="cRight"/> is the right finder value (the stacked omnidirectional bottom separator needs it).</summary>
    private static int[] OmniElements(long value, out int cRight)
    {
        int leftPair = (int)(value / 4537077), rightPair = (int)(value % 4537077);
        int[] dataCharacter = { leftPair / 1597, leftPair % 1597, rightPair / 1597, rightPair % 1597 };
        var dataWidths = new int[4][];
        for (int i = 0; i < 4; i++)
        {
            // Counting 1-based 1, 2, 4, 3, i.e. 0-based 0, 1, 3, 2, so 0 and 2 are the outside characters.
            bool outside = (i & 1) == 0;
            int group = OmniGroup(dataCharacter[i], outside);
            int v = dataCharacter[i] - DataBarTables.OmniGSum[group];
            int vDiv = v / DataBarTables.OmniTEvenOdd[group], vMod = v % DataBarTables.OmniTEvenOdd[group];
            dataWidths[i] = InterleavedWidths(outside ? vDiv : vMod, (i & 1) == 1 ? vDiv : vMod, DataBarTables.OmniModules[group],
                DataBarTables.OmniModules[group + 9], 4, DataBarTables.OmniWidest[group], (i & 1) == 1);
        }

        int checksum = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 8; j++) checksum += DataBarTables.OmniWeights[i][j] * dataWidths[i][j];
        checksum %= 79;
        // The two check characters skip the values that would produce a finder pattern clash.
        if (checksum >= 8) checksum++;
        if (checksum >= 72) checksum++;
        int cLeft = checksum / 9;
        cRight = checksum % 9;

        var total = new int[46];
        total[0] = 1; total[1] = 1; total[44] = 1; total[45] = 1;
        for (int i = 0; i < 8; i++)
        {
            total[i + 2] = dataWidths[0][i];
            total[i + 15] = dataWidths[1][7 - i];
            total[i + 23] = dataWidths[3][i];
            total[i + 36] = dataWidths[2][7 - i];
        }
        for (int i = 0; i < 5; i++)
        {
            total[i + 10] = DataBarTables.OmniFinder[cLeft][i];
            total[i + 31] = DataBarTables.OmniFinder[cRight][4 - i];
        }
        return total;
    }

    /// <summary>GS1 DataBar Stacked: two rows of 50 modules joined by a separator row (ISO/IEC 24724:2011 5.3.2.1, 5X / 1X / 7X).</summary>
    private static BarcodeMatrix Stacked(int[] t, int separator)
    {
        var grid = new Grid(52, 3);
        int writer = grid.Expand(0, 0, false, t, 0, 23);
        grid.Set(0, writer);
        grid.Unset(0, writer + 1);

        grid.Set(2, 0);
        grid.Unset(2, 1);
        grid.Expand(2, 2, true, t, 23, 46);

        // Separator pattern (alternation as in the figures of the standard).
        for (int i = 1; i < 46; i++)
        {
            if (grid.IsSet(0, i) == grid.IsSet(2, i))
            {
                if (!grid.IsSet(0, i)) grid.Set(1, i);
            }
            else if (!grid.IsSet(1, i - 1))
            {
                grid.Set(1, i);
            }
        }
        grid.Unset(1, 1);
        grid.Unset(1, 2);
        grid.Unset(1, 3);
        return grid.ToMatrix(new[] { 5, separator, 7 });
    }

    private static void OmniFinderAdjust(Grid g, int separatorRow, int aboveBelow, int finderStart)
    {
        int moduleRow = separatorRow + aboveBelow;
        // Alternation is always left to right for omnidirectional separators.
        bool latch = true;
        for (int i = finderStart; i < finderStart + 13; i++)
        {
            if (!g.IsSet(moduleRow, i))
            {
                if (latch) { g.Set(separatorRow, i); latch = false; }
                else { g.Unset(separatorRow, i); latch = true; }
            }
            else
            {
                g.Unset(separatorRow, i);
                latch = true;
            }
        }
    }

    private static void OmniSeparator(Grid g, int width, int separatorRow, int aboveBelow, int finderStart, bool bottomFinderValue3)
    {
        int moduleRow = separatorRow + aboveBelow;
        for (int i = 4; i < width - 4; i++)
            if (!g.IsSet(moduleRow, i)) g.Set(separatorRow, i);
        if (bottomFinderValue3)
        {
            // ISO/IEC 24724:2011 5.3.2.2: the single dark module over finder value 3 is shifted one module to the right.
            int finderValue3 = finderStart + 10;
            for (int i = finderStart; i < finderStart + 13; i++)
            {
                if (i == finderValue3) g.Set(separatorRow, i); else g.Unset(separatorRow, i);
            }
        }
        else
        {
            OmniFinderAdjust(g, separatorRow, aboveBelow, finderStart);
        }
    }

    /// <summary>GS1 DataBar Stacked Omnidirectional: two full-height rows with three separator rows in between.</summary>
    private static BarcodeMatrix StackedOmni(int[] t, int cRight, int separator)
    {
        var grid = new Grid(52, 5);
        int writer = grid.Expand(0, 0, false, t, 0, 23);
        grid.Set(0, writer);
        grid.Unset(0, writer + 1);

        grid.Set(4, 0);
        grid.Unset(4, 1);
        grid.Expand(4, 2, true, t, 23, 46);

        // Middle separator.
        for (int i = 5; i < 46; i += 2) grid.Set(2, i);
        // Top separator (below the first row) and bottom separator (above the second row).
        OmniSeparator(grid, 50, 1, -1, 18, false);
        // 17 = 2 (guard) + 15 (inner character); +2 skips the finder elements 4 and 5 (right to left).
        OmniSeparator(grid, 50, 3, 1, 17 + 2, cRight == 3);
        return grid.ToMatrix(new[] { 33, separator, separator, separator, 33 });
    }

    // ---- Limited --------------------------------------------------------------------------------

    private static int[] LimitedElements(long value)
    {
        long[] pairValues = { value / 2013571, value % 2013571 };
        var pairWidths = new int[2][];
        for (int i = 0; i < 2; i++)
        {
            int val = (int)pairValues[i];
            int group = 0;
            for (int g = 6; g > 0; g--)
            {
                if (val >= LimitedGSum[g]) { val -= LimitedGSum[g]; group = g; break; }
            }
            int odd = val / DataBarTables.LimitedTEven[group];
            int even = val % DataBarTables.LimitedTEven[group];
            pairWidths[i] = InterleavedWidths(odd, even, DataBarTables.LimitedModules[group], 26 - DataBarTables.LimitedModules[group], 7,
                DataBarTables.LimitedWidest[group], false);
        }

        int checksum = 0;
        for (int i = 0; i < 14; i++)
        {
            checksum += DataBarTables.LimitedWeights[0][i] * pairWidths[0][i];
            checksum += DataBarTables.LimitedWeights[1][i] * pairWidths[1][i];
        }
        checksum %= 89;
        var finder = DataBarTables.LimitedFinders[checksum];

        var total = new int[47];
        total[0] = 1; total[1] = 1; total[44] = 1; total[45] = 1; total[46] = 5;
        for (int i = 0; i < 14; i++)
        {
            total[i + 2] = pairWidths[0][i];
            total[i + 16] = finder[i];
            total[i + 30] = pairWidths[1][i];
        }
        return total;
    }

    // ---- Expanded and Expanded Stacked -----------------------------------------------------------

    /// <summary>The element string without parentheses; a GS (FNC1) follows every element of variable length that is not the last.</summary>
    private static string ReduceElementString(string data) => Gs1ElementString.TryReduce(data, out string reduced) switch
    {
        null => reduced,
        Gs1ElementStringProblem.Character => throw new BarcodeEncodingException("A value of the element string has a character that GS1 DataBar Expanded cannot encode."),
        Gs1ElementStringProblem.TooLong => throw new BarcodeEncodingException("The element string is too long for GS1 DataBar Expanded (77 characters at most)."),
        _ => throw new BarcodeEncodingException("GS1 DataBar Expanded needs a GS1 element string such as (01)90614141999999(3103)001750."),
    };

    private static void AppendBits(StringBuilder bits, int value, int length)
    {
        for (int i = length - 1; i >= 0; i--) bits.Append(((value >> i) & 1) == 1 ? '1' : '0');
    }

    /// <summary>The integer of <paramref name="length"/> digits at <paramref name="position"/>, or -1 when they are not all digits.</summary>
    private static int ToInt(string s, int position, int length)
    {
        if (position + length > s.Length) return -1;
        int v = 0;
        for (int i = 0; i < length; i++)
        {
            char c = s[position + i];
            if (c is < '0' or > '9') return -1;
            v = v * 10 + (c - '0');
        }
        return v;
    }

    /// <summary>Day count of a YYMMDD date as the DataBar Expanded date fields store it, or -1 when the date is invalid.</summary>
    private static int DateValue(string s, int position)
    {
        if (position + 6 > s.Length) return -1;
        int yy = ToInt(s, position, 2), mm = ToInt(s, position + 2, 2), dd = ToInt(s, position + 4, 2);
        // A month cannot be zero but a day can (it then means the last day of the month).
        if (yy < 0 || mm <= 0 || mm > 12 || dd < 0 || dd > 31) return -1;
        return yy * 384 + (mm - 1) * 32 + dd;
    }

    private enum GeneralMode { Numeric, Alphanumeric, IsoIec }

    private const string AlphanumericPunctuation = "*,-./";
    private const string IsoIecPunctuation = "!\"%&'()*+,-./:;<=>?_ ";

    /// <summary>Character class for the general purpose compaction (FNC1 counts as numeric); null when the character cannot be encoded.</summary>
    private static GeneralMode? CharType(string s, int i)
    {
        char c = s[i];
        if (c == (char)FncOne || c is >= '0' and <= '9') return GeneralMode.Numeric;
        if (c is >= 'A' and <= 'Z' || AlphanumericPunctuation.Contains(c)) return GeneralMode.Alphanumeric;
        if (c is >= 'a' and <= 'z' || IsoIecPunctuation.Contains(c)) return GeneralMode.IsoIec;
        return null;
    }

    private static bool Next(string s, int i, int num, GeneralMode type, GeneralMode? type2 = null)
    {
        if (i + num > s.Length) return false;
        for (; i < s.Length && num > 0; i++, num--)
        {
            var t = CharType(s, i);
            if (type2 is null ? t != type : t != type && t != type2) return false;
        }
        return num == 0;
    }

    private static bool NextTerminate(string s, int i, int num, int maxNum, GeneralMode type)
    {
        if (i + maxNum < s.Length) return false;
        for (; i < s.Length; i++, num--)
            if (CharType(s, i) != type) return false;
        return i == s.Length && num <= 0;
    }

    private static bool NextNone(string s, int i, int num, GeneralMode type)
    {
        for (; i < s.Length && num > 0; i++, num--)
            if (CharType(s, i) == type) return false;
        return num == 0 || i == s.Length;
    }

    /// <summary>
    /// General purpose data compaction of ISO/IEC 24724:2011 7.2.5.5 (numeric, alphanumeric and ISO/IEC 646 modes with their
    /// latches). Returns the odd digit that is left over at the end of numeric mode, or 0 when there is none.
    /// </summary>
    private static char GeneralFieldEncode(string s, ref GeneralMode mode, StringBuilder bits)
    {
        char lastDigit = '\0';
        for (int i = 0; i < s.Length;)
        {
            var type = CharType(s, i) ?? throw new BarcodeEncodingException($"The character \"{s[i]}\" cannot be encoded in GS1 DataBar Expanded.");
            switch (mode)
            {
                case GeneralMode.Numeric:
                    if (i < s.Length - 1)
                    {
                        if (type != GeneralMode.Numeric || CharType(s, i + 1) != GeneralMode.Numeric)
                        {
                            AppendBits(bits, 0, 4); // alphanumeric latch
                            mode = GeneralMode.Alphanumeric;
                        }
                        else
                        {
                            int d1 = s[i] == (char)FncOne ? 10 : s[i] - '0';
                            int d2 = s[i + 1] == (char)FncOne ? 10 : s[i + 1] - '0';
                            AppendBits(bits, 11 * d1 + d2 + 8, 7);
                            i += 2;
                        }
                    }
                    else if (type != GeneralMode.Numeric)
                    {
                        AppendBits(bits, 0, 4);
                        mode = GeneralMode.Alphanumeric;
                    }
                    else
                    {
                        // Ending with a single digit; handled by the caller (7.2.5.5.1 c).
                        lastDigit = s[i];
                        i++;
                    }
                    break;
                case GeneralMode.Alphanumeric:
                    if (s[i] == (char)FncOne)
                    {
                        AppendBits(bits, 15, 5);
                        mode = GeneralMode.Numeric;
                        i++;
                    }
                    else if (type == GeneralMode.IsoIec)
                    {
                        AppendBits(bits, 4, 5); // ISO/IEC 646 latch
                        mode = GeneralMode.IsoIec;
                    }
                    else if (Next(s, i, 6, GeneralMode.Numeric))
                    {
                        AppendBits(bits, 0, 3); // numeric latch
                        mode = GeneralMode.Numeric;
                    }
                    else if (NextTerminate(s, i, 4, 5, GeneralMode.Numeric))
                    {
                        AppendBits(bits, 0, 3);
                        mode = GeneralMode.Numeric;
                    }
                    else if (s[i] is >= '0' and <= '9')
                    {
                        AppendBits(bits, s[i] - 43, 5);
                        i++;
                    }
                    else if (s[i] is >= 'A' and <= 'Z')
                    {
                        AppendBits(bits, s[i] - 33, 6);
                        i++;
                    }
                    else
                    {
                        AppendBits(bits, AlphanumericPunctuation.IndexOf(s[i]) + 58, 6);
                        i++;
                    }
                    break;
                default:
                    if (s[i] == (char)FncOne)
                    {
                        AppendBits(bits, 15, 5);
                        mode = GeneralMode.Numeric;
                        i++;
                    }
                    else
                    {
                        bool next10NotIso = NextNone(s, i, 10, GeneralMode.IsoIec);
                        if (next10NotIso && Next(s, i, 4, GeneralMode.Numeric))
                        {
                            AppendBits(bits, 0, 3);
                            mode = GeneralMode.Numeric;
                        }
                        else if (next10NotIso && Next(s, i, 5, GeneralMode.Alphanumeric, GeneralMode.Numeric))
                        {
                            AppendBits(bits, 4, 5); // alphanumeric latch
                            mode = GeneralMode.Alphanumeric;
                        }
                        else if (s[i] is >= '0' and <= '9')
                        {
                            AppendBits(bits, s[i] - 43, 5);
                            i++;
                        }
                        else if (s[i] is >= 'A' and <= 'Z')
                        {
                            AppendBits(bits, s[i] - 1, 7);
                            i++;
                        }
                        else if (s[i] is >= 'a' and <= 'z')
                        {
                            AppendBits(bits, s[i] - 7, 7);
                            i++;
                        }
                        else
                        {
                            AppendBits(bits, IsoIecPunctuation.IndexOf(s[i]) + 232, 8);
                            i++;
                        }
                    }
                    break;
            }
        }
        return lastDigit;
    }

    /// <summary>Data encodation of DataBar Expanded (ISO/IEC 24724:2011 7.2.5): the bit string, a multiple of 12 bits.</summary>
    private static string ExpandedBinary(string source, int colsPerRow)
    {
        int length = source.Length;
        if (length > 77) throw new BarcodeEncodingException("The element string is too long for GS1 DataBar Expanded (77 characters at most).");
        var bits = new StringBuilder("0"); // component linkage flag: not a composite
        int charactersPerRow = colsPerRow * 2;

        // Decide whether a compressed data field is needed and which method to use (method 2 = no compressed field).
        int method = length >= 16 && source[0] == '0' && source[1] == '1' ? 1 : 2;
        if (length >= 20 && method == 1 && source[2] == '9' && source[16] == '3')
        {
            if (length >= 26 && source[17] == '1' && source[18] == '0')
            {
                int weight = ToInt(source, 20, 6);
                if (weight is >= 0 and <= 99999)
                {
                    if (length == 26) method = source[19] == '3' && weight <= 32767 ? 3 : 7;
                    else if (length == 34 && source[26] == '1' && source[27] is '1' or '3' or '5' or '7' && DateValue(source, 28) >= 0) method = 6 + (source[27] - '0');
                }
            }
            else if (length >= 26 && source[17] == '2' && source[18] == '0')
            {
                int weight = ToInt(source, 20, 6);
                if (weight is >= 0 and <= 99999)
                {
                    if (length == 26) method = (source[19] == '2' && weight <= 9999) || (source[19] == '3' && weight <= 22767) ? 4 : 8;
                    else if (length == 34 && source[26] == '1' && source[27] is '1' or '3' or '5' or '7' && DateValue(source, 28) >= 0) method = 7 + (source[27] - '0');
                }
            }
            else if (source[17] == '9' && source[19] is >= '0' and <= '3')
            {
                if (source[18] == '2') method = 5;
                else if (length >= 23 && source[18] == '3' && ToInt(source, 20, 3) >= 0) method = 6;
            }
        }

        int readPosition;
        switch (method)
        {
            case 1: AppendBits(bits, 4, 3); readPosition = 16; break;                      // "1XX"
            case 2: AppendBits(bits, 0, 4); readPosition = 0; break;                       // "00XX"
            case 3: case 4: AppendBits(bits, 4 + (method - 3), 4); readPosition = 26; break; // 0100, 0101
            case 5: AppendBits(bits, 0x30, 7); readPosition = 20; break;                   // 01100XX
            case 6: AppendBits(bits, 0x34, 7); readPosition = 23; break;                   // 01101XX
            default: AppendBits(bits, 56 + (method - 7), 7); readPosition = length; break; // 0111000 - 0111111
        }

        for (int i = 0; i < readPosition; i++)
            if (source[i] is not (>= '0' and <= '9') && source[i] != (char)FncOne)
                throw new BarcodeEncodingException("Invalid character in the compressed field of the element string (digits only).");

        // Compressed data field.
        if (method == 1)
        {
            AppendBits(bits, source[2] - '0', 4);
            for (int i = 3; i < 15; i += 3) AppendBits(bits, ToInt(source, i, 3), 10);
        }
        else if (method is 3 or 4)
        {
            for (int i = 3; i < 15; i += 3) AppendBits(bits, ToInt(source, i, 3), 10);
            AppendBits(bits, method == 4 && source[19] == '3' ? ToInt(source, 20, 6) + 10000 : ToInt(source, 20, 6), 15);
        }
        else if (method is 5 or 6)
        {
            for (int i = 3; i < 15; i += 3) AppendBits(bits, ToInt(source, i, 3), 10);
            AppendBits(bits, source[19] - '0', 2);
            if (method == 6) AppendBits(bits, ToInt(source, 20, 3), 10);
        }
        else if (method is >= 7 and <= 14)
        {
            for (int i = 3; i < 15; i += 3) AppendBits(bits, ToInt(source, i, 3), 10);
            var weightText = new StringBuilder().Append(source[19]);
            for (int i = 1; i < 6; i++) weightText.Append(source[20 + i]);
            AppendBits(bits, ToInt(weightText.ToString(), 0, 6), 20);
            AppendBits(bits, length == 34 ? DateValue(source, 28) : 38400, 16);
        }

        // The rest of the data goes into the general purpose compaction field.
        var mode = GeneralMode.Numeric;
        char lastDigit = '\0';
        string general = source[readPosition..];
        if (general.Length > 0) lastDigit = GeneralFieldEncode(general, ref mode, bits);

        int bp = bits.Length;
        int remainder = 12 - bp % 12;
        if (remainder == 12) remainder = 0;
        int symbolCharacters = (bp + remainder) / 12 + 1;
        if (charactersPerRow != 0 && symbolCharacters % charactersPerRow == 1) symbolCharacters++;
        if (symbolCharacters < 4) symbolCharacters = 4;
        remainder = 12 * (symbolCharacters - 1) - bp;

        if (lastDigit != '\0')
        {
            // One more numeric digit remains (7.2.5.5.1 c).
            if (remainder is >= 4 and <= 6) AppendBits(bits, lastDigit - '0' + 1, 4);
            else AppendBits(bits, (lastDigit - '0') * 11 + 10 + 8, 7);
            bp = bits.Length;
            remainder = 12 - bp % 12;
            if (remainder == 12) remainder = 0;
            symbolCharacters = (bp + remainder) / 12 + 1;
            if (charactersPerRow != 0 && symbolCharacters % charactersPerRow == 1) symbolCharacters++;
            if (symbolCharacters < 4) symbolCharacters = 4;
            remainder = 12 * (symbolCharacters - 1) - bp;
        }

        if (bits.Length > 252) throw new BarcodeEncodingException("The element string needs more than 21 symbol characters, which GS1 DataBar Expanded cannot hold.");

        // Padding (7.2.5.5.4).
        int pad = remainder;
        if (mode == GeneralMode.Numeric)
        {
            AppendBits(bits, 0, 4);
            pad -= 4;
        }
        for (; pad > 0; pad -= 5) AppendBits(bits, 4, 5);

        if (method is 1 or 2 or 5 or 6)
        {
            // Patch the variable length symbol bit field.
            char bit1 = symbolCharacters % 2 == 1 ? '1' : '0';
            char bit2 = symbolCharacters > 14 ? '1' : '0';
            int at = method == 1 ? 2 : method == 2 ? 3 : 6;
            bits[at] = bit1;
            bits[at + 1] = bit2;
        }
        return bits.ToString();
    }

    private static int ExpandedGroup(int val)
    {
        int i;
        for (i = 0; i < DataBarTables.ExpandedGSum.Length - 1; i++)
            if (val < DataBarTables.ExpandedGSum[i + 1]) return i;
        return i;
    }

    private static int[] ExpandedCharacterWidths(int value)
    {
        int group = ExpandedGroup(value);
        int odd = (value - DataBarTables.ExpandedGSum[group]) / DataBarTables.ExpandedTEven[group];
        int even = (value - DataBarTables.ExpandedGSum[group]) % DataBarTables.ExpandedTEven[group];
        return InterleavedWidths(odd, even, DataBarTables.ExpandedModules[group], 17 - DataBarTables.ExpandedModules[group], 4,
            DataBarTables.ExpandedWidest[group], true);
    }

    private static BarcodeMatrix Expanded(string data, int segmentsPerRow, int separator)
    {
        string reduced = ReduceElementString(data);
        // Stacking is only asked for when fewer than the maximum 22 symbol characters fit in a row (ZPL: 2 to 22, even).
        int colsPerRow = segmentsPerRow is >= 2 and < 22 ? segmentsPerRow / 2 : 0;
        string binary = ExpandedBinary(reduced, colsPerRow);

        int dataChars = binary.Length / 12;
        int symbolChars = dataChars + 1; // plus the check character
        var charWidths = new int[dataChars][];
        for (int i = 0; i < dataChars; i++)
        {
            int vs = 0;
            for (int j = 0; j < 12; j++)
                if (binary[i * 12 + j] == '1') vs |= 0x800 >> j;
            charWidths[i] = ExpandedCharacterWidths(vs);
        }

        // 7.2.6: the check character is the weighted element width sum modulo 211.
        int checksum = 0;
        for (int i = 0; i < dataChars; i++)
        {
            int row = DataBarTables.ExpandedWeightRows[(dataChars - 2) / 2][i];
            for (int j = 0; j < 8; j++) checksum += charWidths[i][j] * DataBarTables.ExpandedWeights[row][j];
        }
        int checkChar = 211 * (symbolChars - 4) + checksum % 211;
        var checkWidths = ExpandedCharacterWidths(checkChar);

        int codeBlocks = (symbolChars + 1) / 2;
        int patternWidth = codeBlocks * 5 + symbolChars * 8 + 4;
        var elements = new int[patternWidth];

        // Finder patterns, check character, forward reading and reversed data characters.
        int p = (symbolChars - 1) / 2 - 1;
        for (int i = 0; i < codeBlocks; i++)
        {
            int k = DataBarTables.ExpandedFinderSequence[p][i] - 1;
            for (int j = 0; j < 5; j++) elements[21 * i + j + 10] = DataBarTables.ExpandedFinders[k][j];
        }
        for (int i = 0; i < 8; i++) elements[i + 2] = checkWidths[i];
        for (int i = 1; i < dataChars; i += 2)
        {
            int k = (i - 1) / 2 * 21 + 23;
            for (int j = 0; j < 8; j++) elements[k + j] = charWidths[i][j];
        }
        for (int i = 0; i < dataChars; i += 2)
        {
            int k = i / 2 * 21 + 15;
            for (int j = 0; j < 8; j++) elements[k + j] = charWidths[i][7 - j];
        }

        if (colsPerRow == 0)
        {
            elements[0] = 1; elements[1] = 1;                         // left guard
            elements[patternWidth - 2] = 1; elements[patternWidth - 1] = 1; // right guard
            var single = new Grid(elements.Sum() + 2, 1);
            single.Expand(0, 0, false, elements, 0, patternWidth);
            return single.ToMatrix(null);
        }
        return ExpandedStacked(elements, patternWidth, codeBlocks, colsPerRow, dataChars, separator);
    }

    private static BarcodeMatrix ExpandedStacked(int[] elements, int patternWidth, int codeBlocks, int colsPerRow, int dataChars, int separator)
    {
        int stackRows = codeBlocks / colsPerRow;
        if (codeBlocks % colsPerRow > 0) stackRows++;
        int totalRows = 4 * stackRows - 3;
        var grid = new Grid(49 * colsPerRow + 24, totalRows);
        var weights = new int[totalRows];

        int currentBlock = 0;
        bool v2Latch = false;
        for (int currentRow = 1; currentRow <= stackRows; currentRow++)
        {
            int row = 4 * (currentRow - 1);
            weights[row] = 34;
            int elementsInSub = 2;
            var sub = new int[patternWidth + 8];
            int specialCaseRow = 0;

            int numColumns = currentRow * colsPerRow > codeBlocks ? codeBlocks - currentBlock : colsPerRow;
            sub[0] = 1; sub[1] = 1; // left guard

            // Last row, partial, even numbered, with even columns (segment pairs) and an odd number of finders.
            if (currentRow == stackRows && numColumns != colsPerRow && (currentRow & 1) == 0 && (colsPerRow & 1) == 0 && (numColumns & 1) == 1)
            {
                specialCaseRow = 1;
                sub[0] = 2; // extra space (the latch is set below)
            }
            bool leftToRight = (colsPerRow & 1) == 1 || (currentRow & 1) == 1 || specialCaseRow == 1;

            int reader = 0;
            do
            {
                int i = 2 + currentBlock * 21;
                for (int j = 0; j < 21; j++)
                {
                    if (i + j < patternWidth)
                    {
                        if (leftToRight) sub[j + reader * 21 + 2] = elements[i + j];
                        else sub[20 - j + (numColumns - 1 - reader) * 21 + 2] = elements[i + j];
                    }
                    elementsInSub++;
                }
                reader++;
                currentBlock++;
            } while (reader < colsPerRow && currentBlock < codeBlocks);

            sub[elementsInSub++] = 1; // right guard
            sub[elementsInSub++] = 1;

            bool latch = !((currentRow & 1) == 1 || specialCaseRow == 1);
            int writer = grid.Expand(row, 0, latch, sub, 0, elementsInSub);

            if (currentRow != 1)
            {
                bool oddLastRow = currentRow == stackRows && dataChars % 2 == 0;
                // Middle separator pattern (above the current row).
                for (int j = 5; j < 49 * colsPerRow; j += 2) grid.Set(row - 2, j);
                weights[row - 2] = separator;
                // Bottom separator pattern (above the current row).
                ExpandedSeparator(grid, writer, reader, row - 1, 1, specialCaseRow, leftToRight, oddLastRow, ref v2Latch);
                weights[row - 1] = separator;
            }
            if (currentRow != stackRows)
            {
                // Top separator pattern (below the current row).
                bool unusedOddLast = false;
                ExpandedSeparator(grid, writer, reader, row + 1, -1, 0, leftToRight, unusedOddLast, ref v2Latch);
                weights[row + 1] = separator;
            }
        }
        return grid.ToMatrix(weights);
    }

    /// <summary>Separator row of DataBar Expanded Stacked (ISO/IEC 24724:2011 7.2.8): the inverse of the adjacent row, adjusted at the finders.</summary>
    private static void ExpandedSeparator(Grid g, int width, int cols, int separatorRow, int aboveBelow, int specialCaseRow, bool leftToRight,
        bool oddLastRow, ref bool v2Latch)
    {
        int moduleRow = separatorRow + aboveBelow;
        bool spaceLatch = false;
        for (int j = 4 + specialCaseRow; j < width - 4; j++)
        {
            if (g.IsSet(moduleRow, j)) g.Unset(separatorRow, j); else g.Set(separatorRow, j);
        }

        // Finder adjustment.
        bool v2 = v2Latch;
        for (int j = 0; j < cols; j++)
        {
            // 49 = data (17) + finder (15) + data (17) triplet; 19 = 2 (guard) + 17 (first check or data character).
            int k = 49 * j + 19 + specialCaseRow;
            if (leftToRight)
            {
                // Last 13 modules of the version 2 finder and first 13 modules of the version 1 finder.
                int start = v2 ? 2 : 0, end = v2 ? 15 : 13;
                for (int i = start; i < end; i++) SeparatorModule(g, moduleRow, separatorRow, i + k, ref spaceLatch);
            }
            else
            {
                if (oddLastRow) k -= 17; // no data character at the beginning of the row: it ends with a finder
                // First 13 modules of the version 1 finder and last 13 modules of the version 2 finder.
                int start = v2 ? 14 : 12, end = v2 ? 2 : 0;
                for (int i = start; i >= end; i--) SeparatorModule(g, moduleRow, separatorRow, i + k, ref spaceLatch);
            }
            v2 = !v2;
        }
        if (aboveBelow == -1) v2Latch = v2; // only remembered when the separator is below a row
    }

    private static void SeparatorModule(Grid g, int moduleRow, int separatorRow, int x, ref bool spaceLatch)
    {
        if (g.IsSet(moduleRow, x))
        {
            g.Unset(separatorRow, x);
            spaceLatch = false;
        }
        else
        {
            if (spaceLatch) g.Unset(separatorRow, x); else g.Set(separatorRow, x);
            spaceLatch = !spaceLatch;
        }
    }
}
