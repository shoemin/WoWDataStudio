using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>An animation track decoded for evaluation: interpolation, global sequence, per-sequence key ranges
/// ({first, last}, inclusive), timestamps (ms on the model's global timeline) and values.</summary>
public sealed class M2Track<T> where T : struct
{
    public static readonly M2Track<T> Empty = new(0, -1, System.Array.Empty<(uint, uint)>(), System.Array.Empty<uint>(), System.Array.Empty<T>());

    public int Interpolation { get; }
    public int GlobalSequence { get; }
    public (uint First, uint Last)[] Ranges { get; }
    public uint[] Times { get; }
    public T[] Values { get; }

    public M2Track(int interpolation, int globalSequence, (uint, uint)[] ranges, uint[] times, T[] values)
    {
        Interpolation = interpolation;
        GlobalSequence = globalSequence;
        Ranges = ranges;
        Times = times;
        Values = values;
    }

    public bool HasKeys => Times.Length > 0 && Values.Length > 0;
}

public struct M2Vertex
{
    public Vector3 Position;
    public byte W0, W1, W2, W3;
    public byte B0, B1, B2, B3;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector2 Uv2;
}

public sealed class M2Sequence
{
    public int AnimId, SubId;
    public uint Start, End;
    public float MoveSpeed;
    public uint Flags;
    public short Frequency;
    public uint BlendTime;
    public Vector3 BoundsMin, BoundsMax;
    public float BoundsRadius;
    public short NextVariant;
    public int NextAlias;
    public uint Duration => End >= Start ? End - Start : 0;
}

public sealed class M2Bone
{
    public int KeyBoneId;
    public uint Flags;
    public short Parent;
    public int SubmeshId;
    public M2Track<Vector3> Translation = M2Track<Vector3>.Empty;
    public M2Track<Quaternion> Rotation = M2Track<Quaternion>.Empty;
    public M2Track<Vector3> Scale = M2Track<Vector3>.Empty;
    public Vector3 Pivot;
    public bool IsAnimated => Translation.HasKeys || Rotation.HasKeys || Scale.HasKeys;
}

public sealed class M2Submesh
{
    public int GeosetId, Level, VertexStart, VertexCount, IndexStart, IndexCount, BoneCount, BoneStart, BoneInfluences, CenterBone;
    public Vector3 Center;
}

public sealed class M2TexUnit
{
    public int Flags, Priority, ShaderId, Submesh, GeosetIndex, Color, RenderFlags, MaterialLayer, TextureCount, TextureLookup,
        TexUnitLookup, TransparencyLookup, TextureTransformLookup;
}

public sealed class M2View
{
    public ushort[] Indices = System.Array.Empty<ushort>();
    public ushort[] Triangles = System.Array.Empty<ushort>();
    public byte[] Properties = System.Array.Empty<byte>();
    public M2Submesh[] Submeshes = System.Array.Empty<M2Submesh>();
    public M2TexUnit[] TexUnits = System.Array.Empty<M2TexUnit>();
    public uint BoneLimit;
}

public sealed class M2Color
{
    public M2Track<Vector3> Color = M2Track<Vector3>.Empty;
    public M2Track<short> Alpha = M2Track<short>.Empty;
}

public sealed class M2TextureDef
{
    public int Type;
    public uint Flags;
    public string FileName = "";
    public bool WrapU => (Flags & 1) != 0;
    public bool WrapV => (Flags & 2) != 0;
}

public sealed class M2TextureTransform
{
    public M2Track<Vector3> Translation = M2Track<Vector3>.Empty;
    public M2Track<Quaternion> Rotation = M2Track<Quaternion>.Empty;
    public M2Track<Vector3> Scale = M2Track<Vector3>.Empty;
}

public sealed class M2Attachment
{
    public int Id, Bone;
    public Vector3 Position;
}

