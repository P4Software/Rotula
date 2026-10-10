using ModelContextProtocol;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Files;

namespace Rotula.Mcp.Session;

/// <summary>What the session remembers about the label it holds besides the design itself.</summary>
/// <param name="Document">A copy of the label (safe to read and render without holding the session lock).</param>
/// <param name="Name">Friendly name, used to suggest a file name when saving.</param>
/// <param name="SourcePath">The file the label was opened from or last saved to, or null.</param>
/// <param name="Merge">Data-merge settings that came with an opened file, kept so saving does not lose them.</param>
/// <param name="Dirty">True when the label changed since it was created, opened or saved.</param>
public sealed record SessionLabel(LabelDocument Document, string Name, string? SourcePath, DataMergeSettings? Merge, bool Dirty);

/// <summary>Describes what an undo did, in words.</summary>
/// <param name="Undone">Short description of the change that was reverted.</param>
/// <param name="StepsLeft">How many earlier changes can still be undone.</param>
/// <param name="HasLabel">False when undoing brought back the state with no label open.</param>
public sealed record UndoOutcome(string Undone, int StepsLeft, bool HasLabel);

/// <summary>
/// The one working label of a connected assistant. All access goes through a lock because tool calls can overlap; every
/// change is recorded first so the last few can be undone (assistants make mistakes). Snapshots are .rotula text produced
/// by the normal serializer, so a snapshot is a deep copy that keeps element ids.
/// </summary>
public sealed class LabelSession
{
    /// <summary>How many changes are remembered for <see cref="Undo"/>.</summary>
    public const int UndoDepth = 20;

    private readonly object _gate = new();
    private readonly int _maxElements;
    private readonly Action<LabelDocument>? _verify;
    private readonly LinkedList<Snapshot> _undo = new();

    private LabelDocument? _doc;
    private string _name = "Untitled";
    private string? _sourcePath;
    private DataMergeSettings? _merge;
    private bool _dirty;
    private long _revision;

    private sealed record Snapshot(string? Json, string Name, string? SourcePath, bool Dirty, string Description);

    /// <summary>Creates an empty session.</summary>
    /// <param name="maxElements">The element limit from the settings; enforced on every change.</param>
    /// <param name="verify">Extra size and sanity checks run on every new or changed label; it throws a plain-language error to refuse.</param>
    public LabelSession(int maxElements, Action<LabelDocument>? verify = null)
    {
        _maxElements = maxElements;
        _verify = verify;
    }

    /// <summary>The element limit.</summary>
    public int MaxElements => _maxElements;

    /// <summary>Increases with every change, so a caller can tell whether the label moved on (used by the print confirmation).</summary>
    public long Revision { get { lock (_gate) return _revision; } }

    /// <summary>True when a label is open.</summary>
    public bool HasLabel { get { lock (_gate) return _doc is not null; } }

    /// <summary>Number of changes that can be undone.</summary>
    public int UndoSteps { get { lock (_gate) return _undo.Count; } }

    /// <summary>Returns a copy of the current label and its state, or throws a plain-language error when none is open.</summary>
    public SessionLabel GetCopy()
    {
        lock (_gate)
        {
            var doc = Require();
            return new SessionLabel(Clone(doc), _name, _sourcePath, CloneMerge(_merge), _dirty);
        }
    }

    /// <summary>Like <see cref="GetCopy"/> but returns null instead of throwing when no label is open.</summary>
    public SessionLabel? TryGet()
    {
        lock (_gate)
            return _doc is null ? null : new SessionLabel(Clone(_doc), _name, _sourcePath, CloneMerge(_merge), _dirty);
    }

    /// <summary>Runs a read-only function on the live label under the lock. The function must not keep the document.</summary>
    public T Read<T>(Func<LabelDocument, T> read)
    {
        lock (_gate) return read(Require());
    }

