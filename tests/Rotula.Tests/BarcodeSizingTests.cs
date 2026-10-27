using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>The barcode sizing helper: X-dimension, bar widths, symbol width, quiet zones and the printer-reality / fit warnings.</summary>
public class BarcodeSizingTests
{
    private static BarcodeElement Bar(BarcodeSymbology s, string data, int module = 2, int x = 100) =>
        new() { Symbology = s, Data = data, ModuleWidth = module, ModuleSize = module, X = x, Y = 20 };

    private static LabelDocument Doc(PrinterDpi dpi, int widthDots = 800, params LabelElement[] elements)
    {
        var d = new LabelDocument { Width = Measure.Dots(widthDots), Height = Measure.Dots(400), Dpi = dpi };
        d.Elements.AddRange(elements);
        return d;
    }

    [Theory]
    [InlineData(203, 2, 9.852, 0.250)]
    [InlineData(300, 2, 6.667, 0.169)]
    [InlineData(600, 3, 5.0, 0.127)]
    public void XDimension_IsDerivedFromDotsAndDpi(int dpi, int module, double mils, double mm)
    {
        var report = BarcodeSizing.Analyze(Bar(BarcodeSymbology.Ean13, "590123412345", module), "590123412345", dpi)!;
        Assert.Equal(mils, report.XDimensionMils, 3);
        Assert.Equal(mm, report.XDimensionMm, 3);
        Assert.Equal(module, report.NarrowBarDots);
    }

    [Theory]
    // Symbol widths follow from each symbology's rules: Code 128 "ABC" = start + 3 + check + stop = 11x5 + 13 modules.
    [InlineData(BarcodeSymbology.Code128, "ABC", 68 * 2)]
    [InlineData(BarcodeSymbology.Ean13, "590123412345", 95 * 2)]
    [InlineData(BarcodeSymbology.UpcA, "01234567890", 95 * 2)]
    [InlineData(BarcodeSymbology.Ean8, "1234567", 67 * 2)]
    // Code 39 "ABC": 5 characters of 6 narrow + 3 wide (6 dots) and 4 gaps; wide = 3 x 2.
    [InlineData(BarcodeSymbology.Code39, "ABC", 5 * (12 + 18) + 4 * 2)]
    // Interleaved 2 of 5 "1234": start 4n, two pairs of 6n + 4w, stop w + 2n.
    [InlineData(BarcodeSymbology.Interleaved2Of5, "1234", 8 + 2 * (12 + 24) + (6 + 4))]
    [InlineData(BarcodeSymbology.Code93, "ABC", 7 * 9 * 2 + 2)]
    // QR "HELLO" at level Q is version 1: 21 modules of 2 dots.
    [InlineData(BarcodeSymbology.QrCode, "HELLO", 21 * 2)]
    public void SymbolWidth_IsMeasuredWhereTheRulesFixIt(BarcodeSymbology s, string data, int expectedDots)
    {
        var report = BarcodeSizing.Analyze(Bar(s, data), data, 203)!;
        Assert.Equal(expectedDots, report.SymbolWidthDots);
        Assert.Equal(expectedDots * 25.4 / 203, report.SymbolWidthMm!.Value, 6);
    }

    [Fact]
    public void SymbolWidth_IsNotClaimed_WhenItCannotBeKnown_AndAnEncoderCountIsUsedInstead()
    {
        var dm = Bar(BarcodeSymbology.DataMatrix, "HELLO", 3);
        Assert.Null(BarcodeSizing.Analyze(dm, "HELLO", 300)!.SymbolWidthDots);
        Assert.Equal(14 * 3, BarcodeSizing.Analyze(dm, "HELLO", 300, encodedModules: 14)!.SymbolWidthDots);
        // MaxiCode has a fixed size and no module setting at all.
        Assert.Null(BarcodeSizing.Analyze(Bar(BarcodeSymbology.MaxiCode, "x"), "x", 203));
    }

    [Fact]
    public void WideBar_FollowsTheRatio_OnlyForSymbologiesWithWideBars()
    {
        var c39 = Bar(BarcodeSymbology.Code39, "A", 3);
        c39.WideBarRatio = 2.5;
        var r = BarcodeSizing.Analyze(c39, "A", 203)!;
        Assert.Equal(3, r.NarrowBarDots);
        Assert.Equal(8, r.WideBarDots);   // 3 x 2.5 = 7.5, rounded to whole dots
        Assert.Null(BarcodeSizing.Analyze(Bar(BarcodeSymbology.Code128, "A"), "A", 203)!.WideBarDots);
    }

