using System.Text;
using Rotula.Core.Barcodes;
using Rotula.Core.Model;

namespace Rotula.Core.Zpl.Import;

/// <summary>
/// Turns existing ZPL II into editable <see cref="LabelDocument"/>s. The parser understands every command
/// <see cref="ZplGenerator"/> writes plus the usual real-world ones. Anything else is kept as raw ZPL (never dropped)
/// and listed as a warning; stored-object recalls (<c>^XG ^IL ^XF</c>) become placeholders. It never throws on bad input.
/// </summary>
public static class ZplParser
{
    /// <summary>Parses ZPL that is already text (for example a string the user pasted).</summary>
    /// <param name="zpl">The ZPL program.</param>
    /// <param name="options">Import options; null for defaults (203 DPI).</param>
    public static ZplParseResult Parse(string zpl, ZplParseOptions? options = null) =>
        new ZplImporter(zpl ?? string.Empty, bytesMode: false, options ?? new ZplParseOptions(), utf8Bom: false).Run();

    /// <summary>
    /// Parses ZPL as raw file bytes. Prefer this for files: text inside <c>^FD</c> is decoded with the <c>^CI</c> that is active
    /// at that point (for example code page 850 for <c>^CI13</c>), which a premature UTF-8 decoding of the file would corrupt.
    /// </summary>
    /// <param name="zpl">The file content.</param>
    /// <param name="options">Import options; null for defaults (203 DPI).</param>
    public static ZplParseResult Parse(ReadOnlySpan<byte> zpl, ZplParseOptions? options = null)
    {
        options ??= new ZplParseOptions();
        if (zpl.Length >= 2 && ((zpl[0] == 0xFF && zpl[1] == 0xFE) || (zpl[0] == 0xFE && zpl[1] == 0xFF)))
        {
            // A UTF-16 file: decode it as text; there are no per-field bytes to preserve.
            var enc = zpl[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            return new ZplImporter(enc.GetString(zpl[2..]), bytesMode: false, options, utf8Bom: false).Run();
        }
        bool bom = zpl.Length >= 3 && zpl[0] == 0xEF && zpl[1] == 0xBB && zpl[2] == 0xBF;
        if (bom) zpl = zpl[3..];
        // Latin-1 maps every byte to one char, so offsets stay byte offsets and no byte is lost.
        return new ZplImporter(Encoding.Latin1.GetString(zpl), bytesMode: true, options, utf8Bom: bom).Run();
    }

    /// <summary>Reads and parses a ZPL file. An unreadable file produces an empty label and an <c>Import.File.Unreadable</c> warning.</summary>
    public static ZplParseResult ParseFile(string path, ZplParseOptions? options = null)
    {
        try
        {
            return Parse(File.ReadAllBytes(path), options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ZplImporter.Failed("Import.File.Unreadable", ex.GetType().Name);
        }
    }
}
