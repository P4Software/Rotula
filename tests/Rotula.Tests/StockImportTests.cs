using System.Text;
using MiniExcelLibs;
using Rotula.Core.Merge;
using Rotula.Core.Stocks;
using static Rotula.Tests.StockTestSupport;

namespace Rotula.Tests;

/// <summary>Supplies-list import with small synthetic CSV and xlsx files (no vendor data).</summary>
public class StockImportTests
{
    private const string Csv =
        "SKU,Description,Unit Price,Size\n" +
        "A-100,Shipping label,9.99,4x6in (101.6x152.4mm)\n" +
        "A-101,Small label direct thermal,3.50,\"2.25 x 1.25 in\"\n" +
        "A-102,No unit given,1.00,100 x 150\n" +
        ",Nameless row,1.00,2 x 1 in\n" +
        "A-104,Contradiction 4x6in and 50x50mm,2.00,\n" +
        "A-105,Label 100x150mm thermal transfer,3.00,\n" +
        "A-100,Duplicate of the first,5.00,4 x 6 in\n" +
        "A-107,Too small,1.00,1 x 0.1 in\n" +
        "A-108,No size anywhere,1.00,\n";

    private static MergeTable Table(string csv) => CsvReader.Parse(csv);

    [Fact]
    public void Csv_ImportsConfidentRowsAndReportsTheRest()
    {
        var r = SuppliesImporter.Import(Table(Csv), new SuppliesImportOptions { GeneratedOn = "2026-01-01" });

        Assert.Equal(new[] { "A-100", "A-101", "A-105" }, r.Imported.Select(i => i.Entry.PartNumber).ToArray());
        var a = r.Imported[0];
        Assert.Equal(ImportConfidence.High, a.Confidence);
        Assert.Equal(2, a.Row);
        Assert.Equal(101.6, a.Entry.WidthMm);
        Assert.Equal(152.4, a.Entry.HeightMm);
        Assert.Equal(4, a.Entry.WidthIn);
        Assert.Equal(6, a.Entry.HeightIn);
        Assert.Equal("user-A-100", a.Entry.Id);
        Assert.Equal(new[] { "user-import" }, a.Entry.Sources.ToArray());
        Assert.False(a.Entry.Verified);
        Assert.Equal(StockOrigin.User, a.Entry.Origin);
        Assert.Equal("Shipping label", a.Entry.Name);

        var b = r.Imported[1];
        Assert.Equal(ImportConfidence.High, b.Confidence);
        Assert.Equal(StockMedia.DirectThermal, b.Entry.Media);
        Assert.Equal(57.15, b.Entry.WidthMm);

        var c = r.Imported[2];
        Assert.Equal(ImportConfidence.Medium, c.Confidence);
        Assert.Equal(100, c.Entry.WidthMm);
        Assert.Equal(StockMedia.ThermalTransfer, c.Entry.Media);

        var keys = r.Warnings.Select(w => w.MessageKey).ToList();
        Assert.Contains(StockMessageKeys.ImportRowUnitAmbiguous, keys);
        Assert.Contains(StockMessageKeys.ImportRowNoPartNumber, keys);
        Assert.Contains(StockMessageKeys.ImportRowSizeConflict, keys);
        Assert.Contains(StockMessageKeys.ImportRowDuplicate, keys);
        Assert.Contains(StockMessageKeys.ImportRowSizeOutOfRange, keys);
        Assert.Contains(StockMessageKeys.ImportRowNoSize, keys);

        var amb = r.Warnings.First(w => w.MessageKey == StockMessageKeys.ImportRowUnitAmbiguous);
        Assert.Equal(new[] { "4", "A-102", "100 x 150" }, amb.Args.ToArray());
    }

