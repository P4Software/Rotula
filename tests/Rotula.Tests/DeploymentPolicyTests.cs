using System.Xml.Linq;
using Rotula.Files.Policy;
using Rotula.Mcp.Security;
using Rotula.Mcp.Settings;

namespace Rotula.Tests;

/// <summary>A policy source made of two dictionaries, standing in for the HKLM and HKCU keys.</summary>
internal sealed class FakePolicySource : IPolicySource
{
    public readonly Dictionary<string, object> Values = new(StringComparer.OrdinalIgnoreCase);

    public FakePolicySource Set(string name, object value) { Values[name] = value; return this; }

    public string? GetString(string name) => Values.TryGetValue(name, out var v) ? v as string : null;

    public int? GetInt(string name) => Values.TryGetValue(name, out var v) && v is int i ? i : null;
}

/// <summary>Organization policies: reading, precedence, the MCP server's use of them, and the ADMX/ADML templates.</summary>
public class DeploymentPolicyTests
{
    [Fact]
    public void NoPolicy_ManagesNothing()
    {
        var p = RotulaPolicy.Read(EmptyPolicySource.Instance);
        Assert.Equal(RotulaPolicy.None, p);
        Assert.Null(p.Language);
        Assert.Null(p.UpdateCheck);
        Assert.Null(p.DefaultPrinter);
        Assert.True(p.AllowUserAddOns);
        Assert.False(p.McpPrintingBlocked);
        Assert.False(p.McpDatabaseBlocked);
        Assert.False(p.IsLanguageManaged);
        Assert.False(p.IsUpdateCheckManaged);
    }

