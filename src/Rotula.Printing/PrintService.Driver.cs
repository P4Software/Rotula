using System.Globalization;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Rendering.View;

namespace Rotula.Printing;

// Picture-mode (Windows driver) half of PrintService: the pre-flight, the acknowledgement gate and page submission that keeps serial numbers
// honest. The printer-language half lives in PrintService.cs and is not touched by anything here.
public sealed partial class PrintService
{
    /// <summary>Name of every picture-mode job in the Windows print queue; the same name the printer-language path gives its jobs.</summary>
    private const string DriverDocumentName = "Rotula label";

    /// <summary>
    /// Message key of a resolved label with fewer than one copy. It is the key the merge engine already uses for a copies value it cannot
    /// accept (<c>MergeBatch</c>), so the app already has a text for it; the argument is the value that was given.
    /// </summary>
    private const string CopiesInvalidKey = "Merge.Copies.Invalid";

    /// <summary>
    /// Most bytes of distinct pictures one driver document holds before it is ended early (128 MB). A document is drawn completely before any
    /// of its pages is handed to the driver, so a label that cannot be drawn stops the job before that document starts; this caps what that
    /// costs in memory. With the default 50 labels per document the cap only bites for large labels at high resolution (a 4 x 6 inch label at
    /// 600 dpi is about 8.6 MB, so 14 of them per document). Copies of one label share one picture and never count twice. Ending a document
    /// early only adds a document boundary, which is safe for serial numbers.
    /// </summary>
    internal const long DefaultChunkPictureBudgetBytes = 128L * 1024 * 1024;

    /// <summary>Labels per driver document for <see cref="PrintResolvedAsync"/>, which takes no options: the default of <see cref="PrintVariableOptions.LabelsPerJob"/>.</summary>
    private static readonly int DefaultLabelsPerJob = new PrintVariableOptions().LabelsPerJob;

    /// <summary>
    /// Draws one label picture: the design, the label's resolved values, the picture resolution. Production always uses
    /// <see cref="DevicePictureRenderer.Render"/>; tests replace it to count renders, record values or make one label fail.
    /// </summary>
    internal Func<LabelDocument, IReadOnlyDictionary<string, string>?, int, DevicePicture> PictureRenderer { get; set; } = DevicePictureRenderer.Render;

    /// <summary>Bytes of distinct pictures one driver document may hold; <see cref="DefaultChunkPictureBudgetBytes"/> unless a test lowers it.</summary>
    internal long ChunkPictureBudgetBytes { get; set; } = DefaultChunkPictureBudgetBytes;

