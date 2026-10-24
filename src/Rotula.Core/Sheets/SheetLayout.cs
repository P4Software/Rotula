using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Sheets;

/// <summary>Paper size of a label sheet.</summary>
public enum SheetPageKind
{
    /// <summary>US Letter, 8.5 x 11 in (215.9 x 279.4 mm).</summary>
    Letter,
    /// <summary>ISO A4, 210 x 297 mm.</summary>
    A4,
    /// <summary>US Legal, 8.5 x 14 in (215.9 x 355.6 mm).</summary>
    Legal,
    /// <summary>Any other size, given in millimetres.</summary>
    Custom,
}

/// <summary>The order in which labels fill the positions of a sheet.</summary>
public enum SheetOrder
{
    /// <summary>Left to right along a row, then the next row down (reading order).</summary>
    AcrossThenDown,
    /// <summary>Top to bottom along a column, then the next column to the right.</summary>
    DownThenAcross,
}

/// <summary>Marks drawn on a sheet to help cut or check the labels.</summary>
public enum SheetGuides
{
    /// <summary>Nothing but the labels.</summary>
    None,
    /// <summary>Short crop marks in the page margins, in line with the label edges.</summary>
    CropMarks,
    /// <summary>A thin grey outline around every printed label (for a test print on plain paper).</summary>
    Outlines,
}

/// <summary>One label position on a sheet, in millimetres from the top-left corner of the paper.</summary>
/// <param name="Index">0-based position in the fill order of the sheet.</param>
/// <param name="Column">0-based column, counted from the left.</param>
/// <param name="Row">0-based row, counted from the top.</param>
/// <param name="XMm">Left edge of the label.</param>
/// <param name="YMm">Top edge of the label.</param>
/// <param name="WidthMm">Label width.</param>
/// <param name="HeightMm">Label height.</param>
public readonly record struct SheetSlot(int Index, int Column, int Row, double XMm, double YMm, double WidthMm, double HeightMm)
{
    /// <summary>1-based position as the operator counts it (the "start at position" number).</summary>
    public int Position => Index + 1;
}

/// <summary>
/// The geometry of a sheet of labels for an office (laser or inkjet) printer: the paper, the distance from the paper's top-left corner to the first
/// label, the label size, how many labels across and down, and the pitch (the distance from one label's edge to the same edge of the next one,
/// which is the label size plus the gap). Everything is in millimetres. The type knows nothing about the label design or the printer.
/// </summary>
/// <remarks>The corner radius of a die-cut sheet is irrelevant here: a label design is rectangular, and a printer prints over the corners.</remarks>
public sealed record SheetLayout
{
    /// <summary>Width and height of a US Letter page.</summary>
    public const double LetterWidthMm = 215.9, LetterHeightMm = 279.4;

    /// <summary>Width and height of an A4 page.</summary>
    public const double A4WidthMm = 210, A4HeightMm = 297;

    /// <summary>Width and height of a US Legal page.</summary>
    public const double LegalWidthMm = 215.9, LegalHeightMm = 355.6;

    /// <summary>Smallest and largest accepted page side.</summary>
    public const double MinPageMm = 25, MaxPageMm = 1500;

    /// <summary>Smallest accepted label side.</summary>
    public const double MinLabelMm = 3;

    /// <summary>Most columns and rows a sheet may have.</summary>
    public const int MaxColumns = 50, MaxRows = 100;

    /// <summary>
    /// How close to the paper edge a label may come before a warning says that the printer may not reach it. Most office printers leave about
    /// 3 to 6 mm unprinted at the edges.
    /// </summary>
    public const double PrinterEdgeMm = 3;

    /// <summary>Rounding slack for comparisons, so a layout given in inches is not refused for a hundredth of a millimetre.</summary>
    public const double ToleranceMm = 0.05;

    /// <summary>Millimetres per inch.</summary>
    public const double MmPerInch = 25.4;

    /// <summary>The named paper size; <see cref="SheetPageKind.Custom"/> means <see cref="PageWidthMm"/> and <see cref="PageHeightMm"/> are the user's.</summary>
    public SheetPageKind PageKind { get; init; } = SheetPageKind.Letter;

    /// <summary>Paper width.</summary>
    public double PageWidthMm { get; init; } = LetterWidthMm;

    /// <summary>Paper height.</summary>
    public double PageHeightMm { get; init; } = LetterHeightMm;

    /// <summary>Distance from the top of the paper to the top of the first row.</summary>
    public double TopMarginMm { get; init; }

    /// <summary>Distance from the left of the paper to the left of the first column.</summary>
    public double LeftMarginMm { get; init; }

    /// <summary>Width of one label.</summary>
    public double LabelWidthMm { get; init; } = 50;

    /// <summary>Height of one label.</summary>
    public double LabelHeightMm { get; init; } = 25;

    /// <summary>Labels across.</summary>
    public int Columns { get; init; } = 1;

    /// <summary>Labels down.</summary>
    public int Rows { get; init; } = 1;

    /// <summary>Distance from the left edge of one column to the left edge of the next (label width plus the gap).</summary>
    public double HorizontalPitchMm { get; init; }

