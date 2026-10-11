# GS1 support

Rotula builds and checks the data of GS1 barcodes and writes the ZPL that prints them: GS1-128 (including SSCC and EAN-14), ITF-14,
GS1 DataMatrix, GS1 DataBar Expanded and GS1 Digital Link in QR Codes. It also builds the text of the QR Codes phones act on (web address, Wi-Fi,
contact, e-mail, phone, SMS, location, calendar event).

> **Rotula does not certify anything.** It follows the GS1 General Specifications (release 26.0) and the Zebra ZPL II Programming Guide as
> closely as it can, and its checks catch the common mistakes (wrong check digit, impossible date, element string too long). But compliance
> depends on what the printer actually prints: bar width, print contrast, quiet zones, label material, the printer's own firmware. **Check
> the printed labels with a barcode verifier (ISO/IEC 15416 for linear symbols, ISO/IEC 15415 or 29158 for 2D symbols) before you rely on them
> for a retailer, a logistics partner or a regulator.** "GS1" is a trademark of GS1 AISBL; Rotula is not affiliated with GS1.

## What is supported

| Carrier | How to make it in Rotula | ZPL | Check |
|---|---|---|---|
| GS1-128 | Code 128, mode **Ean** (the "UCC/EAN" mode), data written with parentheses: `(01)09506000134352(17)251231(10)ABC123` | `^BC` mode `D` | Every element against the dictionary, 48-character limit |
| SSCC-18 | GS1-128 with `(00)` and the 18 digits. Build the digits with `Gs1Keys.Sscc` | `^BC` mode `D` | Check digit |
| EAN-14 | GS1-128 with `(01)` and a GTIN-14 (`Gs1Itf14.Ean14`) | `^BC` mode `D` | Check digit |
| ITF-14 | Interleaved 2 of 5, the 14 digits, check digit option **off** (`Gs1Itf14.Data`) | `^B2` | `Gs1Itf14.Validate` |
| GS1 DataMatrix | Data Matrix with the option **DataMatrixGs1**, same data as GS1-128 | `^BX` quality 200 with escape character `#` | Every element against the dictionary |
| GS1 DataBar Expanded | GS1 DataBar, type Expanded, same data | `^BR` type 6 | Existing DataBar rules plus the dictionary |
| GS1 Digital Link | QR Code (or Data Matrix) with the option **DigitalLink**, data is the URI built by `Gs1DigitalLink.Build` | `^BQ` / `^BX`, plain text | The URI is parsed and its elements checked |
| GS1 DataBar (other types) | GTIN digits as before | `^BR` | GTIN check digit |

Not possible with the ZPL commands of the guide, so not offered:

* **GS1 QR Code with FNC1 in the second mode (reader identifier `]Q3`).** The `^BQ` section of the guide has no FNC1 switch. In Rotula "GS1 QR"
  means a GS1 Digital Link URI in an ordinary QR Code. (The
  preview encoder can draw an FNC1 QR Code, and its tests read it back as `]Q3`, but no element produces one.)
* **GS1 Aztec.** `^BO` has no GS1 mode.
* **GS1 DataBar composite (types 7-12 of `^BR`).** Data with a `|` is refused (`Barcode.DataBar.Composite`). `Gs1Composite` already splits and
  checks the two components, ready for the day the symbol is supported.
* **ITF-14 bearer bars.** `^B2` has no bearer-bar parameter. Draw the frame with a box element around the barcode (thickness and clearances as
  your application standard asks); a frame drawn by a `^GB` would be imported back as a separate box.

## The ZPL that is written

Citations are to the Zebra "ZPL II, ZBI 2, Set-Get-Do, Mirror, WML Programming Guide" (P1134473-11EN Rev A), page numbers as printed.

### GS1-128: `^BC` mode D

