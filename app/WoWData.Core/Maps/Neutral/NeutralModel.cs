using System.Numerics;

namespace WoWData.Core.Maps.Neutral;

/// <summary>
/// The version-neutral map model of the map converter (docs\research\MAP_CONVERSION_DESIGN.md: 1.60 is the hub, every
/// version gets a reader into this model and a writer out of it). It is a superset of what the versions can store:
/// readers fill everything their source has, writers keep what their target can show and list the rest in a
/// <see cref="ConversionReport"/>.
///
/// Conventions (the same in every version unless a field says otherwise):
/// * tile grid index = y * 64 + x; chunk index = indexY * 16 + indexX; liquid / hole / alpha cell index = row * 8 + col
///   (or row * 64 + col for 64 x 64 maps), rows going towards -x (south), columns towards -y (east), as the files store them.
/// * Fields named <c>...112</c> hold 1.12-only bytes that the 1.12 reader keeps so a stock tile writes back byte for byte
///   ("passthrough"). Other readers leave them null; the 1.12 writer then writes the value Blizzard's tools wrote in every
///   stock tile (zeros), and other writers ignore them.
/// </summary>
public enum MapSourceVersion
{
    /// <summary>Built in code (no file was read).</summary>
    None,
    V112,
    V243,
    V335,
    V160,
}

/// <summary>A file reference: a path, a FileDataID, or both. A modern tile names its files by FileDataID only; the 1.12
/// files only by path.</summary>
public sealed class AssetRef
{
    public string? Path { get; set; }
    public uint? FileDataId { get; set; }

    public AssetRef() { }

    public AssetRef(string? path, uint? fileDataId = null)
    {
        Path = path;
        FileDataId = fileDataId;
    }

    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>Same file: same FileDataID when both have one, else the same path (case-insensitive, either slash).</summary>
    public bool SameAs(AssetRef? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (FileDataId is { } a && other.FileDataId is { } b) return a == b;
        return HasPath && other.HasPath && string.Equals(Norm(Path!), Norm(other.Path!), StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string p) => p.Replace('/', '\\');

    public override string ToString() => Path ?? (FileDataId is { } f ? $"FDID {f}" : "(none)");
}

/// <summary>A top-level chunk a reader met but does not model (kept opaque; magic forward, data without the 8-byte header).</summary>
public sealed record RawChunk(string Magic, byte[] Data);

// ====================================================================== map level (WDT, WDL, minimaps)

/// <summary>A map: what its WDT says (map-wide flags, which tiles exist, the global WMO of a WMO-only map) plus the
/// minimap references and, when read, its WDL (far terrain).</summary>
public sealed class NeutralMap
{
    public string Name { get; set; } = "";
    public MapSourceVersion Source { get; set; }
    /// <summary>MVER of the WDT (18 in every version measured).</summary>
    public uint Version { get; set; } = 18;
    /// <summary>MPHD flags as the source stores them: 0x1 WMO-only (the only bit 1.12 reads), 0x2 MCCV, 0x4 big alpha,
    /// 0x8 sorted doodad refs, 0x80 height texturing, 0x200 MAID present (FORMAT KNOWLEDGE, see MAP_SURVEY_160 4.1).</summary>
    public uint Flags { get; set; }
    /// <summary>The 7 MPHD dwords after the flags (zero in 1.12; modern WDTs hold the FileDataIDs of the companion files
    /// there).</summary>
    public uint[] MphdExtra { get; set; } = new uint[7];
    /// <summary>64 x 64 tile entries, index y * 64 + x (always 4096 entries).</summary>
    public NeutralTileEntry[] Tiles { get; } = Enumerable.Range(0, 4096).Select(_ => new NeutralTileEntry()).ToArray();
    /// <summary>The single WMO of a WMO-only map (MPHD 0x1), or null.</summary>
    public NeutralWmo? GlobalWmo { get; set; }
    /// <summary>The far terrain (WDL), when read.</summary>
    public NeutralWdl? Wdl { get; set; }
    /// <summary>WDT chunks the reader did not model.</summary>
    public List<RawChunk> ExtraChunks { get; } = new();

