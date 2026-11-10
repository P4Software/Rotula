using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The print mode stored per printer in settings.json (Printer language (ZPL) or Windows driver (picture)) and the one-time question for a
/// printer that has no saved mode. The rule these tests guard: a printer that already printed with Rotula stays on ZPL with no question, a
/// new printer is asked once, detection only suggests, and nothing about the ZPL path changes.
/// </summary>
[Collection("App")]
public class PrinterModeSettingsTests : IDisposable
{
    private readonly FakePrintService _printing = new();
    private LabelDocument _doc = new() { Width = Measure.Dots(800), Height = Measure.Dots(400), Dpi = PrinterDpi.Dpi203 };
    private int _saves;

    public PrinterModeSettingsTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    /// <summary>A view-model over <paramref name="settings"/> with a driver read that never touches Windows.</summary>
    private PrinterSettingsViewModel Vm(PrintSettings settings) =>
        new(_printing, settings, () => _saves++, () => _doc, (_, _) => Task.FromResult(PrinterDetection.None));

    private static PrintSettings ConfiguredWith(string printerName, params (string Name, string? Mode)[] modes)
    {
        var s = new PrintSettings { Configured = true, Transport = "Spooler", PrinterName = printerName };
        foreach (var (name, mode) in modes) s.PrintModes[name] = mode!;
        return s;
    }

    // ------------------------------------------------------------------------------- the brief's cases

    [Theory]
    [InlineData("ZDesigner X")]
    // A configured printer whose name would suggest the Windows driver still stays on ZPL: it already printed with Rotula, so it is known.
    [InlineData("Brother QL-820NWB")]
    public void ExistingConfiguredPrinterStaysOnZplWithoutAQuestion(string printerName)
    {
        var settings = new PrintSettings { Configured = true, Transport = "Spooler", PrinterName = printerName };

        var vm = Vm(settings);

        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        Assert.True(vm.ModeKnown);
        Assert.False(vm.NeedsModeQuestion);
        Assert.False(vm.IsDriverMode);
        Assert.Null(vm.TryBuildTarget(out var target));
        Assert.Equal(PrinterOutputMode.PrinterLanguage, target!.Mode);
        // The seed is the stored answer: the next save writes it, so the next version of the file says it outright.
        Assert.Equal("PrinterLanguage", settings.PrintModes[printerName]);
    }

    [Fact]
    public async Task NewPrinterAsksOnceWithASuggestion()
    {
        _printing.Printers = new[] { new PrinterInfo("Brother QL-820NWB", "USB001", "Brother QL-820NWB", true, false) };
        var settings = new PrintSettings();
        var vm = Vm(settings);
        await vm.RefreshPrintersAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedPrinterName = null;
        vm.SelectedPrinterName = "Brother QL-820NWB";

        Assert.Contains(nameof(PrinterSettingsViewModel.NeedsModeQuestion), raised);
        Assert.True(vm.NeedsModeQuestion);
        Assert.False(vm.ModeKnown);
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.SuggestedMode);
        // Until the person answers, the printer prints as it always did: a suggestion never flips the mode.
        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        Assert.Equal("How does this printer understand labels?", vm.ModeQuestion);

