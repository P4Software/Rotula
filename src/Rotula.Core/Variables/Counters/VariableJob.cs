using System.Globalization;
using Rotula.Core.Model;
using Rotula.Core.Validation;

namespace Rotula.Core.Variables.Counters;

/// <summary>
/// One print run with counters that remember their value. It reserves the serial numbers before anything is printed and confirms afterwards, so a
/// crash, a cancelled run or a second window can never make two labels carry the same number.
/// </summary>
/// <remarks>
/// <para><b>The rule, exactly.</b></para>
/// <list type="number">
/// <item><see cref="Begin"/> reserves one position per label for every counter that remembers its value, and writes that to the store before
/// returning. From this moment those numbers are taken, whatever happens next.</item>
/// <item>The caller generates label <c>i</c> (0-based) with <see cref="ContextFor"/>, which adds <c>i</c> to each counter's reserved first position.</item>
/// <item>When printing ends, call <see cref="Complete"/> with the number of labels that really were printed. The store then remembers the last value.
/// Positions that were reserved but not used are given back if nobody reserved after this run, otherwise they are skipped (a gap in the series).</item>
/// <item><see cref="Cancel"/> is <c>Complete(0)</c>: a run that failed before anything reached the printer burns no numbers. To burn the numbers of
/// a failed run on purpose (when the printer may have printed some labels before the failure), call <c>Complete(count)</c> instead.</item>
/// <item>If the program dies between steps 1 and 3 the reservation stays in the store as unconfirmed. The next <see cref="Begin"/> skips those numbers
/// and reports them (<see cref="VariableMessageKeys.CounterUnconfirmed"/>); the operator can give them back with <see cref="CounterAdmin.Reclaim"/> when sure nothing was printed.</item>
/// <item>If a counter's settings were edited after numbers were used, <see cref="Begin"/> reports <see cref="VariableMessageKeys.CounterSettingsChanged"/>
/// as an error and reserves nothing. The operator either accepts the edit (<see cref="CounterAdmin.AcceptSettingsChange"/>, counting continues from the remembered
/// position) or starts over (<see cref="CounterAdmin.Reset"/>).</item>
/// </list>
/// A job without any remembering counter, or without a store, does nothing to the store and always works.
/// </remarks>
public sealed class VariableJob
{
    private readonly LabelDocument _doc;
    private readonly VariableContext _baseContext;
    private readonly List<(LabelVariable Variable, CounterReservation Reservation, long First)> _reservations = new();
    private readonly List<ValidationIssue> _issues = new();
    private bool _done;

