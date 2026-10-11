using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.Core.Barcodes;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering.Barcodes;

namespace Rotula.App.ViewModels;

/// <summary>An application identifier offered by the picker.</summary>
public sealed class Gs1AiItem
{
    /// <summary>Creates the entry for a dictionary definition.</summary>
    public Gs1AiItem(Gs1AiDefinition definition)
    {
        Definition = definition;
        Ai = definition.Ai;
        Title = definition.Title;
        Format = definition.FormatText;
    }

    /// <summary>The dictionary entry.</summary>
    public Gs1AiDefinition Definition { get; }

    /// <summary>The identifier digits.</summary>
    public string Ai { get; }

    /// <summary>The name of the data (from the GS1 dictionary, in English).</summary>
    public string Title { get; }

    /// <summary>The value format, for example N14 or X..20.</summary>
    public string Format { get; }

    /// <summary>Text for the list and the screen reader: identifier, name and format.</summary>
    public string Display => $"({Ai}) {Title}  ·  {Format}";

    /// <summary>The display text; WPF uses it as the accessible name of the list row.</summary>
    public override string ToString() => Display;
}

/// <summary>A ready-made set of identifiers for a common use.</summary>
/// <param name="Key">Stable name.</param>
/// <param name="Name">Translated name.</param>
/// <param name="Description">Translated one-line description.</param>
/// <param name="Ais">The identifiers it adds, in order.</param>
public sealed record Gs1Preset(string Key, string Name, string Description, IReadOnlyList<string> Ais)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>One identifier of the message being built, with the field for its value.</summary>
public sealed partial class Gs1Segment : ObservableObject
{
    private readonly Gs1BuilderViewModel _owner;

    /// <summary>Creates a segment.</summary>
    internal Gs1Segment(Gs1BuilderViewModel owner, string ai, string value, FieldsViewModel? fields)
    {
        _owner = owner;
        Ai = ai;
        Definition = Gs1Dictionary.Find(ai);
        Title = Definition?.Title ?? Loc.Get("Gs1Builder.UnknownAi");
        Format = Definition?.FormatText ?? string.Empty;
        string label = $"({ai}) {Title}";
        _text = value;
        var row = new TextRow("Gs1Builder.Value", () => _text, v => SetText(v), fields: fields, setDiscrete: (v, _) => SetText(v));
        Value = new BuilderField(ai, label, row, required: true)
        {
            Hint = Definition is null ? string.Empty : Loc.Get("Gs1Builder.Format", Format, Definition.MaxValueLength),
        };
    }

    private string _text = string.Empty;

    private void SetText(string value)
    {
        if (_text == value) return;
        _text = value;
        Value.Touched = true;
        _owner.OnSegmentChanged();
    }

    /// <summary>The identifier digits.</summary>
    public string Ai { get; }

    /// <summary>The dictionary entry, or null for an identifier the dictionary does not know.</summary>
    public Gs1AiDefinition? Definition { get; }

    /// <summary>Name of the data.</summary>
    public string Title { get; }

    /// <summary>Format of the value.</summary>
    public string Format { get; }

    /// <summary>"(01) GTIN" for the heading of the segment.</summary>
    public string Heading => $"({Ai}) {Title}";

    /// <inheritdoc />
    public override string ToString() => Heading;

    /// <summary>The value as typed, possibly with <c>{{field}}</c> placeholders.</summary>
    public string Text => _text;

    /// <summary>The text field of the value (with the Insert field button and autocomplete).</summary>
    public BuilderField Value { get; }

    /// <summary>Takes the segment out of the message.</summary>
    [RelayCommand]
    private void Remove() => _owner.RemoveSegment(this);

    /// <summary>Moves the segment one place up.</summary>
    [RelayCommand]
    private void MoveUp() => _owner.MoveSegment(this, -1);

    /// <summary>Moves the segment one place down.</summary>
    [RelayCommand]
    private void MoveDown() => _owner.MoveSegment(this, 1);
}

