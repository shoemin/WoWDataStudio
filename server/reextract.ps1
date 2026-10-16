# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
    Re-extracts the server's map data (maps / vmaps / mmaps / dbc) from the client into a STAGING folder and
    writes a change report against the live Server\data. Never writes into Server\data or the client.

.DESCRIPTION
    Runs our own builds of the vmangos extractors (bin\, see README.md) as separate programs:
      dbc    mapextractor -e 2                       -> <Staging>\dbc
      maps   mapextractor -e 1 -f 0                  -> <Staging>\maps        (-f 0 = float heights, like the live data)
      vmaps  vmapextractor -l, vmap_assembler        -> <Staging>\Buildings, <Staging>\vmaps   (-l = like the live data)
      mmaps  MoveMapGen <map> --tile x,y --silent    -> <Staging>\mmaps       (only the requested / touched tiles)
    mapextractor and vmapextractor always extract ALL maps (they have no map filter; ~1 min each). -Maps filters
    the report, the mmaps tile list and the swap. Then writes <Staging>\changes.txt and changes.json.
    The swap into Server\data is a separate step for the user (swap_in.ps1, or this script with -Swap).

.PARAMETER Client      client folder (contains Data\*.MPQ). Default: ..\..\Client next to this script.
.PARAMETER Staging     output folder. Default: <script folder>\staging.
.PARAMETER Maps        map ids for the report / mmaps / swap, e.g. 0,1. Default: all.
.PARAMETER Tiles       explicit navmesh tiles, e.g. 1:40,33 or 0:32,48 or 36:all (whole map). Default: the tiles
                       whose maps/vmaps input changed (vs the stock-client baseline if present) plus 8 neighbours.
.PARAMETER Steps       any of dbc, maps, vmaps, mmaps. Default: maps.
.PARAMETER ServerData  live server data folder, only READ. Default: ..\..\Server\data.
.PARAMETER Baseline    extraction of the stock client (labels edit vs drift, picks touched tiles). Default: <script folder>\baseline.
.PARAMETER Jobs        parallel MoveMapGen processes (default 4; a continent tile takes ~1-3 min and ~1 GB).
.PARAMETER Swap        do not extract; run swap_in.ps1 with -Staging/-ServerData/-Maps/-DryRun instead.

.EXAMPLE
    .\reextract.ps1 -Steps maps,vmaps -Maps 0          # extract, report changes for map 0
    .\reextract.ps1 -Steps mmaps -Maps 0               # navmesh for the touched map-0 tiles (+ neighbours)
    .\reextract.ps1 -Steps mmaps -Tiles 1:40,33        # one tile
    .\reextract.ps1 -Swap -DryRun                      # show what the swap would copy
