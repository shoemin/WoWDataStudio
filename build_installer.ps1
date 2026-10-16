<#
.SYNOPSIS
  Builds the WoW Data Studio installer: dist\WoWDataStudio-Setup-<version>.exe

.DESCRIPTION
  1. checks the user manual against the app (app\WoWData.Tests\ManualTests.cs);
  2. publishes the app (WoWDataStudio.exe) AND its command line (wds.exe) self-contained (Release, win-x64) into the same
     bin\ folder, from the same sources, with the same version and build stamp; fails when either is missing, when their
     versions differ, or when "wds version" does not run (0.8.0: one product, two front ends, always shipped together);
  3. renders the manual (docs\manual\*.md) to HTML with app\tools\ManualBuilder (Markdig);
  4. builds the Blender add-on zip (blender_addon\build_zip.py);
  5. stages bin\, manual\, blender\, scripts\ (the command line's example scripts), docs\LOOSE_FILES_PATCH.md, docs\HOT_RELOAD.md,
     docs\NOGGIT.md, LICENSE, THIRD_PARTY.md in dist\stage\
     (Noggit itself and the server tools are not bundled: GPL; NOGGIT.md says where to get them);
  6. compiles installer\WoWDataStudio.iss with Inno Setup 6 (ISCC.exe).
  Nothing is written outside WoWDataTool\ (dist\, build\manual\, app\**\bin|obj); no World of Warcraft folder is touched.

  Needs: the .NET 8 SDK, Python 3 (for the add-on zip), Inno Setup 6
  (winget install --id JRSoftware.InnoSetup -e --scope user).

.PARAMETER Version
  The version of the app, the command line and the installer (x.y.z). Default: <Version> of app\Directory.Build.props
  (the one place of the version). It is stamped into WoWDataStudio.exe and wds.exe, so they and the installer agree.

.PARAMETER SkipManualCheck
  Do not run ManualTests first (not for releases).

.PARAMETER TestBuild
  Build the TEST variant for automated install / uninstall checks: dist\WoWDataStudio-TestSetup-<version>.exe, with its
  own AppId, name ("WoW Data Studio TEST"), install folder, Start-menu group and Apps entry, so it can never replace or
  remove the user's real installation. Install it only with installer	est_install.ps1 (CONTRIBUTING.md).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build_installer.ps1
  powershell -ExecutionPolicy Bypass -File build_installer.ps1 -Version 0.3.0
  powershell -ExecutionPolicy Bypass -File build_installer.ps1 -Version 0.6.1 -TestBuild
#>
param(
    [string]$Version = "",
    [switch]$SkipManualCheck,
    [switch]$TestBuild
)

# cmdlets stop on errors; native tools (dotnet, python, ISCC) are checked by their exit code (Windows PowerShell 5.1 would
# turn their stderr output into terminating errors under "Stop")
$ErrorActionPreference = "Continue"
$PSDefaultParameterValues['*:ErrorAction'] = 'Stop'
$Root = $PSScriptRoot
$App = Join-Path $Root "app"
$Dist = Join-Path $Root "dist"
$Stage = Join-Path $Dist "stage"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Step([string]$text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Check([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" } }

if ($Version -eq "") {
    [xml]$props = Get-Content (Join-Path $App "Directory.Build.props")
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
# the build stamp shown by "wds version" and Help > About (the same for both programs)
$Build = Get-Date -Format "yyyyMMdd.HHmm"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 0.3.0 (got '$Version')" }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it: winget install --id JRSoftware.InnoSetup -e --scope user" }
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { throw "python not found (needed to build the Blender add-on zip)" }

Write-Host ("WoW Data Studio $Version installer" + $(if ($TestBuild) { " (TEST build: separate AppId, name and Start-menu group)" } else { "" }))
Write-Host "  Inno Setup: $iscc"

if (-not $SkipManualCheck) {
    Step "1/6 Checking the user manual against the app (ManualTests)"
    & dotnet test (Join-Path $App "WoWData.Tests\WoWData.Tests.csproj") -c Release --filter "FullyQualifiedName~ManualTests" -nologo -v q
    Check "The manual check (fix the manual: docs\manual\MAINTAINING.md)"
}

Step "2/6 Publishing the app and its command line (Release, self-contained, win-x64, version $Version build $Build)"
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force $Stage | Out-Null
$bin = Join-Path $Stage "bin"
foreach ($proj in @("WoWData.Cli\WoWData.Cli.csproj", "WoWDataStudio\WoWDataStudio.csproj")) {
    & dotnet publish (Join-Path $App $proj) -c Release -r win-x64 --self-contained true `
        -o $bin -p:Version=$Version -p:WdsBuild=$Build -nologo -v q
    Check "dotnet publish $proj"
}
Get-ChildItem $bin -Filter *.pdb | Remove-Item -Force
# one product, two front ends: both programs, the same version and build, and the command line runs
$guiExe = Join-Path $bin "WoWDataStudio.exe"
$cliExe = Join-Path $bin "wds.exe"
foreach ($exe in @($guiExe, $cliExe)) { if (-not (Test-Path $exe)) { throw "the build has no $(Split-Path $exe -Leaf): the app and the command line are always shipped together" } }
$guiVer = (Get-Item $guiExe).VersionInfo.ProductVersion
$cliVer = (Get-Item $cliExe).VersionInfo.ProductVersion
if ($guiVer -ne $cliVer) { throw "WoWDataStudio.exe ($guiVer) and wds.exe ($cliVer) have different versions" }
if (-not $guiVer.StartsWith("$Version+$Build")) { throw "the programs are version $guiVer, expected $Version+$Build" }
$wdsJson = (& $cliExe version --json) -join "`n" | ConvertFrom-Json
Check "wds version"
if ($wdsJson.data.version -ne $Version -or $wdsJson.data.build -ne $Build) { throw "wds version says $($wdsJson.data.display), expected $Version (build $Build)" }
if ($wdsJson.data.appVersion -ne $guiVer) { throw "wds sees the app as $($wdsJson.data.appVersion), expected $guiVer" }
Write-Host "  WoWDataStudio.exe and wds.exe: $guiVer"

Step "3/6 Rendering the user manual"
$manualOut = Join-Path $Root "build\manual"
& dotnet run --project (Join-Path $App "tools\ManualBuilder\ManualBuilder.csproj") -c Release -- `
    --manual (Join-Path $Root "docs\manual") --out $manualOut --version $Version
Check "The manual builder"
Copy-Item $manualOut (Join-Path $Stage "manual") -Recurse

Step "4/6 Building the Blender add-on zip"
$blender = Join-Path $Stage "blender"
New-Item -ItemType Directory -Force $blender | Out-Null
$zip = Join-Path $blender "wowdata_blender.zip"
& $python.Source -c "import sys; sys.path.insert(0, sys.argv[1]); import build_zip; print('wrote', build_zip.build(sys.argv[2]))" (Join-Path $Root "blender_addon") $zip
Check "The Blender add-on zip"

Step "5/6 Licences and notes"
Copy-Item (Join-Path $Root "scripts") (Join-Path $Stage "scripts") -Recurse   # the command line's example scripts
Copy-Item (Join-Path $Root "LICENSE") $Stage
Copy-Item (Join-Path $Root "THIRD_PARTY.md") $Stage
Copy-Item (Join-Path $Root "CHANGELOG.md") $Stage   # the release notes (0.6.1); known bugs are in the manual (known-issues)
New-Item -ItemType Directory -Force (Join-Path $Stage "docs") | Out-Null
Copy-Item (Join-Path $Root "docs\LOOSE_FILES_PATCH.md") (Join-Path $Stage "docs")
Copy-Item (Join-Path $Root "docs\HOT_RELOAD.md") (Join-Path $Stage "docs")   # 0.11.0: hot reload (the dialogs link it)
# Noggit is NOT bundled (GPL-3 + Qt LGPL obligations; see docs\ARCHITECTURE.md "Noggit and the installer"): a note says
# where to get / build it and how to point the app at it
Copy-Item (Join-Path $Root "installer\NOGGIT.md") (Join-Path $Stage "docs")

Step "6/6 Compiling the installer"
$isccArgs = @("/Q", "/DAppVersion=$Version", "/DStageDir=$Stage", "/DOutDir=$Dist")
if ($TestBuild) { $isccArgs += "/DTestBuild=1" }
& $iscc @isccArgs (Join-Path $Root "installer\WoWDataStudio.iss")
Check "ISCC"

$setup = Join-Path $Dist $(if ($TestBuild) { "WoWDataStudio-TestSetup-$Version.exe" } else { "WoWDataStudio-Setup-$Version.exe" })
if (-not (Test-Path $setup)) { throw "the installer was not produced: $setup" }
$size = (Get-Item $setup).Length / 1MB
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host ""
Write-Host ("Built {0} ({1:N1} MB) in {2:N0} s" -f $setup, $size, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host "SHA-256 $hash"
Write-Host "Version $Version (build $Build): WoWDataStudio.exe and wds.exe"
