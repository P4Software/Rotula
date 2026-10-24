using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Zpl;

namespace Rotula.Printing;

/// <summary>
/// What a print command needs besides the label itself to work out dates, counters, prompts and computed values. Everything is optional:
/// a label without variable definitions ignores all of it.
/// </summary>
public sealed class PrintVariableOptions
{
    /// <summary>Largest number of copies that are written as separate labels (each with its own serial number) in one command.</summary>
    public const int MaxIndividualLabels = 100_000;

    /// <summary>The clock and time zone for date variables; null uses the computer's. Tests pass a fake provider.</summary>
    public TimeProvider? Time { get; set; }

    /// <summary>
    /// Where counters that remember their value keep their position. Null makes such a counter refuse to print
    /// (<see cref="VariableMessageKeys.CounterNoStore"/>) instead of silently starting again and repeating numbers.
    /// </summary>
    public ICounterStore? CounterStore { get; set; }

    /// <summary>The operator's answers to the label's prompts, by variable name (see <see cref="Rotula.Core.Variables.Prompts.PromptSet"/>).</summary>
    public IReadOnlyDictionary<string, string>? PromptAnswers { get; set; }

    /// <summary>Draws the text of Windows fonts for every label (after the variables are replaced). Without it such a text is an error and nothing is sent.</summary>
    public Rotula.Core.Text.ITextRasterizer? TextRasterizer { get; set; }

    /// <summary>
    /// How many labels go into one spooler or network job when every copy is written separately. Smaller means a failure
    /// leaves fewer labels in doubt; larger means fewer jobs. Default 50.
    /// </summary>
    public int LabelsPerJob { get; set; } = 50;
}

/// <summary>One piece of a print run: ZPL for one or more labels that is sent to the printer in one go.</summary>
/// <param name="Zpl">The ZPL text (one or more <c>^XA..^XZ</c> blocks).</param>
/// <param name="Labels">How many labels this piece prints, copies included.</param>
public sealed record PrintBlock(string Zpl, long Labels);

/// <summary>
/// One print command from the first reserved serial number to the last confirmed one. It resolves the label's variables for every label,
/// hands the ZPL out in pieces, and gives back or keeps the reserved serial numbers according to what really reached the printer.
/// </summary>
/// <remarks>
/// <para><b>Copies.</b> A label without counters is one <c>^XA..^XZ</c> with <c>^PQ</c> for the copies, exactly as before. A label with a
/// counter and more than one copy is written as one block per copy, because every copy must carry its own number (<c>^PQ</c> would repeat
/// the same one).</para>
/// <para><b>Reserve then confirm.</b> The numbers of a counter that remembers its value are reserved when the run begins. The caller sends the
/// <see cref="Blocks"/> and then calls <see cref="Complete"/> with the number of labels that may have been printed. A failed send that moved
/// no byte prints nothing, so the caller does not count it and the numbers go back. A send that moved bytes may have printed, so its labels
/// are counted as used: a gap in the series is acceptable, a repeated number on two labels is not.</para>
/// <para>The run does the same for a label without variable definitions, only without any reservation.</para>
/// </remarks>
public sealed class LabelPrintRun
{
    private readonly LabelDocument _doc;
    private readonly IReadOnlyDictionary<string, string>? _data;
    private readonly VariableJob? _job;
    private readonly bool _expanded;
    private readonly int _perJob;
    private readonly List<ValidationIssue> _issues = new();
    private ZplGenerationResult? _first;
    private Rotula.Core.Text.ITextRasterizer? _rasterizer;

    private LabelPrintRun(LabelDocument doc, IReadOnlyDictionary<string, string>? data, VariableJob? job, bool expanded, long labelCount, int perJob)
    {
        _doc = doc;
        _data = data;
        _job = job;
        _expanded = expanded;
        LabelCount = labelCount;
        _perJob = Math.Max(1, perJob);
    }

