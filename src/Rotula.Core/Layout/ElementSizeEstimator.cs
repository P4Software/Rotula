using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Core.Layout;

/// <summary>
/// The size, in dots, that an element takes on the label before rotation. Relative positioning needs it to find a corner or centre of an
/// object, and the ZPL generator cannot draw anything, so this is the one place that answers "how big is it" from the model alone.
/// </summary>
/// <remarks>
/// <para>Boxes, lines, circles, ellipses, diagonals, symbols and images have an exact size in the model. Text in a Windows font is measured
/// exactly by the <see cref="ITextRasterizer"/> (the same layout the printed picture uses). Text in a printer font and barcodes are
/// <b>estimates</b>: the printer's letter widths are its own (so the width comes from <see cref="TextFitEstimator"/>, the estimate behind the
/// overflow warning), and a barcode's width is the module count of its symbology times the module width, with the human-readable band sized as
/// the preview draws it. An anchored object can therefore sit a few dots from where the estimate puts it on paper; the canvas and the ZPL use
/// the same estimate, so they agree with each other.</para>
/// </remarks>
public static class ElementSizeEstimator
{
    // What the preview draws for a raw ZPL field: the real content is unknown.
    private const int RawMarkerWidth = 100;
    private const int RawMarkerHeight = 40;

