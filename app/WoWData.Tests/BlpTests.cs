using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>The Python reference results written by oracle/make_blp.py (shared by the BLP and PNG tests).</summary>
internal static class BlpOracle
{
    private static readonly Lazy<(string Dir, JsonNode Manifest)> Data = new(() =>
    {
        string dir = Oracle.Ensure("blp");
        var man = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!;
        return (dir, man);
    });

    public static string Dir => Data.Value.Dir;
    public static JsonNode Manifest => Data.Value.Manifest;
    public static IEnumerable<JsonNode> List(string key) => Manifest[key]!.AsArray().Select(n => n!);
    public static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(Dir, file));
    public static RgbaImage Image(string file, int w, int h) => new(w, h, Bytes(file));

    /// <summary>The samples (one per format variant + the thin-mip DXT3) and the random set.</summary>
    public static IEnumerable<JsonNode> AllFiles => List("samples").Concat(List("random"));

    public static double Psnr(RgbaImage a, RgbaImage b, bool rgbOnly = true)
    {
        double se = 0; long n = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            if (rgbOnly && (i & 3) == 3) continue;
            double d = a.Pixels[i] - b.Pixels[i];
            se += d * d; n++;
        }
        return se == 0 ? 99.0 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }

    public static string[] Strings(JsonNode? arr) => arr!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
}

public class BlpDecodeTests
{
    private readonly ITestOutputHelper _out;
    public BlpDecodeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Decode_EveryLevel_MatchesPython()
    {
        var bad = new List<string>();
        int files = 0, levels = 0;
        foreach (var e in BlpOracle.AllFiles)
        {
            string stem = (string)e["stem"]!;
            var b = BlpFile.Parse(BlpOracle.Bytes(stem + ".blp"));
            var lv = e["levels"]!.AsArray();
            Assert.Equal(lv.Count, b.LevelCount);
            for (int i = 0; i < lv.Count; i++)
            {
                int w = (int)lv[i]![0]!, h = (int)lv[i]![1]!;
                var img = b.Decode(i);
                if (img.Width != w || img.Height != h || !img.Pixels.AsSpan().SequenceEqual(BlpOracle.Bytes($"{stem}.L{i}.rgba")))
                    bad.Add($"{e["path"]} level {i}");
                levels++;
            }
            files++;
        }
        _out.WriteLine($"{files} files, {levels} levels compared");
        Assert.True(files >= 200, "the oracle should hold the samples and ~200 random files");
        Assert.True(bad.Count == 0, "decode differs from Python: " + string.Join("; ", bad.Take(10)));
    }

    [Fact]
    public void Decode_ShiftOnly_MatchesPython()
    {
        int n = 0;
        foreach (var e in BlpOracle.AllFiles)
        {
            if (e["shift"] is not JsonNode s) continue;
            var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
            var img = Dxt.Decode(b.MipData(0), b.Width, b.Height, b.DxtKind!, b.AlphaDepth > 0, shiftOnly: true);
            Assert.True(img.Pixels.AsSpan().SequenceEqual(BlpOracle.Bytes((string)s!)), (string)e["path"]!);
            n++;
        }
        Assert.True(n > 50);
    }

    [Fact]
    public void Info_FormatName_FormatOf_Validate_MatchPython()
    {
        foreach (var e in BlpOracle.AllFiles)
        {
            var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
            var mine = JsonNode.Parse(JsonSerializer.Serialize(b.GetInfo()));
            Assert.True(JsonNode.DeepEquals(mine, e["info"]), $"{e["path"]}: {mine!.ToJsonString()} vs {e["info"]!.ToJsonString()}");
            Assert.Equal((string)e["info"]!["format"]!, b.FormatName);
            Assert.Equal((string?)e["format_of"], BlpEncoder.FormatOf(b));
            Assert.Equal((string?)e["dxt_kind"], b.DxtKind);
            Assert.Equal(BlpOracle.Strings(e["validate"]), BlpEncoder.Validate(b.Data).ToArray());
        }
        var labels = BlpOracle.List("samples").Select(e => (string?)e["label"]).ToList();
        Assert.Contains("palette, 512x512, alphaEncoding 4", labels);
    }

    [Fact]
    public void Validate_BrokenFiles_MatchPython()
    {
        int n = 0;
        foreach (var e in BlpOracle.List("bad"))
        {
            var data = BlpOracle.Bytes((string)e["file"]!);
            Assert.Equal(BlpOracle.Strings(e["problems"]), BlpEncoder.Validate(data).ToArray());
            n++;
        }
        Assert.True(n >= 10);
        Assert.Equal(new[] { "not a BLP2 file" }, BlpEncoder.Validate(new byte[] { 1, 2, 3 }).ToArray());
        Assert.Throws<BlpException>(() => BlpFile.Parse(new byte[2000]));
    }

