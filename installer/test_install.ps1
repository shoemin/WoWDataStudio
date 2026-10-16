<#
.SYNOPSIS
  Checks the installer with an install / uninstall that can never touch the user's real WoW Data Studio.

.DESCRIPTION
  Uses ONLY the TEST build of the installer (build_installer.ps1 -TestBuild -> dist\WoWDataStudio-TestSetup-<version>.exe),
  which has its own AppId, name ("WoW Data Studio TEST"), Start-menu group and Apps entry. The script:
    1. refuses a setup that is not a TEST build (file name and the AppId inside it), and refuses a target folder inside the
       real installation;
    2. records the user's real installation: the Start-menu folder "WoW Data Studio" (names, sizes, hashes), the real
       Apps entry (HKCU ...\Uninstall\{8C1B6E9A-...}_is1) and the real install folder's file list;
    3. installs silently into a temporary folder (/DIR) with no shortcuts (/NOICONS) and only the task "Add the command
       line tool to PATH", checks the installed exe's version, that the app (WoWDataStudio.exe) and its command line
       (wds.exe) are both there with the same version, that "wds version" runs and reports it, that the TEST bin folder
       is in the user's PATH, the TEST Apps entry and that no "WoW Data Studio" shortcut was created or changed;
    4. uninstalls the TEST installation with ITS OWN uninstaller (never the real one), and checks the TEST Apps entry
       and folder are gone and the user's PATH is exactly as before;
    5. compares the real installation's record before and after: any difference fails the run.
  Never run the normal (real) setup silently for tests, and never call the real uninstaller.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\test_install.ps1 -Setup dist\WoWDataStudio-TestSetup-0.6.1.exe -Version 0.6.1
#>
param(
    [Parameter(Mandatory = $true)][string]$Setup,
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"
$RealAppId = "{8C1B6E9A-4F1D-4C55-9B7E-2D3A6E5F9C41}"
$TestAppId = "{5D7C2F3E-9A41-4B8E-A6C0-3F1E2B7D9A55}"
$RealKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${RealAppId}_is1"
$TestKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${TestAppId}_is1"
$StartMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$RealGroup = Join-Path $StartMenu "WoW Data Studio"
$RealDir = Join-Path $env:LOCALAPPDATA "Programs\WoW Data Studio"

function Fail([string]$m) { Write-Host "FAILED: $m" -ForegroundColor Red; exit 1 }
function Ok([string]$m) { Write-Host "ok: $m" }

# 1. only the TEST build
$Setup = (Resolve-Path $Setup).Path
if ((Split-Path $Setup -Leaf) -notlike "WoWDataStudio-TestSetup-*.exe") { Fail "$Setup is not a TEST build (build it with build_installer.ps1 -TestBuild)" }
$bytes = [IO.File]::ReadAllBytes($Setup)
$text = [Text.Encoding]::ASCII.GetString($bytes)
if ($text.Contains($RealAppId.Trim('{', '}'))) { Fail "the setup carries the real AppId: refusing to run it" }
Ok "the setup is the TEST build"

function Record {
    $r = [ordered]@{}
    if (Test-Path $RealGroup) {
        Get-ChildItem $RealGroup -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
            $r["menu:" + $_.FullName.Substring($RealGroup.Length)] = "$($_.Length) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash) $($_.LastWriteTimeUtc.Ticks)"
        }
    } else { $r["menu"] = "absent" }
    if (Test-Path $RealKey) {
        $p = Get-ItemProperty $RealKey
        foreach ($n in ($p.PSObject.Properties | Where-Object { $_.Name -notlike "PS*" } | Sort-Object Name)) { $r["reg:" + $n.Name] = "$($n.Value)" }
    } else { $r["reg"] = "absent" }
    if (Test-Path $RealDir) {
        Get-ChildItem $RealDir -Recurse -File -Force | Sort-Object FullName | ForEach-Object { $r["dir:" + $_.FullName.Substring($RealDir.Length)] = "$($_.Length) $($_.LastWriteTimeUtc.Ticks)" }
    } else { $r["dir"] = "absent" }
    $r["desktop"] = "$(Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WoW Data Studio.lnk'))"
    return $r
}

