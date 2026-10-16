using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// The 3D viewer's data side: model looks (character composites, geosets, creature skins) against the Python
/// reference (wowdata/modelctx.py), the render-state tables from the decompile, load performance, and the encrypted
/// server password in settings.json. The rendering itself is covered by the UI smoke test (offscreen renders of the
/// boar, the orc male, a tree, an item, a spell effect and the compare view).
/// </summary>
public class ModelViewerTests
{
    private readonly ITestOutputHelper _out;
    public ModelViewerTests(ITestOutputHelper output) { _out = output; }

    private static readonly Lazy<Catalog> Cat = new(() => new Catalog(TestEnv.Client));

    [Fact]
    public void CharacterLooksAndCreatureSkinsEqualPython()
    {
        var dir = Oracle.Ensure("modelctx");
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "looks.json")))!;
        var res = new ModelResolver(Cat.Value);
        foreach (var row in root["looks"]!.AsArray())
        {
            int race = row!["race"]!.GetValue<int>(), sex = row["sex"]!.GetValue<int>();
            var l = row["look"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            var look = new CharacterLook(l[0], l[1], l[2], l[3], l[4]);
            var ml = res.Character(race, sex, look);
            Assert.Equal(row["geosets"]!.AsArray().Select(x => x!.GetValue<int>()).OrderBy(x => x), ml.Geosets!.OrderBy(x => x));
            Assert.Equal(row["hair"]?.GetValue<string>(), ml.Slots.TryGetValue(6, out var hair) ? hair.Path : null, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(row["extra"]?.GetValue<string>(), ml.Slots.TryGetValue(8, out var extra) ? extra.Path : null, StringComparer.OrdinalIgnoreCase);
            if (row["body"] is { } bodyFile)
            {
                var expect = File.ReadAllBytes(Path.Combine(dir, bodyFile.GetValue<string>()));
                var body = ml.Slots[1].Image;
                Assert.Equal(row["size"]![0]!.GetValue<int>(), body.Width);
                Assert.Equal(expect.Length, body.Pixels.Length);
                // the layers are pasted with the same alpha blend; only resized layers (none in 1.12's own textures) could differ
                int maxDiff = 0;
                long sum = 0;
                for (int i = 0; i < expect.Length; i++)
                {
                    int d = Math.Abs(expect[i] - body.Pixels[i]);
                    maxDiff = Math.Max(maxDiff, d);
                    sum += d;
                }
                _out.WriteLine($"race {race} sex {sex} {look}: body max difference {maxDiff}, mean {sum / (double)expect.Length:0.0000}");
                Assert.True(maxDiff <= 1, $"race {race} sex {sex} {look}: composite differs by up to {maxDiff}");
                Assert.Equal(row["source"]!.GetValue<string>(), ml.Slots[1].Source);
            }
        }
        foreach (var d in root["displays"]!.AsArray())
        {
            int id = d!["display"]!.GetValue<int>();
            var ml = res.CreatureDisplay(id);
            foreach (var (slot, path) in d["slots"]!.AsObject())
                Assert.Equal(path!.GetValue<string>(), ml.Slots[int.Parse(slot)].Path, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CharacterOptionsAndGeosetRules()
    {
        var res = new ModelResolver(Cat.Value);
        var o = res.Options(2, 0);                          // orc male, as in character creation
        Assert.True(o.SkinColors.Count >= 5 && o.HairStyles.Count >= 5 && o.FacialHairs.Count >= 5);
        Assert.All(o.HairStyles, s => Assert.True(o.HairColorsByStyle[s].Count > 0));
        var g = res.CharacterGeosets(2, 0, 0, 0);
        Assert.Contains(0, g);
        Assert.Contains(702, g);                            // ears
        Assert.Contains(1, g);                              // hair style 0 has no CharHairGeosets geoset -> 1
        Assert.DoesNotContain(101, g);                      // facial hair 0 of the orc: no beard (100 + 0)
        Assert.Equal((2, 0), res.RaceOfModel(@"Character\Orc\Male\OrcMale.m2"));
        Assert.Null(res.RaceOfModel(@"Creature\Boar\Boar.m2"));
        Assert.Equal("Stand", res.AnimationName(0));
        Assert.Equal("Run", res.AnimationName(5));
    }

    [Fact]
    public void RenderStateFollowsTheDecompileTables()
    {
        // g_M2BlendToGxBlendTable {0,1,2,10,3,4,5} -> g_GxGlBlendSrcByMode / g_GxGlBlendDstByMode
        Assert.False(ModelRenderer.BlendFactors(0).Enable);
        Assert.False(ModelRenderer.BlendFactors(1).Enable);
        Assert.Equal((true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha), ModelRenderer.BlendFactors(2));
        Assert.Equal((true, BlendingFactor.One, BlendingFactor.One), ModelRenderer.BlendFactors(3));
        Assert.Equal((true, BlendingFactor.SrcAlpha, BlendingFactor.One), ModelRenderer.BlendFactors(4));
        Assert.Equal((true, BlendingFactor.DstColor, BlendingFactor.Zero), ModelRenderer.BlendFactors(5));
        Assert.Equal((true, BlendingFactor.DstColor, BlendingFactor.SrcColor), ModelRenderer.BlendFactors(6));
        // render flags
        var f = new M2RenderFlags(0x1 | 0x4 | 0x10, 2);
        Assert.True(f.Unlit && f.TwoSided && f.NoDepthWrite && !f.NoDepthTest && !f.Unfogged);
    }

    [Fact]
    public void BatchesCoverEveryTextureUnitAndEnvironmentMapsAreFlagged()
    {
        var res = new ModelResolver(Cat.Value);
        var axe = ModelAsset.Build(@"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01.m2", TestEnv.Client.Read(@"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01.m2")!, res.LoadBlp);
        Assert.Equal(axe.Model.Views[0].TexUnits.Length, axe.Views[0].Batches.Count);
        var orc = ModelAsset.Build(@"Character\Orc\Male\OrcMale.m2", TestEnv.Client.Read(@"Character\Orc\Male\OrcMale.m2")!, res.LoadBlp);
        Assert.Equal(4, orc.Views.Length);
        // 1.12's four views keep every triangle; they split the mesh into submeshes for bone palettes of 256/75/53/21
        Assert.Equal(new[] { 256, 75, 53, 21 }, orc.Views.Select(v => v.BoneLimit).ToArray());
        Assert.All(orc.Views, v => Assert.Equal(orc.Views[0].TriangleCount, v.TriangleCount));
        Assert.True(orc.Model.Views[3].Submeshes.Length > orc.Model.Views[0].Submeshes.Length);
        Assert.Equal(41, orc.GeosetIds.Length);
        // tex-unit lookup -1 (as uint16 >= 3) means sphere environment mapping (M2Scene_SetBatchTextureStages)
        int env = 0, total = 0;
        foreach (var path in new[] { @"Item\ObjectComponents\Weapon\Sword_2H_Claymore_B_02.m2", @"Item\ObjectComponents\Weapon\Mace_1H_Stratholme_D_02.m2" })
        {
            var data = TestEnv.Client.Read(path);
            if (data == null) continue;
            var a = ModelAsset.Build(path, data, res.LoadBlp);
            foreach (var b in a.Views[0].Batches)
            {
                total++;
                for (int layer = 0; layer < b.LayerCount; layer++)
                {
                    int ui = b.Unit.TexUnitLookup + layer;
                    bool expect = ui < a.Model.TexUnitLookup.Length && (ushort)a.Model.TexUnitLookup[ui] >= 3;
                    Assert.Equal(expect, b.EnvMap[layer]);
                    if (expect) env++;
                }
            }
        }
        _out.WriteLine($"{env} environment-mapped layers in {total} batches");
    }

    [Fact]
    public void ModelsLoadFastAndSkinsSwitchInstantly()
    {
        var client = TestEnv.Client;
        var cat = new Catalog(client);
        var res = new ModelResolver(cat);
        _ = cat.CreatureDisplays;            // DBC tables are loaded once per session (the app has them after the index opens)
        _ = res.AnimationNames;
        res.Options(2, 0);
        foreach (var (path, display) in new[] { (@"Creature\Boar\Boar.m2", 503), (@"Character\Orc\Male\OrcMale.m2", 0), (@"Creature\Wolf\Wolf.m2", 0) })
        {
            var sw = Stopwatch.StartNew();
            var data = client.Read(path)!;
            var asset = ModelAsset.Build(path, data, res.LoadBlp);
            var look = display > 0 ? res.CreatureDisplay(display) : res.DefaultFor(path);
            double ms = sw.Elapsed.TotalMilliseconds;
            _out.WriteLine($"{path}: {asset.Model.Vertices.Length} vertices, {asset.Model.Bones.Length} bones, {asset.Model.Sequences.Length} sequences: cold load {ms:0.0} ms");
            Assert.NotEmpty(look.Slots);
        }
        // switching between every boar skin (each decoded once, then cached)
        var sw2 = Stopwatch.StartNew();
        var ids = new AssetIndexLess(cat).BoarDisplays();
        foreach (var d in ids) res.CreatureDisplay(d);
        double first = sw2.Elapsed.TotalMilliseconds;
        sw2.Restart();
        foreach (var d in ids) res.CreatureDisplay(d);
        double again = sw2.Elapsed.TotalMilliseconds;
        _out.WriteLine($"{ids.Count} boar displays: first pass {first:0.0} ms ({first / ids.Count:0.00} ms each), cached {again / ids.Count:0.000} ms each");
        // bone matrices of the 128-bone orc for one frame
        var orc = M2Model.Load(client.Read(@"Character\Orc\Male\OrcMale.m2")!);
        var mats = new System.Numerics.Matrix4x4[orc.Bones.Length];
        var done = new bool[orc.Bones.Length];
        var t = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            M2Anim.BoneMatrices(orc, new M2Anim.Time(5, orc.Sequences[5].Start, (uint)(i % (int)Math.Max(1, orc.Sequences[5].Duration)), i), mats, done);
        double perFrame = t.Elapsed.TotalMilliseconds / 1000;
        _out.WriteLine($"orc male bone matrices: {perFrame:0.000} ms per frame");
        Assert.True(perFrame > 0);   // the limits are checked in PerformanceTests (alone, tolerant)
    }

    /// <summary>Creature displays of Boar.m2 straight from the DBCs (no index needed).</summary>
    private sealed class AssetIndexLess
    {
        private readonly Catalog _c;
        public AssetIndexLess(Catalog c) { _c = c; }

        public List<int> BoarDisplays() => _c.CreatureDisplays.Values
            .Where(d => _c.CreatureModels.TryGetValue(d.ModelId, out var m) && m.ModelPath.Contains(@"Boar\Boar.", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Id).OrderBy(x => x).ToList();
    }

    [Fact]
    public void ServerPasswordIsEncryptedAndThePlainTextOneIsMigrated()
    {
        var dir = TestEnv.TempDir("settings");
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(dir);
            // a settings.json written by version 0.3: the password as typed
            File.WriteAllText(paths.SettingsFile, """
                {"ClientDir":"C:\\Games\\WoW","Server":{"Enabled":true,"Host":"127.0.0.1","Port":3306,"User":"root","Password":"s3cret-root","Database":"mangos"}}
                """);
            var s = AppSettings.Load(paths);
            Assert.Equal("s3cret-root", s.Server.Password);
            var text = File.ReadAllText(paths.SettingsFile);
            Assert.DoesNotContain("s3cret-root", text);                 // rewritten at once
            Assert.DoesNotContain("\"Password\"", text);
            Assert.Contains("\"PasswordProtected\"", text);
            Assert.Equal(@"C:\Games\WoW", s.ClientDir);
            // the encrypted form reads back, and a new password is saved encrypted too
            var again = AppSettings.Load(paths);
            Assert.Equal("s3cret-root", again.Server.Password);
            Assert.False(again.Server.MigratedFromPlainText);
            again.Server.Password = "other pass é";
            again.Save(paths);
            Assert.DoesNotContain("other pass", File.ReadAllText(paths.SettingsFile));
            Assert.Equal("other pass é", AppSettings.Load(paths).Server.Password);
            // a value another Windows user encrypted cannot be read: empty password, flagged
            var doc = JsonNode.Parse(File.ReadAllText(paths.SettingsFile))!;
            doc["Server"]!["PasswordProtected"] = Convert.ToBase64String(new byte[64]);
            File.WriteAllText(paths.SettingsFile, doc.ToJsonString());
            var bad = AppSettings.Load(paths);
            Assert.True(bad.Server.PasswordUnreadable);
            Assert.Equal("", bad.Server.Password);
            // the default settings (no file) still default to root/root for the local test server
            Assert.Equal("root", new AppSettings().Server.Password);
            Assert.DoesNotContain("\"root\"", JsonSerializer.Serialize(new AppSettings()).Replace("\"User\":\"root\"", ""));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
