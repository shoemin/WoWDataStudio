using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.M2.Modern;

/// <summary>
/// The 1.12-shaped <see cref="M2File"/> of a modern model (the part of <see cref="M2ModernReader"/> that converts):
/// every array the viewer reads, in the version 256 record layouts of <see cref="M2Schema"/>, with the per-sequence
/// tracks put onto one timeline. Built in memory only (it is never written to a file). Views come from the .skin files
/// and are passed to <see cref="M2Model"/> beside it.
/// </summary>
internal sealed class Converter
{
    // the MD20 header of version 264 and later (offsets in the MD21 chunk's data)
    private const int HName = 0x08, HFlags = 0x10, HGlobal = 0x14, HSeq = 0x1C, HSeqLookup = 0x24, HBones = 0x2C, HKeyBones = 0x34,
        HVerts = 0x3C, HColors = 0x48, HTextures = 0x50, HWeights = 0x58, HTexTransforms = 0x60, HReplace = 0x68, HMaterials = 0x70,
        HBoneLookup = 0x78, HTexLookup = 0x80, HUnitLookup = 0x88, HWeightLookup = 0x90, HTransformLookup = 0x98, HBounds = 0xA0,
        HRadius = 0xB8, HCollBox = 0xBC, HCollRadius = 0xD4, HCollTris = 0xD8, HCollVerts = 0xE0, HCollNormals = 0xE8,
        HAttach = 0xF0, HAttachLookup = 0xF8, HEvents = 0x100, HLights = 0x108, HCameras = 0x110, HRibbons = 0x120, HParticles = 0x128;
    public const int HeaderSize = 0x130;

    private const int SeqSize = 0x40, BoneSize = 0x58, ColorSize = 0x28, WeightSize = 0x14, TransformSize = 0x3C, AttachSize = 0x28, EventSize = 0x24;
    public const uint FlagInline = 0x20, FlagAlias = 0x40;

    private readonly M2ModernReader.Buf _b;
    private readonly M2ModernInfo _info;
    private uint[] _start = System.Array.Empty<uint>(), _end = System.Array.Empty<uint>();
    private bool[] _external = System.Array.Empty<bool>();
    private int[] _alias = System.Array.Empty<int>();
    private int _unreadable;

    public Converter(M2ModernReader.Buf b, M2ModernInfo info)
    {
        _b = b;
        _info = info;
    }

