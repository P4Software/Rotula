using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Sheets;
using Rotula.Core.Stocks;
using Rotula.Core.Validation;
using Rotula.Core.Variables.Counters;
using Rotula.Files;
using Rotula.Printing;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>Sheet layouts for office printers: the geometry, its checks, the built-in templates and putting labels on sheets.</summary>
public class SheetLayoutTests
{
    private const double In = 25.4;
    private const double MmToPt = 72.0 / 25.4;

    private static SheetLayout Template(string id) => SheetTemplates.Find(id)!.Layout;

    private static void Near(double expected, double actual, double tolerance = 0.001) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    // ------------------------------------------------------------------------------------ positions

    [Fact]
    public void Letter30_PositionsFollowTheSheetGeometry_InMillimetresAndPoints()
    {
        var layout = Template("letter-30");
        Assert.Equal(30, layout.SlotCount);
        Assert.Equal(SheetPageKind.Letter, layout.PageKind);
        Near(215.9, layout.PageWidthMm);
        Near(279.4, layout.PageHeightMm);

        var first = layout.Slot(0);
        Near(0.1875 * In, first.XMm);
        Near(0.5 * In, first.YMm);
        Near(13.5, first.XMm * MmToPt); // 3/16 in = 13.5 pt
        Near(36, first.YMm * MmToPt);   // 1/2 in = 36 pt
        Near(2.625 * In, first.WidthMm);
        Near(1 * In, first.HeightMm);

        // Next column: one pitch (2-5/8 in label + 1/8 in gap = 2-3/4 in) to the right.
        Near((0.1875 + 2.75) * In, layout.Slot(1).XMm);
        Assert.Equal(1, layout.Slot(1).Column);
        // Position 4 starts the second row.
        var fourth = layout.Slot(3);
        Assert.Equal((0, 1), (fourth.Column, fourth.Row));
        Near(1.5 * In, fourth.YMm);
        var last = layout.Slot(29);
        Assert.Equal(30, last.Position);
        Near((0.1875 + 5.5) * In, last.XMm);
        Near(9.5 * In, last.YMm);
    }

    [Fact]
    public void A4_24_And_A4_21_PositionsAreExact()
    {
        var a24 = Template("a4-24");
        Assert.Equal((3, 8), (a24.Columns, a24.Rows));
        var last = a24.Slot(23);
        Near(140, last.XMm);
        Near(0.5 + 7 * 37, last.YMm);
        Near(70, last.WidthMm);
        Near(37, last.HeightMm);

        var a21 = Template("a4-21");
        var s = a21.Slot(20);
        Assert.Equal((2, 6), (s.Column, s.Row));
        Near(7.2 + 2 * 66.04, s.XMm);
        Near(15.15 + 6 * 38.1, s.YMm);
        Near(2.54, a21.HorizontalGapMm);
        Near(0, a21.VerticalGapMm);
    }

    [Fact]
    public void DownThenAcross_FillsAColumnFirst()
    {
        var layout = Template("letter-30");
        var second = layout.Slot(1, SheetOrder.DownThenAcross);
        Assert.Equal((0, 1), (second.Column, second.Row));
        var eleventh = layout.Slot(10, SheetOrder.DownThenAcross);
        Assert.Equal((1, 0), (eleventh.Column, eleventh.Row));
        Assert.Equal(30, layout.Slots(SheetOrder.DownThenAcross).Select(x => (x.Column, x.Row)).Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Slot(30));
    }

    [Fact]
    public void Centered_PutsTheGridInTheMiddleOfThePaper()
    {
        var layout = SheetLayout.Centered(SheetPageKind.A4, 3, 7, 63.5, 38.1, 2.54);
        Near(7.2, layout.LeftMarginMm, 0.01);
        Near(15.15, layout.TopMarginMm, 0.01);
        Near(layout.LeftMarginMm, layout.PageWidthMm - layout.GridRightMm, 0.01);
    }

    [Fact]
    public void WithPage_KeepsTheGridAndTakesTheNamedSize()
    {
        var legal = Template("letter-30").WithPage(SheetPageKind.Legal);
        Assert.Equal(SheetPageKind.Legal, legal.PageKind);
        Near(355.6, legal.PageHeightMm);
        Assert.Equal(30, legal.SlotCount);
        Assert.Equal(SheetPageKind.Custom, legal.WithPage(SheetPageKind.Custom).PageKind);
    }

