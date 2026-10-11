using System.Text;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering.Export;

/// <summary>Exports a rendered label to PNG, JPEG, PDF or a raw ZPL file.</summary>
public static class LabelExporter
{
    /// <summary>PDF user space is 72 units per inch.</summary>
    private const double PdfPointsPerInch = 72.0;

    /// <summary>Writes the label as a PNG at printer resolution (one pixel per dot) and returns any render issues.</summary>
    public static IReadOnlyList<RenderIssue> SavePng(RenderLabel label, Stream output)
    {
        using var result = LabelRenderer.RenderBitmap(label);
        return WritePng(result, output);
    }

    /// <summary>Writes the label as a PNG file, creating the folder when needed.</summary>
    public static IReadOnlyList<RenderIssue> SavePng(RenderLabel label, string path)
    {
        EnsureFolder(path);
        using var file = File.Create(path);
        return SavePng(label, file);
    }

    /// <summary>The JPEG quality used when none is given: high enough that bar edges stay clean, and the file stays small.</summary>
    public const int DefaultJpegQuality = 95;

    /// <summary>
    /// Writes the label as a JPEG at printer resolution (one pixel per dot) and returns any render issues. JPEG has no transparency and
    /// blurs sharp edges a little, so use PNG when the image will be scanned or printed; JPEG is for sharing and previews.
    /// </summary>
    /// <param name="label">The label to draw (its background is opaque white).</param>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="quality">1 (smallest file) to 100 (best picture).</param>
    public static IReadOnlyList<RenderIssue> SaveJpeg(RenderLabel label, Stream output, int quality = DefaultJpegQuality)
    {
        using var result = LabelRenderer.RenderBitmap(label);
        using var image = SKImage.FromBitmap(result.Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100))
            ?? throw new InvalidOperationException("The label image could not be encoded as JPEG.");
        data.SaveTo(output);
        return result.Issues;
    }

    /// <summary>Writes the label as a JPEG file, creating the folder when needed.</summary>
    public static IReadOnlyList<RenderIssue> SaveJpeg(RenderLabel label, string path, int quality = DefaultJpegQuality)
    {
        EnsureFolder(path);
        using var file = File.Create(path);
        return SaveJpeg(label, file, quality);
    }

    /// <summary>
    /// Writes the label as a one-page PDF whose page is exactly the label size. Text, boxes, lines and
    /// barcode modules are written as vectors, so the PDF stays sharp at any zoom. Text is the approximate
    /// stand-in font, as in the on-screen preview.
    /// </summary>
    public static IReadOnlyList<RenderIssue> SavePdf(RenderLabel label, Stream output)
    {
        if (label.WidthDots <= 0 || label.HeightDots <= 0)
            throw new ArgumentException("The label size must be greater than zero.", nameof(label));

        // Dots -> PDF points: one dot is 1/dpi inch, one point is 1/72 inch.
        double toPoints = PdfPointsPerInch / label.Dpi;
        using var stream = new SKManagedWStream(output, false);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { RasterDpi = label.Dpi });
        using var canvas = document.BeginPage((float)(label.WidthDots * toPoints), (float)(label.HeightDots * toPoints));
        canvas.Clear(SKColors.White);
        canvas.Scale((float)toPoints);
        var issues = LabelRenderer.Draw(canvas, label);
        document.EndPage();
        document.Close();
        return issues;
    }

    /// <summary>Writes the label as a PDF file, creating the folder when needed.</summary>
    public static IReadOnlyList<RenderIssue> SavePdf(RenderLabel label, string path)
    {
        EnsureFolder(path);
        using var file = File.Create(path);
        return SavePdf(label, file);
    }

    /// <summary>
    /// Saves ZPL text exactly as given to a .zpl file (UTF-8 without byte-order mark, which is what
    /// <c>^CI28</c> printers expect). The ZPL itself is produced by the ZPL generator, not by the renderer.
    /// </summary>
    public static void SaveZpl(string zpl, string path)
    {
        ArgumentNullException.ThrowIfNull(zpl);
        EnsureFolder(path);
        File.WriteAllText(path, zpl, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static IReadOnlyList<RenderIssue> WritePng(RenderResult result, Stream output)
    {
        using var image = SKImage.FromBitmap(result.Bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The label image could not be encoded as PNG.");
        data.SaveTo(output);
        return result.Issues;
    }

    private static void EnsureFolder(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
    }
}
