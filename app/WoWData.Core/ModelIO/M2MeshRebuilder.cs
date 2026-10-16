using System.Buffers.Binary;
using System.Numerics;
using WoWData.Core.M2;

namespace WoWData.Core.ModelIO;

/// <summary>The geometry of one view-0 submesh for the rebuild: 48-byte M2 vertex records and triangles (indices into
/// them).</summary>
public sealed class SubmeshInput
{
    public byte[][] Vertices { get; init; } = Array.Empty<byte[]>();
    public int[] Triangles { get; init; } = Array.Empty<int>();
}

public sealed class RebuildReport
{
    public List<string> Views { get; } = new();
    /// <summary>Plain-language notes about parts split for a bone limit.</summary>
    public List<string> Splits { get; } = new();
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int BoneLookup { get; set; }
    public bool BoundsRecomputed { get; set; }
}

public sealed class M2RebuildException : Exception
{
    public M2RebuildException(string message) : base(message) { }
}

/// <summary>
/// Writes new geometry into a model while keeping everything else: the vertex array is rebuilt from the submeshes
/// (vertex records you did not change keep their exact bytes), then the four views are regenerated the way the 1.12
/// files are built (checked on every client model: view 0's indices are the identity, every view has the same
/// triangles, the texture units of views 1-3 are view 0's per source submesh, with geoset_index = the source
/// submesh): each submesh whose vertices use more bones than a view's limit (256 / 75 / 53 / 21) is split into
/// pieces whose bone palette fits, the palettes are collected into bone_lookup, and every texture unit is copied
/// onto each piece of its submesh. Submesh centre = the mean of its vertices, centre bone = the bone most vertices use
/// (98% of the client's submeshes follow both). Bones, animations, materials, attachments, events, lights, cameras,
/// particles, ribbons and the collision mesh are not touched (their blocks are copied byte for byte by the writer).
/// The bounds and the per-sequence bounds are recomputed from the animated vertices (see <see cref="M2Bounds"/>).
/// </summary>
public static class M2MeshRebuilder
{
    public const int MaxVertices = 65535;
    public const int MaxIndices = 65535;

