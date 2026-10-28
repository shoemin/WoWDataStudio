using System.Numerics;
using System.Runtime.InteropServices;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;

namespace WoWDataStudio.Rendering;

/// <summary>Terrain vertex (32 bytes): position relative to the asset's origin, MCNR normal, position inside the chunk (0..1).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TerrainVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 ChunkUv;
    public const int Size = 32;
}

/// <summary>Static mesh vertex (32 bytes): position, normal, texture coordinate (WMO groups).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StaticVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public const int Size = 32;
}

/// <summary>Liquid vertex (28 bytes): position and colour (rgba).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ColorVertex
{
    public Vector3 Position;
    public Vector4 Color;
    public const int Size = 28;
}

public sealed class TerrainDrawChunk
{
    public int Tile;
    public int ChunkX, ChunkY;
    public int IndexStart, IndexCount;
    /// <summary>Index into <see cref="TerrainAsset.Textures"/> per layer (1.12: up to 4; M4, another version: up to 8), -1 = none.</summary>
    public int[] Layers = { -1, -1, -1, -1, -1, -1, -1, -1 };
    public int LayerCount;
}

public sealed class WmoMesh
{
    public string Path = "";
    public StaticVertex[] Vertices = Array.Empty<StaticVertex>();
    public uint[] Indices = Array.Empty<uint>();
    /// <summary>(first index, count, texture index into <see cref="TerrainAsset.Textures"/> or -1, alpha key, blended).</summary>
    public List<(int Start, int Count, int Texture, bool AlphaKey, bool Blend)> Batches = new();
}

/// <summary>Options of the terrain preview.</summary>
/// <param name="SpecularGround">0.6.1: draw the ground with the "_s" textures, as the game does with terrain specular on
/// (CMapArea_LoadTextureNames @006c20e0); <paramref name="Exists"/> finds them (a missing "_s" file falls back to the base).</param>
/// <param name="LoadModel">M4: reads a doodad's model (another version: the modern reader by
/// <see cref="WoWData.Core.M2.IModelSource"/>); null = the file's bytes through <c>read</c> and the 1.12 reader.</param>
/// <param name="NameOf">M4: names a FileDataID of a modern building (groups, textures), see <see cref="WmoGeometry.Load"/>.</param>
public sealed record TerrainBuildOptions(bool Doodads = true, bool Wmos = true, int MaxDoodadModels = 400, bool SpecularGround = false, Func<string, bool>? Exists = null,
                                         Func<string, WoWData.Core.M2.M2Model>? LoadModel = null, Func<uint, string, string>? NameOf = null);

