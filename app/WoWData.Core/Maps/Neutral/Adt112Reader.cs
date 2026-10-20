using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.Maps.Neutral;

/// <summary>
/// 1.12.1 map files (ADT, WDT, WDL) into the neutral model. The files are located the way the 1.12 client reads them
/// (<see cref="AdtFile"/>, <see cref="McnkRecord"/>, <see cref="WdtFile"/>, <see cref="WdlFile"/>: every sub-chunk through
/// its MCNK header offset + 8, decompile evidence in <see cref="MapValidator"/>). Everything the 1.12 format stores is
/// modelled; the few bytes with no meaning (the 13 bytes after MCNR, MCLQ flow vectors, the details of MCSE records)
/// are kept in the <c>...112</c> passthrough fields, so <see cref="Adt112Writer"/> writes every stock tile back byte for byte.
///
/// Layout facts measured on all 2,429 stock tiles (621,824 chunks) that the writer relies on: top-level order
/// MVER MHDR MCIN MTEX MMDX MMID MWMO MWID MDDF MODF MCNK x 256 with nothing in between; MCNK sub-chunks MCVT MCNR MCLY
/// MCRF MCSH MCAL MCLQ MCSE in that order, MCNR followed by 13 pad bytes; MCSH in every chunk (also the 139,223 without
/// flag 0x1); MCAL's size field (nLayers - 1) * 2048 (0xFFFFF800 for no layers); MCLQ's size field 0 with its 804-byte
/// layers behind it; MMID / MWID the offsets of the names in order; MCIN size = MCNK size + 8, flags and async id 0.
/// </summary>
public static class Adt112Reader
{
    /// <summary>MCLQ tile low nibble of a tile that has liquid, per 1.12 kind (river 4, ocean 1, magma 6: measured on every
    /// stock layer; slime 3 is an ASSUMPTION: no stock 1.12 tile has slime, so it is unverified).</summary>
    public static readonly byte[] MclqKindNibble = { 4, 1, 6, 3 };

    /// <summary>(x, y) from a tile file name "Map_x_y.adt", or null.</summary>
    public static (int X, int Y)? TileCoords(string adtName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(adtName.Replace('\\', '/').Split('/')[^1]);
        var parts = stem.Split('_');
        if (parts.Length >= 3 && int.TryParse(parts[^2], out int x) && int.TryParse(parts[^1], out int y) && x is >= 0 and < 64 && y is >= 0 and < 64)
            return (x, y);
        return null;
    }

    // ------------------------------------------------------------------ ADT

