# run_hotreload.ps1 -- the in-game test of HOT RELOAD (WoW Data Studio 0.11.0 + wowcode.dll HR1) in the dedicated test client Client_visual:
# a texture change and a model change are installed and reloaded in the RUNNING game; pictures before and after, without a restart.
# Run it only after the wave that merged HR1 (Client_visual's wowcode.dll must carry the marker WOWCODE_HOTRELOAD_PROTOCOL_1), when no wave
# runs (the safety checks refuse otherwise). Test plan: TEST_PLAN.md next to this script.
#
#   .\run_hotreload.ps1 -Run <run folder> -Plan          read-only: every safety check, then the steps it WOULD take
#   .\run_hotreload.ps1 -Run <run folder>                the real run (about 15 minutes: 2 cases x 2 hook sets)
#   .\run_hotreload.ps1 -Run <run folder> -UseDecompDll  first copy WoWDecomp\wowcode.dll into Client_visual (backed up, put back at the end)
#
# Steps: 1 safety checks  2 snapshots (Client, Client1..6 and Client_visual\Data)  3 the edits, made the app's way in the run's own studio home
# (IngameRunner prepare: 01 BoarSkinIvory green, hr1 Boar.m2 x1.5, 04 DurotarBush01 x4)  4 hot reload switched on for Client_visual
# (wds settings set hot-reload on)  5 per case (boars: texture + creature model; bush: placed doodad) and hook set (minimal, all): a session
# (session.ps1) whose scenario takes the BEFORE pictures and logs 'hrphase install'; this script then installs the case's files
# (wds changes install) and reloads them (wds changes reload --wait --json) while the game keeps running; the scenario takes the AFTER
# pictures 30 s later and stays 20 s more; the loose files are removed after the session.  FINALLY: loose files removed, the switch off,
# the DLL put back (-UseDecompDll), the test character restored, the snapshots compared (0 differences required), REPORT.md.
# The game account's login is session.ps1's (it reads Client\wowauto.txt and deletes its copy; this script never opens that file).
param([Parameter(Mandatory)][string]$Run, [switch]$Plan, [switch]$UseDecompDll,
      [string[]]$Cases = @('boars', 'bush'), [string[]]$HookSets = @('minimal', 'all'))
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$ingame = Split-Path -Parent $here
$base = (Resolve-Path "$PSScriptRoot\..\..\..\..").Path   # the folder that holds WoWDataTool, WoWDecomp, Server and the clients
$decomp = "$base\WoWDecomp"
$client = "$base\Client_visual"
$home_ = "$Run\studio_home"
$runner = "$ingame\IngameRunner\bin\Release\net8.0\IngameRunner.exe"
$wds = @("$base\WoWDataTool\app\WoWData.Cli\bin\Release\net8.0-windows\wds.exe", "$base\WoWDataTool\app\WoWData.Cli\bin\Debug\net8.0-windows\wds.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force "$Run\shots", "$Run\json", "$Run\logs", "$Run\safety" | Out-Null
$logFile = "$Run\run_hotreload.log"
function Log([string]$s) { $l = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $s; $l; for ($i = 0; $i -lt 10; $i++) { try { [IO.File]::AppendAllText($logFile, $l + "`r`n"); return } catch { Start-Sleep -Milliseconds 200 } } }
$failures = New-Object System.Collections.Generic.List[string]
function Fail([string]$s) { $failures.Add($s); Log "!! $s" }

$caseDefs = @{
    boars = @{ Lua = 'hr_boars.lua'; Pos = '-682.7,-4271,39.3,4.712'; Camera = '7,12'
               Files = @('Creature\Boar\BoarSkinIvory.blp', 'Creature\Boar\Boar.m2'); Expect = @{ 'Creature\Boar\BoarSkinIvory.blp' = 'reloaded'; 'Creature\Boar\Boar.m2' = 'reloaded' } }
    bush  = @{ Lua = 'hr_bush.lua'; Pos = '-618.5,-4251.7,38.8,1.25'; Camera = '15,6'
               Files = @('World\Kalimdor\Durotar\PassiveDoodads\Bushes\DurotarBush01.m2'); Expect = @{ 'World\Kalimdor\Durotar\PassiveDoodads\Bushes\DurotarBush01.m2' = 'reloaded' } }
}
$hooks = @{
    minimal = 'none;name EvtSched_DispatchUpdateEvent;name EvtSched_DispatchPollEvent;patch 0064d0e0 c3;skip addr 00643a70;patch 00643a70 680000003fd9042459c3 558bec83ec08568bf1e8'
    all     = 'all;patch 0064d0e0 c3'
}

# ------------------------------------------------------------------ 1. safety (the same blockers as run_v010.ps1)
function Get-Blockers {
    $b = @()
    foreach ($f in Get-ChildItem "$decomp\work" -Filter 'chain_*.log' -ErrorAction SilentlyContinue) {
        if ($f.LastWriteTime -gt (Get-Date).AddMinutes(-10)) {
            $tail = (Get-Content -LiteralPath $f.FullName -Tail 5) -join "`n"
            if ($tail -notmatch '=== chain\S* done') { $b += "chain log $($f.Name) active" }
        }
    }
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'") {
        if ($p.CommandLine -match 'chain_\w+\.ps1') { $b += "chain script running: pid $($p.ProcessId)" }
        if ($p.CommandLine -match 'srvconsole\.ps1') { $b += "console-mode world server (a test's) running: pid $($p.ProcessId)" }
    }
    foreach ($p in Get-CimInstance Win32_Process -Filter "Name='WoW.exe' OR Name='WoWDebug.exe'") { $b += "a game is running: pid $($p.ProcessId) $($p.ExecutablePath)" }
    $miss = @(); foreach ($port in 3306, 3724, 8085) { if (-not (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)) { $miss += $port } }
    if ($miss.Count) { $b += "server ports not listening: $($miss -join ', ')" }
    if (-not (Test-Path "$client\WoW.exe") -or (Get-Item "$client\Data" -Force).LinkType) { $b += "$client is not the dedicated test client (its Data must be a real folder)" }
    if (-not $wds) { $b += 'wds.exe is not built (dotnet build app\WoWDataStudio.sln)' }
    if (-not (Test-Path $runner)) { $b += 'IngameRunner is not built (dotnet build tests\ingame\IngameRunner -c Release)' }
    return $b
}
function Has-Marker([string]$dll) { if (-not (Test-Path $dll)) { return $false }; return [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll)).Contains('WOWCODE_HOTRELOAD_PROTOCOL_1') }

