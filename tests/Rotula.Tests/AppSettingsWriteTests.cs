using Rotula.App.Services;

namespace Rotula.Tests;

/// <summary>Saving settings.json must never leave a damaged or truncated file behind.</summary>
public class AppSettingsWriteTests
{
    [Fact]
    public void Save_ReplacesAnExistingFile_AndLeavesNoTemporaryFile()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new JsonSettingsStore(path);
            Assert.True(store.Save(new AppSettings { Language = "es" }));
            Assert.True(store.Save(new AppSettings { Language = "fr-CA", AutosaveSeconds = 90 }));
            var back = new JsonSettingsStore(path).Load();
            Assert.Equal("fr-CA", back.Language);
            Assert.Equal(90, back.AutosaveSeconds);
            Assert.Equal(new[] { "settings.json" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_WhenTheFileCannotBeReplaced_KeepsTheOldSettings_AndCleansUp()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new JsonSettingsStore(path);
            Assert.True(store.Save(new AppSettings { Language = "es" }));
            byte[] before = File.ReadAllBytes(path);

            // Another program holds the file open: the swap fails, and the old text must survive untouched.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(store.Save(new AppSettings { Language = "en" }));
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(new[] { "settings.json" }, Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray());
            Assert.Equal("es", new JsonSettingsStore(path).Load().Language);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"language\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"gridVisible\": \"maybe\" }")]
    public void Load_ADamagedFile_GivesTheDefaults_AndSaysSo(string text)
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, text);
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.Equal("auto", settings.Language);
            Assert.Equal(30, settings.AutosaveSeconds);
            Assert.True(store.LoadProblem);
            // The next save writes a good file over the damaged one.
            Assert.True(store.Save(settings));
            var again = new JsonSettingsStore(path);
            Assert.Equal("auto", again.Load().Language);
            Assert.False(again.LoadProblem);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheStarterFile_ExplainsTheAutosaveSetting()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            new JsonSettingsStore(path).Load();
            string text = File.ReadAllText(path);
            Assert.Contains("// autosaveSeconds", text);
            Assert.Contains("\"autosaveSeconds\": 30", text);
        }
        finally { Directory.Delete(dir, true); }
    }
}
