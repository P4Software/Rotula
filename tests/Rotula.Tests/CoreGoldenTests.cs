using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using static Rotula.Tests.CoreTestSupport;

namespace Rotula.Tests;

/// <summary>Golden-file tests: one fixture per element type and notable option set.</summary>
public class CoreGoldenTests
{
    private static BarcodeElement Bc(BarcodeSymbology s, string data) =>
        new() { Symbology = s, Data = data, X = 50, Y = 50 };

    [Fact]
    public void EmptyLabel() => AssertGolden("empty_label", Zpl(NewDoc()));

    [Fact]
    public void TextSingleLine() =>
        AssertGolden("text_single_line", Zpl(NewDoc(new TextElement { X = 50, Y = 40, Text = "Hello World", FontHeight = 40, FontWidth = 30 })));

    [Fact]
    public void TextInheritsDefaultFont() =>
        AssertGolden("text_default_font", Zpl(NewDoc(new TextElement { X = 10, Y = 10, Text = "Default" })));

    [Fact]
    public void TextBitmapFontRotatedTypeset() =>
        AssertGolden("text_rotated_typeset", Zpl(NewDoc(new TextElement
        { X = 100, Y = 200, OriginKind = FieldOriginKind.Typeset, Rotation = FieldRotation.Rotated90, FontId = 'A', FontHeight = 20, FontWidth = 12, Text = "Rotated" })));

    [Fact]
    public void TextBlockWrapped() =>
        AssertGolden("text_block", Zpl(NewDoc(new TextElement
        {
            X = 50, Y = 50, Text = "Line one\nA long paragraph that must wrap inside the block.",
            Block = new TextBlockOptions { WidthDots = 500, MaxLines = 4, LineSpacing = 4, Alignment = TextAlignment.Justified, HangingIndent = 10 },
        })));

    [Fact]
    public void TextAccentsUseHexEscapes() =>
        AssertGolden("text_utf8_accents", Zpl(NewDoc(new TextElement { X = 50, Y = 50, Text = "Niño ¿Cómo estás? 100% ^~_" })));

    [Fact]
    public void TextLegacyCodePage()
    {
        var doc = NewDoc(new TextElement { X = 50, Y = 50, Text = "Niño" });
        doc.Encoding = ZplEncoding.Cp850;
        AssertGolden("text_cp850", Zpl(doc));
    }

    [Fact]
    public void ReverseField() =>
        AssertGolden("text_reverse", Zpl(NewDoc(new TextElement { X = 50, Y = 50, ReverseField = true, Text = "Inverse" })));

    [Fact]
    public void DataFieldWithVariable()
    {
        var doc = NewDoc(new DataFieldElement { X = 50, Y = 50, VariableName = "Customer" }, new TextElement { X = 50, Y = 100, Text = "Order {{Order}}" });
        doc.Variables.Add(new LabelVariable("Customer", "Sample Co"));
        doc.Variables.Add(new LabelVariable("Order", "0001"));
        AssertGolden("datafield_sample", Zpl(doc));
        AssertGolden("datafield_merged", Zpl(doc, new Dictionary<string, string> { ["Customer"] = "Acme S.A.", ["Order"] = "A-77" }));
    }

    [Fact]
    public void Code128() =>
        AssertGolden("barcode_code128", Zpl(NewDoc(Bc(BarcodeSymbology.Code128, "ROTULA-12345"))));

    [Fact]
    public void Code128OptionsRotated()
    {
        var b = Bc(BarcodeSymbology.Code128, "ABC123");
        b.HumanReadable = true; b.HumanReadablePosition = HumanReadablePosition.Above; b.Rotation = FieldRotation.Rotated90;
        b.ModuleWidth = 3; b.WideBarRatio = 2.5; b.Code128Mode = Code128Mode.None;
        AssertGolden("barcode_code128_options", Zpl(NewDoc(b)));
    }

