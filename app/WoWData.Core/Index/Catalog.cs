using WoWData.Core.Dbc;
using WoWData.Core.M2;
using WoWData.Core.Mpq;

namespace WoWData.Core.Index;

/// <summary>A file a display uses: its role (model, skin1, model texture, icon...), path and the archive it comes from
/// (null when no archive has it).</summary>
public sealed record AssetFile(string Role, string Path, string? Archive);

public sealed record CreatureDisplayChain(int Id, int ModelId, string? Model, float Scale, string[] Skins, List<AssetFile> Files,
    CreatureDisplayInfoExtraRec? Extra);

/// <summary>
/// Display ids -> the files the client loads for them (port of wowdata/resolve.py). Rules from the client code:
///  * creature display: CreatureDisplayInfo.model_id -> CreatureModelData.model_path (.mdx -> .m2); TextureVariation
///    (skin 1-3) = &lt;model folder&gt;\&lt;skin&gt;.blp, filling the model's replaceable slots 11/12/13;
///    CreatureDisplayInfoExtra.bake = textures\BakedNpcTextures\&lt;bake&gt; (humanoid NPCs).
///  * game object display: GameObjectDisplayInfo.model_path.
///  * item display: models and textures under Item\ObjectComponents\&lt;slot&gt;\ (helmets: one model per race/sex,
///    &lt;model&gt;_&lt;prefix&gt;&lt;M|F&gt;.m2), body textures Item\TextureComponents\&lt;region&gt;Texture\ with the _M/_F/_U suffix
///    (CCharacterComponent_LoadArmorRegionTexture @00476e20), icon = Interface\Icons\&lt;icon&gt;.blp.
///  * character sections name their textures directly.
/// </summary>
public sealed class Catalog
{
    public static readonly string[] Regions = { "ArmUpper", "ArmLower", "Hand", "TorsoUpper", "TorsoLower", "LegUpper", "LegLower", "Foot" };
    public static readonly Dictionary<int, string> Sections = new() { [0] = "skin", [1] = "face", [2] = "facial hair", [3] = "hair", [4] = "underwear" };

    public ClientData Client { get; }
    private readonly Dictionary<string, DbcFile> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? _byBase;
    private Dictionary<string, List<string>>? _helmets;
    private readonly Dictionary<string, List<AssetFile>> _embedded = new(StringComparer.Ordinal);

    private Dictionary<int, CreatureDisplayInfoRec>? _cdi;
    private Dictionary<int, CreatureModelDataRec>? _cmd;
    private Dictionary<int, CreatureDisplayInfoExtraRec>? _cdie;
    private Dictionary<int, GameObjectDisplayInfoRec>? _godi;
    private Dictionary<int, ItemDisplayInfoRec>? _idi;

    public Catalog(ClientData client) { Client = client; }

    public DbcFile Table(string name)
    {
        if (!_tables.TryGetValue(name, out var t))
        {
            var data = Client.Read($"DBFilesClient\\{name}.dbc") ?? throw new DbcException($"DBFilesClient\\{name}.dbc not found");
            t = new DbcFile(data, name);
            _tables[name] = t;
        }
        return t;
    }