    [Theory]
    [InlineData(BarcodeSymbology.Code128, 10, 10)]
    [InlineData(BarcodeSymbology.Code39, 10, 10)]
    [InlineData(BarcodeSymbology.Ean13, 11, 7)]
    [InlineData(BarcodeSymbology.UpcA, 9, 9)]
    [InlineData(BarcodeSymbology.Ean8, 7, 7)]
    [InlineData(BarcodeSymbology.QrCode, 4, 4)]
    [InlineData(BarcodeSymbology.DataMatrix, 1, 1)]
    [InlineData(BarcodeSymbology.Pdf417, 2, 2)]
    [InlineData(BarcodeSymbology.Postnet, 0, 0)]       // family default: postal
    [InlineData(BarcodeSymbology.Code49, 2, 2)]        // family default: stacked
    public void QuietZone_UsesTheStandardValue(BarcodeSymbology s, int left, int right)
    {
        var q = BarcodeSizing.QuietZoneOf(s);
        Assert.Equal((left, right), (q.LeftModules, q.RightModules));
        Assert.False(string.IsNullOrWhiteSpace(q.Source));
    }

    [Fact]
    public void QuietZone_IsScaledByTheModuleInDotsAndMm()
    {
        var r = BarcodeSizing.Analyze(Bar(BarcodeSymbology.Ean13, "590123412345", 3), "590123412345", 300)!;
        Assert.Equal((33, 21), (r.QuietLeftDots, r.QuietRightDots));
        Assert.Equal(33 * 25.4 / 300, r.QuietLeftMm, 6);
        Assert.Equal(95 * 3 + 33 + 21, r.TotalWidthDots);
    }

    [Theory]
    [InlineData(FieldRotation.Normal, 22, 0, 14, 0)]
    [InlineData(FieldRotation.Inverted180, 14, 0, 22, 0)]
    [InlineData(FieldRotation.Rotated90, 0, 22, 0, 14)]
    [InlineData(FieldRotation.Rotated270, 0, 14, 0, 22)]
    public void QuietZoneBox_FollowsTheReadingDirection(FieldRotation rotation, int l, int t, int r, int b)
    {
        var box = BarcodeSizing.QuietZoneBox(Bar(BarcodeSymbology.Ean13, "590123412345"), rotation);
        Assert.Equal(new QuietZoneDots(l, t, r, b), box);
    }

    [Fact]
    public void QuietZoneBox_OfA2DCode_IsOnAllSides()
    {
        var box = BarcodeSizing.QuietZoneBox(Bar(BarcodeSymbology.QrCode, "HELLO", 3), FieldRotation.Rotated90);
        Assert.Equal(new QuietZoneDots(12, 12, 12, 12), box);
    }

    // ---- printer reality check ------------------------------------------------------------

