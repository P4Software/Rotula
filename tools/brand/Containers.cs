using SkiaSharp;

namespace Rotula.Brand;

/// <summary>Hand-written file containers: ICO with PNG-compressed entries, and 24-bit BMP for the Inno Setup wizard.</summary>
internal static class Containers
{
    /// <summary>The sizes every icon carries.</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>Encodes a bitmap as PNG with fixed settings so reruns produce identical bytes.</summary>
    public static byte[] Png(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Packs PNG images into an ICO file: a 6-byte header, one 16-byte directory entry per image, then the PNG data.
    /// Width and height bytes are 0 for 256, as the format requires.
    /// </summary>
    public static byte[] Ico(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette colours
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // colour planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        return ms.ToArray();
    }

    /// <summary>Writes an opaque bitmap as a bottom-up 24-bit BMP (rows padded to 4 bytes), which every Inno Setup version accepts.</summary>
    public static byte[] Bmp(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, stride = (w * 3 + 3) & ~3, pixelBytes = stride * h;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(54 + pixelBytes); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)24);
        bw.Write(0); bw.Write(pixelBytes); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        var row = new byte[stride];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                row[x * 3] = c.Blue; row[x * 3 + 1] = c.Green; row[x * 3 + 2] = c.Red;
            }
            bw.Write(row);
        }
        return ms.ToArray();
    }
}
