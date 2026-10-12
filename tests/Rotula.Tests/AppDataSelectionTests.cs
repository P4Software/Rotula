using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Data;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// The records list of the Data merge window: tick, untick, select all / none / invert, filter, sort, stepping, and the rule that
/// printing, checking and exporting use only the ticked records.
/// </summary>
[Collection("App")]
public class AppDataSelectionTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppDataSelectionTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = Build();
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private MainViewModel Build()
    {
        var main = new MainViewModel(_fake.Build());
        main.Printer.SelectedPrinterName = "Test Printer";
        return main;
    }

    private DataMergeViewModel Data => _main.Data;

    private string Csv(string text, string name = "data.csv")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A label "Item {{n}}" and a file with <paramref name="rows"/> records: n = 1.., name = Name1.., city alternating Lima / Oslo.</summary>
    private async Task Load(int rows, string template = "Item {{n}}")
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = template }));
        var sb = new StringBuilder("n,name,city\n");
        for (int i = 1; i <= rows; i++) sb.Append(i).Append(",Name").Append(i).Append(',').Append(i % 2 == 0 ? "Oslo" : "Lima").Append('\n');
        await Data.LoadAsync(Csv(sb.ToString()));
    }

    private static int[] Numbers(DataMergeViewModel d) => d.VisibleRows.Select(r => r.Number).ToArray();

    private static string[] Items(IEnumerable<string> jobs) =>
        jobs.SelectMany(j => Regex.Matches(j, @"Item (\d+)").Select(m => m.Value)).ToArray();

    // ------------------------------------------------------------------------------------ image exports

    /// <summary>True when the image has any strongly red pixel, which is how the renderer marks a barcode it cannot draw (a crossed red box).</summary>
    private static bool HasPlaceholderRed(string path)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(path);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 80 && c.Blue < 80) return true;
            }
        return false;
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    public async Task ImageExports_UseTheRecordShownOnTheCanvas_SoABarcodeOfAFieldIsDrawnNotACrossedBox(string extension)
    {
        await Load(3);
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new BarcodeElement { Data = "{{name}}", X = 20, Y = 120 }));
        _fake.ExportPath = Path.Combine(_dir, "out." + extension);
        if (extension == "png") _main.ExportPngCommand.Execute(null); else _main.ExportJpegCommand.Execute(null);
        Assert.False(HasPlaceholderRed(_fake.ExportPath), "The export drew the placeholder instead of the barcode of the shown record.");
    }

    /// <summary>Counts dark pixels (text or bars) inside a rectangle of the exported picture.</summary>
    private static int DarkPixels(SkiaSharp.SKBitmap bitmap, int left, int top, int width, int height)
    {
        int count = 0;
        for (int y = top; y < top + height && y < bitmap.Height; y++)
            for (int x = left; x < left + width && x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) count++;
            }
        return count;
    }

    /// <summary>
    /// The real label a user reported (4 x 3 in at 300 dpi: SKU, wrapped description and a Code 128 of a field, all from a data file): after
    /// loading the file and its data, the PNG and JPEG must show the text and the barcode of the record on screen, not an empty label.
    /// </summary>
    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    public async Task ImageExports_OfAFieldLabel_ShowTheSkuTheDescriptionAndTheBarcode(string extension)
    {
        string label = Path.Combine(ZplParserTestSupport.FixturesDir(), "field-label-with-data.rotula");
        Assert.True(_main.LoadFile(label));
        await Data.LoadAsync(Csv("Sku,UPC,description\n38M-BLK,603679585511,NAUTICA COTT/SPAN 3PK 5 in BOXER BRIEF\n38M-WHT,603679585528,SECOND ITEM\n"));
        Assert.Equal(2, Data.RecordCount);

        _fake.ExportPath = Path.Combine(_dir, "label." + extension);
        if (extension == "png") _main.ExportPngCommand.Execute(null); else _main.ExportJpegCommand.Execute(null);

        using var bitmap = SkiaSharp.SKBitmap.Decode(_fake.ExportPath);
        Assert.Equal((1200, 900), (bitmap.Width, bitmap.Height));
        Assert.False(HasPlaceholderRed(_fake.ExportPath));
        Assert.True(DarkPixels(bitmap, 190, 50, 400, 90) > 500, "The SKU text is missing.");
        Assert.True(DarkPixels(bitmap, 40, 220, 1120, 180) > 2000, "The description text is missing.");
        Assert.True(DarkPixels(bitmap, 140, 496, 910, 270) > 20000, "The barcode is missing.");
        string keep = Environment.GetEnvironmentVariable("ROTULA_TEST_KEEP") ?? "";
        if (keep.Length > 0) File.Copy(_fake.ExportPath, Path.Combine(keep, "label." + extension), true);
    }

    /// <summary>Reopening a label saved with a data file brings the data back without any click, so the canvas shows real values.</summary>
    [Fact]
    public async Task OpeningALabelSavedWithADataFile_LoadsTheDataByItself()
    {
        await Load(3);
        string label = Path.Combine(_dir, "withdata.rotula");
        Assert.True(_main.Data.HasData);
        _fake.SavePath = label;          // saving here is what makes this PC trust the file
        _main.SaveCommand.Execute(null);

        var again = Build();
        Assert.True(again.LoadFile(label));
        await again.DataReload;

        Assert.True(again.Data.HasData);
        Assert.Equal(3, again.Data.RecordCount);
        Assert.Equal("Name1", again.Data.CurrentData!["name"]);
    }

    // ------------------------------------------------------------------------------------ ticking

    [Fact]
    public async Task EveryRecordStartsTicked_AndTheSummaryAlwaysSaysHowMany()
    {
        await Load(5);
        Assert.Equal(5, Data.SelectedCount);
        Assert.Equal("5 of 5 selected", Data.SelectionSummary);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Numbers(Data));
        Assert.True(Data.ShownSelectionState);
        Assert.Equal("Print all 5 labels", Data.PrintAllText);
    }

    [Fact]
    public async Task TickingAndUntickingOneRecord_UpdatesTheCounts_TheHeaderAndThePrintButton()
    {
        await Load(5);
        Data.VisibleRows[1].IsSelected = false;
        Assert.Equal(4, Data.SelectedCount);
        Assert.Equal("4 of 5 selected", Data.SelectionSummary);
        Assert.Null(Data.ShownSelectionState);                 // some, not all: the header box shows a dash
        Assert.Equal("Print 4 selected labels", Data.PrintAllText);
        Assert.Equal(new[] { 0, 2, 3, 4 }, Data.SelectedIndexes);

        Data.VisibleRows[1].IsSelected = true;
        Assert.Equal(5, Data.SelectedCount);
        Assert.True(Data.ShownSelectionState);
    }

    [Fact]
    public async Task SelectNone_SelectAll_Invert()
    {
        await Load(4);
        Data.SelectNoneShownCommand.Execute(null);
        Assert.Equal(0, Data.SelectedCount);
        Assert.False(Data.ShownSelectionState);
        Data.VisibleRows[0].IsSelected = true;
        Data.VisibleRows[2].IsSelected = true;
        Data.InvertShownCommand.Execute(null);
        Assert.Equal(new[] { 1, 3 }, Data.SelectedIndexes);
        Data.SelectAllShownCommand.Execute(null);
        Assert.Equal(4, Data.SelectedCount);
    }

    [Fact]
    public async Task TheHeaderBox_SelectsAndUnselectsEverythingShown()
    {
        await Load(4);
        Data.ShownSelectionState = false;
        Assert.Equal(0, Data.SelectedCount);
        Data.ShownSelectionState = true;
        Assert.Equal(4, Data.SelectedCount);
    }

    [Fact]
    public async Task ShiftClickRange_TicksEveryRowBetweenTwoPositions()
    {
        await Load(8);
        Data.SelectNoneShownCommand.Execute(null);
        Data.SetRange(2, 5, true);
        Assert.Equal(new[] { 2, 3, 4, 5 }, Data.SelectedIndexes);
        Data.SetRange(4, 1, false);                 // the end may come before the start
        Assert.Equal(new[] { 5 }, Data.SelectedIndexes);
    }

    // ------------------------------------------------------------------------------------ filter

    [Fact]
    public async Task TheFilter_MatchesAnyColumn_IgnoringCase_AndPlainText()
    {
        await Load(10);
        Data.FilterText = "oSLo";
        await Data.RefreshViewAsync();
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, Numbers(Data));
        Assert.True(Data.IsFiltered);
        Assert.Equal("Showing 5 of 10 records", Data.ShownSummary);

        Data.FilterText = "name1";                   // matches Name1 and Name10, but "." or "*" are not wildcards
        await Data.RefreshViewAsync();
        Assert.Equal(new[] { 1, 10 }, Numbers(Data));
        Data.FilterText = "n.me";
        await Data.RefreshViewAsync();
        Assert.Empty(Data.VisibleRows);
        Assert.True(Data.ShowNoMatches);
    }

    [Fact]
    public async Task TheFilter_CanBeLimitedToOneColumn()
    {
        await Load(10);
        Data.SelectedFilterColumn = Data.FilterColumns.Single(c => c.Name == "n");
        Data.FilterText = "1";
        await Data.RefreshViewAsync();
        Assert.Equal(new[] { 1, 10 }, Numbers(Data));   // "Lima" rows also contain no 1 in other columns, but Name1 would match "all columns"
        Data.SelectedFilterColumn = Data.FilterColumns[0];
        await Data.RefreshViewAsync();
        Assert.Equal(new[] { 1, 10 }, Numbers(Data));
        Assert.Equal("All columns", Data.FilterColumns[0].Name);
    }

    [Fact]
    public async Task SelectAllWithAFilter_ReachesOnlyTheShownRows_AndTheButtonsSaySo()
    {
        await Load(10);
        Data.SelectNoneShownCommand.Execute(null);
        Data.FilterText = "Oslo";
        await Data.RefreshViewAsync();
        Assert.Equal("Select all shown: 5", Data.SelectAllText);
        Assert.Equal("Unselect all shown: 5", Data.SelectNoneText);
        Assert.Equal("Invert shown: 5", Data.InvertText);

        Data.SelectAllShownCommand.Execute(null);
        Assert.Equal(new[] { 1, 3, 5, 7, 9 }, Data.SelectedIndexes);   // zero-based rows of records 2, 4, 6, 8, 10
        Assert.Equal("5 of 10 selected", Data.SelectionSummary);

        Data.ClearFilterCommand.Execute(null);
        await Data.RefreshViewAsync();
        Assert.Equal("Select all", Data.SelectAllText);
        Assert.Equal(10, Data.VisibleRows.Count);
    }

    [Fact]
    public async Task TheSelection_SurvivesFilterChanges_AndSorting()
    {
        await Load(10);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[2].IsSelected = true;       // record 3
        Data.VisibleRows[6].IsSelected = true;       // record 7

        Data.FilterText = "Lima";
        await Data.RefreshViewAsync();               // records 3 and 7 are Lima
        Data.FilterText = "Oslo";
        await Data.RefreshViewAsync();               // hidden, still ticked
        Assert.Equal(2, Data.SelectedCount);
        Assert.Equal(new[] { 2, 6 }, Data.SelectedIndexes);

        Data.ClearFilterCommand.Execute(null);
        await Data.SortByAsync(0);                   // by n, text order
        await Data.SortByAsync(0);                   // descending
        Assert.Equal(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, Numbers(Data));
        Assert.Equal(2, Data.SelectedCount);
        Assert.True(Data.VisibleRows.Single(r => r.Number == 3).IsSelected);
        Assert.True(Data.VisibleRows.Single(r => r.Number == 7).IsSelected);
    }

    [Fact]
    public async Task ShowOnlySelected_ListsTheTickedRecords()
    {
        await Load(6);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[1].IsSelected = true;
        Data.VisibleRows[4].IsSelected = true;
        Data.ShowOnlySelected = true;
        await Data.RefreshViewAsync();
        Assert.Equal(new[] { 2, 5 }, Numbers(Data));
    }

    [Fact]
    public async Task Filtering100000Rows_StaysFast_AndKeepsTheRightRows()
    {
        var sb = new StringBuilder("id,code\n");
        for (int i = 1; i <= 100_000; i++) sb.Append(i).Append(",C").Append(i % 1000).Append('\n');
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{id}}" }));
        await Data.LoadAsync(Csv(sb.ToString(), "big.csv"));
        Assert.Equal(100_000, Data.RecordCount);

        var clock = Stopwatch.StartNew();
        Data.FilterText = "c999";
        await Data.RefreshViewAsync();
        clock.Stop();
        Assert.Equal(100, Data.VisibleRows.Count);
        Assert.True(clock.ElapsedMilliseconds < 3000, $"Filtering took {clock.ElapsedMilliseconds} ms.");

        Data.SelectNoneShownCommand.Execute(null);   // only the 100 shown rows
        Assert.Equal(99_900, Data.SelectedCount);
        Data.ClearFilterCommand.Execute(null);
        await Data.RefreshViewAsync();
        Assert.Equal(100_000, Data.VisibleRows.Count);
    }

    // ------------------------------------------------------------------------------------ sort

    [Fact]
    public async Task Sorting_IsNumericForNumbers_StableForEqualValues_AndCyclesToTheOriginalOrder()
    {
        await Load(10);
        await Data.SortByAsync(0);                   // n: 1..10 sorted as numbers, not as text
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, Numbers(Data));
        await Data.SortByAsync(0);
        Assert.Equal(new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, Numbers(Data));
        await Data.SortByAsync(0);
        Assert.Equal(-1, Data.SortColumn);

        await Data.SortByAsync(2);                   // city: Lima x5, Oslo x5; equal cities keep the original order
        Assert.Equal(new[] { 1, 3, 5, 7, 9, 2, 4, 6, 8, 10 }, Numbers(Data));
        await Data.SortByAsync(2);
        Assert.Equal(new[] { 2, 4, 6, 8, 10, 1, 3, 5, 7, 9 }, Numbers(Data));
    }

    // ------------------------------------------------------------------------------------ stepping

    [Fact]
    public async Task TheNavigator_StepsThroughTickedRecordsOnly_AndTheCanvasFollows()
    {
        await Load(6);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[1].IsSelected = true;       // records 2, 4, 5
        Data.VisibleRows[3].IsSelected = true;
        Data.VisibleRows[4].IsSelected = true;

        Data.FirstRecordCommand.Execute(null);
        Assert.Equal(1, Data.CurrentIndex);
        Assert.Equal("Selected 1 of 3 (row 2)", Data.RecordText);
        Assert.Equal("2", Data.CurrentData!["n"]);          // the canvas shows the ticked record
        Data.NextRecordCommand.Execute(null);
        Assert.Equal(3, Data.CurrentIndex);
        Data.NextRecordCommand.Execute(null);
        Assert.Equal(4, Data.CurrentIndex);
        Assert.False(Data.NextRecordCommand.CanExecute(null));
        Data.PreviousRecordCommand.Execute(null);
        Assert.Equal(3, Data.CurrentIndex);
        Data.LastRecordCommand.Execute(null);
        Assert.Equal("Selected 3 of 3 (row 5)", Data.RecordText);

        // The toolbar of the main window steps the same way.
        _main.PreviousRecordCommand.Execute(null);
        Assert.Equal(3, Data.CurrentIndex);
        Assert.Equal("Selected 2 of 3 (row 4)", _main.DataRecordText);
    }

    [Fact]
    public async Task AnUntickedRecordCanStillBeShown_AndStepsFromItsPlace()
    {
        await Load(6);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[0].IsSelected = true;
        Data.VisibleRows[5].IsSelected = true;
        Data.CurrentIndex = 2;                       // clicking a row in the grid, ticked or not
        Assert.Equal("Row 3 (not selected; 2 selected)", Data.RecordText);
        Data.NextRecordCommand.Execute(null);
        Assert.Equal(5, Data.CurrentIndex);
        Data.CurrentIndex = 2;
        Data.PreviousRecordCommand.Execute(null);
        Assert.Equal(0, Data.CurrentIndex);
    }

    [Fact]
    public async Task WhenEverythingIsTicked_TheRecordTextIsTheOldOne()
    {
        await Load(3);
        Assert.Equal("Record 1 of 3", Data.RecordText);
        Data.NextRecordCommand.Execute(null);
        Assert.Equal("Record 2 of 3", Data.RecordText);
    }

    [Fact]
    public async Task CurrentRow_FollowsTheNavigator_AndSettingItShowsThatRecord()
    {
        await Load(4);
        Assert.Same(Data.VisibleRows[0], Data.CurrentRow);
        Data.CurrentRow = Data.VisibleRows[2];
        Assert.Equal(2, Data.CurrentIndex);
        Data.CurrentRow = null;                      // the grid clears it while its rows are replaced: ignored
        Assert.Equal(2, Data.CurrentIndex);
    }

    // ------------------------------------------------------------------------------------ batch: only ticked records

    [Fact]
    public async Task PrintAll_SendsOnlyTickedRecords_InDataOrder_EvenWhenTheListIsSorted()
    {
        await Load(6);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[1].IsSelected = true;
        Data.VisibleRows[2].IsSelected = true;
        Data.VisibleRows[5].IsSelected = true;
        await Data.SortByAsync(0);
        await Data.SortByAsync(0);                   // descending on screen
        Assert.Equal("Print 3 selected labels", Data.PrintAllText);

        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Item 2", "Item 3", "Item 6" }, Items(_fake.Printer.ZplJobs.Select(j => j.Zpl)));
        Assert.Contains("3 selected records (of 6)", _fake.Confirms.Single());
        Assert.Contains("3 labels", Data.ResultText);
        Assert.False(Data.ResultIsError);
    }

    [Fact]
    public async Task Confirmation_CountsCopies_FixedAndFromAColumn()
    {
        await Load(4);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[0].IsSelected = true;
        Data.VisibleRows[3].IsSelected = true;
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "5";
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("2 selected records (of 4)", _fake.Confirms[0]);
        Assert.Contains("10 labels counting copies", _fake.Confirms[0]);

        Data.SelectAllShownCommand.Execute(null);
        _fake.Confirms.Clear();
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Equal("Send 4 records to Test Printer? That makes 20 labels counting copies. Labels will start printing right away.", _fake.Confirms[0].Replace(Data.Printer.Summary, "Test Printer"));
    }

    [Fact]
    public async Task Confirmation_SumsTheCopiesColumnOfTheTickedRecordsOnly()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{n}}" }));
        await Data.LoadAsync(Csv("n,qty\nA,2\nB,5\nC,7\n"));
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "qty";
        Data.VisibleRows[1].IsSelected = false;
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("9 labels counting copies", _fake.Confirms.Single());   // 2 + 7, not 14
    }

    [Fact]
    public async Task WhenNothingIsTicked_PrintingIsDisabled_WithAPlainReason()
    {
        await Load(3);
        Data.SelectNoneShownCommand.Execute(null);
        Assert.False(Data.PrintAllCommand.CanExecute(null));
        Assert.False(Data.ExportBatchCommand.CanExecute(null));
        Assert.False(Data.CheckAllCommand.CanExecute(null));
        Assert.True(Data.HasBatchBlockedText);
        Assert.Contains("No record is selected", Data.BatchBlockedText);

        await Data.PrintAllCommand.ExecuteAsync(null);   // even if it is forced, nothing leaves
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.True(Data.ResultIsError);
        Assert.Contains("No record is selected", Data.ResultText);

        Data.VisibleRows[0].IsSelected = true;
        Assert.True(Data.PrintAllCommand.CanExecute(null));
        Assert.False(Data.HasBatchBlockedText);
    }

    [Fact]
    public async Task Export_WritesOnlyTickedRecords()
    {
        await Load(5);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[0].IsSelected = true;
        Data.VisibleRows[4].IsSelected = true;
        string path = Path.Combine(_dir, "out.zpl");
        _fake.ExportPath = path;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        string zpl = File.ReadAllText(path);
        Assert.Equal(new[] { "Item 1", "Item 5" }, Items(new[] { zpl }));
        Assert.Contains("2 labels", Data.ResultText);
    }

    [Fact]
    public async Task ExportPerJob_CountsJobsFromTheTickedRecords()
    {
        await Load(7);
        Data.SelectNoneShownCommand.Execute(null);
        foreach (int i in new[] { 0, 2, 4, 6 }) Data.VisibleRows[i].IsSelected = true;
        Data.MaxRecordsText = "2";
        Data.OneFilePerJob = true;
        _fake.ExportPath = Path.Combine(_dir, "jobs.zpl");
        await Data.ExportBatchCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_dir, "jobs_001.zpl")));
        Assert.True(File.Exists(Path.Combine(_dir, "jobs_002.zpl")));
        Assert.False(File.Exists(Path.Combine(_dir, "jobs_003.zpl")));
        Assert.Equal(new[] { "Item 1", "Item 3" }, Items(new[] { File.ReadAllText(Path.Combine(_dir, "jobs_001.zpl")) }));
    }

    [Fact]
    public async Task Check_ExaminesOnlyTickedRecords()
    {
        await Load(5);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[1].IsSelected = true;
        Data.VisibleRows[2].IsSelected = true;
        await Data.CheckAllCommand.ExecuteAsync(null);
        Assert.Contains("Checked 2 records", Data.IssuesSummary);
        Assert.Contains("All 2 records are ready", Data.ResultText);
    }

    // ------------------------------------------------------------------------------------ serial numbers

    private sealed class MemoryVariables(ICounterStore store) : IVariableServices
    {
        public ICounterStore? CounterStore => store;
        public IPromptMemory PromptMemory { get; } = new MemoryPromptMemory();
        public IReadOnlyDictionary<string, string>? Ask(PromptSet set) => new Dictionary<string, string>();
    }

    private async Task<(DataMergeViewModel Data, MemoryCounterStore Store, LabelDocument Doc)> SerialBatch(int rows)
    {
        var store = new MemoryCounterStore();
        _fake.Variables = new MemoryVariables(store);
        var main = Build();
        main.Session.Edit("Undo.Property", d =>
        {
            d.EnsureId();
            d.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
            d.Elements.Add(new TextElement { Text = "{{serial}} Item {{n}}" });
        });
        await main.Data.LoadAsync(Csv("n\n" + string.Join('\n', Enumerable.Range(1, rows)) + "\n", "serial.csv"));
        return (main.Data, store, main.Session.Document);
    }

    [Fact]
    public async Task SerialNumbers_AreReservedOnlyForTickedRecords()
    {
        var (data, store, doc) = await SerialBatch(6);
        data.SelectNoneShownCommand.Execute(null);
        data.VisibleRows[1].IsSelected = true;       // records 2 and 5
        data.VisibleRows[4].IsSelected = true;

        await data.PrintAllCommand.ExecuteAsync(null);
        var sent = _fake.Printer.ZplJobs.Select(j => j.Zpl).ToArray();
        Assert.Matches(@"SN0001 Item 2", string.Concat(sent));
        Assert.Matches(@"SN0002 Item 5", string.Concat(sent));
        Assert.Equal(2, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);     // two numbers used, not five or six

        // The next batch, with different ticks, goes on after them.
        _fake.Printer.ZplJobs.Clear();
        data.SelectNoneShownCommand.Execute(null);
        data.VisibleRows[0].IsSelected = true;
        await data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("SN0003 Item 1", _fake.Printer.ZplJobs.Single().Zpl);
    }

    [Fact]
    public async Task Check_DoesNotUseUpSerialNumbers_AndCountsOnlyTickedRecords()
    {
        var (data, store, doc) = await SerialBatch(5);
        data.SelectNoneShownCommand.Execute(null);
        data.VisibleRows[3].IsSelected = true;
        await data.CheckAllCommand.ExecuteAsync(null);
        await data.CheckAllCommand.ExecuteAsync(null);
        Assert.Equal(0, store.Get(CounterKey.For(doc.Id!, "serial"))?.Next ?? 0);   // two checks, no number used
        await data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("SN0001 Item 4", _fake.Printer.ZplJobs.Single().Zpl);
    }

    [Fact]
    public async Task TheCanvasPreview_ShowsTheSerialNumberTheRecordWillPrint()
    {
        var (data, _, _) = await SerialBatch(6);
        data.SelectNoneShownCommand.Execute(null);
        data.VisibleRows[1].IsSelected = true;
        data.VisibleRows[4].IsSelected = true;
        data.CurrentIndex = 4;
        Assert.Equal("SN0002", data.CurrentData!["serial"]);
        data.CurrentIndex = 1;
        Assert.Equal("SN0001", data.CurrentData!["serial"]);
    }

    // ------------------------------------------------------------------------------------ not saved, reset

    [Fact]
    public async Task TheSelection_IsNeverSaved()
    {
        await Load(4);
        Data.SelectNoneShownCommand.Execute(null);
        Data.VisibleRows[1].IsSelected = true;
        Data.FilterText = "zzz";
        string path = Path.Combine(_dir, "label.rotula");
        RotulaFileSerializer.Save(_main.Session.Document, path, Data.ToSettings());
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("select", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zzz", saved);
    }

    [Fact]
    public async Task LoadingDataAgain_StartsWithEveryRecordTicked_AndNoFilter()
    {
        await Load(4);
        Data.SelectNoneShownCommand.Execute(null);
        Data.FilterText = "Oslo";
        await Data.RefreshViewAsync();
        await Data.LoadAsync(Csv("n,name,city\n1,A,x\n2,B,y\n", "second.csv"));
        Assert.Equal(2, Data.SelectedCount);
        Assert.Equal(string.Empty, Data.FilterText);
        Assert.Equal(2, Data.VisibleRows.Count);
        Assert.Equal(-1, Data.SortColumn);
    }

    // ------------------------------------------------------------------------------------ language

    [Fact]
    public async Task TheTexts_FollowTheLanguage()
    {
        await Load(3);
        Data.VisibleRows[0].IsSelected = false;
        Loc.Instance.SetLanguage("es");
        Assert.Equal("2 de 3 seleccionados", Data.SelectionSummary);
        Assert.Equal("Imprimir 2 etiquetas seleccionadas", Data.PrintAllText);
        Assert.Equal("Seleccionar el registro 2", Data.VisibleRows[1].SelectName);
        Assert.Equal("Todas las columnas", Data.FilterColumns[0].Name);
    }
}

