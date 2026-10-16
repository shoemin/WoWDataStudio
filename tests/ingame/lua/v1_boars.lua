-- v1_boars.lua (0.10.0 test, items A1-A3): 16 yd west of Mottled Boar spawn 4691, facing east (run 20261010's S2 view), camera 7 yd.
-- A1: the boars wander with the retimed Walk (1.5 s, move speed 1.667 yd/s): frame series to see that the feet do not slide.
-- A2: standing boars play the new idle "Stand [4]" (50 % of the Stand variations: head turned, the spike swings).
-- A3: every boar carries the spike on its head (a new bone and part).
-- Each external frame series (VT_Burst, wowshot, about 15 kept frames per second) runs while in-game pictures are taken.
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(2, 'wide', function() VT_UI(false); VT_Shot('b1_wide') end)
SC_Step(1.2, 'target', function() SC_Hw('TargetNearestEnemy()') end)
SC_Step(1.5, 'boar', function() VT_Target(); VT_Shot('b1_boar_near') end)
-- idle and wandering, facing east: 12 s of frames, an in-game picture every 2.5 s
SC_Step(1.0, 'series 1', function() VT_Burst('herd_east', 12) end)
for k = 1, 4 do
  local n = k
  SC_Step(2.5, 'east ' .. n, function() VT_Shot('b1_east_' .. n) end)
end
-- the side view (the boars cross the picture when they wander)
SC_Step(2.0, 'side', function() FlipCameraYaw(90) end)
SC_Step(1.5, 'side shot', function() VT_Target(); VT_Shot('b1_side') end)
SC_Step(1.0, 'series 2', function() VT_Burst('herd_side', 12) end)
for k = 1, 4 do
  local n = k
  SC_Step(2.5, 'side ' .. n, function() VT_Shot('b1_side_' .. n) end)
end
-- the other side
SC_Step(2.0, 'other side', function() FlipCameraYaw(-180) end)
SC_Step(1.5, 'other side shot', function() VT_Shot('b1_other_side') end)
SC_Step(1.0, 'series 3', function() VT_Burst('herd_west', 10) end)
for k = 1, 3 do
  local n = k
  SC_Step(2.8, 'west ' .. n, function() VT_Shot('b1_west_' .. n) end)
end
SC_Step(2, 'back', function() FlipCameraYaw(90); VT_Shot('b1_wide_end') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