    public static (byte[] Data, RebuildReport Report) Rebuild(byte[] originalData, IReadOnlyList<SubmeshInput> submeshes, bool recomputeBounds = true)
    {
        var f = M2File.Parse(originalData);
        var orig = M2Model.FromFile(f);
        if (orig.Views.Length == 0) throw new M2RebuildException("The model has no views (nothing is drawn), so its mesh cannot be rebuilt.");
        var v0 = orig.Views[0];
        if (submeshes.Count != v0.Submeshes.Length) throw new M2RebuildException($"{submeshes.Count} parts for {v0.Submeshes.Length} submeshes.");
        int nbones = orig.Bones.Length;
        var rep = new RebuildReport();

        // ---------------------------------------------------------------- vertices
        var offsets = new int[submeshes.Count];
        var verts = new List<byte[]>();
        for (int s = 0; s < submeshes.Count; s++)
        {
            offsets[s] = verts.Count;
            verts.AddRange(submeshes[s].Vertices);
        }
        if (verts.Count > MaxVertices)
            throw new M2RebuildException($"The model would have {verts.Count:N0} vertices. The game's model format numbers them with 16 bits: at most {MaxVertices:N0}.");
        var vbytes = new byte[verts.Count * 0x30];
        for (int i = 0; i < verts.Count; i++)
        {
            if (verts[i].Length != 0x30) throw new M2RebuildException("bad vertex record");
            Buffer.BlockCopy(verts[i], 0, vbytes, i * 0x30, 0x30);
        }
        var bonesOf = new (byte Bone, byte Weight)[verts.Count][];
        var positions = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            var r = verts[i];
            positions[i] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(4)), BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(8)));
            var l = new List<(byte, byte)>(4);
            for (int k = 0; k < 4; k++)
                if (r[12 + k] > 0)
                {
                    if (r[16 + k] >= nbones && nbones > 0) throw new M2RebuildException($"A vertex uses bone {r[16 + k]}, but the skeleton has {nbones} bones.");
                    l.Add((r[16 + k], r[12 + k]));
                }
            bonesOf[i] = l.ToArray();
        }
        rep.Vertices = verts.Count;

        // ---------------------------------------------------------------- views
        var sub0 = f["views"].Child(0, "submeshes");
        var tu0 = f["views"].Child(0, "texture_units");
        var subRec = M2Schema.Submesh;
        var tuRec = M2Schema.TexUnit;
        var viewRec = M2Schema.View;
        var lookup = new List<ushort>();
        var paletteAt = new Dictionary<string, int>(StringComparer.Ordinal);
        int nviews = orig.Views.Length;
        var viewBytes = new byte[nviews * viewRec.Size];
        var viewSub = new M2Data[nviews][];
        for (int vi = 0; vi < nviews; vi++)
        {
            int limit = (int)Math.Max(1, orig.Views[vi].BoneLimit);
            var indices = new List<ushort>();
            var tris = new List<ushort>();
            var props = new List<byte>();
            var subBytes = new List<byte>();
            var tuBytes = new List<byte>();
            var piecesOf = new List<int>[submeshes.Count];
            int pieceCount = 0;
            for (int s = 0; s < submeshes.Count; s++)
            {
                piecesOf[s] = new List<int>();
                var input = submeshes[s];
                int off = offsets[s];
                var pieces = Split(input, off, bonesOf, limit);
                if (pieces.Count > 1)
                    rep.Splits.Add($"view {vi + 1} ({limit} bones per draw): part {s} (geoset {v0.Submeshes[s].GeosetId}) uses {BonesUsed(input, off, bonesOf).Count} bones, so it is drawn in {pieces.Count} pieces.");
                foreach (var piece in pieces)
                {
                    // view vertices of the piece (ascending), palette in order of first use
                    var vset = new SortedSet<int>();
                    foreach (var t in piece) vset.Add(t);
                    if (piece.Count == 0)
                        for (int k = 0; k < input.Vertices.Length; k++) vset.Add(off + k);      // an empty submesh keeps its vertices
                    var vlist = vset.ToArray();
                    var palette = new List<int>();
                    var palIndex = new Dictionary<int, int>();
                    foreach (int v in vlist)
                        foreach (var (b, _) in bonesOf[v])
                            if (!palIndex.ContainsKey(b))
                            {
                                palIndex[b] = palette.Count;
                                palette.Add(b);
                            }
                    if (palette.Count == 0 && nbones > 0)
                    {
                        palIndex[0] = 0;
                        palette.Add(0);
                    }
                    string key = string.Join(",", palette);
                    if (!paletteAt.TryGetValue(key, out int boneStart))
                    {
                        boneStart = lookup.Count;
                        paletteAt[key] = boneStart;
                        lookup.AddRange(palette.Select(b => (ushort)b));
                    }
                    int vstart = indices.Count;
                    if (vstart + vlist.Length > MaxVertices)
                        throw new M2RebuildException($"View {vi + 1} would need {vstart + vlist.Length:N0} vertices (at most {MaxVertices:N0}).");
                    var local = new Dictionary<int, int>(vlist.Length);
                    int influences = 0;
                    var useCount = new Dictionary<int, int>();
                    var centre = Vector3.Zero;
                    foreach (int v in vlist)
                    {
                        local[v] = indices.Count;
                        indices.Add((ushort)v);
                        var bw = bonesOf[v];
                        var r = verts[v];
                        for (int k = 0; k < 4; k++) props.Add(r[12 + k] > 0 && palIndex.TryGetValue(r[16 + k], out int pi) ? (byte)pi : (byte)0);
                        influences = Math.Max(influences, bw.Length);
                        foreach (var (b, _) in bw) useCount[b] = useCount.GetValueOrDefault(b) + 1;
                        centre += positions[v];
                    }
                    if (vlist.Length > 0) centre /= vlist.Length;
                    int centreBone = useCount.Count == 0 ? (palette.Count > 0 ? palette[0] : 0)
                        : useCount.OrderByDescending(kv => kv.Value).ThenBy(kv => palIndex.GetValueOrDefault(kv.Key)).First().Key;
                    int istart = tris.Count;
                    if (istart > MaxIndices || piece.Count > MaxIndices)
                        throw new M2RebuildException($"View {vi + 1} would need {istart + piece.Count:N0} triangle corners before its last part; the format stores the start with 16 bits (at most {MaxIndices:N0}, about {MaxIndices / 3:N0} triangles).");
                    foreach (int t in piece) tris.Add((ushort)local[t]);
                    // the submesh record: the source's (geoset id, level...) with the new ranges
                    var rec = sub0.Bytes.AsSpan(s * subRec.Size, subRec.Size).ToArray();
                    void U16(string name, int value) => BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(subRec.Field(name).Offset), checked((ushort)value));
                    U16("vertex_start", vstart);
                    U16("vertex_count", vlist.Length);
                    U16("index_start", istart);
                    U16("index_count", piece.Count);
                    U16("bone_count", palette.Count);
                    U16("bone_start", boneStart);
                    U16("bone_influences", influences);
                    U16("center_bone", centreBone);
                    int co = subRec.Field("center").Offset;
                    BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(co), centre.X);
                    BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(co + 4), centre.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(co + 8), centre.Z);
                    subBytes.AddRange(rec);
                    piecesOf[s].Add(pieceCount++);
                }
            }
            // texture units: view 0's, in their order, onto every piece of their submesh
            int ntu = 0;
            for (int t = 0; t < tu0.Count; t++)
            {
                int src = BinaryPrimitives.ReadUInt16LittleEndian(tu0.Bytes.AsSpan(t * tuRec.Size + tuRec.Field("submesh").Offset));
                if (src >= submeshes.Count) continue;
                foreach (int p in piecesOf[src])
                {
                    var rec = tu0.Bytes.AsSpan(t * tuRec.Size, tuRec.Size).ToArray();
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(tuRec.Field("submesh").Offset), (ushort)p);
                    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(tuRec.Field("geoset_index").Offset), (ushort)src);
                    tuBytes.AddRange(rec);
                    ntu++;
                }
            }
            if (tris.Count % 3 != 0) throw new M2RebuildException("internal error: triangle list");
            var orv = f["views"];
            Buffer.BlockCopy(orv.Bytes, vi * viewRec.Size, viewBytes, vi * viewRec.Size, viewRec.Size);
            var idxBytes = new byte[indices.Count * 2];
            for (int i = 0; i < indices.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(idxBytes.AsSpan(i * 2), indices[i]);
            var triBytes = new byte[tris.Count * 2];
            for (int i = 0; i < tris.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(triBytes.AsSpan(i * 2), tris[i]);
            M2Data Make(string name, int count, byte[] bytes)
            {
                var a = viewRec.Array(name);
                return count == 0 ? M2Data.Empty(a.Elem) : new M2Data(a.Elem, count, bytes, a.Elem.Rec is { Arrays.Count: > 0 } ? new M2Data[count][] : null, -1);
            }
            viewSub[vi] = new M2Data[viewRec.Arrays.Count];
            viewSub[vi][viewRec.Array("indices").Index] = Make("indices", indices.Count, idxBytes);
            viewSub[vi][viewRec.Array("triangles").Index] = Make("triangles", tris.Count, triBytes);
            viewSub[vi][viewRec.Array("properties").Index] = Make("properties", props.Count / 4, props.ToArray());
            viewSub[vi][viewRec.Array("submeshes").Index] = Make("submeshes", pieceCount, subBytes.ToArray());
            viewSub[vi][viewRec.Array("texture_units").Index] = Make("texture_units", ntu, tuBytes.ToArray());
            rep.Views.Add($"view {vi + 1} ({limit} bones per draw): {pieceCount} parts, {indices.Count:N0} vertices, {tris.Count / 3:N0} triangles, {ntu} texture units");
            if (vi == 0) rep.Triangles = tris.Count / 3;
        }

        f["vertices"].Set(verts.Count, vbytes);
        f["views"].Set(nviews, viewBytes, viewSub);
        var lb = new byte[lookup.Count * 2];
        for (int i = 0; i < lookup.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(lb.AsSpan(i * 2), lookup[i]);
        f["bone_lookup"].Set(lookup.Count, lb);
        rep.BoneLookup = lookup.Count;

        if (recomputeBounds)
        {
            var tmp = M2Model.Load(f.Serialize());
            M2Bounds.Apply(f, tmp);
            rep.BoundsRecomputed = true;
        }
        return (f.Serialize(), rep);
    }

    private static HashSet<int> BonesUsed(SubmeshInput input, int off, (byte Bone, byte Weight)[][] bonesOf)
    {
        var set = new HashSet<int>();
        for (int k = 0; k < input.Vertices.Length; k++)
            foreach (var (b, _) in bonesOf[off + k]) set.Add(b);
        return set;
    }

    /// <summary>The submesh's triangles (as global vertex indices) split into pieces whose bones fit the limit:
    /// one piece when the whole submesh fits, else first-fit by triangle.</summary>
    private static List<List<int>> Split(SubmeshInput input, int off, (byte Bone, byte Weight)[][] bonesOf, int limit)
    {
        var all = new List<int>(input.Triangles.Length);
        foreach (var t in input.Triangles) all.Add(off + t);
        if (BonesUsed(input, off, bonesOf).Count <= limit) return new List<List<int>> { all };
        var pieces = new List<(List<int> Tris, HashSet<int> Bones)>();
        for (int k = 0; k + 2 < all.Count; k += 3)
        {
            var tb = new HashSet<int>();
            for (int c = 0; c < 3; c++)
                foreach (var (b, _) in bonesOf[all[k + c]]) tb.Add(b);
            (List<int> Tris, HashSet<int> Bones)? target = null;
            foreach (var p in pieces)
            {
                int extra = tb.Count(b => !p.Bones.Contains(b));
                if (p.Bones.Count + extra <= limit)
                {
                    target = p;
                    break;
                }
            }
            if (target == null)
            {
                target = (new List<int>(), new HashSet<int>());
                pieces.Add(target.Value);
            }
            target.Value.Tris.Add(all[k]);
            target.Value.Tris.Add(all[k + 1]);
            target.Value.Tris.Add(all[k + 2]);
            target.Value.Bones.UnionWith(tb);
        }
        return pieces.Select(p => p.Tris).ToList();
    }
}

