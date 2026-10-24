using System.Collections.Concurrent;
using System.Xml.Linq;
using Rotula.App.Localization;

namespace Rotula.Tests;

/// <summary>
/// Reads the application's string tables (src/Rotula.App/Resources/Strings.&lt;language&gt;.resx) for the tests that check
/// translations. Every check goes through <see cref="Languages"/>, so adding a language to <see cref="Loc.SupportedLanguages"/>
/// puts it under the same checks with no test to edit.
/// </summary>
public static class UiStrings
{
    // The files are large and many tests read them; parse each once per test run.
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.Ordinal);

    /// <summary>Every shipped language, English (the reference) first.</summary>
    public static IReadOnlyList<string> Languages => Loc.SupportedLanguages;

    /// <summary>Every shipped language except English.</summary>
    public static IEnumerable<string> Translations => Languages.Where(l => l != "en");

    /// <summary><see cref="Languages"/> as theory data.</summary>
    public static TheoryData<string> LanguageData => ToData(Languages);

    /// <summary><see cref="Translations"/> as theory data.</summary>
    public static TheoryData<string> TranslationData => ToData(Translations);

    private static TheoryData<string> ToData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var v in values) data.Add(v);
        return data;
    }

    /// <summary>The repository root (the folder that holds Rotula.sln).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Rotula.sln not found above the test folder.");
    }

    /// <summary>The folder that holds the Strings.*.resx files.</summary>
    public static string ResourcesFolder() => Path.Combine(RepoRoot(), "src", "Rotula.App", "Resources");

    /// <summary>The culture names of the Strings.&lt;name&gt;.resx files that exist on disk.</summary>
    public static IEnumerable<string> FileLanguages() =>
        Directory.EnumerateFiles(ResourcesFolder(), "Strings.*.resx")
            .Select(f => Path.GetFileNameWithoutExtension(f)["Strings.".Length..]);

    /// <summary>Key to text for one language.</summary>
    public static IReadOnlyDictionary<string, string> Read(string language) =>
        Cache.GetOrAdd(language, l => XDocument.Load(Path.Combine(ResourcesFolder(), $"Strings.{l}.resx")).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value, StringComparer.Ordinal));

    /// <summary>The keys of one language.</summary>
    public static HashSet<string> Keys(string language) => Read(language).Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The keys of <paramref name="wanted"/> that some language lacks, as "language: key" (empty when all are present).</summary>
    public static List<string> MissingInAnyLanguage(IEnumerable<string> wanted)
    {
        var list = wanted.ToList();
        return Languages.SelectMany(l => list.Where(k => !Read(l).ContainsKey(k)).Select(k => $"{l}: {k}")).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
