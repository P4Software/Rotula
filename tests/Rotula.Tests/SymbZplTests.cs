using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;

namespace Rotula.Tests;

/// <summary>
/// ZPL generation and import of the symbologies added after the first nine. The expected strings are written from the parameter
/// tables of the Zebra ZPL II Programming Guide, command by command, not copied from the generator's own output.
/// </summary>
public class SymbZplTests
{
    private static BarcodeElement Base(BarcodeSymbology s, string data, Action<BarcodeElement>? tweak = null)
    {
        var e = new BarcodeElement
        {
            Symbology = s, Data = data, X = 10, Y = 20, Height = 50, ModuleWidth = 3, WideBarRatio = 2.5, ModuleSize = 3,
            HumanReadable = true, HumanReadablePosition = HumanReadablePosition.Below, Rotation = FieldRotation.Normal,
        };
        tweak?.Invoke(e);
        return e;
    }

    private static string Field(BarcodeElement e)
    {
        string zpl = CoreTestSupport.Zpl(CoreTestSupport.NewDoc(e));
        return zpl.Split('\n').Single(l => l.StartsWith("^FO", StringComparison.Ordinal) || l.StartsWith("^FT", StringComparison.Ordinal));
    }

    public static TheoryData<BarcodeSymbology, string, string, Action<BarcodeElement>?> Commands => new()
    {
        // ^B1 o,e,h,f,g : e = Y means ONE check digit, N (default) means two
        { BarcodeSymbology.Code11, "123-45", "^FO10,20^BY3,2.5^B1N,N,50,Y,N^FD123-45^FS", null },
        { BarcodeSymbology.Code11, "123-45", "^FO10,20^BY3,2.5^B1N,Y,50,Y,N^FD123-45^FS", e => e.Code11CheckDigitCount = 1 },
        // ^BI / ^BJ o,h,f,g
        { BarcodeSymbology.Industrial2Of5, "12345", "^FO10,20^BY3,2.5^BIN,50,Y,N^FD12345^FS", null },
        { BarcodeSymbology.Standard2Of5, "12345", "^FO10,20^BY3,2.5^BJN,50,N,Y^FD12345^FS", e => { e.HumanReadable = false; e.HumanReadablePosition = HumanReadablePosition.Above; } },
        // ^BL o,h,g : no interpretation-line parameter
        { BarcodeSymbology.Logmars, "ABC123", "^FO10,20^BY3,2.5^BLN,50,N^FD" + "ABC123^FS", null },
        { BarcodeSymbology.Logmars, "ABC123", "^FO10,20^BY3,2.5^BLN,50,Y^FD" + "ABC123^FS", e => e.HumanReadablePosition = HumanReadablePosition.Above },
        // ^BK o,e,h,f,g,k,l : e is fixed at N, start and stop characters last
        { BarcodeSymbology.Codabar, "123456", "^FO10,20^BY3,2.5^BKN,N,50,Y,N,A,A^FD123456^FS", null },
        { BarcodeSymbology.Codabar, "123456", "^FO10,20^BY3,2.5^BKN,N,50,Y,N,B,D^FD123456^FS", e => { e.CodabarStart = 'B'; e.CodabarStop = 'D'; } },
        // ^BM o,e,h,f,g,e2
        { BarcodeSymbology.Msi, "1234567", "^FO10,20^BY3,2.5^BMN,B,50,Y,N,N^FD1234567^FS", null },
        { BarcodeSymbology.Msi, "1234567", "^FO10,20^BY3,2.5^BMN,A,50,Y,N,N^FD1234567^FS", e => e.MsiCheckDigit = MsiCheckDigitMode.None },
        { BarcodeSymbology.Msi, "1234567", "^FO10,20^BY3,2.5^BMN,C,50,Y,N,N^FD1234567^FS", e => e.MsiCheckDigit = MsiCheckDigitMode.DoubleMod10 },
        { BarcodeSymbology.Msi, "1234567", "^FO10,20^BY3,2.5^BMN,D,50,Y,N,Y^FD1234567^FS", e => { e.MsiCheckDigit = MsiCheckDigitMode.Mod11Mod10; e.MsiCheckDigitInText = true; } },
        // ^BP o,e,h,f,g
        { BarcodeSymbology.Plessey, "12AB", "^FO10,20^BY3,2.5^BPN,N,50,Y,N^FD12AB^FS", null },
        { BarcodeSymbology.Plessey, "12AB", "^FO10,20^BY3,2.5^BPN,Y,50,Y,N^FD12AB^FS", e => e.CheckDigit = true },
        // ^B8 o,h,f,g ; the printer adds the check digit, so a typed one is dropped
        { BarcodeSymbology.Ean8, "9638507", "^FO10,20^BY3,2.5^B8N,50,Y,N^FD9638507^FS", null },
        { BarcodeSymbology.Ean8, "96385074", "^FO10,20^BY3,2.5^B8N,50,Y,N^FD9638507^FS", null },
        // ^B9 o,h,f,g,e ; the data is the 10-digit manufacturer + product code
        { BarcodeSymbology.UpcE, "1234500005", "^FO10,20^BY3,2.5^B9N,50,Y,N,N^FD1234500005^FS", null },
        { BarcodeSymbology.UpcE, "123455", "^FO10,20^BY3,2.5^B9N,50,Y,N,Y^FD1234500005^FS", e => e.CheckDigit = true },
        // ^BS o,h,f,g
        { BarcodeSymbology.UpcEanExtension, "12", "^FO10,20^BY3,2.5^BSN,50,Y,N^FD12^FS", null },
        { BarcodeSymbology.UpcEanExtension, "12345", "^FO10,20^BY3,2.5^BSN,50,Y,Y^FD12345^FS", e => e.HumanReadablePosition = HumanReadablePosition.Above },
        // ^BZ o,h,f,g,t with t = 0 for POSTNET ; ^B5 o,h,f,g for PLANET
        { BarcodeSymbology.Postnet, "12345", "^FO10,20^BY3,2.5^BZN,50,Y,N,0^FD12345^FS", null },
        { BarcodeSymbology.Planet, "12345678901", "^FO10,20^BY3,2.5^B5N,50,N,N^FD12345678901^FS", e => e.HumanReadable = false },
        // ^BR a,b,c,d,e,f : explicit orientation, own magnification, no ^BY
        { BarcodeSymbology.DataBar, "1234567890123", "^FO10,20^BRN,1,3,1,50^FD1234567890123^FS", null },
        { BarcodeSymbology.DataBar, "12345678901231", "^FO10,20^BRN,2,3,2,50^FD1234567890123^FS", e => { e.DataBarType = DataBarType.Truncated; e.DataBarSeparatorHeight = 2; } },
        { BarcodeSymbology.DataBar, "(01)90614141999999(10)AB", "^FO10,20^BRN,6,3,1,50,12^FD(01)90614141999999(10)AB^FS", e => { e.DataBarType = DataBarType.Expanded; e.DataBarSegmentsPerRow = 12; } },
        // ^BO a,b,c,d,e,f,g
        { BarcodeSymbology.Aztec, "Hello", "^FO10,20^BON,3,N,0,N,1^FDHello^FS", null },
        { BarcodeSymbology.Aztec, "Hello", "^FO10,20^BON,3,Y,205,Y,3,ID1^FDHello^FS", e => { e.AztecEci = true; e.AztecErrorControl = 205; e.AztecMenuSymbol = true; e.AztecStructuredAppendCount = 3; e.AztecStructuredAppendId = "ID1"; } },
        // ^BF o,h,m
        { BarcodeSymbology.MicroPdf417, "HELLO", "^FO10,20^BY3^BFN,50,0^FDHELLO^FS", null },
        { BarcodeSymbology.MicroPdf417, "HELLO", "^FO10,20^BY3^BFN,50,7^FDHELLO^FS", e => e.MicroPdf417Mode = 7 },
        // ^BB o,h,s,c,r,m
        { BarcodeSymbology.Codablock, "ABC", "^FO10,20^BY3^BBN,50,Y,,,F^FDABC^FS", null },
        { BarcodeSymbology.Codablock, "ABC", "^FO10,20^BY3^BBN,50,N,12,3,A^FDABC^FS", e => { e.CodablockMode = CodablockMode.A; e.CodablockSecurity = false; e.Columns = 12; e.Rows = 3; } },
        // ^BD m,n,t : no orientation, no ^BY
        { BarcodeSymbology.MaxiCode, "001840123456789", "^FO10,20^BD2,1,1^FD001840123456789^FS", null },
        { BarcodeSymbology.MaxiCode, "free", "^FO10,20^BD4,2,3^FDfree^FS", e => { e.MaxiCodeMode = 4; e.MaxiCodeSymbolNumber = 2; e.MaxiCodeSymbolTotal = 3; } },
        // ^BT o,w1,r1,h1,w2,h2
        { BarcodeSymbology.Tlc39, "123456,ABCD", "^FO10,20^BTN,3,2.5,50,2,4^FD123456,ABCD^FS", null },
        // ^B4 o,h,f,m with f = N, A or B
        { BarcodeSymbology.Code49, "ABC", "^FO10,20^BY3^B4N,50,B,A^FDABC^FS", null },
        { BarcodeSymbology.Code49, "ABC", "^FO10,20^BY3^B4N,50,A,2^FDABC^FS", e => { e.HumanReadablePosition = HumanReadablePosition.Above; e.Code49StartMode = '2'; } },
        { BarcodeSymbology.Code49, "ABC", "^FO10,20^BY3^B4N,50,N,A^FDABC^FS", e => e.HumanReadable = false },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Generated_command_has_the_parameters_in_zpl_order(BarcodeSymbology s, string data, string expected, Action<BarcodeElement>? tweak)
    {
        Assert.Equal(expected, Field(Base(s, data, tweak)));
    }

    [Fact]
    public void Rotation_is_written_as_the_orientation_letter_and_data_bar_always_writes_one()
    {
        Assert.Equal("^FO10,20^BY3,2.5^B8R,50,Y,N^FD9638507^FS", Field(Base(BarcodeSymbology.Ean8, "9638507", e => e.Rotation = FieldRotation.Rotated90)));
        // ^BR would default to R, so an element that inherits ^FW must still say which orientation it means.
        Assert.Equal("^FO10,20^BRN,1,3,1,50^FD1234567890123^FS", Field(Base(BarcodeSymbology.DataBar, "1234567890123", e => e.Rotation = null)));
        // ^BD has no orientation parameter, so none is written even for a rotated element.
        Assert.Equal("^FO10,20^BD2,1,1^FD001840123456789^FS", Field(Base(BarcodeSymbology.MaxiCode, "001840123456789", e => e.Rotation = FieldRotation.Rotated90)));
    }

    [Fact]
    public void Max_code_control_characters_are_hex_escaped()
    {
        var e = Base(BarcodeSymbology.MaxiCode, "001840123456789\u001d96ABC\u001e\u0004", e => e.MaxiCodeMode = 2);
        string zpl = CoreTestSupport.Zpl(CoreTestSupport.NewDoc(e));
        Assert.Contains("^FH_^FD001840123456789_1D96ABC_1E_04^FS", zpl);
        var parsed = (BarcodeElement)ZplParser.Parse(zpl).Document.Elements.Single();
        Assert.Equal(e.Data, parsed.Data);
    }

    [Fact]
    public void Invalid_data_is_reported_and_the_element_left_out()
    {
        var result = ZplGenerator.Generate(CoreTestSupport.NewDoc(Base(BarcodeSymbology.Ean8, "12345")));
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == "Barcode.Ean8.Length");
        Assert.DoesNotContain("^B8", result.Zpl);
    }

