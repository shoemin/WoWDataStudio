using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using WoWData.Core.ServerData;

// WoW Data Studio 0.9.0: makes the isolated test database of the server data tests.
//
//   dotnet run --project app\tools\ServerFixtureBuilder -- --server-folder "C:\...\Server" --out app\WoWData.Tests\serverdb\world_fixture.sql
//
// The live world database is NEVER opened for writing and never started: the tool COPIES the files of the world
// database ("mangos" folder, the InnoDB files and the system tables) out of <server>\mysql5\data into a temporary folder,
// starts a throwaway mysqld on that copy (own port, 127.0.0.1 only), reads the schema of every table (SHOW CREATE TABLE)
// and a small, connected sample of rows (Valley of Trials: its creatures, spawns, loot, vendors, trainers, quests,
// objects, the items and spells they use; every row of the small world tables), writes them as SQL (one statement per
// line) and deletes the copy. The world server must not be running (its files would be copied mid-write): the tool
// refuses then. With --live-port <port> it instead reads a running server read-only (SELECT / SHOW only).
// 0.13.0: with --supplement it writes only the quest-chain supplement (QuestSupplement.cs, quest_chains_fixture.sql).

static string Arg(string[] a, string name, string? def = null)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def ?? throw new ArgumentException($"{name} is required");
}

var server = Arg(args, "--server-folder");
var outFile = Path.GetFullPath(Arg(args, "--out"));
var livePort = args.Contains("--live-port") ? int.Parse(Arg(args, "--live-port"), CultureInfo.InvariantCulture) : 0;
const string Db = "mangos";

ThrowawayMySql? copy = null;
string connStr;
if (livePort > 0)
{
    Console.Error.WriteLine($"Reading the running server on port {livePort} read-only (user WDS_FIXTURE_USER or root; password from WDS_FIXTURE_PASSWORD).");
    connStr = $"Server=127.0.0.1;Port={livePort};User ID={Environment.GetEnvironmentVariable("WDS_FIXTURE_USER") ?? "root"};Password={Environment.GetEnvironmentVariable("WDS_FIXTURE_PASSWORD") ?? ""};Database={Db};SslMode=None;CharacterSet=utf8;Default Command Timeout=600";
}
else
{
    if (System.Diagnostics.Process.GetProcessesByName("mysqld").Length > 0)
    {
        Console.Error.WriteLine("A mysqld is running: stop the world server's database first (its files must not be copied while it writes), or use --live-port.");
        return 4;
    }
    var mysqld = ThrowawayMySql.FindMysqld(server) ?? throw new FileNotFoundException("no mysqld.exe under " + server);
    var live = Path.Combine(server, "mysql5", "data");
    Console.Error.WriteLine($"Copying the world database files out of {live} (read only) and starting a throwaway mysqld on the copy...");
    copy = ThrowawayMySql.Start(mysqld, live, new[] { Db, "mysql", "ibdata1", "ib_logfile0", "ib_logfile1" });
    connStr = $"Server=127.0.0.1;Port={copy.Port};User ID=root;Password=;Database={Db};SslMode=None;CharacterSet=utf8;Default Command Timeout=600";
}

