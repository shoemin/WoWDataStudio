using WoWData.Core.Dbc;
using WoWData.Core.Imaging;
using WoWData.Core.Session;
using WoWData.Core.Usage;
using WoWData.Core.Workspace;

namespace WoWData.Core.Maps;

/// <summary>A tile as the Maps tab describes it: its file, the facts under its name and "On this tile".</summary>
public sealed record TileSummary(MapInfo Map, int X, int Y, string Path, TerrainTile? Tile, List<string> Facts, IReadOnlyList<(int Area, string Name, int Chunks)> Zones,
    bool Modified, UsageReport Report);

/// <summary>"Edit in Noggit": the project that was written and how many earlier imports were copied into it.</summary>
public sealed record NoggitPrepared(NoggitLaunchInfo Info, string? NoggitExe, int Synced, bool AlreadyRunning);

/// <summary>
/// 0.8.0 (moved from the Maps tab's view models into Core): the operations of the Maps tab that the desktop app and the
/// command line (<c>wds maps ...</c>) share: a tile's facts and report, its breakdown, the tiles Noggit saved that are not
/// imported yet, preparing a Noggit project, the terrain export for Blender, the terrain and Noggit imports, and the tile
/// comparison pictures.
/// </summary>
public static class MapOperations
{
    /// <summary>The maps the Maps tab lists (with terrain tiles, or a single building; M4: another version's maps whose
    /// tiles cannot be read are listed with the reason).</summary>
    public static List<MapInfo> ListedMaps(IMapView cat) => cat.Maps.Where(m => m.TileCount > 0 || m.IsGlobalWmo || m.Note != null).ToList();

