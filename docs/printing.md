# Printing

`Rotula.Printing` sends ZPL to a printer without blocking the UI. Everything goes through `IPrintService`
(`new PrintService()` wires the real transports). Calls are async, take a `CancellationToken` and return a
`PrintResult`; failures are values with a stable key (`Print.Error.<Kind>`) plus arguments, never exceptions.
The library holds no user-facing text; the UI localises the keys.

## Choosing a target

| Target | Use it for |
|---|---|
| `PrinterTarget.Spooler("Zebra ZD420")` | Any printer installed in Windows: **USB**, shared or network queues |
| `PrinterTarget.TcpIp("192.168.1.50")` | Network printers listening on port 9100 (change the port if needed) |

`IPrintService.GetPrintersAsync()` lists the installed queue names, which marks the default printer and
printers Windows reports as offline.

## USB printers

Rotula bundles no drivers. To print over USB:

1. Plug in the printer and install its Windows driver (for Zebra: the ZDesigner driver, or the
   "Generic / Text Only" driver, both work because Rotula sends RAW data).
2. The printer appears in `GetPrintersAsync()` with a port such as `USB001`.
3. Print with `PrinterTarget.Spooler(name)`.

The job is sent with the spooler datatype `RAW`, so the driver passes the ZPL through untouched. An
unplugged or switched-off printer is reported as `Print.Error.Offline` instead of queuing silently.

## Network printers

`TcpIp` connects, sends the bytes and closes. `NetworkPrinterOptions` sets the connect timeout (default 5 s)
and send timeout (default 15 s). Error keys: `ConnectionRefused` (nothing on that port), `HostUnreachable`
(name does not resolve or no route), `Timeout`, `Cancelled`.

## Encoding

Bytes follow the label's `ZplEncoding`: UTF-8 for `^CI28` (default), the matching legacy code page otherwise.
A character that the code page cannot represent returns `Print.Error.EncodingFailed` rather than a silent `?`.

## Profiles and the test label

`PrinterProfile` holds DPI, width/height, media tracking, darkness (`~SD`, 0-30) and speed (`^PR`, 1-14).
`PrinterSetup.BuildSetupZpl` produces the commands Core's generator does not emit. Pass a profile to
`PrintAsync` to prepend them in the same job, send them alone with `ApplyProfileAsync`, or print a small
sample label with `PrintTestLabelAsync`.

## Printer status (`~HS`)

Rotula can ask a ZPL compatible printer for its host status with the `~HS` command and read the three-string reply.
`HostStatusParser.Parse` (UI-free, in `PrinterStatus.cs`) decodes it into a `PrinterStatus`:

| String | Fields Rotula uses |
|---|---|
| 1 `aaa,b,c,dddd,eee,f,g,h,iii,j,k,l` | paper out (b), pause (c), label length in dots (dddd), formats in the receive buffer (eee), buffer full (f), communications diagnostics mode (g), partial format (h), corrupt RAM (j), under temperature (k), over temperature (l) |
| 2 `mmm,n,o,p,q,r,s,t,uuuuuuuu,v,www` | head up (o), ribbon out (p), thermal transfer (q), print mode (r), label waiting (t), labels remaining in the batch (uuuuuuuu), graphics stored (www) |
| 3 `xxxx,y` | not used |

Each string is framed by STX (0x02) and ETX (0x03). Frames are recognised by their number of fields, so junk between frames
or strings in another order do not matter; a frame cut off before its ETX, or with a value that is not a number, is ignored
rather than guessed; a reply without any STX is read line by line. Only reported flags become `PrinterCondition`s, so a
partial reply never invents a fault, and `IsIdle` is only true when both strings were read. "Ribbon out" only counts in
thermal transfer mode.

| Condition | Effect |
|---|---|
| `HeadOpen`, `PaperOut`, `RibbonOut`, `HeadTooHot`, `DiagnosticMode`, `BufferFull`, `Paused` | **Blocking**: the job is not sent (`Print.Error.<Condition>`, args: printer, then every reported condition) |
| `HeadTooCold`, `CorruptRam`, `PartialFormat`, `LabelWaiting` | Warning: sent, with a `PrinterStatus.Warning` notice |

