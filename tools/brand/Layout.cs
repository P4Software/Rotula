using System.Globalization;

namespace Rotula.Brand;

/// <summary>One bar of the barcode motif, in pixels of the icon it belongs to.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
/// <param name="Accent">True for the single burnt-orange bar.</param>
internal sealed record Bar(float X, float Y, float W, float H, bool Accent);

/// <summary>Which of the two shapes a <see cref="Layout"/> describes.</summary>
internal enum Shape
{
    /// <summary>The app mark: a dark rounded square with a peeled corner.</summary>
    Tile,

    /// <summary>The .rotula document: a light page with a folded corner.</summary>
    Page,
}

/// <summary>
/// Everything needed to draw one icon at one pixel size. The raster, SVG and XAML writers all consume this, so the mark
/// is defined in exactly one place. Sizes 16, 20 and 24 are hand-tuned on the pixel grid (fewer, thicker bars, no peel on the
/// tile); 32 and up are derived from one 256-unit master drawing, with every edge snapped to whole pixels up to 64 px.
/// </summary>
/// <param name="Kind">Tile or page.</param>
/// <param name="Size">Canvas size in pixels (square).</param>
/// <param name="X">Left edge of the body shape.</param>
/// <param name="Y">Top edge of the body shape.</param>
/// <param name="W">Width of the body shape.</param>
/// <param name="H">Height of the body shape.</param>
/// <param name="Radius">Corner radius of the body shape.</param>
/// <param name="Fold">Size of the peeled or folded corner; 0 for none.</param>
/// <param name="Bars">The barcode bars.</param>
/// <param name="Edge">Width of the inner rim (tile) or outline (page), in pixels.</param>
internal sealed record Layout(Shape Kind, int Size, float X, float Y, float W, float H, float Radius, float Fold, IReadOnlyList<Bar> Bars, float Edge)
{
    /// <summary>Master drawing size.</summary>
    public const int Master = 256;

    /// <summary>Bar widths of the master tile in modules; every bar is followed by a one-module gap. The 3-wide third bar is the accent.</summary>
    private static readonly int[] TileBarModules = [2, 1, 3, 1, 2, 1, 2];

    /// <summary>Bar widths of the master page in modules.</summary>
    private static readonly int[] PageBarModules = [2, 1, 3, 1, 3];

    /// <summary>Index of the accent bar in both patterns.</summary>
    private const int AccentIndex = 2;

    /// <summary>SVG path data of the body outline: rounded square or page, with the cut corner when <see cref="Fold"/> is positive.</summary>
    public string BodyPath()
    {
        float r = Radius, f = Fold, right = X + W, bottom = Y + H;
        string top = f > 0
            ? $"H{N(right - f)}L{N(right)} {N(Y + f)}"
            : $"H{N(right - r)}A{N(r)} {N(r)} 0 0 1 {N(right)} {N(Y + r)}";
        return $"M{N(X + r)} {N(Y)}{top}V{N(bottom - r)}A{N(r)} {N(r)} 0 0 1 {N(right - r)} {N(bottom)}H{N(X + r)}A{N(r)} {N(r)} 0 0 1 {N(X)} {N(bottom - r)}V{N(Y + r)}A{N(r)} {N(r)} 0 0 1 {N(X + r)} {N(Y)}Z";
    }

    /// <summary>SVG path data of the folded flap triangle, or an empty string when there is no fold.</summary>
    public string FlapPath()
    {
        if (Fold <= 0) return "";
        float right = X + W;
        return $"M{N(right - Fold)} {N(Y)}L{N(right)} {N(Y + Fold)}H{N(right - Fold)}Z";
    }

    /// <summary>SVG path data of all bars of one colour class.</summary>
    public string BarsPath(bool accent) =>
        string.Concat(Bars.Where(b => b.Accent == accent).Select(b => $"M{N(b.X)} {N(b.Y)}h{N(b.W)}v{N(b.H)}h{N(-b.W)}z"));

