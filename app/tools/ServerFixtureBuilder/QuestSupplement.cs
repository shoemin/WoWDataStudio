using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WoWData.Core.ServerData;

/// <summary>
/// 0.13.0: the quest-chain supplement of the test database (app\WoWData.Tests\serverdb\quest_chains_fixture.sql): the
/// rows the quest-chain designer's tests and manual pictures need that the Valley of Trials sample does not have, read
/// with SELECT / SHOW only:
///  * whole quest chains, found by following every chain link both ways from a few seeds: the Warrior's Whirlwind-weapon
///    chains (The Islander 1718, Speak with Ruga 1823) and the Warlock's Dreadsteed (7562, 7581, 7629);
///  * their givers, enders and objective creatures / objects (templates and spawns), the items they need and give, the
///    loot rows that drop the needed items, their spells, scripts, area triggers and translations;
///  * the outposts of the Warrior's "Riposte Drill" worked example (Sentinel Hill, Westfall; the Crossroads, The Barrens):
///    their quest givers and the creatures the drill sends you to fight, with their spawns and loot.
/// Written as "INSERT IGNORE INTO `t` (columns) VALUES ...", one statement per line, loaded after world_fixture.sql.
/// </summary>
internal static class QuestSupplement
{
    private static readonly long[] Seeds = { 1718, 1823, 7562, 7581, 7629 };
    // Sentinel Hill: Gryan Stoutmantle, Captain Danuvin; the Crossroads: Thork, Sergra Darkthorn; the drill's foes:
    // Defias Highwayman (Westfall), Bristleback Hunter (The Barrens)
    private static readonly long[] ExtraCreatures = { 234, 821, 3429, 3338, 122, 3258 };