$blockers = @(Get-Blockers)
$dllSrc = if ($UseDecompDll) { "$decomp\wowcode.dll" } else { "$client\wowcode.dll" }
if (-not (Has-Marker $dllSrc)) { $blockers += "$dllSrc has no hot reload (no WOWCODE_HOTRELOAD_PROTOCOL_1): merge and build HR1 first" }
if ($blockers.Count) { $blockers | ForEach-Object { Log "SAFETY: $_" }; if (-not $Plan) { throw 'safety checks failed' } }
else { Log 'safety ok' }
if ($Plan) {
    Log "WOULD: snapshot Client, Client1..6, Client_visual\Data; IngameRunner prepare $client $home_ $Run 01 hr1 04"
    Log "WOULD: wds settings set hot-reload on --yes --client $client --home $home_$(if ($UseDecompDll) { '; copy WoWDecomp\wowcode.dll into Client_visual (backed up)' })"
    foreach ($c in $Cases) { foreach ($h in $HookSets) { Log "WOULD: session $c / $h ($($caseDefs[$c].Lua)), on 'hrphase install': wds changes install + wds changes reload --wait --json $($caseDefs[$c].Files -join ' ')" } }
    Log 'WOULD (finally): remove loose files, hot-reload off, DLL back, character restored, snapshots compared, REPORT.md'
    return
}

