using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Zpl.Import;

namespace Rotula.Tests;

/// <summary>The importer must stay inside a memory budget however many graphic fields a hostile file declares.</summary>
public class ZplImportBudgetTests
{
    // A tiny budget keeps the test cheap: the rule is the same one that protects the real 128 MiB default.
    private static ZplParseOptions Small(long bytes) => new() { MaxDecodedBytes = bytes };

    private static string ManyGraphics(int count, int declaredBytes)
    {
        var sb = new StringBuilder("^XA");
        for (int i = 0; i < count; i++) sb.Append("^FO0,0^GFA,").Append(declaredBytes).Append(',').Append(declaredBytes).Append(",1,00^FS");
        return sb.Append("^XZ").ToString();
    }

    [Fact]
    public void ManyShortGraphicFields_StopAtTheBudget_AndStayAsRawZpl()
    {
        // Each field declares 10,000 bytes (80,000 pixels); a 200,000 byte budget pays for 2 of the 8 declared fields.
        var r = ZplParser.Parse(ManyGraphics(8, 10_000), Small(200_000));

        int images = r.Document.Elements.OfType<ImageElement>().Count();
        int raw = r.Document.Elements.OfType<RawZplElement>().Count();
        Assert.Equal(2, images);
        Assert.Equal(6, raw);
        Assert.Equal(6, r.Warnings.Count(w => w.MessageKey == "Import.Graphic.TooMuchData"));
    }

    [Fact]
    public void RawPassthroughKeepsTheOriginalCommand()
    {
        var r = ZplParser.Parse(ManyGraphics(2, 10_000), Small(100_000));

        var raw = Assert.Single(r.Document.Elements.OfType<RawZplElement>());
        Assert.Contains("^GFA,10000,10000,1,00", raw.Zpl);
    }

    [Fact]
    public void DownloadedGraphicsAndGraphicFieldsShareOneBudget()
    {
        // A ~DG of 60,000 bytes uses 60,000 of the 100,000 budget, so a following ^GF (8 x 10,000 = 80,000) no longer fits.
        string zpl = "~DGR:A.GRF,60000,1," + new string('0', 100) + "^XA^FO0,0^GFA,10000,10000,1,00^FS^XZ";
        var r = ZplParser.Parse(zpl, Small(100_000));

        Assert.Contains(r.Warnings, w => w.MessageKey == "Import.Graphic.TooMuchData");
        Assert.Empty(r.Document.Elements.OfType<ImageElement>());
    }

    [Fact]
    public void DefaultBudgetStillAcceptsARealisticGraphic()
    {
        // 4 x 6 inch label at 300 dpi: 150 bytes per row, 1,800 rows.
        string zpl = "^XA^FO0,0^GFA,270000,270000,150,,^FS^XZ";
        var r = ZplParser.Parse(zpl);

        Assert.DoesNotContain(r.Warnings, w => w.MessageKey == "Import.Graphic.TooMuchData");
        Assert.Single(r.Document.Elements.OfType<ImageElement>());
    }
}
