using System.IO;
using System.Linq;
using System.Text;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Imaging;
using WoWData.Core.Maps;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Maps.Neutral;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>Milestones 5 and 6 of MAP_CONVERSION_DESIGN.md, the parts that need no game data: the minimap table, the WDL
/// heights, the names and tile lists, the minimap and ground texture conversion.</summary>
public class MapConversionUnitTests
{
    [Fact]
    public void Md5TranslateEntriesAreReplacedInPlaceAddedToTheirMapOrAsANewMap()
    {
        var trs = Encoding.Latin1.GetBytes("dir: AhnQiraj\r\nAhnQiraj\\map27_46.blp\taaa.blp\r\ndir: Azeroth\r\nAzeroth\\map32_48.blp\tbbb.blp\r\nAzeroth\\map32_49.blp\tccc.blp\r\ndir: Kalimdor\r\nKalimdor\\map1_1.blp\tddd.blp\r\n");
        var res = Encoding.Latin1.GetString(Md5Translate.Update(trs, "Azeroth", new Dictionary<(int, int), string> { [(32, 48)] = "new1.blp", [(25, 35)] = "new2.blp" }));
        Assert.Equal("dir: AhnQiraj\r\nAhnQiraj\\map27_46.blp\taaa.blp\r\ndir: Azeroth\r\nAzeroth\\map32_48.blp\tnew1.blp\r\nAzeroth\\map32_49.blp\tccc.blp\r\nAzeroth\\map25_35.blp\tnew2.blp\r\ndir: Kalimdor\r\nKalimdor\\map1_1.blp\tddd.blp\r\n", res);
        var added = Encoding.Latin1.GetString(Md5Translate.Update(trs, "ZephrasIsle", new Dictionary<(int, int), string> { [(3, 4)] = "z.blp" }));
        Assert.EndsWith("Kalimdor\\map1_1.blp\tddd.blp\r\ndir: ZephrasIsle\r\nZephrasIsle\\map03_04.blp\tz.blp\r\n", added);
        Assert.Equal("dir: X\r\nX\\map01_02.blp\th.blp\r\n", Encoding.Latin1.GetString(Md5Translate.Update(null, "X", new Dictionary<(int, int), string> { [(1, 2)] = "h.blp" })));
        // the client's own reader finds them
        Assert.Equal("new2.blp", MapCatalog.ParseMd5Translate(Encoding.Latin1.GetBytes(res))[@"Azeroth\map25_35.blp"]);
    }

    [Fact]
    public void WdlHeightsAreTheTruncatedCornersAndTheChunkMeans()
    {
        var t = NeutralTile.CreateFlat("Test", 10, 20, 100.75f);
        t.Chunks[0]!.Heights[0] = 5f;        // corner (0,0) -> 105.75 -> 105
        t.Chunks[255]!.Heights[144] = -0.5f; // far corner (16,16) -> 100.25 -> 100
        var h = WdlHeights.Of(t);
        Assert.Equal(545, h.Length);
        Assert.Equal(105, h[0]);
        Assert.Equal(100, h[16 * 17 + 16]);
        Assert.Equal(100, h[1]);
        Assert.Equal((short)(100.75 + 5.0 / 145), h[289]);
    }

    [Fact]
    public void NamesAndTileLists()
    {
        Assert.Equal("ZephrasIsle", MapConverter.ReadableDirectory("Zephras Isle", 2991));
        Assert.Equal("ExcavationSiteWetlands", MapConverter.ReadableDirectory("Excavation Site: Wetlands", 2998));
        Assert.Equal("Map3104", MapConverter.ReadableDirectory("", 3104));
        Assert.Equal(new[] { (32, 48), (33, 48) }, MapConverter.ParseTiles("32_48,33_48"));
        Assert.Equal(new[] { (32, 48), (33, 48) }, MapConverter.ParseTiles("32,48;33,48"));
        Assert.Throws<ArgumentException>(() => MapConverter.ParseTiles("64_1"));
        Assert.Throws<ArgumentException>(() => MapConverter.ParseTiles("x"));
        Assert.Throws<ArgumentException>(() => MapConverter.ParseTiles(""));
    }

