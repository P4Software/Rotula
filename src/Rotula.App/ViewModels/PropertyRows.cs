using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;

namespace Rotula.App.ViewModels;

/// <summary>The second line of a row: a text and whether it is a warning.</summary>
/// <param name="Text">The text.</param>
/// <param name="IsWarning">True to show it in the warning colour.</param>
public sealed record RowDetail(string Text, bool IsWarning = false);

/// <summary>
/// One editable line of the property panel. Rows read and write through delegates that look the element up by id at the
/// moment they run, because undo/redo replaces the element objects in the document.
/// </summary>
public abstract partial class PropertyRow : ObservableObject
{
    /// <summary>Creates a row.</summary>
    protected PropertyRow(string labelKey, string? hintKey = null)
    {
        LabelKey = labelKey;
        Label = Loc.Instance[labelKey];
        Hint = hintKey is null ? null : Loc.Instance[hintKey];
    }

    /// <summary>Resource key of the label (also used as the automation name).</summary>
    public string LabelKey { get; }

    /// <summary>Translated label (a row can override it with an object initializer, for example to add the unit: "Width (mm)").</summary>
    public string Label { get; init; }

    /// <summary>Optional translated hint shown as tool tip.</summary>
    public string? Hint { get; }

    /// <summary>Plain-language problem with the last value the user typed; empty when fine.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Set when changing this row changes which other rows exist (for example the symbology), so the panel rebuilds.</summary>
    public bool RebuildOnChange { get; init; }

    /// <summary>
    /// Decides whether the row is shown. Unlike a rebuild of the panel it keeps the controls (and their keyboard focus) alive, so a row can
    /// come and go while the user works in the one next to it (the printer-font width while the font changes to a Windows font).
    /// </summary>
    public Func<bool>? VisibleWhen { get; init; }

    /// <summary>False while <see cref="VisibleWhen"/> says the row does not apply.</summary>
    public bool IsVisible => VisibleWhen?.Invoke() ?? true;

    /// <summary>Tells the view to ask <see cref="IsVisible"/> again.</summary>
    public void RefreshVisibility() => OnPropertyChanged(nameof(IsVisible));

    private Func<RowDetail?>? _detailSource;

    /// <summary>
    /// Optional second line under the row's editor (for example the printed size of a QR module, or how full a QR code is). It is re-read
    /// on every refresh, so it follows the document without rebuilding the panel. Keeping it inside the row (instead of a separate note
    /// row) keeps the number of rows per barcode option equal to the catalogue.
    /// </summary>
    public Func<RowDetail?>? DetailSource
    {
        get => _detailSource;
        init { _detailSource = value; RefreshDetail(); }
    }

    /// <summary>The second line shown under the editor; empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = string.Empty;

    /// <summary>True when <see cref="Detail"/> is a warning (shown in the warning colour).</summary>
    [ObservableProperty]
    private bool _detailIsWarning;

    /// <summary>True when there is a second line to show.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>Re-reads <see cref="DetailSource"/>.</summary>
    protected void RefreshDetail()
    {
        var d = _detailSource?.Invoke();
        Detail = d?.Text ?? string.Empty;
        DetailIsWarning = d?.IsWarning ?? false;
    }

    /// <summary>Re-reads the value from the document (after undo, redo or an edit elsewhere).</summary>
    public abstract void Refresh();
}

/// <summary>Whole-number property with a valid range.</summary>
public sealed partial class IntRow : PropertyRow
{
    private readonly Func<int?> _get;
    private readonly Action<int?> _set;
    private readonly int _min, _max;
    private readonly bool _nullable;
    private readonly string? _rangeIssueKey;

    /// <summary>
    /// Creates a row. With <paramref name="nullable"/> an empty box means "inherit the label default". With
    /// <paramref name="rangeIssueKey"/> (a Core issue key whose arguments are the lowest and highest value) an out-of-range
    /// number is explained with that message, the same wording the validation list uses.
    /// </summary>
    public IntRow(string labelKey, Func<int?> get, Action<int?> set, int min, int max, bool nullable = false, string? hintKey = null, string? rangeIssueKey = null)
        : base(labelKey, hintKey)
    {
        _get = get; _set = set; _min = min; _max = max; _nullable = nullable; _rangeIssueKey = rangeIssueKey;
        _text = Format(get());
    }

    /// <summary>True to give the row a whole line instead of sharing it with the next number (used when it carries a longer second line).</summary>
    public bool FullWidth { get; init; }

