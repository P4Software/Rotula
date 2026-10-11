# ZPL coverage

> This page is generated from `src/Rotula.Core/Zpl/Catalog/commands.json`. Do not edit it by hand: change the catalogue and run
> `dotnet run --project tools/gen-zpl-coverage` from the repository root. A test fails when this page is out of date, and other
> tests check every status against the real parser and generator.

Rotula reads and writes ZPL II. This page lists every ZPL II command and says exactly what Rotula does with it. Output is
deterministic (the same label and data always give byte-identical ZPL) and pure ASCII: text outside plain ASCII is written with
`^FH` hex escapes, and `^CI28` (UTF-8) is written by default so accents and symbols such as the Spanish inverted marks print correctly.

## At a glance

| Status | Commands |
|---|---:|
| Supported | 75 |
| Passthrough | 30 |
| Planned | 5 |
| Out of scope | 112 |
| **All** | **222** |

| Group | Supported | Passthrough | Planned | Out of scope | All |
|---|---:|---:|---:|---:|---:|
| Layout & flow | 12 | 0 | 3 | 0 | 15 |
| Fields | 16 | 2 | 0 | 0 | 18 |
| Text & fonts | 4 | 8 | 0 | 0 | 12 |
| Graphics | 6 | 0 | 0 | 0 | 6 |
| Barcodes | 30 | 1 | 0 | 0 | 31 |
| Images & storage | 2 | 6 | 2 | 2 | 12 |
| Media | 5 | 11 | 0 | 11 | 27 |
| Printer configuration | 0 | 2 | 0 | 36 | 38 |
| Host/status | 0 | 0 | 0 | 19 | 19 |
| Network/wireless | 0 | 0 | 0 | 24 | 24 |
| RFID | 0 | 0 | 0 | 15 | 15 |
| Kiosk | 0 | 0 | 0 | 5 | 5 |

## How to read this page

Each group below has one table. A command written `^XX / ~XX` exists with both prefixes and behaves the same way.

| Status | Meaning |
|---|---|
| **Supported** | Rotula understands the command. It is read into the label model when ZPL is opened and written back when the label is generated. |
| **Passthrough** | The command can appear in a label file and affects printing, but Rotula does not model it. It is kept word for word and listed as a notice. |
| **Planned** | Not modelled yet, but meant to be. Until then it behaves like Passthrough. |
| **Out of scope** | A label designer will not model it (printer administration, queries, network, RFID, kiosk hardware). The table says why. It still behaves like Passthrough if a file contains it. |

| Column | Meaning |
|---|---|
| Generate | Yes when Rotula writes the command under its own name when it creates ZPL. |
| Read | Yes when Rotula interprets the command when it opens ZPL. |
| Preview | **Exact**: the on-screen preview draws what the printer prints. **Close**: drawn, but not identical. **Box**: a labelled box marks the place because only the printer can draw it. **No**: it changes the label but the preview ignores it. **n/a**: no visible effect. |

## What happens to commands Rotula does not support

Nothing is silently dropped.

- **Kept word for word.** A command Rotula does not interpret is stored in the label as raw ZPL, exactly as it was written, and is
  written back in the same place when ZPL is generated. A field that uses an unsupported option is kept whole, because a
  half-understood field would print differently from the original.
- **Warned.** Each kept command is listed as a notice with its line and column, so you can see what Rotula did not interpret.
  Harmless comments (`^FX`) are kept without a notice.
- **Round trips.** Opening ZPL and generating it again keeps the geometry, text, barcodes and graphics Rotula supports, and keeps
  every unsupported command in its original position. The one thing a ZPL file cannot carry is Rotula's own information, such as
  `{{variable}}` placeholders: an imported label is plain text until you edit it.
- **Stored objects.** Recalls of things kept in the printer (`^XG`, `^IM`, `^IL`, `^XF`) become placeholders and are written back
  unchanged. When the same file downloads the graphic with `~DG` (or a plain bitmap `~DY`), the preview draws it.
- **Printers differ.** Some commands, values and print modes exist only on some models or firmware versions. Rotula writes what the
  ZPL guide defines; always print a test label before a production run.

## Commands by group

