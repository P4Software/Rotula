# ZPL support

Rotula writes and reads ZPL II. This page lists exactly which commands are understood. "Generate" means the command is
emitted when a label is converted to ZPL; "Import" means it is interpreted when ZPL is opened in Rotula. Output is
deterministic (the same label and data always give byte-identical ZPL) and pure ASCII: non-ASCII text is hex-escaped with
`^FH`, so the transport encoding cannot corrupt it.

## Commands

| Command | Purpose | Generate | Import | Notes |
|---|---|---|---|---|
| `^XA` / `^XZ` | Start / end of label | Yes | Yes | Several labels in one file become several documents |
| `^MN` | Media tracking | Yes | Yes | Continuous, web, mark |
| `^MT` | Media type | Yes | Yes | Thermal transfer / direct thermal |
| `^MU` | Units | No | Yes | Converted to dots on import (warning shown) |
| `^CI` | Character set | Yes | Yes | UTF-8 (28) is the default; national sets and remaps give a warning |
| `^PW` | Print width | Yes | Yes | |
| `^LL` | Label length | Yes | Yes | |
| `^PO` | Print orientation | Yes | Yes | **Not previewed**: the on-screen preview always shows the normal orientation |
| `^PM` | Print mirror | Kept | Yes | Mirroring is not modelled: `^PMY` is kept verbatim as raw ZPL, with a warning, and not previewed |
| `^LH` | Label home | Yes | Yes | A `^LH` after the first field is warned about |
| `^FW` | Default field rotation | Yes | Yes | |
| `^CF` | Default font | Yes | Yes | |
| `^PQ` | Print quantity | Yes | Yes | Quantity, pause, replicates, override |
| `^FO` / `^FT` | Field origin / typeset | Yes | Yes | |
| `^FB` | Field block (wrapped text) | Yes | Yes | Width, max lines, line spacing, justification, hanging indent |
| `^FD` / `^FV` | Field data / variable | Yes | Yes | `^FV` is read as `^FD` |
| `^FS` | Field separator | Yes | Yes | A missing `^FS` is warned about |
| `^FH` | Hex escape indicator | Yes | Yes | |
| `^FR` | Reverse print | Yes | Yes | |
| `^FX` | Comment | Kept | Yes | Kept as a raw element so notes survive a round trip |
| `^A` | Scalable / bitmap font | Yes | Yes | See "Fonts" below |
| `^GB` | Box and lines | Yes | Yes | Lines are boxes with a thin side |
| `^GC` | Circle | Yes | Yes | |
| `^GF` | Graphic field | Yes | Yes | Monochrome images (output is ASCII hex; input also accepts ZPL run-length compression and Z64/B64 data) |
| `^BY` | Barcode defaults | Yes | Yes | Module width, wide-bar ratio |
| `^BC` | Code 128 | Yes | Yes | Subset modes N / U / A / D |
| `^B3` | Code 39 | Yes | Yes | |
| `^BA` | Code 93 | Yes | Yes | |
| `^BE` | EAN-13 | Yes | Yes | |
| `^BU` | UPC-A | Yes | Yes | |
| `^B2` | Interleaved 2 of 5 | Yes | Yes | |
| `^BQ` | QR Code | Yes | Yes | Model and magnification; manual mode gives a warning |
| `^BX` | Data Matrix | Yes | Yes | Quality below 200 gives a warning |
| `^B7` | PDF417 | Yes | Yes | |
| `^XG` / `^IL` / `^XF` | Recall stored graphic / image / format | Kept | Yes | Kept verbatim and drawn as a placeholder in the preview |
| `^IS` / `^DF` | Save image / download format | Kept | Warned | Kept verbatim and not interpreted |
| anything else | | Kept | Warned | Unknown commands are kept verbatim as raw ZPL so nothing is lost silently |

## Fonts

ZPL's built-in bitmap fonts (`A`-`H`, `0`) live in the printer, not in Rotula. The preview draws text with a bundled
stand-in typeface (Liberation Sans / Mono), so letter shapes and line breaks can differ a little from the printed label.
`FontWidth` is honoured as a stretch factor in the preview but the real glyph widths of the printer's bitmap fonts are
not reproduced. Always print a test label before a production run.

## Round trip

Importing ZPL and generating it again keeps the geometry, text, barcodes and graphics. It does **not** keep Rotula-only
information: variables such as `{{name}}` are a Rotula feature and a ZPL file has no way to express them, so a label
that was imported is plain text until you edit it.

## Not supported

Anything not listed above is not interpreted. See the roadmap in the README for planned and out-of-scope items.
