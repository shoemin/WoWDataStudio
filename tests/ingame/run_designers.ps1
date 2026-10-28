# run_designers.ps1 -- the in-game visual test of WoW Data Studio's designers: 0.12.0 (spell designer, class and talent designer) and 0.13.0
# (quest-chain designer), in order, with safety checks at every step, and everything put back at the end even after a failure (try / finally).
# Prepared first (no game, no server writes; the results are the run folder's app_report.json, server, dryrun and chartest):
#   IngameRunner designers prepare CLIENT <run>\studio_home <run>      S1 (spell + visual + trainer), T1 (Destruction tree), Q1 (quest chain),
#                                                                     each saved by the designer into the isolated workspace; <run>\server\*.sql,
#                                                                     <run>\ids.json, <run>\sessions_designers.json, <run>\lua\*.lua
#   IngameRunner designers dryrun  CLIENT <run>\studio_home <run> DUMP   Apply + Undo last apply on a throwaway MySQL (byte-identical required)
#   IngameRunner designers chartest CLIENT <run>\studio_home <run> SNAP  charsnap.ps1 -Restore on a throwaway MySQL (exact restore required)
#
#   .\run_designers.ps1 -Run <run> -Plan      read-only: every safety check, then the steps it WOULD take (nothing is started or written)
#   .\run_designers.ps1 -Run <run>            the real run (about 60-70 minutes)
#   -States before,srvonly,loose,patch3,afterundo (default: all)   -Only q1,t1,c1,k1 (default: all viewpoints; also by name:
#   quest = q1, trainer = t1, cast = c1, talent = k1; e.g. -Only quest -> only the quest chain's sessions, with the whole apply / undo
#   cycle and every safety check)
# Watch run_designers.console.txt (tee the console there), never run_designers.log with a tool that holds it open.
#
# Steps: 1 safety checks + preflight (prepared items ok, dry run and restore test passed, server plan accepted)  2 snapshots (Client + Client1..6
# files; mangos dump; the test character's rows in the characters database, taken offline)  3 baseline sessions (nothing installed, server
# untouched): q1 (no giver), c1 (the stock Shadow Bolt: the reference visual), k1 (the stock tree)  4 Apply to server (the app's backup) + world
# server restart (tools\start_servers.ps1)  5 "srvonly": q1 (server rows, no client tables)  6 loose files: q1, t1, c1, k1  7 patch-3.MPQ: q1, t1,
# c1, k1. Before every q1 and t1 session the character's rows are put back exactly as in the run's first snapshot (charsnap.ps1 -Restore), so
# each starts without the spell and the quests; c1 follows t1 (the spell bought there).
# FINALLY: remove loose files, uninstall patch-3.MPQ, Undo last apply, world server restart, "afterundo" q1 (the NPCs are gone), WDB cache cleared
# and Client_visual's own cache put back, restore_char.ps1 + the exact restore of the character's rows (compared: 0 differences required), mangos
# dump compared (byte-identical required), Client + Client1..6 compared (0 differences required), leftovers checked, summary.
param([Parameter(Mandatory)][string]$Run, [switch]$Plan,
      [string[]]$States = @('before', 'srvonly', 'loose', 'patch3', 'afterundo'), [string[]]$Only = @())
$ErrorActionPreference = 'Stop'
# 2026-10-16: -Only by name as well (quest, trainer, cast, talent)
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { switch ($_.Trim().ToLowerInvariant()) { 'quest' { 'q1' } 'trainer' { 't1' } 'cast' { 'c1' } 'talent' { 'k1' } default { $_.Trim() } } })
$here = $PSScriptRoot
$base = (Resolve-Path "$PSScriptRoot\..\..\..").Path   # the folder that holds WoWDataTool, WoWDecomp, Server and the clients
$decomp = "$base\WoWDecomp"
$client = "$base\Client_visual"
$exe = "$here\IngameRunner\bin\Release\net8.0\IngameRunner.exe"
$home_ = "$Run\studio_home"
$logFile = "$Run\run_designers.log"
New-Item -ItemType Directory -Force "$Run\shots", "$Run\safety", "$Run\db", "$Run\server", "$Run\char" | Out-Null
. "$decomp\tools\lib_wowtest.ps1"    # Q, Wait-CharOffline (the harness's; credentials never printed)