    // ------------------------------------------------------------------------------------ templates

    [Fact]
    public void EveryTemplate_IsValid_AndHasAUniqueId()
    {
        Assert.True(SheetTemplates.All.Count >= 10);
        Assert.Equal(SheetTemplates.All.Count, SheetTemplates.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var t in SheetTemplates.All)
            Assert.True(t.Layout.Validate().All(i => i.Severity != ValidationSeverity.Error), t.Id + ": " + string.Join(", ", t.Layout.Validate()));
        // The common formats are there.
        foreach (var (id, count) in new[] { ("letter-30", 30), ("letter-10", 10), ("letter-2", 2), ("a4-24", 24), ("a4-21", 21) })
            Assert.Equal(count, Template(id).SlotCount);
        Assert.Same(SheetTemplates.All[0], SheetTemplates.Find("LETTER-30"));
        Assert.Null(SheetTemplates.Find("nope"));
    }

    [Fact]
    public void SheetStocksOfTheLibrary_FindTheirTemplate()
    {
        var catalog = StockCatalog.LoadBundled();
        Assert.Equal("letter-30", SheetTemplates.FindForStock(catalog.Find("avery-5160"))?.Id);
        Assert.Equal("letter-10", SheetTemplates.FindForStock(catalog.Find("avery-5163"))?.Id);
        Assert.Equal("a4-21", SheetTemplates.FindForStock(catalog.Find("avery-L7160"))?.Id);
        // A roll stock is never a sheet.
        Assert.Null(SheetTemplates.FindForStock(catalog.Entries.First(e => e.Format == StockFormat.Roll)));
        Assert.Null(SheetTemplates.FindForStock(null));
    }

    [Fact]
    public void FindForLabelSize_PrefersThePaperAsked()
    {
        Assert.Equal("a4-24", SheetTemplates.FindForLabelSize(70, 37, SheetPageKind.A4)?.Id);
        Assert.Equal("letter-30", SheetTemplates.FindForLabelSize(66.7, 25.4)?.Id);
        Assert.Null(SheetTemplates.FindForLabelSize(12, 12));
    }

    // ------------------------------------------------------------------------------------ validation

    private static SheetLayout Grid(Func<SheetLayout, SheetLayout>? change = null)
    {
        var layout = SheetLayout.Create(SheetPageKind.A4, 2, 2, 90, 130, 10, 10, 10, 10);
        return change is null ? layout : change(layout);
    }

    private static string[] Errors(SheetLayout layout) =>
        layout.Validate().Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray();

    [Fact]
    public void Validate_AcceptsAGridThatFits()
    {
        Assert.Empty(Grid().Validate());
        Assert.True(Grid().IsValid);
    }

    [Fact]
    public void Validate_RefusesLabelsThatDoNotFitThePage()
    {
        Assert.Equal(new[] { "Sheet.Labels.TooWide" }, Errors(Grid(l => l with { LeftMarginMm = 25 })));
        Assert.Equal(new[] { "Sheet.Labels.TooTall" }, Errors(Grid(l => l with { Rows = 3 })));
        var tooWide = Grid(l => l with { LeftMarginMm = 25 }).Validate().Single();
        Assert.Equal(new[] { "215", "210" }, tooWide.Args);
    }

    [Fact]
    public void Validate_RefusesOverlappingLabels()
    {
        Assert.Contains("Sheet.Labels.OverlapAcross", Errors(Grid(l => l with { HorizontalPitchMm = 80 })));
        Assert.Contains("Sheet.Labels.OverlapDown", Errors(Grid(l => l with { VerticalPitchMm = 100 })));
        // One column cannot overlap anything, whatever its pitch says.
        Assert.Empty(Errors(Grid(l => l with { Columns = 1, HorizontalPitchMm = 0 })));
    }

    [Fact]
    public void Validate_RefusesNonsense()
    {
        Assert.Contains("Sheet.Margin.Negative", Errors(Grid(l => l with { TopMarginMm = -1 })));
        Assert.Contains("Sheet.Grid.Count", Errors(Grid(l => l with { Columns = 0 })));
        Assert.Contains("Sheet.Grid.Count", Errors(Grid(l => l with { Rows = SheetLayout.MaxRows + 1 })));
        Assert.Contains("Sheet.Label.Size", Errors(Grid(l => l with { LabelWidthMm = 0 })));
        Assert.Contains("Sheet.Page.Size", Errors(Grid(l => l with { PageKind = SheetPageKind.Custom, PageWidthMm = 10 })));
        Assert.Contains("Sheet.Page.Size", Errors(Grid(l => l with { PageHeightMm = double.NaN })));
    }

