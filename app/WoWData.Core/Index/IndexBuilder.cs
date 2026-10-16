using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WoWData.Core.Dbc;
using WoWData.Core.M2;
using WoWData.Core.Mpq;
using WoWData.Core.Usage;

namespace WoWData.Core.Index;

/// <summary>Progress of a long operation: a stage name, its number, the overall fraction (0..1) and a detail line.</summary>
public sealed record BuildProgress(string Stage, int StageNumber, int StageCount, double Fraction, string Detail = "");

public sealed class IndexBuildResult
{
    public Dictionary<string, int> Counts { get; } = new();
    public bool ServerUsed { get; set; }
    public string ServerMessage { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>
/// Builds the SQLite "what uses what" index (port of wowdata/index.py, extended for the app):
/// the Python tables (files, creature/gameobject/item displays, asset, race, char_section, map, server templates and
/// spawns) plus
///  * model_variant: per variant, model -> owner (creature display / item display / character section) -> texture slot
///    -> texture, so a skin picker (Phase 3b) and the variant export (3c) can list every skin of a model;
///  * spell_effect / spell_effect_use: SpellVisualEffectName models and the spells that show them
///    (Spell.SpellVisual -> SpellVisual kits/missile -> SpellVisualKit effect slots);
///  * entry: one row per searchable thing (creature, character, item, game object, world object, spell effect,
///    texture), loaded into memory by the app for instant filtering.
/// </summary>
public static class IndexBuilder
{
    public const int SchemaVersion = 6;   // 4: the "Used in game" tables (Usage/UsageIndex.cs); 5: u_ground_texture (0.6.1); 6: spell effect roles from the SpellVisual layout (0.7.0)

    public const string Schema = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE files(path TEXT PRIMARY KEY COLLATE NOCASE, archive TEXT, ext TEXT);
        CREATE TABLE creature_display(id INTEGER PRIMARY KEY, model_id INT, model TEXT COLLATE NOCASE, scale REAL,
            skin1 TEXT, skin2 TEXT, skin3 TEXT, extra_id INT, race INT, sex INT);
        CREATE TABLE gameobject_display(id INTEGER PRIMARY KEY, model TEXT COLLATE NOCASE);
        CREATE TABLE item_display(id INTEGER PRIMARY KEY, icon TEXT, model1 TEXT, model2 TEXT, model_texture1 TEXT, model_texture2 TEXT);
        CREATE TABLE asset(kind TEXT, display_id INT, role TEXT, path TEXT COLLATE NOCASE, archive TEXT);
        CREATE TABLE model_variant(model TEXT COLLATE NOCASE, owner_kind TEXT, owner_id INT, slot INT, role TEXT,
            texture TEXT COLLATE NOCASE, archive TEXT);
        CREATE TABLE race(id INTEGER PRIMARY KEY, name TEXT, prefix TEXT, client_file TEXT, male_display INT, female_display INT);
        CREATE TABLE char_section(id INTEGER PRIMARY KEY, race INT, sex INT, section INT, variation INT, color INT,
            texture1 TEXT, texture2 TEXT, texture3 TEXT, flags INT);
        CREATE TABLE map(id INTEGER PRIMARY KEY, directory TEXT, name TEXT);
        CREATE TABLE spell_effect(id INTEGER PRIMARY KEY, name TEXT, model TEXT COLLATE NOCASE, archive TEXT);
        CREATE TABLE spell_effect_use(effect_id INT, spell_id INT, spell_name TEXT, spell_rank TEXT, visual_id INT, role TEXT);
        CREATE TABLE creature(entry INTEGER PRIMARY KEY, name TEXT, subname TEXT, level_min INT, level_max INT,
            display1 INT, display2 INT, display3 INT, display4 INT);
        CREATE TABLE creature_spawn(entry INT, map INT, count INT);
        CREATE TABLE gameobject(entry INTEGER PRIMARY KEY, name TEXT, type INT, display_id INT);
        CREATE TABLE gameobject_spawn(entry INT, map INT, count INT);
        CREATE TABLE item(entry INTEGER PRIMARY KEY, name TEXT, display_id INT, inventory_type INT, quality INT, class INT, subclass INT);
        CREATE TABLE entry(kind TEXT, ref_id INT, name TEXT, info TEXT, path TEXT, key TEXT, links TEXT, keywords TEXT);
        CREATE INDEX asset_path ON asset(path);
        CREATE INDEX asset_display ON asset(kind, display_id);
        CREATE INDEX variant_model ON model_variant(model);
        CREATE INDEX variant_texture ON model_variant(texture);
        CREATE INDEX variant_owner ON model_variant(owner_kind, owner_id);
        CREATE INDEX creature_name ON creature(name);
        CREATE INDEX creature_display_model ON creature_display(model);
        CREATE INDEX item_display_idx ON item(display_id);
        CREATE INDEX gameobject_display_idx ON gameobject(display_id);
        CREATE INDEX spawn_entry ON creature_spawn(entry);
        CREATE INDEX go_spawn_entry ON gameobject_spawn(entry);
        CREATE INDEX spell_use_effect ON spell_effect_use(effect_id);
        CREATE INDEX spell_effect_model ON spell_effect(model);
        CREATE INDEX char_section_race ON char_section(race, sex);
        """;

    private static readonly Regex WmoGroup = new(@"_\d{3}\.wmo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int StageCount = 12;

    /// <summary>(Re)build the index at <paramref name="path"/> (written to a temporary file, then moved into place).
    /// The server part is optional: when <paramref name="server"/> is null or unreachable, names come from files.</summary>
    public static async Task<IndexBuildResult> BuildAsync(ClientData client, string path, ServerDbSettings? server,
        IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new IndexBuildResult();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        // the server query runs in parallel with the file work
        Task<ServerData?> serverTask = Task.FromResult<ServerData?>(null);
        string serverMessage = "not used";
        if (server is { Enabled: true })
        {
            serverTask = Task.Run<ServerData?>(async () =>
            {
                try { return await new ServerDb(server).LoadAsync(ct).ConfigureAwait(false); }
                catch (ServerDbException e) { serverMessage = e.Message; return null; }
            }, ct);
        }
        else if (server != null) serverMessage = "turned off in Settings";
        // the "Used in game" server data (spawns, loot, vendors, quests...) loads in parallel too
        var usageTask = server is { Enabled: true } ? LoadUsageAsync(server, ct) : Task.FromResult<UsageServerData?>(null);

        await Task.Run(() => BuildFiles(client, tmp, result, progress, ct), ct).ConfigureAwait(false);

        Report(progress, "Loading names from the server database", 10, 0, server is { Enabled: true } ? server.Describe() : "not used");
        var sd = await serverTask.ConfigureAwait(false);
        var usd = sd == null ? null : await usageTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            using var db = Open(tmp);
            WriteServer(db, sd, result);
            if (sd != null) serverMessage = $"{sd.Creatures.Count:N0} creatures, {sd.GameObjects.Count:N0} game objects, {sd.Items.Count:N0} items ({sd.Schema} schema)";
            Report(progress, "Linking spawns, loot, vendors and quests", 11, 0, usd == null ? "needs the server database" : "");
            foreach (var (k, v) in UsageIndex.WriteServer(db, usd)) result.Counts[k] = v;
            Report(progress, "Saving the index", 12, 0.2, "");
            WriteEntries(db, result);
            WriteMeta(db, client, result, serverMessage, server);
        }, ct).ConfigureAwait(false);
        result.ServerMessage = serverMessage;
        SqliteConnection.ClearAllPools();
        File.Move(tmp, path, overwrite: true);
        result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        Report(progress, "Done", StageCount, 1, $"{result.Seconds} s");
        return result;
    }

    /// <summary>Replace only the server part of an existing index (after the database became reachable, or its
    /// settings changed). Returns the server message.</summary>
    public static async Task<(bool Ok, string Message)> RefreshServerAsync(string path, ServerDbSettings server, CancellationToken ct = default)
    {
        ServerData? sd;
        UsageServerData? usd = null;
        string msg;
        try
        {
            sd = await new ServerDb(server).LoadAsync(ct).ConfigureAwait(false);
            usd = await LoadUsageAsync(server, ct).ConfigureAwait(false);
            msg = $"{sd.Creatures.Count:N0} creatures, {sd.GameObjects.Count:N0} game objects, {sd.Items.Count:N0} items ({sd.Schema} schema)";
        }
        catch (ServerDbException e)
        {
            sd = null;
            msg = e.Message;
        }
        await Task.Run(() =>
        {
            using var db = Open(path);
            using (var tx = db.BeginTransaction())
            {
                foreach (var t in new[] { "creature", "creature_spawn", "gameobject", "gameobject_spawn", "item", "entry" })
                    Exec(db, $"DELETE FROM {t}");
                tx.Commit();
            }
            var res = new IndexBuildResult();
            WriteServer(db, sd, res);
            UsageIndex.WriteServer(db, sd == null ? null : usd);
            WriteEntries(db, res);
            SetMeta(db, "server", (sd != null).ToString());
            SetMeta(db, "server_message", msg);
            SetMeta(db, "server_db", server.Describe());
        }, ct).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        return (sd != null, msg);
    }

    /// <summary>The "Used in game" server data, or null (the names still load when only this part fails).</summary>
    private static async Task<UsageServerData?> LoadUsageAsync(ServerDbSettings server, CancellationToken ct)
    {
        try { return await Task.Run(() => UsageServerLoader.LoadAsync(server, ct), ct).ConfigureAwait(false); }
        catch (ServerDbException) { return null; }
    }

    private static void Report(IProgress<BuildProgress>? p, string stage, int n, double f, string detail) =>
        p?.Report(new BuildProgress(stage, n, StageCount, Math.Clamp(((n - 1) + f) / StageCount, 0, 1), detail));

    internal static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static void SetMeta(SqliteConnection db, string key, string value)
    {
        using var c = db.CreateCommand();
        c.CommandText = "INSERT OR REPLACE INTO meta VALUES ($k, $v)";
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$v", value);
        c.ExecuteNonQuery();
    }

    /// <summary>Prepared multi-row insert helper.</summary>
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
            var names = new string[columns];
            for (int i = 0; i < columns; i++)
            {
                names[i] = "$p" + i;
                _p[i] = _cmd.CreateParameter();
                _p[i].ParameterName = names[i];
                _cmd.Parameters.Add(_p[i]);
            }
            _cmd.CommandText = $"INSERT INTO {table} VALUES ({string.Join(",", names)})";
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

    private static object? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Ext(string p)
    {
        int dot = p.LastIndexOf('.');
        int bs = p.LastIndexOf('\\');
        return dot > bs ? p[dot..].ToLowerInvariant() : "";
    }

    private static void BuildFiles(ClientData client, string tmp, IndexBuildResult result, IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        using var db = Open(tmp);
        foreach (var stmt in Schema.Split(';').Where(s => s.Trim().Length > 0)) Exec(db, stmt);
        UsageIndex.CreateClientTables(db);
        var cat = new Catalog(client);
        using var tx = db.BeginTransaction();

        Report(progress, "Reading the archive file lists", 1, 0, $"{client.Archives.Count} archives");
        var names = cat.Names();
        using (var ins = new Inserter(db, tx, "files", 3))
            foreach (var f in names.Values) ins.Add(f.Path, f.Archive, Ext(f.Path));
        result.Counts["files"] = names.Count;
        result.Counts["deleted"] = client.DeletedNames().Count;
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- creatures
        Report(progress, "Reading creature displays", 2, 0, "CreatureDisplayInfo, CreatureModelData");
        using var asset = new Inserter(db, tx, "asset", 5);
        using var variant = new Inserter(db, tx, "model_variant", 7);
        int n = 0;
        using (var ins = new Inserter(db, tx, "creature_display", 10))
        {
            var all = cat.CreatureDisplays.Values.OrderBy(r => r.Id).ToList();
            foreach (var r in all)
            {
                var info = cat.CreatureDisplay(r.Id)!;
                ins.Add(r.Id, r.ModelId, info.Model, (double)r.Scale, Nz(r.Skin[0]), Nz(r.Skin[1]), Nz(r.Skin[2]),
                    r.ExtraId == 0 ? null : r.ExtraId, info.Extra?.Race, info.Extra?.Sex);
                foreach (var f in info.Files) asset.Add("creature", r.Id, f.Role, f.Path, f.Archive);
                if (info.Model != null)
                {
                    foreach (var f in info.Files)
                    {
                        int slot = f.Role switch { "skin1" => 11, "skin2" => 12, "skin3" => 13, "baked skin" => 1, _ => -1 };
                        if (slot > 0) variant.Add(info.Model, "creature", r.Id, slot, f.Role, f.Path, f.Archive);
                    }
                }
                if (++n % 500 == 0) { Report(progress, "Reading creature displays", 2, (double)n / all.Count, $"{n:N0} of {all.Count:N0}"); ct.ThrowIfCancellationRequested(); }
            }
            result.Counts["creature_display"] = ins.Rows;
        }

        // ---------------------------------------------------------------- game objects
        Report(progress, "Reading game object displays", 3, 0, "GameObjectDisplayInfo");
        using (var ins = new Inserter(db, tx, "gameobject_display", 2))
        {
            foreach (var r in cat.GameObjectDisplays.Values.OrderBy(r => r.Id))
            {
                var info = cat.GameObjectDisplay(r.Id)!.Value;
                ins.Add(r.Id, info.Model);
                foreach (var f in info.Files) asset.Add("gameobject", r.Id, f.Role, f.Path, f.Archive);
            }
            result.Counts["gameobject_display"] = ins.Rows;
        }
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- items
        Report(progress, "Reading item displays", 4, 0, "ItemDisplayInfo");
        using (var ins = new Inserter(db, tx, "item_display", 6))
        {
            var all = cat.ItemDisplays.Values.OrderBy(r => r.Id).ToList();
            n = 0;
            foreach (var r in all)
            {
                var files = cat.ItemDisplay(r.Id)!;
                ins.Add(r.Id, Nz(r.Icon), Nz(r.Model[0]), Nz(r.Model[1]), Nz(r.ModelTexture[0]), Nz(r.ModelTexture[1]));
                foreach (var f in files) asset.Add("item", r.Id, f.Role, f.Path, f.Archive);
                // variants: each item model with the model textures of its own slot (type 2 = object skin)
                foreach (var m in files.Where(f => f.Role.StartsWith("model") && !f.Role.StartsWith("model texture")))
                {
                    var tagNum = m.Role.Length > 5 && char.IsDigit(m.Role[5]) ? m.Role[5] : '1';
                    foreach (var t in files.Where(f => f.Role == "model texture" + tagNum))
                        variant.Add(m.Path, "item", r.Id, 2, t.Role, t.Path, t.Archive);
                }
                if (++n % 2000 == 0) { Report(progress, "Reading item displays", 4, (double)n / all.Count, $"{n:N0} of {all.Count:N0}"); ct.ThrowIfCancellationRequested(); }
            }
            result.Counts["item_display"] = ins.Rows;
        }

        // ---------------------------------------------------------------- characters
        Report(progress, "Reading character races and textures", 5, 0, "ChrRaces, CharSections");
        var races = DbcTables.ChrRaces(cat.Table("ChrRaces"));
        using (var ins = new Inserter(db, tx, "race", 6))
            foreach (var r in races) ins.Add(r.Id, r.Name, r.ClientPrefix, r.ClientFileString, r.MaleDisplayId, r.FemaleDisplayId);
        var sections = DbcTables.CharSections(cat.Table("CharSections"));
        var raceModel = new Dictionary<(int, int), string?>();
        foreach (var r in races)
        {
            raceModel[(r.Id, 0)] = cat.CreatureDisplay(r.MaleDisplayId)?.Model;
            raceModel[(r.Id, 1)] = cat.CreatureDisplay(r.FemaleDisplayId)?.Model;
        }
        using (var ins = new Inserter(db, tx, "char_section", 10))
            foreach (var r in sections)
            {
                ins.Add(r.Id, r.Race, r.Sex, r.Section, r.Variation, r.Color, Nz(r.Texture[0]), Nz(r.Texture[1]), Nz(r.Texture[2]), r.Flags);
                if (raceModel.TryGetValue((r.Race, r.Sex), out var model) && model != null)
                {
                    var files = cat.SectionFiles(r);
                    // replaceable slots of character models: 1 = body skin (skin, face, facial hair, underwear are composited into it), 6 = hair
                    int slot = r.Section == 3 ? 6 : 1;
                    foreach (var f in files) variant.Add(model, "character", r.Id, slot, f.Role, f.Path, f.Archive);
                }
            }
        result.Counts["race"] = races.Count;
        result.Counts["char_section"] = sections.Count;
        using (var ins = new Inserter(db, tx, "map", 3))
            foreach (var r in DbcTables.Map(cat.Table("Map"))) ins.Add(r.Id, r.Directory, r.Name);
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- spell effects
        Report(progress, "Reading spell effects", 6, 0, "SpellVisualEffectName, SpellVisual, SpellVisualKit, Spell");
        try
        {
            var effects = DbcTables.SpellVisualEffectName(cat.Table("SpellVisualEffectName")).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
            using (var ins = new Inserter(db, tx, "spell_effect", 4))
                foreach (var e in effects.Values.OrderBy(e => e.Id))
                {
                    var model = e.FileName.Length > 0 ? cat.Canonical(e.FileName) : null;
                    ins.Add(e.Id, e.Name, model, model == null ? null : cat.ArchiveOf(model));
                }
            var kits = DbcTables.SpellVisualKit(cat.Table("SpellVisualKit")).GroupBy(k => k.Id).ToDictionary(g => g.Key, g => g.Last());
            var visuals = DbcTables.SpellVisual(cat.Table("SpellVisual")).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.Last());
            var spells = DbcTables.Spell(cat.Table("Spell"));
            var kitRoles = SpellVisualRec.KitRoles;
            using var use = new Inserter(db, tx, "spell_effect_use", 6);
            foreach (var s in spells)
            {
                var seen = new HashSet<(int, string)>();
                foreach (var vid in s.Visuals)
                {
                    if (vid <= 0 || !visuals.TryGetValue(vid, out var v)) continue;
                    if (v.HasMissile && v.MissileModel > 0 && effects.ContainsKey(v.MissileModel) && seen.Add((v.MissileModel, "missile")))
                        use.Add(v.MissileModel, s.Id, s.Name, s.Rank, vid, "missile");
                    if (v.AreaModel > 0 && effects.ContainsKey(v.AreaModel) && seen.Add((v.AreaModel, "area")))
                        use.Add(v.AreaModel, s.Id, s.Name, s.Rank, vid, "area");
                    for (int k = 0; k < v.Kits.Length; k++)
                    {
                        if (v.Kits[k] <= 0 || !kits.TryGetValue(v.Kits[k], out var kit)) continue;
                        foreach (var eid in kit.Effects)
                            if (eid > 0 && effects.ContainsKey(eid) && seen.Add((eid, kitRoles[k])))
                                use.Add(eid, s.Id, s.Name, s.Rank, vid, kitRoles[k]);
                    }
                }
            }
            result.Counts["spell_effect"] = effects.Count;
            result.Counts["spell_effect_use"] = use.Rows;
        }
        catch (DbcException e)
        {
            result.Counts["spell_effect"] = 0;
            Report(progress, "Reading spell effects", 6, 1, "skipped: " + e.Message);
        }
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- world objects (model textures for details are read on demand)
        Report(progress, "Listing world objects and textures", 7, 0, "");
        int world = 0, textures = 0;
        foreach (var f in names.Values)
        {
            var e = Ext(f.Path);
            if (e == ".blp") textures++;
            else if ((e == ".m2" || (e == ".wmo" && !WmoGroup.IsMatch(f.Path))) && f.Path.StartsWith("World\\", StringComparison.OrdinalIgnoreCase)) world++;
        }
        result.Counts["textures"] = textures;
        result.Counts["world_objects"] = world;
        result.Counts["asset"] = asset.Rows;
        result.Counts["model_variant"] = variant.Rows;
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- "Used in game" (Usage/UsageIndex.cs)
        Report(progress, "Reading zones, factions and spells", 8, 0, "AreaTable, FactionTemplate, Spell, SkillLine, ItemSet, Lock");
        foreach (var (k, v) in UsageIndex.WriteClientTables(db, tx, cat)) result.Counts[k] = v;
        Report(progress, "Mapping world placements and zones", 9, 0, "map tiles, buildings and model textures");
        foreach (var (k, v) in UsageIndex.WriteWorld(db, tx, cat, (f, d) => Report(progress, "Mapping world placements and zones", 9, f, d), ct))
            result.Counts[k] = v;
        tx.Commit();
    }

    private static void WriteServer(SqliteConnection db, ServerData? sd, IndexBuildResult result)
    {
        result.ServerUsed = sd != null;
        if (sd == null) return;
        using var tx = db.BeginTransaction();
        using (var ins = new Inserter(db, tx, "creature", 9))
            foreach (var c in sd.Creatures.GroupBy(c => c.Entry).Select(g => g.Last()))
                ins.Add(c.Entry, c.Name, c.SubName, c.LevelMin, c.LevelMax, c.Displays[0], c.Displays[1], c.Displays[2], c.Displays[3]);
        using (var ins = new Inserter(db, tx, "creature_spawn", 3))
            foreach (var s in sd.CreatureSpawns) ins.Add(s.Entry, s.Map, s.Count);
        using (var ins = new Inserter(db, tx, "gameobject", 4))
            foreach (var g in sd.GameObjects.GroupBy(c => c.Entry).Select(g => g.Last())) ins.Add(g.Entry, g.Name, g.Type, g.DisplayId);
        using (var ins = new Inserter(db, tx, "gameobject_spawn", 3))
            foreach (var s in sd.GameObjectSpawns) ins.Add(s.Entry, s.Map, s.Count);
        using (var ins = new Inserter(db, tx, "item", 7))
            foreach (var i in sd.Items.GroupBy(c => c.Entry).Select(g => g.Last()))
                ins.Add(i.Entry, i.Name, i.DisplayId, i.InventoryType, i.Quality, i.Class, i.SubClass);
        tx.Commit();
        result.Counts["creature"] = sd.Creatures.Count;
        result.Counts["gameobject"] = sd.GameObjects.Count;
        result.Counts["item"] = sd.Items.Count;
    }

    private static string FileStem(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var b = p[(p.LastIndexOf('\\') + 1)..];
        int dot = b.LastIndexOf('.');
        return dot > 0 ? b[..dot] : b;
    }

    /// <summary>The flat list the app searches. Server rows when the server data is present, display rows otherwise.</summary>
    private static void WriteEntries(SqliteConnection db, IndexBuildResult result)
    {
        bool server = Scalar(db, "SELECT COUNT(*) FROM creature") > 0;
        using var tx = db.BeginTransaction();
        using var ins = new Inserter(db, tx, "entry", 8);
        int count = 0;
        void Add(string kind, long id, string name, string info, string? path, string key, string links, string keywords)
        {
            ins.Add(kind, id, name, info, path, key, links, keywords);
            count++;
        }

        var cdModel = new Dictionary<long, (string? Model, string Skins)>();
        using (var r = Reader(db, "SELECT id, model, skin1, skin2, skin3 FROM creature_display"))
            while (r.Read())
                cdModel[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetString(1),
                    string.Join(", ", Enumerable.Range(2, 3).Where(i => !r.IsDBNull(i)).Select(i => r.GetString(i))));

        if (server)
        {
            using var r = Reader(db, "SELECT entry, name, subname, level_min, level_max, display1, display2, display3, display4 FROM creature ORDER BY entry");
            while (r.Read())
            {
                var ids = Enumerable.Range(5, 4).Select(i => r.IsDBNull(i) ? 0L : r.GetInt64(i)).Where(i => i > 0).Distinct().ToList();
                var first = ids.Count > 0 && cdModel.TryGetValue(ids[0], out var m) ? m : (null, "");
                string lv = r.GetInt64(3) == r.GetInt64(4) ? $"level {r.GetInt64(3)}" : $"level {r.GetInt64(3)}-{r.GetInt64(4)}";
                var sub = r.IsDBNull(2) ? "" : r.GetString(2);
                var info = (sub.Length > 0 ? $"<{sub}> · " : "") + lv + (ids.Count > 0 ? " · display " + string.Join(", ", ids) : " · no display");
                Add("creature", r.GetInt64(0), r.GetString(1), info, first.Model, $"creature:{r.GetInt64(0)}",
                    string.Join(" ", ids.Select(i => "creature:" + i)), first.Skins);
            }
        }
        else
        {
            using var r = Reader(db, "SELECT id, model, skin1, skin2, skin3 FROM creature_display ORDER BY id");
            while (r.Read())
            {
                var model = r.IsDBNull(1) ? null : r.GetString(1);
                var skins = string.Join(", ", Enumerable.Range(2, 3).Where(i => !r.IsDBNull(i)).Select(i => r.GetString(i)));
                var name = FileStem(model) is { Length: > 0 } s ? s : "(no model)";
                if (skins.Length > 0) name += " - " + skins;
                Add("creature", r.GetInt64(0), name, $"creature display {r.GetInt64(0)}", model, $"cdisplay:{r.GetInt64(0)}", $"creature:{r.GetInt64(0)}", skins);
            }
        }

        using (var r = Reader(db, "SELECT id, name, prefix, male_display, female_display FROM race ORDER BY id"))
            while (r.Read())
                for (int sex = 0; sex < 2; sex++)
                {
                    long disp = r.GetInt64(3 + sex);
                    var model = cdModel.TryGetValue(disp, out var m) ? m.Model : null;
                    Add("character", r.GetInt64(0) * 10 + sex, $"{r.GetString(1)} {(sex == 0 ? "Male" : "Female")}", $"player race {r.GetInt64(0)} · display {disp}",
                        model, $"race:{r.GetInt64(0)}:{sex}", $"character:{r.GetInt64(0)}:{sex} creature:{disp}", sex == 0 ? "male man" : "female woman");
                }

        var idModel = new Dictionary<long, (string? Model, string Icon)>();
        using (var r = Reader(db, "SELECT display_id, path, role FROM asset WHERE kind='item' AND (role LIKE 'model%' OR role='icon') ORDER BY display_id, rowid"))
            while (r.Read())
            {
                long id = r.GetInt64(0);
                var role = r.GetString(2);
                idModel.TryGetValue(id, out var cur);
                if (role == "icon") cur.Icon = r.GetString(1);
                else if (!role.StartsWith("model texture") && cur.Model == null) cur.Model = r.GetString(1);
                idModel[id] = cur;
            }
        if (server)
        {
            using var r = Reader(db, "SELECT entry, name, display_id, quality FROM item ORDER BY entry");
            while (r.Read())
            {
                long d = r.GetInt64(2);
                idModel.TryGetValue(d, out var m);
                Add("item", r.GetInt64(0), r.GetString(1), $"item display {d}" + (m.Model == null && m.Icon != null ? " · icon only" : ""),
                    m.Model ?? m.Icon, $"item:{r.GetInt64(0)}", $"item:{d}", FileStem(m.Icon));
            }
        }
        else
        {
            using var r = Reader(db, "SELECT id, icon, model1, model_texture1 FROM item_display ORDER BY id");
            while (r.Read())
            {
                long d = r.GetInt64(0);
                idModel.TryGetValue(d, out var m);
                var name = !r.IsDBNull(2) ? FileStem(r.GetString(2)) : !r.IsDBNull(1) ? r.GetString(1) : $"item display {d}";
                if (!r.IsDBNull(3)) name += " - " + r.GetString(3);
                Add("item", d, name, $"item display {d}", m.Model ?? m.Icon, $"idisplay:{d}", $"item:{d}", r.IsDBNull(1) ? "" : r.GetString(1));
            }
        }

        var goModel = new Dictionary<long, string?>();
        using (var r = Reader(db, "SELECT id, model FROM gameobject_display"))
            while (r.Read()) goModel[r.GetInt64(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        if (server)
        {
            using var r = Reader(db, "SELECT entry, name, type, display_id FROM gameobject ORDER BY entry");
            while (r.Read())
            {
                long d = r.GetInt64(3);
                goModel.TryGetValue(d, out var model);
                Add("gameobject", r.GetInt64(0), r.GetString(1), $"type {r.GetInt64(2)} · display {d}", model, $"go:{r.GetInt64(0)}", $"gameobject:{d}", "");
            }
        }
        else
        {
            foreach (var kv in goModel.OrderBy(k => k.Key))
                Add("gameobject", kv.Key, FileStem(kv.Value) is { Length: > 0 } s ? s : $"display {kv.Key}", $"game object display {kv.Key}", kv.Value,
                    $"godisplay:{kv.Key}", $"gameobject:{kv.Key}", "");
        }

        var spellNames = new Dictionary<long, List<string>>();
        using (var r = Reader(db, "SELECT effect_id, spell_name FROM spell_effect_use GROUP BY effect_id, spell_name ORDER BY effect_id, spell_name"))
            while (r.Read())
            {
                if (!spellNames.TryGetValue(r.GetInt64(0), out var l)) spellNames[r.GetInt64(0)] = l = new List<string>();
                l.Add(r.GetString(1));
            }
        var spellModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = Reader(db, "SELECT id, name, model, archive FROM spell_effect ORDER BY id"))
            while (r.Read())
            {
                long id = r.GetInt64(0);
                var model = r.IsDBNull(2) ? null : r.GetString(2);
                if (model != null) spellModels.Add(model);
                spellNames.TryGetValue(id, out var sn);
                var info = $"spell effect {id}" + (sn != null ? $" · used by {sn.Count} spell name{(sn.Count == 1 ? "" : "s")}" : "") + (r.IsDBNull(3) && model != null ? " · file missing" : "");
                Add("spelleffect", id, r.GetString(1), info, model, $"spell:{id}", "", sn == null ? "" : string.Join(" | ", sn.Take(40)));
            }

        // ground textures (0.6.1): the MTEX names of every map tile; their "_s" files are the specular variants
        var ground = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var gr = Reader(db, "SELECT DISTINCT texture FROM u_ground_texture");
            while (gr.Read()) ground.Add(Mpq.ClientData.ResolveName(gr.GetString(0)));
        }
        catch (SqliteException) { }
        using (var r = Reader(db, "SELECT path, archive, ext FROM files WHERE ext IN ('.m2', '.wmo', '.blp') ORDER BY path"))
            while (r.Read())
            {
                var p = r.GetString(0);
                var ext = r.GetString(2);
                if (ext == ".blp")
                {
                    string info = "texture", kw = "";
                    var baseOfS = Maps.GroundTextures.BaseNameOfSpecular(p);
                    if (ground.Contains(p))
                    {
                        info = $"ground texture (with terrain specular on the game draws {System.IO.Path.GetFileName(Maps.GroundTextures.SpecularName(p))} instead)";
                        kw = "ground terrain";
                    }
                    else if (baseOfS != null && ground.Contains(baseOfS))
                    {
                        info = $"ground texture, specular variant of {System.IO.Path.GetFileName(baseOfS)} (drawn when terrain specular is on)";
                        kw = "ground terrain specular";
                    }
                    Add("texture", 0, FileStem(p), $"{info} · {r.GetString(1)}", p, "file:" + p, "", kw);
                }
                else if (p.StartsWith("World\\", StringComparison.OrdinalIgnoreCase) && !(ext == ".wmo" && WmoGroup.IsMatch(p)))
                    Add("worldobject", 0, FileStem(p), $"{(ext == ".wmo" ? "building/structure (WMO)" : "model (M2)")} · {r.GetString(1)}", p, "file:" + p, "", "");
                else if (ext == ".m2" && p.StartsWith("Spells\\", StringComparison.OrdinalIgnoreCase) && !spellModels.Contains(p))
                    Add("spelleffect", 0, FileStem(p), $"spell model (no SpellVisualEffectName row) · {r.GetString(1)}", p, "file:" + p, "", "");
            }
        tx.Commit();
        result.Counts["entry"] = count;
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar() ?? 0L);
    }

    private static SqliteDataReader Reader(SqliteConnection db, string sql)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteReader(System.Data.CommandBehavior.Default);
    }

    private static void WriteMeta(SqliteConnection db, ClientData client, IndexBuildResult result, string serverMessage, ServerDbSettings? server)
    {
        SetMeta(db, "schema_version", SchemaVersion.ToString());
        SetMeta(db, "built", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        SetMeta(db, "server", result.ServerUsed.ToString());
        SetMeta(db, "server_message", serverMessage);
        SetMeta(db, "server_db", server?.Describe() ?? "");
        SetMeta(db, "fingerprint", client.Fingerprint());
        SetMeta(db, "archives", JsonSerializer.Serialize(client.Archives.Select(a => a.Name)));
        SetMeta(db, "counts", JsonSerializer.Serialize(result.Counts));
    }
}
