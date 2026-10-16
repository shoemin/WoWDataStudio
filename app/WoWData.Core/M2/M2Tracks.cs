using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>
/// 0.10.0: one animation track as raw keys (the encoder's view of an M2Track): interpolation, global sequence, the
/// per-sequence key ranges, the timestamps (ms on the model's ONE global timeline) and the values as raw element bytes
/// (null for tracks without values, the events' trigger times). Read from and written back to an <see cref="M2Data"/>
/// record; a track that is not rewritten keeps its blocks byte for byte.
///
/// The key semantics are the 1.12 client's (M2Model_FindTrackKeys @00713d50, M2Model_EvalQuatTrack @00713ea0,
/// M2Model_EvalFloatTrack @0071af20, M2Model_EvalByteTrack @0071aed0, event triggers in M2Model_Update @00719380):
///  * value tracks: ranges[s] = {first, last} INCLUSIVE key indices for sequence s; no ranges = every key, at the absolute
///    time; last &lt;= first = key[first] for the whole sequence; the key pair is (k0, k0 + 1) where k0 is the last key of
///    the range at or before the time and k0 + 1 may lie past the range; the fraction is NOT clamped;
///  * interpolation 0 = the value of k0, anything else = linear (component-wise for quaternions, not normalised); byte
///    tracks are never interpolated;
///  * a track with a global sequence runs on that sequence's own clock;
///  * event tracks (no values): ranges[s] = {first, end} HALF-OPEN; the triggers are the keys first .. end - 1.
/// </summary>
public sealed class RawTrack
{
    public ushort Interpolation;
    public short GlobalSequence = -1;
    public List<(uint First, uint Last)> Ranges = new();
    public List<uint> Times = new();
    /// <summary>One element per key (null for tracks without values).</summary>
    public List<byte[]>? Values;
    /// <summary>The value element type ("3f4", "4f4", "i2", "u1"...), null without values.</summary>
    public string? ValueType;

    public bool HasValues => Values != null;
    public int KeyCount => Times.Count;
    public int ElemSize => ValueType == null ? 0 : M2Elem.PrimSize(ValueType);

    public RawTrack Clone() => new()
    {
        Interpolation = Interpolation, GlobalSequence = GlobalSequence, Ranges = new(Ranges), Times = new(Times),
        Values = Values?.Select(v => (byte[])v.Clone()).ToList(), ValueType = ValueType,
    };

    /// <summary>An empty track of the same kind (no ranges, no keys).</summary>
    public RawTrack EmptyLike() => new() { Interpolation = Interpolation, GlobalSequence = GlobalSequence, ValueType = ValueType, Values = ValueType == null ? null : new() };

    public static RawTrack Read(M2TrackView t)
    {
        var r = new RawTrack { Interpolation = t.Interpolation, GlobalSequence = t.GlobalSequence };
        foreach (var p in t.RangePairs()) r.Ranges.Add(p);
        r.Times.AddRange(t.Timestamps.AsU32());
        if (t.Field.Value is { } ve)
        {
            r.ValueType = ve.Type;
            r.Values = new List<byte[]>();
            var vals = t.Values!;
            int size = ve.Size;
            for (int i = 0; i < vals.Count; i++) r.Values.Add(vals.Bytes.AsSpan(i * size, size).ToArray());
        }
        return r;
    }

