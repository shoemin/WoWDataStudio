using System.Numerics;
using WoWData.Core.Imaging;

namespace WoWData.Core.Maps;

public enum BreakdownKind { Terrain, Chunk, Texture, ModelGroup, Doodad, Building, BuildingPart, Water, SoundEmitter, Shadows, Area }

/// <summary>One entry of the tile breakdown: a piece of the tile (the terrain, a chunk, a ground texture, a model and its
/// placements, a building, water, a sound emitter...), with what the views need to show it.</summary>
public sealed class BreakdownItem
{
    public BreakdownKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<BreakdownItem> Children { get; } = new();
    /// <summary>The chunk of a chunk entry or the chunk a placement / emitter stands on (-1: none).</summary>
    public int Chunk { get; init; } = -1;
    /// <summary>Game path of the model, building or texture (null for terrain-only entries).</summary>
    public string? Path { get; init; }
    /// <summary>Unique ids of the placements this entry stands for (a model group: all of them).</summary>
    public List<uint> DoodadUids { get; } = new();
    public List<uint> WmoUids { get; } = new();
    /// <summary>The chunks this entry covers (what is highlighted in the 3D view).</summary>
    public HashSet<int> Chunks { get; } = new();
    public Vector3? WorldPosition { get; init; }
    /// <summary>Texture: the name in the tile's MTEX chunk (the layer this entry stands for). For the "_s" child of a ground
    /// texture it differs from <see cref="Path"/>.</summary>
    public string? LayerPath { get; init; }
    /// <summary>Ground texture: its specular variant "&lt;name&gt;_s.blp" when the game has one (0.6.1).</summary>
    public string? SpecularPath { get; init; }
    /// <summary>Texture: true for the "_s" specular file of a ground texture.</summary>
    public bool IsSpecularFile { get; init; }
    /// <summary>Texture: share of the tile's ground it covers (0..1).</summary>
    public double Coverage { get; init; }
    /// <summary>A placement this tile's file lists that stands on a neighbouring tile.</summary>
    public bool OnNeighbour { get; init; }
    public int Count { get; init; } = 1;

    /// <summary>The key of the entry in the Browse list ("file:&lt;path&gt;"), or null.</summary>
    public string? BrowseKey => Kind is BreakdownKind.Texture or BreakdownKind.ModelGroup or BreakdownKind.Doodad or BreakdownKind.Building && Path != null ? "file:" + Path : null;

    public bool IsModel => Kind is BreakdownKind.ModelGroup or BreakdownKind.Doodad;
    public bool IsBuilding => Kind == BreakdownKind.Building;
    public bool IsTerrain => Kind is BreakdownKind.Terrain or BreakdownKind.Chunk;

    public bool Matches(string query) =>
        query.Length == 0 || Title.Contains(query, StringComparison.OrdinalIgnoreCase) || Detail.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    public override string ToString() => Title;
}

public sealed class BreakdownGroup
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; set; } = "";
    public List<BreakdownItem> Items { get; } = new();
}

/// <summary>
/// "Break down this tile": every piece of one map tile as a grouped list. Terrain (the tile mesh and its 256 chunks with
/// holes, heights and zone), ground textures (each with its share of the ground), trees and objects (M2 doodads grouped
/// by model, with each placement), buildings (WMO placements with their groups and doodad sets), water (MCLQ per chunk)
/// and the rest (sound emitters MCSE, the shadow map, the zone ids of the chunks). Built from the same reader as the
/// 3D view (<see cref="TerrainTile"/>) and the file itself (MCSE, MODF doodad sets).
/// </summary>
public sealed class TileBreakdown
{
    public string MapName { get; init; } = "";
    public string MapDirectory { get; init; } = "";
    public int TileX { get; init; }
    public int TileY { get; init; }
    public TerrainTile Tile { get; init; } = null!;
    public List<BreakdownGroup> Groups { get; } = new();

    public BreakdownGroup Group(string key) => Groups.First(g => g.Key == key);

    public IEnumerable<BreakdownItem> All()
    {
        foreach (var g in Groups)
            foreach (var i in g.Items)
            {
                yield return i;
                foreach (var c in i.Children)
                {
                    yield return c;
                    foreach (var cc in c.Children) yield return cc;
                }
            }
    }

