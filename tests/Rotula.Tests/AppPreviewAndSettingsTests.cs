using Rotula.App.Editing;
using Rotula.App.Services;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

[Collection("App")]
public class AppPreviewAndSettingsTests
{
    // ------------------------------------------------------------------------------- preview cache

    [Fact]
    public void RenderLabel_IsBuiltOnce_WhileTheDesignIsUnchanged()
    {
        var s = AppTestSupport.Session(AppTestSupport.Box(10, 10));
        using var cache = new PreviewCache(s);
        var first = cache.GetLabel();
        for (int i = 0; i < 20; i++) Assert.Same(first, cache.GetLabel());
        Assert.Equal(1, cache.LabelBuildCount);

        // Selecting changes nothing about the picture.
        s.SelectAll();
        Assert.Same(first, cache.GetLabel());
        Assert.Equal(1, cache.LabelBuildCount);
    }

    [Fact]
    public void RenderLabel_IsRebuilt_AfterAnEditOrUndo()
    {
        var s = AppTestSupport.Session(AppTestSupport.Box(10, 10));
        using var cache = new PreviewCache(s);
        var first = cache.GetLabel();
        new EditorCommands(s).Add(AppTestSupport.Box(50, 50));
        var second = cache.GetLabel();
        Assert.NotSame(first, second);
        Assert.Equal(2, second.Elements.Count);
        s.PerformUndo();
        Assert.Single(cache.GetLabel().Elements);
        Assert.Equal(3, cache.LabelBuildCount);
    }

    [Fact]
    public void RenderLabel_IsRebuilt_DuringAGestureToo()
    {
        var box = AppTestSupport.Box(10, 10);
        var s = AppTestSupport.Session(box);
        using var cache = new PreviewCache(s);
        _ = cache.GetLabel();
        var token = s.BeginGesture();
        s.Document.Elements[0].X = 200;
        s.Touch();
        Assert.Equal(200, cache.GetLabel().Elements[0].X);
        s.CancelGesture(token);
    }

    [Fact]
    public void Bitmap_IsCached_PerRevisionAndScale()
    {
        var s = AppTestSupport.Session(AppTestSupport.Box(10, 10, 200, 100));
        using var cache = new PreviewCache(s);
        var a = cache.GetBitmap(0.5, out var err);
        Assert.NotNull(a);
        Assert.Null(err);
        Assert.Same(a, cache.GetBitmap(0.5, out _));
        Assert.Equal(1, cache.BitmapBuildCount);

        var b = cache.GetBitmap(1.0, out _);
        Assert.NotNull(b);
        Assert.Equal(2, cache.BitmapBuildCount);
        Assert.Equal(800, b!.Bitmap.Width);

        s.Touch();
        cache.GetBitmap(1.0, out _);
        Assert.Equal(3, cache.BitmapBuildCount);
    }

    [Fact]
    public void Bitmap_ActuallyContainsTheDrawing()
    {
        var box = new BoxElement { X = 100, Y = 100, WidthDots = 200, HeightDots = 100, ThicknessDots = 100 };
        var s = AppTestSupport.Session(box);
        using var cache = new PreviewCache(s);
        var bmp = cache.GetBitmap(1.0, out _)!.Bitmap;
        Assert.Equal(0xFF000000u, (uint)bmp.GetPixel(150, 150));
        Assert.Equal(0xFFFFFFFFu, (uint)bmp.GetPixel(10, 10));
    }

    [Fact]
    public void Bitmap_TooLarge_OrInvalidSize_GivesAMessageKeyInsteadOfCrashing()
    {
        var doc = new LabelDocument { Width = Measure.Dots(9999), Height = Measure.Dots(32000) };
        using var cache = new PreviewCache(new EditorSession(doc));
        Assert.Null(cache.GetBitmap(1.0, out var key));
        Assert.Equal("Preview.TooLarge", key);

        var zero = new LabelDocument { Width = Measure.Dots(0), Height = Measure.Dots(10) };
        using var cache2 = new PreviewCache(new EditorSession(zero));
        Assert.Null(cache2.GetBitmap(1.0, out var key2));
        Assert.Equal("Preview.InvalidSize", key2);
    }

    // ------------------------------------------------------------------------------- settings

    [Fact]
    public void FirstRun_CreatesACommentedStarterFile_ThatLoadsBack()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "Rotula", "settings.json");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.True(File.Exists(path));
            Assert.StartsWith("//", File.ReadAllText(path));
            Assert.Equal("auto", settings.Language);
            Assert.False(store.LoadProblem);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("fr-CA")]
    [InlineData("es")]
    [InlineData("auto")]
    public void Settings_KeepTheLanguageAsSaved(string language)
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Assert.True(new JsonSettingsStore(path).Save(new AppSettings { Language = language }));
            Assert.Equal(language, new JsonSettingsStore(path).Load().Language);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Settings_RoundTrip_AndSurviveHandEditing()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new JsonSettingsStore(path);
            Assert.True(store.Save(new AppSettings { Language = "es", Unit = "Inches", RecentFiles = { @"C:\a.rotula" } }));
            var back = new JsonSettingsStore(path).Load();
            Assert.Equal("es", back.Language);
            Assert.Equal("Inches", back.Unit);
            Assert.Equal(new[] { @"C:\a.rotula" }, back.RecentFiles);

            // Comments and trailing commas are fine in a hand-edited file.
            File.WriteAllText(path, "// my notes\n{ \"language\": \"en\", }");
            Assert.Equal("en", new JsonSettingsStore(path).Load().Language);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DamagedSettings_FallBackToDefaults_AndSayWhy()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ this is broken");
            var store = new JsonSettingsStore(path);
            var settings = store.Load();
            Assert.Equal("auto", settings.Language);
            Assert.True(store.LoadProblem);
        }
        finally { Directory.Delete(dir, true); }
    }
}