    [Fact]
    public void Validate_WarnsWhenLabelsReachThePaperEdge()
    {
        // The 70 x 37 mm sheet is cut edge to edge: valid, with a warning about what a printer can reach.
        var issues = Template("a4-24").Validate();
        var warning = Assert.Single(issues);
        Assert.Equal(ValidationSeverity.Warning, warning.Severity);
        Assert.Equal("Sheet.Labels.NearEdge", warning.MessageKey);
        Assert.True(Template("a4-24").IsValid);
    }

    [Fact]
    public void ValidateStart_AcceptsOnlyPositionsOnTheSheet()
    {
        var layout = Template("letter-10");
        Assert.Null(layout.ValidateStart(1));
        Assert.Null(layout.ValidateStart(10));
        Assert.Equal("Sheet.Start.Range", layout.ValidateStart(0)?.MessageKey);
        Assert.Equal(new[] { "10" }, layout.ValidateStart(11)!.Args);
    }

    [Fact]
    public void CheckDesignSize_SaysWhenTheDesignIsLargerOrSmaller()
    {
        var layout = Template("letter-30");
        Assert.Null(layout.CheckDesignSize(66.8, 25.3));
        Assert.Equal("Sheet.Label.Larger", layout.CheckDesignSize(100, 150)?.MessageKey);
        var smaller = Template("letter-10").CheckDesignSize(50, 20);
        Assert.Equal("Sheet.Label.Smaller", smaller?.MessageKey);
        Assert.Equal(new[] { "50", "20", "101.6", "50.8" }, smaller!.Args);
    }

    // ------------------------------------------------------------------------------------ imposition

