using Rotula.Rendering.Device;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>Rescaling a render label to the resolution of a printer driver keeps the physical size and whole-dot barcode modules.</summary>
public class RenderLabelScalerTests
{
    private static RenderLabel Label(int dpi, params RenderElement[] elements) =>
        new(dpi == 203 ? 812 : 1200, dpi == 203 ? 406 : 600, dpi, elements);

    [Fact]
    public void SameDpiReturnsTheSameLabel()
    {
        var label = Label(203, new RenderBox { X = 10, Y = 10, WidthDots = 50, HeightDots = 20 });

        Assert.Same(label, RenderLabelScaler.ToResolution(label, 203));
    }

    [Fact]
    public void DoublingDpiDoublesSizeAndModule()
    {
        var label = Label(203,
            new RenderBarcode { X = 20, Y = 30, ModuleDots = 2, HeightDots = 80 },
            new RenderBox { X = 10, Y = 12, WidthDots = 50, HeightDots = 20 });

        var scaled = RenderLabelScaler.ToResolution(label, 406);

        Assert.Equal(406, scaled.Dpi);
        Assert.Equal(label.WidthDots * 2, scaled.WidthDots);
        Assert.Equal(label.HeightDots * 2, scaled.HeightDots);
        var barcode = Assert.IsType<RenderBarcode>(scaled.Elements[0]);
        Assert.Equal(4, barcode.ModuleDots);
        Assert.Equal(160, barcode.HeightDots);
        var box = Assert.IsType<RenderBox>(scaled.Elements[1]);
        Assert.Equal(20, box.X);
        Assert.Equal(24, box.Y);
        Assert.Equal(100, box.WidthDots);
        Assert.Equal(40, box.HeightDots);
    }

    [Fact]
    public void ModuleIsAlwaysAWholeNumberOfDotsAndAtLeastOne()
    {
        var up = RenderLabelScaler.ToResolution(Label(203, new RenderBarcode { ModuleDots = 2 }), 300);
        Assert.Equal((int)Math.Round(2 * 300 / 203.0, MidpointRounding.AwayFromZero), ((RenderBarcode)up.Elements[0]).ModuleDots);
        Assert.Equal(3, ((RenderBarcode)up.Elements[0]).ModuleDots);

        // Scaling a 1-dot module far down must never produce a zero-width bar.
        var down = RenderLabelScaler.ToResolution(Label(600, new RenderBarcode { ModuleDots = 1 }), 203);
        Assert.Equal(1, ((RenderBarcode)down.Elements[0]).ModuleDots);
    }

    [Fact]
    public void PhysicalSizeIsKept()
    {
        var label = Label(203);

        foreach (int target in new[] { 150, 300, 600, 1200 })
        {
            var scaled = RenderLabelScaler.ToResolution(label, target);
            double oneDotMm = 25.4 / target;
            Assert.True(Math.Abs(scaled.WidthMm - label.WidthMm) < oneDotMm, $"width at {target} dpi");
            Assert.True(Math.Abs(scaled.HeightMm - label.HeightMm) < oneDotMm, $"height at {target} dpi");
        }
    }

    [Fact]
    public void EveryElementKindIsScaled()
    {
        var label = Label(203,
            new RenderText { X = 5, Y = 6, Text = "Hi", FontHeightDots = 30, WidthDots = 200, HeightDots = 90, HangingIndentDots = 4, LineSpacingDots = 2 },
            new RenderPlaceholder { X = 5, Y = 6, WidthDots = 120, HeightDots = 80, Caption = "R:LOGO.GRF" },
            new RenderBox { X = 5, Y = 6, WidthDots = 50, HeightDots = 20, ThicknessDots = 2, CornerRadiusDots = 4 },
            new RenderCircle { X = 5, Y = 6, DiameterDots = 40, ThicknessDots = 3 },
            new RenderEllipse { X = 5, Y = 6, WidthDots = 60, HeightDots = 30, ThicknessDots = 3 },
            new RenderDiagonal { X = 5, Y = 6, WidthDots = 60, HeightDots = 30, ThicknessDots = 3 },
            new RenderLine { X = 5, Y = 6, DeltaX = -40, DeltaY = 25, ThicknessDots = 3 },
            new RenderBarcode { X = 5, Y = 6, ModuleDots = 2, HeightDots = 80, Data = "123" },
            new RenderImage { X = 5, Y = 6, EncodedBytes = [1, 2, 3], WidthDots = 64, HeightDots = 32 });

        var scaled = RenderLabelScaler.ToResolution(label, 406);

        Assert.Equal(label.Elements.Count, scaled.Elements.Count);
        for (int i = 0; i < label.Elements.Count; i++)
        {
            Assert.Equal(label.Elements[i].GetType(), scaled.Elements[i].GetType());
            Assert.NotEqual(label.Elements[i], scaled.Elements[i]);
        }

        // The bitmap itself is never touched, only the size it is drawn at.
        var image = Assert.IsType<RenderImage>(scaled.Elements[8]);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.EncodedBytes);
        Assert.Equal(128, image.WidthDots);
        Assert.Equal(64, image.HeightDots);

        var line = Assert.IsType<RenderLine>(scaled.Elements[6]);
        Assert.Equal(-80, line.DeltaX);
        Assert.Equal(50, line.DeltaY);

        var text = Assert.IsType<RenderText>(scaled.Elements[0]);
        Assert.Equal(60, text.FontHeightDots);
        Assert.Equal(400, text.WidthDots);
        Assert.Equal(180, text.HeightDots);
        Assert.Equal(8, text.HangingIndentDots);
        Assert.Equal(4, text.LineSpacingDots);
    }

    [Fact]
    public void RotationAndFlagsSurviveScaling()
    {
        var label = Label(203, new RenderBox
        {
            X = 10, Y = 10, WidthDots = 50, HeightDots = 20,
            Rotation = RenderRotation.Rotate270, Typeset = true, Reverse = true, Dimmed = true,
        }) with { Inverted = true, Mirrored = true };

        var scaled = RenderLabelScaler.ToResolution(label, 300);

        Assert.True(scaled.Inverted);
        Assert.True(scaled.Mirrored);
        var box = Assert.IsType<RenderBox>(scaled.Elements[0]);
        Assert.Equal(RenderRotation.Rotate270, box.Rotation);
        Assert.True(box.Typeset);
        Assert.True(box.Reverse);
        Assert.True(box.Dimmed);
    }

    [Fact]
    public void ZeroMeansUnlimitedAndStaysZero()
    {
        // 0 width = no wrapping, 0 height = no height limit, 0 thickness = nothing to draw: none of them may become 1.
        var label = Label(203, new RenderText { WidthDots = 0, HeightDots = 0 }, new RenderBox { WidthDots = 10, HeightDots = 10, ThicknessDots = 0, CornerRadiusDots = 0 });

        var scaled = RenderLabelScaler.ToResolution(label, 300);

        var text = Assert.IsType<RenderText>(scaled.Elements[0]);
        Assert.Equal(0, text.WidthDots);
        Assert.Equal(0, text.HeightDots);
        var box = Assert.IsType<RenderBox>(scaled.Elements[1]);
        Assert.Equal(0, box.ThicknessDots);
        Assert.Equal(0, box.CornerRadiusDots);
    }

    [Fact]
    public void InvalidTargetDpiIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderLabelScaler.ToResolution(Label(203), 0));
    }
}
