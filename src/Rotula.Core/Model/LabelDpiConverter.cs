using System.Globalization;
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
/// (args: element name; the element's coordinates stay in the original dots).
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
            Print = new PrintOptions
            {
                Quantity = source.Print.Quantity,
                PauseEvery = source.Print.PauseEvery,
                Replicates = source.Print.Replicates,
                OverridePause = source.Print.OverridePause,
            },
        };
        foreach (var v in source.Variables) doc.Variables.Add(new LabelVariable(v.Name, v.SampleValue));
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
        if (!c.Same)
            c.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Raw.NotScaled", from.Id, Label(from)));
    }

    private static void CopyText(TextElement from, TextElement to, Ctx c)
    {
        CopyBase(from, to, c);
        to.Text = from.Text;
        to.FontId = from.FontId;
        // null means "inherit the default font" and must stay null; width 0 means proportional and stays 0.
        to.FontHeight = from.FontHeight is { } h ? (h == 0 ? 0 : c.Dots(h, 1)) : null;
        to.FontWidth = from.FontWidth is { } w ? (w == 0 ? 0 : c.Dots(w, 1)) : null;
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
        // Height is the bar height (linear), row height (PDF417) and irrelevant for 2D matrix codes; scaling
        // it everywhere keeps the stored value consistent and harmless.
        n.Height = Ranged(b, nameof(BarcodeElement.Height), b.Height, 1, 32000, c);

        bool matrix = b.Symbology is BarcodeSymbology.QrCode or BarcodeSymbology.DataMatrix;
        // Printers only accept whole-dot modules (1-10). Rounding the ideal value to the nearest integer already
        // gives the symbol width closest to the original physical width, since width = modules * module size.
        int oldModule = matrix ? b.ModuleSize : b.ModuleWidth;
        double ideal = oldModule * c.Ratio;
        int module = Math.Clamp(c.Dots(oldModule, 1), 1, 10);
        if (matrix) { n.ModuleSize = module; n.ModuleWidth = b.ModuleWidth; }
        else { n.ModuleWidth = module; n.ModuleSize = b.ModuleSize; }

        if (!c.Same && ideal > 0 && Math.Abs(module - ideal) / ideal > ModuleToleranceRatio)
            c.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Dpi.Barcode.ModuleRounded", b.Id, Label(b), Inv(oldModule), Inv(module)));
        return n;
    }
}
