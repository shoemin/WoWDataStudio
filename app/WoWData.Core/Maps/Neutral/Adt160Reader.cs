using System.Buffers.Binary;
using System.Numerics;
using WoWData.Core.Casc;

namespace WoWData.Core.Maps.Neutral;

/// <summary>The files of one modern (1.60) split tile. Any of them may be missing (null).</summary>
public sealed class Adt160Files
{
    public byte[]? Root { get; set; }
    public byte[]? Obj0 { get; set; }
    public byte[]? Obj1 { get; set; }
    public byte[]? Tex0 { get; set; }
    public byte[]? Lod { get; set; }
    /// <summary>The tile's 8 MAID FileDataIDs (root, obj0, obj1, tex0, lod, map texture, map normal texture, minimap):
    /// the ones read from, or the ones a writer gave the files it wrote (null when unknown).</summary>
    public uint[]? Maid { get; set; }
}

/// <summary>
/// 1.60 (modern Classic, 1.60.1.70450) map files into the neutral model: a tile's split files (root, obj0, obj1, tex0,
/// lod), the WDT (MPHD, MAIN, MAID, MAI2) and the WDL. Formats as measured on every 1.60 continent and instance tile
/// (docs\research\MAP_SURVEY_160.md, and this milestone's round trip: <see cref="Adt160Writer"/> writes every read tile
/// back byte for byte). Labels: MEASURED = checked on the real files; FORMAT KNOWLEDGE = public format facts not checked here.
///
/// root: <c>MVER MHDR [MH2O] MCNK x 256 [MBMH MBBB MBMI MBNV]</c>. MHDR: only ofsMH2O (relative to MHDR data) is set
/// (MEASURED). MCNK: the 0x80-byte 1.12 header (flags, index, area, low-quality map, no-effect bits, position as in 1.12;
/// with flag 0x10000 the 8 x 8 holes replace ofsHeight/ofsNormal at 0x14; nLayers, ref counts and the layer/alpha/
/// shadow offsets are 0; ofsSndEmitters and ofsLiquid both point at the MCSE header, ofsMCCV at MCCV), then
/// <c>MCVT [MCCV] MCNR MCSE [MPTX] [MCBB]</c>, MCNR's size 448 = 435 normals + 13 pad bytes.
/// tex0: <c>MVER MAMP MDID MHID MCNK x 256</c>, each MCNK without header: <c>MCLY [MCSH] [MCAL]</c>. MDID names each
/// ground texture by the FileDataID of its <c>_s.blp</c> (diffuse + specular) file (MEASURED: 10,889 of 10,889 named
/// entries); the neutral <see cref="AssetRef.Path"/> is the plain diffuse name (1.12's spelling, "_s" removed) and
/// <see cref="AssetRef.FileDataId"/> the <c>_s.blp</c> ID. Alpha: compressed (MCLY 0x200), raw 8-bit (MPHD 0x4 or 0x80) or
/// 4-bit as in 1.12 (instance maps, MPHD 0x248).
/// obj0: <c>MVER MDDF MODF [MLMB] MCNK x 256</c>, MCNK without header: <c>[MCRD] [MCRW]</c> (doodad / WMO refs). MDDF
/// flag 0x40 and MODF flag 0x8 = the name field is a FileDataID (every 1.60 placement; MEASURED). The model lists
/// (<see cref="NeutralTile.DoodadModels"/>, <see cref="NeutralTile.WmoModels"/>) are built in first-use order.
/// obj1 and lod are kept opaque (<see cref="NeutralTile.Obj1Blob"/>, <see cref="NeutralTile.LodBlob"/>).
/// MH2O: see <see cref="ReadMh2o"/>.
/// </summary>
public static class Adt160Reader
{
    /// <summary>MAID slot names.</summary>
    public static readonly string[] MaidSlots = { "root", "obj0", "obj1", "tex0", "lod", "map texture", "map normal texture", "minimap" };

    /// <summary>8-bit alpha maps: MPHD 0x4 (big alpha) or 0x80 (height texturing, which modern clients read as 8-bit
    /// too: MEASURED, the 1.60 continents have 0x80 without 0x4 and store 8-bit alpha).</summary>
    public static bool BigAlpha(uint mphdFlags) => (mphdFlags & 0x84) != 0;

    // ====================================================================== tile