    /// <summary>Write the track into record <paramref name="index"/> of <paramref name="owner"/> (header fields and the
    /// three blocks).</summary>
    public void Write(M2Data owner, int index, M2TrackField f)
    {
        var rec = owner.Rec;
        int at = index * rec.Size + f.Offset;
        BinaryPrimitives.WriteUInt16LittleEndian(owner.Bytes.AsSpan(at), Interpolation);
        BinaryPrimitives.WriteInt16LittleEndian(owner.Bytes.AsSpan(at + 2), GlobalSequence);
        var rb = new byte[Ranges.Count * 8];
        for (int i = 0; i < Ranges.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rb.AsSpan(i * 8), Ranges[i].First);
            BinaryPrimitives.WriteUInt32LittleEndian(rb.AsSpan(i * 8 + 4), Ranges[i].Last);
        }
        owner.Sub[index][f.RangesIndex] = Block(rec.Arrays[f.RangesIndex].Elem, Ranges.Count, rb);
        var tb = new byte[Times.Count * 4];
        for (int i = 0; i < Times.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(tb.AsSpan(i * 4), Times[i]);
        owner.Sub[index][f.TimestampsIndex] = Block(rec.Arrays[f.TimestampsIndex].Elem, Times.Count, tb);
        if (f.ValuesIndex >= 0)
        {
            var elem = rec.Arrays[f.ValuesIndex].Elem;
            var vals = Values ?? new List<byte[]>();
            var vb = new byte[vals.Count * elem.Size];
            for (int i = 0; i < vals.Count; i++) Buffer.BlockCopy(vals[i], 0, vb, i * elem.Size, elem.Size);
            owner.Sub[index][f.ValuesIndex] = Block(elem, vals.Count, vb);
        }
    }

    private static M2Data Block(M2Elem elem, int count, byte[] bytes) => count == 0 ? M2Data.Empty(elem) : new M2Data(elem, count, bytes, null, -1);

    /// <summary>True when both tracks hold exactly the same data.</summary>
    public bool SameAs(RawTrack o)
    {
        if (Interpolation != o.Interpolation || GlobalSequence != o.GlobalSequence || ValueType != o.ValueType) return false;
        if (!Ranges.SequenceEqual(o.Ranges) || !Times.SequenceEqual(o.Times)) return false;
        if ((Values == null) != (o.Values == null)) return false;
        if (Values == null) return true;
        if (Values.Count != o.Values!.Count) return false;
        for (int i = 0; i < Values.Count; i++)
            if (!Values[i].AsSpan().SequenceEqual(o.Values[i])) return false;
        return true;
    }

    // ================================================================= the client's key search

    /// <summary>The key range the client uses for sequence <paramref name="seq"/>: (lo, hi) inclusive (half-open for
    /// events), or every key when the track has no ranges. A range the file does not have reads as {0, 0}.</summary>
    public (uint Lo, uint Hi) RangeOf(int seq)
    {
        if (Ranges.Count == 0) return (0, (uint)Math.Max(0, Times.Count - 1));
        if (seq < 0 || seq >= Ranges.Count) return (0, 0);
        return Ranges[seq];
    }

    /// <summary>M2Model_FindTrackKeys: (k0, k1, fraction) at absolute time <paramref name="time"/> (already the global
    /// sequence's clock for such tracks). False when the track has no usable key.</summary>
    public bool FindKeys(int seq, uint time, out int k0, out int k1, out float f)
    {
        k0 = k1 = 0;
        f = 0;
        int n = Times.Count;
        if (n == 0 || (HasValues && Values!.Count < n)) return false;
        var (lo, hi) = RangeOf(seq);
        if (lo >= n) return false;
        if (hi <= lo)
        {
            k0 = k1 = (int)lo;
            return true;
        }
        if (hi >= n) hi = (uint)n - 1;
        // the last key in [lo, hi] at or before the time; lo when the time is before every key of the range
        uint i = lo;
        if (time >= Times[(int)lo])
        {
            uint a = lo, b = hi;
            while (a < b)
            {
                uint m = (a + b + 1) >> 1;
                if (Times[(int)m] <= time) a = m;
                else b = m - 1;
            }
            i = a;
        }
        if (i + 1 >= n)
        {
            k0 = k1 = (int)i;
            return true;
        }
        k0 = (int)i;
        k1 = (int)i + 1;
        int den = (int)(Times[k1] - Times[k0]);
        f = den == 0 ? 0f : (float)(int)(time - Times[k0]) / den;
        return true;
    }

