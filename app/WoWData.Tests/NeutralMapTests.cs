using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>The stock 1.12 client for the neutral-model tests: the test client of <see cref="TestEnv"/> (the convention of
/// the other map tests), else the user's 1.12 copy on G:, else the tests are skipped.</summary>
public static class Neutral112Data
{
    public const string UserCopy = @"G:\WoW Clients and Patches\Clients\1.12.1.5875 - WoW Client\Data";
    private static ClientData? _own;
    private static readonly object Gate = new();

    private static bool TestEnvHasClient
    {
        get
        {
            try { return File.Exists(Path.Combine(TestEnv.DataDir, "model.MPQ")); }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException) { return false; }
        }
    }

    public static bool Available => TestEnvHasClient || File.Exists(Path.Combine(UserCopy, "model.MPQ"));

    /// <summary>Read-only access to the client (never written).</summary>
    public static ClientData Client
    {
        get
        {
            if (TestEnvHasClient) return TestEnv.Client;
            lock (Gate) return _own ??= new ClientData(UserCopy);
        }
    }
}

/// <summary>A test that needs the stock 1.12 client: skipped when neither copy is there.</summary>
public sealed class Client112FactAttribute : FactAttribute
{
    public Client112FactAttribute()
    {
        if (!Neutral112Data.Available) Skip = $"needs the 1.12 client (TestEnv's Client\\Data or {Neutral112Data.UserCopy})";
    }
}

/// <summary>
/// Milestone 3 of MAP_CONVERSION_DESIGN.md on the stock data: every 1.12 ADT, WDT and WDL read into the neutral model
/// and written back by <see cref="Adt112Writer"/> is byte-identical with an empty conversion report; the neutral content
/// agrees with the existing <see cref="TerrainTile"/> decoding; an edit made in the model lands exactly where it should.
/// </summary>
public class NeutralMapRoundTripTests
{
    private readonly ITestOutputHelper _out;

    public NeutralMapRoundTripTests(ITestOutputHelper output) => _out = output;

