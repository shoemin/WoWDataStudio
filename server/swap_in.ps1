# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
    Installs the CHANGED files of a reextract.ps1 staging folder into the live Server\data, with a backup.
    Run it yourself AFTER stopping the world server (mangosd); restart mangosd afterwards.

.DESCRIPTION
    1. Refuses to run while mangosd.exe is running (a -DryRun only warns).
    2. Recomputes the change list (tools\compare_data.py: size + SHA-1, staging vs Server\data).
    3. Copies the backup of every file it will replace or remove into
       <BackupRoot>\swap_<yyyyMMdd_HHmmss>\<kind>\  with manifest.json (hash-verified).
    4. Copies changed + new files from staging (via a temp name, hash-verified), and removes map-specific files
       that the fresh extraction no longer produces (maps / vmaps of the selected maps; -NoRemove keeps them).
       vmaps are only installed from a COMPLETE assembly (every map's .vmtree), because a .vmtile refers to the
       BIH node order of its map's .vmtree: copying all differing files of a map keeps each map consistent.
       dbc files are only copied when named with -DbcFiles (the live 5875\dbc has server-side edits:
       Spell.dbc, SkillLineAbility.dbc differ from the client's).
    5. Prints the restart reminder and the restore command.

    -Restore <backup folder> puts a previous swap back (replaced/removed files restored, added files deleted
    if they are still the ones the swap installed).

.EXAMPLE
    .\swap_in.ps1 -DryRun                     # what would change (works while mangosd runs)
    .\swap_in.ps1 -Kinds maps,mmaps -Maps 0   # install map 0 terrain + navmesh changes
    .\swap_in.ps1 -Kinds dbc -DbcFiles Map.dbc,AreaTable.dbc
    .\swap_in.ps1 -Restore backups\swap_20261010_150000
#>
[CmdletBinding()]
param(
    [string]$Staging,
    [string]$ServerData,
    [string]$Baseline,
    [string[]]$Kinds,
    [string[]]$Maps,
    [string[]]$DbcFiles,
    [string]$BackupRoot,
    [string]$Restore,
    [switch]$NoRemove,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$top = Split-Path -Parent (Split-Path -Parent $here)
function Get-FullPath([string]$p) { return [IO.Path]::GetFullPath($p).TrimEnd('\') }
function Split-List([string[]]$v) { if (-not $v) { return @() }; return @(($v -join ',') -split '[,\s]+' | Where-Object { $_ -ne '' }) }
if (-not $Staging) { $Staging = Join-Path $here 'staging' }
if (-not $ServerData) { $ServerData = Join-Path $top 'Server\data' }
if (-not $Baseline) { $Baseline = Join-Path $here 'baseline' }
if (-not $BackupRoot) { $BackupRoot = Join-Path $here 'backups' }
$Staging = Get-FullPath $Staging; $ServerData = Get-FullPath $ServerData; $BackupRoot = Get-FullPath $BackupRoot
$KIND_DIR = @{ maps = 'maps'; vmaps = 'vmaps'; mmaps = 'mmaps'; dbc = '5875\dbc' }

function Test-MangosRunning {
    $p = @(Get-Process -Name mangosd -ErrorAction SilentlyContinue)
    if ($p.Count) { return ($p | ForEach-Object { "mangosd.exe pid $($_.Id)" }) -join ', ' }
    return ''
}
function Get-Sha1([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA1).Hash }

$running = Test-MangosRunning
if ($running) {
    if ($DryRun) { Write-Host "WARNING: the world server is running ($running). A real swap would refuse; dry run continues." -ForegroundColor Yellow }
    else { Write-Host "REFUSING: the world server is running ($running). Stop it first, then run this again." -ForegroundColor Red; exit 2 }
}

# ------------------------------------------------------------------ restore
if ($Restore) {
    $Restore = Get-FullPath $Restore
    $man = Get-Content -LiteralPath (Join-Path $Restore 'manifest.json') -Raw | ConvertFrom-Json
    Write-Host "restoring $($man.entries.Count) entries from $Restore into $($man.serverData)"
    foreach ($e in $man.entries) {
        $target = Join-Path $man.serverData $e.rel
        if ($e.action -eq 'add') {
            if ((Test-Path -LiteralPath $target) -and (Get-Sha1 $target) -eq $e.newSha1) {
                Write-Host "  delete   $($e.rel)"
                if (-not $DryRun) { Remove-Item -LiteralPath $target -Force }
            } else { Write-Host "  skip     $($e.rel) (missing or changed since the swap)" }
        } else {
            Write-Host "  restore  $($e.rel)"
            if (-not $DryRun) {
                Copy-Item -LiteralPath (Join-Path $Restore $e.rel) -Destination $target -Force
                if ((Get-Sha1 $target) -ne $e.oldSha1) { throw "restore verification failed: $target" }
            }
        }
    }
    if ($DryRun) { Write-Host 'DRY RUN: nothing written.' } else { Write-Host 'Restore done. Restart the world server (Server\Start mangosd 16bit.bat).' }
    exit 0
}

# ------------------------------------------------------------------ plan
$KindList = @(Split-List $Kinds | ForEach-Object { $_.ToLower() })
if (-not $KindList.Count) { $KindList = @('maps', 'vmaps', 'mmaps' | Where-Object { Test-Path -LiteralPath (Join-Path $Staging $_) }) }
$DbcList = @(Split-List $DbcFiles)
if ($DbcList.Count -and $KindList -notcontains 'dbc') { $KindList += 'dbc' }
foreach ($k in $KindList) { if (-not $KIND_DIR.ContainsKey($k)) { throw "unknown kind '$k' (maps, vmaps, mmaps, dbc)" } }
if (-not $KindList.Count) { Write-Host "nothing in $Staging to install (run reextract.ps1 first)"; exit 0 }
$MapList = @(Split-List $Maps)

$plan = Join-Path $Staging ('swap_plan_{0}.json' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
$a = @((Join-Path $here 'tools\compare_data.py'), '--staging', $Staging, '--server', $ServerData, '--kinds', ($KindList -join ','), '--json', $plan)
if (Test-Path -LiteralPath $Baseline) { $a += @('--baseline', $Baseline) }
if ($MapList.Count) { $a += @('--maps', ($MapList -join ',')) }
& python @a | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'compare_data.py failed' }
$rep = Get-Content -LiteralPath $plan -Raw | ConvertFrom-Json

$actions = New-Object System.Collections.ArrayList
foreach ($k in $KindList) {
    $r = $rep.kinds.$k
    if (-not $r) { Write-Host "[$k] no staging folder, skipped"; continue }
    if ($k -eq 'vmaps') {
        $have = @(Get-ChildItem -LiteralPath (Join-Path $Staging 'vmaps') -Filter *.vmtree).Count
        $live = @(Get-ChildItem -LiteralPath (Join-Path $ServerData 'vmaps') -Filter *.vmtree).Count
        if ($have -lt $live) { Write-Host "[vmaps] SKIPPED: staging has $have .vmtree files, live has $live (not a complete assembly)" -ForegroundColor Yellow; continue }
    }
    $names = @(); $names += @($r.changed | ForEach-Object { @{ n = $_; act = 'replace' } }); $names += @($r.new | ForEach-Object { @{ n = $_; act = 'add' } })
    if ($k -eq 'dbc') {
        $names = @($names | Where-Object { $n = $_.n; @($DbcList | Where-Object { $_ -ieq $n }).Count -gt 0 })
        $skipped = @($r.changed).Count + @($r.new).Count - $names.Count
        if ($skipped -gt 0) { Write-Host "[dbc] $skipped changed/new DBC file(s) not named in -DbcFiles are left alone" }
    }
    if (-not $NoRemove -and ($k -eq 'maps' -or $k -eq 'vmaps')) {
        $names += @($r.only_in_server | Where-Object { $_ -match '^\d{3}' } | ForEach-Object { @{ n = $_; act = 'remove' } })
    }
    foreach ($x in $names) {
        if (-not $x.n) { continue }
        [void]$actions.Add([pscustomobject]@{ kind = $k; name = $x.n; action = $x.act; rel = (Join-Path $KIND_DIR[$k] $x.n)
                src = (Join-Path (Join-Path $Staging $k) $x.n) })
    }
    $c = @($actions | Where-Object { $_.kind -eq $k })
    Write-Host ("[{0}] replace {1}, add {2}, remove {3}   (identical, untouched: {4})" -f $k, @($c | Where-Object { $_.action -eq 'replace' }).Count,
        @($c | Where-Object { $_.action -eq 'add' }).Count, @($c | Where-Object { $_.action -eq 'remove' }).Count, $r.identical)
    if ($r.PSObject.Properties.Name -contains 'edit') { Write-Host ("       of the changed/new files: {0} from client edits, {1} extractor drift (same as the stock-client baseline)" -f @($r.edit).Count, @($r.drift).Count) }
    $c | Select-Object -First 8 | ForEach-Object { Write-Host "         $($_.action) $($_.rel)" }
    if ($c.Count -gt 8) { Write-Host "         ... ($($c.Count) in total, full list in $plan)" }
}
if (-not $actions.Count) { Write-Host 'nothing to install: staging matches Server\data.'; exit 0 }
if ($DryRun) { Write-Host "`nDRY RUN: nothing written. Plan: $plan"; exit 0 }

# ------------------------------------------------------------------ swap
$running = Test-MangosRunning
if ($running) { Write-Host "REFUSING: the world server is running ($running)." -ForegroundColor Red; exit 2 }
$bdir = Join-Path $BackupRoot ('swap_{0}' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Force $bdir | Out-Null
$manifest = [ordered]@{ time = (Get-Date).ToString('s'); staging = $Staging; serverData = $ServerData; entries = @() }
$manPath = Join-Path $bdir 'manifest.json'
function Save-Manifest { $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manPath -Encoding UTF8 }

# 1. back up everything that will be replaced or removed (verified) before touching anything
foreach ($x in $actions) {
    $target = Join-Path $ServerData $x.rel
    $e = [ordered]@{ rel = $x.rel; action = $x.action; oldSha1 = $null; newSha1 = $null }
    if ($x.action -ne 'add') {
        $b = Join-Path $bdir $x.rel
        New-Item -ItemType Directory -Force (Split-Path -Parent $b) | Out-Null
        Copy-Item -LiteralPath $target -Destination $b -Force
        $e.oldSha1 = Get-Sha1 $target
        if ((Get-Sha1 $b) -ne $e.oldSha1) { throw "backup verification failed: $b" }
    }
    if ($x.action -ne 'remove') { $e.newSha1 = Get-Sha1 $x.src }
    $manifest.entries += $e
}
Save-Manifest
Write-Host "backup: $($actions.Count) entries in $bdir"
# 2. install
$n = 0
foreach ($e in $manifest.entries) {
    $target = Join-Path $ServerData $e.rel
    $x = $actions | Where-Object { $_.rel -eq $e.rel } | Select-Object -First 1
    if ($e.action -eq 'remove') {
        Remove-Item -LiteralPath $target -Force
    } else {
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        $tmp = "$target.swaptmp"
        Copy-Item -LiteralPath $x.src -Destination $tmp -Force
        Move-Item -LiteralPath $tmp -Destination $target -Force
        if ((Get-Sha1 $target) -ne $e.newSha1) { throw "install verification failed: $target (restore with -Restore $bdir)" }
    }
    $n++
}
Write-Host "installed: $n file operations into $ServerData"
Write-Host "`nNow restart the world server:  Server\Start mangosd 16bit.bat   (it loads maps/vmaps/mmaps at grid load)." -ForegroundColor Green
Write-Host "To undo:  .\swap_in.ps1 -Restore `"$bdir`""