    private VariableJob(LabelDocument doc, VariableContext context, long itemCount)
    {
        _doc = doc;
        ItemCount = itemCount;
        var bases = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _baseContext = context.Clone();
        _baseContext.CounterBase = bases;
        if (context.CounterBase is { } given) foreach (var kv in given) bases[kv.Key] = kv.Value;

        var remembering = doc.Variables.Where(v => v.Kind == VariableKind.Counter && v.Counter is { Remember: true })
            .DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(v => !bases.ContainsKey(v.Name)).ToList();
        if (context.CounterStore is not { } store)
        {
            // Without a place to remember, such a counter would start again at its first value and repeat numbers already used.
            foreach (var v in remembering)
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterNoStore, null, v.Name));
            return;
        }
        foreach (var v in remembering)
        {
            var c = v.Counter!;
            if (itemCount > CounterBook.MaxReservation)
            {
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterOverflow, null, v.Name));
                continue;
            }
            if (c.ToSerialCounter(v.Name).Validate() is { } bad)
            {
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterInvalid, null, v.Name, bad));
                continue;
            }
            if (doc.Id is null)
            {
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterNeedsLabelId, null, v.Name));
                continue;
            }
            string key = CounterKey.For(doc.Id, v.Name);
            string fingerprint = c.Fingerprint();
            try
            {
                var record = store.Get(key);
                if (record is not null && !string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    _issues.Add(new ValidationIssue(ValidationSeverity.Error, VariableMessageKeys.CounterSettingsChanged, null, v.Name, record.LastValue ?? string.Empty));
                    continue;
                }
                var reservation = store.Reserve(key, fingerprint, Math.Max(1, itemCount));
                // Anything that was pending before this reservation is a run that never finished.
                foreach (var stale in record?.Pending ?? Array.Empty<PendingRange>())
                    _issues.Add(new ValidationIssue(ValidationSeverity.Warning, VariableMessageKeys.CounterUnconfirmed, null, v.Name,
                        stale.First.ToString(CultureInfo.InvariantCulture), (stale.End - 1).ToString(CultureInfo.InvariantCulture)));
                _reservations.Add((v, reservation, reservation.First));
                bases[v.Name] = reservation.First;
            }
            catch (CounterStoreException ex)
            {
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, ex.MessageKey, null, ex.Args));
            }
        }
    }

    /// <summary>Number of labels this run was started for.</summary>
    public long ItemCount { get; }

    /// <summary>Problems found when starting. Errors mean nothing should be printed (<see cref="CanPrint"/> is false).</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>True when there is no error and printing can start.</summary>
    public bool CanPrint => !_issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>
    /// Starts a run of <paramref name="itemCount"/> labels and reserves their serial numbers. If <see cref="CanPrint"/> is false, nothing was reserved
    /// that is not given back by <see cref="Cancel"/>.
    /// </summary>
    /// <param name="doc">The label.</param>
    /// <param name="context">Clock, record template, answers and the counter store.</param>
    /// <param name="itemCount">How many labels get their own serial number (the records of a batch, or the print quantity when every copy must differ).</param>
    public static VariableJob Begin(LabelDocument doc, VariableContext context, long itemCount)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(context);
        return new VariableJob(doc, context, itemCount);
    }

    /// <summary>The context for label <paramref name="itemIndex"/> (0-based) of the run, with <paramref name="record"/> as its data record.</summary>
    public VariableContext ContextFor(long itemIndex, IReadOnlyDictionary<string, string>? record = null) => _baseContext.ForItem(itemIndex, record ?? _baseContext.Record);

    /// <summary>Resolves the variables of label <paramref name="itemIndex"/>.</summary>
    public VariableResolution Resolve(long itemIndex, IReadOnlyDictionary<string, string>? record = null) =>
        VariableEngine.Resolve(_doc, ContextFor(itemIndex, record));

    /// <summary>
    /// Confirms that <paramref name="labelsPrinted"/> labels (counted from the first) were printed. Safe to call more than once; only the first call counts.
    /// Returns the problems met while writing to the store (the numbers stay reserved in that case, which is the safe side).
    /// </summary>
    public IReadOnlyList<ValidationIssue> Complete(long labelsPrinted)
    {
        var problems = new List<ValidationIssue>();
        if (_done) return problems;
        _done = true;
        if (_baseContext.CounterStore is not { } store) return problems;
        long used = Math.Clamp(labelsPrinted, 0, ItemCount);
        foreach (var (variable, reservation, first) in _reservations)
        {
            try
            {
                string? last = null;
                if (used > 0) variable.Counter!.ToSerialCounter(variable.Name).TryGetValue(first + used - 1, out last);
                store.Complete(reservation, used, last);
            }
            catch (CounterStoreException ex)
            {
                problems.Add(new ValidationIssue(ValidationSeverity.Error, ex.MessageKey, null, ex.Args));
            }
        }
        return problems;
    }

    /// <summary>Gives back every reserved number: use when nothing reached the printer.</summary>
    public IReadOnlyList<ValidationIssue> Cancel() => Complete(0);
}

