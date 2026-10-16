using WoWData.Core.Imaging;

namespace WoWData.Core.Blp;

/// <summary>
/// DXT1 / DXT3 / DXT5 (BC1-3) block decoder and encoder: a port of wowdata/dxt.py that produces the same bytes.
/// </summary>
/// <remarks>
/// Decoder: the standard S3TC rules. Interpolated colours use integer division on the 8-bit expanded endpoints
/// ((2*a + b) / 3, (a + b) / 2, (6*a0 + a1) / 7 ...). 565 is expanded by bit replication; <c>shiftOnly</c>
/// reproduces Pillow's plain shifts (white = 248), used only to cross-check against Pillow.
///
/// Encoder, per 4x4 block: endpoints from the extremes of the colours projected on the block's principal axis
/// (inset by 1/16), nearest-palette indices, then two rounds of least-squares endpoint refinement kept only when
/// the error drops. Blocks with transparent pixels (DXT1 1-bit alpha) use the 3-colour mode with index 3 =
/// transparent. DXT5 alpha tries the 8-value and the 6-value (+0/255) modes and keeps the better one.
///
/// Byte-exactness with the numpy reference: every float64 operation is done in the order numpy 2.x performs it
/// on x86-64 (checked against the Python encoder on tens of thousands of blocks):
///  * a sum over the 16 pixels of an (N,16) array is numpy's pairwise sum (8 accumulators, see <see cref="Pw16"/>);
///  * a sum over the pixels of an (N,16,3) array (axis 1) is sequential, starting from 0;
///  * a sum over the 3 channels is sequential;
///  * the covariance einsum accumulates sequentially without FMA; the matrix-vector einsum of the power
///    iteration sums its three products as (p0 + p2) + p1 (numpy's 2-lane SIMD kernel).
/// Ties in argmin pick the first index; np.rint is round-half-to-even (Math.Round's default).
/// </remarks>
public static class Dxt
{
    public static int BlockCount(int width, int height) => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);

    public static int BlockSize(string kind) => kind == "dxt1" ? 8 : 16;

    // ------------------------------------------------------------------ helpers

    private static void Expand565(int c, bool shiftOnly, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
        if (shiftOnly) { r = r5 << 3; g = g6 << 2; b = b5 << 3; return; }
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }

    /// <summary>Colour palette (4 x RGBA) of a DXT1 colour block, alpha 0 for index 3 in 3-colour mode.</summary>
    private static void Dxt1Palette(int c0, int c1, bool shiftOnly, Span<int> pal)
    {
        Expand565(c0, shiftOnly, out int r0, out int g0, out int b0);
        Expand565(c1, shiftOnly, out int r1, out int g1, out int b1);
        pal[0] = r0; pal[1] = g0; pal[2] = b0; pal[3] = 255;
        pal[4] = r1; pal[5] = g1; pal[6] = b1; pal[7] = 255;
        if (c0 > c1)
        {
            pal[8] = (2 * r0 + r1) / 3; pal[9] = (2 * g0 + g1) / 3; pal[10] = (2 * b0 + b1) / 3; pal[11] = 255;
            pal[12] = (r0 + 2 * r1) / 3; pal[13] = (g0 + 2 * g1) / 3; pal[14] = (b0 + 2 * b1) / 3; pal[15] = 255;
        }
        else
        {
            pal[8] = (r0 + r1) / 2; pal[9] = (g0 + g1) / 2; pal[10] = (b0 + b1) / 2; pal[11] = 255;
            pal[12] = 0; pal[13] = 0; pal[14] = 0; pal[15] = 0;
        }
    }

    /// <summary>The 8 alpha values of a DXT5 alpha block (a0 &gt; a1: 8-value mode, else 6 values + 0 + 255).</summary>
    private static void AlphaValues(int a0, int a1, Span<int> vals)
    {
        vals[0] = a0; vals[1] = a1;
        if (a0 > a1)
        {
            for (int k = 1; k <= 6; k++) vals[1 + k] = ((7 - k) * a0 + k * a1) / 7;
        }
        else
        {
            for (int k = 1; k <= 4; k++) vals[1 + k] = ((5 - k) * a0 + k * a1) / 5;
            vals[6] = 0; vals[7] = 255;
        }
    }

    // ------------------------------------------------------------------ image <-> blocks

    /// <summary>Image -> block pixels (N x 16 x RGBA = N*64 bytes), padding by edge replication to whole blocks.</summary>
    public static byte[] ImageToBlocks(RgbaImage img) => ImageToBlocks(img.Pixels, img.Width, img.Height, 4);

    /// <summary>Row-major image with <paramref name="channels"/> bytes per pixel -> (N, 16, channels) blocks, edge-replicated.</summary>
    public static byte[] ImageToBlocks(byte[] px, int width, int height, int channels)
    {
        int nbx = Math.Max(1, (width + 3) / 4), nby = Math.Max(1, (height + 3) / 4);
        var outp = new byte[nbx * nby * 16 * channels];
        int o = 0;
        for (int by = 0; by < nby; by++)
            for (int bx = 0; bx < nbx; bx++)
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1);
                        int s = (sy * width + sx) * channels;
                        for (int c = 0; c < channels; c++) outp[o++] = px[s + c];
                    }
                }
        return outp;
    }

    /// <summary>Block pixels (N x 16 x RGBA) -> image cropped to width x height.</summary>
    public static RgbaImage BlocksToImage(byte[] blocks, int width, int height)
    {
        int nbx = Math.Max(1, (width + 3) / 4);
        var img = new RgbaImage(width, height);
        var dst = img.Pixels;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int b = (y >> 2) * nbx + (x >> 2);
                int s = (b * 16 + (y & 3) * 4 + (x & 3)) * 4;
                int d = (y * width + x) * 4;
                dst[d] = blocks[s]; dst[d + 1] = blocks[s + 1]; dst[d + 2] = blocks[s + 2]; dst[d + 3] = blocks[s + 3];
            }
        return img;
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// DXT data of one mip level -> RGBA image. kind: "dxt1", "dxt3", "dxt5". For DXT1 <paramref name="useAlpha"/> = false
    /// ignores the punch-through alpha (alphaDepth 0). Data shorter than the block count is an error.
    /// </summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> data, int width, int height, string kind, bool useAlpha = true, bool shiftOnly = false)
    {
        int n = BlockCount(width, height);
        int bs = BlockSize(kind);
        if (kind != "dxt1" && kind != "dxt3" && kind != "dxt5") throw new BlpException($"unknown DXT kind '{kind}'");
        if (data.Length < n * bs) throw new BlpException($"DXT level {width}x{height} needs {n * bs} bytes, has {data.Length}");
        var img = new RgbaImage(width, height);
        var dst = img.Pixels;
        int nbx = Math.Max(1, (width + 3) / 4);
        Span<int> pal = stackalloc int[16];
        Span<int> av = stackalloc int[8];
        Span<byte> alpha = stackalloc byte[16];
        for (int b = 0; b < n; b++)
        {
            var blk = data.Slice(b * bs, bs);
            ReadOnlySpan<byte> col;
            if (kind == "dxt1") col = blk;
            else
            {
                col = blk.Slice(8, 8);
                if (kind == "dxt3")
                {
                    for (int i = 0; i < 8; i++)
                    {
                        alpha[2 * i] = (byte)((blk[i] & 0x0F) * 17);
                        alpha[2 * i + 1] = (byte)((blk[i] >> 4) * 17);
                    }
                }
                else
                {
                    AlphaValues(blk[0], blk[1], av);
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)blk[2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)av[(int)((bits >> (3 * i)) & 7)];
                }
            }
            int c0 = col[0] | (col[1] << 8), c1 = col[2] | (col[3] << 8);
            uint idx = (uint)(col[4] | (col[5] << 8) | (col[6] << 16) | (col[7] << 24));
            Dxt1Palette(c0, c1, shiftOnly, pal);
            int bx = (b % nbx) * 4, by = (b / nbx) * 4;
            for (int i = 0; i < 16; i++)
            {
                int x = bx + (i & 3), y = by + (i >> 2);
                if (x >= width || y >= height) continue;
                int p = (int)((idx >> (2 * i)) & 3) * 4;
                int d = (y * width + x) * 4;
                dst[d] = (byte)pal[p]; dst[d + 1] = (byte)pal[p + 1]; dst[d + 2] = (byte)pal[p + 2];
                dst[d + 3] = kind == "dxt1" ? (useAlpha ? (byte)pal[p + 3] : (byte)255) : alpha[i];
            }
        }
        return img;
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>RGBA image -> DXT bytes of one level.</summary>
    public static byte[] Encode(RgbaImage rgba, string kind, bool useAlpha = true) =>
        EncodeBlocks(ImageToBlocks(rgba), kind, useAlpha);

    /// <summary>Block pixels (N x 16 x RGBA, N*64 bytes) -> N encoded blocks (N * BlockSize bytes).</summary>
    public static byte[] EncodeBlocks(ReadOnlySpan<byte> blocksRgba, string kind, bool useAlpha = true)
    {
        if (kind != "dxt1" && kind != "dxt3" && kind != "dxt5") throw new BlpException($"unknown DXT kind '{kind}'");
        int n = blocksRgba.Length / 64;
        int bs = BlockSize(kind);
        var outp = new byte[n * bs];
        for (int b = 0; b < n; b++)
        {
            var px = blocksRgba.Slice(b * 64, 64);
            var dst = outp.AsSpan(b * bs, bs);
            if (kind == "dxt1") EncodeColorBlock(px, useAlpha, dst);
            else
            {
                if (kind == "dxt3") EncodeDxt3AlphaBlock(px, dst);
                else EncodeDxt5AlphaBlock(px, dst);
                EncodeColorBlock(px, false, dst.Slice(8, 8));
            }
        }
        return outp;
    }

    // ---- colour

    private static double Clip255(double x) => Math.Min(Math.Max(x, 0.0), 255.0);

    private static int Pack565(double r, double g, double b)
    {
        int ri = (int)Clip255(Math.Round(r)), gi = (int)Clip255(Math.Round(g)), bi = (int)Clip255(Math.Round(b));
        return (((ri * 31 + 127) / 255) << 11) | (((gi * 63 + 127) / 255) << 5) | ((bi * 31 + 127) / 255);
    }

    /// <summary>numpy's pairwise sum of 16 doubles (8 accumulators, then a fixed tree).</summary>
    private static double Pw16(ReadOnlySpan<double> a)
    {
        double r0 = a[0] + a[8], r1 = a[1] + a[9], r2 = a[2] + a[10], r3 = a[3] + a[11];
        double r4 = a[4] + a[12], r5 = a[5] + a[13], r6 = a[6] + a[14], r7 = a[7] + a[15];
        return ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
    }

    private static void PrincipalAxis(ReadOnlySpan<double> px, ReadOnlySpan<double> w, Span<double> mean, Span<double> v)
    {
        double ws = Pw16(w);
        if (ws == 0) ws = 1;
        for (int c = 0; c < 3; c++)
        {
            double s = 0.0;
            for (int k = 0; k < 16; k++) s += px[k * 3 + c] * w[k];
            mean[c] = s / ws;
        }
        Span<double> d = stackalloc double[48];
        for (int k = 0; k < 16; k++)
        {
            double sq = Math.Sqrt(w[k]);
            for (int c = 0; c < 3; c++) d[k * 3 + c] = (px[k * 3 + c] - mean[c]) * sq;
        }
        Span<double> cov = stackalloc double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0.0;
                for (int k = 0; k < 16; k++) s = d[k * 3 + i] * d[k * 3 + j] + s;
                cov[i * 3 + j] = s;
            }
        for (int c = 0; c < 3; c++)
        {
            double big = double.NegativeInfinity, small = double.PositiveInfinity;
            for (int k = 0; k < 16; k++)
            {
                if (w[k] > 0)
                {
                    double x = px[k * 3 + c];
                    if (x > big) big = x;
                    if (x < small) small = x;
                }
            }
            double e = big - small;
            if (double.IsNaN(e) || double.IsInfinity(e)) e = 1.0;
            v[c] = e;
        }
        if ((Math.Abs(v[0]) + Math.Abs(v[1])) + Math.Abs(v[2]) < 1e-6) { v[0] = 1.0; v[1] = 1.0; v[2] = 1.0; }
        double inv3 = 1.0 / Math.Sqrt(3.0);
        Span<double> nv = stackalloc double[3];
        for (int it = 0; it < 8; it++)
        {
            for (int i = 0; i < 3; i++)
            {
                double p0 = cov[i * 3] * v[0], p1 = cov[i * 3 + 1] * v[1], p2 = cov[i * 3 + 2] * v[2];
                nv[i] = (p0 + p2) + p1;
            }
            double norm = Math.Sqrt((nv[0] * nv[0] + nv[1] * nv[1]) + nv[2] * nv[2]);
            if (norm < 1e-9) { v[0] = inv3; v[1] = inv3; v[2] = inv3; }
            else
            {
                double dn = norm == 0 ? 1 : norm;
                v[0] = nv[0] / dn; v[1] = nv[1] / dn; v[2] = nv[2] / dn;
            }
        }
    }

    /// <summary>Indices and total squared error for endpoints c0/c1 in the requested mode (before the ordering fix-up).</summary>
    private static long Eval(ReadOnlySpan<byte> rgb, ReadOnlySpan<bool> transparent, int c0, int c1, bool three, Span<int> idx)
    {
        Expand565(c0, false, out int r0, out int g0, out int b0);
        Expand565(c1, false, out int r1, out int g1, out int b1);
        Span<int> pal = stackalloc int[12];
        pal[0] = r0; pal[1] = g0; pal[2] = b0; pal[3] = r1; pal[4] = g1; pal[5] = b1;
        if (three)
        {
            pal[6] = (r0 + r1) / 2; pal[7] = (g0 + g1) / 2; pal[8] = (b0 + b1) / 2;
            pal[9] = 0; pal[10] = 0; pal[11] = 0;
        }
        else
        {
            pal[6] = (2 * r0 + r1) / 3; pal[7] = (2 * g0 + g1) / 3; pal[8] = (2 * b0 + b1) / 3;
            pal[9] = (r0 + 2 * r1) / 3; pal[10] = (g0 + 2 * g1) / 3; pal[11] = (b0 + 2 * b1) / 3;
        }
        int np = three ? 3 : 4;
        long err = 0;
        for (int k = 0; k < 16; k++)
        {
            if (transparent[k]) { idx[k] = 3; continue; }
            int r = rgb[k * 4], g = rgb[k * 4 + 1], b = rgb[k * 4 + 2];
            int best = 0, bd = int.MaxValue;
            for (int i = 0; i < np; i++)
            {
                int dr = r - pal[i * 3], dg = g - pal[i * 3 + 1], db = b - pal[i * 3 + 2];
                int dd = dr * dr + dg * dg + db * db;
                if (dd < bd) { bd = dd; best = i; }
            }
            idx[k] = best;
            err += bd;
        }
        return err;
    }

    private static readonly double[] Wa4 = { 1.0, 0.0, 2.0 / 3.0, 1.0 / 3.0 };
    private static readonly double[] Wa3 = { 1.0, 0.0, 0.5, 0.0 };

    /// <summary>Least-squares endpoints for fixed indices; false when the system is singular.</summary>
    private static bool LsEndpoints(ReadOnlySpan<double> px, ReadOnlySpan<double> w, ReadOnlySpan<int> idx, bool three, Span<double> A, Span<double> B)
    {
        Span<double> ua = stackalloc double[16];
        Span<double> ub = stackalloc double[16];
        Span<double> t = stackalloc double[16];
        var wa = three ? Wa3 : Wa4;
        for (int k = 0; k < 16; k++)
        {
            int i = idx[k];
            double alpha = wa[i];
            double beta = three ? (i == 3 ? 0.0 : 1.0 - Wa3[i]) : 1.0 - Wa4[i];
            double use = w[k] * ((three && i == 3) ? 0.0 : 1.0);
            ua[k] = use * alpha;
            ub[k] = use * beta;
        }
        for (int k = 0; k < 16; k++) t[k] = ua[k] * (three ? Wa3 : Wa4)[idx[k]];
        double aa = Pw16(t);
        for (int k = 0; k < 16; k++)
        {
            int i = idx[k];
            double beta = three ? (i == 3 ? 0.0 : 1.0 - Wa3[i]) : 1.0 - Wa4[i];
            t[k] = ub[k] * beta;
        }
        double bb = Pw16(t);
        for (int k = 0; k < 16; k++)
        {
            int i = idx[k];
            double beta = three ? (i == 3 ? 0.0 : 1.0 - Wa3[i]) : 1.0 - Wa4[i];
            t[k] = ua[k] * beta;
        }
        double ab = Pw16(t);
        double det = aa * bb - ab * ab;
        bool ok = Math.Abs(det) > 1e-9;
        if (!ok) det = 1.0;
        for (int c = 0; c < 3; c++)
        {
            double ax = 0.0, bx = 0.0;
            for (int k = 0; k < 16; k++) { ax += ua[k] * px[k * 3 + c]; }
            for (int k = 0; k < 16; k++) { bx += ub[k] * px[k * 3 + c]; }
            A[c] = Clip255((ax * bb - bx * ab) / det);
            B[c] = Clip255((bx * aa - ax * ab) / det);
        }
        return ok;
    }

    /// <summary>One DXT1 colour block (8 bytes) from 16 RGBA pixels. With <paramref name="alphaMode"/>, pixels with
    /// alpha &lt; 128 are transparent and the block uses the 3-colour mode.</summary>
    private static void EncodeColorBlock(ReadOnlySpan<byte> blk, bool alphaMode, Span<byte> dst)
    {
        Span<double> px = stackalloc double[48];
        Span<double> w = stackalloc double[16];
        Span<bool> tr = stackalloc bool[16];
        bool three = false, allt = true;
        for (int k = 0; k < 16; k++)
        {
            px[k * 3] = blk[k * 4]; px[k * 3 + 1] = blk[k * 4 + 1]; px[k * 3 + 2] = blk[k * 4 + 2];
            bool t = alphaMode && blk[k * 4 + 3] < 128;
            tr[k] = t; three |= t; allt &= t;
            w[k] = t ? 0.0 : 1.0;
        }
        Span<double> mean = stackalloc double[3];
        Span<double> axis = stackalloc double[3];
        PrincipalAxis(px, w, mean, axis);
        double tmax = double.NegativeInfinity, tmin = double.PositiveInfinity;
        for (int k = 0; k < 16; k++)
        {
            if (!(w[k] > 0)) continue;
            double t = ((px[k * 3] - mean[0]) * axis[0] + (px[k * 3 + 1] - mean[1]) * axis[1]) + (px[k * 3 + 2] - mean[2]) * axis[2];
            if (t > tmax) tmax = t;
            if (t < tmin) tmin = t;
        }
        if (double.IsInfinity(tmax)) tmax = 0;
        if (double.IsInfinity(tmin)) tmin = 0;
        double inset = (tmax - tmin) / 16.0;
        double hi = tmax - inset, lo = tmin + inset;
        int c0 = Pack565(Clip255(mean[0] + hi * axis[0]), Clip255(mean[1] + hi * axis[1]), Clip255(mean[2] + hi * axis[2]));
        int c1 = Pack565(Clip255(mean[0] + lo * axis[0]), Clip255(mean[1] + lo * axis[1]), Clip255(mean[2] + lo * axis[2]));
        Span<int> idx = stackalloc int[16];
        Span<int> nidx = stackalloc int[16];
        long err = Eval(blk, tr, c0, c1, three, idx);
        Span<double> A = stackalloc double[3];
        Span<double> B = stackalloc double[3];
        for (int round = 0; round < 2; round++)
        {
            bool ok = LsEndpoints(px, w, idx, three, A, B);
            int n0 = ok ? Pack565(A[0], A[1], A[2]) : c0;
            int n1 = ok ? Pack565(B[0], B[1], B[2]) : c1;
            long nerr = Eval(blk, tr, n0, n1, three, nidx);
            if (nerr < err)
            {
                c0 = n0; c1 = n1; err = nerr;
                nidx.CopyTo(idx);
            }
        }
        // mode fix-up: 4-colour needs c0 > c1, 3-colour needs c0 <= c1
        if (!three && c0 < c1)
        {
            (c0, c1) = (c1, c0);
            for (int k = 0; k < 16; k++) idx[k] ^= 1;               // remap 0<->1, 2<->3
        }
        else if (three && c0 > c1)
        {
            (c0, c1) = (c1, c0);
            for (int k = 0; k < 16; k++) if (idx[k] < 2) idx[k] ^= 1;   // remap 0<->1, keep 2 and 3
        }
        // equal endpoints in an opaque block: 3-colour mode by definition; index 0 everywhere is exact
        if (!three && c0 == c1) idx.Clear();
        if (allt) { c0 = 0; c1 = 0; idx.Fill(3); }
        uint bits = 0;
        for (int k = 0; k < 16; k++) bits |= (uint)idx[k] << (2 * k);
        dst[0] = (byte)(c0 & 0xFF); dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)(c1 & 0xFF); dst[3] = (byte)(c1 >> 8);
        dst[4] = (byte)bits; dst[5] = (byte)(bits >> 8); dst[6] = (byte)(bits >> 16); dst[7] = (byte)(bits >> 24);
    }

    // ---- alpha

    private static void EncodeDxt3AlphaBlock(ReadOnlySpan<byte> blk, Span<byte> dst)
    {
        for (int i = 0; i < 8; i++)
        {
            int q0 = Math.Min(15, (blk[(2 * i) * 4 + 3] + 8) / 17);
            int q1 = Math.Min(15, (blk[(2 * i + 1) * 4 + 3] + 8) / 17);
            dst[i] = (byte)(q0 | (q1 << 4));
        }
    }

    private static long AlphaTry(ReadOnlySpan<int> a, int a0, int a1, Span<int> idx)
    {
        Span<int> vals = stackalloc int[8];
        AlphaValues(a0, a1, vals);
        long err = 0;
        for (int k = 0; k < 16; k++)
        {
            int best = 0, bd = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                int d = Math.Abs(a[k] - vals[i]);
                if (d < bd) { bd = d; best = i; }
            }
            idx[k] = best;
            err += (long)bd * bd;
        }
        return err;
    }

    private static void EncodeDxt5AlphaBlock(ReadOnlySpan<byte> blk, Span<byte> dst)
    {
        Span<int> a = stackalloc int[16];
        int hi = 0, lo = 255, mhi = -1, mlo = 256;
        for (int k = 0; k < 16; k++)
        {
            int v = blk[k * 4 + 3];
            a[k] = v;
            if (v > hi) hi = v;
            if (v < lo) lo = v;
            if (v > 0 && v < 255) { if (v > mhi) mhi = v; if (v < mlo) mlo = v; }
        }
        // 8-value mode: a0 > a1
        Span<int> idx8 = stackalloc int[16];
        Span<int> idx6 = stackalloc int[16];
        bool same = hi == lo;
        long err8 = AlphaTry(a, hi, lo, idx8);
        if (same) { idx8.Clear(); err8 = 0; }
        // 6-value mode (a0 <= a1) with explicit 0 and 255, endpoints from the values in between
        bool hasMid = mhi >= 0;
        int b0 = hasMid ? mlo : 0, b1 = hasMid ? mhi : 0;
        long err6 = AlphaTry(a, b0, b1, idx6);
        bool use6 = err6 < err8 && !same;
        int e0 = use6 ? b0 : hi, e1 = use6 ? b1 : lo;
        var idx = use6 ? idx6 : idx8;
        ulong bits = 0;
        for (int i = 0; i < 16; i++) bits |= (ulong)idx[i] << (3 * i);
        dst[0] = (byte)e0; dst[1] = (byte)e1;
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }
}
