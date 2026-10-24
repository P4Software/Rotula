using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Core.Variables.Prompts;
using Rotula.Files;
using Rotula.Printing;

namespace Rotula.Engine;

/// <summary>Limits a host puts on requests. The command line uses <see cref="Default"/>; the REST server reads its own from the settings file.</summary>
/// <param name="MaxCopies">Most copies per label or record.</param>
/// <param name="MaxRecords">Most records per request.</param>
public sealed record EngineLimits(int MaxCopies, int MaxRecords)
{
    /// <summary>The Print dialog's copies limit and a generous record limit.</summary>
    public static EngineLimits Default { get; } = new(99_999_999, 1_000_000);
}

/// <summary>
/// The headless print engine: one label file, optional data, one printer, no UI. It does what the app's Print dialog and data merge do
/// (resolution conversion, printer set-up, variables, serial numbers reserved first and confirmed only for labels that reached the printer,
/// the printer status check before every job and the verification after the last) and reports the outcome as a value, never an exception.
/// </summary>
/// <remarks>
/// Jobs for the same printer run one after the other (a per-printer queue), so two callers never interleave their labels; jobs for different
/// printers run at the same time. Serial numbers that remember their value use the same counter file as the app, which locks it per write.
/// </remarks>
public sealed class PrintEngine
{
    private readonly IPrintService _print;
    private readonly ICounterStore? _counters;
    private readonly Rotula.Core.Text.ITextRasterizer? _rasterizer;
    private readonly TimeProvider? _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the engine.</summary>
    /// <param name="print">The print service (the real one, or a fake in tests).</param>
    /// <param name="counters">Where remembering counters keep their position; null makes such a label refuse to print instead of repeating numbers.</param>
    /// <param name="rasterizer">Draws text in Windows fonts; null makes such a text an error.</param>
    /// <param name="time">Clock for date variables; null uses the computer's.</param>
    public PrintEngine(IPrintService print, ICounterStore? counters = null, Rotula.Core.Text.ITextRasterizer? rasterizer = null, TimeProvider? time = null)
    {
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _counters = counters;
        _rasterizer = rasterizer;
        _time = time;
    }

