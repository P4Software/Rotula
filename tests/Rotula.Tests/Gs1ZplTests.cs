using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>
/// GS1 in ZPL: GS1-128 through ^BC mode D, GS1 DataMatrix through ^BX with FNC1 escapes, GS1 DataBar Expanded through ^BR, and Digital Link
/// in plain QR Codes. The command forms follow the ZPL II Programming Guide (^BC modes, p. 94-105; ^BX field data, p. 146-147; ^FH, p. 193).
/// </summary>
public class Gs1ZplTests
{
    private static BarcodeElement Gs128(string data, Action<BarcodeElement>? tweak = null)
    {
        var b = new BarcodeElement { Symbology = BarcodeSymbology.Code128, Code128Mode = Code128Mode.Ean, Data = data, X = 50, Y = 50 };
        tweak?.Invoke(b);
        return b;
    }

    private static BarcodeElement Dm(string data, Action<BarcodeElement>? tweak = null)
    {
        var b = new BarcodeElement { Symbology = BarcodeSymbology.DataMatrix, DataMatrixGs1 = true, Data = data, ModuleSize = 5, X = 50, Y = 50 };
        tweak?.Invoke(b);
        return b;
    }

    private static string Field(string zpl) => zpl.Split('\n').Single(l => l.Contains("^FO", StringComparison.Ordinal));

    private static List<string> Errors(BarcodeElement b) =>
        BarcodeValidator.Validate(b, b.Data).Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToList();

    // ---- GS1-128 -----------------------------------------------------------------------------------

    [Fact]
    public void Gs128_UsesModeD_WithTheParenthesesKept()
    {
        string zpl = Zpl(NewDoc(Gs128("(01)09506000134352(17)251231(10)ABC123")));
        AssertGolden("gs1_gs128", zpl);
        // Mode D is the sixth ^BC parameter. All the elements before the last have a predefined length, so no >8 is written.
        Assert.Contains("^BC,100,Y,N,N,D^FD(01)09506000134352(17)251231(10)ABC123^FS", zpl);
    }

    [Fact]
    public void Gs128_WritesFnc1AfterEachVariableLengthElement_ThatIsNotLast()
    {
        // The guide's own sample (^BC...D): ^FD(91)0005886>8(10)0000410549>8(99)05
        string zpl = Zpl(NewDoc(Gs128("(91)0005886(10)0000410549(99)05")));
        Assert.Contains("^FD(91)0005886>8(10)0000410549>8(99)05^FS", zpl);
        AssertGolden("gs1_gs128_variable", Zpl(NewDoc(Gs128("(01)09506000134352(10)LOT1(21)SER9(17)251231", b => { b.ModuleWidth = 3; b.Height = 120; b.HumanReadablePosition = HumanReadablePosition.Above; }))));
    }

    [Fact]
    public void Gs128_EscapesAGreaterThanSignInsideAValue()
    {
        string zpl = Zpl(NewDoc(Gs128("(01)09506000134352(10)A>B")));
        Assert.Contains("(10)A><B^FS", zpl);
    }

    [Fact]
    public void Gs128_HexEscapesUnderscoresOnly_BecauseTheUnderscoreIsInTheGs1CharacterSet()
    {
        string zpl = Zpl(NewDoc(Gs128("(01)09506000134352(10)A_B")));
        Assert.Contains("^FH_^FD(01)09506000134352(10)A_5FB^FS", zpl);
    }

    [Theory]
    [InlineData("(01)09506000134352(10)ABC123(17)251231")]
    [InlineData("(91)0005886(10)0000410549(99)05")]
    [InlineData("(10)A>B(21)X")]
    [InlineData("(00)376104250021234569")]
    [InlineData("(01)09506000134352(3103)001234(10)A_B")]
    public void Gs128_RoundTripsThroughTheImporter(string data)
    {
        var original = Gs128(data, b => { b.HumanReadable = true; b.Height = 90; b.ModuleWidth = 2; });
        string zpl = Zpl(NewDoc(original));
        var parsed = ZplParser.Parse(zpl);
        Assert.False(parsed.HasWarnings, string.Join("; ", parsed.Warnings));
        var back = Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
        Assert.Equal(BarcodeSymbology.Code128, back.Symbology);
        Assert.Equal(Code128Mode.Ean, back.Code128Mode);
        Assert.Equal(data, back.Data);
        Assert.Equal(original.Height, back.Height);
        Assert.Equal(Zpl(NewDoc(back)), zpl);
    }

