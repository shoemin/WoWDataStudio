namespace WoWData.Core.Dbc;

// Typed 1.12 (build 5875) layouts, the ones the index needs. Field positions follow wowdata/dbc.py (checked against
// the files and the record structs of our decompile, names/structs.csv). The spell tables were located on the
// files themselves (Spell 133 "Fireball": SpellVisual[2] at fields 115-116 = visual 67, SpellIconID 117, name at 120, rank at 129;
// visual 67 -> precast kit 30 "Fire Precast Hand", cast kit 38 "Fire Cast Hand", missile model 365).

public sealed record CreatureDisplayInfoRec(int Id, int ModelId, int ExtraId, float Scale, string[] Skin);
public sealed record CreatureModelDataRec(int Id, string ModelPath);
public sealed record CreatureDisplayInfoExtraRec(int Id, int Race, int Sex, int[] Equipment, string Bake)
{
    /// <summary>Fields 3-7: skin colour, face, hair style, hair colour, facial hair (the NPC's character look).</summary>
    public int Skin { get; init; }
    public int Face { get; init; }
    public int HairStyle { get; init; }
    public int HairColor { get; init; }
    public int FacialHair { get; init; }
}
public sealed record GameObjectDisplayInfoRec(int Id, string ModelPath);
public sealed record ItemDisplayInfoRec(int Id, string[] Model, string[] ModelTexture, string Icon, string[] Texture);
public sealed record CharSectionsRec(int Id, int Race, int Sex, int Section, int Variation, int Color, string[] Texture, int Flags);
public sealed record ChrRacesRec(int Id, int MaleDisplayId, int FemaleDisplayId, string ClientPrefix, string ClientFileString, string Name);
public sealed record MapRec(int Id, string Directory, string Name);
public sealed record SpellVisualEffectNameRec(int Id, string Name, string FileName);
/// <summary>A spell visual: its kits (in the order of <see cref="KitColumns"/> / <see cref="KitRoles"/>), the missile model and
/// the model of a persistent area (0 = none).</summary>
public sealed record SpellVisualRec(int Id, int[] Kits, int MissileModel)
{
    public static readonly string[] KitColumns = { "PrecastKit", "CastKit", "ImpactKit", "StateKit", "ChannelKit", "AreaKit" };
    /// <summary>Where a kit's effects appear, in words (the "Used in game" roles).</summary>
    public static readonly string[] KitRoles = { "precast", "cast", "impact", "aura", "channel", "area" };
    public bool HasMissile { get; init; }
    public int AreaModel { get; init; }
}
public sealed record SpellVisualKitRec(int Id, int[] Effects);
public sealed record SpellRec(int Id, string Name, string Rank, int[] Visuals);
public sealed record AnimationDataRec(int Id, string Name);
public sealed record CharHairGeosetsRec(int Id, int Race, int Sex, int Variation, int Geoset, int ShowScalp);
/// <summary>CharacterFacialHairStyles (9 fields): race, sex, variation, three unused fields, then the geoset values of
/// groups 1, 3 and 2 (CCharacterComponent_SetFacialHair @00478660: field 6 + 100, field 7 + 300, field 8 + 200).</summary>
public sealed record CharFacialHairRec(int Race, int Sex, int Variation, int Geoset100, int Geoset300, int Geoset200);

public static class DbcTables
{
    private static string[] Strings(DbcFile d, int row, int col, int n)
    {
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = d.String(row, col + i);
        return a;
    }

    private static int[] Ints(DbcFile d, int row, int col, int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = d.Int(row, col + i);
        return a;
    }

