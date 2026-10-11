using SkiaSharp;

namespace Rotula.Brand;

/// <summary>
/// The "Rotula" wordmark, drawn here as a monoline geometric alphabet (straight stems, circles, no font involved), so it is
/// original artwork with no licence attached and renders identically in SVG and Skia. Vertical stems echo the barcode bars and
/// the "l" is the single orange bar. Coordinates: baseline y = 150, ascender y = 0, x-height top y = 50, stroke 20.
/// </summary>
internal static class Wordmark
{
    /// <summary>Stroke thickness in wordmark units.</summary>
    public const float Stroke = 20;

    /// <summary>Total width of the wordmark in its own units.</summary>
    public const float Width = 542;

    /// <summary>Total height of the wordmark in its own units.</summary>
    public const float Height = 150;

    /// <summary>One piece of the lettering.</summary>
    /// <param name="Data">SVG path data.</param>
    /// <param name="Filled">True for a filled polygon, false for a stroked centre line.</param>
    /// <param name="Accent">True for the orange letter.</param>
    internal sealed record Piece(string Data, bool Filled, bool Accent);

    /// <summary>All pieces in reading order.</summary>
    public static readonly IReadOnlyList<Piece> Pieces =
    [
        // R: stem, bowl, diagonal leg.
        new("M10 0V150", false, false),
        new("M10 10H46A34 34 0 0 1 46 78H10", false, false),
        new("M38 78H62L98 150H74Z", true, false),
        // o
        new("M122 100a40 40 0 1 0 80 0a40 40 0 1 0 -80 0", false, false),
        // t: stem with a hook, crossbar at x-height
        new("M248 24V110A30 30 0 0 0 278 140H292", false, false),
        new("M224 60H288", false, false),
        // u: left stem curving into the right stem
        new("M316 50V108A32 32 0 0 0 380 108", false, false),
        new("M380 50V150", false, false),
        // l: the orange bar
        new("M416 0V150", false, true),
        // a: single-storey, circle plus stem
        new("M452 100a40 40 0 1 0 80 0a40 40 0 1 0 -80 0", false, false),
        new("M532 50V150", false, false),
    ];

    /// <summary>Draws the wordmark with its top-left corner at (x, y) and the given scale.</summary>
    public static void Draw(SKCanvas canvas, float x, float y, float scale, SKColor ink, SKColor accent)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(scale);
        foreach (var piece in Pieces)
        {
            using var path = SKPath.ParseSvgPathData(piece.Data);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = piece.Accent ? accent : ink,
                Style = piece.Filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                StrokeWidth = Stroke,
                StrokeCap = SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Miter,
            };
            canvas.DrawPath(path, paint);
        }
        canvas.Restore();
    }
}
