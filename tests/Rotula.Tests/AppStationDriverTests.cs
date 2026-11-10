using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Files;
using Rotula.Files.Station;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The print station in Windows-driver (picture) mode. The findings of the driver pre-flight are acknowledged once, by whoever sets the station up
/// (the "Set up print station" tool stores the fingerprint per label in station.acknowledged.json, next to station.json); an operator who prints a label whose findings were never
/// acknowledged gets one plain sentence and nothing is sent. A station whose printer is in printer-language (ZPL) mode must behave exactly as
/// before: no pre-flight, no new step. Nothing here reaches a real printer: the services are fakes, or the real print service wired to a fake
/// spooler and a fake driver.
/// </summary>
[Collection("App")]
public class AppStationDriverTests : IDisposable
{
    /// <summary>A Windows printer that is not a ZPL printer: it is printed to through its driver once its mode says so.</summary>
    private const string Queue = "Office Laser";

    /// <summary>What the fake driver reports: a 4 x 6 inch page at 203 dpi, printable to its edges.</summary>
    private static readonly DriverMedia Media = new(203, 203, 812, 1218, 812, 1218, 0, 0, true);

    /// <summary>One label on the designer's printer, at most 10 copies a print.</summary>
    private const string OneLabel = """{ "labels": [ { "file": "pallet.rotula", "maxCopies": 10 } ] }""";

    private readonly string _dir = AppTestSupport.TempDir();
    private readonly FakePrintService _print = new();
    private readonly Messages _messages = new();

    public AppStationDriverTests() => Loc.Instance.SetLanguage("en");

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

    private static ValidationIssue Warning(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    private static ValidationIssue Error(string key, params string[] args) => new(ValidationSeverity.Error, key, null, args);

    private static string Fingerprint(params ValidationIssue[] findings) => new DriverPreflightResult(findings, null).Fingerprint;

    /// <summary>Makes the fake pre-flight answer these findings for every document.</summary>
    private void Findings(params ValidationIssue[] findings) => _print.Preflight = (_, _) => new DriverPreflightResult(findings, Media);

    /// <summary>The designer's own printer settings: <see cref="Queue"/>, with the given mode saved for it (null: no mode saved yet).</summary>
    private static PrintSettings Designer(PrinterOutputMode? mode)
    {
        var settings = new PrintSettings { Configured = true, Transport = "Spooler", PrinterName = Queue, StatusCheckWindows = true };
        settings.PrintModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mode is { } m) settings.PrintModes[Queue] = m.ToString();
        return settings;
    }

    private string ConfigPath => Path.Combine(_dir, StationConfig.FileName);

    /// <summary>A label without questions, saved in the station folder; <paramref name="texts"/> lines of text (more lines stand for an edited design).</summary>
    private string SaveLabel(string name = "pallet", DataMergeSettings? merge = null, int texts = 1)
    {
        var doc = new LabelDocument { Width = Measure.Dots(400), Height = Measure.Dots(200), Dpi = PrinterDpi.Dpi203, Id = "drv-" + name };
        for (int i = 0; i < texts; i++) doc.Elements.Add(new TextElement { Text = "PALLET", X = 10, Y = 10 + 30 * i });
        string path = Path.Combine(_dir, name + ".rotula");
        RotulaFileSerializer.Save(doc, path, merge);
        return path;
    }

    /// <summary>The acknowledgements file the set-up tool keeps next to station.json.</summary>
    private string SidecarPath => Path.Combine(_dir, StationAcknowledgements.FileName);

    /// <summary>pallet.rotula on the designer's printer, with its data allowed.</summary>
    private const string StationJson = """{ "labels": [ { "file": "pallet.rotula", "maxCopies": 10, "allowDataMerge": true } ] }""";

    /// <summary>Stores the fingerprint for pallet.rotula as the set-up tool writes it (the station reads it when it starts).</summary>
    private void Acknowledge(string fingerprint) =>
        File.WriteAllText(SidecarPath, $$"""{ "version": 1, "labels": { "pallet.rotula": "{{fingerprint}}" } }""");

    private StationViewModel Station(string json, PrintSettings? designer, IPrintService? service = null)
    {
        var load = StationConfigLoader.Parse(json, ConfigPath);
        Assert.True(load.CanOpen, string.Join(", ", load.Issues.Select(i => i.MessageKey)));
        return new StationViewModel(load.Config!, load.Warnings, new StationServices(service ?? _print, new VariableServices(null, null), _messages, designer));
    }

    private async Task<StationViewModel> OpenStation(string json, PrintSettings? designer, IPrintService? service = null)
    {
        var vm = Station(json, designer, service);
        await vm.StartAsync();
        Assert.True(vm.IsLabelOpen);
        return vm;
    }

    /// <summary>The set-up tool for the station folder, with the station.json given (null leaves the folder without one).</summary>
    private StationSetupViewModel SetUp(string? json, PrintSettings? designer, IPrintService? service = null)
    {
        if (json is not null) File.WriteAllText(ConfigPath, json);
        var load = StationConfigLoader.Load(_dir);
        Assert.True(load.CanOpen, string.Join(", ", load.Issues.Select(i => i.MessageKey)));
        // Opened for the folder, as the File menu does: "Check again" reads the folder again.
        return new StationSetupViewModel(load.Config!, service ?? _print, designer, stationPath: _dir);
    }

    // ------------------------------------------------------------------------------------ the configuration and the acknowledgements file

    [Fact]
    public void AStationWithoutTheAcknowledgementsFile_LoadsWithNoAcknowledgements()
    {
        SaveLabel();
        var load = StationConfigLoader.Parse(OneLabel, ConfigPath);
        Assert.True(load.CanOpen);
        Assert.NotNull(load.Config!.AcknowledgedFindings);
        Assert.Empty(load.Config.AcknowledgedFindings);
    }

    [Fact]
    public void StoredAcknowledgements_AreReadFromTheFileNextToStationJson_ByTheLabelsFullPath()
    {
        SaveLabel();
        Acknowledge("abc123");
        var load = StationConfigLoader.Parse(OneLabel, ConfigPath);
        Assert.True(load.CanOpen);
        string path = load.Config!.Labels.Single().Path;
        Assert.Equal("abc123", load.Config.AcknowledgedFindings[path]);
        Assert.Equal("abc123", load.Config.AcknowledgedFindings[path.ToUpperInvariant()]);
    }

