using Microsoft.Data.Sqlite;
using WoWData.Core.M2;
using WoWData.Core.Mpq;

namespace WoWData.Core.Index;

public enum AssetKind { Creature, Character, Item, GameObject, WorldObject, SpellEffect, Texture }

/// <summary>One searchable row (a creature, item, texture...). <see cref="Key"/> says what it is:
/// creature:&lt;entry&gt;, cdisplay:&lt;id&gt;, race:&lt;race&gt;:&lt;sex&gt;, item:&lt;entry&gt;, idisplay:&lt;id&gt;, go:&lt;entry&gt;,
/// godisplay:&lt;id&gt;, spell:&lt;effect id&gt;, file:&lt;archive path&gt;.</summary>
public sealed class IndexEntry
{
    public AssetKind Kind { get; init; }
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Info { get; init; } = "";
    public string? Path { get; init; }
    public string Key { get; init; } = "";
    /// <summary>Space-separated owner tokens (creature:503, item:22291, character:2:0...) used to mark entries whose
    /// textures the user changed.</summary>
    public string Links { get; init; } = "";
    public string Keywords { get; init; } = "";
    /// <summary>Lower-case text searched by the app (name, info, path, keywords, id).</summary>
    public string SearchText { get; init; } = "";

    public string KindLabel => KindName(Kind);

    public static string KindName(AssetKind k) => k switch
    {
        AssetKind.Creature => "Creature",
        AssetKind.Character => "Character",
        AssetKind.Item => "Item",
        AssetKind.GameObject => "Game object",
        AssetKind.WorldObject => "World object",
        AssetKind.SpellEffect => "Spell effect",
        _ => "Texture",
    };

    public bool IsTexture => Kind == AssetKind.Texture;
    public bool IsModelFile => Path != null && (Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase));
}

public sealed record TextureRef(string Path, string Role, string? Archive, string Note = "")
{
    public bool Exists => Archive != null;
}

/// <summary>A texture variant of a model: who uses it (creature display 503, item display 22291, character section
/// 12) and which texture fills which replaceable slot. Phase 3b's skin picker and 3c's variant export read these.</summary>
public sealed record ModelVariant(string Model, string OwnerKind, long OwnerId, int Slot, string Role, string Texture, string? Archive);

public sealed class EntryDetails
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>The source chain, e.g. "Mottled Boar (creature 3098)", "display 503", "Creature\Boar\Boar.m2", "skin: BoarSkinIvory".</summary>
    public List<string> Chain { get; } = new();
    public List<string> Models { get; } = new();
    public List<TextureRef> Textures { get; } = new();
    public List<string> Facts { get; } = new();
    public List<string> UsedBy { get; } = new();
}

/// <summary>Queries over a built index (read-only).</summary>
public sealed class AssetIndex : IDisposable
{
    private readonly SqliteConnection _db;
    public string Path { get; }

    public AssetIndex(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("index not built", path);
        Path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _db.Open();
    }

    public void Dispose() => _db.Dispose();