/// <summary>
/// Bounds of a model from its animated vertices: per sequence, the box of every vertex skinned by the bone matrices
/// at the sequence's key times (sampled at most every 33 ms); the model's box is the rest pose plus every sequence.
/// The radius is the largest distance of a vertex from the centre of the box. On the client's models this gives the
/// stored values within a few percent (exactly for static items); sequences whose stored bounds are zero (doodads)
/// keep zero.
/// </summary>
public static class M2Bounds
{
    public static (Vector3 Min, Vector3 Max, float Radius) Rest(M2Model m)
    {
        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(float.MinValue);
        foreach (var v in m.Vertices)
        {
            mn = Vector3.Min(mn, v.Position);
            mx = Vector3.Max(mx, v.Position);
        }
        if (m.Vertices.Length == 0) return (Vector3.Zero, Vector3.Zero, 0);
        var c = (mn + mx) / 2;
        float r = 0;
        foreach (var v in m.Vertices) r = Math.Max(r, Vector3.Distance(v.Position, c));
        return (mn, mx, r);
    }

    /// <summary>(min, max, radius) of the vertices while sequence <paramref name="si"/> plays.</summary>
    public static (Vector3 Min, Vector3 Max, float Radius) Sequence(M2Model m, int si, List<Vector3>? collect = null)
    {
        var seq = m.Sequences[si];
        var times = new SortedSet<uint> { 0, seq.Duration };
        void Keys<T>(M2Track<T> t) where T : struct
        {
            if (t.Times.Length == 0) return;
            if (t.Ranges.Length > si)
            {
                var (a, b) = t.Ranges[si];
                for (uint k = a; k <= b && k < t.Times.Length; k++)
                    if (t.Times[k] >= seq.Start && t.Times[k] <= seq.End) times.Add(t.Times[k] - seq.Start);
            }
        }
        foreach (var b in m.Bones)
        {
            Keys(b.Translation);
            Keys(b.Rotation);
            Keys(b.Scale);
        }
        // at most one sample every 33 ms
        var samples = new List<uint>();
        foreach (var t in times)
            if (samples.Count == 0 || t - samples[^1] >= 33 || t == seq.Duration) samples.Add(t);
        var mats = new Matrix4x4[m.Bones.Length];
        var mn = new Vector3(float.MaxValue);
        var mx = new Vector3(float.MinValue);
        var pts = collect ?? new List<Vector3>();
        int first = pts.Count;
        foreach (var t in samples)
        {
            M2Anim.BoneMatrices(m, new M2Anim.Time(si, seq.Start, t, t), mats);
            foreach (var v in m.Vertices)
            {
                var p = Skin(v, mats);
                mn = Vector3.Min(mn, p);
                mx = Vector3.Max(mx, p);
                pts.Add(p);
            }
        }
        if (pts.Count == first) return (Vector3.Zero, Vector3.Zero, 0);
        var c = (mn + mx) / 2;
        float r = 0;
        for (int i = first; i < pts.Count; i++) r = Math.Max(r, Vector3.Distance(pts[i], c));
        if (collect == null) pts.Clear();
        return (mn, mx, r);
    }

