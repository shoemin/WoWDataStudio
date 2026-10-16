using System.IO;
using System.Linq;
using System.Text.Json;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// The texture round trip end to end (Mottled Boar) and the delivery actions against a FAKE client folder. Nothing here
/// writes into the real client: the real Client\Data listing (names, sizes, times) is compared before and after.
/// </summary>
public class WorkflowTests : IDisposable
{
    private const string Boar = @"Creature\Boar\BoarSkinIvory.blp";
    private readonly string _root = TestEnv.TempDir("workflow");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();
    private readonly ClientPaths _paths;
    private readonly Workspace _ws;
    private readonly TextureService _ts;

    public WorkflowTests()
    {
        var fakeClient = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fakeClient, "Data"));
        _paths = new AppPaths(Path.Combine(_root, "home")).ForClient(fakeClient);
        _ws = new Workspace(_paths);
        _ts = new TextureService(TestEnv.Client, _ws);   // reads the real archives, writes only below _root
    }

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());    // the real Client\Data folder is unchanged
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string ExportBoar(out TextureSidecar side)
    {
        var png = Path.Combine(_root, "export", "BoarSkinIvory.png");
        (_, side) = _ts.Export(Boar, png, new() { ["target"] = "creature:3098" });
        return png;
    }

    [Fact]
    public void ExportWritesPngAndSidecar()
    {
        var png = ExportBoar(out var side);
        Assert.True(File.Exists(png));
        var json = JsonSerializer.Deserialize<TextureSidecar>(File.ReadAllText(TextureService.SidecarFor(png)))!;
        Assert.Equal(Boar, json.ArchivePath);
        Assert.Equal("texture.MPQ", json.Source);
        Assert.Equal("DXT1 + 1-bit alpha", json.Blp!.Format);
        Assert.Equal((256, 256, 9), (json.Blp.Width, json.Blp.Height, json.Blp.Levels));
        var orig = BlpFile.Parse(TestEnv.Client.Read(Boar)!);
        Assert.True(Png.Load(png).SameContent(orig.Decode(0)));
        Assert.Equal(TextureService.PixelSha(orig.Decode(0), true), json.PixelSha256);
    }

    [Fact]
    public void UnmodifiedPngGivesTheOriginalBytes()
    {
        var png = ExportBoar(out _);
        var plan = _ts.Plan(png, null);
        Assert.True(plan.Identical);
        Assert.Equal(0, plan.Diff!.ChangedPixels);
    }

    [Fact]
    public void EditedPngRoundTripMatchesEncoderAndCompare()
    {
        var png = ExportBoar(out _);
        var img = Png.Load(png);
        for (int y = 64; y < 128; y++)
            for (int x = 32; x < 160; x++)
            {
                int o = img.Offset(x, y);
                if (img.Pixels[o + 3] == 0) continue;
                img.Pixels[o] = 255; img.Pixels[o + 1] = 0; img.Pixels[o + 2] = 0;
            }
        Png.Save(img, png, true);
        var plan = _ts.Plan(png, null);
        Assert.False(plan.Identical);
        Assert.False(plan.NeedsResize);
        var orig = BlpFile.Parse(TestEnv.Client.Read(Boar)!);
        var (expected, rep) = BlpEncoder.EncodeLike(orig, img);
        Assert.Equal(expected, plan.Encoded);
        Assert.Equal(orig.Data.Length, plan.Encoded.Length);                     // same layout
        Assert.Empty(BlpEncoder.Validate(plan.Encoded));
        Assert.True(rep.PerLevel[0].ReEncoded < rep.PerLevel[0].Blocks);         // only the edited blocks
        // the compare statistics: only the edited area changed
        Assert.InRange(plan.Diff!.ChangedPercent, 5, 20);
        var heat = ImageCompare.Heatmap(plan.OriginalImage!, plan.EditedImage);
        int o2 = heat.Offset(100, 100);
        Assert.Equal((255, 0), (heat.Pixels[o2], (int)heat.Pixels[o2 + 2]));     // changed -> yellow..red
        int o3 = heat.Offset(200, 200);
        Assert.Equal(heat.Pixels[o3], heat.Pixels[o3 + 1]);                         // unchanged -> grey

        var rec = _ts.Commit(plan, null, "test import");
        Assert.True(_ws.IsModified(Boar));
        Assert.Equal(Workspace.Sha256(orig.Data), rec.Original.Sha256);
        Assert.Equal("DXT1 + 1-bit alpha", rec.Original.Format);
        Assert.Equal(plan.Encoded, _ws.ReadEdited(Boar));
        var set = _ws.Manifest.ChangeSets[rec.ChangeSet];
        Assert.Equal("test import", set.Label);
    }

    [Fact]
    public void SizeChecks()
    {
        var png = ExportBoar(out _);
        var odd = ImageOps.Resize(Png.Load(png), 300, 300);
        var oddPath = Path.Combine(_root, "export", "odd.png");
        Png.Save(odd, oddPath, true);
        var plan = _ts.Plan(oddPath, Boar);
        Assert.True(plan.NeedsResize);
        Assert.Equal((256, 256), plan.SuggestedSize);
        var resized = _ts.Plan(oddPath, Boar, (256, 256));
        Assert.False(resized.NeedsResize);
        Assert.Equal(256, resized.EncodedBlp.Width);
        Assert.Contains(resized.Notes, n => n.Contains("resized from 300x300"));
        // a power-of-two but different size needs an explicit choice too, then keeps the format
        var big = Path.Combine(_root, "export", "big.png");
        Png.Save(ImageOps.Resize(Png.Load(png), 512, 512), big, true);
        Assert.True(_ts.Plan(big, Boar).NeedsResize);
        var keepBig = _ts.Plan(big, Boar, (512, 512));
        Assert.Equal(("DXT1 + 1-bit alpha", 512), (keepBig.FormatName, keepBig.EncodedBlp.Width));
        Assert.Throws<TextureException>(() => _ts.Plan(Path.Combine(_root, "missing.png"), Boar));
    }

    [Fact]
    public void AlphaAddedToOpaqueTextureWarnsAndCanSwitchToDxt5()
    {
        const string icon = @"Interface\Icons\INV_Axe_09.blp";    // DXT1 without alpha
        var png = Path.Combine(_root, "export", "axe.png");
        _ts.Export(icon, png);
        var img = Png.Load(png);
        for (int i = 3; i < img.Pixels.Length / 2; i += 4) img.Pixels[i] = 0;
        Png.Save(img, png, true);
        var plan = _ts.Plan(png, null);
        Assert.Contains(plan.Warnings, w => w.Contains("no transparency"));
        var dxt5 = _ts.Plan(png, null, null, ImportFormatChoice.Dxt5KeepAlpha);
        Assert.StartsWith("DXT5", dxt5.FormatName);
    }

    private ChangeRecord SaveTintedBoar(string label = "tint")
    {
        var orig = BlpFile.Parse(TestEnv.Client.Read(Boar)!);
        var img = orig.Decode(0);
        for (int i = 0; i < img.Pixels.Length; i += 4) img.Pixels[i] = (byte)Math.Min(255, img.Pixels[i] + 60);
        var (data, _) = BlpEncoder.EncodeLike(orig, img);
        return _ws.SaveEdit(Boar, data, new OriginInfo { Archive = "texture.MPQ", Sha256 = Workspace.Sha256(orig.Data), Format = orig.FormatName }, _ws.BeginChangeSet(label));
    }

    [Fact]
    public void LooseInstallRemoveAndRevertInFakeClient()
    {
        var rec = SaveTintedBoar();
        var dest = Path.Combine(_paths.DataDir, "Creature", "Boar", "BoarSkinIvory.blp");
        var actions = _ws.InstallLoose();
        Assert.Equal("add", actions.Single().Action);
        Assert.Equal(_ws.ReadEdited(Boar), File.ReadAllBytes(dest));
        Assert.True(_ws.View(_ws.Get(Boar)!).InstalledCurrent);
        Assert.Equal("update", _ws.InstallLoose().Single().Action);
        Assert.Single(_ws.RemoveLoose());
        Assert.False(File.Exists(dest));
        Assert.False(Directory.Exists(Path.Combine(_paths.DataDir, "Creature")));   // emptied folders pruned
        Assert.True(Directory.Exists(_paths.DataDir));

        // a foreign file is never replaced silently; with force it is backed up and restored on removal
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "someone else's file");
        Assert.Throws<WorkspaceException>(() => _ws.InstallLoose());
        Assert.Equal("someone else's file", File.ReadAllText(dest));
        Assert.StartsWith("replace", _ws.InstallLoose(force: true).Single().Action);
        _ws.Revert(Boar);                                          // revert also takes it out of the game
        Assert.Equal("someone else's file", File.ReadAllText(dest));
        Assert.False(_ws.IsModified(Boar));
        Assert.False(File.Exists(_ws.WorkspaceFile(Boar)));
        Assert.Empty(_ws.Manifest.ChangeSets);
    }

    [Fact]
    public void PatchBuildInFakeClientWithBackup()
    {
        SaveTintedBoar();
        var existing = Path.Combine(_paths.DataDir, "patch-3.MPQ");
        File.WriteAllText(existing, "old patch");
        var build = _ws.BuildPatch("patch-3.MPQ", install: true);
        Assert.Equal(existing, build.InstalledTo);
        Assert.Equal("old patch", File.ReadAllText(build.Backup!));
        using (var a = new MpqArchive(existing))
            Assert.Equal(_ws.ReadEdited(Boar), a.Read(Boar));
        // the game would see it: mount the fake Data folder's patch on top of the real archives' order
        Assert.Equal(new[] { "patch-3.MPQ" }, ClientData.ClientArchiveOrder(_paths.DataDir).Where(n => n.StartsWith("patch-", StringComparison.OrdinalIgnoreCase)));
        Assert.True(_ws.View(_ws.Get(Boar)!).PackedCurrent);
        Assert.Throws<WorkspaceException>(() => _ws.BuildPatch("patch-2.MPQ"));
        Assert.Throws<WorkspaceException>(() => _ws.BuildPatch("patch.MPQ"));
        Assert.Throws<WorkspaceException>(() => _ws.BuildPatch("model.MPQ"));
    }

    [Fact]
    public void ChangeSetRevertsAsOne()
    {
        const string axe = @"Interface\Icons\INV_Axe_09.blp";
        var set = _ws.BeginChangeSet("multi-file import");
        foreach (var p in new[] { Boar, axe })
        {
            var orig = BlpFile.Parse(TestEnv.Client.Read(p)!);
            var img = orig.Decode(0);
            img.Pixels[0] ^= 0xFF;
            var (data, _) = BlpEncoder.EncodeLike(orig, img);
            _ws.SaveEdit(p, data, new OriginInfo { Sha256 = Workspace.Sha256(orig.Data), Models = new() { "m.m2" }, UsedBy = new() { "x" } }, set);
        }
        Assert.Equal(2, _ws.Manifest.ChangeSets[set.Id].Files.Count);
        // the manifest keeps the origin metadata per file (checksum, format, users) for the later model-package import
        var reloaded = new Workspace(_paths);
        Assert.Equal(new[] { "m.m2" }, reloaded.Get(axe)!.Original.Models);
        reloaded.RevertChangeSet(set.Id);
        Assert.Empty(reloaded.Manifest.Files);
        Assert.Empty(reloaded.Manifest.ChangeSets);
    }
}
