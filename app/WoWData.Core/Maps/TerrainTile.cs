using System.Buffers.Binary;
using System.Numerics;
using WoWData.Core.Imaging;

namespace WoWData.Core.Maps;

public enum LiquidKind { River, Ocean, Magma, Slime }

/// <summary>One MCLQ layer of a chunk: 9x9 heights and the 8x8 tiles that have liquid ((flags &amp; 0xF) != 0xF, as
/// CMapChunk_CreateLiquids @006af760 renders it).</summary>
public sealed class LiquidLayer
{
    public LiquidKind Kind;
    public float Min, Max;
    public float[] Heights = new float[81];
    public bool[] Tiles = new bool[64];
}

/// <summary>One of the 256 terrain squares of a tile, in world coordinates.</summary>
public sealed class TerrainChunk
{
    public int Index, X, Y;
    public int AreaId;
    public uint Flags;
    /// <summary>The 16-bit hole mask: bit (row * 4 + col) removes the 2x2 quads at (col * 2, row * 2).</summary>
    public uint Holes;
    /// <summary>M4: the 8 x 8 hole mask of a modern tile (bit row * 8 + col removes one quad), or null (1.12: <see cref="Holes"/>
    /// only). When set, it decides <see cref="IsHole"/>.</summary>
    public ulong? HighResHoles;
    /// <summary>Corner of the chunk (its largest world x and y); rows go towards -x, columns towards -y.</summary>
    public Vector3 Position;
    /// <summary>145 heights (absolute: posZ + MCVT), order 9, 8, 9, ... (outer row, inner row, ...).</summary>
    public float[] Heights = new float[145];
    /// <summary>145 normals (world space, from MCNR).</summary>
    public Vector3[] Normals = new Vector3[145];
    /// <summary>The texture layers: the texture path of each (1.12: up to 4; M4, another version: up to 8).</summary>
    public List<string> Layers = new();
    /// <summary>Alpha map of layers 1.. (64x64, 0..255; 1.12 expanded from 4 bits), each drawn over the layers below it;
    /// index 0 is null (the base layer).</summary>
    public List<byte[]?> Alpha = new();
    /// <summary>64x64 shadow (true = shadowed), or null.</summary>
    public bool[]? Shadow;
    public List<LiquidLayer> Liquids = new();
    public int DoodadRefs, WmoRefs;

    /// <summary>World position of outer vertex (row i, column j), i, j in 0..8.</summary>
    public Vector3 Outer(int i, int j) => new(Position.X - i * TerrainMath.UnitSize, Position.Y - j * TerrainMath.UnitSize, Heights[i * 17 + j]);

    public float MinHeight => Heights.Min();
    public float MaxHeight => Heights.Max();

    public bool IsHole(int quadX, int quadY) =>
        HighResHoles is { } h ? (h & (1UL << (quadY * 8 + quadX))) != 0 : (Holes & (1u << ((quadY / 2) * 4 + quadX / 2))) != 0;

    /// <summary>The chunk has holes (either mask).</summary>
    public bool HasHoles => Holes != 0 || HighResHoles is > 0;
}

public sealed record DoodadPlacement(int Index, uint Uid, string Model, Vector3 Position, Vector3 Rotation, float Scale)
{
    public Matrix4x4 World => TerrainMath.PlacementMatrix(Position, Rotation, Scale);
    public Vector3 WorldPosition => TerrainMath.PlacementToWorld(Position);
}

/// <param name="Scale">M4: a modern placement's scale (MODF flag 0x4); 1 in 1.12.</param>
public sealed record WmoPlacement(int Index, uint Uid, string Model, Vector3 Position, Vector3 Rotation, int DoodadSet, Vector3 ExtentMin, Vector3 ExtentMax, float Scale = 1f)
{
    public Matrix4x4 World => TerrainMath.PlacementMatrix(Position, Rotation, Scale);
    public Vector3 WorldPosition => TerrainMath.PlacementToWorld(Position);
}

