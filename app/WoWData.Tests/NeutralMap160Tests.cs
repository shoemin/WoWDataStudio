using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.Casc;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Neutral;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>The modern clients for the 1.60 map tests (read-only, never written): 1.60 = the Battle.net snapshot's
/// <c>_classic_beta_</c>, Classic Era (names) = its <c>_classic_era_</c>. Shared across tests: opened once.</summary>
public static class Neutral160Data
{
    public const string Beta = CascRealStoreTests.ClassicBeta;
    public const string Era = CascRealStoreTests.Bnet + @"\_classic_era_";
    public const uint AzerothWdt = CascRealStoreTests.AzerothWdt, KalimdorWdt = CascRealStoreTests.KalimdorWdt;
    private static readonly Lazy<CascStorage> BetaStorage = new(() => CascStorage.Open(Beta));
    private static readonly Lazy<CascStorage> EraStorage = new(() => CascStorage.Open(Era));
    private static readonly Lazy<ClassicPathResolver> Resolver = new(() => ClassicPathResolver.Build(Neutral112Data.Client, EraStorage.Value));

    public static bool HasBeta => Directory.Exists(Beta);
    public static bool HasNames => Directory.Exists(Era) && Neutral112Data.Available;
    public static CascStorage Storage => BetaStorage.Value;
    /// <summary>"1.12 paths hashed into Classic Era's root", or null when the Era copy or the 1.12 client is missing.</summary>
    public static ClassicPathResolver? Names => HasNames ? Resolver.Value : null;

    public static NeutralMap Map(uint wdtFdid, string name) => Adt160Reader.ReadWdt(Storage.ReadFileDataId(wdtFdid)!, name, Names);

    public static uint WdtOf(string dir) => Names?.FileDataIdOf($@"World\Maps\{dir}\{dir}.wdt")
        ?? throw new InvalidOperationException($"no FileDataID for {dir}.wdt (needs the Era copy)");

    public static Adt160Files Files(NeutralMap map, int x, int y)
    {
        var maid = map.Tile(x, y).Maid!;
        byte[]? Get(int s) => maid[s] == 0 ? null : Storage.ReadFileDataId(maid[s]);
        return new Adt160Files { Maid = maid, Root = Get(0), Obj0 = Get(1), Obj1 = Get(2), Tex0 = Get(3), Lod = Get(4) };
    }

    /// <summary>The inverse of <see cref="Adt160Writer.DefaultLiquidType"/>.</summary>
    public static LiquidKind? DefaultKind(int type) => Array.IndexOf(Adt160Writer.DefaultLiquidType, (ushort)type) is var i and >= 0 ? (LiquidKind)i : null;

    /// <summary>The survey's inferred 1.60 LiquidType -> 1.12 kind table (MAP_SURVEY_160 3.5), until the DB2 reader exists.</summary>
    public static LiquidKind? SurveyKind(int type) => type switch { 1250 or 2 => LiquidKind.Ocean, 7 or 1267 => LiquidKind.Magma, _ => LiquidKind.River };
}

/// <summary>A test that needs the 1.60 copy (and, with <c>names</c>, the Era copy and the 1.12 client).</summary>
public sealed class Casc160FactAttribute : FactAttribute
{
    public Casc160FactAttribute(bool names = false)
    {
        if (!Neutral160Data.HasBeta) Skip = $"needs the 1.60 copy at {Neutral160Data.Beta}";
        else if (names && !Neutral160Data.HasNames) Skip = $"needs the Era copy at {Neutral160Data.Era} and the 1.12 client";
    }
}

/// <summary>
/// Milestones 4 and 7 of MAP_CONVERSION_DESIGN.md on the real data: (a) 1.60 tiles, WDTs and WDLs read into the neutral
/// model and written back are byte-identical and read back to an equal model; (b) stock 1.12 tiles written as 1.60 and
/// back give the original 1.12 bytes wherever 1.60 can hold their content, and every other tile carries the report
/// entry that explains it; (c) 1.60 tiles written as 1.12 validate, load, and report their losses.
/// </summary>
public class NeutralMap160RoundTripTests
{
    private readonly ITestOutputHelper _out;

    public NeutralMap160RoundTripTests(ITestOutputHelper output) => _out = output;

    private static string? FirstDiff(string what, byte[]? a, byte[]? b)
    {
        if (a == null && b == null) return null;
        if (a == null || b == null) return $"{what}: one side missing";
        if (a.AsSpan().SequenceEqual(b)) return null;
        int i = 0;
        while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
        return $"{what}: {a.Length} -> {b.Length} bytes, first difference at {i}";
    }

