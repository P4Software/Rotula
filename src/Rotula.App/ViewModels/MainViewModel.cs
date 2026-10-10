using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Files;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using Rotula.Rendering.View;

namespace Rotula.App.ViewModels;

/// <summary>An entry of the layers list.</summary>
/// <param name="Id">Element id.</param>
/// <param name="Name">Display name.</param>
public sealed record LayerItem(string Id, string Name);

/// <summary>An entry of the recent files menu.</summary>
/// <param name="Path">Full path.</param>
public sealed record RecentFileItem(string Path)
{
    /// <summary>File name shown in the menu.</summary>
    public string Display => System.IO.Path.GetFileName(Path);
}

/// <summary>An entry of the element toolbox.</summary>
/// <param name="Kind">What it creates.</param>
/// <param name="NameKey">Resource key of the name.</param>
/// <param name="Glyph">A short symbol drawn on the button.</param>
public sealed record ToolboxItem(ToolboxKind Kind, string NameKey, string Glyph)
{
    /// <summary>Translated name.</summary>
    public string Name => Loc.Instance[NameKey];
}

/// <summary>The services the main view-model depends on.</summary>
public sealed record MainServices(
    IFileDialogService Files,
    IMessageService Messages,
    IClipboardService Clipboard,
    IImageLoader Images,
    INewLabelDialogService NewLabelDialog,
    IZplImporter ZplImporter,
    IPrintService Printing,
    ISettingsStore Settings);