    /// <summary>Reads a tile by its MAID FileDataIDs from a CASC storage. <paramref name="names"/> fills the paths (the
    /// storage's own listfile is asked too).</summary>
    public static NeutralTile ReadTile(CascStorage storage, NeutralMap map, int x, int y, IAssetNames? names = null)
    {
        var maid = map.Tile(x, y).Maid ?? throw new InvalidDataException($"{map.Name} tile {x}_{y} has no MAID entry");
        var files = new Adt160Files { Maid = maid };
        byte[]? Get(int slot) => maid[slot] == 0 ? null : storage.ReadFileDataId(maid[slot]);
        files.Root = Get(0);
        files.Obj0 = Get(1);
        files.Obj1 = Get(2);
        files.Tex0 = Get(3);
        files.Lod = Get(4);
        return ReadTile(files, map.Name, x, y, BigAlpha(map.Flags), new CompositeAssetNames(names, new CascAssetNames(storage)));
    }

    /// <summary>Reads a tile from its split files. <paramref name="bigAlpha"/>: the map's alpha depth (<see cref="BigAlpha"/>
    /// of the WDT's MPHD flags).</summary>
    public static NeutralTile ReadTile(Adt160Files files, string map, int x, int y, bool bigAlpha, IAssetNames? names = null)
    {
        // 8-bit maps store blend weights, 4-bit maps sequential alphas (MEASURED, see AlphaBlendMode)
        var t = new NeutralTile { Map = map, X = x, Y = y, Source = MapSourceVersion.V160, AlphaBlend = bigAlpha ? AlphaBlendMode.Weights : AlphaBlendMode.Sequential };
        var rootOrder = new List<int>();
        if (files.Root != null) ReadRoot(files.Root, t, rootOrder);
        if (files.Tex0 != null) ReadTex0(files.Tex0, t, rootOrder, bigAlpha, names);
        if (files.Obj0 != null) ReadObj0(files.Obj0, t, rootOrder, names);
        t.Obj1Blob = files.Obj1;
        t.LodBlob = files.Lod;
        return t;
    }

    /// <summary>Location tags of <see cref="RawChunk.Location"/>: a file name, "+" = after the file's MCNKs.</summary>
    private static string Where(string file, bool afterMcnk) => afterMcnk ? file + "+" : file;

    private static RawChunk Raw(byte[] b, MapBytes.TopChunk c, string? location) =>
        new(c.Magic ?? "????", MapBytes.Slice(b, c.Offset + 8, c.Offset + 8 + c.Size)) { Location = location };

    // ------------------------------------------------------------------ root