    public bool IsWmoOnly => (Flags & 1) != 0;
    public NeutralTileEntry Tile(int x, int y) => Tiles[y * 64 + x];
    public IEnumerable<(int X, int Y)> ExistingTiles() =>
        Enumerable.Range(0, 4096).Where(i => Tiles[i].HasAdt).Select(i => (i % 64, i / 64));
}

/// <summary>One cell of the 64 x 64 tile grid.</summary>
public sealed class NeutralTileEntry
{
    /// <summary>MAIN flag 0x1: the tile has an ADT.</summary>
    public bool HasAdt { get; set; }
    /// <summary>The MAIN flags as stored (0x1 = has ADT; modern 0x2 = all water). The writers keep 0x1 in step with
    /// <see cref="HasAdt"/>.</summary>
    public uint Flags { get; set; }
    /// <summary>MAIN's second dword (the client's async load id; 0 on disk in every 1.12 WDT).</summary>
    public uint AsyncId { get; set; }
    /// <summary>The modern MAID FileDataIDs (root, obj0, obj1, tex0, lod, map texture, map normal texture, minimap), or
    /// null when the source has no MAID.</summary>
    public uint[]? Maid { get; set; }
    /// <summary>The tile's minimap texture (1.12: the md5translate.trs file; modern: the MAID minimap FileDataID).</summary>
    public AssetRef? Minimap { get; set; }
}

/// <summary>A WDL: the far terrain (17 x 17 + 16 x 16 heights per tile) and, from TBC on, the far hole masks.</summary>
public sealed class NeutralWdl
{
    public MapSourceVersion Source { get; set; }
    public uint Version { get; set; } = 18;
    /// <summary>Tile index (y * 64 + x) -> 545 heights (17 x 17 outer, then 16 x 16 inner).</summary>
    public SortedDictionary<int, short[]> Heights { get; } = new();
    /// <summary>Tile index -> 16 u16 hole rows (MAHO, TBC+). 1.12 has none.</summary>
    public SortedDictionary<int, ushort[]> Holes { get; } = new();
    /// <summary>WDL chunks the reader did not model (for example WotLK's low-detail WMO lists).</summary>
    public List<RawChunk> ExtraChunks { get; } = new();
}

// ====================================================================== tile level (ADT)

/// <summary>One map tile (an ADT, or a modern root + obj + tex set): 16 x 16 chunks, the asset lists, the placements,
/// flight bounds, texture parameters and LOD data.</summary>
public sealed class NeutralTile
{
    public string Map { get; set; } = "";
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public MapSourceVersion Source { get; set; }
    /// <summary>MVER of the (root) file.</summary>
    public uint Version { get; set; } = 18;
    /// <summary>MHDR flags (0 in 1.12; 0x1 = MFBO present, 0x2 = Northrend in WotLK).</summary>
    public uint HeaderFlags { get; set; }

    /// <summary>Ground textures in their file order (MTEX / modern MDID). Layers point at these objects.</summary>
    public List<AssetRef> Textures { get; } = new();
    /// <summary>Doodad models in their file order (MMDX via MMID). Placements point at these objects.</summary>
    public List<AssetRef> DoodadModels { get; } = new();
    /// <summary>WMOs in their file order (MWMO via MWID).</summary>
    public List<AssetRef> WmoModels { get; } = new();

    public List<NeutralDoodad> Doodads { get; } = new();
    public List<NeutralWmo> Wmos { get; } = new();

    /// <summary>256 chunks, index indexY * 16 + indexX (null when the source had none there).</summary>
    public NeutralChunk?[] Chunks { get; } = new NeutralChunk?[256];

    /// <summary>MFBO: flight bounds, 9 maximum then 9 minimum heights (TBC+), or null.</summary>
    public short[]? FlightBounds { get; set; }
    /// <summary>Per-texture parameters (MTXF flags, MTXP height scale/offset), aligned with <see cref="Textures"/>; empty
    /// when the source has none.</summary>
    public List<TextureParams> TextureParams { get; } = new();
    /// <summary>The tile's LOD file (modern _lod.adt), kept opaque.</summary>
    public byte[]? LodBlob { get; set; }
    /// <summary>Top-level chunks the reader did not model.</summary>
    public List<RawChunk> ExtraChunks { get; } = new();

    public IEnumerable<NeutralChunk> AllChunks => Chunks.Where(c => c != null)!;

