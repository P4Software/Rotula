using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The device picture: the label drawn at the printer driver's resolution as 8-bit grey (0 = black), ready for a GDI print job.
/// </summary>
public class DevicePictureRendererTests
{
    private static LabelDocument Doc(double widthMm, double heightMm, PrinterDpi dpi, params LabelElement[] elements)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(widthMm), Height = Measure.Millimeters(heightMm), Dpi = dpi };
        doc.Elements.AddRange(elements);
        return doc;
    }

    private static byte At(DevicePicture picture, int x, int y) => picture.Gray8[y * picture.WidthPx + x];

    /// <summary>
    /// Two solid boxes of different sizes near the top-left corner: content that looks different after any turn or flip, drawn without
    /// smoothing so a turned picture can be compared pixel for pixel.
    /// </summary>
    private static LabelDocument Asymmetric(PrinterDpi dpi) => Doc(50, 25, dpi,
        new BoxElement { X = 10, Y = 12, WidthDots = 120, HeightDots = 40, ThicknessDots = 40 },
        new BoxElement { X = 200, Y = 90, WidthDots = 30, HeightDots = 70, ThicknessDots = 30 });

    [Fact]
    public void ABarcodeThatCannotBeEncodedFailsTheLabel()
    {
        // EAN-13 takes digits only. The renderer would draw a red cross in its place; on a real label that is a silent substitution, so the
        // label fails instead, carrying a finding the app can translate.
        var bad = Doc(50, 25, PrinterDpi.Dpi300, new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "NOT-DIGITS", X = 40, Y = 40 });

        var error = Assert.Throws<DevicePictureException>(() => DevicePictureRenderer.Render(bad, null, 300));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        var issue = Assert.Single(error.Issues);
        Assert.Equal("Driver.Render.BarcodeNotDrawn", issue.MessageKey);
        Assert.Equal(Rotula.Core.Validation.ValidationSeverity.Error, issue.Severity);
        Assert.Equal(new[] { BarcodeCatalog.Get(BarcodeSymbology.Ean13).DisplayName, "NOT-DIGITS" }, issue.Args);

        // The same barcode with valid data draws normally.
        var good = Doc(50, 25, PrinterDpi.Dpi300, new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "5901234123457", X = 40, Y = 40 });
        Assert.Contains(DevicePictureRenderer.Render(good, null, 300).Gray8, p => p == 0);
    }

    [Fact]
    public void AnImageThatCannotBeDecodedFailsTheLabel()
    {
        // A design never produces unreadable image bytes (the adapter encodes the dithered dots itself), so the drawing step is fed directly:
        // bytes that are not a picture make the renderer draw a red cross, which must fail the label instead.
        var label = new Rotula.Rendering.Input.RenderLabel(200, 100, 300, new Rotula.Rendering.Input.RenderElement[]
        {
            new Rotula.Rendering.Input.RenderImage { EncodedBytes = [1, 2, 3], WidthDots = 50, HeightDots = 50, X = 10, Y = 10 },
        });

        var error = Assert.Throws<DevicePictureException>(() => DevicePictureRenderer.Draw(label));

        var issue = Assert.Single(error.Issues);
        Assert.Equal("Driver.Render.ImageNotDrawn", issue.MessageKey);
        Assert.Empty(issue.Args);
    }

    [Fact]
    public void CertificationMarksAreNotPrinted()
    {
        // The printer prints the real UL and CSA marks; the renderer only has the letters. Printing look-alike letters on a real label would
        // be a silent substitution, so the picture leaves them out (the pre-flight counts them). Registered, copyright and trademark are drawn.
        static LabelDocument Base() => Doc(50, 25, PrinterDpi.Dpi300, new BoxElement { X = 30, Y = 30, WidthDots = 60, HeightDots = 45, ThicknessDots = 45 });
        var withMarks = Base();
        withMarks.Elements.Add(new SymbolElement { Symbol = GraphicSymbol.UnderwritersLaboratories, HeightDots = 60, WidthDots = 60, X = 200, Y = 40 });
        withMarks.Elements.Add(new SymbolElement { Symbol = GraphicSymbol.CanadianStandards, HeightDots = 60, WidthDots = 60, X = 350, Y = 40 });
        // The preview does show them, as text, so the comparison below is not vacuous (and the preview is unchanged).
        var preview = Rotula.Rendering.Input.LabelDocumentAdapter.ToRenderLabelResolved(withMarks, null);
        Assert.Contains(preview.Elements, e => e is Rotula.Rendering.Input.RenderText { Text: "UL" });
        Assert.Contains(preview.Elements, e => e is Rotula.Rendering.Input.RenderText { Text: "CSA" });

        var plain = DevicePictureRenderer.Render(Base(), null, 300);
        Assert.Equal(plain.Gray8, DevicePictureRenderer.Render(withMarks, null, 300).Gray8);

        var withRegistered = Base();
        withRegistered.Elements.Add(new SymbolElement { Symbol = GraphicSymbol.Registered, HeightDots = 60, WidthDots = 60, X = 200, Y = 40 });
        Assert.NotEqual(plain.Gray8, DevicePictureRenderer.Render(withRegistered, null, 300).Gray8);
    }

    [Fact]
    public void AnObjectAnchoredToALeftOutMarkKeepsItsPlace()
    {
        // The mark is left out of the picture, not out of the design: an object positioned relative to it stays exactly where the printer puts it.
        var mark = new SymbolElement { Symbol = GraphicSymbol.UnderwritersLaboratories, HeightDots = 60, WidthDots = 60, X = 150, Y = 40 };
        var box = new BoxElement
        {
            WidthDots = 40, HeightDots = 40, ThicknessDots = 40, X = 0, Y = 100,
            Anchor = AnchorPoint.TopLeft, RelativeTo = AnchorReference.Element, ReferenceElementId = mark.Id,
        };
        var doc = Doc(50, 25, PrinterDpi.Dpi300, mark, box);
        var placed = Assert.IsType<Rotula.Rendering.Input.RenderBox>(Rotula.Rendering.Input.LabelDocumentAdapter.ToRenderLabelResolved(doc, null).Elements[1]);
        // Removing the mark from the design would move the box (its reference would be missing), so this test can tell the two apart.
        var withoutMark = Doc(50, 25, PrinterDpi.Dpi300, box);
        var moved = Assert.IsType<Rotula.Rendering.Input.RenderBox>(Assert.Single(Rotula.Rendering.Input.LabelDocumentAdapter.ToRenderLabelResolved(withoutMark, null).Elements));
        Assert.NotEqual((placed.X, placed.Y), (moved.X, moved.Y));

        var picture = DevicePictureRenderer.Render(doc, null, 300);

        Assert.Equal(0, At(picture, (int)placed.X + 20, (int)placed.Y + 20));
        Assert.Equal(255, At(picture, (int)moved.X + 20, (int)moved.Y + 20));
    }

    [Fact]
    public void PlaceholdersAreNeverDrawn()
    {
        // The same label with and without the parts the driver cannot print. The preview shows hatched stand-ins for them; on a real label
        // they would be a silent substitution, so the picture must be identical to the label without them.
        static LabelDocument Base() => Doc(50, 25, PrinterDpi.Dpi300, new BoxElement { X = 30, Y = 30, WidthDots = 60, HeightDots = 45, ThicknessDots = 45 });
        var withParts = Base();
        withParts.Elements.Add(new RawZplElement { Zpl = "^FO300,150^GB50,50,2^FS", IsVisual = true, X = 300, Y = 150 });
        withParts.Elements.Add(new BarcodeElement { Symbology = BarcodeCatalog.All.First(i => !i.HasPreviewEncoder).Symbology, Data = "12345", X = 20, Y = 150 });
        withParts.Elements.Add(new RecallPlaceholderElement { Command = "XG", Reference = "R:MISSING.GRF", Zpl = "^XGR:MISSING.GRF,1,1", X = 400, Y = 40 });
        // The adapter really turns these into stand-ins, so the comparison below is not vacuous.
        Assert.Equal(3, Rotula.Rendering.Input.LabelDocumentAdapter.ToRenderLabelResolved(withParts, null).Elements.Count(e => e is Rotula.Rendering.Input.RenderPlaceholder));

        var plain = DevicePictureRenderer.Render(Base(), null, 300);
        var picture = DevicePictureRenderer.Render(withParts, null, 300);

        // A normal label is unaffected: its box is still drawn.
        Assert.Contains(plain.Gray8, p => p == 0);
        Assert.Equal(plain.Gray8, picture.Gray8);
    }

    [Theory]
    [InlineData(PrinterDpi.Dpi300)]
    // 203 dpi drawn at 300: the rescaled copy of the label must keep the turn.
    [InlineData(PrinterDpi.Dpi203)]
    public void PrintUpsideDownTurnsThePicture(PrinterDpi design)
    {
        // ^POI: the printer turns the whole label by 180 degrees; in picture mode the picture itself must be turned.
        var normal = DevicePictureRenderer.Render(Asymmetric(design), null, 300);
        var invertedDoc = Asymmetric(design);
        invertedDoc.Orientation = LabelOrientation.Inverted;

        var inverted = DevicePictureRenderer.Render(invertedDoc, null, 300);

        Assert.NotEqual(normal.Gray8, inverted.Gray8);
        int w = normal.WidthPx, h = normal.HeightPx;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.True(At(inverted, x, y) == At(normal, w - 1 - x, h - 1 - y), $"pixel {x},{y} is not the turned pixel");
    }

    [Theory]
    [InlineData(PrinterDpi.Dpi300)]
    // 203 dpi drawn at 300: the rescaled copy of the label must keep the mirror flag.
    [InlineData(PrinterDpi.Dpi203)]
    public void MirrorImageFlipsThePicture(PrinterDpi design)
    {
        // ^PMY: the printer prints the label as a mirror image (left and right swapped); in picture mode the picture itself must be flipped.
        var normal = DevicePictureRenderer.Render(Asymmetric(design), null, 300);
        var mirroredDoc = Asymmetric(design);
        mirroredDoc.MirrorImage = true;

        var mirrored = DevicePictureRenderer.Render(mirroredDoc, null, 300);

        Assert.NotEqual(normal.Gray8, mirrored.Gray8);
        int w = normal.WidthPx, h = normal.HeightPx;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.True(At(mirrored, x, y) == At(normal, w - 1 - x, y), $"pixel {x},{y} is not the mirrored pixel");
    }

    [Fact]
    public void RendersGray8AtTheRequestedDpi()
    {
        // 25.4 x 12.7 mm is 1 x 0.5 inch: 300 x 150 dots in the design, 200 x 100 pixels at 200 dpi.
        // A solid box: 60 x 45 dots at (30, 30) in the design is 40 x 30 pixels at (20, 20) in the picture.
        var box = new BoxElement { X = 30, Y = 30, WidthDots = 60, HeightDots = 45, ThicknessDots = 45 };
        var doc = Doc(25.4, 12.7, PrinterDpi.Dpi300, box);

        var picture = DevicePictureRenderer.Render(doc, null, 200);

        Assert.Equal(200, picture.WidthPx);
        Assert.Equal(100, picture.HeightPx);
        Assert.Equal(20000, picture.Gray8.Length);
        // Inside the box (one pixel in from each edge) every pixel is black; the paper around it is white.
        for (int y = 21; y < 49; y++)
            for (int x = 21; x < 59; x++)
                Assert.Equal(0, At(picture, x, y));
        Assert.Equal(255, At(picture, 5, 5));
        Assert.Equal(255, At(picture, 150, 80));
    }

    [Fact]
    public void BarcodeModulesAreWholePixels()
    {
        // A 2-dot module at 203 dpi drawn at 300 dpi is round(2 * 300 / 203) = 3 pixels; every bar and space is a multiple of it.
        var barcode = new BarcodeElement
        {
            Symbology = BarcodeSymbology.Code128, Data = "ROTULA-12345", ModuleWidth = 2, Height = 80, HumanReadable = false, X = 40, Y = 20,
        };
        var doc = Doc(50, 25, PrinterDpi.Dpi203, barcode);

        var picture = DevicePictureRenderer.Render(doc, null, 300);

        // The barcode starts at round(20 * 300 / 203) = 30 and is 118 pixels tall, so row 80 runs through the bars.
        int row = 80;
        var dark = Enumerable.Range(0, picture.WidthPx).Select(x => At(picture, x, row) < 128).ToArray();
        int first = Array.IndexOf(dark, true), last = Array.LastIndexOf(dark, true);
        Assert.True(first >= 0, "the row should cross the bars");

        var runs = new List<int>();
        int length = 1;
        for (int x = first + 1; x <= last; x++)
        {
            if (dark[x] == dark[x - 1]) { length++; continue; }
            runs.Add(length);
            length = 1;
        }
        runs.Add(length);

        // ROTULA-12345 is a dozen symbol characters, each with 3 bars and 3 spaces.
        Assert.True(runs.Count > 30, $"expected the bars of a Code 128, found {runs.Count} runs");
        Assert.All(runs, r => Assert.Equal(0, r % 3));
        // The narrowest bar or space is exactly one module: 3 pixels, not 6 or more.
        Assert.Equal(3, runs.Min());
    }

    [Fact]
    public void UsesResolvedValuesWithoutResolvingAgain()
    {
        // A counter would show its own next number ("7") if the values were resolved again; the print run already decided this label says "A".
        static LabelDocument WithText(string text)
        {
            var doc = Doc(50, 25, PrinterDpi.Dpi300, new TextElement { Text = text, X = 30, Y = 30, FontHeight = 80, FontWidth = 80 });
            doc.Variables.Add(new LabelVariable("Name", "B") { Kind = VariableKind.Counter, Counter = new CounterVariableOptions { Start = "7" } });
            return doc;
        }
        var values = new Dictionary<string, string> { ["Name"] = "A" };

        var resolved = DevicePictureRenderer.Render(WithText("{{Name}}"), values, 300);
        var literalA = DevicePictureRenderer.Render(WithText("A"), null, 300);
        var literal7 = DevicePictureRenderer.Render(WithText("7"), null, 300);

        Assert.Contains(resolved.Gray8, p => p < 128);
        Assert.Equal(literalA.Gray8, resolved.Gray8);
        Assert.NotEqual(literal7.Gray8, resolved.Gray8);
    }

    [Fact]
    public void PictureSizeMatchesTheRenderedPicture()
    {
        // The pre-flight sizes the picture without drawing it; that figure must be the size the renderer really produces.
        var doc = Doc(37.3, 21.9, PrinterDpi.Dpi203);

        foreach (int dpi in new[] { 96, 150, 203, 300, 360, 600 })
        {
            var picture = DevicePictureRenderer.Render(doc, null, dpi);
            Assert.Equal(DevicePictureRenderer.PictureSize(doc, dpi), (picture.WidthPx, picture.HeightPx));
            Assert.Equal(picture.WidthPx * picture.HeightPx, picture.Gray8.Length);
        }
    }

    [Fact]
    public void RenderDpiIsTheCoarserAxis()
    {
        Assert.Equal(300, DevicePictureRenderer.RenderDpi(new DriverMedia(600, 300, 100, 100, 100, 100, 0, 0, true)));
        Assert.Equal(203, DevicePictureRenderer.RenderDpi(new DriverMedia(203, 203, 100, 100, 100, 100, 0, 0, true)));
        Assert.Equal(400, DevicePictureRenderer.RenderDpi(new DriverMedia(400, 600, 100, 100, 100, 100, 0, 0, true)));
    }

    [Fact]
    public void ANonPositiveResolutionIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DevicePictureRenderer.Render(Doc(10, 10, PrinterDpi.Dpi203), null, 0));
    }
}
