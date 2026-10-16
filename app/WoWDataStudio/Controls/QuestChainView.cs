using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WoWData.Core.Designers;

namespace WoWDataStudio.Controls;

/// <summary>
/// 0.13.0: the flowchart of a quest chain (the Quest chains tab): one lane per faction or race / class restriction, the
/// quests as boxes in columns (left to right in the order they are done), the links as arrows (solid: must be done
/// first; orange: must be in the quest log; dashed: opens; thick blue: next in chain; dotted: breadcrumb), choice groups
/// framed ("one of"), "all of" groups framed in green. Click a quest to select it; mouse wheel = zoom; drag with the
/// right (or middle) button = move; double-click = fit.
/// </summary>
public sealed class QuestChainView : FrameworkElement
{
    public const double BoxW = 178, BoxH = 60, GapX = 40, GapY = 14, LaneHeader = 24, LaneLabelW = 6, Pad = 10;

    public static readonly DependencyProperty ChainProperty = DependencyProperty.Register(nameof(Chain), typeof(QuestChain), typeof(QuestChainView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((QuestChainView)d).Fit()));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(long), typeof(QuestChainView),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public QuestChain? Chain { get => (QuestChain?)GetValue(ChainProperty); set => SetValue(ChainProperty, value); }
    public long Selected { get => (long)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>A quest box was clicked.</summary>
    public event Action<long>? QuestClicked;

    private double _zoom = 1, _panX, _panY;
    private bool _fitPending = true, _dragging;
    private Point _down;
    private long _hover;

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static readonly Brush Back = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)));
    private static readonly Brush LaneA = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFB)));
    private static readonly Brush LaneH = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xEB, 0xE8)));
    private static readonly Brush LaneN = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xEC)));
    private static readonly Brush BoxFill = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush EditedFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE6)));
    private static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Pen BoxPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), 1.2));
    private static readonly Pen ErrorPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), 2.4));
    private static readonly Pen WarnPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), 2.2));
    private static readonly Pen SelectedPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), 3.4));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), 1.6));
    private static readonly Pen RequiresPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)), 1.6));
    private static readonly Pen ActivePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)), 1.8));
    private static readonly Pen OpensPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)), 1.6) { DashStyle = DashStyles.Dash });
    private static readonly Pen ChainPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 3.0));
    private static readonly Pen BreadcrumbPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)), 1.6) { DashStyle = DashStyles.Dot });
    private static readonly Pen ChoicePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), 1.8) { DashStyle = DashStyles.Dash });
    private static readonly Pen AllOfPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)), 1.8) { DashStyle = DashStyles.Dash });
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public QuestChainView()
    {
        Focusable = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (_fitPending) Fit(); };
        ToolTipService.SetInitialShowDelay(this, 250);
    }

    // ------------------------------------------------------------------ layout (canvas units)

    private List<(QuestLane Lane, double Top, double Height)> Lanes()
    {
        var res = new List<(QuestLane, double, double)>();
        var c = Chain;
        if (c == null) return res;
        double y = Pad;
        foreach (var l in c.Lanes)
        {
            int rows = c.Nodes.Where(n => n.LaneIndex == l.Index).Select(n => n.Row).DefaultIfEmpty(0).Max() + 1;
            double h = LaneHeader + rows * (BoxH + GapY) + GapY;
            res.Add((l, y, h));
            y += h + 8;
        }
        return res;
    }

    private Rect BoxOf(QuestNode n, IReadOnlyList<(QuestLane Lane, double Top, double Height)> lanes)
    {
        var lane = lanes.First(l => l.Lane.Index == n.LaneIndex);
        return new Rect(Pad + LaneLabelW + n.Column * (BoxW + GapX), lane.Top + LaneHeader + GapY / 2 + n.Row * (BoxH + GapY), BoxW, BoxH);
    }

    private Size CanvasSize()
    {
        var c = Chain;
        if (c == null) return new Size(1, 1);
        var lanes = Lanes();
        double w = Pad * 2 + LaneLabelW + Math.Max(1, c.Columns) * (BoxW + GapX);
        double h = lanes.Count == 0 ? 100 : lanes[^1].Top + lanes[^1].Height + Pad;
        return new Size(w, h);
    }

    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || Chain == null) { _fitPending = true; InvalidateVisual(); return; }
        var s = CanvasSize();
        // the whole chain when it fits at a readable size; else readable, from its start (move to see the rest)
        _zoom = Math.Min(1.3, Math.Min(ActualWidth / s.Width, ActualHeight / s.Height));
        _zoom = Math.Max(_zoom, 0.72);
        _panX = Math.Max(0, (ActualWidth - s.Width * _zoom) / 2);
        _panY = Math.Max(0, (ActualHeight - s.Height * _zoom) / 2);
        _fitPending = false;
        InvalidateVisual();
    }

    private Point ToScreen(Point p) => new(_panX + p.X * _zoom, _panY + p.Y * _zoom);
    private Point ToCanvas(Point p) => new((p.X - _panX) / _zoom, (p.Y - _panY) / _zoom);

    private FormattedText Txt(string s, double size, Brush b, Typeface? face = null, double maxWidth = 0)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face ?? Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Back, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var c = Chain;
        if (c == null || c.Nodes.Count == 0)
        {
            dc.DrawText(Txt("Open a quest's chain: find a quest on the left, then \"Open chain\".", 13, Muted), new Point(16, 16));
            return;
        }
        if (_fitPending) Fit();
        dc.PushTransform(new TranslateTransform(_panX, _panY));
        dc.PushTransform(new ScaleTransform(_zoom, _zoom));
        var lanes = Lanes();
        var size = CanvasSize();
        // lanes
        foreach (var (lane, top, h) in lanes)
        {
            var (a, ho) = QuestForm.Teams(lane.Races);
            var fill = a && !ho ? LaneA : ho && !a ? LaneH : LaneN;
            dc.DrawRoundedRectangle(fill, null, new Rect(Pad, top, size.Width - Pad * 2, h), 6, 6);
            dc.DrawText(Txt(lane.Name, 12.5, Text, Bold, size.Width - Pad * 2 - 16), new Point(Pad + 8, top + 5));
        }
        var boxes = c.Nodes.ToDictionary(n => n.Id, n => BoxOf(n, lanes));
        // groups
        foreach (var g in c.Groups)
        {
            var members = g.Quests.Where(boxes.ContainsKey).Select(q => boxes[q]).ToList();
            if (members.Count < 2) continue;
            var pen = g.IsChoice ? ChoicePen : AllOfPen;
            string label = g.IsChoice ? $"one of (group {g.Id})" : $"all of (group {g.Id})";
            // members in one column: one frame around them; in several columns: a frame each, with the group's label
            foreach (var col in members.GroupBy(m => Math.Round(m.Left)))
            {
                var r = col.Aggregate(Rect.Union);
                r.Inflate(7, 7);
                dc.DrawRoundedRectangle(null, pen, r, 8, 8);
                dc.DrawText(Txt(label, 10.5, pen.Brush), new Point(r.Left + 6, r.Bottom + 1));
            }
        }
        // links
        foreach (var e in c.Edges)
        {
            if (!boxes.TryGetValue(e.From, out var a) || !boxes.TryGetValue(e.To, out var b)) continue;
            var pen = (e.Kinds & QuestLinkKind.NextInChain) != 0 ? ChainPen : (e.Kinds & QuestLinkKind.RequiresActive) != 0 ? ActivePen
                : (e.Kinds & QuestLinkKind.Requires) != 0 ? RequiresPen : (e.Kinds & QuestLinkKind.Opens) != 0 ? OpensPen : BreadcrumbPen;
            Point p1, p2;
            if (b.Left > a.Right - 1) { p1 = new Point(a.Right, a.Top + a.Height / 2); p2 = new Point(b.Left, b.Top + b.Height / 2); }
            else if (b.Right < a.Left + 1) { p1 = new Point(a.Left, a.Top + a.Height / 2); p2 = new Point(b.Right, b.Top + b.Height / 2); }
            else if (b.Top > a.Bottom) { p1 = new Point(a.Left + a.Width / 2, a.Bottom); p2 = new Point(b.Left + b.Width / 2, b.Top); }
            else { p1 = new Point(a.Left + a.Width / 2, a.Top); p2 = new Point(b.Left + b.Width / 2, b.Bottom); }
            bool horizontal = Math.Abs(p2.X - p1.X) > Math.Abs(p2.Y - p1.Y) || p1.Y == a.Top + a.Height / 2;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p1, false, false);
                double k = horizontal ? Math.Max(24, Math.Abs(p2.X - p1.X) / 2) * Math.Sign(p2.X - p1.X == 0 ? 1 : p2.X - p1.X) : 0;
                double ky = horizontal ? 0 : Math.Max(20, Math.Abs(p2.Y - p1.Y) / 2) * Math.Sign(p2.Y - p1.Y == 0 ? 1 : p2.Y - p1.Y);
                ctx.BezierTo(new Point(p1.X + k, p1.Y + ky), new Point(p2.X - k, p2.Y - ky), p2, true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            // arrowhead
            var dir = horizontal ? new Vector(Math.Sign(p2.X - p1.X == 0 ? 1 : p2.X - p1.X), 0) : new Vector(0, Math.Sign(p2.Y - p1.Y == 0 ? 1 : p2.Y - p1.Y));
            var n = new Vector(-dir.Y, dir.X);
            var tip = p2;
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(tip - dir * 9 + n * 4.5, true, false);
                ctx.LineTo(tip - dir * 9 - n * 4.5, true, false);
            }
            head.Freeze();
            dc.DrawGeometry(pen.Brush, null, head);
        }
        // quests
        foreach (var node in c.Nodes)
        {
            var r = boxes[node.Id];
            dc.DrawRoundedRectangle(node.Edited ? EditedFill : BoxFill, node.Errors > 0 ? ErrorPen : node.Warnings > 0 ? WarnPen : BoxPen, r, 6, 6);
            if (node.Id == _hover && node.Id != Selected) dc.DrawRoundedRectangle(null, HoverPen, Inflate(r, 2), 7, 7);
            if (node.Id == Selected) dc.DrawRoundedRectangle(null, SelectedPen, Inflate(r, 3), 8, 8);
            dc.DrawText(Txt(node.Title, 12.5, Text, Bold, BoxW - 14), new Point(r.Left + 7, r.Top + 5));
            dc.DrawText(Txt($"{node.Id} · level {node.Level}" + (node.MinLevel > 0 && node.MinLevel != node.Level ? $" (from {node.MinLevel})" : ""), 11, Muted, null, BoxW - 14), new Point(r.Left + 7, r.Top + 23));
            var who = node.Givers.Length > 0 ? (node.Enders.Length > 0 && node.Enders != node.Givers ? $"{node.Givers} -> {node.Enders}" : node.Givers) : "no quest giver";
            dc.DrawText(Txt(who, 10.5, node.Givers.Length == 0 ? ErrorPen.Brush : Muted, null, BoxW - 14), new Point(r.Left + 7, r.Top + 40));
            if (node.Errors + node.Warnings > 0)
            {
                var badge = Txt(node.Errors > 0 ? $"{node.Errors}!" : $"{node.Warnings}?", 10.5, Brushes.White, Bold);
                var br = new Rect(r.Right - badge.Width - 12, r.Top - 8, badge.Width + 10, 16);
                dc.DrawRoundedRectangle(node.Errors > 0 ? ErrorPen.Brush : WarnPen.Brush, null, br, 8, 8);
                dc.DrawText(badge, new Point(br.Left + 5, br.Top + 0.5));
            }
        }
        dc.Pop();
        dc.Pop();
    }

    private static Rect Inflate(Rect r, double d)
    {
        r.Inflate(d, d);
        return r;
    }

    private QuestNode? NodeAt(Point screen)
    {
        var c = Chain;
        if (c == null) return null;
        var p = ToCanvas(screen);
        var lanes = Lanes();
        return c.Nodes.FirstOrDefault(n => BoxOf(n, lanes).Contains(p));
    }

    /// <summary>Scroll a quest into view.</summary>
    public void Reveal(long id)
    {
        var c = Chain;
        var n = c?.Node(id);
        if (n == null) return;
        var r = BoxOf(n, Lanes());
        var a = ToScreen(r.TopLeft);
        var b = ToScreen(r.BottomRight);
        if (a.X < 0 || a.Y < 0 || b.X > ActualWidth || b.Y > ActualHeight)
        {
            _panX += ActualWidth / 2 - (a.X + b.X) / 2;
            _panY += ActualHeight / 2 - (a.Y + b.Y) / 2;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        double f = Math.Pow(1.15, e.Delta / 120.0);
        double nz = Math.Clamp(_zoom * f, 0.2, 3);
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
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && NodeAt(e.GetPosition(this)) == null) { Fit(); return; }
        _down = e.GetPosition(this);
        _dragging = e.ChangedButton is MouseButton.Right or MouseButton.Middle;
        if (_dragging) CaptureMouse();
        else if (e.ChangedButton == MouseButton.Left && NodeAt(_down) is { } n)
        {
            Selected = n.Id;
            QuestClicked?.Invoke(n.Id);
        }
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
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
        var n = NodeAt(p);
        long id = n?.Id ?? 0;
        if (id != _hover)
        {
            _hover = id;
            ToolTip = n == null ? null : $"{n.Title} [{n.Id}]\nLevel {n.Level}" + (n.MinLevel > 0 ? $", from level {n.MinLevel}" : "") + $"\n{n.Lane}\nGiven by: {(n.Givers.Length > 0 ? n.Givers : "nobody")}\nHanded in to: {(n.Enders.Length > 0 ? n.Enders : "nobody")}" +
                (n.Errors + n.Warnings > 0 ? $"\n{n.Errors} error(s), {n.Warnings} warning(s): see Checks" : "") + "\nClick to edit it.";
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = 0;
        InvalidateVisual();
    }
}