    private static List<string> MapFiles(string ext) =>
        Neutral112Data.Client.Glob(@"World\Maps\*").Where(n => n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();

    [Client112Fact]
    public void EveryStockTileRoundTripsThroughTheNeutralModelByteIdentically()
    {
        var names = MapFiles(".adt");
        Assert.Equal(2429, names.Count);
        var sw = Stopwatch.StartNew();
        int identical = 0, chunks = 0, normalPads = 0, liquidLayers = 0, tileResidues = 0, waterFlowBytes = 0, flowVectors = 0, emitters = 0, shadows = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var gate = new object();
        Parallel.ForEach(names, name =>
        {
            var data = Neutral112Data.Client.Read(name)!;
            var (x, y) = Adt112Reader.TileCoords(name)!.Value;
            var tile = Adt112Reader.ReadAdt(data, name.Split('\\')[^2], x, y);
            var report = new ConversionReport();
            var back = Adt112Writer.WriteAdt(tile, report);
            if (!back.AsSpan().SequenceEqual(data)) failures.Add($"{name}: not byte-identical ({data.Length} -> {back.Length} bytes)");
            if (!report.IsEmpty) failures.Add($"{name}: report not empty: {report.ToText()}");
            var all = tile.AllChunks.ToList();
            lock (gate)
            {
                if (back.AsSpan().SequenceEqual(data)) identical++;
                chunks += all.Count;
                normalPads += all.Count(c => c.NormalPad112 != null);
                shadows += all.Count(c => c.Shadow != null);
                emitters += all.Sum(c => c.SoundEmitters.Count);
                foreach (var l in all.SelectMany(c => c.Liquids))
                {
                    liquidLayers++;
                    if (l.MclqTileResidue112 != null) tileResidues++;
                    if (l.MclqFlow112 != null) waterFlowBytes++;
                    if (l.MclqFlowVectors112 != null) flowVectors++;
                }
            }
        });
        _out.WriteLine($"{names.Count} tiles, {identical} byte-identical, {chunks} chunks in {sw.Elapsed.TotalSeconds:F1} s; passthrough: MCNR pad {normalPads}, " +
                       $"MCLQ layers {liquidLayers} (tile residue {tileResidues}, water flow bytes {waterFlowBytes}, flow vectors {flowVectors}), sound emitters {emitters}; MCSH {shadows}");
        Assert.Empty(failures.Take(10));
        Assert.Equal(2429, identical);
        Assert.Equal(2429 * 256, chunks);
        Assert.Equal(237523, liquidLayers);
        // every MCLQ tile byte of the stock data follows the rule (exists ? kind nibble : 0xF) | 0x40 fishable | 0x80 deep,
        // and the flow bytes of water vertices are zero: those layers are modelled, not carried as bytes
        Assert.Equal(0, tileResidues);
        Assert.Equal(0, waterFlowBytes);
        Assert.Equal(2429 * 256, shadows);
    }

    [Client112Fact]
    public void EveryStockWdtAndWdlRoundTripsThroughTheNeutralModel()
    {
        int wdts = 0, wdls = 0, wmoOnly = 0, tiles = 0, farTiles = 0;
        foreach (var name in MapFiles(".wdt"))
        {
            var data = Neutral112Data.Client.Read(name)!;
            var map = Adt112Reader.ReadWdt(data, name.Split('\\')[^2]);
            var report = new ConversionReport();
            Assert.True(Adt112Writer.WriteWdt(map, report).AsSpan().SequenceEqual(data), name);
            Assert.True(report.IsEmpty, report.ToText());
            Assert.Equal(new WdtFile(data).Tiles().Count, map.ExistingTiles().Count());
            if (map.IsWmoOnly)
            {
                wmoOnly++;
                Assert.True(map.GlobalWmo!.Model.HasPath);
            }
            tiles += map.ExistingTiles().Count();
            wdts++;
        }
        foreach (var name in MapFiles(".wdl"))
        {
            var data = Neutral112Data.Client.Read(name)!;
            var wdl = Adt112Reader.ReadWdl(data);
            var report = new ConversionReport();
            Assert.True(Adt112Writer.WriteWdl(wdl, report).AsSpan().SequenceEqual(data), name);
            Assert.True(report.IsEmpty, report.ToText());
            farTiles += wdl.Heights.Count;
            wdls++;
        }
        _out.WriteLine($"{wdts} WDTs ({wmoOnly} WMO-only, {tiles} tiles), {wdls} WDLs ({farTiles} far tiles)");
        Assert.Equal((43, 43, 20), (wdts, wdls, wmoOnly));
        // minimap references from md5translate.trs
        var az = Adt112Reader.ReadWdt(Neutral112Data.Client.Read(@"World\Maps\Azeroth\Azeroth.wdt")!, "Azeroth");
        int n = Adt112Reader.ReadMinimaps(az, "Azeroth", Neutral112Data.Client.Read(@"textures\Minimap\md5translate.trs"));
        Assert.True(n >= az.ExistingTiles().Count() - 5, $"{n} minimaps for {az.ExistingTiles().Count()} tiles");
        Assert.StartsWith(@"textures\Minimap\", az.Tile(32, 48).Minimap!.Path);
    }

    /// <summary>The neutral content of a sample of tiles against the existing decoder (TerrainTile): heights, normals,
    /// holes, layers and alpha, shadows, liquids, placements.</summary>
    [Client112Fact]
    public void NeutralContentAgreesWithTheTerrainTileDecoder()
    {
        var names = MapFiles(".adt").Where((_, i) => i % 40 == 0).ToList();
        foreach (var name in names)
        {
            var data = Neutral112Data.Client.Read(name)!;
            var (x, y) = Adt112Reader.TileCoords(name)!.Value;
            var n = Adt112Reader.ReadAdt(data, "m", x, y);
            var t = TerrainTile.Load(data, "m", x, y);
            Assert.Equal(t.Textures, n.Textures.Select(a => a.Path!));
            Assert.Equal(t.Doodads.Count, n.Doodads.Count);
            for (int i = 0; i < t.Doodads.Count; i++)
            {
                Assert.Equal(t.Doodads[i].Model, ClientData.ResolveName(n.Doodads[i].Model.Path!));
                Assert.Equal(t.Doodads[i].Position, n.Doodads[i].Position);
                Assert.Equal(t.Doodads[i].Scale, n.Doodads[i].Scale);
            }
            Assert.Equal(t.Wmos.Select(w => (w.Uid, w.DoodadSet, w.ExtentMax)), n.Wmos.Select(w => (w.UniqueId, (int)w.DoodadSet, w.ExtentMax)));
            for (int c = 0; c < 256; c++)
            {
                var tc = t.Chunks[c]!;
                var nc = n.Chunks[c]!;
                for (int k = 0; k < 145; k++)
                {
                    Assert.Equal(tc.Heights[k], nc.Position.Z + nc.Heights[k]);
                    var v = new Vector3(nc.Normals[k * 3], nc.Normals[k * 3 + 1], nc.Normals[k * 3 + 2]);
                    Assert.Equal(tc.Normals[k], v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitZ);
                }
                Assert.Equal(tc.Holes, NeutralChunk.ReduceHighResHoles(nc.Holes));
                Assert.Equal(tc.Layers, nc.Layers.Select(l => l.Texture.Path!));
                for (int L = 1; L < tc.Layers.Count; L++)
                    Assert.Equal(tc.Alpha[L]!, nc.Layers[L].Alpha!.Select(v => (byte)((v >> 4) * 17)).ToArray());
                Assert.Equal(tc.Shadow != null, (nc.Flags & 1) != 0);
                if (tc.Shadow != null) Assert.Equal(tc.Shadow, nc.Shadow!);
                Assert.Equal(tc.Liquids.Count, nc.Liquids.Count);
                for (int q = 0; q < tc.Liquids.Count; q++)
                {
                    Assert.Equal(tc.Liquids[q].Kind, nc.Liquids[q].Kind);
                    Assert.Equal(tc.Liquids[q].Heights, nc.Liquids[q].Heights!);
                    Assert.Equal(tc.Liquids[q].Tiles, Enumerable.Range(0, 64).Select(k => (nc.Liquids[q].Exists & (1UL << k)) != 0).ToArray());
                }
                Assert.Equal((tc.DoodadRefs, tc.WmoRefs), (nc.DoodadRefs.Count, nc.WmoRefs.Count));
            }
        }
    }

    /// <summary>One chunk's heights changed in the neutral model: the written tile has the new heights (read with the existing
    /// readers), every other chunk and every other top-level chunk is byte-identical, and the tile validates.</summary>
    [Client112Fact]
    public void AHeightEditInTheModelChangesOnlyThatChunk()
    {
        const string name = @"World\Maps\Azeroth\Azeroth_32_48.adt";
        const int edited = 7 * 16 + 9;
        var data = Neutral112Data.Client.Read(name)!;
        var tile = Adt112Reader.ReadAdt(data, "Azeroth", 32, 48);
        var before = (float[])tile.Chunks[edited]!.Heights.Clone();
        for (int k = 0; k < 145; k++) tile.Chunks[edited]!.Heights[k] += 5f + k * 0.01f;
        var report = new ConversionReport();
        var outBytes = Adt112Writer.WriteAdt(tile, report);
        Assert.True(report.IsEmpty, report.ToText());
        Assert.Equal(data.Length, outBytes.Length);

        var orig = new AdtFile(data);
        var back = new AdtFile(outBytes);
        var tt = TerrainTile.Load(outBytes, "Azeroth", 32, 48);
        var tOrig = TerrainTile.Load(data, "Azeroth", 32, 48);
        float posZ = tile.Chunks[edited]!.Position.Z;
        for (int k = 0; k < 145; k++)
        {
            float relative = before[k] + (5f + k * 0.01f);
            Assert.Equal(relative, tile.Chunks[edited]!.Heights[k]);
            Assert.Equal(posZ + relative, tt.Chunks[edited]!.Heights[k]);   // TerrainTile: absolute = posZ + MCVT
        }
        for (int c = 0; c < 256; c++)
        {
            var a = data.AsSpan((int)orig.Mcin[c].Offset, (int)orig.Mcin[c].Size);
            var b = outBytes.AsSpan((int)back.Mcin[c].Offset, (int)back.Mcin[c].Size);
            if (c == edited)
            {
                Assert.False(a.SequenceEqual(b));
                Assert.Equal(tOrig.Chunks[c]!.Normals, tt.Chunks[c]!.Normals);
            }
            else Assert.True(a.SequenceEqual(b), $"chunk {c} changed");
        }
        // everything before the first MCNK (header, names, placements) is unchanged
        Assert.True(data.AsSpan(0, (int)orig.Mcin[0].Offset).SequenceEqual(outBytes.AsSpan(0, (int)back.Mcin[0].Offset)));
        // the differing bytes are exactly the 145 MCVT floats of that chunk
        int diff = 0;
        for (int i = 0; i < data.Length; i++) if (data[i] != outBytes[i]) diff++;
        Assert.InRange(diff, 1, 145 * 4);
        var v = MapValidator.ValidateAdt(outBytes, name);
        Assert.True(v.Ok && v.Count(MapSeverity.Warning) == 0, string.Join("\n", v.Findings));
        // and the neutral model reads it back with the edit
        var again = Adt112Reader.ReadAdt(outBytes, "Azeroth", 32, 48);
        Assert.Equal(tile.Chunks[edited]!.Heights, again.Chunks[edited]!.Heights);
    }
}

/// <summary>
/// The 1.12 writer's lossy rules on content from richer sources (built in code, no client data needed): more than 4
/// layers, 8-bit alpha, fine holes, MH2O liquids, vertex colours, assets without a path, missing shadows. Each rule's
/// report entries are checked and the output is read back with the existing 1.12 readers and validated.
/// </summary>
public class NeutralMapLossTests
{
    private static NeutralTile Flat() => NeutralTile.CreateFlat("Test", 30, 40, 10f);

    private static void AssertValid(byte[] adt)
    {
        var v = MapValidator.ValidateAdt(adt, @"World\Maps\Test\Test_30_40.adt");
        Assert.True(v.Ok && v.Count(MapSeverity.Warning) == 0, string.Join("\n", v.Findings));
    }

    private static byte[] Band(Func<int, int, bool> inside, byte value = 255)
    {
        var a = new byte[4096];
        for (int r = 0; r < 64; r++)
            for (int c = 0; c < 64; c++)
                if (inside(r, c)) a[r * 64 + c] = value;
        return a;
    }

    [Fact]
    public void AFlatTileFromCodeIsAValid112TileAndRoundTrips()
    {
        var tile = Flat();
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        Assert.True(report.IsEmpty, report.ToText());
        AssertValid(bytes);
        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        Assert.All(t.AllChunks, c => Assert.All(c.Heights, h => Assert.Equal(10f, h)));
        Assert.Equal(t.Corner.X, t.Chunks[0]!.Position.X);
        // read back and written again: identical
        var again = Adt112Reader.ReadAdt(bytes, "Test", 30, 40);
        Assert.True(Adt112Writer.WriteAdt(again).AsSpan().SequenceEqual(bytes));
    }

    [Fact]
    public void EightLayersAreReducedToTheFourMostVisibleAndRenormalised()
    {
        var tile = Flat();
        var ch = tile.Chunks[17]!;
        // base + 7 layers; layer L (1..7) paints rows [8L, 8L + rows_L) fully; layers 1, 2, 3 get 8 rows, 4..7 fewer.
        // Layer 5 instead paints rows 8..9 at half alpha, over layer 1.
        int[] rows = { 0, 8, 8, 8, 6, 2, 1, 3 };
        for (int L = 0; L < 8; L++)
        {
            var tex = new AssetRef($@"Tileset\Test\layer{L}.blp");
            tile.Textures.Add(tex);
            int lo = L == 5 ? 8 : L * 8, hi = lo + rows[L];
            ch.Layers.Add(new NeutralLayer
            {
                Texture = tex, Flags = L == 0 ? 0u : 0x100u, EffectId = (uint)L,
                Alpha = L == 0 ? null : Band((r, _) => r >= lo && r < hi, L == 5 ? (byte)128 : (byte)255),
            });
        }
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        var loss = Assert.Single(report.Of(LossKind.TextureLayersDropped));
        Assert.Equal((17, 4), (loss.Chunk, loss.Count));
        Assert.Contains("kept 0, 1, 2, 3", loss.Message);
        Assert.Empty(report.Of(LossKind.AlphaReduced));   // renormalised weights here are 0 or 1: exactly 4-bit values
        AssertValid(bytes);
        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        Assert.Equal(new[] { 0, 1, 2, 3 }.Select(L => $@"Tileset\Test\layer{L}.blp"), t.Chunks[17]!.Layers);
        var back = Adt112Reader.ReadAdt(bytes, "Test", 30, 40).Chunks[17]!;
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, back.Layers.Select(l => l.EffectId));
        // renormalised: in rows 8..9 layer 1 was half hidden by the dropped layer 5; with layer 5 gone it is the only
        // visible layer there again (alpha 255, not 128 or 0). Rows 32..37 showed only the dropped layer 4: base now.
        for (int c = 0; c < 64; c++)
        {
            Assert.Equal(255, back.Layers[1].Alpha![8 * 64 + c]);
            Assert.Equal(255, back.Layers[1].Alpha![12 * 64 + c]);
            Assert.Equal(255, back.Layers[3].Alpha![24 * 64 + c]);
            Assert.All(new[] { 1, 2, 3 }, L => Assert.Equal(0, back.Layers[L].Alpha![33 * 64 + c]));
        }
    }

    [Fact]
    public void EightBitAlphaIsReducedWithAShiftByFour()
    {
        var tile = Flat();
        var tex0 = new AssetRef(@"Tileset\Test\a.blp");
        var tex1 = new AssetRef(@"Tileset\Test\b.blp");
        tile.Textures.AddRange(new[] { tex0, tex1 });
        var ramp = new byte[4096];
        for (int i = 0; i < 4096; i++) ramp[i] = (byte)(i % 256);
        var exact = new byte[4096];
        for (int i = 0; i < 4096; i++) exact[i] = NeutralLayer.Expand4(i % 16);
        tile.Chunks[0]!.Layers.Add(new NeutralLayer { Texture = tex0 });
        tile.Chunks[0]!.Layers.Add(new NeutralLayer { Texture = tex1, Flags = 0x100 | 0x200, Alpha = ramp });   // 0x200: compressed in the source
        tile.Chunks[1]!.Layers.Add(new NeutralLayer { Texture = tex0 });
        tile.Chunks[1]!.Layers.Add(new NeutralLayer { Texture = tex1, Flags = 0x100, Alpha = exact });
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        var loss = Assert.Single(report.Losses);   // chunk 1 holds only 0 / 16n + 15: nothing lost there
        Assert.Equal((LossKind.AlphaReduced, 1), (loss.Kind, loss.Count));
        AssertValid(bytes);
        var adt = new AdtFile(bytes);
        var c0 = adt.Mcnk[0]!;
        long mcal = c0.DataOffset("ofsAlpha");
        for (int k = 0; k < 2048; k++)
            Assert.Equal((byte)((ramp[2 * k] >> 4) | ((ramp[2 * k + 1] >> 4) << 4)), bytes[mcal + k]);
        Assert.Equal(0x100u, MapBytes.U32(bytes, c0.DataOffset("ofsLayer") + 16 + 4));   // 0x200 cleared (storage only)
        // the survey's rule: 16n + 15 back to n exactly
        var back = Adt112Reader.ReadAdt(bytes, "Test", 30, 40);
        Assert.Equal(exact, back.Chunks[1]!.Layers[1].Alpha!);
        Assert.Equal(ramp.Select(v => NeutralLayer.Expand4(v >> 4)), back.Chunks[0]!.Layers[1].Alpha!);
    }

    [Fact]
    public void FineHolesBecome112HolesOnlyWhereAllFourAreHoles()
    {
        var tile = Flat();
        // a whole 2x2 block at fine (row 2..3, col 4..5) = 1.12 hole (row 1, col 2) + one lone fine hole at (7, 0)
        ulong block = (1UL << (2 * 8 + 4)) | (1UL << (2 * 8 + 5)) | (1UL << (3 * 8 + 4)) | (1UL << (3 * 8 + 5));
        tile.Chunks[5]!.Holes = block | (1UL << (7 * 8 + 0));
        tile.Chunks[5]!.Flags = 0x10000;
        tile.Chunks[6]!.Holes = NeutralChunk.ExpandLowResHoles(0x8001);
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        var loss = Assert.Single(report.Losses);
        Assert.Equal((LossKind.HolesReduced, 5, 1), (loss.Kind, loss.Chunk, loss.Count));
        AssertValid(bytes);
        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        Assert.Equal(1u << (1 * 4 + 2), t.Chunks[5]!.Holes);
        Assert.Equal(0x8001u, t.Chunks[6]!.Holes);
        Assert.Equal(0u, t.Chunks[5]!.Flags);   // 0x10000 (high-res holes) is storage only
        Assert.Equal(block, Adt112Reader.ReadAdt(bytes, "Test", 30, 40).Chunks[5]!.Holes);
    }

    private static NeutralLiquid Mh2o(int type, ulong exists, float level, int rx = 0, int ry = 0, int rw = 8, int rh = 8)
    {
        var l = new NeutralLiquid
        {
            Origin = LiquidOrigin.Mh2o, LiquidType = type, LiquidObjectOrVertexFormat = 42, Min = level, Max = level + 1,
            RectX = rx, RectY = ry, RectW = rw, RectH = rh, Exists = exists, Fishable = exists, Depth = new byte[(rw + 1) * (rh + 1)],
            Heights = new float[(rw + 1) * (rh + 1)],
        };
        for (int i = 0; i < l.Heights.Length; i++)
        {
            l.Heights[i] = level + i * 0.01f;
            l.Depth[i] = (byte)i;
        }
        return l;
    }

    private static ulong Tiles(int count) => count >= 64 ? ulong.MaxValue : (1UL << count) - 1;

    [Fact]
    public void Mh2oInstancesBecomeOneMclqLayerPerChunk()
    {
        var tile = Flat();
        // chunk 3: ocean (type 1250) on 40 tiles, magma (7) on 10, an unknown type on 5 -> ocean kept, 2 dropped
        tile.Chunks[3]!.Liquids.Add(Mh2o(7, Tiles(10), 20f));
        tile.Chunks[3]!.Liquids.Add(Mh2o(1250, Tiles(40) << 8, 5f));
        tile.Chunks[3]!.Liquids.Add(Mh2o(999, Tiles(5), 7f));
        // chunk 4: one magma instance with a partial rectangle (tiles x 2..5, y 1..3)
        ulong rect = 0;
        for (int yy = 1; yy < 4; yy++) for (int xx = 2; xx < 6; xx++) rect |= 1UL << (yy * 8 + xx);
        tile.Chunks[4]!.Liquids.Add(Mh2o(7, rect, 30f, 2, 1, 4, 3));
        // chunk 9: a type the mapping does not know
        tile.Chunks[9]!.Liquids.Add(Mh2o(999, Tiles(64), 1f));
        LiquidKind? Map(int type) => type switch { 1250 => LiquidKind.Ocean, 7 => LiquidKind.Magma, _ => null };
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report, new Adt112WriterOptions { LiquidKind = Map });
        var dropped = Assert.Single(report.Of(LossKind.LiquidInstancesDropped));
        Assert.Equal((3, 2), (dropped.Chunk, dropped.Count));
        Assert.Contains("covering 40 tiles", dropped.Message);
        Assert.Equal(2, Assert.Single(report.Of(LossKind.LiquidTypeMapped)).Count);   // chunk 3's ocean, chunk 4's magma
        Assert.Equal(9, Assert.Single(report.Of(LossKind.LiquidTypeUnmapped)).Chunk);
        Assert.Equal(3, report.Losses.Count);
        AssertValid(bytes);

        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        var l3 = Assert.Single(t.Chunks[3]!.Liquids);
        Assert.Equal(LiquidKind.Ocean, l3.Kind);
        Assert.Equal(0x8u, t.Chunks[3]!.Flags & 0x3C);
        Assert.Equal(Enumerable.Range(0, 64).Select(k => k >= 8 && k < 48), l3.Tiles);
        Assert.Equal(5f, l3.Min);
        Assert.Equal(5f + 80 * 0.01f, l3.Heights[80]);
        var l4 = Assert.Single(t.Chunks[4]!.Liquids);
        Assert.Equal(LiquidKind.Magma, l4.Kind);
        Assert.Equal(12, l4.Tiles.Count(b => b));
        // vertex (x 2, y 1) is the rectangle's first vertex; (x 6, y 4) its last; outside it the nearest vertex
        Assert.Equal(30f, l4.Heights[1 * 9 + 2]);
        Assert.Equal(30f + 19 * 0.01f, l4.Heights[4 * 9 + 6]);
        Assert.Equal(30f, l4.Heights[0]);
        Assert.Equal(LiquidKind.River, Assert.Single(t.Chunks[9]!.Liquids).Kind);
        // tile bytes: ocean nibble 1 + fishable 0x40 where liquid, 0x0F elsewhere
        var back = Adt112Reader.ReadAdt(bytes, "Test", 30, 40).Chunks[3]!.Liquids.Single();
        Assert.Equal((Tiles(40) << 8, Tiles(40) << 8, 0UL), (back.Exists, back.Fishable, back.Deep));
        Assert.Null(back.MclqTileResidue112);
        Assert.Equal((byte)8, back.Depth![8]);
    }

