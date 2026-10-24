## What changed and why

<!-- Short description. Link the issue it closes, e.g. "Closes #12". -->

## How it was tested

<!-- Tests added or updated, and anything you checked by hand (for printing, name the printer and connection). -->

## Checklist

- [ ] `dotnet build Rotula.sln -c Release` and `dotnet test Rotula.sln -c Release` pass
- [ ] New behavior has tests; `Rotula.Core` stays at or above 80% line coverage
- [ ] Public members have XML docs and non-obvious logic has a "why" comment (in English)
- [ ] New user-visible text exists in all four languages: `Strings.en.resx`, `Strings.es.resx`, `Strings.pt-BR.resx` and `Strings.fr-CA.resx`
- [ ] No network calls or telemetry added; light theme only
- [ ] New dependencies are permissively licensed and listed in `THIRD-PARTY-NOTICES.md`
- [ ] `CHANGELOG.md` updated under "Unreleased" when users would notice the change
- [ ] `bash scripts/check-hygiene.sh origin/main..HEAD` passes
