using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rotula.Core.Variables.Counters;

/// <summary>A failure of the counter store. The message key is one of <see cref="VariableMessageKeys"/>.</summary>
public sealed class CounterStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CounterStoreException(string messageKey, Exception? inner = null, params string[] args)
        : base(messageKey, inner)
    {
        MessageKey = messageKey;
        Args = args;
    }

    /// <summary>Message key (for example <see cref="VariableMessageKeys.CounterStoreCorrupt"/>).</summary>
    public string MessageKey { get; }

    /// <summary>Arguments of the message.</summary>
    public string[] Args { get; }
}

/// <summary>Builds the key under which a counter is remembered: the label's id and the counter's name.</summary>
public static class CounterKey
{
    /// <summary>The key for counter <paramref name="counterName"/> of the label with id <paramref name="labelId"/> (name compared ignoring case).</summary>
    public static string For(string labelId, string counterName) => labelId + "/" + counterName.ToLowerInvariant();
}

/// <summary>A range of serial positions handed out but not yet confirmed.</summary>
/// <param name="Id">Identifier of the reservation.</param>
/// <param name="First">First position (zero-based index into the counter's sequence).</param>
/// <param name="Count">Number of positions.</param>
/// <param name="CreatedUtc">When it was reserved.</param>
public sealed record PendingRange(string Id, long First, long Count, DateTimeOffset CreatedUtc)
{
    /// <summary>The position after the last one of the range.</summary>
    public long End => First + Count;
}

/// <summary>What the store remembers about one counter.</summary>
/// <param name="Next">Next position that will be handed out. Positions below it are used, skipped or pending; they are never reissued.</param>
/// <param name="LastUsedIndex">Highest position confirmed as printed, or -1 when none.</param>
/// <param name="LastValue">The text printed at <paramref name="LastUsedIndex"/>, or null.</param>
/// <param name="Fingerprint">The counter settings the positions were counted under (see <see cref="CounterVariableOptions.Fingerprint"/>).</param>
/// <param name="Pending">Reservations that were never confirmed or cancelled.</param>
/// <param name="UpdatedUtc">When the record last changed.</param>
public sealed record CounterRecord(long Next, long LastUsedIndex, string? LastValue, string Fingerprint, IReadOnlyList<PendingRange> Pending, DateTimeOffset UpdatedUtc);

/// <summary>A block of positions reserved for one print job.</summary>
/// <param name="Key">The counter key.</param>
/// <param name="Id">Reservation id.</param>
/// <param name="First">First position.</param>
/// <param name="Count">Number of positions.</param>
public sealed record CounterReservation(string Key, string Id, long First, long Count);

/// <summary>
/// Remembers how far each persistent counter has counted. Rules every implementation follows (they exist so a crash or a second window can
/// never cause two labels to carry the same number):
/// <list type="number">
/// <item><see cref="Reserve"/> hands out a block of positions and moves <c>Next</c> past it before anything is printed. Two callers always get
/// different blocks.</item>
/// <item><see cref="Complete"/> confirms how many of the positions were really used. Unused positions at the end of the newest block are given
/// back; unused positions that are not at the end are skipped (a gap), never reissued.</item>
/// <item>A block that is never completed (the program crashed) stays in <see cref="CounterRecord.Pending"/>; its numbers are skipped until the
/// operator explicitly calls <see cref="Reclaim"/> because nothing was printed.</item>
/// <item>Nothing is ever reset silently: <see cref="Reset"/> is an explicit call and a damaged store reports an error instead of starting over.</item>
/// </list>
/// </summary>
public interface ICounterStore
{
    /// <summary>The record of a counter, or null if it has never been used. Never changes anything.</summary>
    CounterRecord? Get(string key);

    /// <summary>
    /// Reserves <paramref name="count"/> positions (1 to 100,000,000). The first call for a key stores <paramref name="fingerprint"/>. If an
    /// existing record was made with a different fingerprint, nothing is reserved and a <see cref="CounterStoreException"/> with
    /// <see cref="VariableMessageKeys.CounterSettingsChanged"/> is thrown; the caller
    /// should have checked <see cref="Get"/> and asked the operator (see <see cref="AcceptSettings"/> and <see cref="Reset"/>).
    /// </summary>
    CounterReservation Reserve(string key, string fingerprint, long count);

