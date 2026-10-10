using System.Text;
using MiniExcelLibs;
using Rotula.Core.Elements;
using Rotula.Core.Merge;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.Tests;

/// <summary>CSV, XLSX, mapping and counter tests of the data-merge engine.</summary>
public class DataMergeTests
{
    // ---- CSV ------------------------------------------------------------------------------

    [Fact]
    public void Csv_BasicHeaderAndRows()
    {
        var t = CsvReader.Parse("Name,Qty\r\nBolt,5\r\nNut,7\r\n");
        Assert.Equal(new[] { "Name", "Qty" }, t.Columns);
        Assert.Equal(2, t.RowCount);
        Assert.Equal(new[] { "Nut", "7" }, t.Rows[1]);
    }

    [Fact]
    public void Csv_QuotesEmbeddedCommasNewlinesAndDoubledQuotes()
    {
        var t = CsvReader.Parse("A,B\n\"x, y\",\"line1\nline2\"\n\"say \"\"hi\"\"\",z\n");
        Assert.Equal("x, y", t.Rows[0][0]);
        Assert.Equal("line1\nline2", t.Rows[0][1]);
        Assert.Equal("say \"hi\"", t.Rows[1][0]);
    }

    [Fact]
    public void Csv_BomIsStripped()
    {
        var t = CsvReader.Parse("﻿Name,Qty\n1,2\n");
        Assert.Equal("Name", t.Columns[0]);
        using var ms = new MemoryStream(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Name;Qty\n1;2\n")).ToArray());
        Assert.Equal("Name", CsvReader.Read(ms).Columns[0]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a,b,c\n1,2,3\n", ',')]
    [InlineData("\"a,b;c\";d\n1;2\n", ';')]
    [InlineData("single\nvalue\n", ',')]
    public void Csv_DelimiterAutoDetect(string text, char expected) =>
        Assert.Equal(expected, CsvReader.DetectDelimiter(text));

    [Fact]
    public void Csv_ExplicitDelimiterOverridesDetection()
    {
        var t = CsvReader.Parse("a,b;c\n1,2;3\n", new CsvOptions { Delimiter = ';' });
        Assert.Equal(new[] { "a,b", "c" }, t.Columns);
    }

    [Fact]
    public void Csv_BlankLinesSkippedShortRowsPaddedLongRowsExtendHeader()
    {
        var t = CsvReader.Parse("A,B,C\n\n1,2\n\n4,5,6,7\n");
        Assert.Equal(2, t.RowCount);
        // A row wider than the header extends the table (Column4) so no data is silently lost.
        Assert.Equal(new[] { "A", "B", "C", "Column4" }, t.Columns);
        Assert.Equal(new[] { "1", "2", "", "" }, t.Rows[0]);
        Assert.Equal(new[] { "4", "5", "6", "7" }, t.Rows[1]);
    }

    [Fact]
    public void Csv_NoHeaderGeneratesColumnNames_AndEmptyOrDuplicateHeadersAreFixed()
    {
        var t = CsvReader.Parse("1,2\n3,4\n", new CsvOptions { HasHeader = false });
        Assert.Equal(new[] { "Column1", "Column2" }, t.Columns);
        Assert.Equal(2, t.RowCount);

        var t2 = CsvReader.Parse("Id,,id,Id\n1,2,3,4\n");
        Assert.Equal(new[] { "Id", "Column2", "id_2", "Id_3" }, t2.Columns);
    }

    [Fact]
    public void Csv_UnterminatedQuoteWarnsButKeepsData()
    {
        var t = CsvReader.Parse("A,B\n1,\"oops\n");
        Assert.Contains(t.Issues, i => i.MessageKey == "Merge.Csv.UnterminatedQuote");
        Assert.Equal("oops\n", t.Rows[0][1]);
    }

    [Fact]
    public void Csv_EmptyInputAndNoTrailingNewline()
    {
        Assert.Empty(CsvReader.Parse("").Columns);
        Assert.Equal("2", CsvReader.Parse("A,B\n1,2").Rows[0][1]);
        Assert.Equal("", CsvReader.Parse("A,B\n1,\"\"\n").Rows[0][1]);
    }

    [Fact]
    public void Csv_AccentsSurvive()
    {
        var t = CsvReader.Parse("Nombre\nAño ¿qué?\n");
        Assert.Equal("Año ¿qué?", t.Rows[0][0]);
    }

    // ---- XLSX -----------------------------------------------------------------------------

    private static MemoryStream MakeWorkbook(string sheet = "Data")
    {
        var ms = new MemoryStream();
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Sku"] = "A100", ["Qty"] = 3, ["Price"] = 2.5, ["When"] = new DateTime(2026, 3, 1) },
            new() { ["Sku"] = "B200", ["Qty"] = 12, ["Price"] = 0.1, ["When"] = new DateTime(2026, 3, 2, 8, 30, 0) },
            new() { ["Sku"] = "C300", ["Qty"] = null, ["Price"] = null, ["When"] = null },
        };
        MiniExcel.SaveAs(ms, rows, sheetName: sheet);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Xlsx_ReadsHeaderRowsAndInvariantValues()
    {
        using var ms = MakeWorkbook();
        var t = XlsxReader.Read(ms);
        Assert.Equal(new[] { "Sku", "Qty", "Price", "When" }, t.Columns);
        Assert.Equal(3, t.RowCount);
        Assert.Equal("A100", t.Rows[0][0]);
        Assert.Equal("3", t.Rows[0][1]);
        Assert.Equal("2.5", t.Rows[0][2]);
        Assert.Equal("2026-03-01", t.Rows[0][3]);
        Assert.Equal("2026-03-02 08:30:00", t.Rows[1][3]);
        Assert.Equal(new[] { "C300", "", "", "" }, t.Rows[2]);
    }

    [Fact]
    public void Xlsx_SheetSelectionAndNoHeader()
    {
        using var ms = MakeWorkbook("Labels");
        Assert.Equal(new[] { "Labels" }, XlsxReader.GetSheetNames(ms));
        ms.Position = 0;
        var t = XlsxReader.Read(ms, new XlsxOptions { SheetName = "Labels", HasHeader = false });
        Assert.Equal(new[] { "Column1", "Column2", "Column3", "Column4" }, t.Columns);
        Assert.Equal(4, t.RowCount);
        Assert.Equal("Sku", t.Rows[0][0]);
    }

    // ---- counters -------------------------------------------------------------------------

    private static string C(SerialCounter c, long i) => c.ValueAt(i);

    [Fact]
    public void Counter_NumericStartStepPaddingPrefixSuffix()
    {
        var c = new SerialCounter { Start = "7", Step = 3, Padding = 5, Prefix = "SN-", Suffix = "/X" };
        Assert.Equal("SN-00007/X", C(c, 0));
        Assert.Equal("SN-00010/X", C(c, 1));
        Assert.Equal("SN-00304/X", C(c, 99));
        Assert.Equal("#Counter", c.VirtualColumn);
    }

    [Fact]
    public void Counter_NumericUnpaddedGrowsAndAllowsNegative()
    {
        var c = new SerialCounter { Start = "8", Step = -5 };
        Assert.Equal("8", C(c, 0));
        Assert.Equal("3", C(c, 1));
        Assert.Equal("-2", C(c, 2));
        Assert.Equal("1000000", C(new SerialCounter { Start = "999999" }, 1));
    }

    [Fact]
    public void Counter_NumericPaddedOverflowWrapsOrErrors()
    {
        var wrap = new SerialCounter { Start = "98", Padding = 2 };
        Assert.Equal("99", C(wrap, 1));
        Assert.Equal("00", C(wrap, 2));
        Assert.Equal("01", C(wrap, 3));
        var err = new SerialCounter { Start = "98", Padding = 2, Overflow = CounterOverflow.Error };
        Assert.True(err.TryGetValue(1, out _));
        Assert.False(err.TryGetValue(2, out _));
        Assert.Throws<OverflowException>(() => err.ValueAt(2));
        var down = new SerialCounter { Start = "1", Step = -1, Padding = 2 };
        Assert.Equal("99", C(down, 2));
    }

    [Theory]
    [InlineData("A9", 1, "B0")]
    [InlineData("A9", 0, "A9")]
    [InlineData("AZ", 1, "BA")]
    [InlineData("A0", 10, "B0")]
    [InlineData("a9", 1, "b0")]
    [InlineData("0Z", 1, "1A")]
    [InlineData("Z9", 1, "A0")] // wraps by default
    public void Counter_AlphanumericOdometer(string start, long index, string expected) =>
        Assert.Equal(expected, C(new SerialCounter { Kind = CounterKind.Alphanumeric, Start = start }, index));

    [Fact]
    public void Counter_AlphanumericOverflowAndStep()
    {
        var c = new SerialCounter { Kind = CounterKind.Alphanumeric, Start = "Y9", Step = 11, Overflow = CounterOverflow.Error };
        Assert.Equal("Y9", C(c, 0));
        Assert.True(c.TryGetValue(0, out _));
        Assert.False(c.TryGetValue(1, out _)); // Y9 + 11 = past Z9
        var s = new SerialCounter { Kind = CounterKind.Alphanumeric, Start = "AA", Step = 13 };
        Assert.Equal("AN", C(s, 1));
        Assert.Equal("BA", C(s, 2));
    }

    [Fact]
    public void Counter_Validation()
    {
        Assert.Equal("Merge.Counter.Start", new SerialCounter { Start = "x1" }.Validate());
        Assert.Equal("Merge.Counter.Start", new SerialCounter { Kind = CounterKind.Alphanumeric, Start = "A-1" }.Validate());
        Assert.Equal("Merge.Counter.Name", new SerialCounter { Name = "1bad" }.Validate());
        Assert.Equal("Merge.Counter.Padding", new SerialCounter { Padding = 40 }.Validate());
        Assert.Null(new SerialCounter().Validate());
        Assert.Throws<InvalidOperationException>(() => new SerialCounter { Start = "q" }.ValueAt(0));
    }

    [Fact]
    public void Counter_LongRangeFailsInsteadOfWrapping()
    {
        var c = new SerialCounter { Start = long.MaxValue.ToString() };
        Assert.False(c.TryGetValue(1, out _));
    }

    // ---- mapping --------------------------------------------------------------------------

    private static LabelDocument DocWith(params string[] fields)
    {
        var doc = CoreTestSupport.NewDoc();
        int y = 10;
        foreach (var f in fields)
        {
            doc.Elements.Add(new DataFieldElement { VariableName = f, X = 10, Y = y });
            y += 40;
        }
        return doc;
    }

    [Fact]
    public void Mapping_CaseInsensitive_ReportsUnmappedAndUnused()
    {
        var doc = DocWith("name", "Missing");
        var table = CsvReader.Parse("NAME,Extra\nBolt,x\n");
        var set = new MergeDataSet(doc, table);
        Assert.Contains(set.Issues, i => i.MessageKey == "Merge.Placeholder.Unmapped" && i.Args[0] == "Missing");
        Assert.Contains(set.Issues, i => i.MessageKey == "Merge.Column.Unused" && i.Args[0] == "Extra");
        Assert.DoesNotContain(set.Issues, i => i.Args.Count > 0 && i.Args[0] == "name");
        Assert.Equal(0, set.Mapping["name"]);
        Assert.Equal("Bolt", set.GetRecordData(0)["name"]);
    }

    [Fact]
    public void Mapping_OverrideAndMissingTarget()
    {
        var doc = DocWith("Product");
        var table = CsvReader.Parse("Title,Other\nBolt,x\n");
        var opts = new MergeOptions();
        opts.ColumnOverrides["product"] = "title";
        var set = new MergeDataSet(doc, table, opts);
        Assert.Equal("Bolt", set.GetRecordData(0)["Product"]);
        Assert.Equal("Other", set.Issues.Single(i => i.MessageKey == "Merge.Column.Unused").Args[0]);

        var bad = new MergeOptions();
        bad.ColumnOverrides["Product"] = "Nope";
        var set2 = new MergeDataSet(doc, table, bad);
        Assert.Contains(set2.Issues, i => i.MessageKey == "Merge.Mapping.ColumnNotFound" && i.Args[0] == "Product" && i.Args[1] == "Nope");
    }

    [Fact]
    public void Mapping_CounterPlaceholderIsNotUnmapped_AndPreviewIncludesIt()
    {
        var doc = DocWith("Name", "#Counter", "#Other");
        var table = CsvReader.Parse("Name\nA\nB\nC\n");
        var opts = new MergeOptions();
        opts.Counters.Add(new SerialCounter { Start = "100", Padding = 6 });
        var set = new MergeDataSet(doc, table, opts);
        Assert.DoesNotContain(set.Issues, i => i.MessageKey == "Merge.Placeholder.Unmapped" && i.Args[0] == "#Counter");
        Assert.Contains(set.Issues, i => i.MessageKey == "Merge.Placeholder.Unmapped" && i.Args[0] == "#Other");
        Assert.Equal("000102", set.GetRecordData(2)["#Counter"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => set.GetRecordData(3));
    }

    [Fact]
    public void Mapping_InvalidCounterIsAnError()
    {
        var opts = new MergeOptions();
        opts.Counters.Add(new SerialCounter { Start = "zz" });
        var set = new MergeDataSet(DocWith("A"), CsvReader.Parse("A\n1\n"), opts);
        Assert.Contains(set.Issues, i => i.Severity == ValidationSeverity.Error && i.MessageKey == "Merge.Counter.Invalid");
    }

    [Fact]
    public void Preview_DictionaryFeedsZplGenerator()
    {
        var doc = DocWith("Name");
        var set = new MergeDataSet(doc, CsvReader.Parse("Name\nBolt\nNut\n"));
        var zpl = ZplGenerator.Generate(doc, set.GetRecordData(1)).Zpl;
        Assert.Contains("^FDNut^FS", zpl);
    }
}
