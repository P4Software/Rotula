using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.Files;
using Rotula.App.Localization;
using Rotula.Core.Merge;

namespace Rotula.App.ViewModels;

/// <summary>A column offered in a mapping drop-down. A null value means "automatic" (same name as the placeholder).</summary>
public sealed class ColumnChoice : ObservableObject
{
    /// <summary>Creates a choice.</summary>
    public ColumnChoice(string? column) => Column = column;

    /// <summary>The column name, or null for "automatic".</summary>
    public string? Column { get; }

    /// <summary>Display text.</summary>
    public string Name => Column ?? Loc.Get("Data.Mapping.Auto");

    /// <summary>Re-reads <see cref="Name"/> after a language change.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Name));
}

/// <summary>One placeholder of the label and the data column that feeds it.</summary>
public sealed partial class MappingRow : ObservableObject
{
    private readonly Action<MappingRow> _changed;

    /// <summary>Creates a row.</summary>
    public MappingRow(string placeholder, IReadOnlyList<ColumnChoice> choices, ColumnChoice selected, bool isCounter, Action<MappingRow> changed)
    {
        Placeholder = placeholder;
        Choices = choices;
        _selected = selected;
        IsCounter = isCounter;
        _changed = changed;
    }

    /// <summary>Placeholder name as used in the label (without braces).</summary>
    public string Placeholder { get; }

    /// <summary>Text shown for the placeholder, for example {{name}}.</summary>
    public string Display => "{{" + Placeholder + "}}";

    /// <summary>The columns to pick from (automatic first).</summary>
    public IReadOnlyList<ColumnChoice> Choices { get; }

    /// <summary>True for counter placeholders ({{#Name}}), which are filled by the counter and cannot be mapped.</summary>
    public bool IsCounter { get; }

    /// <summary>True when the user can pick a column.</summary>
    public bool CanChoose => !IsCounter;

    /// <summary>The picked column.</summary>
    [ObservableProperty]
    private ColumnChoice _selected;

    partial void OnSelectedChanged(ColumnChoice value) => _changed(this);

    /// <summary>What the placeholder is fed from right now, or why it is not.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True when the placeholder has no data (shown as a warning).</summary>
    [ObservableProperty]
    private bool _isUnmapped;

    /// <summary>Updates the status text without raising a mapping change.</summary>
    public void SetStatus(string text, bool unmapped)
    {
        Status = text;
        IsUnmapped = unmapped;
    }
}

/// <summary>A problem in one record, ready to show in a list.</summary>
/// <param name="RecordNumber">1-based record number (the header row is not counted).</param>
/// <param name="IsError">True for errors (the record is not printed), false for warnings.</param>
/// <param name="Message">Plain-language message.</param>
public sealed record RecordIssueRow(int RecordNumber, bool IsError, string Message)
{
    /// <summary>Severity word for the list.</summary>
    public string SeverityText => Loc.Get(IsError ? "Data.Issue.Error" : "Data.Issue.Warning");

    /// <summary>Record column text.</summary>
    public string RecordText => RecordNumber.ToString(CultureInfo.CurrentCulture);
}