### Layout & flow

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^CC / ~CC` | Change the command prefix | Planned | No | No | No | Replaces the caret that starts every command with another character. Files that change the prefix are misread from that point on, because Rotula always looks for the default characters. The command is kept, but what follows it is not understood. |
| `^CD / ~CD` | Change the delimiter | Planned | No | No | No | Replaces the comma that separates command parameters with another character. Files that change the delimiter are misread from that point on: parameters are always split at commas. |
| `^CT / ~CT` | Change the tilde prefix | Planned | No | No | No | Replaces the tilde that starts the immediate commands with another character. Files that change it are misread from that point on. |
| `^LH` | Label home | Supported | Yes | Yes | Exact | Moves the zero point of all positions by a fixed offset from the top-left corner of the label. |
| `^LL` | Label length | Supported | Yes | Yes | Exact | Sets the length of the label in dots. |
| `^LR` | Reverse all fields | Supported | No | Yes | Exact | Prints every field that follows as white on black or black on white, as if each had its own reverse switch. Imported as a reverse flag on each field that follows, so the label prints the same; Rotula writes the flag per field, not ^LR. |
| `^LS` | Label shift | Supported | Yes | Yes | Exact | Shifts every field to the left by a number of dots; a negative value shifts to the right. Written only when it is not zero. |
| `^LT` | Label top | Supported | Yes | Yes | Exact | Moves the whole format down (or up for a negative value) by a few dot rows to fine-tune where it lands on the label. Rotula accepts -240 to 240; most printers only accept -120 to 120. |
| `^MU` | Units of measure | Supported | No | Yes | n/a | Switches the units used by later numbers to dots, inches or millimetres. Values are converted to dots on import and a notice is shown. |
| `^PM` | Mirror the label | Supported | Yes | Yes | No | Prints the whole label as a mirror image, left to right. The on-screen preview does not mirror. |
| `^PO` | Print orientation | Supported | Yes | Yes | No | Prints the whole label rotated by 180 degrees. The on-screen preview always shows the normal orientation. |
| `^PQ` | Print quantity | Supported | Yes | Yes | n/a | Sets how many labels print, how many between pauses, and how many copies of each serial number. |
| `^PW` | Print width | Supported | Yes | Yes | Exact | Sets the width of the printable area in dots. |
| `^XA` | Start of label | Supported | Yes | Yes | n/a | Opens a label format. Everything up to the matching end-of-label command belongs to one label. |
| `^XZ` | End of label | Supported | Yes | Yes | n/a | Closes a label format and sends it to print. |

### Fields

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^FB` | Field block | Supported | Yes | Yes | Close | Wraps text inside a block of a given width, with line limit, spacing, alignment and hanging indent. The preview uses a stand-in typeface, so line breaks can differ a little from the printer. |
| `^FC` | Field clock indicators | Supported | Yes | Yes | No | Chooses the characters that mark real-time-clock codes (date and time) inside a field. The preview shows the codes as typed, not the date. Works only on printers with a clock. A date variable set to use the printer clock writes this command by itself (see docs/variables.md). |
| `^FD` | Field data | Supported | Yes | Yes | Close | Holds the text or data of a field. |
| `^FE` | Field number embedding character | Supported | Yes | Yes | No | Chooses the character that marks references to numbered fields inside the data of a field. The text is written unchanged; the printer expands the references. |
| `^FH` | Hexadecimal escapes in field data | Supported | Yes | Yes | Close | Lets field data carry any byte as an underscore and two hex digits, so accents and control characters survive. Written automatically whenever the text needs it. |
| `^FM` | Multiple field origins | Passthrough | No | No | Box | Gives the places where each part of a PDF417 or MicroPDF417 symbol that is split over several symbols is printed. A field that uses it is kept whole as raw ZPL. |
| `^FN` | Field number | Supported | Yes | Yes | No | Gives a field a number so its data can be filled in or reused later; an optional prompt can be stored with it. Kept on the field and written back. Filling numbered fields from a stored template is not done by Rotula yet. |
| `^FO` | Field origin | Supported | Yes | Yes | Exact | Starts a field and places its top-left corner at the given position. |
| `^FP` | Field direction | Supported | Yes | Yes | No | Prints the characters of a text field top to bottom or right to left, with an optional gap between characters. Text fields only. The preview always draws left to right. |
| `^FR` | Reverse a field | Supported | Yes | Yes | Exact | Prints one field as the opposite of what is already under it. |
| `^FS` | Field separator | Supported | Yes | Yes | n/a | Ends a field. |
| `^FT` | Field typeset position | Supported | Yes | Yes | Exact | Starts a field and places it by its baseline (text) or bottom-left corner (other objects). |
| `^FV` | Field variable data | Supported | No | Yes | Close | Holds the data of a field the same way ^FD does; used in stored formats to mark data that changes. Read as ^FD and written as ^FD. |
| `^FW` | Default field rotation | Supported | Yes | Yes | Exact | Sets the rotation that fields use unless they give their own. |
| `^FX` | Comment | Passthrough | No | No | No | A note for people reading the file; it prints nothing. Kept as a raw element so notes survive a round trip, without a warning. |
| `^SF` | Serialization mask | Supported | Yes | Yes | Close | Makes the printer change selected characters of the field data on every label, following a mask. The first label's value is shown. |
| `^SN` | Serial number (counting) | Supported | Yes | Yes | Close | Makes the printer count a number up or down on every label of a print run. The first label's value is shown. This is separate from the counters of the merge feature, which write a different literal value per record. |
| `^TB` | Text box | Supported | Yes | Yes | Close | Wraps text inside a box of fixed width and height and drops the lines that do not fit. A less-than sign in the text is written as three characters so the printer does not read it as an escape. Line breaks in the text become spaces. |

