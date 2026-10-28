using System.Linq;
using System.Numerics;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// Drawing your 1.12 game's particles did not change what another version's draw: the M3 synthetic model's emitter and
/// ribbon, simulated for three seconds under a moving bone, give exactly the numbers the code before the change gave
/// (measured on commit 6a96f3e: the same test run against that build).
/// </summary>
public class ModernEffectsUnchangedTests
{
    private readonly ITestOutputHelper _out;

    public ModernEffectsUnchangedTests(ITestOutputHelper output) => _out = output;

    public static (double Particles, double Looks, double Edges, int Count) Fingerprint()
    {
        var m = M2ModernReader.Load(SyntheticClients.CubeM2v274(effects: true), id => SyntheticClients.M3File(id));
        var fx = m.Modern!.Effects!;
        var sim = new M2EffectSimulation(fx, m.GlobalSequences);
        double ps = 0, looks = 0, edges = 0;
        int count = 0;
        for (int k = 0; k < 90; k++)
        {
            var time = new M2Anim.Time(0, 0, (uint)(k * 33 % 1000), k * 33.3);
            var bone = Matrix4x4.CreateRotationZ(k * 0.05f) * Matrix4x4.CreateTranslation(0.02f * k, 0, 0.01f * k);
            sim.Step(time, new[] { bone }, 1f / 30f);
            foreach (var q in sim.Particles(0))
            {
                ps += q.Position.X + 2 * q.Position.Y + 3 * q.Position.Z + q.Age;
                var (c, s, cell) = M2EffectSimulation.Look(fx.Particles[0], q);
                var look = M2EffectSimulation.Appearance(fx.Particles[0], q);
                Assert.Equal((c, s, cell, cell, true), (look.Color, look.Size, look.Cell, look.TailCell, look.Visible));
                looks += c.X + c.Y + c.Z + c.W + s.X + s.Y + cell;
                count++;
            }
            foreach (var e in sim.Edges(0)) edges += e.Top.X + 2 * e.Top.Y + 3 * e.Top.Z + e.Bottom.X + e.Bottom.Z + e.Age;
        }
        return (ps, looks, edges, count);
    }

    [Fact]
    public void AnotherVersionsEffectsSimulateExactlyAsBefore()
    {
        var f = Fingerprint();
        _out.WriteLine($"{f.Particles:R} {f.Looks:R} {f.Edges:R} {f.Count}");
        Assert.Null(SyntheticClients.CubeM2v274(effects: true) is { } d ? M2ModernReader.Load(d, id => SyntheticClients.M3File(id)).Modern!.Effects!.Particles[0].Classic : null);
        Assert.Equal((Expected.Particles, Expected.Looks, Expected.Edges, Expected.Count), f);
    }

    private static readonly (double Particles, double Looks, double Edges, int Count) Expected = (4926.166097164154, 1889.455126285553, 4157.209773302078, 825);
}
