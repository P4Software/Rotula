<#
.SYNOPSIS
  Starts real PostgreSQL and MariaDB servers for the Rotula integration tests.

.DESCRIPTION
  Downloads the vendors' official portable Windows archives (once; they are cached), checks their checksums, unpacks them into a
  scratch folder under %LOCALAPPDATA%\Rotula\test-servers, creates a throw-away data folder, and starts each server bound to
  127.0.0.1 only, on a random high port. No service is installed, no firewall rule or registry entry is created.

  The ports and the generated passwords are written to <scratch>\run\servers.json, outside the repository. The integration tests
  read that file; when it is missing they are skipped. Stop-TestServers.ps1 stops everything and deletes the data.

.PARAMETER Only
  Start only the named engines, for example -Only postgresql. Default: every enabled engine in servers.settings.json.
#>
param(
    [string[]]$Only = @()
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'TestServers.Common.ps1')

try {
    $settings = Read-Settings (Join-Path $here 'servers.settings.json')
    $scratch = Get-ScratchRoot $settings
    $cache = Join-Path $scratch 'cache'
    $run = Join-Path $scratch 'run'
    $statePath = Get-StatePath $scratch

    Write-Info 'Rotula test servers'
    Write-Info "  scratch folder: $scratch"
    Write-Info '  The servers listen on this PC only (127.0.0.1) and are removed again by Stop-TestServers.'
    Write-Info ''

    if (Test-Path -LiteralPath $statePath) {
        Write-Warn 'Test servers from an earlier run were found. Removing them first...'
        & (Join-Path $here 'Stop-TestServers.ps1') -Quiet
    }
    if (Test-Path -LiteralPath $run) { Remove-Item -LiteralPath $run -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $run, $cache | Out-Null

    # A self-signed TLS certificate shared by both servers. It is made by a small C# file because Windows PowerShell can neither
    # write a PEM key nor create a certificate without touching the Windows certificate store.
    $certDir = Join-Path $run 'tls'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw 'The .NET SDK (the "dotnet" command) is needed to create the test certificate, and was not found. Install the .NET 10 SDK and try again.' }
    Invoke-Tool $dotnet.Source @('run', (Join-Path $here 'MakeCert.cs'), '--', $certDir) $run 'Creating the test certificate'
    $certFile = Join-Path $certDir 'server.crt'
    $keyFile = Join-Path $certDir 'server.key'

    $state = [ordered]@{ created = (Get-Date).ToString('o'); scratchRoot = $scratch }
    $usedPorts = @()
    $want = { param($name) ($Only.Count -eq 0 -or $Only -contains $name) -and $settings.$name.enabled }

    # ------------------------------------------------------------------ PostgreSQL
    if (& $want 'postgresql') {
        $entry = $settings.postgresql
        Write-Info '--- PostgreSQL ---'
        $zip = Get-VerifiedArchive 'postgresql' $entry $cache
        $pgRoot = Join-Path $run 'postgresql'
        New-Item -ItemType Directory -Force -Path $pgRoot | Out-Null
        $program = Join-Path $pgRoot 'program'
        Write-Info 'Unpacking (a minute or two)...'
        # pgAdmin, StackBuilder, documentation and headers are not needed to run a server.
        Expand-ArchiveSafely $zip $program @('pgsql/pgAdmin 4/', 'pgsql/StackBuilder/', 'pgsql/doc/', 'pgsql/include/')
        $bin = Join-Path $program 'pgsql\bin'
        $data = Join-Path $pgRoot 'data'

        $port = [int]$entry.port
        if ($port -le 0) { $port = Get-FreePort $usedPorts }
        $usedPorts += $port
        $adminUser = 'rotula_admin'
        $adminPassword = New-Password
        $readerPassword = New-Password

        $pwFile = Join-Path $pgRoot 'pw.tmp'
        [IO.File]::WriteAllText($pwFile, $adminPassword)
        Write-Info 'Creating the database folder...'
        # UTF8 and the C locale on purpose: Windows would otherwise pick a legacy code page and the unicode tests would fail for
        # reasons that have nothing to do with Rotula.
        Invoke-Tool (Join-Path $bin 'initdb.exe') @('-D', $data, '-U', $adminUser, '-E', 'UTF8', '--locale=C', '-A', 'scram-sha-256', "--pwfile=$pwFile") $pgRoot 'Creating the PostgreSQL database folder'
        Remove-Item -LiteralPath $pwFile -Force

        $conf = @(
            '',
            '# --- Rotula integration tests ---',
            "listen_addresses = '127.0.0.1'",
            "port = $port",
            'ssl = on',
            ("ssl_cert_file = '{0}'" -f ($certFile -replace '\\', '/')),
            ("ssl_key_file = '{0}'" -f ($keyFile -replace '\\', '/')),
            'fsync = off',
            'synchronous_commit = off',
            'full_page_writes = off',
            'max_connections = 100'
        )
        Add-Content -LiteralPath (Join-Path $data 'postgresql.conf') -Value $conf -Encoding ASCII

        Write-Info "Starting PostgreSQL on 127.0.0.1:$port ..."
        $logFile = Join-Path $pgRoot 'server.log'
        # postgres.exe refuses to run under an account with administrative rights (a CI runner, an elevated prompt) and exits at
        # once; pg_ctl starts it with those rights removed. pg_ctl itself goes through ShellExecute (no redirection), so the server
        # it leaves running inherits none of this script's handles (see Start-ServerDetached). WaitForExit, not -Wait: -Wait would
        # also wait for the server, which never exits.
        $pgCtl = Start-Process -FilePath (Join-Path $bin 'pg_ctl.exe') -ArgumentList (Join-CommandLine @('start', '-D', $data, '-l', $logFile, '-w', '-t', '60')) `
            -WorkingDirectory $pgRoot -WindowStyle Hidden -PassThru
        $null = $pgCtl.Handle # keeps the exit code readable after the process ends
        if (-not $pgCtl.WaitForExit(90000)) { throw "PostgreSQL did not start in time. See $logFile`n$(Get-LogTail $logFile)" }
        if ($pgCtl.ExitCode -ne 0) { throw "PostgreSQL did not start (pg_ctl exit code $($pgCtl.ExitCode)). See $logFile`n$(Get-LogTail $logFile)" }
        $serverPid = [int](Get-Content -LiteralPath (Join-Path $data 'postmaster.pid') -TotalCount 1)
        $ready = $false
        for ($i = 0; $i -lt 120; $i++) {
            if (-not (Get-Process -Id $serverPid -ErrorAction SilentlyContinue)) { throw "PostgreSQL stopped right after starting. See $logFile`n$(Get-LogTail $logFile)" }
            if (Test-PortOpen $port) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw "PostgreSQL did not open port $port in time. See $logFile`n$(Get-LogTail $logFile)" }
        # The port opens before the server accepts logins; wait for it to say it is ready.
        for ($i = 0; $i -lt 60; $i++) {
            & (Join-Path $bin 'pg_isready.exe') -h 127.0.0.1 -p $port -q
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Milliseconds 500
        }
        Write-Good "PostgreSQL $($entry.version) is running."

        $state['postgresql'] = [ordered]@{
            version = $entry.version; host = '127.0.0.1'; port = $port; processId = $serverPid
            adminUser = $adminUser; adminPassword = $adminPassword; readerUser = 'rotula_reader'; readerPassword = $readerPassword
            caCertificate = $certFile; binFolder = $bin; dataFolder = $data
        }
        # Written after every engine so a later failure still leaves something Stop-TestServers can clean up.
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    }

    # ------------------------------------------------------------------ MariaDB
    if (& $want 'mariadb') {
        $entry = $settings.mariadb
        Write-Info '--- MariaDB ---'
        $zip = Get-VerifiedArchive 'mariadb' $entry $cache
        $mdRoot = Join-Path $run 'mariadb'
        New-Item -ItemType Directory -Force -Path $mdRoot | Out-Null
        $program = Join-Path $mdRoot 'program'
        Write-Info 'Unpacking...'
        Expand-ArchiveSafely $zip $program @()
        $top = Get-ChildItem -LiteralPath $program -Directory | Select-Object -First 1
        $bin = Join-Path $top.FullName 'bin'
        $data = Join-Path $mdRoot 'data'

        $port = [int]$entry.port
        if ($port -le 0) { $port = Get-FreePort $usedPorts }
        $usedPorts += $port
        $adminUser = 'root'
        $adminPassword = New-Password
        $readerPassword = New-Password

        Write-Info 'Creating the database folder...'
        Invoke-Tool (Join-Path $bin 'mariadb-install-db.exe') @("--datadir=$data", "--password=$adminPassword") $mdRoot 'Creating the MariaDB database folder'

        Write-Info "Starting MariaDB on 127.0.0.1:$port ..."
        $logFile = Join-Path $mdRoot 'server.log'
        # --no-defaults: never read a my.ini from the system. Only the loopback address is bound.
        $serverArgs = @('--no-defaults', "--datadir=$data", "--port=$port", '--bind-address=127.0.0.1', '--console',
            '--character-set-server=utf8mb4', '--collation-server=utf8mb4_unicode_ci',
            "--ssl-cert=$certFile", "--ssl-key=$keyFile", '--max-connections=100')
        $serverPid = Start-ServerDetached (Join-Path $bin 'mariadbd.exe') $serverArgs $mdRoot $logFile 'mariadbd.exe'
        $ready = $false
        for ($i = 0; $i -lt 120; $i++) {
            if (-not (Get-Process -Id $serverPid -ErrorAction SilentlyContinue)) { throw "MariaDB stopped right after starting. See $logFile`n$(Get-LogTail $logFile)" }
            if (Test-PortOpen $port) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw "MariaDB did not open port $port in time. See $logFile`n$(Get-LogTail $logFile)" }
        Write-Good "MariaDB $($entry.version) is running."

        $state['mariadb'] = [ordered]@{
            version = $entry.version; host = '127.0.0.1'; port = $port; processId = $serverPid
            adminUser = $adminUser; adminPassword = $adminPassword; readerUser = 'rotula_reader'; readerPassword = $readerPassword
            caCertificate = $certFile; binFolder = $bin; dataFolder = $data
        }
        $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
    }

    Write-Info ''
    Write-Good 'Done. The test servers are running.'
    Write-Info 'Next: run  dotnet test tests\Rotula.Data.Integration  (the tests find the servers by themselves).'
    Write-Info 'When you are finished, run Stop-TestServers to stop the servers and delete their data.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'The test servers could not be started.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Nothing was installed on this PC. Run Stop-TestServers to clean up what was started so far.'
    exit 1
}
