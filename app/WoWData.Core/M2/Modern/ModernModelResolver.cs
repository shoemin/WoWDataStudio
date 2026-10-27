using WoWData.Core.Blp;
using WoWData.Core.Casc;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.Versions;

namespace WoWData.Core.M2.Modern;

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (2.2): the models of another version opened read only, read by
/// <see cref="M2ModernReader"/> from <see cref="ModernClientFiles"/> (the model by name or #FileDataID, its .skin files
/// by FileDataID, its textures named through the session's name table).
/// </summary>
public sealed class ModernModelSource : IModelSource
{
    private readonly ModernClientFiles _files;

    public ModernModelSource(ModernClientFiles files) => _files = files;

    /// <summary>The name the session knows for a FileDataID, else "#id" with <paramref name="ext"/>.</summary>
    public static string NameOf(ModernClientFiles files, uint fdid, string ext)
    {
        var info = files.Names?.ByFileDataId(fdid);
        return info != null && info.Source != NameSource.Number && !info.Path.StartsWith('#') ? info.Path : $"#{fdid}{ext}";
    }

    public M2Model Load(string path)
    {
        byte[]? data;
        try { data = _files.Read(path); }
        catch (ClientFileException e) { throw new IOException(e.Message, e); }
        if (data == null) throw new IOException($"{path} is not a file of this client");
        return M2ModernReader.Load(data, id => _files.Read("#" + id), id => NameOf(_files, id, ".blp"));
    }
}

/// <summary>
/// M2 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md (3.3): the looks of another version's models, from its DB2 tables
/// (<see cref="Catalog160"/>, our measured column names) and files by FileDataID:
/// <list type="bullet">
/// <item>creature displays: CreatureDisplayInfo.TextureVariationFileDataID[0..2] fill the skin slots 11-13;</item>
/// <item>humanoid NPCs (CreatureDisplayInfoExtra): the baked NPC texture (BakeMaterialResourcesID -> TextureFileData,
///   else the high-resolution bake) fills slot 1; their hair, face and clothes choices come with the characters (M6),
///   so the 1.12 default geosets are shown;</item>
/// <item>item displays: the model texture of the model's slot (ModelMaterialResourcesID -> TextureFileData) fills
///   slot 2; which model is for which race and sex comes from ComponentModelFileData (the index lists them);</item>
/// <item>a model's default look: the first creature display, else the first item display using it;</item>
/// <item>animation names: AnimationData of your 1.12 game for the ids 1.12 has (the ids are the same; 1.60's own
///   table has no name column), "Anim&lt;id&gt;" for the others.</item>
/// </list>
/// Textures are decoded at most <see cref="MaxTextureSize"/> pixels wide or high (a smaller mip level, as the game's
/// texture quality setting does), so the decoded-texture cache keeps several.
/// </summary>
public sealed class ModernModelResolver : IModelLooks
{
    public const int MaxTextureSize = 1024;

    private readonly Catalog160 _cat;
    private readonly ModernClientFiles _files;
    private readonly ImageCache _blp = new();
    private readonly AnimationCatalog _animations;
    private readonly Dictionary<int, string> _animNames;
    private readonly object _gate = new();
    private Dictionary<uint, int>? _creatureByModel;
    private Dictionary<uint, int>? _itemByModel;

    public ModernModelResolver(Catalog160 cat, ModernClientFiles files, AnimationCatalog? animations112 = null)
    {
        _cat = cat;
        _files = files;
        _animations = animations112 ?? AnimationCatalog.Empty;
        _animNames = _animations.Rows.Where(r => r.Name.Length > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Name);
    }

    public Catalog160 Catalog => _cat;
    public ImageCache TextureCache => _blp;
    public void ClearTextureCache() => _blp.Clear();
    public string Canonical(string path) => path;
    public IReadOnlyDictionary<int, string> AnimationNames => _animNames;
    public string AnimationName(int id) => _animNames.TryGetValue(id, out var n) ? n : $"Anim{id}";
    public AnimationCatalog Animations => _animations;
    /// <summary>True when the animation names come from your 1.12 game's AnimationData.</summary>
    public bool HasAnimationNames => _animNames.Count > 0;

    private string NameOf(uint fdid, string ext) => ModernModelSource.NameOf(_files, fdid, ext);

    /// <summary>The largest mip level of at most <see cref="MaxTextureSize"/> pixels per side.</summary>
    public static int LevelFor(BlpFile blp)
    {
        int level = 0;
        while (Math.Max(blp.Width, blp.Height) >> level > MaxTextureSize && level + 1 < blp.LevelCount) level++;
        return level;
    }

