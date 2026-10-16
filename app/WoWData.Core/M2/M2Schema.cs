namespace WoWData.Core.M2;

/// <summary>The element type of an M2Array: raw characters, a primitive value (one or more numbers), or a record.</summary>
public sealed class M2Elem
{
    public static readonly M2Elem Char = new("char", 1, null);

    /// <summary>Type code: "char", a primitive ("u1", "u2", "i2", "u4", "i4", "f4", "3f4", "4f4", "9f4", "4u1", "2u4",
    /// "vertex") or the record name.</summary>
    public string Type { get; }
    public int Size { get; }
    public M2Rec? Rec { get; }

    private M2Elem(string type, int size, M2Rec? rec)
    {
        Type = type;
        Size = size;
        Rec = rec;
    }

    public static M2Elem Prim(string type) => new(type, PrimSize(type), null);
    public static M2Elem Of(M2Rec rec) => new(rec.Name, rec.Size, rec);
    public bool IsChar => ReferenceEquals(this, Char);

    /// <summary>Number of scalar components and the size of one (e.g. "3f4" = 3 x 4 bytes).</summary>
    public static (int Count, int Size, char Kind) Shape(string t)
    {
        if (t == "vertex") return (1, 0x30, 'v');
        int i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        int n = i == 0 ? 1 : int.Parse(t[..i]);
        char kind = t[i];
        int size = int.Parse(t[(i + 1)..]);
        return (n, size, kind);
    }

    public static int PrimSize(string t)
    {
        var (n, s, _) = Shape(t);
        return n * s;
    }

    public override string ToString() => Type;
}

public sealed class M2PlainField
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public int Offset { get; init; }
    public int Size { get; init; }
}

/// <summary>An M2Array {uint32 count, uint32 offset} inside a record, in file order. Track sub-arrays are named
/// "&lt;track&gt;.ranges" / ".timestamps" / ".values".</summary>
public sealed class M2ArrayField
{
    public string Name { get; init; } = "";
    public int Offset { get; init; }
    public M2Elem Elem { get; init; } = M2Elem.Char;
    public int Index { get; init; }
}

/// <summary>An M2Track (0x1c bytes, or 0x14 without values): uint16 interpolation, int16 global sequence, then the
/// ranges / timestamps / values arrays.</summary>
public sealed class M2TrackField
{
    public string Name { get; init; } = "";
    public int Offset { get; init; }
    public M2Elem? Value { get; init; }
    public int RangesIndex { get; init; }
    public int TimestampsIndex { get; init; }
    public int ValuesIndex { get; init; } = -1;
}

/// <summary>
/// A fixed-size record layout (port of wowdata/m2/schema.py Rec): plain values, arrays and tracks in file order. The
/// sizes, and which fields are arrays, come from M2Shared_FixupHeader @0071cdf0 and its M2Fixup_* helpers in our
/// decompile; the round trip of every client model proves every byte.
/// </summary>
public sealed class M2Rec
{
    public string Name { get; }
    public int Size { get; }
    public IReadOnlyList<M2PlainField> Plain => _plain;
    public IReadOnlyList<M2ArrayField> Arrays => _arrays;
    public IReadOnlyList<M2TrackField> Tracks => _tracks;

    private readonly List<M2PlainField> _plain = new();
    private readonly List<M2ArrayField> _arrays = new();
    private readonly List<M2TrackField> _tracks = new();
    private readonly Dictionary<string, M2PlainField> _plainByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, M2ArrayField> _arrayByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, M2TrackField> _trackByName = new(StringComparer.Ordinal);

