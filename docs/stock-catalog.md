# Stock catalogue

Rotula ships a library of label stocks: part numbers with their label size, so that picking "4 x 6 in" or typing a
part number sets the label size, media type and tracking for you. This page describes what the catalogue is, its file
format, where the data comes from, how to correct or add entries, how to rebuild it, and what it cannot do yet.

> Brand and product names belong to their owners. Rotula is not affiliated with any of the brands listed. Part numbers
> and label dimensions are product facts that the manufacturers publish; the catalogue stores nothing else about the
> products (no prices, no images, no marketing text, no printer-driver settings).

## What is in it

- 837 entries: 810 Zebra part numbers, 24 generic sizes (4 x 6 in, 100 x 150 mm, GS1 logistics labels and similar) and
  3 Avery sheet labels.
- 809 entries are `verified` (the size was read consistently and confirmed by a second, independent statement), 28 are
  not. Unverified entries are in the library on purpose, so they can be found and corrected, but applying one raises a
  warning. The 12 generic standard sizes (`std-*`) are marked verified although one source states them, because the
  size itself is the definition of the stock.
- The code lives in `src/Rotula.Core/Stocks` (namespace `Rotula.Core.Stocks`, no UI, no dependencies beyond
  `System.Text.Json`). The data is `src/Rotula.Core/Stocks/stocks.json`, embedded in `Rotula.Core.dll`.

## File format (schema version 1)

A catalogue file is a JSON object with a `metadata` block and an `entries` list. The same format is used for the
bundled file and for user files.

