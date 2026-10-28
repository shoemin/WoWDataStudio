using System.Linq;
using System.Numerics;
using WoWData.Core.Designers;
using WoWData.Core.M2;
using static WoWData.Tests.SyntheticClassicModels;

namespace WoWData.Tests;

/// <summary>
/// Particles and ribbons of your 1.12 game (M2 version 256) on synthetic models (<see cref="SyntheticClassicModels"/>):
/// the records are read as the 1.12 client reads them (<see cref="M2ClassicEffects"/>) and simulated by its rules; a
/// Duplicate visual copy's effect follows its size and tint; another version's emitters are simulated as before.
/// </summary>
public class Classic112EffectsTests
{
    private static readonly M2Anim.Time Stand = new(0, 0, 100, 100);
    private static readonly M2Anim.Time Walk = new(1, 1000, 100, 100);
    private static readonly Matrix4x4[] NoBones = { Matrix4x4.Identity };

    private static void Near(Vector3 want, Vector3 got, float eps = 1e-4f) => Assert.True(Vector3.Distance(want, got) < eps, $"{got} is not {want}");

    private static M2Model Load(byte[] data) => M2Model.Load(data);

    [Fact]
    public void TheRecordsAreReadAsThe112ClientReadsThem()
    {
        var plane = new Particle
        {
            Flags = 0x1 | 0x8 | 0x10 | 0x200 | 0x400 | 0x8000, Position = new Vector3(1, 2, 3), Blend = 1, ParticleType = 2, Rows = 2, Cols = 4,
            Speed = Keys.PerAnimation(2, 5), Gravity = Keys.Float(1.5f), Lifespan = Keys.Float(0.8f), EmissionRate = Keys.PerAnimation(20, 0),
            MidPoint = 0.25f, Colors = new[] { ((byte)255, (byte)0, (byte)0, (byte)255), ((byte)0, (byte)255, (byte)0, (byte)128), ((byte)0, (byte)0, (byte)255, (byte)0) },
            Sizes = new[] { 0.1f, 0.4f, 0.2f }, LifespanCells = new ushort[] { 0, 3, 1 }, DecayCells = new ushort[] { 4, 7, 2 },
            TailCells = new short[] { 1, 2 }, TailDecayCells = new short[] { 5, 6 },
            Params = new[] { 0.5f, 10f, 0.6f, 0.8f, 1.2f, 0, 0.25f, 3.14f, 0, 0, 0, 0, 0, 0, 1f, 0, 0, 2f, 0, 0, 0, 0 },
        };
        var sphere = new Particle { EmitterType = 2, Flags = 0x1 | 0x100, Blend = 2, AreaLength = Keys.Float(1), AreaWidth = Keys.Float(2) };
        var spline = new Particle { EmitterType = 3, Blend = 3, SplinePoints = new[] { Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX, 3 * Vector3.UnitX }, GeometryModel = @"Spells\Rock.mdx" };
        var bad = new Particle { EmitterType = 4 };
        var ribbon = new Ribbon { Position = new Vector3(0, 0, 1), Gravity = 0.5f, Rows = 2, Cols = 2, EdgesPerSecond = 20, EdgeLifetime = 0.5f };
        var m = Load(Model(new[] { plane, sphere, spline, bad }, new[] { ribbon }, materialBlend: 6));
        Assert.Null(m.Modern);
        Assert.Equal((4, 1), (m.ParticleCount, m.RibbonCount));
        var fx = m.Effects!;
        Assert.Same(fx, m.Effects);                                       // read once
        Assert.Equal(3, fx.Particles.Count);                             // emitter type 4: the client refuses the model
        Assert.Contains(m.EffectNotes, n => n.StartsWith("particle emitter 3 has emitter type 4", StringComparison.Ordinal));
        var p = fx.Particles[0];
        var c = p.Classic!;
        Assert.Equal((0, 1, 1, 2, 4, new Vector3(1, 2, 3)), (p.Bone, p.Blend, p.EmitterType, p.Rows, p.Cols, p.Position));
        Assert.Equal(new[] { 0 }, p.Textures);
        Assert.True(p.Local);
        Assert.Equal((2, true, true, true, true), (c.ParticleType, c.Head, c.Tail, c.TailGrows, c.RandomSpinDirection));
        Assert.True(c.Burst);
        // the tracks keep their per-animation key ranges: Stand uses key 0, Walk key 1
        Assert.Equal(2f, M2EffectSimulation.Eval(p.Speed, Stand, m.GlobalSequences, 0));
        Assert.Equal(5f, M2EffectSimulation.Eval(p.Speed, Walk, m.GlobalSequences, 0));
        Assert.Equal(0f, M2EffectSimulation.Eval(p.EmissionRate, Walk, m.GlobalSequences, 1));
        Assert.Equal(new Vector3(0, 0, -1.5f), M2EffectSimulation.Eval(p.Gravity, Stand, m.GlobalSequences, Vector3.Zero));
        Assert.Equal(0.8f, M2EffectSimulation.Eval(p.Lifespan, Stand, m.GlobalSequences, 0));
        // colours are B, G, R, A in the file
        Assert.Equal(0.25f, c.MidPoint);
        Assert.Equal(new[] { new Vector4(255, 0, 0, 255), new Vector4(0, 255, 0, 128), new Vector4(0, 0, 255, 0) }, c.Colors);
        Assert.Equal(new[] { 0.1f, 0.4f, 0.2f }, c.Sizes);
        Assert.Equal(new[] { (0, 3), (4, 7) }, c.HeadCells);
        Assert.Equal(new[] { 1f, 2f }, c.CellRepeat);
        Assert.Equal(new[] { (1, 2), (5, 6) }, c.TailCells);
        Assert.Equal((0.5f, 10f, 0.6f, 0.8f, 1.2f), (p.TailLength, p.TwinkleSpeed, p.TwinklePercent, p.TwinkleMin, p.TwinkleMax));
        Assert.Equal((0.25f, 3.14f, new Vector3(1, 0, 0), 2f), (p.Drag, p.Spin, p.WindVector, p.WindTime));
        Assert.Equal(new[] { 0f, 0.25f, 1f }, p.Color.Times);            // the life tracks the statistics show
        Assert.Equal(new short[] { 32767, 16448, 0 }, p.Alpha.Values);
        // sphere and spline
        Assert.True(fx.Particles[1].Classic!.SphereUp);
        Assert.Equal(4, fx.Particles[2].SplinePoints.Length);
        Assert.Contains("particles that are models drawn as quads", fx.Particles[2].Approximated);
        Assert.Contains("spline emitters drawn along straight lines between their points", fx.Particles[2].Approximated);
        // the ribbon: its material's blend, the 1.12 rules
        var r = Assert.Single(fx.Ribbons);
        Assert.True(r.Classic);
        Assert.Equal((6, 20f, 0.5f, 0.5f, 2, 2), (r.Blend, r.EdgesPerSecond, r.EdgeLifetime, r.Gravity, r.Rows, r.Cols));
        Assert.Equal(new Vector3(0, 0, 1), r.Position);
        // the statistics' words
        var (summary, approx, leftOut) = M2ClassicEffects.Describe(m);
        Assert.Equal("3 particle emitters and 1 ribbon drawn by the viewer's own simulation of the 1.12 client's rules (close to the game's look, not exact)", summary);
        Assert.Contains("particles that are models drawn as quads (1)", approx);
        Assert.Single(leftOut);
    }