    private SqliteCommand Cmd(string sql, params object?[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        for (int i = 0; i < args.Length; i++) c.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return c;
    }

    /// <summary>Runs a query. One connection serves the details panel and the 3D viewer, which query from background
    /// threads at the same time: access is serialized (SQLite connections are not thread-safe).</summary>
    private List<T> Rows<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        lock (_db)
        {
            using var c = Cmd(sql, args);
            using var r = c.ExecuteReader();
            var l = new List<T>();
            while (r.Read()) l.Add(map(r));
            return l;
        }
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long L(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    public Dictionary<string, string> Meta() => Rows("SELECT key, value FROM meta", r => (r.GetString(0), S(r, 1) ?? "")).ToDictionary(t => t.Item1, t => t.Item2);

    public bool HasServer => Meta().TryGetValue("server", out var s) && s == "True";

    /// <summary>True when the index was built by this schema version for the same archives (names, sizes, times).</summary>
    public static bool IsCurrent(string path, ClientData client)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var ix = new AssetIndex(path);
            var m = ix.Meta();
            return m.GetValueOrDefault("schema_version") == IndexBuilder.SchemaVersion.ToString() && m.GetValueOrDefault("fingerprint") == client.Fingerprint();
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static AssetKind ParseKind(string k) => k switch
    {
        "creature" => AssetKind.Creature,
        "character" => AssetKind.Character,
        "item" => AssetKind.Item,
        "gameobject" => AssetKind.GameObject,
        "worldobject" => AssetKind.WorldObject,
        "spelleffect" => AssetKind.SpellEffect,
        _ => AssetKind.Texture,
    };

    public List<IndexEntry> LoadEntries() => Rows("SELECT kind, ref_id, name, info, path, key, links, keywords FROM entry ORDER BY rowid", r =>
    {
        var kind = ParseKind(r.GetString(0));
        var id = L(r, 1);
        var name = S(r, 2) ?? "";
        var info = S(r, 3) ?? "";
        var path = S(r, 4);
        var kw = S(r, 7) ?? "";
        return new IndexEntry
        {
            Kind = kind, Id = id, Name = name, Info = info, Path = path, Key = r.GetString(5), Links = S(r, 6) ?? "", Keywords = kw,
            SearchText = $"{name} {path} {kw} {info} {(id > 0 ? id.ToString() : "")} {IndexEntry.KindName(kind)}".ToLowerInvariant(),
        };
    });

    // ------------------------------------------------------------------ lookup helpers (also used by tests)

    public List<AssetFile> Assets(string kind, long displayId) =>
        Rows("SELECT role, path, archive FROM asset WHERE kind=$0 AND display_id=$1 ORDER BY rowid", r => new AssetFile(r.GetString(0), r.GetString(1), S(r, 2)), kind, displayId);

    public (long Id, string? Model, string[] Skins)? CreatureDisplay(long id)
    {
        var l = Rows("SELECT id, model, skin1, skin2, skin3 FROM creature_display WHERE id=$0",
            r => (r.GetInt64(0), S(r, 1), new[] { S(r, 2), S(r, 3), S(r, 4) }.Where(s => s != null).Cast<string>().ToArray()), id);
        return l.Count == 0 ? null : l[0];
    }

    public List<(long Entry, string Name, long[] Displays)> FindCreatures(string text)
    {
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cond = string.Join(" AND ", words.Select((w, i) => $"lower(name) LIKE $" + i));
        return Rows($"SELECT entry, name, display1, display2, display3, display4 FROM creature WHERE {cond} ORDER BY level_min, entry",
            r => (r.GetInt64(0), r.GetString(1), new[] { L(r, 2), L(r, 3), L(r, 4), L(r, 5) }.Where(d => d > 0).Distinct().ToArray()),
            words.Select(w => (object?)("%" + w + "%")).ToArray());
    }

    /// <summary>Every texture variant of a model (all displays / sections / item displays using it).</summary>
    public List<ModelVariant> ModelVariants(string model) =>
        Rows("SELECT model, owner_kind, owner_id, slot, role, texture, archive FROM model_variant WHERE model=$0 ORDER BY owner_kind, owner_id, slot",
            r => new ModelVariant(r.GetString(0), r.GetString(1), r.GetInt64(2), (int)r.GetInt64(3), r.GetString(4), r.GetString(5), S(r, 6)), model);

    /// <summary>Distinct creature skin sets of a model: (skins, display ids).</summary>
    public List<(string[] Skins, List<long> Displays)> CreatureSkinSets(string model)
    {
        var rows = Rows("SELECT id, skin1, skin2, skin3 FROM creature_display WHERE model=$0 ORDER BY id",
            r => (r.GetInt64(0), string.Join("|", new[] { S(r, 1), S(r, 2), S(r, 3) }.Where(s => s != null))), model);
        return rows.GroupBy(r => r.Item2).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key.Length == 0 ? Array.Empty<string>() : g.Key.Split('|'), g.Select(x => x.Item1).ToList())).ToList();
    }