    [Fact]
    public void EveryValueIsRead()
    {
        var src = new FakePolicySource()
            .Set(PolicyNames.Language, " ES ")
            .Set(PolicyNames.UpdateCheck, 0)
            .Set(PolicyNames.DefaultPrinter, "  Warehouse ZD420 ")
            .Set(PolicyNames.UserAddOns, 0)
            .Set(PolicyNames.McpPrinting, 0)
            .Set(PolicyNames.McpDatabase, 0);
        var p = RotulaPolicy.Read(src);
        Assert.Equal("es", p.Language);
        Assert.False(p.UpdateCheck);
        Assert.Equal("Warehouse ZD420", p.DefaultPrinter);
        Assert.False(p.AllowUserAddOns);
        Assert.True(p.McpPrintingBlocked);
        Assert.True(p.McpDatabaseBlocked);
        Assert.True(p.IsLanguageManaged);
        Assert.True(p.IsUpdateCheckManaged);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData("english")]
    public void AnUnknownLanguage_IsIgnored(string value) =>
        Assert.Null(RotulaPolicy.Read(new FakePolicySource().Set(PolicyNames.Language, value)).Language);

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, null)]
    [InlineData(-1, null)]
    public void UpdateCheck_OnlyZeroAndOneMeanSomething(int value, bool? expected) =>
        Assert.Equal(expected, RotulaPolicy.Read(new FakePolicySource().Set(PolicyNames.UpdateCheck, value)).UpdateCheck);

    [Fact]
    public void WrongTypesAndOverlongPrinterNames_AreIgnored()
    {
        var p = RotulaPolicy.Read(new FakePolicySource()
            .Set(PolicyNames.UpdateCheck, "1")              // text instead of a number
            .Set(PolicyNames.Language, 1)                   // number instead of text
            .Set(PolicyNames.DefaultPrinter, new string('x', 221))
            .Set(PolicyNames.McpPrinting, 1));              // 1 only means "the settings file decides"
        Assert.Null(p.UpdateCheck);
        Assert.Null(p.Language);
        Assert.Null(p.DefaultPrinter);
        Assert.False(p.McpPrintingBlocked);
    }

    [Fact]
    public void TheComputerWins_ValueByValue()
    {
        var machine = new FakePolicySource().Set(PolicyNames.Language, "en");
        var user = new FakePolicySource().Set(PolicyNames.Language, "es").Set(PolicyNames.UpdateCheck, 1);
        var p = RotulaPolicy.Read(new LayeredPolicySource(machine, user));
        Assert.Equal("en", p.Language);   // set in both: HKLM wins
        Assert.True(p.UpdateCheck);       // only the user sets it: HKCU applies
    }

    [Fact]
    public void TheRegistrySource_NeverThrows()
    {
        // On a build machine without the key (and off Windows) every value is simply "not set".
        var p = RotulaPolicy.Read(RegistryPolicySource.ForThisComputer());
        Assert.NotNull(p);
    }

    [Fact]
    public void AppSettings_UseThePolicyValueButKeepTheUsersOwn()
    {
        var s = new Rotula.App.Services.AppSettings { Language = "en", UpdateCheckEnabled = true };
        var p = new RotulaPolicy { Language = "es", UpdateCheck = false };
        Assert.Equal("es", s.LanguageIn(p));
        Assert.False(s.UpdateCheckIn(p));
        Assert.Equal("en", s.LanguageIn(RotulaPolicy.None));
        Assert.True(s.UpdateCheckIn(RotulaPolicy.None));
        // Forced on by the organization, although the user never switched it on.
        Assert.True(new Rotula.App.Services.AppSettings().UpdateCheckIn(new RotulaPolicy { UpdateCheck = true }));
    }

    // ------------------------------------------------------------------------------------------------- MCP server

    private static McpSettingsLoadResult Loaded(McpSettings s) => new(s, Array.Empty<string>(), "rotula-mcp.settings.json");

    [Fact]
    public void Mcp_APolicySwitchesPrintingAndDatabasesOff_AndSaysSo()
    {
        var open = new McpSettings { AllowPrinting = true, AllowedPrinters = ["Zebra ZD420"], AllowDatabase = true };
        var r = McpSettingsLoader.ApplyPolicy(Loaded(open), new RotulaPolicy { McpPrintingBlocked = true, McpDatabaseBlocked = true });
        Assert.False(r.Settings.AllowPrinting);
        Assert.False(r.Settings.AllowDatabase);
        Assert.True(r.Settings.PrintingBlockedByPolicy);
        Assert.True(r.Settings.DatabaseBlockedByPolicy);
        Assert.Contains(r.Notes, n => n.Contains("Printing is switched off by your organization's policy"));
        Assert.Contains(r.Notes, n => n.Contains("Database access is switched off by your organization's policy"));

        var gate = new PrintGate(r.Settings).Evaluate("Zebra ZD420", 1, "fp", null);
        Assert.Equal(GateDecision.Refused, gate.Decision);
        Assert.Contains("organization's policy", gate.Message);
    }

    [Fact]
    public void Mcp_APolicyNeverSwitchesAnythingOn()
    {
        var closed = new McpSettings();
        var r = McpSettingsLoader.ApplyPolicy(Loaded(closed), RotulaPolicy.Read(new FakePolicySource()
            .Set(PolicyNames.McpPrinting, 1).Set(PolicyNames.McpDatabase, 1)));
        Assert.False(r.Settings.AllowPrinting);
        Assert.False(r.Settings.AllowDatabase);
        Assert.False(r.Settings.PrintingBlockedByPolicy);
        Assert.Empty(r.Notes);

        var open = new McpSettings { AllowPrinting = true, AllowDatabase = true };
        var kept = McpSettingsLoader.ApplyPolicy(Loaded(open), RotulaPolicy.None);
        Assert.True(kept.Settings.AllowPrinting);
        Assert.True(kept.Settings.AllowDatabase);
    }

    // ------------------------------------------------------------------------------------------------- templates

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rotula.sln not found above the test folder.");
    }

    private static readonly XNamespace Gp = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static XDocument Admx() => XDocument.Load(Path.Combine(RepoRoot(), "packaging", "policies", "Rotula.admx"));

    private static XDocument Adml(string culture) => XDocument.Load(Path.Combine(RepoRoot(), "packaging", "policies", culture, "Rotula.adml"));

    [Fact]
    public void Admx_WritesExactlyTheValuesRotulaReads_UnderThePolicyKey()
    {
        var admx = Admx();
        var policies = admx.Descendants(Gp + "policy").ToList();
        Assert.NotEmpty(policies);
        Assert.All(policies, p => Assert.Equal(PolicyNames.KeyPath, (string?)p.Attribute("key")));
        var valueNames = policies.Select(p => (string?)p.Attribute("valueName"))
            .Concat(policies.SelectMany(p => p.Descendants()).Select(e => (string?)e.Attribute("valueName")))
            .OfType<string>().ToHashSet();
        Assert.Equal(PolicyNames.All.OrderBy(n => n), valueNames.OrderBy(n => n));
    }

    [Fact]
    public void Admx_LanguageChoicesAreTheOnesRotulaAccepts()
    {
        var values = Admx().Descendants(Gp + "enum").Single(e => (string?)e.Attribute("valueName") == PolicyNames.Language)
            .Descendants(Gp + "string").Select(s => s.Value).ToList();
        Assert.Equal(new[] { "auto", "en", "es" }, values);
        Assert.All(values, v => Assert.Equal(v, RotulaPolicy.Read(new FakePolicySource().Set(PolicyNames.Language, v)).Language));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    public void Adml_DefinesEveryStringAndPresentationTheAdmxUses(string culture)
    {
        var admx = Admx().ToString();
        var adml = Adml(culture);
        var strings = adml.Descendants(Gp + "string").Select(s => (string)s.Attribute("id")!).ToHashSet();
        var presentations = adml.Descendants(Gp + "presentation").Select(s => (string)s.Attribute("id")!).ToHashSet();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(admx, @"\$\(string\.([A-Za-z0-9_]+)\)"))
            Assert.Contains(m.Groups[1].Value, strings);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(admx, @"\$\(presentation\.([A-Za-z0-9_]+)\)"))
            Assert.Contains(m.Groups[1].Value, presentations);
        Assert.All(adml.Descendants(Gp + "string"), s => Assert.False(string.IsNullOrWhiteSpace(s.Value)));
    }

    [Fact]
    public void Adml_BothLanguagesHaveTheSameIds()
    {
        static string[] Ids(XDocument d) => d.Descendants(Gp + "string").Select(s => (string)s.Attribute("id")!).OrderBy(x => x).ToArray();
        Assert.Equal(Ids(Adml("en-US")), Ids(Adml("es-ES")));
    }
}
