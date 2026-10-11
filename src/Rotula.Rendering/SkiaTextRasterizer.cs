using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering;

/// <summary>
/// Draws the text of a Windows font to a one-bit picture with SkiaSharp, at the printer's resolution. It lays the text out with the same
/// code the label preview uses (<c>TextBlockLayout</c>), so the preview and the printed picture agree on line breaks, alignment and underline.
/// </summary>
/// <remarks>
/// Thread safe: every call builds its own font and bitmap; the only shared state is the read-only typeface cache. Letters are drawn anti-aliased
/// and then cut at half coverage, which is what a one-bit printer shows.
/// </remarks>
public sealed class SkiaTextRasterizer : ITextRasterizer
{
    // Same bound the label renderer uses: about 480 MB at four bytes a pixel is already far beyond any label.
    private const long MaxPixels = 60_000_000;

    /// <inheritdoc />
    public TextMeasurement Measure(TextRasterRequest request)
    {
        using var layout = Lay(request, out var face);
        return Describe(layout, face);
    }

    /// <inheritdoc />
    public TextRasterResult Rasterize(TextRasterRequest request)
    {
        using var layout = Lay(request, out var face);
        var measurement = Describe(layout, face);
        int w = Math.Max(1, measurement.WidthDots), h = Math.Max(1, measurement.HeightDots);
        if ((long)w * h > MaxPixels) throw new ArgumentException("The text is too large to draw.", nameof(request));

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            layout.Paint(canvas, paint);
        }

        // Pack the dots: a pixel at least half dark is a printed dot. Bits run from the most significant, as ^GF expects.
        int bytesPerRow = (w + 7) / 8;
        var packed = new byte[bytesPerRow * h];
        var span = bitmap.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            int row = y * bitmap.RowBytes;
            for (int x = 0; x < w; x++)
            {
                // The picture is black on white, so the blue channel alone says how dark the pixel is.
                if (span[row + x * 4] < 128) packed[y * bytesPerRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return new TextRasterResult(new MonochromeBitmap(w, h, packed), measurement);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetFontFamilies()
    {
        // The font manager can list the same family twice (several faces); the box wants each name once.
        return SKFontManager.Default.GetFontFamilies().Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static TextBlockLayout Lay(TextRasterRequest r, out ResolvedTypeface face)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Text.Length == 0) throw new ArgumentException("There is no text to draw.", nameof(r));
        if (r.Font.Validate() is not null) throw new ArgumentException("The font is not valid.", nameof(r));
        face = TypefaceSource.Resolve(r.Font.FamilyName, r.Font.Bold, r.Font.Italic);
        float em = (float)FontChoice.PointsToDots(r.Font.PointSize, r.Dpi);
        var align = r.Alignment switch
        {
            TextAlignment.Center => RenderTextAlignment.Center,
            TextAlignment.Right => RenderTextAlignment.Right,
            TextAlignment.Justified => RenderTextAlignment.Justified,
            _ => RenderTextAlignment.Left,
        };
        return new TextBlockLayout(r.Text, face.Typeface, em, face.SkewX, r.Font.Underline, r.BoxWidthDots, r.BoxHeightDots, r.MaxLines,
            r.LineSpacingDots, align, r.HangingIndentDots);
    }

    private static TextMeasurement Describe(TextBlockLayout layout, ResolvedTypeface face) =>
        new((int)Math.Ceiling(layout.Width), (int)Math.Ceiling(layout.Height), layout.Lines.Count, (int)Math.Round(layout.Ascent), layout.Overflow,
            face.Substituted, face.ActualFamily);
}
