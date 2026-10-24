using Rotula.App.Localization;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.App.ViewModels;

/// <summary>Turns the stable keys of Rotula.Printing into plain-language sentences in the current language.</summary>
public static class PrintMessages
{
    /// <summary>Describes a failed print result (what happened and what to do next).</summary>
    public static string Describe(PrintError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        object[] args = error.Args.Cast<object>().ToArray();
        switch (error.Kind)
        {
            case PrintErrorKind.InvalidProfile:
                // The arguments are Print.Profile.* keys; list each as its own sentence.
                return Loc.Get(error.Key) + " " + string.Join(" ", error.Args.Where(Loc.Instance.HasKey).Select(k => Loc.Get(k)));
            case PrintErrorKind.Unknown:
                // The detail differs per transport (queue, code, host...), so it is joined into one argument.
                args = new object[] { string.Join(", ", error.Args.Where(a => a.Length > 0)) };
                break;
            case PrintErrorKind.HeadOpen or PrintErrorKind.PaperOut or PrintErrorKind.RibbonOut or PrintErrorKind.HeadTooHot
                or PrintErrorKind.DiagnosticMode or PrintErrorKind.BufferFull or PrintErrorKind.Paused:
            {
                // Arguments: the printer, then every condition it reported. The message names the worst one; the others follow.
                string main = Loc.Get(error.Key, error.Args.Count > 0 ? error.Args[0] : string.Empty, string.Empty);
                var others = error.Args.Skip(1).Where(c => !string.Equals(c, error.Kind.ToString(), StringComparison.Ordinal)).ToList();
                return others.Count == 0 ? main : main + " " + Loc.Get("Print.Status.AlsoReports", Conditions(others));
            }
        }
        // Messages may have up to two holes; pad so that a short argument list never throws.
        if (args.Length < 2) args = args.Concat(Enumerable.Repeat((object)string.Empty, 2 - args.Length)).ToArray();
        return Loc.Instance.HasKey(error.Key) ? Loc.Get(error.Key, args) : Loc.Get("Print.Error.Unknown", string.Join(", ", error.Args));
    }

    /// <summary>Describes a print result: null on success.</summary>
    public static string? Describe(PrintResult result) => result.Error is null ? null : Describe(result.Error);

    /// <summary>
    /// Lists every note a print result carries (skipped serial numbers, a counter file problem, the errors that stopped a label), one per line;
    /// empty when there are none.
    /// </summary>
    public static string DescribeNotices(PrintResult result) =>
        string.Join(Environment.NewLine, result.Notices.Select(DescribeNotice));

    /// <summary>
    /// One note of a print result. The printer status notes carry condition and reason names, which are translated here into a phrase;
    /// every other note goes through the usual issue text.
    /// </summary>
    public static string DescribeNotice(ValidationIssue notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        string? arg = notice.MessageKey switch
        {
            PrinterStatusKeys.Warning or PrinterStatusKeys.FaultAfterJob => Conditions(notice.Args),
            PrinterStatusKeys.NotChecked or PrinterStatusKeys.NotVerified => Reason(notice.Args.FirstOrDefault()),
            PrinterStatusKeys.Verified => string.Empty,
            _ => null,
        };
        return arg is null ? Loc.FormatIssue(notice) : Loc.Get("Issue." + notice.MessageKey, arg);
    }

    /// <summary>True when a print result says the printer reported a fault after the job (the result line is then shown as a problem).</summary>
    public static bool HasFaultAfterJob(PrintResult result) => result.Notices.Any(n => n.MessageKey == PrinterStatusKeys.FaultAfterJob);

    /// <summary>
    /// Describes a decoded printer status for the "Check printer status" button: ready, or what stops it, plus what it is doing. Returns
    /// whether the status is a problem (blocked, or unknown) so the view can colour it.
    /// </summary>
    public static (string Text, bool IsProblem) DescribeStatus(PrinterStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.IsAvailable) return (Loc.Get("Print.Status.Unavailable", Reason(status.Reason!.Value.ToString())), true);
        var lines = new List<string>();
        lines.Add(status.IsBlocked ? Loc.Get("Print.Status.Blocked", Conditions(status.Blocking.Select(c => c.ToString())))
            : Loc.Get("Print.Status.Ready"));
        if (status.Warnings.Count > 0) lines.Add(Loc.Get("Print.Status.Warnings", Conditions(status.Warnings.Select(c => c.ToString()))));
        if (status.LabelsRemaining is > 0 || status.FormatsInBuffer is > 0)
            lines.Add(Loc.Get("Print.Status.Busy", status.LabelsRemaining ?? 0, status.FormatsInBuffer ?? 0));
        if (status.PrintMode is { } mode && status.ThermalTransfer is { } transfer && status.LabelLengthDots is { } length)
            lines.Add(Loc.Get("Print.Status.Details", Loc.Get("Enum.PrinterPrintMode." + mode),
                Loc.Get(transfer ? "Print.Status.ThermalTransfer" : "Print.Status.DirectThermal"), length));
        if (!status.IsComplete) lines.Add(Loc.Get("Print.Status.Partial"));
        return (string.Join(Environment.NewLine, lines), status.IsBlocked);
    }

    /// <summary>Joins condition names as a phrase ("out of labels, paused"); a name this version does not know is shown as it is.</summary>
    private static string Conditions(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => Loc.Instance.HasKey("Enum.PrinterCondition." + n) ? Loc.Get("Enum.PrinterCondition." + n) : n));

    private static string Reason(string? name) =>
        name is not null && Loc.Instance.HasKey("Print.Status.Reason." + name) ? Loc.Get("Print.Status.Reason." + name) : Loc.Get("Print.Status.Reason.Unknown");

    /// <summary>Lists the error-level problems of a label as short sentences (at most <paramref name="max"/>).</summary>
    public static string DescribeIssues(IEnumerable<ValidationIssue> issues, int max = 6) =>
        string.Join(Environment.NewLine, issues.Where(i => i.Severity == ValidationSeverity.Error).Take(max).Select(Loc.FormatIssue));

    /// <summary>
    /// Describes a note raised by the automatic resolution conversion. A <c>Dpi.*</c> key this version has no text for still gets a
    /// safe sentence, so a newer Core never shows a raw key to the user.
    /// </summary>
    public static string DescribeDpiIssue(ValidationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return Loc.Instance.HasKey("Issue." + issue.MessageKey)
            ? Loc.FormatIssue(issue)
            : Loc.Get("Print.DpiIssue.Generic");
    }
}
