using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using WoWData.Core.M2;

namespace WoWData.Core.ModelIO;

/// <summary>What the import found in the skeleton, animations, attachment points and events.</summary>
public sealed class RigAnalysis
{
    public RigPlan Plan { get; init; } = null!;
    public bool Changed { get; set; }
    public List<ModelRule> Rules { get; } = new();
    /// <summary>One line per change, in plain words.</summary>
    public List<string> Changes { get; } = new();
    /// <summary>Provisional bone index (the original index for a kept bone, original count + n for the n-th new bone) ->
    /// final index, for the mesh's weights.</summary>
    public Dictionary<int, int> ProvisionalToFinal { get; } = new();
    public int NewBones { get; set; }
    public int RemovedBones { get; set; }
    public int NewSequences { get; set; }
    public int EditedSequences { get; set; }
    public int RemovedSequences { get; set; }
    public string Summary => Changes.Count == 0 ? "Unchanged." : string.Join("; ", Changes.Take(12)) + (Changes.Count > 12 ? $"; and {Changes.Count - 12} more" : "");
}

/// <summary>
/// 0.10.0: compares the skeleton and animations that came back from Blender with the model the export was made from,
/// and plans the edit (<see cref="RigPlan"/>), with every check in plain words. Unchanged parts are recognised (the
/// add-on marks actions whose keys it did not touch; sampled actions are compared with the original key by key), so an
/// unchanged round trip changes nothing.
/// </summary>
public static class RigImportAnalyzer
{
    public const float PositionTolerance = 1e-4f;
    /// <summary>Sampled transforms equal to the original within these are not an edit.</summary>
    public const double SameTranslation = 2e-3, SameRotation = 2e-3, SameScale = 2e-3;
    /// <summary>Key reduction tolerances (what is lost by dropping a key).</summary>
    public const double ReduceTranslation = 2e-4, ReduceRotation = 2e-4, ReduceScale = 2e-4;
    private static readonly string[] Channels = { "translation", "rotation", "scale" };
    private static readonly Regex Suffix = new(@"(\.\d{3})+$", RegexOptions.CultureInvariant);
    private static readonly Regex Variation = new(@"\s*(\[\d+\]|#\d+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>The animation name of an action: "Stand [2].001" -> "Stand".</summary>
    public static string BaseName(string action)
    {
        var s = Suffix.Replace(action.Trim(), "");
        for (int i = 0; i < 3; i++) s = Variation.Replace(s, "");
        return s.Trim();
    }

    /// <summary>Which edited bone keeps which original index (a bone copied in Blender keeps its source's wow_bone: the
    /// one with the original name keeps it, the copy is new). Returns name -> provisional index.</summary>
    public static Dictionary<string, int> ProvisionalIndices(EditedRig rig, int oldCount, PackageRig? man)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var claimed = new Dictionary<int, string>();
        foreach (var grp in rig.Bones.Where(b => b.WowBone is int w && w >= 0 && w < oldCount).GroupBy(b => b.WowBone!.Value))
        {
            string orig = man?.Bones.FirstOrDefault(x => x.Index == grp.Key)?.Name ?? RigInfo.BoneName(grp.Key, -1);
            var keep = grp.FirstOrDefault(b => b.Name == orig) ?? grp.FirstOrDefault(b => !Suffix.IsMatch(b.Name)) ?? grp.First();
            claimed[grp.Key] = keep.Name;
            map[keep.Name] = grp.Key;
        }
        int next = oldCount;
        foreach (var b in rig.Bones)
            if (!map.ContainsKey(b.Name)) map[b.Name] = next++;
        return map;
    }

