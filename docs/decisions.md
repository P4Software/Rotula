# Design decisions

## App (Rotula.App)

### Language switch: live update
The in-app language switch (View > Language: Same as Windows / English / Espanol) updates open windows immediately; no restart.
All XAML text is bound to the indexer of `Loc` (`{loc:T Key}`), which raises `Item[]` on a change. View-models that cache
translated text (property panel rows, toolbox, layers, status text) rebuild on `Loc.LanguageChanged`. The default follows the
Windows display language (Spanish if it is Spanish, English otherwise). The choice is stored in `%AppData%\Rotula\settings.json`.
Only `Strings.en.resx` and `Strings.es.resx` exist (satellite assemblies, no neutral resources), so `Loc` always asks for "en"
or "es" explicitly and falls back to English per key. `NeutralResourcesLanguage(..., Satellite)` must NOT be used: it also
redirects WPF's own XAML lookup and the application fails to start.

### Core messages
Core only returns `ValidationIssue.MessageKey` + args. The UI looks up `Issue.<MessageKey>`; args that are keywords
(`odd`/`even`, counter reasons) are looked up as `Issue.Arg.<arg>`. A test scans Core for every emitted key (including the ZPL
importer's `Import.*` and merge `Merge.*` keys) and fails when a translation is missing.

### Resize and rotate follow the printer model
ZPL has no free scaling or free rotation, so: rotate = 90 degree steps (rotate handle click/drag-to-quadrant, Ctrl+R);
resize changes what the element type can express: box/image width+height, line length, circle diameter, barcode bar height
(module size for QR/Data Matrix), text font height (block width for wrapped text). The edge opposite the handle stays put.
Resize and rotate are only offered for a single selected element; multi-selection supports move, align, distribute, z-order.

### Element bounds
Rotula.Rendering exposes no measurements. Box/line/circle/image bounds come from the model; text and barcodes are measured by
drawing the element alone with the real renderer and scanning for ink (cached until the element's size-relevant content
changes). Request to Rendering: a public `Measure(RenderElement)` returning the layout size would replace the probe.

### Undo/redo
Memento command: each edit stores the whole document before/after (documents are small; image pixels are shared). Unlimited.
Arrow nudges and typing in one property merge into one step within one second. Drags are gestures: one undo step per drag.

### Preview
`PreviewCache` keeps the `RenderLabel` per document revision and the bitmap per (revision, scale). Selection, handles, grid and
guides are drawn by WPF on top, so pointer movement never re-renders the label. A visible notice says the text preview is
approximate.

### .rotula file
JSON, `formatVersion` first (currently 1), polymorphic elements with a `type` discriminator, ids preserved, images as base64
luminance. A DTO layer (not Core types) so the file shape is stable. Newer files are refused with a message asking to update.
Saved through a temp file so a failed save never destroys the previous version.

### Nudge
Arrow = 1 dot, Shift+arrow = 8 dots (about 1 mm at 203 DPI).

### Tests
`tests/Rotula.Tests` targets `net10.0-windows` (it references the WPF app). All App tests share the xUnit collection "App"
because the language is process-wide state.
