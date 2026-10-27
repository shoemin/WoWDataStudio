using System.Buffers.Binary;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Versions;

namespace WoWData.Core.Index;

/// <summary>Readable and locked records of one DB2 table (locked = in encrypted sections; never guessed).</summary>
public sealed record LockedCount(string Table, int Readable, int Locked, int Copies)
{
    public int Total => Readable + Locked;
}

/// <summary>A creature display of a modern client: the model and its textures by FileDataID.</summary>
public sealed record CreatureDisplay160(int Id, int ModelId, uint Model, float Scale, uint[] Skins, int ExtraId, int Race, int Sex, uint Bake, uint HdBake);

/// <summary>One model of an item display: the model file, and the race / sex it is for (0 = any).</summary>
public sealed record ItemModel160(int Slot, uint File, int Race, int Gender);

/// <summary>A body texture of an item display: the region (ComponentSection), the file, the sex it is for (0 male,
/// 1 female, 3 either, -1 unknown).</summary>
public sealed record ItemBodyTexture160(int Section, uint File, int Gender);

public sealed record ItemDisplay160(int Id, List<ItemModel160> Models, List<(int Slot, uint File)> ModelTextures, List<ItemBodyTexture160> BodyTextures, uint Icon);

/// <summary>
/// The display chains of a modern client (M1 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, section 3.2): the same
/// questions <see cref="Catalog"/> answers for 1.12, answered from the DB2 tables with the columns named by our own
/// measured layout file (<see cref="Db2Layouts"/>). Files are FileDataIDs; names come from <see cref="GameNames"/>.
/// A table whose layout does not match is reported in <see cref="Problems"/> and treated as empty.
/// </summary>
public sealed class Catalog160
{
    private readonly CascStorage _storage;
    private readonly Db2Catalog _catalog;
    private readonly Db2Layouts? _layouts;
    private readonly Dictionary<string, Db2Table?> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = new();
    private readonly object _gate = new();
    private readonly Dictionary<uint, List<uint>> _embedded = new();

    public Catalog160(CascStorage storage, Db2Catalog catalog, int build)
    {
        _storage = storage;
        _catalog = catalog;
        _layouts = Db2Layouts.ForBuild(build);
        Build = build;
    }

    public int Build { get; }
    public CascStorage Storage => _storage;
    /// <summary>Tables that could not be read or whose columns are not known for this build.</summary>
    public IReadOnlyList<string> Problems { get { lock (_gate) return _problems.ToList(); } }
    /// <summary>True when the build has a measured layout file (else nothing is named and the chains are empty).</summary>
    public bool HasLayouts => _layouts != null;

