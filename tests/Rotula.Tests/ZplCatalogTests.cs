using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Catalog;
using Rotula.Core.Zpl.Import;

namespace Rotula.Tests;

/// <summary>
/// Keeps the ZPL command catalogue (commands.json) and the generated coverage page honest. Statuses are checked by behaviour:
/// each command is fed to the real parser and generator, so a command cannot be claimed as supported when it is not, and a
/// command cannot become supported in code without the catalogue saying so.
/// </summary>
public class ZplCatalogTests
{
    private static readonly string[] Prefixes = { "^", "~" };

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(ZplParserTestSupport.FixturesDir(), "..", ".."));

    private static string Wrap(string body) => "^XA" + body + "^XZ";

    /// <summary>The text the parser must keep verbatim for a command that is not interpreted.</summary>
    private static string Probe(ZplCommandInfo c, string prefix) => c.Example.Length > 0 ? c.Example : prefix + c.Code;

    private static bool IsKept(ZplParseResult r) =>
        r.Document.Elements.Any(e => e is RawZplElement or RecallPlaceholderElement);

    // ---- the data itself --------------------------------------------------------------------

    [Fact]
    public void The_catalogue_loads_and_is_well_formed()
    {
        var all = ZplCommandCatalog.All;
        // The command list of the ZPL II guide has about 220 entries.
        Assert.InRange(all.Count, 215, 400);
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct().Count());
        foreach (var c in all)
        {
            Assert.Matches(@"^[\^~][A-Z0-9@]{1,2}$", c.Id);
            Assert.Equal(c.Id[1..], c.Code);
            Assert.True(c.Prefixes.Contains(c.Id[0]), c.Id);
            Assert.Contains(c.Prefixes, new[] { "^", "~", "^~" });
            Assert.Contains(c.Group, ZplCommandCatalog.Groups);
            Assert.False(string.IsNullOrWhiteSpace(c.Name), c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Summary), c.Id);
            Assert.DoesNotContain('\n', c.Summary);
            if (c.Status == ZplCommandStatus.OutOfScope) Assert.False(string.IsNullOrWhiteSpace(c.Reason), c.Id + " needs a reason");
            else Assert.Null(c.Reason);
            if (c.Status == ZplCommandStatus.Supported)
            {
                Assert.True(c.Parser, c.Id + " is supported, so the parser must read it");
                Assert.False(string.IsNullOrWhiteSpace(c.Example), c.Id + " needs an example");
            }
            else
            {
                Assert.False(c.Parser, c.Id + " is not supported, so the parser flag must be off");
                Assert.False(c.Generator, c.Id);
            }
        }
    }

    [Fact]
    public void The_catalogue_lists_the_commands_the_guide_documents()
    {
        // A representative spread: every group, both prefixes, the pairs that differ by prefix.
        string[] expected =
        {
            "^XA", "^XZ", "^FO", "^FT", "^FD", "^FS", "^BC", "^BQ", "^BO", "^B0", "^GB", "^GD", "^GE", "^GS", "^TB", "^LS", "^LT", "^LR",
            "^MM", "^PM", "^PO", "^PR", "^MD", "~SD", "^FN", "^FE", "^FC", "^FP", "^CW", "^SN", "^SF", "~DG", "~DY", "^DF", "^XF", "^XG", "^IM",
            "^IL", "^IS", "^ID", "^PH", "~PH", "^HL", "~HL", "^CC", "~CC", "^CD", "~CD", "^CT", "~CT", "^JB", "~JB", "^WL", "~WL", "^WR", "~WR",
            "^PR", "~PR", "^PM", "~PM", "^JS", "~JS", "^NC", "~NC", "^NT", "~NT", "^RF", "^HY", "~HS", "^KV", "^CN",
        };
        foreach (string id in expected)
            Assert.True(ZplCommandCatalog.Find(id) is not null, id + " is missing from the catalogue");
    }

    [Fact]
    public void Planned_features_are_recorded_for_templates()
    {
        Assert.Equal(ZplCommandStatus.Planned, ZplCommandCatalog.Find("^DF")!.Status);
        Assert.Equal(ZplCommandStatus.Planned, ZplCommandCatalog.Find("^XF")!.Status);
    }

    [Fact]
    public void Search_finds_commands_by_name_code_and_description()
    {
        Assert.Contains(ZplCommandCatalog.Search("diagonal"), c => c.Id == "^GD");
        Assert.Contains(ZplCommandCatalog.Search("^BQ"), c => c.Id == "^BQ");
        Assert.Contains(ZplCommandCatalog.Search("mirror image"), c => c.Id == "^PM");
        Assert.All(ZplCommandCatalog.Search("", ZplCommandStatus.Supported), c => Assert.Equal(ZplCommandStatus.Supported, c.Status));
        Assert.Equal("^PH / ~PH", ZplCommandCatalog.Find("~PH")!.DisplayId);
    }

    // ---- behaviour: supported commands ---------------------------------------------------------------

    public static IEnumerable<object[]> Supported() =>
        ZplCommandCatalog.All.Where(c => c.Status == ZplCommandStatus.Supported).Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Supported))]
    public void A_supported_command_is_interpreted_not_kept_raw(string id)
    {
        var c = ZplCommandCatalog.Find(id)!;
        var r = ZplParser.Parse(Wrap(c.Example));

        Assert.False(IsKept(r), id + " example was kept as raw ZPL: " + c.Example);
        Assert.DoesNotContain(r.Warnings, w => w.MessageKey is "Import.Command.Unknown" or "Import.Setting.Unsupported" or "Import.Internal.Error");
        Assert.DoesNotContain(r.Warnings, w => w.MessageKey == "Import.Parameter.Invalid" || w.MessageKey == "Import.Parameter.OutOfRange");

        // Whether the generator writes the command must match the catalogue flag, in both directions.
        string zpl = ZplGenerator.Generate(r.Document).Zpl;
        string token = c.Id[0] + c.Code;
        bool written = zpl.Contains(token, StringComparison.Ordinal);
        if (c.Generator) Assert.True(written, id + " is marked as generated, but the generator did not write it for: " + c.Example + "\n" + zpl);
        else Assert.False(written, id + " is marked as not generated, but the generator wrote it:\n" + zpl);
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void The_example_of_a_supported_command_survives_a_round_trip(string id)
    {
        var c = ZplCommandCatalog.Find(id)!;
        var first = ZplParser.Parse(Wrap(c.Example));
        var result = ZplGenerator.Generate(first.Document);
        Assert.False(result.HasErrors, id + ": " + string.Join(", ", result.Issues.Select(i => i.MessageKey)));
        var second = ZplParser.Parse(result.Zpl);
        ZplParserTestSupport.AssertSame(first.Document, second.Document);
    }

    // ---- behaviour: everything else is kept, never dropped -------------------------------------------

    public static IEnumerable<object[]> NotSupported() =>
        ZplCommandCatalog.All.Where(c => c.Status != ZplCommandStatus.Supported).Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(NotSupported))]
    public void A_command_that_is_not_supported_is_kept_verbatim_and_warned_about(string id)
    {
        var c = ZplCommandCatalog.Find(id)!;
        foreach (char prefix in c.Prefixes)
        {
            string probe = Probe(c, prefix.ToString());
            if (c.Example.Length > 0 && prefix.ToString() != c.Id[..1]) continue; // examples are written with the primary prefix
            var r = ZplParser.Parse(Wrap(probe));
            Assert.True(IsKept(r), id + " was interpreted although the catalogue says it is not supported: " + probe);

            // Kept verbatim: the generated ZPL still holds the original text.
            string zpl = ZplGenerator.Generate(r.Document).Zpl;
            string command = prefix + c.Code;
            Assert.Contains(command, zpl, StringComparison.Ordinal);

            // ^FX comments are the one intentional silent case; anything else must be listed.
            if (c.Id != "^FX") Assert.NotEmpty(r.Warnings);
        }
    }

    [Fact]
    public void No_command_outside_the_catalogue_is_interpreted_by_the_parser()
    {
        // Every two-character code with either prefix: if the parser understands one, the catalogue must list it.
        var listed = new HashSet<string>(ZplCommandCatalog.All.SelectMany(c => c.Prefixes.Select(p => p + c.Code)));
        const string first = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string second = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var surprises = new List<string>();
        foreach (string prefix in Prefixes)
            foreach (char a in first)
                foreach (char b in second)
                {
                    string command = prefix + a + b;
                    // ^A takes the font id as a parameter (^A0, ^AB ...), so the whole ^A.. family is the one command ^A.
                    if (listed.Contains(command) || (prefix == "^" && a == 'A')) continue;
                    var r = ZplParser.Parse(Wrap(command));
                    if (!IsKept(r) || !r.Warnings.Any(w => w.MessageKey == "Import.Command.Unknown")) surprises.Add(command);
                }
        Assert.True(surprises.Count == 0, "The parser handles commands the catalogue does not list: " + string.Join(" ", surprises));
    }

    [Fact]
    public void Every_barcode_in_the_symbology_catalogue_is_supported_here_with_a_matching_preview()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var c = ZplCommandCatalog.Find(info.ZplCommand);
            Assert.True(c is not null, info.ZplCommand + " is missing");
            Assert.Equal(ZplCommandStatus.Supported, c!.Status);
            Assert.True(c.Generator, info.ZplCommand + " should be generated");
            Assert.Equal("Barcodes", c.Group);
            // The catalogue may only claim an exact or close preview when the designer really has an encoder.
            if (info.HasPreviewEncoder) Assert.NotEqual(ZplCommandPreview.Placeholder, c.Preview);
            else Assert.Equal(ZplCommandPreview.Placeholder, c.Preview);

            var doc = ZplParser.Parse(Wrap(c.Example)).Document;
            var barcode = Assert.IsType<BarcodeElement>(Assert.Single(doc.Elements));
            Assert.Equal(info.Symbology, barcode.Symbology);
        }
    }

    // ---- the generated page ------------------------------------------------------------------------

    [Fact]
    public void The_committed_coverage_page_is_up_to_date()
    {
        string path = Path.Combine(RepoRoot(), "docs", "zpl-coverage.md");
        Assert.True(File.Exists(path), "docs/zpl-coverage.md is missing. Run: dotnet run --project tools/gen-zpl-coverage");
        string committed = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(committed == ZplCoverageReport.Render(),
            "docs/zpl-coverage.md is out of date with src/Rotula.Core/Zpl/Catalog/commands.json. Run: dotnet run --project tools/gen-zpl-coverage");
    }

    [Fact]
    public void The_coverage_page_has_a_summary_legend_and_the_unsupported_command_explanation()
    {
        string page = ZplCoverageReport.Render();
        Assert.StartsWith("# ZPL coverage", page);
        Assert.DoesNotContain('\r', page);
        Assert.Contains("## At a glance", page);
        Assert.Contains("## How to read this page", page);
        Assert.Contains("## What happens to commands Rotula does not support", page);
        foreach (string group in ZplCommandCatalog.Groups.Where(g => ZplCommandCatalog.All.Any(c => c.Group == g)))
            Assert.Contains("### " + group, page);
        foreach (var c in ZplCommandCatalog.All) Assert.Contains("`" + c.DisplayId + "`", page);
        Assert.Contains($"| **All** | **{ZplCommandCatalog.All.Count}** |", page);
    }
}
