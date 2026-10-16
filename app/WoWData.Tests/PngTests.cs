using System.IO;
using System.Linq;
using WoWData.Core.Imaging;
using Xunit.Abstractions;

namespace WoWData.Tests;

public class PngTests
{
    private readonly ITestOutputHelper _out;
    public PngTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// PNGs written by Pillow and hand-written ones (every colour type and bit depth, Adam7, odd sizes, every row
    /// filter, split IDAT chunks, tRNS) decode to exactly Pillow's convert('RGBA'), except the documented rule
    /// differences (16-bit gray, tRNS on 16-bit / low-bit gray: rule "spec" in the manifest).
    /// </summary>
    [Fact]
    public void Decode_MatchesPillow()
    {
        int n = 0, spec = 0;
        foreach (var e in BlpOracle.List("png"))
        {
            var img = Png.Decode(BlpOracle.Bytes((string)e["file"]!));
            Assert.Equal(((int)e["width"]!, (int)e["height"]!), (img.Width, img.Height));
            Assert.True(img.Pixels.AsSpan().SequenceEqual(BlpOracle.Bytes((string)e["expected"]!)), $"{e["file"]} ({e["desc"]}, rule {e["rule"]})");
            n++;
            if ((string)e["rule"]! == "spec") spec++;
        }
        _out.WriteLine($"{n} PNG files decoded ({spec} by the documented non-Pillow rule)");
        Assert.True(n > 100);
    }

    [Fact]
    public void Decode_BrokenFiles_ThrowImageFormatException()
    {
        foreach (var f in BlpOracle.Manifest["png_broken"]!.AsArray())
        {
            var data = BlpOracle.Bytes((string)f!);
            var ex = Assert.Throws<ImageFormatException>(() => Png.Decode(data));
            _out.WriteLine($"{f}: {ex.Message}");
        }
        Assert.Throws<ImageFormatException>(() => Png.Load(Path.Combine(Path.GetTempPath(), "no such file 8f3a.png")));
    }

    private static RgbaImage TestImage(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = img.Offset(x, y);
                // mix of smooth areas and noise so every filter gets picked
                bool noisy = ((x / 8) + (y / 8)) % 3 == 0;
                img.Pixels[o] = (byte)(noisy ? rnd.Next(256) : x * 4);
                img.Pixels[o + 1] = (byte)(noisy ? rnd.Next(256) : y * 3);
                img.Pixels[o + 2] = (byte)(noisy ? rnd.Next(256) : (x + y) * 2);
                img.Pixels[o + 3] = (byte)(x < 5 ? 0 : noisy ? rnd.Next(256) : 255);
            }
        return img;
    }

    [Fact]
    public void Encode_RoundTrips_ThroughOwnDecoder()
    {
        var img = TestImage(61, 35, 1);
        var back = Png.Decode(Png.Encode(img, includeAlpha: true));
        Assert.True(back.SameContent(img));
        var rgb = Png.Decode(Png.Encode(img, includeAlpha: false));
        for (int i = 0; i < img.Pixels.Length; i++)
            Assert.Equal((i & 3) == 3 ? 255 : img.Pixels[i], rgb.Pixels[i]);
    }

    /// <summary>Our encoder's files read back in Pillow (the Python tool) to the same pixels, RGB and RGBA.</summary>
    [Fact]
    public void Encode_DecodesInPillow()
    {
        BlpOracle.Manifest.ToString();     // make sure Python works (clear message otherwise)
        string tmp = Path.Combine(Path.GetTempPath(), "wowdata_png_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var img = TestImage(77, 41, 2);
            foreach (bool alpha in new[] { true, false })
            {
                string png = Path.Combine(tmp, alpha ? "rgba.png" : "rgb.png");
                string raw = png + ".rgba";
                Png.Save(img, png, alpha);
                string script = Path.Combine(Oracle.AppDir, "WoWData.Tests", "oracle", "make_blp.py");
                string res = Oracle.RunPython(script, "--pil-decode", png, raw).Trim();
                Assert.Equal($"77 41 {(alpha ? "RGBA" : "RGB")}", res);
                var pil = File.ReadAllBytes(raw);
                for (int i = 0; i < pil.Length; i++)
                    Assert.Equal(alpha || (i & 3) != 3 ? img.Pixels[i] : (byte)255, pil[i]);
            }
        }
        finally { Directory.Delete(tmp, true); }
    }
}

public class PngImageOpsTests
{
    [Fact]
    public void Pow2Helpers()
    {
        Assert.True(ImageOps.IsPow2(1) && ImageOps.IsPow2(256) && !ImageOps.IsPow2(0) && !ImageOps.IsPow2(48));
        Assert.Equal(64, ImageOps.NextPow2(33));
        Assert.Equal(32, ImageOps.NearestPow2(40));
        Assert.Equal(64, ImageOps.NearestPow2(50));
        Assert.Equal(512, ImageOps.NearestPow2(384));
        Assert.Equal(1, ImageOps.NearestPow2(0));
        Assert.Equal(256, ImageOps.NearestPow2(256));
    }

    [Fact]
    public void Resize_SolidStaysSolid_AndAreaAverages()
    {
        var img = new RgbaImage(6, 6);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = 10; img.Pixels[i + 1] = 200; img.Pixels[i + 2] = 99; img.Pixels[i + 3] = 255; }
        foreach (var (w, h) in new[] { (4, 4), (16, 8), (1, 1), (3, 9) })
        {
            var r = ImageOps.Resize(img, w, h);
            Assert.Equal((w, h), (r.Width, r.Height));
            for (int i = 0; i < r.Pixels.Length; i += 4) Assert.Equal(new byte[] { 10, 200, 99, 255 }, r.Pixels[i..(i + 4)]);
        }
        // 4x1 black/white/black/white -> 2x1 gray
        var bw = new RgbaImage(4, 1);
        for (int x = 0; x < 4; x++) { byte v = (byte)(x % 2 == 0 ? 0 : 255); bw.Pixels[x * 4] = v; bw.Pixels[x * 4 + 1] = v; bw.Pixels[x * 4 + 2] = v; bw.Pixels[x * 4 + 3] = 255; }
        var g = ImageOps.Resize(bw, 2, 1);
        Assert.InRange(g.Pixels[0], 127, 128);
        Assert.InRange(g.Pixels[4], 127, 128);
    }

    [Fact]
    public void Resize_TransparentColourDoesNotBleed()
    {
        // left half: fully transparent but red colour; right half: opaque blue
        var img = new RgbaImage(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int o = img.Offset(x, y);
                if (x < 4) { img.Pixels[o] = 255; img.Pixels[o + 3] = 0; }
                else { img.Pixels[o + 2] = 255; img.Pixels[o + 3] = 255; }
            }
        foreach (var (w, h) in new[] { (2, 2), (4, 4), (16, 16), (3, 5) })
        {
            var r = ImageOps.Resize(img, w, h);
            for (int i = 0; i < r.Pixels.Length; i += 4)
            {
                if (r.Pixels[i + 3] == 0) continue;
                Assert.Equal(0, r.Pixels[i]);         // no red bleeding into visible pixels
                Assert.Equal(255, r.Pixels[i + 2]);
            }
        }
    }
}
