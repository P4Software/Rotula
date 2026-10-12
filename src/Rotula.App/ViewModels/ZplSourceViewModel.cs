using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Editing;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Core.Model;
using Rotula.Core.Zpl.Catalog;
using Rotula.Core.Zpl.Import;

namespace Rotula.App.ViewModels;

/// <summary>Where the text of the ZPL panel comes from.</summary>
public enum ZplSourceState
{
    /// <summary>The text was generated from the design; the design is the source of truth.</summary>
    Generated,

    /// <summary>The user changed the text and the quiet period before it is re-imported has not passed yet.</summary>
    Pending,

    /// <summary>The design was rebuilt from the text the user typed; the text is kept as typed, not regenerated.</summary>
    Edited,
}

/// <summary>
/// The editable ZPL source panel. The text normally mirrors the design (<see cref="SetGenerated"/>); when the user types, the label is
/// re-imported through the ZPL parser once typing pauses (<see cref="Delay"/>), as one undo step, and the text stays as typed until the
/// design changes from somewhere else. Two guards stop the loop between the two directions: while the panel applies its own text the
/// regenerated text is ignored, and while an edit is pending nothing overwrites what is being typed. A hint line explains the
/// command under the caret and a status line reports what the importer could not model.
/// </summary>
public sealed partial class ZplSourceViewModel : ObservableObject
{
    /// <summary>Quiet period after the last keystroke before the text is re-imported.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    /// <summary>How many unsupported command names the status line spells out before "and more".</summary>
    public const int MaxNamesInStatus = 3;

    private readonly EditorSession _session;
    private readonly IDebouncer _debouncer;
    private readonly Func<IReadOnlyDictionary<string, string>?> _previewData;
    private readonly Action _regenerate;
    private bool _settingText;
    private bool _applying;

    /// <summary>Creates the panel view-model.</summary>
    /// <param name="session">The editing session the text describes and edits.</param>
    /// <param name="debouncer">Runs the re-import after the quiet period.</param>
    /// <param name="previewData">The record shown on the canvas (its values are in the generated text), or null for sample values.</param>
    /// <param name="regenerate">Produces the text for the current design again (the owner calls <see cref="SetGenerated"/> from it).</param>
    public ZplSourceViewModel(EditorSession session, IDebouncer debouncer, Func<IReadOnlyDictionary<string, string>?> previewData, Action regenerate)
    {
        _session = session;
        _debouncer = debouncer;
        _previewData = previewData;
        _regenerate = regenerate;
        _statusText = Loc.Get("Zpl.Status.Generated");
        _hintText = Loc.Get("Zpl.Hint.Idle");
    }

    /// <summary>The ZPL text shown in the editor. Set by the user (typing) or by <see cref="SetGenerated"/>.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Whether the design or the text is the source of truth right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsEdited))]
    [NotifyCanExecuteChangedFor(nameof(RevertToDesignCommand))]
    private ZplSourceState _state;

    /// <summary>One line about the last re-import: what was understood and what was kept as raw ZPL.</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>Every notice of the last re-import, one per line (the tooltip of the status line); empty when there were none.</summary>
    [ObservableProperty]
    private string _statusDetail = string.Empty;

    /// <summary>One line about the command under the caret: id, parameters and name, then what the parameters mean.</summary>
    [ObservableProperty]
    private string _hintText;

    /// <summary>The full explanation of the command under the caret (the tooltip of the hint line).</summary>
    [ObservableProperty]
    private string _hintDetail = string.Empty;

    /// <summary>Zero-based caret position in <see cref="Text"/>; the view keeps it current so the hint follows the caret.</summary>
    [ObservableProperty]
    private int _caretIndex;

    /// <summary>The notices of the last re-import, for callers that want the structured form.</summary>
    public IReadOnlyList<ZplImportWarning> LastWarnings { get; private set; } = Array.Empty<ZplImportWarning>();

    /// <summary>What the last re-import carried over from the previous design, or null before any re-import.</summary>
    public CarryOverReport? LastCarryOver { get; private set; }

    /// <summary>True when the text is not (or not yet) the generated one, so "Revert to design" has something to do.</summary>
    public bool IsEdited => State != ZplSourceState.Generated;

    /// <summary>The state in words, for the badge next to the panel title.</summary>
    public string StateText => Loc.Get(State switch
    {
        ZplSourceState.Generated => "Zpl.State.Generated",
        ZplSourceState.Pending => "Zpl.State.Pending",
        _ => "Zpl.State.Edited",
    });

    /// <summary>
    /// Shows text generated from the design. Ignored while the panel is applying its own text (the design just changed because of it) and
    /// while an edit is pending (the user is typing), so neither direction can overwrite the other.
    /// </summary>
    public void SetGenerated(string zpl)
    {
        if (_applying || State == ZplSourceState.Pending) return;
        SetTextSilently(zpl);
        State = ZplSourceState.Generated;
        StatusText = Loc.Get("Zpl.Status.Generated");
        StatusDetail = string.Empty;
        UpdateHint();
    }

    private void SetTextSilently(string value)
    {
        _settingText = true;
        try { Text = value; }
        finally { _settingText = false; }
    }

    partial void OnTextChanged(string value)
    {
        if (_settingText) return;
        State = ZplSourceState.Pending;
        StatusText = Loc.Get("Zpl.Status.Pending");
        StatusDetail = string.Empty;
        _debouncer.Restart(Delay, ApplyPending);
        UpdateHint();
    }

    partial void OnCaretIndexChanged(int value) => UpdateHint();

