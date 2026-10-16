using System.Buffers.Binary;
using System.Numerics;
using WoWData.Core.ModelIO;

namespace WoWData.Core.M2;

/// <summary>New keys of one bone in one sequence (null channel = no new keys for it).</summary>
public sealed class BoneChannelKeys
{
    public SeqKeys? Translation { get; set; }
    public SeqKeys? Rotation { get; set; }
    public SeqKeys? Scale { get; set; }

    public SeqKeys? Of(string channel) => channel switch { "translation" => Translation, "rotation" => Rotation, _ => Scale };

    public void Set(string channel, SeqKeys? k)
    {
        switch (channel)
        {
            case "translation": Translation = k; break;
            case "rotation": Rotation = k; break;
            default: Scale = k; break;
        }
    }
}

/// <summary>A bone of the edited skeleton (in the final order: a parent always comes before its children).</summary>
public sealed class PlanBone
{
    /// <summary>The bone's index in the original model, -1 for a new bone.</summary>
    public int OldIndex { get; set; } = -1;
    public string Name { get; set; } = "";
    /// <summary>The parent's index in the plan's bone list (-1 = none).</summary>
    public int Parent { get; set; } = -1;
    public int KeyBoneId { get; set; } = -1;
    public uint Flags { get; set; }
    public ushort SubmeshId { get; set; }
    public Vector3 Pivot { get; set; }
    /// <summary>New keys by final sequence index. A sequence without an entry keeps the original keys (moved with the
    /// sequence when it moves on the timeline), or the rest pose for a new bone / a new sequence.</summary>
    public Dictionary<int, BoneChannelKeys> Keys { get; } = new();
    /// <summary>Global-sequence channels written again: channel -> (global sequence, keys on its clock).</summary>
    public Dictionary<string, (short GlobalSequence, SeqKeys Keys)> GlobalKeys { get; } = new();
}

/// <summary>A sequence of the edited model (in the final order).</summary>
public sealed class PlanSequence
{
    public int OldIndex { get; set; } = -1;
    public int AnimId { get; set; }
    public int SubId { get; set; }
    public float MoveSpeed { get; set; }
    public uint Flags { get; set; }
    public short Frequency { get; set; }
    public uint ReplayMin { get; set; }
    public uint ReplayMax { get; set; }
    public uint BlendTime { get; set; }
    public ushort NextAlias { get; set; }
    public uint Duration { get; set; }
    /// <summary>The original sequence whose colours, transparency, texture animations, particles... a NEW sequence
    /// copies (stretched to its duration).</summary>
    public int Template { get; set; } = -1;
    /// <summary>The bone keys of the sequence changed (new keys in <see cref="PlanBone.Keys"/>): it gets a new place at
    /// the end of the timeline, so no other sequence moves.</summary>
    public bool KeysChanged { get; set; }
    public string Label { get; set; } = "";
}

public sealed class PlanAttachment
{
    public int OldIndex { get; set; } = -1;
    public int Id { get; set; }
    /// <summary>The bone in the plan's bone list.</summary>
    public int Bone { get; set; }
    public Vector3 Position { get; set; }
}

public sealed class PlanEvent
{
    public int OldIndex { get; set; } = -1;
    public string Code { get; set; } = "";
    public uint Data { get; set; }
    public int Bone { get; set; }
    public Vector3 Position { get; set; }
    /// <summary>New trigger times by final sequence index (relative to the sequence start); a sequence without an entry
    /// keeps its triggers.</summary>
    public Dictionary<int, SeqKeys> Triggers { get; } = new();
}

/// <summary>
/// 0.10.0: an edit of a model's skeleton, animations, attachment points and events, in the model's own terms. The
/// import analysis builds it from what came back from Blender; <see cref="RigWriter"/> writes it. A plan made by
/// <see cref="Identity"/> reproduces the model byte for byte.
/// </summary>
public sealed class RigPlan
{
    public List<PlanBone> Bones { get; } = new();
    public List<PlanSequence> Sequences { get; } = new();
    public List<uint> GlobalSequences { get; } = new();
    /// <summary>Null: the attachment points stay as they are (except bone indices, when bones move).</summary>
    public List<PlanAttachment>? Attachments { get; set; }
    /// <summary>Null: the events stay as they are.</summary>
    public List<PlanEvent>? Events { get; set; }

