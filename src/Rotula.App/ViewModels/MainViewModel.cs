using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.Files;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Printing;
using Rotula.Rendering.Export;
using Rotula.Rendering.Input;
using Rotula.Rendering.View;

namespace Rotula.App.ViewModels;

/// <summary>An entry of the layers list.</summary>
/// <param name="Id">Element id.</param>
/// <param name="Name">Display name.</param>
/// <param name="TypeTag">Short kind label shown in small monospace type (Text, QR, Line, Code 128...).</param>
/// <param name="Detail">The text or barcode data of the element, shown under the name (empty for shapes).</param>
/// <param name="FullText">The complete text or barcode data (the row shows <paramref name="Detail"/>, which is cut short); used for the tooltip.</param>
/// <param name="IsBound">True when the element's content uses at least one data field (the list shows a small {} badge).</param>
/// <param name="MissingText">Warning shown when a field has no matching column in the loaded data; empty when all is well.</param>
public sealed record LayerItem(string Id, string Name, string TypeTag = "", string Detail = "", string FullText = "", bool IsBound = false, string MissingText = "")
{
    /// <summary>True when there is a missing-column warning to show.</summary>
    public bool HasMissing => MissingText.Length > 0;

    /// <summary>Tooltip of the {} badge.</summary>
    public string BoundTip => HasMissing ? MissingText : Loc.Get("Fields.Bound", FullText);

    /// <summary>Tooltip of the row: the full text when the element has one, otherwise its name.</summary>
    public string ToolTipText => FullText.Length > 0 ? FullText : Name;

