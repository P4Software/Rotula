using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;

namespace Rotula.Tests;

/// <summary>
/// A print run that reserves several counters must be all-or-nothing: if the store fails part way (a full disk, a locked file, a permission
/// problem) the numbers already taken for the earlier counters go back, and the failure reaches the caller as a plain-language issue.
/// </summary>
public class VarJobRollbackTests
{
    /// <summary>A store that behaves like the memory store but can be told to fail, and remembers what was asked of it.</summary>
    private sealed class FlakyStore : ICounterStore
    {
        private readonly MemoryCounterStore _inner = new();
        public int Reserves { get; private set; }
        public int FailReserveAt { get; init; } = int.MaxValue;
        public bool FailEveryComplete { get; init; }
        public int FailCompleteAt { get; init; } = int.MaxValue;
        public Func<Exception> Failure { get; init; } = () => new IOException("disk full");
        public List<string> CompleteCalls { get; } = new();
        private int _completes;

        public CounterRecord? Get(string key) => _inner.Get(key);

        public CounterReservation Reserve(string key, string fingerprint, long count)
        {
            if (++Reserves == FailReserveAt) throw Failure();
            return _inner.Reserve(key, fingerprint, count);
        }

        public bool Complete(CounterReservation reservation, long used, string? lastValue)
        {
            CompleteCalls.Add(reservation.Key + ":" + used);
            if (FailEveryComplete || ++_completes == FailCompleteAt) throw Failure();
            return _inner.Complete(reservation, used, lastValue);
        }

        public bool Reclaim(string key, string pendingId) => _inner.Reclaim(key, pendingId);
        public void Reset(string key, string fingerprint, long nextIndex = 0) => _inner.Reset(key, fingerprint, nextIndex);
        public void AcceptSettings(string key, string fingerprint) => _inner.AcceptSettings(key, fingerprint);
    }

    private static LabelDocument ThreeCounters()
    {
        var doc = new LabelDocument();
        doc.EnsureId();
        foreach (var name in new[] { "a", "b", "c" })
            doc.Variables.Add(Vars.Counter(name, c => { c.Remember = true; c.Padding = 4; }));
        return doc;
    }

    private static VariableContext Ctx(ICounterStore store) => new() { Time = FakeTime.AtLocal(2026, 3, 7), CounterStore = store };

    public static TheoryData<string> Failures => new() { "io", "access", "other", "store" };

    private static Exception Make(string kind) => kind switch
    {
        "io" => new IOException("disk full"),
        "access" => new UnauthorizedAccessException("denied"),
        "store" => new CounterStoreException(VariableMessageKeys.CounterStoreBusy, null, "counters.json"),
        _ => new InvalidOperationException("boom"),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void AFailureOnALaterCounter_GivesBackTheEarlierReservations(string kind)
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailReserveAt = 3, Failure = () => Make(kind) };

        var job = VariableJob.Begin(doc, Ctx(store), 10);   // must not throw

        Assert.False(job.CanPrint);
        Assert.Contains(job.Issues, i => i.Severity == ValidationSeverity.Error);
        foreach (var name in new[] { "a", "b", "c" })
        {
            var record = store.Get(CounterKey.For(doc.Id!, name));
            // Nothing was printed, so the numbers are free again: nothing pending and the counter is back at its start.
            Assert.True(record is null || (record.Pending.Count == 0 && record.Next == 0), name + " still holds a reservation");
        }
    }

    [Fact]
    public void TheRollbackReleasesInReverseOrder()
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailReserveAt = 3 };

        VariableJob.Begin(doc, Ctx(store), 5);

        Assert.Equal(new[] { CounterKey.For(doc.Id!, "b") + ":0", CounterKey.For(doc.Id!, "a") + ":0" }, store.CompleteCalls);
    }

    [Fact]
    public void AnUnexpectedFailure_IsReportedInPlainWords_NotAsAnException()
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailReserveAt = 2, Failure = () => new IOException("The disk is full.") };

        var job = VariableJob.Begin(doc, Ctx(store), 5);

        var first = job.Issues.First(i => i.Severity == ValidationSeverity.Error);
        Assert.Equal(VariableMessageKeys.CounterStoreFailed, first.MessageKey);
        Assert.Contains("The disk is full.", first.Args);
    }

    [Fact]
    public void AFailingRollback_DoesNotHideTheOriginalError_AndKeepsTheNumbersTaken()
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailReserveAt = 3, FailEveryComplete = true, Failure = () => new IOException("disk full") };

        var job = VariableJob.Begin(doc, Ctx(store), 5);   // still must not throw

        Assert.False(job.CanPrint);
        // The error that started it all comes first.
        Assert.Equal(ValidationSeverity.Error, job.Issues[0].Severity);
        Assert.Equal(2, store.CompleteCalls.Count);          // both releases were tried even though the first failed
        Assert.Contains(job.Issues, i => i.Severity == ValidationSeverity.Warning && i.MessageKey == VariableMessageKeys.CounterStoreFailed);
        // A release that could not be written leaves the numbers reserved: skipped later, never reissued.
        Assert.Equal(5, store.Get(CounterKey.For(doc.Id!, "a"))!.Next);
    }

    [Fact]
    public void ARefusedCounter_GivesBackTheCountersReservedBeforeIt()
    {
        var doc = ThreeCounters();
        doc.Variables[2].Counter!.Kind = Rotula.Core.Merge.CounterKind.Numeric;
        doc.Variables[2].Counter!.Start = "not a number";   // invalid settings: refused before reserving
        var store = new FlakyStore();

        var job = VariableJob.Begin(doc, Ctx(store), 5);

        Assert.False(job.CanPrint);
        Assert.Empty(store.Get(CounterKey.For(doc.Id!, "a"))!.Pending);
        Assert.Equal(0, store.Get(CounterKey.For(doc.Id!, "a"))!.Next);
    }

    [Fact]
    public void Complete_WhenOneCounterFails_StillConfirmsTheOthers_AndReportsInsteadOfThrowing()
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailCompleteAt = 2, Failure = () => new IOException("disk full") };
        var job = VariableJob.Begin(doc, Ctx(store), 5);
        Assert.True(job.CanPrint);

        var problems = job.Complete(5);   // must not throw

        Assert.Equal(3, store.CompleteCalls.Count);
        Assert.Single(problems);
        Assert.Equal(VariableMessageKeys.CounterStoreFailed, problems[0].MessageKey);
        // The failed one stays reserved (pending), which is the safe side: its numbers are never handed out again.
        Assert.Single(store.Get(CounterKey.For(doc.Id!, "b"))!.Pending);
        Assert.Empty(store.Get(CounterKey.For(doc.Id!, "c"))!.Pending);
    }

    [Fact]
    public void Cancel_WhenTheStoreFails_ReportsInsteadOfThrowing()
    {
        var doc = ThreeCounters();
        var store = new FlakyStore { FailEveryComplete = true };
        var job = VariableJob.Begin(doc, Ctx(store), 5);

        var problems = job.Cancel();

        Assert.Equal(3, problems.Count);
    }
}
