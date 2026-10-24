# Print station

The print station is a locked, print-only screen for operators: on a packing line, at a goods-in desk or next to a
shared printer. It shows a set of labels as large tiles, asks the label's questions in large fields that take scanner
input, prints, and tells the operator in plain words what the printer did. It has no menus, no design tools and no
file dialogs.

## Starting it

| How | What opens |
|---|---|
| `Rotula.exe --station C:\Labels\Line2` | The labels of that folder, with the `station.json` in it when there is one |
| `Rotula.exe --station C:\Labels\Line2\station.json` | That configuration file (any file name works) |
| `Rotula.exe --station C:\Labels\pallet.rotula` | Only that label, with the `station.json` next to it when there is one |
| `Rotula.exe --station` | `%ProgramData%\Rotula\station.json`, so the station can be set up by policy |
| **File > Open print station...** in the designer | A folder you pick, as with `--station <folder>` |

`/station` and `--station=<path>` work too. A shortcut with `--station` in the Startup folder (or a Windows
Assigned Access / kiosk setup) turns a PC into a dedicated station.

When the configuration has a mistake, Rotula says which setting and line before the station opens; nothing is
guessed. A label that is listed but missing is only left out, with a warning at the top of the station.

## Using it

1. **Pick a label.** Tap its tile, or type part of its name in the search box and press Enter. When a label has a
   `key`, scanning that code in the search box opens it directly. With a single label, the station opens it at once.
2. **Answer the questions.** Every question of the label (its prompted variables) is a large field, with the label's
   own hint and default. A keyboard-wedge scanner types the code and ends it with Enter or Tab: the station checks the
   answer and moves to the next field. Shift+Tab goes back.
3. **Print.** With `printOnLastScan` on (the default), the scan into the last field prints straight away. Otherwise
   the focus moves to the big Print button, and Enter prints. The quantity box accepts 1 up to the label's
   `maxCopies`.
4. **Read the result** in the large status area. The questions then empty for the next item; values the station is
   told to keep stay filled in.

The label's rules for each question (required, minimum and maximum length, pattern) are checked when a scan ends
with Enter or Tab, and again before printing. A refused scan stays in its field with the reason under it, so the operator scans
again instead of printing a wrong label.

### What the status means

| Status | Meaning |
|---|---|
| **Printer ready** | The printer answered and reports no problem. |
| **The printer needs attention** | The printer reports a problem (for example the printhead is open or the paper is out). The reason is shown below. |
| **Printer status unknown** | The printer could not be asked (a Windows printer that does not answer, or the check is off). Printing still works. |
| **Not printed: the printer needs attention** | Rotula asked the printer first and held the job back. Fix what it says and press Print again; nothing was sent. |
| **Printed and verified** | The job was sent and the printer then reported that it finished without a problem. |
| **Sent, but not confirmed** | The job was sent, but the printer could not confirm it. Look at the label before moving on. |
| **Sent, but the printer reports a problem** | The job was sent and the printer reported a problem straight after. Check the label that came out. |
| **Sent to the printer** | The job was sent; the printer's answer was not checked. |
| **Not printed** | Something else stopped the job (for example an unreachable printer). The reason is shown below. |

The status check uses the same print path as the designer's Print dialog (see [printing.md](printing.md)). It is
on for every station printer unless that printer sets `"checkStatus": false`.

### Printing the label's data

When a label has data printing set up (a spreadsheet, CSV or database) and the station allows it
(`allowDataMerge`), the label page also shows the records with a **Print all records** button. Files and queries are
the ones saved with the label; the station never opens a file dialog. A label whose data source cannot be reached
says so and still prints single labels.

### Leaving the station

With an `exitPinHash`, the Exit button (and closing the window) asks for the PIN. Five wrong PINs lock the PIN box
for 30 seconds. Without a PIN, Exit leaves straight away.

In full screen (`fullScreen`, the default) the window covers the task bar and has no title bar. Rotula cannot block
keys that Windows keeps for itself, such as the Windows key or Ctrl+Alt+Del; for a fully locked PC, use Windows
Assigned Access (kiosk mode) with `Rotula.exe --station` as the app.

## station.json

The file is JSON; comments (`//`) and trailing commas are allowed. Every setting is optional. Setting names are
checked exactly: a misspelt name is an error rather than being ignored, so a typo never silently changes what the
station does.

```jsonc
{
  "formatVersion": 1,
  "title": "Packing line 2",
  "labelFolder": "labels",
  "printers": {
    "line": { "host": "192.168.1.50", "dpi": 300 },
    "desk": { "windowsPrinter": "Desk printer", "checkStatus": false }
  },
  "defaultPrinter": "line",
  "defaultCopies": 1,
  "maxCopies": 50,
  "allowDataMerge": false,
  "keepValues": false,
  "exitPinHash": "pbkdf2-sha256$600000$...$...",
  "printOnLastScan": true,
  "scanToSelect": true,
  "fullScreen": true,
  "labels": [
    { "file": "pallet.rotula", "name": "Pallet", "key": "PAL-01", "maxCopies": 5, "keepFields": ["lot"] },
    { "file": "case.rotula", "printer": "desk", "allowDataMerge": true }
  ]
}
```

