using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Rotula.Tests;

/// <summary>Shared helpers for the rendering tests: label building and decoding with ZXing.Net.</summary>
internal static class RenderingTestSupport
{
    /// <summary>A white label with the given elements at 203 DPI.</summary>
    public static RenderLabel Label(int w, int h, params RenderElement[] elements) => new(w, h, 203, elements);

    /// <summary>
    /// Decodes the first barcode found in the bitmap, or null when none can be read.
    /// The picture is first cropped to the symbol plus a quiet zone: ZXing's Data Matrix detector only looks
    /// near the image centre, so a small symbol in a corner of a big label would otherwise be missed
    /// (a ZXing limitation, not a rendering problem).
    /// </summary>
    public static Result? Decode(SKBitmap bitmap, BarcodeFormat format)
    {
        const int quietZone = 30;
        var bounds = DarkBounds(bitmap) ?? new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        var crop = new SKRectI(
            Math.Max(0, bounds.Left - quietZone), Math.Max(0, bounds.Top - quietZone),
            Math.Min(bitmap.Width, bounds.Right + quietZone), Math.Min(bitmap.Height, bounds.Bottom + quietZone));
        // Copy the crop into a plain luminance array (a subset bitmap shares its parent's row stride).
        var lum = new byte[crop.Width * crop.Height];
        for (int y = 0; y < crop.Height; y++)
            for (int x = 0; x < crop.Width; x++)
                lum[y * crop.Width + x] = bitmap.GetPixel(crop.Left + x, crop.Top + y).Red;
        var source = new RGBLuminanceSource(lum, crop.Width, crop.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [format],
            },
        };
        return reader.Decode(source);
    }

    /// <summary>Renders at printer resolution (scale 1) and decodes.</summary>
    public static Result? RenderAndDecode(RenderLabel label, BarcodeFormat format, double scale = 1.0)
    {
        using var result = LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = scale });
        return Decode(result.Bitmap, format);
    }

    /// <summary>True when the pixel is clearly dark.</summary>
    public static bool IsDark(SKBitmap bmp, int x, int y) => bmp.GetPixel(x, y).Red < 128;

    /// <summary>Bounding box of all dark pixels, or null for an empty picture.</summary>
    public static SKRectI? DarkBounds(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsDark(bmp, x, y))
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