public readonly record struct M2RenderFlags(int Flags, int Blend)
{
    public bool Unlit => (Flags & 0x1) != 0;
    public bool Unfogged => (Flags & 0x2) != 0;
    public bool TwoSided => (Flags & 0x4) != 0;
    public bool NoDepthTest => (Flags & 0x8) != 0;
    public bool NoDepthWrite => (Flags & 0x10) != 0;
}

/// <summary>
/// A parsed model with typed access to what the viewer and the tools use (vertices, views, bones and their tracks,
/// sequences, textures, render flags, lookups, colours, transparency, texture transforms, attachments). Everything
/// else stays in <see cref="File"/> (the full structure, which also re-serialises the model).
/// </summary>
public sealed class M2Model
{
    public M2File File { get; }
    public string Name { get; }
    public uint Version { get; }
    public uint GlobalFlags { get; }
    public uint[] GlobalSequences { get; }
    public M2Sequence[] Sequences { get; }
    public short[] AnimationLookup { get; }
    public M2Bone[] Bones { get; }
    public short[] KeyBoneLookup { get; }
    public M2Vertex[] Vertices { get; }
    public M2View[] Views { get; }
    public M2Color[] Colors { get; }
    public M2TextureDef[] Textures { get; }
    public M2Track<short>[] Transparency { get; }
    public M2TextureTransform[] TextureTransforms { get; }
    public short[] ReplaceableLookup { get; }
    public M2RenderFlags[] RenderFlags { get; }
    public ushort[] BoneLookup { get; }
    public ushort[] TextureLookup { get; }
    public short[] TexUnitLookup { get; }
    public ushort[] TransparencyLookup { get; }
    public short[] TextureTransformLookup { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public float BoundsRadius { get; }
    public M2Attachment[] Attachments { get; }
    public int EventCount => File["events"].Count;
    public int LightCount => File["lights"].Count;
    public int CameraCount => File["cameras"].Count;
    public int RibbonCount => File["ribbons"].Count;
    public int ParticleCount => File["particles"].Count;

    private M2Model(M2File f)
    {
        File = f;
        var h = f.Header;
        Name = f.ModelName;
        Version = f.Version;
        GlobalFlags = h.U32(0, "global_flags");
        GlobalSequences = f["global_sequences"].AsU32();
        Sequences = ReadSequences(f["sequences"]);
        AnimationLookup = f["animation_lookup"].AsI16();
        Bones = ReadBones(f["bones"]);
        KeyBoneLookup = f["key_bone_lookup"].AsI16();
        Vertices = ReadVertices(f["vertices"]);
        Views = ReadViews(f["views"]);
        var colors = f["colors"];
        Colors = new M2Color[colors.Count];
        for (int i = 0; i < colors.Count; i++)
            Colors[i] = new M2Color { Color = Vec3Track(colors.Track(i, "color")), Alpha = I16Track(colors.Track(i, "alpha")) };
        var tex = f["textures"];
        Textures = new M2TextureDef[tex.Count];
        for (int i = 0; i < tex.Count; i++)
            Textures[i] = new M2TextureDef { Type = (int)tex.U32(i, "type"), Flags = tex.U32(i, "flags"), FileName = tex.Child(i, "filename").AsString() };
        var tr = f["transparency"];
        Transparency = new M2Track<short>[tr.Count];
        for (int i = 0; i < tr.Count; i++) Transparency[i] = I16Track(tr.Track(i, "weight"));
        var tt = f["texture_transforms"];
        TextureTransforms = new M2TextureTransform[tt.Count];
        for (int i = 0; i < tt.Count; i++)
            TextureTransforms[i] = new M2TextureTransform
            {
                Translation = Vec3Track(tt.Track(i, "translation")), Rotation = QuatTrack(tt.Track(i, "rotation")), Scale = Vec3Track(tt.Track(i, "scale")),
            };
        ReplaceableLookup = f["replaceable_lookup"].AsI16();
        var rf = f["render_flags"];
        RenderFlags = new M2RenderFlags[rf.Count];
        for (int i = 0; i < rf.Count; i++) RenderFlags[i] = new M2RenderFlags(rf.U16(i, "flags"), rf.U16(i, "blend"));
        BoneLookup = f["bone_lookup"].AsU16();
        TextureLookup = f["texture_lookup"].AsU16();
        TexUnitLookup = f["tex_unit_lookup"].AsI16();
        TransparencyLookup = f["transparency_lookup"].AsU16();
        TextureTransformLookup = f["texture_transform_lookup"].AsI16();
        BoundsMin = h.Vec3(0, "bounds_min");
        BoundsMax = h.Vec3(0, "bounds_max");
        BoundsRadius = h.F32(0, "bounds_radius");
        var att = f["attachments"];
        Attachments = new M2Attachment[att.Count];
        for (int i = 0; i < att.Count; i++)
            Attachments[i] = new M2Attachment { Id = (int)att.U32(i, "id"), Bone = att.U16(i, "bone"), Position = att.Vec3(i, "position") };
    }

    public static M2Model Load(byte[] data) => new(M2File.Parse(data));
    public static M2Model FromFile(M2File f) => new(f);

    /// <summary>M2 (other versions): set when the model was read by <see cref="Modern.M2ModernReader"/> from a chunked
    /// (MD21) file: its chunks, its skin and texture files, what was left out or drawn approximately. Null for 1.12.</summary>
    public WoWData.Core.M2.Modern.M2ModernInfo? Modern { get; }

    private M2Model(M2File f, M2View[] views, WoWData.Core.M2.Modern.M2ModernInfo modern) : this(f)
    {
        Views = views;
        Modern = modern;
    }

    /// <summary>A model read by the modern reader: its 1.12-shaped structure and the views of its .skin files.</summary>
    public static M2Model FromModern(M2File f, M2View[] views, WoWData.Core.M2.Modern.M2ModernInfo info) => new(f, views, info);

    public string TextureName(int i) => Textures[i].FileName;

    /// <summary>The render flags of a texture unit (opaque when the index is out of range).</summary>
    public M2RenderFlags FlagsOf(M2TexUnit tu) => tu.RenderFlags < RenderFlags.Length ? RenderFlags[tu.RenderFlags] : default;

    // ---------------------------------------------------------------- decoding

    private static M2Sequence[] ReadSequences(M2Data d)
    {
        var a = new M2Sequence[d.Count];
        for (int i = 0; i < d.Count; i++)
            a[i] = new M2Sequence
            {
                AnimId = d.U16(i, "anim_id"), SubId = d.U16(i, "sub_id"), Start = d.U32(i, "start"), End = d.U32(i, "end"),
                MoveSpeed = d.F32(i, "move_speed"), Flags = d.U32(i, "flags"), Frequency = d.I16(i, "frequency"), BlendTime = d.U32(i, "blend_time"),
                BoundsMin = d.Vec3(i, "bounds_min"), BoundsMax = d.Vec3(i, "bounds_max"), BoundsRadius = d.F32(i, "bounds_radius"),
                NextVariant = d.I16(i, "next_variant"), NextAlias = d.U16(i, "next_alias"),
            };
        return a;
    }

    private static M2Bone[] ReadBones(M2Data d)
    {
        var a = new M2Bone[d.Count];
        for (int i = 0; i < d.Count; i++)
            a[i] = new M2Bone
            {
                KeyBoneId = d.I32(i, "key_bone_id"), Flags = d.U32(i, "flags"), Parent = d.I16(i, "parent"), SubmeshId = d.U16(i, "submesh_id"),
                Translation = Vec3Track(d.Track(i, "translation")), Rotation = QuatTrack(d.Track(i, "rotation")), Scale = Vec3Track(d.Track(i, "scale")),
                Pivot = d.Vec3(i, "pivot"),
            };
        return a;
    }

    private static M2Vertex[] ReadVertices(M2Data d)
    {
        var a = new M2Vertex[d.Count];
        var b = d.Bytes.AsSpan();
        for (int i = 0; i < d.Count; i++)
        {
            var s = b.Slice(i * 0x30, 0x30);
            ref var v = ref a[i];
            v.Position = new Vector3(F(s, 0), F(s, 4), F(s, 8));
            v.W0 = s[12]; v.W1 = s[13]; v.W2 = s[14]; v.W3 = s[15];
            v.B0 = s[16]; v.B1 = s[17]; v.B2 = s[18]; v.B3 = s[19];
            v.Normal = new Vector3(F(s, 20), F(s, 24), F(s, 28));
            v.Uv = new Vector2(F(s, 32), F(s, 36));
            v.Uv2 = new Vector2(F(s, 40), F(s, 44));
        }
        return a;
    }

    private static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);