    /// <summary>(a) Every 4th tile of 1.60 Azeroth and Kalimdor (8-bit, compressed alpha, MH2O, MCCV, 5-8 layers) and
    /// every tile of Shadowfang (4-bit alpha, depth-only and flat MH2O): read -> write is byte-identical per file, read
    /// again gives an equal model, the report is empty (Shadowfang: no LOD file); the WDTs and WDLs too.</summary>
    [Casc160Fact]
    public void ModernTilesRoundTripByteIdenticallyAndToAnEqualModel()
    {
        var sw = Stopwatch.StartNew();
        var maps = new List<(NeutralMap Map, int Step)>
        {
            (Neutral160Data.Map(Neutral160Data.AzerothWdt, "Azeroth"), 4),
            (Neutral160Data.Map(Neutral160Data.KalimdorWdt, "Kalimdor"), 4),
        };
        if (Neutral160Data.HasNames) maps.Add((Neutral160Data.Map(Neutral160Data.WdtOf("Shadowfang"), "Shadowfang"), 1));
        var failures = new ConcurrentBag<string>();
        int tiles = 0, chunks = 0, liquids = 0, layers58 = 0, compressed = 0, flatOrDepth = 0;
        var gate = new object();
        foreach (var (map, step) in maps)
        {
            bool big = Adt160Reader.BigAlpha(map.Flags);
            // WDT and WDL
            var wdtBytes = Neutral160Data.Storage.ReadFileDataId(map.Name switch { "Azeroth" => Neutral160Data.AzerothWdt, "Kalimdor" => Neutral160Data.KalimdorWdt, _ => Neutral160Data.WdtOf(map.Name) })!;
            var wr = new ConversionReport();
            Assert.Null(FirstDiff(map.Name + ".wdt", wdtBytes, Adt160Writer.WriteWdt(Adt160Reader.ReadWdt(wdtBytes, map.Name), wr)));
            Assert.True(wr.IsEmpty, wr.ToText());
            var wdlBytes = Neutral160Data.Storage.ReadFileDataId(map.MphdExtra[5])!;
            var wdl = Adt160Reader.ReadWdl(wdlBytes);
            Assert.Null(FirstDiff(map.Name + ".wdl", wdlBytes, Adt160Writer.WriteWdl(wdl, wr)));
            Assert.Equal(map.ExistingTiles().Count(), wdl.Heights.Count);
            var sample = map.ExistingTiles().Where((_, i) => i % step == 0).ToList();
            Parallel.ForEach(sample, new ParallelOptions { MaxDegreeOfParallelism = 8 }, xy =>
            {
                var (x, y) = xy;
                var files = Neutral160Data.Files(map, x, y);
                var tile = Adt160Reader.ReadTile(files, map.Name, x, y, big, Neutral160Data.Names);
                var report = new ConversionReport();
                var back = Adt160Writer.WriteTile(tile, report, new Adt160WriterOptions { BigAlpha = big }, files.Maid);
                foreach (var d in new[]
                         {
                             FirstDiff("root", files.Root, back.Root), FirstDiff("obj0", files.Obj0, back.Obj0), FirstDiff("obj1", files.Obj1, back.Obj1),
                             FirstDiff("tex0", files.Tex0, back.Tex0), FirstDiff("lod", files.Lod, back.Lod),
                         })
                    if (d != null) failures.Add($"{map.Name} {x}_{y} {d}");
                Assert.Equal(files.Maid, back.Maid);
                if (report.Losses.Any(l => !(l.Kind == LossKind.MapFileRefsMissing && files.Lod == null)))
                    failures.Add($"{map.Name} {x}_{y} report: {report.ToText()}");
                var again = Adt160Reader.ReadTile(back, map.Name, x, y, big, Neutral160Data.Names);
                var diffs = NeutralCompare.Tiles(tile, again);
                if (diffs.Count > 0) failures.Add($"{map.Name} {x}_{y} model: {string.Join("; ", diffs.Take(5))}");
                var all = tile.AllChunks.ToList();
                lock (gate)
                {
                    tiles++;
                    chunks += all.Count;
                    liquids += all.Sum(c => c.Liquids.Count);
                    flatOrDepth += all.SelectMany(c => c.Liquids).Count(l => l.Heights == null);
                    layers58 += all.Count(c => c.Layers.Count > 4);
                    compressed += all.SelectMany(c => c.Layers).Count(l => (l.Flags & 0x200) != 0);
                }
            });
        }
        _out.WriteLine($"{tiles} tiles ({chunks} chunks; {liquids} MH2O instances, {flatOrDepth} without heights; {layers58} chunks with 5-8 layers; " +
                       $"{compressed} compressed alpha layers) byte-identical in {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Empty(failures.Take(10));
        Assert.Equal(184 + 247 + (Neutral160Data.HasNames ? 25 : 0), tiles);
        Assert.True(layers58 > 0 && flatOrDepth >= (Neutral160Data.HasNames ? 1 : 0) && compressed > 0);
    }

    private static readonly HashSet<LossKind> FormatNotes160 = new()
    {
        LossKind.AssetIdResolved, LossKind.AssetIdAllocated, LossKind.Obj1Generated, LossKind.MapFileRefsMissing,
        LossKind.LiquidTypeDefaulted, LossKind.LiquidObjectDefaulted,
    };

    /// <summary>What 1.60 cannot hold of a 1.12 tile, each with its report entry (measured on all 2,429 stock tiles: 1,303
    /// tiles have none of these and come back byte for byte).</summary>
    private static readonly HashSet<LossKind> Explained160 = new()
    {
        LossKind.ModelListRebuilt, LossKind.LiquidFlowDropped, LossKind.SoundEmittersDefaulted, LossKind.LiquidMasksMerged, LossKind.AlphaBlendConverted,
    };

    /// <summary>(b) Every 5th stock 1.12 tile -> neutral -> 1.60 (4-bit sequential alpha, the 1.60 instance-map layout)
    /// -> neutral -> 1.12: byte-identical unless the 1.60 writer reported something 1.60 cannot hold; then the content
    /// still agrees except for exactly that. With 8-bit blend weights (every 25th tile) the only new loss is paint hidden
    /// under opaque layers. WDTs and WDLs of every map come back byte for byte.</summary>
    [Client112Fact]
    public void Stock112TilesComeBackByteIdenticalThrough160()
    {
        var resolver = Neutral160Data.Names;
        var names = Neutral112Data.Client.Glob(@"World\Maps\*").Where(n => n.EndsWith(".adt", StringComparison.OrdinalIgnoreCase)).ToList();
        var sw = Stopwatch.StartNew();
        var failures = new ConcurrentBag<string>();
        var counts = new ConcurrentDictionary<string, int>();
        void Count(string k) => counts.AddOrUpdate(k, 1, (_, v) => v + 1);
        foreach (var big in new[] { false, true })
        {
            var sample = names.Where((_, i) => i % (big ? 25 : 5) == 0).ToList();
            Parallel.ForEach(sample, name =>
            {
                var data = Neutral112Data.Client.Read(name)!;
                var (x, y) = Adt112Reader.TileCoords(name)!.Value;
                var map = name.Split('\\')[^2];
                var t1 = Adt112Reader.ReadAdt(data, map, x, y);
                var ids = new AssetIdMap(resolver);   // one map per tile: each keeps its own name spellings
                var r160 = new ConversionReport();
                var files = Adt160Writer.WriteTile(t1, r160, new Adt160WriterOptions { Ids = ids, BigAlpha = big });
                var t2 = Adt160Reader.ReadTile(files, map, x, y, big, new CompositeAssetNames(ids, resolver));
                var r112 = new ConversionReport();
                var back = Adt112Writer.WriteAdt(t2, r112, new Adt112WriterOptions { LiquidKind = Neutral160Data.DefaultKind });
                var lost = r160.Totals().Keys.Where(k => !FormatNotes160.Contains(k)).ToList();
                if (lost.Any(k => !Explained160.Contains(k))) failures.Add($"{name}: unexpected 1.60 report {r160.ToText()}");
                bool same = back.AsSpan().SequenceEqual(data);
                string tag = big ? "8-bit" : "4-bit";
                Count($"{tag} tiles");
                if (same) Count($"{tag} byte-identical");
                if (lost.Count == 0)
                {
                    if (!same) failures.Add($"{name} ({tag}): nothing reported but {FirstDiff("adt", data, back)}");
                    if (r112.Losses.Any(l => l.Kind is not (LossKind.LiquidTypeMapped or LossKind.LodDropped)))
                        failures.Add($"{name} ({tag}): 1.12 report {r112.ToText()}");
                    return;
                }
                foreach (var k in lost) Count($"{tag} reported {k}");
                // the content agrees except for what was reported
                var t3 = Adt112Reader.ReadAdt(back, map, x, y);
                var diffs = NeutralCompare.Tiles(t1, t3, new NeutralCompareOptions { IgnoreModelListOrder = true, IgnoreMclqPassthrough = true }, 5000);
                var lossChunks = r112.Of(LossKind.LiquidInstancesDropped).Select(l => l.Chunk).ToHashSet();
                foreach (var d in diffs)
                {
                    // a chunk with two MCLQ layers (river + ocean) keeps one: its liquid flag bit and that layer go
                    bool ok = (d.Contains("emitter") && lost.Contains(LossKind.SoundEmittersDefaulted))
                              || ((d.Contains("liquid") || d.Contains(" flags:")) && lossChunks.Any(c => d.StartsWith($"chunk {c} ")))
                              || (d.Contains("alpha") && lost.Contains(LossKind.AlphaBlendConverted));
                    if (!ok) failures.Add($"{name} ({tag}): unexplained difference {d}");
                }
            });
        }
        // WDT / WDL of every map
        int maps = 0;
        foreach (var w in Neutral112Data.Client.Glob(@"World\Maps\*").Where(n => n.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase)))
        {
            var data = Neutral112Data.Client.Read(w)!;
            var dir = w.Split('\\')[^2];
            var map = Adt112Reader.ReadWdt(data, dir);
            var ids = new AssetIdMap(resolver);
            var r = new ConversionReport();
            var modern = Adt160Writer.WriteWdt(map, r, new Adt160WriterOptions { Ids = ids });
            var back = Adt112Writer.WriteWdt(Adt160Reader.ReadWdt(modern, dir, new CompositeAssetNames(ids, resolver)), r);
            if (FirstDiff(w, data, back) is { } d) failures.Add(d);
            Assert.DoesNotContain(r.Losses, l => l.Kind is not (LossKind.MapFileRefsMissing or LossKind.MapFileRefsDropped or LossKind.MapFlagsCleared));
            var wdlName = Path.ChangeExtension(w, ".wdl");
            if (Neutral112Data.Client.Read(wdlName) is { } wdl)
            {
                var wr = new ConversionReport();
                var wdlBack = Adt112Writer.WriteWdl(Adt160Reader.ReadWdl(Adt160Writer.WriteWdl(Adt112Reader.ReadWdl(wdl), wr)), wr);
                if (FirstDiff(wdlName, wdl, wdlBack) is { } dd) failures.Add(dd);
                Assert.All(wr.Losses, l => Assert.Equal(LossKind.UnknownChunksDropped, l.Kind));   // 1.60's empty MLDD MLDX MLMD MLMX lists
            }
            maps++;
        }
        _out.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s; {maps} WDT/WDL pairs; " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        foreach (var f in failures.Take(10)) _out.WriteLine(f);
        Assert.Empty(failures.Take(10));
        Assert.Equal(43, maps);
        Assert.Equal(486, counts["4-bit tiles"]);
        Assert.True(counts["4-bit byte-identical"] >= 200, $"{counts["4-bit byte-identical"]} byte-identical");
        Assert.True(counts.GetValueOrDefault("8-bit reported AlphaBlendConverted") > 0);
    }

