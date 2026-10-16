using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.5.1, the pop-out 3D windows), in this run's own app folder. The windows are drawn off screen
/// on an invisible "monitor" left of the desktop (<see cref="PopoutChrome.MonitorsOverride"/>), so saving and restoring
/// their placement and full screen never show anything on the user's screen.
///   56_model_popout              the model pop-out window (Mottled Boar, following the selection, side panel open)
///   57_model_popout_fullscreen   full screen, controls hidden, fly camera with the help overlay (H)
///   58_main_popout_following     the main window while a following pop-out is open (docked view paused)
///   59_model_popout_pinned       a pinned window (the orc) while the main window shows another model
///   60_terrain_popout            the terrain pop-out window (fly camera, help overlay)
///   61_terrain_popout_fullscreen the terrain window full screen with the neighbour tiles, controls hidden
///   62_compare_fullscreen        the model compare window full screen (F11)
/// Checks: follow / pin, the title, the docked pause, F11 / Esc, Ctrl+H, H, presets, the fly camera, the placement
/// saved and restored per kind, F1 topics, and no GL leak over 20 open / close cycles of each kind.
/// </summary>
internal static class PopoutScreenshots
{
    public const string ScreenshotMonitor = @"\\.\WDS-SCREENSHOT";
    private static readonly PixelRect Area = new(-30000, 0, 1600, 1000);

    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static void PlaceOff(Window w, double width, double height)
    {
        ScreenshotRunner.Place(w, width, height);
        w.ShowInTaskbar = false;
    }