    /// <summary>The value the client computes at absolute time <paramref name="time"/> while sequence
    /// <paramref name="seq"/> plays (raw element bytes), or null when the track has no key for it.</summary>
    public byte[]? Evaluate(int seq, uint time)
    {
        if (!HasValues || !FindKeys(seq, time, out int k0, out int k1, out float f)) return null;
        if (k0 == k1 || Interpolation == 0 || f == 0f) return (byte[])Values![k0].Clone();
        return Lerp(ValueType!, Values![k0], Values[k1], f, Interpolation);
    }

    /// <summary>Interpolate two raw values like the client: floats component-wise (quaternions not normalised), 16-bit
    /// fixed-point values through float and rounded; bytes and uint16 values are not interpolated. Hermite / bezier
    /// values (interpolation 2 / 3, camera splines) are not interpolated here (the first key is returned).</summary>
    public static byte[] Lerp(string type, byte[] a, byte[] b, float f, int interpolation = 1)
    {
        var (n, size, kind) = M2Elem.Shape(type);
        if (interpolation >= 2 || kind == 'u' || type == "vertex") return (byte[])a.Clone();
        var r = new byte[a.Length];
        for (int c = 0; c < n; c++)
        {
            int o = c * size;
            if (kind == 'f' && size == 4)
            {
                float x = BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(o)), y = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
                BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(o), (y - x) * f + x);
            }
            else if (kind == 'i' && size == 2)
            {
                float x = BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(o)), y = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o));
                BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(o), (short)Math.Clamp(Math.Round((y - x) * f + x), short.MinValue, short.MaxValue));
            }
            else Buffer.BlockCopy(a, o, r, o, size);
        }
        return r;
    }

    // ================================================================= windows (per-sequence keys)

    /// <summary>
    /// The keys of one sequence as the client plays them, relative to the sequence start: the value at the start, every
    /// key of the sequence's range strictly inside the window, and the value at the end (equal to the client's
    /// piecewise-linear / stepped function on the whole window). A constant range is one key at 0. Event tracks: the
    /// trigger times of the range inside the window. Global-sequence tracks have no per-sequence windows.
    /// </summary>
    public SeqKeys Window(int seq, uint start, uint end)
    {
        var w = new SeqKeys(HasValues);
        int n = Times.Count;
        if (n == 0) return w;
        var (lo, hi) = RangeOf(seq);
        if (!HasValues)
        {
            // events: half-open range
            uint e = Ranges.Count == 0 ? (uint)n : Math.Min(hi, (uint)n);
            for (uint k = lo; k < e; k++)
            {
                uint t = Times[(int)k];
                if (t >= start && t <= end) w.Add(t - start, null);
            }
            w.SortAndDedupe();
            return w;
        }
        if (lo >= n || Values!.Count < n) return w;
        if (hi <= lo || Interpolation >= 2)
        {
            w.Add(0, (byte[])Values[(int)Math.Min(lo, (uint)n - 1)].Clone());
            return w;
        }
        uint h = Math.Min(hi, (uint)n - 1);
        w.Add(0, Evaluate(seq, start)!);
        for (uint k = lo; k <= h; k++)
        {
            uint t = Times[(int)k];
            if (t <= start || t >= end) continue;
            // two keys at one time: the client takes the later one
            if (k < h && Times[(int)k + 1] == t) continue;
            w.Add(t - start, (byte[])Values[(int)k].Clone());
        }
        if (end > start) w.Add(end - start, Evaluate(seq, end)!);
        return w;
    }

    /// <summary>The keys of a global-sequence (or range-less) track on its own clock, 0 .. <paramref name="period"/>.</summary>
    public SeqKeys GlobalWindow(uint period)
    {
        var w = new SeqKeys(HasValues);
        if (Times.Count == 0) return w;
        if (!HasValues)
        {
            foreach (var t in Times) if (t <= period) w.Add(t, null);
            return w;
        }
        uint lastT = uint.MaxValue;
        for (int k = 0; k < Times.Count && k < Values!.Count; k++)
        {
            if (Times[k] > period) break;
            if (Times[k] == lastT) w.Values![^1] = (byte[])Values[k].Clone();
            else w.Add(Times[k], (byte[])Values[k].Clone());
            lastT = Times[k];
        }
        if (w.Count == 0) w.Add(0, (byte[])Values![0].Clone());
        return w;
    }

    /// <summary>The value of a global-sequence track at time <paramref name="t"/> of its own clock.</summary>
    public byte[]? GlobalWindowEval(uint t) => !HasValues || KeyCount == 0 ? null : Evaluate(0, t);

    /// <summary>The largest timestamp of the track (0 without keys).</summary>
    public uint MaxTime => Times.Count == 0 ? 0 : Times.Max();
}

