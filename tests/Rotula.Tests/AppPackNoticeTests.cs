using Rotula.App.Localization;
using Rotula.App.ViewModels;
using Rotula.Data;
using Rotula.Data.Packs;

namespace Rotula.Tests;

/// <summary>Scriptable dialogs of the add-on notice.</summary>
internal sealed class FakePackDialogs : IPackDialogService
{
    public bool Approve;
    public int DetailsShown, Prompts;
    public PackTrustPrompt? LastPrompt;
    public void ShowDetails(PackNoticeViewModel viewModel) => DetailsShown++;
    public bool ShowTrustPrompt(PackTrustPrompt prompt) { Prompts++; LastPrompt = prompt; return Approve; }
}

/// <summary>
/// The start-up loading of data source add-ons: it never blocks or breaks the application, tells the user in plain language about what could
/// not be loaded, and asks for an explicit approval before a pack from the user's own folder runs.
/// </summary>
[Collection("App")]
public class AppPackNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotula-pack-ui-" + Guid.NewGuid().ToString("N"));
    private static string SampleSource => Path.Combine(AppContext.BaseDirectory, "testpacks", "sample");

    public AppPackNoticeTests()
    {
        Loc.Instance.SetLanguage("en");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Loc.Instance.SetLanguage("en");
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(from)) Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private PackLoadOptions Options() => new()
    {
        Locations = new PackLocations(Path.Combine(_root, "bundled"), Path.Combine(_root, "user")),
        TrustStore = new PackTrustStore(Path.Combine(_root, "trusted.json")),
        Registry = new ProviderRegistry(),
    };

    // ------------------------------------------------------------------------------------ start-up loading

    [Fact]
    public async Task ALoaderThatThrows_NeverStopsTheApplication_ItIsLogged_AndReadyCompletes()
    {
        Exception? logged = null;
        var report = await PackStartup.Begin(Options(), ex => logged = ex, _ => throw new InvalidOperationException("boom"));
        Assert.Empty(report.Warnings);
        Assert.Empty(report.Loaded);
        Assert.IsType<InvalidOperationException>(logged);
        await PackStartup.Ready;                                   // completes, so the connection manager never waits forever
        Assert.True(PackStartup.Ready.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LoadingRunsOffTheCallingThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int loaderThread = -1;
        await PackStartup.Begin(Options(), null, o => { loaderThread = Environment.CurrentManagedThreadId; return new PackLoadReport(); });
        Assert.NotEqual(callerThread, loaderThread);
    }

    // ------------------------------------------------------------------------------------ the notice

    [Fact]
    public void WithNothingToReport_TheNoticeStaysHidden()
    {
        var notice = new PackNoticeViewModel(new PackLoadReport(), Options(), new FakePackDialogs());
        Assert.False(notice.IsVisible);
    }

    [Fact]
    public void ABrokenPack_ProducesAPlainNoticeWithDetails_AndNoKeyIsEverShown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bundled", "broken"));
        File.WriteAllText(Path.Combine(_root, "bundled", "broken", "pack.json"), "{ not json");
        var options = Options();
        var report = PackLoader.LoadAll(options);
        var dialogs = new FakePackDialogs();
        var notice = new PackNoticeViewModel(report, options, dialogs);

        Assert.True(notice.IsVisible);
        Assert.Equal("Some data source add-ons could not be loaded.", notice.NoticeText);
        var item = Assert.Single(notice.Items);
        Assert.Contains("\"broken\"", item.Text);
        Assert.Contains("pack.json", item.Text);
        Assert.DoesNotContain("Data.Pack", item.Text);
        Assert.False(item.CanReview);
        notice.ShowDetailsCommand.Execute(null);
        Assert.Equal(1, dialogs.DetailsShown);
        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsVisible);
        Assert.Single(notice.Items);                               // the details stay available
    }

    [Fact]
    public void TheNoticeAndItsTexts_FollowTheLanguage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bundled", "broken"));
        File.WriteAllText(Path.Combine(_root, "bundled", "broken", "pack.json"), "{ not json");
        var options = Options();
        var notice = new PackNoticeViewModel(PackLoader.LoadAll(options), options, new FakePackDialogs());
        Loc.Instance.SetLanguage("es");
        Assert.Equal("Algunos complementos de orígenes de datos no se pudieron cargar.", notice.NoticeText);
        Assert.Contains("complemento", notice.Items[0].Text);
        Assert.Contains("pack.json", notice.Items[0].Text);
    }

    [Fact]
    public void AWarningWhoseKeyHasNoText_FallsBackToTheEnglishSentence()
    {
        var w = new PackWarning("Data.Pack.FromTheFuture", "demo", new[] { "demo" });
        Assert.Equal(w.EnglishText, PackMessages.Describe(w));
        // Fewer arguments than the text needs must not throw either.
        var few = new PackWarning(PackKeys.NeedsNewerRotula, "demo", Array.Empty<string>());
        Assert.DoesNotContain("{", PackMessages.Describe(few));
    }

    // ------------------------------------------------------------------------------------ approval of a user pack

    private (PackLoadOptions Options, PackNoticeViewModel Notice, FakePackDialogs Dialogs) UserPackNotice()
    {
        Copy(SampleSource, Path.Combine(_root, "user", "sample"));
        var options = Options();
        var report = PackLoader.LoadAll(options);
        var dialogs = new FakePackDialogs();
        return (options, new PackNoticeViewModel(report, options, dialogs), dialogs);
    }

    [Fact]
    public void APackInTheUsersOwnFolder_IsNotLoaded_AndWaitsForApproval()
    {
        var (options, notice, _) = UserPackNotice();
        Assert.DoesNotContain("sample", options.Registry.PackIds);
        Assert.Equal("An add-on is waiting for your approval.", notice.NoticeText);
        var item = Assert.Single(notice.Items);
        Assert.True(item.CanReview);
        Assert.Contains("NOT loaded", item.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("program", item.Text);
    }

    [Fact]
    public void TheTrustPrompt_SaysThatAnAddOnIsAProgram_AndShowsPublisherVersionAndFingerprint_InBothLanguages()
    {
        var (_, notice, dialogs) = UserPackNotice();
        notice.ReviewCommand.Execute(notice.Items[0]);
        var prompt = dialogs.LastPrompt!;
        string en = PackMessages.DescribeTrust(prompt);
        Assert.Contains("is a program", en);
        Assert.Contains(prompt.Candidate.Folder, en);
        Assert.Equal(12, prompt.ShortFingerprint.Length);
        Assert.NotNull(prompt.Candidate.Manifest!.Version);
        Loc.Instance.SetLanguage("es");
        string es = PackMessages.DescribeTrust(prompt);
        Assert.Contains("es un programa", es);
        Assert.DoesNotContain("{", es);
    }

    [Fact]
    public void Skipping_LoadsNothingAndRemembersNothing_SoItAsksAgainNextTime()
    {
        var (options, notice, dialogs) = UserPackNotice();
        dialogs.Approve = false;
        notice.ReviewCommand.Execute(notice.Items[0]);
        Assert.Equal(1, dialogs.Prompts);
        Assert.DoesNotContain("sample", options.Registry.PackIds);
        Assert.Empty(options.TrustStore.Load());
        Assert.Single(notice.Items);
        Assert.True(notice.IsVisible);
        // The next start finds the same pack waiting again.
        var again = PackLoader.LoadAll(Options());
        Assert.Contains(again.Warnings, w => w.Key == PackKeys.UserPackNeedsApproval);
    }

    [Fact]
    public void Approving_RecordsTheFingerprint_LoadsThePackAtOnce_AndClearsTheNotice()
    {
        var (options, notice, dialogs) = UserPackNotice();
        dialogs.Approve = true;
        notice.ReviewCommand.Execute(notice.Items[0]);
        Assert.Contains("sample", options.Registry.PackIds);
        Assert.NotNull(options.Registry.TryGet("sample-memory"));
        Assert.Single(options.TrustStore.Load());
        Assert.Empty(notice.Items);
        Assert.False(notice.IsVisible);
        Assert.Contains("sample", notice.StatusText);
    }

    [Fact]
    public void APackThatChangedAfterApproval_IsReportedAsChanged_NotAsNew()
    {
        var (options, notice, dialogs) = UserPackNotice();
        dialogs.Approve = true;
        notice.ReviewCommand.Execute(notice.Items[0]);
        File.AppendAllText(Path.Combine(_root, "user", "sample", "NOTICES.txt"), " changed");
        var report = PackLoader.LoadAll(new PackLoadOptions { Locations = options.Locations, TrustStore = options.TrustStore, Registry = new ProviderRegistry() });
        var w = Assert.Single(report.Warnings);
        Assert.Equal(PackKeys.UserPackChanged, w.Key);
        Assert.Contains("changed since you approved it", PackMessages.Describe(w));
    }
}