    public static Vector3 Skin(in M2Vertex v, Matrix4x4[] mats)
    {
        if (mats.Length == 0) return v.Position;
        var p = Vector3.Zero;
        int sum = 0;
        var pos = v.Position;
        Span<(byte W, byte B)> inf = stackalloc (byte, byte)[] { (v.W0, v.B0), (v.W1, v.B1), (v.W2, v.B2), (v.W3, v.B3) };
        foreach (var (w, b) in inf)
        {
            if (w == 0 || b >= mats.Length) continue;
            p += Vector3.Transform(pos, mats[b]) * w;
            sum += w;
        }
        return sum == 0 ? pos : p / sum;
    }

    /// <summary>Write recomputed bounds into the file: every sequence's box and radius, and the model's.</summary>
    public static void Apply(M2File f, M2Model m)
    {
        var (mn, mx, _) = Rest(m);
        var all = new List<Vector3>();
        foreach (var v in m.Vertices) all.Add(v.Position);
        var seqs = f["sequences"];
        for (int si = 0; si < m.Sequences.Length; si++)
        {
            var (smn, smx, sr) = Sequence(m, si, all);
            if (m.Vertices.Length == 0) continue;
            // sequences the original file left without bounds (static doodads) stay that way
            var s = m.Sequences[si];
            if (s.BoundsMin == Vector3.Zero && s.BoundsMax == Vector3.Zero && s.BoundsRadius == 0) continue;
            SetVec3(seqs, si, "bounds_min", smn);
            SetVec3(seqs, si, "bounds_max", smx);
            seqs.SetF32(si, "bounds_radius", sr);
            mn = Vector3.Min(mn, smn);
            mx = Vector3.Max(mx, smx);
        }
        if (m.Vertices.Length == 0) return;
        var c = (mn + mx) / 2;
        float r = 0;
        foreach (var p in all) r = Math.Max(r, Vector3.Distance(p, c));
        SetVec3(f.Header, 0, "bounds_min", mn);
        SetVec3(f.Header, 0, "bounds_max", mx);
        f.Header.SetF32(0, "bounds_radius", r);
    }

    public static void SetVec3(M2Data d, int index, string field, Vector3 v)
    {
        int o = index * d.Elem.Size + d.Rec.Field(field).Offset;
        BinaryPrimitives.WriteSingleLittleEndian(d.Bytes.AsSpan(o), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(d.Bytes.AsSpan(o + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d.Bytes.AsSpan(o + 8), v.Z);
    }
}