    public M2File Build(Func<uint, string> nameOf, uint[] txid, byte[] texUnitLookup)
    {
        if (_b.Length < HeaderSize) throw new M2Exception($"the MD20 header is shorter than 0x{HeaderSize:x} bytes");
        var h = new RecData(M2Schema.Header256, 1);
        Encoding.ASCII.GetBytes("MD20").CopyTo(h.Bytes, 0);
        h.U32(0, "version", _info.Version);
        h.Array(0, "name", h.Prim("name", Raw(HName, 1, "name")));
        h.U32(0, "global_flags", _b.U32(HFlags));
        h.Array(0, "global_sequences", h.Prim("global_sequences", Raw(HGlobal, 4, "global sequences")));
        h.Array(0, "sequences", Sequences());
        h.Array(0, "animation_lookup", h.Prim("animation_lookup", Raw(HSeqLookup, 2, "sequence lookup")));
        h.Array(0, "bones", Bones());
        h.Array(0, "key_bone_lookup", h.Prim("key_bone_lookup", Raw(HKeyBones, 2, "key bone lookup")));
        h.Array(0, "vertices", h.Prim("vertices", Raw(HVerts, 0x30, "vertices")));
        h.Array(0, "colors", Colors());
        h.Array(0, "textures", Textures(nameOf, txid));
        h.Array(0, "transparency", Weights());
        h.Array(0, "texture_transforms", TextureTransforms());
        h.Array(0, "replaceable_lookup", h.Prim("replaceable_lookup", Raw(HReplace, 2, "replaceable texture lookup")));
        h.Array(0, "render_flags", h.Prim("render_flags", Materials()));
        h.Array(0, "bone_lookup", h.Prim("bone_lookup", Raw(HBoneLookup, 2, "bone lookup")));
        h.Array(0, "texture_lookup", h.Prim("texture_lookup", Raw(HTexLookup, 2, "texture lookup")));
        h.Array(0, "tex_unit_lookup", h.Prim("tex_unit_lookup", texUnitLookup));   // from the shader ids (M2ModernReader.TextureLayers)
        h.Array(0, "transparency_lookup", h.Prim("transparency_lookup", Raw(HWeightLookup, 2, "transparency lookup")));
        h.Array(0, "texture_transform_lookup", h.Prim("texture_transform_lookup", Raw(HTransformLookup, 2, "texture transform lookup")));
        h.Raw(0, "bounds_min", _b.B.AsSpan(HBounds, 12));
        h.Raw(0, "bounds_max", _b.B.AsSpan(HBounds + 12, 12));
        h.Raw(0, "bounds_radius", _b.B.AsSpan(HRadius, 4));
        h.Raw(0, "collision_min", _b.B.AsSpan(HCollBox, 12));
        h.Raw(0, "collision_max", _b.B.AsSpan(HCollBox + 12, 12));
        h.Raw(0, "collision_radius", _b.B.AsSpan(HCollRadius, 4));
        h.Array(0, "collision_triangles", h.Prim("collision_triangles", Raw(HCollTris, 2, "collision triangles")));
        h.Array(0, "collision_vertices", h.Prim("collision_vertices", Raw(HCollVerts, 12, "collision vertices")));
        h.Array(0, "collision_normals", h.Prim("collision_normals", Raw(HCollNormals, 12, "collision normals")));
        h.Array(0, "attachments", Attachments());
        h.Array(0, "attachment_lookup", h.Prim("attachment_lookup", Raw(HAttachLookup, 2, "attachment lookup")));
        h.Array(0, "events", Events());
        uint lights = _b.U32(HLights), cameras = _b.U32(HCameras), ribbons = _b.U32(HRibbons), particles = _b.U32(HParticles);
        if (particles > 0) _info.NotShown.Add($"{particles} particle emitter{(particles == 1 ? "" : "s")}: changed records, later (M3)");
        if (ribbons > 0) _info.NotShown.Add($"{ribbons} ribbon{(ribbons == 1 ? "" : "s")}: changed records, later (M3)");
        if (lights > 0) _info.NotShown.Add($"{lights} light{(lights == 1 ? "" : "s")}: not drawn by the viewer");
        if (cameras > 0) _info.NotShown.Add($"{cameras} camera{(cameras == 1 ? "" : "s")}: not used by the viewer");
        if (_zeroQuats > 0) _info.Approximated.Add($"{_zeroQuats} texture rotation key{(_zeroQuats == 1 ? "" : "s")} of zero length read as no rotation");
        if (_unreadable > 0) _info.Notes.Add($"{_unreadable} key array{(_unreadable == 1 ? "" : "s")} of the animations point outside the model and were left out");
        return M2File.FromHeader(h.ToData());
    }

    private byte[] Raw(int at, int elemSize, string what) => _b.Block(at, elemSize, what).ToArray();

    // ------------------------------------------------------------------ sequences and the timeline