    [Fact]
    public void AModelWithoutEffectsOrWithBadIndicesHasNone()
    {
        Assert.Null(Load(Model(System.Array.Empty<Particle>())).Effects);
        var m = Load(Model(new[] { new Particle { Bone = 3 }, new Particle { Texture = 2 } }, new[] { new Ribbon { Materials = new ushort[] { 5 } } }));
        Assert.Null(m.Effects);
        Assert.Equal(3, m.EffectNotes.Count);
        Assert.Equal("", M2ClassicEffects.Describe(Load(Model(System.Array.Empty<Particle>()))).Summary);
    }

    [Fact]
    public void ParticlesAreBornInTheEmittersFrameTurned90DegreesAndLiveTheirLifespan()
    {
        // a plane 2 long and 0 wide at (1, 0, 0), straight up at speed 1, 20 a second living 0.5 s
        var p = new Particle { Position = new Vector3(1, 0, 0), AreaLength = Keys.Float(2), EmissionRate = Keys.Float(20), Lifespan = Keys.Float(0.5f) };
        var m = Load(Model(new[] { p }));
        var sim = new M2EffectSimulation(m.Effects!, m.GlobalSequences);
        for (int k = 0; k < 60; k++) sim.Step(Stand, NoBones, 1f / 30f);
        var ps = sim.Particles(0);
        Assert.InRange(ps.Count, 8, 12);
        foreach (var q in ps)
        {
            // the length runs along the emitter's X, which the 1.12 client turns onto the bone's Y
            Assert.True(Math.Abs(q.Position.X - 1) < 1e-4f, $"{q.Position}");
            Assert.InRange(q.Position.Y, -1.0001f, 1.0001f);
            Assert.InRange(q.Position.Z, 0f, 0.5001f);
            Assert.True(q.Age < 0.5f && q.Life == 0.5f);
            Near(Vector3.UnitZ, q.Velocity);
        }
        Assert.True(ps.Max(q => q.Position.Y) - ps.Min(q => q.Position.Y) > 0.5f);
        // the same seed gives the same particles
        var again = new M2EffectSimulation(m.Effects!, m.GlobalSequences);
        for (int k = 0; k < 60; k++) again.Step(Stand, NoBones, 1f / 30f);
        Assert.Equal(ps.Select(q => q.Position), again.Particles(0).Select(q => q.Position));
        // Walk switches the emission off through its key range (rate 20 / 0)
        var walking = Load(Model(new[] { new Particle { EmissionRate = Keys.PerAnimation(20, 0) } }));
        var ws = new M2EffectSimulation(walking.Effects!, walking.GlobalSequences);
        for (int k = 0; k < 30; k++) ws.Step(Walk, NoBones, 1f / 30f);
        Assert.Equal(0, ws.ParticleCount);
        for (int k = 0; k < 30; k++) ws.Step(Stand, NoBones, 1f / 30f);
        Assert.True(ws.ParticleCount > 0);
    }