    private static RgbaImage Gradient(int w, int h, bool alpha)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                img.Pixels[o] = (byte)(x * 255 / w);
                img.Pixels[o + 1] = (byte)(y * 255 / h);
                img.Pixels[o + 2] = 80;
                img.Pixels[o + 3] = alpha ? (byte)((x + y) % 256) : (byte)255;
            }
        return img;
    }

    [Fact]
    public void MinimapsBecome256DxtOneWithoutMipsNamedByTheirMd5()
    {
        var src = BlpEncoder.EncodeNew(Gradient(512, 512, false), "dxt1", mips: false);   // as 1.60's minimap tiles
        var (blp, name, reduced) = ConvertTextures.Minimap(src);
        Assert.True(reduced);
        Assert.Empty(BlpEncoder.Validate(blp));
        var b = BlpFile.Parse(blp);
        Assert.Equal((256, 256, "dxt1", 0), (b.Width, b.Height, BlpEncoder.FormatOf(b), (int)b.HasMips));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(blp)).ToLowerInvariant() + ".blp", name);
    }

    [Fact]
    public void GroundTexturesBecomeABaseAndASpecularFileThatOneTwelveLoads()
    {
        // a 1024 x 1024 "_s" file with mips: its own 512 level is taken; the base is opaque DXT1, the _s DXT3 keeps the alpha
        var big = BlpEncoder.EncodeNew(Gradient(1024, 1024, true), "dxt5");
        var g = ConvertTextures.Ground(big, null);
        Assert.Equal((512, 512, true), (g.Width, g.Height, g.Reduced));
        Assert.Empty(BlpEncoder.Validate(g.Base));
        Assert.Empty(BlpEncoder.Validate(g.Specular));
        Assert.Equal("dxt1", BlpEncoder.FormatOf(BlpFile.Parse(g.Base)));
        Assert.Equal("dxt3", BlpEncoder.FormatOf(BlpFile.Parse(g.Specular)));
        var spec = BlpFile.Parse(g.Specular).Decode(0);
        Assert.Contains(Enumerable.Range(0, spec.Width * spec.Height), i => spec.Pixels[i * 4 + 3] < 128);
        // a 768 x 128 picture (1.60's cube maps) becomes a power of two
        var odd = BlpEncoder.EncodeNew(Gradient(768, 128, false), "dxt1", mips: false);
        var g2 = ConvertTextures.Ground(odd, null);
        Assert.True(ImageOps.IsPow2(g2.Width) && ImageOps.IsPow2(g2.Height) && g2.Width <= 512);
        Assert.Empty(BlpEncoder.Validate(g2.Base));
    }
}

/// <summary>The 1.60 client, the Classic Era copy (names) and the 1.12 client for the conversion tests: opened once,
/// read only. The workspaces are throwaway folders; the game folder is never written.</summary>
public static class ConvertTestData
{
    private static readonly Lazy<MapConversionSource> Shared = new(() =>
    {
        var copy = ClientCopies.Inspect(Neutral160Data.Beta) ?? throw new InvalidOperationException("no 1.60 copy");
        return MapConversionSource.Open(copy, null, Neutral112Data.Client, null, TestEnv.TempDir("convert_names"));
    });

    public static IMapSource Source => Shared.Value.Source;

    /// <summary>The folder of the 1.12 client the tests read (TestEnv's game folder, else the G: copy).</summary>
    public static string ClientDir => File.Exists(Path.Combine(TestEnv.DataDir, "model.MPQ")) ? TestEnv.ClientDir : Path.GetDirectoryName(Neutral112Data.UserCopy)!;

