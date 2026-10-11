# Releasing Rotula

Releases are built and signed on a P4 Software developer machine, with the company's Azure login. GitHub Actions does not
sign and does not publish (it holds no signing secrets by design); on a version tag it only re-runs the tests, the coverage
gate and the hygiene check, and keeps an unsigned portable zip as a workflow artifact for inspection.

## One-time set-up on the build machine

- .NET 10 SDK, Inno Setup 6 (`winget install JRSoftware.InnoSetup --scope user`), the Azure CLI, and the Microsoft `sign`
  tool (`dotnet tool install --global sign --prerelease`).
- For publishing from the script: the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`).

## Cutting a release

1. **Bump the version** in `Directory.Build.props` (`<Version>`). It is the single source of truth.
2. **Update `CHANGELOG.md`**: move the Unreleased entries under a `## [x.y.z] - date` heading and fix the compare links at the
   bottom. The release notes are taken from that section.
3. Merge to the main branch, make sure CI is green, then create and push the tag `vX.Y.Z` on that commit.
4. Run `az login` (the signing uses that session).
5. Double-click `packaging\installer\build-installer.cmd`. It publishes the app and the MCP server (`Rotula.Mcp.exe`), writes the
   combined licence text, signs both programs, builds the installer, signs it, verifies all three signatures, builds the
   portable zip from the signed programs, writes the winget manifests and `SHA256SUMS.txt`. Everything is in `artifacts\`
   (git-ignored). If you are not logged in, the files are still built but labelled `UNSIGNED` and must not be published.
   Set `ROTULA_NOPAUSE=1` to skip the final "press Enter" (automation only).
6. Check the output (see "Verifying" below).
7. Publish. Either set `"publishToGitHub": true` in `packaging\installer\build-settings.json` (created next to the script on
   first run) and build again, or run it by hand:

   ```
   gh release create vX.Y.Z --repo P4Software/Rotula --title "Rotula X.Y.Z" --verify-tag --notes-file <notes> ^
     artifacts\Rotula-X.Y.Z-win-x64-setup.exe artifacts\Rotula-X.Y.Z-win-x64-portable.zip ^
     artifacts\LICENSE-THIRD-PARTY.txt artifacts\SHA256SUMS.txt artifacts\Rotula-X.Y.Z-winget-manifests.zip
   ```

   The script never replaces files of a release that already exists; it adds only the missing ones and says so.
8. **Winget**: copy `artifacts\winget\manifests\p\P4Software\Rotula\X.Y.Z\` into a fork of `microsoft/winget-pkgs`, check it
   with `winget validate`, and open a pull request. The manifest already holds the real installer hash and download URL, so
   submit it only after the GitHub Release is public.

## Verifying a signature

```powershell
Get-AuthenticodeSignature .\artifacts\Rotula-X.Y.Z-win-x64-setup.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

`Status` must be `Valid`, the signer subject must contain `Barrdega Sistemas NA LLC`, and a timestamp certificate must be
present. Do the same for `Rotula.App.exe` and `Rotula.Mcp.exe` inside the portable zip. The build script performs these checks itself and never
trusts the exit code of the signing tool. Compare downloads with `SHA256SUMS.txt` (`Get-FileHash`).

## What is in a release

- `Rotula-X.Y.Z-win-x64-setup.exe`: per-user installer. Installs `Rotula.App.exe`, `Rotula.Mcp.exe`, `LICENSE`,
  `LICENSE-THIRD-PARTY.txt`, `THIRD-PARTY-NOTICES.md`, `README.md`, `docs\mcp.md` and an empty `packs` folder (with a one-line
  `README.txt`) beside the program. Uninstalling removes them, and the MCP settings file created next to the exe on first run.
- `Rotula-X.Y.Z-win-x64-portable.zip`: the same files, nothing installed.
- `LICENSE-THIRD-PARTY.txt`: also a separate release file, because the winget manifest links to it as the agreement.
- `SHA256SUMS.txt` and `Rotula-X.Y.Z-winget-manifests.zip`.

## The licence page and the SQL Server networking component

The installer's licence page (which the user must accept) is not the plain MIT file. `build-installer.ps1` generates
`LICENSE-THIRD-PARTY.txt` at build time: Rotula's MIT licence, then the licence text of every non-permissive component that
ships, copied unchanged from the NuGet package, then a sentence saying that installing means agreeing to those terms for those
components, then a pointer to `THIRD-PARTY-NOTICES.md` for the permissive ones. Today the only such component is
`Microsoft.Data.SqlClient.SNI.runtime` (see "SQL Server networking component (SNI)" in `decisions.md`). The version and file
location come from the restore output, and **the build fails with a plain message if that licence file cannot be found**:
never ship without it. When a new non-permissive component is added, add its package id to `New-CombinedLicense` in the script.

> **Needs review by a lawyer before wider distribution.** The wording of the acceptance sentence and the combination of the MIT
> text with the component licence were written by the developers, not by a legal professional. This page does not claim that
> it satisfies the licence terms of the component (for example the requirement to bind end users to terms at least as protective
> as Microsoft's, or the indemnity). Have the exact text reviewed before the project is promoted beyond early adopters.
