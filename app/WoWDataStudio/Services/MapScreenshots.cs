using System.IO;
using System.Numerics;
using System.Windows;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (phase 4, maps and terrain), in this run's own temporary app folder; nothing is installed into
/// the game or the server, Noggit is not started, no server script runs:
///   46_maps_tab                the Maps tab: Eastern Kingdoms, the Elwynn Forest zone, tile 32,48 with its 3D view
///   47_maps_tile_details       the tile's details and "On this tile" (zones, doodads, buildings, textures, spawns)
///   48_terrain_neighbours      the 3D preview with the 8 neighbour tiles
///   49_edit_in_noggit          "Edit in Noggit": the prepared project (client view + bookmark) and the steps
///   50_map_import_review       "Import map changes" after a scripted edit saved into the Noggit project folder
///   51_map_compare             before / after: two synchronized 3D terrain views, height difference, changes
///   52_maps_tile_modified      the Maps tab after the import (orange tile, Modified)
///   53_server_update           "Update server maps" (nothing is run)
///   54_my_changes_map          My changes with the map import
/// Renders: render_terrain_tile, render_terrain_neighbours, render_terrain_compare_left/right (must not be blank, the
/// compare sides must differ).
/// </summary>
internal static class MapScreenshots
{
    public const string Map = "Azeroth";
    public const int TileX = 32, TileY = 48;

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var m = b.Maps;
        ScreenshotRunner.Place(win, 1500, 1240);
        win.UpdateLayout();
        b.SelectedTab = BrowserViewModel.MapsTab;
        await m.EnsureLoadedAsync();
        await ScreenshotRunner.Until(() => m.Maps.Count > 0, 30000);
        ScreenshotRunner.Expect(m.Maps.Count >= 30, $"maps: {m.Maps.Count} maps listed ({m.Status})");
        ScreenshotRunner.Expect(m.Maps.Any(x => x.Kind == "Continent") && m.Maps.Any(x => x.InstanceType == 1) && m.Maps.Any(x => x.InstanceType == 2) && m.Maps.Any(x => x.InstanceType == 3),
            "maps: continents, dungeons, raids and battlegrounds");
        var ek = m.Maps.First(x => x.Directory.Equals(Map, StringComparison.OrdinalIgnoreCase));
        m.SelectedMap = ek;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ScreenshotRunner.Until(() => m.Overview != null && m.Zones.Count > 0, 120000);
        ScreenshotRunner.Expect(m.Overview!.PixelWidth > 500, $"minimap overview {m.Overview.PixelWidth}x{m.Overview.PixelHeight} in {sw.ElapsedMilliseconds} ms ({m.OverviewStatus})");
        var elwynn = m.Zones.FirstOrDefault(z => z.Name == "Elwynn Forest");
        ScreenshotRunner.Expect(elwynn != null && elwynn.Tiles.Count > 5, $"zones: Elwynn Forest covers {elwynn?.Tiles.Count} tiles ({m.Zones.Count} zones)");
        if (elwynn != null) m.SelectedZone = elwynn;
        m.SelectTile(TileX, TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && m.HasTile && m.CurrentTile != null && m.TileUsage.Report != null, 60000);
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset != null, 120000);
        var asset = m.Scene.Asset!;
        ScreenshotRunner.Expect(asset.Chunks.Count == 256 && asset.Doodads.Count > 50 && asset.Textures.Count > 2, $"terrain preview: {asset.Summary}");
        ScreenshotRunner.Expect(m.TileUsage.Report!.Section("doodads") != null && m.TileUsage.Report.Section("zones")!.Lines.Count > 0, "tile report: zones and doodads");
        await ScreenshotRunner.Settle(800);
        view.MapsScreen.FitMinimap();
        await ScreenshotRunner.Shot(win, outDir, "46_maps_tab");
        var tileImg = await view.MapsScreen.TerrainView.CaptureAsync(640, 420);
        Png.Save(tileImg, Path.Combine(outDir, "render_terrain_tile.png"), false);
        ScreenshotRunner.Expect(ScreenshotRunner.LooksRendered(tileImg, out var why), $"render terrain tile: {why}");
        view.MapsScreen.TileUsageBox.BringIntoView(new Rect(0, -280, Math.Max(1, view.MapsScreen.TileUsageBox.ActualWidth), 900));
        await ScreenshotRunner.Shot(win, outDir, "47_maps_tile_details");

        // the neighbours
        m.IncludeNeighbours = true;
        await ScreenshotRunner.Settle(300);
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset is { Tiles.Count: 9 }, 180000);
        m.Camera.Distance *= 2.2f;
        view.MapsScreen.PreviewBox.BringIntoView(new Rect(0, -120, Math.Max(1, view.MapsScreen.PreviewBox.ActualWidth), 640));
        await ScreenshotRunner.Settle(600);
        var nb = await view.MapsScreen.TerrainView.CaptureAsync(640, 420);
        Png.Save(nb, Path.Combine(outDir, "render_terrain_neighbours.png"), false);
        ScreenshotRunner.Expect(ScreenshotRunner.LooksRendered(nb, out var why2) && ScreenshotRunner.Differs(nb, tileImg) > 0, $"render terrain with neighbours: {why2} ({m.PreviewStatus})");
        await ScreenshotRunner.Shot(win, outDir, "48_terrain_neighbours");
        m.IncludeNeighbours = false;
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset is { Tiles.Count: 1 }, 60000);

        // Edit in Noggit: the project in this run's home (client view = hard links, no copies; Noggit is not started)
        var noggit = b.Session.Noggit!;
        var (clientDir, note) = await Task.Run(() => noggit.PrepareClient(false));
        ScreenshotRunner.Expect(Directory.EnumerateFiles(Path.Combine(clientDir, "Data"), "*.MPQ").Count() >= 12, $"noggit: client view ready ({note})");
        ScreenshotRunner.Expect(NoggitService.SameFile(Path.Combine(clientDir, "Data", "terrain.MPQ"), Path.Combine(b.Session.Client!.DataDir, "terrain.MPQ")), "noggit: the view's terrain.MPQ is a hard link of the game's (no copy)");
        var tile = m.CurrentTile!;
        var info = noggit.WriteProject(ek, TileX, TileY, tile.HeightAt(tile.Corner.X - 266, tile.Corner.Y - 266) ?? 100, "Northshire Valley", clientDir, note);
        var nvm = new NoggitLaunchViewModel(info, NoggitService.FindNoggit(b.Session.Settings, b.Session.Client.ClientDir), 0, false);
        var nw = new NoggitWindow(nvm);
        ScreenshotRunner.Place(nw, 860, 860);
        nw.Show();
        await ScreenshotRunner.Shot(nw, outDir, "49_edit_in_noggit");
        nw.Close();
        ScreenshotRunner.Expect(File.ReadAllText(info.ProjectFile).Contains("\"ClientVersion\": \"Vanilla\"") && File.ReadAllText(info.ProjectFile).Contains("WoW Data Studio: Northshire Valley, tile 32,48"),
            "noggit: project file with the Vanilla client and the bookmark");

        // a scripted edit saved into the project folder, as if Noggit had saved it: a hill, three doodads moved, a hole
        var path = ek.TilePath(TileX, TileY);
        var orig = b.Session.Data!.Read(path)!;
        var doc = new AdtDocument(orig, path);
        // a smooth hill (a function of the position, so the vertices shared by two chunks move together)
        const float hx = 8f * 8, hy = 9.5f * 8, radius = 4.5f * 8;
        for (int c = 0; c < 256; c++)
        {
            int cx = c % 16, cy = c / 16;
            var h = doc.GetHeights(c);
            bool changed = false;
            for (int k = 0; k < 145; k++)
            {
                int vr = k / 17, rem = k % 17;
                float i = rem < 9 ? vr : vr + 0.5f, j = rem < 9 ? rem : rem - 9 + 0.5f;
                float d = MathF.Sqrt((cx * 8 + j - hx) * (cx * 8 + j - hx) + (cy * 8 + i - hy) * (cy * 8 + i - hy)) / radius;
                if (d >= 1) continue;
                h[k] += 70f * (1 - d * d) * (1 - d * d);
                changed = true;
            }
            if (!changed) continue;
            doc.SetHeights(c, h);
            doc.RecomputeNormals(c);
        }
        var adt = new AdtFile(orig);
        for (int i = 0; i < 3; i++) doc.SetDoodadPosition(i, adt.Mddf[i].Position + new Vector3(6, 0, 4));
        doc.SetHoles(200, 0x0660);
        Directory.CreateDirectory(info.SaveDir);
        File.WriteAllBytes(Path.Combine(info.SaveDir, Path.GetFileName(path)), doc.Serialize());
        File.WriteAllBytes(Path.Combine(info.SaveDir, $"{Map}.wdt"), b.Session.Data.Read(ek.WdtPath)!);   // Noggit also rewrites the WDT (unchanged)

        var analysis = await Task.Run(() => MapImporter.Analyze(ek, info.SaveDir, b.Session.Data, b.Session.Workspace!));
        var row = analysis.Changes.FirstOrDefault(c => c.Kind == "adt");
        ScreenshotRunner.Expect(row != null && !row.Unchanged && row.CanInclude && row.Diff!.Stats.HeightChunks > 20, $"map import: the tile changed ({row?.Summary})");
        ScreenshotRunner.Expect(analysis.Changes.Any(c => c.Kind == "wdt" && c.Unchanged), "map import: the unchanged WDT is recognised");
        var ivm = m.MakeImportViewModel(analysis);
        var iw = new MapImportWindow(ivm);
        ScreenshotRunner.Place(iw, 1060, 860);
        iw.Show();
        await ScreenshotRunner.Shot(iw, outDir, "50_map_import_review");
        iw.Close();

        var cmp = await m.MakeCompareAsync(orig, row!.NewBytes, path, CompareViewModel.OriginalLabel, "Noggit's file (to import)");
        ScreenshotRunner.Expect(cmp != null && cmp.Summary.StartsWith("Heights changed in", StringComparison.Ordinal), $"map compare: {cmp?.Summary}");
        if (cmp != null)
        {
            var cw = new MapCompareWindow(cmp);
            ScreenshotRunner.Place(cw, 1400, 860);
            cw.Show();
            await ScreenshotRunner.Settle(1200);
            await ScreenshotRunner.Shot(cw, outDir, "51_map_compare");
            var (l, r) = await cw.CaptureBothAsync(560, 420);
            Png.Save(l, Path.Combine(outDir, "render_terrain_compare_left.png"), false);
            Png.Save(r, Path.Combine(outDir, "render_terrain_compare_right.png"), false);
            ScreenshotRunner.Expect(ScreenshotRunner.LooksRendered(l, out _) && ScreenshotRunner.LooksRendered(r, out _), "both sides of the terrain compare render");
            ScreenshotRunner.Expect(ScreenshotRunner.Differs(l, r) > 0, "the edited terrain renders differently from the original");
            cw.Close();
        }

        // save the import (one change set), then the Maps tab shows the modified tile
        var cs = await Task.Run(() => MapImporter.Commit(analysis, b.Session.Workspace!));
        ScreenshotRunner.Expect(cs.Files.Count == 1 && b.Session.Workspace!.IsModified(path), $"map import saved as one change set ({cs.Label})");
        await ScreenshotRunner.Settle(800);
        m.SelectTile(TileX, TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && m.IsTileModified, 30000);
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset != null, 60000);
        ScreenshotRunner.Expect(m.ChangedTiles?.Any(p => (int)p.X == TileX && (int)p.Y == TileY) == true, "the changed tile is marked on the map");
        view.MapsScreen.MapActions.BringIntoView(new Rect(0, -560, Math.Max(1, view.MapsScreen.MapActions.ActualWidth), 900));
        await ScreenshotRunner.Settle(600);
        await ScreenshotRunner.Shot(win, outDir, "52_maps_tile_modified");

        // the server update window (nothing is run)
        var svm = new ServerUpdateViewModel(b.Session, ScreenshotRunner.Main!, b.Session.MapCatalog!);
        ScreenshotRunner.Expect(svm.MapIds.SequenceEqual(new[] { 0 }), $"server update: map 0 to update ({svm.ChangedMaps})");
        ScreenshotRunner.Expect(!svm.PatchOk, "server update: the map change is not in the game's patch-3.MPQ yet (the run installs nothing)");
        var suw = new ServerUpdateWindow(svm);
        ScreenshotRunner.Place(suw, 1000, 1000);
        suw.Show();
        await ScreenshotRunner.Shot(suw, outDir, "53_server_update");
        suw.Close();

        b.SelectedTab = 1;
        await ScreenshotRunner.Settle(500);
        ScreenshotRunner.Expect(b.Changes.Any(c => c.Path.EndsWith($"{Map}_{TileX}_{TileY}.adt", StringComparison.OrdinalIgnoreCase)), "My changes lists the map tile");
        await ScreenshotRunner.Shot(win, outDir, "54_my_changes_map");
        b.SelectedTab = 0;
        ScreenshotRunner.Place(win, 1440, 900);
    }
}
