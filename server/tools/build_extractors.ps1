# SPDX-License-Identifier: MIT
# Builds ONLY the four map extractors (mapextractor, vmapextractor, vmap_assembler, MoveMapGen) from
# WoWDecomp\work\server_src into WoWDataTool\server\build, binaries -> WoWDataTool\server\bin.
# Never builds mangosd, never installs anything (no INSTALL target), never touches Server\.
#   powershell -ExecutionPolicy Bypass -File build_extractors.ps1 [-Config Release] [-SkipConfigure]
param([string]$Config = 'Release', [switch]$SkipConfigure)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSScriptRoot                 # WoWDataTool\server
$w = Split-Path -Parent (Split-Path -Parent $here)       # WoW Custom Client
$cmake = 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$src = "$w\WoWDecomp\work\server_src"
$bld = "$here\build"
$bin = "$here\bin"
$ace = "$w\WoWDecomp\work\libs\ace\ACE_wrappers"
$tbb = "$w\WoWDecomp\work\libs\tbb\tbb"
$env:PATH += ';C:\Program Files (x86)\Microsoft Visual Studio\Installer'
New-Item -ItemType Directory -Force $bld, $bin | Out-Null

# the one-line patch-3 change (idempotent; keeps CRLF; original saved in patches\System.cpp.orig)
python "$here\tools\apply_patch3.py" $src "$here\patches"
if ($LASTEXITCODE -ne 0) { throw 'patch-3 change did not apply' }

# server_src's configure step rewrites src\shared\migrations_list.h (same content): keep its timestamp so the
# normal server build (tools\build_server.ps1) does not see a changed header.
$mig = "$src\src\shared\migrations_list.h"
$migTime = (Get-Item $mig).LastWriteTimeUtc
if (-not $SkipConfigure) {
    # CMAKE_RUNTIME_OUTPUT_DIRECTORY_<CONFIG> beats the top-level set(CMAKE_RUNTIME_OUTPUT_DIRECTORY server_src/bin)
    & $cmake -S $src -B $bld -G 'Visual Studio 17 2022' -A x64 "-DACE_ROOT=$ace" "-DTBB_ROOT_DIR=$tbb" -DUSE_EXTRACTORS=1 `
        "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_$($Config.ToUpper())=$bin" "-DCMAKE_LIBRARY_OUTPUT_DIRECTORY_$($Config.ToUpper())=$bld\lib"
    if ($LASTEXITCODE -ne 0) { throw 'cmake configure failed' }
    (Get-Item $mig).LastWriteTimeUtc = $migTime
}
& $cmake --build $bld --config $Config --target mapextractor vmapextractor vmap_assembler MoveMapGen -- /m /nologo /v:m
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
# runtime DLLs: vmap_assembler / MoveMapGen link ACE, MoveMapGen also TBB (tbb.dll + tbbmalloc.dll via the shared lib)
Copy-Item "$ace\lib\ACE.dll" $bin -Force
Get-ChildItem "$tbb\bin\intel64\vc14" -Filter 'tbb*.dll' | Where-Object { $_.Name -notmatch 'debug' } | Copy-Item -Destination $bin -Force
Get-ChildItem $bin -Filter *.exe | ForEach-Object { '{0,-20} {1,10:n0} bytes  {2}' -f $_.Name, $_.Length, $_.LastWriteTime }
