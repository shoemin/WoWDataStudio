using System.IO;
using System.Linq;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Index;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// M1 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md): another version opened read only, end to end on the synthetic
/// "1.60" client of <see cref="SyntheticClients"/> (our own bytes): the profile, the index (same schema, plus the
/// FileDataID, locked and named-creature tables), the names and their sources, the details panel, textures.
/// </summary>
public class OpenOtherVersionTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("openother");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (AppSession Session, ClientCopy Copy, string Listfile) Open(bool listfile = true, bool rebuild = false, string home = "home")
    {
        var install = Path.Combine(_root, "bnet");
        string flavor, lf;
        if (!Directory.Exists(install)) (flavor, lf) = WriteClassicBeta(install);
        else (flavor, lf) = (Path.Combine(install, "_classic_beta_"), Path.Combine(install, "test-listfile.csv"));
        var copy = ClientCopies.Inspect(flavor)!;
        var paths = new AppPaths(Path.Combine(_root, home));
        var s = new AppSession(paths, AppSettings.Load(paths));
        s.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = listfile ? lf : null, ForceRebuild = rebuild }, null).GetAwaiter().GetResult();
        return (s, copy, lf);
    }

    [Fact]
    public void TheSyntheticClientIsFoundAsACascClient()
    {
        var (flavor, _) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        var copy = ClientCopies.Inspect(flavor);
        Assert.NotNull(copy);
        Assert.Equal(ClientFormat.Casc, copy!.Format);
        using var st = CascStorage.Open(flavor);
        Assert.Equal((70450, "wow_classic_beta"), (st.Build, st.Product));
        var cat = Db2Catalog.ForStorage(st);
        Assert.Equal(Tables().Count, cat.Count);
        // every table matches our layout file (hash and column count) and keeps its encrypted rows apart
        var layouts = Db2Layouts.ForBuild(70450)!;
        foreach (var (name, (rows, locked)) in Tables())
        {
            var t = cat.Open(st, name)!;
            Assert.True(layouts.Apply(t, out var why), why);
            Assert.Equal(rows.Count, t.Records.Count);
            Assert.Equal(locked?.Count ?? 0, t.SkippedRecords);
        }
    }

    [Fact]
    public void OpensReadOnlyWithItsOwnIndexAndNoWorkspace()
    {
        var (s, copy, _) = Open();
        using (s)
        {
            Assert.True(s.IsReadOnly);
            Assert.NotNull(s.Profile);
            Assert.False(s.Profile!.IsOwnClient);
            Assert.Equal(GameCaps.UsedInGame | GameCaps.ModelExport, s.Profile.Caps);
            Assert.False(s.Profile.Has(GameCaps.Install) || s.Profile.Has(GameCaps.Server) || s.Profile.Has(GameCaps.HotReload) || s.Profile.Has(GameCaps.Designers) || s.Profile.Has(GameCaps.Noggit));
            Assert.False(s.Profile.Has(GameCaps.Edit));
            Assert.Equal((70450, "1.60.1"), (s.Profile.Build, s.Profile.Version));
            Assert.Equal("1.60.1.70450 is open read only: wds dbc set changes files. " + GameProfile.WriteReason, s.Profile.RefusedText("dbc set"));
            Assert.Equal("1.60.1.70450 is open read only. " + GameProfile.WriteReason, s.WriteRefusal);
            // none of the 1.12 parts
            Assert.Null(s.Data);
            Assert.Null(s.Workspace);
            Assert.Null(s.Textures);
            Assert.Null(s.Models);
            Assert.Null(s.Catalog);
            Assert.Null(s.Client);
            // its folder holds only the index
            var dir = s.OtherPaths!.Dir;
            Assert.Equal(new[] { "index.sqlite" }, Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToArray());
            Assert.True(s.Index!.IsModern);

            // the Browse list
            string[] keys = { "cdisplay:1", "cdisplay:2", "creature:50", "item:25", "idisplay:20", "go:60", "godisplay:70", "godisplay:71", "spell:80", "file:" + IconName };
            foreach (var k in keys) Assert.True(s.FindEntry(k) != null, k);
            Assert.Equal("Test Pet", s.FindEntry("creature:50")!.Name);
            Assert.Equal("Test Sword", s.FindEntry("item:25")!.Name);
            Assert.Equal("Test Sign", s.FindEntry("go:60")!.Name);
            Assert.Equal("TestCube", s.FindEntry("spell:80")!.Name);
            Assert.Equal("TestCube - TestCubeSkin, #" + LockedSkin, s.FindEntry("cdisplay:1")!.Name);
            // the creature table stays empty: the displays are listed
            Assert.Equal(2, s.Entries.Count(e => e.Key.StartsWith("cdisplay:", StringComparison.Ordinal)));

            // locked records are counted, never guessed
            var locked = s.Index.LockedCounts().ToDictionary(l => l.Table);
            Assert.Equal((2, 2), (locked["CreatureDisplayInfo"].Readable, locked["CreatureDisplayInfo"].Locked));
            Assert.Equal((1, 1), (locked["ItemSparse"].Readable, locked["ItemSparse"].Locked));
            Assert.Equal((1, 1), (locked["ItemDisplayInfo"].Readable, locked["ItemDisplayInfo"].Locked));
            Assert.Equal(0, locked["CreatureModelData"].Locked);
        }
    }

    [Fact]
    public void NamesComeFromTheClientTheInterfaceListAndTheListfile()
    {
        var (s, _, _) = Open();
        using (s)
        {
            var names = s.Files!.Names!;
            Assert.Equal(NameSource.Client, names.ByFileDataId(CubeModel)!.Source);        // listfile name the root's hash confirms
            Assert.Equal(CubeName, names.ByFileDataId(CubeModel)!.Path);
            Assert.Equal(NameSource.Manifest, names.ByFileDataId(IconTexture)!.Source);    // the client's own interface list
            Assert.Equal(NameSource.Listfile, names.ByFileDataId(SkinTexture)!.Source);    // the listfile only
            Assert.Equal(SkinName, names.ByFileDataId(SkinTexture)!.Path);                   // '/' read as '\'
            Assert.Equal(NameSource.Number, names.ByFileDataId(ModelTexture)!.Source);
            var counts = s.Index!.Counts();
            Assert.Equal(1, counts["names_client"]);
            Assert.Equal(1, counts["names_manifest"]);
            Assert.Equal(1, counts["names_listfile"]);
            Assert.Equal("1", s.Index.Meta()["listfile_other_clients"]);   // the line of another product is left out
            // a file without a name is #FileDataID with the extension of its role
            Assert.Equal("#" + LockedSkin + ".blp", names.ByFileDataId(LockedSkin)!.Path);
            Assert.Equal(CascFileState.Encrypted, names.ByFileDataId(LockedSkin)!.State);
            Assert.Equal(CascFileState.NotLocal, names.ByFileDataId(MissingTexture)!.State);
            // reading by name, by #id and by #id.ext
            Assert.NotNull(s.ReadOther(CubeName));
            Assert.NotNull(s.ReadOther("#" + ModelTexture));
            Assert.NotNull(s.ReadOther("#" + ModelTexture + ".blp"));
            Assert.Throws<ClientFileException>(() => s.ReadOther("#" + LockedSkin));
            Assert.Throws<ClientFileException>(() => s.ReadOther("#" + MissingTexture));
            Assert.Null(s.ReadCurrent("#" + LockedSkin));
            Assert.True(s.FileExists(IconName));
            Assert.Contains(names.Glob(@"Creature\TestCube\*"), f => f.FileDataId == SkinTexture);
        }
        // without the listfile the skin has no name and the model is known by its number
        var (s2, _, _) = Open(listfile: false, home: "home2");
        using (s2)
        {
            Assert.Equal(NameSource.Number, s2.Files!.Names!.ByFileDataId(SkinTexture)!.Source);
            Assert.Equal("#" + CubeModel + ".m2", s2.FindEntry("cdisplay:1")!.Path);
        }
    }

    [Fact]
    public void DetailsShowFileDataIdsNameSourcesAndLocks()
    {
        var (s, _, _) = Open();
        using (s)
        {
            var d = s.Details(s.FindEntry("cdisplay:1")!);
            Assert.Equal(new[] { "display 1", CubeName, "skins: TestCubeSkin, " + LockedSkin }, d.Chain.Select(c => c.Replace("#", "")));
            var model = d.Files.Single(f => f.Role == "model");
            Assert.Equal((CubeModel, NameSource.Client, "local"), (model.FileDataId, model.Source, model.State));
            var skin2 = d.Files.Single(f => f.Role == "skin2");
            Assert.Equal((LockedSkin, "encrypted"), (skin2.FileDataId, skin2.State));
            Assert.Contains(d.Facts, f => f.Contains("is encrypted (key 0807060504030201)", StringComparison.Ordinal));
            // the model's own textures (TXID), read on demand: the unnamed one and the run-time slot
            Assert.Contains(d.Textures, t => t.Path == "#" + ModelTexture + ".blp" && t.Role == "model texture" && t.Exists);
            Assert.Contains(d.Facts, f => f.StartsWith("1 texture is chosen by the game at run time", StringComparison.Ordinal));
            Assert.Contains(d.Facts, f => f.Contains("2 of 4 creature displays readable, 2 locked", StringComparison.Ordinal));

            var extra = s.Details(s.FindEntry("cdisplay:2")!);
            Assert.Contains(extra.Textures, t => t.Role == "baked skin" && t.Path == IconName);
            Assert.Contains(extra.Facts, f => f.Contains("race 2, female", StringComparison.Ordinal));

            var item = s.Details(s.FindEntry("item:25")!);
            Assert.Equal("Test Sword", item.Title);
            Assert.Contains(item.Files, f => f.FileDataId == CubeModel && f.Role.StartsWith("model1", StringComparison.Ordinal));
            Assert.Contains(item.Textures, t => t.Role == "model texture1" && t.Path == SkinName);
            Assert.Contains(item.Textures, t => t.Role == "TorsoUpper texture_U" && t.Path == IconName);
            Assert.Contains(item.Textures, t => t.Role == "icon" && t.Path == "#" + HdBake + ".blp");

            var go = s.Details(s.FindEntry("godisplay:71")!);
            Assert.Contains(go.Facts, f => f.Contains("is not on this computer", StringComparison.Ordinal));

            var spell = s.Details(s.FindEntry("spell:80")!);
            Assert.Equal(new[] { "Test Bolt - spell 133, kit" }, spell.UsedBy);

            var tex = s.Details(s.FindEntry("file:" + IconName)!);
            Assert.Equal(IconTexture, tex.Files.Single().FileDataId);
            Assert.Equal(NameSource.Manifest, tex.Files.Single().Source);
            Assert.Contains(tex.UsedBy, u => u.StartsWith("item display 20", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TexturesExportAsPngWithoutASidecar()
    {
        var (s, _, _) = Open();
        using (s)
        {
            var png = Path.Combine(_root, "out", OtherTextures.PngName("#" + ModelTexture + ".blp"));
            var blp = OtherTextures.ExportPng(s, "#" + ModelTexture, png);
            Assert.Equal((16, 16), (blp.Width, blp.Height));
            var img = WoWData.Core.Imaging.Png.Load(png);
            Assert.Equal((16, 16), (img.Width, img.Height));
            Assert.InRange(img.Pixels[2], 190, 210);   // the blue one
            Assert.False(File.Exists(TextureService.SidecarFor(png)));
            Assert.Equal(ModelTexture + ".png", Path.GetFileName(png));
            Assert.Throws<ClientFileException>(() => OtherTextures.ExportPng(s, "#" + LockedSkin, Path.Combine(_root, "out", "locked.png")));
        }
    }

    [Fact]
    public void TheIndexIsReusedUntilAnInputChanges()
    {
        var (s, _, lf) = Open();
        string built;
        using (s) built = s.Index!.Meta()["built"];
        Thread.Sleep(1100);
        var (s2, _, _) = Open();
        using (s2) Assert.Equal(built, s2.Index!.Meta()["built"]);
        // another listfile: built again
        File.AppendAllText(lf, "#\n");
        var (s3, _, _) = Open();
        using (s3) Assert.NotEqual(built, s3.Index!.Meta()["built"]);
    }

    [Fact]
    public void ModelTextureIdsComeFromTheTxidChunk()
    {
        Assert.Equal(new uint[] { 7, 0, 9 }, Catalog160.TextureIds(Md21(7, 0, 9)));
        Assert.Empty(Catalog160.TextureIds(new byte[] { (byte)'M', (byte)'D', (byte)'2', (byte)'0', 0, 1, 0, 0 }));
    }

    [Fact]
    public void ANameOfYour112GameIsFoundThroughClassicEra()
    {
        // two stores sharing FileDataIDs: "1.60" without a name hash for 1000100, "Era" with one
        var beta = WriteStore(Path.Combine(_root, "beta"), "wow_classic_beta", "1.60.1.70450", "_classic_beta_", "WowB.exe", Array.Empty<string>(),
            new[] { new StoreFile(1000100, null, Blp(1, 2, 3)), new StoreFile(1000101, @"World\Own\Hashed.blp", Blp(4, 5, 6)) });
        var era = WriteStore(Path.Combine(_root, "era"), "wow_classic_era", "1.15.9.70003", "_classic_era_", "WowClassic.exe", Array.Empty<string>(),
            new[] { new StoreFile(1000100, @"World\Old\FromEra.blp", Blp(1, 2, 3)) });
        using var b = CascStorage.Open(beta);
        using var e = CascStorage.Open(era);
        var names = GameNames.Build(b, new GameNamesInput
        {
            Names112 = new[] { @"World\Old\FromEra.blp", @"World\Own\Hashed.blp", @"World\Nowhere\Missing.blp" }, Era = e,
        });
        Assert.Equal((@"World\Old\FromEra.blp", NameSource.Era), (names.NameOf(1000100), names.SourceOf(1000100)));
        Assert.Equal((@"World\Own\Hashed.blp", NameSource.Client), (names.NameOf(1000101), names.SourceOf(1000101)));
        Assert.Equal(2, names.Count);
    }
}
