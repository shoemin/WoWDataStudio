using System.Globalization;
using WoWData.Core.Maps;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>Which teams a creature's faction is hostile or friendly to (from the server's faction_template).</summary>
public sealed record FactionReaction(long Template, bool HostileToAlliance, bool HostileToHorde, bool FriendlyToAlliance, bool FriendlyToHorde, string Reputation = "")
{
    /// <summary>The reaction depends on the player's reputation with this faction (it has a place in the reputation list).</summary>
    public bool ByReputation => Reputation.Length > 0;
    public bool Hostile(bool alliance) => alliance ? HostileToAlliance : HostileToHorde;
    public bool Friendly(bool alliance) => alliance ? FriendlyToAlliance : FriendlyToHorde;
    public string Text => (ByReputation ? $"reputation with {Reputation}: " : "") + (HostileToAlliance && HostileToHorde ? "hostile to both factions" : HostileToAlliance ? "Horde (hostile to the Alliance)" : HostileToHorde ? "Alliance (hostile to the Horde)"
        : FriendlyToAlliance && !FriendlyToHorde ? "Alliance" : FriendlyToHorde && !FriendlyToAlliance ? "Horde" : "neutral or friendly to both");
}

public sealed partial class QuestDesigner
{
    // ------------------------------------------------------------------ factions

    // the players' own factions (Faction.dbc): Human, Dwarf, Night Elf, Gnome / Orc, Undead, Tauren, Troll
    private static readonly long[] AlliancePlayerFactions = { 1, 3, 4, 115 };
    private static readonly long[] HordePlayerFactions = { 2, 5, 6, 116 };

    /// <summary>How a faction template reacts to the two teams of players (FactionTemplateEntry::IsHostileTo / IsFriendlyTo
    /// against the players' masks: player 1, Alliance 2, Horde 4, and the players' own factions in the enemy / friend lists).</summary>
    public FactionReaction? Reaction(long template)
    {
        if (!HasTable("faction_template")) return null;
        var t = T("faction_template");
        var r = Ed.Object(t, template);
        if (r == null) return null;
        long hostile = L(t, r, "hostile_mask"), friendly = L(t, r, "friendly_mask"), our = L(t, r, "our_mask");
        var enemies = Enumerable.Range(1, 4).Select(k => L(t, r, "enemy_faction" + k)).Where(x => x != 0).ToHashSet();
        var friends = Enumerable.Range(1, 4).Select(k => L(t, r, "friend_faction" + k)).Where(x => x != 0).ToHashSet();
        // a faction with a place in the reputation list reacts by the player's reputation (Player::GetReactionTo): its
        // starting reputation for the team's races (Faction.dbc ReputationRaceMask / ReputationBase) decides; below
        // -3000 (hostile, hated) players are attacked, from 3000 (friendly) they are friends, between: neutral
        long faction = L(t, r, "faction_id");
        try
        {
            if (Session.Lookup("Faction") is { } fd && fd.Find(faction) is { } fr && fd.Number(fr, fd.Layout.Column("ReputationIndex")!) >= 0)
            {
                string rep = fd.Text(fr, fd.Layout.Column("Name")!, 0);
                long Base(long raceBit)
                {
                    for (int i = 1; i <= 4; i++)
                        if ((fd.Number(fr, fd.Layout.Column($"ReputationRaceMask{i}")!) & raceBit) != 0) return fd.Number(fr, fd.Layout.Column($"ReputationBase{i}")!);
                    return 0;
                }
                var ally = new[] { 0x1L, 0x4, 0x8, 0x40 }.Select(Base).ToList();
                var horde = new[] { 0x2L, 0x10, 0x20, 0x80 }.Select(Base).ToList();
                return new FactionReaction(template, ally.All(v => v < -3000), horde.All(v => v < -3000), ally.All(v => v >= 3000), horde.All(v => v >= 3000), rep);
            }
        }
        catch (Exception e) when (e is Dbc.DbcException or IOException) { }
        bool H(long bit, long[] factions) => !factions.Any(friends.Contains) && (factions.Any(enemies.Contains) || (hostile & (1 | bit)) != 0);
        bool F(long bit, long[] factions) => !H(bit, factions) && (factions.Any(friends.Contains) || (friendly & bit) != 0 || (our & bit) != 0);
        return new FactionReaction(template, H(2, AlliancePlayerFactions), H(4, HordePlayerFactions), F(2, AlliancePlayerFactions), F(4, HordePlayerFactions));
    }

