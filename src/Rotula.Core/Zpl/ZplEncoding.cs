using System.Text;

namespace Rotula.Core.Zpl;

/// <summary>
/// Character encoding of the label (^CI). The default is UTF-8 (^CI28) so accents, the n-tilde and
/// inverted punctuation print correctly; legacy single-byte code pages are available for old printers.
/// </summary>
public sealed class ZplEncoding : IEquatable<ZplEncoding>
{
    static ZplEncoding()
    {
        // Legacy code pages live in the CodePages provider, which is not registered by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private ZplEncoding(int ciCode, int? dotNetCodePage)
    {
        CiCode = ciCode;
        DotNetCodePage = dotNetCodePage;
    }

    /// <summary>UTF-8, emitted as ^CI28 (the default).</summary>
    public static ZplEncoding Utf8 { get; } = new(28, null);

    /// <summary>Zebra code page 850, ^CI13.</summary>
    public static ZplEncoding Cp850 { get; } = new(13, 850);

    /// <summary>Zebra code page 1252 (Windows Latin-1), ^CI27.</summary>
    public static ZplEncoding Cp1252 { get; } = new(27, 1252);

    /// <summary>Creates a legacy encoding from a ^CI number and the matching .NET code page used to produce the bytes.</summary>
    public static ZplEncoding Legacy(int ciCode, int dotNetCodePage) => new(ciCode, dotNetCodePage);

    /// <summary>
    /// Maps a ^CI number found in existing ZPL to an encoding. Returns false for sets Rotula cannot reproduce
    /// (double-byte Asian sets, UTF-16, remap-only variants); the caller should fall back to UTF-8 and warn.
    /// </summary>
    /// <param name="ciCode">The number after ^CI.</param>
    /// <param name="encoding">The encoding when the method returns true.</param>
    /// <param name="approximated">
    /// True for ^CI0-12, which are Zebra Code Page 850 with national character replacements; the replacements are not modelled.
    /// </param>
    public static bool TryFromCiCode(int ciCode, out ZplEncoding encoding, out bool approximated)
    {
        approximated = false;
        switch (ciCode)
        {
            case 28: encoding = Utf8; return true;
            case 13: encoding = Cp850; return true;
            case 27: encoding = Cp1252; return true;
            case >= 0 and <= 12:
                encoding = Legacy(ciCode, 850);
                approximated = ciCode != 0;
                return true;
            case 31: encoding = Legacy(31, 1250); return true;
            case 33: encoding = Legacy(33, 1251); return true;
            case 34: encoding = Legacy(34, 1253); return true;
            case 35: encoding = Legacy(35, 1254); return true;
            case 36: encoding = Legacy(36, 1255); return true;
            default: encoding = Utf8; return false;
        }
    }

    /// <summary>The number sent with ^CI.</summary>
    public int CiCode { get; }

    /// <summary>.NET code page used to turn text into printer bytes, or null for UTF-8.</summary>
    public int? DotNetCodePage { get; }

    /// <summary>True for UTF-8.</summary>
    public bool IsUtf8 => DotNetCodePage is null;

    /// <summary>
    /// The .NET encoding that produces the bytes sent to the printer. It throws on characters the
    /// code page cannot hold (strict fallback) so callers can report the problem instead of
    /// silently printing a wrong character. Public so Printing and the app share one lookup.
    /// </summary>
    public Encoding GetEncoding() =>
        DotNetCodePage is null ? new UTF8Encoding(false) : Encoding.GetEncoding(DotNetCodePage.Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <inheritdoc />
    public bool Equals(ZplEncoding? other) => other is not null && CiCode == other.CiCode && DotNetCodePage == other.DotNetCodePage;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ZplEncoding);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(CiCode, DotNetCodePage);

    /// <inheritdoc />
    public override string ToString() => $"^CI{CiCode}";
}
