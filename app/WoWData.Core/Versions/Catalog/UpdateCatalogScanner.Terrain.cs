using System.Collections.Concurrent;
using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;

namespace WoWData.Core.Versions.Catalog;

public sealed partial class UpdateCatalogScanner
{
    /// <summary>Zone key of a map converted as a whole (instances, new maps).</summary>
    private const int WholeMap = int.MinValue;
    /// <summary>Zone key of chunks without a zone (open sea, or an area the source does not list).</summary>
    private const int NoZone = -1;
    /// <summary>Heights closer than this (yards) count as the same ground (as <see cref="MapConverter.SameHeight"/>).</summary>
    private const float SameHeight = 0.05f;

    private sealed class TerrainAcc
    {
        public int MapId;
        public int ZoneId;
        public readonly HashSet<(int X, int Y)> Tiles = new();
        public readonly HashSet<(int X, int Y)> NewTiles = new();
        public readonly HashSet<(int X, int Y)> ChangedTiles = new();
        public int Chunks, Changed, Heights, Resculpted, Textures, Liquids, Areas;
        public int DAdded, DRemoved, DSwapped, DMoved, WAdded, WRemoved, WSwapped, WMoved;
        public int MissingPlacements;
        public readonly Dictionary<string, int> MissingModels = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> NewTextures = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> AreaIds = new();
        public bool HasChange => Changed > 0 || NewTiles.Count > 0 || DAdded + DRemoved + DSwapped + DMoved + WAdded + WRemoved + WSwapped + WMoved > 0;
    }

    private sealed record MapJob(SourceMap Map, NeutralMap Wdt, string Dir112, HashSet<(int X, int Y)>? Tiles112, bool PerZone, bool IsNew);