    public M2Rec(string name, int size, params object[] fields)
    {
        Name = name;
        Size = size;
        int pos = 0;
        foreach (var f in fields)
        {
            switch (f)
            {
                case FieldSpec p:
                {
                    var pf = new M2PlainField { Name = p.Name, Type = p.Type, Offset = pos, Size = M2Elem.PrimSize(p.Type) };
                    _plain.Add(pf);
                    _plainByName[pf.Name] = pf;
                    pos += pf.Size;
                    break;
                }
                case ArrSpec a:
                    AddArray(a.Name, a.Elem, pos);
                    pos += 8;
                    break;
                case TrkSpec t:
                {
                    int start = pos;
                    pos += 4;   // interpolation + global sequence
                    int r = AddArray(t.Name + ".ranges", M2Elem.Prim("2u4"), pos);
                    pos += 8;
                    int ts = AddArray(t.Name + ".timestamps", M2Elem.Prim("u4"), pos);
                    pos += 8;
                    int v = -1;
                    if (t.Value != null)
                    {
                        v = AddArray(t.Name + ".values", t.Value, pos);
                        pos += 8;
                    }
                    var tf = new M2TrackField { Name = t.Name, Offset = start, Value = t.Value, RangesIndex = r, TimestampsIndex = ts, ValuesIndex = v };
                    _tracks.Add(tf);
                    _trackByName[t.Name] = tf;
                    break;
                }
                default:
                    throw new ArgumentException("bad field spec");
            }
        }
        if (pos != size) throw new InvalidOperationException($"{name}: fields cover 0x{pos:x} bytes, the record is 0x{size:x}");
    }

    private int AddArray(string name, M2Elem elem, int offset)
    {
        var af = new M2ArrayField { Name = name, Offset = offset, Elem = elem, Index = _arrays.Count };
        _arrays.Add(af);
        _arrayByName[name] = af;
        return af.Index;
    }

    public M2PlainField Field(string name) => _plainByName.TryGetValue(name, out var f) ? f : throw new KeyNotFoundException($"{Name}.{name}");
    public M2ArrayField Array(string name) => _arrayByName.TryGetValue(name, out var f) ? f : throw new KeyNotFoundException($"{Name}.{name}[]");
    public M2TrackField Track(string name) => _trackByName.TryGetValue(name, out var f) ? f : throw new KeyNotFoundException($"{Name}.{name} (track)");
    public bool HasArray(string name) => _arrayByName.ContainsKey(name);
    public bool HasPlain(string name) => _plainByName.ContainsKey(name);
    public override string ToString() => $"{Name} 0x{Size:x}";

    // ---------------------------------------------------------------- field spec helpers
    public sealed record FieldSpec(string Name, string Type);
    public sealed record ArrSpec(string Name, M2Elem Elem);
    public sealed record TrkSpec(string Name, M2Elem? Value);
}

/// <summary>
/// Every record layout of the M2 format as the 1.12 client (build 5875) reads it: version 0x100 (256) and 0x101 (257).
/// Port of wowdata/m2/schema.py (see its docstring for the decompile evidence). Names the decompile does not give come
/// from wowdev.wiki and are marked "(wowdev)" there.
/// </summary>
public static class M2Schema
{
    private static M2Rec.FieldSpec P(string n, string t) => new(n, t);
    private static M2Rec.ArrSpec A(string n, string prim) => new(n, prim == "char" ? M2Elem.Char : M2Elem.Prim(prim));
    private static M2Rec.ArrSpec A(string n, M2Rec rec) => new(n, M2Elem.Of(rec));
    private static M2Rec.TrkSpec T(string n, string? value) => new(n, value == null ? null : M2Elem.Prim(value));

    public const string V3 = "3f4";
    public const string Quat = "4f4";          // x, y, z, w; full floats in 1.12 (not the compressed 8-byte form)
    public const string SplineV3 = "9f4";      // value, in tangent, out tangent
    public const string SplineF = "3f4";

    public static readonly M2Rec Sequence = new("M2Sequence", 0x44,
        P("anim_id", "u2"), P("sub_id", "u2"), P("start", "u4"), P("end", "u4"), P("move_speed", "f4"), P("flags", "u4"),
        P("frequency", "i2"), P("pad_16", "u2"), P("replay", "2u4"), P("blend_time", "u4"), P("bounds_min", V3), P("bounds_max", V3),
        P("bounds_radius", "f4"), P("next_variant", "i2"), P("next_alias", "u2"));

    public static readonly M2Rec Playable = new("M2PlayableAnim", 4, P("fallback_id", "i2"), P("flags", "u2"));

    public static readonly M2Rec Bone = new("M2Bone", 0x6c,
        P("key_bone_id", "i4"), P("flags", "u4"), P("parent", "i2"), P("submesh_id", "u2"),
        T("translation", V3), T("rotation", Quat), T("scale", V3), P("pivot", V3));

    public static readonly M2Rec Submesh = new("M2Submesh", 0x20,
        P("geoset_id", "u2"), P("level", "u2"), P("vertex_start", "u2"), P("vertex_count", "u2"), P("index_start", "u2"),
        P("index_count", "u2"), P("bone_count", "u2"), P("bone_start", "u2"), P("bone_influences", "u2"), P("center_bone", "u2"),
        P("center", V3));