    [Fact]
    public void SphereSplineGravityWindAndBurstFollowTheClient()
    {
        // sphere of radius 1..1 flying outwards in the XY plane (no elevation)
        var sphere = new Particle { EmitterType = 2, AreaLength = Keys.Float(1), AreaWidth = Keys.Float(1), HorizontalRange = Keys.Float(MathF.PI), EmissionRate = Keys.Float(30) };
        // the same flying straight up (flag 0x100)
        var up = new Particle { EmitterType = 2, Flags = 0x100, AreaLength = Keys.Float(1), AreaWidth = Keys.Float(1), HorizontalRange = Keys.Float(MathF.PI), EmissionRate = Keys.Float(30) };
        // a spline along X from the second to the third point; half of it (parameters 0..0.5) emits at half the rate
        var spline = new Particle { EmitterType = 3, AreaLength = Keys.Float(0), AreaWidth = Keys.Float(0.5f), EmissionRate = Keys.Float(40), Speed = Keys.Float(0),
            SplinePoints = new[] { new Vector3(-5, 0, 0), Vector3.Zero, new Vector3(4, 0, 0), new Vector3(9, 0, 0) } };
        // gravity 2 and a wind of 1 along X for 0.2 s, at speed 0
        var falling = new Particle { Speed = Keys.Float(0), Gravity = Keys.Float(2), EmissionRate = Keys.Float(30), Lifespan = Keys.Float(2),
            Params = new[] { 1f, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0.2f, 0, 0, 0, 0 } };
        // a burst: 50 at once when switched on (and 0 a second)
        var burst = new Particle { Flags = 0x8000, EmissionRate = Keys.Float(50), Lifespan = Keys.Float(10), Speed = Keys.Float(0) };
        var m = Load(Model(new[] { sphere, up, spline, falling, burst }));
        var sim = new M2EffectSimulation(m.Effects!, m.GlobalSequences);
        sim.Step(Stand, NoBones, 1f / 30f);
        Assert.Equal(52, sim.Particles(4).Count);                         // the burst of 50 and one frame of 50 a second (1.67, rounded)
        for (int k = 0; k < 29; k++) sim.Step(Stand, NoBones, 1f / 30f);
        foreach (var q in sim.Particles(0))
        {
            Assert.True(Math.Abs(q.Position.Z) < 1e-4f);
            Assert.True(q.Position.Length() >= 1f - 1e-4f);
            Near(Vector3.Normalize(q.Position), Vector3.Normalize(q.Velocity), 1e-3f);
        }
        foreach (var q in sim.Particles(1)) Near(Vector3.UnitZ, q.Velocity);
        // the spline: x between 0 and 2 (half of the 0..4 part), turned onto Y by the emitter frame
        Assert.InRange(sim.Particles(2).Count, 15, 25);
        foreach (var q in sim.Particles(2))
        {
            Assert.InRange(q.Position.Y, -1e-4f, 2.0001f);
            Assert.True(Math.Abs(q.Position.X) < 1e-4f);
        }
        // falling: z = -g t^2 / 2; the wind pushes it along the model's X (no turn: the wind is in the particles' space)
        foreach (var q in sim.Particles(3))
        {
            // it moved for whole steps since it was born a fraction of a step old
            float moved = q.Age - 1f / 30f;
            Assert.InRange(q.Position.Z, -q.Age * q.Age - 1e-4f, -(moved > 0 ? moved * moved : 0) + 1e-4f);
            Assert.InRange(q.Velocity.Z, -2 * q.Age - 1e-4f, -2 * Math.Max(0, moved) + 1e-4f);
            if (q.Age > 0.3f) Assert.InRange(q.Velocity.X, 0.15f, 0.25f);
        }
        Assert.InRange(sim.Particles(4).Count, 99, 101);                  // 50 more in the second: no second burst
    }

