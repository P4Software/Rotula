# Changelog

All notable changes to Rotula are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- GitHub Actions workflows: build and test with a Rotula.Core line-coverage gate (80%), a repository hygiene check, and a
  tag workflow that re-runs the checks and keeps an unsigned portable single-file win-x64 zip as a workflow artifact. CI
  does not sign or publish anything: signed releases (installer, portable zip, checksums and winget manifests) are built
  locally with Azure Artifact Signing, as described in docs/releasing.md.
- Project documentation: README (English and Spanish), ZPL support matrix, contributing guide, code of conduct, security
  policy, third-party notices, issue and pull request templates.

## [0.1.0] - 2026-10-10

First public version.

### Added
- **Designer (WPF)**: canvas with rulers, grid, snapping and guides; property panel; layers; align, distribute and z-order;
  multi-selection; resize and rotate that follow what the printer can express; unlimited undo and redo; light theme.
- **Elements**: text and wrapped text blocks, data fields, barcodes, QR codes, boxes, lines, circles and images.
- **Languages**: English and Spanish, following the Windows language with a live switch in the View menu.
- **Files**: versioned `.rotula` JSON files, saved through a temporary file so a failed save keeps the previous version.
- **ZPL generation**: deterministic, pure-ASCII ZPL II output (`^FH` escaping for non-ASCII text) with validation messages
  for labels that cannot be generated correctly.
- **Barcodes**: Code 128, Code 39, Code 93, EAN-13, UPC-A, Interleaved 2 of 5, QR Code, Data Matrix and PDF417, with data
  validation per symbology.
- **ZPL import**: parser for ZPL II with warnings for unsupported or approximated commands, raw passthrough of unknown
  commands, Code 128 bar-pattern recognition for graphics, and placeholders in the preview for stored recalls.
- **Preview and export**: SkiaSharp renderer with bundled stand-in fonts, and PNG and PDF export. The preview is
  approximate and says so.
- **Data merge**: CSV and Excel (`.xlsx`) sources, `{{name}}` variables, serial-number counters and batch ZPL output.
- **Printing**: raw Windows spooler printing (USB, shared and network queues), direct TCP port 9100 printing and printer
  profiles; failures are reported as plain-language results, never exceptions.
- Offline by design: no network calls and no telemetry.

### Known limitations
- Preview text uses a stand-in typeface; bitmap fonts and `FontWidth` are approximated.
- `^PO` is written to the ZPL but not previewed.
- Variables do not survive a ZPL round trip.
- Releases are signed with Azure Artifact Signing, but Windows SmartScreen can still show a reputation warning for a new,
  little-downloaded file until downloads build trust (More info, then Run anyway).

[Unreleased]: https://github.com/P4Software/Rotula/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/P4Software/Rotula/releases/tag/v0.1.0
