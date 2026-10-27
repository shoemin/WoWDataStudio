using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace WoWData.Core.M2.Modern;

/// <summary>Where a sequence's keys are and whether they could be loaded.</summary>
public enum ExternalState
{
    /// <summary>The keys are in the model (or its skeleton file).</summary>
    InModel,
    /// <summary>The sequence plays another one's keys (flag 0x40).</summary>
    Alias,
    /// <summary>In an .anim file, not loaded yet (loaded when the sequence is chosen).</summary>
    NotLoaded,
    /// <summary>In an .anim file, loaded and playing.</summary>
    Loaded,
    /// <summary>External, but the model names no .anim file for it (AFID): it has no keys.</summary>
    NoFile,
    /// <summary>The .anim file cannot be read (encrypted, not downloaded, not in this client, broken).</summary>
    Unreadable,
    /// <summary>The .anim file was read but holds no keys for this model's tracks.</summary>
    NoKeys,
}

/// <summary>One sequence's state: its .anim file (0 = none), the state, why (for the unreadable ones), the keys read.</summary>
public sealed record ExternalSequenceInfo(int Sequence, uint File, ExternalState State, string Why, int Keys);

/// <summary>
/// M3 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: the external animations of a modern model, loaded when a sequence
/// is chosen. A modern model keeps the keys of some sequences in .anim files (AFID: animation id, sub id -> FileDataID);
/// the model's per-sequence key arrays of such a sequence point into that file (FORMAT KNOWLEDGE, MEASURED on the 1.60
/// copy): a raw .anim file (offsets into the whole file), or a chunked one: AFM2 (the model's own tracks), AFSB (the
/// skeleton's bones), AFSA (the skeleton's attachment points), offsets into each chunk's data.
/// <para>
/// <see cref="Ensure"/> reads the file of a sequence once and appends its keys to the model's tracks on the 1.12
/// timeline (the sequence's place was kept by the reader), so the renderer and <see cref="M2Anim"/> play it unchanged.
/// The keys loaded stay within <see cref="Budget"/> bytes per model: when a new sequence would pass it, the least
/// recently chosen ones are released again (their tracks rebuilt without them) and loaded again when chosen. The raw
/// file bytes are never kept. Thread-safe.
/// </para>
/// </summary>
public sealed class ModernAnimations
{
    /// <summary>The keys kept per model (8 MB: the largest 1.60 creature .anim, 1.46 MB, expands to about 3 MB).</summary>
    public const long DefaultBudget = 8L << 20;

    private readonly object _gate = new();
    private readonly Func<uint, byte[]?> _read;
    private readonly SeqOrigin[] _origin;
    private readonly uint[] _start, _end;
    private readonly int[] _alias;
    private readonly List<Bound> _tracks = new();
    private readonly Dictionary<int, long> _loaded = new();
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, ExternalSequenceInfo> _status = new();
    private long _bytes;

    public long Budget { get; set; } = DefaultBudget;
    /// <summary>The bytes of keys loaded now (bounded by <see cref="Budget"/>, except a single larger sequence).</summary>
    public long LoadedBytes { get { lock (_gate) return _bytes; } }
    /// <summary>How many .anim files were read so far (a file is read again after it was released).</summary>
    public int FilesRead { get; private set; }
    /// <summary>The sequences whose keys are loaded now.</summary>
    public IReadOnlyList<int> LoadedSequences { get { lock (_gate) return _lru.ToList(); } }
    /// <summary>The model's tracks that have external sequences.</summary>
    public int TrackCount => _tracks.Count;

    internal ModernAnimations(Func<uint, byte[]?> read, Converter c)
    {
        _read = read;
        _origin = c.Origins;
        _start = c.Starts;
        _end = c.Ends;
        _alias = c.Aliases;
    }

    /// <summary>True when the sequence's keys are in an .anim file.</summary>
    public bool IsExternal(int s) => s >= 0 && s < _origin.Length && _origin[s].External;

