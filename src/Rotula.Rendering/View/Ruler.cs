using Rotula.Core.Model;

namespace Rotula.Rendering.View;

/// <summary>One mark on a ruler.</summary>
/// <param name="PositionDots">Position along the label in dots (0 = label origin).</param>
/// <param name="IsMajor">True for labelled marks.</param>
/// <param name="Label">Text to print next to a major mark; empty for minor marks.</param>
public readonly record struct RulerTick(double PositionDots, bool IsMajor, string Label);

/// <summary>Pure ruler logic: picks readable tick spacing for any unit and zoom.</summary>
public static class Ruler
{
    // Candidate step sizes expressed in the ruler's own unit, ascending.
    private static readonly double[] MmSteps = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
    private static readonly double[] InchSteps = [1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 5, 10, 20, 50];
    private static readonly double[] DotSteps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000];

    /// <summary>
    /// Builds the ticks visible between <paramref name="startDots"/> and <paramref name="endDots"/>.
    /// </summary>
    /// <param name="unit">Unit shown on the ruler.</param>
    /// <param name="dpi">Printer resolution.</param>
    /// <param name="pixelsPerDot">Current scale, see <see cref="ZoomModel.PixelsPerDot"/>.</param>
    /// <param name="startDots">First visible position in dots.</param>
    /// <param name="endDots">Last visible position in dots.</param>
    /// <param name="minMinorPixels">Smallest pixel gap allowed between two minor ticks.</param>
    /// <param name="minMajorPixels">Smallest pixel gap allowed between two labelled ticks.</param>
    public static IReadOnlyList<RulerTick> GetTicks(LengthUnit unit, int dpi, double pixelsPerDot,
        double startDots, double endDots, double minMinorPixels = 8, double minMajorPixels = 56)
    {
        if (pixelsPerDot <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerDot));
        if (endDots < startDots) (startDots, endDots) = (endDots, startDots);

        double dotsPerUnit = UnitConverter.Convert(1, unit, LengthUnit.Dots, dpi);
        double pixelsPerUnit = dotsPerUnit * pixelsPerDot;

        double minor = ChooseStep(StepsFor(unit), pixelsPerUnit, minMinorPixels, 0);
        // The labelled step must be a whole multiple of the minor step so labels always land on a minor tick.
        double major = ChooseStep(StepsFor(unit), pixelsPerUnit, minMajorPixels, minor);

        double startUnits = UnitConverter.Convert(startDots, LengthUnit.Dots, unit, dpi);
        double endUnits = UnitConverter.Convert(endDots, LengthUnit.Dots, unit, dpi);
        long first = (long)Math.Ceiling(startUnits / minor - 1e-9);
        long last = (long)Math.Floor(endUnits / minor + 1e-9);

        double ratio = major / minor;
        var ticks = new List<RulerTick>((int)Math.Min(int.MaxValue, Math.Max(0, last - first + 1)));
        for (long i = first; i <= last; i++)
        {
            double valueUnits = i * minor;
            bool isMajor = Math.Abs(i / ratio - Math.Round(i / ratio)) < 1e-6;
            ticks.Add(new RulerTick(
                UnitConverter.Convert(valueUnits, unit, LengthUnit.Dots, dpi),
                isMajor,
                isMajor ? FormatValue(valueUnits, unit) : string.Empty));
        }
        return ticks;
    }

    private static double[] StepsFor(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimeters => MmSteps,
        LengthUnit.Inches => InchSteps,
        _ => DotSteps,
    };

    /// <summary>Smallest candidate whose pixel size reaches <paramref name="minPixels"/> and that is a multiple of <paramref name="mustDivide"/>.</summary>
    private static double ChooseStep(double[] steps, double pixelsPerUnit, double minPixels, double mustDivide)
    {
        foreach (var s in steps)
        {
            if (s * pixelsPerUnit < minPixels) continue;
            double q = mustDivide > 0 ? s / mustDivide : 1;
            if (mustDivide <= 0 || (Math.Abs(q - Math.Round(q)) < 1e-6 && Math.Round(q) >= 1)) return s;
        }
        return steps[^1];
    }

    private static string FormatValue(double value, LengthUnit unit) => unit switch
    {
        // Inches use up to 3 decimals so 1/16 steps (0.0625) stay distinguishable from 1/8 (0.125).
        LengthUnit.Inches => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
    };
}
