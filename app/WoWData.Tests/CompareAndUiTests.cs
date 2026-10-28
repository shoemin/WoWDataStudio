using System.Diagnostics;
using System.IO;
using System.Linq;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWDataStudio.Helpers;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>The before/after compare: statistics, heat map, and the view model's modes, mip levels and alpha options.</summary>
public class CompareTests
{
    private static (byte[] Orig, byte[] Edit) BoarPair()
    {
        var orig = BlpFile.Parse(TestEnv.Client.Read(@"Creature\Boar\BoarSkinIvory.blp")!);
        var img = orig.Decode(0);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int o = img.Offset(x, y);
                if (img.Pixels[o + 3] != 0) img.Pixels[o + 2] = 255;
            }
        return (orig.Data, BlpEncoder.EncodeLike(orig, img).Data);
    }

    [Fact]
    public void StatsAndHeatmap()
    {
        var a = new RgbaImage(4, 4);
        Array.Fill(a.Pixels, (byte)100);
        var b = a.Clone();
        b.Pixels[b.Offset(1, 1)] = 200;              // one pixel, difference 100
        b.Pixels[b.Offset(2, 2) + 1] = 101;          // one pixel, difference 1
        var st = ImageCompare.Compare(a, b);
        Assert.Equal(2, st.ChangedPixels);
        Assert.Equal(12.5, st.ChangedPercent);
        Assert.Equal(100, st.MaxDifference);
        Assert.Equal(2, st.ColourChangedPixels);
        Assert.Equal(0, st.AlphaChangedPixels);
        Assert.Equal("Colour changed in 12.5 % of pixels; alpha changed in 0 %.", st.Headline);
        Assert.StartsWith(st.Headline, st.Summary);
        var h = ImageCompare.Heatmap(a, b);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, h.Pixels.AsSpan(h.Offset(1, 1), 4).ToArray());     // big -> red
        Assert.True(h.Pixels[h.Offset(2, 2) + 1] > 200);                                              // small -> yellow
        Assert.Equal(h.Pixels[h.Offset(0, 0)], h.Pixels[h.Offset(0, 0) + 1]);                         // unchanged -> grey
        // 0.6.1: colour under pixels transparent in both images counts too (the alpha is often not transparency: a ground
        // _s texture's shine, an opaque material); it is reported separately, and the old "visible" count leaves it out
        var c = a.Clone();
        var d = a.Clone();
        c.Pixels[3] = d.Pixels[3] = 0;
        d.Pixels[0] = 7;
        var hidden = ImageCompare.Compare(c, d);
        Assert.Equal(1, hidden.ChangedPixels);
        Assert.Equal(1, hidden.ColourChangedPixels);
        Assert.Equal(1, hidden.HiddenColourPixels);
        Assert.Contains("where the alpha is 0", hidden.Summary);
        Assert.Equal(0, ImageCompare.VisibleChangedPixels(c, d));
        Assert.Equal(1, ImageCompare.Compare(c, d, compareAlpha: false).ChangedPixels);
        var hh = ImageCompare.Heatmap(c, d);
        Assert.Equal(255, hh.Pixels[0]);                                                              // the hidden colour change is shown
        Assert.Contains("identical", ImageCompare.Compare(a, a).Summary);
        // an alpha-only change: counted as alpha, shown in blue
        var e = a.Clone();
        e.Pixels[e.Offset(3, 3) + 3] = 0;
        var sa = ImageCompare.Compare(a, e);
        Assert.Equal((0, 1, 1), (sa.ColourChangedPixels, sa.AlphaChangedPixels, sa.ChangedPixels));
        Assert.Equal("Colour changed in 0 % of pixels; alpha changed in 6.25 %.", sa.Headline);
        var ha = ImageCompare.Heatmap(a, e);
        Assert.Equal(new byte[] { ImageCompare.AlphaOnlyColour.R, ImageCompare.AlphaOnlyColour.G, ImageCompare.AlphaOnlyColour.B, 255 }, ha.Pixels.AsSpan(ha.Offset(3, 3), 4).ToArray());
        Assert.Equal(0, ImageCompare.Compare(a, e, compareAlpha: false).ChangedPixels);
    }

    /// <summary>The in-game test found "14 %" for a texture whose colour changed everywhere (alpha 0 over most of it, but
    /// not transparency in the game): a whole-texture tint must report every pixel.</summary>
    [Fact]
    public void WholeTextureTintUnderZeroAlphaReportsEveryPixel()
    {
        var orig = BlpFile.Parse(TestEnv.Client.Read(@"Tileset\Durotar\DurotarDirt_s.blp")!);
        var img = orig.Decode(0);
        Assert.True(Enumerable.Range(0, img.Width * img.Height).Count(i => img.Pixels[i * 4 + 3] == 0) > img.Width * img.Height / 10, "the _s texture has alpha 0 in places");
        var tinted = img.Clone();
        for (int i = 0; i < tinted.Pixels.Length; i += 4) { tinted.Pixels[i] = 20; tinted.Pixels[i + 1] = 60; tinted.Pixels[i + 2] = 250; }
        var st = ImageCompare.Compare(img, tinted);
        Assert.InRange(st.ColourChangedPercent, 99, 100);
        Assert.Equal(0, st.AlphaChangedPixels);
        Assert.True(st.HiddenColourPixels > 0);
    }

    [Fact]
    public void ViewModelModesLevelsAndLabels()
    {
        RunSta(() =>
        {
            var (o, e) = BoarPair();
            var vm = new CompareViewModel("BoarSkinIvory.blp", new TextureCompareSide(CompareViewModel.OriginalLabel, o), new TextureCompareSide(CompareViewModel.EditLabel, e));
            Assert.Equal("Original (from game)", vm.Left.Label);
            Assert.Equal("Your edit", vm.Right.Label);
            Assert.Equal(9, vm.Levels.Count);
            Assert.NotNull(vm.LeftImage);
            Assert.NotNull(vm.RightImage);
            Assert.NotNull(vm.DiffImage);
            Assert.InRange(vm.ChangedPercent, 1, 10);
            Assert.Contains("of pixels", vm.DiffSummary);
            Assert.StartsWith("Colour changed in", vm.DiffHeadline);
            Assert.NotNull(vm.Stats);
            foreach (var m in Enum.GetValues<CompareMode>()) vm.Mode = m;
            Assert.False(vm.FlickerShowsOriginal);
            Assert.Same(vm.RightImage, vm.FlickerImage);
            vm.ToggleFlickerCommand.Execute(null);
            Assert.Same(vm.LeftImage, vm.FlickerImage);
            Assert.Equal("Now showing", "Now showing");
            Assert.Equal(CompareViewModel.OriginalLabel, vm.FlickerLabel);
            vm.MipLevel = 3;
            Assert.Equal(32, ((System.Windows.Media.Imaging.BitmapSource)vm.LeftImage!).PixelWidth);
            Assert.Contains("Mip level 3: 32x32", vm.LevelInfo);
            vm.AlphaView = AlphaView.AlphaOnly;
            vm.AlphaView = AlphaView.IgnoreAlpha;
            Assert.False(vm.Checkerboard);
            vm.Dispose();

            // different sizes: no pixel difference, a plain explanation instead
            var small = new TextureCompareSide("Your edit", new RgbaImage(8, 8), "8x8");
            var vm2 = new CompareViewModel("x", new TextureCompareSide(CompareViewModel.OriginalLabel, o), small);
            Assert.Null(vm2.DiffImage);
            Assert.Contains("sizes differ", vm2.DiffSummary);
        });
    }

    internal static void RunSta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() =>
        {
            try { a(); }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Exception("STA test failed: " + err.Message, err);
    }
}

