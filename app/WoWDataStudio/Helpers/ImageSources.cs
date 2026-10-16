using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WoWData.Core.Imaging;

namespace WoWDataStudio.Helpers;

public enum AlphaView { Checkerboard, IgnoreAlpha, AlphaOnly }

public static class ImageSources
{
    /// <summary>A frozen WPF bitmap of an RGBA image, shown as colour + alpha, colour only, or alpha as grey.</summary>
    public static BitmapSource ToBitmap(RgbaImage img, AlphaView view = AlphaView.Checkerboard)
    {
        var src = img.Pixels;
        var bgra = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            switch (view)
            {
                case AlphaView.AlphaOnly:
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = src[i + 3];
                    bgra[i + 3] = 255;
                    break;
                case AlphaView.IgnoreAlpha:
                    bgra[i] = src[i + 2]; bgra[i + 1] = src[i + 1]; bgra[i + 2] = src[i];
                    bgra[i + 3] = 255;
                    break;
                default:
                    bgra[i] = src[i + 2]; bgra[i + 1] = src[i + 1]; bgra[i + 2] = src[i];
                    bgra[i + 3] = src[i + 3];
                    break;
            }
        }
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Render any visual to a PNG file (screenshots).</summary>
    public static void SaveVisual(FrameworkElement el, string path, double scale = 1.0)
    {
        el.UpdateLayout();
        // the element with its margin (a window's content): the whole client area of the window, nothing cut
        var m = el.Margin;
        double bw = el.ActualWidth + m.Left + m.Right, bh = el.ActualHeight + m.Top + m.Bottom;
        int w = (int)Math.Ceiling(bw * scale), h = (int)Math.Ceiling(bh * scale);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // map exactly the element's layout box (a child drawn outside it, such as a 3D view's image from before a
            // resize, must not make the brush shrink the whole picture to fit it in). The brush draws the element at its
            // own offset in its parent (its margin), so the box starts at the parent's corner: before 0.10.0 the margin
            // was transparent at the top and left and the last row of buttons was cut at the bottom and right.
            var box = new Rect(0, 0, bw, bh);
            var off = VisualTreeHelper.GetOffset(el);
            var view = new Rect(off.X - m.Left, off.Y - m.Top, bw, bh);
            if (Window.GetWindow(el)?.Background is Brush back) dc.DrawRectangle(back, null, box);
            dc.DrawRectangle(new VisualBrush(el) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = view, Stretch = Stretch.Fill }, null, box);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        enc.Save(f);
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        bool has = value != null && !(value is string s && s.Length == 0) && !(value is int i && i == 0) && !(value is System.Collections.ICollection col && col.Count == 0);
        return (has ^ Invert) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is true ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}

public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        p?.ToString()?.Split('|').Contains(value?.ToString()) == true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class AlphaViewIndex : IValueConverter
{
    public static readonly AlphaViewIndex Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is AlphaView a ? (int)a : 0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is int i && i >= 0 ? (AlphaView)i : AlphaView.Checkerboard;
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed record ActionLabel(string Glyph, string Title);

/// <summary>Labels of the texture actions (glyph from Segoe MDL2 Assets + title), numbered in workflow order.</summary>
public static class Glyphs
{
    public static readonly ActionLabel Export = new("\uE74E", "1. Export texture as PNG...");
    public static readonly ActionLabel Import = new("\uE8B5", "2. Import edited PNG...");
    public static readonly ActionLabel Compare = new("\uE89F", "Compare original vs. edited");
    public static readonly ActionLabel TestInGame = new("\uE768", "3. Test in game (install as loose files)");
    public static readonly ActionLabel Remove = new("\uE74D", "Remove from game");
    public static readonly ActionLabel Revert = new("\uE7A7", "Revert my change");
    public static readonly ActionLabel BuildPatch = new("\uE7B8", "4. Build release patch (patch-3.MPQ)...");
}
