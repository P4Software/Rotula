# Changelog

All notable changes to Rotula are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Printer status before and after printing** (ZPL `~HS`). Before each job Rotula asks the printer for its status and does not send while it reports it is out of labels, its printhead is open, the ribbon is out, it is paused, too hot, in diagnostics mode or its buffer is full; the Print dialog says which and what to do. After the job it waits (up to 30 seconds) until the printer reports it finished, then says "printed and verified", names a fault that appeared after the job (the labels still count as possibly printed, so no serial number is reused), or says it could not confirm. A printer that cannot answer never blocks printing. On by default for network printers and off for Windows printers, switchable in the Print dialog; "Check printer status" shows the decoded answer at any time. The MCP server has a matching `get_printer_status` tool.
- **A gallery of eight showcase labels** in the README (GS1-128 pallet, parcel, food, wine, lab specimen, asset tag, shelf tags with data merge, print conditions). Their `.rotula` files and the CSV data are in `docs/showcase`, and the pictures are rendered with Rotula's own PNG export by a test that only runs when `ROTULA_RENDER_SHOWCASE=1` is set.
- **Text fit for wrapped text** (Properties panel, Text fit): choose what a text block does when its text is longer than the box. "Shrink to fit" uses the largest font size, between a smallest and a largest size you set, at which all the text fits; "Grow height" adds lines, up to a limit you set, so all the text shows. With data merge every record gets the size or the lines its own text needs, the canvas shows the same size that prints, and "Do not warn" hides the does-not-fit warning when cutting the text off is what you want. Labels that do not use it are saved exactly as before; a label that does cannot be opened by older versions of Rotula.
- **Print an object only when you want it**: in the new Printing section of the properties, mark an object "Do not print this object" (a note or guide that never reaches the printer or an export), set "Print only when" a field equals, does not equal, contains, is empty or is not empty a value (so a warning can appear on some labels only), and "Lock position" so nothing moves it by accident. The Objects list and the canvas show small No print, Hidden and Locked badges.

### Fixed
- **Windows fonts that are named after a style now draw in that style.** "Arial Black", "Segoe UI Semibold", "Segoe UI Black", "Bahnschrift Condensed" and similar names used to be replaced by the default font in the preview, PNG/PDF export and print picture; they now resolve to their parent family at the weight and width the name says.
- **Arial Narrow, Franklin Gothic Medium and other families Windows lists only under their own name now draw in that font** instead of the default one: when the font manager does not know the family, Rotula loads the installed font file.

## [0.3.5] - 2026-10-12

### Fixed
- **Every label now brings its data back when you open it**, not only labels saved on this PC: a data file (CSV or Excel) or a saved database connection is read again by itself, so the fields and barcodes show real values instead of a red crossed box, and reading the data does not mark the label as changed. If the file or connection is not available, the Data window says why and the Reload / Connect and reload data button is still there.

## [0.3.4] - 2026-10-12

### Fixed
- **A label you saved here now brings its data back when you open it**: a data file (CSV or Excel) or a saved database connection is read again by itself, so the fields and barcodes show real values instead of a red crossed box. A label from somewhere else shows its saved query and waits for one click on Reload / Connect and reload data; that click also makes this PC trust that file from then on. If the file or connection is not available, the Data window says why.

## [0.3.3] - 2026-10-11

### Fixed
- **PNG, JPEG and PDF export now show the record picked in the Data window**, as the canvas does. Before, a label with fields exported with the fields empty and a field barcode was drawn as a crossed red box.

## [0.3.2] - 2026-10-11

### Added
- **JPEG export** (File > Export > JPEG image): the label as a JPEG at printer resolution on a white background. Use PNG when the picture will be scanned or printed, since JPEG softens sharp edges a little.

### Fixed
- A size such as "4in x 6in" typed or pasted right after the program started could be read as "no size" on a slow computer, because the first match was cut off after 250 ms; the limit is now 2 seconds.
- Closed windows and finished screens no longer stay in memory listening for language changes.

