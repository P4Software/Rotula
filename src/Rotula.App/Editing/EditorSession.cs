using Rotula.Core.Elements;
using Rotula.Core.Model;

namespace Rotula.App.Editing;

/// <summary>
/// The document being edited together with its selection and undo history. Every change to the document goes through
/// <see cref="Edit"/> (or a gesture) so it is undoable and so the preview cache knows when to rebuild.
/// This type has no WPF dependency, which keeps the editing rules unit-testable.
/// </summary>
public sealed class EditorSession
{
    private List<string> _selection = new();
    private int _savedToken;

    /// <summary>Creates a session around a document (a default 100 x 150 mm label when omitted).</summary>
    public EditorSession(LabelDocument? document = null, Func<DateTime>? clock = null)
    {
        Document = document ?? new LabelDocument();
        Undo = new UndoStack(clock);
        Bounds = new ElementBoundsCalculator(() => Document);
        Undo.Changed += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The document being edited. The reference changes when another document is loaded.</summary>
    public LabelDocument Document { get; private set; }

    /// <summary>Undo/redo history.</summary>
    public UndoStack Undo { get; }

    /// <summary>Measures elements on the label (used for hit-testing, handles, snapping and alignment).</summary>
    public ElementBoundsCalculator Bounds { get; }

    /// <summary>
    /// Increases whenever the document content changes in any way. The preview cache compares it to decide whether
    /// the cached render of the label is still valid.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>Ids of the selected elements, in selection order.</summary>
    public IReadOnlyList<string> SelectedIds => _selection;

    /// <summary>The selected elements in z-order.</summary>
    public IReadOnlyList<LabelElement> SelectedElements => Document.Elements.Where(e => _selection.Contains(e.Id)).ToList();

    /// <summary>True when the document differs from the last saved state.</summary>
    public bool IsDirty => Undo.StateToken != _savedToken;

    /// <summary>Raised after the document changed (any edit, undo, redo or load).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>
    /// Raised on every change of the document content, including the intermediate states of a drag. The canvas listens to
    /// this one to repaint; everything expensive (ZPL, validation, layers) listens to <see cref="DocumentChanged"/> instead.
    /// </summary>
    public event EventHandler? LiveChanged;

    /// <summary>Raised after the selection changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when <see cref="IsDirty"/> may have changed.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Marks the current state as saved.</summary>
    public void MarkSaved()
    {
        _savedToken = Undo.StateToken;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the document (New / Open / Import). History and selection are cleared. The result counts as saved unless
    /// <paramref name="markDirty"/> is set (an imported design has no file yet).
    /// </summary>
    public void Load(LabelDocument document, bool markDirty = false)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Undo.Clear();
        _savedToken = markDirty ? -1 : 0;
        _selection = new List<string>();
        Revision++;
        LiveChanged?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----------------------------------------------------------------------------------- selection

    /// <summary>Replaces the selection. Unknown ids are ignored.</summary>
    public void SetSelection(IEnumerable<string> ids)
    {
        var valid = new HashSet<string>(Document.Elements.Select(e => e.Id));
        var next = ids.Where(valid.Contains).Distinct().ToList();
        if (next.SequenceEqual(_selection)) return;
        _selection = next;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects exactly one element, or clears the selection when null.</summary>
    public void Select(LabelElement? element) => SetSelection(element is null ? Array.Empty<string>() : new[] { element.Id });

    /// <summary>Adds or removes one element from the selection (Ctrl+click).</summary>
    public void ToggleSelection(LabelElement element)
    {
        var next = _selection.ToList();
        if (!next.Remove(element.Id)) next.Add(element.Id);
        SetSelection(next);
    }

    /// <summary>Selects every element.</summary>
    public void SelectAll() => SetSelection(Document.Elements.Select(e => e.Id));

    /// <summary>True when the element is selected.</summary>
    public bool IsSelected(LabelElement e) => _selection.Contains(e.Id);

    // ----------------------------------------------------------------------------------- editing

    /// <summary>
    /// Applies a change as one undoable step.
    /// </summary>
    /// <param name="descriptionKey">Resource key describing the step ("Undo.Move").</param>
    /// <param name="change">The mutation; it may also change the selection through <paramref name="selectionAfter"/>.</param>
    /// <param name="mergeKey">Optional key that lets rapid repeated steps collapse into one.</param>
    /// <param name="selectionAfter">Selection to apply afterwards; null keeps the current one.</param>
    public void Edit(string descriptionKey, Action<LabelDocument> change, string? mergeKey = null, Func<IEnumerable<string>>? selectionAfter = null)
    {
        var before = ElementCloner.CloneDocument(Document);
        var selBefore = _selection.ToList();
        change(Document);
        Commit(descriptionKey, mergeKey, before, selBefore, selectionAfter?.Invoke());
    }

    /// <summary>
    /// Starts a live gesture (drag, resize). The caller mutates elements directly and calls <see cref="Touch"/> for each
    /// intermediate state, then <see cref="EndGesture"/> or <see cref="CancelGesture"/>.
    /// </summary>
    public GestureToken BeginGesture() => new(ElementCloner.CloneDocument(Document), _selection.ToList());

    /// <summary>Signals that the document changed during a gesture (so the preview refreshes) without creating an undo step.</summary>
    public void Touch()
    {
        Revision++;
        LiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finishes a gesture as one undo step.</summary>
    public void EndGesture(GestureToken token, string descriptionKey, string? mergeKey = null)
    {
        Commit(descriptionKey, mergeKey, token.Before, token.SelectionBefore, null);
    }

    /// <summary>Abandons a gesture and puts the document back as it was.</summary>
    public void CancelGesture(GestureToken token) => RestoreSnapshot(token.Before, token.SelectionBefore);

    private void Commit(string descriptionKey, string? mergeKey, LabelDocument before, IReadOnlyList<string> selBefore,
        IEnumerable<string>? selectionAfter)
    {
        if (selectionAfter is not null) SetSelectionSilently(selectionAfter);
        var after = ElementCloner.CloneDocument(Document);
        // Selection can reference elements removed by the change.
        var valid = new HashSet<string>(Document.Elements.Select(e => e.Id));
        _selection = _selection.Where(valid.Contains).ToList();
        Undo.Push(new SnapshotCommand(this, descriptionKey, mergeKey, before, selBefore, after, _selection.ToList()));
        Revision++;
        LiveChanged?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSelectionSilently(IEnumerable<string> ids)
    {
        var valid = new HashSet<string>(Document.Elements.Select(e => e.Id));
        _selection = ids.Where(valid.Contains).Distinct().ToList();
    }

    internal void RestoreSnapshot(LabelDocument snapshot, IReadOnlyList<string> selection)
    {
        ElementCloner.CopySettings(snapshot, Document);
        Document.Elements.Clear();
        // Clone again: the snapshot must stay pristine because it can be restored several times (undo, redo, undo ...).
        foreach (var e in snapshot.Elements) Document.Elements.Add(ElementCloner.Clone(e));
        SetSelectionSilently(selection);
        Revision++;
        LiveChanged?.Invoke(this, EventArgs.Empty);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the latest step.</summary>
    public void PerformUndo() => Undo.Undo();

    /// <summary>Redoes the latest undone step.</summary>
    public void PerformRedo() => Undo.Redo();
}

/// <summary>State captured when a gesture starts.</summary>
public sealed class GestureToken
{
    internal GestureToken(LabelDocument before, IReadOnlyList<string> selectionBefore)
    {
        Before = before;
        SelectionBefore = selectionBefore;
    }

    internal LabelDocument Before { get; }
    internal IReadOnlyList<string> SelectionBefore { get; }
}
