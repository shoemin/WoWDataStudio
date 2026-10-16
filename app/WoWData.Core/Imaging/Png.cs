using System.Buffers.Binary;
using System.IO.Compression;

namespace WoWData.Core.Imaging;

/// <summary>An image file is not a PNG or is damaged; the message is plain language.</summary>
public class ImageFormatException : Exception
{
    public ImageFormatException(string message) : base(message) { }
    public ImageFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// PNG reading and writing without System.Drawing/WPF (zlib through System.IO.Compression).
/// </summary>
/// <remarks>
/// Decoder: every standard colour type (0 gray, 2 RGB, 3 palette, 4 gray+alpha, 6 RGBA), bit depths 1/2/4/8/16,
/// tRNS for gray/RGB/palette, Adam7 interlacing; the result is always 8-bit RGBA and matches Pillow's
/// <c>Image.open(p).convert('RGBA')</c> (what the Python tool uses) for 8-bit and lower files and for 16-bit RGB,
/// RGBA and gray+alpha. Rules:
///  * gray below 8 bits is scaled to 0..255 (1 bit: x255, 2 bits: x85, 4 bits: x17);
///  * 16-bit samples are reduced to 8 bits by taking the high byte (Pillow does this for RGB, RGBA and gray+alpha).
///    Deliberate differences from Pillow, which are Pillow quirks: 16-bit GRAY (Pillow mode I;16) is clipped by Pillow
///    (any value above 255 becomes 255, so a normal 16-bit grayscale image turns almost white); here it also takes the
///    high byte. A tRNS colour key on a 16-bit image is compared with the full 16-bit samples (Pillow compares its low
///    byte with the reduced pixels), and a tRNS gray key on a 1/2/4-bit image is compared with the raw sample
///    (Pillow compares it with the scaled value, so it only works for key 0).
///  * palette indices past the PLTE entries give opaque black.
/// Encoder: 8-bit RGB or RGBA, non-interlaced, per-row adaptive filter (minimum sum of absolute differences).
/// </remarks>
public static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private const long MaxPixels = 1L << 28;

    // ------------------------------------------------------------------ CRC-32 (ISO-HDLC, as PNG requires)

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ------------------------------------------------------------------ files

    public static RgbaImage Load(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { throw new ImageFormatException($"{path} does not exist"); }
        try { return Decode(data); }
        catch (ImageFormatException e) { throw new ImageFormatException($"{Path.GetFileName(path)}: {e.Message}", e); }
    }

