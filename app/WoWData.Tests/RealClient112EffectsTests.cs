using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.M2;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>A test on the real 1.12 client named by WOWDATA_CLIENT_DIR (opt-in: skipped when it is not set; read only).</summary>
public sealed class RealClient112FactAttribute : FactAttribute
{
    public RealClient112FactAttribute()
    {
        var d = Environment.GetEnvironmentVariable("WOWDATA_CLIENT_DIR");
        if (string.IsNullOrEmpty(d)) Skip = "opt-in: set WOWDATA_CLIENT_DIR to a 1.12 client";
        else if (!File.Exists(Path.Combine(d, "Data", "model.MPQ"))) Skip = $"WOWDATA_CLIENT_DIR ({d}) has no Data\\model.MPQ";
    }
}

/// <summary>
/// Particles and ribbons of your 1.12 game on the real client (opt-in: WOWDATA_CLIENT_DIR; read only): every creature
/// and spell-effect model's emitters and ribbons are read by <see cref="M2ClassicEffects"/> and simulated for two seconds
/// of their first animation to finite positions. The counts were measured on the stock 1.12.1 (5875) client.
/// </summary>
[Collection(PerformanceCollection.Name)]
public class RealClient112EffectsTests
{
    private readonly ITestOutputHelper _out;

    public RealClient112EffectsTests(ITestOutputHelper output) => _out = output;