    /// <inheritdoc />
    /// <remarks>
    /// For a driver target the driver is asked for its media with the label's size in millimetres, then <see cref="DriverPreflight.Analyze(LabelDocument, DriverMedia?)"/> judges
    /// the design. A cancelled probe cannot be answered with "no media" (that would claim the driver is unreachable) nor with
    /// <see cref="DriverPreflightResult.None"/> (that would read as "all clear"), so it returns the single error finding
    /// <see cref="DriverPreflight.Cancelled"/> (no arguments): it stops the job like any error and is translated like every other pre-flight
    /// finding. A probe that throws anything else counts as an unreachable driver.
    /// </remarks>
    public async Task<DriverPreflightResult> PreflightAsync(PrinterTarget target, LabelDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);
        // A printer-language target is never probed: nothing about a ZPL print may change, not even a question to its driver.
        if (!IsDriverTarget(target)) return DriverPreflightResult.None;
        var result = await AnalyzeForDriverAsync(target, document, ct).ConfigureAwait(false);
        return result ?? new DriverPreflightResult(new[] { new ValidationIssue(ValidationSeverity.Error, DriverPreflight.Cancelled) }, null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The size of the job is judged first and alone: a job over <see cref="DriverPreflight.MaxLabelsPerJob"/> labels can never print, so asking
    /// the driver (which can take a moment, and is pointless) is skipped and the person reads the one thing to fix. It comes back with no media,
    /// like <see cref="DriverPreflight.Cancelled"/>; being an error it is never part of an acknowledgement. A job within the limit is
    /// pre-flighted exactly as by the overload without a count, and a printer-language target gets <see cref="DriverPreflightResult.None"/>
    /// whatever the count (the ZPL path has its own limit, <c>Variable.Copies.TooMany</c>).
    /// </remarks>
    public async Task<DriverPreflightResult> PreflightAsync(PrinterTarget target, LabelDocument document, long labelCount, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);
        if (IsDriverTarget(target) && DriverPreflight.CheckJobSize(labelCount) is { } tooMany)
            return new DriverPreflightResult(new[] { tooMany }, null);
        return await PreflightAsync(target, document, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Only a Windows queue in <see cref="PrinterOutputMode.WindowsDriver"/> mode is printed this way; any other target fails with
    /// <see cref="PrintErrorKind.Unsupported"/> (printer-language batches send ZPL). Every label is checked first: one with fewer than one copy
    /// fails the call with <see cref="PrintErrorKind.InvalidLabel"/> before the driver is asked anything, because printing it zero times and
    /// reporting success would hide the caller's mistake. A call whose labels add up to more than <see cref="DriverPreflight.MaxLabelsPerJob"/>
    /// pages fails the same way (<see cref="DriverPreflight.TooManyLabels"/> as a notice), also before the driver is asked anything: the
    /// pre-flight refuses such a job as a whole, this guards a caller that skipped it. An empty list succeeds with no page and no driver call. A null argument or a null
    /// entry in <paramref name="labels"/> is a programming error and throws (<see cref="ArgumentNullException"/>, <see cref="ArgumentException"/>),
    /// as a null label does for <see cref="PrintAsync(PrinterTarget, LabelDocument, IReadOnlyDictionary{string, string}?, PrinterProfile?, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// Then the same gate as a single print (errors block, warnings need the matching acknowledgement), and pages in driver documents of the
    /// default <see cref="PrintVariableOptions.LabelsPerJob"/>, one picture drawn per label and resubmitted for its copies. Nothing here
    /// reserves or confirms a serial number: <see cref="PrintResult.PagesSent"/> tells the caller how many pages may have printed (on a
    /// failure: every page of the documents before, plus the pages the driver accepted of the failing one).
    /// </para>
    /// </remarks>
    public async Task<PrintResult> PrintResolvedAsync(PrinterTarget target, LabelDocument document, IReadOnlyList<ResolvedLabel> labels, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(labels);
        if (!IsDriverTarget(target)) return PrintResult.Fail(PrintErrorKind.Unsupported);
        long total = 0; // long: one record may ask for 99,999,999 copies, and a list of them overflows an int
        foreach (var label in labels)
        {
            if (label is null) throw new ArgumentException("The list of labels contains a null entry.", nameof(labels));
            if (label.Copies <= 0)
            {
                var issue = new ValidationIssue(ValidationSeverity.Error, CopiesInvalidKey, null, label.Copies.ToString(CultureInfo.InvariantCulture));
                return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, CopiesInvalidKey)).WithNotices(new[] { issue });
            }
            total += label.Copies;
        }
        if (labels.Count == 0) return PrintResult.Ok(0);
        // Defence in depth for a caller that skipped the pre-flight (which checks the same limit for the whole job, see PreflightAsync): the
        // same refusal, before the driver is asked anything, so nothing is drawn and nothing reaches the spooler.
        if (RefuseTooManyLabels(total) is { } tooMany) return tooMany;

        var (refusal, media, turned) = await GateAsync(target, document, ct).ConfigureAwait(false);
        if (refusal is not null) return refusal;

        var progress = new DriverProgress();
        var outcome = await SubmitPagesAsync(target, document, media!, turned, Expand(labels), DefaultLabelsPerJob, progress, ct).ConfigureAwait(false);
        var result = outcome ?? PrintResult.Ok(Saturate(progress.Bytes));
        return result.WithPages(Saturate(progress.Pages));
    }

