-- v2_quilboar.lua (0.10.0 test, item A4): 8 yd south of Razormane Quilboar spawn 12318 (north-west of Razor Hill), facing north, camera 7 yd.
-- QuillBoar.m2's HandRight attachment point was moved 0.6 yd up: the creature's weapon (item 1901) is drawn there, above the hand,
-- standing and while it attacks. Curse of Weakness (no damage) makes it come to the character and fight (the character is level 255).
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(1.5, 'target', function() SC_Hw('TargetByName("Razormane Quilboar")') end)
SC_Step(1.5, 'idle', function() VT_Target(); VT_UI(false); VT_Shot('q1_idle') end)
SC_Step(1.3, 'side', function() FlipCameraYaw(60) end)
SC_Step(1.5, 'idle side', function() VT_Shot('q1_idle_side') end)
SC_Step(1.3, 'back', function() FlipCameraYaw(-60) end)
SC_Step(1.2, 'aggro', function() SC_Hw('CastSpellByName("Curse of Weakness")') end)
SC_Step(1.2, 'run', function() VT_Target(); VT_Shot('q1_move') end)
SC_Step(1.3, 'series', function() FlipCameraYaw(70); VT_Burst('quil_attack', 8) end)
for k = 1, 5 do
  local n = k
  SC_Step(1.4, 'attack ' .. n, function() VT_Target(); VT_Shot('q1_attack' .. n) end)
end
SC_Step(1.3, 'front', function() FlipCameraYaw(110) end)
SC_Step(1.5, 'attack front', function() VT_Shot('q1_attack_front') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
