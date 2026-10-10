using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using ZXing;

namespace Rotula.Tests;

/// <summary>Tests for <see cref="LabelDpiConverter"/>: same physical label on a printer of another resolution.</summary>
public class DpiConvertTests
{
    private static LabelDocument Doc(PrinterDpi dpi, params LabelElement[] elements)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(100), Height = Measure.Millimeters(50), Dpi = dpi };
        d.Margins.Left = Measure.Dots(16);
        d.Margins.Top = Measure.Dots(8);
        d.DefaultFont = new FontSpec('0', 30, 20);
        d.Elements.AddRange(elements);
        return d;
    }

    private static T One<T>(LabelDocument d) where T : LabelElement => Assert.IsType<T>(Assert.Single(d.Elements));

    [Theory]
    [InlineData(PrinterDpi.Dpi203, PrinterDpi.Dpi300)]
    [InlineData(PrinterDpi.Dpi300, PrinterDpi.Dpi203)]
    [InlineData(PrinterDpi.Dpi203, PrinterDpi.Dpi600)]
    [InlineData(PrinterDpi.Dpi600, PrinterDpi.Dpi203)]
    public void Physical_size_is_preserved(PrinterDpi from, PrinterDpi to)
    {
        var src = Doc(from);
        var r = LabelDpiConverter.Convert(src, to);
        Assert.Equal(to, r.Document.Dpi);
        Assert.Equal(src.Width, r.Document.Width);
        Assert.Equal(src.Height, r.Document.Height);
        double ratio = (double)(int)to / (int)from;
        Assert.InRange(r.Document.WidthDots, src.WidthDots * ratio - 2, src.WidthDots * ratio + 2);
        Assert.InRange(r.Document.HeightDots, src.HeightDots * ratio - 2, src.HeightDots * ratio + 2);
        Assert.Equal(Measure.Dots((int)Math.Round(16 * ratio, MidpointRounding.AwayFromZero)), r.Document.Margins.Left);
        Assert.Equal(new FontSpec('0', 0, 0).FontId, r.Document.DefaultFont!.FontId);
        Assert.Equal((int)Math.Round(30 * ratio, MidpointRounding.AwayFromZero), r.Document.DefaultFont.Height);
    }

    [Fact]
    public void Dot_sized_label_is_scaled()
    {
        var src = Doc(PrinterDpi.Dpi203);
        src.Width = Measure.Dots(812);
        src.Height = Measure.Dots(406);
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;
        Assert.Equal(1200, r.WidthDots);
        Assert.Equal(600, r.HeightDots);
    }

    [Fact]
    public void Identity_returns_an_equal_deep_copy_without_issues()
    {
        var src = Doc(PrinterDpi.Dpi203, new TextElement { Text = "A", X = 5, Y = 7, FontHeight = 20 }, new RawZplElement { Zpl = "^LS20" });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi203);
        Assert.Empty(r.Issues);
        Assert.NotSame(src, r.Document);
        Assert.NotSame(src.Elements[0], r.Document.Elements[0]);
        Assert.Equal(Rotula.Core.Zpl.ZplGenerator.Generate(src).Zpl, Rotula.Core.Zpl.ZplGenerator.Generate(r.Document).Zpl);
        Assert.False(LabelDpiConverter.NeedsConversion(src, PrinterDpi.Dpi203));
        Assert.True(LabelDpiConverter.NeedsConversion(src, PrinterDpi.Dpi300));
    }

    [Fact]
    public void Input_is_not_mutated()
    {
        var text = new TextElement { Text = "A", X = 100, Y = 50, FontHeight = 30, Block = new TextBlockOptions { WidthDots = 400 } };
        var src = Doc(PrinterDpi.Dpi203, text);
        LabelDpiConverter.Convert(src, PrinterDpi.Dpi600);
        Assert.Equal(PrinterDpi.Dpi203, src.Dpi);
        Assert.Equal(100, text.X);
        Assert.Equal(30, text.FontHeight);
        Assert.Equal(400, text.Block!.WidthDots);
        Assert.Equal(Measure.Dots(16), src.Margins.Left);
    }

    [Fact]
    public void Text_and_block_are_scaled_and_null_stays_null()
    {
        var src = Doc(PrinterDpi.Dpi203,
            new TextElement
            {
                Text = "Hi", X = 100, Y = 50, FontHeight = 30, FontWidth = null, FontId = 'A',
                Block = new TextBlockOptions { WidthDots = 400, MaxLines = 3, LineSpacing = -4, HangingIndent = 10 },
            });
        var t = One<TextElement>(LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document);
        Assert.Equal(148, t.X);   // 147.78
        Assert.Equal(74, t.Y);    // 73.89
        Assert.Equal(44, t.FontHeight); // 44.33
        Assert.Null(t.FontWidth);
        Assert.Equal('A', t.FontId);
        Assert.Equal(591, t.Block!.WidthDots); // 591.13
        Assert.Equal(3, t.Block.MaxLines);
        Assert.Equal(-6, t.Block.LineSpacing); // -5.91 -> -6
        Assert.Equal(15, t.Block.HangingIndent);
    }

    [Fact]
    public void Proportional_font_width_zero_stays_zero_and_data_field_keeps_variable()
    {
        var src = Doc(PrinterDpi.Dpi203, new DataFieldElement { VariableName = "sku", FontHeight = 40, FontWidth = 0 });
        src.Variables.Add(new LabelVariable("sku", "X1"));
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;
        var f = One<DataFieldElement>(r);
        Assert.Equal("sku", f.VariableName);
        Assert.Equal(0, f.FontWidth);
        Assert.Equal(59, f.FontHeight);
        Assert.Equal("X1", Assert.Single(r.Variables).SampleValue);
    }

    [Fact]
    public void Graphics_are_scaled_with_minimums()
    {
        var src = Doc(PrinterDpi.Dpi600,
            new BoxElement { X = 30, Y = 30, WidthDots = 600, HeightDots = 300, ThicknessDots = 1, CornerRounding = 2 },
            new LineElement { LengthDots = 1, ThicknessDots = 1 },
            new CircleElement { DiameterDots = 6, ThicknessDots = 1 });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi203).Document;
        var box = Assert.IsType<BoxElement>(r.Elements[0]);
        Assert.Equal((10, 10, 203, 102), (box.X, box.Y, box.WidthDots, box.HeightDots));
        Assert.Equal(1, box.ThicknessDots);
        Assert.Equal(2, box.CornerRounding);
        var line = Assert.IsType<LineElement>(r.Elements[1]);
        Assert.Equal(1, line.LengthDots);
        Assert.Equal(1, line.ThicknessDots);
        var circle = Assert.IsType<CircleElement>(r.Elements[2]);
        Assert.Equal(3, circle.DiameterDots); // clamped to the ^GC minimum, 2.03 ideal
        Assert.Equal(1, circle.ThicknessDots);
    }

    [Fact]
    public void Circle_clamp_is_reported()
    {
        var src = Doc(PrinterDpi.Dpi600, new CircleElement { DiameterDots = 6, Name = "c" });
        var issue = Assert.Single(LabelDpiConverter.Convert(src, PrinterDpi.Dpi203).Issues);
        Assert.Equal("Dpi.Value.Clamped", issue.MessageKey);
        Assert.Equal(new[] { "c", "DiameterDots", "2", "3" }, issue.Args);
    }

    [Fact]
    public void Image_size_is_scaled_and_source_kept()
    {
        var bmp = new GrayscaleBitmap(2, 2, new byte[] { 0, 255, 255, 0 });
        var src = Doc(PrinterDpi.Dpi203,
            new ImageElement { Source = bmp, WidthDots = 200, HeightDots = 100 },
            new ImageElement { Source = bmp },
            new ImageElement { Source = bmp, WidthDots = 50 });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;
        var a = Assert.IsType<ImageElement>(r.Elements[0]);
        Assert.Equal((296, 148), (a.WidthDots, a.HeightDots));
        Assert.Same(bmp, a.Source);
        // "source size" is pinned so the image does not shrink physically.
        var b = Assert.IsType<ImageElement>(r.Elements[1]);
        Assert.Equal((3, 3), (b.WidthDots, b.HeightDots));
        // One side 0 keeps deriving from the aspect ratio.
        var c = Assert.IsType<ImageElement>(r.Elements[2]);
        Assert.Equal((74, 0), (c.WidthDots, c.HeightDots));
    }

    [Fact]
    public void Linear_barcode_scales_height_and_module()
    {
        var src = Doc(PrinterDpi.Dpi203, new BarcodeElement { Data = "ABC", Height = 100, ModuleWidth = 2 });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300);
        var b = One<BarcodeElement>(r.Document);
        Assert.Equal(148, b.Height);
        Assert.Equal(3, b.ModuleWidth);
        Assert.Empty(r.Issues);
        Assert.Empty(BarcodeValidator.Validate(b, b.Data));
    }

    [Fact]
    public void Large_module_deviation_is_reported_and_symbol_stays_valid()
    {
        // 1 dot @203 = 1.48 ideal -> 1 (32% too narrow).
        var src = Doc(PrinterDpi.Dpi203, new BarcodeElement { Data = "ABC", ModuleWidth = 1, Name = "bc" });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("Dpi.Barcode.ModuleRounded", issue.MessageKey);
        Assert.Equal(new[] { "bc", "1", "1" }, issue.Args);
        Assert.Equal(1, One<BarcodeElement>(r.Document).ModuleWidth);
    }

    [Fact]
    public void Module_is_clamped_to_ten_with_a_warning()
    {
        var src = Doc(PrinterDpi.Dpi203, new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "x", ModuleSize = 9, Name = "qr" });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi600);
        var b = One<BarcodeElement>(r.Document);
        Assert.Equal(10, b.ModuleSize);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("Dpi.Barcode.ModuleRounded", issue.MessageKey);
        Assert.Equal(new[] { "qr", "9", "10" }, issue.Args);
        Assert.Empty(BarcodeValidator.Validate(b, "x"));
    }

    [Fact]
    public void Qr_datamatrix_and_pdf417_are_converted()
    {
        var src = Doc(PrinterDpi.Dpi203,
            new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "x", ModuleSize = 4 },
            new BarcodeElement { Symbology = BarcodeSymbology.DataMatrix, Data = "x", ModuleSize = 5, Columns = 20 },
            new BarcodeElement { Symbology = BarcodeSymbology.Pdf417, Data = "x", ModuleWidth = 2, Height = 12, Rows = 5 });
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300);
        Assert.Empty(r.Issues);
        var qr = Assert.IsType<BarcodeElement>(r.Document.Elements[0]);
        var dm = Assert.IsType<BarcodeElement>(r.Document.Elements[1]);
        var pdf = Assert.IsType<BarcodeElement>(r.Document.Elements[2]);
        Assert.Equal(6, qr.ModuleSize);
        Assert.Equal(7, dm.ModuleSize);
        Assert.Equal(20, dm.Columns);
        Assert.Equal((3, 18, 5), (pdf.ModuleWidth, pdf.Height, pdf.Rows));
        foreach (BarcodeElement b in r.Document.Elements) Assert.Empty(BarcodeValidator.Validate(b, "x"));
    }

    [Fact]
    public void Raw_and_recall_elements_are_kept_and_warned()
    {
        var raw = new RawZplElement { Zpl = "^LS20", X = 10, Name = "r" };
        var recall = new RecallPlaceholderElement { Command = "XG", Reference = "R:L.GRF", Zpl = "^XGR:L.GRF,1,1", X = 12, WidthDots = 120, HeightDots = 80 };
        var r = LabelDpiConverter.Convert(Doc(PrinterDpi.Dpi203, raw, recall), PrinterDpi.Dpi300);
        Assert.Equal(2, r.Issues.Count);
        Assert.All(r.Issues, i => Assert.Equal("Dpi.Raw.NotScaled", i.MessageKey));
        Assert.Equal(new[] { "r" }, r.Issues[0].Args);
        var r2 = Assert.IsType<RawZplElement>(r.Document.Elements[0]);
        Assert.Equal(("^LS20", 10), (r2.Zpl, r2.X));
        var rc = Assert.IsType<RecallPlaceholderElement>(r.Document.Elements[1]);
        Assert.Equal((12, 120, 80, "^XGR:L.GRF,1,1"), (rc.X, rc.WidthDots, rc.HeightDots, rc.Zpl));
    }

    [Fact]
    public void Generated_zpl_uses_target_dimensions()
    {
        var src = Doc(PrinterDpi.Dpi203);
        var r = LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document;
        var zpl = Rotula.Core.Zpl.ZplGenerator.Generate(r).Zpl;
        Assert.Contains($"^PW{r.WidthDots}", zpl);
        Assert.Contains($"^LL{r.HeightDots}", zpl);
    }

    [Fact]
    public void Round_trip_203_300_203_stays_within_one_dot()
    {
        var src = Doc(PrinterDpi.Dpi203,
            new TextElement { Text = "x", X = 123, Y = 77, FontHeight = 33, FontWidth = 21, Block = new TextBlockOptions { WidthDots = 333, LineSpacing = 5, HangingIndent = 9 } },
            new BoxElement { X = 11, Y = 13, WidthDots = 301, HeightDots = 99, ThicknessDots = 4 },
            new LineElement { X = 3, Y = 9, LengthDots = 417, ThicknessDots = 3 },
            new CircleElement { DiameterDots = 77, ThicknessDots = 5 },
            new BarcodeElement { Data = "A", Height = 91 });
        var back = LabelDpiConverter.Convert(LabelDpiConverter.Convert(src, PrinterDpi.Dpi300).Document, PrinterDpi.Dpi203).Document;
        void Near(int a, int b) => Assert.InRange(b, a - 1, a + 1);
        var t = (TextElement)src.Elements[0]; var t2 = (TextElement)back.Elements[0];
        Near(t.X, t2.X); Near(t.Y, t2.Y); Near(t.FontHeight!.Value, t2.FontHeight!.Value); Near(t.FontWidth!.Value, t2.FontWidth!.Value);
        Near(t.Block!.WidthDots, t2.Block!.WidthDots); Near(t.Block.LineSpacing, t2.Block!.LineSpacing); Near(t.Block.HangingIndent, t2.Block.HangingIndent);
        var b1 = (BoxElement)src.Elements[1]; var b2 = (BoxElement)back.Elements[1];
        Near(b1.WidthDots, b2.WidthDots); Near(b1.HeightDots, b2.HeightDots); Near(b1.ThicknessDots, b2.ThicknessDots); Near(b1.X, b2.X);
        var l1 = (LineElement)src.Elements[2]; var l2 = (LineElement)back.Elements[2];
        Near(l1.LengthDots, l2.LengthDots);
        var c1 = (CircleElement)src.Elements[3]; var c2 = (CircleElement)back.Elements[3];
        Near(c1.DiameterDots, c2.DiameterDots);
        Near(((BarcodeElement)src.Elements[4]).Height, ((BarcodeElement)back.Elements[4]).Height);
        Assert.Equal(src.WidthDots, back.WidthDots);
    }

    [Theory]
    [InlineData(BarcodeSymbology.Code128, BarcodeFormat.CODE_128, "ROTULA-12345", "ROTULA-12345")]
    [InlineData(BarcodeSymbology.Ean13, BarcodeFormat.EAN_13, "590123412345", "5901234123457")]
    [InlineData(BarcodeSymbology.QrCode, BarcodeFormat.QR_CODE, "https://example.com/r?id=42", "https://example.com/r?id=42")]
    [InlineData(BarcodeSymbology.DataMatrix, BarcodeFormat.DATA_MATRIX, "ROTULA DM", "ROTULA DM")]
    [InlineData(BarcodeSymbology.Pdf417, BarcodeFormat.PDF_417, "PDF417 payload 0123", "PDF417 payload 0123")]
    public void Converted_barcodes_still_decode_at_the_new_resolution(BarcodeSymbology s, BarcodeFormat f, string data, string expected)
    {
        bool matrix = s is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix;
        var src = Doc(PrinterDpi.Dpi203, new BarcodeElement
        {
            Symbology = s, Data = data, X = 60, Y = 60, ModuleWidth = 2, ModuleSize = matrix ? 4 : 2,
            Height = s == BarcodeSymbology.Pdf417 ? 8 : 80,
        });
        src.Width = Measure.Millimeters(100);
        src.Height = Measure.Millimeters(60);
        foreach (var to in new[] { PrinterDpi.Dpi300, PrinterDpi.Dpi600 })
        {
            foreach (var d in new[] { src, LabelDpiConverter.Convert(src, to).Document })
            {
                using var result = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(d));
                var decoded = RenderingTestSupport.Decode(result.Bitmap, f);
                Assert.NotNull(decoded);
                Assert.Equal(expected, decoded!.Text);
            }
        }
    }
}