/// <summary>UI smoke test: the real app, started with --screenshot, walks through every main screen with the real data
/// (welcome, organizing, search, details, texture viewer, import confirmation, all compare modes, my changes, settings,
/// build-patch dialog, help) in its own temporary app folder and must report every step OK. It is also the render smoke
/// test of the 3D viewer: offscreen renders (render_*.png) of the Mottled Boar with two skins, the orc male with three
/// looks, an animation frame and all four views, a palm tree, an item, a spell effect and both sides of the model
/// compare view must not be blank and must differ where the content differs. Phase 4: the Maps tab (map list, zones,
/// minimap, tile report), the 3D terrain preview of a tile and of its neighbours, "Edit in Noggit" (client view and
/// project in the run's own folder; Noggit is not started), a scripted tile edit imported through the review screen, the
/// terrain compare (both sides render and differ) and the server update window (nothing is run). 0.5.1: the pop-out 3D
/// windows (off screen): follow / pin, F11 / Esc, the placement saved and restored per kind, the fly camera and presets,
/// and no GL context, texture or buffer left behind after 20 open / close cycles of each kind. 0.6.0: the tile breakdown
/// (groups, highlight in 3D, a texture with its coverage map, a model on its own, a chunk heat map, Show in Browse), the
/// terrain tile picker, and a scripted terrain edit through the seam check, sync, review, compare and save. 0.6.1: a ground
/// texture with its _s file (details, edit both, compare, breakdown, the Maps tab's specular ground), the additive note
/// and the release patch's state and uninstall (in a fake game folder). 0.7.0: the Data tables tab (links, Used by, a refused
/// edit, saving, compare, an item display in 3D, bulk edit and undo, typing into the grid, new rows, the import preview, Copy
/// to server into a fake server folder). 0.9.0: the Server data tab against a throwaway MySQL server the run starts itself
/// (views, links, edits, a new vendor and quest, a spell next to the game's row, the client / server check, apply to the
/// throwaway server, My changes, the import preview, the Data tables tab's link, the game's cache). 0.11.0: hot reload against a
/// stand-in for the running game (the My changes button and line, the offer after an install, the result, the Settings switch).</summary>
[Collection(IndexCollection.Name)]   // the app builds an index of its own: never at the same time as another build
public class UiSmokeTests
{
    internal static string? FindExe()
    {
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        foreach (var c in new[] { cfg, "Release", "Debug" })
        {
            var p = Path.Combine(Oracle.AppDir, "WoWDataStudio", "bin", c, "net8.0-windows", "WoWDataStudio.exe");
            if (File.Exists(p)) return p;
        }
        var local = Path.Combine(AppContext.BaseDirectory, "WoWDataStudio.exe");
        return File.Exists(local) ? local : null;
    }

