using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.Maps;

/// <summary>
/// An ADT tile split into its chunks for editing and writing back (MVER MHDR MCIN MTEX MMDX MMID MWMO MWID MDDF MODF
/// MCNK x 256; each MCNK into its header and the sub-chunks MCVT MCNR MCLY MCRF MCSH MCAL MCLQ MCSE its header points at).
/// <see cref="Serialize"/> lays the file out again in the same order and recomputes every offset and size the loaders
/// use (MHDR offsets, MCIN offset/size, the MCNK header's sub-chunk offsets and its chunk size), so a sub-chunk that grows
/// or shrinks moves everything after it. Bytes between and after chunks (Blizzard's 13 pad bytes after MCNR, MCLQ's data
/// behind a zero-size header) travel with the chunk before them. An unedited stock tile is written back byte for byte
/// (checked on all 2,429 client tiles by MapTests). A file whose chunks cannot be split (a non-sequential layout) is
/// still readable; it is then written as it was, with in-place edits only.
/// </summary>
public sealed class AdtDocument
{
    private static readonly string[] SubOffsetFields = { "ofsHeight", "ofsNormal", "ofsLayer", "ofsRefs", "ofsAlpha", "ofsShadow", "ofsSndEmitters", "ofsLiquid", "ofsMCCV" };

    /// <summary>A top-level chunk with whatever follows it up to the next chunk.</summary>
    public sealed class TopPiece
    {
        public string Magic = "";
        public byte[] Bytes = Array.Empty<byte>();
        public long OriginalOffset;
        public McnkPiece? Mcnk;
    }

    public sealed class SubPiece
    {
        public string? Magic;
        /// <summary>Offset relative to the MCNK start in the original file.</summary>
        public long OriginalRel;
        public byte[] Bytes = Array.Empty<byte>();
    }

    public sealed class McnkPiece
    {
        public byte[] Header = new byte[MapBytes.McnkHeaderSize];
        /// <summary>Bytes between the header and the first sub-chunk (normally none).</summary>
        public byte[] Lead = Array.Empty<byte>();
        public List<SubPiece> Subs = new();
        /// <summary>Bytes after the MCNK's declared size, up to the next chunk.</summary>
        public byte[] Trail = Array.Empty<byte>();
        /// <summary>Header offset field -> index into <see cref="Subs"/> (fields that point elsewhere are kept as they are).</summary>
        public Dictionary<string, int> FieldSub = new();

        public uint H(string field) => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(Off(field)));
        public void SetH(string field, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(Off(field)), v);
        private static int Off(string field) => MapBytes.McnkFields.First(f => f.Name == field).Offset;

