using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WoWData.Core.Workspace;
using WoWDataStudio.Controls;
using WoWDataStudio.Rendering;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>
/// 0.5.1, the pop-out 3D windows and the new navigation, the parts that need no window or GL: where a saved window comes
/// back (monitor gone, monitors rearranged, too big, off screen), the settings round trip, the camera presets, focus and
/// fly operations, the fly keys, and the model cache shared by the windows. The windows themselves (follow / pin, F11,
/// placement memory, the GL leak check over 20 open / close cycles) are checked by the screenshot run
/// (<see cref="UiSmokeTests"/>, Services\PopoutScreenshots.cs).
/// </summary>
public class PopoutTests
{
    private static readonly MonitorArea Primary = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1040), true, new PixelRect(0, 0, 1920, 1080));
    private static readonly MonitorArea Second = new(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1400), false, new PixelRect(1920, 0, 2560, 1440));

    private static WindowPlacementInfo Saved(int x, int y, int w, int h, MonitorArea? on, bool max = false) => new()
    {
        X = x, Y = y, Width = w, Height = h, Maximized = max, Monitor = on?.Name, MonitorX = on?.WorkArea.X ?? 0, MonitorY = on?.WorkArea.Y ?? 0,
    };

    [Fact]
    public void SavedWindowComesBackOnItsMonitorOrSafelyOnThePrimary()
    {
        var both = new List<MonitorArea> { Primary, Second };
        // the same monitor: exactly where it was, maximized kept
        var r = WindowPlacementInfo.Fit(Saved(2100, 100, 1400, 900, Second, max: true), both)!.Value;
        Assert.Equal(new PixelRect(2100, 100, 1400, 900), r.Rect);
        Assert.True(r.Maximized);
        Assert.Equal("same monitor", r.How);

        // the second monitor is unplugged: centred on the primary, made to fit
        r = WindowPlacementInfo.Fit(Saved(2100, 100, 2400, 1300, Second), new List<MonitorArea> { Primary })!.Value;
        Assert.Equal(new PixelRect(0, 0, 1920, 1040), r.Rect);
        Assert.Contains("is gone", r.How);
        r = WindowPlacementInfo.Fit(Saved(2100, 100, 1000, 600, Second), new List<MonitorArea> { Primary })!.Value;
        Assert.Equal(new PixelRect(460, 220, 1000, 600), r.Rect);

        // the second monitor moved to the left of the primary: the window moves with it
        var moved = Second with { WorkArea = new PixelRect(-2560, 0, 2560, 1400) };
        r = WindowPlacementInfo.Fit(Saved(2100, 100, 1400, 900, Second), new List<MonitorArea> { Primary, moved })!.Value;
        Assert.Equal(new PixelRect(-2380, 100, 1400, 900), r.Rect);
        Assert.Contains("moved in the layout", r.How);

        // partly off its monitor: pulled back in; bigger than the monitor: shrunk to it
        r = WindowPlacementInfo.Fit(Saved(1500, -50, 1000, 700, Primary), both)!.Value;
        Assert.Equal(new PixelRect(920, 0, 1000, 700), r.Rect);
        r = WindowPlacementInfo.Fit(Saved(0, 0, 5000, 3000, Primary), both)!.Value;
        Assert.Equal(new PixelRect(0, 0, 1920, 1040), r.Rect);

        // no monitor name (an old file), far off every screen: centred on the primary
        r = WindowPlacementInfo.Fit(Saved(-9000, -9000, 800, 600, null), both)!.Value;
        Assert.Equal(new PixelRect(560, 220, 800, 600), r.Rect);

        // nothing saved, or no monitors known: the window opens where WPF puts it
        Assert.Null(WindowPlacementInfo.Fit(null, both));
        Assert.Null(WindowPlacementInfo.Fit(Saved(0, 0, 800, 600, Primary), new List<MonitorArea>()));
    }

    [Fact]
    public void MonitorOfAWindowIsTheOneItOverlapsMostOrTheNearest()
    {
        var both = new List<MonitorArea> { Primary, Second };
        Assert.Equal(Second, WoWDataStudio.Services.PopoutChrome.MonitorOf(new PixelRect(1800, 0, 1000, 800), both));
        Assert.Equal(Primary, WoWDataStudio.Services.PopoutChrome.MonitorOf(new PixelRect(-5000, 100, 800, 600), both));
        Assert.Equal(Second, WoWDataStudio.Services.PopoutChrome.MonitorOf(new PixelRect(9000, 100, 800, 600), both));
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), Primary.FullArea);
        Assert.Equal(Primary.WorkArea, new MonitorArea("x", Primary.WorkArea, true).FullArea);
    }

    [Fact]
    public void PopoutSettingsArePersistedPerKind()
    {
        var dir = TestEnv.TempDir("popout");
        try
        {
            var paths = new AppPaths(dir);
            var s = new AppSettings();
            s.Popout("model").Placement = Saved(2100, 100, 1400, 900, Second, max: true);
            s.Popout("model").SidePanel = false;
            s.Popout("terrain").Navigation = "Fly";
            s.Popout("terrain").Placement = Saved(10, 20, 1000, 700, Primary);
            s.Save(paths);
            var back = AppSettings.Load(paths);
            Assert.Equal(new PixelRect(2100, 100, 1400, 900), back.Popout("model").Placement!.Rect);
            Assert.True(back.Popout("model").Placement!.Maximized);
            Assert.Equal(@"\\.\DISPLAY2", back.Popout("model").Placement!.Monitor);
            Assert.Equal(1920, back.Popout("model").Placement!.MonitorX);
            Assert.False(back.Popout("model").SidePanel);
            Assert.Equal("Fly", back.Popout("terrain").Navigation);
            Assert.Equal(new PixelRect(10, 20, 1000, 700), back.Popout("terrain").Placement!.Rect);
            Assert.True(back.Popout("other").SidePanel);      // a new kind gets the defaults
            Assert.Null(back.Popout("other").Placement);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool Near(Vector3 a, Vector3 b, float eps = 1e-3f) => Vector3.Distance(a, b) < eps;

    [Fact]
    public void CameraPresetsFocusAndFlyOperations()
    {
        var cam = new OrbitCamera();
        cam.Frame(new Vector3(-1, -1, 0), new Vector3(1, 1, 2));
        var home = cam.Home;
        Assert.Equal(25f, cam.Yaw);
        Assert.InRange(cam.FlySpeed, 1f, 2f);

        cam.Preset(CameraPreset.Front);
        Assert.Equal((0f, 0f), (cam.Yaw, cam.Pitch));
        Assert.True(cam.Eye.X > home.Target.X + 1, "Front looks at the model's front (+X)");
        cam.Preset(CameraPreset.Side);
        Assert.Equal(90f, cam.Yaw);
        Assert.True(cam.Eye.Y > home.Target.Y + 1, "Side looks at the model's left (+Y)");
        cam.Preset(CameraPreset.Top);
        Assert.Equal(89f, cam.Pitch);
        Assert.True(cam.Eye.Z > home.Target.Z + 1);
        cam.Preset(CameraPreset.Reset);
        Assert.Equal((25f, 12f, home.Distance), (cam.Yaw, cam.Pitch, cam.Distance));

        // terrain: Front = from the south (north is +X), Top = north up on the screen
        var t = new OrbitCamera { FrontYaw = 180f };
        t.Frame(Vector3.Zero, new Vector3(533, 533, 100));
        t.Preset(CameraPreset.Front);
        Assert.True(t.Eye.X < t.Target.X - 100, "terrain Front looks north from the south");
        t.Preset(CameraPreset.Top);
        var (_, up, _) = t.Basis();
        Assert.True(up.X > 0.9f, "terrain Top: north (+X) is up on the screen");

        // focus keeps the direction and frames the selection
        cam.Set(140f, -20f, 50f, new Vector3(30, 30, 30));
        cam.Focus();
        Assert.Equal((140f, -20f), (cam.Yaw, cam.Pitch));
        Assert.True(Near(cam.Target, home.Target) && Math.Abs(cam.Distance - home.Distance) < 1e-3f);

        // fly: the focus distance keeps the eye, looking around keeps the eye, moving moves both
        cam.ResetView();
        var eye = cam.Eye;
        cam.SetDistanceKeepEye(cam.FlyFocusDistance);
        Assert.True(Near(eye, cam.Eye) && cam.Distance < home.Distance);
        cam.Look(30f, -10f);
        Assert.True(Near(eye, cam.Eye));
        Assert.Equal((55f, 2f), (cam.Yaw, cam.Pitch));
        var tgt = cam.Target;
        cam.Move(new Vector3(1, 2, 3));
        Assert.True(Near(cam.Eye, eye + new Vector3(1, 2, 3)) && Near(cam.Target, tgt + new Vector3(1, 2, 3)));

        // the far plane covers the scene even with a short focus distance (fly mode over terrain)
        t.SetDistanceKeepEye(t.FlyFocusDistance);
        var p = t.Projection(1.5f);
        float near = p.M43 / (p.M33 - 1), far = p.M43 / (p.M33 + 1);
        Assert.True(near < 1f && far > 1500f, $"near {near}, far {far}");
    }

    [Fact]
    public void FlyKeysMoveTheCameraAtTheFlySpeed()
    {
        static bool None(Key _) => false;
        Assert.Equal((Vector3.Zero, 1f), ModelViewport.FlyInput(None, ModifierKeys.None));
        var held = new HashSet<Key> { Key.W, Key.D, Key.E };
        Assert.Equal((new Vector3(1, 1, 1), 4f), ModelViewport.FlyInput(held.Contains, ModifierKeys.Shift));
        held = new HashSet<Key> { Key.Down, Key.Left, Key.Q };
        Assert.Equal((new Vector3(-1, -1, -1), 0.25f), ModelViewport.FlyInput(held.Contains, ModifierKeys.Control));
        held = new HashSet<Key> { Key.W, Key.S };
        Assert.Equal(Vector3.Zero, ModelViewport.FlyInput(held.Contains, ModifierKeys.None).Axes);

        var cam = new OrbitCamera();
        cam.Frame(Vector3.Zero, new Vector3(533, 533, 100));
        cam.FlySpeed = 40f;
        var eye = cam.Eye;
        var fwd = cam.Forward;
        ModelViewport.FlyStep(cam, new Vector3(1, 0, 0), 1f, 0.5);
        Assert.True(Near(cam.Eye, eye + fwd * 20f, 0.01f), "W: 20 yards forward in half a second at 40 yd/s");
        eye = cam.Eye;
        ModelViewport.FlyStep(cam, new Vector3(0, 0, 1), 4f, 0.25);
        Assert.True(Near(cam.Eye, eye + Vector3.UnitZ * 40f, 0.01f), "E with Shift: straight up, four times faster");
        eye = cam.Eye;
        ModelViewport.FlyStep(cam, new Vector3(1, 1, 0), 1f, 1.0);
        Assert.InRange(Vector3.Distance(eye, cam.Eye), 39.9f, 40.1f);     // diagonal: not faster
        cam.FlySpeed = 1e9f;
        Assert.Equal(5000f, cam.FlySpeed);
    }

    [Fact]
    public void ModelCacheIsSharedAndKeepsTheMostRecent()
    {
        var cache = new ModelAssetCache { Capacity = 2 };
        ModelAsset A() => (ModelAsset)RuntimeHelpers.GetUninitializedObject(typeof(ModelAsset));
        var a = A();
        var b = A();
        var c = A();
        cache.Add(@"Creature\Boar\Boar.m2", a);
        cache.Add(@"Creature\Wolf\Wolf.m2", b);
        Assert.True(cache.TryGet(@"creature\boar\BOAR.M2", out var hit) && ReferenceEquals(hit, a));   // case-insensitive, and now the most recent
        cache.Add(@"Character\Orc\Male\OrcMale.m2", c);
        Assert.False(cache.TryGet(@"Creature\Wolf\Wolf.m2", out _));
        Assert.True(cache.TryGet(@"Creature\Boar\Boar.m2", out _));
        cache.Clear();
        Assert.False(cache.TryGet(@"Character\Orc\Male\OrcMale.m2", out _));
    }
}
