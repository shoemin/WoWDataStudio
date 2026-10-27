using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Versions;

namespace WoWData.Core.Index;

/// <summary>What a modern index is built from besides the client (all optional; the user's own files only).</summary>
public sealed class ModernIndexOptions
{
    /// <summary>A listfile the user chose (setting other-clients.listfile).</summary>
    public string? Listfile { get; init; }
    /// <summary>The file names of the user's 1.12 game, and a short fingerprint of them (for "is the index current").</summary>
    public IReadOnlyCollection<string>? Names112 { get; init; }
    public string Names112Fingerprint { get; init; } = "";
    /// <summary>A Classic Era storage whose name hashes name the 1.12 files.</summary>
    public CascStorage? Era { get; init; }
    /// <summary>At most this many textures and world models are Browse entries (the rest: "wds find files").</summary>
    public int MaxFileEntries { get; init; } = ModernIndexBuilder.DefaultMaxFileEntries;
}

/// <summary>
/// The index of another client version opened read only (M1 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): the
/// same SQLite schema as the 1.12 index (<see cref="IndexBuilder.Schema"/>, so the Browse list, search and details read
/// it the same way), produced from the DB2 tables (<see cref="Catalog160"/>) and the file names (<see cref="GameNames"/>),
/// plus tables the 1.12 builder never writes: <c>fdid</c> (FileDataID, name, name source, state), <c>locked</c>
/// (readable and encrypted records per table) and <c>named_creature</c> (the few creatures the client names itself;
/// the <c>creature</c> table stays empty so the creature displays stay listed). The 1.12 builder is not touched.
/// </summary>
public static class ModernIndexBuilder
{
    /// <summary>Bumped when what the builder writes changes (part of the fingerprint).</summary>
    public const int Version = 1;
    public const int DefaultMaxFileEntries = 200_000;

    public static readonly string[] StageNames =
    {
        "Reading the client's file names", "Reading creature displays", "Reading game object displays", "Reading item displays",
        "Reading the items and creatures the client names", "Reading spell effects", "Listing textures and world models", "Saving the index",
    };

    public const string ExtraSchema = """
        CREATE TABLE fdid(fdid INTEGER PRIMARY KEY, path TEXT COLLATE NOCASE, source TEXT, state TEXT);
        CREATE TABLE locked(tbl TEXT PRIMARY KEY, readable INT, locked INT, copies INT);
        CREATE TABLE named_creature(entry INTEGER PRIMARY KEY, name TEXT, title TEXT, display1 INT, display2 INT, display3 INT, display4 INT);
        CREATE INDEX fdid_path ON fdid(path COLLATE NOCASE);
        """;

    private static readonly Regex WmoGroup = new(@"_\d{3}\.wmo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What decides whether an existing index is current: the build, the listfile, the 1.12 names, the Era
    /// copy, the layout file and this builder's version.</summary>
    public static string Fingerprint(CascStorage storage, ModernIndexOptions opts)
    {
        var sb = new StringBuilder();
        sb.Append("modern ").Append(Version).Append('|').Append(storage.BuildConfigKey).Append('|').Append(storage.Product);
        if (!string.IsNullOrWhiteSpace(opts.Listfile) && File.Exists(opts.Listfile))
        {
            var fi = new FileInfo(opts.Listfile);
            sb.Append("|lf ").Append(Path.GetFullPath(opts.Listfile).ToUpperInvariant()).Append(' ').Append(fi.Length).Append(' ').Append(fi.LastWriteTimeUtc.Ticks);
        }
        sb.Append("|112 ").Append(opts.Names112Fingerprint);
        sb.Append("|era ").Append(opts.Era?.BuildConfigKey ?? "-");
        sb.Append("|layouts ").Append(LayoutFileHash(storage.Build));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    private static string LayoutFileHash(int build)
    {
        using var s = typeof(Db2Layouts).Assembly.GetManifestResourceStream(Db2Layouts.ResourcePrefix + build.ToString(CultureInfo.InvariantCulture) + ".txt");
        return s == null ? "none" : Convert.ToHexString(SHA256.HashData(s))[..16];
    }

    /// <summary>True when the index at <paramref name="path"/> was built by this builder for the same inputs.</summary>
    public static bool IsCurrent(string path, string fingerprint)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var ix = new AssetIndex(path);
            var m = ix.Meta();
            return m.GetValueOrDefault("kind") == "modern" && m.GetValueOrDefault("fingerprint") == fingerprint;
        }
        catch (SqliteException) { return false; }
    }

