using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>Batch generation: single stream, jobs, copies, counters, invalid-record policy and goldens.</summary>
public class DataMergeBatchTests
{
    private const string Csv = "Name,Ean,Copies\nBolt,4006381333931,2\nNut,4006381333932,1\nWasher,4006381333931,3\n";

    private static LabelDocument Doc() => CoreTestSupport.NewDoc(
        new DataFieldElement { Id = "name", VariableName = "Name", X = 20, Y = 20 },
        new DataFieldElement { Id = "serial", VariableName = "#Serial", X = 20, Y = 80 },
        new BarcodeElement { Id = "ean", Symbology = BarcodeSymbology.Ean13, Data = "{{Ean}}", X = 20, Y = 140 });

    private static MergeOptions Opts()
    {
        var o = new MergeOptions();
        o.Counters.Add(new SerialCounter { Name = "Serial", Start = "1", Padding = 4, Prefix = "SN" });
        return o;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void SingleStream_SkipsInvalidRecordAndReportsIt()
    {
        var report = new MergeReport();
        var set = new MergeDataSet(Doc(), CsvReader.Parse(Csv), Opts());
        string zpl = MergeBatch.GenerateSingleStream(Doc(), set, report);

        Assert.Equal(3, report.Processed);
        Assert.Equal(2, report.Emitted);
        Assert.Equal(new[] { 2 }, report.SkippedRecords);
        Assert.True(report.HasErrors);
        Assert.False(report.Stopped);
        var err = report.Issues.Single(i => i.Issue.Severity == ValidationSeverity.Error);
        Assert.Equal(2, err.RecordNumber);
        Assert.StartsWith("Barcode.", err.Issue.MessageKey);
        Assert.Equal(2, zpl.Split("^XA").Length - 1);
        // Counter follows the record position, so skipping record 2 leaves a visible gap (SN0002).
        Assert.Contains("SN0001", zpl);
        Assert.DoesNotContain("SN0002", zpl);
        Assert.Contains("SN0003", zpl);
        CoreTestSupport.AssertGolden("merge/single_stream_skip", Normalize(zpl));
    }

    [Fact]
    public void StopPolicy_EndsBatchAtFirstInvalidRecord()
    {
        var o = Opts();
        o.OnInvalidRecord = InvalidRecordPolicy.Stop;
        var report = new MergeReport();
        var set = new MergeDataSet(Doc(), CsvReader.Parse(Csv), o);
        var labels = MergeBatch.Labels(Doc(), set, report).ToList();
        Assert.Single(labels);
        Assert.True(report.Stopped);
        Assert.Equal(new[] { 2 }, report.SkippedRecords);
        Assert.Equal(2, report.Processed);
    }

    [Fact]
    public void CopiesFromColumn_PrintQuantity_And_RepeatBlock()
    {
        var csv = CsvReader.Parse("Name,Ean,Copies\nBolt,4006381333931,2\nWasher,4006381333931,3\n");
        var o = Opts();
        o.CopiesColumn = "Copies";
        string pq = MergeBatch.GenerateSingleStream(Doc(), new MergeDataSet(Doc(), csv, o));
        Assert.Contains("^PQ2,0,1,N\n^XZ", pq);
        Assert.Contains("^PQ3,0,1,N\n^XZ", pq);
        CoreTestSupport.AssertGolden("merge/copies_pq", Normalize(pq));

        o.CopiesMode = CopiesMode.RepeatBlock;
        string rep = MergeBatch.GenerateSingleStream(Doc(), new MergeDataSet(Doc(), csv, o));
        Assert.Equal(5, rep.Split("^XA").Length - 1);
        Assert.DoesNotContain("^PQ2", rep);
        CoreTestSupport.AssertGolden("merge/copies_repeat", Normalize(rep));
    }

    [Fact]
    public void FixedCopiesAndDocumentQuantity()
    {
        var table = CsvReader.Parse("Name,Ean,Copies\nBolt,4006381333931,1\n");
        var doc = Doc();
        doc.Print.Quantity = 7;
        var keep = MergeBatch.GenerateSingleStream(doc, new MergeDataSet(doc, table, Opts()));
        Assert.Contains("^PQ7,0,1,N", keep);

        var o = Opts();
        o.CopiesPerRecord = 4;
        var fixedCopies = MergeBatch.GenerateSingleStream(doc, new MergeDataSet(doc, table, o));
        Assert.Contains("^PQ4,0,1,N", fixedCopies);
        Assert.DoesNotContain("^PQ7", fixedCopies);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-2")]
    public void InvalidCopiesColumn_IsARecordError_NotACrash(string value)
    {
        var table = new MergeTable(new[] { "Name", "Ean", "Copies" }, new[] { new[] { "Bolt", "4006381333931", value } });
        var o = Opts();
        o.CopiesColumn = "Copies";
        var report = new MergeReport();
        string zpl = MergeBatch.GenerateSingleStream(Doc(), new MergeDataSet(Doc(), table, o), report);
        Assert.Equal("", zpl);
        Assert.Contains(report.Issues, i => i.Issue.MessageKey == "Merge.Copies.Invalid");
    }

    [Fact]
    public void Jobs_AreChunkedAndLazy()
    {
        var rows = Enumerable.Range(1, 7).Select(n => new[] { "Item" + n, "4006381333931", "1" });
        var table = new MergeTable(new[] { "Name", "Ean", "Copies" }, rows);
        var o = Opts();
        o.MaxRecordsPerJob = 3;
        var report = new MergeReport();
        var jobs = MergeBatch.Jobs(Doc(), new MergeDataSet(Doc(), table, o), report);
        Assert.Equal(0, report.Processed); // nothing evaluated until enumerated
        var list = jobs.ToList();
        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { 3, 3, 1 }, list.Select(j => j.Split("^XA").Length - 1));
        Assert.Equal(7, report.Emitted);
        CoreTestSupport.AssertGolden("merge/jobs_3_third", Normalize(list[2]));
        CoreTestSupport.AssertGolden("merge/jobs_3_first", Normalize(list[0]));
    }

    [Fact]
    public void Jobs_SingleJobWhenUnlimited_AndEmptyTableYieldsNothing()
    {
        var table = CsvReader.Parse("Name,Ean,Copies\nA,4006381333931,1\nB,4006381333931,1\n");
        Assert.Single(MergeBatch.Jobs(Doc(), new MergeDataSet(Doc(), table, Opts())));
        Assert.Empty(MergeBatch.Jobs(Doc(), new MergeDataSet(Doc(), CsvReader.Parse("Name,Ean,Copies\n"), Opts())));
    }

    [Fact]
    public void CounterOverflowInErrorMode_FailsOnlyThoseRecords()
    {
        var o = new MergeOptions();
        o.Counters.Add(new SerialCounter { Name = "Serial", Start = "8", Padding = 1, Overflow = CounterOverflow.Error });
        var table = new MergeTable(new[] { "Name", "Ean" }, Enumerable.Range(0, 4).Select(_ => new[] { "X", "4006381333931" }));
        var report = new MergeReport();
        MergeBatch.GenerateSingleStream(Doc(), new MergeDataSet(Doc(), table, o), report);
        Assert.Equal(2, report.Emitted);
        Assert.Equal(new[] { 3, 4 }, report.SkippedRecords);
        Assert.Contains(report.Issues, i => i.Issue.MessageKey == "Merge.Counter.Overflow" && i.RecordNumber == 3);
    }

    [Fact]
    public void WriteSingleStream_MatchesString_AndIsDeterministic()
    {
        var table = CsvReader.Parse(Csv);
        var set = new MergeDataSet(Doc(), table, Opts());
        using var sw = new StringWriter();
        var report = MergeBatch.WriteSingleStream(Doc(), set, sw);
        Assert.Equal(MergeBatch.GenerateSingleStream(Doc(), set), sw.ToString());
        Assert.Equal(2, report.Emitted);
    }

    [Fact]
    public void LargeBatch_StreamsWithoutBuildingOneString()
    {
        var table = new MergeTable(new[] { "Name", "Ean" },
            Enumerable.Range(0, 20000).Select(i => new[] { "Item" + i, "4006381333931" }));
        var o = Opts();
        o.MaxRecordsPerJob = 5000;
        var set = new MergeDataSet(Doc(), table, o);
        var report = new MergeReport();
        int jobs = 0;
        foreach (var job in MergeBatch.Jobs(Doc(), set, report)) jobs++;
        Assert.Equal(4, jobs);
        Assert.Equal(20000, report.Emitted);
    }

    [Fact]
    public void XlsxEndToEnd_ProducesLabels()
    {
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, new[]
        {
            new Dictionary<string, object?> { ["Name"] = "Bolt", ["Ean"] = "4006381333931", ["Copies"] = 2 },
        });
        ms.Position = 0;
        var table = XlsxReader.Read(ms);
        string zpl = MergeBatch.GenerateSingleStream(Doc(), new MergeDataSet(Doc(), table, Opts()));
        Assert.Contains("^FDBolt^FS", zpl);
    }
}
