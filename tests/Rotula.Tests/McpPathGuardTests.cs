using System.Diagnostics;
using ModelContextProtocol;
using Rotula.Mcp.Security;

namespace Rotula.Tests;

/// <summary>Every path an assistant sends must stay inside an allowed folder.</summary>
public class McpPathGuardTests : IDisposable
{
    private readonly McpTestEnv _env = new();
    private static readonly string[] Rotula = [".rotula"];

    public void Dispose() => _env.Dispose();

    [Fact]
    public void ABareNameLivesInTheFirstAllowedFolder()
    {
        string full = _env.Context.Guard.Resolve("shipping.rotula", Rotula, mustExist: false);
        Assert.Equal(Path.Combine(_env.Labels, "shipping.rotula"), full);
    }

    [Fact]
    public void AnAbsolutePathInsideIsAccepted_AndAnUpperCaseDriveSpellingToo()
    {
        string path = Path.Combine(_env.Labels, "sub", "a.rotula");
        Assert.Equal(path, _env.Context.Guard.Resolve(path, Rotula, false));
        Assert.True(_env.Context.Guard.IsAllowed(path.ToUpperInvariant(), Rotula, false));
    }

    public static TheoryData<string, string> Rejected => new()
    {
        { "..\\evil.rotula", "traversal" },
        { "sub\\..\\..\\evil.rotula", "traversal" },
        { "sub/../../evil.rotula", "traversal" },
        { "..", "traversal" },
        { "\\\\server\\share\\a.rotula", "unc" },
        { "//server/share/a.rotula", "unc" },
        { "\\\\?\\C:\\Windows\\a.rotula", "device path" },
        { "\\\\.\\PhysicalDrive0", "device path" },
        { "C:\\Windows\\System32\\a.rotula", "outside" },
        { "C:evil.rotula", "drive relative" },
        { "\\rooted.rotula", "rooted without drive" },
        { "file:///C:/a.rotula", "url" },
        { "http://example.com/a.rotula", "url" },
        { "a.rotula:stream", "alternate data stream" },
        { "a.rotula::$DATA", "alternate data stream" },
        { "con.rotula", "reserved name" },
        { "NUL", "reserved name" },
        { "nul.txt", "reserved name and extension" },
        { "sub\\COM1.rotula", "reserved name in a folder" },
        { "lpt9.rotula", "reserved name" },
        { "aux.", "reserved name trailing dot" },
        { "COM1 .rotula", "reserved name with space" },
        { "bad<name>.rotula", "wildcard" },
        { "star*.rotula", "wildcard" },
        { "pipe|.rotula", "wildcard" },
        { "a.exe", "extension" },
        { "a.bat", "extension" },
        { "a.rotula.exe", "extension" },
        { "noextension", "extension" },
        { "", "empty" },
        { "   ", "blank" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void BadPathsAreRefusedWithAMessage(string path, string why)
    {
        var ex = Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(path, Rotula, false));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"No message for the refused path ({why}).");
    }