function Same($a, $b) {
    $ka = @($a.Keys); $kb = @($b.Keys)
    if ($ka.Count -ne $kb.Count) { return $false }
    foreach ($k in $ka) { if (-not $b.Contains($k) -or $a[$k] -ne $b[$k]) { return $false } }
    return $true
}

# 2. the real installation, before (and the user's PATH, raw, as the installer reads and writes it)
$before = Record
function UserPath {
    $k = Get-Item "HKCU:\Environment"
    $v = $k.GetValue("Path", $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -eq $v) { return "<none>" }
    return "$($k.GetValueKind('Path'))|$v"
}
$pathBefore = UserPath
Ok ("recorded the real installation: Start-menu group {0}, Apps entry {1}, folder {2}" -f $(if (Test-Path $RealGroup) { "present" } else { "absent" }), $(if (Test-Path $RealKey) { "present" } else { "absent" }), $(if (Test-Path $RealDir) { "present" } else { "absent" }))

# 3. install the TEST build into a temporary folder, no shortcuts
$target = Join-Path ([IO.Path]::GetTempPath()) ("wds_test_install_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
if ($target.StartsWith($RealDir, [StringComparison]::OrdinalIgnoreCase)) { Fail "the target is inside the real installation" }
if (Test-Path $TestKey) { Fail "a TEST installation is already registered ($TestKey): remove it with its own uninstaller first" }
# a TEST Start-menu group left by an earlier, normal (not /NOICONS) TEST install is recorded, not counted against this run
$TestGroup = Join-Path $StartMenu "WoW Data Studio TEST"
function GroupState { if (Test-Path $TestGroup) { (Get-ChildItem $TestGroup -File | Sort-Object Name | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join ";" } else { "<none>" } }
$testGroupBefore = GroupState
if ($testGroupBefore -ne "<none>") { Write-Host "note: a TEST Start-menu group from an earlier TEST install exists ($TestGroup); it must stay as it is" }
$log = "$target.install.log"
$p = Start-Process -FilePath $Setup -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOICONS", "/TASKS=""addtopath""", "/DIR=""$target""", "/LOG=""$log""") -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); Fail "the test install did not finish" }
if ($p.ExitCode -ne 0) { Fail "the test install exited with $($p.ExitCode) (log: $log)" }
$exe = Join-Path $target "bin\WoWDataStudio.exe"
if (-not (Test-Path $exe)) { Fail "WoWDataStudio.exe was not installed into $target" }
$ver = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $ver.StartsWith($Version)) { Fail "installed version $ver, expected $Version" }
Ok "installed the TEST build into $target (version $ver)"
foreach ($d in @("manual\index.html", "blender\wowdata_blender.zip", "bin\blender_addon\wowdata_blender\terrain.py", "docs\NOGGIT.md", "docs\HOT_RELOAD.md", "LICENSE")) {
    if (-not (Test-Path (Join-Path $target $d))) { Fail "missing $d" }
}
Ok "the manual, the Blender add-on (with terrain.py) and the notes are installed"

