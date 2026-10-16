using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WoWData.Core.Index;

namespace WoWData.Core.Usage;

/// <summary>
/// "Used in game": how the selected thing is used by the game, built from the precomputed u_ tables of the index
/// (<see cref="UsageIndex"/>) with a few indexed queries. It opens its own read-only connection for each report and
/// closes it afterwards, so the index file is never held open between selections (a rebuild replaces the file); the
/// name tables are cached on first use. Thread-safe (reports are serialized).
/// Without the server database only the client sections are shown, with <see cref="UsageReport.ServerNote"/>.
/// </summary>
public sealed class UsageService : IDisposable
{
    private SqliteConnection? _db;
    private int _depth;
    private readonly object _gate = new();
    public string IndexPath { get; }

    public UsageService(string indexPath)
    {
        IndexPath = indexPath;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>Runs <paramref name="work"/> with the connection open (nested calls share it).</summary>
    private T WithDb<T>(Func<T> work)
    {
        lock (_gate)
        {
            if (_depth++ == 0)
            {
                _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = IndexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                _db.Open();
            }
            try { return work(); }
            finally
            {
                if (--_depth == 0)
                {
                    _db?.Dispose();
                    _db = null;
                }
            }
        }
    }

    // ------------------------------------------------------------------ query helpers

    private List<T> Rows<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args) => WithDb(() =>
    {
        {
            using var c = _db!.CreateCommand();
            c.CommandText = sql;
            for (int i = 0; i < args.Length; i++) c.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
            using var r = c.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
    });

    private long Scalar(string sql, params object?[] args) => Rows(sql, r => r.IsDBNull(0) ? 0L : r.GetInt64(0), args).FirstOrDefault();

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int I(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
    private static double D(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    // ------------------------------------------------------------------ caches

    private sealed record Npc(int Entry, string Name, string Sub, int LevelMin, int LevelMax, int[] Displays)
    {
        public string Level => LevelMin == LevelMax ? $"level {LevelMin}" : $"level {LevelMin}-{LevelMax}";
    }

    private sealed record ItemInfo(int Entry, string Name, int Display, int InventoryType, int Quality, int Class, int SubClass);
    private sealed record GoInfo(int Entry, string Name, int Type, int Display);
    private sealed record Area(int Id, int Map, int Parent, string Name);

    private sealed class Caches
    {
        public bool Server;
        public Dictionary<int, Npc> Npcs = new();
        public Dictionary<long, List<int>> NpcsByDisplay = new();
        public Dictionary<int, ItemInfo> Items = new();
        public Dictionary<long, List<int>> ItemsByDisplay = new();
        public Dictionary<int, GoInfo> Gos = new();
        public Dictionary<long, List<int>> GosByDisplay = new();
        public Dictionary<int, Area> Areas = new();
        public Dictionary<int, string> Maps = new();
        public Dictionary<int, string> MapDirs = new();
        public Dictionary<int, (string Name, string Rank)> Spells = new();
        public Dictionary<int, int> SpellEffectOfSpell = new();
        public Dictionary<int, (string Name, int Alliance, int Horde)> Factions = new();
        public Dictionary<int, string> Races = new();
    }

    private Caches? _c;

    /// <summary>Loads the name tables (about 0.1 s; done on first use, or ahead with this call from a background thread).</summary>
    public void Warm() => WithDb(() => C);

    private Caches C
    {
        get
        {
            if (_c != null) return _c;
            var c = new Caches();
            c.Server = Rows("SELECT value FROM meta WHERE key='server'", r => S(r, 0)).FirstOrDefault() == "True";
            foreach (var n in Rows("SELECT entry, name, subname, level_min, level_max, display1, display2, display3, display4 FROM creature",
                         r => new Npc(I(r, 0), S(r, 1) ?? "", S(r, 2) ?? "", I(r, 3), I(r, 4), new[] { I(r, 5), I(r, 6), I(r, 7), I(r, 8) }.Where(d => d > 0).Distinct().ToArray())))
            {
                c.Npcs[n.Entry] = n;
                foreach (var d in n.Displays) Add(c.NpcsByDisplay, d, n.Entry);
            }
            foreach (var it in Rows("SELECT entry, name, display_id, inventory_type, quality, class, subclass FROM item",
                         r => new ItemInfo(I(r, 0), S(r, 1) ?? "", I(r, 2), I(r, 3), I(r, 4), I(r, 5), I(r, 6))))
            {
                c.Items[it.Entry] = it;
                Add(c.ItemsByDisplay, it.Display, it.Entry);
            }
            foreach (var g in Rows("SELECT entry, name, type, display_id FROM gameobject", r => new GoInfo(I(r, 0), S(r, 1) ?? "", I(r, 2), I(r, 3))))
            {
                c.Gos[g.Entry] = g;
                Add(c.GosByDisplay, g.Display, g.Entry);
            }
            foreach (var a in Rows("SELECT id, map, parent, name FROM u_area", r => new Area(I(r, 0), I(r, 1), I(r, 2), S(r, 3) ?? ""))) c.Areas[a.Id] = a;
            foreach (var (id, dir, name) in Rows("SELECT id, directory, name FROM map", r => (I(r, 0), S(r, 1) ?? "", S(r, 2) ?? "")))
            {
                c.Maps[id] = name;
                c.MapDirs[id] = dir;
            }
            foreach (var (id, name, rank) in Rows("SELECT id, name, rank FROM u_spell", r => (I(r, 0), S(r, 1) ?? "", S(r, 2) ?? ""))) c.Spells[id] = (name, rank);
            foreach (var (spell, effect) in Rows("SELECT spell_id, MIN(effect_id) FROM spell_effect_use GROUP BY spell_id", r => (I(r, 0), I(r, 1)))) c.SpellEffectOfSpell[spell] = effect;
            foreach (var (id, name, a, h) in Rows("SELECT id, name, alliance, horde FROM u_faction", r => (I(r, 0), S(r, 1) ?? "", I(r, 2), I(r, 3)))) c.Factions[id] = (name, a, h);
            foreach (var (id, name) in Rows("SELECT id, name FROM race", r => (I(r, 0), S(r, 1) ?? ""))) c.Races[id] = name;
            return _c = c;
        }
    }

    private static void Add(Dictionary<long, List<int>> d, long k, int v)
    {
        if (k <= 0) return;
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>();
        l.Add(v);
    }

    // ------------------------------------------------------------------ formatting

    private string ZoneName(int area, int map)
    {
        var c = C;
        if (area > 0 && c.Areas.TryGetValue(area, out var a))
        {
            if (a.Parent > 0 && c.Areas.TryGetValue(a.Parent, out var z)) return $"{a.Name}, {z.Name}";
            return a.Name;
        }
        return c.Maps.TryGetValue(map, out var m) ? (area > 0 ? $"{m} (area {area})" : m) : $"map {map}";
    }

    /// <summary>The zone (top-level area) of an area id, or the map name.</summary>
    private string TopZone(int area, int map)
    {
        var c = C;
        if (area > 0 && c.Areas.TryGetValue(area, out var a))
        {
            while (a.Parent > 0 && c.Areas.TryGetValue(a.Parent, out var p)) a = p;
            return a.Name;
        }
        return c.Maps.TryGetValue(map, out var m) ? m : $"map {map}";
    }

    private string MapName(int map) => C.Maps.TryGetValue(map, out var m) ? m : $"map {map}";

    private string SpellLabel(int spell)
    {
        if (!C.Spells.TryGetValue(spell, out var s)) return $"spell {spell}";
        return s.Rank.Length > 0 ? $"{s.Name} ({s.Rank})" : s.Name;
    }

    private string? SpellKey(int spell) => C.SpellEffectOfSpell.TryGetValue(spell, out var e) ? $"spell:{e}" : null;

    private void NpcLink(UsageLine l, int entry, bool withLevel = true)
    {
        if (C.Npcs.TryGetValue(entry, out var n))
        {
            l.Add(n.Name, $"creature:{entry}");
            if (withLevel) l.Muted($"  {n.Level}");
        }
        else l.Muted($"creature {entry}");
    }

    private void ItemLink(UsageLine l, int entry)
    {
        if (C.Items.TryGetValue(entry, out var it)) l.Add(it.Name, $"item:{entry}", UsageNames.QualityColor(it.Quality));
        else l.Muted($"item {entry}");
    }

    private void GoLink(UsageLine l, int entry)
    {
        if (C.Gos.TryGetValue(entry, out var g)) l.Add(g.Name, $"go:{entry}");
        else l.Muted($"game object {entry}");
    }

    private string QuestLabel(int quest)
    {
        var q = Rows("SELECT title, level FROM u_quest WHERE id=$0", r => (S(r, 0) ?? "", I(r, 1)), quest).FirstOrDefault();
        return q.Item1 == null ? $"quest {quest}" : $"“{q.Item1}”" + (q.Item2 > 0 ? $" (level {q.Item2})" : "");
    }

    /// <summary>Who gives and who takes a quest, appended to a line as links.</summary>
    private void QuestGivers(UsageLine l, int quest)
    {
        var links = Rows("SELECT role, kind, ref FROM u_quest_link WHERE quest=$0 AND role IN ('start','end') ORDER BY role DESC, kind", r => (S(r, 0)!, S(r, 1)!, I(r, 2)), quest);
        void Part(string role, string label)
        {
            var who = links.Where(x => x.Item1 == role).Take(2).ToList();
            if (who.Count == 0) return;
            l.Muted(label);
            for (int i = 0; i < who.Count; i++)
            {
                if (i > 0) l.Muted(" / ");
                var (_, kind, id) = who[i];
                if (kind == "creature") NpcLink(l, id, false);
                else if (kind == "gameobject") GoLink(l, id);
                else ItemLink(l, id);
            }
        }
        Part("start", " · from ");
        Part("end", " · to ");
    }

    // ------------------------------------------------------------------ entry point

    /// <summary>The report for an entry key (creature:3098, cdisplay:503, item:2880, idisplay:..., go:..., godisplay:...,
    /// spell:&lt;effect id&gt;, race:2:0, file:&lt;path&gt;).</summary>
    public UsageReport Report(string key) => WithDb(() => Build(key));

    private UsageReport Build(string key)
    {
        var sw = Stopwatch.StartNew();
        var rep = new UsageReport { Key = key, ServerConnected = C.Server };
        var parts = key.Split(':', 2);
        var kind = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";
        int.TryParse(arg, out var id);
        switch (kind)
        {
            case "creature": Creature(rep, id); break;
            case "cdisplay": CreatureDisplay(rep, id); break;
            case "item": Item(rep, id); break;
            case "idisplay": ItemDisplay(rep, id); break;
            case "go": GameObject(rep, id); break;
            case "godisplay": GameObjectDisplay(rep, id); break;
            case "spell": SpellEffect(rep, id); break;
            case "race":
            {
                var rs = arg.Split(':');
                if (rs.Length == 2 && int.TryParse(rs[0], out var race) && int.TryParse(rs[1], out var sex)) Character(rep, race, sex);
                break;
            }
            case "file":
                if (arg.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) Texture(rep, arg);
                else Model(rep, arg);
                break;
        }
        if (rep.NeedsServer && !rep.ServerConnected) rep.Notes.Add(UsageReport.ServerNote);
        rep.Short = ShortText(rep);
        rep.Milliseconds = sw.Elapsed.TotalMilliseconds;
        return rep;
    }

    /// <summary>The collapsed header's line: the most telling numbers of the report.</summary>
    private static string ShortText(UsageReport rep)
    {
        static string Cut(string s)
        {
            int i = s.IndexOfAny(new[] { '(', ';' });
            return (i > 0 ? s[..i] : s).Trim().TrimEnd(',');
        }
        var parts = new List<string>();
        void Add(string id, Func<string, string> f)
        {
            var s = rep.Section(id);
            if (s != null && s.Summary.Length > 0 && char.IsDigit(s.Summary[0])) parts.Add(f(Cut(s.Summary)));
        }
        Add("spawns", x => x);
        Add("placements", x => x);
        Add("affects", x => x);
        Add("drops", x => "dropped by " + x);
        Add("vendors", x => "sold by " + x);
        Add("quests", x => x);
        Add("loot", x => "drops " + x);
        Add("contains", x => "contains " + x);
        Add("sells", x => "sells " + x);
        Add("teaches", x => "teaches " + x);
        Add("spells", x => "shown by " + x);
        Add("users", x => "used by " + x);
        if (rep.Section("crafted") is { Lines.Count: > 0 }) parts.Add("crafted");
        if (rep.Section("vanity") != null) parts.Add("mount / companion");
        if (rep.Section("character") is { } ch && parts.Count == 0) parts.Add(ch.Summary);
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts.Take(4));
    }

    // ------------------------------------------------------------------ creatures

    private void Creature(UsageReport rep, int entry)
    {
        var c = C;
        if (!c.Npcs.TryGetValue(entry, out var npc))
        {
            rep.Headline.Muted($"Creature {entry} is not in the server database.");
            return;
        }
        var ext = Rows("SELECT faction, rank, npc_flags, type, loot_id, pickpocket_id, skinning_id, vendor_id, trainer_id, trainer_type, trainer_class FROM u_creature WHERE entry=$0",
            r => Enumerable.Range(0, 11).Select(i => I(r, i)).ToArray(), entry).FirstOrDefault() ?? new int[11];
        rep.Headline.Add(npc.Name, bold: true);
        if (npc.Sub.Length > 0) rep.Headline.Muted($" <{npc.Sub}>");
        rep.Headline.Muted($" · {npc.Level}");
        var type = UsageNames.CreatureType(ext[3]);
        if (type.Length > 0) rep.Headline.Muted($" · {type}");
        if (ext[1] != 0) rep.Headline.Add($" · {UsageNames.Rank(ext[1])}", color: "#FFB06000", bold: true);

        var who = rep.Add("npc", "This NPC");
        who.Line().Muted("Level ").Add(npc.LevelMin == npc.LevelMax ? $"{npc.LevelMin}" : $"{npc.LevelMin}-{npc.LevelMax}")
            .Muted(" · rank ").Add(UsageNames.Rank(ext[1])).Muted(type.Length > 0 ? " · " + type : "");
        if (c.Factions.TryGetValue(ext[0], out var f))
            who.Line().Muted("Faction ").Add(f.Name.Length > 0 ? f.Name : $"template {ext[0]}")
                .Muted($" · {UsageNames.Reaction(f.Alliance)} to Alliance, {UsageNames.Reaction(f.Horde)} to Horde");
        var roles = UsageNames.NpcRoles(ext[2]);
        if (ext[9] == 0 && ext[10] > 0 && roles.Contains("trainer")) roles[roles.IndexOf("trainer")] = $"{UsageNames.ClassName(ext[10])} trainer";
        if (roles.Count > 0) who.Line().Muted("Roles: ").Add(string.Join(", ", roles));

        Spawns(rep, "creature", entry);
        CreatureQuests(rep, entry, ext[4]);
        CreatureLoot(rep, ext[4], ext[5], ext[6]);
        Vendor(rep, entry);
        Trainer(rep, entry, ext[9], ext[10]);
        Abilities(rep, entry);
        SameLook(rep, npc.Displays, entry);
        Vanity(rep, npc.Displays.SelectMany(d => c.NpcsByDisplay.GetValueOrDefault(d) ?? new()).Append(entry).Distinct().ToList(), entry);
    }

    private void CreatureDisplay(UsageReport rep, int display)
    {
        var cd = Rows("SELECT model, skin1, skin2, skin3, extra_id FROM creature_display WHERE id=$0", r => (S(r, 0), new[] { S(r, 1), S(r, 2), S(r, 3) }.Where(x => x != null).ToList(), I(r, 4)), display).FirstOrDefault();
        rep.Headline.Add($"Creature display {display}", bold: true);
        if (cd.Item1 != null) rep.Headline.Muted($" · {Path.GetFileName(cd.Item1)}");
        if (cd.Item2 is { Count: > 0 }) rep.Headline.Muted(" · skin " + string.Join(", ", cd.Item2));
        if (cd.Item3 > 0) rep.Headline.Muted(" · dressed NPC (CreatureDisplayInfoExtra)");
        rep.NeedsServer = true;
        var s = rep.Add("npcs", "NPCs using it");
        if (C.Server)
        {
            var list = C.NpcsByDisplay.GetValueOrDefault(display) ?? new();
            s.Summary = list.Count == 0 ? "No creature of the server database uses this display." : UsageNames.Plural(list.Count, "NPC");
            foreach (var e in list.Take(20)) NpcLink(s.Line(), e);
            s.More = Math.Max(0, list.Count - 20);
        }
        else s.Summary = "Which NPCs use this display, where they spawn and their quests come from the server database.";
        SameLook(rep, new[] { display }, 0);
    }

    private void Spawns(UsageReport rep, string kind, int entry)
    {
        var s = rep.Add("spawns", "Where it spawns");
        if (!C.Server)
        {
            rep.NeedsServer = true;
            s.Summary = "Spawn locations come from the server database.";
            return;
        }
        var byArea = Rows("SELECT map, area, COUNT(*) FROM u_spawn WHERE kind=$0 AND entry=$1 GROUP BY map, area ORDER BY 3 DESC", r => (I(r, 0), I(r, 1), I(r, 2)), kind, entry);
        int total = byArea.Sum(x => x.Item3);
        if (total == 0)
        {
            s.Summary = "Not spawned in the world by the database (it may be summoned by a script, an event or a spell).";
            return;
        }
        var maps = byArea.Select(x => x.Item1).Distinct().Select(MapName).ToList();
        var zones = byArea.Select(x => TopZone(x.Item2, x.Item1)).Distinct().ToList();
        s.Summary = $"{UsageNames.Plural(total, "spawn")} in {UsageNames.Plural(zones.Count, "zone")} ({string.Join(", ", maps)}, map {string.Join(", ", byArea.Select(x => x.Item1).Distinct())})";
        foreach (var (map, area, n) in byArea.Take(12)) s.Line().Add(ZoneName(area, map)).Muted($"  {UsageNames.Plural(n, "spawn")}");
        s.More = Math.Max(0, byArea.Count - 12);
        var samples = Rows("SELECT map, x, y, z, area FROM u_spawn WHERE kind=$0 AND entry=$1 ORDER BY rowid LIMIT 3", r => (I(r, 0), D(r, 1), D(r, 2), D(r, 3), I(r, 4)), kind, entry);
        if (samples.Count > 0)
        {
            s.TailLine().Muted("Example positions (map, x, y, z; for .go xyz in game):");
            foreach (var (map, x, y, z, area) in samples) s.TailLine(1).Muted($"{ZoneName(area, map)}: ").Add($"{map}, {x:0.0}, {y:0.0}, {z:0.0}");
        }
    }

    private (int Area, int Map)? MainSpawn(string kind, int entry)
    {
        var r = Rows("SELECT area, map, COUNT(*) FROM u_spawn WHERE kind=$0 AND entry=$1 GROUP BY area, map ORDER BY 3 DESC LIMIT 1", x => (I(x, 0), I(x, 1)), kind, entry);
        return r.Count == 0 ? null : r[0];
    }

    private void CreatureQuests(UsageReport rep, int entry, int lootId)
    {
        if (!C.Server) return;
        var s = rep.Add("quests", "Quests");
        int n = 0;
        foreach (var (quest, role, count) in Rows("SELECT quest, role, count FROM u_quest_link WHERE kind='creature' AND ref=$0 ORDER BY CASE role WHEN 'start' THEN 0 WHEN 'kill' THEN 1 ELSE 2 END, quest", r => (I(r, 0), S(r, 1)!, I(r, 2)), entry))
        {
            if (n++ >= 20) { s.More++; continue; }
            var l = s.Line();
            l.Muted(role switch { "start" => "Gives ", "end" => "Takes back ", "kill" => count > 0 ? $"Kill {count} for " : "Kill for ", _ => role + " " });
            l.Add(QuestLabel(quest));
            if (role == "kill") QuestGivers(l, quest);
        }
        // quest items in its loot
        if (lootId > 0)
            foreach (var (item, quest) in Rows("SELECT DISTINCT l.item, q.quest FROM u_loot l JOIN u_quest_link q ON q.kind='item' AND q.role='collect' AND q.ref=l.item WHERE l.source='creature' AND l.entry=$0 AND l.quest=1 LIMIT 10",
                         r => (I(r, 0), I(r, 1)), lootId))
            {
                var l = s.Line().Muted("Drops ");
                ItemLink(l, item);
                l.Muted(" for ").Add(QuestLabel(quest));
                n++;
            }
        s.Summary = n == 0 ? "Not part of any quest." : UsageNames.Plural(n, "quest link");
    }

    /// <summary>Items of a loot table, references resolved (chances multiplied), by chance.</summary>
    private List<(int Item, double Chance, bool Quest)> LootOf(string source, int entry) =>
        Rows("""
             WITH RECURSIVE l(item, ref, chance, quest, depth) AS (
               SELECT item, ref, chance, quest, 0 FROM u_loot WHERE source=$0 AND entry=$1
               UNION ALL
               SELECT r.item, r.ref, r.chance * l.chance / 100.0, max(r.quest, l.quest), l.depth + 1
               FROM u_loot r JOIN l ON r.source='reference' AND r.entry=l.ref WHERE l.ref > 0 AND l.depth < 6)
             SELECT item, MAX(chance), MAX(quest) FROM l WHERE item > 0 GROUP BY item ORDER BY 2 DESC, 1
             """, r => (I(r, 0), D(r, 1), I(r, 2) == 1), source, entry);

    private void LootLines(UsageSection s, List<(int Item, double Chance, bool Quest)> loot, int max, string? prefix = null)
    {
        foreach (var (item, chance, quest) in loot.Take(max))
        {
            var l = s.Line();
            if (prefix != null) l.Muted(prefix);
            ItemLink(l, item);
            l.Muted($"  {UsageNames.Chance(chance)}{(quest ? " (quest)" : "")}");
        }
    }

    private void CreatureLoot(UsageReport rep, int lootId, int pickId, int skinId)
    {
        if (!C.Server) return;
        var s = rep.Add("loot", "Drops");
        var loot = lootId > 0 ? LootOf("creature", lootId) : new();
        var pick = pickId > 0 ? LootOf("pickpocket", pickId) : new();
        var skin = skinId > 0 ? LootOf("skinning", skinId) : new();
        if (loot.Count + pick.Count + skin.Count == 0)
        {
            s.Summary = "No loot.";
            return;
        }
        s.Summary = UsageNames.Plural(loot.Count, "item") + " (shared loot tables included; chance per kill)" +
                    (skin.Count > 0 ? $", skinning {skin.Count}" : "") + (pick.Count > 0 ? $", pickpocketing {pick.Count}" : "");
        LootLines(s, loot, 12);
        s.More = Math.Max(0, loot.Count - 12);
        LootLines(s, skin, 4, "Skinning: ");
        LootLines(s, pick, 4, "Pickpocketing: ");
    }

    private void Vendor(UsageReport rep, int entry)
    {
        if (!C.Server) return;
        var items = Rows("SELECT item, maxcount FROM u_vendor WHERE creature=$0", r => (I(r, 0), I(r, 1)), entry);
        if (items.Count == 0) return;
        var s = rep.Add("sells", "Sells");
        s.Summary = UsageNames.Plural(items.Count, "item");
        foreach (var (item, max) in items.OrderByDescending(i => C.Items.GetValueOrDefault(i.Item1)?.Quality ?? 0).ThenBy(i => C.Items.GetValueOrDefault(i.Item1)?.Name).Take(12))
        {
            var l = s.Line();
            ItemLink(l, item);
            if (max > 0) l.Muted($"  limited stock ({max})");
        }
        s.More = Math.Max(0, items.Count - 12);
    }

    private void Trainer(UsageReport rep, int entry, int trainerType, int trainerClass)
    {
        if (!C.Server) return;
        var spells = Rows("SELECT DISTINCT taught FROM u_trainer WHERE creature=$0", r => I(r, 0), entry);
        if (spells.Count == 0) return;
        var s = rep.Add("teaches", "Teaches");
        s.Summary = UsageNames.Plural(spells.Count, "spell") + (trainerType == 0 && trainerClass > 0 ? $" ({UsageNames.ClassName(trainerClass)} trainer)" : "");
        foreach (var sp in spells.OrderBy(SpellLabel, StringComparer.OrdinalIgnoreCase).Take(12)) s.Line().Add(SpellLabel(sp), SpellKey(sp));
        s.More = Math.Max(0, spells.Count - 12);
    }

    private void Abilities(UsageReport rep, int entry)
    {
        if (!C.Server) return;
        var spells = Rows("SELECT spell FROM u_creature_spell WHERE creature=$0", r => I(r, 0), entry);
        if (spells.Count == 0) return;
        var s = rep.Add("abilities", "Spells it casts");
        s.Summary = UsageNames.Plural(spells.Count, "spell") + " (creature template and spell list)";
        foreach (var sp in spells.Take(12)) s.Line().Add(SpellLabel(sp), SpellKey(sp)).Muted($"  spell {sp}");
        s.More = Math.Max(0, spells.Count - 12);
    }

    /// <summary>Other NPCs with the same display(s), and how widely the model is used.</summary>
    private void SameLook(UsageReport rep, IEnumerable<int> displays, int self)
    {
        var c = C;
        var s = rep.Add("look", "Same look");
        var ds = displays.ToList();
        var models = new List<string>();
        foreach (var d in ds)
        {
            var cd = Rows("SELECT model, skin1, skin2, skin3 FROM creature_display WHERE id=$0", r => (S(r, 0), string.Join(", ", new[] { S(r, 1), S(r, 2), S(r, 3) }.Where(x => x != null))), d).FirstOrDefault();
            var l = s.Line().Muted($"Display {d}: ").Add(cd.Item1 == null ? "(no model)" : Path.GetFileName(cd.Item1));
            if (cd.Item2 is { Length: > 0 }) l.Muted($", skin {cd.Item2}");
            if (cd.Item1 != null) models.Add(cd.Item1);
            if (!c.Server) continue;
            var others = (c.NpcsByDisplay.GetValueOrDefault(d) ?? new()).Where(e => e != self).ToList();
            if (others.Count == 0) continue;
            var l2 = s.Line(1).Muted($"Also used by {UsageNames.Plural(others.Count, "NPC")}: ");
            for (int i = 0; i < Math.Min(8, others.Count); i++)
            {
                if (i > 0) l2.Muted(", ");
                l2.Add(c.Npcs[others[i]].Name, $"creature:{others[i]}");
            }
            if (others.Count > 8) l2.Muted($", and {others.Count - 8:N0} more");
        }
        foreach (var m in models.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var disp = Rows("SELECT id FROM creature_display WHERE model=$0", r => I(r, 0), m);
            var npcs = c.Server ? disp.SelectMany(d => c.NpcsByDisplay.GetValueOrDefault(d) ?? new()).Distinct().Count() : 0;
            var skins = Scalar("SELECT COUNT(DISTINCT texture) FROM model_variant WHERE model=$0 AND owner_kind='creature'", m);
            s.Line().Muted("Model ").Add(Path.GetFileName(m)).Muted($": {UsageNames.Plural(disp.Count, "creature display")}, {UsageNames.Plural((int)skins, "skin texture")}" +
                                                                    (c.Server ? $", {UsageNames.Plural(npcs, "NPC")}" : "") + " (a mesh edit changes all of them)");
        }
        s.Summary = ds.Count == 1 ? "1 display" : $"{ds.Count} displays (the game picks one at random)";
    }

    /// <summary>Mounts (aura 78), companions (effect 97) and disguises (aura 56) whose creature is one of these entries.</summary>
    private void Vanity(UsageReport rep, List<int> entries, int self)
    {
        if (!C.Server || entries.Count == 0) return;
        var list = Rows($"SELECT spell, effect, aura, misc FROM u_spell_fx WHERE misc IN ({string.Join(",", entries)}) AND (aura IN (78, 56) OR effect IN (97, 28, 41))",
            r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3)));
        if (list.Count == 0) return;
        var s = rep.Add("vanity", "Mount, companion or disguise");
        foreach (var (spell, effect, aura, misc) in list.DistinctBy(x => x.Item1).Take(10))
        {
            string what = aura == 78 ? "Mount" : aura == 56 ? "Disguise (turns the target into it)" : effect == 97 ? "Companion pet" : "Summoned";
            var l = s.Line().Muted(what + ": ").Add(SpellLabel(spell), SpellKey(spell));
            if (misc != self) { l.Muted(" (as "); NpcLink(l, misc, false); l.Muted(")"); }
            var items = Rows("SELECT item FROM u_item_spell WHERE spell=$0 LIMIT 3", r => I(r, 0), spell);
            foreach (var it in items) { l.Muted(" · item "); ItemLink(l, it); }
        }
        s.Summary = UsageNames.Plural(list.DistinctBy(x => x.Item1).Count(), "spell");
    }

