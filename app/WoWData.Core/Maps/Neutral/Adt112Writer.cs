using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WoWData.Core.Maps.Neutral;

/// <summary>Options of the 1.12 writer.</summary>
public sealed class Adt112WriterOptions
{
    /// <summary>Maps a liquid type ID (MH2O LiquidType) to a 1.12 kind; null = unknown (the writer then uses river water
    /// and reports it). Comes from the modern LiquidType table later.</summary>
    public Func<int, LiquidKind?>? LiquidKind { get; set; }
    /// <summary>Folder for assets that have no path (made-up name &lt;folder&gt;\&lt;FileDataID&gt;.&lt;ext&gt;).</summary>
    public string ConvertedFolder { get; set; } = @"World\Converted";
}

/// <summary>
/// The neutral model as 1.12.1 map files. The layout is the one Blizzard's tools wrote in every stock 1.12 tile (see
/// <see cref="Adt112Reader"/>), so a stock tile read by <see cref="Adt112Reader"/> comes back byte for byte. Content from
/// richer sources is reduced to what 1.12 can show, and every reduction goes into the <see cref="ConversionReport"/>:
/// * more than 4 texture layers: the 4 with the most visible coverage are kept and renormalised;
/// * 8-bit alpha to 4 bits with <c>v &gt;&gt; 4</c> (the exact inverse of Blizzard's <c>16n + 15</c>; reported only when a
///   value is not of that form);
/// * fine holes: a 1.12 hole only where all four fine holes are holes;
/// * MCCV / MCLV dropped; shadows absent: no MCSH, flag 0x1 clear;
/// * MH2O liquids: one MCLQ layer per chunk (the instance covering most tiles, kind from <see cref="NeutralLiquid"/>'s
///   kind or the supplied type mapping); MCLQ-born layers are written as they are (one per kind);
/// * assets without a path: <c>World\Converted\&lt;FileDataID&gt;.&lt;ext&gt;</c>.
/// </summary>
public static class Adt112Writer
{
    private const uint McalNoLayers = 0xFFFFF800;     // MCAL size field of a chunk without layers in every stock tile
    private const uint Known112ChunkFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x8000;
    private const uint SilentChunkFlags = 0x40 | 0x10000;   // MCCV present / high-res holes: storage bits, reported elsewhere

    // ====================================================================== ADT