    public Dictionary<int, CreatureDisplayInfoRec> CreatureDisplays => _cdi ??= DbcTables.CreatureDisplayInfo(Table("CreatureDisplayInfo")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
    public Dictionary<int, CreatureModelDataRec> CreatureModels => _cmd ??= DbcTables.CreatureModelData(Table("CreatureModelData")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
    public Dictionary<int, CreatureDisplayInfoExtraRec> CreatureExtras => _cdie ??= DbcTables.CreatureDisplayInfoExtra(Table("CreatureDisplayInfoExtra")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
    public Dictionary<int, GameObjectDisplayInfoRec> GameObjectDisplays => _godi ??= DbcTables.GameObjectDisplayInfo(Table("GameObjectDisplayInfo")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
    public Dictionary<int, ItemDisplayInfoRec> ItemDisplays => _idi ??= DbcTables.ItemDisplayInfo(Table("ItemDisplayInfo")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());

    public IReadOnlyDictionary<string, ListedFile> Names() => Client.Names();

    public string? ArchiveOf(string path) => Names().TryGetValue(ClientData.ResolveName(path).ToUpperInvariant(), out var hit) ? hit.Archive : null;

    /// <summary>Path with the case of the listfile (or as given when unknown).</summary>
    public string Canonical(string path)
    {
        var p = ClientData.ResolveName(path);
        return Names().TryGetValue(p.ToUpperInvariant(), out var hit) ? hit.Path : p;
    }

    public List<string> ByBasename(string baseName, string prefix = "ITEM\\")
    {
        if (_byBase == null)
        {
            var idx = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in Names())
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var b = kv.Key[(kv.Key.LastIndexOf('\\') + 1)..];
                if (!idx.TryGetValue(b, out var l)) idx[b] = l = new List<string>();
                l.Add(kv.Value.Path);
            }
            _byBase = idx;
        }
        return _byBase.TryGetValue(baseName.ToUpperInvariant(), out var r) ? r : new List<string>();
    }

    /// <summary>Per race/sex helmet models &lt;stem&gt;_&lt;race prefix&gt;&lt;M|F&gt;.m2 under Item\ObjectComponents\Head.</summary>
    public List<string> HelmetModels(string stem)
    {
        if (_helmets == null)
        {
            var idx = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            const string head = "ITEM\\OBJECTCOMPONENTS\\HEAD\\";
            foreach (var kv in Names())
            {
                var u = kv.Key;
                if (!u.StartsWith(head, StringComparison.Ordinal) || !u.EndsWith(".M2", StringComparison.Ordinal)) continue;
                var rest = u[head.Length..];
                int us = rest.LastIndexOf('_');
                if (us < 0) continue;
                var key = rest[..us];
                if (!idx.TryGetValue(key, out var l)) idx[key] = l = new List<string>();
                l.Add(kv.Value.Path);
            }
            _helmets = idx;
        }
        var res = _helmets.TryGetValue(stem.ToUpperInvariant(), out var r) ? new List<string>(r) : new List<string>();
        res.Sort(StringComparer.Ordinal);
        return res;
    }

    // ------------------------------------------------------------------ creatures

    public CreatureDisplayChain? CreatureDisplay(int displayId)
    {
        if (!CreatureDisplays.TryGetValue(displayId, out var cdi)) return null;
        CreatureModels.TryGetValue(cdi.ModelId, out var cmd);
        var files = new List<AssetFile>();
        string? model = null;
        if (cmd != null && cmd.ModelPath.Length > 0)
        {
            model = Canonical(cmd.ModelPath);
            files.Add(new("model", model, ArchiveOf(model)));
            int bs = model.LastIndexOf('\\');
            var folder = bs >= 0 ? model[..bs] : "";
            for (int i = 0; i < cdi.Skin.Length; i++)
            {
                if (cdi.Skin[i].Length == 0) continue;
                var p = Canonical(folder + "\\" + cdi.Skin[i] + ".blp");
                files.Add(new($"skin{i + 1}", p, ArchiveOf(p)));
            }
            if (ArchiveOf(model) != null) files.AddRange(EmbeddedTextures(model));
        }
        CreatureDisplayInfoExtraRec? extra = null;
        if (cdi.ExtraId != 0 && CreatureExtras.TryGetValue(cdi.ExtraId, out var ex))
        {
            extra = ex;
            if (ex.Bake.Length > 0)
            {
                var p = Canonical("Textures\\BakedNpcTextures\\" + ex.Bake);
                files.Add(new("baked skin", p, ArchiveOf(p)));
            }
        }
        return new CreatureDisplayChain(displayId, cdi.ModelId, model, cdi.Scale, cdi.Skin.Where(s => s.Length > 0).ToArray(), files, extra);
    }

    /// <summary>Textures named inside a model (type 0 entries).</summary>
    public List<AssetFile> EmbeddedTextures(string model)
    {
        var key = model.ToUpperInvariant();
        if (_embedded.TryGetValue(key, out var cached)) return cached;
        var list = new List<AssetFile>();
        try
        {
            var data = Client.Read(model);
            if (data != null)
                foreach (var t in new M2Header(data).Textures())
                    if (t.Type == 0 && t.FileName.Length > 0)
                    {
                        var p = Canonical(t.FileName);
                        list.Add(new("model texture", p, ArchiveOf(p)));
                    }
        }
        catch (M2Exception) { }
        _embedded[key] = list;
        return list;
    }

    /// <summary>Replaceable texture slots of a model (type != 0), for the variant relations.</summary>
    public List<M2Texture> ModelTextureSlots(string model)
    {
        try
        {
            var data = Client.Read(model);
            return data == null ? new List<M2Texture>() : new M2Header(data).Textures();
        }
        catch (M2Exception)
        {
            return new List<M2Texture>();
        }
    }

    // ------------------------------------------------------------------ game objects

    public (string? Model, List<AssetFile> Files)? GameObjectDisplay(int displayId)
    {
        if (!GameObjectDisplays.TryGetValue(displayId, out var r)) return null;
        var files = new List<AssetFile>();
        string? model = null;
        if (r.ModelPath.Length > 0)
        {
            model = Canonical(r.ModelPath);
            files.Add(new("model", model, ArchiveOf(model)));
            if (model.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) && ArchiveOf(model) != null) files.AddRange(EmbeddedTextures(model));
        }
        return (model, files);
    }

    // ------------------------------------------------------------------ items

    public List<AssetFile>? ItemDisplay(int displayId)
    {
        if (!ItemDisplays.TryGetValue(displayId, out var r)) return null;
        var files = new List<AssetFile>();
        var modelDirs = new List<string>();
        for (int i = 0; i < r.Model.Length; i++)
        {
            var m = r.Model[i];
            if (m.Length == 0) continue;
            var stem = StripExt(m);
            var hits = ByBasename(stem + ".m2");
            if (hits.Count > 0)
            {
                foreach (var h in hits)
                {
                    files.Add(new($"model{i + 1}", h, ArchiveOf(h)));
                    modelDirs.Add(Dir(h).ToUpperInvariant());
                }
            }
            else
            {
                foreach (var p in HelmetModels(stem))
                {
                    files.Add(new($"model{i + 1} (race/sex)", p, ArchiveOf(p)));
                    modelDirs.Add(Dir(p).ToUpperInvariant());
                }
            }
        }
        for (int i = 0; i < r.ModelTexture.Length; i++)
        {
            var t = r.ModelTexture[i];
            if (t.Length == 0) continue;
            var hits = ByBasename(t + ".blp");
            var same = hits.Where(h => modelDirs.Contains(Dir(h).ToUpperInvariant())).ToList();
            foreach (var h in same.Count > 0 ? same : hits) files.Add(new($"model texture{i + 1}", h, ArchiveOf(h)));
        }
        for (int i = 0; i < Regions.Length && i < r.Texture.Length; i++)
        {
            var tex = r.Texture[i];
            if (tex.Length == 0) continue;
            foreach (var suf in new[] { "_M", "_F", "_U" })
            {
                var p = $"Item\\TextureComponents\\{Regions[i]}Texture\\{tex}{suf}.blp";
                if (ArchiveOf(p) != null)
                {
                    p = Canonical(p);
                    files.Add(new($"{Regions[i]} texture{suf}", p, ArchiveOf(p)));
                }
            }
        }
        if (r.Icon.Length > 0)
        {
            var p = Canonical($"Interface\\Icons\\{r.Icon}.blp");
            files.Add(new("icon", p, ArchiveOf(p)));
        }
        return files;
    }

    // ------------------------------------------------------------------ characters

    public List<AssetFile> SectionFiles(CharSectionsRec row)
    {
        var files = new List<AssetFile>();
        foreach (var t in row.Texture)
        {
            if (t.Length == 0) continue;
            var p = Canonical(t);
            files.Add(new($"{(Sections.TryGetValue(row.Section, out var s) ? s : row.Section.ToString())} v{row.Variation} c{row.Color}", p, ArchiveOf(p)));
        }
        return files;
    }

    private static string StripExt(string p)
    {
        int dot = p.LastIndexOf('.');
        int bs = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return dot > bs ? p[..dot] : p;
    }

    private static string Dir(string p)
    {
        int bs = p.LastIndexOf('\\');
        return bs >= 0 ? p[..bs] : "";
    }
}
