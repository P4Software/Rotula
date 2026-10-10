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

    /// <summary>The number sent with ^CI.</summary>
    public int CiCode { get; }

    /// <summary>.NET code page used to turn text into printer bytes, or null for UTF-8.</summary>
    public int? DotNetCodePage { get; }

    /// <summary>True for UTF-8.</summary>
    public bool IsUtf8 => DotNetCodePage is null;

    /// <summary>The .NET encoding that produces the bytes sent to the printer.</summary>
    internal Encoding GetEncoding() =>
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