    /// <summary>A map by its folder name ("Azeroth"), its id ("0") or its name ("Eastern Kingdoms"), or null.</summary>
    public static MapInfo? Find(IMapView cat, string text)
    {
        if (int.TryParse(text, out var id) && cat.ById(id) is { } byId) return byId;
        return cat.ByDirectory(text) ?? cat.Maps.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The facts and the "On this tile" report of a tile (the Maps tab's tile details).</summary>
    public static TileSummary Summarize(AppSession session, MapInfo map, int x, int y)
    {
        var cat = session.Maps ?? throw new InvalidOperationException("No game folder is open.");
        var path = map.TilePath(x, y);
        var tile = cat.LoadTerrain(map, x, y);
        int[]? areas = tile != null ? tile.Chunks.Select(c => c?.AreaId ?? 0).ToArray() : cat.TileAreas(map).GetValueOrDefault((x, y));
        var report = cat.TileReport(map, x, y, tile, areas, k => session.FindEntry(k) != null);
        bool modified = session.Workspace?.IsModified(path) == true;
        var zones = cat.TileZones(areas);
        var facts = new List<string>();
        if (zones.Count > 0) facts.Add("Zone: " + string.Join(", ", zones.Take(3).Select(z => z.Name)) + (zones.Count > 3 ? $" and {zones.Count - 3} more" : ""));
        if (tile != null)
        {
            var (lo, hi) = tile.HeightRange;
            facts.Add($"{tile.FileSize / 1024.0:0} KB, 256 chunks of 33 x 33 yards (the tile is 533 x 533 yards); heights {lo:0} to {hi:0} yards");
            facts.Add($"{tile.Doodads.Count} doodads ({tile.Doodads.Select(d => d.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()} models), {tile.Wmos.Count} buildings (WMO), {tile.Textures.Count} ground textures");
            int water = tile.AllChunks.Count(c => c.Liquids.Count > 0);
            if (water > 0) facts.Add($"Water or other liquid in {water} of 256 chunks");
        }
        else facts.Add("The tile file could not be read.");
        if (tile != null) facts.AddRange(tile.Notes);
        if (modified) facts.Add("Modified: your workspace has an edited version of this tile (the views show it).");
        return new TileSummary(map, x, y, path, tile, facts, zones, modified, report);
    }

    /// <summary>"Break down this tile": every piece of the tile, as the app has it now.</summary>
    public static TileBreakdown Breakdown(AppSession session, MapInfo map, int x, int y)
    {
        var path = map.TilePath(x, y);
        var data = session.ReadCurrent(path) ?? throw new IOException($"{path} could not be read.");
        var cat = session.MapCatalog;
        Dictionary<int, string>? sounds = null;
        string? SoundName(int id)
        {
            if (sounds == null)
            {
                sounds = new Dictionary<int, string>();
                try
                {
                    var bytes = session.Data?.Read(@"DBFilesClient\SoundEntries.dbc");
                    if (bytes != null)
                    {
                        var dbc = new DbcFile(bytes, "SoundEntries.dbc");
                        for (int r = 0; r < dbc.Count; r++) sounds[dbc.Int(r, 0)] = dbc.String(r, 2);
                    }
                }
                catch (DbcException) { }
            }
            return sounds.GetValueOrDefault(id);
        }
        return TileBreakdown.Build(map.Name, map.Directory, x, y, data, session.ReadCurrent, a => cat?.AreaName(a) ?? $"area {a}", SoundName, session.FileExists);
    }

    /// <summary>The tiles Noggit saved for a map that differ from what the app has (not imported yet).</summary>
    public static List<(int X, int Y)> PendingTiles(AppSession session, MapInfo map)
    {
        var l = new List<(int X, int Y)>();
        var noggit = session.Noggit;
        if (noggit == null) return l;
        var dir = noggit.SaveDir(map);
        if (!Directory.Exists(dir)) return l;
        foreach (var f in Directory.EnumerateFiles(dir, "*.adt"))
        {
            var xy = MapValidator.TileXY(f);
            if (xy == null) continue;
            var have = session.ReadCurrent(map.TilePath(xy.Value.X, xy.Value.Y));
            if (have == null || !have.AsSpan().SequenceEqual(File.ReadAllBytes(f))) l.Add((xy.Value.X, xy.Value.Y));
        }
        return l;
    }

    /// <summary>The changed tiles of a map in the workspace (the orange frames of the minimap).</summary>
    public static List<(int X, int Y)> ChangedTiles(Workspace.Workspace ws, MapInfo map)
    {
        var prefix = $@"WORLD\MAPS\{map.Directory.ToUpperInvariant()}\";
        return ws.ModifiedPaths().Where(p => p.ToUpperInvariant().StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith(".adt", StringComparison.OrdinalIgnoreCase))
            .Select(MapValidator.TileXY).Where(t => t != null).Select(t => (t!.Value.X, t.Value.Y)).ToList();
    }

    /// <summary>"Edit in Noggit": the client view (links to the game's archives; a copy only with
    /// <paramref name="allowCopy"/>), the project with a bookmark at the tile, and the earlier imports copied into it.
    /// Noggit itself is not started here.</summary>
    public static NoggitPrepared PrepareNoggit(AppSession session, MapInfo map, int x, int y, bool allowCopy)
    {
        var noggit = session.Noggit ?? throw new NoggitException("No game folder is open.");
        if (map.IsGlobalWmo) throw new NoggitException($"{map.Name} is a single building without terrain; Noggit edits terrain maps.");
        var exe = NoggitService.FindNoggit(session.Settings, session.Client?.ClientDir);
        var client = noggit.PrepareClient(allowCopy);
        TerrainTile? tile = null;
        if (map.HasTile(x, y) && session.ReadCurrent(map.TilePath(x, y)) is { } data) tile = TerrainTile.Load(data, map.Directory, x, y);
        float height = tile?.HeightAt(tile.Corner.X - TerrainMath.TileSize / 2, tile.Corner.Y - TerrainMath.TileSize / 2) ?? 100f;
        var zone = session.MapCatalog?.TileZones(tile?.Chunks.Select(c => c?.AreaId ?? 0).ToArray()).FirstOrDefault().Name;
        var info = noggit.WriteProject(map, x, y, height, zone, client.ClientDir, client.Note);
        int synced = noggit.SyncFromWorkspace(map, session.Workspace!);
        return new NoggitPrepared(info, exe, synced, NoggitService.IsRunning());
    }

    /// <summary>"Import map changes": what Noggit saved for the map compared with what the app has now.</summary>
    public static MapImportAnalysis AnalyzeNoggit(AppSession session, MapInfo map)
    {
        var noggit = session.Noggit ?? throw new WorkspaceException("No game folder is open.");
        return MapImporter.Analyze(map, noggit.SaveDir(map), session.Data!, session.Workspace!);
    }

    /// <summary>"Import edited terrain": the heights Blender exported (with <paramref name="sync"/>, mismatching shared edges
    /// are copied from the edited side).</summary>
    public static MapImportAnalysis AnalyzeTerrain(AppSession session, string path, bool sync) =>
        TerrainImporter.Analyze(path, d => session.MapCatalog?.ByDirectory(d), session.Data!, session.Workspace!, sync);

    /// <summary>The text of the seam question (the app's dialog and the command line's refusal).</summary>
    public static string SeamText(MapImportAnalysis a) =>
        "The heights on these shared edges differ between two tiles. In the game the ground would have a crack (a gap) there:\n\n" + string.Join("\n", a.Seams.Select(s => "• " + s.Text)) +
        "\n\nSync them: the heights of the edited side are copied onto the other tile's edge (a neighbour you did not edit is then saved too). Or cancel and fix it in Blender (\"Sync shared edges\").";

    /// <summary>"Open terrain in Blender": the package request for the chosen tiles.</summary>
    public static TerrainExportRequest TerrainExport(AppSession session, MapInfo map, IReadOnlyCollection<(int X, int Y)> tiles, (int X, int Y) center, string parentFolder, bool references)
    {
        var res = session.Models ?? throw new InvalidOperationException("No game folder is open.");
        return new TerrainExportRequest
        {
            Map = map, Tiles = tiles.ToList(), Center = center, ReadCurrent = p => session.ReadCurrent(p), ReadGame = p => session.Data?.Read(p),
            IsModified = p => session.Workspace?.IsModified(p) == true, ArchiveOf = p => session.Data?.Find(p)?.Name, LoadTexture = res.LoadBlp,
            Exists = session.FileExists, SpecularGround = session.SpecularGround, IncludeReferences = references,
            PackageFolder = TerrainExporter.NewPackageFolder(parentFolder, map, center, tiles.Count),
        };
    }

    /// <summary>The words of the terrain export window about the ground textures.</summary>
    public static string GroundExportText(AppSession session) =>
        "Ground textures: both files of each texture are written (the base file and its _s specular variant); the materials show " +
        (session.SpecularGround ? "the _s files" : "the base files") + ". " + session.GroundRuleText + " (Maps tab: \"Specular ground (_s textures)\" changes it.)";

    /// <summary>Before and after of a map file: the change in words and, for a tile, the height difference seen from above
    /// (grey unchanged, red raised, blue lowered) or the relief of a new tile.</summary>
    public static (MapDiffResult? Diff, RgbaImage? Picture, string Note) Compare(byte[]? before, byte[] after, string path, string leftLabel, string rightLabel)
    {
        if (MapValidator.KindOf(path) != "adt")
        {
            var d = before != null ? MapDiff.DiffBytes(before, after, path) : null;
            return (d, null, d == null ? "A new file: the game has none to compare with." : d.PlainSummary() + ".");
        }
        var xy = MapValidator.TileXY(path) ?? throw new ArgumentException($"{path} is not a tile file.");
        var dir = path.Split('\\').ElementAtOrDefault(2) ?? "";
        var b = before != null ? TerrainTile.Load(before, dir, xy.X, xy.Y) : null;
        var a = TerrainTile.Load(after, dir, xy.X, xy.Y);
        var diff = before != null ? MapDiff.DiffAdt(before, after, leftLabel, rightLabel) : null;
        var img = b != null ? TerrainTile.HeightDifference(b, a, 512, out _, out _) : a.Relief(512);
        return (diff, img, b == null ? "The game has no such tile: a new tile." : diff?.PlainSummary() + ".");
    }
}
