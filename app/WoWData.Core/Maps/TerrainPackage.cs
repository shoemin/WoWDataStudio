using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WoWData.Core.Imaging;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;

namespace WoWData.Core.Maps;

/// <summary>
/// wowdata_terrain.json: the "Terrain to Blender" package (one or more map tiles with their ground textures and,
/// optionally, the doodads and buildings as references). The import uses the tile ids, the checksums of the exported
/// tiles and the grid mapping written here.
/// </summary>
public sealed class TerrainPackageManifest
{
    public const string FileName = "wowdata_terrain.json";
    public const string FormatName = "wowdata-terrain-package";
    /// <summary>Ground textures in one tile's Blender material (GPU texture limits: 12 textures + 4 weight images).</summary>
    public const int MaxMaterialTextures = 12;

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("format_version")] public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("tool")] public string Tool { get; set; } = "WoW Data Studio";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("map")] public TerrainPackageMap Map { get; set; } = new();
    [JsonPropertyName("center")] public int[] Center { get; set; } = new int[2];
    /// <summary>World x, y (yards) of the centre tile's north-west corner: Blender's (0, 0).</summary>
    [JsonPropertyName("origin_world")] public double[] OriginWorld { get; set; } = new double[2];
    [JsonPropertyName("coordinates")] public string Coordinates { get; set; } =
        "Blender X = east = origin_world[1] - world y; Blender Y = north = world x - origin_world[0]; Blender Z = world z (height, yards). 1 unit = 1 yard.";
    [JsonPropertyName("grid")] public TerrainPackageGrid Grid { get; set; } = new();
    [JsonPropertyName("tiles")] public List<TerrainPackageTile> Tiles { get; set; } = new();
    [JsonPropertyName("textures")] public List<TerrainPackageTexture> Textures { get; set; } = new();
    [JsonPropertyName("references")] public TerrainPackageReferences? References { get; set; }
    [JsonPropertyName("import_rules")] public List<string> ImportRules { get; set; } = new()
    {
        "Only heights (Blender Z) come back: the game's ground is a fixed grid of 145 vertices per chunk (9 x 9 + 8 x 8).",
        "Vertices moved sideways are put back on the grid (only their height is kept); added vertices are ignored; deleted vertices keep their height.",
        "Shared edges must have the same heights on both tiles: use \"Sync shared edges\" in Blender, or let the app sync them on import.",
        "Ground textures, doodads and buildings in Blender are for looking only: they are not imported.",
    };

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TerrainPackageManifest? FromJson(string text)
    {
        try
        {
            var m = JsonSerializer.Deserialize<TerrainPackageManifest>(text);
            return m != null && m.Format == FormatName ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public TerrainPackageTile? Tile(int x, int y) => Tiles.FirstOrDefault(t => t.X == x && t.Y == y);
}

public sealed class TerrainPackageMap
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("directory")] public string Directory { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TerrainPackageGrid
{
    [JsonPropertyName("outer")] public int Outer { get; set; } = TerrainGrid.OuterSize;
    [JsonPropertyName("inner")] public int Inner { get; set; } = TerrainGrid.InnerSize;
    [JsonPropertyName("unit")] public double Unit { get; set; } = TerrainMath.UnitSize;
    [JsonPropertyName("tile_size")] public double TileSize { get; set; } = TerrainMath.TileSize;
    [JsonPropertyName("vertex_id")] public string VertexId { get; set; } =
        "wow_vid = 1 + row * 129 + col for the 129 x 129 outer vertices, 1 + 16641 + row * 128 + col for the 128 x 128 inner ones (rows north to south, columns west to east); 0 = added in Blender";
    [JsonPropertyName("heights_file")] public string HeightsFile { get; set; } =
        "little-endian: float32 outer[129 * 129], float32 inner[128 * 128] (absolute heights, yards), uint8 hole[128 * 128] (1 = the quad is a hole)";
    [JsonPropertyName("local_position")] public string LocalPosition { get; set; } =
        "outer (row, col): x = col * unit, y = -row * unit; inner: x = (col + 0.5) * unit, y = -(row + 0.5) * unit; relative to the tile object's location (its north-west corner)";
}

public sealed class TerrainPackageTile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("game_path")] public string GamePath { get; set; } = "";
    /// <summary>"game" or "workspace" (an earlier edit of yours).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "game";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("game_sha256")] public string? GameSha256 { get; set; }
    [JsonPropertyName("archive")] public string? Archive { get; set; }
    [JsonPropertyName("heights_file")] public string HeightsFile { get; set; } = "";
    /// <summary>Blender X, Y of the tile's north-west corner (its object's location).</summary>
    [JsonPropertyName("location")] public double[] Location { get; set; } = new double[2];
    /// <summary>The object's viewport colour (r, g, b, 0..1).</summary>
    [JsonPropertyName("color")] public double[] Color { get; set; } = new double[3];
    [JsonPropertyName("textures")] public List<TerrainTileTexture> Textures { get; set; } = new();
    /// <summary>Textures painted on the tile that are not in its Blender material (more than 12).</summary>
    [JsonPropertyName("textures_not_in_material")] public List<string> TexturesNotInMaterial { get; set; } = new();
    [JsonPropertyName("height_min")] public double HeightMin { get; set; }
    [JsonPropertyName("height_max")] public double HeightMax { get; set; }
    /// <summary>Neighbours in the package: N, E, S, W -> tile id (or null).</summary>
    [JsonPropertyName("neighbours")] public Dictionary<string, string?> Neighbours { get; set; } = new();
}