try
{
    using var c = new MySqlConnection(connStr);
    c.Open();

    List<object?[]> Q(string sql)
    {
        if (!Regex.IsMatch(sql, @"^\s*(SELECT|SHOW)\b", RegexOptions.IgnoreCase)) throw new InvalidOperationException("read-only");
        using var cmd = new MySqlCommand(sql, c);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    HashSet<long> Ids(string sql) => Q(sql).Where(r => r[0] != null).Select(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)).ToHashSet();
    string In(IEnumerable<long> ids) => ids.Any() ? string.Join(",", ids.OrderBy(x => x)) : "-999999999";

    // 0.13.0: --supplement quests: only the extra rows of the quest-chain tests and pictures (no schema), as INSERT IGNORE
    if (args.Contains("--supplement")) return QuestSupplement.Write(Q, Ids, In, outFile);

    // ---- the sample: Valley of Trials (Kalimdor) and what it uses
    const string Box = "map=1 AND position_x BETWEEN -800 AND -300 AND position_y BETWEEN -4500 AND -4000";
    var creatures = Ids($"SELECT DISTINCT id FROM creature WHERE {Box}");
    creatures.UnionWith(new long[] { 3098, 3143, 3158, 3153, 10176, 3145, 3155, 3156, 3157, 3159, 3160 });
    var quests = Ids($"SELECT quest FROM creature_questrelation WHERE id IN ({In(creatures)}) UNION SELECT quest FROM creature_involvedrelation WHERE id IN ({In(creatures)})");
    var gos = Ids($"SELECT DISTINCT id FROM gameobject WHERE {Box}");
    quests.UnionWith(Ids($"SELECT quest FROM gameobject_questrelation WHERE id IN ({In(gos)}) UNION SELECT quest FROM gameobject_involvedrelation WHERE id IN ({In(gos)})"));
    // the chains of these quests (one step each way)
    quests.UnionWith(Ids($"SELECT NextQuestInChain FROM quest_template WHERE entry IN ({In(quests)}) UNION SELECT ABS(PrevQuestId) FROM quest_template WHERE entry IN ({In(quests)}) UNION SELECT ABS(NextQuestId) FROM quest_template WHERE entry IN ({In(quests)})"));
    quests.Remove(0);
    // every giver / ender of these quests (their templates, not their spawns)
    creatures.UnionWith(Ids($"SELECT id FROM creature_questrelation WHERE quest IN ({In(quests)}) UNION SELECT id FROM creature_involvedrelation WHERE quest IN ({In(quests)})"));
    gos.UnionWith(Ids($"SELECT id FROM gameobject_questrelation WHERE quest IN ({In(quests)}) UNION SELECT id FROM gameobject_involvedrelation WHERE quest IN ({In(quests)})"));
    // quest objectives that are creatures / objects
    foreach (var k in Enumerable.Range(1, 4))
    {
        creatures.UnionWith(Ids($"SELECT ReqCreatureOrGOId{k} FROM quest_template WHERE entry IN ({In(quests)}) AND ReqCreatureOrGOId{k} > 0"));
        gos.UnionWith(Ids($"SELECT -ReqCreatureOrGOId{k} FROM quest_template WHERE entry IN ({In(quests)}) AND ReqCreatureOrGOId{k} < 0"));
    }
    long[] Col(string table, string col, IEnumerable<long> ids, string idCol = "entry") => Ids($"SELECT DISTINCT {col} FROM {table} WHERE {idCol} IN ({In(ids)}) AND {col} <> 0").ToArray();
    var lootIds = Col("creature_template", "loot_id", creatures).ToHashSet();
    var pickIds = Col("creature_template", "pickpocket_loot_id", creatures).ToHashSet();
    var skinIds = Col("creature_template", "skinning_loot_id", creatures).ToHashSet();
    var vendorTpl = Col("creature_template", "vendor_id", creatures).ToHashSet();
    var trainerTpl = Col("creature_template", "trainer_id", creatures).ToHashSet();
    var equipIds = Col("creature_template", "equipment_id", creatures).ToHashSet();
    var gossipIds = Col("creature_template", "gossip_menu_id", creatures).ToHashSet();
    var displays = new HashSet<long>();
    foreach (var k in Enumerable.Range(1, 4)) displays.UnionWith(Col("creature_template", "display_id" + k, creatures));
    var goLoot = Ids($"SELECT DISTINCT data1 FROM gameobject_template WHERE entry IN ({In(gos)}) AND type IN (3,25) AND data1 <> 0");
    // reference loot: the references of these loot tables, one level (world drops are large: the first 40 rows each)
    var refs = new HashSet<long>();
    foreach (var (t, ids) in new[] { ("creature_loot_template", lootIds), ("pickpocketing_loot_template", pickIds), ("skinning_loot_template", skinIds), ("gameobject_loot_template", goLoot) })
        refs.UnionWith(Ids($"SELECT DISTINCT -mincountOrRef FROM {t} WHERE entry IN ({In(ids)}) AND mincountOrRef < 0"));

    var items = new HashSet<long> { 25, 2589, 6948, 117, 159, 2070, 4540, 4604 };
    foreach (var (t, ids) in new[] { ("creature_loot_template", lootIds), ("pickpocketing_loot_template", pickIds), ("skinning_loot_template", skinIds), ("gameobject_loot_template", goLoot) })
        items.UnionWith(Ids($"SELECT DISTINCT item FROM {t} WHERE entry IN ({In(ids)}) AND mincountOrRef > 0"));
    items.UnionWith(Ids($"SELECT item FROM (SELECT item FROM reference_loot_template WHERE entry IN ({In(refs)}) ORDER BY entry, item LIMIT 400) x"));
    items.UnionWith(Ids($"SELECT item FROM npc_vendor WHERE entry IN ({In(creatures)}) UNION SELECT item FROM npc_vendor_template WHERE entry IN ({In(vendorTpl)})"));
    foreach (var k in Enumerable.Range(1, 3)) items.UnionWith(Col("creature_equip_template", "item" + k, equipIds));
    foreach (var col in Enumerable.Range(1, 4).SelectMany(k => new[] { "ReqItemId" + k, "RewItemId" + k, "ReqSourceId" + k }).Concat(Enumerable.Range(1, 6).Select(k => "RewChoiceItemId" + k)).Append("SrcItemId"))
        items.UnionWith(Col("quest_template", col, quests));
    items.Remove(0);
    var disenchant = Col("item_template", "disenchant_id", items).ToHashSet();
    var pages = Col("item_template", "page_text", items).ToHashSet();
    var itemDisplays = Col("item_template", "display_id", items).ToHashSet();

    var spells = new HashSet<long> { 133, 143, 145, 3140, 8400, 8401, 8402, 10148, 10149, 10150, 10151, 25306, 116, 168, 1459, 2136, 78, 100, 772, 6673 };
    spells.UnionWith(Ids($"SELECT spell FROM npc_trainer WHERE entry IN ({In(creatures)}) UNION SELECT spell FROM npc_trainer_template WHERE entry IN ({In(trainerTpl)})"));
    foreach (var k in Enumerable.Range(1, 5)) spells.UnionWith(Col("item_template", "spellid_" + k, items));
    foreach (var k in Enumerable.Range(1, 4)) spells.UnionWith(Col("creature_template", "spell_id" + k, creatures));
    foreach (var col in new[] { "RewSpell", "RewSpellCast", "SrcSpell", "ReqSpellCast1", "ReqSpellCast2", "ReqSpellCast3", "ReqSpellCast4" }) spells.UnionWith(Col("quest_template", col, quests));
    // the spells that "learn" spells teach
    foreach (var k in Enumerable.Range(1, 3)) spells.UnionWith(Ids($"SELECT DISTINCT effectTriggerSpell{k} FROM spell_template WHERE entry IN ({In(spells)}) AND effectTriggerSpell{k} <> 0"));
    spells.Remove(0);
    var npcTexts = Ids($"SELECT text_id FROM gossip_menu WHERE entry IN ({In(gossipIds)})");
    var broadcast = new HashSet<long>();
    foreach (var k in Enumerable.Range(0, 8)) broadcast.UnionWith(Col("npc_text", "BroadcastTextID" + k, npcTexts, "ID"));
    broadcast.UnionWith(Ids($"SELECT option_broadcast_text FROM gossip_menu_option WHERE menu_id IN ({In(gossipIds)}) UNION SELECT box_broadcast_text FROM gossip_menu_option WHERE menu_id IN ({In(gossipIds)})"));
    broadcast.Remove(0);
    var spawnGuids = Ids($"SELECT guid FROM creature WHERE {Box}");
    var conditions = new HashSet<long>();

    var where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["creature_template"] = $"entry IN ({In(creatures)})",
        ["creature"] = Box,
        ["creature_addon"] = $"guid IN ({In(spawnGuids)})",
        ["creature_equip_template"] = $"entry IN ({In(equipIds)})",
        ["creature_display_info_addon"] = $"display_id IN ({In(displays)})",
        ["creature_onkill_reputation"] = $"creature_id IN ({In(creatures)})",
        ["creature_questrelation"] = $"quest IN ({In(quests)})",
        ["creature_involvedrelation"] = $"quest IN ({In(quests)})",
        ["gameobject_template"] = $"entry IN ({In(gos)})",
        ["gameobject"] = Box,
        ["gameobject_questrelation"] = $"quest IN ({In(quests)})",
        ["gameobject_involvedrelation"] = $"quest IN ({In(quests)})",
        ["gameobject_loot_template"] = $"entry IN ({In(goLoot)})",
        ["quest_template"] = $"entry IN ({In(quests)})",
        ["creature_loot_template"] = $"entry IN ({In(lootIds)})",
        ["pickpocketing_loot_template"] = $"entry IN ({In(pickIds)})",
        ["skinning_loot_template"] = $"entry IN ({In(skinIds)})",
        ["reference_loot_template"] = $"entry IN ({In(refs)}) AND item IN ({In(items)})",
        ["item_loot_template"] = $"entry IN ({In(items)})",
        ["disenchant_loot_template"] = $"entry IN ({In(disenchant)})",
        ["item_template"] = $"entry IN ({In(items)})",
        ["item_display_info"] = $"ID IN ({In(itemDisplays)})",
        ["page_text"] = $"entry IN ({In(pages)})",
        ["npc_vendor"] = $"entry IN ({In(creatures)})",
        ["npc_vendor_template"] = $"entry IN ({In(vendorTpl)})",
        ["npc_trainer"] = $"entry IN ({In(creatures)})",
        ["npc_trainer_template"] = $"entry IN ({In(trainerTpl)})",
        ["gossip_menu"] = $"entry IN ({In(gossipIds)})",
        ["gossip_menu_option"] = $"menu_id IN ({In(gossipIds)})",
        ["npc_text"] = $"ID IN ({In(npcTexts)})",
        ["broadcast_text"] = $"entry IN ({In(broadcast)})",
        ["spell_template"] = $"entry IN ({In(spells)})",
        ["skill_line_ability"] = $"spell_id IN ({In(spells)})",
        ["locales_item"] = $"entry IN ({In(items)})",
        ["locales_creature"] = $"entry IN ({In(creatures)})",
        ["locales_quest"] = $"entry IN ({In(quests)})",
        ["locales_gameobject"] = $"entry IN ({In(gos)})",
        ["locales_spell"] = $"entry IN ({In(spells)})",
        ["locales_broadcast_text"] = $"entry IN ({In(broadcast)})",
        ["locales_page_text"] = $"entry IN ({In(pages)})",
        ["locales_gossip_menu_option"] = $"menu_id IN ({In(gossipIds)})",
        // every row of the small world tables
        ["area_template"] = "1=1", ["map_template"] = "1=1", ["faction"] = "1=1", ["faction_template"] = "1=1", ["taxi_nodes"] = "1=1",
        ["locales_area"] = "1=1", ["locales_taxi_node"] = "1=1", ["locales_faction"] = "1=1", ["migrations"] = "1=1",
    };

    var sb = new StringBuilder();
    sb.Append("-- WoW Data Studio 0.9.0: the isolated test database of the server data tests (made by app\\tools\\ServerFixtureBuilder).\n");
    sb.Append("-- The schema of every table of the VMaNGOS world database of this machine (SHOW CREATE TABLE) and a small, connected\n");
    sb.Append("-- sample of its rows (Valley of Trials and what it uses; every row of the small world tables). Test data only:\n");
    sb.Append("-- loaded into a throwaway MySQL server by the tests, never shipped. One statement per line.\n");
    var tables = Q("SHOW TABLES").Select(r => Convert.ToString(r[0], CultureInfo.InvariantCulture)!).OrderBy(t => t, StringComparer.Ordinal).ToList();
    int rowCount = 0;
    foreach (var t in tables)
    {
        var create = Convert.ToString(Q($"SHOW CREATE TABLE `{t}`")[0][1], CultureInfo.InvariantCulture)!;
        create = Regex.Replace(create, @" AUTO_INCREMENT=\d+", "");
        sb.Append(Regex.Replace(create, @"\s*\n\s*", " ")).Append(";\n");
        if (!where.TryGetValue(t, out var w)) continue;
        var cols = Q($"SHOW COLUMNS FROM `{t}`").Select(r => (Name: Convert.ToString(r[0], CultureInfo.InvariantCulture)!, Type: Convert.ToString(r[1], CultureInfo.InvariantCulture)!)).ToList();
        var wcols = cols.Select((x, i) => new WorldColumn
        {
            Name = x.Name, Ordinal = i + 1, ColumnType = x.Type,
            DataType = Regex.Match(x.Type, @"^\w+").Value.ToLowerInvariant(), Unsigned = x.Type.Contains("unsigned"),
        }).ToList();
        var rows = Q($"SELECT * FROM `{t}` WHERE {w}");
        foreach (var chunk in rows.Chunk(100))
        {
            sb.Append($"INSERT INTO `{t}` VALUES ");
            sb.Append(string.Join(",", chunk.Select(r => "(" + string.Join(",", r.Select((v, i) => WorldValue.Sql(wcols[i], WorldValue.FromDb(v, wcols[i])))) + ")")));
            sb.Append(";\n");
        }
        rowCount += rows.Count;
        Console.Error.WriteLine($"  {t}: {rows.Count} rows");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
    File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"Wrote {outFile}: {tables.Count} tables, {rowCount} rows, {new FileInfo(outFile).Length / 1024} KB.");
    return 0;
}
finally
{
    copy?.Dispose();
}
