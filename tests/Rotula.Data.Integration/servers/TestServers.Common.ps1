# Shared helpers for Start-TestServers.ps1 and Stop-TestServers.ps1. Dot-sourced; not meant to be run on its own.
# Works in Windows PowerShell 5.1 (what a double-click uses) and in PowerShell 7.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress bars make downloads and archive work many times slower in 5.1
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Write-Info([string]$Message) { Write-Host $Message }
function Write-Good([string]$Message) { Write-Host $Message -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host $Message -ForegroundColor Yellow }

function Read-Settings([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "The settings file was not found: $Path" }
    # The file is commented with // lines, which Windows PowerShell 5.1 cannot parse, so they are removed first.
    $text = (Get-Content -LiteralPath $Path -Encoding UTF8) | Where-Object { $_ -notmatch '^\s*//' }
    return ($text -join "`n") | ConvertFrom-Json
}

# Fixed on purpose: the integration tests look for the state file in exactly this place.
function Get-ScratchRoot($Settings) {
    return [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Rotula\test-servers'))
}

function Test-PortOpen([int]$Port) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        return ($task.Wait(500) -and $client.Connected)
    } catch { return $false } finally { $client.Dispose() }
}

function Get-FreePort([int[]]$Avoid) {
    $random = New-Object Random
    for ($i = 0; $i -lt 200; $i++) {
        $port = $random.Next(20000, 60000)
        if ($Avoid -contains $port) { continue }
        $listener = $null
        try {
            $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        } catch { } finally { if ($listener) { $listener.Stop() } }
    }
    throw 'No free port was found between 20000 and 60000.'
}

function New-Password([int]$Length = 24) {
    # Letters and digits only: the password is also pasted into connection strings and SQL by the tests.
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $sb = New-Object Text.StringBuilder
    foreach ($b in $bytes) { [void]$sb.Append($chars[$b % $chars.Length]) }
    return $sb.ToString()
}

function Get-FileHashText([string]$Path, [string]$Algorithm) {
    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}

function Stop-OnBadChecksum([string]$File, [string]$Kind, [string]$Expected, [string]$Actual, [string]$Folder) {
    throw ("The downloaded file does not match its checksum, so it was NOT used.`n" +
           "  file:     $File`n  expected ($Kind): $Expected`n  found:    $Actual`n" +
           "What to do: delete the folder $Folder and run this again. If it keeps failing, do not use the file and report it.")
}

# Downloads one vendor archive into its OWN new folder under cache\ and verifies it. Returns the path of the verified zip.
# Checksum policy: a vendor-published checksum is mandatory and a mismatch stops everything; with none published, the locally
# recorded hash is compared; with neither, the hash is printed so it can be recorded.
function Get-VerifiedArchive([string]$Name, $Entry, [string]$CacheRoot) {
    $folder = Join-Path $CacheRoot ("{0}-{1}" -f $Name, $Entry.version)
    $file = Join-Path $folder ([IO.Path]::GetFileName(([Uri]$Entry.url).AbsolutePath))
    if (-not (Test-Path -LiteralPath $file)) {
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        Write-Info "Downloading $Name $($Entry.version) from the vendor's official site (this can take a few minutes)..."
        Write-Info "  $($Entry.url)"
        $part = $file + '.part'
        Add-Type -AssemblyName System.Net.Http
        $http = New-Object Net.Http.HttpClient
        $http.Timeout = [TimeSpan]::FromMinutes(30)
        try {
            $response = $http.GetAsync($Entry.url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            [void]$response.EnsureSuccessStatusCode()
            $in = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $out = [IO.File]::Create($part)
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        } finally { $http.Dispose() }
        Move-Item -LiteralPath $part -Destination $file
    } else {
        Write-Info "Using the download already on this PC: $file"
    }

    # Verified on EVERY run, also for a cached file, before anything is extracted from it.
    $sha256 = Get-FileHashText $file 'SHA256'
    $checked = $false
    if (-not [string]::IsNullOrWhiteSpace($Entry.sha256)) {
        if ($sha256 -ne $Entry.sha256.ToLowerInvariant()) { Stop-OnBadChecksum $file 'SHA-256' $Entry.sha256 $sha256 $folder }
        Write-Good "  SHA-256 matches the value published by the vendor."
        $checked = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($Entry.sha512)) {
        $sha512 = Get-FileHashText $file 'SHA512'
        if ($sha512 -ne $Entry.sha512.ToLowerInvariant()) { Stop-OnBadChecksum $file 'SHA-512' $Entry.sha512 $sha512 $folder }
        Write-Good "  SHA-512 matches the value published by the vendor."
        $checked = $true
    }
    if (-not $checked) {
        if (-not [string]::IsNullOrWhiteSpace($Entry.recordedSha256)) {
            if ($sha256 -ne $Entry.recordedSha256.ToLowerInvariant()) { Stop-OnBadChecksum $file 'recorded SHA-256' $Entry.recordedSha256 $sha256 $folder }
            Write-Warn "  The vendor publishes no checksum for $Name. The file matches the hash recorded when it was first downloaded (not a vendor guarantee)."
        } else {
            Write-Warn "  The vendor publishes no checksum for $Name and none is recorded yet. SHA-256 of this file: $sha256"
            Write-Warn "  Put it in servers.settings.json as recordedSha256 to detect later changes."
        }
    }
    return $file
}

# Extracts a zip into a NEW folder, skipping paths that start with one of $SkipPrefixes, and refusing entries that would land
# outside the target folder.
function Expand-ArchiveSafely([string]$Zip, [string]$Target, [string[]]$SkipPrefixes) {
    if (Test-Path -LiteralPath $Target) { throw "The folder already exists and must be empty: $Target" }
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $targetFull = [IO.Path]::GetFullPath($Target).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $skip = $false
            foreach ($p in $SkipPrefixes) { if ($entry.FullName.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break } }
            if ($skip) { continue }
            $dest = [IO.Path]::GetFullPath((Join-Path $Target $entry.FullName))
            if (-not $dest.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase)) { throw "The archive contains an unsafe path: $($entry.FullName)" }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $dest | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $false)
        }
    } finally { $archive.Dispose() }
}

