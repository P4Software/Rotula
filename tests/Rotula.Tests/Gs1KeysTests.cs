using Rotula.Core.Gs1;

namespace Rotula.Tests;

/// <summary>Check digit vectors and the builders of GS1 identification keys (GS1 General Specifications release 26.0).</summary>
public class Gs1KeysTests
{
    [Theory]
    // 7.9.1 Table 7-9: the 17 digits 37610425002123456 get check digit 9
    [InlineData("37610425002123456", 9)]
    // GTIN-13 EAN sample, GTIN-12 UPC sample, GTIN-8 sample used throughout Rotula's symbology examples
    [InlineData("590123412345", 7)]
    [InlineData("03600029145", 2)]
    [InlineData("9638507", 4)]
    // 1.4.2 example GTIN and GLN
    [InlineData("0952123450000", 1)]
    [InlineData("952123450001", 8)]
    // 7.8.6.1 GTIN 95012345678903
    [InlineData("9501234567890", 3)]
    public void CheckDigit_MatchesTheSpecificationVectors(string payload, int expected)
    {
        Assert.Equal(expected, Gs1Keys.CheckDigit(payload));
        Assert.True(Gs1Keys.HasValidCheckDigit(payload + expected));
        Assert.False(Gs1Keys.HasValidCheckDigit(payload + (expected + 1) % 10));
    }