### Text & fonts

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^A` | Font selection | Supported | Yes | Yes | Close | Chooses a font, its rotation, and its height and width in dots. A letter or digit picks a built-in or assigned font. Includes ^A0 (the scalable font) and ^A@ (a font addressed by file name, which is kept with a warning). The preview draws every font with a stand-in typeface. |
| `^CF` | Default font | Supported | Yes | Yes | Close | Sets the font and size used by fields that do not choose their own. |
| `^CI` | Character set | Supported | Yes | Yes | n/a | Chooses how the bytes of field data map to characters; Rotula writes UTF-8 by default. National sets and remapping parameters give a warning. |
| `^CO` | Character cache | Passthrough | No | No | No | Sets the size of the printer's character cache, or the font memory it uses. Kept verbatim. |
| `^CW` | Font identifier | Supported | Yes | Yes | No | Assigns a one-character font id to a font file stored on the printer. Kept as a document setting and written before the fields. A change after the first field is kept as raw ZPL with a warning. |
| `~DB` | Download bitmap font | Passthrough | No | No | No | Sends a bitmap font to the printer. Kept verbatim; the data is binary. |
| `~DE` | Download encoding | Passthrough | No | No | No | Sends a character encoding table to the printer. Kept verbatim. |
| `~DS` | Download scalable font | Passthrough | No | No | No | Sends a scalable font to the printer. Kept verbatim; the data is binary. |
| `~DT` | Download TrueType font | Passthrough | No | No | No | Sends a TrueType font to the printer. Kept verbatim; the data is binary. |
| `~DU` | Download unbounded TrueType font | Passthrough | No | No | No | Sends a TrueType font of any size to the printer. Kept verbatim; the data is binary. |
| `^FL` | Font linking | Passthrough | No | No | No | Links fonts so characters missing from one are taken from another. Kept verbatim. |
| `^PA` | Advanced text properties | Passthrough | No | No | No | Switches on bidirectional text, glyph shaping and similar options for complex scripts. Kept verbatim. |

### Graphics

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^GB` | Box and lines | Supported | Yes | Yes | Exact | Draws a rectangle with a border; a box that is as thin as its border is a straight line. |
| `^GC` | Circle | Supported | Yes | Yes | Exact | Draws a circle with a border of a chosen thickness. |
| `^GD` | Diagonal line | Supported | Yes | Yes | Exact | Draws a straight diagonal line corner to corner across a box, leaning right or left. |
| `^GE` | Ellipse | Supported | Yes | Yes | Exact | Draws an ellipse with a border of a chosen thickness. |
| `^GF` | Graphic field | Supported | Yes | Yes | Exact | Prints a black-and-white bitmap sent as data inside the field. Output is ASCII hex. Input also accepts ZPL run-length compression and Z64 or B64 data; the binary formats are kept as raw ZPL. |
| `^GS` | Graphic symbol | Supported | Yes | Yes | Close | Prints a registered, copyright or trademark sign, or the UL or CSA approval mark. The printer draws the symbol from its own font; the preview shows the nearest letters or signs. |