    /// <summary>(c) Every 8th 1.60 continent tile and every Shadowfang tile written as 1.12: the validator passes without
    /// warnings, the existing readers load it, and the report has an entry for every expected loss. Converted blend
    /// weights show the same picture as 1.60 within one 4-bit step on average.</summary>
    [Casc160Fact]
    public void ModernTilesConvertTo112AndValidate()
    {
        var maps = new List<(NeutralMap Map, int Step)>
        {
            (Neutral160Data.Map(Neutral160Data.AzerothWdt, "Azeroth"), 8),
            (Neutral160Data.Map(Neutral160Data.KalimdorWdt, "Kalimdor"), 8),
        };
        if (Neutral160Data.HasNames) maps.Add((Neutral160Data.Map(Neutral160Data.WdtOf("Shadowfang"), "Shadowfang"), 1));
        var failures = new ConcurrentBag<string>();
        var totals = new ConcurrentDictionary<LossKind, long>();
        double errSum = 0;
        long errN = 0, errBig = 0;
        int tiles = 0;
        var gate = new object();
        foreach (var (map, step) in maps)
        {
            bool big = Adt160Reader.BigAlpha(map.Flags);
            Parallel.ForEach(map.ExistingTiles().Where((_, i) => i % step == 0), new ParallelOptions { MaxDegreeOfParallelism = 8 }, xy =>
            {
                var (x, y) = xy;
                var t = Adt160Reader.ReadTile(Neutral160Data.Files(map, x, y), map.Name, x, y, big, Neutral160Data.Names);
                var r = new ConversionReport();
                var bytes = Adt112Writer.WriteAdt(t, r, new Adt112WriterOptions { LiquidKind = Neutral160Data.SurveyKind });
                var name = $@"World\Maps\{map.Name}\{map.Name}_{x}_{y}.adt";
                var v = MapValidator.ValidateAdt(bytes, name);
                if (!v.Ok || v.Count(MapSeverity.Warning) > 0) failures.Add($"{name}: {string.Join("; ", v.Findings.Take(3))}");
                var adt = new AdtFile(bytes, name);
                var tt = TerrainTile.Load(bytes, map.Name, x, y);
                if (adt.Mcnk.Count(c => c != null) != 256 || tt.AllChunks.Count() != 256) failures.Add($"{name}: does not load");
                foreach (var (k, n) in r.Totals()) totals.AddOrUpdate(k, n, (_, o) => o + n);
                var chunks = t.AllChunks.ToList();
                void Expect(bool cond, LossKind kind, string why)
                {
                    if (cond != r.Of(kind).Any()) failures.Add($"{name}: {why}: {(cond ? "expected" : "unexpected")} {kind}");
                }
                Expect(chunks.Any(c => c.VertexColors != null), LossKind.VertexColorsDropped, "MCCV");
                Expect(chunks.Any(c => c.Layers.Count > 4), LossKind.TextureLayersDropped, "5-8 layers");
                Expect(chunks.Any(c => c.Liquids.Count > 1), LossKind.LiquidInstancesDropped, "several MH2O instances");
                Expect(chunks.Any(c => c.ExtraSubChunks.Count > 0) || t.ExtraChunks.Count > 0, LossKind.UnknownChunksDropped, "MPTX / MCBB / MLMB / blend mesh");
                Expect(t.LodBlob != null || t.Obj1Blob != null, LossKind.LodDropped, "obj1 / lod");
                Expect(chunks.Any(c => c.Liquids.Count > 0), LossKind.LiquidTypeMapped, "liquids");
                if (r.Of(LossKind.TextureLayersDropped).Sum(l => l.Count) != chunks.Sum(c => Math.Max(0, c.Layers.Count - 4)))
                    failures.Add($"{name}: dropped layer count");
                // the picture: visible weights of the 1.12 result (sequential, 4-bit) against 1.60's (2-4 layer chunks)
                if (big)
                {
                    var back = Adt112Reader.ReadAdt(bytes, map.Name, x, y);
                    double sum = 0;
                    long n = 0, bigErr = 0;
                    for (int c = 0; c < 256; c++)
                    {
                        var a = t.Chunks[c]!;
                        var b = back.Chunks[c]!;
                        if (a.Layers.Count < 2 || a.Layers.Count > 4) continue;
                        var wa = AlphaBlending.VisibleWeights(a.Layers, AlphaBlendMode.Weights);
                        var wb = AlphaBlending.VisibleWeights(b.Layers, AlphaBlendMode.Sequential);
                        for (int L = 0; L < a.Layers.Count; L++)
                            for (int k = 0; k < 4096; k++)
                            {
                                double e = Math.Abs(wa[L][k] - wb[L][k]);
                                sum += e;
                                n++;
                                if (e > 2.0 / 15) bigErr++;
                            }
                    }
                    lock (gate)
                    {
                        errSum += sum;
                        errN += n;
                        errBig += bigErr;
                    }
                }
                lock (gate) tiles++;
            });
        }
        _out.WriteLine($"{tiles} tiles; visible-weight error of converted blends: mean {errSum / Math.Max(1, errN):F4}, more than two 4-bit steps in {100.0 * errBig / Math.Max(1, errN):F3}% of layer texels");
        _out.WriteLine(string.Join("\n", totals.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}")));
        Assert.Empty(failures.Take(10));
        Assert.True(errSum / errN < 1.0 / 30, $"mean error {errSum / errN}");
        Assert.True(errBig < errN / 1000, $"{errBig} of {errN}");
        Assert.True(totals.ContainsKey(LossKind.AlphaReduced) && totals.ContainsKey(LossKind.VertexColorsDropped) && totals.ContainsKey(LossKind.TextureLayersDropped));
    }

    /// <summary>The settled questions, measured on the data each time: flat MH2O instances have min == max (so min and max
    /// give the same surface), MH2O attribute blocks follow the "not pure ocean" rule, 8-bit alpha maps are weights.</summary>
    [Casc160Fact]
    public void MeasuredFormatFactsStillHold()
    {
        var map = Neutral160Data.Map(Neutral160Data.KalimdorWdt, "Kalimdor");
        int flat = 0, attrZero = 0, oceanNoAttr = 0, overweight = 0;
        foreach (var (x, y) in map.ExistingTiles().Where((_, i) => i % 10 == 0))
        {
            var t = Adt160Reader.ReadTile(Neutral160Data.Files(map, x, y), "Kalimdor", x, y, true);
            Assert.Equal(AlphaBlendMode.Weights, t.AlphaBlend);
            foreach (var c in t.AllChunks)
            {
                foreach (var l in c.Liquids.Where(l => l.Heights == null))
                {
                    flat++;
                    Assert.Equal(l.Min, l.Max);
                }
                if (c.Liquids.Count > 0 && c.Liquids.All(l => l.LiquidType == 1250) && c.Liquids.All(l => (l.Fishable | l.Deep) == 0))
                {
                    Assert.False(c.Mh2oAttributes160);
                    oceanNoAttr++;
                }
                if (c.Mh2oAttributes160 == true && c.Liquids.All(l => (l.Fishable | l.Deep) == 0)) attrZero++;
                for (int k = 0; k < 4096; k++)
                    if (c.Layers.Skip(1).Sum(l => l.Alpha?[k] ?? 0) > 255) overweight++;
            }
        }
        // the 4-bit instance maps: sequential alphas (overlapping layers), flat / depth-only liquids with min == max
        int seqOverlap = 0;
        if (Neutral160Data.HasNames)
        {
            var sf = Neutral160Data.Map(Neutral160Data.WdtOf("Shadowfang"), "Shadowfang");
            foreach (var (x, y) in sf.ExistingTiles())
            {
                var t = Adt160Reader.ReadTile(Neutral160Data.Files(sf, x, y), "Shadowfang", x, y, Adt160Reader.BigAlpha(sf.Flags));
                Assert.Equal(AlphaBlendMode.Sequential, t.AlphaBlend);
                foreach (var c in t.AllChunks)
                {
                    foreach (var l in c.Liquids.Where(l => l.Heights == null))
                    {
                        flat++;
                        Assert.Equal(l.Min, l.Max);
                    }
                    for (int k = 0; k < 4096; k++)
                        if (c.Layers.Skip(1).Sum(l => (l.Alpha?[k] ?? 0) >> 4) > 15) seqOverlap++;
                }
            }
            Assert.True(flat > 0 && seqOverlap > 0);
        }
        _out.WriteLine($"flat instances {flat}, pure-ocean chunks without attributes {oceanNoAttr}, zero attribute blocks {attrZero}, " +
                       $"8-bit texels over 255 {overweight}, 4-bit texels over 15 {seqOverlap}");
        Assert.Equal(0, overweight);
        Assert.True(oceanNoAttr > 1000 && attrZero > 0);
    }
}

/// <summary>The 1.60 reader and writer on content built in code (no game data): compressed alpha, the name map, a 1.12-style
/// tile written as 1.60 files, the blend-mode conversion, MH2O rectangles and vertex formats, WDT / WDL.</summary>
public class NeutralMap160UnitTests
{
    [Fact]
    public void CompressedAlphaRoundTripsAndFollowsBlizzardsRowRule()
    {
        var rnd = new Random(7);
        for (int n = 0; n < 50; n++)
        {
            var a = new byte[4096];
            for (int i = 0; i < 4096; i++) a[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : (i / 97) * 13);
            var enc = Adt160Writer.EncodeAlpha(a);
            Assert.Equal(a, Adt160Reader.DecodeAlpha(enc, 0, enc.Length, out long end));
            Assert.Equal(enc.Length, end);
        }
        // a row ending in "... fill, 0xA8, 0xFF": the last texel never joins a copy (01 A8 01 FF, as in 1.60's files)
        var row = new byte[4096];
        for (int i = 0; i < 62; i++) row[i] = 0xFF;
        row[62] = 0xA8;
        row[63] = 0xFF;
        var e = Adt160Writer.EncodeAlpha(row);
        Assert.Equal(new byte[] { 0x80 | 62, 0xFF, 0x01, 0xA8, 0x01, 0xFF }, e.Take(6).ToArray());
        // a full constant row is one fill packet of 64
        Assert.Equal(new byte[] { 0x80 | 64, 0x00 }, e.Skip(6).Take(2).ToArray());
        Assert.Equal(6 + 63 * 2, e.Length);
    }