    [Fact]
    public void Mclq112LayersOfTwoKindsStayTwoLayers()
    {
        var tile = Flat();
        var river = new NeutralLiquid { Origin = LiquidOrigin.Mclq, Kind = LiquidKind.River, Exists = Tiles(64), Heights = new float[81], Depth = new byte[81] };
        var ocean = new NeutralLiquid { Origin = LiquidOrigin.Mclq, Kind = LiquidKind.Ocean, Exists = Tiles(8), Heights = new float[81], Depth = new byte[81] };
        tile.Chunks[0]!.Liquids.AddRange(new[] { ocean, river });
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        Assert.True(report.IsEmpty, report.ToText());
        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        Assert.Equal(new[] { LiquidKind.River, LiquidKind.Ocean }, t.Chunks[0]!.Liquids.Select(l => l.Kind));
        Assert.Equal(0xCu, t.Chunks[0]!.Flags);
    }

    [Fact]
    public void VertexColoursAndLightingAreDropped()
    {
        var tile = Flat();
        foreach (var i in new[] { 0, 100, 255 })
        {
            tile.Chunks[i]!.VertexColors = Enumerable.Repeat((byte)0x7F, 145 * 4).ToArray();
            tile.Chunks[i]!.Flags = 0x40;
        }
        tile.Chunks[1]!.VertexLighting = new byte[145 * 4];
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        Assert.Equal(3, Assert.Single(report.Of(LossKind.VertexColorsDropped)).Count);
        Assert.Equal(1, Assert.Single(report.Of(LossKind.VertexLightingDropped)).Count);
        Assert.Equal(2, report.Losses.Count);
        AssertValid(bytes);
        var adt = new AdtFile(bytes);
        Assert.All(adt.Mcnk, c => Assert.Equal((0u, 0u), (c!.Flags, c["ofsMCCV"])));
        Assert.DoesNotContain(adt.Mcnk, c => MapBytes.MagicAt(bytes, c!.Offset + 8 + MapBytes.McnkHeaderSize) == "MCCV");
    }

