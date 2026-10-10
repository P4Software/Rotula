using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Core.Imaging;

/// <summary>
/// Turns an <see cref="ImageElement"/> into the exact monochrome dots that will print. The renderer
/// should use the same method for previews so that what is shown is what prints.
/// </summary>
public static class ImageProcessor
{
    // Classic 4x4 Bayer matrix; values 0..15.
    private static readonly int[,] Bayer4 =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    /// <summary>Resolves the final (unrotated) pixel size of an image element, preserving aspect ratio when one side is 0.</summary>
    public static (int Width, int Height) ResolveSize(ImageElement element)
    {
        var src = element.Source ?? throw new InvalidOperationException("Image element has no source.");
        int w = element.WidthDots, h = element.HeightDots;
        if (w <= 0 && h <= 0) return (src.Width, src.Height);
        if (w <= 0) w = Math.Max(1, (int)Math.Round((double)src.Width * h / src.Height, MidpointRounding.AwayFromZero));
        if (h <= 0) h = Math.Max(1, (int)Math.Round((double)src.Height * w / src.Width, MidpointRounding.AwayFromZero));
        return (w, h);
    }

    /// <summary>Scales, dithers and rotates the source into printable dots.</summary>
    public static MonochromeBitmap ToMonochrome(ImageElement element)
    {
        var src = element.Source ?? throw new InvalidOperationException("Image element has no source.");
        var (w, h) = ResolveSize(element);
        float[] lum = Scale(src, w, h, element.Invert);
        bool[] black = Reduce(lum, w, h, element.Dither, element.Threshold);
        var (rw, rh, rotated) = Rotate(black, w, h, element.Rotation ?? FieldRotation.Normal);
        return Pack(rotated, rw, rh);
    }

    /// <summary>
    /// Scales to the target size: box averaging when shrinking (avoids aliasing, which would make
    /// small photos noisy) and nearest neighbour when enlarging (keeps logos crisp).
    /// </summary>
    private static float[] Scale(GrayscaleBitmap src, int w, int h, bool invert)
    {
        var result = new float[w * h];
        for (int ty = 0; ty < h; ty++)
        {
            int y0 = (int)((long)ty * src.Height / h);
            int y1 = Math.Max(y0 + 1, (int)(((long)(ty + 1) * src.Height + h - 1) / h));
            y1 = Math.Min(y1, src.Height);
            for (int tx = 0; tx < w; tx++)
            {
                int x0 = (int)((long)tx * src.Width / w);
                int x1 = Math.Max(x0 + 1, (int)(((long)(tx + 1) * src.Width + w - 1) / w));
                x1 = Math.Min(x1, src.Width);
                long sum = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        sum += src.GetPixel(x, y);
                float v = (float)sum / ((y1 - y0) * (x1 - x0));
                result[ty * w + tx] = invert ? 255f - v : v;
            }
        }
        return result;
    }

    private static bool[] Reduce(float[] lum, int w, int h, ImageDitherMode mode, int threshold)
    {
        var black = new bool[w * h];
        switch (mode)
        {
            case ImageDitherMode.Threshold:
                for (int i = 0; i < black.Length; i++) black[i] = lum[i] < threshold;
                break;
            case ImageDitherMode.Ordered:
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        black[y * w + x] = lum[y * w + x] < (Bayer4[y & 3, x & 3] + 0.5f) * (255f / 16f);
                break;
            case ImageDitherMode.FloydSteinberg:
                // Work on a copy: error diffusion mutates neighbours.
                var buf = (float[])lum.Clone();
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float old = buf[y * w + x];
                        float neu = old < threshold ? 0f : 255f;
                        black[y * w + x] = neu == 0f;
                        float err = old - neu;
                        if (x + 1 < w) buf[y * w + x + 1] += err * 7f / 16f;
                        if (y + 1 < h)
                        {
                            if (x > 0) buf[(y + 1) * w + x - 1] += err * 3f / 16f;
                            buf[(y + 1) * w + x] += err * 5f / 16f;
                            if (x + 1 < w) buf[(y + 1) * w + x + 1] += err * 1f / 16f;
                        }
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return black;
    }

    /// <summary>Rotates clockwise by 0/90/180/270 degrees. ^GF ignores the field orientation, so rotation is baked into the dots.</summary>
    private static (int W, int H, bool[] Data) Rotate(bool[] src, int w, int h, FieldRotation rotation)
    {
        switch (rotation)
        {
            case FieldRotation.Normal:
                return (w, h, src);
            case FieldRotation.Inverted180:
                var r180 = new bool[src.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        r180[(h - 1 - y) * w + (w - 1 - x)] = src[y * w + x];
                return (w, h, r180);
            case FieldRotation.Rotated90:
                var r90 = new bool[src.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        r90[x * h + (h - 1 - y)] = src[y * w + x];
                return (h, w, r90);
            case FieldRotation.Rotated270:
                var r270 = new bool[src.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        r270[(w - 1 - x) * h + y] = src[y * w + x];
                return (h, w, r270);
            default:
                throw new ArgumentOutOfRangeException(nameof(rotation));
        }
    }

    private static MonochromeBitmap Pack(bool[] black, int w, int h)
    {
        int bpr = (w + 7) / 8;
        var packed = new byte[bpr * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (black[y * w + x]) packed[y * bpr + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return new MonochromeBitmap(w, h, packed);
    }
}
