using System.Text;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Printing;

/// <summary>The single entry point the UI uses to find printers and print. Every call is async and never throws for printing failures.</summary>
public interface IPrintService
{
    /// <summary>Lists installed Windows printers.</summary>
    Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates ZPL for <paramref name="document"/> and prints it. Labels with validation errors are not sent.</summary>
    /// <param name="target">Destination printer.</param>
    /// <param name="document">The label.</param>
    /// <param name="data">Variable values merged into the label.</param>
    /// <param name="profile">Optional profile whose set-up commands are sent first, in the same job.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data = null,
        PrinterProfile? profile = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like the plain overload, but the label's variables (dates, counters that remember their value, prompt answers, computed values) are
    /// resolved from <paramref name="variables"/> for every label, and the reserved serial numbers are confirmed only for labels that may have
    /// been printed. The default implementation ignores <paramref name="variables"/> so existing fakes keep compiling; <see cref="PrintService"/> honours it.
    /// </summary>
    /// <param name="target">Destination printer.</param>
    /// <param name="document">The label; its print quantity is the number of copies.</param>
    /// <param name="data">Values of the data record, or null.</param>
    /// <param name="profile">Optional profile whose set-up commands are sent first.</param>
    /// <param name="variables">Clock, counter memory and prompt answers; null uses the defaults (computer clock, no counter memory).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data,
        PrinterProfile? profile, PrintVariableOptions? variables, CancellationToken cancellationToken = default) =>
        PrintAsync(target, document, data, profile, cancellationToken);

    /// <summary>Prints ready-made ZPL, encoding it with <paramref name="encoding"/>.</summary>
    Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints ready-made ZPL like the plain overload, but writes every occurrence of a block in <paramref name="byteExactBlocks"/>
    /// as Latin-1 (one byte per char) so imported raw ZPL with bytes above 0x7F reaches the printer unchanged.
    /// Use it for data-merge batches: collect the blocks once with <see cref="ByteExactBlocks.From(LabelDocument)"/>.
    /// The default implementation ignores the blocks and behaves like the plain overload; <see cref="PrintService"/> honours them.
    /// </summary>
    /// <param name="target">Destination printer.</param>
    /// <param name="zpl">The ZPL text of one job.</param>
    /// <param name="encoding">Encoding for everything outside the byte-exact blocks.</param>
    /// <param name="byteExactBlocks">Raw ZPL texts to send byte for byte.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, IReadOnlyCollection<string> byteExactBlocks,
        CancellationToken cancellationToken = default) => PrintZplAsync(target, zpl, encoding, cancellationToken);

    /// <summary>Sends only the profile set-up commands (darkness, speed, media, width).</summary>
    Task<PrintResult> ApplyProfileAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a small test label for the profile and prints it. When <see cref="PrinterProfile.UseDetectedSettings"/>
    /// is set and the target is a Windows queue, the driver's resolution and paper size replace the profile's.
    /// </summary>
    Task<PrintResult> PrintTestLabelAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads resolution and paper size from the driver of a Windows queue. TCP targets return unknown info (no network
    /// probing). Never throws. The default implementation returns unknown info so existing fakes keep compiling.
    /// </summary>
    Task<PrinterCapabilities> GetPrinterInfoAsync(PrinterTarget target, CancellationToken ct = default) =>
        Task.FromResult(PrinterCapabilities.Unknown(target?.PrinterName ?? string.Empty));

    /// <summary>
    /// Asks the printer for its host status (<c>~HS</c>) and decodes it. Never throws for a printer problem: a printer that cannot answer
    /// gives an unavailable status. Cancellation throws <see cref="OperationCanceledException"/>. The default implementation reports
    /// "not supported" so existing fakes keep compiling.
    /// </summary>
    /// <param name="target">The printer; its <see cref="PrinterStatusOptions.QueryTimeout"/> bounds the wait.</param>
    /// <param name="ct">Cancellation.</param>
    Task<PrinterStatus> GetStatusAsync(PrinterTarget target, CancellationToken ct = default) =>
        Task.FromResult(PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported));

    /// <summary>
    /// After jobs were sent, asks the printer for its status until it reports it finished, reports a fault, or
    /// <see cref="PrinterStatusOptions.VerifyTimeout"/> passes, and returns the outcome as one notice (<see cref="PrinterStatusKeys"/>).
    /// Print calls do this themselves when the target asks for it; a caller that sends several jobs calls it once after the last.
    /// Cancellation stops the wait and is reported as a notice, never as a failure, because the labels were already sent.
    /// The default implementation returns no notice.
    /// </summary>
    Task<IReadOnlyList<ValidationIssue>> VerifyPrintedAsync(PrinterTarget target, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ValidationIssue>>(Array.Empty<ValidationIssue>());
}

