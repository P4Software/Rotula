using System.Globalization;
using Rotula.Core.Validation;

namespace Rotula.Core.Merge;

/// <summary>
/// Size limits for data files. Readers pad every row to the widest one so that <c>Rows[i][c]</c> is always valid, which means a file can make
/// the table many times bigger than the file itself (one very wide header plus many short rows). These limits are checked before any padding.
/// </summary>
public static class MergeLimits
{
    /// <summary>Most columns a data file may have by default.</summary>
    public const int DefaultMaxColumns = 1000;

    /// <summary>
    /// Most cells (columns times data rows, after padding) a data file may have by default. 20 million is 100,000 rows of 200 columns, far more
    /// than a label run needs, and keeps the table near 160 MB at worst.
    /// </summary>
    public const long DefaultMaxCells = 20_000_000;

    /// <summary>
    /// Returns the issue that says the table is over a limit, or null when it fits.
    /// </summary>
    /// <param name="columns">Width of the widest row.</param>
    /// <param name="dataRows">Number of data rows (the header is not counted).</param>
    /// <param name="maxColumns">Column limit.</param>
    /// <param name="maxCells">Cell limit.</param>
    internal static ValidationIssue? Check(int columns, long dataRows, int maxColumns, long maxCells)
    {
        if (columns > maxColumns)
            return new ValidationIssue(ValidationSeverity.Error, "Merge.Table.TooWide", null,
                columns.ToString(CultureInfo.InvariantCulture), maxColumns.ToString(CultureInfo.InvariantCulture));
        // long arithmetic: columns and rows are each below int.MaxValue, so the product cannot overflow a long.
        long cells = (long)columns * dataRows;
        return cells > maxCells ? TooLarge(cells, maxCells) : null;
    }

    /// <summary>The issue for a table of <paramref name="cells"/> cells when only <paramref name="maxCells"/> are allowed.</summary>
    internal static ValidationIssue TooLarge(long cells, long maxCells) =>
        new(ValidationSeverity.Error, "Merge.Table.TooLarge", null, cells.ToString(CultureInfo.InvariantCulture), maxCells.ToString(CultureInfo.InvariantCulture));

    /// <summary>An empty table that carries the reason the file was not read. Nothing is merged from it, so a refused file can never print by accident.</summary>
    internal static MergeTable Refuse(ValidationIssue reason)
    {
        var table = new MergeTable(Array.Empty<string>());
        table.AddIssue(reason);
        return table;
    }
}