    [RealClient112Fact]
    public void EveryCreatureAndSpellEffectModelsParticlesAndRibbonsReadAndSimulate()
    {
        var client = TestEnv.Client;
        var models = client.Names().Values.Select(f => f.Path)
            .Where(p => p.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) &&
                        (p.StartsWith(@"Creature\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var sw = Stopwatch.StartNew();
        int withParticles = 0, withRibbons = 0, emitters = 0, ribbons = 0, readModels = 0, simulated = 0, leftOut = 0, creatureFx = 0, spellFx = 0;
        var failures = new List<string>();
        var blends = new SortedDictionary<int, int>();
        var types = new SortedDictionary<int, int>();
        var ptypes = new SortedDictionary<int, int>();
        var flagBits = new SortedDictionary<int, int>();
        var approx = new Dictionary<string, int>();
        var notes = new List<string>();
        int twinkleHidden = 0, twinkleSize = 0, wind = 0, local = 0, xy = 0, burst = 0, randomCells = 0, repeat = 0, ribbonGravityUp = 0, ribbonGravityDown = 0, ribbonCells = 0, ribbonBlendOther = 0, maxParticles = 0;
        var ribbonBlends = new SortedDictionary<int, int>();
        foreach (var path in models)
        {
            var data = client.Read(path);
            if (data == null) continue;
            M2Model m;
            M2EffectSet? fx;
            try
            {
                m = M2Model.Load(data);
                if (m.ParticleCount + m.RibbonCount == 0) continue;
                fx = m.Effects;
                readModels++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{path}: {e.Message}");
                continue;
            }
            if (m.ParticleCount > 0) withParticles++;
            if (m.RibbonCount > 0) withRibbons++;
            if (path.StartsWith(@"Creature\", StringComparison.OrdinalIgnoreCase)) creatureFx++;
            else spellFx++;
            emitters += m.ParticleCount;
            ribbons += m.RibbonCount;
            leftOut += m.EffectNotes.Count;
            notes.AddRange(m.EffectNotes.Select(n => $"{path}: {n}"));
            if (fx == null) continue;
            foreach (var p in fx.Particles)
            {
                var c = p.Classic!;
                blends[p.Blend] = blends.GetValueOrDefault(p.Blend) + 1;
                types[p.EmitterType] = types.GetValueOrDefault(p.EmitterType) + 1;
                ptypes[c.ParticleType] = ptypes.GetValueOrDefault(c.ParticleType) + 1;
                for (int bit = 0; bit < 32; bit++)
                    if ((p.Flags & (1u << bit)) != 0) flagBits[bit] = flagBits.GetValueOrDefault(bit) + 1;
                if (p.TwinklePercent < 1) twinkleHidden++;
                if (p.TwinkleMax != p.TwinkleMin) twinkleSize++;
                if (p.WindTime > 0 && p.WindVector != Vector3.Zero) wind++;
                if (p.Local) local++;
                if (p.XyQuads) xy++;
                if (c.Burst) burst++;
                if (c.CellRepeat.Any(r => r != 1)) repeat++;
                if (c.HeadCells.Any(h => h.First != h.Last) || c.TailCells.Any(h => h.First != h.Last)) randomCells++;
                foreach (var a in p.Approximated) approx[a] = approx.GetValueOrDefault(a) + 1;
            }
            foreach (var r in fx.Ribbons)
            {
                if (r.Gravity > 0) ribbonGravityUp++;
                if (r.Gravity < 0) ribbonGravityDown++;
                if (r.Rows * r.Cols > 1) ribbonCells++;
                ribbonBlends[r.Blend] = ribbonBlends.GetValueOrDefault(r.Blend) + 1;
                if (r.Blend is < 0 or > 6) ribbonBlendOther++;
                foreach (var a in r.Approximated) approx[a] = approx.GetValueOrDefault(a) + 1;
            }
            // two seconds of the first animation (Stand when there is one), 30 steps a second
            try
            {
                var sim = new M2EffectSimulation(fx, m.GlobalSequences);
                var mats = new Matrix4x4[Math.Max(1, m.Bones.Length)];
                int seq = m.Sequences.Length == 0 ? 0 : Enumerable.Range(0, m.Sequences.Length).FirstOrDefault(i => m.Sequences[i].AnimId == 0);
                var s = m.Sequences.Length == 0 ? null : m.Sequences[seq];
                for (int k = 0; k < 60; k++)
                {
                    uint t = s == null || s.Duration == 0 ? 0 : (uint)(k * 33 % s.Duration);
                    var time = new M2Anim.Time(seq, s?.Start ?? 0, t, k * 33.3);
                    M2Anim.BoneMatrices(m, time, mats);
                    sim.Step(time, mats, 1f / 30f);
                }
                bool finite = true;
                for (int i = 0; i < fx.Particles.Count; i++)
                    foreach (var q in sim.Particles(i))
                    {
                        var look = M2EffectSimulation.Appearance(fx.Particles[i], q);
                        if (!float.IsFinite(q.Position.X + q.Position.Y + q.Position.Z) || !float.IsFinite(look.Size.X + look.Color.W)) finite = false;
                    }
                for (int i = 0; i < fx.Ribbons.Count; i++)
                    foreach (var x in sim.Edges(i))
                        if (!float.IsFinite(x.Top.X + x.Top.Y + x.Top.Z + x.Bottom.X + x.Bottom.Y + x.Bottom.Z)) finite = false;
                maxParticles = Math.Max(maxParticles, sim.ParticleCount);
                if (finite) simulated++;
                else failures.Add($"{path}: a particle or ribbon edge is not finite after 2 s");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{path}: simulation: {e.Message}");
            }
        }
        _out.WriteLine($"{models.Count} creature and spell-effect models; {readModels} have particles or ribbons ({creatureFx} creature, {spellFx} spell effect): {withParticles} with particles ({emitters} emitters), {withRibbons} with ribbons ({ribbons} ribbons); {simulated} simulated; {leftOut} left out; {sw.Elapsed.TotalSeconds:0.0} s");
        _out.WriteLine("particle blends: " + string.Join(", ", blends.Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine("ribbon blends: " + string.Join(", ", ribbonBlends.Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine("emitter types: " + string.Join(", ", types.Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine("particle types: " + string.Join(", ", ptypes.Select(kv => $"{kv.Key}={kv.Value}")));
        _out.WriteLine("flag bits: " + string.Join(", ", flagBits.Select(kv => $"0x{1u << kv.Key:x}={kv.Value}")));
        _out.WriteLine($"twinkle hides {twinkleHidden}, twinkle sizes {twinkleSize}, wind {wind}, local {local}, xy quads {xy}, burst {burst}, cell ranges {randomCells}, cell repeats {repeat}; ribbons: gravity up {ribbonGravityUp} down {ribbonGravityDown}, cells {ribbonCells}, other blends {ribbonBlendOther}; most live particles {maxParticles}");
        foreach (var a in approx.OrderByDescending(x => x.Value)) _out.WriteLine($"approximated: {a.Key} ({a.Value})");
        foreach (var n in notes.Take(20)) _out.WriteLine("note: " + n);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        // measured on the stock 1.12.1 (5875) client
        Assert.Equal(1112, models.Count);
        Assert.Equal((668, 109, 559), (readModels, creatureFx, spellFx));
        Assert.Equal((653, 2753, 127, 387), (withParticles, emitters, withRibbons, ribbons));
        Assert.Equal((668, 0), (simulated, leftOut));
        Assert.Equal("1=26, 2=433, 4=2293, 5=1", string.Join(", ", blends.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal("2=47, 4=328, 6=12", string.Join(", ", ribbonBlends.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal("1=1671, 2=1059, 3=23", string.Join(", ", types.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal("0=1900, 1=764, 2=89", string.Join(", ", ptypes.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal((36, 488, 0, 237), (twinkleHidden, twinkleSize, wind, burst));
    }

    /// <summary>The app's renderer (`--render`, as "wds models render") draws the Fire Elemental's fire, and leaves it out
    /// with --no-particles.</summary>
    [RealClient112Fact]
    public void TheRendererDrawsTheFireElementalsFireUnlessNoParticles()
    {
        var exe = UiSmokeTests.FindExe() ?? throw new InvalidOperationException("WoWDataStudio.exe not built");
        var root = TestEnv.TempDir("render_fx");
        try
        {
            int Orange(bool particles)
            {
                var png = Path.Combine(root, particles ? "fx.png" : "plain.png");
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in new[] { "--client", TestEnv.ClientDir, "--home", Path.Combine(root, "home"), "--render", @"Creature\FireElemental\FireElemental.m2", png, "--size", "320x240", "--original" })
                    psi.ArgumentList.Add(a);
                if (!particles) psi.ArgumentList.Add("--no-particles");
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEndAsync();
                Assert.True(p.WaitForExit(120_000), "render timed out");
                Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {p.StandardError.ReadToEnd()}");
                _out.WriteLine(o.Result.Trim());
                var im = WoWData.Core.Imaging.Png.Load(png);
                int n = 0;
                for (int i = 0; i < im.Pixels.Length; i += 4) if (im.Pixels[i] > 200 && im.Pixels[i + 1] > 90 && im.Pixels[i + 2] < 120) n++;   // fire: bright orange
                return n;
            }
            int fire = Orange(true), plain = Orange(false);
            _out.WriteLine($"orange pixels: {fire} with particles, {plain} without");
            Assert.True(fire > plain + 1500, $"orange pixels: {fire} with particles, {plain} without");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
