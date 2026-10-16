-- d_quest.lua (designers test, Q1 + the S1 reward): at the hunting spot P (Valley of Trials, facing north), the new quest giver
-- "__GIVER__" stands 3.5 yd west and the test trainer 3.5 yd east. The chain made with the quest-chain designer:
--   __QATITLE__ (__QA__): kill 2 Mottled Boars, 1s  ->  turn in  ->  __QBTITLE__ (__QB__), offered next in chain:
--   no objective, reward: learn __S1NAME__ (__S1__, through the learn spell __LEARN__) and __ITEMCOUNT__ x item __ITEM__.
-- In game: the giver's '!', the detail texts with $N / $c / $B filled in, the quest log text, the kill tracking (Immolate: Statclone's own
-- spell kills a level 1-2 boar at once; S1 is not known yet at this point), the turn-in, the follow-up offered, the rewards received.
-- Every fact is logged (scenario note lines); pictures with the UI shown. In 'before' and 'afterundo' the giver must be missing.
-- GM fallback (only if the kills do not count after every try): '.quest complete __QA__' typed into the chat (the test account is a GM on this
-- realm); the log says 'GM FALLBACK' when it was used.
local GIVER, QA, QB, S1 = '__GIVER__', '__QATITLE__', '__QBTITLE__', '__S1NAME__'
local EVENTS = { 'CHAT_MSG_SYSTEM', 'UI_INFO_MESSAGE', 'UI_ERROR_MESSAGE', 'CHAT_MSG_LOOT', 'CHAT_MSG_MONEY', 'LEARNED_SPELL_IN_TAB',
                 'CHAT_MSG_COMBAT_HOSTILE_DEATH', 'CHAT_MSG_SPELL_SELF_DAMAGE', 'SPELLCAST_FAILED' }
local function shown(n) return VT_Shown(n) end
local function ui() return 'gossip ' .. tostring(shown('GossipFrame')) .. ', greeting ' .. tostring(shown('QuestFrameGreetingPanel')) .. ', detail ' ..
  tostring(shown('QuestFrameDetailPanel')) .. ', progress ' .. tostring(shown('QuestFrameProgressPanel')) .. ', reward ' .. tostring(shown('QuestFrameRewardPanel')) end
local function logIndex(title)
  for i = 1, GetNumQuestLogEntries() do
    local t, level, tag, header, collapsed, complete = GetQuestLogTitle(i)
    if not header and t == title then return i, complete end
  end
end
local function board(title)
  local i, complete = logIndex(title)
  if not i then return 'not in the quest log', false end
  SelectQuestLogEntry(i)
  local s, done = {}, true
  for j = 1, GetNumQuestLeaderBoards() do
    local text, typ, finished = GetQuestLogLeaderBoard(j)
    table.insert(s, tostring(text) .. (finished and ' (done)' or ''))
    if not finished then done = false end
  end
  return table.concat(s, '; ') .. ' complete=' .. tostring(complete), done or complete == 1
end
local function targetGiver()
  ClearTarget(); TargetByName(GIVER)
  local ok = UnitExists('target') and UnitName('target') == GIVER
  SC_Note('target giver: ' .. tostring(ok) .. ' (' .. tostring(UnitName('target')) .. ')')
  return ok
end
-- pick a quest by title in whatever window the NPC opened (gossip or quest greeting); active = to hand in
local function pick(title, active)
  if shown('GossipFrame') then
    local q = { (active and GetGossipActiveQuests or GetGossipAvailableQuests)() }
    for k = 1, table.getn(q), 2 do
      if q[k] == title then if active then SelectGossipActiveQuest((k + 1) / 2) else SelectGossipAvailableQuest((k + 1) / 2) end; return 'gossip ' .. (k + 1) / 2 end
    end
    return 'not in the gossip list (' .. table.concat(q, ', ') .. ')'
  elseif shown('QuestFrameGreetingPanel') then
    local n = active and GetNumActiveQuests() or GetNumAvailableQuests()
    for k = 1, n do
      local t = active and GetActiveTitle(k) or GetAvailableTitle(k)
      if t == title then if active then SelectActiveQuest(k) else SelectAvailableQuest(k) end; return 'greeting ' .. k end
    end
    return 'not in the greeting list'
  end
  return 'no list shown (' .. ui() .. ')'
end
local function detailFacts(tag)
  SC_Note(tag .. ' detail: title [' .. tostring(GetTitleText()) .. '] text [' .. tostring(GetQuestText()) .. '] objectives [' .. tostring(GetObjectiveText()) .. ']')
  local rs = { GetRewardSpell and GetRewardSpell() }
  SC_Note(tag .. ' rewards: money ' .. tostring(GetRewardMoney()) .. ', items ' .. tostring(GetNumQuestRewards()) .. ', choices ' .. tostring(GetNumQuestChoices()) ..
          ', spell ' .. tostring(rs[2]) .. ' icon ' .. tostring(rs[1]))
  for k = 1, (GetNumQuestRewards() or 0) do local n, tex, c = GetQuestItemInfo('reward', k); SC_Note(tag .. ' reward item ' .. k .. ': ' .. tostring(n) .. ' x' .. tostring(c) .. ' icon ' .. tostring(tex)) end