    /// <summary>Send a key to the window as if typed (PreviewKeyDown, then KeyDown on <paramref name="target"/>).</summary>
    private static void Press(Window w, Key key, IInputElement? target = null)
    {
        var src = PresentationSource.FromVisual(w) ?? throw new InvalidOperationException("window not shown");
        target ??= w;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (preview.Handled) return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static async Task<RgbaImage> Render(Controls.ModelViewport vp, string dir, string name)
    {
        await ScreenshotRunner.Settle(250);
        var img = await vp.CaptureAsync(640, 420);
        Png.Save(img, Path.Combine(dir, "render_" + name + ".png"), false);
        Expect(ScreenshotRunner.LooksRendered(img, out var why), $"render {name}: {why}");
        return img;
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        PopoutChrome.MonitorsOverride = () =>
        {
            var l = new List<MonitorArea> { new(ScreenshotMonitor, Area, false, Area) };
            l.AddRange(PopoutChrome.Monitors());
            return l;
        };
        try
        {
            await ModelSteps(win, b, outDir);
            await TerrainSteps(win, b, outDir);
        }
        finally
        {
            b.CloseAllPopouts();
            PopoutChrome.MonitorsOverride = null;
            ScreenshotRunner.Place(win, 1440, 900);
        }
    }

    private static async Task<ModelPopoutWindow> OpenModel(BrowserViewModel b)
    {
        var w = await b.OpenModelPopoutAsync(x => PlaceOff(x, 1300, 860)) ?? throw new InvalidOperationException("the model pop-out did not open");
        await ScreenshotRunner.WaitModel(w.Vm.Viewer);
        await ScreenshotRunner.Until(() => w.Viewport.Frames > 0, 20000);
        return w;
    }

    private static async Task ModelSteps(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var mv = b.ModelViewer;
        var settings = b.Session.Settings;
        settings.Popouts.Clear();
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = true;
        await ScreenshotRunner.Search(b, "mottled boar");
        var boar = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Creature && r.Name == "Mottled Boar") ?? b.Results.First(r => r.Entry.Kind == AssetKind.Creature);
        await ScreenshotRunner.Select(b, boar);
        await ScreenshotRunner.WaitModel(mv);
        var other = mv.Variants.FirstOrDefault(v => mv.SelectedVariant != null && !v.Skins.SequenceEqual(mv.SelectedVariant.Skins));
        if (other != null)
        {
            mv.SelectedVariant = other;
            await ScreenshotRunner.Settle(600);
        }

        // open: the same model, skin and view; the title says what is shown; the docked view pauses
        var w1 = await OpenModel(b);
        Expect(w1.Vm.Viewer.ModelPath.EndsWith("Boar.m2", StringComparison.OrdinalIgnoreCase), $"pop-out: shows the selected model ({w1.Vm.Viewer.ModelPath})");
        Expect(w1.Title == $"{mv.EntryName} — Boar.m2", $"pop-out: the title says what is shown (\"{w1.Title}\")");
        Expect(w1.Vm.Viewer.SelectedVariant?.DisplayId == mv.SelectedVariant?.DisplayId, $"pop-out: opens with the docked view's skin ({w1.Vm.Viewer.SelectedVariant?.Label})");
        Expect(w1.Vm.FollowSelection && mv.PausedForPopout && w1.Viewport.Navigation == CameraNavigation.Orbit, "pop-out: follows the selection by default, orbit camera; the docked view pauses its animation");
        Expect(Manual.GetTopic(w1) == "model-viewer" && Manual.GetTopic(w1.Viewport) == "model-viewer", "pop-out: F1 opens the model viewer chapter");
        Expect(w1.Vm.Viewer.Sequences.Count == mv.Sequences.Count && w1.Vm.Viewer.Geosets.Count == mv.Geosets.Count && w1.Vm.Viewer.Variants.Count == mv.Variants.Count,
            "pop-out: the same controls (animations, geosets, skins) as the docked viewer");
        var boarRender = await Render(w1.Viewport, outDir, "popout_boar");
        await ScreenshotRunner.Shot(w1, outDir, "56_model_popout");
        view.ScrollToModel();
        await ScreenshotRunner.Shot(win, outDir, "58_main_popout_following");

        // presets: front, side, top differ; F focuses; H shows the help
        var cam = w1.Vm.Viewer.Camera;
        w1.Viewport.ApplyPreset(CameraPreset.Front);
        Expect(Math.Abs(cam.Yaw - cam.FrontYaw) < 0.01f && Math.Abs(cam.Pitch) < 0.01f, "camera preset Front");
        var front = await Render(w1.Viewport, outDir, "popout_front");
        Press(w1, Key.D3, w1.Viewport);
        Expect(cam.Pitch > 88f, $"key 3: the Top preset (pitch {cam.Pitch:0})");
        var top = await Render(w1.Viewport, outDir, "popout_top");
        Expect(ScreenshotRunner.Differs(front, top) > 0, "the Front and Top views differ");
        Press(w1, Key.D2, w1.Viewport);
        Expect(Math.Abs(cam.Yaw - (cam.FrontYaw + 90f) % 360f) < 0.01f, "key 2: the Side preset");
        Press(w1, Key.H, w1.Viewport);
        Expect(w1.Vm.ShowHelp && w1.Viewport.HelpText.Contains("front / side / top view") && w1.Viewport.HelpText.Contains("F11"), "H: the help overlay with the controls (and the window keys)");

        // follow: the main window selects the orc male, the pop-out follows
        await ScreenshotRunner.Search(b, "orc male");
        var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character && r.Name.Contains("Orc", StringComparison.OrdinalIgnoreCase));
        Expect(orc != null, "pop-out: orc male found");
        if (orc == null) return;
        await ScreenshotRunner.Select(b, orc);
        await ScreenshotRunner.Until(() => w1.Vm.Viewer.ModelPath.EndsWith("OrcMale.m2", StringComparison.OrdinalIgnoreCase) && !w1.Vm.Viewer.IsLoading && w1.Vm.Viewer.HasModel, 30000);
        Expect(w1.Vm.Viewer.IsCharacter && w1.Title.Contains("OrcMale.m2"), $"pop-out: follows the selection to the orc male (\"{w1.Title}\")");

        // fly camera: moves along the view at the fly speed, the mouse look keeps the eye; renders differ
        w1.Vm.Navigation = CameraNavigation.Fly;
        await ScreenshotRunner.Settle(200);
        Expect(w1.Viewport.Navigation == CameraNavigation.Fly && Math.Abs(cam.Distance - cam.FlyFocusDistance) < 0.01f, $"Fly: the target moves close to the eye ({cam.Distance:0.###} yd)");
        w1.Viewport.ApplyPreset(CameraPreset.Reset);
        var eye0 = cam.Eye;
        var fwd = cam.Forward;
        var flyBefore = await Render(w1.Viewport, outDir, "popout_fly_start");
        Controls.ModelViewport.FlyStep(cam, new Vector3(1, 0, 0), 1f, 0.5);
        var moved = cam.Eye - eye0;
        Expect(Math.Abs(moved.Length() - cam.FlySpeed * 0.5f) < 0.01f && Vector3.Dot(Vector3.Normalize(moved), fwd) > 0.999f, $"Fly: W moves {moved.Length():0.###} yd forward at {cam.FlySpeed:0.##} yd/s");
        var eye1 = cam.Eye;
        cam.Look(-20f, 5f);
        Expect(Vector3.Distance(eye1, cam.Eye) < 0.001f, "Fly: looking around keeps the eye in place");
        var flyAfter = await Render(w1.Viewport, outDir, "popout_fly_moved");
        Expect(ScreenshotRunner.Differs(flyBefore, flyAfter) > 0, "Fly: the view changes when flying and looking");
        Press(w1, Key.F, w1.Viewport);
        Expect(Vector3.Distance(cam.Target, cam.Home.Target) < cam.Home.Distance, "F: focus brings the selection back into view");