/// <summary>
/// Everything the terrain preview draws, built on a background thread from one tile (and optionally its neighbours):
/// the terrain mesh per chunk (holes left out, two-sided), the ground textures and one alpha atlas per tile (64 x 64 per
/// chunk: red, green, blue = the 4-bit alpha maps of layers 1-3, alpha = the shadow map; M4: a second atlas with layers
/// 4-7 for another version's tiles with more than 4 layers, none for 1.12), the liquids as coloured
/// quads, the doodads (M2 models through the model viewer's <see cref="ModelAsset"/>, drawn in their rest pose) and the
/// WMO buildings (their groups with the first texture of each batch). Positions are relative to <see cref="Origin"/>
/// (the corner of the first tile), which keeps 32-bit floats precise.
/// </summary>
public sealed class TerrainAsset
{
    public List<TerrainTile> Tiles { get; } = new();
    public Vector3 Origin { get; private set; }
    public TerrainVertex[] Vertices { get; private set; } = Array.Empty<TerrainVertex>();
    public uint[] Indices { get; private set; } = Array.Empty<uint>();
    public List<TerrainDrawChunk> Chunks { get; } = new();
    public List<string> TexturePaths { get; } = new();
    /// <summary>The file each ground texture was loaded from (the "_s" variant with <see cref="TerrainBuildOptions.SpecularGround"/>).</summary>
    public List<string> DrawnTexturePaths { get; } = new();
    public bool SpecularGround { get; private set; }
    /// <summary>Ground textures without the "_s" file the game would draw (shown with the base file).</summary>
    public int MissingSpecular { get; private set; }
    public List<RgbaImage?> Textures { get; } = new();
    /// <summary>One 1024 x 1024 alpha / shadow atlas per tile.</summary>
    public List<RgbaImage> AlphaAtlases { get; } = new();
    /// <summary>M4: per tile, the alpha maps of layers 4-7 (red, green, blue, alpha) when one of its chunks has more than
    /// 4 layers (another version), else null (always null for 1.12 tiles).</summary>
    public List<RgbaImage?> AlphaAtlases2 { get; } = new();
    public ColorVertex[] Liquid { get; private set; } = Array.Empty<ColorVertex>();
    public List<(ModelAsset Asset, Matrix4x4 World)> Doodads { get; } = new();
    public List<(WmoMesh Mesh, Matrix4x4 World)> Wmos { get; } = new();
    /// <summary>The unique id of each entry of <see cref="Doodads"/> / <see cref="Wmos"/> (the breakdown highlights by it).</summary>
    public List<uint> DoodadUids { get; } = new();
    public List<uint> WmoUids { get; } = new();
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public int MissingModels { get; private set; }
    public int DoodadModels { get; private set; }
    /// <summary>The ground textures (the first entries of <see cref="TexturePaths"/>; WMO textures follow).</summary>
    public int GroundTextures { get; private set; }
    public TimeSpan BuildTime { get; private set; }
    public string Summary => $"{Tiles.Count} tile{(Tiles.Count == 1 ? "" : "s")}, {Chunks.Count} chunks, {GroundTextures} ground textures{(SpecularGround ? " (_s files, as with terrain specular on" + (MissingSpecular > 0 ? $"; {MissingSpecular} without one" : "") + ")" : "")}, {Doodads.Count} doodads ({DoodadModels} models), {Wmos.Count} buildings" +
                             (MissingModels > 0 ? $", {MissingModels} models not found" : "") + $", built in {BuildTime.TotalMilliseconds:0} ms";

    private static readonly Dictionary<LiquidKind, Vector4> LiquidColors = new()
    {
        [LiquidKind.River] = new(0.16f, 0.38f, 0.62f, 0.55f),
        [LiquidKind.Ocean] = new(0.10f, 0.26f, 0.52f, 0.62f),
        [LiquidKind.Magma] = new(0.95f, 0.38f, 0.06f, 0.85f),
        [LiquidKind.Slime] = new(0.32f, 0.62f, 0.12f, 0.72f),
    };

