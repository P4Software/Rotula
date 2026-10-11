using SkiaSharp;

namespace Rotula.Brand;

/// <summary>Draws a <see cref="Layout"/> with Skia (the raster side of the single source of truth).</summary>
internal static class MarkPainter
{
    /// <summary>Renders a layout to a transparent square bitmap of its own size.</summary>
    public static SKBitmap Render(Layout l)
    {
        var bmp = new SKBitmap(l.Size, l.Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        Paint(canvas, l, 0, 0, 1);
        return bmp;
    }

    /// <summary>Paints a layout at an offset and scale (used to place the mark inside larger scenes).</summary>
    public static void Paint(SKCanvas canvas, Layout l, float ox, float oy, float scale)
    {
        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale(scale);
        if (l.Kind == Shape.Tile) PaintTile(canvas, l); else PaintPage(canvas, l);
        canvas.Restore();
    }

    private static void PaintTile(SKCanvas canvas, Layout l)
    {
        using var body = SKPath.ParseSvgPathData(l.BodyPath());
        using (var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, l.Y), new SKPoint(0, l.Y + l.H), [Palette.C(Palette.TileTop), Palette.C(Palette.TileBottom)], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawPath(body, fill);
        }

        // The rim is a centred stroke clipped to the body, so only its inner half shows; it keeps the dark tile visible on a dark taskbar.
        canvas.Save();
        canvas.ClipPath(body, SKClipOperation.Intersect, true);
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = l.Edge * 2, Color = new SKColor(255, 255, 255, 46) })
            canvas.DrawPath(body, rim);
        canvas.Restore();

        if (l.Fold > 0)
        {
            using var flap = SKPath.ParseSvgPathData(l.FlapPath());
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(l.X + l.W - l.Fold, l.Y + l.Fold), new SKPoint(l.X + l.W, l.Y), [Palette.C(Palette.FlapBottom), Palette.C(Palette.FlapTop)], SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(flap, paint);
        }

        DrawBars(canvas, l, Palette.BarLight, Palette.AccentOnDark);
    }

    private static void PaintPage(SKCanvas canvas, Layout l)
    {
        using var body = SKPath.ParseSvgPathData(l.BodyPath());
        using (var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, l.Y), new SKPoint(0, l.Y + l.H), [Palette.C(Palette.PageTop), Palette.C(Palette.PageBottom)], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawPath(body, fill);
        }

        using var flap = SKPath.ParseSvgPathData(l.FlapPath());
        using (var flapFill = new SKPaint { IsAntialias = true, Color = Palette.C(Palette.PageFlap) })
            canvas.DrawPath(flap, flapFill);

        canvas.Save();
        canvas.ClipPath(body, SKClipOperation.Intersect, true);
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = l.Edge * 2, Color = Palette.C(Palette.PageEdge), StrokeJoin = SKStrokeJoin.Miter })
        {
            canvas.DrawPath(body, edge);
            canvas.DrawPath(flap, edge);
        }
        canvas.Restore();

        DrawBars(canvas, l, Palette.Charcoal, Palette.Accent);
    }

    private static void DrawBars(SKCanvas canvas, Layout l, string ink, string accent)
    {
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var bar in l.Bars)
        {
            paint.Color = Palette.C(bar.Accent ? accent : ink);
            canvas.DrawRect(bar.X, bar.Y, bar.W, bar.H, paint);
        }
    }
}
