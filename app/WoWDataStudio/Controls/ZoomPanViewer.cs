using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WoWDataStudio.Controls;

/// <summary>Zoom and pan shared by several viewers (the compare view's side-by-side mode binds both sides to one
/// instance, so they move together). Zoom 1 = one image pixel per screen pixel; Pan = screen position of the image's
/// top-left corner. Its 3D counterpart is Rendering.OrbitCamera, shared by the two viewports of ModelCompareView.</summary>
public sealed class ViewState : INotifyPropertyChanged
{
    private double _zoom = 1, _panX, _panY;
    public bool FitPending { get; set; } = true;

    public double Zoom { get => _zoom; set { if (Math.Abs(_zoom - value) > 1e-9) { _zoom = Math.Clamp(value, 0.02, 64); Raise(); Raise(nameof(ZoomText)); } } }
    public double PanX { get => _panX; set { if (_panX != value) { _panX = value; Raise(); } } }
    public double PanY { get => _panY; set { if (_panY != value) { _panY = value; Raise(); } } }
    public string ZoomText => $"{Zoom * 100:0}%";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public void Fit(double viewW, double viewH, double imgW, double imgH)
    {
        if (viewW <= 0 || viewH <= 0 || imgW <= 0 || imgH <= 0) { FitPending = true; return; }
        var z = Math.Min(viewW / imgW, viewH / imgH) * 0.94;
        if (z >= 2) z = Math.Floor(z);   // whole steps keep pixels crisp
        Zoom = z;
        PanX = Math.Round((viewW - imgW * Zoom) / 2);
        PanY = Math.Round((viewH - imgH * Zoom) / 2);
        FitPending = false;
    }

    public void Actual(double viewW, double viewH, double imgW, double imgH)
    {
        Zoom = 1;
        PanX = Math.Round((viewW - imgW) / 2);
        PanY = Math.Round((viewH - imgH) / 2);
        FitPending = false;
    }

    public void ZoomAt(double factor, Point at)
    {
        var old = Zoom;
        Zoom = old * factor;
        var k = Zoom / old;
        PanX = at.X - (at.X - PanX) * k;
        PanY = at.Y - (at.Y - PanY) * k;
    }
}

