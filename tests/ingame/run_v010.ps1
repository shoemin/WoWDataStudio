# run_v010.ps1 -- the whole in-game visual test of WoW Data Studio 0.9.0 (server data) and 0.10.0 (skeleton and animations), in order,
# with safety checks, and everything put back at the end even after a failure (try / finally).
# Prepared first (no game, no server writes): IngameRunner v010 prepare -> <run>\studio_home (workspace with the client edits A1-A5,
# the server edits S1-S3 and the Spell.dbc row of S3), <run>\server\*.sql, <run>\sessions_v010.json; IngameRunner v010 dryrun (apply + undo
# against a throwaway MySQL); render_v010.ps1.
#
#   .\run_v010.ps1 -Run <run> -Plan      read-only: every safety check, then the steps it WOULD take (nothing is started or written)
#   .\run_v010.ps1 -Run <run>            the real run (about 75-80 minutes)
#
# Steps: 1 safety checks  2 snapshots (Client + Client1..6 files; mangos dump, read-only)  3 baseline sessions (nothing installed, server
# untouched)  4 Apply to server (the app's backup) + world server restart  5 sessions "srvonly" (server applied, client not)  6 loose
# files + sessions  7 patch-3.MPQ + sessions  FINALLY: remove loose files, uninstall patch-3.MPQ, Undo last apply, world server restart,
# "afterundo" session, WDB cache cleared and Client_visual's own cache put back, test character restored, mangos dump compared
# (byte-identical required), Client + Client1..6 compared (0 differences required), leftovers checked, summary.
param([Parameter(Mandatory)][string]$Run, [switch]$Plan,
      [string[]]$States = @('before', 'srvonly', 'loose', 'patch3', 'afterundo'), [string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$base = '<your WoW Custom Client folder>'
$decomp = "$base\WoWDecomp"
$client = "$base\Client_visual"
$exe = "$here\IngameRunner\bin\Release\net8.0\IngameRunner.exe"
$home_ = "$Run\studio_home"
$logFile = "$Run\run_v010.log"
New-Item -ItemType Directory -Force "$Run\shots", "$Run\safety", "$Run\db", "$Run\server" | Out-Null
. "$decomp\tools\lib_wowtest.ps1"    # Q, Wait-CharOffline (the harness's; credentials never printed)

# a log line must never stop the run (2026-10-14: a reader holding run_v010.log open made Add-Content a terminating error): retry, then skip
function Add-LogLine([string]$l) {
    for ($i = 0; $i -lt 10; $i++) { try { [IO.File]::AppendAllText($logFile, $l + "`r`n"); return } catch { Start-Sleep -Milliseconds 200 } }
}
function Log([string]$s) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $s; $l; Add-LogLine $l }
$state = [ordered]@{ applied = $false; undone = $false; serverHasTestRows = $false; restartNeeded = $false; wdbOriginal = $null; failures = @() }
function Fail([string]$s) { $state.failures += $s; Log "!! $s" }

# ------------------------------------------------------------------ safety checks
function Get-Blockers {
    $b = @()
    # (1) the user's rule: a chain log written in the last 10 minutes that does not end with "=== chainNNN done"
    foreach ($f in Get-ChildItem "$decomp\work" -Filter 'chain_*.log' -ErrorAction SilentlyContinue) {
        if ($f.LastWriteTime -gt (Get-Date).AddMinutes(-10)) {
            $tail = (Get-Content -LiteralPath $f.FullName -Tail 5) -join "`n"
            if ($tail -notmatch '=== chain\S* done') { $b += "chain log $($f.Name) active (written $($f.LastWriteTime.ToString('HH:mm:ss')), no '=== chain... done')" }
        }
    }
    # (2) a chain script or the console-mode world server of a test still running (a difftest can be quiet in its log for > 10 min)
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'") {
        if ($p.CommandLine -match 'chain_\w+\.ps1') { $b += "chain script running: pid $($p.ProcessId) $([regex]::Match($p.CommandLine, 'chain_\w+\.ps1').Value)" }
        if ($p.CommandLine -match 'srvconsole\.ps1') { $b += "console-mode world server (tools\srvconsole.ps1, a test's) running: pid $($p.ProcessId)" }
    }
    # (3) a game of the main client or a wave clone
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='WoW.exe' OR Name='WoWDebug.exe'") {
        $path = $p.ExecutablePath
        if (-not $path -or $path -notlike "$client\*") { $b += "game running outside Client_visual: pid $($p.ProcessId) $path" }
    }
    return $b
}
function Test-Ports { $miss = @(); foreach ($port in 3306, 3724, 8085) { if (-not (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)) { $miss += $port } }; return $miss }
function Assert-Safe([string]$where, [switch]$NoPorts) {
    $b = @(Get-Blockers)
    if (-not $NoPorts) { $m = @(Test-Ports); if ($m.Count) { $b += "server ports not listening: $($m -join ', ') (MySQL 3306, realmd 3724, mangosd 8085)" } }
    if ($b.Count -and $Plan) { $b | ForEach-Object { Log "WOULD REFUSE ($where): $_" }; return }
    if ($b.Count) { $b | ForEach-Object { Log "SAFETY ($where): $_" }; throw "safety check failed before: $where" }
    Log "safety ok before: $where"
}
function Get-WorldServer {
    $m = @(Get-CimInstance Win32_Process -Filter "Name='mangosd.exe'")
    if ($m.Count -ne 1) { throw "expected exactly one mangosd.exe, found $($m.Count)" }
    if ($m[0].ExecutablePath -ne "$base\Server\srv16\mangosd.exe") { throw "mangosd is not the 16-bit world server: $($m[0].ExecutablePath)" }
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($m[0].ParentProcessId)" -ErrorAction SilentlyContinue
    if ($parent -and $parent.CommandLine -match 'srvconsole') { throw 'mangosd runs in console mode (a test owns it)' }
    return $m[0]
}