public sealed class TerrainTileTexture
{
    /// <summary>Index into the package's textures.</summary>
    [JsonPropertyName("texture")] public int Texture { get; set; }
    [JsonPropertyName("weight_image")] public string WeightImage { get; set; } = "";
    /// <summary>0 = red, 1 = green, 2 = blue.</summary>
    [JsonPropertyName("channel")] public int Channel { get; set; }
    [JsonPropertyName("coverage")] public double Coverage { get; set; }
}

public sealed class TerrainPackageTexture
{
    /// <summary>The name in the map file (MTEX).</summary>
    [JsonPropertyName("game_path")] public string GamePath { get; set; } = "";
    /// <summary>The PNG the Blender material shows: the file the game draws with the chosen rule (see <see cref="Drawn"/>).</summary>
    [JsonPropertyName("png")] public string? Png { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    /// <summary>0.6.1: the base file's PNG and the specular variant ("&lt;name&gt;_s.blp") with its PNG, when the game has it.</summary>
    [JsonPropertyName("base_png")] public string? BasePng { get; set; }
    [JsonPropertyName("specular_game_path")] public string? SpecularGamePath { get; set; }
    [JsonPropertyName("specular_png")] public string? SpecularPng { get; set; }
    /// <summary>"base" or "specular": which file <see cref="Png"/> is.</summary>
    [JsonPropertyName("drawn")] public string Drawn { get; set; } = "base";
}

public sealed class TerrainPackageReferences
{
    [JsonPropertyName("file")] public string File { get; set; } = "references.gltf";
    [JsonPropertyName("doodads")] public int Doodads { get; set; }
    [JsonPropertyName("buildings")] public int Buildings { get; set; }
    [JsonPropertyName("models")] public int Models { get; set; }
    [JsonPropertyName("missing")] public int Missing { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "Doodads and buildings placed for context, as linked copies of simple meshes. They are ignored when the terrain is imported back (this version imports heights only).";
}

public sealed class TerrainExportRequest
{
    public MapInfo Map { get; init; } = null!;
    public List<(int X, int Y)> Tiles { get; init; } = new();
    public (int X, int Y) Center { get; init; }
    /// <summary>Game path -> bytes, your workspace first.</summary>
    public Func<string, byte[]?> ReadCurrent { get; init; } = _ => null;
    /// <summary>Game path -> the game's own bytes.</summary>
    public Func<string, byte[]?> ReadGame { get; init; } = _ => null;
    public Func<string, bool> IsModified { get; init; } = _ => false;
    public Func<string, string?> ArchiveOf { get; init; } = _ => null;
    public Func<string, RgbaImage?> LoadTexture { get; init; } = _ => null;
    /// <summary>Game path -> whether the file exists (finds the "_s" specular variants of the ground textures).</summary>
    public Func<string, bool> Exists { get; init; } = _ => false;
    /// <summary>True: the Blender materials show the "_s" files (what the game draws with terrain specular on).</summary>
    public bool SpecularGround { get; init; }
    public bool IncludeReferences { get; init; }
    public string PackageFolder { get; init; } = "";
    public Action<string>? Progress { get; init; }
}

public sealed class TerrainExportResult
{
    public string Folder { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public TerrainPackageManifest Manifest { get; init; } = null!;
    public List<string> Notes { get; } = new();
    public long Bytes { get; set; }
}

/// <summary>
/// "Open in Blender" for terrain: writes the package the Blender add-on turns into one object per tile ("Tile 32_48",
/// its own collection and viewport colour, a label, vertex groups EDGE_N / E / S / W), with the tile's ground textures
/// blended as in the game (the textures as PNG plus their weights, 64 x 64 per chunk, as images; the add-on builds the
/// material nodes), and optionally the doodads and buildings as placed references.
/// </summary>
public static class TerrainExporter
{
    /// <summary>Viewport colours: tiles whose x or y differ by one never share a colour (index (x mod 3) + 3 (y mod 3)).</summary>
    private static readonly double[][] Palette =
    {
        new[] { 0.90, 0.38, 0.30 }, new[] { 0.30, 0.60, 0.92 }, new[] { 0.42, 0.80, 0.38 },
        new[] { 0.96, 0.76, 0.24 }, new[] { 0.68, 0.44, 0.90 }, new[] { 0.24, 0.80, 0.78 },
        new[] { 0.95, 0.52, 0.76 }, new[] { 0.62, 0.66, 0.40 }, new[] { 0.98, 0.58, 0.22 },
    };

