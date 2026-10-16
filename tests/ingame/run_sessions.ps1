# run_sessions.ps1 -- the in-game sessions of the visual test for one delivery state (before / loose / patch3), one after the other.
#   .\run_sessions.ps1 -Tag before -OutDir <run>\shots [-Only s1,s3]
param([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$OutDir, [string[]]$Only = @())
$here = $PSScriptRoot
$sessions = [ordered]@{
    s1 = @{ Lua = 's1_start.lua';     Pos = '-618.5,-4251.7,38.8,1.25'; Camera = '15,6' }   # start point, facing the Den
    s2 = @{ Lua = 's2_boars.lua';     Pos = '-682.7,-4271,39.3,4.712'; Camera = '7,12' }   # 16 yd west of boar spawn 4691, facing east
    s3 = @{ Lua = 's3_scorpid.lua';   Pos = '-688,-4151,34.6,0.60'; Camera = '7,15' }        # 9 yd from Scorpid Worker spawn 8552
    s4 = @{ Lua = 's4_character.lua'; Pos = '-618.5,-4251.7,38.8,1.25'; Camera = '5,8' }   # start point, UI shown
}
foreach ($k in $sessions.Keys) {
    if ($Only.Count -and $Only -notcontains $k) { continue }
    $s = $sessions[$k]
    $t0 = Get-Date
    for ($try = 1; $try -le 2; $try++) {
        $out = & "$here\session.ps1" -Scenario "$here\lua\$($s.Lua)" -Tag $Tag -Pos $s.Pos -Camera $s.Camera -OutDir $OutDir 2>&1 | Out-String -Width 250
        $out
        if ($out -match 'result=SUCCESS') { break }
        "!! $k attempt $try did not succeed; retrying"
    }
    "== $k done in {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds
}
