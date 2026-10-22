using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WoWData.Cli;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Imaging;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;
using WoWData.Core.Versions.Catalog;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>The update catalog's parts that need no game data: names and filters, folder groups, the pixel comparison,
/// making a texture loadable by 1.12, the strings and font readers, the catalog file.</summary>
public class UpdateCatalogUnitTests
{
    private static RgbaImage Gradient(int w, int h, bool invert = false)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte r = (byte)(x * 255 / Math.Max(1, w - 1)), g = (byte)(y * 255 / Math.Max(1, h - 1));
                img.Pixels[o] = invert ? (byte)(255 - r) : r;
                img.Pixels[o + 1] = invert ? (byte)(255 - g) : g;
                img.Pixels[o + 2] = invert ? (byte)200 : (byte)60;
                img.Pixels[o + 3] = 255;
            }
        return img;
    }

    [Fact]
    public void KindsAndClassesHaveWordsAndKeysThatParseBack()
    {
        foreach (var k in Enum.GetValues<UpdateKind>())
        {
            Assert.Equal(k, UpdateNames.ParseKind(UpdateNames.Of(k)));
            Assert.Equal(k, UpdateNames.ParseKind(UpdateNames.Key(k)));
        }
        foreach (var c in Enum.GetValues<Portability>())
        {
            Assert.Equal(c, UpdateNames.ParseClass(UpdateNames.Of(c)));
            Assert.Equal(c, UpdateNames.ParseClass(UpdateNames.Key(c)));
        }
        Assert.Equal("zone-terrain", UpdateNames.Key(UpdateKind.ZoneTerrain));
        Assert.Equal("needs-client-code", UpdateNames.Key(Portability.NeedsClientCode));
        Assert.Equal(UpdateKind.SkyLighting, UpdateNames.ParseKind("lighting"));
        Assert.Null(UpdateNames.ParseKind("sky"));   // ambiguous: Sky and lighting, Skyboxes
        Assert.Equal(UpdateKind.ZoneTerrain, UpdateNames.ParseKind("terrain"));
        Assert.Equal(Portability.NeedsConverter, UpdateNames.ParseClass("converter"));
        Assert.Null(UpdateNames.ParseKind("nothing like it"));
        Assert.Equal("1.5 MB", UpdateNames.Size(1572864));
    }

    [Fact]
    public void FiltersMatchKindClassPlaceAndWords()
    {
        var a = new UpdateItem { Id = "terrain:0:12", Kind = UpdateKind.ZoneTerrain, Title = "Elwynn Forest: terrain", Where = "Elwynn Forest (Eastern Kingdoms)", Class = Portability.Ready, Terrain = new TerrainPick(0, "Eastern Kingdoms", "Azeroth", 12, "Elwynn Forest", new() { "32_48" }, false) };
        var b = new UpdateItem { Id = "tex:same:creature", Kind = UpdateKind.Textures, Title = "Re-encoded textures", Where = "Creature", Class = Portability.NoVisibleChange, NotYet = "nothing to bring." };
        Assert.True(new UpdateFilter { Kind = UpdateKind.ZoneTerrain, Where = "elwynn" }.Matches(a));
        Assert.False(new UpdateFilter { Kind = UpdateKind.Textures }.Matches(a));
        Assert.True(new UpdateFilter { Search = "forest terrain" }.Matches(a));
        Assert.False(new UpdateFilter { Search = "forest murloc" }.Matches(a));
        Assert.False(new UpdateFilter { HideNoChange = true }.Matches(b));
        Assert.False(new UpdateFilter { OnlyImportable = true }.Matches(b));
        Assert.True(new UpdateFilter { Class = Portability.NoVisibleChange, Where = "creat" }.Matches(b));
        Assert.Contains(a.Lines(), l => l.StartsWith("Import: yes", StringComparison.Ordinal));
        Assert.Contains(b.Lines(), l => l.StartsWith("Import: not yet. nothing to bring.", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldersAreGroupedWithoutCaseAndSplitWhenLarge()
    {
        var paths = new[] { @"Creature\Boar\a.blp", @"CREATURE\Wolf\b.blp", @"World\Azeroth\c.blp", @"WORLD\AZEROTH\d.blp", @"Tileset\x.blp", @"TILESET\y.blp" };
        var g = UpdateCatalogScanner.Groups(paths, p => p);
        Assert.Equal(new[] { "Creature", "Tileset", @"World\Azeroth" }, g.Select(x => x.Key).ToArray());
        Assert.All(g, x => Assert.Equal(2, x.Items.Count));
        // a large folder is split by its subfolders; small subfolders stay together
        var many = Enumerable.Range(0, 30).Select(i => $@"Creature\Big\f{i}.blp").Concat(Enumerable.Range(0, 30).Select(i => $@"Creature\Large\f{i}.blp"))
            .Concat(new[] { @"Creature\Tiny\a.blp", @"Creature\Small\b.blp" }).ToList();
        var split = UpdateCatalogScanner.Groups(many, p => p, maxGroup: 40, minSub: 5);
        Assert.Equal(new[] { "Creature (other folders)", @"Creature\Big", @"Creature\Large" }, split.Select(x => x.Key).ToArray());
        Assert.Equal(2, split[0].Items.Count);
        Assert.Equal("Tileset", UpdateCatalogScanner.Pretty(new[] { "TILESET", "Tileset", "TILESET" }));
        Assert.Equal("world/azeroth", UpdateCatalogScanner.IdOf(@"World\Azeroth"));
        Assert.Equal(@"Interface\Icons", UpdateCatalogScanner.BaseGroup(@"Interface\Icons\INV_Sword_01.blp"));
        Assert.Equal("Creature", UpdateCatalogScanner.BaseGroup(@"Creature\Boar\Boar.blp"));
        Assert.True(UpdateCatalogScanner.IsComposited(@"Character\Human\Male\HumanMaleSkin00_00.blp"));
        Assert.True(UpdateCatalogScanner.IsComposited(@"Item\TextureComponents\ArmUpperTexture\x.blp"));
        Assert.False(UpdateCatalogScanner.IsComposited(@"Item\ObjectComponents\Weapon\x.blp"));
        Assert.Equal("Garrison", UpdateCatalogScanner.TableGroup("GarrFollower"));
        Assert.Equal("Spell", UpdateCatalogScanner.TableGroup("SpellVisualKit"));
    }

    [Fact]
    public void PicturesAreComparedPixelByPixel()
    {
        var img = Gradient(64, 64);
        var orig = BlpEncoder.EncodeNew(img, "dxt1");
        // the same picture in another format: re-encoded only
        var again = BlpEncoder.EncodeNew(BlpFile.Parse(orig).Decode(0), "dxt3");
        Assert.Equal(PixelVerdict.ReEncoded, UpdateCatalogScanner.ComparePictures(orig, again).Verdict);
        // another picture: repainted
        var other = BlpEncoder.EncodeNew(Gradient(64, 64, invert: true), "dxt1");
        Assert.Equal(PixelVerdict.Repainted, UpdateCatalogScanner.ComparePictures(orig, other).Verdict);
        // the same picture twice as large (compared at the 1.12 size, its own mip level): larger
        var larger = BlpEncoder.EncodeNew(ImageOps.Resize(img, 128, 128), "dxt1");
        var (v, note) = UpdateCatalogScanner.ComparePictures(orig, larger);
        Assert.Equal(PixelVerdict.Larger, v);
        Assert.Contains("64 x 64 made 128 x 128", note);
        Assert.Equal(PixelVerdict.Smaller, UpdateCatalogScanner.ComparePictures(orig, BlpEncoder.EncodeNew(Gradient(32, 32), "dxt1")).Verdict);
    }

    [Fact]
    public void TexturesAreMadeLoadableByOneTwelve()
    {
        // 1024 x 1024 with mips: its own 512 level and below, byte for byte
        var big = BlpEncoder.EncodeNew(Gradient(1024, 1024), "dxt5");
        var (data, note) = UpdateFileConvert.Fit(@"Interface\Glues\x.blp", big);
        Assert.Empty(BlpEncoder.Validate(data));
        var b = BlpFile.Parse(data);
        Assert.Equal((512, 512), (b.Width, b.Height));
        Assert.True(b.MipData(0).SequenceEqual(BlpFile.Parse(big).MipData(1)));
        Assert.Contains("1024 x 1024 made 512 x 512", note);
        // a file 1.12 loads stays as it is
        var ok = BlpEncoder.EncodeNew(Gradient(256, 256), "dxt1");
        Assert.Same(ok, UpdateFileConvert.Fit("a.blp", ok).Data);
        // no mips and not a power of two: decoded, made a power of two at most 512, encoded again
        var odd = BlpEncoder.EncodeNew(Gradient(768, 128), "dxt1", mips: false);
        var (fixedOdd, _) = UpdateFileConvert.Fit("b.blp", odd);
        Assert.Empty(BlpEncoder.Validate(fixedOdd));
        var fo = BlpFile.Parse(fixedOdd);
        Assert.True(ImageOps.IsPow2(fo.Width) && ImageOps.IsPow2(fo.Height) && fo.Width <= 512);
        // other files as they are
        var ttf = new byte[] { 0, 1, 0, 0, 1, 2, 3 };
        Assert.Same(ttf, UpdateFileConvert.Fit(@"Fonts\x.ttf", ttf).Data);
    }

    [Fact]
    public void GlobalStringsAndFontNamesAreRead()
    {
        var lua = "ABANDON_QUEST_CONFIRM = \"Abandon \\\"%s\\\"?\";\r\nHELLO = \"Line one\\nLine two\";\r\n-- a comment\r\nBAD LINE\r\n";
        var s = UpdateCatalogScanner.ParseGlobalStrings(lua);
        Assert.Equal(2, s.Count);
        Assert.Equal("Abandon \"%s\"?", s["ABANDON_QUEST_CONFIRM"]);
        Assert.Equal("Line one\nLine two", s["HELLO"]);
        Assert.Equal("Abandon \"%s\"?", UpdateCatalogScanner.Unescape("Abandon \\\"%s\\\"?"));

        // a TrueType file with only a name table: name 4 (full name) on the Windows platform, UTF-16BE
        var name = Encoding.BigEndianUnicode.GetBytes("Skurri Cyr");
        var ttf = new List<byte>();
        void U16(int v) { ttf.Add((byte)(v >> 8)); ttf.Add((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        U32(0x00010000); U16(1); U16(16); U16(0); U16(0);
        ttf.AddRange(Encoding.ASCII.GetBytes("name")); U32(0); U32(28); U32((uint)(18 + name.Length));
        U16(0); U16(1); U16(18);
        U16(3); U16(1); U16(0x409); U16(4); U16(name.Length); U16(0);
        ttf.AddRange(name);
        Assert.Equal("Skurri Cyr", UpdateCatalogScanner.FontName(ttf.ToArray()));
        Assert.Null(UpdateCatalogScanner.FontName(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ACatalogIsSavedAndReadBack()
    {
        var cat = new UpdateCatalog
        {
            Source = "_classic_beta_, 1.60.1.70450", Target = @"X:\Game\Data", SourceFingerprint = "s", TargetFingerprint = "t", Scanned = new DateTime(2026, 10, 20, 21, 0, 0),
            Items =
            {
                new UpdateItem { Id = "fonts:changed", Kind = UpdateKind.Fonts, Title = "Changed fonts (1)", Where = "Fonts", Files = 1, Bytes = 100, Class = Portability.Ready,
                    FileList = new() { new UpdateFile(@"Fonts\FRIZQT__.TTF", 615960, false, 100, "TrueType") } },
                new UpdateItem { Id = "engine:ssao", Kind = UpdateKind.EngineFeatures, Title = "Ambient occlusion (SSAO)", Class = Portability.NeedsClientCode, NotYet = "client code" },
            },
        };
        var f = Path.Combine(TestEnv.TempDir("catalog_file"), "c.json");
        cat.Save(f);
        var back = UpdateCatalog.Load(f);
        Assert.Equal(2, back.Items.Count);
        var fonts = back.Find("FONTS:changed")!;
        Assert.Equal(Portability.Ready, fonts.Class);
        Assert.Equal(615960u, fonts.FileList![0].FileDataId);
        Assert.False(back.Find("engine:ssao")!.Importable);
        Assert.Equal(1, back.Counts()[UpdateKind.Fonts][Portability.Ready]);
        Assert.StartsWith("2 differences between _classic_beta_", back.SummaryLines()[0]);
    }

    [Fact]
    public void TerrainPicksOfOneMapBecomeOneConversion()
    {
        UpdateItem Zone(int id, string name, params string[] tiles) => new() { Id = $"terrain:0:{id}", Kind = UpdateKind.ZoneTerrain, Terrain = new TerrainPick(0, "Eastern Kingdoms", "Azeroth", id, name, tiles.ToList(), false) };
        var one = UpdateImporter.RequestOf(new() { Zone(12, "Elwynn Forest", "32_48", "33_48") });
        Assert.Equal(("0", "12"), (one.Map, one.Zone));
        var two = UpdateImporter.RequestOf(new() { Zone(12, "Elwynn Forest", "32_48", "33_48"), Zone(40, "Westfall", "33_48", "30_50") });
        Assert.Null(two.Zone);
        Assert.Equal(new[] { (32, 48), (33, 48), (30, 50) }, two.Tiles);
        Assert.Equal("the zones Elwynn Forest and Westfall", two.SelectionLabel);
        var whole = UpdateImporter.RequestOf(new() { new UpdateItem { Id = "map:2991", Terrain = new TerrainPick(2991, "Zephras Isle", "2991", null, null, new(), true) } });
        Assert.Equal("2991", whole.Map);
        Assert.Null(whole.Tiles);
        Assert.Null(whole.Zone);
    }
}

/// <summary>A test that needs the 1.60 copy, the Classic Era copy (names) and the stock 1.12 copy on G: (never the game folder).</summary>
public sealed class CatalogFactAttribute : FactAttribute
{
    public CatalogFactAttribute()
    {
        if (!Neutral160Data.HasBeta) Skip = $"needs the 1.60 copy at {Neutral160Data.Beta}";
        else if (!Directory.Exists(Neutral160Data.Era)) Skip = $"needs the Classic Era copy at {Neutral160Data.Era}";
        else if (!File.Exists(Path.Combine(CatalogTestData.Data112, "model.MPQ"))) Skip = $"needs the 1.12 copy at {CatalogTestData.Data112}";
    }
}

/// <summary>The catalog of the real copies, scanned once per test run into a temporary cache (read only on both sides).</summary>
public static class CatalogTestData
{
    public const string Data112 = Neutral112Data.UserCopy;
    public const string Clients = @"G:\WoW Clients and Patches\Clients";
    public static string Client112Dir => Path.GetDirectoryName(Data112)!;
    public static readonly string CacheRoot = TestEnv.TempDir("catalog_cache");

    private static readonly Lazy<ClientData> Game = new(() => new ClientData(Data112));
    private static readonly Lazy<UpdateCatalogSource> Src = new(() => UpdateCatalogSource.Open(ClientCopies.Inspect(Neutral160Data.Beta)!, null, Game.Value, null, CacheRoot));
    private static readonly Lazy<UpdateCatalog> Cat = new(() => UpdateCatalogStore.LoadOrScan(Src.Value, false));

    public static ClientData Client => Game.Value;
    public static UpdateCatalogSource Source => Src.Value;
    public static UpdateCatalog Catalog => Cat.Value;

    /// <summary>A new empty workspace for the 1.12 copy in a temporary folder.</summary>
    public static MapConvertTarget NewTarget(out string dir)
    {
        dir = TestEnv.TempDir("catalog_ws");
        var ws = new Workspace(new ClientPaths(dir, Client112Dir));
        return new MapConvertTarget { Client = Client, Workspace = ws, Dbc = new DbcService(Client, ws), Locale = 0 };
    }

    public static List<string> FilesIn(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList() : new List<string>();
}

/// <summary>
/// The update catalog of the real "1.60" client against the stock 1.12 copy (skipped without them): counts per kind,
/// classes and reasons on every update, stable unique IDs, the cached catalog; a zone's terrain previews (nothing written)
/// and imports into a throwaway workspace as one change set that reverts completely; a texture pick imports; several picks
/// are one change set; "wds versions updates / update-info / import-updates" give the same results.
/// </summary>
public class UpdateCatalogTests
{
    private readonly ITestOutputHelper _out;

    public UpdateCatalogTests(ITestOutputHelper output) => _out = output;

    [CatalogFact]
    public void TheCatalogListsEveryKindWithItsClass()
    {
        var cat = CatalogTestData.Catalog;
        foreach (var l in cat.SummaryLines().Concat(cat.Notes)) _out.WriteLine(l);
        var counts = cat.Counts();
        int Count(UpdateKind k, Portability? c = null) => counts.TryGetValue(k, out var d) ? c is { } cc ? d.GetValueOrDefault(cc) : d.Values.Sum() : 0;
        Assert.InRange(Count(UpdateKind.ZoneTerrain, Portability.Ready), 40, 150);
        Assert.InRange(Count(UpdateKind.NewMap), 12, 40);
        Assert.True(Count(UpdateKind.SkyLighting, Portability.NeedsConverter) >= 10 && Count(UpdateKind.SkyLighting, Portability.NeedsClientCode) >= 5);
        Assert.True(Count(UpdateKind.Skybox) >= 10);
        Assert.Equal(1, Count(UpdateKind.Weather, Portability.NeedsClientCode));
        Assert.True(Count(UpdateKind.GroundTextures, Portability.Ready) >= 2);
        Assert.True(Count(UpdateKind.Textures, Portability.Ready) >= 30 && Count(UpdateKind.Textures, Portability.NoVisibleChange) >= 3 && Count(UpdateKind.Textures, Portability.NeedsClientCode) >= 1);
        Assert.True(Count(UpdateKind.Models, Portability.NeedsConverter) >= 1 && Count(UpdateKind.Buildings, Portability.NeedsConverter) >= 10);
        Assert.True(Count(UpdateKind.InterfaceArt, Portability.Ready) >= 50);
        Assert.True(Count(UpdateKind.InterfaceStrings) >= 2 && Count(UpdateKind.Fonts) >= 1 && Count(UpdateKind.Sounds) >= 1);
        Assert.True(Count(UpdateKind.DataTables, Portability.NeedsServer) >= 10 && Count(UpdateKind.DataTables, Portability.NeedsClientCode) >= 10);
        Assert.True(Count(UpdateKind.EngineFeatures, Portability.NeedsClientCode) >= 10);
        // every update: a unique ID, a reason for its class, something to import or why not
        Assert.Equal(cat.Items.Count, cat.Items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(cat.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.ClassReason), i.Id));
        Assert.All(cat.Items.Where(i => i.Importable), i => Assert.True(i.Terrain != null || i.FileList is { Count: > 0 }, i.Id));
        Assert.All(cat.Items.Where(i => i.Class is Portability.NeedsClientCode or Portability.NoVisibleChange), i => Assert.False(i.Importable, i.Id));
        // known differences (DIFF_112_160, MAP_SURVEY_160)
        var elwynn = cat.Find("terrain:0:12")!;
        Assert.Equal((UpdateKind.ZoneTerrain, Portability.Ready, 12), (elwynn.Kind, elwynn.Class, elwynn.Terrain!.ZoneId!.Value));
        Assert.Contains("32_48", elwynn.Terrain.Tiles);
        var zephras = cat.Find("map:2991")!;
        Assert.True(zephras.Kind == UpdateKind.NewMap && zephras.Terrain!.WholeMap && zephras.Files >= 50);
        Assert.Contains(cat.Items, i => i.Id.StartsWith("wmo:replaced:", StringComparison.Ordinal) && i.Title.Contains("stormwind", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, cat.Find("fonts:changed")!.FileList!.Count);
        Assert.NotNull(cat.Find("models:player-hd"));
        Assert.NotNull(cat.Find("engine:ssao"));
        Assert.Contains(cat.Items, i => i.Id.StartsWith("hotfix:", StringComparison.Ordinal) && i.Class == Portability.NeedsServer);
        Assert.Contains(cat.Items, i => i.Id.StartsWith("tex:same:", StringComparison.Ordinal) && i.Class == Portability.NoVisibleChange && i.Files > 100);
        // the catalog is kept: a second load is the cached one
        var again = UpdateCatalogStore.LoadCached(CatalogTestData.Source);
        Assert.NotNull(again);
        Assert.Equal(cat.Items.Count, again!.Items.Count);
    }

    [CatalogFact]
    public void AZonePreviewsWritesNothingAndImportsAsOneChangeSetThatReverts()
    {
        var cat = CatalogTestData.Catalog;
        // a small zone: Thunder Bluff (Kalimdor)
        var zone = cat.Items.Where(i => i.Kind == UpdateKind.ZoneTerrain && i.Terrain?.ZoneId != null && i.Files <= 8).OrderBy(i => i.Files).First();
        _out.WriteLine($"{zone.Id}: {zone.Title}, {zone.Files} tiles");
        var target = CatalogTestData.NewTarget(out var dir);
        var dry = UpdateImporter.Run(CatalogTestData.Source, cat, new[] { zone.Id }, target, dryRun: true);
        foreach (var l in dry.Lines()) _out.WriteLine(l);
        Assert.True(dry.Ok && dry.DryRun);
        Assert.Single(dry.Maps);
        Assert.Equal(zone.Terrain!.Tiles.Count, dry.Maps[0].Tiles.Count);
        Assert.Null(dry.ChangeSet);
        Assert.Empty(CatalogTestData.FilesIn(Path.Combine(dir, "workspace")));
        Assert.Empty(target.Workspace.List());

        var res = UpdateImporter.Run(CatalogTestData.Source, cat, new[] { zone.Id }, target, dryRun: false);
        var cs = Assert.IsType<ChangeSet>(res.ChangeSet);
        Assert.Equal(res.Maps[0].Files.Count, cs.Files.Count);
        Assert.Equal(cs.Files.Count, target.Workspace.List().Count);
        Assert.Contains(target.Workspace.List(), v => v.Record.Path.EndsWith(".adt", StringComparison.OrdinalIgnoreCase));
        target.Workspace.RevertChangeSet(cs.Id);
        Assert.Empty(target.Workspace.List());
        Assert.Empty(CatalogTestData.FilesIn(target.Workspace.Paths.WorkspaceDir));
    }

    [CatalogFact]
    public void TexturePicksAndSeveralKindsImportAsOneChangeSet()
    {
        var cat = CatalogTestData.Catalog;
        var tex = cat.Items.Where(i => i.Id.StartsWith("tex:changed:", StringComparison.Ordinal) && i.Importable && i.Files is >= 3 and <= 60).OrderBy(i => i.Files).First();
        var art = cat.Items.Where(i => i.Kind == UpdateKind.InterfaceArt && i.Files is >= 2 and <= 40).OrderBy(i => i.Files).First();
        var light = cat.Items.First(i => i.Kind == UpdateKind.SkyLighting && !i.Importable);
        var target = CatalogTestData.NewTarget(out _);
        var res = UpdateImporter.Run(CatalogTestData.Source, cat, new[] { tex.Id, art.Id, "fonts:changed", light.Id }, target, dryRun: false);
        foreach (var l in res.Lines()) _out.WriteLine(l);
        var cs = Assert.IsType<ChangeSet>(res.ChangeSet);
        Assert.Equal(3, res.Imported.Count);
        Assert.Single(res.NotYet);
        Assert.Equal(light.Id, res.NotYet[0].Item.Id);
        Assert.Equal(res.Files.Count, cs.Files.Count);
        // every texture loads in 1.12; a changed texture replaces the game's file, new interface art is new
        foreach (var f in res.Files.Where(f => f.Path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)))
            Assert.Empty(BlpEncoder.Validate(target.Workspace.ReadEdited(f.Path)!));
        Assert.All(res.Files.Where(f => f.Item == tex.Id), f => Assert.False(f.IsNew));
        Assert.All(res.Files.Where(f => f.Item == art.Id), f => Assert.True(f.IsNew));
        var font = target.Workspace.ReadEdited(@"Fonts\FRIZQT__.TTF")!;
        Assert.Equal(CatalogTestData.Source.Casc.SizeOf(cat.Find("fonts:changed")!.FileList![0].FileDataId), font.LongLength);
        target.Workspace.RevertChangeSet(cs.Id);
        Assert.Empty(target.Workspace.List());
        // unknown IDs are refused in words
        Assert.Contains("no update", Assert.Throws<ArgumentException>(() => UpdateImporter.Run(CatalogTestData.Source, cat, new[] { "no:such:update" }, target, true)).Message);
    }

    private static CliRun Wds(string home, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.RunAsync(new[] { "--home", home, "--client", CatalogTestData.Client112Dir }.Concat(args).ToArray(), o, e, null).GetAwaiter().GetResult();
        return new CliRun(code, o.ToString(), e.ToString());
    }

    [CatalogFact]
    public void TheCommandLineListsShowsAndImportsTheSameUpdates()
    {
        var cat = CatalogTestData.Catalog;
        // an isolated home that already has the catalog (the same cache the Core call made)
        var home = TestEnv.TempDir("catalog_cli");
        foreach (var f in Directory.EnumerateFiles(CatalogTestData.CacheRoot, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(home, "cache", Path.GetRelativePath(CatalogTestData.CacheRoot, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        var list = Wds(home, "versions", "updates", "_classic_beta_", "--folder", CatalogTestData.Clients, "--limit", "0", "--json");
        CliTestEnv.CheckEnvelope(list);
        Assert.True(list.Code == 0, list.ToString());
        Assert.Equal(cat.Items.Count, list.Data.GetProperty("total").GetInt32());
        Assert.Equal(cat.Items.Count, list.Data.GetProperty("items").GetArrayLength());
        var terrain = Wds(home, "versions", "updates", "_classic_beta_", "--folder", CatalogTestData.Clients, "--kind", "zone-terrain", "--zone", "Elwynn", "--json");
        Assert.Contains(terrain.Data.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == "terrain:0:12");
        Assert.All(terrain.Data.GetProperty("items").EnumerateArray(), i => Assert.Equal("zone-terrain", i.GetProperty("kind").GetString()));
        var info = Wds(home, "versions", "update-info", "_classic_beta_", "fonts:changed", "--folder", CatalogTestData.Clients, "--json");
        Assert.True(info.Code == 0, info.ToString());
        Assert.Equal(3, info.Data.GetProperty("update").GetProperty("fileList").GetArrayLength());
        Assert.Equal(6, Wds(home, "versions", "update-info", "_classic_beta_", "no:such", "--folder", CatalogTestData.Clients).Code);
        Assert.Equal(2, Wds(home, "versions", "updates", "_classic_beta_", "--folder", CatalogTestData.Clients, "--kind", "nonsense").Code);
        // import: without --yes nothing is written (exit 5); with --yes one change set
        var ws = new AppPaths(home).ForClient(CatalogTestData.Client112Dir).WorkspaceDir;
        var noYes = Wds(home, "versions", "import-updates", "_classic_beta_", "fonts:changed", "--folder", CatalogTestData.Clients, "--json");
        Assert.Equal(5, noYes.Code);
        Assert.True(noYes.Data.GetProperty("dryRun").GetBoolean());
        Assert.Empty(CatalogTestData.FilesIn(ws));
        var yes = Wds(home, "versions", "import-updates", "_classic_beta_", "fonts:changed", "--folder", CatalogTestData.Clients, "--yes", "--json");
        Assert.True(yes.Code == 0, yes.ToString());
        Assert.Equal(3, yes.Data.GetProperty("changeSet").GetProperty("files").GetInt32());
        Assert.Equal(3, CatalogTestData.FilesIn(ws).Count);
        var w = new Workspace(new AppPaths(home).ForClient(CatalogTestData.Client112Dir));
        w.RevertChangeSet(yes.Data.GetProperty("changeSet").GetProperty("id").GetString()!);
        Assert.Empty(CatalogTestData.FilesIn(ws));
        // only updates that cannot be imported: refused, nothing written
        var engine = Wds(home, "versions", "import-updates", "_classic_beta_", "engine:ssao", "--folder", CatalogTestData.Clients, "--yes");
        Assert.Equal(4, engine.Code);
    }
}