    public static readonly M2Rec TexUnit = new("M2TexUnit", 0x18,
        P("flags", "u1"), P("priority", "i1"), P("shader_id", "u2"), P("submesh", "u2"), P("geoset_index", "u2"), P("color", "i2"),
        P("render_flags", "u2"), P("material_layer", "u2"), P("texture_count", "u2"), P("texture_lookup", "u2"),
        P("tex_unit_lookup", "u2"), P("transparency_lookup", "u2"), P("texture_transform_lookup", "u2"));

    public static readonly M2Rec View = new("M2View", 0x2c,
        A("indices", "u2"), A("triangles", "u2"), A("properties", "4u1"), A("submeshes", Submesh), A("texture_units", TexUnit),
        P("bone_limit", "u4"));

    public static readonly M2Rec Color = new("M2Color", 0x38, T("color", V3), T("alpha", "i2"));
    public static readonly M2Rec Texture = new("M2Texture", 0x10, P("type", "u4"), P("flags", "u4"), A("filename", "char"));
    public static readonly M2Rec Transparency = new("M2TextureWeight", 0x1c, T("weight", "i2"));
    public static readonly M2Rec Flipbook = new("M2Track6c", 0x1c, T("value", "u2"));
    public static readonly M2Rec TextureTransform = new("M2TextureTransform", 0x54, T("translation", V3), T("rotation", Quat), T("scale", V3));
    public static readonly M2Rec RenderFlags = new("M2Material", 4, P("flags", "u2"), P("blend", "u2"));

    public static readonly M2Rec Attachment = new("M2Attachment", 0x30,
        P("id", "u4"), P("bone", "u2"), P("unknown_06", "u2"), P("position", V3), T("visible", "u1"));

    public static readonly M2Rec Event = new("M2Event", 0x2c,
        P("id", "4u1"), P("data", "u4"), P("bone", "u2"), P("unknown_0a", "u2"), P("position", V3), T("enabled", null));

    public static readonly M2Rec Light = new("M2Light", 0xd4,
        P("type", "u2"), P("bone", "i2"), P("position", V3),
        T("ambient_color", V3), T("ambient_intensity", "f4"), T("diffuse_color", V3), T("diffuse_intensity", "f4"),
        T("attenuation_start", "f4"), T("attenuation_end", "f4"), T("visibility", "u1"));

    public static readonly M2Rec Camera = new("M2Camera", 0x7c,
        P("type", "u4"), P("fov", "f4"), P("far_clip", "f4"), P("near_clip", "f4"),
        T("positions", SplineV3), P("position_base", V3), T("target_positions", SplineV3), P("target_position_base", V3),
        T("roll", SplineF));

    public static readonly M2Rec Ribbon = new("M2Ribbon", 0xdc,
        P("id", "u4"), P("bone", "u4"), P("position", V3), A("textures", "u2"), A("materials", "u2"),
        T("color", V3), T("alpha", "i2"), T("height_above", "f4"), T("height_below", "f4"),
        P("edges_per_second", "f4"), P("edge_lifetime", "f4"), P("gravity", "f4"), P("texture_rows", "u2"), P("texture_cols", "u2"),
        T("texture_slot", "u2"), T("visibility", "u1"));

    public static readonly M2Rec Particle = new("M2Particle", 0x1f8,
        P("id", "u4"), P("flags", "u4"), P("position", V3), P("bone", "u2"), P("texture", "u2"),
        A("geometry_model", "char"), A("recursion_model", "char"),
        P("blend", "u2"), P("emitter_type", "u2"), P("particle_type", "u1"), P("head_or_tail", "u1"), P("texture_tile_rotation", "i2"),
        P("texture_rows", "u2"), P("texture_cols", "u2"),
        T("emission_speed", "f4"), T("speed_variation", "f4"), T("vertical_range", "f4"), T("horizontal_range", "f4"),
        T("gravity", "f4"), T("lifespan", "f4"), T("emission_rate", "f4"), T("emission_area_length", "f4"),
        T("emission_area_width", "f4"), T("z_source", "f4"),
        P("mid_point", "f4"), P("colors", "3u4"), P("scales", "3f4"), P("lifespan_uv_anim", "3u2"),
        P("decay_uv_anim", "3u2"), P("tail_uv_anim", "2i2"), P("tail_decay_uv_anim", "2i2"),
        P("params_17c", "22f4"),
        A("spline_points", V3), T("enabled", "u1"));

