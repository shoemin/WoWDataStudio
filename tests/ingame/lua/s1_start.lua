-- s1_start.lua: at the Valley of Trials start point, the character facing the Den (o = 1.25), camera 10 yd behind (session.ps1 -Camera).
-- UI hidden. FlipCameraYaw(+d) turns the view counter-clockwise (towards larger orientation; checked on the Den in the trial run).
-- Panorama of 8 pictures 45 degrees apart (pan k looks at o = 1.25 + k x 0.785): pan0 the Den (#7) with DurotarTree03 (#3) on the right,
-- pan1 DurotarBush01 (#4, 29 yd), pan5 / pan6 the mound (#6, o = -0.72); the ground (#5) everywhere. Then aimed views.
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
for i = 0, 7 do
  local k = i
  SC_Step(2.5, 'pan ' .. k, function() VT_UI(false); VT_Shot(string.format('s1_pan%d', k)) end)
  SC_Step(1.3, 'turn ' .. k, function() FlipCameraYaw(45) end)
end
SC_Step(2, 'den', function() VT_UI(false); VT_Shot('s1_den') end)                                            -- o = 1.25
SC_Step(1.3, 'turn to tree', function() FlipCameraYaw(-28) end)                                              -- o = 0.77
SC_Step(2, 'tree', function() VT_Shot('s1_tree') end)
SC_Step(1.3, 'turn to bush', function() FlipCameraYaw(73) end)                                               -- o = 2.04
SC_Step(2, 'bush', function() VT_Shot('s1_bush') end)
SC_Step(1.3, 'turn to mound', function() FlipCameraYaw(-157) end)                                            -- o = -0.70
SC_Step(2, 'mound', function() VT_Shot('s1_mound') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
