using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rotula.Tests;

/// <summary>
/// The built-in registry test cannot see the database packs (they are loaded at run time), so this scan reads the pack
/// sources for string literals shaped like resource keys and checks that both languages have a text for each.
/// </summary>
public class AppPackLocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static HashSet<string> ResxKeys(string lang) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "Rotula.App", "Resources", $"Strings.{lang}.resx"))
            .Descendants("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

    [Fact]
    public void EveryResourceKeyLiteralInTheDatabasePacks_HasTextInBothLanguages()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var shape = new Regex(@"""((?:Conn\.Provider|Data\.(?:Option|Preset|Dialect|Hint|Pack|Error))(?:\.[A-Za-z0-9]+)+)""");
        var keys = new HashSet<string>();
        foreach (var pack in Directory.EnumerateDirectories(src, "Rotula.Data.Pack.*"))
            foreach (var file in Directory.EnumerateFiles(pack, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
                foreach (var line in File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//")))
                    foreach (Match m in shape.Matches(line)) keys.Add(m.Groups[1].Value);

        Assert.NotEmpty(keys);
        Assert.Empty(keys.Where(k => !ResxKeys("en").Contains(k)).OrderBy(k => k));
        Assert.Empty(keys.Where(k => !ResxKeys("es").Contains(k)).OrderBy(k => k));
    }
}
