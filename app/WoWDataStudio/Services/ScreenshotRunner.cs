using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Workspace;
using WoWDataStudio.Helpers;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// `--screenshot &lt;folder&gt;`: drives the real UI through its main screens with the real game data and renders each to a
/// PNG (WPF RenderTargetBitmap). Uses its own app folder (settings, index, workspace) so the user's data is untouched,
/// and never installs anything into the game folder. Also serves as the UI smoke test (exit code 0 = every step worked).
/// </summary>
public static class ScreenshotRunner
{
    public static bool Running { get; private set; }
    /// <summary>The main view model of the run (for the steps in other files).</summary>
    internal static MainViewModel? Main { get; private set; }
    public static List<string> Failures { get; } = new();
    private static StreamWriter? _log;

    internal static void Log(string s)
    {
        _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
        _log?.Flush();
        Console.WriteLine(s);
    }

    internal static async Task Settle(int ms = 350)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    internal static async Task Shot(Window w, string dir, string name)
    {
        await Settle();
        var path = Path.Combine(dir, name + ".png");
        ImageSources.SaveVisual((FrameworkElement)w.Content, path);
        Log($"saved {path}");
    }

    internal static void Place(Window w, double width, double height)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -30000;
        w.Top = 0;
        w.Width = width;
        w.Height = height;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
    }

    internal static async Task Until(Func<bool> cond, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("the UI did not reach the expected state in time");
            await Task.Delay(40);
        }
    }

    public static async Task<int> RunAsync(MainWindow win, MainViewModel vm, string outDir, string? clientArg)
    {
        Running = true;
        Main = vm;
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, "screenshot.log"), false);
        try
        {
            var session = vm.Session;
            if (Directory.Exists(session.Paths.Root) && string.Equals(Path.GetFullPath(session.Paths.Root).TrimEnd(Path.DirectorySeparatorChar), Path.Combine(outDir, "_home"), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(session.Paths.Root, true);   // a fresh home each run (only our own temporary one)
                Directory.CreateDirectory(session.Paths.Root);
            }
            Place(win, 1440, 900);
            win.Show();

            // 1. welcome (+ a validation error)
            var client = clientArg ?? vm.FolderText;
            vm.FolderText = client;
            vm.Screen = Screen.Welcome;
            await Shot(win, outDir, "01_welcome");
            vm.FolderText = Path.Combine(Path.GetTempPath());
            vm.UseFolderCommand.Execute(null);
            await Shot(win, outDir, "02_welcome_error");
            if (!ClientValidator.Check(client).Ok) throw new InvalidOperationException($"not a 1.12 client folder: {client}");
            vm.FolderText = client;
            vm.WelcomeError = "";

            // 2. organizing (first run: the index is built)
            var open = vm.OpenClientAsync(client, false);
            await Until(() => vm.ProgressPercent >= 20 || open.IsCompleted);
            await Shot(win, outDir, "03_organizing", 0);
            await open;
            if (vm.Screen != Screen.Browser || vm.Browser == null) throw new InvalidOperationException("the main screen did not open: " + vm.WelcomeError);
            var b = vm.Browser;
            b.HotReloadLocator = HotReloadScreenshots.NoGame;   // 0.11.0: the same line in every picture ("the game is not running"), whatever runs on this PC
            Log($"index: {session.Entries.Count} entries, server connected: {session.ServerConnected} ({session.ServerMessage})");
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "server")
            {
                // developers: only the Server data pictures (95-106), against a throwaway database
                await ServerDataScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "rig")
            {
                // developers: only the skeleton and animation pictures (107-112)
                await SkeletonScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "hotreload")
            {
                // developers: only the hot reload pictures (115-118), against a stand-in for the running game
                await HotReloadScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "designers")
            {
                // developers: only the designers' pictures (119-126), against a throwaway database
                await DesignerScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "quests")
            {
                // developers: only the Quest chains pictures (127-136), against a throwaway database
                await QuestScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "versions")
            {
                // developers: only the Other versions pictures (137-140), from the copies in WDS_SCREENSHOT_OTHER_CLIENTS
                await VersionScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }
            if (Environment.GetEnvironmentVariable("WDS_SCREENSHOT_ONLY") == "tables")
            {
                // developers: only the Data tables pictures (83-94), for a quick look at that tab
                await DbcScreenshots.RunAsync(win, b, outDir);
                Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:" + string.Join("", Failures.Select(f => Environment.NewLine + "  " + f)));
                return Failures.Count == 0 ? 0 : 1;
            }

            // 3. search a creature, show its chain and skin
            await Search(b, "mottled boar");
            var boar = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Creature && r.Name == "Mottled Boar") ?? b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Creature)
                       ?? throw new InvalidOperationException("no creature found for 'mottled boar'");
            await Select(b, boar);
            Expect(b.Chain.Any(c => c.Contains("Boar.m2")), "creature chain shows Boar.m2");
            Expect(b.Textures.Any(t => t.FileName.Equals("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase)), "creature textures include BoarSkinIvory.blp");
            await Shot(win, outDir, "04_main_creature");

            // 4. character
            await Search(b, "orc male");
            var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character);
            if (orc != null)
            {
                await Select(b, orc);
                await Shot(win, outDir, "05_main_character");
            }

            // 5. the texture itself, with the viewer
            foreach (var c in b.Chips) c.IsChecked = c.Kind == AssetKind.Texture;
            await Search(b, "boarskinivory");
            var tex = b.Results.FirstOrDefault(r => r.Path.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("texture not found");
            await Select(b, tex);
            await Until(() => b.Viewer.IsLoaded);
            await Shot(win, outDir, "06_texture_viewer");

            // 6. import an edited PNG: confirmation step with the compare view in every mode
            var path = tex.Path;
            var work = Path.Combine(outDir, "_work");
            Directory.CreateDirectory(work);
            var png = Path.Combine(work, "BoarSkinIvory.png");
            session.Textures!.Export(path, png, new() { ["screenshot"] = "1" });
            var img = Png.Load(png);
            PaintTestEdit(img);
            Png.Save(img, png, true);
            var plan = session.Textures.Plan(png, path);
            Expect(!plan.NeedsResize && plan.Diff != null && plan.Diff.ChangedPixels > 0, "import plan has a diff");
            var cmp = new CompareViewModel(Path.GetFileName(path), new TextureCompareSide(CompareViewModel.OriginalLabel, plan.OriginalData!),
                new TextureCompareSide(CompareViewModel.EditLabel, plan.Encoded));
            var cw = new CompareWindow(cmp);
            Place(cw, 1180, 780);
            cw.AsImportConfirmation($"Import {Path.GetFileName(png)} as {path}", plan.Warnings, plan.Notes,
                new[] { $"Will be saved as {plan.FormatName}, {plan.EncodedBlp.Width}x{plan.EncodedBlp.Height}, {plan.EncodedBlp.LevelCount} mip levels (same format as the original).", plan.Diff!.Summary });
            cw.Show();
            await Shot(cw, outDir, "07_import_confirm_side_by_side");
            cmp.Mode = CompareMode.Wipe;
            cmp.WipeFraction = 0.45;
            await Shot(cw, outDir, "08_compare_slider");
            cmp.Mode = CompareMode.Flicker;
            cmp.FlickerShowsOriginal = true;
            await Shot(cw, outDir, "09_compare_toggle");
            cmp.Mode = CompareMode.Difference;
            await Shot(cw, outDir, "10_compare_difference");
            Expect(cmp.ChangedPercent > 0 && cmp.ChangedPercent < 100, $"difference view reports {cmp.ChangedPercent:0.##}% changed");
            cmp.MipLevel = 2;
            cmp.AlphaView = AlphaView.AlphaOnly;
            await Shot(cw, outDir, "11_compare_difference_mip2_alpha");
            cw.Close();

            // 7. keep the change (into this run's own workspace), then the modified state and "My changes"
            Expect(await b.ImportPngAsync(png, path, interactive: false), "import saved to the workspace");
            await Settle(600);
            await Until(() => b.Viewer.IsModified);
            Expect(session.Workspace!.IsModified(path), "workspace records the change");
            foreach (var c in b.Chips) c.IsChecked = true;
            await Search(b, "boar");
            b.OnlyMyChanges = true;
            await Settle(500);
            Expect(b.Results.Count > 0 && b.Results.All(r => r.ShowBadge), "only-my-changes filter lists modified rows");
            var row = b.Results.FirstOrDefault(r => r.IsModified) ?? b.Results[0];
            await Select(b, row);
            await Shot(win, outDir, "12_main_modified");
            b.OnlyMyChanges = false;
            b.SelectedTab = 1;
            await Shot(win, outDir, "13_my_changes");

            // 8. compare window opened from "My changes"
            var cmp2 = b.CompareFor(path) ?? throw new InvalidOperationException("no compare for the modified texture");
            var cw2 = new CompareWindow(cmp2);
            Place(cw2, 1180, 780);
            cw2.Show();
            cmp2.Mode = CompareMode.SideBySide;
            await Shot(cw2, outDir, "14_compare_from_my_changes");
            cw2.Close();

            // 9. dialogs: settings, build patch (not built), help
            var svm = new SettingsViewModel(session.Settings, session.Client!.ClientDir);
            var sw = new SettingsWindow(svm);
            Place(sw, 640, 700);
            sw.Show();
            await svm.TestConnectionCommand.ExecuteAsync(null);
            await Shot(sw, outDir, "15_settings");
            sw.Place(640, 980);
            sw.MapsCard.BringIntoView();
            await Shot(sw, outDir, "55_settings_maps");
            sw.Close();
            var bw = new BuildPatchWindow(session.Workspace, b.Changes.ToList(), session.Client.DataDir);
            Place(bw, 720, 560);
            bw.Show();
            await Shot(bw, outDir, "16_build_patch");
            bw.Close();
            b.SelectedTab = 0;
            b.HelpOpen = true;
            await Shot(win, outDir, "17_help");
            b.HelpOpen = false;

            // a message dialog
            var dlg = Dialogs.Build("Test in game (install as loose files)",
                "This copies your edited texture into the game folder:\n" + session.Client.DataDir + "\\<the file's game path>\n\nThe game only reads such loose files when its loose-file mode is on. The project's own client has it built in from update wave185 on; a normal 1.12.1 client ignores loose files and keeps using its archives (use \"Build release patch\" there).\n\nTextures are read when the game starts: restart the game to see the change. Use \"Remove from game\" to undo.",
                DialogKind.Info, new[] { "Install into the game folder", "Cancel" }, null, "How the loose-file mode works (LOOSE_FILES_PATCH.md)", vm.LooseFilesDoc, out _);
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.Left = -30000;
            dlg.ShowActivated = false;
            dlg.Show();
            await Shot(dlg, outDir, "18_test_in_game_dialog");
            dlg.Close();

            // 10. the 3D viewer (Phase 3b): creature skins, character looks, a tree, an item, a spell effect, the compare view
            await ViewerSteps(win, b, outDir);

            // 11. extra screens for the user manual (docs\manual)
            await ManualScreenshots.RunAsync(win, b, outDir);

            // 12. Phase 3c: export for Blender, import an edited model (scripted edit), review, compare, my changes
            await ModelIOScreenshots.RunAsync(win, b, outDir);

            // 12b. 0.10.0: the skeleton and animation round trip (SkeletonScreenshots.cs)
            await SkeletonScreenshots.RunAsync(win, b, outDir);

            // 13. "Used in game": the panel for each kind of entry and its links (UsedInGameScreenshots.cs)
            await UsedInGameScreenshots.RunAsync(win, b, outDir);

            // 14. Phase 4: maps and terrain (MapScreenshots.cs)
            await MapScreenshots.RunAsync(win, b, outDir);

            // 15. 0.5.1: the pop-out 3D windows, full screen, fly camera, placement memory, GL leak check (PopoutScreenshots.cs)
            await PopoutScreenshots.RunAsync(win, b, outDir);

            // 16. 0.6.0: the tile breakdown, terrain to Blender (tile picker, package) and back (seams, review) (BreakdownScreenshots.cs)
            await BreakdownScreenshots.RunAsync(win, b, outDir);

            // 17. 0.6.1: ground textures and their _s files, colour / alpha statistics, additive glows, uninstalling the
            // release patch (in a fake game folder), the specular ground in the Maps tab (SpecularScreenshots.cs)
            await SpecularScreenshots.RunAsync(win, b, outDir);

            // 18. 0.7.0: the Data tables tab (DBC editor): links, Used by, validation, save, compare, bulk edit, new rows,
            // import preview, copy to server into a fake server folder (DbcScreenshots.cs)
            await DbcScreenshots.RunAsync(win, b, outDir);

            // 19. 0.9.0: the Server data tab, against a THROWAWAY MySQL server loaded from the tests' fixture (never the
            // user's server): views, links, edits, a new vendor and quest, the client / server check, apply, import
            await ServerDataScreenshots.RunAsync(win, b, outDir);

            // 20. 0.11.0: hot reload, against a stand-in for the running game (HotReloadScreenshots.cs)
            await HotReloadScreenshots.RunAsync(win, b, outDir);

            // 21. 0.12.0: the spell designer and the class and talent designer, against a throwaway MySQL server
            // (DesignerScreenshots.cs)
            await DesignerScreenshots.RunAsync(win, b, outDir);

            // 22. 0.13.0: the Quest chains tab, against a throwaway MySQL server with the quest-chain supplement
            // (QuestScreenshots.cs)
            await QuestScreenshots.RunAsync(win, b, outDir);

            // 23. 0.14.0: the Other versions tab, from client copies named by WDS_SCREENSHOT_OTHER_CLIENTS (skipped
            // without them; VersionScreenshots.cs)
            await VersionScreenshots.RunAsync(win, b, outDir);

            Log(Failures.Count == 0 ? "ALL STEPS OK" : "FAILURES:\n  " + string.Join("\n  ", Failures));
            return Failures.Count == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            Log("FAILED: " + e);
            return 2;
        }
        finally
        {
            _log?.Dispose();
            Running = false;
        }
    }

    private static Task Shot(Window w, string dir, string name, int _) => Shot(w, dir, name);

    // ---------------------------------------------------------------- 3D viewer

    internal static async Task WaitModel(ModelViewerViewModel mv)
    {
        await Until(() => !mv.IsLoading && (mv.HasModel || mv.Message.Length > 0), 60000);
        await Settle(500);
    }

    /// <summary>A render of the viewport is not blank: many colours, and the centre differs from the background.</summary>
    internal static bool LooksRendered(RgbaImage img, out string why)
    {
        var colours = new HashSet<int>();
        for (int i = 0; i < img.Pixels.Length; i += 4 * 7) colours.Add(img.Pixels[i] << 16 | img.Pixels[i + 1] << 8 | img.Pixels[i + 2]);
        int bg = img.Pixels[0] << 16 | img.Pixels[1] << 8 | img.Pixels[2];
        int differ = 0, total = 0;
        for (int y = img.Height / 4; y < img.Height * 3 / 4; y += 3)
            for (int x = img.Width / 4; x < img.Width * 3 / 4; x += 3)
            {
                int o = img.Offset(x, y);
                int c = img.Pixels[o] << 16 | img.Pixels[o + 1] << 8 | img.Pixels[o + 2];
                total++;
                if (Math.Abs((c >> 16) - (bg >> 16)) + Math.Abs(((c >> 8) & 255) - ((bg >> 8) & 255)) + Math.Abs((c & 255) - (bg & 255)) > 24) differ++;
            }
        double share = total == 0 ? 0 : differ * 100.0 / total;
        why = $"{colours.Count} colours, {share:0.#}% of the centre differs from the background";
        return colours.Count > 40 && share > 3;
    }

    internal static async Task<RgbaImage> Render(ModelViewerViewModel mv, string dir, string name)
    {
        await Settle(300);
        var path = Path.Combine(dir, "render_" + name + ".png");
        await mv.SaveImageToAsync(path);
        var img = Png.Load(path);
        Expect(LooksRendered(img, out var why), $"render {name}: {why}");
        return img;
    }

    internal static int Differs(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 100;
        long d = 0;
        for (int i = 0; i < a.Pixels.Length; i++) d += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return (int)(d * 100 / (255L * a.Pixels.Length) + (d > 0 ? 1 : 0));
    }

    private static async Task ViewerSteps(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var mv = b.ModelViewer;
        foreach (var c in b.Chips) c.IsChecked = true;
        Place(win, 1500, 1240);
        b.SelectedTab = 0;

        // the Mottled Boar: two skins (skin picker), the creature's animation
        await Search(b, "mottled boar");
        var boar = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Creature && r.Name == "Mottled Boar") ?? b.Results.First(r => r.Entry.Kind == AssetKind.Creature);
        await Select(b, boar);
        await WaitModel(mv);
        Expect(mv.HasModel && mv.ModelPath.EndsWith("Boar.m2", StringComparison.OrdinalIgnoreCase), "boar model shown");
        Expect(mv.Sequences.Count == 13, $"boar has 13 sequences in the dropdown ({mv.Sequences.Count})");
        Expect(mv.Sequences.Any(s => s.Label.StartsWith("Stand", StringComparison.Ordinal)), "sequences are named from AnimationData (Stand)");
        Expect(mv.Variants.Count >= 2, $"skin picker lists the displays of Boar.m2 ({mv.Variants.Count})");
        Log($"boar: {mv.Variants.Count} displays in the skin picker, e.g. {string.Join("; ", mv.Variants.Take(4).Select(v => v.Label))}; load {mv.LastLoadMs:0} ms");
        view.ScrollToModel();
        mv.Playing = false;
        mv.TimeMs = 300;
        await Shot(win, outDir, "19_viewer_creature");
        var boar1 = await Render(mv, outDir, "boar_skin1");
        var other = mv.Variants.FirstOrDefault(v => mv.SelectedVariant != null && !v.Skins.SequenceEqual(mv.SelectedVariant.Skins)) ?? mv.Variants.Last();
        var sw = Stopwatch.StartNew();
        mv.SelectedVariant = other;
        await Until(() => mv.ModelTextures.Any(t => other.Skins.Any(s => s.Length > 0 && t.FileName.StartsWith(s, StringComparison.OrdinalIgnoreCase))), 10000);
        Log($"boar: skin switch to {other.Label} took {sw.ElapsedMilliseconds} ms (look {mv.LastLookMs:0.0} ms)");
        var boar2 = await Render(mv, outDir, "boar_skin2");
        Expect(Differs(boar1, boar2) > 0, "the second boar skin renders differently");
        await Shot(win, outDir, "20_viewer_skin_picker");

        // the compare foundation: two skins side by side with one camera
        var cmp = await mv.MakeCompareAsync();
        Expect(cmp != null, "compare view model for two skins");
        if (cmp != null)
        {
            var cw = new ModelCompareWindow(cmp);
            Place(cw, 1180, 700);
            cw.Show();
            await Settle(900);
            cmp.Camera.Orbit(40, 5);
            await Settle(400);
            await Shot(cw, outDir, "21_compare_two_skins");
            var (l, r) = await cw.Compare.CaptureBothAsync(480, 400);
            Expect(LooksRendered(l, out var w1) && LooksRendered(r, out var w2), "both compare views render");
            Expect(Differs(l, r) > 0, "the two compare views show different skins");
            Png.Save(l, Path.Combine(outDir, "render_compare_left.png"), false);
            Png.Save(r, Path.Combine(outDir, "render_compare_right.png"), false);
            cw.Close();
        }

        // the orc male: character creation pickers, a few looks, one animation frame with the skeleton
        await Search(b, "orc male");
        var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character && r.Name.Contains("Orc", StringComparison.OrdinalIgnoreCase));
        Expect(orc != null, "orc male found");
        if (orc != null)
        {
            await Select(b, orc);
            await WaitModel(mv);
            Expect(mv.IsCharacter && mv.SkinColors.Count > 1 && mv.HairStyles.Count > 1 && mv.FacialHairs.Count > 1,
                $"character pickers ({mv.SkinColors.Count} skins, {mv.Faces.Count} faces, {mv.HairStyles.Count} hair styles, {mv.HairColors.Count} hair colours, {mv.FacialHairs.Count} facial hair)");
            Expect(mv.Sequences.Count == 134, $"orc male: 134 sequences ({mv.Sequences.Count})");
            Log($"orc male: load {mv.LastLoadMs:0} ms");
            view.ScrollToModel();
            var renders = new List<RgbaImage> { await Render(mv, outDir, "orc_look1") };
            int Pick(IList<int> l, int i) => l.Count == 0 ? 0 : l[Math.Min(i, l.Count - 1)];
            var looks = new[]
            {
                new CharacterLook(Pick(mv.SkinColors, 3), 2, Pick(mv.HairStyles, 3), 2, Pick(mv.FacialHairs, 3)),
                new CharacterLook(Pick(mv.SkinColors, 6), 4, Pick(mv.HairStyles, 5), 6, Pick(mv.FacialHairs, 6)),
            };
            int k = 2;
            foreach (var look in looks)
            {
                sw.Restart();
                await mv.SetCharacterAsync(look);
                Log($"orc male: look {look} applied in {sw.ElapsedMilliseconds} ms (composite {mv.LastLookMs:0.0} ms)");
                renders.Add(await Render(mv, outDir, $"orc_look{k++}"));
            }
            Expect(Differs(renders[0], renders[1]) > 0 && Differs(renders[1], renders[2]) > 0, "different character looks render differently");
            // frame-rate check while the 128-bone model animates
            var vp = view.ModelViewport;
            mv.SelectedSequence = mv.Sequences.FirstOrDefault(s => s.Label.StartsWith("Run", StringComparison.Ordinal)) ?? mv.SelectedSequence;
            mv.Playing = true;
            int f0 = vp.Frames;
            var t0 = Stopwatch.StartNew();
            await Task.Delay(2000);
            double fps = (vp.Frames - f0) / t0.Elapsed.TotalSeconds;
            Log($"orc male animating: {fps:0} frames/s, last frame {vp.LastFrameMs:0.00} ms CPU ({vp.GlInfo})");
            // the frame rate is logged, not judged: a busy machine draws fewer frames (timing limits live in PerformanceTests)
            Expect(vp.Frames - f0 >= 3, $"orc male animates ({fps:0} frames/s)");
            mv.SelectedSequence = mv.Sequences.FirstOrDefault(s => s.Label.StartsWith("Attack1H", StringComparison.Ordinal)) ?? mv.SelectedSequence;
            mv.Playing = false;
            mv.TimeMs = Math.Min(mv.DurationMs, 450);
            mv.ShowBones = true;
            await Settle(400);
            var still = await Render(mv, outDir, "orc_animation_frame");
            Expect(Differs(still, renders[^1]) > 0, "an animation frame differs from the rest pose");
            await Shot(win, outDir, "22_viewer_character");
            mv.ShowBones = false;
            // the four views (skin profiles for 256/75/53/21 bones per draw) all render
            foreach (var lod in mv.Lods.ToList())
            {
                mv.SelectedLod = lod;
                await Render(mv, outDir, $"orc_view{lod.Index + 1}");
            }
            Expect(mv.Lods.Count == 4 && mv.Lods[3].Label.Contains("21 bones"), $"views: {string.Join("; ", mv.Lods.Select(l => l.Label))}");
            mv.SelectedLod = mv.Lods.First();
        }

        // a tree doodad, an item, a spell effect
        foreach (var (query, kind, file, shot, name) in new[]
                 {
                     ("durotarpalm01", AssetKind.WorldObject, "DurotarPalm01.m2", "23_viewer_tree", "tree"),
                     ("worn battleaxe", AssetKind.Item, ".m2", "24_viewer_item", "item"),
                     ("ice barrier state", AssetKind.SpellEffect, ".m2", "25_viewer_spell_effect", "spell_effect"),
                 })
        {
            await Search(b, query);
            var row = b.Results.FirstOrDefault(r => r.Entry.Kind == kind);
            if (row == null && kind == AssetKind.Item)
            {
                await Search(b, "battle axe");
                row = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Item);
            }
            Expect(row != null, $"{name}: '{query}' found");
            if (row == null) continue;
            await Select(b, row);
            await WaitModel(mv);
            Expect(mv.HasModel && mv.ModelPath.EndsWith(file, StringComparison.OrdinalIgnoreCase), $"{name}: model shown ({mv.ModelPath}) {mv.Message}");
            Log($"{name}: {mv.ModelPath}, load {mv.LastLoadMs:0} ms, {mv.Variants.Count} variants");
            view.ScrollToModel();
            if (kind == AssetKind.Item)
            {
                mv.Wireframe = true;
                mv.ShowBounds = true;
            }
            if (kind == AssetKind.SpellEffect)
            {
                // spell effects grow in at the start of their sequence: show a frame in the middle
                mv.Playing = false;
                mv.TimeMs = mv.DurationMs * 0.6;
            }
            await Render(mv, outDir, name);
            await Shot(win, outDir, shot);
            mv.Wireframe = false;
            mv.ShowBounds = false;
        }

        // a texture opened from the model's texture list goes to the texture viewer
        await Search(b, "mottled boar");
        await Select(b, b.Results.First(r => r.Entry.Kind == AssetKind.Creature));
        await WaitModel(mv);
        var tex = mv.ModelTextures.FirstOrDefault(t => t.CanOpen);
        Expect(tex != null, "the boar's texture list has an openable texture");
        if (tex != null)
        {
            mv.OpenTextureCommand.Execute(tex);
            await Until(() => b.Viewer.IsLoaded || b.Viewer.Error.Length > 0);
            Expect(b.SelectedTexture != null && string.Equals(b.SelectedTexture.Path, tex.Path, StringComparison.OrdinalIgnoreCase), "clicking a model texture opens it in the texture viewer");
        }
        Place(win, 1440, 900);
    }

    internal static void Expect(bool ok, string what)
    {
        Log((ok ? "ok: " : "FAILED: ") + what);
        if (!ok) Failures.Add(what);
    }

    internal static async Task Search(BrowserViewModel b, string text)
    {
        b.SearchText = text;
        await b.ApplyFilterNow();
        await Settle(250);
    }

    internal static async Task Select(BrowserViewModel b, ResultRow row)
    {
        b.SelectedRow = row;
        await Until(() => !b.DetailsLoading && b.HasSelection);
        if (b.SelectedTexture != null) await Until(() => b.Viewer.IsLoaded || b.Viewer.Error.Length > 0);
        await Settle(300);
    }

    /// <summary>A visible test edit: a red tint over a band and a dark stripe, alpha kept.</summary>
    public static void PaintTestEdit(RgbaImage img)
    {
        var p = img.Pixels;
        for (int y = img.Height / 4; y < img.Height / 2; y++)
            for (int x = img.Width / 8; x < img.Width * 5 / 8; x++)
            {
                int o = img.Offset(x, y);
                if (p[o + 3] == 0) continue;
                p[o] = (byte)Math.Min(255, p[o] / 2 + 128);
                p[o + 1] = (byte)(p[o + 1] / 2);
                p[o + 2] = (byte)(p[o + 2] / 2);
            }
        for (int y = img.Height * 5 / 8; y < img.Height * 5 / 8 + Math.Max(2, img.Height / 32); y++)
            for (int x = 0; x < img.Width; x++)
            {
                int o = img.Offset(x, y);
                if (p[o + 3] == 0) continue;
                p[o] = 20; p[o + 1] = 40; p[o + 2] = 160;
            }
    }
}