/// <summary>Keys of one sequence (or one global sequence): times relative to its start and raw values.</summary>
public sealed class SeqKeys
{
    public List<uint> Times { get; } = new();
    public List<byte[]>? Values { get; }

    public SeqKeys(bool hasValues) => Values = hasValues ? new List<byte[]>() : null;

    public int Count => Times.Count;

    public void Add(uint t, byte[]? v)
    {
        Times.Add(t);
        if (Values != null) Values.Add(v ?? throw new ArgumentNullException(nameof(v)));
    }

    public static SeqKeys Constant(byte[] value)
    {
        var k = new SeqKeys(true);
        k.Add(0, value);
        return k;
    }

    public void SortAndDedupe()
    {
        if (Values != null) return;
        var s = Times.Distinct().OrderBy(x => x).ToList();
        Times.Clear();
        Times.AddRange(s);
    }

    /// <summary>The same keys with times scaled to another duration (a retimed sequence).</summary>
    public SeqKeys Stretched(uint oldDuration, uint newDuration)
    {
        if (oldDuration == newDuration || oldDuration == 0) return this;
        var r = new SeqKeys(Values != null);
        uint last = uint.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            uint t = (uint)Math.Round((double)Times[i] * newDuration / oldDuration);
            if (t == last && Values != null)
            {
                r.Values![^1] = Values[i];
                continue;
            }
            if (t == last) continue;
            r.Add(t, Values?[i]);
            last = t;
        }
        return r;
    }

    /// <summary>The keys clipped to 0 .. <paramref name="duration"/> (the value at the end added when needed).</summary>
    public SeqKeys ClippedTo(uint duration, string? type, int interpolation)
    {
        if (Count == 0 || Times[^1] <= duration) return this;
        var r = new SeqKeys(Values != null);
        for (int i = 0; i < Count && Times[i] <= duration; i++) r.Add(Times[i], Values?[i]);
        if (Values != null)
        {
            // value at the end: interpolate between the last kept key and the next one
            int j = r.Count;
            if (j == 0) r.Add(0, Values[0]);
            else if (r.Times[^1] < duration)
            {
                var a = Values[j - 1];
                var b = Values[j];
                float f = (float)(duration - Times[j - 1]) / (Times[j] - Times[j - 1]);
                r.Add(duration, interpolation == 0 ? a : RawTrack.Lerp(type!, a, b, f, interpolation));
            }
        }
        return r;
    }

    public bool SameAs(SeqKeys o)
    {
        if (!Times.SequenceEqual(o.Times)) return false;
        if ((Values == null) != (o.Values == null)) return false;
        if (Values == null) return true;
        for (int i = 0; i < Values.Count; i++)
            if (!Values[i].AsSpan().SequenceEqual(o.Values![i])) return false;
        return true;
    }
}

/// <summary>Where the keys of one sequence of a rewritten track come from.</summary>
public readonly record struct SeqSource(int KeepRange, SeqKeys? Keys)
{
    /// <summary>Keep the original track's range of sequence <paramref name="oldIndex"/> (its keys stay where they are).</summary>
    public static SeqSource Keep(int oldIndex) => new(oldIndex, null);
    /// <summary>New keys for the sequence (times relative to its start), appended to the track.</summary>
    public static SeqSource New(SeqKeys keys) => new(-1, keys);
    public bool IsKeep => Keys == null;
}

