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

### Visual language (polish pass)
Warm off-white chrome, white surfaces, one burnt-orange accent, spacing scale 4/8/12/16/24, radius 6 (controls) and 8 (cards, popups).
Everything shared lives in `Resources/Theme.xaml`: one field look (40 px, constant 1 px border, focus and error rings drawn as an overlay so
the text never shifts), thin scrollbars without arrows (the dark ZPL strip overrides only the thumb brushes), styled DataGrid, tabs,
tooltips (warm card, wrapped at 340 px, 450 ms delay set once in `App.xaml.cs`), context menus and splitters (wide grab area, hairline).
Secondary windows derive from `Views.DialogWindow`: the same custom title bar as the main window (WindowChrome), size clamped to the work
area, centred on the main window. Button hierarchy: solid accent for the one primary action, outlined for everything else, icon buttons
with tooltips for tool actions.
- **Field errors**: property rows keep their own `HasError`/`Error`, so the red border comes from the attached `FieldState.HasError`.
- **Right column**: Properties 55 % / Objects 45 % by default, both with minimum heights; the splitter position is stored as a share
  (`propertiesPanelShare` in settings.json), so it survives resizing and other DPI settings.
- **Status bar** is a Grid (a `StatusBar` panel cannot ellipsise): message and notice shrink with an ellipsis, counts, problems, pointer
  and label size stay readable. The long "preview is approximate" text is the tooltip of the short notice.
- **Label size picker** (toolbar): a ContextMenu built on click (keyboard accessible by default): common sizes (the current one ticked),
  "Custom size..." (shows the label settings and focuses the width box) and the print resolution. One undo step per change.
- **Narrow windows / scaling**: WPF scales uniformly, so a layout at N % on a W x H screen equals the 100 % layout at W/N x H/N. The
  toolbar drops its text captions below 1460 DIP, the "Preview" word below 1200 and the printer box below 1060; the window minimum is
  960 x 560 DIP (a 1080p screen at 200 %) and every window is clamped to the work area.
- **Bounds while a record is previewed**: text and barcode frames are now measured with the shown record's values (the cache key includes
  them); Rotula.Rendering still has no public `Measure`, so the probe render remains.

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

## Release packaging

### Portable build: self-contained single file
`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
-p:EnableCompressionInSingleFile=true -p:DebugType=none` produces one `Rotula.App.exe` (about 68 MB) that needs no installed
.NET. Verified by publishing locally and launching the exe: the window opens and stays up, so the WPF resources, the
satellite resource assemblies and the SkiaSharp native library all load from the single file. The native library is unpacked
to a temporary folder on first start, which is why the first launch can take a second or two longer.

### Installer: Inno Setup, not MSIX
An MSIX package must be signed with a certificate the machine trusts before Windows will install it, and the project has
no signing certificate (and the CI has no secrets). An unsigned MSIX cannot be installed by double-clicking it, which
breaks the "works by double-click" goal. WiX 4+ is under the Open Source Maintenance Fee and MS-RL terms, which does not fit
the "permissive licences only" rule as cleanly. Inno Setup is a free build-time tool (nothing from it is shipped inside the
app), installs per user without administrator rights, handles the Spanish and English wizard, and winget supports it natively
(`InstallerType: inno`). Unsigned installers only trigger the SmartScreen "unknown publisher" prompt, which the README
documents. The script lives in `packaging/installer/Rotula.iss`; the CI installs Inno Setup with Chocolatey. If a signing
certificate becomes available, signing both the exe and the installer is the only change needed.

### Winget
`packaging/winget/` holds templates for the version, installer and en-US locale manifests (schema 1.9.0).
`scripts/New-WingetManifest.ps1` fills version, installer URL, SHA-256 and date from the tag, and the release workflow attaches
the result as a zip. Submitting it to the winget-pkgs repository is a manual step because it needs a personal token.

### Coverage gate
CI fails when line coverage of `Rotula.Core` drops below 80% (`scripts/check-coverage.ps1`, reading the Cobertura report from
coverlet). The script counts hit lines itself rather than trusting the rounded `line-rate` attribute.