    public FactionReaction? CreatureReaction(long creature)
    {
        var t = T("creature_template");
        var r = Ed.Object(t, creature);
        return r == null ? null : Reaction(L(t, r, "faction"));
    }

    // ------------------------------------------------------------------ drop sources

    /// <summary>Everything that gives an item: creatures' loot (also through reference loot), chests and other objects,
    /// containers, pickpocketing, skinning, fishing, vendors, quests that hand it out, spells that create it.</summary>
    public List<QuestDropSource> DropSources(long item, int max = 60)
    {
        var res = new List<QuestDropSource>();
        if (item == 0) return res;
        string id = item.ToString(CultureInfo.InvariantCulture);
        string Chance(double c) => c < 0 ? $"{-c:0.##}% (only while on the quest)" : $"{c:0.##}%";
        var ct = T("creature_template");
        void Creatures(string column, IEnumerable<(long Entry, double Chance)> loot, string what)
        {
            foreach (var (entry, chance) in loot)
                foreach (var row in Ed.Rows(ct, RowFilter.Eq(column, entry), 20).GroupBy(r => L(ct, r, "entry")).Select(g => Ed.Service.Effective(ct, g.ToList()) ?? g.First()))
                {
                    long c = L(ct, row, "entry");
                    if (res.Any(x => x.Kind == "creature" && x.Id == c && x.Table == what)) continue;
                    res.Add(new QuestDropSource("creature", c, V(ct, row, "name") ?? "", $"{(what == "creature_loot_template" ? "drops it" : what == "pickpocketing_loot_template" ? "can be pickpocketed for it" : "can be skinned for it")}: {Chance(chance)}", what, entry, Spawns("creature", c, 1).Count));
                }
        }
        List<(long Entry, double Chance)> Loot(string table, long? reference = null)
        {
            if (!HasTable(table)) return new();
            var t = T(table);
            var f = reference is { } rf ? RowFilter.Eq("mincountOrRef", -rf) : RowFilter.Where(new FilterTerm("item", FilterOp.Eq, id), new FilterTerm("mincountOrRef", FilterOp.Gt, "0"));
            return Ed.Rows(t, f, 200).Where(r => Ed.Service.RowVersion(t, r).Loaded).Select(r => (L(t, r, "entry"), WorldValue.Number(V(t, r, "ChanceOrQuestChance")))).ToList();
        }
        Creatures("loot_id", Loot("creature_loot_template"), "creature_loot_template");
        Creatures("pickpocket_loot_id", Loot("pickpocketing_loot_template"), "pickpocketing_loot_template");
        Creatures("skinning_loot_id", Loot("skinning_loot_template"), "skinning_loot_template");
        // reference loot: the loot tables that include the reference
        foreach (var (refEntry, refChance) in Loot("reference_loot_template"))
            Creatures("loot_id", Loot("creature_loot_template", refEntry).Select(x => (x.Entry, x.Chance == 0 ? refChance : x.Chance)), "creature_loot_template");
        if (HasTable("gameobject_template"))
        {
            var gt = T("gameobject_template");
            foreach (var (entry, chance) in Loot("gameobject_loot_template"))
                foreach (var row in Ed.Rows(gt, RowFilter.Eq("data1", entry), 20).Where(r => L(gt, r, "type") is 3 or 25).GroupBy(r => L(gt, r, "entry")).Select(g => g.First()))
                    res.Add(new QuestDropSource("object", L(gt, row, "entry"), V(gt, row, "name") ?? "", $"holds it: {Chance(chance)}", "gameobject_loot_template", entry, Spawns("object", L(gt, row, "entry"), 1).Count));
        }
        if (HasTable("item_loot_template"))
            foreach (var (entry, chance) in Loot("item_loot_template"))
                res.Add(new QuestDropSource("item", entry, ItemName(entry), $"contains it: {Chance(chance)}", "item_loot_template", entry, 1));
        if (HasTable("fishing_loot_template"))
            foreach (var (entry, chance) in Loot("fishing_loot_template"))
                res.Add(new QuestDropSource("fishing", entry, ZoneName(entry), $"fishing: {Chance(chance)}", "fishing_loot_template", entry, 1));
        if (HasTable("npc_vendor"))
        {
            var vt = T("npc_vendor");
            foreach (var r in Ed.Rows(vt, RowFilter.Eq("item", item), 20))
                res.Add(new QuestDropSource("vendor", L(vt, r, "entry"), CreatureName(L(vt, r, "entry")), "sells it", "npc_vendor", 0, Spawns("creature", L(vt, r, "entry"), 1).Count));
        }
        if (HasTable("npc_vendor_template"))
        {
            var vt = T("npc_vendor_template");
            foreach (var tpl in Ed.Rows(vt, RowFilter.Eq("item", item), 20).Select(r => L(vt, r, "entry")).Distinct())
                foreach (var row in Ed.Rows(ct, RowFilter.Eq("vendor_id", tpl), 10).GroupBy(r => L(ct, r, "entry")).Select(g => g.First()))
                    res.Add(new QuestDropSource("vendor", L(ct, row, "entry"), V(ct, row, "name") ?? "", $"sells it (vendor list {tpl})", "npc_vendor_template", tpl, Spawns("creature", L(ct, row, "entry"), 1).Count));
        }
        foreach (var r in Ed.Rows(Quests, RowFilter.Eq("SrcItemId", item), 20))
            res.Add(new QuestDropSource("quest", Q(r, "entry"), S(r, "Title"), "hands it out when accepted", "quest_template", 0, 1));
        var rf = RowFilter.Eq("RewItemId1", item);
        for (int k = 2; k <= 4; k++) rf.Or(new FilterTerm("RewItemId" + k, FilterOp.Eq, id));
        for (int k = 1; k <= 6; k++) rf.Or(new FilterTerm("RewChoiceItemId" + k, FilterOp.Eq, id));
        foreach (var r in Ed.Rows(Quests, rf, 20))
            if (res.All(x => !(x.Kind == "quest" && x.Id == Q(r, "entry"))))
                res.Add(new QuestDropSource("quest", Q(r, "entry"), S(r, "Title"), "gives it as a reward", "quest_template", 0, 1));
        if (HasTable("disenchant_loot_template"))
            foreach (var (entry, chance) in Loot("disenchant_loot_template"))
                res.Add(new QuestDropSource("disenchant", entry, $"disenchanting (loot {entry})", $"disenchanting items gives it: {Chance(chance)}", "disenchant_loot_template", entry, 1));
        if (HasTable("spell_template"))
        {
            var st = T("spell_template");
            var f = RowFilter.Where(new FilterTerm("effect1", FilterOp.Eq, "24"), new FilterTerm("effectItemType1", FilterOp.Eq, id))
                .Or(new FilterTerm("effect2", FilterOp.Eq, "24"), new FilterTerm("effectItemType2", FilterOp.Eq, id))
                .Or(new FilterTerm("effect3", FilterOp.Eq, "24"), new FilterTerm("effectItemType3", FilterOp.Eq, id));
            foreach (var r in Ed.Rows(st, f, 20).GroupBy(r => L(st, r, "entry")).Select(g => g.First()))
                res.Add(new QuestDropSource("spell", L(st, r, "entry"), V(st, r, "name") ?? "", "creates it", "spell_template", 0, 1));
        }
        return res.Take(max).ToList();
    }

