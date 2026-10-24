using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;
using SkiaSharp;

namespace Rotula.Rendering.Input;

/// <summary>
/// Maps the Rotula.Core document model onto the renderer's own <see cref="RenderLabel"/> input:
/// variables are substituted, defaults (^CF, ^FW) are applied and margins (^LH) are added to every position,
/// so the preview lays elements out exactly where <see cref="ZplGenerator"/> tells the printer to put them.
/// <para>
/// Whole-label inversion (^PO) and mirroring (^PM) are left out of <see cref="ToRenderLabel"/> so the canvas and the exports show the
/// label as designed; <see cref="AsPrinted"/> adds them for the "as printed" preview.
/// Not previewed: text direction (^FP) is ignored, and bitmap
/// font ids other than '0' are all drawn with the monospaced stand-in. Label shift (^LS) and label top (^LT) are applied. Text preview is approximate in general (see <see cref="LabelRenderer"/>).
/// </para>
/// </summary>
public static class LabelDocumentAdapter
{
    /// <summary>Converts a label document into render input.</summary>
    /// <param name="doc">The design.</param>
    /// <param name="data">Variable values; missing names fall back to the declared sample values.</param>
    /// <param name="includeNonPrinting">
    /// False (the default, used by every print and export) leaves out objects marked "do not print" and objects whose condition is false for
    /// <paramref name="data"/>. True keeps them, flagged <see cref="RenderElement.Dimmed"/>, for the designer's canvas.
    /// </param>
    public static RenderLabel ToRenderLabel(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null, bool includeNonPrinting = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return Build(doc, ResolvePreviewData(doc, data), includeNonPrinting);
    }

