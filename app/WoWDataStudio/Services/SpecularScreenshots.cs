using System.IO;
using System.Windows;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.6.1, the findings of the in-game visual test), in this run's own app folder (the edits go into
/// the run's workspace; the release-patch steps use a FAKE game folder inside the run's folder, never the real one):
///   75_ground_texture_details   Browse: DurotarDirt.blp with both files, which one the game draws, and its "Used in game"
///   76_ground_edit_both_dialog  importing a ground texture: "Edit both (recommended)"
///   77_compare_colour_alpha     the difference view of the edited DurotarDirt_s.blp: colour and alpha counted separately
///   78_breakdown_specular       the tile breakdown: a ground texture's _s file on its own, with its shine map
///   79_additive_keep_black      importing a tinted additive glow: "Keep black areas black"
///   80_my_changes_patch_state   My changes: the installed release patch and "Uninstall release patch (patch-3.MPQ)"
///   81_uninstall_patch_dialog   the question before uninstalling (the earlier patch-3.MPQ is put back)
///   82_maps_specular_ground     the Maps tab: "Specular ground (_s textures)" and what the game draws
/// </summary>
internal static class SpecularScreenshots
{
    public const string Dirt = @"Tileset\Durotar\DurotarDirt.blp";
    public const string DirtS = @"Tileset\Durotar\DurotarDirt_s.blp";
    public const string Glow = @"Spells\PURPLE_GLOW.blp";

    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static async Task ShotDialog(string title, string message, DialogKind kind, string[] buttons, string outDir, string name)
    {
        var dlg = Dialogs.Build(title, message, kind, buttons, null, null, null, out _);
        // the dialog's content drawn on its own at its natural size (an off-screen window does not always grow to it)
        var content = (FrameworkElement)dlg.Content;
        dlg.Content = null;
        var host = new System.Windows.Controls.Border { Child = content, Width = 640 };
        var w = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        w.SetResourceReference(FrameworkElement.StyleProperty, "AppWindow");
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBg");
        w.Show();
        await ScreenshotRunner.Shot(w, outDir, name);
        w.Close();
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var ts = session.Textures!;
        var game = session.GameSpecular;
        ScreenshotRunner.Log($"ground textures: {session.GroundRuleText}");

        // ---- Browse: a ground texture shows both files
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = true;
        b.UsedInGameExpanded = true;
        await ScreenshotRunner.Search(b, "durotardirt");
        var row = b.Results.FirstOrDefault(r => string.Equals(r.Path, Dirt, StringComparison.OrdinalIgnoreCase));
        Expect(row != null && b.Results.Any(r => string.Equals(r.Path, DirtS, StringComparison.OrdinalIgnoreCase)), "ground: DurotarDirt.blp found in Browse");
        if (row != null) await ScreenshotRunner.Select(b, row);
        await ScreenshotRunner.Until(() => !b.DetailsLoading && b.HasSelection, 30000);
        await ScreenshotRunner.Settle(900);
        Expect(b.Textures.Select(t => t.Path).SequenceEqual(new[] { Dirt, DirtS }, StringComparer.OrdinalIgnoreCase), $"ground: both files listed ({string.Join(", ", b.Textures.Select(t => t.FileName))})");
        Expect(b.Facts.Any(f => f.Contains("terrain specular is on")) && b.Facts.Any(f => f.StartsWith("With ")), "ground: which file the game draws, and with your settings");
        await ScreenshotRunner.Shot(win, outDir, "75_ground_texture_details");

        // ---- import a blue DurotarDirt.blp with "Edit both"
        var pair = session.GroundPair(Dirt)!;
        var (t1, m1, b1) = BrowserViewModel.EditBothDialog(pair, game, Dirt);
        await ShotDialog(t1, m1, DialogKind.Question, b1, outDir, "76_ground_edit_both_dialog");
        var dir = Path.Combine(outDir, "_ground");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "DurotarDirt.png");
        ts.Export(Dirt, png);
        var img = Png.Load(png);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = (byte)(img.Pixels[i] / 4); img.Pixels[i + 1] = (byte)(img.Pixels[i + 1] / 2); img.Pixels[i + 2] = 240; }
        Png.Save(img, png, false);
        Expect(await b.ImportPngAsync(png, Dirt, interactive: false, new ImportChoices(EditBoth: true)), "ground: DurotarDirt.png imported with Edit both");
        await ScreenshotRunner.Settle(500);
        ScreenshotRunner.Main!.ToastVisible = false;
        var ws = session.Workspace!;
        Expect(ws.IsModified(Dirt) && ws.IsModified(DirtS) && ws.Get(Dirt)!.ChangeSet == ws.Get(DirtS)!.ChangeSet, "ground: both files saved as one change set");
        var origS = BlpFile.Parse(session.Data!.Read(DirtS)!).Decode(0);
        var newS = BlpFile.Parse(ws.ReadEdited(DirtS)!).Decode(0);
        Expect(!GroundTextures.AlphaDiffers(origS, newS), "ground: the _s file keeps its shine map (alpha)");

        // ---- the compare of the _s file: colour and alpha counted separately (0.6.0 said 17 % for a full tint)
        var cmp = b.CompareFor(DirtS)!;
        cmp.Mode = CompareMode.Difference;
        var cw = new CompareWindow(cmp);
        ScreenshotRunner.Place(cw, 1200, 760);
        cw.Show();
        Expect(cmp.Stats is { AlphaChangedPixels: 0 } st && st.ColourChangedPercent > 95, $"ground: compare says \"{cmp.DiffHeadline}\"");
        await ScreenshotRunner.Shot(cw, outDir, "77_compare_colour_alpha");
        cw.Close();

        // ---- the tile breakdown: the _s file on its own (colours + shine map)
        var m = b.Maps;
        b.SelectedTab = BrowserViewModel.MapsTab;
        await ScreenshotRunner.Settle(300);
        var map = m.Maps.First(x => x.Directory.Equals(MapScreenshots.Map, StringComparison.OrdinalIgnoreCase));
        var bw = m.OpenBreakdown(map, MapScreenshots.TileX, MapScreenshots.TileY, w => ScreenshotRunner.Place(w, 1440, 900));
        var vm = bw.Vm;
        await ScreenshotRunner.Until(() => !vm.Loading, 120000);
        var texGroup = vm.Tree.First(g => g.Group?.Key == "textures");
        var withS = texGroup.Children.FirstOrDefault(n => n.Item?.SpecularPath != null);
        Expect(withS != null && withS.Children.Count == 2, $"breakdown: a ground texture lists its _s file ({withS?.Title})");
        if (withS != null)
        {
            withS.IsExpanded = true;
            var sNode = withS.Children.First(n => n.Item!.IsSpecularFile);
            vm.Selected = sNode;
            vm.OnItsOwn = true;
            await ScreenshotRunner.Until(() => vm.ImageB != null && vm.ImageBLabel.Contains("shine"), 30000);
            Expect(vm.ViewNote.Contains("terrain specular is on"), $"breakdown: {sNode.Title} explains when it is drawn");
            await ScreenshotRunner.Shot(bw, outDir, "78_breakdown_specular");
        }
        bw.Close();

        // ---- an additive glow: the note and "Keep black areas black"
        var glow = b.GlowNote(Glow);
        Expect(glow != null && glow.StartsWith("This texture is drawn additively", StringComparison.Ordinal), $"additive: {Glow} gets the note ({glow})");
        if (glow != null)
        {
            var g0 = BlpFile.Parse(session.Data!.Read(Glow)!).Decode(0);
            var tinted = g0.Clone();
            for (int i = 0; i < tinted.Pixels.Length; i += 4) tinted.Pixels[i + 1] = (byte)Math.Min(255, tinted.Pixels[i + 1] + 90);
            int lifted = WoWData.Core.M2.MaterialBlends.LiftedBlackPixels(g0, tinted);
            Expect(lifted > 0 && WoWData.Core.M2.MaterialBlends.LiftedBlackPixels(g0, WoWData.Core.M2.MaterialBlends.KeepBlackBlack(g0, tinted)) == 0, $"additive: a plain tint lifts {lifted} black pixels; Keep black areas black leaves none");
            var (t2, m2, b2) = BrowserViewModel.KeepBlackDialog(glow, lifted, g0.Width * g0.Height);
            await ShotDialog(t2, m2, DialogKind.Question, b2, outDir, "79_additive_keep_black");
        }

        // ---- the release patch: state and uninstall, in a FAKE game folder of this run
        var fakeClient = Path.Combine(outDir, "_fakeclient");
        Directory.CreateDirectory(Path.Combine(fakeClient, "Data"));
        var fws = new Workspace(new AppPaths(Path.Combine(outDir, "_fakehome")).ForClient(fakeClient));
        File.WriteAllBytes(Path.Combine(fws.Paths.DataDir, "patch-3.MPQ"), new byte[] { 0x4D, 0x50, 0x51, 0x1A });   // "someone else's" patch
        fws.SaveEdit(Dirt, ws.ReadEdited(Dirt)!, new OriginInfo(), fws.BeginChangeSet("ground"));
        fws.SaveEdit(DirtS, ws.ReadEdited(DirtS)!, new OriginInfo(), fws.Manifest.ChangeSets.Values.First());
        fws.BuildPatch(install: true);
        var ps = fws.GetPatchState();
        Expect(ps.Owner == PatchOwner.Ours && ps.RestoreBackup != null, $"patch: {ps.Text}");
        b.PatchStateProvider = () => fws.GetPatchState();
        b.NotifyPatchState();
        b.SelectedTab = 1;
        await ScreenshotRunner.Settle(500);
        Expect(b.HasInstalledPatch && b.PatchStateText.StartsWith("A release patch built by WoW Data Studio is installed: 2 files", StringComparison.Ordinal), $"patch: My changes shows \"{b.PatchStateText}\"");
        await ScreenshotRunner.Shot(win, outDir, "80_my_changes_patch_state");
        var (t3, m3, y3) = BrowserViewModel.UninstallDialog(ps, fws.Paths.BackupDir);
        await ShotDialog(t3, m3, DialogKind.Question, new[] { y3, "Cancel" }, outDir, "81_uninstall_patch_dialog");
        fws.UninstallPatch();
        Expect(File.ReadAllBytes(Path.Combine(fws.Paths.DataDir, "patch-3.MPQ")).Length == 4 && fws.GetPatchState().Owner == PatchOwner.Foreign, "patch: uninstall put the earlier patch-3.MPQ back");
        b.PatchStateProvider = null;
        b.NotifyPatchState();

        // ---- the Maps tab: the ground as the game draws it
        b.SelectedTab = BrowserViewModel.MapsTab;
        if (!ReferenceEquals(m.SelectedMap, map)) m.SelectedMap = map;
        await ScreenshotRunner.Until(() => m.Overview != null, 120000);
        m.SelectTile(MapScreenshots.TileX, MapScreenshots.TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && !m.PreviewLoading && m.Scene.Asset != null, 120000);
        bool before = m.SpecularGround;
        m.SpecularGround = true;
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset is { SpecularGround: true }, 120000);
        Expect(m.Scene.Asset!.DrawnTexturePaths.Any(p => p.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase)), $"maps: the 3D view draws the _s files ({m.PreviewStatus})");
        await ScreenshotRunner.Settle(600);
        await ScreenshotRunner.Shot(win, outDir, "82_maps_specular_ground");
        m.SpecularGround = before;
        await ScreenshotRunner.Until(() => !m.PreviewLoading && m.Scene.Asset?.SpecularGround == before, 120000);
        b.SelectedTab = 0;
    }
}