    /// <summary>The .anim file of a sequence (0 = none or not external).</summary>
    public uint FileOf(int s) => s >= 0 && s < _origin.Length && _origin[s].External ? _origin[s].AnimFile : 0;

    /// <summary>The number of external sequences.</summary>
    public int ExternalCount => _origin.Count(o => o.External);

    /// <summary>Where a sequence's keys are and whether they loaded (without loading them).</summary>
    public ExternalSequenceInfo Status(int s)
    {
        if (s < 0 || s >= _origin.Length) return new ExternalSequenceInfo(s, 0, ExternalState.NoKeys, "no such sequence", 0);
        if (_alias[s] >= 0) return new ExternalSequenceInfo(s, 0, ExternalState.Alias, "", 0);
        var o = _origin[s];
        if (!o.External) return new ExternalSequenceInfo(s, 0, ExternalState.InModel, "", 0);
        lock (_gate)
        {
            if (_status.TryGetValue(s, out var st))
                return st.State == ExternalState.Loaded && !_loaded.ContainsKey(s) ? st with { State = ExternalState.NotLoaded, Why = "released (loaded again when chosen)" } : st;
        }
        return o.AnimFile == 0
            ? new ExternalSequenceInfo(s, 0, ExternalState.NoFile, "the model names no .anim file for it", 0)
            : new ExternalSequenceInfo(s, o.AnimFile, ExternalState.NotLoaded, "", 0);
    }

    /// <summary>
    /// Makes the sequence playable: an external one's .anim file is read (once while it stays loaded) and its keys put
    /// onto the model's tracks; an alias loads its target. Never throws for a missing or broken file: the state says why.
    /// </summary>
    public ExternalSequenceInfo Ensure(int s)
    {
        if (s < 0 || s >= _origin.Length) return Status(s);
        if (_alias[s] >= 0)
        {
            var t = Ensure(_alias[s]);
            return new ExternalSequenceInfo(s, t.File, t.State == ExternalState.InModel ? ExternalState.Alias : t.State, t.Why, t.Keys);
        }
        var o = _origin[s];
        if (!o.External) return Status(s);
        lock (_gate)
        {
            if (_loaded.ContainsKey(s))
            {
                _lru.Remove(s);
                _lru.AddFirst(s);
                return _status[s];
            }
            if (_status.TryGetValue(s, out var known) && known.State != ExternalState.Loaded) return known;
            var res = Load(s, o);
            _status[s] = res;
            return res;
        }
    }

