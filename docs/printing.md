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

## Tests

Tests never print. The spooler is faked (`IRawPrinter`); TCP is tested against an in-process `TcpListener`.
