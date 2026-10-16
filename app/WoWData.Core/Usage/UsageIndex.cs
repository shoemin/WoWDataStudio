using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WoWData.Core.Dbc;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Mpq;

namespace WoWData.Core.Usage;

/// <summary>
/// The "Used in game" part of the SQLite index (tables prefixed u_), written by <see cref="IndexBuilder"/> during
/// "Organizing game data" so the panel only runs a few indexed queries:
///  * client part (rebuilt with the index): zones (AreaTable), the per-tile chunk area ids of every map, the map-tile
///    placement index of models and WMOs (ADT MDDF/MODF + WMO doodad sets, <see cref="MapScanner"/>), the textures named
///    inside every model (M2 type-0 textures, WMO MOTX), factions with their reaction to Alliance and Horde, spells
///    (names, effects, icons, professions), item sets and locks;
///  * server part (rebuilt with "Reload names from the server database"): creature and game object extras, every spawn
///    with its zone (computed from the chunk area ids), loot with the effective chance of each row, vendors, trainers,
///    quests and their givers/enders/objectives, item extras and creature spells.
/// </summary>
public static class UsageIndex
{
    public const string ClientSchema = """
        CREATE TABLE u_area(id INTEGER PRIMARY KEY, map INT, parent INT, name TEXT);
        CREATE TABLE u_area_chunk(map INT, tx INT, ty INT, areas BLOB, PRIMARY KEY(map, tx, ty));
        CREATE TABLE u_place(model TEXT COLLATE NOCASE, map INT, tx INT, ty INT, area INT, parent TEXT COLLATE NOCASE, count INT);
        CREATE TABLE u_model_texture(model TEXT COLLATE NOCASE, texture TEXT COLLATE NOCASE);
        CREATE TABLE u_faction(id INTEGER PRIMARY KEY, faction INT, name TEXT, alliance INT, horde INT);
        CREATE TABLE u_spell(id INTEGER PRIMARY KEY, name TEXT, rank TEXT, icon INT, visual1 INT, visual2 INT);
        CREATE TABLE u_spell_fx(spell INT, effect INT, aura INT, misc INT, item INT, trigger_spell INT);
        CREATE TABLE u_spell_icon(id INTEGER PRIMARY KEY, path TEXT COLLATE NOCASE);
        CREATE TABLE u_spell_skill(spell INT, skill INT, name TEXT, category INT);
        CREATE TABLE u_item_set(id INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE u_lock(id INTEGER PRIMARY KEY, kind TEXT, skill INT);
        CREATE TABLE u_ground_texture(texture TEXT COLLATE NOCASE, map INT, tx INT, ty INT);
        CREATE INDEX u_ground_texture_tex ON u_ground_texture(texture);
        CREATE INDEX u_place_model ON u_place(model);
        CREATE INDEX u_place_parent ON u_place(parent);
        CREATE INDEX u_model_texture_tex ON u_model_texture(texture);
        CREATE INDEX u_model_texture_model ON u_model_texture(model);
        CREATE INDEX u_spell_fx_spell ON u_spell_fx(spell);
        CREATE INDEX u_spell_fx_misc ON u_spell_fx(misc);
        CREATE INDEX u_spell_fx_item ON u_spell_fx(item);
        CREATE INDEX u_spell_icon_path ON u_spell_icon(path);
        CREATE INDEX u_spell_icon_id ON u_spell(icon);
        CREATE INDEX u_spell_skill_spell ON u_spell_skill(spell);
        """;