    /// <summary>A tile built in code: 256 flat chunks at <paramref name="height"/>, positions as the client computes them,
    /// normals straight up, no textures, liquids or placements.</summary>
    public static NeutralTile CreateFlat(string map, int x, int y, float height = 0f)
    {
        var t = new NeutralTile { Map = map, X = x, Y = y };
        for (int i = 0; i < 256; i++) t.Chunks[i] = NeutralChunk.CreateFlat(x, y, i % 16, i / 16, height);
        return t;
    }
}

/// <summary>Texture parameters (MTXF flags; MTXP adds height scale and offset).</summary>
public sealed class TextureParams
{
    public uint Flags { get; set; }
    public float HeightScale { get; set; }
    public float HeightOffset { get; set; }
}

/// <summary>One of a tile's 256 chunks (MCNK).</summary>
public sealed class NeutralChunk
{
    public int IndexX { get; set; }
    public int IndexY { get; set; }
    /// <summary>MCNK flags as the source stores them. 1.12 bits: 0x1 shadow map, 0x2 impassable, 0x4/0x8/0x10/0x20 liquid
    /// layers (the 1.12 writer derives these from <see cref="Liquids"/>), 0x8000 no alpha edge fix. Modern bits: 0x40 MCCV,
    /// 0x10000 high-res holes.</summary>
    public uint Flags { get; set; }
    public uint AreaId { get; set; }
    /// <summary>MCNK position: the chunk corner (largest world x and y) and the base height the MCVT heights are relative to.</summary>
    public Vector3 Position { get; set; }
    /// <summary>145 heights relative to <see cref="Position"/>.Z, rows 9, 8, 9, ... (outer, inner, ...).</summary>
    public float[] Heights { get; set; } = new float[145];
    /// <summary>145 normals as signed bytes x, y, z (world space).</summary>
    public sbyte[] Normals { get; set; } = new sbyte[145 * 3];
    /// <summary>The 13 bytes Blizzard's tools wrote after MCNR in 1.12 (uninitialised memory, non-zero in every stock chunk).</summary>
    public byte[]? NormalPad112 { get; set; }
    /// <summary>Holes, high resolution: 8 x 8 bits, byte = row, bit = column (a 1.12 4 x 4 hole is the 2 x 2 block of
    /// fine holes under it).</summary>
    public ulong Holes { get; set; }
    /// <summary>MCNK 0x40: the 8 x 8 x 2-bit low-quality texture map (16 bytes).</summary>
    public byte[] LowQualityTextureMap { get; set; } = new byte[16];
    /// <summary>MCNK 0x50: the 8 x 8 "no ground-effect doodad" bits.</summary>
    public ulong NoEffectDoodad { get; set; }
    /// <summary>Texture layers (1.12: up to 4; 1.60: up to 8). Layer 0 is the base and has no alpha.</summary>
    public List<NeutralLayer> Layers { get; } = new();
    /// <summary>Baked shadow, 64 x 64 (true = shadowed; MCSH bit order: row-major, lowest bit first), or null when the chunk
    /// has none. Every stock 1.12 chunk stores an MCSH, also the 139,223 without flag 0x1; flag 0x1 in <see cref="Flags"/>
    /// says whether the client uses it.</summary>
    public bool[]? Shadow { get; set; }
    /// <summary>MCCV vertex colours (145 x BGRA), or null.</summary>
    public byte[]? VertexColors { get; set; }
    /// <summary>MCLV vertex lighting (145 x BGRA), or null.</summary>
    public byte[]? VertexLighting { get; set; }
    /// <summary>Liquid instances (MH2O instances, or one per 1.12 MCLQ layer).</summary>
    public List<NeutralLiquid> Liquids { get; } = new();
    /// <summary>Doodads referenced by this chunk (indices into <see cref="NeutralTile.Doodads"/>), in file order.</summary>
    public List<int> DoodadRefs { get; } = new();
    /// <summary>WMOs referenced by this chunk (indices into <see cref="NeutralTile.Wmos"/>), in file order.</summary>
    public List<int> WmoRefs { get; } = new();
    public List<NeutralSoundEmitter> SoundEmitters { get; } = new();