### Barcodes

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^B0` | Aztec Code (zero spelling) | Supported | No | Yes | Exact | The guide also lists Aztec Code as ^B0, with a zero. It is read exactly like ^BO. Read as ^BO and written as ^BO. |
| `^B1` | Code 11 | Supported | Yes | Yes | Exact | Prints a Code 11 barcode with one or two check digits. |
| `^B2` | Interleaved 2 of 5 | Supported | Yes | Yes | Exact | Prints a numeric Interleaved 2 of 5 barcode with an optional check digit. |
| `^B3` | Code 39 | Supported | Yes | Yes | Exact | Prints a Code 39 barcode with an optional check digit. |
| `^B4` | Code 49 | Supported | Yes | Yes | Box | Prints a Code 49 stacked barcode. The printer draws it; the preview shows a labelled box. |
| `^B5` | PLANET Code | Supported | Yes | Yes | Exact | Prints a PLANET postal barcode. |
| `^B7` | PDF417 | Supported | Yes | Yes | Exact | Prints a PDF417 stacked barcode with a chosen security level and size. |
| `^B8` | EAN-8 | Supported | Yes | Yes | Exact | Prints an EAN-8 product barcode. |
| `^B9` | UPC-E | Supported | Yes | Yes | Exact | Prints a UPC-E short product barcode. The designer also accepts the 6-digit UPC-E form and writes the code the printer expects. |
| `^BA` | Code 93 | Supported | Yes | Yes | Exact | Prints a Code 93 barcode with an optional check digit. |
| `^BB` | Codablock | Supported | Yes | Yes | Box | Prints a Codablock A, E or F stacked barcode. The printer draws it; the preview shows a labelled box. |
| `^BC` | Code 128 | Supported | Yes | Yes | Exact | Prints a Code 128 barcode, a dense code for any text, with optional mode and human-readable line. |
| `^BD` | UPS MaxiCode | Supported | Yes | Yes | Box | Prints a UPS MaxiCode symbol. The printer draws it; the preview shows a labelled box. |
| `^BE` | EAN-13 | Supported | Yes | Yes | Exact | Prints an EAN-13 product barcode. |
| `^BF` | MicroPDF417 | Supported | Yes | Yes | Box | Prints a MicroPDF417 symbol, the small version of PDF417. The printer draws it; the preview shows a labelled box. |
| `^BI` | Industrial 2 of 5 | Supported | Yes | Yes | Close | Prints an Industrial 2 of 5 numeric barcode. The guide has no printed sample, so the preview follows the published definition. |
| `^BJ` | Standard 2 of 5 | Supported | Yes | Yes | Exact | Prints a Standard 2 of 5 numeric barcode. |
| `^BK` | ANSI Codabar | Supported | Yes | Yes | Exact | Prints a Codabar barcode; the start and stop characters are options, not part of the data. |
| `^BL` | LOGMARS | Supported | Yes | Yes | Exact | Prints a LOGMARS barcode, a Code 39 variant with a mandatory check character. The check character is always added. |
| `^BM` | MSI | Supported | Yes | Yes | Exact | Prints an MSI barcode with a choice of check digit schemes. |
| `^BO` | Aztec Code | Supported | Yes | Yes | Exact | Prints an Aztec Code with magnification, error control and the menu, rune and structured-append options. Rune, menu, structured append and ECI symbols are shown as a placeholder in the preview. |
| `^BP` | Plessey | Supported | Yes | Yes | Exact | Prints a Plessey barcode. |
| `^BQ` | QR Code | Supported | Yes | Yes | Exact | Prints a QR Code with a chosen model, size and error correction. Manual mode gives a warning. |
| `^BR` | GS1 DataBar | Supported | Yes | Yes | Exact | Prints a GS1 DataBar symbol of types 1 to 6, including stacked and expanded. Types 7 to 12 and composite data are kept as raw ZPL with a warning. |
| `^BS` | UPC/EAN add-on | Supported | Yes | Yes | Exact | Prints the 2- or 5-digit add-on that sits next to a UPC or EAN barcode. |
| `^BT` | TLC39 | Supported | Yes | Yes | Box | Prints a TLC39 symbol, a Code 39 line with a MicroPDF417 part. The printer draws it; the preview shows a labelled box. |
| `^BU` | UPC-A | Supported | Yes | Yes | Exact | Prints a UPC-A product barcode. |
| `^BX` | Data Matrix | Supported | Yes | Yes | Exact | Prints a Data Matrix symbol (ECC 200). Quality below 200 gives a warning. |
| `^BY` | Barcode defaults | Supported | Yes | Yes | n/a | Sets the default module width, wide-to-narrow ratio and height used by the barcodes that follow. |
| `^BZ` | POSTNET and PLANET | Supported | Yes | Yes | Exact | Prints a POSTNET or PLANET postal barcode. Types 2 and 3 (Intelligent Mail) are kept as raw ZPL with a warning. |
| `^CV` | Code validation | Passthrough | No | No | No | Makes the printer check barcode data and reject bad barcodes. Kept verbatim. |

### Images & storage

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^DF` | Store format | Planned | No | No | No | Saves the commands that follow as a reusable template in the printer's memory. Templates, with numbered fields filled in at print time, are planned. Today the command is kept verbatim with a warning. |
| `~DG` | Download graphic | Supported | Yes | Yes | Exact | Sends a bitmap to the printer's memory under a name, to be printed later with ^XG or ^IM. Kept as a document setting and written before the label. A recall in the same file is drawn in the preview. |
| `~DN` | Abort graphic download | Out of scope | No | No | No | Stops a graphic download that is in progress. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~DY` | Download object | Supported | No | Yes | Exact | Sends a file (a bitmap, font or other object) to the printer's memory. Only plain bitmap data (hex or Z64 text, format A, type G or B) is understood; it is written back as ~DG. PNG, TrueType and binary downloads are kept as raw ZPL. |
| `~EG` | Erase all graphics | Passthrough | No | No | No | Deletes every stored graphic from the printer's memory. Kept verbatim. |
| `^ID` | Delete stored object | Passthrough | No | No | No | Deletes objects from the printer's memory. Kept verbatim. |
| `^IL` | Image load | Passthrough | No | No | Box | Loads a stored label image at the top-left corner as the background of the label. Kept as a stored-object placeholder. |
| `^IM` | Image move | Passthrough | No | No | Box | Prints a graphic stored on the printer at its original size. Kept as a stored-object placeholder. When the same file downloads the graphic with ~DG, the preview draws it. |
| `^IS` | Image save | Passthrough | No | No | No | Saves the label format as an image in the printer's memory instead of printing it. Kept verbatim with a warning. |
| `^TO` | Transfer object | Out of scope | No | No | No | Copies stored objects from one storage device to another. *Out of scope: Manages the printer's own storage; it does not change what a label looks like.* |
| `^XF` | Recall format | Planned | No | No | No | Prints a stored template, filling its numbered fields. Planned together with ^DF. Today the recall is kept as a stored-object placeholder with a warning. |
| `^XG` | Recall graphic | Passthrough | No | No | Box | Prints a graphic stored on the printer, at a chosen magnification. Kept as a stored-object placeholder. When the same file downloads the graphic with ~DG, the preview draws it. |

### Media

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `~JC` | Calibrate media sensor | Out of scope | No | No | No | Starts media calibration. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JD` | Start communications diagnostics | Out of scope | No | No | No | Switches communications diagnostics on. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~JE` | Stop communications diagnostics | Out of scope | No | No | No | Switches communications diagnostics off. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~JG` | Graph the sensor | Out of scope | No | No | No | Prints a graph of the sensor readings. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `^JM` | Dots per millimetre | Passthrough | No | No | No | Switches the printer to half its resolution for large labels. Kept verbatim. Rotula keeps the resolution you chose for the label. |
| `^JS` | Sensor select | Out of scope | No | No | No | Chooses which sensor tracks the media. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JS` | Backfeed sequence | Out of scope | No | No | No | Changes when the printer backs the media up. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JW` | Ribbon tension | Out of scope | No | No | No | Sets how tight the ribbon is held. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JZ` | Reprint after error | Passthrough | No | No | No | Chooses whether the label is reprinted after a media or ribbon error. Kept verbatim. |
| `^MD` | Media darkness | Supported | Yes | Yes | n/a | Makes the print lighter or darker relative to the printer's current darkness. Decimal values are allowed. |
| `^MF` | Media feed at power-up and head close | Passthrough | No | No | No | Chooses whether the printer feeds, calibrates or does nothing when it starts or the printhead closes. Kept verbatim. |
| `^ML` | Maximum label length | Passthrough | No | No | No | Tells the printer the longest label it may have to measure during calibration. Kept verbatim. |
| `^MM` | Print mode | Supported | Yes | Yes | n/a | Chooses what the printer does after printing: tear-off, peel, rewind, applicator, cutter, delayed cutter, RFID or kiosk. The reserved modes L and U are kept as raw ZPL with a warning. |
| `^MN` | Media tracking | Supported | Yes | Yes | n/a | Tells the printer how to find the start of each label: continuous, gap or mark. |
| `^MT` | Media type | Supported | Yes | Yes | n/a | Chooses thermal transfer (ribbon) or direct thermal media. |
| `^PF` | Slew dot rows | Passthrough | No | No | No | Feeds blank dot rows before the label prints. Kept verbatim. |
| `^PH / ~PH` | Slew to home | Passthrough | No | No | No | Feeds the media to the start of the next label. Kept verbatim. The ~ form does the same as an immediate command. |
| `^PP / ~PP` | Programmable pause | Passthrough | No | No | No | Pauses the printer after the label so the operator has to confirm. Kept verbatim. The ~ form pauses at once. |
| `^PR` | Print rate | Supported | Yes | Yes | n/a | Sets the print, slew and backfeed speeds. Speeds are written as inches per second (1 to 14); the letters A to E are read as 2, 3, 4, 6 and 8. |
| `~PR` | Applicator reprint | Out of scope | No | No | No | Reprints the last label on an applicator. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |
| `~PS` | Resume printing | Out of scope | No | No | No | Restarts a printer that is paused. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~SD` | Set darkness | Passthrough | No | No | No | Sets the print darkness to an absolute level from 0 to 30. Kept verbatim. |
| `^SI` | Set sensor intensity | Out of scope | No | No | No | Sets how strongly the media sensors shine. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^SP` | Start print early | Passthrough | No | No | No | Starts printing while the rest of the label is still being formatted. Kept verbatim. |
| `^SS` | Set media sensors | Out of scope | No | No | No | Sets the values the media sensors use. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~TA` | Tear-off adjust | Passthrough | No | No | No | Moves the position where labels stop for tearing off. Kept verbatim. |
| `^XB` | Suppress backfeed | Passthrough | No | No | No | Stops the printer from backing the media up after cutting or peeling. Kept verbatim. |

