using System.Text.Json.Serialization;

namespace Rotula.Engine;

/// <summary>
/// One print request, as the command line builds it from its options and as the REST API reads it from JSON. Exactly one of
/// <see cref="Label"/> and <see cref="LabelContent"/>, and exactly one of <see cref="Printer"/> and <see cref="Host"/>, must be set.
/// </summary>
public sealed class PrintJobRequest
{
    /// <summary>The .rotula file. The command line accepts any path; the REST server only a path inside one of its label folders.</summary>
    public string? Label { get; set; }

    /// <summary>The content of a .rotula file, for a caller that keeps its templates itself. The REST server accepts it only when allowed.</summary>
    public string? LabelContent { get; set; }

    /// <summary>An installed Windows printer (USB, shared or network queue).</summary>
    public string? Printer { get; set; }

    /// <summary>Host name or IP address of a network printer reached directly over TCP.</summary>
    public string? Host { get; set; }

    /// <summary>TCP port of <see cref="Host"/>; 9100 when not set.</summary>
    public int? Port { get; set; }

    /// <summary>Copies of the label (or of every record); null keeps the quantity saved in the label.</summary>
    public int? Copies { get; set; }

    /// <summary>Values for the label's fields when printing one label (field name to value).</summary>
    public Dictionary<string, string>? Values { get; set; }

    /// <summary>Several records, each a set of field values; one label (times the copies) per record. Not together with <see cref="Values"/>.</summary>
    public List<Dictionary<string, string>>? Records { get; set; }

    /// <summary>Answers to the label's print-time questions (variable name to answer), the same for every record.</summary>
    public Dictionary<string, string>? Answers { get; set; }

    /// <summary>Printhead resolution (203, 300 or 600). Null uses what the Windows driver reports, or the label's own resolution.</summary>
    public int? Dpi { get; set; }

    /// <summary>Darkness 0-30 sent before the labels; null leaves the printer as it is.</summary>
    public int? Darkness { get; set; }

    /// <summary>Print speed 1-14 inches per second; null leaves the printer as it is.</summary>
    public int? Speed { get; set; }

    /// <summary>Ask the printer for its status (~HS) before every job and do not send while it reports a fault. On by default.</summary>
    public bool CheckStatus { get; set; } = true;

    /// <summary>After the last job, ask the printer until it reports it finished or a fault. On by default.</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Build the ZPL and report it without sending anything and without using up serial numbers.</summary>
    public bool DryRun { get; set; }

    /// <summary>The caller's own reference (an order or shipment number). Echoed in the result and the log, never printed.</summary>
    public string? Reference { get; set; }
}

/// <summary>How a print request ended. The command line turns it into an exit code and the REST API into an HTTP status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrintOutcome>))]
public enum PrintOutcome
{
    /// <summary>Every label was handed to the printer (read the notices for the verification).</summary>
    Printed,
    /// <summary>A dry run: nothing was sent; the ZPL is in the result.</summary>
    DryRun,
    /// <summary>The request itself is wrong (missing printer, bad copies, unreadable data...). Nothing was sent.</summary>
    Invalid,
    /// <summary>The label has errors (or answers are missing). Nothing was sent.</summary>
    InvalidLabel,
    /// <summary>A rule of this computer or server forbids the request (policy, allow-lists). Nothing was sent.</summary>
    Refused,
    /// <summary>The printer reported a fault (paper out, head open, paused...) before a job, so it was held back.</summary>
    HeldBack,
    /// <summary>Sending failed (printer not found, offline, unreachable...). Some jobs may have gone out before; see <see cref="PrintJobResult.Jobs"/>.</summary>
    Failed,
    /// <summary>The caller cancelled; the jobs counted in the result went out.</summary>
    Cancelled,
}

/// <summary>A note about a request: a warning that did not stop it, or the reasons it was stopped.</summary>
/// <param name="Code">Stable key (<c>PrinterStatus.Verified</c>, <c>Barcode.Ean13.CheckDigit</c>...), for programs.</param>
/// <param name="Severity"><c>warning</c> or <c>error</c>.</param>
/// <param name="Message">The sentence in the requested language, for people.</param>
/// <param name="Record">1-based record the note is about, or null.</param>
public sealed record EngineNotice(string Code, string Severity, string Message, int? Record = null);

/// <summary>The outcome of one print request.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">One or two sentences for people, in the requested language.</param>
/// <param name="ErrorCode">For a failure, a stable code: the print error kind (<c>PaperOut</c>, <c>Offline</c>...) or an engine key; null on success.</param>
/// <param name="Printer">The printer as named in messages (queue name or host:port); empty when none was resolved.</param>
/// <param name="Records">Records that produced a label (1 for a single label).</param>
/// <param name="Labels">Labels sent, copies included (or that would be sent, for a dry run).</param>
/// <param name="Jobs">Print jobs handed to the printer.</param>
/// <param name="BytesSent">Bytes handed to the spooler or the socket.</param>
/// <param name="Notices">Warnings and problems, each with a code and a sentence.</param>
/// <param name="Zpl">The ZPL of a dry run; null otherwise.</param>
/// <param name="Reference">The caller's reference from the request.</param>
public sealed record PrintJobResult(PrintOutcome Outcome, string Message, string? ErrorCode, string Printer, int Records, long Labels, int Jobs,
    long BytesSent, IReadOnlyList<EngineNotice> Notices, string? Zpl, string? Reference)
{
    /// <summary>True when the labels went out (or, for a dry run, would go out).</summary>
    public bool Succeeded => Outcome is PrintOutcome.Printed or PrintOutcome.DryRun;
}

/// <summary>A printer's answer to the status question (~HS), decoded.</summary>
/// <param name="Printer">The printer as named in messages.</param>
/// <param name="Available">False when the status could not be read (see <paramref name="UnavailableReason"/>); that does not mean the printer is broken.</param>
/// <param name="Ready">True when nothing stops printing; null when unknown.</param>
/// <param name="Blocking">Conditions that stop printing (<c>PaperOut</c>, <c>HeadOpen</c>...).</param>
/// <param name="Warnings">Conditions that do not stop printing.</param>
/// <param name="LabelsRemaining">Labels the printer still has to print.</param>
/// <param name="FormatsInBuffer">Formats waiting in the printer's buffer.</param>
/// <param name="Complete">False when only part of the reply arrived.</param>
/// <param name="UnavailableReason">Why the status could not be read (<c>NoReply</c>, <c>NotSupported</c>...), or null.</param>
/// <param name="Message">The status in the requested language.</param>
public sealed record PrinterStatusReport(string Printer, bool Available, bool? Ready, IReadOnlyList<string> Blocking, IReadOnlyList<string> Warnings,
    long? LabelsRemaining, int? FormatsInBuffer, bool Complete, string? UnavailableReason, string Message);

/// <summary>An installed Windows printer as the engine lists it.</summary>
/// <param name="Name">Queue name to pass as the printer.</param>
/// <param name="Port">Windows port (USB001, IP_10.0.0.5...).</param>
/// <param name="Driver">Driver name.</param>
/// <param name="IsDefault">The Windows default printer.</param>
/// <param name="IsOffline">Windows reports it offline.</param>
/// <param name="Allowed">The REST server may print to it (always true on the command line).</param>
public sealed record PrinterEntry(string Name, string Port, string Driver, bool IsDefault, bool IsOffline, bool Allowed);
