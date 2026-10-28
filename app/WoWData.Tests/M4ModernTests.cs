using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using WoWDataStudio.ViewModels;
using static WoWData.Tests.SyntheticClients;
using static WoWData.Tests.SyntheticMaps;

namespace WoWData.Tests;

/// <summary>
/// M4 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): the maps of another version opened read only, on the synthetic
/// "1.60" client (our own bytes, <see cref="SyntheticMaps"/>): the map list with its locked rows and unreadable WDT, the
/// zones from AreaTable and the chunk area ids, the minimap overview, a tile as terrain (heights equal to
/// <see cref="Adt160Reader"/>'s, 6 layers with their blend weights kept, 8 x 8 holes, MH2O liquids, placements by
/// FileDataID), a locked obj0, the WMO FileDataID form, the 3D asset with 8-layer atlases, doodads and the building, and
/// "On this tile".
/// </summary>
public class M4ModernTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("m4");
    private readonly AppSession _s;

    public M4ModernTests()
    {
        var (flavor, lf) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        var copy = ClientCopies.Inspect(flavor)!;
        var paths = new AppPaths(Path.Combine(_root, "home"));
        _s = new AppSession(paths, AppSettings.Load(paths));
        _s.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = lf }, null).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MapCatalog160 Cat => _s.OtherMaps!;
    private MapInfo TestMap => Cat.ByDirectory(MapDir)!;

    [Fact]
    public void TheMapsAreListedWithTheirTilesLockedRowsAndUnreadableWdts()
    {
        Assert.Null(_s.MapCatalog);
        Assert.Same(_s.OtherMaps, _s.Maps);
        var listed = MapOperations.ListedMaps(Cat);
        Assert.Equal(new[] { "Test Continent", "Test Dungeon", "Test Missing" }, listed.Select(m => m.Name).OrderBy(n => n));
        var map = TestMap;
        Assert.Equal(2, map.TileCount);
        Assert.True(map.HasTile(TileAX, TileAY) && map.HasTile(TileBX, TileBY));
        Assert.False(map.IsGlobalWmo);
        var dungeon = Cat.ByDirectory("TestDungeon")!;
        Assert.True(dungeon.IsGlobalWmo);
        Assert.Equal($"#{WmoRoot}.wmo", dungeon.GlobalWmo);
        var missing = Cat.ByDirectory("TestMissing")!;
        Assert.Equal(0, missing.TileCount);
        Assert.True(missing.Note?.Contains("not on this computer") == true, missing.Note);
        Assert.Contains("not on this computer", missing.Detail);
        Assert.Equal((3, 1, 3, 1, 1), (Cat.MapRows, Cat.LockedMapRows, Cat.AreaRows, Cat.LockedAreaRows, Cat.UnreadableWdts));
        Assert.Equal("Map table: 3 readable rows, 1 locked (encrypted, no key); AreaTable: 3 readable, 1 locked; 1 map without a readable WDT.", Cat.StatusText);
        Assert.Same(map, MapOperations.Find(Cat, "Test Continent"));
        Assert.Same(map, MapOperations.Find(Cat, "0"));
    }

    [Fact]
    public void ZonesComeFromAreaTableAndTheChunkAreaIds()
    {
        var areas = Cat.TileAreas(TestMap);
        Assert.Equal(2, areas.Count);
        Assert.Equal(Subzone, areas[(TileAX, TileAY)][0]);
        Assert.Equal(Zone, areas[(TileAX, TileAY)][200]);
        Assert.All(areas[(TileBX, TileBY)], a => Assert.Equal(OtherZone, a));
        Assert.Equal(Zone, Cat.ZoneOf(Subzone));
        Assert.Equal("Test Subzone", Cat.AreaName(Subzone));
        Assert.Equal("(no zone)", Cat.AreaName(0));
        var zones = Cat.Zones(TestMap);
        Assert.Equal(new[] { "Other Zone", "Test Zone" }, zones.Select(z => z.Name));
        var tz = zones.Single(z => z.Id == Zone);
        Assert.Equal((256, 1), (tz.Chunks, tz.Tiles.Count));
        var tile = Cat.TileZones(areas[(TileAX, TileAY)]);
        Assert.Equal(new[] { ("Test Subzone (Test Zone)", 128), ("Test Zone", 128) }, tile.Select(t => (t.Name, t.Chunks)).OrderBy(t => t.Name));
        // the area ids are cached in the client's app folder and read back
        Assert.True(File.Exists(Path.Combine(_s.MinimapCacheDir!, $"{MapDir}_areas.bin")));
    }

    [Fact]
    public void TheMinimapOverviewScalesTheMinimapsOfTheMaid()
    {
        var a = Cat.MinimapTile(TestMap, TileAX, TileAY)!;
        Assert.Equal((64, 64), (a.Width, a.Height));
        var ov = Cat.Overview(TestMap, 32, _s.MinimapCacheDir);
        Assert.False(ov.FromCache);
        // bounds 32..33 x 32 with one tile of margin: 4 x 3 cells of 32 pixels
        Assert.Equal((31, 31, 128, 96), (ov.MinX, ov.MinY, ov.Image.Width, ov.Image.Height));
        void At(int tx, int ty, (byte R, byte G, byte B) c)
        {
            int o = ov.Image.Offset((tx - ov.MinX) * 32 + 16, (ty - ov.MinY) * 32 + 16);
            Assert.InRange(ov.Image.Pixels[o], c.R - 12, c.R + 12);
            Assert.InRange(ov.Image.Pixels[o + 1], c.G - 12, c.G + 12);
            Assert.InRange(ov.Image.Pixels[o + 2], c.B - 12, c.B + 12);
        }
        At(TileAX, TileAY, ColourA);
        At(TileBX, TileBY, ColourB);
        Assert.True(Cat.Overview(TestMap, 32, _s.MinimapCacheDir).FromCache);
        Assert.Equal((TileAX, TileAY), ov.TileAt(48, 48));
    }

    [Fact]
    public void ATileAsTerrainKeepsHeightsLayersHolesLiquidsAndPlacements()
    {
        var notes = new List<string>();
        var neutral = Cat.ReadNeutral(TestMap, TileAX, TileAY, notes, out int bytes)!;
        Assert.Empty(notes);
        var t = Cat.LoadTerrain(TestMap, TileAX, TileAY)!;
        Assert.Empty(t.Notes);
        Assert.Equal(bytes, t.FileSize);
        Assert.Equal(256, t.AllChunks.Count());
        // heights: absolute, equal to the reader's (position + relative)
        foreach (var c in t.AllChunks)
            for (int k = 0; k < 145; k++) Assert.Equal(neutral.Chunks[c.Index]!.Position.Z + neutral.Chunks[c.Index]!.Heights[k], c.Heights[k]);
        Assert.Equal(20f + 255 * 0.25f + 16 * 0.1f, t.HeightRange.Max, 3);
        Assert.Equal(Vector3.UnitZ, t.Chunks[5]!.Normals[0]);
        // chunk 0: 6 layers; the 8-bit weights became sequential alphas with the same visible share
        var c0 = t.Chunks[0]!;
        Assert.Equal(6, c0.Layers.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(k => $"#{Ground0 + k}.blp"), c0.Layers);
        Assert.Null(c0.Alpha[0]);
        var weights = AlphaBlending.VisibleWeights(neutral.Chunks[0]!.Layers, AlphaBlendMode.Weights);
        var shown = AlphaBlending.VisibleWeights(c0.Alpha.Select((a, i) => new NeutralLayer { Alpha = a }).ToList(), AlphaBlendMode.Sequential);
        for (int L = 0; L < 6; L++)
            for (int k = 0; k < 4096; k += 37) Assert.InRange(shown[L][k] - weights[L][k], -0.02f, 0.02f);
        Assert.Equal(200, c0.Alpha[5]![50 * 64 + 3]);     // layer 5 alone paints rows 50-59 at 200/255
        // holes: the 8 x 8 mask
        var c1 = t.Chunks[1]!;
        Assert.Equal(0x0000_0000_0018_1800UL, c1.HighResHoles);
        Assert.True(c1.IsHole(3, 1) && c1.IsHole(4, 2) && !c1.IsHole(2, 1) && !c1.IsHole(3, 0));
        Assert.True(c1.HasHoles);
        Assert.False(t.Chunks[0]!.HasHoles);
        // liquids: water on the left half with sloped heights, magma everywhere
        var w = t.Chunks[2]!.Liquids.Single();
        Assert.Equal(LiquidKind.River, w.Kind);
        Assert.Equal(32, w.Tiles.Count(x => x));
        Assert.True(w.Tiles[0] && w.Tiles[3] && !w.Tiles[4]);
        Assert.Equal(22f, w.Heights[2 * 9 + 2]);
        Assert.Equal(21f, w.Heights[2 * 9 + 6]);       // outside the rectangle: its minimum
        var m = t.Chunks[3]!.Liquids.Single();
        Assert.Equal(LiquidKind.Magma, m.Kind);
        Assert.All(m.Tiles, Assert.True);
        // placements by FileDataID: the session's names, else #id
        Assert.Equal(2, t.Doodads.Count);
        Assert.All(t.Doodads, d => Assert.Equal(CubeName, d.Model));
        Assert.Equal(2f, t.Doodads[1].Scale);
        var b = t.Wmos.Single();
        Assert.Equal(($"#{WmoRoot}.wmo", 503u, 2f), (b.Model, b.Uid, b.Scale));
        Assert.Equal(2, t.Chunks[0]!.DoodadRefs);
        Assert.Equal(6, t.Textures.Count);
    }

    [Fact]
    public void ALockedObj0IsReportedAndTheGroundStillShows()
    {
        var t = Cat.LoadTerrain(TestMap, TileBX, TileBY)!;
        Assert.Equal(256, t.AllChunks.Count());
        Assert.Empty(t.Doodads);
        Assert.Contains(t.Notes, n => n.Contains($"#{TileBObj0}") && n.Contains("encrypted") && n.Contains("doodads and buildings"));
        var sum = MapOperations.Summarize(_s, TestMap, TileBX, TileBY);
        Assert.Contains(sum.Facts, f => f.Contains("encrypted"));
        Assert.Contains(sum.Report.Sections, s => s.Id == "notes");
        Assert.False(sum.Modified);
        Assert.Contains("Other Zone", sum.Facts[0]);
    }

    [Fact]
    public void TheWmoFileDataIdFormIsRead()
    {
        var g = WmoGeometry.Load($"#{WmoRoot}.wmo", p => _s.ReadCurrent(p), (id, ext) => WoWData.Core.M2.Modern.ModernModelSource.NameOf(_s.Files!, id, ext))!;
        Assert.True(g.FileDataIdMode);
        Assert.Equal((1, 1), (g.GroupCount, g.Groups.Count));
        Assert.Equal(new[] { $"#{WmoTexture}.blp" }, g.MaterialTextures);
        Assert.Equal(new[] { CubeName }, g.DoodadModels);
        var grp = g.Groups[0];
        Assert.Equal((8, 36), (grp.Positions.Length, grp.Indices.Length));
        Assert.Equal(new WmoBatch(0, 36, 0), grp.Batches.Single());      // the material of the large field (flag 0x2)
        Assert.Equal(new Vector3(-1), g.BoundsMin);
    }

    [Fact]
    public void TheTileIn3DHasEightLayerAtlasesItsDoodadsAndItsBuilding()
    {
        var tile = Cat.LoadTerrain(TestMap, TileAX, TileAY)!;
        var looks = _s.Looks!;
        var a = TerrainAsset.Build(new[] { tile }, p => _s.ReadCurrent(p), looks.LoadBlp, MapsViewModel.OtherBuildOptions(_s));
        var d0 = a.Chunks.Single(c => c.ChunkX == 0 && c.ChunkY == 0);
        Assert.Equal(6, d0.LayerCount);
        Assert.True(d0.Layers.Take(6).All(l => l >= 0) && d0.Layers.Skip(6).All(l => l == -1));
        var at2 = a.AlphaAtlases2.Single()!;
        // layer 5 is in the second atlas's green channel (rows 50-59 of chunk 0)
        Assert.Equal(200, at2.Pixels[(52 * 1024 + 5) * 4 + 1]);
        Assert.Equal(6, a.GroundTextures);
        Assert.All(a.Textures.Take(6), Assert.NotNull);
        Assert.Equal(2, a.Doodads.Count);
        Assert.Equal(1, a.DoodadModels);
        var (mesh, world) = a.Wmos.Single();
        Assert.Equal(12, mesh.Indices.Length / 3);
        Assert.Equal(0, a.MissingModels);
        Assert.Equal(2f, new Vector3(world.M11, world.M12, world.M13).Length(), 3);
        Assert.NotEmpty(a.Liquid);
        // holes are left out of the mesh: chunk 1 has 4 fine holes of 12 indices each fewer
        var d1 = a.Chunks.Single(c => c.ChunkX == 1 && c.ChunkY == 0);
        Assert.Equal(64 * 12 - 4 * 12, d1.IndexCount);
        // the building on its own
        var bld = TerrainAsset.BuildBuilding($"#{WmoRoot}.wmo", p => _s.ReadCurrent(p), looks.LoadBlp, (id, ext) => WoWData.Core.M2.Modern.ModernModelSource.NameOf(_s.Files!, id, ext));
        Assert.Equal(12, bld.Wmos.Single().Mesh.Indices.Length / 3);
        Assert.NotNull(bld.Textures.Single());
        Assert.Contains("1 of 1 groups, 12 triangles, 1 of 1 textures; 1 doodad models listed", BrowserViewModel.BuildingText(bld, WmoGeometry.Load($"#{WmoRoot}.wmo", p => _s.ReadCurrent(p))));
    }

    [Fact]
    public void OnThisTileListsZonesPlacementsAndTexturesWithoutServerParts()
    {
        var sum = MapOperations.Summarize(_s, TestMap, TileAX, TileAY);
        var keys = sum.Report.Sections.Select(x => x.Id).ToList();
        Assert.Equal(new[] { "zones", "doodads", "wmos", "textures", "terrain" }, keys);
        Assert.Contains($"(#{TileARoot})", string.Concat(sum.Report.Headline.Spans.Select(p => p.Text)));
        Assert.True(sum.Facts.Any(f => f.StartsWith("2 doodads (1 models), 1 buildings (WMO), 6 ground textures", StringComparison.Ordinal)), string.Join(" | ", sum.Facts));
        Assert.Contains(sum.Facts, f => f.Contains("Water or other liquid in 2 of 256 chunks"));
        // the doodad by the name the session knows; the building by its FileDataID
        var dd = sum.Report.Sections.Single(x => x.Id == "doodads");
        Assert.Contains(dd.Lines, l => l.Spans[0].Text == "TestCube.m2");
        Assert.Contains(sum.Report.Sections.Single(x => x.Id == "wmos").Lines, l => l.Spans[0].Text == $"#{WmoRoot}.wmo");
    }

    [Fact]
    public void ARootThatCannotBeReadIsAnIoErrorWithItsReason()
    {
        // a map from the WDT with a tile whose root file is not in the client
        var map = TestMap;
        var wdt = Cat.Wdt(map)!;
        var keep = wdt.Tile(TileAX, TileAY).Maid![0];
        wdt.Tile(TileAX, TileAY).Maid![0] = SyntheticClients.MissingTexture;    // a file that is not downloaded
        try
        {
            var e = Assert.Throws<IOException>(() => Cat.LoadTerrain(map, TileAX, TileAY));
            Assert.Contains("not on this computer", e.Message);
        }
        finally { wdt.Tile(TileAX, TileAY).Maid![0] = keep; }
    }
}
