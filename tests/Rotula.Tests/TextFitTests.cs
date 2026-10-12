using System.Text.RegularExpressions;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Files;
using Rotula.Rendering;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>
/// Text fit of a wrapped text block: shrink the font between a minimum and a maximum, or grow the lines up to a cap. Printer fonts are measured with
/// the estimate the overflow warning uses (exact and deterministic, so they have golden files); Windows fonts are measured with the rasterizer the
/// preview and the printed picture use (checked by properties, because the letter widths depend on the installed font).
/// </summary>
public class TextFitTests
{
    private const string Short = "NAUTICA COTT/SPAN 3PK 5 in BOXER BRIEF";
    private static readonly string Long = string.Join(" ", Enumerable.Repeat("NAUTICA COTT/SPAN 3PK 5 in BOXER BRIEF", 5));

    // The sample from the field: a 4x3 in label at 300 dpi with a wrapped block 1119 dots wide and up to 8 lines.
    private static LabelDocument Doc300(TextElement t)
    {
        var d = new LabelDocument { Width = Measure.Dots(1200), Height = Measure.Dots(900), Dpi = PrinterDpi.Dpi300 };
        d.Elements.Add(t);
        return d;
    }

    private static TextElement Block(string text, TextFitOptions? fit, int maxLines = 3, int width = 600, int? height = 30, int? fontWidth = 30) => new()
    {
        Id = "t", X = 20, Y = 20, Text = text, FontHeight = height, FontWidth = fontWidth,
        Block = new TextBlockOptions { WidthDots = width, MaxLines = maxLines, Fit = fit },
    };

    private static TextFitOptions Shrink(double min = 5, double max = 14) => new() { Mode = TextFitMode.Shrink, MinSize = min, MaxSize = max };

    private static (int H, int W, int Lines) Parse(string zpl)
    {
        var a = Regex.Match(zpl, @"\^A0N?,(\d+),(\d+)");
        var b = Regex.Match(zpl, @"\^FB\d+,(\d+),");
        Assert.True(a.Success && b.Success, zpl);
        return (int.Parse(a.Groups[1].Value), int.Parse(a.Groups[2].Value), int.Parse(b.Groups[1].Value));
    }

    // ------------------------------------------------------------------ shrink, printer font

    [Fact]
    public void Shrink_ShortText_KeepsTheMaximumSize()
    {
        var doc = CoreTestSupport.NewDoc(Block("HELLO", Shrink()));
        var result = ZplGenerator.Generate(doc);
        // 14 pt at 203 dpi is 39 dots; the width follows the height (30 over 30).
        Assert.Equal((39, 39, 3), Parse(result.Zpl));
        Assert.DoesNotContain(result.Issues, i => i.MessageKey.StartsWith("Text.Overflow"));
    }

    [Fact]
    public void Shrink_LongText_ChoosesTheLargestSizeThatFits()
    {
        var element = Block(Long, Shrink());
        var doc = CoreTestSupport.NewDoc(element);
        var result = ZplGenerator.Generate(doc);
        var (h, w, lines) = Parse(result.Zpl);

        Assert.InRange(h, 14, 38);
        Assert.Equal(3, lines);
        Assert.Equal(h, w);
        Assert.DoesNotContain(result.Issues, i => i.MessageKey.StartsWith("Text.Overflow"));
        // Fits at the chosen size and does not at one dot more: it is the largest size that fits.
        Assert.Equal(TextOverflow.None, TextFitEstimator.Estimate(element, Long, doc.DefaultFont, h, w, 3));
        Assert.NotEqual(TextOverflow.None, TextFitEstimator.Estimate(element, Long, doc.DefaultFont, h + 1, h + 1, 3));
    }

