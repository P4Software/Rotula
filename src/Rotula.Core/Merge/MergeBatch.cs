using System.Globalization;
using System.Text;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;

namespace Rotula.Core.Merge;

/// <summary>A problem tied to one record of a batch.</summary>
/// <param name="RecordNumber">1-based data row number (the header row is not counted).</param>
/// <param name="Issue">The structured issue (message key plus args, no UI text).</param>
public sealed record RecordIssue(int RecordNumber, ValidationIssue Issue);

/// <summary>Live statistics and problems of a batch; filled in while the batch enumerates.</summary>
public sealed class MergeReport
{
    private readonly List<RecordIssue> _issues = new();

    /// <summary>Records examined so far.</summary>
    public int Processed { get; internal set; }

    /// <summary>Records that produced a label.</summary>
    public int Emitted { get; internal set; }

    /// <summary>1-based numbers of records left out because of errors.</summary>
    public List<int> SkippedRecords { get; } = new();

    /// <summary>True when the batch ended early because of <see cref="InvalidRecordPolicy.Stop"/>.</summary>
    public bool Stopped { get; internal set; }

    /// <summary>Warnings and errors per record (warnings of emitted records included).</summary>
    public IReadOnlyList<RecordIssue> Issues => _issues;

    /// <summary>True when any record was skipped or the batch stopped.</summary>
    public bool HasErrors => SkippedRecords.Count > 0 || Stopped;

    internal void Add(int record, ValidationIssue issue) => _issues.Add(new RecordIssue(record, issue));
}

/// <summary>One generated label (all copies of one record).</summary>
/// <param name="RecordNumber">1-based data row number.</param>
/// <param name="Zpl">The ZPL text, one or more <c>^XA..^XZ</c> blocks each ending with a line feed.</param>
public sealed record MergedLabel(int RecordNumber, string Zpl);

/// <summary>One print job of a batch: the ZPL of consecutive labels and the records it covers.</summary>
/// <param name="Zpl">The ZPL text of the labels of this job.</param>
/// <param name="LastRecordNumber">1-based number of the last record in the job. After this job was sent, this is the value to confirm a
/// <see cref="VariableJob"/> with, because serial numbers follow the record position.</param>
/// <param name="Labels">How many records produced a label in this job.</param>
public sealed record MergedJob(string Zpl, int LastRecordNumber, int Labels);

/// <summary>
/// Turns a <see cref="MergeDataSet"/> into ZPL. Everything is lazy (<see cref="IEnumerable{T}"/>) so 100k rows
/// never need one giant string unless <see cref="GenerateSingleStream"/> is called. A record that fails
/// validation never throws: it is recorded in the <see cref="MergeReport"/> and skipped or the batch stops,
/// per <see cref="MergeOptions.OnInvalidRecord"/>.
/// </summary>
public static class MergeBatch
{
    private const int MaxCopies = 99999999;

    /// <summary>Lazily yields one <see cref="MergedLabel"/> per valid record, in table order.</summary>
    /// <param name="doc">The label design.</param>
    /// <param name="data">The bound data.</param>
    /// <param name="report">Optional report filled while enumerating.</param>
    public static IEnumerable<MergedLabel> Labels(LabelDocument doc, MergeDataSet data, MergeReport? report = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(data);
        report ??= new MergeReport();
        var options = data.Options;

        // Variables with a definition (dates, counters, prompts, computed values) are resolved per record. A label without any keeps the plain path.
        bool defined = VariableEngine.HasDefinitions(doc);
        VariableJob? job = null;
        bool ownsJob = false; // true: this batch reserved the numbers and confirms them; false: the caller passed MergeOptions.Job and confirms what was sent
        var warned = new HashSet<string>(StringComparer.Ordinal);
        if (defined)
        {
            if (options.Job is { } external) job = external;
            else
            {
                // One instant for the whole batch unless asked otherwise (CreateBatchContext), so a run that crosses midnight does not mix two dates.
                job = VariableJob.Begin(doc, data.CreateBatchContext(), data.RecordCount);
                ownsJob = true;
            }
            foreach (var issue in job.Issues) report.Add(0, issue);
            // A prompt with no answer and no data column of its name can never be satisfied, so say so once instead of failing every record.
            bool unanswered = false;
            foreach (var v in doc.Variables.Where(v => v.Kind == VariableKind.Prompt).DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                if (!options.PromptAnswers.ContainsKey(v.Name) && data.Table.IndexOfColumn(v.Name) < 0)
                {
                    report.Add(0, new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.PromptMissingAnswer, null, v.Name));
                    unanswered = true;
                }
            if (!job.CanPrint || unanswered)
            {
                // Nothing reaches the printer, so the serial numbers this batch reserved are handed back (a job the caller owns is the caller's to cancel).
                if (ownsJob) foreach (var issue in job.Cancel()) report.Add(0, issue);
                report.Stopped = true;
                yield break;
            }
        }

        long usedThrough = 0; // highest record position handed out + 1: those serial numbers count as used, also when a record in between was skipped
        try
        {
            for (int i = 0; i < data.RecordCount; i++)
            {
                int number = i + 1;
                report.Processed++;
                var problems = new List<ValidationIssue>();
                string? zpl = null;
                try
                {
                    var values = data.BuildData(i, problems);
                    int? copies = ResolveCopies(data, i, problems);
                    var result = job is null ? ZplGenerator.Generate(doc, values, options.TextRasterizer) : ZplGenerator.GenerateWithVariables(doc, job.ContextFor(i, values), null, options.TextRasterizer);
                    // Variable warnings are the same for every record; say each of them once.
                    problems.AddRange(result.Issues.Where(p => job is null || p.Severity == ValidationSeverity.Error || warned.Add(p.MessageKey + "|" + string.Join("|", p.Args))));
                    if (!problems.Any(p => p.Severity == ValidationSeverity.Error))
                        zpl = ApplyCopies(result.Zpl, copies, options.CopiesMode);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A defect in one record must never take the whole batch down.
                    problems.Add(new ValidationIssue(ValidationSeverity.Error, "Merge.Record.Failed", null, ex.GetType().Name));
                }

                foreach (var p in problems) report.Add(number, p);
                if (zpl is null)
                {
                    report.SkippedRecords.Add(number);
                    if (options.OnInvalidRecord == InvalidRecordPolicy.Stop)
                    {
                        report.Stopped = true;
                        yield break;
                    }
                    continue;
                }
                report.Emitted++;
                usedThrough = i + 1;
                yield return new MergedLabel(number, zpl);
            }
        }
        finally
        {
            // Runs also when the consumer stops early (print failure, cancel): a label counts as used from the moment it was handed out,
            // which is the safe side - a number is never printed twice, at worst one is skipped. A caller that wants only the labels that were
            // really sent confirmed passes its own job (MergeOptions.Job) and completes it itself.
            if (ownsJob && job is not null) foreach (var issue in job.Complete(usedThrough)) report.Add(0, issue);
        }
    }

