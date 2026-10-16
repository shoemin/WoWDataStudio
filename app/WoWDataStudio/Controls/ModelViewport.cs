using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenTK.Wpf;
using WoWData.Core.Imaging;
using WoWDataStudio.Rendering;

namespace WoWDataStudio.Controls;

/// <summary>
/// A 3D viewport for a <see cref="ModelScene"/> or a <see cref="TerrainScene"/>: OpenTK's GLWpfControl (OpenGL 3.3 core,
/// 4x MSAA, drawn into a WPF D3DImage, so WPF overlays and screenshots work) plus the camera controls.
/// <para>Orbit mode (the default): left drag = orbit, right or middle drag (or Shift + left) = pan, wheel = zoom,
/// double-click = reset view; arrows = orbit, Shift + arrows = pan, + / - (or Page Up / Page Down) = zoom.</para>
/// <para>Fly mode (<see cref="Navigation"/>, 0.5.1): W A S D or the arrows fly, Q / E down / up, Shift faster, Ctrl
/// slower, left or right drag = look around, middle drag = slide, wheel or + / - = fly speed.</para>
/// <para>Both: 1 / 2 / 3 = front / side / top view, F = focus on the selection, Home or R = start view, H = the help
/// overlay, Space = play / pause. Click the view first so it gets the keys (a pop-out window forwards them).</para>
/// The <see cref="Camera"/> can be shared between viewports (synchronized views). The viewport redraws only when the
/// scene or camera changes, every frame while an animation plays (unless <see cref="SuspendAnimation"/>), and while
/// flying. Every viewport has its own GL context; it is disposed (GL objects deleted, context destroyed) when its window
/// closes, see <see cref="GlTracker"/>.
/// </summary>
public sealed class ModelViewport : UserControl, IDisposable
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(ModelScene), typeof(ModelViewport),
        new PropertyMetadata(null, (d, e) => ((ModelViewport)d).OnSceneChanged((ModelScene?)e.OldValue, (ModelScene?)e.NewValue)));

    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(nameof(Camera), typeof(OrbitCamera), typeof(ModelViewport),
        new PropertyMetadata(null, (d, e) => ((ModelViewport)d).OnCameraChanged((OrbitCamera?)e.OldValue, (OrbitCamera?)e.NewValue)));

    /// <summary>Terrain mode (the Maps tab's 3D preview): when set, the viewport draws this terrain instead of a model.</summary>
    public static readonly DependencyProperty TerrainProperty = DependencyProperty.Register(nameof(Terrain), typeof(TerrainScene), typeof(ModelViewport),
        new PropertyMetadata(null, (d, e) => ((ModelViewport)d).OnTerrainChanged((TerrainScene?)e.OldValue, (TerrainScene?)e.NewValue)));

    /// <summary>Whether this viewport advances the scene's clock (false for a second view of the same scene).</summary>
    public static readonly DependencyProperty DrivesClockProperty = DependencyProperty.Register(nameof(DrivesClock), typeof(bool), typeof(ModelViewport), new PropertyMetadata(true));

    /// <summary>Orbit or fly (two-way: the camera bar's Orbit / Fly buttons bind to it).</summary>
    public static readonly DependencyProperty NavigationProperty = DependencyProperty.Register(nameof(Navigation), typeof(CameraNavigation), typeof(ModelViewport),
        new FrameworkPropertyMetadata(CameraNavigation.Orbit, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ModelViewport)d).OnNavigationChanged()));

    /// <summary>The help overlay with the controls (H).</summary>
    public static readonly DependencyProperty ShowHelpProperty = DependencyProperty.Register(nameof(ShowHelp), typeof(bool), typeof(ModelViewport),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ModelViewport)d).UpdateOverlays()));

    /// <summary>Do not redraw every frame for the animation (the docked view while a pop-out window follows the
    /// selection: the pop-out plays it, this view redraws only when something changes).</summary>
    public static readonly DependencyProperty SuspendAnimationProperty = DependencyProperty.Register(nameof(SuspendAnimation), typeof(bool), typeof(ModelViewport),
        new PropertyMetadata(false, (d, e) => ((ModelViewport)d).Invalidate()));

    /// <summary>The help overlay also lists the pop-out window's keys (F11, Esc, Ctrl+H, F1).</summary>
    public static readonly DependencyProperty WindowKeysProperty = DependencyProperty.Register(nameof(WindowKeys), typeof(bool), typeof(ModelViewport),
        new PropertyMetadata(false, (d, e) => ((ModelViewport)d).UpdateOverlays()));

    public ModelScene? Scene { get => (ModelScene?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public TerrainScene? Terrain { get => (TerrainScene?)GetValue(TerrainProperty); set => SetValue(TerrainProperty, value); }
    public OrbitCamera? Camera { get => (OrbitCamera?)GetValue(CameraProperty); set => SetValue(CameraProperty, value); }
    public bool DrivesClock { get => (bool)GetValue(DrivesClockProperty); set => SetValue(DrivesClockProperty, value); }
    public CameraNavigation Navigation { get => (CameraNavigation)GetValue(NavigationProperty); set => SetValue(NavigationProperty, value); }
    public bool ShowHelp { get => (bool)GetValue(ShowHelpProperty); set => SetValue(ShowHelpProperty, value); }
    public bool SuspendAnimation { get => (bool)GetValue(SuspendAnimationProperty); set => SetValue(SuspendAnimationProperty, value); }
    public bool WindowKeys { get => (bool)GetValue(WindowKeysProperty); set => SetValue(WindowKeysProperty, value); }

    /// <summary>Raised after every frame (the view model updates its timeline from the scene clock).</summary>
    public event EventHandler? Rendered;

    private readonly GLWpfControl _gl;
    private readonly TextBlock _error;
    private readonly Border _help;
    private readonly StackPanel _helpRows;
    private readonly Border _flyBadge;
    private readonly TextBlock _flyText;
    private readonly ModelRenderer _renderer = new();
    private readonly TerrainRenderer _terrainRenderer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private bool _animating;
    private Point _down;
    private MouseButton? _drag;
    private readonly List<(int W, int H, TaskCompletionSource<RgbaImage> Done)> _captures = new();
    private bool _failed, _started, _disposed;
    private Window? _host;

    public string GlInfo => _renderer.GlInfo;
    /// <summary>Frames drawn so far and the CPU time of the last one (bone matrices + draw calls), for the statistics.</summary>
    public int Frames { get; private set; }
    public double LastFrameMs { get; private set; }
    public bool IsDisposed => _disposed;
    public static GLWpfControlSettings Settings => new()
    {
        MajorVersion = 3, MinorVersion = 3, Profile = OpenTK.Windowing.Common.ContextProfile.Core, Samples = 4, RenderContinuously = false, UseDeviceDpi = true,
    };

    public ModelViewport()
    {
        Focusable = true;
        FocusVisualStyle = null;
        _gl = new GLWpfControl();
        _gl.Render += OnRender;
        _error = new TextBlock
        {
            Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(200, 120, 20, 20)), Padding = new Thickness(10), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom,
        };
        _helpRows = new StackPanel();
        _help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 16, 20, 24)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Visibility = Visibility.Collapsed, Child = _helpRows,
        };
        _flyText = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
        _flyBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 16, 20, 24)), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Visibility = Visibility.Collapsed, Child = _flyText,
        };
        var grid = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        grid.Children.Add(_gl);
        grid.Children.Add(_error);
        grid.Children.Add(_flyBadge);
        grid.Children.Add(_help);
        Content = grid;
        try
        {
            _gl.Start(Settings);
            _started = true;
            GlTracker.ContextOpened();
        }
        catch (Exception e)
        {
            ShowError("3D view not available: OpenGL 3.3 could not be started (" + e.Message + "). Update the graphics driver.");
        }
        Unloaded += (_, _) => { StopAnimating(); StopFlying(); };
        Loaded += (_, _) =>
        {
            AttachToWindow();
            Invalidate();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Invalidate(); else { StopAnimating(); StopFlying(); } };
        UpdateOverlays();
    }

    /// <summary>Dispose with the window: the GL objects and the context of this view go away when its window closes
    /// (pop-out and compare windows are opened and closed many times).</summary>
    private void AttachToWindow()
    {
        var w = Window.GetWindow(this);
        if (ReferenceEquals(w, _host)) return;
        if (_host != null) _host.Closed -= OnHostClosed;
        _host = w;
        if (_host != null) _host.Closed += OnHostClosed;
    }

    private void OnHostClosed(object? sender, EventArgs e) => Dispose();

    private void ShowError(string text)
    {
        _failed = true;
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }

    private void OnSceneChanged(ModelScene? old, ModelScene? now)
    {
        if (old != null) old.Changed -= OnInvalidated;
        if (now != null) now.Changed += OnInvalidated;
        Invalidate();
    }

    private void OnTerrainChanged(TerrainScene? old, TerrainScene? now)
    {
        if (old != null) old.Changed -= OnInvalidated;
        if (now != null) now.Changed += OnInvalidated;
        UpdateOverlays();
        Invalidate();
    }

    private void OnCameraChanged(OrbitCamera? old, OrbitCamera? now)
    {
        if (old != null) old.Changed -= OnCameraMoved;
        if (now != null) now.Changed += OnCameraMoved;
        if (now != null && Navigation == CameraNavigation.Fly) now.SetDistanceKeepEye(now.FlyFocusDistance);
        Invalidate();
    }

    private void OnCameraMoved(object? sender, EventArgs e)
    {
        // in fly mode the target stays a short focus distance in front of the eye, whatever moved the camera (a preset, the
        // start view, a new model or tile framed by the view model)
        var cam = Camera;
        if (cam != null && Navigation == CameraNavigation.Fly && cam.Distance > cam.FlyFocusDistance * 1.5f)
        {
            cam.SetDistanceKeepEye(cam.FlyFocusDistance);
            return;     // raised Changed again
        }
        UpdateFlyBadge();
        Invalidate();
    }

    private void OnNavigationChanged()
    {
        var cam = Camera;
        if (cam != null && Navigation == CameraNavigation.Fly) cam.SetDistanceKeepEye(cam.FlyFocusDistance);
        if (Navigation == CameraNavigation.Orbit) StopFlying();
        UpdateOverlays();
        Invalidate();
    }

    private ModelAsset? _lastAsset;

    private void OnInvalidated(object? sender, EventArgs e)
    {
        if (sender is ModelScene s && !ReferenceEquals(s.Asset, _lastAsset))
        {
            if (_lastAsset != null) _pendingCollect = true;     // free the GL textures of the previous model's skins
            _lastAsset = s.Asset;
        }
        Invalidate();
    }

    /// <summary>Request a redraw (and keep redrawing while something animates).</summary>
    public void Invalidate()
    {
        if (_failed || _disposed) return;
        _gl.InvalidateVisual();
        bool anim = IsVisible && !SuspendAnimation && Scene?.NeedsContinuousRedraw == true && Terrain == null;
        if (anim && !_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!anim) StopAnimating();
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Scene?.NeedsContinuousRedraw != true || !IsVisible || SuspendAnimation || _disposed)
        {
            StopAnimating();
            return;
        }
        _gl.InvalidateVisual();
    }

    private void OnRender(TimeSpan _)
    {
        if (_failed || _disposed) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _last, 0, 0.25);
        _last = now;
        var scene = Scene;
        var cam = Camera;
        try
        {
            if (_pendingCollect)
            {
                _pendingCollect = false;
                _renderer.CollectTextures();
            }
            var terrain = Terrain;
            if (terrain != null && cam != null)
            {
                if (_captures.Count > 0)
                {
                    var list = _captures.ToList();
                    _captures.Clear();
                    foreach (var (w, h, done) in list)
                    {
                        try { done.TrySetResult(_terrainRenderer.RenderToImage(terrain, cam, w, h, _gl.Framebuffer)); }
                        catch (Exception e) { done.TrySetException(e); }
                    }
                }
                var t0 = _clock.Elapsed.TotalMilliseconds;
                _terrainRenderer.Render(terrain, cam, _gl.FrameBufferWidth, _gl.FrameBufferHeight);
                LastFrameMs = _clock.Elapsed.TotalMilliseconds - t0;
                Frames++;
            }
            else
            {
                if (scene != null && DrivesClock) scene.Advance(dt);
                if (scene != null && cam != null)
                {
                    if (_captures.Count > 0)
                    {
                        var list = _captures.ToList();
                        _captures.Clear();
                        foreach (var (w, h, done) in list)
                        {
                            try { done.TrySetResult(_renderer.RenderToImage(scene, cam, w, h, _gl.Framebuffer)); }
                            catch (Exception e) { done.TrySetException(e); }
                        }
                    }
                    var t0 = _clock.Elapsed.TotalMilliseconds;
                    _renderer.Render(scene, cam, _gl.FrameBufferWidth, _gl.FrameBufferHeight);
                    LastFrameMs = _clock.Elapsed.TotalMilliseconds - t0;
                    Frames++;
                }
                else
                {
                    OpenTK.Graphics.OpenGL4.GL.ClearColor(0.17f, 0.19f, 0.22f, 1f);
                    OpenTK.Graphics.OpenGL4.GL.Clear(OpenTK.Graphics.OpenGL4.ClearBufferMask.ColorBufferBit | OpenTK.Graphics.OpenGL4.ClearBufferMask.DepthBufferBit);
                }
            }
        }
        catch (Exception e)
        {
            ShowError("3D view error: " + e.Message);
            foreach (var c in _captures) c.Done.TrySetException(e);
            _captures.Clear();
        }
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Render the current view offscreen at the given size (default: the viewport's size in pixels).</summary>
    public Task<RgbaImage> CaptureAsync(int width = 0, int height = 0)
    {
        var tcs = new TaskCompletionSource<RgbaImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_failed || _disposed)
        {
            tcs.SetException(new InvalidOperationException(_disposed ? "the 3D view was closed" : _error.Text));
            return tcs.Task;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        if (width <= 0) width = (int)Math.Round(Math.Max(64, ActualWidth) * dpi.DpiScaleX);
        if (height <= 0) height = (int)Math.Round(Math.Max(64, ActualHeight) * dpi.DpiScaleY);
        _captures.Add((width, height, tcs));
        _gl.InvalidateVisual();
        return tcs.Task;
    }

    /// <summary>Releases GL objects that are no longer used (call after switching models).</summary>
    public void CollectTextures() => _pendingCollect = true;
    private bool _pendingCollect;

    // ---------------------------------------------------------------- camera commands (camera bar, keys)

    /// <summary>Front / Side / Top / the start view of the selection.</summary>
    public void ApplyPreset(CameraPreset p) => Camera?.Preset(p);

    /// <summary>"Focus on selection" (F): the framed model or tile, seen from the current direction.</summary>
    public void FocusSelection() => Camera?.Focus();

    // ---------------------------------------------------------------- overlays

    private static readonly (string Keys, string What)[] OrbitHelp =
    {
        ("Left-drag", "turn around"), ("Right- / middle-drag", "move (pan)"), ("Wheel, + / -", "zoom"),
        ("Arrow keys", "turn (Shift: move)"),
    };

    private static readonly (string Keys, string What)[] FlyHelp =
    {
        ("W A S D, arrows", "fly forward / left / back / right"), ("Q / E", "down / up"), ("Shift / Ctrl", "faster / slower"),
        ("Right- or left-drag", "look around"), ("Middle-drag", "slide"), ("Wheel, + / -", "fly speed"),
    };

    private static readonly (string Keys, string What)[] CommonHelp =
    {
        ("1 / 2 / 3", "front / side / top view"), ("F", "focus on the selection"), ("Home, R, double-click", "start view"),
    };

    private static readonly (string Keys, string What)[] WindowHelp =
    {
        ("F11", "full screen (Esc leaves it)"), ("Ctrl+H", "hide / show the controls"), ("F1", "the manual"),
    };

    /// <summary>The help overlay's text (screenshots and tests read it).</summary>
    public string HelpText => string.Join("\n", HelpLines().Select(l => $"{l.Keys}: {l.What}"));

    private IEnumerable<(string Keys, string What)> HelpLines()
    {
        var lines = (Navigation == CameraNavigation.Fly ? FlyHelp : OrbitHelp).Concat(CommonHelp);
        if (Scene != null && Terrain == null) lines = lines.Append(("Space", "play / pause the animation"));
        lines = lines.Append(("H", "show / hide this help"));
        if (WindowKeys) lines = lines.Concat(WindowHelp);
        return lines;
    }

    private void UpdateOverlays()
    {
        _help.Visibility = ShowHelp ? Visibility.Visible : Visibility.Collapsed;
        if (ShowHelp)
        {
            _helpRows.Children.Clear();
            _helpRows.Children.Add(new TextBlock
            {
                Text = Navigation == CameraNavigation.Fly ? "Fly camera" : "Orbit camera", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
            });
            foreach (var (keys, what) in HelpLines())
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var k = new TextBlock { Text = keys, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x7A)), Width = 150, FontSize = 12 };
                DockPanel.SetDock(k, Dock.Left);
                row.Children.Add(k);
                row.Children.Add(new TextBlock { Text = what, Foreground = Brushes.White, FontSize = 12 });
                _helpRows.Children.Add(row);
            }
        }
        UpdateFlyBadge();
    }

    private void UpdateFlyBadge()
    {
        bool fly = Navigation == CameraNavigation.Fly;
        _flyBadge.Visibility = fly ? Visibility.Visible : Visibility.Collapsed;
        if (fly && Camera is { } cam) _flyText.Text = $"Fly · {FormatSpeed(cam.FlySpeed)} · H: help";
    }

    private static string FormatSpeed(float s) => s >= 10 ? $"{s:0} yd/s" : $"{s:0.#} yd/s";

    // ---------------------------------------------------------------- input

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            Camera?.ResetView();
            e.Handled = true;
            return;
        }
        _down = e.GetPosition(this);
        _drag = e.ChangedButton;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == e.ChangedButton)
        {
            _drag = null;
            ReleaseMouseCapture();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cam = Camera;
        if (_drag == null || cam == null) return;
        var p = e.GetPosition(this);
        var d = p - _down;
        _down = p;
        double h = Math.Max(1, ActualHeight);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Navigation == CameraNavigation.Fly)
        {
            if (_drag == MouseButton.Middle || (_drag == MouseButton.Left && shift)) cam.Pan((float)(d.X / h), (float)(d.Y / h));
            else cam.Look((float)(-d.X * 0.25), (float)(d.Y * 0.25));
            return;
        }
        bool pan = _drag is MouseButton.Right or MouseButton.Middle || shift;
        if (pan) cam.Pan((float)(d.X / h), (float)(d.Y / h));
        else cam.Orbit((float)(d.X * 0.45), (float)(d.Y * 0.45));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Navigation == CameraNavigation.Fly)
        {
            if (Camera is { } cam) cam.FlySpeed *= MathF.Pow(1.25f, e.Delta / 120f);
        }
        else Camera?.Zoom(MathF.Pow(0.88f, e.Delta / 120f));
        e.Handled = true;      // do not scroll the details panel
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled) HandleKey(e);
    }

    private static readonly Key[] FlyKeys = { Key.W, Key.A, Key.S, Key.D, Key.Q, Key.E, Key.Up, Key.Down, Key.Left, Key.Right, Key.PageUp, Key.PageDown };

    /// <summary>The view's keys (a pop-out window forwards the keys typed while another of its controls has the focus).</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var cam = Camera;
        if (cam == null) return false;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt) || (mods.HasFlag(ModifierKeys.Control) && Navigation == CameraNavigation.Orbit)) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.D1: case Key.NumPad1: cam.Preset(CameraPreset.Front); break;
            case Key.D2: case Key.NumPad2: cam.Preset(CameraPreset.Side); break;
            case Key.D3: case Key.NumPad3: cam.Preset(CameraPreset.Top); break;
            case Key.F: cam.Focus(); break;
            case Key.H: ShowHelp = !ShowHelp; break;
            case Key.Home: case Key.R: cam.ResetView(); break;
            case Key.Space:
                if (Scene != null && Terrain == null) Scene.Playing = !Scene.Playing;
                else return false;
                break;
            default:
                if (Navigation == CameraNavigation.Fly)
                {
                    if (key is Key.Add or Key.OemPlus) cam.FlySpeed *= 1.25f;
                    else if (key is Key.Subtract or Key.OemMinus) cam.FlySpeed /= 1.25f;
                    else if (FlyKeys.Contains(key)) StartFlying();
                    else return false;
                    break;
                }
                switch (key)
                {
                    case Key.Left: if (shift) cam.Pan(-0.05f, 0); else cam.Orbit(-10, 0); break;
                    case Key.Right: if (shift) cam.Pan(0.05f, 0); else cam.Orbit(10, 0); break;
                    case Key.Up: if (shift) cam.Pan(0, -0.05f); else cam.Orbit(0, 6); break;
                    case Key.Down: if (shift) cam.Pan(0, 0.05f); else cam.Orbit(0, -6); break;
                    case Key.Add: case Key.OemPlus: case Key.PageUp: cam.Zoom(0.85f); break;
                    case Key.Subtract: case Key.OemMinus: case Key.PageDown: cam.Zoom(1 / 0.85f); break;
                    default: return false;
                }
                break;
        }
        e.Handled = true;
        return true;
    }

    // ---------------------------------------------------------------- fly loop (while a movement key is held)

    private bool _flying;
    private double _flyLast;

    private void StartFlying()
    {
        if (_flying || _disposed) return;
        _flying = true;
        _flyLast = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFlyFrame;
    }

    private void StopFlying()
    {
        if (!_flying) return;
        _flying = false;
        CompositionTarget.Rendering -= OnFlyFrame;
    }

    /// <summary>The movement for the keys held now: (forward, right, up) in -1..1, and the speed factor.</summary>
    public static (Vector3 Axes, float Factor) FlyInput(Func<Key, bool> down, ModifierKeys mods)
    {
        float f = (down(Key.W) || down(Key.Up) ? 1 : 0) - (down(Key.S) || down(Key.Down) ? 1 : 0);
        float r = (down(Key.D) || down(Key.Right) ? 1 : 0) - (down(Key.A) || down(Key.Left) ? 1 : 0);
        float u = (down(Key.E) || down(Key.PageUp) ? 1 : 0) - (down(Key.Q) || down(Key.PageDown) ? 1 : 0);
        float factor = mods.HasFlag(ModifierKeys.Shift) ? 4f : mods.HasFlag(ModifierKeys.Control) ? 0.25f : 1f;
        return (new Vector3(f, r, u), factor);
    }

    /// <summary>One step of the fly camera: <paramref name="axes"/> (forward, right, up) at speed * factor yards/s.</summary>
    public static void FlyStep(OrbitCamera cam, Vector3 axes, float factor, double seconds)
    {
        if (axes == Vector3.Zero || seconds <= 0) return;
        var (right, _, back) = cam.Basis();
        var dir = -back * axes.X + right * axes.Y + Vector3.UnitZ * axes.Z;
        if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
        cam.Move(dir * (float)(cam.FlySpeed * factor * seconds));
    }

    private void OnFlyFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _flyLast, 0, 0.1);
        _flyLast = now;
        var cam = Camera;
        var win = _host;
        if (cam == null || _disposed || Navigation != CameraNavigation.Fly || !IsVisible || (win != null && !win.IsActive))
        {
            StopFlying();
            return;
        }
        var (axes, factor) = FlyInput(Keyboard.IsKeyDown, Keyboard.Modifiers);
        if (axes == Vector3.Zero)
        {
            StopFlying();
            return;
        }
        FlyStep(cam, axes, factor, dt);
    }

    // ---------------------------------------------------------------- disposal

    /// <summary>Raised once the view's GL objects and context are gone.</summary>
    public event EventHandler? Disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAnimating();
        StopFlying();
        if (_host != null) _host.Closed -= OnHostClosed;
        _host = null;
        Scene = null;
        Terrain = null;
        Camera = null;
        foreach (var c in _captures) c.Done.TrySetException(new InvalidOperationException("the 3D view was closed"));
        _captures.Clear();
        if (_started)
        {
            try
            {
                // delete this view's GL objects in its own context, then the context itself (GLFW window + D3D device)
                _gl.Context?.MakeCurrent();
                _renderer.Dispose();
                _terrainRenderer.Dispose();
            }
            catch (Exception) { /* a lost device: the context goes away below anyway */ }
            try { _gl.Dispose(); }
            catch (Exception) { }
            GlTracker.ContextClosed();
        }
        Disposed?.Invoke(this, EventArgs.Empty);
    }
}
