<#
  Regenerates src\Rotula.Data.Pack.*\NOTICES.txt: for every file the packs ship (the lists below must match the packages in each pack's
  deps.json; tests\Rotula.Data.Packs.Tests fails when they do not), the component, exact version, copyright, project and the licence text
  verbatim from the package in the NuGet cache. Run it after changing a pack's package versions (after a restore), then commit the result.
  Developers only; users never run this. Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
#>
param([string]$Repo = (Split-Path -Parent $PSScriptRoot), [string]$Cache = '')
$ErrorActionPreference = 'Stop'
if (-not $Cache) {
  # "global-packages: C:\path\to\packages\"
  $line = (& dotnet nuget locals global-packages --list) | Select-Object -First 1
  $Cache = ($line -replace '^\s*global-packages:\s*', '').Trim()
}
if (-not (Test-Path -LiteralPath $Cache)) { throw "The NuGet package cache was not found: $Cache. Run dotnet restore first." }

function Pkg($id, $ver) { Join-Path $Cache ("{0}\{1}" -f $id.ToLowerInvariant(), $ver) }
function ReadText($p) { [IO.File]::ReadAllText($p).Replace("`r`n", "`n").TrimEnd() }
function Nuspec($id, $ver) {
    $f = Get-ChildItem (Pkg $id $ver) -Filter *.nuspec | Select-Object -First 1
    [xml](Get-Content $f.FullName -Raw)
}
function Meta($id, $ver) {
    $m = (Nuspec $id $ver).package.metadata
    $lic = if ($m.license) { $m.license.'#text' } else { '' }
    $copy = $m.copyright
    if (-not $copy) { $copy = $m.authors }
    [pscustomobject]@{ Id = $id; Version = $ver; License = $lic; Copyright = $copy; Url = $(if ($m.projectUrl) { $m.projectUrl } else { $m.repository.url }) }
}
function FindFile($id, $ver, $names) {
    foreach ($n in $names) { $p = Join-Path (Pkg $id $ver) $n; if (Test-Path $p) { return $p } }
    return $null
}

# The MIT and Apache texts for packages whose nuspec only carries the licence expression: the verbatim files shipped by sibling packages.
$mitText = ReadText (FindFile 'System.Configuration.ConfigurationManager' '8.0.0' @('LICENSE.TXT'))
$apacheText = ReadText (FindFile 'Apache.Arrow' '23.0.0' @('LICENSE.txt'))

function Build($packName, $intro, $components, $extraSections) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("$packName pack for Rotula: licences and third-party notices")
    [void]$sb.AppendLine(('=' * 70))
    [void]$sb.AppendLine()
    [void]$sb.AppendLine($intro)
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('Components in this folder (all UNMODIFIED, exactly as published by their authors)')
    [void]$sb.AppendLine(('-' * 70))
    foreach ($c in $components) {
        $m = Meta $c.Id $c.Version
        $lic = if ($c.License) { $c.License } else { $m.License }
        [void]$sb.AppendLine(("{0} {1}" -f $c.Id, $c.Version))
        [void]$sb.AppendLine(("    Files: {0}" -f $c.Files))
        [void]$sb.AppendLine(("    License: {0}" -f $lic))
        if ($m.Copyright) { [void]$sb.AppendLine(("    Copyright: {0}" -f $m.Copyright)) }
        if ($m.Url) { [void]$sb.AppendLine(("    Project: {0}" -f $m.Url)) }
    }
    [void]$sb.AppendLine()
    foreach ($s in @($extraSections | ForEach-Object { $_ })) {
        [void]$sb.AppendLine(('=' * 70)); [void]$sb.AppendLine($s.Title); [void]$sb.AppendLine(('=' * 70)); [void]$sb.AppendLine()
        [void]$sb.AppendLine($s.Text); [void]$sb.AppendLine()
    }
    $sb.ToString()
}

function Section($title, $text) { [pscustomobject]@{ Title = $title; Text = $text } }
function FileSection($title, $id, $ver, $names) { Section $title (ReadText (FindFile $id $ver $names)) }