    [Fact]
    public void TheLookFollowsTheTwoKeyBlocksTheCellsAndTwinkling()
    {
        var p = new Particle
        {
            Lifespan = Keys.Float(1), MidPoint = 0.5f, Rows = 4, Cols = 4,
            Colors = new[] { ((byte)255, (byte)0, (byte)0, (byte)255), ((byte)0, (byte)255, (byte)0, (byte)255), ((byte)0, (byte)0, (byte)255, (byte)0) },
            Sizes = new[] { 1f, 3f, 2f }, LifespanCells = new ushort[] { 0, 3, 1 }, DecayCells = new ushort[] { 8, 4, 2 },
        };
        var m = Load(Model(new[] { p, new Particle { Params = new[] { 1f, 10, 0.5f, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } } }));
        var e = m.Effects!.Particles[0];
        M2ParticleLook At(float age) => M2EffectSimulation.Appearance(e, new M2Particle { Age = age, Life = 1 });
        // birth: the first key (the fraction starts at 0.005)
        var a = At(0);
        Assert.True(a.Visible);
        Assert.InRange(a.Color.X, 0.99f, 1f);
        Assert.InRange(a.Size.X, 1.005f, 1.015f);
        Assert.Equal(0, a.Cell);
        // the middle point: the end of block 1
        var b = At(0.5f);
        Assert.InRange(b.Color.Y, 0.99f, 1f);
        Assert.InRange(b.Size.X, 2.985f, 2.995f);
        Assert.Equal(3, b.Cell);                                         // cells 0..3 once
        // block 2 counts down from 8 to 4 twice (repeat 2)
        Assert.Equal(6, At(0.6f).Cell);                                  // fraction 0.2 x 0.99 + 0.005 = 0.203: x2 = 0.406 -> 9 - 5 x 0.406
        Assert.InRange(At(1f).Color.Z, 0.99f, 1f);
        Assert.InRange(At(1f).Color.W, 0f, 0.01f);
        Assert.InRange(At(1f).Size.X, 2f, 2.01f);
        // the cell rule of ParticleKeyBlock_SetTileRange
        Assert.Equal(3, M2EffectSimulation.Cell((0, 3), 0.995f));
        Assert.Equal(4, M2EffectSimulation.Cell((8, 4), 0.995f));
        // twinkling at 50 %: about half of the particles hide at any moment
        var t = m.Effects.Particles[1];
        int shown = Enumerable.Range(0, 400).Count(i => M2EffectSimulation.Appearance(t, new M2Particle { Age = 0.3f, Life = 1, Seed = (uint)i }).Visible);
        Assert.InRange(shown, 120, 280);
    }

