using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.M2.Modern;

/// <summary>Which part of a modern model a track header lives in, and so which part of an .anim file holds its external
/// keys: the model itself (a raw .anim file, or its AFM2 chunk), the skeleton's bones (AFSB) or attachments (AFSA).</summary>
internal enum KeyOwner { Model = 0, SkelBones = 1, SkelAttachments = 2 }

/// <summary>A modern track header (interpolation, global sequence, per-sequence timestamp and value arrays) at
/// <paramref name="At"/> in <paramref name="B"/>.</summary>
internal readonly record struct TrackLoc(M2ModernReader.Buf B, int At, KeyOwner Owner);

/// <summary>Where the keys of one sequence of the timeline come from.</summary>
internal sealed class SeqOrigin
{
    /// <summary>0: the model or its own skeleton; 1: the parent skeleton (SKPD).</summary>
    public int Source;
    /// <summary>The sequence's index in its source (the per-sequence key arrays are indexed by it).</summary>
    public int Index;
    /// <summary>Its keys are in an .anim file (flag 0x20 clear, not an alias).</summary>
    public bool External;
    /// <summary>That .anim file (AFID of its source), 0 when none is named.</summary>
    public uint AnimFile;
}

/// <summary>How the values of a track are stored (the source form) and read into the typed model.</summary>
internal enum ValueKind { Vec3, CompQuat, Quat, Fixed16, Float, U16, U8, GravityPlain, GravityPacked, None }

/// <summary>A converted track whose external sequences can be loaded later: what it is in the typed model
/// (<paramref name="Array"/>, element, field), its headers per source and its value form.</summary>
internal sealed record ExtRecord(string Array, int Index, string Field, TrackLoc?[] Locs, int SrcSize, ValueKind Kind, bool PadEnd);

/// <summary>The skeleton of a model with a SKID chunk (and its parent, SKPD), as the reader found them.</summary>
internal sealed class SkeletonSource
{
    public uint File, ParentFile;
    public M2ModernReader.Buf Sks1 = null!, Skb1 = null!;
    public M2ModernReader.Buf? Ska1;
    public M2ModernReader.Buf? ParentSks1, ParentSkb1;
    /// <summary>Child bone -> parent bone (by the bone's name hash, else its index); -1 = none.</summary>
    public int[] ParentBone = System.Array.Empty<int>();
    public List<M2AnimFile> AnimFiles = new(), ParentAnimFiles = new();
}