    // ------------------------------------------------------------------ items

    private void Item(UsageReport rep, int entry)
    {
        var c = C;
        if (!c.Items.TryGetValue(entry, out var it))
        {
            rep.Headline.Muted($"Item {entry} is not in the server database.");
            return;
        }
        var ext = Rows("SELECT item_level, required_level, set_id, start_quest, bonding FROM u_item WHERE entry=$0", r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3), I(r, 4)), entry).FirstOrDefault();
        rep.Headline.Add(it.Name, color: UsageNames.QualityColor(it.Quality), bold: true).Muted($"  {UsageNames.Quality(it.Quality)}");
        var info = rep.Add("item", "This item");
        var sub = UsageNames.ItemSubClass(it.Class, it.SubClass);
        var slot = UsageNames.InventoryType(it.InventoryType);
        var l0 = info.Line().Add(sub.Length > 0 ? sub : UsageNames.ItemClass(it.Class));
        if (sub.Length > 0) l0.Muted($" ({UsageNames.ItemClass(it.Class)})");
        if (slot.Length > 0) l0.Muted(" · slot ").Add(slot);
        if (ext.Item1 > 0 || ext.Item2 > 0) info.Line().Muted("Item level ").Add($"{ext.Item1}").Muted(" · requires level ").Add($"{Math.Max(1, ext.Item2)}");
        if (ext.Item5 > 0) info.Line().Muted(ext.Item5 switch { 1 => "Binds when picked up", 2 => "Binds when equipped", 3 => "Binds when used", 4 => "Quest item (binds)", _ => "" });
        foreach (var (spell, trig) in Rows("SELECT spell, trigger_type FROM u_item_spell WHERE item=$0", r => (I(r, 0), I(r, 1)), entry))
            info.Line().Muted(trig switch { 0 => "Use: ", 1 => "Equip: ", 2 => "Chance on hit: ", 6 => "Teaches: ", _ => "Spell: " }).Add(SpellLabel(spell), SpellKey(spell));

        ItemSources(rep, entry);
        ItemVendors(rep, entry);
        ItemQuests(rep, entry, ext.Item4);
        Crafted(rep, entry);
        ItemSet(rep, ext.Item3, entry);
        ItemLook(rep, it.Display, entry);
    }

    private void ItemDisplay(UsageReport rep, int display)
    {
        rep.Headline.Add($"Item display {display}", bold: true);
        rep.NeedsServer = true;
        var s = rep.Add("items", "Items using it");
        s.Summary = "Which items use this display, who drops or sells them and their quests come from the server database.";
        Crafted(rep, 0);
        ItemLook(rep, display, 0);
    }

    private void ItemSources(UsageReport rep, int item)
    {
        var s = rep.Add("drops", "Dropped by");
        if (!C.Server)
        {
            rep.NeedsServer = true;
            s.Summary = "Loot comes from the server database.";
            return;
        }
        var src = Rows("""
            WITH RECURSIVE src(source, entry, chance, quest, depth) AS (
              SELECT source, entry, chance, quest, 0 FROM u_loot WHERE item=$0
              UNION ALL
              SELECT l.source, l.entry, l.chance * s.chance / 100.0, s.quest, s.depth + 1
              FROM u_loot l JOIN src s ON s.source='reference' AND l.ref=s.entry WHERE s.depth < 6),
            agg AS (SELECT source, entry, MAX(chance) chance, MAX(quest) quest, MIN(depth) depth FROM src WHERE source <> 'reference' GROUP BY source, entry)
            SELECT 'creature', c.entry, a.chance, a.quest, a.depth FROM agg a JOIN u_creature c ON a.source='creature' AND c.loot_id=a.entry
            UNION ALL SELECT 'pickpocket', c.entry, a.chance, a.quest, a.depth FROM agg a JOIN u_creature c ON a.source='pickpocket' AND c.pickpocket_id=a.entry
            UNION ALL SELECT 'skinning', c.entry, a.chance, a.quest, a.depth FROM agg a JOIN u_creature c ON a.source='skinning' AND c.skinning_id=a.entry
            UNION ALL SELECT 'gameobject', g.entry, a.chance, a.quest, a.depth FROM agg a JOIN u_gameobject g ON a.source='gameobject' AND g.loot_id=a.entry
            UNION ALL SELECT a.source, a.entry, a.chance, a.quest, a.depth FROM agg a WHERE a.source IN ('item', 'fishing', 'disenchant')
            """, r => (S(r, 0)!, I(r, 1), D(r, 2), I(r, 3) == 1, I(r, 4)), item);
        var creatures = src.Where(x => x.Item1 == "creature" && C.Npcs.ContainsKey(x.Item2)).OrderByDescending(x => x.Item3).ThenBy(x => C.Npcs[x.Item2].LevelMin).ToList();
        int viaRef = creatures.Count(x => x.Item5 > 0);
        s.Summary = creatures.Count == 0 ? "No creature drops it." :
            $"{UsageNames.Plural(creatures.Count, "creature")}" + (viaRef > 0 ? $" ({viaRef:N0} through shared loot tables)" : "") + "; chance per kill";
        foreach (var (_, e, chance, quest, depth) in creatures.Take(15))
        {
            var l = s.Line();
            NpcLink(l, e);
            l.Muted($"  {UsageNames.Chance(chance)}{(quest ? " (quest)" : "")}");
            var z = MainSpawn("creature", e);
            if (z != null) l.Muted($" · {TopZone(z.Value.Area, z.Value.Map)}");
        }
        s.More = Math.Max(0, creatures.Count - 15);

        void Other(string id, string title, string source, Action<UsageLine, int> link)
        {
            var rows = src.Where(x => x.Item1 == source).OrderByDescending(x => x.Item3).ToList();
            if (rows.Count == 0) return;
            var o = rep.Add(id, title);
            o.Summary = UsageNames.Plural(rows.Count, source switch { "gameobject" => "object", "fishing" => "zone", "item" => "container", _ => "creature" });
            foreach (var (_, e, chance, quest, _) in rows.Take(10))
            {
                var l = o.Line();
                link(l, e);
                l.Muted($"  {UsageNames.Chance(chance)}{(quest ? " (quest)" : "")}");
            }
            o.More = Math.Max(0, rows.Count - 10);
        }
        Other("gathered", "Found in (chests, herbs, ore, objects)", "gameobject", (l, e) =>
        {
            GoLink(l, e);
            var g = C.Gos.GetValueOrDefault(e);
            var lk = Rows("SELECT lock_kind FROM u_gameobject WHERE entry=$0", r => S(r, 0), e).FirstOrDefault();
            if (g != null) l.Muted($" ({UsageNames.GameObjectType(g.Type, lk)})");
        });
        Other("skinned", "Skinned from", "skinning", (l, e) => NpcLink(l, e));
        Other("pickpocketed", "Pickpocketed from", "pickpocket", (l, e) => NpcLink(l, e));
        Other("contained", "Inside other items", "item", (l, e) => ItemLink(l, e));
        Other("fished", "Fished in", "fishing", (l, e) => l.Add(ZoneName(e, -1)));
        Other("disenchanted", "Disenchanting", "disenchant", (l, e) => l.Muted($"disenchant table {e}"));
    }

    private void ItemVendors(UsageReport rep, int item)
    {
        if (!C.Server) return;
        var vendors = Rows("SELECT creature, maxcount FROM u_vendor WHERE item=$0", r => (I(r, 0), I(r, 1)), item).Where(v => C.Npcs.ContainsKey(v.Item1)).ToList();
        var s = rep.Add("vendors", "Sold by");
        if (vendors.Count == 0)
        {
            s.Summary = "No vendor sells it.";
            return;
        }
        s.Summary = UsageNames.Plural(vendors.Count, "vendor");
        foreach (var (e, max) in vendors.OrderBy(v => C.Npcs[v.Item1].Name).Take(15))
        {
            var l = s.Line();
            NpcLink(l, e, false);
            var z = MainSpawn("creature", e);
            l.Muted(z != null ? $"  {ZoneName(z.Value.Area, z.Value.Map)}" : "  (not spawned)");
            if (max > 0) l.Muted($" · limited stock ({max})");
        }
        s.More = Math.Max(0, vendors.Count - 15);
    }

    private void ItemQuests(UsageReport rep, int item, int startQuest)
    {
        if (!C.Server) return;
        var links = Rows("SELECT quest, role, count FROM u_quest_link WHERE kind='item' AND ref=$0 ORDER BY role, quest", r => (I(r, 0), S(r, 1)!, I(r, 2)), item);
        var s = rep.Add("quests", "Quests");
        if (links.Count == 0)
        {
            s.Summary = "Not part of any quest.";
            return;
        }
        s.Summary = UsageNames.Plural(links.Count, "quest link");
        foreach (var (quest, role, count) in links.Take(15))
        {
            var l = s.Line().Muted(role switch
            {
                "reward" => "Reward of ", "choice" => "Choice of reward of ", "collect" => count > 1 ? $"Collect {count} for " : "Needed for ",
                "provided" => "Given to you by ", "source" => "Used by ", "start" => "Starts ", _ => role + " ",
            });
            l.Add(QuestLabel(quest));
            QuestGivers(l, quest);
        }
        s.More = Math.Max(0, links.Count - 15);
    }

    private void Crafted(UsageReport rep, int item)
    {
        if (item <= 0) return;
        var spells = Rows("SELECT DISTINCT spell FROM u_spell_fx WHERE effect=24 AND item=$0", r => I(r, 0), item);
        var s = rep.Add("crafted", "Crafted by");
        if (spells.Count == 0)
        {
            s.Summary = "No spell creates it.";
            return;
        }
        s.Summary = $"Created by {UsageNames.Plural(spells.Count, "spell")}";
        foreach (var sp in spells.Take(10))
        {
            var l = s.Line().Add(SpellLabel(sp), SpellKey(sp));
            var prof = Rows("SELECT name FROM u_spell_skill WHERE spell=$0 AND category IN (9, 11) LIMIT 1", r => S(r, 0), sp).FirstOrDefault();
            if (prof != null) l.Muted($"  {prof}");
            if (C.Server)
            {
                var trainers = Scalar("SELECT COUNT(DISTINCT creature) FROM u_trainer WHERE taught=$0", sp);
                if (trainers > 0) l.Muted($" · taught by {UsageNames.Plural((int)trainers, "trainer")}");
                foreach (var rec in Rows("SELECT item FROM u_item_spell WHERE spell=$0 AND trigger_type=6 LIMIT 2", r => I(r, 0), sp))
                {
                    l.Muted(" · recipe ");
                    ItemLink(l, rec);
                }
            }
        }
        s.More = Math.Max(0, spells.Count - 10);
    }

    private void ItemSet(UsageReport rep, int setId, int self)
    {
        if (setId <= 0) return;
        var name = Rows("SELECT name FROM u_item_set WHERE id=$0", r => S(r, 0), setId).FirstOrDefault() ?? $"item set {setId}";
        var members = Rows("SELECT entry FROM u_item WHERE set_id=$0 ORDER BY entry", r => I(r, 0), setId);
        var s = rep.Add("set", "Item set");
        s.Summary = $"{name} ({UsageNames.Plural(members.Count, "piece")})";
        foreach (var m in members.Take(12))
        {
            var l = s.Line();
            if (m == self) l.Add(C.Items.GetValueOrDefault(m)?.Name ?? $"item {m}", bold: true).Muted("  (this item)");
            else ItemLink(l, m);
        }
    }

    private void ItemLook(UsageReport rep, int display, int self)
    {
        var c = C;
        var s = rep.Add("look", "Same look");
        var models = Rows("SELECT DISTINCT path FROM asset WHERE kind='item' AND display_id=$0 AND role LIKE 'model%' AND role NOT LIKE 'model texture%'", r => S(r, 0)!, display);
        s.Summary = $"Item display {display}" + (models.Count == 0 ? " (icon and body textures only, no 3D model)" : $" · {string.Join(", ", models.Take(3).Select(Path.GetFileName))}{(models.Count > 3 ? ", ..." : "")}");
        if (c.Server)
        {
            var others = (c.ItemsByDisplay.GetValueOrDefault(display) ?? new()).Where(e => e != self).ToList();
            if (others.Count > 0)
            {
                var l = s.Line().Muted($"Also used by {UsageNames.Plural(others.Count, "item")}: ");
                for (int i = 0; i < Math.Min(10, others.Count); i++)
                {
                    if (i > 0) l.Muted(", ");
                    ItemLink(l, others[i]);
                }
                if (others.Count > 10) l.Muted($", and {others.Count - 10:N0} more");
            }
        }
        foreach (var m in models.Take(2))
        {
            var disp = Rows("SELECT DISTINCT owner_id FROM model_variant WHERE model=$0 AND owner_kind='item'", r => I(r, 0), m);
            var items = c.Server ? disp.Sum(d => c.ItemsByDisplay.GetValueOrDefault(d)?.Count ?? 0) : 0;
            s.Line().Muted("Model ").Add(Path.GetFileName(m)).Muted($": {UsageNames.Plural(disp.Count, "item display")}" + (c.Server ? $", {UsageNames.Plural(items, "item")}" : ""));
        }
    }

    // ------------------------------------------------------------------ game objects

    private void GameObject(UsageReport rep, int entry)
    {
        var c = C;
        if (!c.Gos.TryGetValue(entry, out var g))
        {
            rep.Headline.Muted($"Game object {entry} is not in the server database.");
            return;
        }
        var ext = Rows("SELECT type, data0, data1, loot_id, lock_kind FROM u_gameobject WHERE entry=$0", r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3), S(r, 4)), entry).FirstOrDefault();
        var typeName = UsageNames.GameObjectType(g.Type, ext.Item5);
        rep.Headline.Add(g.Name, bold: true).Muted($" · {typeName}");
        var info = rep.Add("object", "This object");
        info.Line().Muted("Type ").Add(typeName).Muted($" (game object type {g.Type})");
        if (g.Type == 9) info.Line().Muted($"Shows page text {ext.Item2} when read.");
        if (g.Type == 8) info.Line().Muted($"Spell focus {ext.Item2} (spells that need it can be cast nearby).");
        Spawns(rep, "gameobject", entry);
        if (c.Server && ext.Item4 > 0)
        {
            var loot = LootOf("gameobject", ext.Item4);
            var s = rep.Add("contains", g.Type == 25 ? "Fish caught here" : ext.Item5 is "herb" or "mining" ? "Gathered from it" : "Contains");
            s.Summary = loot.Count == 0 ? "Empty loot table." : UsageNames.Plural(loot.Count, "item") + " (chance per opening)";
            LootLines(s, loot, 15);
            s.More = Math.Max(0, loot.Count - 15);
        }
        if (c.Server)
        {
            var s = rep.Add("quests", "Quests");
            int n = 0;
            foreach (var (quest, role, count) in Rows("SELECT quest, role, count FROM u_quest_link WHERE kind='gameobject' AND ref=$0 ORDER BY CASE role WHEN 'start' THEN 0 WHEN 'use' THEN 1 ELSE 2 END, quest", r => (I(r, 0), S(r, 1)!, I(r, 2)), entry).Take(20))
            {
                var l = s.Line().Muted(role switch { "start" => "Gives ", "end" => "Takes back ", "use" => count > 1 ? $"Use {count} times for " : "Use for ", _ => role + " " });
                l.Add(QuestLabel(quest));
                if (role == "use") QuestGivers(l, quest);
                n++;
            }
            if (ext.Item4 > 0)
                foreach (var (item, quest) in Rows("SELECT DISTINCT l.item, q.quest FROM u_loot l JOIN u_quest_link q ON q.kind='item' AND q.role='collect' AND q.ref=l.item WHERE l.source='gameobject' AND l.entry=$0 LIMIT 10",
                             r => (I(r, 0), I(r, 1)), ext.Item4))
                {
                    var l = s.Line().Muted("Holds ");
                    ItemLink(l, item);
                    l.Muted(" for ").Add(QuestLabel(quest));
                    QuestGivers(l, quest);
                    n++;
                }
            s.Summary = n == 0 ? "Not part of any quest." : UsageNames.Plural(n, "quest link");
        }
        GoLook(rep, g.Display, entry);
    }

    private void GameObjectDisplay(UsageReport rep, int display)
    {
        rep.Headline.Add($"Game object display {display}", bold: true);
        rep.NeedsServer = true;
        var s = rep.Add("objects", "Objects using it");
        s.Summary = "Which objects use this display, where they are and what they contain come from the server database.";
        GoLook(rep, display, 0);
    }

    private void GoLook(UsageReport rep, int display, int self)
    {
        var c = C;
        var model = Rows("SELECT model FROM gameobject_display WHERE id=$0", r => S(r, 0), display).FirstOrDefault();
        var s = rep.Add("look", "Same look");
        s.Summary = $"Game object display {display}" + (model != null ? $" · {Path.GetFileName(model)}" : "");
        if (c.Server)
        {
            var others = (c.GosByDisplay.GetValueOrDefault(display) ?? new()).Where(e => e != self).ToList();
            if (others.Count > 0)
            {
                var l = s.Line().Muted($"Also used by {UsageNames.Plural(others.Count, "object")}: ");
                for (int i = 0; i < Math.Min(10, others.Count); i++)
                {
                    if (i > 0) l.Muted(", ");
                    GoLink(l, others[i]);
                }
                if (others.Count > 10) l.Muted($", and {others.Count - 10:N0} more");
            }
        }
        if (model != null)
        {
            var disp = Scalar("SELECT COUNT(*) FROM gameobject_display WHERE model=$0", model);
            var placed = Scalar("SELECT SUM(count) FROM u_place WHERE model=$0", model);
            s.Line().Muted("Model ").Add(Path.GetFileName(model), $"file:{model}").Muted($": {UsageNames.Plural((int)disp, "object display")}" +
                (placed > 0 ? $", also placed {placed:N0} times in the map files" : ""));
        }
    }

    // ------------------------------------------------------------------ spell effects

    private void SpellEffect(UsageReport rep, int effect)
    {
        var e = Rows("SELECT name, model FROM spell_effect WHERE id=$0", r => (S(r, 0), S(r, 1)), effect).FirstOrDefault();
        rep.Headline.Add(e.Item1 ?? $"Spell effect {effect}", bold: true);
        if (e.Item2 != null) rep.Headline.Muted($" · {Path.GetFileName(e.Item2)}");
        var uses = Rows("SELECT spell_id, group_concat(DISTINCT role), MIN(visual_id) FROM spell_effect_use WHERE effect_id=$0 GROUP BY spell_id", r => (I(r, 0), S(r, 1) ?? "", I(r, 2)), effect);
        var s = rep.Add("spells", "Spells that show it");
        var names = uses.Select(u => C.Spells.GetValueOrDefault(u.Item1).Name ?? "").Where(n => n.Length > 0).Distinct().Count();
        s.Summary = uses.Count == 0 ? "No spell shows this effect (Spell -> SpellVisual -> SpellVisualKit / missile)." :
            $"{UsageNames.Plural(uses.Count, "spell")} ({UsageNames.Plural(names, "name")}); visual {string.Join(", ", uses.Select(u => u.Item3).Distinct().Take(4))}{(uses.Select(u => u.Item3).Distinct().Count() > 4 ? ", ..." : "")}";
        // one line per spell name (ranks and copies together); spells with many ranks (the player's) first, then by name
        var byName = uses.GroupBy(u => C.Spells.TryGetValue(u.Item1, out var sp) ? sp.Name : $"spell {u.Item1}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var g in byName.Take(40))
        {
            var ids = g.Select(u => u.Item1).OrderBy(x => x).ToList();
            var ranks = ids.Select(i => C.Spells.GetValueOrDefault(i).Rank ?? "").Where(r => r.Length > 0).Distinct().ToList();
            var roles = g.SelectMany(u => u.Item2.Split(',')).Distinct().ToList();
            var l = s.Line().Add(g.Key);
            if (ranks.Count == 1) l.Muted($" ({ranks[0]})");
            else if (ranks.Count > 1) l.Muted($" ({UsageNames.Plural(ranks.Count, "rank")}: {ranks[0]} to {ranks[^1]})");
            l.Muted($"  {(ids.Count == 1 ? "spell" : "spells")} {string.Join(", ", ids.Take(6))}{(ids.Count > 6 ? ", ..." : "")} · {string.Join(", ", roles)}");
        }
        s.More = Math.Max(0, byName.Count - 40);

        if (uses.Count > 0)
        {
            var ids = string.Join(",", uses.Select(u => u.Item1));
            var w = rep.Add("casters", "Who casts it");
            if (!C.Server)
            {
                rep.NeedsServer = true;
                w.Summary = "Which creatures cast these spells and which trainers teach them come from the server database.";
            }
            else
            {
                var npcs = Rows($"SELECT DISTINCT creature FROM u_creature_spell WHERE spell IN ({ids})", r => I(r, 0)).Where(C.Npcs.ContainsKey).ToList();
                var trainers = Rows($"SELECT c.trainer_class, c.trainer_type, COUNT(DISTINCT t.creature) FROM u_trainer t JOIN u_creature c ON c.entry=t.creature WHERE t.taught IN ({ids}) GROUP BY 1, 2",
                    r => (I(r, 0), I(r, 1), I(r, 2)));
                var items = Rows($"SELECT DISTINCT item FROM u_item_spell WHERE spell IN ({ids}) LIMIT 30", r => I(r, 0));
                var parts = new List<string>();
                if (trainers.Count > 0) parts.Add("player spell");
                if (npcs.Count > 0) parts.Add(UsageNames.Plural(npcs.Count, "creature"));
                if (items.Count > 0) parts.Add(UsageNames.Plural(items.Count, "item"));
                w.Summary = parts.Count == 0 ? "No creature, trainer or item of the server database uses these spells (they may be cast by scripts or other spells)." : string.Join(", ", parts);
                foreach (var (cls, type, n) in trainers)
                    w.Line().Muted("Players: taught by ").Add(type == 0 && cls > 0 ? $"{UsageNames.ClassName(cls)} trainers" : type == 2 ? "profession trainers" : type == 3 ? "pet trainers" : "trainers")
                        .Muted($" ({UsageNames.Plural(n, "trainer")})");
                foreach (var n in npcs.OrderBy(x => C.Npcs[x].LevelMin).Take(12))
                {
                    var l = w.Line().Muted("Creature: ");
                    NpcLink(l, n);
                    var z = MainSpawn("creature", n);
                    if (z != null) l.Muted($" · {TopZone(z.Value.Area, z.Value.Map)}");
                }
                if (npcs.Count > 12) w.Line().Muted($"and {npcs.Count - 12:N0} more creatures");
                foreach (var it in items.Take(6))
                {
                    var l = w.Line().Muted("Item: ");
                    ItemLink(l, it);
                }
            }
        }
        if (e.Item2 != null)
        {
            var same = Rows("SELECT id, name FROM spell_effect WHERE model=$0 AND id<>$1 ORDER BY id LIMIT 10", r => (I(r, 0), S(r, 1) ?? ""), e.Item2, effect);
            if (same.Count > 0)
            {
                var m = rep.Add("look", "Same model");
                m.Summary = $"{Path.GetFileName(e.Item2)} is also used by {UsageNames.Plural(same.Count, "other effect")}";
                foreach (var (id, name) in same) m.Line().Add(name, $"spell:{id}").Muted($"  effect {id}");
            }
        }
    }

    // ------------------------------------------------------------------ characters

    private void Character(UsageReport rep, int race, int sex)
    {
        var c = C;
        var raceName = c.Races.GetValueOrDefault(race, $"race {race}");
        rep.Headline.Add($"{raceName} {(sex == 0 ? "Male" : "Female")}", bold: true).Muted(" · player character");
        var s = rep.Add("character", "Character creation choices");
        var rows = Rows("SELECT section, variation, color, flags FROM char_section WHERE race=$0 AND sex=$1", r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3)), race, sex);
        int Distinct(int section, Func<(int, int, int, int), int> f) => rows.Where(r => r.Item1 == section && r.Item4 == 0).Select(f).Distinct().Count();
        s.Summary = $"{UsageNames.Plural(rows.Count, "CharSections row")} (the textures behind each choice at character creation)";
        s.Line().Muted("Skin colours: ").Add($"{Distinct(0, r => r.Item3)}");
        s.Line().Muted("Faces: ").Add($"{Distinct(1, r => r.Item2)}").Muted(" (one texture per face and skin colour)");
        s.Line().Muted("Hair styles: ").Add($"{Distinct(3, r => r.Item2)}").Muted(", hair colours: ").Add($"{Distinct(3, r => r.Item3)}");
        s.Line().Muted("Facial hair styles: ").Add($"{Distinct(2, r => r.Item2)}").Muted(sex == 0 ? "" : " (markings, piercings...)");
        int special = rows.Count(r => r.Item4 != 0);
        if (special > 0) s.Line().Muted($"{special} more row{(special == 1 ? "" : "s")} with flags 1 (alternative skin/underwear textures, the _1xx files)");
        var disp = Rows("SELECT male_display, female_display FROM race WHERE id=$0", r => (I(r, 0), I(r, 1)), race).FirstOrDefault();
        var model = Rows("SELECT model FROM creature_display WHERE id=$0", r => S(r, 0), sex == 0 ? disp.Item1 : disp.Item2).FirstOrDefault();
        if (model != null) ModelUsers(rep, model, "users", "NPCs with this body");
    }

    // ------------------------------------------------------------------ textures

    private void Texture(UsageReport rep, string path)
    {
        var c = C;
        rep.Headline.Add(Path.GetFileName(path), bold: true).Muted(" · texture");
        var affects = rep.Add("affects", "Changing this texture affects");
        var summary = new List<string>();

        // displays (asset rows: skins, model textures, item textures and icons, game object textures)
        var assets = Rows("SELECT kind, display_id, role FROM asset WHERE path=$0 ORDER BY kind, display_id", r => (S(r, 0)!, I(r, 1), S(r, 2)!), path);
        var cDisp = assets.Where(a => a.Item1 == "creature").Select(a => a.Item2).Distinct().ToList();
        var iDisp = assets.Where(a => a.Item1 == "item").Select(a => a.Item2).Distinct().ToList();
        var gDisp = assets.Where(a => a.Item1 == "gameobject").Select(a => a.Item2).Distinct().ToList();
        bool icon = assets.Any(a => a.Item1 == "item" && a.Item3 == "icon");
        if (cDisp.Count > 0)
        {
            var npcs = cDisp.SelectMany(d => c.NpcsByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            summary.Add(UsageNames.Plural(cDisp.Count, "creature display") + (c.Server ? $" ({UsageNames.Plural(npcs.Count, "NPC")})" : ""));
            var s = rep.Add("creatures", "Creatures");
            s.Summary = $"{UsageNames.Plural(cDisp.Count, "display")}: {string.Join(", ", cDisp.Take(12))}{(cDisp.Count > 12 ? ", ..." : "")}";
            if (c.Server)
            {
                foreach (var n in npcs.OrderBy(x => c.Npcs[x].Name).Take(15))
                {
                    var l = s.Line();
                    NpcLink(l, n);
                    l.Muted($" · display {string.Join(", ", c.Npcs[n].Displays.Intersect(cDisp))}");
                }
                s.More = Math.Max(0, npcs.Count - 15);
            }
            else
                foreach (var d in cDisp.Take(15)) s.Line().Add($"Creature display {d}", $"cdisplay:{d}").Muted("  " + string.Join(", ", assets.Where(a => a.Item1 == "creature" && a.Item2 == d).Select(a => a.Item3).Distinct()));
        }
        if (iDisp.Count > 0)
        {
            var items = iDisp.SelectMany(d => c.ItemsByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            summary.Add(UsageNames.Plural(iDisp.Count, "item display") + (c.Server ? $" ({UsageNames.Plural(items.Count, "item")})" : "") + (icon ? ", as the inventory icon" : ""));
            var s = rep.Add("items", icon ? "Items (inventory icon)" : "Items");
            s.Summary = UsageNames.Plural(iDisp.Count, "item display");
            if (c.Server)
            {
                foreach (var it in items.OrderByDescending(x => c.Items[x].Quality).ThenBy(x => c.Items[x].Name).Take(15)) ItemLink(s.Line(), it);
                s.More = Math.Max(0, items.Count - 15);
            }
            else
                foreach (var d in iDisp.Take(15)) s.Line().Add($"Item display {d}", $"idisplay:{d}");
        }
        if (gDisp.Count > 0)
        {
            var gos = gDisp.SelectMany(d => c.GosByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            summary.Add(UsageNames.Plural(gDisp.Count, "game object display") + (c.Server ? $" ({UsageNames.Plural(gos.Count, "object")})" : ""));
            var s = rep.Add("gameobjects", "Game objects");
            s.Summary = UsageNames.Plural(gDisp.Count, "display");
            if (c.Server) foreach (var g in gos.Take(15)) GoLink(s.Line(), g);
            else foreach (var d in gDisp.Take(15)) s.Line().Add($"Game object display {d}", $"godisplay:{d}");
            s.More = Math.Max(0, (c.Server ? gos.Count : gDisp.Count) - 15);
        }

        // character creation
        var secs = Rows("SELECT race, sex, section, variation, color, flags FROM char_section WHERE texture1=$0 OR texture2=$0 OR texture3=$0 ORDER BY race, sex, section, variation, color",
            r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3), I(r, 4), I(r, 5)), path);
        if (secs.Count > 0)
        {
            summary.Add(UsageNames.Plural(secs.Count, "character-creation choice"));
            var s = rep.Add("character", "Character creation");
            s.Summary = string.Join(", ", secs.Select(x => $"{c.Races.GetValueOrDefault(x.Item1, "race " + x.Item1)} {(x.Item2 == 0 ? "Male" : "Female")}").Distinct());
            foreach (var (race, sex, section, variation, color, flags) in secs.Take(12))
            {
                var count = Rows("SELECT COUNT(DISTINCT color), COUNT(DISTINCT variation) FROM char_section WHERE race=$0 AND sex=$1 AND section=$2 AND flags=0", r => (I(r, 0), I(r, 1)), race, sex, section).First();
                var l = s.Line().Add($"{c.Races.GetValueOrDefault(race, "race " + race)} {(sex == 0 ? "Male" : "Female")}", $"race:{race}:{sex}").Muted(" · ");
                l.Add(section switch
                {
                    0 => $"Skin colour {color + 1} of {count.Item1}",
                    1 => $"Face {variation + 1} of {count.Item2}, for skin colour {color + 1}",
                    2 => $"Facial hair {variation + 1} of {count.Item2}, hair colour {color + 1}",
                    3 => $"Hair style {variation + 1} of {count.Item2}, hair colour {color + 1} of {count.Item1}",
                    4 => $"Underwear, for skin colour {color + 1}",
                    _ => UsageNames.Section(section),
                });
                l.Muted($"  (CharSections {UsageNames.Section(section)}, variation {variation}, colour {color}{(flags != 0 ? $", flags {flags}" : "")})");
            }
            s.More = Math.Max(0, secs.Count - 12);
        }

        // models that name it inside the file (doodads, buildings, spell models, creature/item models)
        var models = Rows("SELECT model FROM u_model_texture WHERE texture=$0 ORDER BY model", r => S(r, 0)!, path);
        if (models.Count > 0)
        {
            var placed = Rows($"SELECT model, SUM(count), COUNT(DISTINCT map || ':' || area) FROM u_place WHERE model IN ({string.Join(",", models.Select((_, i) => "$" + i))}) GROUP BY model",
                r => (S(r, 0)!, I(r, 1), I(r, 2)), models.Cast<object?>().ToArray()).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.OrdinalIgnoreCase);
            int total = placed.Values.Sum(v => v.Item1);
            var zones = models.Count == 0 ? 0 : (int)Scalar($"SELECT COUNT(DISTINCT area) FROM u_place WHERE model IN ({string.Join(",", models.Select((_, i) => "$" + i))})", models.Cast<object?>().ToArray());
            summary.Add(UsageNames.Plural(models.Count, "model") + (total > 0 ? $" (placed {total:N0} times in the world, {UsageNames.Plural(zones, "area")})" : ""));
            var s = rep.Add("models", "Models that use it");
            s.Summary = UsageNames.Plural(models.Count, "model file");
            foreach (var m in models.Take(15))
            {
                var l = s.Line().Add(Path.GetFileName(m), $"file:{m}");
                if (placed.TryGetValue(m, out var p)) l.Muted($"  placed {p.Item1:N0} times");
                else l.Muted("  " + Path.GetDirectoryName(m));
            }
            s.More = Math.Max(0, models.Count - 15);
            var fx = Rows($"SELECT id, name FROM spell_effect WHERE model IN ({string.Join(",", models.Select((_, i) => "$" + i))})", r => (I(r, 0), S(r, 1) ?? ""), models.Cast<object?>().ToArray());
            if (fx.Count > 0)
            {
                summary.Add(UsageNames.Plural(fx.Count, "spell effect"));
                var e = rep.Add("effects", "Spell effects");
                e.Summary = UsageNames.Plural(fx.Count, "effect");
                foreach (var (id, name) in fx.Take(10)) e.Line().Add(name, $"spell:{id}").Muted($"  effect {id}");
            }
        }

        // ground (0.6.1): a ground texture named by map tiles, or the "_s" specular variant of one
        GroundTexture(rep, path, summary);

        // interface: spell icons
        var iconIds = Rows("SELECT id FROM u_spell_icon WHERE path=$0", r => I(r, 0), path);
        if (iconIds.Count > 0)
        {
            var spells = Rows($"SELECT id FROM u_spell WHERE icon IN ({string.Join(",", iconIds)}) ORDER BY name, id", r => I(r, 0));
            summary.Add($"the icon of {UsageNames.Plural(spells.Count, "spell")}");
            var s = rep.Add("interface", "Interface");
            s.Summary = $"Spell icon {string.Join(", ", iconIds)}: {UsageNames.Plural(spells.Count, "spell")}";
            foreach (var sp in spells.DistinctBy(SpellLabel).Take(15)) s.Line().Add(SpellLabel(sp), SpellKey(sp));
            s.More = Math.Max(0, spells.DistinctBy(SpellLabel).Count() - 15);
        }
        else if (path.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase) && !icon)
        {
            summary.Add("interface art (used by the game's UI files)");
        }

        if (summary.Count == 0) affects.Summary = "Nothing found that uses it (it may be loaded by name from the UI or by the engine).";
        else
        {
            affects.Summary = string.Join("; ", summary);
            foreach (var x in summary) affects.Line().Add(x);
        }
        if (!c.Server && (cDisp.Count + iDisp.Count + gDisp.Count) > 0) rep.NeedsServer = true;
    }

    private void GroundTexture(UsageReport rep, string path, List<string> summary)
    {
        var name = Mpq.ClientData.ResolveName(path);
        var baseOfS = Maps.GroundTextures.BaseNameOfSpecular(name);
        string groundName = name;
        bool isSpecular = false;
        List<(int, int, int)> Tiles(string p)
        {
            try { return Rows("SELECT map, tx, ty FROM u_ground_texture WHERE texture=$0", r => (I(r, 0), I(r, 1), I(r, 2)), p); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return new(); }
        }
        var tiles = Tiles(name);
        if (tiles.Count == 0 && baseOfS != null)
        {
            tiles = Tiles(baseOfS);
            if (tiles.Count > 0)
            {
                isSpecular = true;
                groundName = baseOfS;
            }
        }
        if (tiles.Count == 0) return;
        var specName = Maps.GroundTextures.SpecularName(groundName);
        bool hasSpec = specName != null && Scalar("SELECT COUNT(*) FROM files WHERE path=$0", specName) > 0;
        // the zones of those tiles (the most common area of each tile's chunks)
        var zones = new Dictionary<(int Map, string Zone), int>();
        foreach (var (map, tx, ty) in tiles)
        {
            byte[]? blob = null;
            try { blob = Rows("SELECT areas FROM u_area_chunk WHERE map=$0 AND tx=$1 AND ty=$2", r => r.IsDBNull(0) ? null : (byte[])r.GetValue(0), map, tx, ty).FirstOrDefault(); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            int area = 0;
            if (blob != null && blob.Length >= 2)
                area = Enumerable.Range(0, blob.Length / 2).Select(i => (int)BitConverter.ToUInt16(blob, i * 2)).Where(a => a > 0).GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var k = (map, TopZone(area, map));
            zones[k] = zones.GetValueOrDefault(k) + 1;
        }
        var byMap = tiles.GroupBy(t => t.Item1).OrderByDescending(g => g.Count()).ToList();
        string where = string.Join(", ", byMap.Take(3).Select(g => $"{MapName(g.Key)} {g.Count()}")) + (byMap.Count > 3 ? ", ..." : "");
        summary.Add(isSpecular
            ? $"the ground of {UsageNames.Plural(tiles.Count, "map tile")} when terrain specular is on (it is the _s variant of {Path.GetFileName(groundName)})"
            : $"the ground of {UsageNames.Plural(tiles.Count, "map tile")}" + (hasSpec ? $" when terrain specular is off ({Path.GetFileName(specName)} is drawn when it is on)" : ""));
        var s = rep.Add("ground", "Ground (map terrain)");
        s.Summary = $"Painted on {UsageNames.Plural(tiles.Count, "map tile")}: {where}";
        foreach (var ((map, zone), n) in zones.OrderByDescending(z => z.Value).Take(12))
            s.Line().Add(zone).Muted($"  {MapName(map)} · {UsageNames.Plural(n, "tile")}");
        s.More = Math.Max(0, zones.Count - 12);
        var l = s.TailLine();
        if (hasSpec)
        {
            l.Muted("With terrain specular on (Config.wtf: specular 1 and pixelShaders 1) the game draws ").Add(Path.GetFileName(specName)!, "file:" + specName)
             .Muted(" instead of ").Add(Path.GetFileName(groundName), "file:" + groundName).Muted(". Edit both so your change shows with any settings.");
            if (isSpecular) s.TailLine().Muted(Maps.GroundTextures.SpecularAlphaNote);
        }
        else l.Muted($"The game has no {Path.GetFileName(specName)}: with terrain specular on this ground is not drawn from {Path.GetFileName(groundName)}.");
    }

    // ------------------------------------------------------------------ models

    private void Model(UsageReport rep, string path)
    {
        rep.Headline.Add(Path.GetFileName(path), bold: true).Muted(path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) ? " · building / structure (WMO)" : " · model (M2)");
        Placements(rep, path);
        ModelUsers(rep, path, "users", "Used by");
    }

    private void Placements(UsageReport rep, string model)
    {
        var rows = Rows("SELECT map, tx, ty, area, parent, count FROM u_place WHERE model=$0", r => (I(r, 0), I(r, 1), I(r, 2), I(r, 3), S(r, 4) ?? "", I(r, 5)), model);
        var s = rep.Add("placements", "Placed in the world");
        if (rows.Count == 0)
        {
            s.Summary = "Not placed by any map tile or building (it can still be used by a display, a spell or a script).";
            return;
        }
        int total = rows.Sum(r => r.Item6);
        int inside = rows.Where(r => r.Item5.Length > 0).Sum(r => r.Item6);
        var byZone = rows.GroupBy(r => (Map: r.Item1, Area: r.Item4)).Select(g => (g.Key.Map, g.Key.Area, Count: g.Sum(x => x.Item6))).OrderByDescending(x => x.Count).ToList();
        var zones = byZone.Select(z => TopZone(z.Area, z.Map)).Distinct().Count();
        var tiles = rows.Select(r => (r.Item1, r.Item2, r.Item3)).Distinct().Count();
        s.Summary = $"{total:N0} placements in {UsageNames.Plural(zones, "zone")}, {UsageNames.Plural(tiles, "map tile")}" + (inside > 0 ? $" ({inside:N0} inside buildings)" : "");
        foreach (var (map, area, n) in byZone.Take(12)) s.Line().Add(ZoneName(area, map)).Muted($"  {n:N0}  ({MapName(map)})");
        s.More = Math.Max(0, byZone.Count - 12);
        var topTiles = rows.GroupBy(r => (r.Item1, r.Item2, r.Item3)).Select(g => (g.Key, g.Sum(x => x.Item6))).OrderByDescending(x => x.Item2).Take(6).ToList();
        s.TailLine().Muted("Map tiles (ADT files): " + string.Join(", ", topTiles.Select(t => $"{C.MapDirs.GetValueOrDefault(t.Key.Item1, "map" + t.Key.Item1)}_{t.Key.Item2}_{t.Key.Item3} ({t.Item2:N0})")) + (tiles > 6 ? ", ..." : ""));
        var parents = rows.Where(r => r.Item5.Length > 0).GroupBy(r => r.Item5).Select(g => (g.Key, g.Sum(x => x.Item6))).OrderByDescending(x => x.Item2).Take(5).ToList();
        foreach (var (wmo, n) in parents) s.TailLine().Muted("Inside ").Add(Path.GetFileName(wmo), $"file:{wmo}").Muted($"  {n:N0}");
    }

    /// <summary>Every display / effect / race that uses a model file.</summary>
    private void ModelUsers(UsageReport rep, string model, string id, string title)
    {
        var c = C;
        var s = rep.Add(id, title);
        var parts = new List<string>();
        var cd = Rows("SELECT id FROM creature_display WHERE model=$0 ORDER BY id", r => I(r, 0), model);
        if (cd.Count > 0)
        {
            var npcs = cd.SelectMany(d => c.NpcsByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            parts.Add(UsageNames.Plural(cd.Count, "creature display") + (c.Server ? $" ({UsageNames.Plural(npcs.Count, "NPC")})" : ""));
            var l = s.Line().Muted("Creatures: ");
            if (c.Server && npcs.Count > 0)
            {
                for (int i = 0; i < Math.Min(10, npcs.Count); i++)
                {
                    if (i > 0) l.Muted(", ");
                    l.Add(c.Npcs[npcs[i]].Name, $"creature:{npcs[i]}");
                }
                if (npcs.Count > 10) l.Muted($", and {npcs.Count - 10:N0} more");
            }
            else
            {
                for (int i = 0; i < Math.Min(10, cd.Count); i++)
                {
                    if (i > 0) l.Muted(", ");
                    l.Add($"display {cd[i]}", c.Server ? null : $"cdisplay:{cd[i]}");
                }
                if (cd.Count > 10) l.Muted($", and {cd.Count - 10:N0} more");
            }
        }
        var idisp = Rows("SELECT DISTINCT display_id FROM asset WHERE kind='item' AND path=$0 AND role LIKE 'model%' AND role NOT LIKE 'model texture%'", r => I(r, 0), model);
        if (idisp.Count > 0)
        {
            var items = idisp.SelectMany(d => c.ItemsByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            parts.Add(UsageNames.Plural(idisp.Count, "item display") + (c.Server ? $" ({UsageNames.Plural(items.Count, "item")})" : ""));
            var l = s.Line().Muted("Items: ");
            var shown = c.Server ? items.Take(10).ToList() : new List<int>();
            for (int i = 0; i < shown.Count; i++)
            {
                if (i > 0) l.Muted(", ");
                ItemLink(l, shown[i]);
            }
            if (!c.Server) for (int i = 0; i < Math.Min(10, idisp.Count); i++) { if (i > 0) l.Muted(", "); l.Add($"item display {idisp[i]}", $"idisplay:{idisp[i]}"); }
            int more = (c.Server ? items.Count : idisp.Count) - 10;
            if (more > 0) l.Muted($", and {more:N0} more");
        }
        var gd = Rows("SELECT id FROM gameobject_display WHERE model=$0", r => I(r, 0), model);
        if (gd.Count > 0)
        {
            var gos = gd.SelectMany(d => c.GosByDisplay.GetValueOrDefault(d) ?? new()).Distinct().ToList();
            parts.Add(UsageNames.Plural(gd.Count, "game object display") + (c.Server ? $" ({UsageNames.Plural(gos.Count, "object")})" : ""));
            var l = s.Line().Muted("Game objects: ");
            if (c.Server) for (int i = 0; i < Math.Min(10, gos.Count); i++) { if (i > 0) l.Muted(", "); GoLink(l, gos[i]); }
            else for (int i = 0; i < Math.Min(10, gd.Count); i++) { if (i > 0) l.Muted(", "); l.Add($"display {gd[i]}", $"godisplay:{gd[i]}"); }
            int more = (c.Server ? gos.Count : gd.Count) - 10;
            if (more > 0) l.Muted($", and {more:N0} more");
        }
        var fx = Rows("SELECT id, name FROM spell_effect WHERE model=$0", r => (I(r, 0), S(r, 1) ?? ""), model);
        if (fx.Count > 0)
        {
            parts.Add(UsageNames.Plural(fx.Count, "spell effect"));
            var l = s.Line().Muted("Spell effects: ");
            for (int i = 0; i < Math.Min(10, fx.Count); i++) { if (i > 0) l.Muted(", "); l.Add(fx[i].Item2, $"spell:{fx[i].Item1}"); }
        }
        var races = Rows("SELECT r.id, r.name, CASE WHEN r.male_display=d.id THEN 0 ELSE 1 END FROM race r JOIN creature_display d ON d.id IN (r.male_display, r.female_display) WHERE d.model=$0",
            r => (I(r, 0), S(r, 1) ?? "", I(r, 2)), model);
        if (races.Count > 0)
        {
            parts.Add(UsageNames.Plural(races.Count, "player race"));
            var l = s.Line().Muted("Player characters: ");
            for (int i = 0; i < races.Count; i++) { if (i > 0) l.Muted(", "); l.Add($"{races[i].Item2} {(races[i].Item3 == 0 ? "Male" : "Female")}", $"race:{races[i].Item1}:{races[i].Item3}"); }
        }
        s.Summary = parts.Count == 0 ? "No display, spell effect or race uses this model file." : string.Join(", ", parts);
        if (!c.Server && cd.Count + idisp.Count + gd.Count > 0) rep.NeedsServer = true;
    }
}
