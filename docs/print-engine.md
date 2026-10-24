# Print engine: command line and REST API

`rotula.exe` prints Rotula labels without the designer. A script, a scheduled task or a warehouse system (WMS, ERP, shipping
software) hands it a `.rotula` label, the data and a printer; it prints exactly as the designer's Print dialog would, checks the
printer before every job and says how it went in a form a program can act on.

It has two faces over one engine:

- **`rotula print`, `rotula status`, `rotula printers`**: a command line for scripts and schedulers. It runs with the rights of the
  person or service that starts it.
- **`rotula serve`**: a REST API for systems on this computer or the network. It prints only what its settings file allows (API
  keys, label folders, printers) and can run as a Windows service.

What it shares with the designer:

- **The same print path.** Resolution conversion to the printer, darkness and speed, variables (dates, counters, questions, computed
  values), data merge with the counters, copies and "skip or stop" settings saved with the label, and Windows fonts drawn as
  pictures.
- **Serial numbers are used once.** Counters that remember their value use the same counter file as the designer
  (`%AppData%\Rotula\counters.json`, or `--counter-file` / `counterFile`). Numbers are reserved before a run and confirmed only for
  labels that reached the printer: a printer that was offline gives the numbers back; a send that broke halfway keeps them used, so a
  number is never printed twice.
- **The printer status check (ZPL `~HS`).** Before every job the engine asks the printer whether it can print and holds the job back
  while it reports it is out of labels, its printhead is open, the ribbon is out, it is paused, too hot, in diagnostics mode or its
  buffer is full. After the last job it waits (up to 30 seconds) until the printer reports it finished. A printer that cannot answer
  (many USB drivers) never blocks printing; the result says the status was not checked. `--no-status` and `--no-verify` (or
  `"checkStatus": false` and `"verify": false`) switch either part off.
- **One job at a time per printer.** Requests for the same printer wait for each other, so two callers never interleave their
  labels. Different printers print at the same time.
- **The messages of the app**, in English, Spanish, Portuguese (Brazil) or French (Canada).

## Command line

```text
rotula print LABEL.rotula --printer NAME | --host ADDRESS [options]
rotula status --printer NAME | --host ADDRESS [--port N]
rotula printers
rotula serve [--settings FILE] [--urls URL]      rotula serve --init [--label-folder FOLDER]
rotula new-key
rotula help | version
```