/// <summary>Message keys of the notices the status check and the verification add to a <see cref="PrintResult"/>.</summary>
public static class PrinterStatusKeys
{
    /// <summary>The status could not be read before printing, so the job was sent unchecked. Argument: a <see cref="StatusUnavailableReason"/> name.</summary>
    public const string NotChecked = "PrinterStatus.NotChecked";
    /// <summary>The printer reported conditions that do not stop printing (head cold...). Arguments: condition names.</summary>
    public const string Warning = "PrinterStatus.Warning";
    /// <summary>The printer reported it finished the job without a fault.</summary>
    public const string Verified = "PrinterStatus.Verified";
    /// <summary>The printer reported a fault after the job was sent; labels may be missing. Arguments: condition names.</summary>
    public const string FaultAfterJob = "PrinterStatus.FaultAfterJob";
    /// <summary>The outcome could not be confirmed. Argument: a <see cref="StatusUnavailableReason"/> name, <see cref="StillPrinting"/> or <see cref="Stopped"/>.</summary>
    public const string NotVerified = "PrinterStatus.NotVerified";
    /// <summary>Reason of <see cref="NotVerified"/>: the printer was still busy when the wait ended.</summary>
    public const string StillPrinting = "StillPrinting";
    /// <summary>Reason of <see cref="NotVerified"/>: the operator stopped the wait.</summary>
    public const string Stopped = "Stopped";
}

/// <summary>Default <see cref="IPrintService"/>: ZPL from Core, bytes from <see cref="ZplEncoding"/>, transport by target.</summary>
public sealed class PrintService : IPrintService
{
    private readonly IRawPrinter _spooler;
    private readonly INetworkPrinter _network;
    private readonly IPrinterDiscovery _discovery;
    private readonly IPrinterStatusReader _status;

    /// <summary>Creates the service with the real winspool and TCP implementations.</summary>
    public PrintService() : this(new WinspoolRawPrinter(), new TcpRawPrinter(), new WinspoolRawPrinter()) { }

    /// <summary>
    /// Creates the service with explicit transports (tests pass fakes). Status queries use the real <see cref="PrinterStatusReader"/>; they
    /// only happen for a target that asks for them (<see cref="PrinterTarget.WithStatus"/>) or through <see cref="GetStatusAsync"/>.
    /// </summary>
    public PrintService(IRawPrinter spooler, INetworkPrinter network, IPrinterDiscovery discovery)
        : this(spooler, network, discovery, new PrinterStatusReader()) { }

    /// <summary>Creates the service with explicit transports and status reader (tests pass fakes).</summary>
    public PrintService(IRawPrinter spooler, INetworkPrinter network, IPrinterDiscovery discovery, IPrinterStatusReader status)
    {
        _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync(CancellationToken cancellationToken = default) =>
        _discovery.GetPrintersAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data = null,
        PrinterProfile? profile = null, CancellationToken cancellationToken = default) =>
        PrintAsync(target, document, data, profile, null, cancellationToken);