/// <summary>
/// The GS1 builder for GS1-128 and GS1 DataMatrix barcodes: choose application identifiers from the GS1 dictionary (or start from a preset for
/// shipping, product or serialisation), type or bind their values, and see the element string the symbol will carry. Each value is checked with
/// the dictionary's rules in plain language, the length against the 48 characters GS1-128 allows is counted, and Apply stays disabled until the
/// message is valid. The result is the bracketed form <c>(01)09506000134352(10){{Lot}}</c>.
/// </summary>
public sealed partial class Gs1BuilderViewModel : ObservableObject
{
    private const int MaxResults = 60;
    private static readonly string[] PopularAis = { "00", "01", "02", "10", "11", "13", "15", "17", "21", "30", "37", "240", "241", "310", "400", "410", "420" };
    private static readonly Regex Token = new(@"\{\{\s*(#?[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FieldsViewModel? _fields;
    private readonly IReadOnlyList<LabelVariable> _variables;
    private bool _updating;

    /// <summary>Creates the builder.</summary>
    /// <param name="fields">The shared list of available fields (null: no field controls).</param>
    /// <param name="symbology">Code128 (GS1-128) or DataMatrix (GS1 DataMatrix).</param>
    /// <param name="currentData">The data of the barcode; when it is an element string in brackets, its identifiers are restored.</param>
    /// <param name="variables">The label's variables, whose sample values stand in for bound fields.</param>
    public Gs1BuilderViewModel(FieldsViewModel? fields, BarcodeSymbology symbology, string? currentData, IReadOnlyList<LabelVariable>? variables = null)
    {
        _fields = fields;
        Symbology = symbology;
        _variables = variables ?? Array.Empty<LabelVariable>();
        Presets = new[]
        {
            new Gs1Preset("Shipping", Loc.Get("Gs1Builder.Preset.Shipping"), Loc.Get("Gs1Builder.Preset.Shipping.Detail"), new[] { "00" }),
            new Gs1Preset("Product", Loc.Get("Gs1Builder.Preset.Product"), Loc.Get("Gs1Builder.Preset.Product.Detail"), new[] { "01", "10", "17" }),
            new Gs1Preset("Serial", Loc.Get("Gs1Builder.Preset.Serial"), Loc.Get("Gs1Builder.Preset.Serial.Detail"), new[] { "01", "21" }),
        };
        if (!string.IsNullOrWhiteSpace(currentData) && currentData.TrimStart().StartsWith('(')
            && Gs1Message.ParseBracketed(currentData, out _) is { } message)
        {
            foreach (var e in message.Elements) Segments.Add(new Gs1Segment(this, e.Ai, e.Value, _fields));
            Restored = Segments.Count > 0;
        }
        if (_fields is not null) _fields.FieldsChanged += OnFieldsChanged;
        RunSearch();
        Recompute();
    }

    /// <summary>Detaches from the shared field list; the dialog calls it when it closes.</summary>
    public void Detach()
    {
        if (_fields is not null) _fields.FieldsChanged -= OnFieldsChanged;
    }

    private void OnFieldsChanged(object? sender, EventArgs e) => Recompute();

    /// <summary>True when the barcode already held an element string, which was restored.</summary>
    public bool Restored { get; }

    /// <summary>Code128 for GS1-128, DataMatrix for GS1 DataMatrix.</summary>
    public BarcodeSymbology Symbology { get; }

    /// <summary>True for GS1-128, which has the 48 character limit.</summary>
    public bool IsGs128 => Symbology == BarcodeSymbology.Code128;

    /// <summary>Window title.</summary>
    public string Title => Loc.Get(IsGs128 ? "Gs1Builder.Title.Gs128" : "Gs1Builder.Title.DataMatrix");

    // ----------------------------------------------------------------------------------- picker

    /// <summary>The use-case presets.</summary>
    public IReadOnlyList<Gs1Preset> Presets { get; }

    /// <summary>What the user typed in the search box (an identifier, or words of its name).</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    partial void OnSearchChanged(string value) => RunSearch();

    /// <summary>The identifiers that match the search.</summary>
    public ObservableCollection<Gs1AiItem> Results { get; } = new();

    /// <summary>How many identifiers match ("12 identifiers").</summary>
    [ObservableProperty]
    private string _resultsText = string.Empty;

    /// <summary>Refreshes <see cref="Results"/> from <see cref="Search"/>: by identifier prefix first, then by words of the name.</summary>
    public void RunSearch()
    {
        string q = Search.Trim();
        IEnumerable<Gs1AiDefinition> found;
        if (q.Length == 0) found = PopularAis.Select(Gs1Dictionary.Find).OfType<Gs1AiDefinition>();
        else
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Matches(Gs1AiDefinition d) => words.All(w => d.Ai.StartsWith(w, StringComparison.Ordinal)
                || d.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || d.DataTitle.Contains(w, StringComparison.OrdinalIgnoreCase));
            found = Gs1Dictionary.All.Where(Matches)
                .OrderBy(d => d.Ai.StartsWith(q, StringComparison.Ordinal) ? 0 : 1).ThenBy(d => d.Ai, StringComparer.Ordinal);
        }
        var list = found.Take(MaxResults).Select(d => new Gs1AiItem(d)).ToList();
        Results.Clear();
        foreach (var item in list) Results.Add(item);
        ResultsText = q.Length == 0 ? Loc.Get("Gs1Builder.Results.Popular") : Loc.Get("Gs1Builder.Results.Count", list.Count);
    }

