using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using WoWData.Core.Dbc;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Maps.Conversion;

/// <summary>What to convert: a map of the source and which of its tiles (a zone with its subzones, a list of tiles, or
/// every tile).</summary>
public sealed class MapConvertRequest
{
    /// <summary>The source map: its ID, directory or name.</summary>
    public string Map { get; init; } = "";
    /// <summary>A zone (AreaTable ID or name, of the source): the tiles with a chunk in it or one of its subzones.</summary>
    public string? Zone { get; init; }
    /// <summary>Exactly these tiles.</summary>
    public IReadOnlyList<(int X, int Y)>? Tiles { get; init; }
    /// <summary>Where assets without a 1.12 name go (<c>World\Converted\&lt;FileDataID&gt;.&lt;ext&gt;</c>).</summary>
    public string ConvertedFolder { get; init; } = @"World\Converted";
}

/// <summary>The user's 1.12 game: its files, the workspace the conversion is saved into, its data tables.</summary>
public sealed class MapConvertTarget
{
    public required ClientData Client { get; init; }
    public required Ws Workspace { get; init; }
    public required DbcService Dbc { get; init; }
    /// <summary>The game's language (index of <see cref="DbcTable.LocaleNames"/>): new names go there and to enUS.</summary>
    public int Locale { get; init; }

    /// <summary>Your workspace version of a file, else the game's.</summary>
    public byte[]? ReadCurrent(string path) => (Workspace.IsModified(path) ? Workspace.ReadEdited(path) : null) ?? Client.Read(path);
    public bool Exists(string path) => Client.Exists(path) || Workspace.IsModified(path);
}

/// <summary>A file the conversion writes into the workspace.</summary>
public sealed record ConvertedFile(string Path, string Kind, bool IsNew, long Size, string Summary, string UsedBy, bool ReplacesYourEdit);

/// <summary>A new 1.12 AreaTable row made for a source-only area.</summary>
public sealed record NewAreaRow(int SourceId, int Id, string Name, int ParentId, int MapId, int AreaBit, int Chunks);

/// <summary>An asset of the source the conversion could not take (a model without a 1.12 file), with how often it is placed.</summary>
public sealed record LeftOutAsset(string Name, string Kind, int Placements);

/// <summary>A texture converted into the workspace.</summary>
public sealed record ConvertedTexture(string Path, uint FileDataId, int Width, int Height, int SourceWidth, int SourceHeight, string Source, int Uses);

/// <summary>
/// The result of a map conversion (also of a dry run, which writes nothing): what was converted, what was reused from
/// 1.12, what was left out, the new table rows, the files and the conversion report (every reduced or dropped detail).
/// </summary>
public sealed class MapConversionResult
{
    public string SourceLabel { get; init; } = "";
    public string NamesText { get; init; } = "";
    public SourceMap Source { get; init; } = null!;
    public int TargetMapId { get; set; }
    public string TargetDirectory { get; set; } = "";
    public string TargetName { get; set; } = "";
    public bool NewMap { get; set; }
    public string SelectionText { get; set; } = "";
    public bool DryRun { get; init; }
    public List<(int X, int Y)> Tiles { get; } = new();
    public int NewTiles { get; set; }
    /// <summary>Other zones the selected tiles also cover (whole tiles are converted): name and chunks.</summary>
    public List<(string Zone, int Chunks)> AlsoCovers { get; } = new();
    public ConversionReport Report { get; } = new();
    // textures
    public int TexturesReused { get; set; }
    public int TextureUsesReused { get; set; }
    public List<ConvertedTexture> TexturesConverted { get; } = new();
    public List<string> TexturesMissing { get; } = new();
    // placements
    public int DoodadsKept, DoodadsLeftOut, DoodadsKept112, WmosKept, WmosLeftOut, WmosKept112;
    public List<LeftOutAsset> LeftOut { get; } = new();
    // areas
    public int AreasKept { get; set; }
    public List<NewAreaRow> NewAreas { get; } = new();
    public int UnknownAreaChunks { get; set; }
    // shadows, minimaps
    public int ShadowsReused { get; set; }
    public int ShadowsLostResculpted { get; set; }
    public int Shadows160Dropped { get; set; }
    public int Minimaps { get; set; }
    public int MinimapsMissing { get; set; }
    internal HashSet<string> ReusedTextureSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    // map-level and tables
    public List<string> TableNotes { get; } = new();
    public List<ConvertedFile> Files { get; } = new();
    public MapReport Validation { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> ServerSteps { get; } = new();
    public ChangeSet? ChangeSet { get; set; }
    public TimeSpan Elapsed { get; set; }

    public bool Ok => Validation.Ok;

    /// <summary>The result in plain lines (the window's preview and the command line).</summary>
    public List<string> Lines()
    {
        var l = new List<string>();
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        l.Add($"{(DryRun ? "Dry run (nothing written)" : "Converted")}: {Source.Name} (map {Source.Id}) of {SourceLabel} into your 1.12 game as " +
              (NewMap ? $"a NEW map: World\\Maps\\{TargetDirectory}\\ (Map ID {TargetMapId}, \"{TargetName}\")" : $"{TargetName} (World\\Maps\\{TargetDirectory}\\, Map ID {TargetMapId})") + ".");
        l.Add($"Tiles: {N(Tiles.Count)} ({SelectionText}){(NewTiles > 0 ? $"; {N(NewTiles)} of them {(NewTiles == 1 ? "is" : "are")} new to the map" : "")}.");
        if (AlsoCovers.Count > 0) l.Add("  Whole tiles are converted, so these zones change too: " + string.Join(", ", AlsoCovers.Take(8).Select(z => $"{z.Zone} ({N(z.Chunks)} chunks)")) + (AlsoCovers.Count > 8 ? ", ..." : "") + ".");
        l.Add($"Names: {NamesText}.");
        int texUses = TexturesConverted.Sum(t => t.Uses);
        l.Add($"Ground textures: {N(TexturesReused)} reused from 1.12 by path ({N(TextureUsesReused)} layer uses), {N(TexturesConverted.Count)} converted into your workspace ({N(texUses)} uses)" +
              (TexturesConverted.Count(t => t.Width != t.SourceWidth || t.Height != t.SourceHeight) is var rs and > 0 ? $", {N(rs)} of them made smaller (1.12 loads at most {ConvertTextures.MaxSize} x {ConvertTextures.MaxSize})" : "") +
              (TexturesMissing.Count > 0 ? $"; {N(TexturesMissing.Count)} could not be read" : "") + ".");
        l.Add($"Doodads (M2): {N(DoodadsKept)} placements kept, {N(DoodadsKept112)} kept with the 1.12 model at the same place (1.60 swapped the model), {N(DoodadsLeftOut)} left out (no 1.12 model).");
        l.Add($"Buildings (WMO): {N(WmosKept)} placements kept, {N(WmosKept112)} kept with the 1.12 building, {N(WmosLeftOut)} left out (no 1.12 building).");
        if (LeftOut.Count > 0)
            l.Add("  Left out most: " + string.Join(", ", LeftOut.OrderByDescending(a => a.Placements).Take(6).Select(a => $"{a.Name} x{N(a.Placements)}")) + (LeftOut.Count > 6 ? $" and {N(LeftOut.Count - 6)} more models" : "") + ".");
        l.Add($"Areas: {N(AreasKept)} already in your AreaTable, {N(NewAreas.Count)} new row{(NewAreas.Count == 1 ? "" : "s")}" +
              (NewAreas.Count > 0 ? ": " + string.Join(", ", NewAreas.Take(6).Select(a => $"{a.Id} {a.Name}")) + (NewAreas.Count > 6 ? ", ..." : "") : "") +
              (UnknownAreaChunks > 0 ? $"; {N(UnknownAreaChunks)} chunks name an area the source does not list (written as no area)" : "") + ".");
        l.Add($"Shadows: your 1.12 shadow reused on {N(ShadowsReused)} chunks whose ground is unchanged; none on {N(ShadowsLostResculpted)} re-sculpted chunks" +
              (Shadows160Dropped > 0 ? $" ({N(Shadows160Dropped)} chunks' own 1.60 shadow not used)" : "") + ".");
        l.Add($"Minimaps: {N(Minimaps)} tiles (512 x 512 made 256 x 256){(MinimapsMissing > 0 ? $", {N(MinimapsMissing)} tiles without one" : "")}.");
        foreach (var t in TableNotes) l.Add(t);
        var totals = Report.Totals();
        if (totals.Count > 0)
        {
            l.Add("Conversion report (what 1.12 cannot show, totals by kind):");
            foreach (var (k, n) in totals) l.Add($"  {k}: {N(n)}");
        }
        l.Add($"Files: {N(Files.Count)} ({string.Join(", ", Files.GroupBy(f => f.Kind).OrderBy(g => g.Key).Select(g => $"{N(g.Count())} {g.Key}"))}).");
        if (!Validation.Ok) l.Add($"The 1.12 check found {N(Validation.Count(MapSeverity.Error))} errors: nothing can be saved.");
        foreach (var w in Warnings) l.Add("Warning: " + w);
        return l;
    }
}

/// <summary>
/// Milestones 5 and 6 of docs\research\MAP_CONVERSION_DESIGN.md: a map of another version (1.60 today) converted into
/// the user's 1.12 workspace as ONE change set ("Revert this import" in My changes takes it all back): the tiles through
/// the neutral model and <see cref="Adt112Writer"/> (liquid kinds from the source's LiquidType table; the 1.12 shadow
/// of every chunk whose ground is unchanged; area IDs kept or new AreaTable rows), their textures (reused by path or
/// converted), their placements (kept when 1.12 has the model, else left out and listed), the WDT, the WDL (rebuilt from
/// the heights), the minimap tiles and md5translate.trs, and for a map 1.12 does not have a new Map row (plus its
/// default Light row). Everything is checked with the 1.12 validator before anything is written; a dry run only
/// reports. The game folder is never written.
/// </summary>
public static class MapConverter
{
    /// <summary>Heights closer than this (yards, absolute) count as unchanged ground (the 1.12 shadow is reused).</summary>
    public const float SameHeight = 0.05f;
    /// <summary>The 1.12 client's explored-areas mask: 64 dwords (PLAYER_EXPLORED_ZONES_1, size 64, in the 1.12.1 update
    /// fields; VMaNGOS PLAYER_EXPLORED_ZONES_SIZE 64), so exploration bits 0..2047.</summary>
    public const int MaxAreaBit = 64 * 32 - 1;