**Reading the reply** (`IPrinterStatusReader`, implemented by `PrinterStatusReader`; fake it in tests):

- **TCP**: one connection to host:port, write `~HS`, read until three frames arrived, the printer closes, or the timeout
  (default 3 s for connect and reply together). A reply that already holds strings 1 and 2 is decoded even on a timeout.
- **Windows queue (best effort)**: a tiny RAW job `~HS`, then `ReadPrinter` on the same handle. Many USB and standard TCP
  port monitors cannot read back; that is "status unavailable" (`NotSupported`), never an exception. The read runs on its
  own thread and the caller stops waiting at the timeout, so a driver that blocks cannot hang printing. A queue Windows
  reports offline is not queried (the query would wait in the queue). Sending `~HS` to a printer that is not ZPL compatible
  would print it as text, which is why the check is off by default for Windows printers.

**Around a job.** The check is opt-in per target: `PrinterTarget.WithStatus(PrinterStatusOptions)` (default `Off`, so library
callers send exactly what they always sent). The app sets it from the Print dialog switch "Check the printer before and
after printing", stored per transport in `settings.json` (`print.statusCheckNetwork`, default true;
`print.statusCheckWindows`, default false).

- **Before**: `PrintAsync` asks before every block it sends (so paper running out halfway stops the run before the next
  block), `PrintZplAsync` before its job. A blocking condition returns a failure with 0 bytes sent, so reserved serial
  numbers go back. An unknown status never blocks: the job is sent with one `PrinterStatus.NotChecked` notice.
  `ApplyProfileAsync` never asks (set-up commands print nothing).
- **After**: when every block was sent, the service waits `PollInterval` (1 s) and asks again until the printer reports
  no format in the buffer, no label left and no partial format (`PrinterStatus.Verified`), a blocking fault
  (`PrinterStatus.FaultAfterJob`), or `VerifyTimeout` (30 s) passes (`PrinterStatus.NotVerified`, reason `StillPrinting` or
  the unavailable reason). It runs after the serial numbers were confirmed: a fault after the job keeps the labels counted as
  possibly printed. Cancelling the wait gives `NotVerified` / `Stopped`; the result stays a success because the labels were
  sent. "Verified" means the printer says it finished without a fault; it cannot see a label that printed badly.
- **Batches** (Data merge): every job is checked before it goes out; `IPrintService.VerifyPrintedAsync` runs once after the
  last job.

`IPrintService.GetStatusAsync(target)` backs the "Check printer status" button of the printer settings and the MCP tool
`get_printer_status` (which needs the printing permission because it sends a command to the printer).

## Printing through the Windows driver (any printer)

A printer that does not understand ZPL (DYMO, Brother, TSC, SATO, Epson, an office laser printer) can still print Rotula
labels. Rotula draws the label as a picture and hands it to the printer's installed Windows driver. Only a Windows queue can
be printed this way; a network printer on TCP port 9100 always gets ZPL.

### How the mode is chosen and stored

- There are two print modes: *Printer language (ZPL)* and *Windows driver (picture)* (`PrinterOutputMode.PrinterLanguage` and
  `PrinterOutputMode.WindowsDriver`).
- The mode belongs to the **printer**, not to the label. It is saved per Windows queue name in `settings.json`, in the
  `printModes` map under `print`, for example `"printModes": { "ZDesigner ZD420": "PrinterLanguage", "DYMO LabelWriter 450": "WindowsDriver" }`.
  The names are matched without regard to case, and choosing a mode for one printer never changes another.
- The first time you print to a printer that has no saved mode, the Print dialog asks once, "How does this printer understand
  labels?", with the two answers. Nothing is sent until you answer. Rotula pre-selects a suggestion: ZPL when the queue or driver
  name contains ZDesigner, Zebra or ZPL, or the driver is "Generic / Text Only"; Windows driver for everything else. The
  suggestion is only that. Detection never changes a saved mode.
- A `settings.json` written by an older Rotula has no `printModes` map. The first time it is read, the printer that was
  already configured is recorded as ZPL, because it has always printed ZPL, so its user is not asked. This happens once: a map
  that exists, even an empty one, is never filled in again. Other printers are asked about when first used.
