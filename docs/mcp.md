# Rotula MCP server

`Rotula.Mcp.exe` is a small console program that lets AI assistants design and edit Rotula labels. It speaks the
[Model Context Protocol](https://modelcontextprotocol.io/) (MCP) over standard input and output. It is separate from the
Rotula app: it has no window, needs no other program to be running, and makes no network connections of its own.

An assistant application starts the program, asks it which tools it offers, and calls them: create a label, add text and
barcodes, check the result, preview it, save it, generate ZPL. The label designer in the app and the server share the same
engine, so a label saved by one opens in the other.

## Connecting it

Add the program to the MCP server settings of an MCP-capable client. Most clients accept a JSON block like this:

```json
{
  "mcpServers": {
    "rotula": {
      "command": "C:\\Program Files\\Rotula\\Rotula.Mcp.exe"
    }
  }
}
```

Use the real path of `Rotula.Mcp.exe`. There are no command-line switches and no environment variables to set.
Double-clicking the program only prints a short explanation, the path of the settings file and the current safety settings,
and waits for Enter, so the window never closes before it can be read.

The program is one self-contained file for 64-bit Windows; it does not need .NET to be installed.

## The settings file

All configuration lives in `rotula-mcp.settings.json`, **next to `Rotula.Mcp.exe`**. If the file does not exist, a commented
starter is created on first run. Comments (`//`) and trailing commas are allowed.

If the program sits in a folder the current user cannot write to (for example `C:\Program Files`), the starter cannot be
created. The server then runs with the safe defaults and says so in its log; create the file yourself, or keep the program in
a folder you own.

| Setting | Default | Meaning |
|---|---|---|
| `allowedFolders` | `Documents\Rotula Labels` (created on first run) | The only folders the server reads or writes. Local drives only. |
| `allowPrinting` | `false` | Master switch for `print_label`. |
| `allowedPrinters` | `[]` | Exact Windows printer names printing is allowed for. Empty means none. |
| `allowDatabase` | `false` | Master switch for `run_query`, `list_database_connections` and the database source of `merge_preview`. |
| `maxElements` | `200` | Largest number of elements a label may hold (ceiling 2000). |
| `maxFileBytes` | `5242880` | Largest file read or written (ceiling 64 MiB). |
| `maxCopies` | `10` | Most copies per print request (ceiling 1000). |
| `maxDatabaseRows` | `1000` | Most rows a database answer returns (ceiling 10000). |
| `confirmationSeconds` | `120` | How long a print confirmation code stays valid (10 to 600). |
| `logFolder` | `%AppData%\Rotula\mcp` | Where the log files go. |
| `debugLogging` | `false` | Also log tool arguments. This puts label contents in the log; use only to diagnose a problem. |

A damaged settings file never switches anything on: the server falls back to the defaults above, leaves the file untouched
and logs what is wrong. Values outside their range are clamped and mentioned in the log.

## Tools

Positions and sizes are in the label's unit (millimetres unless the label was created in inches or dots), measured from the
top-left corner inside the margins. Every tool that takes a length also accepts `unit` (`mm`, `in` or `dots`). Positions are
stored as whole printer dots, so 10 mm may read back as 10.01 mm. Errors come back as plain sentences that say what is wrong
and what to do.

**Working label.** One label is open at a time. `create_label`, `open_label` and `import_zpl` replace it.

| Tool | What it does |
|---|---|
| `get_server_info` | Shows allowed folders, whether printing and databases are on, and the limits. |
| `list_label_files` | Lists the `.rotula`, `.zpl`, `.png`, `.csv`, `.xlsx` and `.pdf` files in the allowed folders. |
| `create_label` | New empty label: size, unit, DPI (203, 300, 600), name. |
| `open_label` / `save_label` | Read and write `.rotula` files in an allowed folder. Saves are atomic; an existing file is replaced only with `overwrite` or when saving back to the file it was opened from. |
| `get_label` | Summary plus every element with its id. |
| `set_label_settings` | Size, margins, orientation, media type and tracking, default rotation and font, copies (`^PQ`), name. |
| `set_variable` | Declares a `{{variable}}` and its sample value. |
| `undo_last` | Reverts the last change; 20 steps are kept. Covers every tool that changes the label, including create, open, import and convert. |

**Elements.**

| Tool | What it does |
|---|---|
| `add_text` | Text, optionally wrapped in a block (`blockWidth`, `maxLines`, `alignment`). Text is UTF-8 (`^CI28`), so accents and the Spanish inverted marks print correctly. |
| `add_barcode` | Any of the 28 supported symbologies (see `list_symbologies`). Barcode data is checked against each symbology's rules. Options shared by many types (`height`, `moduleWidthDots`, `checkDigit`, `moduleSize`, `columns`, ...) are arguments; options that belong to one type (Codabar start/stop, MSI check digits, GS1 DataBar variant, Aztec error control, MaxiCode mode, ...) go in the `options` object, for example `{"codabarStart":"B"}`. A wrong name or value is refused with the valid ones. |
| `add_qr` | Shortcut for a QR code. |
| `add_box`, `add_line`, `add_circle` | Graphics. |
| `add_image` | A PNG from an allowed folder, converted to black and white (`Threshold`, `FloydSteinberg`, `Ordered`). PNG only; at most 4096 x 4096 pixels. |
| `add_data_field` | A text field bound to a variable, for data merge. |
| `update_element` | Changes properties by name, for example `{"text":"New","fontHeight":8}`. A barcode also accepts `options` (the same object as in `add_barcode`). Unknown names are refused with the list of valid ones; nothing changes if any value is rejected. |
| `move_element`, `delete_element`, `duplicate_element`, `reorder_element` | Position, removal, copy and drawing order. |

**Checking and output.**

| Tool | What it does |
|---|---|
| `list_symbologies` | All 28 barcode types read from the engine's catalogue: the ZPL command, family (linear, 2D, stacked, postal), allowed characters and length, example data that is valid, every option (marked `argument` or `options`) and what the preview cannot show. Types the preview cannot draw (MicroPDF417, Codablock, MaxiCode, TLC39, Code 49, and some Aztec variants) say so in `Preview`; the printer still draws them. `SupportsRotation` is false for MaxiCode. |
| `validate_label` | Runs the engine's checks: barcode data and check digits, ranges, unknown variables, text that cannot be encoded, elements outside the label. |
| `generate_zpl` | ZPL II for the label; optionally writes a `.zpl` file. Nothing is sent to a printer. |
| `import_zpl` | Turns ZPL text or a `.zpl` file into a label. Unsupported commands are kept as raw ZPL and listed as warnings. |
| `convert_dpi` | Rescales the label to another printer resolution, keeping the physical size. |
| `render_preview` | PNG of the label as an image, plus its size. **The preview is approximate**: text is drawn with stand-in fonts, so widths differ from the printer's fonts. Every preview says so. |
| `merge_preview` | Fills the variables from record N (counting from 1) of a `.csv`/`.xlsx` file, or of a database query, and returns the values, unmatched variables, merged ZPL and a preview. |

**Printers and databases.**

| Tool | What it does |
|---|---|
| `list_printers` | Printers installed in Windows. Read-only. |
| `get_printer_info` | Resolution and paper size from the Windows driver. Read-only; no job is created. |
| `print_label` | See "Printing" below. |
| `list_database_connections` | Names of connections saved in the Rotula app (never hosts, users or passwords). Needs `allowDatabase`. |
| `run_query` | One read-only `SELECT` on a saved connection. Needs `allowDatabase`. |

**Resources and prompts.** The open label is available as the resources `rotula://label/zpl` and `rotula://label/json`.
Three prompts are offered: `design_shipping_label`, `convert_old_zpl_label` and `fix_validation_warnings`.

## Printing

Printing is off until the person who installed the server turns it on. Two settings are needed: `allowPrinting: true`, and
the printer's exact Windows name in `allowedPrinters`. Only Windows printer queues are supported; the server never opens a
network connection to a printer.

`print_label` always works in two calls:

1. **First call (dry run).** Nothing is printed. The reply states the printer, number of copies, label size, any resolution
   conversion applied (the label is rescaled when the driver reports a different DPI), the first lines of the ZPL, and a
   one-time confirmation code.
2. **Second call.** Repeating the call with the same printer, the same copies and the code sends exactly one job.

The code is random, valid for `confirmationSeconds`, usable once (a wrong attempt also uses it up) and tied to a fingerprint of
the printer, the copies and the final ZPL, so changing the label, the printer or the copies after the dry run makes it useless.
Only the newest code is valid. Copies are capped by `maxCopies`. Copies are applied to a copy of the label (`^PQ`); the open
label is not changed. A label with validation errors is refused.

## Databases

Off by default (`allowDatabase`). When on:

- only connections saved in the Rotula app can be used; their passwords stay protected by Windows and are never shown;
- a query must pass the same read-only guard the app uses (one `SELECT` or `WITH ... SELECT`; anything that could change
  data, run code, read files or lock rows is refused before a connection is opened), and queries with `@parameters` are not
  accepted;
- answers are capped at `maxDatabaseRows` rows and each cell at 500 characters.

## Safety model

- **Files.** Every path is checked before anything touches the disk. It must be a plain local drive path (or a name inside the
  first allowed folder), resolve inside an allowed folder after normalisation, and end in a whitelisted extension
  (`.rotula`, `.zpl`, `.png`, `.csv`, `.xlsx`, `.pdf`; each tool accepts only the ones it needs). Refused: `..`, network
  (UNC) and device paths (`\\?\`, `\\.\`), alternate data streams, Windows device names (`CON`, `NUL`, `COM1`, ... with or
  without an extension), names ending in a dot or space, paths over 1024 characters, mapped network drives, and any path that
  goes through a symbolic link or junction. A sibling folder that merely starts with the same name is not inside. Reads are
  refused above `maxFileBytes` before the file is opened.
- **Untrusted content.** Tool arguments and the contents of `.rotula`, ZPL, CSV and Excel files are treated as data: the
  server never runs or expands anything in them, and text or barcode data placed in a label (including values merged from a
  data file) is escaped so it cannot add ZPL commands. **One exception to be aware of:** ZPL that `import_zpl` (or a saved
  `.rotula` file made from an import) could not interpret is kept as "raw ZPL" so the label still prints the same, and
  `print_label` sends those blocks to the printer unchanged, including any printer-control commands they contain. The dry run
  lists every raw block (`rawZplBlocks`) so the person confirming can see them; `get_label` shows them as `rawZpl` or
  `recall` elements and `delete_element` removes them. Sizes are limited: elements
  per label, characters per text or barcode, image pixels (decoded size is read from the header first), the output size of
  an image on the label, workbook size after unzipping, and preview size (large labels are scaled down).
- **Saving.** Files are written to a temporary name and renamed, so a failed save does not damage the previous version. An
  existing file is not replaced unless asked.
- **Mistakes.** `undo_last` reverts the last 20 changes. A change that fails midway is rolled back automatically.
- **Network.** The server makes no outbound calls on its own. The only connections it can open are the ones a user-saved
  database connection needs, and only when `allowDatabase` is on. There is no telemetry.
- **Protocol hygiene.** Standard output carries only protocol messages. Diagnostics go to standard error and to a rotating
  log file in `%AppData%\Rotula\mcp` (the newest 7 days are kept). The log holds tool names, outcomes and timings, not label
  contents, file names or queries, unless `debugLogging` is on.

## Limits to know about

- Working state is in memory: one label per server process. Close the client and unsaved work is gone unless it was saved.
- Hard links are not detected; the folder check covers symbolic links and junctions.
- A file can change between the check and the use (a race with another program on the same machine); the guard narrows this
  but a local program with write access to the allowed folder is trusted as much as the user.
- Fonts in previews are stand-ins; only a printed test label proves exact text layout.
- The GS1 DataBar composite (2D) component and Intelligent Mail are not supported; `validate_label` reports the first and the
  ZPL importer keeps such barcodes as raw ZPL.
- The `options` object checks types and plain ranges; the rules that depend on the symbology (an even number of DataBar segments,
  Codablock row limits, MaxiCode message layout) are reported by `validate_label`.
