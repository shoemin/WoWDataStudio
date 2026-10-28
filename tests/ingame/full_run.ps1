# full_run.ps1 -- the whole in-game matrix after `IngameRunner prepare`: baseline (nothing installed) -> "Test all in game" (loose files)
# -> sessions -> "Remove all from game" -> "Build release patch" installed as Data\patch-3.MPQ -> sessions -> patch removed.
# Every step's Data folder listing goes to <run>\delivery_log.txt (IngameRunner deliver).
param([Parameter(Mandatory)][string]$Run, [string[]]$Variants = @('before', 'loose', 'patch3'), [string[]]$Only = @())
$here = $PSScriptRoot
$client = Join-Path (Resolve-Path "$PSScriptRoot\..\..\..").Path 'Client_visual'
$exe = "$here\IngameRunner\bin\Release\net8.0\IngameRunner.exe"
$home_ = "$Run\studio_home"
function Step($s) { "[{0:HH:mm:ss}] ==== {1}" -f (Get-Date), $s }
foreach ($v in $Variants) {
    switch ($v) {
        'before' { Step 'baseline: nothing installed'; & $exe deliver $client $home_ $Run unloose; & $exe deliver $client $home_ $Run unpatch }
        'loose' { Step 'Test all in game (loose files)'; & $exe deliver $client $home_ $Run unpatch; & $exe deliver $client $home_ $Run loose }
        'patch3' { Step 'Remove all from game, then Build release patch + install'; & $exe deliver $client $home_ $Run unloose; & $exe deliver $client $home_ $Run patch }
    }
    Step "sessions $v"
    & "$here\run_sessions.ps1" -Tag $v -OutDir "$Run\shots" -Only $Only
}
Step 'cleanup: remove loose files and patch-3.MPQ'
& $exe deliver $client $home_ $Run unloose
& $exe deliver $client $home_ $Run unpatch
Step 'all done'