    [Fact]
    public void PriceColumnsAreIgnoredAndNeverStored()
    {
        var r = SuppliesImporter.Import(Table(Csv));
        Assert.Equal(new[] { "Unit Price" }, r.Map.IgnoredPriceColumns.ToArray());
        Assert.Contains(r.Warnings, w => w.MessageKey == StockMessageKeys.ImportPriceColumnIgnored && w.Args[0] == "Unit Price");
        string json = r.ToCatalogJson("2026-01-01");
        Assert.DoesNotContain("9.99", json);
        Assert.DoesNotContain("3.50", json);
        Assert.DoesNotContain("price", json.Replace("Prices are never imported.", ""), StringComparison.OrdinalIgnoreCase);
        foreach (var e in r.Entries) Assert.DoesNotContain("9.99", e.Name + e.Notes);
    }

    [Fact]
    public void PriceColumnWithAnUnhelpfulHeaderIsRecognisedByItsContent()
    {
        const string csv = "Part,Description,Col3\nP-1,Label 4x6in,$12.50\nP-2,Label 2x1in,$3.00\nP-3,Label 3x2in,€4,10\nP-4,Label 3x1in,$1.25\n";
        var r = SuppliesImporter.Import(Table(csv));
        Assert.Contains("Col3", r.Map.IgnoredPriceColumns);
        Assert.True(r.Imported.Count >= 3);
        Assert.DoesNotContain("12.50", r.ToCatalogJson());
    }

    [Fact]
    public void OutputIsAValidUserCatalogue()
    {
        var r = SuppliesImporter.Import(Table(Csv), new SuppliesImportOptions { GeneratedOn = "2026-01-01" });
        string json = r.ToCatalogJson("2026-01-01");
        var report = StockCatalogValidator.Validate(Utf8(json));
        Assert.True(report.IsValid, string.Join(";", report.Errors));
        var loaded = StockCatalog.FromJson(Utf8(json));
        Assert.Empty(loaded.Issues);
        Assert.Equal(3, loaded.Count);
        Assert.All(loaded.Entries, e => Assert.Contains("user-import", e.Sources));
        Assert.Contains(loaded.Metadata.Sources, s => s.Id == "user-import");
        // The same input always produces the same file.
        Assert.Equal(json, SuppliesImporter.Import(Table(Csv)).ToCatalogJson("2026-01-01"));
    }

    [Fact]
    public void AssumedUnit_IsUsedOnlyWhenTheUserSaysSo()
    {
        const string csv = "Part Number,Size\nQ-1,100 x 150\n";
        Assert.True(SuppliesImporter.Import(Table(csv)).IsEmpty);
        var r = SuppliesImporter.Import(Table(csv), new SuppliesImportOptions { AssumedUnit = StockSizeUnit.Millimeters });
        var e = Assert.Single(r.Imported);
        Assert.Equal(ImportConfidence.Medium, e.Confidence);
        Assert.Equal(100, e.Entry.WidthMm);
    }

    [Fact]
    public void HeaderUnitStatesTheUnit()
    {
        const string csv = "Item,Label Size (in)\nH-1,4 x 6\nH-2,2.25 x 1.25\n";
        var r = SuppliesImporter.Import(Table(csv));
        Assert.Equal(2, r.Imported.Count);
        Assert.All(r.Imported, i => Assert.Equal(ImportConfidence.High, i.Confidence));
        Assert.Equal(101.6, r.Imported[0].Entry.WidthMm);
    }

    [Fact]
    public void WidthAndHeightColumns_NeedAUnit()
    {
        var withUnit = SuppliesImporter.Import(Table("Part No;Ancho (mm);Alto (mm);Precio\nS-1;100;150;12,5\nS-2;50,8;25,4;3,1\n"));
        Assert.Equal(2, withUnit.Imported.Count);
        Assert.Equal(50.8, withUnit.Imported[1].Entry.WidthMm);
        Assert.Contains("Precio", withUnit.Map.IgnoredPriceColumns);

        var without = SuppliesImporter.Import(Table("Part No,Width,Height\nS-1,100,150\n"));
        Assert.True(without.IsEmpty);
        Assert.Contains(without.Warnings, w => w.MessageKey == StockMessageKeys.ImportRowUnitAmbiguous);

        var unitColumn = SuppliesImporter.Import(Table("Part No,Width,Height,Unit\nS-1,4,6,in\nS-2,100,150,mm\n"));
        Assert.Equal(2, unitColumn.Imported.Count);
        Assert.Equal(101.6, unitColumn.Imported[0].Entry.WidthMm);
        Assert.Equal(100, unitColumn.Imported[1].Entry.WidthMm);
    }

