using System.Collections.Concurrent;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.Mpq;

namespace WoWData.Core.M2;

/// <summary>A texture for one replaceable slot (or a model's own file): pixels plus where they came from.</summary>
public sealed class ResolvedTexture
{
    public RgbaImage Image { get; }
    /// <summary>Human-readable description ("composite of OrcMaleSkin00_00.blp, ...").</summary>
    public string Source { get; }
    /// <summary>The archive path when the texture is one file (null for composites).</summary>
    public string? Path { get; }
    /// <summary>The files it was made of (one for a plain texture, several for the character body composite).</summary>
    public IReadOnlyList<string> Parts { get; }

    public ResolvedTexture(RgbaImage image, string source, string? path, IReadOnlyList<string>? parts = null)
    {
        Image = image;
        Source = source;
        Path = path;
        Parts = parts ?? (path != null ? new[] { path } : System.Array.Empty<string>());
    }
}

/// <summary>The character-creation choices (CharSections variations / colours, CharacterFacialHairStyles variation).</summary>
public sealed record CharacterLook(int Skin = 0, int Face = 0, int HairStyle = 0, int HairColor = 0, int FacialHair = 0);

/// <summary>What character creation offers for a race and sex.</summary>
public sealed class CharacterOptions
{
    public List<int> SkinColors { get; } = new();
    public Dictionary<int, List<int>> FacesBySkin { get; } = new();
    public List<int> HairStyles { get; } = new();
    public Dictionary<int, List<int>> HairColorsByStyle { get; } = new();
    public List<int> FacialHairs { get; } = new();
}

