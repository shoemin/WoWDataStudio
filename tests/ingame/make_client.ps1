# make_client.ps1 -- create the dedicated test client for the in-game visual test (never touches Client or Client1..6):
#   <WoW Custom Client folder>\Client_visual (the folder that holds WoWDataTool)
#   Data\*.MPQ and Data\Interface\Cinematics\*.avi = HARD LINKS to Client\Data (no extra disk space; the game and the app open them read-only;
#   the app's loose install / patch build only ever create NEW files in this Data folder, which never touches the linked originals)
#   game exe + DLLs, realmlist, WTF (Config.wtf set to a 1600x900 window), Interface\AddOns = copies
#   wowcode.dll / map, WoWDebug.exe, wowsyms.txt, wowpatches.txt = the decomp harness's current test build (as tools\worldtest.ps1 copies them)
param([string]$Dest = (Join-Path (Resolve-Path "$PSScriptRoot\..\..\..").Path 'Client_visual'), [string]$Resolution = '1600x900')
$ErrorActionPreference = 'Stop'
$base = (Resolve-Path "$PSScriptRoot\..\..\..").Path   # the folder that holds WoWDataTool, WoWDecomp, Server and the clients
$src = "$base\Client"
$decomp = "$base\WoWDecomp"
if (Test-Path $Dest) { throw "$Dest exists already" }
if ((Get-Item $src).PSDrive.Name -ne (Split-Path -Qualifier $Dest).TrimEnd(':')) { throw 'hard links need the same volume' }
New-Item -ItemType Directory "$Dest\Data\Interface\Cinematics" -Force | Out-Null
foreach ($f in Get-ChildItem "$src\Data" -Filter *.MPQ) { New-Item -ItemType HardLink -Path "$Dest\Data\$($f.Name)" -Target $f.FullName | Out-Null }
foreach ($f in Get-ChildItem "$src\Data\Interface\Cinematics" -File) { New-Item -ItemType HardLink -Path "$Dest\Data\Interface\Cinematics\$($f.Name)" -Target $f.FullName | Out-Null }
foreach ($f in 'WoW.exe', 'dbghelp.dll', 'DivxDecoder.dll', 'fmod.dll', 'ijl15.dll', 'unicows.dll', 'Scan.dll', 'realmlist.wtf') { Copy-Item "$src\$f" "$Dest\$f" }
foreach ($f in 'wowcode.dll', 'wowcode.map', 'WoWDebug.exe', 'WoWLaunch.exe') { Copy-Item "$decomp\$f" "$Dest\$f" }
Copy-Item "$decomp\hooks\wowsyms.txt" "$Dest\wowsyms.txt"
Copy-Item "$decomp\hooks\wowpatches.txt" "$Dest\wowpatches.txt"
Copy-Item "$src\Interface" "$Dest\Interface" -Recurse
New-Item -ItemType Directory "$Dest\WTF" | Out-Null
Copy-Item "$src\WTF\Account" "$Dest\WTF\Account" -Recurse
$cfg = Get-Content "$src\WTF\Config.wtf" | Where-Object { $_ -notmatch '^SET (gxResolution|gxMaximize|gxWindow|lastCharacterIndex|accountName) ' }
$cfg += @("SET gxResolution `"$Resolution`"", 'SET gxWindow "1"', 'SET gxMaximize "0"', 'SET accountName "main"', 'SET lastCharacterIndex "1"')
[IO.File]::WriteAllLines("$Dest\WTF\Config.wtf", [string[]]$cfg)
# the loose-file mode must be on in this client (wave185 patch line)
if (-not (Select-String -Path "$Dest\wowpatches.txt" -Pattern '^0040221f\s+6a01\s+6a00' -Quiet)) { throw 'wowpatches.txt has no loose-file line' }
$links = Get-ChildItem "$Dest\Data" -Recurse -File | ForEach-Object { $n = (fsutil hardlink list $_.FullName | Measure-Object).Count; "{0} links={1}" -f $_.Name, $n }
$links
"copied: {0:N1} MB (hard links not counted)" -f ((Get-ChildItem $Dest -Recurse -File | Where-Object { $_.DirectoryName -notlike "$Dest\Data*" } | Measure-Object Length -Sum).Sum / 1MB)
