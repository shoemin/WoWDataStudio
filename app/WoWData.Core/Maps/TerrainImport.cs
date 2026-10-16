using System.Buffers.Binary;
using System.Text.Json;
using WoWData.Core.Mpq;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Maps;

/// <summary>One tile object as Blender exported it ("Export terrain for WoW Data Studio").</summary>
public sealed class TerrainEditTile
{
    public string Id { get; init; } = "";
    public string ObjectName { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>wow_vid per vertex (grid id + 1; 0 = added in Blender), or null when the mesh lost its ids.</summary>
    public int[]? Vids { get; init; }
    /// <summary>Mesh-local positions, x y z per vertex.</summary>
    public float[] Co { get; init; } = Array.Empty<float>();
    /// <summary>The object's matrix_world, row-major (Blender: world = M @ local).</summary>
    public double[] Matrix { get; init; } = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    public int VertexCount => Co.Length / 3;
}

/// <summary>wowdata_terrain_edit.json + its .bin files: what the add-on's "Export terrain for WoW Data Studio" writes.</summary>
public sealed class TerrainEdit
{
    public const string FileName = "wowdata_terrain_edit.json";
    public const string FormatName = "wowdata-terrain-edit";
    public string Path { get; init; } = "";
    public string Addon { get; init; } = "";
    public string Blender { get; init; } = "";
    public List<TerrainEditTile> Tiles { get; } = new();
    public List<string> Notes { get; } = new();

    public static TerrainEdit Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        if (!r.TryGetProperty("format", out var f) || f.GetString() != FormatName) throw new InvalidDataException($"{path} is not a terrain export of the WoW Data Studio add-on.");
        var e = new TerrainEdit
        {
            Path = path, Addon = r.TryGetProperty("addon", out var a) ? a.GetString() ?? "" : "", Blender = r.TryGetProperty("blender", out var bl) ? bl.GetString() ?? "" : "",
        };
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        foreach (var t in r.GetProperty("tiles").EnumerateArray())
        {
            var file = System.IO.Path.Combine(dir, t.GetProperty("file").GetString()!.Replace('/', System.IO.Path.DirectorySeparatorChar));
            int n = t.GetProperty("vertices").GetInt32();
            bool ids = t.TryGetProperty("has_vid", out var hv) && hv.GetBoolean();
            var bytes = File.ReadAllBytes(file);
            int need = (ids ? n * 4 : 0) + n * 12;
            if (bytes.Length < need) throw new InvalidDataException($"{file} is shorter than its {n} vertices.");
            int[]? vids = null;
            int p = 0;
            if (ids)
            {
                vids = new int[n];
                for (int i = 0; i < n; i++) vids[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
                p = n * 4;
            }
            var co = new float[n * 3];
            for (int i = 0; i < n * 3; i++) co[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(p + i * 4));
            var m = t.TryGetProperty("matrix_world", out var mw) ? mw.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
            e.Tiles.Add(new TerrainEditTile
            {
                Id = t.GetProperty("id").GetString() ?? "", ObjectName = t.TryGetProperty("object", out var on) ? on.GetString() ?? "" : "",
                X = t.GetProperty("x").GetInt32(), Y = t.GetProperty("y").GetInt32(), Vids = vids, Co = co, Matrix = m is { Length: 16 } ? m : new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            });
        }
        if (r.TryGetProperty("notes", out var notes)) foreach (var x in notes.EnumerateArray()) e.Notes.Add(x.GetString() ?? "");
        return e;
    }