    /// <inheritdoc />
    public async Task<PrintResult> PrintAsync(PrinterTarget target, LabelDocument document, IReadOnlyDictionary<string, string>? data,
        PrinterProfile? profile, PrintVariableOptions? variables, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var run = LabelPrintRun.Begin(document, data, variables);
        if (!run.CanPrint)
        {
            // Begin gave any reserved numbers back; the error keys tell the operator what to fix.
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel,
                run.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray())).WithNotices(run.Issues);
        }

        long printed = 0;
        int bytesSent = 0;
        PrintResult? outcome = null;
        IReadOnlyList<ValidationIssue> closing = Array.Empty<ValidationIssue>();
        var statusNotes = new List<ValidationIssue>();
        try
        {
            string setup = string.Empty;
            if (profile is not null)
            {
                var bad = profile.Validate();
                if (bad.Count > 0) return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidProfile, bad.ToArray()));
                setup = PrinterSetup.BuildSetupZpl(profile);
            }
            ArgumentNullException.ThrowIfNull(target);
            // Raw blocks imported from bytes hold bytes above 0x7F as the Latin-1 character with the same value, so they
            // must go out as those exact bytes; the rest of the label follows the document encoding.
            var exact = ByteExactBlocks.From(document);
            bool first = true;
            foreach (var block in run.Blocks())
            {
                if (cancellationToken.IsCancellationRequested) { outcome = PrintResult.Fail(PrintErrorKind.Cancelled); break; }
                string zpl = first ? setup + block.Zpl : block.Zpl;
                if (!TryEncode(zpl, document.Encoding, exact, out var bytes))
                {
                    outcome = PrintResult.Fail(PrintErrorKind.EncodingFailed, document.Encoding.ToString());
                    break;
                }
                // Checked before every block, so paper running out in the middle of a long run stops it before the next block.
                // A block that is held back sent nothing, so its serial numbers go back with the rest.
                var held = await CheckBeforeAsync(target, statusNotes, cancellationToken).ConfigureAwait(false);
                if (held is not null) { outcome = held; break; }
                var sent = await SendAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                if (!sent.Succeeded)
                {
                    // A send that moved bytes may have printed some of these labels: count them as used so a number is never printed twice.
                    if (LabelPrintRun.MayHavePrinted(sent)) printed += block.Labels;
                    outcome = sent;
                    break;
                }
                printed += block.Labels;
                bytesSent += sent.BytesSent;
                first = false;
            }
        }
        finally
        {
            // Always runs, also on cancellation or an unexpected exception, so a reservation is never left hanging.
            closing = run.Complete(printed);
        }
        // Warnings of the run plus problems writing the counter file; the errors of the run are reported through the result itself.
        var notes = run.Issues.Where(i => i.Severity != ValidationSeverity.Error).Concat(closing).Concat(statusNotes).ToList();
        if (outcome is null && run.StoppedEarly)
            return PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidLabel,
                run.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.MessageKey).ToArray()), bytesSent)
                .WithNotices(run.Issues.Concat(closing).Concat(statusNotes).ToList());
        // Verified only after the counters were confirmed: a fault reported now does not give numbers back, because the labels may have printed.
        if (outcome is null && bytesSent > 0 && target.Status.VerifyAfterPrinting)
            notes.AddRange(await VerifyPrintedAsync(target, cancellationToken).ConfigureAwait(false));
        return (outcome ?? PrintResult.Ok(bytesSent)).WithNotices(notes);
    }

    /// <inheritdoc />
    public Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(zpl);
        ArgumentNullException.ThrowIfNull(encoding);
        if (!TryEncode(zpl, encoding, out var bytes))
            return Task.FromResult(PrintResult.Fail(PrintErrorKind.EncodingFailed, encoding.ToString()));
        return SendCheckedAsync(target, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrintResult> PrintZplAsync(PrinterTarget target, string zpl, ZplEncoding encoding, IReadOnlyCollection<string> byteExactBlocks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(zpl);
        ArgumentNullException.ThrowIfNull(encoding);
        ArgumentNullException.ThrowIfNull(byteExactBlocks);
        if (!TryEncode(zpl, encoding, byteExactBlocks, out var bytes))
            return Task.FromResult(PrintResult.Fail(PrintErrorKind.EncodingFailed, encoding.ToString()));
        return SendCheckedAsync(target, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrintResult> ApplyProfileAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var bad = profile.Validate();
        if (bad.Count > 0) return Task.FromResult(PrintResult.Fail(PrintError.Create(PrintErrorKind.InvalidProfile, bad.ToArray())));
        ArgumentNullException.ThrowIfNull(target);
        // Set-up text is pure ASCII, so any encoding yields the same bytes. No status check: the commands print nothing and a printer
        // that is paused or out of labels still takes its new settings.
        if (!TryEncode(PrinterSetup.BuildSetupZpl(profile), ZplEncoding.Utf8, out var bytes))
            return Task.FromResult(PrintResult.Fail(PrintErrorKind.EncodingFailed, ZplEncoding.Utf8.ToString()));
        return SendAsync(target, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PrintResult> PrintTestLabelAsync(PrinterTarget target, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(target);
        if (profile.UseDetectedSettings)
        {
            // Never throws; unknown info leaves the profile as it was.
            var info = await GetPrinterInfoAsync(target, cancellationToken).ConfigureAwait(false);
            profile = PrinterProfile.FromInfo(info, profile);
        }
        return await PrintAsync(target, TestLabel.Build(profile), null, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<PrinterCapabilities> GetPrinterInfoAsync(PrinterTarget target, CancellationToken ct = default)
    {
        if (target is null || target.Transport != PrinterTransport.Spooler || string.IsNullOrWhiteSpace(target.PrinterName))
            return Task.FromResult(PrinterCapabilities.Unknown(target?.PrinterName ?? string.Empty));
        return SafeInfoAsync(target.PrinterName, ct);
    }

    private async Task<PrinterCapabilities> SafeInfoAsync(string name, CancellationToken ct)
    {
        try { return await _discovery.GetPrinterInfoAsync(name, ct).ConfigureAwait(false); }
        catch { return PrinterCapabilities.Unknown(name); }
    }

    /// <inheritdoc />
    public Task<PrinterStatus> GetStatusAsync(PrinterTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return SafeStatusAsync(target, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ValidationIssue>> VerifyPrintedAsync(PrinterTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var options = target.Status;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        PrinterStatus? lastKnown = null;
        var lastReason = StatusUnavailableReason.NoReply;
        try
        {
            while (true)
            {
                // Wait first: right after the send the printer may not have taken the format into its buffer yet, and an empty buffer
                // read too early would look like "finished".
                await Task.Delay(options.PollInterval, ct).ConfigureAwait(false);
                var status = await SafeStatusAsync(target, ct).ConfigureAwait(false);
                if (status.IsAvailable)
                {
                    lastKnown = status;
                    if (status.IsBlocked) return [Notice(PrinterStatusKeys.FaultAfterJob, status.Conditions.Select(c => c.ToString()).ToArray())];
                    if (status.IsIdle) return [Notice(PrinterStatusKeys.Verified)];
                }
                else
                {
                    lastReason = status.Reason!.Value;
                    // Asking again cannot help when the transport cannot read replies at all.
                    if (lastReason == StatusUnavailableReason.NotSupported) break;
                }
                if (clock.Elapsed >= options.VerifyTimeout) break;
            }
        }
        catch (OperationCanceledException)
        {
            return [Notice(PrinterStatusKeys.NotVerified, PrinterStatusKeys.Stopped)];
        }
        return [Notice(PrinterStatusKeys.NotVerified, lastKnown is not null ? PrinterStatusKeys.StillPrinting : lastReason.ToString())];
    }

    /// <summary>
    /// The check in front of a job, when the target asks for it. Returns the failure to report instead of sending (a blocking fault, or
    /// the caller cancelled), or null to send. An unknown status never blocks; it adds one notice so the operator knows nothing was checked.
    /// </summary>
    private async Task<PrintResult?> CheckBeforeAsync(PrinterTarget target, List<ValidationIssue> notes, CancellationToken ct)
    {
        if (!target.Status.CheckBeforePrinting) return null;
        PrinterStatus status;
        try { status = await SafeStatusAsync(target, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return PrintResult.Fail(PrintErrorKind.Cancelled); }
        if (!status.IsAvailable)
        {
            AddOnce(notes, Notice(PrinterStatusKeys.NotChecked, status.Reason!.Value.ToString()));
            return null;
        }
        if (PrintError.FromStatus(status, target.DisplayName) is { } blocked) return PrintResult.Fail(blocked);
        if (status.Warnings.Count > 0) AddOnce(notes, Notice(PrinterStatusKeys.Warning, status.Warnings.Select(c => c.ToString()).ToArray()));
        return null;
    }

    /// <summary>One job with the optional check before and verification after (the ready-made ZPL paths).</summary>
    private async Task<PrintResult> SendCheckedAsync(PrinterTarget target, byte[] bytes, CancellationToken ct)
    {
        var notes = new List<ValidationIssue>();
        var held = await CheckBeforeAsync(target, notes, ct).ConfigureAwait(false);
        if (held is not null) return held.WithNotices(notes);
        var sent = await SendAsync(target, bytes, ct).ConfigureAwait(false);
        if (sent.Succeeded && sent.BytesSent > 0 && target.Status.VerifyAfterPrinting)
            notes.AddRange(await VerifyPrintedAsync(target, ct).ConfigureAwait(false));
        return sent.WithNotices(notes);
    }

    /// <summary>Asks the reader; a reader that breaks its contract and throws is treated as "no status", never as a crash of the print.</summary>
    private async Task<PrinterStatus> SafeStatusAsync(PrinterTarget target, CancellationToken ct)
    {
        try { return await _status.ReadStatusAsync(target, target.Status.QueryTimeout, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return PrinterStatus.Unavailable(StatusUnavailableReason.NoReply); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PrinterStatus.Unavailable(StatusUnavailableReason.NotSupported);
        }
    }

    private static ValidationIssue Notice(string key, params string[] args) => new(ValidationSeverity.Warning, key, null, args);

    private static void AddOnce(List<ValidationIssue> notes, ValidationIssue notice)
    {
        if (!notes.Any(n => n.MessageKey == notice.MessageKey)) notes.Add(notice);
    }

    /// <summary>
    /// Encodes ZPL with the document's code page: UTF-8 for ^CI28, the matching legacy page otherwise.
    /// Strict (exception) fallback so an unmappable character is reported instead of silently becoming "?".
    /// </summary>
    internal static bool TryEncode(string zpl, ZplEncoding encoding, out byte[] bytes) => TryEncode(zpl, encoding, [], out bytes);

    /// <summary>True for a raw ZPL block whose characters are all Latin-1 and at least one is above 0x7F (a byte kept as a char).</summary>
    internal static bool NeedsByteExactEncoding(string rawZpl) => ByteExactBlocks.Needs(rawZpl);

    /// <summary>
    /// Like the plain overload, but every occurrence of a block in <paramref name="byteExactBlocks"/> is written as
    /// Latin-1 (one byte per char) instead of the document encoding. Why: re-encoding such a char as UTF-8 would turn
    /// one original byte into two and corrupt the command (for example a ^GF payload or a binary ^FD).
    /// Matching by text is safe here because the generator emits raw blocks verbatim; a block that cannot be found
    /// simply follows the document encoding.
    /// </summary>
    internal static bool TryEncode(string zpl, ZplEncoding encoding, IReadOnlyCollection<string> byteExactBlocks, out byte[] bytes)
    {
        if (byteExactBlocks.Count == 0) return TryEncodeSegment(zpl, encoding, out bytes);

        var ranges = new List<(int Start, int End)>();
        foreach (var block in byteExactBlocks)
        {
            for (int i = zpl.IndexOf(block, StringComparison.Ordinal); i >= 0; i = zpl.IndexOf(block, i + block.Length, StringComparison.Ordinal))
                ranges.Add((i, i + block.Length));
        }
        ranges.Sort();

        using var ms = new MemoryStream();
        int pos = 0;
        foreach (var (start, end) in ranges)
        {
            if (end <= pos) continue; // nested in an earlier range
            int s = Math.Max(start, pos); // overlapping ranges are merged
            if (s > pos)
            {
                if (!TryEncodeSegment(zpl[pos..s], encoding, out var head)) { bytes = []; return false; }
                ms.Write(head);
            }
            ms.Write(Encoding.Latin1.GetBytes(zpl[s..end]));
            pos = end;
        }
        if (pos < zpl.Length)
        {
            if (!TryEncodeSegment(zpl[pos..], encoding, out var tail)) { bytes = []; return false; }
            ms.Write(tail);
        }
        bytes = ms.ToArray();
        return true;
    }

    private static bool TryEncodeSegment(string zpl, ZplEncoding encoding, out byte[] bytes)
    {
        try
        {
            // DotNetCodePage is read after the ZplEncoding static constructor ran, which registers the legacy code pages.
            Encoding enc = encoding.DotNetCodePage is { } page
                ? Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : new UTF8Encoding(false);
            bytes = enc.GetBytes(zpl);
            return true;
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException or NotSupportedException)
        {
            bytes = [];
            return false;
        }
    }

    private Task<PrintResult> SendAsync(PrinterTarget target, byte[] bytes, CancellationToken ct) => target.Transport switch
    {
        PrinterTransport.Spooler => _spooler.SendAsync(target.PrinterName, "Rotula label", bytes, ct),
        PrinterTransport.Tcp => _network.SendAsync(target.Host, target.Port, bytes, target.Network, ct),
        _ => Task.FromResult(PrintResult.Fail(PrintErrorKind.Unknown, target.Transport.ToString())),
    };
}

/// <summary>Finds the raw ZPL blocks of a label that must be sent byte for byte (single source for every print path).</summary>
public static class ByteExactBlocks
{
    /// <summary>
    /// Returns the distinct raw ZPL texts of <paramref name="document"/> that hold bytes above 0x7F as Latin-1 chars.
    /// Pass the result to <see cref="IPrintService.PrintZplAsync(PrinterTarget, string, ZplEncoding, IReadOnlyCollection{string}, CancellationToken)"/>.
    /// </summary>
    public static IReadOnlyCollection<string> From(LabelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Elements.OfType<RawZplElement>().Select(r => r.Zpl).Where(Needs).Distinct().ToArray();
    }

    /// <summary>True when all chars are Latin-1 and at least one is above 0x7F; a real Unicode char means it is text, not stored bytes.</summary>
    internal static bool Needs(string rawZpl)
    {
        bool high = false;
        foreach (char c in rawZpl)
        {
            if (c > 0xFF) return false;
            if (c > 0x7F) high = true;
        }
        return high;
    }
}

/// <summary>Builds the small label used by "print test label".</summary>
public static class TestLabel
{
    /// <summary>Creates a label sized and configured from the profile: a frame, a title with accents and a QR code.</summary>
    public static LabelDocument Build(PrinterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var doc = new LabelDocument
        {
            Width = profile.Width,
            Height = profile.Height,
            Dpi = profile.Dpi,
            MediaTracking = profile.MediaTracking,
            Encoding = profile.Encoding,
        };
        int w = doc.WidthDots, h = doc.HeightDots;
        double dotsPerMm = (int)profile.Dpi / 25.4;
        int margin = Math.Max(4, Math.Min(w, h) / 25);
        int pad = margin + 6;

        // Text is sized from physical size (7 mm) so it looks the same on 203, 300 and 600 dpi, then limited by the
        // label height and by the widest line (22 chars at roughly 0.6 em each) so it never runs off a narrow label.
        int fontH = (int)Math.Min(7 * dotsPerMm, Math.Min(h / 6.0, (w - 2.0 * pad) / 14.0));
        fontH = Math.Max(10, fontH); // Core rejects fonts under 10 dots

        int line1 = pad;
        int line2 = line1 + (int)(fontH * 1.3);
        int qrY = line2 + (int)(fontH * 1.5);

        // A QR code of this short payload is at most ~29 modules wide with its quiet zone; budget 33 so the symbol
        // always fits. Core only accepts a module size of 1-10, so clamp to that range.
        int availableH = h - qrY - pad;
        int availableW = w - 2 * pad;
        int module = Math.Clamp(Math.Min(availableH, availableW) / 33, 1, 10);

        doc.Elements.Add(new BoxElement { X = 0, Y = 0, WidthDots = Math.Max(1, w), HeightDots = Math.Max(1, h), ThicknessDots = 3 });
        // Accents in the sample text prove that the code page and ^CI setting match the printer.
        doc.Elements.Add(new TextElement { X = pad, Y = line1, Text = "Rotula - prueba / test", FontHeight = fontH, FontWidth = fontH });
        doc.Elements.Add(new TextElement { X = pad, Y = line2, Text = "áéíóú ñ ¿?", FontHeight = fontH, FontWidth = fontH });
        doc.Elements.Add(new BarcodeElement
        {
            X = pad, Y = qrY, Symbology = BarcodeSymbology.QrCode, Data = "ROTULA-TEST", ModuleSize = module,
        });
        return doc;
    }
}