    /// <summary>
    /// Applies a change to the live label. The state before is remembered for undo; if the change throws or leaves the label
    /// over the element limit, the label is put back exactly as it was and the error is reported.
    /// </summary>
    /// <param name="description">Short description for the undo message, for example "add_text".</param>
    /// <param name="change">The change.</param>
    public T Mutate<T>(string description, Func<LabelDocument, T> change)
    {
        lock (_gate)
        {
            var doc = Require();
            string before = RotulaFileSerializer.Serialize(doc, _merge);
            try
            {
                T result = change(doc);
                if (doc.Elements.Count > _maxElements)
                    throw new McpException($"A label may hold at most {_maxElements} elements (maxElements in rotula-mcp.settings.json).");
                _verify?.Invoke(doc);
                Push(new Snapshot(before, _name, _sourcePath, _dirty, description));
                _dirty = true;
                _revision++;
                return result;
            }
            catch
            {
                // Put everything back, including ids, so a half-applied change never survives.
                var restored = RotulaFileSerializer.DeserializeFile(before);
                _doc = restored.Document;
                _merge = restored.Merge;
                throw;
            }
        }
    }

    /// <summary>Replaces the whole working label (create, open, import). The previous state can be undone.</summary>
    public void Replace(LabelDocument document, string name, string? sourcePath, DataMergeSettings? merge, string description, bool dirty)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (document.Elements.Count > _maxElements)
                throw new McpException($"That label has {document.Elements.Count} elements; the limit is {_maxElements} (maxElements in rotula-mcp.settings.json).");
            _verify?.Invoke(document);
            Push(CurrentSnapshot(description));
            _doc = document;
            _name = name;
            _sourcePath = sourcePath;
            _merge = merge;
            _dirty = dirty;
            _revision++;
        }
    }

    /// <summary>Records that the label was saved to <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        lock (_gate)
        {
            _sourcePath = path;
            _dirty = false;
        }
    }

    /// <summary>Changes the friendly name (not part of the undo history).</summary>
    public void Rename(string name)
    {
        lock (_gate) _name = name;
    }

    /// <summary>Reverts the most recent change.</summary>
    public UndoOutcome Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0)
                throw new McpException("There is nothing to undo.");
            var snap = _undo.Last!.Value;
            _undo.RemoveLast();
            if (snap.Json is null)
            {
                _doc = null;
                _merge = null;
            }
            else
            {
                var restored = RotulaFileSerializer.DeserializeFile(snap.Json);
                _doc = restored.Document;
                _merge = restored.Merge;
            }
            _name = snap.Name;
            _sourcePath = snap.SourcePath;
            _dirty = snap.Dirty;
            _revision++;
            return new UndoOutcome(snap.Description, _undo.Count, _doc is not null);
        }
    }

    /// <summary>A short id that is not used by any element of the label, such as "t3" for the third text.</summary>
    public static string NextId(LabelDocument doc, string prefix)
    {
        var used = new HashSet<string>(doc.Elements.Select(e => e.Id), StringComparer.Ordinal);
        for (int i = 1; ; i++)
        {
            string id = prefix + i;
            if (!used.Contains(id)) return id;
        }
    }

    private LabelDocument Require() =>
        _doc ?? throw new McpException("No label is open. Call create_label, open_label or import_zpl first.");

    private Snapshot CurrentSnapshot(string description) =>
        new(_doc is null ? null : RotulaFileSerializer.Serialize(_doc, _merge), _name, _sourcePath, _dirty, description);

    private void Push(Snapshot s)
    {
        _undo.AddLast(s);
        while (_undo.Count > UndoDepth) _undo.RemoveFirst();
    }

    private static LabelDocument Clone(LabelDocument d) => RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(d));

    private static DataMergeSettings? CloneMerge(DataMergeSettings? m) =>
        m is null ? null : RotulaFileSerializer.DeserializeFile(RotulaFileSerializer.Serialize(new LabelDocument(), m)).Merge;
}
