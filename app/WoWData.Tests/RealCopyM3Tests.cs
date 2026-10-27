using System.Diagnostics;
using System.Linq;
using System.Numerics;
using WoWData.Core.Casc;
using WoWData.Core.Db2;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.M2.Modern;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// M3 on the real "1.60" Classic beta copy (opt-in: WOWDATA_CLIENT_COPIES; read only): every distinct creature model is
/// read, every one of its animations is made playable (an external one's .anim file read) and evaluated at its start,
/// middle and end to finite bone matrices; the skeleton files and their parents are read; the particle emitters and
/// ribbons of the creature and spell-effect models parse. Every number was measured on the copy of 2026-10-20 (a
/// snapshot: the build cannot change).
/// </summary>
[Collection(PerformanceCollection.Name)]
public class RealCopyM3Tests
{
    private readonly ITestOutputHelper _out;

    public RealCopyM3Tests(ITestOutputHelper output) => _out = output;

    private static byte[]? Local(CascStorage st, uint f)
    {
        try { return st.State(f) == CascFileState.Local ? st.ReadFileDataId(f) : null; }
        catch (Exception e) when (e is CascMissingException or CascEncryptedException) { return null; }
    }

    private sealed record Anims(int Models, int Sequences, int Evaluated, int External, int Loaded, int NoFile, int NoKeys, int Unreadable, int Skeletons, int SkeletonsRead, int ParentSequences, long MaxLoadedBytes);

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void EverySequenceOfEveryCreatureModelPlaysAndTheirAnimFilesLoad()
    {
        using var st = CascStorage.Open(ClientCopyFactAttribute.PathOf(RealCopyOpenTests.Beta)!);
        var cat = new Catalog160(st, Db2Catalog.ForStorage(st), st.Build);
        var creature = cat.CreatureDisplayIds().Select(i => cat.CreatureDisplay(i)!.Model).Where(m => m != 0).Distinct().OrderBy(x => x).ToList();
        int models = 0, seqs = 0, evaluated = 0, external = 0, loaded = 0, noFile = 0, noKeys = 0, unreadable = 0, skel = 0, skelRead = 0, parent = 0;
        long maxBytes = 0;
        var time = Stopwatch.StartNew();
        var failures = new List<string>();
        foreach (var f in creature)
        {
            var data = Local(st, f);
            if (data == null || !M2ModernReader.IsChunked(data)) continue;
            M2Model m;
            try { m = M2ModernReader.Load(data, id => st.ReadFileDataId(id)); }
            catch (M2Exception e) { failures.Add($"#{f}: {e.Message}"); continue; }
            models++;
            var mi = m.Modern!;
            if (mi.SkeletonFile != 0)
            {
                skel++;
                if (mi.SkeletonRead) skelRead++;
                parent += mi.ParentSequences;
            }
            var mats = new Matrix4x4[Math.Max(1, m.Bones.Length)];
            for (int s = 0; s < m.Sequences.Length; s++)
            {
                seqs++;
                if (mi.Animations is { } ext && ext.IsExternal(s))
                {
                    external++;
                    var r = ext.Ensure(s);
                    switch (r.State)
                    {
                        case ExternalState.Loaded: loaded++; break;
                        case ExternalState.NoFile: noFile++; break;
                        case ExternalState.NoKeys: noKeys++; break;
                        default: unreadable++; failures.Add($"#{f} sequence {s}: {r.Why}"); break;
                    }
                    maxBytes = Math.Max(maxBytes, ext.LoadedBytes);
                }
                var q = m.Sequences[s];
                bool ok = true;
                foreach (var t in new[] { 0u, q.Duration / 2, q.Duration })
                {
                    M2Anim.BoneMatrices(m, new M2Anim.Time(s, q.Start, t, 0), mats);
                    foreach (var x in mats)
                        if (!float.IsFinite(x.M11) || !float.IsFinite(x.M22) || !float.IsFinite(x.M33) || !float.IsFinite(x.M41) || !float.IsFinite(x.M42) || !float.IsFinite(x.M43)) ok = false;
                }
                if (ok) evaluated++;
            }
        }
        var res = new Anims(models, seqs, evaluated, external, loaded, noFile, noKeys, unreadable, skel, skelRead, parent, maxBytes);
        _out.WriteLine($"{res} in {time.Elapsed.TotalSeconds:0.0} s; external sequences that load: {100.0 * loaded / Math.Max(1, external):0.00} %");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        // 998 models (19 of the 1,017 are not on this computer); 48,787 sequences: the 43,243 of the models and 5,544 of
        // the 22 skeleton files (1,377 of them from 4 parent skeletons); 6,529 in .anim files (5,639 named by models,
        // 890 by skeletons): 6,523 load; 2 have no .anim file named, 4 name an .anim file of 16-32 bytes without keys
        Assert.Equal(new Anims(998, 48787, 48787, 6529, 6523, 2, 4, 0, 22, 22, 1377, res.MaxLoadedBytes), res);
        Assert.True(res.MaxLoadedBytes <= ModernAnimations.DefaultBudget + (2L << 20), $"{res.MaxLoadedBytes} bytes of keys kept for one model");
    }

