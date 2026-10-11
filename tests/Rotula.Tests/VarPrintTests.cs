using System.Text;
using System.Text.RegularExpressions;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>A spooler that records every job as text and answers each call with a scripted result.</summary>
internal sealed class ScriptedSpooler : IRawPrinter, IPrinterDiscovery
{
    public List<string> Sent { get; } = new();

    /// <summary>The result of the n-th call (1-based). The default succeeds.</summary>
    public Func<int, PrintResult> Result { get; set; } = _ => PrintResult.Ok(1);

    public Task<PrintResult> SendAsync(string printerName, string documentName, byte[] data, CancellationToken cancellationToken = default)
    {
        Sent.Add(Encoding.UTF8.GetString(data));
        var r = Result(Sent.Count);
        return Task.FromResult(r.Succeeded ? PrintResult.Ok(data.Length) : r);
    }

    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PrinterInfo>>(Array.Empty<PrinterInfo>());
}

/// <summary>
/// Printing resolves a label's variables (dates, counters that remember their value, questions, computed values) and keeps serial numbers
/// honest: reserved before sending, confirmed only for what may have been printed.
/// </summary>
public class VarPrintTests
{
    private static readonly PrinterTarget Target = PrinterTarget.Spooler("Test Printer");

    private static (PrintService Svc, ScriptedSpooler Spooler) Make()
    {
        var spooler = new ScriptedSpooler();
        return (new PrintService(spooler, new TcpRawPrinter(), spooler), spooler);
    }