    [Fact]
    public void AssetsWithoutAPathGetMadeUpPaths()
    {
        var tile = Flat();
        var tex = new AssetRef(null, 123456);
        tile.Textures.Add(tex);
        tile.Chunks[0]!.Layers.Add(new NeutralLayer { Texture = tex });
        tile.Chunks[1]!.Layers.Add(new NeutralLayer { Texture = new AssetRef(null, 123456) });   // same FDID, other object
        var model = new AssetRef(null, 777);
        tile.DoodadModels.Add(model);
        tile.Doodads.Add(new NeutralDoodad { Model = model, UniqueId = 1, Flags = 0x40, Position = new Vector3(100, 10, 200) });
        tile.Doodads.Add(new NeutralDoodad { Model = new AssetRef(@"World\Generic\Tree.m2", 555), UniqueId = 2, Flags = 0x40 });   // not in the list yet
        tile.Wmos.Add(new NeutralWmo { Model = new AssetRef(null, 888), UniqueId = 3, Flags = 0x8 });
        tile.Source = MapSourceVersion.V160;
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        var made = report.Of(LossKind.AssetPathMadeUp).ToList();
        Assert.Equal(3, made.Count);
        Assert.Contains(made, l => l.Count == 2 && l.Message.Contains(@"World\Converted\123456.blp"));
        Assert.Contains(made, l => l.Count == 1 && l.Message.Contains(@"World\Converted\777.mdx"));
        Assert.Contains(made, l => l.Count == 1 && l.Message.Contains(@"World\Converted\888.wmo"));
        Assert.Equal(3, report.Losses.Count);   // the FDID placement flags (0x40 / 0x8) are storage only: no entry
        AssertValid(bytes);
        var adt = new AdtFile(bytes);
        Assert.Equal(new[] { @"World\Converted\123456.blp" }, adt.Textures);
        Assert.Equal(new[] { @"World\Converted\777.mdx", @"World\Generic\Tree.mdx" }, adt.Mmdx.Select(kv => kv.Value));
        Assert.Equal(@"World\Converted\888.wmo", Assert.Single(adt.Mwmo).Value);
        Assert.Equal(new[] { @"World\Converted\777.mdx", @"World\Generic\Tree.mdx" }, adt.Mddf.Select(d => adt.DoodadName(d)));
        Assert.All(adt.Mddf, d => Assert.Equal(0, d.Flags));
        Assert.Equal(0, adt.Modf[0].Flags);
    }