    /// <summary>The plan of an unchanged model.</summary>
    public static RigPlan Identity(M2Model m)
    {
        var p = new RigPlan();
        for (int i = 0; i < m.Bones.Length; i++)
        {
            var b = m.Bones[i];
            p.Bones.Add(new PlanBone { OldIndex = i, Name = RigInfo.BoneName(i, b.KeyBoneId), Parent = b.Parent, KeyBoneId = b.KeyBoneId, Flags = b.Flags, SubmeshId = (ushort)b.SubmeshId, Pivot = b.Pivot });
        }
        var sd = m.File["sequences"];
        for (int i = 0; i < m.Sequences.Length; i++)
        {
            var s = m.Sequences[i];
            p.Sequences.Add(new PlanSequence
            {
                OldIndex = i, AnimId = s.AnimId, SubId = s.SubId, MoveSpeed = s.MoveSpeed, Flags = s.Flags, Frequency = s.Frequency,
                ReplayMin = sd.U32(i, "replay", 0), ReplayMax = sd.U32(i, "replay", 1), BlendTime = s.BlendTime, NextAlias = (ushort)s.NextAlias, Duration = s.Duration,
            });
        }
        p.GlobalSequences.AddRange(m.GlobalSequences);
        return p;
    }
}

public sealed class RigWriteResult
{
    /// <summary>Original bone index -> final index (-1: removed).</summary>
    public int[] BoneMap { get; init; } = Array.Empty<int>();
    /// <summary>Final indices of the sequences that got a new place on the timeline (their bounds are recomputed).</summary>
    public List<int> Relocated { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Changed { get; set; }
}

public sealed class RigEditException : Exception
{
    public RigEditException(string message) : base(message) { }
}

/// <summary>
/// Writes a <see cref="RigPlan"/> into a parsed model. Everything the plan does not change keeps its bytes:
///  * unchanged sequences keep their place on the timeline and their keys in every track (the tracks are written in
///    append mode, <see cref="TrackEncoder"/>); a sequence with new keys or a new length, and every new sequence, gets a
///    place after the last key of the model, so nothing else moves;
///  * the colours, transparency, texture animations, attachment visibility, lights, cameras, ribbons and particles of a
///    moved sequence move with it (stretched when it is retimed); a new sequence copies them from its template;
///  * the animation lookup, the playable-animation table (recomputed with the client's algorithm when the set of
///    animations changes), the variation chains, the key-bone lookup and the attachment lookup are rebuilt only when
///    their content changes;
///  * bone indices everywhere (parents, vertices, the views' bone lookup, submesh centre bones, attachments, events,
///    lights, ribbons, particles) follow the new bone order; a removed bone's vertices and effects go to its nearest
///    remaining ancestor.
/// </summary>
public static class RigWriter
{
    private static readonly string[] TrackArrays = { "colors", "transparency", "track_6c", "texture_transforms", "lights", "cameras", "ribbons", "particles" };