- The Print dialog shows the mode of the chosen printer and a button to change it. To change a mode by hand, close Rotula and
  edit the entry (`"PrinterLanguage"` or `"WindowsDriver"`); a printer that is not listed is asked about again.

### What the check before printing finds

In picture mode Rotula asks the driver for its resolution and printable area and checks the whole job **once**, before
anything is sent. A batch of 500 records is checked once for the design. The Print dialog lists what it found, in plain words.
Findings that make the label wrong are **errors** and stop the print. The others are **warnings** and can be accepted.

| Finding | Kind | In plain words |
|---|---|---|
| Driver not reachable | Error | Rotula could not read the page size and resolution from the driver (printer off, disconnected or not installed) |
| Label bigger than the paper | Error | The label is more than 1 mm wider or taller than the paper the driver will use, so part would be cut off. When the driver accepted the label size, that is the page itself; when it did not, it is the printable area of the driver's own paper |
| Picture too large | Error | At this resolution the picture would have more dots than Rotula can draw |
| Too many labels | Error | The whole job would print more than 100,000 labels (every record times its copies). Every copy is a page of its own in the Windows print queue. Print in smaller batches. Reported alone, without asking the driver |
| Bars one dot wide | Error | A barcode's narrowest bar would be a single printer dot, which scanners cannot read reliably |
| Printer-side field | Error | A text or barcode that the printer would number, date or fill from other fields (`^SN`/`^SF`, `^FC`, `^FE`). A picture cannot do that. Use Rotula's own counters and date variables |
| Parts the driver cannot draw | Warning | Raw ZPL kept from an imported file, a barcode only the printer can draw, a graphic stored on the printer, an image too large to draw, and UL and CSA certification marks. They are left out of the picture and counted |
| Bars under the safe size | Warning | A barcode's narrowest bar is under the minimum for its kind of barcode, so it may not scan. Make the barcode larger or use a higher resolution |
| Label size not accepted | Warning | The driver refused the label size as a paper size, so the label is printed in the middle of the paper it has selected |
| Label in the unprintable edge | Warning | The driver accepted the label size and the label fits the page, but the label is more than 1 mm larger than the area the printer can print on (every full-size label on a driver that keeps a margin; many DYMO and Brother drivers keep about 1.5 mm). Anything in that strip along the edges may be cut off, whatever the design. The message names the label size, the printable area and the widest unprintable edge |
| Resolutions that do not divide | Warning | The printer's horizontal and vertical dots per inch are not a whole ratio, so bars may come out slightly uneven |

Objects set to "Do not print" are not checked, and an object with a print condition is checked because some record may print it.

#### The limit on labels in one job

In picture mode each copy of a label is a page of its own in the Windows print queue (50 pages to a driver document by
default), so 99,999,999 copies would be about two million documents. One picture job therefore prints **at most 100,000
labels**, the same number the printer-language path allows for separately numbered labels (`PrintVariableOptions.MaxIndividualLabels`,
shown in the library as `DriverPreflight.MaxLabelsPerJob`). What counts is the whole job: for a batch merge, the Print dialog's
ticked records and the print station's data, every record times its copies; for a single print, the copies. A job over the limit
gets the error *Too many labels* (`Driver.Preflight.TooManyLabels`, arguments: the labels asked for and the limit) and says to
print in smaller batches. It is an error, so no tick lets it through, and it is worked out without asking the driver.

Library callers: `IPrintService.PreflightAsync(target, document, labelCount, ct)` does this check for a job of `labelCount`
labels (the overload without a count is unchanged and cannot know the size of a batch). `PrintResolvedAsync` and `PrintAsync`
repeat it for the labels of the call itself, before the driver is asked and before a serial number is reserved, and fail with
`InvalidLabel` and the same finding as a notice, so a caller that skips the pre-flight cannot flood the queue either. A batch
is sent in jobs of a few records, so the limit of the whole batch is enforced by the pre-flight, which the app always runs.

#### Sideways stock is turned to fit