    [Fact]
    public void SpanishHeadersAreRecognised()
    {
        var r = SuppliesImporter.Import(Table("Código,Descripción,Tamaño\nE-1,Etiqueta de envío,102 x 152 mm\n"));
        var e = Assert.Single(r.Imported);
        Assert.Equal("E-1", e.Entry.PartNumber);
        Assert.Equal(102, e.Entry.WidthMm);
    }

    [Fact]
    public void MissingColumnsAreExplained()
    {
        var noPart = SuppliesImporter.Import(Table("Foo,Bar\nsome words,more words\nand more,words again\n"));
        Assert.True(noPart.IsEmpty);
        Assert.Contains(noPart.Warnings, w => w.MessageKey == StockMessageKeys.ImportNoPartColumn);

        var noSize = SuppliesImporter.Import(Table("Part Number,Brand\nA1,X\n"));
        Assert.Contains(noSize.Warnings, w => w.MessageKey == StockMessageKeys.ImportNoSizeColumn);

        var empty = SuppliesImporter.Import(new MergeTable(new[] { "Part Number", "Size" }));
        Assert.Contains(empty.Warnings, w => w.MessageKey == StockMessageKeys.ImportNoRows);
    }

    [Fact]
    public void ColumnsPickedFromContentAreFlaggedAsGuesses()
    {
        const string csv = "Alpha,Beta\nAB-100,Thing 4x6in\nAB-101,Thing 2x1in\nAB-102,Thing 3x2in\nAB-103,Thing 3x3in\n";
        var r = SuppliesImporter.Import(Table(csv));
        Assert.Contains(r.Warnings, w => w.MessageKey == StockMessageKeys.ImportColumnGuessed && w.Args[0] == "part");
        Assert.Equal(4, r.Imported.Count);
    }

    [Fact]
    public void RowCapIsApplied()
    {
        var sb = new StringBuilder("Part Number,Size\n");
        for (int i = 0; i < 50; i++) sb.Append("R-").Append(i).Append(",2 x 1 in\n");
        var r = SuppliesImporter.Import(Table(sb.ToString()), new SuppliesImportOptions { MaxRows = 10 });
        Assert.Equal(10, r.Imported.Count);
        Assert.Contains(r.Warnings, w => w.MessageKey == StockMessageKeys.ImportTooManyRows);
    }

    [Fact]
    public void HostileCellsAreCleaned()
    {
        var table = new MergeTable(new[] { "Part Number", "Description", "Size" });
        table.AddRow(new[] { "X\u0007-1/2 \"bad\" id", "line1\nline2\u0000" + new string('z', 5000), "2 x 1 in" });
        var r = SuppliesImporter.Import(table);
        var e = Assert.Single(r.Imported).Entry;
        Assert.Matches("^[A-Za-z0-9._-]+$", e.Id);
        Assert.True(e.Id.Length <= StockLimits.MaxIdLength);
        Assert.True(e.Name.Length <= 300);
        Assert.DoesNotContain('\n', e.Name);
        Assert.Empty(StockCatalog.FromJson(Utf8(r.ToCatalogJson())).Issues);
    }

    // ---- Files ----------------------------------------------------------------------------------------

    [Fact]
    public void Xlsx_ImportsTheSameAsCsv()
    {
        using var dir = new TempDir();
        string path = dir.File("supplies.xlsx");
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Part Number"] = "X-1", ["Description"] = "Test roll", ["Price"] = 19.95, ["Size"] = "4x6in (101.6x152.4mm)" },
            new() { ["Part Number"] = "X-2", ["Description"] = "Test small", ["Price"] = 4.5, ["Size"] = "50 x 25 mm" },
            new() { ["Part Number"] = "X-3", ["Description"] = "No unit", ["Price"] = 1.0, ["Size"] = "10 x 15" },
        };
        MiniExcel.SaveAs(path, rows);

