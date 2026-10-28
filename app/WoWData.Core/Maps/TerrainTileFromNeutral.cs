using System.Numerics;
using WoWData.Core.Maps.Neutral;

namespace WoWData.Core.Maps;

/// <summary>
/// M4 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 3.5): a tile of the neutral map model (another version's tile, read
/// by <see cref="Adt160Reader"/>) as the <see cref="TerrainTile"/> the 3D view draws, without going through a 1.12 file
/// (which would cut it down to 4 layers and 4-bit alpha):
/// <list type="bullet">
/// <item>heights (absolute) and normals as the 1.12 reader makes them;</item>
/// <item>up to 8 texture layers with their 8-bit alpha maps; a map whose layers store blend weights (the 8-bit 1.60
///   maps, <see cref="AlphaBlendMode.Weights"/>) is turned into the sequential alphas the renderer blends with
///   (<see cref="AlphaBlending"/>: the same visible share per texel);</item>
/// <item>the 8 x 8 holes (<see cref="TerrainChunk.HighResHoles"/>), the baked shadow where the chunk uses one;</item>
/// <item>the MH2O liquids, each instance a liquid layer of its kind (the LiquidType's kind; river water when unknown),
///   its vertex heights over its rectangle (flat at its minimum elsewhere and when it has none);</item>
/// <item>the doodad and building placements named by FileDataID (the session's name, else "#id.m2" / "#id.wmo"), with
///   a building's scale (MODF flag 0x4).</item>
/// </list>
/// </summary>
public static class TerrainTileFromNeutral
{
    /// <summary>The most layers a chunk keeps (1.60 stores up to 8; MEASURED max 7 on the continents).</summary>
    public const int MaxLayers = 8;

    /// <param name="liquidKind">LiquidType ID -> 1.12 kind (null = unknown: river water)</param>
    /// <param name="nameOf">FileDataID and extension -> the name the session reads the file by</param>
    /// <param name="fileSize">the bytes read for the tile (its files together)</param>
    public static TerrainTile Convert(NeutralTile n, Func<int, LiquidKind?> liquidKind, Func<uint, string, string> nameOf, int fileSize)
    {
        string Name(AssetRef r, string ext) => r.FileDataId is { } f && f != 0 ? nameOf(f, ext) : r.Path ?? "?";
        var t = new TerrainTile { Map = n.Map, TileX = n.X, TileY = n.Y, FileSize = fileSize };
        var texNames = new Dictionary<AssetRef, string>(ReferenceEqualityComparer.Instance);
        string TexName(AssetRef r)
        {
            if (!texNames.TryGetValue(r, out var s)) texNames[r] = s = Name(r, ".blp");
            return s;
        }
        foreach (var r in n.Textures) t.Textures.Add(TexName(r));
        for (int i = 0; i < 256; i++)
        {
            var c = n.Chunks[i];
            if (c != null) t.Chunks[i] = Chunk(c, i, n.AlphaBlend, TexName, liquidKind, t);
        }
        foreach (var l in t.AllChunks.SelectMany(ch => ch.Layers))
            if (l.Length > 0 && !t.Textures.Contains(l, StringComparer.OrdinalIgnoreCase)) t.Textures.Add(l);
        for (int i = 0; i < n.Doodads.Count; i++)
        {
            var d = n.Doodads[i];
            t.Doodads.Add(new DoodadPlacement(i, d.UniqueId, Name(d.Model, ".m2"), d.Position, d.Rotation, d.Scale));
        }
        for (int i = 0; i < n.Wmos.Count; i++)
        {
            var w = n.Wmos[i];
            float scale = (w.Flags & 0x4) != 0 && w.ScaleRaw != 0 ? w.ScaleRaw / 1024f : 1f;
            t.Wmos.Add(new WmoPlacement(i, w.UniqueId, Name(w.Model, ".wmo"), w.Position, w.Rotation, w.DoodadSet, w.ExtentMin, w.ExtentMax, scale));
        }
        return t;
    }

    private static TerrainChunk Chunk(NeutralChunk c, int idx, AlphaBlendMode mode, Func<AssetRef, string> texName, Func<int, LiquidKind?> liquidKind, TerrainTile t)
    {
        var ch = new TerrainChunk
        {
            Index = idx, X = idx % 16, Y = idx / 16, AreaId = (int)c.AreaId, Flags = c.Flags,
            Holes = NeutralChunk.ReduceHighResHoles(c.Holes), HighResHoles = c.Holes,
            Position = c.Position, DoodadRefs = c.DoodadRefs.Count, WmoRefs = c.WmoRefs.Count,
        };
        for (int k = 0; k < 145; k++) ch.Heights[k] = c.Position.Z + c.Heights[k];
        for (int k = 0; k < 145; k++)
        {
            var v = new Vector3(c.Normals[k * 3], c.Normals[k * 3 + 1], c.Normals[k * 3 + 2]);
            ch.Normals[k] = v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitZ;
        }
        // layers: names, and the alphas as the renderer blends them (each over the layers below it)
        var layers = c.Layers.Take(MaxLayers).ToList();
        if (c.Layers.Count > MaxLayers) t.Notes.Add($"Chunk {idx} has {c.Layers.Count} texture layers: the first {MaxLayers} are shown.");
        foreach (var l in layers) ch.Layers.Add(texName(l.Texture));
        if (layers.Count > 0)
        {
            if (mode == AlphaBlendMode.Weights && layers.Count > 1)
            {
                var seq = AlphaBlending.SequentialFromWeights(AlphaBlending.VisibleWeights(layers, AlphaBlendMode.Weights));
                ch.Alpha.Add(null);
                for (int L = 1; L < layers.Count; L++)
                {
                    var a = new byte[4096];
                    for (int k = 0; k < 4096; k++) a[k] = (byte)Math.Clamp((int)MathF.Round(seq[L][k]), 0, 255);
                    ch.Alpha.Add(a);
                }
            }
            else
                for (int L = 0; L < layers.Count; L++)
                    ch.Alpha.Add(L == 0 ? null : layers[L].Alpha is { Length: 4096 } a ? (byte[])a.Clone() : new byte[4096]);
        }
        if ((c.Flags & 1) != 0 && c.Shadow is { Length: 4096 } sh) ch.Shadow = (bool[])sh.Clone();
        foreach (var l in c.Liquids) ch.Liquids.Add(Liquid(l, liquidKind));
        return ch;
    }

    /// <summary>One liquid instance as a 9 x 9 liquid layer: the tiles of its exists mask, its vertex heights where its
    /// rectangle has them, its minimum elsewhere.</summary>
    public static LiquidLayer Liquid(NeutralLiquid l, Func<int, LiquidKind?> liquidKind)
    {
        var o = new LiquidLayer
        {
            Kind = l.Kind ?? (l.LiquidType is { } lt ? liquidKind(lt) : null) ?? LiquidKind.River,
            Min = l.Min, Max = l.Max,
        };
        Array.Fill(o.Heights, l.Min);
        if (l.Heights is { } h && h.Length >= (l.RectW + 1) * (l.RectH + 1))
            for (int r = 0; r <= l.RectH; r++)
                for (int col = 0; col <= l.RectW; col++)
                {
                    int yy = l.RectY + r, xx = l.RectX + col;
                    if (yy is >= 0 and <= 8 && xx is >= 0 and <= 8) o.Heights[yy * 9 + xx] = h[r * (l.RectW + 1) + col];
                }
        for (int k = 0; k < 64; k++) o.Tiles[k] = (l.Exists & (1UL << k)) != 0;
        return o;
    }
}