    [Fact]
    public void Paginate_FillsSheetsInOrder_AndBreaksPages()
    {
        var layout = Template("letter-10");
        var pages = SheetImposition.Paginate(layout, Enumerable.Range(1, 23)).ToList();
        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { 1, 2, 3 }, pages.Select(p => p.PageNumber));
        Assert.Equal(new[] { 10, 10, 3 }, pages.Select(p => p.Placements.Count));
        Assert.Equal(Enumerable.Range(1, 10), pages[0].Placements.Select(p => p.Item));
        Assert.Equal(Enumerable.Range(1, 10), pages[1].Placements.Select(p => p.Slot.Position));
        Assert.Equal(new long[] { 21, 22, 23 }, pages[2].Placements.Select(p => p.LabelNumber));
        Assert.Equal(3, SheetImposition.PageCount(layout, 23));
        Assert.Equal(2, SheetImposition.PageCount(layout, 20));
        Assert.Equal(0, SheetImposition.PageCount(layout, 0));
        Assert.Empty(SheetImposition.Paginate(layout, Array.Empty<int>()));
    }

    [Fact]
    public void Paginate_StartPosition_SkipsUsedPositionsOnTheFirstSheetOnly()
    {
        var layout = Template("letter-10");
        var pages = SheetImposition.Paginate(layout, Enumerable.Range(1, 10), startPosition: 5).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, pages[0].Placements.Select(p => p.Slot.Position));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, pages[0].Placements.Select(p => p.Item));
        Assert.Equal(new[] { 1, 2, 3, 4 }, pages[1].Placements.Select(p => p.Slot.Position));
        Assert.Equal(2, SheetImposition.PageCount(layout, 10, 5));
        Assert.Equal(1, SheetImposition.PageCount(layout, 6, 5));
        // The last position of a sheet works as a start, the one after does not.
        Assert.Single(SheetImposition.Paginate(layout, new[] { 1 }, 10).Single().Placements);
        Assert.Throws<ArgumentOutOfRangeException>(() => SheetImposition.Paginate(layout, new[] { 1 }, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => SheetImposition.Paginate(layout, new[] { 1 }, 0));
    }

    [Fact]
    public void Paginate_IsLazy_OneSheetAtATime()
    {
        int pulled = 0;
        IEnumerable<int> Endless() { while (true) yield return ++pulled; }
        var first = SheetImposition.Paginate(Template("letter-30"), Endless()).First();
        Assert.Equal(30, first.Placements.Count);
        Assert.Equal(30, pulled);
    }

    [Fact]
    public void Paginate_DownThenAcross_UsesColumnOrder()
    {
        var page = SheetImposition.Paginate(Template("letter-10"), new[] { "a", "b", "c" }, 1, SheetOrder.DownThenAcross).Single();
        Assert.Equal(new[] { (0, 0), (0, 1), (0, 2) }, page.Placements.Select(p => (p.Slot.Column, p.Slot.Row)));
    }

    // ------------------------------------------------------------------------------------ merge records and serial numbers

    private static LabelDocument CounterDoc(int quantity = 1, bool remember = false)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(101.6), Height = Measure.Millimeters(50.8) };
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = remember; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement { Id = "name", X = 10, Y = 10, Text = "{{Name}}" });
        doc.Elements.Add(new TextElement { Id = "sn", X = 10, Y = 60, Text = "{{serial}}" });
        doc.Print.Quantity = quantity;
        return doc;
    }

    [Fact]
    public void MergedLabels_CarryTheirResolvedValuesAndCopies_AndBreakAcrossSheets()
    {
        var doc = CounterDoc();
        var options = new MergeOptions { CopiesPerRecord = 2 };
        var set = new MergeDataSet(doc, CsvReader.Parse("Name\nAlpha\nBeta\nGamma\n"), options);
        var labels = MergeBatch.Labels(doc, set).ToList();
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003" }, labels.Select(l => l.Values!["serial"]));
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, labels.Select(l => l.Values!["Name"]));
        Assert.All(labels, l => Assert.Equal(2, l.Copies));
        // The ZPL of a record carries the same number its values carry.
        Assert.All(labels, l => Assert.Contains(l.Values!["serial"], l.Zpl));

        // 3 records x 2 copies on a 2 x 2 sheet that starts at position 3: two labels on the first sheet, four on the second.
        var sheet = SheetLayout.Create(SheetPageKind.Letter, 2, 2, 101.6, 50.8, 20, 5, 3, 3);
        var flat = labels.SelectMany(l => Enumerable.Repeat(l.Values!["serial"], l.Copies));
        var pages = SheetImposition.Paginate(sheet, flat, startPosition: 3).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { "SN0001", "SN0001" }, pages[0].Placements.Select(p => p.Item));
        Assert.Equal(new[] { 3, 4 }, pages[0].Placements.Select(p => p.Slot.Position));
        Assert.Equal(new[] { "SN0002", "SN0002", "SN0003", "SN0003" }, pages[1].Placements.Select(p => p.Item));
    }

    [Fact]
    public void MergedLabels_WithoutDefinitions_UseTheRecordAndTheLabelQuantity()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(25) };
        doc.Elements.Add(new TextElement { Id = "t", X = 10, Y = 10, Text = "{{Name}}" });
        doc.Print.Quantity = 3;
        var set = new MergeDataSet(doc, CsvReader.Parse("Name\nAlpha\n"), new MergeOptions());
        var label = Assert.Single(MergeBatch.Labels(doc, set));
        Assert.Equal("Alpha", label.Values!["Name"]);
        Assert.Equal(3, label.Copies);
    }

    [Fact]
    public void LabelPrintRun_LabelValues_GiveEveryCopyItsOwnSerial_AndConfirmOnlyWhatWasWritten()
    {
        var store = new MemoryCounterStore();
        var doc = CounterDoc(quantity: 5, remember: true);
        var run = LabelPrintRun.Begin(doc, null, new PrintVariableOptions { CounterStore = store });
        Assert.True(run.CanPrint, string.Join(", ", run.Issues));
        var values = run.LabelValues().ToList();
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004", "SN0005" }, values.Select(v => v!["serial"]));
        run.Complete(values.Count);
        Assert.Equal("SN0005", CounterAdmin.Peek(doc, "serial", store).LastValue);

        // The next run continues after the last confirmed number: a serial is never written twice.
        var next = LabelPrintRun.Begin(doc, null, new PrintVariableOptions { CounterStore = store });
        Assert.Equal("SN0006", next.LabelValues().First()!["serial"]);
        next.Cancel();
        Assert.Equal("SN0005", CounterAdmin.Peek(doc, "serial", store).LastValue);
    }

    [Fact]
    public void LabelPrintRun_LabelValues_RepeatAPlainLabel()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(25) };
        doc.Elements.Add(new TextElement { Id = "t", X = 10, Y = 10, Text = "{{Name}}" });
        doc.Print.Quantity = 3;
        var record = new Dictionary<string, string> { ["Name"] = "Alpha" };
        var run = LabelPrintRun.Begin(doc, record);
        var values = run.LabelValues().ToList();
        Assert.Equal(3, values.Count);
        Assert.All(values, v => Assert.Same(record, v));
    }

    [Fact]
    public void ToRenderLabelResolved_KeepsTheGivenSerial()
    {
        var doc = CounterDoc();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "Alpha", ["serial"] = "SN0042" };
        var resolved = LabelDocumentAdapter.ToRenderLabelResolved(doc, values);
        Assert.Contains(resolved.Elements.OfType<RenderText>(), t => t.Text == "SN0042");
        // The preview path resolves the counter again and shows its own value instead, which is why sheets use the resolved path.
        var preview = LabelDocumentAdapter.ToRenderLabel(doc, values);
        Assert.DoesNotContain(preview.Elements.OfType<RenderText>(), t => t.Text == "SN0042");
    }

    // ------------------------------------------------------------------------------------ PDF

    private static RenderLabel SampleLabel(double widthMm, double heightMm, int dpi = 203) =>
        new((int)Math.Round(widthMm / 25.4 * dpi), (int)Math.Round(heightMm / 25.4 * dpi), dpi,
            new RenderElement[] { new RenderText { Text = "Sheet", FontHeightDots = 30, X = 10, Y = 10 } });

    private static string Pdf(SheetLayout layout, int labels, SheetExportOptions? options, out SheetExportResult result)
    {
        var label = SampleLabel(layout.LabelWidthMm, layout.LabelHeightMm);
        using var ms = new MemoryStream();
        result = SheetExporter.SavePdf(layout, Enumerable.Repeat(label, labels), ms, options);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void Pdf_HasOnePagePerSheet_AtLetterSize()
    {
        string text = Pdf(Template("letter-30"), 31, null, out var result);
        Assert.StartsWith("%PDF-", text);
        Assert.Equal(2, result.Pages);
        Assert.Equal(31, result.Labels);
        Assert.Equal(2, Regex.Matches(text, @"/Type /Page\b").Count);
        // 8.5 x 11 in = 612 x 792 pt.
        Assert.Equal(2, Regex.Matches(text, @"/MediaBox \[\s*0 0 612 792\s*\]").Count);
    }

    [Fact]
    public void Pdf_StartPosition_AddsAPageWhenTheFirstSheetIsPartlyUsed()
    {
        Pdf(Template("letter-10"), 10, new SheetExportOptions { StartPosition = 1 }, out var full);
        Assert.Equal(1, full.Pages);
        string text = Pdf(Template("letter-10"), 10, new SheetExportOptions { StartPosition = 4, Guides = SheetGuides.CropMarks }, out var partial);
        Assert.Equal(2, partial.Pages);
        Assert.Equal(2, Regex.Matches(text, @"/Type /Page\b").Count);
    }

    [Fact]
    public void Pdf_A4_PageIsA4()
    {
        string text = Pdf(Template("a4-21"), 5, new SheetExportOptions { Guides = SheetGuides.Outlines }, out var result);
        Assert.Equal(1, result.Pages);
        // 210 x 297 mm = 595.28 x 841.89 pt.
        var box = Regex.Match(text, @"/MediaBox \[\s*0 0 ([\d.]+) ([\d.]+)\s*\]");
        Assert.True(box.Success);
        Near(595.28, double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 0.1);
        Near(841.89, double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0.1);
    }

    [Fact]
    public void Pdf_EmptyRunWritesNothing_AndAnInvalidLayoutIsRefused()
    {
        using var ms = new MemoryStream();
        var result = SheetExporter.SavePdf(Template("letter-30"), Array.Empty<RenderLabel>(), ms);
        Assert.Equal(0, result.Pages);
        Assert.Equal(0, ms.Length);
        var bad = Template("letter-30") with { Columns = 4 };
        Assert.Throws<ArgumentException>(() => SheetExporter.SavePdf(bad, new[] { SampleLabel(66.68, 25.4) }, new MemoryStream()));
    }

    [Fact]
    public void Placement_DrawsTheLabelAtItsPhysicalSize_CentredInItsPosition()
    {
        var layout = Template("letter-30");
        // 2-5/8 x 1 in at 300 dpi: 787.5 rounds to 788 dots, so the drawn label is 189.12 pt for a 189 pt position.
        var label = SampleLabel(66.675, 25.4, 300);
        var (slot, drawn) = SheetExporter.Placement(layout.Slot(0), label);
        Near(13.5, slot.Left, 0.01);
        Near(36, slot.Top, 0.01);
        Near(189, slot.Width, 0.01);
        Near(72, slot.Height, 0.01);
        Near(label.WidthDots * 72.0 / 300, drawn.Width, 0.01);
        Near(slot.MidX, drawn.MidX, 0.01);
        Near(slot.MidY, drawn.MidY, 0.01);

        // A design half the size keeps its size: it is not stretched to the position.
        var small = SampleLabel(33.3375, 12.7, 203);
        var (_, smallDrawn) = SheetExporter.Placement(layout.Slot(0), small);
        Near(small.WidthDots * 72.0 / 203, smallDrawn.Width, 0.01);
        Near(slot.MidX, smallDrawn.MidX, 0.01);
    }

    [Fact]
    public void Pdf_FileIsWrittenThroughATempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rotula-sheet-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "sheets.pdf");
            var result = SheetExporter.SavePdf(Template("a4-24"), Enumerable.Repeat(SampleLabel(70, 37), 25), path);
            Assert.Equal(2, result.Pages);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            // Nothing to write leaves no file behind.
            string empty = Path.Combine(dir, "empty.pdf");
            SheetExporter.SavePdf(Template("a4-24"), Array.Empty<RenderLabel>(), empty);
            Assert.False(File.Exists(empty));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------------------------ .rotula file

    [Fact]
    public void Serializer_RoundTripsTheSheetSettings_WithoutChangingTheFormatVersion()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(66.68), Height = Measure.Millimeters(25.4) };
        var settings = new SheetSettings
        {
            TemplateId = "letter-30",
            Layout = Template("letter-30") with { TopMarginMm = 13.2 },
            Order = SheetOrder.DownThenAcross,
            Guides = SheetGuides.CropMarks,
        };
        string json = RotulaFileSerializer.Serialize(doc, null, settings);
        Assert.Contains("\"sheet\"", json);
        Assert.Contains("\"formatVersion\": 1", json);

        var back = RotulaFileSerializer.DeserializeFile(json).Sheet;
        Assert.NotNull(back);
        Assert.Equal(settings, back);

        // A label without a sheet layout writes no sheet section and reads back none.
        string plain = RotulaFileSerializer.Serialize(doc);
        Assert.DoesNotContain("\"sheet\"", plain);
        Assert.Null(RotulaFileSerializer.DeserializeFile(plain).Sheet);
    }

    [Fact]
    public void Serializer_DropsASheetSectionThatMakesNoSense()
    {
        var doc = new LabelDocument();
        var settings = new SheetSettings { TemplateId = "letter-30", Layout = Template("letter-30") };
        string json = RotulaFileSerializer.Serialize(doc, null, settings);

        // Labels that no longer fit the page: the section is ignored, the label still opens.
        string broken = json.Replace("\"columns\": 3", "\"columns\": 9");
        var content = RotulaFileSerializer.DeserializeFile(broken);
        Assert.NotNull(content.Document);
        Assert.Null(content.Sheet);

        // An unknown template id is forgotten, the layout is kept.
        var unknown = RotulaFileSerializer.DeserializeFile(json.Replace("\"letter-30\"", "\"made-up\"")).Sheet;
        Assert.NotNull(unknown);
        Assert.Null(unknown!.TemplateId);
        Assert.Equal(30, unknown.Layout.SlotCount);

        // A named paper keeps its real size even if the numbers were edited.
        var edited = RotulaFileSerializer.DeserializeFile(json.Replace("\"pageHeightMm\": 279.4", "\"pageHeightMm\": 300")).Sheet;
        Assert.Equal(279.4, edited!.Layout.PageHeightMm);
    }
}