/// <summary>Operations on the memory of one counter, for a "counters" dialog: look at it, start over, accept an edit, give back lost numbers.</summary>
public static class CounterAdmin
{
    /// <summary>What the store knows about a counter, with the value the next label would get.</summary>
    /// <param name="Exists">False when the counter was never used with this store.</param>
    /// <param name="NextIndex">Position of the next label.</param>
    /// <param name="NextValue">The text the next label would carry (null if the settings are invalid or the counter overflowed).</param>
    /// <param name="LastValue">The last value confirmed as printed, or null.</param>
    /// <param name="SettingsChanged">The counter was edited after numbers were used; printing is blocked until accepted or reset.</param>
    /// <param name="Unconfirmed">Reservations that never finished.</param>
    public sealed record Status(bool Exists, long NextIndex, string? NextValue, string? LastValue, bool SettingsChanged, IReadOnlyList<PendingRange> Unconfirmed);

    /// <summary>The "peek next value" call: shows what the next label will carry without reserving anything.</summary>
    public static Status Peek(LabelDocument doc, string counterName, ICounterStore? store)
    {
        var v = Find(doc, counterName);
        var c = v.Counter ?? new CounterVariableOptions();
        var record = store is not null && doc.Id is not null ? store.Get(CounterKey.For(doc.Id, v.Name)) : null;
        long next = record?.Next ?? 0;
        var serial = c.ToSerialCounter(v.Name);
        string? nextValue = serial.Validate() is null && serial.TryGetValue(next, out var text) ? text : null;
        bool changed = record is not null && !string.Equals(record.Fingerprint, c.Fingerprint(), StringComparison.Ordinal);
        return new Status(record is not null, next, nextValue, record?.LastValue, changed, record?.Pending ?? Array.Empty<PendingRange>());
    }

    /// <summary>Starts the counter over: the next label gets position <paramref name="nextIndex"/> (0 = the counter's start value).</summary>
    public static void Reset(LabelDocument doc, string counterName, ICounterStore store, long nextIndex = 0)
    {
        var v = Find(doc, counterName);
        store.Reset(CounterKey.For(RequireId(doc, v), v.Name), (v.Counter ?? new CounterVariableOptions()).Fingerprint(), nextIndex);
    }

    /// <summary>Starts the counter so that the next label carries the number <paramref name="nextNumber"/> (numeric counters only; uses start and step).</summary>
    public static bool ResetToNumber(LabelDocument doc, string counterName, ICounterStore store, long nextNumber)
    {
        var v = Find(doc, counterName);
        var c = v.Counter ?? new CounterVariableOptions();
        if (c.Kind != Merge.CounterKind.Numeric || c.Step == 0 || !long.TryParse(c.Start, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long start)) return false;
        long diff = nextNumber - start;
        if (diff % c.Step != 0) return false;
        long index = diff / c.Step;
        if (index < 0) return false;
        Reset(doc, counterName, store, index);
        return true;
    }

    /// <summary>Keeps counting from the remembered position after the operator confirmed an edit of the counter's settings.</summary>
    public static void AcceptSettingsChange(LabelDocument doc, string counterName, ICounterStore store)
    {
        var v = Find(doc, counterName);
        store.AcceptSettings(CounterKey.For(RequireId(doc, v), v.Name), (v.Counter ?? new CounterVariableOptions()).Fingerprint());
    }

    /// <summary>Gives back an unconfirmed reservation when the operator is sure nothing was printed.</summary>
    public static bool Reclaim(LabelDocument doc, string counterName, ICounterStore store, string pendingId)
    {
        var v = Find(doc, counterName);
        return store.Reclaim(CounterKey.For(RequireId(doc, v), v.Name), pendingId);
    }

    private static LabelVariable Find(LabelDocument doc, string name) =>
        doc.Variables.FirstOrDefault(v => v.Kind == VariableKind.Counter && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("There is no counter variable called " + name, nameof(name));

    private static string RequireId(LabelDocument doc, LabelVariable v) =>
        doc.Id ?? throw new InvalidOperationException("The label has no id yet: call EnsureId() before using a counter that remembers its value (" + v.Name + ").");
}
