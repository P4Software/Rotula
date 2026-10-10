<#
.SYNOPSIS
  Fills the winget manifest templates in packaging/winget for one release.
.DESCRIPTION
  Replaces the __PLACEHOLDER__ tokens (version, installer URL, SHA-256, release date, notes URL)
  and writes the ready-to-submit manifest set to -OutputDir/manifests/p/P4Software/Rotula/<version>/,
  the folder layout the winget-pkgs repository expects.
.EXAMPLE
  ./scripts/New-WingetManifest.ps1 -Version 0.1.0 -InstallerPath artifacts/Rotula-0.1.0-win-x64-setup.exe -OutputDir artifacts/winget
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $OutputDir,
    [string] $Repository = 'glenntosco/Rotula'
)
$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v')
$tag = "v$Version"
$name = Split-Path -Leaf $InstallerPath
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToUpperInvariant()
$tokens = @{
    '__VERSION__'           = $Version
    '__INSTALLER_URL__'     = "https://github.com/$Repository/releases/download/$tag/$name"
    '__INSTALLER_SHA256__'  = $sha
    '__RELEASE_DATE__'      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    '__RELEASE_NOTES_URL__' = "https://github.com/$Repository/releases/tag/$tag"
}
$dest = Join-Path $OutputDir "manifests/p/P4Software/Rotula/$Version"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$templates = Join-Path $PSScriptRoot '../packaging/winget'
foreach ($file in Get-ChildItem $templates -Filter 'P4Software.Rotula*.yaml') {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($key in $tokens.Keys) { $text = $text.Replace($key, $tokens[$key]) }
    if ($text -match '__[A-Z_]+__') { throw "Unfilled placeholder in $($file.Name): $($Matches[0])" }
    # winget-pkgs expects UTF-8 without a BOM; write through .NET so Windows PowerShell 5 and pwsh 7 behave the same.
    [System.IO.File]::WriteAllText((Join-Path $dest $file.Name), $text, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host "Winget manifests written to $dest"
