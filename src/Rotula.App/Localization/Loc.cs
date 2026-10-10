using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Rotula.Core.Validation;

namespace Rotula.App.Localization;

/// <summary>
/// The application's translator. Every piece of UI text comes from Resources/Strings.en.resx or Strings.es.resx through
/// this class. XAML binds to the indexer (<c>{loc:T Key}</c>), so switching the language updates the open windows at once
/// (the "live" option, see docs/decisions.md).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Language codes the application ships.</summary>
    public static readonly string[] SupportedLanguages = { "en", "es" };

    private readonly ResourceManager _resources = new("Rotula.App.Resources.Strings", typeof(Loc).Assembly);
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    /// <summary>The single instance used by XAML bindings and view-models.</summary>
    public static Loc Instance { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language currently shown (always "en" or "es").</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Two-letter code of the language currently shown.</summary>
    public string LanguageCode => _culture.TwoLetterISOLanguageName;

    /// <summary>
    /// Translates a key. A key that is missing in the current language falls back to English; a key missing everywhere is
    /// shown as <c>!key!</c> so the gap is visible instead of silently blank.
    /// </summary>
    public string this[string key] => Lookup(key);

    /// <summary>Translates a key and formats it with arguments (formatted in the current language's culture).</summary>
    public static string Get(string key, params object[] args)
    {
        string text = Instance.Lookup(key);
        return args.Length == 0 ? text : string.Format(Instance._culture, text, args);
    }

    /// <summary>True when the key exists in the English resources (the reference language).</summary>
    public bool HasKey(string key) => TryGet(key, CultureInfo.GetCultureInfo("en")) is not null;

    private string Lookup(string key) =>
        TryGet(key, _culture) ?? TryGet(key, CultureInfo.GetCultureInfo("en")) ?? "!" + key + "!";

    /// <summary>
    /// Looks a key up in one language. When the key is missing, ResourceManager keeps walking up to the neutral
    /// resources, which this application deliberately does not have, and throws; that just means "not found" here.
    /// </summary>
    private string? TryGet(string key, CultureInfo culture)
    {
        try
        {
            return _resources.GetString(key, culture);
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the language: "en" or "es" explicitly, or (for "auto" or anything unknown) the Windows display language
    /// when it is Spanish, English otherwise.
    /// </summary>
    public static string ResolveLanguage(string? setting, CultureInfo? windowsUiCulture = null)
    {
        if (setting is "en" or "es") return setting;
        var ui = windowsUiCulture ?? CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
    }

    /// <summary>Switches the language and tells every binding to refresh.</summary>
    public void SetLanguage(string setting)
    {
        var code = ResolveLanguage(setting);
        var culture = CultureInfo.GetCultureInfo(code);
        if (culture.Equals(_culture)) return;
        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // "Item[]" is the change notification name for an indexer: all {Binding [key]} refresh.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the language changed (for view-models that cache translated text).</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>Localized display name of an enum value, from the key <c>Enum.TypeName.Value</c>.</summary>
    public static string EnumName<T>(T value) where T : struct, Enum => Instance.Lookup($"Enum.{typeof(T).Name}.{value}");

    /// <summary>
    /// Turns a structured validation issue from Core into a sentence in the current language. The message key is
    /// looked up as <c>Issue.&lt;MessageKey&gt;</c>; arguments that are themselves keys (<c>Issue.Arg.odd</c>) are translated too.
    /// </summary>
    public static string FormatIssue(ValidationIssue issue)
    {
        string key = "Issue." + issue.MessageKey;
        // Rotula.Data raises its own notes (for example the row-cap warning) under their Data.* key, with no Issue. prefix.
        if (!Instance.HasKey(key) && issue.MessageKey.StartsWith("Data.", StringComparison.Ordinal) && Instance.HasKey(issue.MessageKey))
            key = issue.MessageKey;
        if (!Instance.HasKey(key))
            return Get("Issue.Unknown", issue.MessageKey);
        var args = issue.Args.Select(a => (object)(Instance.HasKey("Issue.Arg." + a) ? Instance.Lookup("Issue.Arg." + a) : a)).ToArray();
        return Get(key, args);
    }
}