    private sealed class FakeNames : IAssetNames
    {
        public readonly Dictionary<string, uint> Ids = new(StringComparer.OrdinalIgnoreCase);
        public uint? FileDataIdOf(string path) => Ids.TryGetValue(WoWData.Core.Mpq.ClientData.ResolveName(path), out var v) ? v : null;
        public string? PathOf(uint id) => Ids.FirstOrDefault(kv => kv.Value == id).Key;
    }

    [Fact]
    public void AssetIdMapResolvesAllocatesAndRemembers()
    {
        var names = new FakeNames();
        names.Ids[@"Tileset\Elwynn\ElwynnGrass_s.blp"] = 1001;
        names.Ids[@"World\Generic\Tree.m2"] = 2002;
        var map = new AssetIdMap(names, new LocalIdAllocator(5000));
        Assert.Equal(1001u, map.IdOf(@"Tileset\Elwynn\ElwynnGrass.blp", "texture", out var o1, @"Tileset\Elwynn\ElwynnGrass_s.blp", @"Tileset\Elwynn\ElwynnGrass.blp"));
        Assert.Equal(AssetIdOrigin.Resolved, o1);
        Assert.Equal(2002u, map.IdOf(@"WORLD\GENERIC\TREE.MDX", "doodad model", out _));   // .mdx is the .m2
        Assert.Equal(5000u, map.IdOf(@"World\Custom\New.m2", "doodad model", out var o3));
        Assert.Equal(AssetIdOrigin.Allocated, o3);
        Assert.Equal(5000u, map.IdOf(@"world/custom/new.m2", "doodad model", out _));   // same path, same ID
        Assert.Equal(5001u, map.IdOf(@"World\Custom\Other.m2", "doodad model", out _));
        Assert.Equal(@"Tileset\Elwynn\ElwynnGrass.blp", map.PathOf(1001));   // the model's path, not the _s file's
        Assert.Equal(2, map.Allocated.Count());
        Assert.Contains("5000\tAllocated\tdoodad model\tWorld\\Custom\\New.m2", map.ToText());
        var composite = new CompositeAssetNames(map, names);
        Assert.Equal(2002u, composite.FileDataIdOf(@"World\Generic\Tree.m2"));
        Assert.Equal(@"World\Custom\Other.m2", composite.PathOf(5001));
    }

