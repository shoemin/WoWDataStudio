using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// M2 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 5.2): the modern reader on the synthetic cube v274 of
/// <see cref="SyntheticClients"/> (our own bytes): vertices, bounds, the .skin view, the sequences on one timeline, the
/// compressed quaternions, the external sequence left without keys; then the cube through a read-only session (the
/// model source and the looks of its displays).
/// </summary>
public class M2ModernTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("m2modern");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static M2Model Cube() => M2ModernReader.Load(CubeM2v274(), id => id == CubeSkin ? CubeSkinFile() : null, id => $"#{id}.blp");

    [Fact]
    public void CompressedQuaternionsDecodeAroundTheMiddle()
    {
        Assert.Equal(0f, M2ModernReader.DecompressQuat(32767));
        Assert.Equal(0f, M2ModernReader.DecompressQuat(short.MinValue));
        Assert.Equal(1f, M2ModernReader.DecompressQuat(-1));
        Assert.Equal(-1f, M2ModernReader.DecompressQuat(0));
        Assert.Equal(0.5f, M2ModernReader.DecompressQuat(-16384), 4);
        Assert.Equal(-0.5f, M2ModernReader.DecompressQuat(16384), 4);
        foreach (var f in new[] { -1f, -0.75f, -0.3f, -1e-3f, 0f, 1e-3f, 0.25f, 0.7071068f, 1f })
            Assert.Equal(f, M2ModernReader.DecompressQuat(M2ModernReader.CompressQuat(f)), 4);
    }

    [Fact]
    public void TheCubeIsReadWithItsSkinVertexListAndBounds()
    {
        var m = Cube();
        Assert.Equal(274u, m.Version);
        Assert.Equal("TestCube", m.Name);
        Assert.Equal(8, m.Vertices.Length);
        for (int k = 0; k < 8; k++)
        {
            Assert.Equal(CubeCorner(k), m.Vertices[k].Position);
            Assert.Equal((byte)255, m.Vertices[k].W0);
        }
        Assert.Equal(new Vector3(-1), m.BoundsMin);
        Assert.Equal(new Vector3(1), m.BoundsMax);
        var v = Assert.Single(m.Views);
        Assert.Equal(CubeTriangles, v.Triangles);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (ushort)i), v.Indices);
        var sm = Assert.Single(v.Submeshes);
        Assert.Equal((0, 8, 0, 36), (sm.VertexStart, sm.VertexCount, sm.IndexStart, sm.IndexCount));
        var tu = Assert.Single(v.TexUnits);
        Assert.Equal((1, 0, -1), (tu.TextureCount, tu.TextureLookup, tu.Color));
        // the asset the renderer draws: 12 triangles, the same bounds
        var a = ModelAsset.Build("cube.m2", m, _ => null);
        Assert.Equal(12, a.TriangleCount(0));
        Assert.Equal((new Vector3(-1), new Vector3(1)), (a.BoundsMin, a.BoundsMax));
        // textures: the files by FileDataID (named by the caller), the run-time slot without a file
        Assert.Equal(new[] { (0, $"#{ModelTexture}.blp"), (11, ""), (0, $"#{SkinTexture}.blp") }, m.Textures.Select(t => (t.Type, t.FileName)));
        var info = m.Modern!;
        Assert.Equal((274u, 1, 1), (info.Version, info.SkinProfiles, info.ViewsRead));
        Assert.Equal(new uint[] { CubeSkin }, info.SkinFiles);
        Assert.Equal(new[] { 12 }, info.ViewTriangles);
        Assert.Equal(new uint[] { ModelTexture, 0, SkinTexture }, info.TextureFiles);
        Assert.Equal(new M2AnimFile(4, 0, CubeAnim), Assert.Single(info.AnimFiles));
        Assert.Equal(new[] { "MD21", "SFID", "TXID", "AFID" }, info.Chunks.Select(c => c.Magic));
        Assert.Contains(info.Ignored, x => x.StartsWith("AFID: 1 external animation file", StringComparison.Ordinal));
        Assert.Empty(info.Notes);
        // the M1 TXID helper reads the same chunk
        Assert.Equal(new uint[] { ModelTexture, 0, SkinTexture }, WoWData.Core.Index.Catalog160.TextureIds(CubeM2v274()));
    }

    [Fact]
    public void SequencesArePutOntoOneTimelineAndTheExternalOneHasNoKeys()
    {
        var m = Cube();
        Assert.Equal(2, m.Sequences.Length);
        var stand = m.Sequences[0];
        var walk = m.Sequences[1];
        Assert.Equal((0, 0u, 1000u, 0x20u, (uint)150), (stand.AnimId, stand.Start, stand.End, stand.Flags, stand.BlendTime));
        Assert.Equal((4, 1001u, 1501u, 500u, 0u), (walk.AnimId, walk.Start, walk.End, walk.Duration, walk.Flags));
        Assert.Equal((1, 1, 0), (m.Modern!.InlineSequences, m.Modern.ExternalSequences, m.Modern.AliasSequences));
        Assert.Contains(m.Modern.NotShown, x => x.Contains("1 animation kept in external .anim files", StringComparison.Ordinal));

        var bone = Assert.Single(m.Bones);
        Assert.Equal(-1, bone.Parent);
        Assert.True(bone.IsAnimated);
        // rotation: the two keys of Stand; Walk's keys are in its .anim file (never read, no range)
        var rot = bone.Rotation;
        Assert.Equal(new uint[] { 0, 1000 }, rot.Times);
        Assert.Equal(new[] { (0u, 1u), (uint.MaxValue, uint.MaxValue) }, rot.Ranges);
        Assert.True(Vector4.Distance(new Vector4(0, 0, 0, 1), new Vector4(rot.Values[0].X, rot.Values[0].Y, rot.Values[0].Z, rot.Values[0].W)) < 1e-4f);
        Assert.True(Vector4.Distance(new Vector4(CubeTurn.X, CubeTurn.Y, CubeTurn.Z, CubeTurn.W), new Vector4(rot.Values[1].X, rot.Values[1].Y, rot.Values[1].Z, rot.Values[1].W)) < 1e-4f);
        // translation: the last key (500 ms) is held until Stand ends (repeated at 1000 ms)
        var tr = bone.Translation;
        Assert.Equal(new uint[] { 0, 500, 1000 }, tr.Times);
        Assert.Equal(new[] { Vector3.Zero, new Vector3(0, 0, 2), new Vector3(0, 0, 2) }, tr.Values);
        Assert.Equal((0u, 2u), tr.Ranges[0]);

        // evaluation with the 1.12 semantics (M2Anim): component-wise lerp at the middle of Stand
        var gs = m.GlobalSequences;
        var mid = new M2Anim.Time(0, stand.Start, 500, 0);
        var q = M2Anim.Eval(rot, mid, gs, Quaternion.Identity);
        var s45 = MathF.Sin(MathF.PI / 4);
        Assert.Equal(s45 / 2, q.Z, 3);
        Assert.Equal((1 + s45) / 2, q.W, 3);
        Assert.Equal(new Vector3(0, 0, 1), M2Anim.Eval(tr, new M2Anim.Time(0, stand.Start, 250, 0), gs, Vector3.Zero));
        Assert.Equal(new Vector3(0, 0, 2), M2Anim.Eval(tr, new M2Anim.Time(0, stand.Start, 750, 0), gs, Vector3.Zero));
        Assert.Equal(new Vector3(0, 0, 2), M2Anim.Eval(tr, new M2Anim.Time(0, stand.Start, 1000, 0), gs, Vector3.Zero));
        // Walk: no keys, the track's default
        Assert.Equal(Quaternion.Identity, M2Anim.Eval(rot, new M2Anim.Time(1, walk.Start, 100, 0), gs, Quaternion.Identity));
        // the bone matrices of the renderer: a quarter turn at the end of Stand moves corner (1, -1, -1) to (1, 1, -1)
        var mats = new Matrix4x4[1];
        M2Anim.BoneMatrices(m, new M2Anim.Time(0, stand.Start, 1000, 0), mats);
        var p = Vector3.Transform(CubeCorner(1), mats[0]);
        Assert.True(Vector3.Distance(new Vector3(1, 1, 1), p) < 1e-3f, p.ToString());
    }

    [Fact]
    public void BrokenModelsAndMissingSkinsAreReportedNotThrownAsCrashes()
    {
        Assert.Throws<M2Exception>(() => M2ModernReader.Load(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, _ => null));
        var noSkin = Assert.Throws<M2Exception>(() => M2ModernReader.Load(CubeM2v274(), _ => throw new ClientFileException("#1000008 is encrypted and cannot be read (key 0807060504030201).")));
        Assert.Contains("first view (skin file #1000008) cannot be read: #1000008 is encrypted", noSkin.Message);
        var cut = CubeM2v274()[..200];
        Assert.Throws<M2Exception>(() => M2ModernReader.Load(cut, _ => CubeSkinFile()));
        Assert.Throws<M2Exception>(() => M2ModernReader.ReadSkin(new byte[0x40]));
        // a 1.12 model is not a modern one
        var md20 = new byte[0x200];
        "MD20"u8.CopyTo(md20);
        Assert.Contains("1.12 models open in your game's window", Assert.Throws<M2Exception>(() => M2ModernReader.Load(md20, _ => null)).Message);
    }

    private AppSession Open()
    {
        var install = Path.Combine(_root, "bnet");
        var (flavor, lf) = WriteClassicBeta(install);
        var copy = ClientCopies.Inspect(flavor)!;
        var paths = new AppPaths(Path.Combine(_root, "home"));
        var s = new AppSession(paths, AppSettings.Load(paths));
        s.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = lf }, null).GetAwaiter().GetResult();
        return s;
    }

    [Fact]
    public void AReadOnlySessionReadsTheCubeAndItsLooks()
    {
        using var s = Open();
        Assert.Null(s.Models);
        var looks = Assert.IsType<ModernModelResolver>(s.Looks);
        Assert.Same(s.ModernLooks, looks);
        var m = s.ModelSource!.Load(CubeName);
        Assert.Equal(8, m.Vertices.Length);
        Assert.Equal(12, m.Views[0].Triangles.Length / 3);
        // the texture names come from the session's name table (the listfile names the skin)
        Assert.Equal(new[] { $"#{ModelTexture}.blp", "", SkinName }, m.Textures.Select(t => t.FileName));
        Assert.Equal(3, s.ModelSource.Load("#" + CubeModel).Textures.Length);
        Assert.Throws<IOException>(() => s.ModelSource.Load("#" + MissingTexture));
        // display 1: skin 1 readable (slot 11), skin 2 encrypted (a note with the reason)
        var d1 = looks.CreatureDisplay(1);
        Assert.Equal(SkinName, d1.Slots[11].Path);
        Assert.False(d1.Slots.ContainsKey(12));
        Assert.Contains(d1.Notes, n => n.Contains("encrypted", StringComparison.Ordinal));
        // display 2: a humanoid NPC look: its baked texture (BakeMaterialResourcesID 70 -> the icon's FileDataID here)
        var d2 = looks.CreatureDisplay(2);
        Assert.Equal(IconName, d2.Slots[1].Path);
        Assert.NotNull(d2.Geosets);
        // item display 20: the model texture of its slot (ModelMaterialResourcesID1 80 -> the skin)
        Assert.Equal(SkinName, looks.ItemDisplay(20, CubeName).Slots[2].Path);
        // a model's default look: its first creature display
        Assert.Equal(SkinName, looks.DefaultFor(CubeName).Slots[11].Path);
        Assert.Equal(1, looks.FirstCreatureDisplay(CubeName));
        Assert.Contains("Display 99", looks.CreatureDisplay(99).Notes.Single());
        // no 1.12 game: animations are named by their id
        Assert.Equal("Anim4", looks.AnimationName(4));
        Assert.Null(looks.RaceOfModel(CubeName));
        // textures are decoded at most 1024 pixels per side
        Assert.NotNull(looks.LoadBlp(SkinName));
        Assert.Null(looks.LoadBlp("#" + LockedSkin));
    }
}
