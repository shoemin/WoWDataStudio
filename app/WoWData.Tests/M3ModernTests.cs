using System.IO;
using System.Linq;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// M3 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 5.2) on the synthetic cubes of <see cref="SyntheticClients"/> (our
/// own bytes): the external animation (AFM2 .anim) loaded when its sequence is chosen and kept within its budget; the
/// skeleton file with its parent (SKB1, SKS1, SKA1, SKPD) and its .anim (AFSB); the particle emitter and ribbon read and
/// simulated; the blend modes. Expected values come from how the fixtures are built.
/// </summary>
public class M3ModernTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("m3modern");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static M2Model Load(byte[] model, Func<uint, byte[]?>? files = null) => M2ModernReader.Load(model, files ?? M3File, id => $"#{id}.blp");

    private static Vector3 Translation(M2Model m, int seq, uint ms) =>
        M2Anim.Eval(m.Bones[0].Translation, new M2Anim.Time(seq, m.Sequences[seq].Start, ms, 0), m.GlobalSequences, Vector3.Zero);

    private static Quaternion Rotation(M2Model m, int seq, uint ms) =>
        M2Anim.Eval(m.Bones[0].Rotation, new M2Anim.Time(seq, m.Sequences[seq].Start, ms, 0), m.GlobalSequences, Quaternion.Identity);

    private static void Near(Quaternion want, Quaternion got) =>
        Assert.True(Vector4.Distance(new Vector4(want.X, want.Y, want.Z, want.W), new Vector4(got.X, got.Y, got.Z, got.W)) < 1e-3f, $"{want} != {got}");

    private static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 1e-3f, $"{want} != {got}");

    [Fact]
    public void TheExternalWalkIsReadFromItsAnimFileWhenChosen()
    {
        int reads = 0;
        var m = Load(CubeM2v274(), id => { if (id == CubeAnim) reads++; return M3File(id); });
        var ext = m.Modern!.Animations!;
        Assert.Equal(0, reads);                                            // nothing read before the sequence is chosen
        Assert.Equal(new ExternalSequenceInfo(1, CubeAnim, ExternalState.NotLoaded, "", 0), ext.Status(1));
        Assert.Equal(ExternalState.InModel, ext.Status(0).State);
        Assert.Equal(Quaternion.Identity, Rotation(m, 1, 250));             // Walk has no keys yet
        var st = ext.Ensure(1);
        Assert.Equal((ExternalState.Loaded, 4, 1), (st.State, st.Keys, reads));
        // translation (0,0,0) at 0 ms -> (1,0,0) at 250 ms, held until Walk ends; rotation identity -> quarter turn at 500 ms
        Near(new Vector3(0.5f, 0, 0), Translation(m, 1, 125));
        Near(new Vector3(1, 0, 0), Translation(m, 1, 400));
        Near(CubeTurn, Rotation(m, 1, 500));
        var half = Rotation(m, 1, 250);
        Assert.Equal(MathF.Sin(MathF.PI / 4) / 2, half.Z, 3);
        // Stand is unchanged (its keys and its last translation key held until it ends)
        Near(new Vector3(0, 0, 2), Translation(m, 0, 750));
        Near(CubeTurn, Rotation(m, 0, 1000));
        // a second choice reads nothing again; the keys kept are counted
        Assert.Equal(ExternalState.Loaded, ext.Ensure(1).State);
        Assert.Equal(1, reads);
        Assert.Equal(1, ext.FilesRead);
        Assert.True(ext.LoadedBytes > 0);
        Assert.Equal(new[] { 1 }, ext.LoadedSequences);
        // the bone matrix at the end of Walk: corner (1, -1, -1) turned a quarter about Z and moved by (1, 0, 0)
        var mats = new Matrix4x4[1];
        M2Anim.BoneMatrices(m, new M2Anim.Time(1, m.Sequences[1].Start, 500, 0), mats);
        Near(new Vector3(2, 1, -1), Vector3.Transform(CubeCorner(1), mats[0]));
        // the AFID chunk is used now, not listed as left out
        Assert.DoesNotContain(m.Modern.Ignored, x => x.StartsWith("AFID", StringComparison.Ordinal));
        Assert.True(M2ModernInfo.Describe("AFID").Used);
    }

    [Fact]
    public void TheKeysStayWithinTheBudgetAndReleasedOnesLoadAgain()
    {
        var m = Load(CubeM2v274(secondWalk: true));
        var ext = m.Modern!.Animations!;
        Assert.Equal(2, ext.ExternalCount);
        Assert.Equal(ExternalState.Loaded, ext.Ensure(1).State);
        long one = ext.LoadedBytes;
        ext.Budget = one;                                                  // room for one sequence's keys
        Assert.Equal(ExternalState.Loaded, ext.Ensure(2).State);
        Assert.Equal(new[] { 2 }, ext.LoadedSequences);
        Assert.Equal(one, ext.LoadedBytes);
        // Walk 1 was released: its track rebuilt without its keys (it stands still until chosen again)
        Assert.Equal(Quaternion.Identity, Rotation(m, 1, 500));
        Near(CubeTurn, Rotation(m, 2, 500));
        Assert.Equal(ExternalState.NotLoaded, ext.Status(1).State);
        Assert.Contains("released", ext.Status(1).Why);
        Assert.Equal(ExternalState.Loaded, ext.Ensure(1).State);
        Near(CubeTurn, Rotation(m, 1, 500));
        Assert.Equal(new[] { 1 }, ext.LoadedSequences);
        Assert.Equal(3, ext.FilesRead);
        // Stand is never touched
        Near(CubeTurn, Rotation(m, 0, 1000));
    }

    [Fact]
    public void AMissingOrLockedAnimFileLeavesTheSequenceStillWithItsReason()
    {
        var gone = Load(CubeM2v274(), id => id == CubeAnim ? null : M3File(id));
        var st = gone.Modern!.Animations!.Ensure(1);
        Assert.Equal((ExternalState.Unreadable, "not in this client"), (st.State, st.Why));
        Assert.Equal(Quaternion.Identity, Rotation(gone, 1, 250));
        var locked = Load(CubeM2v274(), id => id == CubeAnim ? throw new ClientFileException("#1000009 is encrypted and cannot be read (key 0807060504030201).") : M3File(id));
        var st2 = locked.Modern!.Animations!.Ensure(1);
        Assert.Equal(ExternalState.Unreadable, st2.State);
        Assert.Contains("encrypted", st2.Why);
        // a broken .anim (a chunk running past its end) is reported, not thrown
        var broken = Load(CubeM2v274(), id => id == CubeAnim ? CubeAnimFile()[..20] : M3File(id));
        Assert.Equal(ExternalState.Unreadable, broken.Modern!.Animations!.Ensure(1).State);
        // an .anim file with no keys for the model
        var empty = Load(CubeM2v274(), id => id == CubeAnim ? new byte[] { (byte)'A', (byte)'F', (byte)'M', (byte)'2', 0, 0, 0, 0 } : M3File(id));
        Assert.Equal(ExternalState.NoKeys, empty.Modern!.Animations!.Ensure(1).State);
    }

    [Fact]
    public void TheSkeletonFileGivesTheBonesSequencesAndAttachmentsWithItsParentsAnimations()
    {
        var m = Load(CubeSkelM2());
        var mi = m.Modern!;
        Assert.Equal((CubeSkel, CubeParentSkel, true, 1), (mi.SkeletonFile, mi.SkeletonParentFile, mi.SkeletonRead, mi.ParentSequences));
        Assert.Single(m.Bones);
        Assert.Equal(new[] { (0, 1000u), (4, 500u), (5, 800u) }, m.Sequences.Select(s => (s.AnimId, s.Duration)));
        Assert.Equal((2, 1, 1), (mi.InlineSequences, mi.ExternalSequences, mi.ParentSequences));   // inline: Stand and the parent's Run
        // the skeleton's attachment point
        var att = Assert.Single(m.Attachments);
        Assert.Equal((11, 0, new Vector3(0, 0, 1)), (att.Id, att.Bone, att.Position));
        // Stand: the child's own (a quarter turn about X); the parent's Stand (a translation) is replaced
        Near(CubeTurnX, Rotation(m, 0, 1000));
        Assert.Equal(Vector3.Zero, Translation(m, 0, 500));
        // Run: from the parent skeleton (its bone found by the name hash)
        Near(new Vector3(0, 0, 1.5f), Translation(m, 2, 400));
        Near(new Vector3(0, 0, 3), Translation(m, 2, 800));
        // Walk: in the skeleton's .anim file (AFSB), read when chosen
        Assert.Equal(new M2AnimFile(4, 0, CubeSkelAnim), Assert.Single(mi.AnimFiles));
        Assert.Equal(ExternalState.Loaded, mi.Animations!.Ensure(1).State);
        Near(CubeTurn, Rotation(m, 1, 500));
        // the bone matrix at the end of Stand: corner (-1, 1, -1) turned a quarter about X -> (-1, 1, 1)
        var mats = new Matrix4x4[1];
        M2Anim.BoneMatrices(m, new M2Anim.Time(0, m.Sequences[0].Start, 1000, 0), mats);
        Near(new Vector3(-1, 1, 1), Vector3.Transform(CubeCorner(2), mats[0]));
        // the skeleton chunk is used
        Assert.True(M2ModernInfo.Describe("SKID").Used);
        Assert.Empty(mi.Notes);
    }

    [Fact]
    public void ASkeletonWithoutItsParentOrWithoutAnyFileIsReported()
    {
        var noParent = Load(CubeSkelM2(), id => id == CubeParentSkel ? null : M3File(id));
        Assert.Equal(2, noParent.Sequences.Length);
        Assert.Contains(noParent.Modern!.Notes, n => n.StartsWith($"parent skeleton #{CubeParentSkel} cannot be read", StringComparison.Ordinal));
        Assert.Single(noParent.Bones);
        var noSkel = Load(CubeSkelM2(), id => id == CubeSkel ? null : M3File(id));
        Assert.Empty(noSkel.Bones);
        Assert.Empty(noSkel.Sequences);
        Assert.False(noSkel.Modern!.SkeletonRead);
        Assert.Contains(noSkel.Modern.Notes, n => n.StartsWith($"skeleton file #{CubeSkel} cannot be read", StringComparison.Ordinal));
        Assert.Equal(12, noSkel.Views[0].Triangles.Length / 3);            // the mesh still shows
    }

    [Fact]
    public void TheParticleEmitterAndRibbonAreReadOnTheTimeline()
    {
        var m = Load(CubeM2v274(effects: true));
        var mi = m.Modern!;
        var fx = mi.Effects!;
        var p = Assert.Single(fx.Particles);
        Assert.Equal((0, new Vector3(0, 0, 1), 4, 1, 1, 1), (p.Bone, p.Position, p.Blend, p.EmitterType, p.Rows, p.Cols));
        Assert.Equal(new[] { 0 }, p.Textures);
        var stand = new M2Anim.Time(0, 0, 100, 0);
        Assert.Equal(1f, M2EffectSimulation.Eval(p.Speed, stand, m.GlobalSequences, 0));
        Assert.Equal(0.5f, M2EffectSimulation.Eval(p.Lifespan, stand, m.GlobalSequences, 0));
        Assert.Equal(20f, M2EffectSimulation.Eval(p.EmissionRate, stand, m.GlobalSequences, 0));
        Assert.Equal(new[] { 0f, 1f }, p.Color.Times);
        Assert.Equal(new[] { new Vector3(255, 0, 0), new Vector3(255, 255, 0) }, p.Color.Values);
        Assert.Equal(new short[] { 32767, 0 }, p.Alpha.Values);
        Assert.Equal(new[] { new Vector2(0.1f), new Vector2(0.2f) }, p.Scale.Values);
        var r = Assert.Single(fx.Ribbons);
        Assert.Equal((0, new Vector3(1, 0, 0), 10f, 1f), (r.Bone, r.Position, r.EdgesPerSecond, r.EdgeLifetime));
        Assert.Equal(new[] { 0 }, r.Textures);
        Assert.Equal(new[] { 0 }, r.Materials);
        Assert.Equal(new Vector3(0, 1, 0), M2Anim.Eval(r.Color, stand, m.GlobalSequences, Vector3.One));
        Assert.Equal(0.5f, M2EffectSimulation.Eval(r.HeightAbove, stand, m.GlobalSequences, 0));
        // the material keeps its modern blend mode (7) beside the nearest 1.12 one (4) the 1.12 shape stores
        Assert.Equal(new[] { 7 }, mi.MaterialBlends);
        Assert.Equal(4, m.RenderFlags[0].Blend);
        Assert.Contains(mi.Approximated, a => a.StartsWith("blend mode 7 (blend add)", StringComparison.Ordinal));
        Assert.Contains(mi.Approximated, a => a.StartsWith("1 particle emitter and 1 ribbon drawn by the viewer's own simulation", StringComparison.Ordinal));
        // the 1.12-shaped structure has no particles (the effects are drawn from Modern.Effects only)
        Assert.Equal(0, m.ParticleCount);
    }

    [Fact]
    public void TheSimulationEmitsMovesAndFadesTheParticlesAndDropsRibbonEdges()
    {
        var m = Load(CubeM2v274(effects: true));
        var fx = m.Modern!.Effects!;
        var sim = new M2EffectSimulation(fx, m.GlobalSequences);
        var bones = new[] { Matrix4x4.Identity };
        var time = new M2Anim.Time(0, 0, 0, 0);
        for (int k = 0; k < 60; k++) sim.Step(time, bones, 1f / 30f);
        // 20 a second living 0.5 s: about 10 alive
        int n = sim.Particles(0).Count;
        Assert.InRange(n, 8, 12);
        foreach (var q in sim.Particles(0))
        {
            // born at (0, 0, 1), moving up at speed 1 for at most 0.5 s (a plane of no size, no angles)
            Assert.InRange(q.Position.Z, 1f, 1.5f + 1e-4f);
            Assert.True(Math.Abs(q.Position.X) < 1e-4f && Math.Abs(q.Position.Y) < 1e-4f);
        }
        // a newborn particle: red, opaque, 0.1; one at the end of its life: yellow, clear, 0.2
        var (c0, s0, _) = M2EffectSimulation.Look(fx.Particles[0], new M2Particle { Age = 0, Life = 0.5f, SizeFactor = Vector2.One });
        Assert.Equal((new Vector4(1, 0, 0, 1), new Vector2(0.1f)), (c0, s0));
        var (c1, s1, _) = M2EffectSimulation.Look(fx.Particles[0], new M2Particle { Age = 0.5f, Life = 0.5f, SizeFactor = Vector2.One });
        Assert.Equal(new Vector4(1, 1, 0, 0), c1);
        Assert.Equal(new Vector2(0.2f), s1);
        // the ribbon: 10 edges a second living 1 s; each edge 0.5 above and below (1, 0, 0)
        int edges = sim.Edges(0).Count;
        Assert.InRange(edges, 9, 13);
        foreach (var e in sim.Edges(0))
        {
            Near(new Vector3(1, 0, 0.5f), e.Top);
            Near(new Vector3(1, 0, -0.5f), e.Bottom);
            Assert.True(e.Age <= 1f + 1e-4f);
        }
        // the same seed gives the same particles; Reset starts again
        var again = new M2EffectSimulation(fx, m.GlobalSequences);
        for (int k = 0; k < 60; k++) again.Step(time, bones, 1f / 30f);
        Assert.Equal(sim.Particles(0).Select(q => q.Position), again.Particles(0).Select(q => q.Position));
        sim.Reset();
        Assert.Equal((0, 0), (sim.ParticleCount, sim.EdgeCount));
        // a bone moving the emitter moves where particles are born
        sim.Step(time, new[] { Matrix4x4.CreateTranslation(0, 5, 0) }, 0.2f);
        Assert.All(sim.Particles(0), q => Assert.Equal(5f, q.Position.Y, 3));
    }

    [Fact]
    public void ModernBlendModesMapToTheirGlFactors()
    {
        Assert.Equal((true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha), Pick(ModelRenderer.ModernBlendFactors(7)));
        Assert.Equal((true, BlendingFactor.SrcAlpha, BlendingFactor.One), Pick(ModelRenderer.ModernBlendFactors(4)));
        Assert.Equal((true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha), Pick(ModelRenderer.ModernBlendFactors(2)));
        Assert.Equal((false, BlendingFactor.One, BlendingFactor.Zero), Pick(ModelRenderer.ModernBlendFactors(0)));
        Assert.Equal(224f / 255f, ModelRenderer.ModernBlendFactors(1).AlphaRef);
        // 0-6 are the 1.12 factors (the 1.12 table is unchanged)
        for (int b = 0; b <= 6; b++) Assert.Equal(ModelRenderer.BlendFactors(b), Pick(ModelRenderer.ModernBlendFactors(b)));
        static (bool, BlendingFactor, BlendingFactor) Pick((bool E, BlendingFactor S, BlendingFactor D, float A) x) => (x.E, x.S, x.D);
    }

    [Fact]
    public void TheSceneLoadsAnExternalSequenceWhenItIsChosen()
    {
        var m = Load(CubeM2v274());
        var scene = new ModelScene { Asset = ModelAsset.Build("cube.m2", m, _ => null) };
        Assert.Equal(0, scene.Sequence);                                   // Stand (in the model)
        Assert.Equal(ExternalState.NotLoaded, m.Modern!.Animations!.Status(1).State);
        scene.Sequence = 1;
        Assert.Equal(ExternalState.Loaded, m.Modern.Animations.Status(1).State);
        Assert.True(m.Bones[0].Rotation.Ranges[1].First != uint.MaxValue);
        // a model with effects keeps the view redrawing while it plays
        var fxScene = new ModelScene { Asset = ModelAsset.Build("fx.m2", Load(CubeM2v274(effects: true)), _ => null) };
        Assert.True(fxScene.NeedsContinuousRedraw);
    }

    [Fact]
    public void AReadOnlySessionReadsTheSkeletonAnimAndEffectFilesByFileDataId()
    {
        var install = Path.Combine(_root, "bnet");
        var (flavor, lf) = WriteClassicBeta(install);
        var paths = new AppPaths(Path.Combine(_root, "home"));
        using var s = new AppSession(paths, AppSettings.Load(paths));
        s.OpenOtherAsync(ClientCopies.Inspect(flavor)!, new OtherOpenOptions { Listfile = lf }, null).GetAwaiter().GetResult();
        var cube = s.ModelSource!.Load(CubeName);
        Assert.Equal(ExternalState.Loaded, cube.Modern!.Animations!.Ensure(1).State);
        var skel = s.ModelSource.Load("#" + CubeSkelModel);
        Assert.Equal((3, 1), (skel.Sequences.Length, skel.Bones.Length));
        Assert.Equal(ExternalState.Loaded, skel.Modern!.Animations!.Ensure(1).State);
        var fx = s.ModelSource.Load("#" + CubeFx);
        Assert.Equal((1, 1), (fx.Modern!.Effects!.Particles.Count, fx.Modern.Effects.Ribbons.Count));
    }
}
