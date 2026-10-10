using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Tests;

/// <summary>Data and option rules of the symbologies added after the first nine.</summary>
public class SymbValidatorTests
{
    private static List<string> Keys(BarcodeElement e) => BarcodeValidator.Validate(e, e.Data).Select(i => i.MessageKey).ToList();

    private static BarcodeElement Make(BarcodeSymbology s, string data, Action<BarcodeElement>? tweak = null)
    {
        var e = SymbSupport.Default(s);
        e.Data = data;
        tweak?.Invoke(e);
        return e;
    }

    [Theory]
    // Code 11: digits and hyphen
    [InlineData(BarcodeSymbology.Code11, "123-45", null)]
    [InlineData(BarcodeSymbology.Code11, "12A", "Barcode.Code11.Charset")]
    // Industrial / Standard 2 of 5: digits, any length
    [InlineData(BarcodeSymbology.Industrial2Of5, "123", null)]
    [InlineData(BarcodeSymbology.Industrial2Of5, "12X", "Barcode.Digits.Only")]
    [InlineData(BarcodeSymbology.Standard2Of5, "1234567", null)]
    [InlineData(BarcodeSymbology.Standard2Of5, "12 3", "Barcode.Digits.Only")]
    // LOGMARS accepts lower case because ZPL converts it
    [InlineData(BarcodeSymbology.Logmars, "abc-123", null)]
    [InlineData(BarcodeSymbology.Logmars, "ABC_1", "Barcode.Logmars.Charset")]
    // Codabar data has no start/stop letters
    [InlineData(BarcodeSymbology.Codabar, "12-34$5:6/7.8+9", null)]
    [InlineData(BarcodeSymbology.Codabar, "A123A", "Barcode.Codabar.Charset")]
    // MSI: 14 digits with check digits, 13 without
    [InlineData(BarcodeSymbology.Msi, "12345678901234", null)]
    [InlineData(BarcodeSymbology.Msi, "123456789012345", "Barcode.Msi.Length")]
    // Plessey is hexadecimal
    [InlineData(BarcodeSymbology.Plessey, "12ABCDEF", null)]
    [InlineData(BarcodeSymbology.Plessey, "12G", "Barcode.Plessey.Charset")]
    [InlineData(BarcodeSymbology.Plessey, "12ab", "Barcode.Plessey.Charset")]
    // EAN-8: 7 digits, or 8 with the right check digit (9638507 -> 4)
    [InlineData(BarcodeSymbology.Ean8, "9638507", null)]
    [InlineData(BarcodeSymbology.Ean8, "96385074", null)]
    [InlineData(BarcodeSymbology.Ean8, "96385075", "Barcode.Ean8.CheckDigit")]
    [InlineData(BarcodeSymbology.Ean8, "963850", "Barcode.Ean8.Length")]
    [InlineData(BarcodeSymbology.Ean8, "96385A7", "Barcode.Digits.Only")]
    // UPC-E: ZPL's 10-digit form, the 6-digit body, and the check-digit spellings
    [InlineData(BarcodeSymbology.UpcE, "1234500005", null)]
    [InlineData(BarcodeSymbology.UpcE, "123455", null)]
    [InlineData(BarcodeSymbology.UpcE, "0123455", null)]
    [InlineData(BarcodeSymbology.UpcE, "1234500006", null)]
    [InlineData(BarcodeSymbology.UpcE, "1234500004", "Barcode.UpcE.NotCompressible")]
    [InlineData(BarcodeSymbology.UpcE, "1123455", "Barcode.UpcE.NumberSystem")]
    [InlineData(BarcodeSymbology.UpcE, "12345", "Barcode.UpcE.Length")]
    // a body whose last digit contradicts the others would print as a different symbol (120003 expands to 1200000000 and prints 120030)
    [InlineData(BarcodeSymbology.UpcE, "120003", "Barcode.UpcE.NotCompressible")]
    [InlineData(BarcodeSymbology.UpcE, "0120003", "Barcode.UpcE.NotCompressible")]
    [InlineData(BarcodeSymbology.UpcE, "120030", null)]
    // Add-ons are exactly 2 or 5 digits
    [InlineData(BarcodeSymbology.UpcEanExtension, "12", null)]
    [InlineData(BarcodeSymbology.UpcEanExtension, "12345", null)]
    [InlineData(BarcodeSymbology.UpcEanExtension, "123", "Barcode.UpcEanExtension.Length")]
    // POSTNET 5/9/11, PLANET 11/13
    [InlineData(BarcodeSymbology.Postnet, "12345", null)]
    [InlineData(BarcodeSymbology.Postnet, "123456789", null)]
    [InlineData(BarcodeSymbology.Postnet, "12345678901", null)]
    [InlineData(BarcodeSymbology.Postnet, "1234", "Barcode.Postnet.Length")]
    [InlineData(BarcodeSymbology.Planet, "12345678901", null)]
    [InlineData(BarcodeSymbology.Planet, "1234567890123", null)]
    [InlineData(BarcodeSymbology.Planet, "12345", "Barcode.Planet.Length")]
    // GS1 DataBar, non-expanded: 13 digits or 14 with a correct check digit (1234567890123 -> 1)
    [InlineData(BarcodeSymbology.DataBar, "1234567890123", null)]
    [InlineData(BarcodeSymbology.DataBar, "12345678901231", null)]
    [InlineData(BarcodeSymbology.DataBar, "12345678901238", "Barcode.DataBar.CheckDigit")]
    [InlineData(BarcodeSymbology.DataBar, "123456789012345", "Barcode.DataBar.Length")]
    [InlineData(BarcodeSymbology.DataBar, "12345678901", null)]
    [InlineData(BarcodeSymbology.DataBar, "1234567890123|composite", "Barcode.DataBar.Composite")]
    // Code 49 and Codablock use printable ASCII
    [InlineData(BarcodeSymbology.Code49, "ABC 123", null)]
    [InlineData(BarcodeSymbology.Code49, "café", "Barcode.Code49.Charset")]
    [InlineData(BarcodeSymbology.Codablock, "Hello 123", null)]
    [InlineData(BarcodeSymbology.Codablock, "tab\there", "Barcode.Codablock.Charset128")]
    // TLC39: 6-digit ECI, optional comma separated alphanumeric fields
    [InlineData(BarcodeSymbology.Tlc39, "123456", null)]
    [InlineData(BarcodeSymbology.Tlc39, "123456,ABCD12345678901234,5551212,888999", null)]
    [InlineData(BarcodeSymbology.Tlc39, "12345", "Barcode.Tlc39.Format")]
    [InlineData(BarcodeSymbology.Tlc39, "123456;ABC", "Barcode.Tlc39.Format")]
    [InlineData(BarcodeSymbology.Tlc39, "123456,AB-C", "Barcode.Tlc39.Fields")]
    public void Data_rules(BarcodeSymbology s, string data, string? expectedKey)
    {
        var keys = Keys(Make(s, data));
        if (expectedKey is null) Assert.Empty(keys);
        else Assert.Contains(expectedKey, keys);
    }

