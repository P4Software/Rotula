using System.Globalization;
using Rotula.App.Localization;
using Rotula.Core.Validation;
using Rotula.Printing;

namespace Rotula.App.Services;

/// <summary>
/// Turns the findings of the Windows-driver pre-flight (<see cref="DriverPreflight"/>) and the errors of the picture renderer
/// (<see cref="DevicePictureRenderer"/>) into plain sentences in the current language, one per finding. The Print dialog, batch printing and the
/// print station all word a finding through this class, so a person reads the same sentence wherever it appears. It only builds text: no WPF
/// types, so it can be used from any view-model.
/// <para>
/// The texts live in the resources under <c>Issue.&lt;key&gt;</c>, the convention <see cref="Loc.FormatIssue"/> already follows for every other
/// issue (for example <c>Issue.Driver.Preflight.RawBlocks</c>), so a finding that reaches a generic issue list still finds its text.
/// </para>
/// </summary>
public static class DriverPreflightGate
{
    /// <summary>Prefix shared by every message key of the picture-mode path (pre-flight findings and renderer errors).</summary>
    private const string DriverKeyPrefix = "Driver.";

    /// <summary>Heading of the findings list in the current language.</summary>
    public static string Title => Loc.Get("Print.Driver.FindingsTitle");

    /// <summary>The findings as one sentence per line, in the order the library reported them; empty when there are none.</summary>
    /// <param name="findings">The pre-flight findings (errors and warnings).</param>
    /// <exception cref="ArgumentNullException"><paramref name="findings"/> is null.</exception>
    public static string Describe(IReadOnlyList<ValidationIssue> findings) => string.Join(Environment.NewLine, Lines(findings));

    /// <summary>The findings as one sentence each, in the order the library reported them.</summary>
    /// <param name="findings">The pre-flight findings (errors and warnings).</param>
    /// <exception cref="ArgumentNullException"><paramref name="findings"/> is null.</exception>
    public static IReadOnlyList<string> Lines(IEnumerable<ValidationIssue> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.Select(FormatFinding).ToList();
    }

    /// <summary>
    /// One finding as a sentence in the current language. A key outside the picture-mode path goes through <see cref="Loc.FormatIssue"/> unchanged.
    /// For a picture-mode key the arguments, written by the library in the invariant culture (so the fingerprint never depends on the
    /// machine's language), are shown in the current language: "9,8" in Spanish, "100 × 50" for a size. Three cases pick a variant text,
    /// because one sentence cannot read right for every argument:
    /// <list type="bullet">
    /// <item><see cref="DriverPreflight.RawBlocks"/> with a count of 1 ("1 part", not "1 parts").</item>
    /// <item><see cref="DriverPreflight.ModuleBelowMinimum"/> whose actual and minimum widths print as the same one-decimal number (9.84 under
    /// 9.85 both read "9.8"): "9.8 mils, under the 9.8 mils needed" would contradict itself, so the variant says "slightly under".</item>
    /// <item><see cref="DriverPreflight.PrinterCounter"/> with an empty name (an unnamed text whose first line is empty): the variant names no field
    /// instead of showing empty quotes.</item>
    /// </list>
    /// </summary>
    /// <param name="finding">The finding.</param>
    /// <exception cref="ArgumentNullException"><paramref name="finding"/> is null.</exception>
    public static string FormatFinding(ValidationIssue finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (!finding.MessageKey.StartsWith(DriverKeyPrefix, StringComparison.Ordinal)) return Loc.FormatIssue(finding);
        string key = VariantKey(finding) ?? "Issue." + finding.MessageKey;
        // A key this version has no text for (a newer library) still gets the generic "problem found" sentence, never a raw key.
        if (!Loc.Instance.HasKey(key)) return Loc.FormatIssue(finding);
        return Loc.Get(key, DisplayArgs(finding));
    }

    /// <summary>The resource key of the variant text <paramref name="finding"/> needs, or null for its ordinary text.</summary>
    private static string? VariantKey(ValidationIssue finding)
    {
        var args = finding.Args;
        return finding.MessageKey switch
        {
            DriverPreflight.RawBlocks when args.Count >= 1 && args[0] == "1" => "Issue." + DriverPreflight.RawBlocks + ".One",
            DriverPreflight.ModuleBelowMinimum when args.Count >= 3 && string.Equals(args[1], args[2], StringComparison.Ordinal)
                => "Issue." + DriverPreflight.ModuleBelowMinimum + ".Slightly",
            DriverPreflight.PrinterCounter when args.Count == 0 || string.IsNullOrWhiteSpace(args[0]) => "Issue." + DriverPreflight.PrinterCounter + ".NoName",
            _ => null,
        };
    }

    /// <summary>
    /// The arguments of a picture-mode finding as they are shown: numbers in the current language, sizes written <c>W × H</c>. Arguments are
    /// padded to three so a text never shows an unreplaced hole when the library sent fewer. An argument that does not parse is shown as sent.
    /// </summary>
    private static object[] DisplayArgs(ValidationIssue finding)
    {
        var shown = finding.Args.Select(a => (object)a).ToList();
        switch (finding.MessageKey)
        {
            case DriverPreflight.ModuleBelowMinimum:
                // Barcode name, then the actual and the minimum module width in mils (always one decimal in the library).
                for (int i = 1; i < shown.Count && i <= 2; i++) shown[i] = Decimal(finding.Args[i], "0.0");
                break;
            case DriverPreflight.LabelLargerThanPaper or DriverPreflight.PaperMismatch or DriverPreflight.LabelTurned:
                // Label size, then the printable area (for a turned label: the label as designed, then the paper it lies on), both "WxH" in
                // millimetres with at most one decimal.
                for (int i = 0; i < shown.Count && i <= 1; i++) shown[i] = Size(finding.Args[i]);
                break;
            case DriverPreflight.MarginBand:
                // Label size and printable area as above, then the widest unprintable edge the library worked out (millimetres, one decimal).
                // Only shown here, never computed: every value of the text travels in the finding, so any formatter can word it.
                for (int i = 0; i < shown.Count && i <= 1; i++) shown[i] = Size(finding.Args[i]);
                if (shown.Count >= 3) shown[2] = Decimal(finding.Args[2], "0.0");
                break;
            case DriverPreflight.TooLarge or DriverPreflight.TooManyLabels:
                // Pixels of the picture, then the most Rotula draws; or the labels asked for, then the most one job may print: whole numbers,
                // grouped for reading ("250,000" in English, "250.000" in Spanish).
                for (int i = 0; i < shown.Count && i <= 1; i++) shown[i] = Whole(finding.Args[i]);
                break;
        }
        while (shown.Count < 3) shown.Add(string.Empty);
        return shown.ToArray();
    }

    private static string Decimal(string value, string format) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number.ToString(format, Loc.Instance.Culture)
            : value;

    private static string Whole(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
            ? number.ToString("N0", Loc.Instance.Culture)
            : value;

    private static string Size(string value)
    {
        string[] parts = value.Split('x');
        if (parts.Length != 2) return value;
        return Decimal(parts[0], "0.#") + " × " + Decimal(parts[1], "0.#");
    }
}
