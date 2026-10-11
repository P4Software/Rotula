using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rotula.App.Localization;
using Rotula.Data;

namespace Rotula.App.ViewModels;

/// <summary>
/// One row of the engine list in the connection manager: a provider, or a preset listed under its provider. Built from
/// <see cref="ProviderRegistry.GetDisplayEntries"/>, so engines that an add-on registers appear without any UI change.
/// </summary>
public sealed class EngineEntry : ObservableObject
{
    /// <summary>Creates an entry.</summary>
    public EngineEntry(ProviderDisplayEntry entry, ProviderCategory groupCategory)
    {
        Entry = entry;
        GroupCategory = groupCategory;
    }

    /// <summary>The registry row.</summary>
    public ProviderDisplayEntry Entry { get; }

    /// <summary>Provider id, or preset id for a preset.</summary>
    public string Id => Entry.Id;

    /// <summary>The provider that connects (equals <see cref="Id"/> for a provider).</summary>
    public string ProviderId => Entry.ProviderId;

    /// <summary>True for a preset listed under its provider.</summary>
    public bool IsPreset => Entry.Kind == ProviderEntryKind.Preset;

    /// <summary>The category of the PROVIDER; presets are listed in the group of the provider they belong to.</summary>
    public ProviderCategory GroupCategory { get; }

    /// <summary>Translated group heading.</summary>
    public string GroupName => Loc.Instance.HasKey("Data.Category." + GroupCategory) ? Loc.Get("Data.Category." + GroupCategory) : GroupCategory.ToString();

    /// <summary>Translated name; the English name of the registry is the fallback for an add-on without a translation.</summary>
    public string Name => Loc.Instance.HasKey(Entry.DisplayNameKey) ? Loc.Get(Entry.DisplayNameKey) : Entry.DisplayName;

    /// <summary>Re-reads the translated texts after a language switch.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(GroupName));
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>A translated hint under the connection form (a preset hint, the read-only account advice, a missing driver).</summary>
public sealed class FormHint : ObservableObject
{
    /// <summary>Creates a hint.</summary>
    public FormHint(string key, bool isWarning)
    {
        Key = key;
        IsWarning = isWarning;
    }

    /// <summary>Message key.</summary>
    public string Key { get; }

    /// <summary>True for a hint that needs attention.</summary>
    public bool IsWarning { get; }

    /// <summary>Translated text; a key without text is shown as a short neutral sentence rather than the key.</summary>
    public string Text => Loc.Instance.HasKey(Key) ? Loc.Get(Key) : string.Empty;

    /// <summary>Re-reads the text.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Text));
}

/// <summary>
/// One field of the connection form, created from a <see cref="ProviderOption"/>. The value lives in the connection manager (standard
/// options map to its properties, other options to the provider extras); this class only reads and writes it through delegates.
/// </summary>
public sealed class OptionFieldViewModel : ObservableObject
{
    private readonly Func<string, string> _get;
    private readonly Action<string, string> _set;
    private readonly Action<OptionFieldViewModel>? _browse;

    /// <summary>Creates a field.</summary>
    /// <param name="option">The option this field edits.</param>
    /// <param name="get">Reads the current value of an option key as text.</param>
    /// <param name="set">Writes a value.</param>
    /// <param name="browse">Opens a picker for file and folder fields, or null.</param>
    /// <param name="advanced">True when the field belongs in the "More options" expander.</param>
    /// <param name="placeholder">Grey hint shown while the box is empty (the usual port).</param>
    public OptionFieldViewModel(ProviderOption option, Func<string, string> get, Action<string, string> set, Action<OptionFieldViewModel>? browse, bool advanced, string placeholder = "")
    {
        Option = option;
        _get = get;
        _set = set;
        _browse = browse;
        IsAdvanced = advanced;
        Placeholder = placeholder;
        Choices = option.Choices.Select(c => new Choice<string>(c.Value, c.LabelKey)).ToList();
    }

    /// <summary>The option definition.</summary>
    public ProviderOption Option { get; }

    /// <summary>The option key (a standard key, or the key of a provider extra).</summary>
    public string Key => Option.Key;

    /// <summary>True when the field is shown inside "More options".</summary>
    public bool IsAdvanced { get; }

    /// <summary>Overrides the file/folder nature of a path field (set by presets over file formats).</summary>
    public bool IsFolder { get; init; }

    /// <summary>Overrides the label for path fields built from a preset ("File" or "Folder").</summary>
    public string? LabelKeyOverride { get; init; }

    /// <summary>Translated label; an add-on option without a translation shows its English label.</summary>
    public string Label => LabelKeyOverride is not null ? Loc.Get(LabelKeyOverride)
        : Loc.Instance.HasKey(Option.LabelKey) ? Loc.Get(Option.LabelKey) : Option.Label;

    /// <summary>Host patterns of the chosen preset (<c>*.pooler.supabase.com</c>), shown as "addresses of this service look like...".</summary>
    public IReadOnlyList<string> HostPatterns { get; init; } = Array.Empty<string>();

