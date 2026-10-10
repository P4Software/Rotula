using System.Text;
using Rotula.Rendering.Input;
using SkiaSharp;

namespace Rotula.Rendering.Export;

/// <summary>Exports a rendered label to PNG, PDF or a raw ZPL file.</summary>
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
