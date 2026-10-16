using System.Buffers.Binary;
using System.Text;

namespace WoWData.Core.Maps;

/// <summary>
/// Readers for the 1.12.1 (build 5875) map files: ADT tiles, WDT and WDL (port of wowdata/maps/formats.py).
/// Everything is read the way the 1.12 client does (the evidence is in <see cref="MapValidator"/>). Chunk magics are
/// stored reversed on disk ('REVM' for MVER); they are returned forward here. The readers never throw on malformed
/// input: what cannot be read stays empty, and the validator reports it.
/// </summary>
public static class MapBytes
{
    public const int McnkHeaderSize = 0x80;
    public const int McvtSize = 145 * 4;           // 9x9 + 8x8 float heights
    public const int McnrSize = 145 * 3;           // signed-byte normals (Blizzard pads 13 bytes after the chunk)
    public const int McnrPad = 13;
    public const int McshSize = 64 * 64 / 8;       // 1-bit shadow map
    public const int AlphaLayerSize = 64 * 64 / 2; // 4-bit alpha, 2048 bytes
    public const int BigAlphaLayerSize = 64 * 64;  // 8-bit alpha (WotLK), 4096 bytes
    public const int MclySize = 16;
    public const int McseSize = 0x34;              // 52-byte 1.12 sound emitter
    public const int MclqLayerSize = 0xC9 * 4;     // 804 bytes: min, max, 9x9 x 8 bytes, 8x8 tile bytes, n_flowvs, 2 x 40 bytes
    public const int MclqTilesOfs = 8 + 81 * 8;    // tile flag bytes inside one layer
    public const int MddfSize = 0x24;
    public const int ModfSize = 0x40;
    public const int McinEntry = 16;
    public const int WdlMareData = 0x442;          // 17*17 + 16*16 int16
    public const int WdtMphdSize = 0x20;
    public const int WdtMainSize = 64 * 64 * 8;
    public const int WdlMaofSize = 64 * 64 * 4;

    /// <summary>MCNK header fields (offsets inside the 0x80-byte header that follows the 8-byte chunk header).</summary>
    public static readonly (string Name, int Offset)[] McnkFields =
    {
        ("flags", 0x00), ("indexX", 0x04), ("indexY", 0x08), ("nLayers", 0x0C), ("nDoodadRefs", 0x10),
        ("ofsHeight", 0x14), ("ofsNormal", 0x18), ("ofsLayer", 0x1C), ("ofsRefs", 0x20), ("ofsAlpha", 0x24),
        ("sizeAlpha", 0x28), ("ofsShadow", 0x2C), ("sizeShadow", 0x30), ("areaId", 0x34), ("nMapObjRefs", 0x38),
        ("holes", 0x3C), ("ofsSndEmitters", 0x58), ("nSndEmitters", 0x5C), ("ofsLiquid", 0x60),
        ("sizeLiquid", 0x64), ("posX", 0x68), ("posY", 0x6C), ("posZ", 0x70), ("ofsMCCV", 0x74),
        ("unused78", 0x78), ("unused7C", 0x7C),
    };

    public static readonly HashSet<string> FloatFields = new() { "posX", "posY", "posZ" };
    /// <summary>One MCLQ layer per MCNK flag bit, in this order: river, ocean, magma, slime.</summary>
    public static readonly uint[] McnkLiquidFlags = { 0x04, 0x08, 0x10, 0x20 };

    /// <summary>MHDR data fields (offsets relative to the MHDR data start = file offset 0x14).</summary>
    public static readonly string[] MhdrFields =
    {
        "flags", "ofsMCIN", "ofsMTEX", "ofsMMDX", "ofsMMID", "ofsMWMO", "ofsMWID", "ofsMDDF",
        "ofsMODF", "ofsMFBO", "ofsMH2O", "ofsMTXF", "unused30", "unused34", "unused38", "unused3C",
    };

    public static readonly (string Field, string Magic)[] MhdrChunks =
    {
        ("ofsMCIN", "MCIN"), ("ofsMTEX", "MTEX"), ("ofsMMDX", "MMDX"), ("ofsMMID", "MMID"),
        ("ofsMWMO", "MWMO"), ("ofsMWID", "MWID"), ("ofsMDDF", "MDDF"), ("ofsMODF", "MODF"),
    };

