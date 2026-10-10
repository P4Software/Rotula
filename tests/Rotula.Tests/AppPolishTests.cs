using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.App.Services;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>Tests of the UI polish pass: label size picker, Objects list text, panel split memory, bounds with record data.</summary>
[Collection("App")]
public class AppPolishTests : IDisposable
{
    private readonly FakeServices _fake = new();

    public AppPolishTests() => _fake.Settings.Current.Language = "en";

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private MainViewModel Create() => new(_fake.Build());

    private static LabelPreset Preset(string key) => LabelPreset.All.First(p => p.NameKey == key);

    [Fact]
    public void SizePresets_ListTheCommonSizes_WithoutTheCustomEntry()
    {
        var vm = Create();
        Assert.Equal(5, vm.SizePresets.Count);
        Assert.DoesNotContain(vm.SizePresets, p => p.NameKey == "Preset.Custom");
    }

    [Fact]
    public void ApplyLabelSize_ChangesTheLabel_AsOneUndoStep()
    {
        var vm = Create();
        var p = Preset("Preset.4x3in");
        vm.ApplyLabelSize(p);
        Assert.Equal(Measure.Inches(4), vm.Session.Document.Width);
        Assert.Equal(Measure.Inches(3), vm.Session.Document.Height);
        Assert.True(vm.IsCurrentSize(p));
        Assert.False(vm.IsCurrentSize(Preset("Preset.4x6in")));

        vm.UndoCommand.Execute(null);
        Assert.False(vm.IsCurrentSize(p));
        Assert.False(vm.Session.Undo.CanUndo); // the picker added exactly one step
    }

