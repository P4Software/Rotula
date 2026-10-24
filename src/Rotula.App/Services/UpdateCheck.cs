using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Rotula.App.Services;

// The opt-in update check. Rules (docs/decisions.md, "Update check"): off unless the user or an administrator switches it on;
// at most one anonymous GET of the public "latest release" page of the GitHub API a day; nothing about the user or the computer
// is sent; nothing is ever downloaded or installed. Everything except the HTTP call itself is plain code with tests.

/// <summary>A semantic version (major.minor.patch with an optional pre-release part), compared by the SemVer 2.0 rules.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _pre;

    private SemanticVersion(int major, int minor, int patch, string[] pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _pre = pre;
    }

    /// <summary>Major version.</summary>
    public int Major { get; }

    /// <summary>Minor version.</summary>
    public int Minor { get; }

    /// <summary>Patch version.</summary>
    public int Patch { get; }

    /// <summary>The pre-release part after "-" (for example "rc1"), or empty for a normal release.</summary>
    public string PreRelease => string.Join('.', _pre);

    /// <summary>True for a pre-release such as 0.4.0-rc1.</summary>
    public bool IsPreRelease => _pre.Length > 0;

    /// <summary>
    /// Reads "1.2.3", "v1.2.3", "1.2.3-rc.1" or "1.2.3+build" (build metadata is ignored, as SemVer says). Returns false for
    /// anything else, so a strange tag on the release page can never be taken for a newer version.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.Length > 64) return false;
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] pre = Array.Empty<string>();
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            s = s[..dash];
            if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return false;
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A normal release is newer than any pre-release of the same number.
        if (_pre.Length == 0 && other._pre.Length == 0) return 0;
        if (_pre.Length == 0) return 1;
        if (other._pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            c = ComparePart(_pre[i], other._pre[i]);
            if (c != 0) return c;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    private static int ComparePart(string a, string b)
    {
        bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
        if (an && bn)
        {
            // Compare digit strings without parsing, so a very long number cannot overflow.
            string ta = a.TrimStart('0'), tb = b.TrimStart('0');
            return ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
        }
        if (an) return -1; // numeric identifiers sort before alphanumeric ones
        if (bn) return 1;
        return string.CompareOrdinal(a, b);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <inheritdoc />
    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>When the next update check is due.</summary>
public static class UpdateSchedule
{
    /// <summary>At most one check per this interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// True when no check was made yet, or the last one is at least <see cref="Interval"/> ago. A last check in the future
    /// (the clock was set back) also counts as due, otherwise a wrong date could stop the check for a long time.
    /// </summary>
    public static bool IsDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc)
    {
        if (lastCheckUtc is not { } last) return true;
        if (last > nowUtc + TimeSpan.FromMinutes(5)) return true;
        return nowUtc - last >= Interval;
    }
}

/// <summary>The newest published release.</summary>
/// <param name="Version">Its version, read from the tag.</param>
/// <param name="PageUrl">The release page on GitHub (built by Rotula, not taken from the answer).</param>
public sealed record ReleaseInfo(SemanticVersion Version, string PageUrl);

/// <summary>Reads the answer of the GitHub "latest release" API.</summary>
public static class GitHubRelease
{
    /// <summary>The repository whose releases are checked.</summary>
    public const string Repository = "P4Software/Rotula";

    /// <summary>The only address the update check ever asks.</summary>
    public const string LatestApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>The list of releases, used when a release page cannot be named.</summary>
    public const string ReleasesPageUrl = "https://github.com/" + Repository + "/releases";

    /// <summary>
    /// Reads <c>tag_name</c> of the answer. Drafts and pre-releases are ignored (the API should never return them, but a wrong
    /// answer must not offer a test build). The page link is built from the parsed version, so the answer can never send the
    /// user to another site. Returns null when the answer is not understood.
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            if (!SemanticVersion.TryParse(tag.GetString(), out var version) || version.IsPreRelease) return null;
            return new ReleaseInfo(version, $"{ReleasesPageUrl}/tag/v{version}");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTrue(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Asks where the newest release is. Production: <see cref="GitHubReleaseFeed"/>; tests use a fake.</summary>
public interface IReleaseFeed
{
    /// <summary>The newest release, or null when the answer was not understood. Network failures throw <see cref="HttpRequestException"/>.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One anonymous GET of <see cref="GitHubRelease.LatestApiUrl"/>. The request carries only what GitHub requires (a product name
/// as User-Agent, without the version) and an Accept header: no cookies, no identifiers. The Windows proxy settings are used, with
/// the signed-in user's credentials for a proxy that asks for them, so it works behind a company proxy like the browser does.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    /// <summary>Longest answer read (the real one is a few kilobytes).</summary>
    public const int MaxAnswerBytes = 512 * 1024;

    private readonly HttpClient _http;

    /// <summary>Creates the feed. <paramref name="handler"/> is for tests; null uses the system proxy settings.</summary>
    public GitHubReleaseFeed(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = true,
            // Default proxy = the Windows (WinINET / WinHTTP) settings; the credentials are for proxies that need sign-in.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = MaxAnswerBytes };
    }

    /// <inheritdoc />
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubRelease.LatestApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Rotula", null));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The release page answered {(int)response.StatusCode}.", null, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return GitHubRelease.Parse(json);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}

/// <summary>What one update check found.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The check is switched off; nothing was sent.</summary>
    Off,
    /// <summary>The last check is less than a day old; nothing was sent.</summary>
    NotDue,
    /// <summary>This is the newest version.</summary>
    UpToDate,
    /// <summary>A newer version exists.</summary>
    Available,
    /// <summary>No answer (offline, blocked, a wrong answer). Silent: it is tried again the next day.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="UpdateChecker.CheckAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Release">The newer release when <paramref name="Status"/> is <see cref="UpdateCheckStatus.Available"/>.</param>
/// <param name="Asked">True when the release page was asked, so the caller records the time of the check.</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, bool Asked = false);

/// <summary>Decides whether to ask, asks, and compares the answer with the running version.</summary>
public sealed class UpdateChecker
{
    private readonly IReleaseFeed _feed;
    private readonly SemanticVersion? _current;

    /// <summary>Creates the checker.</summary>
    /// <param name="feed">Where the newest release is read.</param>
    /// <param name="currentVersion">The running version, for example "0.3.5" (build metadata after "+" is ignored).</param>
    public UpdateChecker(IReleaseFeed feed, string currentVersion)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _current = SemanticVersion.TryParse(currentVersion, out var v) ? v : null;
    }

    /// <summary>
    /// Checks once. Asks nothing when <paramref name="enabled"/> is false or the last check is less than a day old. Never throws
    /// for network trouble: a failure is <see cref="UpdateCheckStatus.Failed"/> and nobody is bothered with it.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool enabled, DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        if (!enabled) return new UpdateCheckResult(UpdateCheckStatus.Off);
        if (!UpdateSchedule.IsDue(lastCheckUtc, nowUtc)) return new UpdateCheckResult(UpdateCheckStatus.NotDue);
        ReleaseInfo? latest;
        try
        {
            latest = await _feed.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Asked: true);
        }
        if (latest is null || _current is null) return new UpdateCheckResult(UpdateCheckStatus.Failed, Asked: true);
        return latest.Version.CompareTo(_current) > 0
            ? new UpdateCheckResult(UpdateCheckStatus.Available, latest, Asked: true)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, Asked: true);
    }
}
