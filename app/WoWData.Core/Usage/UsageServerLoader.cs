using MySqlConnector;
using WoWData.Core.Index;

namespace WoWData.Core.Usage;

public sealed record SrvCreatureExt(int Entry, int Faction, int Rank, int NpcFlags, int Type, int LootId, int PickpocketId, int SkinningId,
    int VendorId, int TrainerId, int TrainerType, int TrainerClass, int SpellListId, int[] Spells);
public sealed record SrvSpawn(int Entry, int Map, float X, float Y, float Z);
public sealed record SrvGameObjectExt(int Entry, int Type, int Data0, int Data1, int Data2, int Data3);
public sealed record SrvLoot(string Source, int Entry, int Item, float Chance, int Group, int MinCountOrRef, int MaxCount);
public sealed record SrvQuest(int Id, string Title, int Level, int MinLevel, int[] ReqItem, int[] ReqItemCount, int[] ReqNpcOrGo, int[] ReqNpcOrGoCount,
    int[] ReqSource, int[] RewChoice, int[] RewItem, int SrcItem);
public sealed record SrvItemExt(int Entry, int ItemLevel, int RequiredLevel, int SetId, int StartQuest, int Bonding, int[] Spells, int[] Triggers);

/// <summary>What the "Used in game" tables read from the server's world database (SELECT only).</summary>
public sealed class UsageServerData
{
    public string Schema { get; set; } = "";
    public List<SrvCreatureExt> Creatures { get; } = new();
    public List<SrvSpawn> CreatureSpawns { get; } = new();
    public List<SrvGameObjectExt> GameObjects { get; } = new();
    public List<SrvSpawn> GameObjectSpawns { get; } = new();
    public List<SrvLoot> Loot { get; } = new();
    /// <summary>(creature entry or vendor template id, item, max count, is template).</summary>
    public List<(int Entry, int Item, int MaxCount, bool Template)> Vendors { get; } = new();
    /// <summary>(creature entry or trainer template id, spell, is template).</summary>
    public List<(int Entry, int Spell, bool Template)> Trainers { get; } = new();
    public List<SrvQuest> Quests { get; } = new();
    /// <summary>(relation: "creature start" / "creature end" / "gameobject start" / "gameobject end", npc or object entry, quest).</summary>
    public List<(string Relation, int Entry, int Quest)> QuestRelations { get; } = new();
    public List<SrvItemExt> Items { get; } = new();
    /// <summary>creature_spells lists: (list id, spell).</summary>
    public List<(int List, int Spell)> SpellLists { get; } = new();
}

/// <summary>
/// Loads the server data of the "Used in game" panel: creature/game object template extras, spawn positions, loot
/// (creature, game object, pickpocketing, skinning, item, fishing, disenchant and reference tables), vendors, trainers,
/// quests and their givers/enders, item extras and creature spell lists.
/// Schemas: VMaNGOS (template tables have a `patch` column: the newest row with patch &lt;= 10 = content patch 1.12, as the
/// project's scripts do; spawn, loot and relation rows have patch_min / patch_max: rows valid at patch 10; trainer rows
/// have build_min / build_max: valid for build 5875) and CMaNGOS / MaNGOS (no patch columns; CamelCase column names).
/// Every statement goes through <see cref="ServerDb.QueryAsync"/>, which refuses anything but SELECT/SHOW.
/// </summary>
public static class UsageServerLoader
{
    public const int ContentPatch = 10;
    public const int ClientBuild = 5875;

    private static int I(object? v) => v == null ? 0 : Convert.ToInt32(v);
    private static float F(object? v) => v == null ? 0 : Convert.ToSingle(v);
    private static string S(object? v) => v == null ? "" : Convert.ToString(v) ?? "";

    private static string Pick(HashSet<string> cols, params string[] options)
    {
        foreach (var o in options) if (cols.Contains(o.ToLowerInvariant())) return o;
        return "";
    }

    private static string Col(string name) => name.Length == 0 ? "0" : $"t.`{name}`";