    [Fact]
    public void StationJson_KeepsTheFormatOlderVersionsRead_TheSettingIsNotAcceptedThere()
    {
        // The acknowledgements live in their own file, so station.json gains no setting an older Rotula would refuse.
        SaveLabel();
        var load = StationConfigLoader.Parse("""{ "acknowledgedFindings": { "pallet.rotula": "abc" }, "labels": [ { "file": "pallet.rotula" } ] }""", ConfigPath);
        Assert.False(load.CanOpen);
        Assert.Equal(StationKeys.InvalidJson, Assert.Single(load.Issues).MessageKey);
    }

    [Theory]
    [InlineData("this is not JSON")]
    [InlineData("""{ "version": 1, "labels": [ "pallet.rotula" ] }""")]
    [InlineData("""{ "version": 99, "labels": { "pallet.rotula": "abc" } }""")]
    [InlineData("""[ "abc" ]""")]
    public async Task ADamagedAcknowledgementsFile_CountsAsNothingAcknowledged_AndTheOperatorGetsThePlainMessage(string content)
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        SaveLabel();
        File.WriteAllText(SidecarPath, content);
        // Never a crash and never a silent pass: the label is refused as if the station had never been set up.
        var vm = await OpenStation(StationJson, Designer(PrinterOutputMode.WindowsDriver));
        Assert.Empty(StationConfigLoader.Parse(StationJson, ConfigPath).Config!.AcknowledgedFindings);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_print.Printed);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), vm.StatusDetail);
    }

    [Fact]
    public void AnAcknowledgementsFileThatCannotBeRead_CountsAsNothingAcknowledged()
    {
        SaveLabel();
        Acknowledge("abc123");
        using var locked = new FileStream(SidecarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var load = StationConfigLoader.Parse(OneLabel, ConfigPath);
        Assert.True(load.CanOpen);
        Assert.Empty(load.Config!.AcknowledgedFindings);
    }

    [Fact]
    public void TheFile_IsWrittenWholeOrNotAtAll_WithKeysRelativeToTheLabelFolder()
    {
        string inside = SaveLabel();
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "case.rotula");
        StationAcknowledgements.Write(SidecarPath, _dir, new Dictionary<string, string> { [inside] = "f1", [outside] = "f2" });

        using (var parsed = JsonDocument.Parse(File.ReadAllText(SidecarPath)))
        {
            Assert.Equal(StationAcknowledgements.CurrentVersion, parsed.RootElement.GetProperty("version").GetInt32());
            var labels = parsed.RootElement.GetProperty("labels");
            // Relative to the label folder, so moving the station folder keeps the acknowledgement; a label outside it keeps its full path.
            Assert.Equal("f1", labels.GetProperty("pallet.rotula").GetString());
            Assert.Equal("f2", labels.GetProperty(outside).GetString());
        }
        var read = StationAcknowledgements.Read(SidecarPath, _dir);
        Assert.Equal("f1", read[inside]);
        Assert.Equal("f2", read[outside]);
        // Written through a temporary file that replaces the old one: nothing is left behind next to it.
        Assert.Equal(new[] { StationAcknowledgements.FileName }, Directory.GetFiles(_dir, "*.json*").Select(Path.GetFileName));

        // Nothing acknowledged any more: the file goes away instead of staying behind empty.
        StationAcknowledgements.Write(SidecarPath, _dir, new Dictionary<string, string>());
        Assert.False(File.Exists(SidecarPath));
    }

    [Fact]
    public void TheFile_RoundTrips_ThroughTheStationLoader()
    {
        string pallet = SaveLabel();
        string box = SaveLabel("box");
        var stored = new Dictionary<string, string> { [pallet] = "f-pallet", [box] = "f-box" };
        StationAcknowledgements.Write(SidecarPath, _dir, stored);

        var load = StationConfigLoader.Parse("""{ "labels": [ { "file": "pallet.rotula" }, { "file": "box.rotula" } ] }""", ConfigPath);
        Assert.True(load.CanOpen);
        Assert.Equal(stored.OrderBy(kv => kv.Key), load.Config!.AcknowledgedFindings.OrderBy(kv => kv.Key));
        // Written again from what was read, the file is the same, byte for byte (stable order, stable layout).
        byte[] first = File.ReadAllBytes(SidecarPath);
        StationAcknowledgements.Write(SidecarPath, _dir, load.Config.AcknowledgedFindings);
        Assert.Equal(first, File.ReadAllBytes(SidecarPath));
    }

    [Fact]
    public void AFailedWrite_LeavesTheOldFileIntact_AndNoTemporaryFileBehind()
    {
        string label = SaveLabel();
        StationAcknowledgements.Write(SidecarPath, _dir, new Dictionary<string, string> { [label] = "old" });
        byte[] before = File.ReadAllBytes(SidecarPath);

        // A station reading the file at that very moment (open without delete sharing) makes the replace fail.
        using (new FileStream(SidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Windows reports a replace over an open file as "access denied"; both kinds are documented, and the set-up tool handles both.
            var failure = Record.Exception(() => StationAcknowledgements.Write(SidecarPath, _dir, new Dictionary<string, string> { [label] = "new" }));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "no exception");
        }

        Assert.Equal(before, File.ReadAllBytes(SidecarPath));
        Assert.Equal("old", StationAcknowledgements.Read(SidecarPath, _dir)[label]);
        Assert.Equal(new[] { StationAcknowledgements.FileName }, Directory.GetFiles(_dir, "*.json*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task SetUp_WhenTheFileCannotBeWritten_SaysSo_AndKeepsTheOldAcknowledgement()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        string label = SaveLabel();
        File.WriteAllText(ConfigPath, OneLabel);
        Acknowledge("old");
        byte[] before = File.ReadAllBytes(SidecarPath);
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        setup.Labels.Single().Acknowledged = true;

        using (new FileStream(SidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            setup.SaveCommand.Execute(null);

        Assert.True(setup.ResultIsError);
        Assert.Equal(Loc.Get("Station.Setup.SaveFailed", SidecarPath), setup.ResultText);
        Assert.Equal(before, File.ReadAllBytes(SidecarPath));
        Assert.Equal("old", StationConfigLoader.Load(_dir).Config!.AcknowledgedFindings[label]);
    }

    [Theory]
    [InlineData(@"SUB\PALLET.ROTULA")]
    [InlineData("sub/Pallet.rotula")]
    [InlineData(@"./sub\pallet.rotula")]
    public void KeysWithOtherSlashesOrLetterCase_FindTheSameLabel(string key)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        SaveLabel(Path.Combine("sub", "pallet"));
        File.WriteAllText(SidecarPath, JsonSerializer.Serialize(new { version = 1, labels = new Dictionary<string, string> { [key] = "f1" } }));

        var load = StationConfigLoader.Parse("""{ "labels": [ { "file": "sub/pallet.rotula" } ] }""", ConfigPath);
        Assert.True(load.CanOpen);
        Assert.Equal("f1", load.Config!.AcknowledgedFindings[load.Config.Labels.Single().Path]);
    }

    // ------------------------------------------------------------------------------------ printer language (ZPL) stations are untouched

    [Fact]
    public async Task AZplStation_NeverCallsPreflight_OnAWindowsQueueOrANetworkPrinter()
    {
        // Even a fake that would report findings is never asked: a ZPL print has no pre-flight step at all.
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var queue = await OpenStation(OneLabel, Designer(PrinterOutputMode.PrinterLanguage));
        await queue.PrintCommand.ExecuteAsync(null);
        var network = await OpenStation("""{ "printers": { "line": { "host": "192.168.1.50" } }, "labels": [ { "file": "pallet.rotula" } ] }""",
            Designer(PrinterOutputMode.WindowsDriver));
        await network.PrintCommand.ExecuteAsync(null);

        Assert.Empty(_print.Preflights);
        Assert.Equal(2, _print.Printed.Count);
        Assert.All(_print.Printed, p =>
        {
            Assert.Equal(PrinterOutputMode.PrinterLanguage, p.Target.Mode);
            Assert.Null(p.Target.AcknowledgedFindings);
        });
        Assert.Equal(Loc.Get("Preview.Approximate"), queue.PreviewNote);
        Assert.Equal(StationStatusKind.Printed, queue.StatusKind);
    }

    [Fact]
    public async Task AQueueWithNoSavedMode_PrintsZpl_AndTheOperatorIsNeverAskedTheModeQuestion()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var designer = Designer(null);
        var vm = await OpenStation(OneLabel, designer);

        Assert.False(vm.Printer!.NeedsModeQuestion);
        Assert.False(vm.Printer.IsDriverMode);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_print.Preflights);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, Assert.Single(_print.Printed).Target.Mode);
        // The station's choice lives in its own copy: the designer's map is exactly as it was.
        Assert.Empty(designer.PrintModes);
    }

    // ------------------------------------------------------------------------------------ picture mode: the operator never acknowledges

    [Fact]
    public async Task WithoutAStoredAcknowledgement_TheOperatorGetsThePlainMessage_AndNothingIsSent()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Single(_print.Preflights);
        Assert.Empty(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Status.Problem"), vm.StatusTitle);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), vm.StatusDetail);
        // No question, no tick box: the operator is never asked to accept the findings.
        Assert.Empty(_messages.Confirms);
    }

    [Fact]
    public async Task OnceSetUpStoredTheFingerprint_TheSamePrintGoesOut_CheckedOnTheVeryDocumentPrinted()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        SaveLabel();
        Acknowledge(Fingerprint(finding));
        var vm = await OpenStation(StationJson, Designer(PrinterOutputMode.WindowsDriver));
        vm.CopiesText = "3";
        await vm.PrintCommand.ExecuteAsync(null);

        var job = Assert.Single(_print.Printed);
        // The service works the fingerprint out again from what it receives: the checked document must be the printed one.
        Assert.Same(Assert.Single(_print.Preflights).Doc, job.Doc);
        Assert.Equal(3, job.Doc.Print.Quantity);
        Assert.Equal(PrinterOutputMode.WindowsDriver, job.Target.Mode);
        Assert.Equal(Queue, job.Target.PrinterName);
        Assert.Equal(Fingerprint(finding), job.Target.AcknowledgedFindings);
        Assert.NotNull(Assert.Single(_print.OptionArgs));
        Assert.Equal(StationStatusKind.Printed, vm.StatusKind);
        Assert.Empty(_messages.Confirms);
    }

    [Fact]
    public async Task AServiceSideNotAcknowledged_SendsTheOperatorToTheSetupPerson_NeverToATickBox()
    {
        // The station's own check passed, but the service worked out other findings when the job arrived (the driver changed in between). The
        // Print dialog's "Tick the box" means nothing to an operator, who has no box: the station says whom to ask, as its own check does.
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings();
        SaveLabel();
        _print.Result = _ => PrintResult.Fail(PrintErrorKind.NotAcknowledged).WithNotices(new[] { finding });
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Single(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), vm.StatusDetail);
        Assert.DoesNotContain(Loc.Get("Print.Error.NotAcknowledged"), vm.StatusDetail);
    }

    [Fact]
    public async Task ALabelThatStoppedTheJobAfterPagesWentOut_TellsTheOperatorHowManyWereSent()
    {
        Findings();
        SaveLabel();
        var notice = Error(DevicePictureRenderer.BarcodeNotDrawn, "EAN-13", "12A");
        _print.Result = _ => PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, notice.MessageKey)).WithNotices(new[] { notice }).WithPages(2);
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        vm.CopiesText = "5";
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.StartsWith(Loc.Get("Print.Error.InvalidLabel.Partial", 2), vm.StatusDetail);
        Assert.DoesNotContain(Loc.Get("Print.Error.InvalidLabel"), vm.StatusDetail);
    }

    [Fact]
    public async Task AnEditedDesign_HasOtherFindings_AndIsRefusedUntilTheStationIsSetUpAgain()
    {
        SaveLabel();
        // Set up when the label had two raw blocks; somebody has since added a third.
        Findings(Warning(DriverPreflight.RawBlocks, "3"));
        Acknowledge(Fingerprint(Warning(DriverPreflight.RawBlocks, "2")));
        var vm = await OpenStation(StationJson, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Empty(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), vm.StatusDetail);
    }

    [Fact]
    public async Task AnErrorFinding_IsNeverPrinted_EvenWithAMatchingStoredFingerprint()
    {
        var error = Error(DriverPreflight.ModuleOneDot, "SKU");
        var warning = Warning(DriverPreflight.RawBlocks, "1");
        Findings(error, warning);
        SaveLabel();
        Acknowledge(Fingerprint(error, warning));
        var vm = await OpenStation(StationJson, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Empty(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Contains(DriverPreflightGate.FormatFinding(error), vm.StatusDetail);
        Assert.Contains(Loc.Get("Station.Driver.CannotPrint"), vm.StatusDetail);
        Assert.DoesNotContain(DriverPreflightGate.FormatFinding(warning), vm.StatusDetail);
    }

    [Fact]
    public async Task NoFindings_PrintsAtOnce_WithoutAnyAcknowledgement()
    {
        Findings();
        SaveLabel();
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        var job = Assert.Single(_print.Printed);
        Assert.Equal(PrinterOutputMode.WindowsDriver, job.Target.Mode);
        Assert.Null(job.Target.AcknowledgedFindings);
        Assert.Equal(StationStatusKind.Printed, vm.StatusKind);
    }

    [Fact]
    public async Task APictureStation_NeverAsksThePrinterForItsStatus_AndItsPreviewNoteSaysSo()
    {
        Findings();
        SaveLabel();
        // The designer asks this Windows printer for its status (~HS): in picture mode the printer would print that command as text.
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await vm.CheckPrinterAsync();
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Empty(_print.StatusQueries);
        Assert.False(Assert.Single(_print.Printed).Target.Status.Enabled);
        Assert.Equal(Loc.Get("Station.Driver.PreviewNote"), vm.PreviewNote);
    }

    [Fact]
    public async Task APrinterOfStationJson_UsesTheModeSavedForItsQueue()
    {
        Findings();
        SaveLabel();
        // The designer's own printer is another one; the map still knows how the station's queue understands labels.
        var designer = Designer(null);
        designer.PrinterName = "Zebra ZD420";
        designer.PrintModes[Queue] = nameof(PrinterOutputMode.WindowsDriver);
        var vm = await OpenStation($$"""{ "printers": { "desk": { "windowsPrinter": "{{Queue}}" } }, "labels": [ { "file": "pallet.rotula" } ] }""", designer);
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Single(_print.Preflights);
        var job = Assert.Single(_print.Printed);
        Assert.Equal(Queue, job.Target.PrinterName);
        Assert.Equal(PrinterOutputMode.WindowsDriver, job.Target.Mode);
    }

    // ------------------------------------------------------------------------------------ the label's data in picture mode

    private string SaveDataLabel()
    {
        string csv = Path.Combine(_dir, "items.csv");
        File.WriteAllText(csv, "sku\nA-1\nB-2\n");
        return SaveLabel(merge: new DataMergeSettings { SourcePath = csv });
    }

    [Fact]
    public async Task TheLabelsData_InPictureMode_IsRefusedWithThePlainMessage_WhenNotSetUp()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveDataLabel();
        var vm = await OpenStation("""{ "labels": [ { "file": "pallet.rotula", "allowDataMerge": true } ] }""", Designer(PrinterOutputMode.WindowsDriver));
        Assert.True(vm.CanPrintRecords, vm.RecordsText);
        await vm.PrintRecordsCommand.ExecuteAsync(null);

        Assert.Single(_print.Preflights);
        Assert.Empty(_print.Resolved);
        Assert.Empty(_print.ZplJobs);
        // Neither the batch question nor the Data window's "print anyway?" question reaches the operator.
        Assert.Empty(_messages.Confirms);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), vm.StatusDetail);
    }

    [Fact]
    public async Task TheLabelsData_InPictureMode_PrintsWithTheStoredAcknowledgement()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        SaveDataLabel();
        Acknowledge(Fingerprint(finding));
        var vm = await OpenStation(StationJson, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintRecordsCommand.ExecuteAsync(null);

        var job = Assert.Single(_print.Resolved);
        Assert.Equal(Fingerprint(finding), job.Target.AcknowledgedFindings);
        Assert.Same(Assert.Single(_print.Preflights).Doc, job.Doc);
        Assert.Empty(_print.ZplJobs);
        // Only the batch question ("Print 2 labels?"), as for a ZPL station; never the findings question.
        Assert.DoesNotContain(_messages.Confirms, m => m.Contains(DriverPreflightGate.FormatFinding(finding), StringComparison.Ordinal));
        Assert.Equal(StationStatusKind.Printed, vm.StatusKind);
    }

    [Fact]
    public async Task TheDesignersSettings_AreNeverChangedByTheStation()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        SaveDataLabel();
        var designer = Designer(PrinterOutputMode.WindowsDriver);
        string before = JsonSerializer.Serialize(designer);
        Acknowledge(Fingerprint(finding));
        var vm = await OpenStation(StationJson, designer);
        await vm.PrintCommand.ExecuteAsync(null);
        await vm.PrintRecordsCommand.ExecuteAsync(null);

        Assert.Single(_print.Printed);
        Assert.Single(_print.Resolved);
        Assert.Equal(before, JsonSerializer.Serialize(designer));
    }

    // ------------------------------------------------------------------------------------ the set-up tool

    [Fact]
    public async Task SetUp_ListsTheFindingsInTheGateWording_AndStoresTheFingerprintOnceTicked()
    {
        var findings = new[] { Warning(DriverPreflight.RawBlocks, "2"), Warning(DriverPreflight.ModuleBelowMinimum, "SKU", "6.6", "9.8") };
        Findings(findings);
        string label = SaveLabel();
        // A hand-written file: a byte order mark, Windows line ends, comments and a trailing comma. Not one byte of it may change.
        byte[] stationJson = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes("// set up by the warehouse office\r\n{\r\n  \"labels\": [ { \"file\": \"pallet.rotula\" } ], // one\r\n}\r\n"))
            .ToArray();
        File.WriteAllBytes(ConfigPath, stationJson);
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);

        var row = Assert.Single(setup.Labels);
        Assert.Equal(DriverPreflightGate.Lines(findings), row.FindingLines);
        Assert.True(row.HasFindingLines);
        Assert.True(row.NeedsAcknowledgement);
        Assert.False(row.HasErrors);
        Assert.False(row.Acknowledged);
        Assert.Equal(Loc.Get("Station.Setup.Mode", Loc.Get("Print.Mode.WindowsDriver")), row.ModeText);

        // Without the tick there is nothing to store: no file is written.
        setup.SaveCommand.Execute(null);
        Assert.False(File.Exists(SidecarPath));
        Assert.Equal(Loc.Get("Station.Setup.Unchanged"), setup.ResultText);

        row.Acknowledged = true;
        setup.SaveCommand.Execute(null);
        Assert.False(setup.ResultIsError, setup.ResultText);
        Assert.Equal(Loc.Get("Station.Setup.Saved", SidecarPath), setup.ResultText);
        // The acknowledgement goes into its own file; the administrator's station.json is not touched at all.
        Assert.True(File.Exists(SidecarPath));
        Assert.Equal(stationJson, File.ReadAllBytes(ConfigPath));
        Assert.Equal(Fingerprint(findings), StationConfigLoader.Load(_dir).Config!.AcknowledgedFindings[label]);

        // The next set-up shows the label as already accepted; taking the tick away and saving withdraws it again.
        var again = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await again.CheckCommand.ExecuteAsync(null);
        Assert.True(again.Labels.Single().Acknowledged);
        again.Labels.Single().Acknowledged = false;
        again.SaveCommand.Execute(null);
        Assert.False(again.ResultIsError, again.ResultText);
        Assert.False(File.Exists(SidecarPath));
        Assert.Empty(StationConfigLoader.Load(_dir).Config!.AcknowledgedFindings);
        Assert.Equal(stationJson, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public async Task SetUp_AnErrorFinding_HasNoBox_AndBlocksThatLabel()
    {
        var error = Error(DriverPreflight.ModuleOneDot, "SKU");
        Findings(error, Warning(DriverPreflight.RawBlocks, "1"));
        string label = SaveLabel();
        var setup = SetUp(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);

        var row = Assert.Single(setup.Labels);
        Assert.True(row.HasErrors);
        Assert.False(row.NeedsAcknowledgement);
        Assert.Contains(DriverPreflightGate.FormatFinding(error), row.FindingLines);
        Assert.Equal(Loc.Get("Station.Setup.Driver.Errors"), row.StatusText);

        // Even a tick set by other means stores nothing for a label that cannot print.
        row.Acknowledged = true;
        setup.SaveCommand.Execute(null);
        Assert.False(File.Exists(SidecarPath));
        Assert.False(StationConfigLoader.Load(_dir).Config!.AcknowledgedFindings.ContainsKey(label));
    }

    [Fact]
    public async Task SetUp_ForAZplStation_ChecksNothing_AndWritesNoFile()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var setup = SetUp(OneLabel, Designer(PrinterOutputMode.PrinterLanguage));
        await setup.CheckCommand.ExecuteAsync(null);

        var row = Assert.Single(setup.Labels);
        Assert.Empty(_print.Preflights);
        Assert.False(row.HasFindingLines);
        Assert.Equal(Loc.Get("Station.Setup.Zpl"), row.StatusText);
        setup.SaveCommand.Execute(null);
        Assert.Equal(OneLabel, File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(SidecarPath));
    }

    [Fact]
    public async Task SetUp_ShowsAQueueWithoutASavedMode_AsZpl_AndSaysWhereToChooseIt()
    {
        SaveLabel();
        var designer = Designer(null);
        var setup = SetUp(OneLabel, designer);
        await setup.CheckCommand.ExecuteAsync(null);

        var row = Assert.Single(setup.Labels);
        Assert.Equal(Loc.Get("Station.Setup.Mode", Loc.Get("Print.Mode.PrinterLanguage")), row.ModeText);
        Assert.Equal(Loc.Get("Station.Setup.NoModeYet"), row.StatusText);
        Assert.Empty(_print.Preflights);
        Assert.Empty(designer.PrintModes);
    }

    [Fact]
    public async Task SetUp_ForAFolderWithoutStationJson_SaysToCreateItFirst_AndWritesNothing()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        Assert.Equal(Loc.Get("Station.Setup.NoStationJson"), setup.StationText);
        // The labels can still be checked, so the person sees what the driver cannot print...
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.True(setup.Labels.Single().NeedsAcknowledgement);
        setup.Labels.Single().Acknowledged = true;
        setup.SaveCommand.Execute(null);

        // ...but nothing is saved until the station has its station.json: the tool never creates one.
        Assert.True(setup.ResultIsError);
        Assert.Equal(Loc.Get("Station.Setup.NoStationJson"), setup.ResultText);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(SidecarPath));
    }

    // ------------------------------------------------------------------------------------ "Check again" reads everything again

    /// <summary>Makes the fake pre-flight count the label's parts, so an edited design (another number of parts) has other findings.</summary>
    private void FindingsFollowTheDesign() => _print.Preflight = (_, d) =>
        new DriverPreflightResult(new[] { Warning(DriverPreflight.RawBlocks, d.Elements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) }, Media);

    [Fact]
    public async Task CheckAgain_ReadsTheEditedLabel_StationJson_AndTheAcknowledgementsAgain()
    {
        FindingsFollowTheDesign();
        SaveLabel();
        var setup = SetUp(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.Equal(new[] { DriverPreflightGate.FormatFinding(Warning(DriverPreflight.RawBlocks, "1")) }, setup.Labels.Single().FindingLines);

        // While the window is open: the label gets a second part, station.json a second label, and the edited label's points are accepted
        // (as another set-up would have stored them).
        SaveLabel(texts: 2);
        SaveLabel("box");
        File.WriteAllText(ConfigPath, """{ "labels": [ { "file": "pallet.rotula" }, { "file": "box.rotula" } ] }""");
        Acknowledge(Fingerprint(Warning(DriverPreflight.RawBlocks, "2")));
        await setup.CheckCommand.ExecuteAsync(null);

        Assert.False(setup.ResultIsError, setup.ResultText);
        Assert.Equal(new[] { "pallet", "box" }, setup.Labels.Select(l => l.Name));
        Assert.Equal(new[] { DriverPreflightGate.FormatFinding(Warning(DriverPreflight.RawBlocks, "2")) }, setup.Labels[0].FindingLines);
        Assert.True(setup.Labels[0].Acknowledged);
        Assert.False(setup.Labels[1].Acknowledged);
    }

    [Fact]
    public async Task CheckAgain_KeepsATickNotSavedYet_WhenTheLabelsPointsDidNotChange()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        SaveLabel();
        SaveLabel("box");
        var setup = SetUp("""{ "labels": [ { "file": "pallet.rotula" }, { "file": "box.rotula" } ] }""", Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        setup.Labels[0].Acknowledged = true;

        await setup.CheckCommand.ExecuteAsync(null);
        Assert.True(setup.Labels[0].Acknowledged);
        Assert.False(setup.Labels[1].Acknowledged);
    }

    [Fact]
    public async Task CheckAgain_WithAStationJsonThatNoLongerOpens_ShowsTheProblem_ClearsEveryResult_AndSavesNothing()
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        var setup = SetUp(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        setup.Labels.Single().Acknowledged = true;

        File.WriteAllText(ConfigPath, """{ "labels": [ """);
        await setup.CheckCommand.ExecuteAsync(null);

        Assert.True(setup.ResultIsError);
        Assert.StartsWith(Loc.Get("Station.Open.Failed"), setup.ResultText);
        Assert.Contains(StationConfig.FileName, setup.ResultText);
        // Never a stale pass: the earlier findings and the tick are gone, and the row says it was not checked.
        var row = setup.Labels.Single();
        Assert.False(row.HasFindingLines);
        Assert.False(row.Acknowledged);
        Assert.True(row.StatusIsProblem);
        setup.SaveCommand.Execute(null);
        Assert.True(setup.ResultIsError);
        Assert.False(File.Exists(SidecarPath));
    }

    [Fact]
    public async Task CheckAgain_WithALabelFileThatNoLongerOpens_ShowsTheProblemOnItsRow_AndSaveKeepsItsAcknowledgement()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        string label = SaveLabel();
        File.WriteAllText(ConfigPath, OneLabel);
        Acknowledge(Fingerprint(finding));
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.True(setup.Labels.Single().Acknowledged);

        File.WriteAllText(label, "this is not a label");
        await setup.CheckCommand.ExecuteAsync(null);

        var row = setup.Labels.Single();
        Assert.True(row.StatusIsProblem);
        Assert.Contains(row.Name, row.StatusText);
        Assert.False(row.HasFindingLines);
        Assert.False(row.Acknowledged);
        // A label that could not be checked keeps what was stored for it.
        setup.SaveCommand.Execute(null);
        Assert.Equal(Fingerprint(finding), StationAcknowledgements.Read(SidecarPath, _dir)[label]);
    }

    // ------------------------------------------------------------------------------------ what Save keeps and drops

    [Fact]
    public async Task SetUp_APrinterThatCannotBeReached_IsAProblem_AndSaveKeepsItsStoredAcknowledgement()
    {
        const string Gone = "Gone Laser";
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        var unavailable = Error(DriverPreflight.DriverUnavailable);
        _print.Preflight = (t, _) => t.PrinterName == Gone
            ? new DriverPreflightResult(new[] { unavailable }, null)
            : new DriverPreflightResult(new[] { finding }, Media);
        string pallet = SaveLabel();
        string box = SaveLabel("box");
        var designer = Designer(PrinterOutputMode.WindowsDriver);
        designer.PrintModes[Gone] = nameof(PrinterOutputMode.WindowsDriver);
        File.WriteAllText(ConfigPath, $$"""
            { "printers": { "desk": { "windowsPrinter": "{{Queue}}" }, "gone": { "windowsPrinter": "{{Gone}}" } },
              "labels": [ { "file": "pallet.rotula", "printer": "gone" }, { "file": "box.rotula", "printer": "desk" } ] }
            """);
        StationAcknowledgements.Write(SidecarPath, _dir, new Dictionary<string, string> { [pallet] = "accepted-earlier" });
        var setup = SetUp(null, designer);
        await setup.CheckCommand.ExecuteAsync(null);

        var unreachable = setup.Labels[0];
        Assert.True(unreachable.StatusIsProblem);
        Assert.False(unreachable.NeedsAcknowledgement);
        Assert.Equal(DriverPreflightGate.FormatFinding(unavailable), unreachable.StatusText);
        setup.Labels[1].Acknowledged = true;
        setup.SaveCommand.Execute(null);

        var stored = StationAcknowledgements.Read(SidecarPath, _dir);
        Assert.Equal("accepted-earlier", stored[pallet]);
        Assert.Equal(Fingerprint(finding), stored[box]);
    }

    [Fact]
    public async Task SetUp_Save_DropsAcknowledgementsOfLabelsTheStationNoLongerOffers()
    {
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        string pallet = SaveLabel();
        File.WriteAllText(ConfigPath, OneLabel);
        StationAcknowledgements.Write(SidecarPath, _dir,
            new Dictionary<string, string> { [pallet] = Fingerprint(finding), [Path.Combine(_dir, "retired.rotula")] = "old" });
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.True(setup.Labels.Single().Acknowledged);

        setup.SaveCommand.Execute(null);
        Assert.False(setup.ResultIsError, setup.ResultText);
        Assert.Equal(new[] { pallet }, StationAcknowledgements.Read(SidecarPath, _dir).Keys);
    }

    [Theory]
    [InlineData("this is not JSON")]
    [InlineData("""{ "version": 2, "labels": { "pallet.rotula": "abc" } }""")]
    public async Task SetUp_Save_RemovesADamagedOrNewerFile_WhenNothingIsAccepted(string content)
    {
        Findings(Warning(DriverPreflight.RawBlocks, "2"));
        SaveLabel();
        File.WriteAllText(ConfigPath, OneLabel);
        File.WriteAllText(SidecarPath, content);
        var setup = SetUp(null, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.False(setup.Labels.Single().Acknowledged);

        setup.SaveCommand.Execute(null);
        Assert.False(setup.ResultIsError, setup.ResultText);
        Assert.Equal(Loc.Get("Station.Setup.Cleared", SidecarPath), setup.ResultText);
        Assert.False(File.Exists(SidecarPath));
    }

    [Fact]
    public async Task SetUp_OneLabelOnTwoPicturePrintersWithDifferentFindings_SaysOnlyOneCanBeAccepted()
    {
        const string Second = "Second Laser";
        var first = Warning(DriverPreflight.RawBlocks, "2");
        var second = Warning(DriverPreflight.PaperMismatch, "100x50", "210x297");
        _print.Preflight = (t, _) => new DriverPreflightResult(new[] { t.PrinterName == Second ? second : first }, Media);
        string label = SaveLabel();
        var designer = Designer(PrinterOutputMode.WindowsDriver);
        designer.PrintModes[Second] = nameof(PrinterOutputMode.WindowsDriver);
        var setup = SetUp($$"""
            { "printers": { "desk": { "windowsPrinter": "{{Queue}}" }, "back": { "windowsPrinter": "{{Second}}" } },
              "labels": [ { "file": "pallet.rotula", "printer": "desk", "key": "A" }, { "file": "pallet.rotula", "printer": "back", "key": "B" } ] }
            """, designer);
        await setup.CheckCommand.ExecuteAsync(null);
        foreach (var row in setup.Labels) row.Acknowledged = true;

        setup.SaveCommand.Execute(null);
        Assert.True(setup.ResultIsError);
        Assert.Equal(Loc.Get("Station.Setup.SharedLabel", SidecarPath, "pallet.rotula", Second), setup.ResultText);
        Assert.Equal(Fingerprint(second), StationAcknowledgements.Read(SidecarPath, _dir)[label]);
    }

    [Fact]
    public async Task APrinterThatCannotBeReached_AtTheStation_SaysToCheckThePrinter_NotToAskForTheLabel()
    {
        var unavailable = Error(DriverPreflight.DriverUnavailable);
        _print.Preflight = (_, _) => new DriverPreflightResult(new[] { unavailable }, null);
        SaveLabel();
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Empty(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Equal(DriverPreflightGate.FormatFinding(unavailable), vm.StatusDetail);
    }

    // ------------------------------------------------------------------------------------ the real print service agrees on the fingerprint

    [Fact]
    public async Task TheRealPrintService_AcceptsTheFingerprintStoredAtSetUp()
    {
        // The real service with a fake spooler and a fake driver that refuses the label size as a custom paper (one PaperMismatch warning).
        var spooler = new FakeSpooler();
        var driver = new FakeDriverPrinter { Media = Media with { CustomSizeAccepted = false } };
        var service = new PrintService(spooler, new TcpRawPrinter(), spooler, new PrinterStatusReader(), driver);
        SaveLabel();
        var designer = Designer(PrinterOutputMode.WindowsDriver);
        File.WriteAllText(ConfigPath, OneLabel);

        var before = await OpenStation(OneLabel, designer, service);
        await before.PrintCommand.ExecuteAsync(null);
        Assert.Empty(driver.Jobs);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), before.StatusDetail);

        var setup = SetUp(null, designer, service);
        await setup.CheckCommand.ExecuteAsync(null);
        var row = Assert.Single(setup.Labels);
        Assert.True(row.NeedsAcknowledgement, string.Join(" | ", row.FindingLines));
        row.Acknowledged = true;
        setup.SaveCommand.Execute(null);
        Assert.False(setup.ResultIsError, setup.ResultText);

        var load = StationConfigLoader.Load(_dir);
        var after = new StationViewModel(load.Config!, load.Warnings, new StationServices(service, new VariableServices(null, null), _messages, designer));
        await after.StartAsync();
        after.CopiesText = "2";
        await after.PrintCommand.ExecuteAsync(null);
        Assert.Equal(StationStatusKind.Printed, after.StatusKind);
        Assert.Single(driver.Jobs);
        Assert.Empty(spooler.Jobs);
    }

    [Fact]
    public async Task TheRealPrintService_AcceptsTheFingerprintStoredAtSetUp_ForTheLabelsDataToo()
    {
        // The batch pre-flights the Data window's own copy of the label; the set-up stored the fingerprint of the station's copy. The real
        // pre-flight must find the same points in both, or a set-up station could never print its records.
        var spooler = new FakeSpooler();
        var driver = new FakeDriverPrinter { Media = Media with { CustomSizeAccepted = false } };
        var service = new PrintService(spooler, new TcpRawPrinter(), spooler, new PrinterStatusReader(), driver);
        SaveDataLabel();
        var designer = Designer(PrinterOutputMode.WindowsDriver);
        const string json = """{ "labels": [ { "file": "pallet.rotula", "allowDataMerge": true } ] }""";

        var before = await OpenStation(json, designer, service);
        await before.PrintRecordsCommand.ExecuteAsync(null);
        Assert.Empty(driver.Jobs);
        Assert.Equal(Loc.Get("Station.Driver.AskSetupPerson"), before.StatusDetail);

        var setup = SetUp(json, designer, service);
        await setup.CheckCommand.ExecuteAsync(null);
        var row = Assert.Single(setup.Labels);
        Assert.True(row.NeedsAcknowledgement, string.Join(" | ", row.FindingLines));
        row.Acknowledged = true;
        setup.SaveCommand.Execute(null);
        Assert.False(setup.ResultIsError, setup.ResultText);

        var load = StationConfigLoader.Load(_dir);
        var after = new StationViewModel(load.Config!, load.Warnings, new StationServices(service, new VariableServices(null, null), _messages, designer));
        await after.StartAsync();
        Assert.True(after.CanPrintRecords, after.RecordsText);
        await after.PrintRecordsCommand.ExecuteAsync(null);
        Assert.Equal(StationStatusKind.Printed, after.StatusKind);
        Assert.NotEmpty(driver.Jobs);
        Assert.Equal(2, driver.AllPages.Count);
        Assert.Empty(spooler.Jobs);
    }

    [Fact]
    public async Task SetUp_ALabelListedTwice_KeepsTheAcknowledgementOfItsPictureEntry()
    {
        // The same file offered on a network (ZPL) printer and on the picture printer: the ZPL entry needs nothing and must not erase what
        // was accepted for the picture entry.
        var finding = Warning(DriverPreflight.RawBlocks, "2");
        Findings(finding);
        string label = SaveLabel();
        var setup = SetUp($$"""
            { "printers": { "line": { "host": "192.168.1.50" }, "desk": { "windowsPrinter": "{{Queue}}" } },
              "labels": [ { "file": "pallet.rotula", "printer": "desk", "key": "A" }, { "file": "pallet.rotula", "printer": "line", "key": "B" } ] }
            """, Designer(PrinterOutputMode.WindowsDriver));
        await setup.CheckCommand.ExecuteAsync(null);
        Assert.Equal(2, setup.Labels.Count);
        setup.Labels[0].Acknowledged = true;
        setup.SaveCommand.Execute(null);

        Assert.Equal(Fingerprint(finding), StationConfigLoader.Load(_dir).Config!.AcknowledgedFindings[label]);
    }

    // ------------------------------------------------------------------------------------ the designer menu

    [Fact]
    public void TheDesignerMenu_OpensTheSetUpForAFolder_OrExplainsWhyNot()
    {
        SaveLabel();
        var fake = new FakeServices();
        fake.Settings.Current.Language = "en";
        var main = new MainViewModel(fake.Build());

        fake.FolderPath = null;
        main.SetUpPrintStationCommand.Execute(null);
        Assert.Empty(fake.ToolDialogs.StationSetups);

        fake.FolderPath = _dir;
        main.SetUpPrintStationCommand.Execute(null);
        var setup = Assert.Single(fake.ToolDialogs.StationSetups);
        Assert.Equal("pallet", Assert.Single(setup.Labels).Name);

        File.WriteAllText(ConfigPath, """{ "exitPinHash": "plain" }""");
        main.SetUpPrintStationCommand.Execute(null);
        Assert.Single(fake.ToolDialogs.StationSetups);
        Assert.Contains("exitPinHash", Assert.Single(fake.Errors).Message);
    }

    // ------------------------------------------------------------------------------------ the windows, built off screen

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// True when <paramref name="element"/> and every parent up to <paramref name="root"/> are set to show (a window measured off screen is never
    /// shown, so <see cref="UIElement.IsVisible"/> is false for everything inside it).
    /// </summary>
    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? d = element; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(d, root)) return true;
        }
        return false;
    }

    /// <summary>Remembers <see cref="Loc"/>'s subscribers so a hosted test can undo what its view-models and bindings added.</summary>
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

    /// <summary>What the hosted set-up window showed.</summary>
    private sealed record SetUpFacts(bool FindingsShown, int FindingRows, bool BoxShown, bool BoxBound, bool SaveInside, bool FitsWidth);

    [Theory]
    [InlineData("en")]
    [InlineData("fr-CA")]   // the longest of the four languages
    public async Task TheSetUpWindow_ShowsTheFindingsAndTheBox_AtItsMinimumSize(string language)
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            Loc.Instance.SetLanguage(language);
            Findings(
                Warning(DriverPreflight.RawBlocks, "2"),
                Warning(DriverPreflight.ModuleBelowMinimum, "A rather long barcode name that has to wrap inside the card", "6.6", "9.8"),
                Warning(DriverPreflight.PaperMismatch, "100x50", "210x297"));
            SaveLabel();
            var setup = SetUp(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
            await setup.CheckCommand.ExecuteAsync(null);

            var facts = WpfTestHost.Run(() =>
            {
                var window = new StationSetupWindow(setup);
                var content = (FrameworkElement)window.Content;
                // The window's minimum size without its frame (a 40-pixel caption and a 1-pixel border, the DialogWindow style).
                var size = new Size(window.MinWidth - 2, window.MinHeight - 40 - 2);
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();

                var card = Descendants<FrameworkElement>(content).Single(e => e.Name == "FindingsCard");
                var rows = Descendants<TextBlock>(card).Count(t => t.Name == "FindingLine");
                var box = Descendants<CheckBox>(content).Single(e => e.Name == "AcknowledgeBox");
                var save = Descendants<Button>(content).Single(b => b.IsDefault);
                var at = save.TranslatePoint(new Point(0, 0), content);
                bool inside = Shown(save, content) && save.ActualWidth > 0 && at.X >= 0 && at.Y >= 0
                    && at.X + save.ActualWidth <= size.Width + 0.5 && at.Y + save.ActualHeight <= size.Height + 0.5;
                var viewer = Descendants<ScrollViewer>(content).First();
                bool fits = card.ActualWidth > 0 && card.ActualWidth <= viewer.ViewportWidth + 0.5;
                // Ticking the box on screen reaches the row the operator's station reads.
                box.IsChecked = true;
                var facts = new SetUpFacts(Shown(card, content), rows, Shown(box, content), setup.Labels[0].Acknowledged, inside, fits);
                BindingOperations.ClearAllBindings(window);
                window.DataContext = null;
                window.Close();
                return facts;
            });

            Assert.True(facts.FindingsShown);
            Assert.Equal(3, facts.FindingRows);
            Assert.True(facts.BoxShown);
            Assert.True(facts.BoxBound);
            Assert.True(facts.SaveInside);
            Assert.True(facts.FitsWidth);
        }
        finally { snapshot.Restore(); }
    }

    [Fact]
    public async Task TheStationWindow_ShowsThePictureNoteUnderThePreview_InPictureMode()
    {
        var snapshot = new LanguageEventSnapshot();
        try
        {
            Findings();
            SaveLabel();
            var vm = await OpenStation("""{ "fullScreen": false, "labels": [ { "file": "pallet.rotula" } ] }""", Designer(PrinterOutputMode.WindowsDriver));
            string? note = WpfTestHost.Run(() =>
            {
                var window = new StationWindow(vm);
                var content = (FrameworkElement)window.Content;
                var size = new Size(1280, 820);
                content.Measure(size);
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                string? text = Descendants<TextBlock>(content).Single(t => t.Name == "PreviewNote").Text;
                BindingOperations.ClearAllBindings(window);
                window.DataContext = null;
                window.Close();
                return text;
            });
            Assert.Equal(Loc.Get("Station.Driver.PreviewNote"), note);
        }
        finally { snapshot.Restore(); }
    }

    // ------------------------------------------------------------------------------------ the limit on labels in one picture job

    /// <summary>A station that allows up to 200,000 copies a print, so a copies value past the picture limit can be typed.</summary>
    private const string BigCopies = """{ "labels": [ { "file": "pallet.rotula", "maxCopies": 200000 } ] }""";

    [Fact]
    public async Task TheStation_AsksThePreflightWithTheCopiesOfThePrint()
    {
        Findings();
        SaveLabel();
        var vm = await OpenStation(OneLabel, Designer(PrinterOutputMode.WindowsDriver));
        vm.CopiesText = "4";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal(new long[] { 4 }, _print.PreflightLabelCounts);
        Assert.Single(_print.Printed);
    }

    [Fact]
    public async Task TheStation_RefusesAPictureJobOverTheLimit_AsAnErrorTheOperatorCannotAccept()
    {
        Findings();
        SaveLabel();
        var vm = await OpenStation(BigCopies, Designer(PrinterOutputMode.WindowsDriver));
        vm.CopiesText = "100001";
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal(new long[] { 100_001 }, _print.PreflightLabelCounts);
        Assert.Empty(_print.Printed);
        Assert.Equal(StationStatusKind.Problem, vm.StatusKind);
        Assert.Contains("100,001", vm.StatusDetail);
        Assert.Contains("100,000", vm.StatusDetail);
        Assert.Contains("smaller batches", vm.StatusDetail);
        Assert.Empty(_messages.Confirms);

        // At the limit it goes through.
        vm.CopiesText = "100000";
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Single(_print.Printed);
    }
}