    [Fact]
    public void Shrink_StopsAtTheMinimumAndTheOverflowWarningStays()
    {
        string huge = string.Join(" ", Enumerable.Repeat(Long, 6));
        var result = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(huge, Shrink(10, 14))));
        // 10 pt at 203 dpi is 28 dots.
        Assert.Equal((28, 28, 3), Parse(result.Zpl));
        Assert.Contains(result.Issues, i => i.MessageKey == "Text.Overflow.Estimated");
    }

    [Fact]
    public void Shrink_ScalesAnExplicitWidthWithTheHeight_AndKeepsProportionalAtZero()
    {
        var wide = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, Shrink(), fontWidth: 60))).Zpl;
        var (h, w, _) = Parse(wide);
        Assert.InRange(w, h * 2 - 1, h * 2 + 1); // 60 over 30 stays two to one

        var proportional = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, Shrink(), fontWidth: 0))).Zpl;
        Assert.Equal(0, Parse(proportional).W);
    }

    [Fact]
    public void Shrink_WithoutAnExplicitFont_StillWritesACompleteFontCommand()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink(), height: null, fontWidth: null));
        doc.DefaultFont = new FontSpec('0', 30, 30);
        string zpl = ZplGenerator.Generate(doc).Zpl;
        Assert.Matches(@"\^A0N?,\d+,\d+\^FB", zpl);
    }

    [Fact]
    public void Shrink_IsDeterministic()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink()));
        Assert.Equal(ZplGenerator.Generate(doc).Zpl, ZplGenerator.Generate(doc).Zpl);
    }

    [Fact]
    public void Shrink_SampleLabel_300dpi_1119DotsWide_8Lines()
    {
        TextElement Sample(string text) => Block(text, Shrink(8, 28), maxLines: 8, width: 1119, height: 80, fontWidth: 80);
        var shortZpl = ZplGenerator.Generate(Doc300(Sample(Short))).Zpl;
        var longZpl = ZplGenerator.Generate(Doc300(Sample(Long + " " + Long + " " + Long))).Zpl;
        Assert.True(Parse(shortZpl).H > Parse(longZpl).H);
        CoreTestSupport.AssertGolden("text_fit_shrink_short", shortZpl);
        CoreTestSupport.AssertGolden("text_fit_shrink_long", longZpl);
    }

    // ------------------------------------------------------------------ grow, printer font

    [Fact]
    public void Grow_AddsLinesUpToTheNeed_AndKeepsTheFontSize()
    {
        var element = Block(Long, new TextFitOptions { Mode = TextFitMode.Grow, MaxLinesCap = 20 }, maxLines: 1);
        var result = ZplGenerator.Generate(CoreTestSupport.NewDoc(element));
        var (h, w, lines) = Parse(result.Zpl);
        Assert.Equal((30, 30), (h, w));
        int needed = TextFitEstimator.CountLines(element, Long, null, null, null);
        Assert.True(needed > 1);
        Assert.Equal(needed, lines);
        Assert.DoesNotContain(result.Issues, i => i.MessageKey.StartsWith("Text.Overflow"));
    }

    [Fact]
    public void Grow_NeverGoesBelowTheBlocksOwnLines_AndStopsAtTheCap()
    {
        var shortText = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block("HI", new TextFitOptions { Mode = TextFitMode.Grow, MaxLinesCap = 20 }, maxLines: 4)));
        Assert.Equal(4, Parse(shortText.Zpl).Lines);

        var capped = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, new TextFitOptions { Mode = TextFitMode.Grow, MaxLinesCap = 2 }, maxLines: 1)));
        Assert.Equal(2, Parse(capped.Zpl).Lines);
        Assert.Contains(capped.Issues, i => i.MessageKey == "Text.Overflow.Estimated");
        CoreTestSupport.AssertGolden("text_fit_grow", ZplGenerator.Generate(CoreTestSupport.NewDoc(
            Block(Long, new TextFitOptions { Mode = TextFitMode.Grow, MaxLinesCap = 20 }, maxLines: 1))).Zpl);
    }

    // ------------------------------------------------------------------ none and the ignore flag

    [Fact]
    public void None_IsTodaysOutput_AndIgnoreOnlySilencesTheWarning()
    {
        var plain = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, null, maxLines: 1)));
        var none = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, new TextFitOptions(), maxLines: 1)));
        var ignored = ZplGenerator.Generate(CoreTestSupport.NewDoc(Block(Long, new TextFitOptions { IgnoreOverflowWarning = true }, maxLines: 1)));

        Assert.Equal(plain.Zpl, none.Zpl);
        Assert.Equal(plain.Zpl, ignored.Zpl);
        Assert.Contains(plain.Issues, i => i.MessageKey == "Text.Overflow.Estimated");
        Assert.DoesNotContain(ignored.Issues, i => i.MessageKey.StartsWith("Text.Overflow"));
    }

    [Fact]
    public void ThePrintersOwnContent_IsNotFitted()
    {
        var element = Block(Long, Shrink());
        element.Serial = new PrinterSerialOptions();
        element.Text = "0001";
        var (h, _, _) = Parse(ZplGenerator.Generate(CoreTestSupport.NewDoc(element)).Zpl);
        Assert.Equal(30, h);
    }

    // ------------------------------------------------------------------ data merge

    [Fact]
    public void Merge_EveryRecordGetsItsOwnSize()
    {
        var field = new DataFieldElement { Id = "d", VariableName = "Name", X = 20, Y = 20, FontHeight = 30, FontWidth = 30,
            Block = new TextBlockOptions { WidthDots = 600, MaxLines = 3, Fit = Shrink() } };
        var doc = CoreTestSupport.NewDoc(field);
        var csv = "Name\n" + Short + "\nHI\n" + Long + "\n";
        var labels = MergeBatch.Labels(doc, new MergeDataSet(doc, CsvReader.Parse(csv))).ToList();

        Assert.Equal(3, labels.Count);
        var sizes = labels.Select(l => Parse(l.Zpl).H).ToList();
        Assert.Equal(39, sizes[1]);          // short content: the maximum
        Assert.True(sizes[2] < sizes[1]);    // long content shrinks
        Assert.True(sizes[2] <= sizes[0]);   // and never more than the medium one
        Assert.Equal(sizes, labels.Select(l => Parse(l.Zpl).H).ToList()); // stable
        // The same batch twice gives the same bytes.
        var again = MergeBatch.Labels(doc, new MergeDataSet(doc, CsvReader.Parse(csv))).Select(l => l.Zpl);
        Assert.Equal(labels.Select(l => l.Zpl), again);
    }

    // ------------------------------------------------------------------ ZPL round trip

    [Fact]
    public void RoundTrip_ComputedValuesComeBackAsAPlainBlock_TheFitModeDoesNot()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink()));
        string zpl = CoreTestSupport.Zpl(doc);
        var (h, w, lines) = Parse(zpl);

        var imported = (TextElement)ZplParser.Parse(zpl).Document.Elements.Single();
        Assert.Null(imported.Block!.Fit);
        Assert.Equal((h, w, lines), (imported.FontHeight, imported.FontWidth, imported.Block.MaxLines));
        // Writing the imported label gives the same ZPL back.
        Assert.Equal(zpl, CoreTestSupport.Zpl(ZplParser.Parse(zpl).Document));
    }

    // ------------------------------------------------------------------ Windows font

    [Fact]
    public void WindowsFont_Shrink_FitsAtTheLargestHalfPointSize()
    {
        var rasterizer = new SkiaTextRasterizer();
        var font = FontChoice.ForWindows("Arial", 30);
        var element = new TextElement { Id = "w", X = 20, Y = 20, Text = Long, Font = font,
            Block = new TextBlockOptions { WidthDots = 600, MaxLines = 6, Fit = Shrink(6, 40) } };
        var doc = CoreTestSupport.NewDoc(element);

        var fit = TextFitResolver.Resolve(doc, element, Long, rasterizer)!;
        Assert.NotNull(fit.PointSize);
        Assert.InRange(fit.PointSize!.Value, 6, 40);
        Assert.Equal(TextOverflow.None, fit.Overflow);

        TextMeasurement Measure(double pt) => rasterizer.Measure(new TextRasterRequest(Long, FontChoice.ForWindows("Arial", pt), 203, 600, 0, 6));
        Assert.False(Measure(fit.PointSize.Value).Overflows);
        Assert.True(fit.PointSize.Value + 0.5 > 40 || Measure(fit.PointSize.Value + 0.5).Overflows);

        // The generator draws the picture at that size and does not warn.
        var result = ZplGenerator.Generate(doc, null, rasterizer);
        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Issues, i => i.MessageKey.StartsWith("Text.Overflow"));
        Assert.Contains("^GFA", result.Zpl);
    }

    [Fact]
    public void WindowsFont_Grow_AddsLines_AndShortTextKeepsTheBlocksLines()
    {
        var rasterizer = new SkiaTextRasterizer();
        TextElement Make(string text) => new() { Id = "w", Text = text, Font = FontChoice.ForWindows("Arial", 14),
            Block = new TextBlockOptions { WidthDots = 400, MaxLines = 1, Fit = new TextFitOptions { Mode = TextFitMode.Grow, MaxLinesCap = 30 } } };
        var longOne = Make(Long);
        var fit = TextFitResolver.Resolve(CoreTestSupport.NewDoc(longOne), longOne, Long, rasterizer)!;
        Assert.True(fit.MaxLines > 1);
        Assert.Equal(TextOverflow.None, fit.Overflow);
        Assert.Equal(1, TextFitResolver.Resolve(CoreTestSupport.NewDoc(Make("Hi")), Make("Hi"), "Hi", rasterizer)!.MaxLines);
    }

    [Fact]
    public void WindowsFont_WithoutARasterizer_DoesNotFit()
    {
        var element = new TextElement { Id = "w", Text = Long, Font = FontChoice.ForWindows("Arial", 14),
            Block = new TextBlockOptions { WidthDots = 400, MaxLines = 1, Fit = Shrink() } };
        Assert.Null(TextFitResolver.Resolve(CoreTestSupport.NewDoc(element), element, Long, null));
    }

    // ------------------------------------------------------------------ preview shows what prints

    [Fact]
    public void Preview_UsesTheSameSizeAndLinesAsTheZpl()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink()));
        var (h, _, lines) = Parse(ZplGenerator.Generate(doc).Zpl);
        var drawn = (RenderText)LabelDocumentAdapter.ToRenderLabel(doc).Elements.Single();
        Assert.Equal(h, drawn.FontHeightDots);
        Assert.Equal(lines, drawn.MaxLines);

        var grow = CoreTestSupport.NewDoc(Block(Long, new TextFitOptions { Mode = TextFitMode.Grow }, maxLines: 1));
        var (_, _, growLines) = Parse(ZplGenerator.Generate(grow).Zpl);
        Assert.Equal(growLines, ((RenderText)LabelDocumentAdapter.ToRenderLabel(grow).Elements.Single()).MaxLines);
    }

    // ------------------------------------------------------------------ file format

    [Fact]
    public void File_FitRoundTripsAndStampsVersionNine()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, new TextFitOptions { Mode = TextFitMode.Grow, MinSize = 7, MaxSize = 18, MaxLinesCap = 12, IgnoreOverflowWarning = true }));
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.Equal(9, System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32());

        var fit = ((TextElement)RotulaFileSerializer.Deserialize(json).Elements.Single()).Block!.Fit!;
        Assert.Equal((TextFitMode.Grow, 7d, 18d, 12, true), (fit.Mode, fit.MinSize, fit.MaxSize, fit.MaxLinesCap, fit.IgnoreOverflowWarning));
    }

    [Fact]
    public void File_WithoutFit_KeepsItsOldVersion_AndAnOldFileOpensWithoutFit()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, null));
        string json = RotulaFileSerializer.Serialize(doc);
        Assert.True(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("formatVersion").GetInt32() < 9);
        Assert.DoesNotContain("\"fit\"", json);

        // A fit that changes nothing is not written either.
        Assert.DoesNotContain("\"fit\"", RotulaFileSerializer.Serialize(CoreTestSupport.NewDoc(Block(Long, new TextFitOptions()))));

        // A version 8 file written before this feature.
        string v8 = json.Replace("\"formatVersion\": 1", "\"formatVersion\": 8");
        Assert.Null(((TextElement)RotulaFileSerializer.Deserialize(v8).Elements.Single()).Block!.Fit);
    }

    [Fact]
    public void File_NewerVersionIsRefused_AndHostileFitValuesAreClamped()
    {
        Assert.True(RotulaFileSerializer.CurrentFormatVersion >= RotulaFileSerializer.TextFitFormatVersion);
        Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\":" + (RotulaFileSerializer.CurrentFormatVersion + 1) + "}"));

        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink()));
        string json = RotulaFileSerializer.Serialize(doc)
            .Replace("\"minSize\": 5", "\"minSize\": -3").Replace("\"maxSize\": 14", "\"maxSize\": 99999").Replace("\"maxLinesCap\": 20", "\"maxLinesCap\": 123456789");
        var fit = ((TextElement)RotulaFileSerializer.Deserialize(json).Elements.Single()).Block!.Fit!;
        Assert.Equal(TextFitOptions.LowestSize, fit.MinSize);
        Assert.Equal(FontChoice.MaxPointSize, fit.MaxSize);
        Assert.Equal(9999, fit.MaxLinesCap);

        string unknown = RotulaFileSerializer.Serialize(doc).Replace("\"Shrink\"", "\"Wobble\"");
        Assert.Null(((TextElement)RotulaFileSerializer.Deserialize(unknown).Elements.Single()).Block!.Fit);
    }

    [Fact]
    public void DpiConversion_KeepsTheFit()
    {
        var doc = CoreTestSupport.NewDoc(Block(Long, Shrink(6, 20)));
        var converted = LabelDpiConverter.Convert(doc, PrinterDpi.Dpi300).Document;
        var fit = ((TextElement)converted.Elements.Single()).Block!.Fit!;
        Assert.Equal((TextFitMode.Shrink, 6d, 20d), (fit.Mode, fit.MinSize, fit.MaxSize));
    }
}
