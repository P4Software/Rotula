using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using SkiaSharp;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>Placement, rotation, reverse print, text and font tests for the renderer.</summary>
public class RenderingLayoutTests
{
    private static RenderResult Render(RenderLabel label, double scale = 1.0) =>
        LabelRenderer.RenderBitmap(label, new RenderOptions { Scale = scale });

    [Fact]
    public void Bitmap_is_one_pixel_per_dot_at_scale_one()
    {
        using var r = Render(Label(406, 203));
        Assert.Equal(406, r.Bitmap.Width);
        Assert.Equal(203, r.Bitmap.Height);
    }

    [Fact]
    public void Scale_multiplies_the_bitmap_size()
    {
        using var r = Render(Label(100, 50), 3);
        Assert.Equal(300, r.Bitmap.Width);
        Assert.Equal(150, r.Bitmap.Height);
    }

    [Fact]
    public void Zero_scale_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Render(Label(100, 50), 0));
    }

    [Fact]
    public void Solid_box_covers_exactly_its_dots()
    {
        using var r = Render(Label(200, 100, new RenderBox { X = 10, Y = 20, WidthDots = 50, HeightDots = 30, ThicknessDots = 100 }));
        Assert.Equal(new SKRectI(10, 20, 60, 50), DarkBounds(r.Bitmap));
    }

    [Fact]
    public void Outlined_box_is_hollow()
    {
        using var r = Render(Label(200, 100, new RenderBox { X = 10, Y = 10, WidthDots = 60, HeightDots = 40, ThicknessDots = 3 }));
        Assert.True(IsDark(r.Bitmap, 10, 10));
        Assert.True(IsDark(r.Bitmap, 69, 49));
        Assert.False(IsDark(r.Bitmap, 40, 30));
    }

    [Theory]
    [InlineData(RenderRotation.None, 100, 100, 150, 120)]
    [InlineData(RenderRotation.Rotate90, 100, 100, 120, 150)]   // 50x20 box becomes 20x50, corner stays at the origin
    [InlineData(RenderRotation.Rotate180, 100, 100, 150, 120)]
    [InlineData(RenderRotation.Rotate270, 100, 100, 120, 150)]
    public void Field_origin_is_the_corner_of_the_rotated_box(RenderRotation rot, int left, int top, int right, int bottom)
    {
        using var r = Render(Label(300, 300, new RenderBox { X = left, Y = top, WidthDots = 50, HeightDots = 20, ThicknessDots = 100, Rotation = rot }));
        Assert.Equal(new SKRectI(left, top, right, bottom), DarkBounds(r.Bitmap));
    }

    [Fact]
    public void Typeset_origin_is_the_bottom_left_corner_of_a_box()
    {
        using var r = Render(Label(300, 300, new RenderBox { X = 100, Y = 100, WidthDots = 50, HeightDots = 20, ThicknessDots = 100, Typeset = true }));
        Assert.Equal(new SKRectI(100, 80, 150, 100), DarkBounds(r.Bitmap));
    }

    [Fact]
    public void Reverse_print_inverts_what_is_underneath()
    {
        var black = new RenderBox { X = 0, Y = 0, WidthDots = 100, HeightDots = 100, ThicknessDots = 100 };
        var reverse = new RenderBox { X = 25, Y = 25, WidthDots = 50, HeightDots = 50, ThicknessDots = 100, Reverse = true };
        using var r = Render(Label(100, 100, black, reverse));
        Assert.True(IsDark(r.Bitmap, 10, 10));   // still black outside the reversed field
        Assert.False(IsDark(r.Bitmap, 50, 50));  // reversed to white inside it
    }

    [Fact]
    public void Reverse_text_draws_white_ink_on_black()
    {
        var black = new RenderBox { WidthDots = 300, HeightDots = 80, ThicknessDots = 100 };
        var text = new RenderText { Text = "REVERSE", FontHeightDots = 50, Reverse = true, X = 5, Y = 5 };
        using var plain = Render(Label(300, 80, black));
        using var r = Render(Label(300, 80, black, text));
        int whiteInk = 0;
        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 300; x++)
                if (!IsDark(r.Bitmap, x, y) && IsDark(plain.Bitmap, x, y)) whiteInk++;
        Assert.True(whiteInk > 100, "Reverse text should knock white glyphs out of the black box.");
    }

    [Fact]
    public void Text_draws_ink_inside_its_line_box()
    {
        using var r = Render(Label(400, 100, new RenderText { Text = "Hello Rotula", FontHeightDots = 40, X = 20, Y = 30 }));
        var b = DarkBounds(r.Bitmap)!.Value;
        Assert.InRange(b.Left, 20, 30);
        Assert.InRange(b.Top, 30, 45);
        Assert.InRange(b.Bottom, 55, 80);
    }

    [Fact]
    public void Typeset_text_sits_on_the_baseline()
    {
        // "HHHH" has no descenders, so its ink must end at the baseline y given as the origin.
        using var r = Render(Label(300, 200, new RenderText { Text = "HHHH", FontHeightDots = 60, X = 20, Y = 120, Typeset = true }));
        var b = DarkBounds(r.Bitmap)!.Value;
        Assert.InRange(b.Bottom, 119, 122);
    }

    [Fact]
    public void Wrapping_and_max_lines_limit_the_block()
    {
        var t = new RenderText { Text = "one two three four five six seven eight nine ten", FontHeightDots = 30, WidthDots = 150, MaxLines = 2, X = 0, Y = 0 };
        using var r = Render(Label(400, 300, t));
        var b = DarkBounds(r.Bitmap)!.Value;
        Assert.True(b.Right <= 152, "Text must wrap inside the block width.");
        Assert.True(b.Bottom < 90, "Only two lines may be drawn.");
    }

    [Fact]
    public void Center_alignment_moves_short_lines_to_the_middle()
    {
        var t = new RenderText { Text = "Hi", FontHeightDots = 30, WidthDots = 300, Alignment = RenderTextAlignment.Center };
        using var r = Render(Label(300, 60, t));
        var b = DarkBounds(r.Bitmap)!.Value;
        Assert.InRange((b.Left + b.Right) / 2, 130, 170);
    }

    [Fact]
    public void Bundled_fonts_load_and_license_text_is_available()
    {
        Assert.Contains("Liberation", RenderFonts.Get(RenderFontFamily.Sans, false).FamilyName);
        Assert.Contains("Liberation", RenderFonts.Get(RenderFontFamily.Sans, true).FamilyName);
        Assert.Contains("Liberation", RenderFonts.Get(RenderFontFamily.Mono, false).FamilyName);
        Assert.Contains("SIL OPEN FONT LICENSE", RenderFonts.LicenseText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Circle_outline_is_hollow_and_fits_its_bounding_box()
    {
        using var r = Render(Label(200, 200, new RenderCircle { X = 20, Y = 20, DiameterDots = 100, ThicknessDots = 4 }));
        var b = DarkBounds(r.Bitmap)!.Value;
        Assert.InRange(b.Left, 19, 21);
        Assert.InRange(b.Right, 119, 121);
        Assert.False(IsDark(r.Bitmap, 70, 70));
    }

    [Fact]
    public void Unreadable_image_is_reported_not_thrown()
    {
        using var r = Render(Label(100, 100, new RenderImage { EncodedBytes = [1, 2, 3], WidthDots = 50, HeightDots = 50 }));
        Assert.Single(r.Issues);
    }

    // ------------------------------------------------------------------ adapter from the Core model

    [Fact]
    public void Adapter_adds_label_home_margins_and_resolves_variables()
    {
        var doc = new LabelDocument { Width = Measure.Dots(400), Height = Measure.Dots(200), Dpi = PrinterDpi.Dpi203 };
        doc.Margins.Left = Measure.Dots(30);
        doc.Margins.Top = Measure.Dots(10);
        doc.Variables.Add(new LabelVariable("sku", "SAMPLE-1"));
        doc.Elements.Add(new DataFieldElement { VariableName = "sku", X = 5, Y = 7 });
        doc.Elements.Add(new BoxElement { X = 0, Y = 0, WidthDots = 10, HeightDots = 10 });

        var label = LabelDocumentAdapter.ToRenderLabel(doc, new Dictionary<string, string> { ["sku"] = "REAL-42" });

        Assert.Equal(400, label.WidthDots);
        Assert.Equal(203, label.Dpi);
        var text = Assert.IsType<RenderText>(label.Elements[0]);
        Assert.Equal("REAL-42", text.Text);
        Assert.Equal(35, text.X);
        Assert.Equal(17, text.Y);
        Assert.Equal(30, label.Elements[1].X);
    }

    [Fact]
    public void Adapter_output_renders_a_scannable_qr_and_a_line()
    {
        var doc = new LabelDocument { Width = Measure.Dots(400), Height = Measure.Dots(300) };
        doc.Elements.Add(new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "FROM-CORE-MODEL", ModuleSize = 5, X = 40, Y = 40 });
        doc.Elements.Add(new LineElement { X = 0, Y = 250, LengthDots = 400, ThicknessDots = 4 });

        using var r = LabelRenderer.RenderBitmap(LabelDocumentAdapter.ToRenderLabel(doc));
        Assert.Empty(r.Issues);
        Assert.True(IsDark(r.Bitmap, 200, 251));
        Assert.Equal("FROM-CORE-MODEL", Decode(r.Bitmap, ZXing.BarcodeFormat.QR_CODE)?.Text);
    }
}