    [Fact]
    public void Gs128_ImportsTheGuidesSample()
    {
        var parsed = ZplParser.Parse("^XA^PW800^LL400^FO20,20^BY2,2.5,145^BCB,,Y,N,N,D^FD(91)0005886>8(10)0000410549>8(99)05^FS^XZ");
        Assert.False(parsed.HasWarnings, string.Join("; ", parsed.Warnings));
        var b = Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
        Assert.Equal(Code128Mode.Ean, b.Code128Mode);
        Assert.Equal("(91)0005886(10)0000410549(99)05", b.Data);
    }

    [Theory]
    [InlineData("(01)09506000134352>8(10)A")]   // FNC1 after an element of predefined length: the printer would encode it, Rotula cannot
    [InlineData("(10)A(21)B")]                    // a variable-length element without the FNC1 the guide asks for
    [InlineData("(10)A B")]                        // a space, which mode D drops
    public void Gs128_DataTheRoundTripCannotKeepExactly_StaysRawZpl(string fieldData)
    {
        var parsed = ZplParser.Parse($"^XA^PW800^LL400^FO20,20^BCN,100,Y,N,N,D^FD{fieldData}^FS^XZ");
        Assert.Contains(parsed.Warnings, w => w.MessageKey == "Import.Barcode.Gs1.Data");
        Assert.IsType<RawZplElement>(Assert.Single(parsed.Document.Elements));
    }

    [Fact]
    public void ModeD_WithOrdinaryData_KeepsItsOldMeaning()
    {
        // Labels made before GS1 support: mode D (or U) with free text prints as before and is not held to GS1 rules.
        var b = Gs128("SKU-0042");
        Assert.Empty(BarcodeValidator.Validate(b, b.Data));
        string zpl = Zpl(NewDoc(b));
        Assert.Contains("^BC,100,Y,N,N,D^FDSKU-0042^FS", zpl);
        var back = (BarcodeElement)ZplParser.Parse(zpl).Document.Elements[0];
        Assert.Equal("SKU-0042", back.Data);
        // Parentheses without mode D are plain Code 128 text again.
        var plain = Gs128("(01)09506000134352", x => x.Code128Mode = Code128Mode.Automatic);
        Assert.Empty(BarcodeValidator.Validate(plain, plain.Data));
    }

