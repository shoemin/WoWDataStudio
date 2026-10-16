-- d_cast.lua (designers test, S1 in action): at the hunting spot P facing north (Mottled Boar spawns 4691 and 7979 in front, 18 and 22 yd;
-- boars of level 1-2 die to the first hit). Three casts of "__S1NAME__" (__S1__, bought in the trainer session just before) at boars,
-- each with an external frame series (wowshot) for the visual: Shadow Bolt's casting hands, then Fireball's missile and impact, duplicated
-- with the tint #40FF60 (green) at size x1.5. The combat log lines ("Your __S1NAME__ hits Mottled Boar for N Shadow damage") are logged and
-- shown in the combat log tab. When the character does not know S1 (state 'before'), the stock Shadow Bolt (Rank 2) is cast instead: the
-- reference picture of the original visual.
local S1 = '__S1NAME__'
local EVENTS = { 'CHAT_MSG_SPELL_SELF_DAMAGE', 'CHAT_MSG_COMBAT_HOSTILE_DEATH', 'CHAT_MSG_SPELL_FAILED_LOCALPLAYER', 'UI_ERROR_MESSAGE',
                 'SPELLCAST_START', 'SPELLCAST_STOP', 'SPELLCAST_FAILED', 'SPELLCAST_INTERRUPTED' }
local spell = { idx = nil, name = nil }
local function live() return UnitExists('target') and UnitName('target') == 'Mottled Boar' and not UnitIsDead('target') end

SC_Step(18, 'settle', function()
  VT_Facts(); VT_UI(true); VT_Events(EVENTS)
  spell.idx = VT_SpellFacts(S1)
  if spell.idx then spell.name = S1 else spell.idx = VT_FindSpell('Shadow Bolt'); spell.name = 'Shadow Bolt'; SC_Note('S1 not known: casting the stock Shadow Bolt (the reference visual), index ' .. tostring(spell.idx)) end
end)
SC_Step(1.5, 'wide', function() VT_UI(false); VT_Shot('c1_wide') end)
for k = 1, 3 do
  local kk = k
  SC_Step(1.2, 'target ' .. kk, function() VT_Events(EVENTS); ClearTarget(); SC_Hw('TargetNearestEnemy()') end)
  SC_Step(1.2, 'retarget ' .. kk, function() VT_Target(); if not live() then SC_Hw('TargetNearestEnemy()') end end)
  SC_Step(1.0, 'cast ' .. kk, function()
    VT_Target()
    if not live() then SC_Note('try ' .. kk .. ': no live boar targeted'); return end
    if not spell.idx then SC_Note('no spell to cast'); return end
    VT_UI(false)
    VT_Burst('cast' .. kk, 5)
    SC_Hw("CastSpell(" .. spell.idx .. ", 'spell')")
  end)
  SC_Step(1.2, 'casting ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_a') end)
  SC_Step(1.2, 'flight ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_b') end)
  SC_Step(1.2, 'impact ' .. kk, function() VT_Shot('c1_cast' .. kk .. '_c') end)
  SC_Step(2.0, 'result ' .. kk, function()
    if not UnitExists('target') then TargetLastTarget() end
    SC_Note('after cast ' .. kk .. ' (' .. tostring(spell.name) .. '): target ' .. tostring(UnitName('target')) .. ' dead ' .. tostring(UnitIsDead('target')) .. ' hp ' .. tostring(UnitHealth('target')))
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
