using Rotula.Core.Variables.Counters;

namespace Rotula.Core.Merge;

/// <summary>When the date and time of date variables are read during a batch.</summary>
public enum DateEvaluation
{
    /// <summary>Once, when the batch starts: every label carries the same date even if the run crosses midnight (default).</summary>
    AtBatchStart,
    /// <summary>For every label, so a long run shows the time that label was generated.</summary>
    PerLabel,
}

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

    /// <summary>The clock and time zone used by date variables; null means the computer's. Tests pass a fake provider.</summary>
    public TimeProvider? Time { get; set; }

    /// <summary>The operator's answers to the label's prompts (variable name to answer), the same for every record. See <see cref="Rotula.Core.Variables.Prompts.PromptSet"/>.</summary>
    public Dictionary<string, string> PromptAnswers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Draws the text of Windows fonts for every record (after the record's values are in the text). Null makes such a text an error, so the
    /// record is skipped or the batch stops according to <see cref="OnInvalidRecord"/>. It must be safe to call from a background thread.
    /// </summary>
    public Rotula.Core.Text.ITextRasterizer? TextRasterizer { get; set; }

    /// <summary>Where counters that remember their value keep their position; null makes them count from their start.</summary>
    public ICounterStore? CounterStore { get; set; }

    /// <summary>Whether date variables are read once per batch (default) or for every label.</summary>
    public DateEvaluation DateEvaluation { get; set; } = DateEvaluation.AtBatchStart;

    /// <summary>
    /// A print run the caller owns. Leave it null and the batch reserves the remembered serial numbers itself and confirms them when the
    /// enumeration ends, which counts every label as used from the moment it is handed out (a failed send afterwards burns those numbers, never
    /// reuses them). Set it to a job started with <c>VariableJob.Begin(doc, set.CreateBatchContext(), set.RecordCount)</c> to confirm only
    /// what was really sent: the batch then reserves nothing and never completes the job, and after sending the caller calls
    /// <c>job.Complete(n)</c> with the record number of the last label that reached the printer (or <c>Cancel()</c> if none did).
    /// </summary>
    public Rotula.Core.Variables.Counters.VariableJob? Job { get; set; }

    /// <summary>
    /// Maximum number of records (data rows that produced a label) per job when producing jobs; 0 means a single
    /// job. Counted in records, not copies, so a ^PQ of 50 still counts as one.
    /// </summary>
    public int MaxRecordsPerJob { get; set; }
}