/// <summary>
/// An image viewer with mouse-wheel zoom (around the cursor), drag to pan, an optional checkerboard behind transparent
/// pixels, and an optional second image shown through a draggable wipe divider (compare "slider" mode).
/// </summary>
public sealed class ZoomPanViewer : Border
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ZoomPanViewer),
        new PropertyMetadata(null, (d, e) => ((ZoomPanViewer)d).OnSourceChanged()));
    public static readonly DependencyProperty SecondSourceProperty = DependencyProperty.Register(nameof(SecondSource), typeof(ImageSource), typeof(ZoomPanViewer),
        new PropertyMetadata(null, (d, e) => ((ZoomPanViewer)d).UpdateWipe()));
    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(nameof(View), typeof(ViewState), typeof(ZoomPanViewer),
        new PropertyMetadata(null, (d, e) => ((ZoomPanViewer)d).OnViewChanged((ViewState?)e.OldValue, (ViewState?)e.NewValue)));
    public static readonly DependencyProperty CheckerboardProperty = DependencyProperty.Register(nameof(Checkerboard), typeof(bool), typeof(ZoomPanViewer),
        new PropertyMetadata(true, (d, e) => ((ZoomPanViewer)d).UpdateBackground()));
    public static readonly DependencyProperty WipeEnabledProperty = DependencyProperty.Register(nameof(WipeEnabled), typeof(bool), typeof(ZoomPanViewer),
        new PropertyMetadata(false, (d, e) => ((ZoomPanViewer)d).UpdateWipe()));
    public static readonly DependencyProperty WipeFractionProperty = DependencyProperty.Register(nameof(WipeFraction), typeof(double), typeof(ZoomPanViewer),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ZoomPanViewer)d).UpdateWipe()));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    /// <summary>Shown to the right of the wipe divider.</summary>
    public ImageSource? SecondSource { get => (ImageSource?)GetValue(SecondSourceProperty); set => SetValue(SecondSourceProperty, value); }
    public ViewState? View { get => (ViewState?)GetValue(ViewProperty); set => SetValue(ViewProperty, value); }
    public bool Checkerboard { get => (bool)GetValue(CheckerboardProperty); set => SetValue(CheckerboardProperty, value); }
    public bool WipeEnabled { get => (bool)GetValue(WipeEnabledProperty); set => SetValue(WipeEnabledProperty, value); }
    /// <summary>Divider position across the viewer, 0..1.</summary>
    public double WipeFraction { get => (double)GetValue(WipeFractionProperty); set => SetValue(WipeFractionProperty, value); }

    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Rectangle _checker = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Image _image2 = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly Rectangle _divider = new() { Width = 3, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(31, 95, 168)), StrokeThickness = 1, Visibility = Visibility.Collapsed, Cursor = Cursors.SizeWE };
    private readonly Border _handle = new()
    {
        Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromRgb(31, 95, 168)),
        BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, Cursor = Cursors.SizeWE,
        Child = new TextBlock { Text = "◀▶", Foreground = Brushes.White, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };
    private Point? _dragStart;
    private bool _dragDivider;
    private double _panStartX, _panStartY;
    private ViewState _ownView = new();

    public ZoomPanViewer()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x38));
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_image2, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(_checker);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_image2);
        _canvas.Children.Add(_divider);
        _canvas.Children.Add(_handle);
        Child = _canvas;
        Focusable = true;
        SizeChanged += (_, _) => { if (CurrentView.FitPending) FitToView(); else Relayout(); };
        Loaded += (_, _) => { AttachView(CurrentView); if (CurrentView.FitPending) FitToView(); Relayout(); };
        UpdateBackground();
    }

    private ViewState CurrentView => View ?? _ownView;

    private void OnViewChanged(ViewState? old, ViewState? now)
    {
        if (old != null) old.PropertyChanged -= ViewPropertyChanged;
        AttachView(now ?? _ownView);
        Relayout();
    }

    private void AttachView(ViewState v)
    {
        v.PropertyChanged -= ViewPropertyChanged;
        v.PropertyChanged += ViewPropertyChanged;
    }

    private void ViewPropertyChanged(object? s, PropertyChangedEventArgs e) => Relayout();

    private (double W, double H) _lastSize;

    private void OnSourceChanged()
    {
        _image.Source = Source;
        var size = ImageSize();
        if (size != _lastSize && size.W > 0) CurrentView.FitPending = true;
        _lastSize = size;
        if (CurrentView.FitPending) FitToView();
        Relayout();
    }

    private (double W, double H) ImageSize() => Source is { } s ? (s.Width, s.Height) : (0, 0);

    public void FitToView()
    {
        var (w, h) = ImageSize();
        CurrentView.Fit(ActualWidth, ActualHeight, w, h);
    }

    public void ActualSize()
    {
        var (w, h) = ImageSize();
        CurrentView.Actual(ActualWidth, ActualHeight, w, h);
    }

    private void UpdateBackground() => _checker.Fill = Checkerboard ? (Brush)Application.Current?.TryFindResource("Checkerboard")! ?? Brushes.White : Brushes.Black;

    private void UpdateWipe()
    {
        _image2.Source = SecondSource;
        var vis = WipeEnabled && SecondSource != null ? Visibility.Visible : Visibility.Collapsed;
        _image2.Visibility = _divider.Visibility = _handle.Visibility = vis;
        Relayout();
    }

    private void Relayout()
    {
        var v = CurrentView;
        var (w, h) = ImageSize();
        double sw = w * v.Zoom, sh = h * v.Zoom;
        foreach (FrameworkElement el in new FrameworkElement[] { _checker, _image, _image2 })
        {
            el.Width = Math.Max(0, sw);
            el.Height = Math.Max(0, sh);
            Canvas.SetLeft(el, v.PanX);
            Canvas.SetTop(el, v.PanY);
        }
        _checker.Visibility = Source == null ? Visibility.Collapsed : Visibility.Visible;
        var mode = v.Zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        RenderOptions.SetBitmapScalingMode(_image, mode);
        RenderOptions.SetBitmapScalingMode(_image2, mode);
        if (_image2.Visibility == Visibility.Visible)
        {
            double x = Math.Clamp(WipeFraction, 0, 1) * ActualWidth;
            _image2.Clip = new RectangleGeometry(new Rect(x - v.PanX, 0, Math.Max(0, sw - (x - v.PanX)), Math.Max(0, sh)));
            _divider.Height = ActualHeight;
            Canvas.SetLeft(_divider, x - 1.5);
            Canvas.SetTop(_divider, 0);
            Canvas.SetLeft(_handle, x - 13);
            Canvas.SetTop(_handle, ActualHeight / 2 - 13);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        CurrentView.ZoomAt(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(this));
        CurrentView.FitPending = false;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _dragDivider = _image2.Visibility == Visibility.Visible && Math.Abs(p.X - WipeFraction * ActualWidth) < 16;
        _dragStart = p;
        _panStartX = CurrentView.PanX;
        _panStartY = CurrentView.PanY;
        if (e.ClickCount == 2 && !_dragDivider) FitToView();
        CaptureMouse();
        Cursor = _dragDivider ? Cursors.SizeWE : Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (_dragDivider) WipeFraction = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
        else
        {
            CurrentView.PanX = _panStartX + (p.X - start.X);
            CurrentView.PanY = _panStartY + (p.Y - start.Y);
            CurrentView.FitPending = false;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
        Cursor = null;
    }
}
