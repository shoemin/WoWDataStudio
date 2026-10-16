using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using WoWData.Core.Workspace;
using WoWDataStudio.Controls;

namespace WoWDataStudio.Services;

/// <summary>
/// What every window with a big 3D view gets (the pop-out windows; the compare windows without the memory):
/// <list type="bullet">
/// <item>full screen with F11 (no frame, covers the taskbar; Esc or F11 leaves it);</item>
/// <item>the keys of the 3D view work while another control of the window has the focus (W A S D, H, F, 1 2 3...);</item>
/// <item>with a <c>kind</c>: the window's size, position, monitor and maximized state are restored when it opens and saved
/// when it closes (<see cref="WindowPlacementInfo"/> in settings.json); a monitor that is gone moves the window to the
/// primary monitor, and the window always fits on screen.</item>
/// </list>
/// Positions are physical pixels (Win32), so windows on monitors with different scaling come back exactly.
/// </summary>
public sealed class PopoutChrome
{
    private readonly Window _w;
    private readonly Func<ModelViewport?> _viewport;
    private readonly PopoutSettings? _settings;
    private readonly Action? _save;
    private PixelRect? _normal;
    private WindowStyle _prevStyle;
    private ResizeMode _prevResize;
    private WindowState _prevState;

    public bool IsFullScreen { get; private set; }
    public event Action? FullScreenChanged;
    /// <summary>Ctrl+H (the window shows or hides its controls).</summary>
    public event Action? ToggleControlsRequested;
    /// <summary>How the placement was restored (for the log and the tests), e.g. "same monitor".</summary>
    public string? RestoredHow { get; private set; }

    public PopoutChrome(Window w, Func<ModelViewport?> viewport, PopoutSettings? settings = null, Action? save = null)
    {
        _w = w;
        _viewport = viewport;
        _settings = settings;
        _save = save;
        if (settings?.Placement != null) w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SourceInitialized += (_, _) => Restore();
        w.LocationChanged += (_, _) => TrackNormal();
        w.SizeChanged += (_, _) => TrackNormal();
        w.StateChanged += (_, _) => TrackNormal();
        w.Closing += (_, _) => Persist();
        w.PreviewKeyDown += OnPreviewKeyDown;
        w.KeyDown += OnKeyDown;
    }

