# restore_char.ps1 -- put the test character (guid 1000, Statclone) back at its start point after the visual test: exactly the "home" step of
# WoWDecomp\tools\scenario.ps1 (playercreateinfo of an orc warlock; the position it had before this test), after it is offline.
. '<your WoW Custom Client folder>\WoWDecomp\tools\lib_wowtest.ps1'
$mtx = New-Object Threading.Mutex($false, 'Global\WoWDecompClient')
try { [void]$mtx.WaitOne() } catch [Threading.AbandonedMutexException] { }
try {
    Wait-CharOffline
    $sp = (Q mangos "select map, zone, position_x, position_y, position_z, orientation from playercreateinfo where race=2 and class=9") -split '\s+'
    Q characters "update characters set map=$($sp[0]), zone=$($sp[1]), position_x=$($sp[2]), position_y=$($sp[3]), position_z=$($sp[4]), orientation=$($sp[5]) where guid=1000" | Out-Null
    Q characters "select guid, name, map, zone, position_x, position_y, position_z, orientation, online from characters where guid=1000"
} finally { $mtx.ReleaseMutex() }