    public static List<CreatureDisplayInfoRec> CreatureDisplayInfo(DbcFile d)
    {
        d.RequireFields(12);
        var l = new List<CreatureDisplayInfoRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 3), d.Float(r, 4), Strings(d, r, 6, 3)));
        return l;
    }

    public static List<CreatureModelDataRec> CreatureModelData(DbcFile d)
    {
        d.RequireFields(16);
        var l = new List<CreatureModelDataRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.String(r, 2)));
        return l;
    }

    public static List<CreatureDisplayInfoExtraRec> CreatureDisplayInfoExtra(DbcFile d)
    {
        d.RequireFields(19);
        var l = new List<CreatureDisplayInfoExtraRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 2), Ints(d, r, 8, 10), d.String(r, 18))
            {
                Skin = d.Int(r, 3), Face = d.Int(r, 4), HairStyle = d.Int(r, 5), HairColor = d.Int(r, 6), FacialHair = d.Int(r, 7),
            });
        return l;
    }

    public static List<GameObjectDisplayInfoRec> GameObjectDisplayInfo(DbcFile d)
    {
        d.RequireFields(12);
        var l = new List<GameObjectDisplayInfoRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.String(r, 1)));
        return l;
    }

    /// <summary>Region textures (Texture[8]): arm upper, arm lower, hand, torso upper, torso lower, leg upper, leg lower, foot.</summary>
    public static List<ItemDisplayInfoRec> ItemDisplayInfo(DbcFile d)
    {
        d.RequireFields(23);
        var l = new List<ItemDisplayInfoRec>(d.Count);
        // id, model[2], model_texture[2], icon, geoset_group[3], flags, spell_visual_id, group_sound_index,
        // helmet_geoset_vis[2], texture[8], item_visual
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), Strings(d, r, 1, 2), Strings(d, r, 3, 2), d.String(r, 5), Strings(d, r, 14, 8)));
        return l;
    }

    /// <summary>Sections: 0 skin, 1 face, 2 facial hair, 3 hair, 4 underwear.</summary>
    public static List<CharSectionsRec> CharSections(DbcFile d)
    {
        d.RequireFields(10);
        var l = new List<CharSectionsRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 2), d.Int(r, 3), d.Int(r, 4), d.Int(r, 5), Strings(d, r, 6, 3), d.Int(r, 9)));
        return l;
    }

    public static List<ChrRacesRec> ChrRaces(DbcFile d)
    {
        d.RequireFields(29);
        var l = new List<ChrRacesRec>(d.Count);
        // id, flags, faction, exploration sound, male display (4), female display (5), client prefix (6), mount scale,
        // base language, creature type, login effect, combat stun, res sickness, splash sound, starting taxi nodes,
        // client file string (15), cinematic sequence, name (17, localized)
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.Int(r, 4), d.Int(r, 5), d.String(r, 6), d.String(r, 15), d.LocString(r, 17)));
        return l;
    }

    public static List<MapRec> Map(DbcFile d)
    {
        d.RequireFields(5);
        var l = new List<MapRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.String(r, 1), d.LocString(r, 4)));
        return l;
    }

    public static List<SpellVisualEffectNameRec> SpellVisualEffectName(DbcFile d)
    {
        d.RequireFields(3);
        var l = new List<SpellVisualEffectNameRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.String(r, 1), d.String(r, 2)));
        return l;
    }

    /// <summary>SpellVisual (16 fields). The field positions are read from the layout file (dbc_layouts_5875.txt), whose
    /// meanings come from the client's code (decompile SpellVisualRec): 1 precast kit, 2 cast kit, 3 impact kit, 4 state
    /// (aura active) kit, 5 channel kit, 6 has missile, 7 missile model, 11 has area effect, 12 area model, 13 area kit (a
    /// persistent area); 14 is a sound and 15 flags (0.7.0: before, 13-15 were all read as kits, so flag values 1 and 2 were
    /// taken for kits 1 and 2 and sound IDs for kits).</summary>
    public static List<SpellVisualRec> SpellVisual(DbcFile d)
    {
        d.RequireFields(16);
        var layout = DbcLayouts.Default.Get("SpellVisual")!;
        int F(string name) => layout.Column(name)?.Field ?? throw new DbcException($"SpellVisual layout has no column {name}");
        var kitFields = SpellVisualRec.KitColumns.Select(F).ToArray();
        int hasMissile = F("HasMissile"), missile = F("MissileModel"), hasArea = F("HasAreaEffect"), area = F("AreaModel");
        var l = new List<SpellVisualRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
        {
            var kits = kitFields.Select(f => d.Int(r, f)).ToArray();
            l.Add(new(d.Int(r, 0), kits, d.Int(r, missile)) { HasMissile = d.Int(r, hasMissile) != 0, AreaModel = d.Int(r, hasArea) != 0 ? d.Int(r, area) : 0 });
        }
        return l;
    }

    /// <summary>SpellVisualKit: fields 3-12 are the effect slots (head, chest, base, left hand, right hand, breath, weapons, specials; kit 30 = "Fire Precast Hand" in both hand slots), each a
    /// SpellVisualEffectName id or -1/0. Only ids that exist in SpellVisualEffectName are used by the index.</summary>
    public static List<SpellVisualKitRec> SpellVisualKit(DbcFile d)
    {
        d.RequireFields(13);
        var l = new List<SpellVisualKitRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), Ints(d, r, 3, 10)));
        return l;
    }

    public static List<SpellRec> Spell(DbcFile d)
    {
        d.RequireFields(130);
        var l = new List<SpellRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.LocString(r, 120), d.LocString(r, 129), new[] { d.Int(r, 115), d.Int(r, 116) }));
        return l;
    }

    public static List<AnimationDataRec> AnimationData(DbcFile d)
    {
        d.RequireFields(2);
        var l = new List<AnimationDataRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.String(r, 1)));
        return l;
    }

    public static List<CharHairGeosetsRec> CharHairGeosets(DbcFile d)
    {
        d.RequireFields(6);
        var l = new List<CharHairGeosetsRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 2), d.Int(r, 3), d.Int(r, 4), d.Int(r, 5)));
        return l;
    }

    public static List<CharFacialHairRec> CharacterFacialHairStyles(DbcFile d)
    {
        d.RequireFields(9);
        var l = new List<CharFacialHairRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 2), d.Int(r, 6), d.Int(r, 7), d.Int(r, 8)));
        return l;
    }
}
