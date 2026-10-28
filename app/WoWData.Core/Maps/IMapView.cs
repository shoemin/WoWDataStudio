using WoWData.Core.Imaging;
using WoWData.Core.Usage;

namespace WoWData.Core.Maps;

/// <summary>
/// M4 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 2.2): what the Maps tab and "wds maps ..." read about the maps of
/// the open game, for either kind of client. <see cref="MapCatalog"/> is your 1.12 game (unchanged behind this
/// interface); <see cref="MapCatalog160"/> is a modern client opened read only.
/// </summary>
public interface IMapView
{
    /// <summary>Every map with its tiles (continents first, then the other world maps, then instances by kind).</summary>
    List<MapInfo> Maps { get; }
    MapInfo? ById(int id);
    MapInfo? ByDirectory(string dir);
    /// <summary>The name of an area id ("(no zone)" for 0, "area N" when unknown).</summary>
    string AreaName(int id);
    /// <summary>The zone (top-level area) of an area id.</summary>
    int ZoneOf(int area);
    /// <summary>The 256 chunk area ids of every tile of a map.</summary>
    Dictionary<(int X, int Y), int[]> TileAreas(MapInfo map);
    /// <summary>The zones of a map with the tiles they cover.</summary>
    List<ZoneInfo> Zones(MapInfo map, Dictionary<(int X, int Y), int[]>? tileAreas = null);
    /// <summary>The zones and subzones of one tile with their share of its 256 chunks, biggest first.</summary>
    List<(int Area, string Name, int Chunks)> TileZones(int[]? areas);
    /// <summary>The minimap picture of one tile, or null.</summary>
    RgbaImage? MinimapTile(MapInfo map, int x, int y);
    /// <summary>The minimap overview of a map, <paramref name="cell"/> pixels per tile, cached as PNG in <paramref name="cacheDir"/>.</summary>
    MinimapOverview Overview(MapInfo map, int cell, string? cacheDir, Action<int, int>? progress = null, CancellationToken ct = default);
    /// <summary>"On this tile": the zones and what is placed on the tile (and, for your game, spawned there).</summary>
    UsageReport TileReport(MapInfo map, int x, int y, TerrainTile? tile, int[]? areas, Func<string, bool>? isEntry = null);
    /// <summary>A tile as terrain (what the 3D view draws); null when the map has no such tile file. Throws
    /// <see cref="IOException"/> when the tile's file is there but cannot be read (encrypted, not downloaded).</summary>
    TerrainTile? LoadTerrain(MapInfo map, int x, int y);
}