    /// <param name="read">game path -> bytes (your workspace first)</param>
    /// <param name="loadTexture">BLP path -> decoded image (cached by the caller)</param>
    public static TerrainAsset Build(IReadOnlyList<TerrainTile> tiles, Func<string, byte[]?> read, Func<string, RgbaImage?> loadTexture, TerrainBuildOptions opts, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = new TerrainAsset();
        a.Tiles.AddRange(tiles);
        var first = tiles[0];
        var (lo0, _) = first.HeightRange;
        a.Origin = new Vector3(first.Corner.X, first.Corner.Y, lo0);
        var verts = new List<TerrainVertex>();
        var idx = new List<uint>();
        var texIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var liquid = new List<ColorVertex>();
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int ti = 0; ti < tiles.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var tile = tiles[ti];
            var atlas = new RgbaImage(1024, 1024);
            RgbaImage? atlas2 = tile.AllChunks.Any(c => c.Layers.Count > 4) ? new RgbaImage(1024, 1024) : null;
            foreach (var c in tile.AllChunks)
            {
                int vb = verts.Count;
                for (int k = 0; k < 145; k++)
                {
                    int row = k / 17, rem = k % 17;
                    float i, j;
                    if (rem < 9) { i = row; j = rem; }
                    else { i = row + 0.5f; j = rem - 9 + 0.5f; }
                    var p = new Vector3(c.Position.X - i * TerrainMath.UnitSize, c.Position.Y - j * TerrainMath.UnitSize, c.Heights[k]) - a.Origin;
                    verts.Add(new TerrainVertex { Position = p, Normal = c.Normals[k], ChunkUv = new Vector2(j / 8f, i / 8f) });
                    if (ti == 0)
                    {
                        bmin = Vector3.Min(bmin, p);
                        bmax = Vector3.Max(bmax, p);
                    }
                }
                var dc = new TerrainDrawChunk { Tile = ti, ChunkX = c.X, ChunkY = c.Y, IndexStart = idx.Count, LayerCount = Math.Min(8, c.Layers.Count) };
                for (int qi = 0; qi < 8; qi++)
                    for (int qj = 0; qj < 8; qj++)
                    {
                        if (c.IsHole(qj, qi)) continue;
                        uint o00 = (uint)(vb + qi * 17 + qj), o01 = o00 + 1, o10 = (uint)(vb + (qi + 1) * 17 + qj), o11 = o10 + 1;
                        uint m = (uint)(vb + qi * 17 + 9 + qj);
                        idx.AddRange(new[] { o00, m, o01, o01, m, o11, o11, m, o10, o10, m, o00 });
                    }
                dc.IndexCount = idx.Count - dc.IndexStart;
                for (int L = 0; L < dc.LayerCount; L++)
                {
                    var path = c.Layers[L];
                    if (path.Length == 0) continue;
                    if (!texIndex.TryGetValue(path, out int t))
                    {
                        t = a.TexturePaths.Count;
                        texIndex[path] = t;
                        a.TexturePaths.Add(path);
                        string drawn = path;
                        RgbaImage? img = null;
                        if (opts.SpecularGround && WoWData.Core.Maps.GroundTextures.SpecularName(path) is { } spec)
                        {
                            if (opts.Exists?.Invoke(spec) ?? true) img = loadTexture(spec);
                            if (img != null) drawn = spec;
                            else a.MissingSpecular++;
                        }
                        img ??= loadTexture(path);
                        a.Textures.Add(img);
                        a.DrawnTexturePaths.Add(drawn);
                    }
                    dc.Layers[L] = t;
                }
                // alpha + shadow into the atlas cell of this chunk
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        int o = ((c.Y * 64 + y) * 1024 + c.X * 64 + x) * 4;
                        int s = y * 64 + x;
                        atlas.Pixels[o] = c.Alpha.Count > 1 && c.Alpha[1] is { } a1 ? a1[s] : (byte)0;
                        atlas.Pixels[o + 1] = c.Alpha.Count > 2 && c.Alpha[2] is { } a2 ? a2[s] : (byte)0;
                        atlas.Pixels[o + 2] = c.Alpha.Count > 3 && c.Alpha[3] is { } a3 ? a3[s] : (byte)0;
                        atlas.Pixels[o + 3] = c.Shadow != null && c.Shadow[s] ? (byte)0 : (byte)255;
                    }
                if (atlas2 != null && c.Alpha.Count > 4)
                    for (int y = 0; y < 64; y++)
                        for (int x = 0; x < 64; x++)
                        {
                            int o = ((c.Y * 64 + y) * 1024 + c.X * 64 + x) * 4;
                            int s = y * 64 + x;
                            for (int k = 0; k < 4; k++) atlas2.Pixels[o + k] = c.Alpha.Count > 4 + k && c.Alpha[4 + k] is { } ak ? ak[s] : (byte)0;
                        }
                a.Chunks.Add(dc);
                // liquids
                foreach (var l in c.Liquids)
                {
                    var col = LiquidColors[l.Kind];
                    for (int ty = 0; ty < 8; ty++)
                        for (int tx = 0; tx < 8; tx++)
                        {
                            if (!l.Tiles[ty * 8 + tx]) continue;
                            Vector3 P(int yy, int xx)
                            {
                                float h = l.Heights[yy * 9 + xx];
                                if (!float.IsFinite(h) || Math.Abs(h) > 1e5f) h = l.Min;
                                return new Vector3(c.Position.X - yy * TerrainMath.UnitSize, c.Position.Y - xx * TerrainMath.UnitSize, h) - a.Origin;
                            }
                            var q00 = P(ty, tx); var q01 = P(ty, tx + 1); var q10 = P(ty + 1, tx); var q11 = P(ty + 1, tx + 1);
                            foreach (var q in new[] { q00, q01, q11, q00, q11, q10 }) liquid.Add(new ColorVertex { Position = q, Color = col });
                        }
                }
            }
            a.AlphaAtlases.Add(atlas);
            a.AlphaAtlases2.Add(atlas2);
        }
        a.GroundTextures = a.TexturePaths.Count;
        a.SpecularGround = opts.SpecularGround;
        a.Vertices = verts.ToArray();
        a.Indices = idx.ToArray();
        a.Liquid = liquid.ToArray();
        if (bmin.X > bmax.X) { bmin = Vector3.Zero; bmax = new Vector3(TerrainMath.TileSize); }
        a.BoundsMin = Vector3.Min(bmin, bmax);
        a.BoundsMax = Vector3.Max(bmin, bmax);
        var originM = Matrix4x4.CreateTranslation(-a.Origin);
        // doodads: unique models once, placements deduplicated by unique id (border placements are in several tiles)
        if (opts.Doodads)
        {
            var models = new Dictionary<string, ModelAsset?>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<uint>();
            foreach (var tile in tiles)
                foreach (var d in tile.Doodads)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(d.Uid)) continue;
                    if (!models.TryGetValue(d.Model, out var asset))
                    {
                        asset = null;
                        if (models.Count < opts.MaxDoodadModels)
                        {
                            if (opts.LoadModel != null)
                            {
                                try { asset = ModelAsset.Build(d.Model, opts.LoadModel(d.Model), loadTexture); }
                                catch (Exception e) when (e is WoWData.Core.M2.M2Exception or System.IO.IOException or IndexOutOfRangeException or ArgumentException) { asset = null; }
                            }
                            else
                            {
                                var data = read(d.Model);
                                if (data != null)
                                {
                                    try { asset = ModelAsset.Build(d.Model, data, loadTexture); }
                                    catch (Exception e) when (e is WoWData.Core.M2.M2Exception or IndexOutOfRangeException or ArgumentException) { asset = null; }
                                }
                            }
                        }
                        models[d.Model] = asset;
                        if (asset == null) a.MissingModels++;
                    }
                    if (asset != null)
                    {
                        a.Doodads.Add((asset, d.World * originM));
                        a.DoodadUids.Add(d.Uid);
                    }
                }
            a.DoodadModels = models.Count(m => m.Value != null);
        }
        if (opts.Wmos)
        {
            var meshes = new Dictionary<string, WmoMesh?>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<uint>();
            foreach (var tile in tiles)
                foreach (var w in tile.Wmos)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(w.Uid)) continue;
                    if (!meshes.TryGetValue(w.Model, out var mesh))
                    {
                        mesh = BuildWmo(w.Model, read, loadTexture, a, texIndex, opts.NameOf);
                        meshes[w.Model] = mesh;
                        if (mesh == null) a.MissingModels++;
                    }
                    if (mesh != null)
                    {
                        a.Wmos.Add((mesh, w.World * originM));
                        a.WmoUids.Add(w.Uid);
                    }
                }
        }
        a.BuildTime = sw.Elapsed;
        return a;
    }

    /// <summary>One building on its own (the breakdown's "On its own" view): its groups at the origin, no terrain.</summary>
    /// <param name="nameOf">M4: names a FileDataID of a modern building (see <see cref="WmoGeometry.Load"/>)</param>
    public static TerrainAsset BuildBuilding(string path, Func<string, byte[]?> read, Func<string, RgbaImage?> loadTexture, Func<uint, string, string>? nameOf = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = new TerrainAsset();
        var mesh = BuildWmo(path, read, loadTexture, a, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), nameOf)
                   ?? throw new System.IO.IOException($"The building {path} could not be read.");
        a.Wmos.Add((mesh, Matrix4x4.Identity));
        a.WmoUids.Add(0);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices)
        {
            lo = Vector3.Min(lo, v.Position);
            hi = Vector3.Max(hi, v.Position);
        }
        a.BoundsMin = lo.X <= hi.X ? lo : Vector3.Zero;
        a.BoundsMax = lo.X <= hi.X ? hi : Vector3.One;
        a.BuildTime = sw.Elapsed;
        return a;
    }

    private static WmoMesh? BuildWmo(string path, Func<string, byte[]?> read, Func<string, RgbaImage?> loadTexture, TerrainAsset a, Dictionary<string, int> texIndex,
                                     Func<uint, string, string>? nameOf = null)
    {
        WmoGeometry? geo;
        try { geo = WmoGeometry.Load(path, read, nameOf); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { geo = null; }
        if (geo == null || geo.Groups.Count == 0) return null;
        var m = new WmoMesh { Path = path };
        var v = new List<StaticVertex>();
        var ind = new List<uint>();
        foreach (var g in geo.Groups)
        {
            int vb = v.Count;
            for (int i = 0; i < g.Positions.Length; i++) v.Add(new StaticVertex { Position = g.Positions[i], Normal = g.Normals[i], Uv = g.Uvs[i] });
            foreach (var b in g.Batches)
            {
                int start = ind.Count;
                for (int k = b.IndexStart; k < b.IndexStart + b.IndexCount; k++) ind.Add((uint)(vb + g.Indices[k]));
                int tex = -1;
                bool key = false, blend = false;
                if (b.Material >= 0 && b.Material < geo.MaterialTextures.Count)
                {
                    var tp = geo.MaterialTextures[b.Material];
                    uint mode = geo.MaterialBlend[b.Material];
                    key = mode == 1;
                    blend = mode >= 2;
                    if (tp.Length > 0)
                    {
                        if (!texIndex.TryGetValue(tp, out tex))
                        {
                            tex = a.TexturePaths.Count;
                            texIndex[tp] = tex;
                            a.TexturePaths.Add(tp);
                            a.Textures.Add(loadTexture(tp));
                        }
                    }
                }
                m.Batches.Add((start, ind.Count - start, tex, key, blend));
            }
        }
        m.Vertices = v.ToArray();
        m.Indices = ind.ToArray();
        return m;
    }
}

