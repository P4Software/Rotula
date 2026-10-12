using Rotula.Rendering;
using Rotula.Rendering.Input;

namespace Rotula.Tests;

/// <summary>
/// Windows lists some styles as families of their own ("Arial Black", "Segoe UI Semibold", "Bahnschrift Condensed") although the font manager only knows
/// the parent family. Those names used to draw in the default face; they must draw in the style they name.
/// </summary>
public class FontStyleVariantTests
{
    private static int InkOf(string family, bool bold = false)
    {
        var text = new RenderText { Text = "HHHHHHHH", FontHeightDots = 60, FamilyName = family, Bold = bold, X = 10, Y = 10 };
        using var result = LabelRenderer.RenderBitmap(new RenderLabel(700, 120, 300, new RenderElement[] { text }));
        int dark = 0;
        for (int y = 0; y < result.Bitmap.Height; y++)
            for (int x = 0; x < result.Bitmap.Width; x++)
                if (result.Bitmap.GetPixel(x, y).Red < 128) dark++;
        return dark;
    }

    [Fact]
    public void ArialBlack_IsDrawnHeavierThanArial_NotAsTheDefaultFace()
    {
        Assert.True(InkOf("Arial Black") > InkOf("Arial", bold: true) * 1.1, "Arial Black should be heavier than Arial Bold");
        Assert.NotEqual(InkOf("Segoe UI"), InkOf("Arial Black"));
    }

    [Fact]
    public void SegoeUiSemibold_IsHeavierThanRegular_AndLighterThanBold()
    {
        int regular = InkOf("Segoe UI"), semibold = InkOf("Segoe UI Semibold"), bold = InkOf("Segoe UI", bold: true);
        Assert.True(semibold > regular && semibold < bold, $"{regular} < {semibold} < {bold} expected");
    }

    [Fact]
    public void BahnschriftCondensed_IsNarrowerThanBahnschrift()
    {
        static int Width(string family)
        {
            var text = new RenderText { Text = "HHHHHHHH", FontHeightDots = 60, FamilyName = family, X = 10, Y = 10 };
            using var result = LabelRenderer.RenderBitmap(new RenderLabel(700, 120, 300, new RenderElement[] { text }));
            int right = 0;
            for (int x = 0; x < result.Bitmap.Width; x++)
                for (int y = 0; y < result.Bitmap.Height; y++)
                    if (result.Bitmap.GetPixel(x, y).Red < 128) { right = x; break; }
            return right;
        }
        Assert.True(Width("Bahnschrift Condensed") < Width("Bahnschrift") - 40);
    }

    private static int RightEdge(string family)
    {
        var text = new RenderText { Text = "HHHHHHHH", FontHeightDots = 60, FamilyName = family, X = 10, Y = 10 };
        using var result = LabelRenderer.RenderBitmap(new RenderLabel(700, 120, 300, new RenderElement[] { text }));
        int right = 0;
        for (int x = 0; x < result.Bitmap.Width; x++)
            for (int y = 0; y < result.Bitmap.Height; y++)
                if (result.Bitmap.GetPixel(x, y).Red < 128) { right = x; break; }
        return right;
    }

    /// <summary>Families the font manager does not list at all are loaded from the installed font file; skipped on a PC that lacks the font.</summary>
    private static bool Installed(string family) =>
        OperatingSystem.IsWindows() && Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts")?
            .GetValueNames().Any(n => n.StartsWith(family + " (", StringComparison.OrdinalIgnoreCase)) == true;

    [Fact]
    public void ArialNarrow_IsLoadedFromItsFile_AndIsNarrowerThanArial()
    {
        if (!Installed("Arial Narrow")) return;
        Assert.True(RightEdge("Arial Narrow") < RightEdge("Arial") - 40);
    }

    [Fact]
    public void FranklinGothicMedium_IsLoadedFromItsFile_NotTheDefaultFace()
    {
        if (!Installed("Franklin Gothic Medium")) return;
        Assert.NotEqual(RightEdge("Segoe UI"), RightEdge("Franklin Gothic Medium"));
    }
}
