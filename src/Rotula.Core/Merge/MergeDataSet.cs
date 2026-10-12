using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Expressions;
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
    private readonly LabelDocument _doc;
    // Names the label defines itself (date, counter, prompt, computed). They need no data column, so they are never reported as unmapped.
    private readonly HashSet<string> _definedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Binds <paramref name="table"/> to the placeholders found in <paramref name="doc"/>.</summary>
    public MergeDataSet(LabelDocument doc, MergeTable table, MergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(table);
        _doc = doc;
        Table = table;
        Options = options ?? new MergeOptions();
        foreach (var v in doc.Variables) if (v.Kind != VariableKind.Text) _definedNames.Add(v.Name);
        _counters = Options.Counters;
        Placeholders = CollectPlaceholders(doc);
        BuildMapping();
        _selected = NormalizeSelection(Options.SelectedRecords, table.RowCount);
    }

    // Rows to merge (null = all). Copied from the options so later edits of the caller's list cannot change a running batch.
    private readonly int[]? _selected;
    private bool _selectedAscending = true;
    private Dictionary<int, int>? _positions; // only built for a selection that is not in ascending order

    private int[]? NormalizeSelection(IReadOnlyList<int>? requested, int rowCount)
    {
        if (requested is null) return null;
        var copy = new int[requested.Count];
        var seen = new HashSet<int>();
        for (int i = 0; i < copy.Length; i++)
        {
            int row = requested[i];
            if (row < 0 || row >= rowCount) throw new ArgumentOutOfRangeException(nameof(MergeOptions.SelectedRecords), "Selected row " + row + " does not exist.");
            if (!seen.Add(row)) throw new ArgumentException("Selected row " + row + " is listed twice.", nameof(MergeOptions.SelectedRecords));
            if (i > 0 && row < copy[i - 1]) _selectedAscending = false;
            copy[i] = row;
        }
        return copy;
    }

    /// <summary>
    /// How many records a batch processes: the selected rows (<see cref="MergeOptions.SelectedRecords"/>) or every row. This is the item count
    /// to start a <c>VariableJob</c> with, because serial numbers are reserved per processed record.
    /// </summary>
    public int BatchCount => _selected?.Length ?? Table.RowCount;

    /// <summary>The zero-based row at batch position <paramref name="position"/> (0 to <see cref="BatchCount"/> - 1).</summary>
    public int RecordAt(int position)
    {
        if (position < 0 || position >= BatchCount) throw new ArgumentOutOfRangeException(nameof(position));
        return _selected is null ? position : _selected[position];
    }

    /// <summary>
    /// The batch position of <paramref name="recordIndex"/>, which is what its serial number follows. A row outside the selection gets the
    /// position it would take if it were added in data order (so a preview of it shows a plausible next number).
    /// </summary>
    public int PositionOf(int recordIndex)
    {
        if (_selected is null) return recordIndex;
        if (_selectedAscending)
        {
            int at = Array.BinarySearch(_selected, recordIndex);
            return at >= 0 ? at : ~at;
        }
        if (_positions is null)
        {
            _positions = new Dictionary<int, int>(_selected.Length);
            for (int i = 0; i < _selected.Length; i++) _positions[_selected[i]] = i;
        }
        return _positions.TryGetValue(recordIndex, out int p) ? p : _selected.Length;
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
            // A print condition reads a field too: listing it lets it be mapped to a data column and keeps that column from being reported as unused.
            if (e.PrintCondition is { } condition && PrintCondition.NormalizeField(condition.Field) is { Length: > 0 } conditionField
                && VariableResolver.FindVariables("{{" + conditionField + "}}").Count == 1 && seen.Add(conditionField))
                list.Add(conditionField);
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
    /// name, mapped placeholders under their placeholder name, and counters under <c>#Name</c>. When the label defines
    /// variables (dates, counters, prompts, computed values) their values are included as the preview would show them:
    /// the clock is read now, counters show the next remembered position without reserving it, prompts show their defaults
    /// (or the answers in <see cref="MergeOptions.PromptAnswers"/>). Keys are case-insensitive. A counter that overflows in error mode is omitted (the label then reports it as missing);
    /// <see cref="MergeBatch"/> reports it explicitly.
    /// </summary>
    public Dictionary<string, string> GetRecordData(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= Table.RowCount) throw new ArgumentOutOfRangeException(nameof(recordIndex));
        var raw = BuildData(recordIndex, null);
        if (!VariableEngine.HasDefinitions(_doc)) return raw;
        return new Dictionary<string, string>(VariableEngine.Resolve(_doc, CreateContext(PositionOf(recordIndex), raw, null)).Values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The base context of a whole batch: the options' clock, prompt answers (required: a question nobody answered is an error) and counter store,
    /// with the date fixed at the current instant unless <see cref="MergeOptions.DateEvaluation"/> asks for one reading per label. Pass it to
    /// <c>VariableJob.Begin</c> to own the run (see <see cref="MergeOptions.Job"/>).
    /// </summary>
    public VariableContext CreateBatchContext()
    {
        DateTimeOffset? batchNow = Options.DateEvaluation == DateEvaluation.AtBatchStart ? (Options.Time ?? TimeProvider.System).GetUtcNow() : null;
        var context = CreateContext(0, new Dictionary<string, string>(), batchNow);
        context.RequirePromptAnswers = true;
        return context;
    }

    /// <summary>
    /// The questions the operator must answer before this batch can print: the label's prompt variables whose name is not a data column
    /// (a prompt with a column is answered by the data, unless it is set to override the record).
    /// </summary>
    /// <param name="memory">Remembered last answers, or null.</param>
    public Rotula.Core.Variables.Prompts.PromptSet BuildPromptSet(Rotula.Core.Variables.Prompts.IPromptMemory? memory = null)
    {
        // Any non-empty value marks the column as present; PromptSet only looks at whether the record has a value for the name.
        var columns = Table.Columns.Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(c => c, _ => "x", StringComparer.OrdinalIgnoreCase);
        return Rotula.Core.Variables.Prompts.PromptSet.Build(_doc, columns, memory);
    }

    /// <summary>
    /// The variable context for the record at <paramref name="recordIndex"/>: its raw data (columns and merge counters), the options' prompt answers,
    /// counter store and clock. Pass it to <see cref="ZplGenerator.GenerateWithVariables"/> or <see cref="VariableEngine.Resolve"/> to get exactly what
    /// a batch would produce for that record (counters show their next position; nothing is reserved).
    /// </summary>
    public VariableContext CreateRecordContext(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= Table.RowCount) throw new ArgumentOutOfRangeException(nameof(recordIndex));
        return CreateContext(PositionOf(recordIndex), BuildData(recordIndex, null), null);
    }

    /// <summary>
    /// The variable context for the label at batch position <paramref name="position"/>: its raw data, the options' prompt answers, counter store and clock.
    /// <paramref name="now"/> fixes the instant (batch start), or null to read the clock.
    /// </summary>
    internal VariableContext CreateContext(int position, Dictionary<string, string> raw, DateTimeOffset? now) => new()
    {
        Time = Options.Time ?? TimeProvider.System,
        Now = now,
        Record = raw,
        PromptAnswers = Options.PromptAnswers.Count == 0 ? null : Options.PromptAnswers,
        CounterStore = Options.CounterStore,
        ItemIndex = position,
    };

    /// <param name="recordIndex">Zero-based row to read.</param>
    /// <param name="problems">Receives counter overflows, or null.</param>
    /// <param name="position">Batch position that merge counters number by; null derives it from the selection.</param>
    internal Dictionary<string, string> BuildData(int recordIndex, List<ValidationIssue>? problems, int? position = null)
    {
        int counterPosition = position ?? PositionOf(recordIndex);
        var row = Table.Rows[recordIndex];
        var data = new Dictionary<string, string>(Table.Columns.Count + _counters.Count, StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < Table.Columns.Count; c++) data[Table.Columns[c]] = row[c];
        // Explicit overrides win over a same-named column, which is what the user asked for.
        foreach (var kv in _placeholderColumn) data[kv.Key] = row[kv.Value];
        foreach (var counter in _counters)
        {
            if (counter.Validate() is not null) continue;
            if (counter.TryGetValue(counterPosition, out var v)) data[counter.VirtualColumn] = v;
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
            if (_definedNames.Contains(name) && Table.IndexOfColumn(name) < 0 && !Options.ColumnOverrides.ContainsKey(name)) continue;
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

        // A column read only by a computed variable's expression is used too.
        foreach (var v in _doc.Variables.Where(v => v.Kind == VariableKind.Computed))
            if (ExpressionParser.Parse(v.Computed?.Expression, out _) is { } tree)
                foreach (var reference in tree.References)
                {
                    int refColumn = Table.IndexOfColumn(reference);
                    if (refColumn >= 0) usedColumns.Add(refColumn);
                }

        for (int c = 0; c < Table.Columns.Count; c++)
            if (!usedColumns.Contains(c)) _issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Merge.Column.Unused", null, Table.Columns[c]));
    }
}
