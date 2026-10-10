using ModelContextProtocol;
using Rotula.Core.Model;

namespace Rotula.Mcp.Session;

/// <summary>
/// Length handling for the tools. Assistants think in millimetres or inches, Core stores dots, so every geometry argument is
/// converted here. The unit is "mm", "in" or "dots"; when a tool is given no unit the label's own unit is used.
/// </summary>
public static class Units
{
    /// <summary>Largest absolute length accepted in any unit before conversion (guards against overflow).</summary>
    public const double MaxAbsolute = 100_000;

    /// <summary>Normalises a unit argument to "mm", "in" or "dots"; null or empty means the label's own unit.</summary>
    public static string Normalize(string? unit, LabelDocument? doc)
    {
        if (string.IsNullOrWhiteSpace(unit)) return doc is null ? "mm" : OfLabel(doc);
        return unit.Trim().ToLowerInvariant() switch
        {
            "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres" => "mm",
            "in" or "inch" or "inches" or "\"" => "in",
            "dot" or "dots" or "px" => "dots",
            _ => throw new McpException($"Unknown unit \"{unit}\". Use \"mm\", \"in\" or \"dots\"."),
        };
    }

    /// <summary>The unit a label was created in, as "mm", "in" or "dots".</summary>
    public static string OfLabel(LabelDocument doc) => doc.Width.Unit switch
    {
        LengthUnit.Inches => "in",
        LengthUnit.Dots => "dots",
        _ => "mm",
    };

    /// <summary>Converts a length in <paramref name="unit"/> to whole dots at the label's resolution.</summary>
    public static int ToDots(double value, string unit, PrinterDpi dpi)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > MaxAbsolute)
            throw new McpException($"The length {value} is not usable. Use a number between -{MaxAbsolute:0} and {MaxAbsolute:0}.");
        return ToMeasure(value, unit).ToDots(dpi);
    }

    /// <summary>Builds a <see cref="Measure"/> from a value and a normalised unit.</summary>
    public static Measure ToMeasure(double value, string unit) => unit switch
    {
        "in" => Measure.Inches(value),
        "dots" => Measure.Dots((int)Math.Round(value, MidpointRounding.AwayFromZero)),
        _ => Measure.Millimeters(value),
    };

    /// <summary>Converts dots back to <paramref name="unit"/>, rounded to two decimals (four for inches).</summary>
    public static double FromDots(int dots, string unit, PrinterDpi dpi) => unit switch
    {
        "in" => Math.Round(dots / (double)(int)dpi, 4),
        "dots" => dots,
        _ => Math.Round(dots * 25.4 / (int)dpi, 2),
    };

    /// <summary>The value of a measure in <paramref name="unit"/> (used to show the label size).</summary>
    public static double Express(Measure m, string unit, PrinterDpi dpi) =>
        // A value already in the wanted unit is returned as typed; converting through whole dots would turn 100 mm into 99.97 mm.
        m.Unit == (unit switch { "in" => LengthUnit.Inches, "dots" => LengthUnit.Dots, _ => LengthUnit.Millimeters }) ? m.Value : FromDots(m.ToDots(dpi), unit, dpi);

    /// <summary>Parses the resolution argument (203, 300 or 600).</summary>
    public static PrinterDpi ParseDpi(int dpi) => dpi switch
    {
        203 => PrinterDpi.Dpi203,
        300 => PrinterDpi.Dpi300,
        600 => PrinterDpi.Dpi600,
        _ => throw new McpException($"{dpi} DPI is not supported. Use 203, 300 or 600."),
    };
}