    [Fact]
    public void Decode_Performance()
    {
        var e = BlpOracle.List("samples").First(x => (string?)x["label"] == "dxt1 1-bit alpha");
        var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
        var big = BlpEncoder.EncodeNew(ImageOps.Resize(b.Decode(0), 512, 512), "dxt5");
        var b5 = BlpFile.Parse(big);
        b5.Decode(0);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) b5.Decode(0);
        double ms = sw.Elapsed.TotalMilliseconds / 10;
        _out.WriteLine($"512x512 DXT5 decode: {ms:F2} ms (the limit is checked in PerformanceTests)");
    }
}

public class BlpEncodeTests
{
    private readonly ITestOutputHelper _out;
    public BlpEncodeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Unmodified_RoundTrip_IsByteIdentical()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var e in BlpOracle.AllFiles)
        {
            var data = BlpOracle.Bytes((string)e["stem"]! + ".blp");
            var b = BlpFile.Parse(data);
            var (outp, rep) = BlpEncoder.EncodeLike(b, b.Decode(0));
            if (!rep.Identical || !outp.AsSpan().SequenceEqual(data)) bad.Add((string)e["path"]!);
            n++;
        }
        Assert.True(bad.Count == 0, "not byte-identical: " + string.Join("; ", bad.Take(10)));
        Assert.True(n >= 200);
    }

    [Fact]
    public void Unmodified_RoundTrip_Performance()
    {
        var e = BlpOracle.List("samples").First(x => (string?)x["label"] == "dxt1 1-bit alpha");   // 256x256, 9 levels
        var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
        var img = b.Decode(0);
        BlpEncoder.EncodeLike(b, img);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) BlpEncoder.EncodeLike(b, img);
        double ms = sw.Elapsed.TotalMilliseconds / 10;
        _out.WriteLine($"EncodeLike, unmodified 256x256 DXT1a: {ms:F2} ms (the limit is checked in PerformanceTests)");
    }

    /// <summary>
    /// Every edited round trip of the oracle (deterministic edits on every sample format, the thin-mip DXT3 layout,
    /// 30 random files, and the resized path) gives the same bytes as Python's encode_like, except where Python
    /// builds a NEW palette with Pillow's median cut (not reproduced byte for byte): there the file must be valid,
    /// keep the layout, and decode with a PSNR close to Python's.
    /// </summary>
    [Fact]
    public void Edited_RoundTrip_MatchesPython()
    {
        long blocks = 0, same = 0;
        int exact = 0, newPal = 0;
        var mismatches = new List<string>();
        foreach (var e in BlpOracle.List("edits"))
        {
            string stem = (string)e["stem"]!;
            var orig = BlpFile.Parse(BlpOracle.Bytes(stem + ".blp"));
            var input = BlpOracle.Image((string)e["in"]!, (int)e["width"]!, (int)e["height"]!);
            var expected = BlpOracle.Bytes((string)e["out"]!);
            var mode = (string)e["palette_mode"]! switch { "keep" => PaletteMode.Keep, "new" => PaletteMode.New, _ => PaletteMode.Auto };
            var pyRep = e["report"]!;
            var (outp, rep) = BlpEncoder.EncodeLike(orig, input, mode);
            string what = $"{e["stem"]} {e["name"]}";
            Assert.Empty(BlpEncoder.Validate(outp));
            Assert.Equal((bool)pyRep["identical"]!, rep.Identical);
            Assert.Equal((int)pyRep["levels"]!, rep.Levels);
            Assert.Equal(pyRep["resized"] != null, rep.Resized);
            if (!rep.Resized)
            {
                Assert.Equal((int)pyRep["changed_pixels"]!, rep.ChangedPixels);
                var pl = pyRep["per_level"]!.AsArray();
                Assert.Equal(pl.Count, rep.PerLevel.Count);
                for (int i = 0; i < pl.Count; i++)
                {
                    Assert.Equal((string)pl[i]!["size"]!, rep.PerLevel[i].Size);
                    Assert.Equal((int?)pl[i]!["blocks"], rep.PerLevel[i].Blocks);
                    Assert.Equal((int?)pl[i]!["pixels"], rep.PerLevel[i].Pixels);
                    Assert.Equal((int)pl[i]!["re_encoded"]!, rep.PerLevel[i].ReEncoded);
                }
                Assert.Equal((string?)pyRep["palette"], rep.Palette);
                if (pyRep["palette_rms_keep"] is JsonNode rms) Assert.Equal((double)rms, rep.PaletteRmsKeep!.Value, 2);
            }
            var nb = BlpFile.Parse(outp);
            var pb = BlpFile.Parse(expected);
            Assert.Equal(expected.Length, outp.Length);
            Assert.Equal(pb.MipOffsets, nb.MipOffsets);
            Assert.Equal(pb.MipSizes, nb.MipSizes);
            // the resized path of a palette file is encode_new, which always builds a new palette
            if (rep.Palette == "new" || (rep.Resized && orig.Encoding == BlpFile.EncPalette))
            {
                // independent median cut: compare quality, not bytes
                double mine = BlpOracle.Psnr(nb.Decode(0), input), py = BlpOracle.Psnr(pb.Decode(0), input);
                _out.WriteLine($"new palette {what}: PSNR {mine:F2} dB (Python/Pillow {py:F2} dB)");
                Assert.True(mine > py - 3.0 && mine > 25, $"{what}: PSNR {mine:F2} dB vs Python {py:F2} dB");
                newPal++;
                continue;
            }
            if (outp.AsSpan().SequenceEqual(expected)) exact++;
            else mismatches.Add(what);
            if (orig.Encoding == BlpFile.EncDxt)
            {
                int bs = Dxt.BlockSize(orig.DxtKind!);
                for (long o = BlpFile.HeaderSize; o + bs <= outp.Length; o += bs)
                {
                    blocks++;
                    if (outp.AsSpan((int)o, bs).SequenceEqual(expected.AsSpan((int)o, bs))) same++;
                }
            }
        }
        _out.WriteLine($"byte-identical to Python: {exact} edits; differing: {mismatches.Count}; new-palette (quality-checked): {newPal}; " +
                       $"DXT blocks identical: {same}/{blocks}");
        Assert.True(mismatches.Count == 0, "differs from Python encode_like: " + string.Join("; ", mismatches.Take(10)));
        Assert.True(exact >= 80, $"only {exact} byte-exact edits");
    }

    /// <summary>The raw block encoder on ~24k blocks (noise, smooth, gradients, two-colour, constant, punch-through
    /// alpha, fully transparent, and real texture blocks) in every mode gives exactly Python's bytes.</summary>
    [Fact]
    public void BlockEncoder_MatchesPython()
    {
        foreach (var e in BlpOracle.List("blocks"))
        {
            var input = BlpOracle.Bytes((string)e["in"]!);
            var expected = BlpOracle.Bytes((string)e["out"]!);
            string kind = (string)e["kind"]!;
            bool useAlpha = (bool)e["use_alpha"]!;
            var sw = Stopwatch.StartNew();
            var outp = Dxt.EncodeBlocks(input, kind, useAlpha);
            double ms = sw.Elapsed.TotalMilliseconds;
            int bs = Dxt.BlockSize(kind), n = (int)e["count"]!, same = 0;
            Assert.Equal(expected.Length, outp.Length);
            for (int i = 0; i < n; i++) if (outp.AsSpan(i * bs, bs).SequenceEqual(expected.AsSpan(i * bs, bs))) same++;
            _out.WriteLine($"{kind} useAlpha={useAlpha}: {same}/{n} blocks identical, {ms:F1} ms");
            Assert.Equal(n, same);
        }
    }

    [Fact]
    public void EncodeNew_MatchesPython()
    {
        foreach (var e in BlpOracle.List("encode_new"))
        {
            var src = BlpOracle.Image((string)e["src"]!, (int)e["width"]!, (int)e["height"]!);
            string fmt = (string)e["fmt"]!;
            BlpFile? tpl = e["template"] is JsonNode t ? BlpFile.Parse(BlpOracle.Bytes((string)t! + ".blp")) : null;
            var data = BlpEncoder.EncodeNew(src, fmt, (bool)e["mips"]!, tpl);
            var expected = BlpOracle.Bytes((string)e["out"]!);
            string what = $"{e["name"]} {fmt} mips={e["mips"]}";
            Assert.Equal(BlpOracle.Strings(e["validate"]), BlpEncoder.Validate(data).ToArray());
            var nb = BlpFile.Parse(data);
            var pb = BlpFile.Parse(expected);
            Assert.Equal(expected.Length, data.Length);
            Assert.True(data.AsSpan(0, BlpFile.PaletteOffset).SequenceEqual(expected.AsSpan(0, BlpFile.PaletteOffset)), what + ": header");
            if (fmt.StartsWith("dxt"))
            {
                Assert.True(data.AsSpan().SequenceEqual(expected), what + ": differs from Python");
            }
            else
            {
                // new palette: independent median cut, so compare quality with Python/Pillow's
                var vis = VisibleRgb(src);
                double mine = BlpOracle.Psnr(Masked(nb.Decode(0), vis), Masked(src, vis)), py = BlpOracle.Psnr(Masked(pb.Decode(0), vis), Masked(src, vis));
                _out.WriteLine($"{what}: PSNR {mine:F2} dB (Python/Pillow {py:F2} dB)");
                Assert.True(mine > 30 && mine > py - 3.0, $"{what}: PSNR {mine:F2} dB vs Python {py:F2} dB");
                // the alpha plane does not depend on the palette: identical
                for (int lv = 0; lv < nb.LevelCount; lv++)
                {
                    var a = nb.Decode(lv).Pixels; var p = pb.Decode(lv).Pixels;
                    for (int i = 3; i < a.Length; i += 4) Assert.Equal(p[i], a[i]);
                }
            }
        }
    }

    private static bool[] VisibleRgb(RgbaImage img)
    {
        var m = new bool[img.Width * img.Height];
        for (int i = 0; i < m.Length; i++) m[i] = img.Pixels[i * 4 + 3] >= 128;
        return m;
    }

    private static RgbaImage Masked(RgbaImage img, bool[] vis)
    {
        var c = img.Clone();
        for (int i = 0; i < vis.Length; i++) if (!vis[i]) { c.Pixels[i * 4] = 0; c.Pixels[i * 4 + 1] = 0; c.Pixels[i * 4 + 2] = 0; }
        return c;
    }

    [Fact]
    public void PartialEdit_TouchesOnlyItsBlocks()
    {
        var e = BlpOracle.List("samples").First(x => (string?)x["label"] == "dxt1 1-bit alpha");
        var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
        var img = b.Decode(0);
        for (int y = 100; y < 118; y++)
            for (int x = 100; x < 118; x++) { int o = img.Offset(x, y); img.Pixels[o] = 255; img.Pixels[o + 1] = 0; img.Pixels[o + 2] = 0; img.Pixels[o + 3] = 255; }
        var (outp, rep) = BlpEncoder.EncodeLike(b, img);
        Assert.False(rep.Identical);
        Assert.Empty(BlpEncoder.Validate(outp));
        var nb = BlpFile.Parse(outp);
        Assert.Equal(b.MipOffsets, nb.MipOffsets);
        Assert.Equal(b.MipSizes, nb.MipSizes);
        // level 0: block rows/cols 25..29 -> 25 blocks re-encoded, everything else identical
        Assert.Equal(25, rep.PerLevel[0].ReEncoded);
        var o0 = b.MipData(0); var n0 = nb.MipData(0);
        for (int blk = 0; blk < 64 * 64; blk++)
        {
            int r = blk / 64, c = blk % 64;
            bool inside = r >= 25 && r < 30 && c >= 25 && c < 30;
            if (!inside) Assert.True(o0.Slice(blk * 8, 8).SequenceEqual(n0.Slice(blk * 8, 8)), $"block {r},{c} changed");
        }
        var dec = nb.Decode(0);
        for (int y = 101; y < 117; y++)
            for (int x = 101; x < 117; x++)
            {
                int o = dec.Offset(x, y);
                Assert.True(dec.Pixels[o] >= 240 && dec.Pixels[o + 1] <= 15 && dec.Pixels[o + 2] <= 15);
            }
    }

    [Fact]
    public void ShortThinLevels_KeepTheirLayout()
    {
        var e = BlpOracle.List("samples").First(x => (string?)x["label"] == "dxt3 thin short mips");
        var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
        Assert.Equal((64, 8, 16u), (b.Width, b.Height, b.MipSizes[2]));
        Assert.Empty(BlpEncoder.Validate(b.Data));
        var img = b.Decode(0);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { int o = img.Offset(x, y); for (int c = 0; c < 4; c++) img.Pixels[o + c] = 255; }
        var (outp, _) = BlpEncoder.EncodeLike(b, img);
        var nb = BlpFile.Parse(outp);
        Assert.Equal(b.MipOffsets, nb.MipOffsets);
        Assert.Equal(b.MipSizes, nb.MipSizes);
        Assert.Equal(b.Data.Length, outp.Length);
        Assert.Empty(BlpEncoder.Validate(outp));
        var d = nb.Decode(0);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Assert.True(d.Pixels[d.Offset(x, y)] >= 250);
    }
}