# Every distinct THIRD-PARTY-NOTICES.TXT of the Microsoft packages in a pack, each once, labelled with the packages that carry it.
function MsNotices($components) {
    $groups = @{}
    foreach ($c in $components) {
        $f = FindFile $c.Id $c.Version @('THIRD-PARTY-NOTICES.TXT', 'THIRD-PARTY-NOTICES.txt')
        if (-not $f -or $c.Id -like 'ClickHouse*') { continue }
        $text = ReadText $f
        $key = (Get-FileHash $f -Algorithm SHA256).Hash
        if (-not $groups.ContainsKey($key)) { $groups[$key] = @{ Text = $text; Names = @() } }
        $groups[$key].Names += ("{0} {1}" -f $c.Id, $c.Version)
    }
    foreach ($g in $groups.Values) { Section ("Third-party notices shipped with: " + ($g.Names -join ', ') + " (THIRD-PARTY-NOTICES.TXT, verbatim)") $g.Text }
}
$mitMs = Section 'MIT License as shipped with the Microsoft .NET packages (LICENSE.TXT, verbatim). Applies to every package above whose license is MIT and that names Microsoft or the .NET Foundation.' $mitText

# ---------------------------------------------------------------- DuckDB
$duck = Build 'DuckDB' ('This pack lets Rotula read DuckDB database files, read-only. DuckDB runs inside Rotula (no server). ' +
        'The native library duckdb.dll is the DuckDB engine, built and published by the DuckDB.NET project from the DuckDB sources.') @(
    @{ Id = 'DuckDB.NET.Data.Full'; Version = '1.5.6'; Files = 'DuckDB.NET.Data.dll' },
    @{ Id = 'DuckDB.NET.Bindings.Full'; Version = '1.5.6'; Files = 'DuckDB.NET.Bindings.dll, duckdb.dll (the DuckDB engine, MIT, see below)' },
    @{ Id = 'Apache.Arrow'; Version = '23.0.0'; Files = 'Apache.Arrow.dll' },
    @{ Id = 'Apache.Arrow.Scalars'; Version = '23.0.0'; Files = 'Apache.Arrow.Scalars.dll' }) @(
    (FileSection 'DuckDB.NET (the .NET driver): MIT License (LICENSE.md of DuckDB.NET.Data.Full 1.5.6, verbatim)' 'DuckDB.NET.Data.Full' '1.5.6' @('LICENSE.md')),
    (FileSection 'DuckDB (the engine in duckdb.dll): MIT License (LICENSE-DuckDB.txt of DuckDB.NET.Bindings.Full 1.5.6, verbatim)' 'DuckDB.NET.Bindings.Full' '1.5.6' @('LICENSE-DuckDB.txt')),
    (FileSection 'Apache Arrow: Apache License 2.0 with the bundled third-party notices (LICENSE.txt of Apache.Arrow 23.0.0, verbatim)' 'Apache.Arrow' '23.0.0' @('LICENSE.txt')))
