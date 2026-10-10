// UPS MaxiCode encoder for the designer preview.
//
// The module sequence, the character set tables and the secondary message compaction follow ISO/IEC 16023 as they are transcribed and
// implemented in the libzint library (backend/maxicode.c and maxicode.h), which this file was ported from and is checked against.
// libzint is distributed under the BSD-3-Clause licence:
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
// The minimal-length compaction ("backtracking") was contributed to libzint by Bue Jensen, taken from BWIPP (Barcode Writer in Pure
// PostScript, MIT licence).

namespace Rotula.Rendering.Barcodes;

/// <summary>
/// Encodes UPS MaxiCode (ISO/IEC 16023) into its 30 x 33 module grid. Modes 2 and 3 take ZPL's field data layout: a three-digit
/// class of service, a three-digit country code and the postal code (nine digits for mode 2, six characters for mode 3) followed by
/// the secondary message; modes 4 to 6 carry the whole field data as the message.
/// </summary>
internal static class MaxiCodeEncoder
{
    private const int Columns = 30, Rows = 33;

    // Code set states. A, B and E come first because they can carry the PAD character.
    private const int StateA = 0, StateB = 1, StateE = 2, StateC = 3, StateD = 4, States = 5;

    // Latch codewords to the state of the row from the prior state of the column (an empty entry means no latch).
    private static readonly int[][][] LatchSeq =
    {
        new[] { Array.Empty<int>(), new[] { 63 }, new[] { 58 }, new[] { 58 }, new[] { 58 } },                   // later A
        new[] { new[] { 63 }, Array.Empty<int>(), new[] { 63 }, new[] { 63 }, new[] { 63 } },                   // later B
        new[] { new[] { 62, 62 }, new[] { 62, 62 }, Array.Empty<int>(), new[] { 62, 62 }, new[] { 62, 62 } },   // later E
        new[] { new[] { 60, 60 }, new[] { 60, 60 }, new[] { 60, 60 }, Array.Empty<int>(), new[] { 60, 60 } },   // later C
        new[] { new[] { 61, 61 }, new[] { 61, 61 }, new[] { 61, 61 }, new[] { 61, 61 }, Array.Empty<int>() },   // later D
    };

    private static int LatchLen(int later, int prior) => LatchSeq[later][prior].Length;

    // Operations: the code set flag in the low bits, 0x20 marks a shift.
    private const int OpDigits = 0, OpSetA = 0x01, OpSetB = 0x02, OpSetE = 0x04, OpSetC = 0x08, OpSetD = 0x10;
    private const int OpShA = 0x20 | OpSetA, Op2ShA = 0x40 | OpSetA, Op3ShA = 0x80 | OpSetA;
    private const int OpShB = 0x20 | OpSetB, OpShE = 0x20 | OpSetE, OpShC = 0x20 | OpSetC, OpShD = 0x20 | OpSetD;

    private const int IdxShA = 6;

    // (operation, characters taken in) in the order of the indexes used below.
    private static readonly (int Op, int Intake)[] OpTable =
    {
        (OpDigits, 9), (OpSetA, 1), (OpSetB, 1), (OpSetE, 1), (OpSetC, 1), (OpSetD, 1),
        (OpShA, 1), (Op2ShA, 2), (Op3ShA, 3), (OpShB, 1), (OpShE, 1), (OpShC, 1), (OpShD, 1),
    };

    // Operation indexes that can be used from each state (digits are handled separately).
    private static readonly int[][] CodeSetOpIdxs =
    {
        new[] { 1, 9, 10, 11, 12 },          // A: set A, shift B, shift E, shift C, shift D
        new[] { 2, 6, 7, 8, 10, 11, 12 },    // B: set B, shift A, 2 shift A, 3 shift A, shift E, shift C, shift D
        new[] { 3, 11, 12 },                 // E
        new[] { 4, 10, 12 },                 // C
        new[] { 5, 10, 11 },                 // D
    };

