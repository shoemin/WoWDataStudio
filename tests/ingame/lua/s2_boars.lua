-- s2_boars.lua: 16 yd west of Mottled Boar spawn 4691 (-682.7, -4271, facing east), camera 7 yd behind. #1 boar skin: the view over the
-- boars and the nearest boar targeted; #9 Shadow Bolt (missile Spells\DeathCoil_Missile: SKULL.BLP + purple glows) cast at boars, seen from
-- the side, with in-game screenshots at fixed times and an external wowshot series (session.ps1) while the cast and the missile run.
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(false) end)
SC_Step(2, 'wide', function() VT_UI(false); VT_Shot('s2_boars_wide') end)
SC_Step(1.2, 'target', function() SC_Hw('TargetNearestEnemy()') end)
SC_Step(1.5, 'boar', function() VT_Target(); VT_Shot('s2_boar_near') end)
SC_Step(1.3, 'side view', function() FlipCameraYaw(90) end)
SC_Step(1.5, 'boar side', function() VT_Shot('s2_boar_side') end)
local casts = { { 'sb1', 0.9, 1.25 }, { 'sb2', 1.5, 1.2 }, { 'sb3', 2.0, 1.2 } }
for _, c in ipairs(casts) do
  local name, first, gap = c[1], c[2], c[3]
  SC_Step(1.5, 'target ' .. name, function() if not UnitExists('target') or UnitIsDead('target') then SC_Hw('TargetNearestEnemy()') end end)
  SC_Step(1.2, 'cast ' .. name, function() VT_Target(); VT_Burst(name, 5); SC_Hw('CastSpellByName("Shadow Bolt")') end)
  SC_Step(first, name .. ' a', function() VT_Shot('s2_' .. name .. '_a') end)
  SC_Step(gap, name .. ' b', function() VT_Shot('s2_' .. name .. '_b') end)
  SC_Step(gap, name .. ' c', function() VT_Shot('s2_' .. name .. '_c') end)
end
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
