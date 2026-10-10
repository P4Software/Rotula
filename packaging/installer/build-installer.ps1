<#
  Builds the signed Rotula release. Double-click build-installer.cmd (it runs this script).

  Steps: read the version, publish the app, sign it, build the installer, sign the installer,
  verify both signatures, build the portable zip from the SIGNED program, write the winget
  manifests and the checksums. Everything lands in the git-ignored "artifacts" folder.

  Signing uses Azure Artifact Signing (account p4software) through your Azure login (az login).
  If signing is not possible the files are still built and clearly marked UNSIGNED, and they are
  never published.

  Nothing is uploaded unless build-settings.json (created next to this script on first run)
  says "publishToGitHub": true, and then only after both signatures were verified.

  Automation only: set ROTULA_NOPAUSE=1 to skip the final "press Enter" pause.
  Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
#>
$ErrorActionPreference = 'Stop'
$repo      = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifacts = Join-Path $repo 'artifacts'
$publish   = Join-Path $repo 'publish\win-x64'   # git-ignored
$stage     = Join-Path $artifacts 'stage'        # git-ignored (inside artifacts)
$settingsPath = Join-Path $PSScriptRoot 'build-settings.json'
$exitCode  = 0

# Signer subject we expect on every signature. The signing identity must not change.
$expectedSigner = 'Barrdega Sistemas NA LLC'

$starter = @'
// Rotula build settings. Lines starting with // are comments and are ignored.
// This file is read by build-installer.ps1 and is not part of the program.
{
  // Leave empty to use the version from Directory.Build.props (the single source of truth).
  "versionOverride": "",

  // Azure Artifact Signing (the company account). Change only if the signing setup changes.
  "signingEndpoint": "https://eus.codesigning.azure.net/",
  "signingAccount": "p4software",
  "signingCertificateProfile": "p4software",

  // GitHub repository that receives the release.
  "githubRepository": "P4Software/Rotula",

  // SAFE BY DEFAULT: false means the script only builds and signs.
  // Set to true to also create/update the GitHub Release v<version> after both signatures verified.
  // The tag v<version> must already exist on GitHub.
  "publishToGitHub": false
}
'@

function Read-Settings {
  if (-not (Test-Path -LiteralPath $settingsPath)) {
    Set-Content -LiteralPath $settingsPath -Value $starter -Encoding ASCII
    Write-Host "Created $settingsPath with the default settings (nothing is published unless you change it)."
  }
  # Strip whole-line // comments so plain JSON parsers (including Windows PowerShell 5.1) accept it.
  $raw = (Get-Content -LiteralPath $settingsPath) | Where-Object { $_ -notmatch '^\s*//' }
  try { return ($raw -join "`n") | ConvertFrom-Json }
  catch { throw "build-settings.json is not valid JSON. Fix it, or delete it to get a fresh copy. ($($_.Exception.Message))" }
}

function Get-SignExe {
  $cmd = Get-Command sign.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $fallback = Join-Path $env:USERPROFILE '.dotnet\tools\sign.exe'
  if (Test-Path -LiteralPath $fallback) { return $fallback }
  return $null
}

function Test-AzLogin {
  # True when an Azure CLI session exists. Signing uses that session.
  try {
    $az = Get-Command az -ErrorAction SilentlyContinue
    if (-not $az) { return $false }
    $null = & az account show --query user.name -o tsv 2>$null
    return ($LASTEXITCODE -eq 0)
  } catch { return $false }
}

function Test-Signed($file) {
  # Trust the file, not the sign tool's exit code: it can report success without signing.
  $s = Get-AuthenticodeSignature -LiteralPath $file
  return ($s.Status -eq 'Valid' -and $s.SignerCertificate -and
          $s.SignerCertificate.Subject -like "*$expectedSigner*" -and
          $null -ne $s.TimeStamperCertificate)
}

function Show-Signature($file) {
  $s = Get-AuthenticodeSignature -LiteralPath $file
  $ts = if ($s.TimeStamperCertificate) { "present ($($s.TimeStamperCertificate.Subject))" } else { 'MISSING' }
  $subj = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '(none)' }
  Write-Host "  $(Split-Path -Leaf $file)"
  Write-Host "    Status:    $($s.Status)"
  Write-Host "    Signer:    $subj"
  Write-Host "    Timestamp: $ts"
}

# Named SignFile on purpose: a function named Sign would call itself instead of the sign tool.
# Calls run one after another; parallel az/sign calls clash over the token cache.
function SignFile($file) {
  $signArgs = @(
    'code', 'artifact-signing', $file,
    '--artifact-signing-endpoint', $script:cfg.signingEndpoint,
    '--artifact-signing-account', $script:cfg.signingAccount,
    '--artifact-signing-certificate-profile', $script:cfg.signingCertificateProfile,
    '--azure-credential-type', 'azure-cli',
    '--description', 'Rotula',
    '--description-url', 'https://github.com/P4Software/Rotula',
    '-v', 'warning')
  # A transient Azure token error happens now and then; a short pause and another try fixes it.
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    & $script:signExe @signArgs | Out-Host
    if (Test-Signed $file) { return $true }
    if ($attempt -lt 3) {
      Write-Host "  Signing attempt $attempt did not produce a valid signature. Trying again in 5 seconds..." -ForegroundColor Yellow
      Start-Sleep -Seconds 5
    }
  }
  return $false
}

