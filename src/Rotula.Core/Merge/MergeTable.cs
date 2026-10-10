using Rotula.Core.Validation;

namespace Rotula.Core.Merge;

/// <summary>
/// A tabular data set for label merge: named columns plus rows of strings. Every row has exactly
/// <see cref="Columns"/>.Count cells (readers pad short rows and trim long ones).
/// </summary>
public sealed class MergeTable
{
    private readonly List<string> _columns;
    private readonly List<string[]> _rows;
    private readonly List<ValidationIssue> _issues = new();

    /// <summary>Creates a table. Column names are made unique and non-empty (see <see cref="NormalizeHeaders"/>).</summary>
    public MergeTable(IEnumerable<string> columns, IEnumerable<string[]>? rows = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns = NormalizeHeaders(columns);
        _rows = new List<string[]>();
        if (rows is not null)
            foreach (var r in rows) AddRow(r);
    }

    /// <summary>Column names in order.</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>Data rows (header excluded).</summary>
    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>Number of data rows.</summary>
    public int RowCount => _rows.Count;

    /// <summary>Problems found while reading (for example an unterminated quote). Message keys only, no UI text.</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>Appends a row, padding or trimming it to the column count.</summary>
    public void AddRow(IReadOnlyList<string> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = new string[_columns.Count];
        for (int i = 0; i < row.Length; i++) row[i] = i < cells.Count ? cells[i] ?? string.Empty : string.Empty;
        _rows.Add(row);
    }

    internal void AddIssue(ValidationIssue issue) => _issues.Add(issue);

    /// <summary>Index of the column called <paramref name="name"/> (case-insensitive), or -1.</summary>
    public int IndexOfColumn(string name)
    {
        for (int i = 0; i < _columns.Count; i++)
            if (string.Equals(_columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Makes header names usable: trims them, replaces empty ones with <c>ColumnN</c> (1-based position) and
    /// de-duplicates case-insensitively by appending <c>_2</c>, <c>_3</c>... so a placeholder maps unambiguously.
    /// </summary>
    public static List<string> NormalizeHeaders(IEnumerable<string> raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        foreach (var h in raw)
        {
            pos++;
            string name = (h ?? string.Empty).Trim();
            if (name.Length == 0) name = "Column" + pos;
            string candidate = name;
            for (int n = 2; !seen.Add(candidate); n++) candidate = name + "_" + n;
            result.Add(candidate);
        }
        return result;
    }
}
