<#
.SYNOPSIS
  Exports every texture of every creature spawned in a zone as PNG (WoW Data Studio command line, wds).

.DESCRIPTION
  1. "wds find search --type creature --zone <zone> --json" lists the creatures spawned in the zone or its subzones
     (the spawns come from your server's database: run "wds serverdb reload" once when the list is empty);
  2. "wds textures export-all <creature> --out <folder>" writes the textures each one uses (under their game paths,
     each PNG with its .blp.json note), so the same texture shared by several creatures is written once.
  Nothing in the game or the workspace changes. Exit code: 0 ok, else the exit code of the failing wds command.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File export_zone_creature_textures.ps1 -Zone Durotar -Out exports\Durotar
  powershell -ExecutionPolicy Bypass -File export_zone_creature_textures.ps1 -Zone "Elwynn Forest" -Out D:\elwynn -Variants
#>
param(
    [Parameter(Mandatory = $true)][string]$Zone,
    [Parameter(Mandatory = $true)][string]$Out,
    [switch]$Variants,                 # also every other skin of the creatures' models
    [string]$WdsHome = "",                # wds --home (default: the app's folder)
    [string]$Wds = "wds"               # the wds program (default: wds on the PATH)
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # wds writes UTF-8
$common = @()
if ($WdsHome -ne "") { $common += @("--home", $WdsHome) }

$json = & $Wds @common find search --type creature --zone $Zone --limit 0 --json --quiet
$result = ($json -join "`n") | ConvertFrom-Json
if (-not $result.ok) { Write-Host "wds find search: $($result.error)" -ForegroundColor Red; exit $result.exitCode }
$creatures = @($result.data.results)
Write-Host "$($creatures.Count) creature(s) in $Zone"

$done = 0
foreach ($c in $creatures) {
    $cmdArgs = @("textures", "export-all", $c.key, "--out", $Out, "--json", "--quiet")
    if ($Variants) { $cmdArgs += "--variants" }
    $r = ((& $Wds @common @cmdArgs) -join "`n") | ConvertFrom-Json
    if (-not $r.ok) {
        if ($r.exitCode -eq 6) { Write-Host "  $($c.name): no texture"; continue }   # nothing found: not an error here
        Write-Host "wds textures export-all $($c.key): $($r.error)" -ForegroundColor Red; exit $r.exitCode
    }
    $done += $r.data.files.Count
    Write-Host ("  {0}: {1} texture(s)" -f $c.name, $r.data.files.Count)
}
Write-Host "Exported $done PNG file(s) into $Out"
exit 0