    /// <summary>Adds an identifier to the message.</summary>
    [RelayCommand]
    private void AddAi(Gs1AiItem? item)
    {
        if (item is null) return;
        AddAiCore(item.Ai);
    }

    /// <summary>Adds an identifier by its digits. Returns the new segment, or null when the identifier is unknown.</summary>
    public Gs1Segment? AddAiCore(string ai)
    {
        if (!Gs1Dictionary.IsKnown(ai)) return null;
        var segment = new Gs1Segment(this, ai, string.Empty, _fields);
        Segments.Add(segment);
        Recompute();
        return segment;
    }

    /// <summary>Replaces the message by the identifiers of a preset; values already typed for the same identifiers are kept.</summary>
    [RelayCommand]
    private void ApplyPreset(Gs1Preset? preset)
    {
        if (preset is null) return;
        var old = Segments.ToList();
        Segments.Clear();
        foreach (var ai in preset.Ais)
        {
            var kept = old.FirstOrDefault(s => s.Ai == ai);
            Segments.Add(new Gs1Segment(this, ai, kept?.Text ?? string.Empty, _fields));
            if (kept is not null) old.Remove(kept);
        }
        Recompute();
    }

    // ----------------------------------------------------------------------------------- the message

    /// <summary>The identifiers of the message, in the order they are encoded.</summary>
    public ObservableCollection<Gs1Segment> Segments { get; } = new();

    internal void OnSegmentChanged() => Recompute();

    internal void RemoveSegment(Gs1Segment segment)
    {
        Segments.Remove(segment);
        Recompute();
    }

    internal void MoveSegment(Gs1Segment segment, int delta)
    {
        int i = Segments.IndexOf(segment), j = i + delta;
        if (i < 0 || j < 0 || j >= Segments.Count) return;
        Segments.Move(i, j);
        Recompute();
    }

    /// <summary>True when no identifier was added yet.</summary>
    public bool IsEmpty => Segments.Count == 0;

    // ----------------------------------------------------------------------------------- the result

    /// <summary>The element string in brackets with placeholders, which is what goes into the barcode's Data.</summary>
    [ObservableProperty]
    private string _template = string.Empty;

    /// <summary>The human-readable line printed under the symbol, with the values of the sample record.</summary>
    [ObservableProperty]
    private string _humanReadable = string.Empty;