[IO.File]::WriteAllText("$Repo\src\Rotula.Data.Pack.DuckDb\NOTICES.txt", $duck.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- Firebird
$fb = Build 'Firebird' ('This pack lets Rotula read Firebird databases over the network, read-only.' + "`n" +
        'FirebirdSql.Data.FirebirdClient is licensed under the Initial Developer''s Public License 1.0 (file-level copyleft). It is used exactly as published, ' +
        'without any change to its files, and its source code is available from the project: https://github.com/FirebirdSQL/NETProvider ' +
        '(the 10.3.4 package was built from commit c6ad75a7f72bb32e1ef6ba65826c0cd554d208bb). Rotula''s own code in this pack is MIT-licensed and is a separate file.') @(
    @{ Id = 'FirebirdSql.Data.FirebirdClient'; Version = '10.3.4'; Files = 'FirebirdSql.Data.FirebirdClient.dll' }) @(
    (FileSection 'Initial Developer''s Public License Version 1.0 (license.txt of FirebirdSql.Data.FirebirdClient 10.3.4, verbatim)' 'FirebirdSql.Data.FirebirdClient' '10.3.4' @('license.txt')))
[IO.File]::WriteAllText("$Repo\src\Rotula.Data.Pack.Firebird\NOTICES.txt", $fb.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- Oracle
$oraConditions = @'
Conditions of the Oracle licence that apply to this folder, in short (the full text below is what counts):
  * Oracle.ManagedDataAccess.dll is distributed UNMODIFIED, with a copy of the licence (this file).
  * No additional fee is charged for the distribution or use of the component.
  * Oracle's copyright and licence notices are kept; nothing in this folder removes or alters them.
  * The component is not reverse engineered, decompiled or disassembled.
  * Export and sanctions laws of the United States and other countries apply.
'@
$oraComponents = @(
    @{ Id = 'Oracle.ManagedDataAccess.Core'; Version = '23.26.301'; Files = 'Oracle.ManagedDataAccess.dll'; License = 'Oracle Free Distribution, Hosting, and Use Terms and Conditions (see below; not an open-source licence)' },
    @{ Id = 'System.Configuration.ConfigurationManager'; Version = '8.0.0'; Files = 'System.Configuration.ConfigurationManager.dll' },
    @{ Id = 'System.Diagnostics.EventLog'; Version = '8.0.0'; Files = 'System.Diagnostics.EventLog.dll, System.Diagnostics.EventLog.Messages.dll' },
    @{ Id = 'System.Diagnostics.PerformanceCounter'; Version = '8.0.0'; Files = 'System.Diagnostics.PerformanceCounter.dll' },
    @{ Id = 'System.DirectoryServices.Protocols'; Version = '8.0.2'; Files = 'System.DirectoryServices.Protocols.dll' },
    @{ Id = 'System.Security.Cryptography.Pkcs'; Version = '8.0.1'; Files = 'System.Security.Cryptography.Pkcs.dll' },
    @{ Id = 'System.Security.Cryptography.ProtectedData'; Version = '10.0.12'; Files = 'System.Security.Cryptography.ProtectedData.dll' })
$ora = Build 'Oracle' ('This OPTIONAL pack lets Rotula read Oracle databases, read-only, with Oracle''s own managed driver (ODP.NET Core, no Oracle client installation needed).' + "`n`n" + $oraConditions) $oraComponents @(
    (FileSection 'Oracle Free Distribution, Hosting, and Use Terms and Conditions (LICENSE.txt of Oracle.ManagedDataAccess.Core 23.26.301, verbatim)' 'Oracle.ManagedDataAccess.Core' '23.26.301' @('LICENSE.txt')),
    (@($mitMs) + (MsNotices $oraComponents)))
[IO.File]::WriteAllText("$Repo\src\Rotula.Data.Pack.Oracle\NOTICES.txt", $ora.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- ClickHouse
$chComponents = @(
    @{ Id = 'ClickHouse.Driver'; Version = '1.5.0'; Files = 'ClickHouse.Driver.dll, ClickHouse.Driver.Common.dll, ClickHouse.Driver.Tcp.dll' },
    @{ Id = 'Microsoft.IO.RecyclableMemoryStream'; Version = '3.0.1'; Files = 'Microsoft.IO.RecyclableMemoryStream.dll' },
    @{ Id = 'NodaTime'; Version = '3.2.3'; Files = 'NodaTime.dll' })
foreach ($n in 'Configuration', 'Configuration.Abstractions', 'Configuration.Binder', 'DependencyInjection', 'DependencyInjection.Abstractions', 'Diagnostics', 'Diagnostics.Abstractions', 'Http', 'Logging', 'Logging.Abstractions', 'Options', 'Options.ConfigurationExtensions', 'Primitives') {
    $chComponents += @{ Id = "Microsoft.Extensions.$n"; Version = '10.0.8'; Files = "Microsoft.Extensions.$n.dll" }
}
$nodaCopy = (Meta 'NodaTime' '3.2.3').Copyright
$ch = Build 'ClickHouse' 'This pack lets Rotula read ClickHouse servers over HTTP(S), read-only, with ClickHouse''s own .NET driver.' $chComponents @(
    (FileSection 'ClickHouse.Driver: MIT License (LICENSE of ClickHouse.Driver 1.5.0, verbatim)' 'ClickHouse.Driver' '1.5.0' @('LICENSE')),
    (FileSection 'Code vendored inside ClickHouse.Driver (THIRD-PARTY-NOTICES.txt of ClickHouse.Driver 1.5.0, verbatim)' 'ClickHouse.Driver' '1.5.0' @('THIRD-PARTY-NOTICES.txt')),
    $mitMs,
    (Section 'Apache License, Version 2.0 (NodaTime 3.2.3 declares the Apache-2.0 expression in its package; the text is the one shipped in Apache.Arrow 23.0.0 LICENSE.txt, verbatim)' $apacheText),
    (MsNotices $chComponents))
[IO.File]::WriteAllText("$Repo\src\Rotula.Data.Pack.ClickHouse\NOTICES.txt", $ch.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
'done'
