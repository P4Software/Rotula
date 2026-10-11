using SkiaSharp;

namespace Rotula.Brand;

/// <summary>Larger compositions built from the mark and the wordmark: installer wizard images and the GitHub social preview.</summary>
internal static class Scenes
{
    /// <summary>Preferred UI faces for the tagline (raster only; the shipped logo files contain no font at all).</summary>
    private static readonly string[] TaglineFaces = ["Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", "Arial"];

    /// <summary>The tall image on the left of the Inno Setup wizard (164 x 314 at 100 %).</summary>
    public static SKBitmap WizardLarge(int width, int height)
    {
        float s = width / 164f;
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height), [Palette.C("#24262E"), Palette.C("#121317")], SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, width, height, bg);

        GhostBars(canvas, 0, height * 0.76f, width, height * 0.24f, new SKColor(255, 255, 255, 20), Palette.C(Palette.AccentOnDark).WithAlpha(120), s * 3, 11);

        float tile = 112 * s, x = (width - tile) / 2, y = 34 * s;
        MarkPainter.Paint(canvas, Layout.ForTile(Layout.Master), x - tile * 8 / 256, y - tile * 8 / 256, tile / 256);

        float scale = 112 * s / Wordmark.Width;
        Wordmark.Draw(canvas, (width - Wordmark.Width * scale) / 2, y + tile + 24 * s, scale, Palette.C(Palette.BarLight), Palette.C(Palette.AccentOnDark));
        return bmp;
    }

    /// <summary>The small square image at the top right of the Inno Setup wizard header (55 x 55 at 100 %).</summary>
    public static SKBitmap WizardSmall(int width, int height)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        float size = MathF.Min(width, height) * 0.92f;
        // The tile has an 8/256 margin inside its canvas; shift so the visible tile (not the canvas) is centred.
        float scale = size / 240f;
        MarkPainter.Paint(canvas, Layout.ForTile(Layout.Master), (width - size) / 2 - 8 * scale, (height - size) / 2 - 8 * scale, scale);
        return bmp;
    }

    /// <summary>The 1280 x 640 GitHub social preview.</summary>
    public static SKBitmap Social()
    {
        const int w = 1280, h = 640;
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), [Palette.C("#F7F5F1"), Palette.C("#EAE6DF")], SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, w, h, bg);

        GhostBars(canvas, 0, 520, w, 120, Palette.C(Palette.Charcoal).WithAlpha(22), Palette.C(Palette.Accent).WithAlpha(70), 9, 29);

        float tile = 240, tx = 120, ty = 150;
        MarkPainter.Paint(canvas, Layout.ForTile(Layout.Master), tx - tile * 8 / 256, ty - tile * 8 / 256, tile / 256);

        float wmScale = 1.0f, wx = 430, wy = 165;
        Wordmark.Draw(canvas, wx, wy, wmScale, Palette.C(Palette.Charcoal), Palette.C(Palette.Accent));

        using var accent = new SKPaint { IsAntialias = true, Color = Palette.C(Palette.Accent) };
        canvas.DrawRect(wx, wy + 150 * wmScale + 34, 64, 6, accent);

        using var face = FindFace();
        using var font = new SKFont(face, 36) { Subpixel = true };
        using var ink = new SKPaint { IsAntialias = true, Color = Palette.C(Palette.Muted) };
        canvas.DrawText("Free barcode & label designer for Windows (ZPL)", wx, wy + 150 * wmScale + 100, SKTextAlign.Left, font, ink);
        return bmp;
    }

    /// <summary>Faint barcode-like bars along an edge: a decoration that repeats the motif without competing with the mark.</summary>
    private static void GhostBars(SKCanvas canvas, float x, float y, float width, float height, SKColor ink, SKColor accent, float module, int seed)
    {
        using var paint = new SKPaint { IsAntialias = false };
        // A fixed linear-congruential sequence keeps the picture identical on every run.
        uint state = (uint)seed * 2654435761u;
        int Next(int max) { state = state * 1664525u + 1013904223u; return (int)((state >> 16) % (uint)max); }
        float cursor = x;
        int accentAt = 7;
        int index = 0;
        while (cursor < x + width)
        {
            float bar = (1 + Next(3)) * module, gap = (1 + Next(2)) * module;
            if (cursor + bar > x + width) bar = x + width - cursor;
            paint.Color = index == accentAt ? accent : ink;
            canvas.DrawRect(cursor, y, bar, height, paint);
            cursor += bar + gap;
            index++;
        }
    }

    private static SKTypeface FindFace()
    {
        foreach (string name in TaglineFaces)
        {
            var face = SKTypeface.FromFamilyName(name, SKFontStyle.Normal);
            if (face is not null && string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase)) return face;
            face?.Dispose();
        }
        return SKTypeface.Default;
    }
}
