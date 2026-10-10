using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Core.Merge;

/// <summary>
/// A table bound to a label: it knows which placeholders the label uses, which column feeds each one, and
/// can produce the variable dictionary for any record. This is also the preview helper
/// (<see cref="GetRecordData"/>) whose result goes straight into <see cref="ZplGenerator.Generate"/>.
/// </summary>
public sealed class MergeDataSet
{
    private readonly Dictionary<string, int> _placeholderColumn = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SerialCounter> _counters;
    private readonly List<ValidationIssue> _issues = new();

    /// <summary>Binds <paramref name="table"/> to the placeholders found in <paramref name="doc"/>.</summary>
    public MergeDataSet(LabelDocument doc, MergeTable table, MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
        Options = options ?? new MergeOptions();
        _counters = Options.Counters;
        Placeholders = CollectPlaceholders(doc);
        BuildMapping();
    }

    /// <summary>The bound table.</summary>
    public MergeTable Table { get; }

    /// <summary>The options this set was built with.</summary>
    public MergeOptions Options { get; }

    /// <summary>Distinct placeholder names used by the label (case-insensitive), in order of first appearance.</summary>
    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>
    /// Mapping problems, as message keys with args: <c>Merge.Placeholder.Unmapped</c>(name),
    /// <c>Merge.Column.Unused</c>(column), <c>Merge.Mapping.ColumnNotFound</c>(placeholder, column),
    /// <c>Merge.Counter.Invalid</c>(counter, key), <c>Merge.Copies.ColumnNotFound</c>(column). All are warnings
    /// except an invalid counter, which is an error.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>Placeholder name to zero-based column index for every placeholder that found a column.</summary>
    public IReadOnlyDictionary<string, int> Mapping => _placeholderColumn;

    /// <summary>Number of records (data rows).</summary>
    public int RecordCount => Table.RowCount;

    /// <summary>Copy-count column index or -1.</summary>
    internal int CopiesColumnIndex { get; private set; } = -1;

    /// <summary>Collects the distinct <c>{{name}}</c> placeholders used by text and barcode elements.</summary>
    public static IReadOnlyList<string> CollectPlaceholders(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var e in doc.Elements)
        {
            string? template = e switch
            {
                TextElement t => t.GetTemplate(),
                BarcodeElement b => b.Data,
                _ => null,
            };
            if (template is null) continue;
            foreach (var name in VariableResolver.FindVariables(template))
                if (seen.Add(name)) list.Add(name);
        }
        return list;
    }

    /// <summary>
    /// Variable dictionary for the record at zero-based <paramref name="recordIndex"/>: every column under its own
    /// name, mapped placeholders under their placeholder name, and counters under <c>#Name</c>. Keys are
    /// case-insensitive. A counter that overflows in error mode is omitted (the label then reports it as missing);
    /// <see cref="MergeBatch"/> reports it explicitly.
    /// </summary>
    public Dictionary<string, string> GetRecordData(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= Table.RowCount) throw new ArgumentOutOfRangeException(nameof(recordIndex));
        return BuildData(recordIndex, null);
    }

    internal Dictionary<string, string> BuildData(int recordIndex, List<ValidationIssue>? problems)
    {
        var row = Table.Rows[recordIndex];
        var data = new Dictionary<string, string>(Table.Columns.Count + _counters.Count, StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < Table.Columns.Count; c++) data[Table.Columns[c]] = row[c];
        // Explicit overrides win over a same-named column, which is what the user asked for.
        foreach (var kv in _placeholderColumn) data[kv.Key] = row[kv.Value];
        foreach (var counter in _counters)
        {
            if (counter.Validate() is not null) continue;
            if (counter.TryGetValue(recordIndex, out var v)) data[counter.VirtualColumn] = v;
            else problems?.Add(new ValidationIssue(ValidationSeverity.Error, "Merge.Counter.Overflow", null, counter.Name));
        }
        return data;
    }

    private void BuildMapping()
    {
        var usedColumns = new HashSet<int>();
        foreach (var counter in _counters)
            if (counter.Validate() is { } key)
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, "Merge.Counter.Invalid", null, counter.Name, key));

        foreach (var name in Placeholders)
        {
            if (name.StartsWith('#'))
            {
                bool known = _counters.Any(c => string.Equals(c.VirtualColumn, name, StringComparison.OrdinalIgnoreCase));
                if (!known) _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Placeholder.Unmapped", null, name));
                continue;
            }
            int col;
            if (Options.ColumnOverrides.TryGetValue(name, out var target))
            {
                col = Table.IndexOfColumn(target);
                if (col < 0)
                {
                    _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Mapping.ColumnNotFound", null, name, target));
                    col = Table.IndexOfColumn(name);
                }
            }
            else col = Table.IndexOfColumn(name);

            if (col < 0) _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Placeholder.Unmapped", null, name));
            else { _placeholderColumn[name] = col; usedColumns.Add(col); }
        }

        if (!string.IsNullOrEmpty(Options.CopiesColumn))
        {
            CopiesColumnIndex = Table.IndexOfColumn(Options.CopiesColumn);
            if (CopiesColumnIndex < 0) _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Copies.ColumnNotFound", null, Options.CopiesColumn));
            else usedColumns.Add(CopiesColumnIndex);
        }

        for (int c = 0; c < Table.Columns.Count; c++)
            if (!usedColumns.Contains(c)) _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Column.Unused", null, Table.Columns[c]));
    }
}
