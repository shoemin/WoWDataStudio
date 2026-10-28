# dbsnap.ps1 -- a READ-ONLY snapshot of the server's world database (mangos) for the 0.9.0 in-game test: mysqldump with
# --skip-lock-tables --single-transaction (SELECT / SHOW only, no locks, no FLUSH), deterministic output (no dump date, no comments,
# rows in primary-key order, one row per line, binary as hex), plus a SHA-256 of the file and one per table.
#   .\dbsnap.ps1 -Out <run>\db\mangos_before.sql
#   .\dbsnap.ps1 -Out <run>\db\mangos_after.sql -Compare <run>\db\mangos_before.sql      -> "differences: 0" when byte-identical
# The password is the harness's (tools\lib_wowtest.ps1, read from that file, passed in MYSQL_PWD, never printed); -Port / -NoPassword
# for a throwaway server.
param([Parameter(Mandatory)][string]$Out, [string]$Compare = '', [int]$Port = 3306, [switch]$NoPassword, [string]$Database = 'mangos', [switch]$ForLoading)
$ErrorActionPreference = 'Stop'
$base = (Resolve-Path "$PSScriptRoot\..\..\..").Path   # the folder that holds WoWDataTool, WoWDecomp, Server and the clients
$bin = "$base\Server\mysql5\bin"
if ($Database -ne 'mangos') { throw 'dbsnap.ps1 is for the world database (mangos) only' }
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$old = $env:MYSQL_PWD
try {
    if (-not $NoPassword) {
        $lib = Get-Content "$base\WoWDecomp\tools\lib_wowtest.ps1" -Raw
        if ($lib -notmatch '-u(\S+)\s+-p(\S+)\s') { throw 'could not read the harness credentials from lib_wowtest.ps1' }
        $user = $Matches[1]; $env:MYSQL_PWD = $Matches[2]
    } else { $user = 'root'; $env:MYSQL_PWD = '' }
    $t0 = Get-Date
    $args_ = @("--host=127.0.0.1", "--port=$Port", "-u$user", '--skip-lock-tables', '--single-transaction', '--skip-dump-date', '--skip-comments',
               '--order-by-primary', $(if ($ForLoading) { '--extended-insert' } else { '--skip-extended-insert' }), '--hex-blob', '--default-character-set=utf8', "--result-file=$Out", $Database)
    & "$bin\mysqldump.exe" @args_
    if ($LASTEXITCODE -ne 0) { throw "mysqldump failed ($LASTEXITCODE)" }
} finally { $env:MYSQL_PWD = $old }
$hash = (Get-FileHash -LiteralPath $Out -Algorithm SHA256).Hash
# one hash per table: the dump's blocks start with "DROP TABLE IF EXISTS `name`;"
$tables = [ordered]@{}
$sha = [Security.Cryptography.SHA256]::Create()
$cur = $null; $sb = New-Object Text.StringBuilder
foreach ($line in [IO.File]::ReadLines($Out)) {
    if ($line -match '^DROP TABLE IF EXISTS `([^`]+)`;') {
        if ($cur) { $tables[$cur] = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))).Replace('-', '') }
        $cur = $Matches[1]; [void]$sb.Clear()
    }
    [void]$sb.AppendLine($line)
}
if ($cur) { $tables[$cur] = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))).Replace('-', '') }
$tables.GetEnumerator() | ForEach-Object { "$($_.Key)`t$($_.Value)" } | Set-Content -Encoding ASCII "$Out.tables.tsv"
"$hash  $(Split-Path -Leaf $Out)" | Set-Content -Encoding ASCII "$Out.sha256"
"{0}: {1:N1} MB, {2} tables, sha256 {3} ({4:N0} s)" -f $Out, ((Get-Item $Out).Length / 1MB), $tables.Count, $hash, ((Get-Date) - $t0).TotalSeconds
if ($Compare) {
    $h0 = (Get-Content "$Compare.sha256").Split(' ')[0]
    if ($h0 -eq $hash) { "BYTE-IDENTICAL to $Compare"; "differences: 0"; return }
    $a = @{}; foreach ($l in Get-Content "$Compare.tables.tsv") { $k, $v = $l.Split("`t"); $a[$k] = $v }
    $b = @{}; foreach ($l in Get-Content "$Out.tables.tsv") { $k, $v = $l.Split("`t"); $b[$k] = $v }
    $diff = 0
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { "REMOVED table $k"; $diff++ } elseif ($a[$k] -ne $b[$k]) { "CHANGED table $k"; $diff++ } }
    foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { "ADDED table $k"; $diff++ } }
    # the differing lines of the changed tables (first 20 each way)
    $x = New-Object 'Collections.Generic.HashSet[string]'; foreach ($l in [IO.File]::ReadLines($Compare)) { [void]$x.Add($l) }
    $y = New-Object 'Collections.Generic.HashSet[string]'; foreach ($l in [IO.File]::ReadLines($Out)) { [void]$y.Add($l) }
    $onlyAfter = @($y | Where-Object { -not $x.Contains($_) }); $onlyBefore = @($x | Where-Object { -not $y.Contains($_) })
    "lines only after: $($onlyAfter.Count); lines only before: $($onlyBefore.Count)"
    $onlyAfter | Select-Object -First 20 | ForEach-Object { '  + ' + $(if ($_.Length -gt 200) { $_.Substring(0, 200) + '...' } else { $_ }) }
    $onlyBefore | Select-Object -First 20 | ForEach-Object { '  - ' + $(if ($_.Length -gt 200) { $_.Substring(0, 200) + '...' } else { $_ }) }
    "differences: $([Math]::Max($diff, 1))"
}