        public SubPiece? Sub(string field) => FieldSub.TryGetValue(field, out var i) ? Subs[i] : null;
    }

    public byte[] Original { get; }
    public string Name { get; }
    public List<TopPiece> Pieces { get; } = new();
    /// <summary>Bytes after the last chunk.</summary>
    public byte[] Tail { get; private set; } = Array.Empty<byte>();
    /// <summary>False when the layout could not be split; <see cref="Serialize"/> then returns the edited original bytes.</summary>
    public bool CanRebuild { get; private set; }
    /// <summary>MCNK i (MCIN order) -> index into <see cref="Pieces"/>.</summary>
    public int[] McnkPieceIndex { get; } = new int[256];
    private readonly byte[] _flat;

    public AdtDocument(byte[] data, string name = "")
    {
        Original = data;
        Name = name;
        _flat = (byte[])data.Clone();
        Array.Fill(McnkPieceIndex, -1);
        CanRebuild = Split();
    }

    public static AdtDocument Load(byte[] data, string name = "") => new(data, name);

    private bool Split()
    {
        var b = Original;
        var top = MapBytes.Chunks(b);
        if (top.Count == 0) return false;
        for (int i = 0; i < top.Count; i++)
        {
            var t = top[i];
            long end = t.Offset + 8 + t.Size;
            if (end > b.Length) return false;
            long next = i + 1 < top.Count ? top[i + 1].Offset : b.Length;
            if (i + 1 == top.Count && next > end) Tail = MapBytes.Slice(b, end, next);
            var piece = new TopPiece { Magic = t.Magic ?? "", OriginalOffset = t.Offset };
            if (t.Magic == "MCNK" && t.Size >= MapBytes.McnkHeaderSize)
            {
                piece.Mcnk = SplitMcnk(b, t.Offset, end, i + 1 < top.Count ? next : end);
                if (piece.Mcnk == null) return false;
            }
            piece.Bytes = MapBytes.Slice(b, t.Offset, i + 1 < top.Count ? next : end);
            Pieces.Add(piece);
        }
        // MCIN -> pieces
        var adt = new AdtFile(b);
        for (int i = 0; i < adt.Mcin.Count && i < 256; i++)
        {
            int k = Pieces.FindIndex(p => p.OriginalOffset == adt.Mcin[i].Offset && p.Magic == "MCNK");
            if (k < 0) return false;
            McnkPieceIndex[i] = k;
        }
        // MHDR offsets must point at chunk starts (or be zero)
        int mhdr = Pieces.FindIndex(p => p.Magic == "MHDR");
        if (mhdr != 1 || adt.Mhdr.Count == 0) return false;
        foreach (var (field, _) in MapBytes.MhdrChunks)
        {
            uint ofs = adt.Mhdr[field];
            if (ofs == 0) continue;
            if (!Pieces.Any(p => p.OriginalOffset == adt.MhdrData + ofs)) return false;
        }
        return true;
    }

    private static McnkPiece? SplitMcnk(byte[] b, long start, long end, long next)
    {
        var m = new McnkPiece { Header = MapBytes.Slice(b, start + 8, start + 8 + MapBytes.McnkHeaderSize) };
        long dataStart = 8 + MapBytes.McnkHeaderSize, total = end - start;
        var starts = new SortedSet<long>();
        foreach (var f in SubOffsetFields)
        {
            long o = m.H(f);
            if (o >= dataStart && o < total) starts.Add(o);
        }
        var list = starts.ToList();
        if (list.Count == 0)
        {
            m.Lead = MapBytes.Slice(b, start + dataStart, end);
        }
        else
        {
            m.Lead = MapBytes.Slice(b, start + dataStart, start + list[0]);
            for (int k = 0; k < list.Count; k++)
            {
                long s = list[k], e = k + 1 < list.Count ? list[k + 1] : total;
                m.Subs.Add(new SubPiece { OriginalRel = s, Magic = MapBytes.MagicAt(b, start + s), Bytes = MapBytes.Slice(b, start + s, start + e) });
            }
            foreach (var f in SubOffsetFields)
            {
                long o = m.H(f);
                int idx = list.IndexOf(o);
                if (idx >= 0) m.FieldSub[f] = idx;
            }
        }
        m.Trail = MapBytes.Slice(b, end, next);
        return m;
    }

    public McnkPiece? Chunk(int index) => index >= 0 && index < 256 && McnkPieceIndex[index] >= 0 ? Pieces[McnkPieceIndex[index]].Mcnk : null;

    // ------------------------------------------------------------------ editing (same-size, in place)

    /// <summary>The 145 heights (9x9 outer + 8x8 inner, row by row: 9, 8, 9, ...) of a chunk, relative to its posZ.</summary>
    public float[] GetHeights(int chunk)
    {
        var sub = Chunk(chunk)?.Sub("ofsHeight") ?? throw new InvalidOperationException($"chunk {chunk} has no heights");
        var h = new float[145];
        for (int i = 0; i < 145; i++) h[i] = BinaryPrimitives.ReadSingleLittleEndian(sub.Bytes.AsSpan(8 + i * 4));
        return h;
    }

    public void SetHeights(int chunk, float[] heights)
    {
        if (heights.Length != 145) throw new ArgumentException("145 heights expected");
        var sub = Chunk(chunk)?.Sub("ofsHeight") ?? throw new InvalidOperationException($"chunk {chunk} has no heights");
        for (int i = 0; i < 145; i++) BinaryPrimitives.WriteSingleLittleEndian(sub.Bytes.AsSpan(8 + i * 4), heights[i]);
    }

    /// <summary>The 145 normals (x, y, z in world space, -127..127) of a chunk.</summary>
    public sbyte[] GetNormals(int chunk)
    {
        var sub = Chunk(chunk)?.Sub("ofsNormal") ?? throw new InvalidOperationException($"chunk {chunk} has no normals");
        return sub.Bytes.AsSpan(8, MapBytes.McnrSize).ToArray().Select(x => (sbyte)x).ToArray();
    }

    public void SetNormals(int chunk, sbyte[] normals)
    {
        var sub = Chunk(chunk)?.Sub("ofsNormal") ?? throw new InvalidOperationException($"chunk {chunk} has no normals");
        for (int i = 0; i < MapBytes.McnrSize; i++) sub.Bytes[8 + i] = (byte)normals[i];
    }

    /// <summary>Recompute the normals of a chunk from its heights (outer vertices by central differences inside the
    /// chunk), as an editor does after sculpting.</summary>
    public void RecomputeNormals(int chunk)
    {
        var h = GetHeights(chunk);
        const float unit = TerrainMath.UnitSize;
        float Outer(int i, int j) => h[Math.Clamp(i, 0, 8) * 17 + Math.Clamp(j, 0, 8)];
        var n = new sbyte[MapBytes.McnrSize];
        int k = 0;
        for (int row = 0; row < 17; row++)
        {
            bool inner = row % 2 == 1;
            int cols = inner ? 8 : 9;
            for (int col = 0; col < cols; col++, k++)
            {
                float ri = inner ? row / 2 + 0.5f : row / 2, cj = inner ? col + 0.5f : col;
                int i0 = (int)MathF.Floor(ri), j0 = (int)MathF.Floor(cj);
                float dhdi = (Outer(i0 + 1, j0) - Outer(i0 - 1 + (inner ? 1 : 0), j0)) / (unit * (inner ? 1 : 2));
                float dhdj = (Outer(i0, j0 + 1) - Outer(i0, j0 - 1 + (inner ? 1 : 0))) / (unit * (inner ? 1 : 2));
                var v = Vector3.Normalize(new Vector3(dhdi, dhdj, 1f));
                n[k * 3] = (sbyte)Math.Clamp(MathF.Round(v.X * 127), -127, 127);
                n[k * 3 + 1] = (sbyte)Math.Clamp(MathF.Round(v.Y * 127), -127, 127);
                n[k * 3 + 2] = (sbyte)Math.Clamp(MathF.Round(v.Z * 127), -127, 127);
            }
        }
        SetNormals(chunk, n);
    }

    /// <summary>The chunk's base height (MCNK posZ); MCVT heights are relative to it.</summary>
    public float PosZ(int chunk) => BitConverter.UInt32BitsToSingle((Chunk(chunk) ?? throw new InvalidOperationException($"no chunk {chunk}")).H("posZ"));

    /// <summary>One MCVT value (relative to posZ) of a chunk, k = 0..144.</summary>
    public void SetHeight(int chunk, int k, float relative)
    {
        var sub = Chunk(chunk)?.Sub("ofsHeight") ?? throw new InvalidOperationException($"chunk {chunk} has no heights");
        BinaryPrimitives.WriteSingleLittleEndian(sub.Bytes.AsSpan(8 + k * 4), relative);
    }

    public float GetHeight(int chunk, int k)
    {
        var sub = Chunk(chunk)?.Sub("ofsHeight") ?? throw new InvalidOperationException($"chunk {chunk} has no heights");
        return BinaryPrimitives.ReadSingleLittleEndian(sub.Bytes.AsSpan(8 + k * 4));
    }

    /// <summary>One MCNR normal (signed bytes x, y, z) of a chunk, k = 0..144.</summary>
    public void SetNormal(int chunk, int k, (sbyte X, sbyte Y, sbyte Z) n)
    {
        var sub = Chunk(chunk)?.Sub("ofsNormal") ?? throw new InvalidOperationException($"chunk {chunk} has no normals");
        sub.Bytes[8 + k * 3] = (byte)n.X;
        sub.Bytes[8 + k * 3 + 1] = (byte)n.Y;
        sub.Bytes[8 + k * 3 + 2] = (byte)n.Z;
    }

    public (sbyte X, sbyte Y, sbyte Z) GetNormal(int chunk, int k)
    {
        var sub = Chunk(chunk)?.Sub("ofsNormal") ?? throw new InvalidOperationException($"chunk {chunk} has no normals");
        return ((sbyte)sub.Bytes[8 + k * 3], (sbyte)sub.Bytes[8 + k * 3 + 1], (sbyte)sub.Bytes[8 + k * 3 + 2]);
    }

    private TopPiece? TopOf(string magic) => Pieces.FirstOrDefault(p => p.Magic == magic);

    /// <summary>Move a doodad placement (MDDF record <paramref name="index"/>) to a new position (placement coordinates).</summary>
    public void SetDoodadPosition(int index, Vector3 pos)
    {
        var p = TopOf("MDDF") ?? throw new InvalidOperationException("no MDDF");
        int o = 8 + index * MapBytes.MddfSize;
        BinaryPrimitives.WriteSingleLittleEndian(p.Bytes.AsSpan(o + 8), pos.X);
        BinaryPrimitives.WriteSingleLittleEndian(p.Bytes.AsSpan(o + 12), pos.Y);
        BinaryPrimitives.WriteSingleLittleEndian(p.Bytes.AsSpan(o + 16), pos.Z);
    }

    public void SetHoles(int chunk, uint holes)
    {
        var c = Chunk(chunk) ?? throw new InvalidOperationException($"no chunk {chunk}");
        c.SetH("holes", holes);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>The file with every offset and size recomputed from the pieces (identical to the original when nothing
    /// changed).</summary>
    public byte[] Serialize()
    {
        if (!CanRebuild) return Original;
        // MCNKs first (their size may change), then the top-level layout
        var bodies = new byte[Pieces.Count][];
        for (int i = 0; i < Pieces.Count; i++)
        {
            var p = Pieces[i];
            bodies[i] = p.Mcnk != null ? BuildMcnk(p.Mcnk) : p.Bytes;
        }
        var newOffsets = new long[Pieces.Count];
        long pos = 0;
        for (int i = 0; i < Pieces.Count; i++)
        {
            newOffsets[i] = pos;
            pos += bodies[i].Length;
        }
        var outBuf = new byte[pos + Tail.Length];
        for (int i = 0; i < Pieces.Count; i++) Buffer.BlockCopy(bodies[i], 0, outBuf, (int)newOffsets[i], bodies[i].Length);
        Buffer.BlockCopy(Tail, 0, outBuf, (int)pos, Tail.Length);
        // MHDR offsets (relative to the MHDR data at MHDR start + 8)
        var orig = new AdtFile(Original);
        int mhdrIdx = 1;
        long mhdrData = newOffsets[mhdrIdx] + 8;
        foreach (var (field, _) in MapBytes.MhdrChunks)
        {
            uint ofs = orig.Mhdr[field];
            if (ofs == 0) continue;
            int k = Pieces.FindIndex(p => p.OriginalOffset == orig.MhdrData + ofs);
            if (k < 0) continue;
            int fi = Array.IndexOf(MapBytes.MhdrFields, field);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan((int)(mhdrData + fi * 4)), (uint)(newOffsets[k] - mhdrData));
        }
        // MCIN entries
        if (orig.Mhdr.GetValueOrDefault("ofsMCIN") is var mcinOfs and > 0)
        {
            int mcinIdx = Pieces.FindIndex(p => p.OriginalOffset == orig.MhdrData + mcinOfs);
            if (mcinIdx >= 0)
            {
                long mcinData = newOffsets[mcinIdx] + 8;
                for (int i = 0; i < orig.Mcin.Count && i < 256; i++)
                {
                    int k = McnkPieceIndex[i];
                    if (k < 0) continue;
                    long e = mcinData + i * 16L;
                    BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan((int)e), (uint)newOffsets[k]);
                    // MCIN size = the MCNK chunk's size + 8 (only rewritten when the original followed that rule)
                    var oc = orig.Mcnk[i];
                    if (oc?.Size is { } os && orig.Mcin[i].Size == os + 8)
                        BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan((int)e + 4), MapBytes.U32(bodies[k], 4) + 8);
                }
            }
        }
        return outBuf;
    }

    private static byte[] BuildMcnk(McnkPiece m)
    {
        var header = (byte[])m.Header.Clone();
        var ms = new MemoryStream();
        ms.Write(new byte[8]);
        ms.Write(header);
        ms.Write(m.Lead);
        var subStart = new long[m.Subs.Count];
        for (int k = 0; k < m.Subs.Count; k++)
        {
            subStart[k] = ms.Position;
            ms.Write(m.Subs[k].Bytes);
        }
        long declaredEnd = ms.Position;
        ms.Write(m.Trail);
        var buf = ms.ToArray();
        // chunk header: magic (reversed) + size of the MCNK body (header + sub-chunks, without the trailing gap)
        buf[0] = (byte)'K'; buf[1] = (byte)'N'; buf[2] = (byte)'C'; buf[3] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)(declaredEnd - 8));
        foreach (var (field, idx) in m.FieldSub)
        {
            int off = 8 + MapBytes.McnkFields.First(f => f.Name == field).Offset;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), (uint)subStart[idx]);
        }
        return buf;
    }
}