    private static bool CanEncode(int op, int ch, int numA)
    {
        if (op is Op2ShA or Op3ShA) return numA >= 2 + (op == Op3ShA ? 1 : 0);
        return (MaxiCodeTables.CodeSet[ch] & op) != 0;
    }

    /// <summary>The symbol value of a character in the code set of <paramref name="op"/>, for characters that exist in several sets.</summary>
    private static int SymbolCh(int op, int ch)
    {
        if (MaxiCodeTables.CodeSet[ch] == (op & 0x1F) || (op & OpSetA) != 0) return MaxiCodeTables.SymbolChar[ch];
        if ((op & OpSetB) != 0)
        {
            int p = " ,./:".IndexOf((char)ch);
            if (p >= 0) return 47 + p;
        }
        if ((op & OpSetE) != 0 && ch is >= 28 and <= 30) return ch + 4; // FS GS RS
        return ch == ' ' ? 59 : ch; // SP CR FS GS RS
    }

    /// <summary>Encodes by <paramref name="op"/>, writing the codewords backwards from <paramref name="ci"/>.</summary>
    private static int Encode(int[] codewords, int ci, byte[] source, int i, int op)
    {
        if (op == OpDigits)
        {
            long value = 0;
            for (int k = 0; k < 9; k++) value = value * 10 + (source[i + k] - '0');
            codewords[--ci] = (int)(value & 0x3F);
            codewords[--ci] = (int)((value & 0xFC0) >> 6);
            codewords[--ci] = (int)((value & 0x3F000) >> 12);
            codewords[--ci] = (int)((value & 0xFC0000) >> 18);
            codewords[--ci] = (int)((value & 0x3F000000) >> 24);
            codewords[--ci] = 31; // NS
        }
        else if (op == Op2ShA)
        {
            codewords[--ci] = SymbolCh(op, source[i + 1]);
            codewords[--ci] = SymbolCh(op, source[i]);
            codewords[--ci] = 56;
        }
        else if (op == Op3ShA)
        {
            codewords[--ci] = SymbolCh(op, source[i + 2]);
            codewords[--ci] = SymbolCh(op, source[i + 1]);
            codewords[--ci] = SymbolCh(op, source[i]);
            codewords[--ci] = 57;
        }
        else
        {
            codewords[--ci] = SymbolCh(op, source[i]);
            if ((op & 0x20) != 0) // shift
                codewords[--ci] = 59 + (op == OpShC ? 1 : 0) + (op == OpShD ? 2 : 0) + (op == OpShE ? 3 : 0);
        }
        return ci;
    }

    private static int BestLength(int state, int i, int ch, int digits, int numA, int[,] bestLengths, int[,] bestOrigins, int[] pathOp, int[] priorCodeSet)
    {
        int minLen = 999999;
        if (digits >= 9)
        {
            // Nothing can beat nine digits in a row.
            int m = (i - 9) & 0x0F;
            int org = bestOrigins[m, state];
            int len = bestLengths[m, org] + LatchLen(state, org) + 6;
            if (len < minLen)
            {
                pathOp[state] = 0;
                priorCodeSet[state] = org;
                minLen = len;
            }
        }
        else
        {
            foreach (int opIdx in CodeSetOpIdxs[state])
            {
                var (op, intake) = OpTable[opIdx];
                if (!CanEncode(op, ch, numA)) continue;
                int m = (i - intake) & 0x0F;
                int org = bestOrigins[m, state];
                int len = bestLengths[m, org] + LatchLen(state, org) + intake + (opIdx >= IdxShA ? 1 : 0);
                if (len < minLen)
                {
                    pathOp[state] = opIdx;
                    priorCodeSet[state] = org;
                    minLen = len;
                }
            }
        }
        return minLen;
    }