    public static double[] ColorOf(int x, int y) => Palette[(x % 3) + 3 * (y % 3)];

    public static string TileId(int x, int y) => $"Tile {x}_{y}";

    /// <summary>Blender X, Y of a tile's north-west corner when tile <paramref name="center"/>'s corner is (0, 0).</summary>
    public static (double X, double Y) Location((int X, int Y) center, int x, int y) =>
        ((x - center.X) * (double)TerrainMath.TileSize, (center.Y - y) * (double)TerrainMath.TileSize);

    /// <summary>The tiles of a square of <paramref name="size"/> x <paramref name="size"/> around a tile that the map has.</summary>
    public static List<(int X, int Y)> Square(MapInfo map, int x, int y, int size)
    {
        int r = size / 2;
        var list = new List<(int, int)>();
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (map.HasTile(x + dx, y + dy)) list.Add((x + dx, y + dy));
        return list;
    }

    public static string NewPackageFolder(string dir, MapInfo map, (int X, int Y) center, int count)
    {
        var name = $"{map.Directory}_{center.X}_{center.Y}_terrain" + (count > 1 ? $"_{count}tiles" : "");
        var p = Path.Combine(dir, name);
        for (int k = 2; Directory.Exists(p); k++) p = Path.Combine(dir, $"{name}_{k}");
        return p;
    }

    public static byte[] HeightsFile(TerrainGrid g)
    {
        var b = new byte[TerrainGrid.Count * 4 + TerrainGrid.InnerCount];
        for (int i = 0; i < TerrainGrid.OuterCount; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), float.IsNaN(g.Outer[i]) ? 0f : g.Outer[i]);
        for (int i = 0; i < TerrainGrid.InnerCount; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan((TerrainGrid.OuterCount + i) * 4), float.IsNaN(g.Inner[i]) ? 0f : g.Inner[i]);
        for (int i = 0; i < TerrainGrid.InnerCount; i++) b[TerrainGrid.Count * 4 + i] = (byte)(g.Holes[i] ? 1 : 0);
        return b;
    }

