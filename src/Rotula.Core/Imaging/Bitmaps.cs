using Rotula.Core.Model;

namespace Rotula.Core.Imaging;

/// <summary>
/// Immutable 8-bit luminance image (0 = black, 255 = white), row-major. Core has no imaging
/// dependency, so the application decodes real image files and hands the pixels over in this form.
/// </summary>
public sealed class GrayscaleBitmap
{
    private readonly byte[] _pixels;

    /// <summary>Creates a bitmap; the pixel array is copied.</summary>
    /// <param name="width">Width in pixels (at least 1).</param>
    /// <param name="height">Height in pixels (at least 1).</param>
    /// <param name="pixels">Exactly <c>width * height</c> luminance bytes.</param>
    public GrayscaleBitmap(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "Bitmap must be at least 1x1.");
        if (pixels.Length != checked(width * height)) throw new ArgumentException("Pixel count does not match width*height.", nameof(pixels));
        Width = width;
        Height = height;
        _pixels = (byte[])pixels.Clone();
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Luminance of the pixel at (x, y).</summary>
    public byte GetPixel(int x, int y) => _pixels[y * Width + x];
}

/// <summary>
/// Packed 1-bit image where a set bit is a black (printed) dot, exactly the layout ^GF expects:
/// rows padded to whole bytes, most significant bit first.
/// </summary>
public sealed class MonochromeBitmap
{
    /// <summary>Creates a bitmap from already packed rows.</summary>
    public MonochromeBitmap(int width, int height, byte[] packedRows)
    {
        Width = width;
        Height = height;
        BytesPerRow = (width + 7) / 8;
        if (packedRows.Length != BytesPerRow * height) throw new ArgumentException("Packed data length mismatch.", nameof(packedRows));
        PackedRows = packedRows;
    }

    /// <summary>Width in dots.</summary>
    public int Width { get; }
    /// <summary>Height in dots.</summary>
    public int Height { get; }
    /// <summary>Bytes per packed row.</summary>
    public int BytesPerRow { get; }
    /// <summary>Packed rows, 1 = black.</summary>
    public byte[] PackedRows { get; }

    /// <summary>True when the dot at (x, y) is black.</summary>
    public bool IsBlack(int x, int y) => (PackedRows[y * BytesPerRow + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    /// <summary>
    /// A copy turned clockwise by the field rotation. A <c>^GF</c> graphic ignores <c>^FW</c> and the rotation of <c>^FO</c>, so a rotated
    /// field must already be turned in its dots.
    /// </summary>
    public MonochromeBitmap Rotate(FieldRotation rotation)
    {
        if (rotation == FieldRotation.Normal) return this;
        var black = new bool[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                black[y * Width + x] = IsBlack(x, y);
        var (rw, rh, turned) = ImageProcessor.Rotate(black, Width, Height, rotation);
        return ImageProcessor.Pack(turned, rw, rh);
    }

    /// <summary>Upper-case hexadecimal rendering of <see cref="PackedRows"/> (the ^GF ASCII data).</summary>
    public string ToHex() => Convert.ToHexString(PackedRows);
}