    public static RigWriteResult Apply(M2File f, RigPlan plan, AnimationCatalog? catalog = null)
    {
        catalog ??= AnimationCatalog.Empty;
        var m = M2Model.FromFile(f);
        int oldBones = m.Bones.Length, oldSeqs = m.Sequences.Length;
        var res = new RigWriteResult { BoneMap = new int[oldBones] };
        Array.Fill(res.BoneMap, -1);
        for (int k = 0; k < plan.Bones.Count; k++)
        {
            var b = plan.Bones[k];
            if (b.Parent >= k) throw new RigEditException($"bone {b.Name}: its parent comes after it (the plan must list parents first)");
            if (b.OldIndex >= 0)
            {
                if (b.OldIndex >= oldBones || res.BoneMap[b.OldIndex] >= 0) throw new RigEditException($"bone {b.Name}: original bone {b.OldIndex} used twice or out of range");
                res.BoneMap[b.OldIndex] = k;
            }
        }
        // the final index of an original bone, or of its nearest remaining ancestor
        int Remap(int old)
        {
            for (int guard = 0; old >= 0 && old < oldBones && guard < 1024; guard++)
            {
                if (res.BoneMap[old] >= 0) return res.BoneMap[old];
                old = m.Bones[old].Parent;
            }
            return 0;
        }
        bool bonesMoved = plan.Bones.Count != oldBones || plan.Bones.Select((b, k) => b.OldIndex != k).Any(x => x);

        // ------------------------------------------------ sequences and the timeline
        int ns = plan.Sequences.Count;
        var starts = new uint[ns];
        var durations = new uint[ns];
        var relocated = new bool[ns];
        uint maxTime = 0;
        foreach (var s in m.Sequences) maxTime = Math.Max(maxTime, Math.Max(s.End, s.Start));
        foreach (var name in TrackArrays.Concat(new[] { "bones", "attachments", "events" }))
        {
            var arr = f[name];
            for (int i = 0; i < arr.Count; i++)
                foreach (var t in arr.Tracks(i))
                    if (t.GlobalSequence < 0 && t.KeyCount > 0)
                        foreach (var x in t.Timestamps.AsU32())
                            if (x < 0x80000000u) maxTime = Math.Max(maxTime, x);   // a few client files have "negative" times (-33)
        }
        uint next = RoundUp((ulong)maxTime + 1000);
        for (int s = 0; s < ns; s++)
        {
            var ps = plan.Sequences[s];
            if (ps.OldIndex >= oldSeqs) throw new RigEditException($"sequence {s}: original sequence {ps.OldIndex} does not exist");
            durations[s] = ps.Duration;
            relocated[s] = ps.OldIndex < 0 || ps.KeysChanged || ps.Duration != m.Sequences[ps.OldIndex].Duration;
            if (!relocated[s])
            {
                starts[s] = m.Sequences[ps.OldIndex].Start;
                continue;
            }
            starts[s] = next;
            ulong after = (ulong)next + ps.Duration + 1000;
            if (after > uint.MaxValue - 100000) throw new RigEditException("the animations no longer fit on the model's timeline (more than 49 days of animation)");
            next = RoundUp(after);
            res.Relocated.Add(s);
        }
        // the old sequence and window that a sequence's non-bone tracks come from (template for new ones)
        (int Old, uint OldStart, uint OldEnd)? SourceOf(int s)
        {
            var ps = plan.Sequences[s];
            int old = ps.OldIndex >= 0 ? ps.OldIndex : ps.Template;
            if (old < 0 || old >= oldSeqs) old = oldSeqs > 0 ? DefaultTemplate(m) : -1;
            if (old < 0) return null;
            return (old, m.Sequences[old].Start, m.Sequences[old].End);
        }
        // sources of a track that is not a bone's: kept, moved with the sequence, or copied from the template
        List<SeqSource> NonBoneSources(RawTrack raw, bool events)
        {
            var l = new List<SeqSource>(ns);
            for (int s = 0; s < ns; s++)
            {
                var ps = plan.Sequences[s];
                if (!relocated[s]) { l.Add(SeqSource.Keep(ps.OldIndex)); continue; }
                if (events && ps.OldIndex < 0) { l.Add(SeqSource.New(new SeqKeys(false))); continue; }
                var src = SourceOf(s);
                if (src == null) { l.Add(SeqSource.New(new SeqKeys(raw.HasValues))); continue; }
                var (old, os, oe) = src.Value;
                var w = raw.Window(old, os, oe).Stretched(oe - os, durations[s]).ClippedTo(durations[s], raw.ValueType, raw.Interpolation);
                l.Add(SeqSource.New(w));
            }
            return l;
        }
        bool seqLayoutChanged = ns != oldSeqs || res.Relocated.Count > 0 || plan.Sequences.Select((s, i) => s.OldIndex != i).Any(x => x);
        bool anyChange = bonesMoved || seqLayoutChanged;

        // ------------------------------------------------ bones
        var boneArr = f["bones"];
        var boneRec = M2Schema.Bone;
        var oldBoneBytes = boneArr.Bytes;
        var oldBoneSub = boneArr.Sub;
        int nb = plan.Bones.Count;
        var newBoneBytes = new byte[nb * boneRec.Size];
        var newBoneSub = new M2Data[nb][];
        var tmp = new M2Data(M2Elem.Of(boneRec), nb, newBoneBytes, newBoneSub, -1);
        for (int k = 0; k < nb; k++)
        {
            var pb = plan.Bones[k];
            int old = pb.OldIndex;
            if (old >= 0)
            {
                Buffer.BlockCopy(oldBoneBytes, old * boneRec.Size, newBoneBytes, k * boneRec.Size, boneRec.Size);
                newBoneSub[k] = (M2Data[])oldBoneSub[old].Clone();
            }
            else
            {
                newBoneSub[k] = boneRec.Arrays.Select(a => M2Data.Empty(a.Elem)).ToArray();
                foreach (var tf in boneRec.Tracks) BinaryPrimitives.WriteInt16LittleEndian(newBoneBytes.AsSpan(k * boneRec.Size + tf.Offset + 2), -1);
            }
            tmp.SetI32(k, "key_bone_id", pb.KeyBoneId);
            tmp.SetI16(k, "parent", (short)pb.Parent);
            tmp.SetU16(k, "submesh_id", pb.SubmeshId);
            tmp.SetVec3(k, "pivot", pb.Pivot);
            bool animated = false;
            foreach (var tf in boneRec.Tracks)
            {
                var raw = old >= 0 ? RawTrack.Read(new M2TrackView(boneArr, old, tf)) : new RawTrack { Interpolation = 1, ValueType = tf.Value!.Type, Values = new List<byte[]>() };
                RawTrack enc;
                if (pb.GlobalKeys.TryGetValue(tf.Name, out var gk))
                    enc = TrackEncoder.Global(raw.KeyCount > 0 ? raw : new RawTrack { Interpolation = 1, ValueType = tf.Value!.Type, Values = new() }, gk.Keys, gk.GlobalSequence);
                else if (raw.GlobalSequence >= 0) enc = raw;
                else
                {
                    var sources = new List<SeqSource>(ns);
                    bool linearKeys = false;
                    for (int s = 0; s < ns; s++)
                    {
                        var ps = plan.Sequences[s];
                        var keys = pb.Keys.TryGetValue(s, out var ck) ? ck.Of(tf.Name) : null;
                        if (keys != null)
                        {
                            sources.Add(SeqSource.New(keys));
                            if (keys.Count > 1) linearKeys = true;
                        }
                        else if (old >= 0 && ps.OldIndex >= 0 && !relocated[s]) sources.Add(SeqSource.Keep(ps.OldIndex));
                        else if (old >= 0 && ps.OldIndex >= 0)
                        {
                            var o = m.Sequences[ps.OldIndex];
                            sources.Add(SeqSource.New(raw.Window(ps.OldIndex, o.Start, o.End).Stretched(o.Duration, durations[s]).ClippedTo(durations[s], raw.ValueType, raw.Interpolation)));
                        }
                        else sources.Add(SeqSource.New(new SeqKeys(true)));
                    }
                    var fill = TrackValues.BoneRest(tf.Name);
                    var input = raw;
                    if (linearKeys && raw.Interpolation == 0 && raw.KeyCount > 1)
                    {
                        // the new keys are linear but the original track is stepped: its kept sequences are written again
                        // as steps (a key 1 ms before each change), then the whole track is linear
                        for (int s = 0; s < ns; s++)
                        {
                            if (!sources[s].IsKeep) continue;
                            var o = m.Sequences[sources[s].KeepRange];
                            sources[s] = SeqSource.New(StepAsLinear(raw.Window(sources[s].KeepRange, o.Start, o.End)));
                        }
                        input = raw.Clone();
                        input.Interpolation = 1;
                    }
                    enc = TrackEncoder.Encode(input, sources, starts, durations, fill);
                    if (ReferenceEquals(enc, raw) && old >= 0)
                    {
                        animated |= raw.KeyCount > 0;
                        continue;      // unchanged: keep the original blocks
                    }
                    if (raw.KeyCount <= 1 && enc.KeyCount > 1 && enc.Interpolation == 0) enc.Interpolation = 1;
                }
                if (ReferenceEquals(enc, raw) && old >= 0)
                {
                    animated |= raw.KeyCount > 0;
                    continue;      // unchanged: keep the original blocks
                }
                enc.Write(tmp, k, tf);
                animated |= enc.KeyCount > 0;
            }
            // 0x200 marks an animated bone (33,824 of the client's 33,860 animated bones have it)
            uint flags = pb.Flags;
            bool wasAnimated = old >= 0 && m.Bones[old].IsAnimated;
            if (animated && !wasAnimated) flags |= 0x200;
            tmp.SetU32(k, "flags", flags);
        }
        bool bonesChanged = nb != oldBones || !newBoneBytes.AsSpan().SequenceEqual(oldBoneBytes) || Enumerable.Range(0, nb).Any(k => !newBoneSub[k].SequenceEqual(oldBoneSub[k]));
        if (bonesChanged)
        {
            boneArr.Set(nb, newBoneBytes, newBoneSub);
            anyChange = true;
        }

        // ------------------------------------------------ global sequences
        if (!plan.GlobalSequences.SequenceEqual(m.GlobalSequences))
        {
            SetU32Array(f["global_sequences"], plan.GlobalSequences);
            res.Notes.Add("global sequences: " + string.Join(", ", plan.GlobalSequences.Select(x => $"{x} ms")));
            anyChange = true;
        }

        // ------------------------------------------------ sequences, variation chains, lookups
        var seqArr = f["sequences"];
        var seqRec = M2Schema.Sequence;
        var sb = new byte[ns * seqRec.Size];
        var sd = new M2Data(M2Elem.Of(seqRec), ns, sb, null, -1);
        for (int s = 0; s < ns; s++)
        {
            var ps = plan.Sequences[s];
            if (ps.OldIndex >= 0) Buffer.BlockCopy(seqArr.Bytes, ps.OldIndex * seqRec.Size, sb, s * seqRec.Size, seqRec.Size);
            else
            {
                // a new sequence starts with its template's bounds (recomputed after the write)
                var src = SourceOf(s);
                if (src != null) Buffer.BlockCopy(seqArr.Bytes, src.Value.Old * seqRec.Size, sb, s * seqRec.Size, seqRec.Size);
            }
            sd.SetU16(s, "anim_id", (ushort)ps.AnimId);
            sd.SetU16(s, "sub_id", (ushort)ps.SubId);
            sd.SetU32(s, "start", starts[s]);
            sd.SetU32(s, "end", starts[s] + durations[s]);
            sd.SetF32(s, "move_speed", ps.MoveSpeed);
            sd.SetU32(s, "flags", ps.Flags);
            sd.SetI16(s, "frequency", ps.Frequency);
            sd.SetU32(s, "replay", ps.ReplayMin, 0);
            sd.SetU32(s, "replay", ps.ReplayMax, 1);
            sd.SetU32(s, "blend_time", ps.BlendTime);
            sd.SetU16(s, "next_alias", ps.NextAlias);
        }
        // variation chains: the sequences of one animation id, in order
        var lastOf = new Dictionary<int, int>();
        for (int s = ns - 1; s >= 0; s--)
        {
            int id = plan.Sequences[s].AnimId;
            sd.SetI16(s, "next_variant", (short)(lastOf.TryGetValue(id, out int nx) ? nx : -1));
            lastOf[id] = s;
        }
        if (ns != oldSeqs || !sb.AsSpan().SequenceEqual(seqArr.Bytes))
        {
            seqArr.Set(ns, sb);
            anyChange = true;
        }
        var ids = plan.Sequences.Select(s => s.AnimId).ToList();
        var lookup = AnimationCatalog.AnimationLookup(ids);
        if (!lookup.SequenceEqual(m.AnimationLookup))
        {
            SetI16Array(f["animation_lookup"], lookup);
            anyChange = true;
        }
        var oldIds = m.Sequences.Select(s => s.AnimId).ToHashSet();
        if (!oldIds.SetEquals(ids) || ns > 0 && oldSeqs > 0 && plan.Sequences[0].AnimId != m.Sequences[0].AnimId && lookup.Length > 0 && (ushort)lookup[0] == 0xFFFF)
        {
            var pl = f["playable_lookup"];
            int count = Math.Max(pl.Count, AnimationCatalog.PlayableCount);
            if (catalog.Rows.Count > 0)
            {
                var table = catalog.PlayableTable(lookup, ns > 0 ? plan.Sequences[0].AnimId : 0, count);
                var pb = new byte[count * 4];
                for (int i = 0; i < count; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(pb.AsSpan(i * 4), table[i].AnimId);
                    BinaryPrimitives.WriteUInt16LittleEndian(pb.AsSpan(i * 4 + 2), table[i].Flags);
                }
                if (!pb.AsSpan().SequenceEqual(pl.Bytes))
                {
                    pl.Set(count, pb);
                    res.Notes.Add("the playable-animation table was recomputed (the animations the game falls back to)");
                    anyChange = true;
                }
            }
            else res.Notes.Add("the playable-animation table was kept (AnimationData.dbc was not available)");
        }
        // key bones: the first bone with each id
        int maxKey = plan.Bones.Count == 0 ? -1 : plan.Bones.Max(b => b.KeyBoneId);
        var kbl = new short[Math.Max(1, maxKey + 1)];
        Array.Fill(kbl, (short)-1);
        for (int k = plan.Bones.Count - 1; k >= 0; k--)
            if (plan.Bones[k].KeyBoneId >= 0) kbl[plan.Bones[k].KeyBoneId] = (short)k;
        if (!kbl.SequenceEqual(m.KeyBoneLookup) && !(m.KeyBoneLookup.Length == 0 && maxKey < 0))
        {
            SetI16Array(f["key_bone_lookup"], kbl);
            anyChange = true;
        }

        // ------------------------------------------------ bone references
        if (bonesMoved)
        {
            var vb = f["vertices"].Bytes;
            for (int v = 0; v < vb.Length / 0x30; v++)
                for (int c = 0; c < 4; c++)
                {
                    int o = v * 0x30 + 16 + c;
                    if (vb[v * 0x30 + 12 + c] == 0 && vb[o] == 0) continue;
                    int nbi = Remap(vb[o]);
                    if (nbi > 255) throw new RigEditException($"a vertex would use bone {nbi}: vertices can only use the first 256 bones (0-255). Put the bones that move vertices first.");
                    vb[o] = (byte)nbi;
                }
            var bl = f["bone_lookup"];
            for (int i = 0; i < bl.Count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bl.Bytes.AsSpan(i * 2), (ushort)Remap(BinaryPrimitives.ReadUInt16LittleEndian(bl.Bytes.AsSpan(i * 2))));
            var views = f["views"];
            for (int vi = 0; vi < views.Count; vi++)
            {
                var subs = views.Child(vi, "submeshes");
                for (int si = 0; si < subs.Count; si++) subs.SetU16(si, "center_bone", (ushort)Remap(subs.U16(si, "center_bone")));
            }
            RemapField(f["attachments"], "bone", Remap);
            RemapField(f["events"], "bone", Remap);
            RemapField(f["lights"], "bone", Remap);
            RemapField(f["ribbons"], "bone", Remap);
            RemapField(f["particles"], "bone", Remap);
            res.Notes.Add("bone numbers updated everywhere (vertices, the views' bone lists, attachment points, events, lights, ribbons, particles)");
        }

        // ------------------------------------------------ attachments
        var att = f["attachments"];
        var attRec = M2Schema.Attachment;
        if (plan.Attachments != null)
        {
            int na = plan.Attachments.Count;
            var ab = new byte[na * attRec.Size];
            var asub = new M2Data[na][];
            var ad = new M2Data(M2Elem.Of(attRec), na, ab, asub, -1);
            for (int i = 0; i < na; i++)
            {
                var pa = plan.Attachments[i];
                if (pa.OldIndex >= 0 && pa.OldIndex < att.Count)
                {
                    Buffer.BlockCopy(att.Bytes, pa.OldIndex * attRec.Size, ab, i * attRec.Size, attRec.Size);
                    asub[i] = (M2Data[])att.Sub[pa.OldIndex].Clone();
                }
                else
                {
                    asub[i] = attRec.Arrays.Select(a => M2Data.Empty(a.Elem)).ToArray();
                    var vis = new RawTrack { Interpolation = 0, GlobalSequence = -1, ValueType = "u1", Values = new List<byte[]> { new byte[] { 1 } }, Times = new List<uint> { 0 } };
                    vis.Write(ad, i, attRec.Track("visible"));
                }
                ad.SetU32(i, "id", (uint)pa.Id);
                ad.SetU16(i, "bone", (ushort)pa.Bone);
                ad.SetVec3(i, "position", pa.Position);
            }
            if (na != att.Count || !ab.AsSpan().SequenceEqual(att.Bytes) || Enumerable.Range(0, na).Any(i => plan.Attachments[i].OldIndex != i))
            {
                att.Set(na, ab, asub);
                var lk = new short[na == 0 ? 0 : plan.Attachments.Max(a => a.Id) + 1];
                Array.Fill(lk, (short)-1);
                for (int i = na - 1; i >= 0; i--) lk[plan.Attachments[i].Id] = (short)i;
                var oldLk = f["attachment_lookup"].AsI16();
                bool sameSet = na == m.Attachments.Length && Enumerable.Range(0, na).All(i => plan.Attachments[i].OldIndex == i && plan.Attachments[i].Id == m.Attachments[i].Id);
                if (!sameSet && !lk.SequenceEqual(oldLk)) SetI16Array(f["attachment_lookup"], lk);
                anyChange = true;
            }
        }

        // ------------------------------------------------ events
        var ev = f["events"];
        var evRec = M2Schema.Event;
        var evTrack = evRec.Track("enabled");
        if (plan.Events != null)
        {
            int ne = plan.Events.Count;
            var eb = new byte[ne * evRec.Size];
            var esub = new M2Data[ne][];
            var ed = new M2Data(M2Elem.Of(evRec), ne, eb, esub, -1);
            for (int i = 0; i < ne; i++)
            {
                var pe = plan.Events[i];
                RawTrack raw;
                if (pe.OldIndex >= 0 && pe.OldIndex < ev.Count)
                {
                    Buffer.BlockCopy(ev.Bytes, pe.OldIndex * evRec.Size, eb, i * evRec.Size, evRec.Size);
                    esub[i] = (M2Data[])ev.Sub[pe.OldIndex].Clone();
                    raw = RawTrack.Read(new M2TrackView(ev, pe.OldIndex, evTrack));
                }
                else
                {
                    esub[i] = evRec.Arrays.Select(a => M2Data.Empty(a.Elem)).ToArray();
                    BinaryPrimitives.WriteInt16LittleEndian(eb.AsSpan(i * evRec.Size + evTrack.Offset + 2), -1);
                    raw = new RawTrack { Interpolation = 0, GlobalSequence = -1 };
                    for (int s = 0; s < oldSeqs; s++) raw.Ranges.Add((0, 0));
                }
                var code = AnimationEvents.CodeBytes(pe.Code);
                Buffer.BlockCopy(code, 0, eb, i * evRec.Size, 4);
                ed.SetU32(i, "data", pe.Data);
                ed.SetU16(i, "bone", (ushort)pe.Bone);
                ed.SetVec3(i, "position", pe.Position);
                var sources = NonBoneSources(raw, true);
                foreach (var (s, t) in pe.Triggers) sources[s] = SeqSource.New(t);
                if (pe.OldIndex < 0) for (int s = 0; s < ns; s++) if (sources[s].IsKeep) sources[s] = SeqSource.New(new SeqKeys(false));
                var enc = TrackEncoder.Encode(raw, sources, starts, durations, null);
                if (!ReferenceEquals(enc, raw) || pe.OldIndex < 0) enc.Write(ed, i, evTrack);
            }
            bool same = ne == ev.Count && eb.AsSpan().SequenceEqual(ev.Bytes) && Enumerable.Range(0, ne).All(i => plan.Events[i].OldIndex == i && esub[i].SequenceEqual(ev.Sub[i]));
            if (!same)
            {
                ev.Set(ne, eb, esub);
                anyChange = true;
            }
        }
        else if (seqLayoutChanged)
            for (int i = 0; i < ev.Count; i++) RewriteTrack(ev, i, evTrack, raw => NonBoneSources(raw, true), starts, durations, null);

        // ------------------------------------------------ every other track follows the sequences
        if (seqLayoutChanged)
        {
            foreach (var name in TrackArrays.Append("attachments"))
            {
                var arr = f[name];
                for (int i = 0; i < arr.Count; i++)
                    foreach (var tf in arr.Rec.Tracks)
                        RewriteTrack(arr, i, tf, raw => NonBoneSources(raw, false), starts, durations, null);
            }
        }
        res.Changed = anyChange;
        return res;
    }