```json
{
  "metadata": {
    "schemaVersion": 1,
    "generatedOn": "2026-10-10",
    "sources": [ { "id": "user-import", "name": "Imported by the user from a supplies list" } ],
    "notes": "Free text: provenance, known gaps."
  },
  "entries": [
    {
      "id": "zebra-10028310",
      "brand": "Zebra",
      "partNumber": "10028310",
      "name": "Z-Perform 1000D 2 x 2 in",
      "widthMm": 50.8, "heightMm": 50.8,
      "widthIn": 2.0, "heightIn": 2.0,
      "media": "direct thermal",
      "format": "roll",
      "cornerRadiusMm": 3.18,
      "sourceUrl": "https://example.com/the-public-page",
      "sources": ["zipship-catalogue-pdf", "zebradesigner-stock-export"],
      "verified": true,
      "verification": "2+ sources agree on size"
    }
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `id` | string, required | Unique inside the file, ignoring case. Letters, digits, `.`, `-`, `_`; at most 64 characters. |
| `name` | string, required | Descriptive name. |
| `brand`, `partNumber`, `series`, `stockFamily`, `category` | string | Grouping and search. `category` is for example `wristband`, `shipping`, `avery-sheet`. |
| `widthMm`, `widthIn` | number, required | Label width (across the web). For wristbands this is the short side. |
| `heightMm`, `heightIn` | number | Label height along the feed. Missing only for a continuous roll (`format` `continuous` or `linerless`). |
| `gapMm` | number or string | Gap between labels along the roll. `null` means unknown and is the norm. The word `"mark"` means black-mark sensing. |
| `hGapMm`, `vGapMm` | number | Gap between label columns / rows (multi-up stocks). |
| `columns`, `rows` | integer | Labels across / down. Default 1. |
| `cornerRadiusMm` | number | Die-cut corner radius. |
| `media` | string | `direct thermal`, `thermal transfer` or `either`. |
| `format` | string | `roll`, `continuous`, `linerless`, `fanfold` or `sheet`. |
| `shape` | string | `rectangle` (default) or `round`. |
| `finish`, `coreMm`, `labelsPerRoll`, `rollsPerCarton`, `rollLengthM`, `regions` | | Extra facts when a source states them. |
| `marginsMm` | object | `{left, top, right, bottom}` for sheets. |
| `sourceUrl` | string | A public `https` page that states the entry. |
| `sources` | string list | Ids of the sources that state the entry; each is declared in `metadata.sources`. |
| `verified` | boolean | True when two independent statements agree. |
| `verification` | string | How the verification came about (free text). |
| `notes` | string | Free notes, English. |

Rules that every file must follow (the loader enforces them leniently, the validator strictly):

- Units are millimetres and inches. The two figures of one size must agree within 0.6 mm. Plausible range: 5 to 600 mm
  wide and 5 to 2000 mm high (the loader accepts 3 mm and up).
- Every entry has a non-empty `sources` list or an `https` `sourceUrl`.
- `verified: true` needs the width and height in both units (a continuous roll needs the width and `rollLengthM`).
- There are no price fields, anywhere. Any property whose name contains `price`, `cost`, `msrp`, `currency`,
  `discount`, `retail` or `wholesale` is rejected by the validator and never stored by the loader.
- Unknown properties are ignored by the loader and never stored.

### Null means unknown

A `null` or missing value never means zero. The gap between labels along the roll is `null` for all but one entry:
none of the sources states it (the ZebraDesigner export has 0, which means "the printer detects it"). Rotula therefore
leaves the gap to printer calibration and raises the warning `Stocks.Apply.GapUnknown` when you apply a die-cut stock.

### Units and wristbands

The bundled data keeps the short side as the width. Wristbands that a designer program stores rotated are stored the
other way round here, so `Z-Band Direct 1 x 11 in` is 25.4 mm wide and 279.4 mm high. Applying a wristband sets the
label to that size and, on an empty label, sets the default field rotation to 90 degrees so text runs along the band.

## Where the data comes from

Each entry lists its `sources`; the metadata names them:

- `zipship-catalogue-pdf`: the public Zebra ZipShip catalogue (PDF, effective May 2022), read as text.
- `zebradesigner-stock-export`: a table of stocks exported from the ZebraDesigner label program: part number, family,
  label width and height, labels across, rows, gap between labels across and corner radius. Lengths in that export are
  micrometres whatever display unit the row uses; this was checked on all 848 rows against the size written in each
  stock name. Nothing else from that export (printer settings, offsets, images, author fields) is read.
- `user-import`: entries a user imported from their own supplies list (only in user files, never in the bundled file).
- Generic and sheet sizes cite the public page in their `sourceUrl`.

Where two sources disagree (15 parts), the entry uses the value whose name, micrometre and unit columns all agree, is
marked `verified: false` and says so in `notes`.

The project maintainer determined that part numbers and label dimensions are public product data. Nothing in this
catalogue is a claim of a licence beyond that.

## Fix or add an entry (pull request)

1. Edit `src/Rotula.Core/Stocks/stocks.json`. Keep the field order of neighbouring entries so the diff stays small.
2. Give every new or changed entry a source: a public `https` page in `sourceUrl`, and/or a `sources` list of ids
   declared in `metadata.sources` (add a new source object there if needed). Say in `notes` what the source states.
3. Write the size in both units. Leave the gap `null` unless a source states it. Do not add prices, images or anything
   copied from a product page other than the facts above.
4. Set `verified: true` only when two independent statements of the size agree; otherwise `false` with a note.
5. Run the tests: `dotnet test Rotula.sln -c Release --filter "FullyQualifiedName~Stock"`. The test
   `Bundled_PassesTheValidator` runs `StockCatalogValidator` on the file and lists every problem by message key; CI runs
   the same test on your pull request.
6. In the pull request, link the page you took the figures from.

To check a separate file (for example a contribution you have not merged yet) from code or a test:

```csharp
var report = StockCatalogValidator.ValidateFile("my-stocks.json");
foreach (var error in report.Errors) Console.WriteLine(error);   // message key plus arguments
```

## Rebuild the bundled file

The scripts in `tools/stock-catalog` rebuild `stocks.json` from the two sources above. Only maintainers who hold the
source files need this.

1. Install Python 3.9 or newer and `pip install openpyxl`.
2. Double-click `tools/stock-catalog/Rebuild-StockCatalog.cmd`. The first run creates `extract-settings.json` next to it
   (the repository ignores that file). Open it and write the full paths of the ZebraDesigner export (`designerExportPath`)
   and of the PDF seed (`seedPath`, a JSON array of entries that each cite a public `https` page; entries without one are
   dropped). The scripts read no other input and contain no built-in paths.
3. Run it again. The results go to the `output` folder: `stock-catalog.json`, `stock-catalog-conflicts.csv`,
   `stock-catalog-unparsed.csv`, `stock-catalog-summary.json` and `stock-catalog-checks.txt`.
4. `python qa_check.py` re-reads the export with independent rules and compares every entry (it reads the same settings
   file).
5. Review the two CSV files by hand, copy `stock-catalog.json` over `src/Rotula.Core/Stocks/stocks.json` and run the
   tests.

## Your own stocks

Rotula reads every `*.json` file in `%AppData%\Rotula\stocks` (at most 100 files of 8 MB and 20 000 entries each, in
file-name order). The format is the one above. An entry with a new `id` is added; an entry with the `id` of a bundled
entry replaces it, so a corrected size of your own roll wins over the library. A damaged file or entry is skipped and
reported; it never stops Rotula from starting.

### Import a supplies list

`SuppliesImporter.ImportFile` turns a CSV or `.xlsx` list into a user catalogue file:

- It finds the part number, description and size by column header (English and Spanish) and, when the headers do not
  say, by looking at the content. A column picked that way is reported (`Stocks.Import.ColumnGuessed`).
- Sizes may be written as `4x6in (101.6x152.4mm)`, `4 x 6 in`, `100x150mm`, a size column, or width and height columns
  with the unit in the header (`Width (mm)`) or in a unit column.
- It never guesses. A size without a unit, two sizes in one row that disagree, a missing part number, a duplicate part
  number or an implausible size put the row in the warnings list instead.
- Columns that look like prices (by header or by content) are never read and never stored. The output file has no
  price property.
- Imported entries use the source `user-import`, are `verified: false`, and note the row they came from. Save
  `result.ToCatalogJson()` into the stocks folder.

## Known limits

- The gap between labels along the roll is unknown for every entry except one (black-mark sensing). Calibrate the
  printer on the stock.
- Multi-up (several labels across, or label sheets) is not supported yet. `columns`, `rows` and the gaps are in the
  data and are reported by `StockApplier`, but the label document is laid out as a single label.
- 28 entries are unverified: 15 where two sources disagree on size or series, 11 stated only by the PDF extraction, 2
  where the export lists the part twice with different sizes. They are listed in the rebuild output.
- Roll length for long rolls comes from the stock name; the label height of those continuous rolls is the user's choice.
- Corner radius is information only; the label model has no rounded outline.
- The data reflects the sources at the time of the last rebuild; a part may since have been discontinued.

## Message keys

The library returns message keys with arguments (`ValidationIssue`) and never UI text. All keys are constants in
`StockMessageKeys` and start with `Stocks.` (`Stocks.File.*`, `Stocks.Entry.*`, `Stocks.Validate.*`, `Stocks.Apply.*`,
`Stocks.Import.*`).
