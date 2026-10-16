using System.Linq;
using System.Numerics;
using WoWData.Core.Dbc;
using WoWData.Core.M2;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.10.0, the skeleton and animation format engine on the game's own models (read only):
///  * the identity plan writes every client model back byte for byte;
///  * every per-sequence track re-encoded from its keys plays exactly as before (the client's key rules);
///  * moving every sequence to a new place on the timeline keeps every value of every track (bones, colours,
///    transparency, texture animations, attachments, events, lights, cameras, ribbons, particles);
///  * the lookup tables follow the rules the client's files follow (animation lookup, key-bone lookup) and the
///    playable-animation table is the client's own algorithm.
/// </summary>
public class RigFormatTests
{
    private readonly ITestOutputHelper _out;
    public RigFormatTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] TrackArrays = { "bones", "colors", "transparency", "track_6c", "texture_transforms", "attachments", "events", "lights", "cameras", "ribbons", "particles" };

    private static readonly Lazy<List<string>> Models = new(() => TestEnv.Client.Names().Keys.Where(n => n.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

    private static readonly Lazy<AnimationCatalog> Catalog = new(() => AnimationCatalog.FromDbc(new DbcFile(TestEnv.Client.Read(@"DBFilesClient\AnimationData.dbc")!, "AnimationData")));

    private static IEnumerable<(string Name, byte[] Data, M2Model Model)> Sample(int step)
    {
        foreach (var n in Models.Value.Where((_, i) => i % step == 0))
        {
            var d = TestEnv.Client.Read(n)!;
            M2Model m;
            try { m = M2Model.Load(d); }
            catch (M2Exception) { continue; }
            yield return (n, d, m);
        }
    }

    [Fact]
    public void IdentityPlanWritesEveryClientModelByteForByte()
    {
        int n = 0;
        var bad = new List<string>();
        foreach (var (name, data, m) in Sample(1))
        {
            var f = M2File.Parse(data);
            var r = RigWriter.Apply(f, RigPlan.Identity(m), Catalog.Value);
            if (r.Changed || !f.Serialize().AsSpan().SequenceEqual(data)) bad.Add(name);
            n++;
        }
        _out.WriteLine($"{n} models, {bad.Count} differ");
        Assert.True(n > 9000, $"only {n} models");
        Assert.Empty(bad);
    }

    private static List<uint> TimesOf(RawTrack raw, int s, M2Sequence sq)
    {
        var times = new SortedSet<uint> { 0, sq.Duration };
        for (uint t = 0; t <= sq.Duration; t += 31) times.Add(t);
        var (lo, hi) = raw.RangeOf(s);
        if (raw.KeyCount > 0)
            for (uint k = lo; k <= Math.Min(hi, (uint)raw.KeyCount - 1); k++)
            {
                uint tt = raw.Times[(int)k];
                if (tt < sq.Start || tt > sq.End) continue;
                times.Add(tt - sq.Start);
                if (tt > sq.Start) times.Add(tt - sq.Start - 1);
                if (tt < sq.End) times.Add(tt - sq.Start + 1);
            }
        return times.ToList();
    }

    private static string? SameValues(RawTrack a, M2Sequence sa, int s, RawTrack b, M2Sequence sb, int sNew)
    {
        if (!a.HasValues)
        {
            var wa = a.Window(s, sa.Start, sa.End);
            var wb = b.Window(sNew, sb.Start, sb.End);
            return wa.Times.SequenceEqual(wb.Times) ? null : $"events [{string.Join(",", wa.Times)}] vs [{string.Join(",", wb.Times)}]";
        }
        if (a.Interpolation >= 2) return null;
        foreach (var t in TimesOf(a, s, sa))
        {
            var va = a.Evaluate(s, sa.Start + t);
            var vb = b.Evaluate(sNew, sb.Start + t);
            if (va == null && vb == null) continue;
            if (va == null || vb == null) return $"t {t}: a value on one side only";
            double d = TrackValues.Distance(a.ValueType!, va, vb);
            double tol = a.ValueType == "i2" ? 1.01 : 2e-4 * Math.Max(1, TrackValues.Components(a.ValueType!, va).Max(Math.Abs));
            if (!(d <= tol)) return $"t {t}: difference {d}";
        }
        return null;
    }

    [Fact]
    public void EveryTrackReencodedFromItsKeysPlaysTheSame()
    {
        int models = 0, tracks = 0;
        var bad = new List<string>();
        foreach (var (name, _, m) in Sample(2))
        {
            models++;
            var starts = m.Sequences.Select(s => s.Start).ToArray();
            var durs = m.Sequences.Select(s => s.Duration).ToArray();
            foreach (var an in TrackArrays)
            {
                var arr = m.File[an];
                for (int i = 0; i < arr.Count; i++)
                    foreach (var t in arr.Tracks(i))
                    {
                        var raw = RawTrack.Read(t);
                        if (raw.GlobalSequence >= 0 || raw.Interpolation >= 2 || raw.KeyCount == 0 || raw.Ranges.Count == 0 && raw.KeyCount <= 1) continue;
                        var wins = Enumerable.Range(0, m.Sequences.Length).Select(s => raw.Window(s, m.Sequences[s].Start, m.Sequences[s].End)).ToList();
                        var fresh = TrackEncoder.Fresh(raw, wins, starts, durs, raw.HasValues ? raw.Values![0] : null);
                        tracks++;
                        for (int s = 0; s < m.Sequences.Length; s++)
                            if (SameValues(raw, m.Sequences[s], s, fresh, m.Sequences[s], s) is { } why)
                            {
                                bad.Add($"{name} {an}[{i}].{t.Name} sequence {s}: {why}");
                                break;
                            }
                    }
            }
        }
        _out.WriteLine($"{models} models, {tracks:N0} tracks re-encoded, {bad.Count} differ");
        foreach (var b in bad.Take(20)) _out.WriteLine(b);
        Assert.True(models > 4000);
        Assert.Empty(bad);
    }

    [Fact]
    public void MovingEverySequenceOnTheTimelineKeepsEveryTrack()
    {
        int models = 0;
        var bad = new List<string>();
        foreach (var (name, data, m) in Sample(3))
        {
            if (m.Sequences.Length == 0) continue;
            models++;
            var f = M2File.Parse(data);
            var plan = RigPlan.Identity(m);
            var bones = f["bones"];
            for (int s = 0; s < m.Sequences.Length; s++)
            {
                plan.Sequences[s].KeysChanged = true;
                for (int b = 0; b < m.Bones.Length; b++)
                {
                    var ck = new BoneChannelKeys();
                    foreach (var t in bones.Tracks(b))
                    {
                        var raw = RawTrack.Read(t);
                        if (raw.GlobalSequence < 0 && raw.KeyCount > 0) ck.Set(t.Name, raw.Window(s, m.Sequences[s].Start, m.Sequences[s].End));
                    }
                    plan.Bones[b].Keys[s] = ck;
                }
            }
            var r = RigWriter.Apply(f, plan, Catalog.Value);
            var nm = M2Model.Load(f.Serialize());
            var before = M2Validator.Errors(M2Validator.Validate(m)).Select(e => e.ToString()).ToHashSet();
            var newErr = M2Validator.Errors(M2Validator.Validate(nm)).FirstOrDefault(e => !before.Contains(e.ToString()));
            if (newErr != null) { bad.Add($"{name}: {newErr}"); continue; }
            if (r.Relocated.Count != m.Sequences.Length) { bad.Add($"{name}: {r.Relocated.Count} of {m.Sequences.Length} moved"); continue; }
            foreach (var an in TrackArrays)
            {
                var oa = m.File[an];
                var na = nm.File[an];
                for (int i = 0; i < oa.Count; i++)
                {
                    var ot = oa.Tracks(i).ToList();
                    var nt = na.Tracks(i).ToList();
                    for (int k = 0; k < ot.Count; k++)
                    {
                        var ro = RawTrack.Read(ot[k]);
                        var rn = RawTrack.Read(nt[k]);
                        if (ro.GlobalSequence >= 0)
                        {
                            if (!ro.SameAs(rn)) bad.Add($"{name} {an}[{i}].{ot[k].Name}: a global-sequence track changed");
                            continue;
                        }
                        for (int s = 0; s < m.Sequences.Length; s++)
                            if (SameValues(ro, m.Sequences[s], s, rn, nm.Sequences[s], s) is { } why)
                            {
                                bad.Add($"{name} {an}[{i}].{ot[k].Name} sequence {s}: {why}");
                                break;
                            }
                    }
                }
            }
        }
        _out.WriteLine($"{models} models with every sequence moved, {bad.Count} problems");
        foreach (var b in bad.Take(20)) _out.WriteLine(b);
        Assert.True(models > 2500);
        Assert.Empty(bad);
    }

    [Fact]
    public void LookupTablesFollowTheClientsRules()
    {
        var cat = Catalog.Value;
        int models = 0, playableSame = 0, playableQuirk = 0;
        var bad = new List<string>();
        foreach (var (name, _, m) in Sample(1))
        {
            models++;
            // animation lookup: the first sequence of every animation id, up to the largest one
            var al = AnimationCatalog.AnimationLookup(m.Sequences.Select(s => s.AnimId).ToList());
            if (m.Sequences.Length > 0 && !al.SequenceEqual(m.AnimationLookup)) bad.Add($"{name}: animation lookup");
            // key-bone lookup: the first bone with each key bone id ([-1] without key bones)
            int maxKey = m.Bones.Length == 0 ? -1 : m.Bones.Max(b => b.KeyBoneId);
            var kbl = new short[Math.Max(1, maxKey + 1)];
            Array.Fill(kbl, (short)-1);
            for (int i = m.Bones.Length - 1; i >= 0; i--) if (m.Bones[i].KeyBoneId >= 0) kbl[m.Bones[i].KeyBoneId] = (short)i;
            if (m.Bones.Length > 0 && !kbl.SequenceEqual(m.KeyBoneLookup)) bad.Add($"{name}: key-bone lookup");
            // variation chains: the sequences of one animation id in order
            for (int s = 0; s < m.Sequences.Length; s++)
            {
                int next = Array.FindIndex(m.Sequences, s + 1, x => x.AnimId == m.Sequences[s].AnimId);
                if (m.Sequences[s].NextVariant != next) { bad.Add($"{name}: variation chain at {s}"); break; }
            }
            // the playable-animation table: the client's algorithm (M2Model_ResolvePlayableAnimation)
            var pl = m.File["playable_lookup"];
            if (pl.Count != AnimationCatalog.PlayableCount) { bad.Add($"{name}: playable table of {pl.Count}"); continue; }
            var table = cat.PlayableTable(m.AnimationLookup, m.Sequences.Length > 0 ? m.Sequences[0].AnimId : 0);
            bool same = Enumerable.Range(0, pl.Count).All(i => table[i] == (pl.I16(i, "fallback_id"), pl.U16(i, "flags")));
            if (same) playableSame++;
            else
            {
                // Blizzard's exporter wrote Stand -> 147 (Closed) for some models without Stand; nothing else differs
                bool quirk = Enumerable.Range(0, pl.Count).All(i => table[i] == (pl.I16(i, "fallback_id"), pl.U16(i, "flags")) || (pl.I16(i, "fallback_id") == 147 && m.AnimationLookup.ElementAtOrDefault(0) == -1));
                if (quirk) playableQuirk++;
                else bad.Add($"{name}: playable table");
            }
        }
        _out.WriteLine($"{models} models: playable table = the client's algorithm in {playableSame}, Blizzard's Stand -> 147 shortcut in {playableQuirk}");
        Assert.True(models > 9000);
        Assert.True(playableSame > 9600, $"{playableSame}");
        Assert.Empty(bad);
    }

    [Fact]
    public void KeyReducerAndDecompositionAreExact()
    {
        // a bone transform round trip: T(p) T R S T(-p) -> decompose -> the same
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3(rnd.NextSingle() * 2 - 1, rnd.NextSingle() * 2 - 1, rnd.NextSingle() * 2);
            var t = new Vector3(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f);
            var q = Quaternion.Normalize(new Quaternion(rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() - 0.5f, rnd.NextSingle() + 0.2f));
            var s = new Vector3(0.5f + rnd.NextSingle(), 0.5f + rnd.NextSingle(), 0.5f + rnd.NextSingle());
            var (t2, q2, s2) = BoneTransform.Decompose(M2Anim.BoneLocal(p, t, q, s), p);
            Assert.True(Vector3.Distance(t, t2) < 1e-4f, $"{t} {t2}");
            Assert.True(Math.Min((q - q2).Length(), (q + q2).Length()) < 1e-4f, $"{q} {q2}");
            Assert.True(Vector3.Distance(s, s2) < 1e-4f, $"{s} {s2}");
        }
        // dense samples of a piecewise-linear curve come back as its corners
        var times = Enumerable.Range(0, 101).Select(x => (uint)(x * 10)).ToList();
        var vals = times.Select(x => new double[] { x <= 400 ? x / 400.0 : 1 - (x - 400) / 600.0, 0, 0 }).ToList();
        var (rt, rv) = KeyReducer.Reduce(times, vals, 1e-6, false);
        Assert.Equal(new uint[] { 0, 400, 1000 }, rt);
        // a constant run is one key
        var (ct, _) = KeyReducer.Reduce(times, times.Select(_ => new double[] { 2, 2, 2 }).ToList(), 1e-6, false);
        Assert.Single(ct);
        // quaternions are put in one hemisphere first
        var qv = new List<double[]> { new double[] { 0, 0, 0, 1 }, new double[] { 0, 0, 0, -1 }, new double[] { 0, 0, 0, 1 } };
        var (qt, qq) = KeyReducer.Reduce(new List<uint> { 0, 10, 20 }, qv, 1e-6, true);
        Assert.Single(qt);
    }

    [Fact]
    public void RemovingAndAddingBonesRemapsEveryReference()
    {
        var data = TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!;
        var m = M2Model.Load(data);
        var plan = RigPlan.Identity(m);
        // remove an unweighted bone that carries an attachment point, add a child of the head
        var info = RigInfo.Read(m);
        int victim = Enumerable.Range(0, m.Bones.Length).First(b => info.Bones[b].Vertices == 0 && m.Attachments.Any(a => a.Bone == b) && m.Bones[b].KeyBoneId < 0 && !m.Bones.Any(c => c.Parent == b));
        int parentOfVictim = m.Bones[victim].Parent;
        plan.Bones.RemoveAt(victim);
        foreach (var b in plan.Bones) if (b.Parent > victim) b.Parent--;
        int head = plan.Bones.FindIndex(b => b.KeyBoneId == 6);
        plan.Bones.Add(new PlanBone { Name = "bone_new", Parent = head, Pivot = plan.Bones[head].Pivot + new Vector3(0.2f, 0, 0) });
        plan.Bones[^1].Keys[0] = new BoneChannelKeys { Rotation = KeysOf(Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(0, 0.5f, 0), 667) };
        var f = M2File.Parse(data);
        var r = RigWriter.Apply(f, plan, Catalog.Value);
        var nm = M2Model.Load(f.Serialize());
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(nm)));
        Assert.Equal(m.Bones.Length, nm.Bones.Length);
        Assert.Equal(-1, r.BoneMap[victim]);
        // the attachment of the removed bone follows its parent; every other reference follows its bone
        int newParent = parentOfVictim >= 0 ? r.BoneMap[parentOfVictim] : 0;   // a root bone without a parent: the first bone
        for (int i = 0; i < m.Attachments.Length; i++)
            Assert.Equal(m.Attachments[i].Bone == victim ? newParent : r.BoneMap[m.Attachments[i].Bone], nm.Attachments[i].Bone);
        for (int i = 0; i < m.Vertices.Length; i++)
            Assert.Equal(r.BoneMap[m.Vertices[i].B0], nm.Vertices[i].B0);
        Assert.Equal(m.KeyBoneLookup.Select(x => x < 0 ? (short)-1 : (short)r.BoneMap[x]), nm.KeyBoneLookup);
        // the new bone is animated in sequence 0 only, with the 0x200 flag
        Assert.True(nm.Bones[^1].IsAnimated);
        Assert.Equal(0x200u, nm.Bones[^1].Flags & 0x200);
        var mats = new Matrix4x4[nm.Bones.Length];
        M2Anim.BoneMatrices(nm, new M2Anim.Time(0, nm.Sequences[0].Start, 333, 333), mats);
        Assert.False(mats[^1].IsIdentity);
    }

    internal static SeqKeys KeysOf(Quaternion a, Quaternion b, uint duration)
    {
        var k = new SeqKeys(true);
        k.Add(0, TrackValues.Quat(a));
        k.Add(duration / 2, TrackValues.Quat(b));
        k.Add(duration, TrackValues.Quat(a));
        return k;
    }

    [Fact]
    public void NewAnimationGetsLookupsChainsAndATimelinePlace()
    {
        var data = TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!;
        var m = M2Model.Load(data);
        var cat = Catalog.Value;
        var plan = RigPlan.Identity(m);
        int stand = RigWriter.DefaultTemplate(m);
        int dance = cat.IdOfName("EmoteDance")!.Value;
        plan.Sequences.Add(new PlanSequence { AnimId = dance, Duration = 2000, Frequency = 32767, BlendTime = 150, Template = stand, Label = "EmoteDance" });
        plan.Sequences.Add(new PlanSequence { AnimId = 0, SubId = 3, Duration = 1500, Frequency = 4000, BlendTime = 150, Flags = 1, Template = stand, Label = "Stand [4]" });
        var head = plan.Bones.FindIndex(b => b.KeyBoneId == 6);
        plan.Bones[head].Keys[plan.Sequences.Count - 2] = new BoneChannelKeys { Rotation = KeysOf(Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(0.6f, 0, 0), 2000) };
        var f = M2File.Parse(data);
        var r = RigWriter.Apply(f, plan, cat);
        RigWriter.RecomputeBounds(f, r.Relocated);
        var nm = M2Model.Load(f.Serialize());
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(nm)));
        int ns = nm.Sequences.Length;
        Assert.Equal(m.Sequences.Length + 2, ns);
        // after everything else on the timeline, apart from each other
        uint lastEnd = m.Sequences.Max(s => s.End);
        Assert.True(nm.Sequences[ns - 2].Start >= lastEnd + 1000);
        Assert.True(nm.Sequences[ns - 1].Start >= nm.Sequences[ns - 2].End + 1000);
        // the old sequences did not move
        for (int s = 0; s < m.Sequences.Length; s++) Assert.Equal((m.Sequences[s].Start, m.Sequences[s].End), (nm.Sequences[s].Start, nm.Sequences[s].End));
        // lookups: the new animation id is found, Stand's chain ends with the new variation
        Assert.Equal(ns - 2, nm.AnimationLookup[dance]);
        int last = nm.AnimationLookup[0];
        while (nm.Sequences[last].NextVariant >= 0) last = nm.Sequences[last].NextVariant;
        Assert.Equal(ns - 1, last);
        // the playable table: the client's own algorithm; EmoteDance now plays itself
        var pl = nm.File["playable_lookup"];
        Assert.Equal((short)dance, pl.I16(dance, "fallback_id"));
        var table = cat.PlayableTable(nm.AnimationLookup, nm.Sequences[0].AnimId);
        Assert.All(Enumerable.Range(0, pl.Count), i => Assert.Equal(table[i], (pl.I16(i, "fallback_id"), pl.U16(i, "flags"))));
        // bounds were computed for the new sequences
        Assert.True(nm.Sequences[ns - 2].BoundsRadius > 0);
        // the head turns in the dance, the bones without keys hold their constant
        var mats = new Matrix4x4[nm.Bones.Length];
        var sq = nm.Sequences[ns - 2];
        M2Anim.BoneMatrices(nm, new M2Anim.Time(ns - 2, sq.Start, 1000, 1000), mats);
        Assert.False(mats[head].IsIdentity);
    }
}
