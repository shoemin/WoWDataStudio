namespace WoWData.Core.Imaging;

/// <summary>Before/after statistics of two images. Colour (RGB) and alpha are counted separately, and colour changes are
/// always counted, also under pixels that are fully transparent: in many textures the alpha is not transparency (the
/// shine of a ground "_s" texture, an opaque model or building material, an environment / glow mask), so the game shows
/// that colour. <see cref="HiddenColourPixels"/> says how many of the colour changes sit under pixels that are fully
/// transparent in both images (invisible only where the game uses the alpha as transparency).</summary>
public sealed record DiffStats(int Width, int Height, int ChangedPixels, double ChangedPercent, int MaxDifference, double MeanDifference, double? PsnrDb,
    int ColourChangedPixels = 0, int AlphaChangedPixels = 0, int HiddenColourPixels = 0, bool AlphaCompared = true)
{
    public int TotalPixels => Width * Height;
    public double ColourChangedPercent => TotalPixels == 0 ? 0 : 100.0 * ColourChangedPixels / TotalPixels;
    public double AlphaChangedPercent => TotalPixels == 0 ? 0 : 100.0 * AlphaChangedPixels / TotalPixels;
    public double HiddenColourPercent => TotalPixels == 0 ? 0 : 100.0 * HiddenColourPixels / TotalPixels;

    /// <summary>"Colour changed in X% of pixels; alpha changed in Y%."</summary>
    public string Headline =>
        ChangedPixels == 0 ? "No pixels changed (identical)."
            : AlphaCompared ? $"Colour changed in {Pct(ColourChangedPercent)} of pixels; alpha changed in {Pct(AlphaChangedPercent)}."
            : $"Colour changed in {Pct(ColourChangedPercent)} of pixels (alpha not compared).";

    public string Summary
    {
        get
        {
            if (ChangedPixels == 0) return Headline;
            var s = Headline + $" {ChangedPixels:N0} of {TotalPixels:N0} pixels differ; largest difference {MaxDifference} of 255.";
            if (HiddenColourPixels > 0)
                s += $" {Pct(HiddenColourPercent)} of the pixels changed colour where the alpha is 0: invisible only where the game uses the alpha as transparency" +
                     " (in a ground _s texture, an opaque material or a glow / shine mask that colour shows).";
            return s;
        }
    }

    private static string Pct(double p) => p == 0 ? "0 %" : p < 0.01 ? "< 0.01 %" : $"{p:0.##} %";
}

/// <summary>Before/after comparison of two images of the same size: statistics and a difference heat map.</summary>
public static class ImageCompare
{
    /// <summary>Largest RGB difference of one pixel (alpha left out).</summary>
    public static int ColourDiff(byte[] a, byte[] b, int o) =>
        Math.Max(Math.Abs(a[o] - b[o]), Math.Max(Math.Abs(a[o + 1] - b[o + 1]), Math.Abs(a[o + 2] - b[o + 2])));

    /// <summary>Per-pixel difference: the largest of the RGB differences and (with <paramref name="compareAlpha"/>) the alpha
    /// difference. Colour under transparent pixels counts too.</summary>
    public static int PixelDiff(byte[] a, byte[] b, int o, bool compareAlpha)
    {
        int d = ColourDiff(a, b, o);
        if (compareAlpha) d = Math.Max(d, Math.Abs(a[o + 3] - b[o + 3]));
        return d;
    }

    public static DiffStats Compare(RgbaImage a, RgbaImage b, bool compareAlpha = true)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("images differ in size");
        int changed = 0, max = 0, colour = 0, alpha = 0, hidden = 0;
        double sum = 0, sq = 0;
        var pa = a.Pixels;
        var pb = b.Pixels;
        int n = a.Width * a.Height;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int dc = ColourDiff(pa, pb, o);
            int da = compareAlpha ? Math.Abs(pa[o + 3] - pb[o + 3]) : 0;
            if (dc > 0)
            {
                colour++;
                if (pa[o + 3] == 0 && pb[o + 3] == 0) hidden++;
            }
            if (da > 0) alpha++;
            int d = Math.Max(dc, da);
            if (d > 0)
            {
                changed++;
                if (d > max) max = d;
            }
            for (int c = 0; c < 3; c++)
            {
                int e = pa[o + c] - pb[o + c];
                sum += Math.Abs(e);
                sq += e * e;
            }
        }
        double mse = sq / (3.0 * n);
        return new DiffStats(a.Width, a.Height, changed, n == 0 ? 0 : 100.0 * changed / n, max, n == 0 ? 0 : sum / (3.0 * n),
            mse == 0 ? null : Math.Round(10 * Math.Log10(255.0 * 255.0 / mse), 2), colour, alpha, hidden, compareAlpha);
    }

    /// <summary>Pixels whose colour changed where both images are fully transparent are left out: the old "visible
    /// change" count, right only where the game uses the alpha as transparency (alpha-key / blended materials). The model
    /// import uses it for textures that no opaque material draws.</summary>
    public static int VisibleChangedPixels(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("images differ in size");
        int changed = 0;
        var pa = a.Pixels;
        var pb = b.Pixels;
        for (int o = 0; o < pa.Length; o += 4)
        {
            if (pa[o + 3] == 0 && pb[o + 3] == 0) continue;
            if (PixelDiff(pa, pb, o, true) > 0) changed++;
        }
        return changed;
    }

    /// <summary>Heat-map colour of an alpha-only change.</summary>
    public static readonly (byte R, byte G, byte B) AlphaOnlyColour = (40, 140, 255);

    /// <summary>Heat map: unchanged pixels as a dimmed grey copy of <paramref name="a"/>, colour changes from yellow (small) to
    /// red (large difference) wherever they are (also under transparent pixels), changes of the alpha alone in blue;
    /// fully opaque.</summary>
    public static RgbaImage Heatmap(RgbaImage a, RgbaImage b, bool compareAlpha = true)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("images differ in size");
        var outImg = new RgbaImage(a.Width, a.Height);
        var po = outImg.Pixels;
        var pa = a.Pixels;
        var pb = b.Pixels;
        for (int o = 0; o < po.Length; o += 4)
        {
            int dc = ColourDiff(pa, pb, o);
            int da = compareAlpha ? Math.Abs(pa[o + 3] - pb[o + 3]) : 0;
            if (dc == 0 && da == 0)
            {
                int g = (pa[o] * 30 + pa[o + 1] * 59 + pa[o + 2] * 11) / 100;
                g = 30 + g * 70 / 255;   // dimmed
                po[o] = po[o + 1] = po[o + 2] = (byte)g;
            }
            else if (dc == 0)
            {
                po[o] = AlphaOnlyColour.R;
                po[o + 1] = AlphaOnlyColour.G;
                po[o + 2] = AlphaOnlyColour.B;
            }
            else
            {
                // 1 -> yellow (255, 230, 0), >= 64 -> red (255, 0, 0); always clearly visible
                double t = Math.Min(1.0, Math.Max(dc, da) / 64.0);
                po[o] = 255;
                po[o + 1] = (byte)(230 * (1 - t));
                po[o + 2] = 0;
            }
            po[o + 3] = 255;
        }
        return outImg;
    }
}
