namespace Rotula.Files.Station;

/// <summary>The key that ended an entry: keyboard-wedge scanners send their code followed by Enter or Tab.</summary>
public enum ScanTerminator
{
    /// <summary>Enter (the usual scanner suffix).</summary>
    Enter,
    /// <summary>Tab.</summary>
    Tab,
    /// <summary>Shift+Tab: the operator goes back one field.</summary>
    ShiftTab,
}

/// <summary>What the station does after a terminator.</summary>
public enum ScanStepKind
{
    /// <summary>Stay in the field (its answer was refused; the next scan replaces it).</summary>
    Stay,
    /// <summary>Move the keyboard to the field <see cref="ScanStep.FieldIndex"/>.</summary>
    MoveTo,
    /// <summary>The last field is done: move the keyboard to the quantity and the Print button, without printing.</summary>
    FocusPrint,
    /// <summary>The last field is done and "print on last scan" is on: print now.</summary>
    Print,
}

/// <summary>One decision of the <see cref="ScanSequencer"/>.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="FieldIndex">The field to focus (for <see cref="ScanStepKind.MoveTo"/> and <see cref="ScanStepKind.Stay"/>), otherwise -1.</param>
public readonly record struct ScanStep(ScanStepKind Kind, int FieldIndex = -1);

/// <summary>Outcome of a scan on the label list.</summary>
public enum LabelScanKind
{
    /// <summary>Nothing was scanned (an empty entry).</summary>
    None,
    /// <summary>The code is the key of a label, or the only label the search finds: open it.</summary>
    Open,
    /// <summary>No label has this key and the search does not narrow the list to one: say so and keep the list.</summary>
    Unknown,
}

/// <summary>
/// The scanner logic of the print station, without any window: keyboard-wedge scanners type the code and end it with Enter or Tab, which is all
/// the station relies on (no timing guesses). The window reports each terminator; this class decides where the keyboard goes next and when to print.
/// </summary>
/// <remarks>
/// <para><b>Questions.</b> Enter or Tab in field <c>i</c> moves to field <c>i + 1</c>; Shift+Tab moves back. A refused answer keeps the keyboard in its
/// field so the operator can scan again. On the last field the station prints at once when "print on last scan" is on; otherwise the keyboard moves to
/// the Print button, and the operator presses it (or Enter there).</para>
/// <para><b>Choosing a label.</b> On the label list, a scan is matched against the labels' keys (ignoring case and surrounding spaces). With
/// "scan to select" off, or no key matching, a search that leaves exactly one label opens it as well, so typing a name and pressing Enter works too.</para>
/// </remarks>
public sealed class ScanSequencer
{
    /// <summary>Creates the sequencer.</summary>
    /// <param name="printOnLastScan">The terminator of the last question prints at once.</param>
    /// <param name="scanToSelect">Scanning a label's key on the label list opens the label.</param>
    public ScanSequencer(bool printOnLastScan, bool scanToSelect)
    {
        PrintOnLastScan = printOnLastScan;
        ScanToSelect = scanToSelect;
    }

    /// <summary>The terminator of the last question prints at once.</summary>
    public bool PrintOnLastScan { get; }

    /// <summary>Scanning a label's key on the label list opens the label.</summary>
    public bool ScanToSelect { get; }

    /// <summary>Decides what happens after a terminator in a question field.</summary>
    /// <param name="fieldIndex">The field the terminator was typed in (0-based).</param>
    /// <param name="fieldCount">How many question fields the label has.</param>
    /// <param name="terminator">The key that ended the entry.</param>
    /// <param name="answerAccepted">False when the answer of this field breaks the label's own rules (required, length, pattern...).</param>
    public ScanStep Next(int fieldIndex, int fieldCount, ScanTerminator terminator, bool answerAccepted)
    {
        if (fieldCount <= 0) return new ScanStep(PrintOnLastScan && terminator == ScanTerminator.Enter ? ScanStepKind.Print : ScanStepKind.FocusPrint);
        fieldIndex = Math.Clamp(fieldIndex, 0, fieldCount - 1);
        if (terminator == ScanTerminator.ShiftTab) return new ScanStep(ScanStepKind.MoveTo, Math.Max(0, fieldIndex - 1));
        // A refused scan stays where it is: moving on would leave a wrong value behind a field the operator no longer looks at.
        if (!answerAccepted) return new ScanStep(ScanStepKind.Stay, fieldIndex);
        if (fieldIndex < fieldCount - 1) return new ScanStep(ScanStepKind.MoveTo, fieldIndex + 1);
        return new ScanStep(PrintOnLastScan ? ScanStepKind.Print : ScanStepKind.FocusPrint);
    }

    /// <summary>
    /// Decides which label a scan (or a typed search ended with Enter) on the label list opens.
    /// </summary>
    /// <typeparam name="T">The label type.</typeparam>
    /// <param name="code">What was scanned or typed.</param>
    /// <param name="labels">Every label of the station.</param>
    /// <param name="keyOf">The scan key of a label (null when it has none).</param>
    /// <param name="searchHits">The labels the search box shows for <paramref name="code"/>.</param>
    /// <param name="match">The label to open, when the result is <see cref="LabelScanKind.Open"/>.</param>
    public LabelScanKind SelectLabel<T>(string? code, IEnumerable<T> labels, Func<T, string?> keyOf, IReadOnlyList<T> searchHits, out T? match)
        where T : class
    {
        match = null;
        string scanned = Clean(code);
        if (scanned.Length == 0) return LabelScanKind.None;
        if (ScanToSelect)
        {
            match = labels.FirstOrDefault(l => keyOf(l) is { } key && string.Equals(Clean(key), scanned, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return LabelScanKind.Open;
        }
        if (searchHits.Count == 1)
        {
            match = searchHits[0];
            return LabelScanKind.Open;
        }
        return LabelScanKind.Unknown;
    }

    /// <summary>
    /// What a scanner typed, without the characters a scanner may add around the code (control characters such as a stray CR or a GS separator at the
    /// ends) and surrounding spaces. Inner GS separators of a GS1 code are kept.
    /// </summary>
    public static string Clean(string? scanned)
    {
        if (string.IsNullOrEmpty(scanned)) return string.Empty;
        int start = 0, end = scanned.Length;
        while (start < end && (char.IsControl(scanned[start]) || char.IsWhiteSpace(scanned[start]))) start++;
        while (end > start && (char.IsControl(scanned[end - 1]) || char.IsWhiteSpace(scanned[end - 1]))) end--;
        return scanned[start..end];
    }
}
