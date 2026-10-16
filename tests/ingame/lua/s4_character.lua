-- s4_character.lua: at the start point, camera 5 yd away, turned to the character's front; UI shown. #10: the action bar (Shadow Bolt on
-- button 2), the player frame and the target frame (the character targets itself: both frames use UI-TargetingFrame.blp); #11: the character
-- from the front; #8: the dressing room with the Acolyte's Robe (item 6129) tried on (Statclone wears nothing, so the robe is shown the way
-- the game previews items).
SC_Step(18, 'settle', function() VT_Facts(); VT_UI(true) end)
SC_Step(1.3, 'face the camera', function() FlipCameraYaw(180); TargetUnit('player') end)
-- the default UI positions of the player and target frames (an add-on's saved layout put them off screen at uiScale 1)
SC_Step(1, 'frames', function()
  PlayerFrame:ClearAllPoints(); PlayerFrame:SetPoint('TOPLEFT', UIParent, 'TOPLEFT', -19, -4); PlayerFrame:Show()
  TargetFrame:ClearAllPoints(); TargetFrame:SetPoint('TOPLEFT', UIParent, 'TOPLEFT', 250, -4)
  SC_Check(TargetFrame:IsVisible(), 'the target frame shows (target = self)')
end)
SC_Step(2, 'ui front', function() VT_UI(true); VT_Shot('s4_ui_front') end)
SC_Step(1.2, 'item query', function()
  GameTooltip:SetOwner(UIParent, 'ANCHOR_NONE'); GameTooltip:SetHyperlink('item:6129:0:0:0'); GameTooltip:Hide()
end)
SC_Step(3, 'dress up', function() DressUpItemLink('item:6129:0:0:0') end)
SC_Step(2.5, 'dressing room', function() SC_Check(DressUpFrame and DressUpFrame:IsVisible(), 'the dressing room is open'); VT_Shot('s4_dressup') end)
SC_Step(1.5, 'close', function() HideUIPanel(DressUpFrame); ClearTarget(); VT_UI(false) end)
SC_Step(1.5, 'front clean', function() VT_Shot('s4_front_clean') end)
SC_Step(1, 'end', function() VT_UI(true) end)
SC_Start()
