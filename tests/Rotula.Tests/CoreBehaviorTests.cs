using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>Non-golden tests: units, validation, escaping, determinism, image processing.</summary>
public class CoreBehaviorTests
{
    [Theory]
    [InlineData(25.4, LengthUnit.Millimeters, PrinterDpi.Dpi203, 203)]
    [InlineData(100, LengthUnit.Millimeters, PrinterDpi.Dpi300, 1181)]
    [InlineData(4, LengthUnit.Inches, PrinterDpi.Dpi203, 812)]
    [InlineData(4, LengthUnit.Inches, PrinterDpi.Dpi600, 2400)]
    [InlineData(55, LengthUnit.Dots, PrinterDpi.Dpi600, 55)]
    public void MeasureConvertsToDots(double value, LengthUnit unit, PrinterDpi dpi, int expected) =>
        Assert.Equal(expected, new Measure(value, unit).ToDots(dpi));

    [Fact]
    public void CheckDigitsMatchKnownCodes()
    {
        Assert.Equal(1, CheckDigits.Gs1("400638133393"));
        Assert.Equal(2, CheckDigits.Gs1("03600029145"));
    }

    private static List<ValidationIssue> Validate(BarcodeSymbology s, string data, Action<BarcodeElement>? tweak = null)
    {
        var b = new BarcodeElement { Symbology = s, Data = data };
        tweak?.Invoke(b);
        return BarcodeValidator.Validate(b, data);
    }

    [Theory]
    [InlineData(BarcodeSymbology.Ean13, "4006381333931", true)]
    [InlineData(BarcodeSymbology.Ean13, "400638133393", true)]
    [InlineData(BarcodeSymbology.Ean13, "4006381333932", false)]
    [InlineData(BarcodeSymbology.Ean13, "40063813339", false)]
    [InlineData(BarcodeSymbology.Ean13, "40063813339A1", false)]
    [InlineData(BarcodeSymbology.UpcA, "036000291452", true)]
    [InlineData(BarcodeSymbology.UpcA, "03600029145", true)]
    [InlineData(BarcodeSymbology.UpcA, "036000291453", false)]
    [InlineData(BarcodeSymbology.Code39, "ABC-123", true)]
    [InlineData(BarcodeSymbology.Code39, "abc", false)]
    [InlineData(BarcodeSymbology.Code128, "Hello", true)]
    [InlineData(BarcodeSymbology.Code128, "a>b", false)]
    [InlineData(BarcodeSymbology.Code128, "ñ", false)]
    [InlineData(BarcodeSymbology.Code93, "HELLO", true)]
    [InlineData(BarcodeSymbology.Interleaved2Of5, "1234", true)]
    [InlineData(BarcodeSymbology.Interleaved2Of5, "123", false)]
    [InlineData(BarcodeSymbology.QrCode, "x", true)]
    [InlineData(BarcodeSymbology.DataMatrix, "x", true)]
    [InlineData(BarcodeSymbology.Pdf417, "x", true)]
    [InlineData(BarcodeSymbology.Code128, "", false)]
    public void ValidatorAcceptsAndRejects(BarcodeSymbology s, string data, bool valid) =>
        Assert.Equal(valid, Validate(s, data).Count == 0);

    [Fact]
    public void I25WithCheckDigitNeedsOddLength() =>
        Assert.Empty(Validate(BarcodeSymbology.Interleaved2Of5, "123", b => b.CheckDigit = true));

    [Fact]
    public void ValidatorReturnsMessageKeysNotText()
    {
        var issue = Assert.Single(Validate(BarcodeSymbology.Ean13, "4006381333932"));
        Assert.Equal("Barcode.Ean13.CheckDigit", issue.MessageKey);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal("1", Assert.Single(issue.Args));
        Assert.Equal("Barcode.Ean13.Length", Assert.Single(Validate(BarcodeSymbology.Ean13, "123")).MessageKey);
    }

    [Fact]
    public void OptionRangesAreValidated()
    {
        Assert.Contains(Validate(BarcodeSymbology.Code128, "A", b => b.ModuleWidth = 11), i => i.MessageKey == "Barcode.ModuleWidth.Range");
        Assert.Contains(Validate(BarcodeSymbology.Code128, "A", b => b.WideBarRatio = 5), i => i.MessageKey == "Barcode.WideBarRatio.Range");
        Assert.Contains(Validate(BarcodeSymbology.QrCode, "A", b => b.ModuleSize = 0), i => i.MessageKey == "Barcode.ModuleSize.Range");
        Assert.Contains(Validate(BarcodeSymbology.Pdf417, "A", b => b.Pdf417SecurityLevel = 9), i => i.MessageKey == "Barcode.Pdf417.Security");
    }