    /// <summary>A table with its named columns, or null (not in the client, not on this computer, or its layout does
    /// not match this build's measured one).</summary>
    public Db2Table? Table(string name)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(name, out var t)) return t;
            t = _catalog.Open(_storage, name);
            if (t == null) _problems.Add($"{name}: not readable in this client.");
            else if (_layouts == null) { _problems.Add($"{name}: no column names for build {Build}."); t = null; }
            else if (!_layouts.Apply(t, out var why)) { _problems.Add(why); t = null; }
            _tables[name] = t;
            return t;
        }
    }

    /// <summary>Readable and locked records of every table read so far (copy-table rows counted apart).</summary>
    public List<LockedCount> Locked()
    {
        lock (_gate)
            return _tables.Values.Where(t => t != null).Select(t => new LockedCount(t!.Name, t.Records.Count - t.CopyCount, t.SkippedRecords, t.CopyCount))
                .OrderBy(l => l.Table, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int C(Db2Table t, string col) => t.ColumnIndex(col);

    // ------------------------------------------------------------------ creatures

    private Dictionary<int, uint>? _modelFiles;
    private Dictionary<int, (int Race, int Sex, uint Bake, uint HdBake)>? _extras;
    private Dictionary<int, List<uint>>? _texturesByMaterial;
    private Dictionary<int, List<uint>>? _modelsByResource;

    /// <summary>TextureFileData: MaterialResourcesID -> texture FileDataIDs.</summary>
    public Dictionary<int, List<uint>> TexturesByMaterial
    {
        get
        {
            lock (_gate)
            {
                if (_texturesByMaterial != null) return _texturesByMaterial;
            }
            var res = new Dictionary<int, List<uint>>();
            if (Table("TextureFileData") is { } t)
            {
                int m = C(t, "MaterialResourcesID");
                foreach (var r in t.Records)
                {
                    if (!res.TryGetValue(r.Int(m), out var l)) res[r.Int(m)] = l = new List<uint>();
                    if (!l.Contains((uint)r.Id)) l.Add((uint)r.Id);
                }
            }
            lock (_gate) return _texturesByMaterial ??= res;
        }
    }

    /// <summary>ModelFileData: ModelResourcesID -> model FileDataIDs.</summary>
    public Dictionary<int, List<uint>> ModelsByResource
    {
        get
        {
            lock (_gate)
            {
                if (_modelsByResource != null) return _modelsByResource;
            }
            var res = new Dictionary<int, List<uint>>();
            if (Table("ModelFileData") is { } t)
            {
                int m = C(t, "ModelResourcesID");
                foreach (var r in t.Records)
                {
                    if (!res.TryGetValue(r.Int(m), out var l)) res[r.Int(m)] = l = new List<uint>();
                    if (!l.Contains((uint)r.Id)) l.Add((uint)r.Id);
                }
            }
            lock (_gate) return _modelsByResource ??= res;
        }
    }

    private uint FirstTexture(int material) => material != 0 && TexturesByMaterial.TryGetValue(material, out var l) && l.Count > 0 ? l[0] : 0;

    /// <summary>Every creature display ID (readable records), ascending.</summary>
    public List<int> CreatureDisplayIds() => Table("CreatureDisplayInfo")?.Records.Select(r => r.Id).Distinct().OrderBy(i => i).ToList() ?? new List<int>();

    public CreatureDisplay160? CreatureDisplay(int id)
    {
        var t = Table("CreatureDisplayInfo");
        var r = t?.Get(id);
        if (t == null || r == null) return null;
        if (_modelFiles == null)
        {
            var mf = new Dictionary<int, uint>();
            if (Table("CreatureModelData") is { } cmd)
            {
                int f = C(cmd, "FileDataID");
                foreach (var m in cmd.Records) mf[m.Id] = m.UInt(f);
            }
            var ex = new Dictionary<int, (int, int, uint, uint)>();
            if (Table("CreatureDisplayInfoExtra") is { } x)
            {
                int race = C(x, "DisplayRaceID"), sex = C(x, "DisplaySexID"), bake = C(x, "BakeMaterialResourcesID"), hd = C(x, "HDBakeMaterialResourcesID");
                foreach (var e in x.Records) ex[e.Id] = (e.Int(race), e.Int(sex), FirstTexture(e.Int(bake)), FirstTexture(e.Int(hd)));
            }
            lock (_gate)
            {
                _extras ??= ex;
                _modelFiles ??= mf;
            }
        }
        int modelId = r.Int(C(t, "ModelID"));
        int extra = r.Int(C(t, "ExtendedDisplayInfoID"));
        var skins = new uint[4];
        int s0 = C(t, "TextureVariationFileDataID1");
        for (int i = 0; i < 4; i++) skins[i] = r.UInt(s0 + i);
        _extras!.TryGetValue(extra, out var exr);
        return new CreatureDisplay160(id, modelId, _modelFiles!.GetValueOrDefault(modelId), r.Float(C(t, "CreatureModelScale")), skins,
            extra, extra != 0 ? exr.Race : 0, extra != 0 ? exr.Sex : 0, extra != 0 ? exr.Bake : 0, extra != 0 ? exr.HdBake : 0);
    }

    /// <summary>The creatures the client names itself (Creature table): id, name, title, displays.</summary>
    public List<(int Id, string Name, string Title, int[] Displays)> NamedCreatures()
    {
        var res = new List<(int, string, string, int[])>();
        if (Table("Creature") is not { } t) return res;
        int name = C(t, "Name"), title = C(t, "Title"), d0 = C(t, "DisplayID1");
        foreach (var r in t.Records.GroupBy(r => r.Id).Select(g => g.First()).OrderBy(r => r.Id))
        {
            var n = r.String(name) ?? "";
            if (n.Length == 0) continue;
            var ds = Enumerable.Range(0, 4).Select(i => r.Int(d0 + i)).Where(v => v > 0).Distinct().ToArray();
            res.Add((r.Id, n, r.String(title) ?? "", ds));
        }
        return res;
    }

    // ------------------------------------------------------------------ game objects

    public List<int> GameObjectDisplayIds() => Table("GameObjectDisplayInfo")?.Records.Select(r => r.Id).Distinct().OrderBy(i => i).ToList() ?? new List<int>();

    public uint GameObjectModel(int displayId)
    {
        var t = Table("GameObjectDisplayInfo");
        var r = t?.Get(displayId);
        return t == null || r == null ? 0 : r.UInt(C(t, "FileDataID"));
    }

    /// <summary>The game objects the client places itself: id, name, display.</summary>
    public List<(int Id, string Name, int Display)> GameObjects()
    {
        var res = new List<(int, string, int)>();
        if (Table("GameObjects") is not { } t) return res;
        int name = C(t, "Name"), disp = C(t, "DisplayID");
        foreach (var r in t.Records.GroupBy(r => r.Id).Select(g => g.First()).OrderBy(r => r.Id))
            res.Add((r.Id, r.String(name) ?? "", r.Int(disp)));
        return res;
    }

    // ------------------------------------------------------------------ items

    private Dictionary<uint, (int Race, int Gender)>? _componentModels;
    private Dictionary<uint, int>? _componentTextures;
    private Dictionary<int, List<(int Section, int Material)>>? _materialRes;
    private Dictionary<int, uint>? _iconByDisplay;

    public List<int> ItemDisplayIds() => Table("ItemDisplayInfo")?.Records.Select(r => r.Id).Distinct().OrderBy(i => i).ToList() ?? new List<int>();

    private void LoadItemTables()
    {
        if (_iconByDisplay != null) return;
        var cm = new Dictionary<uint, (int, int)>();
        if (Table("ComponentModelFileData") is { } c)
        {
            int race = C(c, "RaceID"), g = C(c, "GenderIndex");
            foreach (var r in c.Records) cm[(uint)r.Id] = (r.Int(race), r.Int(g));
        }
        var ct = new Dictionary<uint, int>();
        if (Table("ComponentTextureFileData") is { } c2)
        {
            int g = C(c2, "GenderIndex");
            foreach (var r in c2.Records) ct[(uint)r.Id] = r.Int(g);
        }
        var mr = new Dictionary<int, List<(int, int)>>();
        if (Table("ItemDisplayInfoMaterialRes") is { } m)
        {
            int sec = C(m, "ComponentSection"), mat = C(m, "MaterialResourcesID");
            foreach (var r in m.Records.Where(r => r.HasParent))
            {
                if (!mr.TryGetValue(r.ParentId, out var l)) mr[r.ParentId] = l = new List<(int, int)>();
                l.Add((r.Int(sec), r.Int(mat)));
            }
        }
        var icons = new Dictionary<int, uint>();
        if (Table("ItemAppearance") is { } a)
        {
            int d = C(a, "ItemDisplayInfoID"), ic = C(a, "DefaultIconFileDataID");
            foreach (var r in a.Records.OrderBy(r => r.Id))
                if (r.Int(d) != 0 && r.UInt(ic) != 0) icons.TryAdd(r.Int(d), r.UInt(ic));
        }
        lock (_gate)
        {
            _componentModels ??= cm;
            _componentTextures ??= ct;
            _materialRes ??= mr;
            _iconByDisplay ??= icons;
        }
    }

    public ItemDisplay160? ItemDisplay(int id)
    {
        var t = Table("ItemDisplayInfo");
        var r = t?.Get(id);
        if (t == null || r == null) return null;
        LoadItemTables();
        var models = new List<ItemModel160>();
        var textures = new List<(int, uint)>();
        int m0 = C(t, "ModelResourcesID1"), t0 = C(t, "ModelMaterialResourcesID1");
        for (int slot = 0; slot < 2; slot++)
        {
            int res = r.Int(m0 + slot);
            if (res != 0 && ModelsByResource.TryGetValue(res, out var files))
                foreach (var f in files.OrderBy(f => _componentModels!.TryGetValue(f, out var cm) ? (cm.Race, cm.Gender) : (0, 0)).ThenBy(f => f))
                {
                    var (race, gender) = _componentModels!.TryGetValue(f, out var cm2) ? cm2 : (0, -1);
                    models.Add(new ItemModel160(slot + 1, f, race, gender));
                }
            int mat = r.Int(t0 + slot);
            if (mat != 0 && TexturesByMaterial.TryGetValue(mat, out var tex))
                foreach (var f in tex) textures.Add((slot + 1, f));
        }
        var body = new List<ItemBodyTexture160>();
        if (_materialRes!.TryGetValue(id, out var secs))
            foreach (var (sec, mat) in secs.OrderBy(s => s.Section))
                if (TexturesByMaterial.TryGetValue(mat, out var tex))
                    foreach (var f in tex) body.Add(new ItemBodyTexture160(sec, f, _componentTextures!.TryGetValue(f, out var g) ? g : -1));
        return new ItemDisplay160(id, models, textures, body, _iconByDisplay!.GetValueOrDefault(id));
    }

    /// <summary>Every item the client names (ItemSparse), with its first appearance's display and icon.</summary>
    public List<(int Id, string Name, int Display, uint Icon)> Items()
    {
        var res = new List<(int, string, int, uint)>();
        if (Table("ItemSparse") is not { } sp) return res;
        var display = new Dictionary<int, (int Display, uint Icon)>();
        var ima = Table("ItemModifiedAppearance");
        var ia = Table("ItemAppearance");
        if (ima != null && ia != null)
        {
            int item = C(ima, "ItemID"), app = C(ima, "ItemAppearanceID"), order = C(ima, "OrderIndex");
            int d = C(ia, "ItemDisplayInfoID"), ic = C(ia, "DefaultIconFileDataID");
            foreach (var r in ima.Records.OrderBy(r => r.Int(order)).ThenBy(r => r.Id))
            {
                var a = ia.Get(r.Int(app));
                if (a == null) continue;
                display.TryAdd(r.Int(item), (a.Int(d), a.UInt(ic)));
            }
        }
        int name = C(sp, "Display");
        foreach (var r in sp.Records.GroupBy(r => r.Id).Select(g => g.First()).OrderBy(r => r.Id))
        {
            var n = r.String(name) ?? "";
            if (n.Length == 0) continue;
            display.TryGetValue(r.Id, out var di);
            res.Add((r.Id, n, di.Display, di.Icon));
        }
        return res;
    }

    // ------------------------------------------------------------------ spell effects

    /// <summary>SpellVisualEffectName: id -> (model, texture) FileDataIDs.</summary>
    public List<(int Id, uint Model, uint Texture)> SpellEffects()
    {
        var res = new List<(int, uint, uint)>();
        if (Table("SpellVisualEffectName") is not { } t) return res;
        int m = C(t, "ModelFileDataID"), tx = C(t, "TextureFileDataID");
        foreach (var r in t.Records.GroupBy(r => r.Id).Select(g => g.First()).OrderBy(r => r.Id)) res.Add((r.Id, r.UInt(m), r.UInt(tx)));
        return res;
    }

    /// <summary>
    /// The spells that show each spell effect: SpellXSpellVisual (parent = spell) -> SpellVisual -> SpellVisualEvent
    /// (parent = visual) -> SpellVisualKit -> SpellVisualKitModelAttach (parent = kit) -> SpellVisualEffectName, and
    /// SpellVisual.SpellVisualMissileSetID -> SpellVisualMissile (parent = set) -> SpellVisualEffectName.
    /// </summary>
    public List<(int Effect, int Spell, string SpellName, int Visual, string Role)> SpellEffectUses()
    {
        var res = new List<(int, int, string, int, string)>();
        var sx = Table("SpellXSpellVisual");
        var names = Table("SpellName");
        if (sx == null || names == null) return res;
        var kitsByVisual = new Dictionary<int, List<int>>();
        if (Table("SpellVisualEvent") is { } ev)
        {
            int kit = C(ev, "SpellVisualKitID");
            foreach (var r in ev.Records.Where(r => r.HasParent))
            {
                if (!kitsByVisual.TryGetValue(r.ParentId, out var l)) kitsByVisual[r.ParentId] = l = new List<int>();
                l.Add(r.Int(kit));
            }
        }
        var effectsByKit = new Dictionary<int, List<int>>();
        if (Table("SpellVisualKitModelAttach") is { } ma)
        {
            int e = C(ma, "SpellVisualEffectNameID");
            foreach (var r in ma.Records.Where(r => r.HasParent))
            {
                if (!effectsByKit.TryGetValue(r.ParentId, out var l)) effectsByKit[r.ParentId] = l = new List<int>();
                l.Add(r.Int(e));
            }
        }
        var missileSet = new Dictionary<int, int>();
        if (Table("SpellVisual") is { } sv)
        {
            int set = C(sv, "SpellVisualMissileSetID");
            foreach (var r in sv.Records) if (r.Int(set) > 0) missileSet.TryAdd(r.Id, r.Int(set));
        }
        var effectsBySet = new Dictionary<int, List<int>>();
        if (Table("SpellVisualMissile") is { } mi)
        {
            int e = C(mi, "SpellVisualEffectNameID");
            foreach (var r in mi.Records.Where(r => r.HasParent))
            {
                if (!effectsBySet.TryGetValue(r.ParentId, out var l)) effectsBySet[r.ParentId] = l = new List<int>();
                l.Add(r.Int(e));
            }
        }
        int vcol = C(sx, "SpellVisualID"), ncol = C(names, "Name");
        foreach (var x in sx.Records.Where(r => r.HasParent).OrderBy(r => r.ParentId).ThenBy(r => r.Id))
        {
            int spell = x.ParentId, visual = x.Int(vcol);
            var name = names.Get(spell)?.String(ncol) ?? $"spell {spell}";
            var seen = new HashSet<(int, string)>();
            if (kitsByVisual.TryGetValue(visual, out var kits))
                foreach (var k in kits)
                    if (effectsByKit.TryGetValue(k, out var effs))
                        foreach (var e in effs)
                            if (e > 0 && seen.Add((e, "kit"))) res.Add((e, spell, name, visual, "kit"));
            if (missileSet.TryGetValue(visual, out var set) && effectsBySet.TryGetValue(set, out var mis))
                foreach (var e in mis)
                    if (e > 0 && seen.Add((e, "missile"))) res.Add((e, spell, name, visual, "missile"));
        }
        return res;
    }

    // ------------------------------------------------------------------ maps, interface names

    public List<(int Id, string Directory, string Name)> Maps()
    {
        var res = new List<(int, string, string)>();
        if (Table("Map") is not { } t) return res;
        int d = C(t, "Directory"), n = C(t, "MapName");
        foreach (var r in t.Records.GroupBy(r => r.Id).Select(g => g.First()).OrderBy(r => r.Id)) res.Add((r.Id, r.String(d) ?? "", r.String(n) ?? ""));
        return res;
    }

    /// <summary>The client's own interface file list: (FileDataID, folder\file).</summary>
    public IEnumerable<(uint Fdid, string Path)> InterfaceFiles()
    {
        if (Table("ManifestInterfaceData") is not { } t) yield break;
        int p = C(t, "FilePath"), f = C(t, "FileName");
        foreach (var r in t.Records)
        {
            var dir = (r.String(p) ?? "").Replace('/', '\\').TrimEnd('\\');
            var file = r.String(f) ?? "";
            if (file.Length == 0) continue;
            yield return ((uint)r.Id, dir.Length > 0 ? dir + "\\" + file : file);
        }
    }

    // ------------------------------------------------------------------ embedded textures (on demand)

    /// <summary>
    /// The texture FileDataIDs a modern model names itself (its TXID chunk: one per texture record, 0 for the textures
    /// the game chooses at run time). Read on demand and cached; never during the index build (design 3.2). Empty for
    /// a model that is not on this computer or is not a chunked (MD21) model.
    /// </summary>
    public List<uint> EmbeddedTextures(uint model)
    {
        lock (_gate)
            if (_embedded.TryGetValue(model, out var c)) return c;
        var list = new List<uint>();
        try
        {
            var data = _storage.ReadFileDataId(model);
            if (data != null) list = TextureIds(data);
        }
        catch (Exception e) when (e is CascMissingException or CascEncryptedException or InvalidDataException) { }
        lock (_gate)
        {
            if (_embedded.Count > 4096) _embedded.Clear();
            _embedded[model] = list;
        }
        return list;
    }

    /// <summary>The TXID chunk of a chunked M2 (MD21 ...): its FileDataIDs in texture order (0 kept for run-time slots).</summary>
    public static List<uint> TextureIds(ReadOnlySpan<byte> m2)
    {
        var res = new List<uint>();
        int o = 0;
        while (o + 8 <= m2.Length)
        {
            var magic = m2.Slice(o, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(m2.Slice(o + 4, 4));
            if (size < 0 || o + 8L + size > m2.Length) break;
            if (magic.SequenceEqual("TXID"u8))
            {
                for (int i = 0; i + 4 <= size; i += 4) res.Add(BinaryPrimitives.ReadUInt32LittleEndian(m2.Slice(o + 8 + i, 4)));
                break;
            }
            if (o == 0 && !magic.SequenceEqual("MD21"u8)) break;
            o += 8 + size;
        }
        return res;
    }
}
