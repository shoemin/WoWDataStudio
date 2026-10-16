using System.IO;
using System.Numerics;
using System.Windows;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.10.0): the skeleton and animation round trip, without Blender (the rig edit is scripted
/// into the package's wowdata_rig.json, the file the Blender add-on writes), into this run's own workspace.
///   107_viewer_skeleton            the 3D viewer: bones, attachment points, the animation's properties and events
///   108_export_dialog_skeleton     "Export for Blender" with "Skeleton and animations (for editing)"
///   109_import_review_skeleton     the review: the skeleton and animations row with every change in words
///   110_compare_animation          original vs. edited, the new animation picked (right side only)
///   111_viewer_new_animation       the edited boar playing its new animation in the 3D viewer
///   112_used_in_game_animations    "Used in game": the animations of the orc and what the game plays them for
/// (WDS_SCREENSHOT_ONLY=rig runs only these.)
/// </summary>
internal static class SkeletonScreenshots
{
    /// <summary>The manual's worked edit (and the tests'): a new bone under the head that turns in a new idle
    /// animation "Stand [4]" with a sound event, the walk 1.25 x slower, the name plate 0.15 yd higher.</summary>
    public static void ScriptedRigEdit(EditedRig rig)
    {
        var head = rig.Bones.First(b => b.Name.EndsWith("_Head", StringComparison.Ordinal));
        rig.Bones.Add(new EditedBone { Name = "bone_tusk_new", Parent = head.Name, Head = head.Head + new Vector3(0.3f, 0, 0) });
        var stand = rig.Actions.First(x => x.Name == "Stand");
        var idle = new EditedAction { Name = "Stand [4]", WowSequence = stand.WowSequence, DurationMs = 1600 };
        foreach (var kv in stand.Props) idle.Props[kv.Key] = kv.Value?.DeepClone();
        idle.Props["chance"] = 10.0;
        var p = rig.Bones.Last().Head;
        var hp = head.Head;
        var tusk = new BoneSamples();
        var turn = new BoneSamples();
        foreach (var (t, ang) in new[] { (0.0, 0f), (800.0, 0.7f), (1600.0, 0f) })
        {
            tusk.TimesMs.Add(t);
            tusk.Local.Add(Matrix4x4.CreateTranslation(-p) * Matrix4x4.CreateRotationZ(ang) * Matrix4x4.CreateTranslation(p));
            turn.TimesMs.Add(t);
            turn.Local.Add(Matrix4x4.CreateTranslation(-hp) * Matrix4x4.CreateRotationZ(ang * 0.6f) * Matrix4x4.CreateTranslation(hp));
        }
        idle.Bones["bone_tusk_new"] = tusk;
        idle.Bones[head.Name] = turn;
        idle.Markers.Add(("$CSD", 400));
        rig.Actions.Add(idle);
        var walk = rig.Actions.First(x => x.Name == "Walk");
        walk.Unchanged = false;
        walk.DurationMs *= 1.25;
        foreach (var b in walk.Bones.Values)
            for (int i = 0; i < b.TimesMs.Count; i++) b.TimesMs[i] *= 1.25;
        for (int i = 0; i < walk.Markers.Count; i++) walk.Markers[i] = (walk.Markers[i].Marker, walk.Markers[i].TimeMs * 1.25);
        var plate = rig.Points.FirstOrDefault(x => x.Kind == "attachment" && x.Id == 18);
        if (plate != null) plate.Position += new Vector3(0, 0, 0.15f);
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

        // the viewer: the walk, bones and attachment points, its properties and events
        var walk = mv.Sequences.FirstOrDefault(s => s.Name == "Walk");
        ScreenshotRunner.Expect(walk != null, "viewer: the boar's animations are named like the Blender actions (Walk)");
        if (walk != null) mv.SelectedSequence = walk;
        mv.ShowBones = true;
        mv.ShowAttachments = true;
        mv.Playing = false;
        mv.TimeMs = 450;
        ScreenshotRunner.Expect(mv.AnimationInfo.Contains("moving", StringComparison.Ordinal), $"viewer: the animation's use in words ({mv.AnimationInfo})");
        ScreenshotRunner.Expect(mv.EventsInfo.Contains("$FSD", StringComparison.Ordinal), $"viewer: the walk's events ({mv.EventsInfo})");
        ScreenshotRunner.Expect(mv.SkeletonInfo.Contains("Key bones", StringComparison.Ordinal), "viewer: the skeleton summary");
        await ScreenshotRunner.Settle(500);
        view.ScrollToModel();
        await ScreenshotRunner.Render(mv, outDir, "boar_skeleton_attachments");
        await ScreenshotRunner.Shot(win, outDir, "107_viewer_skeleton");

        // the export dialog with the skeleton
        var ctx = mv.ExportContext();
        var users = await Task.Run(() => b.Session.ModelPackages!.Users(mv.ModelPath, ctx.Subject));
        var ew = new ModelExportWindow(mv.ModelPath, mv.SelectedVariant?.Label ?? "", 9, users.Summary, users.Warning, b.ModelsExportDir, BlenderTools.FindBlender() != null);
        ScreenshotRunner.Place(ew, 720, 860);
        ew.SizeToContent = SizeToContent.Height;
        ew.SkeletonBox.IsChecked = true;
        ew.Show();
        await ScreenshotRunner.Shot(ew, outDir, "108_export_dialog_skeleton");
        ew.Close();

        // export with the skeleton, the scripted rig edit, the review
        var work = Path.Combine(outDir, "_work", "rig");
        var res = await b.ExportAsync(ModelExportMode.CurrentLook, work, true, true);
        ScreenshotRunner.Expect(res?.Manifest.Rig is { Sequences.Count: 13 }, $"export with the skeleton: {res?.Manifest.Rig?.Sequences.Count} animations");
        if (res == null) return;
        var em = EditedModelReader.Read(res.Folder);
        ScriptedRigEdit(em.Rig!);
        RigSidecarFile.Write(em.Rig!, Path.Combine(res.Folder, RigSidecarFile.FileName));
        mv.ShowBones = false;
        mv.ShowAttachments = false;
        var before = b.Session.Workspace!.Manifest.ChangeSets.Keys.ToHashSet();
        ModelImportWindow? review = null;
        var import = b.ImportModelAsync(res.Folder, interactive: true, onShown: w =>
        {
            review = w;
            ScreenshotRunner.Place(w, 1060, 980);
        });
        await ScreenshotRunner.Until(() => review is { IsVisible: true }, 60000);
        var vm = review!.Vm;
        var rigRow = vm.Rows.FirstOrDefault(r => r.Change.Kind == ModelChangeKind.Skeleton);
        ScreenshotRunner.Expect(rigRow != null && !rigRow.Change.Unchanged && rigRow.CanInclude, $"review: the skeleton and animations row can be imported ({rigRow?.Detail})");
        await ScreenshotRunner.Shot(review, outDir, "109_import_review_skeleton");
        if (rigRow != null)
        {
            var cmp = await b.MeshCompareAsync(vm, rigRow);
            if (cmp != null)
            {
                var pick = cmp.Sequences.FirstOrDefault(s => s.Label.StartsWith("Stand [4]", StringComparison.Ordinal));
                ScreenshotRunner.Expect(pick != null && pick.Left < 0, "compare: the new animation is listed (right side only)");
                if (pick != null) cmp.SelectedSequence = pick;
                var cw = new ModelCompareWindow(cmp);
                ScreenshotRunner.Place(cw, 1180, 860);
                cw.Show();
                await ScreenshotRunner.Settle(900);
                cmp.Playing = false;
                cmp.TimeMs = 800;
                cmp.Camera.Orbit(40, 6);
                await ScreenshotRunner.Settle(500);
                await ScreenshotRunner.Shot(cw, outDir, "110_compare_animation");
                cw.Close();
            }
        }
        review.DialogResult = true;
        bool saved = await import;
        ScreenshotRunner.Expect(saved, "the skeleton and animation import was saved");
        await ScreenshotRunner.Until(() => mv.IsModelModified, 20000);
        await ScreenshotRunner.WaitModel(mv);
        var idle = mv.Sequences.FirstOrDefault(s => s.Name == "Stand [4]");
        ScreenshotRunner.Expect(idle != null, "viewer: the new animation Stand [4] is in the list");
        if (idle != null) mv.SelectedSequence = idle;
        mv.ShowBones = true;
        mv.Playing = false;
        mv.TimeMs = 800;
        await ScreenshotRunner.Settle(500);
        view.ScrollToModel();
        await ScreenshotRunner.Shot(win, outDir, "111_viewer_new_animation");
        mv.ShowBones = false;
        // undo the import so the later pictures show the game's boar
        var ws = b.Session.Workspace!;
        foreach (var id in ws.Manifest.ChangeSets.Keys.Where(k => !before.Contains(k)).ToList())
            ws.RevertChangeSet(id);
        mv.InvalidateTextures();

        // Used in game: the animations of the orc
        var u = b.UsedInGame;
        b.UsedInGameExpanded = true;
        await ScreenshotRunner.Search(b, "orc male");
        var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character && r.Name.Contains("Orc", StringComparison.OrdinalIgnoreCase));
        if (orc != null)
        {
            await ScreenshotRunner.Select(b, orc);
            await ScreenshotRunner.Until(() => !u.IsLoading && u.Report != null && u.Report.Key == orc.Entry.Key, 30000);
            ScreenshotRunner.Expect(u.Report?.Section("animations") != null, "used in game: the orc's animations section");
            await ScreenshotRunner.Settle(400);
            var box = view.UsedInGameBox;
            box.BringIntoView(new Rect(0, -40, Math.Max(1, box.ActualWidth), Math.Min(box.ActualHeight + 40, 1100)));
            await ScreenshotRunner.Shot(win, outDir, "112_used_in_game_animations");
        }
        b.UsedInGameExpanded = false;
        ScreenshotRunner.Place(win, 1440, 900);
    }
}
