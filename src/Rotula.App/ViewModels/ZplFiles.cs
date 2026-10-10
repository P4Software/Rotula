using System.Text;
using Rotula.Core.Zpl;

namespace Rotula.App.ViewModels;

/// <summary>Writes ZPL text to files in the encoding the label declares (^CI), the way a printer expects it.</summary>
public static class ZplFiles
{
    /// <summary>
    /// Encodes <paramref name="zpl"/> with the label's code page: UTF-8 (no BOM) for ^CI28, the matching legacy page otherwise.
    /// Returns null when a character cannot be represented, so the caller can tell the user instead of writing "?".
    /// </summary>
    public static byte[]? TryEncode(string zpl, ZplEncoding encoding) => TryEncode(zpl, encoding, Array.Empty<string>());

    /// <summary>
    /// Like <see cref="TryEncode(string, ZplEncoding)"/>, but every occurrence of a block in <paramref name="byteExactBlocks"/>
    /// (raw ZPL imported from bytes, see <c>Rotula.Printing.ByteExactBlocks</c>) is written as Latin-1, one byte per char.
    /// Why: re-encoding such a char as UTF-8 would turn one original byte into two and corrupt the command. This mirrors the
    /// printing path so an exported file is identical to what would be sent to the printer.
    /// </summary>
    public static byte[]? TryEncode(string zpl, ZplEncoding encoding, IReadOnlyCollection<string> byteExactBlocks)
    {
        try
        {
            Encoding enc = encoding.GetEncoding();
            if (byteExactBlocks.Count == 0) return enc.GetBytes(zpl);

            var ranges = new List<(int Start, int End)>();
            foreach (var block in byteExactBlocks)
            {
                if (block.Length == 0) continue;
                for (int i = zpl.IndexOf(block, StringComparison.Ordinal); i >= 0; i = zpl.IndexOf(block, i + block.Length, StringComparison.Ordinal))
                    ranges.Add((i, i + block.Length));
            }
            ranges.Sort();

            using var ms = new MemoryStream();
            int pos = 0;
            foreach (var (start, end) in ranges)
            {
                if (end <= pos) continue; // nested in an earlier range
                int s = Math.Max(start, pos); // overlapping ranges are merged
                if (s > pos) ms.Write(enc.GetBytes(zpl[pos..s]));
                ms.Write(Encoding.Latin1.GetBytes(zpl[s..end]));
                pos = end;
            }
            if (pos < zpl.Length) ms.Write(enc.GetBytes(zpl[pos..]));
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
