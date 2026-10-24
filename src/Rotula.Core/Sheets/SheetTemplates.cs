using Rotula.Core.Stocks;

namespace Rotula.Core.Sheets;

/// <summary>A built-in sheet layout. It is described by its dimensions only; the UI builds its name from them.</summary>
/// <param name="Id">Stable id, stored in the .rotula file (for example <c>letter-30</c>).</param>
/// <param name="Layout">The geometry.</param>
/// <param name="Inches">True when the sheet is normally described in inches (US sizes), false for millimetres.</param>
public sealed record SheetTemplate(string Id, SheetLayout Layout, bool Inches);

/// <summary>
/// A small set of common generic sheet layouts for office printers, given by their dimensions. These are the usual industry geometries of
/// address, shipping and file labels; sheets of different suppliers with the same label size and count can still differ by a millimetre
/// in their margins, so the operator prints one test page on plain paper and adjusts the margins when needed.
/// </summary>
public static class SheetTemplates
{
    private const double In = SheetLayout.MmPerInch;

    /// <summary>All built-in templates, Letter first, then A4.</summary>
    public static IReadOnlyList<SheetTemplate> All { get; } = new[]
    {
        // US Letter. Margins and gaps are the common published geometry of each format.
        new SheetTemplate("letter-30", SheetLayout.Create(SheetPageKind.Letter, 3, 10, 2.625 * In, 1 * In, 0.5 * In, 0.1875 * In, 0.125 * In), true),
        new SheetTemplate("letter-20", SheetLayout.Create(SheetPageKind.Letter, 2, 10, 4 * In, 1 * In, 0.5 * In, 0.15625 * In, 0.1875 * In), true),
        new SheetTemplate("letter-14", SheetLayout.Create(SheetPageKind.Letter, 2, 7, 4 * In, 4.0 / 3 * In, 5.0 / 6 * In, 0.15625 * In, 0.1875 * In), true),
        new SheetTemplate("letter-10", SheetLayout.Create(SheetPageKind.Letter, 2, 5, 4 * In, 2 * In, 0.5 * In, 0.15625 * In, 0.1875 * In), true),
        new SheetTemplate("letter-6", SheetLayout.Create(SheetPageKind.Letter, 2, 3, 4 * In, 10.0 / 3 * In, 0.5 * In, 0.15625 * In, 0.1875 * In), true),
        new SheetTemplate("letter-80", SheetLayout.Create(SheetPageKind.Letter, 4, 20, 1.75 * In, 0.5 * In, 0.5 * In, 0.28125 * In, 0.3125 * In), true),
        new SheetTemplate("letter-2", SheetLayout.Create(SheetPageKind.Letter, 1, 2, 8.5 * In, 5.5 * In, 0, 0), true),
        new SheetTemplate("letter-1", SheetLayout.Create(SheetPageKind.Letter, 1, 1, 8.5 * In, 11 * In, 0, 0), true),
        // A4.
        new SheetTemplate("a4-24", SheetLayout.Create(SheetPageKind.A4, 3, 8, 70, 37, 0.5, 0), false),
        new SheetTemplate("a4-21", SheetLayout.Create(SheetPageKind.A4, 3, 7, 63.5, 38.1, 15.15, 7.2, 2.54), false),
        new SheetTemplate("a4-65", SheetLayout.Create(SheetPageKind.A4, 5, 13, 38.1, 21.2, 10.7, 4.75, 2.5), false),
        new SheetTemplate("a4-16", SheetLayout.Create(SheetPageKind.A4, 2, 8, 99.1, 33.9, 12.9, 4.65, 2.5), false),
        new SheetTemplate("a4-14", SheetLayout.Create(SheetPageKind.A4, 2, 7, 99.1, 38.1, 15.15, 4.65, 2.5), false),
        new SheetTemplate("a4-8", SheetLayout.Create(SheetPageKind.A4, 2, 4, 99.1, 67.7, 13.1, 4.65, 2.5), false),
        new SheetTemplate("a4-2", SheetLayout.Create(SheetPageKind.A4, 1, 2, 210, 148.5, 0, 0), false),
        new SheetTemplate("a4-1", SheetLayout.Create(SheetPageKind.A4, 1, 1, 210, 297, 0, 0), false),
    };

    /// <summary>The template with this id (ignoring case), or null.</summary>
    public static SheetTemplate? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The template that matches a sheet stock of the library: the same label size (within half a millimetre) and the same number of labels across
    /// and down. Null when the stock is not a sheet, states no grid, or no template has its geometry. This is how a label sized from a sheet
    /// stock opens the sheet dialog on the right layout without the UI naming any brand.
    /// </summary>
    public static SheetTemplate? FindForStock(StockEntry? stock)
    {
        if (stock is null || stock.Format != StockFormat.Sheet && stock.Layout != StockLayout.Sheet) return null;
        if (stock.HeightMm is not { } h || stock.Columns * stock.Rows <= 1) return null;
        return All.FirstOrDefault(t => t.Layout.Columns == stock.Columns && t.Layout.Rows == stock.Rows
            && Math.Abs(t.Layout.LabelWidthMm - stock.WidthMm) <= 0.5 && Math.Abs(t.Layout.LabelHeightMm - h) <= 0.5);
    }

    /// <summary>
    /// The template whose labels have the size of a label design (within half a millimetre), preferring the given paper. Null when none fits.
    /// Used to suggest a layout for a design that was not made from a sheet stock.
    /// </summary>
    public static SheetTemplate? FindForLabelSize(double widthMm, double heightMm, SheetPageKind preferredPage = SheetPageKind.Letter) =>
        All.Where(t => Math.Abs(t.Layout.LabelWidthMm - widthMm) <= 0.5 && Math.Abs(t.Layout.LabelHeightMm - heightMm) <= 0.5)
           .OrderBy(t => t.Layout.PageKind == preferredPage ? 0 : 1)
           .FirstOrDefault();
}
