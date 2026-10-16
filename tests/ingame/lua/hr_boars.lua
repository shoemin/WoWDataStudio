-- hr_boars.lua: hot reload in the running game (WoW Data Studio 0.11.0, wowcode.dll HR1; tests\ingame\hotreload\run_hotreload.ps1).
-- At s2 (16 yd west of Mottled Boar spawn 4691, facing east, camera 7 yd behind): the pictures BEFORE, then the line 'hrphase install'
-- asks run_hotreload.ps1 to install the edits as loose files and run "wds changes reload --wait" while this session keeps running;
-- 30 s later the same pictures AFTER (no restart), then 20 s more in the world (the game must stay healthy after the reload).
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(2, 'before wide', function() VT_UI(false); VT_Shot('hr_boars_before'); VT_Burst('hr_before', 2) end)
SC_Step(2.5, 'target', function() SC_Hw('TargetNearestEnemy()') end)
SC_Step(1.5, 'before near', function() VT_Target(); VT_Shot('hr_boar_near_before') end)
SC_Step(1.5, 'ask for the reload', function() AT_Log('hrphase install') end)
SC_Step(30, 'after wide', function() VT_UI(false); VT_Shot('hr_boars_after'); VT_Burst('hr_after', 2) end)
SC_Step(2.5, 'target again', function() if not UnitExists('target') then SC_Hw('TargetNearestEnemy()') end end)
SC_Step(1.5, 'after near', function() VT_Target(); VT_Shot('hr_boar_near_after') end)
SC_Step(10, 'alive 1', function() VT_Facts(); AT_Log('hrphase alive-10s') end)
SC_Step(10, 'alive 2', function() VT_Facts(); VT_Target(); AT_Log('hrphase done') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
