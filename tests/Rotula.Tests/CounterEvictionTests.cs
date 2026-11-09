using Rotula.Core.Variables;
using Rotula.Core.Variables.Counters;

namespace Rotula.Tests;

/// <summary>A serial number is never reissued, not even when the counters file holds more counters than it keeps in full.</summary>
public class CounterEvictionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 7, 8, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    [Fact]
    public void AnEvictedCounterContinuesWhereItStopped_NeverAtZero()
    {
        var book = new CounterBook(maxRecords: 2, maxTotalRecords: 10);
        book.Reserve("l/a", "fp", 5, At(1));
        book.Reserve("l/b", "fp", 3, At(2));
        book.Reserve("l/c", "fp", 2, At(3)); // a is now the oldest of three: it is compacted

        var again = book.Reserve("l/a", "fp", 4, At(4));

        Assert.Equal(5, again.First); // the old behaviour restarted at 0 and printed duplicate serial numbers
    }

    [Fact]
    public void ACompactedCounterKeepsItsPositionAndFingerprint()
    {
        var book = new CounterBook(maxRecords: 1, maxTotalRecords: 10);
        book.Reserve("l/a", "fp", 7, At(1));
        book.Reserve("l/b", "fp", 1, At(2));

        var rec = book.Get("l/a");

        Assert.NotNull(rec);
        Assert.Equal(7, rec!.Next);
        var ex = Assert.Throws<CounterStoreException>(() => book.Reserve("l/a", "other", 1, At(3)));
        Assert.Equal(VariableMessageKeys.CounterSettingsChanged, ex.MessageKey);
    }

    [Fact]
    public void ACounterKeepsItsPositionAcrossSeveralEvictions()
    {
        var book = new CounterBook(maxRecords: 2, maxTotalRecords: 50);
        long expected = 0;
        for (int round = 0; round < 5; round++)
        {
            var r = book.Reserve("l/a", "fp", 3, At(round * 10));
            Assert.Equal(expected, r.First);
            expected += 3;
            for (int k = 0; k < 4; k++) book.Reserve($"l/other{round}-{k}", "fp", 1, At(round * 10 + k + 1));
        }
    }

    [Fact]
    public void TheNewestCountersStayInFull_SoTheirPendingBlocksCanStillBeConfirmed()
    {
        var book = new CounterBook(maxRecords: 2, maxTotalRecords: 10);
        book.Reserve("l/a", "fp", 1, At(1));
        var b = book.Reserve("l/b", "fp", 5, At(2));
        book.Reserve("l/c", "fp", 1, At(3));

        Assert.True(book.Complete(b, 5, "SN0005", At(4)));
        Assert.Equal("SN0005", book.Get("l/b")!.LastValue);
    }

    [Fact]
    public void WhenTheFileIsFull_ANewCounterIsRefusedWithAClearKey_AndKnownCountersKeepWorking()
    {
        var book = new CounterBook(maxRecords: 2, maxTotalRecords: 3);
        book.Reserve("l/a", "fp", 1, At(1));
        book.Reserve("l/b", "fp", 1, At(2));
        book.Reserve("l/c", "fp", 1, At(3));

        var ex = Assert.Throws<CounterStoreException>(() => book.Reserve("l/d", "fp", 1, At(4)));
        Assert.Equal(VariableMessageKeys.CounterStoreFull, ex.MessageKey);
        Assert.Throws<CounterStoreException>(() => book.Reset("l/d", "fp", 0, At(5)));

        Assert.Equal(1, book.Reserve("l/a", "fp", 1, At(6)).First);
        Assert.Null(book.Get("l/d"));
    }

    [Fact]
    public void ResetOfACompactedCounterStartsItAgainOnPurpose()
    {
        var book = new CounterBook(maxRecords: 1, maxTotalRecords: 10);
        book.Reserve("l/a", "fp", 7, At(1));
        book.Reserve("l/b", "fp", 1, At(2));

        book.Reset("l/a", "fp", 0, At(3));

        Assert.Equal(0, book.Reserve("l/a", "fp", 1, At(4)).First);
    }

    [Fact]
    public void CompactedCountersSurviveTheFile_AndOnlyThenTheFormatVersionGoesUp()
    {
        var plain = new CounterBook();
        plain.Reserve("l/a", "fp", 1, At(1));
        Assert.Contains("\"version\": 1", plain.ToJson());

        var book = new CounterBook(maxRecords: 1, maxTotalRecords: 10);
        book.Reserve("l/a", "fp", 7, At(1));
        book.Reserve("l/b", "fp", 2, At(2));
        string json = book.ToJson();
        // An older build ignores fields it does not know and would restart "l/a"; a higher version makes it refuse the file instead.
        Assert.Contains("\"version\": 2", json);

        var back = CounterBook.FromJson(json, "counters.json");
        Assert.Equal(7, back.Reserve("l/a", "fp", 1, At(3)).First);
        Assert.Equal(2, back.Reserve("l/b", "fp", 1, At(4)).First);
    }

    [Fact]
    public void AVersion1FileFromAnEarlierBuild_IsStillRead()
    {
        const string v1 = """
            {
              "comment": "x",
              "version": 1,
              "counters": {
                "lab1/serial": { "next": 12, "lastUsedIndex": 11, "lastValue": "SN0012", "fingerprint": "fp", "pending": [], "updated": "2026-03-07T08:00:00.0000000+00:00" }
              }
            }
            """;

        var book = CounterBook.FromJson(v1, "counters.json");

        Assert.Equal(12, book.Reserve("lab1/serial", "fp", 1, At(1)).First);
        Assert.Contains("\"version\": 1", book.ToJson());
    }

    [Fact]
    public void WithTheDefaultLimits_MoreThanTheFullRecordsOfCountersNeverRepeatANumber()
    {
        var book = new CounterBook();
        int n = CounterBook.MaxRecords + 100;
        for (int i = 0; i < n; i++) book.Reserve($"l/c{i}", "fp", 10, At(i));

        // The oldest counters were compacted; reading the file back and reserving again must continue at 10, not 0.
        var back = CounterBook.FromJson(book.ToJson(), "counters.json");
        for (int i = 0; i < 100; i++) Assert.Equal(10, back.Reserve($"l/c{i}", "fp", 1, At(n + i)).First);
    }
}