A roll printer's driver often gives a page of one shape whatever is asked: a 100 x 150 mm label on a driver whose paper is 150 x 100
mm (landscape), or a 150 x 100 mm label on stock the driver only knows as 100 x 150 mm. When the label as designed is larger than
the paper the driver will use, but the same label **turned a quarter turn** fits, Rotula turns it itself instead of refusing:

- **How it decides.** The driver is asked for its page at the designed size. Only if the label is larger than that page (the error
  *Label bigger than the paper*) is the driver asked a second time, for the turned size (width and height swapped). If the turned
  label fits that answer (the same rules as ever: within 1 mm, the same unprintable-edge check), it is printed turned. A label that
  fits as designed is never turned, and a square label has nothing to turn. If neither way fits, the ordinary refusal stands and
  names the label as designed.
- **What is turned.** The finished picture, pixel for pixel (no resampling), so every barcode module keeps its exact size in
  device dots. The driver is told the turned size, and the check before the job starts compares the page the driver really uses with
  the media of the turned size, so the printed result is the one that was judged. The pre-flight and the print call take the same
  decision (the print call asks again and reaches the same answer from the same driver), so they cannot disagree.
- **The direction is clockwise** (the top of the label ends up on the right-hand edge of the paper). No direction is right for every
  printer: whether a roll leaves the printer top edge first or bottom edge first depends on the printer and its driver, and Windows
  itself turns landscape pages one way on some drivers and the other way on others. Clockwise is the fixed default. If the first
  printout comes out upside down, set the label's print orientation to upside down; it is applied to the picture before it is turned.
- **What the person sees.** A warning, *Label turned to fit* (`Driver.Preflight.LabelTurned`; arguments: the label as designed and the
  paper it lies on), needing the usual single acknowledgement. A warning rather than a note because the printout differs from the
  preview the person designed in and may need the one-time orientation check above; the tick is remembered for exactly these
  findings, so it costs one click. The other paper findings after it are the turned label's.
- **Not in printer-language mode.** ZPL printing never turns anything, and the pre-flight is not run for it.

### The one acknowledgement

If there are warnings, the Print dialog shows them in a card and a box, "I understand and want to print anyway". Tick it and
press Print again: it covers the whole job, however many labels. Without the tick nothing is sent. The tick is for exactly
these findings: change the label so the findings change and Rotula asks again. If there are errors, there is no box; fix the
label (or choose another printer or mode) and print again.

### Batch printing and the print station

- **Batch (Data merge).** The same check runs once for the design, before the question about the batch. Copies and serial
  numbers follow the same rules as in ZPL mode: a number is reserved before the job and given back only when nothing could have
  reached the printer; a number that may have printed is never reused. If a job fails part way, the result says up to which
  record labels may have printed.