    private M2Data Sequences()
    {
        var src = _b.Block(HSeq, SeqSize, "sequences");
        int n = src.Length / SeqSize;
        var d = new RecData(M2Schema.Sequence, n);
        _start = new uint[n];
        _end = new uint[n];
        _external = new bool[n];
        _alias = new int[n];
        var duration = new uint[n];
        var flags = new uint[n];
        var next = new int[n];
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * SeqSize, SeqSize);
            duration[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
            flags[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[0x0C..]);
            next[i] = BinaryPrimitives.ReadUInt16LittleEndian(s[0x3E..]);
        }
        // aliases (flag 0x40) play their target's keys: follow the chain to a sequence that is not an alias
        for (int i = 0; i < n; i++)
        {
            _alias[i] = -1;
            if ((flags[i] & FlagAlias) == 0) continue;
            int t = next[i];
            for (int guard = 0; guard < 32 && t >= 0 && t < n && (flags[t] & FlagAlias) != 0; guard++) t = next[t];
            if (t >= 0 && t < n && (flags[t] & FlagAlias) == 0 && t != i) _alias[i] = t;
        }
        uint cursor = 0;
        for (int i = 0; i < n; i++)
        {
            if (_alias[i] >= 0) continue;
            _start[i] = cursor;
            _end[i] = cursor + duration[i];
            cursor = _end[i] + 1;
            _external[i] = (flags[i] & FlagInline) == 0;
        }
        for (int i = 0; i < n; i++)
        {
            if (_alias[i] < 0) continue;
            _start[i] = _start[_alias[i]];
            _end[i] = _end[_alias[i]];
        }
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * SeqSize, SeqSize);
            d.U16(i, "anim_id", BinaryPrimitives.ReadUInt16LittleEndian(s));
            d.U16(i, "sub_id", BinaryPrimitives.ReadUInt16LittleEndian(s[2..]));
            d.U32(i, "start", _start[i]);
            d.U32(i, "end", _end[i]);
            d.Raw(i, "move_speed", s.Slice(8, 4));
            d.U32(i, "flags", flags[i]);
            d.Raw(i, "frequency", s.Slice(0x10, 2));
            d.Raw(i, "replay", s.Slice(0x14, 8));
            d.U32(i, "blend_time", BinaryPrimitives.ReadUInt16LittleEndian(s[0x1C..]));
            d.Raw(i, "bounds_min", s.Slice(0x20, 12));
            d.Raw(i, "bounds_max", s.Slice(0x2C, 12));
            d.Raw(i, "bounds_radius", s.Slice(0x38, 4));
            d.Raw(i, "next_variant", s.Slice(0x3C, 2));
            d.U16(i, "next_alias", (ushort)next[i]);
            if (_alias[i] >= 0) _info.AliasSequences++;
            else if (_external[i]) _info.ExternalSequences++;
            else _info.InlineSequences++;
        }
        if (_info.ExternalSequences > 0)
            _info.NotShown.Add($"{_info.ExternalSequences} animation{(_info.ExternalSequences == 1 ? "" : "s")} kept in external .anim files: listed, not played yet (M3)");
        return d.ToData();
    }

    /// <summary>
    /// A modern track (interpolation, global sequence, then {count, offset} of the per-sequence timestamp arrays and,
    /// when <paramref name="srcSize"/> &gt; 0, of the per-sequence value arrays) converted to the 1.12 track of element
    /// <paramref name="i"/> of <paramref name="dst"/>: keys concatenated on the timeline, ranges per sequence.
    /// </summary>
    private void Track(RecData dst, int i, string name, int at, int srcSize, Func<byte[], byte[]>? convert, bool padEnd)
    {
        int interp = _b.U16(at);
        int gseq = _b.I16(at + 2);
        uint tn = _b.U32(at + 4), to = _b.U32(at + 8);
        uint vn = srcSize > 0 ? _b.U32(at + 12) : 0, vo = srcSize > 0 ? _b.U32(at + 16) : 0;
        var tf = dst.Rec.Track(name);
        var rangesElem = dst.Rec.Arrays[tf.RangesIndex].Elem;
        var timesElem = dst.Rec.Arrays[tf.TimestampsIndex].Elem;
        var valuesElem = tf.ValuesIndex >= 0 ? dst.Rec.Arrays[tf.ValuesIndex].Elem : null;
        if (!_b.Fits(tn, to, 8) || (srcSize > 0 && !_b.Fits(vn, vo, 8)))
        {
            _unreadable++;
            dst.Track(i, name, interp, gseq, M2Data.Empty(rangesElem), M2Data.Empty(timesElem), valuesElem == null ? null : M2Data.Empty(valuesElem));
            return;
        }
        int outer = (int)(srcSize > 0 ? Math.Min(tn, vn) : tn);

        // one sequence's keys: (times, value bytes in the 1.12 form), or null (no keys, external or unreadable)
        (uint[] Times, byte[] Values)? Keys(int k)
        {
            if (k >= outer) return null;
            if (gseq < 0 && k < _external.Length && _external[k] && _alias[k] < 0) return null;
            uint n = _b.U32((int)to + 8 * k), o = _b.U32((int)to + 8 * k + 4);
            if (n == 0) return null;
            if (!_b.Fits(n, o, 4)) { _unreadable++; return null; }
            uint m = n, vo2 = 0;
            if (srcSize > 0)
            {
                m = _b.U32((int)vo + 8 * k);
                vo2 = _b.U32((int)vo + 8 * k + 4);
                if (!_b.Fits(m, vo2, srcSize)) { _unreadable++; return null; }
            }
            int count = (int)Math.Min(n, m);
            if (count == 0) return null;
            var times = new uint[count];
            for (int j = 0; j < count; j++) times[j] = _b.U32((int)o + 4 * j);
            byte[] values = System.Array.Empty<byte>();
            if (srcSize > 0)
            {
                var raw = _b.B.AsSpan((int)vo2, count * srcSize);
                values = convert != null ? convert(raw.ToArray()) : raw.ToArray();
            }
            return (times, values);
        }

        var allTimes = new List<uint>();
        var allValues = new MemoryStream();
        var ranges = new List<(uint, uint)>();
        if (gseq >= 0)
        {
            // a global-sequence track runs on its own clock: its keys as they are, no ranges
            for (int k = 0; k < outer; k++)
                if (Keys(k) is { } g)
                {
                    allTimes.AddRange(g.Times);
                    allValues.Write(g.Values);
                    break;
                }
        }
        else
        {
            int nSeq = _start.Length;
            var r = new (uint, uint)[nSeq];
            int dstSize = valuesElem?.Size ?? 0;
            for (int s = 0; s < nSeq; s++)
            {
                r[s] = (uint.MaxValue, uint.MaxValue);
                if (_alias[s] >= 0) continue;
                if (Keys(s) is not { } g) continue;
                uint first = (uint)allTimes.Count;
                foreach (var t in g.Times) allTimes.Add(_start[s] + t);
                allValues.Write(g.Values);
                if (padEnd && g.Times.Length > 0 && _start[s] + g.Times[^1] < _end[s] && dstSize > 0)
                {
                    // hold the last value until the sequence ends (instead of leaning towards the next sequence's keys)
                    allTimes.Add(_end[s]);
                    allValues.Write(g.Values, g.Values.Length - dstSize, dstSize);
                }
                r[s] = (first, (uint)allTimes.Count - 1);
            }
            for (int s = 0; s < nSeq; s++)
                if (_alias[s] >= 0) r[s] = r[_alias[s]];
            if (allTimes.Count > 0) ranges.AddRange(r);
        }
        var timesBytes = new byte[allTimes.Count * 4];
        for (int j = 0; j < allTimes.Count; j++) BinaryPrimitives.WriteUInt32LittleEndian(timesBytes.AsSpan(j * 4), allTimes[j]);
        var rangeBytes = new byte[ranges.Count * 8];
        for (int j = 0; j < ranges.Count; j++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rangeBytes.AsSpan(j * 8), ranges[j].Item1);
            BinaryPrimitives.WriteUInt32LittleEndian(rangeBytes.AsSpan(j * 8 + 4), ranges[j].Item2);
        }
        dst.Track(i, name, interp, gseq, RecData.Prim(rangesElem, rangeBytes), RecData.Prim(timesElem, timesBytes),
            valuesElem == null ? null : RecData.Prim(valuesElem, allValues.ToArray()));
    }

    private static byte[] DecompressQuats(byte[] raw)
    {
        int n = raw.Length / 8;
        var res = new byte[n * 16];
        for (int k = 0; k < n; k++)
            for (int c = 0; c < 4; c++)
                BinaryPrimitives.WriteSingleLittleEndian(res.AsSpan(k * 16 + c * 4), M2ModernReader.DecompressQuat(BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(k * 8 + c * 2))));
        return res;
    }

    private int _zeroQuats;

    /// <summary>Texture rotations (full-float quaternions): a key of (almost) zero length would collapse the texture;
    /// it is read as no rotation (MEASURED: one 1.60 creature model has such keys).</summary>
    private byte[] ZeroQuatsAsIdentity(byte[] raw)
    {
        for (int k = 0; k + 16 <= raw.Length; k += 16)
        {
            float x = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k)), y = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 4)),
                z = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 8)), w = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 12));
            if (x * x + y * y + z * z + w * w > 1e-6f) continue;
            System.Array.Clear(raw, k, 12);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(k + 12), 1f);
            _zeroQuats++;
        }
        return raw;
    }

    // ------------------------------------------------------------------ records with tracks

    private M2Data Bones()
    {
        var src = _b.Block(HBones, BoneSize, "bones");
        uint off = _b.U32(HBones + 4);
        int n = src.Length / BoneSize;
        var d = new RecData(M2Schema.Bone, n);
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * BoneSize, BoneSize);
            int at = (int)off + i * BoneSize;
            d.Raw(i, "key_bone_id", s[..4]);
            d.Raw(i, "flags", s.Slice(4, 4));
            d.Raw(i, "parent", s.Slice(8, 2));
            d.Raw(i, "submesh_id", s.Slice(10, 2));
            Track(d, i, "translation", at + 0x10, 12, null, true);
            Track(d, i, "rotation", at + 0x24, 8, DecompressQuats, true);
            Track(d, i, "scale", at + 0x38, 12, null, true);
            d.Raw(i, "pivot", s.Slice(0x4C, 12));
        }
        return d.ToData();
    }

    private M2Data Colors()
    {
        var src = _b.Block(HColors, ColorSize, "colors");
        uint off = _b.U32(HColors + 4);
        int n = src.Length / ColorSize;
        var d = new RecData(M2Schema.Color, n);
        for (int i = 0; i < n; i++)
        {
            int at = (int)off + i * ColorSize;
            Track(d, i, "color", at, 12, null, true);
            Track(d, i, "alpha", at + 0x14, 2, null, true);
        }
        return d.ToData();
    }

    private M2Data Weights()
    {
        var src = _b.Block(HWeights, WeightSize, "texture weights");
        uint off = _b.U32(HWeights + 4);
        int n = src.Length / WeightSize;
        var d = new RecData(M2Schema.Transparency, n);
        for (int i = 0; i < n; i++) Track(d, i, "weight", (int)off + i * WeightSize, 2, null, true);
        return d.ToData();
    }

    private M2Data TextureTransforms()
    {
        var src = _b.Block(HTexTransforms, TransformSize, "texture transforms");
        uint off = _b.U32(HTexTransforms + 4);
        int n = src.Length / TransformSize;
        var d = new RecData(M2Schema.TextureTransform, n);
        for (int i = 0; i < n; i++)
        {
            int at = (int)off + i * TransformSize;
            Track(d, i, "translation", at, 12, null, true);
            Track(d, i, "rotation", at + 0x14, 16, ZeroQuatsAsIdentity, true);
            Track(d, i, "scale", at + 0x28, 12, null, true);
        }
        return d.ToData();
    }

    private M2Data Attachments()
    {
        var src = _b.Block(HAttach, AttachSize, "attachments");
        uint off = _b.U32(HAttach + 4);
        int n = src.Length / AttachSize;
        var d = new RecData(M2Schema.Attachment, n);
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * AttachSize, AttachSize);
            d.Raw(i, "id", s[..4]);
            d.Raw(i, "bone", s.Slice(4, 2));
            d.Raw(i, "unknown_06", s.Slice(6, 2));
            d.Raw(i, "position", s.Slice(8, 12));
            Track(d, i, "visible", (int)off + i * AttachSize + 0x14, 1, null, true);
        }
        return d.ToData();
    }

    private M2Data Events()
    {
        var src = _b.Block(HEvents, EventSize, "events");
        uint off = _b.U32(HEvents + 4);
        int n = src.Length / EventSize;
        var d = new RecData(M2Schema.Event, n);
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * EventSize, EventSize);
            d.Raw(i, "id", s[..4]);
            d.Raw(i, "data", s.Slice(4, 4));
            d.Raw(i, "bone", s.Slice(8, 2));
            d.Raw(i, "position", s.Slice(0x0C, 12));
            Track(d, i, "enabled", (int)off + i * EventSize + 0x18, 0, null, false);
        }
        return d.ToData();
    }

    // ------------------------------------------------------------------ textures and materials

    private M2Data Textures(Func<uint, string> nameOf, uint[] txid)
    {
        var src = _b.Block(HTextures, 0x10, "textures");
        int n = src.Length / 0x10;
        var d = new RecData(M2Schema.Texture, n);
        var charElem = M2Schema.Texture.Array("filename").Elem;
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * 0x10, 0x10);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(s);
            d.U32(i, "type", type);
            d.Raw(i, "flags", s.Slice(4, 4));
            string name = "";
            if (type == 0)
            {
                if (i < txid.Length && txid[i] != 0) name = nameOf(txid[i]);
                else
                {
                    uint ln = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]), lo = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                    if (ln > 0 && _b.Fits(ln, lo, 1)) name = Encoding.Latin1.GetString(_b.B, (int)lo, (int)ln).TrimEnd('\0');
                }
            }
            if (name.Length > 0) d.Array(i, "filename", RecData.Prim(charElem, Encoding.Latin1.GetBytes(name + "\0")));
        }
        return d.ToData();
    }

    /// <summary>The materials (flags, blend mode). Blend modes 1.12 does not have are drawn as the nearest one.</summary>
    private byte[] Materials()
    {
        var raw = _b.Block(HMaterials, 4, "materials").ToArray();
        var odd = new Dictionary<int, int>();
        for (int i = 0; i + 4 <= raw.Length; i += 4)
        {
            int blend = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i + 2));
            if (blend <= 6) continue;
            odd[blend] = odd.GetValueOrDefault(blend) + 1;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i + 2), NearestBlend(blend));
        }
        foreach (var (blend, count) in odd.OrderBy(x => x.Key))
            _info.Approximated.Add($"blend mode {blend} drawn as {M2Schema.BlendModes[NearestBlend(blend)]} ({count} material{(count == 1 ? "" : "s")})");
        return raw;
    }

    /// <summary>The 1.12 blend mode closest to a modern one (7 = blend-add: additive).</summary>
    public static ushort NearestBlend(int blend) => blend switch { <= 6 => (ushort)blend, 7 => 4, _ => 2 };
}

