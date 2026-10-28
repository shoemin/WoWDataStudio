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
    /// <summary>M4: why a map of another version shows no tiles (its WDT is encrypted or not on this computer), or null.</summary>
    public string? Note { get; init; }

    public string Kind => InstanceType switch { 1 => "Dungeon", 2 => "Raid", 3 => "Battleground", _ => TileCount >= 100 ? "Continent" : "World map" };
    public string Label => $"{Name} ({Directory})";
    public string Detail => Note ?? (IsGlobalWmo ? "a single building, no terrain tiles" : $"{TileCount} tile{(TileCount == 1 ? "" : "s")}");
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
public sealed class MapCatalog : IMapView
{
    private readonly ClientData _client;
    private readonly string? _indexPath;
    private Dictionary<string, string>? _minimap;
    private Dictionary<int, AreaRec>? _areas;
    private readonly object _gate = new();

    public List<MapInfo> Maps { get; } = new();

    /// <summary>M4: how <see cref="LoadTerrain"/> reads a tile file: the session sets your workspace's version first
    /// (<c>AppSession.ReadCurrent</c>); null = the game's archives.</summary>
    public Func<string, byte[]?>? ReadTile { get; set; }

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
    public List<ZoneInfo> Zones(MapInfo map, Dictionary<(int X, int Y), int[]>? tileAreas = null) =>
        MapViews.Zones(this, tileAreas ?? TileAreas(map), z => Areas().TryGetValue(z, out var a) ? a.Parent : 0);

    /// <summary>The zones and subzones of one tile with their share of its 256 chunks, biggest first.</summary>
    public List<(int Area, string Name, int Chunks)> TileZones(int[]? areas) => MapViews.TileZones(this, areas);

    /// <summary>M4 (design 2.2, today's code moved here): the tile file as the app has it (<see cref="ReadTile"/>) read as
    /// terrain; null when there is no such file.</summary>
    public TerrainTile? LoadTerrain(MapInfo map, int x, int y)
    {
        var data = (ReadTile ?? _client.Read)(map.TilePath(x, y));
        return data == null ? null : TerrainTile.Load(data, map.Directory, x, y);
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
    public MinimapOverview Overview(MapInfo map, int cell, string? cacheDir, Action<int, int>? progress = null, CancellationToken ct = default) =>
        MapViews.Overview(map, cell, cacheDir, _client.Fingerprint(), (x, y) => MinimapTile(map, x, y), progress, ct);

    // ------------------------------------------------------------------ tile report ("Used in game" style)

    /// <summary>What is placed on a tile, as a "Used in game" report: zones, the doodads and buildings placed in the
    /// file (each name links to the world object in the Browse list), and creature spawns when the server part of the
    /// index is there.</summary>
    public UsageReport TileReport(MapInfo map, int x, int y, TerrainTile? tile, int[]? areas, Func<string, bool>? isEntry = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = new UsageReport { Key = $"tile:{map.Id}:{x}:{y}" };
        rep.Headline.Add($"{map.Name} tile {x},{y}", bold: true).Muted($"  {map.TilePath(x, y)}");
        MapViews.AddZones(rep, TileZones(areas));
        if (tile != null) MapViews.AddPlacements(rep, tile, isEntry);
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
