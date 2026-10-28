using WoWData.Core.Blp;
using WoWData.Core.Casc;
using WoWData.Core.Imaging;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Usage;

namespace WoWData.Core.Maps;

/// <summary>
/// M4 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 3.5): the maps of a modern client opened read only, for the Maps
/// tab and "wds maps ... --version", over the map converter's reader (<see cref="Casc160MapSource"/>):
/// <list type="bullet">
/// <item>maps from the Map table (its readable rows; the locked ones are counted), tiles from each WDT's MAIN/MAID;</item>
/// <item>zones from AreaTable and the chunk area ids of each tile's root file (<see cref="IMapSource.ChunkAreas"/>),
///   cached per map in the client's app folder;</item>
/// <item>minimaps from the MAID minimap FileDataIDs (512 x 512 on 1.60; the overview scales them);</item>
/// <item>a tile as terrain: its split files read one by one (a locked obj0 or tex0 only takes its own part away and says
///   so), <see cref="Adt160Reader"/> into the neutral model, <see cref="TerrainTileFromNeutral"/> into the
///   <see cref="TerrainTile"/> the 3D view draws;</item>
/// <item>"On this tile": the zones and the placements (no server, no "Used in game").</item>
/// </list>
/// A WDT, tile file or minimap that is encrypted (no key) or not on this computer is reported, never guessed.
/// </summary>
public sealed class MapCatalog160 : IMapView
{
    private readonly Casc160MapSource _src;
    private readonly CascStorage _st;
    private readonly Func<uint, string, string> _nameOf;
    private readonly string? _cacheDir;
    private readonly string _fingerprint;
    private readonly Dictionary<int, (SourceMap Source, NeutralMap? Wdt)> _maps = new();
    private readonly Dictionary<int, Dictionary<(int X, int Y), int[]>> _tileAreas = new();
    private readonly object _gate = new();

    public List<MapInfo> Maps { get; } = new();
    /// <summary>Rows of the Map table that are readable / locked (encrypted, no key).</summary>
    public int MapRows { get; private set; }
    public int LockedMapRows { get; private set; }
    /// <summary>Rows of AreaTable that are readable / locked.</summary>
    public int AreaRows { get; private set; }
    public int LockedAreaRows { get; private set; }
    /// <summary>Maps whose WDT cannot be read (encrypted or not on this computer).</summary>
    public int UnreadableWdts => Maps.Count(m => m.Note != null);

