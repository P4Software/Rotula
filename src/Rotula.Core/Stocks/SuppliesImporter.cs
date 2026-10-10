using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rotula.Core.Merge;
using Rotula.Core.Validation;

namespace Rotula.Core.Stocks;

/// <summary>How sure the importer is about an imported row. Rows it is not sure about are never imported.</summary>
public enum ImportConfidence
{
    /// <summary>The size is in a dedicated column with a stated unit, or two statements of it agree (4x6in and 101.6x152.4mm).</summary>
    High,
    /// <summary>One statement of the size with a stated unit, found in free text such as the description.</summary>
    Medium,
}

/// <summary>Settings for <see cref="SuppliesImporter"/>.</summary>
public sealed class SuppliesImportOptions
{
    /// <summary>Brand used for rows without a brand column. Default <c>User</c>.</summary>
    public string Brand { get; set; } = "User";
    /// <summary>
    /// Unit to assume for sizes that state none. Null (default) means "do not guess": such rows are reported, not imported.
    /// Set this only when the user said so explicitly.
    /// </summary>
    public StockSizeUnit? AssumedUnit { get; set; }
    /// <summary>Most data rows read.</summary>
    public int MaxRows { get; set; } = 20000;
    /// <summary>Worksheet to read from an .xlsx file; null for the first one.</summary>
    public string? SheetName { get; set; }
    /// <summary>Date written to the catalogue metadata (yyyy-MM-dd). Null uses today's UTC date.</summary>
    public string? GeneratedOn { get; set; }
    /// <summary>Name of the source file, noted on each entry. Set by <see cref="SuppliesImporter.ImportFile"/>.</summary>
    public string? SourceName { get; set; }
}

/// <summary>Which table columns were recognised.</summary>
public sealed class SuppliesColumnMap
{
    /// <summary>Column index of the part number, or -1.</summary>
    public int PartNumber { get; internal set; } = -1;
    /// <summary>Column index of the description or name, or -1.</summary>
    public int Description { get; internal set; } = -1;
    /// <summary>Column index of a combined size text, or -1.</summary>
    public int Size { get; internal set; } = -1;
    /// <summary>Column index of the width, or -1.</summary>
    public int Width { get; internal set; } = -1;
    /// <summary>Column index of the height, or -1.</summary>
    public int Height { get; internal set; } = -1;
    /// <summary>Column index of the brand, or -1.</summary>
    public int Brand { get; internal set; } = -1;
    /// <summary>Column index of the print technology, or -1.</summary>
    public int Media { get; internal set; } = -1;
    /// <summary>Column index of a unit column (applies to width/height), or -1.</summary>
    public int Unit { get; internal set; } = -1;
    /// <summary>Unit stated in the size column header, such as <c>Size (in)</c>; unknown otherwise.</summary>
    public StockSizeUnit SizeHeaderUnit { get; internal set; }
    /// <summary>Unit stated in the width/height column headers; unknown otherwise.</summary>
    public StockSizeUnit DimensionHeaderUnit { get; internal set; }
    /// <summary>Headers of the columns that look like prices. They are never read.</summary>
    public IReadOnlyList<string> IgnoredPriceColumns { get; internal set; } = Array.Empty<string>();
}

/// <summary>One imported row.</summary>
/// <param name="Entry">The new user entry (source <c>user-import</c>, not verified).</param>
/// <param name="Confidence">How sure the importer is.</param>
/// <param name="Row">Row number in the source list (the header is row 1).</param>
public sealed record ImportedStock(StockEntry Entry, ImportConfidence Confidence, int Row);

/// <summary>Result of an import.</summary>
public sealed class SuppliesImportResult
{
    internal SuppliesImportResult(List<ImportedStock> imported, List<ValidationIssue> warnings, SuppliesColumnMap map, int rowsRead)
    {
        Imported = imported;
        Warnings = warnings;
        Map = map;
        RowsRead = rowsRead;
    }