    private sealed record Fx(int Models, int WithEffects, int Particles, int Ribbons, int Multi, int Spline, int Blend7, int LeftOut);

    private static Fx Effects(CascStorage st, IEnumerable<uint> files)
    {
        int models = 0, with = 0, particles = 0, ribbons = 0, multi = 0, spline = 0, blend7 = 0, leftOut = 0;
        foreach (var f in files)
        {
            var data = Local(st, f);
            if (data == null || !M2ModernReader.IsChunked(data)) continue;
            var m = M2ModernReader.Load(data, id => st.ReadFileDataId(id));
            models++;
            leftOut += m.Modern!.Notes.Count(n => n.Contains("left out", StringComparison.Ordinal) && (n.Contains("particle", StringComparison.Ordinal) || n.Contains("ribbon", StringComparison.Ordinal)));
            if (m.Modern.Effects is not { } fx) continue;
            with++;
            particles += fx.Particles.Count;
            ribbons += fx.Ribbons.Count;
            multi += fx.Particles.Count(p => p.MultiTexture);
            spline += fx.Particles.Count(p => p.EmitterType == 3);
            blend7 += fx.Particles.Count(p => p.Blend == 7);
        }
        return new Fx(models, with, particles, ribbons, multi, spline, blend7, leftOut);
    }

    [ClientCopyFact(RealCopyOpenTests.Beta)]
    public void TheParticleEmittersAndRibbonsOfCreatureAndSpellModelsParse()
    {
        using var st = CascStorage.Open(ClientCopyFactAttribute.PathOf(RealCopyOpenTests.Beta)!);
        var cat = new Catalog160(st, Db2Catalog.ForStorage(st), st.Build);
        var creature = cat.CreatureDisplayIds().Select(i => cat.CreatureDisplay(i)!.Model).Where(m => m != 0).Distinct().OrderBy(x => x).ToList();
        var spells = cat.SpellEffects().Select(x => x.Model).Where(m => m != 0).Distinct().OrderBy(x => x).ToList();
        var c = Effects(st, creature);
        var s = Effects(st, spells);
        _out.WriteLine($"creature: {c}");
        _out.WriteLine($"spell effects: {s}");
        // every record parses (none left out); multi-texture particles, spline emitters and blend mode 7 are counted
        Assert.Equal(new Fx(998, 303, 1795, 83, 271, 10, 124, 0), c);
        Assert.Equal(new Fx(2320, 1818, 7362, 784, 2253, 41, 1234, 0), s);
        // the simulation of every creature model's effects runs one second without a non-finite particle
        int bad = 0;
        foreach (var f in creature)
        {
            var data = Local(st, f);
            if (data == null || !M2ModernReader.IsChunked(data)) continue;
            var m = M2ModernReader.Load(data, id => st.ReadFileDataId(id));
            if (m.Modern!.Effects is not { } fx) continue;
            var sim = new M2EffectSimulation(fx, m.GlobalSequences);
            var bones = new Matrix4x4[Math.Max(1, m.Bones.Length)];
            int seq = WoWDataStudio.Rendering.ModelScene.DefaultSequence(m);
            for (int k = 0; k < 30; k++)
            {
                var t = m.Sequences.Length == 0 ? new M2Anim.Time(0, 0, 0, k * 33) : new M2Anim.Time(seq, m.Sequences[seq].Start, (uint)Math.Min(k * 33, m.Sequences[seq].Duration), k * 33);
                M2Anim.BoneMatrices(m, t, bones);
                sim.Step(t, bones, 1f / 30f);
            }
            for (int i = 0; i < fx.Particles.Count; i++)
                foreach (var p in sim.Particles(i))
                    if (!float.IsFinite(p.Position.X) || !float.IsFinite(p.Position.Y) || !float.IsFinite(p.Position.Z)) bad++;
        }
        Assert.Equal(0, bad);
    }
}