    public const string ServerSchema = """
        CREATE TABLE IF NOT EXISTS u_creature(entry INTEGER PRIMARY KEY, faction INT, rank INT, npc_flags INT, type INT, loot_id INT, pickpocket_id INT,
            skinning_id INT, vendor_id INT, trainer_id INT, trainer_type INT, trainer_class INT);
        CREATE TABLE IF NOT EXISTS u_spawn(kind TEXT, entry INT, map INT, x REAL, y REAL, z REAL, area INT);
        CREATE TABLE IF NOT EXISTS u_gameobject(entry INTEGER PRIMARY KEY, type INT, data0 INT, data1 INT, data2 INT, data3 INT, loot_id INT, lock_kind TEXT);
        CREATE TABLE IF NOT EXISTS u_loot(source TEXT, entry INT, item INT, ref INT, chance REAL, quest INT, mincount INT, maxcount INT);
        CREATE TABLE IF NOT EXISTS u_vendor(creature INT, item INT, maxcount INT);
        CREATE TABLE IF NOT EXISTS u_trainer(creature INT, spell INT, taught INT);
        CREATE TABLE IF NOT EXISTS u_quest(id INTEGER PRIMARY KEY, title TEXT, level INT, min_level INT);
        CREATE TABLE IF NOT EXISTS u_quest_link(quest INT, role TEXT, kind TEXT, ref INT, count INT);
        CREATE TABLE IF NOT EXISTS u_item(entry INTEGER PRIMARY KEY, item_level INT, required_level INT, set_id INT, start_quest INT, bonding INT);
        CREATE TABLE IF NOT EXISTS u_item_spell(item INT, spell INT, trigger_type INT);
        CREATE TABLE IF NOT EXISTS u_creature_spell(creature INT, spell INT);
        CREATE INDEX IF NOT EXISTS u_spawn_entry ON u_spawn(kind, entry);
        CREATE INDEX IF NOT EXISTS u_creature_loot ON u_creature(loot_id);
        CREATE INDEX IF NOT EXISTS u_creature_pick ON u_creature(pickpocket_id);
        CREATE INDEX IF NOT EXISTS u_creature_skin ON u_creature(skinning_id);
        CREATE INDEX IF NOT EXISTS u_gameobject_loot ON u_gameobject(loot_id);
        CREATE INDEX IF NOT EXISTS u_loot_item ON u_loot(item);
        CREATE INDEX IF NOT EXISTS u_loot_ref ON u_loot(ref);
        CREATE INDEX IF NOT EXISTS u_loot_src ON u_loot(source, entry);
        CREATE INDEX IF NOT EXISTS u_vendor_item ON u_vendor(item);
        CREATE INDEX IF NOT EXISTS u_vendor_creature ON u_vendor(creature);
        CREATE INDEX IF NOT EXISTS u_trainer_taught ON u_trainer(taught);
        CREATE INDEX IF NOT EXISTS u_trainer_creature ON u_trainer(creature);
        CREATE INDEX IF NOT EXISTS u_quest_link_ref ON u_quest_link(kind, ref);
        CREATE INDEX IF NOT EXISTS u_quest_link_quest ON u_quest_link(quest);
        CREATE INDEX IF NOT EXISTS u_item_set_idx ON u_item(set_id);
        CREATE INDEX IF NOT EXISTS u_item_spell_spell ON u_item_spell(spell);
        CREATE INDEX IF NOT EXISTS u_item_spell_item ON u_item_spell(item);
        CREATE INDEX IF NOT EXISTS u_creature_spell_spell ON u_creature_spell(spell);
        CREATE INDEX IF NOT EXISTS u_creature_spell_creature ON u_creature_spell(creature);
        """;

    public static readonly string[] ServerTables =
        { "u_creature", "u_spawn", "u_gameobject", "u_loot", "u_vendor", "u_trainer", "u_quest", "u_quest_link", "u_item", "u_item_spell", "u_creature_spell" };

    // ------------------------------------------------------------------ helpers

    private sealed class Inserter : IDisposable
    {
        private readonly SqliteCommand _cmd;
        private readonly SqliteParameter[] _p;
        public int Rows { get; private set; }