    /// <summary>A new empty workspace for the 1.12 client in a temporary folder.</summary>
    public static MapConvertTarget NewTarget(out string dir)
    {
        dir = TestEnv.TempDir("convert_ws");
        var ws = new Workspace(new ClientPaths(dir, ClientDir));
        return new MapConvertTarget { Client = Neutral112Data.Client, Workspace = ws, Dbc = new DbcService(Neutral112Data.Client, ws), Locale = 0 };
    }

    public static List<string> FilesIn(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList() : new List<string>();
}

/// <summary>
/// Milestones 5 and 6 on the real data (skipped without the 1.60 copy, the Classic Era copy and the 1.12 client): a dry
/// run writes nothing; a real conversion is one change set whose files validate, with the WDT, WDL, minimap table and
/// tables in step; reverting it removes everything; a new map gets its Map, Light and AreaTable rows within 1.12's
/// limits; areas made by a first conversion are found by the second.
/// </summary>
public class MapConversionTests
{
    private readonly ITestOutputHelper _out;

    public MapConversionTests(ITestOutputHelper output) => _out = output;

    [Casc160Fact(names: true)]
    public void AZoneDryRunReportsAndWritesNothing()
    {
        var target = ConvertTestData.NewTarget(out var dir);
        var res = MapConverter.Run(ConvertTestData.Source, new MapConvertRequest { Map = "Azeroth", Zone = "Elwynn Forest" }, target, dryRun: true);
        foreach (var l in res.Lines()) _out.WriteLine(l);
        Assert.True(res.Ok, string.Join("; ", res.Validation.Findings.Take(3)));
        Assert.True(res.DryRun);
        Assert.Null(res.ChangeSet);
        Assert.Equal(0, res.TargetMapId);
        Assert.False(res.NewMap);
        Assert.InRange(res.Tiles.Count, 15, 40);
        Assert.Contains((32, 48), res.Tiles);            // Northshire
        Assert.Contains(res.AlsoCovers, z => z.Zone == "Westfall");
        Assert.True(res.TexturesReused > 20 && res.TextureUsesReused > 10000);
        Assert.True(res.DoodadsKept > 10000 && res.DoodadsKept112 > 100, $"{res.DoodadsKept} / {res.DoodadsKept112}");
        Assert.True(res.ShadowsReused > 1000);
        Assert.Equal(res.Tiles.Count, res.Minimaps);
        Assert.Contains(res.NewAreas, a => a.Id == 17203 && a.Name == "Stormwind Harbor" && a.ParentId == 1519 && a.MapId == 0 && a.AreaBit <= MapConverter.MaxAreaBit);
        var totals = res.Report.Totals();
        Assert.True(totals.ContainsKey(LossKind.VertexColorsDropped) && totals.ContainsKey(LossKind.AlphaReduced) && totals.ContainsKey(LossKind.MinimapReduced));
        Assert.Equal(res.Tiles.Count, res.Files.Count(f => f.Kind == "ADT"));
        // nothing was written: no workspace files, no change list
        Assert.Empty(ConvertTestData.FilesIn(dir));
        Assert.Empty(target.Workspace.List());
    }