    private static int BestOrigin(int state, int[,] bestLengths, int row)
    {
        int orgLen = bestLengths[row, 0] + LatchLen(state, 0);
        int bestOrg = 0;
        for (int org = 1; org < States; org++)
        {
            int len = bestLengths[row, org] + LatchLen(state, org);
            if (len < orgLen) { bestOrg = org; orgLen = len; }
        }
        return bestOrg;
    }

    /// <summary>The secondary message: minimal-length compaction by dynamic programming over the five code sets.</summary>
    private static void ProcessText(int[] codewords, int mode, byte[] source, int structuredAppend)
    {
        var bestLengths = new int[16, States];
        var bestOrigins = new int[16, States];
        int cp = 20 - (mode > 3 ? 9 : 0); // offset the first codeword to avoid copying later
        int maxLen = (mode == 5 ? 77 : 93) + 11;
        int length = source.Length;
        var priorCodeSets = new int[length + 9][];
        var pathOps = new int[length + 9][];
        for (int k = 0; k < priorCodeSets.Length; k++) { priorCodeSets[k] = new int[States]; pathOps[k] = new int[States]; }

        if (structuredAppend >= 0)
        {
            codewords[cp++] = 33; // PAD
            codewords[cp++] = structuredAppend;
        }

        int ci = cp;
        int digits = 0, numA = 0;
        for (int i = 0; i < length; i++)
        {
            int ch = source[i];
            digits = ch is >= '0' and <= '9' ? digits + 1 : 0;
            numA = (MaxiCodeTables.CodeSet[ch] & OpSetA) != 0 ? numA + 1 : 0;
            int row = i & 0x0F;
            var lengths = new int[States];
            for (int state = 0; state < States; state++)
                lengths[state] = BestLength(state, i, ch, digits, numA, bestLengths, bestOrigins, pathOps[i], priorCodeSets[i]);
            for (int state = 0; state < States; state++) bestLengths[row, state] = lengths[state];
            for (int state = 0; state < States; state++) bestOrigins[row, state] = BestOrigin(state, bestLengths, row);
        }

        // The best code set to end with.
        int minLen = 0, minState = StateA;
        if (length > 0)
        {
            minLen = 999999;
            int last = (length - 1) & 0x0F;
            for (int state = 0; state < States; state++)
                if (bestLengths[last, state] < minLen) { minLen = bestLengths[last, state]; minState = state; }
        }
        if (ci + minLen > maxLen) throw new BarcodeEncodingException("The data needs too many codewords for MaxiCode.");

        // Follow the best path back to the start of the message.
        ci += minLen;
        int ciTop = ci;
        int current = minState;
        int pos = length;
        while (pos > 0)
        {
            int chI = pos - 1;
            int pcs = priorCodeSets[chI][current];
            var (op, intake) = OpTable[pathOps[chI][current]];
            pos -= intake;
            ci = Encode(codewords, ci, source, pos, op);
            if (current != pcs)
            {
                var latch = LatchSeq[current][pcs];
                for (int j = 0; j < latch.Length; j++) codewords[--ci] = latch[j];
                current = pcs;
            }
        }

        cp = ciTop;
        // End in code set C or D: switch to A for the padding.
        if (cp < maxLen && (minState == StateC || minState == StateD)) codewords[cp++] = 58;
        for (; cp < maxLen; cp++) codewords[cp] = minState == StateE ? 28 : 33;

        // The first nine codewords of a mode 4 to 6 message belong to the primary message.
        if (mode > 3) Array.Copy(codewords, 20 - 9, codewords, 1, 9);
    }

    // ---- Reed-Solomon over GF(64), primitive polynomial x^6 + x + 1 -----------------------------------------

    private static readonly int[] Antilog = new int[126];
    private static readonly int[] Log = new int[64];

    static MaxiCodeEncoder()
    {
        int x = 1;
        for (int i = 0; i < 63; i++)
        {
            Antilog[i] = x;
            Antilog[i + 63] = x;
            Log[x] = i;
            x <<= 1;
            if (x >= 64) x ^= 0x43;
        }
    }

