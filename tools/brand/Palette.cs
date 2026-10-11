using SkiaSharp;

namespace Rotula.Brand;

/// <summary>The brand colours, taken from the app theme (warm off-white, burnt orange, charcoal) with one lifted orange for use on dark.</summary>
internal static class Palette
{
    /// <summary>Charcoal of the code strip and the tile.</summary>
    public const string Charcoal = "#17181D";
    /// <summary>Top of the tile gradient.</summary>
    public const string TileTop = "#2C2E37";
    /// <summary>Bottom of the tile gradient.</summary>
    public const string TileBottom = "#16171C";
    /// <summary>Bars on the dark tile.</summary>
    public const string BarLight = "#F6F3EE";
    /// <summary>The brand accent (Theme.xaml AccentColor), used on light surfaces.</summary>
    public const string Accent = "#C8420F";
    /// <summary>The accent lifted a little so it keeps its punch on the charcoal tile.</summary>
    public const string AccentOnDark = "#EE5A1E";
    /// <summary>Peeled flap on the tile, light edge.</summary>
    public const string FlapTop = "#5A5D69";
    /// <summary>Peeled flap on the tile, shaded edge.</summary>
    public const string FlapBottom = "#383A44";
    /// <summary>Page fill (top).</summary>
    public const string PageTop = "#FFFFFF";
    /// <summary>Page fill (bottom).</summary>
    public const string PageBottom = "#F1EEE8";
    /// <summary>Page outline.</summary>
    public const string PageEdge = "#7C808A";
    /// <summary>Folded corner of the page.</summary>
    public const string PageFlap = "#DCD6CB";
    /// <summary>Warm window background of the app.</summary>
    public const string Warm = "#F0EDE8";
    /// <summary>Muted text.</summary>
    public const string Muted = "#62666D";

    /// <summary>Parses a #RRGGBB colour.</summary>
    public static SKColor C(string hex) => SKColor.Parse(hex);
}
