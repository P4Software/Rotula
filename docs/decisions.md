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

### Database data source (Data merge window)
The Data window has a source switch: File (CSV / Excel) or Database. Database mode lives in `DatabaseSourceViewModel` (connection drop-down,
"Table or view" with a search box and a 100-row preview, "Query" with a monospace SELECT editor) and `ConnectionManagerViewModel` (modal manager,
`DialogWindow` style). Both talk to `Rotula.Data` only through `IDatabaseServices` (the saved connections plus a factory), so view-models run with fakes.
- **Same pipeline**: "Use this data" produces a `MergeTable` and goes through the same `SetTable` / `Rebind` path as a CSV, so mapping, counters, copies,
  the record navigator and batch print/export are unchanged. A table is read in full up to the row cap (the 100-row preview is only a preview).
- **Reconnects when the user opens a label** (0.3.4, then 0.3.5 for every label; before, a button was needed): opening a label that refers to a connection looks the NAME up
  in the user's own saved connections and runs the saved query, so the canvas shows real values at once. Glenn decided on 2026-10-12 to drop the idea of a per-file trust list
  (SHA-256 of the file, set by saving or by a manual reload): a label opened for the first time still showed the red crossed box, which was the original complaint. The accepted
  tradeoff, raised by an automated security review: a label from someone else can run its saved SELECT on the user's own saved connection when opened. The limits that stay: the
  query goes through the read-only guard (`SqlGuard`) before anything is sent, the file holds no host, user or password, only a connection the user already saved under that exact
  name is used, and the rows stay on the PC. A failure shows its plain-language reason in the Data window (no dialog) and "Connect and reload data" stays for a retry. A data file
  is reloaded the same way, through the same local-drive check as the Reload button. Reading the data again does not mark the label as changed.
- **The .rotula file holds only** the connection NAME, the query text, parameter defaults (name, value, type) and the row cap (format version 4, `merge.database`).
  The DTO has no member for host, user, password or connection string; a test asserts that, and that a hand-edited file cannot smuggle them back in. A table is
  saved as the quoted `SELECT * FROM ...` the engine built.
- **Parameters** are text by default; Number and Date can be chosen per parameter (PostgreSQL does not convert implicitly). Numbers and dates are parsed
  culture-independently first so a saved label behaves the same on every PC.
- **Passwords**: a `PasswordBox` is copied into the view-model on change and emptied on save, selection change and close. Store semantics: an empty box means
  "keep" (`null`), switching to a method without a password sends `""` (remove). A saved connection that was edited cannot be tested with its saved password
  (the public API cannot read it back), so the manager asks to type it again; an unchanged one is tested through `IConnectionStore.OpenAsync`.
- **Names are fixed** once saved (labels refer to them). Encrypt-off and trust-certificate each have an amber sentence under the box before it is touched and an
  amber banner while the risky value is on; a certificate failure offers "Trust this server and save".
- **Row cap** 100,000 by default (1 to 1,000,000); a truncated result shows an amber note and the same note stays in the load warnings.
- **Accessibility**: the shared TabControl template now names its content host `PART_SelectedContentHost`; without it WPF did not expose the content of any tab to UI Automation.

### Fields and data binding (field picker)
Any element with content can be bound to data; there is no separate "Data field" tool (a `DataFieldElement` is only read from old files and shows up as a
Text element with the field as its content: the Objects list says Text with a {} badge, the Properties panel shows a Text row; the first edit that cannot be
written as "one field" turns it into an ordinary text element with the same id and place; old files print exactly as before).
- **One list, `FieldsViewModel`** (on `MainViewModel.Fields`): the columns of the loaded data (CSV, Excel, database), the counters' `#Name` virtual columns and the
  label's own variables, de-duplicated case-insensitively (the column wins). It follows load, replace and clear, the record navigator (sample values) and
  the counter list, and raises `FieldsChanged`. The control is disabled, with "Load data first: Printer > Data merge", while the list is empty.
- **Placeholders must match Core's name rule** (`#?[A-Za-z_][A-Za-z0-9_]*`). A column such as "Unit price", or one with an accent, is offered as `{{Unit_price}}`
  and the match to the real column is stored as a column override of the Data merge settings (the same mapping the Variables tab shows), so what the picker
  writes always resolves.
