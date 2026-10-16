using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WoWDataStudio.Controls;

/// <summary>
/// The map overview of the Maps tab: the minimap picture of a map with a grid of its tiles. Click a tile to select it;
/// mouse wheel = zoom, drag with the right (or middle) button = move, double-click = fit. Overlays: the selected tile
/// (blue frame), the tiles of the selected zone (light), the tiles you changed (orange frame) and the tiles Noggit saved
/// that are not imported yet (dashed). 0.13.0: pins (quest givers and other spawns, "Show on map" of the Quest chains tab)
/// and the clicked point in tile units (<see cref="PointClicked"/>: "Pick on the map").
/// </summary>
public sealed class MinimapView : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(MinimapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MinimapView)d).Fit()));
    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(nameof(Cell), typeof(int), typeof(MinimapView), new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OriginXProperty = DependencyProperty.Register(nameof(OriginX), typeof(int), typeof(MinimapView), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OriginYProperty = DependencyProperty.Register(nameof(OriginY), typeof(int), typeof(MinimapView), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(nameof(Tiles), typeof(bool[]), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(Point?), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(IReadOnlyCollection<Point>), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChangedProperty = DependencyProperty.Register(nameof(Changed), typeof(IReadOnlyCollection<Point>), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PendingProperty = DependencyProperty.Register(nameof(Pending), typeof(IReadOnlyCollection<Point>), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PinsProperty = DependencyProperty.Register(nameof(Pins), typeof(IReadOnlyList<MapPin>), typeof(MinimapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <summary>Pixels per tile in <see cref="Source"/>.</summary>
    public int Cell { get => (int)GetValue(CellProperty); set => SetValue(CellProperty, value); }
    /// <summary>The tile at the picture's top-left corner.</summary>
    public int OriginX { get => (int)GetValue(OriginXProperty); set => SetValue(OriginXProperty, value); }
    public int OriginY { get => (int)GetValue(OriginYProperty); set => SetValue(OriginYProperty, value); }
    /// <summary>The map's tiles (64 x 64, index y * 64 + x).</summary>
    public bool[]? Tiles { get => (bool[]?)GetValue(TilesProperty); set => SetValue(TilesProperty, value); }
    public Point? Selected { get => (Point?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public IReadOnlyCollection<Point>? Highlight { get => (IReadOnlyCollection<Point>?)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public IReadOnlyCollection<Point>? Changed { get => (IReadOnlyCollection<Point>?)GetValue(ChangedProperty); set => SetValue(ChangedProperty, value); }
    public IReadOnlyCollection<Point>? Pending { get => (IReadOnlyCollection<Point>?)GetValue(PendingProperty); set => SetValue(PendingProperty, value); }

    /// <summary>Pins at world positions given in tile units (tile x + fraction, tile y + fraction).</summary>
    public IReadOnlyList<MapPin>? Pins { get => (IReadOnlyList<MapPin>?)GetValue(PinsProperty); set => SetValue(PinsProperty, value); }

    /// <summary>A tile of the map was clicked.</summary>
    public event Action<int, int>? TileClicked;

    /// <summary>0.13.0: the clicked point in tile units (fractional), for "Pick on the map".</summary>
    public event Action<double, double>? PointClicked;

    private double _zoom = 1, _panX, _panY;
    private bool _fitPending = true;
    private Point _down;
    private bool _dragging;
    private (int X, int Y)? _hover;

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1));
    private static readonly Pen SelectedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x9C, 0xFF)), 3));
    private static readonly Pen ChangedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1E)), 2.5));
    private static readonly Pen PendingPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x4D)), 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5));
    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(70, 120, 200, 255)));
    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x27)));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public MinimapView()
    {
        Focusable = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (_fitPending) Fit(); };
        ToolTipService.SetInitialShowDelay(this, 200);
    }

    public void Fit()
    {
        var s = Source;
        if (s == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _fitPending = true;
            InvalidateVisual();
            return;
        }
        _zoom = Math.Min(ActualWidth / s.PixelWidth, ActualHeight / s.PixelHeight);
        _panX = (ActualWidth - s.PixelWidth * _zoom) / 2;
        _panY = (ActualHeight - s.PixelHeight * _zoom) / 2;
        _fitPending = false;
        InvalidateVisual();
    }

    /// <summary>Zoom so the selected tile is in view (after selecting from a list).</summary>
    public void CenterOn(int x, int y)
    {
        var s = Source;
        if (s == null) return;
        double cx = ((x - OriginX) + 0.5) * Cell * _zoom + _panX, cy = ((y - OriginY) + 0.5) * Cell * _zoom + _panY;
        if (cx < 0 || cy < 0 || cx > ActualWidth || cy > ActualHeight)
        {
            _panX += ActualWidth / 2 - cx;
            _panY += ActualHeight / 2 - cy;
            InvalidateVisual();
        }
    }

    /// <summary>0.13.0: zoom in on a point (tile units) so a tile is <paramref name="pixelsPerTile"/> pixels wide.</summary>
    public void ZoomOn(double tileX, double tileY, double pixelsPerTile)
    {
        if (Source == null || Cell <= 0 || ActualWidth <= 0) return;
        _fitPending = false;
        _zoom = Math.Clamp(pixelsPerTile / Cell, 0.05, 16);
        _panX = ActualWidth / 2 - (tileX - OriginX) * Cell * _zoom;
        _panY = ActualHeight / 2 - (tileY - OriginY) * Cell * _zoom;
        InvalidateVisual();
    }

    private Rect TileRect(int x, int y) => new(_panX + (x - OriginX) * Cell * _zoom, _panY + (y - OriginY) * Cell * _zoom, Cell * _zoom, Cell * _zoom);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var s = Source;
        if (s == null) return;
        if (_fitPending) Fit();
        dc.DrawImage(s, new Rect(_panX, _panY, s.PixelWidth * _zoom, s.PixelHeight * _zoom));
        int cols = s.PixelWidth / Math.Max(1, Cell), rows = s.PixelHeight / Math.Max(1, Cell);
        if (Cell * _zoom >= 6)
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int tx = OriginX + x, ty = OriginY + y;
                    if (Tiles != null && tx < 64 && ty < 64 && Tiles[ty * 64 + tx]) dc.DrawRectangle(null, GridPen, TileRect(tx, ty));
                }
        if (Highlight != null) foreach (var p in Highlight) dc.DrawRectangle(HighlightBrush, null, TileRect((int)p.X, (int)p.Y));
        if (Pending != null) foreach (var p in Pending) dc.DrawRectangle(null, PendingPen, Inflate(TileRect((int)p.X, (int)p.Y), -2));
        if (Changed != null) foreach (var p in Changed) dc.DrawRectangle(null, ChangedPen, Inflate(TileRect((int)p.X, (int)p.Y), -1));
        if (_hover is { } h) dc.DrawRectangle(null, HoverPen, TileRect(h.X, h.Y));
        if (Selected is { } sel) dc.DrawRectangle(null, SelectedPen, TileRect((int)sel.X, (int)sel.Y));
        if (Pins != null)
        {
            var labels = new List<Rect>();
            foreach (var pin in Pins)
            {
                var c = new Point(_panX + (pin.TileX - OriginX) * Cell * _zoom, _panY + (pin.TileY - OriginY) * Cell * _zoom);
                dc.DrawEllipse(pin.IsNew ? PinNewBrush : PinBrush, PinPen, c, 6, 6);
                if (pin.Label.Length == 0) continue;
                var ft = new FormattedText(pin.Label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var box = new Rect(c.X + 9, c.Y - 9, ft.Width + 8, ft.Height + 2);
                // labels of pins close together are stacked, not drawn over each other
                while (labels.Any(l => l.IntersectsWith(box))) box.Offset(0, box.Height + 2);
                labels.Add(box);
                if (Math.Abs(box.Top - (c.Y - 9)) > 1) dc.DrawLine(PinPen, c, new Point(box.Left, box.Top + box.Height / 2));
                dc.DrawRoundedRectangle(PinLabelBack, null, box, 3, 3);
                dc.DrawText(ft, new Point(box.Left + 4, box.Top + 1));
            }
        }
    }

    private static readonly Brush PinBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
    private static readonly Brush PinNewBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Pen PinPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x27)), 2));
    private static readonly Brush PinLabelBack = Freeze(new SolidColorBrush(Color.FromArgb(200, 0x1B, 0x20, 0x27)));

    private static Rect Inflate(Rect r, double d)
    {
        r.Inflate(d, d);
        return r;
    }

    private (int X, int Y)? TileAt(Point p)
    {
        if (Source == null || Cell <= 0) return null;
        double fx = (p.X - _panX) / (_zoom * Cell), fy = (p.Y - _panY) / (_zoom * Cell);
        if (fx < 0 || fy < 0) return null;
        int x = OriginX + (int)fx, y = OriginY + (int)fy;
        if (x >= 64 || y >= 64 || (int)fx >= Source.PixelWidth / Cell || (int)fy >= Source.PixelHeight / Cell) return null;
        return (x, y);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        double f = Math.Pow(1.18, e.Delta / 120.0);
        double nz = Math.Clamp(_zoom * f, 0.05, 16);
        f = nz / _zoom;
        _panX = p.X - (p.X - _panX) * f;
        _panY = p.Y - (p.Y - _panY) * f;
        _zoom = nz;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            Fit();
            return;
        }
        _down = e.GetPosition(this);
        _dragging = e.ChangedButton is MouseButton.Right or MouseButton.Middle;
        if (_dragging) CaptureMouse();
        else if (e.ChangedButton == MouseButton.Left && TileAt(_down) is { } t && Tiles != null && Tiles[t.Y * 64 + t.X])
        {
            TileClicked?.Invoke(t.X, t.Y);
            PointClicked?.Invoke(OriginX + (_down.X - _panX) / (_zoom * Cell), OriginY + (_down.Y - _panY) / (_zoom * Cell));
        }
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            _panX += p.X - _down.X;
            _panY += p.Y - _down.Y;
            _down = p;
            InvalidateVisual();
            return;
        }
        var t = TileAt(p);
        if (t != _hover)
        {
            _hover = t;
            ToolTip = t is { } tt ? (Tiles != null && Tiles[tt.Y * 64 + tt.X] ? $"Tile {tt.X},{tt.Y}: click to select" : $"Tile {tt.X},{tt.Y}: no terrain") : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }
}

/// <summary>0.13.0: a pin on the map overview: a position in tile units (x from world y, y from world x) and a label.</summary>
public sealed record MapPin(double TileX, double TileY, string Label, bool IsNew = false)
{
    /// <summary>A pin at a world position (server coordinates x, y).</summary>
    public static MapPin FromWorld(double x, double y, string label, bool isNew = false) =>
        new(32 - y / WoWData.Core.Maps.TerrainMath.TileSize, 32 - x / WoWData.Core.Maps.TerrainMath.TileSize, label, isNew);
}