    /// <summary>The sentence about what addresses of the preset look like, or empty.</summary>
    public string PatternHelp => HostPatterns.Count > 0 ? Loc.Get("Conn.Preset.HostHint", string.Join(", ", HostPatterns)) : string.Empty;

    /// <summary>True when there is a pattern sentence.</summary>
    public bool HasPatternHelp => HostPatterns.Count > 0;

    /// <summary>Translated one-sentence help, or empty.</summary>
    public string Help => Option.HelpKey is { } k && Loc.Instance.HasKey(k) ? Loc.Get(k) : string.Empty;

    /// <summary>True when there is a help sentence.</summary>
    public bool HasHelp => Help.Length > 0 && !IsBool;

    /// <summary>Help for a switch is shown under the check box.</summary>
    public bool HasSwitchHelp => Help.Length > 0 && IsBool;

    /// <summary>Grey text inside an empty box.</summary>
    public string Placeholder { get; }

    /// <summary>True while the box is empty and there is a placeholder to show.</summary>
    public bool ShowPlaceholder => Placeholder.Length > 0 && Text.Length == 0;

    /// <summary>True for a short number box such as the port, which sits beside the previous field.</summary>
    public bool IsShort => Option.Kind == ProviderOptionKind.Number;

    /// <summary>Text, host and number boxes.</summary>
    public bool IsTextLike => Option.Kind is ProviderOptionKind.Text or ProviderOptionKind.Host or ProviderOptionKind.Number;

    /// <summary>True for a path with a Browse button.</summary>
    public bool IsPath => Option.Kind == ProviderOptionKind.FilePath;

    /// <summary>True for a check box.</summary>
    public bool IsBool => Option.Kind == ProviderOptionKind.Bool;

    /// <summary>True for a drop-down.</summary>
    public bool IsChoice => Option.Kind == ProviderOptionKind.Choice;

    /// <summary>True when the label sits above the control (everything but a check box).</summary>
    public bool ShowLabel => !IsBool;

    /// <summary>True for a monospaced box (ports, paths, connection strings).</summary>
    public bool IsMono => IsPath || Option.Kind == ProviderOptionKind.Number || Key == ProviderOptionKeys.OdbcConnectionString;

    /// <summary>The choices of a drop-down.</summary>
    public IReadOnlyList<Choice<string>> Choices { get; }

    /// <summary>Text value of text, number and path fields.</summary>
    public string Text
    {
        get => _get(Key);
        set
        {
            if (_get(Key) == value) return;
            _set(Key, value ?? string.Empty);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    /// <summary>Value of a check box.</summary>
    public bool Flag
    {
        get => string.Equals(_get(Key), "true", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (Flag == value) return;
            _set(Key, value ? "true" : "false");
            OnPropertyChanged();
            OnPropertyChanged(nameof(WarningText));
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    /// <summary>Selected value of a drop-down.</summary>
    public string SelectedChoice
    {
        get => _get(Key);
        set
        {
            if (_get(Key) == value) return;
            _set(Key, value ?? string.Empty);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The security warning that goes with a risky switch: encryption off, or an unverifiable certificate accepted. It stays visible while
    /// the risky choice is on, so it cannot be forgotten.
    /// </summary>
    public string WarningText => Key switch
    {
        ProviderOptionKeys.Encrypt when !Flag => Loc.Get("Conn.Encrypt.Off"),
        ProviderOptionKeys.TrustServerCertificate when Flag => Loc.Get("Conn.Trust.On"),
        _ => string.Empty,
    };

    /// <summary>True while <see cref="WarningText"/> has something to say.</summary>
    public bool HasWarning => WarningText.Length > 0;

    /// <summary>True when the Browse button is available.</summary>
    public bool CanBrowse => _browse is not null;

    /// <summary>Text of the Browse button: a folder or a file.</summary>
    public string BrowseKey => IsFolder ? "Conn.BrowseFolder" : "Conn.Browse";

    /// <summary>Runs the file or folder picker.</summary>
    public void Browse() => _browse?.Invoke(this);

    /// <summary>Re-reads the value after the manager changed it, or the texts after a language switch.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Flag));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(HasHelp));
        OnPropertyChanged(nameof(HasSwitchHelp));
        OnPropertyChanged(nameof(PatternHelp));
        foreach (var c in Choices) c.Refresh();
    }

    /// <summary>Checks a number against the option's limits; null when the text is empty or fine.</summary>
    public string? ValidateNumber()
    {
        if (Option.Kind != ProviderOptionKind.Number) return null;
        string t = Text.Trim();
        if (t.Length == 0) return null;
        int min = Option.Minimum ?? 0, max = Option.Maximum ?? int.MaxValue;
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max) return null;
        return Key == ProviderOptionKeys.Port ? Loc.Get("Conn.Port.Invalid") : Loc.Get("Conn.Option.NumberInvalid", Label, min, max);
    }
}
