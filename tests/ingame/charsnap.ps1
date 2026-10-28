# charsnap.ps1 -- the test character's rows in the CHARACTERS database (Statclone, guid 1000, account 9): a snapshot, a comparison, and an exact
# restore, for the designers test (run_designers.ps1), whose sessions teach the character a spell, give it quests, items and money.
#   .\charsnap.ps1 -Out <run>\char\char_before.sql                                       snapshot (READ-ONLY: mysqldump, SELECT only)
#   .\charsnap.ps1 -Out <run>\char\char_after.sql -Compare <run>\char\char_before.sql    snapshot + "differences: N"
#   .\charsnap.ps1 -Out <run>\char\char_check.sql -Restore <run>\char\char_before.sql    put the rows back exactly as in the snapshot, then
#                                                                                         snapshot again and compare ("differences: 0" required)
# The rows: every table of the characters database that holds rows of this character (by guid, owner, receiver, player or account), each with
# the WHERE clause below. A restore deletes those rows and inserts the snapshot's rows again, only while the character is offline and while
# holding the harness's machine-wide client mutex (no game can log in meanwhile). Rows of other characters are never touched (character_social
# rows that NAME this character as a friend are part of the snapshot and come back as they were).
# Not covered (not the character's rows): creature_respawn rows of the boars the sessions kill (world state; they expire at the respawn).
# The password is the harness's (tools\lib_wowtest.ps1, read from that file, passed in MYSQL_PWD, never printed).
# -RequireOffline: refuse to take the snapshot while the character is online (the run's BEFORE snapshot: a restore must bring back online=0).
# -Port N -NoPassword: a throwaway MySQL (the prepare's test of the restore, IngameRunner designers chartest); no client mutex then.
param([Parameter(Mandatory)][string]$Out, [string]$Compare = '', [string]$Restore = '', [int]$Guid = 1000, [int]$Account = 9, [switch]$RequireOffline,
      [int]$Port = 3306, [switch]$NoPassword)