    public static RigAnalysis Analyze(M2Model m, ModelPackageManifest man, EditedRig rig, AnimationCatalog cat, bool isCharacter)
    {
        var plan = RigPlan.Identity(m);
        var identity = RigPlan.Identity(m);
        var a = new RigAnalysis { Plan = plan };
        var mr = man.Rig ?? new PackageRig();
        var info = RigInfo.Read(m, cat.Rows.Count > 0 ? cat : null);
        int oldN = m.Bones.Length, oldS = m.Sequences.Length;
        void Rule(RuleLevel l, string t) => a.Rules.Add(new ModelRule(l, t));
        string BoneLabel(int old) => old >= 0 && old < info.Bones.Count ? info.Bones[old].Name : $"bone {old}";
        string SeqLabel(int s) => s >= 0 && s < oldS ? (mr.Sequences.FirstOrDefault(x => x.Index == s)?.Action ?? info.Sequences[s].Name) : $"sequence {s}";

        // ================================================================ bones
        var prov = ProvisionalIndices(rig, oldN, mr);
        var byName = rig.Bones.ToDictionary(b => b.Name, StringComparer.Ordinal);
        bool rigHasBones = rig.Bones.Count > 0;
        // bones without the armature (a mesh-only file): keep the skeleton
        if (!rigHasBones && oldN > 0)
        {
            Rule(RuleLevel.Info, "The file has no armature: the skeleton and animations are kept as they are.");
            FinishPoints(m, man, rig, plan, a, info, null, isCharacter);
            return a;
        }
        var kept = prov.Where(kv => kv.Value < oldN).ToDictionary(kv => kv.Value, kv => kv.Key);
        var removed = Enumerable.Range(0, oldN).Where(i => !kept.ContainsKey(i)).ToList();
        var added = rig.Bones.Where(b => prov[b.Name] >= oldN).ToList();
        // provisional order: kept bones in their original order, then the new ones; parents first
        var order = Enumerable.Range(0, oldN).Where(kept.ContainsKey).Select(i => kept[i]).Concat(added.Select(b => b.Name)).ToList();
        var parentOf = order.ToDictionary(n => n, n => byName[n].Parent is { } p && byName.ContainsKey(p) ? p : null);
        var final = new List<string>();
        var placed = new HashSet<string>();
        var pending = new List<string>(order);
        while (pending.Count > 0)
        {
            int idx = pending.FindIndex(n => parentOf[n] == null || placed.Contains(parentOf[n]!));
            if (idx < 0)
            {
                Rule(RuleLevel.Blocker, $"The bones {string.Join(", ", pending.Take(5))} are their own ancestors (a loop in the hierarchy).");
                final.AddRange(pending);
                break;
            }
            final.Add(pending[idx]);
            placed.Add(pending[idx]);
            pending.RemoveAt(idx);
        }
        var finalIndex = final.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        plan.Bones.Clear();
        foreach (var name in final)
        {
            var eb = byName[name];
            int old = prov[name] < oldN ? prov[name] : -1;
            var ob = old >= 0 ? m.Bones[old] : null;
            var pb = new PlanBone
            {
                OldIndex = old, Name = name, Parent = parentOf[name] is { } pn ? finalIndex[pn] : -1,
                KeyBoneId = eb.KeyBoneId ?? ob?.KeyBoneId ?? -1, Flags = eb.Flags ?? ob?.Flags ?? 0, SubmeshId = (ushort)(ob?.SubmeshId ?? 0),
                // an unchanged pivot keeps its exact float bits
                Pivot = ob != null && Vector3.Distance(ob.Pivot, eb.Head) <= PositionTolerance ? ob.Pivot : eb.Head,
            };
            plan.Bones.Add(pb);
        }
        foreach (var kv in prov) a.ProvisionalToFinal[kv.Value] = finalIndex.TryGetValue(kv.Key, out int fi) ? fi : -1;
        a.NewBones = added.Count;
        a.RemovedBones = removed.Count;
        // what changed in the skeleton
        if (added.Count > 0) a.Changes.Add($"{added.Count} new bone{(added.Count == 1 ? "" : "s")} ({string.Join(", ", added.Take(6).Select(b => b.Name))}{(added.Count > 6 ? ", ..." : "")})");
        if (removed.Count > 0) a.Changes.Add($"{removed.Count} bone{(removed.Count == 1 ? "" : "s")} removed ({string.Join(", ", removed.Take(6).Select(BoneLabel))}{(removed.Count > 6 ? ", ..." : "")})");
        for (int k = 0; k < plan.Bones.Count; k++)
        {
            var pb = plan.Bones[k];
            if (pb.OldIndex < 0) continue;
            var ob = m.Bones[pb.OldIndex];
            int oldParentFinal = ob.Parent >= 0 && ob.Parent < oldN && kept.ContainsKey(ob.Parent) ? finalIndex[kept[ob.Parent]] : -1;
            if (pb.Parent != oldParentFinal && !(ob.Parent >= 0 && !kept.ContainsKey(ob.Parent)))
                a.Changes.Add($"{pb.Name} now hangs from {(pb.Parent >= 0 ? plan.Bones[pb.Parent].Name : "nothing (a root bone)")}");
            if (pb.Pivot != ob.Pivot) a.Changes.Add($"{pb.Name} moved by {Vector3.Distance(pb.Pivot, ob.Pivot):0.###} yd");
            if (pb.KeyBoneId != ob.KeyBoneId) a.Changes.Add($"{pb.Name} is now key bone {KeyName(pb.KeyBoneId)} (was {KeyName(ob.KeyBoneId)})");
            if (pb.Flags != ob.Flags) a.Changes.Add($"{pb.Name}: flags 0x{ob.Flags:x} -> 0x{pb.Flags:x}");
        }
        // checks
        foreach (var pb in plan.Bones)
            if (pb.KeyBoneId < -1 || pb.KeyBoneId >= M2Schema.KeyBones.Length)
                Rule(RuleLevel.Blocker, $"{pb.Name}: key bone {pb.KeyBoneId} does not exist. The 1.12 client knows key bones 0-26 ({string.Join(", ", M2Schema.KeyBones.Take(8))}, ...).");
        foreach (var grp in plan.Bones.Where(b => b.KeyBoneId >= 0).GroupBy(b => b.KeyBoneId).Where(g => g.Count() > 1))
            Rule(RuleLevel.Warning, $"Key bone {KeyName(grp.Key)} is set on {grp.Count()} bones ({string.Join(", ", grp.Select(b => b.Name))}): the game uses the first, {grp.First().Name}.");
        foreach (int old in removed)
        {
            int key = m.Bones[old].KeyBoneId;
            if (key >= 0 && !plan.Bones.Any(b => b.KeyBoneId == key))
                Rule(RuleLevel.Blocker, $"{BoneLabel(old)} is key bone {KeyName(key)}: the game finds it by that name ({KeyBoneUse(key)}). Keep the bone, or give another bone the key bone {KeyName(key)} (Bone properties > WoW bone) before you delete it.");
        }
        foreach (int old in removed.Where(o => info.Bones[o].Vertices > 0))
        {
            int anc = m.Bones[old].Parent;
            while (anc >= 0 && !kept.ContainsKey(anc)) anc = m.Bones[anc].Parent;
            Rule(RuleLevel.Info, $"{info.Bones[old].Vertices:N0} vertices were weighted to {BoneLabel(old)}: where your file does not weight them otherwise they follow {(anc >= 0 ? BoneLabel(anc) : "the first bone")}.");
        }
        foreach (int old in removed)
        {
            var refs = new List<string>();
            if (m.Attachments.Any(x => x.Bone == old)) refs.Add("attachment points");
            if (info.Events.Any(x => x.Bone == old)) refs.Add("events");
            if (m.ParticleCount > 0 && Enumerable.Range(0, m.ParticleCount).Any(i => m.File["particles"].U16(i, "bone") == old)) refs.Add("particle emitters");
            if (m.RibbonCount > 0 && Enumerable.Range(0, m.RibbonCount).Any(i => m.File["ribbons"].U32(i, "bone") == old)) refs.Add("ribbons");
            if (refs.Count > 0) Rule(RuleLevel.Warning, $"{BoneLabel(old)} carried {string.Join(", ", refs)}: they move to its nearest remaining parent bone.");
        }
        if (plan.Bones.Count > 256) Rule(RuleLevel.Warning, $"The skeleton has {plan.Bones.Count} bones. Vertices can only use the first 256 (each vertex stores its bones in one byte); the game's own models have at most 158 + a few helper bones.");
        if (plan.Bones.Count == 0 && oldN > 0) Rule(RuleLevel.Blocker, "The armature has no bones left.");
        bool bonesChanged = added.Count > 0 || removed.Count > 0 || plan.Bones.Select((b, i) => b.OldIndex != i).Any(x => x) ||
                            plan.Bones.Any(b => b.OldIndex >= 0 && (b.Parent != m.Bones[b.OldIndex].Parent || b.Pivot != m.Bones[b.OldIndex].Pivot ||
                                                                    b.KeyBoneId != m.Bones[b.OldIndex].KeyBoneId || b.Flags != m.Bones[b.OldIndex].Flags));
        if (bonesChanged && isCharacter)
            Rule(RuleLevel.Warning, "This is a character model: every player and NPC of this race and sex uses this skeleton, and the game's code finds the head, hands, weapons, shield, spell hands and the camera through its key bones and attachment points. Test it in game (several emotes, weapons, mounts, spells) before you share it.");

        // ================================================================ sequences
        var seqOfAction = new Dictionary<EditedAction, int>();
        foreach (var grp in rig.Actions.Where(x => x.WowSequence is int w && w >= 0 && w < oldS).GroupBy(x => x.WowSequence!.Value))
        {
            string orig = SeqLabel(grp.Key);
            var keep = grp.FirstOrDefault(x => string.Equals(Suffix.Replace(x.Name, ""), orig, StringComparison.OrdinalIgnoreCase))
                       ?? grp.FirstOrDefault(x => !Suffix.IsMatch(x.Name)) ?? grp.First();
            seqOfAction[keep] = grp.Key;
        }
        var keptSeq = seqOfAction.ToDictionary(kv => kv.Value, kv => kv.Key);
        var removedSeq = Enumerable.Range(0, oldS).Where(i => !keptSeq.ContainsKey(i)).ToList();
        var newActions = rig.Actions.Where(x => !seqOfAction.ContainsKey(x)).ToList();
        if (rig.Actions.Count == 0 && oldS > 0)
        {
            removedSeq.Clear();
            Rule(RuleLevel.Info, "The file has no animations: the model's animations are kept as they are.");
        }
        plan.Sequences.Clear();
        var finalActions = new List<EditedAction?>();
        var fresh = RigPlan.Identity(m);
        foreach (int s in Enumerable.Range(0, oldS).Where(i => keptSeq.ContainsKey(i) || rig.Actions.Count == 0))
        {
            var ps = fresh.Sequences[s];
            ps.Label = SeqLabel(s);
            plan.Sequences.Add(ps);
            finalActions.Add(keptSeq.GetValueOrDefault(s));
        }
        foreach (var act in newActions)
        {
            int template = act.WowSequence is int w && w >= 0 && w < oldS ? w : RigWriter.DefaultTemplate(m);
            var tps = template >= 0 ? identity.Sequences[template] : new PlanSequence();
            plan.Sequences.Add(new PlanSequence
            {
                OldIndex = -1, Template = template, AnimId = tps.AnimId, SubId = 0, MoveSpeed = tps.MoveSpeed, Flags = tps.Flags, Frequency = tps.Frequency,
                ReplayMin = tps.ReplayMin, ReplayMax = tps.ReplayMax, BlendTime = tps.BlendTime, Duration = tps.Duration, Label = act.Name,
            });
            finalActions.Add(act);
        }
        a.NewSequences = newActions.Count;
        a.RemovedSequences = removedSeq.Count;
        foreach (int s in removedSeq)
        {
            var sq = m.Sequences[s];
            var roles = AnimationRoles.Of(sq.AnimId);
            bool lastOfId = !plan.Sequences.Any(p => p.AnimId == sq.AnimId);
            string fb = lastOfId ? cat.FallbackText(sq.AnimId) is { } f ? $" The game then plays {f} instead (AnimationData)." : " The game then has nothing to play for it." : $" Its other variations stay.";
            if (sq.AnimId == 0 && lastOfId) Rule(RuleLevel.Blocker, $"{SeqLabel(s)} was removed: every model needs Stand (animation 0), the game's default and fallback animation.");
            else if (isCharacter && lastOfId && roles.Count > 0) Rule(RuleLevel.Blocker, $"{SeqLabel(s)} was removed from a character model: the game plays it for {string.Join("; ", roles)}.{fb} Keep it (you can edit it instead).");
            else Rule(lastOfId && roles.Count > 0 ? RuleLevel.Warning : RuleLevel.Info, $"{SeqLabel(s)} was removed.{(roles.Count > 0 ? $" The game plays it for {string.Join("; ", roles)}." : "")}{fb}");
            a.Changes.Add($"animation {SeqLabel(s)} removed");
        }

        // per-sequence metadata and keys
        var bonesRaw = new Dictionary<(int Old, string Channel), RawTrack>();
        RawTrack? OldTrack(int oldBone, string ch)
        {
            if (oldBone < 0) return null;
            if (!bonesRaw.TryGetValue((oldBone, ch), out var r)) bonesRaw[(oldBone, ch)] = r = RawTrack.Read(m.File["bones"].Track(oldBone, ch));
            return r;
        }
        var finalOfName = finalIndex;
        for (int s = 0; s < plan.Sequences.Count; s++)
        {
            var ps = plan.Sequences[s];
            var act = finalActions[s];
            if (act == null) continue;
            var oldSq = ps.OldIndex >= 0 ? m.Sequences[ps.OldIndex] : null;
            string label = ps.OldIndex >= 0 ? SeqLabel(ps.OldIndex) : $"new animation \"{act.Name}\"";
            // the animation id: the action's name decides, else its wow_anim_id
            string bn = BaseName(act.Name);
            int? byName2 = cat.Rows.Count > 0 ? cat.IdOfName(bn) : null;
            int? prop = act.IntProp("anim_id");
            int anim = byName2 ?? prop ?? ps.AnimId;
            if (byName2 == null && prop == null && ps.OldIndex < 0)
                Rule(RuleLevel.Blocker, $"The action \"{act.Name}\" is not named after a game animation, so the game would not know when to play it. Rename it to an animation name ({SomeNames(cat)}...), for example \"Stand [3]\" for a third standing variation.");
            if (byName2 == null && prop != null && cat.Rows.Count > 0 && !cat.Exists(prop.Value))
                Rule(RuleLevel.Blocker, $"\"{act.Name}\": animation {prop} does not exist in AnimationData.");
            if (anim != ps.AnimId && ps.OldIndex >= 0) a.Changes.Add($"{label} is now played as {cat.Name(anim)}");
            ps.AnimId = anim;
            if (ps.OldIndex < 0) ps.SubId = plan.Sequences.Take(s).Count(x => x.AnimId == anim);
            // timing
            uint dur = ps.Duration;
            if (!act.Unchanged && act.DurationMs > 0)
            {
                uint d = (uint)Math.Round(act.DurationMs);
                if (oldSq == null || Math.Abs((long)d - oldSq.Duration) > 1) dur = d;
            }
            if (dur == 0) Rule(RuleLevel.Warning, $"{label} lasts 0 ms.");
            bool retimed = oldSq != null && dur != oldSq.Duration;
            ps.Duration = dur;
            // properties
            // the original's (or, for a new animation, its template's) properties: only differences count
            var orig = mr.Sequences.FirstOrDefault(x => x.Index == (ps.OldIndex >= 0 ? ps.OldIndex : ps.Template));
            if (act.IntProp("flags") is int rf && (uint)rf != ps.Flags) ps.Flags = (uint)rf;
            if (act.BoolProp("loop") is bool loop && loop != ((ps.Flags & 1) == 0) && (orig == null || loop != orig.Loop))
            {
                ps.Flags = loop ? ps.Flags & ~1u : ps.Flags | 1u;
                a.Changes.Add($"{label} {(loop ? "now loops" : "now plays once")}");
            }
            if (act.DoubleProp("chance") is double ch && (orig == null || Math.Abs(ch - orig.Chance) > 0.005))
                ps.Frequency = (short)Math.Clamp(Math.Round(ch * 32767 / 100), 0, 32767);
            if (act.IntProp("blend_ms") is int bm && bm >= 0 && (uint)bm != ps.BlendTime) { ps.BlendTime = (uint)bm; a.Changes.Add($"{label}: blend time {bm} ms"); }
            if (act.IntProp("repeat_min") is int rmin && rmin >= 0) ps.ReplayMin = (uint)rmin;
            if (act.IntProp("repeat_max") is int rmax && rmax >= 0) ps.ReplayMax = (uint)rmax;
            if (ps.ReplayMin > ps.ReplayMax) Rule(RuleLevel.Warning, $"{label}: repeat minimum {ps.ReplayMin} is above the maximum {ps.ReplayMax}.");
            if (act.IntProp("next_alias") is int na && na >= 0) ps.NextAlias = (ushort)na;
            bool speedSet = false;
            if (act.DoubleProp("move_speed") is double ms && (orig == null || Math.Abs(ms - orig.MoveSpeed) > 1e-4)) { ps.MoveSpeed = (float)ms; speedSet = true; a.Changes.Add($"{label}: move speed {ms.ToString("0.###", CultureInfo.InvariantCulture)} yd/s"); }
            if (speedSet && (ps.MoveSpeed < 0 || float.IsNaN(ps.MoveSpeed))) Rule(RuleLevel.Blocker, $"{label}: the move speed cannot be negative.");
            if (retimed)
            {
                a.Changes.Add($"{label} retimed: {oldSq!.Duration / 1000.0:0.###} s -> {dur / 1000.0:0.###} s");
                if (!speedSet && ps.MoveSpeed > 0 && AnimationRoles.IsMovement(ps.AnimId) && dur > 0)
                {
                    float old = ps.MoveSpeed;
                    ps.MoveSpeed = (float)(old * oldSq.Duration / dur);
                    Rule(RuleLevel.Info, $"{label} now lasts {dur / 1000.0:0.###} s instead of {oldSq.Duration / 1000.0:0.###} s: its move speed was changed from {old:0.###} to {ps.MoveSpeed:0.###} yards per second, so the feet do not slide (the game plays a movement animation faster or slower to match the unit's real speed). Set the action's move speed to choose it yourself.");
                }
            }
            // keys
            if (act.Unchanged && ps.OldIndex < 0 && ps.Template >= 0)
            {
                // a copy of an action whose keys were not touched: the template's keys
                var tsq = m.Sequences[ps.Template];
                for (int k = 0; k < plan.Bones.Count; k++)
                {
                    int ob = plan.Bones[k].OldIndex;
                    if (ob < 0) continue;
                    var ck = new BoneChannelKeys();
                    foreach (var c in Channels)
                    {
                        var raw = OldTrack(ob, c)!;
                        if (raw.GlobalSequence >= 0 || raw.KeyCount == 0) continue;
                        ck.Set(c, raw.Window(ps.Template, tsq.Start, tsq.End).Stretched(tsq.Duration, dur).ClippedTo(dur, raw.ValueType, raw.Interpolation));
                    }
                    plan.Bones[k].Keys[s] = ck;
                }
                ps.KeysChanged = true;
            }
            else if (!act.Unchanged)
            {
                int changedBones = KeysFromSamples(m, plan, s, act, finalOfName, OldTrack, retimed, a);
                if (changedBones > 0 || ps.OldIndex < 0)
                {
                    ps.KeysChanged = true;
                    if (ps.OldIndex >= 0) a.Changes.Add($"{label}: new keys for {changedBones} bone{(changedBones == 1 ? "" : "s")}");
                }
            }
            if (ps.OldIndex < 0)
            {
                ps.KeysChanged = true;
                a.Changes.Add($"new animation {act.Name} ({cat.Name(anim)}, {dur / 1000.0:0.###} s{(ps.Template >= 0 && act.WowSequence == ps.Template ? $", copied from {SeqLabel(ps.Template)}" : "")})");
            }
            if (ps.KeysChanged && ps.OldIndex >= 0) a.EditedSequences++;
            // properties that differ from the original
            if (ps.OldIndex >= 0)
            {
                var o = identity.Sequences[ps.OldIndex];
                if (o.Frequency != ps.Frequency) a.Changes.Add($"{label}: chance {o.Frequency / 327.67:0.#} % -> {ps.Frequency / 327.67:0.#} %");
                if (o.ReplayMin != ps.ReplayMin || o.ReplayMax != ps.ReplayMax) a.Changes.Add($"{label}: repeats {ps.ReplayMin}-{ps.ReplayMax}");
                if (o.Flags != ps.Flags && ((o.Flags ^ ps.Flags) & ~1u) != 0) a.Changes.Add($"{label}: flags 0x{o.Flags:x} -> 0x{ps.Flags:x}");
            }
        }
        // variation chances add up to at most 100 %
        foreach (var grp in plan.Sequences.GroupBy(p => p.AnimId).Where(g => g.Count() > 1))
        {
            int sum = grp.Sum(p => (int)p.Frequency);
            bool touched = grp.Any(p => p.OldIndex < 0) || grp.Any(p => p.OldIndex >= 0 && m.Sequences[p.OldIndex].Frequency != p.Frequency);
            if (sum > 32767 && touched)
            {
                foreach (var p in grp) p.Frequency = (short)Math.Max(1, Math.Floor(p.Frequency * 32767.0 / sum));
                Rule(RuleLevel.Info, $"The chances of the {grp.Count()} {cat.Name(grp.Key)} variations added up to {sum / 327.67:0} %: they were scaled to 100 % ({string.Join(", ", grp.Select(p => $"{p.Label} {p.Frequency / 327.67:0.#} %"))}).");
            }
        }
        foreach (var p in plan.Sequences)
            if (cat.Rows.Count > 0 && !cat.Exists(p.AnimId)) Rule(RuleLevel.Blocker, $"{p.Label}: animation {p.AnimId} is not in AnimationData.dbc.");
        if (plan.Sequences.Count > 0 && !plan.Sequences.Any(p => p.AnimId == 0) && m.Sequences.Any(q => q.AnimId == 0))
            Rule(RuleLevel.Blocker, "The model no longer has a Stand (animation 0): the game's default animation.");

        // ================================================================ global sequences
        foreach (var gact in rig.Globals.Where(x => !x.Unchanged))
        {
            int gs = gact.GlobalSequence ?? -1;
            if (gs < 0 || gs >= plan.GlobalSequences.Count) continue;
            if (m.GlobalSequences[gs] == 0) continue;     // a global sequence of 0 ms (a few client models) holds one value: nothing to edit
            uint period = (uint)Math.Round(gact.DurationMs);
            bool changedAny = false;
            if (period > 0 && Math.Abs((long)period - plan.GlobalSequences[gs]) > 1)
            {
                a.Changes.Add($"global sequence {gs + 1}: {plan.GlobalSequences[gs]} ms -> {period} ms (every value that runs on it changes speed)");
                plan.GlobalSequences[gs] = period;
                changedAny = true;
            }
            foreach (var (name, bs) in gact.Bones)
            {
                if (!finalOfName.TryGetValue(name, out int fb)) continue;
                var pb = plan.Bones[fb];
                var tr = Decompose(bs, pb.Pivot);
                foreach (var c in Channels)
                {
                    var raw = OldTrack(pb.OldIndex, c);
                    if (raw == null || raw.GlobalSequence != gs) continue;
                    var orig = raw.GlobalWindow(m.GlobalSequences[gs]);
                    var keys = ChannelKeys(c, tr, bs.TimesMs, period);
                    if (orig.Count == 0 || SameAsWindow(c, keys, orig)) continue;
                    pb.GlobalKeys[c] = ((short)gs, keys);
                    changedAny = true;
                }
            }
            if (changedAny) a.Changes.Add($"global sequence {gs + 1} edited");
        }

        // ================================================================ events (markers) and points
        FinishPoints(m, man, rig, plan, a, info, finalActions, isCharacter);

        // ================================================================ limits and the result
        bool seqChanged = plan.Sequences.Count != oldS || plan.Sequences.Select((p, i) => p.OldIndex != i).Any(x => x) || plan.Sequences.Any(p => p.KeysChanged);
        bool metaChanged = plan.Sequences.Where(p => p.OldIndex >= 0).Any(p =>
        {
            var o = identity.Sequences[p.OldIndex];
            return o.AnimId != p.AnimId || o.Flags != p.Flags || o.Frequency != p.Frequency || o.BlendTime != p.BlendTime || o.MoveSpeed != p.MoveSpeed ||
                   o.ReplayMin != p.ReplayMin || o.ReplayMax != p.ReplayMax || o.NextAlias != p.NextAlias || o.Duration != p.Duration;
        });
        bool gsChanged = !plan.GlobalSequences.SequenceEqual(m.GlobalSequences) || plan.Bones.Any(b => b.GlobalKeys.Count > 0);
        a.Changed = bonesChanged || seqChanged || metaChanged || gsChanged || plan.Attachments != null || plan.Events != null;
        if (a.Changed)
        {
            Rule(RuleLevel.Info, "Unchanged animations keep their keys byte for byte; new or edited ones get their own place at the end of the model's timeline (nothing else moves). Keys are stored linear, as the 1.12 client plays them (curves from Blender are sampled and reduced to the keys that matter).");
            Rule(RuleLevel.Info, "The animation lookup, the variation chains, the playable-animation table (the game's fallbacks), the key-bone lookup and the attachment lookup are rebuilt where they changed.");
        }
        return a;
    }

    private static string SomeNames(AnimationCatalog cat) => cat.Rows.Count == 0 ? "Stand, Walk, Run, Attack1H" : string.Join(", ", new[] { 0, 4, 5, 17, 60, 69 }.Where(cat.Exists).Select(cat.Name));

    public static string KeyName(int key) => key < 0 ? "none" : key < M2Schema.KeyBones.Length ? $"{M2Schema.KeyBones[key]} ({key})" : key.ToString(CultureInfo.InvariantCulture);

    private static string KeyBoneUse(int key) => key switch
    {
        6 => "the camera, helmets, the name plate and many spell effects",
        0 or 1 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 => "weapons and hand effects follow the arms and fingers",
        2 or 3 => "shoulder armour",
        4 or 5 => "the body's movement and effects",
        7 => "speech (jaw movement)",
        26 => "the whole model (the root)",
        _ => "game code and effects",
    };

    // ---------------------------------------------------------------- keys from samples

    private sealed record TrsSamples(Vector3[] T, Quaternion[] R, Vector3[] S);

    private static TrsSamples Decompose(BoneSamples bs, Vector3 pivot)
    {
        int n = bs.Local.Count;
        var t = new Vector3[n];
        var r = new Quaternion[n];
        var s = new Vector3[n];
        for (int i = 0; i < n; i++) (t[i], r[i], s[i]) = BoneTransform.Decompose(bs.Local[i], pivot);
        return new TrsSamples(t, r, s);
    }

    private static double[] Comp(string c, TrsSamples x, int i) => c switch
    {
        "translation" => new double[] { x.T[i].X, x.T[i].Y, x.T[i].Z },
        "rotation" => new double[] { x.R[i].X, x.R[i].Y, x.R[i].Z, x.R[i].W },
        _ => new double[] { x.S[i].X, x.S[i].Y, x.S[i].Z },
    };

    private static double SameTol(string c) => c == "translation" ? SameTranslation : c == "rotation" ? SameRotation : SameScale;

    /// <summary>The samples of a channel reduced to linear keys within 0 .. duration (ms rounded; quaternions in one
    /// hemisphere).</summary>
    private static SeqKeys ChannelKeys(string c, TrsSamples x, List<double> timesMs, uint duration)
    {
        var times = new List<uint>();
        var vals = new List<double[]>();
        for (int i = 0; i < timesMs.Count; i++)
        {
            double t = Math.Round(timesMs[i]);
            if (t < 0 || t > duration) continue;
            uint tu = (uint)t;
            if (times.Count > 0 && times[^1] == tu) { vals[^1] = Comp(c, x, i); continue; }
            times.Add(tu);
            vals.Add(Comp(c, x, i));
        }
        if (times.Count == 0 && timesMs.Count > 0) { times.Add(0); vals.Add(Comp(c, x, 0)); }
        if (times.Count > 0 && times[^1] < duration && times[^1] > 0)
        {
            // hold the last pose to the end
            times.Add(duration);
            vals.Add(vals[^1]);
        }
        double tol = c == "translation" ? ReduceTranslation : c == "rotation" ? ReduceRotation : ReduceScale;
        var (rt, rv) = KeyReducer.Reduce(times, vals, tol, c == "rotation");
        var k = new SeqKeys(true);
        string type = c == "rotation" ? "4f4" : "3f4";
        for (int i = 0; i < rt.Count; i++) k.Add(rt[i], TrackValues.FromComponents(type, rv[i]));
        return k;
    }

    private static bool SameAsWindow(string c, SeqKeys keys, SeqKeys orig)
    {
        // evaluate both piecewise-linear key lists at every key time of either
        string type = c == "rotation" ? "4f4" : "3f4";
        foreach (var t in keys.Times.Concat(orig.Times).Distinct())
        {
            var a = EvalKeys(keys, t, type);
            var b = EvalKeys(orig, t, type);
            // relative for large values (a translation of 159 yd carries float rounding of the export)
            double scale = Math.Max(1, TrackValues.Components(type, b).Max(Math.Abs));
            if (TrackValues.Distance(type, a, b) > SameTol(c) * scale) return false;
        }
        return true;
    }

    private static byte[] EvalKeys(SeqKeys k, uint t, string type)
    {
        if (k.Count == 1 || t <= k.Times[0]) return k.Values![0];
        if (t >= k.Times[^1]) return k.Values![^1];
        int i = k.Times.FindLastIndex(x => x <= t);
        float f = (float)(t - k.Times[i]) / (k.Times[i + 1] - k.Times[i]);
        return RawTrack.Lerp(type, k.Values![i], k.Values[i + 1], f);
    }

    /// <summary>Do the sampled matrices equal the original bone's at every sample time (the client's values)?</summary>
    private static bool SameMatrices(BoneSamples bs, Vector3 pivot, int seq, M2Sequence sq, int oldBone, Func<int, string, RawTrack?> oldTrack)
    {
        var t = oldTrack(oldBone, "translation")!;
        var r = oldTrack(oldBone, "rotation")!;
        var sc = oldTrack(oldBone, "scale")!;
        if (t.GlobalSequence >= 0 || r.GlobalSequence >= 0 || sc.GlobalSequence >= 0) return false;
        for (int i = 0; i < bs.Local.Count; i++)
        {
            uint time = (uint)Math.Clamp(Math.Round(bs.TimesMs[i]), 0, sq.Duration);
            var tv = t.Evaluate(seq, sq.Start + time) is { } a ? TrackValues.ToVec3(a) : Vector3.Zero;
            var rv = r.Evaluate(seq, sq.Start + time) is { } b ? TrackValues.ToQuat(b) : Quaternion.Identity;
            var sv = sc.Evaluate(seq, sq.Start + time) is { } c ? TrackValues.ToVec3(c) : Vector3.One;
            if (rv.LengthSquared() > 1e-12f) rv = Quaternion.Normalize(rv);
            var o = M2Anim.BoneLocal(pivot, tv, rv, sv);
            var l = bs.Local[i];
            double mag = Math.Max(1, Math.Max(Math.Abs(o.M41), Math.Max(Math.Abs(o.M42), Math.Abs(o.M43))));
            if (Math.Abs(o.M11 - l.M11) > SameRotation || Math.Abs(o.M12 - l.M12) > SameRotation || Math.Abs(o.M13 - l.M13) > SameRotation ||
                Math.Abs(o.M21 - l.M21) > SameRotation || Math.Abs(o.M22 - l.M22) > SameRotation || Math.Abs(o.M23 - l.M23) > SameRotation ||
                Math.Abs(o.M31 - l.M31) > SameRotation || Math.Abs(o.M32 - l.M32) > SameRotation || Math.Abs(o.M33 - l.M33) > SameRotation ||
                Math.Abs(o.M41 - l.M41) > SameTranslation * mag || Math.Abs(o.M42 - l.M42) > SameTranslation * mag || Math.Abs(o.M43 - l.M43) > SameTranslation * mag)
                return false;
        }
        return true;
    }

    private static bool IsRest(string c, SeqKeys k) => k.Values!.All(v => TrackValues.Distance(c == "rotation" ? "4f4" : "3f4", v, TrackValues.BoneRest(c)) <= SameTol(c));

    /// <summary>Fill the bone keys of sequence <paramref name="s"/> from the action's samples; returns how many bones
    /// got new keys.</summary>
    private static int KeysFromSamples(M2Model m, RigPlan plan, int s, EditedAction act, Dictionary<string, int> finalOfName,
        Func<int, string, RawTrack?> oldTrack, bool retimed, RigAnalysis a)
    {
        var ps = plan.Sequences[s];
        var oldSq = ps.OldIndex >= 0 ? m.Sequences[ps.OldIndex] : null;
        int changed = 0;
        var sampled = new HashSet<int>();
        foreach (var (name, bs) in act.Bones)
        {
            if (!finalOfName.TryGetValue(name, out int fb) || bs.Local.Count == 0) continue;
            sampled.Add(fb);
            var pb = plan.Bones[fb];
            // the whole transform the same as the original at every sample: nothing changed (also when a scale of 0
            // makes the rotation meaningless)
            if (pb.OldIndex >= 0 && oldSq != null && !retimed && pb.Pivot == m.Bones[pb.OldIndex].Pivot && SameMatrices(bs, pb.Pivot, ps.OldIndex, oldSq, pb.OldIndex, oldTrack))
                continue;
            var tr = Decompose(bs, pb.Pivot);
            var ck = new BoneChannelKeys();
            bool any = false;
            foreach (var c in Channels)
            {
                var raw = oldTrack(pb.OldIndex, c);
                if (raw != null && raw.GlobalSequence >= 0) continue;        // runs on its global sequence: edited there
                var keys = ChannelKeys(c, tr, bs.TimesMs, ps.Duration);
                SeqKeys? orig = null;
                if (raw != null && oldSq != null && !retimed && raw.KeyCount > 0 && pb.Pivot == m.Bones[pb.OldIndex].Pivot)
                    orig = raw.Window(ps.OldIndex, oldSq.Start, oldSq.End);
                if (orig != null && SameAsWindow(c, keys, orig)) continue;   // the same motion: keep the original keys
                bool rawEmpty = raw == null || raw.KeyCount == 0;
                if (rawEmpty && IsRest(c, keys)) continue;                   // never animated and still at rest
                ck.Set(c, keys);
                any = true;
            }
            if (any)
            {
                pb.Keys[s] = ck;
                changed++;
            }
        }
        // bones animated in the original sequence but without keys in the action now: back to the rest pose
        if (oldSq != null)
            for (int k = 0; k < plan.Bones.Count; k++)
            {
                var pb = plan.Bones[k];
                if (pb.OldIndex < 0 || sampled.Contains(k)) continue;
                var ck = new BoneChannelKeys();
                bool any = false;
                foreach (var c in Channels)
                {
                    var raw = oldTrack(pb.OldIndex, c)!;
                    if (raw.GlobalSequence >= 0 || raw.KeyCount == 0) continue;
                    var w = raw.Window(ps.OldIndex, oldSq.Start, oldSq.End);
                    if (w.Count == 0 || IsRest(c, w)) continue;
                    ck.Set(c, SeqKeys.Constant(TrackValues.BoneRest(c)));
                    any = true;
                }
                if (any)
                {
                    pb.Keys[s] = ck;
                    changed++;
                }
            }
        // new sequences: bones that are the same constant (not the rest pose) in every sequence keep it
        if (oldSq == null)
            for (int k = 0; k < plan.Bones.Count; k++)
            {
                var pb = plan.Bones[k];
                if (pb.OldIndex < 0 || sampled.Contains(k)) continue;
                var ck = new BoneChannelKeys();
                bool any = false;
                foreach (var c in Channels)
                {
                    var raw = oldTrack(pb.OldIndex, c)!;
                    if (raw.GlobalSequence >= 0 || raw.KeyCount == 0) continue;
                    byte[]? constant = null;
                    bool same = true;
                    for (int q = 0; q < m.Sequences.Length && same; q++)
                    {
                        var w = raw.Window(q, m.Sequences[q].Start, m.Sequences[q].End);
                        if (w.Count == 0) continue;
                        foreach (var v in w.Values!)
                        {
                            constant ??= v;
                            if (TrackValues.Distance(raw.ValueType!, v, constant) > 1e-6) { same = false; break; }
                        }
                    }
                    if (same && constant != null && TrackValues.Distance(raw.ValueType!, constant, TrackValues.BoneRest(c)) > 1e-6)
                    {
                        ck.Set(c, SeqKeys.Constant(constant));
                        any = true;
                    }
                }
                if (any) pb.Keys[s] = ck;
            }
        return changed;
    }

    // ---------------------------------------------------------------- events and attachment points

    private static void FinishPoints(M2Model m, ModelPackageManifest man, EditedRig rig, RigPlan plan, RigAnalysis a, RigInfo info, List<EditedAction?>? finalActions, bool isCharacter)
    {
        var mr = man.Rig ?? new PackageRig();
        void Rule(RuleLevel l, string t) => a.Rules.Add(new ModelRule(l, t));
        var finalOfName = plan.Bones.Select((b, i) => (b.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        int FinalOfOld(int old)
        {
            for (int guard = 0; old >= 0 && old < m.Bones.Length && guard < 1024; guard++)
            {
                int k = plan.Bones.FindIndex(b => b.OldIndex == old);
                if (k >= 0) return k;
                old = m.Bones[old].Parent;
            }
            return 0;
        }
        int BoneOf(string? name, int fallbackOld) => name != null && finalOfName.TryGetValue(name, out int k) ? k : FinalOfOld(fallbackOld);

        // ---- attachments
        var attPoints = rig.Points.Where(p => p.Kind == "attachment").ToList();
        if (attPoints.Count > 0 || m.Attachments.Length == 0)
        {
            var list = new List<PlanAttachment>();
            var claimed = new HashSet<int>();
            foreach (var p in attPoints)
            {
                int old = p.WowIndex is int w && w >= 0 && w < m.Attachments.Length && claimed.Add(w) ? w : -1;
                var oa = old >= 0 ? m.Attachments[old] : null;
                int id = p.Id ?? oa?.Id ?? -1;
                if (id < 0) { Rule(RuleLevel.Warning, $"The empty {p.Name} has no attachment id (name it ATT_<id>_..., for example ATT_11_Helm): it is ignored."); continue; }
                var pos = oa != null && Vector3.Distance(oa.Position, p.Position) <= PositionTolerance ? oa.Position : p.Position;
                list.Add(new PlanAttachment { OldIndex = old, Id = id, Bone = BoneOf(p.Bone, oa?.Bone ?? 0), Position = pos });
                string an = RigAttachment.AttachmentName(id);
                if (id >= M2Schema.AttachmentNames.Length) Rule(RuleLevel.Warning, $"Attachment id {id} ({p.Name}) is not one the 1.12 client asks for (0-{M2Schema.AttachmentNames.Length - 1}).");
                if (oa == null) a.Changes.Add($"new attachment point {an} ({id}) on {plan.Bones[list[^1].Bone].Name}");
                else
                {
                    if (pos != oa.Position) a.Changes.Add($"attachment point {RigAttachment.AttachmentName(oa.Id)} moved by {Vector3.Distance(pos, oa.Position):0.###} yd");
                    if (list[^1].Bone != FinalOfOld(oa.Bone)) a.Changes.Add($"attachment point {an} now follows {plan.Bones[list[^1].Bone].Name}");
                    if (id != oa.Id) a.Changes.Add($"attachment point {RigAttachment.AttachmentName(oa.Id)} is now {an} ({id})");
                }
            }
            // the original attachment points keep their order, new ones follow
            list = list.Where(x => x.OldIndex >= 0).OrderBy(x => x.OldIndex).Concat(list.Where(x => x.OldIndex < 0)).ToList();
            var gone = Enumerable.Range(0, m.Attachments.Length).Where(i => !claimed.Contains(i)).ToList();
            foreach (int i in gone)
            {
                string an = RigAttachment.AttachmentName(m.Attachments[i].Id);
                Rule(isCharacter ? RuleLevel.Blocker : RuleLevel.Warning, $"The attachment point {an} ({m.Attachments[i].Id}) was removed: {AttachmentUse(m.Attachments[i].Id)} no longer show{(isCharacter ? ". Character models must keep their attachment points" : "")}.");
                a.Changes.Add($"attachment point {an} removed");
            }
            bool same = gone.Count == 0 && list.Count == m.Attachments.Length && list.Select((x, i) => x.OldIndex == i && x.Id == m.Attachments[i].Id &&
                        x.Position == m.Attachments[i].Position && x.Bone == FinalOfOld(m.Attachments[i].Bone)).All(x => x);
            if (!same) plan.Attachments = list;
        }

        // ---- events: empties (bone, position) and markers (trigger times)
        var evPoints = rig.Points.Where(p => p.Kind == "event").ToList();
        var markerToEvent = mr.Events.ToDictionary(e => e.Marker, e => e.Index, StringComparer.Ordinal);
        var events = info.Events.Select(e => new PlanEvent { OldIndex = e.Index, Code = e.Code, Data = e.Data, Bone = FinalOfOld(e.Bone), Position = e.Position }).ToList();
        bool evChanged = false;
        foreach (var p in evPoints)
        {
            if (p.WowIndex is int w && w >= 0 && w < events.Count)
            {
                var pe = events[w];
                var oe = info.Events[w];
                if (Vector3.Distance(oe.Position, p.Position) > PositionTolerance) { pe.Position = p.Position; evChanged = true; a.Changes.Add($"event {oe.Code} moved by {Vector3.Distance(p.Position, oe.Position):0.###} yd"); }
                int nb = BoneOf(p.Bone, oe.Bone);
                if (nb != pe.Bone) { pe.Bone = nb; evChanged = true; a.Changes.Add($"event {oe.Code} now follows {plan.Bones[nb].Name}"); }
            }
        }
        if (finalActions != null)
        {
            var newCodes = new Dictionary<string, PlanEvent>(StringComparer.Ordinal);
            for (int s = 0; s < finalActions.Count; s++)
            {
                var act = finalActions[s];
                if (act == null) continue;
                int old = plan.Sequences[s].OldIndex;
                var byEvent = new Dictionary<int, SeqKeys>();
                foreach (var (marker, tms) in act.Markers)
                {
                    // event codes can start with a space (" $GC" in some doodads): the exact name first
                    string mk = markerToEvent.ContainsKey(marker) ? marker : Suffix.Replace(marker, "");
                    if (!markerToEvent.ContainsKey(mk) && markerToEvent.ContainsKey(mk.Trim())) mk = mk.Trim();
                    int ei;
                    if (markerToEvent.TryGetValue(mk, out int known)) ei = known;
                    else if (mk.Trim().Length >= 3 && mk.Trim().StartsWith('$') && !mk.Trim().Contains(' '))
                    {
                        string code = mk.Trim().Length > 4 ? mk.Trim()[..4] : mk.Trim();
                        int existing = events.FindIndex(e => e.Code == code);
                        if (existing >= 0) ei = existing;
                        else
                        {
                            if (!newCodes.TryGetValue(code, out var ne))
                            {
                                var pt = evPoints.FirstOrDefault(p => p.WowIndex == null && p.Code == code);
                                ne = new PlanEvent { Code = code, Bone = BoneOf(pt?.Bone, 0), Position = pt?.Position ?? Vector3.Zero };
                                newCodes[code] = ne;
                                events.Add(ne);
                                a.Changes.Add($"new event {code} ({AnimationEvents.Describe(code)})");
                                if (pt == null) Rule(RuleLevel.Info, $"The new event {code} has no empty (EVT_.._{code}): it sits at the model's origin on {plan.Bones.FirstOrDefault()?.Name ?? "the first bone"}.");
                            }
                            ei = events.IndexOf(ne);
                        }
                    }
                    else
                    {
                        Rule(RuleLevel.Warning, $"The marker \"{marker}\" in {act.Name} is not an event (event markers are named like $FSD or $CSL): it is ignored.");
                        continue;
                    }
                    if (!byEvent.TryGetValue(ei, out var k)) byEvent[ei] = k = new SeqKeys(false);
                    k.Times.Add((uint)Math.Clamp(Math.Round(tms), 0, plan.Sequences[s].Duration));
                }
                for (int ei = 0; ei < events.Count; ei++)
                {
                    var times = byEvent.TryGetValue(ei, out var k) ? k : new SeqKeys(false);
                    times.SortAndDedupe();
                    var orig = old >= 0 && ei < info.Events.Count ? info.Events[ei].Triggers.Where(t => t.Sequence == old).Select(t => t.Time).OrderBy(x => x).ToList() : new List<uint>();
                    // unchanged markers: kept (and stretched with a retimed animation)
                    if (times.Times.SequenceEqual(orig)) continue;
                    events[ei].Triggers[s] = times;
                    if (!times.Times.SequenceEqual(orig))
                    {
                        evChanged = true;
                        a.Changes.Add($"{act.Name}: event {events[ei].Code} at {(times.Count == 0 ? "no time" : string.Join(", ", times.Times.Select(x => $"{x / 1000.0:0.###} s")))}");
                    }
                }
            }
            if (newCodes.Count > 0) evChanged = true;
        }
        if (evChanged) plan.Events = events;
    }

    private static string AttachmentUse(int id) => id switch
    {
        0 => "shields", 1 => "weapons in the right hand", 2 => "weapons in the left hand", 5 or 6 => "shoulder armour", 11 => "helmets",
        17 => "breath effects", 18 => "the name plate", 21 or 22 => "spell effects in the hands", 26 or 27 or 28 => "sheathed weapons", 29 => "the name plate on a mount",
        _ => "the things the game attaches there",
    };
}