# a log line must never stop the run (2026-10-14: a reader holding the log open made Add-Content a terminating error): retry, then skip
function Add-LogLine([string]$l) {
    for ($i = 0; $i -lt 10; $i++) { try { [IO.File]::AppendAllText($logFile, $l + "`r`n"); return } catch { Start-Sleep -Milliseconds 200 } }
}
function Log([string]$s) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $s; $l; Add-LogLine $l }
$state = [ordered]@{ applied = $false; undone = $false; serverHasTestRows = $false; restartNeeded = $false; wdbOriginal = $null; charChanged = $false
                     restores = 0; failures = @() }
function Fail([string]$s) { $state.failures += $s; Log "!! $s" }
$t0run = Get-Date

# ------------------------------------------------------------------ safety checks (the same blockers as run_v010.ps1)
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
function Wait-Offline([int]$seconds = 480) {
    # 2026-10-14 (hot reload run): a session's wait must exceed the character's logout (~2 min after the game closed); Wait-CharOffline gives up
    # after 200 s, so this waits up to 8 minutes
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Q characters 'select online from characters where guid=1000').Trim() -ne '0') {
        if ($sw.Elapsed.TotalSeconds -gt $seconds) { throw "the test character is still online after $seconds s" }
        Start-Sleep 4
    }
}