    /// <summary>Rows that were turned into entries.</summary>
    public IReadOnlyList<ImportedStock> Imported { get; }
    /// <summary>Everything the importer did not take: unreadable rows, ambiguous units, ignored columns (message keys with arguments).</summary>
    public IReadOnlyList<ValidationIssue> Warnings { get; }
    /// <summary>The columns that were recognised.</summary>
    public SuppliesColumnMap Map { get; }
    /// <summary>Data rows read from the list.</summary>
    public int RowsRead { get; }
    /// <summary>The imported entries.</summary>
    public IReadOnlyList<StockEntry> Entries => Imported.Select(i => i.Entry).ToList();
    /// <summary>True when no row could be imported.</summary>
    public bool IsEmpty => Imported.Count == 0;

    /// <summary>The catalogue file (same schema as the bundled library) holding the imported entries, source <c>user-import</c>.</summary>
    public string ToCatalogJson(string? generatedOn = null)
    {
        var meta = new StockCatalogMetadata(StockLimits.CurrentSchemaVersion,
            generatedOn ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "Created by the Rotula supplies-list import. Part numbers and label sizes were read from a list supplied by the user; every entry is unverified. Prices are never imported.",
            new[] { new StockSourceInfo(SuppliesImporter.SourceId, "Imported by the user from a supplies list") });
        return StockCatalog.ToJson(meta, Entries);
    }
}

/// <summary>
/// Turns a supplies list (CSV or .xlsx) into user catalogue entries. It looks for a part number, a description and a
/// size (<c>4x6in (101.6x152.4mm)</c>, a size column, or width and height columns) by header name, in English and Spanish.
/// It never guesses: a row whose size has no stated unit, whose two size statements disagree, or that has no part number
/// goes to <see cref="SuppliesImportResult.Warnings"/>. Columns that look like prices are not read at all.
/// </summary>
public static class SuppliesImporter
{
    /// <summary>Source id written to every imported entry.</summary>
    public const string SourceId = "user-import";

    /// <summary>Largest list file that is read, in bytes.</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    private const int MaxColumns = 200;
    private const int MaxCellLength = 2000;

    private static readonly string[] PartHeaders = { "partnumber", "partno", "partnum", "pn", "sku", "itemnumber", "itemno", "item", "code", "codigo", "referencia", "ref", "material", "stocknumber", "productcode", "numerodeparte", "articulo", "nodeparte" };
    private static readonly string[] DescriptionHeaders = { "description", "descripcion", "desc", "name", "nombre", "product", "producto", "itemdescription", "detalle", "denominacion" };
    private static readonly string[] SizeHeaders = { "size", "labelsize", "dimensions", "dimension", "tamano", "medida", "medidas", "dimensiones", "formato" };
    private static readonly string[] WidthHeaders = { "width", "ancho", "anchura", "labelwidth" };
    private static readonly string[] HeightHeaders = { "height", "alto", "altura", "largo", "length", "labelheight", "labellength" };
    private static readonly string[] BrandHeaders = { "brand", "marca", "manufacturer", "fabricante", "vendor" };
    private static readonly string[] MediaHeaders = { "media", "mediatype", "technology", "tecnologia", "printtype" };
    private static readonly string[] UnitHeaders = { "unit", "units", "unidad", "unidades", "uom" };
    private static readonly string[] PriceWords = { "price", "pricing", "msrp", "cost", "costo", "precio", "importe", "amount", "total", "currency", "moneda", "discount", "descuento", "tax", "iva", "vat", "tarifa", "valor", "value", "margin", "margen" };

    private static readonly Regex Currency = new(@"^\s*(?:[$€£¥]\s*\d|\d[\d.,]*\s*(?:usd|eur|gbp|mxn|cop|pab|[$€£]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HeaderUnit = new(@"[\(\[]\s*(mm|millimet\w*|in|inch\w*|pulg\w*|cm)\s*[\)\]]|\b(mm|inches|inch|millimeters|millimetres|pulgadas)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Reads <paramref name="path"/> (.csv, .tsv, .txt or .xlsx) and imports it. Never throws.</summary>
    public static SuppliesImportResult ImportFile(string path, SuppliesImportOptions? options = null)
    {
        options ??= new SuppliesImportOptions();
        string name = Path.GetFileName(path ?? string.Empty);
        var warnings = new List<ValidationIssue>();
        try
        {
            var info = new FileInfo(path!);
            if (!info.Exists) return Empty(StockMessageKeys.ImportReadFailed, name);
            if (info.Length > MaxFileBytes) return Empty(StockMessageKeys.ImportFileTooLarge, MaxFileBytes.ToString(CultureInfo.InvariantCulture));
            string ext = info.Extension.ToLowerInvariant();
            MergeTable table;
            if (ext == ".xlsx") table = XlsxReader.ReadFile(path!, new XlsxOptions { SheetName = options.SheetName });
            else if (ext is ".csv" or ".tsv" or ".txt") table = CsvReader.ReadFile(path!);
            else return Empty(StockMessageKeys.ImportUnsupportedFormat, ext);
            options = Clone(options);
            options.SourceName = name;
            return Import(table, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The xlsx reader is a third-party component that can throw many exception types on a damaged workbook.
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportReadFailed, name));
            return new SuppliesImportResult(new List<ImportedStock>(), warnings, new SuppliesColumnMap(), 0);
        }
    }

