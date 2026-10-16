-- v3_scorpid_rh.lua (0.10.0 test, item A5 near Razor Hill): 9 yd south-west of Clattering Scorpid spawn 12988 (south of Razor Hill), camera 7 yd.
-- The same Scorpion.m2 edit as v3_scorpid.lua on the scorpids that live by Razor Hill (Clattering Scorpid, display 2486).
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(1.5, 'target', function() SC_Hw('TargetByName("Clattering Scorpid")') end)
SC_Step(1.5, 'idle', function() VT_Target(); VT_UI(false); VT_Shot('c2_idle') end)
SC_Step(1.2, 'aggro', function() SC_Hw('CastSpellByName("Curse of Weakness")') end)
SC_Step(1.0, 'run', function() VT_Target(); VT_Shot('c2_move') end)
SC_Step(1.3, 'side + series', function() FlipCameraYaw(80); VT_Burst('scorp_rh_attack', 10) end)
for k = 1, 6 do
  local n = k
  SC_Step(1.3, 'attack ' .. n, function() VT_Target(); VT_Shot('c2_attack' .. n) end)
end
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