    /// <summary>Chunks that exist only in later expansions (never in a 1.12 file).</summary>
    public static readonly HashSet<string> WotlkTopChunks = new()
    {
        "MH2O", "MFBO", "MTXF", "MTXP", "MAMP", "MBMH", "MBBB", "MBNV", "MBMI", "MLHD", "MLVH",
        "MLVI", "MLLL", "MLND", "MLSI", "MLLD", "MLLN", "MLLV", "MLLI", "MLMD", "MLMX", "MLDD",
        "MLDX", "MLDL", "MLFD", "MLMB", "MTCG", "MAHO", "MLDB", "MWDR", "MWDS",
    };

    public static readonly HashSet<string> WotlkSubChunks = new() { "MCCV", "MCLV", "MCBB", "MCMT", "MCDD", "MCRD", "MCRW", "MCLW" };

    /// <summary>The (forward) magic at a position, or null when it does not fit.</summary>
    public static string? MagicAt(byte[] b, long pos)
    {
        if (pos < 0 || pos + 4 > b.Length) return null;
        int p = (int)pos;
        return new string(new[] { (char)b[p + 3], (char)b[p + 2], (char)b[p + 1], (char)b[p] });
    }

    public static uint U32(byte[] b, long pos) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)pos, 4));
    public static float F32(byte[] b, long pos) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan((int)pos, 4));
    public static uint U32Or0(byte[] b, long pos) => pos >= 0 && pos + 4 <= b.Length ? U32(b, pos) : 0;

    /// <summary>A slice like Python's b[start:stop] (clamped, never throws).</summary>
    public static byte[] Slice(byte[] b, long start, long stop)
    {
        start = Math.Clamp(start, 0, b.Length);
        stop = Math.Clamp(stop, 0, b.Length);
        if (stop <= start) return Array.Empty<byte>();
        return b.AsSpan((int)start, (int)(stop - start)).ToArray();
    }

    public readonly record struct TopChunk(long Offset, string? Magic, long Size);

    /// <summary>Sequential top-level chunks: (offset of the 8-byte header, magic, size). Stops at a truncated header or a
    /// chunk that runs past <paramref name="end"/> (that last chunk is still returned, with its declared size).</summary>
    public static List<TopChunk> Chunks(byte[] b, long pos = 0, long? end = null)
    {
        long e = end ?? b.Length;
        var list = new List<TopChunk>();
        while (pos + 8 <= e)
        {
            var m = MagicAt(b, pos);
            long s = U32(b, pos + 4);
            list.Add(new TopChunk(pos, m, s));
            if (pos + 8 + s > e) break;
            pos += 8 + s;
        }
        return list;
    }

    /// <summary>{offset: string} of the zero-terminated strings in a string block, in order.</summary>
    public static List<KeyValuePair<uint, string>> ZStrings(byte[] data)
    {
        var outList = new List<KeyValuePair<uint, string>>();
        int pos = 0;
        while (pos < data.Length)
        {
            int e = Array.IndexOf(data, (byte)0, pos);
            if (e < 0)
            {
                outList.Add(new((uint)pos, Encoding.Latin1.GetString(data, pos, data.Length - pos)));
                break;
            }
            outList.Add(new((uint)pos, Encoding.Latin1.GetString(data, pos, e - pos)));
            pos = e + 1;
        }
        return outList;
    }
}

/// <summary>One MCNK as the client sees it: header fields plus the sub-chunks located through the header offsets.</summary>
public sealed class McnkRecord
{
    public int Index { get; }
    /// <summary>File offset of the 8-byte 'MCNK' header.</summary>
    public long Offset { get; }
    public long McinSize { get; }
    public string? Magic { get; }
    public long? Size { get; }
    /// <summary>Header fields (empty when the header does not fit in the file). Float fields hold their bits.</summary>
    public Dictionary<string, uint> H { get; } = new();
    public byte[] LowQualityMap { get; } = Array.Empty<byte>();
    public uint NoEffectDoodad { get; }
    public long End { get; }

    public bool HasHeader => H.Count > 0;

    public McnkRecord(byte[] b, int index, long offset, long mcinSize)
    {
        Index = index;
        Offset = offset;
        McinSize = mcinSize;
        Magic = MapBytes.MagicAt(b, offset);
        Size = offset + 8 <= b.Length ? MapBytes.U32(b, offset + 4) : null;
        if (offset + 8 + MapBytes.McnkHeaderSize <= b.Length)
        {
            foreach (var (name, o) in MapBytes.McnkFields) H[name] = MapBytes.U32(b, offset + 8 + o);
            LowQualityMap = MapBytes.Slice(b, offset + 8 + 0x40, offset + 8 + 0x50);
            NoEffectDoodad = MapBytes.U32(b, offset + 8 + 0x50);
        }
        End = offset + 8 + (Size ?? 0);
    }