    /// <summary>Lists the installed Windows printers. Never throws for a spooler problem; an unreadable list is empty.</summary>
    public async Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken ct = default)
    {
        try { return await _print.GetPrintersAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<PrinterInfo>(); }
    }

    /// <summary>
    /// Builds the printer target of a request: exactly one of a Windows printer and a host, a port from 1 to 65535. Returns the key of the
    /// problem, or null.
    /// </summary>
    public static string? TryBuildTarget(string? printer, string? host, int? port, out PrinterTarget? target)
    {
        target = null;
        bool hasPrinter = !string.IsNullOrWhiteSpace(printer), hasHost = !string.IsNullOrWhiteSpace(host);
        if (!hasPrinter && !hasHost) return "Engine.Error.PrinterRequired";
        if (hasPrinter && hasHost) return "Engine.Error.PrinterBoth";
        if (hasPrinter)
        {
            target = PrinterTarget.Spooler(printer!.Trim());
            return null;
        }
        if (port is { } p && (p < 1 || p > 65535)) return "Print.Field.Port";
        target = PrinterTarget.TcpIp(host!.Trim(), port ?? PrinterTarget.DefaultTcpPort);
        return null;
    }

    /// <summary>Asks a printer for its status (~HS) and describes it. Waits for a job running on the same printer to finish first.</summary>
    public async Task<PrinterStatusReport> GetStatusAsync(PrinterTarget target, EngineText text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);
        var queue = Queue(target);
        await queue.WaitAsync(ct).ConfigureAwait(false);
        PrinterStatus status;
        try { status = await _print.GetStatusAsync(target.WithStatus(PrinterStatusOptions.Default), ct).ConfigureAwait(false); }
        finally { queue.Release(); }
        return Report(target.DisplayName, status, text);
    }

    /// <summary>Turns a decoded status into a report with condition codes and a sentence in the caller's language.</summary>
    public static PrinterStatusReport Report(string printer, PrinterStatus status, EngineText text)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(text);
        if (!status.IsAvailable)
            return new PrinterStatusReport(printer, false, null, [], [], null, null, false, status.Reason.ToString(), text.Describe(status));
        return new PrinterStatusReport(printer, true, !status.IsBlocked, status.Blocking.Select(c => c.ToString()).ToList(),
            status.Warnings.Select(c => c.ToString()).ToList(), status.LabelsRemaining, status.FormatsInBuffer, status.IsComplete, null, text.Describe(status));
    }

    /// <summary>
    /// Prints one request. <paramref name="label"/> is the loaded .rotula file; <paramref name="table"/> carries records read from a CSV or Excel
    /// file (the command line), otherwise <see cref="PrintJobRequest.Records"/> or <see cref="PrintJobRequest.Values"/> are used.
    /// </summary>
    public async Task<PrintJobResult> PrintAsync(RotulaFileContent label, PrintJobRequest request, MergeTable? table, EngineText text,
        EngineLimits? limits = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(text);
        limits ??= EngineLimits.Default;
        var job = new JobState(request, text);

        // ---- the request ------------------------------------------------------------------------------------------------
        if (TryBuildTarget(request.Printer, request.Host, request.Port, out var baseTarget) is { } targetError)
            return job.Fail(PrintOutcome.Invalid, targetError);
        job.Printer = baseTarget!.DisplayName;
        if (request.Copies is { } c && (c < 1 || c > limits.MaxCopies))
            return job.Fail(PrintOutcome.Invalid, "Engine.Error.Copies", limits.MaxCopies);
        if (request.Values is not null && (request.Records is not null || table is not null))
            return job.Fail(PrintOutcome.Invalid, "Engine.Error.ValuesAndRecords");
        PrinterDpi? dpi = request.Dpi switch
        {
            null => null,
            203 => PrinterDpi.Dpi203,
            300 => PrinterDpi.Dpi300,
            600 => PrinterDpi.Dpi600,
            _ => (PrinterDpi)(-1),
        };
        if (dpi is (PrinterDpi)(-1)) return job.Fail(PrintOutcome.Invalid, "Engine.Error.Dpi");
        table ??= request.Records is { } records ? RecordsTable(records) : null;
        if (table is not null)
        {
            if (table.RowCount == 0) return job.Fail(PrintOutcome.Invalid, "Engine.Error.NoRecords");
            if (table.RowCount > limits.MaxRecords) return job.Fail(PrintOutcome.Invalid, "Engine.Error.Records", limits.MaxRecords);
        }

        // ---- the label, matched to the printer --------------------------------------------------------------------------
        var doc = label.Document;
        if (dpi is null && baseTarget.Transport == PrinterTransport.Spooler)
        {
            var caps = await _print.GetPrinterInfoAsync(baseTarget, ct).ConfigureAwait(false);
            dpi = caps.Dpi;
        }
        if (dpi is { } printerDpi && LabelDpiConverter.NeedsConversion(doc, printerDpi))
        {
            var converted = LabelDpiConverter.Convert(doc, printerDpi);
            foreach (var issue in converted.Issues)
                job.Notices.Add(new EngineNotice(issue.MessageKey, "warning", text.Has("Issue." + issue.MessageKey) ? text.Describe(issue) : text.Get("Print.DpiIssue.Generic")));
            doc = converted.Document;
        }

        PrinterProfile? profile = null;
        if (request.Darkness is not null || request.Speed is not null)
        {
            profile = new PrinterProfile
            {
                Dpi = doc.Dpi, Width = doc.Width, Height = doc.Height, MediaTracking = doc.MediaTracking, Encoding = doc.Encoding,
                Darkness = request.Darkness, SpeedIps = request.Speed,
            };
            var bad = profile.Validate();
            if (bad.Count > 0) return job.Fail(PrintOutcome.Invalid, "Print.Error.InvalidProfile", bad.Select(k => (object)text.Get(k)).ToArray());
        }

        var status = request.CheckStatus || request.Verify ? new PrinterStatusOptions(request.CheckStatus, request.Verify) : PrinterStatusOptions.Off;
        var target = baseTarget.WithStatus(status);

        var queue = Queue(target);
        await queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return table is null
                ? await PrintOneAsync(job, doc, target, profile, ct).ConfigureAwait(false)
                : await PrintRecordsAsync(job, doc, label.Merge, table, target, profile, ct).ConfigureAwait(false);
        }
        finally
        {
            queue.Release();
        }
    }

    // ---- one label ------------------------------------------------------------------------------------------------------

    private async Task<PrintJobResult> PrintOneAsync(JobState job, LabelDocument doc, PrinterTarget target, PrinterProfile? profile, CancellationToken ct)
    {
        var request = job.Request;
        if (request.Copies is { } copies) doc.Print.Quantity = copies;
        IReadOnlyDictionary<string, string>? data = request.Values is null ? null : new Dictionary<string, string>(request.Values, StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string>? answers = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            var prompts = PromptSet.Build(doc, data);
            if (!prompts.IsEmpty && CheckAnswers(job, doc, prompts, out answers) is { } refused) return refused;
        }

        var options = new PrintVariableOptions { CounterStore = _counters, PromptAnswers = answers, TextRasterizer = _rasterizer, Time = _time };
        if (request.DryRun)
        {
            var run = LabelPrintRun.Begin(doc, data, options);
            var sb = new StringBuilder();
            try
            {
                if (run.CanPrint) foreach (var block in run.Blocks()) sb.Append(block.Zpl);
            }
            finally
            {
                // A dry run prints nothing, so every reserved serial number goes back.
                run.Cancel();
            }
            job.AddIssues(run.Issues);
            if (!run.CanPrint || run.StoppedEarly) return job.Fail(PrintOutcome.InvalidLabel, "Print.Error.InvalidLabel");
            job.Records = 1;
            job.Labels = run.LabelCount;
            string setup = profile is null ? string.Empty : PrinterSetup.BuildSetupZpl(profile);
            return job.Finish(PrintOutcome.DryRun, job.Text.Get("Engine.Result.DryRun", job.Printer), setup + sb);
        }

        PrintResult result;
        try { result = await _print.PrintAsync(target, doc, data, profile, options, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = PrintResult.Fail(PrintErrorKind.Cancelled); }
        job.AddIssues(result.Notices);
        job.BytesSent = result.BytesSent;
        if (!result.Succeeded) return job.FromError(result.Error!);
        job.Records = 1;
        job.Labels = Math.Max(1, doc.Print.Quantity);
        job.Jobs = 1;
        return job.Finish(PrintOutcome.Printed, job.Text.Get("Engine.Result.Printed", job.Labels, job.Printer));
    }

    // ---- several records ------------------------------------------------------------------------------------------------

    private async Task<PrintJobResult> PrintRecordsAsync(JobState job, LabelDocument doc, DataMergeSettings? merge, MergeTable table, PrinterTarget target,
        PrinterProfile? profile, CancellationToken ct)
    {
        var request = job.Request;
        var options = MergeOptionsFor(merge);
        if (request.Copies is { } copies)
        {
            options.CopiesPerRecord = copies;
            options.CopiesColumn = null;
        }
        options.CounterStore = _counters;
        options.TextRasterizer = _rasterizer;
        options.Time = _time;

        MergeDataSet ds;
        try { ds = new MergeDataSet(doc, table, options); }
        catch (ArgumentException ex) { return job.Fail(PrintOutcome.Invalid, "Engine.Error.DataFile", "-", ex.Message); }
        job.AddIssues(table.Issues);
        job.AddIssues(ds.Issues);
        if (ds.Issues.Any(i => i.Severity == ValidationSeverity.Error)) return job.Fail(PrintOutcome.InvalidLabel, "Print.Error.InvalidLabel");

        var prompts = ds.BuildPromptSet();
        if (!prompts.IsEmpty)
        {
            if (CheckAnswers(job, doc, prompts, out var answers) is { } refused) return refused;
            foreach (var kv in answers!) ds.Options.PromptAnswers[kv.Key] = kv.Value;
        }

        // The run owns the serial-number reservation, so only labels that really reached the printer are confirmed (as in the app's batch print).
        VariableJob? serials = null;
        if (VariableEngine.HasDefinitions(doc))
        {
            serials = VariableJob.Begin(doc, ds.CreateBatchContext(), ds.BatchCount);
            ds.Options.Job = serials;
            job.AddIssues(serials.Issues);
            if (serials.Issues.Any(i => i.Severity == ValidationSeverity.Error))
            {
                job.AddIssues(serials.Cancel());
                return job.Fail(PrintOutcome.InvalidLabel, "Print.Error.InvalidLabel");
            }
        }

        var report = new MergeReport();
        string setup = profile is null ? string.Empty : PrinterSetup.BuildSetupZpl(profile);
        var rawBlocks = ByteExactBlocks.From(doc);
        var jobTarget = target.WithStatus(target.Status with { VerifyAfterPrinting = false });
        int confirmedThrough = 0, inFlight = 0;
        PrintError? failure = null;
        var zpl = request.DryRun ? new StringBuilder(setup) : null;
        try
        {
            foreach (var block in MergeBatch.JobBlocks(doc, ds, report))
            {
                ct.ThrowIfCancellationRequested();
                if (zpl is not null)
                {
                    zpl.Append(block.Zpl);
                    job.Jobs++;
                    continue;
                }
                inFlight = block.ItemsThrough;
                var result = await _print.PrintZplAsync(jobTarget, job.Jobs == 0 ? setup + block.Zpl : block.Zpl, doc.Encoding, rawBlocks, ct).ConfigureAwait(false);
                job.AddIssues(result.Notices);
                job.BytesSent += result.BytesSent;
                if (!result.Succeeded)
                {
                    failure = result.Error;
                    // A send that moved bytes may have printed labels: their numbers stay used. One that moved nothing gives them back.
                    if (LabelPrintRun.MayHavePrinted(result)) confirmedThrough = inFlight;
                    break;
                }
                confirmedThrough = inFlight;
                inFlight = 0;
                job.Jobs++;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled while a job was on its way: it may have printed, so its numbers are not given back.
            if (inFlight > 0) confirmedThrough = inFlight;
            failure = PrintError.Create(PrintErrorKind.Cancelled);
        }
        finally
        {
            // Always confirms (a dry run confirms nothing), so a reservation is never left hanging.
            if (serials is not null) job.AddIssues(serials.Complete(zpl is null ? confirmedThrough : 0));
        }

        foreach (var ri in report.Issues) job.Notices.Add(job.Notice(ri.Issue, ri.RecordNumber));
        job.Records = report.Emitted;
        job.Labels = CountLabels(ds, doc, report);

        if (failure is not null) return job.FromError(failure, partial: job.Jobs);
        if (report.Emitted == 0) return job.Fail(PrintOutcome.InvalidLabel, "Engine.Error.NothingPrinted");
        if (zpl is not null) return job.Finish(PrintOutcome.DryRun, job.Text.Get("Engine.Result.DryRun", job.Printer), zpl.ToString());

        if (job.Jobs > 0 && target.Status.VerifyAfterPrinting)
            job.AddIssues(await _print.VerifyPrintedAsync(target, ct).ConfigureAwait(false));
        string message = job.Text.Get("Engine.Result.PrintedRecords", report.Emitted, job.Labels, job.Printer, job.Jobs);
        if (report.SkippedRecords.Count > 0) message += " " + job.Text.Get("Data.Batch.Skipped", report.SkippedRecords.Count);
        if (report.Stopped) message += " " + job.Text.Get("Data.Batch.Stopped", report.SkippedRecords[^1]);
        return job.Finish(PrintOutcome.Printed, message);
    }

    /// <summary>Labels the batch produced, copies included: the copies column or setting per record, or the label's own quantity.</summary>
    private static long CountLabels(MergeDataSet ds, LabelDocument doc, MergeReport report)
    {
        if (ds.Options.CopiesColumn is null) return (long)report.Emitted * Math.Max(1, ds.Options.CopiesPerRecord ?? doc.Print.Quantity);
        // With a copies column every record can differ; the batch already worked them out per label.
        long total = 0;
        int column = ds.Table.IndexOfColumn(ds.Options.CopiesColumn);
        for (int i = 0; i < ds.BatchCount && column >= 0; i++)
        {
            if (report.SkippedRecords.Contains(ds.RecordAt(i) + 1)) continue;
            total += int.TryParse(ds.Table.Rows[ds.RecordAt(i)][column], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;
        }
        return Math.Max(total, report.Emitted);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Checks the answers to the label's questions. A question nobody answered takes the default the designer gave it (never the sample value);
    /// a required question without answer or default refuses the job and lists every question.
    /// </summary>
    private static PrintJobResult? CheckAnswers(JobState job, LabelDocument doc, PromptSet prompts, out IReadOnlyDictionary<string, string>? answers)
    {
        var given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in prompts.Items)
        {
            var defined = doc.Variables.FirstOrDefault(v => string.Equals(v.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (defined?.Prompt?.Default is { Length: > 0 } fallback) given[item.Name] = fallback;
        }
        if (job.Request.Answers is { } supplied)
            foreach (var kv in supplied) given[kv.Key] = kv.Value;
        var validation = prompts.Validate(given);
        answers = validation.Values;
        if (validation.IsValid) return null;
        job.AddIssues(validation.Issues);
        string list = string.Join("; ", prompts.Items.Select(i => i.Required ? i.Name + " (" + i.Label + ", *)" : i.Name + " (" + i.Label + ")"));
        return job.Fail(PrintOutcome.InvalidLabel, "Engine.Error.Answers", list);
    }

    /// <summary>The merge settings saved with the label (counters, column choices, copies, invalid-record handling, job size).</summary>
    internal static MergeOptions MergeOptionsFor(DataMergeSettings? merge)
    {
        var o = new MergeOptions();
        if (merge is null) return o;
        foreach (var c in merge.Counters)
        {
            o.Counters.Add(new SerialCounter
            {
                Name = c.Name, Kind = c.Kind, Start = c.Start, Step = c.Step, Padding = c.Padding, Prefix = c.Prefix, Suffix = c.Suffix, Overflow = c.Overflow,
            });
        }
        foreach (var kv in merge.ColumnOverrides) o.ColumnOverrides[kv.Key] = kv.Value;
        o.CopiesPerRecord = merge.CopiesPerRecord;
        o.CopiesColumn = merge.CopiesColumn;
        o.CopiesMode = merge.CopiesMode;
        o.OnInvalidRecord = merge.OnInvalidRecord;
        o.MaxRecordsPerJob = merge.MaxRecordsPerJob;
        o.DateEvaluation = merge.DateEvaluation;
        return o;
    }

    /// <summary>Turns JSON records into a table: the columns are every field name in the order first seen; a missing field is empty.</summary>
    public static MergeTable RecordsTable(IReadOnlyList<Dictionary<string, string>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
            foreach (var key in record.Keys)
                if (seen.Add(key)) columns.Add(key);
        var table = new MergeTable(columns);
        foreach (var record in records)
        {
            var lookup = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase);
            table.AddRow(columns.Select(c => lookup.TryGetValue(c, out var v) ? v ?? string.Empty : string.Empty).ToArray());
        }
        return table;
    }

    private SemaphoreSlim Queue(PrinterTarget target) => _queues.GetOrAdd(target.DisplayName, _ => new SemaphoreSlim(1, 1));

    /// <summary>What a request collected on its way, turned into the result at the end.</summary>
    private sealed class JobState(PrintJobRequest request, EngineText text)
    {
        public PrintJobRequest Request { get; } = request;
        public EngineText Text { get; } = text;
        public string Printer { get; set; } = string.Empty;
        public int Records { get; set; }
        public long Labels { get; set; }
        public int Jobs { get; set; }
        public long BytesSent { get; set; }
        public List<EngineNotice> Notices { get; } = new();

        public EngineNotice Notice(ValidationIssue issue, int? record = null) =>
            new(issue.MessageKey, issue.Severity == ValidationSeverity.Error ? "error" : "warning", Text.Describe(issue), record);

        public void AddIssues(IEnumerable<ValidationIssue> issues)
        {
            foreach (var issue in issues)
            {
                var notice = Notice(issue);
                // The same note can come from several places (the status check before every job); it is listed once.
                if (!Notices.Any(n => n.Code == notice.Code && n.Message == notice.Message && n.Record is null)) Notices.Add(notice);
            }
        }

        public PrintJobResult Fail(PrintOutcome outcome, string key, params object[] args) =>
            new(outcome, Text.Get(key, args), key, Printer, Records, Labels, Jobs, BytesSent, Notices, null, Request.Reference);

        public PrintJobResult FromError(PrintError error, int partial = 0)
        {
            var outcome = error.Kind switch
            {
                PrintErrorKind.InvalidLabel => PrintOutcome.InvalidLabel,
                PrintErrorKind.InvalidProfile or PrintErrorKind.EncodingFailed => PrintOutcome.Invalid,
                PrintErrorKind.Cancelled => PrintOutcome.Cancelled,
                PrintErrorKind.HeadOpen or PrintErrorKind.PaperOut or PrintErrorKind.RibbonOut or PrintErrorKind.HeadTooHot
                    or PrintErrorKind.DiagnosticMode or PrintErrorKind.BufferFull or PrintErrorKind.Paused => PrintOutcome.HeldBack,
                _ => PrintOutcome.Failed,
            };
            string message = Text.Describe(error);
            if (partial > 0) message += " " + Text.Get("Data.Batch.Partial", partial);
            return new(outcome, message, error.Kind.ToString(), Printer, Records, Labels, Jobs, BytesSent, Notices, null, Request.Reference);
        }

        public PrintJobResult Finish(PrintOutcome outcome, string message, string? zpl = null) =>
            new(outcome, message, null, Printer, Records, Labels, Jobs, BytesSent, Notices, zpl, Request.Reference);
    }
}