    /// <summary>The tile a placement stands on (placement coordinates a, b as MapScanner locates it).</summary>
    public static (int X, int Y, int Chunk) Locate(Vector3 placement)
    {
        float a = placement.X / TerrainMath.TileSize, b = placement.Z / TerrainMath.TileSize;
        int tx = (int)MathF.Floor(a), ty = (int)MathF.Floor(b);
        int cx = Math.Clamp((int)((a - tx) * 16), 0, 15), cy = Math.Clamp((int)((b - ty) * 16), 0, 15);
        return (tx, ty, cy * 16 + cx);
    }

    /// <param name="data">the ADT file</param>
    /// <param name="read">game path -> bytes (building roots)</param>
    /// <param name="areaName">area id -> name</param>
    /// <param name="soundName">SoundEntries id -> name (or null)</param>
    /// <param name="exists">game path -> whether the game (or your workspace) has the file: ground textures get their
    /// "_s" specular variant as a second entry (null: not looked up)</param>
    public static TileBreakdown Build(string mapName, string mapDirectory, int x, int y, byte[] data, Func<string, byte[]?> read, Func<int, string> areaName, Func<int, string?>? soundName = null,
        Func<string, bool>? exists = null)
    {
        var tile = TerrainTile.Load(data, mapDirectory, x, y);
        var adt = new AdtFile(data);
        var bd = new TileBreakdown { MapName = mapName, MapDirectory = mapDirectory, TileX = x, TileY = y, Tile = tile };
        string Pos(Vector3 w) => $"{w.X:0.#}, {w.Y:0.#}, {w.Z:0.#}";

        // ---- terrain
        var terrain = new BreakdownGroup { Key = "terrain", Title = "Terrain" };
        var (lo, hi) = tile.HeightRange;
        int holes = tile.AllChunks.Count(c => c.Holes != 0);
        var mesh = new BreakdownItem
        {
            Kind = BreakdownKind.Terrain, Title = "Terrain mesh (the ground of the tile)", Count = tile.AllChunks.Count(),
            Detail = $"{tile.AllChunks.Count()} chunks of 33 x 33 yards, heights {lo:0.#} to {hi:0.#} yards" + (holes > 0 ? $", holes in {holes} chunks" : ", no holes"),
        };
        foreach (var c in tile.AllChunks.OrderBy(c => c.Index))
        {
            int holeQuads = 0;
            for (int q = 0; q < 64; q++) if (c.IsHole(q % 8, q / 8)) holeQuads++;
            var parts = new List<string> { $"heights {c.MinHeight:0.#} to {c.MaxHeight:0.#}", c.AreaId == 0 ? "no zone" : areaName(c.AreaId), $"{c.Layers.Count} texture layer{(c.Layers.Count == 1 ? "" : "s")}" };
            if (holeQuads > 0) parts.Add($"hole: {holeQuads} of 64 squares");
            if (c.Liquids.Count > 0) parts.Add(string.Join("/", c.Liquids.Select(l => l.Kind.ToString().ToLowerInvariant())));
            var item = new BreakdownItem { Kind = BreakdownKind.Chunk, Title = $"Chunk {c.X},{c.Y}", Detail = string.Join(" · ", parts), Chunk = c.Index };
            item.Chunks.Add(c.Index);
            mesh.Children.Add(item);
            mesh.Chunks.Add(c.Index);
        }
        terrain.Items.Add(mesh);
        terrain.Summary = mesh.Detail;
        bd.Groups.Add(terrain);

        // ---- ground textures
        var textures = new BreakdownGroup { Key = "textures", Title = "Ground textures" };
        var cover = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var chunksOf = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var baseOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in tile.AllChunks)
            foreach (var (path, w) in ChunkWeights(c))
            {
                double share = w.Sum(v => (double)v) / (64 * 64) / 256.0;
                cover[path] = cover.GetValueOrDefault(path) + share;
                if (!chunksOf.TryGetValue(path, out var set)) chunksOf[path] = set = new HashSet<int>();
                set.Add(c.Index);
                if (c.Layers.Count > 0 && string.Equals(c.Layers[0], path, StringComparison.OrdinalIgnoreCase)) baseOf[path] = baseOf.GetValueOrDefault(path) + 1;
            }
        foreach (var path in tile.Textures.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(p => cover.GetValueOrDefault(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var resolved = Mpq.ClientData.ResolveName(path);
            double share = cover.GetValueOrDefault(path);
            var set = chunksOf.GetValueOrDefault(path) ?? new HashSet<int>();
            var spec = GroundTextures.SpecularName(resolved);
            bool hasSpec = spec != null && exists != null && exists(spec);
            var detail = set.Count == 0 ? "listed in the file but not painted on any chunk" :
                $"{share * 100:0.#}% of the ground · in {set.Count} chunk{(set.Count == 1 ? "" : "s")}" + (baseOf.GetValueOrDefault(path) > 0 ? $" · base layer of {baseOf[path]}" : "");
            var item = new BreakdownItem
            {
                Kind = BreakdownKind.Texture, Title = System.IO.Path.GetFileName(resolved) + (hasSpec ? $" + {System.IO.Path.GetFileName(spec)}" : ""), Path = resolved, LayerPath = resolved,
                SpecularPath = hasSpec ? spec : null, Coverage = share,
                Detail = detail + (hasSpec ? $" · two files: the game draws {System.IO.Path.GetFileName(spec)} when terrain specular is on, {System.IO.Path.GetFileName(resolved)} when it is off" : ""),
            };
            item.Chunks.UnionWith(set);
            if (hasSpec)
            {
                var b = new BreakdownItem
                {
                    Kind = BreakdownKind.Texture, Title = System.IO.Path.GetFileName(resolved), Path = resolved, LayerPath = resolved, SpecularPath = spec, Coverage = share,
                    Detail = "the file the map names: drawn when terrain specular is off (Config.wtf specular 0 or pixelShaders 0)",
                };
                var sp = new BreakdownItem
                {
                    Kind = BreakdownKind.Texture, Title = System.IO.Path.GetFileName(spec!), Path = spec, LayerPath = resolved, SpecularPath = spec, IsSpecularFile = true, Coverage = share,
                    Detail = "its specular variant: drawn instead when terrain specular is on (specular 1 and pixelShaders 1); its alpha channel is the shine",
                };
                b.Chunks.UnionWith(set);
                sp.Chunks.UnionWith(set);
                item.Children.Add(b);
                item.Children.Add(sp);
            }
            textures.Items.Add(item);
        }
        textures.Summary = $"{textures.Items.Count} textures, up to 4 layers per chunk";
        bd.Groups.Add(textures);

        // ---- doodads (M2), grouped by model
        var doodads = new BreakdownGroup { Key = "doodads", Title = "Trees and objects (doodads)" };
        foreach (var g in tile.Doodads.GroupBy(d => d.Model, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            int inside = list.Count(d => Locate(d.Position) is var l && l.X == x && l.Y == y);
            var grp = new BreakdownItem
            {
                Kind = BreakdownKind.ModelGroup, Title = $"{System.IO.Path.GetFileNameWithoutExtension(g.Key)} ×{list.Count}", Path = g.Key, Count = list.Count,
                Detail = (inside == list.Count ? "" : $"{list.Count - inside} on a neighbouring tile · ") + System.IO.Path.GetDirectoryName(g.Key),
            };
            int n = 0;
            foreach (var d in list)
            {
                var loc = Locate(d.Position);
                bool own = loc.X == x && loc.Y == y;
                var item = new BreakdownItem
                {
                    Kind = BreakdownKind.Doodad, Title = $"{System.IO.Path.GetFileNameWithoutExtension(g.Key)} #{++n}", Path = g.Key, Chunk = own ? loc.Chunk : -1,
                    WorldPosition = d.WorldPosition, OnNeighbour = !own,
                    Detail = $"at {Pos(d.WorldPosition)} · turned {d.Rotation.X:0.#}, {d.Rotation.Y:0.#}, {d.Rotation.Z:0.#}° · scale {d.Scale:0.###} · unique id {d.Uid}" +
                             (own ? $" · chunk {loc.Chunk % 16},{loc.Chunk / 16}" : $" · stands on tile {loc.X},{loc.Y}"),
                };
                item.DoodadUids.Add(d.Uid);
                grp.DoodadUids.Add(d.Uid);
                if (own) item.Chunks.Add(loc.Chunk);
                grp.Children.Add(item);
            }
            doodads.Items.Add(grp);
        }
        doodads.Summary = $"{tile.Doodads.Count} placements of {doodads.Items.Count} models";
        bd.Groups.Add(doodads);

        // ---- buildings (WMO)
        var buildings = new BreakdownGroup { Key = "buildings", Title = "Buildings (WMO)" };
        var roots = new Dictionary<string, WmoRootInfo?>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in tile.Wmos)
        {
            if (!roots.TryGetValue(w.Model, out var info))
            {
                try { info = read(w.Model) is { } rb ? WmoGeometry.ReadRoot(rb) : null; }
                catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { info = null; }
                roots[w.Model] = info;
            }
            var loc = Locate(w.Position);
            bool own = loc.X == x && loc.Y == y;
            string setName = info != null && w.DoodadSet < info.DoodadSets.Count ? $" \"{info.DoodadSets[w.DoodadSet].Name}\"" : "";
            var b = new BreakdownItem
            {
                Kind = BreakdownKind.Building, Title = System.IO.Path.GetFileNameWithoutExtension(w.Model), Path = w.Model, WorldPosition = w.WorldPosition, Chunk = own ? loc.Chunk : -1,
                OnNeighbour = !own,
                Detail = $"at {Pos(w.WorldPosition)} · turned {w.Rotation.X:0.#}, {w.Rotation.Y:0.#}, {w.Rotation.Z:0.#}° · doodad set {w.DoodadSet}{setName} · unique id {w.Uid}" +
                         (info == null ? " · the building file was not found" : $" · {info.Groups.Count} groups") + (own ? "" : $" · stands on tile {loc.X},{loc.Y}"),
            };
            b.WmoUids.Add(w.Uid);
            if (own) b.Chunks.Add(loc.Chunk);
            if (info != null)
            {
                for (int gi = 0; gi < info.Groups.Count; gi++)
                {
                    var (name, flags) = info.Groups[gi];
                    b.Children.Add(new BreakdownItem
                    {
                        Kind = BreakdownKind.BuildingPart, Title = $"Group {gi}: {name}", Path = w.Model,
                        Detail = ((flags & 0x2000) != 0 ? "indoor" : "outdoor") + $" · file {System.IO.Path.GetFileNameWithoutExtension(w.Model)}_{gi:D3}.wmo",
                    });
                }
                for (int si = 0; si < info.DoodadSets.Count; si++)
                {
                    var (name, start, count) = info.DoodadSets[si];
                    string role = si == 0 ? "always shown" : si == w.DoodadSet ? "shown: this placement's set" : "not shown here";
                    b.Children.Add(new BreakdownItem
                    {
                        Kind = BreakdownKind.BuildingPart, Title = $"Doodad set {si}: {name}", Path = w.Model, Count = count,
                        Detail = $"{count} doodad{(count == 1 ? "" : "s")} inside the building · {role}",
                    });
                }
            }
            buildings.Items.Add(b);
        }
        buildings.Summary = $"{tile.Wmos.Count} placements of {tile.Wmos.Select(w => w.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()} buildings";
        bd.Groups.Add(buildings);

        // ---- water
        var water = new BreakdownGroup { Key = "water", Title = "Water (MCLQ)" };
        foreach (var c in tile.AllChunks.Where(c => c.Liquids.Count > 0).OrderBy(c => c.Index))
            foreach (var l in c.Liquids)
            {
                int squares = l.Tiles.Count(t => t);
                var item = new BreakdownItem
                {
                    Kind = BreakdownKind.Water, Title = $"{l.Kind} in chunk {c.X},{c.Y}", Chunk = c.Index,
                    Detail = $"{squares} of 64 squares · surface {l.Min:0.#} to {l.Max:0.#} yards",
                };
                item.Chunks.Add(c.Index);
                water.Items.Add(item);
            }
        int waterChunks = tile.AllChunks.Count(c => c.Liquids.Count > 0);
        water.Summary = waterChunks == 0 ? "no water" : $"liquid in {waterChunks} chunks ({string.Join(", ", tile.AllChunks.SelectMany(c => c.Liquids).Select(l => l.Kind.ToString().ToLowerInvariant()).Distinct())})";
        bd.Groups.Add(water);

        // ---- other: sound emitters, shadows, zones
        var other = new BreakdownGroup { Key = "other", Title = "Other" };
        int emitters = 0;
        foreach (var c in adt.Mcnk)
        {
            if (c == null || !c.HasHeader || c["nSndEmitters"] == 0) continue;
            long p = c.DataOffset("ofsSndEmitters");
            int idx = c.IndexX < 16 && c.IndexY < 16 ? c.IndexY * 16 + c.IndexX : c.Index;
            for (int e = 0; e < c["nSndEmitters"] && p + (e + 1) * MapBytes.McseSize <= data.Length && e < 64; e++)
            {
                long q = p + e * MapBytes.McseSize;
                int sound = (int)MapBytes.U32(data, q + 4);
                var w = new Vector3(MapBytes.F32(data, q + 8), MapBytes.F32(data, q + 12), MapBytes.F32(data, q + 16));
                var name = soundName?.Invoke(sound);
                var item = new BreakdownItem
                {
                    Kind = BreakdownKind.SoundEmitter, Title = $"Sound emitter: {name ?? $"sound {sound}"}", Chunk = idx, WorldPosition = w,
                    Detail = $"ambient sound {sound} (SoundEntries) · at {Pos(w)} · chunk {idx % 16},{idx / 16} (MCSE)",
                };
                item.Chunks.Add(idx);
                other.Items.Add(item);
                emitters++;
            }
        }
        var shadowChunks = tile.AllChunks.Where(c => c.Shadow != null).Select(c => c.Index).ToList();
        var sh = new BreakdownItem
        {
            Kind = BreakdownKind.Shadows, Title = "Shadow map (MCSH)",
            Detail = shadowChunks.Count == 0 ? "no baked shadows" : $"baked shadows in {shadowChunks.Count} chunks (64 x 64 per chunk)",
        };
        sh.Chunks.UnionWith(shadowChunks);
        other.Items.Add(sh);
        foreach (var g in tile.AllChunks.GroupBy(c => c.AreaId).OrderByDescending(g => g.Count()))
        {
            var item = new BreakdownItem
            {
                Kind = BreakdownKind.Area, Title = $"Zone: {(g.Key == 0 ? "(no zone)" : areaName(g.Key))}", Count = g.Count(),
                Detail = $"area id {g.Key} · {g.Count()} of 256 chunks",
            };
            item.Chunks.UnionWith(g.Select(c => c.Index));
            other.Items.Add(item);
        }
        other.Summary = $"{emitters} sound emitter{(emitters == 1 ? "" : "s")}, shadows in {shadowChunks.Count} chunks, {tile.AllChunks.Select(c => c.AreaId).Distinct().Count()} zone id(s)";
        bd.Groups.Add(other);
        return bd;
    }

    // ------------------------------------------------------------------ texture weights and pictures

    /// <summary>How much each layer's texture shows at every one of the 64 x 64 alpha texels of a chunk, the way the
    /// game blends: layer 0 under the others, each layer k on top with its alpha map:
    /// w3 = a3, w2 = a2 (1 - a3), w1 = a1 (1 - a2)(1 - a3), w0 = (1 - a1)(1 - a2)(1 - a3). Layers that use the same
    /// texture are added.</summary>
    public static List<(string Path, float[] Weight)> ChunkWeights(TerrainChunk c)
    {
        var result = new List<(string, float[])>();
        int n = Math.Min(4, c.Layers.Count);
        if (n == 0) return result;
        var rest = new float[64 * 64];
        Array.Fill(rest, 1f);
        var weights = new float[n][];
        for (int L = n - 1; L >= 0; L--)
        {
            var w = new float[64 * 64];
            var a = L == 0 ? null : c.Alpha.Count > L ? c.Alpha[L] : null;
            for (int i = 0; i < w.Length; i++)
            {
                float al = L == 0 ? 1f : a == null ? 0f : a[i] / 255f;
                w[i] = al * rest[i];
                rest[i] *= 1 - al;
            }
            weights[L] = w;
        }
        for (int L = 0; L < n; L++)
        {
            var path = c.Layers[L];
            if (path.Length == 0) continue;
            int at = result.FindIndex(r => string.Equals(r.Item1, path, StringComparison.OrdinalIgnoreCase));
            if (at < 0) result.Add((path, weights[L]));
            else for (int i = 0; i < 4096; i++) result[at].Item2[i] += weights[L][i];
        }
        return result;
    }

    /// <summary>Where a ground texture is painted: its weight over the tile (north up), <paramref name="size"/> pixels
    /// square (a multiple of 16; 1024 = every alpha texel). Grey levels 0..255.</summary>
    public static byte[] WeightMap(TerrainTile tile, string texture, int size = 1024)
    {
        var map = new byte[size * size];
        int per = size / 16;
        foreach (var c in tile.AllChunks)
            foreach (var (path, w) in ChunkWeights(c))
            {
                if (!string.Equals(path, texture, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Mpq.ClientData.ResolveName(path), texture, StringComparison.OrdinalIgnoreCase)) continue;
                for (int y = 0; y < per; y++)
                    for (int x = 0; x < per; x++)
                    {
                        int sx = x * 64 / per, sy = y * 64 / per;
                        int o = (c.Y * per + y) * size + c.X * per + x;
                        map[o] = (byte)Math.Clamp((int)MathF.Round(map[o] + w[sy * 64 + sx] * 255f), 0, 255);
                    }
            }
        return map;
    }

    /// <summary>The coverage map of a texture as a picture: dark where it is not painted, bright green-yellow where it is,
    /// with the chunk grid faintly drawn.</summary>
    public static RgbaImage CoverageImage(TerrainTile tile, string texture, int size = 512)
    {
        var w = WeightMap(tile, texture, size);
        var img = new RgbaImage(size, size);
        int per = size / 16;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float t = w[y * size + x] / 255f;
                int o = img.Offset(x, y);
                bool grid = x % per == 0 || y % per == 0;
                img.Pixels[o] = (byte)(28 + t * (220 - 28) + (grid ? 14 : 0));
                img.Pixels[o + 1] = (byte)(32 + t * (230 - 32) + (grid ? 14 : 0));
                img.Pixels[o + 2] = (byte)(40 + t * (70 - 40) + (grid ? 14 : 0));
                img.Pixels[o + 3] = 255;
            }
        return img;
    }