    public uint this[string field] => H.GetValueOrDefault(field);
    public float Float(string field) => BitConverter.UInt32BitsToSingle(H.GetValueOrDefault(field));
    public uint Flags => this["flags"];
    public int IndexX => (int)this["indexX"];
    public int IndexY => (int)this["indexY"];
    public int AreaId => (int)this["areaId"];

    /// <summary>File offset of the data of the sub-chunk addressed by a header field: MCNK start + offset + 8 (the
    /// client never checks the sub-chunk magic or size).</summary>
    public long DataOffset(string field) => Offset + this[field] + 8;
}

public readonly record struct MddfEntry(uint NameId, uint Uid, float X, float Y, float Z, float RotX, float RotY, float RotZ, ushort Scale, ushort Flags)
{
    public System.Numerics.Vector3 Position => new(X, Y, Z);
    public System.Numerics.Vector3 Rotation => new(RotX, RotY, RotZ);
}

public readonly record struct ModfEntry(uint NameId, uint Uid, float X, float Y, float Z, float RotX, float RotY, float RotZ,
    float[] Extents, ushort Flags, ushort DoodadSet, ushort NameSet, ushort Pad)
{
    public System.Numerics.Vector3 Position => new(X, Y, Z);
    public System.Numerics.Vector3 Rotation => new(RotX, RotY, RotZ);
}

public readonly record struct McinEntryRec(uint Offset, uint Size, uint Flags, uint AsyncId);

/// <summary>An ADT tile, located the way CMapArea_ParseAdtHeader @006c2010 does (port of formats.Adt).</summary>
public sealed class AdtFile
{
    public string Name { get; }
    public byte[] Data { get; }
    public List<MapBytes.TopChunk> Top { get; }
    public uint? MverSize { get; }
    public uint? Version { get; }
    public long MhdrPos { get; }
    public long MhdrData { get; }
    /// <summary>MHDR fields by name (empty when the MHDR does not fit).</summary>
    public Dictionary<string, uint> Mhdr { get; } = new();
    /// <summary>magic -> (header offset, size or null when the header is outside the file, data bytes).</summary>
    public Dictionary<string, (long Header, long? Size, byte[] Data)> Blocks { get; } = new();
    public List<string> Textures { get; }
    public List<KeyValuePair<uint, string>> Mmdx { get; }
    public List<KeyValuePair<uint, string>> Mwmo { get; }
    public uint[] Mmid { get; }
    public uint[] Mwid { get; }
    public List<MddfEntry> Mddf { get; } = new();
    public List<ModfEntry> Modf { get; } = new();
    public List<McinEntryRec> Mcin { get; } = new();
    public List<McnkRecord?> Mcnk { get; } = new();