    public static void Save(RgbaImage img, string path, bool includeAlpha)
    {
        var bytes = Encode(img, includeAlpha);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>8-bit RGB (alpha dropped) or RGBA PNG.</summary>
    public static byte[] Encode(RgbaImage img, bool includeAlpha)
    {
        ArgumentNullException.ThrowIfNull(img);
        int w = img.Width, h = img.Height, bpp = includeAlpha ? 4 : 3;
        int stride = w * bpp;
        var px = img.Pixels;
        var prev = new byte[stride];
        var cur = new byte[stride];
        var cand = new byte[5][];
        for (int i = 0; i < 5; i++) cand[i] = new byte[stride];
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < h; y++)
            {
                if (includeAlpha) Buffer.BlockCopy(px, y * w * 4, cur, 0, stride);
                else
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4;
                        cur[x * 3] = px[s]; cur[x * 3 + 1] = px[s + 1]; cur[x * 3 + 2] = px[s + 2];
                    }
                int best = 0; long bestSum = long.MaxValue;
                for (int f = 0; f < 5; f++)
                {
                    var o = cand[f];
                    long sum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                        byte v = (byte)(cur[i] - pred);
                        o[i] = v;
                        sum += v < 128 ? v : 256 - v;
                    }
                    if (sum < bestSum) { bestSum = sum; best = f; }
                }
                z.WriteByte((byte)best);
                z.Write(cand[best], 0, stride);
                (prev, cur) = (cur, prev);
            }
        }
        var idat = ms.ToArray();
        using var outp = new MemoryStream();
        outp.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = includeAlpha ? (byte)6 : (byte)2;
        WriteChunk(outp, "IHDR", ihdr);
        WriteChunk(outp, "IDAT", idat);
        WriteChunk(outp, "IEND", Array.Empty<byte>());
        return outp.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(head.Slice(4), data));
        s.Write(crc);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>Any standard PNG -> 8-bit RGBA. Throws <see cref="ImageFormatException"/> for non-PNG or damaged data.</summary>
    public static RgbaImage Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature)) throw new ImageFormatException("not a PNG file");
        int pos = 8;
        int width = 0, height = 0, depth = 0, ctype = -1, interlace = 0;
        byte[]? plte = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        bool seenIhdr = false, seenEnd = false;
        while (pos + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (len > int.MaxValue || pos + 12L + len > data.Length) throw new ImageFormatException($"the PNG file is cut short (chunk {Sanitize(type)})");
            var body = data.AsSpan(pos + 8, (int)len);
            bool critical = char.IsUpper(type[0]);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8 + (int)len));
            if (critical && crc != Crc(data.AsSpan(pos + 4, 4), body)) throw new ImageFormatException($"the PNG file is damaged (bad checksum in chunk {Sanitize(type)})");
            pos += 12 + (int)len;
            if (!seenIhdr && type != "IHDR") throw new ImageFormatException("the PNG file is damaged (it does not start with an IHDR chunk)");
            switch (type)
            {
                case "IHDR":
                    if (len != 13) throw new ImageFormatException("the PNG file is damaged (bad IHDR chunk)");
                    seenIhdr = true;
                    uint w = BinaryPrimitives.ReadUInt32BigEndian(body), h = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4));
                    depth = body[8]; ctype = body[9]; interlace = body[12];
                    if (w == 0 || h == 0 || (long)w * h > MaxPixels) throw new ImageFormatException($"the PNG image size {w}x{h} is not supported");
                    width = (int)w; height = (int)h;
                    bool okDepth = ctype switch
                    {
                        0 => depth is 1 or 2 or 4 or 8 or 16,
                        2 or 4 or 6 => depth is 8 or 16,
                        3 => depth is 1 or 2 or 4 or 8,
                        _ => false,
                    };
                    if (!okDepth) throw new ImageFormatException($"the PNG colour type {ctype} with bit depth {depth} is not valid");
                    if (body[10] != 0 || body[11] != 0 || interlace > 1) throw new ImageFormatException("the PNG file uses an unknown compression, filter or interlace method");
                    break;
                case "PLTE":
                    if (len % 3 != 0 || len > 768) throw new ImageFormatException("the PNG file is damaged (bad palette)");
                    plte = body.ToArray();
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    seenEnd = true;
                    break;
            }
            if (seenEnd) break;
        }
        if (!seenIhdr) throw new ImageFormatException("the PNG file is damaged (no IHDR chunk)");
        if (idat.Length == 0) throw new ImageFormatException("the PNG file has no image data");
        if (ctype == 3 && plte == null) throw new ImageFormatException("the PNG palette image has no palette");

        int channels = ctype switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPP = channels * depth;
        int bpp = Math.Max(1, bitsPP / 8);
        // pass layout
        var passes = interlace == 1
            ? new (int X0, int Y0, int DX, int DY)[] { (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2) }
            : new (int X0, int Y0, int DX, int DY)[] { (0, 0, 1, 1) };
        long total = 0;
        foreach (var p in passes)
        {
            long pw = (width - p.X0 + p.DX - 1) / p.DX, ph = (height - p.Y0 + p.DY - 1) / p.DY;
            if (pw > 0 && ph > 0) total += ph * (1 + (pw * bitsPP + 7) / 8);
        }
        if (total > int.MaxValue) throw new ImageFormatException("the PNG image is too large");
        var raw = new byte[total];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            int got = 0;
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n == 0) break;
                got += n;
            }
            if (got < raw.Length) throw new ImageFormatException("the PNG image data is cut short");
        }
        catch (InvalidDataException e) { throw new ImageFormatException("the PNG image data is damaged (bad compressed data)", e); }

        var img = new RgbaImage(width, height);
        var outp = img.Pixels;
        int rp = 0;
        foreach (var p in passes)
        {
            int pw = (width - p.X0 + p.DX - 1) / p.DX, ph = (height - p.Y0 + p.DY - 1) / p.DY;
            if (pw <= 0 || ph <= 0) continue;
            int stride = (pw * bitsPP + 7) / 8;
            var prev = new byte[stride];
            var cur = new byte[stride];
            for (int y = 0; y < ph; y++)
            {
                int f = raw[rp++];
                Buffer.BlockCopy(raw, rp, cur, 0, stride);
                rp += stride;
                Unfilter(f, cur, prev, bpp);
                ConvertRow(cur, pw, ctype, depth, plte, trns, outp, width, p.Y0 + y * p.DY, p.X0, p.DX);
                (prev, cur) = (cur, prev);
            }
        }
        return img;
    }

    private static string Sanitize(string type)
    {
        foreach (char c in type) if (c < 32 || c > 126) return "?";
        return type;
    }

    private static void Unfilter(int f, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (f)
        {
            case 0: break;
            case 1: for (int i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]); break;
            case 2: for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]); break;
            case 3:
                for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                break;
            case 4:
                for (int i = 0; i < n; i++)
                    cur[i] = (byte)(cur[i] + Paeth(i >= bpp ? cur[i - bpp] : 0, prev[i], i >= bpp ? prev[i - bpp] : 0));
                break;
            default: throw new ImageFormatException($"the PNG image data is damaged (unknown row filter {f})");
        }
    }

    private static int Sample(byte[] row, int i, int depth) => depth switch
    {
        8 => row[i],
        16 => (row[2 * i] << 8) | row[2 * i + 1],
        _ => (row[(i * depth) >> 3] >> (8 - depth - ((i * depth) & 7))) & ((1 << depth) - 1),
    };

    private static void ConvertRow(byte[] row, int pw, int ctype, int depth, byte[]? plte, byte[]? trns, byte[] outp, int width, int y, int x0, int dx)
    {
        int maxv = (1 << depth) - 1;
        for (int i = 0; i < pw; i++)
        {
            int o = (y * width + x0 + i * dx) * 4;
            byte r, g, b, a = 255;
            switch (ctype)
            {
                case 0:
                {
                    int v = Sample(row, i, depth);
                    r = g = b = To8(v, depth, maxv);
                    if (trns != null && trns.Length >= 2 && v == ((trns[0] << 8) | trns[1])) a = 0;
                    break;
                }
                case 2:
                {
                    int vr = Sample(row, 3 * i, depth), vg = Sample(row, 3 * i + 1, depth), vb = Sample(row, 3 * i + 2, depth);
                    r = To8(vr, depth, maxv); g = To8(vg, depth, maxv); b = To8(vb, depth, maxv);
                    if (trns != null && trns.Length >= 6 && vr == ((trns[0] << 8) | trns[1]) && vg == ((trns[2] << 8) | trns[3]) && vb == ((trns[4] << 8) | trns[5])) a = 0;
                    break;
                }
                case 3:
                {
                    int v = Sample(row, i, depth);
                    if (v * 3 + 2 < plte!.Length) { r = plte[v * 3]; g = plte[v * 3 + 1]; b = plte[v * 3 + 2]; }
                    else { r = g = b = 0; }
                    if (trns != null && v < trns.Length) a = trns[v];
                    break;
                }
                case 4:
                    r = g = b = To8(Sample(row, 2 * i, depth), depth, maxv);
                    a = To8(Sample(row, 2 * i + 1, depth), depth, maxv);
                    break;
                default:
                    r = To8(Sample(row, 4 * i, depth), depth, maxv); g = To8(Sample(row, 4 * i + 1, depth), depth, maxv);
                    b = To8(Sample(row, 4 * i + 2, depth), depth, maxv); a = To8(Sample(row, 4 * i + 3, depth), depth, maxv);
                    break;
            }
            outp[o] = r; outp[o + 1] = g; outp[o + 2] = b; outp[o + 3] = a;
        }
    }

    private static byte To8(int v, int depth, int maxv) => depth switch
    {
        8 => (byte)v,
        16 => (byte)(v >> 8),
        _ => (byte)(v * 255 / maxv),
    };
}