    /// <summary>Estimates the unrotated size of <paramref name="e"/> for one record.</summary>
    /// <param name="doc">The label (resolution, default font, variables).</param>
    /// <param name="e">The element.</param>
    /// <param name="data">The record's values, already resolved; null for the sample values.</param>
    /// <param name="rasterizer">Measures Windows fonts exactly; without one such a text is estimated like a printer font.</param>
    public static (int Width, int Height) Estimate(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data = null, ITextRasterizer? rasterizer = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case BoxElement b: return (b.WidthDots, b.HeightDots);
            case LineElement l: return l.Direction == LineDirection.Horizontal ? (l.LengthDots, l.ThicknessDots) : (l.ThicknessDots, l.LengthDots);
            case CircleElement c: return (c.DiameterDots, c.DiameterDots);
            case EllipseElement el: return (el.WidthDots, el.HeightDots);
            case DiagonalLineElement d: return (d.WidthDots, d.HeightDots);
            case SymbolElement s:
            {
                // ^GS with 0 takes the size of the default font; a width of 0 means "as high as it is tall".
                int h = s.HeightDots > 0 ? s.HeightDots : doc.DefaultFont?.Height ?? 30;
                return (s.WidthDots > 0 ? s.WidthDots : h, h);
            }
            case ImageElement img: return img.Source is null ? (Math.Max(1, img.WidthDots), Math.Max(1, img.HeightDots)) : ImageProcessor.ResolveSize(img);
            case RecallPlaceholderElement recall:
            {
                // A graphic the same file downloads has known dots; anything else is drawn at the placeholder size the designer shows.
                if (StoredGraphic.Find(doc.StoredGraphics, recall.Reference) is { } g && recall.Command.Equals("XG", StringComparison.OrdinalIgnoreCase))
                {
                    var (mx, my) = recall.GetMagnification();
                    return (g.WidthDots * mx, g.HeightDots * my);
                }
                return (recall.WidthDots, recall.HeightDots);
            }
            case RawZplElement: return (RawMarkerWidth, RawMarkerHeight);
            case TextElement t: return EstimateText(doc, t, data, rasterizer);
            case BarcodeElement bc: return EstimateBarcode(doc, bc, data);
            default: return (1, 1);
        }
    }

    // ---- text ----------------------------------------------------------------------------------

    private static (int Width, int Height) EstimateText(LabelDocument doc, TextElement t, IReadOnlyDictionary<string, string>? data, ITextRasterizer? rasterizer)
    {
        var scratch = new List<ValidationIssue>();
        string text = VariableResolver.Resolve(t.GetTemplate(), data, doc.Variables, t.Id, scratch);
        bool laidOut = t.Block is not null || t.TextBox is not null;
        // Outside a block the printer prints one line, so line breaks are spaces (the generator and the preview do the same).
        if (!laidOut) text = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        // The text fit decides the size or the line count this record gets; the same decision the ZPL carries.
        var fitted = TextFitResolver.Resolve(doc, t, text, rasterizer);

        if (t.Font is { IsWindows: true } windows && rasterizer is not null && windows.Validate() is null)
        {
            var font = windows;
            if (fitted?.PointSize is { } size) { font = windows.Clone(); font.PointSize = size; }
            if (text.Length == 0) return (1, Math.Max(1, (int)Math.Round(FontChoice.PointsToDots(font.PointSize, (int)doc.Dpi))));
            var request = new TextRasterRequest(text, font, (int)doc.Dpi, t.Block?.WidthDots ?? t.TextBox?.WidthDots ?? 0, t.TextBox?.HeightDots ?? 0,
                fitted?.MaxLines ?? t.Block?.MaxLines ?? 0, t.Block?.LineSpacing ?? 0, t.Block?.Alignment ?? TextAlignment.Left, t.Block?.HangingIndent ?? 0);
            var m = rasterizer.Measure(request);
            return (Math.Max(1, m.WidthDots), Math.Max(1, m.HeightDots));
        }

        // Printer font: nominal character sizes (see the class remarks). A Windows font without a rasterizer is treated the same way, at its
        // point size converted to dots, so the answer is at least the right order of magnitude.
        var def = doc.DefaultFont ?? new FontSpec('0', 30, 30);
        int height = fitted?.FontHeight ?? (t.Font is { IsWindows: true } w2 ? (int)Math.Round(FontChoice.PointsToDots(w2.PointSize, (int)doc.Dpi)) : t.FontHeight ?? def.Height);
        int? width = fitted?.FontWidth ?? t.FontWidth;
        double charWidth = TextFitEstimator.CharacterWidth(t, doc.DefaultFont, height, width);
        if (t.TextBox is { } box) return (box.WidthDots, box.HeightDots);
        if (t.Block is { } block)
        {
            int lines = Math.Clamp(TextFitEstimator.CountLines(t, text, doc.DefaultFont, height, width), 1, Math.Max(1, fitted?.MaxLines ?? block.MaxLines));
            return (block.WidthDots, Math.Max(1, lines * height + (lines - 1) * block.LineSpacing));
        }
        return (Math.Max(1, (int)Math.Round(text.Length * charWidth)), Math.Max(1, height));
    }

    // ---- barcodes ------------------------------------------------------------------------------

    private static (int Width, int Height) EstimateBarcode(LabelDocument doc, BarcodeElement b, IReadOnlyDictionary<string, string>? data)
    {
        var scratch = new List<ValidationIssue>();
        string payload = Barcodes.QrContent.QrContentBuilder.ResolveForElement(b, data, doc.Variables, scratch)
            ?? VariableResolver.Resolve(b.Data, data, doc.Variables, b.Id, scratch);
        var info = BarcodeCatalog.Get(b.Symbology);
        // Symbologies the preview cannot draw are shown as a captioned placeholder; its size is the one the designer works with.
        if (BarcodeCatalog.GetPreviewLimitation(b) is not null)
            return (Math.Clamp(80 + b.Data.Length * Math.Max(1, b.ModuleWidth) * 6, 200, 600), Math.Max(40, b.Height));

        int n = Math.Max(1, payload.Length);
        double r = Math.Clamp(b.WideBarRatio, 2.0, 3.0);
        int module = Math.Max(1, info.UsesModuleSize ? b.ModuleSize : b.ModuleWidth);
        switch (b.Symbology)
        {
            case BarcodeSymbology.QrCode:
            {
                int modules = QrSizing.Analyze(payload, b.QrErrorCorrection, b.QrModel).Modules;
                if (modules <= 0) modules = 21;
                return (modules * module, modules * module);
            }
            case BarcodeSymbology.DataMatrix:
            {
                var (cols, rows) = DataMatrixSize(n, b.Columns, b.Rows);
                return (cols * module, rows * module);
            }
            case BarcodeSymbology.Aztec:
            {
                // Full-range symbols: 15 modules plus 4 per layer; one layer per dozen characters is a fair middle ground.
                int layers = Math.Clamp((n + 11) / 12 + 1, 1, 32);
                int modules = 15 + 4 * layers;
                return (modules * module, modules * module);
            }
            case BarcodeSymbology.MaxiCode:
            {
                // Fixed-size symbol, about 28 x 27 mm; the preview draws modules 7 dots apart at 203 dpi.
                int side = (int)Math.Round(28.0 / 25.4 * (int)doc.Dpi);
                return (side, side);
            }
            case BarcodeSymbology.Pdf417:
            {
                int cols = b.Columns > 0 ? b.Columns : Math.Clamp((int)Math.Ceiling(Math.Sqrt(n / 2.0)), 1, 30);
                int codewords = (int)Math.Ceiling(n / 1.8) + 1 + (1 << (Math.Clamp(b.Pdf417SecurityLevel, 0, 8) + 1));
                int rows = b.Rows > 0 ? b.Rows : Math.Clamp((int)Math.Ceiling(codewords / (double)cols), 3, 90);
                // Start, left row indicator, data columns, right row indicator and stop (the truncated form drops the right side).
                int modules = b.Pdf417Truncate ? 17 * (cols + 2) + 1 : 17 * (cols + 4) + 1;
                return (modules * module, rows * Math.Max(1, b.Height));
            }
            case BarcodeSymbology.DataBar:
            {
                int modules = b.DataBarType switch
                {
                    DataBarType.Stacked or DataBarType.StackedOmnidirectional => 50,
                    DataBarType.Limited => 79,
                    DataBarType.Expanded => Math.Clamp(4 + 21 * Math.Min(Math.Max(2, b.DataBarSegmentsPerRow), Math.Max(2, (n + 1) / 2)), 50, 500),
                    _ => 96,
                };
                return (modules * module, LinearHeight(b, info, module));
            }
        }

        // Where the symbology's own rules fix the width (Code 128 with its code sets, Code 39, Code 93, Interleaved 2 of 5, EAN/UPC), use the
        // width the size readout measures: the per-character formulas below assume the widest encoding, and Code 128 packs two digits into one
        // character, so a tracking number came out about a third too wide and a right-anchored one landed that far too left.
        if (BarcodeSizing.Analyze(b, payload, (int)doc.Dpi)?.SymbolWidthDots is { } measured and > 0)
            return (measured, LinearHeight(b, info, module));

        // Linear codes: modules per character of the symbology, plus start/stop. The ratio r is the wide-to-narrow bar ratio (^BY).
        double modules1D = b.Symbology switch
        {
            BarcodeSymbology.Code128 => 11 * (n + 3) + 2,
            BarcodeSymbology.Code39 or BarcodeSymbology.Logmars => (3 * r + 7) * (n + 2 + (b.CheckDigit ? 1 : 0)),
            BarcodeSymbology.Code93 => 9 * (n + 4) + 1,
            BarcodeSymbology.Ean13 or BarcodeSymbology.UpcA => 95,
            BarcodeSymbology.Ean8 => 67,
            BarcodeSymbology.UpcE => 51,
            BarcodeSymbology.UpcEanExtension => n <= 2 ? 20 : 47,
            BarcodeSymbology.Interleaved2Of5 => ((n + (b.CheckDigit ? 1 : 0) + 1) / 2) * (4 * r + 6) + 6 + r,
            BarcodeSymbology.Industrial2Of5 or BarcodeSymbology.Standard2Of5 => (2 * r + 8) * n + 4 * r + 12,
            BarcodeSymbology.Code11 => (2 * r + 4) * (n + 2 + b.Code11CheckDigitCount),
            BarcodeSymbology.Codabar => (3 * r + 5) * (n + 2),
            BarcodeSymbology.Msi or BarcodeSymbology.Plessey => 4 * (1 + r) * (n + 2) + 2 * (1 + r),
            // Postal codes print at a fixed pitch of about 22.5 bars per inch, 5 bars per digit plus the frame bars.
            BarcodeSymbology.Postnet or BarcodeSymbology.Planet => (5 * n + 2) * (int)doc.Dpi / 22.5 / module,
            _ => 11 * (n + 3) + 2,
        };
        return (Math.Max(1, (int)Math.Round(modules1D * module)), LinearHeight(b, info, module));
    }

    /// <summary>Bars plus the human-readable band, sized as the preview draws it (text at 8 modules, between 14 and 36 dots, plus a 4-dot gap).</summary>
    private static int LinearHeight(BarcodeElement b, BarcodeSymbologyInfo info, int module)
    {
        bool text = info.SupportsHumanReadable && (info.AlwaysPrintsHumanReadable || b.HumanReadable);
        int band = text ? (int)Math.Ceiling(Math.Clamp(module * 8.0, 14, 36)) + 4 : 0;
        return Math.Max(1, b.Height) + band;
    }

    // ECC 200 symbol sizes (square) with their text capacity: the smallest that holds the data is the estimate.
    private static readonly (int Side, int Capacity)[] DataMatrixSquares =
    {
        (10, 3), (12, 6), (14, 10), (16, 16), (18, 25), (20, 31), (22, 43), (24, 52), (26, 64), (32, 91), (36, 127), (40, 169), (44, 214), (48, 259),
        (52, 304), (64, 418), (72, 550), (80, 682), (88, 862), (96, 1024), (104, 1222), (120, 1573), (132, 1954), (144, 2218),
    };

    private static (int Columns, int Rows) DataMatrixSize(int length, int columns, int rows)
    {
        if (columns > 0 && rows > 0) return (columns, rows);
        foreach (var (side, capacity) in DataMatrixSquares)
            if (capacity >= length) return (columns > 0 ? columns : side, rows > 0 ? rows : side);
        return (144, 144);
    }
}