    /// <summary>The add-on's export written from C#, the way Blender writes it (every vertex in grid order with its
    /// wow_vid, float32 local coordinates, the object at the tile's location), with an optional scripted change per
    /// vertex: the tests and the screenshot run use it to stand in for an edit made in Blender.</summary>
    public static string WriteScripted(string packageDir, Func<TerrainPackageTile, int, System.Numerics.Vector3, System.Numerics.Vector3?>? change = null,
        Func<TerrainPackageTile, bool>? include = null, int addVertices = 0)
    {
        var man = TerrainPackageManifest.FromJson(File.ReadAllText(System.IO.Path.Combine(packageDir, TerrainPackageManifest.FileName)))
                  ?? throw new InvalidDataException("not a terrain package");
        var outDir = System.IO.Path.Combine(packageDir, "blender_export");
        Directory.CreateDirectory(outDir);
        var tiles = new List<object>();
        float u = TerrainMath.UnitSize;
        foreach (var t in man.Tiles.Where(t => include?.Invoke(t) ?? true))
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(packageDir, t.HeightsFile.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            int n = TerrainGrid.Count + addVertices;
            var bin = new byte[n * 16];
            for (int id = 0; id < n; id++)
            {
                System.Numerics.Vector3 co;
                if (id < TerrainGrid.Count)
                {
                    var gp = TerrainGrid.GridPosition(id);
                    co = new System.Numerics.Vector3(gp.X * u, -gp.Y * u, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(id * 4)));
                    if (change?.Invoke(t, id, co) is { } c2) co = c2;
                }
                else co = new System.Numerics.Vector3(10, -10, 50);
                BinaryPrimitives.WriteInt32LittleEndian(bin.AsSpan(id * 4), id < TerrainGrid.Count ? id + 1 : 0);
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(n * 4 + id * 12), co.X);
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(n * 4 + id * 12 + 4), co.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(n * 4 + id * 12 + 8), co.Z);
            }
            var file = $"tile_{t.X}_{t.Y}.bin";
            File.WriteAllBytes(System.IO.Path.Combine(outDir, file), bin);
            tiles.Add(new Dictionary<string, object>
            {
                ["id"] = t.Id, ["object"] = t.Id, ["x"] = t.X, ["y"] = t.Y, ["file"] = file, ["vertices"] = n, ["has_vid"] = true,
                ["matrix_world"] = new[] { 1.0, 0, 0, t.Location[0], 0, 1, 0, t.Location[1], 0, 0, 1, 0, 0, 0, 0, 1 },
            });
        }
        var path = System.IO.Path.Combine(outDir, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object> { ["format"] = FormatName, ["format_version"] = 1, ["addon"] = "scripted", ["blender"] = "none", ["tiles"] = tiles }));
        return path;
    }
}

/// <summary>An edge between two tiles whose shared vertices do not have the same heights.</summary>
public sealed class SeamIssue
{
    public (int X, int Y) A { get; init; }
    public (int X, int Y) B { get; init; }
    /// <summary>"east", "south", "south-east corner", ... (the side of A).</summary>
    public string Edge { get; init; } = "";
    public int Vertices { get; set; }
    public float MaxDifference { get; set; }
    /// <summary>B was not part of the import (an existing neighbour).</summary>
    public bool ExistingNeighbour { get; init; }
    public string Text => $"Tile {A.X},{A.Y} and tile {B.X},{B.Y}{(ExistingNeighbour ? " (not edited)" : "")}: {Vertices} {(Vertices == 1 ? "vertex" : "vertices")} on the {Edge} edge differ by up to {MaxDifference:0.##} yards";
}

/// <summary>
/// "Import edited terrain...": reads what the add-on exported, maps every Blender vertex back to its grid vertex of its
/// tile (by the wow_vid attribute, or by position when the mesh lost it), keeps only height changes (sideways moves are
/// put back on the grid, added vertices are ignored, deleted ones keep their height, each listed in words), checks the
/// shared edges against the other imported tiles and the existing neighbours (and syncs them on request: the edited side
/// wins), writes the heights into every chunk copy of a vertex, recomputes the normals (MCNR) of every vertex whose
/// neighbourhood changed, keeps every other byte, and validates. The result is a normal map import (review, compare,
/// one change set).
/// </summary>
public static class TerrainImporter
{
    public const string NewLabel = "Your terrain from Blender";
    private const float HeightTolerance = 5e-4f;      // yards: Blender keeps float32 heights exactly; anything above is an edit
    private const float SideTolerance = 0.01f;        // yards: further than this sideways = moved
    private const float SeamTolerance = 1e-3f;

