using System.Globalization;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>A finding of the quest checks: the quest it is about and the problem in plain words.</summary>
public sealed record QuestProblem(long Quest, string Title, DesignProblem Problem)
{
    public string Text => $"{Title} ({Quest}): {Problem.Text}";
}

/// <summary>
/// 0.13.0: the quest-chain checks, in plain words. What VMaNGOS does with the rows (ObjectMgr::LoadQuests,
/// Player::SatisfyQuestPreviousQuest / ExclusiveGroup / Breadcrumb, the quest relation loaders, CanInteractWithNPC,
/// TalkedToCreature) and what the 1.12 client can show (<see cref="QuestText"/>):
/// <list type="bullet">
/// <item>reachability: nobody offers the quest, a prerequisite is missing, cannot be done by the same players, is in
/// the same choice group, is a breadcrumb, or is itself unreachable; cycles of "must come first" links;</item>
/// <item>levels: gaps between a quest and the one before it, a minimum level above the quest's level, a maximum level
/// below what the chain needs, levels above 60;</item>
/// <item>NPC relations: no giver / no ender, a giver without the Quest giver flag, not spawned anywhere, a giver object
/// that is not a quest giver object;</item>
/// <item>objectives that cannot be completed: an item nothing gives or that drops only from creatures that are nowhere,
/// a creature or object that does not exist or is not spawned, an attackable target that is friendly, an "event"
/// quest nothing completes, counts of 0, unique items asked more than once;</item>
/// <item>factions: a giver, ender or "speak to" NPC hostile to the races that may take the quest, races and classes
/// that cannot be combined (no Horde Paladin, no Alliance Shaman);</item>
/// <item>text limits of the client (quest cache overflow, the quest window's cut, unknown $ tokens).</item>
/// </list>
/// </summary>
public static class QuestRules
{
    public const int LevelGap = 5;
    public const int MaxLevel = 60;