    /// <summary>What the row shows first: the element's text or data when it has any, otherwise its name.</summary>
    public string Primary => Detail.Length > 0 ? Detail : Name;
}

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
    ISettingsStore Settings,
    IToolDialogService Dialogs,
    IDatabaseServices? Database = null,
    IVariableServices? Variables = null);

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
        // Handles and selection frames are measured with the record shown on the canvas, not only the sample values.
        Session.Bounds.DataProvider = () => Preview.Data;
        Printer = new PrinterSettingsViewModel(services.Printing, _settings.Print, SaveSettings, () => Session.Document, applyLabelDpi: SetLabelDpi);
        // Without a counter store (tests, headless use) a counter that remembers its value refuses to print instead of repeating numbers.
        Variables = services.Variables ?? new VariableServices(null, services.Dialogs);
        Data = new DataMergeViewModel(services.Printing, Printer, () => Session.Document, () => DocumentName, services.Files, services.Messages, services.Database, services.Dialogs, Variables);
        Data.SettingsEdited += (_, _) => Session.MarkDirty();
        Data.PreviewDataChanged += (_, _) => OnPreviewDataChanged();
        Fields = new FieldsViewModel(Session, Commands, Data, services.Messages, text => StatusMessage = text, () => services.Dialogs.ShowDataWindow(Data));
        Data.Fields = Fields;
        Properties.AttachFields(Fields);
        Properties.AttachBuilders(vm => services.Dialogs.ShowQrContentBuilder(vm), vm => services.Dialogs.ShowGs1Builder(vm));
        // Data loaded, replaced or cleared, or a mapping changed: the "no such column" markers of the Objects list follow.
        Fields.FieldsChanged += (_, _) => RefreshLayers();
        Data.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DataMergeViewModel.RecordCount) or nameof(DataMergeViewModel.CurrentIndex) or nameof(DataMergeViewModel.HasData))
            {
                OnPropertyChanged(nameof(DataRecordText));
                RefreshCommandStates();
            }
        };

        _unit = Enum.TryParse<LengthUnit>(_settings.Unit, out var u) ? u : LengthUnit.Millimeters;
        _gridVisible = _settings.GridVisible;
        _snapToGrid = _settings.SnapToGrid;
        _snapToGuides = _settings.SnapToGuides;
        _zplPanelVisible = _settings.ZplPanelVisible;
        _objectsPanelExpanded = _settings.ObjectsPanelExpanded;
        // Paths read from settings.json are only kept when they are plain local drive paths (see SafeLocalPath).
        foreach (var p in _settings.RecentFiles.Select(SafeLocalPath.TryGetLocalPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecent))
            RecentFiles.Add(new RecentFileItem(p));

        FillToolbox();

        // Any change of the label (an edit, a loaded file) means the user has started working: the start screen is no longer needed.
        Session.DocumentChanged += (_, _) => { ShowStartScreen = false; OnDocumentChanged(); };
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

    /// <summary>Printer and printer profile fields (shared by the Print dialog and the Data window).</summary>
    public PrinterSettingsViewModel Printer { get; }

    /// <summary>The fields that can be put into the label (data columns, counters, label variables); shared by the property panel and the Data window.</summary>
    public FieldsViewModel Fields { get; }

    /// <summary>The data merge: loaded table, mapping, record navigator, counters and batch actions.</summary>
    public DataMergeViewModel Data { get; }

    /// <summary>Counter memory and the print-time questions, shared by the Print dialog and the Data merge window.</summary>
    public IVariableServices Variables { get; }

    /// <summary>The element toolbox. Rebuilt when the language changes so the names are translated.</summary>
    public ObservableCollection<ToolboxItem> Toolbox { get; } = new();

    private void FillToolbox()
    {
        Toolbox.Clear();
        Toolbox.Add(new ToolboxItem(ToolboxKind.Text, "Tool.Text", "T"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Barcode, "Tool.Barcode", "|||"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.QrCode, "Tool.QrCode", "QR"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Box, "Tool.Box", "▭"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Line, "Tool.Line", "─"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Circle, "Tool.Circle", "◯"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Ellipse, "Tool.Ellipse", "⬭"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.DiagonalLine, "Tool.DiagonalLine", "╲"));
        Toolbox.Add(new ToolboxItem(ToolboxKind.Image, "Tool.Image", "▨"));
    }

    /// <summary>Layers, front-most first.</summary>
    public ObservableCollection<LayerItem> Layers { get; } = new();

    /// <summary>Recently used files, newest first.</summary>
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();

    /// <summary>Path of the file the label was opened from or saved to; null for a label that has not been saved yet.</summary>
    /// <summary>The notice about data source add-ons that could not be loaded; null until start-up loading finished with something to say.</summary>
    [ObservableProperty]
    private PackNoticeViewModel? _packNotice;

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

    /// <summary>True when the status bar has a message (so the divider next to it is only drawn then).</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    /// <summary>
    /// True while the friendly start screen covers the canvas: the program was started with no file and nothing has been done yet.
    /// It goes away once a label is created, opened or imported, on the first edit, or when the user chooses to start with a blank label.
    /// A cancelled dialog or a file that fails to load leaves it in place.
    /// </summary>
    [ObservableProperty]
    private bool _showStartScreen = true;

    /// <summary>Hides the start screen and carries on with the blank label.</summary>
    [RelayCommand]
    private void DismissStartScreen() => ShowStartScreen = false;

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

    /// <summary>Short wording of the approximate-preview notice for the status bar (the full text is its tooltip).</summary>
    public string ApproximateNoticeShort => Loc.Get("Preview.Approximate.Short");

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
        NotifyPresentation();
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

    /// <summary>The ZPL code panel is shown under the canvas (hidden by default so the canvas gets the space).</summary>
    [ObservableProperty]
    private bool _zplPanelVisible;

    partial void OnZplPanelVisibleChanged(bool value) { _settings.ZplPanelVisible = value; SaveSettings(); }

    /// <summary>The Objects list is open; when it is closed the Properties panel gets its height. The choice is remembered.</summary>
    [ObservableProperty]
    private bool _objectsPanelExpanded;

    partial void OnObjectsPanelExpandedChanged(bool value) { _settings.ObjectsPanelExpanded = value; SaveSettings(); }

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

    // The design generated with its sample values: used for export and for the "has errors" gate.
    private ZplGenerationResult? _zplResult;

    private void OnDocumentChanged()
    {
        // The data binding must see the new document first, so the preview data below already matches it.
        Data.OnDocumentChanged();
        Fields.Refresh();
        Preview.Data = Data.CurrentData;
        RefreshZplView();

        RefreshLayers(force: true);
        UpdateTitle();
        NotifyPresentation();
        RefreshCommandStates();
    }

    private List<LayerItem> BuildLayers() =>
        Session.Document.Elements.AsEnumerable().Reverse()
            .Select(e => new LayerItem(e.Id, PropertyPanelViewModel.ElementDisplayName(e), TypeTagOf(e), DetailOf(e), FullTextOf(e), FieldsViewModel.IsBound(e), Fields.MissingText(e)))
            .ToList();

    /// <summary>
    /// Rebuilds the Objects list. Unless forced, the list is only touched when something it shows changed, so moving through the records
    /// does not rebuild it (which would reset the list box on every click of the navigator).
    /// </summary>
    private void RefreshLayers(bool force = false)
    {
        var items = BuildLayers();
        if (!force && items.SequenceEqual(Layers)) return;
        // Rebuilding the list makes the list box report "selection cleared"; that must not reach the real selection.
        bool wasSyncing = _syncingSelection;
        _syncingSelection = true;
        try
        {
            Layers.Clear();
            foreach (var item in items) Layers.Add(item);
        }
        finally { _syncingSelection = wasSyncing; }
    }

    /// <summary>The record shown on the canvas changed (navigator, mapping, counters): redraw and regenerate the ZPL view.</summary>
    private void OnPreviewDataChanged()
    {
        Preview.Data = Data.CurrentData;
        Session.Touch();
        RefreshZplView();
        RefreshCommandStates();
    }

    // ----------------------------------------------------------------------------------- presentation texts (title bar, toolbar, status bar)

    /// <summary>Short kind of an element in the layers list and the properties heading.</summary>
    public static string TypeTagOf(LabelElement e) => e switch
    {
        TextElement => Loc.Get("Tool.Text"),
        BarcodeElement { Symbology: BarcodeSymbology.QrCode } => Loc.Get("Tool.QrCode"),
        BarcodeElement b => Loc.EnumName(b.Symbology),
        BoxElement => Loc.Get("Tool.Box"),
        LineElement => Loc.Get("Tool.Line"),
        CircleElement => Loc.Get("Tool.Circle"),
        EllipseElement => Loc.Get("Tool.Ellipse"),
        DiagonalLineElement => Loc.Get("Tool.DiagonalLine"),
        SymbolElement => Loc.Get("Tool.Symbol"),
        ImageElement => Loc.Get("Tool.Image"),
        _ => e.GetType().Name,
    };

    private static string FullTextOf(LabelElement e) =>
        (e switch { BarcodeElement b => b.Data, TextElement t => t.GetTemplate(), _ => string.Empty }).Replace("\r", " ").Replace("\n", " ");

    private static string DetailOf(LabelElement e)
    {
        string text = FullTextOf(e);
        return text.Length > 60 ? text[..60] + "..." : text;
    }

    /// <summary>Name of the open file with its extension and the program name, for the custom title bar.</summary>
    public string TitleBarText =>
        (Session.IsDirty ? "\u2022 " : string.Empty) + (FilePath is null ? Loc.Get("Document.Untitled") : Path.GetFileName(FilePath)) + " \u2014 Rotula";

    private string SizeText()
    {
        var d = Session.Document;
        int dpi = (int)d.Dpi;
        string format = Unit switch { LengthUnit.Inches => "0.00", LengthUnit.Millimeters => "0.#", _ => "0" };
        // When the label was defined in the unit being shown, show that number as typed (dots would round 100 mm to 99.97).
        double Show(Measure m, int dots) => m.Unit == Unit ? m.Value : UnitConverter.Convert(dots, LengthUnit.Dots, Unit, dpi);
        string w = Show(d.Width, d.WidthDots).ToString(format, CultureInfo.CurrentCulture);
        string h = Show(d.Height, d.HeightDots).ToString(format, CultureInfo.CurrentCulture);
        string suffix = Unit switch { LengthUnit.Millimeters => "mm", LengthUnit.Inches => "in", _ => "dots" };
        return $"{w} x {h} {suffix}";
    }

    /// <summary>Label size and resolution for the toolbar, for example "4.00 x 3.00 in · 203 dpi".</summary>
    public string LabelSummaryText => $"{SizeText()} \u00B7 {(int)Session.Document.Dpi} dpi";

    /// <summary>Right side of the status bar, for example "4.00 x 3.00 in · 203 dpi · ZPL".</summary>
    public string StatusLabelText => LabelSummaryText + " \u00B7 ZPL";

    /// <summary>Left side of the status bar, for example "5 objects · 1 selected".</summary>
    public string ObjectsText
    {
        get
        {
            int total = Session.Document.Elements.Count, sel = Session.SelectedIds.Count;
            return Loc.Get(total == 1 ? "Status.ObjectsOne" : "Status.Objects", total) + " \u00B7 " + Loc.Get(sel == 0 ? "Status.NoneSelected" : "Status.SomeSelected", sel);
        }
    }

    /// <summary>
    /// What the preview does differently for the selected barcode (for example "the printer draws this barcode; the preview shows a placeholder"),
    /// or empty when nothing is selected or the preview is exact. Shown in the status bar.
    /// </summary>
    public string BarcodePreviewNote => Session.SelectedElements is [BarcodeElement barcode] ? Editing.BarcodePreviewNotes.For(barcode) ?? string.Empty : string.Empty;

    /// <summary>Small type tag at the right of the Properties heading ("Barcode", "Text", or "Label" when nothing is selected).</summary>
    public string SelectionTypeTag
    {
        get
        {
            var sel = Session.SelectedElements;
            return sel.Count == 0 ? Loc.Get("Panel.Label") : sel.Count == 1 ? TypeTagOf(sel[0]) : Loc.Get("Status.ManySelected", sel.Count);
        }
    }

    /// <summary>Zoom levels offered in the toolbar drop-down (percent).</summary>
    public IReadOnlyList<int> ZoomPresets { get; } = new[] { 25, 50, 75, 100, 150, 200, 300, 400 };

    /// <summary>Sizes offered by the toolbar's label picker (the New dialog's presets without the "custom" entry).</summary>
    public IReadOnlyList<LabelPreset> SizePresets { get; } = LabelPreset.All.Where(p => p.NameKey != "Preset.Custom").ToArray();

    /// <summary>True when the open label already has the size of <paramref name="preset"/> (compared in printer dots, whatever the unit).</summary>
    public bool IsCurrentSize(LabelPreset preset)
    {
        var d = Session.Document;
        var w = new Measure(preset.Width, preset.Unit);
        var h = new Measure(preset.Height, preset.Unit);
        return w.ToDots(d.Dpi) == d.WidthDots && h.ToDots(d.Dpi) == d.HeightDots;
    }

    /// <summary>Gives the open label the size of a preset. One undo step; does nothing when the label already has that size.</summary>
    public void ApplyLabelSize(LabelPreset preset)
    {
        if (IsCurrentSize(preset)) return;
        Session.Edit("Undo.LabelSize", d =>
        {
            d.Width = new Measure(preset.Width, preset.Unit);
            d.Height = new Measure(preset.Height, preset.Unit);
            // The size no longer comes from a library stock.
            d.StockId = null;
        });
    }

    /// <summary>
    /// Converts the open label to another printer resolution (one undo step; nothing happens when it is already that resolution).
    /// Dot-based sizes and positions are rescaled so the label keeps its physical size and layout.
    /// </summary>
    public void SetLabelDpi(PrinterDpi dpi)
    {
        if (!LabelDpiConverter.NeedsConversion(Session.Document, dpi)) return;
        DpiConversionResult result;
        try { result = LabelDpiConverter.Convert(Session.Document, dpi); }
        catch (NotSupportedException)
        {
            // An element kind Core cannot rescale: leave the design untouched and say why.
            _svc.Messages.ShowError(Loc.Get("Print.DpiConvert.Failed.Title"), Loc.Get("Print.DpiConvert.Failed", (int)Session.Document.Dpi, (int)dpi));
            return;
        }
        Session.Edit("Undo.LabelSettings", d =>
        {
            // Why copy back into the live object: the editor and undo history hold this very document instance.
            var converted = result.Document;
            ElementCloner.CopySettings(converted, d);
            d.Elements.Clear();
            d.Elements.AddRange(converted.Elements);
        });
    }

    /// <summary>The share of the right-hand column used by the Properties panel; setting it saves it in settings.json.</summary>
    public double PropertiesPanelShare
    {
        get => Math.Clamp(_settings.PropertiesPanelShare, 0.2, 0.8);
        set
        {
            double v = Math.Clamp(value, 0.2, 0.8);
            if (Math.Abs(v - _settings.PropertiesPanelShare) < 0.005) return;
            _settings.PropertiesPanelShare = v;
            SaveSettings();
        }
    }

    /// <summary>Sets the zoom to a preset (percent).</summary>
    public void SetZoomPercent(int percent) => Zoom = new ZoomModel(Math.Clamp(percent, 5, 1600) / 100.0);

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(TitleBarText));
        OnPropertyChanged(nameof(LabelSummaryText));
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(ObjectsText));
        OnPropertyChanged(nameof(SelectionTypeTag));
        OnPropertyChanged(nameof(BarcodePreviewNote));
    }

    /// <summary>Opens a larger preview of the label (what the printer will draw, with the shown record's values).</summary>
    [RelayCommand]
    private void ShowPreview() => _svc.Dialogs.ShowPreviewDialog(Preview, DocumentName);

    /// <summary>Reads the installed printers again (the toolbar drop-down and the dialogs share the list).</summary>
    [RelayCommand]
    private Task RefreshPrintersAsync() => Printer.RefreshPrintersAsync();

    /// <summary>Regenerates the ZPL text and the problem counts. With a data record shown, the view shows that record's ZPL.</summary>
    private void RefreshZplView()
    {
        try
        {
            _zplResult = ZplGenerator.Generate(Session.Document);
            var view = Preview.Data is null ? _zplResult : ZplGenerator.Generate(Session.Document, Preview.Data);
            ZplText = view.Zpl;
            Properties.SetIssues(view.Issues);
            ErrorCount = view.Issues.Count(i => i.Severity == ValidationSeverity.Error);
            int warnings = view.Issues.Count - ErrorCount;
            ProblemsText = ErrorCount == 0 && warnings == 0 ? Loc.Get("Status.NoProblems") : DescribeProblems(ErrorCount, warnings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The ZPL view must never take the editor down; say what happened instead.
            _zplResult = null;
            ZplText = string.Empty;
            ProblemsText = Loc.Get("Status.ZplFailed");
        }
    }

    /// <summary>"1 error, 3 warnings": each count in the singular or plural form of the current language; a zero count is left out.</summary>
    private static string DescribeProblems(int errors, int warnings)
    {
        var parts = new List<string>(2);
        if (errors > 0) parts.Add(errors == 1 ? Loc.Get("Status.Errors.One") : Loc.Get("Status.Errors.Many", errors));
        if (warnings > 0) parts.Add(warnings == 1 ? Loc.Get("Status.Warnings.One") : Loc.Get("Status.Warnings.Many", warnings));
        return string.Join(", ", parts);
    }

    private void OnSelectionChanged()
    {
        int n = Session.SelectedIds.Count;
        SelectionText = n == 0 ? Loc.Get("Status.NothingSelected") : n == 1 ? Loc.Get("Status.OneSelected") : Loc.Get("Status.ManySelected", n);
        NotifyPresentation();
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
        OnPropertyChanged(nameof(ApproximateNoticeShort));
        OnPropertyChanged(nameof(DataRecordText));
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
        OnPropertyChanged(nameof(TitleBarText));
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
        // The stock picker remembers favourites and recent stocks in the same settings object this window saves, and starts from the
        // resolution of the selected printer (detected from its driver when possible).
        // Say "from your printer" only when the driver really reported it; otherwise the last print settings are the best guess, and are described as such.
        var detected = Printer.DetectedDpi;
        bool fromSettings = detected is null && _settings.Print.Configured;
        var doc = _svc.NewLabelDialog.ShowNewLabelDialog(new NewLabelContext
        {
            Stocks = StockLibrary.Current,
            Settings = _settings,
            SaveSettings = SaveSettings,
            DefaultDpi = detected ?? (fromSettings ? Printer.SelectedDpi : null),
            DpiFromPrinter = detected is not null,
            DpiFromLastUse = fromSettings,
            Files = _svc.Files,
        });
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
            var content = RotulaFileSerializer.LoadFile(path);
            OpenDocument(content.Document, path, merge: content.Merge);
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

    private void OpenDocument(LabelDocument doc, string? path, bool markDirty = false, DataMergeSettings? merge = null)
    {
        // Data settings are replaced first: the loaded data belonged to the previous label, and only the settings are kept per file.
        Data.Apply(merge);
        Session.Load(doc, markDirty);
        ShowStartScreen = false;
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
            RotulaFileSerializer.Save(Session.Document, path, Data.ToSettings());
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

    /// <summary>Opens the Print dialog (printer, printer profile, copies, test label, save as ZPL).</summary>
    [RelayCommand]
    private void Print()
    {
        var vm = new PrintViewModel(_svc.Printing, Printer, () => Session.Document, () => Data.CurrentData, _svc.Files, DocumentName, Variables);
        _svc.Dialogs.ShowPrintDialog(vm);
    }

    /// <summary>Opens the Data merge window.</summary>
    [RelayCommand]
    private void ShowData() => _svc.Dialogs.ShowDataWindow(Data);

    /// <summary>Picks a CSV or Excel file, opens the Data merge window and loads the file.</summary>
    [RelayCommand]
    private async Task OpenDataFileAsync()
    {
        string? path = _svc.Files.PickOpenData();
        if (path is null) return;
        _svc.Dialogs.ShowDataWindow(Data);
        await Data.LoadAsync(path);
    }

    /// <summary>Shows the first record on the canvas.</summary>
    [RelayCommand(CanExecute = nameof(HasDataRecords))]
    private void FirstRecord() => Data.CurrentIndex = 0;

    /// <summary>Shows the previous record.</summary>
    [RelayCommand(CanExecute = nameof(HasDataRecords))]
    private void PreviousRecord() => Data.CurrentIndex = Math.Max(0, Data.CurrentIndex - 1);

    /// <summary>Shows the next record.</summary>
    [RelayCommand(CanExecute = nameof(HasDataRecords))]
    private void NextRecord() => Data.CurrentIndex = Math.Min(Data.RecordCount - 1, Data.CurrentIndex + 1);

    /// <summary>Shows the last record.</summary>
    [RelayCommand(CanExecute = nameof(HasDataRecords))]
    private void LastRecord() => Data.CurrentIndex = Data.RecordCount - 1;

    private bool HasDataRecords() => Data.RecordCount > 0;

    /// <summary>Record navigator text for the toolbar ("Record 3 of 120").</summary>
    public string DataRecordText => Data.RecordText;

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
        // A new QR code starts in the content builder; Cancel keeps the plain example code. Applying changes the element before it is
        // added, so the whole thing is one undo step.
        if (element is BarcodeElement { Symbology: BarcodeSymbology.QrCode } qr) Properties.PrepareNewQr(qr);
        Commands.Add(element);
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
