using System.Globalization;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Validation;

namespace Rotula.Core.Model;

/// <summary>Result of <see cref="LabelDpiConverter.Convert"/>: the converted copy plus the warnings it raised.</summary>
/// <param name="Document">A new document at the target resolution; the source document is untouched.</param>
/// <param name="Issues">Structured warnings (message keys with arguments, never UI text).</param>
public sealed record DpiConversionResult(LabelDocument Document, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Re-targets a label designed for one printhead resolution to another so the printed label keeps the same
/// physical size and layout. Every dot-based quantity is multiplied by <c>target/source</c> (round half away
/// from zero); lengths stored in mm or inches are already resolution independent and stay as they are.
/// <para>
/// Issue keys: <c>Dpi.Barcode.ModuleRounded</c> (args: element name, old, new module size in dots),
/// <c>Dpi.Value.Clamped</c> (args: element name, property, ideal, applied) and <c>Dpi.Raw.NotScaled</c>
/// (args: element name; the element's coordinates stay in the original dots) and <c>Dpi.Graphic.NotScaled</c>
/// (args: stored graphic path; the bitmap keeps its pixels, so it prints at a different physical size).
/// </para>
/// </summary>
public static class LabelDpiConverter
{
    /// <summary>Relative module-width change above which <c>Dpi.Barcode.ModuleRounded</c> is reported.</summary>
    private const double ModuleToleranceRatio = 0.15;

    /// <summary>True when <paramref name="document"/> was designed for a different resolution than <paramref name="targetDpi"/>.</summary>
    public static bool NeedsConversion(LabelDocument document, PrinterDpi targetDpi)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Dpi != targetDpi;
    }

    /// <summary>
    /// Returns a deep copy of <paramref name="source"/> converted to <paramref name="targetDpi"/>; the input is
    /// never mutated. When the resolution already matches the result is an equal deep copy with no issues.
    /// </summary>
    public static DpiConversionResult Convert(LabelDocument source, PrinterDpi targetDpi)
    {
        ArgumentNullException.ThrowIfNull(source);
        var issues = new List<ValidationIssue>();
        bool same = source.Dpi == targetDpi;
        // One ratio for everything, so a 203 -> 300 -> 203 trip stays within one dot per quantity.
        var c = new Ctx(same ? 1.0 : (double)(int)targetDpi / (int)source.Dpi, same, issues);

        var doc = new LabelDocument
        {
            // mm / inch measures keep their value (same physical size); only dot measures are scaled.
            Width = ScaleMeasure(source.Width, c),
            Height = ScaleMeasure(source.Height, c),
            Dpi = targetDpi,
            Orientation = source.Orientation,
            Margins = new LabelMargins
            {
                Left = ScaleMeasure(source.Margins.Left, c),
                Top = ScaleMeasure(source.Margins.Top, c),
                Right = ScaleMeasure(source.Margins.Right, c),
                Bottom = ScaleMeasure(source.Margins.Bottom, c),
            },
            MediaType = source.MediaType,
            MediaTracking = source.MediaTracking,
            DefaultRotation = source.DefaultRotation,
            DefaultFont = source.DefaultFont is { } f
                ? new FontSpec(f.FontId, f.Height == 0 ? 0 : c.Dots(f.Height, 1), f.Width == 0 ? 0 : c.Dots(f.Width, 1))
                : null,
            Encoding = source.Encoding,
            StockId = source.StockId,
            Id = source.Id,
            // Dot offsets scale with the resolution; the other printer settings are resolution independent.
            LabelShift = c.Dots(source.LabelShift, int.MinValue),
            LabelTop = c.Dots(source.LabelTop, int.MinValue),
            PrintMode = source.PrintMode,
            PrePeel = source.PrePeel,
            MirrorImage = source.MirrorImage,
            PrintRate = source.PrintRate is { } pr ? new PrintRateOptions { Speed = pr.Speed, SlewSpeed = pr.SlewSpeed, BackfeedSpeed = pr.BackfeedSpeed } : null,
            MediaDarkness = source.MediaDarkness,
            Print = new PrintOptions
            {
                Quantity = source.Print.Quantity,
                PauseEvery = source.Print.PauseEvery,
                Replicates = source.Print.Replicates,
                OverridePause = source.Print.OverridePause,
            },
        };
        foreach (var a in source.FontAliases) doc.FontAliases.Add(new FontAlias(a.Letter, a.Path));
        // Stored graphics are fixed bitmaps; they are immutable data, so they are shared rather than rescaled.
        doc.StoredGraphics.AddRange(source.StoredGraphics);
        if (!same)
            foreach (var g in source.StoredGraphics)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Graphic.NotScaled", null, g.Path));
        foreach (var v in source.Variables) doc.Variables.Add(v.Clone());
        foreach (var e in source.Elements) doc.Elements.Add(ConvertElement(e, c));
        return new DpiConversionResult(doc, issues);
    }

    private sealed class Ctx(double ratio, bool same, List<ValidationIssue> issues)
    {
        public double Ratio { get; } = ratio;
        public bool Same { get; } = same;
        public List<ValidationIssue> Issues { get; } = issues;

        /// <summary>Scales a dot quantity, rounding half away from zero and applying a lower bound.</summary>
        public int Dots(int value, int min) => Same ? value : Math.Max(min, (int)Math.Round(value * Ratio, MidpointRounding.AwayFromZero));
    }

    private static Measure ScaleMeasure(Measure m, Ctx c) =>
        m.Unit == LengthUnit.Dots ? Measure.Dots(c.Dots((int)m.Value, 0)) : m;

    private static string Label(LabelElement e) => string.IsNullOrEmpty(e.Name) ? e.Id : e.Name;

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Scales then clamps to a documented printer range; reports when the clamp altered the value.</summary>
    private static int Ranged(LabelElement e, string property, int value, int min, int max, Ctx c)
    {
        int ideal = c.Dots(value, int.MinValue);
        int applied = Math.Clamp(ideal, min, max);
        if (applied != ideal)
            c.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Value.Clamped", e.Id, Label(e), property, Inv(ideal), Inv(applied)));
        return applied;
    }

    private static void CopyBase(LabelElement from, LabelElement to, Ctx c)
    {
        to.Id = from.Id;
        to.Name = from.Name;
        to.X = c.Dots(from.X, int.MinValue);
        to.Y = c.Dots(from.Y, int.MinValue);
        to.OriginKind = from.OriginKind;
        to.Rotation = from.Rotation;
        to.ReverseField = from.ReverseField;
        from.CopyObjectSettingsTo(to);
    }

    private static LabelElement ConvertElement(LabelElement e, Ctx c)
    {
        switch (e)
        {
            case DataFieldElement df:
                {
                    var n = new DataFieldElement { VariableName = df.VariableName };
                    CopyText(df, n, c);
                    return n;
                }
            case TextElement t:
                {
                    var n = new TextElement();
                    CopyText(t, n, c);
                    return n;
                }
            case BarcodeElement b:
                return ConvertBarcode(b, c);
            case BoxElement b:
                {
                    var n = new BoxElement
                    {
                        WidthDots = c.Dots(b.WidthDots, 1),
                        HeightDots = c.Dots(b.HeightDots, 1),
                        ThicknessDots = c.Dots(b.ThicknessDots, 1),
                        Color = b.Color,
                        CornerRounding = b.CornerRounding,
                    };
                    CopyBase(b, n, c);
                    return n;
                }
            case LineElement l:
                {
                    var n = new LineElement
                    {
                        LengthDots = c.Dots(l.LengthDots, 1),
                        ThicknessDots = c.Dots(l.ThicknessDots, 1),
                        Direction = l.Direction,
                        Color = l.Color,
                    };
                    CopyBase(l, n, c);
                    return n;
                }
            case CircleElement ci:
                {
                    var n = new CircleElement { Color = ci.Color };
                    CopyBase(ci, n, c);
                    n.DiameterDots = Ranged(ci, nameof(CircleElement.DiameterDots), ci.DiameterDots, 3, 4095, c);
                    n.ThicknessDots = c.Dots(ci.ThicknessDots, 1);
                    return n;
                }
            case DiagonalLineElement dl:
                {
                    var n = new DiagonalLineElement
                    {
                        WidthDots = c.Dots(dl.WidthDots, 3),
                        HeightDots = c.Dots(dl.HeightDots, 3),
                        ThicknessDots = c.Dots(dl.ThicknessDots, 1),
                        Color = dl.Color,
                        Lean = dl.Lean,
                    };
                    CopyBase(dl, n, c);
                    return n;
                }
            case EllipseElement el:
                {
                    var n = new EllipseElement { Color = el.Color };
                    CopyBase(el, n, c);
                    n.WidthDots = Ranged(el, nameof(EllipseElement.WidthDots), el.WidthDots, 3, 4095, c);
                    n.HeightDots = Ranged(el, nameof(EllipseElement.HeightDots), el.HeightDots, 3, 4095, c);
                    n.ThicknessDots = c.Dots(el.ThicknessDots, 1);
                    return n;
                }
            case SymbolElement sy:
                {
                    var n = new SymbolElement
                    {
                        Symbol = sy.Symbol,
                        HeightDots = sy.HeightDots == 0 ? 0 : c.Dots(sy.HeightDots, 1),
                        WidthDots = sy.WidthDots == 0 ? 0 : c.Dots(sy.WidthDots, 1),
                    };
                    CopyBase(sy, n, c);
                    return n;
                }
            case ImageElement im:
                return ConvertImage(im, c);
            case RawZplElement r:
                {
                    var n = new RawZplElement { Zpl = r.Zpl, IsVisual = r.IsVisual };
                    CopyVerbatim(r, n, c);
                    return n;
                }
            case RecallPlaceholderElement rp:
                {
                    var n = new RecallPlaceholderElement
                    {
                        Command = rp.Command,
                        Reference = rp.Reference,
                        Zpl = rp.Zpl,
                        EmitAsField = rp.EmitAsField,
                        WidthDots = rp.WidthDots,
                        HeightDots = rp.HeightDots,
                    };
                    CopyVerbatim(rp, n, c);
                    return n;
                }
            default:
                throw new NotSupportedException($"Element type {e.GetType().Name} is not supported by the DPI converter.");
        }
    }

    /// <summary>Raw ZPL cannot be rewritten safely: keep it exactly and tell the caller its dots are the old ones.</summary>
    private static void CopyVerbatim(LabelElement from, LabelElement to, Ctx c)
    {
        to.Id = from.Id;
        to.Name = from.Name;
        to.X = from.X;
        to.Y = from.Y;
        to.OriginKind = from.OriginKind;
        to.Rotation = from.Rotation;
        to.ReverseField = from.ReverseField;
        from.CopyObjectSettingsTo(to);
        if (!c.Same)
            c.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Raw.NotScaled", from.Id, Label(from)));
    }

    private static void CopyText(TextElement from, TextElement to, Ctx c)
    {
        CopyBase(from, to, c);
        to.Text = from.Text;
        to.FontId = from.FontId;
        // A Windows font is sized in points, which are physical, so it needs no scaling; the picture is drawn at the new resolution when printed.
        to.Font = from.Font?.Clone();
        to.StoredFontPath = from.StoredFontPath;
        // null means "inherit the default font" and must stay null; width 0 means proportional and stays 0.
        to.FontHeight = from.FontHeight is { } h ? (h == 0 ? 0 : c.Dots(h, 1)) : null;
        to.FontWidth = from.FontWidth is { } w ? (w == 0 ? 0 : c.Dots(w, 1)) : null;
        to.Direction = from.Direction;
        to.InterCharacterGap = c.Dots(from.InterCharacterGap, 0);
        CopyFieldOptions(from, to);
        if (from.TextBox is { } tb)
        {
            to.TextBox = new TextBoxOptions
            {
                WidthDots = Ranged(from, nameof(TextBoxOptions.WidthDots), tb.WidthDots, 1, 32000, c),
                HeightDots = Ranged(from, nameof(TextBoxOptions.HeightDots), tb.HeightDots, 1, 32000, c),
            };
        }
        if (from.Block is { } b)
        {
            to.Block = new TextBlockOptions
            {
                WidthDots = Ranged(from, nameof(TextBlockOptions.WidthDots), b.WidthDots, 1, 9999, c),
                MaxLines = b.MaxLines,
                LineSpacing = c.Dots(b.LineSpacing, int.MinValue),
                Alignment = b.Alignment,
                HangingIndent = c.Dots(b.HangingIndent, int.MinValue),
            };
        }
    }

    /// <summary>Copies the data-field options (field number, embed, clock, serial); none of them depends on the resolution.</summary>
    private static void CopyFieldOptions(FieldDataElement from, FieldDataElement to)
    {
        to.FieldNumber = from.FieldNumber;
        to.FieldPrompt = from.FieldPrompt;
        to.EmbedCharacter = from.EmbedCharacter;
        to.Clock = from.Clock is { } k ? new FieldClockOptions { Primary = k.Primary, Secondary = k.Secondary, Third = k.Third } : null;
        to.Serial = from.Serial is { } s
            ? new PrinterSerialOptions { Kind = s.Kind, Increment = s.Increment, LeadingZeros = s.LeadingZeros, Mask = s.Mask, IncrementText = s.IncrementText }
            : null;
    }

    private static ImageElement ConvertImage(ImageElement im, Ctx c)
    {
        var n = new ImageElement
        {
            Source = im.Source, // immutable, safe to share; re-dithered at the new size by ImageProcessor
            Dither = im.Dither,
            Threshold = im.Threshold,
            Invert = im.Invert,
        };
        CopyBase(im, n, c);
        if (im.WidthDots == 0 && im.HeightDots == 0 && im.Source is { } src)
        {
            // 0/0 means "source pixel size", which is a fixed dot count; pin it so the physical size survives.
            n.WidthDots = c.Dots(src.Width, 1);
            n.HeightDots = c.Dots(src.Height, 1);
        }
        else
        {
            // A single 0 keeps meaning "derive from the aspect ratio".
            n.WidthDots = im.WidthDots == 0 ? 0 : c.Dots(im.WidthDots, 1);
            n.HeightDots = im.HeightDots == 0 ? 0 : c.Dots(im.HeightDots, 1);
        }
        return n;
    }

    private static BarcodeElement ConvertBarcode(BarcodeElement b, Ctx c)
    {
        var n = new BarcodeElement
        {
            Symbology = b.Symbology,
            Data = b.Data,
            WideBarRatio = b.WideBarRatio,
            HumanReadable = b.HumanReadable,
            HumanReadablePosition = b.HumanReadablePosition,
            CheckDigit = b.CheckDigit,
            Code128Mode = b.Code128Mode,
            QrErrorCorrection = b.QrErrorCorrection,
            QrModel = b.QrModel,
            Columns = b.Columns,
            Rows = b.Rows,
            Pdf417SecurityLevel = b.Pdf417SecurityLevel,
            Pdf417Truncate = b.Pdf417Truncate,
        };
        CopyBase(b, n, c);
        CopyFieldOptions(b, n);
        // The options of the later symbologies (Aztec, DataBar, ...) are plain text and carry over unchanged,
        // then the dot-based ones among them are scaled below.
        n.ImportExtraOptions(b.ExportExtraOptions());

        // The catalogue says which options apply to the symbology and which of them are dot measures
        // (BarcodeCatalog.GetDotKind). Options that do not apply are scaled too (silently) so that switching the
        // symbology later does not resurrect a value from the wrong resolution.
        var info = BarcodeCatalog.Get(b.Symbology);
        // Printers only accept whole-dot modules (1-10). Rounding the ideal value to the nearest integer already
        // gives the symbol width closest to the original physical width, since width = modules * module size.
        n.ModuleWidth = Module(b, info, BarcodeOption.ModuleWidth, b.ModuleWidth, c);
        n.ModuleSize = Module(b, info, BarcodeOption.ModuleSize, b.ModuleSize, c);
        n.Tlc39MicroModuleWidth = Module(b, info, BarcodeOption.Tlc39MicroModuleWidth, b.Tlc39MicroModuleWidth, c);
        // Heights scale proportionally: bar height, PDF417/MicroPDF417/Codablock row height, DataBar symbol height.
        n.Height = Ranged(b, nameof(BarcodeElement.Height), b.Height, 1, 32000, c);
        n.Tlc39MicroRowHeight = Ranged(b, nameof(BarcodeElement.Tlc39MicroRowHeight), b.Tlc39MicroRowHeight, 1, 255, c);
        return n;
    }

    /// <summary>Scales one module-width option to whole dots (1-10) and warns when the rounding changes the symbol width noticeably.</summary>
    private static int Module(BarcodeElement b, BarcodeSymbologyInfo info, BarcodeOption option, int old, Ctx c)
    {
        double ideal = old * c.Ratio;
        int module = Math.Clamp(c.Dots(old, 1), 1, 10);
        if (!c.Same && info.Supports(option) && ideal > 0 && Math.Abs(module - ideal) / ideal > ModuleToleranceRatio)
            c.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Barcode.ModuleRounded", b.Id, Label(b), Inv(old), Inv(module)));
        return module;
    }
}
