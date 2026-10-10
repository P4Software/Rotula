using Rotula.Core.Model;
using Rotula.Rendering.View;

namespace Rotula.Tests;

/// <summary>Pure logic: units, zoom, rulers, grid and snapping.</summary>
public class RenderingViewTests
{
    // -------------------------------------------------------------- units

    [Theory]
    [InlineData(25.4, LengthUnit.Millimeters, LengthUnit.Inches, 203, 1.0)]
    [InlineData(1.0, LengthUnit.Inches, LengthUnit.Dots, 203, 203.0)]
    [InlineData(300.0, LengthUnit.Dots, LengthUnit.Inches, 300, 1.0)]
    [InlineData(25.4, LengthUnit.Millimeters, LengthUnit.Dots, 600, 600.0)]
    [InlineData(5.0, LengthUnit.Millimeters, LengthUnit.Millimeters, 203, 5.0)]
    public void Units_convert(double value, LengthUnit from, LengthUnit to, int dpi, double expected)
    {
        Assert.Equal(expected, UnitConverter.Convert(value, from, to, dpi), 9);
    }

    [Fact]
    public void Unit_conversion_round_trips()
    {
        double dots = UnitConverter.MmToDots(37.5, 300);
        Assert.Equal(37.5, UnitConverter.DotsToMm(dots, 300), 9);
    }

