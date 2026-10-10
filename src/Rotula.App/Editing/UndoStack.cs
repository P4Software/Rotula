using Rotula.Core.Model;

namespace Rotula.App.Editing;

/// <summary>One undoable step of the command pattern.</summary>
public interface IUndoCommand
{
    /// <summary>Resource key of the short description shown in "Undo ..." / "Redo ..." (for example <c>Undo.Move</c>).</summary>
    string DescriptionKey { get; }

    /// <summary>Reverts the step.</summary>
    void Undo();

    /// <summary>Applies the step again.</summary>
    void Redo();

    /// <summary>
    /// Steps with the same non-null key that follow each other quickly (arrow-key nudges, typing in a property box)
    /// collapse into one undo step so the user does not have to press Ctrl+Z fifty times.
    /// </summary>
    string? MergeKey { get; }

    /// <summary>Absorbs <paramref name="next"/> (same merge key) into this command; afterwards this command covers both steps.</summary>
    void MergeWith(IUndoCommand next);
}

/// <summary>
/// Unlimited undo/redo history. The stack has no size cap on purpose: label designs are small and the product promise
/// is that every change can be taken back.
/// </summary>
public sealed class UndoStack
{
    /// <summary>Longest gap between two steps for them to still merge.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(1000);

    private readonly List<IUndoCommand> _done = new();
    private readonly Stack<IUndoCommand> _undone = new();
    private readonly Dictionary<IUndoCommand, int> _tokens = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastPush = DateTime.MinValue;
    private int _nextToken = 1;

    /// <summary>Creates a stack; <paramref name="clock"/> is injectable so tests can control merging.</summary>
    public UndoStack(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>Number of steps that can be undone.</summary>
    public int UndoCount => _done.Count;

    /// <summary>Number of steps that can be redone.</summary>
    public int RedoCount => _undone.Count;

    /// <summary>Description key of the next undo step, or null.</summary>
    public string? NextUndoKey => _done.Count > 0 ? _done[^1].DescriptionKey : null;

    /// <summary>Description key of the next redo step, or null.</summary>
    public string? NextRedoKey => _undone.Count > 0 ? _undone.Peek().DescriptionKey : null;

    /// <summary>
    /// Identifies the current point in history (0 = the start). It changes with every edit and returns to an earlier value
    /// after undo, which is exactly what "unsaved changes" needs.
    /// </summary>
    public int StateToken => _done.Count == 0 ? 0 : _tokens[_done[^1]];

    /// <summary>Raised after any change of the history.</summary>
    public event EventHandler? Changed;

    /// <summary>Records a step that has already been applied. Clears the redo history.</summary>
    public void Push(IUndoCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = _clock();
        _undone.Clear();
        if (_done.Count > 0 && command.MergeKey is not null)
        {
            var top = _done[^1];
            if (top.MergeKey == command.MergeKey && now - _lastPush <= MergeWindow)
            {
                top.MergeWith(command);
                // A merged step is a new state of the document, so it must not look like the previously saved one.
                _tokens[top] = _nextToken++;
                _lastPush = now;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        _done.Add(command);
        _tokens[command] = _nextToken++;
        _lastPush = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undoes the latest step.</summary>
    public void Undo()
    {
        if (_done.Count == 0) return;
        var c = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        c.Undo();
        _undone.Push(c);
        // Whatever comes next must never merge into a step the user just walked back over.
        _lastPush = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Redoes the latest undone step.</summary>
    public void Redo()
    {
        if (_undone.Count == 0) return;
        var c = _undone.Pop();
        c.Redo();
        _done.Add(c);
        _lastPush = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the whole history (used when a document is opened or created).</summary>
    public void Clear()
    {
        _done.Clear();
        _undone.Clear();
        _tokens.Clear();
        _lastPush = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Memento-style command: stores the whole document before and after the change. Documents are small, so one generic
/// command covers every edit (move, resize, property change, align, z-order, add, delete) without a class per operation.
/// </summary>
internal sealed class SnapshotCommand : IUndoCommand
{
    private readonly EditorSession _session;
    private readonly LabelDocument _before;
    private LabelDocument _after;
    private readonly IReadOnlyList<string> _selectionBefore;
    private IReadOnlyList<string> _selectionAfter;

    public SnapshotCommand(EditorSession session, string descriptionKey, string? mergeKey,
        LabelDocument before, IReadOnlyList<string> selectionBefore, LabelDocument after, IReadOnlyList<string> selectionAfter)
    {
        _session = session;
        DescriptionKey = descriptionKey;
        MergeKey = mergeKey;
        _before = before;
        _after = after;
        _selectionBefore = selectionBefore;
        _selectionAfter = selectionAfter;
    }

    public string DescriptionKey { get; }

    public string? MergeKey { get; }

    public void Undo() => _session.RestoreSnapshot(_before, _selectionBefore);

    public void Redo() => _session.RestoreSnapshot(_after, _selectionAfter);

    public void MergeWith(IUndoCommand next)
    {
        var n = (SnapshotCommand)next;
        // The earliest "before" is kept, the newest "after" wins.
        _after = n._after;
        _selectionAfter = n._selectionAfter;
    }
}
