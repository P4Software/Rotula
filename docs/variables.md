# Variables: dates, serial numbers, questions and computed values

A **variable** is a name you put in a text or barcode as `{{name}}`. Rotula replaces it with a value when the label is printed. Until
now a variable could only be filled from a data file (or show its sample value). Variables can now also work out their own value:

| Kind | What it gives you | Example |
|---|---|---|
| **Text** | A value from your data file, or its sample value. This is how variables always worked, and old labels open unchanged. | `{{customer}}` |
| **Date** | Today's date, or a fixed date, moved by an offset and written in the format you choose. | `expiry` = today + 30 days, written as `YYMMDD` |
| **Counter** | A serial number that goes up by one for each label. It can remember the last number between sessions. | `SN000124` |
| **Prompt** | A question the operator answers just before printing. | "Which shift?" |
| **Computed** | A value built from other variables with a short formula. | `lot` = `'L' & julian & '-' & counter` |

Everything on this page works offline. Nothing is sent anywhere.

* [For label designers](#for-label-designers)
* [For developers](#for-developers)

---

## For label designers

### Examples to copy

**Expiry date = today + 30 days, printed as YYMMDD** (the format used in GS1 barcodes):

* Variable `expiry`, kind **Date**, source *computer clock*, offsets `+30 days`, format `gs1`.
* In the label write `EXP {{expiry}}`. Printed on 7 March 2026 it shows `EXP 260406`.

**Lot number = L + day of the year + a serial number**:

* Variable `julian`, kind **Date**, format `julian` (the day of the year, `001` to `366`).
* Variable `counter`, kind **Counter**, padding 4.
* Variable `lot`, kind **Computed**, formula `'L' & julian & '-' & counter`.
* In the label write `{{lot}}`. On 7 March 2026 the first label shows `L066-0001`.

(You can also write `L{{julian}}-{{counter}}` straight in the text; both ways give the same result.)

**Best before the end of next month**: kind Date, offset `+1 month`, rounding *end of month*, format `dd/MM/yyyy`.

**Five business days from now**: kind Date, offset `+5 business days`. Weekends are skipped, and so are the holidays you list.

### Date variables

A date variable has these settings:

* **Source**
  * *Computer clock*: Rotula works the date out when it creates the label and the printer receives plain text. This works with every
    printer and every format.
  * *Printer clock*: the printer prints its own date and time. See [the printer clock](#printing-the-date-with-the-printers-own-clock).
  * *Fixed date*: a date you type in (offsets still apply).
* **Offsets**: any number of steps, applied in order. Units: seconds, minutes, hours, days, **business days**, weeks, months, years.
  Negative numbers go back in time.
* **Rounding**: after the offsets, snap to the start or end of the day, month or year.
* **Format**: a preset or your own pattern (below).
* **Culture**: the language of month and day names, for example `es-ES`. Empty means English.
* **UTC**: use universal time instead of the computer's time zone.

How the arithmetic behaves, so there are no surprises:

* **Months and years stop at the end of the month.** 31 January + 1 month is 28 February (29 in a leap year). 29 February + 1 year is 28 February.
* **Days, weeks, months and years keep the time of day** even when the clocks change. **Hours, minutes and seconds count real elapsed time**,
  so "+24 hours" across the spring clock change shows a time one hour later on the clock, while "+1 day" does not.
* A clock time that does not exist (the hour skipped in spring) moves one hour later. A time that happens twice (autumn) uses the first one.
* Business days are Monday to Friday. "+0 business days" leaves the date as it is, even on a weekend.
* Steps are applied in the order you list them: `+1 month, +30 days` and `+30 days, +1 month` can give different dates.
* Dates beyond year 9999 are refused with a message instead of printing nonsense.
* Years always use the Gregorian calendar, even for cultures that normally use another one (so `th-TH` still prints 2026, not 2569).

#### Formats

Use a preset name, or any .NET custom pattern. A pattern of one letter means that letter alone (`d` is the day number, not the "short date").

| Preset | Example for 7 March 2026, 14:05:09 | What it is |
|---|---|---|
| `iso` | `2026-03-07` | ISO 8601 date |
| `isoDateTime` | `2026-03-07T14:05:09` | ISO 8601 date and time |
| `compact` | `20260307` | Year, month, day, no separators |
| `gs1` | `260307` | GS1 date `YYMMDD` (application identifiers 11, 13, 15, 17) |
| `julian` | `066` | Day of the year, three digits |
| `julianYear` | `26066` | Two-digit year and day of the year (`YYDDD`) |
| `week` | `10` | ISO 8601 week number (week 1 holds the first Thursday) |
| `isoWeek` | `2026-W10` | ISO week with its week year (30 December 2024 is `2025-W01`) |
| `weekSunday` | `09` | Week number, weeks start on Sunday (the printer's `%U`) |
| `weekMonday` | `09` | Week number, weeks start on Monday (the printer's `%W`) |
| `monthDayYear` | `Mar 07 2026` | Month name, day, year (names follow the culture) |
| `expiry` | `03/2026` | Month and year, as used for expiry dates |
| `dmy` / `mdy` | `07/03/2026` / `03/07/2026` | Day/month/year and month/day/year |
| `time` / `timeSeconds` | `14:05` / `14:05:09` | 24-hour clock |

Useful pattern letters: `yyyy` year, `yy` two-digit year, `MM` month number, `MMM` short month name, `MMMM` month name, `dd` day, `ddd` short
weekday, `dddd` weekday, `HH` hour 00-23, `hh` hour 01-12, `mm` minutes, `ss` seconds, `tt` AM/PM. Put fixed text in quotes: `'EXP 'MM/yyyy`.

#### Printing the date with the printer's own clock

Some printers have a real-time clock. With the *printer clock* source the label does not carry a date at all: it carries *clock codes*, and the
printer fills in its own date when it prints each label. That is useful for a long print run that crosses midnight, or when the printer's clock
is the authority. Rotula writes the ZPL commands `^FC` (which character starts a clock code) and, for offsets, `^SO`.

The clock codes the printer understands (from the ZPL II Programming Guide). Names are printed in the printer's language.

| Code | Meaning | Code | Meaning |
|---|---|---|---|
| `%a` | weekday, short | `%A` | weekday name |
| `%b` | month, short | `%B` | month name |
| `%d` | day of the month 01-31 | `%j` | day of the year 001-366 |
| `%m` | month 01-12 | `%y` | year 00-99 |
| `%Y` | year 0000-9999 | `%H` | hour 00-23 |
| `%I` | hour 01-12 | `%M` | minute 00-59 |
| `%S` | second 00-59 | `%p` | AM or PM |
| `%U` | week of the year 00-53, Sunday first | `%W` | week of the year 00-53, Monday first |
| `%w` | weekday number 0-6, Sunday is 0 | | |

Offsets: the printer can hold the current time plus up to **two different offsets** in the same label (its "secondary" and "third" clock, set
with `^SO` as months, days, years, hours, minutes and seconds). Weeks are sent as seven days each.

> **Not yet confirmed on a printer with a clock.** The code table above and the order of the `^SO` parameters (clock set, months, days, years,
> hours, minutes, seconds) were written from the ZPL II Programming Guide by memory and are covered by tests that compare Rotula with itself, not with
> a real printer. Before relying on a printer-clock date for production labels, print a test label and check the date and the offset. If a printer
> disagrees, tell us: a wrong parameter order would shift the date by months.

**What the printer cannot do.** In these cases Rotula uses the *computer clock* for that variable instead and shows a warning, so you are never
left with an empty or wrong date:

| Reason | Warning key |
|---|---|
| Business days (a printer does not know your holidays) | `Variable.Date.Printer.BusinessDays` |
| Rounding to the start or end of a day, month or year | `Variable.Date.Printer.Rounding` |
| UTC time | `Variable.Date.Printer.Utc` |
| A format part with no printer code (unpadded numbers such as `d`, fractions of a second, time zone names, the ISO week) | `Variable.Date.Printer.Format` |
| Month, day or AM/PM names in a language other than English | `Variable.Date.Printer.Culture` |
| A third different offset on the same label | `Variable.Date.Printer.SlotsFull` |
| An offset beyond 32000 of a unit | `Variable.Date.Offset.Range` |
| The date is in a barcode (a barcode's data is checked on the computer) | `Variable.Date.Printer.Barcode` |
| A field that already has its own clock characters with none free for this variable | `Variable.Date.Printer.NoIndicator` |
| No clock character is free (every candidate appears in the text) | `Variable.Date.Printer.NoFreeIndicator` |

Things to know:

* Rotula picks a clock character (`%` if possible) that does not appear anywhere else in the field's text, because the printer would treat it as a
  code. If you set the clock characters yourself on the field, those are used.
* When the printer adds several offsets together (for example `+1 month` and `+30 days`) it does so in its own order, which can differ at the end
  of a month. Use a single unit if the exact day matters.
* The preview and the on-screen text show the date as the *computer* would write it; only the printer knows its own clock.
* A label that was exported with printer-clock codes and imported again shows the codes as typed text, because the date is not known until the
  printer prints. (`^SO` is kept as raw ZPL on import.)

### Counters

A counter gives each label the next serial number. The rules are the same as the merge counters (numeric or letters-and-digits, step, zero padding,
prefix and suffix, wrap around or stop at the end).

* Without **Remember**, the counter starts at its first value every time you print. This is the default.
* With **Remember**, Rotula keeps the last number used and the next print continues from there, even after you close the program. The numbers
  are stored per label and per counter name in `%AppData%\Rotula\counters.json`. The file is written safely (a crash cannot leave half a file) and
  can be shared by several Rotula windows at the same time.
* A remembering counter needs the label to have an identity. Rotula gives the label one when you switch **Remember** on and saves it in the
  `.rotula` file. If you "Save as" a copy under a new name and want it to count on its own, use *Start a separate series*; otherwise the copy shares
  the numbers with the original (which is usually what you want for the same product).

**Numbers are never used twice.** This is the exact rule:

1. When printing starts, Rotula **reserves** one number per label and writes that to the file *before* anything is sent to the printer.
2. When the printing is done it **confirms** how many labels were really sent. Reserved numbers that were not used are given back, as long as nobody
   reserved numbers after you. If someone did, the unused ones are simply skipped (a gap).
3. If printing fails before a single label leaves the program, nothing is burned: the numbers go back. If part of a run was sent, the labels that
   were sent are counted as used. If you prefer to burn the whole run on failure, that is a choice the print command makes (confirm all).
4. If the program or the computer **crashes** between the two steps, those numbers stay reserved. The next time you print, Rotula **skips** them and
   tells you which ones (`Variable.Counter.Unconfirmed`). If you know nothing was printed you can give them back with *Reclaim*.
5. If you **edit the counter** (start, step, kind or padding) after numbers were used, Rotula does not guess. Printing is blocked with
   `Variable.Counter.SettingsChanged` until you either *accept* (keep counting from the remembered position under the new rules) or *reset*.
6. **Reset** puts the counter back to its start, or to a number you choose ("the next label must be 5000"). It is always an explicit action.
7. A damaged `counters.json` is **never** treated as empty (that would reuse numbers). Printing with remembering counters is blocked with
   `Variable.Counter.Store.Corrupt` until you choose *Recover*, which keeps the damaged file next to the original (so you can read the last numbers)
   and starts a new one. The previous version is also kept as `counters.json.bak`.

**See the next number before printing.** The preview shows the number the next label will get without using it.

In a **data merge** each record gets the next number; a record that is skipped because of an error leaves a gap (the number is not reused). A
record counts as used from the moment its label is handed to the printer, which is the safe side.

### Prompts (questions at print time)

A prompt variable asks the operator for a value just before printing. Settings:

* **Question** and **help text** shown next to the field.
* **Type**: text, number, date (`yyyy-MM-dd` or `yyyyMMdd`, written to the label with a format you choose) or a choice from a list.
* **Default**, **required**, **upper case**, minimum and maximum length, and an optional **pattern** (a regular expression the whole answer must match).
* **Remember last value**: offer the previous answer next time.
* If your data file already has a column with the same name, the data wins and the question is not asked. Switch on *override* to ask anyway and
  let the answer win.

What happens to an answer, in order: line breaks and tabs become a space and other control characters are removed; the ends are trimmed; upper case
if asked; then required, length, pattern and finally the type check (a number is digits with an optional minus sign and decimal point; a choice must
match an entry ignoring case). Patterns run on a safe engine with a time limit, so a bad pattern cannot freeze the program; features that engine
does not support (look-around, back-references) are reported as an invalid pattern.

### Computed variables

A computed variable is a short formula over other variables. It can use data columns, sample values, prompts, counters, dates and other computed
variables, in any order (a circle of variables that depend on each other is reported, not followed).

```
upper(left(sku, 3)) & '-' & padleft(qty, 4, '0')          ->  ABC-0007
join(' ', first, last)                                     ->  Ana Lopez   (empty parts are left out)
ifempty(note, 'no note')                                   ->  no note
replace(phone, '-', '')                                    ->  5551234
number(price, '0000.00')                                   ->  0012.50
date(manufactured, 'yyMMdd')                               ->  260307
'L' & julian & '-' & counter                               ->  L066-0001
```

Text goes in single or double quotes (write the quote twice, or use `\'`, `\"`, `\\`, `\n`, `\t`). `&` joins. Names are letters, digits and
underscore; `#Name` reads a merge counter column. A name that nothing supplies counts as empty and raises a warning (like a missing `{{placeholder}}`).

| Function | What it does |
|---|---|
| `concat(a, b, ...)` | Joins the texts (same as `a & b`). |
| `join(separator, a, b, ...)` | Joins with a separator, leaving out empty parts. |
| `upper(t)`, `lower(t)`, `trim(t)` | Upper case, lower case, remove spaces at both ends. |
| `length(t)` | Number of characters. |
| `left(t, n)`, `right(t, n)` | First or last `n` characters. |
| `substring(t, start, [length])` | Part of the text; `start` counts from 0. |
| `padleft(t, width, [char])`, `padright(...)` | Pad to a width with the character (default space). |
| `replace(t, find, replacement)` | Replace every occurrence (case sensitive). `find` cannot be empty. |
| `ifempty(t, fallback)` | `t`, or the fallback when `t` is empty. |
| `coalesce(a, b, ...)` | The first one that is not empty. |
| `number(value, format, [culture])` | Write a number with a .NET numeric format (`0000.00`, `N2`). The value is read with a dot as decimal point. |
| `date(value, format, [culture])` | Rewrite a date given as `yyyy-MM-dd` or `yyyyMMdd` with a preset or pattern. |

It is a formula, not a program. There are no loops, no assignments, no access to files, the network or other programs. Hard limits keep a hostile
file harmless: at most 1000 characters, 16 levels of nesting, 200 parts, and no result longer than 10000 characters (checked before it is built).
Mistakes come back with a message key and the character position (`Variable.Expr.*`, listed below).

### Which value wins

For each label Rotula works in this order, later steps seeing the earlier ones:

1. the **data record** (the merge columns);
2. **plain variables**: their sample value, only where the data has nothing;
3. **prompts**: the answer (or the default while previewing). The data wins if it has a value, unless the prompt is set to override;
4. **counters**;
5. **dates**;
6. **computed** variables, last, because they may use anything above.

A counter, date or computed variable with the same name as a data column **replaces** the column and shows a warning
(`Variable.Record.Overridden`). A plain variable never replaces data.

### In the program

The Print dialog and the data merge window do all of this for you. Before printing they ask the label's questions (one dialog, checked against the
label's rules); then every label gets its own date, serial number and computed values. A serial number is confirmed only for labels that really
reached the printer: a send that failed without moving a byte gives its numbers back, a send that moved bytes keeps them (the label may have
printed), and **Check all** never uses a number. **Save as ZPL** and **Export** count their numbers as used, because the file will be printed.
A label with a counter and several copies is written as separate labels, so each copy has its own number.

### In a batch (data merge)

* Every record gets its own counter value and its own computed values.
* **Dates are read once when the batch starts** by default, so a long run that crosses midnight shows one date on every label. Choose
  "per label" to read the clock for each label.
* Answers to prompts are asked once and used for every record.
* Variable warnings (for example a printer-clock fallback) are reported once, not once per record.
* A placeholder that the label defines itself is not reported as "no matching column", and a column used only inside a formula is not reported as unused.

### Files

The `.rotula` file stores the definitions. A label that only uses plain text variables is saved exactly as before (and old copies of Rotula can
still open it). A label with a definition, or with a label id, is saved as **format version 7**; an older Rotula says the file is newer instead of
silently dropping the definition. Files are untrusted input: lists are capped, text is clipped, unknown kinds open as plain variables.

### Using it from an AI assistant (MCP)

`set_variable` takes a `kind` (`text`, `date`, `counter`, `prompt`, `computed`) and a `definition` object. See [mcp.md](mcp.md).

---

## For developers

All of this lives in `Rotula.Core` (namespace `Rotula.Core.Variables`) with no UI dependency, plus `Rotula.Files` (the file and the counter file).

### The model

```csharp
var doc = new LabelDocument();
doc.Variables.Add(new LabelVariable("expiry")
{
    Kind = VariableKind.Date,
    Date = new DateVariableOptions { Format = "gs1", Offsets = { new DateOffset(30, DateUnit.Days) } },   // Offsets is get-only: use Add
});
```

`LabelVariable` keeps `Name` and `SampleValue`; `Kind` defaults to `Text`. Only the options object that matches `Kind` is used. **Copy variables with
`LabelVariable.Clone()`**, not `new LabelVariable(name, sample)`, or the definition is lost. `LabelDocument.Id` (null by default) identifies the
label for remembered counters; `EnsureId()` creates it, `NewId()` starts a separate series.

| Type | Settings |
|---|---|
| `DateVariableOptions` | `Source`, `FixedValue`, `Offsets` (list of `DateOffset(Amount, Unit)`), `Format`, `Culture`, `Utc`, `Rounding`, `Holidays` |
| `CounterVariableOptions` | `Kind`, `Start`, `Step`, `Padding`, `Prefix`, `Suffix`, `Overflow`, `Remember` (wraps `SerialCounter`) |
| `PromptVariableOptions` | `Label`, `InputType`, `Default`, `Required`, `Pattern`, `MinLength`, `MaxLength`, `Choices`, `Uppercase`, `RememberLast`, `HelpText`, `OverrideRecord`, `DateFormat` |
| `ComputedVariableOptions` | `Expression` |

Limits are in `VariableLimits`. `VariableEngine.ValidateDefinitions(doc)` checks a design without running it. `VariableEngine.IsValidName(name)` checks names.

### Resolving values

```csharp
var context = new VariableContext
{
    Time = TimeProvider.System,                 // tests pass a fake provider; the zone comes from Time.LocalTimeZone
    Record = row,                                // raw data columns (or null)
    PromptAnswers = answers,                     // from PromptSet.Validate(...).Values
    RequirePromptAnswers = true,                 // when printing: a missing answer is an error
    CounterStore = FileCounterStore.OpenDefault(),
    ItemIndex = 0,
};
VariableResolution r = VariableEngine.Resolve(doc, context);   // r.Values, r.Issues, r.PrinterBindings, r.ClockOffsets
ZplGenerationResult zpl = ZplGenerator.GenerateWithVariables(doc, context);
```

* `VariableEngine.BuildPreviewData(doc, context)` returns the dictionary to give the preview renderer or the old `ZplGenerator.Generate(doc, data)`.
* `ZplGenerator.Generate(doc, data)` is unchanged: `data` holds final values, new kinds not in `data` show their sample value, and **nothing reads the
  clock**. Use `GenerateWithVariables` to get dates, counters, computed values and printer-clock codes.
* `ZplGenerator` and the engine never read the clock, a file or the environment: time comes only from `VariableContext.Time` / `Now`. The same
  label and context give byte-identical ZPL.
* Printer-clock dates are written as `^FC` codes (and `^SO` lines before the fields) only by `GenerateWithVariables`, only in text fields. The
  decision logic is in `PrinterClock.TryPlan` and its XML documentation holds the code table.

### Printing with remembered counters

```csharp
var job = VariableJob.Begin(doc, context, itemCount: labels);       // reserves the numbers and writes them to counters.json
if (!job.CanPrint) { foreach (var i in job.Issues) Show(i); job.Cancel(); return; }
try
{
    for (int i = 0; i < labels; i++)
    {
        var result = ZplGenerator.GenerateWithVariables(doc, job.ContextFor(i, record), quantity: 1);   // quantity 1: every label differs
        Send(result.Zpl);
        sent++;
    }
}
finally { job.Complete(sent); }    // confirms; unused numbers go back if nobody reserved after you. Cancel() = Complete(0).
```

`MergeBatch.Labels(...)` does this by itself (see `MergeOptions.CounterStore`, `PromptAnswers`, `Time`, `DateEvaluation`). It confirms in a `finally`, so
stopping the enumeration early counts the labels already handed out. **That means a label counts as used when it is generated, not when it is sent**:
if the caller builds the whole batch and then fails to send it, those numbers are burned (skipped, never reused). To confirm only what was really
sent, own the job:

```csharp
var job = VariableJob.Begin(doc, set.CreateBatchContext(), set.RecordCount);
set.Options.Job = job;                                   // the batch now reserves and confirms nothing by itself
if (!job.CanPrint) { Show(job.Issues); job.Cancel(); return; }
int lastSent = 0;
try { foreach (var label in MergeBatch.Labels(doc, set)) { Send(label.Zpl); lastSent = label.RecordNumber; } }
finally { job.Complete(lastSent); }                      // 0 gives everything back; the record number of the last sent label confirms through it
```

A counter that remembers its value but has no `CounterStore` in the context cannot work: a preview shows its first value with the warning
`Variable.Counter.NoStore`, and `VariableJob.Begin` (so also `MergeBatch`) refuses with the same key as an error. It never silently starts again.

`CounterAdmin` has the operator actions: `Peek` (next value, last value, unconfirmed ranges, settings-changed flag), `Reset`, `ResetToNumber`,
`AcceptSettingsChange`, `Reclaim`. `ICounterStore` has `MemoryCounterStore` (Core) and `FileCounterStore` (Files). The file store takes an exclusive
lock on `counters.json.lock` for every operation, writes `counters.json.tmp`, flushes it to disk and swaps it in with `File.Replace` (keeping
`counters.json.bak`). The lock is an OS file handle, so a crashed process cannot leave a stale lock. `FileCounterStore.RecoverFromCorruption()`
implements rule 7. Errors are `CounterStoreException(MessageKey, Args)`.

The stored record per `labelId/counterName` is `{ next, lastUsedIndex, lastValue, fingerprint, pending[], updated }`. `next` is a position in the
counter's sequence, not a value, and only moves forward except when the newest reservation hands its unused tail back or `Reset` is called.

### Prompts

```csharp
var set = PromptSet.Build(doc, record, memory);        // what to ask (skips prompts the record already answers)
foreach (var item in set.Items) { /* name, label, type, initial value, required, help, choices */ }
PromptValidation v = set.Validate(typedAnswers);       // v.Issues (keys + name), v.Values (normalized)
if (v.IsValid) { set.Remember(v.Values, memory); context.PromptAnswers = v.Values; }
```

### Message keys

The UI looks each key up as the resource `Issue.<key>` and formats it with the arguments (all culture-neutral strings). `VariableMessageKeys.All`
lists them; a test checks that every key has text in both languages.

| Key | Arguments |
|---|---|
| `Variable.Name.Invalid` | name |
| `Variable.Name.Duplicate` | name |
| `Variable.Record.Overridden` | name, kind |
| `Variable.Date.Format.Invalid` | variable, format |
| `Variable.Date.Culture.Unknown` | variable, culture |
| `Variable.Date.Offset.Range` | variable, limit |
| `Variable.Date.OutOfRange` | variable |
| `Variable.Date.Fixed.Missing` | variable |
| `Variable.Date.Printer.BusinessDays` / `.Rounding` / `.Utc` / `.Fixed` / `.Barcode` / `.SlotsFull` / `.NoIndicator` / `.NoFreeIndicator` | variable |
| `Variable.Date.Printer.Format` | variable, the part of the format |
| `Variable.Date.Printer.Culture` | variable, culture |
| `Variable.Counter.Invalid` | variable, reason key (`Merge.Counter.*`) |
| `Variable.Counter.Overflow` | variable |
| `Variable.Counter.SettingsChanged` | variable, last value |
| `Variable.Counter.Unconfirmed` | variable, first position, last position |
| `Variable.Counter.Store.Corrupt` | file path |
| `Variable.Counter.Store.NewerVersion` | file path, version |
| `Variable.Counter.Store.Busy` | file path |
| `Variable.Counter.NeedsLabelId` | variable |
| `Variable.Counter.NoStore` | variable |
| `Variable.Prompt.Required` | variable |
| `Variable.Prompt.TooShort` / `.TooLong` | variable, limit |
| `Variable.Prompt.Pattern` / `.Pattern.Invalid` / `.NotNumber` / `.NotDate` / `.NotChoice` / `.NoChoices` / `.Missing` | variable |
| `Variable.Expr.Empty` | variable, position (0) |
| `Variable.Expr.TooLong` / `.TooComplex` | variable, position (0 or where), limit |
| `Variable.Expr.TooDeep` | variable, position, limit |
| `Variable.Expr.UnexpectedChar` / `.UnexpectedToken` / `.BadNumber` | variable, position, what was found |
| `Variable.Expr.UnexpectedEnd` / `.UnterminatedString` | variable, position |
| `Variable.Expr.UnknownFunction` | variable, position, name |
| `Variable.Expr.ArgumentCount` | variable, position, name, fewest, most |
| `Variable.Expr.BadArgument` | variable, position, function, argument number |
| `Variable.Expr.UnknownVariable` | variable, position, name (a warning) |
| `Variable.Expr.Cycle` | variable, position (0), chain `a > b > a` |
| `Variable.Expr.ResultTooLong` | variable, position, limit |
| `Variable.Expr.NotNumber` / `.NotDate` / `.BadFormat` | variable, position, function, value |

### File format version 7

`label.id` (string, 1-64 letters, digits, `-`, `_`) and, per variable, `kind` plus one of `date`, `counter`, `prompt`, `computed` (see
`RotulaFileDtos.cs`). Written only when used; the file is then stamped 7. Reading caps: 500 variables, 16 offsets, 400 holidays, 200 choices, name 64,
values 10000, expression 1000, pattern 200, prompt texts 500, counter affixes 32. Unknown kinds and unusable options fall back to a plain variable.
`FilesCatalogTests` checks by reflection that every settable property of every definition type reaches the DTOs.

### Not done yet / known limits

* Printer-clock dates are text-field only; barcodes use the computer clock.
* Counters count per label of a run, not per copy of one label: for different numbers on each copy, generate the copies as separate labels (`quantity: 1`).
* Prompt answers are asked once per run, not once per record.
* The `^SO` command is written by the generator for printer-clock offsets but is not part of the document model, so it is not read back on import.
