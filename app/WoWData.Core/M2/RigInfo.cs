using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>One animation (sequence) of a model in plain terms.</summary>
public sealed class RigSequence
{
    public int Index { get; init; }
    public int AnimId { get; init; }
    public int SubId { get; init; }
    /// <summary>0 for the first sequence of its animation id, 1 for the second... (the variation chain).</summary>
    public int Variation { get; init; }
    public int Variations { get; init; }
    /// <summary>"Stand", "Stand [2]", "AttackUnarmed"...</summary>
    public string Name { get; init; } = "";
    public string AnimName { get; init; } = "";
    public uint Start { get; init; }
    public uint End { get; init; }
    public uint Duration => End >= Start ? End - Start : 0;
    public float MoveSpeed { get; init; }
    public uint Flags { get; init; }
    /// <summary>Flag 0x1 clear: the animation loops (its events keep firing); set: it plays once and stops at its end.</summary>
    public bool Loops => (Flags & 1) == 0;
    public short Frequency { get; init; }
    /// <summary>The chance of this variation when the animation plays, in percent (frequency / 32767).</summary>
    public double ChancePercent => Frequency / 32767.0 * 100.0;
    public uint ReplayMin { get; init; }
    public uint ReplayMax { get; init; }
    public uint BlendTime { get; init; }
    public short NextVariant { get; init; }
    public int NextAlias { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public float BoundsRadius { get; init; }
}

public sealed class RigBone
{
    public int Index { get; init; }
    public int KeyBoneId { get; init; }
    public string KeyBoneName => KeyBoneId >= 0 && KeyBoneId < M2Schema.KeyBones.Length ? M2Schema.KeyBones[KeyBoneId] : "";
    public uint Flags { get; init; }
    public int Parent { get; init; }
    public int SubmeshId { get; init; }
    public Vector3 Pivot { get; init; }
    /// <summary>The bone's name in the export and in Blender: bone_012, bone_015_Root...</summary>
    public string Name { get; init; } = "";
    public bool Animated { get; init; }
    /// <summary>Channels ("translation", "rotation", "scale") that run on a global sequence, with its index.</summary>
    public Dictionary<string, int> GlobalChannels { get; init; } = new();
    /// <summary>How many vertices are weighted to the bone.</summary>
    public int Vertices { get; set; }
}

public sealed class RigAttachment
{
    public int Index { get; init; }
    public int Id { get; init; }
    public string Name => AttachmentName(Id);
    public int Bone { get; init; }
    public Vector3 Position { get; init; }

    public static string AttachmentName(int id) => id >= 0 && id < M2Schema.AttachmentNames.Length ? M2Schema.AttachmentNames[id] : $"Attachment{id}";
}

public sealed class RigEvent
{
    public int Index { get; init; }
    public string Code { get; init; } = "";
    public string Meaning => AnimationEvents.Describe(Code);
    public uint Data { get; init; }
    public int Bone { get; init; }
    public Vector3 Position { get; init; }
    /// <summary>(sequence, time in ms from the sequence start) of every trigger.</summary>
    public List<(int Sequence, uint Time)> Triggers { get; } = new();
}

/// <summary>
/// 0.10.0: the skeleton, animations, attachment points and events of a model, decoded for the export, the command line
/// (wds models anims) and the import's comparison.
/// </summary>
public sealed class RigInfo
{
    public List<RigBone> Bones { get; } = new();
    public List<RigSequence> Sequences { get; } = new();
    public List<uint> GlobalSequences { get; } = new();
    public List<RigAttachment> Attachments { get; } = new();
    public List<RigEvent> Events { get; } = new();
    public int Particles { get; init; }
    public int Ribbons { get; init; }

    public static string BoneName(int index, int keyBone) =>
        $"bone_{index:000}" + (keyBone >= 0 && keyBone < M2Schema.KeyBones.Length ? "_" + M2Schema.KeyBones[keyBone] : "");

    /// <summary>"Stand", "Stand [2]" (the second variation)...</summary>
    public static string SequenceName(string animName, int variation) => variation == 0 ? animName : $"{animName} [{variation + 1}]";

