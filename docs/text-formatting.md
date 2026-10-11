# Text formatting: printer fonts and Windows fonts

A text can use one of two kinds of font.

| | Printer font | Windows font |
|---|---|---|
| Examples | the built-in ZPL fonts `A` to `Z`, `0` to `9`; a font stored on the printer (`E:ARIAL.TTF`, printed with `^A@`) | Arial, Calibri, any TrueType font on the computer |
| What the label carries | real text (`^FD`) | a picture of the text (`^GF`) drawn at the printer's resolution when the label is printed |
| Bold, italic, underline | not available (the label warns instead of dropping them) | yes |
| Size | dots (`FontHeight`, `FontWidth`) | points |

## For label designers

* **Windows fonts look the same everywhere** because the printer only receives dots. The price is a larger job (a picture instead of a few bytes)
  and text that the printer cannot count, take from a stored field, or give a clock to. Those field options are refused with a message.
* **Data fields work**: `{{description}}` is replaced by the record's value first, and the picture is drawn from the finished text, once for every label.
* **Word wrap** (a block) gives the text a width, a maximum number of lines, line spacing, a hanging indent and left / centre / right / justified
  alignment. Switching it on sizes the block from the label: the width up to the right edge and as many lines as fit below the text.
* **Overflow warning**: a Windows-font text is measured exactly, so the warning is reliable. For a printer font Rotula can only estimate (the
  printer does not say how wide its letters are), so that warning says "may not fit". What the printer does with text beyond the maximum number of lines of a `^FB` block (cut it off, or print it over the last line) has not been checked on a printer here, so treat the warning as important.
  describes the extra text as overprinting the last line; this has not been confirmed on a printer here, so treat the warning as important.
* A Windows font that is not installed on the computer is replaced by another one; the label warns (`Text.Font.Missing`).
* Printed on another computer a Windows-font label looks the same only if that computer has the font; the picture is made at print time.
* Importing the ZPL of a label that used a Windows font gives back a picture (an image), not text: the letters are not in the file any more.

## For developers

All of this is in `Rotula.Core.Text` (no drawing code) plus `Rotula.Rendering.SkiaTextRasterizer`.

```csharp
var font = FontChoice.ForWindows("Arial", pointSize: 12, bold: true);
font.ApplyTo(textElement);                       // Windows font -> TextElement.Font; a printer font -> FontId (the element is saved as it always was)
var choice = FontChoice.FromElement(textElement); // either kind as one value, for the font box
double dots = FontChoice.PointsToDots(12, 203);   // one size, two display units

ITextRasterizer rasterizer = new SkiaTextRasterizer();
var zpl = ZplGenerator.Generate(doc, data, rasterizer);               // also GenerateWithVariables(doc, ctx, quantity, rasterizer)
var m = rasterizer.Measure(new TextRasterRequest("text", font, 203, boxWidth, 0, maxLines));  // lines, size, m.Overflow, m.FontSubstituted
```

* Without a rasterizer a Windows-font text is an error (`Text.Raster.NoRasterizer`) and is not printed; there is never a silent substitute.
* `MergeOptions.TextRasterizer`, `PrintVariableOptions.TextRasterizer` and `IVariableServices.TextRasterizer` carry it to batches and print commands.
* The preview draws with the same layout code (`RenderText.FamilyName`, `Italic`, `Underline`, `HangingIndentDots`), so line breaks agree.
* Size: a Windows font's size is `FontChoice.PointSize` (physical, never rescaled by the resolution converter); a printer font's size is
  `FontHeight` in dots. Only one is authoritative for each kind, so they cannot drift.
* File format version 8 stores `font` and `storedFontPath` on a text element; other labels keep their old version.
