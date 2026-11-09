using System.Text;
using Rotula.Core.Merge;
using Rotula.Core.Validation;

namespace Rotula.Tests;

/// <summary>A data file cannot make the reader allocate far more than the file itself holds (every row is padded to the widest one).</summary>
public class MergeTableLimitsTests
{
    private static string Header(int columns) => string.Join(",", Enumerable.Range(1, columns).Select(i => "c" + i));

    private static string Csv(int columns, int rows)
    {
        var sb = new StringBuilder(Header(columns)).Append('\n');
        for (int r = 0; r < rows; r++) sb.Append("x\n");
        return sb.ToString();
    }

    private static void AssertRefused(MergeTable table, string key)
    {
        Assert.Equal(0, table.RowCount);
        var issue = Assert.Single(table.Issues, i => i.MessageKey == key);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void CsvWiderThanTheColumnLimit_IsRefusedWithAnIssue()
    {
        var table = CsvReader.Parse(Csv(columns: 12, rows: 3), new CsvOptions { MaxColumns = 10 });

        AssertRefused(table, "Merge.Table.TooWide");
    }

    [Fact]
    public void CsvAtTheColumnLimit_IsAccepted()
    {
        var table = CsvReader.Parse(Csv(columns: 10, rows: 3), new CsvOptions { MaxColumns = 10 });

        Assert.Equal(10, table.Columns.Count);
        Assert.Equal(3, table.RowCount);
        Assert.Empty(table.Issues);
    }

    [Fact]
    public void CsvWithTooManyCellsAfterPadding_IsRefused_AndTheBoundaryIsExact()
    {
        // 10 columns x 5 rows = 50 cells: allowed with a budget of 50, refused with 49.
        Assert.Equal(5, CsvReader.Parse(Csv(10, 5), new CsvOptions { MaxCells = 50 }).RowCount);
        AssertRefused(CsvReader.Parse(Csv(10, 5), new CsvOptions { MaxCells = 49 }), "Merge.Table.TooLarge");
    }

    [Fact]
    public void TheClassicAmplificationFile_IsRefusedQuicklyWithDefaultLimits()
    {
        // One header with 100,000 commas plus 100,000 one-cell rows would need about 80 GB once every row is padded to the widest.
        var sb = new StringBuilder(new string(',', 100_000)).Append('\n');
        for (int r = 0; r < 100_000; r++) sb.Append("a\n");

        long before = GC.GetAllocatedBytesForCurrentThread();
        var table = CsvReader.Parse(sb.ToString());
        long used = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertRefused(table, "Merge.Table.TooWide");
        Assert.True(used < 200L * 1024 * 1024, "allocated " + used);
    }

    [Fact]
    public void ARealisticHundredThousandRowFile_StillLoadsWithDefaultLimits()
    {
        var sb = new StringBuilder("sku,name,qty,ean,lot\n");
        for (int r = 0; r < 100_000; r++) sb.Append("A").Append(r).Append(",Bolt,2,4006381333931,L1\n");

        var table = CsvReader.Parse(sb.ToString());

        Assert.Equal(100_000, table.RowCount);
        Assert.Empty(table.Issues);
    }

    [Fact]
    public void XlsxWiderThanTheColumnLimit_IsRefused()
    {
        using var ms = new MemoryStream();
        var row = Enumerable.Range(1, 30).ToDictionary(i => "Col" + i, i => (object?)("v" + i));
        MiniExcelLibs.MiniExcel.SaveAs(ms, new[] { row });
        ms.Position = 0;

        var table = XlsxReader.Read(ms, new XlsxOptions { MaxColumns = 10 });

        AssertRefused(table, "Merge.Table.TooWide");
    }

    [Fact]
    public void XlsxWithTooManyCells_IsRefused()
    {
        using var ms = new MemoryStream();
        var rows = Enumerable.Range(1, 20).Select(r => new Dictionary<string, object?> { ["A"] = r, ["B"] = r, ["C"] = r, ["D"] = r }).ToList();
        MiniExcelLibs.MiniExcel.SaveAs(ms, rows);
        ms.Position = 0;

        // 4 columns x 20 rows = 80 cells.
        var table = XlsxReader.Read(ms, new XlsxOptions { MaxCells = 79 });

        AssertRefused(table, "Merge.Table.TooLarge");
    }

    [Fact]
    public void XlsxWithinTheLimits_StillReads()
    {
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, new[] { new Dictionary<string, object?> { ["Name"] = "Bolt", ["Qty"] = 2 } });
        ms.Position = 0;

        var table = XlsxReader.Read(ms);

        Assert.Equal(1, table.RowCount);
        Assert.Empty(table.Issues);
    }
}

/// <summary>Empty formatted cells far to the right must not make a usable sheet look too wide.</summary>
public class XlsxEmptyColumnTests
{
    [Fact]
    public void EmptyCellsBeyondTheColumnLimit_AreIgnored()
    {
        using var ms = new MemoryStream();
        var row = new Dictionary<string, object?>();
        for (int i = 1; i <= 30; i++) row["Col" + i] = i <= 2 ? "v" + i : null;
        MiniExcelLibs.MiniExcel.SaveAs(ms, new[] { row }, printHeader: false);
        ms.Position = 0;

        var table = XlsxReader.Read(ms, new XlsxOptions { MaxColumns = 10, HasHeader = false });

        Assert.Empty(table.Issues);
        Assert.Equal(1, table.RowCount);
        Assert.Equal("v2", table.Rows[0][1]);
    }
}
