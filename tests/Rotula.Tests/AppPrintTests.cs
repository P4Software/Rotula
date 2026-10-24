using System.Text.RegularExpressions;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The Print dialog's view-model against a fake <see cref="Rotula.Printing.IPrintService"/>.
/// Nothing in this file can reach a real printer or the network: the fake only records calls.
/// </summary>
[Collection("App")]
public class AppPrintTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();
    private readonly MainViewModel _main;

    public AppPrintTests()
    {
        _fake.Settings.Current.Language = "en";
        _main = new MainViewModel(_fake.Build());
        _main.AddElementCommand.Execute(ToolboxKind.Box);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private PrintViewModel Dialog(bool windowsPrinter = true)
    {
        if (windowsPrinter) _main.Printer.SelectedPrinterName = "Test Printer";
        else { _main.Printer.UseNetwork = true; _main.Printer.Host = "192.168.1.50"; }
        return new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => _main.Data.CurrentData, _fake.Build().Files, "my-label");
    }

    [Fact]
    public async Task Print_SendsToTheChosenWindowsPrinter_WithProfileAndCopies()
    {
        var dialog = Dialog();
        dialog.Printer.DarknessText = "20";
        dialog.Printer.SpeedText = "4";
        dialog.CopiesText = "3";
        await dialog.PrintCommand.ExecuteAsync(null);

        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(PrinterTransport.Spooler, call.Target.Transport);
        Assert.Equal("Test Printer", call.Target.PrinterName);
        Assert.Equal(20, call.Profile!.Darkness);
        Assert.Equal(4, call.Profile.SpeedIps);
        Assert.Equal(3, call.Doc.Print.Quantity);
        Assert.NotEqual(3, _main.Session.Document.Print.Quantity); // the open label is never changed by the dialog
        Assert.False(dialog.ResultIsError);
        Assert.Contains("3", dialog.ResultText);
        Assert.False(dialog.IsBusy);
    }

    [Fact]
    public async Task Print_ToATcpPrinter_UsesHostAndPort()
    {
        var dialog = Dialog(windowsPrinter: false);
        dialog.Printer.PortText = "9101";
        await dialog.PrintCommand.ExecuteAsync(null);
        var call = Assert.Single(_fake.Printer.Printed);
        Assert.Equal(PrinterTransport.Tcp, call.Target.Transport);
        Assert.Equal("192.168.1.50", call.Target.Host);
        Assert.Equal(9101, call.Target.Port);
    }

    [Fact]
    public async Task Print_RemembersThePrinterAndProfileInSettings()
    {
        var dialog = Dialog();
        dialog.Printer.DarknessText = "12";
        await dialog.PrintCommand.ExecuteAsync(null);
        var saved = _fake.Settings.Current.Print;
        Assert.True(saved.Configured);
        Assert.Equal("Test Printer", saved.PrinterName);
        Assert.Equal("Spooler", saved.Transport);
        Assert.Equal(12, saved.Darkness);

        // A new session starts from the stored printer.
        var again = new MainViewModel(_fake.Build());
        Assert.Equal("Test Printer", again.Printer.SelectedPrinterName);
        Assert.Equal("12", again.Printer.DarknessText);
    }

    [Fact]
    public async Task Print_WithoutAPrinter_SaysWhatToDo_AndSendsNothing()
    {
        const string expected = "No printer is selected";
        var dialog = Dialog(windowsPrinter: false);
        dialog.Printer.UseNetwork = false;
        dialog.Printer.SelectedPrinterName = null;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains(expected, dialog.ResultText);
        Assert.True(dialog.ResultIsError);
        Assert.Empty(_fake.Printer.Printed);
    }

    [Theory]
    [InlineData("", "Enter the host name")]
    [InlineData("   ", "Enter the host name")]
    public async Task Print_ToANetworkPrinterWithoutAHost_IsRefused(string host, string expected)
    {
        var dialog = Dialog(windowsPrinter: false);
        dialog.Printer.Host = host;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains(expected, dialog.ResultText);
        Assert.Empty(_fake.Printer.Printed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public async Task Print_WithABadPort_IsRefused(string port)
    {
        var dialog = Dialog(windowsPrinter: false);
        dialog.Printer.PortText = port;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains("1 to 65535", dialog.ResultText);
        Assert.Empty(_fake.Printer.Printed);
    }

    [Theory]
    [InlineData("40", "", "Darkness must be a number from 0 to 30")]
    [InlineData("", "20", "Speed must be a number from 1 to 14")]
    [InlineData("abc", "", "must be a number")]
    public async Task Print_WithOutOfRangeProfileValues_ExplainsTheRange_AndSendsNothing(string darkness, string speed, string expected)
    {
        var dialog = Dialog();
        dialog.Printer.DarknessText = darkness;
        dialog.Printer.SpeedText = speed;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains(expected, dialog.ResultText);
        Assert.Empty(_fake.Printer.Printed);
    }

    [Fact]
    public async Task Print_WithABadWidth_UsesThePrintingRangeMessage()
    {
        var dialog = Dialog();
        dialog.Printer.WidthText = "100000";
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains("between 1 and 9999 dots", dialog.ResultText);
        Loc.Instance.SetLanguage("es");
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains("entre 1 y 9999 puntos", dialog.ResultText);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("x")]
    public async Task Print_WithBadCopies_IsRefused(string copies)
    {
        var dialog = Dialog();
        dialog.CopiesText = copies;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Contains("Copies", dialog.ResultText);
        Assert.Empty(_fake.Printer.Printed);
    }

    [Fact]
    public async Task Print_ALabelWithErrors_IsBlocked_ButTheTestLabelStillWorks()
    {
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "123" }));
        var dialog = Dialog();
        Assert.True(dialog.HasLabelProblems);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.Printed);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("EAN-13", dialog.ResultText);

        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Single(_fake.Printer.TestLabels);
        Assert.False(dialog.ResultIsError);
    }

    [Fact]
    public async Task Print_UsesTheRecordShownOnTheCanvas()
    {
        var dialog = Dialog();
        Assert.False(dialog.UsesRecordData);
        string csv = Path.Combine(_dir, "d.csv");
        File.WriteAllText(csv, "name\nAnna\nBen\n");
        _main.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = "Hi {{name}}" }));
        await _main.Data.LoadAsync(csv);
        _main.Data.CurrentIndex = 1;
        Assert.True(dialog.UsesRecordData);
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Equal("Ben", Assert.Single(_fake.Printer.Printed).Data!["name"]);
    }

    [Fact]
    public async Task PrintTestLabel_SendsTheProfile()
    {
        var dialog = Dialog();
        dialog.Printer.WidthText = "80";
        await dialog.PrintTestCommand.ExecuteAsync(null);
        var call = Assert.Single(_fake.Printer.TestLabels);
        Assert.Equal(80, Rotula.Rendering.View.UnitConverter.Convert(call.Profile.Width.Value, call.Profile.Width.Unit, LengthUnit.Millimeters, 203), 3);
    }

    [Theory]
    [InlineData(PrintErrorKind.PrinterNotFound)]
    [InlineData(PrintErrorKind.AccessDenied)]
    [InlineData(PrintErrorKind.Offline)]
    [InlineData(PrintErrorKind.Timeout)]
    [InlineData(PrintErrorKind.ConnectionRefused)]
    [InlineData(PrintErrorKind.HostUnreachable)]
    [InlineData(PrintErrorKind.Cancelled)]
    [InlineData(PrintErrorKind.InvalidLabel)]
    [InlineData(PrintErrorKind.InvalidProfile)]
    [InlineData(PrintErrorKind.EncodingFailed)]
    [InlineData(PrintErrorKind.Unsupported)]
    [InlineData(PrintErrorKind.Unknown)]
    public async Task EveryPrintError_BecomesAPlainSentence_InEveryLanguage(PrintErrorKind kind)
    {
        foreach (var lang in Loc.SupportedLanguages)
        {
            Loc.Instance.SetLanguage(lang);
            _fake.Printer.Result = _ => PrintResult.Fail(PrintError.Create(kind, "Z", "9100", "detail"));
            var dialog = Dialog();
            await dialog.PrintCommand.ExecuteAsync(null);
            Assert.DoesNotContain("!Print", dialog.ResultText);
            Assert.DoesNotMatch(new Regex(@"\{\d\}"), dialog.ResultText);
            Assert.True(dialog.ResultText.Length > 20, kind.ToString());
            Assert.Equal(kind != PrintErrorKind.Cancelled, dialog.ResultIsError);
            if (kind is PrintErrorKind.PrinterNotFound or PrintErrorKind.Offline or PrintErrorKind.AccessDenied) Assert.Contains("Z", dialog.ResultText);
        }
    }

    [Fact]
    public async Task AFailedPrint_DoesNotRememberTheSettings()
    {
        _fake.Printer.Result = _ => PrintResult.Fail(PrintErrorKind.Offline, "Test Printer");
        var dialog = Dialog();
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.False(_fake.Settings.Current.Print.Configured);
    }

    [Fact]
    public async Task Cancel_StopsAJobInFlight_AndTheDialogIsUsableAgain()
    {
        _fake.Printer.Gate = new TaskCompletionSource(); // never completes: the job is "being sent"
        var dialog = Dialog();
        var running = dialog.PrintCommand.ExecuteAsync(null);
        Assert.True(dialog.IsBusy);
        Assert.False(dialog.IsIdle);

        // While busy, a second request is ignored instead of sending twice.
        await dialog.PrintTestCommand.ExecuteAsync(null);
        Assert.Empty(_fake.Printer.TestLabels);

        dialog.CancelOperationCommand.Execute(null);
        await running;
        Assert.False(dialog.IsBusy);
        Assert.Contains("cancelled", dialog.ResultText);
        Assert.False(dialog.ResultIsError);

        _fake.Printer.Gate = null;
        await dialog.PrintCommand.ExecuteAsync(null);
        Assert.Equal(2, _fake.Printer.Printed.Count);
    }

    [Fact]
    public async Task SaveAsZpl_WritesTheSetupAndTheLabel_AndNeverPrints()
    {
        string path = Path.Combine(_dir, "out.zpl");
        _fake.ExportPath = path;
        var dialog = Dialog();
        dialog.Printer.DarknessText = "15";
        dialog.CopiesText = "2";
        await dialog.SaveZplCommand.ExecuteAsync(null);
        string zpl = File.ReadAllText(path);
        Assert.StartsWith("~SD15", zpl);
        Assert.Contains("^GB", zpl);
        Assert.Contains("^PQ2", zpl);
        Assert.Empty(_fake.Printer.Printed);
        Assert.Contains("out.zpl", dialog.ResultText);
    }

    [Fact]
    public async Task SaveAsZpl_ToAFolderThatCannotBeWritten_SaysSo()
    {
        _fake.ExportPath = Path.Combine(_dir, "missing-folder", "x.zpl");
        var dialog = Dialog();
        await dialog.SaveZplCommand.ExecuteAsync(null);
        Assert.True(dialog.ResultIsError);
        Assert.Contains("could not be written", dialog.ResultText);
    }

    [Fact]
    public async Task SaveAsZpl_Cancelled_DoesNothing()
    {
        _fake.ExportPath = null;
        var dialog = Dialog();
        await dialog.SaveZplCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, dialog.ResultText);
    }

    [Fact]
    public async Task Discovery_SelectsTheDefaultPrinter_AndMarksOfflineOnes()
    {
        _fake.Printer.Printers = new[]
        {
            new PrinterInfo("Office", "USB001", "d", false, false),
            new PrinterInfo("Warehouse", "IP_1", "d", true, true),
        };
        var vm = new MainViewModel(_fake.Build());
        await vm.Printer.RefreshPrintersAsync();
        Assert.Equal("Warehouse", vm.Printer.SelectedPrinterName);
        Assert.Contains("(default)", vm.Printer.Printers[1].Display);
        Assert.Contains("(offline)", vm.Printer.Printers[1].Display);
        Assert.False(vm.Printer.IsLoadingPrinters);
    }

    [Fact]
    public async Task Discovery_WithTheSavedPrinterMissing_ExplainsAndPicksAnother()
    {
        _fake.Settings.Current.Print = new Rotula.App.Services.PrintSettings { Configured = true, PrinterName = "Gone" };
        _fake.Printer.Printers = new[] { new PrinterInfo("Office", "USB001", "d", true, false) };
        var vm = new MainViewModel(_fake.Build());
        await vm.Printer.RefreshPrintersAsync();
        Assert.Contains("\"Gone\"", vm.Printer.PrintersNote);
        Assert.Equal("Office", vm.Printer.SelectedPrinterName);
    }

    [Fact]
    public async Task Discovery_WithNoPrinters_SuggestsANetworkPrinter()
    {
        var vm = new MainViewModel(_fake.Build());
        await vm.Printer.RefreshPrintersAsync();
        Assert.Contains("network printer", vm.Printer.PrintersNote);
    }

    [Fact]
    public async Task Discovery_Failure_IsHandledNotThrown()
    {
        _fake.Printer.DiscoveryFailure = new InvalidOperationException("boom");
        var vm = new MainViewModel(_fake.Build());
        await vm.Printer.RefreshPrintersAsync();
        Assert.Empty(vm.Printer.Printers);
        Assert.False(vm.Printer.IsLoadingPrinters);
    }

    [Fact]
    public void ProfileSeedsFromTheOpenLabel_UntilThePrinterIsConfigured()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(60), Height = Measure.Millimeters(40), Dpi = PrinterDpi.Dpi300 };
        var vm = new MainViewModel(_fake.Build(), new EditorSession(doc));
        Assert.Equal(PrinterDpi.Dpi300, vm.Printer.SelectedDpi);
        Assert.Equal(60, double.Parse(vm.Printer.WidthText, System.Globalization.CultureInfo.CurrentCulture));
    }

    [Fact]
    public void MainViewModelPrintCommand_OpensTheDialogWithAFreshViewModel()
    {
        _main.PrintCommand.Execute(null);
        var shown = _fake.ToolDialogs.LastPrint;
        Assert.NotNull(shown);
        Assert.Same(_main.Printer, shown!.Printer);
        Assert.Empty(_fake.Printer.Printed);
    }
}
