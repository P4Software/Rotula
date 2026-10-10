using Rotula.Mcp.Settings;

namespace Rotula.Tests;

/// <summary>The settings file next to the program: starter, defaults, clamping and damaged files.</summary>
public class McpSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-mcp-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, McpSettingsLoader.FileName);
    private string Docs => Path.Combine(_dir, "Documents");

    public McpSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void FirstRunCreatesACommentedStarterAndTheDefaultFolder()
    {
        var r = McpSettingsLoader.Load(SettingsPath, Docs);
        Assert.True(File.Exists(SettingsPath));
        string text = File.ReadAllText(SettingsPath);
        Assert.Contains("//", text);
        Assert.Contains("allowPrinting", text);
        Assert.Contains(r.Notes, n => n.Contains("Created the settings file"));
        Assert.Equal(Path.Combine(Docs, "Rotula Labels"), Assert.Single(r.Settings.AllowedFolders));
        Assert.True(Directory.Exists(Path.Combine(Docs, "Rotula Labels")));

        // Safe by default.
        Assert.False(r.Settings.AllowPrinting);
        Assert.Empty(r.Settings.AllowedPrinters);
        Assert.False(r.Settings.AllowDatabase);
        Assert.Equal(10, r.Settings.MaxCopies);
        Assert.Equal(1000, r.Settings.MaxDatabaseRows);
    }

    [Fact]
    public void TheStarterReadsBackAsTheSameSettings_AndTheSecondRunCreatesNothing()
    {
        var first = McpSettingsLoader.Load(SettingsPath, Docs);
        var second = McpSettingsLoader.Load(SettingsPath, Docs);
        Assert.Empty(second.Notes);
        Assert.Equal(first.Settings.AllowedFolders, second.Settings.AllowedFolders);
        Assert.Equal(first.Settings.MaxElements, second.Settings.MaxElements);
    }

    [Fact]
    public void ValuesAreReadAndClamped()
    {
        File.WriteAllText(SettingsPath, """
            { // comment
              "allowedFolders": [ "C:\\Labels", "D:\\More", ],
              "allowPrinting": true,
              "allowedPrinters": [ "Zebra" ],
              "allowDatabase": true,
              "maxElements": 99999,
              "maxFileBytes": 10,
              "maxCopies": 0,
              "maxDatabaseRows": 5,
            }
            """);
        var r = McpSettingsLoader.Load(SettingsPath, Docs);
        Assert.True(r.Settings.AllowPrinting);
        Assert.Equal(["Zebra"], r.Settings.AllowedPrinters);
        Assert.True(r.Settings.AllowDatabase);
        Assert.Equal(McpSettings.ElementLimitCeiling, r.Settings.MaxElements);
        Assert.Equal(1024, r.Settings.MaxFileBytes);
        Assert.Equal(1, r.Settings.MaxCopies);
        Assert.Equal(5, r.Settings.MaxDatabaseRows);
        Assert.Equal(2, r.Settings.AllowedFolders.Count);
        Assert.Contains(r.Notes, n => n.Contains("maxElements"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{ \"allowPrinting\": ")]
    [InlineData("")]
    public void ADamagedFileFallsBackToTheSafeDefaultsWithAPlainMessage(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var r = McpSettingsLoader.Load(SettingsPath, Docs);
        Assert.False(r.Settings.AllowPrinting);
        Assert.False(r.Settings.AllowDatabase);
        Assert.Single(r.Settings.AllowedFolders);
        Assert.Contains(r.Notes, n => n.Contains("safe defaults"));
        Assert.Equal(content, File.ReadAllText(SettingsPath)); // never overwritten
    }

    [Fact]
    public void WrongTypesAreIgnoredNotTrusted()
    {
        File.WriteAllText(SettingsPath, "{ \"allowPrinting\": \"yes\", \"allowDatabase\": 1, \"allowedPrinters\": \"Zebra\", \"maxCopies\": \"lots\" }");
        var r = McpSettingsLoader.Load(SettingsPath, Docs);
        Assert.False(r.Settings.AllowPrinting);
        Assert.False(r.Settings.AllowDatabase);
        Assert.Empty(r.Settings.AllowedPrinters);
        Assert.Equal(10, r.Settings.MaxCopies);
    }

    [Fact]
    public void AnUnwritableLocationStillGivesWorkingDefaults()
    {
        // A path whose parent does not exist cannot be written; the loader must not throw.
        string impossible = Path.Combine(_dir, "no-such-folder", McpSettingsLoader.FileName);
        var r = McpSettingsLoader.Load(impossible, Docs);
        Assert.False(r.Settings.AllowPrinting);
        Assert.Contains(r.Notes, n => n.Contains("Could not create"));
    }
}
