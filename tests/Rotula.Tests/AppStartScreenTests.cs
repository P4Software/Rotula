using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.App.Views;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>Tests of the start screen, the status bar message flag and the bundled third-party notices.</summary>
[Collection("App")]
public class AppStartScreenTests : IDisposable
{
    private readonly FakeServices _fake = new();

    public AppStartScreenTests() => _fake.Settings.Current.Language = "en";

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private MainViewModel Create() => new(_fake.Build());

    [Fact]
    public void StartScreen_IsShownAtStart_AndCancelledDialogsKeepIt()
    {
        var vm = Create();
        Assert.True(vm.ShowStartScreen);

        _fake.NewLabel = null; // New label dialog cancelled
        vm.NewCommand.Execute(null);
        Assert.True(vm.ShowStartScreen);

        _fake.OpenPath = null; // Open file picker cancelled
        vm.OpenCommand.Execute(null);
        Assert.True(vm.ShowStartScreen);
    }

    [Fact]
    public void StartScreen_GoesAway_WhenALabelIsCreated()
    {
        var vm = Create();
        _fake.NewLabel = new LabelDocument { Width = Measure.Millimeters(100), Height = Measure.Millimeters(50) };
        vm.NewCommand.Execute(null);
        Assert.False(vm.ShowStartScreen);
    }

    [Fact]
    public void StartScreen_StaysWhenAFileFailsToLoad_AndGoesAwayWhenOneLoads()
    {
        string dir = AppTestSupport.TempDir();
        try
        {
            var vm = Create();
            string bad = Path.Combine(dir, "bad.rotula");
            File.WriteAllText(bad, "not json");
            Assert.False(vm.LoadFile(bad));
            Assert.True(vm.ShowStartScreen);
            Assert.Single(_fake.Errors);

            string good = Path.Combine(dir, "good.rotula");
            Rotula.App.Files.RotulaFileSerializer.Save(AppTestSupport.RichDocument(), good, null);
            Assert.True(vm.LoadFile(good));
            Assert.False(vm.ShowStartScreen);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StartScreen_GoesAway_OnTheFirstEdit_AndWithTheBlankLabelChoice()
    {
        var vm = Create();
        vm.AddElementCommand.Execute(Rotula.App.Editing.ToolboxKind.Text);
        Assert.False(vm.ShowStartScreen);

        var other = Create();
        other.DismissStartScreenCommand.Execute(null);
        Assert.False(other.ShowStartScreen);
    }

    [Fact]
    public void StatusBarDivider_FollowsTheMessage()
    {
        var vm = Create();
        Assert.False(vm.HasStatusMessage);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.StatusMessage = "Saved";
        Assert.True(vm.HasStatusMessage);
        Assert.Contains(nameof(MainViewModel.HasStatusMessage), raised);
        vm.StatusMessage = string.Empty;
        Assert.False(vm.HasStatusMessage);
    }

    [Fact]
    public void ThirdPartyNotices_AreEmbeddedInTheAssembly()
    {
        string? text = AboutWindow.ReadNotices();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("Third-party notices", text);
        Assert.Contains("SkiaSharp", text);
    }

    [Fact]
    public void AboutVersion_ComesFromTheAssembly()
    {
        string version = AboutWindow.VersionString();
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version);
    }
}
