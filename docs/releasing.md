# Releasing Rotula

Releases are built and signed on a P4 Software developer machine, with the company's Azure login. GitHub Actions does not
sign and does not publish (it holds no signing secrets by design); on a version tag it only re-runs the tests, the coverage
gate and the hygiene check, and keeps an unsigned portable zip as a workflow artifact for inspection.

## One-time set-up on the build machine

- .NET 10 SDK, Inno Setup 6 (`winget install JRSoftware.InnoSetup --scope user`), the Azure CLI, and the Microsoft `sign`
  tool (`dotnet tool install --global sign --prerelease`). The WiX Toolset that builds the MSI needs no install: `dotnet build`
  restores `WixToolset.Sdk` from NuGet the first time.
- For the winget submission: `winget install Microsoft.WingetCreate` (optional, see step 8).
- For publishing from the script: the GitHub CLI (`winget install GitHub.cli`, then `gh auth login`).

## Cutting a release

1. **Bump the version** in `Directory.Build.props` (`<Version>`). It is the single source of truth.
2. **Update `CHANGELOG.md`**: move the Unreleased entries under a `## [x.y.z] - date` heading and fix the compare links at the
   bottom. The release notes are taken from that section.
3. Merge to the main branch, make sure CI is green, then create and push the tag `vX.Y.Z` on that commit.
4. Run `az login` (the signing uses that session).
5. Double-click `packaging\installer\build-installer.cmd`. It publishes the app, the MCP server (`Rotula.Mcp.exe`) and the print engine
   (`rotula.exe`), writes the combined licence text, signs the three programs, builds the per-user installer (Inno Setup) and the machine-wide MSI
   (`packaging\msi`, WiX Toolset) from the signed programs, signs both, verifies all four signatures, builds the
   portable zip from the signed programs, writes the winget manifests and `SHA256SUMS.txt`. Everything is in `artifacts\`
   (git-ignored). If you are not logged in, the files are still built but labelled `UNSIGNED` and must not be published.
   Set `ROTULA_NOPAUSE=1` to skip the final "press Enter" (automation only).
6. Check the output (see "Verifying" below).
7. Publish. Either set `"publishToGitHub": true` in `packaging\installer\build-settings.json` (created next to the script on
   first run) and build again, or run it by hand:

   ```
   gh release create vX.Y.Z --repo P4Software/Rotula --title "Rotula X.Y.Z" --verify-tag --notes-file <notes> ^
     artifacts\Rotula-X.Y.Z-win-x64-setup.exe artifacts\Rotula-X.Y.Z-win-x64.msi artifacts\Rotula-X.Y.Z-win-x64-portable.zip ^
     artifacts\LICENSE-THIRD-PARTY.txt artifacts\SHA256SUMS.txt artifacts\Rotula-X.Y.Z-winget-manifests.zip
   ```

   The script never replaces files of a release that already exists; it adds only the missing ones and says so.
8. **Winget** (only after the GitHub Release is public: the manifests hold the real download URLs and hashes). The set in
   `artifacts\winget\manifests\p\P4Software\Rotula\X.Y.Z\` has four files: version, installer (the Inno exe for
   `--scope user` and the MSI for `--scope machine`), en-US default locale and es-MX locale.
   1. Check it: `winget validate --manifest artifacts\winget\manifests\p\P4Software\Rotula\X.Y.Z`.
   2. Try it in Windows Sandbox or a test VM: `winget settings --enable LocalManifestFiles` (once, elevated), then
      `winget install --manifest <that folder>` and `winget install --manifest <that folder> --scope machine`, and uninstall both.
   3. Submit it, either with wingetcreate, which forks `microsoft/winget-pkgs` and opens the pull request for you:
      `wingetcreate submit --token <GitHub token with public_repo> artifacts\winget\manifests\p\P4Software\Rotula\X.Y.Z`
      or by hand: fork `microsoft/winget-pkgs`, copy the folder to `manifests/p/P4Software/Rotula/X.Y.Z/` on a new branch, and
      open a pull request titled `New version: P4Software.Rotula version X.Y.Z` (the first one: `New package: ...`).
   4. Follow the pull request: the winget bots validate, install and scan the package and label the pull request; answer any
      `Needs-Author-Feedback` label. A moderator merges it, and the version reaches `winget search` within a few hours.
   Never edit the hashes by hand; if a release file changes, build again (and use a new version number).

## Verifying a signature

```powershell
Get-AuthenticodeSignature .\artifacts\Rotula-X.Y.Z-win-x64-setup.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

`Status` must be `Valid`, the signer subject must contain `Barrdega Sistemas NA LLC`, and a timestamp certificate must be
present. Do the same for the MSI, and for `Rotula.App.exe`, `Rotula.Mcp.exe` and `rotula.exe` inside the portable zip. The build script performs these checks itself and never
trusts the exit code of the signing tool. Compare downloads with `SHA256SUMS.txt` (`Get-FileHash`).

## What is in a release

- `Rotula-X.Y.Z-win-x64-setup.exe`: per-user installer. Installs `Rotula.App.exe`, `Rotula.Mcp.exe`, `rotula.exe`, `LICENSE`,
  `LICENSE-THIRD-PARTY.txt`, `THIRD-PARTY-NOTICES.md`, `README.md`, `docs\mcp.md`, `docs\print-engine.md` and an empty `packs` folder (with a one-line
  `README.txt`) beside the program. Uninstalling removes them, and the MCP settings file created next to the exe on first run.
- `Rotula-X.Y.Z-win-x64.msi`: machine-wide Windows Installer package for administrators (Program Files, every user; Intune,
  Group Policy, Configuration Manager). The same files as the setup exe. See [deployment.md](deployment.md).
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

## Logo and icons

The logo, the application and document icons, the installer images and the GitHub social preview are drawn from code by
`tools/brand` and the results are committed, so a normal build never needs the generator. To change the artwork, edit the
drawing in `tools/brand`, double-click `tools\brand\Regenerate-Brand.cmd` and commit the files it rewrites (running it twice
gives identical files). The generated files are:

- `src/Rotula.App/Assets/rotula.ico` (application), `rotula-document.ico` (`.rotula` files) and
  `src/Rotula.Mcp/Assets/rotula-mcp.ico` (the MCP server uses the same mark as the app);
- `src/Rotula.App/Resources/Brand.xaml` (the same mark as WPF vector images for the title bars, About and start screen);
- `docs/images/logo.svg`, `logo-wordmark.svg`, `logo-wordmark-dark.svg`, `logo-512.png`, `social-preview.png`
  (upload the last one under the repository's Settings, Social preview);
- `packaging/installer/assets/*.bmp` (installer images for 100 to 250 percent scaling).

Windows keeps its own icon cache. After installing a build with new icons, an old icon may still show in Explorer, the taskbar
or the Start menu until the cache is rebuilt: restart Explorer, or sign out and in again.