    // ---------------------------------------------------------------- keys

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (key == Key.Escape && IsFullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
        else if (key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleControlsRequested?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>A key nobody used (focus on a check box, a button...) goes to the 3D view; typing into a text box,
    /// a drop-down or a slider does not.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var vp = _viewport();
        if (vp == null || vp.IsDisposed || e.OriginalSource is ModelViewport) return;
        if (e.OriginalSource is TextBoxBase or ComboBox or ComboBoxItem or Slider or PasswordBox) return;
        if (e.OriginalSource is ButtonBase && e.Key is Key.Space or Key.Enter) return;
        vp.HandleKey(e);
    }

    // ---------------------------------------------------------------- full screen

    public void ToggleFullScreen() => SetFullScreen(!IsFullScreen);

    /// <summary>The monitor area full screen covers now (physical pixels).</summary>
    public PixelRect? FullScreenArea { get; private set; }

    /// <summary>Full screen = no frame, no resize border, the window covering its whole monitor (taskbar included).
    /// Leaving it puts back the frame, the size and position, and the maximized state.</summary>
    public void SetFullScreen(bool on)
    {
        if (on == IsFullScreen || Handle == IntPtr.Zero) return;
        if (on)
        {
            TrackNormal();
            var now = WindowRect;
            var mon = now is { } r ? MonitorOf(r, AllMonitors()) : null;
            if (mon == null) return;
            _prevStyle = _w.WindowStyle;
            _prevResize = _w.ResizeMode;
            _prevState = _w.WindowState;
            IsFullScreen = true;
            FullScreenArea = mon.FullArea;
            if (_w.WindowState != WindowState.Normal) _w.WindowState = WindowState.Normal;
            _w.WindowStyle = WindowStyle.None;
            _w.ResizeMode = ResizeMode.NoResize;
            var a = mon.FullArea;
            SetWindowPos(Handle, IntPtr.Zero, a.X, a.Y, a.Width, a.Height, SWP_NOACTIVATE);
        }
        else
        {
            _w.WindowStyle = _prevStyle;
            _w.ResizeMode = _prevResize;
            IsFullScreen = false;
            FullScreenArea = null;
            if (_normal is { } n) SetWindowPos(Handle, IntPtr.Zero, n.X, n.Y, n.Width, n.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            if (_prevState == WindowState.Maximized) _w.WindowState = WindowState.Maximized;
        }
        FullScreenChanged?.Invoke();
        _viewport()?.Focus();
    }

    // ---------------------------------------------------------------- placement

    private IntPtr Handle => new WindowInteropHelper(_w).Handle;

    private void TrackNormal()
    {
        if (IsFullScreen || _w.WindowState != WindowState.Normal || Handle == IntPtr.Zero) return;
        if (GetWindowRect(Handle, out var r) && r.Right > r.Left && r.Bottom > r.Top) _normal = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private void Restore()
    {
        var saved = _settings?.Placement;
        var fit = WindowPlacementInfo.Fit(saved, AllMonitors());
        if (fit is { } f)
        {
            const uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
            // twice: the first move to a monitor with another scaling makes WPF rescale the window (WM_DPICHANGED)
            SetWindowPos(Handle, IntPtr.Zero, f.Rect.X, f.Rect.Y, f.Rect.Width, f.Rect.Height, flags);
            SetWindowPos(Handle, IntPtr.Zero, f.Rect.X, f.Rect.Y, f.Rect.Width, f.Rect.Height, flags);
            _normal = f.Rect;
            RestoredHow = f.How;
            if (f.Maximized) _w.WindowState = WindowState.Maximized;
        }
        TrackNormal();
    }

    /// <summary>The placement to remember now (null before the window has a handle).</summary>
    public WindowPlacementInfo? Current()
    {
        TrackNormal();
        if (_normal is not { } n) return null;
        bool max = IsFullScreen ? _prevState == WindowState.Maximized : _w.WindowState == WindowState.Maximized;
        var info = new WindowPlacementInfo { X = n.X, Y = n.Y, Width = n.Width, Height = n.Height, Maximized = max };
        var mon = MonitorOf(n, AllMonitors());
        if (mon != null)
        {
            info.Monitor = mon.Name;
            info.MonitorX = mon.WorkArea.X;
            info.MonitorY = mon.WorkArea.Y;
        }
        return info;
    }

    private void Persist()
    {
        if (_settings == null) return;
        var p = Current();
        if (p == null) return;
        _settings.Placement = p;
        _save?.Invoke();
    }

    /// <summary>The monitor a window rectangle is on: the one it overlaps most, else the nearest.</summary>
    public static MonitorArea? MonitorOf(PixelRect r, IReadOnlyList<MonitorArea> monitors)
    {
        if (monitors.Count == 0) return null;
        var best = monitors.OrderByDescending(m => m.WorkArea.OverlapArea(r)).First();
        if (best.WorkArea.OverlapArea(r) > 0) return best;
        long Dist(MonitorArea m)
        {
            long dx = Math.Max(0, Math.Max(m.WorkArea.X - r.Right, r.X - m.WorkArea.Right));
            long dy = Math.Max(0, Math.Max(m.WorkArea.Y - r.Bottom, r.Y - m.WorkArea.Bottom));
            return dx * dx + dy * dy;
        }
        return monitors.OrderBy(Dist).First();
    }

    /// <summary>The screenshot run adds an invisible "monitor" far left of the desktop, so its windows (which are drawn
    /// off screen) can be saved and restored without ever appearing on the user's screen.</summary>
    internal static Func<List<MonitorArea>>? MonitorsOverride { get; set; }

    private static List<MonitorArea> AllMonitors() => MonitorsOverride?.Invoke() ?? Monitors();

    /// <summary>The monitors now: device name, work area (physical pixels), primary.</summary>
    public static List<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref mi))
                list.Add(new MonitorArea(mi.szDevice, new PixelRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top), (mi.dwFlags & 1) != 0,
                    new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window's rectangle now, in physical pixels (tests).</summary>
    public PixelRect? WindowRect => Handle != IntPtr.Zero && GetWindowRect(Handle, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    /// <summary>Move and size the window in physical pixels (tests and screenshots).</summary>
    public void MoveTo(PixelRect r)
    {
        if (Handle == IntPtr.Zero) return;
        _w.WindowState = WindowState.Normal;
        SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        TrackNormal();
    }

    // ---------------------------------------------------------------- Win32

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
}
