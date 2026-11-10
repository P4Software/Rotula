using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// Batch printing (the Data merge window's print button and the Print dialog's ticked records) to a printer in Windows-driver (picture) mode:
/// one pre-flight for the design, one acknowledgement, every job drawn as pictures through <see cref="IPrintService.PrintResolvedAsync"/> and
/// never sent as ZPL, and the serial-number rules of the printer-language path. Every serial scenario runs in both modes, so the printer-language
/// batch is proved unchanged next to the picture batch. Nothing here reaches a real printer: the service is a fake, or the real print service wired
/// to a fake spooler and a fake driver.
/// </summary>
[Collection("App")]
public class AppDriverBatchTests : IDisposable
{
    private const string Queue = "Brother QL-820NWB";
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MemoryCounterStore _store = new();
    private readonly MainViewModel _main;

    /// <summary>What the fake driver reports when a test needs media: a 4 x 6 inch page at 203 dpi, printable to its edges.</summary>
    private static readonly DriverMedia Media = new(203, 203, 812, 1218, 812, 1218, 0, 0, true);

    public AppDriverBatchTests()
    {
        _fake.Settings.Current.Language = "en";
        _fake.Printer.Printers = new[] { new PrinterInfo(Queue, "USB001", "Brother QL-820NWB", true, false) };
        _fake.Variables = new SerialVariables(_store);
        _main = new MainViewModel(_fake.Build());
        _main.Printer.SelectedPrinterName = Queue;
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    public static TheoryData<PrinterOutputMode> BothModes => new() { PrinterOutputMode.PrinterLanguage, PrinterOutputMode.WindowsDriver };

    private DataMergeViewModel Data => _main.Data;

    private string CounterKeyOfLabel => CounterKey.For(_main.Session.Document.Id!, "serial");

    /// <summary>How many serial numbers are used for good (the next one to hand out), or 0 when the series was never touched.</summary>
    private long Used => _store.Get(CounterKeyOfLabel)?.Next ?? 0;

    private static ValidationIssue Warning(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    private static ValidationIssue Error(string key, params string[] args) => new(ValidationSeverity.Error, key, null, args);

    /// <summary>Makes the fake pre-flight answer these findings for every document.</summary>
    private void Findings(params ValidationIssue[] findings) => _fake.Printer.Preflight = (_, _) => new DriverPreflightResult(findings, Media);

    private static string Fingerprint(params ValidationIssue[] findings) => new DriverPreflightResult(findings, null).Fingerprint;

    /// <summary>
    /// A label with a remembered serial counter ("SN0001"...) and the record's n and code, <paramref name="records"/> data rows (n = 1..records,
    /// code = "C" + n unless <paramref name="codes"/> says otherwise, c = n % 3 + 1 unless <paramref name="copies"/> says otherwise), jobs of
    /// <paramref name="perJob"/> records, and the printer in <paramref name="mode"/> (null leaves the printer's mode question unanswered).
    /// </summary>
    private async Task<DataMergeViewModel> Batch(PrinterOutputMode? mode, int records, int perJob, string[]? codes = null, string[]? copies = null)
    {
        _main.Session.Edit("Undo.Property", d =>
        {
            d.EnsureId();
            d.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
            d.Elements.Add(new TextElement { Text = "{{serial}} Item {{n}} {{code}}" });
        });
        var sb = new StringBuilder("n,code,c\n");
        for (int i = 1; i <= records; i++)
        {
            string c = copies is null ? (i % 3 + 1).ToString(CultureInfo.InvariantCulture) : copies[i - 1];
            sb.Append(i).Append(',').Append(codes is null ? "C" + i : codes[i - 1]).Append(',').Append(c).Append('\n');
        }
        string path = Path.Combine(_dir, "data.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(await Data.LoadAsync(path));
        Data.MaxRecordsText = perJob.ToString(CultureInfo.InvariantCulture);
        if (mode is { } m) _main.Printer.ChooseModeCommand.Execute(m);
        return Data;
    }

    /// <summary>The serial number of every record that was handed to the printer, in order (one per record, whatever its copies).</summary>
    private string[] SentSerials(PrinterOutputMode mode) => mode == PrinterOutputMode.WindowsDriver
        ? _fake.Printer.Resolved.SelectMany(j => j.Labels).Select(l => l.Values!["serial"]).ToArray()
        : _fake.Printer.ZplJobs.SelectMany(j => Regex.Matches(j.Zpl, @"SN\d{4}").Select(m => m.Value)).ToArray();

    /// <summary>How many print jobs reached the service in <paramref name="mode"/>.</summary>
    private int JobsSent(PrinterOutputMode mode) => mode == PrinterOutputMode.WindowsDriver ? _fake.Printer.Resolved.Count : _fake.Printer.ZplJobs.Count;

    private void ForgetSent()
    {
        _fake.Printer.ZplJobs.Clear();
        _fake.Printer.RawBlockArgs.Clear();
        _fake.Printer.Resolved.Clear();
    }

    /// <summary>Prints again with every job succeeding and returns the first serial number that went out.</summary>
    private async Task<string> FirstSerialOfTheNextBatch(PrinterOutputMode mode)
    {
        ForgetSent();
        _fake.Printer.Result = _ => PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);
        return SentSerials(mode)[0];
    }

    private PrintViewModel Dialog() =>
        new(_fake.Printer, _main.Printer, () => _main.Session.Document, () => Data.CurrentData, _fake.Build().Files, "my-label", _main.Variables, Data);

    // ------------------------------------------------------------------------------------ (b) every job goes out with each record's copies

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task EachJob_GoesOutWithTheCopiesOfEachRecord_InTheChosenMode(PrinterOutputMode mode)
    {
        await Batch(mode, records: 5, perJob: 2);
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "c";
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.False(Data.ResultIsError, Data.ResultText);
        Assert.Contains("5 labels", Data.ResultText);
        Assert.Contains("3 job", Data.ResultText);
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004", "SN0005" }, SentSerials(mode));
        Assert.Equal(5, Used);
        if (mode == PrinterOutputMode.WindowsDriver)
        {
            Assert.Empty(_fake.Printer.ZplJobs);
            Assert.Equal(3, _fake.Printer.Resolved.Count);
            Assert.Equal(new[] { 2, 2, 1 }, _fake.Printer.Resolved.Select(j => j.Labels.Count));
            // c = n % 3 + 1 for n = 1..5: each record's label goes out with its own copies.
            Assert.Equal(new[] { 2, 3, 1, 2, 3 }, _fake.Printer.Resolved.SelectMany(j => j.Labels).Select(l => l.Copies));
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, _fake.Printer.Resolved.SelectMany(j => j.Labels).Select(l => l.Values!["n"]));
            Assert.All(_fake.Printer.Resolved, j => Assert.Equal(PrinterOutputMode.WindowsDriver, j.Target.Mode));
            // Checked once for the design, not per job or per record, and every job prints the very document that was checked.
            var check = Assert.Single(_fake.Printer.Preflights);
            Assert.All(_fake.Printer.Resolved, j => Assert.Same(check.Doc, j.Doc));
        }
        else
        {
            Assert.Empty(_fake.Printer.Resolved);
            Assert.Empty(_fake.Printer.Preflights);
            Assert.Equal(3, _fake.Printer.ZplJobs.Count);
            string zpl = string.Concat(_fake.Printer.ZplJobs.Select(j => j.Zpl));
            Assert.Equal(new[] { "2", "3", "1", "2", "3" }, Regex.Matches(zpl, @"\^PQ(\d+),").Select(m => m.Groups[1].Value));
        }
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task PrinterCommands_AndStatusChecks_StayInThePrinterLanguageMode(PrinterOutputMode mode)
    {
        await Batch(mode, records: 3, perJob: 2);
        Data.Printer.DarknessText = "18";
        Data.Printer.CheckStatus = true;
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.False(Data.ResultIsError, Data.ResultText);
        if (mode == PrinterOutputMode.WindowsDriver)
        {
            // A printer that does not understand ZPL would print the set-up commands and ~HS as text: nothing of the kind goes out.
            Assert.Empty(_fake.Printer.ZplJobs);
            Assert.Empty(_fake.Printer.StatusQueries);
            Assert.Empty(_fake.Printer.Verifications);
            Assert.All(_fake.Printer.Resolved, j => Assert.False(j.Target.Status.Enabled));
        }
        else
        {
            Assert.StartsWith("~SD18", _fake.Printer.ZplJobs[0].Zpl);
            Assert.Single(_fake.Printer.Verifications);
        }
    }

    // ------------------------------------------------------------------------------------ (c) a failed job that may have printed part of its labels

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task AFailedJob_ThatMayHavePrinted_KeepsItsNumbers_AndStops(PrinterOutputMode mode)
    {
        await Batch(mode, records: 6, perJob: 2);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "2";
        // The second job breaks after part of it may have reached the printer. ZPL: bytes went out. Picture: 3 pages went out, which is both
        // copies of record 3 and the first copy of record 4, so record 4 counts as printed too (a record cut short is the safe side).
        var broken = mode == PrinterOutputMode.WindowsDriver
            ? PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage (Win32 error 1)"), 10).WithPages(3)
            : PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, Queue), 10);
        _fake.Printer.Result = n => n == 2 ? broken : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(2, JobsSent(mode));                    // the third job is never sent
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004" }, SentSerials(mode));
        Assert.True(Data.ResultIsError);
        Assert.Contains("1 job(s) were already sent", Data.ResultText);
        Assert.Equal(4, Used);
        // Record 4 had one of its two copies out: the text says it printed only in part, never that it printed.
        if (mode == PrinterOutputMode.WindowsDriver)
        {
            Assert.Contains(Loc.Get("Data.Driver.PrintedInPart", 4, 1, 2), Data.ResultText);
            Assert.DoesNotContain(Loc.Get("Data.Driver.PrintedThrough", 4), Data.ResultText);
        }

        Assert.Equal("SN0005", await FirstSerialOfTheNextBatch(mode));
    }

