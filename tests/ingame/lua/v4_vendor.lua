-- v4_vendor.lua (0.9.0 test, items S1-S3): at the test character's start point (Valley of Trials), facing north, camera 6 yd, UI shown.
-- S2: the new NPC 900000 "WDS Test Vendor" <WoW Data Studio> stands 3.5 yd north (Duokna's look, display 1879): targeted by name (the
--     target frame shows the name), pictures with and without the UI, then the vendor window (AT_INTERACT: the game's right-click).
-- S1: the vendor lists item 900000 "WDS Test Blade" (epic, +25 Str, +20 Sta, 30-55 damage, Thunderfury's icon INV_Sword_39).
-- S3: item 900001 "WDS Test Draught" uses the new spell 900000: its tooltip line "Use: Restores 900 to 1000 health." comes from the
--     game's Spell.dbc row 900000 that "Make the game match the server" added (missing when only the server has the spell).
-- Every fact is also logged as text (scenario note lines in wowcode.log): target name, merchant items with their icons, tooltip lines.
local VENDOR = 'WDS Test Vendor'
local function tipLines(tag)
  local t = {}
  for i = 1, 12 do
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
local function itemFacts(id)
  local name, link, quality, minLevel, itype, isub, stack, equip, tex = GetItemInfo(id)
  SC_Note('GetItemInfo(' .. id .. '): ' .. tostring(name) .. ' quality ' .. tostring(quality) .. ' type ' .. tostring(itype) .. '/' .. tostring(isub) ..
          ' equip ' .. tostring(equip) .. ' icon ' .. tostring(tex))
  return tex
end
local function showTip(how, arg)
  GameTooltip:SetOwner(UIParent, 'ANCHOR_NONE')
  GameTooltip:ClearAllPoints()
  GameTooltip:SetPoint('TOPLEFT', UIParent, 'TOPLEFT', 820, -200)
  if how == 'merchant' then GameTooltip:SetMerchantItem(arg) else GameTooltip:SetHyperlink(arg) end
  GameTooltip:Show()
end

SC_Step(18, 'settle', function() VT_Facts(); VT_UI(true) end)
-- the server answers item queries asynchronously: ask first, look later
SC_Step(1.0, 'item queries', function() itemFacts(900000); itemFacts(900001)
  GameTooltip:SetOwner(UIParent, 'ANCHOR_NONE'); GameTooltip:SetHyperlink('item:900000:0:0:0'); GameTooltip:SetHyperlink('item:900001:0:0:0'); GameTooltip:Hide() end)
SC_Step(1.5, 'target the vendor', function() SC_Hw('TargetByName("' .. VENDOR .. '")') end)
SC_Step(1.5, 'vendor ui', function()
  VT_Target()
  if UnitExists('target') then SC_Note('target name ' .. tostring(UnitName('target')) .. ' level ' .. tostring(UnitLevel('target')) .. ' reaction ' .. tostring(UnitReaction('player', 'target'))) end
  VT_Shot('v1_vendor_ui')
end)
SC_Step(1.5, 'vendor clean', function() VT_UI(false); VT_Shot('v1_vendor_clean') end)
SC_Step(1.3, 'side', function() FlipCameraYaw(50) end)
SC_Step(1.5, 'vendor side', function() VT_Shot('v1_vendor_side') end)
SC_Step(1.3, 'back', function() FlipCameraYaw(-50); VT_UI(true) end)
SC_Step(1.0, 'interact', function()
  if UnitExists('target') and UnitName('target') == VENDOR then AT_INTERACT = 1 else SC_Note('no vendor targeted: no interaction') end
end)
SC_Step(3.0, 'merchant window', function()
  local shown = MerchantFrame and MerchantFrame:IsVisible()
  SC_Note('merchant window shown: ' .. tostring(shown))
  if shown then
    local n = GetMerchantNumItems() or 0
    for i = 1, n do
      local name, tex, price, qty, avail, usable = GetMerchantItemInfo(i)
      SC_Note('merchant item ' .. i .. ': ' .. tostring(name) .. ' icon ' .. tostring(tex) .. ' price ' .. tostring(price) .. ' usable ' .. tostring(usable))
    end
  end
  VT_Shot('v1_merchant')
end)
SC_Step(1.5, 'tooltip blade', function()
  if MerchantFrame and MerchantFrame:IsVisible() then showTip('merchant', 1) else showTip('link', 'item:900000:0:0:0') end
end)
SC_Step(1.2, 'tooltip blade shot', function() tipLines('blade'); VT_Shot('v1_tip_blade') end)
SC_Step(1.5, 'tooltip draught', function()
  if MerchantFrame and MerchantFrame:IsVisible() then showTip('merchant', 2) else showTip('link', 'item:900001:0:0:0') end
end)
SC_Step(1.2, 'tooltip draught shot', function() tipLines('draught'); VT_Shot('v1_tip_draught') end)
SC_Step(1.2, 'close', function() GameTooltip:Hide(); if MerchantFrame and MerchantFrame:IsVisible() then CloseMerchant() end end)
-- the item links on their own (also when there is no vendor), and the two icons large
SC_Step(1.5, 'link blade', function() showTip('link', 'item:900000:0:0:0') end)
SC_Step(1.2, 'link blade shot', function() tipLines('link blade'); VT_Shot('v1_link_blade') end)
SC_Step(1.5, 'link draught', function() showTip('link', 'item:900001:0:0:0') end)
SC_Step(1.2, 'link draught shot', function() tipLines('link draught'); VT_Shot('v1_link_draught') end)
SC_Step(1.2, 'icons', function()
  GameTooltip:Hide()
  local f = CreateFrame('Frame', 'VTIcons', UIParent)
  f:SetWidth(300); f:SetHeight(150); f:SetPoint('CENTER', UIParent, 'CENTER', 0, 120)
  local bg = f:CreateTexture(nil, 'BACKGROUND'); bg:SetAllPoints(f); bg:SetTexture(0, 0, 0, 0.8)
  for k, id in ipairs({ 900000, 900001 }) do
    local t = f:CreateTexture(nil, 'ARTWORK')
    t:SetWidth(128); t:SetHeight(128); t:SetPoint('LEFT', f, 'LEFT', 14 + (k - 1) * 150, 0)
    local tex = itemFacts(id)
    if tex then t:SetTexture(tex) else t:SetTexture(0.3, 0, 0, 1) end
  end
  f:Show()
end)
SC_Step(1.5, 'icons shot', function() VT_Shot('v1_icons') end)
SC_Step(1.0, 'end', function() if VTIcons then VTIcons:Hide() end; ClearTarget(); VT_UI(true) end)
SC_Start()
