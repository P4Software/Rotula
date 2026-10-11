using Rotula.Core.Barcodes;
using Rotula.Core.Barcodes.QrContent;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using Rotula.Files;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>
/// Every ^BQ parameter (ZPL II Programming Guide p. 128-134: orientation a, model b, magnification c, error correction d, mask e, and the
/// ^FD header with data input and character mode), the sizing helpers of the QR options panel, and the persistence of the content spec.
/// </summary>
public class QrOptionsTests
{
    private static BarcodeElement Qr(string data, Action<BarcodeElement>? tweak = null)
    {
        var b = new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = data, X = 20, Y = 20, ModuleSize = 5 };
        tweak?.Invoke(b);
        return b;
    }

    private static string FieldOf(BarcodeElement b) => Zpl(NewDoc(b)).Split('\n').Single(l => l.Contains("^BQ", StringComparison.Ordinal));

    private static BarcodeElement Back(BarcodeElement b)
    {
        var parsed = ZplParser.Parse(Zpl(NewDoc(b)));
        Assert.False(parsed.HasWarnings, string.Join("; ", parsed.Warnings));
        return Assert.IsType<BarcodeElement>(Assert.Single(parsed.Document.Elements));
    }

    // ---- ZPL ------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultQr_WritesModelMagnificationAndHeader()
    {
        Assert.Equal("^FO20,20^BQ,2,5^FDQA,hello^FS", FieldOf(Qr("hello")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void Mask_IsTheFifthParameter(int mask) =>
        Assert.Equal($"^FO20,20^BQ,2,5,,{mask}^FDQA,hello^FS", FieldOf(Qr("hello", b => b.QrMask = mask)));

    [Theory]
    [InlineData(QrErrorCorrection.Low, 'L')]
    [InlineData(QrErrorCorrection.Medium, 'M')]
    [InlineData(QrErrorCorrection.Quartile, 'Q')]
    [InlineData(QrErrorCorrection.High, 'H')]
    public void ErrorCorrection_IsTheFirstLetterOfTheHeader(QrErrorCorrection level, char letter) =>
        Assert.Contains("^FD" + letter + "A,hello^FS", FieldOf(Qr("hello", b => b.QrErrorCorrection = level)));

    [Fact]
    public void ManualInput_WritesTheCharacterMode()
    {
        Assert.Contains("^FDQM,N0123^FS", FieldOf(Qr("0123", b => b.QrInputMode = QrInputMode.ManualNumeric)));
        Assert.Contains("^FDHM,AAC-42^FS", FieldOf(Qr("AC-42", b => { b.QrInputMode = QrInputMode.ManualAlphanumeric; b.QrErrorCorrection = QrErrorCorrection.High; })));
        // The byte count is four digits, counted in the bytes the printer receives: é is two bytes under ^CI28, so café is 5.
        Assert.Contains("^FDQM,B0005" + "caf_C3_A9^FS", FieldOf(Qr("café", b => b.QrInputMode = QrInputMode.ManualByte)));
        Assert.Contains("^FDQM,K", FieldOf(Qr("x", b => b.QrInputMode = QrInputMode.ManualKanji)));
    }

    [Theory]
    [InlineData(2, 5, 'Q', QrInputMode.Automatic, -1)]
    [InlineData(1, 3, 'L', QrInputMode.Automatic, 5)]
    [InlineData(2, 10, 'H', QrInputMode.ManualNumeric, 0)]
    [InlineData(2, 2, 'M', QrInputMode.ManualAlphanumeric, 7)]
    [InlineData(2, 4, 'Q', QrInputMode.ManualByte, 2)]
    public void EveryParameter_RoundTripsThroughTheImporter(int model, int size, char level, QrInputMode mode, int mask)
    {
        string data = mode switch { QrInputMode.ManualNumeric => "0123456", QrInputMode.ManualAlphanumeric => "AC-42 $%", _ => "plain text" };
        var original = Qr(data, b =>
        {
            b.QrModel = model; b.ModuleSize = size; b.QrMask = mask; b.QrInputMode = mode;
            b.QrErrorCorrection = level switch { 'L' => QrErrorCorrection.Low, 'M' => QrErrorCorrection.Medium, 'H' => QrErrorCorrection.High, _ => QrErrorCorrection.Quartile };
        });
        var back = Back(original);
        Assert.Equal(model, back.QrModel);
        Assert.Equal(size, back.ModuleSize);
        Assert.Equal(original.QrErrorCorrection, back.QrErrorCorrection);
        Assert.Equal(mask, back.QrMask);
        Assert.Equal(mode, back.QrInputMode);
        Assert.Equal(data, back.Data);
    }

    [Fact]
    public void TheMaskOfAnotherProgramsZpl_IsRead_AndOutOfRangeValuesAreIgnored()
    {
        var parsed = ZplParser.Parse("^XA^PW800^LL400^FO10,10^BQN,2,6,Q,3^FDQA,abc^FS^FO10,100^BQN,2,6,Q,9^FDQA,abc^FS^XZ");
        var elements = parsed.Document.Elements.OfType<BarcodeElement>().ToList();
        Assert.Equal(3, elements[0].QrMask);
        Assert.Equal(-1, elements[1].QrMask);
    }

    // ---- validation -------------------------------------------------------------------------------------

    private static List<string> Errors(BarcodeElement b) =>
        BarcodeValidator.Validate(b, b.Data).Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToList();

    [Fact]
    public void Validation_CoversMaskInputModeAndCapacity()
    {
        Assert.Empty(Errors(Qr("hello")));
        Assert.Contains("Barcode.Qr.Mask", Errors(Qr("hello", b => b.QrMask = 8)));
        Assert.Contains("Barcode.Qr.ManualNumeric", Errors(Qr("12a", b => b.QrInputMode = QrInputMode.ManualNumeric)));
        Assert.Contains("Barcode.Qr.ManualAlphanumeric", Errors(Qr("hello", b => b.QrInputMode = QrInputMode.ManualAlphanumeric)));   // lower case is not alphanumeric
        Assert.Empty(Errors(Qr("HELLO 12", b => b.QrInputMode = QrInputMode.ManualAlphanumeric)));
        // 2000 bytes fit at L (2953) and M (2331) but not at Q (1663).
        string data = new string('a', 2000);
        Assert.Contains("Barcode.Qr.Capacity", Errors(Qr(data, b => b.QrErrorCorrection = QrErrorCorrection.Quartile)));
        Assert.Empty(Errors(Qr(data, b => b.QrErrorCorrection = QrErrorCorrection.Medium)));
        Assert.Contains("Barcode.Qr.Capacity", Errors(Qr(new string('1', 1200), b => { b.QrModel = 1; b.QrErrorCorrection = QrErrorCorrection.Low; })));   // model 1 holds 1167 digits at most
        var issue = BarcodeValidator.Validate(Qr(data), data).Single(i => i.MessageKey == "Barcode.Qr.Capacity");
        Assert.Equal(new[] { "2000", "1663", "Quartile" }, issue.Args);
    }

    // ---- sizing helpers ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(7089, '1', QrErrorCorrection.Low, true)]
    [InlineData(7090, '1', QrErrorCorrection.Low, false)]
    [InlineData(5596, '1', QrErrorCorrection.Medium, true)]
    [InlineData(3993, '1', QrErrorCorrection.Quartile, true)]
    [InlineData(3994, '1', QrErrorCorrection.Quartile, false)]
    [InlineData(3057, '1', QrErrorCorrection.High, true)]
    [InlineData(4296, 'A', QrErrorCorrection.Low, true)]
    [InlineData(4297, 'A', QrErrorCorrection.Low, false)]
    [InlineData(3391, 'A', QrErrorCorrection.Medium, true)]
    [InlineData(2420, 'A', QrErrorCorrection.Quartile, true)]
    [InlineData(1852, 'A', QrErrorCorrection.High, true)]
    [InlineData(1853, 'A', QrErrorCorrection.High, false)]
    [InlineData(2953, 'a', QrErrorCorrection.Low, true)]
    [InlineData(2954, 'a', QrErrorCorrection.Low, false)]
    [InlineData(2331, 'a', QrErrorCorrection.Medium, true)]
    [InlineData(1663, 'a', QrErrorCorrection.Quartile, true)]
    [InlineData(1273, 'a', QrErrorCorrection.High, true)]
    [InlineData(1274, 'a', QrErrorCorrection.High, false)]
    public void Version40Capacity_MatchesTheStandard(int length, char filler, QrErrorCorrection level, bool fits)
    {
        var fit = QrSizing.Analyze(new string(filler, length), level);
        Assert.Equal(fits, fit.Fits);
        // These are the exact limits of the largest symbol, so the smallest one that holds them is version 40.
        if (fits) Assert.Equal(40, fit.Version);
    }

    [Fact]
    public void Version_IsTheSmallestSymbolThatHoldsThePayload()
    {
        // Version 1 at level L holds 41 digits, 25 alphanumeric characters or 17 bytes; version 2 holds 77, 47 and 32.
        Assert.Equal(1, QrSizing.Analyze(new string('1', 41), QrErrorCorrection.Low).Version);
        Assert.Equal(2, QrSizing.Analyze(new string('1', 42), QrErrorCorrection.Low).Version);
        Assert.Equal(1, QrSizing.Analyze(new string('A', 25), QrErrorCorrection.Low).Version);
        Assert.Equal(2, QrSizing.Analyze(new string('A', 26), QrErrorCorrection.Low).Version);
        Assert.Equal(1, QrSizing.Analyze(new string('a', 17), QrErrorCorrection.Low).Version);
        Assert.Equal(2, QrSizing.Analyze(new string('a', 18), QrErrorCorrection.Low).Version);
        // Higher correction needs a larger symbol for the same text: version 1 at H holds only 7 bytes.
        Assert.Equal(1, QrSizing.Analyze(new string('a', 7), QrErrorCorrection.High).Version);
        Assert.Equal(2, QrSizing.Analyze(new string('a', 8), QrErrorCorrection.High).Version);
        var fit = QrSizing.Analyze("https://example.com", QrErrorCorrection.Quartile);
        Assert.Equal(17 + 4 * fit.Version, fit.Modules);
        Assert.InRange(fit.UsedPercent, 0, 100);
        Assert.Equal(QrDataMode.Byte, fit.Mode);
        Assert.Equal(19, fit.Units);
        Assert.Equal(QrDataMode.Alphanumeric, QrSizing.ModeOf("HELLO 123"));
        Assert.Equal(QrDataMode.Numeric, QrSizing.ModeOf("0123"));
        // Bytes are counted as UTF-8: 5 two-byte characters are 10 bytes.
        Assert.Equal(10, QrSizing.Analyze("ñññññ", QrErrorCorrection.Low).Units);
    }

    [Fact]
    public void Model1_HasItsOwnSmallerLimits()
    {
        Assert.True(QrSizing.Analyze(new string('1', 1167), QrErrorCorrection.Low, 1).Fits);
        Assert.False(QrSizing.Analyze(new string('1', 1168), QrErrorCorrection.Low, 1).Fits);
        Assert.True(QrSizing.Analyze(new string('a', 486), QrErrorCorrection.Low, 1).Fits);
        Assert.False(QrSizing.Analyze(new string('a', 487), QrErrorCorrection.Low, 1).Fits);
    }

    [Fact]
    public void LongestRecord_IsTheOneThatNeedsTheMostBits()
    {
        Assert.Equal("a much longer text", QrSizing.Longest(new[] { "short", "a much longer text", "12345678901234567890123" }));
        Assert.Null(QrSizing.Longest(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(203, 2)]     // 0.25 mm is 2.0 dots at 203 dpi
    [InlineData(300, 3)]     // 2.95 dots
    [InlineData(600, 6)]     // 5.9 dots
    [InlineData(152, 2)]     // 1.5 dots
    [InlineData(100, 1)]
    public void MinimumModule_IsWholeDotsFor0_25mm(int dpi, int dots) => Assert.Equal(dots, QrSizing.MinimumModuleDots(dpi));

    [Fact]
    public void MinimumModule_ThresholdIsConfigurable() => Assert.Equal(4, QrSizing.MinimumModuleDots(203, 0.5));

    [Fact]
    public void SymbolSize_InMillimetres()
    {
        // version 5: 37 modules; 3 dots of 1/300 inch: 37 x 3 x 25.4 / 300 = 9.398 mm
        Assert.Equal(9.398, QrSizing.SymbolSizeMm(5, 3, 300), 3);
        Assert.Equal(21 * 2 * 25.4 / 203, QrSizing.SymbolSizeMm(1, 2, 203), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => QrSizing.SymbolSizeMm(41, 2, 203));
    }

    [Fact]
    public void ErrorCorrectionSuggestion_FollowsThePayloadLength()
    {
        Assert.Equal(QrErrorCorrection.High, QrSizing.SuggestErrorCorrection("HELLO"));
        Assert.Equal(QrErrorCorrection.High, QrSizing.SuggestErrorCorrection("https://example.com/a"));
        // 1200 bytes: every higher level needs a symbol more than two versions larger than level L does
        Assert.Equal(QrErrorCorrection.Low, QrSizing.SuggestErrorCorrection(new string('a', 1200)));
        Assert.Equal(QrErrorCorrection.Low, QrSizing.SuggestErrorCorrection(new string('a', 5000)));   // does not fit at all
        Assert.True(QrSizing.SuggestErrorCorrection(new string('a', 300)) >= QrErrorCorrection.Medium);
    }

    [Fact]
    public void TheChoicesForThePanel_AreData()
    {
        Assert.Equal(new[] { 7, 15, 25, 30 }, QrSizing.ErrorCorrectionLevels.Select(l => l.Detail));
        Assert.Equal(new[] { "L - Low (7%)", "M - Medium (15%)", "Q - Quartile (25%)", "H - High (30%)" }, QrSizing.ErrorCorrectionLevels.Select(l => l.English));
        Assert.All(QrSizing.ErrorCorrectionLevels, l => Assert.True(Enum.TryParse<QrErrorCorrection>(l.Value, out _)));
        Assert.Equal(new[] { "-1", "0", "1", "2", "3", "4", "5", "6", "7" }, QrSizing.Masks.Select(m => m.Value));
        Assert.Equal(Enum.GetNames<QrInputMode>(), QrSizing.InputModes.Select(m => m.Value));
        Assert.Equal(new[] { "1", "2" }, QrSizing.Models.Select(m => m.Value));
        Assert.All(QrSizing.ErrorCorrectionLevels.Concat(QrSizing.Masks).Concat(QrSizing.InputModes).Concat(QrSizing.Models), c => Assert.StartsWith("Qr.", c.LabelKey));
        var options = BarcodeCatalog.Get(BarcodeSymbology.QrCode).Options;
        Assert.All(new[] { BarcodeOption.QrModel, BarcodeOption.ModuleSize, BarcodeOption.QrErrorCorrection, BarcodeOption.QrMask, BarcodeOption.QrInputMode, BarcodeOption.DigitalLink, BarcodeOption.QrContentSpec },
            o => Assert.Contains(o, options));
    }

    // ---- persistence ------------------------------------------------------------------------------------

    [Fact]
    public void TheSpecAndTheQrOptions_SurviveExtraOptions()
    {
        var spec = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{first}}"), ("lastName", "Pérez, Jr"));
        var original = Qr(QrContentBuilder.BuildTemplate(spec), b =>
        {
            b.QrContentSpec = spec.ToJson(); b.QrMask = 4; b.QrInputMode = QrInputMode.ManualByte; b.QrErrorCorrection = QrErrorCorrection.High;
        });
        var extras = original.ExportExtraOptions();
        Assert.Equal("4", extras["QrMask"]);
        Assert.Equal("ManualByte", extras["QrInputMode"]);
        Assert.Equal(spec.ToJson(), extras["QrContentSpec"]);
        var copy = new BarcodeElement();
        copy.ImportExtraOptions(extras);
        Assert.Equal(original.QrContentSpec, copy.QrContentSpec);
        Assert.Equal(4, copy.QrMask);
        Assert.True(QrContentSpec.TryParse(copy.QrContentSpec, out var spec2));
        Assert.Equal("{{first}}", spec2.Get("firstName"));
    }

    [Fact]
    public void TheQrOptions_SurviveTheRotulaFile()
    {
        var original = Qr("hello", b => { b.QrMask = 4; b.QrInputMode = QrInputMode.ManualByte; b.DigitalLink = true; });
        var back = (BarcodeElement)RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(NewDoc(original))).Elements[0];
        Assert.Equal(4, back.QrMask);
        Assert.Equal(QrInputMode.ManualByte, back.QrInputMode);
        Assert.True(back.DigitalLink);
        // A short definition fits the 64 characters the file format allows for one extra option today.
        var spec = QrContentSpec.Create(QrContentKind.Phone, ("number", "{{p}}"));
        Assert.True(spec.ToJson().Length <= 64);
        original.QrContentSpec = spec.ToJson();
        var withSpec = (BarcodeElement)RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(NewDoc(original))).Elements[0];
        Assert.Equal(spec.ToJson(), withSpec.QrContentSpec);
    }

    [Fact]
    public void ALongContentSpec_SurvivesTheRotulaFile()
    {
        var spec = QrContentSpec.Create(QrContentKind.VCard, ("firstName", "{{first}}"), ("lastName", "{{last}}"), ("organization", "Rotula"), ("email", "{{email}}"));
        var original = Qr(QrContentBuilder.BuildTemplate(spec), b => b.QrContentSpec = spec.ToJson());
        var back = (BarcodeElement)RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(NewDoc(original))).Elements[0];
        Assert.Equal(original.QrContentSpec, back.QrContentSpec);
    }

    [Fact]
    public void AHostileSpecInAFile_IsIgnoredWithoutFailing()
    {
        var b = new BarcodeElement();
        b.ImportExtraOptions(new Dictionary<string, string> { ["QrContentSpec"] = new string('x', 30000), ["QrMask"] = "banana", ["QrInputMode"] = "7" });
        Assert.Equal(string.Empty, b.QrContentSpec);
        Assert.Equal(-1, b.QrMask);
        Assert.Equal(QrInputMode.Automatic, b.QrInputMode);
        // A spec that is not valid JSON is kept as text but never used: the element prints its Data.
        b.ImportExtraOptions(new Dictionary<string, string> { ["QrContentSpec"] = "not json" });
        b.Symbology = BarcodeSymbology.QrCode;
        b.Data = "fallback";
        var doc = NewDoc(b);
        b.X = 10; b.Y = 10;
        Assert.Contains("^FDQA,fallback^FS", Zpl(doc));
    }
}