### Station settings

| Setting | Default | Meaning |
|---|---|---|
| `formatVersion` | `1` | The format of the file. A newer number than this Rotula knows is refused. |
| `title` | "Print station" | Shown at the top of the station. |
| `labelFolder` | the file's folder | Where the labels are, relative to `station.json` or absolute. |
| `labels` | every `.rotula` in `labelFolder` | The labels to offer, in this order (see below). Without it, every label of the folder is offered by file name. |
| `printers` | none | Named printers (see below). |
| `defaultPrinter` | the only printer | The printer of labels that do not name one. With no printers at all, the printer last used in the designer on this PC is used. |
| `defaultCopies` | `1` | The quantity a label opens with. |
| `maxCopies` | `100` | The highest quantity an operator may print at once (1 to 99999999). |
| `allowDataMerge` | `false` | Whether labels may print their saved data (all records). |
| `keepValues` | `false` | Keep every answer after printing instead of emptying the fields. |
| `exitPinHash` | none | The protected code of the exit PIN (see below). Without it, leaving needs no PIN. |
| `printOnLastScan` | `true` | The scan into the last field prints. Off: it moves to the Print button instead. |
| `scanToSelect` | `true` | Scanning a label's `key` in the search box opens that label. |
| `fullScreen` | `true` | Open full screen without a title bar. |
| `$schema` | | Ignored; allowed so an editor can point at a schema. |

### Label settings

| Setting | Meaning |
|---|---|
| `file` | **Required.** The `.rotula` file, relative to `labelFolder` or absolute. |
| `name` | The name on the tile. Default: the file name. |
| `key` | The code that opens the label when scanned. Codes are compared without case and must be unique. |
| `printer` | One of the names under `printers`. |
| `defaultCopies`, `maxCopies`, `allowDataMerge`, `keepValues` | Override the station setting of the same name for this label. |
| `keepFields` | Answers (by variable name) to keep after printing, for example a lot number that stays the same all shift. |

### Printer settings

| Setting | Meaning |
|---|---|
| `windowsPrinter` | The name of a printer installed in Windows (USB, shared or network queue). |
| `host` | A network address; printed to directly over TCP. Give either `windowsPrinter` or `host`, not both. |
| `port` | The TCP port, 1 to 65535. Default `9100`. |
| `dpi` | 203, 300 or 600. Default: the label's own resolution. |
| `darkness` | 0 to 30. Default: the printer's own setting. |
| `speed` | 1 to 14 (inches per second). Default: the printer's own setting. |
| `checkStatus` | Ask the printer before and after each job. Default `true`. |

### Problems Rotula reports

Every problem has a stable message key (`StationConfig.*` in `Rotula.Files.Station.StationKeys`) and is shown in plain
words in the UI language:

| Key | When |
|---|---|
| `NotFound` | The path, the configuration or the `labelFolder` does not exist. |
| `ReadFailed` | The file exists but cannot be read. |
| `InvalidJson` | A JSON mistake, a misspelt setting or a value of the wrong kind; the line and setting are named. |
| `NewerVersion` | `formatVersion` is newer than this Rotula. |
| `NoLabels` | There is no label left to offer. |
| `LabelMissing` | (warning) A listed label file does not exist; it is left out. |
| `LabelNotRotula` | A listed file is not a `.rotula` label. |
| `LabelNoFile` | A label entry has no `file`. |
| `DuplicateKey` | Two labels share a `key`. |
| `UnknownPrinter` | A label or `defaultPrinter` names a printer that is not defined. |
| `PrinterTarget` | A printer has neither or both of `windowsPrinter` and `host`. |
| `PrinterPort`, `PrinterDpi`, `PrinterProfile` | A printer's port, resolution, darkness or speed is out of range. |
| `Copies`, `LabelCopies` | `maxCopies` or `defaultCopies` is out of range, for the station or one label. |
| `PinHash` | `exitPinHash` is not a code made by Rotula. |

## The exit PIN

The PIN is never stored. `exitPinHash` holds a salted PBKDF2-SHA256 code made from it
(`pbkdf2-sha256$<iterations>$<salt>$<hash>`, 600,000 iterations by default), which Rotula checks in constant time.
To make one, use **File > Create print station PIN...** in the designer, or run `Rotula.exe --station-pin` on any PC.
Type the PIN twice (4 to 32 characters), then copy the line it gives into `station.json`.

Keep the folder of `station.json` writable only by the people who set up the station: anyone who can edit the file
can remove the PIN.

## For developers

- `Rotula.Files.Station.StationConfigLoader` reads and checks the configuration; `StationConfig` is the result.
- `StationPin` makes and checks the PIN codes.
- `ScanSequencer` holds the scanner rules (which terminator moves where, when a scan prints, which scan opens a label)
  with no UI, so it is tested on its own.
- `StationViewModel` in the app drives the screen through `IPrintService` and the label's `PromptSet`, so the
  station prints exactly what the Print dialog would.
