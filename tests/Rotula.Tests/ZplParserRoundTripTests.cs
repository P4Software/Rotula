using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Zpl;
using Rotula.Core.Zpl.Import;
using static Rotula.Tests.ZplParserTestSupport;

namespace Rotula.Tests;

/// <summary>parse(generate(doc)) must equal doc for every supported element, and generate(parse(zpl)) must reproduce zpl.</summary>
public class ZplParserRoundTripTests
{
    private static LabelDocument RoundTrip(LabelDocument doc, bool fromBytes = false)
    {
        string zpl = CoreTestSupport.Zpl(doc);
        var result = fromBytes ? ZplParser.Parse(Encoding.ASCII.GetBytes(zpl)) : ZplParser.Parse(zpl);
        Assert.True(result.Warnings.Count == 0, "Unexpected warnings: " + string.Join("; ", result.Warnings));
        return result.Document;
    }

    private static GrayscaleBitmap Gradient(int w, int h)
    {
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = (byte)((x * 31 + y * 17) % 256);
        return new GrayscaleBitmap(w, h, px);
    }

    [Fact]
    public void Text_variants_round_trip()
    {
        var doc = CoreTestSupport.NewDoc(
            new TextElement { X = 10, Y = 10, Text = "Plain" },
            new TextElement { X = 10, Y = 40, Text = "Sized", FontHeight = 50, FontWidth = 40 },
            new TextElement { X = 10, Y = 90, Text = "Font", FontId = 'A', FontHeight = 20, FontWidth = 10, Rotation = FieldRotation.Rotated90 },
            new TextElement { X = 100, Y = 200, Text = "Typeset", OriginKind = FieldOriginKind.Typeset, FontHeight = 24, FontWidth = 24, Rotation = FieldRotation.Inverted180 },
            new TextElement { X = 10, Y = 150, Text = "Reverse", ReverseField = true },
            new TextElement { X = 10, Y = 180, Text = "Tilde ~ caret ^ underscore _ comma, done" },
            new TextElement
            {
                X = 50, Y = 220, Text = "Line one\nLine two", FontHeight = 30, FontWidth = 30,
                Block = new TextBlockOptions { WidthDots = 400, MaxLines = 4, LineSpacing = 6, Alignment = TextAlignment.Justified, HangingIndent = 12 },
            });
        foreach (var alignment in new[] { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right })
            doc.Elements.Add(new TextElement { X = 5, Y = 5, Text = "Aligned", FontHeight = 20, FontWidth = 20, Block = new TextBlockOptions { WidthDots = 300, MaxLines = 2, Alignment = alignment } });

        AssertSame(doc, RoundTrip(doc));
        AssertSame(doc, RoundTrip(doc, fromBytes: true));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("cp850")]
    [InlineData("cp1252")]
    public void Accented_text_round_trips_under_every_encoding(string which)
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { X = 10, Y = 10, Text = "Niño ¿qué? ¡Sí! áéíóú ü Ñ" });
        doc.Encoding = which switch { "cp850" => ZplEncoding.Cp850, "cp1252" => ZplEncoding.Cp1252, _ => ZplEncoding.Utf8 };
        AssertSame(doc, RoundTrip(doc));
        AssertSame(doc, RoundTrip(doc, fromBytes: true));
    }

    [Fact]
    public void Every_symbology_round_trips_with_non_default_options()
    {
        var doc = CoreTestSupport.NewDoc(
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Code128, Data = "SKU-0042", Height = 80, ModuleWidth = 3, WideBarRatio = 2.5, HumanReadable = true, HumanReadablePosition = HumanReadablePosition.Above, Code128Mode = Code128Mode.Ucc },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Code128, Data = "NoText", Height = 60, HumanReadable = false, Code128Mode = Code128Mode.None, Rotation = FieldRotation.Rotated90 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Code39, Data = "ABC-123", CheckDigit = true, Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Code93, Data = "TEST93", CheckDigit = true, Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Ean13, Data = "590123412345", Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.UpcA, Data = "03600029145", CheckDigit = true, Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.UpcA, Data = "03600029145", CheckDigit = false, Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Interleaved2Of5, Data = "123456789", CheckDigit = true, Height = 70 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.QrCode, Data = "https://example.com/p?id=7", ModuleSize = 6, QrErrorCorrection = QrErrorCorrection.High, ReverseField = true },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.QrCode, Data = "low", ModuleSize = 2, QrErrorCorrection = QrErrorCorrection.Low, QrModel = 1 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.DataMatrix, Data = "DM-0001", ModuleSize = 5, Columns = 20, Rows = 20 },
            new BarcodeElement { X = 10, Y = 10, Symbology = BarcodeSymbology.Pdf417, Data = "PDF417 payload", ModuleWidth = 3, Height = 8, Pdf417SecurityLevel = 3, Columns = 4, Rows = 10, Pdf417Truncate = true });

        AssertSame(doc, RoundTrip(doc));
        AssertSame(doc, RoundTrip(doc, fromBytes: true));
    }

    [Fact]
    public void Graphics_round_trip_including_a_box_as_thick_as_it_is_wide()
    {
        var doc = CoreTestSupport.NewDoc(
            new BoxElement { X = 10, Y = 10, WidthDots = 300, HeightDots = 200, ThicknessDots = 5, CornerRounding = 3 },
            // Same thickness as width: indistinguishable from a vertical line except for the rounding parameter.
            new BoxElement { X = 10, Y = 10, WidthDots = 20, HeightDots = 100, ThicknessDots = 20, Color = GraphicColor.White },
            new BoxElement { X = 10, Y = 10, WidthDots = 100, HeightDots = 4, ThicknessDots = 4 },
            new LineElement { X = 10, Y = 10, Direction = LineDirection.Horizontal, LengthDots = 400, ThicknessDots = 4 },
            new LineElement { X = 10, Y = 10, Direction = LineDirection.Vertical, LengthDots = 200, ThicknessDots = 6, Color = GraphicColor.White },
            new CircleElement { X = 10, Y = 10, DiameterDots = 120, ThicknessDots = 8, Color = GraphicColor.White });

        AssertSame(doc, RoundTrip(doc));
    }

    [Fact]
    public void Image_round_trips_through_its_printed_dots()
    {
        foreach (var mode in new[] { ImageDitherMode.Threshold, ImageDitherMode.FloydSteinberg, ImageDitherMode.Ordered })
        {
            var doc = CoreTestSupport.NewDoc(new ImageElement { X = 30, Y = 30, Source = Gradient(37, 21), Dither = mode, ReverseField = true });
            AssertSame(doc, RoundTrip(doc));
        }
        // Scaled and rotated: the rotation is already baked into the ^GF dots.
        var scaled = CoreTestSupport.NewDoc(new ImageElement { X = 5, Y = 5, Source = Gradient(40, 40), WidthDots = 64, HeightDots = 32, Rotation = FieldRotation.Rotated90 });
        AssertSame(scaled, RoundTrip(scaled));
    }

    [Fact]
    public void Label_settings_round_trip()
    {
        var doc = new LabelDocument
        {
            Width = Measure.Dots(1181), Height = Measure.Dots(600),
            Orientation = LabelOrientation.Inverted, MediaType = MediaType.DirectThermal, MediaTracking = MediaTracking.Mark,
            DefaultRotation = FieldRotation.Rotated270, DefaultFont = new FontSpec('A', 20, 10),
        };
        doc.Print.Quantity = 25;
        doc.Print.PauseEvery = 5;
        doc.Print.Replicates = 2;
        doc.Print.OverridePause = true;
        doc.Margins.Left = Measure.Dots(24);
        doc.Margins.Top = Measure.Dots(12);
        doc.Elements.Add(new TextElement { X = 5, Y = 5, Text = "Inherits rotation and font" });
        doc.Elements.Add(new TextElement { X = 5, Y = 50, Text = "Explicit normal", Rotation = FieldRotation.Normal });
        AssertSame(doc, RoundTrip(doc));

        var mm = new LabelDocument { Width = Measure.Millimeters(100), Height = Measure.Millimeters(150) };
        AssertSame(mm, RoundTrip(mm));
    }

    [Fact]
    public void Document_without_default_font_keeps_explicit_fonts()
    {
        var doc = CoreTestSupport.NewDoc(new TextElement { X = 1, Y = 1, Text = "Hi", FontId = '0', FontHeight = 30, FontWidth = 30 });
        doc.DefaultFont = null;
        AssertSame(doc, RoundTrip(doc));
    }

    [Fact]
    public void Imported_raw_and_placeholder_elements_are_written_back_unchanged()
    {
        var doc = CoreTestSupport.NewDoc(
            new RawZplElement { Zpl = "^LS20" },
            new RecallPlaceholderElement { Command = "XG", Reference = "R:LOGO.GRF", Zpl = "^XGR:LOGO.GRF,1,1", EmitAsField = true, X = 20, Y = 20 },
            new RecallPlaceholderElement { Command = "IL", Reference = "R:BG.GRF", Zpl = "^ILR:BG.GRF" });
        string zpl = CoreTestSupport.Zpl(doc);
        Assert.Contains("\n^LS20\n", zpl);
        Assert.Contains("\n^FO20,20^XGR:LOGO.GRF,1,1^FS\n", zpl);
        Assert.Contains("\n^ILR:BG.GRF\n", zpl);
    }

    [Fact]
    public void Every_existing_golden_file_is_a_fixpoint_of_parse_then_generate()
    {
        var files = Directory.GetFiles(FixturesDir(), "*.zpl", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string golden = File.ReadAllText(file).Replace("\r\n", "\n");
            var result = ZplParser.Parse(golden);
            Assert.True(result.Warnings.Count == 0, $"{Path.GetFileName(file)} warned: {string.Join("; ", result.Warnings)}");
            var generated = ZplGenerator.Generate(result.Document);
            Assert.False(generated.HasErrors, $"{Path.GetFileName(file)}: {string.Join(", ", generated.Issues)}");
            Assert.Equal(golden, generated.Zpl);
        }
    }
}