    public static int Write(Func<string, List<object?[]>> q, Func<string, HashSet<long>> ids, Func<IEnumerable<long>, string> inList, string outFile)
    {
        // the chains: every quest linked by PrevQuestId, NextQuestId, NextQuestInChain, BreadcrumbForQuestId or an exclusive group
        var quests = new HashSet<long>(Seeds);
        for (int pass = 0; pass < 12; pass++)
        {
            int before = quests.Count;
            var l = inList(quests);
            quests.UnionWith(ids($"SELECT ABS(PrevQuestId) FROM quest_template WHERE entry IN ({l}) UNION SELECT ABS(NextQuestId) FROM quest_template WHERE entry IN ({l}) " +
                $"UNION SELECT NextQuestInChain FROM quest_template WHERE entry IN ({l}) UNION SELECT BreadcrumbForQuestId FROM quest_template WHERE entry IN ({l})"));
            quests.UnionWith(ids($"SELECT entry FROM quest_template WHERE ABS(PrevQuestId) IN ({l}) OR ABS(NextQuestId) IN ({l}) OR NextQuestInChain IN ({l}) OR BreadcrumbForQuestId IN ({l})"));
            quests.UnionWith(ids($"SELECT q2.entry FROM quest_template q1 JOIN quest_template q2 ON q2.ExclusiveGroup = q1.ExclusiveGroup WHERE q1.entry IN ({l}) AND q1.ExclusiveGroup <> 0"));
            quests.Remove(0);
            if (quests.Count == before || quests.Count > 80) break;
        }
        var ql = inList(quests);
        var creatures = new HashSet<long>(ExtraCreatures);
        creatures.UnionWith(ids($"SELECT id FROM creature_questrelation WHERE quest IN ({ql}) UNION SELECT id FROM creature_involvedrelation WHERE quest IN ({ql})"));
        var gos = ids($"SELECT id FROM gameobject_questrelation WHERE quest IN ({ql}) UNION SELECT id FROM gameobject_involvedrelation WHERE quest IN ({ql})");
        foreach (var k in Enumerable.Range(1, 4))
        {
            creatures.UnionWith(ids($"SELECT ReqCreatureOrGOId{k} FROM quest_template WHERE entry IN ({ql}) AND ReqCreatureOrGOId{k} > 0"));
            gos.UnionWith(ids($"SELECT -ReqCreatureOrGOId{k} FROM quest_template WHERE entry IN ({ql}) AND ReqCreatureOrGOId{k} < 0"));
        }
        var items = new HashSet<long>();
        var needed = new HashSet<long>();
        foreach (var k in Enumerable.Range(1, 4))
        {
            needed.UnionWith(ids($"SELECT ReqItemId{k} FROM quest_template WHERE entry IN ({ql})"));
            items.UnionWith(ids($"SELECT RewItemId{k} FROM quest_template WHERE entry IN ({ql}) UNION SELECT ReqSourceId{k} FROM quest_template WHERE entry IN ({ql})"));
        }
        foreach (var k in Enumerable.Range(1, 6)) items.UnionWith(ids($"SELECT RewChoiceItemId{k} FROM quest_template WHERE entry IN ({ql})"));
        items.UnionWith(ids($"SELECT SrcItemId FROM quest_template WHERE entry IN ({ql})"));
        needed.Remove(0);
        items.UnionWith(needed);
        // who drops the needed items: the loot ids, the creatures and chests that use them
        var nl = inList(needed);
        // at most 4 droppers (creatures, chests) per needed item: the ones with the highest chance
        var lootIds = new HashSet<long>();
        var goLoot = new HashSet<long>();
        foreach (var it in needed)
        {
            var l = ids($"SELECT entry FROM (SELECT l.entry, MAX(ABS(l.ChanceOrQuestChance)) c FROM creature_loot_template l JOIN creature_template t ON t.loot_id = l.entry WHERE l.item = {it} GROUP BY l.entry ORDER BY c DESC, l.entry LIMIT 4) x");
            lootIds.UnionWith(l);
            goLoot.UnionWith(ids($"SELECT entry FROM (SELECT l.entry FROM gameobject_loot_template l JOIN gameobject_template t ON t.data1 = l.entry AND t.type IN (3,25) WHERE l.item = {it} GROUP BY l.entry ORDER BY l.entry LIMIT 4) x"));
        }
        var droppers = ids($"SELECT DISTINCT entry FROM creature_template WHERE loot_id IN ({inList(lootIds)})");
        creatures.UnionWith(droppers);
        gos.UnionWith(ids($"SELECT DISTINCT entry FROM gameobject_template WHERE type IN (3,25) AND data1 IN ({inList(goLoot)})"));
        // vendors that sell the needed items, and the spells that create them
        var vendorCreatures = ids($"SELECT DISTINCT entry FROM npc_vendor WHERE item IN ({nl})");
        var vendorTpl = ids($"SELECT DISTINCT entry FROM npc_vendor_template WHERE item IN ({nl})");
        vendorCreatures.UnionWith(ids($"SELECT DISTINCT entry FROM creature_template WHERE vendor_id IN ({inList(vendorTpl)})"));
        creatures.UnionWith(vendorCreatures);
        var makers = new HashSet<long>();
        foreach (var k in Enumerable.Range(1, 3)) makers.UnionWith(ids($"SELECT DISTINCT entry FROM spell_template WHERE effect{k} = 24 AND effectItemType{k} IN ({nl})"));
        // the drill's foes' own loot (all of it)
        var foeLoot = ids($"SELECT DISTINCT loot_id FROM creature_template WHERE entry IN ({inList(ExtraCreatures)}) AND loot_id <> 0");
        // items that start a quest of the chains
        items.UnionWith(ids($"SELECT entry FROM item_template WHERE start_quest IN ({ql})"));
        items.Remove(0);
        creatures.Remove(0);
        gos.Remove(0);
        var spells = new HashSet<long>();
        foreach (var col in new[] { "RewSpell", "RewSpellCast", "SrcSpell", "ReqSpellCast1", "ReqSpellCast2", "ReqSpellCast3", "ReqSpellCast4" })
            spells.UnionWith(ids($"SELECT {col} FROM quest_template WHERE entry IN ({ql})"));
        foreach (var k in Enumerable.Range(1, 3)) spells.UnionWith(ids($"SELECT DISTINCT effectTriggerSpell{k} FROM spell_template WHERE entry IN ({inList(spells)}) AND effectTriggerSpell{k} <> 0"));
        spells.UnionWith(makers);
        spells.Remove(0);
        var startScripts = ids($"SELECT StartScript FROM quest_template WHERE entry IN ({ql})");
        var endScripts = ids($"SELECT CompleteScript FROM quest_template WHERE entry IN ({ql})");
        var cl = inList(creatures);
        var gl = inList(gos);
        // spawns: every spawn of the givers, enders and objectives (at most 60 per creature)
        var spawnGuids = new HashSet<long>();
        foreach (var c in creatures)
            spawnGuids.UnionWith(ids($"SELECT guid FROM creature WHERE id = {c} ORDER BY guid LIMIT {(droppers.Contains(c) || vendorCreatures.Contains(c) ? 10 : 60)}"));
        var goGuids = new HashSet<long>();
        foreach (var g in gos)
            goGuids.UnionWith(ids($"SELECT guid FROM gameobject WHERE id = {g} ORDER BY guid LIMIT 10"));
        var gossipIds = ids($"SELECT gossip_menu_id FROM creature_template WHERE entry IN ({cl}) AND gossip_menu_id <> 0");
        var npcTexts = ids($"SELECT text_id FROM gossip_menu WHERE entry IN ({inList(gossipIds)})");
        var displays = new HashSet<long>();
        foreach (var k in Enumerable.Range(1, 4)) displays.UnionWith(ids($"SELECT display_id{k} FROM creature_template WHERE entry IN ({cl})"));
        displays.Remove(0);
        var il = inList(items);
        var where = new List<(string Table, string Where)>
        {
            ("quest_template", $"entry IN ({ql})"),
            ("creature_questrelation", $"quest IN ({ql})"),
            ("creature_involvedrelation", $"quest IN ({ql})"),
            ("gameobject_questrelation", $"quest IN ({ql})"),
            ("gameobject_involvedrelation", $"quest IN ({ql})"),
            ("areatrigger_involvedrelation", $"quest IN ({ql})"),
            ("locales_quest", $"entry IN ({ql})"),
            ("quest_start_scripts", $"id IN ({inList(startScripts)})"),
            ("quest_end_scripts", $"id IN ({inList(endScripts)})"),
            ("creature_template", $"entry IN ({cl})"),
            ("locales_creature", $"entry IN ({cl})"),
            ("creature", $"guid IN ({inList(spawnGuids)})"),
            ("creature_addon", $"guid IN ({inList(spawnGuids)})"),
            ("creature_display_info_addon", $"display_id IN ({inList(displays)})"),
            ("gameobject_template", $"entry IN ({gl})"),
            ("locales_gameobject", $"entry IN ({gl})"),
            ("gameobject", $"guid IN ({inList(goGuids)})"),
            ("creature_loot_template", $"(entry IN ({inList(lootIds)}) AND item IN ({nl})) OR entry IN ({inList(foeLoot)})"),
            ("gameobject_loot_template", $"entry IN ({inList(goLoot)}) AND item IN ({nl})"),
            ("item_template", $"entry IN ({il})"),
            ("locales_item", $"entry IN ({il})"),
            ("spell_template", $"entry IN ({inList(spells)})"),
            ("npc_vendor", $"item IN ({nl})"),
            ("npc_vendor_template", $"entry IN ({inList(vendorTpl)}) AND item IN ({nl})"),
            ("gossip_menu", $"entry IN ({inList(gossipIds)})"),
            ("gossip_menu_option", $"menu_id IN ({inList(gossipIds)})"),
            ("npc_text", $"ID IN ({inList(npcTexts)})"),
        };
        var sb = new StringBuilder();
        sb.Append("-- WoW Data Studio 0.13.0: the quest-chain supplement of the isolated test database (made by app\\tools\\ServerFixtureBuilder --supplement,\n");
        sb.Append("-- read-only: SELECT / SHOW). Whole quest chains (the Warrior's Whirlwind-weapon chains, the Warlock's Dreadsteed), their NPCs,\n");
        sb.Append("-- items, loot, spells and scripts, and the outposts of the \"Riposte Drill\" example (Sentinel Hill, the Crossroads).\n");
        sb.Append("-- Loaded after world_fixture.sql; test data only, never shipped. One statement per line.\n");
        int total = 0;
        foreach (var (t, w) in where)
        {
            var cols = q($"SHOW COLUMNS FROM `{t}`").Select(r => (Name: Convert.ToString(r[0], CultureInfo.InvariantCulture)!, Type: Convert.ToString(r[1], CultureInfo.InvariantCulture)!)).ToList();
            var wcols = cols.Select((x, i) => new WorldColumn
            {
                Name = x.Name, Ordinal = i + 1, ColumnType = x.Type,
                DataType = Regex.Match(x.Type, @"^\w+").Value.ToLowerInvariant(), Unsigned = x.Type.Contains("unsigned"),
            }).ToList();
            var rows = q($"SELECT * FROM `{t}` WHERE {w}");
            var names = string.Join(",", cols.Select(c => $"`{c.Name}`"));
            foreach (var chunk in rows.Chunk(100))
            {
                sb.Append($"INSERT IGNORE INTO `{t}` ({names}) VALUES ");
                sb.Append(string.Join(",", chunk.Select(r => "(" + string.Join(",", r.Select((v, i) => WorldValue.Sql(wcols[i], WorldValue.FromDb(v, wcols[i])))) + ")")));
                sb.Append(";\n");
            }
            total += rows.Count;
            Console.Error.WriteLine($"  {t}: {rows.Count} rows");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Wrote {outFile}: {quests.Count} quests ({string.Join(",", quests.OrderBy(x => x))}), {total} rows, {new FileInfo(outFile).Length / 1024} KB.");
        return 0;
    }
}