    private static void Report(IProgress<BuildProgress>? p, int n, double f, string detail) =>
        p?.Report(new BuildProgress(StageNames[n - 1], n, StageNames.Length, Math.Clamp(((n - 1) + f) / StageNames.Length, 0, 1), detail));

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private sealed class Ins : IDisposable
    {
        private readonly SqliteCommand _cmd;
        private readonly SqliteParameter[] _p;
        public int Rows { get; private set; }

        public Ins(SqliteConnection db, SqliteTransaction tx, string table, int n)
        {
            _cmd = db.CreateCommand();
            _cmd.Transaction = tx;
            _p = new SqliteParameter[n];
            for (int i = 0; i < n; i++)
            {
                _p[i] = _cmd.CreateParameter();
                _p[i].ParameterName = "$p" + i;
                _cmd.Parameters.Add(_p[i]);
            }
            _cmd.CommandText = $"INSERT INTO {table} VALUES ({string.Join(",", Enumerable.Range(0, n).Select(i => "$p" + i))})";
            _cmd.Prepare();
        }

        public void Add(params object?[] v)
        {
            for (int i = 0; i < _p.Length; i++) _p[i].Value = v[i] ?? DBNull.Value;
            _cmd.ExecuteNonQuery();
            Rows++;
        }

        public void Dispose() => _cmd.Dispose();
    }

    /// <summary>The file table of one build: FileDataID -> (name, source), what every display references, states.</summary>
    private sealed class Files
    {
        private readonly CascStorage _s;
        public readonly GameNames Names;
        public readonly Dictionary<uint, string> Referenced = new();
        private readonly Dictionary<uint, CascFileState> _states = new();

        public Files(CascStorage s, GameNames names)
        {
            _s = s;
            Names = names;
        }

        public CascFileState State(uint f)
        {
            if (!_states.TryGetValue(f, out var st)) _states[f] = st = _s.State(f);
            return st;
        }

        /// <summary>The name of a referenced file (its known name, else #FileDataID with the extension of its role).</summary>
        public string? Name(uint f, string ext)
        {
            if (f == 0 || !_s.Root.Contains(f)) return null;
            var n = Names.NameOf(f);
            if (n != null) { Referenced[f] = n; return n; }
            var guessed = ext.Length > 0 ? ext : GuessExt(f);
            n = $"#{f}{guessed}";
            Referenced[f] = n;
            return n;
        }

        /// <summary>The state word of a file, as the archive column of the index: null when it is not in the client.</summary>
        public string? Archive(uint f) => f == 0 || !_s.Root.Contains(f) ? null : ModernFileInfo.StateWord(State(f));

        private string GuessExt(uint f)
        {
            if (State(f) != CascFileState.Local) return "";
            try { return _s.ReadFileDataId(f) is { } d ? ClientFileKinds.GuessExtension(d) : ""; }
            catch (Exception e) when (e is CascMissingException or CascEncryptedException or InvalidDataException) { return ""; }
        }
    }

    private static string Stem(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var b = p[(p.LastIndexOf('\\') + 1)..];
        int dot = b.LastIndexOf('.');
        return dot > 0 ? b[..dot] : b;
    }

    private static string Ext(string p)
    {
        int dot = p.LastIndexOf('.');
        int bs = p.LastIndexOf('\\');
        return dot > bs ? p[dot..].ToLowerInvariant() : "";
    }

    /// <summary>Builds the index (written to a temporary file, then moved into place).</summary>
    public static async Task<IndexBuildResult> BuildAsync(CascStorage storage, Catalog160 cat, string path, ModernIndexOptions opts,
        IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        var res = new IndexBuildResult();
        await Task.Run(() => Build(storage, cat, path, opts, res, progress, ct), ct).ConfigureAwait(false);
        return res;
    }

