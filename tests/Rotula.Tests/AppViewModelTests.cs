using Rotula.App.Editing;
using Rotula.App.Files;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering.View;

namespace Rotula.Tests;

[Collection("App")]
public class AppViewModelTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();

    public AppViewModelTests()
    {
        _fake.Settings.Current.Language = "en";
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    private MainViewModel Create() => new(_fake.Build());

    private string Path_(string name) => Path.Combine(_dir, name);

    // ------------------------------------------------------------------------------- files

    [Fact]
    public void New_AsksForSize_ReplacesLabel_AndStartsClean()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        _fake.SaveAnswer = SaveChangesAnswer.Discard;
        _fake.NewLabel = new LabelDocument { Width = Measure.Inches(2), Height = Measure.Inches(1) };
        vm.NewCommand.Execute(null);
        Assert.Equal(1, _fake.SaveQuestions);
        Assert.Empty(vm.Session.Document.Elements);
        Assert.Equal(Measure.Inches(2), vm.Session.Document.Width);
        Assert.False(vm.Session.IsDirty);
        Assert.Null(vm.FilePath);
    }

    [Fact]
    public void New_Cancelled_KeepsTheCurrentLabel()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        _fake.SaveAnswer = SaveChangesAnswer.Cancel;
        vm.NewCommand.Execute(null);
        Assert.Single(vm.Session.Document.Elements);
    }

    [Fact]
    public void Save_WithoutAName_AsksWhere_WritesFile_UpdatesTitleAndRecent()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Text);
        Assert.StartsWith("* ", vm.Title);
        _fake.SavePath = Path_("mine.rotula");
        vm.SaveCommand.Execute(null);

        Assert.True(File.Exists(_fake.SavePath));
        Assert.Equal("mine - Rotula", vm.Title);
        Assert.False(vm.Session.IsDirty);
        Assert.Equal(_fake.SavePath, vm.FilePath);
        Assert.Equal(new[] { _fake.SavePath }, vm.RecentFiles.Select(r => r.Path));
        Assert.Equal(new[] { _fake.SavePath }, _fake.Settings.Current.RecentFiles);
    }

    [Fact]
    public void Save_Cancelled_WritesNothing()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Text);
        _fake.SavePath = null;
        vm.SaveCommand.Execute(null);
        Assert.True(vm.Session.IsDirty);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_ToAnUnwritableLocation_ShowsAPlainMessage_AndStaysDirty()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Text);
        // A file used as a folder name cannot be created.
        File.WriteAllText(Path_("blocker"), "x");
        _fake.SavePath = Path.Combine(Path_("blocker"), "label.rotula");
        vm.SaveCommand.Execute(null);
        Assert.Single(_fake.Errors);
        Assert.Contains("could not be written", _fake.Errors[0].Message);
        Assert.True(vm.Session.IsDirty);
    }

    [Fact]
    public void Open_RoundTripsADocument_AndMovesFileToTheTopOfRecent()
    {
        var doc = AppTestSupport.RichDocument();
        RotulaFileSerializer.Save(doc, Path_("a.rotula"));
        RotulaFileSerializer.Save(doc, Path_("b.rotula"));
        var vm = Create();
        _fake.OpenPath = Path_("a.rotula");
        vm.OpenCommand.Execute(null);
        _fake.OpenPath = Path_("b.rotula");
        vm.OpenCommand.Execute(null);
        _fake.OpenPath = Path_("a.rotula");
        vm.OpenCommand.Execute(null);

        Assert.Equal(7, vm.Session.Document.Elements.Count);
        Assert.Equal(new[] { "a.rotula", "b.rotula" }, vm.RecentFiles.Select(r => r.Display));
        Assert.False(vm.Session.IsDirty);
    }

    [Fact]
    public void Open_DamagedFile_ExplainsWhatToDo_AndKeepsTheCurrentLabel()
    {
        File.WriteAllText(Path_("bad.rotula"), "{ this is not valid");
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        _fake.SaveAnswer = SaveChangesAnswer.Discard;
        _fake.OpenPath = Path_("bad.rotula");
        vm.OpenCommand.Execute(null);
        Assert.Single(_fake.Errors);
        Assert.Contains("bad.rotula", _fake.Errors[0].Message);
        Assert.Contains("backup", _fake.Errors[0].Message);
        Assert.Single(vm.Session.Document.Elements);
    }

    [Fact]
    public void Open_MissingFile_ReportsAndForgetsItFromRecent()
    {
        var vm = Create();
        RotulaFileSerializer.Save(new LabelDocument(), Path_("gone.rotula"));
        Assert.True(vm.LoadFile(Path_("gone.rotula")));
        File.Delete(Path_("gone.rotula"));
        Assert.False(vm.LoadFile(Path_("gone.rotula")));
        Assert.Single(_fake.Errors);
        Assert.Empty(vm.RecentFiles);
    }

    [Fact]
    public void RecentFiles_AreCappedAtTen()
    {
        var vm = Create();
        for (int i = 0; i < 12; i++)
        {
            RotulaFileSerializer.Save(new LabelDocument(), Path_($"f{i}.rotula"));
            vm.LoadFile(Path_($"f{i}.rotula"));
        }
        Assert.Equal(10, vm.RecentFiles.Count);
        Assert.Equal("f11.rotula", vm.RecentFiles[0].Display);
    }

    [Fact]
    public void ConfirmClose_PromptsOnlyWhenDirty()
    {
        var vm = Create();
        Assert.True(vm.ConfirmClose());
        Assert.Equal(0, _fake.SaveQuestions);
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        _fake.SaveAnswer = SaveChangesAnswer.Cancel;
        Assert.False(vm.ConfirmClose());
        _fake.SaveAnswer = SaveChangesAnswer.Discard;
        Assert.True(vm.ConfirmClose());
    }

    // ------------------------------------------------------------------------------- export / import / print

    [Fact]
    public void ExportPng_And_Pdf_WriteRealFiles()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Text);
        _fake.ExportPath = Path_("out.png");
        vm.ExportPngCommand.Execute(null);
        _fake.ExportPath = Path_("out.pdf");
        vm.ExportPdfCommand.Execute(null);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(Path_("out.png")).Take(4));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path_("out.pdf")), 0, 4));
        Assert.Empty(_fake.Errors);
    }

    [Fact]
    public void ExportZpl_WritesGeneratedCode_ButRefusesWhenTheLabelHasErrors()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Barcode);
        _fake.ExportPath = Path_("out.zpl");
        vm.ExportZplCommand.Execute(null);
        string zpl = File.ReadAllText(Path_("out.zpl"));
        Assert.StartsWith("^XA", zpl);
        Assert.Contains("^BC", zpl);

        // An empty barcode is an error: nothing may be written, and the message says what to fix.
        File.Delete(Path_("out.zpl"));
        vm.Session.Edit("Undo.Property", d => ((BarcodeElement)d.Elements[0]).Data = "");
        vm.ExportZplCommand.Execute(null);
        Assert.False(File.Exists(Path_("out.zpl")));
        Assert.Single(_fake.Errors);
        Assert.Contains("no data", _fake.Errors[0].Message);
    }

    [Fact]
    public void ImportZpl_WithTheStub_SaysItIsNotAvailableYet_AndChangesNothing()
    {
        File.WriteAllText(Path_("in.zpl"), "^XA^FO10,10^FDHi^FS^XZ");
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        _fake.SaveAnswer = SaveChangesAnswer.Discard;
        _fake.ZplPath = Path_("in.zpl");
        vm.ImportZplCommand.Execute(null);
        Assert.Single(_fake.Errors);
        Assert.Contains("not available", _fake.Errors[0].Message);
        Assert.Single(vm.Session.Document.Elements);
    }

    [Fact]
    public void ImportZpl_WithAnImporter_LoadsTheResultAsAnUnsavedLabel()
    {
        File.WriteAllText(Path_("in.zpl"), "^XA^XZ");
        var imported = new LabelDocument();
        imported.Elements.Add(new TextElement { Text = "from zpl" });
        _fake.Importer = new FixedImporter(ZplImportResult.Success(imported, new[] { "note" }));
        _fake.ZplPath = Path_("in.zpl");
        var vm = Create();
        vm.ImportZplCommand.Execute(null);
        Assert.Single(vm.Session.Document.Elements);
        Assert.True(vm.Session.IsDirty); // it has no file yet
        Assert.Null(vm.FilePath);
        Assert.Single(_fake.Infos);
    }

    [Fact]
    public async Task Print_WithTheStub_ExplainsPrintingIsNotConnected()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Text);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Single(_fake.Infos);
        Assert.Contains("not connected", _fake.Infos[0].Message);
    }

    [Fact]
    public async Task Print_PassesTheGeneratedZplToTheService()
    {
        var printer = new RecordingPrinter();
        _fake.Printer = printer;
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.StartsWith("^XA", printer.Zpl);
        Assert.Contains("^GB", printer.Zpl);
    }

    // ------------------------------------------------------------------------------- editing through the VM

    [Fact]
    public void ZplText_FollowsTheDocument_AndCopyPutsItOnTheClipboard()
    {
        var vm = Create();
        Assert.StartsWith("^XA", vm.ZplText);
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        Assert.Contains("^GB", vm.ZplText);
        vm.CopyZplCommand.Execute(null);
        Assert.Equal(vm.ZplText, _fake.ClipboardText);
    }

    [Fact]
    public void UndoRedoCommands_AndTheirMenuText_FollowTheHistory()
    {
        var vm = Create();
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Undo", vm.UndoText);
        vm.AddElementCommand.Execute(ToolboxKind.Circle);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Undo add", vm.UndoText);
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Session.Document.Elements);
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.Equal("Redo add", vm.RedoText);
        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Session.Document.Elements);
    }

    [Fact]
    public void CopyCutPaste_GoThroughTheClipboardService()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        vm.CopyCommand.Execute(null);
        Assert.Contains("rotulaElements", _fake.ClipboardText);
        vm.PasteCommand.Execute(null);
        Assert.Equal(2, vm.Session.Document.Elements.Count);
        vm.CutCommand.Execute(null); // cuts the pasted copy (it is the selection)
        Assert.Single(vm.Session.Document.Elements);
        vm.PasteCommand.Execute(null);
        Assert.Equal(2, vm.Session.Document.Elements.Count);
    }

    [Fact]
    public void Paste_WithForeignClipboardText_SaysSoInTheStatusBar()
    {
        _fake.ClipboardText = "plain words";
        var vm = Create();
        vm.PasteCommand.Execute(null);
        Assert.Contains("nothing to paste", vm.StatusMessage);
    }

    [Fact]
    public void CommandStates_FollowTheSelection()
    {
        var vm = Create();
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        Assert.True(vm.DeleteCommand.CanExecute(null));
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        Assert.False(vm.DistributeCommand.CanExecute(DistributeKind.Horizontal)); // needs three
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.DistributeCommand.CanExecute(DistributeKind.Horizontal));
    }

    [Fact]
    public void Nudge_UsesOneDot_AndShiftEightDots()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        int x0 = vm.Session.Document.Elements[0].X, y0 = vm.Session.Document.Elements[0].Y;
        vm.NudgeBy(1, 0, large: false);
        Assert.Equal(x0 + 1, vm.Session.Document.Elements[0].X);
        vm.NudgeBy(0, -1, large: true);
        Assert.Equal(y0 - MainViewModel.NudgeLarge, vm.Session.Document.Elements[0].Y);
        Assert.Equal(8, MainViewModel.NudgeLarge);
    }

    [Fact]
    public void AddDataField_DeclaresAVariable_InTheSameUndoStep()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.DataField);
        Assert.Single(vm.Session.Document.Variables);
        Assert.Equal(vm.Session.Document.Variables[0].Name, ((DataFieldElement)vm.Session.Document.Elements[0]).VariableName);
        Assert.DoesNotContain(vm.Session.Document.Variables, v => v.Name == "x");
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Session.Document.Variables);
    }

    [Fact]
    public void AddImage_LoadsThroughTheImageService_AndFailsPolitely()
    {
        var vm = Create();
        _fake.ImagePath = Path_("pic.png");
        vm.AddElementCommand.Execute(ToolboxKind.Image);
        var img = Assert.IsType<ImageElement>(vm.Session.Document.Elements[0]);
        Assert.NotNull(img.Source);

        _fake.Image = null; // loader throws InvalidDataException
        vm.AddElementCommand.Execute(ToolboxKind.Image);
        Assert.Single(vm.Session.Document.Elements);
        Assert.Single(_fake.Errors);
        Assert.Contains("pic.png", _fake.Errors[0].Message);
    }

    [Fact]
    public void SelectNextPrevious_CyclesThroughElements_ForKeyboardUsers()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        vm.AddElementCommand.Execute(ToolboxKind.Line);
        vm.SelectNoneCommand.Execute(null);
        vm.SelectNextCommand.Execute(null);
        Assert.Equal(vm.Session.Document.Elements[0].Id, vm.Session.SelectedIds[0]);
        vm.SelectNextCommand.Execute(null);
        vm.SelectNextCommand.Execute(null);
        Assert.Equal(vm.Session.Document.Elements[0].Id, vm.Session.SelectedIds[0]); // wrapped around
        vm.SelectPreviousCommand.Execute(null);
        Assert.Equal(vm.Session.Document.Elements[1].Id, vm.Session.SelectedIds[0]);
    }

    [Fact]
    public void Layers_ListFrontFirst_AndSelectionFromTheListReachesTheSession()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Box);
        vm.AddElementCommand.Execute(ToolboxKind.Circle);
        Assert.Equal(2, vm.Layers.Count);
        Assert.Equal(vm.Session.Document.Elements[1].Id, vm.Layers[0].Id);
        vm.SelectLayers(new[] { vm.Layers[1].Id });
        Assert.Equal(new[] { vm.Layers[1].Id }, vm.Session.SelectedIds);
    }

    [Fact]
    public void Problems_AreCounted_ForTheStatusBar()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(ToolboxKind.Barcode);
        Assert.Equal(0, vm.ErrorCount);
        vm.Session.Edit("Undo.Property", d => ((BarcodeElement)d.Elements[0]).Data = "");
        Assert.Equal(1, vm.ErrorCount);
        Assert.Contains("1 errors", vm.ProblemsText);
    }

    // ------------------------------------------------------------------------------- view state

    [Fact]
    public void Zoom_StepsStayInsideTenToEightHundredPercent()
    {
        var vm = Create();
        for (int i = 0; i < 30; i++) vm.ZoomInCommand.Execute(null);
        Assert.Equal(800, vm.Zoom.Percent);
        for (int i = 0; i < 30; i++) vm.ZoomOutCommand.Execute(null);
        Assert.Equal(10, vm.Zoom.Percent);
        vm.ZoomActualSizeCommand.Execute(null);
        Assert.Equal("100 %", vm.ZoomText);
    }

    [Fact]
    public void ViewSettings_ArePersisted()
    {
        var vm = Create();
        vm.SetUnitCommand.Execute(LengthUnit.Inches);
        vm.GridVisible = false;
        vm.SnapToGuides = false;
        Assert.Equal("Inches", _fake.Settings.Current.Unit);
        Assert.False(_fake.Settings.Current.GridVisible);
        Assert.False(_fake.Settings.Current.SnapToGuides);

        var again = Create(); // a new window reads them back
        Assert.Equal(LengthUnit.Inches, again.Unit);
        Assert.False(again.GridVisible);
    }

    [Fact]
    public void CursorText_ShowsPositionInTheChosenUnit()
    {
        var vm = Create();
        vm.SetCursorDots(203, 406);
        Assert.Contains("25.4", vm.CursorText);
        Assert.EndsWith("mm", vm.CursorText);
        vm.SetUnitCommand.Execute(LengthUnit.Inches);
        vm.SetCursorDots(203, 406);
        Assert.EndsWith("in", vm.CursorText);
        vm.SetCursorDots(null, null);
        Assert.Equal(string.Empty, vm.CursorText);
    }

    [Fact]
    public void Language_StartsFromSettings_SwitchesLive_AndIsSaved()
    {
        _fake.Settings.Current.Language = "es";
        var vm = Create();
        Assert.Equal("es", Loc.Instance.LanguageCode);
        Assert.StartsWith("Sin título", vm.Title);
        Assert.Contains("aproximada", vm.ApproximateNotice);

        vm.SetLanguageCommand.Execute("en");
        Assert.Equal("en", Loc.Instance.LanguageCode);
        Assert.StartsWith("Untitled", vm.Title);
        Assert.Equal("en", _fake.Settings.Current.Language);
        Assert.Equal("Text", vm.Toolbox[0].Name);

        vm.SetLanguageCommand.Execute("es");
        Assert.Equal("Texto", vm.Toolbox[0].Name);
        Assert.True(vm.IsLanguageEs);
    }

    [Fact]
    public void ApproximatePreviewNotice_IsAlwaysAvailable()
    {
        var vm = Create();
        Assert.Contains("approximate", vm.ApproximateNotice);
    }

    // ------------------------------------------------------------------------------- dialog view-model

    [Fact]
    public void NewLabelPresets_CoverTheRequiredSizes_AndFillTheBoxes()
    {
        var names = LabelPreset.All.Select(p => p.NameKey).ToArray();
        foreach (var n in new[] { "Preset.4x6in", "Preset.4x3in", "Preset.2x1in", "Preset.100x150mm", "Preset.100x50mm" })
            Assert.Contains(n, names);

        var vm = new NewLabelViewModel { SelectedPreset = LabelPreset.All.First(p => p.NameKey == "Preset.100x50mm") };
        var doc = vm.TryCreate()!;
        Assert.Equal(Measure.Millimeters(100), doc.Width);
        Assert.Equal(Measure.Millimeters(50), doc.Height);
        Assert.False(vm.IsCustom);

        vm.SelectedPreset = LabelPreset.All.First(p => p.NameKey == "Preset.4x6in");
        Assert.Equal(Measure.Inches(4), vm.TryCreate()!.Width);
    }

    [Fact]
    public void NewLabelDialog_RejectsBadSizes_WithAMessage()
    {
        var vm = new NewLabelViewModel { SelectedPreset = LabelPreset.All[^1], WidthText = "abc", Unit = LengthUnit.Millimeters };
        Assert.Null(vm.TryCreate());
        Assert.Contains("not a number", vm.Error);
        vm.WidthText = "100000";
        vm.HeightText = "10";
        Assert.Null(vm.TryCreate());
        Assert.Contains("9999", vm.Error);
        vm.WidthText = "50";
        Assert.NotNull(vm.TryCreate());
        Assert.Equal(string.Empty, vm.Error);
    }

    private sealed class FixedImporter(ZplImportResult result) : IZplImporter
    {
        public ZplImportResult Import(byte[] zpl) => result;
    }

    private sealed class RecordingPrinter : IPrintService
    {
        public string? Zpl;
        public Task<PrintOutcome> PrintAsync(LabelDocument document, string zpl)
        {
            Zpl = zpl;
            return Task.FromResult(new PrintOutcome(true, null));
        }
    }
}