    /// <summary>Imports an already-read table (first row of data is row 2, the header being row 1).</summary>
    public static SuppliesImportResult Import(MergeTable table, SuppliesImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        options ??= new SuppliesImportOptions();
        var warnings = new List<ValidationIssue>();
        var imported = new List<ImportedStock>();

        if (table.RowCount == 0)
        {
            warnings.Add(StockMessageKeys.Error(StockMessageKeys.ImportNoRows));
            return new SuppliesImportResult(imported, warnings, new SuppliesColumnMap(), 0);
        }
        int rowCount = Math.Min(table.RowCount, Math.Clamp(options.MaxRows, 1, 200000));
        if (rowCount < table.RowCount)
            warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportTooManyRows, rowCount.ToString(CultureInfo.InvariantCulture)));

        var map = DetectColumns(table, rowCount, warnings);
        if (map.PartNumber < 0)
        {
            warnings.Add(StockMessageKeys.Error(StockMessageKeys.ImportNoPartColumn));
            return new SuppliesImportResult(imported, warnings, map, rowCount);
        }
        bool hasDimensionPair = map.Width >= 0 && map.Height >= 0;
        if (map.Size < 0 && !hasDimensionPair && map.Description < 0)
        {
            warnings.Add(StockMessageKeys.Error(StockMessageKeys.ImportNoSizeColumn));
            return new SuppliesImportResult(imported, warnings, map, rowCount);
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < rowCount; r++)
        {
            var cells = table.Rows[r];
            int rowNo = r + 2;
            string row = rowNo.ToString(CultureInfo.InvariantCulture);
            string part = Clean(Cell(cells, map.PartNumber), 80);
            if (part.Length == 0)
            {
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportRowNoPartNumber, row));
                continue;
            }

            var size = ReadSize(cells, map, options, out var sizeProblem, out string problemText);
            if (size is null)
            {
                string key = sizeProblem switch
                {
                    SizeProblem.UnitAmbiguous => StockMessageKeys.ImportRowUnitAmbiguous,
                    SizeProblem.Conflict => StockMessageKeys.ImportRowSizeConflict,
                    SizeProblem.OutOfRange => StockMessageKeys.ImportRowSizeOutOfRange,
                    _ => StockMessageKeys.ImportRowNoSize,
                };
                warnings.Add(sizeProblem == SizeProblem.None
                    ? StockMessageKeys.Warn(key, row, part)
                    : StockMessageKeys.Warn(key, row, part, problemText));
                continue;
            }

            string id = MakeId(part);
            if (!seenIds.Add(id))
            {
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportRowDuplicate, row, part));
                continue;
            }

            string description = Clean(Cell(cells, map.Description), 300);
            string brand = Clean(Cell(cells, map.Brand), 80);
            var media = ReadMedia(Cell(cells, map.Media) + " " + description);
            string confidence = size.Value.Confidence == ImportConfidence.High ? "high" : "medium";
            var entry = new StockEntry
            {
                Id = id,
                Brand = brand.Length > 0 ? brand : (string.IsNullOrWhiteSpace(options.Brand) ? "User" : Clean(options.Brand, 80)),
                PartNumber = part,
                Name = description.Length > 0 ? description : part + " " + SizeLabel(size.Value),
                WidthMm = size.Value.WidthMm,
                HeightMm = size.Value.HeightMm,
                WidthIn = size.Value.WidthIn,
                HeightIn = size.Value.HeightIn,
                Media = media,
                Category = "user",
                Sources = new[] { SourceId },
                Verified = false,
                Verification = "user-import",
                Notes = string.Create(CultureInfo.InvariantCulture, $"Imported from row {rowNo}{(options.SourceName is null ? string.Empty : " of " + Clean(options.SourceName, 100))}; confidence {confidence}."),
                Origin = StockOrigin.User,
            };
            imported.Add(new ImportedStock(entry, size.Value.Confidence, rowNo));
        }
        return new SuppliesImportResult(imported, warnings, map, rowCount);
    }

    // ---- Column detection --------------------------------------------------------------------------

    private static SuppliesColumnMap DetectColumns(MergeTable table, int rowCount, List<ValidationIssue> warnings)
    {
        var map = new SuppliesColumnMap();
        int n = Math.Min(table.Columns.Count, MaxColumns);
        var price = new bool[n];
        var ignored = new List<string>();
        var keys = new string[n];
        for (int c = 0; c < n; c++)
        {
            string header = table.Columns[c];
            // The unit hint "(mm)" must not confuse the header match, so match on the header without brackets.
            keys[c] = StockText.Alnum(Regex.Replace(header, @"[\(\[][^\)\]]*[\)\]]", " "));
            price[c] = IsPriceHeader(keys[c]) || LooksLikePriceColumn(table, c, rowCount);
            if (price[c])
            {
                ignored.Add(header);
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportPriceColumnIgnored, header));
            }
        }
        map.IgnoredPriceColumns = ignored;

        map.PartNumber = Find(keys, price, PartHeaders, exact: true, -1);
        map.Description = Find(keys, price, DescriptionHeaders, exact: true, map.PartNumber);
        map.Size = Find(keys, price, SizeHeaders, exact: true, map.PartNumber);
        map.Width = Find(keys, price, WidthHeaders, exact: true, map.PartNumber);
        map.Height = Find(keys, price, HeightHeaders, exact: true, map.PartNumber);
        map.Brand = Find(keys, price, BrandHeaders, exact: true, map.PartNumber);
        map.Media = Find(keys, price, MediaHeaders, exact: true, map.PartNumber);
        map.Unit = Find(keys, price, UnitHeaders, exact: true, map.PartNumber);

        // Looser second pass: "Part Number (SKU)", "Label size", "Description 1".
        if (map.PartNumber < 0) map.PartNumber = Find(keys, price, PartHeaders, exact: false, -1);
        if (map.Description < 0) map.Description = Find(keys, price, DescriptionHeaders, exact: false, map.PartNumber);
        if (map.Size < 0) map.Size = Find(keys, price, SizeHeaders, exact: false, map.PartNumber);

        // A description column that is really the size column (one list calls it "Description") is fine: the size is read from both.
        if (map.Size >= 0) map.SizeHeaderUnit = UnitOf(table.Columns[map.Size]);
        if (map.Width >= 0 && map.Height >= 0)
        {
            var w = UnitOf(table.Columns[map.Width]);
            var h = UnitOf(table.Columns[map.Height]);
            map.DimensionHeaderUnit = w == h ? w : StockSizeUnit.Unknown;
        }

        // Content fallback: only for the part number and the size, only from columns nobody else claimed. Reported as a guess.
        if (map.PartNumber < 0)
        {
            int c = GuessPartColumn(table, n, rowCount, price, map);
            if (c >= 0)
            {
                map.PartNumber = c;
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportColumnGuessed, "part", table.Columns[c]));
            }
        }
        if (map.Size < 0 && !(map.Width >= 0 && map.Height >= 0))
        {
            int c = GuessSizeColumn(table, n, rowCount, price, map);
            if (c >= 0)
            {
                map.Size = c;
                map.SizeHeaderUnit = UnitOf(table.Columns[c]);
                warnings.Add(StockMessageKeys.Warn(StockMessageKeys.ImportColumnGuessed, "size", table.Columns[c]));
            }
        }
        return map;
    }

    private static bool IsPriceHeader(string key)
    {
        if (key.Length == 0) return false;
        foreach (string w in PriceWords)
            if (key.Contains(w, StringComparison.Ordinal)) return true;
        return StockLimits.IsPriceLikeName(key) || key == "list" || key == "map";
    }

    private static bool LooksLikePriceColumn(MergeTable table, int col, int rowCount)
    {
        int filled = 0, money = 0;
        for (int r = 0; r < Math.Min(rowCount, 200); r++)
        {
            string v = Cell(table.Rows[r], col);
            if (v.Length == 0) continue;
            filled++;
            try { if (Currency.IsMatch(v)) money++; }
            catch (RegexMatchTimeoutException) { }
        }
        return filled >= 3 && money * 10 >= filled * 6;
    }

    private static int Find(string[] keys, bool[] price, string[] names, bool exact, int exclude)
    {
        // Names are tried in priority order so "partnumber" wins over a later "code" column.
        foreach (string name in names)
            for (int c = 0; c < keys.Length; c++)
            {
                if (price[c] || c == exclude || keys[c].Length == 0) continue;
                if (exact ? keys[c] == name : keys[c].Contains(name, StringComparison.Ordinal) && name.Length >= 4)
                    return c;
            }
        return -1;
    }

    private static StockSizeUnit UnitOf(string header)
    {
        try
        {
            var m = HeaderUnit.Match(header);
            if (!m.Success) return StockSizeUnit.Unknown;
            string u = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant();
            return u.StartsWith("mm", StringComparison.Ordinal) || u.StartsWith("millimet", StringComparison.Ordinal) || u == "cm"
                ? StockSizeUnit.Millimeters : StockSizeUnit.Inches;
        }
        catch (RegexMatchTimeoutException) { return StockSizeUnit.Unknown; }
    }

    private static int GuessPartColumn(MergeTable table, int n, int rowCount, bool[] price, SuppliesColumnMap map)
    {
        int sample = Math.Min(rowCount, 200);
        for (int c = 0; c < n; c++)
        {
            if (price[c] || c == map.Description || c == map.Size || c == map.Width || c == map.Height || c == map.Brand) continue;
            int filled = 0, codeLike = 0;
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < sample; r++)
            {
                string v = Cell(table.Rows[r], c).Trim();
                if (v.Length == 0) continue;
                filled++;
                distinct.Add(v);
                if (v.Length is >= 3 and <= 40 && !v.Contains(' ') && v.Any(char.IsDigit)) codeLike++;
            }
            if (filled >= 3 && codeLike * 10 >= filled * 8 && distinct.Count * 10 >= filled * 9) return c;
        }
        return -1;
    }

    private static int GuessSizeColumn(MergeTable table, int n, int rowCount, bool[] price, SuppliesColumnMap map)
    {
        int sample = Math.Min(rowCount, 200);
        for (int c = 0; c < n; c++)
        {
            if (price[c] || c == map.PartNumber) continue;
            int filled = 0, sized = 0;
            for (int r = 0; r < sample; r++)
            {
                string v = Cell(table.Rows[r], c);
                if (v.Length == 0) continue;
                filled++;
                if (StockSizeParser.FindAll(v).Count > 0) sized++;
            }
            if (filled >= 3 && sized * 10 >= filled * 6) return c;
        }
        return -1;
    }

    // ---- Reading a row -----------------------------------------------------------------------------

    private enum SizeProblem { None, UnitAmbiguous, Conflict, OutOfRange }

    private readonly record struct RowSize(double WidthMm, double HeightMm, double WidthIn, double HeightIn, ImportConfidence Confidence);

    private static RowSize? ReadSize(string[] cells, SuppliesColumnMap map, SuppliesImportOptions options, out SizeProblem problem, out string problemText)
    {
        problem = SizeProblem.None;
        problemText = string.Empty;

        // 1. A dedicated size column, 2. width and height columns, 3. the description text. The first source that holds a
        // size decides; later sources are not mixed in, so a stray number in a description cannot override a size column.
        if (map.Size >= 0)
        {
            string text = Clean(Cell(cells, map.Size), 200);
            if (text.Length > 0)
            {
                var found = FromText(text, map.SizeHeaderUnit, options, dedicated: true, out problem);
                if (found is not null) return CheckRange(found.Value, text, out problem, out problemText);
                if (problem != SizeProblem.None) { problemText = text; return null; }
            }
        }
        if (map.Width >= 0 && map.Height >= 0)
        {
            string wt = Clean(Cell(cells, map.Width), 40), ht = Clean(Cell(cells, map.Height), 40);
            if (wt.Length > 0 && ht.Length > 0)
            {
                var unit = map.DimensionHeaderUnit;
                if (unit == StockSizeUnit.Unknown && map.Unit >= 0) unit = ParseUnitCell(Cell(cells, map.Unit));
                bool stated = unit != StockSizeUnit.Unknown;
                if (!stated && options.AssumedUnit is { } assumed) unit = assumed;
                string text = wt + " x " + ht;
                if (unit == StockSizeUnit.Unknown) { problem = SizeProblem.UnitAmbiguous; problemText = text; return null; }
                if (!TryDecimal(wt, out double w) || !TryDecimal(ht, out double h)) { problem = SizeProblem.None; return null; }
                var rs = Build(w, unit, h, unit, stated ? ImportConfidence.High : ImportConfidence.Medium);
                return CheckRange(rs, text, out problem, out problemText);
            }
        }
        if (map.Description >= 0)
        {
            string text = Clean(Cell(cells, map.Description), 300);
            if (text.Length > 0)
            {
                var found = FromText(text, StockSizeUnit.Unknown, options, dedicated: false, out problem);
                if (found is not null) return CheckRange(found.Value, text, out problem, out problemText);
                if (problem != SizeProblem.None) problemText = text;
            }
        }
        return null;
    }

    private static RowSize? FromText(string text, StockSizeUnit headerUnit, SuppliesImportOptions options, bool dedicated, out SizeProblem problem)
    {
        problem = SizeProblem.None;
        var sizes = StockSizeParser.FindAll(text);
        if (sizes.Count == 0) return null;

        var written = sizes.Where(s => s.UnitWritten).ToList();
        if (written.Count > 0)
        {
            // Every size with a written unit must say the same thing (4x6in and 101.6x152.4mm); otherwise the row is contradictory.
            var first = written[0];
            var (fw, fh) = (ParsedSize.ToMm(first.First * first.FirstScale, first.FirstUnit), ParsedSize.ToMm(first.Second * first.SecondScale, first.SecondUnit));
            foreach (var other in written.Skip(1))
            {
                double ow = ParsedSize.ToMm(other.First * other.FirstScale, other.FirstUnit), oh = ParsedSize.ToMm(other.Second * other.SecondScale, other.SecondUnit);
                if (Math.Abs(ow - fw) > StockLimits.UnitToleranceMm || Math.Abs(oh - fh) > StockLimits.UnitToleranceMm) { problem = SizeProblem.Conflict; return null; }
            }
            // Prefer the exact figures: inches from the inch statement, millimetres from the millimetre one.
            var inch = written.FirstOrDefault(s => s.FirstUnit == StockSizeUnit.Inches && s.SecondUnit == StockSizeUnit.Inches);
            var mm = written.FirstOrDefault(s => s.FirstUnit == StockSizeUnit.Millimeters && s.SecondUnit == StockSizeUnit.Millimeters);
            bool twoStatements = written.Count > 1 && inch is not null && mm is not null;
            var conf = dedicated || twoStatements ? ImportConfidence.High : ImportConfidence.Medium;
            if (inch is not null && mm is not null)
                return new RowSize(mm.First * mm.FirstScale, mm.Second * mm.SecondScale, inch.First, inch.Second, conf);
            return Build(first.First * first.FirstScale, first.FirstUnit, first.Second * first.SecondScale, first.SecondUnit, conf);
        }

        // Only unit-less sizes. A header such as "Size (in)" states the unit; otherwise do not guess unless the user said so.
        var bare = sizes[0];
        var unit = headerUnit;
        bool stated = unit != StockSizeUnit.Unknown;
        if (!stated && options.AssumedUnit is { } assumed) unit = assumed;
        if (unit == StockSizeUnit.Unknown) { problem = SizeProblem.UnitAmbiguous; return null; }
        return Build(bare.First, unit, bare.Second, unit, stated && dedicated ? ImportConfidence.High : ImportConfidence.Medium);
    }

    private static RowSize Build(double a, StockSizeUnit ua, double b, StockSizeUnit ub, ImportConfidence conf)
    {
        double wMm = ParsedSize.ToMm(a, ua), hMm = ParsedSize.ToMm(b, ub);
        double wIn = ua == StockSizeUnit.Inches ? a : wMm / 25.4, hIn = ub == StockSizeUnit.Inches ? b : hMm / 25.4;
        return new RowSize(Math.Round(wMm, 2), Math.Round(hMm, 2), Math.Round(wIn, 3), Math.Round(hIn, 3), conf);
    }

    private static RowSize? CheckRange(RowSize s, string text, out SizeProblem problem, out string problemText)
    {
        problem = SizeProblem.None;
        problemText = string.Empty;
        if (s.WidthMm < StockCatalogValidator.MinPlausibleMm || s.WidthMm > StockLimits.MaxWidthMm
            || s.HeightMm < StockCatalogValidator.MinPlausibleMm || s.HeightMm > StockLimits.MaxHeightMm)
        {
            problem = SizeProblem.OutOfRange;
            problemText = text;
            return null;
        }
        return s;
    }

    private static string SizeLabel(RowSize s) =>
        StockEntry.IsSixteenth(s.WidthIn) && StockEntry.IsSixteenth(s.HeightIn)
            ? string.Create(CultureInfo.InvariantCulture, $"{s.WidthIn:0.###} x {s.HeightIn:0.###} in")
            : string.Create(CultureInfo.InvariantCulture, $"{s.WidthMm:0.##} x {s.HeightMm:0.##} mm");

    private static StockSizeUnit ParseUnitCell(string text)
    {
        string t = StockText.Alnum(text);
        if (t is "mm" or "millimeters" or "millimetres" or "milimetros" or "cm") return StockSizeUnit.Millimeters;
        if (t is "in" or "inch" or "inches" or "pulgadas" or "pulgada") return StockSizeUnit.Inches;
        return StockSizeUnit.Unknown;
    }

    private static StockMedia ReadMedia(string text)
    {
        string t = text.ToLowerInvariant();
        bool dt = t.Contains("direct thermal", StringComparison.Ordinal) || t.Contains("termica directa", StringComparison.Ordinal) || t.Contains("térmica directa", StringComparison.Ordinal);
        bool tt = t.Contains("thermal transfer", StringComparison.Ordinal) || t.Contains("transferencia termica", StringComparison.Ordinal) || t.Contains("transferencia térmica", StringComparison.Ordinal);
        return dt && tt ? StockMedia.Unknown : dt ? StockMedia.DirectThermal : tt ? StockMedia.ThermalTransfer : StockMedia.Unknown;
    }

    private static bool TryDecimal(string s, out double v) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v) && v > 0;

    private static string MakeId(string part)
    {
        var sb = new StringBuilder("user-");
        char last = '-';
        foreach (char ch in part)
        {
            char c = char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_' ? ch : '-';
            if (c == '-' && last == '-') continue;
            sb.Append(c);
            last = c;
        }
        string id = sb.ToString().TrimEnd('-');
        return id.Length > StockLimits.MaxIdLength ? id[..StockLimits.MaxIdLength] : id;
    }

    private static string Cell(string[] cells, int col) => col < 0 || col >= cells.Length ? string.Empty : cells[col] is { } s ? (s.Length > MaxCellLength ? s[..MaxCellLength] : s) : string.Empty;

    /// <summary>Trims, removes control characters and caps the length.</summary>
    private static string Clean(string s, int max)
    {
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (char c in s)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    private static SuppliesImportResult Empty(string key, string arg) =>
        new(new List<ImportedStock>(), new List<ValidationIssue> { StockMessageKeys.Error(key, arg) }, new SuppliesColumnMap(), 0);

    private static SuppliesImportOptions Clone(SuppliesImportOptions o) => new()
    {
        Brand = o.Brand,
        AssumedUnit = o.AssumedUnit,
        MaxRows = o.MaxRows,
        SheetName = o.SheetName,
        GeneratedOn = o.GeneratedOn,
        SourceName = o.SourceName,
    };
}