    /// <summary>Distance from the top edge of one row to the top edge of the next (label height plus the gap).</summary>
    public double VerticalPitchMm { get; init; }

    /// <summary>Number of label positions on one sheet.</summary>
    public int SlotCount => Math.Max(0, Columns) * Math.Max(0, Rows);

    /// <summary>Gap between two columns (pitch minus label width).</summary>
    public double HorizontalGapMm => HorizontalPitchMm - LabelWidthMm;

    /// <summary>Gap between two rows (pitch minus label height).</summary>
    public double VerticalGapMm => VerticalPitchMm - LabelHeightMm;

    /// <summary>The right edge of the last column.</summary>
    public double GridRightMm => LeftMarginMm + (Math.Max(1, Columns) - 1) * HorizontalPitchMm + LabelWidthMm;

    /// <summary>The bottom edge of the last row.</summary>
    public double GridBottomMm => TopMarginMm + (Math.Max(1, Rows) - 1) * VerticalPitchMm + LabelHeightMm;

    /// <summary>The paper width and height of a named size; null for <see cref="SheetPageKind.Custom"/>.</summary>
    public static (double WidthMm, double HeightMm)? PageSize(SheetPageKind kind) => kind switch
    {
        SheetPageKind.Letter => (LetterWidthMm, LetterHeightMm),
        SheetPageKind.A4 => (A4WidthMm, A4HeightMm),
        SheetPageKind.Legal => (LegalWidthMm, LegalHeightMm),
        _ => null,
    };

    /// <summary>
    /// Builds a layout from the usual sheet description: paper, grid, label size, margins and gaps (rather than pitches).
    /// </summary>
    /// <param name="page">A named paper size (not <see cref="SheetPageKind.Custom"/>; use the object initializer for that).</param>
    /// <param name="columns">Labels across.</param>
    /// <param name="rows">Labels down.</param>
    /// <param name="labelWidthMm">Label width.</param>
    /// <param name="labelHeightMm">Label height.</param>
    /// <param name="topMm">Top margin.</param>
    /// <param name="leftMm">Left margin.</param>
    /// <param name="horizontalGapMm">Gap between columns.</param>
    /// <param name="verticalGapMm">Gap between rows.</param>
    public static SheetLayout Create(SheetPageKind page, int columns, int rows, double labelWidthMm, double labelHeightMm,
        double topMm, double leftMm, double horizontalGapMm = 0, double verticalGapMm = 0)
    {
        var size = PageSize(page) ?? throw new ArgumentException("A custom page needs its size; set PageWidthMm and PageHeightMm instead.", nameof(page));
        return new SheetLayout
        {
            PageKind = page, PageWidthMm = size.WidthMm, PageHeightMm = size.HeightMm,
            Columns = columns, Rows = rows, LabelWidthMm = labelWidthMm, LabelHeightMm = labelHeightMm,
            TopMarginMm = topMm, LeftMarginMm = leftMm,
            HorizontalPitchMm = labelWidthMm + horizontalGapMm, VerticalPitchMm = labelHeightMm + verticalGapMm,
        };
    }

    /// <summary>
    /// Builds a layout whose grid is centred on the paper. Used when only the label size, the grid and the gaps are known (a sheet stock of the
    /// library states no margins): most sheets are cut symmetrically, and the operator can still adjust the margins.
    /// </summary>
    public static SheetLayout Centered(SheetPageKind page, int columns, int rows, double labelWidthMm, double labelHeightMm,
        double horizontalGapMm = 0, double verticalGapMm = 0)
    {
        var size = PageSize(page) ?? throw new ArgumentException("A custom page needs its size.", nameof(page));
        double gridW = columns * labelWidthMm + (columns - 1) * horizontalGapMm;
        double gridH = rows * labelHeightMm + (rows - 1) * verticalGapMm;
        return Create(page, columns, rows, labelWidthMm, labelHeightMm,
            Math.Max(0, Math.Round((size.HeightMm - gridH) / 2, 2)), Math.Max(0, Math.Round((size.WidthMm - gridW) / 2, 2)), horizontalGapMm, verticalGapMm);
    }

    /// <summary>The same grid on another paper size (the named size's dimensions replace the page width and height).</summary>
    public SheetLayout WithPage(SheetPageKind page)
    {
        if (PageSize(page) is not { } size) return this with { PageKind = SheetPageKind.Custom };
        return this with { PageKind = page, PageWidthMm = size.WidthMm, PageHeightMm = size.HeightMm };
    }