    /// <summary>
    /// Lazily yields jobs of at most <see cref="MergeOptions.MaxRecordsPerJob"/> records each (a single job when
    /// the setting is 0). With <see cref="InvalidRecordPolicy.Stop"/> the job in progress is still yielded, then the
    /// sequence ends; check <see cref="MergeReport.Stopped"/>.
    /// </summary>
    public static IEnumerable<string> Jobs(LabelDocument doc, MergeDataSet data, MergeReport? report = null) =>
        JobBlocks(doc, data, report).Select(j => j.Zpl);

    /// <summary>
    /// Like <see cref="Jobs"/>, but each job also says which record it ends at. A caller that owns the serial-number reservation
    /// (<see cref="MergeOptions.Job"/>) confirms with that number after the job was really sent, so a failed send never burns the labels after it.
    /// </summary>
    public static IEnumerable<MergedJob> JobBlocks(LabelDocument doc, MergeDataSet data, MergeReport? report = null)
    {
        int max = Math.Max(0, data.Options.MaxRecordsPerJob);
        var sb = new StringBuilder();
        int inJob = 0, last = 0;
        foreach (var label in Labels(doc, data, report))
        {
            sb.Append(label.Zpl);
            inJob++;
            last = label.RecordNumber;
            if (max > 0 && inJob >= max)
            {
                yield return new MergedJob(sb.ToString(), last, inJob);
                sb.Clear();
                inJob = 0;
            }
        }
        if (inJob > 0) yield return new MergedJob(sb.ToString(), last, inJob);
    }

    /// <summary>Streams every label to <paramref name="writer"/> without building the whole text in memory.</summary>
    public static MergeReport WriteSingleStream(LabelDocument doc, MergeDataSet data, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var report = new MergeReport();
        foreach (var label in Labels(doc, data, report)) writer.Write(label.Zpl);
        return report;
    }

    /// <summary>Builds one string with all labels. Convenient for small sets; prefer <see cref="Jobs"/> or <see cref="WriteSingleStream"/> for large ones.</summary>
    public static string GenerateSingleStream(LabelDocument doc, MergeDataSet data, MergeReport? report = null)
    {
        var sb = new StringBuilder();
        foreach (var label in Labels(doc, data, report)) sb.Append(label.Zpl);
        return sb.ToString();
    }

    private static int? ResolveCopies(MergeDataSet data, int index, List<ValidationIssue> problems)
    {
        if (data.CopiesColumnIndex >= 0)
        {
            string raw = data.Table.Rows[index][data.CopiesColumnIndex].Trim();
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= MaxCopies) return n;
            problems.Add(new ValidationIssue(ValidationSeverity.Error, "Merge.Copies.Invalid", null, raw));
            return null;
        }
        var fixedCopies = data.Options.CopiesPerRecord;
        if (fixedCopies is { } f && (f < 1 || f > MaxCopies))
        {
            problems.Add(new ValidationIssue(ValidationSeverity.Error, "Merge.Copies.Invalid", null, f.ToString(CultureInfo.InvariantCulture)));
            return null;
        }
        return fixedCopies;
    }

    /// <summary>
    /// Rewrites the generator's trailing <c>^PQ</c> line. The generator always ends with "\n^PQ...\n^XZ\n", so the
    /// last occurrence is ours; imported raw ZPL elsewhere in the block can never be hit.
    /// </summary>
    private static string ApplyCopies(string zpl, int? copies, CopiesMode mode)
    {
        if (copies is null) return zpl;
        int pq = zpl.LastIndexOf("\n^PQ", StringComparison.Ordinal);
        if (pq < 0) return zpl;
        int lineEnd = zpl.IndexOf('\n', pq + 1);
        if (lineEnd < 0) return zpl;
        string head = zpl[..(pq + 1)];
        string tail = zpl[(lineEnd + 1)..];
        if (mode == CopiesMode.RepeatBlock)
        {
            string block = head + "^PQ1,0,1,N\n" + tail;
            var sb = new StringBuilder(block.Length * copies.Value);
            for (int i = 0; i < copies.Value; i++) sb.Append(block);
            return sb.ToString();
        }
        return head + "^PQ" + copies.Value.ToString(CultureInfo.InvariantCulture) + ",0,1,N\n" + tail;
    }
}