/// <summary>Self-contained checks (no game data), mirroring tests/test_blp.py SyntheticTests.</summary>
public class BlpSyntheticTests
{
    internal static RgbaImage Synthetic(int w = 64, int h = 32)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = img.Offset(x, y);
                img.Pixels[o] = (byte)(x * 255 / (w - 1));
                img.Pixels[o + 1] = (byte)(y * 255 / (h - 1));
                img.Pixels[o + 2] = 128;
                img.Pixels[o + 3] = (byte)(x < w / 4 ? 0 : x < w / 2 ? 128 : 255);
            }
        return img;
    }

    [Fact]
    public void EveryFormat_ValidatesDecodesAndReencodesIdentically()
    {
        var img = Synthetic();
        foreach (var fmt in BlpEncoder.Formats)
        {
            var data = BlpEncoder.EncodeNew(img, fmt);
            Assert.Empty(BlpEncoder.Validate(data));
            var b = BlpFile.Parse(data);
            Assert.Equal(7, b.LevelCount);                // 64x32 .. 1x1
            Assert.Equal(fmt, BlpEncoder.FormatOf(b));
            var dec = b.Decode(0);
            var vis = new bool[64 * 32];
            for (int i = 0; i < vis.Length; i++) vis[i] = img.Pixels[i * 4 + 3] >= 128;
            double se = 0; int n = 0;
            for (int i = 0; i < vis.Length; i++)
                if (vis[i]) for (int c = 0; c < 3; c++) { double d = dec.Pixels[i * 4 + c] - img.Pixels[i * 4 + c]; se += d * d; n++; }
            double psnr = se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
            Assert.True(psnr > 30, $"{fmt}: PSNR {psnr:F1}");
            var (outp, rep) = BlpEncoder.EncodeLike(b, dec);
            Assert.True(rep.Identical, fmt);
            Assert.Equal(data, outp);
        }
    }

    [Fact]
    public void SmallMips()
    {
        var img = Synthetic(4, 4);
        foreach (var fmt in new[] { "dxt1", "dxt3", "dxt5" })
        {
            var b = BlpFile.Parse(BlpEncoder.EncodeNew(img, fmt));
            Assert.Equal(3, b.LevelCount);
            var d = b.Decode(2);
            Assert.Equal((1, 1), (d.Width, d.Height));
        }
    }

    [Fact]
    public void Validate_Rejects()
    {
        var good = BlpEncoder.EncodeNew(Synthetic(), "dxt1");
        Assert.Empty(BlpEncoder.Validate(good));
        Assert.NotEmpty(BlpEncoder.Validate(good[..^8]));
        var bad = (byte[])good.Clone();
        bad[8] = 3;
        Assert.NotEmpty(BlpEncoder.Validate(bad));
        var np2 = new RgbaImage(48, 32);
        Assert.NotEmpty(BlpEncoder.Validate(BlpEncoder.EncodeNew(np2, "dxt1")));
        Assert.Throws<BlpException>(() => BlpEncoder.EncodeNew(np2, "bc7"));
    }

    [Fact]
    public void Dxt5_AlphaModes()
    {
        var blk = new byte[64];
        int[] a = { 0, 255, 100, 120 };
        for (int i = 0; i < 16; i++) blk[i * 4 + 3] = (byte)a[i % 4];
        var enc = Dxt.EncodeBlocks(blk, "dxt5");
        var dec = Dxt.Decode(enc, 4, 4, "dxt5");
        Assert.Equal(0, dec.Pixels[3]);
        Assert.Equal(255, dec.Pixels[7]);
        for (int i = 0; i < 16; i++) Assert.True(Math.Abs(dec.Pixels[i * 4 + 3] - a[i % 4]) <= 4);
    }

    [Fact]
    public void SuggestFormat_And_Formats()
    {
        Assert.Equal(new[] { "dxt1", "dxt1a", "dxt3", "dxt5", "pal", "pal1", "pal4", "pal8" }, BlpEncoder.Formats);
        var img = new RgbaImage(4, 4);
        Assert.Equal("dxt1a", BlpEncoder.SuggestFormat(img));
        for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
        Assert.Equal("dxt1", BlpEncoder.SuggestFormat(img));
        img.Pixels[3] = 7;
        Assert.Equal("dxt5", BlpEncoder.SuggestFormat(img));
    }
}