    public RgbaImage? LoadBlp(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return _blp.GetOrAdd(path, p =>
        {
            try
            {
                var data = _files.Read(p);
                if (data == null) return null;
                var blp = BlpFile.Parse(data);
                return blp.Decode(LevelFor(blp));
            }
            catch (Exception e) when (e is BlpException or ClientFileException or IOException or ArgumentException or IndexOutOfRangeException or InvalidDataException) { return null; }
        });
    }

    /// <summary>Why a texture file cannot be shown ("encrypted", "not downloaded"...), for the notes.</summary>
    private string Why(uint fdid)
    {
        if (!_files.Storage.Root.Contains(fdid)) return "not in this client";
        var st = _files.Storage.State(fdid);
        return st == CascFileState.Local ? "not a readable texture" : ModernFileInfo.StateWord(st);
    }

    private void Fill(ModelLook l, int slot, uint fdid, string role)
    {
        if (fdid == 0) return;
        var p = NameOf(fdid, ".blp");
        var img = LoadBlp(p);
        if (img != null) l.Slots[slot] = new ResolvedTexture(img, role == "" ? p : $"{role} {p}", p);
        else l.Notes.Add($"{role} {p} cannot be shown ({Why(fdid)}).".TrimStart());
    }

    public ModelLook CreatureDisplay(int displayId)
    {
        var d = _cat.CreatureDisplay(displayId);
        if (d == null) return new ModelLook { Notes = { $"Display {displayId} is not readable (encrypted, or not in this client)." } };
        var l = new ModelLook();
        for (int i = 0; i < 3; i++) Fill(l, 11 + i, d.Skins[i], "skin");
        if (d.ExtraId != 0)
        {
            Fill(l, 1, d.Bake != 0 ? d.Bake : d.HdBake, "baked NPC texture");
            l.Geosets = new HashSet<int>(ModelResolver.DefaultGroupGeosets) { 0 };
            l.Notes.Add("A humanoid NPC: its baked texture is shown; its hair, face and clothes choices come later (characters), so the default parts are shown.");
        }
        return l;
    }

    public ModelLook ItemDisplay(int displayId, string? model = null)
    {
        var l = new ModelLook();
        var d = _cat.ItemDisplay(displayId);
        if (d == null) return l;
        int slot = 1;
        if (model != null && _files.IdOf(model) is { } id && d.Models.FirstOrDefault(m => m.File == id) is { } mine) slot = mine.Slot;
        var tex = d.ModelTextures.FirstOrDefault(t => t.Slot == slot);
        if (tex.File == 0) tex = d.ModelTextures.FirstOrDefault();
        Fill(l, 2, tex.File, "");
        return l;
    }

    public ModelLook DefaultFor(string model)
    {
        if (_files.IdOf(model) is not { } fdid) return new ModelLook();
        lock (_gate)
        {
            if (_creatureByModel == null)
            {
                var c = new Dictionary<uint, int>();
                foreach (var id in _cat.CreatureDisplayIds())
                    if (_cat.CreatureDisplay(id) is { Model: not 0 } d) c.TryAdd(d.Model, id);
                _creatureByModel = c;
            }
        }
        if (_creatureByModel.TryGetValue(fdid, out var disp)) return CreatureDisplay(disp);
        lock (_gate)
        {
            if (_itemByModel == null)
            {
                var m = new Dictionary<uint, int>();
                foreach (var id in _cat.ItemDisplayIds())
                    if (_cat.ItemDisplay(id) is { } d)
                        foreach (var x in d.Models) m.TryAdd(x.File, id);
                _itemByModel = m;
            }
        }
        return _itemByModel.TryGetValue(fdid, out var item) ? ItemDisplay(item, model) : new ModelLook();
    }

    /// <summary>The first creature display using a model (FileDataID), or 0.</summary>
    public int FirstCreatureDisplay(string model)
    {
        DefaultFor(model);
        return _files.IdOf(model) is { } f && _creatureByModel!.TryGetValue(f, out var d) ? d : 0;
    }

    // characters come in M6 (ChrModel, ChrCustomization*): no character looks for another version yet
    public ModelLook Character(int race, int sex, CharacterLook look) => new() { Notes = { "Player characters of other versions come later." } };
    public CharacterOptions Options(int race, int sex) => new();
    public (int Race, int Sex)? RaceOfModel(string path) => null;

    /// <summary>The animations of your 1.12 game (AnimationData), for the names of the same ids; Empty without a game.</summary>
    public static AnimationCatalog Animations112(Mpq.ClientData? client112)
    {
        if (client112 == null) return AnimationCatalog.Empty;
        try
        {
            var data = client112.Read("DBFilesClient\\AnimationData.dbc");
            return data == null ? AnimationCatalog.Empty : AnimationCatalog.FromDbc(new Dbc.DbcFile(data, "AnimationData"));
        }
        catch (Exception e) when (e is Dbc.DbcException or IOException or Mpq.MpqException or ArgumentException) { return AnimationCatalog.Empty; }
    }
}
