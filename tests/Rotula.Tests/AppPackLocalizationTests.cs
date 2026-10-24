using System.Text.RegularExpressions;

namespace Rotula.Tests;

/// <summary>
/// The built-in registry test cannot see the database packs (they are loaded at run time), so this scan reads the pack
/// sources for string literals shaped like resource keys and checks that every language has a text for each.
/// </summary>
public class AppPackLocalizationTests
{
    private static string RepoRoot() => UiStrings.RepoRoot();

    [Fact]
    public void EveryResourceKeyLiteralInTheDatabasePacks_HasTextInEveryLanguage()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var shape = new Regex(@"""((?:Conn\.Provider|Data\.(?:Option|Preset|Dialect|Hint|Pack|Error))(?:\.[A-Za-z0-9]+)+)""");
        var keys = new HashSet<string>();
        foreach (var pack in Directory.EnumerateDirectories(src, "Rotula.Data.Pack.*"))
            foreach (var file in Directory.EnumerateFiles(pack, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
                foreach (var line in File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")))
                    foreach (Match m in shape.Matches(line)) keys.Add(m.Groups[1].Value);

        Assert.NotEmpty(keys);
        Assert.Empty(UiStrings.MissingInAnyLanguage(keys));
    }
}
