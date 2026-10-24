<#
.SYNOPSIS
  Fills the winget manifest templates in packaging/winget for one release.
.DESCRIPTION
  Replaces the __PLACEHOLDER__ tokens (version, installer URLs, SHA-256 hashes, the MSI's ProductCode, release date, notes URL,
  third-party licence URL) and writes the ready-to-submit manifest set to -OutputDir/manifests/p/P4Software/Rotula/<version>/,
  the folder layout the winget-pkgs repository expects. The ProductCode is read from the MSI itself (Windows only).
.EXAMPLE
  ./scripts/New-WingetManifest.ps1 -Version 0.4.0 -InstallerPath artifacts/Rotula-0.4.0-win-x64-setup.exe -MsiPath artifacts/Rotula-0.4.0-win-x64.msi -OutputDir artifacts/winget
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $MsiPath,
    [Parameter(Mandatory)] [string] $OutputDir,
    [string] $Repository = 'P4Software/Rotula'
)
$ErrorActionPreference = 'Stop'

function Get-MsiProperty([string] $path, [string] $name) {
    # Reads one row of the MSI's Property table through the Windows Installer automation interface (works in PowerShell 5.1 and 7).
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path -LiteralPath $path).Path, 0))
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$name'"))
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $record) { throw "The MSI $path has no $name." }
        $value = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        return $value
    } finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

$Version = $Version.TrimStart('v')
$tag = "v$Version"
$base = "https://github.com/$Repository/releases/download/$tag"
$productCode = Get-MsiProperty $MsiPath 'ProductCode'
if ($productCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw "The MSI ProductCode '$productCode' does not look like a GUID." }
$tokens = @{
    '__VERSION__'           = $Version
    '__INSTALLER_URL__'     = "$base/$(Split-Path -Leaf $InstallerPath)"
    '__INSTALLER_SHA256__'  = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToUpperInvariant()
    '__MSI_URL__'           = "$base/$(Split-Path -Leaf $MsiPath)"
    '__MSI_SHA256__'        = (Get-FileHash -Algorithm SHA256 -LiteralPath $MsiPath).Hash.ToUpperInvariant()
    '__MSI_PRODUCT_CODE__'  = $productCode.ToUpperInvariant()
    '__RELEASE_DATE__'      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    '__RELEASE_NOTES_URL__' = "https://github.com/$Repository/releases/tag/$tag"
    '__LICENSE_THIRD_PARTY_URL__' = "$base/LICENSE-THIRD-PARTY.txt"
}
$dest = Join-Path $OutputDir "manifests/p/P4Software/Rotula/$Version"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$templates = Join-Path $PSScriptRoot '../packaging/winget'
foreach ($file in Get-ChildItem $templates -Filter 'P4Software.Rotula*.yaml') {
    # -Encoding UTF8: the Spanish locale file has accents, and Windows PowerShell 5.1 would read a BOM-less file as ANSI.
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($key in $tokens.Keys) { $text = $text.Replace($key, $tokens[$key]) }
    if ($text -match '__[A-Z_]+__') { throw "Unfilled placeholder in $($file.Name): $($Matches[0])" }
    # winget-pkgs expects UTF-8 without a BOM; write through .NET so Windows PowerShell 5 and pwsh 7 behave the same.
    [System.IO.File]::WriteAllText((Join-Path $dest $file.Name), $text, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host "Winget manifests written to $dest"