## [0.3.1] - 2026-10-11

### Changed
- **The Print dialog is where you choose what to print**: when data is loaded, the Print dialog lists the records with their tick boxes (the same ticks as in the Data window), says how many are selected, and prints exactly the ticked records, or saves them as a ZPL file. If no record is ticked, Print stays off and explains why. Without data the dialog works as before.

## [0.3.0] - 2026-10-11

### Added
- **Variables are used when printing**: the Print dialog and the data merge window now work out dates, computed values and serial numbers for every
  label, ask the label's print-time questions in a dialog, and confirm remembered serial numbers only for labels that really reached the printer
  (a failed send that moved no byte gives its numbers back; one that moved bytes keeps them, so no number is printed twice). Checking a batch uses
  no numbers. Copies of a label with a counter each get their own number.
- **Text formatting core** (see `docs/text-formatting.md`): a text can use a Windows font, which is drawn to a picture at the printer's resolution
  when the label is printed (data values are in the text before it is drawn); printer fonts keep real text, `^A@` stored fonts are supported, and a
  text that does not fit its box raises a warning. `.rotula` files that use a Windows font or a stored font are saved as format version 8.
- **Record picker in Data merge**: a list of the loaded records (file or database) with a tick box on each, a filter, column sorting, and Select all / Select none / Invert. Only ticked records are previewed, printed, checked or exported, and serial numbers are reserved only for them.
- **Text formatting controls**: font choice (printer fonts and Windows fonts), size in points, bold, italic, underline, alignment, a formatting bar above the selected text, Ctrl+B/I/U, an overflow warning with one-click fixes, and drag handles that change the wrap width.
- The database button and Ctrl+M stay inactive until a label exists (new, opened or imported), so the Data window can no longer be opened with nothing to merge into.
- **Word wrap** now sizes its block from the label (width to the right edge, as many lines as fit below) instead of a fixed 400 x 1 block.
- Resizing an element no longer builds a very large scratch bitmap on every mouse move, and a failed preview shows "preview unavailable" instead of an error dialog.
- The **Objects** list can be collapsed (closed by default; the choice is remembered) so the Properties panel gets its height.

## [0.2.0] - 2026-10-11

### Added
- **Variables that work out their own value** (engine, file format and MCP only for now; printing and the editor come next; see `docs/variables.md`): date and time variables with offsets
  (including business days), rounding, formats such as GS1 `YYMMDD` or the day of the year, and a choice of the computer clock or the printer's own
  clock; serial-number counters that can remember their last value between sessions (reserve, then confirm, so a crash or a second window never
  repeats a number); print-time prompts with validation; and computed variables from a small, safe formula language. (Batch merge in the Rotula engine resolves all of
  them per record; the print screens do not use them yet.) `.rotula` files that use them are saved as format version 7; other files are unchanged.
- **Database add-ons (provider packs) for Oracle, Firebird, DuckDB and ClickHouse**, built as separate folders by `scripts\build-packs.cmd` (see
  `docs/data-providers.md`). Each opens its data read-only (Oracle `SET TRANSACTION READ ONLY`, Firebird READ ONLY transaction, DuckDB read-only
  file with external access off and locked settings, ClickHouse `readonly=1` on every request) behind the query check, and carries its own licence
  notices. The query check now also refuses Oracle quoted database links (`table@"link"`), treats DuckDB block comments as nesting, and reads
  ClickHouse quoted names with backslash escapes like the vendor documents. Provider API 1.1 (`FormatCell`, `IdentifierBackslashEscapes`).
- **QR content builder and GS1 builder**: pick what the code holds (web address, text, Wi-Fi, contact card, calendar event, email, phone,
  SMS, location, GS1 Digital Link), fill in the fields (each can be bound to a data field), and see the code, its printed size and how
  full it is. QR options show the recovery percentage per error-correction level. GS1-128 and GS1 Data Matrix get an identifier picker
  with plain-language checks.