    /// <summary>
    /// Confirms a reservation: <paramref name="used"/> positions (from the first) were printed, <paramref name="lastValue"/> is the text of the last
    /// one. Use 0 to cancel. Returns false when the reservation no longer exists (it was reset or reclaimed meanwhile); nothing changes then.
    /// </summary>
    bool Complete(CounterReservation reservation, long used, string? lastValue);

    /// <summary>Forgets the unconfirmed reservation <paramref name="pendingId"/> because the operator knows nothing was printed. Gives the numbers back only if no later block was handed out.</summary>
    bool Reclaim(string key, string pendingId);

    /// <summary>Starts over: the next position becomes <paramref name="nextIndex"/> and all pending blocks are forgotten.</summary>
    void Reset(string key, string fingerprint, long nextIndex = 0);

    /// <summary>Keeps counting from the remembered position under new counter settings (after the operator confirmed the edit).</summary>
    void AcceptSettings(string key, string fingerprint);
}

/// <summary>The counter bookkeeping shared by every store: a dictionary of records and the operations on it.</summary>
public sealed class CounterBook
{
    /// <summary>Most records kept; the least recently updated are dropped beyond this (their numbers are then not remembered).</summary>
    public const int MaxRecords = 5000;
    /// <summary>Most pending blocks kept per counter; older ones are dropped (their numbers stay skipped).</summary>
    public const int MaxPending = 64;
    /// <summary>Most positions one reservation may take.</summary>
    public const long MaxReservation = 100_000_000;
    /// <summary>Highest position the book stores.</summary>
    public const long MaxIndex = long.MaxValue / 4;
    /// <summary>The newest file format version this build writes and reads.</summary>
    public const int FormatVersion = 1;

    private readonly Dictionary<string, CounterRecord> _records = new(StringComparer.Ordinal);

    /// <summary>The record for <paramref name="key"/>, or null.</summary>
    public CounterRecord? Get(string key) => _records.GetValueOrDefault(key);

    /// <summary>Number of records.</summary>
    public int Count => _records.Count;