/// <summary>Writes one record array of the 1.12 schema in memory: plain values, arrays, tracks.</summary>
internal sealed class RecData
{
    public M2Rec Rec { get; }
    public int Count { get; }
    public byte[] Bytes { get; }
    private readonly M2Data[][] _sub;

    public RecData(M2Rec rec, int count)
    {
        Rec = rec;
        Count = count;
        Bytes = new byte[count * rec.Size];
        _sub = new M2Data[count][];
        for (int i = 0; i < count; i++) _sub[i] = rec.Arrays.Select(a => M2Data.Empty(a.Elem)).ToArray();
    }

    private int At(int i, string field) => i * Rec.Size + Rec.Field(field).Offset;

    public void U16(int i, string f, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(At(i, f)), v);
    public void U32(int i, string f, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(At(i, f)), v);

    /// <summary>Copies bytes into a field (at most the field's size).</summary>
    public void Raw(int i, string f, ReadOnlySpan<byte> v)
    {
        var field = Rec.Field(f);
        v[..Math.Min(v.Length, field.Size)].CopyTo(Bytes.AsSpan(i * Rec.Size + field.Offset));
    }

    public void Array(int i, string name, M2Data d) => _sub[i][Rec.Array(name).Index] = d;

    public void Track(int i, string name, int interp, int gseq, M2Data ranges, M2Data times, M2Data? values)
    {
        var t = Rec.Track(name);
        int o = i * Rec.Size + t.Offset;
        BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(o), (ushort)interp);
        BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(o + 2), (short)gseq);
        _sub[i][t.RangesIndex] = ranges;
        _sub[i][t.TimestampsIndex] = times;
        if (t.ValuesIndex >= 0 && values != null) _sub[i][t.ValuesIndex] = values;
    }

    /// <summary>A primitive (or plain record) array of this record's field <paramref name="name"/>.</summary>
    public M2Data Prim(string name, byte[] bytes) => Prim(Rec.Array(name).Elem, bytes);

    public static M2Data Prim(M2Elem elem, byte[] bytes) =>
        bytes.Length < elem.Size ? M2Data.Empty(elem) : new M2Data(elem, bytes.Length / elem.Size, bytes.Length % elem.Size == 0 ? bytes : bytes[..(bytes.Length / elem.Size * elem.Size)], null, -1);

    public M2Data ToData() => Count == 0 ? M2Data.Empty(M2Elem.Of(Rec)) : new M2Data(M2Elem.Of(Rec), Count, Bytes, _sub, -1);
}