    [Fact]
    public void TrailingDotsAndSpacesNeverSurviveIntoTheResolvedPath()
    {
        // Windows drops them, so the guard must either refuse or hand back the normalised name; an alias must never get through.
        foreach (string name in new[] { "evil.rotula.", "evil.rotula ", "sub \\a.rotula", "sub.\\a.rotula" })
        {
            try
            {
                string full = _env.Context.Guard.Resolve(name, Rotula, false);
                Assert.StartsWith(_env.Labels + "\\", full);
                Assert.DoesNotMatch(@"[. ]\\|[. ]$", full);
            }
            catch (McpException) { }
        }
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotInside()
    {
        string evil = _env.Labels + " Evil";
        Directory.CreateDirectory(evil);
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(Path.Combine(evil, "a.rotula"), Rotula, false));
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(_env.Labels + "x\\a.rotula", Rotula, false));
    }

    [Fact]
    public void LongPathsAreRefused()
    {
        string longPath = Path.Combine(_env.Labels, new string('a', 1100) + ".rotula");
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(longPath, Rotula, false));
        string longSegment = Path.Combine(_env.Labels, new string('b', 300) + ".rotula");
        // Windows file names are at most 255 characters; either the guard or the file system must refuse this, never write outside.
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(longSegment, Rotula, mustExist: true));
    }

    [Fact]
    public void ControlCharactersAreRefused()
    {
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve("a\0.rotula", Rotula, false));
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve("a\n.rotula", Rotula, false));
    }

    [Fact]
    public void AMissingFileIsAFriendlyErrorForReads()
    {
        var ex = Assert.Throws<McpException>(() => _env.Context.Guard.Resolve("missing.rotula", Rotula, mustExist: true));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefusedBeforeItIsRead()
    {
        using var env = new McpTestEnv(s => s with { MaxFileBytes = 2048 });
        string big = Path.Combine(env.Labels, "big.rotula");
        File.WriteAllBytes(big, new byte[4096]);
        var ex = Assert.Throws<McpException>(() => env.Context.Guard.Resolve(big, Rotula, mustExist: true));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void ExtensionsOutsideTheGlobalWhitelistAreNeverAccepted()
    {
        // A tool asking for ".exe" must still be refused.
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve("a.exe", [".exe"], false));
        foreach (string ext in new[] { ".rotula", ".zpl", ".png", ".csv", ".xlsx", ".pdf" })
            Assert.True(_env.Context.Guard.IsAllowed("ok" + ext, [ext], false), ext);
    }

    [Fact]
    public void AJunctionInsideTheFolderCannotLeadOut()
    {
        string outside = Path.Combine(_env.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.rotula"), "{}");
        string junction = Path.Combine(_env.Labels, "link");
        Assert.True(TryMakeJunction(junction, outside), "mklink /J failed, but junctions need no administrator rights");

        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve("link\\secret.rotula", Rotula, mustExist: true));
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(Path.Combine(junction, "new.rotula"), Rotula, mustExist: false));
        // The listing also skips it.
        Assert.DoesNotContain(_env.Label.ListLabelFiles().Files, f => f.Path.Contains("secret.rotula"));
    }

    [Fact]
    public void ASymbolicLinkToAFileCannotLeadOut()
    {
        string target = Path.Combine(_env.Root, "target.rotula");
        File.WriteAllText(target, "{}");
        string link = Path.Combine(_env.Labels, "alias.rotula");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; } // symlinks need developer mode or admin
        Assert.Throws<McpException>(() => _env.Context.Guard.Resolve(link, Rotula, mustExist: true));
    }

    [Fact]
    public void NoUsableFolderMeansEverythingIsRefused()
    {
        using var env = new McpTestEnv(s => s with { AllowedFolders = ["\\\\server\\share"] });
        Assert.Empty(env.Context.Guard.Roots);
        Assert.Throws<McpException>(() => env.Context.Guard.Resolve("a.rotula", Rotula, false));
    }

    [Fact]
    public void ToolsRefuseOutsidePathsEndToEnd()
    {
        _env.Label.CreateLabel(50, 30);
        Assert.Throws<McpException>(() => _env.Label.SaveLabel("C:\\Windows\\Temp\\x.rotula"));
        Assert.Throws<McpException>(() => _env.Label.OpenLabel("..\\..\\x.rotula"));
        Assert.Throws<McpException>(() => _env.Label.GenerateZpl("\\\\server\\share\\x.zpl"));
        Assert.Throws<McpException>(() => _env.Elements.AddImage("C:\\Windows\\Web\\Wallpaper\\x.png", 0, 0));
        Assert.Throws<McpException>(() => _env.Preview.MergePreview("..\\data.csv"));
        Assert.Throws<McpException>(() => _env.Preview.RenderPreview(path: "C:\\x.png"));
    }

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return false; }
    }
}
