using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.6.0, terrain through Blender 4.5 (headless, the add-on loaded from blender_addon\, blender_addon\tests\studio_terrain.py):
/// a 3 x 3 export becomes 9 objects "Tile x_y" with their own collections, colours, labels and EDGE_N/E/S/W vertex groups;
/// a scripted raise of one tile's edge + "Sync shared edges" comes back with matching seams, only those chunks changed,
/// recomputed normals and a passing validator; an unchanged round trip is "no change"; sideways moves and added vertices
/// are snapped / ignored with a message. Skipped (with a message) without Blender. The real Client\Data is checked
/// unchanged afterwards; imports go into the workspace of a fake client folder.
/// </summary>
public class TerrainBlenderTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = TestEnv.TempDir("terrain_blender");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();

    public TerrainBlenderTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string AddonRoot => Path.Combine(Oracle.ToolDir, "blender_addon");
    private static MapInfo Azeroth => TerrainBreakdownTests.Map("Azeroth");

    /// <summary>Import the package in headless Blender, apply the edit, export; returns the report or null without Blender.</summary>
    private JsonNode? ThroughBlender(string package, object? edit, string tag)
    {
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            _out.WriteLine("Blender 4.5 not found: the Blender part of this test was skipped.");
            return null;
        }
        var report = Path.Combine(_root, tag + "_report.json");
        string editFile = "";
        if (edit != null)
        {
            editFile = Path.Combine(_root, tag + "_edit.json");
            File.WriteAllText(editFile, JsonSerializer.Serialize(edit));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = BlenderTools.RunHeadless(blender, Path.Combine(AddonRoot, "tests", "studio_terrain.py"),
            new[] { report, Path.Combine(package, TerrainPackageManifest.FileName), editFile }, 900_000);
        Assert.True(File.Exists(report), "Blender wrote no report: " + r.Output);
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        _out.WriteLine($"Blender {rep["blender"]} in {sw.Elapsed.TotalSeconds:0.0} s: {rep["import"]?["objects"]} tiles, {rep["import"]?["references"]} references; " +
                       $"raised {rep["raised_vertices"]}, sync {rep["sync"]?.ToJsonString()}, seams after edit {rep["seams_after_edit"]?.ToJsonString()}, after sync {rep["seams_after_sync"]?.ToJsonString()}");
        Assert.True(rep["ok"]!.GetValue<bool>(), rep["error"]?.ToString());
        return rep;
    }

    private MapImportAnalysis Analyze(string package, Workspace ws, bool sync = false) =>
        TerrainImporter.Analyze(package, d => TerrainBreakdownTests.Catalog.ByDirectory(d), TestEnv.Client, ws, sync);

    private void Log(MapImportAnalysis a)
    {
        foreach (var g in a.General) _out.WriteLine("  " + g);
        foreach (var c in a.Changes)
        {
            _out.WriteLine($"  {c.FileName}: {(c.Unchanged ? "unchanged" : c.Summary)}");
            foreach (var n in c.Notes) _out.WriteLine("     " + n);
        }
    }

    [Fact]
    public void ThreeByThreeEdgeEditSyncedInBlenderComesBackSeamless()
    {
        var map = Azeroth;
        var pkg = TerrainBreakdownTests.ExportPackage(map, 32, 48, 3, Path.Combine(_root, "edge"));
        var edit = new { raise_edge = new { tile = "Tile 32_48", edge = "E", rows = new[] { 40, 72 }, height = 6.0, width = 4 }, sync = true };
        var rep = ThroughBlender(pkg.Folder, edit, "edge");
        if (rep == null) return;

        // the Blender scene: one object per tile, own collection, colour, label and edge groups
        var tiles = rep["import"]!["tiles"]!.AsArray();
        Assert.Equal(9, tiles.Count);
        var names = tiles.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(pkg.Manifest.Tiles.Select(t => $"Tile {t.X}_{t.Y}").OrderBy(n => n), names.OrderBy(n => n));
        foreach (var t in tiles)
        {
            Assert.Equal(t!["name"]!.GetValue<string>(), t["collection"]!.GetValue<string>());
            Assert.Equal(new[] { "EDGE_N", "EDGE_E", "EDGE_S", "EDGE_W" }, t["groups"]!.AsArray().Select(g => g!.GetValue<string>()));
            Assert.Equal(TerrainGrid.Count, t["vertices"]!.GetValue<int>());
            var pt = pkg.Manifest.Tiles.Single(p => p.Id == t["name"]!.GetValue<string>());
            var col = t["color"]!.AsArray().Select(c => c!.GetValue<double>()).ToArray();
            Assert.Equal(pt.Color[0], col[0], 3);
            Assert.Equal(pt.Color[1], col[1], 3);
        }
        Assert.Equal(9, tiles.Select(t => string.Join(",", t!["color"]!.AsArray().Select(c => Math.Round(c!.GetValue<double>(), 3)))).Distinct().Count());
        Assert.Equal(9, rep["labels"]!.AsArray().Count);
        Assert.Equal(9, rep["materials"]!.AsObject().Count);
        Assert.Empty(rep["seams_at_import"]!.AsArray());
        // the edit opened a seam with the neighbour, "Sync shared edges" closed it
        var opened = Assert.Single(rep["seams_after_edit"]!.AsArray());
        Assert.Equal("Tile 33_48", opened!["b"]!.GetValue<string>());
        Assert.Equal(33, opened["vertices"]!.GetValue<int>());
        Assert.Equal(33, rep["sync"]!["synced"]!.GetValue<int>());
        Assert.Empty(rep["seams_after_sync"]!.AsArray());

        // back in the app: no seam left, two tiles changed, only the chunks along the edge
        var ws = TerrainBreakdownTests.FakeWorkspace(Path.Combine(_root, "ws"));
        var a = Analyze(pkg.Folder, ws);
        Log(a);
        Assert.Empty(a.Seams);
        Assert.Equal(new[] { "Azeroth_32_48.adt", "Azeroth_33_48.adt" }, a.Changes.Where(c => !c.Unchanged).Select(c => c.FileName));
        Assert.Equal(7, a.Changes.Count(c => c.Unchanged));
        var here = a.Changes.Single(c => c.FileName == "Azeroth_32_48.adt");
        var there = a.Changes.Single(c => c.FileName == "Azeroth_33_48.adt");
        foreach (var c in new[] { here, there })
        {
            Assert.True(c.CanInclude && c.Include && c.Validation.Ok, c.Summary);
            Assert.Contains(c.Notes, n => n.StartsWith("Normals (lighting) recomputed", StringComparison.Ordinal));
        }
        var origHere = TestEnv.Client.Read(map.TilePath(32, 48))!;
        var origThere = TestEnv.Client.Read(map.TilePath(33, 48))!;
        Assert.Equal(new[] { 79, 95, 111, 127, 143, 159 }, TerrainBreakdownTests.ChangedChunks(origHere, here.NewBytes, "ofsHeight"));
        Assert.All(TerrainBreakdownTests.ChangedChunks(origHere, here.NewBytes, null), c => Assert.True(c % 16 == 15 && c / 16 is >= 4 and <= 9, $"chunk {c}"));
        Assert.Equal(new[] { 64, 80, 96, 112, 128, 144 }, TerrainBreakdownTests.ChangedChunks(origThere, there.NewBytes, "ofsHeight"));
        Assert.All(TerrainBreakdownTests.ChangedChunks(origThere, there.NewBytes, null), c => Assert.True(c % 16 == 0 && c / 16 is >= 4 and <= 9, $"neighbour chunk {c}"));
        Assert.Equal(TerrainBreakdownTests.StripTerrain(origHere), TerrainBreakdownTests.StripTerrain(here.NewBytes));
        Assert.Equal(TerrainBreakdownTests.StripTerrain(origThere), TerrainBreakdownTests.StripTerrain(there.NewBytes));
        // seams identical in the files, heights raised by the edit, normals follow the new heights
        var ga = TerrainGrid.FromTile(TerrainTile.Load(here.NewBytes, "Azeroth", 32, 48));
        var gb = TerrainGrid.FromTile(TerrainTile.Load(there.NewBytes, "Azeroth", 33, 48));
        for (int r = 0; r <= 128; r++) Assert.Equal(ga.O(r, 128), gb.O(r, 0));
        var g0 = TerrainGrid.FromTile(TerrainTile.Load(origHere, "Azeroth", 32, 48));
        Assert.Equal(6.0, ga.O(56, 128) - g0.O(56, 128), 3);
        Assert.Equal(0.0, ga.O(30, 128) - g0.O(30, 128), 6);
        var tile = TerrainTile.Load(here.NewBytes, "Azeroth", 32, 48);
        var field = new TerrainNormals.Field(32, 48, (x, y) => x == 32 && y == 48 ? ga : x == 33 && y == 48 ? gb : null);
        foreach (var probe in new[] { TerrainGrid.OuterId(56, 126), TerrainGrid.InnerId(50, 125) })
        {
            var (pc, pk) = TerrainGrid.Owner(probe);
            Assert.True(Vector3.Dot(tile.Chunks[pc]!.Normals[pk], TerrainNormals.Normal(field, probe)) > 0.9999f);
        }
        // one change set into the (fake) workspace, then reverted
        var cs = MapImporter.Commit(a, ws);
        Assert.Equal(2, cs.Files.Count);
        Assert.Empty(Workspace.MapProblems(ws.ReadEdited(map.TilePath(32, 48))!, map.TilePath(32, 48)));
        ws.RevertChangeSet(cs.Id);
        Assert.False(ws.IsModified(map.TilePath(33, 48)));
    }

    [Fact]
    public void UnchangedRoundTripThroughBlenderIsNoChange()
    {
        var pkg = TerrainBreakdownTests.ExportPackage(Azeroth, 32, 48, 3, Path.Combine(_root, "same"), references: true);
        var rep = ThroughBlender(pkg.Folder, null, "same");
        if (rep == null) return;
        Assert.True(rep["import"]!["references"]!.GetValue<int>() > 100, "the doodads and buildings were imported as references");
        var a = Analyze(pkg.Folder, TerrainBreakdownTests.FakeWorkspace(Path.Combine(_root, "ws")));
        Log(a);
        Assert.True(a.NothingChanged);
        Assert.Equal(9, a.Changes.Count(c => c.Unchanged));
        Assert.Contains(a.General, g => g.StartsWith("No changes found", StringComparison.Ordinal));
        Assert.Contains(a.General, g => g.Contains("only for reference", StringComparison.Ordinal));
        Assert.Empty(a.Ignored);
    }

    [Fact]
    public void SidewaysMovesAndAddedVerticesAreSnappedWithAMessage()
    {
        var pkg = TerrainBreakdownTests.ExportPackage(Azeroth, 32, 48, 1, Path.Combine(_root, "xy"));
        var edit = new { move_xy = new { tile = "Tile 32_48", count = 25, offset = new[] { 1.5, -0.7 } }, add_vertex = new { tile = "Tile 32_48" } };
        var rep = ThroughBlender(pkg.Folder, edit, "xy");
        if (rep == null) return;
        var a = Analyze(pkg.Folder, TerrainBreakdownTests.FakeWorkspace(Path.Combine(_root, "ws")));
        Log(a);
        Assert.True(a.NothingChanged);
        Assert.Contains(a.Ignored, s => s.StartsWith("25 vertices moved sideways (up to 1.66 yards) were put back on the grid", StringComparison.Ordinal));
        Assert.Contains(a.Ignored, s => s.StartsWith("1 vertex added in Blender was ignored", StringComparison.Ordinal));
        Assert.Contains(a.General, g => g.StartsWith("Ignored: 25 vertices moved sideways", StringComparison.Ordinal));
        Assert.Contains(a.Changes.Single().Notes, n => n.Contains("put back on the grid", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenInBlenderImportsTheTerrainPackage()
    {
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            _out.WriteLine("Blender 4.5 not found: skipped.");
            return;
        }
        var pkg = TerrainBreakdownTests.ExportPackage(Azeroth, 32, 48, 1, Path.Combine(_root, "open"));
        var report = Path.Combine(_root, "open_report.json");
        var psi = new System.Diagnostics.ProcessStartInfo(blender) { UseShellExecute = false };
        foreach (var arg in new[] { "--factory-startup", "--python", Path.Combine(AddonRoot, "studio", "studio_open.py"), "--", pkg.ManifestPath, AddonRoot, BlenderTools.AddonVersion(AddonRoot) })
            psi.ArgumentList.Add(arg);
        psi.Environment["WOWDATA_OPEN_REPORT"] = report;
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(report) && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(250);
            Thread.Sleep(1500);
        }
        Assert.True(File.Exists(report), "Blender did not open the terrain package");
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        _out.WriteLine(rep.ToJsonString());
        Assert.False(rep["background"]!.GetValue<bool>());
        Assert.Equal(BlenderTools.AddonVersion(AddonRoot), rep["version"]!.GetValue<string>());   // the app's own add-on was loaded
        Assert.Equal(1, rep["import"]!["objects"]!.GetValue<int>());
        Assert.Contains("Tile 32_48", rep["object_names"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}
