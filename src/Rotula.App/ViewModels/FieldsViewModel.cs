using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Zpl;

namespace Rotula.App.ViewModels;

/// <summary>Where an available field comes from.</summary>
public enum FieldKind
{
    /// <summary>A column of the loaded data (CSV, Excel or database).</summary>
    Column,
    /// <summary>A serial counter of the Data merge window (<c>#Name</c>).</summary>
    Counter,
    /// <summary>A variable the label declares itself.</summary>
    Variable,
}

/// <summary>What kind of element "Add to label" creates for a field.</summary>
public enum FieldElementKind
{
    /// <summary>A text element whose content is the placeholder.</summary>
    Text,
    /// <summary>A Code 128 barcode whose data is the placeholder.</summary>
    Barcode,
    /// <summary>A QR code whose data is the placeholder.</summary>
    Qr,
}

/// <summary>Naming rules for the <c>{{name}}</c> placeholders that fields turn into.</summary>
public static class FieldNaming
{
    // Same shape as the one Core's VariableResolver accepts; a placeholder that does not match it would never be replaced.
    private static readonly Regex ValidName = new(@"^#?[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex PureBinding = new(@"^\s*\{\{\s*(#?[A-Za-z_][A-Za-z0-9_]*)\s*\}\}\s*$", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> can be written between double braces and be recognised by the label.</summary>
    public static bool IsValidToken(string name) => ValidName.IsMatch(name);

    /// <summary>
    /// The field name when <paramref name="content"/> is nothing but one placeholder (a "pure binding", for example <c>{{Sku}}</c>);
    /// null for fixed text or for text that mixes fixed parts and fields.
    /// </summary>
    public static string? FindBinding(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var m = PureBinding.Match(content);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// A name for <paramref name="column"/> that placeholders can use. Names that already fit are kept. Otherwise accents are
    /// dropped and every other character becomes an underscore ("Unit price" becomes "Unit_price"). <paramref name="taken"/> holds
    /// the names in use (case-insensitive); a clash gets a number appended.
    /// </summary>
    public static string ToToken(string column, ISet<string> taken)
    {
        string name = column.Trim();
        if (!IsValidToken(name) || name.StartsWith('#'))
        {
            var sb = new StringBuilder();
            foreach (char ch in name.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') ? ch : '_');
            }
            name = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
            if (name.Length == 0) name = "Field";
            if (char.IsAsciiDigit(name[0])) name = "_" + name;
        }
        string candidate = name;
        for (int n = 2; taken.Contains(candidate); n++) candidate = name + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}

/// <summary>One field the user can put on the label: a data column, a counter or a label variable.</summary>
public sealed partial class FieldItem : ObservableObject
{
    /// <summary>Creates an item.</summary>
    public FieldItem(string token, string name, FieldKind kind)
    {
        Token = token;
        Name = name;
        Kind = kind;
    }

    /// <summary>The name written between the braces (<c>Sku</c>, <c>#Serial</c>).</summary>
    public string Token { get; }

    /// <summary>The source name: the column as the data calls it, which differs from <see cref="Token"/> only for columns that had to be renamed.</summary>
    public string Name { get; }

    /// <summary>Where the field comes from.</summary>
    public FieldKind Kind { get; }

    /// <summary>True for a column whose name cannot be a placeholder; a match to the column is stored in the Data merge settings.</summary>
    public bool NeedsMapping => Kind == FieldKind.Column && !string.Equals(Token, Name, StringComparison.Ordinal);

    /// <summary>The text as it goes into a label: <c>{{Sku}}</c>.</summary>
    public string Placeholder => "{{" + Token + "}}";

    /// <summary>Second line of a field in the Variables tab: where it comes from, and for a renamed column the original name.</summary>
    public string Detail => NeedsMapping ? KindText + " · " + Name : KindText;

    /// <summary>Translated name of the kind, shown as a small tag.</summary>
    public string KindText => Loc.Get("Fields.Kind." + Kind);

    /// <summary>The value in the record shown on the canvas, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleText), nameof(HasSample))]
    private string _sample = string.Empty;

    /// <summary>The sample for display: "(empty)" when the record has no value.</summary>
    public string SampleText => !HasRecord ? string.Empty : Sample.Length > 0 ? Sample : Loc.Get("Fields.Sample.Empty");

    /// <summary>True when there is a sample value to show.</summary>
    public bool HasSample => HasRecord;

    /// <summary>True while a record is available to take the sample from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSample), nameof(SampleText))]
    private bool _hasRecord;

    /// <summary>Re-reads the translated texts after a language change.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(SampleText));
    }

    /// <summary>The token, so a combo box or a screen reader announces the field by name.</summary>
    public override string ToString() => Token;
}

/// <summary>Outcome of putting fields on the label.</summary>
/// <param name="Added">Number of elements created.</param>
/// <param name="Outside">How many of them do not fit inside the label.</param>
public sealed record FieldAddResult(int Added, int Outside);

/// <summary>
/// The "available fields" service shared by the property panel, the autocomplete and the Data merge window. It lists the columns of the
/// loaded data, the counters' virtual columns and the label's own variables, keeps that list current when data is loaded, replaced or
/// cleared, and offers the editing operations built on it (add to the label, use in the selected element, find fields without data).
/// </summary>
public sealed partial class FieldsViewModel : ObservableObject
{
    private const int Gap = 12;
    private readonly EditorSession _session;
    private readonly EditorCommands _commands;
    private readonly DataMergeViewModel _data;
    private readonly IMessageService _messages;
    private readonly Action<string> _status;
    private readonly Action _openData;
    private HashSet<string> _unmapped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the service.</summary>
    /// <param name="session">The editing session.</param>
    /// <param name="commands">Editing operations (used to add elements).</param>
    /// <param name="data">The data merge view-model that owns the loaded table and the counters.</param>
    /// <param name="messages">Used for the "does not fit" warning.</param>
    /// <param name="status">Shows a message in the status bar.</param>
    /// <param name="openData">Opens the Data merge window.</param>
    public FieldsViewModel(EditorSession session, EditorCommands commands, DataMergeViewModel data, IMessageService messages, Action<string> status, Action openData)
    {
        _session = session;
        _commands = commands;
        _data = data;
        _messages = messages;
        _status = status;
        _openData = openData;
        data.TableChanged += (_, _) => Refresh();
        data.PreviewDataChanged += (_, _) => Refresh();
        data.Counters.CollectionChanged += (_, _) => Refresh();
        // The label's own variables change with the document; the main view-model calls Refresh after the data was bound to the new document.
        _session.SelectionChanged += (_, _) => NotifySelection();
        Loc.Instance.SubscribeWeak(this, static self => self.OnLanguageChanged());
        Refresh();
    }

    /// <summary>The fields that can be inserted, columns first, then counters, then the label's variables.</summary>
    public ObservableCollection<FieldItem> Fields { get; } = new();

    /// <summary>Raised after the list, a sample value, or the set of missing columns changed.</summary>
    public event EventHandler? FieldsChanged;

    /// <summary>True when data is loaded.</summary>
    public bool HasData => _data.HasData;

    /// <summary>True when there is no data loaded (the panel then offers a link to the Data merge window).</summary>
    public bool HasNoData => !_data.HasData;

    /// <summary>True when at least one field can be inserted.</summary>
    public bool HasFields => Fields.Count > 0;

    /// <summary>Number of columns of the loaded data.</summary>
    public int ColumnCount => Fields.Count(f => f.Kind == FieldKind.Column);

    /// <summary>The columns of the loaded data (the ones "Add all fields" puts on the label).</summary>
    public IEnumerable<FieldItem> Columns => Fields.Where(f => f.Kind == FieldKind.Column);

    /// <summary>True when the data columns list has entries.</summary>
    public bool HasColumns => ColumnCount > 0;

    /// <summary>Tooltip of the Insert field button: what it does, or why it is disabled.</summary>
    public string InsertTip => Loc.Get(HasFields ? "Fields.Insert.Tip" : "Fields.Insert.NoFields");

    /// <summary>"3 fields available", read out by screen readers when the list opens.</summary>
    public string CountText => CountTextFor(Fields.Count);

    /// <summary>Translated "N fields available" for any count.</summary>
    public static string CountTextFor(int n) => n switch { 0 => Loc.Get("Fields.Count.None"), 1 => Loc.Get("Fields.Count.One"), _ => Loc.Get("Fields.Count.Many", n) };

    /// <summary>Finds a field by its token (case-insensitive).</summary>
    public FieldItem? Find(string token) => Fields.FirstOrDefault(f => string.Equals(f.Token, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the Data merge window (the "Open Data merge" link).</summary>
    [RelayCommand]
    private void OpenData() => _openData();

    // ----------------------------------------------------------------------------------- keeping the list current

    private void OnLanguageChanged()
    {
        foreach (var f in Fields) f.RefreshLanguage();
        OnPropertyChanged(nameof(InsertTip));
        OnPropertyChanged(nameof(CountText));
        NotifySelection();
    }

    /// <summary>
    /// Rebuilds the list from the loaded data, the counters and the label's variables. When only the sample values changed (the record
    /// navigator moved) the existing items are updated in place, so an open list does not flicker or lose its selection.
    /// </summary>
    public void Refresh()
    {
        var wanted = BuildItems();
        bool same = wanted.Count == Fields.Count && wanted.Zip(Fields).All(p => p.First.Token == p.Second.Token && p.First.Name == p.Second.Name && p.First.Kind == p.Second.Kind);
        if (!same)
        {
            Fields.Clear();
            foreach (var f in wanted) Fields.Add(f);
        }
        else
        {
            for (int i = 0; i < wanted.Count; i++)
            {
                Fields[i].HasRecord = wanted[i].HasRecord;
                Fields[i].Sample = wanted[i].Sample;
            }
        }
        // Names the loaded data cannot feed. This is the same list the Variables tab warns about, so both always agree.
        _unmapped = _data.DataSet is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_data.DataSet.Issues.Where(i => i.MessageKey == "Merge.Placeholder.Unmapped" && i.Args.Count > 0).Select(i => i.Args[0]), StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(HasNoData));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(HasColumns));
        OnPropertyChanged(nameof(InsertTip));
        OnPropertyChanged(nameof(CountText));
        NotifySelection();
        AddAllFieldsCommand.NotifyCanExecuteChanged();
        FieldsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The data column index and the placeholder name of each column field, in the order of the list (kept by BuildItems for SampleRecords).
    private List<(int Column, string Token)> _columnTokens = new();

    /// <summary>
    /// The loaded records as name-to-value dictionaries keyed by placeholder name (at most <paramref name="max"/>), for features that must look at
    /// all of them, such as the QR capacity meter that measures the longest record. Empty when no data is loaded.
    /// </summary>
    public IEnumerable<IReadOnlyDictionary<string, string>> SampleRecords(int max = 1000)
    {
        var table = _data.Table;
        if (table is null || table.RowCount == 0) yield break;
        int count = Math.Min(max, table.RowCount);
        for (int r = 0; r < count; r++)
        {
            var record = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (column, token) in _columnTokens) record[token] = table.Rows[r][column] ?? string.Empty;
            yield return record;
        }
    }

    private List<FieldItem> BuildItems()
    {
        var list = new List<FieldItem>();
        var columnTokens = new List<(int, string)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var table = _data.Table;
        Dictionary<string, string>? record = null;
        if (_data.DataSet is { RecordCount: > 0 } set)
        {
            try { record = set.GetRecordData(Math.Clamp(_data.CurrentIndex, 0, set.RecordCount - 1)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { record = null; }
        }
        if (table is not null)
        {
            // Names that already work are reserved first, so a renamed column never takes the name of another real column.
            foreach (var c in table.Columns) if (FieldNaming.IsValidToken(c) && !c.StartsWith('#')) taken.Add(c);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < table.Columns.Count; i++)
            {
                string column = table.Columns[i];
                string token;
                if (FieldNaming.IsValidToken(column) && !column.StartsWith('#')) token = column;
                else { token = FieldNaming.ToToken(column, taken); taken.Add(token); }
                if (!used.Add(token)) continue; // two columns that differ only by case: the first one wins, as it does in the data
                columnTokens.Add((i, token));
                var item = new FieldItem(token, column, FieldKind.Column) { HasRecord = record is not null };
                if (record is not null && table.RowCount > 0)
                    item.Sample = table.Rows[Math.Clamp(_data.CurrentIndex, 0, table.RowCount - 1)][i] ?? string.Empty;
                list.Add(item);
            }
        }
        foreach (var ed in _data.Counters)
        {
            string token = "#" + ed.Name;
            if (ed.HasError || !FieldNaming.IsValidToken(token) || !taken.Add(token)) continue;
            var item = new FieldItem(token, token, FieldKind.Counter) { HasRecord = record is not null };
            if (record is not null && record.TryGetValue(token, out var v)) item.Sample = v;
            list.Add(item);
        }
        foreach (var v in _session.Document.Variables)
        {
            if (!FieldNaming.IsValidToken(v.Name) || v.Name.StartsWith('#') || !taken.Add(v.Name)) continue;
            var item = new FieldItem(v.Name, v.Name, FieldKind.Variable) { HasRecord = true };
            item.Sample = record is not null && record.TryGetValue(v.Name, out var rv) ? rv : v.SampleValue;
            list.Add(item);
        }
        _columnTokens = columnTokens;
        return list;
    }

    /// <summary>Stores the match between a placeholder name and the column it stands for, when the column's own name cannot be used.</summary>
    public void EnsureMapping(FieldItem field)
    {
        if (field.NeedsMapping) _data.EnsureColumnMapping(field.Token, field.Name);
    }

    // ----------------------------------------------------------------------------------- what the elements use

    /// <summary>The text that can hold placeholders: the content of a text element or the data of a barcode; null for other elements.</summary>
    public static string? TemplateOf(LabelElement e) => e switch
    {
        TextElement t => t.GetTemplate(),
        BarcodeElement b => b.Data,
        _ => null,
    };

    /// <summary>True when the element's content uses at least one field.</summary>
    public static bool IsBound(LabelElement e) => TemplateOf(e) is { } t && VariableResolver.FindVariables(t).Count > 0;

    /// <summary>
    /// Names the element uses that the loaded data has no column for. Empty when no data is loaded (nothing can be missing then) and
    /// for names the label defines itself (dates, counters, prompts).
    /// </summary>
    public IReadOnlyList<string> MissingFor(LabelElement e)
    {
        if (!_data.HasData || _unmapped.Count == 0 || TemplateOf(e) is not { } t) return Array.Empty<string>();
        return VariableResolver.FindVariables(t).Where(_unmapped.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The warning for an element, or an empty string.</summary>
    public string MissingText(LabelElement e)
    {
        var missing = MissingFor(e);
        return missing.Count == 0 ? string.Empty : Loc.Get("Fields.Missing", string.Join(", ", missing));
    }

    // ----------------------------------------------------------------------------------- the selected element

    private LabelElement? SelectedTarget => _session.SelectedElements is [{ } one] && (one is TextElement || one is BarcodeElement) ? one : null;

    /// <summary>One line about the selection for the Variables tab.</summary>
    public string SelectionText => SelectedTarget is { } e ? Loc.Get("Fields.Selected.One", PropertyPanelViewModel.ElementDisplayName(e)) : Loc.Get("Fields.Selected.None");

    /// <summary>True when "Use here" has an element to work on.</summary>
    public bool HasTarget => SelectedTarget is not null;

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasTarget));
        UseHereCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Puts the field into the selected element: the text or barcode data becomes the field, unless the element holds real text, in which
    /// case the field is added at the end. One undo step.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTarget))]
    private void UseHere(FieldItem? field)
    {
        if (field is null || SelectedTarget is not { } target) return;
        string id = target.Id;
        string current = TemplateOf(target) ?? string.Empty;
        bool replace = IsPlaceholderContent(target, current);
        string content = replace ? field.Placeholder : current + field.Placeholder;
        EnsureMapping(field);
        _session.Edit("Undo.InsertField", d =>
        {
            int index = d.Elements.FindIndex(e => e.Id == id);
            SetContent(d, index, content);
        });
        _status(Loc.Get("Fields.Used", field.Placeholder, PropertyPanelViewModel.ElementDisplayName(target)));
    }

    /// <summary>True when the element's content was never typed by the user (empty, a placeholder text or sample data), so a field may replace it.</summary>
    private static bool IsPlaceholderContent(LabelElement e, string content)
    {
        if (content.Length == 0 || FieldNaming.FindBinding(content) is not null) return true;
        if (e is BarcodeElement) return PropertyPanelViewModel.IsSampleData(content);
        return Loc.SupportedLanguages.Any(l => content == DefaultTextIn(l));
    }

    private static string DefaultTextIn(string language)
    {
        // Full culture names ("pt-BR"), as in Loc.SupportedLanguages: a two-letter name would never match them.
        if (Loc.Instance.LanguageCode == language) return Loc.Get("Element.DefaultText");
        // The label may have been designed in another language: compare with that language's default text too.
        var rm = new System.Resources.ResourceManager("Rotula.App.Resources.Strings", typeof(Loc).Assembly);
        try { return rm.GetString("Element.DefaultText", CultureInfo.GetCultureInfo(language)) ?? string.Empty; }
        catch (System.Resources.MissingManifestResourceException) { return string.Empty; }
    }

    /// <summary>
    /// Sets the text or barcode content of the element at <paramref name="index"/>. A legacy data-field element keeps its type while the
    /// content is a single field; any other content turns it into a plain text element.
    /// </summary>
    public static void SetContent(LabelDocument d, int index, string content)
    {
        if (index < 0) return;
        switch (d.Elements[index])
        {
            case DataFieldElement df when FieldNaming.FindBinding(content) is { } name:
                df.VariableName = name;
                break;
            case DataFieldElement df:
                d.Elements[index] = ElementCloner.ToPlainText(df, content);
                break;
            case TextElement t:
                t.Text = content;
                break;
            case BarcodeElement b:
                b.Data = content;
                // The QR content builder's definition describes exactly the text it built. Any other text (typed, inserted, bound) makes the
                // definition a second opinion that would win when printing, so it is dropped here, for every way of editing the content.
                if (b.QrContentSpec.Length > 0 && !(Rotula.Core.Barcodes.QrContent.QrContentSpec.TryParse(b.QrContentSpec, out var spec)
                        && Rotula.Core.Barcodes.QrContent.QrContentBuilder.BuildTemplate(spec) == content))
                    b.QrContentSpec = string.Empty;
                break;
        }
    }

    // ----------------------------------------------------------------------------------- adding to the label

    private int FontHeight => _session.Document.DefaultFont?.Height ?? 30;

    private (int Left, int Top, int Bottom) Frame()
    {
        var d = _session.Document;
        int marginLeft = d.Margins.Left.ToDots(d.Dpi), marginTop = d.Margins.Top.ToDots(d.Dpi), marginBottom = d.Margins.Bottom.ToDots(d.Dpi);
        // Positions are relative to the margins, so a label that has margins needs no extra inset; one without gets a small one.
        return (marginLeft > 0 ? 0 : 20, marginTop > 0 ? 0 : 20, d.HeightDots - marginTop - marginBottom);
    }

    private int LowestBottom()
    {
        var els = _session.Document.Elements;
        return els.Count == 0 ? 0 : (int)Math.Ceiling(els.Max(e => _session.Bounds.GetBounds(e).Bottom));
    }

    private LabelElement NewElement(FieldItem f, FieldElementKind kind, int x, int y, int? fontHeight)
    {
        switch (kind)
        {
            case FieldElementKind.Barcode:
                return new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = f.Placeholder, Height = 60, X = x, Y = y };
            case FieldElementKind.Qr:
                return new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = f.Placeholder, ModuleSize = 4, X = x, Y = y };
            default:
                var t = new TextElement { Text = f.Placeholder, X = x, Y = y };
                if (fontHeight is { } h && h != FontHeight)
                {
                    int w = _session.Document.DefaultFont?.Width ?? h;
                    t.FontHeight = h;
                    t.FontWidth = Math.Max(1, (int)Math.Round(w * (double)h / FontHeight));
                }
                return t;
        }
    }

    // A rough height before the element exists (the real one needs the renderer): used only to choose a position.
    private int EstimatedHeight(FieldElementKind kind, int fontHeight) => kind switch
    {
        FieldElementKind.Barcode => 60 + fontHeight + 10,
        FieldElementKind.Qr => 4 * 29,
        _ => fontHeight,
    };

    private static void DeclareVariable(LabelDocument d, FieldItem f)
    {
        // A text variable keeps the label readable and printable when no data is loaded (the sample value), and costs nothing when it is.
        if (f.Kind != FieldKind.Column || !FieldNaming.IsValidToken(f.Token)) return;
        if (d.Variables.Any(v => string.Equals(v.Name, f.Token, StringComparison.OrdinalIgnoreCase))) return;
        d.Variables.Add(new LabelVariable(f.Token, f.Sample.Length > 60 ? f.Sample[..60] : f.Sample));
    }

    /// <summary>Adds a text element, a barcode or a QR code whose content is the field, at the next free place, and selects it. One undo step.</summary>
    public FieldAddResult AddToLabel(FieldItem field, FieldElementKind kind = FieldElementKind.Text)
    {
        var (left, top, limit) = Frame();
        int fontH = FontHeight;
        int height = EstimatedHeight(kind, fontH);
        int y = _session.Document.Elements.Count == 0 ? top : LowestBottom() + Gap;
        if (y + height > limit)
        {
            // Not below the others: look for a free band further up before giving up.
            int free = FindFreeBand(left, top, limit, height);
            if (free >= 0) y = free;
        }
        var element = NewElement(field, kind, left, y, null);
        bool outside = y + height > limit;
        EnsureMapping(field);
        _commands.Add(element, d => DeclareVariable(d, field));
        _status(Loc.Get("Fields.Added.One", field.Placeholder));
        if (outside) _status(Loc.Get("Fields.Outside.One"));
        return new FieldAddResult(1, outside ? 1 : 0);
    }

    private int FindFreeBand(int left, int top, int limit, int height)
    {
        var rects = _session.Document.Elements.Select(e => _session.Bounds.GetBounds(e)).ToList();
        for (int y = top; y + height <= limit; y += 8)
        {
            var band = new RectD(left, y - Gap / 2, 200, height + Gap);
            if (!rects.Any(r => r.Intersects(band))) return y;
        }
        return -1;
    }

    /// <summary>
    /// Adds one text element per data column, stacked top to bottom with an even gap below the existing elements. The text is made smaller
    /// when needed so the stack fits the label; when it still does not fit, the elements are placed anyway and the user is told. One undo step.
    /// </summary>
    public FieldAddResult AddAll()
    {
        var columns = Columns.ToList();
        if (columns.Count == 0) return new FieldAddResult(0, 0);
        var (left, top, limit) = Frame();
        int start = _session.Document.Elements.Count == 0 ? top : LowestBottom() + Gap;
        int n = columns.Count;
        // The largest font (down to a still readable 14 dots) for which the whole stack fits; the gap shrinks with the font.
        int font = FontHeight, gap = Gap;
        for (int f = FontHeight; f >= 14; f -= 2)
        {
            int g = Math.Max(4, f / 3);
            if (start + n * f + (n - 1) * g <= limit) { font = f; gap = g; break; }
            font = f; gap = g;
        }
        var made = new List<LabelElement>();
        int y = start;
        foreach (var c in columns)
        {
            made.Add(NewElement(c, FieldElementKind.Text, left, y, font));
            y += font + gap;
        }
        int outside = made.Count(e => e.Y + font > limit);
        foreach (var c in columns) EnsureMapping(c);
        _session.Edit("Undo.AddFields", d =>
        {
            foreach (var c in columns) DeclareVariable(d, c);
            d.Elements.AddRange(made);
        }, selectionAfter: () => made.Select(m => m.Id).ToList());
        _status(Loc.Get("Fields.Added.Many", made.Count));
        if (outside > 0)
            _messages.ShowInfo(Loc.Get("Fields.Outside.Title"), Loc.Get("Fields.Outside.Many", outside, made.Count));
        return new FieldAddResult(made.Count, outside);
    }

    /// <summary>Adds the field as a text element.</summary>
    [RelayCommand]
    private void AddText(FieldItem? field) { if (field is not null) AddToLabel(field, FieldElementKind.Text); }

    /// <summary>Adds the field as a Code 128 barcode.</summary>
    [RelayCommand]
    private void AddBarcode(FieldItem? field) { if (field is not null) AddToLabel(field, FieldElementKind.Barcode); }

    /// <summary>Adds the field as a QR code.</summary>
    [RelayCommand]
    private void AddQr(FieldItem? field) { if (field is not null) AddToLabel(field, FieldElementKind.Qr); }

    /// <summary>Adds every data column to the label (command form of <see cref="AddAll"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasColumns))]
    private void AddAllFields() => AddAll();
}