/// <summary>
/// An ADT tile as terrain: 256 chunks with absolute heights, normals (MCNR, verified to be world x, y, z on the stock
/// tiles), texture layers with their 4-bit alpha maps (expanded to 8 bits), shadows (MCSH), holes and liquids (MCLQ),
/// plus the doodad (MDDF) and WMO (MODF) placements with resolved names. Built from the same reader as the validator,
/// tolerant of broken files (what cannot be read stays empty).
/// </summary>
public sealed class TerrainTile
{
    public string Map { get; init; } = "";
    public int TileX { get; init; }
    public int TileY { get; init; }
    public TerrainChunk?[] Chunks { get; } = new TerrainChunk?[256];
    public List<string> Textures { get; } = new();
    public List<DoodadPlacement> Doodads { get; } = new();
    public List<WmoPlacement> Wmos { get; } = new();
    public int FileSize { get; init; }
    /// <summary>M4: what could not be read or is shown approximately (another version: a locked file of the tile...);
    /// empty for 1.12 tiles.</summary>
    public List<string> Notes { get; } = new();

    public IEnumerable<TerrainChunk> AllChunks => Chunks.Where(c => c != null)!;

    public (float Min, float Max) HeightRange
    {
        get
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var c in AllChunks)
                foreach (var h in c.Heights)
                    if (float.IsFinite(h))
                    {
                        lo = Math.Min(lo, h);
                        hi = Math.Max(hi, h);
                    }
            return lo > hi ? (0, 0) : (lo, hi);
        }
    }

    /// <summary>The world-space corner with the largest x and y (chunk 0's corner).</summary>
    public Vector2 Corner => new(TerrainMath.MapHalf - TileY * TerrainMath.TileSize, TerrainMath.MapHalf - TileX * TerrainMath.TileSize);

    public static TerrainTile Load(byte[] data, string map, int tileX, int tileY)
    {
        var adt = new AdtFile(data);
        var t = new TerrainTile { Map = map, TileX = tileX, TileY = tileY, FileSize = data.Length };
        t.Textures.AddRange(adt.Textures);
        for (int i = 0; i < adt.Mcnk.Count; i++)
        {
            var c = adt.Mcnk[i];
            if (c == null || c.Magic != "MCNK" || !c.HasHeader || c.End > data.Length) continue;
            int idx = c["indexX"] < 16 && c["indexY"] < 16 ? (int)(c["indexY"] * 16 + c["indexX"]) : i;
            t.Chunks[idx] = ReadChunk(data, adt, c, idx);
        }
        for (int i = 0; i < adt.Mddf.Count; i++)
        {
            var d = adt.Mddf[i];
            t.Doodads.Add(new DoodadPlacement(i, d.Uid, WoWData.Core.Mpq.ClientData.ResolveName(adt.DoodadName(d) ?? "?"), d.Position, d.Rotation, d.Scale / 1024f));
        }
        for (int i = 0; i < adt.Modf.Count; i++)
        {
            var w = adt.Modf[i];
            t.Wmos.Add(new WmoPlacement(i, w.Uid, WoWData.Core.Mpq.ClientData.ResolveName(adt.WmoName(w) ?? "?"), w.Position, w.Rotation, w.DoodadSet,
                new Vector3(w.Extents[0], w.Extents[1], w.Extents[2]), new Vector3(w.Extents[3], w.Extents[4], w.Extents[5])));
        }
        return t;
    }

    private static TerrainChunk ReadChunk(byte[] b, AdtFile adt, McnkRecord c, int idx)
    {
        var ch = new TerrainChunk
        {
            Index = idx, X = idx % 16, Y = idx / 16, AreaId = c.AreaId, Flags = c.Flags, Holes = c["holes"] & 0xFFFF,
            Position = new Vector3(c.Float("posX"), c.Float("posY"), c.Float("posZ")),
            DoodadRefs = (int)c["nDoodadRefs"], WmoRefs = (int)c["nMapObjRefs"],
        };
        long end = c.End;
        long p = c.DataOffset("ofsHeight");
        if (p + MapBytes.McvtSize <= end)
            for (int i = 0; i < 145; i++) ch.Heights[i] = ch.Position.Z + MapBytes.F32(b, p + i * 4);
        else Array.Fill(ch.Heights, ch.Position.Z);
        p = c.DataOffset("ofsNormal");
        for (int i = 0; i < 145; i++)
        {
            if (p + i * 3 + 3 <= end)
            {
                var n = new Vector3((sbyte)b[p + i * 3], (sbyte)b[p + i * 3 + 1], (sbyte)b[p + i * 3 + 2]);
                ch.Normals[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            }
            else ch.Normals[i] = Vector3.UnitZ;
        }
        long ly = c.DataOffset("ofsLayer");
        long mcal = c.DataOffset("ofsAlpha");
        int nl = (int)Math.Min(4, c["nLayers"]);
        for (int L = 0; L < nl; L++)
        {
            long q = ly + L * 16;
            if (q + 16 > end) break;
            uint tid = MapBytes.U32(b, q), lf = MapBytes.U32(b, q + 4), oa = MapBytes.U32(b, q + 8);
            ch.Layers.Add(tid < adt.Textures.Count ? adt.Textures[(int)tid] : "");
            if (L > 0 && (lf & 0x100) != 0 && mcal + oa + MapBytes.AlphaLayerSize <= end)
            {
                var a = new byte[64 * 64];
                for (int k = 0; k < MapBytes.AlphaLayerSize; k++)
                {
                    byte v = b[mcal + oa + k];
                    a[k * 2] = (byte)((v & 0xF) * 17);       // low nibble first
                    a[k * 2 + 1] = (byte)((v >> 4) * 17);
                }
                ch.Alpha.Add(a);
            }
            else ch.Alpha.Add(L == 0 ? null : new byte[64 * 64]);
        }
        if ((c.Flags & 1) != 0)
        {
            long s = c.DataOffset("ofsShadow");
            if (s + MapBytes.McshSize <= end)
            {
                ch.Shadow = new bool[64 * 64];
                for (int k = 0; k < 64 * 64; k++) ch.Shadow[k] = (b[s + k / 8] & (1 << (k % 8))) != 0;
            }
        }
        long lq = c.DataOffset("ofsLiquid");
        int layer = 0;
        for (int bit = 0; bit < 4; bit++)
        {
            if ((c.Flags & MapBytes.McnkLiquidFlags[bit]) == 0) continue;
            long q = lq + layer++ * MapBytes.MclqLayerSize;
            if (q + MapBytes.MclqLayerSize > end) break;
            var l = new LiquidLayer { Kind = (LiquidKind)bit, Min = MapBytes.F32(b, q), Max = MapBytes.F32(b, q + 4) };
            for (int k = 0; k < 81; k++) l.Heights[k] = MapBytes.F32(b, q + 8 + k * 8 + 4);
            for (int k = 0; k < 64; k++) l.Tiles[k] = (b[q + MapBytes.MclqTilesOfs + k] & 0xF) != 0xF;
            ch.Liquids.Add(l);
        }
        return ch;
    }

    /// <summary>The terrain height at a world position inside the tile (bilinear over the outer grid), or null.</summary>
    public float? HeightAt(float worldX, float worldY)
    {
        var corner = Corner;
        float fx = (corner.Y - worldY) / TerrainMath.ChunkSize, fy = (corner.X - worldX) / TerrainMath.ChunkSize;
        int cx = (int)MathF.Floor(fx), cy = (int)MathF.Floor(fy);
        if (cx < 0 || cy < 0 || cx > 15 || cy > 15) return null;
        var ch = Chunks[cy * 16 + cx];
        if (ch == null) return null;
        float u = (fx - cx) * 8, v = (fy - cy) * 8;
        int j = Math.Clamp((int)u, 0, 7), i = Math.Clamp((int)v, 0, 7);
        float du = u - j, dv = v - i;
        float h00 = ch.Heights[i * 17 + j], h01 = ch.Heights[i * 17 + j + 1], h10 = ch.Heights[(i + 1) * 17 + j], h11 = ch.Heights[(i + 1) * 17 + j + 1];
        return (h00 * (1 - du) + h01 * du) * (1 - dv) + (h10 * (1 - du) + h11 * du) * dv;
    }

    /// <summary>A height map of the tile: 129x129 samples of the outer grid (16 chunks x 8 + 1), shaded grey with a
    /// light from the north-west. Used for the overview and the difference overlay.</summary>
    public float[] OuterGrid()
    {
        var g = new float[129 * 129];
        Array.Fill(g, float.NaN);
        foreach (var c in AllChunks)
            for (int i = 0; i <= 8; i++)
                for (int j = 0; j <= 8; j++)
                    g[(c.Y * 8 + i) * 129 + c.X * 8 + j] = c.Heights[i * 17 + j];
        return g;
    }

    /// <summary>A shaded relief picture of the tile (north up, as the minimap).</summary>
    public RgbaImage Relief(int size = 256)
    {
        var g = OuterGrid();
        var img = new RgbaImage(size, size);
        var (lo, hi) = HeightRange;
        float range = Math.Max(1f, hi - lo);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x * 128f / (size - 1), fy = y * 128f / (size - 1);
                int ix = Math.Min(127, (int)fx), iy = Math.Min(127, (int)fy);
                float h = g[iy * 129 + ix], hx = g[iy * 129 + ix + 1], hy = g[(iy + 1) * 129 + ix];
                int o = img.Offset(x, y);
                if (float.IsNaN(h))
                {
                    img.Pixels[o + 3] = 0;
                    continue;
                }
                float shade = Math.Clamp(0.65f + ((float.IsNaN(hx) ? 0 : h - hx) + (float.IsNaN(hy) ? 0 : h - hy)) * 0.04f, 0.2f, 1.15f);
                float t = (h - lo) / range;
                var (r, gg, bb) = HeightColor(t);
                img.Pixels[o] = (byte)Math.Clamp(r * shade, 0, 255);
                img.Pixels[o + 1] = (byte)Math.Clamp(gg * shade, 0, 255);
                img.Pixels[o + 2] = (byte)Math.Clamp(bb * shade, 0, 255);
                img.Pixels[o + 3] = 255;
            }
        return img;
    }

    private static (float R, float G, float B) HeightColor(float t)
    {
        // low green -> brown -> light grey
        t = Math.Clamp(t, 0, 1);
        if (t < 0.5f) return (70 + t * 2 * 90, 110 + t * 2 * 40, 60 + t * 2 * 30);
        float u = (t - 0.5f) * 2;
        return (160 + u * 70, 150 + u * 75, 90 + u * 130);
    }

    /// <summary>The difference of two versions of a tile as a heat map over the newer relief: unchanged ground grey,
    /// raised ground red, lowered ground blue (stronger = bigger change). <paramref name="maxDelta"/> gets the largest
    /// change in yards; <paramref name="changedChunks"/> the chunks with any height change.</summary>
    public static RgbaImage HeightDifference(TerrainTile before, TerrainTile after, int size, out float maxDelta, out int changedChunks)
    {
        var ga = before.OuterGrid();
        var gb = after.OuterGrid();
        maxDelta = 0;
        changedChunks = 0;
        for (int c = 0; c < 256; c++)
        {
            var ca = before.Chunks[c];
            var cb = after.Chunks[c];
            if (ca == null || cb == null) continue;
            bool changed = false;
            for (int k = 0; k < 145; k++)
            {
                float d = Math.Abs(cb.Heights[k] - ca.Heights[k]);
                if (d > 1e-4f) changed = true;
                maxDelta = Math.Max(maxDelta, d);
            }
            if (changed) changedChunks++;
        }
        var relief = after.Relief(size);
        float scale = Math.Max(0.5f, maxDelta);
        var img = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int ix = (int)(x * 128f / (size - 1)), iy = (int)(y * 128f / (size - 1));
                float a = ga[iy * 129 + ix], b = gb[iy * 129 + ix];
                int o = img.Offset(x, y);
                byte grey = (byte)((relief.Pixels[o] + relief.Pixels[o + 1] + relief.Pixels[o + 2]) / 3 * 0.8f + 30);
                img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = grey;
                img.Pixels[o + 3] = 255;
                if (float.IsNaN(a) || float.IsNaN(b)) continue;
                float d = b - a;
                if (Math.Abs(d) < 1e-3f) continue;
                float s = Math.Clamp(Math.Abs(d) / scale, 0.25f, 1f);
                if (d > 0)
                {
                    img.Pixels[o] = (byte)(grey + (255 - grey) * s);
                    img.Pixels[o + 1] = (byte)(grey * (1 - s * 0.8f));
                    img.Pixels[o + 2] = (byte)(grey * (1 - s * 0.8f));
                }
                else
                {
                    img.Pixels[o] = (byte)(grey * (1 - s * 0.8f));
                    img.Pixels[o + 1] = (byte)(grey * (1 - s * 0.5f));
                    img.Pixels[o + 2] = (byte)(grey + (255 - grey) * s);
                }
            }
        return img;
    }
}
