-- vt_lib.lua: helpers of the WoW Data Studio in-game visual test (appended after the harness's _lib.lua by tests\ingame\session.ps1).
-- Lua 5.0 (WoW 1.12): no '#', table.getn, string.gfind.
function VT_Shot(name) AT_Log('shot ' .. name); Screenshot() end          -- session.ps1 pairs the k-th 'shot' line with the k-th file
function VT_Burst(name, secs) AT_Log('burst ' .. name .. ' ' .. secs) end  -- session.ps1 captures the window with wowshot for SECS seconds
function VT_UI(show) if show then UIParent:Show() else UIParent:Hide() end end
function VT_Facts()
  local x, y = GetPlayerMapPosition('player')
  SC_Note('zone ' .. tostring(GetRealZoneText()) .. ' / ' .. tostring(GetSubZoneText()) .. ' map pos ' .. string.format('%.4f %.4f', x or -1, y or -1) ..
          ' game time ' .. tostring(GetGameTime()))
end
function VT_Target()
  if UnitExists('target') then
    SC_Note('target ' .. tostring(UnitName('target')) .. ' dead=' .. tostring(UnitIsDead('target')) .. ' hp=' .. tostring(UnitHealth('target')))
  else SC_Note('no target') end
end
-- 2026-10-15 (designers test, tests\ingame\lua\designers): shared helpers. Nothing above changes, so the older scenarios behave as before.
function VT_Shown(name) local f = getglobal(name); return (f and f:IsVisible()) and true or false end
function VT_TipLines(tag)
  local t = {}
  for i = 1, 20 do
    local l = getglobal('GameTooltipTextLeft' .. i)
    local r = getglobal('GameTooltipTextRight' .. i)
    if l and l:IsVisible() and l:GetText() then
      local s = l:GetText()
      if r and r:IsVisible() and r:GetText() then s = s .. ' | ' .. r:GetText() end
      table.insert(t, s)
    end
  end
  SC_Note('tooltip ' .. tag .. ' (' .. table.getn(t) .. ' lines): ' .. table.concat(t, ' / '))
end
function VT_TipAt(x, y) GameTooltip:SetOwner(UIParent, 'ANCHOR_NONE'); GameTooltip:ClearAllPoints(); GameTooltip:SetPoint('TOPLEFT', UIParent, 'TOPLEFT', x or 820, y or -200) end
-- every message of these events becomes a 'scenario note: event ...' line (a global frame: made again after a re-created Lua state)
function VT_Events(list)
  if VTEventFrame then return end
  VTEventFrame = CreateFrame('Frame', 'VTEventFrame')
  for i = 1, table.getn(list) do VTEventFrame:RegisterEvent(list[i]) end
  VTEventFrame:SetScript('OnEvent', function()
    SC_Note('event ' .. event .. ': ' .. tostring(arg1) .. (arg2 and arg2 ~= '' and (' | ' .. tostring(arg2)) or ''))
  end)
end
-- the spell book: index of a spell by name (nil when the character does not know it), and its tab
function VT_FindSpell(name)
  local i = 1
  while true do
    local n, r = GetSpellName(i, 'spell')
    if not n then return nil end
    if n == name then return i, r end
    i = i + 1
  end
end
function VT_SpellTab(idx)
  for t = 1, GetNumSpellTabs() do
    local name, tex, off, num = GetSpellTabInfo(t)
    if idx > off and idx <= off + num then return t, name, off end
  end
end
function VT_SpellFacts(name)
  local idx, rank = VT_FindSpell(name)
  if not idx then SC_Note('spell book: ' .. name .. ' NOT known'); return nil end
  local t, tname = VT_SpellTab(idx)
  SC_Note('spell book: ' .. name .. ' (' .. tostring(rank) .. ') at index ' .. idx .. ' in tab ' .. tostring(t) .. ' ' .. tostring(tname) .. ', icon ' .. tostring(GetSpellTexture(idx, 'spell')))
  return idx
end
-- open the spell book at the spell's tab and page (best effort: the 1.12 SpellBookFrame keeps a page per tab, 12 spells a page)
function VT_ShowSpellbook(idx)
  if not VT_Shown('SpellBookFrame') then ToggleSpellBook('spell') end
  local t, tname, off = VT_SpellTab(idx)
  if not t then return end
  local tab = getglobal('SpellBookSkillLineTab' .. t)
  if tab then tab:Click() end
  if SPELLBOOK_PAGENUMBERS then SPELLBOOK_PAGENUMBERS[t] = math.floor((idx - off - 1) / 12) + 1 end
  if SpellBookFrame_Update then SpellBookFrame_Update() end
end
-- big icons of textures (the in-game proof of an icon), in a black box in the middle of the screen
function VT_Icons(list)
  if VTIconBox then VTIconBox:Hide() end
  local f = CreateFrame('Frame', 'VTIconBox', UIParent)
  f:SetWidth(20 + 150 * table.getn(list)); f:SetHeight(150); f:SetPoint('CENTER', UIParent, 'CENTER', 0, 140)
  local bg = f:CreateTexture(nil, 'BACKGROUND'); bg:SetAllPoints(f); bg:SetTexture(0, 0, 0, 0.8)
  for k = 1, table.getn(list) do
    local t = f:CreateTexture(nil, 'ARTWORK')
    t:SetWidth(128); t:SetHeight(128); t:SetPoint('LEFT', f, 'LEFT', 14 + (k - 1) * 150, 0)
    if list[k] then t:SetTexture(list[k]) else t:SetTexture(0.3, 0, 0, 1) end
  end
  f:Show()
end
