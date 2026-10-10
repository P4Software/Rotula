using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Model;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using SkiaSharp;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>PNG, PDF and raw ZPL export.</summary>
public class RenderingExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-render-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static RenderLabel Sample() => Label(406, 203,
        new RenderBarcode { Symbology = BarcodeSymbology.QrCode, Data = "EXPORT-QR", ModuleDots = 4, X = 30, Y = 30 },
        new RenderText { Text = "Export", FontHeightDots = 30, X = 200, Y = 40 });

    [Fact]
    public void Png_has_printer_resolution_and_still_scans()
    {
        var path = Path.Combine(_dir, "sub", "label.png");
        var issues = LabelExporter.SavePng(Sample(), path);
        Assert.Empty(issues);

        using var bmp = SKBitmap.Decode(path);
        Assert.Equal(406, bmp.Width);
        Assert.Equal(203, bmp.Height);
        Assert.Equal("EXPORT-QR", Decode(bmp, ZXing.BarcodeFormat.QR_CODE)?.Text);
    }

    [Fact]
    public void Pdf_is_a_one_page_document_sized_like_the_label()
    {
        var path = Path.Combine(_dir, "label.pdf");
        LabelExporter.SavePdf(Sample(), path);

        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.StartsWith("%PDF-", text);
        // 406 dots at 203 DPI = 2 inch = 144 pt; 203 dots = 1 inch = 72 pt.
        Assert.Matches(@"/MediaBox \[\s*0 0 144 72\s*\]", text);
        Assert.Single(Regex.Matches(text, @"/Type /Page\b"));
    }

    [Fact]
    public void Zpl_is_written_verbatim_as_utf8_without_bom()
    {
        var path = Path.Combine(_dir, "label.zpl");
        const string zpl = "^XA\n^FO10,10^FDcafé^FS\n^XZ\n";
        LabelExporter.SaveZpl(zpl, path);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]); // no byte-order mark
        Assert.Equal(zpl, Encoding.UTF8.GetString(bytes));
    }
}
