<#
  Builds the signed Rotula release. Double-click build-installer.cmd (it runs this script).

  Steps: read the version, publish the app and the MCP server, write the combined licence text,
  sign both programs, build the per-user installer (Inno Setup) and the machine-wide MSI (WiX Toolset) from the SIGNED
  programs, sign both, verify all four signatures, build the portable zip from the SIGNED programs, write the winget
  manifests and the checksums. Everything lands in the git-ignored "artifacts" folder.

  Signing uses Azure Artifact Signing (account p4software) through your Azure login (az login).
  If signing is not possible the files are still built and clearly marked UNSIGNED, and they are
  never published.

  Nothing is uploaded unless build-settings.json (created next to this script on first run)
  says "publishToGitHub": true, and then only after all signatures were verified.

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

function New-ZipFromFolder($folder, $zipPath) {
  # Windows PowerShell 5.1's Compress-Archive writes backslashes in entry names, which unzip tools on other systems
  # (and some on Windows) show as odd file names. The zip format wants forward slashes, so build it by hand.
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
  $root = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\')
  $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
  try {
    # Sorted so the same input always gives the same entry order.
    foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName)) {
      $entry = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
      [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
  } finally { $zip.Dispose() }
}

function Find-ThirdPartyLicense($packageId) {
  # Finds LICENSE.txt of a NuGet package that is really part of the build: the version comes from the restore output
  # (obj\project.assets.json) of the shipped projects, the folder from the package folders recorded there. Nothing is
  # guessed from the global cache, so an old cached version can never be picked up by mistake.
  $versions = @{}
  foreach ($proj in 'Rotula.App', 'Rotula.Mcp') {
    $assetsPath = Join-Path $repo "src\$proj\obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "The restore output for $proj was not found ($assetsPath). Run the build again." }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($lib in $assets.libraries.PSObject.Properties.Name) {
      if ($lib -match ('^' + [regex]::Escape($packageId) + '/(.+)$')) { $versions[$Matches[1]] = @($assets.packageFolders.PSObject.Properties.Name) }
    }
  }
  if ($versions.Count -eq 0) { throw "The package $packageId is not part of this build any more. Remove it from the third-party list in build-installer.ps1 and from THIRD-PARTY-NOTICES.md." }
  if ($versions.Count -gt 1) { throw "The programs use different versions of $packageId ($($versions.Keys -join ', ')). Make them the same version." }
  $ver = @($versions.Keys)[0]
  foreach ($folder in $versions[$ver]) {
    $candidate = Join-Path $folder (Join-Path $packageId.ToLowerInvariant() (Join-Path $ver 'LICENSE.txt'))
    if (Test-Path -LiteralPath $candidate) { return [pscustomobject]@{ Package = $packageId; Version = $ver; Path = $candidate } }
  }
  throw "The licence text of $packageId $ver was not found in the NuGet package cache. Run 'dotnet restore' (or 'dotnet nuget locals global-packages -l' to see the cache) and build again. Rotula must never ship without that licence text."
}

