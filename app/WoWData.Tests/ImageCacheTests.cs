using System.Linq;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;

namespace WoWData.Tests;

/// <summary>
/// After 0.14.0: the decoded-texture cache of the model resolver is bounded. It used to keep every texture ever decoded, so
/// browsing many models grew the memory without end and the 3D views kept the GPU copy of every one of them (found by
/// the `--leaktest` mode: about 1 MB and 1.3 GL textures more per new creature, never given back).
/// </summary>
public class ImageCacheTests
{
    private static RgbaImage Img(int w, int h) => new(w, h);

    [Fact]
    public void KeepsTheMostRecentlyUsedWithinTheSize()
    {
        var c = new ImageCache(3 * 64 * 64 * 4);       // room for three 64 x 64 images
        int loads = 0;
        RgbaImage? Load(string _) { loads++; return Img(64, 64); }
        var a = c.GetOrAdd("a", Load);
        c.GetOrAdd("b", Load);
        c.GetOrAdd("c", Load);
        Assert.Same(a, c.GetOrAdd("A", Load));          // paths compare without case; "a" is now the most recent
        Assert.Equal(3, loads);
        c.GetOrAdd("d", Load);                            // over the size: the least recently used ("b") goes
        Assert.Equal(3, c.Count);
        Assert.True(c.Contains("a") && !c.Contains("b") && c.Contains("c") && c.Contains("d"));
        Assert.True(c.Bytes <= c.MaxBytes);
        c.GetOrAdd("b", Load);
        Assert.Equal(5, loads);                           // decoded again after it was forgotten
    }

    [Fact]
    public void MissingImagesCostNothingAndRemoveAndClearWork()
    {
        var c = new ImageCache(64 * 64 * 4);
        for (int i = 0; i < 100; i++) Assert.Null(c.GetOrAdd("missing" + i, _ => null));
        Assert.Equal(100, c.Count);
        Assert.Equal(0, c.Bytes);
        c.GetOrAdd("big", _ => Img(128, 128));          // larger than the cache: kept alone (the newest is never dropped)
        Assert.True(c.Contains("big"));
        Assert.Equal(1, c.Count);
        c.Remove("big");
        Assert.Equal(0, c.Bytes);
        c.GetOrAdd("x", _ => Img(8, 8));
        c.Clear();
        Assert.Equal(0, c.Count);
        Assert.Equal(0, c.Bytes);
    }

    [Fact]
    public void ResolverTextureCacheStaysBoundedWhileBrowsingManyTextures()
    {
        var cat = new Catalog(TestEnv.Client);
        var res = new ModelResolver(cat);
        var paths = TestEnv.Client.Glob(@"Creature\*.blp").Take(400).ToList();
        Assert.True(paths.Count >= 200, $"the client has creature textures ({paths.Count})");
        long decoded = 0;
        foreach (var p in paths) decoded += res.LoadBlp(p)?.Pixels.LongLength ?? 0;
        Assert.True(decoded > res.TextureCache.MaxBytes, $"the test decodes more than the cache holds ({decoded >> 20} MB)");
        Assert.True(res.TextureCache.Bytes <= res.TextureCache.MaxBytes, $"the cache keeps at most {res.TextureCache.MaxBytes >> 20} MB ({res.TextureCache.Bytes >> 20} MB)");
        // the most recent textures are still there: going back to them costs nothing
        Assert.Same(res.LoadBlp(paths[^1]), res.LoadBlp(paths[^1]));
    }
}
