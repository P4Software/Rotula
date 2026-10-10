using System.Text.Json;
using Rotula.Files;
using Rotula.App.Localization;
using Rotula.App.ViewModels;

namespace Rotula.Tests;

/// <summary>
/// Paths stored in a .rotula file or in settings.json are untrusted: a crafted file must not make Rotula touch a network
/// share (which would send the user's Windows credentials to the attacker) just by being opened.
/// </summary>
[Collection("App")]
public class AppUntrustedPathTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly string _dir = AppTestSupport.TempDir();

    public AppUntrustedPathTests() => _fake.Settings.Current.Language = "en";

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(@"\\attacker\share\x.csv")]
    [InlineData(@"//attacker/share/x.csv")]
    [InlineData(@"\\?\C:\Data\x.csv")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\UNC\attacker\share\x.csv")]
    [InlineData("http://example.com/x.csv")]
    [InlineData("https://example.com/x.csv")]
    [InlineData("file:///C:/Data/x.csv")]
    [InlineData("file://attacker/share/x.csv")]
    [InlineData(@"data\x.csv")]
    [InlineData("x.csv")]
    [InlineData(@"..\..\x.csv")]
    [InlineData(@"\x.csv")]
    [InlineData("C:x.csv")]
    [InlineData(@"C:\Data\x.csv:hidden")]
    [InlineData(@"C:\Data\*.csv")]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeLocalPath_RejectsEverythingThatIsNotAPlainLocalDrivePath(string path)
    {
        Assert.Null(SafeLocalPath.TryGetLocalPath(path));
    }

    [Theory]
    [InlineData(@"C:\Data\labels.csv")]
    [InlineData("D:/Data/labels.xlsx")]
    [InlineData(@"c:\a\..\b\x.csv")]
    public void SafeLocalPath_AcceptsAFullyQualifiedDrivePath(string path)
    {
        string? result = SafeLocalPath.TryGetLocalPath(path);
        Assert.NotNull(result);
        Assert.True(Path.IsPathFullyQualified(result));
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void SafeLocalPath_NullIsRejected() => Assert.Null(SafeLocalPath.TryGetLocalPath(null));

    private static MergeDto Dto(string? source) => new() { SourcePath = source, Delimiter = "auto" };

    [Theory]
    [InlineData(@"\\attacker\share\x.csv")]
    [InlineData(@"\\?\C:\x.csv")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"relative\x.csv")]
    [InlineData("http://example.com/x.csv")]
    public void TheDtoMapper_DiscardsAnUntrustedSourcePath_AndRemembersThatItDid(string path)
    {
        var settings = MergeSettingsMapper.FromDto(Dto(path));
        Assert.Null(settings.SourcePath);
        Assert.True(settings.SourcePathIgnored);
    }

    [Fact]
    public void TheDtoMapper_KeepsAValidLocalPath()
    {
        var settings = MergeSettingsMapper.FromDto(Dto(@"C:\Data\labels.csv"));
        Assert.Equal(@"C:\Data\labels.csv", settings.SourcePath);
        Assert.False(settings.SourcePathIgnored);
    }

    [Fact]
    public void TheDtoMapper_NoPath_IsNotReportedAsIgnored()
    {
        var settings = MergeSettingsMapper.FromDto(Dto(null));
        Assert.Null(settings.SourcePath);
        Assert.False(settings.SourcePathIgnored);
    }

    private string CraftedLabel(string sourcePath)
    {
        string json = RotulaFileSerializer.Serialize(new Rotula.Core.Model.LabelDocument(), new DataMergeSettings { SourcePath = @"C:\placeholder.csv" })
            .Replace("C:\\\\placeholder.csv", sourcePath.Replace("\\", "\\\\"));
        string file = Path.Combine(_dir, "crafted.rotula");
        File.WriteAllText(file, json);
        Assert.Contains(sourcePath.Replace("\\", "\\\\"), json);
        return file;
    }

    [Fact]
    public void OpeningACraftedLabel_NeverKeepsTheNetworkPath_AndTellsTheUser()
    {
        var vm = new MainViewModel(_fake.Build());
        Assert.True(vm.LoadFile(CraftedLabel(@"\\attacker\share\x.csv")));
        Assert.Null(vm.Data.SourcePath);
        Assert.False(vm.Data.CanReload);
        Assert.True(vm.Data.SourceHintIgnored);
        Assert.False(vm.Data.ShowReloadHint);
        Assert.Contains("ignored", Loc.Get("Data.Hint.Ignored"));
    }

    [Fact]
    public void OpeningALabelWithALocalHint_DoesNotLookAtTheFileSystem()
    {
        // The hint points to a file that does not exist. If opening the label probed it, CanReload would be false;
        // it is true because nothing is checked until the user clicks Reload.
        var vm = new MainViewModel(_fake.Build());
        Assert.True(vm.LoadFile(CraftedLabel(@"C:\definitely\not\here\x.csv")));
        Assert.Equal(@"C:\definitely\not\here\x.csv", vm.Data.SourcePath);
        Assert.True(vm.Data.CanReload);
        Assert.True(vm.Data.ShowReloadHint);
        Assert.False(vm.Data.HasData);
        Assert.Equal(string.Empty, vm.Data.ResultText);
    }

    [Fact]
    public void TheViewModelSource_ProbesTheFileSystemOnlyInsideReload()
    {
        string root = AppRoot();
        string vmSource = File.ReadAllText(Path.Combine(root, "ViewModels", "DataMergeViewModel.cs"));
        string mapper = File.ReadAllText(Path.Combine(FilesRoot(), "DataMergeSettings.cs"));
        string serializer = File.ReadAllText(Path.Combine(FilesRoot(), "RotulaFileSerializer.cs"));

        Assert.DoesNotContain("File.Exists", mapper);
        Assert.DoesNotContain("File.Exists", serializer);
        // The single existence check in the view-model sits in the Reload command, after the user clicked it.
        int reload = vmSource.IndexOf("private async Task ReloadAsync", StringComparison.Ordinal);
        int at = vmSource.IndexOf("File.Exists", StringComparison.Ordinal);
        Assert.True(reload >= 0 && at > reload);
        // The remembered path is only ever checked as "local", inside the Reload command, after the click.
        Assert.Contains("File.Exists(local)", vmSource[reload..]);
        Assert.DoesNotContain("File.Exists(SourcePath", vmSource);
        int canReload = vmSource.IndexOf("public bool CanReload", StringComparison.Ordinal);
        Assert.DoesNotContain("File.", vmSource[canReload..vmSource.IndexOf('\n', canReload)]);
    }

    private static string FilesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Rotula.Files");
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Rotula.App");
    }

    [Fact]
    public async Task Reload_RefusesAHintThatBecameNonLocal()
    {
        var vm = new MainViewModel(_fake.Build());
        vm.Data.Apply(new DataMergeSettings { SourcePath = @"C:\ok\x.csv" });
        // Simulate a hint that was set to something unsafe after the fact: it must be refused at click time too.
        typeof(DataMergeViewModel).GetProperty(nameof(DataMergeViewModel.SourcePath))!.SetValue(vm.Data, @"\\attacker\share\x.csv");
        await vm.Data.ReloadCommand.ExecuteAsync(null);
        Assert.True(vm.Data.ResultIsError);
        Assert.Contains("not on a local disk", vm.Data.ResultText);
        Assert.False(vm.Data.HasData);
    }

    [Fact]
    public void TheRecentFilesList_DropsUntrustedPathsFromSettings()
    {
        _fake.Settings.Current.RecentFiles = new List<string>
        {
            @"\\attacker\share\a.rotula", @"C:\labels\b.rotula", @"\\?\C:\c.rotula", "relative.rotula", "https://x/y.rotula", @"C:\labels\b.rotula",
        };
        var vm = new MainViewModel(_fake.Build());
        Assert.Equal(new[] { @"C:\labels\b.rotula" }, vm.RecentFiles.Select(r => r.Path));
    }

    [Fact]
    public void ANewerOrOlderFileWithoutMerge_StillLoads()
    {
        var content = RotulaFileSerializer.DeserializeFile("{\"formatVersion\":1,\"label\":{},\"elements\":[]}");
        Assert.Null(content.Merge);
        var withMerge = RotulaFileSerializer.DeserializeFile("{\"formatVersion\":2,\"label\":{},\"elements\":[],\"merge\":{\"maxRecordsPerJob\":5,\"copiesMode\":\"bogus\"}}");
        Assert.Equal(5, withMerge.Merge!.MaxRecordsPerJob);
        Assert.Equal(Rotula.Core.Merge.CopiesMode.PrintQuantity, withMerge.Merge.CopiesMode); // unknown values fall back to defaults
        var tooNew = Assert.Throws<RotulaFileException>(() => RotulaFileSerializer.Deserialize("{\"formatVersion\":" + (RotulaFileSerializer.CurrentFormatVersion + 1) + "}"));
        Assert.Equal("File.Error.NewerVersion", tooNew.MessageKey);
        _ = JsonDocument.Parse("{}");
    }
}
