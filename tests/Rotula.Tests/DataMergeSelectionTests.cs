using System.Text.RegularExpressions;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Variables.Counters;

namespace Rotula.Tests;

/// <summary>Merging only some rows: which rows print, and which serial numbers they get.</summary>
public class DataMergeSelectionTests
{
    private static MergeTable Table(int rows) =>
        new(new[] { "n" }, Enumerable.Range(1, rows).Select(i => new[] { i.ToString() }));

    private static LabelDocument PlainLabel()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "Item {{n}}" });
        return doc;
    }

    private static LabelDocument SerialLabel()
    {
        var doc = PlainLabel();
        doc.EnsureId();
        doc.Variables.Add(Vars.Counter("serial", c => { c.Remember = true; c.Padding = 4; c.Prefix = "SN"; }));
        doc.Elements.Add(new TextElement { X = 10, Y = 40, Text = "{{serial}}" });
        return doc;
    }

    private static string[] Serials(IEnumerable<string> zpl) =>
        zpl.SelectMany(z => Regex.Matches(z, @"SN\d{4}").Select(m => m.Value)).ToArray();

    [Fact]
    public void WithoutASelection_EveryRowIsMerged_AsBefore()
    {
        var doc = PlainLabel();
        var set = new MergeDataSet(doc, Table(4));
        var labels = MergeBatch.Labels(doc, set).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, labels.Select(l => l.RecordNumber));
        Assert.Equal(new[] { 1, 2, 3, 4 }, labels.Select(l => l.ItemNumber));
        Assert.Equal(4, set.BatchCount);
    }

    [Fact]
    public void ASelection_MergesOnlyThoseRows_AndReportsTheirRowNumbers()
    {
        var doc = PlainLabel();
        var options = new MergeOptions { SelectedRecords = new[] { 2, 5 } };
        var set = new MergeDataSet(doc, Table(6), options);
        var report = new MergeReport();
        var labels = MergeBatch.Labels(doc, set, report).ToList();

        Assert.Equal(new[] { 3, 6 }, labels.Select(l => l.RecordNumber));
        Assert.Contains("Item 3", labels[0].Zpl);
        Assert.Contains("Item 6", labels[1].Zpl);
        Assert.Equal(2, report.Processed);
        Assert.Equal(2, set.BatchCount);
    }

    [Fact]
    public void AnEmptySelection_ProducesNothing_AndReservesNothing()
    {
        var doc = SerialLabel();
        var store = new MemoryCounterStore();
        var set = new MergeDataSet(doc, Table(3), new MergeOptions { SelectedRecords = Array.Empty<int>(), CounterStore = store });
        Assert.Empty(MergeBatch.Labels(doc, set));
        Assert.Null(store.Get(CounterKey.For(doc.Id!, "serial")));
    }

    [Fact]
    public void ARememberedCounter_NumbersByPositionInTheSelection_NotByRow()
    {
        var doc = SerialLabel();
        var store = new MemoryCounterStore();
        var set = new MergeDataSet(doc, Table(6), new MergeOptions { SelectedRecords = new[] { 1, 4 }, CounterStore = store });
        var labels = MergeBatch.Labels(doc, set).ToList();

        Assert.Equal(new[] { "SN0001", "SN0002" }, Serials(labels.Select(l => l.Zpl)));
        // Only two numbers were used, not six: the next label of any later run carries SN0003.
        Assert.Equal(2, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);
    }

    [Fact]
    public void ALegacyMergeCounter_FollowsThePosition_AndThePreviewOfARowAgrees()
    {
        var doc = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        doc.Elements.Add(new TextElement { X = 10, Y = 10, Text = "{{#Serial}}" });
        var options = new MergeOptions { SelectedRecords = new[] { 2, 4 } };
        options.Counters.Add(new SerialCounter { Name = "Serial", Start = "1", Padding = 4, Prefix = "SN" });
        var set = new MergeDataSet(doc, Table(5), options);

        Assert.Equal(new[] { "SN0001", "SN0002" }, Serials(MergeBatch.Labels(doc, set).Select(l => l.Zpl)));
        Assert.Equal("SN0002", set.GetRecordData(4)["#Serial"]); // the canvas preview shows what that row will print
    }

    [Fact]
    public void JobBlocks_ConfirmByPosition_SoAFailedSecondJobKeepsOnlyTheSentNumbers()
    {
        var doc = SerialLabel();
        var store = new MemoryCounterStore();
        var options = new MergeOptions { SelectedRecords = new[] { 0, 2, 4, 5 }, CounterStore = store, MaxRecordsPerJob = 2 };
        var set = new MergeDataSet(doc, Table(6), options);
        var job = VariableJob.Begin(doc, set.CreateBatchContext(), set.BatchCount);
        options.Job = job;

        var jobs = MergeBatch.JobBlocks(doc, set).ToList();
        Assert.Equal(new[] { 3, 6 }, jobs.Select(j => j.LastRecordNumber)); // row numbers for the user
        Assert.Equal(new[] { 2, 4 }, jobs.Select(j => j.ItemsThrough));      // positions for the counter

        job.Complete(jobs[0].ItemsThrough); // only the first job reached the printer
        Assert.Equal(2, store.Get(CounterKey.For(doc.Id!, "serial"))!.Next);
    }

    [Fact]
    public void ASelectionThatIsOutOfRangeOrRepeated_IsRefused()
    {
        var doc = PlainLabel();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MergeDataSet(doc, Table(3), new MergeOptions { SelectedRecords = new[] { 3 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MergeDataSet(doc, Table(3), new MergeOptions { SelectedRecords = new[] { -1 } }));
        Assert.Throws<ArgumentException>(() => new MergeDataSet(doc, Table(3), new MergeOptions { SelectedRecords = new[] { 1, 1 } }));
    }

    [Fact]
    public void ASelectionInAnyOrder_IsProcessedInTheOrderGiven()
    {
        var doc = SerialLabel();
        var set = new MergeDataSet(doc, Table(4), new MergeOptions { SelectedRecords = new[] { 3, 0 }, CounterStore = new MemoryCounterStore() });
        var labels = MergeBatch.Labels(doc, set).ToList();
        Assert.Equal(new[] { 4, 1 }, labels.Select(l => l.RecordNumber));
        Assert.Equal(1, set.PositionOf(0));
    }
}