    /// <summary>Problems and notes found so far: errors stop printing, warnings (a skipped serial range, a printer-clock fallback) should be shown.</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>True when there is no error and the blocks may be sent.</summary>
    public bool CanPrint { get; private set; }

    /// <summary>Number of labels the run prints, copies included.</summary>
    public long LabelCount { get; }

    /// <summary>True when the run stopped before the last label because a later label had an error (see <see cref="Issues"/>).</summary>
    public bool StoppedEarly { get; private set; }

    /// <summary>
    /// Starts a run: resolves the variables, reserves the serial numbers of remembering counters and checks the first label.
    /// Never throws for a problem of the label; look at <see cref="CanPrint"/> and <see cref="Issues"/>.
    /// </summary>
    /// <param name="doc">The label, already converted to the printer resolution when that was needed. Its print quantity is the number of copies.</param>
    /// <param name="data">Values of the data record shown or printed, or null.</param>
    /// <param name="options">Clock, counter store and answers; null uses the defaults.</param>
    public static LabelPrintRun Begin(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null, PrintVariableOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new PrintVariableOptions();

        if (!VariableEngine.HasDefinitions(doc))
        {
            // Plain labels keep the path they always had: one block, the printer repeats it for the copies.
            var plain = new LabelPrintRun(doc, data, null, false, Math.Max(1, doc.Print.Quantity), options.LabelsPerJob);
            plain._first = ZplGenerator.Generate(doc, data, options.TextRasterizer);
            plain._issues.AddRange(plain._first.Issues);
            plain.CanPrint = !plain._first.HasErrors;
            return plain;
        }

        int copies = Math.Max(1, doc.Print.Quantity);
        var counters = doc.Variables.Where(v => v.Kind == VariableKind.Counter).ToList();
        bool expanded = counters.Count > 0 && copies > 1;
        long itemCount = expanded ? copies : 1;

        var time = options.Time ?? TimeProvider.System;
        var context = new VariableContext
        {
            Time = time,
            // One instant for the whole run, so a long run that crosses midnight does not mix two dates.
            Now = time.GetUtcNow(),
            Record = data,
            PromptAnswers = options.PromptAnswers,
            RequirePromptAnswers = true,
            CounterStore = options.CounterStore,
        };
        var job = VariableJob.Begin(doc, context, itemCount);
        var run = new LabelPrintRun(doc, data, job, expanded, copies, options.LabelsPerJob) { _rasterizer = options.TextRasterizer };
        run._issues.AddRange(job.Issues);
        if (expanded && copies > PrintVariableOptions.MaxIndividualLabels)
        {
            // Writing millions of separate labels would never finish; refuse in plain words instead of starting.
            run._issues.Add(new ValidationIssue(ValidationSeverity.Error, "Variable.Copies.TooMany", null, PrintVariableOptions.MaxIndividualLabels.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (run._issues.Any(i => i.Severity == ValidationSeverity.Error))
        {
            // Nothing will be sent, so whatever was reserved goes back.
            foreach (var issue in job.Cancel()) run._issues.Add(issue);
            return run;
        }

        // The first label is generated now so an error in the design is reported before anything is sent.
        run._first = Generate(run, 0);
        foreach (var issue in run._first.Issues) run._issues.Add(issue);
        if (run._first.HasErrors)
        {
            foreach (var issue in job.Cancel()) run._issues.Add(issue);
            return run;
        }
        run.CanPrint = true;
        return run;
    }

    private static ZplGenerationResult Generate(LabelPrintRun run, long index) =>
        // quantity 1: with separate labels every block differs, so the copies are the blocks and not ^PQ.
        ZplGenerator.GenerateWithVariables(run._doc, run._job!.ContextFor(index, run._data), run._expanded ? 1 : null, run._rasterizer);

    /// <summary>The ZPL pieces to send, in order. Enumerate it once, after <see cref="Begin"/> reported <see cref="CanPrint"/>.</summary>
    public IEnumerable<PrintBlock> Blocks()
    {
        if (!CanPrint) yield break;
        if (!_expanded)
        {
            yield return new PrintBlock(_first!.Zpl, LabelCount);
            yield break;
        }

        var sb = new System.Text.StringBuilder();
        long inBlock = 0;
        for (long i = 0; i < LabelCount; i++)
        {
            var result = i == 0 ? _first! : Generate(this, i);
            if (i > 0)
            {
                // Warnings repeat for every label (same message each time), so only a new error matters here.
                var errors = result.Issues.Where(x => x.Severity == ValidationSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    _issues.AddRange(errors);
                    StoppedEarly = true;
                    break;
                }
            }
            sb.Append(result.Zpl);
            inBlock++;
            if (inBlock >= _perJob)
            {
                yield return new PrintBlock(sb.ToString(), inBlock);
                sb.Clear();
                inBlock = 0;
            }
        }
        if (inBlock > 0) yield return new PrintBlock(sb.ToString(), inBlock);
    }

    /// <summary>
    /// The values that fill every label of the run, in order and copies included (<see cref="LabelCount"/> items), for an output that draws the label
    /// instead of sending ZPL, such as label sheets saved as PDF. They are resolved exactly as <see cref="Blocks"/> resolves them, so copy <c>i</c> carries
    /// the serial number the ZPL of copy <c>i</c> would carry; a copy that cannot be resolved ends the sequence (<see cref="StoppedEarly"/>). Confirm
    /// the labels that were written with <see cref="Complete"/>, as after sending blocks. Enumerate either this or <see cref="Blocks"/>, once.
    /// </summary>
    /// <remarks>The dictionaries are already resolved: draw them without resolving the variables again, or a counter would show its preview value.</remarks>
    public IEnumerable<IReadOnlyDictionary<string, string>?> LabelValues()
    {
        if (!CanPrint) yield break;
        if (_job is null)
        {
            // No definitions: every copy is the same record.
            for (long i = 0; i < LabelCount; i++) yield return _data;
            yield break;
        }
        if (!_expanded)
        {
            // No counter (or one copy): the copies are the same label, as ^PQ would print them.
            var values = _job.Resolve(0, _data).Values;
            for (long i = 0; i < LabelCount; i++) yield return values;
            yield break;
        }
        for (long i = 0; i < LabelCount; i++)
        {
            var resolution = _job.Resolve(i, _data);
            var errors = resolution.Issues.Where(x => x.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                _issues.AddRange(errors);
                StoppedEarly = true;
                yield break;
            }
            yield return resolution.Values;
        }
    }

    /// <summary>
    /// Confirms how many labels (counted from the first) may have been printed and releases the serial numbers that were not used.
    /// Safe to call more than once; only the first call counts. Returns problems met while writing the counter file.
    /// </summary>
    /// <param name="labelsPrinted">Labels in the blocks that were sent, including a block whose send failed after some bytes went out.</param>
    public IReadOnlyList<ValidationIssue> Complete(long labelsPrinted) =>
        _job is null ? Array.Empty<ValidationIssue>() : _job.Complete(_expanded ? labelsPrinted : labelsPrinted > 0 ? 1 : 0);

    /// <summary>Nothing reached the printer: gives every reserved serial number back.</summary>
    public IReadOnlyList<ValidationIssue> Cancel() => Complete(0);

    /// <summary>
    /// True when a failed send may have printed something and its labels must therefore be counted as used. A failure that moved bytes, a
    /// time-out and an unknown failure are in doubt; everything else (printer not found, offline, refused, cancelled before sending) printed nothing.
    /// </summary>
    public static bool MayHavePrinted(PrintResult failed) =>
        failed.BytesSent > 0 || failed.Error?.Kind is PrintErrorKind.Timeout or PrintErrorKind.Unknown;
}