    [Theory]
    [InlineData(PrinterDpi.Dpi300, 1, BarcodeSymbology.Code128, true)]
    [InlineData(PrinterDpi.Dpi600, 1, BarcodeSymbology.QrCode, true)]
    [InlineData(PrinterDpi.Dpi300, 2, BarcodeSymbology.Code128, false)]
    [InlineData(PrinterDpi.Dpi203, 2, BarcodeSymbology.Code128, false)]
    public void OneDotModule_WarnsAtHighResolution(PrinterDpi dpi, int module, BarcodeSymbology s, bool warns)
    {
        var b = Bar(s, s == BarcodeSymbology.QrCode ? "HELLO" : "ABC", module);
        var issues = BarcodeSizing.Check(b, b.Data, Doc(dpi, 1200, b));
        Assert.Equal(warns, issues.Any(i => i.MessageKey == BarcodeSizing.OneDotKey));
        Assert.DoesNotContain(issues, i => i.MessageKey == BarcodeSizing.ThinBarKey);
        Assert.All(issues, i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
    }

    [Fact]
    public void ThinBar_WarnsOnlyFor1DCodesAt203Dpi()
    {
        var linear = Bar(BarcodeSymbology.Code128, "ABC", 1);
        var issue = Assert.Single(BarcodeSizing.Check(linear, "ABC", Doc(PrinterDpi.Dpi203, 800, linear)), i => i.MessageKey == BarcodeSizing.ThinBarKey);
        Assert.Equal(new[] { "1", "4.9", "0.125", "203" }, issue.Args);

        // A 2D module of 1 dot at 203 dpi is not a "narrow bar" and is not flagged by this rule.
        var qr = Bar(BarcodeSymbology.QrCode, "HELLO", 1);
        Assert.DoesNotContain(BarcodeSizing.Check(qr, "HELLO", Doc(PrinterDpi.Dpi203, 800, qr)), i => i.MessageKey == BarcodeSizing.ThinBarKey);
    }

    // ---- quiet zone fit ---------------------------------------------------------------------

    [Fact]
    public void SymbolPlusQuietZones_MustFitTheLabelWidth()
    {
        // Code 128 "ABC" is 136 dots; quiet zones 20 + 20 dots at module 2.
        var fits = Bar(BarcodeSymbology.Code128, "ABC", 2, x: 20);
        Assert.Empty(BarcodeSizing.Check(fits, "ABC", Doc(PrinterDpi.Dpi203, 176, fits)));

        var left = Bar(BarcodeSymbology.Code128, "ABC", 2, x: 19);   // quiet zone would start left of the label edge
        var l = Assert.Single(BarcodeSizing.Check(left, "ABC", Doc(PrinterDpi.Dpi203, 400, left)));
        Assert.Equal(BarcodeSizing.QuietZoneKey, l.MessageKey);

        var right = Bar(BarcodeSymbology.Code128, "ABC", 2, x: 20);
        var r = Assert.Single(BarcodeSizing.Check(right, "ABC", Doc(PrinterDpi.Dpi203, 175, right)));
        Assert.Equal(BarcodeSizing.QuietZoneKey, r.MessageKey);
        // From 0 mm to 22 mm needed (176 dots), on a label that is 21.9 mm (175 dots) wide.
        Assert.Equal(new[] { "0", "22", "21.9" }, r.Args);
    }

    [Fact]
    public void QuietZoneFit_IsSkippedForRotatedFields_AndForSymbolsWhoseWidthIsUnknown()
    {
        var rotated = Bar(BarcodeSymbology.Code128, "ABC", 2, x: 700);
        rotated.Rotation = FieldRotation.Rotated90;
        Assert.Empty(BarcodeSizing.Check(rotated, "ABC", Doc(PrinterDpi.Dpi203, 800, rotated)));

        var dm = Bar(BarcodeSymbology.DataMatrix, "HELLO", 4, x: 790);
        Assert.Empty(BarcodeSizing.Check(dm, "HELLO", Doc(PrinterDpi.Dpi203, 800, dm)));
    }

    [Fact]
    public void TheDocumentDefaultRotation_DecidesWhenTheFieldHasNone()
    {
        var b = Bar(BarcodeSymbology.Code128, "ABC", 2, x: 5);
        var doc = Doc(PrinterDpi.Dpi203, 800, b);
        doc.DefaultRotation = FieldRotation.Rotated90;
        Assert.Empty(BarcodeSizing.Check(b, "ABC", doc));
    }

    [Fact]
    public void AnOutOfRangeModule_IsLeftToTheValidator()
    {
        var b = Bar(BarcodeSymbology.Code128, "ABC", 0);
        Assert.Empty(BarcodeSizing.Check(b, "ABC", Doc(PrinterDpi.Dpi203, 800, b)));
    }

    [Fact]
    public void TheZplGenerator_ReportsTheSizeWarnings_AndStillPrints()
    {
        var b = Bar(BarcodeSymbology.Code128, "ABC", 1, x: 0);
        var result = ZplGenerator.Generate(Doc(PrinterDpi.Dpi203, 800, b));
        Assert.False(result.HasErrors);
        Assert.Contains(result.Issues, i => i.MessageKey == BarcodeSizing.ThinBarKey);
        Assert.Contains(result.Issues, i => i.MessageKey == BarcodeSizing.QuietZoneKey);   // x = 0 leaves no room for the 10X margin
        Assert.Contains("^BC", result.Zpl);
    }

    [Fact]
    public void ADefaultBarcodeOnADefaultLabel_HasNoSizeWarning()
    {
        var b = Bar(BarcodeSymbology.Code128, "ABC123", 2, x: 50);
        var result = ZplGenerator.Generate(Doc(PrinterDpi.Dpi203, 800, b));
        Assert.DoesNotContain(result.Issues, i => i.MessageKey.StartsWith("Barcode.Size.", StringComparison.Ordinal));
    }
}