    /// <summary>The corner of chunk (cx, cy) of tile (tx, ty) as the 1.12 client computes it.</summary>
    public static Vector3 CornerOf(int tileX, int tileY, int cx, int cy, float z = 0f) =>
        new(TerrainMath.MapHalf - tileY * TerrainMath.TileSize - cy * TerrainMath.ChunkSize,
            TerrainMath.MapHalf - tileX * TerrainMath.TileSize - cx * TerrainMath.ChunkSize, z);

    public static NeutralChunk CreateFlat(int tileX, int tileY, int cx, int cy, float height)
    {
        var c = new NeutralChunk { IndexX = cx, IndexY = cy, Position = CornerOf(tileX, tileY, cx, cy, height) };
        for (int k = 0; k < 145; k++) c.Normals[k * 3 + 2] = 127;
        return c;
    }

    /// <summary>The 1.12 4 x 4 hole mask expanded to 8 x 8 (each 1.12 hole = the 2 x 2 fine holes under it).</summary>
    public static ulong ExpandLowResHoles(uint lowRes)
    {
        ulong h = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if ((lowRes & (1u << (r * 4 + c))) != 0)
                    for (int dr = 0; dr < 2; dr++)
                        for (int dc = 0; dc < 2; dc++)
                            h |= 1UL << ((2 * r + dr) * 8 + 2 * c + dc);
        return h;
    }

    /// <summary>The 1.12 4 x 4 mask of an 8 x 8 mask: a 1.12 hole only where all four fine holes are holes (conservative:
    /// no new gaps in the ground).</summary>
    public static uint ReduceHighResHoles(ulong highRes)
    {
        uint l = 0;
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                bool all = true;
                for (int dr = 0; dr < 2 && all; dr++)
                    for (int dc = 0; dc < 2 && all; dc++)
                        all = (highRes & (1UL << ((2 * r + dr) * 8 + 2 * c + dc))) != 0;
                if (all) l |= 1u << (r * 4 + c);
            }
        return l;
    }
}

/// <summary>One texture layer of a chunk (MCLY + its alpha map).</summary>
public sealed class NeutralLayer
{
    /// <summary>The ground texture (one of <see cref="NeutralTile.Textures"/>; writers add it to the list when it is not there).</summary>
    public AssetRef Texture { get; set; } = new();
    /// <summary>MCLY flags as stored: 0x1-0x80 texture animation, 0x100 has alpha map, 0x200 compressed alpha (storage
    /// only), 0x400 cube-map reflection (WotLK+).</summary>
    public uint Flags { get; set; }
    /// <summary>Ground effect (GroundEffectTexture ID).</summary>
    public uint EffectId { get; set; }
    /// <summary>64 x 64 8-bit alpha (row-major), or null (base layer / no alpha map). 1.12's 4-bit values n are held as
    /// Blizzard's own 4-to-8-bit rule: 0 -> 0, n -> 16n + 15 (MAP_SURVEY_160 3.4), so <c>v &gt;&gt; 4</c> gives n back.</summary>
    public byte[]? Alpha { get; set; }

    public static byte Expand4(int n) => n == 0 ? (byte)0 : (byte)(16 * n + 15);
}

/// <summary>Where a liquid instance came from: an MCLQ layer (1.12, 2.4.3, 3.3.5 Outland) or an MH2O instance.</summary>
public enum LiquidOrigin { Mclq, Mh2o }

