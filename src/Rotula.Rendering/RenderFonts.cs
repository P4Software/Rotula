using System.Reflection;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering;

/// <summary>
/// Provides the bundled stand-in typefaces. Printers use their own resident fonts (for example ZPL font 0),
/// which are not redistributable, so the preview uses Liberation Sans / Liberation Mono (SIL OFL 1.1)
/// embedded in this assembly. Widths and line breaks therefore only approximate the printed result.
/// </summary>
public static class RenderFonts
{
    private static readonly Lazy<SKTypeface> Sans = new(() => Load("LiberationSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> SansBold = new(() => Load("LiberationSans-Bold.ttf"));
    private static readonly Lazy<SKTypeface> Mono = new(() => Load("LiberationMono-Regular.ttf"));

    /// <summary>Returns the shared typeface for a family/weight. Do not dispose it; it lives for the process.</summary>
    public static SKTypeface Get(RenderFontFamily family, bool bold) => family switch
    {
        // Only a regular Mono face is bundled; bold mono falls back to regular rather than faking it.
        RenderFontFamily.Mono => Mono.Value,
        _ => bold ? SansBold.Value : Sans.Value,
    };

    /// <summary>Returns the text of the font license, for an About / third-party notices screen.</summary>
    public static string LicenseText()
    {
        using var s = OpenResource("LICENSE-LiberationFonts.txt");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static SKTypeface Load(string file)
    {
        using var s = OpenResource(file);
        // FromStream copies what it needs; the typeface stays valid after the stream is closed.
        return SKTypeface.FromStream(s) ?? throw new InvalidOperationException($"The bundled font {file} could not be loaded.");
    }

    private static Stream OpenResource(string file) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("Rotula.Rendering.Fonts." + file)
        ?? throw new InvalidOperationException($"The bundled font resource {file} is missing from the application.");
}
