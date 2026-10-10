using System.IO.Compression;
using System.Text;
using ModelContextProtocol;
using Rotula.Core.Imaging;
using SkiaSharp;

namespace Rotula.Mcp.Security;

/// <summary>
/// Reads and writes the few kinds of file the server handles, after <see cref="PathGuard"/> approved the path. File content is
/// untrusted: sizes are checked before anything is decoded and nothing in a file is ever executed.
/// </summary>
public static class SafeFiles
{
    /// <summary>Widest or tallest image accepted, in pixels.</summary>
    public const int MaxImageSide = 4096;

    /// <summary>Most pixels an image may hold.</summary>
    public const int MaxImagePixels = 8_000_000;

    /// <summary>Most bytes a workbook may expand to when unzipped (a zip bomb guard).</summary>
    public const long MaxWorkbookExpandedBytes = 100L * 1024 * 1024;

    /// <summary>Writes a file by writing a temporary file next to it and renaming, so a failure never destroys the old version.</summary>
    /// <param name="path">A path already approved by <see cref="PathGuard"/>.</param>
    /// <param name="data">The content.</param>
    /// <param name="maxBytes">Largest size the settings allow.</param>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> data, int maxBytes)
    {
        if (data.Length > maxBytes)
            throw new McpException($"The result is {data.Length:N0} bytes; the limit is {maxBytes:N0} bytes (maxFileBytes in rotula-mcp.settings.json). Nothing was written.");
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"The file could not be written ({ex.GetType().Name}). Check that the folder is writable and the file is not open elsewhere.");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes UTF-8 text (no byte-order mark) atomically.</summary>
    public static void WriteTextAtomic(string path, string text, int maxBytes) =>
        WriteAtomic(path, new UTF8Encoding(false).GetBytes(text), maxBytes);

    /// <summary>Reads an approved file completely.</summary>
    public static byte[] ReadBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"The file could not be read ({ex.GetType().Name}). Check that it is not open exclusively elsewhere.");
        }
    }

    /// <summary>Reads UTF-8 text from an approved file.</summary>
    public static string ReadText(string path) => new UTF8Encoding(false, false).GetString(ReadBytes(path));

    /// <summary>
    /// Decodes a PNG into a luminance bitmap. Transparent areas become white (paper is white). The size is read from the header
    /// first so an enormous image is refused before any pixel memory is allocated.
    /// </summary>
    public static GrayscaleBitmap LoadPng(string path)
    {
        byte[] bytes = ReadBytes(path);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new McpException("That file is not a PNG image. Only PNG images are supported.");
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || info.Width > MaxImageSide || info.Height > MaxImageSide || (long)info.Width * info.Height > MaxImagePixels)
            throw new McpException($"That image is {info.Width} x {info.Height} pixels; the limit is {MaxImageSide} pixels on a side and {MaxImagePixels:N0} pixels in total. Shrink it first.");

        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new McpException("That PNG could not be decoded; the file may be damaged.");

        var span = bitmap.GetPixelSpan();
        var gray = new byte[info.Width * info.Height];
        for (int i = 0; i < gray.Length; i++)
        {
            double a = span[i * 4 + 3] / 255.0;
            double r = span[i * 4] * a + 255 * (1 - a), g = span[i * 4 + 1] * a + 255 * (1 - a), b = span[i * 4 + 2] * a + 255 * (1 - a);
            gray[i] = (byte)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);
        }
        return new GrayscaleBitmap(info.Width, info.Height, gray);
    }

    /// <summary>
    /// Refuses a workbook whose parts would unzip to more than <see cref="MaxWorkbookExpandedBytes"/>. A tiny .xlsx can
    /// otherwise expand to gigabytes (a zip bomb) when the reader walks its sheets.
    /// </summary>
    public static void CheckWorkbook(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (total > MaxWorkbookExpandedBytes)
                    throw new McpException("That workbook expands to an unreasonable size and was refused.");
            }
        }
        catch (InvalidDataException)
        {
            throw new McpException("That file is not a valid .xlsx workbook.");
        }
    }
}
