-- d_trainer.lua (designers test, S1 taught by a trainer): at the hunting spot P; the test trainer "__TRAINER__" (a copy of Nartok, its own
-- trainer list) stands 3.5 yd east. The spell designer's "Add to trainer" made the learn spell __LEARN__ (Learn spell -> __S1__) and the
-- npc_trainer row (level 1, __COST__ copper). In game: the trainer window lists "__S1NAME__" with its icon, cost and description (the $s1
-- numbers filled in); it is bought; the spell book has it, with its icon; its tooltip shows the damage. The character starts from the
-- snapshot of the run's start (the run script restores it before this session), so the spell is not known yet.
local TRAINER, S1 = '__TRAINER__', '__S1NAME__'
local EVENTS = { 'CHAT_MSG_SYSTEM', 'UI_ERROR_MESSAGE', 'UI_INFO_MESSAGE', 'CHAT_MSG_MONEY', 'LEARNED_SPELL_IN_TAB', 'TRAINER_SHOW', 'TRAINER_CLOSED' }
local before = {}
local function services(tag)
  if not GetNumTrainerServices then SC_Note(tag .. ': no trainer API'); return nil end
  local found = nil
  for i = 1, GetNumTrainerServices() do
    local name, rank, category = GetTrainerServiceInfo(i)
    if category ~= 'header' then
      SC_Note(tag .. ' service ' .. i .. ': ' .. tostring(name) .. ' (' .. tostring(rank) .. ') ' .. tostring(category) .. ', icon ' .. tostring(GetTrainerServiceIcon(i)) ..
              ', cost ' .. tostring(GetTrainerServiceCost(i)) .. ', level ' .. tostring(GetTrainerServiceLevelReq(i)) ..
              ', description [' .. tostring(GetTrainerServiceDescription and GetTrainerServiceDescription(i)) .. ']')
      if name == S1 then found = i end
    end
  end
  SC_Note(tag .. ': ' .. GetNumTrainerServices() .. ' line(s), ' .. S1 .. ' at ' .. tostring(found))
  return found
end

SC_Step(18, 'settle', function()
  VT_Facts(); VT_UI(true); VT_Events(EVENTS)
  before.money = GetMoney(); before.spell = VT_FindSpell(S1)
  SC_Note('before: money ' .. before.money .. ', ' .. S1 .. ' known ' .. tostring(before.spell ~= nil))
end)
SC_Step(1.5, 'target the trainer', function()
  VT_Events(EVENTS); ClearTarget(); TargetByName(TRAINER)
  SC_Note('target trainer: ' .. tostring(UnitExists('target') and UnitName('target') == TRAINER) .. ' (' .. tostring(UnitName('target')) .. ')')
end)
SC_Step(1.5, 'trainer shot', function() VT_Target(); VT_Shot('t1_trainer_npc') end)
SC_Step(1.0, 'interact', function() if UnitExists('target') and UnitName('target') == TRAINER then AT_INTERACT = 1 else SC_Note('no trainer: nothing to talk to') end end)
SC_Step(2.5, 'gossip', function()
  SC_Note('gossip ' .. tostring(VT_Shown('GossipFrame')) .. ', trainer window ' .. tostring(VT_Shown('ClassTrainerFrame')))
  if VT_Shown('GossipFrame') then
    local o = { GetGossipOptions() }
    SC_Note('gossip options: ' .. table.concat(o, ', '))
    for k = 1, table.getn(o), 2 do if o[k + 1] == 'trainer' then SelectGossipOption((k + 1) / 2); break end end
  end
end)
SC_Step(2.5, 'trainer window', function()
  SC_Note('trainer window ' .. tostring(VT_Shown('ClassTrainerFrame')))
  if SetTrainerServiceTypeFilter then SetTrainerServiceTypeFilter('available', 1); SetTrainerServiceTypeFilter('unavailable', 1); SetTrainerServiceTypeFilter('used', 1) end
end)
SC_Step(1.5, 'select S1', function()
  local i = services('trainer')
  before.service = i
  if i then if ClassTrainer_SetSelection then ClassTrainer_SetSelection(i) else SelectTrainerService(i) end end
end)
SC_Step(1.5, 'trainer shot', function() VT_Shot('t1_trainer_window') end)
SC_Step(1.0, 'buy', function()
  local i = before.service
  if i then
    local name, rank, category = GetTrainerServiceInfo(i)
    SC_Note('buying ' .. tostring(name) .. ' (' .. tostring(category) .. ')')
    if category == 'available' then BuyTrainerService(i) end
  end
end)
SC_Step(2.5, 'after buying', function()
  local i = services('after buying')
  if i then if ClassTrainer_SetSelection then ClassTrainer_SetSelection(i) else SelectTrainerService(i) end end
  SC_Note('money ' .. GetMoney() .. ' (' .. (GetMoney() - (before.money or 0)) .. ')')
end)
SC_Step(1.5, 'bought shot', function() VT_Shot('t1_trainer_bought') end)
SC_Step(1.0, 'close', function() if CloseTrainer then CloseTrainer() end; CloseGossip() end)
SC_Step(1.5, 'spell book', function()
  local idx = VT_SpellFacts(S1)
  SC_Note(S1 .. ' learned at the trainer: ' .. tostring(idx ~= nil and before.spell == nil))
  if idx then VT_ShowSpellbook(idx) end
end)
SC_Step(1.5, 'tooltip', function()
  local idx = VT_FindSpell(S1)
  if idx then VT_TipAt(); GameTooltip:SetSpell(idx, 'spell'); GameTooltip:Show(); VT_TipLines('S1 spell book') end
end)
SC_Step(1.2, 'spell book shot', function() VT_Shot('t1_spellbook') end)
SC_Step(1.2, 'icon', function()
  GameTooltip:Hide(); if VT_Shown('SpellBookFrame') then ToggleSpellBook('spell') end
  local idx = VT_FindSpell(S1)
  VT_Icons({ idx and GetSpellTexture(idx, 'spell') or nil })
end)
SC_Step(1.5, 'icon shot', function() VT_Shot('t1_s1_icon') end)
SC_Step(1.0, 'end', function() if VTIconBox then VTIconBox:Hide() end; ClearTarget(); VT_UI(true) end)
SC_Start()