    [Fact]
    public void Msi_without_check_digits_allows_only_13_digits()
    {
        Assert.Contains("Barcode.Msi.Length", Keys(Make(BarcodeSymbology.Msi, "12345678901234", e => e.MsiCheckDigit = MsiCheckDigitMode.None)));
        Assert.Empty(Keys(Make(BarcodeSymbology.Msi, "1234567890123", e => e.MsiCheckDigit = MsiCheckDigitMode.None)));
    }

    [Fact]
    public void Upc_e_reports_the_expected_check_digit()
    {
        var issue = BarcodeValidator.Validate(Make(BarcodeSymbology.UpcE, "12345500"), "12345500").Single(i => i.MessageKey == "Barcode.UpcE.NumberSystem");
        Assert.Empty(issue.Args);
        // 0 + 123455 expands to 0 12345 00005, whose UPC-A check digit is 8, so 01234558 is right and 01234556 is wrong.
        Assert.Empty(Keys(Make(BarcodeSymbology.UpcE, "01234558")));
        var wrong = BarcodeValidator.Validate(Make(BarcodeSymbology.UpcE, "01234556"), "01234556").Single();
        Assert.Equal("Barcode.UpcE.CheckDigit", wrong.MessageKey);
        Assert.Equal(new[] { "8" }, wrong.Args);
    }