/// <summary>
/// Writes the keys of new, edited or moved sequences into a track WITHOUT touching the keys of the others ("append
/// mode"): the original timestamps and values stay as they are (an unchanged sequence keeps its range and plays exactly
/// as before), the new keys are appended after them on the timeline, and the ranges are rebuilt in the new sequence
/// order. When every sequence keeps its range in the original order, the track is returned unchanged (byte-identical).
/// A guard key (a copy of the last key, 1 ms later) is put between the original and the appended keys, so a range that
/// ends on the last original key still holds its last value (the client interpolates towards the key after a range).
/// </summary>
public static class TrackEncoder
{
    /// <param name="track">The original track.</param>
    /// <param name="sources">One entry per sequence of the new model.</param>
    /// <param name="starts">The new start of each sequence on the timeline.</param>
    /// <param name="durations">The new duration of each sequence.</param>
    /// <param name="fill">The value of a sequence without keys when the track needs one (a range-less track that gets
    /// ranges, an empty track that gets keys): the rest value (bones) or a template value.</param>
    public static RawTrack Encode(RawTrack track, IReadOnlyList<SeqSource> sources, IReadOnlyList<uint> starts, IReadOnlyList<uint> durations, byte[]? fill)
    {
        if (track.GlobalSequence >= 0) return track;          // runs on its own clock: not per sequence
        int n = track.KeyCount;
        if (track.Ranges.Count == 0 && n <= 1)
        {
            // a range-less constant (or empty) track gives every sequence the same value whatever its place on the
            // timeline: new keys that only repeat that value change nothing
            bool Same(SeqKeys k) => track.HasValues
                ? n == 1 ? k.Values!.All(v => v.AsSpan().SequenceEqual(track.Values![0])) : k.Count == 0
                : n == 0 && k.Count == 0;
            if (sources.All(s => s.IsKeep || Same(s.Keys!))) return track;
        }
        bool allKeep = sources.All(s => s.IsKeep);
        if (allKeep)
        {
            if (track.Ranges.Count == 0) return track;         // every sequence uses every key: nothing to do
            bool identity = true;
            for (int i = 0; i < sources.Count && identity; i++) identity = sources[i].KeepRange == i;
            if (identity) return track;                         // the same sequences in the same order: unchanged
        }
        bool events = !track.HasValues;
        // a track that will hold no keys at all stays empty
        if (n == 0 && sources.All(s => s.IsKeep || s.Keys!.Count == 0)) return EmptyWithRanges(track, sources.Count);
        var r = track.Clone();
        // range-less track (every sequence uses all its keys at their absolute times): kept sequences get a range over all
        // its keys, which plays the same
        (uint, uint) Kept(int old)
        {
            if (track.Ranges.Count == 0) return n == 0 ? (0u, 0u) : events ? (0u, (uint)n) : (0u, (uint)(n - 1));
            return old >= 0 && old < track.Ranges.Count ? track.Ranges[old] : (0u, 0u);
        }
        // the value of a kept sequence that had no key (an empty track that gets keys elsewhere)
        int fillIndex = -1;
        if (!events && n == 0 && sources.Any(s => s.IsKeep))
        {
            var v = fill ?? throw new ArgumentException("a fill value is needed to give keys to an empty track");
            r.Times.Add(0);
            r.Values!.Add((byte[])v.Clone());
            fillIndex = 0;
        }
        else if (!events && n > 0)
        {
            // the guard key: ranges that end on the last original key keep holding its value
            uint lastT = track.Times[n - 1];
            bool needGuard = track.Ranges.Count == 0 || track.Ranges.Any(p => p.Last >= n - 1 && p.Last > p.First);
            if (needGuard && lastT < uint.MaxValue)
            {
                r.Times.Add(lastT + 1);
                r.Values!.Add((byte[])track.Values![n - 1].Clone());
            }
        }
        r.Ranges = new List<(uint, uint)>(sources.Count);
        for (int s = 0; s < sources.Count; s++)
        {
            var src = sources[s];
            if (src.IsKeep)
            {
                r.Ranges.Add(fillIndex >= 0 ? ((uint)fillIndex, (uint)fillIndex) : Kept(src.KeepRange));
                continue;
            }
            var keys = src.Keys!;
            uint start = starts[s];
            uint first = (uint)r.Times.Count;
            if (events)
            {
                foreach (var t in keys.Times) r.Times.Add(start + Math.Min(t, durations[s]));
                r.Ranges.Add((first, (uint)r.Times.Count));
                continue;
            }
            if (keys.Count == 0)
            {
                var v = fill ?? (n > 0 ? track.Values![0] : throw new ArgumentException("no keys and no fill value"));
                r.Times.Add(start);
                r.Values!.Add((byte[])v.Clone());
            }
            else
            {
                for (int k = 0; k < keys.Count; k++)
                {
                    r.Times.Add(start + keys.Times[k]);
                    r.Values!.Add((byte[])keys.Values![k].Clone());
                }
            }
            r.Ranges.Add((first, (uint)r.Times.Count - 1));
        }
        return r;
    }