    [Fact]
    public void ScreenshotRunWalksEveryScreen()
    {
        var exe = FindExe() ?? throw new InvalidOperationException("WoWDataStudio.exe not built");
        var before = TestEnv.DataSnapshot();
        var dir = TestEnv.TempDir("ui");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--screenshot");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("--client");
            psi.ArgumentList.Add(TestEnv.ClientDir);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            // 20 minutes: the whole run takes about 11 minutes alone on the maintainer's machine, longer beside the other tests
            if (!p.WaitForExit(1_200_000))
            {
                p.Kill(true);
                throw new TimeoutException("the app did not finish the screenshot run");
            }
            var log = File.ReadAllText(Path.Combine(dir, "screenshot.log"));
            Assert.True(p.ExitCode == 0, $"exit code {p.ExitCode}\n{log}");
            Assert.Contains("ALL STEPS OK", log);
            var pngs = Directory.GetFiles(dir, "*.png");
            Assert.True(pngs.Length >= 25, $"{pngs.Length} screenshots");
            foreach (var f in pngs)
            {
                var img = Png.Load(f);
                Assert.True(img.Width >= 300 && img.Height >= 150, f);
                Assert.True(img.Pixels.Distinct().Count() > 4, $"{f} is blank");
            }
            foreach (var mode in new[] { "07_import_confirm_side_by_side", "08_compare_slider", "09_compare_toggle", "10_compare_difference",
                         "19_viewer_creature", "20_viewer_skin_picker", "21_compare_two_skins", "22_viewer_character", "23_viewer_tree", "24_viewer_item", "25_viewer_spell_effect",
                         "28_model_actions", "29_export_dialog", "30_import_review", "31_compare_original_edited_model", "32_model_modified", "33_my_changes_model",
                         "34_install_addon_dialog", "35_export_dialog_character",
                         "46_maps_tab", "47_maps_tile_details", "48_terrain_neighbours", "49_edit_in_noggit", "50_map_import_review", "51_map_compare",
                         "52_maps_tile_modified", "53_server_update", "54_my_changes_map", "55_settings_maps",
                         "56_model_popout", "57_model_popout_fullscreen", "58_main_popout_following", "59_model_popout_pinned", "60_terrain_popout",
                         "61_terrain_popout_fullscreen", "62_compare_fullscreen",
                         "63_tile_breakdown", "64_breakdown_texture", "65_breakdown_model", "66_breakdown_chunk_heatmap", "67_terrain_export_picker",
                         "68_terrain_seam_dialog", "69_terrain_import_review", "70_terrain_compare",
                         // 0.6.1 (Services\SpecularScreenshots.cs)
                         "75_ground_texture_details", "76_ground_edit_both_dialog", "77_compare_colour_alpha", "78_breakdown_specular",
                         "79_additive_keep_black", "80_my_changes_patch_state", "81_uninstall_patch_dialog", "82_maps_specular_ground",
                         // 0.7.0 (Services\DbcScreenshots.cs)
                         "83_tables_spell", "84_tables_link_used_by", "85_tables_validation", "86_tables_save", "87_tables_my_changes",
                         "88_tables_compare", "89_tables_item_display", "90_tables_bulk_edit", "91_tables_taxi_node",
                         "92_tables_creature_display", "93_tables_import_preview", "94_tables_server_copy",
                         // 0.9.0 (Services\ServerDataScreenshots.cs, against a throwaway MySQL server, never the live one)
                         "95_server_items", "96_server_creature", "97_server_save", "98_server_vendor", "99_server_quest", "100_server_spell_client",
                         "101_server_check", "102_server_apply", "103_server_my_changes", "104_server_import", "105_server_dbc_link", "106_server_wdb",
                         // 0.11.0 (Services\HotReloadScreenshots.cs, against a stand-in for the running game)
                         "115_my_changes_hot_reload", "116_hot_reload_offer", "117_hot_reload_result", "118_settings_hot_reload" })
                Assert.Contains(pngs, f => Path.GetFileNameWithoutExtension(f) == mode);
            foreach (var render in new[] { "boar_skin1", "boar_skin2", "orc_look1", "orc_look2", "orc_look3", "orc_animation_frame", "orc_view4", "tree", "item", "spell_effect", "compare_left", "compare_right", "import_original", "import_edited", "boar_edited_mesh",
                         "terrain_tile", "terrain_neighbours", "terrain_compare_left", "terrain_compare_right",
                         "popout_boar", "popout_front", "popout_top", "popout_fly_start", "popout_fly_moved", "terrain_popout_fly", "terrain_popout_neighbours",
                         "breakdown_highlight", "breakdown_model" })
                Assert.Contains(pngs, f => Path.GetFileNameWithoutExtension(f) == "render_" + render);
            Assert.Contains("ok: orc male animates", log);
            // 0.5.1: the pop-out windows (Services\PopoutScreenshots.cs): follow, pin, F11 / Esc, placement memory, no GL leak
            foreach (var line in new[]
                     {
                         "ok: pop-out: follows the selection by default", "ok: pop-out: follows the selection to the orc male", "ok: pinned: keeps its model",
                         "ok: two windows side by side", "ok: F11: full screen covers the monitor", "ok: Esc: leaves full screen", "ok: F11 twice: back to the window",
                         "ok: placement saved on close", "ok: placement written to settings.json", "ok: placement restored", "ok: H: the help overlay",
                         "ok: Fly: W moves", "ok: model pop-out: 20 open/close cycles leave no GL context behind", "ok: model pop-out: 20 open/close cycles leave no GL texture or buffer behind",
                         "ok: terrain pop-out: takes the Maps tab's 3D preview", "ok: terrain pop-out: follows the selected tile", "ok: terrain pop-out: pinned, it keeps its tile",
                         "ok: terrain placement restored", "ok: terrain pop-out: 20 open/close cycles leave no GL context behind",
                         "ok: terrain pop-out: 20 open/close cycles leave no GL texture or buffer behind", "ok: compare window: F11 full screen",
                         "ok: pop-out: F1 opens the model viewer chapter", "ok: terrain pop-out: F1 opens the maps chapter",
                         // 0.6.0: the tile breakdown and terrain through Blender (Services\BreakdownScreenshots.cs)
                         "ok: breakdown: terrain, textures, doodads, buildings, water and other groups", "ok: breakdown: the terrain mesh has 256 chunks",
                         "ok: breakdown: the highlighted model renders differently from the plain tile", "ok: breakdown: Show in Browse selects",
                         "ok: terrain export: F1 opens the terrain-in-Blender chapter", "ok: terrain import: the raised edge disagrees with the existing neighbour",
                         "ok: terrain import: synced, two tiles to save", "ok: terrain import saved as one change set",
                         // 0.6.1: ground textures and _s files, colour / alpha figures, additive glows, the release patch, the specular ground
                         "ok: ground: both files listed", "ok: ground: both files saved as one change set", "ok: ground: the _s file keeps its shine map (alpha)",
                         "ok: ground: compare says \"Colour changed in", "ok: breakdown: a ground texture lists its _s file", "PURPLE_GLOW.blp gets the note",
                         "ok: patch: uninstall put the earlier patch-3.MPQ back", "ok: maps: the 3D view draws the _s files",
                         // 0.7.0: the Data tables tab (DBC editor)
                         "ok: tables: 158 tables listed", "ok: tables: the spell's icon is shown through SpellIconID", "ok: tables: Used by of SpellIcon 185 lists Fireball",
                         "ok: tables: a link to a row that does not exist is refused", "ok: tables: the saved table reads back with the new icon",
                         "ok: tables: My changes shows Spell.dbc as a DBC table", "ok: tables: an item display can be shown in 3D",
                         "ok: tables: undo of the bulk edit leaves the table unchanged", "ok: tables: the new flight point is renamed in the grid",
                         "ok: tables: the grid refuses a negative map ID", "ok: tables: the creature display shows its skin",
                         "ok: tables: import preview:", "ok: tables: undo of the import leaves the table unchanged",
                         "ok: tables: TaxiPath is copied, Spell (server database) is not", "ok: tables: copy to server wrote TaxiPath.dbc and backed up the old one",
                         "ok: tables: the server's Spell.dbc was not touched",
                     })
                Assert.Contains(line, log);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        Assert.Equal(before, TestEnv.DataSnapshot());       // the run never touched the real game folder
    }
}
