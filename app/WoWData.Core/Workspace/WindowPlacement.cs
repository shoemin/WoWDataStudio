namespace WoWData.Core.Workspace;

/// <summary>A rectangle in physical screen pixels (left, top, width, height).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public int OverlapArea(PixelRect o)
    {
        int w = Math.Min(Right, o.Right) - Math.Max(X, o.X), h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>One monitor: its device name (\\.\DISPLAY2), its work area (without the taskbar), whether it is the primary,
/// and its whole area (with the taskbar: what full screen covers; null = the work area).</summary>
public sealed record MonitorArea(string Name, PixelRect WorkArea, bool Primary, PixelRect? Bounds = null)
{
    public PixelRect FullArea => Bounds ?? WorkArea;
}

/// <summary>Where a window was when it was closed: its normal (not maximized) rectangle in physical pixels, whether it was
/// maximized, and the monitor it was on with that monitor's work area at the time (so a rearranged monitor layout keeps
/// the window at the same place on the same monitor).</summary>
public sealed class WindowPlacementInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public string? Monitor { get; set; }
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }

    public PixelRect Rect => new(X, Y, Width, Height);

    /// <summary>
    /// Where to put a window saved as <paramref name="saved"/> on the monitors there are now:
    /// * its monitor still exists: the same place on it (moved with the monitor when the layout was rearranged);
    /// * its monitor is gone (unplugged, or the name changed): centred on the primary monitor;
    /// * in every case the window fits the work area (made smaller if needed) and is fully on screen.
    /// Returns null when there is nothing saved or no monitor is known (the window then opens where WPF puts it).
    /// </summary>
    public static (PixelRect Rect, bool Maximized, string How)? Fit(WindowPlacementInfo? saved, IReadOnlyList<MonitorArea> monitors, int minWidth = 320, int minHeight = 240)
    {
        if (saved == null || monitors.Count == 0 || saved.Width <= 0 || saved.Height <= 0) return null;
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        var r = saved.Rect;
        MonitorArea? target = saved.Monitor == null ? null : monitors.FirstOrDefault(m => string.Equals(m.Name, saved.Monitor, StringComparison.OrdinalIgnoreCase));
        string how;
        if (target != null)
        {
            // the same monitor: keep the offset from its work area (it may have moved in the layout)
            int dx = target.WorkArea.X - saved.MonitorX, dy = target.WorkArea.Y - saved.MonitorY;
            r = r with { X = r.X + dx, Y = r.Y + dy };
            how = dx == 0 && dy == 0 ? "same monitor" : "same monitor (moved in the layout)";
        }
        else
        {
            // no name, or the monitor is gone: the monitor the rectangle overlaps most, else the primary one (centred)
            target = monitors.OrderByDescending(m => m.WorkArea.OverlapArea(r)).First();
            if (target.WorkArea.OverlapArea(r) == 0 || saved.Monitor != null)
            {
                target = primary;
                var wa = target.WorkArea;
                int w0 = Math.Min(r.Width, wa.Width), h0 = Math.Min(r.Height, wa.Height);
                r = new PixelRect(wa.X + (wa.Width - w0) / 2, wa.Y + (wa.Height - h0) / 2, w0, h0);
                how = saved.Monitor != null ? $"monitor {saved.Monitor} is gone: centred on the primary monitor" : "off screen: centred on the primary monitor";
            }
            else how = "overlapping monitor";
        }
        var a = target.WorkArea;
        int w = Math.Clamp(r.Width, Math.Min(minWidth, a.Width), a.Width);
        int h = Math.Clamp(r.Height, Math.Min(minHeight, a.Height), a.Height);
        int x = Math.Clamp(r.X, a.X, a.Right - w);
        int y = Math.Clamp(r.Y, a.Y, a.Bottom - h);
        if (w != r.Width || h != r.Height || x != r.X || y != r.Y) how += ", fitted into the work area";
        return (new PixelRect(x, y, w, h), saved.Maximized, how);
    }
}

/// <summary>What a 3D pop-out window remembers per kind ("model", "terrain"): its placement and its layout choices.</summary>
public sealed class PopoutSettings
{
    public WindowPlacementInfo? Placement { get; set; }
    /// <summary>The side panel with the controls is open.</summary>
    public bool SidePanel { get; set; } = true;
    /// <summary>"Orbit" or "Fly" (null = the kind's default: orbit for models, fly for terrain).</summary>
    public string? Navigation { get; set; }
}
