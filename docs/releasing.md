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
5. Double-click `packaging\installer\build-installer.cmd`. It publishes the app, signs `Rotula.App.exe`, builds the installer,
   signs it, verifies both signatures, builds the portable zip from the signed exe, writes the winget manifests and
   `SHA256SUMS.txt`. Everything is in `artifacts\` (git-ignored). If you are not logged in, the files are still built but
   labelled `UNSIGNED` and must not be published.
6. Check the output (see "Verifying" below).
7. Publish. Either set `"publishToGitHub": true` in `packaging\installer\build-settings.json` (created next to the script on
   first run) and build again, or run it by hand:

   ```
   gh release create vX.Y.Z --repo P4Software/Rotula --title "Rotula X.Y.Z" --verify-tag --notes-file <notes> ^
     artifacts\Rotula-X.Y.Z-win-x64-setup.exe artifacts\Rotula-X.Y.Z-win-x64-portable.zip ^
     artifacts\SHA256SUMS.txt artifacts\Rotula-X.Y.Z-winget-manifests.zip
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
present. Do the same for `Rotula.App.exe` inside the portable zip. The build script performs these checks itself and never
trusts the exit code of the signing tool. Compare downloads with `SHA256SUMS.txt` (`Get-FileHash`).