    public static RigInfo Read(M2Model m, AnimationCatalog? catalog = null)
    {
        catalog ??= AnimationCatalog.Empty;
        var f = m.File;
        var info = new RigInfo { Particles = m.ParticleCount, Ribbons = m.RibbonCount };
        info.GlobalSequences.AddRange(m.GlobalSequences);
        var bones = f["bones"];
        var used = new int[m.Bones.Length];
        foreach (var v in m.Vertices)
        {
            if (v.W0 > 0 && v.B0 < used.Length) used[v.B0]++;
            if (v.W1 > 0 && v.B1 < used.Length && v.B1 != v.B0) used[v.B1]++;
            if (v.W2 > 0 && v.B2 < used.Length && v.B2 != v.B0 && v.B2 != v.B1) used[v.B2]++;
            if (v.W3 > 0 && v.B3 < used.Length && v.B3 != v.B0 && v.B3 != v.B1 && v.B3 != v.B2) used[v.B3]++;
        }
        for (int i = 0; i < m.Bones.Length; i++)
        {
            var b = m.Bones[i];
            var gch = new Dictionary<string, int>();
            foreach (var t in bones.Tracks(i))
                if (t.GlobalSequence >= 0 && t.KeyCount > 0) gch[t.Name] = t.GlobalSequence;
            info.Bones.Add(new RigBone
            {
                Index = i, KeyBoneId = b.KeyBoneId, Flags = b.Flags, Parent = b.Parent, SubmeshId = b.SubmeshId, Pivot = b.Pivot,
                Name = BoneName(i, b.KeyBoneId), Animated = b.IsAnimated, GlobalChannels = gch, Vertices = used[i],
            });
        }
        var seqs = f["sequences"];
        var rank = new Dictionary<int, int>();
        var counts = m.Sequences.GroupBy(s => s.AnimId).ToDictionary(g => g.Key, g => g.Count());
        for (int i = 0; i < m.Sequences.Length; i++)
        {
            var s = m.Sequences[i];
            int v = rank.GetValueOrDefault(s.AnimId);
            rank[s.AnimId] = v + 1;
            uint rmin = seqs.U32(i, "replay", 0), rmax = seqs.U32(i, "replay", 1);
            string an = catalog.Name(s.AnimId);
            info.Sequences.Add(new RigSequence
            {
                Index = i, AnimId = s.AnimId, SubId = s.SubId, Variation = v, Variations = counts[s.AnimId], Name = SequenceName(an, v), AnimName = an,
                Start = s.Start, End = s.End, MoveSpeed = s.MoveSpeed, Flags = s.Flags, Frequency = s.Frequency, ReplayMin = rmin, ReplayMax = rmax,
                BlendTime = s.BlendTime, NextVariant = s.NextVariant, NextAlias = s.NextAlias, BoundsMin = s.BoundsMin, BoundsMax = s.BoundsMax, BoundsRadius = s.BoundsRadius,
            });
        }
        foreach (var a in m.Attachments.Select((a, i) => (a, i)))
            info.Attachments.Add(new RigAttachment { Index = a.i, Id = a.a.Id, Bone = a.a.Bone, Position = a.a.Position });
        var ev = f["events"];
        for (int i = 0; i < ev.Count; i++)
        {
            var e = new RigEvent
            {
                Index = i, Code = AnimationEvents.Code(ev.Bytes.AsSpan(i * ev.Elem.Size, 4)), Data = ev.U32(i, "data"), Bone = ev.U16(i, "bone"), Position = ev.Vec3(i, "position"),
            };
            var raw = RawTrack.Read(ev.Track(i, "enabled"));
            for (int s = 0; s < m.Sequences.Length; s++)
            {
                var w = raw.Window(s, m.Sequences[s].Start, m.Sequences[s].End);
                foreach (var t in w.Times) e.Triggers.Add((s, t));
            }
            info.Events.Add(e);
        }
        return info;
    }
}