    private static RawTrack EmptyWithRanges(RawTrack track, int nseq)
    {
        var r = track.EmptyLike();
        if (track.Ranges.Count > 0)
            for (int i = 0; i < nseq; i++) r.Ranges.Add((0, 0));
        return r;
    }

    /// <summary>A track written from scratch: every sequence from its keys (the full re-encode used by the semantic
    /// round-trip check and for new bones).</summary>
    public static RawTrack Fresh(RawTrack like, IReadOnlyList<SeqKeys> perSequence, IReadOnlyList<uint> starts, IReadOnlyList<uint> durations, byte[]? fill)
    {
        var empty = like.EmptyLike();
        empty.Interpolation = like.Interpolation;
        if (perSequence.All(k => k.Count == 0))
        {
            for (int i = 0; i < perSequence.Count && like.Ranges.Count > 0; i++) empty.Ranges.Add((0, 0));
            return empty;
        }
        return Encode(empty, perSequence.Select(SeqSource.New).ToList(), starts, durations, fill);
    }

    /// <summary>A global-sequence track written from its keys on the global sequence's clock.</summary>
    public static RawTrack Global(RawTrack like, SeqKeys keys, short globalSequence)
    {
        var r = like.EmptyLike();
        r.GlobalSequence = globalSequence;
        r.Interpolation = like.Interpolation == 0 && keys.Count > 1 ? (ushort)1 : like.Interpolation;
        for (int k = 0; k < keys.Count; k++)
        {
            r.Times.Add(keys.Times[k]);
            r.Values?.Add((byte[])keys.Values![k].Clone());
        }
        return r;
    }
}