        public Inserter(SqliteConnection db, SqliteTransaction tx, string table, int columns)
        {
            _cmd = db.CreateCommand();
            _cmd.Transaction = tx;
            _p = new SqliteParameter[columns];
            for (int i = 0; i < columns; i++)
            {
                _p[i] = _cmd.CreateParameter();
                _p[i].ParameterName = "$p" + i;
                _cmd.Parameters.Add(_p[i]);
            }
            _cmd.CommandText = $"INSERT INTO {table} VALUES ({string.Join(",", Enumerable.Range(0, columns).Select(i => "$p" + i))})";
            _cmd.Prepare();
        }

        public void Add(params object?[] values)
        {
            for (int i = 0; i < _p.Length; i++) _p[i].Value = values[i] ?? DBNull.Value;
            _cmd.ExecuteNonQuery();
            Rows++;
        }

        public void Dispose() => _cmd.Dispose();
    }

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        foreach (var stmt in sql.Split(';').Where(s => s.Trim().Length > 0))
        {
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = stmt;
            c.ExecuteNonQuery();
        }
    }

    public static void CreateClientTables(SqliteConnection db, SqliteTransaction? tx = null) => Exec(db, ClientSchema, tx);

    public static void CreateServerTables(SqliteConnection db, SqliteTransaction? tx = null) => Exec(db, ServerSchema, tx);

    /// <summary>The player races' Faction ids per side (Faction.dbc "PLAYER, Human" ... ): Alliance 1 Human, 3 Dwarf,
    /// 4 Night Elf, 8 Gnome; Horde 2 Orc, 5 Undead, 6 Tauren, 9 Troll.</summary>
    private static readonly int[] AllianceFactions = { 1, 3, 4, 8 }, HordeFactions = { 2, 5, 6, 9 };

    /// <summary>Reaction of a faction template to the players of a side (group mask 2 = Alliance, 4 = Horde): 1 friendly,
    /// 0 neutral, -1 hostile, the way the client's FactionTemplate checks read: the enemy / friend faction lists first,
    /// then hostile when the hostile mask has the side or all players (1), friendly when the template belongs to the side
    /// or its friendly mask has it. A template with no masks (e.g. 189 "Creature": boars, wolves) is neutral.</summary>
    public static int Reaction(FactionTemplateRec f, int side)
    {
        var players = side == 2 ? AllianceFactions : HordeFactions;
        if (f.Enemies.Any(e => e > 0 && players.Contains(e))) return -1;
        if (f.Friends.Any(e => e > 0 && players.Contains(e))) return 1;
        if ((f.HostileMask & (side | 1)) != 0) return -1;
        if ((f.OurMask & side) != 0 || (f.FriendlyMask & side) != 0) return 1;
        return 0;
    }

    private static string LockKind(LockRec l, out int skill)
    {
        skill = 0;
        for (int i = 0; i < 8; i++)
        {
            if (l.Type[i] == 2)
            {
                skill = l.Skill[i];
                switch (l.Index[i])
                {
                    case 2: return "herb";
                    case 3: return "mining";
                    case 19: return "fishing";
                    case 1: return "lockpicking";
                }
            }
        }
        for (int i = 0; i < 8; i++) if (l.Type[i] == 1 && l.Index[i] > 0) return "key";
        return "";
    }

    // ------------------------------------------------------------------ client part

    /// <summary>Zones, factions, spells, icons, professions, item sets, locks (DBCs only; well under a second).</summary>
    public static Dictionary<string, int> WriteClientTables(SqliteConnection db, SqliteTransaction tx, Catalog cat)
    {
        var counts = new Dictionary<string, int>();
        T Try<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (DbcException) { return fallback; }
        }
        using (var ins = new Inserter(db, tx, "u_area", 4))
            foreach (var a in Try(() => UsageDbc.AreaTable(cat.Table("AreaTable")), new()).GroupBy(a => a.Id).Select(g => g.Last()))
                ins.Add(a.Id, a.Map, a.Parent, a.Name);
        var factionNames = Try(() => UsageDbc.FactionNames(cat.Table("Faction")), new());
        using (var ins = new Inserter(db, tx, "u_faction", 5))
            foreach (var f in Try(() => UsageDbc.FactionTemplate(cat.Table("FactionTemplate")), new()).GroupBy(f => f.Id).Select(g => g.Last()))
                ins.Add(f.Id, f.Faction, factionNames.GetValueOrDefault(f.Faction, ""), Reaction(f, 2), Reaction(f, 4));
        var spells = Try(() => UsageDbc.Spell(cat.Table("Spell")), new());
        using (var ins = new Inserter(db, tx, "u_spell", 6))
        using (var fx = new Inserter(db, tx, "u_spell_fx", 6))
            foreach (var s in spells.GroupBy(s => s.Id).Select(g => g.Last()))
            {
                ins.Add(s.Id, s.Name, s.Rank, s.Icon, s.Visuals[0], s.Visuals[1]);
                for (int i = 0; i < 3; i++)
                    if (s.Effect[i] != 0) fx.Add(s.Id, s.Effect[i], s.Aura[i], s.Misc[i], s.ItemType[i], s.Trigger[i]);
            }
        counts["u_spell"] = spells.Count;
        using (var ins = new Inserter(db, tx, "u_spell_icon", 2))
            foreach (var (id, path) in Try(() => UsageDbc.SpellIcon(cat.Table("SpellIcon")), new()).GroupBy(i => i.Id).Select(g => g.Last()))
                if (path.Length > 0) ins.Add(id, cat.Canonical(path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp"));
        var skills = Try(() => UsageDbc.SkillLine(cat.Table("SkillLine")), new()).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.Last());
        using (var ins = new Inserter(db, tx, "u_spell_skill", 4))
            foreach (var (skill, spell) in Try(() => UsageDbc.SkillLineAbility(cat.Table("SkillLineAbility")), new()).Distinct())
                if (skills.TryGetValue(skill, out var sl)) ins.Add(spell, skill, sl.Name, sl.Category);
        using (var ins = new Inserter(db, tx, "u_item_set", 2))
            foreach (var (id, name) in Try(() => UsageDbc.ItemSet(cat.Table("ItemSet")), new()).GroupBy(i => i.Id).Select(g => g.Last()))
                ins.Add(id, name);
        using (var ins = new Inserter(db, tx, "u_lock", 3))
            foreach (var l in Try(() => UsageDbc.Lock(cat.Table("Lock")), new()).GroupBy(l => l.Id).Select(g => g.Last()))
            {
                var kind = LockKind(l, out int skill);
                if (kind.Length > 0) ins.Add(l.Id, kind, skill);
            }
        return counts;
    }

    /// <summary>The map scan (chunk areas + placements) and the textures named inside every model. Progress 0..1.</summary>
    public static Dictionary<string, int> WriteWorld(SqliteConnection db, SqliteTransaction tx, Catalog cat, Action<double, string>? progress, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>();
        var sw = Stopwatch.StartNew();
        List<MapRec> maps;
        try { maps = DbcTables.Map(cat.Table("Map")); }
        catch (DbcException) { maps = new(); }
        var scan = MapScanner.Scan(cat.Client, maps, p => cat.Canonical(p),
            (done, total) => progress?.Invoke(total == 0 ? 0.7 : 0.7 * done / total, $"{done:N0} of {total:N0} map tiles"), ct);
        using (var ins = new Inserter(db, tx, "u_area_chunk", 4))
            foreach (var ((map, tx2, ty), areas) in scan.Areas)
            {
                var blob = new byte[areas.Length * 2];
                for (int i = 0; i < areas.Length; i++) BitConverter.TryWriteBytes(blob.AsSpan(i * 2), (ushort)Math.Clamp(areas[i], 0, 65535));
                ins.Add(map, tx2, ty, blob);
            }
        using (var ins = new Inserter(db, tx, "u_place", 7))
            foreach (var p in scan.Placements) ins.Add(p.Model, p.Map, p.TileX, p.TileY, p.Area, p.Parent, p.Count);
        using (var ins = new Inserter(db, tx, "u_ground_texture", 4))
            foreach (var g in scan.GroundTextures) ins.Add(g.Texture, g.Map, g.TileX, g.TileY);
        counts["u_ground_texture"] = scan.GroundTextures.Count;
        counts["map_tiles"] = scan.Tiles;
        counts["doodad_placements"] = scan.DoodadPlacements;
        counts["wmo_placements"] = scan.WmoPlacements;
        counts["wmo_doodads"] = scan.WmoDoodads;
        counts["u_place"] = scan.Placements.Count;
        counts["map_scan_ms"] = (int)sw.ElapsedMilliseconds;
        ct.ThrowIfCancellationRequested();

        // textures named inside models: every .m2 (type 0 entries) and every root .wmo (MOTX)
        sw.Restart();
        var models = cat.Names().Values.Where(f =>
                f.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
                (f.Path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(f.Path, @"_\d{3}\.wmo$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Select(f => f.Path).ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<(string Model, string Texture)>();
        int done = 0;
        Parallel.ForEach(models, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, m =>
        {
            try
            {
                var data = cat.Client.Read(m);
                if (data != null)
                {
                    IEnumerable<string> names = m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)
                        ? WmoHeader.Textures(data)
                        : new M2Header(data).Textures().Where(t => t.Type == 0).Select(t => t.FileName);
                    foreach (var t in names.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                        found.Add((m, t));
                }
            }
            catch (Exception e) when (e is M2Exception or MpqException or IOException or ArgumentException) { }
            int d = Interlocked.Increment(ref done);
            if (d % 500 == 0) progress?.Invoke(0.7 + 0.3 * d / models.Count, $"{d:N0} of {models.Count:N0} models");
        });
        int n = 0;
        using (var ins = new Inserter(db, tx, "u_model_texture", 2))
            foreach (var (m, t) in found.Select(x => (x.Model, cat.Canonical(x.Texture))).Distinct())
            {
                ins.Add(m, t);
                n++;
            }
        counts["u_model_texture"] = n;
        counts["model_texture_ms"] = (int)sw.ElapsedMilliseconds;
        return counts;
    }

    // ------------------------------------------------------------------ server part

    /// <summary>The chunk area ids stored by the client part, for the spawn zones.</summary>
    public static Dictionary<(int, int, int), int[]> LoadAreaGrid(SqliteConnection db)
    {
        var res = new Dictionary<(int, int, int), int[]>();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT map, tx, ty, areas FROM u_area_chunk";
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var blob = (byte[])r.GetValue(3);
            var a = new int[blob.Length / 2];
            for (int i = 0; i < a.Length; i++) a[i] = BitConverter.ToUInt16(blob, i * 2);
            res[(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2))] = a;
        }
        return res;
    }

    /// <summary>Effective chance (percent) of every loot row: group 0 rows keep their own chance; in a group, rows
    /// without a chance share what the explicit chances leave (as the server rolls one item per group).
    /// Negative chances are quest drops (only while the quest is active).</summary>
    public static IEnumerable<(SrvLoot Row, double Chance, bool Quest)> EffectiveChances(IEnumerable<SrvLoot> rows)
    {
        foreach (var g in rows.GroupBy(r => (r.Source, r.Entry, r.Group)))
        {
            if (g.Key.Group == 0)
            {
                foreach (var r in g) yield return (r, Math.Abs(r.Chance), r.Chance < 0);
                continue;
            }
            double explicitSum = g.Where(r => r.Chance != 0).Sum(r => Math.Abs(r.Chance));
            int zero = g.Count(r => r.Chance == 0);
            double share = zero == 0 ? 0 : Math.Max(0, 100 - explicitSum) / zero;
            foreach (var r in g) yield return (r, r.Chance == 0 ? share : Math.Abs(r.Chance), r.Chance < 0);
        }
    }

    /// <summary>Writes the server part (the u_ server tables are created when missing and emptied first).</summary>
    public static Dictionary<string, int> WriteServer(SqliteConnection db, UsageServerData? sd)
    {
        var counts = new Dictionary<string, int>();
        using var tx = db.BeginTransaction();
        CreateServerTables(db, tx);
        foreach (var t in ServerTables) Exec(db, $"DELETE FROM {t}", tx);
        if (sd == null)
        {
            tx.Commit();
            return counts;
        }
        var grid = LoadAreaGrid(db);
        var creatureById = new Dictionary<int, SrvCreatureExt>();
        using (var ins = new Inserter(db, tx, "u_creature", 12))
            foreach (var c in sd.Creatures)
                if (creatureById.TryAdd(c.Entry, c))
                    ins.Add(c.Entry, c.Faction, c.Rank, c.NpcFlags, c.Type, c.LootId, c.PickpocketId, c.SkinningId, c.VendorId, c.TrainerId, c.TrainerType, c.TrainerClass);
        using (var ins = new Inserter(db, tx, "u_spawn", 7))
        {
            foreach (var s in sd.CreatureSpawns) ins.Add("creature", s.Entry, s.Map, s.X, s.Y, s.Z, MapScanner.AreaAt(grid, s.Map, s.X, s.Y));
            foreach (var s in sd.GameObjectSpawns) ins.Add("gameobject", s.Entry, s.Map, s.X, s.Y, s.Z, MapScanner.AreaAt(grid, s.Map, s.X, s.Y));
            counts["u_spawn"] = ins.Rows;
        }
        var locks = new Dictionary<int, string>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT id, kind FROM u_lock";
            using var r = c.ExecuteReader();
            while (r.Read()) locks[r.GetInt32(0)] = r.GetString(1);
        }
        using (var ins = new Inserter(db, tx, "u_gameobject", 8))
            foreach (var g in sd.GameObjects.GroupBy(g => g.Entry).Select(g => g.Last()))
            {
                // chest (3) and fishing hole (25): data0 = lock, data1 = loot id
                int loot = g.Type is 3 or 25 ? g.Data1 : 0;
                string? lk = g.Type == 3 && locks.TryGetValue(g.Data0, out var k) ? k : null;
                ins.Add(g.Entry, g.Type, g.Data0, g.Data1, g.Data2, g.Data3, loot, lk);
            }
        using (var ins = new Inserter(db, tx, "u_loot", 8))
        {
            foreach (var (r, chance, quest) in EffectiveChances(sd.Loot))
            {
                bool isRef = r.MinCountOrRef < 0;
                ins.Add(r.Source, r.Entry, isRef ? 0 : r.Item, isRef ? -r.MinCountOrRef : 0, chance, quest ? 1 : 0, isRef ? 0 : r.MinCountOrRef, r.MaxCount);
            }
            counts["u_loot"] = ins.Rows;
        }
        // vendors: direct rows + vendor templates through creature_template.vendor_id
        var vendorTemplates = sd.Vendors.Where(v => v.Template).GroupBy(v => v.Entry).ToDictionary(g => g.Key, g => g.ToList());
        using (var ins = new Inserter(db, tx, "u_vendor", 3))
        {
            var seen = new HashSet<(int, int)>();
            foreach (var v in sd.Vendors.Where(v => !v.Template))
                if (seen.Add((v.Entry, v.Item))) ins.Add(v.Entry, v.Item, v.MaxCount);
            foreach (var c in creatureById.Values.Where(c => c.VendorId > 0))
                if (vendorTemplates.TryGetValue(c.VendorId, out var list))
                    foreach (var v in list)
                        if (seen.Add((c.Entry, v.Item))) ins.Add(c.Entry, v.Item, v.MaxCount);
            counts["u_vendor"] = ins.Rows;
        }
        // trainers: the trainer's spell is a "learn" spell (effect 36) whose trigger is the spell the player gets
        var learn = new Dictionary<int, int>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT spell, trigger_spell FROM u_spell_fx WHERE effect = 36 AND trigger_spell > 0";
            using var r = c.ExecuteReader();
            while (r.Read()) learn.TryAdd(r.GetInt32(0), r.GetInt32(1));
        }
        var trainerTemplates = sd.Trainers.Where(t => t.Template).GroupBy(t => t.Entry).ToDictionary(g => g.Key, g => g.Select(x => x.Spell).ToList());
        using (var ins = new Inserter(db, tx, "u_trainer", 3))
        {
            var seen = new HashSet<(int, int)>();
            void Add(int creature, int spell)
            {
                if (seen.Add((creature, spell))) ins.Add(creature, spell, learn.GetValueOrDefault(spell, spell));
            }
            foreach (var t in sd.Trainers.Where(t => !t.Template)) Add(t.Entry, t.Spell);
            foreach (var c in creatureById.Values.Where(c => c.TrainerId > 0))
                if (trainerTemplates.TryGetValue(c.TrainerId, out var list))
                    foreach (var s in list) Add(c.Entry, s);
            counts["u_trainer"] = ins.Rows;
        }
        // quests
        using (var q = new Inserter(db, tx, "u_quest", 4))
        using (var l = new Inserter(db, tx, "u_quest_link", 5))
        {
            foreach (var x in sd.Quests.GroupBy(x => x.Id).Select(g => g.Last()))
            {
                q.Add(x.Id, x.Title, x.Level, x.MinLevel);
                for (int i = 0; i < 4; i++)
                {
                    if (x.ReqItem[i] > 0) l.Add(x.Id, "collect", "item", x.ReqItem[i], x.ReqItemCount[i]);
                    if (x.ReqNpcOrGo[i] > 0) l.Add(x.Id, "kill", "creature", x.ReqNpcOrGo[i], x.ReqNpcOrGoCount[i]);
                    else if (x.ReqNpcOrGo[i] < 0) l.Add(x.Id, "use", "gameobject", -x.ReqNpcOrGo[i], x.ReqNpcOrGoCount[i]);
                    if (x.ReqSource[i] > 0) l.Add(x.Id, "source", "item", x.ReqSource[i], 0);
                    if (x.RewItem[i] > 0) l.Add(x.Id, "reward", "item", x.RewItem[i], 0);
                }
                for (int i = 0; i < 6; i++)
                    if (x.RewChoice[i] > 0) l.Add(x.Id, "choice", "item", x.RewChoice[i], 0);
                if (x.SrcItem > 0) l.Add(x.Id, "provided", "item", x.SrcItem, 0);
            }
            foreach (var (rel, entry, quest) in sd.QuestRelations.Distinct())
            {
                var parts = rel.Split(' ');
                l.Add(quest, parts[1], parts[0], entry, 0);
            }
            foreach (var it in sd.Items.Where(i => i.StartQuest > 0)) l.Add(it.StartQuest, "start", "item", it.Entry, 0);
            counts["u_quest"] = q.Rows;
            counts["u_quest_link"] = l.Rows;
        }
        using (var ins = new Inserter(db, tx, "u_item", 6))
        using (var sp = new Inserter(db, tx, "u_item_spell", 3))
            foreach (var it in sd.Items.GroupBy(i => i.Entry).Select(g => g.Last()))
            {
                ins.Add(it.Entry, it.ItemLevel, it.RequiredLevel, it.SetId, it.StartQuest, it.Bonding);
                for (int i = 0; i < it.Spells.Length; i++)
                    if (it.Spells[i] > 0) sp.Add(it.Entry, it.Spells[i], it.Triggers[i]);
            }
        // creature spells: template spells + the creature_spells list
        var lists = sd.SpellLists.GroupBy(s => s.List).ToDictionary(g => g.Key, g => g.Select(x => x.Spell).ToList());
        using (var ins = new Inserter(db, tx, "u_creature_spell", 2))
            foreach (var c in creatureById.Values)
            {
                var set = new HashSet<int>(c.Spells.Where(s => s > 0));
                if (c.SpellListId > 0 && lists.TryGetValue(c.SpellListId, out var l2)) set.UnionWith(l2);
                foreach (var s in set) ins.Add(c.Entry, s);
            }
        tx.Commit();
        return counts;
    }
}
