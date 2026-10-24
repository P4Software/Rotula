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