    /// <summary>The manifest and the edit for a path the user picked: the package folder, its blender_export folder, or
    /// either JSON file.</summary>
    public static (string Manifest, string? Edit) Locate(string path)
    {
        string dir = File.Exists(path) ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))! : System.IO.Path.GetFullPath(path);
        string? manifest = null, edit = null;
        if (File.Exists(path) && System.IO.Path.GetFileName(path).Equals(TerrainEdit.FileName, StringComparison.OrdinalIgnoreCase)) edit = path;
        var d = new DirectoryInfo(dir);
        for (int up = 0; up < 3 && d != null && manifest == null; up++, d = d.Parent)     // the folder, blender_export\ or a folder inside it
        {
            var m = System.IO.Path.Combine(d.FullName, TerrainPackageManifest.FileName);
            if (File.Exists(m)) manifest = m;
        }
        if (manifest == null) throw new FileNotFoundException($"No terrain package ({TerrainPackageManifest.FileName}) was found in {dir} or the folder above it. Pick the folder that \"Open in Blender\" made for the terrain.");
        if (edit == null)
        {
            var root = System.IO.Path.GetDirectoryName(manifest)!;
            foreach (var cand in new[] { System.IO.Path.Combine(root, "blender_export", TerrainEdit.FileName), System.IO.Path.Combine(root, TerrainEdit.FileName), System.IO.Path.Combine(dir, TerrainEdit.FileName) })
                if (File.Exists(cand)) { edit = cand; break; }
        }
        return (manifest, edit);
    }

    private sealed class TileState
    {
        public int X, Y;
        public string Path = "";
        public byte[] Base = Array.Empty<byte>();
        public string BaseLabel = "";
        public byte[]? Game;
        public string? Archive;
        public TerrainTile Tile = null!;
        public TerrainGrid Old = null!;
        public TerrainGrid New = null!;
        public HashSet<int> Changed = new();
        public bool Edited;                  // came from Blender
        public bool Synced;                  // an existing neighbour pulled in to match an edge
        public List<string> Notes = new();
        public string? Block;
        public AdtDocument? Doc;
        public int NormalsRecomputed;
    }

    public static MapImportAnalysis Analyze(string path, Func<string, MapInfo?> mapByDirectory, ClientData client, Ws ws, bool syncSeams = false)
    {
        var (manifestPath, editPath) = Locate(path);
        var man = TerrainPackageManifest.FromJson(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException($"{manifestPath} is not a terrain package of WoW Data Studio.");
        var map = mapByDirectory(man.Map.Directory) ?? throw new InvalidDataException($"The map {man.Map.Directory} of the package is not in this game.");
        var pkgDir = System.IO.Path.GetDirectoryName(manifestPath)!;
        var a = new MapImportAnalysis
        {
            Map = map, SourceDir = editPath != null ? System.IO.Path.GetDirectoryName(editPath)! : pkgDir, ChangeSetPrefix = "Terrain from Blender",
            Heading = $"Import edited terrain: {map.Name}",
            SourceText = $"From Blender's export in {(editPath != null ? System.IO.Path.GetDirectoryName(editPath) : pkgDir)} (package {System.IO.Path.GetFileName(pkgDir)}). Each tile is compared with what the app has now (your earlier import of it, or the game's file).",
        };
        if (editPath == null)
        {
            a.General.Add("Blender has not exported this terrain yet. In Blender: the WoW Terrain panel (N), \"Export terrain for WoW Data Studio\" (or File > Export > WoW 1.12 terrain for WoW Data Studio), then import again.");
            return a;
        }
        var edit = TerrainEdit.Load(editPath);
        byte[]? ReadCurrent(string p) => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p);

        var states = new Dictionary<(int, int), TileState>();
        var others = new Dictionary<(int, int), TerrainGrid?>();
        TileState NewState(int x, int y)
        {
            var p = map.TilePath(x, y);
            var cur = ReadCurrent(p) ?? throw new IOException($"{p} could not be read.");
            var t = TerrainTile.Load(cur, map.Directory, x, y);
            var g = TerrainGrid.FromTile(t);
            return new TileState
            {
                X = x, Y = y, Path = p, Base = cur, BaseLabel = ws.IsModified(p) ? "your earlier import" : "the game's file", Game = client.Read(p), Archive = client.Find(p)?.Name,
                Tile = t, Old = g, New = g.Clone(),
            };
        }

        // ---- 1. Blender's vertices -> heights per grid vertex
        long movedSideways = 0, added = 0, deleted = 0;
        float maxMove = 0;
        foreach (var et in edit.Tiles)
        {
            var pt = man.Tile(et.X, et.Y);
            if (pt == null)
            {
                a.General.Add($"Blender exported \"{et.ObjectName}\" as tile {et.X},{et.Y}, which is not in this package: left out.");
                continue;
            }
            if (!map.HasTile(et.X, et.Y))
            {
                a.General.Add($"Tile {et.X},{et.Y} is not in the map's tile list: left out.");
                continue;
            }
            var st = NewState(et.X, et.Y);
            st.Edited = true;
            states[(et.X, et.Y)] = st;
            if (!string.Equals(ModelIO.ModelExporter.Sha(st.Base), pt.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                st.Block = $"tile {et.X},{et.Y} changed in the app since this package was exported (another import or a revert). Export the terrain again and redo the edit on the new version.";
                continue;
            }
            var M = et.Matrix;
            double lx = pt.Location[0], ly = pt.Location[1];
            int n = et.VertexCount;
            var best = new Dictionary<int, (float Dist, float Z)>();
            int localAdded = 0;
            for (int i = 0; i < n; i++)
            {
                double x = et.Co[i * 3], y = et.Co[i * 3 + 1], z = et.Co[i * 3 + 2];
                double wx = M[0] * x + M[1] * y + M[2] * z + M[3], wy = M[4] * x + M[5] * y + M[6] * z + M[7], wz = M[8] * x + M[9] * y + M[10] * z + M[11];
                float col = (float)((wx - lx) / TerrainMath.UnitSize), row = (float)(-(wy - ly) / TerrainMath.UnitSize);
                int id;
                if (et.Vids != null)
                {
                    id = et.Vids[i] - 1;
                    if (id < 0 || id >= TerrainGrid.Count) { localAdded++; continue; }
                }
                else
                {
                    // no ids (the attribute was removed): the nearest grid vertex, outer or inner
                    int oc = (int)MathF.Round(col), orr = (int)MathF.Round(row);
                    int ic = (int)MathF.Floor(col), ir = (int)MathF.Floor(row);
                    float dOuter = MathF.Abs(col - oc) + MathF.Abs(row - orr), dInner = MathF.Abs(col - ic - 0.5f) + MathF.Abs(row - ir - 0.5f);
                    if (dOuter <= dInner && oc is >= 0 and <= 128 && orr is >= 0 and <= 128) id = TerrainGrid.OuterId(orr, oc);
                    else if (ic is >= 0 and <= 127 && ir is >= 0 and <= 127) id = TerrainGrid.InnerId(ir, ic);
                    else { localAdded++; continue; }
                    if (MathF.Min(dOuter, dInner) > 0.3f) { localAdded++; continue; }
                }
                var gp = TerrainGrid.GridPosition(id);
                float dist = MathF.Sqrt((col - gp.X) * (col - gp.X) + (row - gp.Y) * (row - gp.Y)) * TerrainMath.UnitSize;
                if (best.TryGetValue(id, out var have))
                {
                    localAdded++;
                    if (dist >= have.Dist) continue;
                }
                best[id] = (dist, (float)wz);
            }
            int localMoved = 0;
            float localMax = 0;
            foreach (var (id, (dist, z)) in best)
            {
                if (dist > SideTolerance)
                {
                    localMoved++;
                    localMax = Math.Max(localMax, dist);
                }
                float old = st.Old.Height(id);
                if (float.IsNaN(old) || !float.IsFinite(z)) continue;
                if (MathF.Abs(z - old) > HeightTolerance)
                {
                    st.New.SetHeight(id, z);
                    st.Changed.Add(id);
                }
            }
            int localDeleted = TerrainGrid.Count - best.Count;
            if (localMoved > 0) st.Notes.Add($"{V(localMoved)} moved sideways in Blender (up to {localMax:0.##} yards): the game's grid cannot move, so {(localMoved == 1 ? "it was" : "they were")} put back on the grid; only the height was used.");
            if (localAdded > 0) st.Notes.Add($"{V(localAdded)} added in Blender {(localAdded == 1 ? "was" : "were")} ignored: the game's ground has a fixed number of vertices.");
            if (localDeleted > 0) st.Notes.Add($"{V(localDeleted)} of the tile {(localDeleted == 1 ? "was" : "were")} missing in Blender's export (deleted): {(localDeleted == 1 ? "it keeps its" : "they keep their")} height.");
            if (et.Vids == null) st.Notes.Add("The mesh had lost its vertex ids (wow_vid): vertices were matched by their position instead.");
            movedSideways += localMoved;
            added += localAdded;
            deleted += localDeleted;
            maxMove = Math.Max(maxMove, localMax);
        }
        foreach (var pt in man.Tiles.Where(t => !edit.Tiles.Any(e => e.X == t.X && e.Y == t.Y)))
            a.General.Add($"Tile {pt.X},{pt.Y} of the package is not in Blender's export (its object was deleted or renamed without its tile id): it stays as it is.");
        if (movedSideways > 0) a.Ignored.Add($"{V(movedSideways)} moved sideways (up to {maxMove:0.##} yards) {(movedSideways == 1 ? "was" : "were")} put back on the grid: only heights can change.");
        if (added > 0) a.Ignored.Add($"{V(added)} added in Blender {(added == 1 ? "was" : "were")} ignored.");
        if (deleted > 0) a.Ignored.Add($"{V(deleted)} deleted in Blender kept {(deleted == 1 ? "its" : "their")} height.");

        // ---- 2. shared edges with the other imported tiles and the existing neighbours
        TerrainGrid? Current(int x, int y)
        {
            if (states.TryGetValue((x, y), out var s)) return s.New;
            if (others.TryGetValue((x, y), out var g)) return g;
            g = null;
            if (map.HasTile(x, y) && ReadCurrent(map.TilePath(x, y)) is { } bytes) g = TerrainGrid.FromTile(TerrainTile.Load(bytes, map.Directory, x, y));
            others[(x, y)] = g;
            return g;
        }
        for (int pass = 0; pass < 4; pass++)
        {
            bool syncedAny = false;
            a.Seams.Clear();
            foreach (var st in states.Values.Where(s => s.Block == null).ToList())
            {
                foreach (var (dx, dy, edge, pairs) in SharedEdges())
                {
                    int bx = st.X + dx, by = st.Y + dy;
                    var other = states.GetValueOrDefault((bx, by));
                    if (other != null && (other.Block != null || !Forward(dx, dy))) continue;   // each pair of imported tiles once
                    var gb = Current(bx, by);
                    if (gb == null) continue;
                    var issue = new SeamIssue { A = (st.X, st.Y), B = (bx, by), Edge = edge, ExistingNeighbour = other == null };
                    foreach (var (ia, ib) in pairs)
                    {
                        float ha = st.New.Height(ia), hb = gb.Height(ib);
                        if (float.IsNaN(ha) || float.IsNaN(hb) || MathF.Abs(ha - hb) <= SeamTolerance) continue;
                        bool ca = st.Changed.Contains(ia), cb = other?.Changed.Contains(ib) == true;
                        if (!ca && !cb) continue;            // an old difference of the game's own tiles: not ours
                        if (!syncSeams)
                        {
                            issue.Vertices++;
                            issue.MaxDifference = Math.Max(issue.MaxDifference, MathF.Abs(ha - hb));
                            continue;
                        }
                        // the edited side wins; both edited: the one that moved further from the original
                        bool fromA = ca && !cb || ca && cb && MathF.Abs(ha - st.Old.Height(ia)) >= MathF.Abs(hb - other!.Old.Height(ib));
                        if (fromA)
                        {
                            if (other == null)
                            {
                                other = NewState(bx, by);
                                other.Synced = true;
                                states[(bx, by)] = other;
                                others.Remove((bx, by));
                                gb = other.New;
                            }
                            other.New.SetHeight(ib, ha);
                            other.Changed.Add(ib);
                        }
                        else
                        {
                            st.New.SetHeight(ia, hb);
                            st.Changed.Add(ia);
                        }
                        issue.Vertices++;
                        issue.MaxDifference = Math.Max(issue.MaxDifference, MathF.Abs(ha - hb));
                        syncedAny = true;
                    }
                    if (issue.Vertices == 0) continue;
                    if (syncSeams)
                    {
                        var known = a.SyncedSeams.FirstOrDefault(s => s.A == issue.A && s.B == issue.B);
                        if (known == null) a.SyncedSeams.Add(issue);
                        else
                        {
                            known.Vertices += issue.Vertices;
                            known.MaxDifference = Math.Max(known.MaxDifference, issue.MaxDifference);
                        }
                    }
                    else a.Seams.Add(issue);
                }
            }
            if (!syncSeams || !syncedAny) break;
        }

        // ---- 3. heights and normals into the files
        foreach (var st in states.Values.Where(s => s.Block == null && s.Changed.Count > 0))
        {
            var doc = new AdtDocument(st.Base, st.Path);
            if (!doc.CanRebuild)
            {
                st.Block = "the tile's file has an unusual layout that the app cannot rewrite safely.";
                continue;
            }
            foreach (var id in st.Changed)
            {
                float h = st.New.Height(id);
                foreach (var (chunk, k) in TerrainGrid.Copies(id))
                    if (doc.Chunk(chunk) != null) doc.SetHeight(chunk, k, h - doc.PosZ(chunk));
            }
            st.Doc = doc;
        }
        // normals: every vertex whose neighbourhood changed, in every imported tile
        var affected = new Dictionary<(int, int), HashSet<int>>();
        foreach (var st in states.Values.Where(s => s.Block == null))
            foreach (var id in st.Changed)
                foreach (var (dx, dy, aid) in TerrainNormals.Affected(id))
                {
                    var key = (st.X + dx, st.Y + dy);
                    if (!states.TryGetValue(key, out var target) || target.Block != null) continue;
                    if (!affected.TryGetValue(key, out var set)) affected[key] = set = new HashSet<int>();
                    set.Add(aid);
                }
        foreach (var (key, ids) in affected)
        {
            var st = states[key];
            if (st.Doc == null)
            {
                st.Doc = new AdtDocument(st.Base, st.Path);
                if (!st.Doc.CanRebuild) { st.Block = "the tile's file has an unusual layout that the app cannot rewrite safely."; continue; }
            }
            var field = new TerrainNormals.Field(st.X, st.Y, Current);
            foreach (var id in ids)
            {
                if (float.IsNaN(st.New.Height(id))) continue;
                var q = TerrainNormals.Quantize(TerrainNormals.Normal(field, id));
                foreach (var (chunk, k) in TerrainGrid.Copies(id))
                    if (st.Doc.Chunk(chunk) != null) st.Doc.SetNormal(chunk, k, q);
            }
            st.NormalsRecomputed = ids.Count;
        }

        // ---- 4. the review rows
        foreach (var st in states.Values.OrderBy(s => s.Y).ThenBy(s => s.X))
        {
            var name = System.IO.Path.GetFileName(st.Path);
            byte[] bytes = st.Doc != null && st.Block == null ? st.Doc.Serialize() : st.Base;
            var report = MapValidator.ValidateBytes(bytes, name);
            var diff = MapDiff.DiffBytes(st.Base, bytes, name, st.BaseLabel, NewLabel);
            var ch = new MapFileChange
            {
                GamePath = st.Path, SourceFile = st.Edited ? editPath : $"(edge synced to match {string.Join(", ", a.SyncedSeams.Where(s => s.B == (st.X, st.Y)).Select(s => $"tile {s.A.X},{s.A.Y}"))})",
                Kind = "adt", Tile = (st.X, st.Y), NewBytes = bytes, BaseBytes = st.Base, BaseLabel = st.BaseLabel, GameBytes = st.Game, GameArchive = st.Archive,
                Validation = report, Diff = diff, Unchanged = st.Block == null && diff.SameContent, BlockReason = st.Block, NewLabel = NewLabel,
            };
            ch.Include = ch.CanInclude;
            if (st.Synced) ch.Notes.Add($"Not edited in Blender: its shared edge was synced so it matches your edit (the heights of {st.Changed.Count} vertices copied from the edited side).");
            ch.Notes.AddRange(st.Notes);
            if (st.NormalsRecomputed > 0 && !ch.Unchanged) ch.Notes.Add($"Normals (lighting) recomputed for the {st.NormalsRecomputed} vertices around the changed heights; everything else in the file is kept byte for byte.");
            a.Changes.Add(ch);
        }

        // ---- 5. the general messages
        if (a.Changes.Count > 0 && a.NothingChanged && a.Seams.Count == 0)
            a.General.Add("No changes found: the heights Blender exported are the same as what the app has. Nothing will be written.");
        a.General.AddRange(a.Ignored.Select(s => "Ignored: " + s));
        foreach (var s in a.SyncedSeams) a.General.Add($"Synced: {s.Text} (the edited side was copied).");
        foreach (var s in a.Seams) a.General.Add($"Edge mismatch: {s.Text}. Sync it, or the game shows a crack (a gap in the ground) there.");
        if (man.References != null) a.General.Add("The doodads and buildings in the Blender scene were only for reference: they are not imported (moving them in Blender changes nothing).");
        if (a.Changes.Any(c => c.CanInclude && c.Include))
            a.General.Add("This changes the ground the server knows about: after testing in the game, also update the server's maps (Maps tab, \"Update server maps\"), or creatures will walk on the old ground.");
        return a;
    }

    private static string V(long n) => n == 1 ? "1 vertex" : $"{n} vertices";

    private static bool Forward(int dx, int dy) => (dx, dy) is (1, 0) or (0, 1) or (1, 1) or (-1, 1);

    /// <summary>The 8 neighbours with the pairs of vertex ids they share with this tile: (dx, dy, edge, (id here, id there)).</summary>
    private static IEnumerable<(int Dx, int Dy, string Edge, List<(int, int)> Pairs)> SharedEdges()
    {
        List<(int, int)> Line(Func<int, (int, int)> f) => Enumerable.Range(0, 129).Select(f).ToList();
        yield return (1, 0, "east", Line(r => (TerrainGrid.OuterId(r, 128), TerrainGrid.OuterId(r, 0))));
        yield return (-1, 0, "west", Line(r => (TerrainGrid.OuterId(r, 0), TerrainGrid.OuterId(r, 128))));
        yield return (0, 1, "south", Line(c => (TerrainGrid.OuterId(128, c), TerrainGrid.OuterId(0, c))));
        yield return (0, -1, "north", Line(c => (TerrainGrid.OuterId(0, c), TerrainGrid.OuterId(128, c))));
        yield return (1, 1, "south-east corner", new List<(int, int)> { (TerrainGrid.OuterId(128, 128), TerrainGrid.OuterId(0, 0)) });
        yield return (-1, 1, "south-west corner", new List<(int, int)> { (TerrainGrid.OuterId(128, 0), TerrainGrid.OuterId(0, 128)) });
        yield return (1, -1, "north-east corner", new List<(int, int)> { (TerrainGrid.OuterId(0, 128), TerrainGrid.OuterId(128, 0)) });
        yield return (-1, -1, "north-west corner", new List<(int, int)> { (TerrainGrid.OuterId(0, 0), TerrainGrid.OuterId(128, 128)) });
    }

    /// <summary>The shared edges of tiles as they are (no import): mismatches between neighbouring tiles of a map
    /// (the check the tests and the seam report use).</summary>
    public static List<SeamIssue> CheckSeams(MapInfo map, IEnumerable<(int X, int Y)> tiles, Func<string, byte[]?> read)
    {
        var grids = new Dictionary<(int, int), TerrainGrid?>();
        TerrainGrid? G(int x, int y)
        {
            if (grids.TryGetValue((x, y), out var g)) return g;
            g = map.HasTile(x, y) && read(map.TilePath(x, y)) is { } b ? TerrainGrid.FromTile(TerrainTile.Load(b, map.Directory, x, y)) : null;
            grids[(x, y)] = g;
            return g;
        }
        var list = new List<SeamIssue>();
        var set = tiles.ToHashSet();
        foreach (var (x, y) in set)
        {
            var ga = G(x, y);
            if (ga == null) continue;
            foreach (var (dx, dy, edge, pairs) in SharedEdges())
            {
                if (set.Contains((x + dx, y + dy)) && !Forward(dx, dy)) continue;
                var gb = G(x + dx, y + dy);
                if (gb == null) continue;
                var issue = new SeamIssue { A = (x, y), B = (x + dx, y + dy), Edge = edge, ExistingNeighbour = !set.Contains((x + dx, y + dy)) };
                foreach (var (ia, ib) in pairs)
                {
                    float d = MathF.Abs(ga.Height(ia) - gb.Height(ib));
                    if (d > SeamTolerance)
                    {
                        issue.Vertices++;
                        issue.MaxDifference = Math.Max(issue.MaxDifference, d);
                    }
                }
                if (issue.Vertices > 0) list.Add(issue);
            }
        }
        return list;
    }
}