### Printer configuration

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^CM` | Memory letter assignment | Out of scope | No | No | No | Changes which letters name the memory devices. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JA` | Cancel all | Out of scope | No | No | No | Cancels every format waiting in the printer. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `^JB` | Initialize flash memory | Out of scope | No | No | No | Erases a flash memory device. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JB` | Reset optional memory | Out of scope | No | No | No | Erases the optional memory card. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JF` | Battery condition | Out of scope | No | No | No | Sets the battery condition setting. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JH` | Early warning settings | Out of scope | No | No | No | Sets which early warnings the printer gives. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JI / ~JI` | Start ZBI program | Out of scope | No | No | No | Starts a Zebra Basic Interpreter program. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JJ` | Auxiliary port | Out of scope | No | No | No | Sets how the auxiliary port is used. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JL` | Set label length | Out of scope | No | No | No | Makes the printer measure the label length. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JN` | Head test fatal | Out of scope | No | No | No | Makes a failed printhead test stop the printer. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JO` | Head test non-fatal | Out of scope | No | No | No | Makes a failed printhead test only warn. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JP` | Pause and cancel format | Out of scope | No | No | No | Pauses the printer and cancels the current format. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~JQ` | Terminate ZBI | Out of scope | No | No | No | Stops a running Zebra Basic Interpreter program. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JR` | Power-on reset | Out of scope | No | No | No | Restarts the printer. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JT` | Printhead test interval | Out of scope | No | No | No | Sets how often the printhead is tested. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^JU` | Configuration update | Out of scope | No | No | No | Saves or restores the printer settings. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~JX` | Cancel partial format | Out of scope | No | No | No | Cancels a format that is only partly received. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~KB` | Kill battery | Out of scope | No | No | No | Switches the printer's battery off. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^KD` | Date and time format | Out of scope | No | No | No | Chooses how the printer shows the date and time. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^KL` | Define language | Out of scope | No | No | No | Sets the language of the printer's own display. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^KN` | Printer name | Out of scope | No | No | No | Sets the printer's name and description. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^KP` | Set password | Out of scope | No | No | No | Sets the printer's panel password. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^MA` | Maintenance alerts | Out of scope | No | No | No | Sets when the printer prints maintenance warnings. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^MC` | Map clear | Out of scope | No | No | No | Chooses whether the format memory is cleared after printing. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^MI` | Maintenance information message | Out of scope | No | No | No | Sets the text of maintenance messages. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^MP` | Mode protection | Out of scope | No | No | No | Locks printer modes against changes from the panel. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^MW` | Printhead cold warning | Out of scope | No | No | No | Switches the cold printhead warning on or off. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~PM` | Decommissioning mode | Out of scope | No | No | No | Wipes the printer's memory before it is retired. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `~RO` | Reset advanced counters | Out of scope | No | No | No | Resets the printer's usage counters. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^SC` | Serial communications | Out of scope | No | No | No | Sets the serial port speed and format. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^SE` | Encoding table | Out of scope | No | No | No | Chooses the encoding table the printer uses. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^SL` | Clock mode and language | Passthrough | No | No | No | Sets the format and language the printer uses for date and time codes in fields. Kept verbatim. |
| `^SO` | Clock offset | Passthrough | No | No | No | Sets an offset added to the real-time clock for date and time codes. Kept verbatim when imported. Rotula also writes it by itself for date variables that use the printer clock with an offset (see docs/variables.md); that copy is not read back into the model. |
| `^SR` | Printhead resistance | Out of scope | No | No | No | Sets the printhead resistance value. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^ST` | Set date and time | Out of scope | No | No | No | Sets the printer's real-time clock. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^SZ` | ZPL mode | Out of scope | No | No | No | Chooses between ZPL and ZPL II. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^XS` | Dynamic media calibration | Out of scope | No | No | No | Switches dynamic media calibration on or off. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |
| `^ZZ` | Printer sleep | Out of scope | No | No | No | Puts the printer to sleep after a time. *Out of scope: Printer administration: it changes how the device is set up, not what a label looks like.* |