end
local before = {}

SC_Step(18, 'settle', function()
  VT_Facts(); VT_UI(true); VT_Events(EVENTS)
  before.money = GetMoney(); before.items = GetItemCount(__ITEM__); before.spell = VT_FindSpell(S1)
  SC_Note('before: money ' .. before.money .. ', item __ITEM__ x' .. before.items .. ', ' .. S1 .. ' known ' .. tostring(before.spell ~= nil) ..
          ', ' .. QA .. ' in log ' .. tostring(logIndex(QA) ~= nil))
end)
SC_Step(1.5, 'target the giver', function() VT_Events(EVENTS); targetGiver() end)
SC_Step(1.5, 'giver with !', function() VT_Target(); VT_Shot('q1_giver_ui') end)
SC_Step(1.2, 'giver clean', function() VT_UI(false); VT_Shot('q1_giver_clean') end)
SC_Step(1.0, 'interact', function() VT_UI(true); if UnitExists('target') and UnitName('target') == GIVER then AT_INTERACT = 1 else SC_Note('no giver: nothing to talk to') end end)
SC_Step(2.5, 'open quest A', function() SC_Note('window: ' .. ui()); if not shown('QuestFrameDetailPanel') then SC_Note('pick A: ' .. pick(QA, false)) end end)
SC_Step(2.0, 'quest A detail', function() SC_Note('window: ' .. ui()); if shown('QuestFrameDetailPanel') then detailFacts('A'); VT_Shot('q1_A_detail') else VT_Shot('q1_A_nodetail') end end)
SC_Step(1.2, 'accept A', function() if shown('QuestFrameDetailPanel') and GetTitleText() == QA then AcceptQuest() end end)
SC_Step(2.0, 'quest log A', function()
  local i = logIndex(QA)
  SC_Note('A in the quest log: ' .. tostring(i))
  if i then
    SelectQuestLogEntry(i)
    local desc, obj = GetQuestLogQuestText()
    SC_Note('A log text: description [' .. tostring(desc) .. '] objectives [' .. tostring(obj) .. ']')
    SC_Note('A board: ' .. board(QA))
    ShowUIPanel(QuestLogFrame); if QuestLog_SetSelection then QuestLog_SetSelection(i) end
    AddQuestWatch(i); if QuestWatch_Update then QuestWatch_Update() end
  end
end)
SC_Step(1.5, 'quest log A shot', function() VT_Shot('q1_A_log') end)
SC_Step(1.0, 'close log', function() HideUIPanel(QuestLogFrame); CloseQuest(); CloseGossip() end)
-- the kills: up to 8 tries (a target behind the character, or one already dead, makes a try fail), each: target, Immolate, look
for k = 1, 8 do
  local kk = k
  SC_Step(1.2, 'target ' .. kk, function()
    local _, done = board(QA)
    if done or not logIndex(QA) then return end
    SC_Hw('TargetNearestEnemy()')
  end)
  SC_Step(1.0, 'immolate ' .. kk, function()
    local _, done = board(QA)
    if done or not logIndex(QA) then return end
    VT_Target()
    if UnitExists('target') and UnitName('target') == 'Mottled Boar' and not UnitIsDead('target') then SC_Hw("CastSpellByName('Immolate')") end
  end)
  SC_Step(3.2, 'look ' .. kk, function()
    if not logIndex(QA) then return end
    local b, done = board(QA)
    SC_Note('A board after try ' .. kk .. ': ' .. b)
    if kk == 1 or kk == 2 or done then VT_Shot('q1_A_kill_' .. kk) end
  end)
