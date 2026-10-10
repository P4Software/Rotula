using Rotula.Core.Elements;
using Rotula.Core.Zpl;

namespace Rotula.Core.Model;

/// <summary>Printable-area margins. The left/top values become the ^LH label home.</summary>
public sealed class LabelMargins
{
    /// <summary>Left margin; element X coordinates are relative to it.</summary>
    public Measure Left { get; set; } = Measure.Dots(0);
    /// <summary>Top margin; element Y coordinates are relative to it.</summary>
    public Measure Top { get; set; } = Measure.Dots(0);
    /// <summary>Right margin (used for bounds warnings only).</summary>
    public Measure Right { get; set; } = Measure.Dots(0);
    /// <summary>Bottom margin (used for bounds warnings only).</summary>
    public Measure Bottom { get; set; } = Measure.Dots(0);
}

/// <summary>Default font set with ^CF; text elements that omit a value inherit it.</summary>
public sealed class FontSpec
{
    /// <summary>Creates a font spec.</summary>
    public FontSpec(char fontId, int height, int width)
    {
        FontId = fontId;
        Height = height;
        Width = width;
    }

    /// <summary>ZPL font identifier ('0' is the scalable font, A-Z and 1-9 are bitmap fonts).</summary>
    public char FontId { get; set; }
    /// <summary>Character height in dots.</summary>
    public int Height { get; set; }
    /// <summary>Character width in dots (0 = proportional to height).</summary>
    public int Width { get; set; }
}

/// <summary>Print job options (^PQ).</summary>
public sealed class PrintOptions
{
    /// <summary>Total number of labels (1-99999999).</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>Pause after this many labels (0 = never).</summary>
    public int PauseEvery { get; set; }
    /// <summary>Replicates of each serial number (1 = none).</summary>
    public int Replicates { get; set; } = 1;
    /// <summary>Override the pause count (^PQ "o" parameter).</summary>
    public bool OverridePause { get; set; }
}

/// <summary>A declared variable with the sample value used when no data is merged.</summary>
public sealed class LabelVariable
{
    /// <summary>Creates a variable.</summary>
    public LabelVariable(string name, string sampleValue = "")
    {
        Name = name;
        SampleValue = sampleValue;
    }

    /// <summary>Variable name used in <c>{{name}}</c> placeholders (letters, digits, underscore).</summary>
    public string Name { get; set; }
    /// <summary>Value used when the merge data has no entry for <see cref="Name"/>.</summary>
    public string SampleValue { get; set; }
}

/// <summary>
/// The label design: size, printer settings and an ordered element list (the order is both
/// z-order and ZPL emission order). Lengths stay in user units until ZPL generation.
/// </summary>
public sealed class LabelDocument
{
    /// <summary>Label width.</summary>
    public Measure Width { get; set; } = Measure.Millimeters(100);
    /// <summary>Label height.</summary>
    public Measure Height { get; set; } = Measure.Millimeters(150);
    /// <summary>Printhead resolution used to convert lengths to dots.</summary>
    public PrinterDpi Dpi { get; set; } = PrinterDpi.Dpi203;
    /// <summary>Whole-label orientation (^PO).</summary>
    public LabelOrientation Orientation { get; set; } = LabelOrientation.Normal;
    /// <summary>Margins; left/top define ^LH.</summary>
    public LabelMargins Margins { get; set; } = new();
    /// <summary>Media type (^MT).</summary>
    public MediaType MediaType { get; set; } = MediaType.ThermalTransfer;
    /// <summary>Media tracking (^MN).</summary>
    public MediaTracking MediaTracking { get; set; } = MediaTracking.Web;
    /// <summary>Default field rotation (^FW); elements with a null rotation inherit it.</summary>
    public FieldRotation DefaultRotation { get; set; } = FieldRotation.Normal;
    /// <summary>Default font (^CF) or null to leave the printer default.</summary>
    public FontSpec? DefaultFont { get; set; } = new('0', 30, 30);
    /// <summary>Character encoding (^CI); UTF-8 (^CI28) by default.</summary>
    public ZplEncoding Encoding { get; set; } = ZplEncoding.Utf8;
    /// <summary>Print quantity options (^PQ).</summary>
    public PrintOptions Print { get; set; } = new();
    /// <summary>Declared variables with sample values.</summary>
    public List<LabelVariable> Variables { get; } = new();
    /// <summary>Elements in z-order / emission order.</summary>
    public List<LabelElement> Elements { get; } = new();

    /// <summary>Label width in dots at <see cref="Dpi"/>.</summary>
    public int WidthDots => Width.ToDots(Dpi);
    /// <summary>Label height in dots at <see cref="Dpi"/>.</summary>
    public int HeightDots => Height.ToDots(Dpi);
}
