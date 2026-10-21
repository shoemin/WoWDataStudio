using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.Maps.Neutral;

/// <summary>The LiquidType and LiquidObject IDs of an MH2O instance (null object = not known).</summary>
public readonly record struct Mh2oLiquidIds(ushort LiquidType, ushort? LiquidObject);

/// <summary>Options of the 1.60 writer.</summary>
public sealed class Adt160WriterOptions
{
    /// <summary>The name map: FileDataIDs for assets that have only a path (resolver first, then the allocator), and the
    /// record of every ID written. Share one map across the tiles of a map; null = a fresh map without a resolver (every
    /// path-only asset gets a local ID).</summary>
    public AssetIdMap? Ids { get; set; }
    /// <summary>8-bit alpha holding blend weights (MPHD 0x4 / 0x80, the 1.60 continents) or 4-bit sequential alpha (the
    /// 1.60 instance maps, MPHD 0x248). Null: the tile's own <see cref="NeutralTile.AlphaBlend"/> (weights = 8-bit), so
    /// nothing is converted; for <see cref="Adt160Writer.WriteWdt"/> the map's flags when it comes from 1.60, else 4-bit.
    /// Use the same value for a map's WDT and tiles.</summary>
    public bool? BigAlpha { get; set; }
    /// <summary>Compress 8-bit alpha (MCLY 0x200). Null: keep each layer's 0x200 when the tile comes from 1.60, else
    /// compress (as 95.7% of the 1.60 continent layers are).</summary>
    public bool? CompressAlpha { get; set; }
    /// <summary>LiquidType / LiquidObject for liquids that have no LiquidType (1.12 MCLQ layers), from their 1.12 kind.
    /// Null = <see cref="Adt160Writer.DefaultLiquidType"/> and no LiquidObject. Belongs to the modern LiquidType /
    /// LiquidObject tables later.</summary>
    public Func<NeutralLiquid, LiquidKind, Mh2oLiquidIds?>? LiquidIds { get; set; }
    /// <summary>Write the tile's LOD file when the model has one (<see cref="NeutralTile.LodBlob"/>).</summary>
    public bool WriteLod { get; set; } = true;
}

/// <summary>
/// The neutral model as 1.60 (modern Classic) split map files: root, obj0, obj1, tex0 (+ lod when the model has one), the
/// WDT with MAID, and the WDL. The layout is the one Blizzard's tools wrote in 1.60 (see <see cref="Adt160Reader"/>), so
/// a 1.60 tile read and written back is byte-identical. Files are named by FileDataID: assets that have only a path
/// (content from 1.12) get theirs from <see cref="Adt160WriterOptions.Ids"/> (the user's names, e.g.
/// <see cref="ClassicPathResolver"/>), else a new local ID; the map of IDs is the writer's result for the caller.
/// Reductions and assumptions go into the <see cref="ConversionReport"/>:
/// * ground textures with only a path: MDID = the FileDataID of the <c>_s.blp</c> (diffuse + specular) file, as 1.60 does,
///   else the plain file's (<see cref="LossKind.AssetIdResolved"/>);
/// * 1.12 MCLQ layers become MH2O instances: full 8 x 8 rectangle, exists bitmap only when a tile is dry, vertex format
///   from what the layer has (water: heights + depth = LVF 0; magma / slime: heights + uv = LVF 1); type from the kind
///   (<see cref="DefaultLiquidType"/> or <see cref="Adt160WriterOptions.LiquidIds"/>); flow data dropped;
///   fishable / deep merged per chunk;
/// * alpha: by default in the tile's own blend mode (<see cref="AlphaBlendMode"/>): 1.12 content as 4-bit sequential alpha
///   (the 1.60 instance-map layout, lossless), 1.60 continent content as 8-bit weights; asked for the other depth, the
///   writer converts (sequential to weights loses paint hidden under opaque layers: <see cref="LossKind.AlphaBlendConverted"/>);
/// * 1.12 holes: the 8 x 8 mask, flag 0x10000 (what 1.60 did with every 1.12 hole);
/// * 1.12 sound emitters: 28-byte records (id, position, size): the other 1.12 fields are dropped;
/// * the 1.12 model name lists: placements name their model by FileDataID, so list order and unused entries are lost;
/// * obj1 without source data: written with empty LOD lists; no LOD file: MAID slot empty.
/// </summary>
public static class Adt160Writer
{
    /// <summary>LiquidType per 1.12 kind for liquids without one: water 5 and ocean 2 (what the 1.60 instance maps use for
    /// the 1.12 water and ocean, MEASURED), magma 7 (1.60 continents, MEASURED), slime 8 (ASSUMPTION: no slime in 1.60's
    /// terrain; 8 follows the "slow" variants 5 / 7 of the classic LiquidType table, FORMAT KNOWLEDGE).</summary>
    public static readonly ushort[] DefaultLiquidType = { 5, 2, 7, 8 };

