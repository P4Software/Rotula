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
- **Never connects by itself**: opening a label that refers to a connection only shows `This label uses the saved connection "X"` with the query text
  (read-only) and a "Connect and reload data" button. Even reading the list of saved connections waits until the Data window is shown. The query text from a
  file is untrusted: the guard (`SqlGuard`) checks it live and again in the engine before anything is sent.
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

**Open action (owner / legal):** Rotula's own licence is MIT and does not bind end users to terms "at least as protective" as Microsoft's.
Before the next release, add a short notice to the installer licence page and to the README that names the component, forbids reverse
engineering it and disclaims Microsoft's liability, or take legal advice on whether the MIT text plus the notice in
`THIRD-PARTY-NOTICES.md` is enough. Until then this decision is accepted but not fully discharged.

### Accepted, not yet shipped (placeholders; the pack work finishes the notices)
Each of these ships only as an optional provider pack with its own licence notices file, never inside the core executable.
- **Oracle (ODP.NET, `Oracle.ManagedDataAccess.Core`)**, "Oracle Free Distribution, Hosting, and Use Terms and Conditions". Conditions: distribute
  unmodified; include a copy of the licence with every distribution; do not charge end users an additional fee for the distribution or use of the
  component (distributing it inside a free product is allowed); keep Oracle notices; comply with export and sanctions law; no reverse engineering or
  decompiling. The "information collection" clause concerns installers and auto-update, which a NuGet library does not have; verify with a network
  trace when the pack is built. Oracle names .NET 8 and netstandard2.1 builds only; test on .NET 10 before release.
- **Firebird (`FirebirdSql.Data.FirebirdClient`)**, Initial Developer's Public License 1.0 (file-level copyleft). Conditions: keep the licence and
  notices, do not modify its files, and make its source available (link to the upstream repository in the notices).
- **Azure Cosmos DB (`Microsoft.Azure.Cosmos`)**: MIT, but the package ships Microsoft Visual C++ runtime files (`vcruntime140.dll`,
  `vcruntime140_1.dll`, `msvcp140.dll`, `Cosmos.CRTCompat.dll`) under Microsoft's redistribution terms, plus the native `Microsoft.Azure.Cosmos.ServiceInterop.dll`.
  Read and record the VC++ redistribution conditions when the pack is built. Use read-only keys.
- **Microsoft Entra sign-in for the SQL Server family (`Microsoft.Data.SqlClient.Extensions.Azure`)**: MIT packages (Azure.Identity, Azure.Core,
  Microsoft.Identity.Client, Microsoft.Identity.Client.Broker) plus `Microsoft.Identity.Client.NativeInterop` (native `msalruntime.dll`) under
  Microsoft Software License Terms. A keyword search of those terms found no explicit redistribution clause; read them in full when the pack is
  built and record the conditions here.

Not bundled, ever: SAP HANA, Teradata, IBM Db2 and Informix drivers. The user installs their own ODBC driver; Rotula offers the preset and the
dialect hint.
