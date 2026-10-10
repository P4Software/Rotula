using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.Core.Elements;
using Rotula.Core.Model;
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

    /// <summary>The rows currently shown.</summary>
    public ObservableCollection<PropertyRow> Rows { get; } = new();

    /// <summary>Translated validation messages for the current selection (or for the label).</summary>
    public ObservableCollection<string> IssueMessages { get; } = new();

    /// <summary>Heading above the rows.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

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
        if (sel.Count != 1) return sel.Count == 0 ? "doc" : "multi" + sel.Count;
        var e = _session.Document.Elements.FirstOrDefault(x => x.Id == sel[0]);
        // The symbology decides which barcode rows exist, so it is part of the key.
        return sel[0] + ":" + (e is BarcodeElement b ? b.Symbology.ToString() : e?.GetType().Name) + ":" + (e is TextElement t && t.Block is not null);
    }

    private void Rebuild(bool force = false)
    {
        string key = SelectionKey();
        if (!force && key == _builtFor) return;
        _builtFor = key;
        Rows.Clear();
        Hint = string.Empty;
        var sel = _session.SelectedElements;
        if (sel.Count == 0)
        {
            Title = Loc.Get("Panel.Label");
            BuildDocumentRows();
        }
        else if (sel.Count > 1)
        {
            Title = Loc.Get("Panel.Selection");
            Hint = Loc.Get("Panel.MultiHint", sel.Count);
        }
        else
        {
            Title = Loc.Get("Panel.Element", ElementDisplayName(sel[0]));
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
            DataFieldElement => Loc.Get("Element.DataField"),
            TextElement => Loc.Get("Element.Text"),
            BarcodeElement b => Loc.EnumName(b.Symbology),
            BoxElement => Loc.Get("Element.Box"),
            LineElement => Loc.Get("Element.Line"),
            CircleElement => Loc.Get("Element.Circle"),
            ImageElement => Loc.Get("Element.Image"),
            _ => e.GetType().Name,
        };
        return string.IsNullOrWhiteSpace(e.Name) ? type : $"{e.Name} ({type})";
    }

    // ----------------------------------------------------------------------------------- element rows

    private T El<T>(string id) where T : LabelElement => (T)_session.Document.Elements.First(x => x.Id == id);

    private void Edit<T>(string id, string prop, Action<T> change) where T : LabelElement =>
        _session.Edit("Undo.Property", d => change((T)d.Elements.First(x => x.Id == id)), mergeKey: $"prop:{id}:{prop}");

    private void AddInt<T>(string id, string labelKey, Func<T, int> get, Action<T, int> set, int min, int max) where T : LabelElement =>
        Rows.Add(new IntRow(labelKey, () => get(El<T>(id)), v => Edit<T>(id, labelKey, e => set(e, v!.Value)), min, max));

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
        if (element is not CircleElement)
        {
            // Null means "use the label default (^FW)".
            var items = new List<ChoiceItem> { new(null, Loc.Get("Prop.Rotation.Inherit")) };
            items.AddRange(ChoiceRow.ForEnum<FieldRotation>());
            Rows.Add(new ChoiceRow("Prop.Rotation", items, () => El<LabelElement>(id).Rotation, v => Edit<LabelElement>(id, "rotation", e => e.Rotation = (FieldRotation?)v)));
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

    private void BuildTextRows(TextElement element)
    {
        string id = element.Id;
        Rows.Add(new HeadingRow("Panel.Text"));
        if (element is DataFieldElement)
        {
            AddText<DataFieldElement>(id, "Prop.Variable", e => e.VariableName, (e, v) => e.VariableName = v.Trim());
        }
        else
        {
            AddText<TextElement>(id, "Prop.Text", e => e.Text, (e, v) => e.Text = v, multiline: true);
        }
        Rows.Add(new TextRow("Prop.FontId", () => El<TextElement>(id).FontId?.ToString() ?? string.Empty,
            v => Edit<TextElement>(id, "fontid", e => e.FontId = v.Length == 0 ? null : v[0]), maxLength: 1, hintKey: "Prop.FontId.Hint",
            validate: v => v.Length == 1 && !char.IsLetterOrDigit(v[0]) ? Loc.Get("Validation.FontId") : null));
        AddNullableInt<TextElement>(id, "Prop.FontHeight", e => e.FontHeight, (e, v) => e.FontHeight = v, 10, 32000, "Prop.Inherit.Hint");
        AddNullableInt<TextElement>(id, "Prop.FontWidth", e => e.FontWidth, (e, v) => e.FontWidth = v, 0, 32000, "Prop.Inherit.Hint");

        Rows.Add(new BoolRow("Prop.Block", () => El<TextElement>(id).Block is not null,
            v => Edit<TextElement>(id, "block", e => e.Block = v ? new TextBlockOptions() : null)) { RebuildOnChange = true });
        if (element.Block is not null)
        {
            AddInt<TextElement>(id, "Prop.Block.Width", e => e.Block!.WidthDots, (e, v) => e.Block!.WidthDots = v, 1, 9999);
            AddInt<TextElement>(id, "Prop.Block.MaxLines", e => e.Block!.MaxLines, (e, v) => e.Block!.MaxLines = v, 1, 9999);
            AddInt<TextElement>(id, "Prop.Block.LineSpacing", e => e.Block!.LineSpacing, (e, v) => e.Block!.LineSpacing = v, -9999, 9999);
            AddEnum<TextElement, TextAlignment>(id, "Prop.Block.Alignment", e => e.Block!.Alignment, (e, v) => e.Block!.Alignment = v);
            AddInt<TextElement>(id, "Prop.Block.HangingIndent", e => e.Block!.HangingIndent, (e, v) => e.Block!.HangingIndent = v, 0, 9999);
        }
    }

    private void BuildBarcodeRows(BarcodeElement element)
    {
        string id = element.Id;
        var sym = element.Symbology;
        bool linear = sym is BarcodeSymbology.Code128 or BarcodeSymbology.Code39 or BarcodeSymbology.Code93
            or BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA or BarcodeSymbology.Interleaved2Of5;
        bool pdf = sym == BarcodeSymbology.Pdf417;
        bool twoD = sym is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix;

        Rows.Add(new HeadingRow("Panel.Barcode"));
        Rows.Add(new ChoiceRow("Prop.Symbology", ChoiceRow.ForEnum<BarcodeSymbology>(),
            () => El<BarcodeElement>(id).Symbology, v => Edit<BarcodeElement>(id, "symbology", e => e.Symbology = (BarcodeSymbology)v!)) { RebuildOnChange = true });
        // Barcode data is validated by Core (BarcodeValidator); the messages appear below the rows while the user types,
        // instead of blocking the typing, because half-typed data is normally invalid.
        AddText<BarcodeElement>(id, "Prop.Data", e => e.Data, (e, v) => e.Data = v, multiline: false);
        if (linear || pdf)
        {
            AddInt<BarcodeElement>(id, "Prop.BarHeight", e => e.Height, (e, v) => e.Height = v, 1, 32000);
            AddInt<BarcodeElement>(id, "Prop.ModuleWidth", e => e.ModuleWidth, (e, v) => e.ModuleWidth = v, 1, 10);
        }
        if (linear)
        {
            Rows.Add(new DoubleRow("Prop.WideBarRatio", () => El<BarcodeElement>(id).WideBarRatio,
                v => Edit<BarcodeElement>(id, "ratio", e => e.WideBarRatio = v), 2.0, 3.0));
            AddBool<BarcodeElement>(id, "Prop.HumanReadable", e => e.HumanReadable, (e, v) => e.HumanReadable = v);
            AddEnum<BarcodeElement, HumanReadablePosition>(id, "Prop.HumanReadablePosition", e => e.HumanReadablePosition, (e, v) => e.HumanReadablePosition = v);
        }
        if (sym is BarcodeSymbology.Code39 or BarcodeSymbology.Code93 or BarcodeSymbology.Interleaved2Of5 or BarcodeSymbology.UpcA)
            AddBool<BarcodeElement>(id, "Prop.CheckDigit", e => e.CheckDigit, (e, v) => e.CheckDigit = v);
        if (sym == BarcodeSymbology.Code128)
            AddEnum<BarcodeElement, Code128Mode>(id, "Prop.Code128Mode", e => e.Code128Mode, (e, v) => e.Code128Mode = v);
        if (sym == BarcodeSymbology.QrCode)
        {
            AddEnum<BarcodeElement, QrErrorCorrection>(id, "Prop.QrErrorCorrection", e => e.QrErrorCorrection, (e, v) => e.QrErrorCorrection = v);
            AddInt<BarcodeElement>(id, "Prop.QrModel", e => e.QrModel, (e, v) => e.QrModel = v, 1, 2);
        }
        if (twoD)
            AddInt<BarcodeElement>(id, "Prop.ModuleSize", e => e.ModuleSize, (e, v) => e.ModuleSize = v, 1, 10);
        if (sym == BarcodeSymbology.DataMatrix)
        {
            AddInt<BarcodeElement>(id, "Prop.Columns", e => e.Columns, (e, v) => e.Columns = v, 0, 144);
            AddInt<BarcodeElement>(id, "Prop.Rows", e => e.Rows, (e, v) => e.Rows = v, 0, 144);
        }
        if (pdf)
        {
            AddInt<BarcodeElement>(id, "Prop.Columns", e => e.Columns, (e, v) => e.Columns = v, 0, 30);
            AddInt<BarcodeElement>(id, "Prop.Rows", e => e.Rows, (e, v) => e.Rows = v, 0, 90);
            AddInt<BarcodeElement>(id, "Prop.Pdf417Security", e => e.Pdf417SecurityLevel, (e, v) => e.Pdf417SecurityLevel = v, 1, 8);
            AddBool<BarcodeElement>(id, "Prop.Pdf417Truncate", e => e.Pdf417Truncate, (e, v) => e.Pdf417Truncate = v);
        }
    }

    // ----------------------------------------------------------------------------------- label rows

    private void EditDoc(string prop, Action<LabelDocument> change) =>
        _session.Edit("Undo.LabelSettings", change, mergeKey: "doc:" + prop);

    private void BuildDocumentRows()
    {
        var doc = () => _session.Document;
        Rows.Add(new HeadingRow("Panel.Size"));
        Rows.Add(new DoubleRow("Prop.LabelWidth", () => doc().Width.Value,
            v => EditDoc("w", d => d.Width = new Measure(v, d.Width.Unit)), 0.1, 100000));
        Rows.Add(new DoubleRow("Prop.LabelHeight", () => doc().Height.Value,
            v => EditDoc("h", d => d.Height = new Measure(v, d.Height.Unit)), 0.1, 100000));
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
        d.Variables.Clear();
        d.Variables.AddRange(list);
    }

    /// <summary>Called by rows that request a rebuild (symbology, block toggle, unit).</summary>
    internal void RebuildNow() => Rebuild(force: true);
}