    /// <summary>The text in the box.</summary>
    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value)
    {
        if (_applying) return;
        Commit(value);
    }

    private bool _applying;

    private void Commit(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            if (_nullable) { Error = string.Empty; _set(null); return; }
            Error = Loc.Get("Validation.Required");
            return;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int n))
        {
            Error = Loc.Get("Validation.NotInteger");
            return;
        }
        if (n < _min || n > _max)
        {
            Error = _rangeIssueKey is null
                ? Loc.Get("Validation.Range", _min, _max)
                : Loc.FormatIssue(new Rotula.Core.Validation.ValidationIssue(Rotula.Core.Validation.ValidationSeverity.Error, _rangeIssueKey, null,
                    _min.ToString(CultureInfo.InvariantCulture), _max.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        Error = string.Empty;
        if (_get() != n) _set(n);
    }

    private static string Format(int? v) => v?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <inheritdoc />
    public override void Refresh()
    {
        RefreshDetail();
        // Do not overwrite what the user is typing while it is invalid or equal in value.
        if (HasError) return;
        string current = Format(_get());
        if (current == Text) return;
        _applying = true;
        Text = current;
        _applying = false;
    }
}

/// <summary>Decimal property with a valid range.</summary>
public sealed partial class DoubleRow : PropertyRow
{
    private readonly Func<double> _get;
    private readonly Action<double> _set;
    private readonly double _min, _max;
    private bool _applying;

    /// <summary>Creates a row.</summary>
    public DoubleRow(string labelKey, Func<double> get, Action<double> set, double min, double max, string? hintKey = null)
        : base(labelKey, hintKey)
    {
        _get = get; _set = set; _min = min; _max = max;
        _text = Format(get());
    }

    /// <summary>The text in the box.</summary>
    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value)
    {
        if (_applying) return;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double n) || double.IsNaN(n) || double.IsInfinity(n))
        {
            Error = Loc.Get("Validation.NotNumber");
            return;
        }
        if (n < _min || n > _max)
        {
            Error = Loc.Get("Validation.Range", _min.ToString(CultureInfo.CurrentCulture), _max.ToString(CultureInfo.CurrentCulture));
            return;
        }
        Error = string.Empty;
        if (Math.Abs(_get() - n) > 1e-9) _set(n);
    }

    private static string Format(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    /// <inheritdoc />
    public override void Refresh()
    {
        if (HasError) return;
        double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double shown);
        if (Math.Abs(shown - _get()) < 1e-9 && Text.Length > 0) return;
        _applying = true;
        Text = Format(_get());
        _applying = false;
    }
}