    /// <summary>The position with 0-based <paramref name="index"/> in the given fill order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is not on the sheet.</exception>
    public SheetSlot Slot(int index, SheetOrder order = SheetOrder.AcrossThenDown)
    {
        if (index < 0 || index >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        int column, row;
        if (order == SheetOrder.DownThenAcross) { column = index / Rows; row = index % Rows; }
        else { row = index / Columns; column = index % Columns; }
        return new SheetSlot(index, column, row, LeftMarginMm + column * HorizontalPitchMm, TopMarginMm + row * VerticalPitchMm, LabelWidthMm, LabelHeightMm);
    }

    /// <summary>Every position of the sheet in fill order.</summary>
    public IEnumerable<SheetSlot> Slots(SheetOrder order = SheetOrder.AcrossThenDown)
    {
        for (int i = 0; i < SlotCount; i++) yield return Slot(i, order);
    }

    /// <summary>
    /// Checks that the layout can be printed: sizes in range, labels that do not overlap and stay on the paper. Errors make the layout unusable;
    /// the warning <c>Sheet.Labels.NearEdge</c> says that labels come so close to the paper edge that a printer may not reach them.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();
        if (!InRange(PageWidthMm, MinPageMm, MaxPageMm) || !InRange(PageHeightMm, MinPageMm, MaxPageMm))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Page.Size", null, Mm(MinPageMm), Mm(MaxPageMm)));
        if (!InRange(LabelWidthMm, MinLabelMm, MaxPageMm) || !InRange(LabelHeightMm, MinLabelMm, MaxPageMm))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Label.Size", null, Mm(MinLabelMm)));
        if (Columns < 1 || Columns > MaxColumns || Rows < 1 || Rows > MaxRows)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Grid.Count", null, Num(MaxColumns), Num(MaxRows)));
        if (!double.IsFinite(TopMarginMm) || !double.IsFinite(LeftMarginMm) || TopMarginMm < 0 || LeftMarginMm < 0)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Margin.Negative", null));
        if (!double.IsFinite(HorizontalPitchMm) || !double.IsFinite(VerticalPitchMm))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Labels.OverlapAcross", null, Mm(HorizontalPitchMm), Mm(LabelWidthMm)));
        if (issues.Count > 0) return issues; // the checks below need sane numbers

        // The pitch only matters when there is a next column or row to collide with.
        if (Columns > 1 && HorizontalPitchMm < LabelWidthMm - ToleranceMm)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Labels.OverlapAcross", null, Mm(HorizontalPitchMm), Mm(LabelWidthMm)));
        if (Rows > 1 && VerticalPitchMm < LabelHeightMm - ToleranceMm)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Labels.OverlapDown", null, Mm(VerticalPitchMm), Mm(LabelHeightMm)));
        if (GridRightMm > PageWidthMm + ToleranceMm)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Labels.TooWide", null, Mm(GridRightMm), Mm(PageWidthMm)));
        if (GridBottomMm > PageHeightMm + ToleranceMm)
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Sheet.Labels.TooTall", null, Mm(GridBottomMm), Mm(PageHeightMm)));
        if (issues.Count == 0)
        {
            double closest = Math.Min(Math.Min(LeftMarginMm, TopMarginMm), Math.Min(PageWidthMm - GridRightMm, PageHeightMm - GridBottomMm));
            if (closest < PrinterEdgeMm - ToleranceMm)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Sheet.Labels.NearEdge", null, Mm(PrinterEdgeMm)));
        }
        return issues;
    }

    /// <summary>True when <see cref="Validate"/> reports no error.</summary>
    public bool IsValid => Validate().All(i => i.Severity != ValidationSeverity.Error);

    /// <summary>Checks a "start at position" number (1-based); null when it is on the sheet, otherwise the error <c>Sheet.Start.Range</c>.</summary>
    public ValidationIssue? ValidateStart(int startPosition) =>
        startPosition >= 1 && startPosition <= SlotCount
            ? null
            : new ValidationIssue(ValidationSeverity.Error, "Sheet.Start.Range", null, Num(Math.Max(1, SlotCount)));

    /// <summary>
    /// Compares the size of the label design with the size of the positions on the sheet. The design is always printed at its own physical size,
    /// centred in each position, so a different size is a warning: a larger design is cut off at the edges of the position
    /// (<c>Sheet.Label.Larger</c>), a smaller one leaves a white border (<c>Sheet.Label.Smaller</c>). Null when the sizes agree within half a millimetre.
    /// </summary>
    /// <param name="designWidthMm">Width of the label design.</param>
    /// <param name="designHeightMm">Height of the label design.</param>
    public ValidationIssue? CheckDesignSize(double designWidthMm, double designHeightMm)
    {
        const double slack = 0.5;
        if (Math.Abs(designWidthMm - LabelWidthMm) <= slack && Math.Abs(designHeightMm - LabelHeightMm) <= slack) return null;
        bool larger = designWidthMm > LabelWidthMm + slack || designHeightMm > LabelHeightMm + slack;
        return larger
            ? new ValidationIssue(ValidationSeverity.Warning, "Sheet.Label.Larger", null, Mm(designWidthMm), Mm(designHeightMm), Mm(LabelWidthMm), Mm(LabelHeightMm))
            : new ValidationIssue(ValidationSeverity.Warning, "Sheet.Label.Smaller", null, Mm(designWidthMm), Mm(designHeightMm), Mm(LabelWidthMm), Mm(LabelHeightMm));
    }

    private static bool InRange(double v, double min, double max) => double.IsFinite(v) && v >= min - ToleranceMm && v <= max + ToleranceMm;

    /// <summary>Millimetres as message text: invariant, at most two decimals (the UI shows the argument as it is).</summary>
    internal static string Mm(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
}