        var r = SuppliesImporter.ImportFile(path);
        Assert.Equal(new[] { "X-1", "X-2" }, r.Imported.Select(i => i.Entry.PartNumber).ToArray());
        Assert.Contains("Price", r.Map.IgnoredPriceColumns);
        Assert.Contains("supplies.xlsx", r.Imported[0].Entry.Notes);
        Assert.Contains(r.Warnings, w => w.MessageKey == StockMessageKeys.ImportRowUnitAmbiguous);
        Assert.DoesNotContain("19.95", r.ToCatalogJson());
    }

    [Fact]
    public void Xlsx_NumericWidthAndHeightCells()
    {
        using var dir = new TempDir();
        string path = dir.File("dims.xlsx");
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["SKU"] = "N-1", ["Width (mm)"] = 100, ["Height (mm)"] = 150.5, ["Cost"] = 7 },
        };
        MiniExcel.SaveAs(path, rows);
        var r = SuppliesImporter.ImportFile(path);
        var e = Assert.Single(r.Imported).Entry;
        Assert.Equal(100, e.WidthMm);
        Assert.Equal(150.5, e.HeightMm);
    }

    [Fact]
    public void CsvFile_WithBomAndSemicolons()
    {
        using var dir = new TempDir();
        string path = dir.File("list.csv");
        File.WriteAllText(path, "Referencia;Descripcion;Medidas\nR-1;Etiqueta;4 x 6 in\n", new UTF8Encoding(true));
        var r = SuppliesImporter.ImportFile(path);
        Assert.Single(r.Imported);
    }

    [Fact]
    public void DamagedAndOversizedFilesNeverThrow()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("fake.xlsx"), "this is not a zip file");
        var fake = SuppliesImporter.ImportFile(dir.File("fake.xlsx"));
        Assert.True(fake.IsEmpty);
        Assert.Contains(fake.Warnings, w => w.MessageKey == StockMessageKeys.ImportReadFailed);

        File.WriteAllBytes(dir.File("bin.csv"), new byte[] { 0, 1, 2, 3, 0xFF, 0xFE, 0, 0, 13, 10, 7 });
        Assert.True(SuppliesImporter.ImportFile(dir.File("bin.csv")).IsEmpty);

        File.WriteAllText(dir.File("notes.docx"), "x");
        Assert.Contains(SuppliesImporter.ImportFile(dir.File("notes.docx")).Warnings, w => w.MessageKey == StockMessageKeys.ImportUnsupportedFormat);

        using (var f = File.Create(dir.File("huge.csv"))) f.SetLength(SuppliesImporter.MaxFileBytes + 1);
        Assert.Contains(SuppliesImporter.ImportFile(dir.File("huge.csv")).Warnings, w => w.MessageKey == StockMessageKeys.ImportFileTooLarge);

        Assert.Contains(SuppliesImporter.ImportFile(dir.File("missing.csv")).Warnings, w => w.MessageKey == StockMessageKeys.ImportReadFailed);
    }

    [Fact]
    public void ImportedCatalogueCanBeLoadedFromTheUserFolderAndSearched()
    {
        using var dir = new TempDir();
        var r = SuppliesImporter.Import(Table(Csv));
        File.WriteAllText(dir.File("my-supplies.json"), r.ToCatalogJson());
        var all = StockCatalog.LoadCombined(dir.Path);
        Assert.Equal(StockCatalog.LoadBundled().Count + 3, all.Count);
        Assert.Equal("user-A-101", all.Search("A-101").Hits[0].Entry.Id);
        Assert.Equal("user-A-100", all.Search("a100").Hits[0].Entry.Id);
    }
}