    [Fact]
    public void ApplyLabelSize_ToTheCurrentSize_AddsNoUndoStep()
    {
        var vm = Create();
        var p = Preset("Preset.100x150mm");
        vm.ApplyLabelSize(p);
        vm.UndoCommand.Execute(null);
        vm.ApplyLabelSize(p);
        vm.ApplyLabelSize(p);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.Session.Undo.CanUndo);
    }

    [Fact]
    public void SetLabelDpi_IsUndoable_AndIgnoresTheCurrentResolution()
    {
        var vm = Create();
        vm.SetLabelDpi(vm.Session.Document.Dpi);
        Assert.False(vm.Session.Undo.CanUndo);
        vm.SetLabelDpi(PrinterDpi.Dpi300);
        Assert.Equal(PrinterDpi.Dpi300, vm.Session.Document.Dpi);
        vm.UndoCommand.Execute(null);
        Assert.NotEqual(PrinterDpi.Dpi300, vm.Session.Document.Dpi);
    }

    [Fact]
    public void PropertiesPanelShare_IsRemembered_AndClamped()
    {
        var vm = Create();
        Assert.Equal(0.55, vm.PropertiesPanelShare, 3);
        vm.PropertiesPanelShare = 0.62;
        Assert.Equal(0.62, _fake.Settings.Current.PropertiesPanelShare, 3);
        vm.PropertiesPanelShare = 0.99;
        Assert.Equal(0.8, vm.PropertiesPanelShare, 3);
        vm.PropertiesPanelShare = 0.01;
        Assert.Equal(0.2, vm.PropertiesPanelShare, 3);
    }

    [Fact]
    public void Layers_KeepTheFullTextForTheTooltip_WhileTheRowIsCutShort()
    {
        var vm = Create();
        string longText = new('x', 90);
        vm.Session.Edit("Undo.Property", d => d.Elements.Add(new TextElement { Text = longText }));
        var item = vm.Layers.Single();
        Assert.Equal(63, item.Detail.Length);
        Assert.Equal(longText, item.ToolTipText);
    }

    [Fact]
    public void Problems_UseTheSingularForOne()
    {
        var vm = Create();
        vm.Session.Edit("Undo.Property", d => d.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "12" }));
        Assert.Contains("1 error", vm.ProblemsText);
        Assert.DoesNotContain("1 errors", vm.ProblemsText);
    }

    [Fact]
    public void ApproximateNoticeShort_IsShorterThanTheFullExplanation_InBothLanguages()
    {
        var vm = Create();
        Assert.True(vm.ApproximateNoticeShort.Length < vm.ApproximateNotice.Length / 2);
        Loc.Instance.SetLanguage("es");
        Assert.Contains("aproximada", vm.ApproximateNoticeShort);
    }

    [Fact]
    public void Bounds_OfAText_FollowTheRecordShownOnTheCanvas()
    {
        var vm = Create();
        vm.Session.Edit("Undo.Property", d =>
        {
            d.Variables.Add(new LabelVariable("name", "Al"));
            d.Elements.Add(new TextElement { Text = "{{name}}", FontHeight = 40, FontWidth = 40 });
        });
        var text = vm.Session.Document.Elements[0];
        double sample = vm.Session.Bounds.GetBounds(text).Width;

        vm.Preview.Data = new Dictionary<string, string> { ["name"] = "Alexandria Montgomery" };
        double record = vm.Session.Bounds.GetBounds(text).Width;
        Assert.True(record > sample * 3, $"record {record} should be much wider than sample {sample}");

        vm.Preview.Data = null;
        Assert.Equal(sample, vm.Session.Bounds.GetBounds(text).Width);
    }

    [Fact]
    public void PropertiesSubtitle_DescribesTheLabel_AndLeavesElementsToTheTag()
    {
        var vm = Create();
        Assert.True(vm.Properties.ShowsLabel);
        Assert.Equal("Size, margins and printer defaults", vm.Properties.Subtitle);
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        Assert.False(vm.Properties.ShowsLabel);
        Assert.Equal(string.Empty, vm.Properties.Subtitle);
    }

    [Fact]
    public void ClonerKeepsRawZplAndRecallElements()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new RawZplElement { Zpl = "^FO10,10^FDé^FS", IsVisual = true });
        doc.Elements.Add(new RecallPlaceholderElement { Command = "XG", Reference = "R:LOGO.GRF", Zpl = "^XGR:LOGO.GRF,1,1", EmitAsField = true });
        var copy = ElementCloner.CloneDocument(doc);
        Assert.Equal("^FO10,10^FDé^FS", ((RawZplElement)copy.Elements[0]).Zpl);
        Assert.Equal("R:LOGO.GRF", ((RecallPlaceholderElement)copy.Elements[1]).Reference);
    }

    [Fact]
    public void ImportedRawZpl_CanBeEdited_Measured_SavedAndReopened()
    {
        var vm = Create();
        vm.Session.Edit("Undo.Property", d =>
        {
            d.Elements.Add(new RawZplElement { X = 10, Y = 10, Zpl = "^FO10,10^GB100,40,3^FS", IsVisual = true });
            d.Elements.Add(new RecallPlaceholderElement { Command = "XG", Reference = "R:LOGO.GRF", Zpl = "^XGR:LOGO.GRF,1,1", EmitAsField = true });
            d.Elements.Add(new TextElement { Text = "after" });
        });
        // The canvas asks for the bounds of every element to draw handles and hit-test; none of them may throw.
        foreach (var e in vm.Session.Document.Elements) _ = vm.Session.Bounds.GetBounds(e);
        vm.Session.Edit("Undo.Property", d => d.Elements[2].X = 50);

        string path = Path.Combine(Path.GetTempPath(), "rotula-raw-" + Guid.NewGuid().ToString("N") + ".rotula");
        try
        {
            Rotula.App.Files.RotulaFileSerializer.Save(vm.Session.Document, path);
            Assert.Equal(Rotula.App.Files.RotulaFileSerializer.RawZplFormatVersion,
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("formatVersion").GetInt32());
            var back = Rotula.App.Files.RotulaFileSerializer.Load(path);
            Assert.Equal("^FO10,10^GB100,40,3^FS", ((RawZplElement)back.Elements[0]).Zpl);
            Assert.True(((RawZplElement)back.Elements[0]).IsVisual);
            Assert.Equal("R:LOGO.GRF", ((RecallPlaceholderElement)back.Elements[1]).Reference);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LabelsWithoutRawZpl_StayOnTheOldestFormatVersion()
    {
        var doc = new LabelDocument();
        doc.Elements.Add(new TextElement { Text = "x" });
        using var json = System.Text.Json.JsonDocument.Parse(Rotula.App.Files.RotulaFileSerializer.Serialize(doc));
        Assert.Equal(Rotula.App.Files.RotulaFileSerializer.BaseFormatVersion, json.RootElement.GetProperty("formatVersion").GetInt32());
    }

    // ------------------------------------------------------------------------------- driver detection in the printer settings

    private (PrinterSettingsViewModel Vm, LabelDocument Doc, List<PrinterDpi> Applied) Settings(PrinterDetection detection, Func<string, CancellationToken, Task<PrinterDetection>>? detect = null)
    {
        var doc = new LabelDocument { Width = Measure.Inches(4), Height = Measure.Inches(3), Dpi = PrinterDpi.Dpi203 };
        var applied = new List<PrinterDpi>();
        var vm = new PrinterSettingsViewModel(_fake.Printer, new PrintSettings(), () => { }, () => doc,
            detect ?? ((_, _) => Task.FromResult(detection)), d => { applied.Add(d); doc.Dpi = d; });
        return (vm, doc, applied);
    }

    [Fact]
    public async Task Detection_SetsTheDpi_ExplainsIt_AndOffersThePaperSize()
    {
        var (vm, _, _) = Settings(new PrinterDetection(PrinterDpi.Dpi300, Measure.Inches(4), Measure.Inches(6)));
        vm.WidthText = "100";
        vm.SelectedPrinterName = "ZD621";
        await vm.DetectAsync();
        Assert.Equal(PrinterDpi.Dpi300, vm.SelectedDpi);
        Assert.Equal("Detected 300 dpi from the printer driver", vm.DpiNote);
        Assert.Equal("Use the printer size (4 x 6 in)", vm.DetectedSizeText);
        Assert.Equal("100", vm.WidthText); // offered, not applied silently
        vm.UseDetectedSizeCommand.Execute(null);
        Assert.Equal("101.6", vm.WidthText.Replace(',', '.'));
        Assert.Equal("152.4", vm.HeightText.Replace(',', '.'));
    }

    [Fact]
    public async Task Detection_WithNothingRead_KeepsTheSelectorEditable_AndSaysSo()
    {
        var (vm, _, _) = Settings(PrinterDetection.None);
        vm.SelectedPrinterName = "Unknown";
        await vm.DetectAsync();
        Assert.Equal(PrinterDpi.Dpi203, vm.SelectedDpi);
        Assert.Contains("choose it manually", vm.DpiNote);
        Assert.False(vm.HasDetectedSize);
        Assert.False(vm.HasDpiMismatch);
    }

    [Fact]
    public async Task DpiMismatch_IsReported_AndOneClickSwitchesTheLabel()
    {
        var (vm, doc, applied) = Settings(new PrinterDetection(PrinterDpi.Dpi300, null, null));
        vm.SelectedPrinterName = "ZD621";
        await vm.DetectAsync();
        Assert.True(vm.HasDpiMismatch);
        Assert.Equal("Switch the label to 300 dpi", vm.SwitchLabelDpiText);
        vm.SwitchLabelDpiCommand.Execute(null);
        Assert.Equal(new[] { PrinterDpi.Dpi300 }, applied);
        Assert.Equal(PrinterDpi.Dpi300, doc.Dpi);
        Assert.False(vm.HasDpiMismatch);
    }

    [Fact]
    public async Task Detection_IsSkippedForNetworkPrinters_AndFailuresAreHandled()
    {
        int calls = 0;
        var (vm, _, _) = Settings(PrinterDetection.None, (_, _) => { calls++; throw new InvalidOperationException("driver"); });
        vm.UseNetwork = true;
        vm.SelectedPrinterName = "ignored";
        await vm.DetectAsync();
        Assert.Equal(0, calls);
        Assert.Equal(string.Empty, vm.DpiNote);
        vm.UseNetwork = false;
        await vm.DetectAsync();
        Assert.True(calls >= 1);
        Assert.Contains("choose it manually", vm.DpiNote);
    }

    [Fact]
    public async Task PrintDialog_UsesTheOpenDesignSize_NotAStoredOne()
    {
        var vm = Create();
        vm.ApplyLabelSize(Preset("Preset.4x3in"));
        vm.Printer.WidthText = "100";
        vm.Printer.HeightText = "50"; // an old stored size
        var dialog = new PrintViewModel(_fake.Printer, vm.Printer, () => vm.Session.Document, () => null, _fake.Build().Files, "x");
        Assert.Equal("101.6", dialog.Printer.WidthText.Replace(',', '.'));
        Assert.Equal("76.2", dialog.Printer.HeightText.Replace(',', '.'));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Detection_SurvivesAClearedSelectionBetweenTwoReads()
    {
        // The printer list is rebuilt when the dialog opens: the selection goes null and back, which starts a read, a clear and a read again.
        var (vm, _, _) = Settings(new PrinterDetection(PrinterDpi.Dpi300, null, null));
        vm.SelectedPrinterName = "ZD621";
        vm.SelectedPrinterName = null;
        vm.SelectedPrinterName = "ZD621";
        await vm.DetectAsync();
        Assert.Equal("Detected 300 dpi from the printer driver", vm.DpiNote);
        Assert.True(vm.HasDpiMismatch);
    }
}
