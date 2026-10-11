using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Core.Barcodes;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering.Barcodes;

namespace Rotula.App.ViewModels;

/// <summary>The settings of the QR code the builder is made for.</summary>
/// <param name="Level">Error correction level the symbol starts with (the dialog can change it).</param>
/// <param name="Model">QR model, 1 or 2.</param>
/// <param name="ModuleSize">Dots per module, for the printed-size line.</param>
/// <param name="Dpi">Printer resolution, for the printed-size line.</param>
public sealed record QrBuilderSettings(QrErrorCorrection Level, int Model, int ModuleSize, int Dpi);

/// <summary>One content type chip (Web address, Wi-Fi, Contact ...).</summary>
public sealed partial class QrKindChip : ObservableObject
{
    private readonly Action<QrKindChip>? _chosen;

    /// <summary>Creates a chip. <paramref name="chosen"/> is called when the chip becomes the selected one (a click, or an arrow key in the group).</summary>
    public QrKindChip(QrKindSchema schema, Action<QrKindChip>? chosen = null)
    {
        Kind = schema.Kind;
        Label = Loc.Get(schema.LabelKey);
        _chosen = chosen;
    }

    /// <summary>The content kind.</summary>
    public QrContentKind Kind { get; }

    /// <summary>Translated name.</summary>
    public string Label { get; }

    /// <summary>True for the chosen chip.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The label; WPF uses it as the accessible name of the chip.</summary>
    public override string ToString() => Label;

    // The radio button writes IsSelected itself, for a click and for the arrow keys alike, so the choice is made here and not in a click handler.
    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _chosen?.Invoke(this);
    }
}

/// <summary>
/// The QR content builder: pick a kind of content, fill in a form, and get the text the QR code carries. Every field can be typed or bound to
/// a data column. The dialog checks the content as it is typed (Core's validators), shows a live preview made by the same encoder as the label
/// preview, and says how full the code is. Apply stays disabled until the content is valid and fits.
/// </summary>
public sealed partial class QrContentBuilderViewModel : ObservableObject
{
    /// <summary>Most records measured when looking for the longest text the code has to carry.</summary>
    private const int MaxMeasuredRecords = 500;

    private readonly FieldsViewModel? _fields;
    private readonly QrBuilderSettings _settings;
    private readonly IReadOnlyList<LabelVariable> _variables;
    private readonly Dictionary<QrContentKind, Dictionary<string, string>> _values = new();
    private QrContentKind _kind;
    private bool _rebuilding;
    private int _fieldsVersion;
    private readonly string _caption;

    /// <summary>Creates the builder.</summary>
    /// <param name="fields">The shared list of available fields (null: no field controls).</param>
    /// <param name="initial">The definition to restore, or null to start fresh.</param>
    /// <param name="settings">Level, model, module size and resolution of the QR code.</param>
    /// <param name="currentData">The text the code has now, used to pick a starting kind when there is no definition (a web address becomes the Web address kind, other text the Text kind).</param>
    /// <param name="variables">The label's own variables, whose sample values stand in for bound fields when no data is loaded.</param>
    public QrContentBuilderViewModel(FieldsViewModel? fields, QrContentSpec? initial, QrBuilderSettings settings, string? currentData = null, IReadOnlyList<LabelVariable>? variables = null)
    {
        _fields = fields;
        _settings = settings;
        _variables = variables ?? Array.Empty<LabelVariable>();
        _caption = Loc.Get("QrContent.Title");
        Level = settings.Level;
        Kinds = QrContentSchema.All.Select(k => new QrKindChip(k, OnChipChosen)).ToList();
        LevelChoices = QrSizing.ErrorCorrectionLevels.Select(c => new ChoiceItem(Enum.Parse<QrErrorCorrection>(c.Value), Loc.Get(c.LabelKey))).ToList();
        _selectedLevel = LevelChoices.First(c => Equals(c.Value, Level));

        if (initial is not null)
        {
            _values[initial.Kind] = initial.Fields.Where(f => f.Value.Length > 0).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            _kind = initial.Kind;
            Restored = true;
        }
        else
        {
            _kind = QrContentKind.Url;
            string data = currentData?.Trim() ?? string.Empty;
            if (data.Length > 0)
            {
                bool web = data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                // Any other text becomes the Text kind, so nothing the user wrote is lost when the builder is opened.
                _kind = web ? QrContentKind.Url : QrContentKind.Text;
                _values[_kind] = new Dictionary<string, string>(StringComparer.Ordinal) { [web ? "url" : "text"] = currentData!.Trim() };
            }
        }
        if (_fields is not null) _fields.FieldsChanged += OnFieldsChanged;
        SelectKindCore(_kind);
    }