    [Fact]
    public void Data_bar_expanded_needs_a_well_formed_element_string_with_a_correct_gtin()
    {
        void Expect(string data, string? key)
        {
            var keys = Keys(Make(BarcodeSymbology.DataBar, data, e => e.DataBarType = DataBarType.Expanded));
            if (key is null) Assert.Empty(keys); else Assert.Contains(key, keys);
        }
        Expect("(01)90614141999999", null);
        Expect("(01)90614141999999(10)ABC123", null);
        Expect("(10)ABC123", null);
        Expect("0190614141999999", "Barcode.DataBar.ElementString");
        Expect("(01)90614141999997", "Barcode.DataBar.CheckDigit");
        Expect("(01)906141419999", "Barcode.DataBar.Gtin");
    }

    [Fact]
    public void Data_bar_expanded_checks_what_the_encoder_can_encode()
    {
        BarcodeElement Expanded(string data) => Make(BarcodeSymbology.DataBar, data, e => e.DataBarType = DataBarType.Expanded);
        Assert.Contains("Barcode.DataBar.Charset", Keys(Expanded("(10)AB#12")));
        Assert.Contains("Barcode.DataBar.Charset", Keys(Expanded("(10)café")));
        Assert.Contains("Barcode.DataBar.ElementString", Keys(Expanded("(10)AB(21)CD(")));
        Assert.Contains("Barcode.DataBar.TooLong", Keys(Expanded("(10)" + new string('A', 80))));
        Assert.Empty(Keys(Expanded("(10)" + new string('A', 20) + "(21)" + new string('B', 20) + "(240)" + new string('C', 20))));
        Assert.Empty(Keys(Expanded("(240)abc-DEF_9 x")));
    }

    [Fact]
    public void Data_bar_limited_range_is_checked_on_the_padded_value()
    {
        Assert.Empty(Keys(Make(BarcodeSymbology.DataBar, "234567890123", e => e.DataBarType = DataBarType.Limited)));
        Assert.Contains("Barcode.DataBar.LimitedRange", Keys(Make(BarcodeSymbology.DataBar, "2345678901234", e => e.DataBarType = DataBarType.Limited)));
    }

    [Fact]
    public void Aztec_structured_append_id_cannot_break_the_zpl()
    {
        foreach (var bad in new[] { "A,B", "A^B", "A~B", "A\nB", "café" })
            Assert.Contains("Barcode.Aztec.StructuredAppendIdChars", Keys(Make(BarcodeSymbology.Aztec, "x", e => { e.AztecStructuredAppendCount = 2; e.AztecStructuredAppendId = bad; })));
        Assert.Empty(Keys(Make(BarcodeSymbology.Aztec, "x", e => { e.AztecStructuredAppendCount = 2; e.AztecStructuredAppendId = "ID-42_a"; })));
    }

    [Fact]
    public void Settings_the_command_cannot_carry_give_warnings_not_errors()
    {
        var maxi = Make(BarcodeSymbology.MaxiCode, "001840123456789", e => e.Rotation = FieldRotation.Rotated90);
        var issue = Assert.Single(BarcodeValidator.Validate(maxi, maxi.Data));
        Assert.Equal((ValidationSeverity.Warning, "Barcode.MaxiCode.RotationIgnored"), (issue.Severity, issue.MessageKey));
        var logmars = Make(BarcodeSymbology.Logmars, "ABC", e => e.HumanReadable = false);
        Assert.Equal("Barcode.Logmars.TextAlways", Assert.Single(BarcodeValidator.Validate(logmars, logmars.Data)).MessageKey);
        // Warnings do not stop the ZPL from being generated.
        var result = Rotula.Core.Zpl.ZplGenerator.Generate(CoreTestSupport.NewDoc(maxi));
        Assert.False(result.HasErrors);
        Assert.Contains("^BD2,1,1", result.Zpl);
        Assert.False(BarcodeCatalog.Get(BarcodeSymbology.MaxiCode).SupportsRotation);
        Assert.All(BarcodeCatalog.All.Where(i => i.Symbology != BarcodeSymbology.MaxiCode), i => Assert.True(i.SupportsRotation));
    }

