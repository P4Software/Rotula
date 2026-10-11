using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Core.Barcodes.QrContent;

namespace Rotula.App.ViewModels;

/// <summary>A line of feedback in a builder dialog: a problem or a note, with its severity.</summary>
/// <param name="Text">The plain-language message.</param>
/// <param name="IsError">True for an error (blocks Apply), false for a warning.</param>
public sealed record BuilderMessage(string Text, bool IsError)
{
    /// <summary>The message; WPF uses it as the accessible name of the list row.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// One text field of a builder dialog (a QR content field, or the value of a GS1 application identifier). The text lives in a real
/// <see cref="TextRow"/>, so the shared field controls work on it unchanged: the Insert field flyout, the <c>{{</c> autocomplete and the
/// binding to a data column all call the row.
/// </summary>
public partial class BuilderField : ObservableObject
{
    /// <summary>Creates a field.</summary>
    /// <param name="name">Stable name (the key of the value in the definition).</param>
    /// <param name="label">Translated label.</param>
    /// <param name="row">The row that holds the text and offers the field controls.</param>
    /// <param name="required">True when the content cannot be built without a value.</param>
    public BuilderField(string name, string label, TextRow row, bool required = false)
    {
        Name = name;
        Label = label;
        Row = row;
        IsRequired = required;
    }

    /// <summary>Stable name of the field.</summary>
    public string Name { get; }

    /// <summary>Translated label, also the automation name of the editor.</summary>
    public string Label { get; }

    /// <summary>The label with a star when the field is required.</summary>
    public string LabelText => IsRequired ? Label + " *" : Label;

    /// <summary>True when a value is required.</summary>
    public bool IsRequired { get; }

    /// <summary>The row that holds the text.</summary>
    public TextRow Row { get; }

    /// <summary>True for a tall, multi-line box.</summary>
    public bool IsMultiline => Row.IsMultiline;

    /// <summary>A hint under the box: what kind of value this is, with an example.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string _hint = string.Empty;

    /// <summary>True when there is a hint.</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>A permanent note under the box (for example the warning that a Wi-Fi password is printed in the code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = string.Empty;

    /// <summary>True when there is a note.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>The problem with the current value, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>A softer remark about the current value (for example a time without a time zone), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = string.Empty;

    /// <summary>True when <see cref="Warning"/> is set.</summary>
    public bool HasWarning => Warning.Length > 0;

    /// <summary>The label; WPF uses it as the accessible name of the item.</summary>
    public override string ToString() => Label;

    /// <summary>True once the user has changed the value; "required" is only reported after that, so a fresh form is not covered in errors.</summary>
    public bool Touched { get; internal set; }
}

/// <summary>
/// The editor of one field of a QR content kind. Which control it needs follows the schema: a text box (with the example and the hint of the
/// kind of value), a drop-down for a choice, a check box for a yes/no, and a date and time picker for a calendar date. Every field can also be
/// bound to a data column; a bound choice or flag shows the placeholder instead of its control.
/// </summary>
public sealed partial class QrFieldEditor : BuilderField
{
    private bool _syncing;
    private string? _offset;

