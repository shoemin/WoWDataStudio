# session.ps1 -- ONE unattended in-game session in the dedicated test client (default Client_visual) for the visual test of WoW Data Studio.
# The launch is tools\worldtest.ps1's (WoWDebug -noactivate, the trace DLL's autotest login with Client\wowauto.txt copied in and deleted again --
# the password is never read, printed or logged here), but the client folder is a PATH (the decomp harness only knows Client1..6, whose Data
# folders are junctions to the main client's Data). Before the login the offline test character (guid 1000, Statclone) is placed by a DB
# update exactly like tools\scenario.ps1's "home" step (the only character DB write; the caller restores the start point at the end).
# The scenario (tools\lua\scenarios\_lib.lua + vt_lib.lua + the scenario file) logs `shot NAME` before each in-game Screenshot() and
# `burst NAME SECONDS` to ask for an external capture series (wowshot.exe, PrintWindow) while a spell flies.
#   .\session.ps1 -Scenario lua\s1_start.lua -Tag before -Pos '-618.5,-4251.7,38.8,1.25' -OutDir <run>\shots
param([Parameter(Mandatory)][string]$Scenario, [Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Pos, [Parameter(Mandatory)][string]$OutDir,
      [string]$Client = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..").Path 'Client_visual'), [int]$Stay = 150, [string]$Camera = '15,8', [switch]$BurstAll,
      [string]$Hooks = 'none;name EvtSched_DispatchUpdateEvent;name EvtSched_DispatchPollEvent;patch 0064d0e0 c3;skip addr 00643a70;patch 00643a70 680000003fd9042459c3 558bec83ec08568bf1e8')
$ErrorActionPreference = 'Stop'
$decomp = Join-Path (Resolve-Path "$PSScriptRoot\..\..\..").Path 'WoWDecomp'
$parent = Split-Path -Parent $decomp
. "$decomp\tools\lib_wowtest.ps1"
. "$decomp\tools\wintools.ps1"
$here = $PSScriptRoot
$CrashPattern = 'ACCESS VIOLATION|ILLEGAL|STACK_OVERFLOW|INT_DIVIDE|PRIV_INSTR|\[debug string\].*(?:error \d+:|Not enough memory)'
if ($Client -match '\\Client\d?$' -or (Get-Item "$Client\Data" -Force).LinkType) { throw "$Client is not the dedicated test client (its Data must be a real folder)" }
New-Item -ItemType Directory -Force $OutDir, "$OutDir\logs" | Out-Null
# Native programs and stderr (2026-10-14, run 20261013_2308_v010): run_v010.ps1 / run_sessions.ps1 call this script with `2>&1`, and under
# Windows PowerShell 5.1 that turns EVERY stderr line of a native program called in here (even one without its own redirection) into a
# terminating NativeCommandError because of ErrorActionPreference=Stop -- the session then dies mid-way (WoW left running, nothing
# collected). So every native call below goes through Invoke-Native: its own ErrorActionPreference=Continue (function scope), stderr kept as
# text, never fatal; the caller checks the exit code / the result where it matters.
function Invoke-Native([scriptblock]$Cmd) {
    $ErrorActionPreference = 'Continue'
    try { & $Cmd 2>&1 | ForEach-Object { "$_" } } catch { "native call failed: $($_.Exception.Message)" }
}
function Stop-WowTree([int]$ProcId) { $null = Invoke-Native { & taskkill /T /F /PID $ProcId } }   # tools\wintools.ps1's Stop-ProcessTree, never fatal
$mtx = New-Object Threading.Mutex($false, 'Global\WoWDecompClient')   # one client on the test account machine-wide (as scenario.ps1)
try { [void]$mtx.WaitOne() } catch [Threading.AbandonedMutexException] { }
try {
    Wait-CharOffline
    $x, $y, $z, $o = $Pos -split ','
    Q characters "update characters set map=1, zone=14, position_x=$x, position_y=$y, position_z=$z, orientation=$o where guid=1000" | Out-Null
    # camera distance (yd) and pitch (deg) at login: the character's saved camera in THIS client's WTF (CameraZoomIn/Out from the probe do nothing)
    $cd, $cp = $Camera -split ','
    [IO.File]::WriteAllLines("$Client\WTF\Account\MAIN\ShoeLand\Statclone\camera-settings.txt", [string[]]@("cameraDistance $cd", "cameraPitch $cp"))
    $runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $probe = (Get-Content "$decomp\tools\lua\scenarios\_lib.lua" -Raw) -replace '__SCENARIO_RUN_ID__', $runId
    $probe += "`n" + (Get-Content "$here\lua\vt_lib.lua" -Raw) + "`n" + (Get-Content $Scenario -Raw)
    [IO.File]::WriteAllText("$Client\wowprobe.lua", $probe)
    $lines = @(Get-Content "$parent\Client\wowauto.txt" | ForEach-Object { if ($_ -match '^character\s') { 'character 2' } elseif ($_ -match '^stay\s') { "stay $Stay" } elseif ($_ -match '^timeout\s') { "timeout $($Stay + 90)" } else { $_ } })
    [IO.File]::WriteAllLines("$Client\wowauto.txt", [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
    foreach ($f in 'wowtest.txt', 'wowcode.log', 'last.out') { if (Test-Path "$Client\$f") { [IO.File]::Delete("$Client\$f") } }
    if (Test-Path "$Client\Screenshots") { Get-ChildItem "$Client\Screenshots" -File | ForEach-Object { [IO.File]::Delete($_.FullName) } }
    $burstDir = "$OutDir\burst_$Tag"
    $p = Start-Process -FilePath "$Client\WoWDebug.exe" -ArgumentList "-hooks `"$Hooks`" -seconds $($Stay + 60) -quiet -noactivate" -WorkingDirectory $Client -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Client\last.out"
    $null = $p.Handle
    $deadline = (Get-Date).AddSeconds($Stay + 100)
    $dialog = $null; $bursts = @{}; $result = 'timeout'
    while ((Get-Date) -lt $deadline) {
        if ($p.HasExited) { $result = 'exited'; break }
        $log = ''
        if (Test-Path "$Client\wowcode.log") { $fs = [IO.File]::Open("$Client\wowcode.log", 'Open', 'Read', 'ReadWrite'); $log = (New-Object IO.StreamReader($fs)).ReadToEnd(); $fs.Close() }
        if ($log -match 'autotest: (SUCCESS|FAILED|TIMEOUT)') { $result = $Matches[1]; break }
        if ((Test-Path "$Client\last.out") -and (Select-String -Path "$Client\last.out" -Pattern $CrashPattern -Quiet)) { $result = 'crash'; break }
        foreach ($m in [regex]::Matches($log, 'autotest: \[lua\] burst (\S+) (\d+)')) {
            $bn = $m.Groups[1].Value
            if ($bursts.ContainsKey($bn)) { continue }
            $bursts[$bn] = 0
            $wow = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" | Where-Object Name -eq 'WoW.exe' | Select-Object -First 1
            if ($wow) {
                New-Item -ItemType Directory -Force $burstDir | Out-Null
                $end = (Get-Date).AddSeconds([int]$m.Groups[2].Value); $k = 0; $miss = 0; $sw = [Diagnostics.Stopwatch]::StartNew(); $times = @()
                # 2026-10-14: a frame wowshot cannot take ("no visible WoW window for that pid": the window hidden, or the game closing
                # because the scenario ended before the series did -- c1's 10 s series outlasts its last 8.5 s of steps) is skipped, not
                # fatal -- its stderr line used to become a terminating error under ErrorActionPreference=Stop (run 20261013_2308_v010).
                # The series stops when the game is gone; only frames that exist get a time.
                $wowProc = Get-Process -Id $wow.ProcessId -ErrorAction SilentlyContinue
                while ((Get-Date) -lt $end) {
                    if (-not $wowProc -or $wowProc.HasExited -or $p.HasExited) { break }
                    $k++; $ms = $sw.ElapsedMilliseconds
                    $file = "$burstDir\{0}_{1:D3}.bmp" -f $bn, $k
                    $null = Invoke-Native { & "$decomp\wowshot.exe" $wow.ProcessId $file }
                    if (Test-Path -LiteralPath $file) { $times += "{0}`t{1}" -f $k, $ms } else { $miss++ }
                }
                [IO.File]::WriteAllLines("$burstDir\${bn}_times.tsv", [string[]]$times)   # frame number -> ms since the series started
                $bursts[$bn] = $k - $miss
            }
        }
        $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessId)
        $dialog = Find-ErrorDialog -Pids (@($p.Id) + $kids)
        if ($dialog) { $result = 'dialog'; break }
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Seconds 2
    if ($result -eq 'exited' -and (Test-Path "$Client\wowcode.log") -and (Select-String -Path "$Client\wowcode.log" -Pattern 'autotest: (SUCCESS|FAILED|TIMEOUT)' -Quiet)) { $result = (Select-String -Path "$Client\wowcode.log" -Pattern 'autotest: (SUCCESS|FAILED|TIMEOUT)' | Select-Object -First 1).Matches[0].Groups[1].Value }
    if (-not $p.HasExited) { Stop-WowTree $p.Id }
}
finally {
    if ($p -and -not $p.HasExited) { Stop-WowTree $p.Id }   # a session that stopped early never leaves the game running
    foreach ($f in 'wowauto.txt', 'wowprobe.lua') { if (Test-Path "$Client\$f") { [IO.File]::Delete("$Client\$f") } }   # never leave the credentials behind
    $mtx.ReleaseMutex()
}
# collect: the k-th `shot NAME` line <-> the k-th screenshot file (one per second at most; the scenario waits >= 1.2 s between shots)
$logLines = if (Test-Path "$Client\wowcode.log") { Get-Content "$Client\wowcode.log" } else { @() }
Copy-Item "$Client\wowcode.log" "$OutDir\logs\${Tag}_$([IO.Path]::GetFileNameWithoutExtension($Scenario))_wowcode.log" -ErrorAction SilentlyContinue
$names = @($logLines | ForEach-Object { if ($_ -match 'autotest: \[lua\] shot (\S+)') { $Matches[1] } })
$files = @(if (Test-Path "$Client\Screenshots") { Get-ChildItem "$Client\Screenshots" -Filter *.tga | Sort-Object LastWriteTime })
$pairs = @()
for ($i = 0; $i -lt $files.Count; $i++) {
    $n = if ($i -lt $names.Count) { $names[$i] } else { "extra$i" }
    $pairs += "$($files[$i].FullName)|$OutDir\${n}_$Tag.png"
}
if ($pairs.Count) { Invoke-Native { $pairs | python "$here\tga2png.py" } }
if (Test-Path $burstDir) {
    $bmps = @(Get-ChildItem $burstDir -Filter *.bmp | ForEach-Object { "$($_.FullName)|$($_.FullName -replace '\.bmp$', '.jpg')" })
    if ($bmps.Count) {
        $conv = @(Invoke-Native { $bmps | python "$here\tga2png.py" -delete $(if ($BurstAll) { '-burstall' } else { '-burst' }) })
        "burst frames kept: $(@($conv | Where-Object { $_ -like 'converted *' }).Count)"
        $conv | Where-Object { $_ -notlike 'converted *' } | Select-Object -First 5 | ForEach-Object { "burst conversion: $_" }
    }
}
$steps = $logLines | Where-Object { $_ -match 'scenario (step|done|note)' } | ForEach-Object { ($_ -replace '^\[\s*\d+\]\s*', '').Trim() }
"session $Tag $([IO.Path]::GetFileName($Scenario)): result=$result, shots logged=$($names.Count), files=$($files.Count), bursts=$(($bursts.GetEnumerator() | ForEach-Object { "$($_.Key):$($_.Value)" }) -join ' ')"
if ($dialog) { "error dialog: [$($dialog.Title)] $($dialog.Text)" }
$steps | ForEach-Object { '    ' + $_ }
