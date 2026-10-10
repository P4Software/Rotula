using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>Helpers shared by the Core* test files: base documents and golden-file comparison.</summary>
internal static class CoreTestSupport
{
    /// <summary>An 800x400 dot label at 203 DPI with default settings, so goldens stay easy to read.</summary>
    public static LabelDocument NewDoc(params LabelElement[] elements)
    {
        var doc = new LabelDocument { Width = Measure.Dots(800), Height = Measure.Dots(400) };
        doc.Elements.AddRange(elements);
        return doc;
    }

    /// <summary>Generates ZPL and fails the test if the generator reported any error.</summary>
    public static string Zpl(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null)
    {
        var result = ZplGenerator.Generate(doc, data);
        Assert.False(result.HasErrors, string.Join(", ", result.Issues));
        return result.Zpl;
    }

    private static string FixturesDir()
    {
        // Walk up from the test binary to the repo root so no csproj change or env var is needed.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "fixtures");
    }

    /// <summary>
    /// Compares <paramref name="actual"/> with tests/fixtures/NAME.zpl. A missing fixture is written
    /// and the test fails once, so a new golden must be reviewed and committed deliberately.
    /// </summary>
    public static void AssertGolden(string name, string actual)
    {
        string path = Path.Combine(FixturesDir(), name + ".zpl");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new System.Text.UTF8Encoding(false));
            Assert.Fail($"Golden file '{name}.zpl' did not exist and was created. Review it, then rerun.");
        }
        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }
}
