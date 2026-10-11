using System.Text;
using Rotula.Core.Gs1;
using ZXing.Datamatrix.Encoder;

namespace Rotula.Rendering.Barcodes;

/// <summary>
/// Draws a GS1 DataMatrix (ECC 200, square) for the preview: the first codeword is FNC1 (232), which marks the symbol as GS1, every
/// element of variable length that is not the last is followed by another FNC1, and the text is written in ASCII encodation with digit
/// pairs. ZXing.Net's own writer is not used for the encodation because its GS1 hint is not honoured on every path; only its tables
/// (symbol sizes, Reed-Solomon, module placement) are.
/// </summary>
/// <remarks>
/// A printer chooses the encodation modes (C40, text, X12, EDIFACT) that give the smallest symbol, so its symbol can be smaller than
/// this one. The data and the GS1 marking are the same.
/// </remarks>
internal static class DataMatrixGs1Encoder
{
    private const int Fnc1 = 232;
    private const int Pad = 129;

    /// <summary>The codewords before padding: FNC1, then the data in ASCII encodation with FNC1 between the elements.</summary>
    internal static List<int> DataCodewords(Gs1Message message)
    {
        var words = new List<int> { Fnc1 };
        var els = message.Elements;
        for (int e = 0; e < els.Count; e++)
        {
            string text = els[e].Ai + els[e].Value;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c > 127) throw new BarcodeEncodingException($"The character \"{c}\" cannot be encoded in a GS1 DataMatrix.");
                // Two digits in a row share one codeword (ISO/IEC 16022, ASCII encodation).
                if (c is >= '0' and <= '9' && i + 1 < text.Length && text[i + 1] is >= '0' and <= '9')
                {
                    words.Add(130 + (c - '0') * 10 + (text[i + 1] - '0'));
                    i++;
                }
                else words.Add(c + 1);
            }
            if (e < els.Count - 1 && Gs1Message.NeedsSeparator(els[e].Ai)) words.Add(Fnc1);
        }
        return words;
    }

    /// <summary>Encodes the message as a square ECC 200 symbol.</summary>
    internal static bool[,] Encode(Gs1Message message)
    {
        var words = DataCodewords(message);
        SymbolInfo? symbol = SymbolInfo.lookup(words.Count, false, false);
        if (symbol is null) throw new BarcodeEncodingException("The GS1 data is too long for a Data Matrix symbol.");

        int errorTotal = symbol.getErrorLengthForInterleavedBlock(1) * symbol.getInterleavedBlockCount();
        int capacity = symbol.getCodewordCount() - errorTotal;
        var sb = new StringBuilder();
        foreach (int w in words) sb.Append((char)w);
        // Padding: one fixed pad codeword, then pseudo-random ones (the 253-state randomisation of ISO/IEC 16022).
        if (sb.Length < capacity) sb.Append((char)Pad);
        while (sb.Length < capacity)
        {
            int pseudoRandom = 149 * (sb.Length + 1) % 253 + 1;
            int value = Pad + pseudoRandom;
            sb.Append((char)(value <= 254 ? value : value - 254));
        }

        string codewords = ErrorCorrection.encodeECC200(sb.ToString(), symbol);
        var placement = new DefaultPlacement(codewords, symbol.getSymbolDataWidth(), symbol.getSymbolDataHeight());
        placement.place();

        // Assemble the symbol: finder pattern (solid left column and bottom row, alternating top row and right column) around each data
        // region, exactly as DataMatrixWriter.encodeLowLevel does.
        int dataWidth = symbol.getSymbolDataWidth(), dataHeight = symbol.getSymbolDataHeight();
        var matrix = new bool[symbol.getSymbolWidth(), symbol.getSymbolHeight()];
        int my = 0;
        for (int y = 0; y < dataHeight; y++)
        {
            int mx;
            if (y % symbol.matrixHeight == 0)
            {
                mx = 0;
                for (int x = 0; x < symbol.getSymbolWidth(); x++) matrix[mx++, my] = x % 2 == 0;
                my++;
            }
            mx = 0;
            for (int x = 0; x < dataWidth; x++)
            {
                if (x % symbol.matrixWidth == 0) matrix[mx++, my] = true;
                matrix[mx++, my] = placement.getBit(x, y);
                if (x % symbol.matrixWidth == symbol.matrixWidth - 1) matrix[mx++, my] = y % 2 == 0;
            }
            my++;
            if (y % symbol.matrixHeight == symbol.matrixHeight - 1)
            {
                mx = 0;
                for (int x = 0; x < symbol.getSymbolWidth(); x++) matrix[mx++, my] = true;
                my++;
            }
        }
        return matrix;
    }
}