    public AdtFile(byte[] b, string name = "")
    {
        Name = name;
        Data = b;
        Top = MapBytes.Chunks(b);
        MverSize = b.Length >= 8 ? MapBytes.U32(b, 4) : null;
        Version = b.Length >= 12 ? MapBytes.U32(b, 8) : null;
        MhdrPos = 8 + (MverSize ?? 0);
        MhdrData = MhdrPos + 8;
        if (MhdrData + 64 <= b.Length)
            for (int i = 0; i < 16; i++) Mhdr[MapBytes.MhdrFields[i]] = MapBytes.U32(b, MhdrData + i * 4);
        foreach (var (field, magic) in MapBytes.MhdrChunks)
        {
            if (!Mhdr.TryGetValue(field, out var ofs)) continue;
            long hp = MhdrData + ofs;
            if (hp + 8 > b.Length)
            {
                Blocks[magic] = (hp, null, Array.Empty<byte>());
                continue;
            }
            long size = MapBytes.U32(b, hp + 4);
            Blocks[magic] = (hp, size, MapBytes.Slice(b, hp + 8, hp + 8 + size));
        }
        var mtex = Block("MTEX");
        Textures = mtex.Length > 0 ? MapBytes.ZStrings(mtex).Select(kv => kv.Value).ToList() : new List<string>();
        if (Textures.Count > 0 && Textures[^1] == "") Textures.RemoveAt(Textures.Count - 1);
        Mmdx = MapBytes.ZStrings(Block("MMDX"));
        Mwmo = MapBytes.ZStrings(Block("MWMO"));
        Mmid = U32s(Block("MMID"));
        Mwid = U32s(Block("MWID"));
        var d = Block("MDDF");
        for (int i = 0; i < d.Length / MapBytes.MddfSize; i++)
        {
            int p = i * MapBytes.MddfSize;
            Mddf.Add(new MddfEntry(MapBytes.U32(d, p), MapBytes.U32(d, p + 4), MapBytes.F32(d, p + 8), MapBytes.F32(d, p + 12), MapBytes.F32(d, p + 16),
                MapBytes.F32(d, p + 20), MapBytes.F32(d, p + 24), MapBytes.F32(d, p + 28),
                BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 32)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 34))));
        }
        d = Block("MODF");
        for (int i = 0; i < d.Length / MapBytes.ModfSize; i++)
        {
            int p = i * MapBytes.ModfSize;
            var ext = new float[6];
            for (int k = 0; k < 6; k++) ext[k] = MapBytes.F32(d, p + 32 + k * 4);
            Modf.Add(new ModfEntry(MapBytes.U32(d, p), MapBytes.U32(d, p + 4), MapBytes.F32(d, p + 8), MapBytes.F32(d, p + 12), MapBytes.F32(d, p + 16),
                MapBytes.F32(d, p + 20), MapBytes.F32(d, p + 24), MapBytes.F32(d, p + 28), ext,
                BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 56)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 58)),
                BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 60)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 62))));
        }
        d = Block("MCIN");
        for (int i = 0; i < Math.Min(256, d.Length / MapBytes.McinEntry); i++)
        {
            int p = i * MapBytes.McinEntry;
            var e = new McinEntryRec(MapBytes.U32(d, p), MapBytes.U32(d, p + 4), MapBytes.U32(d, p + 8), MapBytes.U32(d, p + 12));
            Mcin.Add(e);
            Mcnk.Add(e.Offset + 8L <= b.Length ? new McnkRecord(b, i, e.Offset, e.Size) : null);
        }
    }

    private static uint[] U32s(byte[] d)
    {
        var a = new uint[d.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = MapBytes.U32(d, i * 4);
        return a;
    }

    public byte[] Block(string magic) => Blocks.TryGetValue(magic, out var blk) ? blk.Data : Array.Empty<byte>();

    /// <summary>The MMDX name of a doodad placement (resolved MDDF.nameId -> MMID -> MMDX), or null.</summary>
    public string? DoodadName(MddfEntry e)
    {
        if (e.NameId >= Mmid.Length) return null;
        uint ofs = Mmid[e.NameId];
        foreach (var kv in Mmdx) if (kv.Key == ofs) return kv.Value;
        return null;
    }

    public string? WmoName(ModfEntry e)
    {
        if (e.NameId >= Mwid.Length) return null;
        uint ofs = Mwid[e.NameId];
        foreach (var kv in Mwmo) if (kv.Key == ofs) return kv.Value;
        return null;
    }
}

/// <summary>A WDT, read positionally exactly like CMap_LoadWdt @00694760 (port of formats.Wdt).</summary>
public sealed class WdtFile
{
    public readonly record struct Slot(string Expected, long Position, string? Magic, long? Size);

    public string Name { get; }
    public byte[] Data { get; }
    public List<MapBytes.TopChunk> Top { get; }
    public List<Slot> Slots { get; } = new();
    public uint? Version { get; }
    public uint? MphdFlags { get; }
    public byte[] Mphd { get; }
    /// <summary>64x64 (flags, asyncId), index = y * 64 + x.</summary>
    public (uint Flags, uint AsyncId)[] Main { get; } = new (uint, uint)[64 * 64];
    public long AfterMain { get; }
    public string? GlobalWmoName { get; }
    public byte[]? GlobalModf { get; }
    public long EndOfRead { get; }

