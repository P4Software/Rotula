using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Barcodes;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Rendering.View;

namespace Rotula.App.ViewModels;

/// <summary>
/// The property panel. Shows every property of the selected element (or of the label when nothing is selected), validates
/// what the user types and turns Core's <see cref="ValidationIssue"/>s into messages in the current language.
/// </summary>
public sealed partial class PropertyPanelViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly Func<string, bool> _replaceImage;
    private string _builtFor = "#";
    private IReadOnlyList<ValidationIssue> _issues = Array.Empty<ValidationIssue>();

    /// <summary>Creates the panel.</summary>
    /// <param name="session">The editing session.</param>
    /// <param name="replaceImage">Called with an element id when the user presses "Choose image"; returns true when it changed the image.</param>
    public PropertyPanelViewModel(EditorSession session, Func<string, bool>? replaceImage = null)
    {
        _session = session;
        _replaceImage = replaceImage ?? (_ => false);
        _session.SelectionChanged += (_, _) => Rebuild();
        _session.DocumentChanged += (_, _) => OnDocumentChanged();
        Loc.Instance.LanguageChanged += (_, _) => Rebuild(force: true);
        Rebuild(force: true);
    }

    private FieldsViewModel? _fields;

    /// <summary>
    /// Connects the shared list of available fields. The content rows (text, barcode data) then offer the field controls, and the
    /// "no such column" warnings follow the loaded data. Called once by the main view-model, which creates the fields after this panel.
    /// </summary>
    public void AttachFields(FieldsViewModel fields)
    {
        _fields = fields;
        fields.FieldsChanged += (_, _) => { _fieldsVersion++; foreach (var r in Rows) r.Refresh(); };
        Rebuild(force: true);
    }

    /// <summary>The rows currently shown.</summary>
    public ObservableCollection<PropertyRow> Rows { get; } = new();

    /// <summary>Translated validation messages for the current selection (or for the label).</summary>
    public ObservableCollection<string> IssueMessages { get; } = new();

    /// <summary>Heading above the rows.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Second line under the "Properties" heading: what the panel is showing (the label, or the selected element).</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>True while nothing is selected and the panel shows the settings of the label itself (the view adds an explanation).</summary>
    [ObservableProperty]
    private bool _showsLabel;

    /// <summary>Text shown when several elements are selected.</summary>
    [ObservableProperty]
    private string _hint = string.Empty;

    /// <summary>Gives the panel the latest validation result of the whole label.</summary>
    public void SetIssues(IReadOnlyList<ValidationIssue> issues)
    {
        _issues = issues;
        RefreshIssues();
    }

    private void OnDocumentChanged()
    {
        // Same selection: only refresh the values, so the box the user is typing in keeps its focus and caret.
        string key = SelectionKey();
        if (key != _builtFor) { Rebuild(); return; }
        foreach (var r in Rows) r.Refresh();
    }

    private string SelectionKey()
    {
        var sel = _session.SelectedIds;
        // The label's size unit is part of the key because it appears in the Width/Height labels ("Width (mm)").
        if (sel.Count != 1) return sel.Count == 0 ? "doc:" + _session.Document.Width.Unit : "multi" + sel.Count;
        var e = _session.Document.Elements.FirstOrDefault(x => x.Id == sel[0]);
        // The symbology decides which barcode rows exist, so it is part of the key.
        // For a barcode the key also holds what changes which rows exist besides the symbology: the DataBar variant (segments per row
        // only exist for Expanded) and whether a rotation that the symbology cannot print is still set (its row must stay until cleared).
        string barcodeLayout = e is BarcodeElement b
            ? $"{b.Symbology}/{b.DataBarType == DataBarType.Expanded}/{Enum.IsDefined(b.Symbology) && !BarcodeCatalog.Get(b.Symbology).SupportsRotation && b.Rotation is not null}"
            : e is DataFieldElement ? nameof(TextElement) : e?.GetType().Name ?? string.Empty; // a legacy data field is edited as the text it is
        return sel[0] + ":" + barcodeLayout + ":" + (e is TextElement t && t.Block is not null);
    }

    private void Rebuild(bool force = false)
    {
        string key = SelectionKey();
        if (!force && key == _builtFor) return;
        _builtFor = key;
        Rows.Clear();
        Hint = string.Empty;
        var sel = _session.SelectedElements;
        ShowsLabel = sel.Count == 0;
        if (sel.Count == 0)
        {
            Title = Loc.Get("Panel.Label");
            Subtitle = Loc.Get("Panel.LabelSubtitle");
            BuildDocumentRows();
        }
        else if (sel.Count > 1)
        {
            Title = Loc.Get("Panel.Selection");
            Subtitle = Title;
            Hint = Loc.Get("Panel.MultiHint", sel.Count);
        }
        else
        {
            Title = Loc.Get("Panel.Element", ElementDisplayName(sel[0]));
            // The tag at the top right already says what kind of element it is; the subtitle only adds the user's own name for it.
            Subtitle = sel[0].Name ?? string.Empty;
            BuildElementRows(sel[0]);
        }
        RefreshIssues();
    }

    private void RefreshIssues()
    {
        IssueMessages.Clear();
        var sel = _session.SelectedIds;
        foreach (var i in _issues)
        {
            bool relevant = sel.Count == 0 ? i.ElementId is null : i.ElementId is not null && sel.Contains(i.ElementId);
            if (relevant) IssueMessages.Add((i.Severity == ValidationSeverity.Error ? Loc.Get("Issue.ErrorPrefix") : Loc.Get("Issue.WarningPrefix")) + " " + Loc.FormatIssue(i));
        }
    }

    /// <summary>Localized type name of an element, for titles and the layers list.</summary>
    public static string ElementDisplayName(LabelElement e)
    {
        string type = e switch
        {
            TextElement => Loc.Get("Element.Text"),
            BarcodeElement b => Loc.EnumName(b.Symbology),
            BoxElement => Loc.Get("Element.Box"),
            LineElement => Loc.Get("Element.Line"),
            CircleElement => Loc.Get("Element.Circle"),
            EllipseElement => Loc.Get("Element.Ellipse"),
            DiagonalLineElement => Loc.Get("Element.DiagonalLine"),
            SymbolElement => Loc.Get("Element.Symbol"),
            ImageElement => Loc.Get("Element.Image"),
            _ => e.GetType().Name,
        };
        return string.IsNullOrWhiteSpace(e.Name) ? type : $"{e.Name} ({type})";
    }

    // ----------------------------------------------------------------------------------- element rows

    private T El<T>(string id) where T : LabelElement => (T)_session.Document.Elements.First(x => x.Id == id);

    private void Edit<T>(string id, string prop, Action<T> change) where T : LabelElement =>
        _session.Edit("Undo.Property", d => change((T)d.Elements.First(x => x.Id == id)), mergeKey: $"prop:{id}:{prop}");

    private void AddInt<T>(string id, string labelKey, Func<T, int> get, Action<T, int> set, int min, int max, string? rangeIssueKey = null, string? hintKey = null) where T : LabelElement =>
        Rows.Add(new IntRow(labelKey, () => get(El<T>(id)), v => Edit<T>(id, labelKey, e => set(e, v!.Value)), min, max, hintKey: hintKey, rangeIssueKey: rangeIssueKey));

    private void AddNullableInt<T>(string id, string labelKey, Func<T, int?> get, Action<T, int?> set, int min, int max, string? hint = null) where T : LabelElement =>
        Rows.Add(new IntRow(labelKey, () => get(El<T>(id)), v => Edit<T>(id, labelKey, e => set(e, v)), min, max, nullable: true, hintKey: hint));

    private void AddBool<T>(string id, string labelKey, Func<T, bool> get, Action<T, bool> set, bool rebuild = false) where T : LabelElement =>
        Rows.Add(new BoolRow(labelKey, () => get(El<T>(id)), v => { Edit<T>(id, labelKey, e => set(e, v)); }) { RebuildOnChange = rebuild });

    private void AddEnum<T, TE>(string id, string labelKey, Func<T, TE> get, Action<T, TE> set, IEnumerable<TE>? values = null)
        where T : LabelElement where TE : struct, Enum =>
        Rows.Add(new ChoiceRow(labelKey, ChoiceRow.ForEnum(values), () => get(El<T>(id)), v => Edit<T>(id, labelKey, e => set(e, (TE)v!))));

    private void AddText<T>(string id, string labelKey, Func<T, string> get, Action<T, string> set, bool multiline = false) where T : LabelElement =>
        Rows.Add(new TextRow(labelKey, () => get(El<T>(id)), v => Edit<T>(id, labelKey, e => set(e, v)), multiline));

    private void BuildElementRows(LabelElement element)
    {
        string id = element.Id;

        Rows.Add(new HeadingRow("Panel.Position"));
        AddInt<LabelElement>(id, "Prop.X", e => e.X, (e, v) => e.X = v, 0, 32000);
        AddInt<LabelElement>(id, "Prop.Y", e => e.Y, (e, v) => e.Y = v, 0, 32000);
        Rows.Add(new ChoiceRow("Prop.OriginKind", ChoiceRow.ForEnum<FieldOriginKind>(),
            () => El<LabelElement>(id).OriginKind, v => Edit<LabelElement>(id, "origin", e => e.OriginKind = (FieldOriginKind)v!), "Prop.OriginKind.Hint"));
        // A barcode whose ZPL command has no orientation parameter (MaxiCode) cannot be rotated, so the control is hidden. It stays
        // visible only while such an element still carries a rotation (from an older file), so the user can clear it.
        bool rotationUnsupported = element is BarcodeElement { Symbology: var rs } && Enum.IsDefined(rs) && !BarcodeCatalog.Get(rs).SupportsRotation;
        // Graphics without an orientation parameter in ZPL (circle, ellipse, diagonal line) cannot be rotated.
        if (element is not (CircleElement or EllipseElement or DiagonalLineElement) && (!rotationUnsupported || element.Rotation is not null))
        {
            // Null means "use the label default (^FW)".
            var items = new List<ChoiceItem> { new(null, Loc.Get("Prop.Rotation.Inherit")) };
            items.AddRange(ChoiceRow.ForEnum<FieldRotation>());
            Rows.Add(new ChoiceRow(rotationUnsupported ? "Prop.Rotation.Unsupported" : "Prop.Rotation", items, () => El<LabelElement>(id).Rotation,
                v => Edit<LabelElement>(id, "rotation", e => e.Rotation = (FieldRotation?)v)));
        }
        AddBool<LabelElement>(id, "Prop.Reverse", e => e.ReverseField, (e, v) => e.ReverseField = v);
        Rows.Add(new TextRow("Prop.Name", () => El<LabelElement>(id).Name ?? string.Empty,
            v => Edit<LabelElement>(id, "name", e => e.Name = string.IsNullOrWhiteSpace(v) ? null : v), maxLength: 60));

        switch (element)
        {
            case TextElement t:
                BuildTextRows(t);
                break;
            case BarcodeElement b:
                BuildBarcodeRows(b);
                break;
            case BoxElement:
                Rows.Add(new HeadingRow("Panel.Shape"));
                AddInt<BoxElement>(id, "Prop.Width", e => e.WidthDots, (e, v) => e.WidthDots = v, 1, 32000);
                AddInt<BoxElement>(id, "Prop.Height", e => e.HeightDots, (e, v) => e.HeightDots = v, 1, 32000);
                AddInt<BoxElement>(id, "Prop.Thickness", e => e.ThicknessDots, (e, v) => e.ThicknessDots = v, 1, 32000);
                AddInt<BoxElement>(id, "Prop.CornerRounding", e => e.CornerRounding, (e, v) => e.CornerRounding = v, 0, 8);
                AddEnum<BoxElement, GraphicColor>(id, "Prop.Color", e => e.Color, (e, v) => e.Color = v);
                break;
            case LineElement:
                Rows.Add(new HeadingRow("Panel.Shape"));
                AddInt<LineElement>(id, "Prop.Length", e => e.LengthDots, (e, v) => e.LengthDots = v, 1, 32000);
                AddInt<LineElement>(id, "Prop.Thickness", e => e.ThicknessDots, (e, v) => e.ThicknessDots = v, 1, 32000);
                AddEnum<LineElement, LineDirection>(id, "Prop.Direction", e => e.Direction, (e, v) => e.Direction = v);
                AddEnum<LineElement, GraphicColor>(id, "Prop.Color", e => e.Color, (e, v) => e.Color = v);
                break;
            case CircleElement:
                Rows.Add(new HeadingRow("Panel.Shape"));
                AddInt<CircleElement>(id, "Prop.Diameter", e => e.DiameterDots, (e, v) => e.DiameterDots = v, 3, 4095);
                AddInt<CircleElement>(id, "Prop.Thickness", e => e.ThicknessDots, (e, v) => e.ThicknessDots = v, 1, 4095);
                AddEnum<CircleElement, GraphicColor>(id, "Prop.Color", e => e.Color, (e, v) => e.Color = v);
                break;
            case EllipseElement:
                Rows.Add(new HeadingRow("Panel.Shape"));
                AddInt<EllipseElement>(id, "Prop.Width", e => e.WidthDots, (e, v) => e.WidthDots = v, 3, 4095, "Graphic.Ellipse.Range");
                AddInt<EllipseElement>(id, "Prop.Height", e => e.HeightDots, (e, v) => e.HeightDots = v, 3, 4095, "Graphic.Ellipse.Range");
                AddInt<EllipseElement>(id, "Prop.Thickness", e => e.ThicknessDots, (e, v) => e.ThicknessDots = v, 1, 4095);
                AddEnum<EllipseElement, GraphicColor>(id, "Prop.Color", e => e.Color, (e, v) => e.Color = v);
                break;
            case DiagonalLineElement:
                Rows.Add(new HeadingRow("Panel.Shape"));
                AddInt<DiagonalLineElement>(id, "Prop.Width", e => e.WidthDots, (e, v) => e.WidthDots = v, 3, 32000, "Graphic.Diagonal.Range");
                AddInt<DiagonalLineElement>(id, "Prop.Height", e => e.HeightDots, (e, v) => e.HeightDots = v, 3, 32000, "Graphic.Diagonal.Range");
                AddInt<DiagonalLineElement>(id, "Prop.Thickness", e => e.ThicknessDots, (e, v) => e.ThicknessDots = v, 1, 32000);
                AddEnum<DiagonalLineElement, DiagonalLean>(id, "Prop.Lean", e => e.Lean, (e, v) => e.Lean = v);
                AddEnum<DiagonalLineElement, GraphicColor>(id, "Prop.Color", e => e.Color, (e, v) => e.Color = v);
                break;
            case SymbolElement:
                Rows.Add(new HeadingRow("Panel.Symbol"));
                AddEnum<SymbolElement, GraphicSymbol>(id, "Prop.Symbol", e => e.Symbol, (e, v) => e.Symbol = v);
                AddInt<SymbolElement>(id, "Prop.Symbol.Height", e => e.HeightDots, (e, v) => e.HeightDots = v, 0, 32000, "Graphic.Symbol.Range");
                AddInt<SymbolElement>(id, "Prop.Symbol.Width", e => e.WidthDots, (e, v) => e.WidthDots = v, 0, 32000, "Graphic.Symbol.Range");
                break;
            case ImageElement:
                Rows.Add(new HeadingRow("Panel.Image"));
                Rows.Add(new ActionRow("Prop.ChooseImage", () => { _replaceImage(id); }));
                AddInt<ImageElement>(id, "Prop.Width", e => e.WidthDots, (e, v) => e.WidthDots = v, 0, 32000);
                AddInt<ImageElement>(id, "Prop.Height", e => e.HeightDots, (e, v) => e.HeightDots = v, 0, 32000);
                AddEnum<ImageElement, ImageDitherMode>(id, "Prop.Dither", e => e.Dither, (e, v) => e.Dither = v);
                AddInt<ImageElement>(id, "Prop.Threshold", e => e.Threshold, (e, v) => e.Threshold = v, 0, 255);
                AddBool<ImageElement>(id, "Prop.Invert", e => e.Invert, (e, v) => e.Invert = v);
                break;
        }
    }

    /// <summary>
    /// The row of an element's content (text, barcode data). Typing merges into one undo step; inserting, binding and unbinding a field
    /// are steps of their own so one Undo takes exactly one of them back.
    /// </summary>
    private TextRow ContentRow(string id, string labelKey, bool multiline, Func<string> get) =>
        new(labelKey, get, v => EditContent(id, labelKey, v, null), multiline, fields: _fields,
            setDiscrete: (v, undoKey) => EditContent(id, labelKey, v, undoKey),
            warning: () => _fields?.MissingText(El<LabelElement>(id)) ?? string.Empty);

    private void EditContent(string id, string labelKey, string value, string? discreteUndoKey)
    {
        bool hadSpec = _session.Document.Elements.FirstOrDefault(x => x.Id == id) is BarcodeElement { QrContentSpec.Length: > 0 };
        _session.Edit(discreteUndoKey ?? "Undo.Property", d => FieldsViewModel.SetContent(d, d.Elements.FindIndex(x => x.Id == id), value),
            mergeKey: discreteUndoKey is null ? $"prop:{id}:{labelKey}" : null);
        // SetContent drops the builder definition when the text no longer matches it; remember that, so the panel can explain what happened.
        if (hadSpec && _session.Document.Elements.FirstOrDefault(x => x.Id == id) is BarcodeElement { QrContentSpec.Length: 0 })
        {
            _specCleared.Add(id);
            // The panel refreshed while the edit ran, before the note knew; ask the status line again.
            foreach (var note in Rows.OfType<NoteRow>()) note.Refresh();
        }
    }

    private readonly HashSet<string> _specCleared = new(StringComparer.Ordinal);

    /// <summary>
    /// The block a text gets when word wrap is switched on: as wide as the room left on the label to the right of the text, and as many lines
    /// as fit in the room left below it (at least two). A fixed 400 x 1 block would cut the text to one line, and 400 dots is a thumb-width at 600 dpi.
    /// The user can still change both numbers.
    /// </summary>
    /// <param name="doc">The label (size, resolution, margins).</param>
    /// <param name="text">The text element that gets the block.</param>
    public static TextBlockOptions DefaultBlockFor(LabelDocument doc, TextElement text)
    {
        // Positions count from the label home (^LH, the left and top margins), so the room is what is left of the printable area.
        int roomRight = doc.WidthDots - doc.Margins.Left.ToDots(doc.Dpi) - text.X - doc.Margins.Right.ToDots(doc.Dpi);
        int roomBelow = doc.HeightDots - doc.Margins.Top.ToDots(doc.Dpi) - text.Y - doc.Margins.Bottom.ToDots(doc.Dpi);
        // A text turned a quarter runs down the label: its width comes from the room below and its lines stack across.
        var rotation = text.Rotation ?? doc.DefaultRotation;
        bool vertical = rotation is FieldRotation.Rotated90 or FieldRotation.Rotated270;
        int alongText = vertical ? roomBelow : roomRight;
        int acrossText = vertical ? roomRight : roomBelow;
        double lineHeight = text.Font is { IsWindows: true } windows
            ? FontChoice.PointsToDots(windows.PointSize, (int)doc.Dpi) * 1.2
            : text.FontHeight ?? doc.DefaultFont?.Height ?? 30;
        int lines = (int)(acrossText / Math.Max(1, lineHeight));
        return new TextBlockOptions { WidthDots = Math.Clamp(alongText, 1, 9999), MaxLines = Math.Clamp(lines, 2, 9999) };
    }

    private void BuildTextRows(TextElement element)
    {
        string id = element.Id;
        Rows.Add(new HeadingRow("Panel.Text"));
        // A legacy data-field element is shown as the text it is: its content is the field, written {{name}}.
        Rows.Add(ContentRow(id, "Prop.Text", multiline: true, () => El<TextElement>(id).GetTemplate()));
        Rows.Add(new TextRow("Prop.FontId", () => El<TextElement>(id).FontId?.ToString() ?? string.Empty,
            v => Edit<TextElement>(id, "fontid", e => e.FontId = v.Length == 0 ? null : v[0]), maxLength: 1, hintKey: "Prop.FontId.Hint",
            validate: v => v.Length == 1 && !char.IsLetterOrDigit(v[0]) ? Loc.Get("Validation.FontId") : null));
        AddNullableInt<TextElement>(id, "Prop.FontHeight", e => e.FontHeight, (e, v) => e.FontHeight = v, 10, 32000, "Prop.Inherit.Hint");
        AddNullableInt<TextElement>(id, "Prop.FontWidth", e => e.FontWidth, (e, v) => e.FontWidth = v, 0, 32000, "Prop.Inherit.Hint");

        Rows.Add(new BoolRow("Prop.Block", () => El<TextElement>(id).Block is not null,
            v => _session.Edit("Undo.Property", d =>
            {
                var e = (TextElement)d.Elements.First(x => x.Id == id);
                e.Block = v ? DefaultBlockFor(d, e) : null;
            }, mergeKey: $"prop:{id}:block")) { RebuildOnChange = true });
        if (element.Block is not null)
        {
            AddInt<TextElement>(id, "Prop.Block.Width", e => e.Block!.WidthDots, (e, v) => e.Block!.WidthDots = v, 1, 9999);
            AddInt<TextElement>(id, "Prop.Block.MaxLines", e => e.Block!.MaxLines, (e, v) => e.Block!.MaxLines = v, 1, 9999);
            AddInt<TextElement>(id, "Prop.Block.LineSpacing", e => e.Block!.LineSpacing, (e, v) => e.Block!.LineSpacing = v, -9999, 9999);
            AddEnum<TextElement, TextAlignment>(id, "Prop.Block.Alignment", e => e.Block!.Alignment, (e, v) => e.Block!.Alignment = v);
            AddInt<TextElement>(id, "Prop.Block.HangingIndent", e => e.Block!.HangingIndent, (e, v) => e.Block!.HangingIndent = v, 0, 9999);
        }
    }

    /// <summary>
    /// Data that nobody typed on purpose: empty, or one of the example values the designer itself puts in. Only such data is
    /// swapped for the new symbology's example when the symbology changes, so text the user wrote is never overwritten.
    /// </summary>
    internal static bool IsSampleData(string data) =>
        data.Length == 0 || data == "123456789" || data == "https://example.com" || BarcodeCatalog.All.Any(i => i.ExampleData == data);

    /// <summary>
    /// Switches the symbology and repairs what the old settings would break: sample data that is wrong for the new code, a
    /// rotation or text switch the new ZPL command cannot honour, and the columns/rows that mean something else per symbology.
    /// It runs inside one undoable edit, so a single Undo restores everything.
    /// </summary>
    internal static void ApplySymbology(BarcodeElement e, BarcodeSymbology symbology)
    {
        var info = BarcodeCatalog.Get(symbology);
        bool replaceData = IsSampleData(e.Data);
        e.Symbology = symbology;
        if (replaceData) e.Data = info.ExampleData;
        // The builder definition only means something for a QR code; keeping it on another symbology would let it disagree with Data.
        if (symbology != BarcodeSymbology.QrCode) e.QrContentSpec = string.Empty;
        if (!info.SupportsRotation) e.Rotation = null;
        if (info.AlwaysPrintsHumanReadable) e.HumanReadable = true;
        // Columns and rows mean different things (or nothing) per symbology, so a value from the old one is never carried over.
        e.Columns = 0;
        e.Rows = 0;
    }

    /// <summary>The symbology picker entries: grouped by family, each showing its ZPL command.</summary>
    private static List<ChoiceItem> SymbologyItems() =>
        BarcodeCatalog.All.OrderBy(i => (int)i.Category)
            .Select(i => new ChoiceItem(i.Symbology, $"{Loc.EnumName(i.Symbology)}  ({i.ZplCommand})", Loc.EnumName(i.Category))).ToList();

    private void BuildBarcodeRows(BarcodeElement element)
    {
        string id = element.Id;
        var info = BarcodeCatalog.Get(element.Symbology);

        Rows.Add(new HeadingRow("Panel.Barcode"));
        Rows.Add(new ChoiceRow("Prop.Symbology", SymbologyItems(),
            () => El<BarcodeElement>(id).Symbology, v => Edit<BarcodeElement>(id, "symbology", e => ApplySymbology(e, (BarcodeSymbology)v!))) { RebuildOnChange = true });
        Rows.Add(new NoteRow("Prop.PreviewNote", () => BarcodePreviewNotes.For(El<BarcodeElement>(id))));
        // Barcode data is validated by Core (BarcodeValidator); the messages appear below the rows while the user types,
        // instead of blocking the typing, because half-typed data is normally invalid.
        var dataRow = ContentRow(id, "Prop.Data", multiline: false, () => El<BarcodeElement>(id).Data);
        // The builders sit next to the content: the QR content builder for QR codes, the GS1 builder for the symbologies that carry a GS1 element string.
        if (info.Supports(BarcodeOption.QrContentSpec)) dataRow.WithAction(Loc.Get("QrContent.Open"), () => EditQrContent(id));
        else if (info.Symbology is BarcodeSymbology.Code128 or BarcodeSymbology.DataMatrix) dataRow.WithAction(Loc.Get("Gs1Builder.Open"), () => EditGs1(id));
        Rows.Add(dataRow);
        // The builder status is the QrContentSpec option; it is shown first so it stays close to the content it describes.
        if (info.Supports(BarcodeOption.QrContentSpec)) AddBarcodeOption(id, element, info, BarcodeOption.QrContentSpec);
        foreach (var option in info.Options.Where(o => o != BarcodeOption.QrContentSpec))
            AddBarcodeOption(id, element, info, option);
    }

    /// <summary>
    /// Adds the row of one catalogue option. The option names equal the <see cref="BarcodeElement"/> property names, so the catalogue
    /// alone decides which rows a symbology gets. Ranges mirror the ones Core validates (Core still reports the exact problem).
    /// </summary>
    private void AddBarcodeOption(string id, BarcodeElement element, BarcodeSymbologyInfo info, BarcodeOption option)
    {
        var sym = info.Symbology;
        switch (option)
        {
            case BarcodeOption.Height:
                {
                    (int min, int max) = sym switch
                    {
                        BarcodeSymbology.MicroPdf417 or BarcodeSymbology.Tlc39 => (1, 9999),
                        BarcodeSymbology.Codablock => (2, 32000),
                        _ => (1, 32000),
                    };
                    AddInt<BarcodeElement>(id, "Prop.BarHeight", e => e.Height, (e, v) => e.Height = v, min, max, "Barcode.Height.Range");
                    break;
                }
            case BarcodeOption.ModuleWidth:
                AddInt<BarcodeElement>(id, "Prop.ModuleWidth", e => e.ModuleWidth, (e, v) => e.ModuleWidth = v, 1, 10, "Barcode.ModuleWidth.Range");
                break;
            case BarcodeOption.WideBarRatio:
                Rows.Add(new DoubleRow("Prop.WideBarRatio", () => El<BarcodeElement>(id).WideBarRatio,
                    v => Edit<BarcodeElement>(id, "ratio", e => e.WideBarRatio = v), 2.0, 3.0));
                break;
            case BarcodeOption.HumanReadable:
                AddBool<BarcodeElement>(id, "Prop.HumanReadable", e => e.HumanReadable, (e, v) => e.HumanReadable = v);
                break;
            case BarcodeOption.HumanReadablePosition:
                AddEnum<BarcodeElement, HumanReadablePosition>(id, "Prop.HumanReadablePosition", e => e.HumanReadablePosition, (e, v) => e.HumanReadablePosition = v);
                break;
            case BarcodeOption.CheckDigit:
                AddBool<BarcodeElement>(id, "Prop.CheckDigit", e => e.CheckDigit, (e, v) => e.CheckDigit = v);
                break;
            case BarcodeOption.Code128Mode:
                AddEnum<BarcodeElement, Code128Mode>(id, "Prop.Code128Mode", e => e.Code128Mode, (e, v) => e.Code128Mode = v);
                break;
            case BarcodeOption.QrErrorCorrection:
                // The capacity meter lives in this row: how full the code is depends on this very choice.
                Rows.Add(new ChoiceRow("Prop.QrErrorCorrection",
                    QrSizing.ErrorCorrectionLevels.Select(c => new ChoiceItem(Enum.Parse<QrErrorCorrection>(c.Value), Loc.Get(c.LabelKey))).ToList(),
                    () => El<BarcodeElement>(id).QrErrorCorrection, v => Edit<BarcodeElement>(id, "Prop.QrErrorCorrection", e => e.QrErrorCorrection = (QrErrorCorrection)v!))
                { DetailSource = () => CapacityDetail(El<BarcodeElement>(id)) });
                break;
            case BarcodeOption.QrModel:
                Rows.Add(new ChoiceRow("Prop.QrModel", QrSizing.Models.Select(c => new ChoiceItem(int.Parse(c.Value, CultureInfo.InvariantCulture), Loc.Get(c.LabelKey))).ToList(),
                    () => El<BarcodeElement>(id).QrModel, v => Edit<BarcodeElement>(id, "Prop.QrModel", e => e.QrModel = (int)v!)));
                break;
            case BarcodeOption.QrMask:
                Rows.Add(new ChoiceRow("Prop.QrMask", QrSizing.Masks.Select(c => new ChoiceItem(int.Parse(c.Value, CultureInfo.InvariantCulture), Loc.Get(c.LabelKey))).ToList(),
                    () => El<BarcodeElement>(id).QrMask, v => Edit<BarcodeElement>(id, "Prop.QrMask", e => e.QrMask = (int)v!)));
                break;
            case BarcodeOption.QrInputMode:
                Rows.Add(new ChoiceRow("Prop.QrInputMode", QrSizing.InputModes.Select(c => new ChoiceItem(Enum.Parse<QrInputMode>(c.Value), Loc.Get(c.LabelKey))).ToList(),
                    () => El<BarcodeElement>(id).QrInputMode, v => Edit<BarcodeElement>(id, "Prop.QrInputMode", e => e.QrInputMode = (QrInputMode)v!)));
                break;
            case BarcodeOption.DigitalLink:
                AddBool<BarcodeElement>(id, "Prop.DigitalLink", e => e.DigitalLink, (e, v) => e.DigitalLink = v);
                break;
            case BarcodeOption.DataMatrixGs1:
                AddBool<BarcodeElement>(id, "Prop.DataMatrixGs1", e => e.DataMatrixGs1, (e, v) => e.DataMatrixGs1 = v);
                break;
            case BarcodeOption.QrContentSpec:
                // One status row: what the content was built with, or that a hand edit removed the definition. Hidden while there is nothing to say.
                Rows.Add(new NoteRow("Prop.QrContentSpec", () => SpecStatus(El<BarcodeElement>(id))));
                break;
            case BarcodeOption.ModuleSize:
                if (sym == BarcodeSymbology.QrCode)
                    Rows.Add(new IntRow("Prop.ModuleSize", () => El<BarcodeElement>(id).ModuleSize, v => Edit<BarcodeElement>(id, "Prop.ModuleSize", e => e.ModuleSize = v!.Value), 1, 10,
                        rangeIssueKey: "Barcode.ModuleSize.Range")
                    { FullWidth = true, DetailSource = () => ModuleSizeDetail(El<BarcodeElement>(id)) });
                else
                    AddInt<BarcodeElement>(id, "Prop.ModuleSize", e => e.ModuleSize, (e, v) => e.ModuleSize = v, 1, 10, "Barcode.ModuleSize.Range");
                break;
            case BarcodeOption.Columns:
                if (sym == BarcodeSymbology.Codablock)
                    AddInt<BarcodeElement>(id, "Prop.Columns.Chars", e => e.Columns, (e, v) => e.Columns = v, 0, 62, hintKey: "Prop.Columns.Chars.Hint");
                else
                    AddInt<BarcodeElement>(id, "Prop.Columns", e => e.Columns, (e, v) => e.Columns = v, 0, sym == BarcodeSymbology.Pdf417 ? 30 : 144);
                break;
            case BarcodeOption.Rows:
                // Codablock A allows 1-22 rows and E/F 2-4; the box accepts the wider range and Core's message names the exact one.
                AddInt<BarcodeElement>(id, "Prop.Rows", e => e.Rows, (e, v) => e.Rows = v, 0, sym switch { BarcodeSymbology.Codablock => 22, BarcodeSymbology.Pdf417 => 90, _ => 144 });
                break;
            case BarcodeOption.Pdf417SecurityLevel:
                AddInt<BarcodeElement>(id, "Prop.Pdf417Security", e => e.Pdf417SecurityLevel, (e, v) => e.Pdf417SecurityLevel = v, 1, 8);
                break;
            case BarcodeOption.Pdf417Truncate:
                AddBool<BarcodeElement>(id, "Prop.Pdf417Truncate", e => e.Pdf417Truncate, (e, v) => e.Pdf417Truncate = v);
                break;
            case BarcodeOption.Code11CheckDigitCount:
                AddInt<BarcodeElement>(id, "Prop.Code11CheckDigitCount", e => e.Code11CheckDigitCount, (e, v) => e.Code11CheckDigitCount = v, 1, 2);
                break;
            case BarcodeOption.MsiCheckDigit:
                AddEnum<BarcodeElement, MsiCheckDigitMode>(id, "Prop.MsiCheckDigit", e => e.MsiCheckDigit, (e, v) => e.MsiCheckDigit = v);
                break;
            case BarcodeOption.MsiCheckDigitInText:
                AddBool<BarcodeElement>(id, "Prop.MsiCheckDigitInText", e => e.MsiCheckDigitInText, (e, v) => e.MsiCheckDigitInText = v);
                break;
            case BarcodeOption.CodabarStart:
                AddCharChoice(id, "Prop.CodabarStart", "ABCD", e => e.CodabarStart, (e, v) => e.CodabarStart = v);
                break;
            case BarcodeOption.CodabarStop:
                AddCharChoice(id, "Prop.CodabarStop", "ABCD", e => e.CodabarStop, (e, v) => e.CodabarStop = v);
                break;
            case BarcodeOption.DataBarType:
                AddEnum<BarcodeElement, DataBarType>(id, "Prop.DataBarType", e => e.DataBarType, (e, v) => e.DataBarType = v);
                break;
            case BarcodeOption.DataBarSeparatorHeight:
                AddInt<BarcodeElement>(id, "Prop.DataBarSeparatorHeight", e => e.DataBarSeparatorHeight, (e, v) => e.DataBarSeparatorHeight = v, 1, 2, "Barcode.DataBar.Separator");
                break;
            case BarcodeOption.DataBarSegmentsPerRow:
                // Only the Expanded variant has segments; for the others the setting would be ignored, so it is not offered.
                if (element.DataBarType == DataBarType.Expanded)
                    AddInt<BarcodeElement>(id, "Prop.DataBarSegmentsPerRow", e => e.DataBarSegmentsPerRow, (e, v) => e.DataBarSegmentsPerRow = v, 2, 22, "Barcode.DataBar.Segments", hintKey: "Prop.DataBarSegmentsPerRow.Hint");
                break;
            case BarcodeOption.AztecEci:
                AddBool<BarcodeElement>(id, "Prop.AztecEci", e => e.AztecEci, (e, v) => e.AztecEci = v);
                break;
            case BarcodeOption.AztecErrorControl:
                AddInt<BarcodeElement>(id, "Prop.AztecErrorControl", e => e.AztecErrorControl, (e, v) => e.AztecErrorControl = v, 0, 300, hintKey: "Prop.AztecErrorControl.Hint");
                break;
            case BarcodeOption.AztecMenuSymbol:
                AddBool<BarcodeElement>(id, "Prop.AztecMenuSymbol", e => e.AztecMenuSymbol, (e, v) => e.AztecMenuSymbol = v);
                break;
            case BarcodeOption.AztecStructuredAppendCount:
                AddInt<BarcodeElement>(id, "Prop.AztecStructuredAppendCount", e => e.AztecStructuredAppendCount, (e, v) => e.AztecStructuredAppendCount = v, 1, 26, "Barcode.Aztec.StructuredAppend", hintKey: "Prop.AztecStructuredAppendCount.Hint");
                break;
            case BarcodeOption.AztecStructuredAppendId:
                Rows.Add(new TextRow("Prop.AztecStructuredAppendId", () => El<BarcodeElement>(id).AztecStructuredAppendId,
                    v => Edit<BarcodeElement>(id, "aztecid", e => e.AztecStructuredAppendId = v), maxLength: 24));
                break;
            case BarcodeOption.MicroPdf417Mode:
                AddInt<BarcodeElement>(id, "Prop.MicroPdf417Mode", e => e.MicroPdf417Mode, (e, v) => e.MicroPdf417Mode = v, 0, 33, "Barcode.MicroPdf417.Mode", hintKey: "Prop.MicroPdf417Mode.Hint");
                break;
            case BarcodeOption.CodablockMode:
                AddEnum<BarcodeElement, CodablockMode>(id, "Prop.CodablockMode", e => e.CodablockMode, (e, v) => e.CodablockMode = v);
                break;
            case BarcodeOption.CodablockSecurity:
                AddBool<BarcodeElement>(id, "Prop.CodablockSecurity", e => e.CodablockSecurity, (e, v) => e.CodablockSecurity = v);
                break;
            case BarcodeOption.MaxiCodeMode:
                AddInt<BarcodeElement>(id, "Prop.MaxiCodeMode", e => e.MaxiCodeMode, (e, v) => e.MaxiCodeMode = v, 2, 6, "Barcode.MaxiCode.Mode", hintKey: "Prop.MaxiCodeMode.Hint");
                break;
            case BarcodeOption.MaxiCodeSymbolNumber:
                AddInt<BarcodeElement>(id, "Prop.MaxiCodeSymbolNumber", e => e.MaxiCodeSymbolNumber, (e, v) => e.MaxiCodeSymbolNumber = v, 1, 8, "Barcode.MaxiCode.Symbol");
                break;
            case BarcodeOption.MaxiCodeSymbolTotal:
                AddInt<BarcodeElement>(id, "Prop.MaxiCodeSymbolTotal", e => e.MaxiCodeSymbolTotal, (e, v) => e.MaxiCodeSymbolTotal = v, 1, 8, "Barcode.MaxiCode.Symbol");
                break;
            case BarcodeOption.Tlc39MicroModuleWidth:
                AddInt<BarcodeElement>(id, "Prop.Tlc39MicroModuleWidth", e => e.Tlc39MicroModuleWidth, (e, v) => e.Tlc39MicroModuleWidth = v, 1, 10, "Barcode.Tlc39.MicroModuleWidth");
                break;
            case BarcodeOption.Tlc39MicroRowHeight:
                AddInt<BarcodeElement>(id, "Prop.Tlc39MicroRowHeight", e => e.Tlc39MicroRowHeight, (e, v) => e.Tlc39MicroRowHeight = v, 1, 255, "Barcode.Tlc39.MicroRowHeight");
                break;
            case BarcodeOption.Code49StartMode:
                {
                    // 'A' means automatic; the others are the numbered start modes of the symbology.
                    var items = new List<ChoiceItem> { new('A', Loc.Get("Prop.Code49StartMode.Auto")) };
                    items.AddRange("012345".Select(c => new ChoiceItem(c, c.ToString())));
                    Rows.Add(new ChoiceRow("Prop.Code49StartMode", items, () => El<BarcodeElement>(id).Code49StartMode,
                        v => Edit<BarcodeElement>(id, "code49", e => e.Code49StartMode = (char)v!)));
                    break;
                }
        }
    }

    // ----------------------------------------------------------------------------------- QR builder, capacity and size

    private Func<QrContentBuilderViewModel, bool>? _showQrBuilder;
    private Func<Gs1BuilderViewModel, bool>? _showGs1Builder;
    private long _fieldsVersion;
    private string _measureKey = string.Empty;
    private (string? Payload, int Records) _measured;

    /// <summary>The most records looked at when measuring how full a QR code is (the longest of them decides).</summary>
    private const int MaxMeasuredRecords = 2000;

    /// <summary>Connects the windows of the content builder and the GS1 builder (each returns true when the user applied). Without them the buttons do nothing.</summary>
    public void AttachBuilders(Func<QrContentBuilderViewModel, bool> showQrBuilder, Func<Gs1BuilderViewModel, bool> showGs1Builder)
    {
        _showQrBuilder = showQrBuilder;
        _showGs1Builder = showGs1Builder;
    }

    /// <summary>The "Built with: ..." line of the QR content, or the note that a hand edit removed the definition; empty when neither applies.</summary>
    private string SpecStatus(BarcodeElement b)
    {
        if (b.QrContentSpec.Length > 0)
            return QrContentSpec.TryParse(b.QrContentSpec, out var spec)
                ? Loc.Get("QrContent.Status.Built", Loc.Get(QrContentSchema.For(spec.Kind).LabelKey))
                : Loc.Get("QrContent.Status.Unreadable");
        return _specCleared.Contains(b.Id) ? Loc.Get("QrContent.Status.Cleared") : string.Empty;
    }

    /// <summary>
    /// The longest text the QR code will carry, over the loaded records (or the sample values when no data is loaded). Cached, because the
    /// panel refreshes on every key press and resolving thousands of records each time would be wasteful.
    /// </summary>
    private (string? Payload, int Records) LongestPayload(BarcodeElement b)
    {
        var doc = _session.Document;
        string key = string.Join('\u0001', b.Data, b.QrContentSpec, b.QrErrorCorrection, b.QrModel, _fieldsVersion, string.Join(';', doc.Variables.Select(v => v.Name + "=" + v.SampleValue + (v.Kind == Rotula.Core.Variables.VariableKind.Text ? string.Empty : "|" + Rotula.Files.RotulaFileSerializer.Fingerprint(v)))));
        if (key == _measureKey) return _measured;
        var records = _fields?.SampleRecords(MaxMeasuredRecords).ToList() ?? new List<IReadOnlyDictionary<string, string>>();
        var sets = records.Count > 0 ? records.Cast<IReadOnlyDictionary<string, string>?>() : new IReadOnlyDictionary<string, string>?[] { null };
        var texts = new List<string>();
        bool defined = Rotula.Core.Variables.VariableEngine.HasDefinitions(doc);
        foreach (var rawRecord in sets)
        {
            var scratch = new List<ValidationIssue>();
            // Resolve dates, counters and computed values first, so the code is sized for what is really printed.
            var record = defined
                ? Rotula.Core.Variables.VariableEngine.BuildPreviewData(doc, new Rotula.Core.Variables.VariableContext { Record = rawRecord })
                : rawRecord;
            string text = QrContentBuilder.ResolveForElement(b, record, doc.Variables, scratch) ?? VariableResolver.Resolve(b.Data, record, doc.Variables, b.Id, scratch);
            if (text.Length > 0) texts.Add(text);
        }
        _measured = (QrSizing.Longest(texts), records.Count);
        _measureKey = key;
        return _measured;
    }

    private RowDetail? CapacityDetail(BarcodeElement b)
    {
        var (payload, records) = LongestPayload(b);
        var report = QrCapacityInfo.Describe(payload, b.QrErrorCorrection, b.QrModel);
        var lines = new List<string> { report.Text };
        if (report.Hint.Length > 0) lines.Add(report.Hint);
        if (records > 1 && payload is not null) lines.Add(Loc.Get("Qr.Capacity.Basis", records));
        return new RowDetail(string.Join(Environment.NewLine, lines), report.State is CapacityState.Dense or CapacityState.Overflow);
    }

    private RowDetail? ModuleSizeDetail(BarcodeElement b)
    {
        var report = QrCapacityInfo.Describe(LongestPayload(b).Payload, b.QrErrorCorrection, b.QrModel);
        // Model 1 has no version table and an overflowing code has no symbol: the module is still worth describing.
        int version = report.Fit is { Fits: true, Version: > 0 } fit ? fit.Version : 0;
        return QrCapacityInfo.ModuleSize(version, b.ModuleSize, (int)_session.Document.Dpi);
    }

    /// <summary>
    /// Opens the QR content builder for a barcode on the label and, when the user applies it, stores the structured definition and the
    /// text it builds in one undo step. Returns true when something was applied.
    /// </summary>
    public bool EditQrContent(string elementId)
    {
        if (_showQrBuilder is null || _session.Document.Elements.FirstOrDefault(x => x.Id == elementId) is not BarcodeElement b) return false;
        var vm = NewQrBuilder(b);
        bool shown;
        try { shown = _showQrBuilder(vm); }
        finally { vm.Detach(); }
        if (!shown || !vm.Applied) return false;
        string template = vm.Template, spec = vm.SpecJson;
        var level = vm.Level;
        // One edit with no merge key: it never merges into the typing step before it, and one Undo takes back data, definition and level together.
        _session.Edit("Undo.QrContentBuilder", d =>
        {
            if (d.Elements.FirstOrDefault(x => x.Id == elementId) is not BarcodeElement target) return;
            ApplyQrResult(target, template, spec, level);
        });
        _specCleared.Remove(elementId);
        return true;
    }

    /// <summary>
    /// Lets the user start a new QR code from the content builder. The element is not on the label yet, so it is changed in place and the caller
    /// adds it (the whole thing is then one undo step). Returns true when the user applied the builder.
    /// </summary>
    public bool PrepareNewQr(BarcodeElement element)
    {
        if (_showQrBuilder is null) return false;
        var vm = NewQrBuilder(element);
        bool shown;
        try { shown = _showQrBuilder(vm); }
        finally { vm.Detach(); }
        if (!shown || !vm.Applied) return false;
        ApplyQrResult(element, vm.Template, vm.SpecJson, vm.Level);
        return true;
    }

    private static void ApplyQrResult(BarcodeElement target, string template, string specJson, QrErrorCorrection level)
    {
        // Data first, then the definition: the definition is only valid for exactly this Data (see FieldsViewModel.SetContent).
        target.Data = template;
        target.QrContentSpec = specJson;
        target.QrErrorCorrection = level;
    }

    private QrContentBuilderViewModel NewQrBuilder(BarcodeElement b)
    {
        QrContentSpec? initial = b.QrContentSpec.Length > 0 && QrContentSpec.TryParse(b.QrContentSpec, out var spec) ? spec : null;
        var settings = new QrBuilderSettings(b.QrErrorCorrection, b.QrModel, b.ModuleSize, (int)_session.Document.Dpi);
        return new QrContentBuilderViewModel(_fields, initial, settings, initial is null ? b.Data : null, _session.Document.Variables);
    }

    /// <summary>
    /// Opens the GS1 builder for a Code 128 or Data Matrix barcode and, when the user applies it, stores the element string and switches the
    /// barcode to its GS1 form, all in one undo step. Returns true when something was applied.
    /// </summary>
    public bool EditGs1(string elementId)
    {
        if (_showGs1Builder is null || _session.Document.Elements.FirstOrDefault(x => x.Id == elementId) is not BarcodeElement b) return false;
        var vm = new Gs1BuilderViewModel(_fields, b.Symbology, b.Data, _session.Document.Variables);
        bool shown;
        try { shown = _showGs1Builder(vm); }
        finally { vm.Detach(); }
        if (!shown || !vm.Applied) return false;
        string data = vm.Template;
        _session.Edit("Undo.Gs1Builder", d =>
        {
            if (d.Elements.FirstOrDefault(x => x.Id == elementId) is not BarcodeElement target) return;
            target.Data = data;
            // Without these switches the data is just text: GS1-128 needs mode D, GS1 DataMatrix its option (and cannot also be a Digital Link).
            if (target.Symbology == BarcodeSymbology.Code128) target.Code128Mode = Code128Mode.Ean;
            else if (target.Symbology == BarcodeSymbology.DataMatrix) { target.DataMatrixGs1 = true; target.DigitalLink = false; }
        });
        return true;
    }

    /// <summary>A drop-down of single characters (for example the Codabar start letters A to D).</summary>
    private void AddCharChoice(string id, string labelKey, string choices, Func<BarcodeElement, char> get, Action<BarcodeElement, char> set) =>
        Rows.Add(new ChoiceRow(labelKey, choices.Select(c => new ChoiceItem(c, c.ToString())).ToList(),
            () => get(El<BarcodeElement>(id)), v => Edit<BarcodeElement>(id, labelKey, e => set(e, (char)v!))));

    // ----------------------------------------------------------------------------------- label rows

    private void EditDoc(string prop, Action<LabelDocument> change) =>
        _session.Edit("Undo.LabelSettings", change, mergeKey: "doc:" + prop);

    private void BuildDocumentRows()
    {
        var doc = () => _session.Document;
        Rows.Add(new HeadingRow("Panel.Size"));
        // The label stock the size came from, when it came from one (the stock picker of the New label dialog stores its id).
        if (!string.IsNullOrEmpty(doc().StockId))
            Rows.Add(new NoteRow("Prop.Stock", () => StockLibrary.Describe(doc().StockId, (what, id) => Loc.Get(StockKnown(id) ? "Stock.Panel.Line" : "Stock.Panel.LineMissing", what))));
        string unit = Loc.Get(doc().Width.Unit switch { LengthUnit.Inches => "Unit.Short.Inches", LengthUnit.Dots => "Unit.Short.Dots", _ => "Unit.Short.Millimeters" });
        Rows.Add(new DoubleRow("Prop.LabelWidth", () => doc().Width.Value,
            v => EditDoc("w", d => { d.Width = new Measure(v, d.Width.Unit); d.StockId = null; }), 0.1, 100000) { Label = Loc.Get("Prop.LabelWidth.Unit", unit) });
        Rows.Add(new DoubleRow("Prop.LabelHeight", () => doc().Height.Value,
            v => EditDoc("h", d => { d.Height = new Measure(v, d.Height.Unit); d.StockId = null; }), 0.1, 100000) { Label = Loc.Get("Prop.LabelHeight.Unit", unit) });
        Rows.Add(new ChoiceRow("Prop.LabelUnit", ChoiceRow.ForEnum<LengthUnit>(), () => doc().Width.Unit, v =>
            EditDoc("unit", d => ConvertSizeUnit(d, (LengthUnit)v!))) { RebuildOnChange = true });
        Rows.Add(new ChoiceRow("Prop.Dpi", ChoiceRow.ForEnum<PrinterDpi>(), () => doc().Dpi, v => EditDoc("dpi", d => d.Dpi = (PrinterDpi)v!)));
        Rows.Add(new ChoiceRow("Prop.Orientation", ChoiceRow.ForEnum<LabelOrientation>(), () => doc().Orientation, v => EditDoc("orient", d => d.Orientation = (LabelOrientation)v!)));

        Rows.Add(new HeadingRow("Panel.Margins"));
        Rows.Add(new IntRow("Prop.MarginLeft", () => doc().Margins.Left.ToDots(doc().Dpi), v => EditDoc("ml", d => d.Margins.Left = Measure.Dots(v!.Value)), 0, 32000));
        Rows.Add(new IntRow("Prop.MarginTop", () => doc().Margins.Top.ToDots(doc().Dpi), v => EditDoc("mt", d => d.Margins.Top = Measure.Dots(v!.Value)), 0, 32000));
        Rows.Add(new IntRow("Prop.MarginRight", () => doc().Margins.Right.ToDots(doc().Dpi), v => EditDoc("mr", d => d.Margins.Right = Measure.Dots(v!.Value)), 0, 32000));
        Rows.Add(new IntRow("Prop.MarginBottom", () => doc().Margins.Bottom.ToDots(doc().Dpi), v => EditDoc("mb", d => d.Margins.Bottom = Measure.Dots(v!.Value)), 0, 32000));

        Rows.Add(new HeadingRow("Panel.Defaults"));
        Rows.Add(new ChoiceRow("Prop.DefaultRotation", ChoiceRow.ForEnum<FieldRotation>(), () => doc().DefaultRotation, v => EditDoc("drot", d => d.DefaultRotation = (FieldRotation)v!)));
        Rows.Add(new IntRow("Prop.DefaultFontHeight", () => doc().DefaultFont?.Height ?? 30,
            v => EditDoc("dfh", d => (d.DefaultFont ??= new FontSpec('0', 30, 30)).Height = v!.Value), 10, 32000));
        Rows.Add(new IntRow("Prop.DefaultFontWidth", () => doc().DefaultFont?.Width ?? 30,
            v => EditDoc("dfw", d => (d.DefaultFont ??= new FontSpec('0', 30, 30)).Width = v!.Value), 0, 32000));
        var encodings = new List<ChoiceItem>
        {
            new(ZplEncoding.Utf8, Loc.Get("Encoding.Utf8")),
            new(ZplEncoding.Cp850, Loc.Get("Encoding.Cp850")),
            new(ZplEncoding.Cp1252, Loc.Get("Encoding.Cp1252")),
        };
        Rows.Add(new ChoiceRow("Prop.Encoding", encodings, () => doc().Encoding, v => EditDoc("enc", d => d.Encoding = (ZplEncoding)v!)));
        Rows.Add(new ChoiceRow("Prop.MediaType", ChoiceRow.ForEnum<MediaType>(), () => doc().MediaType, v => EditDoc("mtype", d => d.MediaType = (MediaType)v!)));
        Rows.Add(new ChoiceRow("Prop.MediaTracking", ChoiceRow.ForEnum<MediaTracking>(), () => doc().MediaTracking, v => EditDoc("mtr", d => d.MediaTracking = (MediaTracking)v!)));

        Rows.Add(new HeadingRow("Panel.PrintJob"));
        Rows.Add(new IntRow("Prop.Quantity", () => doc().Print.Quantity, v => EditDoc("qty", d => d.Print.Quantity = v!.Value), 1, 99999999));
        Rows.Add(new IntRow("Prop.PauseEvery", () => doc().Print.PauseEvery, v => EditDoc("pause", d => d.Print.PauseEvery = v!.Value), 0, 99999999));
        Rows.Add(new IntRow("Prop.Replicates", () => doc().Print.Replicates, v => EditDoc("rep", d => d.Print.Replicates = v!.Value), 1, 99999999));

        Rows.Add(new HeadingRow("Panel.Variables"));
        Rows.Add(new TextRow("Prop.Variables", () => VariablesToText(doc()), v => EditDoc("vars", d => ApplyVariables(d, v)),
            multiline: true, hintKey: "Prop.Variables.Hint", validate: v => ParseVariables(v, out _) ? null : Loc.Get("Validation.Variables")));
    }

    /// <summary>True when the stock id is in the library.</summary>
    internal static bool StockKnown(string id) => StockLibrary.Current.Catalog.Find(id) is not null;

    /// <summary>Changes the unit of the label size and converts the numbers so the physical size stays the same.</summary>
    private static void ConvertSizeUnit(LabelDocument d, LengthUnit unit)
    {
        int dpi = (int)d.Dpi;
        double Convert(Measure m) => Math.Round(UnitConverter.Convert(m.Value, m.Unit, unit, dpi), unit == LengthUnit.Dots ? 0 : 3);
        d.Width = new Measure(Convert(d.Width), unit);
        d.Height = new Measure(Convert(d.Height), unit);
    }

    /// <summary>One <c>name=sample</c> per line.</summary>
    public static string VariablesToText(LabelDocument d) =>
        string.Join(Environment.NewLine, d.Variables.Select(v => v.Name + "=" + v.SampleValue));

    /// <summary>Parses the variables text; false when a line is not <c>name=value</c> with a valid name.</summary>
    public static bool ParseVariables(string text, out List<LabelVariable> variables)
    {
        variables = new List<LabelVariable>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            int eq = line.IndexOf('=');
            string name = (eq < 0 ? line : line[..eq]).Trim();
            if (eq < 0 || name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;
            if (variables.Any(v => v.Name == name)) return false;
            variables.Add(new LabelVariable(name, line[(eq + 1)..]));
        }
        return true;
    }

    private static void ApplyVariables(LabelDocument d, string text)
    {
        if (!ParseVariables(text, out var list)) return;
        // The text box only shows "name=sample". A variable that already exists keeps its definition (date, counter, prompt, computed)
        // and only takes the new sample value; rebuilding it from the line would silently turn it into plain text.
        var merged = new List<LabelVariable>(list.Count);
        foreach (var edited in list)
        {
            var existing = d.Variables.FirstOrDefault(v => string.Equals(v.Name, edited.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { merged.Add(edited); continue; }
            var kept = existing.Clone();
            kept.Name = edited.Name;
            kept.SampleValue = edited.SampleValue;
            merged.Add(kept);
        }
        d.Variables.Clear();
        d.Variables.AddRange(merged);
    }

    /// <summary>Called by rows that request a rebuild (symbology, block toggle, unit).</summary>
    internal void RebuildNow() => Rebuild(force: true);
}
