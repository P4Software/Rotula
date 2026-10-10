using System.Globalization;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>Switches for <see cref="StockApplier.Apply"/>.</summary>
public sealed class StockApplyOptions
{
    /// <summary>
    /// For wristbands (long side along the feed direction) default the fields to rotated text so it runs along the band.
    /// Only done on a label without elements; a label that already has content keeps its rotation.
    /// </summary>
    public bool RotateWristbandFields { get; set; } = true;
}

/// <summary>Outcome of applying a stock to a label. The facts the document model has no place for are surfaced here.</summary>
public sealed class StockApplyResult
{
    internal StockApplyResult(LabelDocument document, StockEntry stock, IReadOnlyList<ValidationIssue> warnings)
    {
        Document = document;
        Stock = stock;
        Warnings = warnings;
    }

    /// <summary>The label with the stock applied. Always a new object: the target passed in is never modified.</summary>
    public LabelDocument Document { get; internal set; }
    /// <summary>The stock that was applied.</summary>
    public StockEntry Stock { get; }
    /// <summary>Warnings as message keys with arguments (see <see cref="StockMessageKeys"/>); never UI text.</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; internal set; }
    /// <summary>True when no target was given and a fresh label was created.</summary>
    public bool CreatedNew { get; internal set; }
    /// <summary>True when the stock's long side was stored as the width and the sides were swapped (wristbands).</summary>
    public bool SidesSwapped { get; internal set; }
    /// <summary>True when the label's default field rotation was set to 90 degrees for a wristband.</summary>
    public bool FieldsRotated { get; internal set; }
    /// <summary>True when the label height was set; false for a continuous roll (the height stays the user's choice).</summary>
    public bool HeightApplied { get; internal set; }
    /// <summary>Die-cut corner radius in millimetres, if known. The label model has no corner radius; use this for display or a future outline.</summary>
    public double? CornerRadiusMm { get; internal set; }
    /// <summary>Labels across the web. Multi-up layout is a later feature: the label is laid out as a single label.</summary>
    public int Columns { get; internal set; } = 1;
    /// <summary>Label rows (sheets). See <see cref="Columns"/>.</summary>
    public int Rows { get; internal set; } = 1;
    /// <summary>Gap between columns in millimetres, if known (for the later multi-up feature).</summary>
    public double? HGapMm { get; internal set; }
    /// <summary>Gap between rows in millimetres, if known.</summary>
    public double? VGapMm { get; internal set; }
    /// <summary>Gap between labels along the roll in millimetres, if known (usually unknown: the printer measures it).</summary>
    public double? GapMm { get; internal set; }
    /// <summary>Roll length in metres for a continuous roll, if known.</summary>
    public double? RollLengthM { get; internal set; }
}