/// <summary>
/// The 1.12-shaped <see cref="M2File"/> of a modern model (the part of <see cref="M2ModernReader"/> that converts):
/// every array the viewer reads, in the version 256 record layouts of <see cref="M2Schema"/>, with the per-sequence
/// tracks put onto one timeline. Built in memory only (it is never written to a file). Views come from the .skin files
/// and are passed to <see cref="M2Model"/> beside it.
/// M3: the sequences, bones, global sequences and attachments of a model with a skeleton file come from the skeleton
/// (and the sequences its parent adds); each sequence of the timeline knows its <see cref="SeqOrigin"/>; tracks with
/// external sequences are recorded (<see cref="ExtRecords"/>) so <see cref="ModernAnimations"/> can load their keys
/// from the .anim file when the sequence is chosen.
/// </summary>
internal sealed partial class Converter
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
    private readonly SkeletonSource? _skel;
    private uint[] _start = System.Array.Empty<uint>(), _end = System.Array.Empty<uint>();
    private int[] _alias = System.Array.Empty<int>();
    private SeqOrigin[] _origin = System.Array.Empty<SeqOrigin>();
    private int _unreadable;

    /// <summary>Tracks with external sequences (for <see cref="ModernAnimations"/>).</summary>
    public List<ExtRecord> ExtRecords { get; } = new();
    public uint[] Starts => _start;
    public uint[] Ends => _end;
    public int[] Aliases => _alias;
    public SeqOrigin[] Origins => _origin;

    public Converter(M2ModernReader.Buf b, M2ModernInfo info, SkeletonSource? skel = null)
    {
        _b = b;
        _info = info;
        _skel = skel;
    }

    public M2File Build(Func<uint, string> nameOf, uint[] txid, byte[] texUnitLookup)
    {
        if (_b.Length < HeaderSize) throw new M2Exception($"the MD20 header is shorter than 0x{HeaderSize:x} bytes");
        var h = new RecData(M2Schema.Header256, 1);
        Encoding.ASCII.GetBytes("MD20").CopyTo(h.Bytes, 0);
        h.U32(0, "version", _info.Version);
        h.Array(0, "name", h.Prim("name", Raw(_b, HName, 1, "name")));
        h.U32(0, "global_flags", _b.U32(HFlags));
        h.Array(0, "global_sequences", h.Prim("global_sequences", _skel != null ? Raw(_skel.Sks1, 0, 4, "skeleton global sequences") : Raw(_b, HGlobal, 4, "global sequences")));
        h.Array(0, "sequences", Sequences(out var lookup));
        h.Array(0, "animation_lookup", h.Prim("animation_lookup", lookup));
        h.Array(0, "bones", Bones());
        h.Array(0, "key_bone_lookup", h.Prim("key_bone_lookup", _skel != null ? Raw(_skel.Skb1, 8, 2, "skeleton key bone lookup") : Raw(_b, HKeyBones, 2, "key bone lookup")));
        h.Array(0, "vertices", h.Prim("vertices", Raw(_b, HVerts, 0x30, "vertices")));
        h.Array(0, "colors", Colors());
        h.Array(0, "textures", Textures(nameOf, txid));
        h.Array(0, "transparency", Weights());
        h.Array(0, "texture_transforms", TextureTransforms());
        h.Array(0, "replaceable_lookup", h.Prim("replaceable_lookup", Raw(_b, HReplace, 2, "replaceable texture lookup")));
        h.Array(0, "render_flags", h.Prim("render_flags", Materials()));
        h.Array(0, "bone_lookup", h.Prim("bone_lookup", Raw(_b, HBoneLookup, 2, "bone lookup")));
        h.Array(0, "texture_lookup", h.Prim("texture_lookup", Raw(_b, HTexLookup, 2, "texture lookup")));
        h.Array(0, "tex_unit_lookup", h.Prim("tex_unit_lookup", texUnitLookup));   // from the shader ids (M2ModernReader.TextureLayers)
        h.Array(0, "transparency_lookup", h.Prim("transparency_lookup", Raw(_b, HWeightLookup, 2, "transparency lookup")));
        h.Array(0, "texture_transform_lookup", h.Prim("texture_transform_lookup", Raw(_b, HTransformLookup, 2, "texture transform lookup")));
        h.Raw(0, "bounds_min", _b.B.AsSpan(HBounds, 12));
        h.Raw(0, "bounds_max", _b.B.AsSpan(HBounds + 12, 12));
        h.Raw(0, "bounds_radius", _b.B.AsSpan(HRadius, 4));
        h.Raw(0, "collision_min", _b.B.AsSpan(HCollBox, 12));
        h.Raw(0, "collision_max", _b.B.AsSpan(HCollBox + 12, 12));
        h.Raw(0, "collision_radius", _b.B.AsSpan(HCollRadius, 4));
        h.Array(0, "collision_triangles", h.Prim("collision_triangles", Raw(_b, HCollTris, 2, "collision triangles")));
        h.Array(0, "collision_vertices", h.Prim("collision_vertices", Raw(_b, HCollVerts, 12, "collision vertices")));
        h.Array(0, "collision_normals", h.Prim("collision_normals", Raw(_b, HCollNormals, 12, "collision normals")));
        h.Array(0, "attachments", Attachments());
        h.Array(0, "attachment_lookup", h.Prim("attachment_lookup", _skel?.Ska1 != null ? Raw(_skel.Ska1, 8, 2, "skeleton attachment lookup") : Raw(_b, HAttachLookup, 2, "attachment lookup")));
        h.Array(0, "events", Events());
        uint lights = _b.U32(HLights), cameras = _b.U32(HCameras);
        if (lights > 0) _info.NotShown.Add($"{lights} light{(lights == 1 ? "" : "s")}: not drawn by the viewer");
        if (cameras > 0) _info.NotShown.Add($"{cameras} camera{(cameras == 1 ? "" : "s")}: not used by the viewer");
        if (_zeroQuats > 0) _info.Approximated.Add($"{_zeroQuats} texture rotation key{(_zeroQuats == 1 ? "" : "s")} of zero length read as no rotation");
        return M2File.FromHeader(h.ToData());
    }

    /// <summary>Notes the arrays left out (called after the effects were read too).</summary>
    public void Finish()
    {
        if (_unreadable > 0) _info.Notes.Add($"{_unreadable} key array{(_unreadable == 1 ? "" : "s")} of the animations point outside the model and were left out");
    }

    private static byte[] Raw(M2ModernReader.Buf b, int at, int elemSize, string what) => b.Block(at, elemSize, what).ToArray();

    // ------------------------------------------------------------------ sequences and the timeline

    /// <summary>
    /// The sequences on one timeline. Without a skeleton: the model's. With one: the skeleton's, then the sequences of
    /// its parent skeleton that the child does not have itself (same animation id and sub id; MEASURED on 1.60: every
    /// child sequence also exists in its parent, so the child's version replaces the parent's).
    /// </summary>
    private M2Data Sequences(out byte[] lookup)
    {
        var rows = new List<(M2ModernReader.Buf B, int At, int Source, int Index, List<M2AnimFile> Files)>();
        if (_skel == null)
        {
            var src = _b.Block(HSeq, SeqSize, "sequences");
            uint o = _b.U32(HSeq + 4);
            for (int i = 0; i < src.Length / SeqSize; i++) rows.Add((_b, (int)o + i * SeqSize, 0, i, _info.AnimFiles));
            lookup = Raw(_b, HSeqLookup, 2, "sequence lookup");
        }
        else
        {
            var own = _skel.Sks1.Block(8, SeqSize, "skeleton sequences");
            uint o = _skel.Sks1.U32(12);
            var have = new HashSet<(int, int)>();
            for (int i = 0; i < own.Length / SeqSize; i++)
            {
                rows.Add((_skel.Sks1, (int)o + i * SeqSize, 0, i, _skel.AnimFiles));
                have.Add((_skel.Sks1.U16((int)o + i * SeqSize), _skel.Sks1.U16((int)o + i * SeqSize + 2)));
            }
            lookup = Raw(_skel.Sks1, 16, 2, "skeleton sequence lookup");
            if (_skel.ParentSks1 is { } ps)
            {
                var par = ps.Block(8, SeqSize, "parent skeleton sequences");
                uint po = ps.U32(12);
                int added = 0;
                for (int i = 0; i < par.Length / SeqSize; i++)
                {
                    int at = (int)po + i * SeqSize;
                    if (have.Contains((ps.U16(at), ps.U16(at + 2)))) continue;
                    rows.Add((ps, at, 1, i, _skel.ParentAnimFiles));
                    added++;
                }
                _info.ParentSequences = added;
                if (added > 0) lookup = System.Array.Empty<byte>();   // rebuilt below for the merged list
            }
        }
        int n = rows.Count;
        var d = new RecData(M2Schema.Sequence, n);
        _start = new uint[n];
        _end = new uint[n];
        _alias = new int[n];
        _origin = new SeqOrigin[n];
        var duration = new uint[n];
        var flags = new uint[n];
        var next = new int[n];
        // a sequence's alias target (next_alias) is an index in its own source: map it to the timeline
        var bySource = new Dictionary<(int Source, int Index), int>();
        var byId = new Dictionary<(int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var (b, at, source, index, files) = rows[i];
            duration[i] = b.U32(at + 4);
            flags[i] = b.U32(at + 0x0C);
            next[i] = b.U16(at + 0x3E);
            bySource[(source, index)] = i;
            byId.TryAdd((b.U16(at), b.U16(at + 2)), i);
            int animId = b.U16(at), subId = b.U16(at + 2);
            _origin[i] = new SeqOrigin
            {
                Source = source, Index = index, External = (flags[i] & (FlagInline | FlagAlias)) == 0,
                AnimFile = files.FirstOrDefault(f => f.AnimId == animId && f.SubId == subId)?.File ?? 0,
            };
        }
        int Map(int i, int target)
        {
            var (_, _, source, _, _) = rows[i];
            if (bySource.TryGetValue((source, target), out int t)) return t;
            // a parent's alias whose target the child replaced: the child's sequence with the same ids
            if (source == 1 && _skel?.ParentSks1 is { } ps)
            {
                uint po = ps.U32(12);
                int at = (int)po + target * SeqSize;
                if (target >= 0 && at + SeqSize <= ps.Length && byId.TryGetValue((ps.U16(at), ps.U16(at + 2)), out t)) return t;
            }
            return -1;
        }
        var nextT = new int[n];
        for (int i = 0; i < n; i++) nextT[i] = Map(i, next[i]);
        // aliases (flag 0x40) play their target's keys: follow the chain to a sequence that is not an alias
        for (int i = 0; i < n; i++)
        {
            _alias[i] = -1;
            if ((flags[i] & FlagAlias) == 0) continue;
            int t = nextT[i];
            for (int guard = 0; guard < 32 && t >= 0 && t < n && (flags[t] & FlagAlias) != 0; guard++) t = nextT[t];
            if (t >= 0 && t < n && (flags[t] & FlagAlias) == 0 && t != i) _alias[i] = t;
        }
        uint cursor = 0;
        for (int i = 0; i < n; i++)
        {
            if (_alias[i] >= 0) continue;
            _start[i] = cursor;
            _end[i] = cursor + duration[i];
            cursor = _end[i] + 1;
        }
        for (int i = 0; i < n; i++)
        {
            if (_alias[i] < 0) continue;
            _start[i] = _start[_alias[i]];
            _end[i] = _end[_alias[i]];
            _origin[i].External = false;
        }
        for (int i = 0; i < n; i++)
        {
            var (b, at, _, _, _) = rows[i];
            var s = b.B.AsSpan(at, SeqSize);
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
            d.U16(i, "next_alias", (ushort)(rows[i].Source == 0 ? next[i] : Math.Max(0, nextT[i])));
            if (_alias[i] >= 0) _info.AliasSequences++;
            else if (_origin[i].External) _info.ExternalSequences++;
            else _info.InlineSequences++;
        }
        if (lookup.Length == 0 && n > 0)
        {
            // the merged list: animation id -> its first sequence (the 1.12 lookup form)
            int max = 0;
            for (int i = 0; i < n; i++) max = Math.Max(max, rows[i].B.U16(rows[i].At));
            var l = new short[max + 1];
            System.Array.Fill(l, (short)-1);
            for (int i = n - 1; i >= 0; i--) l[rows[i].B.U16(rows[i].At)] = (short)i;
            lookup = new byte[l.Length * 2];
            for (int i = 0; i < l.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(lookup.AsSpan(i * 2), l[i]);
        }
        return d.ToData();
    }

    /// <summary>Keys gathered for one track on the timeline, values still in their source form.</summary>
    internal sealed class Gathered
    {
        public int Interp, GlobalSeq;
        public readonly List<uint> Times = new();
        public readonly MemoryStream Values = new();
        public (uint, uint)[] Ranges = System.Array.Empty<(uint, uint)>();
        public bool HasExternal;
    }

    /// <summary>One source sequence's keys of a track: (times, value bytes in the source form), or null (no keys or
    /// unreadable). <paramref name="data"/> holds the key arrays (the track's own buffer, or an .anim part).</summary>
    internal static (uint[] Times, byte[] Values)? KeysOf(TrackLoc loc, int k, int srcSize, M2ModernReader.Buf data, ref int unreadable)
    {
        var b = loc.B;
        int at = loc.At;
        uint tn = b.U32(at + 4), to = b.U32(at + 8);
        uint vn = srcSize > 0 ? b.U32(at + 12) : 0, vo = srcSize > 0 ? b.U32(at + 16) : 0;
        int outer = (int)(srcSize > 0 ? Math.Min(tn, vn) : tn);
        if (k >= outer) return null;
        uint n = b.U32((int)to + 8 * k), o = b.U32((int)to + 8 * k + 4);
        if (n == 0) return null;
        if (!data.Fits(n, o, 4)) { unreadable++; return null; }
        uint m = n, vo2 = 0;
        if (srcSize > 0)
        {
            m = b.U32((int)vo + 8 * k);
            vo2 = b.U32((int)vo + 8 * k + 4);
            if (!data.Fits(m, vo2, srcSize)) { unreadable++; return null; }
        }
        int count = (int)Math.Min(n, m);
        if (count == 0) return null;
        var times = new uint[count];
        for (int j = 0; j < count; j++) times[j] = data.U32((int)o + 4 * j);
        byte[] values = srcSize > 0 ? data.B.AsSpan((int)vo2, count * srcSize).ToArray() : System.Array.Empty<byte>();
        return (times, values);
    }

    /// <summary>True when the header's outer arrays fit its buffer.</summary>
    private static bool HeaderFits(TrackLoc loc, int srcSize)
    {
        var b = loc.B;
        if (loc.At < 0 || loc.At + (srcSize > 0 ? 20 : 12) > b.Length) return false;
        uint tn = b.U32(loc.At + 4), to = b.U32(loc.At + 8);
        if (!b.Fits(tn, to, 8)) return false;
        return srcSize == 0 || b.Fits(b.U32(loc.At + 12), b.U32(loc.At + 16), 8);
    }

    /// <summary>
    /// A modern track (interpolation, global sequence, then {count, offset} of the per-sequence timestamp arrays and,
    /// when <paramref name="srcSize"/> &gt; 0, of the per-sequence value arrays) gathered onto the timeline: keys
    /// concatenated, ranges per sequence. <paramref name="locs"/> holds its header per source (0: the model or its
    /// skeleton, 1: the parent skeleton). External sequences get no keys here (see <see cref="ModernAnimations"/>).
    /// </summary>
    internal Gathered Gather(TrackLoc?[] locs, int srcSize, bool padEnd)
    {
        var g = new Gathered();
        // the interpolation and global sequence: of the first header with key arrays (a child skeleton's empty track
        // takes its parent's), else of the first one
        TrackLoc? head = null;
        foreach (var l in locs)
            if (l is { } x && x.At >= 0 && x.At + 8 <= x.B.Length && x.B.U32(x.At + 4) > 0) { head = l; break; }
        if (head == null) foreach (var l in locs) if (l != null) { head = l; break; }
        if (head is not { } h) return g;
        g.Interp = h.B.U16(h.At);
        g.GlobalSeq = h.B.I16(h.At + 2);
        var ok = new TrackLoc?[locs.Length];
        for (int i = 0; i < locs.Length; i++)
        {
            if (locs[i] is not { } l) continue;
            if (HeaderFits(l, srcSize)) ok[i] = l;
            else _unreadable++;
        }
        if (ok[0] == null && locs[0] != null) return g;
        if (g.GlobalSeq >= 0)
        {
            // a global-sequence track runs on its own clock: its keys as they are, no ranges
            if (ok[0] is { } l0)
            {
                uint tn = l0.B.U32(l0.At + 4);
                uint vn = srcSize > 0 ? l0.B.U32(l0.At + 12) : tn;
                int outer = (int)Math.Min(tn, vn);
                for (int k = 0; k < outer; k++)
                    if (KeysOf(l0, k, srcSize, l0.B, ref _unreadable) is { } x)
                    {
                        g.Times.AddRange(x.Times);
                        g.Values.Write(x.Values);
                        break;
                    }
            }
            return g;
        }
        int nSeq = _start.Length;
        var r = new (uint, uint)[nSeq];
        for (int s = 0; s < nSeq; s++)
        {
            r[s] = (uint.MaxValue, uint.MaxValue);
            if (_alias[s] >= 0) continue;
            var o = _origin[s];
            if (o.Source >= ok.Length || ok[o.Source] is not { } loc) continue;
            if (o.External)
            {
                g.HasExternal = true;
                continue;
            }
            if (KeysOf(loc, o.Index, srcSize, loc.B, ref _unreadable) is not { } x) continue;
            uint first = (uint)g.Times.Count;
            foreach (var t in x.Times) g.Times.Add(_start[s] + t);
            g.Values.Write(x.Values);
            if (padEnd && srcSize > 0 && x.Times.Length > 0 && _start[s] + x.Times[^1] < _end[s])
            {
                // hold the last value until the sequence ends (instead of leaning towards the next sequence's keys)
                g.Times.Add(_end[s]);
                g.Values.Write(x.Values, x.Values.Length - srcSize, srcSize);
            }
            r[s] = (first, (uint)g.Times.Count - 1);
        }
        for (int s = 0; s < nSeq; s++)
            if (_alias[s] >= 0) r[s] = r[_alias[s]];
        if (g.Times.Count > 0) g.Ranges = r;
        return g;
    }

    /// <summary>The gathered track written as the 1.12 track of element <paramref name="i"/> of <paramref name="dst"/>;
    /// a track with external sequences is recorded for <see cref="ModernAnimations"/>.</summary>
    private void Track(RecData dst, int i, string name, TrackLoc?[] locs, int srcSize, ValueKind kind, Func<byte[], byte[]>? convert, bool padEnd, string array)
    {
        var g = Gather(locs, srcSize, padEnd);
        var tf = dst.Rec.Track(name);
        var rangesElem = dst.Rec.Arrays[tf.RangesIndex].Elem;
        var timesElem = dst.Rec.Arrays[tf.TimestampsIndex].Elem;
        var valuesElem = tf.ValuesIndex >= 0 ? dst.Rec.Arrays[tf.ValuesIndex].Elem : null;
        var timesBytes = new byte[g.Times.Count * 4];
        for (int j = 0; j < g.Times.Count; j++) BinaryPrimitives.WriteUInt32LittleEndian(timesBytes.AsSpan(j * 4), g.Times[j]);
        var rangeBytes = new byte[g.Ranges.Length * 8];
        for (int j = 0; j < g.Ranges.Length; j++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rangeBytes.AsSpan(j * 8), g.Ranges[j].Item1);
            BinaryPrimitives.WriteUInt32LittleEndian(rangeBytes.AsSpan(j * 8 + 4), g.Ranges[j].Item2);
        }
        var raw = g.Values.ToArray();
        var values = convert != null ? convert(raw) : raw;
        dst.Track(i, name, g.Interp, g.GlobalSeq, RecData.Prim(rangesElem, rangeBytes), RecData.Prim(timesElem, timesBytes),
            valuesElem == null ? null : RecData.Prim(valuesElem, values));
        if (g.HasExternal && kind != ValueKind.None) ExtRecords.Add(new ExtRecord(array, i, name, locs, srcSize, kind, padEnd));
    }

    internal static byte[] DecompressQuats(byte[] raw)
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
    private byte[] ZeroQuatsAsIdentity(byte[] raw) => ZeroQuatsAsIdentity(raw, ref _zeroQuats);

    internal static byte[] ZeroQuatsAsIdentity(byte[] raw, ref int count)
    {
        for (int k = 0; k + 16 <= raw.Length; k += 16)
        {
            float x = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k)), y = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 4)),
                z = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 8)), w = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(k + 12));
            if (x * x + y * y + z * z + w * w > 1e-6f) continue;
            System.Array.Clear(raw, k, 12);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(k + 12), 1f);
            count++;
        }
        return raw;
    }

    // ------------------------------------------------------------------ records with tracks

    private M2Data Bones()
    {
        var bones = _skel?.Skb1 ?? _b;
        int arr = _skel != null ? 0 : HBones;
        var owner = _skel != null ? KeyOwner.SkelBones : KeyOwner.Model;
        var src = bones.Block(arr, BoneSize, _skel != null ? "skeleton bones" : "bones");
        uint off = bones.U32(arr + 4);
        int n = src.Length / BoneSize;
        uint parentOff = _skel?.ParentSkb1 is { } pb ? pb.U32(4) : 0;
        var d = new RecData(M2Schema.Bone, n);
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * BoneSize, BoneSize);
            int at = (int)off + i * BoneSize;
            TrackLoc?[] L(int field)
            {
                var own = new TrackLoc(bones, at + field, owner);
                if (_skel?.ParentSkb1 is not { } p || i >= _skel.ParentBone.Length || _skel.ParentBone[i] < 0) return new TrackLoc?[] { own };
                return new TrackLoc?[] { own, new TrackLoc(p, (int)parentOff + _skel.ParentBone[i] * BoneSize + field, KeyOwner.SkelBones) };
            }
            d.Raw(i, "key_bone_id", s[..4]);
            d.Raw(i, "flags", s.Slice(4, 4));
            d.Raw(i, "parent", s.Slice(8, 2));
            d.Raw(i, "submesh_id", s.Slice(10, 2));
            Track(d, i, "translation", L(0x10), 12, ValueKind.Vec3, null, true, "bones");
            Track(d, i, "rotation", L(0x24), 8, ValueKind.CompQuat, DecompressQuats, true, "bones");
            Track(d, i, "scale", L(0x38), 12, ValueKind.Vec3, null, true, "bones");
            d.Raw(i, "pivot", s.Slice(0x4C, 12));
        }
        return d.ToData();
    }

    private TrackLoc?[] Own(int at) => new TrackLoc?[] { new TrackLoc(_b, at, KeyOwner.Model) };

    private M2Data Colors()
    {
        var src = _b.Block(HColors, ColorSize, "colors");
        uint off = _b.U32(HColors + 4);
        int n = src.Length / ColorSize;
        var d = new RecData(M2Schema.Color, n);
        for (int i = 0; i < n; i++)
        {
            int at = (int)off + i * ColorSize;
            Track(d, i, "color", Own(at), 12, ValueKind.Vec3, null, true, "colors");
            Track(d, i, "alpha", Own(at + 0x14), 2, ValueKind.Fixed16, null, true, "colors");
        }
        return d.ToData();
    }

    private M2Data Weights()
    {
        var src = _b.Block(HWeights, WeightSize, "texture weights");
        uint off = _b.U32(HWeights + 4);
        int n = src.Length / WeightSize;
        var d = new RecData(M2Schema.Transparency, n);
        for (int i = 0; i < n; i++) Track(d, i, "weight", Own((int)off + i * WeightSize), 2, ValueKind.Fixed16, null, true, "transparency");
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
            Track(d, i, "translation", Own(at), 12, ValueKind.Vec3, null, true, "texture_transforms");
            Track(d, i, "rotation", Own(at + 0x14), 16, ValueKind.Quat, ZeroQuatsAsIdentity, true, "texture_transforms");
            Track(d, i, "scale", Own(at + 0x28), 12, ValueKind.Vec3, null, true, "texture_transforms");
        }
        return d.ToData();
    }

    private M2Data Attachments()
    {
        var b = _skel?.Ska1 ?? (_skel != null ? null : _b);
        if (b == null) return M2Data.Empty(M2Elem.Of(M2Schema.Attachment));
        int arr = _skel != null ? 0 : HAttach;
        var owner = _skel != null ? KeyOwner.SkelAttachments : KeyOwner.Model;
        var src = b.Block(arr, AttachSize, "attachments");
        uint off = b.U32(arr + 4);
        int n = src.Length / AttachSize;
        var d = new RecData(M2Schema.Attachment, n);
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(i * AttachSize, AttachSize);
            d.Raw(i, "id", s[..4]);
            d.Raw(i, "bone", s.Slice(4, 2));
            d.Raw(i, "unknown_06", s.Slice(6, 2));
            d.Raw(i, "position", s.Slice(8, 12));
            Track(d, i, "visible", new TrackLoc?[] { new TrackLoc(b, (int)off + i * AttachSize + 0x14, owner) }, 1, ValueKind.None, null, true, "attachments");
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
            Track(d, i, "enabled", Own((int)off + i * EventSize + 0x18), 0, ValueKind.None, null, false, "events");
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

    /// <summary>The materials (flags, blend mode). Blend modes 1.12 does not have are stored as the nearest one; the
    /// original modes are kept in <see cref="M2ModernInfo.MaterialBlends"/> (M3: the 3D view draws mode 7 as the modern
    /// client's "blend add").</summary>
    private byte[] Materials()
    {
        var raw = _b.Block(HMaterials, 4, "materials").ToArray();
        var odd = new Dictionary<int, int>();
        var orig = new int[raw.Length / 4];
        for (int i = 0; i + 4 <= raw.Length; i += 4)
        {
            int blend = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i + 2));
            orig[i / 4] = blend;
            if (blend <= 6) continue;
            odd[blend] = odd.GetValueOrDefault(blend) + 1;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i + 2), NearestBlend(blend));
        }
        _info.MaterialBlends = orig;
        foreach (var (blend, count) in odd.OrderBy(x => x.Key))
            _info.Approximated.Add(blend == 7
                ? $"blend mode 7 (blend add) drawn with the modern client's factors (one, one minus source alpha) ({count} material{(count == 1 ? "" : "s")})"
                : $"blend mode {blend} drawn as {M2Schema.BlendModes[NearestBlend(blend)]} ({count} material{(count == 1 ? "" : "s")})");
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