    /// <summary>A folder name for a map from its name ("Zephras Isle" -&gt; "ZephrasIsle"; "Map&lt;id&gt;" when nothing is left).</summary>
    public static string ReadableDirectory(string name, int id)
    {
        var sb = new StringBuilder();
        foreach (var word in name.Split(new[] { ' ', '-', '_', '\'', ':', ',', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new string(word.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (w.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        }
        var s = sb.ToString();
        return s.Length == 0 || char.IsDigit(s[0]) ? $"Map{id}" + s : s;
    }

    /// <summary>Parses "32_48,33_48" (or "32,48;33,48").</summary>
    public static List<(int X, int Y)> ParseTiles(string text)
    {
        var res = new List<(int, int)>();
        var t = text.Trim();
        var parts = t.Contains(';') ? t.Split(';') : t.Contains('_') ? t.Split(',', ' ') : t.Split(' ');
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            var xy = p.Split('_', ',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y) || x is < 0 or > 63 || y is < 0 or > 63)
                throw new ArgumentException($"\"{p}\" is not a tile (x_y with numbers 0 to 63, for example 32_48).");
            if (!res.Contains((x, y))) res.Add((x, y));
        }
        if (res.Count == 0) throw new ArgumentException("No tile given (x_y,x_y..., for example 32_48,33_48).");
        return res;
    }

    /// <summary>The zones of a source map (areas without a parent on that map), by name.</summary>
    public static List<SourceArea> ZonesOf(IMapSource source, SourceMap map) =>
        source.Areas().Values.Where(a => a.MapId == map.Id && a.ParentId == 0 && a.Name.Length > 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The source area of a zone given by ID or name (a zone of the map first, then any area of the map).</summary>
    public static SourceArea? FindZone(IMapSource source, SourceMap map, string text)
    {
        var areas = source.Areas();
        var t = text.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return areas.GetValueOrDefault(id);
        var onMap = areas.Values.Where(a => a.MapId == map.Id && string.Equals(a.Name, t, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.ParentId == 0 ? 0 : 1).ToList();
        return onMap.FirstOrDefault() ?? areas.Values.Where(a => string.Equals(a.Name, t, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.ParentId == 0 ? 0 : 1).FirstOrDefault();
    }

    private static int TopZone(IReadOnlyDictionary<int, SourceArea> areas, int id)
    {
        for (int guard = 0; guard < 10 && areas.TryGetValue(id, out var a) && a.ParentId != 0 && a.ParentId != id; guard++) id = a.ParentId;
        return id;
    }

    private static bool Within(IReadOnlyDictionary<int, SourceArea> areas, int id, int zone)
    {
        for (int guard = 0; guard < 10; guard++)
        {
            if (id == zone) return true;
            if (!areas.TryGetValue(id, out var a) || a.ParentId == 0 || a.ParentId == id) return false;
            id = a.ParentId;
        }
        return false;
    }

    // ====================================================================== run

    /// <summary>
    /// Converts (or with <paramref name="dryRun"/> only reports). Throws <see cref="ArgumentException"/> for a request that
    /// cannot be met (no such map, zone or tile) and <see cref="WorkspaceException"/> when the 1.12 check refuses a file
    /// (nothing is written then).
    /// </summary>
    public static MapConversionResult Run(IMapSource source, MapConvertRequest req, MapConvertTarget target, bool dryRun,
                                          IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var smap = source.FindMap(req.Map) ?? throw new ArgumentException($"{source.Label} has no map \"{req.Map}\" (give its ID, folder or name; the Other versions tab's tables list them: Map).");
        if (smap.WdtFileDataId == 0) throw new ArgumentException($"{smap.Label} has no WDT in {source.Label}: there is no terrain to convert.");
        progress?.Report($"Reading {smap.Name}'s tile list...");
        var wdt = source.ReadWdt(smap);
        if (wdt.IsWmoOnly) throw new ArgumentException($"{smap.Label} is a single building (a WMO-only map): it has no terrain tiles, and modern buildings cannot be shown by 1.12 yet.");
        var res = new MapConversionResult { SourceLabel = source.Label, NamesText = source.NamesText, Source = smap, DryRun = dryRun };
        var areas = source.Areas();

        // ---- the tiles
        var existing = wdt.ExistingTiles().ToHashSet();
        var chunkAreas = new ConcurrentDictionary<(int, int), int[]>();
        if (req.Tiles is { Count: > 0 } list)
        {
            var missing = list.Where(t => !existing.Contains(t)).ToList();
            if (missing.Count > 0) throw new ArgumentException($"{smap.Name} has no tile {string.Join(", ", missing.Select(t => $"{t.X}_{t.Y}"))} in {source.Label}.");
            res.Tiles.AddRange(list.OrderBy(t => t.Y).ThenBy(t => t.X));
            res.SelectionText = $"tile{(list.Count == 1 ? "" : "s")} {string.Join(", ", res.Tiles.Take(8).Select(t => $"{t.X}_{t.Y}"))}{(list.Count > 8 ? ", ..." : "")}";
        }
        else if (!string.IsNullOrWhiteSpace(req.Zone))
        {
            var zone = FindZone(source, smap, req.Zone) ?? throw new ArgumentException(
                $"{smap.Name} has no zone \"{req.Zone}\" in {source.Label}. Its zones: {string.Join(", ", ZonesOf(source, smap).Take(30).Select(z => $"{z.Name} ({z.Id})"))}.");
            progress?.Report($"Finding the tiles of {zone.Name}...");
            int done = 0;
            Parallel.ForEach(existing, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1), CancellationToken = ct }, t =>
            {
                var a = source.ChunkAreas(smap, wdt, t.X, t.Y);
                if (a != null) chunkAreas[t] = a;
                if (Interlocked.Increment(ref done) % 50 == 0) progress?.Report($"Finding the tiles of {zone.Name}: {done} of {existing.Count} tiles read...");
            });
            foreach (var (t, a) in chunkAreas)
                if (a.Any(id => Within(areas, id, zone.Id))) res.Tiles.Add(t);
            if (res.Tiles.Count == 0) throw new ArgumentException($"No tile of {smap.Name} has ground of {zone.Name} ({zone.Id}).");
            res.Tiles.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            res.SelectionText = $"the zone {zone.Name} ({zone.Id}) and its subzones";
            var other = new Dictionary<int, int>();
            foreach (var t in res.Tiles)
                foreach (var id in chunkAreas[t])
                    if (id != 0 && !Within(areas, id, zone.Id))
                    {
                        int z = TopZone(areas, id);
                        other[z] = other.GetValueOrDefault(z) + 1;
                    }
            foreach (var (z, n) in other.OrderByDescending(kv => kv.Value)) res.AlsoCovers.Add((areas.TryGetValue(z, out var za) ? za.Name : $"area {z}", n));
        }
        else
        {
            res.Tiles.AddRange(existing.OrderBy(t => t.Y).ThenBy(t => t.X));
            res.SelectionText = "the whole map";
        }

        // ---- the 1.12 map: the same map (same ID and folder, or our earlier conversion of it) or a new one
        var mapTable = target.Dbc.LoadCurrent("Map");
        var mapCols = mapTable.Layout;
        var idCol = mapCols.Column("ID")!;
        var dirCol = mapCols.Column("Directory")!;
        var readable = ReadableDirectory(smap.Name, smap.Id);
        DbcRow? row112 = mapTable.Find(smap.Id);
        if (row112 != null)
        {
            var d = mapTable.Text(row112, dirCol);
            if (!string.Equals(d, smap.Directory, StringComparison.OrdinalIgnoreCase) && !string.Equals(d, readable, StringComparison.OrdinalIgnoreCase)) row112 = null;
        }
        row112 ??= mapTable.Rows.FirstOrDefault(r => string.Equals(mapTable.Text(r, dirCol), smap.Directory, StringComparison.OrdinalIgnoreCase) && !smap.Directory.All(char.IsDigit));
        if (row112 != null)
        {
            res.TargetMapId = (int)mapTable.Number(row112, idCol);
            res.TargetDirectory = mapTable.Text(row112, dirCol);
            res.TargetName = mapTable.Text(row112, mapCols.Column("MapName")!, target.Locale) is { Length: > 0 } n ? n : mapTable.Text(row112, mapCols.Column("MapName")!, 0);
        }
        else
        {
            res.NewMap = true;
            var dir = smap.Directory.Length > 0 && !smap.Directory.All(char.IsDigit) ? smap.Directory : readable;
            var used = mapTable.Rows.Select(r => mapTable.Text(r, dirCol)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (used.Contains(dir) || target.Exists($@"World\Maps\{dir}\{dir}.wdt")) dir += smap.Id.ToString(CultureInfo.InvariantCulture);
            res.TargetDirectory = dir;
            res.TargetMapId = mapTable.Find(smap.Id) == null ? smap.Id : (int)mapTable.SuggestId();
            res.TargetName = smap.Name.Length > 0 ? smap.Name : dir;
        }
        var targetMap = new MapInfo { Id = res.TargetMapId, Directory = res.TargetDirectory, Name = res.TargetName };

        // ---- areas: every area ID the tiles use (from the chunk headers)
        progress?.Report("Reading the zones of the tiles...");
        foreach (var t in res.Tiles.Where(t => !chunkAreas.ContainsKey(t)).ToList())
            if (source.ChunkAreas(smap, wdt, t.X, t.Y) is { } a) chunkAreas[t] = a;
        var areaChunks = new Dictionary<int, int>();
        foreach (var t in res.Tiles)
            if (chunkAreas.TryGetValue(t, out var a))
                foreach (var id in a) areaChunks[id] = areaChunks.GetValueOrDefault(id) + 1;
        var areaPlan = PlanAreas(source, smap, res, target, areaChunks);

        // ---- the tiles, converted (into a staging folder; nothing reaches the workspace before everything is checked)
        var staging = dryRun ? null : Path.Combine(target.Workspace.Paths.TempDir, "mapconvert_" + Guid.NewGuid().ToString("N")[..8]);
        if (staging != null) Directory.CreateDirectory(staging);
        try
        {
            var pending = new ConcurrentBag<(string Path, string StagedFile, OriginInfo Origin, string Summary, string Kind)>();
            var wdlHeights = new ConcurrentDictionary<int, short[]>();
            var minimapNames = new ConcurrentDictionary<(int X, int Y), string>();
            var textures = new ConcurrentDictionary<string, TexturePlan>(StringComparer.OrdinalIgnoreCase);
            var leftOut = new ConcurrentDictionary<string, (string Kind, int N)>();
            var gate = new object();
            var opts = new Adt112WriterOptions { LiquidKind = source.LiquidKindOf, ConvertedFolder = req.ConvertedFolder };
            int doneTiles = 0;
            var stagedPaths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            void Stage(string path, byte[] data, string kind, string summary, string usedBy)
            {
                if (!stagedPaths.TryAdd(ClientData.ResolveName(path), true)) return;   // identical minimap tiles share one file
                var game = target.Client.Read(path);
                var origin = new OriginInfo
                {
                    Archive = target.Client.Find(path)?.Name, Sha256 = game != null ? Ws.Sha256(game) : null, Size = game?.LongLength ?? 0,
                    Format = kind, IsNew = game == null, UsedBy = { usedBy },
                };
                string staged = "";
                if (staging != null)
                {
                    staged = Path.Combine(staging, ClientData.ResolveName(path).Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, data);
                }
                pending.Add((path, staged, origin, summary, kind));
                lock (gate) res.Files.Add(new ConvertedFile(ClientData.ResolveName(path), KindName(path), game == null, data.LongLength, summary, usedBy, target.Workspace.IsModified(path)));
            }

            Parallel.ForEach(res.Tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 1)), CancellationToken = ct }, xy =>
            {
                var (x, y) = xy;
                var tile = source.ReadTile(smap, wdt, x, y);
                tile.Map = res.TargetDirectory;
                var path = targetMap.TilePath(x, y);
                var oldBytes = res.NewMap ? null : target.ReadCurrent(path);
                var old = oldBytes != null ? Adt112Reader.ReadAdt(oldBytes, res.TargetDirectory, x, y) : null;
                var stats = new TileStats();
                MapAreas(tile, areaPlan, stats);
                ApplyShadows(tile, old, stats);
                KeepGroundEffects(tile, old);
                PlanTextures(tile, target, req.ConvertedFolder, textures, stats);
                FilterPlacements(tile, old, target, stats, leftOut);
                var tileName = Adt112Writer.TileName(tile);
                if (stats.LeftOut > 0)
                    res.Report.Add(LossKind.PlacementsLeftOut, tileName, null, stats.LeftOut, $"{stats.DoodadsLeftOut} doodads and {stats.WmosLeftOut} buildings left out: 1.12 has no such model (a newer format, not converted yet)");
                if (stats.ShadowsLost > 0)
                    res.Report.Add(LossKind.ShadowsDropped, tileName, null, stats.ShadowsLost, $"{stats.ShadowsLost} chunks lost the 1.12 shadow: their ground changed, so it would not fit");
                if (stats.Shadows160 > 0)
                    res.Report.Add(LossKind.ShadowsDropped, tileName, null, stats.Shadows160, $"{stats.Shadows160} chunks' own {VersionName(source.Version)} shadow not used (shadows come only from your 1.12 tile)");
                var bytes = Adt112Writer.WriteAdt(tile, res.Report, opts);
                var v = MapValidator.ValidateAdt(bytes, Path.GetFileName(path));
                wdlHeights[y * 64 + x] = WdlHeights.Of(tile);
                byte[]? minimap = null;
                try { minimap = source.ReadMinimap(wdt, x, y); }
                catch (Exception e) when (e is Casc.CascMissingException or Casc.CascEncryptedException) { }
                (byte[] Blp, string HashName, bool Reduced)? mm = null;
                try { if (minimap != null) mm = ConvertTextures.Minimap(minimap); }
                catch (Blp.BlpException) { }
                lock (gate)
                {
                    foreach (var f in v.Findings) res.Validation.Findings.Add(f);
                    res.Validation.Files++;
                    stats.AddTo(res);
                    if (oldBytes == null) res.NewTiles++;
                    if (mm == null) res.MinimapsMissing++;
                    else res.Minimaps++;
                }
                Stage(path, bytes, "ADT terrain tile (converted from " + VersionName(source.Version) + ")", $"{smap.Name} tile {x},{y} converted from {source.Label}" +
                      (stats.LeftOut > 0 ? $"; {stats.LeftOut} placements left out (no 1.12 model)" : ""), $"{res.TargetName} tile {x},{y}");
                if (mm is { } m)
                {
                    minimapNames[(x, y)] = m.HashName;
                    Stage($@"textures\Minimap\{m.HashName}", m.Blp, "BLP minimap tile (256 x 256 DXT1)", $"minimap of {res.TargetDirectory} tile {x},{y} (from {source.Label}, 512 x 512 made 256 x 256)", $"{res.TargetName} minimap {x},{y}");
                }
                int d = Interlocked.Increment(ref doneTiles);
                progress?.Report($"Converting tiles: {d} of {res.Tiles.Count}...");
            });
            if (res.Minimaps > 0)
                res.Report.Add(LossKind.MinimapReduced, res.TargetDirectory, null, res.Minimaps, $"{res.Minimaps} minimap tiles of 512 x 512 made 256 x 256 (1.12's size)");
            foreach (var (name, (kind, n)) in leftOut) res.LeftOut.Add(new LeftOutAsset(name, kind, n));

            // ---- textures that 1.12 does not have
            progress?.Report($"Converting {textures.Count} ground textures...");
            foreach (var tp in textures.Values.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                byte[]? spec = null, plain = null;
                try
                {
                    if (tp.FileDataId is { } f) spec = source.ReadFile(f);
                    if (tp.Named && source.FileDataIdOf(tp.Path) is { } pf && pf != tp.FileDataId) plain = source.ReadFile(pf);
                }
                catch (Exception e) when (e is Casc.CascMissingException or Casc.CascEncryptedException) { }
                if (spec == null)
                {
                    res.TexturesMissing.Add($"{tp.Path} (FileDataID {tp.FileDataId?.ToString(CultureInfo.InvariantCulture) ?? "none"})");
                    res.Report.Add(LossKind.AssetMissing, res.TargetDirectory, null, tp.Uses, $"ground texture {tp.Path} could not be read from {source.Label}: the ground shows no texture there");
                    continue;
                }
                var g = ConvertTextures.Ground(spec, plain);
                var probe = Blp.BlpFile.Parse(spec);
                res.TexturesConverted.Add(new ConvertedTexture(tp.Path, tp.FileDataId ?? 0, g.Width, g.Height, probe.Width, probe.Height, g.Source, tp.Uses));
                if (g.Reduced) res.Report.Add(LossKind.TextureResized, res.TargetDirectory, null, 1, $"{tp.Path}: {probe.Width} x {probe.Height} made {g.Width} x {g.Height} (1.12 loads at most {ConvertTextures.MaxSize} x {ConvertTextures.MaxSize})");
                Stage(tp.Path, g.Base, "BLP ground texture (DXT1, converted)", $"ground texture from {source.Label} (FileDataID {tp.FileDataId}), {g.Width} x {g.Height}", $"{res.TargetName} ground");
                Stage(GroundTextures.SpecularName(tp.Path)!, g.Specular, "BLP ground texture, specular (DXT3, converted)", $"specular (_s) ground texture from {source.Label} (FileDataID {tp.FileDataId})", $"{res.TargetName} ground");
            }

            // ---- WDT, WDL, minimap table
            progress?.Report("Writing the tile list, the far terrain and the minimap table...");
            var wdtPath = targetMap.WdtPath;
            NeutralMap wdt112;
            byte[]? oldWdt = res.NewMap ? null : target.ReadCurrent(wdtPath);
            if (oldWdt != null) wdt112 = Adt112Reader.ReadWdt(oldWdt, res.TargetDirectory);
            else wdt112 = new NeutralMap { Name = res.TargetDirectory, Source = MapSourceVersion.V112 };
            bool wdtChanged = oldWdt == null;
            foreach (var (x, y) in res.Tiles)
            {
                var e = wdt112.Tile(x, y);
                if (!e.HasAdt) { e.HasAdt = true; e.Flags |= 1; wdtChanged = true; }
            }
            if (wdtChanged)
            {
                var wb = Adt112Writer.WriteWdt(wdt112, res.Report);
                foreach (var f in MapValidator.ValidateWdt(wb, Path.GetFileName(wdtPath)).Findings) res.Validation.Findings.Add(f);
                res.Validation.Files++;
                Stage(wdtPath, wb, "WDT map tile list", oldWdt == null ? $"the tile list of the new map {res.TargetName} ({res.Tiles.Count} tiles)" : $"{res.NewTiles} new tiles flagged", $"{res.TargetName} (WDT)");
            }
            var wdlPath = targetMap.WdlPath;
            var oldWdl = res.NewMap ? null : target.ReadCurrent(wdlPath);
            var wdl = oldWdl != null ? Adt112Reader.ReadWdl(oldWdl) : new NeutralWdl { Source = MapSourceVersion.V112 };
            foreach (var (i, h) in wdlHeights) wdl.Heights[i] = h;
            var wdlBytes = Adt112Writer.WriteWdl(wdl, res.Report, Path.GetFileName(wdlPath));
            if (oldWdl == null || !oldWdl.AsSpan().SequenceEqual(wdlBytes))
            {
                var tiles = wdt112.ExistingTiles().ToList();
                foreach (var f in MapValidator.ValidateWdl(wdlBytes, Path.GetFileName(wdlPath), null, tiles).Findings) res.Validation.Findings.Add(f);
                res.Validation.Files++;
                Stage(wdlPath, wdlBytes, "WDL far terrain", $"far terrain of {res.Tiles.Count} converted tiles rebuilt from their heights", $"{res.TargetName} (WDL)");
            }
            if (minimapNames.Count > 0)
            {
                const string trs = @"textures\Minimap\md5translate.trs";
                var tb = Md5Translate.Update(target.ReadCurrent(trs), res.TargetDirectory, minimapNames);
                Stage(trs, tb, "minimap table (md5translate.trs)", $"{minimapNames.Count} minimap tiles of {res.TargetDirectory}", "the minimap");
            }

            // ---- the data tables (Map, AreaTable, Light): one file each
            var tables = BuildTables(source, smap, res, target, areaPlan);
            foreach (var (name, table, summary) in tables)
            {
                var bytes = table.Write();
                var probs = DbcValidation.FileProblems(bytes, table.Name, target.Dbc.Layouts);
                foreach (var p in probs) res.Validation.Add(MapSeverity.Error, "DBC", name + ".dbc", "", p);
                var path = DbcService.PathOf(table.Name);
                var game = target.Client.Read(path);
                var origin = new OriginInfo { Archive = target.Client.Find(path)?.Name, Sha256 = game != null ? Ws.Sha256(game) : null, Size = game?.LongLength ?? 0, Format = $"DBC, {table.Rows.Count:N0} rows", IsNew = game == null };
                origin.UsedBy.Add(table.Layout.ServerText);
                string staged = "";
                if (staging != null)
                {
                    staged = Path.Combine(staging, "DBFilesClient", table.Name + ".dbc");
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, bytes);
                }
                pending.Add((path, staged, origin, summary, "DBC"));
                res.Files.Add(new ConvertedFile(path, "data table", game == null, bytes.Length, summary, "the game's tables", target.Workspace.IsModified(path)));
            }

            // ---- notes: files that already hold your edits, the server
            var replaced = res.Files.Where(f => f.ReplacesYourEdit).ToList();
            if (replaced.Count > 0)
                res.Warnings.Add($"{replaced.Count} file{(replaced.Count == 1 ? " already has" : "s already have")} your own edits ({string.Join(", ", replaced.Take(5).Select(f => Path.GetFileName(f.Path)))}{(replaced.Count > 5 ? ", ..." : "")}): " +
                                 "the conversion builds on them (tables keep your rows) and takes them into its change set, so \"Revert this import\" of the conversion removes those earlier edits too.");
            ServerNotes(res);
            res.Files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            if (!res.Validation.Ok)
            {
                res.Elapsed = sw.Elapsed;
                if (!dryRun) throw new WorkspaceException("The 1.12 check refused the converted files, nothing was saved: " + string.Join("; ", res.Validation.Errors.Take(3).Select(f => f.Plain)));
                return res;
            }
            if (!dryRun)
            {
                progress?.Report($"Saving {pending.Count} files into your workspace...");
                var cs = target.Workspace.BeginChangeSet($"Map conversion: {res.TargetName} from {VersionName(source.Version)} ({res.SelectionText})");
                var edits = pending.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new Ws.PendingEdit(p.Path, () => File.ReadAllBytes(p.StagedFile), p.Origin, p.Summary)).ToList();
                target.Workspace.SaveEdits(edits, cs, (i, n) => progress?.Report($"Saving into your workspace: {i * 100 / Math.Max(1, n)}%..."));
                foreach (var t in new[] { "Map", "AreaTable", "Light" }) target.Dbc.Invalidate(t);
                res.ChangeSet = cs;
            }
            res.Elapsed = sw.Elapsed;
            return res;
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string VersionName(MapSourceVersion v) => v switch { MapSourceVersion.V160 => "1.60", MapSourceVersion.V243 => "2.4.3", MapSourceVersion.V335 => "3.3.5a", _ => v.ToString() };

    private static string KindName(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".adt" => "ADT", ".wdt" => "WDT", ".wdl" => "WDL", ".trs" => "minimap table",
        ".blp" => path.StartsWith(@"textures\Minimap\", StringComparison.OrdinalIgnoreCase) ? "minimap" : "texture",
        ".dbc" => "data table", _ => "file",
    };

    // ------------------------------------------------------------------ per tile

    private sealed class TileStats
    {
        public int DoodadsKept, DoodadsLeftOut, DoodadsKept112, WmosKept, WmosLeftOut, WmosKept112, LeftOut;
        public int ShadowsReused, ShadowsLost, Shadows160;
        public int TextureUsesReused;
        public HashSet<string> ReusedTextures = new(StringComparer.OrdinalIgnoreCase);

        public void AddTo(MapConversionResult r)
        {
            r.DoodadsKept += DoodadsKept; r.DoodadsLeftOut += DoodadsLeftOut; r.DoodadsKept112 += DoodadsKept112;
            r.WmosKept += WmosKept; r.WmosLeftOut += WmosLeftOut; r.WmosKept112 += WmosKept112;
            r.ShadowsReused += ShadowsReused; r.ShadowsLostResculpted += ShadowsLost; r.Shadows160Dropped += Shadows160;
            r.TextureUsesReused += TextureUsesReused;
            lock (r.ReusedTextureSet) foreach (var t in ReusedTextures) r.ReusedTextureSet.Add(t);
            r.TexturesReused = r.ReusedTextureSet.Count;
        }
    }

    private static void MapAreas(NeutralTile t, AreaPlan plan, TileStats st)
    {
        foreach (var c in t.AllChunks)
        {
            if (c.AreaId == 0) continue;
            if (plan.Map.TryGetValue((int)c.AreaId, out var to)) c.AreaId = (uint)to;
            else c.AreaId = 0;
        }
    }

    /// <summary>The user's rule: the 1.12 shadow of a chunk whose ground is unchanged (same heights within
    /// <see cref="SameHeight"/>) is reused; every other chunk has none.</summary>
    private static void ApplyShadows(NeutralTile t, NeutralTile? old, TileStats st)
    {
        for (int i = 0; i < 256; i++)
        {
            var c = t.Chunks[i];
            if (c == null) continue;
            bool had160 = c.Shadow != null && (c.Flags & 1) != 0;
            var o = old?.Chunks[i];
            bool oldUses = o?.Shadow != null && (o.Flags & 1) != 0;
            if (o != null && o.Shadow != null && SameGround(c, o))
            {
                c.Shadow = o.Shadow;
                c.Flags = (c.Flags & ~1u) | (o.Flags & 1u);
                if (oldUses) st.ShadowsReused++;
                continue;
            }
            if (had160) st.Shadows160++;
            if (oldUses) st.ShadowsLost++;
            c.Shadow = null;
            c.Flags &= ~1u;
        }
    }

    /// <summary>Ground clutter: a layer whose texture your 1.12 chunk also has takes the 1.12 layer's ground-effect ID
    /// (1.60 changed 8-10% of them, MAP_SURVEY_160 3.3, and its new IDs are not 1.12 rows); other layers keep the
    /// source's ID (the 1.12 client skips an ID its GroundEffectTexture table does not have).</summary>
    private static void KeepGroundEffects(NeutralTile t, NeutralTile? old)
    {
        if (old == null) return;
        for (int i = 0; i < 256; i++)
        {
            var c = t.Chunks[i];
            var o = old.Chunks[i];
            if (c == null || o == null) continue;
            foreach (var l in c.Layers)
                if (l.Texture.HasPath && o.Layers.FirstOrDefault(x => x.Texture.HasPath && string.Equals(ClientData.ResolveName(x.Texture.Path!), ClientData.ResolveName(l.Texture.Path!), StringComparison.OrdinalIgnoreCase)) is { } ol)
                    l.EffectId = ol.EffectId;
        }
    }

    private static bool SameGround(NeutralChunk a, NeutralChunk b)
    {
        for (int k = 0; k < 145; k++)
            if (MathF.Abs(a.Position.Z + a.Heights[k] - (b.Position.Z + b.Heights[k])) > SameHeight) return false;
        return true;
    }

    private sealed class TexturePlan
    {
        public string Path = "";
        public uint? FileDataId;
        public bool Named;
        public int Uses;
    }

    /// <summary>Every ground texture: reused when 1.12 (or your workspace) has the file by path, else planned for
    /// conversion under its name or <c>World\Converted\&lt;FileDataID&gt;.blp</c> (the layer then names that path).</summary>
    private static void PlanTextures(NeutralTile t, MapConvertTarget target, string folder, ConcurrentDictionary<string, TexturePlan> plans, TileStats st)
    {
        var uses = new Dictionary<AssetRef, int>(ReferenceEqualityComparer.Instance);
        foreach (var a in t.Textures) uses.TryAdd(a, 0);
        foreach (var l in t.AllChunks.SelectMany(c => c.Layers))
            uses[l.Texture] = uses.GetValueOrDefault(l.Texture) + 1;
        foreach (var (a, n) in uses)
        {
            if (a.HasPath && target.Exists(a.Path!))
            {
                st.TextureUsesReused += n;
                if (n > 0) st.ReusedTextures.Add(ClientData.ResolveName(a.Path!));
                continue;
            }
            if (!a.HasPath && a.FileDataId == null) continue;   // the writer reports it
            bool named = a.HasPath;
            var path = named ? ClientData.ResolveName(a.Path!) : $@"{folder}\{a.FileDataId}.blp";
            a.Path = path;
            var plan = plans.GetOrAdd(path, p => new TexturePlan { Path = p, FileDataId = a.FileDataId, Named = named });
            lock (plan) plan.Uses += n;
        }
    }

    /// <summary>Placements whose model 1.12 has are kept; one whose model 1.12 does not have keeps the 1.12 placement with
    /// the same unique ID when your 1.12 tile has one (1.60 swapped the model of many landmarks and trees, MAP_SURVEY_160
    /// section 5), else it is left out and listed. The model lists are rebuilt from what is kept.</summary>
    private static void FilterPlacements(NeutralTile t, NeutralTile? old, MapConvertTarget target, TileStats st, ConcurrentDictionary<string, (string Kind, int N)> leftOut)
    {
        bool Has(AssetRef a) => a.HasPath && target.Exists(a.Path!);
        string Name(AssetRef a) => a.HasPath ? a.Path! : a.FileDataId is { } f ? $"FDID {f}" : "(no name)";
        var oldDoodads = old?.Doodads.GroupBy(d => d.UniqueId).ToDictionary(g => g.Key, g => g.First()) ?? new();
        var oldWmos = old?.Wmos.GroupBy(d => d.UniqueId).ToDictionary(g => g.Key, g => g.First()) ?? new();

        var dMap = new int[t.Doodads.Count];
        var doodads = new List<NeutralDoodad>();
        for (int i = 0; i < t.Doodads.Count; i++)
        {
            var d = t.Doodads[i];
            if (Has(d.Model)) { dMap[i] = doodads.Count; doodads.Add(d); st.DoodadsKept++; continue; }
            if (oldDoodads.TryGetValue(d.UniqueId, out var od) && Has(od.Model)) { dMap[i] = doodads.Count; doodads.Add(od); st.DoodadsKept112++; continue; }
            dMap[i] = -1;
            st.DoodadsLeftOut++;
            st.LeftOut++;
            leftOut.AddOrUpdate(Name(d.Model), _ => ("doodad", 1), (_, v) => (v.Kind, v.N + 1));
        }
        var wMap = new int[t.Wmos.Count];
        var wmos = new List<NeutralWmo>();
        for (int i = 0; i < t.Wmos.Count; i++)
        {
            var w = t.Wmos[i];
            if (Has(w.Model)) { wMap[i] = wmos.Count; wmos.Add(w); st.WmosKept++; continue; }
            if (oldWmos.TryGetValue(w.UniqueId, out var ow) && Has(ow.Model)) { wMap[i] = wmos.Count; wmos.Add(ow); st.WmosKept112++; continue; }
            wMap[i] = -1;
            st.WmosLeftOut++;
            st.LeftOut++;
            leftOut.AddOrUpdate(Name(w.Model), _ => ("WMO", 1), (_, v) => (v.Kind, v.N + 1));
        }
        t.Doodads.Clear();
        t.Doodads.AddRange(doodads);
        t.Wmos.Clear();
        t.Wmos.AddRange(wmos);
        foreach (var c in t.AllChunks)
        {
            var dr = c.DoodadRefs.Where(r => r >= 0 && r < dMap.Length && dMap[r] >= 0).Select(r => dMap[r]).Distinct().ToList();
            c.DoodadRefs.Clear();
            c.DoodadRefs.AddRange(dr);
            var wr = c.WmoRefs.Where(r => r >= 0 && r < wMap.Length && wMap[r] >= 0).Select(r => wMap[r]).Distinct().ToList();
            c.WmoRefs.Clear();
            c.WmoRefs.AddRange(wr);
        }
        t.DoodadModels.Clear();
        foreach (var m in doodads.Select(d => d.Model).Distinct(ReferenceEqualityComparer.Instance).Cast<AssetRef>()) t.DoodadModels.Add(m);
        t.WmoModels.Clear();
        foreach (var m in wmos.Select(d => d.Model).Distinct(ReferenceEqualityComparer.Instance).Cast<AssetRef>()) t.WmoModels.Add(m);
    }

    // ------------------------------------------------------------------ areas

    private sealed class AreaPlan
    {
        /// <summary>Source area ID -&gt; 1.12 area ID (identity for areas 1.12 has).</summary>
        public readonly Dictionary<int, int> Map = new();
        public readonly List<SourceArea> New = new();
        public readonly Dictionary<int, int> NewBits = new();
    }

    /// <summary>An area 1.12 has keeps its ID; a source-only area gets a new 1.12 row (its source ID when that is free,
    /// else the next free ID) with a free exploration bit (1.12 allows 0..2047), its parent made first.</summary>
    private static AreaPlan PlanAreas(IMapSource source, SourceMap smap, MapConversionResult res, MapConvertTarget target, Dictionary<int, int> areaChunks)
    {
        var plan = new AreaPlan();
        var at = target.Dbc.LoadCurrent("AreaTable");
        var idc = at.Layout.Column("ID")!;
        var bitc = at.Layout.Column("AreaBit")!;
        var usedIds = at.Rows.Select(r => at.Number(r, idc)).ToHashSet();
        var usedBits = at.Rows.Select(r => at.Number(r, bitc)).ToHashSet();
        long nextId = usedIds.Count == 0 ? 1 : usedIds.Max() + 1;
        int nextBit = 0;
        var areas = source.Areas();
        int Ensure(int id, int depth)
        {
            if (id == 0) return 0;
            if (plan.Map.TryGetValue(id, out var known)) return known;
            if (usedIds.Contains(id) && !plan.New.Any(n => n.Id == id))
            {
                plan.Map[id] = id;
                return id;
            }
            if (!areas.TryGetValue(id, out var a) || depth > 8) return -1;
            int parent = a.ParentId != 0 && a.ParentId != id ? Ensure(a.ParentId, depth + 1) : 0;
            int newId = id;
            if (usedIds.Contains(newId)) newId = (int)nextId++;
            usedIds.Add(newId);
            while (usedBits.Contains(nextBit)) nextBit++;
            if (nextBit > MaxAreaBit) throw new WorkspaceException($"Your AreaTable has no free exploration bit left (1.12 knows bits 0 to {MaxAreaBit}) for the new area {a.Name}.");
            usedBits.Add(nextBit);
            plan.Map[id] = newId;
            plan.New.Add(a);
            plan.NewBits[id] = nextBit;
            res.NewAreas.Add(new NewAreaRow(id, newId, a.Name, Math.Max(0, parent), -1, nextBit, areaChunks.GetValueOrDefault(id)));
            return newId;
        }
        foreach (var (id, n) in areaChunks.OrderBy(kv => kv.Key))
        {
            if (id == 0) continue;
            int to = Ensure(id, 0);
            if (to < 0)
            {
                res.UnknownAreaChunks += n;
                res.Report.Add(LossKind.AreaUnknown, res.TargetDirectory, null, n, $"area {id} is not in {source.Label}'s AreaTable (encrypted or removed): {n} chunks written without an area");
                continue;
            }
            if (!res.NewAreas.Any(a => a.SourceId == id)) res.AreasKept++;
        }
        return plan;
    }

    // ------------------------------------------------------------------ tables

    private static List<(string Name, DbcTable Table, string Summary)> BuildTables(IMapSource source, SourceMap smap, MapConversionResult res, MapConvertTarget target, AreaPlan plan)
    {
        var list = new List<(string, DbcTable, string)>();
        var locale = Math.Clamp(target.Locale, 0, DbcFile.Locales - 1);
        void SetLoc(DbcTable t, DbcRow r, DbcColumn c, string text)
        {
            for (int i = 0; i < c.LocaleCount; i++) t.SetText(r, c.Field + i, i == 0 || i == locale ? text : "");
        }
        void SetNum(DbcTable t, DbcRow r, string col, long v)
        {
            if (t.Layout.Column(col) is { } c) t.SetRaw(r, c.Field, unchecked((uint)v));
        }
        bool Exists(string table, long id) => id != 0 && target.Dbc.Lookup(table)?.Find(id) != null;

        // ---- Map: a new map gets a row (a copy of Eastern Kingdoms' with the source's values)
        if (res.NewMap)
        {
            var mt = target.Dbc.LoadCurrent("Map");
            var template = mt.Find(0) ?? mt.Rows[0];
            var r = template.Clone();
            mt.SetRaw(r, mt.Layout.Column("ID")!.Field, (uint)res.TargetMapId);
            mt.SetText(r, mt.Layout.Column("Directory")!.Field, res.TargetDirectory);
            SetNum(mt, r, "InstanceType", Math.Clamp(smap.InstanceType, 0, 3));
            SetNum(mt, r, "PVP", 0);
            SetLoc(mt, r, mt.Layout.Column("MapName")!, res.TargetName);
            SetNum(mt, r, "MinLevel", 0);
            SetNum(mt, r, "MaxLevel", 0);
            SetNum(mt, r, "MaxPlayers", Math.Max(0, smap.MaxPlayers));
            SetNum(mt, r, "AreaTableID", smap.AreaTableId != 0 && plan.Map.TryGetValue(smap.AreaTableId, out var ma) ? ma : 0);
            SetLoc(mt, r, mt.Layout.Column("MapDescription0")!, "");
            SetLoc(mt, r, mt.Layout.Column("MapDescription1")!, "");
            // the loading screen: the source's when 1.12 has that row, else the parent map's, else the usual one of the kind
            int ls = smap.LoadingScreenId;
            string lsWhy;
            if (Exists("LoadingScreens", ls)) lsWhy = $"loading screen {ls} (the same as in {VersionName(source.Version)})";
            else
            {
                int parent = smap.CosmeticParentMapId >= 0 ? smap.CosmeticParentMapId : smap.ParentMapId;
                var pr = parent >= 0 ? mt.Find(parent) : null;
                ls = pr != null ? (int)mt.Number(pr, mt.Layout.Column("LoadingScreenID")!) : smap.InstanceType switch { 1 => 23, 2 => 61, 3 => 104, _ => 4 };
                if (!Exists("LoadingScreens", ls)) ls = 4;
                var lst = target.Dbc.Lookup("LoadingScreens");
                var lsName = lst?.Find(ls) is { } lr ? lst.Label(lr) : "?";
                lsWhy = $"loading screen {ls} ({lsName}) reused: {VersionName(source.Version)}'s own ({smap.LoadingScreenId}) is not in 1.12";
            }
            SetNum(mt, r, "LoadingScreenID", ls);
            SetNum(mt, r, "BracketLevelRange", 0);
            mt.InsertSorted(r);
            list.Add(("Map", mt, $"new map {res.TargetMapId} {res.TargetName} (World\\Maps\\{res.TargetDirectory}), converted from {source.Label}"));
            res.TableNotes.Add($"Map.dbc: a new row {res.TargetMapId} \"{res.TargetName}\" (folder {res.TargetDirectory}, {lsWhy}).");

            // ---- its default light (the row at 0,0,0 of the map; without one the sky has no light settings)
            var lt = target.Dbc.LoadCurrent("Light");
            var cc = lt.Layout.Column("ContinentID")!;
            if (!lt.Rows.Any(x => lt.Number(x, cc) == res.TargetMapId))
            {
                var lsrc = lt.Rows.FirstOrDefault(x => lt.Number(x, cc) == 0 && lt.Float(x, lt.Layout.Column("X")!) == 0 && lt.Float(x, lt.Layout.Column("Y")!) == 0 && lt.Float(x, lt.Layout.Column("Z")!) == 0);
                if (lsrc != null)
                {
                    var lr = lsrc.Clone();
                    long lid = lt.SuggestId();
                    lt.SetRaw(lr, lt.Layout.Column("ID")!.Field, (uint)lid);
                    lt.SetRaw(lr, cc.Field, (uint)res.TargetMapId);
                    lt.InsertSorted(lr);
                    list.Add(("Light", lt, $"default light of the new map {res.TargetMapId} (a copy of Eastern Kingdoms')"));
                    res.TableNotes.Add($"Light.dbc: a new row {lid}, the default light of map {res.TargetMapId} (a copy of Eastern Kingdoms' default light).");
                }
            }
        }

        // ---- AreaTable: the new areas
        if (plan.New.Count > 0)
        {
            var at = target.Dbc.LoadCurrent("AreaTable");
            var template = at.Rows[0];
            var nameCol = at.Layout.Column("AreaName")!;
            int dropped = 0;
            foreach (var a in plan.New)
            {
                int id = plan.Map[a.Id];
                var r = template.Clone();
                foreach (var c in at.Layout.Columns)
                {
                    if (c.Storage == DbcStorage.Loc) SetLoc(at, r, c, "");
                    else if (c.IsString) at.SetText(r, c.Field, "");
                    else for (int f = 0; f < c.Fields; f++) at.SetRaw(r, c.Field + f, 0);
                }
                SetNum(at, r, "ID", id);
                int mapId = a.MapId == smap.Id ? res.TargetMapId : Exists("Map", a.MapId) ? a.MapId : res.TargetMapId;
                SetNum(at, r, "MapID", mapId);
                SetNum(at, r, "ParentAreaID", a.ParentId != 0 && plan.Map.TryGetValue(a.ParentId, out var p) ? p : 0);
                SetNum(at, r, "AreaBit", plan.NewBits[a.Id]);
                // the area flags 1.12 knows (0x1..0x200: snow ... city; the same bits in later versions)
                SetNum(at, r, "Flags", a.Flags & 0x3FF);
                if ((a.Flags & ~0x3FFu) != 0) dropped++;
                if (Exists("ZoneMusic", a.ZoneMusic)) SetNum(at, r, "ZoneMusic", a.ZoneMusic);
                if (Exists("ZoneIntroMusicTable", a.IntroSound)) SetNum(at, r, "IntroSound", a.IntroSound);
                if (Exists("SoundAmbience", a.AmbienceId)) SetNum(at, r, "AmbienceID", a.AmbienceId);
                if (Exists("SoundProviderPreferences", a.SoundProviderPref)) SetNum(at, r, "SoundProviderPref", a.SoundProviderPref);
                if (Exists("SoundProviderPreferences", a.SoundProviderPrefUnderwater)) SetNum(at, r, "SoundProviderPrefUnderwater", a.SoundProviderPrefUnderwater);
                SetNum(at, r, "ExplorationLevel", a.ExplorationLevel);
                SetNum(at, r, "FactionGroupMask", a.FactionGroupMask & 0x6);
                SetLoc(at, r, nameCol, a.Name.Length > 0 ? a.Name : $"Area {a.Id}");
                // the loc flags field as the game's rows have it
                if (nameCol.Storage == DbcStorage.Loc) at.SetRaw(r, DbcTable.LocFlagsField(nameCol), at.Raw(template, DbcTable.LocFlagsField(nameCol)));
                at.InsertSorted(r);
                int i = res.NewAreas.FindIndex(n => n.SourceId == a.Id);
                if (i >= 0) res.NewAreas[i] = res.NewAreas[i] with { MapId = mapId, ParentId = a.ParentId != 0 && plan.Map.TryGetValue(a.ParentId, out var pp) ? pp : 0 };
            }
            list.Add(("AreaTable", at, $"{plan.New.Count} new areas converted from {source.Label}: {string.Join(", ", plan.New.Take(5).Select(a => a.Name))}{(plan.New.Count > 5 ? ", ..." : "")}"));
            res.TableNotes.Add($"AreaTable.dbc: {plan.New.Count} new row{(plan.New.Count == 1 ? "" : "s")} (exploration bit{(plan.New.Count == 1 ? "" : "s")} {(plan.New.Count == 1 ? res.NewAreas[0].AreaBit.ToString(CultureInfo.InvariantCulture) : $"{res.NewAreas.Min(n => n.AreaBit)}-{res.NewAreas.Max(n => n.AreaBit)}")}; music, ambience and sounds only where 1.12 has the same row)." +
                               (dropped > 0 ? $" Area flags 1.12 does not know were dropped on {dropped} row{(dropped == 1 ? "" : "s")}." : ""));
        }
        return list;
    }

    private static void ServerNotes(MapConversionResult res)
    {
        var s = res.ServerSteps;
        s.Add("Test it in the game: My changes > Test all in game (install as loose files), or \"wds changes install --yes\"; then restart the game.");
        if (res.NewAreas.Count > 0)
            s.Add($"The server reads areas from its database (area_template), not from AreaTable.dbc: add the {res.NewAreas.Count} new area{(res.NewAreas.Count == 1 ? "" : "s")} there with Server data > Check client and server... > areas > Make the server match the game " +
                  $"(or \"wds server match areas <id> --to server\" for each: {string.Join(", ", res.NewAreas.Take(8).Select(a => a.Id))}{(res.NewAreas.Count > 8 ? ", ..." : "")}), then Apply to server.");
        if (res.NewMap)
            s.Add($"The server reads maps from its database (map_template): add map {res.TargetMapId} the same way (Check client and server > maps, or \"wds server match maps {res.TargetMapId} --to server\"), then Apply to server.");
        s.Add("The server's terrain files are made from the game's archives: build the release patch (Build release patch, install it), then Update server maps " +
              (res.NewMap ? "with terrain, collision and pathing for the new map" : "for the converted tiles") + " (\"wds changes build-patch --install --yes\", \"wds server-maps extract\").");
    }
}
