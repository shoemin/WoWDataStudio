using System.Buffers.Binary;
using System.Text.Json.Serialization;
using WoWData.Core.Imaging;

namespace WoWData.Core.Blp;

/// <summary>Palette policy of <see cref="BlpEncoder.EncodeLike"/> for palette files.</summary>
public enum PaletteMode { Auto, Keep, New }

/// <summary>One mip level of an <see cref="EncodeReport"/> (Python per_level entry).</summary>
public sealed class LevelReport
{
    [JsonPropertyName("level")] public int Level { get; init; }
    /// <summary>"WxH" of the level.</summary>
    [JsonPropertyName("size")] public string Size { get; init; } = "";
    /// <summary>DXT: number of blocks the file stores for this level (null for palette files).</summary>
    [JsonPropertyName("blocks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Blocks { get; init; }
    /// <summary>Palette: number of pixels of this level (null for DXT files).</summary>
    [JsonPropertyName("pixels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Pixels { get; init; }
    /// <summary>Blocks (DXT) or pixels (palette) that were re-encoded.</summary>
    [JsonPropertyName("re_encoded")] public int ReEncoded { get; init; }
}

/// <summary>What <see cref="BlpEncoder.EncodeLike"/> did (the Python EncodeReport dict).</summary>
public sealed class EncodeReport
{
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    /// <summary>The image size differed from the original: a new file was written in the original's format.</summary>
    [JsonPropertyName("resized")] public bool Resized { get; set; }
    /// <summary>The output bytes equal the original file.</summary>
    [JsonPropertyName("identical")] public bool Identical { get; set; }
    /// <summary>Changed pixels of level 0 (0 for the resized path, where Python does not report it).</summary>
    [JsonPropertyName("changed_pixels")] public int ChangedPixels { get; set; }
    [JsonPropertyName("levels")] public int Levels { get; set; }
    [JsonPropertyName("per_level")] public List<LevelReport> PerLevel { get; set; } = new();
    /// <summary>Palette files: "keep" or "new".</summary>
    [JsonPropertyName("palette"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Palette { get; set; }
    /// <summary>Palette auto mode: RMS error of the changed pixels with the original palette (rounded to 2 decimals).</summary>
    [JsonPropertyName("palette_rms_keep"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? PaletteRmsKeep { get; set; }
}

/// <summary>
/// BLP2 writing (port of wowdata/blp.py): encode an edited image back into an original file's exact format and mip
/// layout re-encoding only what changed, write new files, and check what the 1.12 client accepts.
/// </summary>
/// <remarks>
/// Encoding policy ("EncodeLike"): start from the original file's bytes and replace only what changed.
///  * DXT: a 4x4 block whose pixels are unchanged keeps its original bytes; changed blocks are re-encoded. Lower mips
///    are rebuilt from the edited level 0 (2x2 box filter) only where their footprint changed.
///  * Palette: unchanged pixels keep their original index and alpha bits; changed pixels get the nearest colour of
///    the original palette, or (when that is too far off) a new 256-colour palette is built.
/// So an unmodified image gives a byte-identical BLP, and an edit only touches the blocks it covers.
/// </remarks>
public static class BlpEncoder
{
    private static readonly (string Name, byte Enc, byte Depth, byte AEnc)[] FormatTable =
    {
        ("dxt1", BlpFile.EncDxt, 0, 0), ("dxt1a", BlpFile.EncDxt, 1, 0), ("dxt3", BlpFile.EncDxt, 8, 1), ("dxt5", BlpFile.EncDxt, 8, 7),
        ("pal", BlpFile.EncPalette, 0, 8), ("pal1", BlpFile.EncPalette, 1, 8), ("pal4", BlpFile.EncPalette, 4, 8), ("pal8", BlpFile.EncPalette, 8, 8),
    };

    /// <summary>The writable formats: dxt1 dxt1a dxt3 dxt5 pal pal1 pal4 pal8.</summary>
    public static readonly IReadOnlyList<string> Formats = FormatTable.Select(f => f.Name).ToArray();

    // ------------------------------------------------------------------ image helpers

    /// <summary>2x2 box filter with round-half-to-even (a dimension already at 1 is kept). For an odd dimension the
    /// last row/column is dropped (numpy fails or broadcasts there; only non-power-of-two images get there).</summary>
    internal static RgbaImage Downsample(RgbaImage img)
    {
        int w = img.Width, h = img.Height;
        int nw = w > 1 ? w / 2 : 1, nh = h > 1 ? h / 2 : 1;
        int sx = w > 1 ? 2 : 1, sy = h > 1 ? 2 : 1;
        var src = img.Pixels;
        var dst = new byte[nw * nh * 4];
        int div = sx * sy;
        for (int y = 0; y < nh; y++)
        {
            int r0 = y * sy, r1 = sy == 2 ? r0 + 1 : r0;
            for (int x = 0; x < nw; x++)
            {
                int c0 = x * sx, c1 = sx == 2 ? c0 + 1 : c0;
                int a = (r0 * w + c0) * 4, b = (r0 * w + c1) * 4, c = (r1 * w + c0) * 4, d = (r1 * w + c1) * 4;
                int o = (y * nw + x) * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    int s = div == 4 ? src[a + ch] + src[b + ch] + src[c + ch] + src[d + ch]
                          : div == 2 ? (sx == 2 ? src[a + ch] + src[b + ch] : src[a + ch] + src[c + ch])
                          : 2 * src[a + ch];
                    // the float maths of numpy ((a+b)/2 + (c+d)/2)/2 is exact, so this is rint(s / div)
                    dst[o + ch] = (byte)Math.Round(s / (double)(div == 1 ? 2 : div), MidpointRounding.ToEven);
                }
            }
        }
        return new RgbaImage(nw, nh, dst);
    }

    internal static bool[] DownsampleMask(bool[] m, int w, int h, out int nw, out int nh)
    {
        nw = w > 1 ? w / 2 : 1;
        nh = h > 1 ? h / 2 : 1;
        int sx = w > 1 ? 2 : 1, sy = h > 1 ? 2 : 1;
        var outm = new bool[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int r0 = y * sy, r1 = sy == 2 ? r0 + 1 : r0, c0 = x * sx, c1 = sx == 2 ? c0 + 1 : c0;
                outm[y * nw + x] = m[r0 * w + c0] | m[r0 * w + c1] | m[r1 * w + c0] | m[r1 * w + c1];
            }
        return outm;
    }

    internal static List<RgbaImage> BuildChain(RgbaImage level0, int count, bool binaryAlpha)
    {
        var chain = new List<RgbaImage> { level0 };
        for (int i = 1; i < count; i++)
        {
            var nxt = Downsample(chain[^1]);
            if (binaryAlpha)
            {
                var p = nxt.Pixels;
                for (int j = 3; j < p.Length; j += 4) p[j] = p[j] >= 128 ? (byte)255 : (byte)0;
            }
            chain.Add(nxt);
        }
        return chain;
    }

    private static bool[] Changed(RgbaImage a, RgbaImage b, bool compareAlpha)
    {
        var pa = a.Pixels; var pb = b.Pixels;
        var m = new bool[a.Width * a.Height];
        for (int i = 0; i < m.Length; i++)
        {
            int o = i * 4;
            m[i] = pa[o] != pb[o] || pa[o + 1] != pb[o + 1] || pa[o + 2] != pb[o + 2] || (compareAlpha && pa[o + 3] != pb[o + 3]);
        }
        return m;
    }

    private static int PackedAlphaSize(int n, int depth) => depth switch
    {
        0 => 0, 1 => (n + 7) / 8, 4 => (n + 1) / 2, 8 => n,
        _ => throw new BlpException($"unsupported alpha depth {depth}"),
    };

    /// <summary>Alpha plane (one byte per pixel) -> stored bits (1 bit LSB first / 4 bits low nibble first / 8 bits).</summary>
    private static void PackAlpha(ReadOnlySpan<byte> alpha, int depth, Span<byte> dst)
    {
        int n = alpha.Length;
        switch (depth)
        {
            case 0: return;
            case 1:
                dst.Slice(0, (n + 7) / 8).Clear();
                for (int i = 0; i < n; i++) if (alpha[i] >= 128) dst[i >> 3] |= (byte)(1 << (i & 7));
                return;
            case 4:
                dst.Slice(0, (n + 1) / 2).Clear();
                for (int i = 0; i < n; i++)
                {
                    int q = Math.Min(15, (alpha[i] + 8) / 17);
                    dst[i >> 1] |= (byte)(q << (4 * (i & 1)));
                }
                return;
            case 8:
                alpha.CopyTo(dst);
                return;
            default:
                throw new BlpException($"unsupported alpha depth {depth}");
        }
    }

    /// <summary>The alpha values the given depth can store (what the client will see).</summary>
    private static byte QuantizeAlpha(byte a, int depth) => depth switch
    {
        0 => 255,
        1 => a >= 128 ? (byte)255 : (byte)0,
        4 => (byte)(Math.Min(15, (a + 8) / 17) * 17),
        _ => a,
    };

    /// <summary>Nearest palette entry (first index on ties) by squared RGB distance.</summary>
    private sealed class PaletteMatcher
    {
        private readonly int[] _pal = new int[768];
        private readonly Dictionary<int, int> _cache = new();

        public PaletteMatcher(ReadOnlySpan<byte> bgra)
        {
            for (int i = 0; i < 256; i++) { _pal[i * 3] = bgra[i * 4 + 2]; _pal[i * 3 + 1] = bgra[i * 4 + 1]; _pal[i * 3 + 2] = bgra[i * 4]; }
        }

        /// <summary>Returns the index; <paramref name="err"/> = squared error.</summary>
        public int Nearest(int r, int g, int b, out int err)
        {
            int key = (r << 16) | (g << 8) | b;
            if (_cache.TryGetValue(key, out int packed)) { err = packed >> 8; return packed & 0xFF; }
            int best = 0, bd = int.MaxValue;
            var p = _pal;
            for (int i = 0; i < 256; i++)
            {
                int dr = r - p[i * 3], dg = g - p[i * 3 + 1], db = b - p[i * 3 + 2];
                int d = dr * dr + dg * dg + db * db;
                if (d < bd) { bd = d; best = i; if (d == 0) break; }
            }
            _cache[key] = (bd << 8) | best;
            err = bd;
            return best;
        }
    }

    /// <summary>
    /// New 256-colour palette for the RGB of an image (BGRA, alpha byte = <paramref name="paletteAlpha"/>).
    /// Python uses Pillow's median cut (no dithering). This is an independent median cut, not byte-identical to Pillow:
    /// the box with the most pixels is split on its widest channel at the pixel-weighted median, and each box's colour
    /// is its pixel-weighted mean. With 256 colours or fewer, the colours are kept exactly. Unused entries are zero.
    /// </summary>
    internal static byte[] MakePalette(RgbaImage img, byte paletteAlpha)
    {
        var px = img.Pixels;
        int n = img.Width * img.Height;
        var keys = new int[n];
        for (int i = 0; i < n; i++) keys[i] = (px[i * 4] << 16) | (px[i * 4 + 1] << 8) | px[i * 4 + 2];
        Array.Sort(keys);
        var colors = new List<int>();
        var counts = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (i > 0 && keys[i] == keys[i - 1]) counts[^1]++;
            else { colors.Add(keys[i]); counts.Add(1); }
        }
        var cols = colors.ToArray();
        var cnts = counts.ToArray();
        var pal = new byte[1024];
        for (int i = 0; i < 256; i++) pal[i * 4 + 3] = paletteAlpha;
        var boxes = new List<(int Start, int End, long Pixels)> { (0, cols.Length, n) };
        while (boxes.Count < 256)
        {
            int pick = -1; long most = -1;
            for (int i = 0; i < boxes.Count; i++)
                if (boxes[i].End - boxes[i].Start > 1 && boxes[i].Pixels > most) { most = boxes[i].Pixels; pick = i; }
            if (pick < 0) break;
            var (s, e, total) = boxes[pick];
            int[] lo = { 255, 255, 255 }, hi = { 0, 0, 0 };
            for (int i = s; i < e; i++)
                for (int c = 0; c < 3; c++)
                {
                    int v = (cols[i] >> (16 - 8 * c)) & 0xFF;
                    if (v < lo[c]) lo[c] = v;
                    if (v > hi[c]) hi[c] = v;
                }
            int ch = 0;
            for (int c = 1; c < 3; c++) if (hi[c] - lo[c] > hi[ch] - lo[ch]) ch = c;
            int shift = 16 - 8 * ch;
            // sort the box's colours (with their counts) by the chosen channel, stable on the packed value
            var order = new int[e - s];
            for (int i = 0; i < order.Length; i++) order[i] = s + i;
            Array.Sort(order, (x, y) =>
            {
                int vx = (cols[x] >> shift) & 0xFF, vy = (cols[y] >> shift) & 0xFF;
                return vx != vy ? vx.CompareTo(vy) : cols[x].CompareTo(cols[y]);
            });
            var nc = new int[order.Length]; var nn = new int[order.Length];
            for (int i = 0; i < order.Length; i++) { nc[i] = cols[order[i]]; nn[i] = cnts[order[i]]; }
            nc.CopyTo(cols, s); nn.CopyTo(cnts, s);
            long cum = 0, half = (total + 1) / 2;
            int split = s + 1;
            for (int i = s; i < e - 1; i++)
            {
                cum += cnts[i];
                split = i + 1;
                if (cum >= half) break;
            }
            long left = 0;
            for (int i = s; i < split; i++) left += cnts[i];
            boxes[pick] = (s, split, left);
            boxes.Add((split, e, total - left));
        }
        for (int bi = 0; bi < boxes.Count; bi++)
        {
            var (s, e, total) = boxes[bi];
            long r = 0, g = 0, b = 0;
            for (int i = s; i < e; i++)
            {
                r += (long)((cols[i] >> 16) & 0xFF) * cnts[i];
                g += (long)((cols[i] >> 8) & 0xFF) * cnts[i];
                b += (long)(cols[i] & 0xFF) * cnts[i];
            }
            pal[bi * 4 + 2] = (byte)((r + total / 2) / total);
            pal[bi * 4 + 1] = (byte)((g + total / 2) / total);
            pal[bi * 4] = (byte)((b + total / 2) / total);
        }
        return pal;
    }

    /// <summary>Most common alpha byte of a BGRA palette (smallest value on ties, like np.bincount().argmax()).</summary>
    private static byte CommonPaletteAlpha(ReadOnlySpan<byte> bgra)
    {
        Span<int> hist = stackalloc int[256];
        for (int i = 0; i < 256; i++) hist[bgra[i * 4 + 3]]++;
        int best = 0;
        for (int v = 1; v < 256; v++) if (hist[v] > hist[best]) best = v;
        return (byte)best;
    }

    // ------------------------------------------------------------------ encode like an original

    /// <summary>
    /// Encodes an image into the original BLP's format. Same size: the original file is copied and only changed
    /// blocks / pixels are rewritten. Different size: a new file with the original's encoding, alpha depth/encoding
    /// and mip policy. <paramref name="mode"/> applies to palette encodings only.
    /// </summary>
    public static (byte[] Data, EncodeReport Report) EncodeLike(BlpFile orig, RgbaImage rgba, PaletteMode mode = PaletteMode.Auto, double maxPaletteRms = 6.0)
    {
        var rep = new EncodeReport { Format = orig.FormatName, Width = rgba.Width, Height = rgba.Height };
        if (rgba.Width != orig.Width || rgba.Height != orig.Height)
        {
            rep.Resized = true;
            var data = EncodeNew(rgba, FormatOf(orig), mips: orig.HasMips != 0, template: orig);
            rep.Levels = BlpFile.Parse(data).LevelCount;
            rep.Identical = false;
            return (data, rep);
        }
        if (orig.Encoding == BlpFile.EncDxt) return EncodeLikeDxt(orig, rgba, rep);
        if (orig.Encoding == BlpFile.EncPalette) return EncodeLikePalette(orig, rgba, rep, mode, maxPaletteRms);
        throw new BlpException($"encoding {orig.Encoding} cannot be written (the 1.12 client only reads palette and DXT)");
    }

    private static (byte[], EncodeReport) EncodeLikeDxt(BlpFile orig, RgbaImage rgba, EncodeReport rep)
    {
        string kind = orig.DxtKind ?? throw new BlpException($"unknown DXT alpha encoding {orig.AlphaEncoding}");
        bool useAlpha = orig.AlphaDepth > 0;
        var outp = (byte[])orig.Data.Clone();
        int levels = orig.LevelCount;
        int bs = Dxt.BlockSize(kind);
        var new0 = rgba.Clone();
        if (!useAlpha) for (int i = 3; i < new0.Pixels.Length; i += 4) new0.Pixels[i] = 255;
        var orig0 = orig.Decode(0);
        var mask = Changed(orig0, new0, useAlpha);
        if (kind == "dxt1" && useAlpha)
        {
            // colour under fully transparent pixels is not stored by DXT1 (index 3 = transparent black):
            // an editor changing it is not an edit
            var po = orig0.Pixels; var pn = new0.Pixels;
            for (int i = 0; i < mask.Length; i++) if (po[i * 4 + 3] < 128 && pn[i * 4 + 3] < 128) mask[i] = false;
        }
        int changedPixels = 0;
        foreach (bool b in mask) if (b) changedPixels++;
        rep.ChangedPixels = changedPixels;
        rep.Levels = levels;
        // lower levels are only built when some change reaches them (the result is the same: unchanged levels are kept)
        List<RgbaImage>? chain = null;
        int mw = orig.Width, mh = orig.Height;
        for (int lv = 0; lv < levels; lv++)
        {
            var (lw, lh) = BlpFile.LevelDims(orig.Width, orig.Height, lv);
            if (lv > 0) mask = DownsampleMask(mask, mw, mh, out mw, out mh);
            var mblocks = Dxt.ImageToBlocks(MaskBytes(mask), mw, mh, 1);
            int nb = mblocks.Length / 16;
            // Blizzard's converter stored only max(1, (w/4)*(h/4)) blocks (16x2 -> 1 block instead of 4): 628 shipped
            // DXT3 files do that. Keep their layout: only the blocks the file has are written.
            int stored = (int)Math.Min(nb, orig.MipSizes[lv] / (uint)bs);
            var bmask = new bool[stored];
            int nchg = 0;
            for (int i = 0; i < stored; i++)
            {
                bool any = false;
                for (int k = 0; k < 16; k++) any |= mblocks[i * 16 + k] != 0;
                bmask[i] = any;
                if (any) nchg++;
            }
            rep.PerLevel.Add(new LevelReport { Level = lv, Size = $"{lw}x{lh}", Blocks = stored, ReEncoded = nchg });
            if (nchg == 0) continue;
            chain ??= BuildChain(new0, levels, binaryAlpha: kind == "dxt1");
            var blocks = Dxt.ImageToBlocks(chain[lv]);
            var sel = new byte[nchg * 64];
            for (int i = 0, j = 0; i < stored; i++)
                if (bmask[i]) { Array.Copy(blocks, i * 64, sel, j * 64, 64); j++; }
            var enc = Dxt.EncodeBlocks(sel, kind, useAlpha);
            long off = orig.MipOffsets[lv];
            if (off + (long)stored * bs > outp.Length) throw new BlpException($"level {lv} lies outside the original file");
            for (int i = 0, j = 0; i < stored; i++)
                if (bmask[i]) { Array.Copy(enc, j * bs, outp, off + (long)i * bs, bs); j++; }
        }
        rep.Identical = outp.AsSpan().SequenceEqual(orig.Data);
        return (outp, rep);
    }

    private static byte[] MaskBytes(bool[] m)
    {
        var b = new byte[m.Length];
        for (int i = 0; i < m.Length; i++) b[i] = m[i] ? (byte)1 : (byte)0;
        return b;
    }

    private static (byte[], EncodeReport) EncodeLikePalette(BlpFile orig, RgbaImage rgba, EncodeReport rep, PaletteMode paletteMode, double maxRms)
    {
        int depth = orig.AlphaDepth;
        int levels = orig.LevelCount;
        var new0 = rgba.Clone();
        for (int i = 3; i < new0.Pixels.Length; i += 4) new0.Pixels[i] = QuantizeAlpha(new0.Pixels[i], depth);
        var orig0 = orig.Decode(0);
        var mask0 = Changed(orig0, new0, depth > 0);
        var chain = BuildChain(new0, levels, binaryAlpha: depth == 1);
        int changedPixels = 0;
        foreach (bool b in mask0) if (b) changedPixels++;
        rep.ChangedPixels = changedPixels;
        rep.Levels = levels;
        var pal = orig.PaletteRaw.ToArray();
        var matcher = new PaletteMatcher(pal);
        // decide the palette
        bool newPalette = paletteMode == PaletteMode.New;
        if (paletteMode == PaletteMode.Auto && changedPixels > 0)
        {
            double sum = 0;
            var p = new0.Pixels;
            for (int i = 0; i < mask0.Length; i++)
                if (mask0[i]) { matcher.Nearest(p[i * 4], p[i * 4 + 1], p[i * 4 + 2], out int e); sum += e; }
            double rms = Math.Sqrt(sum / changedPixels / 3.0);
            rep.PaletteRmsKeep = Math.Round(rms, 2);
            if (rms > maxRms) newPalette = true;
        }
        rep.Palette = newPalette ? "new" : "keep";
        var outp = (byte[])orig.Data.Clone();
        if (newPalette)
        {
            pal = MakePalette(new0, CommonPaletteAlpha(pal));
            matcher = new PaletteMatcher(pal);
            pal.CopyTo(outp, BlpFile.PaletteOffset);
        }
        var mask = mask0;
        int mw = orig.Width, mh = orig.Height;
        for (int lv = 0; lv < levels; lv++)
        {
            var (lw, lh) = BlpFile.LevelDims(orig.Width, orig.Height, lv);
            if (lv > 0) mask = DownsampleMask(mask, mw, mh, out mw, out mh);
            var img = chain[lv];
            if (img.Width != lw || img.Height != lh) throw new BlpException($"level {lv}: mip size {img.Width}x{img.Height} differs from {lw}x{lh}");
            var ip = img.Pixels;
            int n = lw * lh;
            var (idx, alpha) = orig.PalettePlanes(orig.MipData(lv), lw, lh);
            int nchg;
            if (newPalette)
            {
                for (int i = 0; i < n; i++)
                {
                    idx[i] = (byte)matcher.Nearest(ip[i * 4], ip[i * 4 + 1], ip[i * 4 + 2], out _);
                    alpha[i] = ip[i * 4 + 3];
                }
                nchg = n;
            }
            else
            {
                nchg = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!mask[i]) continue;
                    idx[i] = (byte)matcher.Nearest(ip[i * 4], ip[i * 4 + 1], ip[i * 4 + 2], out _);
                    alpha[i] = ip[i * 4 + 3];
                    nchg++;
                }
            }
            rep.PerLevel.Add(new LevelReport { Level = lv, Size = $"{lw}x{lh}", Pixels = n, ReEncoded = nchg });
            int payloadLen = n + PackedAlphaSize(n, depth);
            uint off = orig.MipOffsets[lv], size = orig.MipSizes[lv];
            if (payloadLen != size) throw new BlpException($"level {lv}: payload {payloadLen} bytes, original mip {size} bytes");
            if (off + (long)size > outp.Length) throw new BlpException($"level {lv} lies outside the original file");
            idx.CopyTo(outp, off);
            PackAlpha(alpha, depth, outp.AsSpan((int)off + n, payloadLen - n));
        }
        rep.Identical = outp.AsSpan().SequenceEqual(orig.Data);
        return (outp, rep);
    }

    // ------------------------------------------------------------------ new files

    /// <summary>The writable format name of a file (dxt1 dxt1a dxt3 dxt5 pal pal1 pal4 pal8).</summary>
    public static string FormatOf(BlpFile b)
    {
        foreach (var (name, e, d, a) in FormatTable)
            if (e == b.Encoding && (e == BlpFile.EncPalette || a == b.AlphaEncoding) && d == b.AlphaDepth) return name;
        if (b.Encoding == BlpFile.EncDxt)
        {
            switch (b.AlphaEncoding)
            {
                case 0: return b.AlphaDepth != 0 ? "dxt1a" : "dxt1";
                case 1: return "dxt3";
                case 7: return "dxt5";
            }
        }
        if (b.Encoding == BlpFile.EncPalette)
        {
            switch (b.AlphaDepth)
            {
                case 0: return "pal";
                case 1: return "pal1";
                case 4: return "pal4";
                case 8: return "pal8";
            }
        }
        throw new BlpException($"no writable format for encoding {b.Encoding} (alpha depth {b.AlphaDepth}, alpha encoding {b.AlphaEncoding})");
    }

    /// <summary>dxt1 for an opaque image, dxt1a when alpha is only 0/255, else dxt5.</summary>
    public static string SuggestFormat(RgbaImage rgba)
    {
        var p = rgba.Pixels;
        bool opaque = true, binary = true;
        for (int i = 3; i < p.Length; i += 4)
        {
            if (p[i] != 255) opaque = false;
            if (p[i] != 0 && p[i] != 255) { binary = false; break; }
        }
        return opaque ? "dxt1" : binary ? "dxt1a" : "dxt5";
    }

    /// <summary>
    /// A complete new BLP2 file in <paramref name="fmt"/> (see <see cref="Formats"/>). With <paramref name="template"/>,
    /// its header bytes that are not computed (type, alphaEncoding of palette files, DXT palette block) are copied.
    /// </summary>
    public static byte[] EncodeNew(RgbaImage rgba, string fmt, bool mips = true, BlpFile? template = null)
    {
        int fi = Array.FindIndex(FormatTable, f => f.Name == fmt);
        if (fi < 0) throw new BlpException($"unknown format '{fmt}' (choose from {string.Join(", ", Formats)})");
        var (_, enc, depth, aenc) = FormatTable[fi];
        if (template != null && template.Encoding == enc && template.AlphaDepth == depth) aenc = template.AlphaEncoding;
        int w = rgba.Width, h = rgba.Height;
        int levels = Math.Min(mips ? BlpFile.LevelsFor(w, h) : 1, 16);
        var payloads = new List<byte[]>();
        byte[] palette;
        if (enc == BlpFile.EncDxt)
        {
            string kind = BlpFile.KindOf(aenc) ?? throw new BlpException($"unknown DXT alpha encoding {aenc}");
            var chain = BuildChain(rgba, levels, binaryAlpha: kind == "dxt1");
            foreach (var img in chain) payloads.Add(Dxt.Encode(img, kind, useAlpha: depth > 0));
            palette = template != null && template.Encoding == BlpFile.EncDxt ? template.PaletteRaw.ToArray() : new byte[1024];
        }
        else
        {
            var q = rgba.Clone();
            for (int i = 3; i < q.Pixels.Length; i += 4) q.Pixels[i] = QuantizeAlpha(q.Pixels[i], depth);
            byte alphaByte = 0;
            if (template != null && template.Encoding == BlpFile.EncPalette) alphaByte = CommonPaletteAlpha(template.PaletteRaw);
            var pal = MakePalette(q, alphaByte);
            var matcher = new PaletteMatcher(pal);
            var chain = BuildChain(q, levels, binaryAlpha: depth == 1);
            foreach (var img in chain)
            {
                int n = img.Width * img.Height;
                var payload = new byte[n + PackedAlphaSize(n, depth)];
                var alpha = new byte[n];
                var ip = img.Pixels;
                for (int i = 0; i < n; i++)
                {
                    payload[i] = (byte)matcher.Nearest(ip[i * 4], ip[i * 4 + 1], ip[i * 4 + 2], out _);
                    alpha[i] = ip[i * 4 + 3];
                }
                PackAlpha(alpha, depth, payload.AsSpan(n));
                payloads.Add(payload);
            }
            palette = pal;
        }
        int total = BlpFile.HeaderSize;
        foreach (var p in payloads) total += p.Length;
        var data = new byte[total];
        data[0] = (byte)'B'; data[1] = (byte)'L'; data[2] = (byte)'P'; data[3] = (byte)'2';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), template?.Type ?? 1u);
        data[8] = enc; data[9] = depth; data[10] = aenc; data[11] = mips ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C), (uint)w);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10), (uint)h);
        int pos = BlpFile.HeaderSize;
        for (int i = 0; i < payloads.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14 + 4 * i), (uint)pos);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x54 + 4 * i), (uint)payloads[i].Length);
            payloads[i].CopyTo(data, pos);
            pos += payloads[i].Length;
        }
        palette.AsSpan(0, 1024).CopyTo(data.AsSpan(BlpFile.PaletteOffset));
        return data;
    }

    // ------------------------------------------------------------------ client compatibility

    /// <summary>
    /// Problems that would stop the 1.12 client from using the file (empty = OK); same rules and messages as Python validate().
    /// Rules from the decompile: BlpImage_SetData requires 'BLP2' and type 1; Texture_ChooseBlpGxFormat maps
    /// alphaEncoding 0/1/7 to DXT1/3/5 and 8 to palette formats by alphaDepth; the client computes the mip count from
    /// the dimensions (MipBits_GetLevelCount), so a file with hasMips must store the full chain down to 1x1;
    /// BlpImage_IsValidDimension allows powers of two 1..512.
    /// </summary>
    public static IReadOnlyList<string> Validate(byte[] data)
    {
        var problems = new List<string>();
        BlpFile b;
        try { b = BlpFile.Parse(data); }
        catch (BlpException e) { return new[] { e.Message }; }
        if (b.Type != 1) problems.Add($"type is {b.Type}, the client requires 1");
        if (b.Encoding != BlpFile.EncPalette && b.Encoding != BlpFile.EncDxt)
            problems.Add($"encoding {b.Encoding} is not handled by the 1.12 client (only 1 = palette, 2 = DXT)");
        if (b.Encoding == BlpFile.EncDxt && BlpFile.KindOf(b.AlphaEncoding) == null)
            problems.Add($"DXT with alphaEncoding {b.AlphaEncoding} (expected 0, 1 or 7)");
        if (b.Encoding == BlpFile.EncDxt && b.AlphaEncoding == 0 && b.AlphaDepth != 0 && b.AlphaDepth != 1)
            problems.Add($"DXT1 with alphaDepth {b.AlphaDepth} (expected 0 or 1)");
        bool knownDepth = b.AlphaDepth is 0 or 1 or 4 or 8;
        if (b.Encoding == BlpFile.EncPalette && !knownDepth)
            problems.Add($"palette alphaDepth {b.AlphaDepth} (expected 0, 1, 4, 8)");
        if (!(ImageOps.IsPow2(b.Width) && ImageOps.IsPow2(b.Height)))
            problems.Add($"size {b.Width}x{b.Height} is not a power of two");
        if (b.Width > 512 || b.Height > 512)
            problems.Add($"size {b.Width}x{b.Height} is above 512 (BlpImage_IsValidDimension limit; risky)");
        // Python would index past the 16-entry mip table for absurd sizes; capped here
        int need = Math.Min(16, b.HasMips != 0 ? BlpFile.LevelsFor(b.Width, b.Height) : 1);
        for (int lv = 0; lv < need; lv++)
        {
            long o = b.MipOffsets[lv], s = b.MipSizes[lv];
            var (lw, lh) = BlpFile.LevelDims(b.Width, b.Height, lv);
            long exp;
            if (b.Encoding == BlpFile.EncDxt)
            {
                // Blizzard's converter stored max(1, (w/4)*(h/4)) blocks (integer division), so a level thinner than
                // 4 pixels has a single block (16x2 -> 1 block, not 4); 628 shipped files do that: accept it
                exp = Math.Max(1L, (long)(lw / 4) * (lh / 4)) * Dxt.BlockSize(b.DxtKind ?? "dxt1");
            }
            else
            {
                long n = (long)lw * lh;
                // (Python raises for an unknown alpha depth here; that depth is already reported above)
                exp = n + (knownDepth ? PackedAlphaSize((int)Math.Min(n, int.MaxValue), b.AlphaDepth) : 0);
            }
            if (o == 0 || o + s > data.Length)
            {
                problems.Add($"level {lv} ({lw}x{lh}) is missing or outside the file");
                continue;
            }
            if (s < exp) problems.Add($"level {lv} ({lw}x{lh}) has {s} bytes, needs {exp}");
        }
        return problems;
    }
}