### Hygiene gate
`scripts/check-hygiene.sh` runs in CI and can be run locally. It fails when the tree contains tooling directories or files
that must not be published, or when a commit message in the pushed range carries a co-author trailer or generated-with
footer naming an automated tool.

### Printing (Print dialog)
`Rotula.Printing.IPrintService` is the only printing API the App uses; `new PrintService()` is created in `App.xaml.cs`
and nowhere else. The Print dialog (Ctrl+P) works on `PrintViewModel` + `PrinterSettingsViewModel`: a Windows printer
(discovered asynchronously) or TCP host and port, plus the profile (DPI, width/height, media tracking, darkness ~SD, speed ^PR),
copies, "Print test label" and "Save as ZPL". Every send is async with a `CancellationToken`, a busy bar and a Stop button; closing
the window cancels a job in flight. The label is validated in the App before sending (so the user sees the real issues, not the
service's bare keys); the test label does not depend on the design being valid. `Print.Error.<Kind>` and `Print.Profile.*.Range`
keys are translated in both languages; a test enumerates `PrintErrorKind` and scans the Printing sources for the keys.
The last printer and profile are stored in `settings.json` (`print` section) only after something was actually sent or saved.
Tests use a fake `IPrintService` that records calls: no test can reach a printer or the network.
Known gap: `PrintZplAsync` does not do the byte-exact encoding of imported raw ZPL blocks that `PrintAsync` does, so a batch
containing raw blocks with high bytes could be altered. This needs an API change in Rotula.Printing.

### Data merge
`DataMergeViewModel` (window opened with Ctrl+M, not modal so the canvas can be watched) wraps Core.Merge: CSV (delimiter auto
detected and shown; UTF-8 with fallback to Windows-1252 as Excel writes it) or .xlsx (sheet picker), read off the UI thread with
`FileShare.ReadWrite` so a workbook open in Excel can still be read. The record shown on the canvas and in the ZPL view comes
from `MergeDataSet.GetRecordData`; the export/"has errors" gate keeps using the design with its sample values.
Batches (check, print all, export) take a private copy of the label and a fresh `MergeOptions`, then enumerate `MergeBatch` on a
background thread, so editing the label never changes a running batch. Print all asks for confirmation, sends jobs of at most
`MaxRecordsPerJob` records, puts the printer set-up commands in front of the first job only, and on failure says how many jobs
were already sent. Export writes one file (through a temp file) or one numbered file per job, in the label's encoding.
Settings are saved in the .rotula file (`merge` object); the data is never saved.

### .rotula format version 2
`formatVersion` 2 adds the optional `merge` object. A file is written as version 1 when it has no data-merge settings, so older
copies of Rotula still open it; version 1 files open unchanged; versions above 2 are refused with the "update Rotula" message.
Unknown or missing values inside `merge` fall back to defaults.

### Untrusted paths
A path stored in a .rotula file (the data-source hint) or in settings.json (recent files) is untrusted: `\host\share` makes
Windows send the user's credentials just by checking the path. `SafeLocalPath` accepts only plain local drive paths
(`C:\...`), rejecting UNC, device (`\?\`, `\.\`), URL, relative and alternate-stream forms, using string checks only. Opening a
label never touches the file system for the hint (`CanReload` does not look at the disk); the file is checked only when the user
clicks Reload, which also refuses mapped network drive letters. A path the user picks in a file dialog is trusted.

### Look and feel
Warm off-white chrome, white surfaces, one burnt-orange accent (`#C8420F`) for primary actions, selection, focus and the canvas
handles. Everything is in `Resources/Theme.xaml` (colours, type, restyled Button/TextBox/ComboBox/CheckBox/RadioButton/ListBox/
Tab/Menu templates, focus ring) and `Resources/Icons.xaml` (original thin-stroke vector icons drawn for this project, no icon
pack). The title bar is drawn by the app (`WindowChrome`), the tool rail replaces the toolbox, and the properties panel lays
number fields two to a line (`PropertyGridPanel`). The ZPL panel is the only dark surface (a code view by design) and is hidden by
default; View > Show ZPL code or the toolbar toggle shows it, and the choice is stored in settings.json. UI font is
Segoe UI Variable with Segoe UI fallback; no font is bundled.
