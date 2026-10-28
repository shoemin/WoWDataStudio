using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace WoWData.Cli.Commands;

/// <summary>maps (the Maps tab, Noggit, terrain in Blender) and server-maps (the guided server update). M4: maps list /
/// zones / tiles / minimap / tile / render also read another version opened read only (--version, through
/// <see cref="IMapView"/>); the 1.12 output is unchanged.</summary>
internal static class MapCommands
{
    private static IMapView Catalog(AppSession s) => s.Maps ?? throw new CliError(ExitCode.Error, "The maps could not be read.");
    private static MapCatalog Catalog112(AppSession s) => s.MapCatalog ?? throw new CliError(ExitCode.Error, "The maps could not be read.");

    /// <summary>M4: a picture of another version never goes into its client's folder.</summary>
    private static string OutPng(AppSession s, Invocation inv)
    {
        var outFile = Path.GetFullPath(inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "give --out <file.png>."));
        if (s.Profile?.Copy is { } copy && (FindCommands.IsInside(outFile, copy.Folder) || (s.Files != null && FindCommands.IsInside(outFile, s.Files.Storage.DataDirectory))))
            throw new CliError(ExitCode.Refused, $"Refusing to write into the folder of {copy.Name}: other versions are only read. Save the picture somewhere else.");
        return outFile;
    }

    /// <summary>M4: a tile as terrain; a tile file another version cannot read is "not found" with its reason.</summary>
    private static TerrainTile? Terrain(AppSession s, MapInfo map, int x, int y)
    {
        try { return Catalog(s).LoadTerrain(map, x, y); }
        catch (IOException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    private static MapInfo Map(AppSession s, string text) =>
        MapOperations.Find(Catalog(s), text) ?? throw new CliError(ExitCode.NotFound, $"no map \"{text}\" (a folder name like Azeroth, an id like 0, or a name like \"Eastern Kingdoms\"; \"wds maps list\" lists them).");

    private static (MapInfo Map, int X, int Y) Tile(AppSession s, Invocation inv, int first = 0)
    {
        var map = Map(s, inv.Arg(first));
        int x = inv.Int(first + 1), y = inv.Int(first + 2);
        if (map.IsGlobalWmo) throw new CliError(ExitCode.NotFound, $"{map.Name} is a single building ({map.GlobalWmo}): it has no terrain tiles.");
        if (!map.HasTile(x, y)) throw new CliError(ExitCode.NotFound, $"{map.Name} has no tile {x},{y} (\"wds maps tiles {map.Directory}\" lists them).");
        return (map, x, y);
    }

    private static object MapJson(MapInfo m, Workspace? ws) => new
    {
        id = m.Id, directory = m.Directory, name = m.Name, kind = m.Kind, instanceType = m.InstanceType, tiles = m.TileCount, singleBuilding = m.IsGlobalWmo ? m.GlobalWmo : null,
        changedFiles = ws == null ? 0 : ChangeOperations.MapFiles(ws, m).Count,
    };

    /// <summary>The rows of a map import review (Noggit or Blender), with --exclude applied.</summary>
    private static CliResult Review(MapImportAnalysis a, Invocation inv, AppSession s, string heading)
    {
        foreach (var c in a.Changes)
            if (inv.Opts("exclude").Any(x => c.FileName.Contains(x, StringComparison.OrdinalIgnoreCase) || c.GamePath.Contains(x, StringComparison.OrdinalIgnoreCase))) c.Include = false;
        string Status(MapFileChange c) => c.Kind == null ? "Not imported" : c.HasErrors ? "Cannot be imported" : c.Unchanged ? "Unchanged" : c.Include ? "Will be saved" : "Left out";
        var r = new CliResult().Line(heading + (inv.DryRun ? " (dry run)" : ""));
        foreach (var g in a.General) r.Line("  " + g);
        foreach (var i in a.Ignored) r.Line("  ignored: " + i);
        foreach (var seam in a.SyncedSeams) r.Line("  synced: " + seam.Text);
        foreach (var c in a.Changes)
        {
            r.Line($"  [{Status(c)}] {c.FileName}{(c.Tile is { } t ? $" (tile {t.X},{t.Y})" : "")} - {(c.Kind == null ? "other file" : MapImporter.FormatOf(c.Kind))}: {c.Summary}");
            foreach (var p in c.Problems) r.Line("      " + p);
            foreach (var n in c.Notes) r.Line("      " + n);
        }
        ChangeSet? cs = null;
        int code = ExitCode.Ok;
        bool any = a.Changes.Any(c => c.CanInclude && c.Include);
        if (a.Changes.Count == 0 || a.NothingChanged) r.Line("No changes found: nothing will be saved.");
        else if (!any)
        {
            r.Line("Nothing is selected that can be imported.");
            if (a.Changes.Any(c => c.HasErrors)) { code = ExitCode.ValidationFailed; r.Error = "Files the 1.12 game would read wrongly cannot be imported: fix them and save again."; }
        }
        else if (!inv.DryRun)
        {
            cs = MapImporter.Commit(a, s.Workspace!);
            r.Line($"Saved {Common.Plural(cs.Files.Count, "map file")} into your workspace (\"{cs.Label}\", change set {cs.Id}). Next: \"wds maps test-in-game\" or \"wds changes build-patch\", then \"wds server-maps\".");
        }
        r.Code = code;
        return r.With(new
        {
            map = a.Map.Directory, source = a.SourceDir, general = a.General, ignored = a.Ignored, seams = a.Seams.Select(x => x.Text), syncedSeams = a.SyncedSeams.Select(x => x.Text),
            rows = a.Changes.Select(c => new
            {
                path = c.GamePath, kind = c.Kind, tile = c.Tile == null ? null : new { x = c.Tile.Value.X, y = c.Tile.Value.Y }, status = Status(c), summary = c.Summary,
                problems = c.Problems, notes = c.Notes, details = c.Diff?.Lines.Select(l => $"{l.Section}: {l.Message}").Take(200),
            }),
            saved = cs == null ? null : new { changeSet = cs.Id, label = cs.Label, files = cs.Files },
        });
    }

    private static object BreakdownItemJson(BreakdownItem i, int depth) => new
    {
        kind = i.Kind, title = i.Title, detail = i.Detail, path = i.Path, chunk = i.Chunk >= 0 ? i.Chunk : (int?)null, count = i.Count, coverage = i.Coverage, onNeighbour = i.OnNeighbour,
        specular = i.SpecularPath, children = depth > 0 ? i.Children.Select(c => BreakdownItemJson(c, depth - 1)) : null, childCount = i.Children.Count,
    };

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "maps list",
            Summary = "every map of the Maps tab: continents, world maps, dungeons, raids, battlegrounds",
            Examples = new[] { "wds maps list", "wds maps list --json", "wds maps list --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var cat = Catalog(s);
                var maps = MapOperations.ListedMaps(cat);
                var r = new CliResult().With(maps.Select(m => MapJson(m, s.Workspace)));
                foreach (var l in Common.Table(new[] { "ID", "Folder", "Name", "Kind", "Tiles", "Changed" }, maps.Select(m => (IReadOnlyList<string>)new[] { m.Id.ToString(), m.Directory, m.Name, m.Kind, m.Detail, (s.Workspace == null ? 0 : ChangeOperations.MapFiles(s.Workspace, m).Count).ToString() }))) r.Line(l);
                if (cat is MapCatalog160 other) r.Line("").Line($"{s.Profile!.VersionBuild}, read only: " + other.StatusText);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps zones",
            Summary = "the zones of a map (ZONES) with the tiles each covers",
            Args = new[] { "<map>" },
            Examples = new[] { "wds maps zones Azeroth", "wds maps zones 1 --json", "wds maps zones Azeroth --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                var zones = Catalog(s).Zones(map);
                var r = new CliResult().With(zones.Select(z => new { id = z.Id, name = z.Name, parent = z.Parent, chunks = z.Chunks, tiles = z.Tiles.Select(t => new[] { t.X, t.Y }) }));
                foreach (var l in Common.Table(new[] { "ID", "Zone", "Tiles", "Example tile" }, zones.Select(z => (IReadOnlyList<string>)new[] { z.Id.ToString(), z.Name, z.Tiles.Count.ToString(), z.Tiles.Count > 0 ? $"{z.Tiles[0].X},{z.Tiles[0].Y}" : "" }))) r.Line(l);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps tiles",
            Summary = "the tiles of a map (the minimap), with the ones you changed and the ones Noggit saved that are not imported yet",
            Args = new[] { "<map>" },
            Options = new[] { new OptionSpec("changed", null, "only the tiles in your workspace (orange on the minimap)"), new OptionSpec("pending", null, "only the tiles Noggit saved that you did not import yet (dashed yellow)") },
            Examples = new[] { "wds maps tiles Azeroth --changed", "wds maps tiles Kalimdor --json", "wds maps tiles DeadminesInstance --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                var changed = s.Workspace == null ? new HashSet<(int X, int Y)>() : MapOperations.ChangedTiles(s.Workspace, map).ToHashSet();
                var pending = MapOperations.PendingTiles(s, map).ToHashSet();
                var other = Catalog(s) as MapCatalog160;
                var all = new List<(int X, int Y)>();
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) if (map.HasTile(x, y)) all.Add((x, y));
                var list = all.Where(t => (!inv.Flag("changed") || changed.Contains(t)) && (!inv.Flag("pending") || pending.Contains(t))).ToList();
                var r = new CliResult().With(new { map = map.Directory, total = map.TileCount, tiles = list.Select(t => new { x = t.X, y = t.Y, path = map.TilePath(t.X, t.Y), changed = changed.Contains(t), pending = pending.Contains(t) }) });
                if (other != null) r.Line($"{map.Name}: {map.TileCount} tiles ({s.Profile!.VersionBuild}, read only; the root file's FileDataID after each tile).");
                else r.Line($"{map.Name}: {map.TileCount} tiles; {changed.Count} changed in your workspace; {pending.Count} saved by Noggit and not imported yet.");
                if (map.Note != null) r.Line($"  {map.Note}.");
                foreach (var t in list.Take(inv.Flag("changed") || inv.Flag("pending") ? int.MaxValue : 4096))
                    r.Line($"  {t.X},{t.Y}  {map.TilePath(t.X, t.Y)}{(other?.TileFiles(map, t.X, t.Y) is { } maid ? $"  #{maid[0]}" : "")}{(changed.Contains(t) ? "  [Modified]" : "")}{(pending.Contains(t) ? "  [saved by Noggit, not imported]" : "")}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps minimap",
            Summary = "the map's minimap overview as a PNG (the picture the Maps tab shows, cut into its tiles)",
            Args = new[] { "<map>" },
            Options = new[] { new OptionSpec("out", "file.png", "the PNG to write (required)"), new OptionSpec("cell", "pixels", "pixels per tile (default: 32, 64 or 128 by the map's size)") },
            Examples = new[] { "wds maps minimap Azeroth --out azeroth.png", "wds maps minimap DeadminesInstance --out deadmines.png --cell 128", "wds maps minimap Azeroth --out azeroth160.png --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                if (map.IsGlobalWmo) return CliResult.Fail(ExitCode.NotFound, $"{map.Name} is a single building: it has no minimap tiles.");
                if (map.TileCount == 0) return CliResult.Fail(ExitCode.NotFound, $"{map.Name} has no tiles to show" + (map.Note != null ? $": {map.Note}." : "."));
                var outFile = OutPng(s, inv);
                int cell = inv.IntOpt("cell") ?? (map.TileCount > 400 ? 32 : map.TileCount > 60 ? 64 : 128);
                var ov = Catalog(s).Overview(map, cell, s.MinimapCacheDir, (a, b) => { if (a % 100 == 0) inv.Ctx.Progress($"  {a} of {b} tiles"); });
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                Png.Save(ov.Image, outFile, false);
                return new CliResult().With(new { map = map.Directory, file = outFile, width = ov.Image.Width, height = ov.Image.Height, cell = ov.Cell, minX = ov.MinX, minY = ov.MinY })
                    .Line($"{map.Name}: {ov.Image.Width}x{ov.Image.Height} ({ov.Cell} pixels per tile, the top-left tile is {ov.MinX},{ov.MinY}) -> {outFile}");
            },
        };
        yield return new CommandSpec
        {
            Path = "maps tile",
            Summary = "a tile's details: zones, size, heights, doodads, buildings, water, and \"On this tile\" (what is placed and spawned there)",
            Args = new[] { "<map>", "<x>", "<y>" },
            Examples = new[] { "wds maps tile Azeroth 32 48", "wds maps tile Kalimdor 40 41 --json", "wds maps tile Azeroth 32 48 --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var (map, x, y) = Tile(s, inv);
                TileSummary t;
                try { t = MapOperations.Summarize(s, map, x, y); }
                catch (IOException e) { throw new CliError(ExitCode.NotFound, e.Message); }
                var r = new CliResult().With(new { map = map.Directory, x, y, path = t.Path, modified = t.Modified, facts = t.Facts, zones = t.Zones.Select(z => new { area = z.Area, name = z.Name, chunks = z.Chunks }), report = FindCommands.ReportJson(t.Report) });
                r.Line($"{map.Name}, tile {x},{y}: {t.Path}{(t.Modified ? "  [Modified]" : "")}");
                foreach (var f in t.Facts) r.Line("* " + f);
                r.Line("").Line("ON THIS TILE");
                FindCommands.AppendReport(r, t.Report);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps breakdown",
            Summary = "\"Break down this tile...\": every piece of the tile by kind (terrain and chunks, ground textures, doodads, buildings, water, other)",
            Args = new[] { "<map>", "<x>", "<y>" },
            Options = new[] { new OptionSpec("filter", "text", "only the pieces whose text contains this (palm, river, hole, a file name...)"), new OptionSpec("all", null, "also every child (chunks, placements); default: the groups and their first level") },
            Examples = new[] { "wds maps breakdown Azeroth 32 48", "wds maps breakdown Azeroth 32 48 --filter elwynnbush --all --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var (map, x, y) = Tile(s, inv);
                var bd = MapOperations.Breakdown(s, map, x, y);
                var filter = inv.Opt("filter");
                bool all = inv.Flag("all") || filter != null;
                bool Keep(BreakdownItem i) => filter == null || i.Matches(filter) || i.Children.Any(Keep);
                var r = new CliResult().With(new
                {
                    map = map.Directory, x, y, pieces = bd.All().Count(),
                    groups = bd.Groups.Select(g => new { key = g.Key, title = g.Title, summary = g.Summary, items = g.Items.Where(Keep).Select(i => BreakdownItemJson(i, all ? 4 : 0)) }),
                });
                r.Line($"{map.Name}, tile {x},{y}: {bd.All().Count():N0} pieces.");
                foreach (var g in bd.Groups)
                {
                    var items = g.Items.Where(Keep).ToList();
                    if (filter != null && items.Count == 0) continue;
                    r.Line("").Line($"{g.Title.ToUpperInvariant()}: {g.Summary}");
                    void Add(BreakdownItem i, int depth)
                    {
                        r.Line(new string(' ', 2 + 2 * depth) + i.Title + (i.Detail.Length > 0 ? " - " + i.Detail : ""));
                        if (all) foreach (var c in i.Children.Where(Keep)) Add(c, depth + 1);
                        else if (i.Children.Count > 0) r.Line(new string(' ', 4 + 2 * depth) + $"({i.Children.Count} more inside; --all lists them)");
                    }
                    foreach (var i in items) Add(i, 0);
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps render",
            Summary = "a 2D preview of a tile as a PNG: its relief, the height heat map (with chunks outlined) or where a ground texture is painted",
            Args = new[] { "<map>", "<x>", "<y>" },
            Options = new[]
            {
                new OptionSpec("out", "file.png", "the PNG to write (required)"),
                new OptionSpec("kind", "relief|heights|texture", "relief (shaded, default), heights (blue low, red high, holes dark) or texture (the coverage map of --texture)"),
                new OptionSpec("texture", "path", "the ground texture for --kind texture"),
                new OptionSpec("chunks", "list", "heights: outline these chunks (0-255, comma-separated)"),
                new OptionSpec("size", "pixels", "the picture's size (default 512)"),
            },
            Details = "The interactive 3D view of a tile is in the app (Maps tab); this draws the previews of the tile breakdown (\"On its own\").",
            Examples = new[] { "wds maps render Azeroth 32 48 --out relief.png", "wds maps render Azeroth 32 48 --kind heights --chunks 0,1,2 --out heights.png", "wds maps render Azeroth 32 48 --kind texture --texture Tileset\\Elwynn\\ElwynnGrassBase.blp --out grass.png", "wds maps render Azeroth 32 48 --out relief160.png --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var (map, x, y) = Tile(s, inv);
                var outFile = OutPng(s, inv);
                int size = inv.IntOpt("size") ?? 512;
                var tile = Terrain(s, map, x, y) ?? throw new CliError(ExitCode.NotFound, $"{map.TilePath(x, y)} could not be read.");
                var kind = (inv.Opt("kind") ?? "relief").ToLowerInvariant();
                RgbaImage img = kind switch
                {
                    "relief" => tile.Relief(size),
                    "heights" => TileBreakdown.HeightMap(tile, inv.Opt("chunks")?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => int.TryParse(c.Trim(), out var v) ? v : throw new CliError(ExitCode.Usage, "--chunks are numbers 0-255.")).ToList(), size),
                    "texture" => TileBreakdown.CoverageImage(tile, inv.Opt("texture") is { } tx ? Core.Mpq.ClientData.ResolveName(tx.Replace('/', '\\')) : throw new CliError(ExitCode.Usage, "--kind texture needs --texture <path>."), size),
                    _ => throw new CliError(ExitCode.Usage, "--kind is relief, heights or texture."),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                Png.Save(img, outFile, false);
                return new CliResult().With(new { map = map.Directory, x, y, kind, file = outFile, width = img.Width, height = img.Height }).Line($"{map.Name}, tile {x},{y} ({kind}) -> {outFile}");
            },
        };
        yield return new CommandSpec
        {
            Path = "maps noggit",
            Summary = "\"Edit in Noggit...\": prepare the client view and the project with a bookmark at the tile; --launch starts Noggit",
            Args = new[] { "<map>", "[x]", "[y]" },
            Options = new[]
            {
                new OptionSpec("copy-archives", null, "when the app's data folder is on another drive than the game: allow copying the archives (about 5 GB)"),
                new OptionSpec("launch", null, "start Noggit afterwards (our fixed fork; set its path with \"wds settings set noggit\")"),
            },
            Details = "Noggit never sees the game folder itself: the client view links to the game's archives. First time in Noggit: Open an existing project, pick the project file; " +
                      "then double-click the bookmark. Save in Noggit, then \"wds maps import-noggit <map>\".",
            Examples = new[] { "wds maps noggit Azeroth 32 48", "wds maps noggit Azeroth 32 48 --launch --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                int x = inv.ArgOrNull(1) != null ? inv.Int(1) : map.Bounds.MinX, y = inv.ArgOrNull(2) != null ? inv.Int(2) : map.Bounds.MinY;
                NoggitPrepared p;
                try { p = MapOperations.PrepareNoggit(s, map, x, y, inv.Flag("copy-archives")); }
                catch (NoggitException e) when (e.NeedsCopy) { return CliResult.Fail(ExitCode.NeedsYes, e.Message + "\nNothing was copied. Run it again with --copy-archives to copy them."); }
                catch (NoggitException e) { return CliResult.Fail(ExitCode.Refused, e.Message); }
                var r = new CliResult().With(new { map = map.Directory, x, y, project = p.Info.ProjectFile, projectDir = p.Info.ProjectDir, saveDir = p.Info.SaveDir, clientDir = p.Info.ClientDir, bookmark = p.Info.BookmarkName, clientNote = p.Info.ClientNote, noggit = p.NoggitExe, synced = p.Synced, running = p.AlreadyRunning, launched = false });
                r.Line($"Noggit project: {p.Info.ProjectFile}").Line($"Bookmark: {p.Info.BookmarkName}").Line($"Noggit saves into: {p.Info.SaveDir}").Line($"Client view: {p.Info.ClientDir} ({p.Info.ClientNote})");
                if (p.Synced > 0) r.Line($"{Common.Plural(p.Synced, "map file")} you imported earlier copied into the project, so Noggit continues from your version.");
                r.Line(p.NoggitExe != null ? $"Noggit: {p.NoggitExe}" : "Noggit was not found: set it with \"wds settings set noggit <noggit.exe>\".");
                if (inv.Flag("launch"))
                {
                    if (p.NoggitExe == null) return new CliResult { Code = ExitCode.NotFound, Error = "Noggit was not found: set it with \"wds settings set noggit <noggit.exe>\"." };
                    NoggitService.Launch(p.NoggitExe);
                    r.Line("Noggit is starting. First time: Open an existing project and pick the project file above; then double-click the bookmark.");
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps import-noggit",
            Summary = "\"Import map changes...\": review the files Noggit saved (what changed, the 1.12 check) and save them as one import",
            Args = new[] { "<map>" },
            Options = new[] { new OptionSpec("exclude", "file", "leave out the files whose name contains this", true), Common.DryRun },
            Examples = new[] { "wds maps import-noggit Azeroth --dry-run", "wds maps import-noggit Azeroth --exclude .wdl", "wds maps import-noggit Azeroth --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                var a = MapOperations.AnalyzeNoggit(s, map);
                return Review(a, inv, s, $"Import map changes: {map.Name} (from {a.SourceDir})");
            },
        };
        yield return new CommandSpec
        {
            Path = "maps export-terrain",
            Summary = "\"Open terrain in Blender...\": a terrain package of a tile and its neighbours (each tile its own object); --open starts Blender",
            Args = new[] { "<map>", "<x>", "<y>" },
            Options = new[]
            {
                new OptionSpec("size", "1|3|5", "the selected tile only (1, default), with its 8 neighbours (3) or two rings (5)"),
                new OptionSpec("tiles", "x,y;x,y", "exactly these tiles (the selected tile always goes)"),
                new OptionSpec("references", null, "doodads and buildings as grey reference shapes (ignored on import)"),
                new OptionSpec("out", "folder", "where the package goes (default: <exports>\\Terrain)"),
                new OptionSpec("open", null, "start Blender with it (\"Export and open in Blender\")"),
            },
            Examples = new[] { "wds maps export-terrain Azeroth 32 48 --size 3", "wds maps export-terrain Azeroth 32 48 --tiles \"33,48;32,49\" --references --out terrain --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var (map, x, y) = Tile(s, inv);
                var set = new HashSet<(int X, int Y)> { (x, y) };
                if (inv.Opt("tiles") is { } tl)
                    foreach (var part in tl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var xy = part.Split(',');
                        if (xy.Length != 2 || !int.TryParse(xy[0], out var tx) || !int.TryParse(xy[1], out var ty)) throw new CliError(ExitCode.Usage, $"--tiles is like \"33,48;32,49\" (not {part}).");
                        if (!map.HasTile(tx, ty)) throw new CliError(ExitCode.NotFound, $"{map.Name} has no tile {tx},{ty}.");
                        set.Add((tx, ty));
                    }
                else foreach (var t in TerrainExporter.Square(map, x, y, inv.IntOpt("size") ?? 1)) set.Add(t);
                var dir = inv.Opt("out") is { } o ? Path.GetFullPath(o) : Path.Combine(s.Settings.DefaultExportDir, "Terrain");
                Directory.CreateDirectory(dir);
                var req = MapOperations.TerrainExport(s, map, set, (x, y), dir, inv.Flag("references"));
                inv.Ctx.Progress($"Exporting {Common.Plural(set.Count, "tile")} for Blender...");
                var res = TerrainExporter.Export(req);
                var r = new CliResult().With(new { map = map.Directory, x, y, tiles = res.Manifest.Tiles.Count, folder = res.Folder, manifest = res.ManifestPath, textures = res.Manifest.Textures.Count, notes = res.Notes, groundTextures = MapOperations.GroundExportText(s) });
                r.Line($"Exported {Common.Plural(res.Manifest.Tiles.Count, "tile")} -> {res.Folder} ({Common.Plural(res.Manifest.Textures.Count, "ground texture")}).").Line(MapOperations.GroundExportText(s));
                foreach (var n in res.Notes) r.Line("  note: " + n);
                r.Line("In Blender: edit the heights, Sync shared edges, Export terrain for WoW Data Studio. Then \"wds maps import-terrain <folder>\".");
                if (inv.Flag("open")) OpenFile(r, res.ManifestPath);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps export-building",
            Summary = "\"Open building in Blender\": a building's (WMO's) shape as a glTF to look at (buildings cannot be imported back yet)",
            Args = new[] { "<wmo>" },
            Options = new[] { new OptionSpec("out", "folder", "where the folder goes (default: <exports>\\Models)"), new OptionSpec("open", null, "start Blender with it") },
            Examples = new[] { "wds maps export-building World\\wmo\\Azeroth\\Buildings\\Stormwind\\Stormwind.wmo --out buildings" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Common.GamePath(s, inv.Arg(0));
                if (!path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)) throw new CliError(ExitCode.Usage, $"{path} is not a building (.wmo).");
                var dir = inv.Opt("out") is { } o ? Path.GetFullPath(o) : Path.Combine(s.Settings.DefaultExportDir, "Models");
                var folder = Path.Combine(dir, ModelExporter.SafeName(Path.GetFileNameWithoutExtension(path)) + "_building");
                var gltf = TerrainExporter.ExportBuilding(path, p => s.ReadCurrent(p), folder);
                var r = new CliResult().With(new { path, gltf }).Line($"{path} -> {gltf} (to look at: buildings cannot be imported back yet).");
                if (inv.Flag("open")) OpenFile(r, gltf);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps import-terrain",
            Summary = "\"Import edited terrain...\": the heights from Blender back, with the edge (seam) check, the review and one import",
            Args = new[] { "<package>" },
            Options = new[]
            {
                new OptionSpec("sync-edges", null, "when shared edges disagree: copy the heights of the edited side onto the neighbour (\"Sync the edges\"); without it the import is refused"),
                new OptionSpec("exclude", "file", "leave out the tiles whose file name contains this", true),
                Common.DryRun,
            },
            Examples = new[] { "wds maps import-terrain terrain\\Azeroth_32_48_terrain --dry-run", "wds maps import-terrain terrain\\Azeroth_32_48_terrain --sync-edges --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Path.GetFullPath(inv.Arg(0));
                if (!Directory.Exists(path) && !File.Exists(path)) throw new CliError(ExitCode.NotFound, $"{path} does not exist.");
                var a = MapOperations.AnalyzeTerrain(s, path, false);
                if (a.Seams.Count > 0)
                {
                    if (!inv.Flag("sync-edges"))
                        return new CliResult { Code = ExitCode.ValidationFailed, Error = "Edges do not match their neighbours. " + MapOperations.SeamText(a) + "\nRun it again with --sync-edges to sync them, or fix it in Blender." }
                            .With(new { seams = a.Seams.Select(x => x.Text) });
                    a = MapOperations.AnalyzeTerrain(s, path, true);
                }
                return Review(a, inv, s, a.Heading ?? $"Import edited terrain: {a.Map.Name}");
            },
        };
        yield return new CommandSpec
        {
            Path = "maps compare",
            Summary = "a map file before (the game's) and after (your edit): every change in words; --out writes the height difference (red raised, blue lowered)",
            Args = new[] { "<map-or-path>", "[x]", "[y]" },
            Options = new[] { new OptionSpec("out", "file.png", "the height difference picture (tiles only)") },
            Examples = new[] { "wds maps compare Azeroth 32 48 --out diff.png", "wds maps compare World\\Maps\\Azeroth\\Azeroth.wdt --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                string path;
                if (inv.ArgOrNull(1) != null) { var (map, x, y) = Tile(s, inv); path = map.TilePath(x, y); }
                else path = Core.Mpq.ClientData.ResolveName(inv.Arg(0).Replace('/', '\\'));
                var after = s.Workspace!.ReadEdited(path) ?? throw new CliError(ExitCode.NotFound, $"{path} has not been changed: nothing to compare.");
                var before = s.Data!.Read(path);
                var (diff, img, note) = MapOperations.Compare(before, after, path, "Original (from game)", "Your edit");
                string? outFile = null;
                if (inv.Opt("out") is { } o && img != null) { outFile = Path.GetFullPath(o); Directory.CreateDirectory(Path.GetDirectoryName(outFile)!); Png.Save(img, outFile, false); }
                var r = new CliResult().With(new { path, note, changes = diff?.Lines.Select(l => new { section = l.Section, message = l.Message }), picture = outFile });
                r.Line($"{path}: {note}");
                if (diff != null) foreach (var l in diff.Lines.Take(200)) r.Line($"  {l.Section}: {l.Message}");
                if (outFile != null) r.Line($"Height difference -> {outFile}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "maps test-in-game",
            Summary = "\"Test map in game (loose files)\": copy the map's changed files into the game folder (needs --yes)",
            Args = new[] { "<map>" },
            Options = new[] { new OptionSpec("force", null, "replace a file there that the app did not put there (it is backed up first and put back on remove)"), ChangeCommands.ReloadOpt, Common.DryRun },
            Examples = new[] { "wds maps test-in-game Azeroth --dry-run", "wds maps test-in-game Azeroth --yes", "wds maps test-in-game Kalimdor --yes --reload" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var map = Map(s, inv.Arg(0));
                var paths = ChangeOperations.MapFiles(s.Workspace!, map);
                if (paths.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"{map.Name} has no changes in your workspace yet.");
                return ChangeCommands.Install(inv, s, paths, ChangeOperations.ServerMapsNote);
            },
        };
        yield return new CommandSpec
        {
            Path = "maps revert",
            Summary = "\"Revert the tile\": throw away your edit of a tile (or every changed file of the map); also removes its loose copy (needs --yes)",
            Args = new[] { "<map>", "[x]", "[y]" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds maps revert Azeroth 32 48 --yes", "wds maps revert Azeroth --dry-run" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                List<string> paths;
                if (inv.ArgOrNull(1) != null) { var (map, x, y) = Tile(s, inv); paths = s.Workspace!.IsModified(map.TilePath(x, y)) ? new List<string> { map.TilePath(x, y) } : new(); }
                else paths = ChangeOperations.MapFiles(s.Workspace!, Map(s, inv.Arg(0)));
                if (paths.Count == 0) return CliResult.Fail(ExitCode.NotFound, "Nothing to revert: no such change in your workspace.");
                var r = new CliResult().With(new { files = paths, reverted = false });
                r.Line($"Revert {Common.Plural(paths.Count, "map file")}: the edited copies are deleted from your workspace and the game's files are used again (loose copies are removed from the game folder; the same copies in the Noggit project are forgotten).");
                foreach (var p in paths) r.Line("  " + p);
                if (inv.DryRun) return r;
                if (!inv.Yes) return Common.NeedsYes(r, "This deletes your edits.");
                var done = paths.SelectMany(p => ChangeOperations.Revert(s, p, null)).ToList();
                foreach (var d in done) r.Line(d);
                return r.With(new { files = paths, reverted = true, done });
            },
        };

        // ------------------------------------------------------------------ server-maps
        yield return new CommandSpec
        {
            Path = "server-maps status",
            Summary = "\"Update server maps...\": the changed maps, whether they are in patch-3.MPQ, the world server, the last extraction and update",
            Examples = new[] { "wds server-maps status", "wds server-maps status --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var u = new ServerMapsUpdater(s, Catalog112(s));
                var (patchOk, patchText) = u.PatchState();
                var (running, serverText) = u.ServerState();
                var rep = u.Report();
                var r = new CliResult().With(new
                {
                    tools = u.ToolsDir, serverData = u.ServerDataDir, staging = u.Service?.Paths.Staging, backups = u.Service?.Paths.Backups, maps = u.MapIds, changedMaps = u.ChangedMapsText,
                    patchOk, patchText, serverRunning = running, serverText, report = rep?.Lines(), toInstall = rep?.ToInstall, lastUpdate = u.LastUpdate(), undoText = u.UndoText,
                });
                r.Line($"Server tools: {u.ToolsDir ?? "not found (wds settings set server-tools <folder>)"}").Line($"Server data: {u.ServerDataDir}");
                r.Line("1. Your map changes: " + u.ChangedMapsText).Line("   " + patchText);
                r.Line("2. Extract into the staging folder: " + (rep == null ? "not done yet (wds server-maps extract)" : $"{rep.ToInstall} file(s) differ from the server's data."));
                if (rep != null) foreach (var l in rep.Lines().Take(30)) r.Line("   " + l);
                r.Line("3. Install into the server: " + serverText).Line("Undo: " + u.UndoText);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server-maps extract",
            Summary = "\"Extract into staging\": run the extractors into the staging folder only (the server's data is only read; allowed while it runs)",
            Options = new[] { new OptionSpec("steps", "list", "maps,vmaps,mmaps (default: all three): terrain (about 20 s), collision (about 1 min), pathing (1-3 min per tile)") },
            Details = ServerMapsUpdater.MmapsCaveat,
            Examples = new[] { "wds server-maps extract", "wds server-maps extract --steps maps,vmaps --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var u = new ServerMapsUpdater(s, Catalog112(s));
                var steps = Steps(inv);
                var (patchOk, patchText) = u.PatchState();
                var r = new CliResult();
                if (!patchOk && u.MapIds.Count > 0) r.Warnings.Add(patchText + " The extraction does not contain those changes.");
                var (res, rep, text) = await u.ExtractAsync(steps, new SyncProgress<ServerProgress>(p => inv.Ctx.Progress($"  {p.Stage}: {p.Line.Trim()}")));
                r.Code = res.Ok ? ExitCode.Ok : ExitCode.Error;
                if (!res.Ok) r.Error = text;
                else r.Line(text);
                if (rep != null) foreach (var l in rep.Lines()) r.Line("  " + l);
                return r.With(new { ok = res.Ok, exitCode = res.ExitCode, seconds = res.Time.TotalSeconds, report = rep?.Lines(), toInstall = rep?.ToInstall, staging = u.Service?.Paths.Staging, log = res.Output.TakeLast(200) });
            },
        };
        yield return new CommandSpec
        {
            Path = "server-maps install",
            Summary = "\"Install into the server\" (--yes) or \"Show what would change\" (--dry-run): only the changed files, after a verified backup; refused while mangosd runs",
            Options = new[] { new OptionSpec("steps", "list", "maps,vmaps,mmaps (default: all three)"), Common.DryRun },
            Details = ServerMapsUpdater.StopHowTo,
            Examples = new[] { "wds server-maps install --dry-run", "wds server-maps install --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var u = new ServerMapsUpdater(s, Catalog112(s));
                if (!inv.DryRun)
                {
                    var (running, text) = u.ServerState();
                    if (running) return CliResult.Fail(ExitCode.Refused, "Refused: " + text + "\n" + ServerMapsUpdater.StopHowTo);
                    if (!inv.Yes) return Common.NeedsYes(new CliResult().Line($"This installs the files that differ into the server's data ({u.ServerDataDir}) after a verified backup."), "This changes the server's data.");
                }
                var res = await u.InstallAsync(Steps(inv), inv.DryRun, new SyncProgress<ServerProgress>(p => inv.Ctx.Progress("  " + p.Line.Trim())));
                var r = new CliResult { Code = res.RefusedServerRuns ? ExitCode.Refused : res.Ok ? ExitCode.Ok : ExitCode.Error };
                if (res.Ok) r.Line(res.Message); else r.Error = res.Message;
                if (res.Script != null && inv.DryRun) foreach (var l in res.Script.Output) r.Line("  " + l);
                return r.With(new { ok = res.Ok, refused = res.RefusedServerRuns, message = res.Message, backup = res.Backup, log = res.Script?.Output.TakeLast(400) });
            },
        };
        yield return new CommandSpec
        {
            Path = "server-maps undo",
            Summary = "\"Undo server update\": put the server's data back as before the last update done through the app (needs --yes; refused while mangosd runs)",
            Examples = new[] { "wds server-maps undo --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var u = new ServerMapsUpdater(s, Catalog112(s));
                var last = u.LastUpdate();
                if (last == null) return new CliResult().Line("No server update was done through the app yet (or it was already undone): nothing to undo.").With(new { undone = false });
                var (running, text) = u.ServerState();
                if (running) return CliResult.Fail(ExitCode.Refused, "Refused: undoing also needs the world server stopped. " + text + "\n" + ServerMapsUpdater.StopHowTo);
                if (!inv.Yes) return Common.NeedsYes(new CliResult().Line(u.UndoText), $"This puts the server's data back from {last.Backup} and removes the files the update added.");
                var res = await u.UndoAsync(new SyncProgress<ServerProgress>(p => inv.Ctx.Progress("  " + p.Line.Trim())));
                var r = new CliResult { Code = res.RefusedServerRuns ? ExitCode.Refused : res.Ok ? ExitCode.Ok : ExitCode.Error };
                if (res.Ok) r.Line(res.Message); else r.Error = res.Message;
                return r.With(new { undone = res.Ok, backup = res.Backup, message = res.Message });
            },
        };
    }

    private static List<string> Steps(Invocation inv)
    {
        var steps = (inv.Opt("steps") ?? "maps,vmaps,mmaps").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Distinct().ToList();
        foreach (var x in steps) if (!ServerMapsUpdater.AllSteps.Contains(x)) throw new CliError(ExitCode.Usage, $"--steps are maps, vmaps and mmaps (not {x}).");
        if (steps.Count == 0) throw new CliError(ExitCode.Usage, "--steps: at least one of maps, vmaps, mmaps.");
        return steps;
    }

    private static void OpenFile(CliResult r, string file)
    {
        var blender = BlenderTools.FindBlender();
        var root = BlenderTools.AddonRoot();
        if (blender == null || root == null) { r.Warnings.Add(blender == null ? "Blender was not found: the package was written; open it later in Blender (File > Import)." : "The app's copy of the Blender add-on was not found. Repair the installation."); return; }
        BlenderTools.OpenInBlender(blender, root, file);
        r.Line("Blender is starting with it.");
    }
}
