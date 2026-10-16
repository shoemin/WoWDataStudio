using System.Text;
using Microsoft.Data.Sqlite;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Imaging;
using WoWData.Core.Mpq;
using WoWData.Core.Usage;

namespace WoWData.Core.Maps;

/// <summary>One map of Map.dbc with its WDT tile list.</summary>
public sealed class MapInfo
{
    public int Id { get; init; }
    public string Directory { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Map.dbc field 2: 0 world, 1 dungeon, 2 raid, 3 battleground.</summary>
    public int InstanceType { get; init; }
    public bool[] Tiles { get; init; } = new bool[64 * 64];
    public int TileCount { get; init; }
    /// <summary>A map that is one building (WDT MPHD flag 1): no terrain tiles.</summary>
    public bool IsGlobalWmo { get; init; }
    public string? GlobalWmo { get; init; }

    public string Kind => InstanceType switch { 1 => "Dungeon", 2 => "Raid", 3 => "Battleground", _ => TileCount >= 100 ? "Continent" : "World map" };
    public string Label => $"{Name} ({Directory})";
    public string Detail => IsGlobalWmo ? "a single building, no terrain tiles" : $"{TileCount} tile{(TileCount == 1 ? "" : "s")}";
    public bool HasTile(int x, int y) => x is >= 0 and < 64 && y is >= 0 and < 64 && Tiles[y * 64 + x];

    public (int MinX, int MinY, int MaxX, int MaxY) Bounds
    {
        get
        {
            int minX = 64, minY = 64, maxX = -1, maxY = -1;
            for (int i = 0; i < 4096; i++)
                if (Tiles[i])
                {
                    minX = Math.Min(minX, i % 64); maxX = Math.Max(maxX, i % 64);
                    minY = Math.Min(minY, i / 64); maxY = Math.Max(maxY, i / 64);
                }
            return maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
        }
    }

    public string TilePath(int x, int y) => $@"World\Maps\{Directory}\{Directory}_{x}_{y}.adt";
    public string WdtPath => $@"World\Maps\{Directory}\{Directory}.wdt";
    public string WdlPath => $@"World\Maps\{Directory}\{Directory}.wdl";
}

public sealed record ZoneInfo(int Id, string Name, int Parent, int Chunks, List<(int X, int Y)> Tiles)
{
    public string Label => $"{Name} ({Tiles.Count} tile{(Tiles.Count == 1 ? "" : "s")})";
}

/// <summary>
/// The maps of the game for the Maps tab: Map.dbc names and instance types, each map's WDT tiles, its zones (AreaTable)
/// and which tiles they cover (the chunk area ids the index stores per tile, u_area_chunk), and the minimap
/// (textures\Minimap\md5translate.trs: "&lt;map&gt;\mapX_Y.blp" -&gt; a hashed BLP).
/// </summary>
public sealed class MapCatalog
{
    private readonly ClientData _client;
    private readonly string? _indexPath;
    private Dictionary<string, string>? _minimap;
    private Dictionary<int, AreaRec>? _areas;
    private readonly object _gate = new();

    public List<MapInfo> Maps { get; } = new();

    public MapCatalog(ClientData client, string? indexPath)
    {
        _client = client;
        _indexPath = indexPath;
    }

