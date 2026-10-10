namespace Rotula.Core.Merge;

/// <summary>How the number of copies per record is expressed in the ZPL.</summary>
public enum CopiesMode
{
    /// <summary>One <c>^XA..^XZ</c> per record with <c>^PQ</c> set to the copy count (smallest output, fastest printing).</summary>
    PrintQuantity,
    /// <summary>The label block is repeated once per copy with <c>^PQ1</c> (needed when a printer or workflow ignores ^PQ).</summary>
    RepeatBlock,
}

/// <summary>What to do with a record whose label has validation errors (for example an invalid barcode).</summary>
public enum InvalidRecordPolicy
{
    /// <summary>Report the record, leave it out of the output and continue with the next one.</summary>
    Skip,
    /// <summary>Report the record and end the batch there; <see cref="MergeReport.Stopped"/> becomes true.</summary>
    Stop,
}

/// <summary>Settings for building a <see cref="MergeDataSet"/> and running a <see cref="MergeBatch"/>.</summary>
public sealed class MergeOptions
{
    /// <summary>Serial counters, each available to labels as <c>{{#Name}}</c>.</summary>
    public List<SerialCounter> Counters { get; } = new();

    /// <summary>
    /// Explicit placeholder-to-column choices (keys are placeholder names, values column names; both
    /// case-insensitive). Placeholders not listed here map to the column with the same name.
    /// </summary>
    public Dictionary<string, string> ColumnOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies of every record, or null to keep the <c>^PQ</c> of the label document untouched.
    /// Ignored for records when <see cref="CopiesColumn"/> supplies a value.
    /// </summary>
    public int? CopiesPerRecord { get; set; }

    /// <summary>Optional column holding a per-record copy count (1-99999999); overrides <see cref="CopiesPerRecord"/>.</summary>
    public string? CopiesColumn { get; set; }

    /// <summary>How copies are written; <c>^PQ</c> by default.</summary>
    public CopiesMode CopiesMode { get; set; } = CopiesMode.PrintQuantity;

    /// <summary>Handling of records with validation errors; skip by default.</summary>
    public InvalidRecordPolicy OnInvalidRecord { get; set; } = InvalidRecordPolicy.Skip;

    /// <summary>
    /// Maximum number of records (data rows that produced a label) per job when producing jobs; 0 means a single
    /// job. Counted in records, not copies, so a ^PQ of 50 still counts as one.
    /// </summary>
    public int MaxRecordsPerJob { get; set; }
}
