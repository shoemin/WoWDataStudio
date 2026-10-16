namespace WoWData.Core.Imaging;

/// <summary>Image helpers: resizing for power-of-two textures and power-of-two arithmetic.</summary>
public static class ImageOps
{
    public static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Smallest power of two &gt;= n (1 for n &lt;= 1).</summary>
    public static int NextPow2(int n)
    {
        if (n <= 1) return 1;
        if (n > (1 << 30)) throw new ArgumentOutOfRangeException(nameof(n));
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>The power of two closest to n (the larger one on a tie, e.g. 384 -> 512; 1 for n &lt;= 1).</summary>
    public static int NearestPow2(int n)
    {
        if (n <= 1) return 1;
        int hi = NextPow2(n), lo = hi >> 1;
        if (hi == n) return n;
        return n - lo < hi - n ? lo : hi;
    }

    /// <summary>
    /// Resized copy. Each axis is resampled separately: area averaging (exact pixel coverage) when it shrinks, bilinear
    /// interpolation (pixel centres, edges clamped) when it grows. The filtering is done on premultiplied alpha so the
    /// colour of transparent pixels does not bleed into visible ones; fully transparent results get colour 0.
    /// </summary>
    public static RgbaImage Resize(RgbaImage src, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "the new size must be positive");
        if (width == src.Width && height == src.Height) return src.Clone();
        int sw = src.Width, sh = src.Height;
        // premultiplied float copy
        var f = new float[sw * sh * 4];
        var p = src.Pixels;
        for (int i = 0; i < sw * sh; i++)
        {
            float a = p[i * 4 + 3] / 255f;
            f[i * 4] = p[i * 4] * a; f[i * 4 + 1] = p[i * 4 + 1] * a; f[i * 4 + 2] = p[i * 4 + 2] * a; f[i * 4 + 3] = p[i * 4 + 3];
        }
        var wx = Weights(sw, width);
        var wy = Weights(sh, height);
        // horizontal pass: sh rows x width
        var tmp = new float[width * sh * 4];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                foreach (var (si, w) in wx[x])
                {
                    int s = (y * sw + si) * 4;
                    r += f[s] * w; g += f[s + 1] * w; b += f[s + 2] * w; a += f[s + 3] * w;
                }
                int d = (y * width + x) * 4;
                tmp[d] = r; tmp[d + 1] = g; tmp[d + 2] = b; tmp[d + 3] = a;
            }
        var dst = new RgbaImage(width, height);
        var o = dst.Pixels;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                foreach (var (si, w) in wy[y])
                {
                    int s = (si * width + x) * 4;
                    r += tmp[s] * w; g += tmp[s + 1] * w; b += tmp[s + 2] * w; a += tmp[s + 3] * w;
                }
                int d = (y * width + x) * 4;
                byte ab = ToByte(a);
                o[d + 3] = ab;
                if (a <= 0.0001f) { o[d] = 0; o[d + 1] = 0; o[d + 2] = 0; continue; }
                float inv = 255f / a;
                o[d] = ToByte(r * inv); o[d + 1] = ToByte(g * inv); o[d + 2] = ToByte(b * inv);
            }
        return dst;
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>For every destination index, the source indices and weights (summing to 1).</summary>
    private static List<(int Src, float W)>[] Weights(int srcLen, int dstLen)
    {
        var res = new List<(int, float)>[dstLen];
        if (dstLen < srcLen)
        {
            double scale = (double)srcLen / dstLen;
            for (int i = 0; i < dstLen; i++)
            {
                double x0 = i * scale, x1 = (i + 1) * scale;
                var l = new List<(int, float)>();
                for (int s = (int)Math.Floor(x0); s < Math.Min(srcLen, (int)Math.Ceiling(x1)); s++)
                {
                    double cover = Math.Min(x1, s + 1) - Math.Max(x0, s);
                    if (cover > 1e-9) l.Add((s, (float)(cover / scale)));
                }
                res[i] = l;
            }
        }
        else
        {
            double scale = (double)srcLen / dstLen;
            for (int i = 0; i < dstLen; i++)
            {
                double x = (i + 0.5) * scale - 0.5;
                if (x < 0) x = 0;
                if (x > srcLen - 1) x = srcLen - 1;
                int i0 = (int)Math.Floor(x);
                int i1 = Math.Min(i0 + 1, srcLen - 1);
                float t = (float)(x - i0);
                res[i] = t == 0 || i1 == i0 ? new List<(int, float)> { (i0, 1f) } : new List<(int, float)> { (i0, 1f - t), (i1, t) };
            }
        }
        return res;
    }
}
