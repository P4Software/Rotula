using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;

namespace Rotula.App.ViewModels;

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

    /// <summary>Creates a row. With <paramref name="nullable"/> an empty box means "inherit the label default".</summary>
    public IntRow(string labelKey, Func<int?> get, Action<int?> set, int min, int max, bool nullable = false, string? hintKey = null)
        : base(labelKey, hintKey)
    {
        _get = get; _set = set; _min = min; _max = max; _nullable = nullable;
        _text = Format(get());
    }

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
            Error = Loc.Get("Validation.Range", _min, _max);
            return;
        }
        Error = string.Empty;
        if (_get() != n) _set(n);
    }

    private static string Format(int? v) => v?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <inheritdoc />
    public override void Refresh()
    {
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

/// <summary>Free text (one or several lines).</summary>
public sealed partial class TextRow : PropertyRow
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly int _maxLength;
    private readonly Func<string, string?>? _validate;
    private bool _applying;

    /// <summary>Creates a row. <paramref name="validate"/> returns a plain-language problem, or null when the text is fine.</summary>
    public TextRow(string labelKey, Func<string> get, Action<string> set, bool multiline = false, int maxLength = 0, string? hintKey = null,
        Func<string, string?>? validate = null)
        : base(labelKey, hintKey)
    {
        _get = get; _set = set; _maxLength = maxLength; _validate = validate;
        IsMultiline = multiline;
        _text = get();
    }

    /// <summary>True for a tall, multi-line box.</summary>
    public bool IsMultiline { get; }

    /// <summary>The text.</summary>
    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value)
    {
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
        if (_get() != value) _set(value);
    }

    /// <inheritdoc />
    public override void Refresh()
    {
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
public sealed record ChoiceItem(object? Value, string Display)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>Pick one of several options (enum values, or "inherit").</summary>
public sealed partial class ChoiceRow : PropertyRow
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
    }

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