    [Fact]
    public void Generation_is_deterministic_for_every_symbology()
    {
        foreach (var s in SymbSupport.Symbologies)
        {
            var doc = CoreTestSupport.NewDoc(SymbSupport.Variant(s));
            Assert.Equal(CoreTestSupport.Zpl(doc), CoreTestSupport.Zpl(doc));
        }
    }

    [Fact]
    public void Golden_label_with_every_new_symbology()
    {
        var doc = CoreTestSupport.NewDoc();
        int y = 10;
        foreach (var s in SymbSupport.Symbologies.Where(s => s >= BarcodeSymbology.Code11))
        {
            var e = SymbSupport.Default(s);
            e.Y = y;
            e.X = 20;
            e.Rotation = FieldRotation.Normal;
            e.HumanReadable = true;
            y += 15;
            doc.Elements.Add(e);
        }
        CoreTestSupport.AssertGolden("barcode_symbologies_extra", CoreTestSupport.Zpl(doc));
    }

    // ---- import ---------------------------------------------------------------------------------

    private static LabelDocument Parse(string body, out IReadOnlyList<ZplImportWarning> warnings)
    {
        var r = ZplParser.Parse("^XA^PW800^LL400" + body + "^XZ");
        warnings = r.Warnings;
        return r.Document;
    }