end
SC_Step(1.0, 'kills done?', function()
  if not logIndex(QA) then return end
  local b, done = board(QA)
  SC_Note('A board: ' .. b)
  if not done then SC_Note('GM FALLBACK: .quest complete __QA__'); SendChatMessage('.quest complete __QA__', 'SAY') end
end)
SC_Step(2.0, 'tracked', function() if logIndex(QA) then SC_Note('A board: ' .. board(QA)); VT_Shot('q1_A_tracked') end end)
-- turn in A at the giver
SC_Step(1.0, 'giver again', function() ClearTarget(); targetGiver() end)
SC_Step(1.0, 'interact (turn in)', function() if UnitName('target') == GIVER then AT_INTERACT = 1 end end)
SC_Step(2.5, 'select A', function() SC_Note('window: ' .. ui()); if not (shown('QuestFrameProgressPanel') or shown('QuestFrameRewardPanel')) then SC_Note('pick A (active): ' .. pick(QA, true)) end end)
SC_Step(2.0, 'progress A', function()
  SC_Note('window: ' .. ui())
  if shown('QuestFrameProgressPanel') then SC_Note('A progress text [' .. tostring(GetProgressText()) .. '] completable ' .. tostring(IsQuestCompletable())); VT_Shot('q1_A_progress'); CompleteQuest() end
end)
SC_Step(2.0, 'reward A', function()
  SC_Note('window: ' .. ui())
  if shown('QuestFrameRewardPanel') then SC_Note('A reward text [' .. tostring(GetRewardText()) .. '] money ' .. tostring(GetRewardMoney())); VT_Shot('q1_A_reward') end
end)
SC_Step(1.0, 'hand in A', function() if shown('QuestFrameRewardPanel') then GetQuestReward(0) end end)
-- the follow-up: offered at once (next in chain)
SC_Step(2.5, 'follow-up offered', function()
  SC_Note('window after the turn-in: ' .. ui() .. ', title ' .. tostring(GetTitleText()))
  if shown('QuestFrameDetailPanel') then detailFacts('B'); VT_Shot('q1_B_offered')
  else SC_Note('the follow-up was NOT offered at once: asking the giver'); VT_Shot('q1_B_notoffered'); if targetGiver() then AT_INTERACT = 1 end end
end)
SC_Step(2.0, 'B detail', function()
  if not shown('QuestFrameDetailPanel') then SC_Note('pick B: ' .. pick(QB, false)) end
end)
SC_Step(1.5, 'accept B', function() SC_Note('window: ' .. ui()); if shown('QuestFrameDetailPanel') and GetTitleText() == QB then AcceptQuest() end end)
SC_Step(2.0, 'B in the log', function()
  local i = logIndex(QB)
  SC_Note('B in the quest log: ' .. tostring(i) .. (i and (', board: ' .. board(QB)) or ''))
  if i then SelectQuestLogEntry(i); local desc, obj = GetQuestLogQuestText(); SC_Note('B log text: description [' .. tostring(desc) .. '] objectives [' .. tostring(obj) .. ']') end
end)
-- hand in B: the reward window shows the spell to learn and the apples
SC_Step(1.0, 'giver for B', function() CloseQuest(); ClearTarget(); targetGiver() end)
SC_Step(1.0, 'interact (B)', function() if UnitName('target') == GIVER then AT_INTERACT = 1 end end)
SC_Step(2.5, 'select B', function() SC_Note('window: ' .. ui()); if not (shown('QuestFrameProgressPanel') or shown('QuestFrameRewardPanel')) then SC_Note('pick B (active): ' .. pick(QB, true)) end end)
SC_Step(2.0, 'progress B', function()
  SC_Note('window: ' .. ui())
  if shown('QuestFrameProgressPanel') then SC_Note('B progress text [' .. tostring(GetProgressText()) .. '] completable ' .. tostring(IsQuestCompletable())); CompleteQuest() end
end)
SC_Step(2.0, 'reward B', function()
  SC_Note('window: ' .. ui())
  if shown('QuestFrameRewardPanel') then SC_Note('B reward text [' .. tostring(GetRewardText()) .. ']'); detailFacts('B reward'); VT_Shot('q1_B_reward') end
end)
SC_Step(1.0, 'hand in B', function() if shown('QuestFrameRewardPanel') then GetQuestReward(0) end end)
-- what the character got
SC_Step(3.0, 'rewards', function()
  CloseQuest(); CloseGossip()
  local idx = VT_SpellFacts(S1)
  SC_Note('after: money ' .. GetMoney() .. ' (' .. (GetMoney() - (before.money or 0)) .. '), item __ITEM__ x' .. GetItemCount(__ITEM__) .. ' (+' .. (GetItemCount(__ITEM__) - (before.items or 0)) ..
          '), ' .. S1 .. ' learned ' .. tostring(idx ~= nil and before.spell == nil) .. ', A in log ' .. tostring(logIndex(QA) ~= nil) .. ', B in log ' .. tostring(logIndex(QB) ~= nil))
  if idx then VT_ShowSpellbook(idx) end
end)
SC_Step(1.5, 'spell book', function()
  local idx = VT_FindSpell(S1)
  if idx then VT_TipAt(); GameTooltip:SetSpell(idx, 'spell'); GameTooltip:Show(); VT_TipLines('S1 spell book') end
  VT_Shot('q1_spellbook')
end)
SC_Step(1.2, 'icons', function()
  GameTooltip:Hide(); if VT_Shown('SpellBookFrame') then ToggleSpellBook('spell') end
  local idx = VT_FindSpell(S1)
  VT_Icons({ idx and GetSpellTexture(idx, 'spell') or nil })
end)
SC_Step(1.5, 'icons shot', function() VT_Shot('q1_s1_icon') end)
SC_Step(1.0, 'end', function() if VTIconBox then VTIconBox:Hide() end; ClearTarget(); VT_UI(true) end)
SC_Start()