- **Print station.** The station prints in picture mode when the mode saved for its printer on that PC is Windows driver. An
  operator is never asked about the mode. The findings are accepted once by whoever sets the station up, with **File > Set up
  print station...** (see [print-station.md](print-station.md#picture-mode-any-windows-printer)). The acceptance is kept in
  `station.acknowledged.json` next to `station.json`. An operator who prints a label whose findings were not accepted gets a
  plain message to ask that person; nothing is sent.

### What stays ZPL-only

Printer set-up commands (darkness, speed), the printer status check before and after a job, printer-side counters, cutter and
tear-off belong to the printer language. In picture mode they are not sent: use the driver's own settings dialog for those. The
Darkness and Speed boxes of the Print dialog are therefore hidden in picture mode (with the line that says to leave them empty).
Hidden, they are not read, so they can never stop a print, and the values you set stay in `settings.json` and come back when you
switch the printer back to printer language. The resolution, width and height boxes stay. The
library refuses them too: `PrintZplAsync` and `ApplyProfileAsync` answer `Unsupported` for a Windows queue in driver mode, and a
status query (`GetStatusAsync`, `VerifyPrintedAsync`) answers "not supported" without sending `~HS`. The MCP server and the command
line print ZPL only. ZPL output, export and the ZPL panel are the same as before.

### Known limitations of picture mode

- **Parts left out.** Raw ZPL kept from an imported file, a barcode only a ZPL printer can draw, a recall of a graphic stored on
  the printer (not in the file), an image too large to draw, and UL and CSA certification marks are left out of the picture. They
  are counted and listed before printing, never replaced by a stand-in.
- **Bitmap images are scaled smoothly.** A logo is resampled to the printer's resolution with smooth (not black-and-white,
  dot-for-dot) sampling, so its edges can come out slightly grey and the driver then dithers them. Check a logo on a test label.
- **The unprintable edge.** Many label drivers keep a strip of about a millimetre or two along the edges that they cannot print.
  When the driver accepts the label size, the label still prints, but Rotula warns once when the label is larger than the area the
  driver can print on (every full-size label on such a driver), whatever the design, and names the widest edge: anything in that
  strip may be cut off. Leave a margin of at least that width around the content.
- **Sideways stock is turned for you, one way.** See [Sideways stock](#sideways-stock-is-turned-to-fit). The direction is fixed
  (clockwise); if your printer feeds the label the other way up, set the label's print orientation to upside down.
- **The driver's own scaling and margins** are outside Rotula's control: a driver set to "fit to page" or with its own offsets moves
  the label. Just before printing, Rotula compares the resolution and page the driver is about to use with the ones the check was
  made for, and refuses the job when they changed (for example when the driver's defaults were changed in between). A label is never
  printed on another page than the one the check judged; print again to check the label against the new settings.

### Trying it without a label printer

1. Open the Print dialog and choose **Microsoft Print to PDF** as the printer.
2. When the dialog asks how the printer understands labels, choose **Windows driver (picture)** (or change it with the
   button under the printer name).
3. Press **Print**, and give the PDF a name.
4. Open the PDF. The page is the label's size and the label is drawn on it. Check the size and look at the barcodes.

A PDF has almost no unprintable margin and no label gap, so this shows the drawing and the page size, not what a real
printer's driver does with scaling and margins. The automated tests use Microsoft Print to PDF where it is available.

## Before a release

Automated tests cannot do these, so do them by hand before publishing a version that touches printing:

- [ ] Print one label with a barcode through a real non-Zebra label printer (DYMO, Brother, TSC...) in Windows driver mode and scan it.
- [ ] On that printer, print a label with a bitmap logo and a text in a Windows font, and look at the result.
- [ ] On a real printer whose driver keeps an unprintable margin, print a full-size label whose content reaches the edge. Rotula must print it after one warning that the printer cannot print the outer edge of the page (it is not refused); check on paper that the edge it names matches what is cut off. A label larger than the page itself must be refused.
- [ ] Open the Print dialog for a new printer in English and in French: the question "How does this printer understand labels?" and its two answers fit and read well.
- [ ] In the Print dialog in picture mode, with a label that has findings: the findings card reads well, the tick box "I understand and want to print anyway" is easy to see, and without the tick nothing is sent (English and French).
- [ ] Open **File > Set up print station...**: the window opens, lists the labels, shows the points, and Save and Check again work.
- [ ] In the Preview window and the status bar in picture mode, the longer note about what the preview shows fits and is not cut off.
- [ ] Print a label in ZPL mode to a ZPL printer and check that nothing about it changed.

## Tests

Tests never print. The spooler is faked (`IRawPrinter`), status replies are scripted (`IPrinterStatusReader`); TCP printing and the TCP status query are tested against an in-process `TcpListener`.

## Reading the resolution from the driver

`IPrinterDiscovery.GetPrinterInfoAsync(name)` (implemented by `WinspoolRawPrinter`) and
`IPrintService.GetPrinterInfoAsync(target)` return a `PrinterCapabilities` record: raw horizontal/vertical dpi,
the supported `PrinterDpi` (203, 300 or 600) when the raw value is within 5%, the current paper size and
`DpiSource`. The record is not called `PrinterInfo` because that name is already the queue-list item.

Order of detection for a Windows queue (all read-only: no print job, no output DC, nothing printed, no network):

1. `CreateIC("WINSPOOL", name)` + `GetDeviceCaps(LOGPIXELSX/Y)` (`DpiSource.DeviceCaps`), then the default DEVMODE from
   `DocumentProperties(DM_OUT_BUFFER)` using `dmPrintQuality` (when positive) and `dmYResolution` (`DpiSource.DevMode`),
   then `DeviceCapabilities(DC_ENUMRESOLUTIONS)` when the driver lists exactly one resolution.
2. Tokens in the driver name, then the queue name: `300dpi`, `300 dpi`, `12dpmm`, or a trailing `-600`
   (`DpiSource.DriverName`).

The first source whose value maps to a supported dpi wins. A value that maps to nothing (96, 1200) is kept in
`HorizontalDpi` with `Dpi == null` so the UI can show it and let the user choose. Paper size comes from DEVMODE
`dmPaperWidth/Length`, or from the form list (`DC_PAPERS`/`DC_PAPERSIZE`) for a predefined form; null if unknown.
TCP targets return `PrinterInfoSource.Unknown` (no network probing). Every error yields unknown info; the work runs
on the thread pool.

`PrinterProfile.FromInfo(info, baseProfile)` applies detected dpi and paper size to a profile. With
`PrinterProfile.UseDetectedSettings = true`, `PrintTestLabelAsync` does that automatically; it is off by default so
a resolution the user picked is never overridden.

The test label is sized from physical units (7 mm text, QR module size clamped to Core's 1-10) so it is readable and
inside the label at 203/300/600 dpi.

## Label sheets for office printers

Labels can also be printed on sheets with a laser or inkjet printer. This path does not send ZPL: it draws the labels into a
PDF that is printed from any PDF reader.

- **Model** (`Rotula.Core.Sheets`, no UI): `SheetLayout` is the paper (`Letter`, `A4`, `Legal` or a custom size), the top and
  left margin, the label size, columns x rows and the pitch across and down (label size plus gap), all in millimetres.
  `Validate()` returns message keys (`Sheet.Labels.TooWide`, `Sheet.Labels.OverlapAcross`, `Sheet.Labels.NearEdge` and the
  others in `Issue.Sheet.*`); `ValidateStart` checks a start position and `CheckDesignSize` warns when the design is larger or
  smaller than the positions. `SheetTemplates.All` holds generic Letter and A4 layouts described only by their dimensions;
  `FindForStock` matches a sheet stock of the library by label size and grid, without naming a brand.
- **Imposition**: `SheetImposition.Paginate` pulls the labels lazily and fills the positions in reading order (or down the
  columns), sheet after sheet. The first sheet can start at a later position (a partly used sheet); later sheets start at 1.
- **Values**: the labels come from the same iterations as printing. With data, `MergeBatch.Labels` yields each record with its
  resolved values (`MergedLabel.Values`) and its copies; without data, `LabelPrintRun.LabelValues()` yields one value set per
  copy (each copy of a label with a counter has its own number). The renderer draws them with
  `LabelDocumentAdapter.ToRenderLabelResolved`, which does not resolve the variables again.
- **Serial numbers**: reserved before the PDF is written and confirmed only when the file is complete; a failed or cancelled
  export gives them back. A PDF will be printed, so its numbers count as used, exactly as for "Save as ZPL".
- **Output**: `SheetExporter.SavePdf` writes one page per sheet at the paper's real size. Each label is drawn at its physical
  size (its dots divided by its resolution), centred in its position and clipped to it; text, boxes and barcode modules stay
  vectors. Optional crop marks (in the margins only) or grey outlines. The page size is exact for US paper and within 0.02 mm
  for A4 (the PDF writer rounds it to whole pixels at 600 dpi).
- **The .rotula file** keeps the chosen layout in an optional root `sheet` object (template id, paper, margins, label size,
  grid, pitch, order, guides). It does not raise the format version: an older build ignores it. The start position is not
  saved, because it depends on the sheet in the printer today.

**Not offered: sending the sheets straight to a Windows printer.** Office printer drivers add their own margins and often scale
to fit, which would move every label; a PDF printed at actual size (100 %, no "fit to page") keeps the positions. Sheets of
different suppliers with the same label size can differ by about a millimetre in their margins, so print one page on plain
paper with outlines, hold it against a sheet and adjust the margins.