    /// <summary>Re-imports the typed text now, without waiting for the quiet period. Does nothing unless an edit is pending.</summary>
    public void ApplyPending()
    {
        if (State != ZplSourceState.Pending) return;
        _debouncer.Cancel();
        ApplyText(Text);
    }

    /// <summary>Throws the typed text away and shows the text generated from the design again.</summary>
    [RelayCommand(CanExecute = nameof(IsEdited))]
    private void RevertToDesign()
    {
        _debouncer.Cancel();
        State = ZplSourceState.Generated;
        _regenerate();
    }

    /// <summary>Rebuilds the texts that are translated (state badge, status, hint) after a language change.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StateText));
        if (State == ZplSourceState.Generated) StatusText = Loc.Get("Zpl.Status.Generated");
        UpdateHint();
    }

    /// <summary>
    /// Replaces the design with the label read from <paramref name="text"/>, as one undo step. The label keeps its resolution, identity,
    /// stock, declared variable definitions and the designer-only settings of elements that are still there (see <see cref="DesignCarryOver"/>);
    /// ZPL carries none of those. An empty text is not an empty label: it is reported and nothing changes.
    /// </summary>
    private void ApplyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            State = ZplSourceState.Edited;
            StatusText = Loc.Get("Zpl.Status.Empty");
            StatusDetail = string.Empty;
            return;
        }

        var current = _session.Document;
        var result = ZplParser.Parse(text, new ZplParseOptions { Dpi = current.Dpi });
        LastWarnings = result.Warnings;
        var parsed = result.Document;
        LastCarryOver = DesignCarryOver.Apply(current, parsed, _previewData());

        _applying = true;
        try
        {
            _session.Edit("Undo.ZplSource", d =>
            {
                string? id = d.Id, stockId = d.StockId;
                var dpi = d.Dpi;
                var width = d.Width;
                var height = d.Height;
                bool sameSize = d.WidthDots == parsed.WidthDots && d.HeightDots == parsed.HeightDots;
                var definitions = d.Variables.Select(v => v.Clone()).ToList();

                ElementCloner.CopySettings(parsed, d);
                // Why these are restored: the text cannot say them. The size keeps its unit when it did not change, so "100 mm" does not become "800 dots".
                d.Id = id;
                d.Dpi = dpi;
                d.StockId = sameSize ? stockId : null;
                if (sameSize) { d.Width = width; d.Height = height; }
                // The declared variables (dates, counters, prompts, computed values) stay; the text can only add plain ones from numbered fields.
                d.Variables.Clear();
                d.Variables.AddRange(definitions);
                foreach (var v in parsed.Variables)
                    if (!d.Variables.Any(x => x.Name == v.Name)) d.Variables.Add(v.Clone());
                d.Elements.Clear();
                d.Elements.AddRange(parsed.Elements);
            }, selectionAfter: () => Array.Empty<string>());
        }
        finally { _applying = false; }

        State = ZplSourceState.Edited;
        DescribeResult(result);
        UpdateHint();
    }

    /// <summary>Builds the status line: the count and first names of the commands kept as raw ZPL, then the other notices.</summary>
    private void DescribeResult(ZplParseResult result)
    {
        var unsupported = result.Warnings.Where(w => w.KeptAsRaw).ToList();
        var names = unsupported.Select(w => w.Command ?? w.Args.FirstOrDefault() ?? "?").Distinct(StringComparer.Ordinal).ToList();
        string shown = string.Join(", ", names.Take(MaxNamesInStatus)) + (names.Count > MaxNamesInStatus ? ", " + Loc.Get("Zpl.Status.More", names.Count - MaxNamesInStatus) : string.Empty);
        int other = result.Warnings.Count - unsupported.Count;

        var parts = new List<string>(3);
        if (result.Documents.Count > 1) parts.Add(Loc.Get("Zpl.Status.MultipleLabels", result.Documents.Count));
        parts.Add(unsupported.Count switch
        {
            0 => Loc.Get("Zpl.Status.Clean"),
            1 => Loc.Get("Zpl.Status.Kept.One", shown),
            _ => Loc.Get("Zpl.Status.Kept.Many", unsupported.Count, shown),
        });
        if (other > 0) parts.Add(Loc.Get(other == 1 ? "Zpl.Status.Notices.One" : "Zpl.Status.Notices.Many", other));
        if (LastCarryOver is { RestoredBindings: > 0 } carry) parts.Add(Loc.Get("Zpl.Status.Rebound", carry.RestoredBindings));
        StatusText = string.Join(" · ", parts);
        StatusDetail = string.Join(Environment.NewLine, result.Warnings.Select(CoreZplImporter.Describe));
    }

    /// <summary>Explains the command under the caret, or says that the caret is on none / on one Rotula does not know.</summary>
    private void UpdateHint()
    {
        string? token = ZplCommandReference.CommandTokenAtCaret(Text, CaretIndex);
        if (token is null)
        {
            HintText = Loc.Get("Zpl.Hint.Idle");
            HintDetail = string.Empty;
            return;
        }
        var hint = ZplCommandReference.Describe(token);
        if (hint is null)
        {
            HintText = Loc.Get("Zpl.Hint.Unknown", token);
            HintDetail = string.Empty;
            return;
        }
        string kept = hint.Status is ZplCommandStatus.Supported or null ? string.Empty : " · " + Loc.Get("Zpl.Hint.Kept");
        HintText = hint.Headline + " — " + (hint.Parameters.Count == 0 ? hint.Summary : hint.ParameterText) + kept;
        var lines = new List<string> { hint.Summary };
        lines.AddRange(hint.Parameters.Select(p => p.Name + ": " + p.Meaning));
        if (hint.Note is not null) lines.Add(hint.Note);
        HintDetail = string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
    }
}
