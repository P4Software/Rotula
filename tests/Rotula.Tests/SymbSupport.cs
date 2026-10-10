using System.Reflection;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>Shared helpers for the Symb* tests: one sample element per symbology and a full-property comparison.</summary>
internal static class SymbSupport
{
    /// <summary>Every symbology, as theory data.</summary>
    public static TheoryData<BarcodeSymbology> AllSymbologies()
    {
        var data = new TheoryData<BarcodeSymbology>();
        foreach (var s in Enum.GetValues<BarcodeSymbology>()) data.Add(s);
        return data;
    }

    /// <summary>The symbologies the importer can read back completely (everything in the enum).</summary>
    public static IEnumerable<BarcodeSymbology> Symbologies => Enum.GetValues<BarcodeSymbology>();

    /// <summary>The element the catalogue's example data describes, with every option at its default.</summary>
    public static BarcodeElement Default(BarcodeSymbology s)
    {
        var info = BarcodeCatalog.Get(s);
        var e = new BarcodeElement { Symbology = s, Data = info.ExampleData, X = 30, Y = 40, Height = 60 };
        // Some symbologies need option values that make the catalogue example valid.
        if (s == BarcodeSymbology.Interleaved2Of5) e.CheckDigit = false;
        return e;
    }

    /// <summary>
    /// An element that moves every option of the symbology away from its default, so a lost or mis-numbered ZPL parameter
    /// shows up as a difference after a round trip.
    /// </summary>
    public static BarcodeElement Variant(BarcodeSymbology s)
    {
        var e = Default(s);
        e.Height = 77;
        e.ModuleWidth = 3;
        e.WideBarRatio = 2.5;
        e.HumanReadable = false;
        e.HumanReadablePosition = HumanReadablePosition.Above;
        e.Rotation = FieldRotation.Rotated90;
        e.X = 55;
        e.Y = 66;
        switch (s)
        {
            case BarcodeSymbology.Code128: e.Code128Mode = Code128Mode.Automatic; break;
            case BarcodeSymbology.Code39: e.CheckDigit = true; break;
            case BarcodeSymbology.Code93: e.CheckDigit = true; break;
            case BarcodeSymbology.UpcA: e.CheckDigit = false; break;
            case BarcodeSymbology.Interleaved2Of5: e.CheckDigit = true; e.Data = "123456789"; break;
            case BarcodeSymbology.QrCode: e.QrModel = 1; e.ModuleSize = 7; e.QrErrorCorrection = QrErrorCorrection.High; break;
            case BarcodeSymbology.DataMatrix: e.ModuleSize = 6; e.Columns = 24; e.Rows = 24; break;
            case BarcodeSymbology.Pdf417: e.Pdf417SecurityLevel = 4; e.Columns = 5; e.Rows = 20; e.Pdf417Truncate = true; break;
            case BarcodeSymbology.Code11: e.Code11CheckDigitCount = 1; break;
            case BarcodeSymbology.Logmars: e.HumanReadable = true; break;
            case BarcodeSymbology.Codabar: e.CodabarStart = 'B'; e.CodabarStop = 'D'; break;
            case BarcodeSymbology.Msi: e.MsiCheckDigit = MsiCheckDigitMode.Mod11Mod10; e.MsiCheckDigitInText = true; break;
            case BarcodeSymbology.Plessey: e.CheckDigit = true; e.Data = "12AB"; break;
            case BarcodeSymbology.UpcE: e.CheckDigit = false; break;
            case BarcodeSymbology.UpcEanExtension: e.HumanReadable = false; e.HumanReadablePosition = HumanReadablePosition.Below; break;
            case BarcodeSymbology.Postnet: e.HumanReadable = true; break;
            case BarcodeSymbology.Planet: e.HumanReadable = true; break;
            case BarcodeSymbology.DataBar:
                e.DataBarType = DataBarType.Expanded; e.Data = "(01)90614141999999(10)ABC123"; e.ModuleSize = 3;
                e.DataBarSeparatorHeight = 2; e.DataBarSegmentsPerRow = 8; e.Rotation = FieldRotation.Inverted180;
                break;
            case BarcodeSymbology.Aztec:
                e.ModuleSize = 5; e.AztecEci = true; e.AztecErrorControl = 205; e.AztecMenuSymbol = true;
                e.AztecStructuredAppendCount = 3; e.AztecStructuredAppendId = "ABC";
                break;
            case BarcodeSymbology.MicroPdf417: e.MicroPdf417Mode = 5; e.Height = 12; break;
            case BarcodeSymbology.Codablock:
                e.CodablockMode = CodablockMode.E; e.CodablockSecurity = false; e.Columns = 10; e.Rows = 3; e.Height = 14;
                break;
            case BarcodeSymbology.MaxiCode:
                e.Rotation = null; e.MaxiCodeMode = 3; e.Data = "001840ABC123 extra"; e.MaxiCodeSymbolNumber = 2; e.MaxiCodeSymbolTotal = 4;
                break;
            case BarcodeSymbology.Tlc39:
                e.WideBarRatio = 2.4; e.Height = 55; e.Tlc39MicroModuleWidth = 3; e.Tlc39MicroRowHeight = 6;
                break;
            case BarcodeSymbology.Code49:
                e.Code49StartMode = '3'; e.HumanReadable = true; e.Data = "ABC123";
                break;
        }
        return e;
    }