    [Fact]
    public void ARibbonSpansTheBonesYAxisAndPositiveGravityLiftsItsEdges()
    {
        var r = new Ribbon { Position = new Vector3(1, 0, 0), EdgesPerSecond = 10, EdgeLifetime = 1, Gravity = 0.5f };
        var m = Load(Model(System.Array.Empty<Particle>(), new[] { r }));
        var sim = new M2EffectSimulation(m.Effects!, m.GlobalSequences);
        for (int k = 0; k < 60; k++) sim.Step(Stand, NoBones, 1f / 30f);
        var edges = sim.Edges(0);
        Assert.InRange(edges.Count, 9, 13);
        Near(new Vector3(1, 0.5f, 0), edges[0].Top);
        Near(new Vector3(1, -0.5f, 0), edges[0].Bottom);
        foreach (var x in edges.Skip(1))
        {
            // z = gravity x age^2 (CRibbonEmitter_Update)
            Assert.Equal(0.5f * x.Age * x.Age, x.Top.Z, 2);
            Assert.Equal(x.Top.Z, x.Bottom.Z, 4);
        }
    }

    [Fact]
    public void ADuplicatedVisualsEffectFollowsItsSizeAndTint()
    {
        var plane = new Particle { AreaLength = Keys.Float(1), AreaWidth = Keys.Float(0.5f), SpeedVariation = Keys.Float(0.3f), VerticalRange = Keys.Float(0.4f),
            Gravity = Keys.Float(1), Position = new Vector3(0.2f, 0.1f, 0.5f), Colors = new[] { ((byte)200, (byte)100, (byte)50, (byte)255), ((byte)200, (byte)100, (byte)50, (byte)255), ((byte)200, (byte)100, (byte)50, (byte)0) },
            Params = new[] { 1f, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.5f, 0, 0, 1f, 0, 0, 0, 0 } };
        var sphere = new Particle { EmitterType = 2, AreaLength = Keys.Float(0.5f), AreaWidth = Keys.Float(1), HorizontalRange = Keys.Float(3), VerticalRange = Keys.Float(1) };
        var spline = new Particle { EmitterType = 3, AreaLength = Keys.Float(0.25f), AreaWidth = Keys.Float(0.75f), VerticalRange = Keys.Float(0.5f), HorizontalRange = Keys.Float(0.3f),
            SplinePoints = new[] { new Vector3(-1, 0, 0), Vector3.Zero, new Vector3(1, 1, 0), new Vector3(2, 1, 0) } };
        var ribbon = new Ribbon { Position = new Vector3(0.3f, 0, 0.2f), Gravity = -0.4f, Color = Keys.Vec3(new Vector3(0.8f, 0.6f, 0.4f)) };
        var data = Model(new[] { plane, sphere, spline }, new[] { ribbon });
        var tint = new Vector3(0.5f, 1f, 0.25f);
        var copy = Load(SpellVisuals.Variant(data, 1.5f, tint, _ => null));
        var orig = Load(data);
        var (fo, fc) = (orig.Effects!, copy.Effects!);
        // the spline's parameters stay (they are positions 0..1 along the curve), the speed variation is a fraction
        Assert.Equal(0.75f, M2EffectSimulation.Eval(fc.Particles[2].AreaWidth, Stand, copy.GlobalSequences, 0));
        Assert.Equal(0.3f, M2EffectSimulation.Eval(fc.Particles[0].SpeedVariation, Stand, copy.GlobalSequences, 0));
        Assert.Equal(-0.6f, fc.Ribbons[0].Gravity, 4);
        Near(new Vector3(0.75f, 0, 0), fc.Particles[0].WindVector);
        // simulated with the same seed, every particle and ribbon edge of the copy is 1.5 times as far
        var so = new M2EffectSimulation(fo, orig.GlobalSequences);
        var sc = new M2EffectSimulation(fc, copy.GlobalSequences);
        for (int k = 0; k < 45; k++)
        {
            so.Step(Stand, NoBones, 1f / 30f);
            sc.Step(Stand, NoBones, 1f / 30f);
        }
        for (int i = 0; i < 3; i++)
        {
            var (a, b) = (so.Particles(i), sc.Particles(i));
            Assert.Equal(a.Count, b.Count);
            Assert.True(a.Count > 3);
            for (int k = 0; k < a.Count; k++)
            {
                Near(a[k].Position * 1.5f, b[k].Position, 1e-3f);
                var (la, lb) = (M2EffectSimulation.Appearance(fo.Particles[i], a[k]), M2EffectSimulation.Appearance(fc.Particles[i], b[k]));
                Assert.Equal(la.Size.X * 1.5f, lb.Size.X, 3);
                if (i == 0)
                {
                    Assert.Equal(la.Color.X * tint.X, lb.Color.X, 2);
                    Assert.Equal(la.Color.Y * tint.Y, lb.Color.Y, 2);
                    Assert.Equal(la.Color.Z * tint.Z, lb.Color.Z, 2);
                }
            }
        }
        Assert.Equal(so.Edges(0).Count, sc.Edges(0).Count);
        for (int k = 0; k < so.Edges(0).Count; k++)
        {
            Near(so.Edges(0)[k].Top * 1.5f, sc.Edges(0)[k].Top, 1e-3f);
            Near(so.Edges(0)[k].Bottom * 1.5f, sc.Edges(0)[k].Bottom, 1e-3f);
        }
        Near(new Vector3(0.4f, 0.6f, 0.1f), M2Anim.Eval(fc.Ribbons[0].Color, Stand, copy.GlobalSequences, Vector3.Zero));
    }

    [Fact]
    public void ParticleBlendModesUseTheMaterialsNumbering()
    {
        // M2Scene_DrawParticleBatch hands the particle's blend to the material blend state: 1 alpha key, 3 and 4 add
        Assert.Equal((BlendKind.AlphaKey, "alpha key"), MaterialBlends.OfParticle(1));
        Assert.Equal((BlendKind.Additive, "no-alpha add"), MaterialBlends.OfParticle(3));
        Assert.Equal((BlendKind.Additive, "add"), MaterialBlends.OfParticle(4));
        Assert.Equal((BlendKind.Modulate, "mod"), MaterialBlends.OfParticle(5));
        var data = Model(new[] { new Particle { Blend = 1 } });
        var uses = MaterialBlends.Find(@"Spells\Synthetic.m2", data, @"Spells\Synthetic_Fx.blp");
        Assert.Equal(new[] { "particle alpha key" }, uses.Select(u => $"{u.Where} {u.BlendName}"));
        Assert.Null(MaterialBlends.AdditiveNote(uses));                   // an alpha-key particle is not a glow
    }
}
