using System.Security.Cryptography;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Maps.Neutral;

namespace WoWData.Core.Maps.Conversion;

/// <summary>
/// The far terrain (WDL) entry of a tile, from its heights, in the positional 1.12 format (17 x 17 outer points, then
/// 16 x 16 inner points, int16). MEASURED on 8 stock Azeroth tiles against the stock Azeroth.wdl (2,312 + 2,048 points):
/// the outer points are the chunk corners' absolute heights truncated to whole yards (2,312 of 2,312 exact); the inner
/// points are not one vertex (Blizzard's tool smoothed them): the truncated mean of the chunk's 145 heights is the
/// closest of the rules tried (mean error 1.2 yards; the centre vertex 2.7).
/// </summary>
public static class WdlHeights
{
    public static short[] Of(NeutralTile t)
    {
        var h = new short[545];
        static short Clamp(double v) => (short)Math.Clamp(Math.Truncate(v), short.MinValue, short.MaxValue);
        NeutralChunk Chunk(int cx, int cy) => t.Chunks[cy * 16 + cx] ?? NeutralChunk.CreateFlat(t.X, t.Y, cx, cy, 0f);
        for (int r = 0; r < 17; r++)
            for (int c = 0; c < 17; c++)
            {
                var ch = Chunk(Math.Min(c, 15), Math.Min(r, 15));
                int vr = r == 16 ? 8 : 0, vc = c == 16 ? 8 : 0;
                h[r * 17 + c] = Clamp(ch.Position.Z + ch.Heights[vr * 17 + vc]);
            }
        for (int r = 0; r < 16; r++)
            for (int c = 0; c < 16; c++)
            {
                var ch = Chunk(c, r);
                double sum = 0;
                foreach (var v in ch.Heights) sum += v;
                h[289 + r * 16 + c] = Clamp(ch.Position.Z + sum / 145);
            }
        return h;
    }
}

/// <summary>
/// 1.12's minimap table, <c>textures\Minimap\md5translate.trs</c>: lines <c>dir: &lt;map&gt;</c> and
/// <c>&lt;map&gt;\mapXX_YY.blp TAB &lt;md5&gt;.blp</c> (MEASURED: the name is the MD5 of the BLP file's bytes, CRLF lines,
/// 8,637 lines in 235 "dir:" groups).
/// </summary>
public static class Md5Translate
{
    public static string KeyOf(string dir, int x, int y) => $@"{dir}\map{x:D2}_{y:D2}.blp";

    /// <summary>The table with the entries of <paramref name="tiles"/> (tile -&gt; hashed file name) set: existing lines are
    /// replaced in place, new ones go to the end of the map's group (a new group at the end for a new map).</summary>
    public static byte[] Update(byte[]? trs, string dir, IReadOnlyDictionary<(int X, int Y), string> tiles)
    {
        var text = trs == null ? "" : Encoding.Latin1.GetString(trs);
        string nl = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var want = tiles.ToDictionary(kv => KeyOf(dir, kv.Key.X, kv.Key.Y), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        int lastOfDir = -1;
        bool inDir = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
            {
                inDir = string.Equals(l[4..].Trim(), dir, StringComparison.OrdinalIgnoreCase);
                if (inDir) lastOfDir = i;
                continue;
            }
            int tab = l.IndexOf('\t');
            if (tab <= 0) continue;
            var key = l[..tab].Trim();
            if (key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) lastOfDir = i;
            else if (inDir) lastOfDir = i;
            if (want.TryGetValue(key, out var hash))
            {
                lines[i] = key + "\t" + hash;
                want.Remove(key);
            }
        }
        if (want.Count > 0)
        {
            var add = want.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "\t" + kv.Value).ToList();
            if (lastOfDir < 0)
            {
                lines.Add("dir: " + dir);
                lines.AddRange(add);
            }
            else lines.InsertRange(lastOfDir + 1, add);
        }
        return Encoding.Latin1.GetBytes(string.Join(nl, lines) + nl);
    }
}