    /// <summary>Detaches from the shared field list; the dialog calls it when it closes.</summary>
    public void Detach()
    {
        if (_fields is not null) _fields.FieldsChanged -= OnFieldsChanged;
    }

    private void OnFieldsChanged(object? sender, EventArgs e)
    {
        _fieldsVersion++;
        Recompute();
    }

    // ----------------------------------------------------------------------------------- the form

    /// <summary>True when the builder was opened on an element that already had a definition, which was restored.</summary>
    public bool Restored { get; }

    /// <summary>Window title.</summary>
    public string Title => _caption;

    /// <summary>The content type chips, in the order of the schema.</summary>
    public IReadOnlyList<QrKindChip> Kinds { get; }

    /// <summary>The fields of the chosen kind.</summary>
    public ObservableCollection<QrFieldEditor> Editors { get; } = new();

    /// <summary>The chosen content kind.</summary>
    public QrContentKind Kind => _kind;

    /// <summary>Name of the chosen kind, for the form heading.</summary>
    public string KindName => Loc.Get(QrContentSchema.For(_kind).LabelKey);

    /// <summary>Chooses a content kind. The values typed for the other kinds are kept, so going back does not lose them.</summary>
    [RelayCommand]
    private void SelectKind(QrKindChip? chip)
    {
        if (chip is null || chip.Kind == _kind) return;
        SelectKindCore(chip.Kind);
    }

    private bool _syncingChips;

    private void OnChipChosen(QrKindChip chip)
    {
        if (!_syncingChips && chip.Kind != _kind) SelectKindCore(chip.Kind);
    }

