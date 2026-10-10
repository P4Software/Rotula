using Rotula.Core.Model;

namespace Rotula.Rendering.View;

/// <summary>Grid configuration for the design canvas.</summary>
/// <param name="Unit">Unit the spacing is expressed in.</param>
/// <param name="Spacing">Distance between grid lines, in <paramref name="Unit"/>.</param>
/// <param name="Visible">Whether the grid is drawn.</param>
/// <param name="SnapEnabled">Whether dragging snaps to the grid.</param>
public sealed record GridSettings(LengthUnit Unit = LengthUnit.Millimeters, double Spacing = 1.0,
    bool Visible = true, bool SnapEnabled = true)
{
    /// <summary>Grid spacing in dots for the given printer resolution (never smaller than 1 dot).</summary>
    public double SpacingDots(int dpi) => Math.Max(1.0, UnitConverter.Convert(Spacing, Unit, LengthUnit.Dots, dpi));
}

/// <summary>Pure snapping helpers working in dots. A printer can only place whole dots, so results can be rounded.</summary>
public static class Snapping
{
    /// <summary>Snaps a value to the nearest multiple of <paramref name="stepDots"/>.</summary>
    public static double SnapToGrid(double valueDots, double stepDots)
    {
        if (stepDots <= 0) return valueDots;
        return Math.Round(valueDots / stepDots, MidpointRounding.AwayFromZero) * stepDots;
    }

    /// <summary>Snaps both coordinates of a point to the grid described by <paramref name="grid"/>; no-op when snapping is off.</summary>
    public static (double X, double Y) SnapPoint(double xDots, double yDots, GridSettings grid, int dpi)
    {
        if (!grid.SnapEnabled) return (xDots, yDots);
        double step = grid.SpacingDots(dpi);
        return (SnapToGrid(xDots, step), SnapToGrid(yDots, step));
    }

    /// <summary>
    /// Snaps a value to the nearest target (for example another element's edge or centre, or a label margin)
    /// when it is within <paramref name="thresholdDots"/>; otherwise returns the value unchanged.
    /// </summary>
    /// <param name="valueDots">The coordinate being dragged.</param>
    /// <param name="targetsDots">Candidate coordinates to snap to.</param>
    /// <param name="thresholdDots">Maximum distance at which snapping engages.</param>
    /// <param name="snappedTo">The target used, or null when nothing was close enough.</param>
    public static double SnapToTargets(double valueDots, IEnumerable<double> targetsDots, double thresholdDots, out double? snappedTo)
    {
        snappedTo = null;
        double best = double.MaxValue;
        foreach (var t in targetsDots)
        {
            double d = Math.Abs(t - valueDots);
            if (d <= thresholdDots && d < best)
            {
                best = d;
                snappedTo = t;
            }
        }
        return snappedTo ?? valueDots;
    }

    /// <summary>Converts a screen-pixel snap tolerance into dots at the current zoom, so snapping feels the same at any zoom.</summary>
    public static double ThresholdDots(double thresholdPixels, double pixelsPerDot)
    {
        if (pixelsPerDot <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerDot));
        return thresholdPixels / pixelsPerDot;
    }

    /// <summary>Grid line positions (in dots) inside a visible range, for drawing the grid.</summary>
    public static IEnumerable<double> GridLines(double startDots, double endDots, double stepDots)
    {
        if (stepDots <= 0) yield break;
        long first = (long)Math.Ceiling(startDots / stepDots - 1e-9);
        long last = (long)Math.Floor(endDots / stepDots + 1e-9);
        for (long i = first; i <= last; i++)
            yield return i * stepDots;
    }
}
