using System.Text;
using System.Text.Json;
using MiniExcelLibs;
using Rotula.Files;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>The data-merge window's view-model: loading, mapping, navigation, counters, checking, batches and saved settings.</summary>
[Collection("App")]
public class AppDataMergeTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppDataMergeTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
        _main.Printer.SelectedPrinterName = "Test Printer";
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private DataMergeViewModel Data => _main.Data;

    private string Csv(string text, string name = "data.csv")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private void AddText(string template) => _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = template }));

    private static async Task Idle(DataMergeViewModel d)
    {
        for (int i = 0; i < 300 && d.IsBusy; i++) await Task.Delay(10);
        Assert.False(d.IsBusy);
    }

    // ------------------------------------------------------------------------------------ loading

    [Fact]
    public async Task LoadingASemicolonCsv_ShowsTheDetectedDelimiter_Columns_AndRecordCount()
    {
        AddText("{{sku}}");
        string path = Csv("sku;qty\nA1;5\nB2;7\n");
        Assert.True(await Data.LoadAsync(path));
        Assert.True(Data.HasData);
        Assert.Equal(new[] { "sku", "qty" }, Data.PreviewColumns);
        Assert.Equal(2, Data.RecordCount);
        Assert.Contains("semicolon", Data.DelimiterText);
        Assert.Equal(path, Data.SourcePath);
        Assert.Contains("2 records", Data.TableSummary);
        Assert.Equal("Record 1 of 2", Data.RecordText);
    }

    [Fact]
    public async Task AnExplicitDelimiter_OverridesDetection()
    {
        string path = Csv("a,b;c\n1,2;3\n");
        await Data.LoadAsync(path);
        Assert.Equal(2, Data.PreviewColumns.Count); // detection picks the comma
        Data.SelectedDelimiter = "semicolon";
        await Idle(Data);
        Assert.Equal(new[] { "a,b", "c" }, Data.PreviewColumns);
        Assert.Contains("Delimiter used", Data.DelimiterText);
    }

    [Fact]
    public async Task WithoutAHeaderRow_ColumnsAreNumbered()
    {
        string path = Csv("A1,5\nB2,7\n");
        await Data.LoadAsync(path);
        Data.HasHeader = false;
        await Idle(Data);
        Assert.Equal(new[] { "Column1", "Column2" }, Data.PreviewColumns);
        Assert.Equal(2, Data.RecordCount);
    }

    [Fact]
    public async Task AWindows1252Csv_IsReadWithItsAccents()
    {
        string path = Path.Combine(_dir, "ansi.csv");
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(path, Encoding.GetEncoding(1252).GetBytes("nombre\nJosé Muñoz\n"));
        await Data.LoadAsync(path);
        Assert.Equal("José Muñoz", Data.Table!.Rows[0][0]);
    }

    [Fact]
    public async Task AnXlsx_ListsItsSheets_AndTheSheetPickerReloads()
    {
        string path = Path.Combine(_dir, "book.xlsx");
        var sheets = new Dictionary<string, object>
        {
            ["Stock"] = new[] { new Dictionary<string, object> { ["sku"] = "A1" } },
            ["Orders"] = new[] { new Dictionary<string, object> { ["order"] = "O9" }, new Dictionary<string, object> { ["order"] = "O10" } },
        };
        MiniExcel.SaveAs(path, sheets);
        Assert.True(await Data.LoadAsync(path));
        Assert.True(Data.IsXlsx);
        Assert.Equal(new[] { "Stock", "Orders" }, Data.Sheets);
        Assert.Equal("Stock", Data.SelectedSheet);
        Assert.Equal(1, Data.RecordCount);

        Data.SelectedSheet = "Orders";
        await Idle(Data);
        Assert.Equal(new[] { "order" }, Data.PreviewColumns);
        Assert.Equal(2, Data.RecordCount);
    }

    [Fact]
    public async Task AFileOpenElsewhere_ExplainsToCloseIt_AndKeepsThePreviousData()
    {
        await Data.LoadAsync(Csv("a\n1\n2\n", "good.csv"));
        string locked = Csv("a\n9\n", "locked.csv");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await Data.LoadAsync(locked));
        }
        Assert.True(Data.ResultIsError);
        Assert.Contains("close it", Data.ResultText);
        Assert.Equal(2, Data.RecordCount);
        Assert.EndsWith("good.csv", Data.SourcePath);
    }

    [Fact]
    public async Task ACorruptWorkbook_IsReportedPlainly_NotAsACrash()
    {
        string path = Path.Combine(_dir, "bad.xlsx");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        Assert.False(await Data.LoadAsync(path));
        Assert.Contains("could not be read", Data.ResultText);
        Assert.False(Data.HasData);
    }

    [Fact]
    public async Task AMissingFile_IsReportedPlainly()
    {
        Assert.False(await Data.LoadAsync(Path.Combine(_dir, "nope.csv")));
        Assert.True(Data.ResultIsError);
        Assert.False(Data.HasData);
    }

    [Fact]
    public async Task AnUnclosedQuote_ShowsAWarningButStillLoads()
    {
        Assert.True(await Data.LoadAsync(Csv("a,b\n\"oops,1\n")));
        Assert.True(Data.HasLoadWarnings);
        Assert.Contains("quote", Data.LoadWarnings[0]);
    }

    [Fact]
    public async Task AnEmptyFile_SaysThereAreNoColumns()
    {
        Assert.False(await Data.LoadAsync(Csv("")));
        Assert.Contains("no columns", Data.ResultText);
    }

    // ------------------------------------------------------------------------------------ mapping

    [Fact]
    public async Task Mapping_IsAutomaticByName_AndWarnsAboutUnmappedAndUnusedInPlainLanguage()
    {
        AddText("{{sku}} {{missing}}");
        await Data.LoadAsync(Csv("sku,spare\nA1,x\n"));
        var rows = Data.MappingRows.ToDictionary(r => r.Placeholder);
        Assert.False(rows["sku"].IsUnmapped);
        Assert.Contains("sku", rows["sku"].Status);
        Assert.True(rows["missing"].IsUnmapped);
        Assert.Contains(Data.MappingWarnings, w => w.Contains("missing") && w.Contains("no matching column"));
        Assert.Contains(Data.MappingWarnings, w => w.Contains("spare") && w.Contains("not used"));

        Loc.Instance.SetLanguage("es");
        Assert.Contains(Data.MappingWarnings, w => w.Contains("missing") && w.Contains("columna"));
    }

    [Fact]
    public async Task Mapping_AnOverrideFeedsThePlaceholderFromAnotherColumn_AndClearsTheWarning()
    {
        AddText("{{sku}}");
        await Data.LoadAsync(Csv("code,qty\nA1,5\n"));
        var row = Data.MappingRows.Single(r => r.Placeholder == "sku");
        Assert.True(row.IsUnmapped);
        bool edited = false;
        Data.SettingsEdited += (_, _) => edited = true;
        row.Selected = row.Choices.Single(c => c.Column == "code");
        Assert.True(edited);
        Assert.False(row.IsUnmapped);
        Assert.Equal("A1", Data.CurrentData!["sku"]);
        Assert.DoesNotContain(Data.MappingWarnings, w => w.Contains("no matching column"));
        Assert.Equal("code", Data.ToSettings().ColumnOverrides["sku"]);
    }

    [Fact]
    public async Task Mapping_FollowsTheLabelWhenAPlaceholderIsAdded()
    {
        await Data.LoadAsync(Csv("name\nAnna\n"));
        Assert.Empty(Data.MappingRows);
        Assert.True(Data.LabelHasNoPlaceholders);
        AddText("Hi {{name}}");
        Assert.Single(Data.MappingRows);
        Assert.Contains("Hi Anna", _main.ZplText);
    }

    // ------------------------------------------------------------------------------------ navigator and preview

    [Fact]
    public async Task Navigator_StaysInRange_AndRaisesPreviewChanges()
    {
        AddText("{{name}}");
        await Data.LoadAsync(Csv("name\nAnna\nBen\nCy\n"));
        int changes = 0;
        Data.PreviewDataChanged += (_, _) => changes++;

        Assert.False(Data.PreviousRecordCommand.CanExecute(null));
        Data.NextRecordCommand.Execute(null);
        Assert.Equal(1, Data.CurrentIndex);
        Data.LastRecordCommand.Execute(null);
        Assert.Equal(2, Data.CurrentIndex);
        Assert.False(Data.NextRecordCommand.CanExecute(null));
        Data.NextRecordCommand.Execute(null);
        Assert.Equal(2, Data.CurrentIndex);
        Data.FirstRecordCommand.Execute(null);
        Assert.Equal(0, Data.CurrentIndex);
        Data.PreviousRecordCommand.Execute(null);
        Assert.Equal(0, Data.CurrentIndex);
        Assert.True(changes >= 3);
        Assert.Equal("Record 1 of 3", Data.RecordText);
    }

    [Fact]
    public async Task TheCanvasPreviewAndTheZplView_ShowTheChosenRecord()
    {
        AddText("Name {{name}}");
        await Data.LoadAsync(Csv("name\nAnna\nBen\n"));
        Assert.Contains("Name Anna", _main.ZplText);
        Assert.Equal("Anna", _main.Preview.Data!["name"]);
        long revision = _main.Session.Revision;

        Data.NextRecordCommand.Execute(null);
        Assert.Contains("Name Ben", _main.ZplText);
        Assert.DoesNotContain("Name Anna", _main.ZplText);
        Assert.True(_main.Session.Revision > revision); // the cached bitmap is invalidated
        Assert.Equal("Record 2 of 2", _main.DataRecordText);

        Data.PreviewEnabled = false;
        Assert.Null(_main.Preview.Data);
        Assert.DoesNotContain("Name Ben", _main.ZplText);
    }

    [Fact]
    public async Task TheToolbarNavigationCommands_DriveTheSameRecord()
    {
        AddText("{{name}}");
        Assert.False(_main.NextRecordCommand.CanExecute(null));
        await Data.LoadAsync(Csv("name\nAnna\nBen\n"));
        Assert.True(_main.NextRecordCommand.CanExecute(null));
        _main.NextRecordCommand.Execute(null);
        Assert.Equal(1, Data.CurrentIndex);
        _main.FirstRecordCommand.Execute(null);
        Assert.Equal(0, Data.CurrentIndex);
        _main.LastRecordCommand.Execute(null);
        Assert.Equal(1, Data.CurrentIndex);
    }

    [Fact]
    public async Task GoTo_AcceptsAValidNumber_AndExplainsAnInvalidOne()
    {
        await Data.LoadAsync(Csv("n\n1\n2\n3\n"));
        Data.GoToText = "3";
        Data.GoToRecordCommand.Execute(null);
        Assert.Equal(2, Data.CurrentIndex);
        Data.GoToText = "9";
        Data.GoToRecordCommand.Execute(null);
        Assert.True(Data.ResultIsError);
        Assert.Contains("1 to 3", Data.ResultText);
        Assert.Equal(2, Data.CurrentIndex);
    }

    [Fact]
    public async Task ReplacingTheDataWithAShorterFile_ClampsTheRecordPointer()
    {
        await Data.LoadAsync(Csv("n\n1\n2\n3\n", "a.csv"));
        Data.LastRecordCommand.Execute(null);
        await Data.LoadAsync(Csv("n\n1\n", "b.csv"));
        Assert.Equal(0, Data.CurrentIndex);
    }

    // ------------------------------------------------------------------------------------ counters

    [Fact]
    public async Task ACounter_FillsThePlaceholder_PerRecord_WithPaddingPrefixAndSuffix()
    {
        AddText("{{#Serial}}");
        await Data.LoadAsync(Csv("n\n1\n2\n3\n"));
        Data.AddCounterCommand.Execute(null);
        var c = Data.SelectedCounter!;
        Assert.Equal("Serial", c.Name);
        Assert.Equal("{{#Serial}}", c.Placeholder);
        c.PaddingText = "4";
        c.Prefix = "SN-";
        Assert.Equal("SN-0001", Data.CurrentData!["#Serial"]);
        Data.NextRecordCommand.Execute(null);
        Assert.Equal("SN-0002", Data.CurrentData!["#Serial"]);
        Assert.Contains("SN-0001, SN-0002, SN-0003", c.Example);
        Assert.Contains("counter", Data.MappingRows.Single().Status);
    }

    [Fact]
    public async Task AnAlphanumericCounter_CountsLettersAndDigits()
    {
        AddText("{{#Lot}}");
        await Data.LoadAsync(Csv("n\n1\n2\n"));
        Data.AddCounterCommand.Execute(null);
        var c = Data.SelectedCounter!;
        c.Name = "Lot";
        c.IsAlphanumeric = true;
        c.StartText = "A9";
        Assert.Equal("A9", Data.CurrentData!["#Lot"]);
        Data.NextRecordCommand.Execute(null);
        Assert.Equal("B0", Data.CurrentData!["#Lot"]);
    }

    [Theory]
    [InlineData("name", "", "1", "0", "counter name")]
    [InlineData("Serial", "abc", "1", "0", "start value")]
    [InlineData("Serial", "1", "x", "0", "step")]
    [InlineData("Serial", "1", "1", "40", "zero padding")]
    public async Task AnInvalidCounter_ExplainsWhatToChange_AndIsNotUsed(string name, string start, string step, string padding, string expected)
    {
        await Data.LoadAsync(Csv("n\n1\n"));
        Data.AddCounterCommand.Execute(null);
        var c = Data.SelectedCounter!;
        c.Name = name == "name" ? "bad name!" : name;
        c.StartText = start;
        c.StepText = step;
        c.PaddingText = padding;
        Assert.True(c.HasError);
        Assert.Contains(expected, c.Error);
        Assert.False(Data.CurrentData!.ContainsKey("#" + c.Name));
    }

    [Fact]
    public async Task AUsedCounterThatRunsOut_WithErrorOverflow_MarksTheRecordsInTheCheck()
    {
        AddText("{{#Serial}}");
        await Data.LoadAsync(Csv("n\n1\n2\n3\n"));
        Data.AddCounterCommand.Execute(null);
        var c = Data.SelectedCounter!;
        c.PaddingText = "1"; // only 0-9
        c.StartText = "8";
        c.StopOnOverflow = true;
        await Data.CheckAllCommand.ExecuteAsync(null);
        Assert.Contains(Data.Issues, i => i.IsError && i.RecordNumber == 3 && i.Message.Contains("Serial"));
    }

    // ------------------------------------------------------------------------------------ checking

    private void AddEanLabel() => _main.Session.Edit("Undo.Property", d => d.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "{{code}}" }));

    [Fact]
    public async Task CheckAll_ListsPerRecordProblems_InReadableSentences()
    {
        AddEanLabel();
        await Data.LoadAsync(Csv("code\n5901234123457\n123\n5901234123457\n"));
        await Data.CheckAllCommand.ExecuteAsync(null);
        Assert.True(Data.HasIssues);
        var issue = Assert.Single(Data.Issues);
        Assert.Equal(2, issue.RecordNumber);
        Assert.True(issue.IsError);
        Assert.Contains("EAN-13", issue.Message);
        Assert.Contains("Checked 3 records: 2 ready, 1 with errors", Data.IssuesSummary);
        Assert.True(Data.ResultIsError);
        Assert.Equal("Error", issue.SeverityText);
    }

    [Fact]
    public async Task CheckAll_WithCleanData_SaysEverythingIsReady()
    {
        AddEanLabel();
        await Data.LoadAsync(Csv("code\n5901234123457\n"));
        await Data.CheckAllCommand.ExecuteAsync(null);
        Assert.False(Data.ResultIsError);
        Assert.Contains("ready to print", Data.ResultText);
    }

    [Fact]
    public async Task CheckAll_WithoutData_AsksToOpenAFile()
    {
        await Data.CheckAllCommand.ExecuteAsync(null);
        Assert.Contains("Open a data file", Data.ResultText);
    }

    // ------------------------------------------------------------------------------------ print all

    private async Task Ready(int records, int maxPerJob)
    {
        AddText("Item {{n}}");
        var sb = new StringBuilder("n\n");
        for (int i = 1; i <= records; i++) sb.Append(i).Append('\n');
        await Data.LoadAsync(Csv(sb.ToString()));
        Data.MaxRecordsText = maxPerJob.ToString();
    }

    [Fact]
    public async Task PrintAll_PassesTheByteExactRawBlocksToEveryJob()
    {
        await Ready(3, 1);
        const string raw = "^FO10,10^FDéÿ^FS"; // Latin-1 chars above 0x7F: stored bytes, not text
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new RawZplElement { Zpl = raw }));
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(3, _fake.Printer.ZplJobs.Count);
        Assert.All(_fake.Printer.RawBlockArgs, blocks => Assert.Equal(new[] { raw }, blocks));
    }

    [Fact]
    public void ZplFiles_WritesRawBlocksAsLatin1_AndTheRestWithTheDocumentEncoding()
    {
        const string raw = "^FDé^FS";
        byte[]? bytes = ZplFiles.TryEncode("^XAñ" + raw + "^XZ", ZplEncoding.Utf8, new[] { raw });
        Assert.NotNull(bytes);
        // The text outside the block is UTF-8 (two bytes for the n with tilde); the block keeps one byte per char.
        Assert.Equal(new byte[] { 0xC3, 0xB1 }, bytes!.Skip(3).Take(2).ToArray());
        Assert.Equal(0xE9, bytes[bytes.Length - 7]);
    }

    [Fact]
    public async Task PrintAll_SendsJobsOfTheChosenSize_AfterConfirming()
    {
        await Ready(5, 2);
        Data.Printer.DarknessText = "18";
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Single(_fake.Confirms);
        Assert.Contains("5 records", _fake.Confirms[0]);
        Assert.Equal(3, _fake.Printer.ZplJobs.Count);
        Assert.StartsWith("~SD18", _fake.Printer.ZplJobs[0].Zpl);                  // printer setup goes out once, first
        Assert.DoesNotContain("~SD18", _fake.Printer.ZplJobs[1].Zpl);
        Assert.Equal(new[] { 2, 2, 1 }, _fake.Printer.ZplJobs.Select(j => System.Text.RegularExpressions.Regex.Matches(j.Zpl, @"\^XA").Count - (j == _fake.Printer.ZplJobs[0] ? 1 : 0)));
        Assert.All(_fake.Printer.ZplJobs, j => Assert.Equal("Test Printer", j.Target.PrinterName));
        Assert.Contains("Item 5", _fake.Printer.ZplJobs[2].Zpl);
        Assert.False(Data.ResultIsError);
        Assert.Contains("5 labels", Data.ResultText);
        Assert.Contains("3 job", Data.ResultText);
        Assert.True(_fake.Settings.Current.Print.Configured);
    }

    [Fact]
    public async Task PrintAll_WithASingleJobSetting_SendsOneJob()
    {
        await Ready(4, 0);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.ZplJobs);
    }

    [Fact]
    public async Task PrintAll_WhenTheUserDeclines_SendsNothing()
    {
        await Ready(3, 0);
        _fake.ConfirmAnswer = false;
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.ZplJobs);
    }

    [Fact]
    public async Task PrintAll_WhenAJobFails_StopsAndSaysHowManyWereAlreadySent()
    {
        await Ready(6, 2);
        _fake.Printer.Result = n => n == 2 ? PrintResult.Fail(PrintErrorKind.Offline, "Test Printer") : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Equal(2, _fake.Printer.ZplJobs.Count);
        Assert.True(Data.ResultIsError);
        Assert.Contains("offline", Data.ResultText);
        Assert.Contains("1 job(s) were already sent", Data.ResultText);
    }

    [Fact]
    public async Task PrintAll_CanBeStopped_AndStopsWithoutSendingMore()
    {
        await Ready(4, 2);
        _fake.Printer.Gate = new TaskCompletionSource();
        var running = Data.PrintAllCommand.ExecuteAsync(null);
        for (int i = 0; i < 300 && _fake.Printer.ZplJobs.Count == 0; i++) await Task.Delay(10);
        Assert.True(Data.IsBusy);
        Data.CancelOperationCommand.Execute(null);
        await running;
        Assert.False(Data.IsBusy);
        Assert.Single(_fake.Printer.ZplJobs);
        Assert.False(Data.ResultIsError);
    }

    [Fact]
    public async Task PrintAll_WithoutAPrinter_AsksForOne()
    {
        await Ready(2, 0);
        Data.Printer.SelectedPrinterName = null;
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("No printer is selected", Data.ResultText);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Confirms);
    }

    [Fact]
    public async Task PrintAll_SkipsInvalidRecords_AndReportsThem()
    {
        AddEanLabel();
        await Data.LoadAsync(Csv("code\n5901234123457\n123\n5901234123457\n"));
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Contains("1 record(s) were left out", Data.ResultText);
        Assert.True(Data.ResultIsError);
        Assert.Single(Data.Issues);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(_fake.Printer.ZplJobs[0].Zpl, @"\^BE").Count);
    }

    [Fact]
    public async Task PrintAll_WithTheStopPolicy_StopsAtTheFirstInvalidRecord()
    {
        AddEanLabel();
        await Data.LoadAsync(Csv("code\n5901234123457\n123\n5901234123457\n"));
        Data.InvalidPolicy = InvalidRecordPolicy.Stop;
        await Data.PrintAllCommand.ExecuteAsync(null);
                Assert.Contains("stopped at record 2", Data.ResultText);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(_fake.Printer.ZplJobs[0].Zpl, @"\^BE"));
    }

    [Fact]
    public async Task EditingTheLabelWhileABatchRuns_DoesNotChangeTheBatch()
    {
        await Ready(2, 1);
        _fake.Printer.Gate = new TaskCompletionSource();
        var running = Data.PrintAllCommand.ExecuteAsync(null);
        for (int i = 0; i < 300 && _fake.Printer.ZplJobs.Count == 0; i++) await Task.Delay(10);
        _main.Session.Edit("Undo.Property", d => ((TextElement)d.Elements[0]).Text = "CHANGED {{n}}");
        _fake.Printer.Gate.SetResult();
        await running;
        Assert.All(_fake.Printer.ZplJobs, j => Assert.DoesNotContain("CHANGED", j.Zpl));
    }

    // ------------------------------------------------------------------------------------ export

    [Fact]
    public async Task ExportBatch_WritesOneFileWithEveryLabel_AndTheCopiesSetting()
    {
        await Ready(3, 0);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "2";
        string path = Path.Combine(_dir, "batch.zpl");
        _fake.ExportPath = path;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        string zpl = File.ReadAllText(path);
        Assert.Contains("Item 1", zpl);
        Assert.Contains("Item 3", zpl);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(zpl, @"\^PQ2").Count);
        Assert.Contains("3 labels", Data.ResultText);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ExportBatch_RepeatMode_WritesTheLabelOncePerCopy()
    {
        await Ready(1, 0);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "3";
        Data.CopiesMode = CopiesMode.RepeatBlock;
        string path = Path.Combine(_dir, "rep.zpl");
        _fake.ExportPath = path;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"\^XA").Count);
    }

    [Fact]
    public async Task ExportBatch_CopiesFromAColumn()
    {
        AddText("{{n}}");
        await Data.LoadAsync(Csv("n,qty\nA,2\nB,5\n"));
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "qty";
        string path = Path.Combine(_dir, "col.zpl");
        _fake.ExportPath = path;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        string zpl = File.ReadAllText(path);
        Assert.Contains("^PQ2", zpl);
        Assert.Contains("^PQ5", zpl);
    }

    [Fact]
    public async Task ExportBatch_OneFilePerJob_WritesNumberedFiles()
    {
        await Ready(5, 2);
        Data.OneFilePerJob = true;
        _fake.ExportPath = Path.Combine(_dir, "batch.zpl");
        await Data.ExportBatchCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_dir, "batch_001.zpl")));
        Assert.True(File.Exists(Path.Combine(_dir, "batch_002.zpl")));
        Assert.True(File.Exists(Path.Combine(_dir, "batch_003.zpl")));
        Assert.False(File.Exists(Path.Combine(_dir, "batch_004.zpl")));
        Assert.Contains("Item 5", File.ReadAllText(Path.Combine(_dir, "batch_003.zpl")));
        Assert.Contains("3 files", Data.ResultText);
    }

    [Fact]
    public async Task ExportBatch_OneFilePerJob_AsksBeforeReplacingFiles()
    {
        await Ready(4, 2);
        Data.OneFilePerJob = true;
        File.WriteAllText(Path.Combine(_dir, "batch_001.zpl"), "OLD");
        _fake.ExportPath = Path.Combine(_dir, "batch.zpl");
        _fake.ConfirmAnswer = false;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_dir, "batch_001.zpl")));
        Assert.Single(_fake.Confirms);
        Assert.False(File.Exists(Path.Combine(_dir, "batch_002.zpl")));
    }

    [Fact]
    public async Task ExportBatch_ToAnUnwritableFolder_SaysSo_AndLeavesNoTempFile()
    {
        await Ready(2, 0);
        _fake.ExportPath = Path.Combine(_dir, "missing", "x.zpl");
        await Data.ExportBatchCommand.ExecuteAsync(null);
        Assert.True(Data.ResultIsError);
        Assert.Contains("could not be written", Data.ResultText);
    }

    [Fact]
    public async Task ExportBatch_InALegacyCodePage_WritesThoseBytes()
    {
        _main.Session.Edit("Undo.Property", d =>
        {
            d.Encoding = Rotula.Core.Zpl.ZplEncoding.Cp1252;
            d.Elements.Add(new TextElement { Text = "{{n}}" });
        });
        await Data.LoadAsync(Csv("n\nCafé\n"));
        string path = Path.Combine(_dir, "ansi.zpl");
        _fake.ExportPath = path;
        await Data.ExportBatchCommand.ExecuteAsync(null);
        // The generator writes non-ASCII text as ^FH escapes (_E9 = the single Windows-1252 byte of é), so the file is plain ASCII
        // and cannot be mangled by the code page; what matters is that the escape is the one-byte form, not UTF-8's _C3_A9.
        string zpl = File.ReadAllText(path);
        Assert.Contains("Caf_E9", zpl);
        Assert.DoesNotContain("_C3", zpl);
        Assert.Contains("^CI27", zpl);
    }

    // ------------------------------------------------------------------------------------ saved settings

    [Fact]
    public async Task Settings_AreSavedInTheRotulaFile_ButTheDataIsNot()
    {
        AddText("{{name}}");
        string csv = Csv("name,qty\nSecretPerson,3\n");
        await Data.LoadAsync(csv);
        Data.AddCounterCommand.Execute(null);
        Data.SelectedCounter!.PrefixSet("L-");
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "qty";
        Data.CopiesMode = CopiesMode.RepeatBlock;
        Data.InvalidPolicy = InvalidRecordPolicy.Stop;
        Data.MaxRecordsText = "250";
        Data.OneFilePerJob = true;

        string file = Path.Combine(_dir, "label.rotula");
        _fake.SavePath = file;
        _main.SaveCommand.Execute(null);
        string json = File.ReadAllText(file);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("formatVersion").GetInt32());
            var merge = doc.RootElement.GetProperty("merge");
            Assert.Equal(csv, merge.GetProperty("sourcePath").GetString());
            Assert.Equal("qty", merge.GetProperty("copiesColumn").GetString());
        }
        Assert.DoesNotContain("SecretPerson", json.Replace("SecretPerson}}", string.Empty));
        Assert.DoesNotContain("\"rows\"", json);

        // Reopen in a fresh session: the settings come back, the data does not.
        var again = new MainViewModel(_fake.Build());
        Assert.True(again.LoadFile(file));
        var d = again.Data;
        Assert.False(d.HasData);
        Assert.Equal(csv, d.SourcePath);
        Assert.True(d.ShowReloadHint);
        Assert.Equal(CopiesSource.Column, d.CopiesSource);
        Assert.Equal("qty", d.CopiesColumn);
        Assert.Equal(CopiesMode.RepeatBlock, d.CopiesMode);
        Assert.Equal(InvalidRecordPolicy.Stop, d.InvalidPolicy);
        Assert.Equal("250", d.MaxRecordsText);
        Assert.True(d.OneFilePerJob);
        Assert.Equal("L-", d.Counters.Single().Prefix);
        Assert.False(again.Session.IsDirty); // loading settings is not an edit
    }

    [Fact]
    public async Task EditingDataSettings_MarksTheLabelAsChanged_SoClosingOffersToSave()
    {
        _fake.SavePath = Path.Combine(_dir, "x.rotula");
        _main.SaveCommand.Execute(null);
        Assert.False(_main.Session.IsDirty);
        Data.MaxRecordsText = "10";
        Assert.True(_main.Session.IsDirty);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ALabelWithoutDataSettings_IsStillSavedAsVersion1()
    {
        _fake.SavePath = Path.Combine(_dir, "plain.rotula");
        _main.SaveCommand.Execute(null);
        using var doc = JsonDocument.Parse(File.ReadAllText(_fake.SavePath));
        Assert.Equal(1, doc.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("merge", out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AVersion1File_StillOpens_WithDefaultDataSettings()
    {
        string file = Path.Combine(_dir, "old.rotula");
        File.WriteAllText(file, RotulaFileSerializer.Serialize(AppTestSupport.RichDocument()));
        Assert.True(_main.LoadFile(file));
        Assert.False(Data.HasData);
        Assert.Null(Data.SourcePath);
        Assert.Equal(CopiesSource.LabelDefault, Data.CopiesSource);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OpeningAnotherLabel_ForgetsTheLoadedData()
    {
        await Data.LoadAsync(Csv("n\n1\n"));
        _fake.NewLabel = new LabelDocument();
        _main.NewCommand.Execute(null);
        Assert.False(Data.HasData);
        Assert.Null(_main.Preview.Data);
    }

    [Fact]
    public async Task OpeningALabel_ReadsTheRememberedFileByItself()
    {
        string csv = Csv("n\n1\n2\n");
        await Data.LoadAsync(csv);
        _fake.SavePath = Path.Combine(_dir, "r.rotula");
        _main.SaveCommand.Execute(null);
        var again = new MainViewModel(_fake.Build());
        again.LoadFile(_fake.SavePath);
        await again.DataReload;
        Assert.True(again.Data.HasData);
        Assert.Equal(2, again.Data.RecordCount);
    }

    [Fact]
    public async Task Reload_OfAMissingFile_ExplainsInsteadOfFailing()
    {
        string csv = Csv("n\n1\n");
        await Data.LoadAsync(csv);
        _fake.SavePath = Path.Combine(_dir, "m.rotula");
        _main.SaveCommand.Execute(null);
        File.Delete(csv);
        var again = new MainViewModel(_fake.Build());
        again.LoadFile(_fake.SavePath);
        await again.Data.ReloadCommand.ExecuteAsync(null);
        Assert.Contains("was not found", again.Data.ResultText);
    }

    [Fact]
    public void TheDataWindowCommand_OpensTheWindow_AndOpenDataFile_LoadsAfterPicking()
    {
        _main.ShowDataCommand.Execute(null);
        Assert.Same(_main.Data, _fake.ToolDialogs.LastData);
    }

    [Fact]
    public async Task OpenDataFile_FromTheMenu_ShowsTheWindowAndLoads()
    {
        _fake.DataPath = Csv("n\n1\n2\n3\n");
        await _main.OpenDataFileCommand.ExecuteAsync(null);
        Assert.Same(_main.Data, _fake.ToolDialogs.LastData);
        Assert.Equal(3, _main.Data.RecordCount);
    }

    [Fact]
    public async Task OpenDataFile_Cancelled_DoesNothing()
    {
        _fake.DataPath = null;
        await _main.OpenDataFileCommand.ExecuteAsync(null);
        Assert.Null(_fake.ToolDialogs.LastData);
    }

    [Fact]
    public async Task TheDataTextsAreTranslated()
    {
        await Data.LoadAsync(Csv("n\n1\n2\n"));
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Registro 1 de 2", Data.RecordText);
        Assert.Contains("2 registros", Data.TableSummary);
        Assert.Contains("punto y coma", Loc.Get("Data.Delimiter.Semicolon"));
    }
}

internal static class CounterEditorTestExtensions
{
    /// <summary>Sets the prefix (kept as a method so the test reads as an action).</summary>
    public static void PrefixSet(this CounterEditor editor, string prefix) => editor.Prefix = prefix;
}
