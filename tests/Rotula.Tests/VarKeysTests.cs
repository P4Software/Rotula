using System.Xml.Linq;
using Rotula.Core.Variables;

namespace Rotula.Tests;

/// <summary>The message keys of the variable engine: well formed, unique, and translated in every language.</summary>
public class VarKeysTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static HashSet<string> ResxKeys(string lang) => UiStrings.Keys(lang);

    [Fact]
    public void KeysAreUniqueAndShapedLikeTheOthers()
    {
        var all = VariableMessageKeys.All;
        Assert.True(all.Count > 40);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, k => Assert.Matches(@"^Variable(\.[A-Za-z0-9]+)+$", k));
    }

    [Fact]
    public void EveryKeyTheEngineCanRaise_IsListed()
    {
        // Keys written as literals anywhere in the variable code must be in the list (they would otherwise reach the user untranslated).
        string folder = Path.Combine(RepoRoot(), "src", "Rotula.Core", "Variables");
        var used = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("VariableMessageKeys.cs", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(Variable\\.[A-Za-z0-9.]+)\"").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.Empty(used.Except(VariableMessageKeys.All));
    }

    /// <summary>
    /// Expected to FAIL until the UI work adds the texts: each key needs <c>Issue.&lt;key&gt;</c> in every Strings.&lt;language&gt;.resx.
    /// The failure message lists exactly what is missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(UiStrings.LanguageData), MemberType = typeof(UiStrings))]
    public void EveryKeyHasTextInTheAppResources(string lang)
    {
        var have = ResxKeys(lang);
        var missing = VariableMessageKeys.All.Where(k => !have.Contains("Issue." + k)).ToList();
        Assert.True(missing.Count == 0, $"Strings.{lang}.resx lacks Issue.* entries for: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryTextFormatsWithTheArgumentsTheKeyDocuments()
    {
        // Once the texts exist, none may use a hole beyond the arguments the engine passes (checked against the documented maximum).
        var en = XDocument.Load(Path.Combine(RepoRoot(), "src", "Rotula.App", "Resources", "Strings.en.resx"))
            .Descendants("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
        foreach (var key in VariableMessageKeys.All)
        {
            if (!en.TryGetValue("Issue." + key, out var text)) continue; // reported by the test above
            var holes = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d)\}").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();
            Assert.True(holes <= 5, key);
        }
    }
}