/// <summary>The pieces of a tile the breakdown view highlights in the 3D view: chunks (of the asset's first tile), doodads
/// and buildings by unique id, or the ground painted with a texture (lit by how much of it shows). Everything else is
/// drawn dimmed and greyer.</summary>
public sealed class TerrainHighlight
{
    public HashSet<int> Chunks { get; init; } = new();
    public HashSet<uint> Doodads { get; init; } = new();
    public HashSet<uint> Wmos { get; init; } = new();
    /// <summary>A ground texture's game path: the ground is lit where it is painted.</summary>
    public string? Texture { get; init; }
    public bool IsEmpty => Chunks.Count == 0 && Doodads.Count == 0 && Wmos.Count == 0 && Texture == null;
}

/// <summary>What a viewport shows in terrain mode: the asset and the display options. Changing anything raises
/// <see cref="Changed"/> so the viewport redraws. UI thread only.</summary>
public sealed class TerrainScene
{
    private TerrainAsset? _asset;
    private TerrainHighlight? _highlight;
    private bool _doodads = true, _wmos = true, _water = true, _wireframe, _textures = true;
    private Vector3 _background = new(0.53f, 0.66f, 0.80f);

    public event EventHandler? Changed;
    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public TerrainAsset? Asset { get => _asset; set { _asset = value; Raise(); } }
    public bool ShowDoodads { get => _doodads; set { _doodads = value; Raise(); } }
    public bool ShowWmos { get => _wmos; set { _wmos = value; Raise(); } }
    public bool ShowWater { get => _water; set { _water = value; Raise(); } }
    public bool Wireframe { get => _wireframe; set { _wireframe = value; Raise(); } }
    public bool ShowTextures { get => _textures; set { _textures = value; Raise(); } }
    public Vector3 Background { get => _background; set { _background = value; Raise(); } }
    /// <summary>What the breakdown view highlights (the rest is drawn dimmed), or null.</summary>
    public TerrainHighlight? Highlight { get => _highlight; set { _highlight = value; Raise(); } }

    /// <summary>Frame the first tile: a view from the south-east, looking at its centre from above.</summary>
    public static void FrameTile(OrbitCamera cam, TerrainAsset a)
    {
        var lo = a.BoundsMin;
        var hi = a.BoundsMax;
        cam.HomeYaw = 225f;
        cam.HomePitch = 40f;
        cam.FrontYaw = 180f;     // "Front": from the south looking north (WoW's +X is north)
        cam.Frame(lo, hi);
    }
}