    private readonly ConcurrentDictionary<(int Map, int X, int Y), int[]> _tileAreas = new();
    private readonly ConcurrentDictionary<(int Map, int X, int Y), long> _tileBytes = new();
    private readonly ConcurrentDictionary<(int Map, int X, int Y), bool> _tileHasZone = new();
    private readonly ConcurrentDictionary<string, (string Kind, int Placements, HashSet<string> Where)> _newModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string NewModel, int Placements, HashSet<string> Where)> _landmarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Uses, HashSet<string> Where)> _newGround = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (string Name, int Map112Id)> _comparedMaps = new();

    private int TopZone(IReadOnlyDictionary<int, SourceArea> areas, int id)
    {
        if (id == 0 || !areas.ContainsKey(id)) return NoZone;
        for (int guard = 0; guard < 10 && areas.TryGetValue(id, out var a) && a.ParentId != 0 && a.ParentId != id && areas.ContainsKey(a.ParentId); guard++) id = a.ParentId;
        return id;
    }

    private static string TexKey(AssetRef a) => a.HasPath ? ClientData.ResolveName(a.Path!).ToUpperInvariant() : "#" + (a.FileDataId?.ToString(CultureInfo.InvariantCulture) ?? "?");

    private static bool SameModel(AssetRef a, AssetRef b) =>
        a.HasPath && b.HasPath ? string.Equals(ClientData.ResolveName(a.Path!), ClientData.ResolveName(b.Path!), StringComparison.OrdinalIgnoreCase)
            : a.FileDataId != null && a.FileDataId == b.FileDataId;

    private static string ModelName(AssetRef a) => a.HasPath ? ClientData.ResolveName(a.Path!) : a.FileDataId is { } f ? $"#{f}" : "(no name)";

    private void ScanTerrain()
    {
        var src = _s.Source;
        var c = _s.Client112;
        var dbc = new DbcService(c, null);
        var map112 = dbc.LoadGame("Map");
        var idCol = map112.Layout.Column("ID")!;
        var dirCol = map112.Layout.Column("Directory")!;
        var areas = src.Areas();
        var at112 = dbc.LoadGame("AreaTable");
        var areaIds112 = at112.Rows.Select(r => at112.Number(r, at112.Layout.Column("ID")!)).ToHashSet();
        var jobs = new List<MapJob>();
        foreach (var m in src.Maps().Where(m => m.WdtFileDataId != 0))
        {
            if (_s.Casc.State(m.WdtFileDataId) != Casc.CascFileState.Local) { Note($"{m.Label}: its tile list (WDT) is not readable here, so the map was not compared."); continue; }
            NeutralMap wdt;
            try { wdt = src.ReadWdt(m); }
            catch (Exception e) when (e is InvalidDataException or IOException or Casc.CascMissingException or Casc.CascEncryptedException) { Note($"{m.Label}: its tile list could not be read ({e.Message})."); continue; }
            var readable = MapConverter.ReadableDirectory(m.Name, m.Id);
            DbcRow? row = map112.Find(m.Id);
            if (row != null)
            {
                var d = map112.Text(row, dirCol);
                if (!string.Equals(d, m.Directory, StringComparison.OrdinalIgnoreCase) && !string.Equals(d, readable, StringComparison.OrdinalIgnoreCase)) row = null;
            }
            row ??= map112.Rows.FirstOrDefault(r => string.Equals(map112.Text(r, dirCol), m.Directory, StringComparison.OrdinalIgnoreCase) && !m.Directory.All(char.IsDigit));
            if (row == null)
            {
                if (wdt.IsWmoOnly) { NewWmoMapItem(m); continue; }
                jobs.Add(new MapJob(m, wdt, readable, null, false, true));
                continue;
            }
            var dir = map112.Text(row, dirCol);
            if (wdt.IsWmoOnly) continue;   // a single building: its changes are the building's (Buildings)
            var wdt112Bytes = c.Read($@"World\Maps\{dir}\{dir}.wdt");
            var tiles112 = wdt112Bytes == null ? new HashSet<(int, int)>() : Adt112Reader.ReadWdt(wdt112Bytes, dir).ExistingTiles().ToHashSet();
            lock (_gate) _comparedMaps[m.Id] = (m.Name, (int)map112.Number(row, idCol));
            jobs.Add(new MapJob(m, wdt, dir, tiles112, m.InstanceType == 0, false));
        }

        var work = jobs.SelectMany(j => j.Wdt.ExistingTiles().Select(t => (Job: j, t.X, t.Y))).ToList();
        Report($"Comparing {N(work.Count)} tiles of {N(jobs.Count)} maps chunk by chunk...");
        var accs = new ConcurrentDictionary<(int Map, int Zone), TerrainAcc>();
        TerrainAcc Acc(int map, int zone) => accs.GetOrAdd((map, zone), k => new TerrainAcc { MapId = k.Map, ZoneId = k.Zone });
        int done = 0, failed = 0;
        Parallel.ForEach(work, Par, w =>
        {
            try { CompareTile(w.Job, w.X, w.Y, areas, Acc); }
            catch (Exception e) when (e is InvalidDataException or IOException or Casc.CascMissingException or Casc.CascEncryptedException or IndexOutOfRangeException or ArgumentException)
            {
                Interlocked.Increment(ref failed);
            }
            int n = Interlocked.Increment(ref done);
            if (n % 250 == 0) Report($"Comparing tiles: {N(n)} of {N(work.Count)}...");
        });
        if (failed > 0) Note($"{N(failed)} tiles could not be read and were left out of the terrain comparison.");

        foreach (var j in jobs)
        {
            var mine = accs.Values.Where(a => a.MapId == j.Map.Id).ToList();
            if (j.IsNew) { NewMapItem(j, mine, areaIds112); continue; }
            foreach (var a in mine.Where(a => a.HasChange))
                TerrainItem(j, a, areas, areaIds112);
        }
        ModelAndBuildingItems();
        LightItems(dbc, areas);
    }

    private void CompareTile(MapJob j, int x, int y, IReadOnlyDictionary<int, SourceArea> areas, Func<int, int, TerrainAcc> acc)
    {
        var src = _s.Source;
        var t60 = src.ReadTile(j.Map, j.Wdt, x, y);
        NeutralTile? t12 = null;
        bool isNewTile = j.Tiles112 == null || !j.Tiles112.Contains((x, y));
        if (!isNewTile)
        {
            var b = _s.Client112.Read($@"World\Maps\{j.Dir112}\{j.Dir112}_{x}_{y}.adt");
            if (b != null) t12 = Adt112Reader.ReadAdt(b, j.Dir112, x, y);
            else isNewTile = true;
        }
        var maid = j.Wdt.Tile(x, y).Maid;
        long bytes = 0;
        if (maid != null) foreach (var slot in new[] { 0, 1, 3 }) if (maid[slot] != 0) bytes += Size160(maid[slot]);
        _tileBytes[(j.Map.Id, x, y)] = bytes;
        var areaOf = new int[256];
        int ZoneAt(int i)
        {
            if (!j.PerZone) return WholeMap;
            return TopZone(areas, areaOf[i]);
        }
        for (int i = 0; i < 256; i++) areaOf[i] = (int)(t60.Chunks[i]?.AreaId ?? 0);
        _tileAreas[(j.Map.Id, x, y)] = areaOf;
        bool hasZone = false;
        string mapLabel = j.Map.Name;
        string WhereLabel(int i)
        {
            int z = ZoneAt(i);
            return z == WholeMap ? mapLabel : z == NoZone ? $"{mapLabel} (no zone)" : areas.TryGetValue(z, out var za) ? za.Name : mapLabel;
        }
        for (int i = 0; i < 256; i++)
        {
            var c60 = t60.Chunks[i];
            if (c60 == null) continue;
            int zone = ZoneAt(i);
            if (zone >= 0) hasZone = true;
            var a = acc(j.Map.Id, zone);
            var c12 = t12?.Chunks[i];
            bool changed = false;
            int h = 0, r = 0, tx = 0, lq = 0, ar = 0;
            if (c12 == null) changed = true;
            else
            {
                float max = 0;
                for (int k = 0; k < 145; k++) max = Math.Max(max, MathF.Abs(c60.Position.Z + c60.Heights[k] - (c12.Position.Z + c12.Heights[k])));
                if (max > SameHeight) { h = 1; changed = true; }
                if (max > 1f) r = 1;
                if (!c60.Layers.Select(l => TexKey(l.Texture)).SequenceEqual(c12.Layers.Select(l => TexKey(l.Texture)), StringComparer.OrdinalIgnoreCase)) { tx = 1; changed = true; }
                if (LiquidSig(c60) != LiquidSig(c12)) { lq = 1; changed = true; }
                if (c60.AreaId != c12.AreaId) { ar = 1; changed = true; }
            }
            var newTex = c60.Layers.Where(l => (l.Texture.HasPath || l.Texture.FileDataId is > 0) && (!l.Texture.HasPath || !_s.Client112.Exists(l.Texture.Path!)))
                .Select(l => TexKey(l.Texture)).Distinct().ToList();
            foreach (var key in newTex)
                _newGround.AddOrUpdate(key, _ => (1, new HashSet<string> { WhereLabel(i) }), (_, v) => { lock (v.Where) v.Where.Add(WhereLabel(i)); return (v.Uses + 1, v.Where); });
            lock (a)
            {
                a.Tiles.Add((x, y));
                if (isNewTile) a.NewTiles.Add((x, y));
                a.Chunks++;
                if (changed) { a.Changed++; a.ChangedTiles.Add((x, y)); }
                a.Heights += h; a.Resculpted += r; a.Textures += tx; a.Liquids += lq; a.Areas += ar;
                if (c60.AreaId != 0) a.AreaIds.Add((int)c60.AreaId);
                foreach (var key in newTex) a.NewTextures[key] = a.NewTextures.GetValueOrDefault(key) + 1;
            }
        }
        _tileHasZone[(j.Map.Id, x, y)] = hasZone;

        // placements, by unique ID (1.60 re-numbered many unchanged doodads: same model at the same place counts as unchanged)
        int ChunkAt(System.Numerics.Vector3 p)
        {
            int cx = Math.Clamp((int)((p.X / TerrainMath.TileSize - x) * 16), 0, 15);
            int cy = Math.Clamp((int)((p.Z / TerrainMath.TileSize - y) * 16), 0, 15);
            return cy * 16 + cx;
        }
        void Placements<T>(List<T> now, List<T>? old, Func<T, uint> uid, Func<T, AssetRef> model, Func<T, System.Numerics.Vector3> pos, bool wmo)
        {
            var oldById = old?.GroupBy(uid).ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<uint, T>();
            var seen = new HashSet<uint>();
            var added = new List<T>();
            foreach (var p in now)
            {
                int i = ChunkAt(pos(p));
                var a = acc(j.Map.Id, ZoneAt(i));
                if (oldById.TryGetValue(uid(p), out var o))
                {
                    seen.Add(uid(p));
                    if (!SameModel(model(p), model(o)))
                    {
                        lock (a) { if (wmo) a.WSwapped++; else a.DSwapped++; }
                        var nm = model(p);
                        if (wmo && (!nm.HasPath || !_s.Client112.Exists(nm.Path!)))
                            _landmarks.AddOrUpdate(ModelName(model(o)), _ => (ModelName(nm), 1, new HashSet<string> { WhereLabel(i) }),
                                (_, v) => { lock (v.Where) v.Where.Add(WhereLabel(i)); return (v.NewModel, v.Placements + 1, v.Where); });
                    }
                    else if (System.Numerics.Vector3.Distance(pos(p), pos(o)) > 0.1f) lock (a) { if (wmo) a.WMoved++; else a.DMoved++; }
                    continue;
                }
                added.Add(p);
            }
            var gone = old?.Where(o => !seen.Contains(uid(o))).ToList() ?? new List<T>();
            // re-numbered: the same model at the same place under a new unique ID counts as unchanged
            (string, float, float) Key(T q) => (ModelName(model(q)).ToUpperInvariant(), MathF.Round(pos(q).X, 1), MathF.Round(pos(q).Z, 1));
            var goneAt = gone.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var renumbered = new Dictionary<(string, float, float), int>();
            foreach (var p in added)
            {
                var key = Key(p);
                if (goneAt.TryGetValue(key, out var n) && n > 0)
                {
                    goneAt[key] = n - 1;
                    renumbered[key] = renumbered.GetValueOrDefault(key) + 1;
                    continue;
                }
                int i = ChunkAt(pos(p));
                var a = acc(j.Map.Id, ZoneAt(i));
                var m = model(p);
                bool missing = !m.HasPath || !_s.Client112.Exists(m.Path!);
                lock (a)
                {
                    if (wmo) a.WAdded++; else a.DAdded++;
                    if (missing)
                    {
                        a.MissingPlacements++;
                        a.MissingModels[ModelName(m)] = a.MissingModels.GetValueOrDefault(ModelName(m)) + 1;
                    }
                }
                if (missing)
                    _newModels.AddOrUpdate(ModelName(m), _ => (wmo ? "WMO" : "M2", 1, new HashSet<string> { WhereLabel(i) }), (_, v) => { lock (v.Where) v.Where.Add(WhereLabel(i)); return (v.Kind, v.Placements + 1, v.Where); });
            }
            foreach (var o in gone)
            {
                var key = Key(o);
                if (renumbered.TryGetValue(key, out var n) && n > 0)
                {
                    renumbered[key] = n - 1;
                    continue;
                }
                var a = acc(j.Map.Id, ZoneAt(ChunkAt(pos(o))));
                lock (a) { if (wmo) a.WRemoved++; else a.DRemoved++; }
            }
        }
        Placements(t60.Doodads, t12?.Doodads, d => d.UniqueId, d => d.Model, d => d.Position, false);
        Placements(t60.Wmos, t12?.Wmos, w => w.UniqueId, w => w.Model, w => w.Position, true);
    }

    private string LiquidSig(NeutralChunk c)
    {
        if (c.Liquids.Count == 0) return "";
        return string.Join(",", c.Liquids.Select(l => (l.Kind ?? (l.LiquidType is { } t ? _s.Source.LiquidKindOf(t) : null))?.ToString() ?? "?").Distinct().OrderBy(s => s, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ items

    private static string TileList(IEnumerable<(int X, int Y)> tiles, int max = 12)
    {
        var l = tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        return string.Join(", ", l.Take(max).Select(t => $"{t.X}_{t.Y}")) + (l.Count > max ? $" and {l.Count - max} more" : "");
    }

    private void TerrainItem(MapJob j, TerrainAcc a, IReadOnlyDictionary<int, SourceArea> areas, HashSet<long> areaIds112)
    {
        var m = j.Map;
        string zoneName = a.ZoneId == WholeMap ? m.Name : a.ZoneId == NoZone ? "open sea and tiles without a zone" : areas.TryGetValue(a.ZoneId, out var z) ? z.Name : $"area {a.ZoneId}";
        List<(int X, int Y)> tiles;
        if (a.ZoneId == NoZone)
        {
            // only the tiles that hold no zone at all (the others come with their zones)
            tiles = a.Tiles.Where(t => !_tileHasZone.GetValueOrDefault((m.Id, t.X, t.Y)) && (a.ChangedTiles.Contains(t) || a.NewTiles.Contains(t))).ToList();
            if (tiles.Count == 0) return;
        }
        else tiles = a.Tiles.ToList();
        long bytes = tiles.Sum(t => _tileBytes.GetValueOrDefault((m.Id, t.X, t.Y)));
        var item = new UpdateItem
        {
            Id = a.ZoneId == WholeMap ? $"terrain:{m.Id}" : a.ZoneId == NoZone ? $"terrain:{m.Id}:sea" : $"terrain:{m.Id}:{a.ZoneId}",
            Kind = UpdateKind.ZoneTerrain,
            Title = a.ZoneId == WholeMap ? $"{m.Name}: terrain" : $"{zoneName}: terrain",
            Where = a.ZoneId == WholeMap ? m.Name : $"{zoneName} ({m.Name})",
            Files = tiles.Count, Bytes = bytes, Class = Portability.Ready,
            ClassReason = "the map conversion (Convert map...) converts it into your workspace: ground, texture layers and their blending, water, zones, and the models and buildings your game has.",
            Terrain = new TerrainPick(m.Id, m.Name, m.Directory, a.ZoneId >= 0 ? a.ZoneId : null, a.ZoneId >= 0 ? zoneName : null,
                a.ZoneId == WholeMap ? new List<string>() : tiles.OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => $"{t.X}_{t.Y}").ToList(), a.ZoneId == WholeMap),
        };
        item.Changes.Add($"{N(a.Changed)} of {N(a.Chunks)} ground chunks change: heights on {N(a.Heights)} ({N(a.Resculpted)} re-sculpted by more than a yard), texture layers on {N(a.Textures)}, water on {N(a.Liquids)}, zone IDs on {N(a.Areas)}.");
        if (a.NewTiles.Count > 0) item.Changes.Add($"{N(a.NewTiles.Count)} tiles your map does not have yet are added: {TileList(a.NewTiles)}.");
        if (a.DAdded + a.DRemoved + a.DSwapped + a.DMoved > 0)
            item.Changes.Add($"Trees, rocks and other doodads: {N(a.DAdded)} new, {N(a.DRemoved)} gone, {N(a.DSwapped)} with another model, {N(a.DMoved)} moved.");
        if (a.WAdded + a.WRemoved + a.WSwapped + a.WMoved > 0)
            item.Changes.Add($"Buildings: {N(a.WAdded)} new, {N(a.WRemoved)} gone, {N(a.WSwapped)} replaced by another building, {N(a.WMoved)} moved.");
        if (a.ZoneId != WholeMap) item.Changes.Add("Whole tiles are converted: the parts of neighbouring zones on these tiles change too.");
        if (a.NewTextures.Count > 0) item.Needs.Add($"{N(a.NewTextures.Count)} ground textures your game does not have: converted with it (at most 512 x 512).");
        if (a.MissingPlacements > 0)
            item.Needs.Add($"{N(a.MissingPlacements)} placements of {N(a.MissingModels.Count)} models and buildings 1.12 does not have: left out until the model and building converters exist (most placed: " +
                           string.Join(", ", a.MissingModels.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} x{N(kv.Value)}")) + ").");
        if (a.DSwapped + a.WSwapped > 0) item.Needs.Add("Where 1.60 put another model in place of yours (landmarks, trees), your 1.12 model stays at that place.");
        var newAreas = a.AreaIds.Where(id => !areaIds112.Contains(id) && areas.ContainsKey(id)).ToList();
        if (newAreas.Count > 0)
            item.Needs.Add($"{N(newAreas.Count)} zones or subzones your AreaTable does not have ({string.Join(", ", newAreas.Take(4).Select(id => areas[id].Name))}{(newAreas.Count > 4 ? ", ..." : "")}): new AreaTable rows; the server needs them too (Server data > Check client and server > areas).");
        item.Needs.Add("After the import: build the release patch and Update server maps, so the server's terrain and collision match.");
        item.Facts.Add($"Tiles: {TileList(tiles)}.");
        item.Facts.Add($"Map {m.Id} ({m.Directory}) of {_s.Copy.Name}; your map: World\\Maps\\{j.Dir112}.");
        item.Facts.Add("Not compared: the blending of the texture layers (1.60 repainted about half of them), holes, ground clutter.");
        Add(item);
    }

    private void NewMapItem(MapJob j, List<TerrainAcc> accs, HashSet<long> areaIds112)
    {
        var m = j.Map;
        var a = accs.FirstOrDefault();
        var tiles = j.Wdt.ExistingTiles().ToList();
        long bytes = tiles.Sum(t => _tileBytes.GetValueOrDefault((m.Id, t.X, t.Y)));
        var areas = _s.Source.Areas();
        var zones = areas.Values.Where(x => x.MapId == m.Id).ToList();
        var item = new UpdateItem
        {
            Id = $"map:{m.Id}", Kind = UpdateKind.NewMap, Title = $"{(m.Name.Length > 0 ? m.Name : "Map " + m.Id)} (a map 1.12 does not have)",
            Where = m.Name.Length > 0 ? m.Name : m.Directory, Files = tiles.Count, Bytes = bytes, Class = Portability.Ready,
            ClassReason = "the map conversion converts the whole map into your workspace as a new map (its tiles, a Map row, a default light, its zones).",
            Terrain = new TerrainPick(m.Id, m.Name, m.Directory, null, null, new List<string>(), true),
        };
        item.Changes.Add($"A new map World\\Maps\\{j.Dir112}\\ with {N(tiles.Count)} tiles, a new Map row ({m.Id}), a default light, and {N(zones.Count)} zones as new AreaTable rows.");
        if (a != null)
        {
            if (a.NewTextures.Count > 0) item.Needs.Add($"{N(a.NewTextures.Count)} ground textures your game does not have: converted with it.");
            if (a.MissingPlacements > 0)
                item.Needs.Add($"{N(a.MissingPlacements)} placements of {N(a.MissingModels.Count)} models and buildings 1.12 does not have: left out until the model and building converters exist" +
                               $" ({N(a.DAdded + a.WAdded)} placements in all).");
        }
        item.Needs.Add("The server: the map (map_template) and its areas (area_template) in the server's database, then Update server maps after the release patch.");
        item.Facts.Add($"Map {m.Id}, folder {m.Directory}, {(m.InstanceType switch { 1 => "dungeon", 2 => "raid", 3 => "battleground", _ => "open world" })}{(m.MaxPlayers > 0 ? $", {m.MaxPlayers} players" : "")}; tiles {TileList(tiles)}.");
        if (zones.Count > 0) item.Facts.Add("Zones: " + string.Join(", ", zones.Where(z => z.ParentId == 0).Take(8).Select(z => z.Name)) + ".");
        Add(item);
    }

    private void NewWmoMapItem(SourceMap m)
    {
        Add(new UpdateItem
        {
            Id = $"map:{m.Id}", Kind = UpdateKind.NewMap, Title = $"{(m.Name.Length > 0 ? m.Name : "Map " + m.Id)} (a single building, new)",
            Where = m.Name.Length > 0 ? m.Name : m.Directory, Files = 1, Bytes = Size160(m.WdtFileDataId), Class = Portability.NeedsConverter,
            ClassReason = "the map is one modern building (a WMO-only map): it needs the building converter (modern WMO to 1.12's version 17).",
            Changes = { "With the building converter: a new instance map." },
            Needs = { "The WMO converter (does not exist yet).", "The server: the map in map_template." },
            Facts = { $"Map {m.Id}, folder {m.Directory}." },
            NotYet = "the building converter does not exist yet.",
        });
    }

    private void ModelAndBuildingItems()
    {
        var models = _newModels.Where(kv => kv.Value.Kind == "M2").ToList();
        var wmos = _newModels.Where(kv => kv.Value.Kind == "WMO").ToList();
        if (models.Count > 0)
            Add(new UpdateItem
            {
                Id = "models:placed-new", Kind = UpdateKind.Models, Where = "the 1.60 world", Title = $"New models placed in the 1.60 world ({N(models.Count)})",
                Files = models.Count, Bytes = models.Sum(kv => kv.Key.StartsWith('#') && uint.TryParse(kv.Key[1..], out var f) ? Size160(f) : 0),
                Class = Portability.NeedsConverter,
                ClassReason = "1.12 does not have these models; they are modern M2 files (version 272) with separate skin files: they need the model converter. The map conversion leaves their placements out until then.",
                Changes = { $"With the model converter: {N(models.Sum(kv => kv.Value.Placements))} more trees, rocks and props in the converted zones." },
                Needs = { "The M2 converter (version 272 to 256)." },
                Facts = models.OrderByDescending(kv => kv.Value.Placements).Take(10).Select(kv => $"{kv.Key}: placed {N(kv.Value.Placements)} times ({string.Join(", ", kv.Value.Where.Take(3))}{(kv.Value.Where.Count > 3 ? ", ..." : "")})").ToList(),
                NotYet = "the model converter does not exist yet.",
            });
        if (wmos.Count > 0)
            Add(new UpdateItem
            {
                Id = "wmo:placed-new", Kind = UpdateKind.Buildings, Where = "the 1.60 world", Title = $"New buildings placed in the 1.60 world ({N(wmos.Count)})",
                Files = wmos.Count, Bytes = wmos.Sum(kv => kv.Key.StartsWith('#') && uint.TryParse(kv.Key[1..], out var f) ? Size160(f) : 0),
                Class = Portability.NeedsConverter,
                ClassReason = "1.12 does not have these buildings; they are modern WMO files: they need the building converter.",
                Changes = { $"With the building converter: {N(wmos.Sum(kv => kv.Value.Placements))} more buildings in the converted zones." },
                Needs = { "The WMO converter (modern to 1.12's version 17)." },
                Facts = wmos.OrderByDescending(kv => kv.Value.Placements).Take(10).Select(kv => $"{kv.Key}: placed {N(kv.Value.Placements)} times ({string.Join(", ", kv.Value.Where.Take(3))})").ToList(),
                NotYet = "the building converter does not exist yet.",
            });
        foreach (var (old, v) in _landmarks.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            Add(new UpdateItem
            {
                Id = "wmo:replaced:" + IdOf(old), Kind = UpdateKind.Buildings, Where = string.Join(", ", v.Where.Take(3)),
                Title = $"Replaced landmark: {Path.GetFileNameWithoutExtension(old)}", Files = 1,
                Bytes = v.NewModel.StartsWith('#') && uint.TryParse(v.NewModel[1..], out var f) ? Size160(f) : 0, Class = Portability.NeedsConverter,
                ClassReason = "1.60 put a new building in place of this one (same placement); it is a modern WMO without a 1.12 version: it needs the building converter.",
                Changes = { $"With the building converter: the 1.60 building instead of {old} ({N(v.Placements)} placements)." },
                Needs = { "The WMO converter. Until then the map conversion keeps your 1.12 building there." },
                Facts = { $"Your building: {old}; 1.60's: {v.NewModel}; in {string.Join(", ", v.Where)}." },
                NotYet = "the building converter does not exist yet.",
            });
        var ground = _newGround.ToList();
        if (ground.Count > 0)
            Add(new UpdateItem
            {
                Id = "ground:new", Kind = UpdateKind.GroundTextures, Where = "the 1.60 terrain", Title = $"New ground textures of the 1.60 terrain ({N(ground.Count)})",
                Files = ground.Count, Bytes = ground.Sum(kv => kv.Key.StartsWith('#') && uint.TryParse(kv.Key[1..], out var f) ? Size160(f) : 0), Class = Portability.Ready,
                ClassReason = "ground textures 1.12 does not have; the map conversion converts them (at most 512 x 512, with their _s file) together with the zones that use them.",
                Changes = { "New kinds of ground in the converted zones." },
                Needs = { "Pick the zones that use them (Zone terrain): they come with the zone." },
                Facts = ground.OrderByDescending(kv => kv.Value.Uses).Take(10).Select(kv => $"{kv.Key}: {N(kv.Value.Uses)} chunk layers ({string.Join(", ", kv.Value.Where.Take(3))}{(kv.Value.Where.Count > 3 ? ", ..." : "")})").ToList(),
                NotYet = "not on its own: pick the zones that use them; the map conversion brings them.",
            });
    }
}