    [Fact]
    public void MissingShadowsWriteNoMcshAndClearTheFlag()
    {
        var tile = Flat();
        tile.Chunks[0]!.Flags = 0x1 | 0x8000;   // asks for a shadow the model does not have
        tile.Chunks[1]!.Shadow = Enumerable.Range(0, 4096).Select(i => i % 3 == 0).ToArray();
        tile.Chunks[1]!.Flags = 0x1;
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteAdt(tile, report);
        var loss = Assert.Single(report.Losses);
        Assert.Equal((LossKind.ShadowMissing, 0), (loss.Kind, loss.Chunk));
        AssertValid(bytes);
        var adt = new AdtFile(bytes);
        Assert.Equal((0x8000u, 0u), (adt.Mcnk[0]!.Flags, adt.Mcnk[0]!["sizeShadow"]));
        Assert.NotEqual("MCSH", MapBytes.MagicAt(bytes, adt.Mcnk[0]!.Offset + adt.Mcnk[0]!["ofsShadow"]));
        var t = TerrainTile.Load(bytes, "Test", 30, 40);
        Assert.Null(t.Chunks[0]!.Shadow);
        Assert.Equal(tile.Chunks[1]!.Shadow, t.Chunks[1]!.Shadow);
    }

    [Fact]
    public void AModernMapWritesA112WdtAndReportsWhatItDrops()
    {
        var map = new NeutralMap { Name = "Modern", Source = MapSourceVersion.V160, Flags = 0x3CA };
        map.MphdExtra[0] = 1234;
        map.Tile(1, 2).HasAdt = true;
        map.Tile(1, 2).Flags = 0x3;
        map.Tile(1, 2).Maid = new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var report = new ConversionReport();
        var bytes = Adt112Writer.WriteWdt(map, report);
        Assert.Equal(1, Assert.Single(report.Of(LossKind.MapFlagsCleared)).Count);
        Assert.Equal(2, Assert.Single(report.Of(LossKind.MapFileRefsDropped)).Count);
        var w = new WdtFile(bytes);
        Assert.Equal(0u, w.MphdFlags);
        Assert.Equal(new[] { (1, 2) }, w.Tiles());
        Assert.Equal(1u, w.Main[2 * 64 + 1].Flags);
        Assert.True(MapValidator.ValidateWdt(bytes).Ok);
        var wdl = new NeutralWdl();
        wdl.Heights[2 * 64 + 1] = Enumerable.Range(0, 545).Select(i => (short)i).ToArray();
        wdl.Holes[2 * 64 + 1] = new ushort[16];
        var wr = new ConversionReport();
        var wdlBytes = Adt112Writer.WriteWdl(wdl, wr);
        Assert.Equal(LossKind.FarHolesDropped, Assert.Single(wr.Losses).Kind);
        Assert.Equal(wdl.Heights[2 * 64 + 1], new WdlFile(wdlBytes).Heights(1, 2));
    }
}