/// <summary>
/// View-model of the main window: the open label, its files, editing commands, view settings (zoom, unit, grid),
/// derived data (ZPL, validation, layers) and the status bar. Everything that touches Windows goes through
/// <see cref="MainServices"/>, so this class is fully testable.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Arrow-key nudge in dots.</summary>
    public const int NudgeSmall = 1;

    /// <summary>Shift + arrow nudge in dots (about 1 mm at 203 DPI).</summary>
    public const int NudgeLarge = 8;

    private const int MaxRecent = 10;

    private readonly MainServices _svc;
    private readonly AppSettings _settings;
    private readonly List<IRelayCommand> _allCommands = new();
    private bool _syncingSelection;

    /// <summary>Creates the view-model and loads settings.</summary>
    public MainViewModel(MainServices services, EditorSession? session = null)
    {
        _svc = services;
        _settings = services.Settings.Load();
        Loc.Instance.SetLanguage(_settings.Language);
        Session = session ?? new EditorSession();
        Commands = new EditorCommands(Session);
        Properties = new PropertyPanelViewModel(Session, ChooseImageFor);
        Preview = new PreviewCache(Session);

        _unit = Enum.TryParse<LengthUnit>(_settings.Unit, out var u) ? u : LengthUnit.Millimeters;
        _gridVisible = _settings.GridVisible;
        _snapToGrid = _settings.SnapToGrid;
        _snapToGuides = _settings.SnapToGuides;
        foreach (var p in _settings.RecentFiles.Take(MaxRecent)) RecentFiles.Add(new RecentFileItem(p));

        FillToolbox();

        Session.DocumentChanged += (_, _) => OnDocumentChanged();
        Session.SelectionChanged += (_, _) => OnSelectionChanged();
        Session.DirtyChanged += (_, _) => OnDirtyChanged();
        Session.Undo.Changed += (_, _) => RefreshCommandStates();
        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        foreach (var p in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.PropertyType.IsAssignableTo(typeof(IRelayCommand)) && p.GetValue(this) is IRelayCommand c)
                _allCommands.Add(c);

        OnDocumentChanged();
        OnSelectionChanged();
        UpdateTitle();
    }

    // ----------------------------------------------------------------------------------- state

    /// <summary>The editing session (document, selection, undo).</summary>
    public EditorSession Session { get; }

    /// <summary>Editing operations on the session.</summary>
    public EditorCommands Commands { get; }

    /// <summary>The property panel.</summary>
    public PropertyPanelViewModel Properties { get; }

    /// <summary>Cache of the rendered preview.</summary>
    public PreviewCache Preview { get; }

    /// <summary>The element toolbox. Rebuilt when the language changes so the names are translated.</summary>
    public ObservableCollection<ToolboxItem> Toolbox { get; } = new();

    private void FillToolbox()
    {
        Toolbox.Clear();
        Toolbox.Add(new ToolboxItem(ToolboxKind.Text, "Tool.Text", "T"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.DataField, "Tool.DataField", "{}"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Barcode, "Tool.Barcode", "|||"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.QrCode, "Tool.QrCode", "QR"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Box, "Tool.Box", "▭"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Line, "Tool.Line", "─"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Circle, "Tool.Circle", "◯"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Image, "Tool.Image", "▨"));
    }

    /// <summary>Layers, front-most first.</summary>
    public ObservableCollection<LayerItem> Layers { get; } = new();

    /// <summary>Recently used files, newest first.</summary>
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();

    /// <summary>Path of the file the label was opened from or saved to; null for a label that has not been saved yet.</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>Window title.</summary>
    [ObservableProperty]
    private string _title = "Rotula";

    /// <summary>The generated ZPL (read-only code view).</summary>
    [ObservableProperty]
    private string _zplText = string.Empty;

    /// <summary>Status bar message about the last action.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Status bar text about the selection.</summary>
    [ObservableProperty]
    private string _selectionText = string.Empty;

    /// <summary>Status bar text about the pointer position.</summary>
    [ObservableProperty]
    private string _cursorText = string.Empty;

    /// <summary>Status bar text about problems in the label.</summary>
    [ObservableProperty]
    private string _problemsText = string.Empty;

    /// <summary>Number of errors in the label (they block ZPL export and printing).</summary>
    [ObservableProperty]
    private int _errorCount;

    /// <summary>The notice that the on-screen text preview is approximate.</summary>
    public string ApproximateNotice => Loc.Get("Preview.Approximate");

    /// <summary>Current zoom.</summary>
    [ObservableProperty]
    private ZoomModel _zoom = new(1.0);

    /// <summary>Zoom as text for the status bar.</summary>
    public string ZoomText => Zoom.Percent.ToString(CultureInfo.CurrentCulture) + " %";

    partial void OnZoomChanged(ZoomModel value) => OnPropertyChanged(nameof(ZoomText));

    /// <summary>Unit of the rulers and the pointer position.</summary>
    [ObservableProperty]
    private LengthUnit _unit;

    partial void OnUnitChanged(LengthUnit value)
    {
        _settings.Unit = value.ToString();
        SaveSettings();
        OnPropertyChanged(nameof(IsUnitMm));
        OnPropertyChanged(nameof(IsUnitInch));
        OnPropertyChanged(nameof(IsUnitDots));
    }

    /// <summary>Unit is millimetres (menu check state).</summary>
    public bool IsUnitMm => Unit == LengthUnit.Millimeters;
    /// <summary>Unit is inches (menu check state).</summary>
    public bool IsUnitInch => Unit == LengthUnit.Inches;
    /// <summary>Unit is dots (menu check state).</summary>
    public bool IsUnitDots => Unit == LengthUnit.Dots;

    /// <summary>Grid is drawn.</summary>
    [ObservableProperty]
    private bool _gridVisible;

    partial void OnGridVisibleChanged(bool value) { _settings.GridVisible = value; SaveSettings(); }

    /// <summary>Dragging snaps to the grid.</summary>
    [ObservableProperty]
    private bool _snapToGrid;

    partial void OnSnapToGridChanged(bool value) { _settings.SnapToGrid = value; SaveSettings(); }

    /// <summary>Dragging snaps to other elements and to the label edges and centre.</summary>
    [ObservableProperty]
    private bool _snapToGuides;

    partial void OnSnapToGuidesChanged(bool value) { _settings.SnapToGuides = value; SaveSettings(); }

    /// <summary>Grid configuration for the canvas.</summary>
    public GridSettings Grid => new(LengthUnit.Millimeters, _settings.GridSpacingMm, GridVisible, SnapToGrid);

    /// <summary>The language setting: "auto", "en" or "es".</summary>
    public string LanguageSetting => _settings.Language;

    /// <summary>Menu check state for the language entries.</summary>
    public bool IsLanguageAuto => LanguageSetting == "auto";
    /// <summary>Menu check state for English.</summary>
    public bool IsLanguageEn => LanguageSetting == "en";
    /// <summary>Menu check state for Spanish.</summary>
    public bool IsLanguageEs => LanguageSetting == "es";

    /// <summary>Raised when the view should zoom so the whole label fits.</summary>
    public event EventHandler? ZoomToFitRequested;

    /// <summary>Raised when the application should close.</summary>
    public event EventHandler? ExitRequested;

    // ----------------------------------------------------------------------------------- derived data

    private ZplGenerationResult? _zplResult;

    private void OnDocumentChanged()
    {
        try
        {
            _zplResult = ZplGenerator.Generate(Session.Document);
            ZplText = _zplResult.Zpl;
            Properties.SetIssues(_zplResult.Issues);
            ErrorCount = _zplResult.Issues.Count(i => i.Severity == ValidationSeverity.Error);
            int warnings = _zplResult.Issues.Count - ErrorCount;
            ProblemsText = ErrorCount == 0 && warnings == 0 ? Loc.Get("Status.NoProblems") : Loc.Get("Status.Problems", ErrorCount, warnings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The ZPL view must never take the editor down; say what happened instead.
            _zplResult = null;
            ZplText = string.Empty;
            ProblemsText = Loc.Get("Status.ZplFailed");
        }

        // Rebuilding the list makes the list box report "selection cleared"; that must not reach the real selection.
        _syncingSelection = true;
        try
        {
            Layers.Clear();
            foreach (var e in Session.Document.Elements.AsEnumerable().Reverse())
                Layers.Add(new LayerItem(e.Id, PropertyPanelViewModel.ElementDisplayName(e)));
        }
        finally { _syncingSelection = false; }
        UpdateTitle();
        RefreshCommandStates();
    }

    private void OnSelectionChanged()
    {
        int n = Session.SelectedIds.Count;
        SelectionText = n == 0 ? Loc.Get("Status.NothingSelected") : n == 1 ? Loc.Get("Status.OneSelected") : Loc.Get("Status.ManySelected", n);
        RefreshCommandStates();
    }

    private void OnDirtyChanged()
    {
        UpdateTitle();
        RefreshCommandStates();
    }

    private void OnLanguageChanged()
    {
        FillToolbox();
        OnDocumentChanged();
        OnSelectionChanged();
        OnPropertyChanged(nameof(ApproximateNotice));
        OnPropertyChanged(nameof(LanguageSetting));
        OnPropertyChanged(nameof(IsLanguageAuto));
        OnPropertyChanged(nameof(IsLanguageEn));
        OnPropertyChanged(nameof(IsLanguageEs));
    }

    /// <summary>The label name used in titles and prompts.</summary>
    public string DocumentName => FilePath is null ? Loc.Get("Document.Untitled") : Path.GetFileNameWithoutExtension(FilePath);

    private void UpdateTitle()
    {
        Title = (Session.IsDirty ? "* " : string.Empty) + DocumentName + " - Rotula";
        OnPropertyChanged(nameof(DocumentName));
    }

    partial void OnFilePathChanged(string? value) => UpdateTitle();

    private void RefreshCommandStates()
    {
        foreach (var c in _allCommands) c.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
    }

    /// <summary>Text of the Undo menu entry, for example "Undo Move".</summary>
    public string UndoText => Session.Undo.NextUndoKey is { } k ? Loc.Get("Menu.UndoWhat", Loc.Get(k)) : Loc.Get("Menu.Undo");

    /// <summary>Text of the Redo menu entry.</summary>
    public string RedoText => Session.Undo.NextRedoKey is { } k ? Loc.Get("Menu.RedoWhat", Loc.Get(k)) : Loc.Get("Menu.Redo");

    private bool HasSelection() => Session.SelectedIds.Count > 0;
    private bool HasMany() => Session.SelectedIds.Count > 1;
    private bool HasThree() => Session.SelectedIds.Count > 2;

    /// <summary>Shows the pointer position in the status bar. Pass null when the pointer leaves the label.</summary>
    public void SetCursorDots(double? xDots, double? yDots)
    {
        if (xDots is null || yDots is null) { CursorText = string.Empty; return; }
        int dpi = (int)Session.Document.Dpi;
        string f(double v) => UnitConverter.Convert(v, LengthUnit.Dots, Unit, dpi).ToString(Unit == LengthUnit.Dots ? "0" : "0.0#", CultureInfo.CurrentCulture);
        string suffix = Unit switch { LengthUnit.Millimeters => "mm", LengthUnit.Inches => "in", _ => "dots" };
        CursorText = $"X {f(xDots.Value)}  Y {f(yDots.Value)} {suffix}";
    }

    // ----------------------------------------------------------------------------------- file commands

    private bool EnsureSaved()
    {
        if (!Session.IsDirty) return true;
        switch (_svc.Messages.AskSaveChanges(DocumentName))
        {
            case SaveChangesAnswer.Save: return SaveCore();
            case SaveChangesAnswer.Discard: return true;
            default: return false;
        }
    }

    /// <summary>Asks to save unsaved changes; returns false when the user cancelled (the window must stay open).</summary>
    public bool ConfirmClose() => EnsureSaved();

    /// <summary>Creates a new label from the New dialog.</summary>
    [RelayCommand]
    private void New()
    {
        if (!EnsureSaved()) return;
        var doc = _svc.NewLabelDialog.ShowNewLabelDialog();
        if (doc is null) return;
        OpenDocument(doc, null);
        StatusMessage = Loc.Get("Status.NewCreated");
    }

    /// <summary>Opens a .rotula file chosen in a dialog.</summary>
    [RelayCommand]
    private void Open()
    {
        if (!EnsureSaved()) return;
        string? path = _svc.Files.PickOpenLabel();
        if (path is not null) LoadFile(path);
    }

    /// <summary>Opens a file from the recent list.</summary>
    [RelayCommand]
    private void OpenRecent(RecentFileItem? item)
    {
        if (item is null || !EnsureSaved()) return;
        LoadFile(item.Path);
    }

    /// <summary>Opens a .rotula file (also used for files given on the command line or dropped on the window).</summary>
    public bool LoadFile(string path)
    {
        try
        {
            var doc = RotulaFileSerializer.Load(path);
            OpenDocument(doc, path);
            AddRecent(path);
            StatusMessage = Loc.Get("Status.Opened", Path.GetFileName(path));
            return true;
        }
        catch (RotulaFileException ex)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.OpenFailed"), Loc.Get(ex.MessageKey, ex.Args.Cast<object>().Prepend(Path.GetFileName(path)).ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.OpenFailed"), Loc.Get("File.Error.Read", Path.GetFileName(path)));
            RemoveRecent(path);
        }
        return false;
    }

    private void OpenDocument(LabelDocument doc, string? path, bool markDirty = false)
    {
        Session.Load(doc, markDirty);
        FilePath = path;
        Zoom = new ZoomModel(1.0);
        ZoomToFitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves; asks for a name first when the label has none. Returns false when cancelled or failed.</summary>
    [RelayCommand]
    private void Save() => SaveCore();

    /// <summary>Saves under a new name.</summary>
    [RelayCommand]
    private void SaveAs() => SaveAsCore();

    private bool SaveCore() => FilePath is null ? SaveAsCore() : SaveTo(FilePath);

    private bool SaveAsCore()
    {
        string? path = _svc.Files.PickSaveLabel(DocumentName + ".rotula");
        return path is not null && SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            RotulaFileSerializer.Save(Session.Document, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.SaveFailed"), Loc.Get("File.Error.Write", path));
            return false;
        }
        FilePath = path;
        Session.MarkSaved();
        AddRecent(path);
        StatusMessage = Loc.Get("Status.Saved", Path.GetFileName(path));
        return true;
    }

    private void AddRecent(string path)
    {
        path = Path.GetFullPath(path);
        var existing = RecentFiles.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentFiles.Remove(existing);
        RecentFiles.Insert(0, new RecentFileItem(path));
        while (RecentFiles.Count > MaxRecent) RecentFiles.RemoveAt(RecentFiles.Count - 1);
        _settings.RecentFiles = RecentFiles.Select(r => r.Path).ToList();
        SaveSettings();
    }

    private void RemoveRecent(string path)
    {
        var existing = RecentFiles.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        RecentFiles.Remove(existing);
        _settings.RecentFiles = RecentFiles.Select(r => r.Path).ToList();
        SaveSettings();
    }

    /// <summary>Clears the recent files list.</summary>
    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        _settings.RecentFiles = new List<string>();
        SaveSettings();
    }

    private void SaveSettings() => _svc.Settings.Save(_settings);

    /// <summary>Asks the window to close.</summary>
    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    // ----------------------------------------------------------------------------------- export / import / print

    /// <summary>Exports a PNG.</summary>
    [RelayCommand]
    private void ExportPng() => Export(ExportKind.Png);

    /// <summary>Exports a PDF.</summary>
    [RelayCommand]
    private void ExportPdf() => Export(ExportKind.Pdf);

    /// <summary>Exports the ZPL text.</summary>
    [RelayCommand]
    private void ExportZpl() => Export(ExportKind.Zpl);

    private void Export(ExportKind kind)
    {
        string ext = kind switch { ExportKind.Png => ".png", ExportKind.Pdf => ".pdf", _ => ".zpl" };
        if (kind == ExportKind.Zpl && !CheckNoErrors()) return;
        string? path = _svc.Files.PickExportFile(kind, DocumentName + ext);
        if (path is null) return;
        try
        {
            IReadOnlyList<Rendering.RenderIssue> issues = Array.Empty<Rendering.RenderIssue>();
            switch (kind)
            {
                case ExportKind.Png:
                    issues = LabelExporter.SavePng(LabelDocumentAdapter.ToRenderLabel(Session.Document), path);
                    break;
                case ExportKind.Pdf:
                    issues = LabelExporter.SavePdf(LabelDocumentAdapter.ToRenderLabel(Session.Document), path);
                    break;
                default:
                    LabelExporter.SaveZpl(_zplResult!.GetZplOrThrow(), path);
                    break;
            }
            StatusMessage = Loc.Get("Status.Exported", Path.GetFileName(path));
            if (issues.Count > 0)
                _svc.Messages.ShowInfo(Loc.Get("Dialog.ExportNotes"), string.Join(Environment.NewLine, issues.Select(i => i.Message)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.ExportFailed"), Loc.Get("File.Error.Write", path));
        }
    }

    /// <summary>Tells the user about errors that stop ZPL export and printing; returns true when there are none.</summary>
    private bool CheckNoErrors()
    {
        if (_zplResult is null || _zplResult.HasErrors)
        {
            var lines = (_zplResult?.Issues ?? Array.Empty<ValidationIssue>()).Where(i => i.Severity == ValidationSeverity.Error)
                .Take(8).Select(Loc.FormatIssue);
            _svc.Messages.ShowError(Loc.Get("Dialog.FixErrors"), Loc.Get("Dialog.FixErrors.Body") + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines));
            return false;
        }
        return true;
    }

    /// <summary>Imports a ZPL file as a new label.</summary>
    [RelayCommand]
    private void ImportZpl()
    {
        if (!EnsureSaved()) return;
        string? path = _svc.Files.PickOpenZpl();
        if (path is null) return;
        byte[] zpl;
        try
        {
            zpl = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.ImportFailed"), Loc.Get("File.Error.Read", Path.GetFileName(path)));
            return;
        }
        var result = _svc.ZplImporter.Import(zpl);
        if (result.Document is null)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.ImportFailed"), Loc.Get(result.MessageKey ?? "Import.Failed"));
            return;
        }
        OpenDocument(result.Document, null, markDirty: true);
        StatusMessage = Loc.Get("Status.Imported", Path.GetFileName(path));
        if (result.Warnings.Count > 0)
            _svc.Messages.ShowInfo(Loc.Get("Dialog.ImportNotes"), string.Join(Environment.NewLine, result.Warnings));
    }

    /// <summary>Prints the label.</summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        if (!CheckNoErrors()) return;
        var outcome = await _svc.Printing.PrintAsync(Session.Document, _zplResult!.GetZplOrThrow());
        if (outcome.Success) StatusMessage = Loc.Get(outcome.MessageKey ?? "Status.Printed");
        else _svc.Messages.ShowInfo(Loc.Get("Dialog.PrintFailed"), Loc.Get(outcome.MessageKey ?? "Print.Failed"));
    }

    /// <summary>Copies the ZPL text to the clipboard.</summary>
    [RelayCommand]
    private void CopyZpl()
    {
        if (ZplText.Length == 0) return;
        _svc.Clipboard.SetText(ZplText);
        StatusMessage = Loc.Get("Status.ZplCopied");
    }

    // ----------------------------------------------------------------------------------- edit commands

    /// <summary>Undo.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Session.PerformUndo();
    private bool CanUndo() => Session.Undo.CanUndo;

    /// <summary>Redo.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Session.PerformRedo();
    private bool CanRedo() => Session.Undo.CanRedo;

    /// <summary>Copy the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        var text = Commands.CopySelected();
        if (text is not null) _svc.Clipboard.SetText(text);
    }

    /// <summary>Cut the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Cut()
    {
        Copy();
        Commands.DeleteSelected();
    }

    /// <summary>Paste from the clipboard.</summary>
    [RelayCommand]
    private void Paste()
    {
        if (!Commands.Paste(_svc.Clipboard.GetText()))
            StatusMessage = Loc.Get("Status.NothingToPaste");
    }

    /// <summary>Duplicate the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate() => Commands.DuplicateSelected();

    /// <summary>Delete the selection.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete() => Commands.DeleteSelected();

    /// <summary>Select all.</summary>
    [RelayCommand]
    private void SelectAll() => Session.SelectAll();

    /// <summary>Selects the next element in z-order (keyboard way to pick elements without the mouse).</summary>
    [RelayCommand]
    private void SelectNext() => StepSelection(+1);

    /// <summary>Selects the previous element in z-order.</summary>
    [RelayCommand]
    private void SelectPrevious() => StepSelection(-1);

    private void StepSelection(int direction)
    {
        var all = Session.Document.Elements;
        if (all.Count == 0) return;
        int current = Session.SelectedIds.Count == 0 ? -1 : all.FindIndex(e => e.Id == Session.SelectedIds[^1]);
        int next = current < 0 ? (direction > 0 ? 0 : all.Count - 1) : (current + direction + all.Count) % all.Count;
        Session.Select(all[next]);
        StatusMessage = Loc.Get("Status.Selected", PropertyPanelViewModel.ElementDisplayName(all[next]));
    }

    /// <summary>Clear the selection.</summary>
    [RelayCommand]
    private void SelectNone() => Session.SetSelection(Array.Empty<string>());

    /// <summary>Nudge by arrow keys. The parameter is "dx,dy" in steps; Shift is handled by <see cref="NudgeBy"/>.</summary>
    public void NudgeBy(int dxSteps, int dySteps, bool large)
    {
        int step = large ? NudgeLarge : NudgeSmall;
        Commands.Nudge(dxSteps * step, dySteps * step);
    }

    /// <summary>Rotate clockwise.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateClockwise() => Commands.Rotate90(true);

    /// <summary>Rotate counter-clockwise.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RotateCounterClockwise() => Commands.Rotate90(false);

    /// <summary>Bring to front.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringToFront() => Commands.BringToFront();

    /// <summary>Send to back.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendToBack() => Commands.SendToBack();

    /// <summary>Bring forward.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringForward() => Commands.BringForward();

    /// <summary>Send backward.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendBackward() => Commands.SendBackward();

    /// <summary>Align the selection (the parameter is an <see cref="AlignKind"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Align(AlignKind kind) => Commands.Align(kind);

    /// <summary>Distribute the selection (the parameter is a <see cref="DistributeKind"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasThree))]
    private void Distribute(DistributeKind kind) => Commands.Distribute(kind);

    /// <summary>Adds an element from the toolbox.</summary>
    [RelayCommand]
    private void AddElement(ToolboxKind kind)
    {
        var doc = Session.Document;
        LabelElement element = ElementFactory.Create(kind, doc, doc.Elements.Count, Loc.Get("Element.DefaultText"));
        if (element is ImageElement image)
        {
            string? path = _svc.Files.PickOpenImage();
            if (path is null) return;
            if (!TryLoadImage(path, out var bitmap)) return;
            image.Source = bitmap;
            // Start at a size that fits the label instead of the (often huge) pixel size of the file.
            int target = Math.Min(bitmap.Width, Math.Max(32, doc.WidthDots / 2));
            image.WidthDots = target;
        }
        Commands.Add(element, ElementFactory.EnsureVariable(kind, Loc.Get("Element.DefaultSample")));
        StatusMessage = Loc.Get("Status.Added", PropertyPanelViewModel.ElementDisplayName(element));
    }

    private bool TryLoadImage(string path, out Rotula.Core.Imaging.GrayscaleBitmap bitmap)
    {
        try
        {
            bitmap = _svc.Images.Load(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            _svc.Messages.ShowError(Loc.Get("Dialog.ImageFailed"), Loc.Get("Image.Error.Read", Path.GetFileName(path)));
            bitmap = null!;
            return false;
        }
    }

    private bool ChooseImageFor(string elementId)
    {
        string? path = _svc.Files.PickOpenImage();
        if (path is null || !TryLoadImage(path, out var bitmap)) return false;
        Session.Edit("Undo.Property", d =>
        {
            if (d.Elements.FirstOrDefault(e => e.Id == elementId) is ImageElement img) img.Source = bitmap;
        });
        return true;
    }

    // ----------------------------------------------------------------------------------- view commands

    /// <summary>Zoom in.</summary>
    [RelayCommand]
    private void ZoomIn() => Zoom = Zoom.ZoomIn();

    /// <summary>Zoom out.</summary>
    [RelayCommand]
    private void ZoomOut() => Zoom = Zoom.ZoomOut();

    /// <summary>Zoom to 100% (true size).</summary>
    [RelayCommand]
    private void ZoomActualSize() => Zoom = new ZoomModel(1.0);

    /// <summary>Fit the whole label in the window.</summary>
    [RelayCommand]
    private void ZoomFit() => ZoomToFitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Choose the ruler unit (parameter is a <see cref="LengthUnit"/>).</summary>
    [RelayCommand]
    private void SetUnit(LengthUnit unit) => Unit = unit;

    /// <summary>Toggle the grid.</summary>
    [RelayCommand]
    private void ToggleGrid() => GridVisible = !GridVisible;

    /// <summary>Switch the language: "auto", "en" or "es". The change is live; no restart is needed.</summary>
    [RelayCommand]
    private void SetLanguage(string code)
    {
        _settings.Language = code is "en" or "es" ? code : "auto";
        Loc.Instance.SetLanguage(_settings.Language);
        // SetLanguage does nothing when the resolved language is unchanged (auto -> en on an English PC), but the menu
        // check marks still have to move.
        OnLanguageChanged();
        SaveSettings();
    }

    /// <summary>Selects elements from the layers list.</summary>
    public void SelectLayers(IEnumerable<string> ids)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try { Session.SetSelection(ids); }
        finally { _syncingSelection = false; }
    }

    /// <summary>Saves the settings one last time when the program closes.</summary>
    public void OnClosing() => SaveSettings();
}