    private static object[] HeaderFields() => new object[]
    {
        P("magic", "4u1"), P("version", "u4"), A("name", "char"), P("global_flags", "u4"),
        A("global_sequences", "u4"), A("sequences", Sequence), A("animation_lookup", "i2"), A("playable_lookup", Playable),
        A("bones", Bone), A("key_bone_lookup", "i2"), A("vertices", "vertex"), A("views", View),
        A("colors", Color), A("textures", Texture), A("transparency", Transparency), A("track_6c", Flipbook),
        A("texture_transforms", TextureTransform), A("replaceable_lookup", "i2"), A("render_flags", RenderFlags),
        A("bone_lookup", "u2"), A("texture_lookup", "u2"), A("tex_unit_lookup", "i2"), A("transparency_lookup", "u2"),
        A("texture_transform_lookup", "i2"),
        P("bounds_min", V3), P("bounds_max", V3), P("bounds_radius", "f4"),
        P("collision_min", V3), P("collision_max", V3), P("collision_radius", "f4"),
        A("collision_triangles", "u2"), A("collision_vertices", V3), A("collision_normals", V3),
        A("attachments", Attachment), A("attachment_lookup", "i2"), A("events", Event), A("lights", Light),
        A("cameras", Camera), A("camera_lookup", "i2"), A("ribbons", Ribbon), A("particles", Particle),
    };

    public static readonly M2Rec Header256 = new("M2Header", 0x144, HeaderFields());

    /// <summary>Version 0x101: two more uint16 arrays (texCombineLookup2 at +0x148, read by M2Scene_SetBatchTextureStages).</summary>
    public static readonly M2Rec Header257 = new("M2Header257", 0x154,
        HeaderFields().Concat(new object[] { A("texture_combiner_combos", "u2"), A("unknown_14c", "u2") }).ToArray());

    public static readonly IReadOnlyDictionary<int, string> TextureTypes = new Dictionary<int, string>
    {
        [0] = "file", [1] = "body (character skin)", [2] = "object skin (item, cape)", [6] = "hair", [8] = "fur / skin extra",
        [11] = "creature skin 1", [12] = "creature skin 2", [13] = "creature skin 3",
    };

    public static readonly string[] BlendModes = { "opaque", "alpha key", "alpha", "no-alpha add", "add", "mod", "mod2x" };

    public static readonly IReadOnlyDictionary<int, string> RenderFlagBits = new Dictionary<int, string>
    {
        [0x1] = "unlit", [0x2] = "unfogged", [0x4] = "two-sided", [0x8] = "no depth test", [0x10] = "no depth write",
    };

    /// <summary>Key bones (wowdev; the client resolves them through key_bone_lookup).</summary>
    public static readonly string[] KeyBones =
    {
        "ArmL", "ArmR", "ShoulderL", "ShoulderR", "SpineLow", "Waist", "Head", "Jaw", "IndexFingerR", "MiddleFingerR",
        "PinkyFingerR", "RingFingerR", "ThumbR", "IndexFingerL", "MiddleFingerL", "PinkyFingerL", "RingFingerL",
        "ThumbL", "$BTH", "$CSR", "$CSL", "_Breath", "_Name", "_NameMount", "$CHD", "$CCH", "Root",
    };

    /// <summary>Attachment ids (wowdev; ids above 36 do not occur in 1.12 models).</summary>
    public static readonly string[] AttachmentNames =
    {
        "Shield", "HandRight", "HandLeft", "ElbowRight", "ElbowLeft", "ShoulderRight", "ShoulderLeft", "KneeRight", "KneeLeft",
        "HipRight", "HipLeft", "Helm", "Back", "ShoulderFlapRight", "ShoulderFlapLeft", "ChestBloodFront", "ChestBloodBack",
        "Breath", "PlayerName", "Base", "Head", "SpellLeftHand", "SpellRightHand", "Special1", "Special2", "Special3",
        "SheathMainHand", "SheathOffHand", "SheathShield", "PlayerNameMounted", "LargeWeaponLeft", "LargeWeaponRight",
        "HipWeaponLeft", "HipWeaponRight", "Chest", "HandArrow", "Bullet",
    };
}