    /// <summary>
    /// Converts a label document into render input with values that were already resolved for one label of a run (a print run's or a merge batch's
    /// values, see <c>LabelPrintRun.LabelValues</c> and <c>MergedLabel.Values</c>). Unlike <see cref="ToRenderLabel"/> the variables are not
    /// resolved again, so every label keeps its own serial number instead of the preview's "next number". Objects that are not printed are left out.
    /// </summary>
    /// <param name="doc">The design.</param>
    /// <param name="values">The resolved values of this label, or null for the sample values of a label without definitions.</param>
    public static RenderLabel ToRenderLabelResolved(LabelDocument doc, IReadOnlyDictionary<string, string>? values)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return Build(doc, values, includeNonPrinting: false);
    }

    private static RenderLabel Build(LabelDocument doc, IReadOnlyDictionary<string, string>? data, bool includeNonPrinting)
    {
        int dpi = (int)doc.Dpi;
        int homeX = doc.Margins.Left.ToDots(doc.Dpi);
        int homeY = doc.Margins.Top.ToDots(doc.Dpi);

        // Resolver warnings (unknown variables) are reported by the ZPL validation, not by the preview.
        var ignored = new List<ValidationIssue>();
        var elements = new List<RenderElement>(doc.Elements.Count);
        foreach (var e in doc.Elements)
        {
            // Decided on the data that fills this record's placeholders, exactly as the ZPL generator does, so the picture and the print agree.
            bool printed = ElementPrinting.IsPrinted(e, data, doc.Variables);
            if (!printed && !includeNonPrinting) continue;
            var mapped = Map(doc, e, data, ignored);
            if (mapped is null) continue;
            // ^LS moves everything to the left, ^LT moves the whole format down (or up when negative).
            elements.Add(mapped with { X = e.X + homeX - doc.LabelShift, Y = e.Y + homeY + doc.LabelTop, Dimmed = !printed });
        }

        return new RenderLabel(doc.WidthDots, doc.HeightDots, dpi, elements);
    }

    /// <summary>True when the printer turns (<c>^POI</c>) or mirrors (<c>^PMY</c>) the whole label, so "as printed" differs from "as designed".</summary>
    public static bool ChangesWhenPrinted(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.Orientation == LabelOrientation.Inverted || doc.MirrorImage;
    }

    /// <summary>
    /// Returns <paramref name="label"/> set to draw the way the printer puts it on the media: turned by 180 degrees for <c>^POI</c> and
    /// mirrored for <c>^PMY</c>. The same instance comes back when the document does neither.
    /// </summary>
    /// <param name="label">Render input made from <paramref name="doc"/> by <see cref="ToRenderLabel"/>.</param>
    /// <param name="doc">The design whose print orientation and mirror settings apply.</param>
    public static RenderLabel AsPrinted(RenderLabel label, LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!ChangesWhenPrinted(doc)) return label;
        return label with { Inverted = doc.Orientation == LabelOrientation.Inverted, Mirrored = doc.MirrorImage };
    }

    // Stateless and thread safe; shared so the preview does not build one per element.
    private static readonly SkiaTextRasterizer FitRasterizer = new();

    /// <summary>
    /// Dates, counters, prompts and computed values only exist once resolved; without this the preview would show the bare sample
    /// text of a variable that has a definition. Nothing is reserved or remembered: a counter shows the number the next label gets.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ResolvePreviewData(LabelDocument doc, IReadOnlyDictionary<string, string>? data) =>
        Rotula.Core.Variables.VariableEngine.HasDefinitions(doc)
            ? Rotula.Core.Variables.VariableEngine.BuildPreviewData(doc, new Rotula.Core.Variables.VariableContext { Record = data })
            : data;

    /// <summary>
    /// The ids of the elements that are not part of the printed label for <paramref name="data"/> (marked "do not print", or their condition is
    /// false). Uses the same value resolution as <see cref="ToRenderLabel"/>, so the designer's badges agree with the picture and the ZPL.
    /// </summary>
    /// <param name="doc">The design.</param>
    /// <param name="data">The shown record, or null for the sample values.</param>
    public static HashSet<string> FindNotPrinted(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var resolved = ResolvePreviewData(doc, data);
        return doc.Elements.Where(e => !ElementPrinting.IsPrinted(e, resolved, doc.Variables)).Select(e => e.Id).ToHashSet();
    }

    private static RenderElement? Map(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> issues)
    {
        var rotation = ToRotation(e.Rotation ?? doc.DefaultRotation);
        bool typeset = e.OriginKind == FieldOriginKind.Typeset;

        switch (e)
        {
            case TextElement t:
            {
                var def = doc.DefaultFont ?? new FontSpec('0', 30, 30);
                var text = VariableResolver.Resolve(t.GetTemplate(), data, doc.Variables, t.Id, issues);
                // Outside a ^FB block the printer prints one line, so line breaks are treated as spaces.
                if (t.Block is null)
                    text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                // Text fit: the same decision the ZPL generator makes for this text, so the canvas shows the size that prints. The rasterizer is the
                // one the preview itself draws with (Windows fonts are measured exactly; printer fonts use the estimate).
                var fitted = Core.Text.TextFitResolver.Resolve(doc, t, text, FitRasterizer);
                return new RenderText
                {
                    Text = text,
                    // A ^TB box wraps at its width and shows only the lines that fit its height.
                    HeightDots = t.TextBox?.HeightDots ?? 0,
                    // A Windows font is drawn as the printed picture is: its own family at its point size, converted at the printer's resolution.
                    FontHeightDots = t.Font is { IsWindows: true } wf ? Core.Text.FontChoice.PointsToDots(fitted?.PointSize ?? wf.PointSize, (int)doc.Dpi) : fitted?.FontHeight ?? t.FontHeight ?? def.Height,
                    FamilyName = t.Font is { IsWindows: true } wf2 ? wf2.FamilyName : null,
                    Bold = t.Font is { IsWindows: true, Bold: true },
                    Italic = t.Font is { IsWindows: true, Italic: true },
                    Underline = t.Font is { IsWindows: true, Underline: true },
                    HangingIndentDots = t.Block?.HangingIndent ?? 0,
                    Font = (t.FontId ?? def.FontId) == '0' ? RenderFontFamily.Sans : RenderFontFamily.Mono,
                    WidthDots = t.Block?.WidthDots ?? t.TextBox?.WidthDots ?? 0,
                    MaxLines = fitted?.MaxLines ?? t.Block?.MaxLines ?? 0,
                    LineSpacingDots = t.Block?.LineSpacing ?? 0,
                    Alignment = t.Block?.Alignment switch
                    {
                        TextAlignment.Center => RenderTextAlignment.Center,
                        TextAlignment.Right => RenderTextAlignment.Right,
                        TextAlignment.Justified => RenderTextAlignment.Justified,
                        _ => RenderTextAlignment.Left,
                    },
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = t.ReverseField,
                };
            }

            case BarcodeElement b:
            {
                var info = BarcodeCatalog.Get(b.Symbology);
                var data1 = Core.Barcodes.QrContent.QrContentBuilder.ResolveForElement(b, data, doc.Variables, issues)
                    ?? VariableResolver.Resolve(b.Data, data, doc.Variables, b.Id, issues);
                // Symbologies (or options) without an exact preview are drawn as a labelled placeholder: the printer still
                // prints the real symbol from the generated ZPL, so the designer must not pretend to show it.
                if (BarcodeCatalog.GetPreviewLimitation(b) is not null)
                {
                    return new RenderPlaceholder
                    {
                        WidthDots = PlaceholderWidth(b),
                        HeightDots = Math.Max(40, b.Height),
                        Caption = info.DisplayName + " - printer draws it",
                        Typeset = typeset,
                    };
                }
                return new RenderBarcode
                {
                    Symbology = b.Symbology,
                    Data = data1,
                    // ZPL: ^BY sets the module for linear codes and PDF417; ^BQ, ^BX, ^BO and ^BR carry their own module size.
                    // MaxiCode has a fixed size: its modules are about 0.88 mm apart, 7 dots at 203 dpi.
                    ModuleDots = b.Symbology == BarcodeSymbology.MaxiCode ? MaxiCodeModuleDots : info.UsesModuleSize ? b.ModuleSize : b.ModuleWidth,
                    HeightDots = b.Height,
                    ShowText = info.SupportsHumanReadable && (info.AlwaysPrintsHumanReadable || b.HumanReadable),
                    TextAbove = b.HumanReadablePosition == HumanReadablePosition.Above,
                    CheckDigit = b.CheckDigit,
                    QrErrorCorrection = b.QrErrorCorrection,
                    Pdf417SecurityLevel = b.Pdf417SecurityLevel,
                    Pdf417Truncate = b.Pdf417Truncate,
                    Code11CheckDigits = b.Code11CheckDigitCount,
                    MsiCheckDigit = b.MsiCheckDigit,
                    MsiCheckDigitInText = b.MsiCheckDigitInText,
                    CodabarStart = b.CodabarStart,
                    CodabarStop = b.CodabarStop,
                    DataBarType = b.DataBarType,
                    DataBarSegmentsPerRow = b.DataBarSegmentsPerRow,
                    DataBarSeparatorHeight = b.DataBarSeparatorHeight,
                    AztecErrorControl = b.AztecErrorControl,
                    MaxiCodeMode = b.MaxiCodeMode,
                    MaxiCodeSymbolNumber = b.MaxiCodeSymbolNumber,
                    MaxiCodeSymbolTotal = b.MaxiCodeSymbolTotal,
                    Gs1 = BarcodeValidator.IsGs128Data(b, data1) || BarcodeValidator.IsGs1DataMatrix(b),
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = b.ReverseField,
                };
            }

            case BoxElement box:
            {
                int min = Math.Min(box.WidthDots, box.HeightDots);
                return new RenderBox
                {
                    WidthDots = box.WidthDots,
                    HeightDots = box.HeightDots,
                    ThicknessDots = box.ThicknessDots,
                    // ZPL rounding degree 0-8 maps to a corner radius of up to half the shorter side.
                    CornerRadiusDots = box.CornerRounding * min / 16.0,
                    White = box.Color == GraphicColor.White,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = box.ReverseField,
                };
            }

            case LineElement line:
            {
                // ZPL draws a line as a box whose short side equals the thickness, so a solid box is exact.
                bool horizontal = line.Direction == LineDirection.Horizontal;
                int w = horizontal ? line.LengthDots : line.ThicknessDots;
                int h = horizontal ? line.ThicknessDots : line.LengthDots;
                return new RenderBox
                {
                    WidthDots = w,
                    HeightDots = h,
                    ThicknessDots = Math.Max(w, h),
                    White = line.Color == GraphicColor.White,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = line.ReverseField,
                };
            }

            case CircleElement c:
                return new RenderCircle
                {
                    DiameterDots = c.DiameterDots,
                    ThicknessDots = c.ThicknessDots,
                    White = c.Color == GraphicColor.White,
                    // The image/circle origin is the bounding box corner; rotation of a circle changes nothing visible.
                    Typeset = typeset,
                    Reverse = c.ReverseField,
                };

            case EllipseElement el:
                return new RenderEllipse
                {
                    WidthDots = el.WidthDots,
                    HeightDots = el.HeightDots,
                    ThicknessDots = el.ThicknessDots,
                    White = el.Color == GraphicColor.White,
                    Typeset = typeset,
                    Reverse = el.ReverseField,
                };

            case DiagonalLineElement dl:
                return new RenderDiagonal
                {
                    WidthDots = dl.WidthDots,
                    HeightDots = dl.HeightDots,
                    ThicknessDots = dl.ThicknessDots,
                    LeansRight = dl.Lean == DiagonalLean.Right,
                    White = dl.Color == GraphicColor.White,
                    Typeset = typeset,
                    Reverse = dl.ReverseField,
                };

            case SymbolElement sy:
            {
                // The printer draws these from its own symbol font; the preview uses the nearest glyphs of the stand-in typeface.
                string glyph = sy.Symbol switch
                {
                    GraphicSymbol.Registered => "\u00AE",
                    GraphicSymbol.Copyright => "\u00A9",
                    GraphicSymbol.Trademark => "\u2122",
                    GraphicSymbol.UnderwritersLaboratories => "UL",
                    _ => "CSA",
                };
                return new RenderText
                {
                    Text = glyph,
                    FontHeightDots = Math.Max(1, sy.HeightDots),
                    Font = RenderFontFamily.Sans,
                    Rotation = rotation,
                    Typeset = typeset,
                    Reverse = sy.ReverseField,
                };
            }

            case ImageElement img:
            {
                if (img.Source is null) return null;
                // Use the exact dots the printer will receive, so dithering is previewed faithfully.
                var mono = ImageProcessor.ToMonochrome(img);
                return new RenderImage
                {
                    EncodedBytes = EncodeMonochrome(mono),
                    WidthDots = mono.Width,
                    HeightDots = mono.Height,
                    // ^GF ignores field rotation (it is applied to the dots already), so none is passed on.
                    Typeset = typeset,
                    Reverse = img.ReverseField,
                };
            }

            case RecallPlaceholderElement recall when TryMapStoredGraphic(doc, recall, typeset) is { } stored:
                return stored;

            case RecallPlaceholderElement recall:
                // The stored object is unknown, so the designer's own size is used. The caption says what is recalled.
                return new RenderPlaceholder
                {
                    WidthDots = Math.Max(1, recall.WidthDots),
                    HeightDots = Math.Max(1, recall.HeightDots),
                    Caption = string.IsNullOrWhiteSpace(recall.Reference)
                        ? "Stored on printer (^" + recall.Command + ")"
                        : "Stored on printer: " + recall.Reference,
                    Typeset = typeset,
                };

            // Only raw blocks that draw something get a marker; configuration commands have no place on the label.
            // The real size of the content is unknown, so a small fixed marker is drawn at the field position.
            case RawZplElement { IsVisual: true }:
                return new RenderPlaceholder
                {
                    WidthDots = RawMarkerWidthDots,
                    HeightDots = RawMarkerHeightDots,
                    Caption = "Raw ZPL",
                    Typeset = typeset,
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Draws a recalled graphic (<c>^XG</c>, <c>^IM</c>) when the same file downloads it with <c>~DG</c>: the dots are known,
    /// so the preview is exact. The magnification is applied by repeating dots, as the printer does.
    /// </summary>
    private static RenderElement? TryMapStoredGraphic(LabelDocument doc, RecallPlaceholderElement recall, bool typeset)
    {
        if (!recall.Command.Equals("XG", StringComparison.OrdinalIgnoreCase) && !recall.Command.Equals("IM", StringComparison.OrdinalIgnoreCase)) return null;
        var graphic = StoredGraphic.Find(doc.StoredGraphics, recall.Reference);
        if (graphic is null) return null;

        var (mx, my) = recall.GetMagnification();
        long w = (long)graphic.WidthDots * mx, h = (long)graphic.HeightDots * my;
        // A huge magnified bitmap is not worth building; the renderer scales the original instead.
        if (w * h > 16_000_000) (mx, my) = (1, 1);
        int bytesPerRow = graphic.BytesPerRow, rows = graphic.HeightDots;
        MonochromeBitmap mono;
        if (mx == 1 && my == 1)
        {
            mono = new MonochromeBitmap(graphic.WidthDots, rows, graphic.Data);
        }
        else
        {
            int outW = graphic.WidthDots * mx, outH = rows * my;
            var scaled = new MonochromeBitmap(outW, outH, new byte[((outW + 7) / 8) * outH]);
            int outBytes = scaled.BytesPerRow;
            for (int y = 0; y < outH; y++)
                for (int x = 0; x < outW; x++)
                {
                    int sx = x / mx, sy = y / my;
                    if ((graphic.Data[sy * bytesPerRow + (sx >> 3)] & (0x80 >> (sx & 7))) != 0)
                        scaled.PackedRows[y * outBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            mono = scaled;
        }
        return new RenderImage
        {
            EncodedBytes = EncodeMonochrome(mono),
            WidthDots = mono.Width,
            HeightDots = mono.Height,
            Typeset = typeset,
            Reverse = recall.ReverseField,
        };
    }

    /// <summary>
    /// The real size of a symbol without a preview is unknown, so the stand-in grows a little with the amount of data
    /// (and with the module width) to keep the label layout believable.
    /// </summary>
    private static int PlaceholderWidth(BarcodeElement b) => Math.Clamp(80 + b.Data.Length * Math.Max(1, b.ModuleWidth) * 6, 200, 600);

    private const int MaxiCodeModuleDots = 7;

    private const int RawMarkerWidthDots = 100;
    private const int RawMarkerHeightDots = 40;

    private static RenderRotation ToRotation(FieldRotation r) => r switch
    {
        FieldRotation.Rotated90 => RenderRotation.Rotate90,
        FieldRotation.Inverted180 => RenderRotation.Rotate180,
        FieldRotation.Rotated270 => RenderRotation.Rotate270,
        _ => RenderRotation.None,
    };

    /// <summary>Encodes packed 1-bit dots (1 = black) as a grey PNG.</summary>
    private static byte[] EncodeMonochrome(MonochromeBitmap mono)
    {
        using var bmp = new SKBitmap(new SKImageInfo(mono.Width, mono.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = new byte[mono.Width * mono.Height];
        for (int y = 0; y < mono.Height; y++)
            for (int x = 0; x < mono.Width; x++)
                pixels[y * mono.Width + x] = mono.IsBlack(x, y) ? (byte)0 : (byte)255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