    private static void Build(CascStorage storage, Catalog160 cat, string path, ModernIndexOptions opts, IndexBuildResult result,
        IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        // ---------------------------------------------------------------- 1. names
        Report(progress, 1, 0, opts.Listfile != null ? "your listfile, your 1.12 names, the client's interface list" : "your 1.12 names, the client's interface list");
        var names = GameNames.Build(storage, new GameNamesInput
        {
            Names112 = opts.Names112, Era = opts.Era, Listfile = opts.Listfile, Manifest = cat.InterfaceFiles().ToList(),
        }, ct);
        var files = new Files(storage, names);
        ct.ThrowIfCancellationRequested();

        using var db = IndexBuilder.Open(tmp);
        foreach (var stmt in (IndexBuilder.Schema + ExtraSchema).Split(';').Where(s => s.Trim().Length > 0)) Exec(db, stmt);
        using var tx = db.BeginTransaction();
        using var asset = new Ins(db, tx, "asset", 5);
        using var variant = new Ins(db, tx, "model_variant", 7);

        // ---------------------------------------------------------------- 2. creature displays
        Report(progress, 2, 0, "CreatureDisplayInfo, CreatureModelData, CreatureDisplayInfoExtra");
        using (var ins = new Ins(db, tx, "creature_display", 10))
        {
            var ids = cat.CreatureDisplayIds();
            int n = 0;
            foreach (var id in ids)
            {
                var d = cat.CreatureDisplay(id)!;
                var model = files.Name(d.Model, ".m2");
                var skins = d.Skins.Select(s => files.Name(s, ".blp")).ToArray();
                ins.Add(id, d.ModelId, model, (double)d.Scale, skins[0], skins[1], skins[2], d.ExtraId == 0 ? null : d.ExtraId,
                    d.ExtraId == 0 ? null : d.Race, d.ExtraId == 0 ? null : d.Sex);
                if (model != null) asset.Add("creature", id, "model", model, files.Archive(d.Model));
                for (int i = 0; i < 4; i++)
                {
                    if (skins[i] == null) continue;
                    asset.Add("creature", id, $"skin{i + 1}", skins[i], files.Archive(d.Skins[i]));
                    if (model != null && i < 3) variant.Add(model, "creature", id, 11 + i, $"skin{i + 1}", skins[i], files.Archive(d.Skins[i]));
                }
                if (files.Name(d.Bake, ".blp") is { } bake)
                {
                    asset.Add("creature", id, "baked skin", bake, files.Archive(d.Bake));
                    if (model != null) variant.Add(model, "creature", id, 1, "baked skin", bake, files.Archive(d.Bake));
                }
                if (files.Name(d.HdBake, ".blp") is { } hd) asset.Add("creature", id, "baked skin (high resolution)", hd, files.Archive(d.HdBake));
                if (++n % 1000 == 0) { Report(progress, 2, (double)n / ids.Count, $"{n:N0} of {ids.Count:N0}"); ct.ThrowIfCancellationRequested(); }
            }
            result.Counts["creature_display"] = ins.Rows;
        }

        // ---------------------------------------------------------------- 3. game object displays
        Report(progress, 3, 0, "GameObjectDisplayInfo");
        using (var ins = new Ins(db, tx, "gameobject_display", 2))
        {
            foreach (var id in cat.GameObjectDisplayIds())
            {
                var f = cat.GameObjectModel(id);
                var model = files.Name(f, "");
                ins.Add(id, model);
                if (model != null) asset.Add("gameobject", id, "model", model, files.Archive(f));
            }
            result.Counts["gameobject_display"] = ins.Rows;
        }
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 4. item displays
        Report(progress, 4, 0, "ItemDisplayInfo, ModelFileData, TextureFileData, ItemDisplayInfoMaterialRes");
        var races = new Dictionary<int, string>();
        if (cat.Table("ChrRaces") is { } chr)
        {
            int nameCol = chr.ColumnIndex("Name");
            foreach (var r in chr.Records) races.TryAdd(r.Id, r.String(nameCol) ?? $"race {r.Id}");
        }
        string Gender(int g) => g switch { 0 => "male", 1 => "female", _ => "" };
        using (var ins = new Ins(db, tx, "item_display", 6))
        {
            var ids = cat.ItemDisplayIds();
            int n = 0;
            foreach (var id in ids)
            {
                var d = cat.ItemDisplay(id)!;
                string? icon = files.Name(d.Icon, ".blp");
                var m1 = d.Models.FirstOrDefault(m => m.Slot == 1);
                var m2 = d.Models.FirstOrDefault(m => m.Slot == 2);
                var t1 = d.ModelTextures.FirstOrDefault(t => t.Slot == 1);
                var t2 = d.ModelTextures.FirstOrDefault(t => t.Slot == 2);
                ins.Add(id, icon, m1 == null ? null : files.Name(m1.File, ".m2"), m2 == null ? null : files.Name(m2.File, ".m2"),
                    t1.File == 0 ? null : files.Name(t1.File, ".blp"), t2.File == 0 ? null : files.Name(t2.File, ".blp"));
                var modelNames = new Dictionary<int, List<string>>();
                foreach (var m in d.Models)
                {
                    var mn = files.Name(m.File, ".m2");
                    if (mn == null) continue;
                    var who = m.Race > 0 ? $" ({races.GetValueOrDefault(m.Race, "race " + m.Race)}{(Gender(m.Gender) is { Length: > 0 } g ? " " + g : "")})" : "";
                    asset.Add("item", id, $"model{m.Slot}" + who, mn, files.Archive(m.File));
                    if (!modelNames.TryGetValue(m.Slot, out var l)) modelNames[m.Slot] = l = new List<string>();
                    l.Add(mn);
                }
                foreach (var (slot, f) in d.ModelTextures)
                {
                    var tn = files.Name(f, ".blp");
                    if (tn == null) continue;
                    asset.Add("item", id, $"model texture{slot}", tn, files.Archive(f));
                    if (modelNames.TryGetValue(slot, out var ms))
                        foreach (var mn in ms) variant.Add(mn, "item", id, 2, $"model texture{slot}", tn, files.Archive(f));
                }
                foreach (var b in d.BodyTextures)
                {
                    var tn = files.Name(b.File, ".blp");
                    if (tn == null) continue;
                    var region = b.Section >= 0 && b.Section < Catalog.Regions.Length ? Catalog.Regions[b.Section] : $"section {b.Section}";
                    var suffix = b.Gender switch { 0 => "_M", 1 => "_F", 3 => "_U", _ => "" };
                    asset.Add("item", id, $"{region} texture{suffix}", tn, files.Archive(b.File));
                }
                if (icon != null) asset.Add("item", id, "icon", icon, files.Archive(d.Icon));
                if (++n % 2000 == 0) { Report(progress, 4, (double)n / ids.Count, $"{n:N0} of {ids.Count:N0}"); ct.ThrowIfCancellationRequested(); }
            }
            result.Counts["item_display"] = ins.Rows;
        }

        // ---------------------------------------------------------------- 5. what the client names: items, creatures, game objects, maps
        Report(progress, 5, 0, "ItemSparse, Creature, GameObjects, Map");
        using (var ins = new Ins(db, tx, "item", 7))
        {
            foreach (var (id, name, disp, iconF) in cat.Items())
            {
                ins.Add(id, name, disp, 0, 0, 0, 0);
                if (iconF != 0) files.Name(iconF, ".blp");
            }
            result.Counts["item"] = ins.Rows;
        }
        using (var ins = new Ins(db, tx, "named_creature", 7))
        {
            foreach (var (id, name, title, ds) in cat.NamedCreatures())
                ins.Add(id, name, title, ds.ElementAtOrDefault(0), ds.ElementAtOrDefault(1), ds.ElementAtOrDefault(2), ds.ElementAtOrDefault(3));
            result.Counts["named_creature"] = ins.Rows;
        }
        using (var ins = new Ins(db, tx, "gameobject", 4))
        {
            foreach (var (id, name, disp) in cat.GameObjects()) ins.Add(id, name, 0, disp);
            result.Counts["gameobject"] = ins.Rows;
        }
        using (var ins = new Ins(db, tx, "map", 3))
            foreach (var (id, dir, name) in cat.Maps()) ins.Add(id, dir, name);
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 6. spell effects
        Report(progress, 6, 0, "SpellVisualEffectName, SpellXSpellVisual, SpellVisualEvent, SpellVisualKitModelAttach, SpellVisualMissile, SpellName");
        var effectIds = new HashSet<int>();
        using (var ins = new Ins(db, tx, "spell_effect", 4))
        {
            foreach (var (id, model, tex) in cat.SpellEffects())
            {
                var mn = files.Name(model, ".m2");
                var name = mn != null ? Stem(mn) : tex != 0 && files.Name(tex, ".blp") is { } tn ? Stem(tn) : $"effect {id}";
                ins.Add(id, name, mn, mn == null ? null : files.Archive(model));
                effectIds.Add(id);
            }
            result.Counts["spell_effect"] = ins.Rows;
        }
        using (var ins = new Ins(db, tx, "spell_effect_use", 6))
        {
            foreach (var (e, spell, sname, visual, role) in cat.SpellEffectUses())
                if (effectIds.Contains(e)) ins.Add(e, spell, sname, "", visual, role);
            result.Counts["spell_effect_use"] = ins.Rows;
        }
        ct.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- 7. files: textures and world models (named)
        Report(progress, 7, 0, $"{names.Count:N0} named files");
        var listed = new List<(uint Fdid, string Name, NameSource Source)>();
        foreach (var (f, n, src) in names.All)
        {
            var e = Ext(n);
            if (e == ".blp" || e == ".m2" || e == ".wmo") listed.Add((f, n, src));
        }
        int candidates = listed.Count;
        if (listed.Count > opts.MaxFileEntries)
        {
            // hard limit (design 3.2): files a display uses, then verified and 1.12 names, then the listfile by path
            listed = listed.OrderBy(x => files.Referenced.ContainsKey(x.Fdid) ? 0 : x.Source is NameSource.Client or NameSource.Manifest or NameSource.Era ? 1 : 2)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(opts.MaxFileEntries).ToList();
        }
        var listedSet = listed.Select(x => x.Fdid).ToHashSet();
        result.Counts["file_entries"] = listed.Count;
        result.Counts["file_entries_left_out"] = candidates - listed.Count;
        using (var ins = new Ins(db, tx, "files", 3))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (f, n, _) in listed)
                if (seen.Add(n)) ins.Add(n, files.Archive(f), Ext(n));
            foreach (var (f, n) in files.Referenced)
                if (!listedSet.Contains(f) && seen.Add(n)) ins.Add(n, files.Archive(f), Ext(n));
            result.Counts["files"] = ins.Rows;
        }
        using (var ins = new Ins(db, tx, "fdid", 4))
        {
            int n = 0;
            foreach (var (f, name, src) in names.All)
            {
                ins.Add(f, name, NameSources.Code(src), listedSet.Contains(f) || files.Referenced.ContainsKey(f) ? ModernFileInfo.StateWord(files.State(f)) : null);
                if (++n % 100000 == 0) { Report(progress, 7, 0.5 * n / Math.Max(1, names.Count), $"{n:N0} names"); ct.ThrowIfCancellationRequested(); }
            }
            foreach (var (f, name) in files.Referenced)
                if (names.NameOf(f) == null) ins.Add(f, name, NameSources.Code(NameSource.Number), ModernFileInfo.StateWord(files.State(f)));
            result.Counts["fdid"] = ins.Rows;
        }
        foreach (var (src, c) in names.Counts) result.Counts["names_" + NameSources.Code(src)] = c;
        result.Counts["names_unnamed_referenced"] = files.Referenced.Count(kv => names.NameOf(kv.Key) == null);
        result.Counts["asset"] = asset.Rows;
        result.Counts["model_variant"] = variant.Rows;
        using (var ins = new Ins(db, tx, "locked", 4))
            foreach (var l in cat.Locked()) ins.Add(l.Table, l.Readable, l.Locked, l.Copies);
        tx.Commit();

        // ---------------------------------------------------------------- 8. entries
        Report(progress, 8, 0.2, "");
        WriteEntries(db, listed, result);
        void Meta(string k, string v)
        {
            using var c = db.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO meta VALUES ($k, $v)";
            c.Parameters.AddWithValue("$k", k);
            c.Parameters.AddWithValue("$v", v);
            c.ExecuteNonQuery();
        }
        Meta("kind", "modern");
        Meta("modern_version", Version.ToString(CultureInfo.InvariantCulture));
        Meta("schema_version", IndexBuilder.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Meta("built", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Meta("server", "False");
        Meta("server_message", "another version is read only: names come from the client itself");
        Meta("fingerprint", Fingerprint(storage, opts));
        Meta("version", storage.Version);
        Meta("product", storage.Product);
        Meta("build_name", storage.BuildName);
        Meta("listfile", opts.Listfile ?? "");
        Meta("era", opts.Era != null ? $"{opts.Era.Product} {opts.Era.Version}" : "");
        Meta("names112", opts.Names112?.Count.ToString(CultureInfo.InvariantCulture) ?? "0");
        Meta("client_files", names.ClientFiles.ToString(CultureInfo.InvariantCulture));
        Meta("listfile_other_clients", names.ListfileOtherClients.ToString(CultureInfo.InvariantCulture));
        Meta("listfile_contradicted", names.ListfileContradicted.ToString(CultureInfo.InvariantCulture));
        Meta("problems", JsonSerializer.Serialize(cat.Problems));
        result.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        Meta("seconds", result.Seconds.ToString(CultureInfo.InvariantCulture));
        Meta("counts", JsonSerializer.Serialize(result.Counts));
        db.Close();
        SqliteConnection.ClearAllPools();
        // a just-written file can be held for a moment by another program (a virus scanner): try again before giving up
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
        Report(progress, StageNames.Length, 1, $"{result.Seconds} s");
    }

    private static SqliteDataReader Reader(SqliteConnection db, string sql)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        return c.ExecuteReader();
    }

    /// <summary>The Browse list: every creature display (and the creatures the client names), items and item displays,
    /// game objects and their displays, spell effects, named textures and world models.</summary>
    private static void WriteEntries(SqliteConnection db, List<(uint Fdid, string Name, NameSource Source)> listed, IndexBuildResult result)
    {
        using var tx = db.BeginTransaction();
        using var ins = new Ins(db, tx, "entry", 8);
        int count = 0;
        void Add(string kind, long id, string name, string info, string? path, string key, string links, string keywords)
        {
            ins.Add(kind, id, name, info, path, key, links, keywords);
            count++;
        }

        var cd = new Dictionary<long, (string? Model, string Skins)>();
        using (var r = Reader(db, "SELECT id, model, skin1, skin2, skin3 FROM creature_display ORDER BY id"))
            while (r.Read())
            {
                var model = r.IsDBNull(1) ? null : r.GetString(1);
                var skins = string.Join(", ", Enumerable.Range(2, 3).Where(i => !r.IsDBNull(i)).Select(i => Stem(r.GetString(i))));
                cd[r.GetInt64(0)] = (model, skins);
            }
        using (var r = Reader(db, "SELECT entry, name, title, display1, display2, display3, display4 FROM named_creature ORDER BY entry"))
            while (r.Read())
            {
                var ids = Enumerable.Range(3, 4).Select(i => r.IsDBNull(i) ? 0L : r.GetInt64(i)).Where(i => i > 0).Distinct().ToList();
                var first = ids.Count > 0 && cd.TryGetValue(ids[0], out var m) ? m : (null, "");
                var title = r.IsDBNull(2) ? "" : r.GetString(2);
                Add("creature", r.GetInt64(0), r.GetString(1), (title.Length > 0 ? $"<{title}> · " : "") + "named by the client · display " + string.Join(", ", ids),
                    first.Model, $"creature:{r.GetInt64(0)}", string.Join(" ", ids.Select(i => "creature:" + i)), first.Skins);
            }
        foreach (var (id, (model, skins)) in cd)
        {
            var name = Stem(model) is { Length: > 0 } s ? s : "(no model)";
            if (skins.Length > 0) name += " - " + skins;
            Add("creature", id, name, $"creature display {id}", model, $"cdisplay:{id}", $"creature:{id}", skins);
        }

        var idModel = new Dictionary<long, (string? Model, string? Icon, string? Texture)>();
        using (var r = Reader(db, "SELECT id, icon, model1, model_texture1 FROM item_display ORDER BY id"))
            while (r.Read())
                idModel[r.GetInt64(0)] = (r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(3) ? null : r.GetString(3));
        using (var r = Reader(db, "SELECT entry, name, display_id FROM item ORDER BY entry"))
            while (r.Read())
            {
                long d = r.GetInt64(2);
                idModel.TryGetValue(d, out var m);
                Add("item", r.GetInt64(0), r.GetString(1), d > 0 ? $"item display {d}" + (m.Model == null && m.Icon != null ? " · icon only" : "") : "no display",
                    m.Model ?? m.Icon, $"item:{r.GetInt64(0)}", d > 0 ? $"item:{d}" : "", Stem(m.Icon));
            }
        foreach (var (d, m) in idModel)
        {
            var name = m.Model != null ? Stem(m.Model) : m.Icon != null ? Stem(m.Icon) : $"item display {d}";
            if (m.Texture != null) name += " - " + Stem(m.Texture);
            Add("item", d, name, $"item display {d}", m.Model ?? m.Icon, $"idisplay:{d}", $"item:{d}", Stem(m.Icon));
        }

        var goModel = new Dictionary<long, string?>();
        using (var r = Reader(db, "SELECT id, model FROM gameobject_display ORDER BY id"))
            while (r.Read()) goModel[r.GetInt64(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        using (var r = Reader(db, "SELECT entry, name, display_id FROM gameobject ORDER BY entry"))
            while (r.Read())
            {
                long d = r.GetInt64(2);
                goModel.TryGetValue(d, out var model);
                var name = r.GetString(1);
                Add("gameobject", r.GetInt64(0), name.Length > 0 ? name : Stem(model) is { Length: > 0 } s ? s : $"game object {r.GetInt64(0)}",
                    $"placed by the client · display {d}", model, $"go:{r.GetInt64(0)}", $"gameobject:{d}", "");
            }
        foreach (var (d, model) in goModel)
            Add("gameobject", d, Stem(model) is { Length: > 0 } s ? s : $"display {d}", $"game object display {d}", model, $"godisplay:{d}", $"gameobject:{d}", "");

        var spellNames = new Dictionary<long, List<string>>();
        using (var r = Reader(db, "SELECT effect_id, spell_name FROM spell_effect_use GROUP BY effect_id, spell_name ORDER BY effect_id, spell_name"))
            while (r.Read())
            {
                if (!spellNames.TryGetValue(r.GetInt64(0), out var l)) spellNames[r.GetInt64(0)] = l = new List<string>();
                l.Add(r.GetString(1));
            }
        var spellModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = Reader(db, "SELECT id, name, model FROM spell_effect ORDER BY id"))
            while (r.Read())
            {
                long id = r.GetInt64(0);
                var model = r.IsDBNull(2) ? null : r.GetString(2);
                if (model != null) spellModels.Add(model);
                spellNames.TryGetValue(id, out var sn);
                Add("spelleffect", id, r.GetString(1), $"spell effect {id}" + (sn != null ? $" · used by {sn.Count} spell name{(sn.Count == 1 ? "" : "s")}" : ""),
                    model, $"spell:{id}", "", sn == null ? "" : string.Join(" | ", sn.Take(40)));
            }

        int textures = 0, world = 0;
        foreach (var (f, p, src) in listed.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Ext(p);
            var how = $"#{f} · {NameSources.Short(src)}";
            if (ext == ".blp")
            {
                Add("texture", 0, Stem(p), "texture · " + how, p, "file:" + p, "", "");
                textures++;
            }
            else if (p.StartsWith("World\\", StringComparison.OrdinalIgnoreCase) && !(ext == ".wmo" && WmoGroup.IsMatch(p)))
            {
                Add("worldobject", 0, Stem(p), $"{(ext == ".wmo" ? "building/structure (WMO)" : "model (M2)")} · {how}", p, "file:" + p, "", "");
                world++;
            }
            else if (ext == ".m2" && p.StartsWith("Spells\\", StringComparison.OrdinalIgnoreCase) && !spellModels.Contains(p))
                Add("spelleffect", 0, Stem(p), $"spell model (no SpellVisualEffectName row) · {how}", p, "file:" + p, "", "");
        }
        tx.Commit();
        result.Counts["textures"] = textures;
        result.Counts["world_objects"] = world;
        result.Counts["entry"] = count;
    }
}