    /// <summary>The encoded characters with the FNC1 markers shown, for example "[FNC1]0109506000134352[FNC1]10ABC".</summary>
    [ObservableProperty]
    private string _fnc1Text = string.Empty;

    /// <summary>The counter: "23 of 48 characters" for GS1-128, "23 characters" for GS1 DataMatrix.</summary>
    [ObservableProperty]
    private string _counterText = string.Empty;

    /// <summary>True when the counter is over the limit or only an estimate.</summary>
    [ObservableProperty]
    private bool _counterIsWarning;

    /// <summary>How many data characters the message needs (identifiers, values and separators).</summary>
    [ObservableProperty]
    private int _dataCharacters;

    /// <summary>The drawing of the symbol, or null while the message is incomplete or invalid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private BarcodeMatrix? _previewMatrix;

    /// <summary>True when there is a symbol to draw.</summary>
    public bool HasPreview => PreviewMatrix is not null;

    /// <summary>Problems and warnings in plain language.</summary>
    public ObservableCollection<BuilderMessage> Messages { get; } = new();

    /// <summary>True when there are messages.</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>The neutral line shown while Apply is disabled only because the message is empty or a value is still missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusHint))]
    private string _statusHint = string.Empty;

    /// <summary>True when <see cref="StatusHint"/> is set.</summary>
    public bool HasStatusHint => StatusHint.Length > 0;

    /// <summary>True when the message is valid and complete.</summary>
    [ObservableProperty]
    private bool _canApply;

    partial void OnCanApplyChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    /// <summary>The value of a placeholder in the record shown on the canvas, or the label variable's sample; null when it is not known.</summary>
    private string? SampleOf(string name)
    {
        if (_fields?.Find(name) is { HasRecord: true } f) return f.Sample;
        return _variables.FirstOrDefault(v => v.Name == name)?.SampleValue;
    }

    /// <summary>Checks everything again: each value, the relations between identifiers, the length and the preview.</summary>
    private void Recompute()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            Messages.Clear();
            foreach (var s in Segments) { s.Value.Error = string.Empty; s.Value.Warning = string.Empty; }
            OnPropertyChanged(nameof(IsEmpty));

            // The message as designed (placeholders kept) and as it would print for the sample record (placeholders replaced).
            var designed = new Gs1Message(Segments.Select(s => new Gs1Element(s.Ai, s.Text)));
            Template = designed.ToBracketed();

            bool estimated = false, incomplete = false, errors = false;
            var resolvedElements = new List<Gs1Element>();
            foreach (var s in Segments)
            {
                string value = Resolve(s.Text, out bool unknown);
                if (s.Text.Length == 0) incomplete = true;
                if (unknown)
                {
                    // A bound field with no value to look at: the value cannot be checked yet, and its length is only a guess.
                    estimated = true;
                    s.Value.Warning = Loc.Get("Gs1Builder.Bound.Unchecked");
                    value = Guess(s.Definition);
                }
                resolvedElements.Add(new Gs1Element(s.Ai, value));
                if (s.Text.Length == 0)
                {
                    if (s.Value.Touched) { s.Value.Error = Loc.Get("QrContent.Field.Required"); errors = true; AddMessage(s, s.Value.Error, true); }
                    continue;
                }
                if (unknown) continue;
                foreach (var issue in Gs1Validator.ValidateElement(new Gs1Element(s.Ai, value)))
                {
                    string text = Loc.FormatIssue(issue);
                    bool isError = issue.Severity == ValidationSeverity.Error;
                    if (isError && s.Value.Error.Length == 0) s.Value.Error = text;
                    errors |= isError;
                    AddMessage(s, text, isError);
                }
            }