    private static byte[] Band(Func<int, int, bool> inside, byte value)
    {
        var a = new byte[4096];
        for (int r = 0; r < 64; r++)
            for (int c = 0; c < 64; c++)
                if (inside(r, c)) a[r * 64 + c] = value;
        return a;
    }

    /// <summary>A 1.12-style tile: three overlapping layers, water and magma MCLQ layers, holes, a shadow, a 1.12 sound
    /// emitter, placements named by path only.</summary>
    private static NeutralTile Tile112()
    {
        var t = NeutralTile.CreateFlat("Test", 30, 40, 10f);
        t.Source = MapSourceVersion.V112;
        var tex = Enumerable.Range(0, 3).Select(i => new AssetRef($@"Tileset\Test\t{i}.blp")).ToList();
        t.Textures.AddRange(tex);
        var ch = t.Chunks[17]!;
        ch.Layers.Add(new NeutralLayer { Texture = tex[0] });
        ch.Layers.Add(new NeutralLayer { Texture = tex[1], Flags = 0x100, Alpha = Band((r, _) => r < 40, NeutralLayer.Expand4(15)) });
        ch.Layers.Add(new NeutralLayer { Texture = tex[2], Flags = 0x100, EffectId = 7, Alpha = Band((r, _) => r >= 20, NeutralLayer.Expand4(7)) });
        ch.Holes = NeutralChunk.ExpandLowResHoles(0x8001);
        ch.Shadow = Enumerable.Range(0, 4096).Select(i => i % 5 == 0).ToArray();
        ch.Flags = 0x1;
        ch.NormalPad112 = Enumerable.Range(1, 13).Select(i => (byte)i).ToArray();
        var water = new NeutralLiquid { Origin = LiquidOrigin.Mclq, Kind = LiquidKind.River, Min = 5, Max = 6, Exists = ulong.MaxValue >> 8, Fishable = 0xFF, Heights = new float[81], Depth = new byte[81] };
        for (int v = 0; v < 81; v++)
        {
            water.Heights[v] = 5 + v * 0.01f;
            water.Depth[v] = (byte)v;
        }
        var magma = new NeutralLiquid { Origin = LiquidOrigin.Mclq, Kind = LiquidKind.Magma, Min = 20, Max = 20, Exists = 0xF0F0, Heights = Enumerable.Repeat(20f, 81).ToArray(), TexU = new ushort[81], TexV = Enumerable.Range(0, 81).Select(i => (ushort)i).ToArray() };
        t.Chunks[3]!.Liquids.Add(water);
        t.Chunks[4]!.Liquids.Add(magma);
        t.Chunks[5]!.SoundEmitters.Add(new NeutralSoundEmitter { Id = 9, Position = new Vector3(1, 2, 3), Raw112 = new byte[MapBytes.McseSize] });
        var tree = new AssetRef(@"World\Generic\Tree.mdx");
        t.DoodadModels.Add(tree);
        t.Doodads.Add(new NeutralDoodad { Model = tree, UniqueId = 1, Position = new Vector3(100, 10, 200), ScaleRaw = 1024 });
        ch.DoodadRefs.Add(0);
        var house = new AssetRef(@"World\wmo\Test\House.wmo");
        t.WmoModels.Add(house);
        t.Wmos.Add(new NeutralWmo { Model = house, UniqueId = 2, ExtentMax = new Vector3(5, 5, 5) });
        ch.WmoRefs.Add(0);
        return t;
    }