# ------------------------------------------------------------------ steps
# Native calls (2026-10-14, run 20261013_2308_v010): never pipe a native program into `Select-Object -First N` (Windows PowerShell kills the
# still-running program: exit -1); stderr merged with 2>&1 under ErrorActionPreference=Stop is a terminating error on its first line -- the
# native call runs to the end with ErrorActionPreference=Continue (function scope), then the lines are picked.
function Invoke-Exe([string[]]$a) {
    $ErrorActionPreference = 'Continue'
    $o = @(& $exe @a 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ Out = $o; Code = $LASTEXITCODE }
}
function IR([string[]]$a) {
    Log ("IngameRunner " + ($a -join ' '))
    if ($Plan) { return }
    $r = Invoke-Exe $a
    $r.Out | ForEach-Object { Add-LogLine "    $_" }
    $r.Out | Select-Object -Last 6 | ForEach-Object { "    $_" }
    if ($r.Code -ne 0) { throw "IngameRunner $($a[0..1] -join ' ') failed (exit $($r.Code))" }
}
# a PowerShell script of this folder: its output captured (stderr as text), never fatal by itself; the caller checks the result lines
function Invoke-Ps([string]$script, [hashtable]$a) {
    $ErrorActionPreference = 'Continue'
    try { return @(& $script @a 2>&1 | ForEach-Object { "$_" }) } catch { return @("script failed: $($_.Exception.Message)") }
}
function Deliver([string]$mode) { IR @('deliver', $client, $home_, $Run, $mode) }
function ClearWdb { IR @('designers', 'wdb', $client, $home_, $Run) }
# the character's rows back exactly as in the run's first snapshot (offline, under the client mutex), compared: 0 differences required
function Restore-Char([string]$why) {
    Log "character rows restored to the first snapshot ($why)"
    if ($Plan) { Log "  (plan) charsnap.ps1 -Restore $Run\char\char_before.sql (waits until the character is offline; 'differences: 0' required)"; return }
    Assert-Safe "character restore ($why)"
    $state.restores++
    $o = Invoke-Ps "$here\charsnap.ps1" @{ Out = "$Run\char\restore_$($state.restores).sql"; Restore = "$Run\char\char_before.sql" }
    $o | ForEach-Object { Add-LogLine "    $_" }
    $o | Select-Object -Last 2 | ForEach-Object { Log "  $_" }
    if (($o | Select-Object -Last 1) -ne 'differences: 0') { throw "the character's rows could not be restored ($why)" }
    Remove-Item "$Run\char\restore_$($state.restores).sql*" -ErrorAction SilentlyContinue   # identical to char_before.sql
}
function Restart-WorldServer([string]$why) {
    Log "world server restart ($why)"
    if ($Plan) { Log '  (plan) Wait-Offline; Stop-Process mangosd (srv16) -Force; tools\start_servers.ps1; wait for port 8085; the server log checked for the test IDs'; return }
    Assert-Safe "restart ($why)"
    Wait-Offline
    $m = Get-WorldServer
    $t0 = Get-Date
    Stop-Process -Id $m.ProcessId -Force          # the project's stop (tools\regress.ps1, tools\srvmode.ps1); the character is offline
    for ($i = 0; $i -lt 60 -and (Get-Process -Id $m.ProcessId -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    for ($i = 0; $i -lt 30 -and (Get-NetTCPConnection -State Listen -LocalPort 8085 -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    $o = Invoke-Ps "$decomp\tools\start_servers.ps1" @{}
    $o | ForEach-Object { Log "  $_" }
    if (-not (Get-NetTCPConnection -State Listen -LocalPort 8085 -ErrorAction SilentlyContinue)) { throw 'mangosd did not come back (port 8085)' }
    Start-Sleep 10                                 # "World initialized" comes with the port; let the grid loader settle
    $null = Get-WorldServer
    Log ("world server up again after {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds)
    # what the server said about the test rows at start (its log folder): a quest, trainer or spell row it refused shows here
    foreach ($lf in Get-ChildItem "$base\Server\logs" -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $t0 }) {
        $hits = @(Select-String -LiteralPath $lf.FullName -Pattern '\b(9000\d\d|3[5-9]\d\d\d)\b' -ErrorAction SilentlyContinue | Select-Object -First 30)
        if ($hits.Count) { Log "  server log $($lf.Name) mentions the test IDs:"; $hits | ForEach-Object { Log "    $($_.Line)" } }
    }
}
$viewpoints = if (Test-Path "$Run\sessions_designers.json") { Get-Content "$Run\sessions_designers.json" -Raw | ConvertFrom-Json } else { @() }
function Session([string]$key, [string]$tag) {
    $vp = $viewpoints | Where-Object { $_.key -eq $key } | Select-Object -First 1
    if (-not $vp) { throw "no viewpoint $key" }
    if ($Only.Count -and $Only -notcontains $key) { return }
    # q1 and t1 start from the character as it was (no spell, no quests, the run's money): its rows are put back first
    if ($key -in 'q1', 't1') { Restore-Char "before $key [$tag]" }
    Log "session $key [$tag] $($vp.lua) at $($vp.pos) camera $($vp.camera), stay $($vp.stay) s"
    if ($Plan) { return }
    Assert-Safe "session $key $tag"
    $state.charChanged = $true
    $t0 = Get-Date
    for ($try = 1; $try -le 2; $try++) {
        # 2026-10-14 (hot reload run): the previous session's wowcode.log must not be read for this one (session.ps1 deletes it too, before the launch)
        if (Test-Path "$client\wowcode.log") { try { [IO.File]::Delete("$client\wowcode.log") } catch { } }
        $a = @{ Scenario = "$Run\lua\$($vp.lua)"; Tag = $tag; Pos = $vp.pos; Camera = $vp.camera; OutDir = "$Run\shots"; Stay = [int]$vp.stay }
        # a session that stops with an error is a failed attempt (retried once), not the end of the whole run (2026-10-14)
        try { $out = & "$here\session.ps1" @a 2>&1 | Out-String -Width 250 } catch { $out = "session stopped: $($_.Exception.Message)" }
        Add-LogLine $out
        ($out -split "`n" | Where-Object { $_ -match 'result=|error dialog|scenario (done|note: (before|after|A board|B in|the new|target|spell book|S1|try|after cast|GM FALLBACK|window after))' } | Select-Object -First 40) | ForEach-Object { "    $_".TrimEnd() }
        if ($out -match 'result=SUCCESS') { break }
        Fail "session $key [$tag] attempt $try did not succeed"
        if ($try -eq 1 -and $key -in 'q1', 't1') { Restore-Char "retry of $key [$tag]" }
    }
    Log ("session $key [$tag] done in {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds)
}
function Sessions([string]$tag, [string[]]$keys) { foreach ($k in $keys) { Session $k $tag } }
$all = @('q1', 't1', 'c1', 'k1')

# ================================================================== 1. preflight
Log "run_designers $(if ($Plan) { 'PLAN (read-only)' } else { 'START' }) -- run folder $Run"
foreach ($f in $exe, "$here\session.ps1", "$here\snapshot.ps1", "$here\dbsnap.ps1", "$here\charsnap.ps1", "$here\restore_char.ps1", "$decomp\tools\start_servers.ps1",
               "$Run\sessions_designers.json", "$Run\app_report.json", "$Run\ids.json", "$Run\dryrun\dryrun_report.txt", "$Run\chartest\chartest_report.txt") {
    if (-not (Test-Path $f)) { throw "missing: $f" }
}
foreach ($vp in $viewpoints) { if (-not (Test-Path "$Run\lua\$($vp.lua)")) { throw "missing scenario $Run\lua\$($vp.lua) (IngameRunner designers prepare writes it)" } }
if (-not (Test-Path "$base\Client\wowauto.txt")) { throw 'Client\wowauto.txt (the harness master auto-login file) is missing: the sessions cannot log in. Only the user may restore it.' }
if ((Get-Item "$client\Data" -Force).LinkType) { throw 'Client_visual\Data is a link: not the dedicated test client' }
$rep = Get-Content "$Run\app_report.json" -Raw | ConvertFrom-Json
$bad = @($rep | Where-Object { -not $_.Ok }); if ($bad.Count) { throw "prepared items not ok: $($bad.Id -join ', ')" }
Log "prepared items ok: $(($rep | ForEach-Object Id) -join ', ')"
$ids = Get-Content "$Run\ids.json" -Raw | ConvertFrom-Json
Log "IDs: spell $($ids.S1) (learn $($ids.Learn)), talent $($ids.T1Talent) (rank spell $($ids.T1Spell)), trainer $($ids.Trainer), giver $($ids.Giver), quests $($ids.QA) -> $($ids.QB)"
if (-not (Select-String -LiteralPath "$Run\dryrun\dryrun_report.txt" -Pattern 'RESULT: after Undo last apply the database dump is byte-identical' -Quiet)) { throw 'the dry run did not pass (dryrun\dryrun_report.txt)' }
if (-not (Select-String -LiteralPath "$Run\chartest\chartest_report.txt" -Pattern 'RESULT: charsnap.ps1 -Restore puts the character' -Quiet)) { throw 'the character restore test did not pass (chartest\chartest_report.txt)' }
Log 'dry run (apply + undo byte-identical) and character restore test: passed'
$free = (Get-PSDrive C).Free / 1GB
if ($free -lt 5) { $msg = "only {0:N1} GB free on C: (the run needs about 1.5 GB: pictures, frame series, dumps)" -f $free; if ($Plan) { Log "WOULD REFUSE: $msg" } else { throw $msg } }
Log ("free disk: {0:N0} GB" -f $free)
# the gap the run needs: it cannot know when the next wave starts; it checks before every step and stops (with the whole cleanup) when one does
$last = Get-ChildItem "$decomp\work" -Filter 'chain_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($last) { Log ("last chain log: {0}, written {1:yyyy-MM-dd HH:mm} ({2:N0} min ago); the run needs a gap of about 70 minutes" -f $last.Name, $last.LastWriteTime, ((Get-Date) - $last.LastWriteTime).TotalMinutes) }
Assert-Safe 'start'
try { $ws = Get-WorldServer; Log "world server: pid $($ws.ProcessId) $($ws.ExecutablePath)" } catch { if ($Plan) { Log "WOULD REFUSE (world server): $($_.Exception.Message)" } else { throw } }
if ((Q characters 'select online from characters where guid=1000').Trim() -ne '0') { Log 'test character is online now: the run waits for it (up to 8 minutes) before its first snapshot' }
if (Test-Path "$client\Data\patch-3.MPQ") { $m = 'Client_visual already has a patch-3.MPQ: look at it first (the app would back it up)'; if ($Plan) { Log "WOULD REFUSE: $m" } else { throw $m } }
$loose0 = @(Get-ChildItem "$client\Data" -Recurse -File | Where-Object { $_.Extension -ne '.MPQ' -and $_.FullName -notlike '*\Cinematics\*' })
if ($loose0.Count) { $m = "Client_visual\Data has $($loose0.Count) loose file(s) already: look at them first"; if ($Plan) { Log "WOULD REFUSE: $m" } else { throw $m } }
# the server plan: new rows with custom IDs in this test's tables only, nothing of an earlier test on the server (exit 4 = refused by the rule)
$pr = Invoke-Exe @('designers', 'server', $client, $home_, $Run, 'plan')     # read-only; the whole output first (see Invoke-Exe), then the lines to log
$pr.Out | Select-Object -First 3 | ForEach-Object { Log "  $_" }
$pr.Out | Select-Object -Skip 3 | Where-Object { $_ -match '^(Safety|World server running|Conflicts|REFUSED|ERROR)' } | ForEach-Object { Log "  $_" }
if ($pr.Code -ne 0) {
    $pr.Out | Select-Object -Last 5 | ForEach-Object { Log "  $_" }
    if ($Plan) { Log "WOULD REFUSE: the server plan is refused (exit $($pr.Code))" } else { throw "the server plan is refused (exit $($pr.Code)): see $Run\server\server_log.txt" }
}
if ($Plan) { Log 'PLAN: the steps of the real run follow (nothing is executed)' }

# ================================================================== 2. snapshots (read-only)
Log 'snapshot Client + Client1..6 (before)'
if (-not $Plan) { & "$here\snapshot.ps1" -Out "$Run\safety\clients_before.tsv" | ForEach-Object { Log "  $_" } }
Log 'mangos dump (before, read-only)'
if (-not $Plan) { & "$here\dbsnap.ps1" -Out "$Run\db\mangos_before.sql" | ForEach-Object { Log "  $_" } }
Log 'the test character''s rows (before, read-only; taken while it is offline)'
if (-not $Plan) {
    Wait-Offline
    $o = Invoke-Ps "$here\charsnap.ps1" @{ Out = "$Run\char\char_before.sql"; RequireOffline = $true }
    $o | ForEach-Object { Log "  $_" }
    if (-not (Test-Path "$Run\char\char_before.sql") -or ($o -match 'script failed')) { throw 'no snapshot of the test character' }
}
$wdbBackups = "$home_\$(Split-Path -Leaf (Get-ChildItem $home_ -Directory | Select-Object -First 1))\backups"
$wdbBefore = @(Get-ChildItem $wdbBackups -Directory -Filter 'wdb_*' -ErrorAction SilentlyContinue | ForEach-Object FullName)

try {
    # ============================================================== 3. baseline
    if ($States -contains 'before') {
        Log '==== state before: nothing installed, server untouched'
        Assert-Safe 'baseline' -NoPorts:$Plan
        Deliver 'unloose'; Deliver 'patchstate'
        ClearWdb
        if (-not $Plan) { $state.wdbOriginal = @(Get-ChildItem $wdbBackups -Directory -Filter 'wdb_*' -ErrorAction SilentlyContinue | ForEach-Object FullName | Where-Object { $wdbBefore -notcontains $_ }) | Select-Object -First 1; Log "Client_visual's own cache kept in: $($state.wdbOriginal)" }
        Sessions 'before' @('q1', 'c1', 'k1')
    }
    # ============================================================== 4. apply to the server
    Log '==== Apply to server (the app: backup of every row and table, one transaction, read back, logged)'
    Assert-Safe 'apply' -NoPorts:$Plan
    if (-not $Plan) {
        $cmp = & "$here\dbsnap.ps1" -Out "$Run\db\mangos_before_apply.sql" -Compare "$Run\db\mangos_before.sql"
        Log "  database unchanged since the first dump: $($cmp | Select-Object -Last 1)"
        if (($cmp | Select-Object -Last 1) -ne 'differences: 0') { throw 'the world database changed since the first dump (another tool or a wave wrote to it): not applying' }
    }
    IR @('designers', 'server', $client, $home_, $Run, 'apply')
    if (-not $Plan) { $state.applied = $true; $state.serverHasTestRows = $true; $state.restartNeeded = $true }
    Restart-WorldServer 'spell_template needs a restart; the NPCs, quests and trainer rows are read at start too'
    if (-not $Plan) { $state.restartNeeded = $false }
    # ============================================================== 5. server only
    if ($States -contains 'srvonly') {
        Log '==== state srvonly: server applied, nothing installed in the game (the game lacks the spells: the quest reward and the spell book show the gap)'
        ClearWdb
        Session 'q1' 'srvonly'
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
                IR @('designers', 'server', $client, $home_, $Run, 'undo')
                if (-not $Plan) { $state.undone = $true; $state.restartNeeded = $true }
            } elseif ($Plan) { IR @('designers', 'server', $client, $home_, $Run, 'undo') } } },
        @{ n = 'world server restart (forget the test rows)'; s = {
            if ($state.restartNeeded -or $Plan) {
                if (@(Get-Blockers).Count -and -not $Plan) { Fail 'a chain / test is running now: the world server was NOT restarted; it still holds the test rows in memory until its next restart' }
                else { Restart-WorldServer 'after Undo last apply'; $state.restartNeeded = $false; $state.serverHasTestRows = $false }
            } } },
        @{ n = 'afterundo session (the giver, the trainer and the quests are gone)'; s = {
            if ($States -contains 'afterundo' -and ($state.undone -or $Plan) -and -not $state.restartNeeded) { ClearWdb; Session 'q1' 'afterundo' } } },
        @{ n = "WDB cache of Client_visual: clear the test's cache, put its own back"; s = {
            ClearWdb
            if ($state.wdbOriginal -and (Test-Path $state.wdbOriginal)) {
                Copy-Item "$($state.wdbOriginal)\*" "$client\WDB\" -Recurse -Force
                Log "  restored $((Get-ChildItem $state.wdbOriginal -Recurse -File).Count) cache file(s) from $($state.wdbOriginal)"
            } } },
        @{ n = 'restore the test character (restore_char.ps1, then its rows exactly as in the first snapshot)'; s = {
            if ($Plan) { Log '  (plan) restore_char.ps1; charsnap.ps1 -Restore char_before.sql; charsnap.ps1 -Compare: differences 0 required'; return }
            if (-not (Test-Path "$Run\char\char_before.sql")) { Log '  no snapshot of the character was taken: nothing changed it'; return }
            if (@(Get-Blockers).Count) { Fail "a chain / test is running now: the character was NOT restored; later run: charsnap.ps1 -Out $Run\char\char_after.sql -Restore $Run\char\char_before.sql"; return }
            & "$here\restore_char.ps1" | ForEach-Object { Log "  $_" }
            $o = Invoke-Ps "$here\charsnap.ps1" @{ Out = "$Run\char\char_after.sql"; Restore = "$Run\char\char_before.sql" }
            $o | ForEach-Object { Log "  $_" }
            if (($o | Select-Object -Last 1) -ne 'differences: 0') { Fail 'the test character''s rows differ from the first snapshot' } else { $state.charChanged = $false } } },
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
            if (-not $Plan) {
                # 0.13.3: spells (and their skill line rows) in the spell ID range 35000-39999 too
                $n = (Q mangos 'select (select count(*) from spell_template where entry between 900000 and 999999 or entry between 35000 and 39999) + (select count(*) from creature_template where entry between 900000 and 999999) + (select count(*) from quest_template where entry between 900000 and 999999) + (select count(*) from creature where guid between 900000 and 999999) + (select count(*) from npc_trainer where entry between 900000 and 999999) + (select count(*) from skill_line_ability where spell_id between 35000 and 39999)').Trim()
                if ($n -ne '0') { Fail "test rows still on the server: $n" } else { Log '  no row of the custom ID range left on the server' }
            } } }
    )) {
        try { Log "cleanup: $($step.n)"; & $step.s } catch { Fail "cleanup step '$($step.n)' failed: $($_.Exception.Message)" }
    }
    $sum = [ordered]@{ finished = (Get-Date).ToString('s'); minutes = [Math]::Round(((Get-Date) - $t0run).TotalMinutes, 1); plan = [bool]$Plan; applied = $state.applied
                       undone = $state.undone; worldServerStillHasTestRows = $state.serverHasTestRows; characterRestores = $state.restores; failures = $state.failures }
    $sum | ConvertTo-Json | Set-Content -Encoding UTF8 "$Run\run_designers_summary.json"
    Log ("==== {0}: {1} failure(s), {2:N1} min" -f $(if ($Plan) { 'PLAN done' } else { 'run done' }), $state.failures.Count, ((Get-Date) - $t0run).TotalMinutes)
    if (-not $Plan) { Log "next: look at $Run\shots (and shots\logs), write $Run\judgement.json and REPORT.md" }
}