        vm.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);

        Assert.False(vm.NeedsModeQuestion);
        Assert.True(vm.ModeKnown);
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.Mode);
        Assert.True(vm.IsDriverMode);
        // The answer is kept at once, so closing the dialog without printing does not bring the question back.
        Assert.True(_saves > 0);
        vm.Persist();
        Assert.Equal("WindowsDriver", settings.PrintModes["Brother QL-820NWB"]);
        Assert.Null(vm.TryBuildTarget(out var target));
        Assert.Equal(PrinterOutputMode.WindowsDriver, target!.Mode);

        // Asked once: the next session over the same settings knows the printer.
        var again = Vm(settings);
        Assert.Equal("Brother QL-820NWB", again.SelectedPrinterName);
        Assert.False(again.NeedsModeQuestion);
        Assert.Equal(PrinterOutputMode.WindowsDriver, again.Mode);
    }

    [Fact]
    public void ChoiceForPrinterBNeverOverwritesPrinterA()
    {
        var settings = ConfiguredWith("Printer A", ("Printer A", "WindowsDriver"));
        var vm = Vm(settings);
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.Mode);

        vm.SelectedPrinterName = "Printer B";
        Assert.True(vm.NeedsModeQuestion);
        vm.ChooseModeCommand.Execute(PrinterOutputMode.PrinterLanguage);
        vm.Persist();

        Assert.Equal("WindowsDriver", settings.PrintModes["Printer A"]);
        Assert.Equal("PrinterLanguage", settings.PrintModes["Printer B"]);
        vm.SelectedPrinterName = "Printer A";
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.Mode);
        Assert.False(vm.NeedsModeQuestion);

        // Changing A later through the Mode property touches A only.
        vm.Mode = PrinterOutputMode.PrinterLanguage;
        Assert.Equal("PrinterLanguage", settings.PrintModes["Printer A"]);
        Assert.Equal("PrinterLanguage", settings.PrintModes["Printer B"]);
        Assert.Equal(2, settings.PrintModes.Count);
    }

    [Fact]
    public void TcpTargetIsAlwaysZpl()
    {
        var settings = ConfiguredWith("Brother QL-820NWB", ("Brother QL-820NWB", "WindowsDriver"));
        var vm = Vm(settings);
        Assert.True(vm.IsDriverMode);

        vm.UseNetwork = true;
        vm.Host = "192.168.1.50";

        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.SuggestedMode);
        Assert.True(vm.ModeKnown);
        Assert.False(vm.NeedsModeQuestion);
        Assert.False(vm.IsDriverMode);
        Assert.Null(vm.TryBuildTarget(out var target));
        Assert.Equal(PrinterTransport.Tcp, target!.Transport);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, target.Mode);

        // Asking for the driver on a network printer changes nothing and stores nothing.
        vm.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        vm.Mode = PrinterOutputMode.WindowsDriver;
        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        Assert.Single(settings.PrintModes);
        Assert.Equal("WindowsDriver", settings.PrintModes["Brother QL-820NWB"]);

        // Back on the Windows printer its own saved mode applies again.
        vm.UseNetwork = false;
        Assert.True(vm.IsDriverMode);
    }

    [Fact]
    public void DriverModeSkipsZplDpiConversion()
    {
        _doc.Elements.Add(new BoxElement { X = 10, Y = 10, WidthDots = 100, HeightDots = 50, ThicknessDots = 3 });
        var vm = Vm(ConfiguredWith("Brother QL-820NWB", ("Brother QL-820NWB", "WindowsDriver")));
        vm.SelectedDpi = PrinterDpi.Dpi300;

        var result = vm.ConvertForPrinter(_doc, out var issues, out string? error);

        Assert.Same(_doc, result);
        Assert.Empty(issues);
        Assert.Null(error);
        vm.RefreshDpiMismatch();
        Assert.False(vm.HasDpiMismatch);
        Assert.Equal(string.Empty, vm.DpiMismatchText);
        Assert.False(vm.HasConversionIssues);
    }

    [Fact]
    public void SettingsJsonRoundTripKeepsTheMap()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var saved = new AppSettings();
            saved.Print.PrintModes["Brother QL-820NWB"] = "WindowsDriver";
            saved.Print.PrintModes["ZDesigner ZD420-203dpi ZPL"] = "PrinterLanguage";
            Assert.True(new JsonSettingsStore(path).Save(saved));

            var store = new JsonSettingsStore(path);
            var back = store.Load();

            Assert.False(store.LoadProblem);
            Assert.Equal(2, back.Print.PrintModes.Count);
            Assert.Equal("WindowsDriver", back.Print.PrintModes["Brother QL-820NWB"]);
            Assert.Equal("PrinterLanguage", back.Print.PrintModes["ZDesigner ZD420-203dpi ZPL"]);
            // Queue names are keys and must keep their exact spelling (no camel-casing of dictionary keys).
            string json = File.ReadAllText(path);
            Assert.Contains("\"printModes\"", json);
            Assert.Contains("\"Brother QL-820NWB\"", json);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------------------- hard requirements beyond the brief

    [Fact]
    public void ZplModePrinterStillConvertsExactlyAsBefore()
    {
        _doc.Elements.Add(new BoxElement { X = 10, Y = 10, WidthDots = 100, HeightDots = 50, ThicknessDots = 3 });
        _doc.Elements.Add(new TextElement { X = 20, Y = 80, Text = "Lot 42", FontHeight = 30, FontWidth = 20 });
        _doc.Elements.Add(new BarcodeElement { X = 20, Y = 150, Symbology = BarcodeSymbology.Code128, Data = "ABC123", Height = 80, ModuleWidth = 2 });
        var expected = LabelDpiConverter.Convert(_doc, PrinterDpi.Dpi300);

        // Both a printer saved as ZPL and a printer that has not been answered yet print as they always did.
        foreach (var settings in new[] { ConfiguredWith("ZDesigner X", ("ZDesigner X", "PrinterLanguage")), new PrintSettings() })
        {
            var vm = Vm(settings);
            vm.SelectedPrinterName = settings.Configured ? "ZDesigner X" : "Brother QL-820NWB";
            vm.SelectedDpi = PrinterDpi.Dpi300;

            var result = vm.ConvertForPrinter(_doc, out var issues, out string? error);

            Assert.Null(error);
            Assert.NotNull(result);
            Assert.NotSame(_doc, result);
            Assert.Equal(PrinterDpi.Dpi300, result!.Dpi);
            Assert.Equal(ZplGenerator.Generate(expected.Document).Zpl, ZplGenerator.Generate(result).Zpl);
            Assert.Equal(expected.Issues.Select(i => i.MessageKey), issues.Select(i => i.MessageKey));
            vm.RefreshDpiMismatch();
            Assert.True(vm.HasDpiMismatch);
            Assert.Null(vm.TryBuildTarget(out var target));
            Assert.Equal(PrinterOutputMode.PrinterLanguage, target!.Mode);
        }
    }

    [Fact]
    public void OldSettingsFileWithoutTheMapLoadsAndKeepsTheConfiguredPrinterOnZpl()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            // What a version before driver printing wrote: no printModes at all.
            File.WriteAllText(path, "// old\n{ \"print\": { \"configured\": true, \"transport\": \"Spooler\", \"printerName\": \"Office laser\", \"dpi\": \"Dpi203\" } }");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();

            Assert.False(store.LoadProblem);
            // Seeded while loading, before anything can save the file: whichever part of Rotula saves first writes the seed, never an empty map.
            Assert.Equal("PrinterLanguage", Assert.Single(settings.Print.PrintModes, p => p.Key == "Office laser").Value);
            var vm = Vm(settings.Print);
            Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
            Assert.False(vm.NeedsModeQuestion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NewFileWithAnEmptyMapAndAnUnansweredPrinterIsNotSeededAndStillAsks()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            // Written by this version: the map is present, only empty, because the printer was never answered for.
            File.WriteAllText(path, "{ \"print\": { \"configured\": true, \"transport\": \"Spooler\", \"printerName\": \"Brother QL-820NWB\", \"printModes\": {} } }");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();

            Assert.False(store.LoadProblem);
            Assert.Empty(settings.Print.PrintModes);
            var vm = Vm(settings.Print);
            Assert.Equal("Brother QL-820NWB", vm.SelectedPrinterName);
            Assert.True(vm.NeedsModeQuestion);
            Assert.False(vm.ModeKnown);
            Assert.Empty(settings.Print.PrintModes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PrintingOnceWithoutAnsweringDoesNotSeedTheNextStart()
    {
        // The DYMO / Brother user who prints once from the Data window before the question was ever shown: the next start must still ask.
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var first = new JsonSettingsStore(path).Load();
            var vm = Vm(first.Print);
            vm.SelectedPrinterName = "Brother QL-820NWB";
            vm.Persist();
            Assert.True(new JsonSettingsStore(path).Save(first));

            var next = new JsonSettingsStore(path).Load();
            Assert.True(next.Print.Configured);
            var again = Vm(next.Print);

            Assert.Equal("Brother QL-820NWB", again.SelectedPrinterName);
            Assert.True(again.NeedsModeQuestion);
            Assert.False(next.Print.PrintModes.ContainsKey("Brother QL-820NWB"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SeededMapSurvivesASaveLoadRoundTrip()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"print\": { \"configured\": true, \"transport\": \"Spooler\", \"printerName\": \"Office laser\" } }");
            var loaded = new JsonSettingsStore(path).Load();
            Assert.True(new JsonSettingsStore(path).Save(loaded));

            Assert.Equal("PrinterLanguage", PrintModesInFile(path)["Office laser"]);
            var back = new JsonSettingsStore(path).Load();
            Assert.Equal("PrinterLanguage", back.Print.PrintModes["Office laser"]);
            var vm = Vm(back.Print);
            Assert.False(vm.NeedsModeQuestion);
            Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ASettingsFileSavedByThisVersionAlwaysContainsTheMap()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            // The first-run starter file, and a file saved from settings whose map was never touched: both write printModes, even empty.
            string starter = Path.Combine(dir, "starter", "settings.json");
            new JsonSettingsStore(starter).Load();
            Assert.Empty(PrintModesInFile(starter));

            string saved = Path.Combine(dir, "settings.json");
            Assert.True(new JsonSettingsStore(saved).Save(new AppSettings()));
            Assert.Empty(PrintModesInFile(saved));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The printModes object written in a settings file; fails the test when the property is missing or is not an object.</summary>
    private static Dictionary<string, string?> PrintModesInFile(string path)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.True(json.RootElement.GetProperty("print").TryGetProperty("printModes", out var map), "printModes is missing from " + path);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, map.ValueKind);
        return map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
    }

    [Fact]
    public void ExplicitNullMapInTheFileIsTreatedAsAnOlderFile()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            // This version never writes null (an empty map is written as {}), so a null map means a file from before print modes: it is seeded.
            File.WriteAllText(path, "{ \"print\": { \"configured\": true, \"printerName\": \"ZDesigner X\", \"printModes\": null } }");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.False(store.LoadProblem);

            var vm = Vm(settings.Print);

            Assert.NotNull(settings.Print.PrintModes);
            Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
            Assert.False(vm.NeedsModeQuestion);
            vm.SelectedPrinterName = "Brother QL-820NWB";
            Assert.True(vm.NeedsModeQuestion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("Laser")]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownModeStringsAreTreatedAsNoSavedMode(string? stored)
    {
        // The map is not empty, so nothing is seeded: the bad entry alone decides, and it must read as "not answered", never crash.
        var settings = ConfiguredWith("Brother QL-820NWB", ("Brother QL-820NWB", stored));

        var vm = Vm(settings);

        Assert.False(vm.ModeKnown);
        Assert.True(vm.NeedsModeQuestion);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        Assert.False(vm.IsDriverMode);
        // Answering replaces the bad entry.
        vm.ChooseModeCommand.Execute(PrinterOutputMode.WindowsDriver);
        Assert.Equal("WindowsDriver", settings.PrintModes["Brother QL-820NWB"]);
    }

    [Fact]
    public void UnknownModeStringInTheFileLoadsWithoutAProblem()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"print\": { \"configured\": true, \"printerName\": \"Brother\", \"printModes\": { \"Brother\": \"Laser\", \"Other\": null } } }");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.False(store.LoadProblem);

            var vm = Vm(settings.Print);

            Assert.True(vm.NeedsModeQuestion);
            Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.Mode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void QueueNamesMatchWithoutRegardToCase_AndCaseDuplicatesDoNotCrash()
    {
        var vm = Vm(ConfiguredWith("Brother QL", ("brother ql", "windowsdriver")));
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.Mode);
        Assert.False(vm.NeedsModeQuestion);

        // A hand-edited file can hold two spellings of one queue; System.Text.Json loads both into an ordinary dictionary.
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"print\": { \"configured\": true, \"printerName\": \"A\", \"printModes\": { \"A\": \"WindowsDriver\", \"a\": \"PrinterLanguage\" } } }");
            var settings = new JsonSettingsStore(path).Load();
            var fromFile = Vm(settings.Print);
            Assert.True(fromFile.ModeKnown);
            Assert.False(fromFile.NeedsModeQuestion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SuggestionReadsTheDriverNameOnceThePrintersAreListed()
    {
        var vm = Vm(new PrintSettings());
        vm.SelectedPrinterName = "Shipping";
        // Only the queue name is known before the list is read, and it says nothing about ZPL.
        Assert.Equal(PrinterOutputMode.WindowsDriver, vm.SuggestedMode);

        _printing.Printers = new[] { new PrinterInfo("Shipping", "USB001", "ZDesigner ZD420-203dpi ZPL", true, false) };
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await vm.RefreshPrintersAsync();

        Assert.Equal("Shipping", vm.SelectedPrinterName);
        Assert.Equal(PrinterOutputMode.PrinterLanguage, vm.SuggestedMode);
        Assert.Contains(nameof(PrinterSettingsViewModel.SuggestedMode), raised);
        Assert.True(vm.NeedsModeQuestion);
        Assert.Contains("Printer language (ZPL)", vm.ModeSuggestionText);
    }

    [Fact]
    public void TheExplanationSaysWhichModeIsInUse()
    {
        var vm = Vm(ConfiguredWith("Printer A", ("Printer A", "WindowsDriver"), ("Printer B", "PrinterLanguage")));
        Assert.Equal(Loc.Get("Print.Mode.Explain.WindowsDriver"), vm.ModeExplanation);
        Assert.Contains("Windows driver (picture)", vm.ModeSuggestionText);

        vm.SelectedPrinterName = "Printer B";
        Assert.Equal(Loc.Get("Print.Mode.Explain.PrinterLanguage"), vm.ModeExplanation);

        Loc.Instance.SetLanguage("es");
        Assert.Equal(UiStrings.Read("es")["Print.Mode.Question"], vm.ModeQuestion);
        Assert.Equal(UiStrings.Read("es")["Print.Mode.Explain.PrinterLanguage"], vm.ModeExplanation);
    }

    [Fact]
    public void DriverModeNeverAsksThePrinterForItsZplStatus()
    {
        var settings = ConfiguredWith("Printer A", ("Printer A", "WindowsDriver"), ("Printer B", "PrinterLanguage"));
        settings.StatusCheckWindows = true;
        var vm = Vm(settings);
        Assert.True(vm.CheckStatus);

        // A picture-mode printer does not understand ~HS and would print it as text.
        Assert.Null(vm.TryBuildTarget(out var driverTarget));
        Assert.False(driverTarget!.Status.Enabled);

        vm.SelectedPrinterName = "Printer B";
        Assert.Null(vm.TryBuildTarget(out var zplTarget));
        Assert.True(zplTarget!.Status.Enabled);
    }
}