    /// <summary>The Reed-Solomon check codewords (generator roots alpha^1 to alpha^nsym) of <paramref name="data"/>.</summary>
    private static int[] ReedSolomon(int[] data, int nsym)
    {
        var poly = new int[nsym + 1];
        poly[0] = 1;
        int index = 1;
        for (int i = 1; i <= nsym; i++)
        {
            poly[i] = 1;
            for (int k = i - 1; k > 0; k--)
            {
                if (poly[k] != 0) poly[k] = Antilog[Log[poly[k]] + index];
                poly[k] ^= poly[k - 1];
            }
            poly[0] = Antilog[Log[poly[0]] + index];
            index++;
        }

        var res = new int[nsym];
        foreach (int d in data)
        {
            int m = res[nsym - 1] ^ d;
            if (m != 0)
            {
                int logM = Log[m];
                for (int k = nsym - 1; k > 0; k--)
                    res[k] = res[k - 1] ^ (poly[k] != 0 ? Antilog[logM + Log[poly[k]]] : 0);
                res[0] = Antilog[logM + Log[poly[0]]];
            }
            else
            {
                for (int k = nsym - 1; k > 0; k--) res[k] = res[k - 1];
                res[0] = 0;
            }
        }
        Array.Reverse(res);
        return res;
    }

    private static void PrimaryEcc(int[] codewords)
    {
        var ecc = ReedSolomon(codewords[..10], 10);
        for (int j = 0; j < 10; j++) codewords[10 + j] = ecc[j];
    }

    private static void SecondaryEcc(int[] codewords, int dataLength, int eccLength)
    {
        // The secondary message is protected as two interleaved halves (even and odd codewords).
        for (int half = 0; half < 2; half++)
        {
            var data = new int[dataLength / 2];
            for (int j = 0; j < dataLength; j += 2) data[j >> 1] = codewords[j + half + 20];
            var ecc = ReedSolomon(data, eccLength);
            for (int j = 0; j < eccLength; j++) codewords[dataLength + (j << 1) + half + 20] = ecc[j];
        }
    }

    // ---- primary message ---------------------------------------------------------------------------------

    private static void PrimaryMode2(int[] cw, int postcode, int postcodeLength, int country, int service)
    {
        cw[0] = ((postcode & 0x03) << 4) | 2;
        cw[1] = (postcode & 0xFC) >> 2;
        cw[2] = (postcode & 0x3F00) >> 8;
        cw[3] = (postcode & 0xFC000) >> 14;
        cw[4] = (postcode & 0x3F00000) >> 20;
        cw[5] = ((postcode & 0x3C000000) >> 26) | ((postcodeLength & 0x03) << 4);
        cw[6] = ((postcodeLength & 0x3C) >> 2) | ((country & 0x03) << 4);
        cw[7] = (country & 0xFC) >> 2;
        cw[8] = ((country & 0x300) >> 8) | ((service & 0x0F) << 2);
        cw[9] = (service & 0x3F0) >> 4;
    }

    private static void PrimaryMode3(int[] cw, int[] postcode, int country, int service)
    {
        cw[0] = ((postcode[5] & 0x03) << 4) | 3;
        cw[1] = ((postcode[4] & 0x03) << 4) | ((postcode[5] & 0x3C) >> 2);
        cw[2] = ((postcode[3] & 0x03) << 4) | ((postcode[4] & 0x3C) >> 2);
        cw[3] = ((postcode[2] & 0x03) << 4) | ((postcode[3] & 0x3C) >> 2);
        cw[4] = ((postcode[1] & 0x03) << 4) | ((postcode[2] & 0x3C) >> 2);
        cw[5] = ((postcode[0] & 0x03) << 4) | ((postcode[1] & 0x3C) >> 2);
        cw[6] = ((postcode[0] & 0x3C) >> 2) | ((country & 0x03) << 4);
        cw[7] = (country & 0xFC) >> 2;
        cw[8] = ((country & 0x300) >> 8) | ((service & 0x0F) << 2);
        cw[9] = (service & 0x3F0) >> 4;
    }