    /// <summary>Creates the editor.</summary>
    /// <param name="schema">The field's definition from <see cref="QrContentSchema"/>.</param>
    /// <param name="fields">The shared list of available fields; null disables the field controls.</param>
    /// <param name="get">Reads the stored value.</param>
    /// <param name="set">Stores a value.</param>
    public QrFieldEditor(QrFieldSchema schema, FieldsViewModel? fields, Func<string> get, Action<string> set)
        : base(schema.Name, Loc.Get(schema.LabelKey),
            new TextRow(schema.LabelKey, get, set, multiline: schema.Hint == QrInputHint.MultilineText, fields: fields, setDiscrete: (v, _) => set(v)),
            schema.Required)
    {
        Schema = schema;
        Hint = HintFor(schema);
        if (schema.Name == "password") Note = Loc.Get("QrContent.Password.Warning");
        if (schema.Hint == QrInputHint.Choice)
        {
            Choices = schema.Choices.Select(c => new ChoiceItem(c, Loc.Get("QrContent.Choice." + c))).ToList();
            _selectedChoice = Choices[0];
        }
        Row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TextRow.Text)) SyncFromText(); };
        SyncFromText();
    }

    /// <summary>The schema entry the editor was made from.</summary>
    public QrFieldSchema Schema { get; }

    /// <summary>True for fields edited in a plain text box (text, multi-line, phone, email, address, number, digits).</summary>
    public bool IsText => Schema.Hint is not (QrInputHint.Choice or QrInputHint.Flag or QrInputHint.DateTime);

    /// <summary>True for a drop-down of fixed values.</summary>
    public bool IsChoice => Schema.Hint == QrInputHint.Choice;

    /// <summary>True for a check box.</summary>
    public bool IsFlag => Schema.Hint == QrInputHint.Flag;

    /// <summary>True for a date and time.</summary>
    public bool IsDateTime => Schema.Hint == QrInputHint.DateTime;

    /// <summary>True when the field is the Wi-Fi password, whose value is shown in clear text on purpose and is printed in the code.</summary>
    public bool IsPassword => Schema.Name == "password";

    /// <summary>The values of a choice field, translated.</summary>
    public IReadOnlyList<ChoiceItem> Choices { get; } = Array.Empty<ChoiceItem>();

    /// <summary>True when the stored value is a placeholder or mixes placeholders in (a bound field); the editor then shows the text instead of its control.</summary>
    public bool IsBound => Row.Text.Contains("{{", StringComparison.Ordinal);

    /// <summary>True when the drop-down / check box / pickers are shown (the field is not bound).</summary>
    public bool IsFixed => !IsBound;

    /// <summary>True when a bound choice, flag or date offers the way back to a fixed value.</summary>
    public bool ShowUseFixed => IsBound && !IsText;

    /// <summary>True when the Insert field button replaces the whole value (a value that cannot hold mixed text: a choice, a flag, a date).</summary>
    public bool ReplacesWhole => (IsChoice || IsFlag || IsDateTime) && IsFixed;

    /// <summary>The chosen value of a choice field.</summary>
    [ObservableProperty]
    private ChoiceItem? _selectedChoice;

    partial void OnSelectedChoiceChanged(ChoiceItem? value)
    {
        if (_syncing || value is null) return;
        Touched = true;
        Row.Text = (string)value.Value!;
    }

    /// <summary>The state of a flag field.</summary>
    [ObservableProperty]
    private bool _flag;

    partial void OnFlagChanged(bool value)
    {
        if (_syncing) return;
        Touched = true;
        Row.Text = value ? "true" : string.Empty;
    }

    /// <summary>The date of a date field.</summary>
    [ObservableProperty]
    private DateTime? _date;

    partial void OnDateChanged(DateTime? value) => WriteDateTime();

    /// <summary>The time of day of a date field, as hours and minutes; empty for a date without a time (an all-day event).</summary>
    [ObservableProperty]
    private string _timeText = string.Empty;

    partial void OnTimeTextChanged(string value) => WriteDateTime();

    /// <summary>True when the time text cannot be read.</summary>
    [ObservableProperty]
    private bool _timeInvalid;

    private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" };

    private static string HintFor(QrFieldSchema schema)
    {
        if (schema.Hint is QrInputHint.Choice or QrInputHint.Flag) return string.Empty;
        string key = schema.Hint switch
        {
            QrInputHint.Phone => "QrContent.Hint.Phone",
            QrInputHint.Email => "QrContent.Hint.Email",
            QrInputHint.Url => "QrContent.Hint.Url",
            QrInputHint.Number => "QrContent.Hint.Number",
            QrInputHint.Digits => "QrContent.Hint.Digits",
            QrInputHint.DateTime => "QrContent.Hint.DateTime",
            QrInputHint.MultilineText => "QrContent.Hint.Multiline",
            _ => "QrContent.Hint.Text",
        };
        return Loc.Get(key, schema.Sample);
    }

    /// <summary>Brings the choice, the flag and the pickers in line with the text (it changed through the box, the flyout or an undo).</summary>
    private void SyncFromText()
    {
        _syncing = true;
        try
        {
            string text = Row.Text;
            if (IsChoice) SelectedChoice = Choices.FirstOrDefault(c => (string)c.Value! == text) ?? (text.Length == 0 ? Choices[0] : SelectedChoice);
            if (IsFlag) Flag = text.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "y" or "si";
            if (IsDateTime) ReadDateTime(text);
        }
        finally { _syncing = false; }
        OnPropertyChanged(nameof(IsBound));
        OnPropertyChanged(nameof(IsFixed));
        OnPropertyChanged(nameof(ReplacesWhole));
        OnPropertyChanged(nameof(ShowUseFixed));
    }

    private void ReadDateTime(string text)
    {
        if (text.Contains("{{", StringComparison.Ordinal) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            Date = null;
            TimeText = string.Empty;
            TimeInvalid = false;
            return;
        }
        Date = when.DateTime.Date;
        bool hasTime = text.Contains('T', StringComparison.OrdinalIgnoreCase) || text.Contains(':', StringComparison.Ordinal);
        TimeText = hasTime ? when.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        // Keep the zone the value came with, so reading and writing it again does not move the time.
        _offset = hasTime && System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d{2}:?\d{2})\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? when.ToString("zzz", CultureInfo.InvariantCulture) : null;
        TimeInvalid = false;
    }

    /// <summary>Writes the picked date and time into the text as ISO 8601 with the time zone (the form the calendar format needs).</summary>
    private void WriteDateTime()
    {
        if (_syncing || !IsDateTime || Date is not { } date) return;
        Touched = true;
        string time = TimeText.Trim();
        string text;
        if (time.Length == 0)
        {
            TimeInvalid = false;
            text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else if (DateTime.TryParseExact(time, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            TimeInvalid = false;
            var local = date.Date + t.TimeOfDay;
            // A picked date has no zone of its own: use the zone it had, or this computer's zone for that moment (summer time included).
            string offset = _offset ?? new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToString("zzz", CultureInfo.InvariantCulture);
            text = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + offset;
        }
        else
        {
            TimeInvalid = true;
            return;
        }
        if (Row.Text != text) Row.Text = text;
    }

    /// <summary>Back from a bound field to a fixed value: the first choice, unchecked, or empty.</summary>
    [RelayCommand]
    private void UseFixedValue()
    {
        Touched = true;
        Row.Text = IsChoice ? Choices[0].Value as string ?? string.Empty : string.Empty;
    }
}
