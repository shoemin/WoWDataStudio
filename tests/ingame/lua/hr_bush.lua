-- hr_bush.lua: hot reload of a model PLACED in the world (a doodad of the ADT: DurotarBush01, 29 yd W of the start point; wowcode.dll
-- re-creates it through the restream, CMap_FlushForRestream). At the start point (o = 1.25), turned 45 degrees to the bush (s1's pan1),
-- camera 15 yd behind: BEFORE, 'hrphase install', AFTER 30 s later, then 20 s more in the world.
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(1.3, 'turn to bush', function() FlipCameraYaw(45) end)
SC_Step(2, 'before', function() VT_UI(false); VT_Shot('hr_bush_before'); VT_Burst('hr_before', 2) end)
SC_Step(1.5, 'ask for the reload', function() AT_Log('hrphase install') end)
SC_Step(30, 'after', function() VT_UI(false); VT_Shot('hr_bush_after'); VT_Burst('hr_after', 2) end)
SC_Step(10, 'alive 1', function() VT_Facts(); AT_Log('hrphase alive-10s') end)
SC_Step(10, 'alive 2', function() VT_Facts(); AT_Log('hrphase done') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