/// <summary>Editable form of one serial counter, with validation that explains what to change.</summary>
public sealed partial class CounterEditor : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates an editor from saved settings.</summary>
    public CounterEditor(CounterSettings settings, Action changed)
    {
        _changed = changed;
        _name = settings.Name;
        _isAlphanumeric = settings.Kind == CounterKind.Alphanumeric;
        _startText = settings.Start;
        _stepText = settings.Step.ToString(CultureInfo.InvariantCulture);
        _paddingText = settings.Padding.ToString(CultureInfo.InvariantCulture);
        _prefix = settings.Prefix;
        _suffix = settings.Suffix;
        _stopOnOverflow = settings.Overflow == CounterOverflow.Error;
        Validate();
    }

    /// <summary>Counter name; the label uses {{#Name}}.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private string _name;

    /// <summary>True for letters-and-digits counting, false for plain numbers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNumeric))]
    private bool _isAlphanumeric;

    /// <summary>True for plain numbers (binding helper for the radio buttons).</summary>
    public bool IsNumeric
    {
        get => !IsAlphanumeric;
        // Ignore the "false" a radio group writes into the option that was just unchecked (see PrinterSettingsViewModel.UseWindowsPrinter).
        set { if (value) IsAlphanumeric = false; }
    }

    /// <summary>First value.</summary>
    [ObservableProperty]
    private string _startText;

    /// <summary>Increment per record.</summary>
    [ObservableProperty]
    private string _stepText;

    /// <summary>Minimum digits, zero-padded (numeric counters).</summary>
    [ObservableProperty]
    private string _paddingText;

    /// <summary>Text before the number.</summary>
    [ObservableProperty]
    private string _prefix;

    /// <summary>Text after the number.</summary>
    [ObservableProperty]
    private string _suffix;

    /// <summary>True to report records as errors when the counter runs out; false to wrap around.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WrapOnOverflow))]
    private bool _stopOnOverflow;

    /// <summary>True to wrap around when the counter runs out (binding helper for the radio buttons).</summary>
    public bool WrapOnOverflow
    {
        get => !StopOnOverflow;
        // Ignore the "false" a radio group writes into the option that was just unchecked (see PrinterSettingsViewModel.UseWindowsPrinter).
        set { if (value) StopOnOverflow = false; }
    }

    /// <summary>The placeholder to type in the label.</summary>
    public string Placeholder => "{{#" + Name + "}}";

    /// <summary>What is wrong with the settings, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>True when the settings are not usable.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>The first three values, so the effect of the settings is visible.</summary>
    [ObservableProperty]
    private string _example = string.Empty;

    /// <summary>List text, for example "Serial".</summary>
    public string Display => Name.Length == 0 ? Loc.Get("Data.Counter.Unnamed") : Name;

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(Display)); Edited(); }
    partial void OnIsAlphanumericChanged(bool value) => Edited();
    partial void OnStartTextChanged(string value) => Edited();
    partial void OnStepTextChanged(string value) => Edited();
    partial void OnPaddingTextChanged(string value) => Edited();
    partial void OnPrefixChanged(string value) => Edited();
    partial void OnSuffixChanged(string value) => Edited();
    partial void OnStopOnOverflowChanged(bool value) => Edited();

    private void Edited()
    {
        Validate();
        _changed();
    }

    /// <summary>Builds the Core counter, or reports what is wrong in <see cref="Error"/>.</summary>
    public SerialCounter? TryBuild()
    {
        if (!int.TryParse(StepText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int step))
        {
            Error = Loc.Get("Data.Counter.StepInvalid");
            return null;
        }
        if (!int.TryParse(PaddingText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int padding))
        {
            Error = Loc.Get("Data.Counter.PaddingInvalid");
            return null;
        }
        var counter = new SerialCounter
        {
            Name = Name.Trim(), Kind = IsAlphanumeric ? CounterKind.Alphanumeric : CounterKind.Numeric, Start = StartText.Trim(), Step = step,
            Padding = padding, Prefix = Prefix, Suffix = Suffix, Overflow = StopOnOverflow ? CounterOverflow.Error : CounterOverflow.Wrap,
        };
        if (counter.Validate() is { } key)
        {
            Error = Loc.Get("Data.Counter.Invalid", Loc.Get("Issue." + key));
            return null;
        }
        return counter;
    }

    private void Validate()
    {
        var counter = TryBuild();
        if (counter is null) { Example = string.Empty; return; }
        Error = string.Empty;
        var values = new List<string>();
        for (int i = 0; i < 3; i++)
            if (counter.TryGetValue(i, out var v)) values.Add(v);
        Example = string.Join(", ", values) + (values.Count > 0 ? ", ..." : string.Empty);
    }

    /// <summary>The settings to store in the .rotula file.</summary>
    public CounterSettings ToSettings()
    {
        _ = int.TryParse(StepText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int step);
        _ = int.TryParse(PaddingText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int padding);
        return new CounterSettings
        {
            Name = Name, Kind = IsAlphanumeric ? CounterKind.Alphanumeric : CounterKind.Numeric, Start = StartText, Step = step == 0 && StepText.Trim() != "0" ? 1 : step,
            Padding = padding, Prefix = Prefix, Suffix = Suffix, Overflow = StopOnOverflow ? CounterOverflow.Error : CounterOverflow.Wrap,
        };
    }

    /// <summary>Re-reads texts that depend on the language.</summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Display));
        Validate();
    }
}