    public WdtFile(byte[] b, string name = "")
    {
        Name = name;
        Data = b;
        Top = MapBytes.Chunks(b);
        long pos = 0;
        (long, string?, long?)? Take(string expected)
        {
            if (pos + 8 > b.Length)
            {
                Slots.Add(new Slot(expected, pos, null, null));
                return null;
            }
            var m = MapBytes.MagicAt(b, pos);
            long s = MapBytes.U32(b, pos + 4);
            Slots.Add(new Slot(expected, pos, m, s));
            long hdr = pos;
            pos += 8;
            return (hdr, m, s);
        }
        Take("MVER");
        Version = pos + 4 <= b.Length ? MapBytes.U32(b, pos) : null;
        pos += 4;
        Take("MPHD");
        MphdFlags = pos + 4 <= b.Length ? MapBytes.U32(b, pos) : null;
        Mphd = MapBytes.Slice(b, pos, pos + MapBytes.WdtMphdSize);
        pos += MapBytes.WdtMphdSize;
        Take("MAIN");
        for (int i = 0; i < 64 * 64; i++)
        {
            long p = pos + i * 8L;
            Main[i] = p + 8 <= b.Length ? (MapBytes.U32(b, p), MapBytes.U32(b, p + 4)) : (0u, 0u);
        }
        pos += MapBytes.WdtMainSize;
        AfterMain = pos;
        if (MphdFlags is { } fl && (fl & 1) != 0)
        {
            var r = Take("MWMO");
            long nameSize = r is { } rr && rr.Item3 is { } sz ? sz : 0;
            var nameBytes = MapBytes.Slice(b, pos, pos + nameSize);
            int z = Array.IndexOf(nameBytes, (byte)0);
            GlobalWmoName = Encoding.Latin1.GetString(nameBytes, 0, z < 0 ? nameBytes.Length : z);
            pos += nameSize;
            Take("MODF");
            GlobalModf = MapBytes.Slice(b, pos, pos + MapBytes.ModfSize);
            pos += MapBytes.ModfSize;
        }
        EndOfRead = pos;
    }

    public bool IsGlobalWmo => MphdFlags is { } f && (f & 1) != 0;

    /// <summary>Tiles flagged in MAIN (flags &amp; 1), as (x, y).</summary>
    public List<(int X, int Y)> Tiles()
    {
        var l = new List<(int, int)>();
        for (int i = 0; i < Main.Length; i++)
            if ((Main[i].Flags & 1) != 0) l.Add((i % 64, i / 64));
        return l;
    }
}

/// <summary>A WDL, read positionally exactly like CMap_LoadWdl @006944a0 (port of formats.Wdl).</summary>
public sealed class WdlFile
{
    public string Name { get; }
    public byte[] Data { get; }
    public List<MapBytes.TopChunk> Top { get; }
    public string? MverMagic { get; }
    public uint? MverSize { get; }
    public uint? Version { get; }
    public string? TableMagic { get; }
    public uint? TableSize { get; }
    /// <summary>64x64 MARE offsets (index = y * 64 + x), empty when the table does not fit.</summary>
    public uint[] Offsets { get; }

    public WdlFile(byte[] b, string name = "")
    {
        Name = name;
        Data = b;
        Top = MapBytes.Chunks(b);
        MverMagic = MapBytes.MagicAt(b, 0);
        MverSize = b.Length >= 8 ? MapBytes.U32(b, 4) : null;
        Version = b.Length >= 12 ? MapBytes.U32(b, 8) : null;
        TableMagic = MapBytes.MagicAt(b, 12);
        TableSize = b.Length >= 20 ? MapBytes.U32(b, 16) : null;
        if (b.Length >= 20 + MapBytes.WdlMaofSize)
        {
            Offsets = new uint[4096];
            for (int i = 0; i < 4096; i++) Offsets[i] = MapBytes.U32(b, 20 + i * 4);
        }
        else Offsets = Array.Empty<uint>();
    }

    public List<(int X, int Y)> Tiles()
    {
        var l = new List<(int, int)>();
        for (int i = 0; i < Offsets.Length; i++)
            if (Offsets[i] != 0) l.Add((i % 64, i / 64));
        return l;
    }

    /// <summary>The 17x17 outer + 16x16 inner heights of a tile, or null.</summary>
    public short[]? Heights(int x, int y)
    {
        if (Offsets.Length == 0) return null;
        uint o = Offsets[y * 64 + x];
        if (o == 0 || o + 8 + MapBytes.WdlMareData > Data.Length) return null;
        var h = new short[545];
        for (int i = 0; i < 545; i++) h[i] = BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan((int)o + 8 + i * 2));
        return h;
    }
}