function New-CombinedLicense($outFile) {
  # Writes the text the installer shows on its licence page (and ships as LICENSE-THIRD-PARTY.txt):
  # Rotula's MIT licence, then the verbatim licence of every non-permissive component that ships, then a pointer to the
  # notices for the permissive ones. The component licences are copied unchanged (only line endings are unified).
  # To add another non-permissive component, add its NuGet id here.
  $nonPermissive = @('Microsoft.Data.SqlClient.SNI.runtime')
  $crlf = "`r`n"
  $norm = { param($t) (($t -replace "`r`n", "`n") -replace "`r", "`n").TrimEnd() -replace "`n", $crlf }
  $rule = ('=' * 78)
  $out = New-Object System.Text.StringBuilder
  [void]$out.Append("ROTULA LICENCE AND THIRD-PARTY TERMS$crlf$rule$crlf$crlf")
  [void]$out.Append("PART 1 - ROTULA$crlf$crlf")
  [void]$out.Append((& $norm (Get-Content -LiteralPath (Join-Path $repo 'LICENSE') -Raw)) + $crlf + $crlf)
  [void]$out.Append("$rule$crlf$crlf")
  [void]$out.Append("PART 2 - THIRD-PARTY COMPONENTS WITH THEIR OWN LICENCE TERMS$crlf$crlf")
  [void]$out.Append("Rotula includes the components listed below. They are NOT covered by the MIT licence above: each one$crlf")
  [void]$out.Append("comes with its own licence terms, reproduced in full below. By installing or using Rotula you agree to$crlf")
  [void]$out.Append("be bound by those terms for those components. Where those terms differ from the MIT licence for one of$crlf")
  [void]$out.Append("those components, those terms apply to that component.$crlf$crlf")
  $licenses = @()
  foreach ($id in $nonPermissive) {
    $found = Find-ThirdPartyLicense $id
    $licenses += [pscustomobject]@{ Info = $found; Text = (& $norm (Get-Content -LiteralPath $found.Path -Raw)) }
  }
  foreach ($l in $licenses) {
    [void]$out.Append("$rule$crlf")
    [void]$out.Append("Component: $($l.Info.Package) $($l.Info.Version)$crlf")
    [void]$out.Append("Licence text (copied unchanged from the component's own LICENSE.txt):$crlf$rule$crlf$crlf")
    [void]$out.Append($l.Text + $crlf + $crlf)
  }
  [void]$out.Append("$rule$crlf$crlf")
  [void]$out.Append("PART 3 - OTHER THIRD-PARTY COMPONENTS$crlf$crlf")
  [void]$out.Append("All other third-party components in Rotula use permissive open-source licences (MIT, Apache 2.0, BSD and$crlf")
  [void]$out.Append("similar). They are listed with their licences in THIRD-PARTY-NOTICES.md, which is installed next to this$crlf")
  [void]$out.Append("file and is also at https://github.com/P4Software/Rotula/blob/main/THIRD-PARTY-NOTICES.md$crlf")
  $text = $out.ToString()
  if ($text -match '[^\x00-\x7F]') { throw 'The combined licence text contains characters that are not plain ASCII. Check LICENSE and the component licences.' }
  # Last line of defence: the verbatim text must really be inside the file we are about to ship.
  foreach ($l in $licenses) { if (-not $text.Contains($l.Text)) { throw "The licence text of $($l.Info.Package) is missing from the combined licence file." } }
  [System.IO.File]::WriteAllText($outFile, $text, (New-Object System.Text.ASCIIEncoding))
  return @($licenses | ForEach-Object { "$($_.Info.Package) $($_.Info.Version)" })
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
  # The MSI is built with the WiX Toolset SDK, which dotnet restores from NuGet on the first build (nothing to install by hand).
  $msiProject = Join-Path $repo 'packaging\msi\Rotula.Msi.wixproj'
  if (-not (Test-Path -LiteralPath $msiProject)) { throw "The MSI project was not found at $msiProject." }

  # --- Version ------------------------------------------------------------------------------
  if ($cfg.versionOverride) { $version = [string]$cfg.versionOverride }
  else {
    $props = [xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)
    $version = [string]($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
  }
  $version = $version.Trim().TrimStart('v')
  if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "The version '$version' is not major.minor.patch (optionally with a -suffix). Fix <Version> in Directory.Build.props." }
  # Windows Installer only knows numeric versions: 0.4.0-rc1 becomes 0.4.0 inside the MSI (the file name keeps the full text).
  $versionNumeric = $version.Split('-')[0]
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
  Write-Host '1/6 Publishing the app and the MCP server (this takes a few minutes)...'
  # Same flags for both programs. DebugType=none keeps the huge SkiaSharp symbol file (.pdb) out of the output; only the
  # single .exe of each program is copied to the stage folder, so no .pdb or documentation .xml can slip into a release.
  $publishMcp = Join-Path $repo 'publish\win-x64-mcp'
  if (Test-Path -LiteralPath $publishMcp) { Remove-Item -LiteralPath $publishMcp -Recurse -Force }
  foreach ($p in @(@('Rotula.App', $publish), @('Rotula.Mcp', $publishMcp))) {
    & dotnet publish (Join-Path $repo "src\$($p[0])\$($p[0]).csproj") -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
      "-p:Version=$version" -o $p[1]
    if ($LASTEXITCODE -ne 0) { throw "$($p[0]) could not be published. Read the messages above." }
  }

  Copy-Item -LiteralPath (Join-Path $publish 'Rotula.App.exe') -Destination $stage
  Copy-Item -LiteralPath (Join-Path $publishMcp 'Rotula.Mcp.exe') -Destination $stage
  foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md') { Copy-Item -LiteralPath (Join-Path $repo $f) -Destination $stage }
  New-Item -ItemType Directory -Force -Path (Join-Path $stage 'docs'), (Join-Path $stage 'packs') | Out-Null
  Copy-Item -LiteralPath (Join-Path $repo 'docs\mcp.md') -Destination (Join-Path $stage 'docs')
  # The icon of .rotula documents: the installer registers it by path, and the portable zip carries it too.
  New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets') | Out-Null
  Copy-Item -LiteralPath (Join-Path $repo 'src\Rotula.App\Assets\rotula-document.ico') -Destination (Join-Path $stage 'Assets')
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packs-README.txt') -Destination (Join-Path $stage 'packs\README.txt')

  # The licence page the user must accept. Built after the publish because the restore output it reads is fresh now.
  $shipped = New-CombinedLicense (Join-Path $stage 'LICENSE-THIRD-PARTY.txt')
  Write-Host "  Combined licence written; includes the full terms of: $($shipped -join ', ')"
  $exe = Join-Path $stage 'Rotula.App.exe'
  $mcpExe = Join-Path $stage 'Rotula.Mcp.exe'

  # --- 2. Sign the program ------------------------------------------------------------------
  $signed = $false
  Write-Host '2/6 Signing the programs...'
  if ($canSign) { $signed = ((SignFile $exe) -and (SignFile $mcpExe)) } else { Write-Host '  Skipped (see the note above).' }
  if ($canSign -and -not $signed) { Write-Host '  Signing failed; the output will be UNSIGNED.' -ForegroundColor Yellow }

  # --- 3. Installers ------------------------------------------------------------------------
  Write-Host '3/6 Building the installers...'
  & $iscc "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'Rotula.iss')
  if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built. Read the messages above.' }
  $setup = Join-Path $artifacts "Rotula-$version-win-x64-setup.exe"
  if (-not (Test-Path -LiteralPath $setup)) { throw "The installer was not found at $setup after the build." }

  # The MSI packs the same stage folder, so it carries the SIGNED programs too. Its build output stays in publish\ (git-ignored).
  $msiOut = Join-Path $repo 'publish\msi'
  if (Test-Path -LiteralPath $msiOut) { Remove-Item -LiteralPath $msiOut -Recurse -Force }
  & dotnet build $msiProject -c Release "-p:ProductVersion=$versionNumeric" "-p:StageDir=$stage" -o $msiOut
  if ($LASTEXITCODE -ne 0) { throw 'The MSI could not be built. Read the messages above.' }
  $builtMsi = Get-ChildItem -LiteralPath $msiOut -Recurse -Filter 'Rotula.msi' | Select-Object -First 1
  if (-not $builtMsi) { throw "The MSI was not found in $msiOut after the build." }
  $msi = Join-Path $artifacts "Rotula-$version-win-x64.msi"
  Copy-Item -LiteralPath $builtMsi.FullName -Destination $msi

  # --- 4. Sign the installers ---------------------------------------------------------------
  Write-Host '4/6 Signing the installers...'
  $setupSigned = $false
  if ($signed) { $setupSigned = ((SignFile $setup) -and (SignFile $msi)) } else { Write-Host '  Skipped.' }

  # --- 5. Verify ----------------------------------------------------------------------------
  Write-Host '5/6 Checking the signatures...'
  Show-Signature $exe
  Show-Signature $mcpExe
  Show-Signature $setup
  Show-Signature $msi
  $allSigned = ((Test-Signed $exe) -and (Test-Signed $mcpExe) -and (Test-Signed $setup) -and (Test-Signed $msi))

  # --- 6. Zip, manifests, checksums ---------------------------------------------------------
  Write-Host '6/6 Packaging the portable zip, winget manifests and checksums...'
  $suffix = if ($allSigned) { '' } else { '-UNSIGNED' }
  $zip = Join-Path $artifacts "Rotula-$version-win-x64-portable$suffix.zip"
  # The zip is made from the stage folder, so it carries the SIGNED executables.
  New-ZipFromFolder $stage $zip
  # The licence text is also a separate release file, so the winget manifest can link to the terms the user agrees to.
  $licenseAsset = Join-Path $artifacts 'LICENSE-THIRD-PARTY.txt'
  Copy-Item -LiteralPath (Join-Path $stage 'LICENSE-THIRD-PARTY.txt') -Destination $licenseAsset

  if ($allSigned) {
    $wingetOut = Join-Path $artifacts 'winget'
    & (Join-Path $repo 'scripts\New-WingetManifest.ps1') -Version $version -InstallerPath $setup -MsiPath $msi -OutputDir $wingetOut -Repository $cfg.githubRepository
    New-ZipFromFolder $wingetOut (Join-Path $artifacts "Rotula-$version-winget-manifests.zip")
  } else {
    # Rename so an unsigned installer can never be mistaken for the release file; no winget files for it.
    $unsignedSetup = Join-Path $artifacts "Rotula-$version-win-x64-setup-UNSIGNED.exe"
    Move-Item -LiteralPath $setup -Destination $unsignedSetup
    $setup = $unsignedSetup
    $unsignedMsi = Join-Path $artifacts "Rotula-$version-win-x64-UNSIGNED.msi"
    Move-Item -LiteralPath $msi -Destination $unsignedMsi
    $msi = $unsignedMsi
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
    Write-Host "Done. All programs and both installers are signed by '$expectedSigner'. Output: $artifacts" -ForegroundColor Green

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

      $assets = @($setup, $msi, $zip, $licenseAsset, (Join-Path $artifacts 'SHA256SUMS.txt'), (Join-Path $artifacts "Rotula-$version-winget-manifests.zip"))
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
