using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Automatic conversion of the label to the printer resolution when printing (fake print service only).</summary>
[Collection("App")]
public class AppDpiConversionTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly MainViewModel _main;

    public AppDpiConversionTests()
    {
        _fake.Settings.Current.Language = "en";
        Loc.Instance.SetLanguage("en");
        _main = new MainViewModel(_fake.Build());
        _main.AddElementCommand.Execute(ToolboxKind.Box);
        var d = _main.Session.Document;
        d.Dpi = PrinterDpi.Dpi203;
        d.Width = Measure.Inches(4);
        d.Height = Measure.Inches(3);
    }

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private PrintViewModel Dialog(PrinterDpi printerDpi)
    {
        _main.Printer.SelectedPrinterName = "ZD621R-300dpi";
        _main.Printer.SelectedDpi = printerDpi;
        return new PrintViewModel(_fake.Printer, _main.Printer, () => _main.Session.Document, () => null, _fake.Build().Files, "x");
    }

    [Fact]
    public async Task Print_ToAHigherDpiPrinter_SendsAConvertedCopy_AndLeavesTheDesignAlone()
    {
        var dialog = Dialog(PrinterDpi.Dpi300);
        var design = _main.Session.Document;
        string before = ZplGenerator.Generate(design, null).Zpl;
        int undoSteps = _main.Session.Undo.UndoCount;
        await dialog.PrintCommand.ExecuteAsync(null);
        var sent = Assert.Single(_fake.Printer.Printed).Doc;
        Assert.Equal(PrinterDpi.Dpi300, sent.Dpi);
        string zpl = ZplGenerator.Generate(sent, null).Zpl;
        Assert.Contains("^PW1200", zpl);
        Assert.Contains("^LL900", zpl);
        Assert.Equal(PrinterDpi.Dpi203, design.Dpi);
        Assert.Equal(before, ZplGenerator.Generate(design, null).Zpl);
        Assert.Equal(undoSteps, _main.Session.Undo.UndoCount);
    }

    [Fact]
    public async Task Print_AtTheSameDpi_IsNotConverted()
    {
        var dialog = Dialog(PrinterDpi.Dpi203);
        await dialog.PrintCommand.ExecuteAsync(null);
        string zpl = ZplGenerator.Generate(Assert.Single(_fake.Printer.Printed).Doc, null).Zpl;
        Assert.Contains("^PW812", zpl);
    }

    [Fact]
    public void Note_IsInformative_AndSwitchingTheLabelIsOneUndoStep()
    {
        var dialog = Dialog(PrinterDpi.Dpi300);
        dialog.Printer.RefreshDpiMismatch();
        int undoSteps = _main.Session.Undo.UndoCount;
        Assert.True(dialog.Printer.HasDpiMismatch);
        Assert.Equal("This label was designed at 203 dpi. It will be converted to 300 dpi automatically so it prints at the same size.", dialog.Printer.DpiMismatchText);
        Assert.Equal("Switch the label to 300 dpi", dialog.Printer.SwitchLabelDpiText);
        dialog.Printer.SwitchLabelDpiCommand.Execute(null);
        Assert.Equal(PrinterDpi.Dpi300, _main.Session.Document.Dpi);
        Assert.Equal(undoSteps + 1, _main.Session.Undo.UndoCount);
        Assert.Equal(1200, _main.Session.Document.WidthDots);
        Assert.False(dialog.Printer.HasDpiMismatch);
        _main.UndoCommand.Execute(null);
        Assert.Equal(PrinterDpi.Dpi203, _main.Session.Document.Dpi);
        Assert.Equal(812, _main.Session.Document.WidthDots);
    }

    [Fact]
    public async Task SaveZpl_ForAChosenPrinter_UsesTheConvertedLabel()
    {
        var dialog = Dialog(PrinterDpi.Dpi300);
        // The dialog's own file dialog is a fake; only the conversion helper is asserted here.
        var converted = dialog.Printer.ConvertForPrinter(_main.Session.Document, out _, out string? error);
        Assert.Null(error);
        Assert.Equal(PrinterDpi.Dpi300, converted!.Dpi);
        await Task.CompletedTask;
    }

    [Fact]
    public void ConversionIssues_AreListed_InBothLanguages()
    {
        var barcode = new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "ABC123", ModuleWidth = 1 };
        _main.Session.Document.Elements.Add(barcode);
        var dialog = Dialog(PrinterDpi.Dpi300);
        dialog.Printer.RefreshDpiMismatch();
        Assert.True(dialog.Printer.HasConversionIssues);
        Loc.Instance.SetLanguage("es");
        dialog.Printer.RefreshDpiMismatch();
        Assert.DoesNotContain("Issue.", string.Join(" ", dialog.Printer.ConversionIssues));
        Assert.Contains("dpi", dialog.Printer.DpiMismatchText);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    public void EveryDpiIssueKey_HasText_AndUnknownDpiKeysGetAFallback(string lang)
    {
        Loc.Instance.SetLanguage(lang);
        foreach (var key in new[] { "Dpi.Barcode.ModuleRounded", "Dpi.Raw.NotScaled", "Dpi.Value.Clamped" })
        {
            string text = PrintMessages.DescribeDpiIssue(new ValidationIssue(ValidationSeverity.Warning, key, "id", "Name", "1", "2", "3"));
            Assert.DoesNotContain("{", text);
            Assert.DoesNotContain("Issue.", text);
        }
        string generic = PrintMessages.DescribeDpiIssue(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Future.Thing", "id"));
        Assert.DoesNotContain("Dpi.Future", generic);
        Assert.NotEmpty(generic);
    }
}