#>
[CmdletBinding()]
param(
    [string]$Client,
    [string]$Staging,
    [string[]]$Maps,
    [string[]]$Tiles,
    [string[]]$Steps = @('maps'),
    [string]$ServerData,
    [string]$Baseline,
    [string]$ServerSource,
    [int]$Jobs = 4,
    [switch]$MapHeightAsInt,
    [switch]$VmapSmall,
    [switch]$Swap,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$top = Split-Path -Parent (Split-Path -Parent $here)            # ...\WoW Custom Client
function Get-FullPath([string]$p) { return [IO.Path]::GetFullPath($p).TrimEnd('\') }
if (-not $Client) { $Client = Join-Path $here '..\..\Client' }
if (-not $Staging) { $Staging = Join-Path $here 'staging' }
if (-not $ServerData) { $ServerData = Join-Path $top 'Server\data' }
if (-not $Baseline) { $Baseline = Join-Path $here 'baseline' }
if (-not $ServerSource) { $ServerSource = Join-Path $top 'WoWDecomp\work\server_src' }
$Client = Get-FullPath $Client; $Staging = Get-FullPath $Staging; $ServerData = Get-FullPath $ServerData
$Baseline = Get-FullPath $Baseline; $ServerSource = Get-FullPath $ServerSource
$bin = Join-Path $here 'bin'
$py = 'python'
$tools = Join-Path $here 'tools'

# powershell -File passes "0,1" as ONE string: split every list parameter on commas / spaces ourselves
function Split-List([string[]]$v) { if (-not $v) { return @() }; return @(($v -join ',') -split '[,\s]+' | Where-Object { $_ -ne '' }) }
$StepList = Split-List $Steps | ForEach-Object { $_.ToLower() }
foreach ($s in $StepList) { if ('dbc', 'maps', 'vmaps', 'mmaps' -notcontains $s) { throw "unknown step '$s' (use dbc, maps, vmaps, mmaps)" } }
$MapList = @(Split-List $Maps | ForEach-Object { [int]$_ })
$TileSpecs = @()     # @{Map=..; X=..; Y=..} ; X = -1 means the whole map
if ($Tiles) {
    $joined = $Tiles -join ' '
    foreach ($m in [regex]::Matches($joined, '(\d+):(?:(\d+),(\d+)|(all))')) {
        if ($m.Groups[4].Success) { $TileSpecs += @{ Map = [int]$m.Groups[1].Value; X = -1; Y = -1 } }
        else { $TileSpecs += @{ Map = [int]$m.Groups[1].Value; X = [int]$m.Groups[2].Value; Y = [int]$m.Groups[3].Value } }
    }
    if ($TileSpecs.Count -eq 0) { throw "could not parse -Tiles '$joined' (expected map:x,y or map:all)" }
}

if ($Swap) {
    $a = @{ Staging = $Staging; ServerData = $ServerData; Baseline = $Baseline }
    if ($MapList.Count) { $a.Maps = $MapList -join ',' }
    if ($DryRun) { $a.DryRun = $true }
    & (Join-Path $here 'swap_in.ps1') @a
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------- safety checks
function Test-Inside([string]$child, [string]$parent) {
    $c = $child.ToLower() + '\'; $p = $parent.ToLower() + '\'
    return $c.StartsWith($p)
}
foreach ($pair in @(@($Staging, $ServerData), @($ServerData, $Staging), @($Staging, $Client), @($Client, $Staging))) {
    if (Test-Inside $pair[0] $pair[1]) { throw "refusing: '$($pair[0])' is inside '$($pair[1])' (staging must be separate from Server\data and the client)" }
}
foreach ($m in 'terrain.MPQ', 'model.MPQ', 'patch.MPQ', 'wmo.MPQ') {
    if (-not (Test-Path -LiteralPath (Join-Path $Client "Data\$m"))) { throw "$Client\Data\$m not found (-Client must be the client folder)" }
}
if ($Client.Length -gt 110) { throw "client path too long for mapextractor (input buffer 128 chars): $Client" }
if (-not (Test-Path -LiteralPath $ServerData)) { throw "server data folder not found: $ServerData" }
foreach ($e in 'mapextractor.exe', 'vmapextractor.exe', 'vmap_assembler.exe', 'MoveMapGen.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $bin $e))) { throw "$bin\$e missing: build it first (README.md, tools\build_extractors.ps1)" }
}
New-Item -ItemType Directory -Force $Staging | Out-Null
$logs = Join-Path $Staging 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$emptyIn = Join-Path $Staging '.empty_stdin'
[IO.File]::WriteAllBytes($emptyIn, [byte[]]@())
$patch3 = Join-Path $Client 'Data\patch-3.MPQ'
$timings = [ordered]@{}

function Get-FreeGB([string]$path) {
    $d = Get-PSDrive -Name ($path.Substring(0, 1))
    return [math]::Round($d.Free / 1GB, 1)
}

# Starts a tool with stdin = empty file, stdout/stderr to <logs>\<name>.out/.err; returns the Process object.
function Start-Tool([string]$exe, [string[]]$argv, [string]$cwd, [string]$name) {
    $q = @($argv | ForEach-Object { if ($_ -match '[\s"]') { '"' + $_ + '"' } else { $_ } })
    $p = Start-Process -FilePath (Join-Path $bin $exe) -ArgumentList ($q -join ' ') -WorkingDirectory $cwd -NoNewWindow -PassThru `
        -RedirectStandardInput $emptyIn -RedirectStandardOutput (Join-Path $logs "$name.out.txt") -RedirectStandardError (Join-Path $logs "$name.err.txt")
    $null = $p.Handle      # keeps ExitCode readable after exit (Windows PowerShell quirk)
    return $p
}
function Invoke-Tool([string]$exe, [string[]]$argv, [string]$cwd, [string]$name) {
    Write-Host ("  {0} {1}   (in {2})" -f $exe, ($argv -join ' '), $cwd)
    $p = Start-Tool $exe $argv $cwd $name
    $p.WaitForExit()
    return $p.ExitCode
}
# Deletes a folder we own inside staging (refuses anything outside it).
function Clear-StagingDir([string]$dir) {
    if (-not (Test-Inside $dir $Staging)) { throw "internal: refusing to clear $dir (not inside staging)" }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

Write-Host "client     : $Client$(if (Test-Path -LiteralPath $patch3) { '   (patch-3.MPQ present)' } else { '   (no patch-3.MPQ)' })"
Write-Host "staging    : $Staging   (free on drive: $(Get-FreeGB $Staging) GB)"
Write-Host "server data: $ServerData   (read only)"
Write-Host "steps      : $($StepList -join ', ')$(if ($MapList.Count) { '   maps: ' + ($MapList -join ',') })"

# ---------------------------------------------------------------- dbc
if ($StepList -contains 'dbc') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n[dbc] mapextractor -e 2"
    Clear-StagingDir (Join-Path $Staging 'dbc')
    $rc = Invoke-Tool 'mapextractor.exe' @('-i', $Client, '-e', '2') $Staging 'mapextractor_dbc'
    if ($rc -ne 0) { throw "mapextractor (dbc) failed with exit code $rc, see $logs" }
    $timings.dbc = [int]$sw.Elapsed.TotalSeconds
    Write-Host ("  {0} files, {1} s" -f @(Get-ChildItem (Join-Path $Staging 'dbc')).Count, $timings.dbc)
}

# ---------------------------------------------------------------- maps
if ($StepList -contains 'maps') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $f = '0'; if ($MapHeightAsInt) { $f = '1' }
    Write-Host "`n[maps] mapextractor -e 1 -f $f"
    Clear-StagingDir (Join-Path $Staging 'maps')
    Clear-StagingDir (Join-Path $Staging 'mmaps')       # navmesh tiles built from older inputs are stale now
    $rc = Invoke-Tool 'mapextractor.exe' @('-i', $Client, '-e', '1', '-f', $f) $Staging 'mapextractor_maps'
    if ($rc -ne 0) { throw "mapextractor (maps) failed with exit code $rc, see $logs" }
    $timings.maps = [int]$sw.Elapsed.TotalSeconds
    Write-Host ("  {0} .map files, {1} s" -f @(Get-ChildItem (Join-Path $Staging 'maps') -Filter *.map).Count, $timings.maps)
}

# ---------------------------------------------------------------- vmaps
if ($StepList -contains 'vmaps') {
    if ((Get-FreeGB $Staging) -lt 3) { throw 'less than 3 GB free: vmaps need ~1 GB (Buildings 263 MB + vmaps 575 MB)' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $l = '-l'; if ($VmapSmall) { $l = '-s' }
    Write-Host "`n[vmaps] vmapextractor $l + vmap_assembler"
    Clear-StagingDir (Join-Path $Staging 'Buildings')       # vmapextractor refuses a 'polluted' Buildings folder
    Clear-StagingDir (Join-Path $Staging 'vmaps')
    Clear-StagingDir (Join-Path $Staging 'mmaps')
    $rc = Invoke-Tool 'vmapextractor.exe' @($l, '-d', (Join-Path $Client 'Data')) $Staging 'vmapextractor'
    if ($rc -ne 0) { throw "vmapextractor failed with exit code $rc, see $logs" }
    $opened = Select-String -LiteralPath (Join-Path $logs 'vmapextractor.out.txt') -Pattern '^Opening .*\.MPQ' | ForEach-Object { Split-Path -Leaf $_.Line }
    Write-Host "  archives opened (last = highest priority): $($opened -join ', ')"
    New-Item -ItemType Directory -Force (Join-Path $Staging 'vmaps') | Out-Null
    $rc = Invoke-Tool 'vmap_assembler.exe' @('Buildings', 'vmaps') $Staging 'vmap_assembler'
    if ($rc -ne 0) { throw "vmap_assembler failed with exit code $rc, see $logs" }
    $timings.vmaps = [int]$sw.Elapsed.TotalSeconds
    Write-Host ("  {0} vmaps files, {1} s" -f @(Get-ChildItem (Join-Path $Staging 'vmaps')).Count, $timings.vmaps)
}

function Invoke-Report([string]$kinds, [string]$json, [string]$txt) {
    $a = @((Join-Path $tools 'compare_data.py'), '--staging', $Staging, '--server', $ServerData, '--kinds', $kinds, '--json', $json, '--txt', $txt)
    if (Test-Path -LiteralPath $Baseline) { $a += @('--baseline', $Baseline) }
    if ($MapList.Count) { $a += @('--maps', ($MapList -join ',')) }
    & $py @a | Write-Host
    if ($LASTEXITCODE -ne 0) { throw 'compare_data.py failed' }
}

# ---------------------------------------------------------------- mmaps
if ($StepList -contains 'mmaps') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n[mmaps] MoveMapGen"
    if ($TileSpecs.Count -eq 0) {
        # tiles whose navmesh input changed (+ 8 neighbours), from a maps/vmaps report
        $tj = Join-Path $Staging 'touched_tiles.json'
        Invoke-Report 'maps,vmaps' $tj (Join-Path $Staging 'touched_tiles.txt')
        $rep = Get-Content -LiteralPath $tj -Raw | ConvertFrom-Json
        foreach ($t in @($rep.mmap_rebuild_tiles)) {
            if ($t -match '^(\d+):(\d+),(\d+)$') { $TileSpecs += @{ Map = [int]$Matches[1]; X = [int]$Matches[2]; Y = [int]$Matches[3] } }
        }
        Write-Host "  touched tiles: $(@($rep.touched_tiles).Count) -> rebuild set with neighbours: $($TileSpecs.Count)"
    }
    if ($MapList.Count) { $TileSpecs = @($TileSpecs | Where-Object { $MapList -contains $_.Map }) }
    $work = Join-Path $Staging 'mmapwork'
    $mapIds = @($TileSpecs | ForEach-Object { $_.Map } | Sort-Object -Unique)
    if ($TileSpecs.Count -eq 0) {
        Write-Host '  nothing to build (no tiles requested or touched)'
    } else {
        if ((Get-FreeGB $Staging) -lt 3) { throw 'less than 3 GB free: the mmaps work folder needs ~1 GB' }
        # inputs: fresh staging extraction if there is one, else a COPY of the live data (read only; MoveMapGen
        # only reads maps\ and vmaps\ and writes mmaps\ in its working folder, verified in generator.cpp/MapBuilder.cpp)
        $mapsSrc = Join-Path $Staging 'maps'
        if (-not (Test-Path -LiteralPath $mapsSrc) -or @(Get-ChildItem -LiteralPath $mapsSrc -Filter *.map).Count -eq 0) { $mapsSrc = Join-Path $ServerData 'maps' }
        $vmapsSrc = Join-Path $Staging 'vmaps'
        if (-not (Test-Path -LiteralPath $vmapsSrc) -or @(Get-ChildItem -LiteralPath $vmapsSrc -Filter *.vmtree).Count -eq 0) { $vmapsSrc = Join-Path $ServerData 'vmaps' }
        Write-Host "  inputs: maps from $mapsSrc, vmaps from $vmapsSrc"
        $wmaps = Join-Path $work 'maps'; $wvmaps = Join-Path $work 'vmaps'
        Clear-StagingDir $wmaps
        New-Item -ItemType Directory -Force $wmaps, $wvmaps | Out-Null
        foreach ($m in $mapIds) {   # ALL tiles of the map: MoveMapGen sizes the navmesh (.mmap maxTiles) from them
            Get-ChildItem -LiteralPath $mapsSrc -Filter ('{0:D3}*.map' -f $m) | Copy-Item -Destination $wmaps
        }
        robocopy $vmapsSrc $wvmaps /MIR /NJH /NJS /NFL /NDL /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "copying vmaps into $wvmaps failed (robocopy $LASTEXITCODE)" }
        $cfgDir = Join-Path $ServerSource 'contrib\mmap'
        # expand map:all into the map's tile list (tiles that have a .map or .vmtile)
        $tasks = @()
        foreach ($t in $TileSpecs) {
            if ($t.X -ge 0) { $tasks += $t; continue }
            $set = @{}
            Get-ChildItem -LiteralPath $wmaps -Filter ('{0:D3}*.map' -f $t.Map) | ForEach-Object { $set[('{0}_{1}' -f [int]$_.Name.Substring(5, 2), [int]$_.Name.Substring(3, 2))] = 1 }
            Get-ChildItem -LiteralPath $wvmaps -Filter ('{0:D3}_*.vmtile' -f $t.Map) | ForEach-Object { $set[('{0}_{1}' -f [int]$_.Name.Substring(4, 2), [int]$_.Name.Substring(7, 2))] = 1 }
            foreach ($k in $set.Keys) { $xy = $k -split '_'; $tasks += @{ Map = $t.Map; X = [int]$xy[0]; Y = [int]$xy[1] } }
        }
        # skip tiles without any input and without a live navmesh tile (MoveMapGen would only rewrite the .mmap)
        $tasks = @($tasks | Where-Object {
                $mm = '{0:D3}{1:D2}{2:D2}' -f $_.Map, $_.Y, $_.X
                (Test-Path -LiteralPath (Join-Path $wmaps "$mm.map")) -or
                (Test-Path -LiteralPath (Join-Path $wvmaps ('{0:D3}_{1:D2}_{2:D2}.vmtile' -f $_.Map, $_.X, $_.Y))) -or
                (Test-Path -LiteralPath (Join-Path $ServerData "mmaps\$mm.mmtile"))
            } | Sort-Object { $_.Map * 10000 + $_.X * 100 + $_.Y } -Unique)
        Write-Host "  $($tasks.Count) tile(s) to build with up to $Jobs parallel MoveMapGen processes"
        # one job folder per slot: junctions to the shared (staging-owned) inputs, its own mmaps\ output
        $slots = @()
        for ($i = 0; $i -lt [math]::Max(1, $Jobs); $i++) {
            $jd = Join-Path $work "job$i"
            if (Test-Path -LiteralPath $jd) {
                foreach ($j in 'maps', 'vmaps') { $jp = Join-Path $jd $j; if (Test-Path -LiteralPath $jp) { [IO.Directory]::Delete($jp, $false) } }   # junction only
                Clear-StagingDir $jd
            }
            New-Item -ItemType Directory -Force (Join-Path $jd 'mmaps') | Out-Null
            New-Item -ItemType Junction -Path (Join-Path $jd 'maps') -Target $wmaps | Out-Null
            New-Item -ItemType Junction -Path (Join-Path $jd 'vmaps') -Target $wvmaps | Out-Null
            foreach ($c in 'config.json', 'offmesh.txt') { if (Test-Path -LiteralPath (Join-Path $cfgDir $c)) { Copy-Item -LiteralPath (Join-Path $cfgDir $c) $jd } }
            $slots += @{ Dir = $jd; Proc = $null; Task = $null; Start = $null }
        }
        $outDir = Join-Path $Staging 'mmaps'
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $queue = New-Object System.Collections.Queue
        foreach ($t in $tasks) { $queue.Enqueue($t) }
        $done = 0; $failed = @()
        while ($queue.Count -gt 0 -or @($slots | Where-Object { $_.Proc }).Count -gt 0) {
            foreach ($s in $slots) {
                if ($s.Proc -and $s.Proc.HasExited) {
                    $t = $s.Task; $mm = '{0:D3}{1:D2}{2:D2}' -f $t.Map, $t.Y, $t.X
                    $tile = Join-Path $s.Dir "mmaps\$mm.mmtile"
                    # MoveMapGen --silent returns 1 on success (generator.cpp), negative on errors
                    if ($s.Proc.ExitCode -eq 1 -and (Test-Path -LiteralPath $tile)) {
                        Move-Item -LiteralPath $tile -Destination $outDir -Force
                        Copy-Item -LiteralPath (Join-Path $s.Dir ('mmaps\{0:D3}.mmap' -f $t.Map)) -Destination $outDir -Force
                        $done++
                        Write-Host ('  [{0}/{1}] map {2} tile {3},{4} done in {5} s' -f $done, $tasks.Count, $t.Map, $t.X, $t.Y, [int]((Get-Date) - $s.Start).TotalSeconds)
                    } elseif ($s.Proc.ExitCode -eq 1) {
                        $done++
                        Write-Host ('  [{0}/{1}] map {2} tile {3},{4}: no navmesh data (empty tile), nothing written' -f $done, $tasks.Count, $t.Map, $t.X, $t.Y)
                    } else {
                        $failed += ('{0}:{1},{2} (exit {3})' -f $t.Map, $t.X, $t.Y, $s.Proc.ExitCode)
                        Write-Host ('  map {0} tile {1},{2} FAILED (exit {3}), see {4}' -f $t.Map, $t.X, $t.Y, $s.Proc.ExitCode, $logs)
                    }
                    $s.Proc = $null; $s.Task = $null
                }
                if (-not $s.Proc -and $queue.Count -gt 0) {
                    $t = $queue.Dequeue()
                    $s.Task = $t; $s.Start = Get-Date
                    $s.Proc = Start-Tool 'MoveMapGen.exe' @("$($t.Map)", '--tile', "$($t.X),$($t.Y)", '--silent') $s.Dir ('movemapgen_{0}_{1}_{2}' -f $t.Map, $t.X, $t.Y)
                }
            }
            Start-Sleep -Milliseconds 500
        }
        if ($failed.Count) { Write-Host "  FAILED tiles: $($failed -join '  ')" }
    }
    $timings.mmaps = [int]$sw.Elapsed.TotalSeconds
    Write-Host "  mmaps step: $($timings.mmaps) s"
}

# ---------------------------------------------------------------- change report
Write-Host "`n[report]"
$kinds = @('maps', 'vmaps', 'mmaps', 'dbc' | Where-Object { Test-Path -LiteralPath (Join-Path $Staging $_) })
if ($kinds.Count) {
    Invoke-Report ($kinds -join ',') (Join-Path $Staging 'changes.json') (Join-Path $Staging 'changes.txt')
    Write-Host "  full lists: $Staging\changes.txt (and changes.json)"
}
$run = [ordered]@{ time = (Get-Date).ToString('s'); client = $Client; patch3 = (Test-Path -LiteralPath $patch3); staging = $Staging
    serverData = $ServerData; steps = $StepList; maps = $MapList; seconds = $timings
    options = @{ mapHeightAsInt = [bool]$MapHeightAsInt; vmapSmall = [bool]$VmapSmall } }
$run | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Staging 'reextract_run.json') -Encoding UTF8
Write-Host "`nDone. Nothing was written outside $Staging. To install: stop mangosd, then  .\swap_in.ps1 -DryRun  /  .\swap_in.ps1"