/// <summary>The parts of the minimap overview and of "On this tile" both kinds of map catalog share.</summary>
public static class MapViews
{
    /// <summary>
    /// The overview of a map: its minimap tiles at <paramref name="cell"/> pixels each over the tiles' bounding box (one
    /// tile of margin), tiles without a minimap in grey, the background dark. Cached as PNG in <paramref name="cacheDir"/>
    /// under the catalog's <paramref name="fingerprint"/>.
    /// </summary>
    public static MinimapOverview Overview(MapInfo map, int cell, string? cacheDir, string fingerprint, Func<int, int, RgbaImage?> minimapTile,
                                           Action<int, int>? progress, CancellationToken ct)
    {
        var (minX, minY, maxX, maxY) = map.Bounds;
        minX = Math.Max(0, minX - 1); minY = Math.Max(0, minY - 1);
        maxX = Math.Min(63, maxX + 1); maxY = Math.Min(63, maxY + 1);
        int w = (maxX - minX + 1) * cell, h = (maxY - minY + 1) * cell;
        string? cacheFile = cacheDir == null ? null : Path.Combine(cacheDir, $"{map.Directory}_{cell}.png");
        string key = $"{fingerprint}|{minX},{minY},{maxX},{maxY}|{cell}|v1";
        if (cacheFile != null && File.Exists(cacheFile) && File.Exists(cacheFile + ".key") && File.ReadAllText(cacheFile + ".key") == key)
        {
            try { return new MinimapOverview(map, Png.Load(cacheFile), minX, minY, cell, true); }
            catch (Exception e) when (e is IOException or ImageFormatException) { }
        }
        var img = new RgbaImage(w, h);
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = 0x1F; img.Pixels[i + 1] = 0x24; img.Pixels[i + 2] = 0x2C; img.Pixels[i + 3] = 255;
        }
        var list = new List<(int X, int Y)>();
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (map.HasTile(x, y)) list.Add((x, y));
        int done = 0;
        Parallel.ForEach(list, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, t =>
        {
            var tile = minimapTile(t.X, t.Y);
            int ox = (t.X - minX) * cell, oy = (t.Y - minY) * cell;
            if (tile == null)
            {
                FillCell(img, ox, oy, cell, 0x55, 0x5D, 0x68);
            }
            else
            {
                var small = tile.Width == cell && tile.Height == cell ? tile : ImageOps.Resize(tile, cell, cell);
                for (int yy = 0; yy < cell; yy++)
                    Buffer.BlockCopy(small.Pixels, yy * cell * 4, img.Pixels, ((oy + yy) * w + ox) * 4, cell * 4);
                for (int yy = 0; yy < cell; yy++)
                    for (int xx = 0; xx < cell; xx++) img.Pixels[((oy + yy) * w + ox + xx) * 4 + 3] = 255;
            }
            int d = Interlocked.Increment(ref done);
            if (d % 16 == 0) progress?.Invoke(d, list.Count);
        });
        if (cacheFile != null)
        {
            try
            {
                Directory.CreateDirectory(cacheDir!);
                Png.Save(img, cacheFile + ".tmp", false);
                File.Move(cacheFile + ".tmp", cacheFile, true);
                File.WriteAllText(cacheFile + ".key", key);
            }
            catch (IOException) { }
        }
        return new MinimapOverview(map, img, minX, minY, cell, false);
    }

    private static void FillCell(RgbaImage img, int ox, int oy, int cell, byte r, byte g, byte b)
    {
        for (int y = 0; y < cell; y++)
            for (int x = 0; x < cell; x++)
            {
                int o = ((oy + y) * img.Width + ox + x) * 4;
                img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; img.Pixels[o + 3] = 255;
            }
    }

    /// <summary>The zones and subzones of one tile with their share of its 256 chunks, biggest first.</summary>
    public static List<(int Area, string Name, int Chunks)> TileZones(IMapView view, int[]? areas)
    {
        if (areas == null) return new();
        return areas.Where(a => a != 0).GroupBy(a => a).Select(g => (g.Key, ZoneName(view, g.Key), g.Count())).OrderByDescending(t => t.Item3).ToList();
    }

    private static string ZoneName(IMapView view, int area)
    {
        int z = view.ZoneOf(area);
        return z == area ? view.AreaName(area) : $"{view.AreaName(area)} ({view.AreaName(z)})";
    }

    /// <summary>The zones of a map with the tiles they cover (most chunks first), from the tiles' chunk area ids.</summary>
    public static List<ZoneInfo> Zones(IMapView view, Dictionary<(int X, int Y), int[]> tileAreas, Func<int, int> parentOf)
    {
        var chunks = new Dictionary<int, int>();
        var tiles = new Dictionary<int, HashSet<(int, int)>>();
        foreach (var (xy, areas) in tileAreas)
            foreach (var a in areas)
            {
                if (a == 0) continue;
                int z = view.ZoneOf(a);
                chunks[z] = chunks.GetValueOrDefault(z) + 1;
                if (!tiles.TryGetValue(z, out var set)) tiles[z] = set = new HashSet<(int, int)>();
                set.Add(xy);
            }
        return chunks.Keys.Select(z => new ZoneInfo(z, view.AreaName(z), parentOf(z), chunks[z],
                tiles[z].OrderBy(t => t.Item2).ThenBy(t => t.Item1).ToList()))
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The zones section of "On this tile".</summary>
    public static void AddZones(UsageReport rep, List<(int Area, string Name, int Chunks)> zones)
    {
        var zs = rep.Add("zones", "Zones on this tile");
        zs.Summary = zones.Count == 0 ? "no zone ids" : $"{zones.Count} zone{(zones.Count == 1 ? "" : "s")}/subzone{(zones.Count == 1 ? "" : "s")}";
        foreach (var (area, name, chunks) in zones) zs.Line().Add(name).Muted($"  {chunks} of 256 chunks ({chunks * 100 / 256}%)");
    }

    /// <summary>The doodads, buildings, ground textures and terrain sections of "On this tile" (each file name links to
    /// its entry in the Browse list when <paramref name="isEntry"/> knows it).</summary>
    public static void AddPlacements(UsageReport rep, TerrainTile tile, Func<string, bool>? isEntry)
    {
        var dd = rep.Add("doodads", "Doodads placed (M2 models)");
        var groups = tile.Doodads.GroupBy(d => d.Model, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        dd.Summary = $"{tile.Doodads.Count} placements of {groups.Count} models";
        foreach (var g in groups.Take(40))
        {
            var key = "file:" + g.Key;
            var l = dd.Line();
            if (isEntry?.Invoke(key) == true) l.Add(Path.GetFileName(g.Key), key);
            else l.Add(Path.GetFileName(g.Key));
            l.Muted($"  x{g.Count()}  {Path.GetDirectoryName(g.Key)}");
        }
        dd.More = Math.Max(0, groups.Count - 40);
        var ws = rep.Add("wmos", "Buildings placed (WMO)");
        var wg = tile.Wmos.GroupBy(w => w.Model, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        ws.Summary = $"{tile.Wmos.Count} placements of {wg.Count} buildings";
        foreach (var g in wg)
        {
            var key = "file:" + g.Key;
            var l = ws.Line();
            if (isEntry?.Invoke(key) == true) l.Add(Path.GetFileName(g.Key), key);
            else l.Add(Path.GetFileName(g.Key));
            l.Muted($"  x{g.Count()}  doodad set {string.Join(", ", g.Select(w => w.DoodadSet).Distinct())}");
        }
        var tx = rep.Add("textures", "Ground textures");
        tx.Summary = $"{tile.Textures.Count} textures";
        foreach (var t in tile.Textures.Take(30))
        {
            var key = "file:" + t;
            var l = tx.Line();
            if (isEntry?.Invoke(key) == true) l.Add(Path.GetFileName(t), key);
            else l.Add(Path.GetFileName(t));
            l.Muted($"  {Path.GetDirectoryName(t)}");
        }
        int water = tile.AllChunks.Count(c => c.Liquids.Count > 0), holes = tile.AllChunks.Count(c => c.HasHoles);
        var (lo, hi) = tile.HeightRange;
        var tr = rep.Add("terrain", "Terrain");
        tr.Summary = $"heights {lo:0} to {hi:0} yards";
        tr.Line().Add($"{tile.AllChunks.Count()} chunks, heights {lo:0.#} to {hi:0.#} yards, {tile.AllChunks.Sum(c => c.Layers.Count)} texture layers");
        tr.Line().Add(water > 0 ? $"water or other liquid in {water} chunks ({string.Join(", ", tile.AllChunks.SelectMany(c => c.Liquids).Select(l => l.Kind).Distinct())})" : "no water");
        tr.Line().Add(holes > 0 ? $"holes in {holes} chunks (caves, building entrances)" : "no holes");
        tr.Line().Muted($"file size {tile.FileSize / 1024.0:0} KB");
    }
}