    [Fact]
    public async Task APictureJobThatStopsWithinARecordsCopies_KeepsOnlyTheRecordsWithAPageOut()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 2);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "2";
        // One page of the second job went out: the first copy of record 3. Record 4 never reached the driver, so its number goes back.
        _fake.Printer.Result = n => n == 2 ? PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "EndPage (Win32 error 5)"), 10).WithPages(1) : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(3, Used);
        Assert.Contains(Loc.Get("Data.Driver.PrintedInPart", 3, 1, 2), Data.ResultText);
        Assert.Equal("SN0004", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Theory]
    [InlineData(4, 2, false, 2)]   // pages 1-2 are record 1, 3-5 record 2: page 4 leaves record 2 with 2 of its 3 copies out
    [InlineData(6, 3, true, 0)]    // 2 + 3 + 1 pages: records 1 to 3 are out in full
    public async Task DifferentCopiesPerRecord_WithPartOfAJobOut_KeepTheNumbersOfTheRecordsWithAPageOut(int pagesOut, int used, bool full, int copiesOut)
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 5, perJob: 0);
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "c";   // c = n % 3 + 1: 2, 3, 1, 2, 3 copies
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage (Win32 error 1)"), 10).WithPages(pagesOut);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(used, Used);
        Assert.Contains(full ? Loc.Get("Data.Driver.PrintedThrough", used) : Loc.Get("Data.Driver.PrintedInPart", used, copiesOut, 3), Data.ResultText);
    }

    [Fact]
    public async Task ASkippedRecordInsideAPartlyPrintedPictureJob_KeepsItsGap_AndCountsWhatWentOut()
    {
        // Record 2 has a copies value that cannot be used, so it is left out; its batch position (and serial number) stays a gap, as in ZPL.
        await Batch(PrinterOutputMode.WindowsDriver, records: 5, perJob: 0, copies: new[] { "1", "x", "1", "1", "1" });
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "c";
        // Two pages went out: records 1 and 3 (positions 1 and 3); records 4 and 5 never reached the driver.
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage (Win32 error 1)"), 10).WithPages(2);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 1, 3, 4, 5 }, _fake.Printer.Resolved.Single().Labels.Select(l => int.Parse(l.Values!["n"], CultureInfo.InvariantCulture)));
        Assert.Equal(3, Used);
        Assert.Contains(Loc.Get("Data.Driver.PrintedThrough", 3), Data.ResultText);
        Assert.Equal("SN0004", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Fact]
    public async Task AnErrorThatEscapesTheService_DuringAPictureJob_KeepsThatWholeJobsNumbers()
    {
        // The print service lets an out-of-memory error through after earlier driver documents of the same call may have been accepted, and the
        // page count never comes back. Every number of the job in flight stays used; none is given back.
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 2);
        _fake.Printer.Result = n => n == 2 ? throw new InvalidOperationException("The driver binding broke down.") : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.False(Data.IsBusy);
        Assert.True(Data.ResultIsError);
        Assert.Equal(2, _fake.Printer.Resolved.Count);
        Assert.Equal(4, Used);
        Assert.Equal("SN0005", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Theory]
    [InlineData(PrintErrorKind.Unknown)]
    [InlineData(PrintErrorKind.Timeout)]
    public async Task APictureFailureInDoubt_WithNoPageOut_GivesTheJobsNumbersBack(PrintErrorKind kind)
    {
        // The accepted rule in picture mode: the driver's page count decides, and the print service counts every picture it handed over, even
        // when the driver threw. No page out means nothing could have reached the printer (the ZPL rule would keep these numbers).
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 2);
        _fake.Printer.Result = n => n == 2 ? PrintResult.Fail(PrintError.Create(kind, Queue, "x", "y")) : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.True(Data.ResultIsError);
        Assert.Equal(2, Used);
        Assert.Equal("SN0003", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Theory]
    [InlineData(PrintErrorKind.NotAcknowledged)]
    [InlineData(PrintErrorKind.InvalidLabel)]
    public async Task AJobRefusedByTheDriverCheckMidBatch_SaysWhatHappened_WithoutABoxToTick(PrintErrorKind kind)
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 2);
        // The service checks every job again; by the second job the driver reports something new (other paper, or the printer is gone).
        var refused = kind == PrintErrorKind.NotAcknowledged
            ? PrintResult.Fail(PrintErrorKind.NotAcknowledged).WithNotices(new[] { Warning(DriverPreflight.PaperMismatch, "100x50", "210x297") })
            : PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DriverPreflight.DriverUnavailable)).WithNotices(new[] { Error(DriverPreflight.DriverUnavailable) });
        _fake.Printer.Result = n => n == 2 ? refused : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.True(Data.ResultIsError);
        Assert.Contains(Loc.Get("Data.Driver.JobRefused", 3), Data.ResultText);
        Assert.Contains("1 job(s) were already sent", Data.ResultText);
        Assert.DoesNotContain(Loc.Get("Print.Error.NotAcknowledged"), Data.ResultText);
        Assert.DoesNotContain(Loc.Get("Print.Error.InvalidLabel"), Data.ResultText);
        Assert.Contains(DriverPreflightGate.FormatFinding(refused.Notices[0]), Data.ResultText);
        Assert.Equal(2, Used);
    }

    [Fact]
    public async Task TwoBarcodesThatCannotBeDrawn_AreBothListed()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 0);
        var first = Error(DevicePictureRenderer.BarcodeNotDrawn, "EAN-13", "12A");
        var second = Error(DevicePictureRenderer.BarcodeNotDrawn, "Code 128", "\u0001");
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.BarcodeNotDrawn))
            .WithNotices(new[] { first, second });
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Contains(DriverPreflightGate.FormatFinding(first), Data.ResultText);
        Assert.Contains(DriverPreflightGate.FormatFinding(second), Data.ResultText);
    }

    [Fact]
    public async Task WithTickedRecords_ThePagesOutAreCountedByBatchPosition_NotByRowNumber()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 0);
        Data.SelectNoneShownCommand.Execute(null);
        foreach (int row in new[] { 1, 2, 4, 5 }) Data.VisibleRows[row].IsSelected = true;   // records 2, 3, 5 and 6
        // Three pages went out (one copy each): records 2, 3 and 5. They hold the batch positions 1 to 3, so three numbers are used, not five.
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartPage (Win32 error 1)"), 10).WithPages(3);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004" }, SentSerials(PrinterOutputMode.WindowsDriver));
        Assert.Equal(3, Used);
        Assert.Contains(Loc.Get("Data.Driver.PrintedThrough", 5), Data.ResultText);   // what the person reads is the record number
    }

    // ------------------------------------------------------------------------------------ (d) a failed job that printed nothing

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task AFailedJob_ThatPrintedNothing_GivesItsNumbersBack(PrinterOutputMode mode)
    {
        await Batch(mode, records: 6, perJob: 2);
        // ZPL: the printer refused the job before a byte went out. Picture: the driver refused it before it accepted a page.
        var refused = mode == PrinterOutputMode.WindowsDriver
            ? PrintResult.Fail(PrintError.Create(PrintErrorKind.DriverRefused, Queue, "StartDoc (Win32 error 1801)"))
            : PrintResult.Fail(PrintErrorKind.Offline, Queue);
        _fake.Printer.Result = n => n == 2 ? refused : PrintResult.Ok(5);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(2, JobsSent(mode));
        Assert.True(Data.ResultIsError);
        Assert.Contains("1 job(s) were already sent", Data.ResultText);
        Assert.Equal(2, Used);                              // only the first job's two labels
        Assert.DoesNotContain(Loc.Get("Data.Driver.PrintedThrough", 4), Data.ResultText);

        Assert.Equal("SN0003", await FirstSerialOfTheNextBatch(mode));
    }

    // ------------------------------------------------------------------------------------ (e) cancel mid-batch

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task CancellingBetweenJobs_StopsLikeThePrinterLanguagePath(PrinterOutputMode mode)
    {
        await Batch(mode, records: 6, perJob: 2);
        // The operator presses Stop while the first job is going out; that job completes, and the batch stops before the next one.
        _fake.Printer.Result = n =>
        {
            if (n == 1) Data.CancelOperation();
            return PrintResult.Ok(5);
        };
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.False(Data.IsBusy);
        Assert.Equal(1, JobsSent(mode));
        Assert.False(Data.ResultIsError);
        Assert.StartsWith(Loc.Get("Data.Batch.CancelledPartial", 1), Data.ResultText);
        Assert.Equal(2, Used);

        Assert.Equal("SN0003", await FirstSerialOfTheNextBatch(mode));
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task CancellingWhileAJobIsGoingOut_GivesBackWhatCouldNotHavePrinted(PrinterOutputMode mode)
    {
        await Batch(mode, records: 4, perJob: 2);
        _fake.Printer.Gate = new TaskCompletionSource();     // the first job never finishes on its own: it is "being sent"
        var running = Data.PrintAllCommand.ExecuteAsync(null);
        for (int i = 0; i < 300 && JobsSent(mode) == 0; i++) await Task.Delay(10);
        Assert.True(Data.IsBusy);
        Data.CancelOperation();
        await running;

        // The service reports the cancel as a result with nothing out, so the numbers of that job go back, as before.
        Assert.False(Data.IsBusy);
        Assert.Equal(1, JobsSent(mode));
        Assert.False(Data.ResultIsError);
        Assert.Equal(0, Used);
        _fake.Printer.Gate = null;
        Assert.Equal("SN0001", await FirstSerialOfTheNextBatch(mode));
    }

    // ------------------------------------------------------------------------------------ (a) the findings are acknowledged once, before anything is reserved

    [Fact]
    public async Task DataWindow_WarningsAreAskedOnce_WithTheFindings_AndOkPrintsWithTheAcknowledgement()
    {
        var finding = Warning(DriverPreflight.PaperMismatch, "100x50", "101.6x152.4");
        Findings(finding);
        await Batch(PrinterOutputMode.WindowsDriver, records: 5, perJob: 2);
        await Data.PrintAllCommand.ExecuteAsync(null);

        // One question with the findings (not one per job or per record), then the batch question, each asked once.
        Assert.Equal(2, _fake.Confirms.Count);
        Assert.Contains(DriverPreflightGate.FormatFinding(finding), _fake.Confirms[0]);
        Assert.Contains("5 records", _fake.Confirms[1]);
        Assert.Single(_fake.Printer.Preflights);
        Assert.Equal(3, _fake.Printer.Resolved.Count);
        Assert.All(_fake.Printer.Resolved, j => Assert.Equal(Fingerprint(finding), j.Target.AcknowledgedFindings));
        Assert.False(Data.ResultIsError, Data.ResultText);
    }

    [Fact]
    public async Task DataWindow_CancellingTheFindingsQuestion_SendsNothing_AndReservesNothing()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 0);
        _fake.ConfirmAnswers.Enqueue(false);    // no to the findings, which come first
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Single(_fake.Confirms);          // the batch question is never asked
        Assert.Contains(DriverPreflightGate.FormatFinding(Warning(DriverPreflight.RawBlocks, "2")), _fake.Confirms[0]);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Null(_store.Get(CounterKeyOfLabel));       // no serial number was even reserved

        Assert.Equal("SN0001", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Fact]
    public async Task DataWindow_AnErrorFinding_NeverPrints_AndSaysWhy()
    {
        var oneDot = Error(DriverPreflight.ModuleOneDot, "SKU");
        Findings(oneDot, Warning(DriverPreflight.RawBlocks, "1"));
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 0);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Empty(_fake.Confirms);                     // reported before any question: there is nothing to acknowledge or confirm
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.True(Data.ResultIsError);
        Assert.Contains(Loc.Get("Data.Driver.CannotPrint"), Data.ResultText);
        Assert.Contains(DriverPreflightGate.FormatFinding(oneDot), Data.ResultText);
        Assert.Null(_store.Get(CounterKeyOfLabel));
    }

    [Fact]
    public async Task DataWindow_NoFindings_PrintsWithoutAnotherQuestion()
    {
        Findings();
        await Batch(PrinterOutputMode.WindowsDriver, records: 2, perJob: 0);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Single(_fake.Confirms);
        Assert.Single(_fake.Printer.Resolved);
        Assert.Null(_fake.Printer.Resolved[0].Target.AcknowledgedFindings);
    }

    [Fact]
    public async Task DataWindow_AStoppedCheck_IsACalmStop()
    {
        Findings(Error(DriverPreflight.Cancelled));
        await Batch(PrinterOutputMode.WindowsDriver, records: 2, perJob: 0);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.False(Data.ResultIsError);
        Assert.Equal(Loc.Get("Common.OperationCancelled"), Data.ResultText);
    }

    [Fact]
    public async Task DataWindow_InThePrinterLanguageMode_NeverRunsAPreflight()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        await Batch(PrinterOutputMode.PrinterLanguage, records: 3, perJob: 0);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Preflights);
        Assert.Single(_fake.Confirms);
        Assert.Single(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Resolved);
    }

    [Fact]
    public async Task PrintDialogRecords_InDriverMode_UseTheFindingsCardAndTheTick_AndNeverSendZpl()
    {
        var finding = Warning(DriverPreflight.ModuleBelowMinimum, "SKU", "6.6", "9.8");
        Findings(finding);
        await Batch(PrinterOutputMode.WindowsDriver, records: 4, perJob: 2);
        var dialog = Dialog();

        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.True(dialog.NeedsAcknowledgement);
        Assert.Contains("SKU", Assert.Single(dialog.PreflightLines));
        Assert.Equal(Loc.Get("Print.Driver.TickToContinue"), dialog.ResultText);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Confirms);                     // the findings come first: the batch question waits for the tick
        Assert.Equal(0, Used);

        dialog.Acknowledged = true;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Confirms);                    // so the batch question is asked once
        Assert.False(dialog.ResultIsError, dialog.ResultText);
        Assert.Equal(2, _fake.Printer.Resolved.Count);
        Assert.All(_fake.Printer.Resolved, j => Assert.Equal(Fingerprint(finding), j.Target.AcknowledgedFindings));
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Printed);
        // The dialog's own card and tick answer the findings; the Data window's findings question is never shown on top.
        Assert.DoesNotContain(_fake.Confirms, c => c.Contains("SKU", StringComparison.Ordinal));
        Assert.Equal(4, Used);
        dialog.Detach();
    }

    [Fact]
    public async Task PrintDialogRecords_ForAPrinterThatHasNotAnswered_SendNothingAtAll()
    {
        await _main.Printer.RefreshPrintersAsync();
        await Batch(null, records: 3, perJob: 0);          // the question was never answered for this printer
        Assert.True(_main.Printer.NeedsModeQuestion);
        var dialog = Dialog();

        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Printer.Preflights);
        Assert.Empty(_fake.Confirms);
        Assert.Equal(Loc.Get("Print.Mode.AnswerFirst"), dialog.ResultText);
        dialog.Detach();
    }

    // ------------------------------------------------------------------------------------ the mode question in the Data window

    [Fact]
    public async Task DataWindow_APrinterThatHasNotAnswered_DoesNotBatchPrint_UntilItIsAnswered()
    {
        await _main.Printer.RefreshPrintersAsync();
        await Batch(null, records: 3, perJob: 0);
        Assert.True(_main.Printer.ShowModeQuestion);

        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Confirms);                      // not even the batch question
        Assert.True(Data.ResultIsError);
        Assert.Equal(Loc.Get("Print.Mode.AnswerFirst"), Data.ResultText);
        Assert.Null(_store.Get(CounterKeyOfLabel));

        _main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.ZplJobs);
    }

    [Fact]
    public async Task DataWindow_AQueueThatIsNotInstalled_SaysToChooseAnotherPrinter()
    {
        await _main.Printer.RefreshPrintersAsync();
        await Batch(null, records: 2, perJob: 0);
        _main.Printer.SelectedPrinterName = "Old Printer";
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.ZplJobs);
        Assert.Equal(Loc.Get("Print.Mode.PrinterNotListed", "Old Printer"), Data.ResultText);
    }

    // ------------------------------------------------------------------------------------ a record that cannot be drawn

    [Fact]
    public async Task ABarcodeThatCannotBeDrawn_StopsTheBatch_NamesTheRecord_AndKeepsOnlyWhatMayHavePrinted()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 0, codes: new[] { "A1", "A2", "A3", "BAD", "A5", "A6" });
        var notDrawn = Error(DevicePictureRenderer.BarcodeNotDrawn, "Code 128", "BAD");
        // Records 1 and 2 went out in the first driver document; the next document could not be drawn because of record 4's data.
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.BarcodeNotDrawn), 10)
            .WithNotices(new[] { notDrawn }).WithPages(2);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.True(Data.ResultIsError);
        Assert.Contains(Loc.Get("Data.Driver.RecordNotDrawn", 4, 3), Data.ResultText);
        Assert.Contains(DriverPreflightGate.FormatFinding(notDrawn), Data.ResultText);
        Assert.Contains(Loc.Get("Data.Driver.PrintedThrough", 2), Data.ResultText);
        // The headline of a whole label with errors ("nothing was sent") would be false here: two labels may be on paper.
        Assert.DoesNotContain(Loc.Get("Print.Error.InvalidLabel"), Data.ResultText);
        Assert.Equal(2, Used);
        Assert.Equal("SN0003", await FirstSerialOfTheNextBatch(PrinterOutputMode.WindowsDriver));
    }

    [Fact]
    public async Task ABarcodeValueThatAlsoSitsInAnotherRecordsField_NamesTheRange_NotTheWrongRecord()
    {
        // Record 5's barcode value "2" cannot be drawn, but record 4 holds "2" too, in its copies column (c = n % 3 + 1). Naming record 4 would
        // send the person to correct the wrong record, so the batch names the records the problem can be in instead.
        await Batch(PrinterOutputMode.WindowsDriver, records: 6, perJob: 0, codes: new[] { "A1", "A2", "A3", "A4", "2", "A6" });
        var notDrawn = Error(DevicePictureRenderer.BarcodeNotDrawn, "EAN-13", "2");
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.BarcodeNotDrawn), 10)
            .WithNotices(new[] { notDrawn }).WithPages(2);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.True(Data.ResultIsError);
        Assert.Contains(Loc.Get("Data.Driver.RecordsNotDrawn", 3, 6), Data.ResultText);
        Assert.DoesNotContain(Loc.Get("Data.Driver.RecordNotDrawn", 4, 3), Data.ResultText);
        Assert.Equal(2, Used);
    }

    [Fact]
    public async Task APartThatCannotBeDrawn_WithoutAValueToMatch_NamesTheRecordsItCanBeIn()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 5, perJob: 0);
        var notDrawn = Error(DevicePictureRenderer.ImageNotDrawn);
        _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, DevicePictureRenderer.ImageNotDrawn))
            .WithNotices(new[] { notDrawn }).WithPages(1);
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.True(Data.ResultIsError);
        Assert.Contains(Loc.Get("Data.Driver.RecordsNotDrawn", 2, 5), Data.ResultText);
        Assert.Contains(DriverPreflightGate.FormatFinding(notDrawn), Data.ResultText);
        Assert.Equal(1, Used);
    }

    [Fact]
    public void EveryNewBatchTextExists_InAllFourLanguages_WithoutHoles()
    {
        string[] keys =
        {
            "Data.Driver.Confirm", "Data.Driver.CannotPrint", "Data.Driver.RecordNotDrawn", "Data.Driver.RecordsNotDrawn", "Data.Driver.PrintedThrough",
            "Data.Driver.PrintedInPart", "Data.Driver.JobRefused",
        };
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
        foreach (string lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            foreach (string key in keys)
            {
                string text = Loc.Get(key, "7", "9", "11");
                Assert.DoesNotMatch(new Regex(@"\{\d\}"), text);
                Assert.True(text.Length > 30, $"{lang} {key}: {text}");
            }
            // The preview notes in picture mode must not promise more than the print keeps: the Print dialog lists what is left out.
            var resx = UiStrings.Read(lang);
            // No-break spaces (French typography) are compared as plain spaces, so the check cannot pass on a space alone.
            foreach (var (key, old) in OldPreviewNotes[lang])
                Assert.NotEqual(old, resx[key].Replace(' ', ' ').Replace(' ', ' '));
        }
    }

    /// <summary>The picture-mode preview notes (full, status bar, screen reader name) before they said that the Print dialog lists the parts left out of the print.</summary>
    private static readonly Dictionary<string, (string Key, string Old)[]> OldPreviewNotes = new()
    {
        ["en"] = new[]
        {
            ("Preview.DriverNote", "The preview shows what this printer will print: it prints this same picture through its Windows driver, so text uses Windows fonts instead of the printer's own fonts."),
            ("Preview.DriverNote.Short", "Preview shows the printed picture"),
            ("Preview.DriverNote.Name", "Note: the preview shows the printed picture"),
        },
        ["es"] = new[]
        {
            ("Preview.DriverNote", "La vista previa muestra lo que imprimirá esta impresora: imprime esta misma imagen con su controlador de Windows, así que el texto usa fuentes de Windows en lugar de las fuentes propias de la impresora."),
            ("Preview.DriverNote.Short", "La vista previa muestra la imagen impresa"),
            ("Preview.DriverNote.Name", "Nota: la vista previa muestra la imagen impresa"),
        },
        ["pt-BR"] = new[]
        {
            ("Preview.DriverNote", "A visualização mostra o que esta impressora vai imprimir: ela imprime esta mesma imagem pelo driver do Windows, então o texto usa fontes do Windows em vez das fontes da própria impressora."),
            ("Preview.DriverNote.Short", "A visualização mostra a imagem impressa"),
            ("Preview.DriverNote.Name", "Observação: a visualização mostra a imagem impressa"),
        },
        ["fr-CA"] = new[]
        {
            ("Preview.DriverNote", "L’aperçu montre ce que cette imprimante va imprimer : elle imprime cette même image par son pilote Windows, alors le texte utilise des polices Windows au lieu des polices propres à l’imprimante."),
            ("Preview.DriverNote.Short", "L’aperçu montre l’image imprimée"),
            ("Preview.DriverNote.Name", "Remarque : l’aperçu montre l’image imprimée"),
        },
    };

    // ------------------------------------------------------------------------------------ the real print service agrees on the fingerprint

    [Fact]
    public async Task TheRealService_PrintsTheBatchOnceTheFindingsAreAccepted()
    {
        // The real service works the fingerprint out again for every job, so this proves the batch checks the very document it prints.
        var spooler = new FakeSpooler();
        var driver = new FakeDriverPrinter { Media = Media with { CustomSizeAccepted = false } };
        var service = new PrintService(spooler, new TcpRawPrinter(), spooler, new PrinterStatusReader(), driver);
        var main = new MainViewModel(_fake.Build() with { Printing = service });
        main.Printer.SelectedPrinterName = Queue;
        main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "Item {{n}}" }));
        string path = Path.Combine(_dir, "real.csv");
        File.WriteAllText(path, "n\n1\n2\n3\n", new UTF8Encoding(false));
        Assert.True(await main.Data.LoadAsync(path));
        main.Data.MaxRecordsText = "2";
        main.Printer.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);

        await main.Data.PrintAllCommand.ExecuteAsync(null);

        Assert.False(main.Data.ResultIsError, main.Data.ResultText);
        Assert.Equal(2, _fake.Confirms.Count);              // the batch, then the paper finding
        Assert.Equal(2, driver.Jobs.Count);                 // two jobs of two and one records
        Assert.Equal(3, driver.AllPages.Count);
        Assert.Empty(spooler.Jobs);                         // not one byte of ZPL
    }

    // ------------------------------------------------------------------------------------ the Data window shows the mode question

    [Fact]
    public async Task TheDataWindow_ShowsThePrintModeQuestion_ForAPrinterThatHasNotAnswered()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            var main = new MainViewModel(_fake.Build());
            await main.Printer.RefreshPrintersAsync();
            main.Printer.SelectedPrinterName = Queue;
            main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "Item {{n}}" }));
            string path = Path.Combine(_dir, "hosted.csv");
            File.WriteAllText(path, "n\n1\n2\n", new UTF8Encoding(false));
            Assert.True(await main.Data.LoadAsync(path));
            Assert.True(main.Printer.ShowModeQuestion);

            var facts = WpfTestHost.Run(() =>
            {
                var window = new DataWindow(main.Data);
                var content = (FrameworkElement)window.Content;
                // The printer card lives on the batch tab; only the selected tab is laid out, so that one is selected.
                var batchTab = Logical<TabItem>(content).First(t => Logical<PrinterSettingsView>(t).Any());
                ((TabControl)batchTab.Parent).SelectedItem = batchTab;
                var size = new Size(1100, 800);
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                var view = Logical<PrinterSettingsView>(content).Single();
                var question = (FrameworkElement)view.FindName("ModeQuestionPanel");
                var result = (view.ShowModeChoice, Shown(question, view));
                DetachAll(window);
                return result;
            });

            Assert.True(facts.ShowModeChoice);
            Assert.True(facts.Item2, "The Data window hides the print mode question.");
        }
        finally { snapshot.Restore(); }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>Counter memory and no questions to ask, so a label with a remembered serial counter can print.</summary>
    private sealed class SerialVariables(MemoryCounterStore store) : IVariableServices
    {
        public ICounterStore? CounterStore => store;
        public IPromptMemory PromptMemory { get; } = new MemoryPromptMemory();
        public IReadOnlyDictionary<string, string>? Ask(PromptSet set) => new Dictionary<string, string>();
    }

    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var nested in Logical<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// True when <paramref name="element"/> and every parent up to <paramref name="root"/> are set to show (a window measured off screen is never
    /// shown, so <see cref="UIElement.IsVisible"/> cannot be used).
    /// </summary>
    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(d, root)) return true;
        }
        return false;
    }

    /// <summary>Drops every binding in the window, so the closed window never reacts to later tests that switch the language from another thread.</summary>
    private static void DetachAll(Window window)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(window);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            BindingOperations.ClearAllBindings(node);
            if (node is Visual)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
            foreach (var logical in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) pending.Push(logical);
        }
        window.DataContext = null;
    }

    /// <summary>Remembers <see cref="Loc"/>'s subscribers so a hosted test can undo what its view-models and bindings added (see AppPrintRecordsTests).</summary>
    private sealed class LanguageEventSnapshot
    {
        private static readonly System.Reflection.FieldInfo[] Fields = new[] { "LanguageChanged", "PropertyChanged" }
            .Select(n => typeof(Loc).GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!)
            .ToArray();

        private readonly Delegate?[] _saved = Fields.Select(f => (Delegate?)f.GetValue(Loc.Instance)).ToArray();

        public void Restore()
        {
            for (int i = 0; i < Fields.Length; i++) Fields[i].SetValue(Loc.Instance, _saved[i]);
        }
    }

    // ------------------------------------------------------------------------------------ the limit on labels in one picture job

    /// <summary>The Print dialog's text for the size finding, as the person reads it.</summary>
    private static string TooManyText(long labels) => DriverPreflightGate.FormatFinding(Error(DriverPreflight.TooManyLabels,
        labels.ToString(CultureInfo.InvariantCulture), DriverPreflight.MaxLabelsPerJob.ToString(CultureInfo.InvariantCulture)));

    [Fact]
    public async Task APictureBatch_IsPreflightedWithTheCopiesOfEveryRecord_AndAtTheLimitItPrints()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 2, copies: new[] { "60000", "30000", "10000" });
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "c";
        await Data.PrintAllCommand.ExecuteAsync(null);

        // Records x copies, summed over the whole batch (not per job): exactly the limit is allowed.
        Assert.Equal(new long[] { 100_000 }, _fake.Printer.PreflightLabelCounts);
        Assert.False(Data.ResultIsError, Data.ResultText);
        Assert.Equal(2, _fake.Printer.Resolved.Count);
        Assert.Equal(3, Used);
    }

    [Fact]
    public async Task APictureBatch_OneLabelOverTheLimit_IsRefusedBeforeAnythingIsAskedReservedOrSent()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 2, copies: new[] { "60000", "30000", "10001" });
        Data.CopiesSource = CopiesSource.Column;
        Data.CopiesColumn = "c";
        await Data.PrintAllCommand.ExecuteAsync(null);

        Assert.Equal(new long[] { 100_001 }, _fake.Printer.PreflightLabelCounts);
        Assert.True(Data.ResultIsError);
        Assert.Contains(TooManyText(100_001), Data.ResultText);
        Assert.Empty(_fake.Printer.Resolved);
        Assert.Empty(_fake.Printer.ZplJobs);
        // Nothing was reserved: the series was never touched.
        Assert.Null(_store.Get(CounterKeyOfLabel));
        Assert.Equal(0, Used);
    }

    [Fact]
    public async Task OnlyTheTickedRecords_AreCounted_AgainstTheLimit()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 50);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "40000";
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.True(Data.ResultIsError);   // 3 x 40,000 = 120,000
        Assert.Equal(new long[] { 120_000 }, _fake.Printer.PreflightLabelCounts);
        Assert.Empty(_fake.Printer.Resolved);

        Data.VisibleRows[2].IsSelected = false;   // 2 x 40,000 = 80,000
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 120_000, 80_000 }, _fake.Printer.PreflightLabelCounts);
        Assert.False(Data.ResultIsError, Data.ResultText);
        Assert.Equal(new[] { 1, 2 }.Select(i => i.ToString(CultureInfo.InvariantCulture)), _fake.Printer.Resolved.SelectMany(j => j.Labels).Select(l => l.Values!["n"]));
    }

    [Fact]
    public async Task ThePrintDialog_CountsTheTickedRecordsTimesItsCopies_AndShowsTheSizeErrorInItsFindingsCard()
    {
        await Batch(PrinterOutputMode.WindowsDriver, records: 3, perJob: 50);
        var dialog = Dialog();
        dialog.CopiesText = "40000";
        await dialog.PrintCommand.ExecuteAsync(null);

        Assert.Equal(new long[] { 120_000 }, _fake.Printer.PreflightLabelCounts);
        Assert.True(dialog.ResultIsError);
        Assert.False(dialog.NeedsAcknowledgement);
        Assert.Equal(TooManyText(120_000), Assert.Single(dialog.PreflightLines));
        Assert.Equal(TooManyText(120_000), dialog.ResultText);
        Assert.Empty(_fake.Printer.Resolved);

        Data.VisibleRows[2].IsSelected = false;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 120_000, 80_000 }, _fake.Printer.PreflightLabelCounts);
        Assert.False(dialog.ResultIsError, dialog.ResultText);
        Assert.NotEmpty(_fake.Printer.Resolved);
    }

    [Fact]
    public async Task APrinterLanguageBatch_IsNotLimitedByThePictureCap_AndNeverPreflighted()
    {
        await Batch(PrinterOutputMode.PrinterLanguage, records: 3, perJob: 50);
        Data.CopiesSource = CopiesSource.Fixed;
        Data.FixedCopiesText = "40000";
        await Data.PrintAllCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.PreflightLabelCounts);
        Assert.NotEmpty(_fake.Printer.ZplJobs);
    }
}
