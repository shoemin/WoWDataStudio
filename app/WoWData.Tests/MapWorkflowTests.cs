using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using WoWData.Core.Maps;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// The map workflow without Noggit itself: a scripted tile edit saved into a Noggit project folder (as if Noggit had
/// saved it) is found, validated, described and imported as one change set; broken files are refused in plain words;
/// the Noggit project and the client view (hard links, never the game folder); the map list, minimap and tile report.
/// Everything is written into temporary folders; the real Client\Data is checked to be unchanged.
/// </summary>
public class MapWorkflowTests
{
    private const string Tile = @"World\Maps\Azeroth\Azeroth_32_48.adt";
    private const string Other = @"World\Maps\Azeroth\Azeroth_32_49.adt";

    private static MapInfo Azeroth() => MapCatalog.Load(TestEnv.Client, null).ByDirectory("Azeroth")!;

    [Fact]
    public void ScriptedTileEditImportsAsOneChangeSet()
    {
        var before = TestEnv.DataSnapshot();
        var root = TestEnv.TempDir("mapimport");
        try
        {
            var cp = new AppPaths(Path.Combine(root, "home")).ForClient(TestEnv.ClientDir);
            var ws = new Workspace(cp);
            var save = Path.Combine(root, "project", "World", "Maps", "Azeroth");
            Directory.CreateDirectory(save);
            // the edited tile: raised heights in 20 chunks, recomputed normals, two doodads moved
            var orig = TestEnv.Client.Read(Tile)!;
            var doc = new AdtDocument(orig, Tile);
            for (int c = 40; c < 60; c++)
            {
                var h = doc.GetHeights(c);
                for (int k = 0; k < 145; k++) h[k] += 7.5f;
                doc.SetHeights(c, h);
                doc.RecomputeNormals(c);
            }
            var adt = new AdtFile(orig);
            doc.SetDoodadPosition(5, adt.Mddf[5].Position + new Vector3(3, 0, 0));
            doc.SetDoodadPosition(6, adt.Mddf[6].Position + new Vector3(0, 0, -3));
            File.WriteAllBytes(Path.Combine(save, "Azeroth_32_48.adt"), doc.Serialize());
            // an untouched tile Noggit re-saved, a broken one, and a file the import does not handle
            File.WriteAllBytes(Path.Combine(save, "Azeroth_32_49.adt"), TestEnv.Client.Read(Other)!);
            var broken = (byte[])TestEnv.Client.Read(@"World\Maps\Azeroth\Azeroth_33_48.adt")!.Clone();
            var b = new AdtFile(broken);
            BitConverter.TryWriteBytes(broken.AsSpan((int)b.Mcnk[20]!.Offset + 8 + 0x0C), 5u);   // five texture layers
            File.WriteAllBytes(Path.Combine(save, "Azeroth_33_48.adt"), broken);
            File.WriteAllText(Path.Combine(save, "notes.txt"), "not a map file");

            var map = Azeroth();
            var a = MapImporter.Analyze(map, save, TestEnv.Client, ws);
            Assert.Equal(4, a.Changes.Count);
            var edited = a.Changes.Single(c => c.FileName == "Azeroth_32_48.adt");
            Assert.True(edited.CanInclude && edited.Include && !edited.Unchanged && edited.Validation.Ok);
            Assert.Equal((32, 48), edited.Tile);
            Assert.Equal(20, edited.Diff!.Stats.HeightChunks);
            Assert.Equal(2, edited.Diff.Stats.DoodadsMoved);
            Assert.Equal("Heights changed in 20 chunks (up to 7.5 yards), 2 doodads moved.", edited.Summary);
            Assert.True(a.Changes.Single(c => c.FileName == "Azeroth_32_49.adt").Unchanged);
            var bad = a.Changes.Single(c => c.FileName == "Azeroth_33_48.adt");
            Assert.True(bad.HasErrors && !bad.CanInclude);
            Assert.Contains(bad.Problems, p => p.StartsWith("Error: A terrain square has too many texture layers", StringComparison.Ordinal));
            Assert.Null(a.Changes.Single(c => c.FileName == "notes.txt").Kind);
            Assert.Contains(a.General, g => g.Contains("update the server's maps", StringComparison.Ordinal));

            var cs = MapImporter.Commit(a, ws);
            Assert.Single(cs.Files);
            Assert.StartsWith("Map import: Eastern Kingdoms (tile 32,48)", cs.Label);
            Assert.True(ws.IsModified(Tile));
            var rec = ws.Get(Tile)!;
            Assert.Equal("adt", rec.Kind);
            Assert.Equal("ADT terrain tile", rec.Original.Format);
            Assert.Equal(TestEnv.Client.Find(Tile)!.Name, rec.Original.Archive);
            Assert.Contains("Eastern Kingdoms tile 32,48", rec.Original.UsedBy);
            // the app now has the edit: the same files are "unchanged" against your earlier import
            var again = MapImporter.Analyze(map, save, TestEnv.Client, ws);
            var e2 = again.Changes.Single(c => c.FileName == "Azeroth_32_48.adt");
            Assert.True(e2.Unchanged);
            Assert.Equal("your earlier import", e2.BaseLabel);
            // the workspace refuses a broken map file on its own too
            var ex = Assert.Throws<WorkspaceException>(() => ws.SaveEdit(@"World\Maps\Azeroth\Azeroth_33_48.adt", broken, new OriginInfo(), ws.BeginChangeSet("x")));
            Assert.Contains("would not load correctly", ex.Message);
            // revert the import: back to the game's tile
            ws.RevertChangeSet(cs.Id);
            Assert.False(ws.IsModified(Tile));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Assert.Equal(before, TestEnv.DataSnapshot());
    }

    [Fact]
    public void NoggitProjectAndClientViewNeverTouchTheGame()
    {
        var root = TestEnv.TempDir("noggit");
        try
        {
            // a fake game folder
            var game = Path.Combine(root, "Game");
            var data = Directory.CreateDirectory(Path.Combine(game, "Data")).FullName;
            foreach (var n in new[] { "terrain.MPQ", "model.MPQ", "patch.MPQ", "patch-3.MPQ" }) File.WriteAllText(Path.Combine(data, n), "fake archive " + n);
            File.WriteAllText(Path.Combine(game, "WoW.exe"), "fake");
            var hashes = Directory.GetFiles(data).ToDictionary(f => f, Workspace.Sha256File);
            var settings = new AppSettings();
            var cp = new AppPaths(Path.Combine(root, "home")).ForClient(game);
            var svc = new NoggitService(cp, settings);
            Directory.CreateDirectory(Path.Combine(svc.ViewDir, "Data"));
            File.WriteAllText(Path.Combine(svc.ViewDir, "Data", "patch-4.MPQ"), "stale");     // a view file the game no longer has

            var (clientDir, note) = svc.PrepareClient(false);
            Assert.Equal(svc.ViewDir, clientDir);
            Assert.Contains("linked", note);
            foreach (var n in new[] { "terrain.MPQ", "model.MPQ", "patch.MPQ", "patch-3.MPQ" })
                Assert.True(NoggitService.SameFile(Path.Combine(data, n), Path.Combine(clientDir, "Data", n)), $"{n} is a hard link of the game's file");
            Assert.False(File.Exists(Path.Combine(clientDir, "Data", "patch-4.MPQ")));
            Assert.False(File.Exists(Path.Combine(clientDir, "WoW.exe")));
            // a second run is a no-op, and a rebuilt patch-3.MPQ (a new file) is linked again
            svc.PrepareClient(false);
            File.Delete(Path.Combine(data, "patch-3.MPQ"));
            File.WriteAllText(Path.Combine(data, "patch-3.MPQ"), "rebuilt");
            hashes[Path.Combine(data, "patch-3.MPQ")] = Workspace.Sha256File(Path.Combine(data, "patch-3.MPQ"));
            svc.PrepareClient(false);
            Assert.True(NoggitService.SameFile(Path.Combine(data, "patch-3.MPQ"), Path.Combine(clientDir, "Data", "patch-3.MPQ")));
            // the game folder itself is refused as Noggit's client
            settings.NoggitClientDir = game;
            Assert.Throws<NoggitException>(() => svc.PrepareClient(false));
            settings.NoggitClientDir = null;

            // the project: Vanilla, the client view, the map pinned, a bookmark at the tile; user bookmarks are kept
            var map = Azeroth();
            var info = svc.WriteProject(map, 32, 48, 120f, "Northshire Valley", clientDir, note);
            Assert.Equal(Path.Combine(cp.Dir, "noggit", "projects", "Azeroth", "WDS_Azeroth.noggitproj"), info.ProjectFile);
            Assert.Equal(Path.Combine(cp.Dir, "noggit", "projects", "Azeroth", "World", "Maps", "Azeroth"), info.SaveDir);
            Assert.True(Directory.Exists(info.SaveDir));
            using (var doc = JsonDocument.Parse(File.ReadAllText(info.ProjectFile)))
            {
                var p = doc.RootElement.GetProperty("Project");
                Assert.Equal("WDS_Azeroth", p.GetProperty("ProjectName").GetString());
                Assert.Equal("Vanilla", p.GetProperty("Client").GetProperty("ClientVersion").GetString());
                Assert.Equal(Path.GetFullPath(clientDir).Replace('\\', '/'), p.GetProperty("Client").GetProperty("ClientPath").GetString());
                var bm = p.GetProperty("Bookmarks")[0];
                Assert.Equal("WoW Data Studio: Northshire Valley, tile 32,48", bm.GetProperty("BookmarkName").GetString());
                Assert.Equal(0, bm.GetProperty("MapId").GetInt32());
                Assert.Equal(32 * 533.3333 + 266.6667, bm.GetProperty("Position").GetProperty("X").GetDouble(), 0.01);
                Assert.Equal(48 * 533.3333 + 266.6667, bm.GetProperty("Position").GetProperty("Z").GetDouble(), 0.01);
                Assert.Equal(180.0, bm.GetProperty("Position").GetProperty("Y").GetDouble(), 0.01);
                Assert.Equal(0, p.GetProperty("PinnedMaps")[0].GetProperty("MapId").GetInt32());
            }
            // a bookmark the user added in Noggit survives the next "Edit in Noggit"
            var text = File.ReadAllText(info.ProjectFile).Replace("\"Bookmarks\": [", "\"Bookmarks\": [ { \"BookmarkName\": \"My spot\", \"MapId\": 0, \"CameraPitch\": 1, \"CameraYaw\": 2, \"Position\": { \"X\": 1, \"Y\": 2, \"Z\": 3 } },");
            File.WriteAllText(info.ProjectFile, text);
            File.WriteAllText(Path.Combine(info.ProjectDir, "Other.noggitproj"), "{}");
            svc.WriteProject(map, 31, 49, 50f, null, clientDir, note);
            using (var doc = JsonDocument.Parse(File.ReadAllText(info.ProjectFile)))
            {
                var names = doc.RootElement.GetProperty("Project").GetProperty("Bookmarks").EnumerateArray().Select(x => x.GetProperty("BookmarkName").GetString()).ToList();
                Assert.Equal(new[] { "WoW Data Studio: Eastern Kingdoms, tile 31,49", "My spot" }, names);
            }
            Assert.Single(Directory.GetFiles(info.ProjectDir, "*.noggitproj"));

            // the workspace's map files go into the project; after "Revert" the identical copies are removed
            var ws = new Workspace(cp);
            var bytes = TestEnv.Client.Read(Tile)!;
            var edit = new AdtDocument(bytes);
            var hh = edit.GetHeights(0);
            hh[0] += 1;
            edit.SetHeights(0, hh);
            var editedBytes = edit.Serialize();
            ws.SaveEdit(Tile, editedBytes, new OriginInfo(), ws.BeginChangeSet("t"));
            Assert.Equal(1, svc.SyncFromWorkspace(map, ws));
            Assert.Equal(0, svc.SyncFromWorkspace(map, ws));
            var sha = ws.Get(Tile)!.WorkspaceSha256;
            ws.Revert(Tile);
            Assert.Equal(1, svc.ForgetReverted(map, new[] { (Tile, sha) }));
            Assert.False(File.Exists(Path.Combine(info.SaveDir, "Azeroth_32_48.adt")));

            // the game folder's files are unchanged
            foreach (var (f, h) in hashes) Assert.Equal(h, Workspace.Sha256File(f));
            Assert.True(File.Exists(Path.Combine(game, "WoW.exe")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MapListMinimapAndTileReport()
    {
        var cat = MapCatalog.Load(TestEnv.Client, null);
        Assert.True(cat.Maps.Count >= 40, $"{cat.Maps.Count} maps");
        var ek = cat.ByDirectory("Azeroth")!;
        Assert.Equal("Eastern Kingdoms", ek.Name);
        Assert.Equal(687, ek.TileCount);
        Assert.Equal("Continent", ek.Kind);
        Assert.Equal(1018, cat.ByDirectory("Kalimdor")!.TileCount);
        Assert.Equal("Raid", cat.ById(409)!.Kind);                 // Molten Core
        Assert.Equal("Battleground", cat.ById(489)!.Kind);         // Warsong Gulch
        Assert.Contains(cat.Maps, m => m.IsGlobalWmo);
        Assert.Equal("Continent", cat.Maps[0].Kind);

        var mm = cat.MinimapPath(ek, 32, 48);
        Assert.NotNull(mm);
        Assert.StartsWith(@"textures\Minimap\", mm);
        var t = cat.MinimapTile(ek, 32, 48)!;
        Assert.Equal(256, t.Width);
        var root = TestEnv.TempDir("minimap");
        try
        {
            var wsg = cat.ById(489)!;
            var ov = cat.Overview(wsg, 32, root);
            Assert.False(ov.FromCache);
            Assert.Equal((ov.Columns, ov.Rows), (ov.Image.Width / 32, ov.Image.Height / 32));
            Assert.Equal((wsg.Bounds.MinX - 1, wsg.Bounds.MinY - 1), (ov.MinX, ov.MinY));
            var again = cat.Overview(wsg, 32, root);
            Assert.True(again.FromCache);
            Assert.Equal(ov.Image.Pixels, again.Image.Pixels);
            Assert.Equal((ov.MinX + 1, ov.MinY + 2), ov.TileAt(32 * 1 + 5, 32 * 2 + 5));
        }
        finally
        {
            Directory.Delete(root, true);
        }

        var tile = TerrainTile.Load(TestEnv.Client.Read(Tile)!, "Azeroth", 32, 48);
        var rep = cat.TileReport(ek, 32, 48, tile, tile.Chunks.Select(c => c?.AreaId ?? 0).ToArray());
        Assert.Equal("tile:0:32:48", rep.Key);
        Assert.Contains(rep.Section("zones")!.Lines, l => l.Text.StartsWith("Northshire Valley (Elwynn Forest)", StringComparison.Ordinal));
        Assert.Equal($"{tile.Doodads.Count} placements of {tile.Doodads.Select(d => d.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()} models", rep.Section("doodads")!.Summary);
        Assert.True(tile.Doodads.Count > 500 && tile.Wmos.Count > 3);
        Assert.Contains(tile.Doodads, d => d.Model.EndsWith("ElwynnBush09.m2", StringComparison.OrdinalIgnoreCase) && d.Model.EndsWith(".m2", StringComparison.Ordinal));   // .mdx read as .m2
    }
}