    /// <summary>Reserves a block. See <see cref="ICounterStore.Reserve"/>.</summary>
    public CounterReservation Reserve(string key, string fingerprint, long count, DateTimeOffset now)
    {
        if (count < 1 || count > MaxReservation) throw new ArgumentOutOfRangeException(nameof(count));
        var rec = _records.GetValueOrDefault(key);
        string counterName = key[(key.LastIndexOf('/') + 1)..];
        if (rec is not null && !string.Equals(rec.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new CounterStoreException(VariableMessageKeys.CounterSettingsChanged, null, counterName, rec.LastValue ?? string.Empty);
        long first = rec?.Next ?? 0;
        if (first > MaxIndex - count) throw new CounterStoreException(VariableMessageKeys.CounterOverflow, null, counterName);
        string id = Guid.NewGuid().ToString("N");
        var pending = (rec?.Pending ?? Array.Empty<PendingRange>()).Append(new PendingRange(id, first, count, now));
        var list = pending.TakeLast(MaxPending).ToList();
        Put(key, new CounterRecord(first + count, rec?.LastUsedIndex ?? -1, rec?.LastValue, fingerprint, list, now));
        return new CounterReservation(key, id, first, count);
    }

    /// <summary>Confirms a block. See <see cref="ICounterStore.Complete"/>.</summary>
    public bool Complete(CounterReservation r, long used, string? lastValue, DateTimeOffset now)
    {
        var rec = _records.GetValueOrDefault(r.Key);
        var range = rec?.Pending.FirstOrDefault(p => p.Id == r.Id);
        if (rec is null || range is null) return false;
        used = Math.Clamp(used, 0, range.Count);
        long next = rec.Next;
        // Only the newest block can hand its unused tail back; an older one would leave a hole that a later block already passed.
        if (used < range.Count && range.End == rec.Next) next = range.First + used;
        long lastIndex = rec.LastUsedIndex;
        string? lastText = rec.LastValue;
        if (used > 0 && range.First + used - 1 >= lastIndex)
        {
            lastIndex = range.First + used - 1;
            lastText = lastValue;
        }
        var pending = rec.Pending.Where(p => p.Id != r.Id).ToList();
        Put(r.Key, rec with { Next = next, LastUsedIndex = lastIndex, LastValue = lastText, Pending = pending, UpdatedUtc = now });
        return true;
    }

    /// <summary>Forgets a pending block. See <see cref="ICounterStore.Reclaim"/>.</summary>
    public bool Reclaim(string key, string pendingId, DateTimeOffset now)
    {
        var rec = _records.GetValueOrDefault(key);
        var range = rec?.Pending.FirstOrDefault(p => p.Id == pendingId);
        if (rec is null || range is null) return false;
        long next = range.End == rec.Next ? range.First : rec.Next;
        Put(key, rec with { Next = next, Pending = rec.Pending.Where(p => p.Id != pendingId).ToList(), UpdatedUtc = now });
        return true;
    }

    /// <summary>Starts over. See <see cref="ICounterStore.Reset"/>.</summary>
    public void Reset(string key, string fingerprint, long nextIndex, DateTimeOffset now)
    {
        if (nextIndex < 0 || nextIndex > MaxIndex) throw new ArgumentOutOfRangeException(nameof(nextIndex));
        Put(key, new CounterRecord(nextIndex, -1, null, fingerprint, Array.Empty<PendingRange>(), now));
    }

    /// <summary>Accepts new settings. See <see cref="ICounterStore.AcceptSettings"/>.</summary>
    public void AcceptSettings(string key, string fingerprint, DateTimeOffset now)
    {
        if (_records.GetValueOrDefault(key) is { } rec) Put(key, rec with { Fingerprint = fingerprint, UpdatedUtc = now });
    }

    private void Put(string key, CounterRecord record)
    {
        _records[key] = record;
        if (_records.Count <= MaxRecords) return;
        foreach (var old in _records.OrderBy(kv => kv.Value.UpdatedUtc).Take(_records.Count - MaxRecords).Select(kv => kv.Key).ToList())
            if (old != key) _records.Remove(old);
    }

    // ---- JSON -------------------------------------------------------------------------------

    /// <summary>Writes the book as the text of <c>counters.json</c>.</summary>
    public string ToJson()
    {
        var counters = new JsonObject();
        foreach (var (key, r) in _records.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var pending = new JsonArray();
            foreach (var p in r.Pending)
                pending.Add(new JsonObject { ["id"] = p.Id, ["first"] = p.First, ["count"] = p.Count, ["created"] = p.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) });
            counters[key] = new JsonObject
            {
                ["next"] = r.Next,
                ["lastUsedIndex"] = r.LastUsedIndex,
                ["lastValue"] = r.LastValue,
                ["fingerprint"] = r.Fingerprint,
                ["pending"] = pending,
                ["updated"] = r.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        }
        var root = new JsonObject
        {
            ["comment"] = "Rotula remembers the last serial number of counters that are set to remember. Do not edit by hand while Rotula is running.",
            ["version"] = FormatVersion,
            ["counters"] = counters,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads <c>counters.json</c> text. A file that is not valid (not JSON, wrong shape, absurd values, too many records) throws
    /// <see cref="CounterStoreException"/> with <see cref="VariableMessageKeys.CounterStoreCorrupt"/>; it is never treated as empty because that
    /// would reissue numbers.
    /// </summary>
    public static CounterBook FromJson(string json, string pathForMessages)
    {
        var book = new CounterBook();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || !v.TryGetInt32(out int version) || version < 1)
                throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
            if (version > FormatVersion)
                throw new CounterStoreException(VariableMessageKeys.CounterStoreNewer, null, pathForMessages, version.ToString(CultureInfo.InvariantCulture));
            if (!root.TryGetProperty("counters", out var counters) || counters.ValueKind != JsonValueKind.Object)
                throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
            int n = 0;
            foreach (var entry in counters.EnumerateObject())
            {
                if (++n > MaxRecords) throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
                var e = entry.Value;
                long next = Num(e, "next", 0), last = Num(e, "lastUsedIndex", -1);
                if (next < 0 || next > MaxIndex || last < -1 || last >= next) throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
                string fingerprint = Str(e, "fingerprint") ?? throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
                var pending = new List<PendingRange>();
                if (e.TryGetProperty("pending", out var pa) && pa.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pe in pa.EnumerateArray())
                    {
                        if (pending.Count >= MaxPending) throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
                        long first = Num(pe, "first", -1), cnt = Num(pe, "count", 0);
                        if (first < 0 || cnt < 1 || cnt > MaxReservation || first + cnt > next) throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, null, pathForMessages);
                        pending.Add(new PendingRange(Str(pe, "id") ?? Guid.NewGuid().ToString("N"), first, cnt, Date(pe, "created")));
                    }
                }
                book._records[entry.Name] = new CounterRecord(next, last, Str(e, "lastValue"), fingerprint, pending, Date(e, "updated"));
            }
            return book;
        }
        catch (JsonException ex)
        {
            throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, ex, pathForMessages);
        }
        catch (InvalidOperationException ex) // GetInt64 on a wrong value kind
        {
            throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, ex, pathForMessages);
        }
        catch (FormatException ex)
        {
            throw new CounterStoreException(VariableMessageKeys.CounterStoreCorrupt, ex, pathForMessages);
        }
    }

    private static long Num(JsonElement e, string name, long fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt64(out long v)) throw new FormatException(name);
        return v;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        string s = p.GetString() ?? string.Empty;
        return s.Length > VariableLimits.MaxValueLength ? s[..VariableLimits.MaxValueLength] : s;
    }

    private static DateTimeOffset Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTimeOffset.UnixEpoch;
}