    [Theory]
    [MemberData(nameof(AllSymbologies))]
    public void Default_and_variant_elements_round_trip_with_every_option(BarcodeSymbology s)
    {
        foreach (var original in new[] { SymbSupport.Default(s), SymbSupport.Variant(s) })
        {
            var doc = CoreTestSupport.NewDoc(original);
            string zpl = CoreTestSupport.Zpl(doc);
            var result = ZplParser.Parse(zpl);
            Assert.True(result.Warnings.Count == 0, zpl + "\n" + string.Join("; ", result.Warnings));
            var back = Assert.IsType<BarcodeElement>(Assert.Single(result.Document.Elements));
            var diffs = SymbSupport.Diff(original, back);
            Assert.True(diffs.Count == 0, zpl + "\n" + string.Join("\n", diffs));
            // generate(parse(generate(doc))) is stable text.
            Assert.Equal(zpl, CoreTestSupport.Zpl(result.Document));
        }
    }

    public static TheoryData<BarcodeSymbology> AllSymbologies => SymbSupport.AllSymbologies();

    [Fact]
    public void Omitted_parameters_take_the_defaults_of_the_zpl_guide()
    {
        var doc = Parse("^FO10,10^B1^FD123^FS^FO10,60^BS^FD12^FS^FO10,110^BZ^FD12345^FS^FO10,160^B5^FD12345678901^FS" +
                        "^FO10,210^BM^FD1234^FS^FO10,260^BK^FD123^FS^FO10,310^B9^FD1234500005^FS^FO10,360^BP^FD12^FS", out var warnings);
        Assert.Empty(warnings);
        var b = doc.Elements.Cast<BarcodeElement>().ToList();
        // ^B1: two check digits, interpretation line below
        Assert.Equal((2, true, HumanReadablePosition.Below), (b[0].Code11CheckDigitCount, b[0].HumanReadable, b[0].HumanReadablePosition));
        // ^BS: both switches default to yes
        Assert.Equal((true, HumanReadablePosition.Above), (b[1].HumanReadable, b[1].HumanReadablePosition));
        // ^BZ and ^B5: both default to no
        Assert.Equal(BarcodeSymbology.Postnet, b[2].Symbology);
        Assert.False(b[2].HumanReadable);
        Assert.Equal(BarcodeSymbology.Planet, b[3].Symbology);
        Assert.False(b[3].HumanReadable);
        // ^BM: one mod 10 check digit
        Assert.Equal(MsiCheckDigitMode.Mod10, b[4].MsiCheckDigit);
        // ^BK: start and stop default to A
        Assert.Equal(('A', 'A'), (b[5].CodabarStart, b[5].CodabarStop));
        // ^B9: the check digit is printed by default
        Assert.True(b[6].CheckDigit);
        // ^BP: no check digit by default, text on
        Assert.False(b[7].CheckDigit);
        Assert.True(b[7].HumanReadable);
    }