    private void SelectKindCore(QrContentKind kind)
    {
        _kind = kind;
        _rebuilding = true;
        try
        {
            _syncingChips = true;
            foreach (var chip in Kinds) chip.IsSelected = chip.Kind == kind;
            _syncingChips = false;
            Editors.Clear();
            var bag = Values(kind);
            foreach (var f in QrContentSchema.For(kind).Fields)
            {
                string name = f.Name;
                var editor = new QrFieldEditor(f, _fields, () => bag.TryGetValue(name, out var v) ? v : string.Empty, v => SetValue(name, v));
                // A field that already has a value was filled by the user (or restored), so its problems are shown at once.
                editor.Touched = editor.Row.Text.Length > 0;
                Editors.Add(editor);
            }
        }
        finally { _rebuilding = false; _syncingChips = false; }
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindName));
        Recompute();
    }

    private Dictionary<string, string> Values(QrContentKind kind)
    {
        if (!_values.TryGetValue(kind, out var bag)) _values[kind] = bag = new Dictionary<string, string>(StringComparer.Ordinal);
        return bag;
    }

    private void SetValue(string name, string value)
    {
        var bag = Values(_kind);
        if (value.Length == 0) bag.Remove(name); else bag[name] = value;
        if (_rebuilding) return;
        if (Editors.FirstOrDefault(e => e.Name == name) is { } editor) editor.Touched = true;
        Recompute();
    }

    /// <summary>The definition as it stands now: the kind and the values that are set.</summary>
    public QrContentSpec CurrentSpec() => new(_kind, new Dictionary<string, string>(Values(_kind), StringComparer.Ordinal));

    // ----------------------------------------------------------------------------------- error correction

    /// <summary>The error correction levels with their recovery percentages.</summary>
    public IReadOnlyList<ChoiceItem> LevelChoices { get; }

    /// <summary>The level the symbol uses; it is applied to the element together with the content.</summary>
    public QrErrorCorrection Level { get; private set; }

    [ObservableProperty]
    private ChoiceItem? _selectedLevel;

    partial void OnSelectedLevelChanged(ChoiceItem? value)
    {
        if (value?.Value is not QrErrorCorrection level) return;
        Level = level;
        OnPropertyChanged(nameof(Level));
        Recompute();
    }

    // ----------------------------------------------------------------------------------- result of the check

    /// <summary>The text the code carries, with <c>{{field}}</c> placeholders for bound fields (what goes into the element's Data).</summary>
    [ObservableProperty]
    private string _generatedText = string.Empty;

    /// <summary>"123 characters" or similar, for the read-only text box.</summary>
    [ObservableProperty]
    private string _characterCount = string.Empty;

    /// <summary>The text used for the preview, with sample values for bound fields; null while the content is incomplete or invalid.</summary>
    [ObservableProperty]
    private string? _previewText;

    /// <summary>The preview as a grid of modules, or null while there is nothing to draw.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowPreviewHint))]
    private BarcodeMatrix? _previewMatrix;

    /// <summary>True when there is a code to draw.</summary>
    public bool HasPreview => PreviewMatrix is not null;

    /// <summary>True when the placeholder text should stand where the code would be.</summary>
    public bool ShowPreviewHint => PreviewMatrix is null;

    /// <summary>Why there is no preview yet.</summary>
    [ObservableProperty]
    private string _previewHint = string.Empty;

    /// <summary>A note under the preview when it shows sample values for bound fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewNote))]
    private string _previewNote = string.Empty;

    /// <summary>True when <see cref="PreviewNote"/> is set.</summary>
    public bool HasPreviewNote => PreviewNote.Length > 0;

    /// <summary>The capacity meter: how full the code is.</summary>
    [ObservableProperty]
    private QrCapacityReport _capacity = new(CapacityState.Empty, string.Empty, string.Empty, null);

    /// <summary>The headline of the capacity meter.</summary>
    public string CapacityText => Capacity.Text;

    /// <summary>What to do about the capacity (lower the level, shorten the text, a suggested level); may be empty.</summary>
    public string CapacityHint => Capacity.Hint;

    /// <summary>True when there is a capacity hint.</summary>
    public bool HasCapacityHint => Capacity.Hint.Length > 0;

    /// <summary>The fill of the meter, 0 to 100.</summary>
    public double CapacityPercent => Math.Min(100, Capacity.Percent);

    /// <summary>True when the meter should be drawn in the warning colour.</summary>
    public bool CapacityIsWarning => Capacity.State is CapacityState.Dense or CapacityState.Overflow;

    /// <summary>True when the meter should be drawn in the error colour (the content does not fit).</summary>
    public bool CapacityIsError => Capacity.State == CapacityState.Overflow;

    /// <summary>The printed size line (symbol size in mm and inches at the label's resolution), with the too-small warning when needed.</summary>
    [ObservableProperty]
    private string _sizeText = string.Empty;

    /// <summary>True when the module is too small to scan.</summary>
    [ObservableProperty]
    private bool _sizeIsWarning;

    /// <summary>Problems and warnings with the content, in plain language.</summary>
    public ObservableCollection<BuilderMessage> Messages { get; } = new();

    /// <summary>True when there are messages.</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>The neutral line shown while Apply is disabled only because required fields are still empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusHint))]
    private string _statusHint = string.Empty;

    /// <summary>True when <see cref="StatusHint"/> is set.</summary>
    public bool HasStatusHint => StatusHint.Length > 0;

    /// <summary>True when the content is valid and fits, so it can be applied.</summary>
    [ObservableProperty]
    private bool _canApply;

    partial void OnCanApplyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    /// <summary>True when a Wi-Fi password field exists in the current form (the dialog shows the warning about it next to the field).</summary>
    public bool HasPasswordField => Editors.Any(e => e.IsPassword);

    // ----------------------------------------------------------------------------------- recompute

    /// <summary>Checks everything again after a change: the text, the problems, the preview and the capacity.</summary>
    private void Recompute()
    {
        if (_rebuilding) return;
        var spec = CurrentSpec();
        GeneratedText = QrContentBuilder.BuildTemplate(spec);
        CharacterCount = Loc.Get("QrContent.Characters", GeneratedText.Length);

        // 1. The design: every field that is not a placeholder is checked as it is, a placeholder is checked with the field's example.
        var design = QrContentBuilder.Validate(spec, Level, _settings.Model);
        var issues = design.Where(i => !IsCapacity(i)).ToList();
        bool anyValue = spec.Fields.Count > 0;

        // 2. The preview: bound fields take the value of the record shown on the canvas, or the label variable's sample, or the field's example.
        string? text = ResolvePreview(spec, out bool usedSamples, out var recordIssues);
        foreach (var i in recordIssues) if (!issues.Any(x => x.Field == i.Field && x.Issue.MessageKey == i.Issue.MessageKey)) issues.Add(i);

        // 3. Capacity: the longest text over the loaded records when there are any, otherwise the preview text.
        string? payload = text is null ? null : LongestOverRecords(spec) ?? text;
        Capacity = QrCapacityInfo.Describe(payload, Level, _settings.Model);
        OnPropertyChanged(nameof(CapacityText));
        OnPropertyChanged(nameof(CapacityHint));
        OnPropertyChanged(nameof(HasCapacityHint));
        OnPropertyChanged(nameof(CapacityPercent));
        OnPropertyChanged(nameof(CapacityIsWarning));
        OnPropertyChanged(nameof(CapacityIsError));
        PreviewText = text;

        // 4. Printed size.
        int version = Capacity.Fit is { Fits: true, Version: > 0 } fit ? fit.Version : 0;
        var size = QrCapacityInfo.ModuleSize(version, _settings.ModuleSize, _settings.Dpi);
        SizeText = size.Text;
        SizeIsWarning = size.IsWarning;

        // 5. Preview image.
        PreviewMatrix = null;
        if (text is not null && text.Length > 0)
        {
            var matrix = BarcodeEncoder.TryEncode(BarcodeSymbology.QrCode, text, new BarcodeEncodeOptions { QrErrorCorrection = Level }, out _);
            PreviewMatrix = matrix;
        }
        PreviewHint = Loc.Get(anyValue ? "QrContent.Preview.Fix" : "QrContent.Preview.Waiting");
        PreviewNote = spec.Placeholders().Count > 0 && usedSamples ? Loc.Get("QrContent.Preview.Bound") : string.Empty;

        // 6. Show the problems next to their fields and in the list.
        ShowIssues(issues);
        bool errors = issues.Any(i => i.Issue.Severity == ValidationSeverity.Error) || (anyValue && text is null);
        CanApply = anyValue && !errors && text is not null && Capacity.Fits;
        OnPropertyChanged(nameof(HasPasswordField));
    }

    private static bool IsCapacity(QrFieldIssue i) => i.Issue.MessageKey.StartsWith("QrContent.Capacity.", StringComparison.Ordinal);

    private void ShowIssues(List<QrFieldIssue> issues)
    {
        Messages.Clear();
        foreach (var editor in Editors) { editor.Error = string.Empty; editor.Warning = string.Empty; }
        bool anyError = false;
        foreach (var item in issues)
        {
            var editor = item.Field is null ? null : Editors.FirstOrDefault(e => e.Name == item.Field);
            bool isError = item.Issue.Severity == ValidationSeverity.Error;
            bool requiredEmpty = item.Issue.MessageKey == "QrContent.Field.Required";
            // "Required" is only said about a field the user has already touched; before that the form just waits.
            if (requiredEmpty && editor is { Touched: false })
            {
                anyError = true;
                continue;
            }
            string message = Loc.FormatIssue(item.Issue);
            if (editor is not null)
            {
                if (isError && !editor.HasError) editor.Error = message;
                else if (!isError && !editor.HasWarning) editor.Warning = message;
            }
            string line = editor is null ? message : editor.Label + ": " + message;
            Messages.Add(new BuilderMessage(line, isError));
            if (isError) anyError = true;
        }
        OnPropertyChanged(nameof(HasMessages));
        StatusHint = anyError && Messages.All(m => !m.IsError) ? Loc.Get("QrContent.Waiting") : string.Empty;
    }

    /// <summary>
    /// Builds the preview text. When every placeholder has a value (from the record shown on the canvas, or a label variable's sample) the content is
    /// resolved with them, which is what the printed code will carry for that record; otherwise the field's example stands in for the missing ones.
    /// </summary>
    private string? ResolvePreview(QrContentSpec spec, out bool usedSamples, out List<QrFieldIssue> recordIssues)
    {
        recordIssues = new List<QrFieldIssue>();
        usedSamples = false;
        if (spec.Fields.Count == 0) return null;
        var placeholders = spec.Placeholders();
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in placeholders)
        {
            var field = _fields?.Find(name);
            if (field is { HasRecord: true }) record[name] = field.Sample;
            else if (_variables.FirstOrDefault(v => v.Name == name) is { } v) record[name] = v.SampleValue;
        }
        QrContentResult result;
        if (placeholders.All(record.ContainsKey))
        {
            result = QrContentBuilder.Resolve(spec, record, Level, _settings.Model);
            usedSamples = placeholders.Count > 0;
            if (!result.HasErrors || placeholders.Count == 0)
            {
                // What is wrong with the shown record is a remark about the data, not about the design, so it does not block Apply.
                recordIssues = result.Issues.Where(i => !IsCapacity(i) && i.Issue.MessageKey != "Variable.Missing")
                    .Select(i => i.Issue.Severity == ValidationSeverity.Error ? new QrFieldIssue(i.Field, new ValidationIssue(ValidationSeverity.Warning, i.Issue.MessageKey, null, i.Issue.Args.ToArray())) : i)
                    .ToList();
                return result.Text;
            }
        }
        // Samples of the schema stand in for what is not known (this is what Validate checks too).
        var schema = QrContentSchema.For(spec.Kind);
        var sampled = spec.Fields.ToDictionary(f => f.Key, f => f.Value.Contains("{{", StringComparison.Ordinal) ? Substitute(f.Value, record, schema.Field(f.Key)?.Sample ?? "x") : f.Value, StringComparer.Ordinal);
        usedSamples = placeholders.Count > 0;
        result = QrContentBuilder.Resolve(new QrContentSpec(spec.Kind, sampled), new Dictionary<string, string>(), Level, _settings.Model);
        return result.Text;
    }

    /// <summary>Replaces placeholders of a value by the record's value, or by <paramref name="fallback"/> when the record has none.</summary>
    private static string Substitute(string value, Dictionary<string, string> record, string fallback)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"\{\{\s*(#?[A-Za-z_][A-Za-z0-9_]*)\s*\}\}");
        // A value that is only a placeholder gets the example as a whole; mixed text keeps its fixed part and gets the example in place of the field.
        return rx.Replace(value, m => record.TryGetValue(m.Groups[1].Value, out var v) ? v : fallback);
    }

    /// <summary>The longest text the code will carry over the loaded records, or null when no data is loaded.</summary>
    private string? LongestOverRecords(QrContentSpec spec)
    {
        if (_fields is null || !_fields.HasData) return null;
        var texts = new List<string>();
        foreach (var record in _fields.SampleRecords(MaxMeasuredRecords))
        {
            var result = QrContentBuilder.Resolve(spec, record, Level, _settings.Model);
            if (result.Text is { Length: > 0 } t) texts.Add(t);
        }
        return texts.Count == 0 ? null : QrSizing.Longest(texts);
    }

    // ----------------------------------------------------------------------------------- result

    /// <summary>True after Apply: the result below is valid.</summary>
    public bool Applied { get; private set; }

    /// <summary>The text for the element's Data (with placeholders). Valid after Apply.</summary>
    public string Template { get; private set; } = string.Empty;

    /// <summary>The definition as JSON for the element's QrContentSpec. Valid after Apply.</summary>
    public string SpecJson { get; private set; } = string.Empty;

    /// <summary>Raised when the dialog should close; the argument is true for Apply.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Stores the result and asks the dialog to close. Does nothing while the content is not valid.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply) return;
        var spec = CurrentSpec();
        Template = QrContentBuilder.BuildTemplate(spec);
        SpecJson = spec.ToJson();
        Applied = true;
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>Closes without applying.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
