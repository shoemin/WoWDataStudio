-- d_cast.lua (designers test, S1 in action): three casts of "__S1NAME__" (__S1__, learned from the quest chain's reward in the q1 session
-- just before) at Mottled Boars, each with an external frame series (wowshot) for the visual: Shadow Bolt's casting hands, then
-- Fireball's missile and impact, duplicated with the tint #40FF60 (green) at size x1.5. The combat log lines ("Your __S1NAME__ hits
-- Mottled Boar for N Shadow damage") are logged and shown in the combat log tab. When the character does not know S1 (state 'before'),
-- the stock Shadow Bolt is cast instead: the reference picture of the original visual.
-- 2026-10-16 (second run): the first run stood at the hunting spot P, where TargetNearestEnemy reaches no boar (they are 18-22 yd away
-- at the sides; the game's area is 41 yd ahead and 10 yd around): no cast at all. Now each cast has its own boar viewpoint, 16 yd from
-- a boar spawn and facing it (the session starts at the first, the 0.10 / hot reload runs' view of spawn 4691; ".go xyzo", a GM
-- command, for the second (7979) and third (8547)); TargetByName is the second way to target. S1 (777-888 damage) kills a boar at once.
local S1 = '__S1NAME__'
local VIEWS = { '__B1__', '__B2__', '__B3__' }
local EVENTS = { 'CHAT_MSG_SPELL_SELF_DAMAGE', 'CHAT_MSG_COMBAT_HOSTILE_DEATH', 'CHAT_MSG_SPELL_FAILED_LOCALPLAYER', 'UI_ERROR_MESSAGE',
                 'SPELLCAST_START', 'SPELLCAST_STOP', 'SPELLCAST_FAILED', 'SPELLCAST_INTERRUPTED', 'LEARNED_SPELL_IN_TAB', 'CHAT_MSG_SYSTEM' }
local spell = { idx = nil, name = nil }
local function live() return VT_Live('Mottled Boar') end

SC_Step(18, 'settle', function()
  VT_Facts(); VT_UI(true); VT_Events(EVENTS); VT_TrackErrors()
  spell.idx = VT_SpellFacts(S1)
  if spell.idx then spell.name = S1 else spell.idx = VT_FindSpell('Shadow Bolt'); spell.name = 'Shadow Bolt'; SC_Note('S1 not known: casting the stock Shadow Bolt (the reference visual), index ' .. tostring(spell.idx)) end
end)
-- 2026-10-16 (run 4): the quest reward did not teach S1 (the server kept it as a disabled spell), so S1 was never cast. When S1 is not
-- known, the GM command '.learn all_trainer' (game master level) teaches every spell of the class's trainers, the test trainer's S1
-- included (the server's trainer way of learning, LearnSpell); the character's rows are put back after the run as always.
SC_Step(1.0, 'learn S1 (GM)', function()
  if spell.name == S1 then return end
  VT_Gm('.learn all_trainer')
end)
SC_Step(4.0, 'S1 known?', function()
  if spell.name == S1 then return end
  local idx = VT_SpellFacts(S1)
  if idx then spell.idx = idx; spell.name = S1; SC_Note('S1 learned through .learn all_trainer: casting ' .. S1)
  else spell.idx = VT_FindSpell('Shadow Bolt'); SC_Note('S1 still not known after .learn all_trainer: casting the stock Shadow Bolt, index ' .. tostring(spell.idx)) end
end)
SC_Step(1.5, 'wide', function() VT_UI(false); VT_Shot('c1_wide') end)
for k = 1, 3 do
  local kk = k
  SC_Step(kk == 1 and 0.5 or 1.0, 'go ' .. kk, function() VT_UI(true); ClearTarget(); if kk > 1 then VT_Gm('.go xyzo ' .. VIEWS[kk]) end end)
  SC_Step(kk == 1 and 0.5 or 3.5, 'target ' .. kk, function() VT_Events(EVENTS); ClearTarget(); SC_Hw('TargetNearestEnemy()') end)
  SC_Step(1.2, 'retarget ' .. kk, function() VT_Target(); if not live() then VT_TargetByNameLive('Mottled Boar') end end)
  SC_Step(1.0, 'cast ' .. kk, function()
    VT_Target()
    if not live() then SC_Note('try ' .. kk .. ': no live boar targeted'); return end
    if not spell.idx then SC_Note('no spell to cast'); return end
    VT_LastError = nil
    VT_UI(false)
    VT_Burst('cast' .. kk, 5)
    SC_Hw("CastSpell(" .. spell.idx .. ", 'spell')")
  end)
  SC_Step(1.2, 'casting ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_a') end)
  SC_Step(1.2, 'flight ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_b') end)
  SC_Step(1.2, 'impact ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_c') end)
  SC_Step(2.0, 'result ' .. kk, function()
    if not UnitExists('target') then TargetLastTarget() end
    SC_Note('after cast ' .. kk .. ' (' .. tostring(spell.name) .. '): target ' .. tostring(UnitName('target')) .. ' dead ' .. tostring(UnitIsDead('target')) .. ' hp ' .. tostring(UnitHealth('target')) ..
            (VT_LastError and (', error: ' .. VT_LastError) or ''))
  end)
end
SC_Step(1.5, 'combat log', function()
  VT_UI(true)
  if FCF_SelectDockFrame and ChatFrame2 then FCF_SelectDockFrame(ChatFrame2) end
end)
SC_Step(1.5, 'combat log shot', function() VT_Shot('c1_combatlog') end)
SC_Step(1.0, 'spell tooltip', function()
  if FCF_SelectDockFrame and ChatFrame1 then FCF_SelectDockFrame(ChatFrame1) end
  if spell.idx then VT_TipAt(); GameTooltip:SetSpell(spell.idx, 'spell'); GameTooltip:Show(); VT_TipLines(tostring(spell.name)) end
end)
SC_Step(1.5, 'tooltip shot', function() VT_Shot('c1_tooltip') end)
SC_Step(1.0, 'end', function() GameTooltip:Hide(); ClearTarget(); VT_UI(true) end)
SC_Start()