/// <summary>
/// Applies a library stock to a label: physical size, media type and tracking, and the orientation rule for wristbands.
/// </summary>
public static class StockApplier
{
    /// <summary>
    /// Returns a copy of <paramref name="target"/> (or a new label when null) sized for <paramref name="stock"/>.
    /// </summary>
    /// <param name="target">The label to resize, or null to create a new one. It is not modified.</param>
    /// <param name="stock">The stock to apply.</param>
    /// <param name="dpi">The printer resolution. An existing label at another resolution is converted with
    /// <see cref="LabelDpiConverter"/> (its warnings are included), so the content keeps its physical size.</param>
    /// <param name="options">Optional switches.</param>
    /// <exception cref="ArgumentException">The stock has no positive width.</exception>
    public static StockApplyResult Apply(LabelDocument? target, StockEntry stock, PrinterDpi dpi, StockApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (!(stock.WidthMm > 0)) throw new ArgumentException("The stock has no width.", nameof(stock));
        options ??= new StockApplyOptions();

        var warnings = new List<ValidationIssue>();
        LabelDocument doc;
        bool created = target is null;
        if (target is null)
        {
            doc = new LabelDocument { Dpi = dpi };
        }
        else
        {
            // Convert (also for an equal resolution) gives a deep copy, so the caller's label is never touched.
            var converted = LabelDpiConverter.Convert(target, dpi);
            doc = converted.Document;
            warnings.AddRange(converted.Issues);
        }
        bool hadContent = doc.Elements.Count > 0;
        int oldWidthDots = doc.WidthDots, oldHeightDots = doc.HeightDots;

        var result = new StockApplyResult(doc, stock, warnings) { CreatedNew = created };

        // --- Size ------------------------------------------------------------------------------
        double widthMm = stock.WidthMm, widthIn = stock.WidthIn;
        double? heightMm = stock.HeightMm, heightIn = stock.HeightIn;
        // Wristbands are stored with the short side as the width (how they run through the printer). A hand-made entry that
        // lists the long side first is put right, so the label is always narrow across and long along the feed.
        bool wristband = string.Equals(stock.Category, "wristband", StringComparison.OrdinalIgnoreCase);
        if (wristband && heightMm is { } hh && hh < widthMm && heightIn is { } hhi)
        {
            (widthMm, heightMm) = (hh, widthMm);
            (widthIn, heightIn) = (hhi, widthIn);
            result.SidesSwapped = true;
        }

        doc.Width = MeasureFor(stock, widthMm, widthIn);
        if (heightMm is { } hmm)
        {
            doc.Height = MeasureFor(stock, hmm, heightIn ?? hmm / 25.4);
            result.HeightApplied = true;
        }
        else
        {
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyContinuousNoHeight, stock.Id,
                stock.RollLengthM is { } m ? m.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty));
        }

        // --- Media type and tracking ---------------------------------------------------------------
        // "Either" and "unknown" leave the label's media type alone: choosing for the user would be a guess.
        if (stock.Media == StockMedia.DirectThermal) doc.MediaType = MediaType.DirectThermal;
        else if (stock.Media == StockMedia.ThermalTransfer) doc.MediaType = MediaType.ThermalTransfer;

        bool dieCut = false;
        if (stock.Sensing == StockSensing.Mark) doc.MediaTracking = MediaTracking.Mark;
        else if (stock.Sensing == StockSensing.Continuous || stock.Format == StockFormat.Continuous || stock.IsContinuous) doc.MediaTracking = MediaTracking.Continuous;
        else if (stock.Sensing == StockSensing.Gap || stock.Format is StockFormat.Roll or StockFormat.Fanfold or StockFormat.Sheet)
        {
            doc.MediaTracking = MediaTracking.Web;
            dieCut = true;
        }
        // Linerless and unknown formats keep the label's own tracking.

        if (dieCut && stock.GapMm is null)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyGapUnknown, stock.Id));
        if (!stock.Verified)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyUnverified, stock.Id));

        // --- Facts the model cannot hold ------------------------------------------------------------
        result.CornerRadiusMm = stock.CornerRadiusMm;
        result.Columns = stock.Columns;
        result.Rows = stock.Rows;
        result.HGapMm = stock.HGapMm;
        result.VGapMm = stock.VGapMm;
        result.GapMm = stock.GapMm;
        result.RollLengthM = stock.RollLengthM;
        if (stock.Layout == StockLayout.Sheet)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplySheetLayoutNotSupported, stock.Id));
        else if (stock.Columns > 1 || stock.Rows > 1)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyMultiUpNotSupported,
                stock.Columns.ToString(CultureInfo.InvariantCulture), stock.Rows.ToString(CultureInfo.InvariantCulture)));
        if (stock.Layout == StockLayout.Round)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyRoundShape, stock.Id));

        // --- Orientation -----------------------------------------------------------------------------
        // The whole-label orientation (^PO) stays normal: the stock already runs through the printer short side first.
        // For a wristband the text should run along the long side, which is a field rotation, not a label orientation.
        if (wristband && options.RotateWristbandFields)
        {
            if (!hadContent)
            {
                doc.DefaultRotation = FieldRotation.Rotated90;
                result.FieldsRotated = true;
            }
            else if (doc.DefaultRotation != FieldRotation.Rotated90)
            {
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyRotationKept, stock.Id));
            }
        }

        doc.StockId = stock.Id;

        if (hadContent && (doc.WidthDots != oldWidthDots || (result.HeightApplied && doc.HeightDots != oldHeightDots)))
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ApplyElementsMayClip, doc.Elements.Count.ToString(CultureInfo.InvariantCulture)));

        return result;
    }

    /// <summary>
    /// Sizes the label in the unit the maker quotes: a 4 in stock is exactly 4 in (101.6 mm would round the same at the
    /// supported resolutions, but 2.375 in must not become 60.3 mm), a 102 mm stock stays 102 mm.
    /// </summary>
    private static Measure MeasureFor(StockEntry stock, double mm, double inches)
    {
        if (stock.NativeUnit == StockUnit.Inches && StockEntry.IsSixteenth(inches) && Math.Abs(mm - inches * 25.4) <= StockLimits.UnitToleranceMm)
            return Measure.Inches(inches);
        return Measure.Millimeters(mm);
    }
}
