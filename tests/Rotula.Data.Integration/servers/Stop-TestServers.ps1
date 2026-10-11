<#
.SYNOPSIS
  Stops the Rotula integration-test servers and deletes their data.

.DESCRIPTION
  Stops the servers started by Start-TestServers.ps1 (only processes whose program lives in the scratch run folder), then deletes
  <scratch>\run (data, unpacked programs, certificate, generated passwords). The downloaded archives in <scratch>\cache are kept
  so the next start is quick; pass -PurgeCache to delete them as well.
#>
param(
    [switch]$PurgeCache,
    [switch]$Quiet
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TestServers.Common.ps1')

try {
    $settings = Read-Settings (Join-Path $here 'servers.settings.json')
    $scratch = Get-ScratchRoot $settings
    $run = Join-Path $scratch 'run'
    $statePath = Get-StatePath $scratch

    if (-not $Quiet) { Write-Info 'Stopping the Rotula test servers...' }

    $runFull = [IO.Path]::GetFullPath($run).TrimEnd('\') + '\'

    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($state.PSObject.Properties['postgresql']) {
            $pg = $state.postgresql
            if (Test-ProcessUnder ([int]$pg.processId) $runFull) {
                # A clean shutdown first; the process is ended only if that does not work.
                try { Invoke-Tool (Join-Path $pg.binFolder 'pg_ctl.exe') @('stop', '-D', $pg.dataFolder, '-m', 'immediate', '-w', '-t', '30') $run 'Stopping PostgreSQL' } catch { }
                Stop-OurProcess ([int]$pg.processId) $runFull 'PostgreSQL'
            }
        }
        if ($state.PSObject.Properties['mariadb']) {
            $md = $state.mariadb
            if (Test-ProcessUnder ([int]$md.processId) $runFull) {
                try {
                    Invoke-Tool (Join-Path $md.binFolder 'mariadb-admin.exe') @('--no-defaults', '--protocol=tcp', '-h', '127.0.0.1', '-P', "$($md.port)", '-u', $md.adminUser, "--password=$($md.adminPassword)", 'shutdown') $run 'Stopping MariaDB'
                } catch { }
                for ($i = 0; $i -lt 50 -and (Test-ProcessUnder ([int]$md.processId) $runFull); $i++) { Start-Sleep -Milliseconds 200 }
                Stop-OurProcess ([int]$md.processId) $runFull 'MariaDB'
            }
        }
    }

    # A start that failed half-way may have left a server running without a state file; whatever still runs from the run folder is ours.
    if (Test-Path -LiteralPath $run) {
        foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
            if (Test-ProcessUnder $p.Id $runFull) { Stop-OurProcess $p.Id $runFull $p.ProcessName }
        }
    }

    if (Test-Path -LiteralPath $run) {
        # Safety: only ever delete a folder that is named "run" inside a scratch folder that really is ours.
        if ((Split-Path -Leaf $run) -ne 'run') { throw "Refusing to delete an unexpected folder: $run" }
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            try { Remove-Item -LiteralPath $run -Recurse -Force; break }
            catch { if ($attempt -eq 5) { throw }; Start-Sleep -Seconds 1 }   # a server may still be releasing its files
        }
        if (-not $Quiet) { Write-Good 'Servers stopped and their data deleted.' }
    } elseif (-not $Quiet) {
        Write-Info 'There was nothing to stop.'
    }

    if ($PurgeCache) {
        $cache = Join-Path $scratch 'cache'
        if ((Split-Path -Leaf $cache) -ne 'cache') { throw "Refusing to delete an unexpected folder: $cache" }
        if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force; Write-Good 'Downloaded archives deleted.' }
    }
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'The test servers could not be stopped completely.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'What to do: close any program using the files, then run this again.'
    exit 1
}