    /// <summary>
    /// The properties of <see cref="BarcodeElement"/> that ZPL carries for this symbology: the options in the catalogue plus the
    /// properties every barcode has. Everything else is deliberately ignored by the generator and so by the comparison.
    /// </summary>
    public static HashSet<string> RelevantProperties(BarcodeSymbology s)
    {
        var set = new HashSet<string>(BarcodeCatalog.Get(s).Options.Select(o => o.ToString()))
        {
            nameof(BarcodeElement.Symbology), nameof(BarcodeElement.Data), nameof(BarcodeElement.Rotation), nameof(BarcodeElement.X),
            nameof(BarcodeElement.Y), nameof(BarcodeElement.OriginKind), nameof(BarcodeElement.ReverseField),
        };
        if (BarcodeCatalog.Get(s).AlwaysPrintsHumanReadable) set.Add(nameof(BarcodeElement.HumanReadable));
        return set;
    }

    /// <summary>Compares every relevant public property by reflection; the result is empty when the elements are equivalent.</summary>
    public static List<string> Diff(BarcodeElement a, BarcodeElement b, FieldRotation documentRotation = FieldRotation.Normal)
    {
        var diffs = new List<string>();
        if (a.Symbology != b.Symbology) { diffs.Add($"Symbology: {a.Symbology} vs {b.Symbology}"); return diffs; }
        foreach (var name in RelevantProperties(a.Symbology))
        {
            var prop = typeof(BarcodeElement).GetProperty(name) ?? throw new InvalidOperationException("No property " + name);
            object? x = prop.GetValue(a), y = prop.GetValue(b);
            if (name == nameof(BarcodeElement.Rotation)) { x = a.Rotation ?? documentRotation; y = b.Rotation ?? documentRotation; }
            if (name == nameof(BarcodeElement.Data))
            {
                x = BarcodeValidator.NormalizeForPrinter(a, (string)x!);
                y = BarcodeValidator.NormalizeForPrinter(b, (string)y!);
            }
            if (!Equals(x, y)) diffs.Add($"{name}: expected {x}, got {y}");
        }
        return diffs;
    }

    /// <summary>All public instance properties of <see cref="BarcodeElement"/> that describe the barcode itself.</summary>
    public static IEnumerable<PropertyInfo> OptionProperties() =>
        typeof(BarcodeElement).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.DeclaringType == typeof(BarcodeElement) && p.CanWrite);
}