    public static MapCatalog Load(ClientData client, string? indexPath)
    {
        var cat = new MapCatalog(client, indexPath);
        var dbc = client.Read(@"DBFilesClient\Map.dbc") ?? throw new InvalidOperationException("Map.dbc not found");
        var d = new DbcFile(dbc, "Map.dbc");
        d.RequireFields(5);
        for (int r = 0; r < d.Count; r++)
        {
            string dir = d.String(r, 1);
            if (dir.Length == 0) continue;
            var wdt = client.Read($@"World\Maps\{dir}\{dir}.wdt");
            if (wdt == null) continue;
            var w = new WdtFile(wdt);
            var tiles = new bool[4096];
            int n = 0;
            if (!w.IsGlobalWmo)
                foreach (var (x, y) in w.Tiles())
                {
                    tiles[y * 64 + x] = true;
                    n++;
                }
            cat.Maps.Add(new MapInfo
            {
                Id = d.Int(r, 0), Directory = dir, Name = d.LocString(r, 4), InstanceType = d.Int(r, 2), Tiles = tiles, TileCount = n,
                IsGlobalWmo = w.IsGlobalWmo, GlobalWmo = w.GlobalWmoName,
            });
        }
        // continents first, then the other world maps, then instances by kind; each by name
        cat.Maps.Sort((a, b) =>
        {
            int ka = a.InstanceType == 0 ? (a.TileCount >= 100 ? 0 : 1) : 1 + a.InstanceType, kb = b.InstanceType == 0 ? (b.TileCount >= 100 ? 0 : 1) : 1 + b.InstanceType;
            return ka != kb ? ka.CompareTo(kb) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return cat;
    }

    public MapInfo? ById(int id) => Maps.FirstOrDefault(m => m.Id == id);
    public MapInfo? ByDirectory(string dir) => Maps.FirstOrDefault(m => string.Equals(m.Directory, dir, StringComparison.OrdinalIgnoreCase));

    private Dictionary<int, AreaRec> Areas()
    {
        lock (_gate)
        {
            if (_areas != null) return _areas;
            var d = _client.Read(@"DBFilesClient\AreaTable.dbc");
            _areas = d == null ? new() : UsageDbc.AreaTable(new DbcFile(d, "AreaTable.dbc")).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            return _areas;
        }
    }

    public string AreaName(int id) => Areas().TryGetValue(id, out var a) ? a.Name : id == 0 ? "(no zone)" : $"area {id}";

    /// <summary>The zone (top-level area) of an area id.</summary>
    public int ZoneOf(int area)
    {
        var areas = Areas();
        for (int guard = 0; guard < 8 && areas.TryGetValue(area, out var a) && a.Parent != 0; guard++) area = a.Parent;
        return area;
    }

    private SqliteConnection? OpenIndex()
    {
        if (_indexPath == null || !File.Exists(_indexPath)) return null;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    /// <summary>The 256 chunk area ids of every tile of a map (from the index; read from the ADTs when the index has
    /// none).</summary>
    public Dictionary<(int X, int Y), int[]> TileAreas(MapInfo map)
    {
        var result = new Dictionary<(int, int), int[]>();
        try
        {
            using var db = OpenIndex();
            if (db != null)
            {
                using var c = db.CreateCommand();
                c.CommandText = "SELECT tx, ty, areas FROM u_area_chunk WHERE map=$m";
                c.Parameters.AddWithValue("$m", map.Id);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var blob = (byte[])r.GetValue(2);
                    var a = new int[256];   // UsageIndex stores the 256 area ids as uint16
                    for (int i = 0; i < 256 && i * 2 + 2 <= blob.Length; i++) a[i] = BitConverter.ToUInt16(blob, i * 2);
                    result[(r.GetInt32(0), r.GetInt32(1))] = a;
                }
            }
        }
        catch (SqliteException) { }
        return result;
    }

    /// <summary>The zones of a map with the tiles they cover (most chunks first).</summary>
    public List<ZoneInfo> Zones(MapInfo map, Dictionary<(int X, int Y), int[]>? tileAreas = null)
    {
        tileAreas ??= TileAreas(map);
        var chunks = new Dictionary<int, int>();
        var tiles = new Dictionary<int, HashSet<(int, int)>>();
        foreach (var (xy, areas) in tileAreas)
            foreach (var a in areas)
            {
                if (a == 0) continue;
                int z = ZoneOf(a);
                chunks[z] = chunks.GetValueOrDefault(z) + 1;
                if (!tiles.TryGetValue(z, out var set)) tiles[z] = set = new HashSet<(int, int)>();
                set.Add(xy);
            }
        return chunks.Keys.Select(z => new ZoneInfo(z, AreaName(z), Areas().TryGetValue(z, out var a) ? a.Parent : 0, chunks[z],
                tiles[z].OrderBy(t => t.Item2).ThenBy(t => t.Item1).ToList()))
            .OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The zones and subzones of one tile with their share of its 256 chunks, biggest first.</summary>
    public List<(int Area, string Name, int Chunks)> TileZones(int[]? areas)
    {
        if (areas == null) return new();
        return areas.Where(a => a != 0).GroupBy(a => a).Select(g => (g.Key, ZoneName(g.Key), g.Count())).OrderByDescending(t => t.Item3).ToList();
    }

    private string ZoneName(int area)
    {
        int z = ZoneOf(area);
        return z == area ? AreaName(area) : $"{AreaName(area)} ({AreaName(z)})";
    }

    // ------------------------------------------------------------------ minimap

    private Dictionary<string, string> MinimapTable()
    {
        lock (_gate)
        {
            if (_minimap != null) return _minimap;
            _minimap = ParseMd5Translate(_client.Read(@"textures\Minimap\md5translate.trs"));
            return _minimap;
        }
    }

    /// <summary>md5translate.trs: lines "dir: &lt;folder&gt;" and "&lt;folder&gt;\mapX_Y.blp&lt;TAB&gt;&lt;hash&gt;.blp".
    /// Returns {UPPER "&lt;folder&gt;\mapX_Y.blp": "&lt;hash&gt;.blp"}.</summary>
    public static Dictionary<string, string> ParseMd5Translate(byte[]? data)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data == null) return d;
        foreach (var raw in Encoding.Latin1.GetString(data).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("dir:", StringComparison.OrdinalIgnoreCase)) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            d[line[..tab].Trim()] = line[(tab + 1)..].Trim();
        }
        return d;
    }

