using System.IO;
using WoWData.Core.Index;
using WoWDataStudio.Rendering;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot`: another version opened read only IN THE MAIN WINDOW (docs\manual\other-versions.md). It needs
/// the folder of client copies in WDS_SCREENSHOT_OTHER_CLIENTS with the "1.60" Classic beta (_classic_beta_); without it
/// these pictures are skipped. WDS_SCREENSHOT_ONLY=versions-open makes only them. The client is only read; its index is
/// built in the run's own temporary app folder. At the end your 1.12 game is opened again.
///   144_versions_open_tab        the Other versions tab with the 1.60 client selected: "Open read only"
///   145_versions_open_creature   the main window with 1.60 open: the read-only banner, a creature display, its files
///                                with FileDataID, name source and state, the locked counts in the status line
///   146_versions_open_item       an item the client names: its models per race and sex, its textures
///   147_versions_open_texture    an interface icon of 1.60 in the texture viewer: only Export works, the rest is greyed
///   148_versions_open_creature_3d  the boar display in the 3D view (read by the modern reader, its skin)
///   149_versions_open_item_3d      the Worn Shortsword in the 3D view with its item texture; Export for Blender works
///   150_versions_open_anim_file    Taerar's EmoteRoar, kept in an .anim file, read when chosen, paused at 1.6 s
///   151_versions_open_particles    the Arcane_Missile_Lvl4 spell effect with its particles (simulated)
///   152_versions_open_maps         the Maps tab: Eastern Kingdoms (Azeroth) with Elwynn Forest lit on the minimap
///                                  overview, Goldshire's tile (32, 48) with its zones, facts and 3D view
///   153_versions_open_tile_3d      the same tile in 3D, closer on Goldshire's buildings and trees, and "On this tile"
///   154_versions_open_building     a building of that tile (followed from "On this tile" to Browse) in the building
///                                  preview: its groups and textures by FileDataID
///   157_versions_open_tables       the Data tables tab of 1.60: the Map table read only, Export rows, Save greyed out
///   158_versions_open_no_server    the Server data tab: "No server connected" for 1.60
///   159_versions_open_my_changes   My changes: no workspace, every button greyed out with the reason
///   160_versions_open_map_actions  the Maps tab's edit buttons greyed out with the reason under the tile
///   161_versions_open_used         "Used in game" of a creature display: its model's displays, no server connected
/// </summary>
internal static class VersionOpenScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static async Task Select(BrowserViewModel v, string search, Func<IndexEntry, bool> pick, string what)
    {
        v.SearchText = search;
        await v.ApplyFilterNow();
        var row = v.Results.FirstOrDefault(r => pick(r.Entry));
        Expect(row != null, $"versions-open: {what} is found by \"{search}\"");
        if (row == null) return;
        v.SelectedRow = row;
        await ScreenshotRunner.Until(() => v.HasSelection && v.DetailTitle.Length > 0 && !v.DetailsLoading, 30000);
        if (v.SelectedTexture is { Exists: true })
            await ScreenshotRunner.Until(() => v.Viewer.IsLoaded || v.Viewer.Error.Length > 0, 30000);
        if (v.IsModelEntry)
            await ScreenshotRunner.Until(() => ReferenceEquals(v.ModelViewer.Loaded?.Entry, row.Entry) && !v.ModelViewer.IsLoading && (v.ModelViewer.HasModel || v.ModelViewer.Message.Length > 0), 60000);
        await ScreenshotRunner.Settle(500);
    }

    /// <summary>The details scrolled to the 3D view, the model framed, drawn for a moment.</summary>
    private static async Task Show3D(MainWindow win, BrowserViewModel v, string what)
    {
        var m = v.ModelViewer;
        Expect(m.HasModel, $"versions-open: {what} is shown in the 3D view ({m.Message})");
        Expect(m.Stats.Any(x => x.Label == "Format" && x.Value.Contains("MD21", StringComparison.Ordinal)), $"versions-open: {what} was read by the modern reader");
        Expect(m.FormatNotes.Length > 0, $"versions-open: the 3D view says what the modern reader read for {what}");
        m.Playing = false;
        m.ResetViewCommand.Execute(null);
        win.BrowserScreen.ScrollToModel();
        await ScreenshotRunner.Settle(900);
        ScreenshotRunner.Log($"versions-open: {what}: {string.Join(", ", m.Stats.Select(x => $"{x.Label} {x.Value}"))}");
    }

    /// <summary>The Maps tab of 1.60 in the main window (152-154, 158).</summary>
    private static async Task MapsAsync(MainWindow win, BrowserViewModel vm, string outDir)
    {
        var m = vm.Maps;
        var screen = win.BrowserScreen.MapsScreen;
        vm.SelectedTab = BrowserViewModel.MapsTab;
        await m.EnsureLoadedAsync();
        await ScreenshotRunner.Until(() => m.Maps.Count > 0 || m.Status.StartsWith("The maps could not", StringComparison.Ordinal), 120000);
        Expect(m.Maps.Count >= 60, $"versions-open maps: {m.Maps.Count} maps listed ({m.Status})");
        Expect(!m.CanEdit && m.WriteRefusal != null, "versions-open maps: the Maps tab of another version cannot edit, and says why");
        Expect(m.Status.Contains("8 locked (encrypted, no key)", StringComparison.Ordinal), "versions-open maps: the status says how many Map rows are locked: " + m.Status);
        var az = m.Maps.FirstOrDefault(x => x.Directory.Equals("Azeroth", StringComparison.OrdinalIgnoreCase));
        Expect(az != null && az.TileCount == 736, $"versions-open maps: Azeroth has {az?.TileCount} tiles");
        if (az == null) return;
        if (m.SelectedMap != az) m.SelectedMap = az;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ScreenshotRunner.Until(() => m.Overview != null && m.Zones.Count > 0, 300000);
        Expect(m.Overview != null, $"versions-open maps: the minimap overview is built in {sw.ElapsedMilliseconds} ms ({m.OverviewStatus})");
        var elwynn = m.Zones.FirstOrDefault(z => z.Name == "Elwynn Forest");
        Expect(elwynn != null && elwynn.Tiles.Count > 5, $"versions-open maps: Elwynn Forest covers {elwynn?.Tiles.Count} tiles ({m.Zones.Count} zones)");
        if (elwynn != null) m.SelectedZone = elwynn;
        m.SelectTile(32, 48);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && m.HasTile && m.CurrentTile != null && m.TileUsage.Report != null, 120000);
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset != null, 180000);
        var asset = m.Scene.Asset;
        Expect(asset != null && asset.Chunks.Count == 256 && asset.Doodads.Count > 50 && asset.Wmos.Count > 0 && asset.MissingModels == 0, $"versions-open maps: the tile in 3D: {asset?.Summary}");
        Expect(m.TileFacts.Any(f => f.Contains("doodads", StringComparison.Ordinal)), "versions-open maps: the tile's facts: " + string.Join(" | ", m.TileFacts));
        ScreenshotRunner.Log($"versions-open maps: {m.Status} | {m.OverviewStatus} | {m.PreviewStatus}");
        await ScreenshotRunner.Settle(800);
        screen.FitMinimap();
        screen.TileScroll.ScrollToTop();
        await ScreenshotRunner.Settle(600);
        await ScreenshotRunner.Shot(win, outDir, "152_versions_open_maps");
        if (asset == null) return;

        // closer on the biggest building standing on the tile (Northshire Abbey) and the trees around it, "On this tile" under it
        (System.Numerics.Vector3 Lo, System.Numerics.Vector3 Hi) Box(WmoMesh mesh, System.Numerics.Matrix4x4 world)
        {
            var lo = new System.Numerics.Vector3(float.MaxValue);
            var hi = new System.Numerics.Vector3(float.MinValue);
            foreach (var vx in mesh.Vertices)
            {
                var p = System.Numerics.Vector3.Transform(vx.Position, world);
                lo = System.Numerics.Vector3.Min(lo, p);
                hi = System.Numerics.Vector3.Max(hi, p);
            }
            return (lo, hi);
        }
        bool OnTile(System.Numerics.Vector3 c) => c.X >= asset.BoundsMin.X && c.X <= asset.BoundsMax.X && c.Y >= asset.BoundsMin.Y && c.Y <= asset.BoundsMax.Y;
        var boxes = asset.Wmos.Select(x => (x.Mesh, Box: Box(x.Mesh, x.World))).Where(x => OnTile((x.Box.Lo + x.Box.Hi) / 2)).OrderByDescending(x => x.Mesh.Indices.Length).ToList();
        Expect(boxes.Count > 0, "versions-open maps: a building stands on the tile");
        if (boxes.Count > 0)
        {
            var (lo, hi) = boxes[0].Box;
            m.Camera.Set(215f, 30f, Math.Max(160f, (hi - lo).Length() * 2.8f), (lo + hi) / 2);
        }
        var img = await screen.TerrainView.CaptureAsync(640, 420);
        WoWData.Core.Imaging.Png.Save(img, Path.Combine(outDir, "render_versions_open_tile.png"), false);
        Expect(ScreenshotRunner.LooksRendered(img, out var why), $"versions-open maps: the tile renders: {why}");
        // the 3D view at the top of the tile panel, "On this tile" under it
        var top = screen.PreviewBox.TranslatePoint(new System.Windows.Point(0, 0), (System.Windows.UIElement)screen.TileScroll.Content).Y;
        screen.TileScroll.ScrollToVerticalOffset(Math.Max(0, top - 6));
        await ScreenshotRunner.Settle(900);
        await ScreenshotRunner.Shot(win, outDir, "153_versions_open_tile_3d");

        // the map's edit buttons, greyed out with the reason
        screen.MapActions.BringIntoView();
        await ScreenshotRunner.Settle(600);
        Expect(!screen.MapActions.IsEnabled || !screen.ImportTerrainButton.IsEnabled, "versions-open maps: the map's edit buttons are greyed out");
        Expect(Equals(screen.ImportTerrainButton.ToolTip, m.WriteRefusal), "versions-open maps: a greyed-out edit button gives the read-only reason on hover");
        await ScreenshotRunner.Shot(win, outDir, "160_versions_open_map_actions");

        // a building of the tile, followed from "On this tile" to its Browse entry and its building preview
        var linked = m.TileUsage.Report!.Section("wmos")?.Lines.SelectMany(l => l.Spans).Where(x => x.Key != null).Select(x => x.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        var link = m.CurrentTile!.Wmos.OrderByDescending(x => (x.ExtentMax - x.ExtentMin).Length()).Select(x => "file:" + x.Model).FirstOrDefault(linked.Contains);
        string? path = link?[5..];
        if (link != null) await vm.NavigateToKeyAsync(link, false);
        Expect(link != null, "versions-open maps: a building on the tile links to its Browse entry");
        await ScreenshotRunner.Until(() => vm.BuildingScene.Asset != null || vm.BuildingStatus.StartsWith("The building could not", StringComparison.Ordinal), 120000);
        Expect(vm.IsBuildingEntry && vm.BuildingScene.Asset is { Wmos.Count: 1 }, $"versions-open maps: {path} is shown in the building preview: {vm.BuildingStatus}");
        Expect(vm.BuildingStatus.Contains("by FileDataID (GFID, MOMT)", StringComparison.Ordinal), "versions-open maps: the building's groups and textures were found by FileDataID");
        ScreenshotRunner.Log($"versions-open maps: building {path}: {vm.BuildingStatus}");
        vm.BuildingCamera.ResetView();
        win.BrowserScreen.DetailsScroll.ScrollToTop();
        await ScreenshotRunner.Settle(900);
        await ScreenshotRunner.Shot(win, outDir, "154_versions_open_building");
    }

    public static async Task RunAsync(MainWindow win, MainViewModel main, BrowserViewModel b, string outDir)
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ScreenshotRunner.Log("versions-open: WDS_SCREENSHOT_OTHER_CLIENTS is not set to a folder of client copies; pictures 144-154 and 157-161 skipped");
            return;
        }
        var own = main.Session.Client?.ClientDir;
        var v = b.Versions;
        b.SelectedTab = BrowserViewModel.VersionsTab;
        v.Folder = folder;
        await v.FindClientsCommand.ExecuteAsync(null);
        var beta = v.Clients.Where(c => c.Copy.Format == WoWData.Core.Versions.ClientFormat.Casc && c.Copy.Version.StartsWith("1.60", StringComparison.Ordinal))
            .OrderByDescending(c => c.Build).FirstOrDefault();
        if (beta == null)
        {
            Expect(false, "versions-open: no 1.60 client (_classic_beta_) in the folder");
            return;
        }
        v.SelectedClient = beta;
        await v.Opening;
        win.BrowserScreen.VersionsScreen.ShowInnerTab(0);
        await ScreenshotRunner.Settle(500);
        Expect(v.CanConvertMaps, "versions-open: \"Open read only\" is shown for the 1.60 client");
        ScreenshotRunner.Main!.ToastVisible = false;
        await ScreenshotRunner.Shot(win, outDir, "144_versions_open_tab");

        // "Open read only": the 1.60 client in this window
        var opened = await main.OpenOtherClientAsync(beta.Copy, null);
        try
        {
            Expect(opened && main.Browser is { IsReadOnly: true }, "versions-open: the 1.60 client opened read only in the main window: " + main.WelcomeError);
            if (main.Browser is not { IsReadOnly: true } vm) return;
            vm.HotReloadLocator = HotReloadScreenshots.NoGame;
            main.ToastVisible = false;
            ScreenshotRunner.Log($"versions-open: {main.Session.Entries.Count} entries; {vm.StatusLine}; {vm.NamesLine}");
            Expect(main.Session.IsReadOnly && main.Session.Workspace == null, "versions-open: the session is read only, without a workspace");
            Expect(main.WindowTitle.Contains("(read only)", StringComparison.Ordinal), "versions-open: the window title says read only: " + main.WindowTitle);
            Expect(vm.StatusLine.Contains("creature displays readable", StringComparison.Ordinal), "versions-open: the status line shows the locked creature displays");
            Expect(vm.ServerStatusText.StartsWith("No server connected for ", StringComparison.Ordinal), "versions-open: the server button says no server is connected: " + vm.ServerStatusText);

            await Select(vm, "boar", e => e.Key.StartsWith("cdisplay:", StringComparison.Ordinal) && e.Path != null && !e.Path.StartsWith('#')
                                          && e.Name.Contains(" - ", StringComparison.Ordinal), "a boar creature display");
            Expect(vm.Files.Any(f => f.Role == "model"), "versions-open: the creature display lists its model with its FileDataID");
            win.BrowserScreen.DetailsScroll.ScrollToTop();
            await ScreenshotRunner.Settle(300);
            await ScreenshotRunner.Shot(win, outDir, "145_versions_open_creature");

            // "Used in game": what the client says, and that no server is connected
            vm.UsedInGameExpanded = true;
            var u = vm.UsedInGame;
            await ScreenshotRunner.Until(() => !u.IsLoading && u.Report != null, 30000);
            Expect(u.Report?.Section("users") != null, "versions-open: Used in game lists the displays of the model: " + u.Report?.Short);
            Expect(u.Report?.Notes.Any(n => n.StartsWith("No server is connected for ", StringComparison.Ordinal)) == true, "versions-open: Used in game says no server is connected");
            win.BrowserScreen.UsedInGameBox.BringIntoView();
            await ScreenshotRunner.Settle(500);
            await ScreenshotRunner.Shot(win, outDir, "161_versions_open_used");
            vm.UsedInGameExpanded = false;

            await Show3D(win, vm, "the boar display");
            await ScreenshotRunner.Shot(win, outDir, "148_versions_open_creature_3d");

            await Select(vm, "worn shortsword", e => e.Key == "item:25", "Worn Shortsword (item 25)");
            Expect(vm.Textures.Count > 0, "versions-open: the item lists its textures");
            win.BrowserScreen.DetailsScroll.ScrollToTop();
            await ScreenshotRunner.Settle(300);
            await ScreenshotRunner.Shot(win, outDir, "146_versions_open_item");
            await Show3D(win, vm, "the Worn Shortsword");
            Expect(vm.ModelViewer.ModelTextures.Any(t => t.Slot.StartsWith("object skin", StringComparison.Ordinal) && t.Path != null), "versions-open: the sword's item texture fills its slot");
            var pkg = await vm.ExportOtherModelAsync(WoWData.Core.ModelIO.ModelExportMode.CurrentLook, Path.Combine(outDir, "_export", "Models"), true);
            Expect(pkg != null && pkg.GltfFiles.Count == 1 && File.Exists(Path.Combine(pkg.Folder, pkg.GltfFiles[0])), "versions-open: Export for Blender writes the sword's package: " + pkg?.Folder);
            await ScreenshotRunner.Shot(win, outDir, "149_versions_open_item_3d");

            // an animation kept in an .anim file, read when it is chosen, paused in the middle
            await Select(vm, "taerar", e => e.Key == "cdisplay:15363", "Taerar's display (15363)");
            await Show3D(win, vm, "Taerar");
            var mv = vm.ModelViewer;
            var roar = mv.Sequences.FirstOrDefault(x => x.Name == "EmoteRoar");
            Expect(roar != null, "versions-open: Taerar lists EmoteRoar");
            if (roar != null)
            {
                mv.SelectedSequence = roar;
                mv.Playing = false;
                mv.TimeMs = 1600;
                Expect(mv.AnimationInfo.Contains(".anim file #", StringComparison.Ordinal) && mv.AnimationInfo.Contains("read when you chose it", StringComparison.Ordinal),
                    "versions-open: the animation line says EmoteRoar's keys were read from its .anim file: " + mv.AnimationInfo);
                var m = mv.Scene.Asset!.Model;
                Expect(m.Modern?.Animations?.Status(roar.Index).State == WoWData.Core.M2.Modern.ExternalState.Loaded, "versions-open: EmoteRoar's .anim file is loaded");
                win.BrowserScreen.ScrollToModel();
                await ScreenshotRunner.Settle(900);
                await ScreenshotRunner.Shot(win, outDir, "150_versions_open_anim_file");
            }

            // a spell effect's particles and ribbons (simulated, paused after the preroll)
            await Select(vm, "arcane_missile_lvl4", e => e.Name.Equals("Arcane_Missile_Lvl4", StringComparison.OrdinalIgnoreCase) && e.Key.StartsWith("spell:", StringComparison.Ordinal), "the Arcane_Missile_Lvl4 spell effect");
            await Show3D(win, vm, "Arcane_Missile_Lvl4");
            Expect(vm.ModelViewer.Stats.Any(x => x.Label == "Particles / ribbons" && x.Value.Contains("drawn approximately", StringComparison.Ordinal)),
                "versions-open: the statistics count the effect's particle emitters and ribbons");
            Expect(vm.ModelViewer.FormatNotes.Contains("drawn by the viewer's own simulation", StringComparison.Ordinal), "versions-open: the notes say the effects are drawn approximately");
            await ScreenshotRunner.Shot(win, outDir, "151_versions_open_particles");

            await Select(vm, "inv_sword_04", e => e.Kind == AssetKind.Texture, "the INV_Sword_04 icon");
            Expect(vm.Viewer.IsLoaded, "versions-open: the icon is shown in the texture viewer");
            Expect(vm.UsedBy.Count > 0, "versions-open: the icon's users are listed");
            var png = Path.Combine(outDir, "_export", "INV_Sword_04.png");
            Expect(vm.ExportOtherTextureTo(vm.SelectedTexture!.Path, png) && File.Exists(png), "versions-open: Export texture as PNG writes the PNG");
            Expect(vm.CanExport && !vm.CanImport, "versions-open: a texture of another version can be exported, not imported");
            main.ToastVisible = false;
            win.BrowserScreen.DetailsScroll.ScrollToTop();
            await ScreenshotRunner.Settle(300);
            await ScreenshotRunner.Shot(win, outDir, "147_versions_open_texture");

            await MapsAsync(win, vm, outDir);

            // the Data tables tab: the Map table read only, rows exported as CSV
            vm.SelectedTab = BrowserViewModel.TablesTab;
            var t = vm.VersionTables;
            await t.EnsureLoadedAsync();
            Expect(t.Tables.Count > 500, $"versions-open tables: {t.Tables.Count} tables of 1.60 listed");
            t.SelectedTable = t.Tables.FirstOrDefault(x => x.Name == "Map");
            await ScreenshotRunner.Until(() => t.Rows != null || t.TableNote.Contains("could not", StringComparison.Ordinal), 60000);
            Expect(t.Rows != null && t.Table!.RowCount >= 73, $"versions-open tables: the Map table is read: {t.TableNote}");
            var csv = Path.Combine(outDir, "_export", "Map.csv");
            Expect(await t.ExportToAsync(csv) && File.Exists(csv), "versions-open tables: Export rows writes a CSV");
            await ScreenshotRunner.Settle(600);
            await ScreenshotRunner.Shot(win, outDir, "157_versions_open_tables");

            // the Server data tab: no server is connected for 1.60
            vm.SelectedTab = BrowserViewModel.ServerTab;
            await ScreenshotRunner.Settle(500);
            Expect(vm.ServerMissingText.StartsWith("No server is connected for ", StringComparison.Ordinal), "versions-open: the Server data tab says no server is connected");
            await ScreenshotRunner.Shot(win, outDir, "158_versions_open_no_server");

            // My changes: no workspace, the buttons greyed out with the reason
            vm.SelectedTab = 1;
            await ScreenshotRunner.Settle(500);
            await ScreenshotRunner.Shot(win, outDir, "159_versions_open_my_changes");
            vm.SelectedTab = 0;
        }
        finally
        {
            // your 1.12 game again (the steps after this one, and the next run, start from it)
            if (own != null) await main.OpenClientAsync(own, false);
            Expect(main.Browser is { IsReadOnly: false }, "versions-open: back to your 1.12 game: " + main.WelcomeError);
        }
    }
}