    [Casc160Fact(names: true)]
    public void AConversionIsOneChangeSetThatValidatesAndRevertsCompletely()
    {
        var target = ConvertTestData.NewTarget(out var dir);
        // Northshire, a tile 1.60 added west of the Great Sea (not in the 1.12 WDT) and a tile with 1.60's Stormwind Harbor
        var src = ConvertTestData.Source;
        var smap = src.FindMap("0")!;
        var wdt160 = src.ReadWdt(smap);
        var harbor = wdt160.ExistingTiles().Where(t => t.Y >= 44 && t.Y <= 50 && t.X >= 29 && t.X <= 34).OrderBy(t => t.Y).ThenBy(t => t.X)
            .First(t => src.ChunkAreas(smap, wdt160, t.X, t.Y)!.Contains(17203));
        var req = new MapConvertRequest { Map = "0", Tiles = new[] { (32, 48), (24, 35), harbor } };
        var res = MapConverter.Run(ConvertTestData.Source, req, target, dryRun: false);
        foreach (var l in res.Lines()) _out.WriteLine(l);
        var cs = Assert.IsType<ChangeSet>(res.ChangeSet);
        var ws = target.Workspace;
        Assert.Equal(cs.Files.Count, ws.List().Count);
        Assert.Equal(res.Files.Count, cs.Files.Count);
        Assert.Equal(1, res.NewTiles);
        // every map file validates and the tiles load
        foreach (var f in res.Files.Where(f => MapValidator.KindOf(f.Path) != null))
        {
            var v = MapValidator.ValidateBytes(ws.ReadEdited(f.Path)!, Path.GetFileName(f.Path));
            Assert.True(v.Ok, $"{f.Path}: {string.Join("; ", v.Findings.Take(3))}");
        }
        var adt = ws.ReadEdited(@"World\Maps\Azeroth\Azeroth_24_35.adt")!;
        Assert.Equal(256, TerrainTile.Load(adt, "Azeroth", 24, 35).AllChunks.Count());
        // the WDT flags the new tile, the WDL has both, the minimap table names the new minimaps
        var wdt = new WdtFile(ws.ReadEdited(@"World\Maps\Azeroth\Azeroth.wdt")!);
        Assert.Contains((24, 35), wdt.Tiles());
        Assert.Equal(new WdtFile(target.Client.Read(@"World\Maps\Azeroth\Azeroth.wdt")!).Tiles().Count() + 1, wdt.Tiles().Count());
        var wdl = new WdlFile(ws.ReadEdited(@"World\Maps\Azeroth\Azeroth.wdl")!);
        Assert.NotNull(wdl.Heights(24, 35));
        Assert.NotNull(wdl.Heights(40, 30));                 // the other tiles stay
        var trs = MapCatalog.ParseMd5Translate(ws.ReadEdited(@"textures\Minimap\md5translate.trs"));
        var mm = trs[@"Azeroth\map24_35.blp"];
        var mmBlp = BlpFile.Parse(ws.ReadEdited($@"textures\Minimap\{mm}")!);
        Assert.Equal((256, 256), (mmBlp.Width, mmBlp.Height));
        Assert.Equal(MapCatalog.ParseMd5Translate(target.Client.Read(@"textures\Minimap\md5translate.trs")).Count + 1, trs.Count);
        // the new areas of the new tile are AreaTable rows of the workspace, with free exploration bits
        var at = target.Dbc.LoadCurrent("AreaTable");
        var gameAt = target.Dbc.LoadGame("AreaTable");
        var gameBits = gameAt.Rows.Select(r => gameAt.Number(r, gameAt.Layout.Column("AreaBit")!)).ToHashSet();
        Assert.Equal(res.NewAreas.Count, res.NewAreas.Select(a => a.AreaBit).Distinct().Count());
        Assert.DoesNotContain(res.NewAreas, a => gameBits.Contains(a.AreaBit));   // a free bit (the game's own table repeats 4)
        foreach (var a in res.NewAreas)
        {
            var row = at.Find(a.Id);
            Assert.NotNull(row);
            Assert.Equal(a.Name, at.Text(row!, at.Layout.Column("AreaName")!));
            Assert.InRange(at.Number(row!, at.Layout.Column("AreaBit")!), 0, MapConverter.MaxAreaBit);
        }
        Assert.Contains(res.NewAreas, a => a.Id == 17203);
        // converted textures come with their _s file; placements only name models the game has
        foreach (var t in res.TexturesConverted)
        {
            Assert.NotNull(ws.ReadEdited(t.Path));
            Assert.NotNull(ws.ReadEdited(GroundTextures.SpecularName(t.Path)!));
        }
        var tile = Adt112Reader.ReadAdt(ws.ReadEdited(@"World\Maps\Azeroth\Azeroth_32_48.adt")!, "Azeroth", 32, 48);
        Assert.All(tile.DoodadModels, m => Assert.True(target.Client.Exists(m.Path!), m.Path));
        Assert.All(tile.WmoModels, m => Assert.True(target.Client.Exists(m.Path!), m.Path));
        Assert.Contains(tile.AllChunks, c => c.Shadow != null && (c.Flags & 1) != 0);   // 1.12 shadows kept on unchanged ground
        // a second run finds the areas the first made (nothing new)
        var again = MapConverter.Run(ConvertTestData.Source, req, target, dryRun: true);
        Assert.Empty(again.NewAreas);
        Assert.Contains(again.Warnings, w => w.Contains("already have your own edits"));
        // reverting the change set removes every file and record
        ws.RevertChangeSet(cs.Id);
        Assert.Empty(ws.List());
        Assert.Empty(ConvertTestData.FilesIn(ws.Paths.WorkspaceDir));
    }