    private static void ReadRoot(byte[] b, NeutralTile t, List<int> order)
    {
        bool seenMcnk = false;
        MapBytes.TopChunk? mh2o = null;
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic == null || c.Offset + 8 + c.Size > b.Length) continue;
            long d = c.Offset + 8;
            switch (c.Magic)
            {
                case "MVER":
                    t.Version = MapBytes.U32(b, d);
                    break;
                case "MHDR":
                    t.HeaderFlags = MapBytes.U32(b, d);   // the offsets are recomputed by the writer
                    break;
                case "MH2O":
                    mh2o = c;
                    break;
                case "MCNK":
                    seenMcnk = true;
                    var ch = ReadRootChunk(b, c);
                    int idx = ch.IndexX < 16 && ch.IndexY < 16 ? ch.IndexY * 16 + ch.IndexX : order.Count;
                    if (idx >= 256 || t.Chunks[idx] != null) idx = Enumerable.Range(0, 256).First(i => t.Chunks[i] == null);
                    t.Chunks[idx] = ch;
                    order.Add(idx);
                    break;
                case "MFBO" when c.Size >= 36:
                    t.FlightBounds = new short[18];
                    for (int k = 0; k < 18; k++) t.FlightBounds[k] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan((int)d + k * 2));
                    break;
                default:
                    t.ExtraChunks.Add(Raw(b, c, Where("root", seenMcnk)));
                    break;
            }
        }
        if (mh2o is { } m) ReadMh2o(b, m.Offset + 8, m.Size, t);
    }

    private static NeutralChunk ReadRootChunk(byte[] b, MapBytes.TopChunk c)
    {
        long h = c.Offset + 8, end = Math.Min(b.Length, c.Offset + 8 + c.Size);
        uint flags = MapBytes.U32(b, h);
        var ch = new NeutralChunk
        {
            Flags = flags, IndexX = (int)MapBytes.U32(b, h + 4), IndexY = (int)MapBytes.U32(b, h + 8), AreaId = MapBytes.U32(b, h + 0x34),
            Holes = (flags & 0x10000) != 0 ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)h + 0x14, 8))
                                           : NeutralChunk.ExpandLowResHoles(MapBytes.U32(b, h + 0x3C) & 0xFFFF),
            LowQualityTextureMap = MapBytes.Slice(b, h + 0x40, h + 0x50),
            NoEffectDoodad = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)h + 0x50, 8)),
            Position = new Vector3(MapBytes.F32(b, h + 0x68), MapBytes.F32(b, h + 0x6C), MapBytes.F32(b, h + 0x70)),
        };
        uint nSnd = MapBytes.U32(b, h + 0x5C);
        foreach (var s in MapBytes.Chunks(b, h + MapBytes.McnkHeaderSize, end))
        {
            if (s.Magic == null || s.Offset + 8 + s.Size > end) break;
            long d = s.Offset + 8;
            switch (s.Magic)
            {
                case "MCVT" when s.Size >= MapBytes.McvtSize:
                    for (int i = 0; i < 145; i++) ch.Heights[i] = MapBytes.F32(b, d + i * 4);
                    break;
                case "MCNR" when s.Size >= MapBytes.McnrSize:
                    for (int i = 0; i < MapBytes.McnrSize; i++) ch.Normals[i] = (sbyte)b[d + i];
                    if (s.Size >= MapBytes.McnrSize + MapBytes.McnrPad)
                    {
                        var pad = MapBytes.Slice(b, d + MapBytes.McnrSize, d + MapBytes.McnrSize + MapBytes.McnrPad);
                        if (pad.Any(v => v != 0)) ch.NormalPad112 = pad;
                    }
                    break;
                case "MCCV":
                    ch.VertexColors = MapBytes.Slice(b, d, d + s.Size);
                    break;
                case "MCLV":
                    ch.VertexLighting = MapBytes.Slice(b, d, d + s.Size);
                    break;
                case "MCSE":
                    ReadEmitters(b, d, s.Size, nSnd, ch);
                    break;
                default:
                    ch.ExtraSubChunks.Add(Raw(b, s, null));
                    break;
            }
        }
        return ch;
    }

    /// <summary>MCSE: WotLK+ records of 28 bytes (id, position, size; FORMAT KNOWLEDGE) or 1.12's 52-byte records (kept
    /// whole). 1.60 MCSE is empty in every chunk (MEASURED).</summary>
    private static void ReadEmitters(byte[] b, long d, long size, uint n, NeutralChunk ch)
    {
        if (n == 0 || size == 0) return;
        int rec = size == n * MapBytes.McseSize ? MapBytes.McseSize : 28;
        for (int k = 0; k < n && (k + 1) * rec <= size; k++)
        {
            long e = d + k * rec;
            var em = new NeutralSoundEmitter { Id = MapBytes.U32(b, e) };
            if (rec == 28)
            {
                em.Position = new Vector3(MapBytes.F32(b, e + 4), MapBytes.F32(b, e + 8), MapBytes.F32(b, e + 12));
                em.Size = new Vector3(MapBytes.F32(b, e + 16), MapBytes.F32(b, e + 20), MapBytes.F32(b, e + 24));
            }
            else
            {
                em.Position = new Vector3(MapBytes.F32(b, e + 8), MapBytes.F32(b, e + 12), MapBytes.F32(b, e + 16));
                em.Raw112 = MapBytes.Slice(b, e, e + MapBytes.McseSize);
            }
            ch.SoundEmitters.Add(em);
        }
    }

    // ------------------------------------------------------------------ MH2O

    /// <summary>
    /// MH2O (relative offsets from the chunk data start): 256 headers of 12 bytes {ofsInstances, count, ofsAttributes} in
    /// chunk order; instances of 24 bytes {u16 LiquidType, u16 second field, f32 min, f32 max, u8 x, y, width, height,
    /// u32 ofsExists, u32 ofsVertexData}; attributes = u64 fishable + u64 deep per chunk (bit row * 8 + col); the exists
    /// bitmap covers the rectangle (bit row * width + col, ofsExists 0 = every tile of the rectangle).
    /// <para>The second field is a LiquidObject ID in 1.60 (MEASURED: always &gt;= 42), not the vertex format, so the format
    /// is taken from the byte count up to the next referenced block (MEASURED reliable: 5, 8, 1, 9 bytes per vertex =
    /// LVF 0 height + depth, 1 height + uv, 2 depth, 3 height + uv + depth; arrays one after the other). A value 0..3
    /// that fits the byte count is read as the vertex format itself (WotLK, and <see cref="Adt160Writer"/> without a
    /// LiquidObject).</para>
    /// <para>No vertex heights (no vertex data, or depth only): the surface is flat. MEASURED: in all 17,348 such 1.60
    /// instances (instance maps; the continents have none) min == max, and where 1.12 has a layer in the same chunk
    /// (6,469) its 81 heights all equal that value, so min and max agree on every measured case and the choice changes
    /// nothing for real data. The model keeps its convention (flat at <see cref="NeutralLiquid.Min"/>, as the 1.12 writer
    /// assumes); the 1.60 writer stores min and max as they are, so a flat instance with min != max (none measured) would
    /// still round-trip.</para>
    /// <para>Fishable / deep are per chunk in MH2O: copied onto every instance of the chunk (the writer merges them).</para>
    /// </summary>
    public static void ReadMh2o(byte[] b, long d, long size, NeutralTile t)
    {
        long End(long rel) => d + rel;
        // every referenced offset, to measure the vertex blocks
        var offs = new SortedSet<long> { size };
        for (int i = 0; i < 256; i++)
        {
            long hb = d + i * 12;
            if (hb + 12 > d + size) break;
            uint oi = MapBytes.U32(b, hb), n = MapBytes.U32(b, hb + 4), oa = MapBytes.U32(b, hb + 8);
            if (oi != 0) offs.Add(oi);
            if (oa != 0) offs.Add(oa);
            for (int k = 0; k < n; k++)
            {
                long ip = d + oi + k * 24;
                if (ip + 24 > d + size) break;
                uint oe = MapBytes.U32(b, ip + 16), ov = MapBytes.U32(b, ip + 20);
                if (oe != 0) offs.Add(oe);
                if (ov != 0) offs.Add(ov);
            }
        }
        for (int i = 0; i < 256; i++)
        {
            long hb = d + i * 12;
            if (hb + 12 > d + size) break;
            uint oi = MapBytes.U32(b, hb), n = MapBytes.U32(b, hb + 4), oa = MapBytes.U32(b, hb + 8);
            if (n == 0 && oa == 0) continue;
            var ch = t.Chunks[i] ??= NeutralChunk.CreateFlat(t.X, t.Y, i % 16, i / 16, 0f);
            ch.Mh2oAttributes160 = oa != 0;
            ulong fish = 0, deep = 0;
            if (oa != 0 && oa + 16 <= size)
            {
                fish = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)End(oa), 8));
                deep = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)End(oa) + 8, 8));
            }
            for (int k = 0; k < n; k++)
            {
                long ip = d + oi + k * 24;
                if (ip + 24 > d + size) break;
                int rx = b[ip + 12], ry = b[ip + 13], rw = b[ip + 14], rh = b[ip + 15];
                var l = new NeutralLiquid
                {
                    Origin = LiquidOrigin.Mh2o,
                    LiquidType = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)ip)),
                    LiquidObjectOrVertexFormat = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)ip + 2)),
                    Min = MapBytes.F32(b, ip + 4), Max = MapBytes.F32(b, ip + 8),
                    RectX = rx, RectY = ry, RectW = rw, RectH = rh, Fishable = fish, Deep = deep,
                };
                uint oe = MapBytes.U32(b, ip + 16), ov = MapBytes.U32(b, ip + 20);
                ulong exists = 0;
                for (int yy = 0; yy < rh; yy++)
                    for (int xx = 0; xx < rw; xx++)
                    {
                        int bit = yy * rw + xx;
                        bool ex = oe == 0 || (b[End(oe) + bit / 8] >> (bit % 8) & 1) != 0;
                        if (ex && ry + yy < 8 && rx + xx < 8) exists |= 1UL << ((ry + yy) * 8 + rx + xx);
                    }
                l.Exists = exists;
                if (ov != 0)
                {
                    int nv = (rw + 1) * (rh + 1);
                    long avail = offs.GetViewBetween(ov + 1, long.MaxValue).Min - ov;
                    int lvf = VertexFormat(l.LiquidObjectOrVertexFormat!.Value, avail, nv);
                    long p = End(ov);
                    if (lvf is 0 or 1 or 3)
                    {
                        l.Heights = new float[nv];
                        for (int v = 0; v < nv; v++) l.Heights[v] = MapBytes.F32(b, p + v * 4);
                        p += nv * 4;
                    }
                    if (lvf is 1 or 3)
                    {
                        l.TexU = new ushort[nv];
                        l.TexV = new ushort[nv];
                        for (int v = 0; v < nv; v++)
                        {
                            l.TexU[v] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)(p + v * 4)));
                            l.TexV[v] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)(p + v * 4 + 2)));
                        }
                        p += nv * 4;
                    }
                    if (lvf is 0 or 2 or 3) l.Depth = MapBytes.Slice(b, p, p + nv);
                }
                ch.Liquids.Add(l);
            }
        }
    }

    /// <summary>Bytes per vertex of each liquid vertex format (LVF 0..3).</summary>
    public static readonly int[] LvfBytesPerVertex = { 5, 8, 1, 9 };

    private static int VertexFormat(int second, long availableBytes, int nv)
    {
        if (second <= 3 && LvfBytesPerVertex[second] * nv <= availableBytes) return second;
        double bpv = (double)availableBytes / nv;
        return bpv >= 9 ? 3 : bpv >= 8 ? 1 : bpv >= 5 ? 0 : 2;
    }

    // ------------------------------------------------------------------ tex0

    private static void ReadTex0(byte[] b, NeutralTile t, List<int> order, bool bigAlpha, IAssetNames? names)
    {
        int k = 0;
        bool seenMcnk = false;
        var heightIds = new List<uint>();
        var mtxp = new List<(uint Flags, float Scale, float Offset)>();
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic == null || c.Offset + 8 + c.Size > b.Length) continue;
            long d = c.Offset + 8;
            switch (c.Magic)
            {
                case "MVER":
                    break;
                case "MAMP":
                    // texture amplifier: 0 (4 bytes) in every 1.60 tile (MEASURED); anything else is kept opaque
                    if (c.Size != 4 || MapBytes.U32(b, d) != 0) t.ExtraChunks.Add(Raw(b, c, "tex0"));
                    break;
                case "MDID":
                    for (int i = 0; i + 4 <= c.Size; i += 4) t.Textures.Add(GroundTexture(MapBytes.U32(b, d + i), names));
                    break;
                case "MHID":
                    for (int i = 0; i + 4 <= c.Size; i += 4) heightIds.Add(MapBytes.U32(b, d + i));
                    break;
                case "MTXP":
                    for (int i = 0; i + 16 <= c.Size; i += 16) mtxp.Add((MapBytes.U32(b, d + i), MapBytes.F32(b, d + i + 4), MapBytes.F32(b, d + i + 8)));
                    break;
                case "MCNK":
                    seenMcnk = true;
                    int idx = k < order.Count ? order[k] : k;
                    k++;
                    if (idx >= 256) break;
                    var ch = t.Chunks[idx] ??= NeutralChunk.CreateFlat(t.X, t.Y, idx % 16, idx / 16, 0f);
                    ReadTexChunk(b, d, d + c.Size, t, ch, bigAlpha);
                    break;
                default:
                    t.ExtraChunks.Add(Raw(b, c, Where("tex0", seenMcnk)));
                    break;
            }
        }
        if (heightIds.Any(v => v != 0) || mtxp.Count > 0)
            for (int i = 0; i < Math.Max(t.Textures.Count, Math.Max(heightIds.Count, mtxp.Count)); i++)
            {
                var p = new TextureParams();
                if (i < heightIds.Count && heightIds[i] != 0) p.HeightTexture = new AssetRef(names?.PathOf(heightIds[i]), heightIds[i]);
                if (i < mtxp.Count) (p.Flags, p.HeightScale, p.HeightOffset) = mtxp[i];
                t.TextureParams.Add(p);
            }
    }

    /// <summary>A ground texture by its MDID FileDataID; the path without the <c>_s</c> of the diffuse + specular file.</summary>
    private static AssetRef GroundTexture(uint fdid, IAssetNames? names)
    {
        var p = fdid == 0 ? null : names?.PathOf(fdid);
        if (p != null && p.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase)) p = p[..^6] + p[^4..];
        return new AssetRef(p, fdid == 0 ? null : fdid);
    }

    private static void ReadTexChunk(byte[] b, long start, long end, NeutralTile t, NeutralChunk ch, bool bigAlpha)
    {
        long mcly = -1, mcal = -1, mclySize = 0, mcalEnd = 0;
        foreach (var s in MapBytes.Chunks(b, start, end))
        {
            if (s.Magic == null || s.Offset + 8 + s.Size > end) break;
            long d = s.Offset + 8;
            switch (s.Magic)
            {
                case "MCLY":
                    mcly = d;
                    mclySize = s.Size;
                    break;
                case "MCAL":
                    mcal = d;
                    mcalEnd = d + s.Size;
                    break;
                case "MCSH" when s.Size >= MapBytes.McshSize:
                    var sh = new bool[4096];
                    for (int k = 0; k < 4096; k++) sh[k] = (b[d + k / 8] & (1 << (k % 8))) != 0;
                    ch.Shadow = sh;
                    break;
                default:
                    ch.ExtraSubChunks.Add(Raw(b, s, "tex0"));
                    break;
            }
        }
        if (mcly < 0) return;
        for (int L = 0; L * 16 + 16 <= mclySize; L++)
        {
            long q = mcly + L * 16;
            uint tid = MapBytes.U32(b, q), lf = MapBytes.U32(b, q + 4), oa = MapBytes.U32(b, q + 8), eff = MapBytes.U32(b, q + 12);
            var layer = new NeutralLayer { Texture = tid < t.Textures.Count ? t.Textures[(int)tid] : new AssetRef(), Flags = lf, EffectId = eff };
            if ((lf & 0x100) != 0 && mcal >= 0 && mcal + oa < mcalEnd)
            {
                long a = mcal + oa;
                if ((lf & 0x200) != 0) layer.Alpha = DecodeAlpha(b, a, mcalEnd, out _);
                else if (bigAlpha) layer.Alpha = MapBytes.Slice(b, a, Math.Min(a + 4096, mcalEnd)) is { Length: 4096 } raw ? raw : null;
                else if (a + MapBytes.AlphaLayerSize <= mcalEnd)
                {
                    var alpha = new byte[4096];
                    for (int k = 0; k < MapBytes.AlphaLayerSize; k++)
                    {
                        byte v = b[a + k];
                        alpha[k * 2] = NeutralLayer.Expand4(v & 0xF);      // low nibble first, as 1.12
                        alpha[k * 2 + 1] = NeutralLayer.Expand4(v >> 4);
                    }
                    layer.Alpha = alpha;
                }
            }
            ch.Layers.Add(layer);
        }
    }

    /// <summary>
    /// Compressed alpha (MCLY 0x200; FORMAT KNOWLEDGE, confirmed: every 1.60 layer decodes to exactly 4,096 values): a
    /// control byte, bit 0x80 = fill (the next byte repeated n times) else copy (the next n bytes), n = low 7 bits.
    /// </summary>
    public static byte[] DecodeAlpha(byte[] b, long start, long end, out long next)
    {
        var v = new byte[4096];
        long q = start;
        int o = 0;
        while (o < 4096 && q < end)
        {
            byte c = b[q++];
            int n = c & 0x7F;
            if ((c & 0x80) != 0)
            {
                if (q >= end) break;
                byte val = b[q++];
                for (int k = 0; k < n && o < 4096; k++) v[o++] = val;
            }
            else
                for (int k = 0; k < n && o < 4096 && q < end; k++) v[o++] = b[q++];
        }
        next = q;
        return v;
    }

    // ------------------------------------------------------------------ obj0

    private static void ReadObj0(byte[] b, NeutralTile t, List<int> order, IAssetNames? names)
    {
        int k = 0;
        bool seenMcnk = false;
        var mmdx = new Dictionary<uint, string>();
        var mwmo = new Dictionary<uint, string>();
        uint[] mmid = Array.Empty<uint>(), mwid = Array.Empty<uint>();
        var doodadRecs = new List<(long Pos, byte[] B)>();
        var wmoRecs = new List<(long Pos, byte[] B)>();
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic == null || c.Offset + 8 + c.Size > b.Length) continue;
            long d = c.Offset + 8;
            switch (c.Magic)
            {
                case "MVER":
                    break;
                case "MMDX":
                    foreach (var kv in MapBytes.ZStrings(MapBytes.Slice(b, d, d + c.Size))) mmdx.TryAdd(kv.Key, kv.Value);
                    break;
                case "MWMO":
                    foreach (var kv in MapBytes.ZStrings(MapBytes.Slice(b, d, d + c.Size))) mwmo.TryAdd(kv.Key, kv.Value);
                    break;
                case "MMID":
                    mmid = Enumerable.Range(0, (int)c.Size / 4).Select(i => MapBytes.U32(b, d + i * 4)).ToArray();
                    break;
                case "MWID":
                    mwid = Enumerable.Range(0, (int)c.Size / 4).Select(i => MapBytes.U32(b, d + i * 4)).ToArray();
                    break;
                case "MDDF":
                    for (long p = d; p + MapBytes.MddfSize <= d + c.Size; p += MapBytes.MddfSize) doodadRecs.Add((p, b));
                    break;
                case "MODF":
                    for (long p = d; p + MapBytes.ModfSize <= d + c.Size; p += MapBytes.ModfSize) wmoRecs.Add((p, b));
                    break;
                case "MCNK":
                    seenMcnk = true;
                    int idx = k < order.Count ? order[k] : k;
                    k++;
                    if (idx >= 256) break;
                    var ch = t.Chunks[idx] ??= NeutralChunk.CreateFlat(t.X, t.Y, idx % 16, idx / 16, 0f);
                    foreach (var s in MapBytes.Chunks(b, d, d + c.Size))
                    {
                        if (s.Magic == null || s.Offset + 8 + s.Size > d + c.Size) break;
                        if (s.Magic is "MCRD" or "MCRW")
                        {
                            var list = s.Magic == "MCRD" ? ch.DoodadRefs : ch.WmoRefs;
                            for (long p = s.Offset + 8; p + 4 <= s.Offset + 8 + s.Size; p += 4) list.Add((int)MapBytes.U32(b, p));
                        }
                        else ch.ExtraSubChunks.Add(Raw(b, s, "obj0"));
                    }
                    break;
                default:
                    t.ExtraChunks.Add(Raw(b, c, Where("obj0", seenMcnk)));
                    break;
            }
        }
        var models = new Dictionary<string, AssetRef>();
        AssetRef Model(uint nameId, bool isFdid, Dictionary<uint, string> block, uint[] ids, List<AssetRef> list)
        {
            string key;
            string? path;
            uint? fdid = null;
            if (isFdid)
            {
                key = "#" + nameId;
                fdid = nameId;
                path = names?.PathOf(nameId);
            }
            else
            {
                path = nameId < ids.Length && block.TryGetValue(ids[nameId], out var n) ? n : null;
                key = "@" + nameId;
            }
            key = (list == t.DoodadModels ? "d" : "w") + key;
            if (!models.TryGetValue(key, out var a))
            {
                a = new AssetRef(path, fdid);
                models[key] = a;
                list.Add(a);
            }
            return a;
        }
        foreach (var (p, _) in doodadRecs)
        {
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 34));
            t.Doodads.Add(new NeutralDoodad
            {
                Model = Model(MapBytes.U32(b, p), (flags & 0x40) != 0, mmdx, mmid, t.DoodadModels), UniqueId = MapBytes.U32(b, p + 4),
                Position = Vec(b, p + 8), Rotation = Vec(b, p + 20), ScaleRaw = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 32)), Flags = flags,
            });
        }
        foreach (var (p, _) in wmoRecs) t.Wmos.Add(ReadModf(b, p, (nameId, isFdid) => Model(nameId, isFdid, mwmo, mwid, t.WmoModels)));
    }

    private static Vector3 Vec(byte[] b, long p) => new(MapBytes.F32(b, p), MapBytes.F32(b, p + 4), MapBytes.F32(b, p + 8));

    private static NeutralWmo ReadModf(byte[] b, long p, Func<uint, bool, AssetRef> model)
    {
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 56));
        return new NeutralWmo
        {
            Model = model(MapBytes.U32(b, p), (flags & 0x8) != 0), UniqueId = MapBytes.U32(b, p + 4),
            Position = Vec(b, p + 8), Rotation = Vec(b, p + 20), ExtentMin = Vec(b, p + 32), ExtentMax = Vec(b, p + 44),
            Flags = flags, DoodadSet = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 58)),
            NameSet = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 60)), ScaleRaw = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p + 62)),
        };
    }

    // ====================================================================== WDT

    /// <summary>
    /// A modern WDT: <c>MVER MPHD MAIN MAID [MAI2]</c> (tile maps) or <c>MVER MPHD MAIN MAID MODF</c> (WMO-only; the MODF
    /// names its WMO by FileDataID, flag 0x8). MPHD = flags + 7 FileDataIDs of the companion files (FORMAT KNOWLEDGE,
    /// consistent with Azeroth: lgt, occ, fogs, mpv, tex, wdl (775970 = Azeroth.wdl by name hash), pd4). MAIN flags: 0x1
    /// = tile exists, 0x2 on every other cell of the 1.60 continents (FORMAT KNOWLEDGE: "all water"). MAID = 8 FileDataIDs
    /// per cell (<see cref="MaidSlots"/>; the minimap also goes to <see cref="NeutralTileEntry.Minimap"/>). MAI2 and any
    /// other chunk are kept opaque.
    /// </summary>
    public static NeutralMap ReadWdt(byte[] b, string name = "", IAssetNames? names = null)
    {
        var m = new NeutralMap { Name = name, Source = MapSourceVersion.V160 };
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic == null || c.Offset + 8 + c.Size > b.Length) continue;
            long d = c.Offset + 8;
            switch (c.Magic)
            {
                case "MVER":
                    m.Version = MapBytes.U32(b, d);
                    break;
                case "MPHD":
                    m.Flags = MapBytes.U32(b, d);
                    for (int k = 0; k < 7 && 4 + k * 4 + 4 <= c.Size; k++) m.MphdExtra[k] = MapBytes.U32(b, d + 4 + k * 4);
                    break;
                case "MAIN":
                    for (int i = 0; i < 4096 && i * 8 + 8 <= c.Size; i++)
                    {
                        var e = m.Tiles[i];
                        e.Flags = MapBytes.U32(b, d + i * 8);
                        e.AsyncId = MapBytes.U32(b, d + i * 8 + 4);
                        e.HasAdt = (e.Flags & 1) != 0;
                    }
                    break;
                case "MAID":
                    int per = (int)(c.Size / 4096 / 4);
                    for (int i = 0; i < 4096; i++)
                    {
                        var ids = new uint[8];
                        for (int k = 0; k < Math.Min(per, 8); k++) ids[k] = MapBytes.U32(b, d + (i * per + k) * 4);
                        if (ids.All(v => v == 0)) continue;
                        m.Tiles[i].Maid = ids;
                        if (ids[7] != 0) m.Tiles[i].Minimap = new AssetRef(names?.PathOf(ids[7]), ids[7]);
                    }
                    break;
                case "MODF" when c.Size >= MapBytes.ModfSize:
                    m.GlobalWmo = ReadModf(b, d, (nameId, isFdid) => isFdid ? new AssetRef(names?.PathOf(nameId), nameId) : new AssetRef());
                    break;
                default:
                    m.ExtraChunks.Add(Raw(b, c, "wdt"));
                    break;
            }
        }
        return m;
    }

    // ====================================================================== WDL

    /// <summary>
    /// A modern WDL (1.60 Azeroth.wdl, MVER 18): <c>MVER MLDD MLDX MLMD MLMX MAOF</c> then per tile <c>MARE [MAOE] MAHO</c>
    /// (MEASURED: 736 MARE, 736 MAHO, 459 MAOE; the four leading lists are empty). MAHO masks are kept only when not
    /// all zero (the writer writes one after every MARE); MAOE goes to <see cref="NeutralWdl.TileExtraChunks"/>.
    /// </summary>
    public static NeutralWdl ReadWdl(byte[] b)
    {
        var w = new NeutralWdl { Source = MapSourceVersion.V160 };
        var tileAt = new Dictionary<long, int>();
        int last = -1;
        foreach (var c in MapBytes.Chunks(b))
        {
            if (c.Magic == null || c.Offset + 8 + c.Size > b.Length) continue;
            long d = c.Offset + 8;
            switch (c.Magic)
            {
                case "MVER":
                    w.Version = MapBytes.U32(b, d);
                    break;
                case "MAOF":
                    for (int i = 0; i < 4096 && i * 4 + 4 <= c.Size; i++)
                        if (MapBytes.U32(b, d + i * 4) is var o && o != 0) tileAt.TryAdd(o, i);
                    break;
                case "MARE":
                    last = tileAt.GetValueOrDefault(c.Offset, -1);
                    if (last >= 0 && c.Size >= MapBytes.WdlMareData)
                    {
                        var h = new short[545];
                        for (int k = 0; k < 545; k++) h[k] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan((int)d + k * 2));
                        w.Heights[last] = h;
                    }
                    break;
                case "MAHO" when last >= 0 && c.Size >= 32:
                    var holes = new ushort[16];
                    for (int k = 0; k < 16; k++) holes[k] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)d + k * 2));
                    if (holes.Any(v => v != 0)) w.Holes[last] = holes;
                    break;
                default:
                    if (last >= 0)
                    {
                        if (!w.TileExtraChunks.TryGetValue(last, out var list)) w.TileExtraChunks[last] = list = new List<RawChunk>();
                        list.Add(Raw(b, c, null));
                    }
                    else w.ExtraChunks.Add(Raw(b, c, "wdl"));
                    break;
            }
        }
        return w;
    }
}
