using System.Text.RegularExpressions;
using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;

namespace Rotula.Tests;

/// <summary>
/// <see cref="MergeBatch.JobLabels"/> hands batch printing in picture mode the labels of each job (values and copies per record) instead of
/// their ZPL text. It must group the batch exactly as <see cref="MergeBatch.JobBlocks"/> does, so serial numbers are confirmed per job in the
/// same way in both print modes: same jobs, same last record, same batch position, same skipped records and the same report.
/// </summary>
public class MergeBatchJobLabelsTests
{
    /// <summary>Seven records; record 3 carries an EAN-13 with a wrong check digit, so it is skipped (or stops the batch).</summary>
    private const string SevenRecords =
        "Name,Ean,Copies\n" +
        "A,4006381333931,2\n" +
        "B,4006381333931,1\n" +
        "C,4006381333932,1\n" +
        "D,4006381333931,3\n" +
        "E,4006381333931,1\n" +
        "F,4006381333931,2\n" +
        "G,4006381333931,1\n";

    private static LabelDocument Doc() => CoreTestSupport.NewDoc(
        new DataFieldElement { Id = "name", VariableName = "Name", X = 20, Y = 20 },
        new DataFieldElement { Id = "serial", VariableName = "#Serial", X = 20, Y = 80 },
        new BarcodeElement { Id = "ean", Symbology = BarcodeSymbology.Ean13, Data = "{{Ean}}", X = 20, Y = 140 });

    /// <summary>Options with a serial counter and the given job size; the copies come from the Copies column.</summary>
    private static MergeOptions Opts(int perJob)
    {
        var o = new MergeOptions { MaxRecordsPerJob = perJob, CopiesColumn = "Copies" };
        o.Counters.Add(new SerialCounter { Name = "Serial", Start = "1", Padding = 4, Prefix = "SN" });
        return o;
    }

    /// <summary>
    /// Runs both groupings on separate data sets and reports (built from the same options), so neither can see the other's state, and checks
    /// they agree job for job and in every report figure.
    /// </summary>
    private static (List<MergedJob> Blocks, List<MergedPictureJob> Pictures) BothGroupings(Func<MergeOptions> options, string csv = SevenRecords)
    {
        var doc = Doc();
        var blockReport = new MergeReport();
        var pictureReport = new MergeReport();
        var blocks = MergeBatch.JobBlocks(doc, new MergeDataSet(doc, CsvReader.Parse(csv), options()), blockReport).ToList();
        var pictures = MergeBatch.JobLabels(doc, new MergeDataSet(doc, CsvReader.Parse(csv), options()), pictureReport).ToList();

        Assert.Equal(blocks.Select(j => (j.LastRecordNumber, j.ItemsThrough, j.Labels)),
            pictures.Select(j => (j.LastRecordNumber, j.ItemsThrough, j.LabelCount)));
        // The same records in the same jobs: the serial numbers of each picture job's labels are exactly the ones in the matching block's ZPL.
        Assert.Equal(blocks.Select(j => string.Join(",", Regex.Matches(j.Zpl, @"SN\d{4}").Select(m => m.Value))),
            pictures.Select(j => string.Join(",", j.Labels.Select(l => l.Values!["#Serial"]))));
        Assert.All(pictures, j => Assert.Equal(j.LabelCount, j.Labels.Count));
        // A picture job does not keep the ZPL text of its labels: only its length was needed, to cut the jobs like the ZPL ones.
        Assert.All(pictures.SelectMany(j => j.Labels), l => Assert.Equal(string.Empty, l.Zpl));
        Assert.Equal(blockReport.Processed, pictureReport.Processed);
        Assert.Equal(blockReport.Emitted, pictureReport.Emitted);
        Assert.Equal(blockReport.SkippedRecords, pictureReport.SkippedRecords);
        Assert.Equal(blockReport.Stopped, pictureReport.Stopped);
        Assert.Equal(blockReport.Issues.Select(i => (i.RecordNumber, i.Issue.MessageKey)), pictureReport.Issues.Select(i => (i.RecordNumber, i.Issue.MessageKey)));
        return (blocks, pictures);
    }

