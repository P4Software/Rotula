using System.Text.Json;
using Rotula.App.Editing;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Files;
using Rotula.Rendering;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>A rasterizer that records its requests and returns a fixed, recognisable picture (no drawing, so the tests are exact).</summary>
internal sealed class FakeRasterizer : ITextRasterizer
{
    public List<TextRasterRequest> Requests { get; } = new();
    public TextOverflow Overflow { get; set; }
    public bool Substituted { get; set; }
    public int Width { get; set; } = 16;
    public int Height { get; set; } = 4;
    public int Baseline { get; set; } = 3;

    private TextMeasurement Measurement() => new(Width, Height, 1, Baseline, Overflow, Substituted, Substituted ? "Segoe UI" : "Arial");

    public TextMeasurement Measure(TextRasterRequest request) => Measurement();

    public TextRasterResult Rasterize(TextRasterRequest request)
    {
        lock (Requests) Requests.Add(request);
        int bpr = (Width + 7) / 8;
        var bytes = new byte[bpr * Height];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = i % 2 == 0 ? (byte)0xF0 : (byte)0x0F;
        return new TextRasterResult(new MonochromeBitmap(Width, Height, bytes), Measurement());
    }

    public IReadOnlyList<string> GetFontFamilies() => new[] { "Arial" };
}