    [Casc160Fact(names: true)]
    public void ANewMapGetsItsMapLightAndAreaRows()
    {
        var target = ConvertTestData.NewTarget(out _);
        var dry = MapConverter.Run(ConvertTestData.Source, new MapConvertRequest { Map = "Zephras Isle" }, target, dryRun: true);
        foreach (var l in dry.Lines()) _out.WriteLine(l);
        Assert.True(dry.Ok);
        Assert.True(dry.NewMap);
        Assert.Equal(("ZephrasIsle", 2991), (dry.TargetDirectory, dry.TargetMapId));
        Assert.Equal(dry.Tiles.Count, dry.NewTiles);
        Assert.True(dry.Tiles.Count >= 50, $"{dry.Tiles.Count} tiles");
        Assert.Contains(dry.NewAreas, a => a.Id == 16593 && a.Name == "Zephras Isle" && a.ParentId == 0 && a.MapId == 2991);
        Assert.All(dry.NewAreas.Where(a => a.Id != 16593), a => Assert.Equal(16593, a.ParentId));
        Assert.True(dry.DoodadsLeftOut > 0 && dry.LeftOut.Count > 0);
        Assert.Contains(dry.TableNotes, n => n.StartsWith("Map.dbc: a new row 2991"));
        Assert.Contains(dry.ServerSteps, s => s.Contains("map_template"));
        // a real conversion of two of its tiles: the tables of the workspace
        var tiles = dry.Tiles.Take(2).ToList();
        var res = MapConverter.Run(ConvertTestData.Source, new MapConvertRequest { Map = "2991", Tiles = tiles }, target, dryRun: false);
        Assert.NotNull(res.ChangeSet);
        var map = target.Dbc.LoadCurrent("Map");
        var row = map.Find(2991)!;
        Assert.Equal("ZephrasIsle", map.Text(row, map.Layout.Column("Directory")!));
        Assert.Equal("Zephras Isle", map.Text(row, map.Layout.Column("MapName")!));
        var ls = (int)map.Number(row, map.Layout.Column("LoadingScreenID")!);
        Assert.NotNull(target.Dbc.LoadCurrent("LoadingScreens").Find(ls));
        var light = target.Dbc.LoadCurrent("Light");
        Assert.Contains(light.Rows, r => light.Number(r, light.Layout.Column("ContinentID")!) == 2991);
        var wdt = new WdtFile(target.Workspace.ReadEdited(@"World\Maps\ZephrasIsle\ZephrasIsle.wdt")!);
        Assert.Equal(tiles.OrderBy(t => t).ToList(), wdt.Tiles().OrderBy(t => t).ToList());
        // a second run converts into the same new map (it is not made again)
        var more = MapConverter.Run(ConvertTestData.Source, new MapConvertRequest { Map = "2991", Tiles = dry.Tiles.Skip(2).Take(1).ToList() }, target, dryRun: true);
        Assert.False(more.NewMap);
        Assert.Equal(("ZephrasIsle", 2991), (more.TargetDirectory, more.TargetMapId));
        Assert.Empty(more.NewAreas.Where(a => a.Id == 16593));
        target.Workspace.RevertChangeSet(res.ChangeSet!.Id);
        Assert.Empty(target.Workspace.List());
    }