    public static NeutralTile ReadAdt(byte[] b, string map = "", int x = -1, int y = -1)
    {
        var adt = new AdtFile(b);
        var t = new NeutralTile
        {
            Map = map, X = x, Y = y, Source = MapSourceVersion.V112, Version = adt.Version ?? 18,
            HeaderFlags = adt.Mhdr.GetValueOrDefault("flags"),
        };
        foreach (var name in adt.Textures) t.Textures.Add(new AssetRef(name));
        ReadNames(adt.Mmdx, adt.Mmid, t.DoodadModels);
        ReadNames(adt.Mwmo, adt.Mwid, t.WmoModels);
        foreach (var d in adt.Mddf)
            t.Doodads.Add(new NeutralDoodad
            {
                Model = d.NameId < t.DoodadModels.Count ? t.DoodadModels[(int)d.NameId] : new AssetRef(),
                UniqueId = d.Uid, Position = d.Position, Rotation = d.Rotation, ScaleRaw = d.Scale, Flags = d.Flags,
            });
        foreach (var w in adt.Modf)
            t.Wmos.Add(new NeutralWmo
            {
                Model = w.NameId < t.WmoModels.Count ? t.WmoModels[(int)w.NameId] : new AssetRef(),
                UniqueId = w.Uid, Position = w.Position, Rotation = w.Rotation,
                ExtentMin = new Vector3(w.Extents[0], w.Extents[1], w.Extents[2]), ExtentMax = new Vector3(w.Extents[3], w.Extents[4], w.Extents[5]),
                Flags = w.Flags, DoodadSet = w.DoodadSet, NameSet = w.NameSet, ScaleRaw = w.Pad,
            });
        for (int i = 0; i < adt.Mcnk.Count; i++)
        {
            var c = adt.Mcnk[i];
            if (c == null || c.Magic != "MCNK" || !c.HasHeader || c.End > b.Length) continue;
            int idx = c["indexX"] < 16 && c["indexY"] < 16 ? c.IndexY * 16 + c.IndexX : i;
            if (t.Chunks[idx] != null) continue;
            t.Chunks[idx] = ReadChunk(b, adt, t, c);
        }
        // top-level chunks that 1.12 does not have (a TBC/WotLK tile read with this reader)
        var known = new HashSet<string> { "MVER", "MHDR", "MCIN", "MTEX", "MMDX", "MMID", "MWMO", "MWID", "MDDF", "MODF", "MCNK" };
        foreach (var top in adt.Top)
        {
            if (top.Magic == null || known.Contains(top.Magic) || top.Offset + 8 + top.Size > b.Length) continue;
            var data = MapBytes.Slice(b, top.Offset + 8, top.Offset + 8 + top.Size);
            if (top.Magic == "MFBO" && data.Length >= 36)
            {
                t.FlightBounds = new short[18];
                for (int k = 0; k < 18; k++) t.FlightBounds[k] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(k * 2));
            }
            else if (top.Magic == "MTXF")
            {
                for (int k = 0; k + 4 <= data.Length; k += 4) t.TextureParams.Add(new TextureParams { Flags = MapBytes.U32(data, k) });
            }
            else t.ExtraChunks.Add(new RawChunk(top.Magic, data));
        }
        return t;
    }

    /// <summary>The name list in MMID/MWID order (each entry = the name at that offset; one object per entry, so
    /// placements keep pointing at the same entry even when two entries spell the same name).</summary>
    private static void ReadNames(List<KeyValuePair<uint, string>> names, uint[] ids, List<AssetRef> into)
    {
        var byOfs = new Dictionary<uint, string>();
        foreach (var kv in names) byOfs.TryAdd(kv.Key, kv.Value);
        foreach (var o in ids) into.Add(byOfs.TryGetValue(o, out var n) ? new AssetRef(n) : new AssetRef());
    }

    private static NeutralChunk ReadChunk(byte[] b, AdtFile adt, NeutralTile t, McnkRecord c)
    {
        long hdr = c.Offset + 8, end = Math.Min(c.End, b.Length);
        uint flags = c.Flags;
        var ch = new NeutralChunk
        {
            IndexX = c.IndexX, IndexY = c.IndexY, Flags = flags, AreaId = c["areaId"],
            Position = new Vector3(c.Float("posX"), c.Float("posY"), c.Float("posZ")),
            Holes = NeutralChunk.ExpandLowResHoles(c["holes"] & 0xFFFF),
            LowQualityTextureMap = MapBytes.Slice(b, hdr + 0x40, hdr + 0x50),
            NoEffectDoodad = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)hdr + 0x50, 8)),
        };
        long p = c.DataOffset("ofsHeight");
        if (p + MapBytes.McvtSize <= end)
            for (int i = 0; i < 145; i++) ch.Heights[i] = MapBytes.F32(b, p + i * 4);
        p = c.DataOffset("ofsNormal");
        if (p + MapBytes.McnrSize <= end)
        {
            for (int i = 0; i < MapBytes.McnrSize; i++) ch.Normals[i] = (sbyte)b[p + i];
            long pad = p + MapBytes.McnrSize;
            if (pad + MapBytes.McnrPad <= end) ch.NormalPad112 = MapBytes.Slice(b, pad, pad + MapBytes.McnrPad);
        }
        // layers + alpha
        long ly = c.DataOffset("ofsLayer"), mcal = c.DataOffset("ofsAlpha");
        uint nl = c["nLayers"];
        for (int L = 0; L < nl && ly + L * 16 + 16 <= end; L++)
        {
            long q = ly + L * 16;
            uint tid = MapBytes.U32(b, q), lf = MapBytes.U32(b, q + 4), oa = MapBytes.U32(b, q + 8), eff = MapBytes.U32(b, q + 12);
            var layer = new NeutralLayer
            {
                Texture = tid < t.Textures.Count ? t.Textures[(int)tid] : new AssetRef(), Flags = lf, EffectId = eff,
            };
            long a = mcal + oa;
            if ((lf & 0x100) != 0 && a + MapBytes.AlphaLayerSize <= end)
            {
                var alpha = new byte[4096];
                for (int k = 0; k < MapBytes.AlphaLayerSize; k++)
                {
                    byte v = b[a + k];
                    alpha[k * 2] = NeutralLayer.Expand4(v & 0xF);      // low nibble first
                    alpha[k * 2 + 1] = NeutralLayer.Expand4(v >> 4);
                }
                layer.Alpha = alpha;
            }
            ch.Layers.Add(layer);
        }
        // refs: doodads first, then WMOs
        p = c.DataOffset("ofsRefs");
        uint nd = c["nDoodadRefs"], nw = c["nMapObjRefs"];
        for (int k = 0; k < nd + nw && p + k * 4 + 4 <= end; k++)
            (k < nd ? ch.DoodadRefs : ch.WmoRefs).Add((int)MapBytes.U32(b, p + k * 4));
        // shadow: the MCSH the header points at (every stock chunk has one, flag 0x1 or not)
        long sh = c.Offset + c["ofsShadow"];
        if ((c["sizeShadow"] >= MapBytes.McshSize || (flags & 1) != 0) && MapBytes.MagicAt(b, sh) == "MCSH" && sh + 8 + MapBytes.McshSize <= end)
        {
            var s = new bool[4096];
            for (int k = 0; k < 4096; k++) s[k] = (b[sh + 8 + k / 8] & (1 << (k % 8))) != 0;
            ch.Shadow = s;
        }
        // liquids: one 804-byte MCLQ layer per flag bit, in bit order
        long lq = c.DataOffset("ofsLiquid");
        int layerNo = 0;
        for (int bit = 0; bit < 4; bit++)
        {
            if ((flags & MapBytes.McnkLiquidFlags[bit]) == 0) continue;
            long q = lq + layerNo++ * MapBytes.MclqLayerSize;
            if (q + MapBytes.MclqLayerSize > end) break;
            ch.Liquids.Add(ReadMclqLayer(b, q, (LiquidKind)bit));
        }
        // sound emitters (52-byte records)
        p = c.DataOffset("ofsSndEmitters");
        uint ns = c["nSndEmitters"];
        for (int k = 0; k < ns && p + (k + 1) * MapBytes.McseSize <= end; k++)
        {
            long e = p + k * MapBytes.McseSize;
            ch.SoundEmitters.Add(new NeutralSoundEmitter
            {
                Id = MapBytes.U32(b, e),
                Position = new Vector3(MapBytes.F32(b, e + 8), MapBytes.F32(b, e + 12), MapBytes.F32(b, e + 16)),
                Raw112 = MapBytes.Slice(b, e, e + MapBytes.McseSize),
            });
        }
        return ch;
    }

    /// <summary>One MCLQ layer: min, max, 9 x 9 vertices (water: depth, flow0, flow1, filler, height; magma/slime: u16 s,
    /// u16 t, height), 8 x 8 tile bytes, flow vector count, 2 flow vectors.</summary>
    public static NeutralLiquid ReadMclqLayer(byte[] b, long q, LiquidKind kind)
    {
        bool water = kind is LiquidKind.River or LiquidKind.Ocean;
        var l = new NeutralLiquid
        {
            Origin = LiquidOrigin.Mclq, Kind = kind, Min = MapBytes.F32(b, q), Max = MapBytes.F32(b, q + 4),
            RectX = 0, RectY = 0, RectW = 8, RectH = 8, Heights = new float[81],
        };
        byte[]? flow = null;
        if (water) l.Depth = new byte[81];
        else
        {
            l.TexU = new ushort[81];
            l.TexV = new ushort[81];
        }
        for (int v = 0; v < 81; v++)
        {
            long o = q + 8 + v * 8;
            l.Heights[v] = MapBytes.F32(b, o + 4);
            if (water)
            {
                l.Depth![v] = b[o];
                if (b[o + 1] != 0 || b[o + 2] != 0 || b[o + 3] != 0)
                {
                    flow ??= new byte[81 * 3];
                    flow[v * 3] = b[o + 1];
                    flow[v * 3 + 1] = b[o + 2];
                    flow[v * 3 + 2] = b[o + 3];
                }
            }
            else
            {
                l.TexU![v] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)o));
                l.TexV![v] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)o + 2));
            }
        }
        l.MclqFlow112 = flow;
        ulong exists = 0, fish = 0, deep = 0;
        long tiles = q + MapBytes.MclqTilesOfs;
        for (int k = 0; k < 64; k++)
        {
            byte tb = b[tiles + k];
            if ((tb & 0xF) != 0xF) exists |= 1UL << k;
            if ((tb & 0x40) != 0) fish |= 1UL << k;
            if ((tb & 0x80) != 0) deep |= 1UL << k;
        }
        l.Exists = exists;
        l.Fishable = fish;
        l.Deep = deep;
        byte[]? residue = null;
        for (int k = 0; k < 64; k++)
        {
            byte r = (byte)(b[tiles + k] ^ MclqTileByte(l, k, MclqKindNibble[(int)kind]));
            if (r != 0) (residue ??= new byte[64])[k] = r;
        }
        l.MclqTileResidue112 = residue;
        var fv = MapBytes.Slice(b, tiles + 64, tiles + 64 + 84);
        l.MclqFlowVectors112 = fv.Any(x => x != 0) ? fv : null;
        return l;
    }

    /// <summary>The MCLQ tile byte by the rule every stock layer follows: (has liquid ? kind nibble : 0xF) | 0x40 fishable |
    /// 0x80 deep.</summary>
    public static byte MclqTileByte(NeutralLiquid l, int k, byte kindNibble)
    {
        ulong bit = 1UL << k;
        int v = (l.Exists & bit) != 0 ? kindNibble : 0xF;
        if ((l.Fishable & bit) != 0) v |= 0x40;
        if ((l.Deep & bit) != 0) v |= 0x80;
        return (byte)v;
    }

    // ------------------------------------------------------------------ WDT

    public static NeutralMap ReadWdt(byte[] b, string map = "")
    {
        var w = new WdtFile(b);
        var m = new NeutralMap { Name = map, Source = MapSourceVersion.V112, Version = w.Version ?? 18, Flags = w.MphdFlags ?? 0 };
        for (int k = 0; k < 7; k++) m.MphdExtra[k] = w.Mphd.Length >= 4 + (k + 1) * 4 ? MapBytes.U32(w.Mphd, 4 + k * 4) : 0;
        for (int i = 0; i < 4096; i++)
        {
            var e = m.Tiles[i];
            e.Flags = w.Main[i].Flags;
            e.AsyncId = w.Main[i].AsyncId;
            e.HasAdt = (e.Flags & 1) != 0;
        }
        if (w.IsGlobalWmo && w.GlobalModf is { Length: MapBytes.ModfSize } modf)
        {
            var ext = new float[6];
            for (int k = 0; k < 6; k++) ext[k] = MapBytes.F32(modf, 32 + k * 4);
            m.GlobalWmo = new NeutralWmo
            {
                Model = new AssetRef(w.GlobalWmoName), UniqueId = MapBytes.U32(modf, 4),
                Position = new Vector3(MapBytes.F32(modf, 8), MapBytes.F32(modf, 12), MapBytes.F32(modf, 16)),
                Rotation = new Vector3(MapBytes.F32(modf, 20), MapBytes.F32(modf, 24), MapBytes.F32(modf, 28)),
                ExtentMin = new Vector3(ext[0], ext[1], ext[2]), ExtentMax = new Vector3(ext[3], ext[4], ext[5]),
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(modf.AsSpan(56)), DoodadSet = BinaryPrimitives.ReadUInt16LittleEndian(modf.AsSpan(58)),
                NameSet = BinaryPrimitives.ReadUInt16LittleEndian(modf.AsSpan(60)), ScaleRaw = BinaryPrimitives.ReadUInt16LittleEndian(modf.AsSpan(62)),
            };
        }
        // chunks after what the client reads (none in the stock WDTs besides the empty MWMO of tile maps)
        long pos = w.EndOfRead;
        if (!w.IsGlobalWmo && pos + 8 <= b.Length && MapBytes.MagicAt(b, pos) == "MWMO") pos += 8 + MapBytes.U32(b, pos + 4);
        foreach (var top in MapBytes.Chunks(b, pos))
            if (top.Magic != null && top.Offset + 8 + top.Size <= b.Length)
                m.ExtraChunks.Add(new RawChunk(top.Magic, MapBytes.Slice(b, top.Offset + 8, top.Offset + 8 + top.Size)));
        return m;
    }

    /// <summary>Fills the minimap references of a map from 1.12's textures\Minimap\md5translate.trs ("&lt;dir&gt;\mapX_Y.blp"
    /// -&gt; hashed name).</summary>
    public static int ReadMinimaps(NeutralMap map, string directory, byte[]? md5translate)
    {
        var table = MapCatalog.ParseMd5Translate(md5translate);
        int n = 0;
        for (int i = 0; i < 4096; i++)
            if (table.TryGetValue($@"{directory}\map{i % 64:D2}_{i / 64:D2}.blp", out var h))
            {
                map.Tiles[i].Minimap = new AssetRef($@"textures\Minimap\{h}");
                n++;
            }
        return n;
    }

    // ------------------------------------------------------------------ WDL

    public static NeutralWdl ReadWdl(byte[] b)
    {
        var w = new WdlFile(b);
        var d = new NeutralWdl { Source = MapSourceVersion.V112, Version = w.Version ?? 18 };
        for (int i = 0; i < w.Offsets.Length; i++)
        {
            if (w.Offsets[i] == 0) continue;
            var h = w.Heights(i % 64, i / 64);
            if (h != null) d.Heights[i] = h;
        }
        // MAHO (TBC+: the far hole mask of the MARE before it) and anything else
        var tileAt = new Dictionary<long, int>();
        for (int i = 0; i < w.Offsets.Length; i++)
            if (w.Offsets[i] != 0) tileAt.TryAdd(w.Offsets[i], i);
        int lastTile = -1;
        foreach (var top in MapBytes.Chunks(b))
        {
            if (top.Magic == null || top.Offset + 8 + top.Size > b.Length) continue;
            if (top.Magic == "MARE") lastTile = tileAt.GetValueOrDefault(top.Offset, -1);
            else if (top.Magic == "MAHO" && lastTile >= 0 && top.Size >= 32)
            {
                var holes = new ushort[16];
                for (int k = 0; k < 16; k++) holes[k] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)top.Offset + 8 + k * 2));
                d.Holes[lastTile] = holes;
            }
            else if (top.Magic is not ("MVER" or "MAOF"))
                d.ExtraChunks.Add(new RawChunk(top.Magic, MapBytes.Slice(b, top.Offset + 8, top.Offset + 8 + top.Size)));
        }
        return d;
    }
}