    [Fact]
    public void Code39() =>
        AssertGolden("barcode_code39", Zpl(NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.Code39, Data = "ROTULA-39", CheckDigit = true, X = 50, Y = 50 })));

    [Fact]
    public void Code93() =>
        AssertGolden("barcode_code93", Zpl(NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.Code93, Data = "ROTULA93", CheckDigit = true, HumanReadable = false, X = 50, Y = 50 })));

    [Fact]
    public void Ean13() =>
        AssertGolden("barcode_ean13", Zpl(NewDoc(Bc(BarcodeSymbology.Ean13, "4006381333931"))));

    [Fact]
    public void Ean13WithoutCheckDigitGivesSameOutput() =>
        Assert.Equal(Zpl(NewDoc(Bc(BarcodeSymbology.Ean13, "4006381333931"))), Zpl(NewDoc(Bc(BarcodeSymbology.Ean13, "400638133393"))));

    [Fact]
    public void UpcA() =>
        AssertGolden("barcode_upca", Zpl(NewDoc(new BarcodeElement { Symbology = BarcodeSymbology.UpcA, Data = "036000291452", CheckDigit = true, X = 50, Y = 50 })));

    [Fact]
    public void Interleaved2Of5() =>
        AssertGolden("barcode_i25", Zpl(NewDoc(Bc(BarcodeSymbology.Interleaved2Of5, "12345678"))));

    [Fact]
    public void QrCode()
    {
        var b = Bc(BarcodeSymbology.QrCode, "https://example.com/p?id=7");
        b.ModuleSize = 6; b.QrErrorCorrection = QrErrorCorrection.High;
        AssertGolden("barcode_qr", Zpl(NewDoc(b)));
    }

    [Fact]
    public void DataMatrix()
    {
        var b = Bc(BarcodeSymbology.DataMatrix, "DM-0001");
        b.ModuleSize = 5;
        AssertGolden("barcode_datamatrix", Zpl(NewDoc(b)));
    }

    [Fact]
    public void Pdf417()
    {
        var b = Bc(BarcodeSymbology.Pdf417, "PDF417 sample payload");
        b.Height = 8; b.Columns = 4; b.Pdf417SecurityLevel = 3; b.Pdf417Truncate = true; b.ModuleWidth = 3;
        AssertGolden("barcode_pdf417", Zpl(NewDoc(b)));
    }

    [Fact]
    public void BoxLineCircle()
    {
        AssertGolden("graphics_box", Zpl(NewDoc(new BoxElement { X = 20, Y = 20, WidthDots = 300, HeightDots = 150, ThicknessDots = 5, CornerRounding = 3 })));
        AssertGolden("graphics_lines", Zpl(NewDoc(
            new LineElement { X = 20, Y = 20, LengthDots = 400, ThicknessDots = 4 },
            new LineElement { X = 20, Y = 40, LengthDots = 200, ThicknessDots = 2, Direction = LineDirection.Vertical, Color = GraphicColor.White })));
        AssertGolden("graphics_circle", Zpl(NewDoc(new CircleElement { X = 20, Y = 20, DiameterDots = 120, ThicknessDots = 6 })));
    }

    private static GrayscaleBitmap Gradient(int w, int h)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)(x * 255 / (w - 1));
        return new GrayscaleBitmap(w, h, px);
    }

    [Theory]
    [InlineData(ImageDitherMode.Threshold, "image_threshold")]
    [InlineData(ImageDitherMode.FloydSteinberg, "image_floyd")]
    [InlineData(ImageDitherMode.Ordered, "image_ordered")]
    public void Images(ImageDitherMode mode, string golden) =>
        AssertGolden(golden, Zpl(NewDoc(new ImageElement { X = 10, Y = 10, Source = Gradient(16, 4), Dither = mode })));

    [Fact]
    public void LabelSettings()
    {
        var doc = NewDoc();
        doc.Dpi = PrinterDpi.Dpi300;
        doc.Width = Measure.Millimeters(100); doc.Height = Measure.Inches(2);
        doc.Orientation = LabelOrientation.Inverted;
        doc.MediaType = MediaType.DirectThermal; doc.MediaTracking = MediaTracking.Mark;
        doc.DefaultRotation = FieldRotation.Rotated270;
        doc.DefaultFont = new FontSpec('A', 20, 10);
        doc.Margins.Left = Measure.Millimeters(2); doc.Margins.Top = Measure.Millimeters(1);
        doc.Print = new PrintOptions { Quantity = 25, PauseEvery = 5, Replicates = 2, OverridePause = true };
        AssertGolden("label_settings", Zpl(doc));
    }

    [Fact]
    public void FullLabelCombined()
    {
        var doc = NewDoc(
            new BoxElement { X = 10, Y = 10, WidthDots = 780, HeightDots = 380, ThicknessDots = 3 },
            new TextElement { X = 30, Y = 30, FontHeight = 50, FontWidth = 40, Text = "Rotula" },
            new BarcodeElement { X = 30, Y = 120, Data = "{{Sku}}", Height = 90 },
            new BarcodeElement { X = 600, Y = 30, Symbology = BarcodeSymbology.QrCode, Data = "{{Sku}}", ModuleSize = 5 });
        doc.Variables.Add(new LabelVariable("Sku", "SKU-0042"));
        AssertGolden("full_label", Zpl(doc));
    }
}