    public static TerrainExportResult Export(TerrainExportRequest req)
    {
        if (req.Tiles.Count == 0) throw new InvalidOperationException("No tile was chosen.");
        var dir = req.PackageFolder;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "tiles"));
        Directory.CreateDirectory(Path.Combine(dir, "textures"));
        Directory.CreateDirectory(Path.Combine(dir, "weights"));
        var center = req.Center;
        var corner = new Vector2(TerrainMath.MapHalf - center.Y * TerrainMath.TileSize, TerrainMath.MapHalf - center.X * TerrainMath.TileSize);
        var man = new TerrainPackageManifest
        {
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Note = "Made by WoW Data Studio's \"Open in Blender\" for terrain. Open it in Blender with the WoW Data Studio add-on (File > Import > WoW 1.12 terrain), " +
                   "edit the heights, then \"Export terrain for WoW Data Studio\" and \"Import edited terrain...\" in the app.",
            Map = new TerrainPackageMap { Id = req.Map.Id, Directory = req.Map.Directory, Name = req.Map.Name },
            Center = new[] { center.X, center.Y },
            OriginWorld = new double[] { corner.X, corner.Y },
        };
        var res = new TerrainExportResult { Folder = dir, ManifestPath = Path.Combine(dir, TerrainPackageManifest.FileName), Manifest = man };
        var texIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pngNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<TerrainTile>();
        var set = req.Tiles.ToHashSet();
        foreach (var (x, y) in req.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X))
        {
            var path = req.Map.TilePath(x, y);
            req.Progress?.Invoke($"Tile {x},{y}...");
            var data = req.ReadCurrent(path) ?? throw new IOException($"{path} could not be read.");
            var game = req.ReadGame(path);
            var tile = TerrainTile.Load(data, req.Map.Directory, x, y);
            loaded.Add(tile);
            var grid = TerrainGrid.FromTile(tile);
            var hf = $"tiles/{x}_{y}.bin";
            File.WriteAllBytes(Path.Combine(dir, hf.Replace('/', Path.DirectorySeparatorChar)), HeightsFile(grid));
            var (lx, ly) = Location(center, x, y);
            var (lo, hi) = tile.HeightRange;
            var pt = new TerrainPackageTile
            {
                Id = TileId(x, y), X = x, Y = y, GamePath = path, Source = req.IsModified(path) ? "workspace" : "game", Sha256 = ModelExporter.Sha(data), Size = data.Length,
                GameSha256 = game == null ? null : ModelExporter.Sha(game), Archive = req.ArchiveOf(path), HeightsFile = hf, Location = new[] { lx, ly }, Color = ColorOf(x, y),
                HeightMin = lo, HeightMax = hi,
                Neighbours = new Dictionary<string, string?>
                {
                    ["N"] = set.Contains((x, y - 1)) ? TileId(x, y - 1) : null, ["E"] = set.Contains((x + 1, y)) ? TileId(x + 1, y) : null,
                    ["S"] = set.Contains((x, y + 1)) ? TileId(x, y + 1) : null, ["W"] = set.Contains((x - 1, y)) ? TileId(x - 1, y) : null,
                },
            };
            // ground textures: the PNGs once per package, the weights 3 per RGB image per tile
            var cover = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in tile.AllChunks)
                foreach (var (tp, w) in TileBreakdown.ChunkWeights(c))
                    cover[tp] = cover.GetValueOrDefault(tp) + w.Sum(v => (double)v) / (4096.0 * 256);
            var used = cover.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key).ToList();
            var inMaterial = used.Take(TerrainPackageManifest.MaxMaterialTextures).ToList();
            pt.TexturesNotInMaterial = used.Skip(TerrainPackageManifest.MaxMaterialTextures).Select(p => WoWData.Core.Mpq.ClientData.ResolveName(p)).ToList();
            if (pt.TexturesNotInMaterial.Count > 0)
                res.Notes.Add($"Tile {x},{y} has {used.Count} ground textures; its Blender material shows the {TerrainPackageManifest.MaxMaterialTextures} that cover the most ground (GPU limit).");
            for (int k = 0; k < inMaterial.Count; k += 3)
            {
                var img = new RgbaImage(1024, 1024);
                for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
                var wname = $"weights/{x}_{y}_{k / 3}.png";
                for (int ch = 0; ch < 3 && k + ch < inMaterial.Count; ch++)
                {
                    var tp = inMaterial[k + ch];
                    var map = TileBreakdown.WeightMap(tile, tp, 1024);
                    for (int i = 0; i < map.Length; i++) img.Pixels[i * 4 + ch] = map[i];
                    if (!texIndex.TryGetValue(tp, out int ti))
                    {
                        ti = man.Textures.Count;
                        texIndex[tp] = ti;
                        var resolved = WoWData.Core.Mpq.ClientData.ResolveName(tp);
                        var pkt = new TerrainPackageTexture { GamePath = resolved };
                        string? SavePng(string gamePath, RgbaImage img)
                        {
                            var name = ModelExporter.SafeName(Path.GetFileNameWithoutExtension(gamePath));
                            var pn = name;
                            for (int n = 2; !pngNames.Add(pn); n++) pn = $"{name}_{n}";
                            Png.Save(img, Path.Combine(dir, "textures", pn + ".png"), false);
                            return $"textures/{pn}.png";
                        }
                        var timg = req.LoadTexture(tp);
                        if (timg != null) pkt.BasePng = SavePng(resolved, timg);
                        var spec = GroundTextures.SpecularName(resolved);
                        RgbaImage? simg = null;
                        if (spec != null && req.Exists(spec))
                        {
                            pkt.SpecularGamePath = spec;
                            simg = req.LoadTexture(spec);
                            if (simg != null) pkt.SpecularPng = SavePng(spec, simg);   // colours only: the alpha (shine) would look see-through
                        }
                        bool useSpec = req.SpecularGround && pkt.SpecularPng != null;
                        pkt.Png = useSpec ? pkt.SpecularPng : pkt.BasePng;
                        pkt.Drawn = useSpec ? "specular" : "base";
                        var shown = useSpec ? simg : timg;
                        if (shown != null)
                        {
                            pkt.Width = shown.Width;
                            pkt.Height = shown.Height;
                        }
                        if (timg == null && simg == null) res.Notes.Add($"The ground texture {resolved} was not found; it shows grey in Blender.");
                        else if (req.SpecularGround && spec != null && pkt.SpecularGamePath == null)
                            res.Notes.Add($"The game has no {Path.GetFileName(spec)} (the file it draws with terrain specular on); Blender shows {Path.GetFileName(resolved)}.");
                        man.Textures.Add(pkt);
                    }
                    pt.Textures.Add(new TerrainTileTexture { Texture = ti, WeightImage = wname, Channel = ch, Coverage = Math.Round(cover[tp], 4) });
                }
                Png.Save(img, Path.Combine(dir, wname.Replace('/', Path.DirectorySeparatorChar)), false);
            }
            man.Tiles.Add(pt);
        }
        if (req.IncludeReferences)
        {
            req.Progress?.Invoke("Doodads and buildings...");
            man.References = WriteReferences(loaded, corner, req, Path.Combine(dir, "references.gltf"));
            if (man.References.Missing > 0) res.Notes.Add($"{man.References.Missing} doodad or building models were not found and are left out.");
        }
        File.WriteAllText(res.ManifestPath, man.ToJson());
        res.Bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return res;
    }

    // ------------------------------------------------------------------ references (doodads and buildings)

    private static readonly Matrix4x4 ToGltfRow = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);    // model (x, y, z) -> glTF (x, z, -y)
    private static readonly Matrix4x4 FromGltfRow = Matrix4x4.Transpose(ToGltfRow);

    /// <summary>World (x north, y west, z up) -> package / Blender (x east, y north, z up) for a package whose origin
    /// is the world point <paramref name="corner"/>.</summary>
    public static Matrix4x4 WorldToPackage(Vector2 corner) => new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, corner.Y, -corner.X, 0, 1);

    private static TerrainPackageReferences WriteReferences(List<TerrainTile> tiles, Vector2 corner, TerrainExportRequest req, string file)
    {
        var refs = new TerrainPackageReferences { File = Path.GetFileName(file) };
        var b = new GltfBuilder();
        var materials = new JsonArray
        {
            new JsonObject { ["name"] = "WoW doodad (reference)", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.36, 0.55, 0.30, 1.0), ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.9 }, ["doubleSided"] = true },
            new JsonObject { ["name"] = "WoW building (reference)", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.72, 0.62, 0.48, 1.0), ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.9 }, ["doubleSided"] = true },
        };
        b.Json["materials"] = materials;
        var meshes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = b.AddNode(new JsonObject { ["name"] = "WoW references (ignored on import)", ["extras"] = new JsonObject { ["wow_references"] = true } });
        ((JsonArray)b.Json["scenes"]![0]!["nodes"]!).Add(root);
        var w2p = WorldToPackage(corner);
        int Mesh(string path, bool wmo)
        {
            if (meshes.TryGetValue(path, out int mi)) return mi;
            mi = -1;
            try
            {
                var (pos, tri) = wmo ? WmoMesh(path, req.ReadCurrent) : M2Mesh(path, req.ReadCurrent);
                if (pos.Length > 0 && tri.Length > 0)
                {
                    var flat = new float[pos.Length * 3];
                    for (int i = 0; i < pos.Length; i++)
                    {
                        var g = ModelExporter.ToGltf(pos[i]);
                        flat[i * 3] = g.X; flat[i * 3 + 1] = g.Y; flat[i * 3 + 2] = g.Z;
                    }
                    int pa = b.AccessorF(flat, "VEC3", GltfBuilder.ArrayBuffer, minMax: true);
                    int ia = b.AccessorIndices(tri, pos.Length);
                    b.Arr("meshes").Add(new JsonObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(path),
                        ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = pa }, ["indices"] = ia, ["material"] = wmo ? 1 : 0 }),
                        ["extras"] = new JsonObject { ["wow_model"] = path },
                    });
                    mi = b.Arr("meshes").Count - 1;
                    refs.Models++;
                }
            }
            catch (Exception e) when (e is M2Exception or IndexOutOfRangeException or ArgumentException or InvalidOperationException) { mi = -1; }
            if (mi < 0) missing.Add(path);
            meshes[path] = mi;
            return mi;
        }
        void Place(string path, uint uid, Matrix4x4 world, bool wmo)
        {
            int mi = Mesh(path, wmo);
            if (mi < 0) return;
            var n = FromGltfRow * (world * w2p) * ToGltfRow;
            var node = new JsonObject
            {
                ["name"] = $"{Path.GetFileNameWithoutExtension(path)} {uid}", ["mesh"] = mi,
                ["matrix"] = new JsonArray(n.M11, n.M12, n.M13, n.M14, n.M21, n.M22, n.M23, n.M24, n.M31, n.M32, n.M33, n.M34, n.M41, n.M42, n.M43, n.M44),
                ["extras"] = new JsonObject { ["wow_reference"] = wmo ? "building" : "doodad", ["wow_model"] = path, ["wow_uid"] = uid },
            };
            b.AddChild(root, b.AddNode(node));
            if (wmo) refs.Buildings++;
            else refs.Doodads++;
        }
        var seenD = new HashSet<uint>();
        var seenW = new HashSet<uint>();
        foreach (var t in tiles)
        {
            foreach (var d in t.Doodads) if (seenD.Add(d.Uid)) Place(d.Model, d.Uid, d.World, false);
            foreach (var w in t.Wmos) if (seenW.Add(w.Uid)) Place(w.Model, w.Uid, w.World, true);
        }
        refs.Missing = missing.Count;
        var bin = b.Bin();
        var binName = Path.GetFileNameWithoutExtension(file) + ".bin";
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(file)!, binName), bin);
        File.WriteAllText(file, b.Finish(binName, bin.Length).ToJsonString(GltfBuilder.Compact));
        return refs;
    }

    /// <summary>A doodad as a static mesh: view 0's triangles in the rest pose.</summary>
    public static (Vector3[] Positions, int[] Triangles) M2Mesh(string path, Func<string, byte[]?> read)
    {
        var data = read(path);
        if (data == null) return (Array.Empty<Vector3>(), Array.Empty<int>());
        var m = M2Model.Load(data);
        if (m.Views.Length == 0) return (Array.Empty<Vector3>(), Array.Empty<int>());
        var v = m.Views[0];
        var pos = new Vector3[v.Indices.Length];
        for (int i = 0; i < pos.Length; i++) pos[i] = v.Indices[i] < m.Vertices.Length ? m.Vertices[v.Indices[i]].Position : Vector3.Zero;
        var tri = v.Triangles.Where(t => t < pos.Length).Select(t => (int)t).ToArray();
        return (pos, tri.Length % 3 == 0 ? tri : tri[..(tri.Length / 3 * 3)]);
    }

    /// <summary>A building as one static mesh: every group's triangles.</summary>
    public static (Vector3[] Positions, int[] Triangles) WmoMesh(string path, Func<string, byte[]?> read)
    {
        var geo = WmoGeometry.Load(path, read);
        if (geo == null) return (Array.Empty<Vector3>(), Array.Empty<int>());
        var pos = new List<Vector3>();
        var tri = new List<int>();
        foreach (var g in geo.Groups)
        {
            int vb = pos.Count;
            pos.AddRange(g.Positions);
            foreach (var bt in g.Batches)
                for (int k = bt.IndexStart; k + 3 <= bt.IndexStart + bt.IndexCount && k + 3 <= g.Indices.Length; k += 3)
                {
                    tri.Add(vb + g.Indices[k]);
                    tri.Add(vb + g.Indices[k + 1]);
                    tri.Add(vb + g.Indices[k + 2]);
                }
        }
        return (pos.ToArray(), tri.ToArray());
    }

    /// <summary>A building as a static glTF of its own (Open in Blender on a building: for looking, not importable).</summary>
    public static string ExportBuilding(string path, Func<string, byte[]?> read, string folder)
    {
        Directory.CreateDirectory(folder);
        var (pos, tri) = WmoMesh(path, read);
        if (pos.Length == 0) throw new IOException($"{path} has no geometry that could be read.");
        var b = new GltfBuilder();
        var flat = new float[pos.Length * 3];
        for (int i = 0; i < pos.Length; i++)
        {
            var g = ModelExporter.ToGltf(pos[i]);
            flat[i * 3] = g.X; flat[i * 3 + 1] = g.Y; flat[i * 3 + 2] = g.Z;
        }
        b.Json["materials"] = new JsonArray(new JsonObject { ["name"] = "WoW building", ["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.72, 0.62, 0.48, 1.0), ["metallicFactor"] = 0.0 }, ["doubleSided"] = true });
        int pa = b.AccessorF(flat, "VEC3", GltfBuilder.ArrayBuffer, minMax: true);
        int ia = b.AccessorIndices(tri, pos.Length);
        b.Arr("meshes").Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(path), ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = pa }, ["indices"] = ia, ["material"] = 0 }) });
        int node = b.AddNode(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(path), ["mesh"] = 0, ["extras"] = new JsonObject { ["wow_model"] = path, ["wow_view_only"] = true } });
        ((JsonArray)b.Json["scenes"]![0]!["nodes"]!).Add(node);
        var name = ModelExporter.SafeName(Path.GetFileNameWithoutExtension(path));
        var bin = b.Bin();
        File.WriteAllBytes(Path.Combine(folder, name + ".bin"), bin);
        var gltf = Path.Combine(folder, name + ".gltf");
        File.WriteAllText(gltf, b.Finish(name + ".bin", bin.Length).ToJsonString(GltfBuilder.Compact));
        return gltf;
    }
}