    [Casc160Fact(names: true)]
    public void BadRequestsAreRefusedInWords()
    {
        var target = ConvertTestData.NewTarget(out _);
        var src = ConvertTestData.Source;
        Assert.Contains("no map", Assert.Throws<ArgumentException>(() => MapConverter.Run(src, new MapConvertRequest { Map = "NoSuchMap" }, target, true)).Message);
        Assert.Contains("no zone", Assert.Throws<ArgumentException>(() => MapConverter.Run(src, new MapConvertRequest { Map = "0", Zone = "Nowhere Land" }, target, true)).Message);
        Assert.Contains("no tile", Assert.Throws<ArgumentException>(() => MapConverter.Run(src, new MapConvertRequest { Map = "0", Tiles = new[] { (0, 0) } }, target, true)).Message);
    }
}

/// <summary>"wds versions convert-map" gives the same result as the app's Core call, needs --yes to write, and its change
/// set reverts with "wds changes revert-set".</summary>
[Collection(IndexCollection.Name)]
public class MapConversionCliTests
{
    private const string Clients = @"G:\WoW Clients and Patches\Clients";

    [Casc160Fact(names: true)]
    public void ConvertMapCommandMatchesTheCoreAndNeedsYes()
    {
        using var env = new CliTestEnv("convert");
        var args = new[] { "versions", "convert-map", "_classic_beta_", "Azeroth", "--tiles", "32_48", "--folder", Clients };
        // --dry-run: the report, nothing written
        var dry = env.Json(args.Append("--dry-run").ToArray());
        Assert.True(dry.Code == 0, dry.ToString());
        Assert.True(dry.Data.GetProperty("dryRun").GetBoolean());
        Assert.Equal("32_48", dry.Data.GetProperty("tiles")[0].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, dry.Data.GetProperty("changeSet").ValueKind);
        Assert.Empty(ConvertTestData.FilesIn(env.Paths.WorkspaceDir));
        // the same numbers as the Core call the app makes
        var target = ConvertTestData.NewTarget(out _);
        var core = MapConverter.Run(ConvertTestData.Source, new MapConvertRequest { Map = "Azeroth", Tiles = new[] { (32, 48) } }, target, true);
        Assert.Equal(core.DoodadsKept, dry.Data.GetProperty("doodads").GetProperty("kept").GetInt32());
        Assert.Equal(core.DoodadsLeftOut, dry.Data.GetProperty("doodads").GetProperty("leftOut").GetInt32());
        Assert.Equal(core.Files.Count, dry.Data.GetProperty("files").GetArrayLength());
        Assert.Equal(core.Report.Totals().Count, dry.Data.GetProperty("report").EnumerateObject().Count());
        // without --yes: exit 5, nothing written
        var noYes = env.Json(args);
        Assert.Equal(5, noYes.Code);
        Assert.Empty(ConvertTestData.FilesIn(env.Paths.WorkspaceDir));
        // --yes: one change set, reverted by its command
        var yes = env.Json(args.Append("--yes").ToArray());
        Assert.True(yes.Code == 0, yes.ToString());
        var id = yes.Data.GetProperty("changeSet").GetProperty("id").GetString()!;
        Assert.True(File.Exists(Path.Combine(env.Paths.WorkspaceDir, "World", "Maps", "Azeroth", "Azeroth_32_48.adt")));
        var rev = env.Json("changes", "revert-set", id, "--yes");
        Assert.True(rev.Code == 0, rev.ToString());
        Assert.Empty(ConvertTestData.FilesIn(env.Paths.WorkspaceDir));
        // usage errors
        Assert.Equal(2, env.Run(args.Concat(new[] { "--zone", "Elwynn Forest" }).ToArray()).Code);
        Assert.Equal(6, env.Run("versions", "convert-map", "_classic_beta_", "NoSuchMap", "--folder", Clients, "--dry-run").Code);
    }
}