/// <summary>The resolved textures (by M2 texture type) and visible geosets of one variant of a model.</summary>
public sealed class ModelLook
{
    public Dictionary<int, ResolvedTexture> Slots { get; } = new();
    /// <summary>Null = every geoset visible; otherwise the visible geoset ids.</summary>
    public HashSet<int>? Geosets { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Model targets -> the textures of the replaceable slots and the visible geosets (port of wowdata/modelctx.py):
///  * creature skins 11/12/13 = CreatureDisplayInfo.TextureVariation[0..2] (&lt;model folder&gt;\&lt;name&gt;.blp);
///  * 1 = character body: player models get a skin composited from CharSections like CCharacterComponent_UpdateTextures
///    (base skin, face lower/upper, facial hair lower/upper, the scalp textures of the hair style, underwear), humanoid
///    NPCs their baked texture (CreatureDisplayInfoExtra.bake);
///  * 6 = hair (CharSections section 3 texture 0), 8 = skin extra / fur (section 0 texture 1);
///  * 2 = object skin: ItemDisplayInfo.ModelTexture for item models.
/// Character geosets follow CCharacterComponent_ApplyGeosets @00477520 with no items worn: geoset 0, the group defaults
/// {1, 101, 201, 301, 401, 501, 601, 702, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501} (CharCreate defaults), the hair
/// geoset from CharHairGeosets (SetHairStyle; 1 when the style has none) and the facial hair geosets from
/// CharacterFacialHairStyles (SetFacialHair: +100 / +300 / +200).
/// Textures are read through <see cref="ReadFile"/> (the app passes "your edit if you have one, else the game's").
/// </summary>
public sealed class ModelResolver
{
    // CharComponent_InitRegionRects @00475c50: x, y, width, height of the 10 regions of the 256x256 body texture
    public static readonly IReadOnlyDictionary<string, (int X, int Y, int W, int H)> Regions = new Dictionary<string, (int, int, int, int)>
    {
        ["ArmUpper"] = (0, 0, 128, 64), ["ArmLower"] = (0, 64, 128, 64), ["Hand"] = (0, 128, 128, 32),
        ["TorsoUpper"] = (128, 0, 128, 64), ["TorsoLower"] = (128, 64, 128, 32), ["LegUpper"] = (128, 96, 128, 64),
        ["LegLower"] = (128, 160, 128, 64), ["Foot"] = (128, 224, 128, 32), ["FaceUpper"] = (0, 160, 128, 32),
        ["FaceLower"] = (0, 192, 128, 64),
    };

    public static readonly int[] DefaultGroupGeosets = { 1, 101, 201, 301, 401, 501, 601, 702, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501 };

    public Catalog Catalog { get; }
    public Func<string, byte[]?> ReadFile { get; }

    private readonly ConcurrentDictionary<string, RgbaImage?> _blp = new(StringComparer.OrdinalIgnoreCase);
    private List<CharSectionsRec>? _sections;
    private List<CharHairGeosetsRec>? _hairGeosets;
    private List<CharFacialHairRec>? _facialHair;
    private List<ChrRacesRec>? _races;
    private Dictionary<int, string>? _animNames;
    private readonly object _gate = new();

    public ModelResolver(Catalog catalog, Func<string, byte[]?>? readFile = null)
    {
        Catalog = catalog;
        ReadFile = readFile ?? catalog.Client.Read;
    }

    /// <summary>Forget decoded textures (after a texture was edited or reverted).</summary>
    public void ClearTextureCache() => _blp.Clear();

    public void Forget(string path) => _blp.TryRemove(path, out _);

    public RgbaImage? LoadBlp(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return _blp.GetOrAdd(path, p =>
        {
            try
            {
                var data = ReadFile(p);
                return data == null ? null : BlpFile.Parse(data).Decode(0);
            }
            catch (Exception e) when (e is BlpException or IOException or MpqException or ArgumentException or IndexOutOfRangeException)
            {
                return null;
            }
        });
    }

    // ---------------------------------------------------------------- tables
    private List<CharSectionsRec> Sections { get { lock (_gate) return _sections ??= DbcTables.CharSections(Catalog.Table("CharSections")); } }
    private List<CharHairGeosetsRec> HairGeosets { get { lock (_gate) return _hairGeosets ??= DbcTables.CharHairGeosets(Catalog.Table("CharHairGeosets")); } }
    private List<CharFacialHairRec> FacialHair { get { lock (_gate) return _facialHair ??= DbcTables.CharacterFacialHairStyles(Catalog.Table("CharacterFacialHairStyles")); } }
    public List<ChrRacesRec> Races { get { lock (_gate) return _races ??= DbcTables.ChrRaces(Catalog.Table("ChrRaces")); } }

    /// <summary>AnimationData id -> name (Stand, Walk, Attack1H...).</summary>
    public IReadOnlyDictionary<int, string> AnimationNames
    {
        get
        {
            lock (_gate)
            {
                if (_animNames == null)
                {
                    try { _animNames = DbcTables.AnimationData(Catalog.Table("AnimationData")).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last().Name); }
                    catch (DbcException) { _animNames = new(); }
                }
                return _animNames;
            }
        }
    }

    public string AnimationName(int id) => AnimationNames.TryGetValue(id, out var n) && n.Length > 0 ? n : $"Anim{id}";

    private AnimationCatalog? _animations;

    /// <summary>0.10.0: AnimationData with the flags and fallbacks the client uses (your edited table first, like the
    /// names), for the playable-animation table and the animation checks.</summary>
    public AnimationCatalog Animations
    {
        get
        {
            lock (_gate)
            {
                if (_animations == null)
                {
                    try { _animations = AnimationCatalog.FromDbc(Catalog.Table("AnimationData")); }
                    catch (DbcException) { _animations = AnimationCatalog.Empty; }
                }
                return _animations;
            }
        }
    }

    public List<CharSectionsRec> CharSections(int race, int sex) => Sections.Where(r => r.Race == race && r.Sex == sex).ToList();

    /// <summary>(race id, sex) for Character\&lt;Race&gt;\&lt;Male|Female&gt;\*.m2, else null.</summary>
    public (int Race, int Sex)? RaceOfModel(string path)
    {
        var parts = ClientData.ResolveName(path).Split('\\');
        if (parts.Length < 4 || !parts[0].Equals("character", StringComparison.OrdinalIgnoreCase)) return null;
        int sex = parts[2].ToLowerInvariant() switch { "male" => 0, "female" => 1, _ => -1 };
        if (sex < 0) return null;
        foreach (var r in Races)
            if (r.ClientFileString.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) return (r.Id, sex);
        return null;
    }

    public CharacterOptions Options(int race, int sex)
    {
        var o = new CharacterOptions();
        var rows = CharSections(race, sex);
        o.SkinColors.AddRange(rows.Where(r => r.Section == 0 && r.Variation == 0).Select(r => r.Color).Distinct().OrderBy(x => x));
        foreach (var g in rows.Where(r => r.Section == 1).GroupBy(r => r.Color))
            o.FacesBySkin[g.Key] = g.Select(r => r.Variation).Distinct().OrderBy(x => x).ToList();
        var styles = HairGeosets.Where(h => h.Race == race && h.Sex == sex).Select(h => h.Variation)
            .Concat(rows.Where(r => r.Section == 3).Select(r => r.Variation)).Distinct().OrderBy(x => x).ToList();
        o.HairStyles.AddRange(styles);
        foreach (var s in styles)
        {
            var colors = rows.Where(r => r.Section == 3 && r.Variation == s).Select(r => r.Color).Distinct().OrderBy(x => x).ToList();
            if (colors.Count == 0) colors = rows.Where(r => r.Section == 3).Select(r => r.Color).Distinct().OrderBy(x => x).ToList();
            o.HairColorsByStyle[s] = colors;
        }
        o.FacialHairs.AddRange(FacialHair.Where(f => f.Race == race && f.Sex == sex).Select(f => f.Variation).Distinct().OrderBy(x => x));
        return o;
    }

    // ---------------------------------------------------------------- characters

    private static void Paste(RgbaImage dst, RgbaImage src, (int X, int Y, int W, int H) region)
    {
        double scale = dst.Height / 256.0;
        int x = (int)(region.X * scale), y = (int)(region.Y * scale), w = (int)(region.W * scale), h = (int)(region.H * scale);
        if (src.Width != w || src.Height != h) src = ImageOps.Resize(src, w, h);
        var d = dst.Pixels;
        var s = src.Pixels;
        for (int j = 0; j < h && y + j < dst.Height; j++)
            for (int i = 0; i < w && x + i < dst.Width; i++)
            {
                int so = (j * w + i) * 4, dO = dst.Offset(x + i, y + j);
                float a = s[so + 3] / 255f;
                for (int c = 0; c < 3; c++) d[dO + c] = (byte)(s[so + c] * a + d[dO + c] * (1 - a) + 0.5f);
            }
    }

    /// <summary>Slots 1 (body composite), 6 (hair) and 8 (skin extra) for a player model with the given choices.</summary>
    public (Dictionary<int, ResolvedTexture> Slots, List<string> Notes) CharacterTextures(int race, int sex, CharacterLook look)
    {
        var rows = CharSections(race, sex);
        CharSectionsRec? Row(int section, int variation, int color) =>
            rows.FirstOrDefault(r => r.Section == section && r.Variation == variation && r.Color == color)
            ?? rows.FirstOrDefault(r => r.Section == section && r.Variation == variation);

        var outSlots = new Dictionary<int, ResolvedTexture>();
        var notes = new List<string>();
        var baseRow = Row(0, 0, look.Skin);
        if (baseRow == null || baseRow.Texture[0].Length == 0)
        {
            notes.Add($"no CharSections skin row for race {race} sex {sex}");
            return (outSlots, notes);
        }
        var baseImg = LoadBlp(baseRow.Texture[0]);
        if (baseImg == null)
        {
            notes.Add($"skin texture {baseRow.Texture[0]} missing");
            return (outSlots, notes);
        }
        var body = baseImg.Clone();
        var used = new List<string> { baseRow.Texture[0] };
        var layers = new List<(string Path, string Region)>();
        var face = Row(1, look.Face, look.Skin);
        if (face != null) layers.AddRange(new[] { (face.Texture[0], "FaceLower"), (face.Texture[1], "FaceUpper") });
        var fh = Row(2, look.FacialHair, look.HairColor);
        if (fh != null) layers.AddRange(new[] { (fh.Texture[0], "FaceLower"), (fh.Texture[1], "FaceUpper") });
        var hair = Row(3, look.HairStyle, look.HairColor);
        if (hair != null) layers.AddRange(new[] { (hair.Texture[1], "FaceLower"), (hair.Texture[2], "FaceUpper") });
        var uw = Row(4, 0, look.Skin);
        if (uw != null) layers.AddRange(new[] { (uw.Texture[0], "LegUpper"), (uw.Texture[1], "TorsoUpper") });
        foreach (var (path, region) in layers)
        {
            if (path.Length == 0) continue;
            var img = LoadBlp(path);
            if (img == null) continue;
            Paste(body, img, Regions[region]);
            used.Add(path);
        }
        outSlots[1] = new ResolvedTexture(body, "composite of " + string.Join(", ", used.Select(System.IO.Path.GetFileName)), null, used.Select(Catalog.Canonical).ToList());
        string? hairTex = hair != null && hair.Texture[0].Length > 0 ? hair.Texture[0] : null;
        if (hairTex == null)   // bald style: take the first style of this colour so other hair geosets still look right
            hairTex = rows.FirstOrDefault(r => r.Section == 3 && r.Color == look.HairColor && r.Texture[0].Length > 0)?.Texture[0];
        if (hairTex != null && LoadBlp(hairTex) is { } hi) outSlots[6] = new ResolvedTexture(hi, hairTex, Catalog.Canonical(hairTex));
        if (baseRow.Texture[1].Length > 0 && LoadBlp(baseRow.Texture[1]) is { } ei)
            outSlots[8] = new ResolvedTexture(ei, baseRow.Texture[1], Catalog.Canonical(baseRow.Texture[1]));
        notes.Add($"character look: skin {look.Skin}, face {look.Face}, hair style {look.HairStyle} colour {look.HairColor}, facial hair {look.FacialHair}");
        return (outSlots, notes);
    }

    /// <summary>The hair geoset of a style (CCharacterComponent_GetHairGeoset @00478540: 1 when none or not positive).</summary>
    public int HairGeoset(int race, int sex, int style)
    {
        var r = HairGeosets.FirstOrDefault(h => h.Race == race && h.Sex == sex && h.Variation == style);
        return r != null && r.Geoset > 0 ? r.Geoset : 1;
    }

    /// <summary>Visible geosets with no items worn (see the class summary).</summary>
    public HashSet<int> CharacterGeosets(int race, int sex, int hairStyle, int facialHair)
    {
        var ids = (int[])DefaultGroupGeosets.Clone();
        ids[0] = HairGeoset(race, sex, hairStyle);
        var fh = FacialHair.FirstOrDefault(f => f.Race == race && f.Sex == sex && f.Variation == facialHair);
        if (fh != null)
        {
            ids[1] = fh.Geoset100 + 100;
            ids[3] = fh.Geoset300 + 300;
            ids[2] = fh.Geoset200 + 200;
        }
        var set = new HashSet<int>(ids) { 0 };
        return set;
    }

    public ModelLook Character(int race, int sex, CharacterLook look)
    {
        var l = new ModelLook();
        var (slots, notes) = CharacterTextures(race, sex, look);
        foreach (var kv in slots) l.Slots[kv.Key] = kv.Value;
        l.Notes.AddRange(notes);
        l.Geosets = CharacterGeosets(race, sex, look.HairStyle, look.FacialHair);
        return l;
    }

    // ---------------------------------------------------------------- creatures, NPCs, items

    /// <summary>Creature skins (slots 11-13) from the display's texture variations.</summary>
    public ModelLook CreatureSkins(string model, IReadOnlyList<string> skins)
    {
        var l = new ModelLook();
        int bs = model.LastIndexOf('\\');
        var folder = bs >= 0 ? model[..bs] : "";
        for (int i = 0; i < skins.Count && i < 3; i++)
        {
            if (string.IsNullOrEmpty(skins[i])) continue;
            var p = Catalog.Canonical(folder + "\\" + skins[i] + ".blp");
            var img = LoadBlp(p);
            if (img != null) l.Slots[11 + i] = new ResolvedTexture(img, p, p);
            else l.Notes.Add($"skin {p} not found");
        }
        return l;
    }

    /// <summary>A creature display: its skins, or for a humanoid NPC (CreatureDisplayInfoExtra) the baked body texture,
    /// hair from CharSections and the NPC's hair / facial-hair geosets.</summary>
    public ModelLook CreatureDisplay(int displayId)
    {
        var chain = Catalog.CreatureDisplay(displayId);
        if (chain?.Model == null) return new ModelLook { Notes = { $"display {displayId} has no model" } };
        var cdi = Catalog.CreatureDisplays[displayId];
        var l = CreatureSkins(chain.Model, cdi.Skin);
        if (chain.Extra is { } ex)
        {
            var (slots, _) = CharacterTextures(ex.Race, ex.Sex, new CharacterLook(ex.Skin, ex.Face, ex.HairStyle, ex.HairColor, ex.FacialHair));
            foreach (var kv in slots) l.Slots[kv.Key] = kv.Value;
            if (ex.Bake.Length > 0)
            {
                var p = Catalog.Canonical("Textures\\BakedNpcTextures\\" + ex.Bake);
                var img = LoadBlp(p);
                if (img != null) l.Slots[1] = new ResolvedTexture(img, "baked NPC texture " + p, p);
            }
            l.Geosets = CharacterGeosets(ex.Race, ex.Sex, ex.HairStyle, ex.FacialHair);
            l.Notes.Add("humanoid NPC: baked body texture, hair and facial hair from the display; worn equipment is not shown");
        }
        else if (RaceOfModel(chain.Model) is { } rs)
        {
            var c = Character(rs.Race, rs.Sex, new CharacterLook());
            foreach (var kv in c.Slots) l.Slots[kv.Key] = kv.Value;
            l.Geosets = c.Geosets;
        }
        return l;
    }

    /// <summary>An item display's model texture for slot 2 (object skin).</summary>
    public ModelLook ItemDisplay(int displayId, string? model = null)
    {
        var l = new ModelLook();
        var files = Catalog.ItemDisplay(displayId);
        if (files == null) return l;
        string tag = "1";
        if (model != null)
        {
            var m = files.FirstOrDefault(f => f.Role.StartsWith("model", StringComparison.Ordinal) && !f.Role.StartsWith("model texture", StringComparison.Ordinal) &&
                                              string.Equals(f.Path, model, StringComparison.OrdinalIgnoreCase));
            if (m != null && m.Role.Length > 5 && char.IsDigit(m.Role[5])) tag = m.Role[5].ToString();
        }
        var tex = files.FirstOrDefault(f => f.Role == "model texture" + tag && f.Archive != null) ?? files.FirstOrDefault(f => f.Role.StartsWith("model texture", StringComparison.Ordinal) && f.Archive != null);
        if (tex != null && LoadBlp(tex.Path) is { } img) l.Slots[2] = new ResolvedTexture(img, tex.Path, tex.Path);
        return l;
    }

    /// <summary>The default look of a model file: a character's default skin, the first creature display's skins, or
    /// nothing (the model's own textures only).</summary>
    public ModelLook DefaultFor(string model)
    {
        if (RaceOfModel(model) is { } rs) return Character(rs.Race, rs.Sex, new CharacterLook());
        var first = Catalog.CreatureDisplays.Values.Where(d => Catalog.CreatureModels.TryGetValue(d.ModelId, out var cm) &&
                string.Equals(Catalog.Canonical(cm.ModelPath), model, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Id).FirstOrDefault();
        if (first != null) return CreatureDisplay(first.Id);
        if (model.StartsWith("Item\\", StringComparison.OrdinalIgnoreCase))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(model);
            var item = Catalog.ItemDisplays.Values.OrderBy(d => d.Id).FirstOrDefault(d =>
                d.Model.Any(m => m.Length > 0 && string.Equals(System.IO.Path.GetFileNameWithoutExtension(m), stem, StringComparison.OrdinalIgnoreCase)));
            if (item != null) return ItemDisplay(item.Id, model);
        }
        return new ModelLook();
    }
}
