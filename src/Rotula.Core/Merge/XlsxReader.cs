using System.Globalization;
using MiniExcelLibs;

namespace Rotula.Core.Merge;

/// <summary>Options for <see cref="XlsxReader"/>.</summary>
public sealed class XlsxOptions
{
    /// <summary>Worksheet name, or null for the first sheet.</summary>
    public string? SheetName { get; set; }

    /// <summary>True when the first row holds column names.</summary>
    public bool HasHeader { get; set; } = true;

    /// <summary>Most columns the sheet may have; a wider sheet is refused with a <c>Merge.Table.TooWide</c> issue instead of being padded.</summary>
    public int MaxColumns { get; set; } = MergeLimits.DefaultMaxColumns;

    /// <summary>Most cells (columns times data rows) the table may have; a larger sheet is refused with a <c>Merge.Table.TooLarge</c> issue.</summary>
    public long MaxCells { get; set; } = MergeLimits.DefaultMaxCells;
}

/// <summary>
/// Reads .xlsx worksheets into a <see cref="MergeTable"/> using MiniExcel (no Excel installation needed).
/// Cell values are converted with the invariant culture so output never depends on the machine locale.
/// </summary>
public static class XlsxReader
{
    /// <summary>Names of the worksheets in the workbook, in workbook order.</summary>
    public static IReadOnlyList<string> GetSheetNames(Stream stream) => MiniExcel.GetSheetNames(stream);

    /// <summary>Reads a workbook from a file.</summary>
    public static MergeTable ReadFile(string path, XlsxOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(fs, options);
    }

    /// <summary>Reads a workbook from a stream.</summary>
    public static MergeTable Read(Stream stream, XlsxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new XlsxOptions();

        // useHeaderRow:false makes MiniExcel key cells by column letter (A, B, ...), so we control header handling.
        var rows = new List<string[]>();
        int width = 0;
        long storedCells = 0;
        foreach (var item in MiniExcel.Query(stream, useHeaderRow: false, sheetName: options.SheetName, excelType: ExcelType.XLSX))
        {
            var dict = (IDictionary<string, object?>)item;
            var cells = new List<(int Col, string Text)>();
            foreach (var kv in dict)
            {
                int col = ColumnIndex(kv.Key);
                if (col < 0) continue;
                string text = ToText(kv.Value);
                if (col >= options.MaxColumns)
                {
                    // An empty cell out there is only formatting (the sheet's used range reaches that far), so it is ignored; a filled one
                    // makes the sheet too wide. Stopping here also keeps a lone cell at column XFD from allocating a 16,384 entry row.
                    if (text.Length == 0) continue;
                    return MergeLimits.Refuse(MergeLimits.Check(col + 1, 0, options.MaxColumns, options.MaxCells)!);
                }
                cells.Add((col, text));
                width = Math.Max(width, col + 1);
            }
            var row = new string[cells.Count == 0 ? 0 : cells.Max(c => c.Col) + 1];
            Array.Fill(row, string.Empty);
            foreach (var (col, text) in cells) row[col] = text;
            // Rows without any text are formatting leftovers, not records.
            if (row.Any(c => c.Length > 0))
            {
                rows.Add(row);
                // Rows are kept as long as their last filled cell, so this is the real memory use so far; stop before it grows without bound.
                storedCells += row.Length;
                if (storedCells > options.MaxCells) return MergeLimits.Refuse(MergeLimits.TooLarge(storedCells, options.MaxCells));
            }
        }

        List<string> header = new();
        int start = 0;
        if (options.HasHeader && rows.Count > 0) { header.AddRange(rows[0]); start = 1; }
        while (header.Count < width) header.Add(string.Empty);
        if (MergeLimits.Check(width, rows.Count - start, options.MaxColumns, options.MaxCells) is { } tooBig)
            return MergeLimits.Refuse(tooBig);

        var table = new MergeTable(header);
        for (int i = start; i < rows.Count; i++) table.AddRow(rows[i]);
        return table;
    }

    /// <summary>Converts column letters ("A", "AB") to a zero-based index; -1 if the key is not a column.</summary>
    internal static int ColumnIndex(string key)
    {
        int n = 0;
        foreach (char c in key)
        {
            if (c is < 'A' or > 'Z') return -1;
            n = n * 26 + (c - 'A' + 1);
        }
        return n == 0 ? -1 : n - 1;
    }

    private static string ToText(object? v) => v switch
    {
        null => string.Empty,
        string s => s,
        // Midnight dates print as a plain date; anything with a time keeps it.
        DateTime d => d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };
}