/// <summary>Sizes and coordinate conversions of the 1.12 terrain.</summary>
public static class TerrainMath
{
    public const float TileSize = 1600f / 3f;            // 533.33 yards
    public const float ChunkSize = TileSize / 16f;       // 33.33 yards
    public const float UnitSize = ChunkSize / 8f;        // 4.17 yards between outer vertices
    public const float MapHalf = 32f * TileSize;         // 17066.67

    /// <summary>MDDF / MODF placement coordinates (a, height, b) -> world (x north, y west, z up).</summary>
    public static Vector3 PlacementToWorld(Vector3 p) => new(MapHalf - p.Z, MapHalf - p.X, p.Y);

    /// <summary>The placement of a doodad or WMO as a world matrix (row-vector convention, System.Numerics): model space
    /// (x forward, y left, z up) -> world. The rotation follows the placement record (degrees): the client's convention,
    /// checked against the MODF bounding boxes of the stock tiles (MapTests): in placement axes (a, h, b) the model is
    /// turned by Ry(ry - 90) * Rz(-rx) * Rx(rz) with its axes taken as (x, z, -y).</summary>
    public static Matrix4x4 PlacementMatrix(Vector3 pos, Vector3 rotDeg, float scale)
    {
        static Matrix4x4 Ry(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new Matrix4x4(c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1); }
        static Matrix4x4 Rz(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new Matrix4x4(c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); }
        static Matrix4x4 Rx(float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new Matrix4x4(1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1); }
        const float D = MathF.PI / 180f;
        // column-vector R = Ry * Rz * Rx  ==  row-vector  Rx^T * Rz^T * Ry^T (the helpers above are already transposed)
        var r = Rx(rotDeg.Z * D) * Rz(-rotDeg.X * D) * Ry((rotDeg.Y - 90f) * D);
        // model (x, y, z) -> placement-local (x, z, -y)
        var fix = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        // placement (a, h, b) -> world (-b, -a, h) + (MapHalf, MapHalf, 0)
        var toWorld = new Matrix4x4(0, -1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0, MapHalf, MapHalf, 0, 1);
        var t = Matrix4x4.CreateTranslation(pos);
        return Matrix4x4.CreateScale(scale) * fix * r * t * toWorld;
    }
}