    [Fact]
    public void Data_bar_limited_only_encodes_values_below_two_trillion()
    {
        Assert.Empty(Keys(Make(BarcodeSymbology.DataBar, "1999999999999", e => e.DataBarType = DataBarType.Limited)));
        Assert.Contains("Barcode.DataBar.LimitedRange", Keys(Make(BarcodeSymbology.DataBar, "2000000000000", e => e.DataBarType = DataBarType.Limited)));
        Assert.Empty(Keys(Make(BarcodeSymbology.DataBar, "2000000000000", e => e.DataBarType = DataBarType.Omnidirectional)));
    }

    [Fact]
    public void Max_code_modes_two_and_three_need_the_structured_header()
    {
        Assert.Empty(Keys(Make(BarcodeSymbology.MaxiCode, "001840123456789")));
        Assert.Contains("Barcode.MaxiCode.Message", Keys(Make(BarcodeSymbology.MaxiCode, "00184012345")));
        Assert.Empty(Keys(Make(BarcodeSymbology.MaxiCode, "001840AB12CD tail", e => e.MaxiCodeMode = 3)));
        Assert.Contains("Barcode.MaxiCode.Message", Keys(Make(BarcodeSymbology.MaxiCode, "001840ab12cd", e => e.MaxiCodeMode = 3)));
        // Modes 4-6 take any text.
        Assert.Empty(Keys(Make(BarcodeSymbology.MaxiCode, "free text", e => e.MaxiCodeMode = 4)));
        Assert.Contains("Barcode.MaxiCode.Mode", Keys(Make(BarcodeSymbology.MaxiCode, "x", e => e.MaxiCodeMode = 7)));
        Assert.Contains("Barcode.MaxiCode.Symbol", Keys(Make(BarcodeSymbology.MaxiCode, "x", e => { e.MaxiCodeMode = 4; e.MaxiCodeSymbolNumber = 3; e.MaxiCodeSymbolTotal = 2; })));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(23, true)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    [InlineData(104, true)]
    [InlineData(105, false)]
    [InlineData(201, true)]
    [InlineData(232, true)]
    [InlineData(233, false)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Aztec_error_control_values(int value, bool valid)
    {
        var keys = Keys(Make(BarcodeSymbology.Aztec, "data", e => e.AztecErrorControl = value));
        Assert.Equal(valid, !keys.Contains("Barcode.Aztec.ErrorControl"));
    }

    [Fact]
    public void Option_ranges_are_checked()
    {
        Assert.Contains("Barcode.Aztec.StructuredAppend", Keys(Make(BarcodeSymbology.Aztec, "x", e => e.AztecStructuredAppendCount = 27)));
        Assert.Contains("Barcode.Aztec.StructuredAppendId", Keys(Make(BarcodeSymbology.Aztec, "x", e => e.AztecStructuredAppendId = new string('A', 25))));
        Assert.Contains("Barcode.ModuleSize.Range", Keys(Make(BarcodeSymbology.Aztec, "x", e => e.ModuleSize = 11)));
        Assert.Contains("Barcode.MicroPdf417.Mode", Keys(Make(BarcodeSymbology.MicroPdf417, "x", e => e.MicroPdf417Mode = 34)));
        Assert.Contains("Barcode.Codabar.StartStop", Keys(Make(BarcodeSymbology.Codabar, "123", e => e.CodabarStart = 'E')));
        Assert.Contains("Barcode.Code11.CheckDigits", Keys(Make(BarcodeSymbology.Code11, "123", e => e.Code11CheckDigitCount = 3)));
        Assert.Contains("Barcode.DataBar.Segments", Keys(Make(BarcodeSymbology.DataBar, "1234567890123", e => e.DataBarSegmentsPerRow = 7)));
        Assert.Contains("Barcode.DataBar.Separator", Keys(Make(BarcodeSymbology.DataBar, "1234567890123", e => e.DataBarSeparatorHeight = 3)));
        Assert.Contains("Barcode.Codablock.Rows", Keys(Make(BarcodeSymbology.Codablock, "ABC", e => { e.CodablockMode = CodablockMode.F; e.Rows = 5; })));
        Assert.Empty(Keys(Make(BarcodeSymbology.Codablock, "ABC", e => { e.CodablockMode = CodablockMode.A; e.Rows = 22; })));
        Assert.Contains("Barcode.Codablock.Columns", Keys(Make(BarcodeSymbology.Codablock, "ABC", e => e.Columns = 63)));
        Assert.Contains("Barcode.Code49.StartMode", Keys(Make(BarcodeSymbology.Code49, "ABC", e => e.Code49StartMode = '6')));
        Assert.Contains("Barcode.Tlc39.MicroModuleWidth", Keys(Make(BarcodeSymbology.Tlc39, "123456", e => e.Tlc39MicroModuleWidth = 11)));
        Assert.Contains("Barcode.Tlc39.MicroRowHeight", Keys(Make(BarcodeSymbology.Tlc39, "123456", e => e.Tlc39MicroRowHeight = 256)));
        Assert.Contains("Barcode.Height.Range", Keys(Make(BarcodeSymbology.MicroPdf417, "x", e => e.Height = 10000)));
        Assert.Contains("Barcode.WideBarRatio.Range", Keys(Make(BarcodeSymbology.Code11, "1", e => e.WideBarRatio = 3.5)));
        Assert.Contains("Barcode.ModuleWidth.Range", Keys(Make(BarcodeSymbology.Postnet, "12345", e => e.ModuleWidth = 0)));
    }

    [Fact]
    public void Printer_normalisation_matches_what_zpl_expects()
    {
        Assert.Equal("9638507", BarcodeValidator.NormalizeForPrinter(BarcodeSymbology.Ean8, "96385074"));
        Assert.Equal("1234500005", BarcodeValidator.NormalizeForPrinter(BarcodeSymbology.UpcE, "123455"));
        Assert.Equal("1234500005", BarcodeValidator.NormalizeForPrinter(BarcodeSymbology.UpcE, "01234558"));
        var bar = Make(BarcodeSymbology.DataBar, "12345678901231");
        Assert.Equal("1234567890123", BarcodeValidator.NormalizeForPrinter(bar, bar.Data));
        bar.DataBarType = DataBarType.Expanded;
        Assert.Equal("12345678901231", BarcodeValidator.NormalizeForPrinter(bar, bar.Data));
    }

    // ---- check digit algorithms against published examples -------------------------------------

    [Fact]
    public void Mod43_matches_the_zpl_guide_example()
    {
        // The ZPL II Programming Guide sums 1+2+3+4+5+10+11+12+13+14+40 = 115 (the digit string of its example is 12345ABCDE/),
        // 115 mod 43 = 29, and the character with value 29 is T.
        Assert.Equal('T', CheckDigits.Mod43("12345ABCDE/"));
        Assert.Null(CheckDigits.Mod43("abc_"));
    }

    [Fact]
    public void Upc_e_zero_suppression_follows_the_upc_rules()
    {
        // (manufacturer + product) -> UPC-E body, one case per rule of the ZPL guide's product code table.
        Assert.Equal("120030", UpcE.Compress("1200000003"));  // mfr ends 000, product 00000-00999
        Assert.Equal("123453", UpcE.Compress("1230000045"));  // mfr ends 300, product 00000-00099
        Assert.Equal("123454", UpcE.Compress("1234000005"));  // mfr ends 0 (10-90), product 00000-00009
        Assert.Equal("123455", UpcE.Compress("1234500005"));  // otherwise product 00005-00009
        Assert.Null(UpcE.Compress("1234500004"));
        Assert.Null(UpcE.Compress("1234500010"));
        // And back again.
        foreach (var body in new[] { "120030", "123453", "123454", "123455", "120001", "121002" })
            Assert.Equal(body, UpcE.Compress(UpcE.Expand(body)));
    }

    [Fact]
    public void Code_11_check_characters()
    {
        // Worked by hand from the weights (C: 1..10 from the right, K: 1..9 including C); '-' has the value 10.
        Assert.Equal("5", CheckDigits.Code11("123-45", 1));
        Assert.Equal("52", CheckDigits.Code11("123-45", 2));
        // A remainder of 10 is written as the hyphen.
        Assert.Equal("-", CheckDigits.Code11("-", 1));
    }

    [Fact]
    public void Msi_check_digits()
    {
        // 1234567 is the textbook IBM mod 10 example: the check digit is 4.
        Assert.Equal(4, CheckDigits.MsiMod10("1234567"));
        // Mod 11 (weights 2..7 from the right) of 1234567: 7*2+6*3+5*4+4*5+3*6+2*7+1*2 = 106, 106 mod 11 = 7, check = 4.
        Assert.Equal(4, CheckDigits.MsiMod11("1234567"));
    }
}