    /// <summary>"Add drop source": a creature drops the item (its loot table, made when it has none). A quest-only drop
    /// (negative chance) drops only for players on a quest that needs it.</summary>
    public DesignResult AddDropSource(long item, long creature, double chance, bool questOnly = true, long min = 1, long max = 1)
    {
        try
        {
            if (!Ed.LinkExists("item", item)) return DesignResult.Refused($"There is no item {item} on the server.");
            var ct = T("creature_template");
            var row = Ed.Object(ct, creature);
            if (row == null) return DesignResult.Refused($"There is no creature {creature} on the server.");
            if (chance is <= 0 or > 100) return DesignResult.Refused("The chance is a percentage above 0 and at most 100.");
            long lootId = L(ct, row, "loot_id");
            bool newTable = lootId == 0;
            string made = "";
            if (newTable)
            {
                lootId = creature;
                made = $" {CreatureName(creature)} had no loot table: it now uses loot {lootId}.";
            }
            var lt = T("creature_loot_template");
            var existing = Ed.Rows(lt, RowFilter.Where(new FilterTerm("entry", FilterOp.Eq, lootId.ToString(CultureInfo.InvariantCulture)), new FilterTerm("item", FilterOp.Eq, item.ToString(CultureInfo.InvariantCulture)), new FilterTerm("groupid", FilterOp.Eq, "0")));
            string ch = (questOnly ? -chance : chance).ToString("0.###", CultureInfo.InvariantCulture);
            if (existing.Count > 0)
            {
                var r = Ed.Set(lt.Name, lt.KeyText(existing[0]), new (string, string?)[] { ("ChanceOrQuestChance", ch), ("mincountOrRef", min.ToString(CultureInfo.InvariantCulture)), ("maxcount", Math.Max(min, max).ToString(CultureInfo.InvariantCulture)) });
                if (!r.Ok) return DesignResult.Refused(r.Message);
            }
            else
            {
                // the loot entry is given as the row's ID (a new loot table does not exist yet: it is not checked as a link)
                var values = new List<(string, string?)> { ("item", item.ToString(CultureInfo.InvariantCulture)), ("ChanceOrQuestChance", ch), ("groupid", "0"), ("mincountOrRef", min.ToString(CultureInfo.InvariantCulture)), ("maxcount", Math.Max(min, max).ToString(CultureInfo.InvariantCulture)) };
                if (lt.Has("patch_min")) values.Add(("patch_min", "0"));
                if (lt.Has("patch_max")) values.Add(("patch_max", "10"));
                var (_, r) = Ed.Add(lt.Name, values, lootId, $"Drop source: {CreatureName(creature)} drops {ItemName(item)}");
                if (!r.Ok) return DesignResult.Refused(r.Message);
            }
            if (newTable)
            {
                var s = Ed.Set(ct.Name, ct.KeyText(Ed.Object(ct, creature)!), "loot_id", lootId.ToString(CultureInfo.InvariantCulture));
                if (!s.Ok) return DesignResult.Refused(s.Message);
            }
            var problems = new List<DesignProblem>();
            if (Spawns("creature", creature, 1).Count == 0) problems.Add(new DesignProblem(DesignSeverity.Warning, "drop source", $"{CreatureName(creature)} is not spawned anywhere."));
            return DesignResult.Done($"{CreatureName(creature)} drops {ItemName(item)}: {chance:0.##}%{(questOnly ? " (only while on the quest)" : "")}.{made}", problems);
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    // ------------------------------------------------------------------ new quest givers, placed on the map

    /// <summary>
    /// "New quest giver": a creature made from an existing one (its look, faction and level; <paramref name="copyFrom"/>),
    /// with its own name, the Quest giver flag and none of the copied vendor, trainer, loot or script parts, with the
    /// first free ID of your custom range; with a position it is also placed in the world (a creature spawn).
    /// </summary>
    public (long Entry, long? Guid, string Message) NewNpc(string name, long copyFrom, string? subname = null, long? level = null, long? faction = null,
        long? map = null, double? x = null, double? y = null, double? z = null, double orientation = 0, long? id = null)
    {
        var ct = T("creature_template");
        var src = Ed.Object(ct, copyFrom) ?? throw new DesignException($"There is no creature {copyFrom} to copy the look from.");
        if (string.IsNullOrWhiteSpace(name)) throw new DesignException("A quest giver needs a name.");
        var values = new List<(string, string?)>
        {
            ("name", name), ("subname", subname ?? ""), ("npc_flags", "2"), ("gossip_menu_id", "0"), ("loot_id", "0"), ("pickpocket_loot_id", "0"), ("skinning_loot_id", "0"),
            ("trainer_id", "0"), ("vendor_id", "0"), ("trainer_type", "0"), ("trainer_class", "0"), ("trainer_race", "0"), ("trainer_spell", "0"), ("gold_min", "0"), ("gold_max", "0"),
            ("script_name", ""), ("patch", "0"),
        };
        if (level != null) { values.Add(("level_min", level.Value.ToString(CultureInfo.InvariantCulture))); values.Add(("level_max", level.Value.ToString(CultureInfo.InvariantCulture))); }
        if (faction != null) values.Add(("faction", faction.Value.ToString(CultureInfo.InvariantCulture)));
        long entry = id ?? Ed.SuggestId(ct);
        var (key, r) = Ed.Duplicate(ct.Name, ct.KeyText(src), values.Where(v => ct.Has(v.Item1)), entry);
        if (!r.Ok || key == null) throw new DesignException(r.Message);
        string msg = $"New quest giver {name} ({entry}), looking like {CreatureName(copyFrom)}.";
        long? guid = null;
        if (map != null && x != null && y != null)
        {
            var (g, m) = PlaceNpc(entry, map.Value, x.Value, y.Value, z, orientation);
            guid = g;
            msg += " " + m;
        }
        else msg += " Not placed yet: give it a position (map, x, y).";
        return (entry, guid, msg);
    }

    /// <summary>A spawn of a creature at a position (z from the terrain when it is not given).</summary>
    public (long Guid, string Message) PlaceNpc(long entry, long map, double x, double y, double? z = null, double orientation = 0)
    {
        if (!Ed.LinkExists("creature", entry)) throw new DesignException($"There is no creature {entry} on the server.");
        double zz = z ?? GroundHeight(map, x, y) ?? throw new DesignException($"The ground height at {x:0.#}, {y:0.#} on map {map} could not be read from the game's terrain: give z.");
        var t = T("creature");
        long guid = Ed.SuggestId(t);
        var values = new List<(string, string?)>
        {
            ("guid", guid.ToString(CultureInfo.InvariantCulture)), ("id", entry.ToString(CultureInfo.InvariantCulture)), ("map", map.ToString(CultureInfo.InvariantCulture)),
            ("position_x", F(x)), ("position_y", F(y)), ("position_z", F(zz)), ("orientation", F(orientation)),
            ("spawntimesecsmin", "300"), ("spawntimesecsmax", "300"), ("wander_distance", "0"), ("movement_type", "0"), ("patch_min", "0"), ("patch_max", "10"),
        };
        var (_, r) = Ed.Add(t.Name, values.Where(v => t.Has(v.Item1)), guid, $"Spawn of {CreatureName(entry)}");
        if (!r.Ok) throw new DesignException(r.Message);
        return (guid, $"Placed on map {map} at {x:0.#}, {y:0.#}, {zz:0.#} (spawn {guid}).");
    }

    /// <summary>A position for a FLOAT column: 6 significant digits, as the server's MySQL stores it (a longer value reads
    /// back rounded, and "Undo last apply" would see the row as changed since the apply).</summary>
    private static string F(double v) => float.Parse(((float)v).ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The terrain height at a world position, from the game's (or your edited) map tiles.</summary>
    public double? GroundHeight(long map, double x, double y)
    {
        try
        {
            var mt = Session.Lookup("Map");
            var row = mt?.Find(map);
            if (mt == null || row == null) return null;
            var dir = mt.Text(row, mt.Layout.Column("Directory")!, 0);
            int tileX = (int)Math.Floor(32 - y / TerrainMath.TileSize), tileY = (int)Math.Floor(32 - x / TerrainMath.TileSize);
            if (tileX is < 0 or > 63 || tileY is < 0 or > 63) return null;
            var data = Session.ReadFile($@"World\Maps\{dir}\{dir}_{tileX}_{tileY}.adt");
            if (data == null) return null;
            var tile = TerrainTile.Load(data, dir, tileX, tileY);
            return tile.HeightAt((float)x, (float)y);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or Dbc.DbcException) { return null; }
    }

    /// <summary>The map tile of a world position (tile x from world y, tile y from world x).</summary>
    public static (int X, int Y) TileOf(double x, double y) => ((int)Math.Floor(32 - y / TerrainMath.TileSize), (int)Math.Floor(32 - x / TerrainMath.TileSize));

    // ------------------------------------------------------------------ "speak to an NPC" objectives (gossip)

    /// <summary>Is a creature objective of a quest a "speak to" (a gossip option of the NPC that closes with talk credit)?</summary>
    public bool IsTalkTarget(long quest, long creature) => TalkOption(quest, creature) != null;

    private string?[]? TalkOption(long quest, long creature)
    {
        if (!HasTable("gossip_menu_option") || !HasTable("conditions")) return null;
        var ct = T("creature_template");
        var c = Ed.Object(ct, creature);
        long menu = c == null ? 0 : L(ct, c, "gossip_menu_id");
        if (menu == 0) return null;
        var ot = T("gossip_menu_option");
        var cond = T("conditions");
        foreach (var o in Ed.Rows(ot, RowFilter.Eq("menu_id", menu)))
        {
            if (L(ot, o, "action_menu_id") >= 0) continue;
            long cid = L(ot, o, "condition_id");
            var cr = cid == 0 ? null : Ed.Rows(cond, RowFilter.Eq("condition_entry", cid), 1).FirstOrDefault();
            if (cr != null && L(cond, cr, "type") == 9 && L(cond, cr, "value1") == quest) return o;
        }
        return null;
    }

    /// <summary>
    /// "Speak to an NPC": objective <paramref name="slot"/> is the NPC (count 1), and the NPC gets a gossip option shown only
    /// while the quest is in the player's log (a condition: quest taken, not done). Choosing it closes the window and gives
    /// the talk credit (VMaNGOS Player::OnGossipSelect: an option with action_menu_id -1 calls TalkedToCreature). An NPC
    /// without a gossip menu gets one (its greeting in npc_text / broadcast_text) and the Gossip flag.
    /// </summary>
    public DesignResult AddTalkObjective(long quest, long npc, int slot = 1, string? optionText = null, string? greeting = null)
    {
        try
        {
            var q = Require(quest);
            if (slot is < 1 or > 4) return DesignResult.Refused("A quest has 4 kill / use / speak-to objectives (1-4).");
            if (!Ed.LinkExists("creature", npc)) return DesignResult.Refused($"There is no creature {npc} on the server.");
            if ((Q(q, "SpecialFlags") & 2) != 0) return DesignResult.Refused("The quest is completed by an event (special flags: Explore or event): VMaNGOS gives no talk credit then. Clear that flag first.");
            var ct = T("creature_template");
            var c = Ed.Object(ct, npc)!;
            long menu = L(ct, c, "gossip_menu_id");
            var made = new List<string>();
            if (menu == 0 || !Ed.LinkExists("gossip_menu", menu))
            {
                menu = NewGossipMenu(greeting ?? $"Yes, $N? What do you need?");
                var r0 = Ed.Set(ct.Name, ct.KeyText(c), "gossip_menu_id", menu.ToString(CultureInfo.InvariantCulture));
                if (!r0.Ok) return DesignResult.Refused(r0.Message);
                made.Add($"a gossip menu ({menu})");
            }
            EnsureNpcFlag(npc, 0x1);
            if (TalkOption(quest, npc) == null)
            {
                long cond = Condition(9, quest, 1);
                var ot = T("gossip_menu_option");
                long next = Ed.Rows(ot, RowFilter.Eq("menu_id", menu)).Select(o => L(ot, o, "id")).DefaultIfEmpty(-1).Max() + 1;
                var values = new List<(string, string?)>
                {
                    ("menu_id", menu.ToString(CultureInfo.InvariantCulture)), ("id", next.ToString(CultureInfo.InvariantCulture)), ("option_icon", "0"),
                    ("option_text", optionText ?? "I am ready."), ("option_id", "1"), ("npc_option_npcflag", "1"), ("action_menu_id", "-1"),
                    ("condition_id", cond.ToString(CultureInfo.InvariantCulture)),
                };
                var (_, r) = Ed.Add(ot.Name, values.Where(v => ot.Has(v.Item1)), menu, $"Talk option of {CreatureName(npc)} for quest {quest}");
                if (!r.Ok) return DesignResult.Refused(r.Message);
                made.Add($"the option \"{optionText ?? "I am ready."}\" (shown while the quest is in the log)");
            }
            SetColumns(quest, new (string, string?)[] { ($"ReqCreatureOrGOId{slot}", npc.ToString(CultureInfo.InvariantCulture)), ($"ReqCreatureOrGOCount{slot}", "1"), ($"ReqSpellCast{slot}", "0"), ($"ObjectiveText{slot}", $"Speak with {CreatureName(npc)}") }, $"Speak-to objective of quest {quest}");
            return DesignResult.Done($"Objective {slot}: speak with {CreatureName(npc)}." + (made.Count > 0 ? " Made " + string.Join(" and ", made) + "." : ""));
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    /// <summary>A condition row (reused when one with the same values exists: the table's values are unique).</summary>
    public long Condition(long type, long value1, long value2 = 0)
    {
        var t = T("conditions");
        var hit = Ed.Rows(t, RowFilter.Where(new FilterTerm("type", FilterOp.Eq, type.ToString(CultureInfo.InvariantCulture)), new FilterTerm("value1", FilterOp.Eq, value1.ToString(CultureInfo.InvariantCulture)),
            new FilterTerm("value2", FilterOp.Eq, value2.ToString(CultureInfo.InvariantCulture)), new FilterTerm("value3", FilterOp.Eq, "0"), new FilterTerm("value4", FilterOp.Eq, "0"), new FilterTerm("flags", FilterOp.Eq, "0")), 1).FirstOrDefault();
        if (hit != null) return L(t, hit, "condition_entry");
        long id = Ed.SuggestId(t);
        var (_, r) = Ed.Add(t.Name, new (string, string?)[] { ("type", type.ToString(CultureInfo.InvariantCulture)), ("value1", value1.ToString(CultureInfo.InvariantCulture)), ("value2", value2.ToString(CultureInfo.InvariantCulture)) }, id, $"Condition {type} {value1}");
        if (!r.Ok) throw new DesignException(r.Message);
        return id;
    }

    /// <summary>A gossip menu with its greeting text (broadcast_text, npc_text, gossip_menu; menu IDs are small numbers).</summary>
    public long NewGossipMenu(string greeting)
    {
        var bt = T("broadcast_text");
        long b = Ed.SuggestId(bt);
        var (_, r1) = Ed.Add(bt.Name, new (string, string?)[] { ("male_text", greeting), ("female_text", greeting) }, b, "Greeting text");
        if (!r1.Ok) throw new DesignException(r1.Message);
        var nt = T("npc_text");
        long n = Ed.SuggestId(nt);
        var (_, r2) = Ed.Add(nt.Name, new (string, string?)[] { ("BroadcastTextID0", b.ToString(CultureInfo.InvariantCulture)), ("Probability0", "1") }, n, "Greeting");
        if (!r2.Ok) throw new DesignException(r2.Message);
        var gm = T("gossip_menu");
        long m = Ed.SuggestId(gm);
        var (_, r3) = Ed.Add(gm.Name, new (string, string?)[] { ("text_id", n.ToString(CultureInfo.InvariantCulture)) }, m, "Gossip menu");
        if (!r3.Ok) throw new DesignException(r3.Message);
        return m;
    }

    // ------------------------------------------------------------------ exploration (area triggers)

    public bool TriggerExists(long id) => Session.Lookup("AreaTrigger")?.Find(id) != null;

    public string TriggerPlace(long id)
    {
        var t = Session.Lookup("AreaTrigger");
        var r = t?.Find(id);
        if (t == null || r == null) return "not in the game's AreaTrigger.dbc";
        float X(string c) => t.Float(r, t.Layout.Column(c)!);
        return $"map {t.Number(r, t.Layout.Column("MapID")!)} at {X("X"):0.#}, {X("Y"):0.#}, {X("Z"):0.#}, radius {X("Radius"):0.#}";
    }

    /// <summary>"Explore": entering an area trigger completes the quest (areatrigger_involvedrelation; the quest gets the
    /// Explore or event flag). A trigger serves one quest only.</summary>
    public DesignResult AddExplore(long quest, long trigger)
    {
        try
        {
            Require(quest);
            if (!TriggerExists(trigger)) return DesignResult.Refused($"There is no area trigger {trigger} in the game's AreaTrigger.dbc (\"wds quests explore --new-trigger\" makes one at a position).");
            var t = T("areatrigger_involvedrelation");
            var other = Ed.Rows(t, RowFilter.Eq("id", trigger), 1).FirstOrDefault();
            if (other != null && L(t, other, "quest") != quest) return DesignResult.Refused($"Area trigger {trigger} already completes quest {L(t, other, "quest")}: a trigger serves one quest. Make a new one.");
            if (other == null)
            {
                var (_, r) = Ed.Add(t.Name, new (string, string?)[] { ("id", trigger.ToString(CultureInfo.InvariantCulture)), ("quest", quest.ToString(CultureInfo.InvariantCulture)) }, trigger, $"Explore objective of quest {quest}");
                if (!r.Ok) return DesignResult.Refused(r.Message);
            }
            long sf = Q(Require(quest), "SpecialFlags");
            if ((sf & 2) == 0) SetColumn(quest, "SpecialFlags", sf | 2);
            _touched.Add(quest);
            return DesignResult.Done($"Quest {quest} is completed by entering area trigger {trigger} ({TriggerPlace(trigger)}).");
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    /// <summary>A new area trigger (a sphere) at a position: AreaTrigger.dbc for the game (it tells the server when you
    /// enter it) and areatrigger_template for the server, with the same ID.</summary>
    public long NewAreaTrigger(long map, double x, double y, double z, double radius = 10)
    {
        long id = Session.NextId("AreaTrigger", HasTable("areatrigger_template") ? "areatrigger_template" : null, "id");
        var row = Session.AddRow("AreaTrigger", id);
        Session.SetNumber("AreaTrigger", row, "MapID", map);
        Session.SetFloat("AreaTrigger", row, "X", (float)x);
        Session.SetFloat("AreaTrigger", row, "Y", (float)y);
        Session.SetFloat("AreaTrigger", row, "Z", (float)z);
        Session.SetFloat("AreaTrigger", row, "Radius", (float)radius);
        if (HasTable("areatrigger_template"))
        {
            var t = T("areatrigger_template");
            var values = new List<(string, string?)> { ("id", id.ToString(CultureInfo.InvariantCulture)), ("map_id", map.ToString(CultureInfo.InvariantCulture)), ("x", F(x)), ("y", F(y)), ("z", F(z)), ("radius", F(radius)) };
            if (t.Has("build")) values.Add(("build", "5875"));
            var (_, r) = Ed.Add(t.Name, values.Where(v => t.Has(v.Item1)), id, $"Area trigger {id}");
            if (!r.Ok) throw new DesignException(r.Message);
        }
        Note("AreaTrigger.dbc changed: the game needs it to notice the new exploration point (\"Test in game\" or the release patch).");
        return id;
    }
}