    /// <summary>The heights of a tile as a heat map (north up; blue low, green, yellow, red high; holes dark), with the
    /// given chunks outlined in white and the rest dimmed when <paramref name="highlight"/> is not empty.</summary>
    public static RgbaImage HeightMap(TerrainTile tile, IReadOnlyCollection<int>? highlight = null, int size = 512)
    {
        var g = tile.OuterGrid();
        var (lo, hi) = tile.HeightRange;
        float range = Math.Max(1f, hi - lo);
        var img = new RgbaImage(size, size);
        int per = size / 16;
        bool any = highlight is { Count: > 0 };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = x * 128f / size, fy = y * 128f / size;
                int ix = Math.Min(127, (int)fx), iy = Math.Min(127, (int)fy);
                float dx = fx - ix, dy = fy - iy;
                float h00 = g[iy * 129 + ix], h01 = g[iy * 129 + ix + 1], h10 = g[(iy + 1) * 129 + ix], h11 = g[(iy + 1) * 129 + ix + 1];
                float h = (h00 * (1 - dx) + h01 * dx) * (1 - dy) + (h10 * (1 - dx) + h11 * dx) * dy;
                int o = img.Offset(x, y);
                int chunk = (y / per) * 16 + x / per;
                var ch = tile.Chunks[chunk];
                bool hole = ch != null && ch.IsHole((x % per) * 8 / per, (y % per) * 8 / per);
                var (r, gg, b) = float.IsNaN(h) || hole ? (30f, 30f, 34f) : Heat((h - lo) / range);
                float shade = 1f;
                if (!float.IsNaN(h01) && !float.IsNaN(h10) && !hole) shade = Math.Clamp(1f + ((h00 - h01) + (h00 - h10)) * 0.025f, 0.7f, 1.2f);
                bool lit = !any || highlight!.Contains(chunk);
                float dim = lit ? 1f : 0.35f;
                img.Pixels[o] = (byte)Math.Clamp(r * shade * dim, 0, 255);
                img.Pixels[o + 1] = (byte)Math.Clamp(gg * shade * dim, 0, 255);
                img.Pixels[o + 2] = (byte)Math.Clamp(b * shade * dim, 0, 255);
                img.Pixels[o + 3] = 255;
                if (any && lit)
                {
                    // outline: a highlighted chunk next to a non-highlighted one
                    int cx = x / per, cy = y / per, lx = x % per, ly = y % per;
                    bool edge = (lx == 0 && (cx == 0 || !highlight!.Contains(chunk - 1))) || (lx == per - 1 && (cx == 15 || !highlight!.Contains(chunk + 1))) ||
                                (ly == 0 && (cy == 0 || !highlight!.Contains(chunk - 16))) || (ly == per - 1 && (cy == 15 || !highlight!.Contains(chunk + 16)));
                    if (edge) img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = 255;
                }
            }
        return img;
    }

    private static (float R, float G, float B) Heat(float t)
    {
        t = Math.Clamp(t, 0, 1);
        // blue -> cyan -> green -> yellow -> red
        var stops = new (float T, float R, float G, float B)[] { (0, 40, 70, 190), (0.25f, 40, 170, 200), (0.5f, 70, 180, 70), (0.75f, 230, 210, 60), (1, 210, 60, 40) };
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].T)
            {
                var (t0, r0, g0, b0) = stops[i - 1];
                var (t1, r1, g1, b1) = stops[i];
                float u = (t - t0) / (t1 - t0);
                return (r0 + (r1 - r0) * u, g0 + (g1 - g0) * u, b0 + (b1 - b0) * u);
            }
        return (210, 60, 40);
    }
}