    private static void RewriteTrack(M2Data arr, int i, M2TrackField tf, Func<RawTrack, List<SeqSource>> sources, uint[] starts, uint[] durations, byte[]? fill)
    {
        var raw = RawTrack.Read(new M2TrackView(arr, i, tf));
        if (raw.GlobalSequence >= 0) return;
        var enc = TrackEncoder.Encode(raw, sources(raw), starts, durations, fill ?? (raw.HasValues && raw.KeyCount > 0 ? raw.Values![0] : null));
        if (!ReferenceEquals(enc, raw)) enc.Write(arr, i, tf);
    }

    /// <summary>The sequence a new one copies its non-bone animation from by default: the first Stand, else the first.</summary>
    public static int DefaultTemplate(M2Model m)
    {
        for (int i = 0; i < m.Sequences.Length; i++)
            if (m.Sequences[i].AnimId == 0) return i;
        return m.Sequences.Length > 0 ? 0 : -1;
    }

    private static uint RoundUp(ulong t) => (uint)Math.Min(uint.MaxValue, (t + 99) / 100 * 100);

    private static SeqKeys StepAsLinear(SeqKeys w)
    {
        if (w.Count < 2) return w;
        var r = new SeqKeys(true);
        for (int i = 0; i < w.Count; i++)
        {
            if (i > 0 && w.Times[i] > w.Times[i - 1] + 1 && !w.Values![i].AsSpan().SequenceEqual(w.Values[i - 1]))
                r.Add(w.Times[i] - 1, w.Values[i - 1]);
            r.Add(w.Times[i], w.Values![i]);
        }
        return r;
    }