    private ExternalSequenceInfo Load(int s, SeqOrigin o)
    {
        if (o.AnimFile == 0) return new ExternalSequenceInfo(s, 0, ExternalState.NoFile, "the model names no .anim file for it", 0);
        byte[]? bytes;
        try { bytes = _read(o.AnimFile); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new ExternalSequenceInfo(s, o.AnimFile, ExternalState.Unreadable, e.Message, 0); }
        FilesRead++;
        if (bytes == null) return new ExternalSequenceInfo(s, o.AnimFile, ExternalState.Unreadable, "not in this client", 0);
        Dictionary<KeyOwner, M2ModernReader.Buf> parts;
        try { parts = Parts(bytes); }
        catch (M2Exception e) { return new ExternalSequenceInfo(s, o.AnimFile, ExternalState.Unreadable, e.Message, 0); }
        int keys = 0, unreadable = 0;
        long size = 0;
        var aliases = Enumerable.Range(0, _alias.Length).Where(a => _alias[a] == s).ToArray();
        foreach (var t in _tracks)
        {
            if (o.Source >= t.Rec.Locs.Length || t.Rec.Locs[o.Source] is not { } loc) continue;
            if (!parts.TryGetValue(loc.Owner, out var data)) continue;
            if (Converter.KeysOf(loc, o.Index, t.Rec.SrcSize, data, ref unreadable) is not { } x) continue;
            int n = x.Times.Length;
            bool pad = t.Rec.PadEnd && _start[s] + x.Times[^1] < _end[s];
            var times = new uint[n + (pad ? 1 : 0)];
            for (int j = 0; j < n; j++) times[j] = _start[s] + x.Times[j];
            var values = x.Values;
            if (pad)
            {
                times[n] = _end[s];
                values = new byte[x.Values.Length + t.Rec.SrcSize];
                x.Values.CopyTo(values, 0);
                System.Array.Copy(x.Values, x.Values.Length - t.Rec.SrcSize, values, x.Values.Length, t.Rec.SrcSize);
            }
            size += t.Add(s, times, values, _origin.Length, aliases);
            keys += n;
        }
        if (keys == 0)
            return new ExternalSequenceInfo(s, o.AnimFile, ExternalState.NoKeys, unreadable > 0 ? $"{unreadable} key array(s) point outside the file" : "the file holds no keys for this model", 0);
        _loaded[s] = size;
        _bytes += size;
        _lru.AddFirst(s);
        // keep within the budget: release the least recently chosen sequences (never the one just loaded)
        while (_bytes > Budget && _lru.Count > 1)
        {
            int old = _lru.Last!.Value;
            _lru.RemoveLast();
            _bytes -= _loaded[old];
            _loaded.Remove(old);
            foreach (var t in _tracks) t.Remove(old);
        }
        return new ExternalSequenceInfo(s, o.AnimFile, ExternalState.Loaded, unreadable > 0 ? $"{unreadable} key array(s) point outside the file and were left out" : "", keys);
    }

    /// <summary>The parts of an .anim file by owner: a raw file is the model's part; a chunked one (AFM2 / AFSA / AFSB)
    /// has one part per chunk.</summary>
    internal static Dictionary<KeyOwner, M2ModernReader.Buf> Parts(byte[] bytes)
    {
        var res = new Dictionary<KeyOwner, M2ModernReader.Buf>();
        string magic = bytes.Length >= 4 ? Encoding.ASCII.GetString(bytes, 0, 4) : "";
        if (magic is not ("AFM2" or "AFSA" or "AFSB"))
        {
            res[KeyOwner.Model] = new M2ModernReader.Buf(bytes);
            return res;
        }
        foreach (var (m, off, size) in M2ModernReader.Chunks(bytes))
        {
            var owner = m switch { "AFM2" => KeyOwner.Model, "AFSB" => KeyOwner.SkelBones, "AFSA" => KeyOwner.SkelAttachments, _ => (KeyOwner?)null };
            if (owner is { } ow && !res.ContainsKey(ow)) res[ow] = new M2ModernReader.Buf(bytes.AsSpan(off, size).ToArray());
        }
        return res;
    }

    // ------------------------------------------------------------------ binding to the typed model

    /// <summary>Binds the recorded tracks to the typed model and its effects (called once by the reader).</summary>
    internal void Bind(M2Model m, M2EffectSet? fx, IEnumerable<ExtRecord> records)
    {
        foreach (var r in records)
        {
            var b = BindOne(m, fx, r);
            if (b != null) _tracks.Add(b);
        }
    }

    private static Bound? BindOne(M2Model m, M2EffectSet? fx, ExtRecord r)
    {
        int i = r.Index;
        switch (r.Array)
        {
            case "bones" when i < m.Bones.Length:
            {
                var b = m.Bones[i];
                return r.Field switch
                {
                    "translation" => Vec3(r, () => b.Translation, t => b.Translation = t),
                    "rotation" => Q(r, () => b.Rotation, t => b.Rotation = t),
                    "scale" => Vec3(r, () => b.Scale, t => b.Scale = t),
                    _ => null,
                };
            }
            case "colors" when i < m.Colors.Length:
            {
                var c = m.Colors[i];
                return r.Field == "color" ? Vec3(r, () => c.Color, t => c.Color = t) : r.Field == "alpha" ? S16(r, () => c.Alpha, t => c.Alpha = t) : null;
            }
            case "transparency" when i < m.Transparency.Length:
                return S16(r, () => m.Transparency[i], t => m.Transparency[i] = t);
            case "texture_transforms" when i < m.TextureTransforms.Length:
            {
                var x = m.TextureTransforms[i];
                return r.Field switch
                {
                    "translation" => Vec3(r, () => x.Translation, t => x.Translation = t),
                    "rotation" => Q(r, () => x.Rotation, t => x.Rotation = t),
                    "scale" => Vec3(r, () => x.Scale, t => x.Scale = t),
                    _ => null,
                };
            }
            case "particles" when fx != null && i < fx.Particles.Count:
                return fx.Particles[i].BindTrack(r);
            case "ribbons" when fx != null && i < fx.Ribbons.Count:
                return fx.Ribbons[i].BindTrack(r);
        }
        return null;
    }