/// <summary>Textures for 1.12: decoded from the source file, at most 512 x 512 (BlpImage_IsValidDimension of the 1.12
/// client allows powers of two up to 512, see <see cref="BlpEncoder.Validate"/>), a power of two.</summary>
public static class ConvertTextures
{
    public const int MaxSize = 512;

    /// <summary>The picture of a BLP at most <paramref name="max"/> on a side: the source's own smaller mip level when it has
    /// one (the standard-resolution picture, not a re-sampled one), else a resize; non-power-of-two sizes are resized.</summary>
    public static RgbaImage Picture(byte[] blp, int max, out int srcW, out int srcH, out bool reduced)
    {
        var b = BlpFile.Parse(blp);
        srcW = b.Width;
        srcH = b.Height;
        int level = 0;
        while (Math.Max(b.Width >> level, b.Height >> level) > max && level + 1 < b.LevelCount) level++;
        var img = b.Decode(level);
        int w = Math.Min(max, ImageOps.IsPow2(img.Width) ? img.Width : ImageOps.NearestPow2(img.Width));
        int h = Math.Min(max, ImageOps.IsPow2(img.Height) ? img.Height : ImageOps.NearestPow2(img.Height));
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (w != img.Width || h != img.Height) img = ImageOps.Resize(img, w, h);
        reduced = img.Width != srcW || img.Height != srcH;
        return img;
    }

    /// <summary>
    /// A ground texture for 1.12 from the 1.60 files: the base file (opaque DXT1, as 1.12's own ground textures) and its
    /// "_s" file (DXT3, the specular strength in alpha, as 1.12's). <paramref name="specular"/> is the file the 1.60 tile
    /// names (its MDID entry is the _s file); <paramref name="plain"/> the 1.60 diffuse file when its name is known
    /// (else the base is the _s file's colours).
    /// </summary>
    public static (byte[] Base, byte[] Specular, int Width, int Height, bool Reduced, string Source) Ground(byte[] specular, byte[]? plain)
    {
        var spec = Picture(specular, MaxSize, out int sw, out int sh, out bool reduced);
        RgbaImage baseImg;
        string source;
        if (plain != null)
        {
            baseImg = Picture(plain, MaxSize, out _, out _, out bool r2);
            reduced |= r2;
            if (baseImg.Width != spec.Width || baseImg.Height != spec.Height) baseImg = ImageOps.Resize(baseImg, spec.Width, spec.Height);
            source = "the 1.60 diffuse file and its _s file";
        }
        else
        {
            baseImg = spec.Clone();
            source = "the 1.60 _s file (its colours for the base file)";
        }
        for (int i = 3; i < baseImg.Pixels.Length; i += 4) baseImg.Pixels[i] = 255;
        return (BlpEncoder.EncodeNew(baseImg, "dxt1"), BlpEncoder.EncodeNew(spec, "dxt3"), spec.Width, spec.Height, reduced, source);
    }

    /// <summary>
    /// A 1.12 minimap tile from the 1.60 one: 256 x 256 DXT1 without mip levels (MEASURED: every 1.12 minimap tile; 1.60's
    /// are 512 x 512 DXT1 without mips), named by the MD5 of its bytes as Blizzard's are.
    /// </summary>
    public static (byte[] Blp, string HashName, bool Reduced) Minimap(byte[] src)
    {
        var img = Picture(src, 256, out int w, out int h, out bool reduced);
        if (img.Width != 256 || img.Height != 256)
        {
            img = ImageOps.Resize(img, 256, 256);
            reduced = true;
        }
        for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
        var blp = BlpEncoder.EncodeNew(img, "dxt1", mips: false);
        return (blp, Convert.ToHexString(MD5.HashData(blp)).ToLowerInvariant() + ".blp", reduced);
    }
}