    /// <summary>The game path of a tile's minimap texture, or null.</summary>
    public string? MinimapPath(MapInfo map, int x, int y) =>
        MinimapTable().TryGetValue($@"{map.Directory}\map{x:D2}_{y:D2}.blp", out var h) ? $@"textures\Minimap\{h}" : null;

    /// <summary>The minimap picture of one tile (256 x 256), or null.</summary>
    public RgbaImage? MinimapTile(MapInfo map, int x, int y)
    {
        var p = MinimapPath(map, x, y);
        var data = p == null ? null : _client.Read(p);
        if (data == null) return null;
        try { return BlpFile.Parse(data).Decode(0); }
        catch (BlpException) { return null; }
    }

    /// <summary>The overview of a map: its minimap tiles at <paramref name="cell"/> pixels each over the tiles' bounding
    /// box (one tile of margin), tiles without a minimap in dark grey. Cached as PNG in <paramref name="cacheDir"/>,
    /// keyed by the archives' fingerprint.</summary>
    public MinimapOverview Overview(MapInfo map, int cell, string? cacheDir, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var (minX, minY, maxX, maxY) = map.Bounds;
        minX = Math.Max(0, minX - 1); minY = Math.Max(0, minY - 1);
        maxX = Math.Min(63, maxX + 1); maxY = Math.Min(63, maxY + 1);
        int w = (maxX - minX + 1) * cell, h = (maxY - minY + 1) * cell;
        string? cacheFile = cacheDir == null ? null : Path.Combine(cacheDir, $"{map.Directory}_{cell}.png");
        string key = $"{_client.Fingerprint()}|{minX},{minY},{maxX},{maxY}|{cell}|v1";
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
            var tile = MinimapTile(map, t.X, t.Y);
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

    // ------------------------------------------------------------------ tile report ("Used in game" style)

    /// <summary>What is placed on a tile, as a "Used in game" report: zones, the doodads and buildings placed in the
    /// file (each name links to the world object in the Browse list), and creature spawns when the server part of the
    /// index is there.</summary>
    public UsageReport TileReport(MapInfo map, int x, int y, TerrainTile? tile, int[]? areas, Func<string, bool>? isEntry = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = new UsageReport { Key = $"tile:{map.Id}:{x}:{y}" };
        rep.Headline.Add($"{map.Name} tile {x},{y}", bold: true).Muted($"  {map.TilePath(x, y)}");
        var zones = TileZones(areas);
        var zs = rep.Add("zones", "Zones on this tile");
        zs.Summary = zones.Count == 0 ? "no zone ids" : $"{zones.Count} zone{(zones.Count == 1 ? "" : "s")}/subzone{(zones.Count == 1 ? "" : "s")}";
        foreach (var (area, name, chunks) in zones) zs.Line().Add(name).Muted($"  {chunks} of 256 chunks ({chunks * 100 / 256}%)");
        if (tile != null)
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
            int water = tile.AllChunks.Count(c => c.Liquids.Count > 0), holes = tile.AllChunks.Count(c => c.Holes != 0);
            var (lo, hi) = tile.HeightRange;
            var tr = rep.Add("terrain", "Terrain");
            tr.Summary = $"heights {lo:0} to {hi:0} yards";
            tr.Line().Add($"{tile.AllChunks.Count()} chunks, heights {lo:0.#} to {hi:0.#} yards, {tile.AllChunks.Sum(c => c.Layers.Count)} texture layers");
            tr.Line().Add(water > 0 ? $"water or other liquid in {water} chunks ({string.Join(", ", tile.AllChunks.SelectMany(c => c.Liquids).Select(l => l.Kind).Distinct())})" : "no water");
            tr.Line().Add(holes > 0 ? $"holes in {holes} chunks (caves, building entrances)" : "no holes");
            tr.Line().Muted($"file size {tile.FileSize / 1024.0:0} KB");
        }
        // creature spawns on the tile (server part of the index)
        try
        {
            using var db = OpenIndex();
            if (db != null)
            {
                using var c = db.CreateCommand();
                c.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='u_spawn'";
                if (c.ExecuteScalar() != null)
                {
                    float xMax = TerrainMath.MapHalf - y * TerrainMath.TileSize, yMax = TerrainMath.MapHalf - x * TerrainMath.TileSize;
                    c.CommandText = "SELECT s.kind, s.entry, COUNT(*), e.name FROM u_spawn s LEFT JOIN entry e ON e.key = (CASE s.kind WHEN 'creature' THEN 'creature:' ELSE 'go:' END) || s.entry " +
                                    "WHERE s.map=$m AND s.x <= $x1 AND s.x > $x0 AND s.y <= $y1 AND s.y > $y0 GROUP BY s.kind, s.entry ORDER BY COUNT(*) DESC LIMIT 30";
                    c.Parameters.AddWithValue("$m", map.Id);
                    c.Parameters.AddWithValue("$x1", xMax);
                    c.Parameters.AddWithValue("$x0", xMax - TerrainMath.TileSize);
                    c.Parameters.AddWithValue("$y1", yMax);
                    c.Parameters.AddWithValue("$y0", yMax - TerrainMath.TileSize);
                    var rows = new List<(string Kind, long Entry, long N, string? Name)>();
                    try
                    {
                        using var r = c.ExecuteReader();
                        while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)));
                    }
                    catch (SqliteException) { }
                    if (rows.Count > 0)
                    {
                        var sp = rep.Add("spawns", "Spawns on this tile (server)");
                        sp.Summary = $"{rows.Sum(r => r.N)} spawns of {rows.Count}+ creatures and objects";
                        foreach (var r in rows)
                        {
                            var key = (r.Kind == "creature" ? "creature:" : "go:") + r.Entry;
                            var l = sp.Line();
                            var label = r.Name ?? $"{r.Kind} {r.Entry}";
                            if (isEntry?.Invoke(key) == true) l.Add(label, key); else l.Add(label);
                            l.Muted($"  x{r.N}");
                        }
                    }
                }
            }
        }
        catch (SqliteException) { }
        rep.Milliseconds = sw.Elapsed.TotalMilliseconds;
        rep.Short = tile == null ? "" : $" · {tile.Doodads.Count} doodads · {tile.Wmos.Count} buildings";
        return rep;
    }
}

/// <summary>A map's minimap overview: the picture and which tile each cell is.</summary>
public sealed record MinimapOverview(MapInfo Map, RgbaImage Image, int MinX, int MinY, int Cell, bool FromCache)
{
    public int Columns => Image.Width / Cell;
    public int Rows => Image.Height / Cell;

    /// <summary>The tile under a pixel of the picture, or null.</summary>
    public (int X, int Y)? TileAt(double px, double py)
    {
        if (px < 0 || py < 0 || px >= Image.Width || py >= Image.Height) return null;
        return (MinX + (int)(px / Cell), MinY + (int)(py / Cell));
    }
}