    [Fact]
    public void Data_bar_orientation_defaults_to_rotated_and_magnification_to_the_printer_resolution()
    {
        var doc = Parse("^FO10,10^BR,1,,,,^FD1234567890123^FS", out _);
        var b = Assert.IsType<BarcodeElement>(doc.Elements.Single());
        Assert.Equal(FieldRotation.Rotated90, b.Rotation);
        Assert.Equal(2, b.ModuleSize);   // 8 dots/mm printers
        Assert.Equal(25, b.Height);
        Assert.Equal(22, b.DataBarSegmentsPerRow);
        Assert.Equal(1, b.DataBarSeparatorHeight);
    }

    [Fact]
    public void By_sets_module_width_ratio_and_default_height_for_the_commands_that_use_it()
    {
        var doc = Parse("^BY4,2.4,70^FO10,10^B8^FD9638507^FS^BY5,2.0,33^FO10,100^BF^FDHELLO^FS^FO10,200^BT^FD123456^FS^FO10,300^B4^FDABC^FS", out _);
        var ean = (BarcodeElement)doc.Elements[0];
        Assert.Equal((4, 2.4, 70), (ean.ModuleWidth, ean.WideBarRatio, ean.Height));
        // ^BF takes only the module width and the height from ^BY; its ratio is fixed, so the model keeps its own default.
        var micro = (BarcodeElement)doc.Elements[1];
        Assert.Equal((5, 3.0, 33), (micro.ModuleWidth, micro.WideBarRatio, micro.Height));
        // ^BT has its own widths, so ^BY does not reach it.
        var tlc = (BarcodeElement)doc.Elements[2];
        Assert.Equal((2, 2.0, 40, 2, 4), (tlc.ModuleWidth, tlc.WideBarRatio, tlc.Height, tlc.Tlc39MicroModuleWidth, tlc.Tlc39MicroRowHeight));
        Assert.Equal(33, ((BarcodeElement)doc.Elements[3]).Height);
    }

