-- v3_scorpid.lua (0.10.0 test, item A5): 9 yd south-west of Scorpid Worker spawn 8552 (run 20261010's S3 view), camera 7 yd.
-- Scorpion.m2's two attack animations (AttackUnarmed, AttackUnarmed [2]) now swell the stinger 2.5x at mid-strike. Curse of Weakness
-- (no damage) makes the scorpid come and attack; an external frame series and in-game pictures catch the strikes.
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(1.5, 'target', function() SC_Hw('TargetByName("Scorpid Worker")') end)
SC_Step(1.5, 'idle', function() VT_Target(); VT_UI(false); VT_Shot('c1_idle') end)
SC_Step(1.2, 'aggro', function() SC_Hw('CastSpellByName("Curse of Weakness")') end)
SC_Step(1.0, 'run', function() VT_Target(); VT_Shot('c1_move') end)
SC_Step(1.3, 'side + series', function() FlipCameraYaw(80); VT_Burst('scorp_attack', 10) end)
for k = 1, 6 do
  local n = k
  SC_Step(1.3, 'attack ' .. n, function() VT_Target(); VT_Shot('c1_attack' .. n) end)
end
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