$ErrorActionPreference = 'Stop'
$base = (Resolve-Path "$PSScriptRoot\..\..\..").Path   # the folder that holds WoWDataTool, WoWDecomp, Server and the clients
$bin = "$base\Server\mysql5\bin"
$G = $Guid
$tables = [ordered]@{
    'characters' = "guid=$G"; 'character_account_data' = "guid=$G"; 'character_action' = "guid=$G"; 'character_aura' = "guid=$G"
    'character_battleground_data' = "guid=$G"; 'character_deleted_items' = "player_guid=$G"; 'character_forgotten_skills' = "guid=$G"
    'character_gifts' = "guid=$G"; 'character_homebind' = "guid=$G"; 'character_honor_cp' = "guid=$G"; 'character_instance' = "guid=$G"
    'character_inventory' = "guid=$G"; 'character_pet' = "owner_guid=$G"; 'character_queststatus' = "guid=$G"; 'character_reputation' = "guid=$G"
    'character_skills' = "guid=$G"; 'character_social' = "guid=$G or friend=$G"; 'character_spell' = "guid=$G"; 'character_spell_cooldown' = "guid=$G"
    'character_stats' = "guid=$G"; 'character_tutorial' = "account=$Account"; 'corpse' = "player_guid=$G"; 'item_instance' = "owner_guid=$G"
    'item_loot' = "owner_guid=$G"; 'mail' = "receiver_guid=$G"; 'mail_items' = "receiver_guid=$G"; 'guild_member' = "guid=$G"
    'group_member' = "member_guid=$G"; 'petition' = "owner_guid=$G"; 'petition_sign' = "player_guid=$G"
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$lib = Get-Content "$base\WoWDecomp\tools\lib_wowtest.ps1" -Raw
if ($lib -notmatch '-u(\S+)\s+-p(\S+)\s') { throw 'could not read the harness credentials from lib_wowtest.ps1' }
$user = $Matches[1]; $pwd_ = $Matches[2]
if ($NoPassword) { $user = 'root'; $pwd_ = '' }
$live = $Port -eq 3306

# a native program, its whole output captured, stderr as text (never a terminating error), the exit code checked by the caller
function Invoke-My([string]$exe, [string[]]$a, [string]$stdinFile = '') {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = ($a | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = [bool]$stdinFile
    $psi.EnvironmentVariables['MYSQL_PWD'] = $pwd_
    $p = [Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    if ($stdinFile) { $fs = [IO.File]::OpenRead($stdinFile); $fs.CopyTo($p.StandardInput.BaseStream); $fs.Close(); $p.StandardInput.Close() }
    $o = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    return [pscustomobject]@{ Out = $o; Err = $errTask.Result; Code = $p.ExitCode }
}
function Q1([string]$sql) {
    $r = Invoke-My "$bin\mysql.exe" @('--host=127.0.0.1', "--port=$Port", "-u$user", '-N', '-B', 'characters', '-e', $sql)
    if ($r.Code -ne 0) { throw "query failed: $($r.Err.Trim())" }
    return $r.Out.Trim()
}
function Snapshot([string]$file) {
    $sb = New-Object Text.StringBuilder
    foreach ($t in $tables.Keys) {
        [void]$sb.AppendLine("-- TABLE $t WHERE $($tables[$t])")
        $r = Invoke-My "$bin\mysqldump.exe" @('--host=127.0.0.1', "--port=$Port", "-u$user", '--skip-lock-tables', '--single-transaction', '--no-create-info', '--compact',
            '--skip-extended-insert', '--complete-insert', '--order-by-primary', '--hex-blob', '--default-character-set=utf8', "--where=$($tables[$t])", 'characters', $t)
        if ($r.Code -ne 0) { throw "mysqldump of $t failed: $($r.Err.Trim())" }
        foreach ($l in ($r.Out -split "`r?`n")) { if ($l -like 'INSERT INTO*') { [void]$sb.AppendLine($l) } }
    }
    [IO.File]::WriteAllText($file, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
    $h = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    "$h  $(Split-Path -Leaf $file)" | Set-Content -Encoding ASCII "$file.sha256"
    $rows = @([IO.File]::ReadAllLines($file) | Where-Object { $_ -like 'INSERT INTO*' }).Count
    "{0}: {1} row(s) of guid {2} in {3} table(s), sha256 {4}" -f $file, $rows, $G, $tables.Count, $h
}
# the value of a column in the snapshot's characters row (the column list is in the INSERT: --complete-insert)
function Get-CharColumn([string[]]$lines, [string]$col) {
    $row = @($lines | Where-Object { $_.StartsWith('INSERT INTO `characters`') })[0]
    $m = [regex]::Match($row, '^INSERT INTO `characters` \((.*?)\) VALUES \((.*)\);$')
    if (-not $m.Success) { return $null }
    $names = @($m.Groups[1].Value -split ', ' | ForEach-Object { $_.Trim('`') })
    $i = [Array]::IndexOf($names, $col)
    if ($i -lt 0) { return $null }
    # the values up to that column are numbers or quoted strings without commas in the test character's row up to `online`; checked by the caller
    return ($m.Groups[2].Value -split ',')[$i]
}
function Compare-Snap([string]$a, [string]$b) {   # (not "Diff": that is an alias of Compare-Object, and aliases win over functions)
    $ha = (Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash; $hb = (Get-FileHash -LiteralPath $b -Algorithm SHA256).Hash
    if ($ha -eq $hb) { "IDENTICAL to $a"; 'differences: 0'; return }
    $x = [IO.File]::ReadAllLines($a); $y = [IO.File]::ReadAllLines($b)
    $sx = New-Object 'Collections.Generic.HashSet[string]'; foreach ($l in $x) { [void]$sx.Add($l) }
    $sy = New-Object 'Collections.Generic.HashSet[string]'; foreach ($l in $y) { [void]$sy.Add($l) }
    $only1 = @($x | Where-Object { -not $sy.Contains($_) }); $only2 = @($y | Where-Object { -not $sx.Contains($_) })
    "lines only in $(Split-Path -Leaf $a): $($only1.Count); only in $(Split-Path -Leaf $b): $($only2.Count)"
    $only1 | Select-Object -First 15 | ForEach-Object { '  - ' + $(if ($_.Length -gt 220) { $_.Substring(0, 220) + '...' } else { $_ }) }
    $only2 | Select-Object -First 15 | ForEach-Object { '  + ' + $(if ($_.Length -gt 220) { $_.Substring(0, 220) + '...' } else { $_ }) }
    "differences: $([Math]::Max(1, $only1.Count + $only2.Count))"
}

if ($Restore) {
    if (-not (Test-Path $Restore)) { throw "no snapshot $Restore" }
    $snap = [IO.File]::ReadAllLines($Restore)
    # the snapshot must be of this character, with exactly these tables and clauses
    $heads = @($snap | Where-Object { $_ -like '-- TABLE *' })
    $want = @($tables.Keys | ForEach-Object { "-- TABLE $_ WHERE $($tables[$_])" })
    if (($heads -join "`n") -ne ($want -join "`n")) { throw "$Restore is not a snapshot of guid $G with this script's tables" }
    if (@($snap | Where-Object { $_.StartsWith('INSERT INTO `characters`') }).Count -ne 1) { throw "$Restore has no characters row of guid $G" }
    if ((Get-CharColumn $snap 'online') -ne '0') { throw "$Restore was taken while the character was online (online=$(Get-CharColumn $snap 'online')): restoring it would mark it online" }
    $mtx = $null
    if ($live) {
        $mtx = New-Object Threading.Mutex($false, 'Global\WoWDecompClient')   # no game of the harness can log in while the rows are rewritten
        try { [void]$mtx.WaitOne() } catch [Threading.AbandonedMutexException] { }
    }
    try {
        # offline first (a closed game stays in the world ~60 s; the server saves the character when it leaves): up to 8 minutes
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ((Q1 "select online from characters where guid=$G") -ne '0') {
            if ($sw.Elapsed.TotalSeconds -gt 480) { throw "the character is still online after 480 s: not restored" }
            Start-Sleep 4
        }
        $sql = New-Object Text.StringBuilder
        [void]$sql.AppendLine('SET autocommit=0;'); [void]$sql.AppendLine('START TRANSACTION;')
        $cur = $null
        foreach ($l in $snap) {
            if ($l -match '^-- TABLE (\S+) WHERE (.+)$') { [void]$sql.AppendLine("DELETE FROM ``$($Matches[1])`` WHERE $($Matches[2]);"); continue }
            if ($l -like 'INSERT INTO*') { [void]$sql.AppendLine($l) }
        }
        [void]$sql.AppendLine('COMMIT;')
        $tmp = [IO.Path]::Combine((Split-Path -Parent $Out), 'char_restore_' + (Get-Date -Format 'HHmmss') + '.sql')
        [IO.File]::WriteAllText($tmp, $sql.ToString(), (New-Object Text.UTF8Encoding($false)))
        if ((Q1 "select online from characters where guid=$G") -ne '0') { throw 'the character came online: not restored' }
        $r = Invoke-My "$bin\mysql.exe" @('--host=127.0.0.1', "--port=$Port", "-u$user", '--default-character-set=utf8', 'characters') $tmp
        if ($r.Code -ne 0) { throw "restore failed (exit $($r.Code)): $($r.Err.Trim())" }
        "restored guid $G from $Restore ($(@($snap | Where-Object { $_ -like 'INSERT INTO*' }).Count) row(s)); script $tmp"
        Snapshot $Out
        Compare-Snap $Restore $Out
    } finally { if ($mtx) { $mtx.ReleaseMutex() } }
    return
}
if ($RequireOffline -and (Q1 "select online from characters where guid=$G") -ne '0') { throw "the character is online: no snapshot taken (-RequireOffline)" }
Snapshot $Out
if ($RequireOffline -and (Get-CharColumn ([IO.File]::ReadAllLines($Out)) 'online') -ne '0') { throw "the character came online during the snapshot: take it again" }
if ($Compare) { Compare-Snap $Compare $Out }
