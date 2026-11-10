using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>An image is bounded by its total pixel count, not only per axis (9999 x 32000 passed both axis checks and needed GBs).</summary>
public class ImagePixelCapTests
{
    private static GrayscaleBitmap Tiny() => new(2, 2, new byte[] { 0, 255, 255, 0 });

    [Fact]
    public void AnImageWithTooManyPixels_IsReportedByTheGenerator_NotRendered()
    {
        var doc = CoreTestSupport.NewDoc(new ImageElement { Source = Tiny(), WidthDots = 9999, HeightDots = 32000 });

        var result = ZplGenerator.Generate(doc);

        Assert.Contains(result.Issues, i => i.MessageKey == "Image.Size.Pixels" && i.Severity == ValidationSeverity.Error);
        Assert.DoesNotContain("^GFA", result.Zpl);
    }

    [Fact]
    public void ToMonochrome_RefusesAnImageOverThePixelCap_BeforeAllocating()
    {
        var img = new ImageElement { Source = Tiny(), WidthDots = 9999, HeightDots = 32000 };

        Assert.False(ImageProcessor.IsWithinPixelLimit(img));
        Assert.Throws<InvalidOperationException>(() => ImageProcessor.ToMonochrome(img));
    }

    [Fact]
    public void AnImageOnTheCap_StillWorks()
    {
        // 6000 x 6000 = 36 million pixels, below the cap; a tiny source scaled up keeps this cheap to build.
        var img = new ImageElement { Source = Tiny(), WidthDots = 64, HeightDots = 64 };

        Assert.True(ImageProcessor.IsWithinPixelLimit(img));
        Assert.Equal(64, ImageProcessor.ToMonochrome(img).Width);
        Assert.True(ImageProcessor.IsWithinPixelLimit(new ImageElement { Source = Tiny(), WidthDots = 6000, HeightDots = 6000 }));
    }

    [Fact]
    public void ARotatedImageIsMeasuredBeforeRotation()
    {
        var img = new ImageElement { Source = Tiny(), WidthDots = 8000, HeightDots = 8000, Rotation = FieldRotation.Rotated90 };

        Assert.False(ImageProcessor.IsWithinPixelLimit(img)); // 64 million pixels either way
    }
}