    /// <summary>The locked rows and unreadable maps in words (the Maps tab's status, "wds maps list --version").</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string> { $"Map table: {MapRows} readable rows" + (LockedMapRows > 0 ? $", {LockedMapRows} locked (encrypted, no key)" : "") };
            parts.Add($"AreaTable: {AreaRows:N0} readable" + (LockedAreaRows > 0 ? $", {LockedAreaRows} locked" : ""));
            if (UnreadableWdts > 0) parts.Add($"{UnreadableWdts} map{(UnreadableWdts == 1 ? "" : "s")} without a readable WDT");
            return string.Join("; ", parts) + ".";
        }
    }
    public Casc160MapSource Source => _src;

    private MapCatalog160(Casc160MapSource src, Func<uint, string, string> nameOf, string? cacheDir)
    {
        _src = src;
        _st = src.Client.Casc!;
        _nameOf = nameOf;
        _cacheDir = cacheDir;
        _fingerprint = $"{_st.BuildName}|{_st.Build}|{_st.Root.Count}";
    }

    /// <param name="nameOf">FileDataID and extension -> the name the session reads the file by (its known name, else "#id.ext")</param>
    /// <param name="cacheDir">where the minimap overviews and the tiles' area ids are cached (the client's app folder), or null</param>
    public static MapCatalog160 Load(Casc160MapSource src, Func<uint, string, string> nameOf, string? cacheDir)
    {
        var cat = new MapCatalog160(src, nameOf, cacheDir);
        var maps = src.Maps();
        cat.MapRows = maps.Count;
        cat.LockedMapRows = Locked(src, "Map");
        cat.AreaRows = src.Areas().Count;
        cat.LockedAreaRows = Locked(src, "AreaTable");
        foreach (var m in maps)
        {
            if (m.WdtFileDataId == 0) continue;
            NeutralMap? wdt = null;
            string? note = null;
            try { wdt = src.ReadWdt(m); }
            catch (CascEncryptedException e) { note = $"its WDT (#{m.WdtFileDataId}) is encrypted{Key(e)}: no tiles to show"; }
            catch (CascMissingException) { note = $"its WDT (#{m.WdtFileDataId}) is not on this computer: no tiles to show"; }
            catch (InvalidDataException e) { note = e.Message.Contains("is not in", StringComparison.Ordinal) ? $"its WDT (#{m.WdtFileDataId}) is not in this client: no tiles to show" : "its WDT cannot be read: " + e.Message; }
            var tiles = new bool[4096];
            int n = 0;
            if (wdt != null && !wdt.IsWmoOnly)
                foreach (var (x, y) in wdt.ExistingTiles())
                    if (wdt.Tile(x, y).Maid is { } maid && maid[0] != 0)
                    {
                        tiles[y * 64 + x] = true;
                        n++;
                    }
            string? global = wdt?.GlobalWmo?.Model is { } gm ? gm.FileDataId is { } gf and > 0 ? nameOf(gf, ".wmo") : gm.Path : null;
            cat.Maps.Add(new MapInfo
            {
                Id = m.Id, Directory = m.Directory, Name = m.Name.Length > 0 ? m.Name : m.Directory, InstanceType = m.InstanceType, Tiles = tiles, TileCount = n,
                IsGlobalWmo = wdt?.IsWmoOnly == true, GlobalWmo = global, Note = note,
            });
            cat._maps[m.Id] = (m, wdt);
        }
        // the 1.12 order: continents first, then the other world maps, then instances by kind; each by name
        cat.Maps.Sort((a, b) =>
        {
            int ka = a.InstanceType == 0 ? (a.TileCount >= 100 ? 0 : 1) : 1 + a.InstanceType, kb = b.InstanceType == 0 ? (b.TileCount >= 100 ? 0 : 1) : 1 + b.InstanceType;
            return ka != kb ? ka.CompareTo(kb) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return cat;
    }

    private static string Key(CascEncryptedException e) => e.KeyName != null ? $" (key {e.KeyName})" : "";

    private static int Locked(Casc160MapSource src, string table)
    {
        try { return src.Client.Catalog.Open(src.Client.Casc!, table)?.SkippedIds.Count ?? 0; }
        catch (Exception e) when (e is IOException or InvalidDataException or CascMissingException or CascEncryptedException) { return 0; }
    }

    public MapInfo? ById(int id) => Maps.FirstOrDefault(m => m.Id == id);
    public MapInfo? ByDirectory(string dir) => Maps.FirstOrDefault(m => string.Equals(m.Directory, dir, StringComparison.OrdinalIgnoreCase));

    /// <summary>The map's WDT as read (null when it could not be).</summary>
    public NeutralMap? Wdt(MapInfo map) => _maps.TryGetValue(map.Id, out var e) ? e.Wdt : null;

    public string AreaName(int id) => _src.Areas().TryGetValue(id, out var a) && a.Name.Length > 0 ? a.Name : id == 0 ? "(no zone)" : $"area {id}";

    public int ZoneOf(int area)
    {
        var areas = _src.Areas();
        for (int guard = 0; guard < 8 && areas.TryGetValue(area, out var a) && a.ParentId != 0; guard++) area = a.ParentId;
        return area;
    }

    /// <summary>The 256 chunk area ids of every readable tile (the root files' MCNK headers), cached per map.</summary>
    public Dictionary<(int X, int Y), int[]> TileAreas(MapInfo map)
    {
        lock (_gate)
            if (_tileAreas.TryGetValue(map.Id, out var have)) return have;
        var result = new Dictionary<(int X, int Y), int[]>();
        if (_maps.TryGetValue(map.Id, out var e) && e.Wdt is { } wdt)
        {
            string? cache = _cacheDir == null ? null : Path.Combine(_cacheDir, $"{map.Directory}_areas.bin");
            if (cache == null || !TryLoadAreas(cache, result))
            {
                var list = new List<(int X, int Y)>();
                for (int i = 0; i < 4096; i++) if (map.Tiles[i]) list.Add((i % 64, i / 64));
                var found = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), int[]>();
                Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, t =>
                {
                    try
                    {
                        if (_src.ChunkAreas(e.Source, wdt, t.X, t.Y) is { } a) found[t] = a;
                    }
                    catch (Exception ex) when (ex is CascEncryptedException or CascMissingException or InvalidDataException) { }
                });
                foreach (var (k, v) in found) result[k] = v;
                if (cache != null) SaveAreas(cache, result);
            }
        }
        lock (_gate) _tileAreas[map.Id] = result;
        return result;
    }

    private bool TryLoadAreas(string file, Dictionary<(int X, int Y), int[]> into)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadString() != "areas v1|" + _fingerprint) return false;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int x = r.ReadByte(), y = r.ReadByte();
                var a = new int[256];
                for (int k = 0; k < 256; k++) a[k] = r.ReadInt32();
                into[(x, y)] = a;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            into.Clear();
            return false;
        }
    }

    private void SaveAreas(string file, Dictionary<(int X, int Y), int[]> areas)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var w = new BinaryWriter(File.Create(file + ".tmp")))
            {
                w.Write("areas v1|" + _fingerprint);
                w.Write(areas.Count);
                foreach (var ((x, y), a) in areas)
                {
                    w.Write((byte)x);
                    w.Write((byte)y);
                    foreach (var v in a) w.Write(v);
                }
            }
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public List<ZoneInfo> Zones(MapInfo map, Dictionary<(int X, int Y), int[]>? tileAreas = null) =>
        MapViews.Zones(this, tileAreas ?? TileAreas(map), z => _src.Areas().TryGetValue(z, out var a) ? a.ParentId : 0);

    public List<(int Area, string Name, int Chunks)> TileZones(int[]? areas) => MapViews.TileZones(this, areas);

    /// <summary>The minimap picture of one tile (512 x 512 on 1.60), or null (none, encrypted or not on this computer).</summary>
    public RgbaImage? MinimapTile(MapInfo map, int x, int y)
    {
        if (Wdt(map) is not { } wdt || !map.HasTile(x, y)) return null;
        try
        {
            var data = _src.ReadMinimap(wdt, x, y);
            return data == null ? null : BlpFile.Parse(data).Decode(0);
        }
        catch (Exception e) when (e is BlpException or CascEncryptedException or CascMissingException or InvalidDataException or IndexOutOfRangeException) { return null; }
    }

    public MinimapOverview Overview(MapInfo map, int cell, string? cacheDir, Action<int, int>? progress = null, CancellationToken ct = default) =>
        MapViews.Overview(map, cell, cacheDir, _fingerprint, (x, y) => MinimapTile(map, x, y), progress, ct);

    /// <summary>The MAID FileDataIDs of a tile (root, obj0, obj1, tex0, lod, map texture, map normal texture, minimap), or null.</summary>
    public uint[]? TileFiles(MapInfo map, int x, int y) => map.HasTile(x, y) ? Wdt(map)?.Tile(x, y).Maid : null;

    /// <summary>A tile as the neutral model (its root, obj0 and tex0 read one by one; <paramref name="notes"/> gets what
    /// could not be read, <paramref name="bytes"/> the bytes read). Throws <see cref="IOException"/> when the root file
    /// cannot be read. Null when the map has no such tile.</summary>
    public NeutralTile? ReadNeutral(MapInfo map, int x, int y, List<string> notes, out int bytes)
    {
        bytes = 0;
        if (Wdt(map) is not { } wdt || TileFiles(map, x, y) is not { } maid) return null;
        int read = 0;
        byte[]? Get(int slot, string what, string lost)
        {
            if (maid[slot] == 0) return null;
            try
            {
                var b = _st.ReadFileDataId(maid[slot]);
                read += b?.Length ?? 0;
                if (b == null) notes.Add($"The {what} file (#{maid[slot]}) is not in this client: {lost} are not shown.");
                return b;
            }
            catch (CascEncryptedException e)
            {
                if (slot == 0) throw new IOException($"{map.Directory}_{x}_{y}: its root file (#{maid[slot]}) is encrypted{Key(e)} and cannot be read.", e);
                notes.Add($"The {what} file (#{maid[slot]}) is encrypted{Key(e)}: {lost} are not shown.");
            }
            catch (CascMissingException e)
            {
                if (slot == 0) throw new IOException($"{map.Directory}_{x}_{y}: its root file (#{maid[slot]}) is not on this computer.", e);
                notes.Add($"The {what} file (#{maid[slot]}) is not on this computer: {lost} are not shown.");
            }
            return null;
        }
        var files = new Adt160Files { Maid = maid };
        files.Root = Get(0, "root", "the ground");
        if (files.Root == null) return null;
        files.Tex0 = Get(3, "texture (tex0)", "its ground textures");
        files.Obj0 = Get(1, "placement (obj0)", "its doodads and buildings");
        bytes = read;
        return Adt160Reader.ReadTile(files, map.Directory, x, y, Adt160Reader.BigAlpha(wdt.Flags), _src.Names);
    }

    public TerrainTile? LoadTerrain(MapInfo map, int x, int y)
    {
        var notes = new List<string>();
        var n = ReadNeutral(map, x, y, notes, out int bytes);
        if (n == null) return null;
        var t = TerrainTileFromNeutral.Convert(n, lt => _src.LiquidKindOf(lt), _nameOf, bytes);
        t.Notes.InsertRange(0, notes);
        return t;
    }

    /// <summary>"On this tile" of another version: zones and placements (no server, no "Used in game").</summary>
    public UsageReport TileReport(MapInfo map, int x, int y, TerrainTile? tile, int[]? areas, Func<string, bool>? isEntry = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = new UsageReport { Key = $"tile:{map.Id}:{x}:{y}" };
        var root = TileFiles(map, x, y)?[0] ?? 0;
        rep.Headline.Add($"{map.Name} tile {x},{y}", bold: true).Muted($"  {map.TilePath(x, y)}" + (root != 0 ? $" (#{root})" : ""));
        MapViews.AddZones(rep, TileZones(areas));
        if (tile != null)
        {
            MapViews.AddPlacements(rep, tile, isEntry);
            if (tile.Notes.Count > 0)
            {
                var nt = rep.Add("notes", "Not shown or approximated");
                nt.Summary = $"{tile.Notes.Count} note{(tile.Notes.Count == 1 ? "" : "s")}";
                foreach (var s in tile.Notes) nt.Line().Add(s);
            }
        }
        rep.Milliseconds = sw.Elapsed.TotalMilliseconds;
        rep.Short = tile == null ? "" : $" · {tile.Doodads.Count} doodads · {tile.Wmos.Count} buildings";
        return rep;
    }
}

/// <summary>A session's file names (<see cref="Versions.IModernNames"/>) as the map converter's names.</summary>
public sealed class ModernAssetNames : IAssetNames
{
    private readonly Versions.IModernNames _names;

    public ModernAssetNames(Versions.IModernNames names) => _names = names;

    public uint? FileDataIdOf(string path) => _names.ByPath(path)?.FileDataId;

    public string? PathOf(uint fileDataId) => _names.ByFileDataId(fileDataId) is { Source: not Versions.NameSource.Number } i && !i.Path.StartsWith('#') ? i.Path : null;
}