    [Fact]
    public void Postal_command_with_type_one_is_planet()
    {
        var doc = Parse("^FO10,10^BZN,40,Y,N,1^FD12345678901^FS", out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(BarcodeSymbology.Planet, ((BarcodeElement)doc.Elements.Single()).Symbology);
    }

    [Theory]
    [InlineData("^FO10,10^BRN,1,2,1,50^FD12345678901|composite data^FS", "Import.Barcode.DataBar.Composite")]
    [InlineData("^FO10,10^BRN,7,2,1,50^FD12345678901^FS", "Import.Barcode.DataBar.Type")]
    [InlineData("^FO10,10^BRN,12,2,1,50^FD12345678901^FS", "Import.Barcode.DataBar.Type")]
    [InlineData("^FO10,10^BZN,40,Y,N,3^FD01234567094987654321^FS", "Import.Barcode.Postal.Type")]
    public void Features_the_model_cannot_hold_are_kept_as_raw_zpl_with_a_warning(string zpl, string key)
    {
        var doc = Parse(zpl, out var warnings);
        var raw = Assert.IsType<RawZplElement>(Assert.Single(doc.Elements));
        Assert.True(raw.IsVisual);
        Assert.Equal(zpl, raw.Zpl.TrimEnd('\n'));
        Assert.Contains(warnings, w => w.MessageKey == key);
        // The raw field is written back unchanged.
        Assert.Contains(zpl, CoreTestSupport.Zpl(doc));
    }

    [Fact]
    public void Max_code_inherits_the_label_rotation_because_the_command_has_no_orientation()
    {
        var doc = Parse("^FWR^FO10,10^BD4^FDhello^FS", out _);
        var b = (BarcodeElement)doc.Elements.Single();
        Assert.Equal(4, b.MaxiCodeMode);
        Assert.Null(b.Rotation);
    }

    [Fact]
    public void Parameters_out_of_range_are_clamped_not_rejected()
    {
        var doc = Parse("^FO10,10^BO,99,,999,,40^FDx^FS^FO10,60^BF,,99^FDx^FS^FO10,110^BD9,12,0^FDx^FS", out _);
        var aztec = (BarcodeElement)doc.Elements[0];
        Assert.Equal(10, aztec.ModuleSize);
        Assert.Equal(0, aztec.AztecErrorControl);   // 999 is not a legal error control value
        Assert.Equal(26, aztec.AztecStructuredAppendCount);
        Assert.Equal(33, ((BarcodeElement)doc.Elements[1]).MicroPdf417Mode);
        var maxi = (BarcodeElement)doc.Elements[2];
        Assert.Equal((6, 1, 1), (maxi.MaxiCodeMode, maxi.MaxiCodeSymbolNumber, maxi.MaxiCodeSymbolTotal));
    }
}
