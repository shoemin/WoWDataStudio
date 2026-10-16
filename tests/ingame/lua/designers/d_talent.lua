-- d_talent.lua (designers test, T1): the talent frame on the Warlock's Destruction tree. The talent designer added "__T1NAME__"
-- (talent __T1TALENT__, rank spell __T1SPELL__: mana cost of Destruction spells -7 %, icon Spell_Nature_StarFall) in the empty place of the
-- first row, first column (tier 1, column 1 as the game's API counts), and moved Shadowburn from tier 3 column 4 to tier 2 column 4.
-- In game: the grid, both tooltips, and every talent's place logged (GetTalentInfo). In 'before' the stock tree (the reference).
-- Learning the new talent is not tried: the server reads its own Talent.dbc (not changed by this test), so it does not know the talent.
local T1 = '__T1NAME__'
local tab = nil
local function talents()
  if not tab then return {} end
  local res = {}
  for i = 1, GetNumTalents(tab) do
    local name, icon, tier, column, rank, maxRank = GetTalentInfo(tab, i)
    res[name or ('#' .. i)] = i
    SC_Note('talent ' .. i .. ': ' .. tostring(name) .. ' tier ' .. tostring(tier) .. ' column ' .. tostring(column) .. ' rank ' .. tostring(rank) .. '/' .. tostring(maxRank) .. ' icon ' .. tostring(icon))
  end
  return res
end

SC_Step(18, 'settle', function() VT_Facts(); VT_UI(true) end)
SC_Step(1.5, 'open the talent frame', function() ToggleTalentFrame() end)
SC_Step(1.5, 'Destruction', function()
  SC_Note('talent frame shown ' .. tostring(VT_Shown('TalentFrame')) .. ', tabs ' .. tostring(GetNumTalentTabs()))
  for t = 1, GetNumTalentTabs() do
    local name, icon, spent = GetTalentTabInfo(t)
    SC_Note('tab ' .. t .. ': ' .. tostring(name) .. ' (' .. tostring(spent) .. ' points)')
    if name == 'Destruction' then tab = t end
  end
  if tab then
    local b = getglobal('TalentFrameTab' .. tab)
    if b then b:Click() end
    if PanelTemplates_SetTab and TalentFrame then PanelTemplates_SetTab(TalentFrame, tab) end
    if TalentFrame_Update then TalentFrame_Update() end
  end
end)
SC_Step(1.5, 'tree', function()
  local l = talents()
  SC_Note('the new talent ' .. T1 .. ' at ' .. tostring(l[T1]) .. ', Shadowburn at ' .. tostring(l['Shadowburn']))
  VT_Shot('k1_tree')
end)
SC_Step(1.2, 'new talent tooltip', function()
  local l = talents()
  if l[T1] then
    local btn = getglobal('TalentFrameTalent' .. l[T1])
    GameTooltip:SetOwner(btn or UIParent, 'ANCHOR_RIGHT'); GameTooltip:SetTalent(tab, l[T1]); GameTooltip:Show(); VT_TipLines('new talent')
  else SC_Note('no ' .. T1 .. ' in the tree') end
end)
SC_Step(1.5, 'new talent shot', function() VT_Shot('k1_tip_new') end)
SC_Step(1.2, 'Shadowburn tooltip', function()
  GameTooltip:Hide()
  local l = talents()
  if l['Shadowburn'] then
    local btn = getglobal('TalentFrameTalent' .. l['Shadowburn'])
    GameTooltip:SetOwner(btn or UIParent, 'ANCHOR_RIGHT'); GameTooltip:SetTalent(tab, l['Shadowburn']); GameTooltip:Show(); VT_TipLines('Shadowburn')
  end
end)
SC_Step(1.5, 'Shadowburn shot', function() VT_Shot('k1_tip_moved') end)
SC_Step(1.0, 'end', function() GameTooltip:Hide(); if VT_Shown('TalentFrame') then ToggleTalentFrame() end; VT_UI(true) end)
SC_Start()