function Get-ReleaseNotes($version, $outFile) {
  $changelog = Join-Path $repo 'CHANGELOG.md'
  $lines = Get-Content -LiteralPath $changelog
  $start = $null
  for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^## \[$([regex]::Escape($version))\]") { $start = $i + 1; break } }
  if ($null -eq $start) { return $false }
  $end = $lines.Count
  for ($i = $start; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^(## \[|\[[^\]]+\]: )') { $end = $i; break } }
  Set-Content -LiteralPath $outFile -Value ($lines[$start..($end - 1)] -join "`n") -Encoding UTF8
  return $true
}

try {
  $cfg = Read-Settings
  $script:cfg = $cfg

  # --- Tools --------------------------------------------------------------------------------
  $iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
  if (-not $iscc) { throw "Inno Setup is not installed. Install it with: winget install JRSoftware.InnoSetup --scope user  (then double-click build-installer.cmd again)" }

  # --- Version ------------------------------------------------------------------------------
  if ($cfg.versionOverride) { $version = [string]$cfg.versionOverride }
  else {
    $props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
    $version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
  }
  $version = $version.Trim().TrimStart('v')
  if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "The version '$version' is not major.minor.patch (optionally with a -suffix). Fix <Version> in Directory.Build.props." }
  Write-Host "Building Rotula $version" -ForegroundColor Cyan

  # --- Signing availability -----------------------------------------------------------------
  $script:signExe = Get-SignExe
  $canSign = $false
  if (-not $script:signExe) { Write-Host "The 'sign' tool is not installed, so the files will be UNSIGNED. Install it with: dotnet tool install --global sign --prerelease" -ForegroundColor Yellow }
  elseif (-not (Test-AzLogin)) { Write-Host "You are not logged in to Azure, so the files will be UNSIGNED. Run 'az login' and build again to sign." -ForegroundColor Yellow }
  else { $canSign = $true }

  # --- Clean output -------------------------------------------------------------------------
  # Start from empty folders so a file from an older build can never end up in this release.
  foreach ($d in @($artifacts, $publish)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
  New-Item -ItemType Directory -Force -Path $artifacts, $stage | Out-Null

  # --- 1. Publish ---------------------------------------------------------------------------
  Write-Host '1/6 Publishing the app (this takes a minute)...'
  & dotnet publish (Join-Path $repo 'src\Rotula.App\Rotula.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    "-p:Version=$version" -o $publish
  if ($LASTEXITCODE -ne 0) { throw 'The app could not be published. Read the messages above.' }

  Copy-Item -LiteralPath (Join-Path $publish 'Rotula.App.exe') -Destination $stage
  foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md') { Copy-Item -LiteralPath (Join-Path $repo $f) -Destination $stage }
  $exe = Join-Path $stage 'Rotula.App.exe'

  # --- 2. Sign the program ------------------------------------------------------------------
  $signed = $false
  Write-Host '2/6 Signing the program...'
  if ($canSign) { $signed = SignFile $exe } else { Write-Host '  Skipped (see the note above).' }
  if ($canSign -and -not $signed) { Write-Host '  Signing failed; the output will be UNSIGNED.' -ForegroundColor Yellow }

  # --- 3. Installer -------------------------------------------------------------------------
  Write-Host '3/6 Building the installer...'
  & $iscc "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'Rotula.iss')
  if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built. Read the messages above.' }
  $setup = Join-Path $artifacts "Rotula-$version-win-x64-setup.exe"
  if (-not (Test-Path -LiteralPath $setup)) { throw "The installer was not found at $setup after the build." }

  # --- 4. Sign the installer ----------------------------------------------------------------
  Write-Host '4/6 Signing the installer...'
  $setupSigned = $false
  if ($signed) { $setupSigned = SignFile $setup } else { Write-Host '  Skipped.' }

  # --- 5. Verify ----------------------------------------------------------------------------
  Write-Host '5/6 Checking the signatures...'
  Show-Signature $exe
  Show-Signature $setup
  $allSigned = ((Test-Signed $exe) -and (Test-Signed $setup))

  # --- 6. Zip, manifests, checksums ---------------------------------------------------------
  Write-Host '6/6 Packaging the portable zip, winget manifests and checksums...'
  $suffix = if ($allSigned) { '' } else { '-UNSIGNED' }
  $zip = Join-Path $artifacts "Rotula-$version-win-x64-portable$suffix.zip"
  # The zip is made from the stage folder, so it carries the SIGNED exe.
  Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force

  if ($allSigned) {
    $wingetOut = Join-Path $artifacts 'winget'
    & (Join-Path $repo 'scripts\New-WingetManifest.ps1') -Version $version -InstallerPath $setup -OutputDir $wingetOut -Repository $cfg.githubRepository
    Compress-Archive -Path (Join-Path $wingetOut '*') -DestinationPath (Join-Path $artifacts "Rotula-$version-winget-manifests.zip") -Force
  } else {
    # Rename so an unsigned installer can never be mistaken for the release file; no winget files for it.
    $unsignedSetup = Join-Path $artifacts "Rotula-$version-win-x64-setup-UNSIGNED.exe"
    Move-Item -LiteralPath $setup -Destination $unsignedSetup
    $setup = $unsignedSetup
    Set-Content -LiteralPath (Join-Path $artifacts 'UNSIGNED-DO-NOT-PUBLISH.txt') -Encoding ASCII -Value @(
      'These files are NOT signed. Do not publish them.',
      "Run 'az login', then double-click build-installer.cmd again to get signed files.")
  }

  Get-ChildItem -LiteralPath $artifacts -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name
  } | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ASCII

  Write-Host ''
  Get-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') | Out-Host

  if (-not $allSigned) {
    $exitCode = 2
    Write-Host ''
    Write-Host "Done, but the files are UNSIGNED (Windows will warn users) and will not be published." -ForegroundColor Yellow
    Write-Host "Next step: run 'az login', then double-click build-installer.cmd again. Files: $artifacts" -ForegroundColor Yellow
    if ($cfg.publishToGitHub) { Write-Host 'publishToGitHub is true, but nothing was published because the files are not signed.' -ForegroundColor Yellow }
  } else {
    Write-Host ''
    Write-Host "Done. Both files are signed by '$expectedSigner'. Output: $artifacts" -ForegroundColor Green

    # --- Optional publish -------------------------------------------------------------------
    if (-not $cfg.publishToGitHub) {
      Write-Host 'Nothing was published (publishToGitHub is false in build-settings.json). See docs/releasing.md for the next step.'
    } else {
      $tag = "v$version"
      $repoName = $cfg.githubRepository
      if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is not installed. Install it with: winget install GitHub.cli  (then run gh auth login).' }
      # The tag must already exist on GitHub: creating it here could tag a different commit than the one just built.
      $null = & gh api "repos/$repoName/git/ref/tags/$tag" 2>$null
      if ($LASTEXITCODE -ne 0) { throw "The tag $tag does not exist on GitHub yet. Tag the commit you built and push the tag, then build again." }

      $assets = @($setup, $zip, (Join-Path $artifacts 'SHA256SUMS.txt'), (Join-Path $artifacts "Rotula-$version-winget-manifests.zip"))
      $notes = Join-Path $artifacts 'release-notes.md'
      $haveNotes = Get-ReleaseNotes $version $notes
      $null = & gh release view $tag --repo $repoName 2>$null
      if ($LASTEXITCODE -ne 0) {
        $ghArgs = @('release', 'create', $tag, '--repo', $repoName, '--title', "Rotula $version", '--verify-tag')
        if ($version.Contains('-')) { $ghArgs += '--prerelease' }
        if ($haveNotes) { $ghArgs += @('--notes-file', $notes) } else { $ghArgs += '--generate-notes' }
        & gh @ghArgs @assets
        if ($LASTEXITCODE -ne 0) { throw 'Creating the GitHub Release failed. Read the messages above; nothing was overwritten.' }
        Write-Host "Published release $tag." -ForegroundColor Green
      } else {
        # Never replace files of a release that already exists: people may have downloaded or hashed them.
        $existing = @(& gh release view $tag --repo $repoName --json assets --jq '.assets[].name')
        $missing = @($assets | Where-Object { $existing -notcontains (Split-Path -Leaf $_) })
        $skipped = @($assets | Where-Object { $existing -contains (Split-Path -Leaf $_) })
        foreach ($s in $skipped) { Write-Host "Release $tag already has $(Split-Path -Leaf $s); it was NOT replaced." -ForegroundColor Yellow }
        if ($missing.Count -gt 0) {
          & gh release upload $tag --repo $repoName @missing
          if ($LASTEXITCODE -ne 0) { throw 'Uploading the missing files failed.' }
          Write-Host "Added $($missing.Count) missing file(s) to release $tag."
        }
      }
    }
  }
} catch {
  $exitCode = 1
  Write-Host ''
  Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
  Write-Host 'Nothing was published. Fix the problem above and double-click build-installer.cmd again.' -ForegroundColor Red
}
if (-not $env:ROTULA_NOPAUSE) { Read-Host 'Press Enter to close this window' }
exit $exitCode
