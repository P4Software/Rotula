using System.Globalization;
using System.Text;
using Rotula.Core.Model;
using Rotula.Core.Validation;
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
                var result = ZplGenerator.Generate(doc, values);
                problems.AddRange(result.Issues);
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
            yield return new MergedLabel(number, zpl);
        }
    }

    /// <summary>
    /// Lazily yields jobs of at most <see cref="MergeOptions.MaxRecordsPerJob"/> records each (a single job when
    /// the setting is 0). With <see cref="InvalidRecordPolicy.Stop"/> the job in progress is still yielded, then the
    /// sequence ends; check <see cref="MergeReport.Stopped"/>.
    /// </summary>
    public static IEnumerable<string> Jobs(LabelDocument doc, MergeDataSet data, MergeReport? report = null)
    {
        int max = Math.Max(0, data.Options.MaxRecordsPerJob);
        var sb = new StringBuilder();
        int inJob = 0;
        foreach (var label in Labels(doc, data, report))
        {
            sb.Append(label.Zpl);
            inJob++;
            if (max > 0 && inJob >= max)
            {
                yield return sb.ToString();
                sb.Clear();
                inJob = 0;
            }
        }
        if (inJob > 0) yield return sb.ToString();
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
