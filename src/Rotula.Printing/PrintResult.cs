namespace Rotula.Printing;

/// <summary>Stable categories of printing failures. The UI maps <see cref="PrintError.Key"/> to localised text.</summary>
public enum PrintErrorKind
{
    /// <summary>The Windows printer name does not exist.</summary>
    PrinterNotFound,
    /// <summary>The user may not print to this printer.</summary>
    AccessDenied,
    /// <summary>The printer is installed but offline (switched off, unplugged, paused by the driver).</summary>
    Offline,
    /// <summary>Connecting or sending did not finish in time.</summary>
    Timeout,
    /// <summary>The host answered but nothing listens on the port.</summary>
    ConnectionRefused,
    /// <summary>The host name does not resolve or the network route is missing.</summary>
    HostUnreachable,
    /// <summary>The operation was cancelled by the caller.</summary>
    Cancelled,
    /// <summary>The label has validation errors and was not sent.</summary>
    InvalidLabel,
    /// <summary>The printer profile has out-of-range values.</summary>
    InvalidProfile,
    /// <summary>The text cannot be represented in the label's legacy code page.</summary>
    EncodingFailed,
    /// <summary>Spooler printing is only available on Windows.</summary>
    Unsupported,
    /// <summary>Any other failure; the arguments carry the technical detail.</summary>
    Unknown,
    /// <summary>The status check found the printhead open; nothing was sent. Arguments: printer, then every reported condition name.</summary>
    HeadOpen,
    /// <summary>The status check found the printer out of labels; nothing was sent. Arguments as for <see cref="HeadOpen"/>.</summary>
    PaperOut,
    /// <summary>The status check found the ribbon out; nothing was sent. Arguments as for <see cref="HeadOpen"/>.</summary>
    RibbonOut,
    /// <summary>The status check found the printhead too hot; nothing was sent. Arguments as for <see cref="HeadOpen"/>.</summary>
    HeadTooHot,
    /// <summary>The status check found communications diagnostics mode on (the job would print as a data dump); nothing was sent.</summary>
    DiagnosticMode,
    /// <summary>The status check found the printer's receive buffer full; nothing was sent. Arguments as for <see cref="HeadOpen"/>.</summary>
    BufferFull,
    /// <summary>The status check found the printer paused; nothing was sent. Arguments as for <see cref="HeadOpen"/>.</summary>
    Paused,
}

/// <summary>
/// A printing failure: a stable message key plus positional arguments, so the UI can localise it
/// (this library contains no user-facing text).
/// </summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Key">Resource key, always <c>Print.Error.&lt;Kind&gt;</c>.</param>
/// <param name="Args">Values for the placeholders of the localised message (printer name, host, port, detail...).</param>
public sealed record PrintError(PrintErrorKind Kind, string Key, IReadOnlyList<string> Args)
{
    /// <summary>Creates an error whose key is derived from <paramref name="kind"/>.</summary>
    public static PrintError Create(PrintErrorKind kind, params string[] args) => new(kind, "Print.Error." + kind, args);

    /// <summary>
    /// The error for a printer whose status blocks printing: the kind of its most serious condition, then the printer name and the names of
    /// every reported condition as arguments, so the UI can mention the others too. Null when nothing blocks.
    /// </summary>
    public static PrintError? FromStatus(PrinterStatus status, string printer)
    {
        ArgumentNullException.ThrowIfNull(status);
        var blocking = status.Blocking;
        if (blocking.Count == 0) return null;
        var kind = blocking[0] switch
        {
            PrinterCondition.HeadOpen => PrintErrorKind.HeadOpen,
            PrinterCondition.PaperOut => PrintErrorKind.PaperOut,
            PrinterCondition.RibbonOut => PrintErrorKind.RibbonOut,
            PrinterCondition.HeadTooHot => PrintErrorKind.HeadTooHot,
            PrinterCondition.DiagnosticMode => PrintErrorKind.DiagnosticMode,
            PrinterCondition.BufferFull => PrintErrorKind.BufferFull,
            _ => PrintErrorKind.Paused,
        };
        return Create(kind, [printer ?? string.Empty, .. status.Conditions.Select(c => c.ToString())]);
    }
}

/// <summary>Outcome of a print operation. Failures are values, never exceptions, so callers need no try/catch.</summary>
public sealed class PrintResult
{
    private PrintResult(int bytesSent, PrintError? error)
    {
        BytesSent = bytesSent;
        Error = error;
    }

    /// <summary>True when every byte was handed to the spooler or the socket.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Number of bytes sent (also set on failure, for the part that went out).</summary>
    public int BytesSent { get; }

    /// <summary>The failure, or null on success.</summary>
    public PrintError? Error { get; }

    /// <summary>
    /// Notes the operator should read although printing may have worked: serial numbers skipped because an earlier run never finished, a printer
    /// clock that was replaced by the computer clock, a counter file that could not be updated. Empty for most results.
    /// </summary>
    public IReadOnlyList<Rotula.Core.Validation.ValidationIssue> Notices { get; private init; } = Array.Empty<Rotula.Core.Validation.ValidationIssue>();

    /// <summary>A copy of this result that also carries <paramref name="notices"/>.</summary>
    public PrintResult WithNotices(IReadOnlyList<Rotula.Core.Validation.ValidationIssue> notices) =>
        notices.Count == 0 ? this : new PrintResult(BytesSent, Error) { Notices = notices };

    /// <summary>Creates a success result.</summary>
    public static PrintResult Ok(int bytesSent) => new(bytesSent, null);

    /// <summary>Creates a failure result.</summary>
    public static PrintResult Fail(PrintError error, int bytesSent = 0) => new(bytesSent, error);

    /// <summary>Creates a failure result from a kind and arguments.</summary>
    public static PrintResult Fail(PrintErrorKind kind, params string[] args) => new(0, PrintError.Create(kind, args));
}
