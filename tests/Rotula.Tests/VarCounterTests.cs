using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>Counter variables: wrapping the serial counter, remembering the position between sessions, crash safety, concurrency, corruption.</summary>
public class VarCounterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-counters-" + Guid.NewGuid().ToString("N"));

    public VarCounterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* a scanner may still hold a file; the temp folder is disposable */ }
    }

    private string CountersPath => Path.Combine(_dir, "counters.json");

    private static LabelDocument LabelWithCounter(Action<CounterVariableOptions>? configure = null, string name = "serial")
    {
        var doc = new LabelDocument();
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter(name, c =>
        {
            c.Remember = true;
            c.Padding = 4;
            c.Prefix = "SN";
            configure?.Invoke(c);
        }));
        return doc;
    }

    private static VariableContext Ctx(ICounterStore? store, long item = 0) => new() { Time = FakeTime.AtLocal(2026, 3, 7), CounterStore = store, ItemIndex = item };

    private static string Value(LabelDocument doc, VariableContext ctx) => VariableEngine.Resolve(doc, ctx).Values["serial"];

    // ------------------------------------------------------------------------------------ plain behaviour

    [Fact]
    public void ACounterVariable_UsesTheSerialCounterRules()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Start = "7"; c.Step = 3; c.Padding = 3; c.Prefix = "A-"; c.Suffix = "!"; }));
        Assert.Equal("A-007!", Value(doc, Ctx(null, 0)));
        Assert.Equal("A-016!", Value(doc, Ctx(null, 3)));

        doc.Variables.Clear();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Kind = CounterKind.Alphanumeric; c.Start = "A9"; }));
        Assert.Equal("B0", Value(doc, Ctx(null, 1)));
    }

    [Fact]
    public void WithoutRemember_EveryRunStartsAtTheStart_EvenWithAStore()
    {
        var store = new MemoryCounterStore();
        var doc = new LabelDocument();
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial"));
        for (int run = 0; run < 3; run++)
        {
            var job = VariableJob.Begin(doc, Ctx(store), 5);
            Assert.True(job.CanPrint);
            Assert.Equal("1", job.Resolve(0).Values["serial"]);
            job.Complete(5);
        }
        Assert.Null(store.Get(CounterKey.For(doc.Id!, "serial")));
    }

    [Fact]
    public void ErrorOverflow_IsAnErrorIssue_AndWrapWraps()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Padding = 2; c.Overflow = CounterOverflow.Error; }));
        var r = VariableEngine.Resolve(doc, Ctx(null, 100));
        Assert.Contains(r.Issues, i => i.MessageKey == VariableMessageKeys.CounterOverflow && i.Severity == ValidationSeverity.Error);
        doc.Variables[0].Counter!.Overflow = CounterOverflow.Wrap;
        Assert.Equal("02", Value(doc, Ctx(null, 101)));
    }

    [Fact]
    public void InvalidSettings_AreReported()
    {
        var doc = new LabelDocument();
        doc.Variables.Add(Vars.Counter("serial", c => c.Start = "abc"));
        Assert.Contains(VariableEngine.Resolve(doc, Ctx(null)).Issues, i => i.MessageKey == VariableMessageKeys.CounterInvalid);
        Assert.Contains(VariableEngine.ValidateDefinitions(doc), i => i.MessageKey == VariableMessageKeys.CounterInvalid);
    }

    // ------------------------------------------------------------------------------------ remembering

    [Fact]
    public void PeekShowsTheNextValue_WithoutReservingIt()
    {
        var store = new MemoryCounterStore();
        var doc = LabelWithCounter();
        Assert.Equal("SN0001", Value(doc, Ctx(store)));
        Assert.Equal("SN0001", Value(doc, Ctx(store)));
        Assert.Null(store.Get(CounterKey.For(doc.Id!, "serial")));

        var job = VariableJob.Begin(doc, Ctx(store), 3);
        job.Complete(3);
        Assert.Equal("SN0004", Value(doc, Ctx(store)));
        var status = CounterAdmin.Peek(doc, "serial", store);
        Assert.Equal(("SN0004", "SN0003", false), (status.NextValue, status.LastValue, status.SettingsChanged));
    }

    [Fact]
    public void TheLastValueIsRemembered_BetweenSessions_InTheFile()
    {
        var doc = LabelWithCounter();
        {
            var store = new FileCounterStore(CountersPath);
            var job = VariableJob.Begin(doc, Ctx(store), 10);
            Assert.Equal("SN0001", job.Resolve(0).Values["serial"]);
            Assert.Equal("SN0010", job.Resolve(9).Values["serial"]);
            Assert.Empty(job.Complete(10));
        }
        {
            var store = new FileCounterStore(CountersPath); // a new "session": nothing in memory
            Assert.Equal("SN0011", Value(doc, Ctx(store)));
            var job = VariableJob.Begin(doc, Ctx(store), 5);
            Assert.Equal("SN0011", job.Resolve(0).Values["serial"]);
            job.Complete(5);
        }
        var final = new FileCounterStore(CountersPath).Get(CounterKey.For(doc.Id!, "serial"))!;
        Assert.Equal((15L, 14L, "SN0015"), (final.Next, final.LastUsedIndex, final.LastValue));
        Assert.True(File.Exists(CountersPath));
        Assert.False(File.Exists(CountersPath + ".tmp"));
    }

    [Fact]
    public void CountersAreKeyedByLabelAndName()
    {
        var store = new MemoryCounterStore();
        var a = LabelWithCounter();
        var b = LabelWithCounter();
        Assert.NotEqual(a.Id, b.Id);
        VariableJob.Begin(a, Ctx(store), 4).Complete(4);
        Assert.Equal("SN0005", Value(a, Ctx(store)));
        Assert.Equal("SN0001", Value(b, Ctx(store)));
        Assert.Equal(CounterKey.For("X", "Serial"), CounterKey.For("X", "serial"));
    }

    [Fact]
    public void ACounterThatRemembersNeedsALabelId()
    {
        var store = new MemoryCounterStore();
        var doc = new LabelDocument(); // no id yet
        doc.Variables.Add(Vars.Counter("serial", c => c.Remember = true));
        var job = VariableJob.Begin(doc, Ctx(store), 2);
        Assert.False(job.CanPrint);
        Assert.Equal(VariableMessageKeys.CounterNeedsLabelId, Assert.Single(job.Issues).MessageKey);
        Assert.Contains(VariableEngine.Resolve(doc, Ctx(store)).Issues, i => i.MessageKey == VariableMessageKeys.CounterNeedsLabelId);
    }

    // ------------------------------------------------------------------------------------ reserve / confirm / crash

    [Fact]
    public void ACrashBetweenReserveAndConfirm_SkipsTheNumbers_AndTellsTheOperator()
    {
        var doc = LabelWithCounter();
        var store = new FileCounterStore(CountersPath);
        var crashed = VariableJob.Begin(doc, Ctx(store), 5);  // reserves positions 0-4 ... and the process dies: Complete is never called
        Assert.Equal("SN0001", crashed.Resolve(0).Values["serial"]);

        var after = new FileCounterStore(CountersPath);        // the next session
        var job = VariableJob.Begin(doc, Ctx(after), 3);
        Assert.True(job.CanPrint);
        Assert.Equal("SN0006", job.Resolve(0).Values["serial"]);   // never reuses SN0001-SN0005
        var warning = Assert.Single(job.Issues);
        Assert.Equal(VariableMessageKeys.CounterUnconfirmed, warning.MessageKey);
        Assert.Equal(new[] { "serial", "0", "4" }, warning.Args);
        job.Complete(3);

        // The operator knows nothing was printed and gives the numbers back; the unused range is not at the end any more, so it stays skipped.
        var pending = after.Get(CounterKey.For(doc.Id!, "serial"))!.Pending;
        Assert.Single(pending);
        Assert.True(CounterAdmin.Reclaim(doc, "serial", after, pending[0].Id));
        Assert.Equal("SN0009", Value(doc, Ctx(after)));
    }

    [Fact]
    public void ReclaimingTheNewestUnconfirmedBlock_GivesTheNumbersBack()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();
        VariableJob.Begin(doc, Ctx(store), 5);          // never completed
        var pending = store.Get(CounterKey.For(doc.Id!, "serial"))!.Pending[0];
        Assert.Equal("SN0006", Value(doc, Ctx(store))); // peek skips the pending block
        Assert.True(CounterAdmin.Reclaim(doc, "serial", store, pending.Id));
        Assert.Equal("SN0001", Value(doc, Ctx(store)));
        Assert.False(CounterAdmin.Reclaim(doc, "serial", store, pending.Id)); // already gone
    }

    [Fact]
    public void AFailedPrint_BurnsNothing_UnlessTheCallerChoosesTo()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();

        var failed = VariableJob.Begin(doc, Ctx(store), 10);
        failed.Cancel();                                  // the connection failed before a single label left
        Assert.Equal("SN0001", Value(doc, Ctx(store)));

        var partial = VariableJob.Begin(doc, Ctx(store), 10);
        partial.Complete(4);                              // four labels were sent before the failure: they are used, the other six come back
        Assert.Equal("SN0005", Value(doc, Ctx(store)));

        var burn = VariableJob.Begin(doc, Ctx(store), 10);
        burn.Complete(10);                                // chosen: burn the whole run because the printer may have printed any of them
        Assert.Equal("SN0015", Value(doc, Ctx(store)));
    }

    [Fact]
    public void UnusedNumbers_AreOnlyReturnedWhenNobodyReservedAfterwards()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();
        var first = VariableJob.Begin(doc, Ctx(store), 5);    // 0-4
        var second = VariableJob.Begin(doc, Ctx(store), 5);   // 5-9 (a second window)
        Assert.Equal("SN0006", second.Resolve(0).Values["serial"]);
        first.Complete(2);                                    // 2-4 cannot be handed back: window two already passed them
        second.Complete(5);
        Assert.Equal("SN0011", Value(doc, Ctx(store)));       // nothing was reused
        var rec = store.Get(CounterKey.For(doc.Id!, "serial"))!;
        Assert.Equal(9, rec.LastUsedIndex);
        Assert.Empty(rec.Pending);
    }

    [Fact]
    public void CompletingTwice_OrAfterAReset_ChangesNothing()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();
        var job = VariableJob.Begin(doc, Ctx(store), 5);
        job.Complete(5);
        job.Complete(0);                                      // the second call is ignored
        Assert.Equal("SN0006", Value(doc, Ctx(store)));

        var job2 = VariableJob.Begin(doc, Ctx(store), 5);
        CounterAdmin.Reset(doc, "serial", store);
        job2.Complete(5);                                     // its reservation was wiped by the reset; it must not move the counter
        Assert.Equal("SN0001", Value(doc, Ctx(store)));
    }

    // ------------------------------------------------------------------------------------ reset / settings

    [Fact]
    public void Reset_StartsOver_OrAtAChosenNumber()
    {
        var doc = LabelWithCounter(c => { c.Start = "1000"; c.Step = 5; });
        var store = new MemoryCounterStore();
        VariableJob.Begin(doc, Ctx(store), 10).Complete(10);
        Assert.Equal("SN1050", Value(doc, Ctx(store)));
        CounterAdmin.Reset(doc, "serial", store);
        Assert.Equal("SN1000", Value(doc, Ctx(store)));
        Assert.True(CounterAdmin.ResetToNumber(doc, "serial", store, 1500));
        Assert.Equal("SN1500", Value(doc, Ctx(store)));
        Assert.False(CounterAdmin.ResetToNumber(doc, "serial", store, 1503)); // not on the step
        Assert.False(CounterAdmin.ResetToNumber(doc, "serial", store, 5));    // before the start
    }

    [Fact]
    public void EditingTheCounterAfterUse_BlocksPrinting_UntilAcceptedOrReset()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();
        VariableJob.Begin(doc, Ctx(store), 5).Complete(5);

        doc.Variables[0].Counter!.Start = "500"; // the operator edits the start
        var warn = VariableEngine.Resolve(doc, Ctx(store)).Issues.Single(i => i.MessageKey == VariableMessageKeys.CounterSettingsChanged);
        Assert.Equal(ValidationSeverity.Warning, warn.Severity);
        Assert.Equal(new[] { "serial", "SN0005" }, warn.Args);

        var blocked = VariableJob.Begin(doc, Ctx(store), 5);
        Assert.False(blocked.CanPrint);
        Assert.Equal(ValidationSeverity.Error, blocked.Issues[0].Severity);
        Assert.True(CounterAdmin.Peek(doc, "serial", store).SettingsChanged);
        Assert.Equal(5, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next); // nothing was reserved

        CounterAdmin.AcceptSettingsChange(doc, "serial", store);
        var ok = VariableJob.Begin(doc, Ctx(store), 2);
        Assert.True(ok.CanPrint);
        Assert.Equal("SN0505", ok.Resolve(0).Values["serial"]); // continues from position 5 under the new start
    }

    // ------------------------------------------------------------------------------------ the file

    [Fact]
    public void TheFile_HasTheDocumentedShape_AndNoLeftovers()
    {
        var doc = LabelWithCounter();
        var store = new FileCounterStore(CountersPath);
        VariableJob.Begin(doc, Ctx(store), 3).Complete(3);
        VariableJob.Begin(doc, Ctx(store), 3).Complete(3);
        string json = File.ReadAllText(CountersPath);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        var entry = parsed.RootElement.GetProperty("counters").GetProperty(CounterKey.For(doc.Id!, "serial"));
        Assert.Equal(6, entry.GetProperty("next").GetInt64());
        Assert.Equal("SN0006", entry.GetProperty("lastValue").GetString());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(File.Exists(CountersPath + ".bak"), "the previous version is kept for manual recovery");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"counters\":{\"a/b\":{\"next\":-5,\"fingerprint\":\"x\"}}}")]
    [InlineData("{\"version\":1,\"counters\":{\"a/b\":{\"next\":\"nine\",\"fingerprint\":\"x\"}}}")]
    [InlineData("{\"version\":1,\"counters\":{\"a/b\":{\"next\":5,\"lastUsedIndex\":9,\"fingerprint\":\"x\"}}}")]
    [InlineData("{\"version\":1,\"counters\":{\"a/b\":{\"next\":5}}}")]
    [InlineData("{\"version\":1,\"counters\":{\"a/b\":{\"next\":5,\"fingerprint\":\"x\",\"pending\":[{\"first\":0,\"count\":99}]}}}")]
    [InlineData("{\"version\":0,\"counters\":{}}")]
    public void ACorruptFile_IsNeverTreatedAsEmpty(string content)
    {
        File.WriteAllText(CountersPath, content);
        var doc = LabelWithCounter();
        var store = new FileCounterStore(CountersPath);

        var read = Assert.Throws<CounterStoreException>(() => store.Get("a/b"));
        Assert.Equal(VariableMessageKeys.CounterStoreCorrupt, read.MessageKey);
        var job = VariableJob.Begin(doc, Ctx(store), 3);
        Assert.False(job.CanPrint);
        Assert.Equal(VariableMessageKeys.CounterStoreCorrupt, job.Issues[0].MessageKey);
        Assert.Equal(content, File.ReadAllText(CountersPath)); // untouched
        // the preview reports it instead of showing a number that could be a duplicate
        Assert.Contains(VariableEngine.Resolve(doc, Ctx(store)).Issues, i => i.MessageKey == VariableMessageKeys.CounterStoreCorrupt);
    }

    [Fact]
    public void RecoverFromCorruption_KeepsTheDamagedFileAside_AndStartsFresh()
    {
        File.WriteAllText(CountersPath, "{ broken");
        var store = new FileCounterStore(CountersPath);
        string? aside = store.RecoverFromCorruption();
        Assert.NotNull(aside);
        Assert.Equal("{ broken", File.ReadAllText(aside!));
        Assert.False(File.Exists(CountersPath));
        var doc = LabelWithCounter();
        Assert.Equal("SN0001", Value(doc, Ctx(store)));
        VariableJob.Begin(doc, Ctx(store), 2).Complete(2);
        Assert.Equal("SN0003", Value(doc, Ctx(store)));
    }

    [Fact]
    public void ANewerFile_IsRefusedWithItsOwnKey()
    {
        File.WriteAllText(CountersPath, "{\"version\":99,\"counters\":{}}");
        var ex = Assert.Throws<CounterStoreException>(() => new FileCounterStore(CountersPath).Get("a/b"));
        Assert.Equal(VariableMessageKeys.CounterStoreNewer, ex.MessageKey);
    }

    [Fact]
    public void AHugeFile_IsRefused()
    {
        File.WriteAllBytes(CountersPath, new byte[5 * 1024 * 1024]);
        Assert.Equal(VariableMessageKeys.CounterStoreCorrupt, Assert.Throws<CounterStoreException>(() => new FileCounterStore(CountersPath).Get("k")).MessageKey);
    }

    [Fact]
    public void ALockedFile_ReportsBusyInsteadOfHanging()
    {
        File.WriteAllText(CountersPath, new CounterBook().ToJson()); // a look at a file that does not exist takes no lock at all
        using var hold = new FileStream(CountersPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new FileCounterStore(CountersPath, lockTimeout: TimeSpan.FromMilliseconds(150));
        var ex = Assert.Throws<CounterStoreException>(() => store.Get("k"));
        Assert.Equal(VariableMessageKeys.CounterStoreBusy, ex.MessageKey);
    }

    [Fact]
    public void ALockHeldByAnotherProgram_IsRespected_UntilThatProgramEnds()
    {
        // A second operating-system process holds the lock file; this is the real cross-process case, not a thread.
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps)) return; // not Windows: nothing to start
        File.WriteAllText(CountersPath, new CounterBook().ToJson());
        string ready = Path.Combine(_dir, "ready.txt");
        string lockFile = CountersPath.Replace("'", "''") + ".lock";
        // The script is passed as an encoded command so no quoting rules can bite.
        string script = "$f = [System.IO.File]::Open('" + lockFile + "', 'OpenOrCreate', 'ReadWrite', 'None'); " +
                        "Set-Content -Path '" + ready.Replace("'", "''") + "' -Value 'x'; Start-Sleep -Seconds 4; $f.Close()";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var info = new System.Diagnostics.ProcessStartInfo(ps, "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var child = System.Diagnostics.Process.Start(info)!;
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(ready) && waited.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
            Assert.True(File.Exists(ready), "the helper process did not start");

            var impatient = new FileCounterStore(CountersPath, lockTimeout: TimeSpan.FromMilliseconds(300));
            Assert.Equal(VariableMessageKeys.CounterStoreBusy, Assert.Throws<CounterStoreException>(() => impatient.Get("k")).MessageKey);

            // a patient caller waits for the other program to finish and then succeeds
            var patient = new FileCounterStore(CountersPath, lockTimeout: TimeSpan.FromSeconds(30));
            var reservation = patient.Reserve("k", "f", 3);
            Assert.Equal(0, reservation.First);
        }
        finally
        {
            if (!child.HasExited) child.Kill(true);
        }
    }

    [Fact]
    public void ManyThreadsAndManyStoreObjects_NeverGetTheSameNumber()
    {
        // Each store object stands for another program on the same file; the lock file is the only thing that keeps them apart.
        var doc = LabelWithCounter(c => c.Padding = 6);
        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                var store = new FileCounterStore(CountersPath, lockTimeout: TimeSpan.FromSeconds(30));
                for (int i = 0; i < 15; i++)
                {
                    var job = VariableJob.Begin(doc, Ctx(store), 4);
                    Assert.True(job.CanPrint, string.Join(";", job.Issues));
                    for (int k = 0; k < 4; k++) seen.Add(long.Parse(job.Resolve(k).Values["serial"][2..]));
                    job.Complete(4);
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Empty(errors);
        Assert.Equal(8 * 15 * 4, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());           // no duplicates
        Assert.Equal(Enumerable.Range(1, 480).Select(i => (long)i), seen.OrderBy(x => x)); // and no gaps: every reserved block was fully used
        Assert.Equal("SN000481", Value(doc, Ctx(new FileCounterStore(CountersPath))));
    }

    [Fact]
    public void TheMemoryStore_IsThreadSafeToo()
    {
        var doc = LabelWithCounter();
        var store = new MemoryCounterStore();
        var firsts = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 200, _ => firsts.Add(store.Reserve(CounterKey.For(doc.Id!, "serial"), "f", 3).First));
        Assert.Equal(200, firsts.Distinct().Count());
        Assert.Equal(600, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);
    }

    [Fact]
    public void ARememberingCounterWithoutAStore_IsAWarningInAPreview_ButAnErrorWhenPrinting()
    {
        var doc = LabelWithCounter();
        var preview = VariableEngine.Resolve(doc, Ctx(null));
        Assert.Equal("SN0001", preview.Values["serial"]);
        var warning = Assert.Single(preview.Issues);
        Assert.Equal((ValidationSeverity.Warning, VariableMessageKeys.CounterNoStore), (warning.Severity, warning.MessageKey));

        // A print must never start the numbers over without saying so.
        var job = VariableJob.Begin(doc, Ctx(null), 5);
        Assert.False(job.CanPrint);
        var error = Assert.Single(job.Issues);
        Assert.Equal((ValidationSeverity.Error, VariableMessageKeys.CounterNoStore, "serial"), (error.Severity, error.MessageKey, error.Args[0]));
        Assert.Empty(job.Cancel());
    }

    [Fact]
    public void AStoreThatChangesBetweenLookAndReserve_ReportsAKeyInsteadOfThrowingAnythingElse()
    {
        var store = new MemoryCounterStore();
        string key = CounterKey.For("label", "serial");
        store.Reserve(key, "fp-old", 1);
        var ex = Assert.Throws<CounterStoreException>(() => store.Reserve(key, "fp-new", 1));
        Assert.Equal(VariableMessageKeys.CounterSettingsChanged, ex.MessageKey);
        Assert.Equal("serial", ex.Args[0]);
        Assert.Equal(1, store.Get(key)!.Next); // nothing was reserved by the refused call

        // a run too large to reserve is an issue, not an exception
        var doc = LabelWithCounter();
        var job = VariableJob.Begin(doc, Ctx(store), CounterBook.MaxReservation + 1);
        Assert.False(job.CanPrint);
        Assert.Equal(VariableMessageKeys.CounterOverflow, Assert.Single(job.Issues).MessageKey);
    }

    [Fact]
    public void LookingAtACounterThatNeverWasUsed_CreatesNothingOnDisk()
    {
        string folder = Path.Combine(_dir, "not-created-yet");
        var store = new FileCounterStore(Path.Combine(folder, "counters.json"));
        Assert.Null(store.Get("anything"));
        Assert.False(Directory.Exists(folder));
        var doc = LabelWithCounter();
        Assert.Equal("SN0001", Value(doc, Ctx(store)));  // a preview only reads
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void TheBook_RoundTripsItsJson()
    {
        var book = new CounterBook();
        var t = DateTimeOffset.Parse("2026-03-07T10:00:00Z");
        var r = book.Reserve("l/c", "fp", 10, t);
        book.Complete(r, 4, "X4", t);
        book.Reserve("l/c", "fp", 5, t);
        var again = CounterBook.FromJson(book.ToJson(), "p");
        Assert.Equal(book.Get("l/c"), again.Get("l/c")! with { Pending = book.Get("l/c")!.Pending }); // records match
        Assert.Single(again.Get("l/c")!.Pending);
        Assert.Equal(3, again.Get("l/c")!.LastUsedIndex);
    }

    [Fact]
    public void TheBook_CapsItsSize()
    {
        var book = new CounterBook();
        var t = DateTimeOffset.UnixEpoch;
        for (int i = 0; i < CounterBook.MaxRecords + 50; i++) book.Reset("k" + i, "f", 1, t.AddSeconds(i));
        Assert.Equal(CounterBook.MaxRecords, book.Count);
        Assert.Null(book.Get("k0"));
        Assert.NotNull(book.Get("k" + (CounterBook.MaxRecords + 49)));
        for (int i = 0; i < CounterBook.MaxPending + 10; i++) book.Reserve("p", "f", 1, t);
        Assert.Equal(CounterBook.MaxPending, book.Get("p")!.Pending.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Reserve("p", "f", 0, t));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Reserve("p", "f", CounterBook.MaxReservation + 1, t));
    }
}
