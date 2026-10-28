using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using WoWData.Core.Casc;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Versions;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// M4 on the real "1.60" Classic beta copy (opt-in: WOWDATA_CLIENT_COPIES; read only): the maps of the Map table with
/// their WDTs and tiles, a tile's chunk heights against <see cref="Adt160Reader"/>, a sample of maps whose tiles all load
/// as terrain (layers, liquids, placements counted), a sample of buildings read in the WMO FileDataID form (every root
/// and group), and the minimap overview of Azeroth. Every number was measured on the copy of 2026-10-20 (a snapshot:
/// the build cannot change). Names come from the client's own hashes only (no index is built here): files are named
/// "#FileDataID" as the session does for files it has no name for.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class RealCopyM4Tests
{
    private readonly ITestOutputHelper _out;

    public RealCopyM4Tests(ITestOutputHelper output) => _out = output;

    private static readonly Regex IdName = new(@"^#(\d+)", RegexOptions.CultureInvariant);

    /// <summary>A file by "#id[.ext]" (what the catalog names files by here); null when it cannot be read.</summary>
    private static byte[]? Read(CascStorage st, string path)
    {
        var m = IdName.Match(path);
        if (!m.Success) return null;
        try { return st.ReadFileDataId(uint.Parse(m.Groups[1].Value)); }
        catch (Exception e) when (e is CascMissingException or CascEncryptedException) { return null; }
    }

    private sealed class Opened : IDisposable
    {
        public OtherClient Client = null!;
        public CascStorage St => Client.Casc!;
        public Casc160MapSource Source = null!;
        public MapCatalog160 Cat = null!;
        public void Dispose() => Client.Dispose();
    }

    private static Opened Open()
    {
        var copy = ClientCopies.Inspect(ClientCopyFactAttribute.PathOf(RealCopyOpenTests.Beta)!)!;
        var o = new Opened { Client = OtherClient.Open(copy) };
        o.Source = new Casc160MapSource(o.Client, new CascAssetNames(o.St), "the client's own names");
        o.Cat = MapCatalog160.Load(o.Source, (id, ext) => $"#{id}{ext}", null);
        return o;
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void TheMapsHaveTheirTilesAndTheLockedRowsAreCounted()
    {
        using var o = Open();
        var cat = o.Cat;
        var az = cat.ByDirectory("Azeroth")!;
        var kal = cat.ByDirectory("Kalimdor")!;
        _out.WriteLine($"maps {cat.Maps.Count}, listed {MapOperations.ListedMaps(cat).Count}, tiles {cat.Maps.Sum(m => m.TileCount)}, WMO-only {cat.Maps.Count(m => m.IsGlobalWmo)}; {cat.StatusText}");
        foreach (var m in cat.Maps.Where(m => m.Note != null)) _out.WriteLine($"  {m.Id} {m.Directory}: {m.Note}");
        Assert.Equal((736, 988), (az.TileCount, kal.TileCount));
        Assert.Equal("Continent", az.Kind);
        Assert.Equal((73, 8, 1375, 46), (cat.MapRows, cat.LockedMapRows, cat.AreaRows, cat.LockedAreaRows));
        Assert.Equal(71, cat.Maps.Count);                       // 73 readable rows, 2 without a WDT
        Assert.Equal(2, cat.UnreadableWdts);                    // 13 "test" and 3109 Manor Mistmantle: not in this copy
        Assert.Equal(21, cat.Maps.Count(m => m.IsGlobalWmo));
        Assert.Equal(3322, cat.Maps.Sum(m => m.TileCount));
        Assert.Equal(71, MapOperations.ListedMaps(cat).Count);
        Assert.Equal("Map table: 73 readable rows, 8 locked (encrypted, no key); AreaTable: 1,375 readable, 46 locked; 2 maps without a readable WDT.", cat.StatusText);
        // zones of Azeroth from the chunk area ids of every tile
        var sw = Stopwatch.StartNew();
        var areas = cat.TileAreas(az);
        var zones = cat.Zones(az, areas);
        _out.WriteLine($"Azeroth: {areas.Count} tiles with area ids, {zones.Count} zones in {sw.ElapsedMilliseconds} ms; Elwynn: {zones.FirstOrDefault(z => z.Name == "Elwynn Forest")?.Label}");
        Assert.Equal(736, areas.Count);
        Assert.Contains(zones, z => z.Name == "Elwynn Forest");
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void ATilesChunkHeightsEqualTheNeutralReaders()
    {
        using var o = Open();
        var az = o.Cat.ByDirectory("Azeroth")!;
        var src = o.Source.Maps().Single(m => m.Directory == "Azeroth");
        var wdt = o.Source.ReadWdt(src);
        var n = o.Source.ReadTile(src, wdt, 32, 48);
        var t = o.Cat.LoadTerrain(az, 32, 48)!;
        Assert.Equal(256, t.AllChunks.Count());
        foreach (var c in t.AllChunks)
        {
            var nc = n.Chunks[c.Index]!;
            for (int k = 0; k < 145; k++) Assert.Equal(nc.Position.Z + nc.Heights[k], c.Heights[k]);
            Assert.Equal(Math.Min(8, nc.Layers.Count), c.Layers.Count);
            Assert.Equal(nc.Liquids.Count, c.Liquids.Count);
        }
        Assert.Equal((n.Doodads.Count, n.Wmos.Count), (t.Doodads.Count, t.Wmos.Count));
        var (lo, hi) = t.HeightRange;
        _out.WriteLine($"Azeroth 32_48: heights {lo:0.0}..{hi:0.0}, {t.AllChunks.Sum(c => c.Layers.Count)} layers (max {t.AllChunks.Max(c => c.Layers.Count)}), {t.AllChunks.Count(c => c.Liquids.Count > 0)} chunks with liquid, " +
                       $"{t.Doodads.Count} doodads, {t.Wmos.Count} buildings, {t.Textures.Count} textures, {t.FileSize / 1024} KB, notes {t.Notes.Count}");
        Assert.Empty(t.Notes);
        Assert.Equal((893, 43, 785, 8, 7), (t.AllChunks.Sum(c => c.Layers.Count), t.AllChunks.Count(c => c.Liquids.Count > 0), t.Doodads.Count, t.Wmos.Count, t.Textures.Count));
    }

    private sealed record TileStats(int Tiles, int Failed, int Chunks, int MoreThan4, int MaxLayers, int LiquidChunks, int Liquids, int HoleChunks, int Doodads, int Wmos, int Notes);

    private static TileStats LoadAll(MapCatalog160 cat, MapInfo map, Func<int, bool> pick, HashSet<string> wmos, List<string> failures)
    {
        int tiles = 0, failed = 0, chunks = 0, more4 = 0, maxL = 0, liqC = 0, liq = 0, holes = 0, dd = 0, ww = 0, notes = 0, i = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                if (!map.HasTile(x, y) || !pick(i++)) continue;
                try
                {
                    var t = cat.LoadTerrain(map, x, y);
                    if (t == null) { failed++; failures.Add($"{map.Directory} {x}_{y}: no tile"); continue; }
                    tiles++;
                    foreach (var c in t.AllChunks)
                    {
                        chunks++;
                        if (c.Layers.Count > 4) more4++;
                        maxL = Math.Max(maxL, c.Layers.Count);
                        if (c.Liquids.Count > 0) liqC++;
                        liq += c.Liquids.Count;
                        if (c.HasHoles) holes++;
                    }
                    dd += t.Doodads.Count;
                    ww += t.Wmos.Count;
                    notes += t.Notes.Count;
                    foreach (var w in t.Wmos) wmos.Add(w.Model);
                }
                catch (IOException e) { failed++; failures.Add($"{map.Directory} {x}_{y}: {e.Message}"); }
            }
        return new TileStats(tiles, failed, chunks, more4, maxL, liqC, liq, holes, dd, ww, notes);
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void EveryTileOfTheSampledMapsLoadsAsTerrain()
    {
        using var o = Open();
        var cat = o.Cat;
        var wmos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        var total = new List<TileStats>();
        // every tile of every map with tiles except the two continents and the 256-tile Emerald Dream; every 16th tile of those
        foreach (var map in cat.Maps.Where(m => m.TileCount > 0).OrderBy(m => m.Id))
        {
            bool big = map.TileCount > 250;
            var st = LoadAll(cat, map, i => !big || i % 16 == 0, wmos, failures);
            total.Add(st);
            _out.WriteLine($"{map.Id} {map.Directory}: {st}");
        }
        var sum = new TileStats(total.Sum(t => t.Tiles), total.Sum(t => t.Failed), total.Sum(t => t.Chunks), total.Sum(t => t.MoreThan4), total.Max(t => t.MaxLayers),
            total.Sum(t => t.LiquidChunks), total.Sum(t => t.Liquids), total.Sum(t => t.HoleChunks), total.Sum(t => t.Doodads), total.Sum(t => t.Wmos), total.Sum(t => t.Notes));
        _out.WriteLine($"TOTAL {sum} in {sw.Elapsed.TotalSeconds:0.0} s; {wmos.Count} distinct buildings placed");
        foreach (var f in failures.Take(20)) _out.WriteLine("  " + f);
        Assert.Empty(failures);
        Assert.Equal(0, sum.Notes);
        // measured: 1,466 tiles of 47 maps; 2,822 chunks with more than 4 layers (up to 8, map 2997), 63,067 with liquid
        Assert.Equal(new TileStats(1466, 0, 375296, 2822, 8, 63067, 63638, 14376, 273642, 3792, 0), sum);
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void TheSampledBuildingsLoadInTheFileDataIdForm()
    {
        using var o = Open();
        var cat = o.Cat;
        var wmos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var map in new[] { "Azeroth", "Kalimdor" }.Select(d => cat.ByDirectory(d)!))
            LoadAll(cat, map, i => i % 24 == 0, wmos, failures);
        foreach (var m in cat.Maps.Where(m => m.IsGlobalWmo && m.GlobalWmo != null)) wmos.Add(m.GlobalWmo!);
        int roots = 0, rootsFailed = 0, groups = 0, groupsExpected = 0, tris = 0, textures = 0, texturesRead = 0, fdidMode = 0, doodadModels = 0, badMaterials = 0;
        var sw = Stopwatch.StartNew();
        foreach (var w in wmos.OrderBy(x => x))
        {
            var g = WmoGeometry.Load(w, p => Read(o.St, p));
            if (g == null) { rootsFailed++; failures.Add($"{w}: root not readable"); continue; }
            roots++;
            if (g.FileDataIdMode) fdidMode++;
            groups += g.Groups.Count;
            groupsExpected += Math.Min(g.GroupCount, 512);
            tris += g.Groups.Sum(x => x.Indices.Length / 3);
            textures += g.MaterialTextures.Count(t => t.Length > 0);
            texturesRead += g.MaterialTextures.Count(t => t.Length > 0 && Read(o.St, t) is { Length: > 4 } b && b[0] == (byte)'B' && b[1] == (byte)'L' && b[2] == (byte)'P');
            doodadModels += g.DoodadModels.Count;
            badMaterials += g.Groups.SelectMany(x => x.Batches).Count(b => b.Material >= g.MaterialTextures.Count && b.Material != 0xFF);
        }
        _out.WriteLine($"buildings {wmos.Count}: roots {roots} (failed {rootsFailed}), FileDataID form {fdidMode}, groups {groups} of {groupsExpected}, {tris:N0} triangles, " +
                       $"textures {texturesRead} of {textures} read as BLP, {doodadModels} doodad models listed, {badMaterials} batches with a material out of range, {sw.Elapsed.TotalSeconds:0.0} s");
        foreach (var f in failures.Take(20)) _out.WriteLine("  " + f);
        Assert.Empty(failures);
        Assert.Equal(roots, fdidMode);
        Assert.Equal(0, badMaterials);
        Assert.Equal((131, 1298, 1298, 4119850, 2091, 2091), (roots, groups, groupsExpected, tris, textures, texturesRead));
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void TheMinimapOverviewOfAzerothBuilds()
    {
        using var o = Open();
        var az = o.Cat.ByDirectory("Azeroth")!;
        var one = o.Cat.MinimapTile(az, 32, 48)!;
        var sw = Stopwatch.StartNew();
        var ov = o.Cat.Overview(az, 32, null);
        int grey = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                if (!az.HasTile(x, y)) continue;
                int p = ov.Image.Offset((x - ov.MinX) * 32 + 16, (y - ov.MinY) * 32 + 16);
                if (ov.Image.Pixels[p] == 0x55 && ov.Image.Pixels[p + 1] == 0x5D && ov.Image.Pixels[p + 2] == 0x68) grey++;
            }
        _out.WriteLine($"minimap tile {one.Width}x{one.Height}; overview {ov.Image.Width}x{ov.Image.Height} from {ov.MinX},{ov.MinY}, {grey} tiles without a minimap, {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal((512, 512), (one.Width, one.Height));
        Assert.Equal(0, grey);
        Assert.Equal((800, 1408), (ov.Image.Width, ov.Image.Height));
        var (minX, minY, maxX, maxY) = az.Bounds;
        Assert.Equal(((maxX - minX + 3) * 32, (maxY - minY + 3) * 32), (ov.Image.Width, ov.Image.Height));
    }
}