/// <summary>Raw M2 values from and to numbers (vectors, quaternions, fixed-point).</summary>
public static class TrackValues
{
    public static byte[] Vec3(Vector3 v)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), v.Z);
        return b;
    }

    public static byte[] Quat(Quaternion q)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(b, q.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), q.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), q.Z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), q.W);
        return b;
    }

    public static Vector3 ToVec3(byte[] b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(8)));

    public static Quaternion ToQuat(byte[] b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4)),
        BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(12)));

    public static readonly byte[] ZeroVec3 = Vec3(Vector3.Zero);
    public static readonly byte[] OneVec3 = Vec3(Vector3.One);
    public static readonly byte[] IdentityQuat = Quat(Quaternion.Identity);

    /// <summary>The rest value of a bone channel (translation 0, rotation identity, scale 1).</summary>
    public static byte[] BoneRest(string channel) => channel switch { "rotation" => IdentityQuat, "scale" => OneVec3, _ => ZeroVec3 };

    /// <summary>Components of a raw value as floats (for tolerances and the effects JSON).</summary>
    public static double[] Components(string type, byte[] v)
    {
        var (n, size, kind) = M2Elem.Shape(type);
        var r = new double[n];
        for (int c = 0; c < n; c++)
        {
            var s = v.AsSpan(c * size);
            // every arm as double: the switch's common type would otherwise be float, and 32-bit integers (an id of
            // 0xFFFFFFFF) would lose their low bits
            r[c] = (kind, size) switch
            {
                ('f', 4) => (double)BinaryPrimitives.ReadSingleLittleEndian(s),
                ('i', 2) => (double)BinaryPrimitives.ReadInt16LittleEndian(s),
                ('u', 2) => (double)BinaryPrimitives.ReadUInt16LittleEndian(s),
                ('u', 1) => (double)s[0],
                ('i', 1) => (double)(sbyte)s[0],
                ('u', 4) => (double)BinaryPrimitives.ReadUInt32LittleEndian(s),
                ('i', 4) => (double)BinaryPrimitives.ReadInt32LittleEndian(s),
                _ => 0.0,
            };
        }
        return r;
    }

    /// <summary>A raw value from components (rounded and clamped for integer types).</summary>
    public static byte[] FromComponents(string type, IReadOnlyList<double> c)
    {
        var (n, size, kind) = M2Elem.Shape(type);
        var b = new byte[n * size];
        for (int i = 0; i < n; i++)
        {
            double x = i < c.Count ? c[i] : 0;
            var s = b.AsSpan(i * size);
            switch (kind, size)
            {
                case ('f', 4): BinaryPrimitives.WriteSingleLittleEndian(s, (float)x); break;
                case ('i', 2): BinaryPrimitives.WriteInt16LittleEndian(s, (short)Math.Clamp(Math.Round(x), short.MinValue, short.MaxValue)); break;
                case ('u', 2): BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)); break;
                case ('u', 1): s[0] = (byte)Math.Clamp(Math.Round(x), 0, 255); break;
                case ('i', 1): s[0] = (byte)(sbyte)Math.Clamp(Math.Round(x), sbyte.MinValue, sbyte.MaxValue); break;
                case ('u', 4): BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)Math.Clamp(Math.Round(x), 0, uint.MaxValue)); break;
                case ('i', 4): BinaryPrimitives.WriteInt32LittleEndian(s, (int)Math.Clamp(Math.Round(x), int.MinValue, int.MaxValue)); break;
            }
        }
        return b;
    }

    /// <summary>The largest component difference of two raw values (quaternions: normalised, the closer of q and -q).</summary>
    public static double Distance(string type, byte[] a, byte[] b)
    {
        var x = Components(type, a);
        var y = Components(type, b);
        if (type == "4f4")
        {
            // rotations: the client lerps quaternion components without normalising (a slightly shorter quaternion halfway
            // between keys is the same rotation): compare directions
            double nx = Math.Sqrt(x.Sum(v => v * v)), ny = Math.Sqrt(y.Sum(v => v * v));
            if (nx > 1e-9) for (int i = 0; i < 4; i++) x[i] /= nx;
            if (ny > 1e-9) for (int i = 0; i < 4; i++) y[i] /= ny;
        }
        double d = 0, dn = 0;
        for (int i = 0; i < x.Length; i++)
        {
            d = Math.Max(d, Math.Abs(x[i] - y[i]));
            dn = Math.Max(dn, Math.Abs(x[i] + y[i]));
        }
        return type == "4f4" ? Math.Min(d, dn) : d;
    }
}