### Host/status

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `~HB` | Battery status | Out of scope | No | No | No | Asks a mobile printer for its battery status. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HD` | Head diagnostic | Out of scope | No | No | No | Asks for printhead diagnostics. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HF` | Host format | Out of scope | No | No | No | Sends a stored format back to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HG` | Host graphic | Out of scope | No | No | No | Sends a stored graphic back to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HH` | Configuration label return | Out of scope | No | No | No | Sends the printer configuration back to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HI` | Host identification | Out of scope | No | No | No | Asks the printer to identify itself. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HM` | Host RAM status | Out of scope | No | No | No | Asks for the memory status. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HQ` | Host query | Out of scope | No | No | No | Asks for a named piece of printer information. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HS` | Host status | Out of scope | No | No | No | Asks for the printer status. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HT` | Host linked fonts list | Out of scope | No | No | No | Asks for the list of linked fonts. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~HU` | Alert configuration return | Out of scope | No | No | No | Sends the alert configuration back to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HV` | Host verification | Out of scope | No | No | No | Sends the data of a field back to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HW` | Host directory list | Out of scope | No | No | No | Asks for a directory listing of a storage device. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HY` | Upload graphics | Out of scope | No | No | No | Asks the printer to send stored graphics to the host. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^HZ` | Display description information | Out of scope | No | No | No | Asks the printer to describe itself in XML. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `^LF` | List font links | Out of scope | No | No | No | Prints or returns the list of linked fonts. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |
| `~WC` | Print configuration label | Out of scope | No | No | No | Prints the printer's configuration label. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `^WD` | Print directory label | Out of scope | No | No | No | Prints a directory listing of a storage device. *Out of scope: A diagnostic or immediate printer action that prints nothing on the label.* |
| `~WQ` | Write query | Out of scope | No | No | No | Asks for a wireless or network setting. *Out of scope: A query that makes the printer answer the host; a label file has no use for the reply.* |

