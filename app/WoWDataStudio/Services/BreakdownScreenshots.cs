using System.IO;
using System.Numerics;
using System.Windows;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.6.0, the tile breakdown and terrain through Blender), in this run's own app folder (the
/// package and the scripted "Blender" edit are written into the run's export folder; Blender itself is not started;
/// the import goes into the run's workspace):
///   63_tile_breakdown            the breakdown of Azeroth 32,48: the grouped list, a model's placements highlighted in the tile
///   64_breakdown_texture         a ground texture on its own: the texture and its coverage map
///   65_breakdown_model           a doodad on its own, in the model renderer
///   66_breakdown_chunk_heatmap   a chunk on its own: the tile's height heat map with the chunk outlined
///   67_terrain_export_picker     "Open terrain in Blender": the tile picker (3 x 3 plus one clicked tile)
///   68_terrain_seam_dialog       "Import edited terrain": an edge that disagrees with its existing neighbour
///   69_terrain_import_review     the review after syncing the edge (two tiles, what was recomputed)
///   70_terrain_compare           before / after of the edited tile (two 3D views, height difference)
/// Renders: render_breakdown_highlight (must differ from the plain tile), render_breakdown_model.
/// Checks: the breakdown's groups and counts, Show in Browse lands on the texture entry, the F1 topics.
/// </summary>
internal static class BreakdownScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var m = b.Maps;
        b.SelectedTab = BrowserViewModel.MapsTab;
        await ScreenshotRunner.Settle(300);
        var ek = m.Maps.First(x => x.Directory.Equals(MapScreenshots.Map, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(m.SelectedMap, ek)) m.SelectedMap = ek;
        await ScreenshotRunner.Until(() => m.Overview != null, 120000);
        m.SelectTile(MapScreenshots.TileX, MapScreenshots.TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && m.CurrentTile != null && !m.PreviewLoading && m.Scene.Asset != null, 120000);

        // ---- the breakdown window
        var bw = m.OpenBreakdown(ek, MapScreenshots.TileX, MapScreenshots.TileY, w => ScreenshotRunner.Place(w, 1440, 900));
        var vm = bw.Vm;
        await ScreenshotRunner.Until(() => !vm.Loading, 120000);
        var bd = vm.Breakdown;
        Expect(bd != null && vm.TileScene.Asset != null, $"breakdown: {vm.Status}");
        if (bd == null) { bw.Close(); return; }
        Expect(bd.Groups.Select(g => g.Key).SequenceEqual(new[] { "terrain", "textures", "doodads", "buildings", "water", "other" }), "breakdown: terrain, textures, doodads, buildings, water and other groups");
        Expect(bd.Group("terrain").Items[0].Children.Count == 256, "breakdown: the terrain mesh has 256 chunks");
        Expect(bd.Group("doodads").Items.Sum(i => i.Count) == bd.Tile.Doodads.Count && bd.Group("buildings").Items.Count == bd.Tile.Wmos.Count,
            $"breakdown: {bd.Group("doodads").Summary}; {bd.Group("buildings").Summary}");
        Expect(Manual.GetTopic(bw) == "maps-and-terrain", "breakdown: F1 opens the maps chapter");
        var plain = await bw.CaptureAsync(640, 420);

        // a model group highlighted in the tile (the most placed model)
        var doodadGroups = vm.Tree.First(g => g.Group?.Key == "doodads").Children;
        var groupNode = doodadGroups.Where(n => n.Item!.Path!.Contains("treecanopy", StringComparison.OrdinalIgnoreCase) && n.Item.Count >= 4).OrderBy(n => n.Item!.Count).FirstOrDefault()
                        ?? doodadGroups.First();
        vm.Tree.First(g => g.Group?.Key == "doodads").IsExpanded = true;
        groupNode.IsExpanded = true;
        groupNode.IsSelected = true;
        vm.Selected = groupNode;
        await ScreenshotRunner.Settle(700);
        var hi = await bw.CaptureAsync(640, 420);
        Png.Save(hi, Path.Combine(outDir, "render_breakdown_highlight.png"), false);
        Expect(ScreenshotRunner.LooksRendered(hi, out var why) && ScreenshotRunner.Differs(hi, plain) > 0, $"breakdown: the highlighted model renders differently from the plain tile ({why})");
        Expect(vm.TileScene.Highlight is { Doodads.Count: > 0 } h && h.Doodads.Count == groupNode.Item!.Count, $"breakdown: {groupNode.Title} highlighted ({groupNode.Item!.Count} placements)");
        int inAsset = vm.TileScene.Asset!.DoodadUids.Count(u => vm.TileScene.Highlight!.Doodads.Contains(u));
        int yellow = 0;
        for (int i = 0; i < hi.Pixels.Length; i += 4)
            if (hi.Pixels[i] > 150 && hi.Pixels[i + 1] > 110 && hi.Pixels[i + 2] < 90) yellow++;
        Expect(inAsset > 0 && yellow > 200, $"breakdown: the highlighted placements show in yellow ({inAsset} placements in the 3D view, {yellow} yellow pixels; camera {vm.TileCamera.Target} at {vm.TileCamera.Distance:0} yards)");
        await ScreenshotRunner.Shot(bw, outDir, "63_tile_breakdown");

        // a ground texture on its own: texture + coverage map
        var texNode = vm.Tree.First(g => g.Group?.Key == "textures").Children.First();
        vm.Selected = texNode;
        vm.OnItsOwn = true;
        await ScreenshotRunner.Until(() => vm.ImageB != null, 30000);
        Expect(vm.ImageA != null && vm.ImageB != null && vm.HasImages, $"breakdown: {texNode.Title} on its own with its coverage map ({vm.ImageBLabel})");
        await ScreenshotRunner.Shot(bw, outDir, "64_breakdown_texture");

        // a doodad on its own (the model renderer)
        var doodad = groupNode.Children.First(n => n.Item is { OnNeighbour: false });
        vm.Selected = doodad;
        await ScreenshotRunner.Until(() => vm.ActiveTerrain == null && vm.ModelScene.Asset != null, 60000);
        await ScreenshotRunner.Settle(600);
        var model = await bw.CaptureAsync(640, 420);
        Png.Save(model, Path.Combine(outDir, "render_breakdown_model.png"), false);
        Expect(ScreenshotRunner.LooksRendered(model, out var why2), $"breakdown: {doodad.Title} on its own in the model renderer ({why2})");
        await ScreenshotRunner.Shot(bw, outDir, "65_breakdown_model");

        // a chunk on its own: the height heat map
        var terrainGroup = vm.Tree.First(g => g.Group?.Key == "terrain");
        terrainGroup.IsExpanded = true;
        var chunkNode = terrainGroup.Children[0].Children[9 * 16 + 8];
        terrainGroup.Children[0].IsExpanded = true;
        vm.Selected = chunkNode;
        await ScreenshotRunner.Until(() => vm.HasImages && vm.ImageB == null && vm.ImageA != null, 30000);
        Expect(chunkNode.Title == "Chunk 8,9", $"breakdown: {chunkNode.Title} as a height heat map");
        await ScreenshotRunner.Shot(bw, outDir, "66_breakdown_chunk_heatmap");

        // Show in Browse: the texture entry, with its "Used in game"
        vm.OnItsOwn = false;
        vm.Selected = texNode;
        Expect(vm.CanShowInBrowse, $"breakdown: {texNode.Title} can be shown in Browse");
        await vm.ShowInBrowseCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => !b.DetailsLoading && b.HasSelection, 30000);
        Expect(b.SelectedTab == 0 && string.Equals(b.SelectedRow?.Path, texNode.Item!.Path, StringComparison.OrdinalIgnoreCase),
            $"breakdown: Show in Browse selects {Path.GetFileName(texNode.Item!.Path)} in the Browse tab ({b.SelectedRow?.Path})");
        bw.Close();
        b.SelectedTab = BrowserViewModel.MapsTab;
        await ScreenshotRunner.Settle(300);

        // ---- "Open terrain in Blender": the tile picker
        var evm = m.MakeTerrainExportViewModel(ek, MapScreenshots.TileX, MapScreenshots.TileY, 3);
        evm.Toggle(MapScreenshots.TileX + 2, MapScreenshots.TileY);
        Expect(evm.Chosen.Count == 10, $"terrain export: 3 x 3 plus one clicked tile = {evm.Chosen.Count} tiles");
        var ew = new TerrainExportWindow(evm);
        ScreenshotRunner.Place(ew, 1000, 760);
        ew.Show();
        await ScreenshotRunner.Settle(500);
        Expect(Manual.GetTopic(ew) == "terrain-in-blender", "terrain export: F1 opens the terrain-in-Blender chapter");
        await ScreenshotRunner.Shot(ew, outDir, "67_terrain_export_picker");
        ew.Close();

        // the package (1 x 1, into this run's export folder) and a scripted edit standing in for Blender: the east edge raised
        evm.PresetCommand.Execute(1);
        evm.Folder = Path.Combine(outDir, "_work", "terrain");
        var res = await m.ExportTerrainAsync(evm);
        Expect(File.Exists(res.ManifestPath) && res.Manifest.Tiles.Count == 1, $"terrain export: {res.Folder} ({res.Bytes / 1e6:0.0} MB)");
        TerrainEdit.WriteScripted(res.Folder, (t, id, co) =>
        {
            var (r, c) = TerrainGrid.RowCol(id);
            float row = TerrainGrid.IsInner(id) ? r + 0.5f : r, col = TerrainGrid.IsInner(id) ? c + 0.5f : c;
            return row is >= 40 and <= 72 && col >= 120 ? co + new Vector3(0, 0, 9f * (col - 119) / 9f) : null;
        });
        var a = await Task.Run(() => m.AnalyzeTerrain(res.Folder, false));
        Expect(a.Seams.Count == 1 && a.Seams[0].ExistingNeighbour, $"terrain import: the raised edge disagrees with the existing neighbour ({string.Join("; ", a.Seams.Select(s => s.Text))})");
        var dlg = Dialogs.Build("Edges do not match their neighbours",
            "The heights on these shared edges differ between two tiles. In the game the ground would have a crack (a gap) there:\n\n" + string.Join("\n", a.Seams.Select(s => "• " + s.Text)) +
            "\n\nSync them: the heights of the edited side are copied onto the other tile's edge (a neighbour you did not edit is then saved too). Or cancel and fix it in Blender (\"Sync shared edges\").",
            DialogKind.Warning, new[] { "Sync the edges (copy from the edited side)", "Cancel" }, null, null, null, out _);
        dlg.WindowStartupLocation = WindowStartupLocation.Manual;
        dlg.Left = -30000;
        dlg.ShowActivated = false;
        dlg.SizeToContent = SizeToContent.Manual;
        dlg.MaxWidth = 800;
        dlg.Width = 760;
        dlg.Height = 400;
        dlg.Show();
        await ScreenshotRunner.Shot(dlg, outDir, "68_terrain_seam_dialog");
        dlg.Close();
        var synced = await Task.Run(() => m.AnalyzeTerrain(res.Folder, true));
        Expect(synced.Seams.Count == 0 && synced.Changes.Count(c => c.CanInclude) == 2 && synced.Changes.All(c => c.Validation.Ok),
            $"terrain import: synced, two tiles to save ({string.Join("; ", synced.Changes.Select(c => c.FileName + ": " + c.Summary))})");
        var ivm = m.MakeImportViewModel(synced);
        var iw = new MapImportWindow(ivm);
        ScreenshotRunner.Place(iw, 1060, 860);
        iw.Show();
        Expect(ivm.Heading.StartsWith("Import edited terrain", StringComparison.Ordinal), $"terrain import review: {ivm.Heading}");
        await ScreenshotRunner.Shot(iw, outDir, "69_terrain_import_review");
        iw.Close();
        var row = synced.Changes.First(c => c.Tile == (MapScreenshots.TileX, MapScreenshots.TileY));
        var cmp = await m.MakeCompareAsync(row.BaseBytes, row.NewBytes, row.GamePath, "Before (your earlier import)", "Your terrain from Blender (to import)");
        if (cmp != null)
        {
            var cw = new MapCompareWindow(cmp);
            ScreenshotRunner.Place(cw, 1400, 860);
            cw.Show();
            await ScreenshotRunner.Settle(1200);
            await ScreenshotRunner.Shot(cw, outDir, "70_terrain_compare");
            cw.Close();
        }
        Expect(cmp != null && cmp.Summary.StartsWith("Heights changed in", StringComparison.Ordinal), $"terrain compare: {cmp?.Summary}");
        var cs = await Task.Run(() => MapImporter.Commit(synced, b.Session.Workspace!));
        Expect(cs.Files.Count == 2 && cs.Label.StartsWith("Terrain from Blender", StringComparison.Ordinal), $"terrain import saved as one change set ({cs.Label})");
        m.RefreshChangedTiles();
        b.SelectedTab = 0;
    }
}