    [Fact]
    public void Sscc_IsBuiltFromExtensionCompanyPrefixAndSerial()
    {
        // Table 7-9: extension 3, company prefix 7610425, serial reference 002123456, check digit 9.
        Assert.Equal("376104250021234569", Gs1Keys.Sscc(3, "7610425", "2123456"));
        Assert.Equal("376104250021234569", Gs1Keys.Sscc(3, "7610425", 2123456L));
        var sscc = Gs1Keys.Sscc(0, "9521234", "1");
        Assert.Equal(18, sscc.Length);
        Assert.Equal("0952123400000000" + "1", sscc[..17]);
        Assert.True(Gs1Keys.HasValidCheckDigit(sscc));
        // 3.3.1: the SSCC element validates against the dictionary
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("00", sscc)));
    }

    [Theory]
    [InlineData(10, "7610425", "1", "Gs1.Key.Extension")]
    [InlineData(1, "761", "1", "Gs1.Key.CompanyPrefix")]            // a company prefix has at least 4 digits
    [InlineData(1, "76104250000000", "1", "Gs1.Key.CompanyPrefix")]   // and at most 12
    [InlineData(1, "7610425", "A", "Gs1.Key.Reference")]
    [InlineData(1, "7610425", "1234567890", "Gs1.Key.ReferenceTooLong")]  // 9 digits fit after a 7-digit prefix
    [InlineData(1, "761042500001", "12345", "Gs1.Key.ReferenceTooLong")]  // a 12-digit prefix leaves 4
    public void Sscc_RejectsWhatDoesNotFit(int extension, string prefix, string serial, string key)
    {
        var ex = Assert.Throws<Gs1KeyException>(() => Gs1Keys.Sscc(extension, prefix, serial));
        Assert.Equal(key, ex.MessageKey);
    }

    [Fact]
    public void Gln_AddsReferenceAndCheckDigit()
    {
        // 1.4.2: GLN 9521234500018 is company prefix 9521234 + location reference 50001 + check digit 8.
        Assert.Equal("9521234500018", Gs1Keys.Gln("9521234", "50001"));
        // The reference is padded with zeros on the left to fill 12 digits in all.
        Assert.Equal(Gs1Keys.Gln("9521234", "1"), Gs1Keys.Gln("9521234", "00001"));
    }

    [Fact]
    public void Gtin_CompletionAndConversion()
    {
        Assert.Equal("9521234500001", Gs1Keys.CompleteGtin("952123450000"));
        Assert.Equal("5901234123457", Gs1Keys.CompleteGtin("5901234123457"));    // already complete: unchanged
        Assert.Equal("96385074", Gs1Keys.CompleteGtin("9638507"));
        Assert.Equal("09521234500001", Gs1Keys.ToGtin14("9521234500001"));
        Assert.Equal("00000096385074", Gs1Keys.ToGtin14("96385074"));
        Assert.Equal("00036000291452", Gs1Keys.ToGtin14("036000291452"));
        Assert.Equal("Gs1.Key.CheckDigit", Assert.Throws<Gs1KeyException>(() => Gs1Keys.ToGtin14("9521234500002")).MessageKey);
        Assert.Equal("Gs1.Key.GtinLength", Assert.Throws<Gs1KeyException>(() => Gs1Keys.ToGtin14("12345")).MessageKey);
        Assert.Equal("Gs1.Key.Digits", Assert.Throws<Gs1KeyException>(() => Gs1Keys.CompleteGtin("12A")).MessageKey);
        Assert.True(Gs1Keys.IsValidGtin("5901234123457") && Gs1Keys.IsValidGtin("036000291452") && Gs1Keys.IsValidGtin("96385074") && Gs1Keys.IsValidGtin("09521234500001"));
        Assert.False(Gs1Keys.IsValidGtin("5901234123458"));
    }

    [Fact]
    public void Gtin14_ForACartonUsesTheIndicatorAndTheInnerItem()
    {
        // 2.1: GTIN-14 = indicator digit + the 12 first digits of the inner GTIN-13 + a new check digit.
        string carton = Gs1Keys.Gtin14FromInner(1, "5901234123457");
        Assert.Equal("1590123412345", carton[..13]);
        Assert.True(Gs1Keys.HasValidCheckDigit(carton));
        // A GTIN-12 inner item gets a leading zero first.
        string fromUpc = Gs1Keys.Gtin14FromInner(2, "036000291452");
        Assert.Equal("2003600029145", fromUpc[..13]);
        Assert.True(Gs1Keys.HasValidCheckDigit(fromUpc));
        Assert.Throws<Gs1KeyException>(() => Gs1Keys.Gtin14FromInner(10, "5901234123457"));
        Assert.Throws<Gs1KeyException>(() => Gs1Keys.Gtin14FromInner(1, "09521234500001"));   // a GTIN-14 cannot be nested
    }

    [Fact]
    public void OtherKeys_FollowTheirStructures()
    {
        // GSRN (8017/8018): 18 digits. GSIN (402): 17 digits. GRAI (8003): 0 + 13 + serial. GDTI (253): 13 + serial.
        string gsrn = Gs1Keys.Gsrn("9521234", "1234567890");
        Assert.Equal(18, gsrn.Length);
        Assert.True(Gs1Keys.HasValidCheckDigit(gsrn));
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("8017", gsrn)));

        string gsin = Gs1Keys.Gsin("9521234", "12345");
        Assert.Equal(17, gsin.Length);
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("402", gsin)));

        string grai = Gs1Keys.Grai("9521234", "1", "SER001");
        Assert.Equal("0", grai[..1]);
        Assert.Equal(14 + 6, grai.Length);
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("8003", grai)));

        string gdti = Gs1Keys.Gdti("9521234", "12", "A/B");
        Assert.Equal(13 + 3, gdti.Length);
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("253", gdti)));

        string giai = Gs1Keys.Giai("9521234", "ASSET-0001");
        Assert.Equal("9521234ASSET-0001", giai);
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("8004", giai)));
        Assert.Empty(Gs1Validator.ValidateElement(new Gs1Element("401", Gs1Keys.Ginc("9521234", "CONS1"))));

        Assert.Equal("Gs1.Key.Characters", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Giai("9521234", "A B")).MessageKey);
        Assert.Equal("Gs1.Key.ReferenceTooLong", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Grai("9521234", "1", new string('A', 17))).MessageKey);
    }

    [Theory]
    // 5.11.8 sample: net weight 1.234 kg is (3103)001234; 7.8.6.1: 4.00 kg is (3102)000400
    [InlineData("310", "1.234", 3, "3103", "001234")]
    [InlineData("310", "4.00", 2, "3102", "000400")]
    [InlineData("320", "0.5", 0, "3200", "000001")]     // 0.5 rounds away from zero to 1
    [InlineData("310", "999999", 0, "3100", "999999")]
    [InlineData("390", "12.5", 2, "3902", "1250")]      // an amount has a variable length: no zeros are added
    public void Measure_PutsTheDecimalPointIntoTheIdentifier(string prefix, string value, int decimals, string ai, string digits)
    {
        var element = Gs1Keys.Measure(prefix, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), decimals);
        Assert.Equal(new Gs1Element(ai, digits), element);
        Assert.Empty(Gs1Validator.ValidateElement(element));
    }

    [Fact]
    public void Measure_RejectsWhatDoesNotFit_AndCanBeReadBack()
    {
        Assert.Equal("Gs1.Key.MeasureRange", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Measure("310", 1_000_000m, 0)).MessageKey);
        Assert.Equal("Gs1.Key.MeasureRange", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Measure("310", -1m, 0)).MessageKey);
        Assert.Equal("Gs1.Key.NotMeasure", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Measure("310", 1m, 6)).MessageKey);   // 3106 does not exist
        Assert.Equal("Gs1.Key.NotMeasure", Assert.Throws<Gs1KeyException>(() => Gs1Keys.Measure("010", 1m, 1)).MessageKey);
        Assert.Equal(1.234m, Gs1Keys.ReadMeasure(new Gs1Element("3103", "001234")));
        Assert.Null(Gs1Keys.ReadMeasure(new Gs1Element("10", "ABC")));
    }

    [Fact]
    public void Itf14AndEan14_UseTheSameFourteenDigits()
    {
        Assert.Null(Gs1Itf14.Validate("09521234500001"));
        Assert.Equal("Gs1.Itf14.CheckDigit", Gs1Itf14.Validate("09521234500002")!.MessageKey);
        Assert.Equal("1", Gs1Itf14.Validate("09521234500002")!.Args[0]);
        Assert.Equal("Gs1.Itf14.Length", Gs1Itf14.Validate("9521234500001")!.MessageKey);
        Assert.Equal("09521234500001", Gs1Itf14.Data("9521234500001"));
        Assert.Equal("(01)09521234500001", Gs1Itf14.Ean14("9521234500001"));
        string carton = Gs1Itf14.Data(1, "9521234500001");
        Assert.Null(Gs1Itf14.Validate(carton));
        Assert.Equal('1', carton[0]);
    }

    [Fact]
    public void Composite_SplitsAndChecksBothComponents()
    {
        Assert.True(Gs1Composite.TrySplit("09521234500001|(17)251231(10)ABC", out var linear, out var twoD));
        Assert.Equal("09521234500001", linear);
        Assert.Equal("(17)251231(10)ABC", twoD);
        Assert.Equal("09521234500001|(17)251231(10)ABC", Gs1Composite.Join(linear, twoD));
        Assert.False(Gs1Composite.TrySplit("095212345000", out _, out _));

        Assert.DoesNotContain(Gs1Composite.Validate("09521234500001", "(17)251231(10)ABC"), i => i.Severity == Rotula.Core.Validation.ValidationSeverity.Error);
        // The GTIN is in the linear component, so the 2D component must not repeat AI 01.
        Assert.Contains(Gs1Composite.Validate("09521234500001", "(01)09521234500001(10)ABC"), i => i.MessageKey == "Gs1.Composite.RepeatedAi");
        Assert.Contains(Gs1Composite.Validate("09521234500002", "(10)ABC"), i => i.MessageKey == "Gs1.Composite.Linear");
        Assert.Contains(Gs1Composite.Validate("09521234500001", "(17)251399"), i => i.MessageKey == "Gs1.Lint.DateMonth");
        Assert.Contains(Gs1Composite.Validate("(01)09521234500001", "(01)09521234500001"), i => i.MessageKey == "Gs1.Composite.RepeatedAi");
    }
}
