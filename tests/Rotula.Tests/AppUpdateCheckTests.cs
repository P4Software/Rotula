using System.Net;
using System.Net.Http;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.App.ViewModels;
using Rotula.Files.Policy;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>The release feed of the tests: counts the calls and answers what the test says.</summary>
internal sealed class FakeReleaseFeed : IReleaseFeed
{
    public int Calls;
    public ReleaseInfo? Answer;
    public Exception? Failure;

    public static ReleaseInfo Release(string version)
    {
        Assert.True(SemanticVersion.TryParse(version, out var v));
        return new ReleaseInfo(v, $"{GitHubRelease.ReleasesPageUrl}/tag/v{v}");
    }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Answer) : Task.FromException<ReleaseInfo?>(Failure);
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The version compare, the daily schedule, the answer parser and the checker: all UI-free.</summary>
public class UpdateCheckLogicTests
{
    private static SemanticVersion V(string s)
    {
        Assert.True(SemanticVersion.TryParse(s, out var v), s);
        return v;
    }

    [Theory]
    [InlineData("0.3.5", "0.3.6")]
    [InlineData("0.3.5", "0.4.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("0.3.9", "0.3.10")]          // numeric, not alphabetical
    [InlineData("1.0.0-rc1", "1.0.0")]       // a release is newer than its pre-release
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    public void Older_IsBelowNewer(string older, string newer)
    {
        Assert.True(V(older).CompareTo(V(newer)) < 0);
        Assert.True(V(newer).CompareTo(V(older)) > 0);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.7", "1.2.3")]
    [InlineData("0.3.5+8ee3037d", "0.3.5")]  // the informational version of a local build
    public void PrefixAndBuildMetadata_AreIgnored(string a, string b)
    {
        Assert.Equal(0, V(a).CompareTo(V(b)));
        Assert.Equal(V(b).ToString(), V(a).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("latest")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3-<script>")]
    [InlineData("99999999999.0.0")]
    public void StrangeTags_AreNotVersions(string? text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Schedule_AtMostOnceADay()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdateSchedule.IsDue(null, now));
        Assert.False(UpdateSchedule.IsDue(now.AddHours(-1), now));
        Assert.False(UpdateSchedule.IsDue(now.AddHours(-23.9), now));
        Assert.True(UpdateSchedule.IsDue(now.AddDays(-1), now));
        Assert.True(UpdateSchedule.IsDue(now.AddDays(-30), now));
        // A clock set back must not stop the check for weeks.
        Assert.True(UpdateSchedule.IsDue(now.AddDays(10), now));
        Assert.False(UpdateSchedule.IsDue(now.AddMinutes(2), now));
    }

    [Fact]
    public void Parse_ReadsTheTag_AndBuildsItsOwnLink()
    {
        var r = GitHubRelease.Parse("""
            {"tag_name":"v0.4.0","name":"Rotula 0.4.0","draft":false,"prerelease":false,
             "html_url":"https://evil.example/phish","assets":[{"name":"x"}]}
            """);
        Assert.NotNull(r);
        Assert.Equal("0.4.0", r!.Version.ToString());
        Assert.Equal("https://github.com/P4Software/Rotula/releases/tag/v0.4.0", r.PageUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.5.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.5.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"v0.5.0-rc1"}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("""{"tag_name":5}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Parse_RefusesWhatIsNotAPublishedRelease(string json) => Assert.Null(GitHubRelease.Parse(json));

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Checker_AsksNothingWhenOffOrNotDue()
    {
        var feed = new FakeReleaseFeed { Answer = FakeReleaseFeed.Release("9.0.0") };
        var checker = new UpdateChecker(feed, "0.3.5");
        Assert.Equal(UpdateCheckStatus.Off, (await checker.CheckAsync(false, null, Now)).Status);
        Assert.Equal(UpdateCheckStatus.NotDue, (await checker.CheckAsync(true, Now.AddHours(-2), Now)).Status);
        Assert.Equal(0, feed.Calls);
    }

    [Fact]
    public async Task Checker_ComparesWithTheRunningVersion()
    {
        var feed = new FakeReleaseFeed { Answer = FakeReleaseFeed.Release("0.4.0") };
        var newer = await new UpdateChecker(feed, "0.3.5+abc").CheckAsync(true, null, Now);
        Assert.Equal(UpdateCheckStatus.Available, newer.Status);
        Assert.Equal("0.4.0", newer.Release!.Version.ToString());
        Assert.True(newer.Asked);

        Assert.Equal(UpdateCheckStatus.UpToDate, (await new UpdateChecker(feed, "0.4.0").CheckAsync(true, null, Now)).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await new UpdateChecker(feed, "0.5.0-rc1").CheckAsync(true, null, Now)).Status);
        // A pre-release of the published version is older than the release itself.
        Assert.Equal(UpdateCheckStatus.Available, (await new UpdateChecker(feed, "0.4.0-rc2").CheckAsync(true, null, Now)).Status);
    }

    [Fact]
    public async Task Checker_FailuresAreQuiet()
    {
        var feed = new FakeReleaseFeed { Failure = new HttpRequestException("offline") };
        var r = await new UpdateChecker(feed, "0.3.5").CheckAsync(true, null, Now);
        Assert.Equal(UpdateCheckStatus.Failed, r.Status);
        Assert.True(r.Asked);

        feed.Failure = new TaskCanceledException("timeout");
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateChecker(feed, "0.3.5").CheckAsync(true, null, Now)).Status);

        var unknown = new FakeReleaseFeed { Answer = null };
        Assert.Equal(UpdateCheckStatus.Failed, (await new UpdateChecker(unknown, "0.3.5").CheckAsync(true, null, Now)).Status);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Feed_SendsOneAnonymousGet_ToTheLatestReleaseApi()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"tag_name":"v0.4.0"}""");
        using var feed = new GitHubReleaseFeed(handler);
        var r = await feed.GetLatestAsync(CancellationToken.None);
        Assert.Equal("0.4.0", r!.Version.ToString());

        var req = handler.Request!;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.github.com/repos/P4Software/Rotula/releases/latest", req.RequestUri!.AbsoluteUri);
        Assert.Null(req.Content);
        // Only what GitHub requires: a product name without a version, and the Accept header. Nothing that identifies anyone.
        Assert.Equal("Rotula", req.Headers.UserAgent.ToString());
        Assert.Equal(new[] { "Accept", "User-Agent" }, req.Headers.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Feed_AnErrorAnswerIsAFailure()
    {
        using var feed = new GitHubReleaseFeed(new RecordingHandler(HttpStatusCode.Forbidden, "rate limited"));
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetLatestAsync(CancellationToken.None));
        var r = await new UpdateChecker(feed, "0.3.5").CheckAsync(true, null, Now);
        Assert.Equal(UpdateCheckStatus.Failed, r.Status);
    }
}

/// <summary>The update check and the managed settings as the main window sees them.</summary>
[Collection("App")]
public class AppDeploymentTests : IDisposable
{
    private readonly FakeServices _fake = new();
    private readonly FakeReleaseFeed _feed = new() { Answer = FakeReleaseFeed.Release("0.9.0") };
    private readonly ManualClock _clock = new();
    private readonly List<string> _opened = new();

    public AppDeploymentTests() => Loc.Instance.SetLanguage("en");

    public void Dispose() => Loc.Instance.SetLanguage("en");

    private sealed class Links(List<string> opened, bool works) : IUrlLauncher
    {
        public bool Open(string url) { opened.Add(url); return works; }
    }

    private MainViewModel Main(RotulaPolicy? policy = null, bool linksWork = true) =>
        new(_fake.Build() with
        {
            Policy = policy,
            Updates = new UpdateChecker(_feed, "0.3.5"),
            Links = new Links(_opened, linksWork),
            Clock = _clock,
        });

    [Fact]
    public async Task UpdateCheck_IsOffByDefault_AndSendsNothing()
    {
        var vm = Main();
        Assert.False(vm.UpdateCheckEnabled);
        Assert.True(vm.CanChangeUpdateCheck);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(0, _feed.Calls);
        Assert.Null(vm.UpdateNotice);
        Assert.Null(_fake.Settings.Current.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task SwitchingItOn_ChecksOnce_ThenAtMostDaily()
    {
        var vm = Main();
        vm.UpdateCheckEnabled = true;                    // starts the first check at once
        await vm.CheckForUpdatesAsync();                 // same moment: not due again
        Assert.True(_fake.Settings.Current.UpdateCheckEnabled);
        Assert.Equal(1, _feed.Calls);
        Assert.Equal(_clock.Now, _fake.Settings.Current.LastUpdateCheckUtc);
        Assert.NotNull(vm.UpdateNotice);
        Assert.Equal("Rotula 0.9.0 is available. You can download it from the release page.", vm.UpdateNotice!.NoticeText);

        _clock.Now = _clock.Now.AddHours(20);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(1, _feed.Calls);
        _clock.Now = _clock.Now.AddHours(5);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, _feed.Calls);
    }

    [Fact]
    public async Task TheNoticeLinksToTheReleasePage_AndClosingItRemembersTheVersion()
    {
        _fake.Settings.Current.UpdateCheckEnabled = true;
        var vm = Main();
        await vm.CheckForUpdatesAsync();
        var notice = vm.UpdateNotice!;
        notice.OpenReleaseCommand.Execute(null);
        Assert.Equal("https://github.com/P4Software/Rotula/releases/tag/v0.9.0", Assert.Single(_opened));

        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsVisible);
        Assert.Equal("0.9.0", _fake.Settings.Current.UpdateDismissedVersion);

        // The next day the same version is not announced again; a newer one is.
        var again = Main();
        _clock.Now = _clock.Now.AddDays(1);
        await again.CheckForUpdatesAsync();
        Assert.Null(again.UpdateNotice);
        _feed.Answer = FakeReleaseFeed.Release("0.9.1");
        _clock.Now = _clock.Now.AddDays(1);
        await again.CheckForUpdatesAsync();
        Assert.Equal("0.9.1", again.UpdateNotice!.Release.Version.ToString());
    }

    [Fact]
    public async Task ABrowserThatDoesNotOpen_IsExplainedInTheStatusBar()
    {
        _fake.Settings.Current.UpdateCheckEnabled = true;
        var vm = Main(linksWork: false);
        await vm.CheckForUpdatesAsync();
        vm.UpdateNotice!.OpenReleaseCommand.Execute(null);
        Assert.Contains("https://github.com/P4Software/Rotula/releases/tag/v0.9.0", vm.StatusMessage);
    }

    [Fact]
    public async Task UpToDateOrOffline_ShowsNothing()
    {
        _fake.Settings.Current.UpdateCheckEnabled = true;
        _feed.Answer = FakeReleaseFeed.Release("0.3.5");
        var vm = Main();
        await vm.CheckForUpdatesAsync();
        Assert.Null(vm.UpdateNotice);

        _feed.Failure = new HttpRequestException("offline");
        _clock.Now = _clock.Now.AddDays(1);
        await vm.CheckForUpdatesAsync();
        Assert.Null(vm.UpdateNotice);
        Assert.Equal(2, _feed.Calls);
        Assert.Empty(_fake.Errors);
    }

    [Fact]
    public async Task APolicyCanForceTheCheckOff_EvenWhenTheUserSwitchedItOn()
    {
        _fake.Settings.Current.UpdateCheckEnabled = true;
        var vm = Main(new RotulaPolicy { UpdateCheck = false });
        Assert.False(vm.UpdateCheckEnabled);
        Assert.True(vm.IsUpdateCheckManaged);
        Assert.False(vm.CanChangeUpdateCheck);
        vm.UpdateCheckEnabled = true;
        await vm.CheckForUpdatesAsync();
        Assert.Equal(0, _feed.Calls);
        // The user's own choice is kept for the day the policy goes away.
        Assert.True(_fake.Settings.Current.UpdateCheckEnabled);
    }

    [Fact]
    public async Task APolicyCanSwitchTheCheckOn()
    {
        var vm = Main(new RotulaPolicy { UpdateCheck = true });
        Assert.True(vm.UpdateCheckEnabled);
        vm.UpdateCheckEnabled = false;                   // locked: ignored
        Assert.True(vm.UpdateCheckEnabled);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(1, _feed.Calls);
        Assert.False(_fake.Settings.Current.UpdateCheckEnabled);
    }

    [Fact]
    public void WithoutAnUpdateService_TheMenuEntryIsDisabled()
    {
        var vm = new MainViewModel(_fake.Build());
        Assert.False(vm.CanChangeUpdateCheck);
    }

    [Fact]
    public void AManagedLanguage_IsAppliedAndLocked()
    {
        _fake.Settings.Current.Language = "en";
        var vm = Main(new RotulaPolicy { Language = "es" });
        Assert.Equal("es", Loc.Instance.LanguageCode);
        Assert.Equal("es", vm.LanguageSetting);
        Assert.True(vm.IsLanguageEs);
        Assert.True(vm.IsLanguageManaged);
        Assert.False(vm.CanChooseLanguage);
        Assert.False(vm.SetLanguageCommand.CanExecute("en"));
        vm.SetLanguageCommand.Execute("en");
        Assert.Equal("es", Loc.Instance.LanguageCode);
        Assert.Equal("en", _fake.Settings.Current.Language);
        Assert.Equal("Administrado por tu organización", Loc.Get("Policy.Managed"));
    }

    [Theory]
    [InlineData("pt-BR", "Gerenciado pela sua organização")]
    [InlineData("fr-CA", "Géré par votre organisation")]
    public void AManagedPortugueseOrFrench_IsAppliedAndLocksEveryLanguageEntry(string language, string managed)
    {
        _fake.Settings.Current.Language = "es";
        var vm = Main(new RotulaPolicy { Language = language });
        Assert.Equal(language, Loc.Instance.LanguageCode);
        Assert.Equal(language, vm.LanguageSetting);
        Assert.Equal(language == "pt-BR", vm.IsLanguagePtBr);
        Assert.Equal(language == "fr-CA", vm.IsLanguageFrCa);
        Assert.False(vm.IsLanguageEs);
        // The four language entries and "Same as Windows" share one command, so all of them are disabled.
        foreach (var code in new[] { "auto" }.Concat(Loc.SupportedLanguages))
            Assert.False(vm.SetLanguageCommand.CanExecute(code), code);
        vm.SetLanguageCommand.Execute("en");
        Assert.Equal(language, Loc.Instance.LanguageCode);
        Assert.Equal("es", _fake.Settings.Current.Language); // the user's own choice waits for the policy to go
        Assert.Equal(managed, Loc.Get("Policy.Managed"));
    }

    [Fact]
    public void AnUnmanagedLanguage_StaysTheUsersChoice()
    {
        var vm = Main();
        Assert.True(vm.CanChooseLanguage);
        Assert.True(vm.SetLanguageCommand.CanExecute("es"));
        vm.SetLanguageCommand.Execute("es");
        Assert.Equal("es", _fake.Settings.Current.Language);
    }

    [Fact]
    public async Task ThePolicyPrinter_IsPreselectedUntilTheUserChoosesOne()
    {
        _fake.Printer.Printers = new[]
        {
            new PrinterInfo("Office laser", "USB001", "Driver", IsDefault: true, IsOffline: false),
            new PrinterInfo("Warehouse ZD420", "USB002", "Driver", IsDefault: false, IsOffline: false),
        };
        var vm = Main(new RotulaPolicy { DefaultPrinter = "Warehouse ZD420" });
        await vm.Printer.RefreshPrintersAsync();
        Assert.Equal("Warehouse ZD420", vm.Printer.SelectedPrinterName);

        // A printer the user picked before wins over the suggestion.
        _fake.Settings.Current.Print = new PrintSettings { Configured = true, PrinterName = "Office laser" };
        var chosen = Main(new RotulaPolicy { DefaultPrinter = "Warehouse ZD420" });
        await chosen.Printer.RefreshPrintersAsync();
        Assert.Equal("Office laser", chosen.Printer.SelectedPrinterName);
    }

    [Fact]
    public async Task APolicyPrinterThatIsNotInstalled_FallsBackQuietly()
    {
        _fake.Printer.Printers = new[] { new PrinterInfo("Office laser", "USB001", "Driver", IsDefault: true, IsOffline: false) };
        var vm = Main(new RotulaPolicy { DefaultPrinter = "Not here" });
        await vm.Printer.RefreshPrintersAsync();
        Assert.Equal("Office laser", vm.Printer.SelectedPrinterName);
        Assert.Equal(string.Empty, vm.Printer.PrintersNote);
    }
}