/// <summary>Selection works for a database source too, because it feeds the same merge pipeline as a file.</summary>
[Collection("App")]
public class AppDataSelectionDatabaseTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();

    public AppDataSelectionDatabaseTests() => _fake.Settings.Current.Language = "en";

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task ADatabaseSource_PrintsOnlyTickedRows()
    {
        _fake.Database.Store.Add(new DatabaseConnectionSettings { Name = "Warehouse", Provider = DatabaseProvider.SqlServer, Host = "h", Database = "WH" }, "pw");
        var main = new MainViewModel(_fake.Build());
        main.Printer.SelectedPrinterName = "Test Printer";
        main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "{{Name}} x{{Qty}}" }));
        var data = main.Data;
        data.IsDatabaseMode = true;
        await data.Database.RefreshConnectionsAsync();
        await data.Database.SelectConnectionAsync("Warehouse");
        data.Database.SelectedTable = data.Database.Tables.Single(t => t.Name == "Products");
        for (int i = 0; i < 500 && data.Database.IsBusy; i++) await Task.Delay(10);
        await data.Database.UseDataAsync();

        Assert.Equal(2, data.RecordCount);
        Assert.Equal(2, data.SelectedCount);
        data.VisibleRows[0].IsSelected = false;                      // leave out "Tuerca"
        await data.PrintAllCommand.ExecuteAsync(null);

        string zpl = _fake.Printer.ZplJobs.Single().Zpl;
        Assert.Contains("Perno", zpl);
        Assert.DoesNotContain("Tuerca", zpl);
        Assert.Contains("the 1 selected records (of 2)", _fake.Confirms.Single());
    }
}