    public static byte[] WriteAdt(NeutralTile tile, ConversionReport? report = null, Adt112WriterOptions? options = null)
    {
        options ??= new Adt112WriterOptions();
        var ctx = new TileContext(tile, report, options);
        // the chunks first: their layers may add textures
        var chunks = new byte[256][];
        for (int i = 0; i < 256; i++) chunks[i] = EncodeChunk(ctx, tile.Chunks[i], i);
        var mddf = EncodeMddf(ctx);
        var modf = EncodeModf(ctx);
        var mtex = ctx.Textures.NameBlock(out _);
        var mmdx = ctx.Models.NameBlock(out var mmid);
        var mwmo = ctx.WmoNames.NameBlock(out var mwid);
        ctx.ReportTileExtras();

        long total = 12 + 8 + 64 + 8 + 256 * 16 + 8 + mtex.Length + 8 + mmdx.Length + 8 + mmid.Length * 4 + 8 + mwmo.Length
                     + 8 + mwid.Length * 4 + 8 + mddf.Length + 8 + modf.Length + chunks.Sum(c => (long)c.Length);
        var buf = new byte[total];
        var w = new SpanWriter(buf);
        w.Chunk("MVER", 4);
        w.U32(18);
        w.Chunk("MHDR", 64);
        int mhdrData = w.Pos;
        w.Skip(64);
        var ofs = new Dictionary<string, int>();
        ofs["MCIN"] = w.Pos;
        w.Chunk("MCIN", 256 * 16);
        int mcin = w.Pos;
        w.Skip(256 * 16);
        ofs["MTEX"] = w.Pos;
        w.Chunk("MTEX", mtex.Length);
        w.Bytes(mtex);
        ofs["MMDX"] = w.Pos;
        w.Chunk("MMDX", mmdx.Length);
        w.Bytes(mmdx);
        ofs["MMID"] = w.Pos;
        w.Chunk("MMID", mmid.Length * 4);
        foreach (var v in mmid) w.U32(v);
        ofs["MWMO"] = w.Pos;
        w.Chunk("MWMO", mwmo.Length);
        w.Bytes(mwmo);
        ofs["MWID"] = w.Pos;
        w.Chunk("MWID", mwid.Length * 4);
        foreach (var v in mwid) w.U32(v);
        ofs["MDDF"] = w.Pos;
        w.Chunk("MDDF", mddf.Length);
        w.Bytes(mddf);
        ofs["MODF"] = w.Pos;
        w.Chunk("MODF", modf.Length);
        w.Bytes(modf);
        for (int i = 0; i < 256; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(mcin + i * 16), (uint)w.Pos);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(mcin + i * 16 + 4), (uint)chunks[i].Length);
            w.Bytes(chunks[i]);
        }
        // MHDR: flags (MFBO / Northrend bits have no 1.12 meaning), then the 8 offsets the client reads
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(mhdrData), tile.HeaderFlags & ~3u);
        foreach (var (field, magic) in MapBytes.MhdrChunks)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(mhdrData + Array.IndexOf(MapBytes.MhdrFields, field) * 4), (uint)(ofs[magic] - mhdrData));
        return buf;
    }

    /// <summary>State of one tile write: the asset lists as written and the report.</summary>
    private sealed class TileContext
    {
        public readonly NeutralTile Tile;
        public readonly ConversionReport? Report;
        public readonly Adt112WriterOptions Options;
        public readonly AssetList Textures, Models, WmoNames;
        public readonly string Name;
        public readonly bool From112;
        public int MccvChunks, MclvChunks, AlphaLayers, AlphaChunks, MappedLiquids, FlagChunks, LayerFlagLayers, SoundDefaulted;
        public uint FlagBits, LayerFlagBits;

        public TileContext(NeutralTile tile, ConversionReport? report, Adt112WriterOptions options)
        {
            Tile = tile;
            Report = report;
            Options = options;
            Name = TileName(tile);
            From112 = tile.Source == MapSourceVersion.V112;
            Textures = new AssetList(this, tile.Textures, "blp", "texture");
            Models = new AssetList(this, tile.DoodadModels, "mdx", "doodad model");
            WmoNames = new AssetList(this, tile.WmoModels, "wmo", "WMO");
        }

        public void Add(LossKind kind, int? chunk, int count, string message) => Report?.Add(kind, Name, chunk, count, message);

        public void ReportTileExtras()
        {
            if (MccvChunks > 0) Add(LossKind.VertexColorsDropped, null, MccvChunks, $"MCCV vertex colours dropped on {MccvChunks} chunks");
            if (MclvChunks > 0) Add(LossKind.VertexLightingDropped, null, MclvChunks, $"MCLV vertex lighting dropped on {MclvChunks} chunks");
            if (AlphaLayers > 0) Add(LossKind.AlphaReduced, null, AlphaLayers, $"8-bit alpha reduced to 4 bits on {AlphaLayers} layers in {AlphaChunks} chunks");
            if (MappedLiquids > 0) Add(LossKind.LiquidTypeMapped, null, MappedLiquids, $"{MappedLiquids} liquid instances mapped from their type ID to a 1.12 kind");
            if (FlagChunks > 0) Add(LossKind.ChunkFlagsCleared, null, FlagChunks, $"chunk flag bits 0x{FlagBits:X} cleared on {FlagChunks} chunks");
            if (LayerFlagLayers > 0) Add(LossKind.LayerFlagsCleared, null, LayerFlagLayers, $"layer flag bits 0x{LayerFlagBits:X} cleared on {LayerFlagLayers} layers");
            if (SoundDefaulted > 0) Add(LossKind.SoundEmittersDefaulted, null, SoundDefaulted, $"{SoundDefaulted} sound emitters written with only id and position (other 1.12 fields 0)");
            if (Tile.FlightBounds != null) Add(LossKind.FlightBoundsDropped, null, 1, "MFBO flight bounds dropped");
            if (Tile.TextureParams.Count > 0) Add(LossKind.TextureParamsDropped, null, Tile.TextureParams.Count, $"{Tile.TextureParams.Count} texture parameter entries (MTXF/MTXP) dropped");
            if (Tile.LodBlob != null) Add(LossKind.LodDropped, null, 1, "LOD data dropped");
            if (Tile.ExtraChunks.Count > 0)
                Add(LossKind.UnknownChunksDropped, null, Tile.ExtraChunks.Count, "chunks dropped: " + string.Join(" ", Tile.ExtraChunks.Select(c => c.Magic)));
            Textures.ReportMadeUp();
            Models.ReportMadeUp();
            WmoNames.ReportMadeUp();
        }
    }

    public static string TileName(NeutralTile t) => $"{(t.Map.Length > 0 ? t.Map : "tile")}_{t.X}_{t.Y}";

    /// <summary>An asset list as written: the model's list in order, entries found by object first (so duplicates keep
    /// their index), then by name; unknown ones appended.</summary>
    private sealed class AssetList
    {
        private readonly TileContext _ctx;
        private readonly List<AssetRef> _list;
        private readonly Dictionary<AssetRef, int> _byRef = new(ReferenceEqualityComparer.Instance);
        private readonly string _ext, _what;
        private readonly Dictionary<int, int> _madeUpUses = new();
        private readonly Dictionary<int, string> _madeUpPath = new();

        public AssetList(TileContext ctx, List<AssetRef> source, string ext, string what)
        {
            _ctx = ctx;
            _list = new List<AssetRef>(source);
            for (int i = 0; i < _list.Count; i++) _byRef.TryAdd(_list[i], i);
            _ext = ext;
            _what = what;
        }

        public int IndexOf(AssetRef a)
        {
            int i;
            if (!_byRef.TryGetValue(a, out i))
            {
                i = _list.FindIndex(x => x.SameAs(a));
                if (i < 0)
                {
                    i = _list.Count;
                    _list.Add(a);
                }
                _byRef[a] = i;
            }
            if (!_list[i].HasPath) _madeUpUses[i] = _madeUpUses.GetValueOrDefault(i) + 1;
            return i;
        }

        private string PathOf(int i)
        {
            var a = _list[i];
            if (a.HasPath)
            {
                var p = a.Path!;
                // modern model names end in .m2; 1.12 names them .mdx (the client maps .mdx to .m2)
                if (_ext == "mdx" && p.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) p = p[..^3] + ".mdx";
                return p;
            }
            if (!_madeUpPath.TryGetValue(i, out var made))
            {
                made = a.FileDataId is { } f ? $@"{_ctx.Options.ConvertedFolder}\{f}.{_ext}" : $@"{_ctx.Options.ConvertedFolder}\missing_{i}.{_ext}";
                _madeUpPath[i] = made;
            }
            return made;
        }

        /// <summary>The zero-terminated names in order and each name's offset.</summary>
        public byte[] NameBlock(out uint[] offsets)
        {
            var ms = new MemoryStream();
            offsets = new uint[_list.Count];
            for (int i = 0; i < _list.Count; i++)
            {
                offsets[i] = (uint)ms.Position;
                ms.Write(Encoding.Latin1.GetBytes(PathOf(i)));
                ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        public void ReportMadeUp()
        {
            foreach (var i in _madeUpPath.Keys.Concat(_madeUpUses.Keys).Distinct().OrderBy(i => i))
            {
                var a = _list[i];
                int uses = _madeUpUses.GetValueOrDefault(i);
                var path = PathOf(i);
                if (a.FileDataId is { } f)
                    _ctx.Add(LossKind.AssetPathMadeUp, null, uses, $"{_what} FDID {f} has no 1.12 path: written as {path} ({uses} uses)");
                else
                    _ctx.Add(LossKind.AssetMissing, null, uses, $"{_what} entry {i} has neither a path nor a FileDataID: written as {path} ({uses} uses)");
            }
        }
    }

    // ------------------------------------------------------------------ placements

    private static byte[] EncodeMddf(TileContext ctx)
    {
        var t = ctx.Tile;
        var buf = new byte[t.Doodads.Count * MapBytes.MddfSize];
        int cleared = 0;
        uint bits = 0;
        for (int i = 0; i < t.Doodads.Count; i++)
        {
            var d = t.Doodads[i];
            var s = buf.AsSpan(i * MapBytes.MddfSize);
            ushort flags = d.Flags;
            if (!ctx.From112 && flags != 0)
            {
                // 0x40 = the name is a FileDataID: storage only (the 1.12 file names it by path)
                if ((flags & ~0x40) != 0) { cleared++; bits |= (uint)(flags & ~0x40); }
                flags = 0;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)ctx.Models.IndexOf(d.Model));
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], d.UniqueId);
            WriteVec(s[8..], d.Position);
            WriteVec(s[20..], d.Rotation);
            BinaryPrimitives.WriteUInt16LittleEndian(s[32..], d.ScaleRaw);
            BinaryPrimitives.WriteUInt16LittleEndian(s[34..], flags);
        }
        if (cleared > 0) ctx.Add(LossKind.PlacementFlagsCleared, null, cleared, $"doodad flag bits 0x{bits:X} cleared on {cleared} placements");
        return buf;
    }

    private static byte[] EncodeModf(TileContext ctx)
    {
        var t = ctx.Tile;
        var buf = new byte[t.Wmos.Count * MapBytes.ModfSize];
        int cleared = 0, scaled = 0;
        uint bits = 0;
        for (int i = 0; i < t.Wmos.Count; i++)
        {
            var m = t.Wmos[i];
            var (flags, pad) = Modf112Flags(m, ctx.From112, ref cleared, ref scaled, ref bits);
            EncodeModfEntry(buf.AsSpan(i * MapBytes.ModfSize), m, (uint)ctx.WmoNames.IndexOf(m.Model), flags, pad);
        }
        if (cleared > 0) ctx.Add(LossKind.PlacementFlagsCleared, null, cleared, $"WMO flag bits 0x{bits:X} cleared on {cleared} placements");
        if (scaled > 0) ctx.Add(LossKind.WmoScaleDropped, null, scaled, $"{scaled} WMO placements had a scale other than 1 (written at scale 1)");
        return buf;
    }

    private static (ushort Flags, ushort Pad) Modf112Flags(NeutralWmo m, bool from112, ref int cleared, ref int scaled, ref uint bits)
    {
        if (from112) return (m.Flags, m.ScaleRaw);
        // 0x8 = name is a FileDataID (storage only); 0x4 = has scale (lost unless the scale is 1)
        if ((m.Flags & 0x4) != 0 && m.ScaleRaw != 1024) scaled++;
        if ((m.Flags & ~0xC) != 0) { cleared++; bits |= (uint)(m.Flags & ~0xC); }
        return (0, 0);
    }

    private static void EncodeModfEntry(Span<byte> s, NeutralWmo m, uint nameId, ushort flags, ushort pad)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, nameId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], m.UniqueId);
        WriteVec(s[8..], m.Position);
        WriteVec(s[20..], m.Rotation);
        WriteVec(s[32..], m.ExtentMin);
        WriteVec(s[44..], m.ExtentMax);
        BinaryPrimitives.WriteUInt16LittleEndian(s[56..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s[58..], m.DoodadSet);
        BinaryPrimitives.WriteUInt16LittleEndian(s[60..], m.NameSet);
        BinaryPrimitives.WriteUInt16LittleEndian(s[62..], pad);
    }

    private static void WriteVec(Span<byte> s, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(s, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(s[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[8..], v.Z);
    }

    // ------------------------------------------------------------------ chunks

    private sealed record LayerOut(int TextureId, uint Flags, uint EffectId, byte[]? Alpha4);

    private static byte[] EncodeChunk(TileContext ctx, NeutralChunk? ch, int index)
    {
        if (ch == null)
        {
            ctx.Add(LossKind.ChunkMissing, index, 1, "chunk missing in the model: written flat at height 0");
            ch = NeutralChunk.CreateFlat(ctx.Tile.X, ctx.Tile.Y, index % 16, index / 16, 0f);
        }
        // --- texture layers (at most 4), alpha to 4 bits
        var layers = ch.Layers.Count > 4 ? ReduceLayers(ctx, ch, index) : ch.Layers;
        var outLayers = new List<LayerOut>(layers.Count);
        int nAlpha = 0, reduced = 0;
        foreach (var l in layers)
        {
            uint flags = l.Flags & 0xFF;
            uint extra = l.Flags & ~0x3FFu;     // 0x100 follows the alpha map, 0x200 (compressed) is storage only
            if (extra != 0)
            {
                ctx.LayerFlagLayers++;
                ctx.LayerFlagBits |= extra;
            }
            byte[]? a4 = null;
            if (l.Alpha != null)
            {
                flags |= 0x100;
                a4 = PackAlpha4(l.Alpha, out bool lossy);
                if (lossy) reduced++;
                nAlpha++;
            }
            outLayers.Add(new LayerOut(ctx.Textures.IndexOf(l.Texture), flags, l.EffectId, a4));
        }
        if (reduced > 0)
        {
            ctx.AlphaLayers += reduced;
            ctx.AlphaChunks++;
        }
        // --- holes
        uint holes = NeutralChunk.ReduceHighResHoles(ch.Holes);
        ulong kept = NeutralChunk.ExpandLowResHoles(holes);
        if (kept != ch.Holes)
        {
            int lost = BitOperations.PopCount(ch.Holes & ~kept);
            ctx.Add(LossKind.HolesReduced, index, lost, $"{lost} fine holes are not whole 2x2 blocks: ground left closed there");
        }
        // --- vertex colours / lighting
        if (ch.VertexColors != null) ctx.MccvChunks++;
        if (ch.VertexLighting != null) ctx.MclvChunks++;
        // --- liquids
        var liquids = ChooseLiquids(ctx, ch, index);
        // --- flags
        uint unknown = ch.Flags & ~Known112ChunkFlags & ~SilentChunkFlags;
        if (unknown != 0)
        {
            ctx.FlagChunks++;
            ctx.FlagBits |= unknown;
        }
        uint flags112 = ch.Flags & (0x1 | 0x2 | 0x8000);
        if ((flags112 & 1) != 0 && ch.Shadow == null)
        {
            flags112 &= ~1u;
            ctx.Add(LossKind.ShadowMissing, index, 1, "flag 0x1 (shadow map) set without a shadow: cleared");
        }
        foreach (var (kind, _) in liquids) flags112 |= MapBytes.McnkLiquidFlags[(int)kind];
        // --- sound emitters
        foreach (var e in ch.SoundEmitters)
            if (e.Raw112 is not { Length: MapBytes.McseSize }) ctx.SoundDefaulted++;

        // --- layout: header, MCVT, MCNR (+13), MCLY, MCRF, [MCSH], MCAL, MCLQ (size 0 + layers), MCSE
        int nRefs = ch.DoodadRefs.Count + ch.WmoRefs.Count;
        bool shadow = ch.Shadow != null;
        int size = 8 + MapBytes.McnkHeaderSize + 8 + MapBytes.McvtSize + 8 + MapBytes.McnrSize + MapBytes.McnrPad + 8 + 16 * outLayers.Count
                   + 8 + 4 * nRefs + (shadow ? 8 + MapBytes.McshSize : 0) + 8 + nAlpha * MapBytes.AlphaLayerSize
                   + 8 + liquids.Count * MapBytes.MclqLayerSize + 8 + ch.SoundEmitters.Count * MapBytes.McseSize;
        var buf = new byte[size];
        var w = new SpanWriter(buf);
        w.Chunk("MCNK", size - 8);
        int h = w.Pos;
        w.Skip(MapBytes.McnkHeaderSize);
        void H(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(h + o), v);

        H(0x00, flags112);
        H(0x04, (uint)ch.IndexX);
        H(0x08, (uint)ch.IndexY);
        H(0x0C, (uint)outLayers.Count);
        H(0x10, (uint)ch.DoodadRefs.Count);
        H(0x34, ch.AreaId);
        H(0x38, (uint)ch.WmoRefs.Count);
        H(0x3C, holes);
        ch.LowQualityTextureMap.AsSpan(0, Math.Min(16, ch.LowQualityTextureMap.Length)).CopyTo(buf.AsSpan(h + 0x40));
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(h + 0x50), ch.NoEffectDoodad);
        H(0x5C, (uint)ch.SoundEmitters.Count);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(h + 0x68), ch.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(h + 0x6C), ch.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(h + 0x70), ch.Position.Z);

        H(0x14, (uint)w.Pos);
        w.Chunk("MCVT", MapBytes.McvtSize);
        for (int i = 0; i < 145; i++) w.F32(ch.Heights[i]);

        H(0x18, (uint)w.Pos);
        w.Chunk("MCNR", MapBytes.McnrSize);
        for (int i = 0; i < MapBytes.McnrSize; i++) w.U8((byte)ch.Normals[i]);
        if (ch.NormalPad112 is { Length: MapBytes.McnrPad } pad) w.Bytes(pad);
        else w.Skip(MapBytes.McnrPad);

        H(0x1C, (uint)w.Pos);
        w.Chunk("MCLY", 16 * outLayers.Count);
        int alphaOfs = 0;
        foreach (var l in outLayers)
        {
            w.U32((uint)l.TextureId);
            w.U32(l.Flags);
            w.U32(l.Alpha4 != null ? (uint)alphaOfs : 0);
            w.U32(l.EffectId);
            if (l.Alpha4 != null) alphaOfs += MapBytes.AlphaLayerSize;
        }

        H(0x20, (uint)w.Pos);
        w.Chunk("MCRF", 4 * nRefs);
        foreach (var r in ch.DoodadRefs) w.U32((uint)r);
        foreach (var r in ch.WmoRefs) w.U32((uint)r);

        int mcshPos = w.Pos;
        if (shadow)
        {
            w.Chunk("MCSH", MapBytes.McshSize);
            var s = ch.Shadow!;
            for (int k = 0; k < 512; k++)
            {
                int v = 0;
                for (int bit = 0; bit < 8; bit++)
                    if (s[k * 8 + bit]) v |= 1 << bit;
                w.U8((byte)v);
            }
        }

        int mcalPos = w.Pos;
        w.Chunk("MCAL", outLayers.Count == 0 ? unchecked((int)McalNoLayers) : nAlpha * MapBytes.AlphaLayerSize);
        foreach (var l in outLayers)
            if (l.Alpha4 != null) w.Bytes(l.Alpha4);
        H(0x24, (uint)mcalPos);
        H(0x28, (uint)(8 + nAlpha * MapBytes.AlphaLayerSize));
        // no MCSH: the shadow offset points at MCAL (as TBC/WotLK write it), size 0
        H(0x2C, (uint)(shadow ? mcshPos : mcalPos));
        H(0x30, shadow ? (uint)MapBytes.McshSize : 0);

        H(0x60, (uint)w.Pos);
        H(0x64, (uint)(8 + liquids.Count * MapBytes.MclqLayerSize));
        w.Chunk("MCLQ", 0);
        foreach (var (kind, l) in liquids) WriteMclqLayer(ref w, l, kind);

        H(0x58, (uint)w.Pos);
        w.Chunk("MCSE", ch.SoundEmitters.Count * MapBytes.McseSize);
        foreach (var e in ch.SoundEmitters)
        {
            var rec = buf.AsSpan(w.Pos, MapBytes.McseSize);
            if (e.Raw112 is { Length: MapBytes.McseSize } raw) raw.CopyTo(rec);
            BinaryPrimitives.WriteUInt32LittleEndian(rec, e.Id);
            WriteVec(rec[8..], e.Position);
            w.Skip(MapBytes.McseSize);
        }
        if (w.Pos != size) throw new InvalidOperationException($"MCNK {index}: wrote {w.Pos} of {size} bytes");
        return buf;
    }

    /// <summary>4-bit alpha (low nibble first) by <c>v &gt;&gt; 4</c>; lossy = a value other than 0 / 16n + 15.</summary>
    private static byte[] PackAlpha4(byte[] a, out bool lossy)
    {
        var o = new byte[MapBytes.AlphaLayerSize];
        lossy = false;
        for (int k = 0; k < MapBytes.AlphaLayerSize; k++)
        {
            int lo = a[k * 2], hi = a[k * 2 + 1];
            if (!lossy && (NeutralLayer.Expand4(lo >> 4) != lo || NeutralLayer.Expand4(hi >> 4) != hi)) lossy = true;
            o[k] = (byte)((lo >> 4) | ((hi >> 4) << 4));
        }
        return o;
    }

    /// <summary>
    /// More than 4 layers: the visible weight of each layer is computed per texel under 1.12's sequential blending (each
    /// layer drawn over the ones below with its alpha: weight_L = alpha_L x product of (1 - alpha) of the layers above;
    /// the base gets what is left), the 4 layers with the largest total weight are kept in their order, their weights are
    /// renormalised to 1 per texel, and turned back into sequential alphas (alpha_j = weight_j / sum of weights j and below).
    /// The lowest kept layer becomes the base.
    /// </summary>
    private static List<NeutralLayer> ReduceLayers(TileContext ctx, NeutralChunk ch, int index)
    {
        int n = ch.Layers.Count;
        var weight = new float[n][];
        var total = new double[n];
        for (int L = 0; L < n; L++) weight[L] = new float[4096];
        for (int t = 0; t < 4096; t++)
        {
            float rest = 1f;
            for (int L = n - 1; L >= 1; L--)
            {
                float a = (ch.Layers[L].Alpha?[t] ?? 0) / 255f;
                float wL = a * rest;
                weight[L][t] = wL;
                rest -= wL;
            }
            weight[0][t] = rest;
            for (int L = 0; L < n; L++) total[L] += weight[L][t];
        }
        var keep = Enumerable.Range(0, n).OrderByDescending(L => total[L]).ThenBy(L => L).Take(4).OrderBy(L => L).ToList();
        var outList = new List<NeutralLayer>(4);
        var alphas = keep.Select(_ => new byte[4096]).ToList();
        for (int t = 0; t < 4096; t++)
        {
            float sum = 0;
            foreach (var L in keep) sum += weight[L][t];
            float below = 0;
            for (int j = 0; j < keep.Count; j++)
            {
                float wj = sum > 0 ? weight[keep[j]][t] / sum : (j == 0 ? 1f : 0f);
                below += wj;
                if (j > 0) alphas[j][t] = (byte)Math.Clamp(MathF.Round(below > 0 ? wj / below * 255f : 0f), 0, 255);
            }
        }
        for (int j = 0; j < keep.Count; j++)
        {
            var src = ch.Layers[keep[j]];
            outList.Add(new NeutralLayer
            {
                Texture = src.Texture, EffectId = src.EffectId,
                Flags = j == 0 ? src.Flags & ~0x100u : src.Flags | 0x100,
                Alpha = j == 0 ? null : alphas[j],
            });
        }
        var dropped = Enumerable.Range(0, n).Except(keep).ToList();
        ctx.Add(LossKind.TextureLayersDropped, index, dropped.Count,
            $"{n} texture layers, kept {string.Join(", ", keep)} (most visible), dropped {string.Join(", ", dropped.Select(L => $"{L} ({ch.Layers[L].Texture}, {total[L] / 40.96:F1}% visible)"))}");
        return outList;
    }

    /// <summary>The MCLQ layers to write: MCLQ-born layers of distinct kinds as they are; otherwise the one instance that
    /// covers the most tiles.</summary>
    private static List<(LiquidKind Kind, NeutralLiquid Liquid)> ChooseLiquids(TileContext ctx, NeutralChunk ch, int index)
    {
        var res = new List<(LiquidKind, NeutralLiquid)>();
        var all = ch.Liquids;
        if (all.Count == 0) return res;
        if (all.All(l => l.Origin == LiquidOrigin.Mclq && l.Kind != null) && all.Select(l => l.Kind).Distinct().Count() == all.Count)
        {
            foreach (var l in all.OrderBy(l => (int)l.Kind!.Value)) res.Add((l.Kind!.Value, l));
            return res;
        }
        int best = 0;
        for (int i = 1; i < all.Count; i++)
            if (all[i].TileCount > all[best].TileCount) best = i;
        var pick = all[best];
        res.Add((KindOf(ctx, pick, index), pick));
        if (all.Count > 1)
            ctx.Add(LossKind.LiquidInstancesDropped, index, all.Count - 1,
                $"{all.Count} liquid instances, kept the one covering {pick.TileCount} tiles, dropped {string.Join(", ", all.Where((_, i) => i != best).Select(l => $"type {l.LiquidType?.ToString() ?? l.Kind?.ToString() ?? "?"} ({l.TileCount} tiles)"))}");
        return res;
    }

    private static LiquidKind KindOf(TileContext ctx, NeutralLiquid l, int index)
    {
        if (l.Kind is { } k) return k;
        if (l.LiquidType is { } type && ctx.Options.LiquidKind?.Invoke(type) is { } mapped)
        {
            ctx.MappedLiquids++;
            return mapped;
        }
        ctx.Add(LossKind.LiquidTypeUnmapped, index, 1, $"liquid type {l.LiquidType?.ToString() ?? "(none)"} has no 1.12 kind: written as river water");
        return LiquidKind.River;
    }

    /// <summary>One 804-byte MCLQ layer. An MH2O instance becomes: its min/max; 9 x 9 vertices from its rectangle (vertices
    /// outside it take the nearest rectangle vertex; no heights = flat at min, per MPQ_ERA_DELTA 2.7; no depth = 0); tile
    /// bytes from its exists / fishable / deep bits; no flow.</summary>
    private static void WriteMclqLayer(ref SpanWriter w, NeutralLiquid l, LiquidKind kind)
    {
        bool water = kind is LiquidKind.River or LiquidKind.Ocean;
        bool full = l.RectX == 0 && l.RectY == 0 && l.RectW == 8 && l.RectH == 8;
        w.F32(l.Min);
        w.F32(l.Max);
        for (int v = 0; v < 81; v++)
        {
            int vx = v % 9, vy = v / 9;
            int idx = full ? v : VertexIndex(l, vx, vy);
            if (water)
            {
                w.U8(l.Depth != null && idx < l.Depth.Length ? l.Depth[idx] : (byte)0);
                if (full && l.MclqFlow112 is { Length: 81 * 3 } f)
                {
                    w.U8(f[v * 3]);
                    w.U8(f[v * 3 + 1]);
                    w.U8(f[v * 3 + 2]);
                }
                else w.Skip(3);
            }
            else
            {
                w.U16(l.TexU != null && idx < l.TexU.Length ? l.TexU[idx] : (ushort)0);
                w.U16(l.TexV != null && idx < l.TexV.Length ? l.TexV[idx] : (ushort)0);
            }
            w.F32(l.Heights != null && idx < l.Heights.Length ? l.Heights[idx] : l.Min);
        }
        byte nib = Adt112Reader.MclqKindNibble[(int)kind];
        for (int k = 0; k < 64; k++)
        {
            byte tb = Adt112Reader.MclqTileByte(l, k, nib);
            if (l.MclqTileResidue112 is { Length: 64 } r) tb ^= r[k];
            w.U8(tb);
        }
        if (l.MclqFlowVectors112 is { Length: 84 } fv) w.Bytes(fv);
        else w.Skip(84);
    }

    /// <summary>Index into an instance's vertex arrays of the 9 x 9 vertex (vx, vy), clamped to its rectangle.</summary>
    private static int VertexIndex(NeutralLiquid l, int vx, int vy)
    {
        int x = Math.Clamp(vx - l.RectX, 0, Math.Max(0, l.RectW));
        int y = Math.Clamp(vy - l.RectY, 0, Math.Max(0, l.RectH));
        return y * (l.RectW + 1) + x;
    }

    // ====================================================================== WDT

    public static byte[] WriteWdt(NeutralMap map, ConversionReport? report = null, Adt112WriterOptions? options = null)
    {
        options ??= new Adt112WriterOptions();
        string name = (map.Name.Length > 0 ? map.Name : "map") + ".wdt";
        bool from112 = map.Source == MapSourceVersion.V112;
        uint flags = map.Flags & 1;
        if (!from112 && (map.Flags & ~1u) != 0)
            report?.Add(LossKind.MapFlagsCleared, name, null, 1, $"MPHD flags 0x{map.Flags & ~1u:X} have no 1.12 meaning: cleared");
        else if (from112) flags = map.Flags;
        var extra = from112 ? map.MphdExtra : new uint[7];
        int refs = (from112 ? 0 : map.MphdExtra.Count(v => v != 0)) + map.Tiles.Count(t => t.Maid != null);
        if (refs > 0) report?.Add(LossKind.MapFileRefsDropped, name, null, refs, $"{refs} MPHD/MAID file references dropped (1.12 finds the files by name)");
        if (map.ExtraChunks.Count > 0)
            report?.Add(LossKind.UnknownChunksDropped, name, null, map.ExtraChunks.Count, "chunks dropped: " + string.Join(" ", map.ExtraChunks.Select(c => c.Magic)));
        byte[] wmoName = Array.Empty<byte>();
        bool global = (flags & 1) != 0 && map.GlobalWmo != null;
        if (global)
        {
            var g = map.GlobalWmo!;
            string path = g.Model.HasPath ? g.Model.Path! : $@"{options.ConvertedFolder}\{g.Model.FileDataId?.ToString() ?? "missing"}.wmo";
            if (!g.Model.HasPath)
                report?.Add(g.Model.FileDataId != null ? LossKind.AssetPathMadeUp : LossKind.AssetMissing, name, null, 1, $"global WMO {g.Model} has no 1.12 path: written as {path}");
            wmoName = Encoding.Latin1.GetBytes(path + "\0");
        }
        int size = 12 + 8 + MapBytes.WdtMphdSize + 8 + MapBytes.WdtMainSize + 8 + wmoName.Length + (global ? 8 + MapBytes.ModfSize : 0);
        var buf = new byte[size];
        var w = new SpanWriter(buf);
        w.Chunk("MVER", 4);
        w.U32(18);
        w.Chunk("MPHD", MapBytes.WdtMphdSize);
        w.U32(flags);
        foreach (var v in extra) w.U32(v);
        w.Chunk("MAIN", MapBytes.WdtMainSize);
        foreach (var t in map.Tiles)
        {
            uint f = from112 ? (t.Flags & ~1u) | (t.HasAdt ? 1u : 0u) : (t.HasAdt ? 1u : 0u);
            w.U32(f);
            w.U32(from112 ? t.AsyncId : 0);
        }
        w.Chunk("MWMO", wmoName.Length);
        w.Bytes(wmoName);
        if (global)
        {
            w.Chunk("MODF", MapBytes.ModfSize);
            int c = 0, s = 0;
            uint bits = 0;
            var (mf, pad) = Modf112Flags(map.GlobalWmo!, from112, ref c, ref s, ref bits);
            EncodeModfEntry(buf.AsSpan(w.Pos, MapBytes.ModfSize), map.GlobalWmo!, 0, mf, pad);
            w.Skip(MapBytes.ModfSize);
            if (c > 0) report?.Add(LossKind.PlacementFlagsCleared, name, null, 1, $"global WMO flag bits 0x{bits:X} cleared");
            if (s > 0) report?.Add(LossKind.WmoScaleDropped, name, null, 1, "global WMO scale other than 1 dropped");
        }
        return buf;
    }

    // ====================================================================== WDL

    public static byte[] WriteWdl(NeutralWdl wdl, ConversionReport? report = null, string name = "map.wdl")
    {
        if (wdl.Holes.Count > 0) report?.Add(LossKind.FarHolesDropped, name, null, wdl.Holes.Count, $"far hole masks (MAHO) of {wdl.Holes.Count} tiles dropped");
        if (wdl.ExtraChunks.Count > 0)
            report?.Add(LossKind.UnknownChunksDropped, name, null, wdl.ExtraChunks.Count, "chunks dropped: " + string.Join(" ", wdl.ExtraChunks.Select(c => c.Magic)));
        int size = 12 + 8 + MapBytes.WdlMaofSize + wdl.Heights.Count * (8 + MapBytes.WdlMareData);
        var buf = new byte[size];
        var w = new SpanWriter(buf);
        w.Chunk("MVER", 4);
        w.U32(18);
        w.Chunk("MAOF", MapBytes.WdlMaofSize);
        int table = w.Pos;
        w.Skip(MapBytes.WdlMaofSize);
        foreach (var (i, h) in wdl.Heights)
        {
            if (h.Length != 545) throw new ArgumentException($"WDL tile {i}: 545 heights expected, got {h.Length}");
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(table + i * 4), (uint)w.Pos);
            w.Chunk("MARE", MapBytes.WdlMareData);
            foreach (var v in h) w.U16((ushort)v);
        }
        return buf;
    }

    // ====================================================================== bytes

    /// <summary>Little-endian writes into a pre-sized buffer.</summary>
    private ref struct SpanWriter
    {
        private readonly byte[] _buf;
        public int Pos;

        public SpanWriter(byte[] buf)
        {
            _buf = buf;
            Pos = 0;
        }

        /// <summary>A chunk header: the magic stored reversed, then the size.</summary>
        public void Chunk(string magic, int size)
        {
            _buf[Pos] = (byte)magic[3];
            _buf[Pos + 1] = (byte)magic[2];
            _buf[Pos + 2] = (byte)magic[1];
            _buf[Pos + 3] = (byte)magic[0];
            BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(Pos + 4), size);
            Pos += 8;
        }

        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(Pos), v); Pos += 4; }
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(Pos), v); Pos += 2; }
        public void U8(byte v) { _buf[Pos++] = v; }
        public void F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(Pos), v); Pos += 4; }
        public void Bytes(byte[] b) { b.CopyTo(_buf, Pos); Pos += b.Length; }
        public void Skip(int n) => Pos += n;
    }
}