        // F11 full screen, Ctrl+H hides the controls, Esc leaves full screen
        Press(w1, Key.F11);
        Expect(w1.Chrome.IsFullScreen && w1.WindowStyle == WindowStyle.None && w1.Vm.IsFullScreen && w1.Chrome.WindowRect == Area,
            $"F11: full screen covers the monitor ({w1.Chrome.WindowRect})");
        w1.Vm.ShowControls = false;
        await ScreenshotRunner.Settle(500);
        Expect(!w1.SidePanel.IsVisible, "Hide controls: the side panel and tool bar are hidden");
        await ScreenshotRunner.Shot(w1, outDir, "57_model_popout_fullscreen");
        w1.Vm.ShowControls = true;
        Press(w1, Key.Escape);
        Expect(!w1.Chrome.IsFullScreen && w1.WindowStyle != WindowStyle.None && !w1.Vm.IsFullScreen, "Esc: leaves full screen");
        Press(w1, Key.F11);
        Press(w1, Key.F11);
        Expect(!w1.Chrome.IsFullScreen, "F11 twice: back to the window");
        w1.Vm.ShowHelp = false;
        w1.Vm.Navigation = CameraNavigation.Orbit;

        // pin: the orc stays while the main window shows the boar; a second (following) window shows the boar
        w1.Vm.FollowSelection = false;
        Expect(!mv.PausedForPopout, "pinned: the docked view plays its animation again");
        await ScreenshotRunner.Search(b, "mottled boar");
        await ScreenshotRunner.Select(b, b.Results.First(r => r.Entry.Kind == AssetKind.Creature));
        await ScreenshotRunner.WaitModel(mv);
        await ScreenshotRunner.Settle(800);
        Expect(w1.Vm.Viewer.ModelPath.EndsWith("OrcMale.m2", StringComparison.OrdinalIgnoreCase) && w1.Title.EndsWith("(pinned)", StringComparison.Ordinal),
            $"pinned: keeps its model (\"{w1.Title}\")");
        var w2 = await OpenModel(b);
        Expect(w2.Vm.Viewer.ModelPath.EndsWith("Boar.m2", StringComparison.OrdinalIgnoreCase) && w1.Vm.Viewer.ModelPath.EndsWith("OrcMale.m2", StringComparison.OrdinalIgnoreCase),
            "two windows side by side: the pinned orc and a following boar");
        await ScreenshotRunner.Shot(w1, outDir, "59_model_popout_pinned");
        w2.Close();
        await ScreenshotRunner.Settle(200);

        // the placement is remembered per kind (size, position, monitor), also in settings.json
        var rect = new PixelRect(Area.X + 120, Area.Y + 60, 1100, 720);
        w1.Chrome.MoveTo(rect);
        await ScreenshotRunner.Settle(200);
        w1.Close();
        await ScreenshotRunner.Settle(300);
        var saved = settings.Popouts.GetValueOrDefault("model")?.Placement;
        Expect(saved != null && saved.Rect == rect && saved.Monitor == ScreenshotMonitor && !saved.Maximized, $"placement saved on close: {saved?.Rect} on {saved?.Monitor}");
        var onDisk = AppSettings.Load(b.Session.Paths).Popouts.GetValueOrDefault("model")?.Placement;
        Expect(onDisk?.Rect == rect, "placement written to settings.json");
        Expect(!mv.PausedForPopout, "no pop-out open: the docked view is not paused");
        var w3 = await OpenModel(b);
        Expect(w3.Chrome.WindowRect == rect && w3.Chrome.RestoredHow == "same monitor", $"placement restored: {w3.Chrome.WindowRect} ({w3.Chrome.RestoredHow})");
        w3.Close();
        await ScreenshotRunner.Settle(300);

