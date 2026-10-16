using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Index;
using WoWData.Core.Maps;
using WoWData.Core.Usage;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.6.0 without Blender: "Break down this tile" lists exactly what the ADT holds (counts per group against the file and
/// against MapScanner, the placement scan behind the index), its Browse keys resolve in the index, the terrain grid and
/// the normal formula match the stock tiles, and the terrain package's import path (a scripted edit written in the
/// add-on's export format): heights only, sideways moves snapped with a message, seams checked and synced (also into an
/// existing neighbour tile), normals recomputed only around the change, everything else byte for byte.
/// </summary>
public class TerrainBreakdownTests
{
    private readonly ITestOutputHelper _out;

    public TerrainBreakdownTests(ITestOutputHelper output) => _out = output;

    internal static MapCatalog Catalog => _catalog ??= MapCatalog.Load(TestEnv.Client, null);
    private static MapCatalog? _catalog;
    internal static MapInfo Map(string dir) => Catalog.ByDirectory(dir)!;

    internal static TileBreakdown Breakdown(string dir, int x, int y)
    {
        var map = Map(dir);
        var data = TestEnv.Client.Read(map.TilePath(x, y))!;
        return TileBreakdown.Build(map.Name, map.Directory, x, y, data, TestEnv.Client.Read, Catalog.AreaName);
    }

    private static readonly object ScanGate = new();
    private static Dictionary<string, MapScanResult> _scans = new();

    private static MapScanResult Scan(string dir)
    {
        lock (ScanGate)
        {
            if (_scans.TryGetValue(dir, out var s)) return s;
            var map = Map(dir);
            s = MapScanner.Scan(TestEnv.Client, new[] { new MapRec(map.Id, map.Directory, map.Name) }, p => WoWData.Core.Mpq.ClientData.ResolveName(p));
            _scans[dir] = s;
            return s;
        }
    }

    [Theory]
    [InlineData("Azeroth", 32, 48)]    // Northshire: doodads, buildings, many textures
    [InlineData("Azeroth", 32, 49)]    // Goldshire side: sound emitters
    [InlineData("Azeroth", 31, 50)]    // water
    [InlineData("Kalimdor", 40, 30)]
    public void BreakdownListsMatchTheAdtContents(string dir, int x, int y)
    {
        var map = Map(dir);
        var data = TestEnv.Client.Read(map.TilePath(x, y))!;
        var adt = new AdtFile(data);
        var bd = Breakdown(dir, x, y);
        _out.WriteLine(string.Join("\n", bd.Groups.Select(g => $"{g.Title}: {g.Summary} ({g.Items.Count} entries)")));
        Assert.Equal(new[] { "terrain", "textures", "doodads", "buildings", "water", "other" }, bd.Groups.Select(g => g.Key));
        // terrain: the mesh and its 256 chunks
        var mesh = bd.Group("terrain").Items.Single();
        Assert.Equal(256, mesh.Children.Count);
        Assert.All(mesh.Children, c => Assert.Single(c.Chunks));
        Assert.Equal(Enumerable.Range(0, 256), mesh.Children.Select(c => c.Chunk).OrderBy(c => c));
        // ground textures: every texture the file lists, coverage adds up to the whole ground
        var mtex = adt.Textures.Where(t => t.Length > 0).Select(t => WoWData.Core.Mpq.ClientData.ResolveName(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(mtex, bd.Group("textures").Items.Select(i => i.Path!).OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        Assert.InRange(bd.Group("textures").Items.Sum(i => i.Coverage), 0.999, 1.001);
        // doodads: grouped by model, one child per MDDF record
        var doodads = bd.Group("doodads").Items;
        Assert.Equal(adt.Mddf.Count, doodads.Sum(g => g.Children.Count));
        Assert.Equal(adt.Mddf.Count, doodads.Sum(g => g.Count));
        Assert.Equal(adt.Mddf.Select(d => adt.DoodadName(d)!).Select(WoWData.Core.Mpq.ClientData.ResolveName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), doodads.Count);
        Assert.All(doodads, g => Assert.Equal(g.Children.Count, g.DoodadUids.Count));
        // buildings: one entry per MODF record, with the root's groups and doodad sets
        var buildings = bd.Group("buildings").Items;
        Assert.Equal(adt.Modf.Count, buildings.Count);
        foreach (var b in buildings)
        {
            var root = WmoGeometry.ReadRoot(TestEnv.Client.Read(b.Path!)!);
            Assert.Equal(root.Groups.Count + root.DoodadSets.Count, b.Children.Count);
        }
        // water and sound emitters
        Assert.Equal(bd.Tile.AllChunks.Sum(c => c.Liquids.Count), bd.Group("water").Items.Count);
        int emitters = adt.Mcnk.Where(c => c != null && c.HasHeader).Sum(c => (int)c!["nSndEmitters"]);
        Assert.Equal(emitters, bd.Group("other").Items.Count(i => i.Kind == BreakdownKind.SoundEmitter));
        Assert.Equal(256, bd.Group("other").Items.Where(i => i.Kind == BreakdownKind.Area).Sum(i => i.Chunks.Count));

        // against MapScanner (the scan behind the index's u_place / u_area_chunk): the zone of every chunk, and every
        // doodad and building placed on this tile (by its position, each unique id once)
        var scan = Scan(dir);
        var areas = scan.Areas[(map.Id, x, y)];
        foreach (var c in mesh.Children) Assert.Equal(areas[c.Chunk], bd.Tile.Chunks[c.Chunk]!.AreaId);
        var scanned = scan.Placements.Where(p => p.TileX == x && p.TileY == y && p.Parent == "").GroupBy(p => p.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Count), StringComparer.OrdinalIgnoreCase);
        var listed = doodads.SelectMany(g => g.Children).Where(d => !d.OnNeighbour).GroupBy(d => d.DoodadUids[0]).Select(g => g.First())
            .Concat(buildings.Where(b => !b.OnNeighbour).GroupBy(b => b.WmoUids[0]).Select(g => g.First()))
            .GroupBy(d => d.Path!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        int missing = scanned.Where(kv => listed.GetValueOrDefault(kv.Key) != kv.Value).Sum(kv => Math.Abs(kv.Value - listed.GetValueOrDefault(kv.Key)));
        _out.WriteLine($"scan: {scanned.Values.Sum()} placements on the tile, breakdown {listed.Values.Sum()}; differences {missing}");
        Assert.Equal(scanned.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase), listed.Where(kv => scanned.ContainsKey(kv.Key) || kv.Value > 0)
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void GridCopiesAndNormalsMatchTheStockTiles()
    {
        // every (chunk, value) of the file is exactly one grid vertex, and the vertex lists it among its copies
        var seen = new HashSet<(int, int)>();
        for (int id = 0; id < TerrainGrid.Count; id++)
            foreach (var (c, k) in TerrainGrid.Copies(id))
            {
                Assert.True(seen.Add((c, k)));
                Assert.Equal(id, TerrainGrid.IdOf(c, k));
            }
        Assert.Equal(256 * 145, seen.Count);
        // the normal formula (sum of the unit normals of the triangles around the vertex) against the stored MCNR,
        // inside the tile and across tile edges with the neighbours loaded
        var map = Map("Azeroth");
        var grids = new Dictionary<(int, int), TerrainGrid?>();
        TerrainGrid? G(int x, int y)
        {
            if (!grids.TryGetValue((x, y), out var g)) grids[(x, y)] = g = map.HasTile(x, y) ? TerrainGrid.FromTile(TerrainTile.Load(TestEnv.Client.Read(map.TilePath(x, y))!, map.Directory, x, y)) : null;
            return g;
        }
        double sum = 0, edgeSum = 0;
        int n = 0, edgeN = 0;
        foreach (var (tx, ty) in new[] { (32, 48), (31, 49) })
        {
            var tile = TerrainTile.Load(TestEnv.Client.Read(map.TilePath(tx, ty))!, map.Directory, tx, ty);
            var field = new TerrainNormals.Field(tx, ty, G);
            for (int id = 0; id < TerrainGrid.Count; id += 7)
            {
                var (c, k) = TerrainGrid.Owner(id);
                var stored = tile.Chunks[c]!.Normals[k];
                double ang = Math.Acos(Math.Clamp(Vector3.Dot(TerrainNormals.Normal(field, id), stored), -1, 1)) * 180 / Math.PI;
                var (r, col) = TerrainGrid.RowCol(id);
                if (!TerrainGrid.IsInner(id) && (r is 0 or 128 || col is 0 or 128)) { edgeSum += ang; edgeN++; }
                else { sum += ang; n++; }
            }
        }
        _out.WriteLine($"normals: mean {sum / n:0.00} degrees inside ({n}), {edgeSum / edgeN:0.00} on tile edges ({edgeN})");
        Assert.True(sum / n < 0.5, $"mean {sum / n} degrees");
        // tile edges: closer with the neighbours' heights than without (a few stock edges were re-saved after a neighbour changed)
        Assert.True(edgeSum / edgeN < 2.0, $"edge mean {edgeSum / edgeN} degrees");
    }

    // ---------------------------------------------------------------- the import path with a scripted edit (no Blender)

    /// <summary>The add-on's export format, written from C# (TerrainEdit.WriteScripted) as Blender writes it.</summary>
    internal static void WriteEdit(string packageDir, Func<TerrainPackageTile, int, Vector3, Vector3?>? change = null, Func<TerrainPackageTile, bool>? include = null, int addVertices = 0) =>
        TerrainEdit.WriteScripted(packageDir, change, include, addVertices);

    internal static TerrainExportResult ExportPackage(MapInfo map, int x, int y, int size, string folder, bool references = false) =>
        TerrainExporter.Export(new TerrainExportRequest
        {
            Map = map, Tiles = TerrainExporter.Square(map, x, y, size), Center = (x, y), ReadCurrent = TestEnv.Client.Read, ReadGame = TestEnv.Client.Read,
            ArchiveOf = p => TestEnv.Client.Find(p)?.Name, IncludeReferences = references, PackageFolder = folder,
            LoadTexture = p => TestEnv.Client.Read(p) is { } d ? BlpFile.Parse(d).Decode(0) : null,
        });

    /// <summary>A workspace of a FAKE client folder (nothing can reach the real game folder).</summary>
    internal static Workspace FakeWorkspace(string root)
    {
        var fake = Path.Combine(root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fake, "Data"));
        return new Workspace(new AppPaths(Path.Combine(root, "home")).ForClient(fake));
    }

    [Fact]
    public void PackageHoldsTheTilesTexturesAndReferences()
    {
        var root = TestEnv.TempDir("terrainpkg");
        try
        {
            var map = Map("Azeroth");
            var res = ExportPackage(map, 32, 48, 3, Path.Combine(root, "pkg"), references: true);
            var man = res.Manifest;
            Assert.Equal(9, man.Tiles.Count);
            Assert.Equal(new[] { 32, 48 }, man.Center);
            var center = man.Tile(32, 48)!;
            Assert.Equal(new double[] { 0, 0 }, center.Location);
            Assert.Equal(TerrainMath.TileSize, man.Tile(33, 48)!.Location[0], 3);
            Assert.Equal(0, man.Tile(33, 48)!.Location[1], 3);
            Assert.Equal(-TerrainMath.TileSize, man.Tile(32, 49)!.Location[1], 3);
            // neighbours never share a colour
            foreach (var t in man.Tiles)
                foreach (var u in man.Tiles.Where(u => u != t && Math.Abs(u.X - t.X) <= 1 && Math.Abs(u.Y - t.Y) <= 1))
                    Assert.NotEqual(t.Color, u.Color);
            Assert.Equal("Tile 33_48", center.Neighbours["E"]);
            Assert.Equal(WoWData.Core.ModelIO.ModelExporter.Sha(TestEnv.Client.Read(map.TilePath(32, 48))!), center.Sha256);
            // heights file: the grid of the tile
            var bin = File.ReadAllBytes(Path.Combine(res.Folder, center.HeightsFile));
            Assert.Equal(TerrainGrid.Count * 4 + TerrainGrid.InnerCount, bin.Length);
            var grid = TerrainGrid.FromTile(TerrainTile.Load(TestEnv.Client.Read(map.TilePath(32, 48))!, "Azeroth", 32, 48));
            Assert.Equal(grid.O(17, 40), BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(TerrainGrid.OuterId(17, 40) * 4)));
            // textures and weights: every painted texture of the tile has a weight channel, and the weights add up to 1
            Assert.All(man.Tiles, t => Assert.True(t.Textures.Count > 0));
            Assert.All(man.Textures, t => Assert.True(t.Png != null && File.Exists(Path.Combine(res.Folder, t.Png))));
            var w = WoWData.Core.Imaging.Png.Load(Path.Combine(res.Folder, center.Textures[0].WeightImage));
            Assert.Equal(1024, w.Width);
            long total = 0;
            int px = 1000 * 1024 + 300;
            foreach (var use in center.Textures)
                total += WoWData.Core.Imaging.Png.Load(Path.Combine(res.Folder, use.WeightImage)).Pixels[px * 4 + use.Channel];
            Assert.InRange(total, 250, 260);
            // references: a glTF with the doodads and buildings
            Assert.NotNull(man.References);
            Assert.True(man.References!.Doodads > 100 && man.References.Buildings > 0, $"{man.References.Doodads} doodads, {man.References.Buildings} buildings");
            var g = WoWData.Core.ModelIO.GltfFile.Load(Path.Combine(res.Folder, man.References.File));
            Assert.Equal(man.References.Doodads + man.References.Buildings + 1, g.NodeCount);
            _out.WriteLine($"package: {res.Bytes / 1e6:0.0} MB, {man.Textures.Count} textures, {man.References.Doodads} doodads, {man.References.Buildings} buildings ({man.References.Models} models)");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ScriptedEditImportsHeightsOnlyWithSeamsSyncedAndNormalsRecomputed()
    {
        var before = TestEnv.DataSnapshot();
        var root = TestEnv.TempDir("terrainimport");
        try
        {
            var map = Map("Azeroth");
            var ws = FakeWorkspace(root);
            MapInfo? ByDir(string d) => Catalog.ByDirectory(d);

            // 1. unchanged round trip: no change
            var pkg = ExportPackage(map, 32, 48, 1, Path.Combine(root, "one")).Folder;
            WriteEdit(pkg);
            var a0 = TerrainImporter.Analyze(pkg, ByDir, TestEnv.Client, ws);
            Assert.True(a0.NothingChanged);
            Assert.Contains(a0.General, g => g.StartsWith("No changes found", StringComparison.Ordinal));

            // 2. sideways moves and an added vertex: snapped / ignored, said in words, no change
            WriteEdit(pkg, (t, id, co) => id is > 2000 and < 2025 ? co + new Vector3(1.5f, -0.7f, 0) : null, addVertices: 1);
            var a1 = TerrainImporter.Analyze(pkg, ByDir, TestEnv.Client, ws);
            Assert.True(a1.NothingChanged);
            Assert.Contains(a1.Ignored, s => s.StartsWith("24 vertices moved sideways", StringComparison.Ordinal));
            Assert.Contains(a1.Ignored, s => s.StartsWith("1 vertex added", StringComparison.Ordinal));
            Assert.Contains(a1.Changes.Single().Notes, n => n.Contains("put back on the grid", StringComparison.Ordinal));

            // 3. the east edge raised on this tile only: the existing neighbour 33,48 disagrees; synced on request
            Vector3? RaiseEast(TerrainPackageTile t, int id, Vector3 co)
            {
                var (r, c) = TerrainGrid.RowCol(id);
                float row = TerrainGrid.IsInner(id) ? r + 0.5f : r, col = TerrainGrid.IsInner(id) ? c + 0.5f : c;
                return row is >= 40 and <= 72 && col >= 124 ? co + new Vector3(0, 0, 6f * (col - 123) / 5f) : null;
            }
            WriteEdit(pkg, RaiseEast);
            var a2 = TerrainImporter.Analyze(pkg, ByDir, TestEnv.Client, ws);
            var seam = Assert.Single(a2.Seams);
            Assert.Equal(((32, 48), (33, 48), "east", true), (seam.A, seam.B, seam.Edge, seam.ExistingNeighbour));
            Assert.Equal(33, seam.Vertices);
            var a3 = TerrainImporter.Analyze(pkg, ByDir, TestEnv.Client, ws, syncSeams: true);
            Assert.Empty(a3.Seams);
            Assert.Single(a3.SyncedSeams);
            Assert.Equal(new[] { "Azeroth_32_48.adt", "Azeroth_33_48.adt" }, a3.Changes.Select(c => c.FileName));
            var nb = a3.Changes.Single(c => c.FileName == "Azeroth_33_48.adt");
            Assert.Contains(nb.Notes, n => n.StartsWith("Not edited in Blender", StringComparison.Ordinal));
            foreach (var ch in a3.Changes)
            {
                Assert.True(ch.CanInclude && ch.Include, ch.Summary);
                Assert.True(ch.Validation.Ok);
                _out.WriteLine($"{ch.FileName}: {ch.Summary}");
            }
            // only the edge chunks changed: heights in column 15 (here) / 0 (the neighbour), normals at most one chunk further
            var edited = a3.Changes[0];
            var orig = TestEnv.Client.Read(map.TilePath(32, 48))!;
            var heightChunks = ChangedChunks(orig, edited.NewBytes, "ofsHeight");
            var anyChunks = ChangedChunks(orig, edited.NewBytes, null);
            Assert.Equal(new[] { 79, 95, 111, 127, 143, 159 }, heightChunks);                     // column 15, rows 4 to 9
            Assert.All(anyChunks, c => Assert.True(c % 16 == 15 && c / 16 is >= 4 and <= 9, $"chunk {c}"));
            Assert.Equal(heightChunks.Count, edited.Diff!.Stats.HeightChunks);
            var nbChunks = ChangedChunks(TestEnv.Client.Read(map.TilePath(33, 48))!, nb.NewBytes, null);
            Assert.All(nbChunks, c => Assert.True(c % 16 == 0 && c / 16 is >= 4 and <= 9, $"neighbour chunk {c}"));
            // the shared edge is identical, the normals are recomputed (lighting follows the new heights)
            var ta = TerrainTile.Load(edited.NewBytes, "Azeroth", 32, 48);
            var tb = TerrainTile.Load(nb.NewBytes, "Azeroth", 33, 48);
            var ga = TerrainGrid.FromTile(ta);
            var gb = TerrainGrid.FromTile(tb);
            for (int r = 0; r <= 128; r++) Assert.Equal(ga.O(r, 128), gb.O(r, 0));
            Assert.Equal(6f, ga.O(56, 128) - TerrainGrid.FromTile(TerrainTile.Load(orig, "Azeroth", 32, 48)).O(56, 128), 3);
            var field = new TerrainNormals.Field(32, 48, (x, y) => x == 32 && y == 48 ? ga : x == 33 && y == 48 ? gb : null);
            int probe = TerrainGrid.OuterId(56, 126);
            var (pc, pk) = TerrainGrid.Owner(probe);
            Assert.True(Vector3.Dot(ta.Chunks[pc]!.Normals[pk], TerrainNormals.Normal(field, probe)) > 0.9999f);
            var doc0 = new AdtDocument(orig);
            Assert.NotEqual(doc0.GetNormal(pc, pk), new AdtDocument(edited.NewBytes).GetNormal(pc, pk));
            // every byte outside MCVT / MCNR is the original's
            Assert.Equal(StripTerrain(orig), StripTerrain(edited.NewBytes));
            Assert.Empty(TerrainImporter.CheckSeams(map, new[] { (32, 48) }, p => a3.Changes.FirstOrDefault(c => c.GamePath == p)?.NewBytes ?? TestEnv.Client.Read(p))
                .Where(s => s.B == (33, 48)));

            // 4. one change set; the import is now the base: the same export again is "changed since the export"
            var cs = MapImporter.Commit(a3, ws);
            Assert.Equal(2, cs.Files.Count);
            Assert.StartsWith("Terrain from Blender: Eastern Kingdoms (tiles 32,48 33,48)", cs.Label);
            var a4 = TerrainImporter.Analyze(pkg, ByDir, TestEnv.Client, ws);
            Assert.Contains(a4.Changes.Single(c => c.FileName == "Azeroth_32_48.adt").Problems, p => p.Contains("changed in the app since this package was exported", StringComparison.Ordinal));
            ws.RevertChangeSet(cs.Id);
            Assert.False(ws.IsModified(map.TilePath(32, 48)));

            // 5. 3 x 3: the edge raised on one tile only; its neighbour is in the package (not an existing tile) and is synced
            var pkg3 = ExportPackage(map, 32, 48, 3, Path.Combine(root, "three")).Folder;
            WriteEdit(pkg3, (t, id, co) => t.X == 32 && t.Y == 48 ? RaiseEast(t, id, co) : null);
            var b1 = TerrainImporter.Analyze(pkg3, ByDir, TestEnv.Client, ws);
            Assert.False(Assert.Single(b1.Seams).ExistingNeighbour);
            var b2 = TerrainImporter.Analyze(pkg3, ByDir, TestEnv.Client, ws, syncSeams: true);
            Assert.Equal(2, b2.Changes.Count(c => c.CanInclude));
            Assert.All(b2.Changes.Where(c => c.Tile is not ((32, 48) or (33, 48))), c => Assert.True(c.Unchanged));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Assert.Equal(before, TestEnv.DataSnapshot());
    }

    /// <summary>The chunks (index) whose bytes differ; with a field name only that sub-chunk is compared.</summary>
    internal static List<int> ChangedChunks(byte[] a, byte[] b, string? field)
    {
        var da = new AdtDocument(a);
        var db = new AdtDocument(b);
        var list = new List<int>();
        for (int c = 0; c < 256; c++)
        {
            var x = da.Chunk(c)!;
            var y = db.Chunk(c)!;
            bool diff = field == null
                ? !x.Header.SequenceEqual(y.Header) || x.Subs.Count != y.Subs.Count || x.Subs.Zip(y.Subs).Any(p => !p.First.Bytes.SequenceEqual(p.Second.Bytes))
                : !x.Sub(field)!.Bytes.SequenceEqual(y.Sub(field)!.Bytes);
            if (diff) list.Add(c);
        }
        return list;
    }

    /// <summary>The file with every MCVT and MCNR blanked (what must not change).</summary>
    internal static byte[] StripTerrain(byte[] data)
    {
        var d = new AdtDocument((byte[])data.Clone());
        for (int c = 0; c < 256; c++)
        {
            if (d.Chunk(c) is not { } ch) continue;
            Array.Clear(ch.Sub("ofsHeight")!.Bytes, 8, 145 * 4);
            Array.Clear(ch.Sub("ofsNormal")!.Bytes, 8, 145 * 3);
        }
        return d.Serialize();
    }
}