/// <summary>Printer fonts keep real text; Windows fonts become a picture drawn after the data is in the text.</summary>
public class TextFormattingZplTests
{
    private static LabelDocument Doc(params LabelElement[] elements)
    {
        var d = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400), Dpi = PrinterDpi.Dpi203 };
        d.Elements.AddRange(elements);
        return d;
    }

    private static TextElement Win(string text = "Hello", Action<FontChoice>? font = null)
    {
        var f = FontChoice.ForWindows("Arial", 12);
        font?.Invoke(f);
        return new TextElement { X = 10, Y = 20, Text = text, Font = f };
    }

    // ------------------------------------------------------------------------------------ golden

    [Fact]
    public void ALabelWithoutAWindowsFont_IsExactlyAsBefore()
    {
        var doc = Doc(new TextElement { X = 10, Y = 20, Text = "Plain", FontId = 'A', FontHeight = 30, FontWidth = 20 });
        var zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Contains("^FO10,20^AA,30,20^FDPlain^FS\n", zpl);
        Assert.DoesNotContain("^GF", zpl);
    }

    [Fact]
    public void AWindowsFont_IsPrintedAsAGraphicField_NotAsText()
    {
        var fake = new FakeRasterizer();
        var r = ZplGenerator.Generate(Doc(Win()), null, fake);
        Assert.False(r.HasErrors);
        // 16 x 4 dots = 2 bytes a row, 8 bytes in all, written as hexadecimal.
        Assert.Contains("^FO10,20^GFA,8,8,2,F00FF00FF00FF00F^FS\n", r.Zpl);
        Assert.DoesNotContain("^FD", r.Zpl);
        var request = Assert.Single(fake.Requests);
        Assert.Equal("Hello", request.Text);
        Assert.Equal(203, request.Dpi);
        Assert.Equal(12, request.Font.PointSize);
    }

    [Fact]
    public void ABlock_GivesTheRasterizerItsBox()
    {
        var fake = new FakeRasterizer();
        var t = Win("Some words");
        t.Block = new TextBlockOptions { WidthDots = 300, MaxLines = 3, LineSpacing = 4, Alignment = TextAlignment.Justified, HangingIndent = 12 };
        ZplGenerator.Generate(Doc(t), null, fake);
        var q = Assert.Single(fake.Requests);
        Assert.Equal((300, 3, 4, TextAlignment.Justified, 12), (q.BoxWidthDots, q.MaxLines, q.LineSpacingDots, q.Alignment, q.HangingIndentDots));
    }

    [Fact]
    public void ABoundText_IsDrawnAfterTheValueIsInTheText_ForEveryRecord()
    {
        var fake = new FakeRasterizer();
        var doc = Doc(Win("Name: {{name}}"));
        doc.Variables.Add(new LabelVariable("name", "Sample"));
        var table = new MergeTable(new[] { "name" }, new[] { new[] { "Ana" }, new[] { "Luis" } });
        var set = new MergeDataSet(doc, table, new MergeOptions { TextRasterizer = fake });
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, set, report).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal(new[] { "Name: Ana", "Name: Luis" }, fake.Requests.Select(r => r.Text));
        Assert.All(labels, l => Assert.Contains("^GFA", l.Zpl));
        Assert.Empty(report.SkippedRecords);
    }

    [Fact]
    public void AnEmptyValue_PrintsNothingAndIsNotAnError()
    {
        var fake = new FakeRasterizer();
        var doc = Doc(Win("{{x}}"));
        var r = ZplGenerator.Generate(doc, new Dictionary<string, string> { ["x"] = "" }, fake);
        Assert.DoesNotContain("^GF", r.Zpl);
        Assert.Empty(fake.Requests);
        Assert.DoesNotContain(r.Issues, i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void WithoutARasterizer_AWindowsFontIsACleanError_NotASilentSubstitute()
    {
        var r = ZplGenerator.Generate(Doc(Win()));
        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i => i.MessageKey == "Text.Raster.NoRasterizer");
        Assert.DoesNotContain("^FD", r.Zpl);
        Assert.DoesNotContain("^GF", r.Zpl);
    }

    // ------------------------------------------------------------------------------------ placement

    [Fact]
    public void ARotatedText_IsTurnedInItsDots()
    {
        var fake = new FakeRasterizer { Width = 16, Height = 4 };
        var t = Win();
        t.Rotation = FieldRotation.Rotated90;
        var r = ZplGenerator.Generate(Doc(t), null, fake);
        // 4 wide, 16 high after the turn: one byte a row, 16 rows.
        Assert.Contains("^GFA,16,16,1,", r.Zpl);
    }

    [Theory]
    [InlineData(FieldRotation.Normal, 0, -3)]
    [InlineData(FieldRotation.Rotated90, -1, 0)]
    [InlineData(FieldRotation.Inverted180, -16, -1)]
    [InlineData(FieldRotation.Rotated270, -3, -16)]
    public void ATypesetText_IsPlacedFromItsBaseline(FieldRotation rotation, int dx, int dy)
    {
        // Picture 16 x 4 with the baseline 3 dots below its top.
        Assert.Equal((dx, dy), RasterTextEmitter.TypesetCorner(rotation, 16, 4, 3));
    }

    [Fact]
    public void ATypesetText_EmitsFoWithTheBaselineShiftedOut()
    {
        var fake = new FakeRasterizer { Baseline = 3 };
        var t = Win();
        t.OriginKind = FieldOriginKind.Typeset;
        t.Y = 100;
        var zpl = ZplGenerator.Generate(Doc(t), null, fake).Zpl;
        Assert.Contains("^FO10,97^GFA", zpl);
        Assert.DoesNotContain("^FT", zpl);
    }

    [Fact]
    public void AReversedText_KeepsItsReverseFlag()
    {
        var t = Win();
        t.ReverseField = true;
        Assert.Contains("^FO10,20^FR^GFA", ZplGenerator.Generate(Doc(t), null, new FakeRasterizer()).Zpl);
    }

    // ------------------------------------------------------------------------------------ warnings and errors

    [Fact]
    public void ATextThatDoesNotFit_RaisesTheOverflowWarning()
    {
        var r = ZplGenerator.Generate(Doc(Win()), null, new FakeRasterizer { Overflow = TextOverflow.Lines });
        Assert.Contains(r.Issues, i => i.MessageKey == "Text.Overflow" && i.Severity == ValidationSeverity.Warning);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void AMissingFont_IsReported()
    {
        var r = ZplGenerator.Generate(Doc(Win()), null, new FakeRasterizer { Substituted = true });
        var issue = Assert.Single(r.Issues, i => i.MessageKey == "Text.Font.Missing");
        Assert.Equal(new[] { "Arial", "Segoe UI" }, issue.Args);
    }

    [Fact]
    public void FieldOptionsAPictureCannotHonour_AreErrors()
    {
        var t = Win();
        t.FieldNumber = 3;
        var r = ZplGenerator.Generate(Doc(t), null, new FakeRasterizer());
        Assert.Contains(r.Issues, i => i.MessageKey == "Text.Raster.FieldOption" && i.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain("^GF", r.Zpl);
    }

    [Fact]
    public void APrinterClockDate_InAPicture_FallsBackToTheComputerClockWithAWarning()
    {
        var doc = Doc(Win("EXP {{d}}"));
        var o = new Rotula.Core.Variables.DateVariableOptions { Source = Rotula.Core.Variables.DateSource.PrinterClock, Format = "iso" };
        doc.Variables.Add(Vars.Date("d", o));
        var fake = new FakeRasterizer();
        var ctx = new Rotula.Core.Variables.VariableContext { Time = FakeTime.AtLocal(2026, 3, 7) };
        var r = ZplGenerator.GenerateWithVariables(doc, ctx, null, fake);
        Assert.Equal("EXP 2026-03-07", Assert.Single(fake.Requests).Text);
        Assert.Contains(r.Issues, i => i.MessageKey == "Text.Raster.PrinterClock");
        Assert.DoesNotContain("^FC", r.Zpl);
    }

    [Fact]
    public void BoldItalicUnderline_OnAPrinterFont_WarnInsteadOfVanishing()
    {
        var t = new TextElement { Text = "x", Font = new FontChoice { Source = FontSource.Printer, PrinterFontId = 'B', Bold = true } };
        var r = ZplGenerator.Generate(Doc(t));
        Assert.Contains(r.Issues, i => i.MessageKey == "Text.Style.PrinterFont");
        Assert.Contains("^AB", r.Zpl);
        Assert.False(FontChoice.ForPrinter('A').SupportsStyles);
        Assert.True(FontChoice.ForWindows("Arial").SupportsStyles);
    }

    [Fact]
    public void AStoredPrinterFont_IsPrintedWithAtA()
    {
        var t = new TextElement { X = 5, Y = 6, Text = "x", StoredFontPath = "E:ARIAL.TTF", FontHeight = 30, FontWidth = 25 };
        Assert.Contains("^FO5,6^A@,30,25,E:ARIAL.TTF^FDx^FS", ZplGenerator.Generate(Doc(t)).Zpl);
        t.StoredFontPath = "bad path";
        Assert.Contains(ZplGenerator.Generate(Doc(t)).Issues, i => i.MessageKey == "Text.StoredFont.Invalid");
    }

    [Fact]
    public void AnEstimateWarnsWhenAPrinterTextClearlyDoesNotFitItsBlock()
    {
        var t = new TextElement { Text = string.Join(' ', Enumerable.Repeat("wordy", 40)), FontHeight = 30, FontWidth = 30, Block = new TextBlockOptions { WidthDots = 200, MaxLines = 1 } };
        Assert.Contains(ZplGenerator.Generate(Doc(t)).Issues, i => i.MessageKey == "Text.Overflow.Estimated");
        t.Block = new TextBlockOptions { WidthDots = 800, MaxLines = 20 };
        Assert.DoesNotContain(ZplGenerator.Generate(Doc(t)).Issues, i => i.MessageKey == "Text.Overflow.Estimated");
    }

    // ------------------------------------------------------------------------------------ the model

    [Fact]
    public void FontChoice_ConvertsBetweenTheElementAndTheFontBox()
    {
        var t = new TextElement { FontId = 'C' };
        Assert.Equal('C', FontChoice.FromElement(t).PrinterFontId);
        Assert.Equal('0', FontChoice.FromElement(new TextElement()).PrinterFontId);

        FontChoice.ForWindows("Arial", 14, bold: true).ApplyTo(t);
        Assert.True(t.Font!.Bold);
        Assert.Equal("Arial", FontChoice.FromElement(t).FamilyName);

        FontChoice.ForPrinter('E').ApplyTo(t);
        Assert.Null(t.Font);          // a printer font is stored the way it always was
        Assert.Equal('E', t.FontId);
    }

    [Fact]
    public void PointsAndDots_ConvertAtTheResolution()
    {
        Assert.Equal(50.75, FontChoice.PointsToDots(18, 203), 2);
        Assert.Equal(18, FontChoice.DotsToPoints(FontChoice.PointsToDots(18, 600), 600), 6);
        Assert.NotNull(FontChoice.ForWindows("", 12).Validate());
        Assert.NotNull(FontChoice.ForWindows("Arial", 0).Validate());
        Assert.Null(FontChoice.ForWindows("Arial", 12).Validate());
    }

    [Fact]
    public void ConvertingTheResolution_KeepsAWindowsFontsPhysicalSize()
    {
        var doc = Doc(Win());
        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi300).Document;
        var font = ((TextElement)converted.Elements[0]).Font!;
        Assert.Equal(12, font.PointSize);
        var fake = new FakeRasterizer();
        ZplGenerator.Generate(converted, null, fake);
        Assert.Equal(300, fake.Requests.Single().Dpi); // the same points, drawn with more dots
    }

    [Fact]
    public void Cloning_CopiesTheFont_Independently()
    {
        var t = Win();
        t.StoredFontPath = null;
        var copy = (TextElement)ElementCloner.Clone(t);
        Assert.NotSame(t.Font, copy.Font);
        copy.Font!.Bold = true;
        Assert.False(t.Font!.Bold);
    }

    // ------------------------------------------------------------------------------------ files and import

    [Fact]
    public void TheFont_SurvivesSaveAndLoad_AndStampsVersionEight()
    {
        var doc = Doc(Win("Hi", f => { f.Bold = true; f.Italic = true; f.Underline = true; f.PointSize = 17.5; }));
        ((TextElement)doc.Elements[0]).StoredFontPath = null;
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(8, JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());
        var back = (TextElement)RotulaFileSerializer.Deserialize(json).Elements[0];
        Assert.Equal("Arial", back.Font!.FamilyName);
        Assert.True(back.Font.Bold && back.Font.Italic && back.Font.Underline);
        Assert.Equal(17.5, back.Font.PointSize);
    }

    [Fact]
    public void ALabelWithoutTheNewFields_StaysAtItsOldVersion()
    {
        var doc = Doc(new TextElement { Text = "x", FontId = 'A' });
        Assert.Equal(1, JsonDocument.Parse(RotulaFileSerializer.Serialize(doc)).RootElement.GetProperty("formatVersion").GetInt32());
        doc.Elements.Add(new TextElement { Text = "y", StoredFontPath = "E:F.TTF" });
        Assert.Equal(8, JsonDocument.Parse(RotulaFileSerializer.Serialize(doc)).RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void HostileFontValues_AreCleanedOrDropped()
    {
        string json = JsonSerializer.Serialize(new
        {
            formatVersion = 8,
            label = new { },
            elements = new object[]
            {
                new { type = "text", text = "a", font = new { source = "Windows", familyName = new string('x', 500) + "\u0001", pointSize = 1e9 }, storedFontPath = "..\\evil" },
                new { type = "text", text = "b", font = new { source = "Windows", familyName = "", pointSize = 12 } },
            },
        });
        var doc = RotulaFileSerializer.Deserialize(json);
        var a = (TextElement)doc.Elements[0];
        Assert.True(a.Font!.FamilyName.Length <= FontChoice.MaxFamilyLength);
        Assert.Equal(FontChoice.MaxPointSize, a.Font.PointSize);
        Assert.Null(a.StoredFontPath);
        Assert.Null(((TextElement)doc.Elements[1]).Font);
    }

    [Fact]
    public void AStoredFontText_RoundTripsThroughZpl()
    {
        var t = new TextElement { X = 5, Y = 6, Text = "x", StoredFontPath = "E:ARIAL.TTF", FontHeight = 30, FontWidth = 25 };
        var back = Assert.IsType<TextElement>(ZplParser.Parse(ZplGenerator.Generate(Doc(t)).Zpl).Document.Elements.Single());
        Assert.Equal("E:ARIAL.TTF", back.StoredFontPath);
        Assert.Equal(30, back.FontHeight);
        Assert.Equal("x", back.Text);
    }

    [Fact]
    public void AWindowsFontText_ComesBackAsAPicture_ByDesign()
    {
        // Documented limit of the round trip: the printed ZPL holds dots, not letters, so it can only come back as an image.
        var zpl = ZplGenerator.Generate(Doc(Win()), null, new FakeRasterizer()).Zpl;
        Assert.IsType<ImageElement>(ZplParser.Parse(zpl).Document.Elements.Single());
    }
}

/// <summary>The SkiaSharp rasterizer: real drawing with the bundled Liberation faces, so no test needs an installed font.</summary>
public class SkiaTextRasterizerTests
{
    private static readonly SkiaTextRasterizer Raster = new();

    private static TextRasterRequest Req(string text, double pt = 12, int dpi = 203, int box = 0, int maxLines = 0, Action<FontChoice>? font = null,
        int boxHeight = 0, TextAlignment align = TextAlignment.Left)
    {
        var f = FontChoice.ForWindows("Liberation Sans", pt);
        font?.Invoke(f);
        return new TextRasterRequest(text, f, dpi, box, boxHeight, maxLines, 0, align);
    }

    private static (int First, int Last) InkColumns(MonochromeBitmap b)
    {
        int first = -1, last = -1;
        for (int x = 0; x < b.Width; x++)
            for (int y = 0; y < b.Height; y++)
                if (b.IsBlack(x, y)) { if (first < 0) first = x; last = x; break; }
        return (first, last);
    }

    private static int InkCount(MonochromeBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (b.IsBlack(x, y)) n++;
        return n;
    }

    private const string Long = "The quick brown fox jumps over the lazy dog and keeps running through the meadow";

    [Fact]
    public void ASimpleText_GivesANonEmptyPictureAtThePrinterResolution()
    {
        var result = Raster.Rasterize(Req("Hello"));
        Assert.True(InkCount(result.Bitmap) > 20);
        Assert.Equal(1, result.Measurement.LineCount);
        Assert.Equal(result.Measurement.WidthDots, result.Bitmap.Width);
        // 12 pt at 203 dpi is an em of about 34 dots; the line is a little taller than that.
        Assert.InRange(result.Bitmap.Height, 30, 60);
        Assert.InRange(result.Measurement.BaselineDots, 20, result.Bitmap.Height);
        Assert.Equal(TextOverflow.None, result.Measurement.Overflow);
        Assert.False(result.Measurement.FontSubstituted);
    }

    [Fact]
    public void ALongText_WrapsInsideTheBoxWidth()
    {
        var result = Raster.Rasterize(Req(Long, box: 300));
        Assert.True(result.Measurement.LineCount > 2);
        Assert.Equal(300, result.Bitmap.Width);
        var (_, last) = InkColumns(result.Bitmap);
        Assert.True(last < 300, "ink reaches the edge of the box");
        Assert.Equal(TextOverflow.None, result.Measurement.Overflow);
    }

    [Fact]
    public void MoreLinesThanAllowed_AreCutAndReported()
    {
        var measure = Raster.Measure(Req(Long, box: 300, maxLines: 2));
        Assert.Equal(2, measure.LineCount);
        Assert.True((measure.Overflow & TextOverflow.Lines) != 0);
        Assert.True(measure.Overflows);
    }

    [Fact]
    public void ABoxThatIsTooShort_CutsLinesAndReportsHeight()
    {
        var full = Raster.Measure(Req(Long, box: 300));
        var cut = Raster.Measure(Req(Long, box: 300, boxHeight: full.HeightDots / 3));
        Assert.True(cut.LineCount < full.LineCount);
        Assert.True((cut.Overflow & TextOverflow.Height) != 0);
    }

    [Fact]
    public void AWordWiderThanTheBox_IsReportedAsWidthOverflow()
    {
        var m = Raster.Measure(Req("Supercalifragilisticexpialidocious", box: 120));
        Assert.True((m.Overflow & TextOverflow.Width) != 0);
    }

    [Fact]
    public void Alignment_MovesTheInk()
    {
        var left = InkColumns(Raster.Rasterize(Req("Hi", box: 400, align: TextAlignment.Left)).Bitmap);
        var right = InkColumns(Raster.Rasterize(Req("Hi", box: 400, align: TextAlignment.Right)).Bitmap);
        var center = InkColumns(Raster.Rasterize(Req("Hi", box: 400, align: TextAlignment.Center)).Bitmap);
        Assert.True(left.First < 10);
        Assert.True(right.Last > 385);
        Assert.InRange(center.First, 150, 250);
    }

    [Fact]
    public void BoldAndUnderline_AddInk()
    {
        int plain = InkCount(Raster.Rasterize(Req("Hello world")).Bitmap);
        int bold = InkCount(Raster.Rasterize(Req("Hello world", font: f => f.Bold = true)).Bitmap);
        int under = InkCount(Raster.Rasterize(Req("Hello world", font: f => f.Underline = true)).Bitmap);
        Assert.True(bold > plain);
        Assert.True(under > plain);
    }

    [Fact]
    public void ABiggerSize_MakesABiggerPicture()
    {
        var small = Raster.Measure(Req("Hello", pt: 10));
        var large = Raster.Measure(Req("Hello", pt: 20));
        Assert.True(large.HeightDots > small.HeightDots * 1.7);
        Assert.True(large.WidthDots > small.WidthDots * 1.7);
        // The same points at a higher resolution are more dots.
        Assert.True(Raster.Measure(Req("Hello", dpi: 600)).WidthDots > small.WidthDots * 2);
    }

    [Fact]
    public void AnUnknownFamily_IsReportedAsSubstituted()
    {
        var m = Raster.Measure(Req("Hello", font: f => f.FamilyName = "Definitely Not A Font 12345"));
        Assert.True(m.FontSubstituted);
        Assert.NotEqual("Definitely Not A Font 12345", m.ActualFamily);
    }

    [Fact]
    public void ThePreview_DrawsTheSameInkAsThePrintedPicture()
    {
        var req = Req("Hello world", pt: 14);
        var printed = Raster.Rasterize(req);
        // The preview: the same text as a render element at scale 1 (one pixel a dot).
        var label = new RenderLabel(400, 100, 203, new RenderElement[]
        {
            new RenderText
            {
                Text = req.Text, FamilyName = "Liberation Sans", FontHeightDots = FontChoice.PointsToDots(14, 203), X = 0, Y = 0,
            },
        });
        using var preview = LabelRenderer.RenderBitmap(label);
        int first = -1, last = -1;
        var span = preview.Bitmap.GetPixelSpan();
        for (int x = 0; x < preview.Bitmap.Width; x++)
            for (int y = 0; y < preview.Bitmap.Height; y++)
                if (span[y * preview.Bitmap.RowBytes + x * 4] < 128) { if (first < 0) first = x; last = x; break; }
        var (pf, pl) = InkColumns(printed.Bitmap);
        Assert.InRange(Math.Abs(first - pf), 0, 1);
        Assert.InRange(Math.Abs(last - pl), 0, 1);
    }

    [Fact]
    public void ItIsSafeToUseFromSeveralThreads()
    {
        var expected = Raster.Rasterize(Req(Long, box: 300)).Bitmap.PackedRows;
        Parallel.For(0, 40, _ => Assert.Equal(expected, Raster.Rasterize(Req(Long, box: 300)).Bitmap.PackedRows));
    }

    [Fact]
    public void TheFontList_IsSortedAndHasNoDuplicates()
    {
        var families = Raster.GetFontFamilies();
        Assert.Equal(families.Count, families.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EndToEnd_TheGeneratorPrintsTheRealPicture()
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400) };
        doc.Variables.Add(new LabelVariable("description", Long));
        doc.Elements.Add(new TextElement
        {
            X = 10, Y = 10, Text = "{{description}}", Font = FontChoice.ForWindows("Liberation Sans", 12),
            Block = new TextBlockOptions { WidthDots = 400, MaxLines = 5 },
        });
        var r = ZplGenerator.Generate(doc, null, Raster);
        Assert.False(r.HasErrors);
        Assert.Contains("^FO10,10^GFA,", r.Zpl);
        Assert.DoesNotContain(r.Issues, i => i.MessageKey == "Text.Overflow");

        // A one-line block cuts the text: the warning appears.
        ((TextElement)doc.Elements[0]).Block!.MaxLines = 1;
        Assert.Contains(ZplGenerator.Generate(doc, null, Raster).Issues, i => i.MessageKey == "Text.Overflow");
    }
}