    private static int Number(string s) => s.All(c => c is >= '0' and <= '9') && s.Length > 0 ? int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : -1;

    /// <summary>Encodes a MaxiCode symbol; the result is indexed [column, row].</summary>
    /// <param name="mode">2 to 6.</param>
    /// <param name="data">ZPL field data (see the class remarks); characters above U+00FF cannot be encoded.</param>
    /// <param name="symbolNumber">Number of this symbol in a structured append set, 1-8.</param>
    /// <param name="symbolTotal">Total symbols in the set, 1-8 (1 = not structured).</param>
    public static bool[,] Encode(int mode, string data, int symbolNumber, int symbolTotal)
    {
        if (mode is < 2 or > 6) throw new BarcodeEncodingException("MaxiCode modes are 2 to 6.");
        if (data.Any(c => c > 0xFF)) throw new BarcodeEncodingException("MaxiCode encodes ISO 8859-1 characters only.");
        var codewords = new int[144];
        string message = data;

        if (mode <= 3)
        {
            int primaryLength = mode == 2 ? 15 : 12;
            if (data.Length < primaryLength) throw new BarcodeEncodingException($"MaxiCode mode {mode} needs the {primaryLength} character primary message first.");
            int service = Number(data[..3]), country = Number(data.Substring(3, 3));
            if (service < 0 || country < 0) throw new BarcodeEncodingException("The class of service and the country code of MaxiCode must be numeric.");
            string postcode = data.Substring(6, primaryLength - 6);
            if (mode == 2)
            {
                int number = Number(postcode);
                if (number < 0) throw new BarcodeEncodingException("The postal code of MaxiCode mode 2 must be numeric.");
                PrimaryMode2(codewords, number, postcode.Length, country, service);
            }
            else
            {
                var symbols = new int[6];
                string upper = postcode.ToUpperInvariant();
                for (int i = 0; i < 6; i++)
                {
                    char c = upper[i];
                    if (c < ' ' || (MaxiCodeTables.CodeSet[c] & OpSetA) == 0)
                        throw new BarcodeEncodingException("The postal code of MaxiCode mode 3 has a character that is not allowed.");
                    symbols[i] = MaxiCodeTables.SymbolChar[c];
                }
                PrimaryMode3(codewords, symbols, country, service);
            }
            message = data[primaryLength..];
        }
        else
        {
            codewords[0] = mode;
        }

        int structured = symbolTotal > 1 ? (symbolTotal - 1) | ((symbolNumber - 1) << 3) : -1;
        ProcessText(codewords, mode, message.Select(c => (byte)c).ToArray(), structured);

        PrimaryEcc(codewords);
        if (mode == 5) SecondaryEcc(codewords, 68, 28); // enhanced error correction: 68 data + 56 check codewords
        else SecondaryEcc(codewords, 84, 20);           // standard error correction: 84 data + 40 check codewords

        // Copy the codewords into the module grid by the sequence of ISO/IEC 16023 Figure 5.
        var grid = new bool[Columns, Rows];
        for (int row = 0; row < Rows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int seq = MaxiCodeTables.Grid[row * Columns + col] + 5;
                int block = seq / 6;
                if (block != 0 && ((codewords[block - 1] >> (5 - seq % 6)) & 1) == 1) grid[col, row] = true;
            }
        }

        // Orientation markings.
        void Mark(int row, int col) => grid[col, row] = true;
        Mark(0, 28); Mark(0, 29);   // top right filler
        Mark(9, 10); Mark(9, 11); Mark(10, 11);   // top left marker
        Mark(15, 7); Mark(16, 8);   // left hand marker
        Mark(16, 20); Mark(17, 20); // right hand marker
        Mark(22, 10); Mark(23, 10); // bottom left marker
        Mark(22, 17); Mark(23, 17); // bottom right marker
        return grid;
    }
}
