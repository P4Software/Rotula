using System.Globalization;

namespace Rotula.Core.Model;

/// <summary>Unit in which a <see cref="Measure"/> is expressed.</summary>
public enum LengthUnit
{
    /// <summary>Printer dots (device pixels at the chosen DPI).</summary>
    Dots,
    /// <summary>Millimetres.</summary>
    Millimeters,
    /// <summary>Inches.</summary>
    Inches,
}

/// <summary>Supported printhead resolutions. The numeric value of each member is the DPI.</summary>
public enum PrinterDpi
{
    /// <summary>203 DPI (8 dots/mm).</summary>
    Dpi203 = 203,
    /// <summary>300 DPI (12 dots/mm).</summary>
    Dpi300 = 300,
    /// <summary>600 DPI (24 dots/mm).</summary>
    Dpi600 = 600,
}

/// <summary>
/// A length in a user-facing unit. Conversion to dots is only possible once the printer
/// resolution is known, which is why the document keeps lengths unconverted until generation.
/// </summary>
/// <param name="Value">Numeric value in <paramref name="Unit"/>.</param>
/// <param name="Unit">The unit of <paramref name="Value"/>.</param>
public readonly record struct Measure(double Value, LengthUnit Unit)
{
    /// <summary>Creates a length in millimetres.</summary>
    public static Measure Millimeters(double value) => new(value, LengthUnit.Millimeters);

    /// <summary>Creates a length in inches.</summary>
    public static Measure Inches(double value) => new(value, LengthUnit.Inches);

    /// <summary>Creates a length in printer dots.</summary>
    public static Measure Dots(int value) => new(value, LengthUnit.Dots);

    /// <summary>Converts the length to whole dots at the given resolution (half away from zero).</summary>
    public int ToDots(PrinterDpi dpi)
    {
        double dots = Unit switch
        {
            LengthUnit.Dots => Value,
            LengthUnit.Millimeters => Value * (int)dpi / 25.4,
            LengthUnit.Inches => Value * (int)dpi,
            _ => throw new ArgumentOutOfRangeException(nameof(Unit)),
        };
        return (int)Math.Round(dots, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value}{Unit switch { LengthUnit.Dots => "dots", LengthUnit.Millimeters => "mm", _ => "in" }}");
}
