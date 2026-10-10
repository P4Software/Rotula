using Rotula.Rendering.View;

namespace Rotula.App.Editing;

/// <summary>Result of snapping a dragged rectangle: the correction to apply and the guide lines to draw.</summary>
/// <param name="Dx">Horizontal correction in dots.</param>
/// <param name="Dy">Vertical correction in dots.</param>
/// <param name="VerticalGuides">X positions (element coordinates) of vertical guide lines that are active.</param>
/// <param name="HorizontalGuides">Y positions of horizontal guide lines that are active.</param>
public sealed record SnapResult(double Dx, double Dy, IReadOnlyList<double> VerticalGuides, IReadOnlyList<double> HorizontalGuides);

/// <summary>
/// Snapping while dragging: smart guides (edges and centres of other elements and of the label) win over the grid,
/// because aligning to a neighbour is almost always what the user means when the two are close.
/// </summary>
public static class DragSnapper
{
    /// <summary>Calculates the snap correction for a rectangle that is being dragged.</summary>
    /// <param name="moving">The dragged rectangle at its unsnapped position (element coordinates).</param>
    /// <param name="others">Other elements' bounds to align against.</param>
    /// <param name="label">The label rectangle in element coordinates.</param>
    /// <param name="grid">Grid settings; grid snapping applies when no guide is close enough.</param>
    /// <param name="dpi">Printer resolution.</param>
    /// <param name="thresholdDots">Guide snapping distance in dots (derive it from pixels with <see cref="Snapping.ThresholdDots"/>).</param>
    /// <param name="useGuides">Whether smart guides are enabled.</param>
    public static SnapResult Snap(RectD moving, IReadOnlyList<RectD> others, RectD label, GridSettings grid, int dpi, double thresholdDots, bool useGuides = true)
    {
        var xTargets = new List<double> { label.X, label.CenterX, label.Right, 0 };
        var yTargets = new List<double> { label.Y, label.CenterY, label.Bottom, 0 };
        foreach (var o in others)
        {
            xTargets.Add(o.X); xTargets.Add(o.CenterX); xTargets.Add(o.Right);
            yTargets.Add(o.Y); yTargets.Add(o.CenterY); yTargets.Add(o.Bottom);
        }

        var (dx, vGuide) = SnapAxis(new[] { moving.X, moving.CenterX, moving.Right }, xTargets, thresholdDots, useGuides);
        var (dy, hGuide) = SnapAxis(new[] { moving.Y, moving.CenterY, moving.Bottom }, yTargets, thresholdDots, useGuides);

        if (vGuide is null && grid.SnapEnabled)
        {
            // The grid starts at the label's top-left corner, which is not the element origin when margins are set.
            double step = grid.SpacingDots(dpi);
            dx = Snapping.SnapToGrid(moving.X - label.X, step) + label.X - moving.X;
        }
        if (hGuide is null && grid.SnapEnabled)
        {
            double step = grid.SpacingDots(dpi);
            dy = Snapping.SnapToGrid(moving.Y - label.Y, step) + label.Y - moving.Y;
        }

        return new SnapResult(dx, dy,
            vGuide is null ? Array.Empty<double>() : new[] { vGuide.Value },
            hGuide is null ? Array.Empty<double>() : new[] { hGuide.Value });
    }

    private static (double Delta, double? Guide) SnapAxis(double[] candidates, List<double> targets, double threshold, bool useGuides)
    {
        if (!useGuides) return (0, null);
        double bestDelta = 0;
        double? bestGuide = null;
        double bestDistance = double.MaxValue;
        foreach (double c in candidates)
        {
            double snapped = Snapping.SnapToTargets(c, targets, threshold, out double? target);
            if (target is null) continue;
            double distance = Math.Abs(snapped - c);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestDelta = snapped - c;
                bestGuide = target;
            }
        }
        return (bestDelta, bestGuide);
    }
}