### Network/wireless

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^KC` | Client identifier | Out of scope | No | No | No | Sets the DHCP client identifier. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NB` | Check for wired print server | Out of scope | No | No | No | Checks for a wired print server at start-up. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NC` | Select primary network device | Out of scope | No | No | No | Chooses between the wired and the wireless device. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `~NC` | Network connect | Out of scope | No | No | No | Connects the printer to the network. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^ND` | Change network settings | Out of scope | No | No | No | Changes the network settings. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NI` | Network ID | Out of scope | No | No | No | Sets the printer's network ID. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NN` | SNMP settings | Out of scope | No | No | No | Sets the SNMP name and contact. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NP` | Boot settings source | Out of scope | No | No | No | Chooses whether the printer or the print server holds the network settings. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `~NR` | Set all devices transparent | Out of scope | No | No | No | Switches every connected printer to transparent mode. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NS` | Wired network settings | Out of scope | No | No | No | Changes the wired network settings. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NT` | SMTP settings | Out of scope | No | No | No | Sets the mail server for alerts. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `~NT` | Set connected printer transparent | Out of scope | No | No | No | Switches the connected printer to transparent mode. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^NW` | Web authentication timeout | Out of scope | No | No | No | Sets how long a web login lasts. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^SQ` | Halt ZebraNet alert | Out of scope | No | No | No | Stops a ZebraNet alert. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^SX` | Set ZebraNet alert | Out of scope | No | No | No | Sets up ZebraNet alerts. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WA` | Antenna parameters | Out of scope | No | No | No | Sets the wireless antenna. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WE` | WEP mode | Out of scope | No | No | No | Sets the WEP encryption keys. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WL` | LEAP settings | Out of scope | No | No | No | Sets the LEAP login. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `~WL` | Print network configuration label | Out of scope | No | No | No | Prints the network configuration. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WP` | Wireless password | Out of scope | No | No | No | Sets the wireless password. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WR` | Wireless transmit rate | Out of scope | No | No | No | Sets the wireless transmit rates. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `~WR` | Reset wireless radio card | Out of scope | No | No | No | Resets the wireless radio. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WS` | Wireless radio card values | Out of scope | No | No | No | Sets the wireless radio values. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |
| `^WX` | Wireless security | Out of scope | No | No | No | Sets the wireless security type. *Out of scope: Network or wireless setup of the device; it belongs to printer or network tools.* |

