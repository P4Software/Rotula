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
    public static byte[]? TryEncode(string zpl, ZplEncoding encoding)
    {
        try
        {
            // DotNetCodePage is read after the ZplEncoding static constructor registered the legacy code pages.
            Encoding enc = encoding.DotNetCodePage is { } page
                ? Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(false);
            return enc.GetBytes(zpl);
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