    private static void RemapField(M2Data arr, string field, Func<int, int> remap)
    {
        if (arr.Count == 0) return;
        var ft = arr.Rec.Field(field);
        for (int i = 0; i < arr.Count; i++)
        {
            var s = arr.Bytes.AsSpan(i * arr.Elem.Size + ft.Offset);
            switch (ft.Type)
            {
                case "u2": BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)remap(BinaryPrimitives.ReadUInt16LittleEndian(s))); break;
                case "i2":
                {
                    short b = BinaryPrimitives.ReadInt16LittleEndian(s);
                    if (b >= 0) BinaryPrimitives.WriteInt16LittleEndian(s, (short)remap(b));
                    break;
                }
                default: BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)remap((int)BinaryPrimitives.ReadUInt32LittleEndian(s))); break;
            }
        }
    }

    private static void SetU32Array(M2Data d, IReadOnlyList<uint> v)
    {
        var b = new byte[v.Count * 4];
        for (int i = 0; i < v.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        d.Set(v.Count, b);
    }

    private static void SetI16Array(M2Data d, IReadOnlyList<short> v)
    {
        var b = new byte[v.Count * 2];
        for (int i = 0; i < v.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), v[i]);
        d.Set(v.Count, b);
    }

    /// <summary>Recompute the bounds of some sequences (moved or new ones) from the animated vertices and widen the model's
    /// bounds to hold them. The other sequences keep their stored bounds.</summary>
    public static void RecomputeBounds(M2File f, IEnumerable<int> sequences)
    {
        var list = sequences.Distinct().ToList();
        if (list.Count == 0) return;
        var m = M2Model.Load(f.Serialize());
        if (m.Vertices.Length == 0) return;
        var seqs = f["sequences"];
        var mn = f.Header.Vec3(0, "bounds_min");
        var mx = f.Header.Vec3(0, "bounds_max");
        bool widened = false;
        var all = new List<Vector3>();
        foreach (int si in list)
        {
            if (si >= m.Sequences.Length) continue;
            var (smn, smx, sr) = M2Bounds.Sequence(m, si, all);
            seqs.SetVec3(si, "bounds_min", smn);
            seqs.SetVec3(si, "bounds_max", smx);
            seqs.SetF32(si, "bounds_radius", sr);
            var nmn = Vector3.Min(mn, smn);
            var nmx = Vector3.Max(mx, smx);
            if (nmn != mn || nmx != mx) widened = true;
            mn = nmn;
            mx = nmx;
        }
        if (!widened) return;
        var c = (mn + mx) / 2;
        float r = f.Header.F32(0, "bounds_radius");
        foreach (var p in all) r = Math.Max(r, Vector3.Distance(p, c));
        f.Header.SetVec3(0, "bounds_min", mn);
        f.Header.SetVec3(0, "bounds_max", mx);
        f.Header.SetF32(0, "bounds_radius", r);
    }
}
