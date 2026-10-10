using ModelContextProtocol;
using Rotula.Core.Barcodes;
using Rotula.Core.Model;
using Rotula.Mcp.Tools;

namespace Rotula.Tests;

/// <summary>list_symbologies, add_barcode and update_element over the whole barcode catalogue.</summary>
public sealed class McpBarcodeBreadthTests : IDisposable
{
    private readonly McpTestEnv _env = new();

    public McpBarcodeBreadthTests() => _env.Label.CreateLabel(120, 120);

    public void Dispose() => _env.Dispose();

    private static string Id(ChangeResult r) => r.Elements.Single().Id;

    [Fact]
    public void ListSymbologies_ReadsTheWholeCatalogue()
    {
        var list = _env.Label.ListSymbologies().Symbologies;
        Assert.Equal(28, list.Count);
        Assert.Equal(BarcodeCatalog.All.Select(i => i.Symbology.ToString()), list.Select(s => s.Name));
        foreach (var s in list)
        {
            var info = BarcodeCatalog.Get(Enum.Parse<BarcodeSymbology>(s.Name));
            Assert.Equal(info.ZplCommand, s.Zpl);
            Assert.Equal(info.ExampleData, s.ExampleData);
            Assert.False(string.IsNullOrWhiteSpace(s.DataRules));
            Assert.Equal(info.Options.Count, s.OptionDetails!.Count);
        }
    }

    [Fact]
    public void ListSymbologies_SaysWhichOptionsAreArgumentsAndWhichGoInTheOptionsObject()
    {
        var codabar = _env.Label.ListSymbologies().Symbologies.Single(s => s.Name == "Codabar");
        Assert.Contains(codabar.OptionDetails!, o => o.Name == "codabarStart" && o.Where == "options");
        Assert.Contains(codabar.OptionDetails!, o => o.Name == "moduleWidthDots" && o.Where == "argument");
        Assert.Contains("codabarStop", codabar.Options);
    }

    [Fact]
    public void ListSymbologies_ReportsPreviewLimitsAndRotation()
    {
        var all = _env.Label.ListSymbologies().Symbologies.ToDictionary(s => s.Name);
        Assert.Equal("exact", all["Code128"].Preview);
        Assert.Contains("placeholder", all["MaxiCode"].Preview);
        Assert.False(all["MaxiCode"].SupportsRotation);
        Assert.True(all["Aztec"].SupportsRotation);
        Assert.Equal("postal", all["Postnet"].Kind);
        Assert.Equal("stacked", all["Codablock"].Kind);
    }

    [Fact]
    public void EveryCatalogueExample_IsAcceptedByAddBarcode_WithoutIssues()
    {
        foreach (var info in BarcodeCatalog.All)
        {
            var r = _env.Elements.AddBarcode(info.Symbology.ToString(), info.ExampleData, 5, 5);
            Assert.DoesNotContain(r.Issues, i => i.Severity == "error");
            Assert.Equal(info.Symbology.ToString(), r.Elements.Single().Properties["symbology"]);
        }
        var zpl = _env.Label.GenerateZpl();
        Assert.False(zpl.HasErrors);
        foreach (var info in BarcodeCatalog.All) Assert.Contains(info.ZplCommand, zpl.Zpl);
    }

    [Fact]
    public void AddBarcode_AppliesTheOptionsObject_AndShowsThemBack()
    {
        var r = _env.Elements.AddBarcode("Codabar", "123456", 5, 5,
            options: new() { ["codabarStart"] = McpTestEnv.J("B"), ["codabarStop"] = McpTestEnv.J("d") });
        Assert.DoesNotContain(r.Issues, i => i.Severity == "error");
        var shown = (IDictionary<string, object>)r.Elements.Single().Properties["options"]!;
        Assert.Equal("B", shown["codabarStart"]);
        Assert.Equal("D", shown["codabarStop"]);
    }

    [Fact]
    public void Options_AreAcceptedForEveryType_ThatListsThem()
    {
        var samples = new Dictionary<string, object>
        {
            ["code11CheckDigitCount"] = 1, ["msiCheckDigit"] = "DoubleMod10", ["msiCheckDigitInText"] = true, ["codabarStart"] = "C", ["codabarStop"] = "D",
            ["dataBarType"] = "Stacked", ["dataBarSeparatorHeight"] = 2, ["dataBarSegmentsPerRow"] = 8, ["aztecEci"] = true, ["aztecErrorControl"] = 150,
            ["aztecMenuSymbol"] = false, ["aztecStructuredAppendCount"] = 2, ["aztecStructuredAppendId"] = "SET1", ["microPdf417Mode"] = 3,
            ["codablockMode"] = "E", ["codablockSecurity"] = false, ["maxiCodeMode"] = 4, ["maxiCodeSymbolNumber"] = 1, ["maxiCodeSymbolTotal"] = 2,
            ["tlc39MicroModuleWidth"] = 3, ["tlc39MicroRowHeight"] = 6, ["code49StartMode"] = "2",
        };
        foreach (var s in _env.Label.ListSymbologies().Symbologies)
        {
            foreach (var option in s.OptionDetails!.Where(o => o.Where == "options"))
            {
                var r = _env.Elements.AddBarcode(s.Name, s.ExampleData, 5, 5, options: new() { [option.Name] = McpTestEnv.J(samples[option.Name]) });
                Assert.Contains(option.Name, ((IDictionary<string, object>)r.Elements.Single().Properties["options"]!).Keys);
            }
        }
    }