    [Fact]
    public void InvalidBarcodeIsOmittedAndReported()
    {
        var doc = NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.Ean13, Data = "123", X = 1, Y = 1 });
        var result = ZplGenerator.Generate(doc);
        Assert.True(result.HasErrors);
        Assert.DoesNotContain("^BE", result.Zpl);
        Assert.Throws<ZplValidationException>(() => result.GetZplOrThrow());
    }

    [Fact]
    public void MissingVariableWarnsAndBecomesEmpty()
    {
        var result = ZplGenerator.Generate(NewDoc(new TextElement { X = 1, Y = 1, Text = "A{{nope}}B" }));
        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == "Variable.Missing" && i.Args[0] == "nope");
        Assert.Contains("^FDAB^FS", result.Zpl);
    }

    [Fact]
    public void Utf8IsDefaultAndOutputIsAscii()
    {
        string zpl = Zpl(NewDoc(new TextElement { X = 1, Y = 1, Text = "ñ¿€" }));
        Assert.Contains("^CI28", zpl);
        Assert.Contains("^FH_^FD_C3_B1_C2_BF_E2_82_AC^FS", zpl);
        Assert.All(zpl, c => Assert.True(c < 128));
    }

    [Fact]
    public void LegacyCodePageEncodesSingleBytesAndWarnsOnUnmappable()
    {
        var doc = NewDoc(new TextElement { X = 1, Y = 1, Text = "ñ€" });
        doc.Encoding = ZplEncoding.Cp850;
        var result = ZplGenerator.Generate(doc);
        Assert.Contains("^CI13", result.Zpl);
        Assert.Contains("^FH_^FD_A4?^FS", result.Zpl); // n-tilde is 0xA4 in CP850; the euro sign is not in CP850
        Assert.Contains(result.Issues, i => i.MessageKey == "Text.UnmappableChar");
    }

    [Fact]
    public void SingleLineTextTurnsNewlinesIntoSpaces() =>
        Assert.Contains("^FDa b^FS", Zpl(NewDoc(new TextElement { X = 1, Y = 1, Text = "a\nb" })));

    [Fact]
    public void OutputIsDeterministic()
    {
        LabelDocument Build()
        {
            var d = NewDoc(new TextElement { Id = "a", X = 5, Y = 5, Text = "x" }, new BarcodeElement { Id = "b", Data = "123", X = 9, Y = 9 });
            return d;
        }
        Assert.Equal(Zpl(Build()), Zpl(Build()));
        var doc = Build();
        Assert.Equal(Zpl(doc), Zpl(doc));
    }

    [Fact]
    public void OutsideLabelOnlyWarns()
    {
        var result = ZplGenerator.Generate(NewDoc(new BoxElement { X = 790, Y = 0, WidthDots = 50, HeightDots = 10 }));
        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == "Element.OutsideLabel");
    }

    [Fact]
    public void BadGraphicSizeIsAnError()
    {
        var result = ZplGenerator.Generate(NewDoc(new CircleElement { DiameterDots = 1 }));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ImageProcessingPacksBlackAsOneBits()
    {
        // Left half black, right half white, 16x1, hard threshold.
        var px = new byte[16];
        for (int x = 8; x < 16; x++) px[x] = 255;
        var img = new ImageElement { Source = new GrayscaleBitmap(16, 1, px), Dither = ImageDitherMode.Threshold };
        var mono = ImageProcessor.ToMonochrome(img);
        Assert.Equal("FF00", mono.ToHex());
        Assert.Equal(2, mono.BytesPerRow);
        Assert.True(mono.IsBlack(0, 0));
        Assert.False(mono.IsBlack(15, 0));
    }

    [Fact]
    public void ImageScalingKeepsAspectAndRotationSwapsSides()
    {
        var src = new GrayscaleBitmap(8, 4, new byte[32]);
        var img = new ImageElement { Source = src, WidthDots = 16, Rotation = FieldRotation.Rotated90 };
        Assert.Equal((16, 8), ImageProcessor.ResolveSize(img));
        var mono = ImageProcessor.ToMonochrome(img);
        Assert.Equal((8, 16), (mono.Width, mono.Height));
    }

    [Fact]
    public void ImageInvertAndFloydSteinbergAreStable()
    {
        var px = Enumerable.Repeat((byte)128, 64).ToArray();
        var img = new ImageElement { Source = new GrayscaleBitmap(8, 8, px), Dither = ImageDitherMode.FloydSteinberg };
        Assert.Equal(ImageProcessor.ToMonochrome(img).ToHex(), ImageProcessor.ToMonochrome(img).ToHex());
        img.Invert = true;
        Assert.NotEmpty(ImageProcessor.ToMonochrome(img).ToHex());
    }

    [Fact]
    public void MissingImageSourceIsReported()
    {
        var result = ZplGenerator.Generate(NewDoc(new ImageElement()));
        Assert.Contains(result.Issues, i => i.MessageKey == "Image.Source.Missing");
    }
}