| Option | Meaning |
|---|---|
| `--printer NAME` | An installed Windows printer (USB, shared or network queue). |
| `--host ADDRESS`, `--port N` | A network printer reached directly over TCP (port 9100 when not given). |
| `--copies N` | Copies of the label, or of every record. Without it, the quantity saved in the label (or the data's copies column) is used. |
| `--set NAME=VALUE` | The value of a field (`{{NAME}}` in the label). Repeat for more fields. |
| `--data FILE` | Records from a `.csv` / `.txt` (delimiter detected), `.xlsx` (`--sheet NAME`) or `.json` file; one label per record. A JSON object is one label's values. |
| `--answer NAME=VALUE` | The answer to a question the label asks at print time (a prompt variable). |
| `--dpi 203\|300\|600` | Printhead resolution. Without it, a Windows printer's driver is asked; a network printer prints at the label's own resolution. |
| `--darkness 0-30`, `--speed 1-14` | Sent to the printer before the labels. |
| `--no-status`, `--no-verify` | Skip the status check before every job, or the wait for "finished" after the last one. |
| `--dry-run` | Builds the ZPL and prints it to the output (or to `--output FILE`) without sending anything and without using up serial numbers. |
| `--reference TEXT` | Your own reference (order, wave, shipment), repeated in the result and the server log. |
| `--counter-file FILE` | Where remembering counters keep their position. |
| `--json` | The result as JSON, in the same shape as the REST API's answer. |
| `--lang en\|es\|pt-BR\|fr-CA` | Language of the messages. Without it the Language policy, then the `ROTULA_LANG` environment variable, then Windows decides. |

Questions a label asks at print time take their **designer default** when no `--answer` is given. A required question without
a default and without an answer stops the job and lists every question; a sample value is never printed in place of an answer.

Examples:

```bat
rotula print "C:\Labels\carton.rotula" --printer "Dock 1" --set sku=4006381333931 --set lot=L2410 --copies 2
rotula print "C:\Labels\carton.rotula" --host 10.20.0.31 --data "C:\Exports\wave-1207.csv" --reference WAVE-1207
rotula print "C:\Labels\carton.rotula" --printer "Dock 1" --data picks.json --dry-run --output wave.zpl
rotula status --host 10.20.0.31 --json
```

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Printed (or the dry run worked, or the printer is ready). |
| 1 | Sending failed (printer not found, offline, unreachable...), or the status could not be read. |
| 2 | The command line or the request is wrong (unknown option, missing printer, unreadable data or label file). |
| 3 | The label has errors, or the answers to its questions are missing. Nothing was sent. |
| 4 | The printer reported a fault (out of labels, head open, paused...) and nothing was sent; for `status`, the printer cannot print now. |
| 5 | Switched off by the organization's `PrintEngine` policy. |
| 6 | Stopped with Ctrl+C. Labels already sent are counted; serial numbers of labels not sent go back. |

## REST API

### Set up

```bat
rotula serve --init --settings "C:\ProgramData\Rotula\rotula-engine.settings.json" --label-folder "D:\Labels"
```

This writes a starter settings file and prints a new API key **once**. Give the key to the calling system; the file keeps only its
SHA-256 hash. Add the printers, then start the server:

```bat
rotula serve --settings "C:\ProgramData\Rotula\rotula-engine.settings.json"
```

```jsonc
{
  // Where the server listens. 127.0.0.1 accepts calls from this computer only.
  "urls": ["http://127.0.0.1:9180"],
  // SHA-256 hashes of the API keys ("rotula new-key" makes a key and its hash). Several keys can be listed, one per caller.
  "apiKeyHashes": ["3f1c...e9"],
  // A request names a label relative to one of these folders: "outbound/carton.rotula".
  "labelFolders": ["D:\\Labels"],
  // Windows printer names, or host / host:port of network printers. Nothing else can be printed to or queried.
  "allowedPrinters": ["Dock 1", "10.20.0.31", "10.20.0.32:6101"],
  // Accept the content of a .rotula file in the request (labelContent). Off: only labels from the folders print.
  "allowInlineLabels": false,
  "maxCopies": 1000,
  "maxRecords": 10000,
  "maxRequestBytes": 10485760,
  "maxLabelBytes": 5242880,
  // Language of the messages when a request has no Accept-Language header.
  "language": "en",
  // Optional: the counter file shared with the designer, for example on a server where the designer also runs.
  "counterFile": null
}
```

Unknown settings are an error (a misspelt `allowedPrinter` must not silently allow nothing or everything), and the server does not
start without at least one valid key. Everything a caller can reach is listed: there is no "allow all".

**HTTPS.** Listening on another address than `127.0.0.1` over plain `http` works but the server warns, because keys and label data
would cross the network unencrypted. Use an `https://` URL with a certificate configured through the standard ASP.NET Core
settings, for example the environment variables `ASPNETCORE_Kestrel__Certificates__Default__Path` and
`ASPNETCORE_Kestrel__Certificates__Default__Password`, or put the server behind your reverse proxy.

**As a Windows service** (elevated prompt):

```bat
sc.exe create RotulaEngine binPath= "\"C:\Program Files\Rotula\rotula.exe\" serve --settings \"C:\ProgramData\Rotula\rotula-engine.settings.json\"" start= auto
sc.exe start RotulaEngine
```

Run the service as an account that may use the printers and read the label folders. A service account has its own
`%AppData%`; set `counterFile` when serial numbers must continue from the designer's file.

### Calls

Every route except `GET /api/v1/health` and the OpenAPI document needs the key in the `X-Api-Key` header (or
`Authorization: Bearer KEY`). The OpenAPI 3 description is at `/openapi/v1.json`. Messages follow the `Accept-Language` header
(or `?lang=`).

| Route | What it does |
|---|---|
| `GET /api/v1/health` | `{"status":"ok","version":"0.4.0","printingBlocked":false}`. No key needed. |
| `GET /api/v1/printers` | The Windows printers, each with `allowed` (on `allowedPrinters`). |
| `GET /api/v1/printers/{name}/status` | Status of an allowed Windows printer. |
| `POST /api/v1/printers/status` | Status of an allowed printer: `{"host":"10.20.0.31"}` or `{"printer":"Dock 1"}`. |
| `POST /api/v1/jobs` | Prints. |

A print request:

```json
{
  "label": "outbound/carton.rotula",
  "printer": "Dock 1",
  "reference": "WAVE-1207",
  "copies": 1,
  "records": [
    { "sku": "4006381333931", "lot": "L2410", "qty": 12 },
    { "sku": "4006381333948", "lot": "L2411", "qty": 6 }
  ],
  "answers": { "operator": "M. Rivera" }
}
```

| Field | Meaning |
|---|---|
| `label` / `labelContent` | A label file relative to a label folder, or the content of a `.rotula` file (only with `allowInlineLabels`). Exactly one. |
| `printer` / `host`, `port` | A Windows printer, or a network printer and its port. Exactly one, and it must be on `allowedPrinters`. |
| `values` | The field values of one label. |
| `records` | Several records; one label (times the copies) each. Not together with `values`. Values may be text, numbers or true/false. |
| `copies`, `answers`, `dpi`, `darkness`, `speed` | As the command-line options. |
| `checkStatus`, `verify` | `true` by default. |
| `dryRun` | `true` returns the ZPL in `zpl` without printing. |
| `reference` | Your own reference, repeated in the answer and the log. |

Unknown fields are refused with 400, so a misspelt `copeis` never prints the label's own quantity by accident.

The answer is the same JSON as `rotula print --json`:

```json
{
  "outcome": "printed",
  "message": "Sent 2 record(s), 2 label(s), to Dock 1 in 1 job(s).",
  "printer": "Dock 1",
  "records": 2,
  "labels": 2,
  "jobs": 1,
  "bytesSent": 1874,
  "notices": [ { "code": "PrinterStatus.Verified", "severity": "warning", "message": "The printer reported that it finished the job." } ],
  "reference": "WAVE-1207",
  "succeeded": true
}
```

| `outcome` | HTTP | Meaning |
|---|---|---|
| `printed` | 200 | Every label was handed to the printer. Read `notices` for the verification. |
| `dryRun` | 200 | Nothing was sent; the ZPL is in `zpl`. |
| `invalid` | 400 | The request is wrong (copies out of range, values and records together...). |
| `invalidLabel` | 422 | The label has errors or questions are unanswered; `notices` say which. Nothing was sent. |
| `heldBack` | 409 | The printer reported a fault before a job; `errorCode` is `PaperOut`, `HeadOpen`, `RibbonOut`, `Paused`... Nothing more was sent. |
| `failed` | 502 | Sending failed (`errorCode`: `Offline`, `HostUnreachable`, `Timeout`...). `jobs` says how many went out before. |
| `cancelled` | 503 | The call was cancelled. |

Refusals before the engine runs (no key 401, printer or label folder not allowed 403, label not found 404, unreadable body 400)
answer `{"errorCode":"Engine.Error.PrinterNotAllowed","message":"..."}`.

### Log

The server writes one line per job to its console (and to the Windows event log when it runs as a service): the reference, an
8-character id of the API key (a piece of its hash, useless for calling the server), the caller's address, the outcome, the printer,
the number of labels and jobs, and the error code. It never logs label data.

## Policy

An administrator can switch the engine off for a computer with the **Allow the print engine to print** policy (`PrintEngine`,
REG_DWORD `0`, in `packaging/policies`). The command line then refuses to print and to query printers (exit code 5), and the
server answers 403 to jobs and status requests; the health check reports `"printingBlocked": true`. The policy cannot switch
anything on; the server's own settings still decide what it allows. See [deployment.md](deployment.md).

## Where it is

The installer and the MSI put `rotula.exe` beside the designer: `%LOCALAPPDATA%\Programs\Rotula\rotula.exe` for a per-user
install, `C:\Program Files\Rotula\rotula.exe` for all users (MSI or `/ALLUSERS`). The portable zip has it too. It is not added to
`PATH`; call it by its full path or add the folder yourself. For a Windows service, use the all-users install so the service
account can reach the program.

## Building it yourself

`rotula.exe` is built from `src/Rotula.Cli` (the engine itself is `src/Rotula.Engine`, a library with no UI):

```bat
dotnet publish src\Rotula.Cli\Rotula.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish\win-x64-engine
```
