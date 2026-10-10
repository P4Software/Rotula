using Rotula.Core.Model;

namespace Rotula.Rendering.View;

/// <summary>Pure conversions between millimetres, inches and printer dots.</summary>
public static class UnitConverter
{
    /// <summary>Millimetres per inch (exact by definition).</summary>
    public const double MmPerInch = 25.4;

    /// <summary>Converts a length between units; <paramref name="dpi"/> is the printer resolution used for dots.</summary>
    public static double Convert(double value, LengthUnit from, LengthUnit to, int dpi)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "The printer resolution must be greater than zero.");
        if (from == to) return value;
        double inches = from switch
        {
            LengthUnit.Millimeters => value / MmPerInch,
            LengthUnit.Inches => value,
            _ => value / dpi,
        };
        return to switch
        {
            LengthUnit.Millimeters => inches * MmPerInch,
            LengthUnit.Inches => inches,
            _ => inches * dpi,
        };
    }

    /// <summary>Millimetres to dots (not rounded).</summary>
    public static double MmToDots(double mm, int dpi) => Convert(mm, LengthUnit.Millimeters, LengthUnit.Dots, dpi);

    /// <summary>Dots to millimetres.</summary>
    public static double DotsToMm(double dots, int dpi) => Convert(dots, LengthUnit.Dots, LengthUnit.Millimeters, dpi);

    /// <summary>Inches to dots (not rounded).</summary>
    public static double InchesToDots(double inches, int dpi) => Convert(inches, LengthUnit.Inches, LengthUnit.Dots, dpi);

    /// <summary>Dots to inches.</summary>
    public static double DotsToInches(double dots, int dpi) => Convert(dots, LengthUnit.Dots, LengthUnit.Inches, dpi);
}
