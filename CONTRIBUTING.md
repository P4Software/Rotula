# Contributing to Rotula

Thanks for your interest. Rotula is an MIT licensed, offline-first label designer for Windows (.NET 10, WPF). Bug reports,
ZPL samples that import badly, translations and pull requests are all welcome.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems go through the private route
in [SECURITY.md](SECURITY.md), not a public issue.

## Ground rules

These are product rules; changes that break them will not be merged.

- **Offline-first**: no network calls, no telemetry, no update checks, no analytics. Printing to the printer address the user
  entered is the only network traffic.
- **Light theme only.**
- **Spanish and English** through `.resx` resources (`src/Rotula.App/Resources/Strings.en.resx` and `Strings.es.resx`). Every new
  user-visible string needs both. Core returns message keys, not text.
- **Permissive dependencies only** (MIT, Apache-2.0, BSD and similar). Add any new package to
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) in the same pull request. Discuss new dependencies in an issue first.
- **Plain-language messages**: say what happened and what the user can do next.
- **Do not name printer or software vendors in the product name or UI**; "ZPL compatible" wording is fine.

## Getting started

You need Windows and the .NET 10 SDK.

```
git clone https://github.com/P4Software/Rotula.git
cd Rotula
dotnet build Rotula.sln -c Release
dotnet test  Rotula.sln -c Release
```

| Project | What lives there |
|---|---|
| `src/Rotula.Core` | Label model, ZPL generator and parser, barcode validation, data merge. No UI, warnings are errors. |
| `src/Rotula.Rendering` | SkiaSharp preview renderer, barcode drawing, PNG and PDF export |
| `src/Rotula.Printing` | Windows spooler and TCP 9100 printing |
| `src/Rotula.App` | WPF application |
| `tests/Rotula.Tests` | xUnit tests; `tests/fixtures` holds golden ZPL files |

## Code style

- Document code in English: XML docs on every public type and member, and a short comment on the *why* of anything
  non-obvious. Missing XML docs are build warnings.
- File-scoped namespaces, nullable enabled, 4-space indentation (2 for XAML, project files, JSON, resx). The `.editorconfig`
  has the rest.
- Keep Core free of UI and platform code.

## Tests and coverage

- Every behavior change comes with a test. For ZPL output add or update a golden file; for the parser add a round-trip case;
  for barcodes add a validation case, and for drawing code decode the rendered image where practical.
- CI fails if line coverage of `Rotula.Core` falls below **80%**. Check locally:

  ```
  dotnet test Rotula.sln -c Release --collect:"XPlat Code Coverage" --results-directory coverage/raw
  pwsh scripts/check-coverage.ps1 -Report <path to coverage.cobertura.xml> -Package Rotula.Core -Threshold 80
  ```

## Commit and pull request rules

- Small, focused pull requests against `main`. Describe what changed and why; fill in the pull request template.
- Commit messages: a short imperative summary (`feat(core): ...`, `fix(rendering): ...`, `docs: ...`), then a body if useful.
- The repository hygiene check runs in CI (`scripts/check-hygiene.sh`) and fails when the tree contains tooling folders or
  files that do not belong in the project, or when a commit message has an automated co-author trailer or a
  "generated with" footer. Run it before you push: `bash scripts/check-hygiene.sh origin/main..HEAD`.
- Never commit secrets, signing keys or personal data. Sample files must be free of real customer data.

## Label stock library

Rotula ships a catalogue of label stocks (part numbers and label sizes). Corrections and new entries are welcome as pull
requests that edit `src/Rotula.Core/Stocks/stocks.json`. Every entry needs a public source (an `https` page in
`sourceUrl`, or a source id declared in the file's metadata), the size in millimetres and inches, and no prices or other
data than physical facts. CI runs the catalogue validator on the file (`dotnet test --filter "FullyQualifiedName~Stock"`
runs it locally). The format, the rules, how to rebuild the file and how users add their own stocks are in
[docs/stock-catalog.md](docs/stock-catalog.md). Brand and product names belong to their owners; Rotula is not affiliated.

## Reporting bugs and asking for features

Use the issue templates. For ZPL import problems, attach the smallest ZPL that shows the issue (remove private data) and say
which printer it came from. For preview differences, attach a photo or scan of the printed label.

## Releases

Maintainers tag `vX.Y.Z` on `main`; the release workflow builds, tests and publishes the portable zip, the installer and the
winget manifests. Update `CHANGELOG.md` before tagging.