`^BCo,h,f,g,e,m` has four modes (p. 94-95): `N` none, `U` UCC case, `A` automatic, `D` "UCC/EAN mode (x.11.x and newer firmware)": *"The code starts
in the appropriate subset followed by FNC1 to indicate a UCC/EAN 128 barcode. The printer automatically strips out parentheses and spaces for
encoding but prints them in the human-readable section. The printer automatically determines if a check digit is required, calculates it, and prints
it."* (p. 95). Rotula uses `D` because it is the one mode that takes the element string in the form people write and read, and prints the
interpretation line with the parentheses. The FNC1 between elements is the invocation code `>8` (Table 2, p. 96: `>8` = FNC1; and the "Chaining
several application identifiers" samples, p. 104-105), exactly as in the guide's own sample:

```
^BY2,2.5,145^FO218,343^BCB,,Y,N,N,D
^FD(91)0005886>8(10)0000410549>8(99)05^FS
```

* An FNC1 is written after every element of **variable length** that is not the last. Which elements those are comes from the dictionary
  (GS1 General Specifications 7.8.3 and Table 7-6): elements of predefined length (00, 01, 02, 03, 11-13, 15-17, 20, 31-36, 41...) take none.
* A greater-than sign in a value (it is in the GS1 character set) is written `><`, the invocation code for that character (Table 2).
* Parentheses and spaces in a value are lost in mode D, so a value that contains one is an error (`Gs1.Gs128.Parentheses`).
* Mode `U` (UCC case mode, p. 95 and 101) is only for an SSCC (`19` digits) and mode `N` needs the subsets chosen by hand (`>;>8` to start in
  subset C with FNC1, p. 100-101); both stay available as the "Code 128 mode" option and keep working, but neither is checked as GS1.
* Mode `D` with data that does not start with a parenthesis keeps its old meaning (free text after an FNC1 start); labels made before GS1
  support print as they did.
* The check digit option (`e`) is left off for GS1-128: mode D computes it. Switching it on gives the warning `Gs1.Gs128.CheckDigitOption`.
* **Firmware.** The guide says mode D needs "x.11.x and newer firmware". Older printers do not know it.

### GS1 DataMatrix: `^BX` with escape sequences

`^BXo,h,s,c,r,f,g,a` (p. 144-147). For quality 200 the field data may contain escape sequences, *"_1 to _3 for FNC characters 1 to 3"*, with the
escape character chosen in parameter `g` ("any character", p. 145-147). Rotula writes `g` = `#` and builds the data as `#1`, then the elements,
with `#1` after each variable-length element that is not the last:

```
^FO50,50^BX,5,200,,,,#^FD#1010950600013435210ABC123^FS
```

The first FNC1 is what makes the symbol a GS1 DataMatrix (reader identifier `]d2`). The default escape characters cannot be used: the tilde
starts a ZPL control command, and the underscore is the `^FH` indicator Rotula uses whenever a value holds an underscore (the guide says hex
processing happens before escape processing, p. 146), so `_1` would be read as a hex escape. The number sign is not in the GS1 character set, so
it never occurs in a value. A value with an underscore is sent as `_5F` through `^FH_`.

### Digital Link, QR Code and the rest

A Digital Link URI is plain text in a QR Code (`^BQ`, `^FDQA,https://...`) or a Data Matrix. The option **DigitalLink** only turns on the check.
GS1 DataBar Expanded takes the element string with parentheses in `^FD`, as the guide describes for `^BR` type 6.

### What Rotula does not check

* **Physical rules.** Module width (the `^BY` value in dots against the printer's resolution), symbol height, quiet zones, print quality, and the
  165.10 mm maximum length of a GS1-128 symbol. The minimum and target X-dimensions of each application are in the General Specifications,
  section 5.12.3; one dot is 0.125 mm at 203 dpi (so 4 dots are 0.50 mm) and 0.085 mm at 300 dpi. Use the printer's own test, a ruler, and a verifier.
* **The field must fit.** `^BC` field data is limited to the width of the label (guide p. 94). A GS1-128 symbol is `11N + 66` modules wide with its
  quiet zones, N being the number of symbol characters, where two digits share one in subset C (General Specifications 5.4.7.2). A symbol with
  only an SSCC (20 digits, N = 10) is 176 modules: 704 dots at 4 dots per module, which fits the 812 dots of a 4 inch label at 203 dpi. A
  symbol with 48 digits (N = 24) is 330 modules: 1320 dots at 4 dots per module, which does not.
* **The printer chooses the Data Matrix encodation.** A printer picks the modes (C40, text, X12, EDIFACT) that make the smallest symbol; the
  designer preview uses plain ASCII encodation with digit pairs and so can show a slightly larger symbol. The data and the FNC1 positions are the same.
* **Issued prefixes.** The dictionary tests `gcppos1` and `gcppos2` check that the company-prefix position holds digits; whether a prefix was
  really issued by GS1 needs GS1's registry, and Rotula makes no network calls.
* **Coupons** (AI 8110 and 8112) are not tested beyond length and characters (`couponcode` and `couponposoffer` are listed as not checked).

## Validation

`Gs1Validator` gives, per element: unknown identifier, value too short or too long, characters outside the allowed set, then the content tests of the
dictionary (check digits, dates with day `00` where the dictionary allows it, ISO country and currency codes, IBAN, ...). Across the elements of
a message it reports repeated identifiers, combinations the dictionary forbids (`ex=`, an **error**) and companions it requires (`req=`, a
**warning**: the dictionary judges them over all barcodes of the item, and a label often splits them over several symbols).

The century of a two-digit year only matters on 29 February; it is decided from `Gs1LintOptions.ReferenceYear`, so tests do not depend on the clock.

## The dictionary

`src/Rotula.Core/Gs1/Data/gs1-dictionary.json` is built from the **GS1 Barcode Syntax Dictionary**, release 2026-01-27
(https://github.com/gs1/gs1-syntax-dictionary, Apache-2.0; notice in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)). It holds 224 lines that expand
to 541 identifiers: title, Rotula's own plain-English name, format of every component, whether an FNC1 separator is needed, the tests, the `req=`
and `ex=` relations and the Digital Link key and qualifier order. To rebuild it with a newer release, double-click
`tools/gs1-dictionary/Rebuild-Gs1Dictionary.cmd`; the first run creates `settings.json` next to it and asks for the path of the downloaded
`gs1-syntax-dictionary.txt`. The tests fail when a new release names a test that `Gs1Linters` neither runs nor lists as "not checked".

## QR content

`Rotula.Core.Barcodes.QrContent` writes the exact text of:

| Kind | Result |
|---|---|
| `QrUrlContent` | the address, checked |
| `QrWifiContent` | `WIFI:T:WPA;S:name;P:password;H:false;;`, `\ ; , : "` escaped, hex-looking names quoted |
| `QrVCardContent` | vCard 3.0, CRLF, lines folded at 75 UTF-8 octets, `\ , ;` and line breaks escaped |
| `QrMeCardContent` | `MECARD:N:Last,First;TEL:...;;` |
| `QrEmailContent` | `mailto:` with `%20` (never `+`) and CRLF as `%0D%0A` in the body |
| `QrPhoneContent` | `tel:+50761234567`, separators removed |
| `QrSmsContent` | `SMSTO:number:message` |
| `QrGeoContent` | `geo:lat,lon[,alt]` with a decimal point whatever the computer's language |
| `QrCalendarEventContent` | `BEGIN:VEVENT` ... with times in UTC (`20260314T140000Z`), all-day events as dates |

Characters outside ASCII go into the QR Code as the printer sends them (UTF-8 after `^CI28`); the designer preview draws them as ZXing does.
Test your phones with a real scan before printing a batch.

## QR Code options

Every parameter of `^BQ` (guide p. 128-134) is a property of the barcode element and a row of the catalogue:

| ZPL | Property | Notes |
|---|---|---|
| `a` orientation | `Rotation` | The guide says QR Codes print "normal (^FW has no effect on rotation)"; check a rotated one on the printer. |
| `b` model | `QrModel` | 1 or 2 (default 2, recommended). |
| `c` magnification | `ModuleSize` | Dots per module. Rotula accepts 1-10 (the guide allows up to 100). |
| `d` error correction | `QrErrorCorrection` | Written as the first letter of the `^FD` header (`L` 7%, `M` 15%, `Q` 25%, `H` 30%); parameter `d` stays empty. |
| `e` mask | `QrMask` | 0-7, or -1 to leave the parameter out. The guide has no automatic choice: an omitted mask is 7. |
| `^FD` data input | `QrInputMode` | `A,` automatic; `M,N` numeric, `M,A` alphanumeric, `M,B` + four-digit byte count, `M,K` Kanji. The byte count is of the bytes the printer receives (UTF-8 under `^CI28`). The mixed mode of the guide is imported as plain text. |

`QrSizing` (Core) gives the panel its data: `ErrorCorrectionLevels`, `Models`, `Masks`, `InputModes` (value, label key, English name, recovery
percentage); `Analyze(payload, level, model)` (version needed, modules, share of the largest symbol used, whether it fits);
`MinimumModuleDots(dpi, minimumMm = 0.25)`; `SymbolSizeMm(version, moduleDots, dpi)`; `SuggestErrorCorrection(payload)`; `Longest(payloads)`.

Numbers it relies on (ISO/IEC 18004 model 2): data codewords per version and level (the tables ZXing.Net carries), 4 mode bits, character count
bits 10/12/14 (numeric), 9/11/13 (alphanumeric), 8/16/16 (bytes) for versions 1-9, 10-26, 27-40, and 10 bits per 3 digits, 11 per 2
alphanumeric characters, 8 per byte. Largest symbol (version 40), L/M/Q/H: 7089/5596/3993/3057 digits, 4296/3391/2420/1852 alphanumeric
characters, 2953/2331/1663/1273 bytes. Model 1 (version 14 at most): 1167/897/639/507 digits, 707/543/387/307 alphanumeric, 486/373/266/211 bytes
(JIS X 0510 values, not checked against a printer). The estimate puts the whole payload in one character mode; a printer may mix modes and fit a
little more, never less. The text is counted as UTF-8 bytes.

## Content builder (QR)

A QR Code can carry a structured definition (`BarcodeElement.QrContentSpec`, JSON, kept through `ExportExtraOptions` and the `.rotula` file): a
kind (URL, text, Wi-Fi, vCard, MeCard, GS1 Digital Link, e-mail, phone, SMS, location, calendar event) and named fields, each of which may hold
fixed text, `{{column}}` placeholders, or both. `BarcodeElement.Data` keeps the generated template (`QrContentBuilder.BuildTemplate`) for older code.

When the ZPL is generated, and for the preview, the payload is built **per record** by `QrContentBuilder.Resolve`: first the placeholders are
replaced with the record's values, then the values are cleaned (control characters removed; line breaks kept only in multi-line fields) and
escaped for the format. A name like `Smith; John`, an address with commas, or a Wi-Fi password holding `;S:evil;;` is data inside its field and
cannot close the field, add a property or start a new line. Payloads with errors are not printed. `QrContentSchema` describes every kind and field
(label key, required, input hint, maximum length, choices, sample) so a dialog can be built from data. `QrContentBuilder.Validate` checks a design
(fields with placeholders are checked with their sample value) and every resolved payload is checked against the capacity of the symbol, with the
suggestion to lower the error correction level or shorten the text.

## Message keys

Core returns keys with culture-neutral arguments; the application translates them. `Args[0]` of every `Gs1.Lint.*`, `Gs1.Value.*` and `Gs1.Ai.*`
key is the application identifier. The keys are in the code as literals; search for the prefix `Gs1.` and `QrContent.`.