# Runs a program, waits, and throws with the tail of its output when it fails. WorkingDirectory is always our own run folder,
# never the extracted vendor folder.
function Invoke-Tool([string]$Exe, [string[]]$Arguments, [string]$WorkDir, [string]$Description) {
    $out = Join-Path $WorkDir ([guid]::NewGuid().ToString('N') + '.out')
    $err = Join-Path $WorkDir ([guid]::NewGuid().ToString('N') + '.err')
    try {
        $argLine = Join-CommandLine $Arguments
        $p = Start-Process -FilePath $Exe -ArgumentList $argLine -WorkingDirectory $WorkDir -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $out -RedirectStandardError $err
        if ($p.ExitCode -ne 0) {
            $tail = @(Get-Content -LiteralPath $out -ErrorAction SilentlyContinue) + @(Get-Content -LiteralPath $err -ErrorAction SilentlyContinue)
            $tail = $tail | Select-Object -Last 15
            throw "$Description failed (exit code $($p.ExitCode)).`n$($tail -join "`n")"
        }
    } finally { Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue }
}

# Quotes each argument for the Windows command line.
function Join-CommandLine([string[]]$Arguments) {
    return (($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' ')
}

function Get-StatePath([string]$ScratchRoot) { return Join-Path (Join-Path $ScratchRoot 'run') 'servers.json' }

function Test-ProcessUnder([int]$ProcessId, [string]$Folder) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $false }
    try { return [bool]($proc.Path -and $proc.Path.StartsWith($Folder, [StringComparison]::OrdinalIgnoreCase)) } catch { return $false }
}

# Ends a server process only when it is really the one we started (its executable lives in our run folder).
function Stop-OurProcess([int]$ProcessId, [string]$RunFolder, [string]$What) {
    if (-not $ProcessId) { return }
    if (-not (Test-ProcessUnder $ProcessId $RunFolder)) { return }
    Write-Info "  Stopping $What (process $ProcessId)..."
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 50 -and (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 200 }
}

# Starts a long-running server so that it does NOT inherit this script's handles. A server that inherits the caller's output
# pipe keeps it open, and anything waiting for this script to finish (a build agent, a "| more") would then wait for the server
# too. Start-Process without redirection goes through ShellExecute, which inherits nothing; cmd.exe does the log redirection.
# Returns the process id of the server itself (found as the child of the cmd.exe that was started).
function Start-ServerDetached([string]$Exe, [string[]]$Arguments, [string]$WorkDir, [string]$LogFile, [string]$ProcessName) {
    $inner = '"' + $Exe + '" ' + (Join-CommandLine $Arguments) + ' > "' + $LogFile + '" 2>&1 < nul'
    $cmd = Start-Process -FilePath $env:ComSpec -ArgumentList ('/c "' + $inner + '"') -WorkingDirectory $WorkDir -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 100; $i++) {
        $child = Get-CimInstance Win32_Process -Filter ("ParentProcessId = {0} AND Name = '{1}'" -f $cmd.Id, $ProcessName) -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($child) { return [int]$child.ProcessId }
        if ($cmd.HasExited) { throw "The server stopped right after starting. See $LogFile" }
        Start-Sleep -Milliseconds 100
    }
    throw "The server process did not appear. See $LogFile"
}
