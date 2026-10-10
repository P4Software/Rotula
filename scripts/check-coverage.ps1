<#
.SYNOPSIS
  Coverage gate: fails when line coverage of one assembly is below a threshold.
.DESCRIPTION
  Reads a Cobertura report produced by coverlet and counts line hits for the
  given package (assembly) by hand, so the result does not depend on the
  rounding of the report's own line-rate attribute.
.EXAMPLE
  ./scripts/check-coverage.ps1 -Report coverage/coverage.cobertura.xml -Package Rotula.Core -Threshold 80
#>
param(
    [Parameter(Mandatory)] [string] $Report,
    [string] $Package = 'Rotula.Core',
    [double] $Threshold = 80
)
$ErrorActionPreference = 'Stop'
[xml] $xml = Get-Content -LiteralPath $Report -Raw
$pkgs = @($xml.coverage.packages.package | Where-Object { $_.name -eq $Package })
if ($pkgs.Count -eq 0) { Write-Error "Package '$Package' not found in $Report"; exit 2 }
# A class can appear once per method group; key by file+line so a line counts once.
$lines = @{}
foreach ($pkg in $pkgs) {
    foreach ($line in $pkg.SelectNodes('.//line')) {
        $file = $line.SelectSingleNode("ancestor::class").filename
        $key = "$file`:$($line.number)"
        $hit = [int]$line.hits -gt 0
        if (-not $lines.ContainsKey($key) -or $hit) { $lines[$key] = $hit }
    }
}
$total = $lines.Count
$covered = @($lines.Values | Where-Object { $_ }).Count
$pct = if ($total -eq 0) { 0 } else { [math]::Round(100.0 * $covered / $total, 2) }
Write-Host "$Package line coverage: $pct% ($covered/$total lines), threshold $Threshold%"
if ($pct -lt $Threshold) { Write-Host "Coverage gate FAILED."; exit 1 }
Write-Host "Coverage gate passed."