    [Theory]
    [InlineData("(01)09506000134353", "Gs1.Lint.CheckDigit")]
    [InlineData("(01)09506000134352(17)251332", "Gs1.Lint.DateMonth")]
    [InlineData("(01)09506000134352(0099)1", "Gs1.Ai.Unknown")]
    [InlineData("(10)AB(CD", "Gs1.Parse.Malformed")]
    [InlineData("(10)A(CD)E", "Gs1.Parse.Malformed")]               // a parenthesis inside a value looks like a new identifier
    [InlineData("(00)376104250021234569(10)123456789012345678(11)250504", "Gs1.Gs128.TooLong")]   // 49 data characters
    public void Gs128_BadDataIsAnErrorAndNothingIsPrinted(string data, string key)
    {
        var b = Gs128(data);
        Assert.Contains(key, Errors(b));
        var result = ZplGenerator.Generate(NewDoc(b));
        Assert.True(result.HasErrors);
        Assert.DoesNotContain("^BC", result.Zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void Gs128_RejectsParenthesesInsideValues_AndWarnsAboutTheCheckDigitOption()
    {
        Assert.Contains("Gs1.Gs128.Parentheses", Errors(Gs128("(10)A\\(B")));
        var issues = BarcodeValidator.Validate(Gs128("(01)09506000134352", b => b.CheckDigit = true), "(01)09506000134352");
        Assert.Contains(issues, i => i.MessageKey == "Gs1.Gs128.CheckDigitOption" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Gs128_48CharactersAreEnough_49AreNot()
    {
        // 5.4.4.3: 20 + (2 + 17) + 1 separator + 8 = 48 data characters.
        Assert.Empty(Errors(Gs128("(00)376104250021234569(10)12345678901234567(11)250504")));
    }

    // ---- GS1 DataMatrix ----------------------------------------------------------------------------

    [Fact]
    public void Dm_WritesFnc1FirstAndTheEscapeCharacterParameter()
    {
        string zpl = Zpl(NewDoc(Dm("(01)09506000134352(10)ABC123")));
        AssertGolden("gs1_datamatrix", zpl);
        // ^BX o,h,s,c,r,f,g: orientation default, height 5, quality 200, no columns/rows/format, escape character #.
        // #1 is FNC1; the symbol starts with one, which marks it as GS1.
        Assert.Contains("^BX,5,200,,,,#^FD#10109506000134352" + "10ABC123^FS", zpl);
    }

    [Fact]
    public void Dm_WritesFnc1BetweenVariableLengthElements()
    {
        string zpl = Zpl(NewDoc(Dm("(01)09506000134352(10)LOT1(21)S9(17)251231")));
        Assert.Contains("^FD#1" + "0109506000134352" + "10LOT1#1" + "21S9#1" + "17251231^FS", zpl);
    }

    [Fact]
    public void Dm_WithoutTheGs1Option_IsUnchanged()
    {
        string zpl = Zpl(NewDoc(Dm("ROTULA 123", b => b.DataMatrixGs1 = false)));
        Assert.Contains("^BX,5,200^FDROTULA 123^FS", zpl);
    }

    [Theory]
    [InlineData("(01)09506000134352(10)ABC123")]
    [InlineData("(01)09506000134352(10)LOT1(21)S9(17)251231")]
    [InlineData("(00)376104250021234569(37)12")]
    public void Dm_RoundTripsThroughTheImporter(string data)
    {
        var original = Dm(data, b => b.Rotation = FieldRotation.Rotated90);
        string zpl = Zpl(NewDoc(original));
        var parsed = ZplParser.Parse(zpl);
        Assert.False(parsed.HasWarnings, string.Join("; ", parsed.Warnings));
        var back = Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
        Assert.True(back.DataMatrixGs1);
        Assert.Equal(data, back.Data);
        Assert.Equal(5, back.ModuleSize);
        Assert.Equal(Zpl(NewDoc(back)), zpl);
    }

    [Fact]
    public void Dm_ImportKeepsPlainTildeDataPlain()
    {
        // A tilde sequence that is not exactly a GS1 pattern (here ~d029, a decimal code word) is not touched.
        var parsed = ZplParser.Parse("^XA^PW800^LL400^FO10,10^BXN,5,200,,,,#^FD#1abc#d029^FS^XZ");
        var b = Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
        Assert.False(b.DataMatrixGs1);
    }

    [Fact]
    public void Dm_ImportReadsTheDefaultTildeEscapeToo()
    {
        // Another program's ZPL: no g parameter, so the escape character is the tilde (written through ^FH as _7E), FNC1 first and between.
        var parsed = ZplParser.Parse("^XA^PW800^LL400^FO10,10^BXN,5,200^FH_^FD_7E10109506000134352" + "10LOT1_7E1" + "17251231^FS^XZ");
        Assert.False(parsed.HasWarnings, string.Join("; ", parsed.Warnings));
        var b = Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
        Assert.True(b.DataMatrixGs1);
        Assert.Equal("(01)09506000134352(10)LOT1(17)251231", b.Data);
    }

    [Fact]
    public void Dm_UnderscoreInAValue_GoesThroughHex()
    {
        string zpl = Zpl(NewDoc(Dm("(01)09506000134352(10)A_B")));
        Assert.Contains("^BX,5,200,,,,#^FH_^FD#10109506000134352" + "10A_5FB^FS", zpl);
        var b = (BarcodeElement)ZplParser.Parse(zpl).Document.Elements[0];
        Assert.Equal("(01)09506000134352(10)A_B", b.Data);
    }

    [Theory]
    [InlineData("(01)09506000134353", "Gs1.Lint.CheckDigit")]
    [InlineData("01095060001343", "Gs1.Parse.Malformed")]       // no parentheses
    [InlineData("(10)A#B", "Gs1.Lint.Cset82")]                   // the escape character is not in the GS1 character set
    public void Dm_BadDataIsAnError(string data, string key) => Assert.Contains(key, Errors(Dm(data)));

    [Fact]
    public void Dm_Gs1AndDigitalLinkTogether_AreContradictory() =>
        Assert.Contains("Gs1.Option.Conflict", Errors(Dm("(01)09506000134352", b => b.DigitalLink = true)));

    // ---- GS1 DataBar Expanded ----------------------------------------------------------------------

    [Fact]
    public void DataBarExpanded_IsCheckedAgainstTheDictionary()
    {
        BarcodeElement Make(string data) => new() { Symbology = BarcodeSymbology.DataBar, DataBarType = DataBarType.Expanded, Data = data, X = 10, Y = 10 };
        // 5.5.2.3.1 sample
        Assert.Empty(Errors(Make("(01)90614141000015(3202)000150")));
        Assert.Contains("Gs1.Lint.DateMonth", Errors(Make("(01)90614141000015(17)251399")));
        Assert.Contains("Gs1.Value.TooLong", Errors(Make("(01)90614141000015(10)ABCDEFGHIJKLMNOPQRSTU")));
        // The composite component is still not supported.
        Assert.Contains("Barcode.DataBar.Composite", Errors(Make("(01)90614141000015|(10)ABC")));
    }

    // ---- Digital Link in QR Code ---------------------------------------------------------------------

    [Fact]
    public void QrDigitalLink_IsCheckedButPrintedAsPlainText()
    {
        var good = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, DigitalLink = true, Data = "https://id.gs1.org/01/09506000134352/10/ABC123?17=251231", X = 20, Y = 20 };
        Assert.Empty(Errors(good));
        string zpl = Zpl(NewDoc(good));
        Assert.Contains("^BQ,2,4^FDQA,https://id.gs1.org/01/09506000134352/10/ABC123?17=251231^FS", zpl);

        var bad = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, DigitalLink = true, Data = "https://id.gs1.org/01/09506000134353", X = 20, Y = 20 };
        Assert.Contains("Gs1.Lint.CheckDigit", Errors(bad));
        var notAUri = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, DigitalLink = true, Data = "hello" };
        Assert.Contains("Gs1.DigitalLink.Uri", Errors(notAUri));
        // Without the option nothing is checked
        bad.DigitalLink = false;
        Assert.Empty(Errors(bad));
    }

    // ---- catalogue, options, persistence -------------------------------------------------------------

    [Fact]
    public void TheCatalogue_ExposesTheGs1OptionRows()
    {
        Assert.Contains(BarcodeOption.DataMatrixGs1, BarcodeCatalog.Get(BarcodeSymbology.DataMatrix).Options);
        Assert.Contains(BarcodeOption.DigitalLink, BarcodeCatalog.Get(BarcodeSymbology.DataMatrix).Options);
        Assert.Contains(BarcodeOption.DigitalLink, BarcodeCatalog.Get(BarcodeSymbology.QrCode).Options);
        // GS1-128 needs no row of its own: the mode row of Code 128 (Ean) is the switch.
        Assert.Contains(BarcodeOption.Code128Mode, BarcodeCatalog.Get(BarcodeSymbology.Code128).Options);
        Assert.DoesNotContain(BarcodeOption.DataMatrixGs1, BarcodeCatalog.Get(BarcodeSymbology.QrCode).Options);
        Assert.All(new[] { BarcodeOption.DataMatrixGs1, BarcodeOption.DigitalLink }, o => Assert.Equal(BarcodeDotKind.NotDots, BarcodeCatalog.GetDotKind(o)));
    }

    [Fact]
    public void TheGs1Options_TravelThroughTheExtraOptionsMechanism()
    {
        Assert.Empty(new BarcodeElement().ExportExtraOptions());
        var b = new BarcodeElement { Symbology = BarcodeSymbology.DataMatrix, DataMatrixGs1 = true };
        var extras = b.ExportExtraOptions();
        Assert.Equal("true", extras["DataMatrixGs1"]);
        Assert.False(extras.ContainsKey("DigitalLink"));
        var copy = new BarcodeElement();
        copy.ImportExtraOptions(extras);
        Assert.True(copy.DataMatrixGs1);
        copy.ImportExtraOptions(new Dictionary<string, string> { ["DigitalLink"] = "true", ["DataMatrixGs1"] = "nope" });
        Assert.True(copy.DigitalLink);
        Assert.True(copy.DataMatrixGs1);   // an unreadable value leaves the setting alone
    }

    [Fact]
    public void ExampleData_PassesTheValidatorOfEverySymbology()
    {
        foreach (var info in BarcodeCatalog.All)
            Assert.DoesNotContain(BarcodeValidator.Validate(new BarcodeElement { Symbology = info.Symbology, Data = info.ExampleData }, info.ExampleData),
                i => i.Severity == ValidationSeverity.Error);
    }
}