- **Content row** (`TextRow` with fields): `[Fixed value | Field v]`. Choosing a field makes the content a pure binding `{{Name}}` shown as a chip with an (x) that goes back
  to fixed text (the shown record's value); "Insert field" and typing `{{` (autocomplete, `FieldBox`) insert at the caret to mix text and fields. Insert, bind and unbind are
  undo steps of their own and never merge with typing. Images bound to a field are a later Core feature; the row type is the extension point.
- **Missing columns**: a name used by an element that the loaded data cannot feed (the same "unmapped" list as the Variables tab) gets an amber warning icon in the
  Objects list and under the content row. Nothing is drawn on the label. Names the label defines itself (dates, counters, prompts) are never flagged.
- **Add to label** (Variables tab of the Data window): a text element with `{{Column}}` as content, or Code 128 / QR code through the arrow next to it; "Add all fields"
  stacks one text per column below the existing elements with an even gap, shrinks the text to fit and warns when it still does not. A text variable with the current
  sample is declared in the same undo step so the label stays readable without data. "Use here" puts the field into the element selected on the label.

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

### Stock picker (New label dialog)
The New label dialog is a stock picker: a search box with the focus, filters (brand, unit, roll/sheet/round, verified only, favourites
only), a virtualised, grouped list and a detail pane. The five original sizes stay as the "Common sizes" group, with "Custom size..." as its
last row, so the old way of working is one click away. The library (bundled plus `%AppData%\Rotula\stocks`) is read once on a worker
thread through `IStockStore`; the search itself takes well under a millisecond, so the window only waits 120 ms after typing before it runs
(the view-model has a synchronous `RunSearch` for tests). The first hit is selected, so type then Enter creates that stock; Down and Up move
through the results without leaving the box; Ctrl+D stars the stock and Ctrl+F returns to the search box.
- The detail pane shows every known fact and exactly the warnings that creating the label will raise: `StockApplier.Apply` is run on a blank
  label when a stock is selected. The library names a stock by id in those messages; the UI shows its name and part number instead.
  A continuous roll asks for the label height before it can be created, because the stock has none.
- The resolution starts from the printer (the detected one when the printer has been read, else the last used) and says where it came from.
- Favourites and recent stocks are two lists in `settings.json` (`stockFavorites`, `stockRecent`), written through the same settings object
  the main window saves, so neither overwrites the other. They are read and written with `StockUserList`.
- "Save as my stock" appends to one file, `my-stocks.json` (atomic write; ids start with `user-`, so a user entry can never replace a
  bundled one by accident). A supplies list is previewed (certainty per row, notes for skipped rows) and saved as `import-<date>.json` only
  after confirmation. Prices are never read.
- The label stores the stock id; the Label panel shows the stock name and part number (or says the stock is no longer in the library).

### Add-ons (provider packs) at start-up
`PackLoader.LoadAll` runs on a background thread while the window opens (`PackStartup.Begin`); a failure of the loader itself is logged and
ends as an empty report. The connection manager waits for it (`PackStartup.Ready`), so a connection of an add-on is never shown as "missing"
only because loading was not over. Problems appear as a small non-modal notice under the toolbar ("Some data source add-ons could not be loaded: Details");
the details dialog lists each problem in plain language (the key is translated, the English sentence of the loader is the fallback) with the
technical detail folded away. An add-on in the user's own folder is never loaded without approval: the prompt says that an add-on is a
program, shows publisher, version and fingerprint, and defaults to Skip. Skip changes nothing and the question comes back at the next start;
Approve records the fingerprint and loads the add-on at once.

### Connection manager drawn from the registry
The engine list and the form come from `ProviderRegistry` and the option schema of each provider (`Data.Option.*` labels, text, number,
host, file or folder path, choice, switch), so an add-on engine needs no UI change. Presets are listed under their provider; choosing one
fills the port, the secure defaults and its options, shows its hints and warns when the ODBC driver of a file preset is missing. A typed
host that matches a preset offers its settings. The password stays a separate protected box (the schema has no secret kind).
- Settings built by the form carry `ProviderId`, `PresetId`, `CaCertificatePath` and all extras, so saving again never drops them. Built-in
  providers keep an empty `providerId` in `connections.json`.
- A saved connection whose add-on is not installed is shown read-only with a plain explanation; Save and Test are off and the entry is never rewritten.
- Test goes through `OpenWithSettingsAsync`. The saved password is reused only for an existing connection edited under its own name with an
  empty password box (the store then decides whether the edit still allows it); anything else passes an explicit password (or "none"), so the
  placeholder name used for an unnamed form can never pick up the password of a real connection. When the store asks for the password again
  the user is told and the password box takes the focus.

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

### Machine-wide MSI (WiX Toolset), next to the Inno Setup installer
Approved by the owner (Glenn) as part of the enterprise deployment work: administrators need a per-machine MSI for Intune,
Group Policy software installation and Configuration Manager, which handle MSI packages natively (product code detection,
upgrades, return codes). The per-user Inno Setup installer stays the default for people who install Rotula themselves.
- **Tool**: WiX Toolset v5 (`WixToolset.Sdk/5.0.2`, MS-RL), restored by `dotnet build` on the build machine. It is a build-time
  tool only: nothing of it is inside the MSI or the app, so the "permissive licences for what ships" rule is unaffected. It is
  listed under "Build and test only" in THIRD-PARTY-NOTICES.md. The WiX project asks organizations that earn money from it to
  pay its Open Source Maintenance Fee; **whether that applies to P4 Software is for the owner to check** before the first MSI
  release.
- **Not in Rotula.sln**: the WiX build needs Windows Installer and only runs on Windows; the solution must keep building on any
  runner. `packaging/msi/Rotula.Msi.wixproj` is built by `build-installer.ps1` from the stage folder after the programs are
  signed, and the MSI is signed and verified like the setup exe. An unsigned MSI is renamed `-UNSIGNED` and never published.
- **Shape**: per-machine, `Program Files\Rotula`, advertised Start menu shortcut, `.rotula` association in HKLM (property
  `ASSOCIATE=0` skips it), ARP links and icon, no Modify. No wizard pages (silent deployment is the use case); deploying it
  means accepting `LICENSE-THIRD-PARTY.txt` for the users, as `docs/deployment.md` says.
- **Upgrades**: fixed UpgradeCode `{4B2C6076-5B82-411C-B751-EC4B70BEE9C6}` (never change it), a new ProductCode per build,
  `MajorUpgrade` with same-version upgrades allowed (MSI versions are numeric, so 0.4.0 must be able to replace 0.4.0-rc1) and
  downgrades refused. The UpgradeCode differs from the Inno AppId on purpose: they are two products, and a computer should
  have only one of them.

### Organization policies (Group Policy / Intune)
`Rotula.Files.Policy` reads `SOFTWARE\Policies\P4Software\Rotula` from HKLM and HKCU once at start-up, value by value with
the computer winning (`LayeredPolicySource` over two `RegistryPolicySource`s, behind `IPolicySource` so tests use a dictionary).
It lives in Rotula.Files because both the app and the MCP server need it. `RotulaPolicy.Read` ignores values it does not
understand, so a typo cannot force a wrong value.
- **What a policy may do**: choose a value for the user and lock it (language, update check), suggest a default (printer, not
  locked), or take something away (add-ons from the user's folder; MCP printing and database access). It never switches a risky
  feature on: the MCP policies only block, and the MCP settings file still has to allow printing and databases.
- **Locked settings in the UI**: the menu entries are disabled (the language commands' CanExecute, the update entry's
  IsEnabled) and a disabled "Managed by your organization" line appears under them. The user's own value stays in
  settings.json and comes back when the policy is removed; `AppSettings.LanguageIn` / `UpdateCheckIn` give the value in force.
- **Templates**: `packaging/policies/Rotula.admx` with en-US and es-ES ADML. A test checks that every registry value name of the
  template is a `PolicyNames` constant (and the other way round) and that both ADML files define the same strings.

### Update check: opt-in, anonymous, never installs
The owner approved an update check on the condition that it sends nothing unless explicitly enabled. Rules:
- Off by default. It runs only when the user ticks Help > Check for updates once a day, or the `UpdateCheck` policy is 1. The
  policy at 0 forces it off and the user cannot switch it on.
- At most once a day (`UpdateSchedule.IsDue`, 24 hours; a last-check time in the future counts as due so a wrong clock cannot
  stop it). The time is stored before asking, so an offline computer tries once a day, not at every start.
- One `GET https://api.github.com/repos/P4Software/Rotula/releases/latest`, User-Agent `Rotula` without a version, no cookies,
  no identifiers; the Windows proxy with default credentials. Failures are silent.
- `tag_name` is parsed as SemVer (`SemanticVersion`, pre-release ordering per SemVer 2.0); drafts and pre-releases are ignored.
  The link of the notice is built from the parsed version (`https://github.com/P4Software/Rotula/releases/tag/vX.Y.Z`), never
  taken from the answer, and the launcher opens https addresses only.
- A newer version shows a non-modal strip under the toolbar ("View release", close). Closing it remembers that version so it
  does not come back for it. Nothing is downloaded or installed, ever.
- Testable: version compare, schedule, parsing and the checker are UI-free (`Services/UpdateCheck.cs`); HTTP is behind
  `IReleaseFeed`, and `GitHubReleaseFeed` takes a handler so a test sees the exact request.

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
`packaging/winget/` holds templates for the version, installer, en-US default locale and es-MX locale manifests (schema 1.10.0).
The installer manifest lists two installers: the Inno Setup exe (`Scope: user`, the default) and the MSI (`Scope: machine`,
`InstallerType: wix`, `ElevationRequirement: elevationRequired`, its ProductCode and the fixed UpgradeCode).
`scripts/New-WingetManifest.ps1` fills version, URLs, SHA-256 hashes, the MSI's ProductCode (read from the signed MSI) and the
date; `build-installer.ps1` runs it and zips the result. Submitting it to winget-pkgs is a manual step (docs/releasing.md).

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

### Print dialog: the place where the records to print are chosen
When a data file or database is loaded and has records, `PrintViewModel` receives the `DataMergeViewModel` and the dialog shows a Records card
(the same `DataRecordsView`, bound to the same view-model, so ticks stay in step with the Data window). Print then sends the ticked records as a
batch, in data order, through `DataMergeViewModel.PrintBatchAsync` (the code behind the Data window's own print button: serial numbers reserved
first and confirmed only for labels that reached the printer, print-time questions, jobs, partial and cancel handling, the "Print N labels on ..."
question). Save as ZPL goes through `ExportBatchCoreAsync` the same way, so it follows the Data window's one-file or one-file-per-job setting and, like
that export, does not add the printer set-up commands. The copies typed in the dialog are the copies of each record's label, unless the Data window
sets copies per record (fixed or from a column); the dialog says so then. With records but nothing ticked, Print is disabled and the reason is shown.
With no data loaded the dialog is the single-label dialog it always was. The dialog subscribes to the data view-model and unsubscribes when it closes.

### Printer status (~HS) around a job
Approved by the owner (assessment item 4). The `~HS` reply is decoded in Rotula.Printing (`HostStatusParser`), not Core: it is about the printer,
not the label, and Core's catalogue keeps `~HS` out of scope for label files. The check is opt-in per `PrinterTarget` so library callers and
the MCP `print_label` path send exactly what they did before; the app turns it on per transport (on for TCP, off for Windows queues, because
many USB drivers cannot read back and a non-ZPL queue would print the query). An unknown status never blocks. A blocking condition is a
`PrintError` kind of its own (`HeadOpen`, `PaperOut`, ...) with 0 bytes sent, so `LabelPrintRun` gives the numbers back; the verification after
the job runs after the numbers were confirmed and is a notice, so a fault after the job never gives a number back. Batches check every job and
verify once after the last one.

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

### Signed releases are built locally, not in CI
Release files are signed with Azure Artifact Signing (account and certificate profile `p4software`, signer "Barrdega Sistemas
NA LLC") using the developer's `az login` session and the Microsoft `sign` tool, the same process as the company's other
products. CI has no signing secrets by design, so `.github/workflows/release.yml` no longer builds the installer, the winget
manifests or the GitHub Release: on a `v*` tag it runs the tests, the coverage gate, the hygiene check and a smoke test, and
keeps an unsigned portable zip as a workflow artifact only. Because it never touches Releases, it cannot fail or clobber a
release created locally. `packaging/installer/build-installer.cmd` (double-click) does the whole signed build into the
git-ignored `artifacts/` folder; it is safe by default (builds and signs, publishes only with `"publishToGitHub": true` in
`build-settings.json`, only after both signatures verify, never replaces assets of an existing release, and never publishes
unsigned output). Signatures are verified with `Get-AuthenticodeSignature` (Valid, expected signer, timestamp), never from the
sign tool's exit code; the signing helper is named `SignFile`; calls run one at a time and retry up to three times on a
transient token error. The version lives only in `Directory.Build.props`. See `docs/releasing.md`.

## MCP server (Rotula.Mcp)

Owner decision: Rotula ships a Model Context Protocol server so AI assistants can create and edit labels. Details for users are
in `docs/mcp.md`; the decisions that shaped the code:

- **Separate headless executable**, `src/Rotula.Mcp` (`net10.0-windows`, because it reuses the DPAPI-protected connection store and the
  Windows spooler). It never references WPF. The `.rotula` serializer, its DTOs, `DataMergeSettings` and `SafeLocalPath` moved from
  `Rotula.App/Files` into the UI-free library `src/Rotula.Files` (namespace `Rotula.Files`) so both hosts share one file format.
  Behaviour and format are unchanged; the one fix is that a non-integer `formatVersion` is now reported as "not a Rotula file"
  instead of a raw exception.
- **Official SDK** (`ModelContextProtocol` 2.2.0, Apache-2.0, permissive) over stdio, hosted by the generic host with the *empty*
  builder: no environment variables, no appsettings files, no console logger (stdout is the protocol). Publish flags (single file,
  self-contained) are passed on the command line only, never in the csproj, so the test project can reference the program.
- **Nothing dangerous without the settings file** (`rotula-mcp.settings.json` next to the program): printing, printers, databases
  and the label folders are all opt-in. A damaged file means the safe defaults, never more access.
- **One gate for paths** (`PathGuard`): textual rules of `SafeLocalPath`, canonical containment with a trailing separator, a
  whitelist of extensions per tool, reserved Windows names, links and junctions refused, size checked before reading.
- **Printing is a two-step state machine** (`PrintGate`): the first call is always a dry run; the second needs a random one-time
  token bound to a hash of printer + copies + final ZPL. Changing anything invalidates it. Copies go on a copy of the label.
- **Messages**: tools return plain sentences. The English texts come from the app's own `Strings.en.resx`, embedded as XML in the
  server, so validation and print messages stay identical in both programs.
- **Previews are approximate** and say so every time (stand-in fonts, as in the app).

## Database providers (Rotula.Data)

Owner decisions of 2026-10-10: the choice of databases must be large; some engines need non-MIT components; those are accepted as recorded below.
Developer documentation is in `docs/data-providers.md`; the licence list is in `THIRD-PARTY-NOTICES.md`.

### Provider model and packs
- **The closed `DatabaseProvider` enum is now a registry of descriptors.** Each engine is an `IProviderDescriptor` (id, message key for its
  name, category, port, sign-in methods, a form schema the UI renders, SQL dialect, read-only steps, connection-string builder, error mapper).
  The five original engines keep their ids (`sqlserver`, `postgresql`, `mysql`, `sqlite`, `odbc`) and their enum values; `Custom` was appended
  for pack engines and always travels with a string `ProviderId`. `connections.json` stays at version 1; the new fields (`providerId`,
  `presetId`, `caCertificatePath`, `extras`) are optional, so an older Rotula still reads the file. Entries of a pack that is not loaded are
  kept on the next save instead of being dropped.
- **Presets are data** (`src/Rotula.Data/Presets/presets.json`): MariaDB, Amazon RDS/Aurora, Redshift, CockroachDB, YugabyteDB, Supabase, Neon,
  PlanetScale, TiDB, SingleStore, Azure SQL / Synapse / Fabric, Google Cloud SQL, and Microsoft Access, Excel and text files through ODBC. They
  are secure by default (encrypt on, no blind trust of certificates) and a test enforces it. Pooler hosts (Neon `-pooler`, Supabase pooler)
  do not receive the `options` startup parameter; the in-transaction `SET TRANSACTION READ ONLY` and the guard remain.
- **Packs are separate folders**, loaded by `PackLoader` into their own `AssemblyLoadContext`. The `packs` folder next to the program is
  trusted (it is installed with Rotula). `%AppData%\Rotula\packs` is opt-in: loading a pack runs its code with the user's rights, so each
  user pack needs an explicit approval after a plain-language warning, and the approval stores the SHA-256 of EVERY file of the pack; any
  change asks again. No other folder is ever searched; folders with links are skipped. A pack can add provider ids but never replace one.
  A pack that fails to load is a warning with a message key and never stops the application. Proven with a single-file, self-contained
  executable that loads a sample pack, with its own managed dependency and its own native library, from a `packs` folder beside it.
- **The query guard stays in the core.** A pack supplies a dialect and a connection factory; the shared code applies the guard and the
  read-only transaction. Engines that are not SQL (MongoDB, DynamoDB, REST) provide their own source through `CreateCustomSource` and must
  bring their own allow-list validator and tests.

### Read-only is layered, and the guard is a safety net
The guard (one SELECT, no writes, no file or network functions) is the first layer; the second is whatever the engine offers (read-only
session or transaction, read-only open mode, `readonly=1`, `enable_external_access=false`); the third, which only the user can provide, is
a database account that can only read. SQL Server, ODBC and the cloud warehouses have no read-only session, so for them the account is the
real control and the UI says so.

### Guard: differential lexing (security)
Where an engine and the guard could disagree about the end of a string or quoted name (backslash escapes that depend on server settings such
as MySQL `NO_BACKSLASH_ESCAPES`, PostgreSQL `standard_conforming_strings`, or rules the vendor documents loosely), the guard lexes the text again
with each uncertain feature flipped, all on and all off. If the readings place a token boundary differently, the query is refused as
**ambiguous** (`Data.Query.Rejected.Ambiguous`) instead of guessed. A quoted identifier of an uncertain kind that contains a backslash is refused
outright when the dialect reads that kind of quote without backslash escapes. The ODBC "database behind the driver" hint only ADDS rules (the four
classic lexings still apply), because the hint is a claim Rotula cannot check. The price is that `'it\'s'` is refused for MySQL (write `'it''s'`). Each dialect lists the features it checks and the vendor page
its rules come from. When the engine nests block comments the guard nests them only if the vendor documents it (PostgreSQL, ClickHouse);
otherwise it does not, because skipping text the engine will run is the bypass.

Other guard changes: `NEXT VALUE FOR` (a sequence call that changes state, valid in SQL Server and MariaDB) is refused in every dialect; the
words NEXT and VALUE alone stay allowed.

### Saved passwords are reused only for the destination they were saved for
`IConnectionStore.OpenWithSettingsAsync` lets the connection manager test an edited connection. The saved password is used only if every property
of the settings is equal to the saved one except `Name`, `Database` and the two timeouts (default deny, computed by reflection so a property
added later is covered automatically; a test fails if an unknown property type appears). Any other difference, including turning encryption off or
trusting the certificate, makes the caller supply the password again (`Data.Store.PasswordReenterRequired`). The password is never returned.

### SQL Server networking component (SNI)
`Microsoft.Data.SqlClient` is MIT, but its native networking library arrives as `Microsoft.Data.SqlClient.SNI.runtime` (7.1.0) under
"Microsoft Software License Terms: MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY", not under an open-source licence. The owner accepted those terms on
2026-10-10. The terms say the software consists of "Distributable Code". Summary of what they require of Rotula (P4 Software), read from the
`LICENSE.txt` in the package:
- Rotula may copy and distribute the object code, and may let distributors of Rotula do the same.
- It must be used inside Rotula and not distributed as a standalone product.
- **Pass-through:** distributors and external end users must agree to terms that protect the component at least as much as Microsoft's agreement.
- **Indemnity:** P4 Software must indemnify, defend and hold Microsoft harmless from claims, including attorneys' fees, related to the distribution or use of
  Rotula, except claims based solely on the unmodified component.
- No use of Microsoft trademarks in the application's name or in a way that suggests Microsoft endorses it.
- Do not modify or distribute its source so that any part becomes subject to a licence that requires source disclosure or the right to modify.
- Do not reverse engineer it, work around its technical limits, remove Microsoft notices, or rent, lease, share or transfer it as a stand-alone offering.
- Comply with export law. If features of the software collect data, comply with the law and the Microsoft Privacy Statement (Rotula has no telemetry).
- Microsoft's liability is limited to US$5 and the software is provided as is.

**Open action (owner / legal), partly done:** Rotula's own licence is MIT and does not bind end users to terms "at least as protective" as Microsoft's.
The installer's licence page (which must be accepted) is now a combined text generated at build time: the MIT licence, the full unchanged licence of the
SNI component, and a sentence saying that installing means agreeing to those terms for that component (`LICENSE-THIRD-PARTY.txt`, also shipped in the portable
zip, the installed folder, as a release file and as the winget agreement). Still open: a lawyer should review the wording and the indemnity question before
wider distribution (flagged in `docs/releasing.md`). Until then this decision is accepted but not claimed to be fully discharged.

### Provider packs built in phase 2, group 1: Oracle, Firebird, DuckDB, ClickHouse
All four are separate folders built by `scripts\build-packs.ps1` into `artifacts\packs\<id>\` (git-ignored); `src\PackProject.props` holds what they share.
Each folder is complete (its own driver, native libraries, `pack.json`, `NOTICES.txt` generated from the files the build really produced, `README.md`).
- **A pack references the host assemblies as plain files, not as a project reference.** A project reference to `Rotula.Data` drags its whole
  package graph (SQL Server networking with its non-MIT licence, SQLite, Npgsql, MySqlConnector) into the pack folder, with licences the pack's
  notices would not cover. `Rotula.Data`, `Rotula.Data.Abstractions` and `Rotula.Core` are compile-only references; a test fails if any of them, or
  any driver of another pack, appears in a published pack folder or if a DLL is missing from `NOTICES.txt`.
- **Publish is `win-x64`, not self-contained, never trimmed or bundled.** The runtime identifier only selects the one native library set (DuckDB
  ships five); the .NET runtime is Rotula's. Trimming would modify Oracle's DLL, which its licence forbids.
- **Provider API 1.1** (additive; a 1.0 pack still loads): `IProviderDescriptor.FormatCell` (a pack can write a value the driver's .NET type
  cannot hold: DuckDB `DECIMAL(38)` would silently lose digits as a `System.Decimal`; Firebird loses the trailing zeros of a price and returns
  `DECFLOAT` as `125E-2`), and `SqlDialect.IdentifierBackslashEscapes`. The packs that use them (Firebird, DuckDB, ClickHouse) declare `minDataApiVersion` 1.1; Oracle stays at 1.0.
- **Oracle** (`Oracle.ManagedDataAccess.Core` 23.26.301): accepted as an optional pack, shipped unmodified with its licence in `NOTICES.txt`; the
  pack's README repeats the conditions in plain words (no fee for the add-on itself, keep the folder intact, no reverse engineering: the API was
  taken from Oracle's documentation, not from the DLL). The package has `net8.0`/`netstandard2.1` builds only and runs on .NET 10 (a real
  connection attempt went through the whole network stack); Oracle states no .NET 10 support. The "information collection" clause names installers
  and auto-update, which a library has not; no network trace was made. TLS (TCPS) is on by default; the connect descriptor is built only from parts
  restricted to letters, digits and `. - _ $ #`, so a value cannot add descriptor keywords. **Unverified against a real server** (none is available):
  see the pack README.
- **Firebird** (`FirebirdSql.Data.FirebirdClient` 10.3.4, IDPL 1.0): shipped unmodified, the licence and the upstream source URL are in `NOTICES.txt`.
  Only the library's own files carry the copyleft; Rotula's pack code is a separate MIT file. Read-only is a READ ONLY snapshot transaction, verified
  on a real Firebird 5.0.4 server (official zip, SHA-256 checked, bound to 127.0.0.1 in a scratch folder, stopped and deleted afterwards): it
  refuses writes even for SYSDBA, also from selectable procedures and `IN AUTONOMOUS TRANSACTION`. Firebird has no TLS: "Encrypt" means
  wire encryption, required whenever a session key exists (user and password).
- **DuckDB** (`DuckDB.NET.Data.Full` 1.5.6, MIT): the file is opened `access_mode=READ_ONLY` with `enable_external_access=false`, no extension
  auto-install or auto-load, and `lock_configuration=true`, all as open-time configuration (a `SET` afterwards would be refused by the lock).
  Verified against the real engine, including that file reads, `COPY`, `ATTACH`, `INSTALL` fail and that nothing can be switched back on. `:memory:`,
  `md:` (cloud), UNC and URL paths are refused. DuckDB block comments nest (verified), so the lexer nests them.
- **ClickHouse** (`ClickHouse.Driver` 1.5.0, MIT, the maintained successor of `ClickHouse.Client`, with a `net10.0` build): `readonly=1` is
  sent with every request, once, as the last URL parameter, by a handler in front of the driver. ClickHouse has no native Windows build, so
  the server side is **unverified**; the requests the real driver produces are verified with a fake network, and the server's rules were read
  in its source (`SettingsConstraints.cpp`, `HTTPHandler.cpp`): a profile with `readonly=1` refuses any setting whose value changes (except
  `database`, `default_format` and a few more), so the pack sends no time limit by default (opt-in `serverTimeLimit`), drops the driver's
  `enable_http_compression=false`, and an account with a `readonly=2` profile cannot be used (it may not change `readonly`). Quoted identifiers use the string
  escape rules ("the same rules ... also apply for string literals", vendor Syntax page), so `QuoteIdentifier` doubles backslashes for it.
- **Guard changes found by the real engines**: Oracle `table@"LINK"` (quoted link) was not refused and is now; any `@` that is not a parameter
  is refused for Oracle. ClickHouse backslash handling in quoted names and DuckDB nested comments are now verified instead of guessed.

### Accepted, not yet shipped (placeholders; the pack work finishes the notices)
Oracle and Firebird are done (see the section above). Each of these ships only as an optional provider pack with its own licence notices file, never inside the core executable.
- **Azure Cosmos DB (`Microsoft.Azure.Cosmos`)**: MIT, but the package ships Microsoft Visual C++ runtime files (`vcruntime140.dll`,
  `vcruntime140_1.dll`, `msvcp140.dll`, `Cosmos.CRTCompat.dll`) under Microsoft's redistribution terms, plus the native `Microsoft.Azure.Cosmos.ServiceInterop.dll`.
  Read and record the VC++ redistribution conditions when the pack is built. Use read-only keys.
- **Microsoft Entra sign-in for the SQL Server family (`Microsoft.Data.SqlClient.Extensions.Azure`)**: MIT packages (Azure.Identity, Azure.Core,
  Microsoft.Identity.Client, Microsoft.Identity.Client.Broker) plus `Microsoft.Identity.Client.NativeInterop` (native `msalruntime.dll`) under
  Microsoft Software License Terms. A keyword search of those terms found no explicit redistribution clause; read them in full when the pack is
  built and record the conditions here.

Not bundled, ever: SAP HANA, Teradata, IBM Db2 and Informix drivers. The user installs their own ODBC driver; Rotula offers the preset and the
dialect hint.

## Variables: dates, counters, prompts and computed values

### One declared variable, one optional definition
`LabelVariable` keeps its `(name, sampleValue)` shape and gains `Kind` (Text by default) plus one nullable options object per kind
(`Date`, `Counter`, `Prompt`, `Computed`). Old labels and the old constructor keep working; a plain variable is written to the file exactly as before.
Option objects (not a class hierarchy) were chosen so the file DTOs, the reflection guard in `FilesCatalogTests` and a property panel can treat every
kind alike. Code that copies variables must call `LabelVariable.Clone()`.

### The generator never reads the clock, a file or the environment
Time, the data record, prompt answers and the counter store enter only through `VariableContext` (a `TimeProvider`, so tests use a fake clock with a
fixed daylight-saving zone). `ZplGenerator.Generate(doc, data)` is unchanged and treats `data` as final values (new kinds not in `data` show their
sample value); `GenerateWithVariables(doc, context)` resolves everything. Same document + same context = byte-identical ZPL.

### Resolution order
record, then plain sample values, then prompts, counters, dates, and computed variables last (they may read everything and each other; cycles are an
error with a key, not a stack overflow). A counter, date or computed variable replaces a same-named data column with a warning; a prompt yields to a
non-empty data column unless it is set to override; a plain variable never replaces data.

### Date arithmetic
Seconds, minutes and hours are elapsed time; days, weeks, months and years work on the wall-clock date. Months and years clamp to the last day of the
target month. A non-existent wall time (spring forward) moves one hour later; a repeated one uses the first occurrence. The Gregorian calendar is forced
whatever the culture. Offsets apply in list order. Culture names are looked up with `predefinedOnly` so a typo is reported instead of silently
formatting with invariant data.

### Printer clock maps onto ^FC and ^SO, with an explicit fallback
A date variable with source "printer clock" is written as clock codes in text fields (`^FC` indicators chosen so they do not occur in the rest of the
field; the user's own `^FC` wins) and its offset as `^SO` (secondary and third clock: months, days, years, hours, minutes, seconds). At most two
different offsets fit a label. Anything the printer cannot express (business days, rounding, UTC, formats without a printer code such as the ISO week,
non-English names, barcodes, a third offset) falls back to the computer clock with a warning key; the variable never prints empty. `^SO` stays
"Passthrough" in the command catalogue because it is written for variables but not modelled or read back; the catalogue notes say so.

### Counters that remember: reserve, confirm, never reuse
Opt-in per counter (off by default). The store keeps, per `labelId/counterName`, the next position (not a value), the last confirmed value, a
fingerprint of kind/start/step/padding and the unconfirmed reservations. `Begin` reserves a block under an exclusive file lock and writes it before
printing; `Complete(n)` confirms n and gives back the unused tail only if it is the newest block; a crash leaves the block pending, so its numbers are
skipped and reported until the operator reclaims them. An edited counter (fingerprint changed) blocks printing until accepted or reset. A damaged
`counters.json` is never read as empty (that would reuse numbers): every call fails with a key until an explicit recover moves the file aside.
Atomic write = temp file, flush to disk, `File.Replace` (previous version kept as `.bak`). The lock is an exclusive OS file handle on a sidecar
file, so it works across processes and sessions and cannot go stale. A failed print gives the numbers back unless the caller chooses to burn them.
A label gets a `LabelDocument.Id` only when a remembering counter needs one; saving a copy under a new name shares the series unless `NewId()` is called.

### Computed variables: a tiny grammar, not a scripting language
Text and number literals, names, `&`, and a fixed list of 16 functions. No loops, assignment, reflection or I/O. Limits: 1000 characters, 16 levels,
200 nodes, 10000 characters of output (checked before building, including `replace` and `padleft`). Errors carry a message key and a position.
A name nothing supplies is empty plus a warning (like a missing placeholder), not a failure, so a column that only exists at merge time does not
block design-time validation.

### Prompts
The core returns a `PromptSet` (what to ask) and validates answers into the data dictionary; it never shows UI. User regular expressions run with
the non-backtracking engine, a 200 ms limit and a 200 character cap; unsupported constructs make the pattern invalid. Answers are cleaned
(line breaks to spaces, control characters dropped, trimmed) before the checks.

### Merge and MCP
`MergeBatch` resolves every kind per record: one counter value per record (a skipped record leaves a gap that counts as used), dates read once at
batch start unless asked per label, prompt answers shared by all records, variable warnings reported once. MCP `set_variable` takes `kind` and a
`definition` object validated as a whole before the label changes. MCP previews resolve variables (counters read the store but never reserve);
`print_label` resolves dates/computed values at send time, refuses remembering counters (it cannot record the numbers) and requires `answers` for prompts.

## GS1 (Rotula.Core/Gs1)

Details and the exact ZPL are in [gs1.md](gs1.md); the decisions behind them:

* **GS1-128 is Code 128 in mode Ean (`^BC` mode D), not a new symbology or a new flag.** Mode D already starts with FNC1 and prints the
  interpretation line with parentheses (guide p. 95), so a second switch would be ambiguous on import. The data is the element string with
  parentheses; Rotula inserts `>8` after each variable-length element that is not the last (the guide's own sample, p. 104). Mode D with data that
  does not start with `(` keeps its old meaning, so labels made before GS1 support print the same.
* **GS1 DataMatrix writes `#` as the escape character (`^BX` parameter g).** The tilde is a ZPL control prefix and the underscore is the `^FH`
  indicator Rotula uses (hex runs before escape processing, so `_1` would be read as hex). `#` is not in the GS1 character set, so values
  never need it doubled. A file from another program with the default tilde escape is still read.
* **No GS1 QR with FNC1 and no GS1 Aztec**: the guide has no FNC1 switch for `^BQ` or `^BO`. GS1 QR is a Digital Link URI in a plain QR Code
  (option DigitalLink checks it). The preview encoder can draw FNC1 QR Codes (tested, `]Q3`) but nothing in the model asks for one.
* **ITF-14 bearer bars are documented, not modelled.** `^B2` has no parameter for them and a `^GB` frame would come back as a separate box
  when the ZPL is imported, which would break the round trip.
* **Dictionary relations**: `ex=` is an error, `req=` is a warning, because the dictionary says associations are judged over all barcodes of the
  item and labels split them over symbols. Repeated identifiers are an error when the values differ.
* **The syntax tests are C# ports of the reference linters** (Apache-2.0 notice kept). `gcppos1/2` run in part (shape only, no registry
  lookup) and the two coupon tests are not run; a test fails when a new dictionary release names a test that is neither run nor listed.
  The century of a two-digit year uses an explicit reference year, never the clock inside the test.
* **GS1 options travel in `ExportExtraOptions`** (`DataMatrixGs1`, `DigitalLink`), so the .rotula file needed no change.


### Counters: no store is an error when printing, and the caller can own the confirmation
A counter that remembers its value but has no counter store would silently start again at its first value, so `VariableJob.Begin` (and therefore
`MergeBatch`) refuses with `Variable.Counter.NoStore`; a preview only warns. `MergeBatch` confirms the numbers it reserved when enumeration ends,
which means "used when generated", the safe side; a caller that wants "used when sent" passes its own `VariableJob` in `MergeOptions.Job` and calls
`Complete(lastSentRecordNumber)` itself. Store failures that are not I/O (settings changed between look and reserve, position exhausted) are
`CounterStoreException`s with message keys, never bare `InvalidOperationException`s. A read-only look (previews, the MCP server) creates no folder
and no lock when `counters.json` does not exist.

### The printer-clock codes and ^SO parameter order are not confirmed on a printer
The `^FC` code table and the `^SO` order (clock set, months, days, years, hours, minutes, seconds) come from the ZPL II Programming Guide as
remembered by the author; no excerpt is in the repository and no printer with a clock was used. `docs/variables.md` says so. Confirm on hardware
before the first release that advertises the printer-clock source.


## Text fit (Rotula.Core/Text)

### Text fit is Rotula-only metadata; the ZPL carries computed values
A wrapped text block can shrink its font (between a smallest and a largest size) or grow its lines (up to a cap). `TextFitOptions` lives on
`TextBlockOptions.Fit`, is saved in the .rotula file (format version 9, only when a block uses it, so every other file keeps its old version and an
older build refuses a version 9 file instead of printing the block cut off), and is decided by one function, `TextFitResolver`, called by the ZPL
generator (and so by every record of a data merge, each record gets its own size), the Windows-font picture, the canvas preview and the overflow
warning of the text panel. The printer never sees the mode: it receives plain `^A` and `^FB` values, so importing that ZPL gives an ordinary block
with the computed height, width and lines, and the fit mode is not restored. That loss is by design (the printer-side text has no such concept).

### Sizes are points; measuring reuses the two existing measurers
The limits are stored in points for printer and Windows fonts alike (the size box already shows points for both). A printer font's height is stored
in dots, a Windows font's in points; keeping the limits in points means a font switch or a 203/300 dpi conversion never rescales them. A printer
font is measured with `TextFitEstimator` (the estimate behind the overflow warning, now with a candidate-size overload), a Windows font with
`ITextRasterizer.Measure` (the layout the preview and the printed picture use). No third measurer exists. The search is a linear scan from the
largest size down (whole dots; half points for a Windows font; at most 200 candidates): bitmap fonts scale in whole cell multiples and word wrap is
not monotonic, so a binary search could miss the largest size that fits. An explicit font width follows the height, as the resize handles do.

### Grow never shrinks the block, and nothing moves
Grow gives the block as many lines as the text needs, between the block's own line count and the cap. Elements below do not move (the designer
decides the layout); at the cap the normal overflow warning stays. "Do not warn" silences that warning in any mode. A text whose content the printer
produces (`^SN`, `^SF`, clock codes) is not fitted, because its real length is unknown when the ZPL is made. Text boxes (`^TB`) are not fitted.

### Per-object printing: conditions, "do not print" and lock
Every object has an optional `printCondition` (field, operator, value), a `notPrintable` flag and a design-time `locked` flag, stored in the .rotula
file from format version 10 (stamped only when one is used, so old builds refuse such a file instead of printing a note or ignoring a rule).
The condition reads the field through `VariableResolver.TryLookup`, the same lookup `{{field}}` uses (record data first, then the declared sample),
on the data of that record after variables are resolved, so computed, date and counter values work. Comparisons ignore case and surrounding
spaces; a missing field counts as empty and warns; a rule with no field yet does not hide the object. A switched-off object is skipped before it is
emitted or validated, so a barcode invalid only on a record where it is hidden does not stop that record. The renderer leaves such objects out of
prints and PNG/JPEG/PDF; only the design canvas draws them dimmed (`includeNonPrinting`), and the Preview window shows the printed result. Lock is
enforced in `EditorCommands` (move, nudge, resize, rotate, align, distribute, delete, cut) and never touches ZPL; property edits stay allowed.
Condition fields are listed as merge placeholders so they can be mapped to columns. ZPL import never creates conditions or locks.

## Print station (Rotula.Files/Station, StationViewModel)

### One print path, a locked screen on top
The station is a separate window over the same services as the designer: it builds the printer target and profile through
`PrinterSettingsViewModel`, asks the label's questions through `PromptSet`, and prints through `IPrintService` with the status check on. So a
label prints the same from the station as from the Print dialog, and nothing in the print path knows about stations.

### The configuration is strict and the PIN is only a hash
`station.json` refuses unknown settings (`UnmappedMemberHandling.Disallow`), so a misspelt `maxCopies` is an error with its line, not a silently
unlimited station. The exit PIN is stored as `pbkdf2-sha256$<iterations>$<salt>$<hash>` (600,000 iterations, 16-byte salt) and checked with
`CryptographicOperations.FixedTimeEquals`, from the framework only. Five wrong PINs lock the box for 30 seconds. The PIN keeps an operator from
leaving by accident; it is not a security boundary, because anyone who can edit `station.json` can remove it, and Windows keys cannot be blocked.

### Scanners are read by their terminator
Keyboard-wedge scanners end a code with Enter or Tab, which `ScanSequencer` turns into "stay", "next field", "focus Print" or "print". Timing
heuristics (telling a scan from typing by speed) were left out: they misfire on slow PCs and remote sessions, and the terminator already works for
both people and scanners.
