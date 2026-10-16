using System.Diagnostics;
using System.IO;
using System.Linq;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.Usage;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.6.1, from the in-game visual test (tests\ingame\results\20261010_2254): with terrain specular on (Config.wtf
/// specular 1 + pixelShaders 1) the game draws &lt;name&gt;_s.blp instead of the ground texture the map names
/// (CMapArea_LoadTextureNames @006c20e0). Both files everywhere (breakdown, Used in game, search, details, previews,
/// terrain export), "edit both" with the shine map kept; the pixel-change figures count colour and alpha separately; the
/// model import's invisible-colour rule follows the material's blend mode; the additive advice; uninstalling the
/// release patch; --render showing the workspace.
/// </summary>
public class GroundTextureTests
{
    public const string Dirt = @"Tileset\Durotar\DurotarDirt.blp";
    public const string DirtS = @"Tileset\Durotar\DurotarDirt_s.blp";
    private readonly ITestOutputHelper _out;
    public GroundTextureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheClientsNameRuleAndTheConfigSettings()
    {
        Assert.Equal(DirtS, GroundTextures.SpecularName(Dirt));
        Assert.Equal(@"Tileset\A.B\x_s.blp", GroundTextures.SpecularName(@"Tileset\A.B\x.blp"));      // the last '.'
        Assert.Null(GroundTextures.SpecularName(@"Tileset\noext"));
        Assert.Equal(Dirt, GroundTextures.BaseNameOfSpecular(DirtS));
        Assert.Null(GroundTextures.BaseNameOfSpecular(Dirt));
        var on = TerrainSpecularSettings.Parse(new[] { "SET gxResolution \"1600x900\"", "SET specular \"1\"", "set pixelShaders \"1\"" });
        Assert.True(on.UsesSpecularTextures);
        Assert.Contains("specular 1, pixelShaders 1", on.Describe());
        Assert.False(TerrainSpecularSettings.Parse(new[] { "SET specular \"1\"" }).UsesSpecularTextures);      // pixel shaders default to 0
        Assert.False(TerrainSpecularSettings.Parse(new[] { "SET specular \"0\"", "SET pixelShaders \"1\"" }).UsesSpecularTextures);
        Assert.False(TerrainSpecularSettings.Read(Path.Combine(Path.GetTempPath(), "no_such_client_" + Guid.NewGuid().ToString("N"))).FileFound);
        Assert.True(GroundTextures.SpecularOn(GroundTextureMode.AsTheGame, on));
        Assert.False(GroundTextures.SpecularOn(GroundTextureMode.Base, on));
        Assert.True(GroundTextures.SpecularOn(GroundTextureMode.Specular, TerrainSpecularSettings.Unknown));
        // the pair from either file
        var pair = GroundTextures.PairOf(DirtS, TestEnv.Client.Exists)!;
        Assert.Equal((Dirt, DirtS), (pair.Base, pair.Specular));
        Assert.Equal(DirtS, pair.Partner(Dirt));
        Assert.Equal(Dirt, pair.Partner(DirtS));
        Assert.Equal(DirtS, pair.Drawn(true));
        Assert.Equal(Dirt, pair.Drawn(false));
        Assert.Contains("draws DurotarDirt_s.blp", GroundTextures.WhichIsDrawn(pair, on));
        Assert.Null(GroundTextures.PairOf(@"Creature\Boar\BoarSkinIvory.blp", TestEnv.Client.Exists));
    }

    private static (MapInfo Map, int X, int Y) DirtTile()
    {
        var map = TerrainBreakdownTests.Map("Kalimdor");
        foreach (var (x, y) in new[] { (39, 33), (40, 33), (39, 32), (40, 32), (40, 31) })
        {
            var t = TerrainTile.Load(TestEnv.Client.Read(map.TilePath(x, y))!, map.Directory, x, y);
            if (t.Textures.Any(p => string.Equals(WoWData.Core.Mpq.ClientData.ResolveName(p), Dirt, StringComparison.OrdinalIgnoreCase))) return (map, x, y);
        }
        throw new InvalidOperationException("no Durotar tile with DurotarDirt.blp");
    }

    [Fact]
    public void TileBreakdownListsBothFiles()
    {
        var (map, x, y) = DirtTile();
        var bd = TileBreakdown.Build(map.Name, map.Directory, x, y, TestEnv.Client.Read(map.TilePath(x, y))!, TestEnv.Client.Read, TerrainBreakdownTests.Catalog.AreaName, null, TestEnv.Client.Exists);
        var item = bd.Group("textures").Items.Single(i => i.Path == Dirt);
        Assert.Equal(DirtS, item.SpecularPath);
        Assert.Contains("DurotarDirt_s.blp", item.Title);
        Assert.Contains("terrain specular is on", item.Detail);
        Assert.Equal(2, item.Children.Count);
        var s = item.Children.Single(c => c.IsSpecularFile);
        Assert.Equal((DirtS, Dirt, "file:" + DirtS), (s.Path, s.LayerPath, s.BrowseKey));
        Assert.Contains("alpha channel is the shine", s.Detail);
        Assert.Equal(item.Chunks, s.Chunks);
        // the highlight of the _s entry lights the ground painted with the map's texture
        Assert.Equal(Dirt, WoWDataStudio.ViewModels.TileBreakdownViewModel.HighlightOf(s)!.Texture);
        // without the exists lookup the breakdown is as before (one entry per MTEX name)
        var plain = TileBreakdown.Build(map.Name, map.Directory, x, y, TestEnv.Client.Read(map.TilePath(x, y))!, TestEnv.Client.Read, TerrainBreakdownTests.Catalog.AreaName);
        Assert.Empty(plain.Group("textures").Items.Single(i => i.Path == Dirt).Children);
    }

    [Fact]
    public void TerrainPreviewAndBlenderExportFollowTheRule()
    {
        var (map, x, y) = DirtTile();
        var tile = TerrainTile.Load(TestEnv.Client.Read(map.TilePath(x, y))!, map.Directory, x, y);
        RgbaImage? Load(string p) => TestEnv.Client.Read(p) is { } d ? BlpFile.Parse(d).Decode(0) : null;
        var plain = TerrainAsset.Build(new[] { tile }, TestEnv.Client.Read, Load, new TerrainBuildOptions(false, false));
        var spec = TerrainAsset.Build(new[] { tile }, TestEnv.Client.Read, Load, new TerrainBuildOptions(false, false, SpecularGround: true, Exists: TestEnv.Client.Exists));
        int i = plain.TexturePaths.FindIndex(p => string.Equals(WoWData.Core.Mpq.ClientData.ResolveName(p), Dirt, StringComparison.OrdinalIgnoreCase));
        Assert.True(i >= 0);
        Assert.Equal(plain.TexturePaths, spec.TexturePaths);                         // the layers are still the map's names
        Assert.Equal(Dirt, WoWData.Core.Mpq.ClientData.ResolveName(plain.DrawnTexturePaths[i]));
        Assert.Equal(DirtS, WoWData.Core.Mpq.ClientData.ResolveName(spec.DrawnTexturePaths[i]));
        Assert.Contains("_s files", spec.Summary);
        Assert.False(spec.Textures[i]!.SameContent(Load(Dirt)!));                    // the _s pixels (its alpha differs)

        var root = TestEnv.TempDir("groundpkg");
        try
        {
            TerrainExportResult Export(bool specular, string folder) => TerrainExporter.Export(new TerrainExportRequest
            {
                Map = map, Tiles = new List<(int, int)> { (x, y) }, Center = (x, y), ReadCurrent = TestEnv.Client.Read, ReadGame = TestEnv.Client.Read,
                ArchiveOf = p => TestEnv.Client.Find(p)?.Name, PackageFolder = Path.Combine(root, folder), LoadTexture = Load,
                Exists = TestEnv.Client.Exists, SpecularGround = specular,
            });
            var a = Export(true, "spec");
            var t = a.Manifest.Textures.Single(t => t.GamePath == Dirt);
            Assert.Equal(DirtS, t.SpecularGamePath);
            Assert.Equal(("specular", t.SpecularPng), (t.Drawn, t.Png));
            Assert.True(File.Exists(Path.Combine(a.Folder, t.BasePng!)) && File.Exists(Path.Combine(a.Folder, t.SpecularPng!)));
            Assert.Contains("_s", Path.GetFileName(t.Png));
            var b = Export(false, "base");
            var tb = b.Manifest.Textures.Single(t => t.GamePath == Dirt);
            Assert.Equal(("base", tb.BasePng), (tb.Drawn, tb.Png));
            Assert.NotNull(tb.SpecularPng);                                               // both files are always written
            Assert.Contains("\"specular_png\"", File.ReadAllText(b.ManifestPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>"Edit both": a blue DurotarDirt.blp makes DurotarDirt_s.blp blue too, with its shine map (alpha) unchanged;
    /// both are saved as one change set. Then an edit of the _s file whose PNG lost the alpha keeps the shine map.</summary>
    [Fact]
    public void EditBothKeepsTheShineMap()
    {
        var root = TestEnv.TempDir("editboth");
        try
        {
            var ws = TerrainBreakdownTests.FakeWorkspace(root);
            var ts = new TextureService(TestEnv.Client, ws);
            var png = Path.Combine(root, "DurotarDirt.png");
            ts.Export(Dirt, png);
            var img = Png.Load(png);
            for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = 30; img.Pixels[i + 1] = 80; img.Pixels[i + 2] = 240; }
            Png.Save(img, png, false);
            var plan = ts.Plan(png, null);
            Assert.InRange(plan.Diff!.ColourChangedPercent, 99, 100);
            var partner = ts.PlanPartner(plan, DirtS);
            var origS = BlpFile.Parse(TestEnv.Client.Read(DirtS)!).Decode(0);
            var newS = partner.EditedImage;
            Assert.Equal(partner.Original!.FormatName, partner.EncodedBlp.FormatName);
            int maxAlpha = 0, blue = 0;
            for (int i = 0; i < newS.Pixels.Length; i += 4)
            {
                maxAlpha = Math.Max(maxAlpha, Math.Abs(newS.Pixels[i + 3] - origS.Pixels[i + 3]));
                if (newS.Pixels[i + 2] > 200 && newS.Pixels[i] < 60) blue++;
            }
            _out.WriteLine($"_s: largest alpha difference {maxAlpha}, blue pixels {blue} of {newS.Width * newS.Height}; {partner.Diff!.Summary}");
            Assert.True(maxAlpha <= 4, $"the shine map changed by up to {maxAlpha}");
            Assert.True(blue > newS.Width * newS.Height * 95 / 100);
            Assert.InRange(partner.Diff.ColourChangedPercent, 99, 100);             // 0.6.0 said about 17 % here
            Assert.Contains(partner.Notes, n => n.Contains("shine"));
            var recs = ts.Commit(new[] { plan, partner }, null, "ground edit");
            Assert.Equal(2, recs.Count);
            Assert.Single(recs.Select(r => r.ChangeSet).Distinct());
            Assert.True(ws.IsModified(Dirt) && ws.IsModified(DirtS));

            // an edit of the _s file from an editor that dropped the alpha: merged with the current shine map it keeps it
            var flat = BlpFile.Parse(ws.ReadEdited(DirtS)!).Decode(0);
            for (int i = 3; i < flat.Pixels.Length; i += 4) flat.Pixels[i] = 255;
            Assert.True(GroundTextures.AlphaDiffers(flat, origS));
            var kept = GroundTextures.MergeIntoPartner(flat, BlpFile.Parse(ws.ReadEdited(DirtS)!).Decode(0), keepPartnerAlpha: true);
            Assert.False(GroundTextures.AlphaDiffers(kept, BlpFile.Parse(ws.ReadEdited(DirtS)!).Decode(0)));
            var mine = GroundTextures.MergeIntoPartner(flat, origS, keepPartnerAlpha: false);
            Assert.True(mine.IsOpaque());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>The model import must not drop an edit on an opaque material whose texture has alpha 0 in places
    /// (Blender's rewrite of invisible colour is only ignored where every material uses the alpha as transparency).</summary>
    [Fact]
    public void ModelImportDecidesInvisibleColourByTheBlendMode()
    {
        var exported = new RgbaImage(8, 8);
        for (int i = 0; i < exported.Pixels.Length; i += 4) { exported.Pixels[i] = 90; exported.Pixels[i + 1] = 60; exported.Pixels[i + 2] = 30; exported.Pixels[i + 3] = 255; }
        for (int i = 0; i < 16 * 4; i += 4) exported.Pixels[i + 3] = 0;               // alpha 0 in the first two rows
        var edited = exported.Clone();
        for (int i = 0; i < 16 * 4; i += 4) edited.Pixels[i + 2] = 250;              // only colour under alpha 0 changed
        var tex = new PackageTexture { Id = "t0", GamePath = @"World\test.blp" };
        ModelPackageManifest Man(int blend) => new() { Materials = { new PackageMaterial { Id = "m0", Name = "Bark", Texture = "t0", Blend = blend } } };
        var (sameOpaque, ruleOpaque) = ModelImportAnalyzer.CompareWithExport(exported, edited, Man(0), tex);
        Assert.False(sameOpaque);
        Assert.Contains("Bark draws this texture opaque", ruleOpaque!.Text);
        var (sameKey, ruleKey) = ModelImportAnalyzer.CompareWithExport(exported, edited, Man(1), tex);
        Assert.True(sameKey);
        Assert.Contains("not counted as a change", ruleKey!.Text);
        Assert.True(ModelImportAnalyzer.CompareWithExport(exported, exported.Clone(), Man(0), tex).Unchanged);
        // a visible edit is a change whatever the blend mode
        var visible = exported.Clone();
        visible.Pixels[visible.Offset(5, 5)] = 0;
        Assert.False(ModelImportAnalyzer.CompareWithExport(exported, visible, Man(1), tex).Unchanged);
    }

    [Fact]
    public void BlendModesOfRealModelsAndBuildings()
    {
        // the Shadow Bolt missile: its glow is drawn additively (the in-game test's tinted square)
        var missile = TestEnv.Client.Read(@"Spells\DeathCoil_Missile.m2")!;
        var tex = new M2Header(missile).Textures().Where(t => t.Type == 0).Select(t => t.FileName).ToList();
        _out.WriteLine(string.Join(", ", tex));
        var uses = tex.SelectMany(t => MaterialBlends.Find(@"Spells\DeathCoil_Missile.m2", missile, t)).ToList();
        foreach (var u in uses) _out.WriteLine($"{u.Where}: {u.BlendName} ({u.Kind})");
        Assert.Contains(uses, u => u.Kind == BlendKind.Additive);
        var note = MaterialBlends.AdditiveNote(uses)!;
        Assert.StartsWith("This texture is drawn additively: black is invisible, bright colours glow. Tinting the black background will show as a visible square.", note);
        // a creature skin is drawn opaque: no note
        var boar = TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!;
        var skin = MaterialBlends.Find(@"Creature\Boar\Boar.m2", boar, @"Creature\Boar\BoarSkinIvory.blp");
        Assert.Contains(skin, u => u.Kind == BlendKind.Opaque);
        Assert.Null(MaterialBlends.AdditiveNote(skin));
        // keep black areas black: black stays black, the bright part takes the tint
        var orig = new RgbaImage(2, 1);
        orig.Pixels[3] = orig.Pixels[7] = 255;
        orig.Pixels[4] = orig.Pixels[5] = orig.Pixels[6] = 200;
        var tint = orig.Clone();
        for (int i = 0; i < 8; i += 4) { tint.Pixels[i] = Math.Max(tint.Pixels[i], (byte)40); tint.Pixels[i + 1] = 255; }
        Assert.Equal(1, MaterialBlends.LiftedBlackPixels(orig, tint));
        var kept = MaterialBlends.KeepBlackBlack(orig, tint);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, kept.Pixels[..4]);
        Assert.Equal(255, kept.Pixels[5]);
    }

    /// <summary>Uninstall release patch: only a patch the app built is removed without asking; the patch that was there
    /// before is put back; a foreign one is moved to the backup folder only when confirmed (never deleted).</summary>
    [Fact]
    public void UninstallReleasePatchRestoresThePreviousOne()
    {
        var root = TestEnv.TempDir("unpatch");
        try
        {
            var ws = TerrainBreakdownTests.FakeWorkspace(root);
            var dest = Path.Combine(ws.Paths.DataDir, "patch-3.MPQ");
            Assert.Equal(PatchOwner.None, ws.GetPatchState().Owner);
            // someone else's patch-3.MPQ is there first
            File.WriteAllBytes(dest, new byte[] { 1, 2, 3, 4 });
            Assert.Equal(PatchOwner.Foreign, ws.GetPatchState().Owner);
            Assert.Throws<WorkspaceException>(() => ws.UninstallPatch());
            Assert.True(File.Exists(dest));
            // an edit, built and installed: ours, and the foreign one is the one to put back
            var boar = BlpFile.Parse(TestEnv.Client.Read(@"Creature\Boar\BoarSkinIvory.blp")!);
            var img = boar.Decode(0);
            img.Pixels[0] ^= 0xFF;
            ws.SaveEdit(@"Creature\Boar\BoarSkinIvory.blp", BlpEncoder.EncodeLike(boar, img).Data, new OriginInfo(), ws.BeginChangeSet("t"));
            var b1 = ws.BuildPatch(install: true);
            var st = ws.GetPatchState();
            Assert.Equal(PatchOwner.Ours, st.Owner);
            Assert.StartsWith("A release patch built by WoW Data Studio is installed: 1 file, built", st.Text);
            Assert.Equal(b1.Backup, st.RestoreBackup);
            // a second build replaces our own: the one to put back is still the foreign original
            var b2 = ws.BuildPatch(install: true);
            Assert.Equal(b1.Backup, ws.GetPatchState().RestoreBackup);
            Assert.Equal(b1.Backup, b2.OriginalBackup);
            var done = ws.UninstallPatch();
            _out.WriteLine(string.Join("\n", done));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(dest));            // the previous patch is back
            Assert.Equal(PatchOwner.Foreign, ws.GetPatchState().Owner);
            Assert.All(ws.List(), v => Assert.False(v.Packed));
            // the foreign one is only moved out when the user agreed, into the backup folder
            var moved = ws.UninstallPatch(confirmedNotOurs: true);
            Assert.False(File.Exists(dest));
            Assert.Contains(moved, m => m.Contains("not deleted"));
            Assert.Single(Directory.GetFiles(ws.Paths.BackupDir, "patch-3.MPQ.removed_*.bak"));
            // no patch before: uninstalling just removes ours
            ws.BuildPatch(install: true);
            Assert.Null(ws.GetPatchState().RestoreBackup);
            ws.UninstallPatch();
            Assert.False(File.Exists(dest));
            Assert.Equal(PatchOwner.None, ws.GetPatchState().Owner);
            // a 0.6.0 record (no checksum) whose build file no longer matches: "probably ours", only moved with consent
            ws.BuildPatch(install: true);
            var last = ws.Manifest.PatchBuilds.Last();
            last.Sha256 = null;
            File.WriteAllBytes(last.Path, new byte[] { 9 });
            Assert.Equal(PatchOwner.OursUnverified, ws.GetPatchState().Owner);
            Assert.Throws<WorkspaceException>(() => ws.UninstallPatch());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}

/// <summary>The index side of the ground textures (the shared index of the run).</summary>
[Collection(IndexCollection.Name)]
public class GroundTextureIndexTests
{
    private readonly IndexFixture _f;
    private readonly ITestOutputHelper _out;

    public GroundTextureIndexTests(IndexCollection.Handle h, ITestOutputHelper output)
    {
        _f = h.Fixture;
        _out = output;
    }

    [Fact]
    public void SearchDetailsAndUsedInGameShowBothFiles()
    {
        Assert.True(_f.Index.IsGroundTexture(GroundTextureTests.Dirt));
        Assert.False(_f.Index.IsGroundTexture(GroundTextureTests.DirtS));                 // the map names the base only
        var pair = _f.Index.GroundPair(GroundTextureTests.DirtS)!;
        Assert.Equal((GroundTextureTests.Dirt, GroundTextureTests.DirtS), (pair.Base, pair.Specular));
        Assert.True(_f.Result.Counts["u_ground_texture"] > 10000, $"{_f.Result.Counts["u_ground_texture"]} ground texture uses");
        // the search list says what each file is
        var b = _f.Entries.Single(e => e.Key == "file:" + GroundTextureTests.Dirt);
        var s = _f.Entries.Single(e => e.Key == "file:" + GroundTextureTests.DirtS);
        Assert.StartsWith("ground texture (with terrain specular on the game draws DurotarDirt_s.blp instead)", b.Info);
        Assert.StartsWith("ground texture, specular variant of DurotarDirt.blp", s.Info);
        // details: both files in the texture list, from either entry
        foreach (var e in new[] { b, s })
        {
            var d = _f.Index.Details(e, null);
            Assert.Equal(new[] { GroundTextureTests.Dirt, GroundTextureTests.DirtS }, d.Textures.Select(t => t.Path));
            Assert.Contains(d.Facts, f => f.Contains("draws DurotarDirt_s.blp when terrain specular is on"));
            Assert.Contains(d.Facts, f => f.StartsWith("Ground texture of ") && f.Contains("Kalimdor"));
        }
        // Used in game: the ground section on both, each linking the other file
        using var svc = new UsageService(_f.Path);
        foreach (var (path, other) in new[] { (GroundTextureTests.Dirt, GroundTextureTests.DirtS), (GroundTextureTests.DirtS, GroundTextureTests.Dirt) })
        {
            var r = svc.Report("file:" + path);
            var g = r.Section("ground")!;
            _out.WriteLine($"{path}: {g.Summary} | {r.Section("affects")!.Summary}");
            Assert.StartsWith("Painted on ", g.Summary);
            Assert.Contains(g.Lines, l => l.Text.Contains("Durotar"));
            Assert.Contains(g.Tail, l => l.Spans.Any(x => x.Key == "file:" + other));
            Assert.Contains("the ground of", r.Section("affects")!.Summary);
        }
        // how many ground textures the maps use without an _s file (the game has nothing to draw there with specular on)
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_f.Path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT DISTINCT texture FROM u_ground_texture";
        var used = new List<string>();
        using (var rd = c.ExecuteReader()) while (rd.Read()) used.Add(rd.GetString(0));
        var noSpec = used.Where(t => GroundTextures.SpecularName(t) is { } sn && !TestEnv.Client.Exists(sn)).ToList();
        _out.WriteLine($"{used.Count} ground textures used by maps; {noSpec.Count} without an _s file: {string.Join(", ", noSpec.Take(20))}");
    }
}

/// <summary>`--render` shows the workspace version (0.6.1); --original the game's files.</summary>
public class RenderCliTests
{
    [Fact]
    public void RenderShowsTheWorkspaceVersionUnlessOriginal()
    {
        var exe = UiSmokeTests.FindExe() ?? throw new InvalidOperationException("WoWDataStudio.exe not built");
        var root = TestEnv.TempDir("render");
        var before = TestEnv.DataSnapshot();
        try
        {
            var home = Path.Combine(root, "home");
            var ws = new Workspace(new AppPaths(home).ForClient(TestEnv.ClientDir));
            const string skin = @"Creature\Boar\BoarSkinIvory.blp";
            var orig = BlpFile.Parse(TestEnv.Client.Read(skin)!);
            var img = orig.Decode(0);
            for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = 255; img.Pixels[i + 1] = 0; img.Pixels[i + 2] = 255; }
            ws.SaveEdit(skin, BlpEncoder.EncodeLike(orig, img).Data, new OriginInfo(), ws.BeginChangeSet("magenta boar"));
            string Render(string name, bool original)
            {
                var png = Path.Combine(root, name + ".png");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in new[] { "--client", TestEnv.ClientDir, "--home", home, "--render", @"Creature\Boar\Boar.m2", png, "--display", "503", "--size", "320x240" }) psi.ArgumentList.Add(a);
                if (original) psi.ArgumentList.Add("--original");
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEndAsync();
                Assert.True(p.WaitForExit(120_000), "render timed out");
                Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {p.StandardError.ReadToEnd()}");
                Assert.True(File.Exists(png));
                return o.Result;
            }
            var o1 = Render("ws", false);
            var o2 = Render("orig", true);
            Assert.Contains("workspace", o1);
            Assert.Contains("--original", o2);
            int Magenta(string f)
            {
                var im = Png.Load(f);
                int n = 0;
                for (int i = 0; i < im.Pixels.Length; i += 4) if (im.Pixels[i] > 150 && im.Pixels[i + 1] < 90 && im.Pixels[i + 2] > 150) n++;
                return n;
            }
            int mw = Magenta(Path.Combine(root, "ws.png")), mo = Magenta(Path.Combine(root, "orig.png"));
            Assert.True(mw > 2000 && mo < mw / 10, $"magenta pixels: workspace {mw}, original {mo}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Assert.Equal(before, TestEnv.DataSnapshot());
    }
}