    [Fact]
    public void AnUnknownOrForeignOption_IsRefusedWithTheValidOnes_AndAddsNothing()
    {
        var unknown = Assert.Throws<McpException>(() => _env.Elements.AddBarcode("Codabar", "123", 5, 5, options: new() { ["bogus"] = McpTestEnv.J(1) }));
        Assert.Contains("codabarStart", unknown.Message);
        var foreign = Assert.Throws<McpException>(() => _env.Elements.AddBarcode("Code128", "123", 5, 5, options: new() { ["msiCheckDigit"] = McpTestEnv.J("None") }));
        Assert.Contains("another barcode type", foreign.Message);
        Assert.Empty(_env.Label.GetLabel().Elements);
    }

    [Theory]
    [InlineData("codabarStart", "Z")]
    [InlineData("codabarStart", "AB")]
    [InlineData("msiCheckDigit", "Triple")]
    [InlineData("dataBarType", "Huge")]
    public void ABadOptionValue_IsRefused_AndNothingChanges(string option, string value)
    {
        var symbology = option.StartsWith("msi") ? "Msi" : option.StartsWith("dataBar") ? "DataBar" : "Codabar";
        var ex = Assert.Throws<McpException>(() => _env.Elements.AddBarcode(symbology, BarcodeCatalog.Get(Enum.Parse<BarcodeSymbology>(symbology)).ExampleData, 5, 5,
            options: new() { [option] = McpTestEnv.J(value) }));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Empty(_env.Label.GetLabel().Elements);
    }

    [Fact]
    public void HostileOptionValues_AreRefused()
    {
        Assert.Throws<McpException>(() => _env.Elements.AddBarcode("Msi", "123", 5, 5, options: new() { ["msiCheckDigitInText"] = McpTestEnv.J(new { a = 1 }) }));
        Assert.Throws<McpException>(() => _env.Elements.AddBarcode("MaxiCode", "x", 5, 5, options: new() { ["maxiCodeMode"] = McpTestEnv.J(99999999999.0) }));
        Assert.Throws<McpException>(() => _env.Elements.AddBarcode("Aztec", "x", 5, 5, options: new() { ["aztecStructuredAppendId"] = McpTestEnv.J(new string('x', 5000)) }));
        var tooMany = Enumerable.Range(0, 100).ToDictionary(i => "k" + i, i => McpTestEnv.J(i));
        Assert.Throws<McpException>(() => _env.Elements.AddBarcode("Aztec", "x", 5, 5, options: tooMany));
        Assert.Empty(_env.Label.GetLabel().Elements);
    }

    [Fact]
    public void UpdateElement_ChangesTheSymbologyAndItsOptionsTogether()
    {
        string id = Id(_env.Elements.AddBarcode("Code128", "ABC", 5, 5));
        var r = _env.Elements.UpdateElement(id, new()
        {
            ["options"] = McpTestEnv.J(new Dictionary<string, object> { ["msiCheckDigit"] = "None" }),
            ["symbology"] = McpTestEnv.J("Msi"),
            ["data"] = McpTestEnv.J("1234567"),
        });
        var props = r.Elements.Single().Properties;
        Assert.Equal("Msi", props["symbology"]);
        Assert.Equal("None", ((IDictionary<string, object>)props["options"]!)["msiCheckDigit"]);
    }

    [Fact]
    public void UpdateElement_WithABadOption_ChangesNothing()
    {
        string id = Id(_env.Elements.AddBarcode("Codabar", "123", 5, 5));
        Assert.Throws<McpException>(() => _env.Elements.UpdateElement(id, new()
        {
            ["data"] = McpTestEnv.J("999"),
            ["options"] = McpTestEnv.J(new Dictionary<string, object> { ["codabarStart"] = "Q" }),
        }));
        Assert.Equal("123", _env.Label.GetLabel().Elements.Single().Properties["data"]);
    }

    [Fact]
    public void Duplicate_KeepsTheOptions()
    {
        string id = Id(_env.Elements.AddBarcode("Msi", "123", 5, 5, options: new() { ["msiCheckDigit"] = McpTestEnv.J("Mod11Mod10") }));
        var copy = _env.Elements.DuplicateElement(id);
        Assert.Equal("Mod11Mod10", ((IDictionary<string, object>)copy.Elements.Single().Properties["options"]!)["msiCheckDigit"]);
    }

    [Fact]
    public void ValidateLabel_StillReportsTheSymbologyRules_ForOptions()
    {
        _env.Elements.AddBarcode("DataBar", "1234567890123", 5, 5, options: new() { ["dataBarSegmentsPerRow"] = McpTestEnv.J(5) });
        Assert.False(_env.Label.ValidateLabel().Printable);
    }

    [Fact]
    public void SymbologyAliases_AreAccepted()
    {
        Assert.Equal("DataBar", _env.Elements.AddBarcode("GS1 DataBar", "1234567890123", 5, 5).Elements.Single().Properties["symbology"]);
        Assert.Equal("Codabar", _env.Elements.AddBarcode("NW-7", "123", 5, 25).Elements.Single().Properties["symbology"]);
        Assert.Equal("Ean8", _env.Elements.AddBarcode("ean-8", "9638507", 5, 45).Elements.Single().Properties["symbology"]);
    }
}
