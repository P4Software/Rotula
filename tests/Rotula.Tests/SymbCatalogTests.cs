using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>The symbology catalogue must describe every symbology completely and agree with the generator, validator and element.</summary>
public class SymbCatalogTests
{
    public static TheoryData<BarcodeSymbology> All => SymbSupport.AllSymbologies();

    [Fact]
    public void Every_symbology_has_exactly_one_catalogue_entry()
    {
        var listed = BarcodeCatalog.All.Select(e => e.Symbology).ToList();
        Assert.Equal(Enum.GetValues<BarcodeSymbology>().OrderBy(s => s), listed.OrderBy(s => s));
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(28, listed.Count);
    }

    [Fact]
    public void Every_zpl_barcode_command_is_covered_once_except_the_shared_postal_command()
    {
        // ^BZ prints POSTNET and ^B5 prints PLANET, so ^BZ is listed for POSTNET and PLANET uses ^B5: all 28 commands appear.
        string[] expected = { "^B1", "^B2", "^B3", "^B4", "^B5", "^B7", "^B8", "^B9", "^BA", "^BB", "^BC", "^BD", "^BE", "^BF", "^BI", "^BJ", "^BK", "^BL",
            "^BM", "^BO", "^BP", "^BQ", "^BR", "^BS", "^BT", "^BU", "^BX", "^BZ" };
        var commands = BarcodeCatalog.All.Select(e => e.ZplCommand).ToHashSet();
        foreach (var c in expected) Assert.Contains(c, commands);
        Assert.Equal(expected.Length, commands.Count);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Entry_is_complete(BarcodeSymbology s)
    {
        var info = BarcodeCatalog.Get(s);
        Assert.Equal(s, info.Symbology);
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayName));
        Assert.Matches(@"^\^B[0-9A-Z]$", info.ZplCommand);
        Assert.False(string.IsNullOrWhiteSpace(info.CharacterSet));
        Assert.False(string.IsNullOrWhiteSpace(info.LengthRule));
        Assert.False(string.IsNullOrEmpty(info.ExampleData));
        Assert.NotEmpty(info.Options);
        Assert.Equal(info.Options.Count, info.Options.Distinct().Count());
        // An exact preview needs no apology; a missing one must explain itself.
        if (!info.HasPreviewEncoder) Assert.False(string.IsNullOrWhiteSpace(info.PreviewNote));
        Assert.Equal(s, BarcodeCatalog.FindByZplCommand(info.ZplCommand.ToLowerInvariant())!.Symbology);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Example_data_validates_with_the_default_options(BarcodeSymbology s)
    {
        var e = SymbSupport.Default(s);
        var issues = BarcodeValidator.Validate(e, e.Data);
        Assert.True(issues.Count == 0, string.Join(", ", issues));
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Generated_zpl_uses_the_catalogue_command(BarcodeSymbology s)
    {
        var e = SymbSupport.Default(s);
        string zpl = CoreTestSupport.Zpl(CoreTestSupport.NewDoc(e));
        // ^BZ is the generic postal command (POSTNET); PLANET is written with its own ^B5.
        Assert.Contains(BarcodeCatalog.Get(s).ZplCommand, zpl);
        // The field must start with the origin and contain the barcode command before the data, never a bare ^FD.
        int fo = zpl.IndexOf("^FO30,40", StringComparison.Ordinal);
        int cmd = zpl.IndexOf(BarcodeCatalog.Get(s).ZplCommand, fo, StringComparison.Ordinal);
        int fd = zpl.IndexOf("^FD", fo, StringComparison.Ordinal);
        Assert.True(fo >= 0 && cmd > fo && cmd < fd, zpl);
    }

    [Fact]
    public void Every_option_names_a_real_element_property_and_every_property_is_in_some_entry()
    {
        var props = SymbSupport.OptionProperties().Select(p => p.Name).ToHashSet();
        var optionNames = Enum.GetNames<BarcodeOption>().ToHashSet();
        foreach (var name in optionNames) Assert.Contains(name, props);
        // Properties every barcode has are not options.
        props.ExceptWith(new[] { nameof(BarcodeElement.Symbology), nameof(BarcodeElement.Data) });
        var used = BarcodeCatalog.All.SelectMany(e => e.Options).Select(o => o.ToString()).ToHashSet();
        foreach (var name in props) Assert.True(used.Contains(name), $"{name} is not listed as an option of any symbology");
    }

    [Fact]
    public void Extra_options_round_trip_through_the_open_ended_dictionary()
    {
        foreach (var s in SymbSupport.Symbologies)
        {
            var original = SymbSupport.Variant(s);
            var copy = new BarcodeElement();
            copy.ImportExtraOptions(original.ExportExtraOptions());
            // The extra options are exactly the properties that have no field of their own in the old file format.
            var oldProps = new[] { "Symbology", "Data", "Height", "ModuleWidth", "WideBarRatio", "HumanReadable", "HumanReadablePosition", "CheckDigit",
                "Code128Mode", "QrErrorCorrection", "QrModel", "ModuleSize", "Columns", "Rows", "Pdf417SecurityLevel", "Pdf417Truncate" };
            foreach (var p in SymbSupport.OptionProperties().Where(p => !oldProps.Contains(p.Name)))
                Assert.True(Equals(p.GetValue(original), p.GetValue(copy)), $"{s}: {p.Name}");
        }
    }

    [Fact]
    public void Default_extra_options_export_nothing_and_unknown_names_are_ignored()
    {
        Assert.Empty(new BarcodeElement().ExportExtraOptions());
        var e = new BarcodeElement();
        e.ImportExtraOptions(new Dictionary<string, string> { ["FromTheFuture"] = "1", ["MaxiCodeMode"] = "not a number", ["DataBarType"] = "99", ["CodabarStart"] = "B" });
        Assert.Equal('B', e.CodabarStart);
        Assert.Equal(2, e.MaxiCodeMode);
        Assert.Equal(DataBarType.Omnidirectional, e.DataBarType);
    }
}
