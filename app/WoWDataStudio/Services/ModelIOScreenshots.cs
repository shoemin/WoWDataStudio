using System.Buffers.Binary;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.ModelIO;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (Phase 3c): the model export / import screens, without Blender (the edit is scripted into the
/// exported package: some vertices of the boar moved, a texture tinted), into this run's own workspace.
///   28_model_actions                  the model actions under the 3D view
///   29_export_dialog                  "Export for Blender" (creature with several skins)
///   30_import_review                  the review screen of "Import edited model..."
///   31_compare_original_edited_model  original vs. edited mesh in two synchronized 3D views
///   32_model_modified                 the edited boar in the 3D view (Modified badge)
///   33_my_changes_model               My changes with the model import (one change set)
///   34_install_addon_dialog           Tools > Install the Blender add-on...
///   35_export_dialog_character        the export dialog of a character model, with its warning
/// </summary>
internal static class ModelIOScreenshots
{
    /// <summary>Move every vertex of the package's submesh <paramref name="submesh"/> whose M2 x is above
    /// <paramref name="above"/> up by <paramref name="lift"/> yards (glTF y = M2 z), in the .bin.</summary>
    public static int ScriptedMeshEdit(string gltfPath, int submesh, float above, float lift)
    {
        var g = JsonNode.Parse(File.ReadAllText(gltfPath))!.AsObject();
        var binPath = Path.Combine(Path.GetDirectoryName(gltfPath)!, g["buffers"]![0]!["uri"]!.GetValue<string>());
        var bin = File.ReadAllBytes(binPath);
        int moved = 0;
        foreach (var mesh in g["meshes"]!.AsArray())
        {
            if (GltfFile.IntOf(mesh!["extras"]?["wow_submesh"]) != submesh) continue;
            int acc = GltfFile.IntOf(mesh["primitives"]![0]!["attributes"]!["POSITION"])!.Value;
            var a = g["accessors"]![acc]!;
            var bv = g["bufferViews"]![GltfFile.IntOf(a["bufferView"])!.Value]!;
            int off = GltfFile.IntOf(bv["byteOffset"]) ?? 0;
            int n = GltfFile.IntOf(a["count"])!.Value;
            for (int i = 0; i < n; i++)
            {
                var s = bin.AsSpan(off + i * 12);
                if (BinaryPrimitives.ReadSingleLittleEndian(s) <= above) continue;
                BinaryPrimitives.WriteSingleLittleEndian(s[4..], BinaryPrimitives.ReadSingleLittleEndian(s[4..]) + lift);
                moved++;
            }
        }
        File.WriteAllBytes(binPath, bin);
        return moved;
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var mv = b.ModelViewer;
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = true;
        await ScreenshotRunner.Search(b, "mottled boar");
        var boar = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Creature && r.Name == "Mottled Boar") ?? b.Results.First(r => r.Entry.Kind == AssetKind.Creature);
        await ScreenshotRunner.Select(b, boar);
        await ScreenshotRunner.WaitModel(mv);
        view.ModelActions.BringIntoView();
        await ScreenshotRunner.Shot(win, outDir, "28_model_actions");

        // the export dialog
        var svc = mv.ExportContext();
        var users = await Task.Run(() => b.Session.ModelPackages!.Users(mv.ModelPath, svc.Subject));
        var ew = new ModelExportWindow(mv.ModelPath, mv.SelectedVariant?.Label ?? "", 9, users.Summary, users.Warning, b.ModelsExportDir, BlenderTools.FindBlender() != null);
        ScreenshotRunner.Place(ew, 720, 760);
        ew.SizeToContent = SizeToContent.Height;
        ew.ModeVariants.IsChecked = true;
        ew.Show();
        await ScreenshotRunner.Shot(ew, outDir, "29_export_dialog");
        ew.Close();

        // export all skins into this run's folder, script an edit, import it
        var work = Path.Combine(outDir, "_work", "models");
        var res = await b.ExportAsync(ModelExportMode.AllVariants, work, false);
        ScreenshotRunner.Expect(res != null && res.Manifest.Variants.Count >= 9, $"export: {res?.Manifest.Variants.Count} skins as variants in {res?.Folder}");
        if (res == null) return;
        int moved = ScriptedMeshEdit(res.GltfFiles[0], 1, 0.9f, 0.15f);
        var skin = res.Manifest.Textures.First(t => t.GamePath != null && t.GamePath.EndsWith("BoarSkinBrown.blp", StringComparison.OrdinalIgnoreCase));
        var pngPath = Path.Combine(res.Folder, skin.Png.Replace('/', Path.DirectorySeparatorChar));
        var img = Png.Load(pngPath);
        ScreenshotRunner.PaintTestEdit(img);
        Png.Save(img, pngPath, !img.IsOpaque());
        ScreenshotRunner.Expect(moved > 10, $"scripted edit moved {moved} vertices");

        ModelImportWindow? review = null;
        var import = b.ImportModelAsync(res.Folder, interactive: true, onShown: w =>
        {
            review = w;
            ScreenshotRunner.Place(w, 1060, 900);
        });
        await ScreenshotRunner.Until(() => review is { IsVisible: true }, 60000);
        var vm = review!.Vm;
        var mesh = vm.Rows.FirstOrDefault(r => r.Change.Kind == ModelChangeKind.Mesh);
        ScreenshotRunner.Expect(mesh != null && !mesh.Change.Unchanged && mesh.CanInclude, $"review: the mesh change is listed and can be imported ({mesh?.Detail})");
        ScreenshotRunner.Expect(vm.Rows.Count(r => r.Change.Kind == ModelChangeKind.Texture && !r.Change.Unchanged) == 1, "review: exactly one texture changed");
        await ScreenshotRunner.Shot(review, outDir, "30_import_review");

        if (mesh != null)
        {
            var cmp = await b.MeshCompareAsync(vm, mesh);
            ScreenshotRunner.Expect(cmp != null, "mesh compare view model");
            if (cmp != null)
            {
                var cw = new ModelCompareWindow(cmp);
                ScreenshotRunner.Place(cw, 1180, 700);
                cw.Show();
                await ScreenshotRunner.Settle(900);
                cmp.Camera.Orbit(55, 8);
                await ScreenshotRunner.Settle(400);
                await ScreenshotRunner.Shot(cw, outDir, "31_compare_original_edited_model");
                var (l, r) = await cw.Compare.CaptureBothAsync(480, 400);
                ScreenshotRunner.Expect(ScreenshotRunner.LooksRendered(l, out _) && ScreenshotRunner.LooksRendered(r, out _), "both sides of the original/edited compare render");
                ScreenshotRunner.Expect(ScreenshotRunner.Differs(l, r) > 0, "the edited mesh renders differently from the original");
                Png.Save(l, Path.Combine(outDir, "render_import_original.png"), false);
                Png.Save(r, Path.Combine(outDir, "render_import_edited.png"), false);
                cw.Close();
            }
        }
        review.DialogResult = true;      // "Save into my workspace"
        bool saved = await import;
        ScreenshotRunner.Expect(saved, "the model import was saved as one change set");
        await ScreenshotRunner.Settle(800);
        await ScreenshotRunner.Until(() => mv.IsModelModified, 20000);
        await ScreenshotRunner.WaitModel(mv);
        view.ScrollToModel();
        mv.Playing = false;
        mv.TimeMs = 300;
        await ScreenshotRunner.Render(mv, outDir, "boar_edited_mesh");
        view.ModelActions.BringIntoView(new Rect(0, -520, Math.Max(1, view.ModelActions.ActualWidth), 620));
        await ScreenshotRunner.Shot(win, outDir, "32_model_modified");
        b.SelectedTab = 1;
        await ScreenshotRunner.Settle(500);
        ScreenshotRunner.Expect(b.Changes.Any(c => c.Path.EndsWith("Boar.m2", StringComparison.OrdinalIgnoreCase)), "My changes lists the model");
        await ScreenshotRunner.Shot(win, outDir, "33_my_changes_model");
        b.SelectedTab = 0;

        // the add-on dialog (not installed in the screenshot run)
        var dlg = Dialogs.Build("Install the Blender add-on",
            "The add-on \"WoW 1.12 model (wowdata)\" version " + (BlenderTools.AddonRoot() is { } root ? BlenderTools.AddonVersion(root) : "?") + " is ready:\n" +
            Path.Combine(b.Session.Settings.DefaultExportDir, "..", "Blender", "wowdata_blender.zip") + "\n\n" +
            "Install it into your Blender now? WoW Data Studio runs Blender in the background to install and enable the add-on with Blender's own installer, and saves Blender's preferences so it stays enabled. Close Blender first if it is open.\n\n" +
            "To install it yourself: start Blender 4.5, Edit > Preferences > Add-ons, the drop-down arrow at the top right, Install from Disk..., pick the zip, and tick \"Import-Export: WoW 1.12 model (wowdata)\".",
            DialogKind.Question, new[] { "Install it into Blender", "Show the zip (install it myself)", "Cancel" }, null, null, null, out _);
        dlg.WindowStartupLocation = WindowStartupLocation.Manual;
        dlg.Left = -30000;
        dlg.ShowActivated = false;
        dlg.Show();
        await ScreenshotRunner.Shot(dlg, outDir, "34_install_addon_dialog");
        dlg.Close();

        // a character: the export dialog warns that every orc changes
        await ScreenshotRunner.Search(b, "orc male");
        var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character && r.Name.Contains("Orc", StringComparison.OrdinalIgnoreCase));
        if (orc != null)
        {
            await ScreenshotRunner.Select(b, orc);
            await ScreenshotRunner.WaitModel(mv);
            var u = b.Session.ModelPackages!.Users(mv.ModelPath, ModelSubject.Character);
            ScreenshotRunner.Expect(u.Warning?.Contains("every orc male player and NPC") == true, "character export warns: every orc male player and NPC");
            var cw2 = new ModelExportWindow(mv.ModelPath, "skin 0, face 0, hair 0/0, facial hair 0", 1, u.Summary, u.Warning, b.ModelsExportDir, BlenderTools.FindBlender() != null);
            ScreenshotRunner.Place(cw2, 720, 760);
            cw2.SizeToContent = SizeToContent.Height;
            cw2.Show();
            await ScreenshotRunner.Shot(cw2, outDir, "35_export_dialog_character");
            cw2.Close();
        }
        ScreenshotRunner.Place(win, 1440, 900);
    }
}