function Wds([string[]]$a, [string]$jsonOut) {
    # 2026-10-14: stdout only (wds writes progress to stderr), in a scope with ErrorActionPreference=Continue -- under 'Stop' a native stderr
    # line becomes a terminating error in Windows PowerShell 5.1 (it stopped run 20261013_2308_v010); stderr goes to its own file
    $errFile = if ($jsonOut) { "$jsonOut.stderr.txt" } else { "$Run\logs\wds_last.stderr.txt" }
    $out = & { $ErrorActionPreference = 'Continue'; & $wds @a --client $client --home $home_ --json 2> $errFile } | Out-String
    if ($jsonOut) { [IO.File]::WriteAllText($jsonOut, $out) }
    try { return ($out | ConvertFrom-Json) } catch { return $null }
}

$dllBackup = $null
$state = [ordered]@{ prepared = $false; enabled = $false }
$results = @()
try {
    # ------------------------------------------------------------------ 2. snapshots
    & "$ingame\snapshot.ps1" -Out "$Run\safety\clients_before.tsv" | Out-Null
    $dataBefore = Get-ChildItem "$client\Data" -Recurse -File | ForEach-Object { "$($_.FullName.Substring($client.Length))`t$($_.Length)`t$($_.LastWriteTimeUtc.ToString('o'))" }
    [IO.File]::WriteAllLines("$Run\safety\visual_data_before.tsv", [string[]]$dataBefore)
    Log "snapshots taken ($($dataBefore.Count) files in Client_visual\Data)"
    if ($UseDecompDll) {
        $dllBackup = "$Run\safety\wowcode.dll.client_visual"
        Copy-Item "$client\wowcode.dll" $dllBackup
        Copy-Item "$decomp\wowcode.dll" "$client\wowcode.dll" -Force
        Log 'copied WoWDecomp\wowcode.dll into Client_visual (the old one is put back at the end)'
    }

    # ------------------------------------------------------------------ 3. the edits (the app's own Core calls, in the run's home)
    $prep = & { $ErrorActionPreference = 'Continue'; & $runner prepare $client $home_ $Run 01 hr1 04 2>&1 | ForEach-Object { "$_" } } | Out-String
    [IO.File]::WriteAllText("$Run\logs\prepare.txt", $prep)
    $list = Wds @('changes', 'list') "$Run\json\changes_list.json"
    $paths = @($list.data.changes | ForEach-Object { $_.path })
    foreach ($c in $Cases) { foreach ($f in $caseDefs[$c].Files) { if ($paths -notcontains $f) { throw "the edit of $f was not prepared (see logs\prepare.txt)" } } }
    $state.prepared = $true
    Log "edits prepared: $($paths -join ', ')"

    # ------------------------------------------------------------------ 4. the switch
    $sw = Wds @('settings', 'set', 'hot-reload', 'on', '--yes') "$Run\json\switch_on.json"
    if (-not (Test-Path "$client\wowhotreload.txt")) { throw 'wds did not write wowhotreload.txt' }
    $state.enabled = $true
    Log 'hot reload switched on for Client_visual'

    # ------------------------------------------------------------------ 5. the sessions
    foreach ($c in $Cases) {
        foreach ($h in $HookSets) {
            $tag = "${c}_$h"
            $case = $caseDefs[$c]
            Log "--- session $tag"
            $sessOut = "$Run\logs\session_$tag.txt"
            $args_ = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$ingame\session.ps1", '-Scenario', "$ingame\lua\$($case.Lua)", '-Tag', $tag,
                       '-Pos', $case.Pos, '-Camera', $case.Camera, '-OutDir', "$Run\shots", '-Stay', '110', '-Hooks', $hooks[$h])
            # 2026-10-14: Start-Process joins -ArgumentList with spaces and does not quote, so "...\WoW Custom Client\..." was split ("-File
            # '<...>\WoW' failed") -- quote every argument that contains a space or a ';'
            $args_ = @($args_ | ForEach-Object { if ("$_" -match '[\s;]') { '"' + "$_" + '"' } else { "$_" } })
            # 2026-10-14 (run 20261014_2147): the PREVIOUS session's wowcode.log (session.ps1 copies it, and deletes it only a second
            # after its start) still had 'hrphase install', so sessions 2-4 sent status/reload before their game had started --
            # delete it here, before the session starts (its game is gone by now)
            if (Test-Path "$client\wowcode.log") { [IO.File]::Delete("$client\wowcode.log") }
            $sess = Start-Process powershell.exe -ArgumentList $args_ -PassThru -WindowStyle Hidden -RedirectStandardOutput $sessOut -RedirectStandardError "$sessOut.err"
            # 2026-10-14 (run 20261014_2252): 150 s was too short -- from the 2nd session on, session.ps1 first waits for the server to log the
            # character out of the previous session (Wait-CharOffline, ~2 min), and 'hrphase install' came at ~3 min; the session itself ends
            # within Stay+100 s after its launch, so wait for that (the loop also ends when the session does)
            $deadline = (Get-Date).AddSeconds(480)
            $reloaded = $false
            while (-not $sess.HasExited -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                $log = ''
                if (Test-Path "$client\wowcode.log") { $fs = [IO.File]::Open("$client\wowcode.log", 'Open', 'Read', 'ReadWrite'); $log = (New-Object IO.StreamReader($fs)).ReadToEnd(); $fs.Close() }
                if (-not $reloaded -and $log -match 'autotest: \[lua\] hrphase install') {
                    $reloaded = $true
                    $st = Wds @('changes', 'reload', '--status') "$Run\json\${tag}_status.json"
                    Log "  status: $($st.data.state) ($($st.data.message))"
                    $ins = Wds (@('changes', 'install', '--yes') + $case.Files) "$Run\json\${tag}_install.json"
                    Log "  installed: $(@($ins.data.files).Count) file(s), exit $($ins.exitCode)"
                    $sw2 = [Diagnostics.Stopwatch]::StartNew()
                    $rl = Wds (@('changes', 'reload', '--wait') + $case.Files) "$Run\json\${tag}_reload.json"
                    Log "  reload: exit $($rl.exitCode) in $([int]$sw2.Elapsed.TotalMilliseconds) ms; $($rl.data.message)"
                    $results += [pscustomobject]@{ Tag = $tag; Status = $st; Install = $ins; Reload = $rl }
                }
            }
            if (-not $sess.HasExited) { $sess.WaitForExit(120000) | Out-Null }
            $sessText = (Get-Content $sessOut -ErrorAction SilentlyContinue) -join "`n"
            Log ("  " + (($sessText -split "`n") | Where-Object { $_ -match '^session ' } | Select-Object -First 1))
            # checks
            if (-not $reloaded) { Fail "${tag}: the scenario never asked for the reload ('hrphase install' missing)" }
            if ($sessText -notmatch 'result=SUCCESS') { Fail "${tag}: the session did not end with SUCCESS (crash, timeout or error dialog?): see logs\session_$tag.txt" }
            $wlog = Get-ChildItem "$Run\shots\logs" -Filter "${tag}_*_wowcode.log" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($wlog) {
                $wl = Get-Content $wlog.FullName
                if (-not ($wl -match 'hotreload: on')) { Fail "${tag}: wowcode.log has no 'hotreload: on' line" }
                if (-not ($wl -match 'hotreload: reload #\d+')) { Fail "${tag}: wowcode.log has no reload line" }
                if ($wl -match 'EXCEPTION') { Fail "${tag}: wowcode.log reports an exception" }
                if (-not ($wl -match 'hrphase done')) { Fail "${tag}: the scenario did not reach 'hrphase done' after the reload" }
            } else { Fail "${tag}: no wowcode.log collected" }
            $r = $results | Where-Object Tag -eq $tag | Select-Object -Last 1
            if ($r) {
                if (-not $r.Reload.data.done) { Fail "${tag}: the reload was not done: $($r.Reload.data.message)" }
                foreach ($kv in $case.Expect.GetEnumerator()) {
                    $f = @($r.Reload.data.report.files) | Where-Object { $_.path -eq $kv.Key } | Select-Object -First 1
                    if (-not $f) { Fail "${tag}: $($kv.Key) is not in the report" }
                    elseif ($f.result -ne $kv.Value) { Fail "${tag}: $($kv.Key): $($f.result) ($($f.detail)), expected $($kv.Value)" }
                }
            }
            # the pictures: before vs after must differ where the change is (the whole picture is compared; the gallery shows both)
            foreach ($pair in @(Get-ChildItem "$Run\shots" -Filter "*_before_$tag.png" -ErrorAction SilentlyContinue)) {
                $after = $pair.FullName -replace '_before_', '_after_'
                if (-not (Test-Path $after)) { Fail "${tag}: no AFTER picture for $($pair.Name)"; continue }
                $d = & { $ErrorActionPreference = 'Continue'; & python -I "$here\picdiff.py" $pair.FullName $after 2>&1 | ForEach-Object { "$_" } } | Out-String
                Log "  picture $($pair.Name): $($d.Trim())"
                if ($d -notmatch 'changed=(\d+(\.\d+)?)%' -or [double]$Matches[1] -lt 0.5) { Fail "${tag}: $($pair.Name) and its AFTER picture hardly differ ($($d.Trim()))" }
            }
            # the loose files out again before the next session (the game is gone by now)
            $null = Wds (@('changes', 'remove', '--yes') + $case.Files) "$Run\json\${tag}_remove.json"
        }
    }
}
catch { Fail "stopped: $($_.Exception.Message)" }
finally {
    if ($state.prepared) { $null = Wds @('changes', 'remove', '--yes') "$Run\json\final_remove.json" }
    if ($state.enabled -or (Test-Path "$client\wowhotreload.txt")) { $null = Wds @('settings', 'unset', 'hot-reload', '--yes') "$Run\json\switch_off.json" }
    if (Test-Path "$client\wowhotreload.txt") { Fail 'wowhotreload.txt is still in Client_visual' }
    if ($dllBackup) { Copy-Item $dllBackup "$client\wowcode.dll" -Force; Log 'Client_visual\wowcode.dll put back' }
    try { & "$ingame\restore_char.ps1" | Out-Null; Log 'test character restored' } catch { Fail "restore_char: $($_.Exception.Message)" }
    & "$ingame\snapshot.ps1" -Out "$Run\safety\clients_after.tsv" -Compare "$Run\safety\clients_before.tsv" | Tee-Object -FilePath "$Run\safety\clients_compare.txt" | Out-Null
    if ((Get-Content "$Run\safety\clients_compare.txt" -Raw) -notmatch 'differences: 0\b') { Fail 'Client / Client1..6 changed (see safety\clients_compare.txt)' }
    $dataAfter = Get-ChildItem "$client\Data" -Recurse -File | ForEach-Object { "$($_.FullName.Substring($client.Length))`t$($_.Length)`t$($_.LastWriteTimeUtc.ToString('o'))" }
    $diff = Compare-Object @($dataBefore) @($dataAfter)
    if ($diff) { Fail "Client_visual\Data differs from before: $(@($diff).Count) line(s)"; $diff | Out-File "$Run\safety\visual_data_diff.txt" }
    $verdict = if ($failures.Count) { "FAIL ($($failures.Count))" } else { 'PASS' }
    $md = @("# Hot reload in-game test ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))", '', "Verdict: **$verdict**", '')
    foreach ($r in $results) {
        $md += "## $($r.Tag)", '', "* status: $($r.Status.data.state): $($r.Status.data.message)", "* reload: $($r.Reload.data.message -replace "`n", ' ')", ''
        foreach ($f in @($r.Reload.data.report.files)) { $md += "  * ``$($f.path)``: $($f.result) - $($f.detail)" }
        $md += ''
        foreach ($b in @(Get-ChildItem "$Run\shots" -Filter "*_before_$($r.Tag).png" -ErrorAction SilentlyContinue)) {
            $md += "| before | after |", '|---|---|', "| ![](shots/$($b.Name)) | ![](shots/$($b.Name -replace '_before_', '_after_')) |", ''
        }
    }
    if ($failures.Count) { $md += '## Failures', ''; $md += $failures | ForEach-Object { "* $_" } }
    [IO.File]::WriteAllLines("$Run\REPORT.md", [string[]]$md)
    Log "VERDICT: $verdict (report: $Run\REPORT.md)"
}
