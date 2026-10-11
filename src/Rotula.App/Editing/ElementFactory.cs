using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.App.Editing;

/// <summary>The kinds of element the toolbox can create.</summary>
public enum ToolboxKind
{
    /// <summary>Fixed text.</summary>
    Text,
    /// <summary>Code 128 barcode.</summary>
    Barcode,
    /// <summary>QR code.</summary>
    QrCode,
    /// <summary>Rectangle.</summary>
    Box,
    /// <summary>Line.</summary>
    Line,
    /// <summary>Circle.</summary>
    Circle,
    /// <summary>Picture.</summary>
    Image,
    /// <summary>Ellipse.</summary>
    Ellipse,
    /// <summary>Diagonal line.</summary>
    DiagonalLine,
}

/// <summary>Creates new elements with sensible defaults for the toolbox.</summary>
public static class ElementFactory
{
    /// <summary>
    /// Creates an element of the requested kind. <paramref name="index"/> staggers the position so repeated adds do not
    /// land exactly on top of each other.
    /// </summary>
    public static LabelElement Create(ToolboxKind kind, LabelDocument doc, int index, string defaultText = "Text", string defaultData = "123456789")
    {
        int offset = 24 + (index % 10) * 16;
        LabelElement e = kind switch
        {
            ToolboxKind.Text => new TextElement { Text = defaultText },
            ToolboxKind.Barcode => new BarcodeElement { Symbology = BarcodeSymbology.Code128, Data = defaultData, Height = 80 },
            ToolboxKind.QrCode => new BarcodeElement { Symbology = BarcodeSymbology.QrCode, Data = "https://example.com", ModuleSize = 4 },
            ToolboxKind.Box => new BoxElement { WidthDots = 200, HeightDots = 100, ThicknessDots = 3 },
            ToolboxKind.Line => new LineElement { LengthDots = 200, ThicknessDots = 3 },
            ToolboxKind.Circle => new CircleElement { DiameterDots = 100, ThicknessDots = 3 },
            ToolboxKind.Image => new ImageElement(),
            ToolboxKind.Ellipse => new EllipseElement { WidthDots = 160, HeightDots = 100, ThicknessDots = 3 },
            ToolboxKind.DiagonalLine => new DiagonalLineElement { WidthDots = 150, HeightDots = 150, ThicknessDots = 3 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        e.X = offset;
        e.Y = offset;
        return e;
    }

    /// <summary>
    /// A new barcode of any symbology, filled with the catalogue's example data so that it validates and exports to ZPL straight away
    /// (the options keep their defaults, which are the values ZPL assumes).
    /// </summary>
    public static BarcodeElement CreateBarcode(BarcodeSymbology symbology)
    {
        var info = Core.Barcodes.BarcodeCatalog.Get(symbology);
        var element = new BarcodeElement { Symbology = symbology, Data = info.ExampleData, Height = 80, X = 24, Y = 24 };
        if (info.AlwaysPrintsHumanReadable) element.HumanReadable = true;
        return element;
    }
}