/// <summary>
/// Turns dense samples (a pose every frame) into the few linear keys the 1.12 format stores: a key is dropped when the
/// line between its neighbours reproduces it within the tolerance. Quaternions are first put in one hemisphere (q and -q
/// are the same rotation, but the client interpolates the components).
/// </summary>
public static class KeyReducer
{
    public static (List<uint> Times, List<double[]> Values) Reduce(IReadOnlyList<uint> times, IReadOnlyList<double[]> values, double tolerance, bool quaternion)
    {
        var vals = values.Select(v => (double[])v.Clone()).ToList();
        if (quaternion)
            for (int i = 1; i < vals.Count; i++)
            {
                double dot = 0;
                for (int c = 0; c < 4; c++) dot += vals[i][c] * vals[i - 1][c];
                if (dot < 0) for (int c = 0; c < 4; c++) vals[i][c] = -vals[i][c];
            }
        var outT = new List<uint>();
        var outV = new List<double[]>();
        int n = times.Count;
        if (n == 0) return (outT, outV);
        outT.Add(times[0]);
        outV.Add(vals[0]);
        int anchor = 0;
        int i2 = 1;
        while (i2 < n)
        {
            // extend the segment anchor..j as far as every sample in between lies on the line
            int j = i2;
            int lastGood = i2;
            for (; j < n; j++)
            {
                if (!Fits(times, vals, anchor, j, tolerance)) break;
                lastGood = j;
            }
            outT.Add(times[lastGood]);
            outV.Add(vals[lastGood]);
            anchor = lastGood;
            i2 = lastGood + 1;
        }
        // a constant run collapses to its two ends; all equal -> one key
        if (outT.Count == 2 && Max(outV[0], outV[1]) <= tolerance && times.Count > 0)
        {
            bool allSame = vals.All(v => Max(v, vals[0]) <= tolerance);
            if (allSame) return (new List<uint> { outT[0] }, new List<double[]> { outV[0] });
        }
        return (outT, outV);
    }

    private static bool Fits(IReadOnlyList<uint> t, List<double[]> v, int a, int b, double tol)
    {
        double ta = t[a], tb = t[b];
        for (int k = a + 1; k < b; k++)
        {
            double f = tb == ta ? 0 : (t[k] - ta) / (tb - ta);
            for (int c = 0; c < v[k].Length; c++)
            {
                double lin = v[a][c] + (v[b][c] - v[a][c]) * f;
                if (Math.Abs(lin - v[k][c]) > tol) return false;
            }
        }
        return true;
    }

    private static double Max(double[] a, double[] b)
    {
        double d = 0;
        for (int i = 0; i < a.Length; i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }
}

/// <summary>A bone's local animation transform in the client's form: T(pivot) T(translation) R S T(-pivot).</summary>
public static class BoneTransform
{
    /// <summary>Split a bone's local matrix (row-vector form, <see cref="M2Anim.BoneLocal"/>) into translation, rotation
    /// and scale around <paramref name="pivot"/>.</summary>
    public static (Vector3 Translation, Quaternion Rotation, Vector3 Scale) Decompose(Matrix4x4 local, Vector3 pivot)
    {
        var r0 = new Vector3(local.M11, local.M12, local.M13);
        var r1 = new Vector3(local.M21, local.M22, local.M23);
        var r2 = new Vector3(local.M31, local.M32, local.M33);
        float sx = r0.Length(), sy = r1.Length(), sz = r2.Length();
        if (Vector3.Dot(Vector3.Cross(r0, r1), r2) < 0) sx = -sx;
        var rot = new Matrix4x4(
            sx == 0 ? 1 : r0.X / sx, sx == 0 ? 0 : r0.Y / sx, sx == 0 ? 0 : r0.Z / sx, 0,
            sy == 0 ? 0 : r1.X / sy, sy == 0 ? 1 : r1.Y / sy, sy == 0 ? 0 : r1.Z / sy, 0,
            sz == 0 ? 0 : r2.X / sz, sz == 0 ? 0 : r2.Y / sz, sz == 0 ? 1 : r2.Z / sz, 0,
            0, 0, 0, 1);
        var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rot));
        var t = local.Translation + Vector3.TransformNormal(pivot, local) - pivot;
        return (t, q, new Vector3(sx, sy, sz));
    }
}