    /// <summary>
    /// The failure for a picture job with more than <see cref="DriverPreflight.MaxLabelsPerJob"/> labels, or null when the job is within the
    /// limit. Same shape as the other refusals of a label the print call cannot accept: <see cref="PrintErrorKind.InvalidLabel"/> naming the
    /// message key, and the finding itself as a notice, so the app words it with the same text the pre-flight's finding gets.
    /// </summary>
    private static PrintResult? RefuseTooManyLabels(long labelCount)
    {
        if (DriverPreflight.CheckJobSize(labelCount) is not { } issue) return null;
        return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, issue.MessageKey)).WithNotices(new[] { issue });
    }

    /// <summary>True for the targets printed as pictures: a Windows queue whose mode is <see cref="PrinterOutputMode.WindowsDriver"/>. Null-safe, so a null target keeps its old failure.</summary>
    private static bool IsDriverTarget(PrinterTarget? target) => target is { Transport: PrinterTransport.Spooler, Mode: PrinterOutputMode.WindowsDriver };

    /// <summary>
    /// The picture-mode path of <see cref="PrintAsync(PrinterTarget, LabelDocument, IReadOnlyDictionary{string, string}?, PrinterProfile?, PrintVariableOptions?, CancellationToken)"/>.
    /// Steps: (1) pre-flight, any error finding fails; (2) a warning without the matching acknowledgement fails; only then (3) the run begins and
    /// reserves serial numbers; (4) the labels are drawn and handed over in documents of <see cref="PrintVariableOptions.LabelsPerJob"/>; (5) the
    /// run is completed with the pages that may have printed, in a <c>finally</c>; (6) the result carries <see cref="PrintResult.PagesSent"/>.
    /// </summary>
    private async Task<PrintResult> PrintDriverAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data,
        PrintVariableOptions? variables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Every copy is a page of its own here, so the print quantity is the number of pictures. Checked before everything else: no driver
        // question, no serial number reserved (Begin below), nothing drawn.
        if (RefuseTooManyLabels(Math.Max(1, document.Print.Quantity)) is { } tooMany) return tooMany;

        // (1) and (2) come before Begin on purpose: a job that is refused here must not reserve (or even read) a serial number.
        var (refusal, media, turned) = await GateAsync(target, document, ct).ConfigureAwait(false);
        if (refusal is not null) return refusal;

        // (3) Started exactly as the printer-language path starts (PrintLanguageAsync: LabelPrintRun.Begin, then CanPrint), with the same
        // handling of an unexpected failure and of a label with errors.
        LabelPrintRun run;
        try { run = LabelPrintRun.Begin(document, data, variables); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Begin gives back whatever it reserved before it failed.
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.Unknown, target.DisplayName, ex.GetType().Name, ex.Message));
        }
        if (!run.CanPrint)
        {
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel,
                run.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray())).WithNotices(run.Issues);
        }

        var progress = new DriverProgress();
        PrintResult? outcome = null;
        IReadOnlyList<ValidationIssue> closing = Array.Empty<ValidationIssue>();
        try
        {
            // LabelPrintRun clamps LabelsPerJob the same way, so a document never holds more labels than a ZPL job would.
            int perJob = Math.Max(1, variables?.LabelsPerJob ?? DefaultLabelsPerJob);
            // (4) LabelValues resolves every label exactly as the ZPL blocks would be resolved, so page i carries the serial number of label i.
            outcome = await SubmitPagesAsync(target, document, media!, turned, run.LabelValues(), perJob, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            // (5) The printer-language rule (PrintLanguageAsync: "closing = run.Complete(printed)" in its finally): always runs, also on cancel or
            // an unexpected exception, so a reservation is never left hanging. Complete counts from the first label, so every page that may have
            // printed keeps its number and every later number goes back, as for the blocks the ZPL path never sent.
            closing = run.Complete(progress.Pages);
        }

        // Warnings of the run plus problems writing the counter file (PrintLanguageAsync builds its notes the same way); no printer status here.
        var notes = run.Issues.Where(i => i.Severity != ValidationSeverity.Error).Concat(closing).ToList();
        int pages = Saturate(progress.Pages);
        int bytes = Saturate(progress.Bytes);
        if (outcome is null && run.StoppedEarly)
        {
            // A later label could not be resolved: as in PrintLanguageAsync, the labels before it were sent (the last document was cut short
            // there) and the run reports the label's errors.
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel,
                    run.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray()), bytes)
                .WithNotices(run.Issues.Concat(closing).ToList())
                .WithPages(pages);
        }
        // (6) A failure keeps its own error and notices (a label that could not be drawn lists what); the run's notes are added after them.
        var result = outcome ?? PrintResult.Ok(bytes);
        return result.WithPages(pages).WithNotices(result.Notices.Concat(notes).ToList());
    }

    /// <summary>
    /// Pre-flight and acknowledgement for a print call. Returns the refusal to report (nothing was sent and nothing reserved), or no refusal and
    /// the media the pictures are drawn for and whether they are turned a quarter turn to fit it.
    /// </summary>
    private async Task<(PrintResult? Refusal, DriverMedia? Media, bool Turned)> GateAsync(PrinterTarget target, LabelDocument document, CancellationToken ct)
    {
        var preflight = await AnalyzeForDriverAsync(target, document, ct).ConfigureAwait(false);
        if (preflight is null) return (PrintResult.Fail(PrintErrorKind.Cancelled), null, false);
        if (preflight.HasErrors)
        {
            // Errors block whatever was acknowledged; the findings travel as notices so the app can say which part and why.
            string[] keys = preflight.Findings.Where(f => f.Severity == ValidationSeverity.Error).Select(f => f.MessageKey).ToArray();
            return (PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, keys)).WithNotices(preflight.Findings), null, false);
        }
        // Compared with the fingerprint worked out just now, never with one remembered from the dialog: a design changed since the person ticked
        // the box has other findings, so a new fingerprint, and is asked about again.
        if (preflight.NeedsAcknowledgement && !string.Equals(target.AcknowledgedFindings, preflight.Fingerprint, StringComparison.Ordinal))
            return (PrintResult.Fail(PrintErrorKind.NotAcknowledged).WithNotices(preflight.Findings), null, false);
        // Without errors Analyze always reports the media it judged (an unusable media is the DriverUnavailable error).
        return (null, preflight.Media, preflight.LabelTurned);
    }

    /// <summary>
    /// Asks the driver for its media at the label's size and runs <see cref="DriverPreflight.Analyze(LabelDocument, DriverMedia?)"/>. Null when
    /// the probe was cancelled.
    /// <para>
    /// <b>Sideways stock.</b> When the label as designed is larger than the paper the driver will use
    /// (<see cref="DriverPreflight.LabelLargerThanPaper"/>), the driver is asked once more for the label turned a quarter turn (width and height
    /// swapped), and when that one fits, the result is the turned label's (<see cref="DriverPreflightResult.LabelTurned"/>, with the warning
    /// <see cref="DriverPreflight.LabelTurned"/>). The second answer is asked for rather than guessed from the first, so the judgement, the
    /// size the driver is later asked to print at, and the check before StartDoc are all for the page the driver really gives for that size.
    /// A label that fits as designed is never turned (the second question is not even asked), a square label has nothing to turn, and when
    /// neither way fits the result is the first one, the ordinary refusal. Pre-flight and print both come through here, so they cannot disagree.
    /// </para>
    /// </summary>
    private async Task<DriverPreflightResult?> AnalyzeForDriverAsync(PrinterTarget target, LabelDocument document, CancellationToken ct)
    {
        var (widthMm, heightMm) = LabelSizeMm(document);
        if (!IsPositive(widthMm) || !IsPositive(heightMm))
        {
            // The driver cannot be asked about a page with no size (the probe refuses it), and "driver unavailable" would send the person to the
            // wrong place. These are the findings the printer-language path reports for the same label (ZplGenerator's label size checks).
            var size = new List<ValidationIssue>();
            if (!IsPositive(widthMm)) size.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Width.Range", null, "1", "9999"));
            if (!IsPositive(heightMm)) size.Add(new ValidationIssue(ValidationSeverity.Error, "Label.Height.Range", null, "1", "32000"));
            return new DriverPreflightResult(size, null);
        }

        var (media, cancelled) = await ProbeAsync(target, widthMm, heightMm, ct).ConfigureAwait(false);
        if (cancelled) return null;
        var asDesigned = DriverPreflight.Analyze(document, media);

        // Only a label that does not fit as designed is looked at turned, and only when turning changes anything.
        bool square = Math.Abs(widthMm - heightMm) <= GdiPageLayout.PageToleranceMm;
        if (square || !asDesigned.Findings.Any(f => f.MessageKey == DriverPreflight.LabelLargerThanPaper)) return asDesigned;

        var (turnedMedia, turnedCancelled) = await ProbeAsync(target, heightMm, widthMm, ct).ConfigureAwait(false);
        if (turnedCancelled) return null;
        var turned = DriverPreflight.Analyze(document, turnedMedia, labelTurned: true);
        // The turned label counts only when the driver reported usable media for it (Media is null for "driver unavailable") and it fits.
        bool fits = turned.Media is not null && !turned.Findings.Any(f => f.MessageKey == DriverPreflight.LabelLargerThanPaper);
        return fits ? turned : asDesigned;
    }

    /// <summary>
    /// One question to the driver. A cancel is reported as such (never as "no media", which would claim the driver is unreachable); a probe
    /// that throws anything else counts as an unreachable driver (null media), as the contract says an unreachable one answers null.
    /// </summary>
    private async Task<(DriverMedia? Media, bool Cancelled)> ProbeAsync(PrinterTarget target, double widthMm, double heightMm, CancellationToken ct)
    {
        DriverMedia? media;
        try { media = await _driver.ProbeAsync(target.PrinterName, widthMm, heightMm, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // By contract the probe answers null for a printer it cannot reach; a driver binding that throws instead is just as unreachable.
            media = null;
        }
        // A driver that ignores the token still cannot turn a cancel into a print.
        return ct.IsCancellationRequested ? (null, true) : (media, false);
    }

    /// <summary>
    /// Draws the labels and hands them to the driver in documents of at most <paramref name="perJob"/> pages, adding to
    /// <paramref name="progress"/> every page that may have printed. Returns null when every label was handed over and accepted, or the failure
    /// that ended the run. Each document is drawn completely before any of its pages goes out, so a label that cannot be drawn stops the run with
    /// nothing of its document sent.
    /// </summary>
    private async Task<PrintResult?> SubmitPagesAsync(PrinterTarget target, LabelDocument document, DriverMedia media, bool turned,
        IEnumerable<IReadOnlyDictionary<string, string>?> labels, int perJob, DriverProgress progress, CancellationToken ct)
    {
        var (designedWidthMm, designedHeightMm) = LabelSizeMm(document);
        // A turned label lies on the paper the other way round: the driver is told the turned size (the one the pre-flight asked it about, which
        // its check before StartDoc compares with), and every picture is turned to match. Turning the finished picture, not drawing a turned
        // design, keeps every pixel (so every barcode module) exactly as it was drawn.
        var (widthMm, heightMm) = turned ? (designedHeightMm, designedWidthMm) : (designedWidthMm, designedHeightMm);
        int renderDpi = DevicePictureRenderer.RenderDpi(media);
        int maxDistinct = MaxDistinctPictures(document, renderDpi);

        // The last picture drawn and the values instance it was drawn for. The run hands out the same dictionary instance for every copy of a
        // label, so comparing instances finds the copies without comparing contents, and the picture is drawn once and resubmitted (500 copies of a
        // plain label: one render, 500 pages). It is kept across documents for that reason. A flag is needed because a label without variables has
        // null values, and null must match null.
        bool hasLast = false;
        IReadOnlyDictionary<string, string>? lastValues = null;
        DevicePicture? lastPicture = null;

        using var source = labels.GetEnumerator();
        // True when source.Current is a label that did not fit the previous document (it needed one distinct picture too many).
        bool carried = false;
        while (true)
        {
            // Consecutive pages of one picture are stored once with a count, so a document of copies holds a single picture.
            var runs = new List<PageRun>();
            int pages = 0;
            bool exhausted = false;
            while (pages < perJob)
            {
                // Checked before every label is drawn (drawing is the slow part): a cancel stops the run with nothing of this document sent.
                if (ct.IsCancellationRequested) return PrintResult.Fail(PrintErrorKind.Cancelled);
                if (!carried && !source.MoveNext())
                {
                    exhausted = true;
                    break;
                }
                carried = false;
                var values = source.Current;

                DevicePicture picture;
                if (hasLast && ReferenceEquals(lastValues, values))
                {
                    picture = lastPicture!;
                }
                else
                {
                    if (runs.Count >= maxDistinct)
                    {
                        // The document already holds as many distinct pictures as the memory cap allows: this label opens the next one.
                        carried = true;
                        break;
                    }
                    try
                    {
                        picture = PictureRenderer(document, values, renderDpi);
                        if (turned) picture = picture.TurnedClockwise();
                    }
                    catch (DevicePictureException ex)
                    {
                        // The label cannot be drawn faithfully for these values (a red cross would be a silent substitution). Nothing of this
                        // document was handed over, so its labels count as not printed; the documents before it stay counted.
                        return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel, ex.Issues.Select(i => i.MessageKey).ToArray()), Saturate(progress.Bytes))
                            .WithNotices(ex.Issues);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Any other drawing failure is reported like an unexpected failure at the start of a run, never as a crash of the caller.
                        return PrintResult.Fail(PrintError.Create(PrintErrorKind.Unknown, target.DisplayName, ex.GetType().Name, ex.Message), Saturate(progress.Bytes));
                    }
                    hasLast = true;
                    lastValues = values;
                    lastPicture = picture;
                }

                if (runs.Count > 0 && ReferenceEquals(runs[^1].Picture, picture)) runs[^1] = runs[^1] with { Count = runs[^1].Count + 1 };
                else runs.Add(new PageRun(picture, 1));
                pages++;
            }

            if (pages > 0)
            {
                var failure = await SubmitDocumentAsync(target, widthMm, heightMm, media, runs, pages, progress, ct).ConfigureAwait(false);
                // A failed document ends the run: the documents after it are never started, so their labels' numbers go back (as the blocks
                // after a failed send in PrintLanguageAsync, which breaks out of its loop).
                if (failure is not null) return failure;
            }
            if (exhausted) return null;
        }
    }

    /// <summary>
    /// Hands one document of <paramref name="pages"/> pages to the driver and adds the pages that may have printed to <paramref name="progress"/>.
    /// Returns null when the driver accepted the whole document, or the failure. Never throws for a driver problem.
    /// </summary>
    /// <param name="target">The Windows queue.</param>
    /// <param name="widthMm">Label width in millimetres.</param>
    /// <param name="heightMm">Label height in millimetres.</param>
    /// <param name="media">
    /// The media the pre-flight judged and the pictures were drawn for. Every document carries it, because the driver checks its device context
    /// against it before the document starts: a driver that changed its resolution or page since the check refuses the document with no page.
    /// </param>
    /// <param name="runs">The document's pictures, consecutive copies stored once with a count.</param>
    /// <param name="pages">How many pages the runs hold.</param>
    /// <param name="progress">Where the pages that may have printed are added.</param>
    /// <param name="ct">Cancels the document between pages.</param>
    private async Task<PrintResult?> SubmitDocumentAsync(PrinterTarget target, double widthMm, double heightMm, DriverMedia media,
        IReadOnlyList<PageRun> runs, int pages, DriverProgress progress, CancellationToken ct)
    {
        var feed = new PageFeed(runs);
        bool counted = false;
        try
        {
            var outcome = await _driver.PrintAsync(target.PrinterName, DriverDocumentName, widthMm, heightMm, media, feed.Pages(), ct).ConfigureAwait(false);
            if (outcome?.Result is null)
                return PrintResult.Fail(PrintError.Create(PrintErrorKind.Unknown, target.DisplayName, nameof(IDriverPrinter), "The driver printer returned no outcome."));

            if (outcome.Result.Succeeded)
            {
                // The whole document was accepted. Counted in full even if a driver reports fewer pages: Complete counts from the first label, so
                // under-counting a finished document would give back a number that may be on paper once the next document goes out.
                progress.Pages += pages;
                progress.Bytes += outcome.Result.BytesSent;
                counted = true;
                return null;
            }

            // On a failure the driver's own count is the authority: a page counts once the driver accepted it (EndPage succeeded), because from then
            // on it may print even though the job is aborted. This is the ZPL rule "a send that moved bytes may have printed"
            // (LabelPrintRun.MayHavePrinted) made exact: the ZPL path can only keep or release a whole block, the driver says which pages went out.
            // The pages after the failing one were never accepted and go back. Clamped so a misbehaving count can never lower the total.
            progress.Pages += Math.Clamp(outcome.PagesSubmitted, 0, pages);
            progress.Bytes += outcome.Result.BytesSent;
            counted = true;
            // Returned untouched (same error, same arguments): the caller only adds the page count and the run's notes.
            return outcome.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PrintResult.Fail(PrintErrorKind.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A driver binding that throws instead of reporting is still a printer problem, so it becomes a result, not an escaping exception.
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.Unknown, target.DisplayName, ex.GetType().Name, ex.Message));
        }
        finally
        {
            // Without an outcome the driver reported no count (it threw, or returned nothing). Every picture it was handed may already be on its way
            // to paper, so every one of them counts: a gap in the series is acceptable, the same number on two labels is not.
            if (!counted) progress.Pages += feed.Yielded;
        }
    }

    /// <summary>The labels of a resolved batch, one entry per copy, each copy carrying the same values instance (so it is drawn once).</summary>
    private static IEnumerable<IReadOnlyDictionary<string, string>?> Expand(IReadOnlyList<ResolvedLabel> labels)
    {
        foreach (var label in labels)
            for (int i = 0; i < label.Copies; i++)
                yield return label.Values;
    }

    /// <summary>How many distinct pictures of <paramref name="document"/> fit the memory cap of one driver document; at least one.</summary>
    private int MaxDistinctPictures(LabelDocument document, int renderDpi)
    {
        // Every picture of a document has the same size, worked out without drawing anything.
        var (width, height) = DevicePictureRenderer.PictureSize(document, renderDpi);
        long bytes = Math.Max(1L, (long)width * height);
        return (int)Math.Clamp(ChunkPictureBudgetBytes / bytes, 1L, int.MaxValue);
    }

    /// <summary>
    /// The label's designed size in millimetres: the figures the person typed, as the pre-flight's paper check reads them, and the size each
    /// picture is printed at. NaN when the design resolution is unusable.
    /// </summary>
    private static (double Width, double Height) LabelSizeMm(LabelDocument document)
    {
        int dpi = (int)document.Dpi;
        if (dpi <= 0) return (double.NaN, double.NaN);
        return (UnitConverter.Convert(document.Width.Value, document.Width.Unit, LengthUnit.Millimeters, dpi),
            UnitConverter.Convert(document.Height.Value, document.Height.Unit, LengthUnit.Millimeters, dpi));
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    /// <summary>A long count as the int a <see cref="PrintResult"/> carries, never negative and never wrapping.</summary>
    private static int Saturate(long value) => (int)Math.Clamp(value, 0L, int.MaxValue);

    /// <summary>Consecutive pages that print the same picture.</summary>
    /// <param name="Picture">The picture.</param>
    /// <param name="Count">How many pages in a row print it.</param>
    private readonly record struct PageRun(DevicePicture Picture, int Count);

    /// <summary>Pages that may have printed and their picture bytes, summed over the documents of one call. A class so the <c>finally</c> that completes the run sees every addition.</summary>
    private sealed class DriverProgress
    {
        /// <summary>Pages that may have printed, counted from the first label.</summary>
        public long Pages;
        /// <summary>Picture bytes the driver reported for those pages.</summary>
        public long Bytes;
    }

    /// <summary>Hands one document's pictures to the driver lazily, one per page, and counts how many it handed over.</summary>
    private sealed class PageFeed(IReadOnlyList<PageRun> runs)
    {
        /// <summary>Pictures handed to the driver so far.</summary>
        public int Yielded { get; private set; }

        /// <summary>The pages, in order; a run of copies yields the same picture instance again.</summary>
        public IEnumerable<DevicePicture> Pages()
        {
            foreach (var run in runs)
            {
                for (int i = 0; i < run.Count; i++)
                {
                    // Counted before it is handed over: if the driver throws while holding it, it may already be on its way to paper.
                    Yielded++;
                    yield return run.Picture;
                }
            }
        }
    }
}