/// <summary>
/// Free text (one or several lines). A row can also be the content of a text element or a barcode: with <see cref="Fields"/> set it
/// offers the field binding controls (a fixed value, one field shown as a chip, or text that mixes both).
/// </summary>
public sealed partial class TextRow : PropertyRow
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Action<string, string>? _setDiscrete;
    private readonly Func<string>? _warning;
    private readonly int _maxLength;
    private readonly Func<string, string?>? _validate;
    private bool _applying;
    private bool _discrete;
    private string _discreteKey = "Undo.InsertField";

    /// <summary>Creates a row. <paramref name="validate"/> returns a plain-language problem, or null when the text is fine.</summary>
    /// <param name="labelKey">Resource key of the label.</param>
    /// <param name="get">Reads the value.</param>
    /// <param name="set">Writes the value while the user types (steps merge into one undo step).</param>
    /// <param name="multiline">True for a tall, multi-line box.</param>
    /// <param name="maxLength">Longest text allowed, or 0.</param>
    /// <param name="hintKey">Resource key of the tool tip.</param>
    /// <param name="validate">Returns a problem text, or null.</param>
    /// <param name="fields">The available fields; when set the row is a field target (binding switch, Insert field, autocomplete).</param>
    /// <param name="setDiscrete">Writes a value as its own undo step, given the value and the resource key of the step (insert, bind and unbind use it).</param>
    /// <param name="warning">Returns the "no such column" warning for the row's element, or an empty string.</param>
    public TextRow(string labelKey, Func<string> get, Action<string> set, bool multiline = false, int maxLength = 0, string? hintKey = null,
        Func<string, string?>? validate = null, FieldsViewModel? fields = null, Action<string, string>? setDiscrete = null, Func<string>? warning = null)
        : base(labelKey, hintKey)
    {
        _get = get; _set = set; _maxLength = maxLength; _validate = validate;
        _setDiscrete = setDiscrete; _warning = warning;
        Fields = fields;
        IsMultiline = multiline;
        _text = get();
        _warningText = warning?.Invoke() ?? string.Empty;
    }

    /// <summary>True for a tall, multi-line box.</summary>
    public bool IsMultiline { get; }

    private Action? _action;

    /// <summary>Caption of an extra button under the box (for example "Content builder..."), or null when the row has none.</summary>
    public string? ActionCaption { get; private set; }

    /// <summary>True when the row has the extra button.</summary>
    public bool HasAction => ActionCaption is not null;

    /// <summary>Adds the extra button under the box and returns the row.</summary>
    public TextRow WithAction(string caption, Action action)
    {
        ActionCaption = caption;
        _action = action;
        return this;
    }

    /// <summary>Runs the extra button's action.</summary>
    [RelayCommand]
    private void RunAction() => _action?.Invoke();

    /// <summary>The available fields, or null when the row is plain text with no field controls.</summary>
    public FieldsViewModel? Fields { get; }

    /// <summary>True when the row offers the field controls.</summary>
    public bool IsFieldTarget => Fields is not null;

    /// <summary>The text.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>The "no column called X in the loaded data" warning, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warningText;

    /// <summary>True when there is a warning to show.</summary>
    public bool HasWarning => WarningText.Length > 0;

    /// <summary>The field the whole content is bound to (<c>Sku</c>), or null for fixed text or text that mixes fields.</summary>
    public string? BoundToken => FieldNaming.FindBinding(Text);

    /// <summary>The value of the bound field in the record shown on the canvas, for the chip; empty when unknown.</summary>
    public string BoundSample => BoundToken is { } t && Fields?.Find(t) is { HasRecord: true } f ? f.SampleText : string.Empty;

    /// <summary>Tool tip of the chip.</summary>
    public string BoundTip => BoundToken is not { } t ? string.Empty : BoundSample.Length > 0 ? Loc.Get("Fields.Sample.Label", BoundSample) : Loc.Get("Fields.Chip.Tip", t);

    /// <summary>True when the content is exactly one field.</summary>
    public bool IsBound => IsFieldTarget && BoundToken is not null;

    /// <summary>True when the content is not a single field (the text box is shown).</summary>
    public bool IsFixed => !IsBound;

    partial void OnTextChanged(string value)
    {
        NotifyBinding();
        OnPropertyChanged(nameof(IsBound));
        OnPropertyChanged(nameof(IsFixed));
        if (_applying) return;
        if (_maxLength > 0 && value.Length > _maxLength)
        {
            Error = Loc.Get("Validation.TooLong", _maxLength);
            return;
        }
        string? problem = _validate?.Invoke(value);
        if (problem is not null)
        {
            Error = problem;
            return;
        }
        Error = string.Empty;
        if (_get() == value) return;
        if (_discrete && _setDiscrete is not null) _setDiscrete(value, _discreteKey);
        else _set(value);
    }

    private void NotifyBinding()
    {
        OnPropertyChanged(nameof(BoundToken));
        OnPropertyChanged(nameof(BoundSample));
        OnPropertyChanged(nameof(BoundTip));
    }

    private void CommitDiscrete(string value, string undoKey)
    {
        // Why a flag instead of calling the setter directly: the text box, the validation and the properties all react to Text.
        _discrete = true;
        _discreteKey = undoKey;
        try { Text = value; }
        finally { _discrete = false; }
    }

    /// <summary>
    /// Replaces <paramref name="length"/> characters from <paramref name="start"/> with <paramref name="insert"/> as one undo step of its own
    /// (it never merges with typing). Returns the caret position after the inserted text.
    /// </summary>
    public int ReplaceRange(int start, int length, string insert)
    {
        string t = Text;
        start = Math.Clamp(start, 0, t.Length);
        length = Math.Clamp(length, 0, t.Length - start);
        CommitDiscrete(t[..start] + insert + t[(start + length)..], "Undo.InsertField");
        return start + insert.Length;
    }

    /// <summary>Inserts a field's <c>{{placeholder}}</c> at the caret, replacing the selection. Returns the caret position after it.</summary>
    public int InsertField(FieldItem field, int selectionStart, int selectionLength)
    {
        Fields?.EnsureMapping(field);
        return ReplaceRange(selectionStart, selectionLength, field.Placeholder);
    }

    /// <summary>Makes the whole content the field (a pure binding).</summary>
    public void BindTo(FieldItem field)
    {
        Fields?.EnsureMapping(field);
        CommitDiscrete(field.Placeholder, "Undo.InsertField");
    }

    /// <summary>Back to a fixed value: the field's current sample value, or its name when there is none.</summary>
    [RelayCommand]
    private void Unbind()
    {
        if (BoundToken is not { } token) return;
        string sample = Fields?.Find(token)?.Sample ?? string.Empty;
        CommitDiscrete(sample.Length > 0 ? sample : token.TrimStart('#'), "Undo.UnbindField");
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_warning is not null) WarningText = _warning();
        NotifyBinding(); // the sample under the chip follows the record shown on the canvas
        if (HasError) return;
        string current = _get();
        if (current == Text) return;
        _applying = true;
        Text = current;
        _applying = false;
    }
}

