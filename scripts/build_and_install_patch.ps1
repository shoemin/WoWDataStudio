<#
.SYNOPSIS
  Builds the release patch (patch-3.MPQ) of all your changes and installs it into the game (wds).

.DESCRIPTION
  1. "wds changes build-patch --dry-run" lists what goes in (and stops when there is nothing to build);
  2. "wds changes build-patch --install --yes" builds it, checks it by reading it back, backs up a patch-3.MPQ that is
     already in the game, and installs the new one (with -NoInstall: build only, the game is not changed);
  3. "wds changes patch-state" prints what is installed now.
  Close the game first. Exit code: 0 ok, else the exit code of the failing wds command (see "wds --help").

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build_and_install_patch.ps1
  powershell -ExecutionPolicy Bypass -File build_and_install_patch.ps1 -NoInstall
#>
param(
    [switch]$NoInstall,
    [string]$WdsHome = "",                # wds --home (default: the app's folder)
    [string]$Wds = "wds"               # the wds program (default: wds on the PATH)
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # wds writes UTF-8
$common = @()
if ($WdsHome -ne "") { $common += @("--home", $WdsHome) }

function Invoke-Wds([string[]]$a) {
    $r = ((& $Wds @common @a --json --quiet) -join "`n") | ConvertFrom-Json
    if (-not $r.ok) { Write-Host "wds $($a -join ' '): $($r.error)" -ForegroundColor Red; exit $r.exitCode }
    return $r
}

$plan = Invoke-Wds @("changes", "build-patch", "--dry-run")
Write-Host "Included: $($plan.data.files.Count) file(s)"
$plan.data.files | ForEach-Object { Write-Host "  $_" }
if ($plan.data.serverNote) { Write-Host $plan.data.serverNote -ForegroundColor Yellow }

if ($NoInstall) { $built = Invoke-Wds @("changes", "build-patch") }
else { $built = Invoke-Wds @("changes", "build-patch", "--install", "--yes") }
Write-Host "Built: $($built.data.path) ($($built.data.size) bytes, SHA-256 $($built.data.sha256))"
if ($built.data.installedTo) { Write-Host "Installed into the game: $($built.data.installedTo)" }
if ($built.data.backup) { Write-Host "The previous patch-3.MPQ was backed up to: $($built.data.backup)" }

$state = Invoke-Wds @("changes", "patch-state")
Write-Host $state.data.text
exit 0