/// <summary>
/// A liquid instance: a superset of an MH2O instance (type, object, min/max, rectangle, exists bits, per-vertex heights
/// / depth / texture coordinates, fishable and deep tiles) that also holds an MCLQ layer exactly (full 8 x 8 rectangle,
/// 9 x 9 vertices, the basic 1.12 kind, the flow fields).
/// </summary>
public sealed class NeutralLiquid
{
    public LiquidOrigin Origin { get; set; } = LiquidOrigin.Mh2o;
    /// <summary>The 1.12 kind when the source states it (an MCLQ layer); null for MH2O instances (the type ID decides).</summary>
    public LiquidKind? Kind { get; set; }
    /// <summary>LiquidType ID (MH2O), or null (MCLQ has none: the kind is the MCNK flag bit).</summary>
    public int? LiquidType { get; set; }
    /// <summary>MH2O's second u16: the vertex format in 3.3.5a, a LiquidObject ID in modern clients; null for MCLQ.</summary>
    public int? LiquidObjectOrVertexFormat { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }
    /// <summary>The rectangle of tiles the vertex arrays cover (MCLQ: always 0, 0, 8, 8).</summary>
    public int RectX { get; set; }
    public int RectY { get; set; }
    public int RectW { get; set; } = 8;
    public int RectH { get; set; } = 8;
    /// <summary>Tiles with liquid, bit row * 8 + col of the full 8 x 8 (not relative to the rectangle).</summary>
    public ulong Exists { get; set; }
    /// <summary>Fishable tiles (MCLQ tile bit 0x40; MH2O attribute mask).</summary>
    public ulong Fishable { get; set; }
    /// <summary>Deep / fatigue tiles (MCLQ tile bit 0x80; MH2O attribute mask).</summary>
    public ulong Deep { get; set; }
    /// <summary>(RectW + 1) x (RectH + 1) vertex heights, or null (flat at <see cref="Min"/>).</summary>
    public float[]? Heights { get; set; }
    /// <summary>Vertex depth (MCLQ water vertices, MH2O LVF 0/2/3), or null.</summary>
    public byte[]? Depth { get; set; }
    /// <summary>Vertex texture coordinates (MCLQ magma/slime vertices, MH2O LVF 1/3), or null.</summary>
    public ushort[]? TexU { get; set; }
    public ushort[]? TexV { get; set; }
    /// <summary>MCLQ water vertices' flow0, flow1 and filler bytes (81 x 3), or null when all are zero (every stock chunk).</summary>
    public byte[]? MclqFlow112 { get; set; }
    /// <summary>MCLQ tile bytes that do not follow the rule (exists ? kind nibble : 0xF) | fishable 0x40 | deep 0x80: the
    /// XOR of the stored byte with the rule's byte (64 bytes), or null when every tile follows it (every stock layer).</summary>
    public byte[]? MclqTileResidue112 { get; set; }
    /// <summary>MCLQ's flow vector count + 2 x 40-byte flow vectors (84 bytes; the count is 0 in every stock layer, the
    /// vectors are not always zero), or null.</summary>
    public byte[]? MclqFlowVectors112 { get; set; }

    public int TileCount => System.Numerics.BitOperations.PopCount(Exists);
}

/// <summary>A sound emitter (1.12 MCSE: 52-byte records; WotLK: 28 bytes id + position + size).</summary>
public sealed class NeutralSoundEmitter
{
    public uint Id { get; set; }
    public Vector3 Position { get; set; }
    /// <summary>WotLK's emitter size, or zero.</summary>
    public Vector3 Size { get; set; }
    /// <summary>The whole 52-byte 1.12 record (the fields after the id and position are kept as they are), or null.</summary>
    public byte[]? Raw112 { get; set; }
}

/// <summary>A placed doodad (MDDF).</summary>
public sealed class NeutralDoodad
{
    /// <summary>The model (one of <see cref="NeutralTile.DoodadModels"/>; writers add it when it is not there).</summary>
    public AssetRef Model { get; set; } = new();
    public uint UniqueId { get; set; }
    /// <summary>Placement coordinates (a, height, b) as the files store them.</summary>
    public Vector3 Position { get; set; }
    /// <summary>Rotation in degrees as stored.</summary>
    public Vector3 Rotation { get; set; }
    /// <summary>Scale as stored (1024 = 1.0).</summary>
    public ushort ScaleRaw { get; set; } = 1024;
    /// <summary>MDDF flags as stored (0 in every 1.12 placement; modern 0x40 = name is a FileDataID).</summary>
    public ushort Flags { get; set; }

    public float Scale => ScaleRaw / 1024f;
}

/// <summary>A placed WMO (MODF).</summary>
public sealed class NeutralWmo
{
    public AssetRef Model { get; set; } = new();
    public uint UniqueId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 ExtentMin { get; set; }
    public Vector3 ExtentMax { get; set; }
    /// <summary>MODF flags as stored (0 in 1.12; modern 0x4 = has scale, 0x8 = name is a FileDataID).</summary>
    public ushort Flags { get; set; }
    public ushort DoodadSet { get; set; }
    public ushort NameSet { get; set; }
    /// <summary>MODF's last u16: padding in 1.12 (0 in every stock placement), the scale (1024 = 1.0) when flag 0x4 is set
    /// in modern clients.</summary>
    public ushort ScaleRaw { get; set; }
}
