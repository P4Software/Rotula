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
on for every station printer unless that printer sets `"checkStatus": false`. A printer that prints through its
Windows driver (below) is never asked for its status: the question is a ZPL command it would print as text.

### Picture mode (any Windows printer)

A station printer can be an ordinary office or label printer that does not understand ZPL. The station then draws
each label as a picture and prints it through the printer's Windows driver, exactly as the designer's Print dialog
does. Which way a printer is used is its **print mode**, saved per Windows printer on this PC (`print.printModes`
in the designer's settings.json). The station reads the modes of the Windows account it runs under: a printer saved
as "Windows driver (picture)" prints pictures at the station, every other printer prints ZPL as before. An operator
is never asked about the mode; a printer with no saved mode prints ZPL. The mode is chosen in the designer's Print
dialog.

Some labels have parts a Windows driver cannot print the same way (raw ZPL kept from an imported file, a barcode
whose bars come out narrower than its safe size, a label size the driver does not accept, ...). In the Print dialog
the person ticks one box to accept them. At the station they are accepted once, by whoever sets the station up:

1. In the designer, on the PC where the station runs and signed in as the account it runs under, choose
   **File > Set up print station...** and pick the station folder. The check uses that PC's printer drivers, so
   it must run there.
2. Rotula checks every label with the printer it uses and shows its print mode. For a label printed through a
   Windows driver it lists what the driver cannot print the same way, in the same words as the Print dialog.
3. Read the points. When the label is fine as it is, tick **I checked these points. Let the station print this
   label anyway.** and press **Save**. A label with a point that cannot be printed at all has no box: change the
   label, or set the printer to printer language (ZPL) if it understands ZPL, then press **Check again**.

**Check again** reads station.json, every label file and the saved acceptances again, so it checks what is on
disk now: a label you just changed in the designer, a label added to station.json, a print mode chosen in the
Print dialog. A tick you have not saved yet stays when the label's points are still the same. If station.json no
longer opens, the tool says why, shows no results and saves nothing until it is corrected. A label file that no
longer opens, or a printer that does not answer (switched off, disconnected), is shown as a problem on its own
row; whatever was accepted for that label before is kept.

Saving writes **`station.acknowledged.json`** next to station.json. station.json itself is never changed, so its
comments stay as written and an older Rotula still opens it. The file is small and written by the tool, not by hand:

```json
{
  "version": 1,
  "labels": {
    "pallet.rotula": "3f2a9c..."
  }
}
```

Each key is a label file, relative to `labelFolder` (a label outside that folder keeps its full path), and each
value is the fingerprint of the points that were accepted for it. The file is written whole through a temporary
file, so a station that starts at that moment reads either the old or the new version. Nothing is written when
nothing changed. A label taken out of station.json loses its entry at the next save, and the file is deleted when
no label is accepted any more, so a station that prints ZPL never gets one. A missing, unreadable or damaged file
counts as "nothing accepted". The tool needs the station's station.json: for a folder without one it still checks
the labels, but saves nothing and says to create station.json first.

One label file offered on two picture printers that find different points can be accepted for one of them only
(the file has one entry per label file). The tool says so when you save; give each printer its own copy of the
label file to print it on both.

When an operator prints a label whose points were never accepted, or a label that was edited since the set-up (its
points are no longer the ones accepted), nothing is sent and the status says, in plain words, to ask the person who
set up the station to check the label and set the station up again. A label with a point that cannot be printed
at all is never printed. The station reads both files when it starts, so restart it after saving.

The note under the preview says that the preview shows the printed picture (instead of "approximate") when the
label's printer prints through its Windows driver.

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
- `StationAcknowledgements` reads and writes `station.acknowledged.json` (never throws on read; writes through a
  temporary file; deletes the file when nothing is accepted). The loader fills `StationConfig.AcknowledgedFindings`
  from it.
- `StationPrinting` (app) builds a label's printer and the document it prints, shared by the station and
  `StationSetupViewModel`, so the set-up checks exactly the target and document the station prints (the fingerprint
  depends on both, and on the driver's media).
- `StationPin` makes and checks the PIN codes.
- `ScanSequencer` holds the scanner rules (which terminator moves where, when a scan prints, which scan opens a label)
  with no UI, so it is tested on its own.
- `StationViewModel` in the app drives the screen through `IPrintService` and the label's `PromptSet`, so the
  station prints exactly what the Print dialog would.
