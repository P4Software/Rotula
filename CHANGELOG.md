# Changelog

All notable changes to Rotula are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
