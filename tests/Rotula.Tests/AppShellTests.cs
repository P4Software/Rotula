using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>Texts and settings behind the main window chrome: title bar, toolbar, status bar, objects list and the ZPL panel toggle.</summary>
[Collection("App")]
public class AppShellTests : IDisposable
{
    private readonly FakeServices _fake = new();

    public AppShellTests() => _fake.Settings.Current.Language = "en";

    public void Dispose() => Loc.Instance.SetLanguage("en");

    [Fact]
    public void TheZplPanel_IsHiddenByDefault_AndTheChoiceIsRemembered()
    {
        Assert.False(new AppSettings().ZplPanelVisible);
        var vm = new MainViewModel(_fake.Build());
        Assert.False(vm.ZplPanelVisible);

        int saves = _fake.Settings.Saves;
        vm.ZplPanelVisible = true;
        Assert.True(_fake.Settings.Current.ZplPanelVisible);
        Assert.True(_fake.Settings.Saves > saves);

        // A new session starts the way the user left it.
        Assert.True(new MainViewModel(_fake.Build()).ZplPanelVisible);
        vm.ZplPanelVisible = false;
        Assert.False(new MainViewModel(_fake.Build()).ZplPanelVisible);
    }

    [Fact]
    public void TheZplText_IsStillKeptUpToDate_WhileThePanelIsHidden()
    {
        var vm = new MainViewModel(_fake.Build());
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        Assert.Contains("^GB", vm.ZplText); // Copy and Export work the moment the panel is shown
    }

    [Fact]
    public void TheTitleBar_ShowsTheFileNameWithItsExtension_AndAChangeMarker()
    {
        var vm = new MainViewModel(_fake.Build());
        Assert.Equal("Untitled \u2014 Rotula", vm.TitleBarText);
        _fake.SavePath = Path.Combine(Path.GetTempPath(), "shipping-label.rotula");
        try
        {
            vm.AddElementCommand.Execute(ToolboxKind.Box);
            Assert.StartsWith("\u2022 ", vm.TitleBarText);
            vm.SaveCommand.Execute(null);
            Assert.Equal("shipping-label.rotula \u2014 Rotula", vm.TitleBarText);
        }
        finally { File.Delete(_fake.SavePath); }
    }

    [Fact]
    public void TheStatusAndToolbarTexts_DescribeTheLabelAndSelection()
    {
        var doc = new LabelDocument { Width = Measure.Inches(4), Height = Measure.Inches(3), Dpi = PrinterDpi.Dpi203 };
        var vm = new MainViewModel(_fake.Build(), new Rotula.App.Editing.EditorSession(doc)) { Unit = LengthUnit.Inches };
        Assert.Equal("4.00 x 3.00 in \u00B7 203 dpi", vm.LabelSummaryText);
        Assert.Equal("4.00 x 3.00 in \u00B7 203 dpi \u00B7 ZPL", vm.StatusLabelText);
        Assert.Equal("0 objects \u00B7 none selected", vm.ObjectsText);

        vm.AddElementCommand.Execute(ToolboxKind.Text);
        vm.AddElementCommand.Execute(ToolboxKind.QrCode);
        Assert.Equal("2 objects \u00B7 1 selected", vm.ObjectsText);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal("2 objects \u00B7 2 selected", vm.ObjectsText);

        vm.Unit = LengthUnit.Millimeters;
        Assert.Equal("101.6 x 76.2 mm \u00B7 203 dpi", vm.LabelSummaryText);
    }

    [Fact]
    public void TheLabelSize_IsShownAsTyped_NotRoundedThroughDots()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(100), Height = Measure.Millimeters(60) };
        var vm = new MainViewModel(_fake.Build(), new Rotula.App.Editing.EditorSession(doc));
        Assert.StartsWith("100 x 60 mm", vm.LabelSummaryText);
    }

    [Fact]
    public void TheObjectsList_HasATypeTag_AndShowsTheContent()
    {
        var vm = new MainViewModel(_fake.Build());
        vm.Session.Edit("Undo.Property", d =>
        {
            d.Elements.Add(new TextElement { Text = "Ship to {{name}}" });
            d.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = "ABC123" });
            d.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "https://example.com" });
            d.Elements.Add(new LineElement());
        });
        var byTag = vm.Layers.ToDictionary(l => l.TypeTag);
        Assert.Equal("Ship to {{name}}", byTag["Text"].Primary);
        Assert.Equal("ABC123", vm.Layers.Single(l => l.Detail == "ABC123").Primary);
        Assert.Contains("QR", string.Join(",", vm.Layers.Select(l => l.TypeTag)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Line", byTag.Keys);
        Assert.Equal("Line", byTag["Line"].Primary.Length > 0 ? "Line" : "");
    }

    [Fact]
    public void ThePropertiesTag_FollowsTheSelection()
    {
        var vm = new MainViewModel(_fake.Build());
        Assert.Equal("Label", vm.SelectionTypeTag);
        vm.AddElementCommand.Execute(ToolboxKind.Barcode);
        Assert.Equal(MainViewModel.TypeTagOf(vm.Session.SelectedElements[0]), vm.SelectionTypeTag);
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        vm.SelectAllCommand.Execute(null);
        Assert.Contains("2", vm.SelectionTypeTag);
    }

    [Fact]
    public void ZoomPresets_SetTheZoom()
    {
        var vm = new MainViewModel(_fake.Build());
        Assert.Contains(150, vm.ZoomPresets);
        vm.SetZoomPercent(150);
        Assert.Equal(150, vm.Zoom.Percent);
    }

    [Fact]
    public void TheShellTexts_AreTranslated()
    {
        var vm = new MainViewModel(_fake.Build());
        Loc.Instance.SetLanguage("es");
        Assert.Contains("ninguno seleccionado", vm.ObjectsText);
        Assert.StartsWith("Sin t\u00EDtulo", vm.TitleBarText);
        Assert.Equal("Mostrar c\u00F3digo ZPL", Loc.Get("Menu.ShowZpl"));
    }

    [Fact]
    public void ThePreviewCommand_OpensThePreviewDialog()
    {
        var vm = new MainViewModel(_fake.Build());
        vm.ShowPreviewCommand.Execute(null);
        Assert.Equal(1, _fake.ToolDialogs.Previews);
    }
}
