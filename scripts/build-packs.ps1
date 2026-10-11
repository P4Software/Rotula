<#
  Builds the optional database add-ons ("provider packs") of Rotula. Double-click build-packs.cmd (it runs this script).

  What it does: for every folder src\Rotula.Data.Pack.* it publishes the add-on for Windows 64-bit into
  artifacts\packs\<id>\ (the "artifacts" folder is ignored by git). Each folder is complete: the add-on, the database driver it
  needs, its native libraries, pack.json, the licence notices (NOTICES.txt) and a README. To use an add-on, copy its folder into
  the "packs" folder next to Rotula.exe (or into %AppData%\Rotula\packs, where Rotula asks the user to approve it first).

  Nothing is signed, uploaded or installed. A folder is replaced only after its build succeeded, so a failed build never leaves a
  half-written add-on behind. The add-on files are never trimmed or bundled into one file: some licences (Oracle) require the
  driver to be shipped exactly as its publisher made it.

  Optional, for scripts: -Pack duckdb,firebird builds only those add-ons.
  Automation only: set ROTULA_NOPAUSE=1 to skip the final "press Enter" pause.
  Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
#>
param([string[]] $Pack = @())
$ErrorActionPreference = 'Stop'
$repo      = Split-Path -Parent $PSScriptRoot
$packsOut  = Join-Path $repo 'artifacts\packs'
$work      = Join-Path $packsOut '.build'
$exitCode  = 0
$built     = New-Object System.Collections.Generic.List[object]
$failed    = New-Object System.Collections.Generic.List[string]

function Show-Size([long] $bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

function Wait-Close {
  if ($env:ROTULA_NOPAUSE -eq '1') { return }
  Write-Host ''
  Read-Host 'Press Enter to close this window' | Out-Null
}

try {
  Write-Host 'Rotula add-ons: building the database packs'
  Write-Host '-------------------------------------------'

  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on this computer. Install the .NET 10 SDK from https://dotnet.microsoft.com/download and run this again.'
  }

  $projects = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Directory -Filter 'Rotula.Data.Pack.*' | Sort-Object Name)
  if ($projects.Count -eq 0) { throw 'No add-on projects (src\Rotula.Data.Pack.*) were found. Run this script from a full copy of the Rotula source.' }

  New-Item -ItemType Directory -Force -Path $packsOut | Out-Null
  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $work | Out-Null

  foreach ($project in $projects) {
    $manifestPath = Join-Path $project.FullName 'pack.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { Write-Host "Skipping $($project.Name): it has no pack.json."; continue }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $id = [string] $manifest.id
    if ($Pack.Count -gt 0 -and ($Pack -notcontains $id)) { continue }

    Write-Host ''
    Write-Host "Building the $($manifest.name) add-on ($id) ..."
    $target = Join-Path $work $id
    $csproj = Join-Path $project.FullName ($project.Name + '.csproj')
    # win-x64 chooses the one native library set that applies; --self-contained false keeps the .NET runtime out of the add-on
    # (Rotula brings it). No trimming and no single file, on purpose (see the header).
    & dotnet publish $csproj -c Release -r win-x64 --self-contained false -o $target --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
      Write-Host "The $($manifest.name) add-on could not be built. Read the messages above; the previous version of this add-on (if any) was left untouched." -ForegroundColor Red
      $failed.Add($id)
      continue
    }

    # A pack folder must be complete or Rotula will refuse to load it: check before it replaces the old one.
    $missing = @($manifest.entryAssembly, 'pack.json', [string] $manifest.licenseNoticesFile, 'README.md') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $target $_)) }
    if ($missing.Count -gt 0) {
      Write-Host "The $($manifest.name) add-on is incomplete (missing: $($missing -join ', ')). It was not copied to the output folder." -ForegroundColor Red
      $failed.Add($id)
      continue
    }

    $final = Join-Path $packsOut $id
    if (Test-Path -LiteralPath $final) { Remove-Item -LiteralPath $final -Recurse -Force }
    Move-Item -LiteralPath $target -Destination $final
    $files = @(Get-ChildItem -LiteralPath $final -Recurse -File)
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    $built.Add([pscustomobject]@{ Id = $id; Name = [string] $manifest.name; Files = $files.Count; Bytes = [long] $size; Path = $final })
    Write-Host "  done: $($files.Count) files, $(Show-Size $size)"
  }

  if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }

  Write-Host ''
  Write-Host 'Result'
  Write-Host '------'
  foreach ($b in $built) { Write-Host ('{0,-12} {1,10}   {2,4} files   {3}' -f $b.Id, (Show-Size $b.Bytes), $b.Files, $b.Path) }
  if ($built.Count -gt 0) {
    Write-Host ''
    Write-Host 'To use an add-on, copy its folder into the "packs" folder next to Rotula.exe.'
    Write-Host 'Each folder contains NOTICES.txt with the licences of the database driver. Keep that file with the add-on.'
  }
  if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host "These add-ons failed: $($failed -join ', ')." -ForegroundColor Red
    $exitCode = 1
  }
  elseif ($built.Count -eq 0) {
    Write-Host 'Nothing was built (no add-on matched).' -ForegroundColor Yellow
    $exitCode = 1
  }
}
catch {
  Write-Host ''
  Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
  $exitCode = 1
}
Wait-Close
exit $exitCode