        // no GL leak: 20 open / close cycles bring every count back
        await Cycles("model", 20, async () =>
        {
            var w = await OpenModel(b);
            int f = w.Viewport.Frames;
            w.Close();
            return f;
        });

        // the compare window: full screen too
        var cmp = await mv.MakeCompareAsync();
        if (cmp != null)
        {
            var cw = new ModelCompareWindow(cmp);
            PlaceOff(cw, 1180, 700);
            cw.Show();
            await ScreenshotRunner.Settle(900);
            Press(cw, Key.F11);
            Expect(cw.Chrome.IsFullScreen && cw.WindowStyle == WindowStyle.None, "compare window: F11 full screen");
            await ScreenshotRunner.Shot(cw, outDir, "62_compare_fullscreen");
            Press(cw, Key.Escape);
            Expect(!cw.Chrome.IsFullScreen && cw.IsVisible, "compare window: Esc leaves full screen (the window stays open)");
            cw.Close();
        }
        ScreenshotRunner.Place(win, 1440, 900);
    }

    /// <summary>Open and close a pop-out <paramref name="n"/> times: the GL contexts and objects must come back.</summary>
    private static async Task Cycles(string kind, int n, Func<Task<int>> openClose)
    {
        await ScreenshotRunner.Settle(500);
        int ctx0 = GlTracker.Contexts, obj0 = GlTracker.Objects, closed0 = GlTracker.ContextsClosed;
        var sw = Stopwatch.StartNew();
        int frames = 0;
        for (int i = 0; i < n; i++)
        {
            frames += await openClose();
            await ScreenshotRunner.Settle(60);
        }
        await ScreenshotRunner.Settle(500);
        ScreenshotRunner.Log($"{kind} pop-out: {n} open/close cycles in {sw.ElapsedMilliseconds} ms, {frames} frames drawn; before: {ctx0} contexts / {obj0} objects; after: {GlTracker.Summary}");
        Expect(GlTracker.Contexts == ctx0 && GlTracker.ContextsClosed - closed0 == n, $"{kind} pop-out: {n} open/close cycles leave no GL context behind ({GlTracker.Contexts} = {ctx0})");
        Expect(GlTracker.Objects == obj0 && frames >= n, $"{kind} pop-out: {n} open/close cycles leave no GL texture or buffer behind ({GlTracker.Objects} = {obj0})");
    }

    private static async Task<TerrainPopoutWindow> OpenTerrain(BrowserViewModel b)
    {
        var w = b.OpenTerrainPopoutWindow(x => PlaceOff(x, 1300, 860)) ?? throw new InvalidOperationException("the terrain pop-out did not open");
        await ScreenshotRunner.Until(() => !w.Vm.Loading && w.Vm.Scene.Asset != null, 120000);
        await ScreenshotRunner.Until(() => w.Viewport.Frames > 0, 20000);
        return w;
    }

    private static async Task TerrainSteps(MainWindow win, BrowserViewModel b, string outDir)
    {
        var m = b.Maps;
        var settings = b.Session.Settings;
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = BrowserViewModel.MapsTab;
        await m.EnsureLoadedAsync();
        if (m.SelectedMap == null || !m.SelectedMap.Directory.Equals(MapScreenshots.Map, StringComparison.OrdinalIgnoreCase))
        {
            m.SelectedMap = m.Maps.First(x => x.Directory.Equals(MapScreenshots.Map, StringComparison.OrdinalIgnoreCase));
            await ScreenshotRunner.Until(() => m.Zones.Count > 0, 120000);
        }
        m.IncludeNeighbours = false;
        m.SelectTile(MapScreenshots.TileX, MapScreenshots.TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && !m.PreviewLoading && m.Scene.Asset is { Tiles.Count: 1 }, 120000);
        await ScreenshotRunner.Settle(500);

        // open: the Maps tab's preview is shared (no second build), fly camera by default
        var t1 = await OpenTerrain(b);
        Expect(t1.Vm.Source == "shared with the Maps tab" && t1.Vm.OwnBuilds == 0 && ReferenceEquals(t1.Vm.Scene.Asset, m.Scene.Asset),
            $"terrain pop-out: takes the Maps tab's 3D preview ({t1.Vm.Source})");
        Expect(t1.Title.StartsWith($"{m.SelectedMap!.Name}, tile {MapScreenshots.TileX},{MapScreenshots.TileY} — ", StringComparison.Ordinal), $"terrain pop-out: title \"{t1.Title}\"");
        Expect(t1.Viewport.Navigation == CameraNavigation.Fly, "terrain pop-out: fly camera by default");
        Expect(Manual.GetTopic(t1) == "maps-and-terrain", "terrain pop-out: F1 opens the maps chapter");
        var cam = t1.Vm.Camera;
        Controls.ModelViewport.FlyStep(cam, new Vector3(1, 0, -0.3f), 1f, 2.0);
        cam.Look(-15f, 8f);
        t1.Vm.ShowHelp = true;
        await Render(t1.Viewport, outDir, "terrain_popout_fly");
        await ScreenshotRunner.Shot(t1, outDir, "60_terrain_popout");
        t1.Vm.ShowHelp = false;

        // neighbours in the pop-out only: it builds its own; full screen without controls
        t1.Vm.IncludeNeighbours = true;
        await ScreenshotRunner.Until(() => !t1.Vm.Loading && t1.Vm.Scene.Asset is { Tiles.Count: 9 }, 180000);
        Expect(t1.Vm.OwnBuilds == 1 && m.Scene.Asset is { Tiles.Count: 1 }, $"terrain pop-out: its own neighbour tiles, the Maps tab keeps one tile ({t1.Vm.Status})");
        FullScreenNoControls(t1);
        cam.Preset(CameraPreset.Reset);
        cam.Distance *= 2.2f;
        await ScreenshotRunner.Settle(800);
        await Render(t1.Viewport, outDir, "terrain_popout_neighbours");
        await ScreenshotRunner.Shot(t1, outDir, "61_terrain_popout_fullscreen");
        t1.Vm.ShowControls = true;
        t1.Chrome.SetFullScreen(false);

        // follow: another tile in the Maps tab; then pinned: it stays
        int nx = MapScreenshots.TileX + 1;
        m.SelectTile(nx, MapScreenshots.TileY);
        await ScreenshotRunner.Until(() => t1.Vm.TileX == nx && !t1.Vm.Loading && t1.Vm.Scene.Asset is { Tiles.Count: 9 } a && a.Tiles[0].TileX == nx, 180000);
        Expect(t1.Title.Contains($"tile {nx},{MapScreenshots.TileY}"), $"terrain pop-out: follows the selected tile (\"{t1.Title}\")");
        t1.Vm.FollowSelection = false;
        m.SelectTile(MapScreenshots.TileX, MapScreenshots.TileY);
        await ScreenshotRunner.Until(() => !m.IsTileLoading && !m.PreviewLoading && m.TileX == MapScreenshots.TileX, 120000);
        await ScreenshotRunner.Settle(500);
        Expect(t1.Vm.TileX == nx && t1.Title.EndsWith("(pinned)", StringComparison.Ordinal), "terrain pop-out: pinned, it keeps its tile");

        // placement per kind
        var rect = new PixelRect(Area.X + 200, Area.Y + 90, 1000, 640);
        t1.Chrome.MoveTo(rect);
        await ScreenshotRunner.Settle(200);
        t1.Close();
        await ScreenshotRunner.Settle(300);
        var saved = settings.Popouts.GetValueOrDefault("terrain");
        Expect(saved?.Placement?.Rect == rect && saved.Navigation == "Fly" && settings.Popouts.GetValueOrDefault("model")?.Placement?.Rect != rect,
            $"terrain placement saved separately from the model window's ({saved?.Placement?.Rect})");
        var t2 = await OpenTerrain(b);
        Expect(t2.Chrome.WindowRect == rect && t2.Chrome.RestoredHow == "same monitor", $"terrain placement restored: {t2.Chrome.WindowRect} ({t2.Chrome.RestoredHow})");
        t2.Close();

        await Cycles("terrain", 20, async () =>
        {
            var w = await OpenTerrain(b);
            int f = w.Viewport.Frames;
            w.Close();
            return f;
        });
        b.SelectedTab = 0;
    }

    private static void FullScreenNoControls(TerrainPopoutWindow t)
    {
        t.Chrome.SetFullScreen(true);
        t.Vm.ShowControls = false;
        Expect(t.Chrome.IsFullScreen && t.Chrome.WindowRect == Area, "terrain pop-out: full screen");
    }
}