    [Fact]
    public void Unit_conversion_rejects_a_zero_dpi()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnitConverter.Convert(1, LengthUnit.Dots, LengthUnit.Inches, 0));
    }

    // -------------------------------------------------------------- zoom

    [Fact]
    public void Zoom_is_clamped_between_10_and_800_percent()
    {
        Assert.Equal(10, ZoomModel.FromPercent(1).Percent);
        Assert.Equal(800, ZoomModel.FromPercent(5000).Percent);
        Assert.Equal(100, new ZoomModel(double.NaN).Percent);
        Assert.Equal(100, default(ZoomModel).Percent);
    }

    [Fact]
    public void Zoom_in_and_out_walk_the_preset_steps_and_stop_at_the_ends()
    {
        var z = ZoomModel.FromPercent(100);
        Assert.Equal(125, z.ZoomIn().Percent);
        Assert.Equal(75, z.ZoomOut().Percent);
        Assert.Equal(150, ZoomModel.FromPercent(130).ZoomIn().Percent); // between steps goes to the next one
        Assert.Equal(800, ZoomModel.FromPercent(800).ZoomIn().Percent);
        Assert.Equal(10, ZoomModel.FromPercent(10).ZoomOut().Percent);

        var up = ZoomModel.FromPercent(10);
        for (int i = 0; i < 50; i++) up = up.ZoomIn();
        Assert.Equal(800, up.Percent);
    }

    [Fact]
    public void At_100_percent_one_inch_is_96_device_independent_pixels()
    {
        var z = ZoomModel.FromPercent(100);
        Assert.Equal(96.0, z.PixelsPerDot(203) * 203, 9);
        Assert.Equal(192.0, ZoomModel.FromPercent(200).PixelsPerDot(300) * 300, 9);
    }

    [Fact]
    public void Fit_to_viewport_keeps_the_whole_label_visible_with_a_margin()
    {
        // 812x406 dots at 203 DPI (4x2 inch) in an 800x500 viewport with 24 px margin.
        var z = ZoomModel.FitToViewport(800, 500, 812, 406, 203, margin: 24);
        double pxPerDot = z.PixelsPerDot(203);
        Assert.True(812 * pxPerDot <= 800 - 48 + 0.5);
        Assert.True(406 * pxPerDot <= 500 - 48 + 0.5);
    }

    [Fact]
    public void Scroll_after_zoom_keeps_the_point_under_the_cursor()
    {
        double oldScroll = 120, anchor = 200, oldPpd = 0.5, newPpd = 1.0;
        double dotUnderCursorBefore = (oldScroll + anchor) / oldPpd;
        double newScroll = ZoomModel.ScrollAfterZoom(oldScroll, anchor, oldPpd, newPpd);
        double dotUnderCursorAfter = (newScroll + anchor) / newPpd;
        Assert.Equal(dotUnderCursorBefore, dotUnderCursorAfter, 9);
    }

    // -------------------------------------------------------------- ruler

    [Fact]
    public void Millimetre_ruler_has_labelled_ticks_that_are_far_enough_apart()
    {
        double ppd = ZoomModel.FromPercent(100).PixelsPerDot(203);
        var ticks = Ruler.GetTicks(LengthUnit.Millimeters, 203, ppd, 0, UnitConverter.MmToDots(100, 203));

        Assert.Equal(0, ticks[0].PositionDots, 6);
        Assert.True(ticks[0].IsMajor);
        Assert.Equal("0", ticks[0].Label);

        var majors = ticks.Where(t => t.IsMajor).ToList();
        Assert.True(majors.Count >= 3);
        double gapPx = (majors[1].PositionDots - majors[0].PositionDots) * ppd;
        Assert.True(gapPx >= 56 - 1e-6);
        // Only labelled ticks carry text.
        Assert.All(ticks, t => Assert.Equal(t.IsMajor, t.Label.Length > 0));
    }

    [Fact]
    public void Zooming_in_makes_the_ruler_finer()
    {
        var coarse = Ruler.GetTicks(LengthUnit.Millimeters, 203, ZoomModel.FromPercent(50).PixelsPerDot(203), 0, 800);
        var fine = Ruler.GetTicks(LengthUnit.Millimeters, 203, ZoomModel.FromPercent(400).PixelsPerDot(203), 0, 800);
        Assert.True(fine.Count > coarse.Count);
    }

    [Fact]
    public void Inch_ruler_uses_binary_fractions_and_formats_labels()
    {
        double ppd = ZoomModel.FromPercent(800).PixelsPerDot(203);
        var ticks = Ruler.GetTicks(LengthUnit.Inches, 203, ppd, 0, 203);
        Assert.Contains(ticks, t => t.IsMajor && t.Label == "1");
        Assert.Contains(ticks, t => Math.Abs(t.PositionDots - 203.0 / 2) < 1e-6); // half-inch mark exists
    }

    [Fact]
    public void Dot_ruler_covers_the_requested_range_only()
    {
        var ticks = Ruler.GetTicks(LengthUnit.Dots, 203, 1.0, 100, 200);
        Assert.All(ticks, t => Assert.InRange(t.PositionDots, 100 - 1e-6, 200 + 1e-6));
        Assert.Contains(ticks, t => t.IsMajor && t.Label == "100");
    }

    // -------------------------------------------------------------- grid & snap

    [Fact]
    public void Grid_spacing_converts_to_dots_and_is_at_least_one_dot()
    {
        Assert.Equal(8.0, new GridSettings(LengthUnit.Millimeters, 1.0).SpacingDots(203), 0);
        Assert.Equal(1.0, new GridSettings(LengthUnit.Dots, 0.2).SpacingDots(203));
    }

    [Theory]
    [InlineData(13.0, 8.0, 16.0)]
    [InlineData(11.9, 8.0, 8.0)]
    [InlineData(4.0, 8.0, 8.0)]   // exact halves round away from zero
    [InlineData(-5.0, 8.0, -8.0)]
    [InlineData(7.0, 0.0, 7.0)]   // no grid, no snap
    public void Snap_to_grid_rounds_to_the_nearest_line(double value, double step, double expected)
    {
        Assert.Equal(expected, Snapping.SnapToGrid(value, step));
    }

    [Fact]
    public void Snap_point_respects_the_snap_switch()
    {
        var on = new GridSettings(LengthUnit.Dots, 10, SnapEnabled: true);
        var off = on with { SnapEnabled = false };
        Assert.Equal((20.0, 30.0), Snapping.SnapPoint(17, 33, on, 203));
        Assert.Equal((17.0, 33.0), Snapping.SnapPoint(17, 33, off, 203));
    }

    [Fact]
    public void Snap_to_targets_picks_the_closest_target_within_the_threshold()
    {
        double v = Snapping.SnapToTargets(98, [0, 100, 105, 200], 5, out var hit);
        Assert.Equal(100, v);
        Assert.Equal(100, hit);

        double far = Snapping.SnapToTargets(150, [0, 100, 200], 5, out var none);
        Assert.Equal(150, far);
        Assert.Null(none);
    }

    [Fact]
    public void Snap_threshold_shrinks_in_dots_as_you_zoom_in()
    {
        Assert.Equal(10.0, Snapping.ThresholdDots(8, 0.8), 9);
        Assert.True(Snapping.ThresholdDots(8, 4.0) < Snapping.ThresholdDots(8, 0.5));
    }

    [Fact]
    public void Grid_lines_cover_the_visible_range()
    {
        var lines = Snapping.GridLines(5, 35, 10).ToList();
        Assert.Equal([10.0, 20.0, 30.0], lines);
    }
}