    private static Bound Vec3(ExtRecord r, Func<M2Track<Vector3>> get, Action<M2Track<Vector3>> set) => new Bound<Vector3>(r, get, set, Decode.Vec3);
    private static Bound Q(ExtRecord r, Func<M2Track<Quaternion>> get, Action<M2Track<Quaternion>> set) =>
        new Bound<Quaternion>(r, get, set, r.Kind == ValueKind.CompQuat ? Decode.CompQuat : Decode.Quat);
    private static Bound S16(ExtRecord r, Func<M2Track<short>> get, Action<M2Track<short>> set) => new Bound<short>(r, get, set, Decode.Fixed16);

    /// <summary>A bound track: its record, and how to read, replace and rebuild the typed track.</summary>
    internal abstract class Bound
    {
        public ExtRecord Rec { get; }
        protected Bound(ExtRecord rec) => Rec = rec;
        /// <summary>Appends a sequence's keys (timeline times, values in the source form); returns the bytes kept.</summary>
        public abstract long Add(int s, uint[] times, byte[] values, int nSeq, int[] aliases);
        /// <summary>Removes a sequence's keys again (the track is rebuilt from the model's own keys and the others).</summary>
        public abstract void Remove(int s);
    }

    internal sealed class Bound<T> : Bound where T : struct
    {
        private readonly Func<M2Track<T>> _get;
        private readonly Action<M2Track<T>> _set;
        private readonly Func<byte[], T[]> _decode;
        private readonly M2Track<T> _base;
        private readonly Dictionary<int, (uint[] Times, T[] Values, int[] Aliases)> _keys = new();
        private int _nSeq;

        public Bound(ExtRecord rec, Func<M2Track<T>> get, Action<M2Track<T>> set, Func<byte[], T[]> decode) : base(rec)
        {
            _get = get;
            _set = set;
            _decode = decode;
            _base = get();
        }

        public override long Add(int s, uint[] times, byte[] values, int nSeq, int[] aliases)
        {
            var v = _decode(values);
            int n = Math.Min(times.Length, v.Length);
            if (n == 0) return 0;
            if (n < times.Length) times = times[..n];
            if (n < v.Length) v = v[..n];
            _keys[s] = (times, v, aliases);
            _nSeq = Math.Max(_nSeq, nSeq);
            _set(Append(_get(), s, times, v, nSeq, aliases));
            return (long)n * (4 + System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        }

        public override void Remove(int s)
        {
            if (!_keys.Remove(s)) return;
            var t = _base;
            foreach (var (k, x) in _keys) t = Append(t, k, x.Times, x.Values, _nSeq, x.Aliases);
            _set(t);
        }

        private static M2Track<T> Append(M2Track<T> cur, int s, uint[] times, T[] values, int nSeq, int[] aliases)
        {
            uint first = (uint)cur.Times.Length;
            var allTimes = new uint[cur.Times.Length + times.Length];
            cur.Times.CopyTo(allTimes, 0);
            times.CopyTo(allTimes, cur.Times.Length);
            var allValues = new T[cur.Values.Length + values.Length];
            cur.Values.CopyTo(allValues, 0);
            values.CopyTo(allValues, cur.Values.Length);
            nSeq = Math.Max(nSeq, cur.Ranges.Length);
            var ranges = new (uint, uint)[nSeq];
            if (cur.Ranges.Length > 0) cur.Ranges.CopyTo(ranges, 0);
            for (int k = cur.Ranges.Length; k < nSeq; k++) ranges[k] = (uint.MaxValue, uint.MaxValue);
            var r = (first, (uint)allTimes.Length - 1);
            ranges[s] = r;
            foreach (var a in aliases) if (a < nSeq) ranges[a] = r;
            return new M2Track<T>(cur.Interpolation, cur.GlobalSequence, ranges, allTimes, allValues);
        }
    }