# 0.8.0: one product, two front ends: the command line is installed with the app, in the same version, and runs
$cli = Join-Path $target "bin\wds.exe"
if (-not (Test-Path $cli)) { Fail "wds.exe (the command line) was not installed" }
$cliVer = (Get-Item $cli).VersionInfo.ProductVersion
if ($cliVer -ne $ver) { Fail "wds.exe is version $cliVer, WoWDataStudio.exe ${ver}: they must be the same" }
foreach ($d in @("bin\wds_prompt.cmd", "scripts\build_and_install_patch.ps1", "scripts\dbc_csv_round_trip.cmd", "scripts\export_zone_creature_textures.ps1")) {
    if (-not (Test-Path (Join-Path $target $d))) { Fail "missing $d" }
}
$testHome = Join-Path ([IO.Path]::GetTempPath()) ("wds_test_home_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$wv = (& $cli --home $testHome version --json) -join "`n" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $wv.ok) { Fail "wds version did not run (exit code $LASTEXITCODE)" }
if (-not $ver.StartsWith($wv.data.version) -or $wv.data.appVersion -ne $ver) { Fail "wds version says $($wv.data.display) (app $($wv.data.appVersion)), the files say $ver" }
& $cli --home $testHome --help | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "wds --help did not run" }
$p2 = Start-Process -FilePath $cli -ArgumentList @("--home", """$testHome""", "no-such-command") -NoNewWindow -PassThru -RedirectStandardError "$testHome.err.txt" -RedirectStandardOutput "$testHome.out.txt"
$null = $p2.Handle   # (PowerShell 5.1: keeps the exit code readable)
$p2.WaitForExit()
if ($p2.ExitCode -ne 2) { Fail "wds with an unknown command exited with $($p2.ExitCode), expected 2" }
Remove-Item "$testHome.err.txt", "$testHome.out.txt" -Force -ErrorAction SilentlyContinue
if (Test-Path $testHome) { Remove-Item $testHome -Recurse -Force }
Ok "wds.exe is installed with the app, the same version ($cliVer), and runs (version, help, exit codes)"
$binDir = Join-Path $target "bin"
$pathNow = UserPath
$pathValue = $pathNow.Substring($pathNow.IndexOf("|") + 1)
if (-not (";" + $pathValue + ";").ToUpperInvariant().Contains((";" + $binDir + ";").ToUpperInvariant())) {
    Fail "the task ""Add the command line tool to PATH"" did not put $binDir into the user's PATH ($pathValue)"
}
Ok "the TEST bin folder is in the user's PATH"
if (-not (Test-Path $TestKey)) { Fail "the TEST Apps entry was not registered" }
$tk = Get-ItemProperty $TestKey
if ($tk.DisplayName -ne "WoW Data Studio TEST") { Fail "the TEST Apps entry is called '$($tk.DisplayName)'" }
Ok "the TEST Apps entry: $($tk.DisplayName) $($tk.DisplayVersion)"
if ((GroupState) -ne $testGroupBefore) { Fail "the TEST Start-menu group was created or changed although /NOICONS was given" }
$mid = Record
if (-not (Same $before $mid)) { Fail "the real installation changed during the test install" }
Ok "the real Start-menu group, Apps entry and folder are untouched after the install"

# 4. uninstall the TEST installation with its own uninstaller
$unins = Join-Path $target "unins000.exe"
if (-not (Test-Path $unins)) { Fail "the TEST uninstaller is missing" }
$u = Start-Process -FilePath $unins -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -PassThru
if (-not $u.WaitForExit(300000)) { $u.Kill(); Fail "the test uninstall did not finish" }
for ($i = 0; $i -lt 60 -and ((Test-Path $exe) -or (Test-Path $TestKey)); $i++) { Start-Sleep -Milliseconds 500 }   # the uninstaller finishes from a copy of itself
if (Test-Path $TestKey) { Fail "the TEST Apps entry is still there after the uninstall" }
if (Test-Path $exe) { Fail "the TEST files are still there after the uninstall" }
if ((GroupState) -ne $testGroupBefore) { Fail "the uninstall changed the TEST Start-menu group of the earlier TEST install" }
Ok "uninstalled the TEST build with its own uninstaller"
$pathAfter = UserPath
# the uninstaller finishes (and restores the PATH) from a copy of itself, after the files and the Apps entry are gone
for ($i = 0; $i -lt 120 -and $pathAfter -ne $pathBefore; $i++) { Start-Sleep -Milliseconds 500; $pathAfter = UserPath }
if ($pathAfter -ne $pathBefore) { Fail "the user's PATH is not as before the test install:`n  before: $pathBefore`n  after:  $pathAfter" }
Ok "the user's PATH is exactly as before (the uninstaller removed the TEST bin folder)"
if (Test-Path $target) { Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue }

# 5. the real installation, after
$after = Record
if (-not (Same $before $after)) {
    foreach ($k in $before.Keys) { if ($after[$k] -ne $before[$k]) { Write-Host "  changed: $k" } }
    foreach ($k in $after.Keys) { if (-not $before.Contains($k)) { Write-Host "  new: $k" } }
    Fail "the real installation changed"
}
Ok "the real installation is exactly as before (Start-menu group, Apps entry, folder, desktop shortcut)"
Write-Host "TEST INSTALL OK" -ForegroundColor Green