### RFID

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^HL / ~HL` | Return RFID data log | Out of scope | No | No | No | Returns the RFID data log to the host. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^HR` | Calibrate RFID tag position | Out of scope | No | No | No | Finds the best position for writing a tag. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RB` | Define EPC data structure | Out of scope | No | No | No | Describes how EPC data is split into fields. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RF` | Read or write RFID tag | Out of scope | No | No | No | Reads or writes the data of an RFID tag. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RI` | Get tag ID | Out of scope | No | No | No | Reads the identifier of a tag. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RL` | Lock tag memory | Out of scope | No | No | No | Locks or permanently locks parts of a tag. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RM` | Enable motion | Out of scope | No | No | No | Lets the media move while RFID commands run. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RN` | Detect multiple tags | Out of scope | No | No | No | Detects more than one tag in the field. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RR` | RFID retries | Out of scope | No | No | No | Sets how often a tag operation is retried. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RS` | RFID setup | Out of scope | No | No | No | Sets up the RFID options of the printer. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RT` | Read tag | Out of scope | No | No | No | Reads a tag and returns the data. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RU` | Read unique chip serial | Out of scope | No | No | No | Reads the unique serial number of a chip. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `~RV` | Report encoding result | Out of scope | No | No | No | Returns the result of the last tag operation. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^RW` | Read and write power | Out of scope | No | No | No | Sets the RFID read and write power. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |
| `^WT` | Legacy RFID write | Out of scope | No | No | No | Writes tag data with the older command. *Out of scope: Reads or writes RFID tags and needs tag hardware; it is not part of a label design.* |

### Kiosk

| Command | Name | Status | Generate | Read | Preview | What it does |
|---|---|---|:---:|:---:|:---:|---|
| `^CN` | Cut now | Out of scope | No | No | No | Cuts the media at once. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |
| `^CP` | Eject or retract a page | Out of scope | No | No | No | Ejects or pulls back a page on a kiosk printer. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |
| `^KV` | Cutter and presenter settings | Out of scope | No | No | No | Sets cutter and presenter behaviour of kiosk printers. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |
| `~PL` | Present length addition | Out of scope | No | No | No | Adds to the length a kiosk printer presents. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |
| `^PN` | Presenter cycle | Out of scope | No | No | No | Controls the presenter of a kiosk printer. *Out of scope: Controls kiosk or presenter hardware; it is not part of the label artwork.* |

## Barcode previews

The designer draws barcodes module for module, so what you see scans. The accuracy of the printed ZPL does not depend on the
preview: the commands and their parameters are generated and read for every symbology in the Barcodes table.

| Preview | Commands |
|---|---|
| Exact | Aztec Code (zero spelling) (`^B0`), Code 11 (`^B1`), Interleaved 2 of 5 (`^B2`), Code 39 (`^B3`), PLANET Code (`^B5`), PDF417 (`^B7`), EAN-8 (`^B8`), UPC-E (`^B9`), Code 93 (`^BA`), Code 128 (`^BC`), EAN-13 (`^BE`), Standard 2 of 5 (`^BJ`), ANSI Codabar (`^BK`), LOGMARS (`^BL`), MSI (`^BM`), Aztec Code (`^BO`), Plessey (`^BP`), QR Code (`^BQ`), GS1 DataBar (`^BR`), UPC/EAN add-on (`^BS`), UPC-A (`^BU`), Data Matrix (`^BX`), POSTNET and PLANET (`^BZ`) |
| Close | Industrial 2 of 5 (`^BI`) |
| Box | Code 49 (`^B4`), Codablock (`^BB`), UPS MaxiCode (`^BD`), MicroPDF417 (`^BF`), TLC39 (`^BT`) |

## Fonts

ZPL's built-in bitmap fonts (`A` to `H`, `0`) live in the printer, not in Rotula. The preview draws text with a bundled stand-in
typeface (Liberation Sans or Mono), so letter shapes and line breaks can differ a little from the printed label. The font width is
honoured as a stretch factor in the preview, but the real glyph widths of the printer's bitmap fonts are not reproduced. Fonts
assigned with `^CW` are kept and written back; the preview still uses the stand-in typeface for them.

## Character sets

`^CI28` (UTF-8) is written by default. Opening a file that selects a national code page, or one that remaps characters, works but
gives a notice because the mapping is approximated. Text is decoded with the `^CI` in force at that point in the file.