    private static LabelDocument CounterLabel(int copies = 1)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "{{serial}}" });
        doc.Print.Quantity = copies;
        return doc;
    }

    private static string[] Numbers(IEnumerable<string> jobs) =>
        jobs.SelectMany(j => Regex.Matches(j, @"SN\d{4}").Select(m => m.Value)).ToArray();

    private static PrintVariableOptions Options(ICounterStore? store, IReadOnlyDictionary<string, string>? answers = null, TimeProvider? time = null, int perJob = 50) =>
        new() { CounterStore = store, PromptAnswers = answers, Time = time, LabelsPerJob = perJob };

    // ------------------------------------------------------------------------------------ values

    [Fact]
    public async Task Print_ResolvesDatesComputedValuesAndAnswers_WithTheGivenClock()
    {
        var (svc, spooler) = Make();
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        doc.Variables.Add(Vars.Date("expiry", "gs1", new DateOffset(30, DateUnit.Days)));
        doc.Variables.Add(Vars.Prompt("shift", p => p.Required = true));
        doc.Variables.Add(Vars.Computed("lot", "'L-' & shift & '-' & expiry"));
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "EXP {{expiry}} {{lot}}" });

        var r = await svc.PrintAsync(Target, doc, null, null,
            Options(null, new Dictionary<string, string> { ["shift"] = "B" }, FakeTime.AtLocal(2026, 3, 7)));

        Assert.True(r.Succeeded);
        // 7 March 2026 + 30 days = 6 April 2026
        Assert.Contains("EXP 260406 L-B-260406", Assert.Single(spooler.Sent));
    }

    [Fact]
    public async Task Print_APromptWithoutAnAnswer_SendsNothing()
    {
        var (svc, spooler) = Make();
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Prompt("shift"));
        doc.Elements.Add(new TextElement { Text = "{{shift}}" });
        var r = await svc.PrintAsync(Target, doc, null, null, Options(null));
        Assert.False(r.Succeeded);
        Assert.Equal(PrintErrorKind.InvalidLabel, r.Error!.Kind);
        Assert.Contains(r.Notices, n => n.MessageKey == VariableMessageKeys.PromptMissingAnswer);
        Assert.Empty(spooler.Sent);
    }

    [Fact]
    public async Task Print_APlainLabel_IsSentExactlyAsBefore()
    {
        var (svc, spooler) = Make();
        var doc = new LabelDocument();
        doc.Variables.Add(new LabelVariable("name", "Ana"));
        doc.Elements.Add(new TextElement { X = 1, Y = 1, Text = "{{name}}" });
        doc.Print.Quantity = 4;
        await svc.PrintAsync(Target, doc);
        Assert.Equal(ZplGenerator.Generate(doc).Zpl, Assert.Single(spooler.Sent));
    }

    // ------------------------------------------------------------------------------------ serial numbers

    [Fact]
    public async Task ARememberedCounter_ContinuesAcrossPrints()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel();
        await svc.PrintAsync(Target, doc, null, null, Options(store));
        await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.Equal(new[] { "SN0001", "SN0002" }, Numbers(spooler.Sent));
    }

    [Fact]
    public async Task AFailedPrint_ThatSentNothing_GivesTheNumberBack()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel();
        spooler.Result = n => n == 1 ? PrintResult.Fail(PrintErrorKind.Offline, "Test Printer") : PrintResult.Ok(1);

        var failed = await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.False(failed.Succeeded);
        var again = await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.True(again.Succeeded);

        // Nothing came out of the printer the first time, so SN0001 was never printed and is used now.
        Assert.Equal(new[] { "SN0001", "SN0001" }, Numbers(spooler.Sent));
        Assert.Equal("SN0001", CounterAdmin.Peek(doc, "serial", store).LastValue);
    }

    [Fact]
    public async Task AFailedPrint_ThatMayHavePrinted_DoesNotRepeatTheNumber()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel();
        // Some bytes reached the printer before the connection broke: the label may be out.
        spooler.Result = n => n == 1 ? PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, "Test Printer"), 40) : PrintResult.Ok(1);

        Assert.False((await svc.PrintAsync(Target, doc, null, null, Options(store))).Succeeded);
        Assert.True((await svc.PrintAsync(Target, doc, null, null, Options(store))).Succeeded);

        Assert.Equal(new[] { "SN0001", "SN0002" }, Numbers(spooler.Sent));
    }

    [Fact]
    public async Task Copies_OfACountingLabel_EachCarryTheirOwnNumber()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 3);
        await svc.PrintAsync(Target, doc, null, null, Options(store));

        var job = Assert.Single(spooler.Sent);
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003" }, Numbers(spooler.Sent));
        Assert.Equal(3, Regex.Matches(job, @"\^XA").Count);
        Assert.DoesNotContain("^PQ3", job);
        Assert.Equal(3, Regex.Matches(job, @"\^PQ1,").Count);
    }

    [Fact]
    public async Task Copies_OfALabelWithoutCounters_StayOneBlockWithPq()
    {
        var (svc, spooler) = Make();
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Date("d", "iso"));
        doc.Elements.Add(new TextElement { Text = "{{d}}" });
        doc.Print.Quantity = 3;
        await svc.PrintAsync(Target, doc, null, null, Options(null, time: FakeTime.AtLocal(2026, 3, 7)));
        var job = Assert.Single(spooler.Sent);
        Assert.Single(Regex.Matches(job, @"\^XA"));
        Assert.Contains("^PQ3,", job);
        Assert.Contains("2026-03-07", job);
    }

    [Fact]
    public async Task ARunInPieces_ThatFailsHalfWay_KeepsOnlyWhatWasSent()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: 5);
        // Pieces of two labels: the first goes out, the second is refused before any byte moves.
        spooler.Result = n => n == 2 ? PrintResult.Fail(PrintErrorKind.Offline, "Test Printer") : PrintResult.Ok(1);

        var r = await svc.PrintAsync(Target, doc, null, null, Options(store, perJob: 2));
        Assert.False(r.Succeeded);
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004" }, Numbers(spooler.Sent));

        // The two labels of the first piece are used; the refused piece never printed, so its numbers are free again.
        spooler.Result = _ => PrintResult.Ok(1);
        var next = new List<string>();
        spooler.Sent.Clear();
        doc.Print.Quantity = 1; // the same label (same id), so the same series
        await svc.PrintAsync(Target, doc, null, null, Options(store));
        next.AddRange(Numbers(spooler.Sent));
        Assert.Equal(new[] { "SN0003" }, next);
    }

    [Fact]
    public async Task TooManySeparateCopies_AreRefusedWithTheirOwnMessage_AndNothingIsReserved()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        var doc = CounterLabel(copies: PrintVariableOptions.MaxIndividualLabels + 1);
        var r = await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.False(r.Succeeded);
        Assert.Contains(r.Notices, n => n.MessageKey == "Variable.Copies.TooMany");
        Assert.DoesNotContain(r.Notices, n => n.MessageKey == VariableMessageKeys.CounterOverflow);
        Assert.Empty(spooler.Sent);
        doc.Print.Quantity = 1;
        await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.Equal(new[] { "SN0001" }, Numbers(spooler.Sent));
    }

    [Fact]
    public async Task ACounterThatRemembers_WithoutAStore_RefusesToPrint()
    {
        var (svc, spooler) = Make();
        var r = await svc.PrintAsync(Target, CounterLabel(), null, null, Options(null));
        Assert.False(r.Succeeded);
        Assert.Contains(r.Notices, n => n.MessageKey == VariableMessageKeys.CounterNoStore);
        Assert.Empty(spooler.Sent);
    }

    [Fact]
    public async Task ACancelledPrint_GivesTheNumbersBack()
    {
        var (svc, spooler) = Make();
        var store = new MemoryCounterStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var doc = CounterLabel(copies: 3);
        var r = await svc.PrintAsync(Target, doc, null, null, Options(store), cts.Token);
        Assert.Equal(PrintErrorKind.Cancelled, r.Error!.Kind);
        Assert.Empty(spooler.Sent);
        doc.Print.Quantity = 1;
        await svc.PrintAsync(Target, doc, null, null, Options(store));
        Assert.Equal(new[] { "SN0001" }, Numbers(spooler.Sent));
    }

    // ------------------------------------------------------------------------------------ batch

    [Fact]
    public void ABatchJob_SaysWhichRecordItEndsAt_SoOnlySentLabelsAreConfirmed()
    {
        var store = new MemoryCounterStore();
        var doc = CounterLabel();
        doc.Elements.Add(new TextElement { X = 5, Y = 40, Text = "{{n}}" });
        var table = new MergeTable(new[] { "n" }, Enumerable.Range(1, 5).Select(i => new[] { i.ToString() }));
        var set = new MergeDataSet(doc, table, new MergeOptions { CounterStore = store, MaxRecordsPerJob = 2 });
        var job = VariableJob.Begin(doc, set.CreateBatchContext(), set.RecordCount);
        set.Options.Job = job;
        var jobs = MergeBatch.JobBlocks(doc, set).ToList();
        Assert.Equal(new[] { 2, 4, 5 }, jobs.Select(j => j.LastRecordNumber));

        job.Complete(jobs[0].LastRecordNumber); // only the first job was really sent
        Assert.Equal("SN0002", CounterAdmin.Peek(doc, "serial", store).LastValue);
        var again = VariableJob.Begin(doc, set.CreateBatchContext(), 1);
        Assert.Equal("SN0003", again.Resolve(0).Values["serial"]);
    }

    // ------------------------------------------------------------------------------------ the dialogs

    /// <summary>Answers the print-time questions with fixed text and counts how often it was asked.</summary>
    private sealed class FixedAnswers(ICounterStore? store, Dictionary<string, string>? answers) : IVariableServices
    {
        public int Asked;
        public ICounterStore? CounterStore => store;
        public IPromptMemory PromptMemory { get; } = new MemoryPromptMemory();

        public IReadOnlyDictionary<string, string>? Ask(PromptSet set)
        {
            if (set.IsEmpty) return new Dictionary<string, string>();
            Asked++;
            return answers;
        }
    }

    [Collection("App")]
    public class Dialogs : IDisposable
    {
        private readonly FakeServices _fake = new();
        private readonly string _dir = AppTestSupport.TempDir();

        public Dialogs() => _fake.Settings.Current.Language = "en";

        public void Dispose()
        {
            Loc.Instance.SetLanguage("en");
            Directory.Delete(_dir, true);
        }

        private MainViewModel Main(IVariableServices variables)
        {
            _fake.Variables = variables;
            var main = new MainViewModel(_fake.Build());
            main.Printer.SelectedPrinterName = "Test Printer";
            return main;
        }

        [Fact]
        public async Task PrintDialog_AsksTheQuestions_AndPassesTheAnswersAndTheStore()
        {
            var store = new MemoryCounterStore();
            var vars = new FixedAnswers(store, new Dictionary<string, string> { ["shift"] = "B" });
            var main = Main(vars);
            main.Session.Edit("Undo.Property", d =>
            {
                d.EnsureId();
                d.Variables.Add(Vars.Prompt("shift"));
                d.Elements.Add(new TextElement { Text = "{{shift}}" });
            });
            var dialog = new PrintViewModel(_fake.Printer, main.Printer, () => main.Session.Document, () => null, _fake.Build().Files, "x", main.Variables);
            await dialog.PrintCommand.ExecuteAsync(null);

            Assert.Equal(1, vars.Asked);
            Assert.Equal("B", _fake.Printer.OptionArgs.Single()!.PromptAnswers!["shift"]);
            Assert.Same(store, _fake.Printer.OptionArgs.Single()!.CounterStore);
            // The copy sent to the printer keeps the definition of the variable.
            Assert.Equal(VariableKind.Prompt, _fake.Printer.Printed.Single().Doc.Variables.Single().Kind);
        }

        [Fact]
        public async Task PrintDialog_WhenTheQuestionsAreCancelled_PrintsNothing()
        {
            var main = Main(new FixedAnswers(null, null));
            main.Session.Edit("Undo.Property", d =>
            {
                d.Variables.Add(Vars.Prompt("shift"));
                d.Elements.Add(new TextElement { Text = "{{shift}}" });
            });
            var dialog = new PrintViewModel(_fake.Printer, main.Printer, () => main.Session.Document, () => null, _fake.Build().Files, "x", main.Variables);
            await dialog.PrintCommand.ExecuteAsync(null);
            Assert.Empty(_fake.Printer.Printed);
        }

        private static async Task Idle(DataMergeViewModel d)
        {
            for (int i = 0; i < 300 && d.IsBusy; i++) await Task.Delay(10);
            Assert.False(d.IsBusy);
        }

        private async Task<DataMergeViewModel> BatchWithCounter(MainViewModel main, int records, int perJob)
        {
            main.Session.Edit("Undo.Property", d =>
            {
                d.EnsureId();
                d.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
                d.Elements.Add(new TextElement { Text = "{{serial}} {{n}}" });
            });
            string path = Path.Combine(_dir, "d.csv");
            File.WriteAllText(path, "n\n" + string.Join('\n', Enumerable.Range(1, records)) + "\n", new UTF8Encoding(false));
            await main.Data.LoadAsync(path);
            main.Data.MaxRecordsText = perJob.ToString();
            return main.Data;
        }

        private static string[] SentNumbers(FakePrintService p) => Numbers(p.ZplJobs.Select(j => j.Zpl));

        [Fact]
        public async Task PrintAll_ContinuesTheSeries_AndAFailedJobKeepsOnlyWhatWasSent()
        {
            var store = new MemoryCounterStore();
            var main = Main(new FixedAnswers(store, new Dictionary<string, string>()));
            var data = await BatchWithCounter(main, records: 6, perJob: 2);
            // The second job moves bytes and then breaks: those labels may be out. The third job is never sent.
            _fake.Printer.Result = n => n == 2 ? PrintResult.Fail(PrintError.Create(PrintErrorKind.Timeout, "Test Printer"), 10) : PrintResult.Ok(5);
            await data.PrintAllCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004" }, SentNumbers(_fake.Printer));

            // Print again: the series goes on after the labels that may have printed, nothing is repeated.
            _fake.Printer.ZplJobs.Clear();
            _fake.Printer.Result = _ => PrintResult.Ok(5);
            await data.PrintAllCommand.ExecuteAsync(null);
            Assert.Equal("SN0005", SentNumbers(_fake.Printer)[0]);
        }

        [Fact]
        public async Task PrintAll_WhenTheFirstJobIsRefused_GivesAllNumbersBack()
        {
            var store = new MemoryCounterStore();
            var main = Main(new FixedAnswers(store, new Dictionary<string, string>()));
            var data = await BatchWithCounter(main, records: 4, perJob: 2);
            _fake.Printer.Result = _ => PrintResult.Fail(PrintErrorKind.Offline, "Test Printer");
            await data.PrintAllCommand.ExecuteAsync(null);

            _fake.Printer.ZplJobs.Clear();
            _fake.Printer.Result = _ => PrintResult.Ok(5);
            await data.PrintAllCommand.ExecuteAsync(null);
            Assert.Equal("SN0001", SentNumbers(_fake.Printer)[0]);
        }

        [Fact]
        public async Task CheckAll_DoesNotUseUpSerialNumbers()
        {
            var store = new MemoryCounterStore();
            var main = Main(new FixedAnswers(store, new Dictionary<string, string>()));
            var data = await BatchWithCounter(main, records: 4, perJob: 0);
            await data.CheckAllCommand.ExecuteAsync(null);
            await data.CheckAllCommand.ExecuteAsync(null);
            Assert.False(data.ResultIsError);

            await data.PrintAllCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "SN0001", "SN0002", "SN0003", "SN0004" }, SentNumbers(_fake.Printer));
        }

        [Fact]
        public async Task PrintAll_WithoutACounterStore_ExplainsInsteadOfRepeatingNumbers()
        {
            var main = Main(new FixedAnswers(null, new Dictionary<string, string>()));
            var data = await BatchWithCounter(main, records: 2, perJob: 0);
            await data.PrintAllCommand.ExecuteAsync(null);
            Assert.Empty(_fake.Printer.ZplJobs);
            Assert.True(data.ResultIsError || data.Issues.Count > 0);
        }

        [Fact]
        public void TheCopyThatIsPrinted_KeepsVariableDefinitionsAndTheLabelId()
        {
            var doc = new LabelDocument();
            doc.EnsureId();
            doc.Variables.Add(Vars.Counter("serial", c => c.Remember = true));
            doc.Variables.Add(Vars.Date("d", "iso"));
            var copy = ElementCloner.CloneDocument(doc);
            Assert.Equal(doc.Id, copy.Id);
            Assert.Equal(new[] { VariableKind.Counter, VariableKind.Date }, copy.Variables.Select(v => v.Kind));
            Assert.True(copy.Variables[0].Counter!.Remember);
            Assert.NotSame(doc.Variables[0], copy.Variables[0]);
        }

        [Fact]
        public void EditingTheVariablesText_KeepsTheDefinitionsAndOnlyChangesSamples()
        {
            var s = AppTestSupport.Session();
            s.Edit("Undo.LabelSettings", d =>
            {
                d.Variables.Add(Vars.Counter("serial", c => c.Padding = 6));
                d.Variables.Add(new LabelVariable("name", "Ana"));
            });
            var p = new PropertyPanelViewModel(s);
            var row = (TextRow)p.Rows.First(r => r.LabelKey == "Prop.Variables");
            row.Text = "serial=000001\nname=Luis\nextra=1";
            Assert.Equal(3, s.Document.Variables.Count);
            var serial = s.Document.Variables[0];
            Assert.Equal(VariableKind.Counter, serial.Kind);
            Assert.Equal(6, serial.Counter!.Padding);
            Assert.Equal("000001", serial.SampleValue);
            Assert.Equal("Luis", s.Document.Variables[1].SampleValue);
            Assert.Equal(VariableKind.Text, s.Document.Variables[2].Kind);
        }
    }
}