    /// <summary>The paths of a tile's files, used to find or allocate their FileDataIDs.</summary>
    public static string TileFilePath(string map, int x, int y, int slot) => slot switch
    {
        0 => $@"World\Maps\{map}\{map}_{x}_{y}.adt",
        1 => $@"World\Maps\{map}\{map}_{x}_{y}_obj0.adt",
        2 => $@"World\Maps\{map}\{map}_{x}_{y}_obj1.adt",
        3 => $@"World\Maps\{map}\{map}_{x}_{y}_tex0.adt",
        4 => $@"World\Maps\{map}\{map}_{x}_{y}_lod.adt",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    // ====================================================================== tile

    /// <summary>Writes a tile. <paramref name="maid"/>: the FileDataIDs to give its files (a 1.60 source's MAID entry);
    /// null = found or allocated through the name map by <see cref="TileFilePath"/>. The result's <see cref="Adt160Files.Maid"/>
    /// holds the IDs used (slots 5-7 are the WDT's business).</summary>
    public static Adt160Files WriteTile(NeutralTile tile, ConversionReport? report = null, Adt160WriterOptions? options = null, uint[]? maid = null)
    {
        options ??= new Adt160WriterOptions();
        var ids = options.Ids ??= new AssetIdMap();
        var ctx = new Ctx(tile, report, options, ids);
        var files = new Adt160Files
        {
            Root = WriteRoot(ctx),
            Tex0 = WriteTex0(ctx),
            Obj0 = WriteObj0(ctx),
            Obj1 = tile.Obj1Blob ?? MinimalObj1(ctx),
            Lod = options.WriteLod ? tile.LodBlob : null,
        };
        if (tile.LodBlob != null && !options.WriteLod) ctx.Add(LossKind.LodDropped, null, 1, "LOD file not written (option)");
        else if (tile.LodBlob == null) ctx.Add(LossKind.MapFileRefsMissing, null, 1, "no LOD data in the model: no _lod.adt, MAID slot empty");
        ctx.ReportTotals();
        var outIds = new uint[8];
        for (int s = 0; s < 5; s++)
        {
            if (s == 4 && files.Lod == null) continue;
            if (maid is { Length: >= 5 } && maid[s] != 0) outIds[s] = maid[s];
            else outIds[s] = ids.IdOf(TileFilePath(tile.Map.Length > 0 ? tile.Map : "Map", tile.X, tile.Y, s), "tile " + Adt160Reader.MaidSlots[s], out _);
        }
        if (maid is { Length: 8 })
            for (int s = 5; s < 8; s++) outIds[s] = maid[s];
        files.Maid = outIds;
        return files;
    }

    /// <summary>State of one tile write.</summary>
    private sealed class Ctx
    {
        public readonly NeutralTile Tile;
        public readonly ConversionReport? Report;
        public readonly Adt160WriterOptions Options;
        public readonly AssetIdMap Ids;
        public readonly string Name;
        public readonly bool From160;
        public readonly bool BigAlpha;
        public readonly List<AssetRef> Textures;
        private readonly Dictionary<AssetRef, int> _texIndex = new(ReferenceEqualityComparer.Instance);
        public int AlphaLayers, AlphaChunks, Emitters, FlowLayers, MaskChunks, TypeDefaulted, ObjectDefaulted, BlendChunks;
        public long HiddenTexels;
        public readonly Dictionary<string, int> Resolved = new(), Allocated = new(), Missing = new();
        public readonly List<string> AllocatedExamples = new();

        public Ctx(NeutralTile tile, ConversionReport? report, Adt160WriterOptions options, AssetIdMap ids)
        {
            Tile = tile;
            Report = report;
            Options = options;
            Ids = ids;
            Name = Adt112Writer.TileName(tile);
            From160 = tile.Source == MapSourceVersion.V160;
            BigAlpha = options.BigAlpha ?? tile.AlphaBlend == AlphaBlendMode.Weights;
            Textures = new List<AssetRef>(tile.Textures);
            for (int i = 0; i < Textures.Count; i++) _texIndex.TryAdd(Textures[i], i);
        }

        public void Add(LossKind kind, int? chunk, int count, string message) => Report?.Add(kind, Name, chunk, count, message);

        public int TextureIndex(AssetRef a)
        {
            if (_texIndex.TryGetValue(a, out int i)) return i;
            i = Textures.FindIndex(x => x.SameAs(a));
            if (i < 0)
            {
                i = Textures.Count;
                Textures.Add(a);
            }
            _texIndex[a] = i;
            return i;
        }

        /// <summary>The FileDataID to write for an asset (0 when it has neither ID nor path).</summary>
        public uint IdOf(AssetRef a, string kind)
        {
            if (a.FileDataId is { } f)
            {
                Ids.Record(f, a.Path, kind);
                return f;
            }
            if (!a.HasPath)
            {
                Missing[kind] = Missing.GetValueOrDefault(kind) + 1;
                return 0;
            }
            var path = a.Path!;
            string[] lookups = kind == "texture" && path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase)
                ? new[] { path[..^4] + "_s.blp", path }
                : new[] { path };
            uint id = Ids.IdOf(path, kind, out var origin, lookups);
            var d = origin == AssetIdOrigin.Allocated ? Allocated : Resolved;
            d[kind] = d.GetValueOrDefault(kind) + 1;
            if (origin == AssetIdOrigin.Allocated && AllocatedExamples.Count < 5) AllocatedExamples.Add($"{path} -> {id}");
            return id;
        }

        public void ReportTotals()
        {
            static string List(Dictionary<string, int> d) => string.Join(", ", d.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key} uses"));
            if (Resolved.Count > 0)
                Add(LossKind.AssetIdResolved, null, Resolved.Values.Sum(), $"FileDataIDs from the name resolver: {List(Resolved)} (ground textures as their _s.blp file when it exists)");
            if (Allocated.Count > 0)
                Add(LossKind.AssetIdAllocated, null, Allocated.Values.Sum(), $"local FileDataIDs allocated: {List(Allocated)}; e.g. {string.Join("; ", AllocatedExamples)}");
            if (Missing.Count > 0)
                Add(LossKind.AssetMissing, null, Missing.Values.Sum(), $"assets with neither a path nor a FileDataID written as 0: {List(Missing)}");
            if (AlphaLayers > 0) Add(LossKind.AlphaReduced, null, AlphaLayers, $"8-bit alpha reduced to 4 bits on {AlphaLayers} layers in {AlphaChunks} chunks");
            if (BlendChunks > 0)
                Add(LossKind.AlphaBlendConverted, null, BlendChunks, BigAlpha
                    ? $"sequential alphas of {BlendChunks} multi-layer chunks converted to 8-bit blend weights; {HiddenTexels} layer texels had paint hidden under an opaque layer above (dropped)"
                    : $"8-bit blend weights of {BlendChunks} multi-layer chunks converted to sequential alphas");
            if (Emitters > 0) Add(LossKind.SoundEmittersDefaulted, null, Emitters, $"{Emitters} 1.12 sound emitters written as 28-byte records (id, position, size): the other 1.12 fields dropped");
            if (FlowLayers > 0) Add(LossKind.LiquidFlowDropped, null, FlowLayers, $"{FlowLayers} MCLQ layers had flow vectors / flow bytes / irregular tile bytes, which MH2O cannot hold");
            if (MaskChunks > 0) Add(LossKind.LiquidMasksMerged, null, MaskChunks, $"fishable / deep tiles of {MaskChunks} chunks with several liquid layers merged (MH2O has one mask per chunk)");
            if (TypeDefaulted > 0) Add(LossKind.LiquidTypeDefaulted, null, TypeDefaulted, $"{TypeDefaulted} liquid instances got their LiquidType from their 1.12 kind");
            if (ObjectDefaulted > 0) Add(LossKind.LiquidObjectDefaulted, null, ObjectDefaulted, $"{ObjectDefaulted} liquid instances have no LiquidObject ID: the vertex format was written in its place");
            if (Tile.ExtraChunks.Any(c => !KnownLocation(c.Location)))
                Add(LossKind.UnknownChunksDropped, null, Tile.ExtraChunks.Count(c => !KnownLocation(c.Location)),
                    "chunks of another file layout dropped: " + string.Join(" ", Tile.ExtraChunks.Where(c => !KnownLocation(c.Location)).Select(c => c.Magic)));
        }
    }