    /// <summary>Formats a number the same way in every writer: invariant, at most two decimals, no trailing zeros.</summary>
    public static string N(float v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The layout of the app mark at the given pixel size.</summary>
    public static Layout ForTile(int px) => px switch
    {
        // Hand-tuned: 1 px gaps, whole-pixel bars, no peel (it would only add noise this small).
        16 => new(Shape.Tile, 16, 0, 0, 16, 16, 4, 0, Hand(3, 3, 10, (0, 2, false), (3, 1, false), (5, 2, true), (8, 2, false)), 1),
        20 => new(Shape.Tile, 20, 0, 0, 20, 20, 5, 0, Hand(4, 4, 12, (0, 2, false), (3, 1, false), (5, 2, true), (8, 1, false), (10, 2, false)), 1),
        24 => new(Shape.Tile, 24, 0, 0, 24, 24, 6, 0, Hand(5, 5, 14, (0, 2, false), (3, 1, false), (5, 3, true), (9, 2, false), (12, 2, false)), 1),
        _ => Scaled(Shape.Tile, px),
    };

    /// <summary>The layout of the .rotula document icon at the given pixel size.</summary>
    public static Layout ForPage(int px) => px switch
    {
        16 => new(Shape.Page, 16, 2, 0, 12, 16, 1.5f, 4, Hand(4, 8, 5, (0, 1, false), (2, 1, false), (4, 2, true), (7, 1, false)), 1),
        20 => new(Shape.Page, 20, 3, 1, 14, 18, 2, 5, Hand(5, 10, 5, (0, 2, false), (3, 1, false), (5, 2, true), (8, 2, false)), 1),
        24 => new(Shape.Page, 24, 3, 1, 18, 22, 2, 6, Hand(6, 12, 7, (0, 2, false), (3, 1, false), (5, 2, true), (8, 1, false), (10, 2, false)), 1),
        _ => Scaled(Shape.Page, px),
    };

    /// <summary>Builds hand-placed bars from (offset from <paramref name="x0"/>, width, accent) tuples.</summary>
    private static List<Bar> Hand(float x0, float y, float h, params (int Off, int W, bool Accent)[] bars) =>
        bars.Select(b => new Bar(x0 + b.Off, y, b.W, h, b.Accent)).ToList();

    /// <summary>Derives a layout from the 256-unit master, snapping to whole pixels up to 64 px so small sizes stay crisp.</summary>
    private static Layout Scaled(Shape kind, int px)
    {
        float s = px / (float)Master;
        bool snap = px <= 64;
        // Half-way values round away from zero (not to even) so equal master gaps stay equal after snapping.
        float Sc(float v) => snap ? MathF.Round(v * s, MidpointRounding.AwayFromZero) : v * s;
        float Edge(float from, float to, out float length)
        {
            float a = Sc(from), b = Sc(to);
            if (b - a < 1) b = a + 1;
            length = b - a;
            return a;
        }

        bool tile = kind == Shape.Tile;
        // Master geometry: tile 8..248 square, radius 56, peel 56. Page 40..216 x 8..248, radius 20, fold 56.
        float mx = tile ? 8 : 40, my = 8, mr = tile ? 56 : 20;
        // The body is centred in its canvas, so snap one margin and mirror it: both sides get the same margin.
        float x = Sc(mx), y = Sc(my), w = px - 2 * x, h = px - 2 * y;
        float radius = snap ? MathF.Round(mr * s, MidpointRounding.AwayFromZero) : mr * s, fold = snap ? MathF.Round(56 * s, MidpointRounding.AwayFromZero) : 56 * s;

        int[] pattern = tile ? TileBarModules : PageBarModules;
        const int module = 8;
        int modules = pattern.Sum() + pattern.Length - 1;
        float cursor = Master / 2f - modules * module / 2f;
        float barTop = tile ? 80 : 112, barBottom = tile ? 192 : 208;
        var bars = new List<Bar>();
        float previousEnd = float.NegativeInfinity;
        for (int i = 0; i < pattern.Length; i++)
        {
            float bx = Edge(cursor, cursor + pattern[i] * module, out float bw);
            // Snapping must never close a gap: keep at least one pixel of background between neighbouring bars.
            if (snap && bx < previousEnd + 1)
            {
                bw -= previousEnd + 1 - bx;
                bx = previousEnd + 1;
            }
            previousEnd = bx + bw;
            float by = Edge(barTop, barBottom, out float bh);
            bars.Add(new Bar(bx, by, bw, bh, i == AccentIndex));
            cursor += (pattern[i] + 1) * module;
        }

        // Rim/outline: 1 px at small sizes, 5 master units at 256.
        return new Layout(kind, px, x, y, w, h, radius, fold, bars, MathF.Max(1, 5 * s));
    }
}
