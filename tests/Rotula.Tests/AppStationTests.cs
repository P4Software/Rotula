using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Files;
using Rotula.Files.Station;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The print station view-model with a fake print service: opening labels (also by scanning their key), the scanner sequence through the questions,
/// printing with the printer status check (held back, verified, not confirmed, fault after the job), the reset for the next item, the quantity limit,
/// the data print, and the PIN that guards the exit.
/// </summary>
[Collection("App")]
public class AppStationTests : IDisposable
{
    private const string Network = """ "printers": { "line": { "host": "192.168.1.50" } }, """;

    private readonly string _dir = AppTestSupport.TempDir();
    private readonly FakePrintService _print = new();
    private readonly Messages _messages = new();
    private readonly List<int> _focus = new();
    private int _focusPrint;

    public AppStationTests() => Loc.Instance.SetLanguage("en");

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private sealed class Messages : IMessageService
    {
        public readonly List<string> Confirms = new();
        public void ShowError(string title, string message) { }
        public void ShowInfo(string title, string message) { }
        public SaveChangesAnswer AskSaveChanges(string documentName) => SaveChangesAnswer.Cancel;
        public bool Confirm(string title, string message) { Confirms.Add(message); return true; }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A label asking for a lot (required, letters and digits, upper case) and a number with a default.</summary>
    private string SaveLabel(string name, DataMergeSettings? merge = null)
    {
        var doc = new LabelDocument { Width = Measure.Dots(400), Height = Measure.Dots(200), Dpi = PrinterDpi.Dpi203, Id = "station-" + name };
        doc.Variables.Add(Vars.Prompt("lot", p => { p.Label = "Lot"; p.Required = true; p.Pattern = "[A-Z0-9]+"; p.Uppercase = true; }));
        doc.Variables.Add(Vars.Prompt("count", p => { p.InputType = Rotula.Core.Variables.PromptInputType.Number; p.Default = "1"; }));
        doc.Elements.Add(new TextElement { Text = "LOT {{lot}} x {{count}}", X = 10, Y = 10 });
        string path = Path.Combine(_dir, name + ".rotula");
        RotulaFileSerializer.Save(doc, path, merge);
        return path;
    }

    private StationViewModel Station(string json, TimeProvider? time = null)
    {
        var load = StationConfigLoader.Parse(json, Path.Combine(_dir, StationConfig.FileName));
        Assert.True(load.CanOpen, string.Join(", ", load.Issues.Select(i => i.MessageKey)));
        var vm = new StationViewModel(load.Config!, load.Warnings, new StationServices(_print, new VariableServices(null, null), _messages, Time: time));
        vm.FocusFieldRequested += (_, i) => _focus.Add(i);
        vm.FocusPrintRequested += (_, _) => _focusPrint++;
        return vm;
    }

    private async Task<StationViewModel> OpenSingle(string extra = "")
    {
        SaveLabel("pallet");
        var vm = Station("{" + Network + extra + """ "labels": [ { "file": "pallet.rotula", "maxCopies": 10 } ] }""");
        await vm.StartAsync();
        Assert.True(vm.IsLabelOpen);
        return vm;
    }

    private static ValidationIssue Note(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    // ------------------------------------------------------------------------------------ opening

    [Fact]
    public async Task TheOnlyLabel_OpensAtOnce_WithItsQuestions_PrinterAndPreview()
    {
        var vm = await OpenSingle();
        Assert.False(vm.CanGoBack);
        Assert.Equal(new[] { "lot", "count" }, vm.Fields.Select(f => f.Name));
        Assert.Equal("Lot *", vm.Fields[0].Caption);
        Assert.Equal("1", vm.Fields[1].Value);
        Assert.Equal("1", vm.CopiesText);
        Assert.Contains("192.168.1.50:9100", vm.PrinterText);
        Assert.NotNull(vm.Preview);
        Assert.NotNull(vm.Labels[0].Thumbnail);
        Assert.Equal(0, _focus.Last());
        // The station asks the printer as soon as the label is open; this fake cannot answer, which is not a fault.
        Assert.Single(_print.StatusQueries);
        Assert.Equal(StationStatusKind.Unknown, vm.StatusKind);
    }

    [Fact]
    public async Task APrinterThatReportsAFault_IsShownWhenTheLabelOpens()
    {
        _print.Status = HostStatusParser.Parse(HsSamples.S1() + HsSamples.S2(headUp: 1) + HsSamples.S3);
        var vm = await OpenSingle();
        Assert.Equal(StationStatusKind.HeldBack, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.NeedsAttention"), vm.StatusTitle);

        _print.Status = HostStatusParser.Parse(HsSamples.Ready);
        await vm.CheckPrinterAsync();
        Assert.Equal(StationStatusKind.Ready, vm.StatusKind);
    }

    [Fact]
    public async Task ScanningALabelsKey_OpensIt_AndAnUnknownCodeIsExplained()
    {
        SaveLabel("pallet");
        SaveLabel("case");
        var vm = Station("{" + Network + """ "labels": [ { "file": "pallet.rotula", "key": "PAL-01" }, { "file": "case.rotula", "key": "CASE-07" } ] }""");
        await vm.StartAsync();
        Assert.True(vm.IsChoosing);
        Assert.Equal(2, vm.VisibleLabels.Count);

        vm.SearchText = "XYZ-99";
        await vm.SubmitSearchAsync();
        Assert.True(vm.IsChoosing);
        Assert.Contains("XYZ-99", vm.SearchMessage);

        vm.SearchText = " case-07\r";
        await vm.SubmitSearchAsync();
        Assert.Equal("case", vm.Current!.Name);

        vm.BackToListCommand.Execute(null);
        Assert.True(vm.IsChoosing);
        Assert.Equal(string.Empty, vm.SearchText);

        // Typing part of a name that leaves one label, then Enter, opens it as well.
        vm.SearchText = "pall";
        Assert.Single(vm.VisibleLabels);
        await vm.SubmitSearchAsync();
        Assert.Equal("pallet", vm.Current!.Name);
    }

    // ------------------------------------------------------------------------------------ scanning and printing

    [Fact]
    public async Task EnterMovesToTheNextQuestion_AndTheLastScanPrints_ThenTheQuestionsReset()
    {
        var vm = await OpenSingle();
        vm.Fields[0].Value = "ab12";
        await vm.FieldTerminatedAsync(vm.Fields[0], Rotula.Files.Station.ScanTerminator.Enter);
        Assert.Equal(1, _focus.Last());
        Assert.Empty(_print.Printed);

        vm.Fields[1].Value = "3";
        _print.Result = _ => PrintResult.Ok(10).WithNotices(new[] { Note(PrinterStatusKeys.Verified) });
        await vm.FieldTerminatedAsync(vm.Fields[1], Rotula.Files.Station.ScanTerminator.Enter);

        var job = Assert.Single(_print.Printed);
        Assert.Equal("192.168.1.50", job.Target.Host);
        Assert.True(job.Target.Status.CheckBeforePrinting);
        Assert.True(job.Target.Status.VerifyAfterPrinting);
        Assert.Equal(1, job.Doc.Print.Quantity);
        Assert.NotNull(job.Profile);
        var answers = _print.OptionArgs.Single()!.PromptAnswers!;
        Assert.Equal("AB12", answers["lot"]);
        Assert.Equal("3", answers["count"]);
        Assert.Equal(StationStatusKind.Verified, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.Verified"), vm.StatusTitle);

        // Ready for the next item: answers back to their defaults, the keyboard in the first field.
        Assert.Equal(string.Empty, vm.Fields[0].Value);
        Assert.Equal("1", vm.Fields[1].Value);
        Assert.Equal(0, _focus.Last());
    }

    [Fact]
    public async Task WithoutPrintOnLastScan_TheLastEnterGoesToThePrintButton()
    {
        var vm = await OpenSingle(""" "printOnLastScan": false, """);
        vm.Fields[0].Value = "A1";
        await vm.FieldTerminatedAsync(vm.Fields[1], Rotula.Files.Station.ScanTerminator.Tab);
        Assert.Equal(1, _focusPrint);
        Assert.Empty(_print.Printed);
    }

    [Fact]
    public async Task ARefusedScan_StaysInItsField_AndPrintSaysWhatToFix()
    {
        var vm = await OpenSingle();
        vm.Fields[0].Value = "ab-12";
        await vm.FieldTerminatedAsync(vm.Fields[0], Rotula.Files.Station.ScanTerminator.Enter);
        Assert.Equal(0, _focus.Last());
        Assert.True(vm.Fields[0].HasError);

        vm.Fields[0].Value = string.Empty;
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_print.Printed);
        Assert.True(vm.Fields[0].HasError);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Fields.Fix"), vm.StatusDetail);
    }

    [Fact]
    public async Task APrinterFault_HoldsTheJobBack_AndKeepsTheAnswers()
    {
        var vm = await OpenSingle();
        vm.Fields[0].Value = "A1";
        _print.Result = _ => PrintResult.Fail(PrintErrorKind.HeadOpen, "192.168.1.50:9100", "HeadOpen");
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Single(_print.Printed);
        Assert.Equal(StationStatusKind.HeldBack, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.HeldBack"), vm.StatusTitle);
        Assert.Contains("printhead is open", vm.StatusDetail);
        Assert.Equal("A1", vm.Fields[0].Value);
    }

    [Fact]
    public async Task APrintThatCouldNotBeConfirmed_OrFailedAfterTheJob_IsSaidSo()
    {
        var vm = await OpenSingle();
        vm.Fields[0].Value = "A1";
        _print.Result = _ => PrintResult.Ok(10).WithNotices(new[] { Note(PrinterStatusKeys.NotVerified, "StillPrinting") });
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(StationStatusKind.NotVerified, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.NotVerified"), vm.StatusTitle);

        vm.Fields[0].Value = "A2";
        _print.Result = _ => PrintResult.Ok(10).WithNotices(new[] { Note(PrinterStatusKeys.FaultAfterJob, "PaperOut") });
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.Fault"), vm.StatusTitle);
        Assert.Contains("out of labels", vm.StatusDetail);

        vm.Fields[0].Value = "A3";
        _print.Result = _ => PrintResult.Ok(10);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(StationStatusKind.Printed, vm.StatusKind);
    }

    [Fact]
    public async Task TheQuantity_IsLimited_AndUsedAsTheCopies()
    {
        var vm = await OpenSingle();
        Assert.Equal(10, vm.MaxCopies);
        vm.Fields[0].Value = "A1";
        vm.CopiesText = "11";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_print.Printed);
        Assert.Equal(Loc.Get("Station.Copies.Invalid", 10), vm.StatusDetail);

        vm.CopiesText = "4";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(4, Assert.Single(_print.Printed).Doc.Print.Quantity);
    }

    [Fact]
    public async Task KeptAnswers_StayForTheNextItem()
    {
        SaveLabel("pallet");
        var vm = Station("{" + Network + """ "labels": [ { "file": "pallet.rotula", "keepFields": ["LOT"] } ] }""");
        await vm.StartAsync();
        vm.Fields[0].Value = "A1";
        vm.Fields[1].Value = "7";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Single(_print.Printed);
        Assert.Equal("A1", vm.Fields[0].Value);
        Assert.Equal("1", vm.Fields[1].Value);
    }

    [Fact]
    public async Task ALabelWithoutAPrinter_CannotPrint_AndSaysWhy()
    {
        SaveLabel("pallet");
        var vm = Station("""{ "labels": [ { "file": "pallet.rotula" } ] }""");
        await vm.StartAsync();
        Assert.False(vm.CanPrint);
        Assert.Equal(Loc.Get("Station.Printer.None"), vm.StatusDetail);
        vm.Fields[0].Value = "A1";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_print.Printed);
    }

    [Fact]
    public async Task TheLabelsData_CanBePrinted_WhenTheStationAllowsIt()
    {
        string csv = Path.Combine(_dir, "items.csv");
        File.WriteAllText(csv, "sku\nA-1\nB-2\n");
        SaveLabel("pallet", new DataMergeSettings { SourcePath = csv });
        var vm = Station("{" + Network + """ "labels": [ { "file": "pallet.rotula", "allowDataMerge": true } ] }""");
        await vm.StartAsync();
        Assert.True(vm.ShowRecords);
        Assert.True(vm.CanPrintRecords, vm.RecordsText);
        Assert.Contains("2", vm.RecordsText);

        vm.Fields[0].Value = "L1";
        await vm.PrintRecordsCommand.ExecuteAsync(null);
        Assert.Single(_messages.Confirms);
        Assert.NotEmpty(_print.ZplJobs);
        Assert.Contains("LOT L1", string.Concat(_print.ZplJobs.Select(j => j.Zpl)));
        Assert.Equal(StationStatusKind.Printed, vm.StatusKind);
    }

    [Fact]
    public async Task WithoutPermission_TheLabelsDataIsNotOffered()
    {
        string csv = Path.Combine(_dir, "items.csv");
        File.WriteAllText(csv, "sku\nA-1\n");
        SaveLabel("pallet", new DataMergeSettings { SourcePath = csv });
        var vm = Station("{" + Network + """ "labels": [ { "file": "pallet.rotula" } ] }""");
        await vm.StartAsync();
        Assert.False(vm.ShowRecords);
        Assert.False(vm.CanPrintRecords);
    }

    // ------------------------------------------------------------------------------------ leaving

    [Fact]
    public async Task WithoutAPin_ExitLeavesAtOnce()
    {
        var vm = await OpenSingle();
        int exits = 0;
        vm.ExitRequested += (_, _) => exits++;
        vm.RequestExitCommand.Execute(null);
        Assert.Equal(1, exits);
        Assert.True(vm.ExitAuthorized);
        Assert.False(vm.IsPinPromptOpen);
    }

    [Fact]
    public async Task ThePin_GuardsTheExit_AndLocksAfterTooManyWrongTries()
    {
        SaveLabel("pallet");
        string hash = StationPin.Hash("2468", 10_000);
        var clock = new ManualClock();
        var vm = Station("{" + Network + $$""" "exitPinHash": "{{hash}}", "labels": [ { "file": "pallet.rotula" } ] }""", clock);
        await vm.StartAsync();
        int exits = 0;
        vm.ExitRequested += (_, _) => exits++;

        vm.RequestExitCommand.Execute(null);
        Assert.True(vm.IsPinPromptOpen);
        Assert.Equal(0, exits);
        Assert.False(await vm.SubmitPinAsync("1111"));
        Assert.Equal(Loc.Get("Station.Pin.Wrong"), vm.PinError);
        vm.CancelExitCommand.Execute(null);
        Assert.False(vm.IsPinPromptOpen);
        Assert.False(vm.ExitAuthorized);

        vm.RequestExitCommand.Execute(null);
        for (int i = 1; i < StationViewModel.MaxPinAttempts; i++) Assert.False(await vm.SubmitPinAsync("0000"));
        Assert.Contains("30", vm.PinError);
        // Locked: even the right PIN waits until the lock-out is over.
        Assert.False(await vm.SubmitPinAsync("2468"));
        clock.Now += StationViewModel.PinLockout + TimeSpan.FromSeconds(1);
        Assert.True(await vm.SubmitPinAsync("2468"));
        Assert.True(vm.ExitAuthorized);
        Assert.Equal(1, exits);
    }

    // ------------------------------------------------------------------------------------ entry points

    [Theory]
    [InlineData(new[] { "--station", @"C:\Labels" }, LaunchMode.Station, @"C:\Labels")]
    [InlineData(new[] { "--STATION" }, LaunchMode.Station, null)]
    [InlineData(new[] { "/station", @"C:\Labels\station.json" }, LaunchMode.Station, @"C:\Labels\station.json")]
    [InlineData(new[] { @"--station=C:\Labels" }, LaunchMode.Station, @"C:\Labels")]
    [InlineData(new[] { "--station", "--other" }, LaunchMode.Station, null)]
    [InlineData(new[] { "--station-pin" }, LaunchMode.StationPin, null)]
    [InlineData(new[] { @"C:\Labels\a.rotula" }, LaunchMode.Designer, @"C:\Labels\a.rotula")]
    [InlineData(new string[0], LaunchMode.Designer, null)]
    public void TheCommandLine_ChoosesTheStationOrTheDesigner(string[] args, LaunchMode mode, string? path)
    {
        var launch = StationLaunch.Parse(args);
        Assert.Equal(mode, launch.Mode);
        Assert.Equal(path, launch.Path);
    }

    [Fact]
    public void TheDesignerMenu_OpensTheStationForAFolder_OrExplainsWhyNot()
    {
        SaveLabel("pallet");
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());

        fake.FolderPath = null;
        main.OpenPrintStationCommand.Execute(null);
        Assert.Empty(fake.ToolDialogs.Stations);

        fake.FolderPath = _dir;
        main.OpenPrintStationCommand.Execute(null);
        var station = Assert.Single(fake.ToolDialogs.Stations);
        Assert.Equal("pallet", Assert.Single(station.Labels).Name);

        File.WriteAllText(Path.Combine(_dir, StationConfig.FileName), """{ "exitPinHash": "plain" }""");
        main.OpenPrintStationCommand.Execute(null);
        Assert.Single(fake.ToolDialogs.Stations);
        var error = Assert.Single(fake.Errors);
        Assert.Contains("exitPinHash", error.Message);

        main.CreateStationPinCommand.Execute(null);
        Assert.Single(fake.ToolDialogs.PinTools);
    }

    [Fact]
    public void ThePinTool_MakesTheLineForStationJson_WithoutThePin()
    {
        var fake = new FakeServices();
        var vm = new StationPinViewModel(fake.Build().Clipboard, iterations: 10_000) { Pin = "12", Confirm = "12" };
        vm.CreateCommand.Execute(null);
        Assert.False(vm.HasResult);
        Assert.Contains("4", vm.Message);

        vm.Pin = "4711";
        vm.Confirm = "4712";
        vm.CreateCommand.Execute(null);
        Assert.Equal(Loc.Get("Station.NewPin.Mismatch"), vm.Message);

        vm.Confirm = "4711";
        vm.CreateCommand.Execute(null);
        Assert.StartsWith("\"exitPinHash\": \"pbkdf2-sha256$", vm.ResultLine);
        Assert.DoesNotContain("4711", vm.ResultLine);
        string hash = vm.ResultLine.Split('"')[3];
        Assert.True(StationPin.Verify("4711", hash));
        vm.CopyCommand.Execute(null);
        Assert.Equal(vm.ResultLine, fake.ClipboardText);
    }
}