    /// <summary>Who uses a texture: creature/item/game object displays, model variants, character sections, by path.</summary>
    public List<string> TextureUsers(string path, int limit = 200)
    {
        var users = new List<string>();
        foreach (var (kind, id, role) in Rows("SELECT kind, display_id, role FROM asset WHERE path=$0 ORDER BY kind, display_id LIMIT $1",
                     r => (r.GetString(0), r.GetInt64(1), r.GetString(2)), path, limit))
        {
            string name = kind switch
            {
                "creature" => CreatureNamesForDisplay(id) is { Count: > 0 } n ? $"creature display {id} ({string.Join(", ", n.Take(3))}{(n.Count > 3 ? ", ..." : "")})" : $"creature display {id}",
                "item" => ItemNamesForDisplay(id) is { Count: > 0 } n2 ? $"item display {id} ({string.Join(", ", n2.Take(3))}{(n2.Count > 3 ? ", ..." : "")})" : $"item display {id}",
                _ => $"game object display {id}",
            };
            users.Add($"{name}: {role}");
        }
        foreach (var (race, sex, section, v, c, rname) in Rows(
                     "SELECT s.race, s.sex, s.section, s.variation, s.color, r.name FROM char_section s LEFT JOIN race r ON r.id=s.race WHERE texture1=$0 OR texture2=$0 OR texture3=$0 LIMIT $1",
                     r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), S(r, 5) ?? "race " + r.GetInt64(0)), path, limit))
            users.Add($"character {rname} {(sex == 0 ? "male" : "female")}: {Catalog.Sections.GetValueOrDefault((int)section, section.ToString())} v{v} c{c}");
        foreach (var (id, name) in Rows("SELECT id, name FROM spell_effect WHERE model=$0", r => (r.GetInt64(0), r.GetString(1)), path))
            users.Add($"spell effect {id} {name}");
        return users;
    }

    /// <summary>The model files that use a texture (through displays, variants or as an embedded texture of a display's model).</summary>
    public List<string> ModelsUsingTexture(string path)
    {
        var models = Rows("SELECT DISTINCT model FROM model_variant WHERE texture=$0", r => r.GetString(0), path);
        models.AddRange(Rows("SELECT DISTINCT a2.path FROM asset a JOIN asset a2 ON a2.kind=a.kind AND a2.display_id=a.display_id AND (a2.role='model' OR a2.role LIKE 'model_' OR a2.role LIKE 'model_ (race/sex)') WHERE a.path=$0 AND a.role='model texture'",
            r => r.GetString(0), path));
        return models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every model or building that draws a texture: the displays' models plus the files that name it inside
    /// (u_model_texture). For the blend-mode advice.</summary>
    public List<string> ModelsDrawingTexture(string path)
    {
        var models = ModelsUsingTexture(path);
        try { models.AddRange(Rows("SELECT DISTINCT model FROM u_model_texture WHERE texture=$0", r => r.GetString(0), path)); }
        catch (SqliteException) { }
        return models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when a map tile names this file as a ground texture (MTEX).</summary>
    public bool IsGroundTexture(string path)
    {
        try { return Rows("SELECT 1 FROM u_ground_texture WHERE texture=$0 LIMIT 1", r => 1, path).Count > 0; }
        catch (SqliteException) { return false; }
    }

    /// <summary>The map tiles that name a ground texture: (map id, map name, tile count).</summary>
    public List<(long Map, string? Name, long Tiles)> GroundTextureMaps(string path)
    {
        try
        {
            return Rows("SELECT g.map, m.name, COUNT(*) FROM u_ground_texture g LEFT JOIN map m ON m.id = g.map WHERE g.texture=$0 GROUP BY g.map ORDER BY COUNT(*) DESC",
                r => (r.GetInt64(0), S(r, 1), r.GetInt64(2)), path);
        }
        catch (SqliteException) { return new(); }
    }

    /// <summary>The ground texture pair (base + "_s") of a texture, or null when no map uses it (or its base) as ground.</summary>
    public Maps.GroundTexturePair? GroundPair(string path) =>
        Maps.GroundTextures.PairOf(path, p => ArchiveOf(p) != null, IsGroundTexture);

    /// <summary>Owner tokens (creature:503, item:22291, character:2:0, gameobject:3813) of displays that use any of the paths.</summary>
    public HashSet<string> OwnersOfTextures(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            foreach (var (k, id) in Rows("SELECT DISTINCT kind, display_id FROM asset WHERE path=$0", r => (r.GetString(0), r.GetInt64(1)), p))
                set.Add($"{k}:{id}");
            foreach (var (race, sex) in Rows("SELECT DISTINCT race, sex FROM char_section WHERE texture1=$0 OR texture2=$0 OR texture3=$0", r => (r.GetInt64(0), r.GetInt64(1)), p))
                set.Add($"character:{race}:{sex}");
        }
        return set;
    }

    /// <summary>0.8.0: the Browse keys of what is in a zone (by name, zone or subzone, case-insensitive): the creatures and
    /// game objects spawned there (server data) and the world objects placed there (map files). Empty when the index
    /// has no such zone.</summary>
    public HashSet<string> KeysInZone(string zone)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<long> areas;
        try
        {
            areas = Rows("SELECT a.id FROM u_area a LEFT JOIN u_area p ON p.id = a.parent WHERE a.name = $0 COLLATE NOCASE OR p.name = $0 COLLATE NOCASE",
                r => r.GetInt64(0), zone);
        }
        catch (SqliteException) { return keys; }
        if (areas.Count == 0) return keys;
        var list = string.Join(",", areas);
        try
        {
            foreach (var (kind, entry) in Rows($"SELECT DISTINCT kind, entry FROM u_spawn WHERE area IN ({list})", r => (r.GetString(0), r.GetInt64(1))))
                keys.Add(kind == "gameobject" ? $"go:{entry}" : $"creature:{entry}");
        }
        catch (SqliteException) { }   // no server data in this index
        try
        {
            foreach (var m in Rows($"SELECT DISTINCT model FROM u_place WHERE area IN ({list})", r => r.GetString(0)))
                keys.Add("file:" + m);
        }
        catch (SqliteException) { }
        return keys;
    }

    public List<string> CreatureNamesForDisplay(long id) =>
        Rows("SELECT name FROM creature WHERE display1=$0 OR display2=$0 OR display3=$0 OR display4=$0 ORDER BY entry LIMIT 10", r => r.GetString(0), id);

    public List<string> ItemNamesForDisplay(long id) => Rows("SELECT name FROM item WHERE display_id=$0 ORDER BY entry LIMIT 10", r => r.GetString(0), id);

    // ------------------------------------------------------------------ 3D viewer queries

    /// <summary>The display ids of a creature entry (server database), in template order.</summary>
    public List<long> CreatureDisplayIds(long entry) =>
        Rows("SELECT display1, display2, display3, display4 FROM creature WHERE entry=$0", r => new[] { L(r, 0), L(r, 1), L(r, 2), L(r, 3) }, entry)
            .SelectMany(x => x).Where(x => x > 0).Distinct().ToList();

    public long? ItemDisplayOf(long entry) => Rows("SELECT display_id FROM item WHERE entry=$0", r => (long?)L(r, 0), entry).FirstOrDefault();

    public string? GameObjectModel(long displayId) => Rows("SELECT model FROM gameobject_display WHERE id=$0", r => S(r, 0), displayId).FirstOrDefault();

    public long? GameObjectDisplayOf(long entry) => Rows("SELECT display_id FROM gameobject WHERE entry=$0", r => (long?)L(r, 0), entry).FirstOrDefault();

    public string? SpellEffectModel(long id) => Rows("SELECT model FROM spell_effect WHERE id=$0", r => S(r, 0), id).FirstOrDefault();

    /// <summary>Every creature display using a model: (display id, skins, CreatureDisplayInfoExtra id or 0), by id.</summary>
    public List<(long Id, string[] Skins, long ExtraId)> CreatureDisplaysUsingModel(string model) =>
        Rows("SELECT id, skin1, skin2, skin3, extra_id FROM creature_display WHERE model=$0 ORDER BY id",
            r => (r.GetInt64(0), new[] { S(r, 1) ?? "", S(r, 2) ?? "", S(r, 3) ?? "" }, L(r, 4)), model);

    /// <summary>Every item display using a model with its model texture: (item display id, texture path).</summary>
    public List<(long Id, string Texture)> ItemDisplaysUsingModel(string model) =>
        Rows("SELECT owner_id, texture FROM model_variant WHERE model=$0 AND owner_kind='item' ORDER BY owner_id", r => (r.GetInt64(0), r.GetString(1)), model);

    /// <summary>The first creature entry name of each display (one query for many displays).</summary>
    public Dictionary<long, string> CreatureNamesForDisplays(IEnumerable<long> displays)
    {
        var want = displays.ToHashSet();
        var res = new Dictionary<long, string>();
        if (want.Count == 0) return res;
        foreach (var (entry, name, ds) in Rows("SELECT entry, name, display1, display2, display3, display4 FROM creature ORDER BY entry",
                     r => (r.GetInt64(0), r.GetString(1), new[] { L(r, 2), L(r, 3), L(r, 4), L(r, 5) })))
            foreach (var d in ds)
                if (d > 0 && want.Contains(d) && !res.ContainsKey(d)) res[d] = name;
        return res;
    }

    /// <summary>The first item name of each item display.</summary>
    public Dictionary<long, string> ItemNamesForDisplays(IEnumerable<long> displays)
    {
        var want = displays.ToHashSet();
        var res = new Dictionary<long, string>();
        if (want.Count == 0) return res;
        foreach (var (name, d) in Rows("SELECT name, display_id FROM item ORDER BY entry", r => (r.GetString(0), L(r, 1))))
            if (want.Contains(d) && !res.ContainsKey(d)) res[d] = name;
        return res;
    }

    private List<(long Map, string? Name, long Count)> Spawns(string table, long entry) =>
        Rows($"SELECT s.map, m.name, s.count FROM {table} s LEFT JOIN map m ON m.id = s.map WHERE s.entry=$0 ORDER BY s.count DESC", r => (r.GetInt64(0), S(r, 1), r.GetInt64(2)), entry);

    // ------------------------------------------------------------------ details

    /// <summary>The details panel content for an entry. <paramref name="catalog"/> (optional) reads the textures
    /// named inside models that the index does not list (world objects, spell effects).</summary>
    public EntryDetails Details(IndexEntry e, Catalog? catalog)
    {
        var d = new EntryDetails { Title = e.Name, Subtitle = $"{e.KindLabel}" + (e.Id > 0 && e.Kind != AssetKind.Character ? $" {e.Id}" : "") };
        var parts = e.Key.Split(':', 2);
        var kind = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";
        long.TryParse(arg, out var id);
        switch (kind)
        {
            case "creature":
            {
                var c = Rows("SELECT name, subname, level_min, level_max, display1, display2, display3, display4 FROM creature WHERE entry=$0",
                    r => (r.GetString(0), S(r, 1), r.GetInt64(2), r.GetInt64(3), new[] { L(r, 4), L(r, 5), L(r, 6), L(r, 7) }.Where(x => x > 0).Distinct().ToList()), id).FirstOrDefault();
                if (c.Item1 == null) break;
                d.Title = c.Item1;
                d.Subtitle = $"Creature {id}" + (string.IsNullOrEmpty(c.Item2) ? "" : $" <{c.Item2}>") + (c.Item3 == c.Item4 ? $" · level {c.Item3}" : $" · level {c.Item3}-{c.Item4}");
                bool first = true;
                foreach (var disp in c.Item5)
                {
                    AddCreatureDisplay(d, disp, first ? $"{c.Item1} (creature {id})" : null, catalog);
                    first = false;
                }
                if (c.Item5.Count > 1) d.Facts.Add($"This creature picks one of {c.Item5.Count} displays at random: {string.Join(", ", c.Item5)}.");
                var sp = Spawns("creature_spawn", id);
                d.Facts.Add(sp.Count == 0 ? "Not spawned in the world (server database)." :
                    "Spawned: " + string.Join(", ", sp.Take(5).Select(s => $"{s.Count} on {s.Name ?? "map " + s.Map}")));
                break;
            }
            case "cdisplay":
                AddCreatureDisplay(d, id, null, catalog);
                d.Facts.Add("Names from the server database are not available, so this is listed by its display id.");
                break;
            case "race":
            {
                var rs = arg.Split(':');
                long race = long.Parse(rs[0]), sex = long.Parse(rs[1]);
                var r0 = Rows("SELECT name, male_display, female_display FROM race WHERE id=$0", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), race).First();
                long disp = sex == 0 ? r0.Item2 : r0.Item3;
                var cd = CreatureDisplay(disp);
                d.Title = $"{r0.Item1} {(sex == 0 ? "Male" : "Female")}";
                d.Subtitle = $"Player character · race {race}";
                d.Chain.Add($"{d.Title} (player race {race})");
                d.Chain.Add($"display {disp}");
                if (cd?.Model != null) { d.Chain.Add(cd.Value.Model); d.Models.Add(cd.Value.Model); }
                var secs = Rows("SELECT section, variation, color, texture1, texture2, texture3 FROM char_section WHERE race=$0 AND sex=$1 ORDER BY section, variation, color",
                    r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), new[] { S(r, 3), S(r, 4), S(r, 5) }), race, sex);
                d.Chain.Add($"{secs.Count} character-creation textures (skin, face, facial hair, hair, underwear)");
                foreach (var s in secs)
                    foreach (var t in s.Item4)
                        if (t != null && !d.Textures.Any(x => string.Equals(x.Path, t, StringComparison.OrdinalIgnoreCase)))
                            d.Textures.Add(new TextureRef(t, $"{Catalog.Sections.GetValueOrDefault((int)s.Item1, s.Item1.ToString())} v{s.Item2} c{s.Item3}", ArchiveOf(t)));
                if (cd?.Model != null) AddEmbedded(d, cd.Value.Model, catalog);
                d.Facts.Add("In game the skin, face, facial hair and underwear textures are combined into one body texture; hair is separate.");
                break;
            }
            case "item":
            {
                var it = Rows("SELECT name, display_id, quality, inventory_type FROM item WHERE entry=$0", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)), id).FirstOrDefault();
                if (it.Item1 == null) break;
                d.Title = it.Item1;
                d.Subtitle = $"Item {id} · quality {it.Item3} · inventory type {it.Item4}";
                AddItemDisplay(d, it.Item2, $"{it.Item1} (item {id})");
                break;
            }
            case "idisplay":
                AddItemDisplay(d, id, null);
                break;
            case "go":
            {
                var g = Rows("SELECT name, type, display_id FROM gameobject WHERE entry=$0", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), id).FirstOrDefault();
                if (g.Item1 == null) break;
                d.Title = g.Item1;
                d.Subtitle = $"Game object {id} · type {g.Item2}";
                AddGameObjectDisplay(d, g.Item3, $"{g.Item1} (game object {id})", catalog);
                var sp = Spawns("gameobject_spawn", id);
                d.Facts.Add(sp.Count == 0 ? "Not spawned in the world (server database)." :
                    "Spawned: " + string.Join(", ", sp.Take(5).Select(s => $"{s.Count} on {s.Name ?? "map " + s.Map}")));
                break;
            }
            case "godisplay":
                AddGameObjectDisplay(d, id, null, catalog);
                break;
            case "spell":
            {
                var s = Rows("SELECT name, model, archive FROM spell_effect WHERE id=$0", r => (r.GetString(0), S(r, 1), S(r, 2)), id).FirstOrDefault();
                if (s.Item1 == null) break;
                d.Title = s.Item1;
                d.Subtitle = $"Spell effect {id}";
                d.Chain.Add($"{s.Item1} (SpellVisualEffectName {id})");
                if (s.Item2 != null)
                {
                    d.Chain.Add(s.Item2);
                    d.Models.Add(s.Item2);
                    if (s.Item3 == null) d.Facts.Add("The model file of this effect is not in the game archives.");
                    else AddEmbedded(d, s.Item2, catalog);
                }
                var uses = SpellUses(id, 60);
                d.UsedBy.AddRange(uses);
                if (uses.Count == 0) d.Facts.Add("No spell in Spell.dbc points at this effect.");
                break;
            }
            case "file":
            {
                var path = arg;
                d.Title = System.IO.Path.GetFileName(path);
                d.Subtitle = e.KindLabel;
                d.Chain.Add(path);
                if (e.Kind == AssetKind.Texture)
                {
                    var pair = GroundPair(path);
                    if (pair != null)
                    {
                        // both files of a ground texture (0.6.1): the game draws one or the other
                        d.Textures.Add(new TextureRef(pair.Base, "ground texture (named by the map; drawn with terrain specular off)", ArchiveOf(pair.Base)));
                        if (pair.Specular != null) d.Textures.Add(new TextureRef(pair.Specular, "specular variant _s (drawn with terrain specular on; alpha = shine)", ArchiveOf(pair.Specular)));
                        var maps = GroundTextureMaps(pair.Base);
                        if (maps.Count > 0) d.Facts.Add($"Ground texture of {maps.Sum(m => m.Tiles)} map tiles ({string.Join(", ", maps.Take(4).Select(m => $"{m.Name ?? "map " + m.Map} {m.Tiles}"))}{(maps.Count > 4 ? ", ..." : "")}).");
                        d.Facts.Add(pair.Specular != null
                            ? $"The game draws {System.IO.Path.GetFileName(pair.Specular)} when terrain specular is on (Config.wtf: specular 1 and pixelShaders 1) and {System.IO.Path.GetFileName(pair.Base)} when it is off: edit both."
                            : $"The game has no {System.IO.Path.GetFileName(Maps.GroundTextures.SpecularName(pair.Base))}: with terrain specular on this ground cannot be drawn from {System.IO.Path.GetFileName(pair.Base)}.");
                    }
                    else d.Textures.Add(new TextureRef(path, "this texture", ArchiveOf(path)));
                    d.UsedBy.AddRange(TextureUsers(path));
                    var models = ModelsUsingTexture(path);
                    if (models.Count > 0) d.Facts.Add($"Used by {models.Count} model{(models.Count == 1 ? "" : "s")}: {string.Join(", ", models.Take(5).Select(System.IO.Path.GetFileName))}{(models.Count > 5 ? ", ..." : "")}.");
                }
                else
                {
                    d.Models.Add(path);
                    AddEmbedded(d, path, catalog);
                    foreach (var u in Rows("SELECT id, name FROM spell_effect WHERE model=$0", r => $"spell effect {r.GetInt64(0)} {r.GetString(1)}", path)) d.UsedBy.Add(u);
                }
                var arch = ArchiveOf(path);
                if (arch != null) d.Facts.Add($"Stored in {arch}.");
                break;
            }
        }
        return d;
    }

    /// <summary>The spells that show a spell effect (Spell -> SpellVisual -> kit / missile -> SpellVisualEffectName), in words.</summary>
    public List<string> SpellUses(long effectId, int limit = 10000) =>
        Rows("SELECT spell_id, spell_name, spell_rank, group_concat(role, ', ') FROM spell_effect_use WHERE effect_id=$0 GROUP BY spell_id ORDER BY spell_name, spell_id LIMIT $1",
            r => $"{r.GetString(1)}{(string.IsNullOrEmpty(S(r, 2)) ? "" : " (" + r.GetString(2) + ")")} - spell {r.GetInt64(0)}, {r.GetString(3)}", effectId, limit);

    private string? ArchiveOf(string path) => Rows("SELECT archive FROM files WHERE path=$0", r => r.GetString(0), path).FirstOrDefault();

    private void AddCreatureDisplay(EntryDetails d, long disp, string? head, Catalog? catalog)
    {
        var cd = CreatureDisplay(disp);
        if (cd == null)
        {
            d.Facts.Add($"Display {disp} is not in CreatureDisplayInfo.dbc.");
            return;
        }
        var (_, model, skins) = cd.Value;
        var chain = new List<string>();
        if (head != null) chain.Add(head);
        chain.Add($"display {disp}");
        if (model != null) chain.Add(model);
        if (skins.Length > 0) chain.Add((skins.Length == 1 ? "skin: " : "skins: ") + string.Join(", ", skins));
        if (d.Chain.Count == 0) d.Chain.AddRange(chain);
        else d.Facts.Add("Also: " + string.Join(" → ", chain));
        if (d.Title.Length == 0) d.Title = model != null ? System.IO.Path.GetFileNameWithoutExtension(model) : $"display {disp}";
        if (model != null && !d.Models.Contains(model)) d.Models.Add(model);
        foreach (var f in Assets("creature", disp))
            if (f.Role != "model" && !d.Textures.Any(t => string.Equals(t.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                d.Textures.Add(new TextureRef(f.Path, f.Role, f.Archive, f.Archive == null ? "file missing from the archives" : ""));
        if (model != null)
        {
            var sets = CreatureSkinSets(model);
            int displays = sets.Sum(s => s.Displays.Count);
            var allSkins = sets.SelectMany(s => s.Skins).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (displays > 1)
                d.Facts.Add($"Texture variations of {System.IO.Path.GetFileName(model)}: {displays} displays use it with {allSkins.Count} skin{(allSkins.Count == 1 ? "" : "s")}: {string.Join(", ", allSkins.Take(12))}{(allSkins.Count > 12 ? ", ..." : "")}.");
            var bySkin = sets.Where(s => s.Skins.SequenceEqual(skins)).SelectMany(s => s.Displays).Where(x => x != disp).ToList();
            if (bySkin.Count > 0) d.Facts.Add($"The same skin is also used by display{(bySkin.Count == 1 ? "" : "s")} {string.Join(", ", bySkin.Take(10))}{(bySkin.Count > 10 ? ", ..." : "")}: a texture edit changes them too.");
        }
    }

    private void AddItemDisplay(EntryDetails d, long disp, string? head)
    {
        var files = Assets("item", disp);
        var chain = new List<string>();
        if (head != null) chain.Add(head);
        chain.Add($"item display {disp}");
        var models = files.Where(f => f.Role.StartsWith("model") && !f.Role.StartsWith("model texture")).Select(f => f.Path).ToList();
        if (models.Count == 1) chain.Add(models[0]);
        else if (models.Count > 1) chain.Add($"{models.Count} models (one per race/sex or per hand)");
        var tex = files.Where(f => f.Role.StartsWith("model texture")).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).Distinct().ToList();
        if (tex.Count > 0) chain.Add("texture: " + string.Join(", ", tex));
        d.Chain.AddRange(chain);
        if (d.Title.Length == 0) d.Title = models.Count > 0 ? System.IO.Path.GetFileNameWithoutExtension(models[0]) : $"item display {disp}";
        d.Models.AddRange(models);
        foreach (var f in files)
            if (!models.Contains(f.Path) && !d.Textures.Any(t => string.Equals(t.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                d.Textures.Add(new TextureRef(f.Path, f.Role, f.Archive, f.Archive == null ? "file missing from the archives" : ""));
        if (models.Count == 0) d.Facts.Add("This item has no 3D model (only an icon and/or body textures).");
        var others = Rows("SELECT COUNT(*) FROM item WHERE display_id=$0", r => r.GetInt64(0), disp).FirstOrDefault();
        if (others > 1) d.Facts.Add($"{others} items share item display {disp}: a texture edit changes all of them.");
    }

    private void AddGameObjectDisplay(EntryDetails d, long disp, string? head, Catalog? catalog)
    {
        var files = Assets("gameobject", disp);
        var model = files.FirstOrDefault(f => f.Role == "model")?.Path;
        if (head != null) d.Chain.Add(head);
        d.Chain.Add($"game object display {disp}");
        if (model != null)
        {
            d.Chain.Add(model);
            d.Models.Add(model);
        }
        foreach (var f in files.Where(f => f.Role != "model"))
            d.Textures.Add(new TextureRef(f.Path, f.Role, f.Archive));
        if (model != null && model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)) AddEmbedded(d, model, catalog);
    }

    private void AddEmbedded(EntryDetails d, string model, Catalog? catalog)
    {
        if (catalog == null) return;
        try
        {
            if (model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            {
                var data = catalog.Client.Read(model);
                if (data == null) return;
                foreach (var t in WmoHeader.Textures(data))
                {
                    var p = catalog.Canonical(t);
                    if (!d.Textures.Any(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase)))
                        d.Textures.Add(new TextureRef(p, "building texture", catalog.ArchiveOf(p)));
                }
                return;
            }
            foreach (var f in catalog.EmbeddedTextures(model))
                if (!d.Textures.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                    d.Textures.Add(new TextureRef(f.Path, f.Role, f.Archive));
            var slots = catalog.ModelTextureSlots(model).Where(t => t.Type != 0).Select(t => t.Type).Distinct().ToList();
            if (slots.Count > 0 && d.Textures.Count == 0)
                d.Facts.Add($"The textures of this model are chosen by the game at run time (replaceable slot{(slots.Count == 1 ? "" : "s")} {string.Join(", ", slots)}).");
        }
        catch (Exception ex) when (ex is MpqException or M2Exception or IOException)
        {
            d.Facts.Add("Could not read the model: " + ex.Message);
        }
    }
}