    /// <summary>Value decoders from the source form.</summary>
    internal static class Decode
    {
        private static float F(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));

        public static Vector3[] Vec3(byte[] b)
        {
            var a = new Vector3[b.Length / 12];
            for (int i = 0; i < a.Length; i++) a[i] = new Vector3(F(b, i * 12), F(b, i * 12 + 4), F(b, i * 12 + 8));
            return a;
        }

        public static Quaternion[] CompQuat(byte[] b)
        {
            var a = new Quaternion[b.Length / 8];
            for (int i = 0; i < a.Length; i++)
            {
                float C(int c) => M2ModernReader.DecompressQuat(BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 8 + c * 2)));
                a[i] = new Quaternion(C(0), C(1), C(2), C(3));
            }
            return a;
        }

        public static Quaternion[] Quat(byte[] b)
        {
            int zero = 0;
            var x = Converter.ZeroQuatsAsIdentity((byte[])b.Clone(), ref zero);
            var a = new Quaternion[x.Length / 16];
            for (int i = 0; i < a.Length; i++) a[i] = new Quaternion(F(x, i * 16), F(x, i * 16 + 4), F(x, i * 16 + 8), F(x, i * 16 + 12));
            return a;
        }

        public static short[] Fixed16(byte[] b)
        {
            var a = new short[b.Length / 2];
            for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2));
            return a;
        }

        public static float[] Float(byte[] b)
        {
            var a = new float[b.Length / 4];
            for (int i = 0; i < a.Length; i++) a[i] = F(b, i * 4);
            return a;
        }

        public static ushort[] U16(byte[] b)
        {
            var a = new ushort[b.Length / 2];
            for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i * 2));
            return a;
        }

        public static byte[] U8(byte[] b) => (byte[])b.Clone();

        /// <summary>A plain gravity (a float pulling down the Z axis) as a vector.</summary>
        public static Vector3[] GravityPlain(byte[] b) => Float(b).Select(g => new Vector3(0, 0, -g)).ToArray();

        /// <summary>
        /// A packed gravity vector (particle flag 0x800000; FORMAT KNOWLEDGE): int8 x, int8 y (the direction's X and Y
        /// in 1/128), int16 z (the strength, in steps of 0.04238648; MEASURED on 1.60: 16 steps = 0.678, as the plain
        /// gravities of the same kind of effect, 0.694). The direction's Z completes the unit vector; the strength
        /// pulls along it, so (0, 0, -16) pulls down like a plain gravity of 0.678.
        /// </summary>
        public static Vector3[] GravityPacked(byte[] b)
        {
            var a = new Vector3[b.Length / 4];
            for (int i = 0; i < a.Length; i++) a[i] = Unpack(b.AsSpan(i * 4));
            return a;
        }

        public static Vector3 Unpack(ReadOnlySpan<byte> s)
        {
            float x = (sbyte)s[0] / 128f, y = (sbyte)s[1] / 128f;
            float z = MathF.Sqrt(Math.Max(0f, 1f - x * x - y * y));
            float strength = BinaryPrimitives.ReadInt16LittleEndian(s[2..]) * 0.04238648f;
            return new Vector3(x, y, z) * strength;
        }
    }
}