    private static M2View[] ReadViews(M2Data d)
    {
        var a = new M2View[d.Count];
        for (int i = 0; i < d.Count; i++)
        {
            var sub = d.Child(i, "submeshes");
            var tus = d.Child(i, "texture_units");
            var v = new M2View
            {
                Indices = d.Child(i, "indices").AsU16(),
                Triangles = d.Child(i, "triangles").AsU16(),
                Properties = d.Child(i, "properties").Bytes,
                BoneLimit = d.U32(i, "bone_limit"),
                Submeshes = new M2Submesh[sub.Count],
                TexUnits = new M2TexUnit[tus.Count],
            };
            for (int k = 0; k < sub.Count; k++)
                v.Submeshes[k] = new M2Submesh
                {
                    GeosetId = sub.U16(k, "geoset_id"), Level = sub.U16(k, "level"), VertexStart = sub.U16(k, "vertex_start"), VertexCount = sub.U16(k, "vertex_count"),
                    IndexStart = sub.U16(k, "index_start"), IndexCount = sub.U16(k, "index_count"), BoneCount = sub.U16(k, "bone_count"),
                    BoneStart = sub.U16(k, "bone_start"), BoneInfluences = sub.U16(k, "bone_influences"), CenterBone = sub.U16(k, "center_bone"),
                    Center = sub.Vec3(k, "center"),
                };
            for (int k = 0; k < tus.Count; k++)
                v.TexUnits[k] = new M2TexUnit
                {
                    Flags = tus.U8(k, "flags"), Priority = tus.I8(k, "priority"), ShaderId = tus.U16(k, "shader_id"), Submesh = tus.U16(k, "submesh"),
                    GeosetIndex = tus.U16(k, "geoset_index"), Color = tus.I16(k, "color"), RenderFlags = tus.U16(k, "render_flags"),
                    MaterialLayer = tus.U16(k, "material_layer"), TextureCount = tus.U16(k, "texture_count"), TextureLookup = tus.U16(k, "texture_lookup"),
                    TexUnitLookup = tus.U16(k, "tex_unit_lookup"), TransparencyLookup = tus.U16(k, "transparency_lookup"),
                    TextureTransformLookup = tus.U16(k, "texture_transform_lookup"),
                };
            a[i] = v;
        }
        return a;
    }

    private static M2Track<T> Track<T>(M2TrackView t, Func<byte[], int, T> read, int size) where T : struct
    {
        var vals = t.Values;
        if (t.KeyCount == 0 && t.Ranges.Count == 0 && (vals == null || vals.Count == 0)) return new M2Track<T>(t.Interpolation, t.GlobalSequence, System.Array.Empty<(uint, uint)>(), System.Array.Empty<uint>(), System.Array.Empty<T>());
        var v = new T[vals?.Count ?? 0];
        for (int i = 0; i < v.Length; i++) v[i] = read(vals!.Bytes, i * size);
        return new M2Track<T>(t.Interpolation, t.GlobalSequence, t.RangePairs(), t.Timestamps.AsU32(), v);
    }

    private static M2Track<Vector3> Vec3Track(M2TrackView t) => Track(t, (b, o) =>
        new Vector3(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 8))), 12);

    private static M2Track<Quaternion> QuatTrack(M2TrackView t) => Track(t, (b, o) =>
        new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o + 12))), 16);

    private static M2Track<short> I16Track(M2TrackView t) => Track(t, (b, o) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)), 2);
}