/// <summary>
/// Base class of stores: subclasses only say how to run a read-modify-write transaction on the <see cref="CounterBook"/> (in memory, or under a
/// file lock).
/// </summary>
public abstract class CounterStoreBase : ICounterStore
{
    /// <summary>The clock used for time stamps.</summary>
    protected TimeProvider Time { get; }

    /// <summary>Creates the store.</summary>
    protected CounterStoreBase(TimeProvider? time = null) => Time = time ?? TimeProvider.System;

    /// <summary>Runs <paramref name="action"/> on the current book. When <paramref name="write"/> is true the changed book is saved before returning.</summary>
    protected abstract T Transact<T>(bool write, Func<CounterBook, T> action);

    /// <inheritdoc />
    public CounterRecord? Get(string key) => Transact(false, b => b.Get(key));

    /// <inheritdoc />
    public CounterReservation Reserve(string key, string fingerprint, long count) =>
        Transact(true, b => b.Reserve(key, fingerprint, count, Time.GetUtcNow()));

    /// <inheritdoc />
    public bool Complete(CounterReservation reservation, long used, string? lastValue) =>
        Transact(true, b => b.Complete(reservation, used, lastValue, Time.GetUtcNow()));

    /// <inheritdoc />
    public bool Reclaim(string key, string pendingId) => Transact(true, b => b.Reclaim(key, pendingId, Time.GetUtcNow()));

    /// <inheritdoc />
    public void Reset(string key, string fingerprint, long nextIndex = 0) =>
        Transact(true, b => { b.Reset(key, fingerprint, nextIndex, Time.GetUtcNow()); return 0; });

    /// <inheritdoc />
    public void AcceptSettings(string key, string fingerprint) =>
        Transact(true, b => { b.AcceptSettings(key, fingerprint, Time.GetUtcNow()); return 0; });
}

/// <summary>A store that lives only in memory (tests, previews, and counters that should not outlive the program).</summary>
public sealed class MemoryCounterStore : CounterStoreBase
{
    private readonly CounterBook _book = new();
    private readonly object _gate = new();

    /// <summary>Creates an empty store.</summary>
    public MemoryCounterStore(TimeProvider? time = null) : base(time) { }

    /// <inheritdoc />
    protected override T Transact<T>(bool write, Func<CounterBook, T> action)
    {
        lock (_gate) return action(_book);
    }
}
