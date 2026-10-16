using System.Diagnostics;
using System.Linq;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Usage;
using WoWDataStudio.Rendering;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.6.1: every timing limit of the suite, in one place. The class runs alone (its collection disables parallel runs, so
/// xUnit runs it after the parallel tests), each measurement is repeated and the best run counts, and the limits have a
/// margin of about 3x over what this machine measures when it is idle. Functional tests and the UI smoke test only log
/// their timings: a busy machine (another build, the game, a video) makes them slower without anything being wrong.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class PerformanceTests
{
    private readonly ITestOutputHelper _out;
    public PerformanceTests(ITestOutputHelper output) => _out = output;

    /// <summary>The best of <paramref name="runs"/> timings of <paramref name="a"/> (milliseconds).</summary>
    private static double Best(int runs, Action a)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void BlpDecodeAndEncodeLike()
    {
        var e = BlpOracle.List("samples").First(x => (string?)x["label"] == "dxt1 1-bit alpha");
        var b = BlpFile.Parse(BlpOracle.Bytes((string)e["stem"]! + ".blp"));
        var img = b.Decode(0);
        var b5 = BlpFile.Parse(BlpEncoder.EncodeNew(ImageOps.Resize(img, 512, 512), "dxt5"));
        b5.Decode(0);
        double decode = Best(10, () => b5.Decode(0));
        BlpEncoder.EncodeLike(b, img);
        double encode = Best(10, () => BlpEncoder.EncodeLike(b, img));
        _out.WriteLine($"512x512 DXT5 decode {decode:F2} ms (limit 40); EncodeLike unmodified 256x256 DXT1a {encode:F2} ms (limit 50)");
        Assert.True(decode < 40, $"decode took {decode:F1} ms");
        Assert.True(encode < 50, $"EncodeLike took {encode:F1} ms");
    }

    [Fact]
    public void ModelsLoadAndAnimateFast()
    {
        var client = TestEnv.Client;
        var cat = new Catalog(client);
        var res = new ModelResolver(cat);
        _ = cat.CreatureDisplays;
        _ = res.AnimationNames;
        foreach (var path in new[] { @"Creature\Boar\Boar.m2", @"Character\Orc\Male\OrcMale.m2", @"Creature\Wolf\Wolf.m2" })
        {
            var data = client.Read(path)!;
            double ms = Best(3, () => ModelAsset.Build(path, data, res.LoadBlp));
            _out.WriteLine($"{path}: build {ms:0.0} ms (limit 200)");
            Assert.True(ms < 200, $"{path} took {ms:0} ms to load");
        }
        var orc = M2Model.Load(client.Read(@"Character\Orc\Male\OrcMale.m2")!);
        var mats = new System.Numerics.Matrix4x4[orc.Bones.Length];
        var done = new bool[orc.Bones.Length];
        double perFrame = Best(3, () =>
        {
            for (int i = 0; i < 200; i++)
                M2Anim.BoneMatrices(orc, new M2Anim.Time(5, orc.Sequences[5].Start, (uint)(i % (int)Math.Max(1, orc.Sequences[5].Duration)), i), mats, done);
        }) / 200;
        _out.WriteLine($"orc male bone matrices: {perFrame:0.000} ms per frame (limit 2)");
        Assert.True(perFrame < 2, $"{perFrame} ms per frame for the bones");
    }

    /// <summary>"Used in game" reports come from indexed queries: each well under a frame of a second (best of 5 runs; the
    /// panel itself loads on a background thread).</summary>
    [Fact]
    public void UsedInGamePanelLoadsUnder100Milliseconds()
    {
        var f = IndexFixture.Instance;
        using var svc = new UsageService(f.Path);
        var keys = new List<string> { @"file:Creature\Boar\BoarSkinIvory.blp", @"file:Tileset\Durotar\DurotarDirt.blp", "race:2:0",
            f.Entries.First(e => e.Kind == AssetKind.SpellEffect && e.Name == "Fire Precast Hand").Key };
        keys.Add(f.Entries.First(e => e.Kind == AssetKind.WorldObject && e.Path != null && e.Path.EndsWith("\\DurotarPalm01.m2", StringComparison.OrdinalIgnoreCase)).Key);
        if (f.HasServerData) keys.AddRange(new[] { "creature:3098", "item:2880", "item:12282", "go:1618", "creature:3143" });
        svc.Warm();
        foreach (var k in keys) svc.Report(k);
        foreach (var k in keys)
        {
            double ms = Best(5, () => svc.Report(k));
            _out.WriteLine($"{k}: {ms:0.0} ms (limit 100)");
            Assert.True(ms < 100, $"{k} took {ms:0} ms");
        }
    }
}
