using System.Globalization;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.Tests;

/// <summary>
/// The driver pre-flight: what is checked before a label is printed as a picture through a Windows driver, which findings block the job,
/// which ones only need the person's acknowledgement, and the fingerprint that ties an acknowledgement to one set of findings.
/// </summary>
public class DriverPreflightTests
{
    private static LabelDocument Doc(double widthMm, double heightMm, PrinterDpi dpi, params LabelElement[] elements)
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(widthMm), Height = Measure.Millimeters(heightMm), Dpi = dpi };
        doc.Elements.AddRange(elements);
        return doc;
    }

    /// <summary>Media whose page and printable area are exactly the given size in millimetres at the given resolution, with no unprintable offset.</summary>
    private static DriverMedia MediaFor(double widthMm, double heightMm, int dpiX, int dpiY, bool customSizeAccepted = true)
    {
        int width = (int)Math.Round(widthMm * dpiX / 25.4, MidpointRounding.AwayFromZero);
        int height = (int)Math.Round(heightMm * dpiY / 25.4, MidpointRounding.AwayFromZero);
        return new DriverMedia(dpiX, dpiY, width, height, width, height, 0, 0, customSizeAccepted);
    }

    /// <summary>
    /// A die-cut 4 x 6 inch label printer at 300 dpi as DYMO and Brother drivers report it: the page is exactly 101.6 x 152.4 mm (1200 x 1800
    /// dots), but the driver keeps an 18-dot (about 1.5 mm) strip on every edge it cannot print, so the printable area is 1164 x 1764 dots
    /// (98.6 x 149.4 mm).
    /// </summary>
    private static DriverMedia DieCut300(bool customSizeAccepted = true) =>
        new(300, 300, PhysicalWidthDots: 1200, PhysicalHeightDots: 1800, PrintableWidthDots: 1164, PrintableHeightDots: 1764,
            OffsetXDots: 18, OffsetYDots: 18, customSizeAccepted);

    private static TextElement Text(string text = "Hello") => new() { Text = text, X = 20, Y = 20 };

    private static BarcodeElement Code128(int moduleWidth, string name = "SKU") =>
        new() { Name = name, Symbology = BarcodeSymbology.Code128, Data = "ROTULA-12345", ModuleWidth = moduleWidth, Height = 80, X = 20, Y = 20 };

    private static RawZplElement Raw(bool visual) => new() { Zpl = visual ? "^FO10,10^GB50,50,2^FS" : "^PW800", IsVisual = visual };

    [Fact]
    public void NoMediaIsAnError()
    {
        var result = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Text()), null);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.DriverUnavailable", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.True(result.HasErrors);
        Assert.Null(result.Media);
    }

    [Fact]
    public void PlainLabelOnMatchingMediaHasNoFindings()
    {
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text());
        var media = new DriverMedia(300, 300, 1181, 591, 1181, 591, 0, 0, true);

        var first = DriverPreflight.Analyze(doc, media);
        var second = DriverPreflight.Analyze(doc, media);

        Assert.Empty(first.Findings);
        Assert.False(first.HasErrors);
        Assert.False(first.NeedsAcknowledgement);
        Assert.Same(media, first.Media);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void RawBlocksAreCountedAndWarned()
    {
        // A configuration block (not visual) is lost in picture mode just like a drawing one, so both count.
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text(), Raw(visual: true), Raw(visual: false));

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.RawBlocks", finding.MessageKey);
        Assert.Equal(new[] { "2" }, finding.Args);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.True(result.NeedsAcknowledgement);
        Assert.False(result.HasErrors);
    }

    /// <summary>A symbology the renderer has no encoder for: the preview shows a "printer draws it" stand-in instead of the symbol.</summary>
    private static BarcodeSymbology Undrawable => BarcodeCatalog.All.First(i => !i.HasPreviewEncoder).Symbology;

    [Fact]
    public void BarcodeTheRendererCannotDrawCountsWithTheRawBlocks()
    {
        // A picture cannot contain what only the printer can draw: a raw block and an undrawable barcode are two parts that will not print.
        var barcode = new BarcodeElement { Symbology = Undrawable, Data = "12345", ModuleWidth = 3, ModuleSize = 6, X = 20, Y = 150 };
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text(), Raw(visual: true), barcode);

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        // Single: the undrawable barcode is not judged for its module size either, since it is not printed at all.
        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.RawBlocks", finding.MessageKey);
        Assert.Equal(new[] { "2" }, finding.Args);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void EveryPlaceholderKindIsCountedAndNothingElse()
    {
        // Counted: a recall of a graphic that is not in the file, an image too large to draw (10000 x 10000 dots is above the image cap),
        // a stored-format recall (^IL and ^XF are never drawn, even when the file downloads a graphic) and a configuration raw block.
        var missingRecall = new RecallPlaceholderElement { Command = "XG", Reference = "R:MISSING.GRF", Zpl = "^XGR:MISSING.GRF,1,1", X = 300, Y = 40 };
        var hugeImage = new ImageElement { Source = new GrayscaleBitmap(2, 2, new byte[4]), WidthDots = 10000, HeightDots = 10000 };
        var imageLoad = new RecallPlaceholderElement { Command = "IL", Reference = "R:LOGO.GRF", Zpl = "^ILR:LOGO.GRF" };
        var formRecall = new RecallPlaceholderElement { Command = "XF", Reference = "R:FORM.ZPL", Zpl = "^XFR:FORM.ZPL" };
        var configuration = Raw(visual: false);
        // Not counted: a recall whose graphic the file downloads (~DG) is drawn exactly, and an ordinary image is drawn.
        var drawnRecall = new RecallPlaceholderElement { Command = "XG", Reference = "R:LOGO.GRF", Zpl = "^XGR:LOGO.GRF,1,1", X = 100, Y = 40 };
        var smallImage = new ImageElement { Source = new GrayscaleBitmap(2, 2, new byte[4]), WidthDots = 20, HeightDots = 20, X = 200, Y = 40 };
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text(), missingRecall, hugeImage, imageLoad, formRecall, configuration, drawnRecall, smallImage);
        doc.StoredGraphics.Add(new StoredGraphic("R:LOGO.GRF", 1, new byte[] { 0xFF, 0xFF }));

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.RawBlocks", finding.MessageKey);
        Assert.Equal(new[] { "5" }, finding.Args);
    }

    [Fact]
    public void CertificationMarksCountWithTheRawBlocks()
    {
        // The printer prints real UL and CSA marks; a picture would only have the letters, so they are parts the driver cannot print.
        // Registered, copyright and trademark are ordinary glyphs and print fine.
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text(),
            new SymbolElement { Symbol = GraphicSymbol.UnderwritersLaboratories, X = 300, Y = 40 },
            new SymbolElement { Symbol = GraphicSymbol.CanadianStandards, X = 400, Y = 40 },
            new SymbolElement { Symbol = GraphicSymbol.Registered, X = 500, Y = 40 },
            new SymbolElement { Symbol = GraphicSymbol.Copyright, X = 600, Y = 40 },
            new SymbolElement { Symbol = GraphicSymbol.Trademark, X = 700, Y = 40 });

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.RawBlocks", finding.MessageKey);
        Assert.Equal(new[] { "2" }, finding.Args);
    }

    [Theory]
    [InlineData(0, 591)]
    [InlineData(1181, 0)]
    [InlineData(-5, 591)]
    public void ADriverWithoutAPrintableAreaIsUnavailable(int printableWidth, int printableHeight)
    {
        // A driver that reports no printable area has reported nothing usable; "larger than 0 x 0 mm paper" would only confuse.
        var result = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Text()),
            new DriverMedia(300, 300, 1181, 591, printableWidth, printableHeight, 0, 0, true));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.DriverUnavailable", finding.MessageKey);
        Assert.Null(result.Media);
    }

    [Theory]
    // No physical page at all, and a physical page smaller than the area printed on it: neither is a page a label can be judged against.
    [InlineData(0, 591)]
    [InlineData(1181, 0)]
    [InlineData(1100, 591)]
    [InlineData(1181, 500)]
    public void AnAcceptedPageThatCannotHoldItsPrintableAreaIsUnavailable(int physicalWidth, int physicalHeight)
    {
        // When the label size was accepted, the fit is judged against the physical page, so a driver that reports none has reported nothing usable.
        var accepted = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Text()),
            new DriverMedia(300, 300, physicalWidth, physicalHeight, 1181, 591, 0, 0, true));
        Assert.Equal("Driver.Preflight.DriverUnavailable", Assert.Single(accepted.Findings).MessageKey);

        // A refused size is centred in the printable area and never uses the physical page, so the same answer is judged as usual.
        var refused = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Text()),
            new DriverMedia(300, 300, physicalWidth, physicalHeight, 1181, 591, 0, 0, false));
        Assert.Equal("Driver.Preflight.PaperMismatch", Assert.Single(refused.Findings).MessageKey);
    }

    [Theory]
    // Printable area 1200 x 600 dots at 300 dpi = 101.6 x 50.8 mm; up to 1 mm more still fits.
    [InlineData(102.5, 50.8, false)]
    [InlineData(102.7, 50.8, true)]
    [InlineData(101.6, 51.7, false)]
    [InlineData(101.6, 51.9, true)]
    public void TheOneMillimetreToleranceHoldsOnBothAxes(double labelWidthMm, double labelHeightMm, bool tooLarge)
    {
        var result = DriverPreflight.Analyze(Doc(labelWidthMm, labelHeightMm, PrinterDpi.Dpi300, Text()), new DriverMedia(300, 300, 1200, 600, 1200, 600, 0, 0, true));

        Assert.Equal(tooLarge, result.Findings.Any(f => f.MessageKey == "Driver.Preflight.LabelLargerThanPaper"));
        Assert.Equal(tooLarge, result.HasErrors);
    }

    [Fact]
    public void NotPrintedObjectsAreNotChecked()
    {
        // An object marked "do not print" is never part of the printed label, so it cannot be lost or scan badly.
        var hiddenRaw = Raw(visual: true);
        hiddenRaw.NotPrintable = true;
        var hiddenBarcode = Code128(moduleWidth: 2);
        hiddenBarcode.NotPrintable = true;
        var doc = Doc(50, 25, PrinterDpi.Dpi203, hiddenRaw, hiddenBarcode);

        var result = DriverPreflight.Analyze(doc, MediaFor(50, 25, 100, 100));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void OneDeviceDotModuleIsAnError()
    {
        // 2 dots at 203 dpi drawn at 100 dpi: 2 * 100 / 203 = 0.99, which rounds to a single device dot.
        var doc = Doc(50, 25, PrinterDpi.Dpi203, Code128(moduleWidth: 2));

        var result = DriverPreflight.Analyze(doc, MediaFor(50, 25, 100, 100));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.ModuleOneDot", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(new[] { "SKU" }, finding.Args);
        Assert.Equal(doc.Elements[0].Id, finding.ElementId);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ModuleUnderTheMinimumIsAWarningWithMils()
    {
        // A 1-dot module designed at 300 dpi becomes 4 dots at 1200 dpi: 3.3 mils, under the Code 128 minimum of 9.8 mils.
        // (A 3-dot module, 2.5 mils, cannot come out of a 203, 300 or 600 dpi design at 1200 dpi: the ratios are 5.9, 4 and 2.)
        var doc = Doc(50, 25, PrinterDpi.Dpi300, Code128(moduleWidth: 1));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // The arguments must not follow the machine's culture: a decimal comma would break the message and the fingerprint.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var result = DriverPreflight.Analyze(doc, MediaFor(50, 25, 1200, 1200));

            var finding = Assert.Single(result.Findings);
            Assert.Equal("Driver.Preflight.ModuleBelowMinimum", finding.MessageKey);
            Assert.Equal(ValidationSeverity.Warning, finding.Severity);
            string actual = BarcodeSizing.MilsOf(4, 1200).ToString("0.0", CultureInfo.InvariantCulture);
            string minimum = BarcodeSizing.MinimumModuleMils(BarcodeSymbology.Code128).ToString("0.0", CultureInfo.InvariantCulture);
            Assert.Equal(new[] { "SKU", actual, minimum }, finding.Args);
            Assert.Equal("3.3", actual);
            Assert.Equal("9.8", minimum);
            Assert.True(result.NeedsAcknowledgement);
            Assert.False(result.HasErrors);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>A QR Code with 4-dot modules designed at 300 dpi: 13.3 mils on a 300 dpi driver.</summary>
    private static BarcodeElement FourDotQr(string data = "https://example.com/menu") =>
        new() { Name = "Menu", Symbology = BarcodeSymbology.QrCode, Data = data, ModuleSize = 4, X = 20, Y = 20 };

    [Fact]
    public void AFourDotGenericQrAt300DpiIsNotWarned()
    {
        // 13.3 mils is above the matrix fallback of 10.0 mils a generic QR gets (ISO/IEC 18004 states no minimum).
        var result = DriverPreflight.Analyze(Doc(50, 50, PrinterDpi.Dpi300, FourDotQr()), MediaFor(50, 50, 300, 300));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void AFourDotGs1DigitalLinkQrAt300DpiIsWarnedWithTheGs1Minimum()
    {
        // The same QR carrying a GS1 Digital Link is judged against the GS1 QR figure (0.375 mm, 14.8 mils), by the option or by the content
        // builder's kind, and the warning shows that minimum (it is part of the fingerprint).
        var byOption = FourDotQr("https://id.gs1.org/01/09506000134352");
        byOption.DigitalLink = true;
        var byBuilder = FourDotQr("https://id.gs1.org/01/09506000134352");
        byBuilder.QrContentSpec = Rotula.Core.Barcodes.QrContent.QrContentSpec.Create(
            Rotula.Core.Barcodes.QrContent.QrContentKind.GS1DigitalLink, ("gtin", "09506000134352")).ToJson();

        foreach (var qr in new[] { byOption, byBuilder })
        {
            var result = DriverPreflight.Analyze(Doc(50, 50, PrinterDpi.Dpi300, qr), MediaFor(50, 50, 300, 300));

            var finding = Assert.Single(result.Findings);
            Assert.Equal("Driver.Preflight.ModuleBelowMinimum", finding.MessageKey);
            Assert.Equal(ValidationSeverity.Warning, finding.Severity);
            Assert.Equal(new[] { "Menu", "13.3", "14.8" }, finding.Args);
        }
    }

    [Fact]
    public void UnnamedBarcodeIsNamedBySymbology()
    {
        // Without a name the symbology names the barcode, never the internal id (it changes on every ZPL import and would break the fingerprint).
        var doc = Doc(50, 25, PrinterDpi.Dpi203, Code128(moduleWidth: 2, name: "  "));

        var result = DriverPreflight.Analyze(doc, MediaFor(50, 25, 100, 100));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(new[] { BarcodeCatalog.Get(BarcodeSymbology.Code128).DisplayName }, finding.Args);
    }

    /// <summary>A text the printer numbers itself (^SN with a start value, or ^SF with a mask over the end of the data).</summary>
    private static TextElement PrinterNumberedText(PrinterSerialKind kind, string? name = null) => new()
    {
        Name = name,
        Text = kind == PrinterSerialKind.Number ? "0001" : "LOT-0001",
        X = 20,
        Y = 20,
        Serial = new PrinterSerialOptions { Kind = kind, Mask = kind == PrinterSerialKind.Mask ? "DDDD" : string.Empty },
    };

    [Theory]
    [InlineData(PrinterSerialKind.Number)]
    [InlineData(PrinterSerialKind.Mask)]
    public void TextNumberedByThePrinterIsAnError(PrinterSerialKind kind)
    {
        // The printer changes the number on every label; a picture is drawn once for all copies, so every copy would carry the same number.
        var doc = Doc(100, 50, PrinterDpi.Dpi300, PrinterNumberedText(kind, name: "Lot"));

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.PrinterCounter", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(new[] { "Lot" }, finding.Args);
        Assert.Equal(doc.Elements[0].Id, finding.ElementId);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void TextDatedByThePrinterClockIsAnError()
    {
        // ^FC: the printer replaces the clock codes with its own date; a picture would print "%Y-%m-%d" as it is. Unnamed, so the first line
        // of its text names it (never the internal id).
        var clock = new TextElement { Text = "EXP %Y-%m-%d\nsecond line", X = 20, Y = 20, Clock = new FieldClockOptions() };
        var doc = Doc(100, 50, PrinterDpi.Dpi300, clock);

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.PrinterCounter", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(new[] { "EXP %Y-%m-%d" }, finding.Args);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void BarcodeNumberedByThePrinterIsAnError()
    {
        // 4-dot modules at 300 dpi are 13.3 mils, above the Code 128 minimum, so the printer-side number is the only finding.
        var named = Code128(moduleWidth: 4, name: "Carton");
        named.Data = "000123";
        named.Serial = new PrinterSerialOptions();
        var unnamed = Code128(moduleWidth: 4, name: "");
        unnamed.Data = "000456";
        unnamed.Y = 200;
        unnamed.Clock = new FieldClockOptions();
        var doc = Doc(100, 50, PrinterDpi.Dpi300, named, unnamed);

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        Assert.All(result.Findings, f => Assert.Equal("Driver.Preflight.PrinterCounter", f.MessageKey));
        Assert.All(result.Findings, f => Assert.Equal(ValidationSeverity.Error, f.Severity));
        // The same naming rule as the barcode findings: its own name, or else its symbology.
        Assert.Equal(new[] { "Carton", BarcodeCatalog.Get(BarcodeSymbology.Code128).DisplayName }, result.Findings.Select(f => f.Args.Single()));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void PrinterSideFieldsAreNotRawBlocksAndOrdinaryFieldsAreFine()
    {
        var media = MediaFor(100, 50, 300, 300);
        // A field number (^FN) only stores the data on the printer; the data still prints as written, so it is not a printer-side value.
        // An object that is not printed cannot print a wrong number.
        var numbered = new TextElement { Text = "Ana", X = 20, Y = 20, FieldNumber = 3 };
        var hidden = PrinterNumberedText(PrinterSerialKind.Number);
        hidden.NotPrintable = true;
        Assert.Empty(DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Text(), numbered, hidden), media).Findings);

        // Next to a raw block, the raw-block count stays 1: the printer-side field is reported as its own error, never counted with the parts left out.
        var mixed = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Raw(visual: true), PrinterNumberedText(PrinterSerialKind.Number, "Lot")), media);
        Assert.Equal(new[] { "1" }, Assert.Single(mixed.Findings, f => f.MessageKey == "Driver.Preflight.RawBlocks").Args);
        Assert.Single(mixed.Findings, f => f.MessageKey == "Driver.Preflight.PrinterCounter");
    }

    [Theory]
    // ^FE: the printer replaces "#2#" with the data of field 2; a picture would print "#2#" as it is.
    [InlineData("Lot #2#")]
    // The setting alone counts, even with no reference in the data: the rule stays simple, and the data can change per record.
    [InlineData("Lot 2")]
    public void TextWithEmbeddedFieldReferencesIsAnError(string text)
    {
        var embedded = new TextElement { Name = "Lot", Text = text, X = 20, Y = 20, EmbedCharacter = '#' };
        var doc = Doc(100, 50, PrinterDpi.Dpi300, embedded);

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.PrinterCounter", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(new[] { "Lot" }, finding.Args);
        Assert.Equal(embedded.Id, finding.ElementId);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void EmbeddedReferencesCountForBarcodesAndOnlyWhenSet()
    {
        var media = MediaFor(100, 50, 300, 300);
        // The same text without the setting prints "#2#" as plain characters on every printer: nothing to report.
        var plain = new TextElement { Text = "Lot #2#", X = 20, Y = 20 };
        Assert.Empty(DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, plain), media).Findings);

        // A barcode whose data the printer completes from another field is just as wrong as a picture.
        var barcode = Code128(moduleWidth: 4, name: "Carton");
        barcode.Data = "CT#1#";
        barcode.EmbedCharacter = '#';
        var finding = Assert.Single(DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, barcode), media).Findings);
        Assert.Equal("Driver.Preflight.PrinterCounter", finding.MessageKey);
        Assert.Equal(new[] { "Carton" }, finding.Args);
    }

    [Fact]
    public void MarginBandKeyIsPublic()
    {
        // The app translates the finding by its key, and the four-language test finds the keys through the public constants.
        Assert.Equal("Driver.Preflight.MarginBand", DriverPreflight.MarginBand);
    }

    [Fact]
    public void LabelLargerThanPaperIsAnError()
    {
        var doc = Doc(100, 150, PrinterDpi.Dpi300, Text());

        var result = DriverPreflight.Analyze(doc, MediaFor(100, 50, 300, 300));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.LabelLargerThanPaper", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Equal(new[] { "100x150", "100x50" }, finding.Args);
        Assert.True(result.HasErrors);

        // Up to 1 mm larger is tolerated: that is the rounding of a driver's paper sizes, not a clipped label.
        var almost = DriverPreflight.Analyze(Doc(100.8, 50.9, PrinterDpi.Dpi300, Text()), MediaFor(100, 50, 300, 300));
        Assert.Empty(almost.Findings);
    }

    [Fact]
    public void CustomSizeRefusedIsAPaperMismatchWarning()
    {
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text());

        var result = DriverPreflight.Analyze(doc, MediaFor(210, 297, 300, 300, customSizeAccepted: false));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.PaperMismatch", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Equal(new[] { "100x50", "210x297" }, finding.Args);
        Assert.True(result.NeedsAcknowledgement);
        Assert.False(result.HasErrors);
    }

    // ---- The physical page and the unprintable margin band

    [Fact]
    public void ALabelTheSizeOfAnAcceptedPageOnlyWarnsAboutTheMarginBand()
    {
        // The driver took the 4 x 6 inch size and prints from the paper's corner, so the label fits its paper. Only the strip the printer cannot
        // reach along the edges is worth a warning: whatever is designed there may be cut off.
        var result = DriverPreflight.Analyze(Doc(101.6, 152.4, PrinterDpi.Dpi300, Text()), DieCut300());

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.MarginBand", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        // Label size, then the printable area, written as the paper findings write them, then the widest unprintable edge in millimetres
        // with one decimal (18 dots at 300 dpi on every side: 1.5 mm).
        Assert.Equal(new[] { "101.6x152.4", "98.6x149.4", "1.5" }, finding.Args);
        Assert.Null(finding.ElementId);
        Assert.False(result.HasErrors);
        Assert.True(result.NeedsAcknowledgement);
    }

    [Theory]
    // Uneven margins: nothing on the left, 35 dots (3.0 mm) on the right. The widest edge is 3.0 mm, not the average 1.5 mm.
    [InlineData(0, 0, 1165, 1800, "98.6x152.4", "3.0")]
    // The widest edge at the top (35 dots from the offset), none at the bottom.
    [InlineData(0, 35, 1200, 1765, "101.6x149.4", "3.0")]
    // Four different edges: left 12 (1.0 mm), top 18 (1.5 mm), right 24 (2.0 mm), bottom 18 (1.5 mm).
    [InlineData(12, 18, 1164, 1764, "98.6x149.4", "2.0")]
    public void TheMarginBandNamesTheWidestUnprintableEdge(int offsetX, int offsetY, int printableWidth, int printableHeight, string printable, string widest)
    {
        // Every edge is worked out from the page: left and top are the offsets, right and bottom are what the page has beyond the printable area.
        var media = new DriverMedia(300, 300, 1200, 1800, printableWidth, printableHeight, offsetX, offsetY, true);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Invariant whatever the machine's language: the argument is part of the fingerprint.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var result = DriverPreflight.Analyze(Doc(101.6, 152.4, PrinterDpi.Dpi300, Text()), media);

            var finding = Assert.Single(result.Findings);
            Assert.Equal("Driver.Preflight.MarginBand", finding.MessageKey);
            Assert.Equal(new[] { "101.6x152.4", printable, widest }, finding.Args);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TheWidestEdgeIsPartOfTheFingerprint()
    {
        // The same label and printable area with a wider edge is another finding, so an acknowledgement does not carry over to it.
        var even = DriverPreflight.Analyze(Doc(101.6, 152.4, PrinterDpi.Dpi300, Text()), new DriverMedia(300, 300, 1200, 1800, 1164, 1764, 18, 18, true));
        var uneven = DriverPreflight.Analyze(Doc(101.6, 152.4, PrinterDpi.Dpi300, Text()), new DriverMedia(300, 300, 1200, 1800, 1164, 1764, 0, 18, true));

        Assert.Equal(even.Findings[0].Args[1], uneven.Findings[0].Args[1]);
        Assert.NotEqual(even.Findings[0].Args[2], uneven.Findings[0].Args[2]);
        Assert.NotEqual(even.Fingerprint, uneven.Fingerprint);
    }

    [Fact]
    public void ALabelLargerThanTheAcceptedPageIsStillAnError()
    {
        // 120 mm does not fit a 101.6 mm page however it is placed: that is the paper error, and the margin band adds nothing to it.
        var result = DriverPreflight.Analyze(Doc(120, 152.4, PrinterDpi.Dpi300, Text()), DieCut300());

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.LabelLargerThanPaper", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        // The second size is the area the printer can print on, as the sentence says ("larger than the area this printer can print on").
        Assert.Equal(new[] { "120x152.4", "98.6x149.4" }, finding.Args);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void ARefusedSizeOnPaperSmallerThanTheLabelKeepsTheError()
    {
        // Custom size refused: the label is centred in the printable area of the driver's own paper, which is smaller than the label. That is
        // the paper error plus the mismatch warning, never a margin band (there is no page corner the label is drawn from).
        var result = DriverPreflight.Analyze(Doc(101.6, 152.4, PrinterDpi.Dpi300, Text()), DieCut300(customSizeAccepted: false));

        Assert.Equal(new[] { "Driver.Preflight.LabelLargerThanPaper", "Driver.Preflight.PaperMismatch" }, result.Findings.Select(f => f.MessageKey));
        Assert.All(result.Findings, f => Assert.Equal(new[] { "101.6x152.4", "98.6x149.4" }, f.Args));
        Assert.True(result.HasErrors);
    }

    [Theory]
    // Inside the printable area: nothing to say.
    [InlineData(95, 145)]
    // Up to 1 mm into the band is the same rounding allowance the paper check has (98.6 + 0.9, 149.4 + 0.9).
    [InlineData(99.5, 150.3)]
    public void ALabelThatStaysOutOfTheMarginBandHasNoPaperFinding(double labelWidthMm, double labelHeightMm)
    {
        var result = DriverPreflight.Analyze(Doc(labelWidthMm, labelHeightMm, PrinterDpi.Dpi300, Text()), DieCut300());

        Assert.Empty(result.Findings);
    }

    [Theory]
    // More than 1 mm into the band on one axis is enough.
    [InlineData(99.8, 140)]
    [InlineData(90, 150.6)]
    public void MoreThanOneMillimetreIntoTheBandOnEitherAxisIsWarned(double labelWidthMm, double labelHeightMm)
    {
        var result = DriverPreflight.Analyze(Doc(labelWidthMm, labelHeightMm, PrinterDpi.Dpi300, Text()), DieCut300());

        Assert.Equal("Driver.Preflight.MarginBand", Assert.Single(result.Findings).MessageKey);
    }

    [Fact]
    public void NonWholeAxisRatioIsAWarning()
    {
        var doc = Doc(100, 50, PrinterDpi.Dpi300, Text());

        var uneven = DriverPreflight.Analyze(doc, MediaFor(100, 50, 600, 400));
        var finding = Assert.Single(uneven.Findings);
        Assert.Equal("Driver.Preflight.AxisRatio", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Equal(new[] { "600", "400" }, finding.Args);

        // 600 x 300 is a whole ratio: every module still lands on whole dots after the device stretches the picture.
        Assert.Empty(DriverPreflight.Analyze(doc, MediaFor(100, 50, 600, 300)).Findings);
    }

    [Fact]
    public void HugePictureIsAnErrorNotACrash()
    {
        var doc = Doc(200, 300, PrinterDpi.Dpi203, Text());
        var media = MediaFor(200, 300, 1200, 1200);
        // Warm up first, so the measurement below does not count one-time static initialisation.
        DriverPreflight.Analyze(Doc(10, 10, PrinterDpi.Dpi203), MediaFor(10, 10, 300, 300));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = DriverPreflight.Analyze(doc, media);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var finding = Assert.Single(result.Findings);
        Assert.Equal("Driver.Preflight.TooLarge", finding.MessageKey);
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        var (w, h) = DevicePictureRenderer.PictureSize(doc, 1200);
        Assert.Equal(new[] { ((long)w * h).ToString(CultureInfo.InvariantCulture), "40000000" }, finding.Args);
        Assert.True((long)w * h > DevicePictureRenderer.MaxPixels);
        // The picture would be more than 130 million bytes; the check itself must stay tiny.
        Assert.True(allocated < 1_000_000, $"Analyze allocated {allocated} bytes");

        // The renderer refuses the same job on its own, as a second guard.
        Assert.Throws<InvalidOperationException>(() => DevicePictureRenderer.Render(doc, null, 1200));
    }

    [Fact]
    public void FingerprintChangesWhenFindingsChange()
    {
        var media = MediaFor(100, 50, 300, 300);
        var oneBlock = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Raw(true)), media);
        var twoBlocks = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300, Raw(true), Raw(true)), media);
        var none = DriverPreflight.Analyze(Doc(100, 50, PrinterDpi.Dpi300), media);

        Assert.NotEqual(oneBlock.Fingerprint, twoBlocks.Fingerprint);
        Assert.NotEqual(oneBlock.Fingerprint, none.Fingerprint);
        Assert.Equal(none.Fingerprint, DriverPreflightResult.None.Fingerprint);
        // SHA-256 as lower-case hexadecimal.
        Assert.Matches("^[0-9a-f]{64}$", oneBlock.Fingerprint);
    }

    [Fact]
    public void FingerprintIgnoresFindingOrder()
    {
        var raw = new ValidationIssue(ValidationSeverity.Warning, "Driver.Preflight.RawBlocks", null, "2");
        var axis = new ValidationIssue(ValidationSeverity.Warning, "Driver.Preflight.AxisRatio", null, "600", "400");

        var ab = new DriverPreflightResult(new[] { raw, axis }, null);
        var ba = new DriverPreflightResult(new[] { axis, raw }, null);

        Assert.Equal(ab.Fingerprint, ba.Fingerprint);
    }

    [Fact]
    public void FingerprintCannotBeFooledByASeparatorInAnArgument()
    {
        // "a|b" as one argument must not look like the two arguments "a" and "b".
        var one = new DriverPreflightResult(new[] { new ValidationIssue(ValidationSeverity.Error, "Driver.Preflight.ModuleOneDot", null, "a|b") }, null);
        var two = new DriverPreflightResult(new[] { new ValidationIssue(ValidationSeverity.Error, "Driver.Preflight.ModuleOneDot", null, "a", "b") }, null);

        Assert.NotEqual(one.Fingerprint, two.Fingerprint);
    }

    [Fact]
    public void NoneHasNothingToReport()
    {
        Assert.Empty(DriverPreflightResult.None.Findings);
        Assert.Null(DriverPreflightResult.None.Media);
        Assert.False(DriverPreflightResult.None.HasErrors);
        Assert.False(DriverPreflightResult.None.NeedsAcknowledgement);
    }
}
