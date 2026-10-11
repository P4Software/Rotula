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
        string.Join(Environment.NewLine, result.Notices.Select(Loc.FormatIssue));

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