- **Database name drop-down**: after you enter the server and login, the database list is loaded from the server.
- **App icon and logo** on the program, the installer, the uninstaller entry and `.rotula` files.

## [0.1.2] - 2026-10-11

### Fixed
- The app no longer crashes when you open the Data merge window a second time, or open Print and then Data merge. Closed windows were
  still sharing the printer-type and counter option buttons with the next window, which made them switch each other back and forth
  until the app ran out of memory.

## [0.1.1] - 2026-10-10

### Fixed
- The app no longer crashes (stack overflow) when switching between Windows printer and Network printer in the print dialog and the data merge window.
- The database table list no longer freezes or crashes the app when the server has thousands of tables; the list now scrolls inside its own area.

## [0.1.0] - 2026-10-10

First public version.

### Added
- **Designer (WPF)**: canvas with rulers, grid, snapping and guides; property panel; layers; align, distribute and z-order;
  multi-selection; resize and rotate that follow what the printer can express; unlimited undo and redo; light theme.
- **Elements**: text and wrapped text blocks, data fields, barcodes, QR codes, boxes, lines, circles and images.
- **Languages**: English and Spanish, following the Windows language with a live switch in the View menu; the installer is
  available in both.
- **Files**: versioned `.rotula` JSON files, saved through a temporary file so a failed save keeps the previous version.
- **Barcodes**: 28 barcode types (linear, 2D, stacked and postal families) with data validation per symbology.
- **ZPL generation and import**: deterministic, pure-ASCII ZPL II output with validation messages; a ZPL importer that warns
  about unsupported or approximated commands and keeps unknown ones as raw ZPL. A catalogue of ZPL II commands drives a
  generated coverage page (`docs/zpl-coverage.md`) that says what Rotula does with each command.
- **Stock library**: a searchable library of label stocks (part number, size, brand, type), with favourites, recent stocks,
  your own stocks and import of a supplies list from CSV or Excel. It is part of the New label dialog.
- **DPI conversion**: rescale a label to another printer resolution (203, 300 or 600 dpi) keeping its physical size.
- **Preview and export**: SkiaSharp renderer with bundled stand-in fonts, and PNG and PDF export. The preview is
  approximate and says so.
- **Data merge**: CSV, Excel (`.xlsx`) and database sources, `{{name}}` variables, serial-number counters and batch ZPL output.
- **Databases**: read-only sources for SQL Server, PostgreSQL, MySQL, SQLite and ODBC, with a connection manager with presets
  for common hosted services, passwords protected by Windows, and a guard that allows a single `SELECT`. Further engines can
  be added as add-ons; an add-on in your own folder runs only after you approve it.
- **MCP server**: `Rotula.Mcp.exe`, a separate program that lets MCP-capable AI clients create and edit labels, validate them,
  preview them and generate ZPL. Printing and database access are off until switched on in its settings file, and printing
  needs a two-step confirmation (see `docs/mcp.md`).
- **Printing**: raw Windows spooler printing (USB, shared and network queues), direct TCP port 9100 printing and printer
  profiles; failures are reported as plain-language results, never exceptions.
- **Releases**: signed per-user installer (with a `.rotula` file association option), portable zip with the same files,
  checksums and winget manifests, built as described in `docs/releasing.md`. GitHub Actions only builds, tests and checks.
- Offline by design: no telemetry and no network calls other than the printers and databases you set up.

### Known limitations
- Preview text uses a stand-in typeface; bitmap fonts and `FontWidth` are approximated.
- `^PO` is written to the ZPL but not previewed.
- Variables do not survive a ZPL round trip.
- Releases are signed with Azure Artifact Signing, but Windows SmartScreen can still show a reputation warning for a new,
  little-downloaded file until downloads build trust (More info, then Run anyway).

[Unreleased]: https://github.com/P4Software/Rotula/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/P4Software/Rotula/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/P4Software/Rotula/releases/tag/v0.1.0