# ------------------------------------------------------------------ steps
# Native calls (2026-10-14, run 20261013_2308_v010): (1) never pipe a native program into `Select-Object -First N` -- once N lines
# arrived, Windows PowerShell stops the pipeline and KILLS the still-running program (Process.Kill -> exit code -1), even when it already
# wrote everything and was only shutting down: the IngameRunner server plan was "refused (exit -1)" with a complete, clean plan in its log.
# Capture all output, then pick lines. (2) stderr merged with 2>&1 under ErrorActionPreference=Stop is a terminating error on its first
# line (the output and the exit code are lost): the native call runs with ErrorActionPreference=Continue (function scope).
function Invoke-Exe([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    $o = @(& $exe @a 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ Out = $o; Code = $LASTEXITCODE }
}
function IR([string[]]$a) {
    Log ("IngameRunner " + ($a -join ' '))
    if ($Plan) { return }
    $r = Invoke-Exe $a
    $out = $r.Out
    $code = $r.Code
    $out | ForEach-Object { Add-LogLine "    $_" }
    $out | Select-Object -Last 6 | ForEach-Object { "    $_" }
    if ($code -ne 0) { throw "IngameRunner $($a[0..1] -join ' ') failed (exit $code)" }
}
function Deliver([string]$mode) { IR @('deliver', $client, $home_, $Run, $mode) }
function ClearWdb { IR @('v010', 'wdb', $client, $home_, $Run) }
function Restart-WorldServer([string]$why) {
    Log "world server restart ($why)"
    if ($Plan) { Log '  (plan) Wait-CharOffline; Stop-Process mangosd (srv16) -Force; tools\start_servers.ps1; wait for port 8085'; return }
    Assert-Safe "restart ($why)"
    Wait-CharOffline
    $m = Get-WorldServer
    $t0 = Get-Date
    Stop-Process -Id $m.ProcessId -Force          # the project's stop (tools\regress.ps1, tools\srvmode.ps1); the character is offline
    for ($i = 0; $i -lt 60 -and (Get-Process -Id $m.ProcessId -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    for ($i = 0; $i -lt 30 -and (Get-NetTCPConnection -State Listen -LocalPort 8085 -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    & "$decomp\tools\start_servers.ps1" 2>&1 | ForEach-Object { Log "  $_" }
    if (-not (Get-NetTCPConnection -State Listen -LocalPort 8085 -ErrorAction SilentlyContinue)) { throw 'mangosd did not come back (port 8085)' }
    Start-Sleep 10                                 # "World initialized" comes with the port; let the grid loader settle
    $null = Get-WorldServer
    Log ("world server up again after {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds)
    # what the server said about the test rows at start (its log folder)
    foreach ($lf in Get-ChildItem "$base\Server\logs" -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $t0 }) {
        $hits = @(Select-String -LiteralPath $lf.FullName -Pattern '90000[01]' -ErrorAction SilentlyContinue | Select-Object -First 20)
        if ($hits.Count) { Log "  server log $($lf.Name) mentions the test IDs:"; $hits | ForEach-Object { Log "    $($_.Line)" } }
    }
}
$viewpoints = if (Test-Path "$Run\sessions_v010.json") { Get-Content "$Run\sessions_v010.json" -Raw | ConvertFrom-Json } else { @() }
function Sessions([string]$tag, [string[]]$keys) {
    foreach ($vp in $viewpoints) {
        if ($keys -notcontains $vp.key) { continue }
        if ($Only.Count -and $Only -notcontains $vp.key) { continue }
        Log "session $($vp.key) [$tag] $($vp.lua) at $($vp.pos) camera $($vp.camera)"
        if ($Plan) { continue }
        Assert-Safe "session $($vp.key) $tag"
        $t0 = Get-Date
        for ($try = 1; $try -le 2; $try++) {
            $a = @{ Scenario = "$here\lua\$($vp.lua)"; Tag = $tag; Pos = $vp.pos; Camera = $vp.camera; OutDir = "$Run\shots"; Stay = 170 }
            if ($vp.key -eq 'b1') { $a.BurstAll = $true }
            # a session that stops with an error is a failed attempt (retried once), not the end of the whole run (2026-10-14)
            try { $out = & "$here\session.ps1" @a 2>&1 | Out-String -Width 250 } catch { $out = "session stopped: $($_.Exception.Message)" }
            Add-LogLine $out
            ($out -split "`n" | Where-Object { $_ -match 'result=|error dialog|scenario (done|note)' } | Select-Object -First 30) | ForEach-Object { "    $_".TrimEnd() }
            if ($out -match 'result=SUCCESS') { break }
            Fail "session $($vp.key) [$tag] attempt $try did not succeed"
        }
        Log ("session $($vp.key) [$tag] done in {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds)
    }
}
$all = @('b1', 'q1', 'c1', 'c2', 'v1')

# ================================================================== 1. preflight
Log "run_v010 $(if ($Plan) { 'PLAN (read-only)' } else { 'START' }) -- run folder $Run"
foreach ($f in $exe, "$here\session.ps1", "$here\snapshot.ps1", "$here\dbsnap.ps1", "$here\restore_char.ps1", "$decomp\tools\start_servers.ps1", "$Run\sessions_v010.json", "$Run\app_report.json") {
    if (-not (Test-Path $f)) { throw "missing: $f" }
}
if (-not (Test-Path "$base\Client\wowauto.txt")) { throw 'Client\wowauto.txt (the harness master auto-login file) is missing: the sessions cannot log in. Only the user may restore it.' }
if ((Get-Item "$client\Data" -Force).LinkType) { throw 'Client_visual\Data is a link: not the dedicated test client' }
$rep = Get-Content "$Run\app_report.json" -Raw | ConvertFrom-Json
$bad = @($rep | Where-Object { -not $_.Ok }); if ($bad.Count) { throw "prepared items not ok: $($bad.Id -join ', ')" }
Log "prepared items ok: $(($rep | ForEach-Object Id) -join ', ')"
$free = (Get-PSDrive C).Free / 1GB; if ($free -lt 5) { $msg = "only {0:N1} GB free on C: (the frame series need up to 2.5 GB at a time, the results about 1.5 GB)" -f $free; if ($Plan) { Log "WOULD REFUSE: $msg" } else { throw $msg } }
Log ("free disk: {0:N0} GB" -f $free)
Assert-Safe 'start'
try { $ws = Get-WorldServer; Log "world server: pid $($ws.ProcessId) $($ws.ExecutablePath)" } catch { if ($Plan) { Log "WOULD REFUSE (world server): $($_.Exception.Message)" } else { throw } }
if ((Q characters 'select online from characters where guid=1000').Trim() -ne '0') { Log 'test character is online now: the sessions wait for it (Wait-CharOffline)' }
if (Test-Path "$client\Data\patch-3.MPQ") { throw 'Client_visual already has a patch-3.MPQ: look at it first (the app would back it up)' }
# the server plan: new rows with custom IDs only, nothing of an earlier test on the server (exit 4 = refused by the safety rule)
$pr = Invoke-Exe @('v010', 'server', $client, $home_, $Run, 'plan')     # the whole output first (see Invoke-Exe), then the lines to log
$pr.Out | Select-Object -First 3 | ForEach-Object { Log "  $_" }
$pr.Out | Where-Object { $_ -match '^(World server running|Conflicts|REFUSED|ERROR)' } | ForEach-Object { Log "  $_" }
if ($pr.Code -ne 0) {
    $pr.Out | Select-Object -Last 5 | ForEach-Object { Log "  $_" }
    if ($Plan) { Log "WOULD REFUSE: the server plan is refused (exit $($pr.Code))" } else { throw "the server plan is refused (exit $($pr.Code)): see $Run\server\server_log.txt" }
}
if ($Plan) {
    Log 'PLAN: the steps of the real run follow (nothing is executed)'
}

# ================================================================== 2. snapshots (read-only)
Log 'snapshot Client + Client1..6 (before)'
if (-not $Plan) { & "$here\snapshot.ps1" -Out "$Run\safety\clients_before.tsv" | ForEach-Object { Log "  $_" } }
Log 'mangos dump (before, read-only)'
if (-not $Plan) { & "$here\dbsnap.ps1" -Out "$Run\db\mangos_before.sql" | ForEach-Object { Log "  $_" } }
$wdbBackups = "$home_\$(Split-Path -Leaf (Get-ChildItem $home_ -Directory | Select-Object -First 1))\backups"
$wdbBefore = @(Get-ChildItem $wdbBackups -Directory -Filter 'wdb_*' -ErrorAction SilentlyContinue | ForEach-Object FullName)

try {
    # ============================================================== 3. baseline
    if ($States -contains 'before') {
        Log '==== state before: nothing installed, server untouched'
        Deliver 'unloose'; Deliver 'patchstate'
        ClearWdb
        if (-not $Plan) { $state.wdbOriginal = @(Get-ChildItem $wdbBackups -Directory -Filter 'wdb_*' -ErrorAction SilentlyContinue | ForEach-Object FullName | Where-Object { $wdbBefore -notcontains $_ }) | Select-Object -First 1; Log "Client_visual's own cache kept in: $($state.wdbOriginal)" }
        Sessions 'before' $all
    }
    # ============================================================== 4. apply to the server
    Log '==== Apply to server (the app: backup of every row and table, one transaction, read back, logged)'
    Assert-Safe 'apply' -NoPorts:$Plan
    if (-not $Plan) {
        & "$here\dbsnap.ps1" -Out "$Run\db\mangos_before_apply.sql" -Compare "$Run\db\mangos_before.sql" | Select-Object -Last 1 | ForEach-Object { Log "  database unchanged since the first dump: $_" }
    }
    IR @('v010', 'server', $client, $home_, $Run, 'apply')
    if (-not $Plan) { $state.applied = $true; $state.serverHasTestRows = $true; $state.restartNeeded = $true }
    Restart-WorldServer 'spell_template needs a restart; the other tables are read at start too'
    if (-not $Plan) { $state.restartNeeded = $false }
    # ============================================================== 5. server only
    if ($States -contains 'srvonly') {
        Log '==== state srvonly: server applied, nothing installed in the game (S3: the draught has no "Use:" line yet)'
        ClearWdb
        Sessions 'srvonly' @('v1')
    }
    # ============================================================== 6. loose files
    if ($States -contains 'loose') {
        Log '==== state loose: Test all in game (loose files) + server applied'
        Assert-Safe 'loose install'
        Deliver 'loose'
        ClearWdb
        Sessions 'loose' $all
    }
    # ============================================================== 7. patch-3.MPQ
    if ($States -contains 'patch3') {
        Log '==== state patch3: Remove all from game, Build release patch + install'
        Assert-Safe 'patch build'
        Deliver 'unloose'; Deliver 'patch'
        ClearWdb
        Sessions 'patch3' $all
    }
}
catch { Fail "run stopped: $($_.Exception.Message)" }
finally {
    Log '==== cleanup (always)'
    foreach ($step in @(
        @{ n = 'remove loose files (Remove all from game)'; s = { Deliver 'unloose' } },
        @{ n = 'uninstall patch-3.MPQ (Uninstall release patch)'; s = { Deliver 'uninstall'; Deliver 'patchstate' } },
        @{ n = 'Undo last apply'; s = {
            if ($state.applied -and -not $state.undone) {
                # the database undo does not touch a running chain; only the restart below waits for one
                IR @('v010', 'server', $client, $home_, $Run, 'undo')
                if (-not $Plan) { $state.undone = $true; $state.restartNeeded = $true }
            } elseif ($Plan) { IR @('v010', 'server', $client, $home_, $Run, 'undo') } } },
        @{ n = 'world server restart (forget the test rows)'; s = {
            if ($state.restartNeeded -or $Plan) {
                if (@(Get-Blockers).Count) { Fail 'a chain / test is running now: the world server was NOT restarted; it still holds the test rows in memory until its next restart' }
                else { Restart-WorldServer 'after Undo last apply'; $state.restartNeeded = $false; $state.serverHasTestRows = $false }
            } } },
        @{ n = 'afterundo session (the vendor and the items are gone)'; s = {
            if ($States -contains 'afterundo' -and ($state.undone -or $Plan) -and -not $state.restartNeeded) { ClearWdb; Sessions 'afterundo' @('v1') } } },
        @{ n = "WDB cache of Client_visual: clear the test's cache, put its own back"; s = {
            ClearWdb
            if ($state.wdbOriginal -and (Test-Path $state.wdbOriginal)) {
                Copy-Item "$($state.wdbOriginal)\*" "$client\WDB\" -Recurse -Force
                Log "  restored $((Get-ChildItem $state.wdbOriginal -Recurse -File).Count) cache file(s) from $($state.wdbOriginal)"
            } } },
        @{ n = 'restore the test character (restore_char.ps1)'; s = { if (-not $Plan) { & "$here\restore_char.ps1" | ForEach-Object { Log "  $_" } } } },
        @{ n = 'mangos dump (after) compared with before: byte-identical required'; s = {
            if (-not $Plan) {
                $cmp = & "$here\dbsnap.ps1" -Out "$Run\db\mangos_after.sql" -Compare "$Run\db\mangos_before.sql"
                $cmp | ForEach-Object { Log "  $_" }
                if (($cmp | Select-Object -Last 1) -ne 'differences: 0') { Fail 'the mangos dump after the run differs from before' }
                else { Remove-Item "$Run\db\mangos_after.sql", "$Run\db\mangos_before_apply.sql*" -ErrorAction SilentlyContinue }   # identical: the hashes stay
            } } },
        @{ n = 'Client + Client1..6 compared with before: 0 differences required'; s = {
            if (-not $Plan) {
                $cmp = & "$here\snapshot.ps1" -Out "$Run\safety\clients_after.tsv" -Compare "$Run\safety\clients_before.tsv"
                $cmp | Select-Object -Last 30 | ForEach-Object { Log "  $_" }
                if (($cmp | Select-Object -Last 1) -ne 'differences: 0') { Fail 'Client / Client1..6 changed' }
            } } },
        @{ n = 'leftovers'; s = {
            foreach ($f in "$client\wowauto.txt", "$client\wowprobe.lua", "$client\Data\patch-3.MPQ") { if (Test-Path $f) { Fail "left over: $f" } }
            $loose = @(Get-ChildItem "$client\Data" -Recurse -File | Where-Object { $_.Extension -ne '.MPQ' -and $_.FullName -notlike '*\Cinematics\*' })
            if ($loose.Count) { Fail "loose files left in Client_visual\Data: $($loose.Count)" }
            if (-not (Test-Path "$base\Client\wowauto.txt")) { Fail 'Client\wowauto.txt is missing!' }
            if (-not $Plan -and (Q mangos 'select count(*) from item_template where entry in (900000,900001)').Trim() -ne '0') { Fail 'test items still on the server' } } }
    )) {
        try { Log "cleanup: $($step.n)"; & $step.s } catch { Fail "cleanup step '$($step.n)' failed: $($_.Exception.Message)" }
    }
    $sum = [ordered]@{ finished = (Get-Date).ToString('s'); plan = [bool]$Plan; applied = $state.applied; undone = $state.undone; worldServerStillHasTestRows = $state.serverHasTestRows; failures = $state.failures }
    $sum | ConvertTo-Json | Set-Content -Encoding UTF8 "$Run\run_v010_summary.json"
    Log ("==== {0}: {1} failure(s)" -f $(if ($Plan) { 'PLAN done' } else { 'run done' }), $state.failures.Count)
    if (-not $Plan) { Log "next: python make_gallery_v010.py $Run  (after writing $Run\judgement.json from the pictures)" }
}
