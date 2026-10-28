# render_v010.ps1 -- the app-side pictures of the 0.10.0 items, drawn by WoW Data Studio's own renderer through the command line
# (wds models render, the app's "Save image..."): the game's model (--original) and the workspace edit at the same moments of an
# animation, as strips in <run>\app\renders\. Reads only the run's isolated home; writes only <run>\app\renders.
#   .\render_v010.ps1 -Run <run>
param([Parameter(Mandatory)][string]$Run, [string]$Wds = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path 'dist\stage\bin\wds.exe'))
$ErrorActionPreference = 'Stop'
$client = Join-Path (Resolve-Path "$PSScriptRoot\..\..\..").Path 'Client_visual'
$out = "$Run\app\renders"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter 'test_*' -ErrorAction SilentlyContinue | ForEach-Object { [IO.File]::Delete($_.FullName) }
function Draw([string]$model, [int]$display, [string]$seq, [int]$ms, [int]$yaw, [string]$name, [switch]$Original) {
    $f = "$out\$name.png"
    $a = @('--home', "$Run\studio_home", '--client', $client, 'models', 'render', $model, $f, '--display', $display, '--sequence', $seq, '--time', $ms, '--yaw', $yaw, '--size', '480x360')
    if ($Original) { $a += '--original' }
    & $Wds @a 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { "render failed: $name" }
    return $f
}
function Strip([string]$name, [int]$cols, [string[]]$files) { python "$PSScriptRoot\contact.py" "$out\$name.png" $cols 320 @files }

# A1: the walk at 8 equal phases (original 1.0 s, retimed 1.5 s): the same poses at the same phase
$o = @(); $e = @()
for ($k = 0; $k -lt 8; $k++) {
    $o += Draw 'Creature\Boar\Boar.m2' 503 'Walk' ([int](1000 * $k / 8)) 90 ("A1_walk_orig_{0}" -f $k) -Original
    $e += Draw 'Creature\Boar\Boar.m2' 503 'Walk' ([int](1500 * $k / 8)) 90 ("A1_walk_edit_{0}" -f $k)
}
Strip 'A1_walk_phases_original_top_edit_bottom' 8 ($o + $e)
# A2 + A3: Stand (original) and the new Stand [4] (edit) at 5 moments; the spike from three sides at rest
$o = @(); $e = @()
foreach ($ms in 0, 416, 833, 1250, 1600) {
    $o += Draw 'Creature\Boar\Boar.m2' 503 'Stand' $ms 30 ("A2_stand_orig_{0}" -f $ms) -Original
    $e += Draw 'Creature\Boar\Boar.m2' 503 'Stand [4]' $ms 30 ("A2_stand4_edit_{0}" -f $ms)
}
Strip 'A2_stand_original_top_new_stand4_bottom' 5 ($o + $e)
$s = @()
foreach ($y in 0, 90, 30) { $s += Draw 'Creature\Boar\Boar.m2' 503 'Stand' 0 $y ("A3_spike_yaw{0}" -f $y) }
foreach ($y in 0, 90, 30) { $s += Draw 'Creature\Boar\Boar.m2' 503 'Stand' 0 $y ("A3_orig_yaw{0}" -f $y) -Original }
Strip 'A3_spike_edit_top_original_bottom' 3 $s
# A4: the quilboar (the renderer draws no held weapon: the attachment point is checked by numbers and in the game)
$q = @((Draw 'Creature\Quillboar\QuillBoar.m2' 1218 'Attack1H' 500 60 'A4_attack1h_orig' -Original), (Draw 'Creature\Quillboar\QuillBoar.m2' 1218 'Attack1H' 500 60 'A4_attack1h_edit'))
Strip 'A4_quilboar_original_edit' 2 $q
# A5: the scorpion's two attacks at 5 moments, original and edit
foreach ($seq in 'AttackUnarmed', 'AttackUnarmed [2]') {
    $o = @(); $e = @()
    foreach ($ms in 0, 250, 500, 750, 950) {
        $tag = ($seq -replace '[^A-Za-z0-9]', '')
        $o += Draw 'Creature\Scorpion\Scorpion.m2' 2485 $seq $ms 70 ("A5_{0}_orig_{1}" -f $tag, $ms) -Original
        $e += Draw 'Creature\Scorpion\Scorpion.m2' 2485 $seq $ms 70 ("A5_{0}_edit_{1}" -f $tag, $ms)
    }
    Strip ("A5_{0}_original_top_edit_bottom" -f ($seq -replace '[^A-Za-z0-9]', '')) 5 ($o + $e)
}
# the numbers of the compare (app <-> command line parity: wds models compare / anims / skeleton of the workspace edits)
foreach ($m in 'Creature\Boar\Boar.m2', 'Creature\Quillboar\QuillBoar.m2', 'Creature\Scorpion\Scorpion.m2') {
    $n = [IO.Path]::GetFileNameWithoutExtension($m)
    & $Wds --home "$Run\studio_home" --client $client models compare $m 2>&1 | Out-File -Encoding utf8 "$out\${n}_compare.txt"
    & $Wds --home "$Run\studio_home" --client $client models anims $m 2>&1 | Out-File -Encoding utf8 "$out\${n}_anims_edited.txt"
    & $Wds --home "$Run\studio_home" --client $client models skeleton $m 2>&1 | Out-File -Encoding utf8 "$out\${n}_skeleton_edited.txt"
}
& $Wds --home "$Run\studio_home" --client $client server check spells --ids 900000 2>&1 | Out-File -Encoding utf8 "$out\server_check_spells_900000.txt"
& $Wds --home "$Run\studio_home" --client $client server check items --ids 900000,900001 2>&1 | Out-File -Encoding utf8 "$out\server_check_items.txt"
& $Wds --home "$Run\studio_home" --client $client server status 2>&1 | Out-File -Encoding utf8 "$out\server_status.txt"
"renders: $((Get-ChildItem $out -Filter *.png).Count) pictures in $out"