    /// <summary>FROM clause: the newest row per entry with patch &lt;= 10 when the table has a patch column.</summary>
    private static string Latest(string table, HashSet<string> cols, string entry = "entry") => cols.Contains("patch")
        ? $"FROM `{table}` t JOIN (SELECT `{entry}` e, MAX(patch) p FROM `{table}` WHERE patch <= {ContentPatch} GROUP BY `{entry}`) m ON t.`{entry}` = m.e AND t.patch = m.p"
        : $"FROM `{table}` t";

    /// <summary>WHERE clause for rows valid at patch 10 / build 5875 (true when the table has no such columns).</summary>
    private static string Valid(HashSet<string> cols)
    {
        var w = new List<string>();
        if (cols.Contains("patch_min")) w.Add($"t.patch_min <= {ContentPatch}");
        if (cols.Contains("patch_max")) w.Add($"t.patch_max >= {ContentPatch}");
        if (cols.Contains("build_min")) w.Add($"t.build_min <= {ClientBuild}");
        if (cols.Contains("build_max")) w.Add($"(t.build_max = 0 OR t.build_max >= {ClientBuild})");
        return w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w);
    }

    public static async Task<UsageServerData> LoadAsync(ServerDbSettings settings, CancellationToken ct = default)
    {
        var db = new ServerDb(settings);
        if (!settings.Enabled) throw new ServerDbException("the server database is turned off in Settings");
        await using var c = await db.ConnectAsync(ct).ConfigureAwait(false);
        var res = new UsageServerData();
        Task<HashSet<string>> Cols(string t) => db.TableColumnsAsync(c, t, ct);
        Task<List<object?[]>> Q(string sql) => db.QueryAsync(c, sql, ct);

        // ---------------------------------------------------------------- creature_template extras
        var cc = await Cols("creature_template").ConfigureAwait(false);
        if (cc.Count == 0) throw new ServerDbException($"{settings.Describe()} has no creature_template table");
        res.Schema = cc.Contains("patch") ? "VMaNGOS" : "CMaNGOS/MaNGOS";
        string entry = Pick(cc, "entry", "Entry");
        var cols = new[]
        {
            Pick(cc, "faction", "FactionAlliance", "faction_A"), Pick(cc, "rank", "Rank"), Pick(cc, "npc_flags", "NpcFlags", "npcflag"),
            Pick(cc, "type", "CreatureType"), Pick(cc, "loot_id", "LootId", "lootid"), Pick(cc, "pickpocket_loot_id", "PickpocketLootId", "pickpocketloot"),
            Pick(cc, "skinning_loot_id", "SkinningLootId", "skinloot"), Pick(cc, "vendor_id", "VendorTemplateId"), Pick(cc, "trainer_id", "TrainerTemplateId"),
            Pick(cc, "trainer_type", "TrainerType"), Pick(cc, "trainer_class", "TrainerClass"), Pick(cc, "spell_list_id", "SpellListId"),
            Pick(cc, "spell_id1", "spell1"), Pick(cc, "spell_id2", "spell2"), Pick(cc, "spell_id3", "spell3"), Pick(cc, "spell_id4", "spell4"),
        };
        foreach (var r in await Q($"SELECT t.`{entry}`, {string.Join(", ", cols.Select(Col))} {Latest("creature_template", cc, entry)}").ConfigureAwait(false))
            res.Creatures.Add(new SrvCreatureExt(I(r[0]), I(r[1]), I(r[2]), I(r[3]), I(r[4]), I(r[5]), I(r[6]), I(r[7]), I(r[8]), I(r[9]), I(r[10]), I(r[11]), I(r[12]),
                new[] { I(r[13]), I(r[14]), I(r[15]), I(r[16]) }));

        // ---------------------------------------------------------------- spawns (VMaNGOS: id plus up to four alternative entries id2..id5)
        async Task Spawns(string table, List<SrvSpawn> into)
        {
            var sc = await Cols(table).ConfigureAwait(false);
            if (!sc.Contains("id") || !sc.Contains("map") || !sc.Contains("position_x")) return;
            var ids = new[] { "id", "id2", "id3", "id4", "id5" }.Where(sc.Contains).ToList();
            foreach (var r in await Q($"SELECT t.map, t.position_x, t.position_y, t.position_z, {string.Join(", ", ids.Select(i => "t." + i))} FROM `{table}` t{Valid(sc)}").ConfigureAwait(false))
            {
                var seen = new HashSet<int>();
                for (int i = 4; i < r.Length; i++)
                {
                    int id = I(r[i]);
                    if (id > 0 && seen.Add(id)) into.Add(new SrvSpawn(id, I(r[0]), F(r[1]), F(r[2]), F(r[3])));
                }
            }
        }
        await Spawns("creature", res.CreatureSpawns).ConfigureAwait(false);
        await Spawns("gameobject", res.GameObjectSpawns).ConfigureAwait(false);

        // ---------------------------------------------------------------- game object extras
        var gc = await Cols("gameobject_template").ConfigureAwait(false);
        if (gc.Count > 0)
            foreach (var r in await Q($"SELECT t.`{Pick(gc, "entry")}`, {Col(Pick(gc, "type"))}, {Col(Pick(gc, "data0"))}, {Col(Pick(gc, "data1"))}, {Col(Pick(gc, "data2"))}, {Col(Pick(gc, "data3"))} {Latest("gameobject_template", gc)}").ConfigureAwait(false))
                res.GameObjects.Add(new SrvGameObjectExt(I(r[0]), I(r[1]), I(r[2]), I(r[3]), I(r[4]), I(r[5])));

        // ---------------------------------------------------------------- loot
        foreach (var (table, source) in new[]
                 {
                     ("creature_loot_template", "creature"), ("gameobject_loot_template", "gameobject"), ("pickpocketing_loot_template", "pickpocket"),
                     ("skinning_loot_template", "skinning"), ("item_loot_template", "item"), ("fishing_loot_template", "fishing"),
                     ("disenchant_loot_template", "disenchant"), ("reference_loot_template", "reference"),
                 })
        {
            var lc = await Cols(table).ConfigureAwait(false);
            if (!lc.Contains("entry") || !lc.Contains("item")) continue;
            string chance = Pick(lc, "ChanceOrQuestChance", "chance"), group = Pick(lc, "groupid"), min = Pick(lc, "mincountOrRef", "mincount"), max = Pick(lc, "maxcount");
            foreach (var r in await Q($"SELECT t.entry, t.item, {Col(chance)}, {Col(group)}, {Col(min)}, {Col(max)} FROM `{table}` t{Valid(lc)}").ConfigureAwait(false))
                res.Loot.Add(new SrvLoot(source, I(r[0]), I(r[1]), F(r[2]), I(r[3]), I(r[4]), I(r[5])));
        }

        // ---------------------------------------------------------------- vendors and trainers
        foreach (var (table, template) in new[] { ("npc_vendor", false), ("npc_vendor_template", true) })
        {
            var vc = await Cols(table).ConfigureAwait(false);
            if (!vc.Contains("entry") || !vc.Contains("item")) continue;
            foreach (var r in await Q($"SELECT t.entry, t.item, {Col(Pick(vc, "maxcount"))} FROM `{table}` t{Valid(vc)}").ConfigureAwait(false))
                res.Vendors.Add((I(r[0]), I(r[1]), I(r[2]), template));
        }
        foreach (var (table, template) in new[] { ("npc_trainer", false), ("npc_trainer_template", true) })
        {
            var tc = await Cols(table).ConfigureAwait(false);
            if (!tc.Contains("entry") || !tc.Contains("spell")) continue;
            foreach (var r in await Q($"SELECT t.entry, t.spell FROM `{table}` t{Valid(tc)}").ConfigureAwait(false))
                res.Trainers.Add((I(r[0]), I(r[1]), template));
        }

        // ---------------------------------------------------------------- quests
        var qc = await Cols("quest_template").ConfigureAwait(false);
        if (qc.Count > 0)
        {
            string N(string name, int i) => Col(Pick(qc, name + i));
            var sel = new List<string>
            {
                $"t.`{Pick(qc, "entry")}`", Col(Pick(qc, "Title")), Col(Pick(qc, "QuestLevel")), Col(Pick(qc, "MinLevel")), Col(Pick(qc, "SrcItemId")),
            };
            for (int i = 1; i <= 4; i++) sel.Add(N("ReqItemId", i));
            for (int i = 1; i <= 4; i++) sel.Add(N("ReqItemCount", i));
            for (int i = 1; i <= 4; i++) sel.Add(N("ReqCreatureOrGOId", i));
            for (int i = 1; i <= 4; i++) sel.Add(N("ReqCreatureOrGOCount", i));
            for (int i = 1; i <= 4; i++) sel.Add(N("ReqSourceId", i));
            for (int i = 1; i <= 6; i++) sel.Add(N("RewChoiceItemId", i));
            for (int i = 1; i <= 4; i++) sel.Add(N("RewItemId", i));
            foreach (var r in await Q($"SELECT {string.Join(", ", sel)} {Latest("quest_template", qc, Pick(qc, "entry"))}").ConfigureAwait(false))
            {
                int[] A(int start, int n) => Enumerable.Range(start, n).Select(i => I(r[i])).ToArray();
                res.Quests.Add(new SrvQuest(I(r[0]), S(r[1]), I(r[2]), I(r[3]), A(5, 4), A(9, 4), A(13, 4), A(17, 4), A(21, 4), A(25, 6), A(31, 4), I(r[4])));
            }
            foreach (var (table, rel) in new[]
                     {
                         ("creature_questrelation", "creature start"), ("creature_involvedrelation", "creature end"),
                         ("gameobject_questrelation", "gameobject start"), ("gameobject_involvedrelation", "gameobject end"),
                     })
            {
                var rc = await Cols(table).ConfigureAwait(false);
                if (!rc.Contains("id") || !rc.Contains("quest")) continue;
                foreach (var r in await Q($"SELECT t.id, t.quest FROM `{table}` t{Valid(rc)}").ConfigureAwait(false))
                    res.QuestRelations.Add((rel, I(r[0]), I(r[1])));
            }
        }

        // ---------------------------------------------------------------- item extras
        var ic = await Cols("item_template").ConfigureAwait(false);
        if (ic.Count > 0)
        {
            var sel = new List<string>
            {
                $"t.`{Pick(ic, "entry")}`", Col(Pick(ic, "item_level", "ItemLevel")), Col(Pick(ic, "required_level", "RequiredLevel")),
                Col(Pick(ic, "set_id", "itemset")), Col(Pick(ic, "start_quest", "startquest")), Col(Pick(ic, "bonding")),
            };
            for (int i = 1; i <= 5; i++) sel.Add(Col(Pick(ic, "spellid_" + i)));
            for (int i = 1; i <= 5; i++) sel.Add(Col(Pick(ic, "spelltrigger_" + i)));
            foreach (var r in await Q($"SELECT {string.Join(", ", sel)} {Latest("item_template", ic, Pick(ic, "entry"))}").ConfigureAwait(false))
                res.Items.Add(new SrvItemExt(I(r[0]), I(r[1]), I(r[2]), I(r[3]), I(r[4]), I(r[5]),
                    Enumerable.Range(6, 5).Select(i => I(r[i])).ToArray(), Enumerable.Range(11, 5).Select(i => I(r[i])).ToArray()));
        }

        // ---------------------------------------------------------------- creature spell lists (VMaNGOS)
        var sl = await Cols("creature_spells").ConfigureAwait(false);
        if (sl.Contains("entry"))
        {
            var spellCols = Enumerable.Range(1, 8).Select(i => Pick(sl, "spellId_" + i)).Where(s => s.Length > 0).ToList();
            if (spellCols.Count > 0)
                foreach (var r in await Q($"SELECT t.entry, {string.Join(", ", spellCols.Select(Col))} FROM `creature_spells` t").ConfigureAwait(false))
                    for (int i = 1; i < r.Length; i++)
                        if (I(r[i]) > 0) res.SpellLists.Add((I(r[0]), I(r[i])));
        }
        return res;
    }
}