/// <summary>Yes/no property.</summary>
public sealed partial class BoolRow : PropertyRow
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private bool _applying;

    /// <summary>Creates a row.</summary>
    public BoolRow(string labelKey, Func<bool> get, Action<bool> set, string? hintKey = null) : base(labelKey, hintKey)
    {
        _get = get; _set = set;
        _value = get();
    }

    /// <summary>The state.</summary>
    [ObservableProperty]
    private bool _value;

    partial void OnValueChanged(bool value)
    {
        if (_applying) return;
        if (_get() != value) _set(value);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_get() == Value) return;
        _applying = true;
        Value = _get();
        _applying = false;
    }
}

/// <summary>One entry of a <see cref="ChoiceRow"/>.</summary>
/// <param name="Value">The underlying value (an enum member, or null for "inherit").</param>
/// <param name="Display">Translated name.</param>
/// <param name="Group">Translated name of the group the item is listed under, or null for a flat list.</param>
public sealed record ChoiceItem(object? Value, string Display, string? Group = null)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>Pick one of several options (enum values, or "inherit").</summary>
public partial class ChoiceRow : PropertyRow
{
    private readonly Func<object?> _get;
    private readonly Action<object?> _set;
    private bool _applying;

    /// <summary>Creates a row.</summary>
    public ChoiceRow(string labelKey, IReadOnlyList<ChoiceItem> items, Func<object?> get, Action<object?> set, string? hintKey = null)
        : base(labelKey, hintKey)
    {
        Items = items; _get = get; _set = set;
        _selected = Find(get());
        // The drop-down binds to a view so that items with a group (the barcode families) are listed under group headings.
        View = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
        if (items.Any(i => i.Group is not null))
            View.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ChoiceItem.Group)));
    }

    /// <summary>The items as the drop-down shows them (grouped when the items carry a group).</summary>
    public System.ComponentModel.ICollectionView View { get; }

    /// <summary>All options.</summary>
    public IReadOnlyList<ChoiceItem> Items { get; }

    /// <summary>The chosen option.</summary>
    [ObservableProperty]
    private ChoiceItem? _selected;

    private ChoiceItem? Find(object? v) => Items.FirstOrDefault(i => Equals(i.Value, v));

    partial void OnSelectedChanged(ChoiceItem? value)
    {
        if (_applying || value is null) return;
        if (!Equals(_get(), value.Value)) _set(value.Value);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        RefreshDetail();
        var item = Find(_get());
        if (ReferenceEquals(item, Selected)) return;
        _applying = true;
        Selected = item;
        _applying = false;
    }

    /// <summary>Builds the options for an enum, translated.</summary>
    public static List<ChoiceItem> ForEnum<T>(IEnumerable<T>? values = null) where T : struct, Enum =>
        (values ?? Enum.GetValues<T>()).Select(v => new ChoiceItem(v, Loc.EnumName(v))).ToList();
}

/// <summary>A button in the property panel (for example "Choose image").</summary>
public sealed partial class ActionRow : PropertyRow
{
    private readonly Action _action;

    /// <summary>Creates a row.</summary>
    public ActionRow(string labelKey, Action action) : base(labelKey) => _action = action;

    /// <summary>Runs the action.</summary>
    [RelayCommand]
    private void Run() => _action();

    /// <inheritdoc />
    public override void Refresh() { }
}

/// <summary>A heading that groups rows.</summary>
public sealed class HeadingRow : PropertyRow
{
    /// <summary>Creates a heading.</summary>
    public HeadingRow(string labelKey) : base(labelKey) { }

    /// <inheritdoc />
    public override void Refresh() { }
}

/// <summary>
/// A read-only line of explanatory text under the rows, for example what the preview of a barcode does differently. The text is
/// re-read on every refresh, so it follows the element's options without rebuilding the panel (which would take the focus away).
/// </summary>
public sealed partial class NoteRow : PropertyRow
{
    private readonly Func<string?> _get;

    /// <summary>Creates a note. <paramref name="get"/> returns the current text, or null/empty to hide the note.</summary>
    public NoteRow(string labelKey, Func<string?> get) : base(labelKey)
    {
        _get = get;
        _text = get() ?? string.Empty;
    }

    /// <summary>The note, empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>True when there is a note to show.</summary>
    public bool HasText => Text.Length > 0;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasText));

    /// <inheritdoc />
    public override void Refresh() => Text = _get() ?? string.Empty;
}

/// <summary>
/// The text formatting block (font, size, bold / italic / underline, alignment and the overflow warning). It has no value of its own: the
/// controls bind to the shared <see cref="TextFormatViewModel"/>, which refreshes itself on every selection and document change.
/// </summary>
public sealed class TextFormatRow : PropertyRow
{
    /// <summary>Creates the row around the shared formatting view-model.</summary>
    public TextFormatRow(TextFormatViewModel format) : base("Panel.Format") => Format = format;

    /// <summary>What the controls show and change.</summary>
    public TextFormatViewModel Format { get; }

    /// <inheritdoc />
    public override void Refresh() { }
}
