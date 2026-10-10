using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using SkiaSharp;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>Preview of imported content the designer cannot model: recall placeholders and visual raw ZPL.</summary>
public class RenderingPlaceholderTests
{
    private static LabelDocument Doc(params LabelElement[] elements)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        foreach (var e in elements) d.Elements.Add(e);
        return d;
    }

    [Fact]
    public void Recall_placeholder_maps_to_a_captioned_box_of_its_size()
    {
        var label = LabelDocumentAdapter.ToRenderLabel(Doc(new RecallPlaceholderElement
        {
            X = 20, Y = 30, Command = "XG", Reference = "R:LOGO.GRF", WidthDots = 150, HeightDots = 90,
        }));
        var p = Assert.IsType<RenderPlaceholder>(Assert.Single(label.Elements));
        Assert.Equal((20, 30, 150, 90), (p.X, p.Y, p.WidthDots, p.HeightDots));
        Assert.Contains("R:LOGO.GRF", p.Caption);
    }

    [Fact]
    public void Visual_raw_zpl_gets_a_marker_and_configuration_raw_zpl_does_not()
    {
        var label = LabelDocumentAdapter.ToRenderLabel(Doc(
            new RawZplElement { X = 5, Y = 6, Zpl = "^FO5,6^XYZ^FS", IsVisual = true },
            new RawZplElement { Zpl = "^MMT", IsVisual = false }));
        var p = Assert.IsType<RenderPlaceholder>(Assert.Single(label.Elements));
        Assert.Equal((5, 6), (p.X, p.Y));
        Assert.True(p.WidthDots > 0 && p.HeightDots > 0);
    }

    [Fact]
    public void Placeholder_is_hatched_inside_its_box_and_blank_outside()
    {
        using var r = LabelRenderer.RenderBitmap(Label(300, 200, new RenderPlaceholder { X = 50, Y = 40, WidthDots = 150, HeightDots = 100, Caption = "Stored" }));
        var bounds = DarkBounds(r.Bitmap);
        Assert.NotNull(bounds);
        Assert.Equal(new SKRectI(50, 40, 200, 140), bounds!.Value);
        // Hatch lines cross the box; count dark pixels on a row away from the caption.
        int dark = 0;
        for (int x = 51; x < 199; x++) if (IsDark(r.Bitmap, x, 45)) dark++;
        Assert.InRange(dark, 5, 100);
    }

    [Fact]
    public void Placeholder_does_not_report_issues()
    {
        using var r = LabelRenderer.RenderBitmap(Label(100, 60, new RenderPlaceholder { WidthDots = 40, HeightDots = 20 }));
        Assert.Empty(r.Issues);
    }
}
