using System.IO.Compression;
using System.Text;

namespace Rotula.Core.Zpl.Import;

/// <summary>Decodes the ASCII data of a <c>^GFA</c> graphic field (plain hex, ZPL run-length compression, Z64/B64 wrappers).</summary>
internal static class ZplGraphicData
{
    /// <summary>Upper bound for the decoded bitmap so a hostile file cannot exhaust memory.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Decodes <paramref name="data"/> into exactly <paramref name="totalBytes"/> packed bytes.
    /// </summary>
    /// <param name="data">The text after the fourth comma of <c>^GFA</c>.</param>
    /// <param name="totalBytes">Total bytes of the graphic (<c>^GF</c> parameter c).</param>
    /// <param name="bytesPerRow">Bytes per row (<c>^GF</c> parameter d).</param>
    /// <param name="packed">The bitmap bytes (1 = black); padded with zeros when the data was short.</param>
    /// <param name="truncated">True when the data ended before the declared size.</param>
    /// <returns>False when the payload is not decodable (bad base64, bad compression stream, bad size).</returns>
    public static bool TryDecode(string data, int totalBytes, int bytesPerRow, out byte[] packed, out bool truncated)
    {
        packed = Array.Empty<byte>();
        truncated = false;
        if (totalBytes < 1 || totalBytes > MaxBytes || bytesPerRow < 1 || bytesPerRow > totalBytes) return false;

        string text = data.Trim();
        try
        {
            if (text.StartsWith(":Z64:", StringComparison.OrdinalIgnoreCase) || text.StartsWith(":B64:", StringComparison.OrdinalIgnoreCase))
            {
                bool zlib = char.ToUpperInvariant(text[1]) == 'Z';
                int end = text.IndexOf(':', 5);
                string b64 = (end < 0 ? text[5..] : text[5..end]).Replace("\r", "").Replace("\n", "").Replace(" ", "");
                byte[] raw = Convert.FromBase64String(b64);
                if (zlib)
                {
                    using var input = new MemoryStream(raw);
                    using var z = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    int n;
                    // Bounded read: the compressed text is at most two characters per bitmap byte plus compression markers.
                    while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        if (output.Length > (long)totalBytes * 8 + 1024) return false;
                    }
                    raw = output.ToArray();
                }
                text = Encoding.ASCII.GetString(raw);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            return false;
        }

        int rowNibbles = bytesPerRow * 2;
        int totalNibbles = totalBytes * 2;
        var nibbles = new byte[totalNibbles];
        int pos = 0;
        int count = 0;

        void Put(byte nibble, int times)
        {
            for (int k = 0; k < times && pos < totalNibbles; k++) nibbles[pos++] = nibble;
        }

        foreach (char ch in text)
        {
            if (pos >= totalNibbles) break;
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is >= 'G' and <= 'Y') { count += ch - 'G' + 1; continue; }
            if (ch is >= 'g' and <= 'z') { count += (ch - 'g' + 1) * 20; continue; }
            if (Uri.IsHexDigit(ch))
            {
                Put((byte)Convert.ToInt32(ch.ToString(), 16), count == 0 ? 1 : count);
                count = 0;
                continue;
            }
            switch (ch)
            {
                case ',':
                case '!':
                    // Fill the rest of the row (a whole row when already at the start of one).
                    Put(ch == ',' ? (byte)0 : (byte)15, rowNibbles - pos % rowNibbles);
                    break;
                case ':':
                    if (pos >= rowNibbles && pos % rowNibbles == 0)
                        for (int k = 0; k < rowNibbles && pos < totalNibbles; k++, pos++) nibbles[pos] = nibbles[pos - rowNibbles];
                    break;
                default:
                    break; // Unknown characters are ignored like a printer would.
            }
            count = 0;
        }

        truncated = pos < totalNibbles;
        packed = new byte[totalBytes];
        for (int i = 0; i < totalBytes; i++) packed[i] = (byte)((nibbles[2 * i] << 4) | nibbles[2 * i + 1]);
        return true;
    }
}