    [Fact]
    public void JobLabels_GroupsSevenRecordsInJobsOfThree_ExactlyLikeJobBlocks_AndSkipsTheSameRecord()
    {
        var (_, pictures) = BothGroupings(() => Opts(3));

        // Record 3 is left out, so the jobs hold records 1,2,4 / 5,6,7: three records each, and the last job ends at batch position 7.
        Assert.Equal(new[] { (4, 4), (7, 7) }, pictures.Select(j => (j.LastRecordNumber, j.ItemsThrough)));
        Assert.Equal(new[] { 1, 2, 4, 5, 6, 7 }, pictures.SelectMany(j => j.Labels).Select(l => l.RecordNumber));
    }

    [Fact]
    public void JobLabels_CarryTheValuesAndCopiesOfEachRecord()
    {
        var (_, pictures) = BothGroupings(() => Opts(3));
        var labels = pictures.SelectMany(j => j.Labels).ToList();

        // The copies of each record come from its own Copies cell, and the values carry the serial number its ZPL carries.
        Assert.Equal(new[] { 2, 1, 3, 1, 2, 1 }, labels.Select(l => l.Copies));
        Assert.Equal(new[] { "A", "B", "D", "E", "F", "G" }, labels.Select(l => l.Values!["Name"]));
        // Serial numbers follow the batch position, so the skipped record 3 leaves the gap SN0003.
        Assert.Equal(new[] { "SN0001", "SN0002", "SN0004", "SN0005", "SN0006", "SN0007" }, labels.Select(l => l.Values!["#Serial"]));
    }

    [Fact]
    public void JobLabels_WithTheStopPolicy_StopsWhereJobBlocksStops()
    {
        var (_, pictures) = BothGroupings(() =>
        {
            var o = Opts(3);
            o.OnInvalidRecord = InvalidRecordPolicy.Stop;
            return o;
        });

        // The job in progress (records 1 and 2) is still handed over, then the sequence ends.
        var only = Assert.Single(pictures);
        Assert.Equal(2, only.LastRecordNumber);
        Assert.Equal(2, only.LabelCount);
    }

    [Fact]
    public void JobLabels_WithTickedRecords_FollowTheBatchPosition_NotTheRowNumber()
    {
        var (_, pictures) = BothGroupings(() =>
        {
            var o = Opts(2);
            o.SelectedRecords = new[] { 1, 3, 4, 6 };   // records 2, 4, 5 and 7
            return o;
        });

        Assert.Equal(new[] { (4, 2), (7, 4) }, pictures.Select(j => (j.LastRecordNumber, j.ItemsThrough)));
        Assert.Equal(new[] { 1, 2, 3, 4 }, pictures.SelectMany(j => j.Labels).Select(l => l.ItemNumber));
    }

    [Fact]
    public void JobLabels_SplitBeforeALabelThatWouldPassTheCharacterLimit_LikeJobBlocks()
    {
        var doc = Doc();
        int one = MergeBatch.Labels(doc, new MergeDataSet(doc, CsvReader.Parse(SevenRecords), Opts(0))).First().Zpl.Length;
        var (blocks, pictures) = BothGroupings(() =>
        {
            var o = Opts(0);   // "one job" would otherwise hold every record
            o.MaxJobCharacters = 2L * one + 1;
            return o;
        });

        Assert.True(pictures.Count > 1);
        Assert.All(blocks, b => Assert.True(b.Zpl.Length <= 2L * one + 1));
    }

    [Fact]
    public void JobLabels_IsLazy_AndAnEmptySelectionYieldsNothing()
    {
        var doc = Doc();
        var report = new MergeReport();
        var jobs = MergeBatch.JobLabels(doc, new MergeDataSet(doc, CsvReader.Parse(SevenRecords), Opts(3)), report);
        Assert.Equal(0, report.Processed);   // nothing is generated until the jobs are enumerated
        using (var e = jobs.GetEnumerator())
        {
            Assert.True(e.MoveNext());
            Assert.Equal(4, report.Processed);   // the first job only needed the first four records
        }

        Assert.Empty(MergeBatch.JobLabels(doc, new MergeDataSet(doc, CsvReader.Parse("Name,Ean,Copies\n"), Opts(3))));
    }
}