    private static List<string> Tags(byte[] d) => MapBytes.Chunks(d).Select(c => c.Magic!).ToList();

    [Fact]
    public void A112StyleTileWritesAsModernFilesAndReadsBack()
    {
        var t = Tile112();
        var ids = new AssetIdMap();
        var report = new ConversionReport();
        var files = Adt160Writer.WriteTile(t, report, new Adt160WriterOptions { Ids = ids });
        // the 1.60 layout
        Assert.Equal(new[] { "MVER", "MHDR", "MH2O" }.Concat(Enumerable.Repeat("MCNK", 256)), Tags(files.Root!));
        Assert.Equal(new[] { "MVER", "MAMP", "MDID", "MHID" }.Concat(Enumerable.Repeat("MCNK", 256)), Tags(files.Tex0!));
        Assert.Equal(new[] { "MVER", "MDDF", "MODF" }.Concat(Enumerable.Repeat("MCNK", 256)), Tags(files.Obj0!));
        Assert.Equal(new[] { "MVER", "MLFD", "MLDD", "MLDX", "MLMD", "MLMX" }, Tags(files.Obj1!));
        Assert.Null(files.Lod);
        Assert.Equal(0u, files.Maid![4]);
        Assert.All(files.Maid.Take(4), v => Assert.True(v >= LocalIdAllocator.DefaultFirst));
        // every path-only asset got a local ID, listed in the map; ground textures were asked for as _s.blp first
        Assert.Equal(new[] { LossKind.AssetIdAllocated, LossKind.SoundEmittersDefaulted, LossKind.LiquidTypeDefaulted, LossKind.LiquidObjectDefaulted, LossKind.Obj1Generated, LossKind.MapFileRefsMissing }.OrderBy(k => k),
            report.Totals().Keys.OrderBy(k => k));
        Assert.Equal(5, report.Of(LossKind.AssetIdAllocated).Single().Count);   // 3 textures, 1 doodad, 1 WMO
        Assert.Equal(2, report.Of(LossKind.LiquidTypeDefaulted).Single().Count);
        // MCLQ water -> type 5, LVF 0 (height + depth); magma -> type 7, LVF 1 (height + uv): the format number in the
        // LiquidObject field; an attribute block for both (not ocean)
        var root = files.Root!;
        long mh2o = MapBytes.Chunks(root).First(c => c.Magic == "MH2O").Offset + 8;
        uint inst3 = MapBytes.U32(root, mh2o + 3 * 12);
        Assert.Equal((5, 0), (BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan((int)(mh2o + inst3))), BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan((int)(mh2o + inst3 + 2)))));
        uint inst4 = MapBytes.U32(root, mh2o + 4 * 12);
        Assert.Equal((7, 1), (BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan((int)(mh2o + inst4))), BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan((int)(mh2o + inst4 + 2)))));
        Assert.NotEqual(0u, MapBytes.U32(root, mh2o + 3 * 12 + 8));
        Assert.NotEqual(0u, MapBytes.U32(root, mh2o + 4 * 12 + 8));
        // read back: equal content (sequential 4-bit alpha kept as is)
        var back = Adt160Reader.ReadTile(files, "Test", 30, 40, false, ids);
        Assert.Equal(AlphaBlendMode.Sequential, back.AlphaBlend);
        var diffs = NeutralCompare.Tiles(t, back, new NeutralCompareOptions { IgnoreStorageDetails = true, IgnoreMclqPassthrough = true });
        Assert.Equal(new[] { "chunk 5 emitter 0 raw: differs != " }.Select(s => s.Split(':')[0]), diffs.Select(s => s.Split(':')[0]));
        Assert.Equal(@"World\Generic\Tree.mdx", back.Doodads[0].Model.Path);
        Assert.Equal(0x10001u, back.Chunks[17]!.Flags);   // shadow flag kept, high-res holes flag added
        Assert.Equal(0xCu, (uint)back.Wmos[0].Flags);      // FileDataID + scale flags, scale 1.0
        Assert.Equal((ushort)1024, back.Wmos[0].ScaleRaw);
        Assert.Equal((ushort)0x40, back.Doodads[0].Flags);
        // and as 1.12 again: the same bytes as the 1.12 writer makes of the original (the emitter written as id + position)
        var a112 = Adt112Writer.WriteAdt(t);
        var b112 = Adt112Writer.WriteAdt(back, null, new Adt112WriterOptions { LiquidKind = Neutral160Data.DefaultKind });
        var diff = Enumerable.Range(0, a112.Length).Where(i => a112[i] != b112[i]).ToList();
        Assert.Equal(a112.Length, b112.Length);
        Assert.All(diff, i => Assert.InRange(i, new AdtFile(a112).Mcnk[5]!.Offset, new AdtFile(a112).Mcnk[5]!.End));
    }

    [Fact]
    public void SequentialAlphasBecomeWeightsAndBack()
    {
        var t = Tile112();
        var report = new ConversionReport();
        var files = Adt160Writer.WriteTile(t, report, new Adt160WriterOptions { BigAlpha = true });
        var conv = report.Of(LossKind.AlphaBlendConverted).Single();
        Assert.Equal(1, conv.Count);
        // layer 1 (15 in rows 0..39) is fully under layer 2 nowhere (layer 2 is 7, not opaque): nothing hidden
        Assert.Contains(" 0 layer texels had paint hidden", conv.Message);
        var back = Adt160Reader.ReadTile(files, "Test", 30, 40, true);
        Assert.Equal(AlphaBlendMode.Weights, back.AlphaBlend);
        var l1 = back.Chunks[17]!.Layers[1].Alpha!;
        var l2 = back.Chunks[17]!.Layers[2].Alpha!;
        // rows 20..39: layer 2 (alpha 127/255) over layer 1 (255): weights 127 and 128; rows 0..19: 255 and 0; rows 40..: 0 and 127
        Assert.Equal((128, 127), (l1[30 * 64], l2[30 * 64]));
        Assert.Equal((255, 0), (l1[5 * 64], l2[5 * 64]));
        Assert.Equal((0, 127), (l1[50 * 64], l2[50 * 64]));
        Assert.All(Enumerable.Range(0, 4096), k => Assert.True(l1[k] + l2[k] <= 255));
        // the 1.12 writer turns the weights back into the original 4-bit sequential alphas
        var b112 = Adt112Writer.WriteAdt(back, null, new Adt112WriterOptions { LiquidKind = Neutral160Data.DefaultKind });
        var r = Adt112Reader.ReadAdt(b112, "Test", 30, 40).Chunks[17]!;
        Assert.Equal(t.Chunks[17]!.Layers[1].Alpha, r.Layers[1].Alpha);
        Assert.Equal(t.Chunks[17]!.Layers[2].Alpha, r.Layers[2].Alpha);
        // paint under an opaque layer is lost, and reported
        var t2 = Tile112();
        t2.Chunks[17]!.Layers[2].Alpha = Band((r0, _) => r0 >= 20, 255);
        var rep2 = new ConversionReport();
        Adt160Writer.WriteTile(t2, rep2, new Adt160WriterOptions { BigAlpha = true });
        Assert.Contains($" {20 * 64} layer texels had paint hidden", rep2.Of(LossKind.AlphaBlendConverted).Single().Message);
        // 4-bit output of a weights tile: back to sequential alphas
        var rep3 = new ConversionReport();
        var f4 = Adt160Writer.WriteTile(back, rep3, new Adt160WriterOptions { BigAlpha = false });
        Assert.Single(rep3.Of(LossKind.AlphaBlendConverted));
        var seq = Adt160Reader.ReadTile(f4, "Test", 30, 40, false).Chunks[17]!;
        Assert.Equal(t.Chunks[17]!.Layers[1].Alpha, seq.Layers[1].Alpha);
        Assert.Equal(t.Chunks[17]!.Layers[2].Alpha, seq.Layers[2].Alpha);
    }

    [Fact]
    public void Mh2oRectanglesVertexFormatsAndMasksRoundTrip()
    {
        var t = NeutralTile.CreateFlat("Test", 1, 2, 0f);
        t.Source = MapSourceVersion.V160;
        t.AlphaBlend = AlphaBlendMode.Weights;
        NeutralLiquid L(int type, int obj, int rx, int ry, int rw, int rh, bool h, bool uv, bool depth, ulong exists)
        {
            int nv = (rw + 1) * (rh + 1);
            return new NeutralLiquid
            {
                LiquidType = type, LiquidObjectOrVertexFormat = obj, Min = 3, Max = h ? 4 : 3, RectX = rx, RectY = ry, RectW = rw, RectH = rh, Exists = exists,
                Heights = h ? Enumerable.Range(0, nv).Select(i => 3 + i * 0.01f).ToArray() : null,
                TexU = uv ? Enumerable.Range(0, nv).Select(i => (ushort)i).ToArray() : null,
                TexV = uv ? Enumerable.Range(0, nv).Select(i => (ushort)(i * 2)).ToArray() : null,
                Depth = depth ? Enumerable.Range(0, nv).Select(i => (byte)i).ToArray() : null,
            };
        }
        ulong Rect(int rx, int ry, int rw, int rh)
        {
            ulong m = 0;
            for (int y = ry; y < ry + rh; y++)
                for (int x = rx; x < rx + rw; x++) m |= 1UL << (y * 8 + x);
            return m;
        }
        t.Chunks[0]!.Liquids.Add(L(1250, 42, 0, 0, 8, 8, true, false, true, ulong.MaxValue));                     // LVF 0
        t.Chunks[1]!.Liquids.Add(L(7, 300, 2, 1, 4, 3, true, true, false, Rect(2, 1, 4, 3) & ~(1UL << 10)));     // LVF 1, a dry tile
        t.Chunks[2]!.Liquids.Add(L(2, 50, 0, 0, 8, 8, false, false, true, ulong.MaxValue));                      // LVF 2
        t.Chunks[3]!.Liquids.Add(L(1284, 351, 1, 1, 5, 5, true, true, true, Rect(1, 1, 5, 5)));                  // LVF 3
        t.Chunks[4]!.Liquids.Add(L(2, 50, 0, 0, 8, 8, false, false, false, ulong.MaxValue));                     // no vertex data
        // two instances with different masks: merged into one chunk mask
        var a = L(5, 60, 0, 0, 8, 8, true, false, true, 0xFF);
        var b = L(1240, 61, 0, 0, 8, 8, true, false, true, 0xFF00);
        a.Fishable = 0x0F;
        b.Deep = 0xF000;
        t.Chunks[5]!.Liquids.AddRange(new[] { a, b });
        var report = new ConversionReport();
        var files = Adt160Writer.WriteTile(t, report, new Adt160WriterOptions());
        Assert.Equal(1, report.Of(LossKind.LiquidMasksMerged).Single().Count);
        var back = Adt160Reader.ReadTile(files, "Test", 1, 2, true);
        a.Deep = b.Deep;
        b.Fishable = a.Fishable;
        // no attribute block where no mask is set (the source chunks did not say), one where a mask is set
        Assert.Equal(new bool?[] { false, false, false, false, false, true }, Enumerable.Range(0, 6).Select(c => back.Chunks[c]!.Mh2oAttributes160));
        Assert.Empty(NeutralCompare.Tiles(t, back, new NeutralCompareOptions { IgnoreStorageDetails = true }));
        Assert.Equal(new[] { 1250, 7, 2, 1284, 2 }, Enumerable.Range(0, 5).Select(c => back.Chunks[c]!.Liquids[0].LiquidType!.Value));
        Assert.Null(back.Chunks[4]!.Liquids[0].Heights);
        Assert.Null(back.Chunks[2]!.Liquids[0].Heights);
        Assert.NotNull(back.Chunks[2]!.Liquids[0].Depth);
        // written again: the same bytes
        var again = Adt160Writer.WriteTile(back, null, new Adt160WriterOptions());
        Assert.Equal(files.Root, again.Root);
    }

    [Fact]
    public void A112MapGetsAModernWdtWithMaidAndAWdl()
    {
        var map = new NeutralMap { Name = "Test", Source = MapSourceVersion.V112 };
        map.Tile(30, 40).HasAdt = true;
        map.Tile(30, 40).Flags = 1;
        map.Tile(31, 40).HasAdt = true;
        map.Tile(31, 40).Flags = 1;
        map.Tile(30, 40).Minimap = new AssetRef(@"textures\Minimap\0123abcd.blp");
        var ids = new AssetIdMap();
        var tileFiles = Adt160Writer.WriteTile(NeutralTile.CreateFlat("Test", 30, 40), null, new Adt160WriterOptions { Ids = ids });
        var report = new ConversionReport();
        var wdt = Adt160Writer.WriteWdt(map, report, new Adt160WriterOptions { Ids = ids }, new Dictionary<int, uint[]> { [40 * 64 + 30] = tileFiles.Maid! });
        Assert.Equal(new[] { "MVER", "MPHD", "MAIN", "MAID" }, Tags(wdt));
        var back = Adt160Reader.ReadWdt(wdt, "Test", ids);
        Assert.Equal(0x200u, back.Flags);   // MAID; 4-bit alpha (the default for 1.12 content)
        Assert.Equal(tileFiles.Maid!.Take(4), back.Tile(30, 40).Maid!.Take(4));
        Assert.Equal(@"textures\Minimap\0123abcd.blp", back.Tile(30, 40).Minimap!.Path);
        Assert.Equal(@"World\Maps\Test\Test_31_40_obj0.adt", ids.PathOf(back.Tile(31, 40).Maid![1]));
        Assert.Equal(@"World\Maps\Test\Test.wdl", ids.PathOf(back.MphdExtra[5]));
        Assert.Equal(new[] { (30, 40), (31, 40) }, back.ExistingTiles());
        Assert.Contains(report.Losses, l => l.Kind == LossKind.AssetIdAllocated && l.Message.Contains("minimap"));
        var wdl = new NeutralWdl();
        wdl.Heights[40 * 64 + 30] = Enumerable.Range(0, 545).Select(i => (short)i).ToArray();
        var wb = Adt160Writer.WriteWdl(wdl);
        Assert.Equal(new[] { "MVER", "MLDD", "MLDX", "MLMD", "MLMX", "MAOF", "MARE", "MAHO" }, Tags(wb));
        var w2 = Adt160Reader.ReadWdl(wb);
        Assert.Equal(wdl.Heights[40 * 64 + 30], w2.Heights[40 * 64 + 30]);
        Assert.Empty(w2.Holes);
        var maho = MapBytes.Chunks(wb).Single(c => c.Magic == "MAHO");
        Assert.Equal(32, maho.Size);
        Assert.All(wb.AsSpan((int)maho.Offset + 8, 32).ToArray(), v => Assert.Equal(0, v));   // 1.60 writes a (zero) MAHO after every MARE
    }
}