    private static bool KnownLocation(string? l) => l is "root" or "root+" or "tex0" or "tex0+" or "obj0" or "obj0+";

    private static byte[] Extras(IEnumerable<RawChunk> chunks, string location, string? skipMagic = null)
    {
        var ms = new MemoryStream();
        foreach (var c in chunks)
            if (c.Location == location && c.Magic != skipMagic) WriteChunk(ms, c.Magic, c.Data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ root

    private const uint LiquidFlags112 = 0x4 | 0x8 | 0x10 | 0x20;

    private static byte[] WriteRoot(Ctx ctx)
    {
        var t = ctx.Tile;
        var chunks = new byte[256][];
        for (int i = 0; i < 256; i++) chunks[i] = EncodeRootChunk(ctx, t.Chunks[i], i);
        var mh2o = EncodeMh2o(ctx);
        var head = Extras(t.ExtraChunks, "root");
        var tail = Extras(t.ExtraChunks, "root+");
        var ms = new MemoryStream();
        WriteChunk(ms, "MVER", U32(18));
        long mhdr = ms.Position + 8;
        WriteChunk(ms, "MHDR", new byte[64]);
        uint ofsMh2o = 0, ofsMfbo = 0;
        if (mh2o != null)
        {
            ofsMh2o = (uint)(ms.Position - mhdr);
            WriteChunk(ms, "MH2O", mh2o);
        }
        ms.Write(head);
        foreach (var c in chunks) ms.Write(c);
        if (t.FlightBounds is { Length: 18 } fb)
        {
            ofsMfbo = (uint)(ms.Position - mhdr);
            var d = new byte[36];
            for (int k = 0; k < 18; k++) BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(k * 2), fb[k]);
            WriteChunk(ms, "MFBO", d);
        }
        ms.Write(tail);
        var buf = ms.ToArray();
        uint hflags = (t.HeaderFlags & ~1u) | (ofsMfbo != 0 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)mhdr), hflags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)mhdr + 9 * 4), ofsMfbo);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)mhdr + 10 * 4), ofsMh2o);
        return buf;
    }

    private static byte[] EncodeRootChunk(Ctx ctx, NeutralChunk? ch, int index)
    {
        if (ch == null)
        {
            ctx.Add(LossKind.ChunkMissing, index, 1, "chunk missing in the model: written flat at height 0");
            ch = NeutralChunk.CreateFlat(ctx.Tile.X, ctx.Tile.Y, index % 16, index / 16, 0f);
        }
        uint flags = (ch.Flags & ~LiquidFlags112 & ~0x40u) | 0x10000 | (ch.VertexColors != null ? 0x40u : 0);
        if ((flags & 1) != 0 && ch.Shadow == null)
        {
            flags &= ~1u;
            ctx.Add(LossKind.ShadowMissing, index, 1, "flag 0x1 (shadow map) set without a shadow: cleared");
        }
        var ms = new MemoryStream();
        var hdr = new byte[MapBytes.McnkHeaderSize];
        void H(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(o), v);
        H(0x00, flags);
        H(0x04, (uint)ch.IndexX);
        H(0x08, (uint)ch.IndexY);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x14), ch.Holes);
        H(0x34, ch.AreaId);
        ch.LowQualityTextureMap.AsSpan(0, Math.Min(16, ch.LowQualityTextureMap.Length)).CopyTo(hdr.AsSpan(0x40));
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x50), ch.NoEffectDoodad);
        H(0x5C, (uint)ch.SoundEmitters.Count);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(0x68), ch.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(0x6C), ch.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(0x70), ch.Position.Z);
        ms.Write(new byte[8]);   // MCNK header, filled at the end
        ms.Write(hdr);
        var mcvt = new byte[MapBytes.McvtSize];
        for (int i = 0; i < 145; i++) BinaryPrimitives.WriteSingleLittleEndian(mcvt.AsSpan(i * 4), ch.Heights[i]);
        WriteChunk(ms, "MCVT", mcvt);
        if (ch.VertexColors != null)
        {
            H(0x74, (uint)ms.Position);
            WriteChunk(ms, "MCCV", ch.VertexColors);
        }
        var mcnr = new byte[MapBytes.McnrSize + MapBytes.McnrPad];
        for (int i = 0; i < MapBytes.McnrSize; i++) mcnr[i] = (byte)ch.Normals[i];
        if (ch.NormalPad112 is { Length: MapBytes.McnrPad } pad) pad.CopyTo(mcnr, MapBytes.McnrSize);
        WriteChunk(ms, "MCNR", mcnr);
        if (ch.VertexLighting != null)
        {
            H(0x78, (uint)ms.Position);
            WriteChunk(ms, "MCLV", ch.VertexLighting);
        }
        H(0x58, (uint)ms.Position);
        H(0x60, (uint)ms.Position);
        var mcse = new byte[ch.SoundEmitters.Count * 28];
        for (int k = 0; k < ch.SoundEmitters.Count; k++)
        {
            var e = ch.SoundEmitters[k];
            var s = mcse.AsSpan(k * 28);
            BinaryPrimitives.WriteUInt32LittleEndian(s, e.Id);
            WriteVec(s[4..], e.Position);
            WriteVec(s[16..], e.Size);
            if (e.Raw112 != null) ctx.Emitters++;
        }
        WriteChunk(ms, "MCSE", mcse);
        foreach (var x in ch.ExtraSubChunks)
            if (x.Location is null or "root") WriteChunk(ms, x.Magic, x.Data);
        var buf = ms.ToArray();
        hdr.CopyTo(buf, 8);
        SetHeader(buf, 0, "MCNK", buf.Length - 8);
        return buf;
    }

    /// <summary>MH2O: headers, all instances in chunk order, then per chunk its attributes and per instance its exists
    /// bitmap and vertex data (the order of every 1.60 tile, MEASURED by the round trip). Null when no chunk has liquid.</summary>
    private static byte[]? EncodeMh2o(Ctx ctx)
    {
        var t = ctx.Tile;
        if (!t.AllChunks.Any(c => c.Liquids.Count > 0)) return null;
        int total = t.AllChunks.Sum(c => c.Liquids.Count);
        var head = new byte[256 * 12];
        var inst = new byte[total * 24];
        var data = new MemoryStream();
        int instBase = head.Length, dataBase = head.Length + inst.Length, n = 0;
        for (int i = 0; i < 256; i++)
        {
            var ch = t.Chunks[i];
            if (ch == null || ch.Liquids.Count == 0) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12), (uint)(instBase + n * 24));
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12 + 4), (uint)ch.Liquids.Count);
            ulong fish = 0, deep = 0;
            foreach (var l in ch.Liquids)
            {
                fish |= l.Fishable;
                deep |= l.Deep;
            }
            if (ch.Liquids.Any(l => l.Fishable != fish || l.Deep != deep) && ch.Liquids.Count > 1) ctx.MaskChunks++;
            // a block when a mask is set, when the 1.60 source had one, or (other sources) when a liquid is not ocean: 1.60
            // writes none for pure ocean chunks and one (zero or not) for every other chunk (see Mh2oAttributes160)
            bool attr = (fish | deep) != 0 || (ch.Mh2oAttributes160 ?? ch.Liquids.Any(l => l.Kind is { } k && k != LiquidKind.Ocean));
            if (attr)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(i * 12 + 8), (uint)(dataBase + data.Position));
                data.Write(U64(fish));
                data.Write(U64(deep));
            }
            foreach (var l in ch.Liquids)
            {
                var s = inst.AsSpan(n * 24, 24);
                n++;
                int rx = Math.Clamp(l.RectX, 0, 8), ry = Math.Clamp(l.RectY, 0, 8);
                int rw = Math.Clamp(l.RectW, 0, 8 - rx), rh = Math.Clamp(l.RectH, 0, 8 - ry);
                int lvf = l.Heights != null ? (l.TexU != null && l.TexV != null ? (l.Depth != null ? 3 : 1) : 0) : (l.Depth != null ? 2 : -1);
                var ids = LiquidIdsOf(ctx, l, i);
                ushort second;
                if (ids.LiquidObject is { } obj) second = obj;
                else
                {
                    second = (ushort)Math.Max(lvf, 0);
                    ctx.ObjectDefaulted++;
                }
                BinaryPrimitives.WriteUInt16LittleEndian(s, ids.LiquidType);
                BinaryPrimitives.WriteUInt16LittleEndian(s[2..], second);
                BinaryPrimitives.WriteSingleLittleEndian(s[4..], l.Min);
                BinaryPrimitives.WriteSingleLittleEndian(s[8..], l.Max);
                s[12] = (byte)rx;
                s[13] = (byte)ry;
                s[14] = (byte)rw;
                s[15] = (byte)rh;
                // exists bitmap only when a tile of the rectangle is dry
                var bits = new byte[(rw * rh + 7) / 8];
                bool all = true;
                for (int yy = 0; yy < rh; yy++)
                    for (int xx = 0; xx < rw; xx++)
                    {
                        int bit = yy * rw + xx;
                        if ((l.Exists >> ((ry + yy) * 8 + rx + xx) & 1) != 0) bits[bit / 8] |= (byte)(1 << (bit % 8));
                        else all = false;
                    }
                if (!all)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)(dataBase + data.Position));
                    data.Write(bits);
                }
                if (lvf >= 0)
                {
                    int nv = (rw + 1) * (rh + 1);
                    BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)(dataBase + data.Position));
                    if (l.Heights != null)
                        for (int v = 0; v < nv; v++) data.Write(F32(v < l.Heights.Length ? l.Heights[v] : l.Min));
                    if (lvf is 1 or 3)
                        for (int v = 0; v < nv; v++)
                        {
                            data.Write(U16(v < l.TexU!.Length ? l.TexU[v] : (ushort)0));
                            data.Write(U16(v < l.TexV!.Length ? l.TexV[v] : (ushort)0));
                        }
                    if (lvf is 0 or 2 or 3)
                        for (int v = 0; v < nv; v++) data.WriteByte(l.Depth != null && v < l.Depth.Length ? l.Depth[v] : (byte)0);
                }
                if (l.MclqFlow112 != null || l.MclqFlowVectors112 != null || l.MclqTileResidue112 != null) ctx.FlowLayers++;
            }
        }
        var outBuf = new byte[dataBase + data.Length];
        head.CopyTo(outBuf, 0);
        inst.CopyTo(outBuf, instBase);
        data.ToArray().CopyTo(outBuf, dataBase);
        return outBuf;
    }

    private static Mh2oLiquidIds LiquidIdsOf(Ctx ctx, NeutralLiquid l, int chunk)
    {
        if (l.LiquidType is { } type)
            return new Mh2oLiquidIds((ushort)type, l.LiquidObjectOrVertexFormat is { } o ? (ushort)o : null);
        var kind = l.Kind ?? LiquidKind.River;
        if (l.Kind == null) ctx.Add(LossKind.LiquidTypeUnmapped, chunk, 1, "a liquid with neither a LiquidType nor a 1.12 kind: written as water");
        ctx.TypeDefaulted++;
        var ids = ctx.Options.LiquidIds?.Invoke(l, kind) ?? new Mh2oLiquidIds(DefaultLiquidType[(int)kind], null);
        if (ids.LiquidObject == null && l.LiquidObjectOrVertexFormat is { } keep) ids = ids with { LiquidObject = (ushort)keep };
        return ids;
    }

    // ------------------------------------------------------------------ tex0

    private static byte[] WriteTex0(Ctx ctx)
    {
        var t = ctx.Tile;
        var chunks = new byte[256][];
        for (int i = 0; i < 256; i++) chunks[i] = EncodeTexChunk(ctx, t.Chunks[i] ?? NeutralChunk.CreateFlat(t.X, t.Y, i % 16, i / 16, 0f), i);
        var ms = new MemoryStream();
        WriteChunk(ms, "MVER", U32(18));
        var mamp = t.ExtraChunks.FirstOrDefault(c => c.Magic == "MAMP" && c.Location == "tex0");
        WriteChunk(ms, "MAMP", mamp?.Data ?? new byte[4]);
        var mdid = new byte[ctx.Textures.Count * 4];
        var mhid = new byte[ctx.Textures.Count * 4];
        for (int i = 0; i < ctx.Textures.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mdid.AsSpan(i * 4), ctx.IdOf(ctx.Textures[i], "texture"));
            if (i < t.TextureParams.Count && t.TextureParams[i].HeightTexture is { } ht)
                BinaryPrimitives.WriteUInt32LittleEndian(mhid.AsSpan(i * 4), ctx.IdOf(ht, "height texture"));
        }
        WriteChunk(ms, "MDID", mdid);
        WriteChunk(ms, "MHID", mhid);
        if (t.TextureParams.Any(p => p.Flags != 0 || p.HeightScale != 0 || p.HeightOffset != 0))
        {
            var mtxp = new byte[ctx.Textures.Count * 16];
            for (int i = 0; i < Math.Min(ctx.Textures.Count, t.TextureParams.Count); i++)
            {
                var p = t.TextureParams[i];
                BinaryPrimitives.WriteUInt32LittleEndian(mtxp.AsSpan(i * 16), p.Flags);
                BinaryPrimitives.WriteSingleLittleEndian(mtxp.AsSpan(i * 16 + 4), p.HeightScale);
                BinaryPrimitives.WriteSingleLittleEndian(mtxp.AsSpan(i * 16 + 8), p.HeightOffset);
            }
            WriteChunk(ms, "MTXP", mtxp);
        }
        ms.Write(Extras(t.ExtraChunks, "tex0", "MAMP"));
        foreach (var c in chunks) ms.Write(c);
        ms.Write(Extras(t.ExtraChunks, "tex0+"));
        return ms.ToArray();
    }

    private static byte[] EncodeTexChunk(Ctx ctx, NeutralChunk ch, int index)
    {
        var mcly = new byte[ch.Layers.Count * 16];
        var mcal = new MemoryStream();
        bool reduced = false;
        // the target's blend mode: 8-bit maps hold weights, 4-bit maps sequential alphas (AlphaBlendMode); with one
        // upper layer the two are the same
        var alphas = ch.Layers.Select(l => l.Alpha).ToArray();
        var target = ctx.BigAlpha ? AlphaBlendMode.Weights : AlphaBlendMode.Sequential;
        if (ctx.Tile.AlphaBlend != target && ch.Layers.Count > 2 && ch.Layers.Skip(1).Count(l => l.Alpha != null) > 1)
        {
            ctx.BlendChunks++;
            if (target == AlphaBlendMode.Weights)
            {
                alphas = AlphaBlending.WeightBytes(ch.Layers, out long hidden);
                ctx.HiddenTexels += hidden;
            }
            else
            {
                var seq = AlphaBlending.SequentialFromWeights(AlphaBlending.VisibleWeights(ch.Layers, AlphaBlendMode.Weights));
                for (int L = 1; L < ch.Layers.Count; L++)
                {
                    if (alphas[L] == null) continue;
                    alphas[L] = seq[L].Select(AlphaBlending.Level4).ToArray();
                    if (!ctx.BigAlpha && seq[L].Any(s => MathF.Abs(AlphaBlending.Level4(s) - s) > 2.5f))
                    {
                        if (!reduced) ctx.AlphaLayers++;
                        reduced = true;
                    }
                }
            }
        }
        for (int L = 0; L < ch.Layers.Count; L++)
        {
            var l = ch.Layers[L];
            uint flags = l.Flags & ~0x300u;
            uint ofs = 0;
            if (alphas[L] is { Length: 4096 } a)
            {
                flags |= 0x100;
                ofs = (uint)mcal.Length;
                if (ctx.BigAlpha)
                {
                    bool compress = ctx.Options.CompressAlpha ?? (!ctx.From160 || (l.Flags & 0x200) != 0);
                    if (compress)
                    {
                        flags |= 0x200;
                        mcal.Write(EncodeAlpha(a));
                    }
                    else mcal.Write(a);
                }
                else
                {
                    var a4 = new byte[MapBytes.AlphaLayerSize];
                    for (int k = 0; k < MapBytes.AlphaLayerSize; k++)
                    {
                        int lo = a[k * 2], hi = a[k * 2 + 1];
                        if (NeutralLayer.Expand4(lo >> 4) != lo || NeutralLayer.Expand4(hi >> 4) != hi)
                        {
                            if (!reduced) ctx.AlphaLayers++;
                            reduced = true;
                        }
                        a4[k] = (byte)((lo >> 4) | ((hi >> 4) << 4));
                    }
                    mcal.Write(a4);
                }
            }
            var s = mcly.AsSpan(L * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)ctx.TextureIndex(l.Texture));
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], ofs);
            BinaryPrimitives.WriteUInt32LittleEndian(s[12..], l.EffectId);
        }
        if (reduced) ctx.AlphaChunks++;
        var ms = new MemoryStream();
        ms.Write(new byte[8]);
        WriteChunk(ms, "MCLY", mcly);
        if (ch.Shadow is { Length: 4096 } sh)
        {
            var d = new byte[MapBytes.McshSize];
            for (int k = 0; k < 4096; k++)
                if (sh[k]) d[k / 8] |= (byte)(1 << (k % 8));
            WriteChunk(ms, "MCSH", d);
        }
        if (mcal.Length > 0) WriteChunk(ms, "MCAL", mcal.ToArray());
        foreach (var x in ch.ExtraSubChunks)
            if (x.Location == "tex0") WriteChunk(ms, x.Magic, x.Data);
        var buf = ms.ToArray();
        SetHeader(buf, 0, "MCNK", buf.Length - 8);
        return buf;
    }

    /// <summary>
    /// Compressed alpha exactly as Blizzard's tools wrote it (MEASURED: this rule reproduces 37,994 of 37,994 compressed
    /// 1.60 layers of 120 sampled tiles byte for byte, and every layer of the round-trip test): row by row (64 values; no
    /// packet crosses a row), a fill packet (0x80 | n, value) when the next value repeats the current one, else a copy
    /// packet that takes values while each has a different successor in the row; the last value of a row never joins a
    /// copy (it is its own packet unless it ends a fill). A row is at most 64 values, so n never exceeds 127.
    /// </summary>
    public static byte[] EncodeAlpha(byte[] a)
    {
        var o = new MemoryStream(1024);
        for (int r0 = 0; r0 < 4096; r0 += 64)
        {
            int p = 0;
            while (p < 64)
            {
                if (p + 1 < 64 && a[r0 + p] == a[r0 + p + 1])
                {
                    int run = 1;
                    while (p + run < 64 && a[r0 + p + run] == a[r0 + p]) run++;
                    o.WriteByte((byte)(0x80 | run));
                    o.WriteByte(a[r0 + p]);
                    p += run;
                }
                else
                {
                    int s = p++;
                    while (p + 1 < 64 && a[r0 + p] != a[r0 + p + 1]) p++;
                    o.WriteByte((byte)(p - s));
                    o.Write(a, r0 + s, p - s);
                }
            }
        }
        return o.ToArray();
    }

    // ------------------------------------------------------------------ obj0 / obj1

    private static byte[] WriteObj0(Ctx ctx)
    {
        var t = ctx.Tile;
        ReportModelList(ctx, t.DoodadModels, t.Doodads.Select(d => d.Model), "doodad");
        ReportModelList(ctx, t.WmoModels, t.Wmos.Select(w => w.Model), "WMO");
        var mddf = new byte[t.Doodads.Count * MapBytes.MddfSize];
        for (int i = 0; i < t.Doodads.Count; i++)
        {
            var d = t.Doodads[i];
            var s = mddf.AsSpan(i * MapBytes.MddfSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s, ctx.IdOf(d.Model, "doodad model"));
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], d.UniqueId);
            WriteVec(s[8..], d.Position);
            WriteVec(s[20..], d.Rotation);
            BinaryPrimitives.WriteUInt16LittleEndian(s[32..], d.ScaleRaw);
            BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)(d.Flags | 0x40));
        }
        var modf = new byte[t.Wmos.Count * MapBytes.ModfSize];
        for (int i = 0; i < t.Wmos.Count; i++) EncodeModf(modf.AsSpan(i * MapBytes.ModfSize), t.Wmos[i], ctx.IdOf(t.Wmos[i].Model, "WMO"), ctx.From160);
        var ms = new MemoryStream();
        WriteChunk(ms, "MVER", U32(18));
        WriteChunk(ms, "MDDF", mddf);
        WriteChunk(ms, "MODF", modf);
        ms.Write(Extras(t.ExtraChunks, "obj0"));
        for (int i = 0; i < 256; i++)
        {
            var ch = t.Chunks[i];
            var c = new MemoryStream();
            if (ch != null)
            {
                if (ch.DoodadRefs.Count > 0) WriteChunk(c, "MCRD", ch.DoodadRefs.SelectMany(r => U32((uint)r)).ToArray());
                if (ch.WmoRefs.Count > 0) WriteChunk(c, "MCRW", ch.WmoRefs.SelectMany(r => U32((uint)r)).ToArray());
                foreach (var x in ch.ExtraSubChunks)
                    if (x.Location == "obj0") WriteChunk(c, x.Magic, x.Data);
            }
            WriteChunk(ms, "MCNK", c.ToArray());
        }
        ms.Write(Extras(t.ExtraChunks, "obj0+"));
        return ms.ToArray();
    }

    /// <summary>A model list that is not the first-use order of its placements (or has entries no placement uses) cannot
    /// be stored: modern placements name the model by FileDataID.</summary>
    private static void ReportModelList(Ctx ctx, List<AssetRef> list, IEnumerable<AssetRef> uses, string what)
    {
        var order = new List<AssetRef>();
        var seen = new HashSet<AssetRef>(ReferenceEqualityComparer.Instance);
        foreach (var u in uses)
            if (seen.Add(u)) order.Add(u);
        if (order.Count == list.Count && order.Zip(list).All(p => ReferenceEquals(p.First, p.Second))) return;
        int unused = list.Count(a => !seen.Contains(a));
        ctx.Add(LossKind.ModelListRebuilt, null, list.Count,
            $"the {what} model list ({list.Count} entries{(unused > 0 ? $", {unused} unused" : "")}) is not in first-use order: placements name their model by FileDataID, so a reader rebuilds the list in first-use order");
    }

    private static void EncodeModf(Span<byte> s, NeutralWmo m, uint id, bool from160)
    {
        ushort flags = (ushort)(m.Flags | 0x8), scale = m.ScaleRaw;
        if (!from160 && (m.Flags & 0x4) == 0)
        {
            // 1.12: the field is padding (0). 1.60 writes flag 0x4 with scale 1024 on every placement (MEASURED: 0xC)
            flags |= 0x4;
            scale = 1024;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s, id);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], m.UniqueId);
        WriteVec(s[8..], m.Position);
        WriteVec(s[20..], m.Rotation);
        WriteVec(s[32..], m.ExtentMin);
        WriteVec(s[44..], m.ExtentMax);
        BinaryPrimitives.WriteUInt16LittleEndian(s[56..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s[58..], m.DoodadSet);
        BinaryPrimitives.WriteUInt16LittleEndian(s[60..], m.NameSet);
        BinaryPrimitives.WriteUInt16LittleEndian(s[62..], scale);
    }

    /// <summary>An obj1 for a tile without one: MVER, MLFD (zero counts) and empty MLDD MLDX MLMD MLMX lists.</summary>
    private static byte[] MinimalObj1(Ctx ctx)
    {
        ctx.Add(LossKind.Obj1Generated, null, 1, "no obj1 in the model: written with empty level-of-detail placement lists");
        var ms = new MemoryStream();
        WriteChunk(ms, "MVER", U32(18));
        WriteChunk(ms, "MLFD", new byte[48]);
        foreach (var m in new[] { "MLDD", "MLDX", "MLMD", "MLMX" }) WriteChunk(ms, m, Array.Empty<byte>());
        return ms.ToArray();
    }

    // ====================================================================== WDT

    /// <summary>
    /// Writes the WDT: <c>MVER MPHD MAIN MAID [extra chunks] [MODF]</c>. MAID per tile: <paramref name="tileMaids"/> (the
    /// <see cref="Adt160Files.Maid"/> of the tiles just written), else the map's own entry, else IDs from the name map for
    /// the tile file names; the minimap slot from <see cref="NeutralTileEntry.Minimap"/>. A map from another version gets
    /// MPHD 0x200 (MAID) + 0x4 when 8-bit alpha, and the WDL's FileDataID in the companion list.
    /// </summary>
    public static byte[] WriteWdt(NeutralMap map, ConversionReport? report = null, Adt160WriterOptions? options = null, IReadOnlyDictionary<int, uint[]>? tileMaids = null)
    {
        options ??= new Adt160WriterOptions();
        var ids = options.Ids ??= new AssetIdMap();
        string name = (map.Name.Length > 0 ? map.Name : "map") + ".wdt";
        bool from160 = map.Source == MapSourceVersion.V160;
        bool big = options.BigAlpha ?? (from160 && Adt160Reader.BigAlpha(map.Flags));
        uint flags = map.Flags | 0x200;
        if (!from160) flags = (map.Flags & 1) | 0x200;
        if (big && (flags & 0x84) == 0) flags |= 0x4;
        if (!big) flags &= ~0x84u;
        var extra = (uint[])map.MphdExtra.Clone();
        if (!from160)
        {
            extra = new uint[7];
            if (map.Wdl != null || map.ExistingTiles().Any())
                extra[5] = ids.IdOf($@"World\Maps\{map.Name}\{map.Name}.wdl", "wdl", out _);
            report?.Add(LossKind.MapFileRefsMissing, name, null, 5, "MPHD companion files (lgt, occ, fogs, mpv, tex) not written: their FileDataIDs are 0");
        }
        var ms = new MemoryStream();
        WriteChunk(ms, "MVER", U32(map.Version));
        var mphd = new byte[MapBytes.WdtMphdSize];
        BinaryPrimitives.WriteUInt32LittleEndian(mphd, flags);
        for (int k = 0; k < 7; k++) BinaryPrimitives.WriteUInt32LittleEndian(mphd.AsSpan(4 + k * 4), extra[k]);
        WriteChunk(ms, "MPHD", mphd);
        var main = new byte[MapBytes.WdtMainSize];
        var maid = new byte[4096 * 8 * 4];
        int missingSlots = 0, allocatedMinimaps = 0;
        for (int i = 0; i < 4096; i++)
        {
            var e = map.Tiles[i];
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(i * 8), (e.Flags & ~1u) | (e.HasAdt ? 1u : 0u));
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(i * 8 + 4), e.AsyncId);
            uint[]? m = tileMaids != null && tileMaids.TryGetValue(i, out var tm) ? (uint[])tm.Clone() : (uint[]?)e.Maid?.Clone();
            if (m == null && e.HasAdt)
            {
                m = new uint[8];
                for (int s = 0; s < 4; s++) m[s] = ids.IdOf(TileFilePath(map.Name, i % 64, i / 64, s), "tile " + Adt160Reader.MaidSlots[s], out _);
            }
            if (m == null) continue;
            if (m.Length < 8) Array.Resize(ref m, 8);
            if (m[7] == 0 && e.Minimap != null)
            {
                if (e.Minimap.FileDataId is { } f) m[7] = f;
                else if (e.Minimap.HasPath)
                {
                    m[7] = ids.IdOf(e.Minimap.Path!, "minimap", out var origin);
                    if (origin == AssetIdOrigin.Allocated) allocatedMinimaps++;
                }
            }
            if (e.HasAdt) missingSlots += m.Skip(4).Count(v => v == 0);
            for (int s = 0; s < 8; s++) BinaryPrimitives.WriteUInt32LittleEndian(maid.AsSpan((i * 8 + s) * 4), m[s]);
        }
        WriteChunk(ms, "MAIN", main);
        WriteChunk(ms, "MAID", maid);
        if (!from160 && missingSlots > 0)
            report?.Add(LossKind.MapFileRefsMissing, name, null, missingSlots, $"{missingSlots} MAID slots (LOD, map textures, minimaps) are empty: no such files");
        if (allocatedMinimaps > 0)
            report?.Add(LossKind.AssetIdAllocated, name, null, allocatedMinimaps, $"{allocatedMinimaps} minimaps got local FileDataIDs (1.12 minimaps have md5 names that no root hashes)");
        foreach (var c in map.ExtraChunks)
        {
            if (from160 || c.Location == "wdt") WriteChunk(ms, c.Magic, c.Data);
            else report?.Add(LossKind.UnknownChunksDropped, name, null, 1, $"chunk {c.Magic} of another WDT layout dropped");
        }
        if (map.GlobalWmo is { } g)
        {
            var modf = new byte[MapBytes.ModfSize];
            uint id = g.Model.FileDataId ?? (g.Model.HasPath ? ids.IdOf(g.Model.Path!, "WMO", out _) : 0);
            if (g.Model.FileDataId is { } gf) ids.Record(gf, g.Model.Path, "WMO");
            EncodeModf(modf, g, id, from160);
            WriteChunk(ms, "MODF", modf);
        }
        return ms.ToArray();
    }

    // ====================================================================== WDL

    /// <summary>
    /// Writes the WDL in 1.60's layout (MVER 18): <c>MLDD MLDX MLMD MLMX</c> (empty unless the source had them),
    /// <c>MAOF</c>, then per tile in index order <c>MARE</c>, its extra chunks (MAOE), <c>MAHO</c> (zeros when the model
    /// has none, as 1.60 writes one for every tile). A WDL without heights (another modern layout) is written back as its
    /// chunks.
    /// </summary>
    public static byte[] WriteWdl(NeutralWdl wdl, ConversionReport? report = null, string name = "map.wdl")
    {
        var ms = new MemoryStream();
        bool from160 = wdl.Source == MapSourceVersion.V160;
        WriteChunk(ms, "MVER", U32(from160 ? wdl.Version : 18));
        if (wdl.Heights.Count == 0 && wdl.ExtraChunks.Count > 0)
        {
            foreach (var c in wdl.ExtraChunks) WriteChunk(ms, c.Magic, c.Data);
            return ms.ToArray();
        }
        if (from160) foreach (var c in wdl.ExtraChunks) WriteChunk(ms, c.Magic, c.Data);
        else
        {
            foreach (var m in new[] { "MLDD", "MLDX", "MLMD", "MLMX" }) WriteChunk(ms, m, Array.Empty<byte>());
            if (wdl.ExtraChunks.Count > 0)
                report?.Add(LossKind.UnknownChunksDropped, name, null, wdl.ExtraChunks.Count, "chunks dropped: " + string.Join(" ", wdl.ExtraChunks.Select(c => c.Magic)));
        }
        long maof = ms.Position + 8;
        WriteChunk(ms, "MAOF", new byte[MapBytes.WdlMaofSize]);
        var offsets = new Dictionary<int, uint>();
        foreach (var (i, h) in wdl.Heights)
        {
            if (h.Length != 545) throw new ArgumentException($"WDL tile {i}: 545 heights expected, got {h.Length}");
            offsets[i] = (uint)ms.Position;
            var d = new byte[MapBytes.WdlMareData];
            for (int k = 0; k < 545; k++) BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(k * 2), h[k]);
            WriteChunk(ms, "MARE", d);
            if (wdl.TileExtraChunks.TryGetValue(i, out var extras))
                foreach (var c in extras) WriteChunk(ms, c.Magic, c.Data);
            var holes = new byte[32];
            if (wdl.Holes.TryGetValue(i, out var hm))
                for (int k = 0; k < 16 && k < hm.Length; k++) BinaryPrimitives.WriteUInt16LittleEndian(holes.AsSpan(k * 2), hm[k]);
            WriteChunk(ms, "MAHO", holes);
        }
        var buf = ms.ToArray();
        foreach (var (i, o) in offsets) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan((int)maof + i * 4), o);
        return buf;
    }

    // ====================================================================== bytes

    private static void WriteChunk(Stream s, string magic, byte[] data)
    {
        var h = new byte[8];
        SetHeader(h, 0, magic, data.Length);
        s.Write(h);
        s.Write(data);
    }

    private static void SetHeader(byte[] b, int pos, string magic, int size)
    {
        b[pos] = (byte)magic[3];
        b[pos + 1] = (byte)magic[2];
        b[pos + 2] = (byte)magic[1];
        b[pos + 3] = (byte)magic[0];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pos + 4), size);
    }

    private static void WriteVec(Span<byte> s, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(s, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(s[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[8..], v.Z);
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    private static byte[] F32(float v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        return b;
    }
}