            var resolved = new Gs1Message(resolvedElements);
            if (Segments.Count == 0)
            {
                HumanReadable = string.Empty;
                Fnc1Text = string.Empty;
                DataCharacters = 0;
                CounterText = IsGs128 ? Loc.Get("Gs1Builder.Counter", 0, Gs1Validator.MaxGs128DataCharacters) : Loc.Get("Gs1Builder.Counter.Open", 0);
                CounterIsWarning = false;
                PreviewMatrix = null;
                CanApply = false;
                StatusHint = Loc.Get("Gs1Builder.Hint.Empty");
                OnPropertyChanged(nameof(HasMessages));
                return;
            }

            // Relations between identifiers (duplicates, "cannot be combined", "needs"): per message, not per value.
            foreach (var issue in Gs1Validator.ValidateRelations(resolved))
            {
                bool isError = issue.Severity == ValidationSeverity.Error;
                errors |= isError;
                Messages.Add(new BuilderMessage(Loc.FormatIssue(issue), isError));
            }

            HumanReadable = resolved.ToHumanReadable();
            Fnc1Text = "[FNC1]" + resolved.ToElementString().Replace(Gs1Message.GroupSeparator.ToString(), "[FNC1]", StringComparison.Ordinal);
            DataCharacters = resolved.DataCharacterCount;
            bool over = IsGs128 && DataCharacters > Gs1Validator.MaxGs128DataCharacters;
            CounterText = IsGs128
                ? Loc.Get(estimated ? "Gs1Builder.Counter.Estimated" : "Gs1Builder.Counter", DataCharacters, Gs1Validator.MaxGs128DataCharacters)
                : Loc.Get("Gs1Builder.Counter.Open", DataCharacters);
            CounterIsWarning = over || estimated;
            if (over)
            {
                // Over the limit with real values is an error; with guessed lengths it is only a warning, because the real data may be shorter.
                string text = Loc.Get("Gs1Builder.TooLong", DataCharacters, Gs1Validator.MaxGs128DataCharacters);
                Messages.Add(new BuilderMessage(text, !estimated));
                errors |= !estimated;
            }

            PreviewMatrix = null;
            if (!errors && !incomplete)
            {
                string preview = resolved.ToBracketed();
                PreviewMatrix = BarcodeEncoder.TryEncode(IsGs128 ? BarcodeSymbology.Code128 : BarcodeSymbology.DataMatrix, preview, new BarcodeEncodeOptions { Gs1 = true }, out _);
            }
            CanApply = !errors && !incomplete && Segments.Count > 0;
            StatusHint = incomplete && !errors ? Loc.Get("Gs1Builder.Hint.Incomplete") : string.Empty;
            OnPropertyChanged(nameof(HasMessages));
        }
        finally { _updating = false; }
    }

    private void AddMessage(Gs1Segment segment, string text, bool isError) => Messages.Add(new BuilderMessage($"({segment.Ai}) {segment.Title}: {text}", isError));

    /// <summary>Replaces placeholders by their sample values. <paramref name="unknown"/> is set when a placeholder has none.</summary>
    private string Resolve(string text, out bool unknown)
    {
        bool miss = false;
        string result = Token.Replace(text, m =>
        {
            string? sample = SampleOf(m.Groups[1].Value);
            if (sample is null) miss = true;
            return sample ?? string.Empty;
        });
        unknown = miss;
        return result;
    }

    /// <summary>A stand-in value of a plausible length for an identifier whose bound field has no sample.</summary>
    private static string Guess(Gs1AiDefinition? def)
    {
        if (def is null) return "0";
        int length = def.HasFixedValueLength ? def.MaxValueLength : Math.Min(def.MaxValueLength, 12);
        return new string('0', Math.Max(1, length));
    }

    // ----------------------------------------------------------------------------------- result

    /// <summary>True after Apply.</summary>
    public bool Applied { get; private set; }

    /// <summary>Raised when the dialog should close; the argument is true for Apply.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Accepts the message and asks the dialog to close. Does nothing while the message is not valid.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply) return;
        Applied = true;
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>Closes without applying.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