    /// <summary>Check quests (a chain); quests linked from outside the set are looked up too.</summary>
    public static List<QuestProblem> Check(QuestDesigner d, IReadOnlyCollection<long> ids)
    {
        var res = new List<QuestProblem>();
        var cache = new Dictionary<long, QuestSummary?>();
        QuestSummary? Sum(long id) => cache.TryGetValue(id, out var s) ? s : cache[id] = d.Exists(id) ? d.Summary(id) : null;
        var givers = new Dictionary<long, List<QuestRelation>>();
        List<QuestRelation> G(long id) => givers.TryGetValue(id, out var g) ? g : givers[id] = d.Givers(id);
        var unreachable = new Dictionary<long, string>();

        // reachability, in the order of the links (a quest after an unreachable one is unreachable too)
        var chain = QuestChains.Build(d, ids.Where(d.Exists).ToList(), ids.FirstOrDefault());
        foreach (var cyc in QuestChains.Cycles(chain.Nodes.Select(n => n.Id), chain.Edges))
            foreach (var q in cyc)
            {
                Add(q, DesignSeverity.Error, "chain", $"It is in a circle of quests that each need the other first ({string.Join(" -> ", cyc.Select(c => Sum(c)?.Title ?? "#" + c))} -> again): none of them can ever be taken.");
                unreachable.TryAdd(q, "it is in a circle");
            }
        foreach (var id in QuestChains.TopoOrder(chain.Nodes.Select(n => n.Id), chain.Edges))
        {
            var s = Sum(id);
            if (s == null) continue;
            CheckQuest(s);
        }
        return res;

        void Add(long quest, DesignSeverity sev, string field, string message) =>
            res.Add(new QuestProblem(quest, Sum(quest)?.Title ?? $"#{quest}", new DesignProblem(sev, field, message)));

        void CheckQuest(QuestSummary s)
        {
            long id = s.Id;
            var row = d.Require(id);
            var gv = G(id);
            var ev = d.Enders(id);
            bool autoComplete = s.Method == 0;
            // ---- reachability
            if (s.Method == 1) Add(id, DesignSeverity.Warning, "method", "Its Method is 1 (Disabled): VMaNGOS treats it as switched off.");
            bool offeredByChain = d.Ed.Rows(d.Quests, RowFilter.Eq("NextQuestInChain", id), 5).Count > 0;
            if (gv.Count == 0)
            {
                Add(id, DesignSeverity.Error, "giver", offeredByChain
                    ? "Nobody offers it: \"Next in chain\" points to it, but the NPC must also offer it (a quest giver row for the same NPC)."
                    : "Nobody offers it: no creature, object or item starts it. Add a quest giver.");
                unreachable.TryAdd(id, "nobody offers it");
            }
            var prevs = new List<long>();
            if (s.Prev != 0) prevs.Add(s.Prev);
            foreach (var r in d.Ed.Rows(d.Quests, RowFilter.Eq("NextQuestId", id).Or(new FilterTerm("NextQuestId", FilterOp.Eq, (-id).ToString(CultureInfo.InvariantCulture))), 50))
            {
                long from = d.Q(r, "entry");
                long sign = d.Q(r, "NextQuestId") < 0 ? -1 : 1;
                if (!prevs.Any(p => Math.Abs(p) == from)) prevs.Add(sign * from);
            }
            var reachablePrev = new List<long>();
            foreach (var p in prevs)
            {
                long pid = Math.Abs(p);
                var ps = Sum(pid);
                if (ps == null)
                {
                    Add(id, DesignSeverity.Error, "prev-quest", $"It needs quest {pid} first, which does not exist: it can never be taken.");
                    continue;
                }
                bool ok = true;
                if (ps.Breadcrumb != 0 && p > 0)
                {
                    Add(id, DesignSeverity.Error, "prev-quest", $"It needs the breadcrumb \"{ps.Title}\" done first; VMaNGOS refuses to unlock a quest with a breadcrumb (log: \"should not be unlocked by breadcrumb quest\").");
                    ok = false;
                }
                if (s.Group > 0 && ps.Group == s.Group)
                {
                    Add(id, DesignSeverity.Error, "exclusive-group", $"It needs \"{ps.Title}\" first, but both are in choice group {s.Group}: doing that one closes this one.");
                    ok = false;
                }
                if (!Overlap(s.Races, ps.Races))
                {
                    Add(id, DesignSeverity.Error, "races", $"It needs \"{ps.Title}\" first, which only {QuestForm.RaceText(ps.Races)} can take, but this quest is for {QuestForm.RaceText(s.Races)}: nobody can do both.");
                    ok = false;
                }
                if (!Overlap(s.Classes, ps.Classes))
                {
                    Add(id, DesignSeverity.Error, "classes", $"It needs \"{ps.Title}\" first, which only {QuestForm.ClassText(ps.Classes, d.Classes())} can take, but this quest is for {QuestForm.ClassText(s.Classes, d.Classes())}.");
                    ok = false;
                }
                if (s.MaxLevel > 0 && ps.MinLevel > s.MaxLevel)
                {
                    Add(id, DesignSeverity.Error, "max-level", $"Its maximum level {s.MaxLevel} is below the level needed for \"{ps.Title}\" ({ps.MinLevel}): a player who did that one is too high for this one.");
                    ok = false;
                }
                if (unreachable.TryGetValue(pid, out var why))
                {
                    Add(id, DesignSeverity.Error, "prev-quest", $"It needs \"{ps.Title}\" first, which nobody can do ({why}).");
                    ok = false;
                }
                if (ok) reachablePrev.Add(pid);
                // level gap after the quest before it
                if (s.MinLevel - ps.Level > LevelGap)
                    Add(id, DesignSeverity.Warning, "min-level", $"Level gap: it needs level {s.MinLevel}, {s.MinLevel - ps.Level} levels above \"{ps.Title}\" (level {ps.Level}). Players wait (or grind) between the two; lower the minimum level or add a step.");
                if (ps.Level - s.Level > LevelGap)
                    Add(id, DesignSeverity.Note, "level", $"It is {ps.Level - s.Level} levels below \"{ps.Title}\" before it: it will look grey (easy) to players who just did that one.");
            }
            if (prevs.Count > 0 && reachablePrev.Count == 0) unreachable.TryAdd(id, "what it needs first cannot be done");
            if (s.Group < 0)
            {
                // "all of the group": the quest they open needs every one
                var members = d.Ed.Rows(d.Quests, RowFilter.Eq("ExclusiveGroup", s.Group), 50).Select(r => d.Q(r, "entry")).Distinct().ToList();
                if (s.Next == 0) Add(id, DesignSeverity.Warning, "exclusive-group", $"It is in an \"all of\" group ({s.Group}) but opens no quest (Opens / NextQuestId): the group has no effect.");
                else if (members.Any(m => Sum(m)?.Next != s.Next)) Add(id, DesignSeverity.Warning, "exclusive-group", $"The quests of \"all of\" group {s.Group} should all open the same quest.");
            }
            if (s.Breadcrumb != 0)
            {
                if (Sum(s.Breadcrumb) == null) Add(id, DesignSeverity.Error, "breadcrumb-for", $"It is a breadcrumb for quest {s.Breadcrumb}, which does not exist (the server clears the link).");
                if (s.Next != 0) Add(id, DesignSeverity.Warning, "next-quest", "A breadcrumb should not open another quest (the server log says so).");
                if (s.Group != 0) Add(id, DesignSeverity.Warning, "exclusive-group", "A breadcrumb should not be in an exclusive group (the server log says so).");
            }
            if (s.NextInChain != 0 && Sum(s.NextInChain) == null) Add(id, DesignSeverity.Error, "next-in-chain", $"Next in chain is quest {s.NextInChain}, which does not exist: the server clears it and the chain stops here.");
            if (s.NextInChain != 0 && Sum(s.NextInChain) is { } nic)
            {
                var enderIds = ev.Where(e => e.Kind == "creature").Select(e => e.Id).ToHashSet();
                var nicGivers = G(nic.Id).Where(g => g.Kind == "creature").Select(g => g.Id).ToHashSet();
                if (enderIds.Count > 0 && nicGivers.Count > 0 && !enderIds.Overlaps(nicGivers))
                    Add(id, DesignSeverity.Warning, "next-in-chain", $"\"{nic.Title}\" is next in chain, but the NPC you hand this one to ({string.Join(", ", ev.Select(e => e.Name))}) does not offer it: the game shows no follow-up window.");
            }
            // ---- levels
            if (s.MinLevel > s.Level && s.Level > 0) Add(id, DesignSeverity.Warning, "min-level", $"Its minimum level ({s.MinLevel}) is above the quest's level ({s.Level}): it is grey before anyone can take it.");
            if (s.Level > MaxLevel || s.MinLevel > MaxLevel) Add(id, DesignSeverity.Error, "level", $"Level {Math.Max(s.Level, s.MinLevel)} is above {MaxLevel}, the highest player level of 1.12.");
            if (s.Level == 0) Add(id, DesignSeverity.Warning, "level", "The quest level is 0: it shows grey and gives little experience. Set its level.");
            if (s.MaxLevel > 0 && s.MaxLevel < s.MinLevel) Add(id, DesignSeverity.Error, "max-level", "Its maximum level is below its minimum level: nobody can take it.");
            // ---- race and class
            if (s.Classes != 0 && s.Races != 0)
            {
                var combos = RaceClassCombos(d);
                if (combos != null && !combos.Any(c => (s.Races & (1L << (int)(c.Race - 1))) != 0 && (s.Classes & (1L << (int)(c.Class - 1))) != 0))
                    Add(id, DesignSeverity.Error, "classes", $"No race of {QuestForm.RaceText(s.Races)} can be a {QuestForm.ClassText(s.Classes, d.Classes())} (CharBaseInfo): nobody can take it.");
            }
            var (ally, horde) = QuestForm.Teams(s.Races);
            // ---- givers and enders
            if (ev.Count == 0 && !autoComplete) Add(id, DesignSeverity.Error, "ender", "Nobody takes it back: no creature or object ends it, so it can never be handed in. Add a quest ender.");
            foreach (var g in gv.Concat(ev).Distinct())
            {
                string role = gv.Contains(g) ? "giver" : "ender";
                if (!g.Exists) { Add(id, DesignSeverity.Error, role, $"The quest {role} {g.Kind} {g.Id} does not exist."); continue; }
                if (g.Kind == "item") continue;
                if (g.Kind == "creature" && g.Note.Contains("no Quest giver flag", StringComparison.Ordinal))
                    Add(id, DesignSeverity.Error, role, $"{g.Name} has no Quest giver flag (npc_flags 0x2): players cannot talk to it about quests (Player::CanInteractWithNPC).");
                if (g.Kind == "object" && d.GameObjectType(g.Id) != 2)
                    Add(id, DesignSeverity.Error, role, $"{g.Name} is not a quest giver object (type 2): the server ignores the row.");
                if (g.Spawns == 0)
                    Add(id, DesignSeverity.Warning, role, $"{g.Name} is not spawned anywhere: nobody can find it. Place it on the map.");
            }
            // ---- factions: can each team that may take the quest talk to a giver and an ender?
            foreach (var (list, role) in new[] { (gv, "giver"), (ev, "ender") })
            {
                var npcs = list.Where(x => x.Exists).ToList();
                if (npcs.Count == 0) continue;
                bool Usable(QuestRelation x, bool alliance) => x.Kind != "creature" || d.CreatureReaction(x.Id) is not { } fr || !fr.Hostile(alliance);
                bool okA = npcs.Any(x => Usable(x, true)), okH = npcs.Any(x => Usable(x, false));
                string names = string.Join(", ", npcs.Where(x => x.Kind == "creature").Select(x => x.Name).Distinct().Take(3));
                if (!okA && !okH && (ally || horde))
                    Add(id, DesignSeverity.Warning, role, $"Every quest {role} ({names}) is hostile to players of both factions: unless a script changes {(npcs.Count == 1 ? "its" : "their")} faction, nobody can {(role == "giver" ? "take" : "hand in")} the quest.");
                else if (!okA && ally)
                    Add(id, s.Races == 0 ? DesignSeverity.Note : horde ? DesignSeverity.Warning : DesignSeverity.Error, role, s.Races == 0
                        ? $"Only the Horde can {(role == "giver" ? "take" : "hand in")} it: {names} {(npcs.Count == 1 ? "is" : "are")} hostile to the Alliance."
                        : $"The quest is for {QuestForm.RaceText(s.Races)}, but {names} {(npcs.Count == 1 ? "is" : "are")} hostile to the Alliance: Alliance players cannot talk to {(npcs.Count == 1 ? "it" : "them")}.");
                else if (!okH && horde)
                    Add(id, s.Races == 0 ? DesignSeverity.Note : ally ? DesignSeverity.Warning : DesignSeverity.Error, role, s.Races == 0
                        ? $"Only the Alliance can {(role == "giver" ? "take" : "hand in")} it: {names} {(npcs.Count == 1 ? "is" : "are")} hostile to the Horde."
                        : $"The quest is for {QuestForm.RaceText(s.Races)}, but {names} {(npcs.Count == 1 ? "is" : "are")} hostile to the Horde: Horde players cannot talk to {(npcs.Count == 1 ? "it" : "them")}.");
            }
            // ---- objectives
            bool any = false;
            for (int k = 1; k <= 4; k++)
            {
                long target = d.Q(row, "ReqCreatureOrGOId" + k), count = d.Q(row, "ReqCreatureOrGOCount" + k), spell = d.Q(row, "ReqSpellCast" + k);
                if (target == 0) continue;
                any = true;
                if (count == 0) Add(id, DesignSeverity.Error, $"target{k}-count", $"Objective {k} needs a count above 0 (the server ignores it).");
                if (spell != 0 && !d.Ed.LinkExists("spell", spell)) Add(id, DesignSeverity.Error, $"target{k}-spell", $"Objective {k}: spell {spell} does not exist on the server.");
                if (target > 0)
                {
                    if (!d.Ed.LinkExists("creature", target)) { Add(id, DesignSeverity.Error, $"target{k}", $"Objective {k}: creature {target} does not exist."); continue; }
                    bool talk = d.IsTalkTarget(id, target);
                    if (talk && (s.SpecialFlags & 2) != 0) Add(id, DesignSeverity.Error, $"target{k}", $"Objective {k} is a \"speak to\", but the quest has the Explore or event flag: VMaNGOS gives no talk credit then.");
                    if (d.Spawns("creature", target, 1).Count == 0)
                        Add(id, DesignSeverity.Warning, $"target{k}", $"Objective {k}: {d.CreatureName(target)} is not spawned anywhere; only a script that summons it can complete this.");
                    if (!talk && spell == 0 && d.CreatureReaction(target) is { } fr)
                    {
                        bool friendlyToAll = (!ally || fr.FriendlyToAlliance) && (!horde || fr.FriendlyToHorde);
                        if (friendlyToAll) Add(id, DesignSeverity.Error, $"target{k}", $"Objective {k}: {d.CreatureName(target)} is friendly to {(ally && horde ? "both factions" : ally ? "the Alliance" : "the Horde")}: they cannot attack it. Pick an enemy, or make it a \"speak to\" or a spell to cast.");
                    }
                }
                else
                {
                    if (!d.Ed.LinkExists("gameobject", -target)) { Add(id, DesignSeverity.Error, $"target{k}", $"Objective {k}: game object {-target} does not exist."); continue; }
                    if (d.Spawns("object", -target, 1).Count == 0) Add(id, DesignSeverity.Warning, $"target{k}", $"Objective {k}: {d.ObjectName(-target)} is not spawned anywhere.");
                }
            }
            for (int k = 1; k <= 4; k++)
            {
                long item = d.Q(row, "ReqItemId" + k), count = d.Q(row, "ReqItemCount" + k);
                if (item == 0) continue;
                any = true;
                if (!d.Ed.LinkExists("item", item)) { Add(id, DesignSeverity.Error, $"item{k}", $"Item {item} does not exist: the quest can't be done."); continue; }
                if (count == 0) Add(id, DesignSeverity.Error, $"item{k}-count", $"Item objective {k} needs a count above 0.");
                var sources = d.DropSources(item);
                bool fromThisQuest = d.Q(row, "SrcItemId") == item;
                if (sources.Count == 0 && !fromThisQuest)
                    Add(id, DesignSeverity.Error, $"item{k}", $"Nothing gives {d.ItemName(item)}: no creature drops it, no chest holds it, no vendor sells it, no quest or spell makes it. Add a drop source.");
                else if (!fromThisQuest && sources.All(x => x.Spawns == 0))
                    Add(id, DesignSeverity.Warning, $"item{k}", $"{d.ItemName(item)} comes only from {string.Join(", ", sources.Take(3).Select(x => x.Name))}, which {(sources.Count == 1 ? "is" : "are")} not spawned anywhere.");
                if (d.HasTable("item_template"))
                {
                    var it = d.T("item_template");
                    if (d.Ed.Object(it, item) is { } ir && QuestDesigner.L(it, ir, "max_count") is var mc and > 0 && mc < count)
                        Add(id, DesignSeverity.Error, $"item{k}-count", $"It asks for {count} {d.ItemName(item)}, but a player can carry only {mc} (max_count).");
                }
            }
            var triggers = d.AreaTriggers(id);
            foreach (var t in triggers.Where(t => !t.Exists))
                Add(id, DesignSeverity.Error, "explore", $"Area trigger {t.Id} is not in the game's AreaTrigger.dbc: the game never tells the server you entered it.");
            if (triggers.Count > 0 && (s.SpecialFlags & 2) == 0)
                Add(id, DesignSeverity.Error, "special-flags", "It is completed by an area trigger, but lacks the Explore or event flag: VMaNGOS ignores the trigger.");
            if ((s.SpecialFlags & 2) != 0 && triggers.Count == 0 && !HasCompletingScript(d, id))
                Add(id, DesignSeverity.Warning, "special-flags", "It has the Explore or event flag, but no area trigger or script completes it (an escort's or event's creature script may: check it).");
            if (d.Q(row, "RepObjectiveFaction") != 0) any = true;
            if (triggers.Count > 0 || (s.SpecialFlags & 2) != 0) any = true;
            if (d.Q(row, "SrcItemId") != 0 && d.Q(row, "SrcItemCount") == 0) Add(id, DesignSeverity.Warning, "start-item-count", "The start item has a count of 0 (the server makes it 1).");
            if (d.Q(row, "SrcItemId") != 0 && !d.Ed.LinkExists("item", d.Q(row, "SrcItemId"))) Add(id, DesignSeverity.Error, "start-item", $"Start item {d.Q(row, "SrcItemId")} does not exist: the quest can't be done.");
            if (!any && gv.Select(g => g.Id).ToHashSet().SetEquals(ev.Select(e => e.Id)) && gv.Count > 0 && s.Next == 0 && s.NextInChain == 0 && (d.Q(row, "RewOrReqMoney") >= 0))
                Add(id, DesignSeverity.Note, "objectives", "It has no objective and is handed in where it was taken: players finish it at once.");
            // ---- rewards
            for (int k = 1; k <= 6; k++)
                if (d.Q(row, "RewChoiceItemId" + k) is var ci and not 0)
                {
                    if (!d.Ed.LinkExists("item", ci)) Add(id, DesignSeverity.Error, $"choice{k}", $"Reward choice {k}: item {ci} does not exist.");
                    if ((s.Flags & 0x400) != 0) Add(id, DesignSeverity.Error, $"choice{k}", "An automatically rewarded quest cannot give a choice of items (VMaNGOS drops them).");
                }
            for (int k = 1; k <= 4; k++)
                if (d.Q(row, "RewItemId" + k) is var ri and not 0 && !d.Ed.LinkExists("item", ri)) Add(id, DesignSeverity.Error, $"reward-item{k}", $"Reward item {k}: item {ri} does not exist.");
            foreach (var col in new[] { "RewSpell", "RewSpellCast" })
            {
                if (d.Q(row, col) is var sp and not 0 && !d.Ed.LinkExists("spell", sp))
                    Add(id, DesignSeverity.Error, col == "RewSpell" ? "spell" : "spell-cast", $"Spell {sp} does not exist on the server: the quest gives no spell.");
                // 0.13.3 (found in game): a reward spell above 40000 is never cast, so the quest teaches nothing
                if (d.Q(row, col) is var big and > 0 && !SpellIdLimits.Castable(big))
                    Add(id, DesignSeverity.Error, col == "RewSpell" ? "spell" : "spell-cast", SpellIdLimits.Problem(big));
            }
            if (d.Q(row, "RewSpell") is var rs and not 0 && d.Q(row, "RewSpellCast") == 0 && d.Spells.Exists(rs) && d.Spells.LearnSpellsOf(rs).Count > 0)
                Add(id, DesignSeverity.Note, "spell", $"The reward spell {d.SpellName(rs)} is cast on the player as it is; to teach it, use its learn spell as \"Spell cast on the player\" (\"Reward spell... (learn)\").");
            for (int k = 1; k <= 5; k++)
                if (d.Q(row, "RewRepFaction" + k) != 0 && d.Q(row, "RewRepValue" + k) == 0) Add(id, DesignSeverity.Warning, $"rep{k}-value", $"Reputation {k} has a faction but no amount.");
            if (d.Quests.Has("RewXP") && d.Q(row, "RewXP") == 0 && s.Level > 0 && (s.Flags & 0x400) == 0)
                Add(id, DesignSeverity.Note, "xp", "It gives no experience (VMaNGOS gives exactly RewXP; 0 = none).");
            // ---- texts
            foreach (var p in TextProblems(d, id)) res.Add(new QuestProblem(id, s.Title, p));
            if (string.IsNullOrWhiteSpace(d.S(row, "Title"))) Add(id, DesignSeverity.Error, "title", "It has no title.");
            if (string.IsNullOrWhiteSpace(d.S(row, "Objectives"))) Add(id, DesignSeverity.Warning, "objectives", "The quest log has no objectives text.");
            if (string.IsNullOrWhiteSpace(d.S(row, "OfferRewardText"))) Add(id, DesignSeverity.Note, "completion-text", "No completion text: the reward window is empty.");
        }
    }

    /// <summary>The client's text limits of one quest (errors here block saving: the game would overwrite its memory).</summary>
    public static List<DesignProblem> TextProblems(QuestDesigner d, long id)
    {
        var row = d.Require(id);
        var res = new List<DesignProblem>();
        foreach (var lim in QuestText.Limits)
            if (d.Quests.Has(lim.Column))
                res.AddRange(QuestText.Check(lim.Field, d.S(row, lim.Column), d.Names.Races, d.Names.Classes));
        return res;
    }

    private static bool Overlap(long a, long b) => a == 0 || b == 0 || (a & b) != 0;

    /// <summary>The race / class pairs the game allows (CharBaseInfo).</summary>
    public static List<(long Race, long Class)>? RaceClassCombos(QuestDesigner d)
    {
        try
        {
            var t = d.Session.Lookup("CharBaseInfo");
            if (t == null) return null;
            var rc = t.Layout.Column("Race")!;
            var cc = t.Layout.Column("Class")!;
            return t.Rows.Select(r => (t.Number(r, rc), t.Number(r, cc))).Where(x => x.Item1 > 0 && x.Item2 > 0).ToList();
        }
        catch (Exception e) when (e is Dbc.DbcException or IOException) { return null; }
    }

    /// <summary>Does a script complete the quest (SCRIPT_COMMAND_QUEST_EXPLORED, 7, with the quest in datalong)?</summary>
    public static bool HasCompletingScript(QuestDesigner d, long quest)
    {
        foreach (var table in new[] { "quest_start_scripts", "quest_end_scripts", "gossip_scripts", "event_scripts", "generic_scripts", "creature_movement_scripts", "spell_scripts", "creature_ai_scripts" })
        {
            if (!d.HasTable(table)) continue;
            var t = d.T(table);
            if (!t.Has("command") || !t.Has("datalong")) continue;
            if (d.Ed.Rows(t, RowFilter.Where(new FilterTerm("command", FilterOp.Eq, "7"), new FilterTerm("datalong", FilterOp.Eq, quest.ToString(CultureInfo.InvariantCulture))), 1).Count > 0) return true;
        }
        return false;
    }
}
