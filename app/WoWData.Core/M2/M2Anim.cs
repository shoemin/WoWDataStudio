using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>
/// Track evaluation with the 1.12 client's semantics (port of wowdata/m2/anim.py, checked against the decompile):
///  * all sequences share one timeline (ms); sequence s plays from sequences[s].Start to .End;
///  * a track's ranges[s] = {first, last} (inclusive) are the keys sequence s uses (M2Model_FindTrackKeys @00713d50);
///    a track without ranges uses all its keys; when last &lt;= first the value is key[first];
///  * a track with a global sequence runs on its own clock (global time modulo global_sequences[gs]) instead of the
///    sequence time, still within the keys of ranges[s];
///  * the key pair is the last key at or before the time and the next one (which may belong to the next range, as in
///    the client); the fraction is clamped to [0, 1] (the client does not clamp a time before the first key);
///  * interpolation 0 = step (key0), anything else = linear: vectors and floats are lerped, quaternions are lerped
///    component-wise WITHOUT normalisation (M2Model_EvalQuatTrack @00713ea0); the rotation matrix is then built
///    with the standard formula, as the client does. 1.12 bone/colour/transparency tracks only use types 0 and 1;
///    types 2/3 (hermite/bezier) only occur in camera spline tracks, which the viewer does not play.
/// </summary>
public static class M2Anim
{
    /// <summary>Animation time: the sequence index, the time inside it (ms from its start) and the global clock (ms).</summary>
    public readonly record struct Time(int Sequence, uint SequenceStart, uint SequenceTime, double GlobalMs);

    /// <summary>Key pair and fraction (M2Model_FindTrackKeys); false when the track has no key for the sequence.</summary>
    public static bool FindKeys<T>(M2Track<T> t, in Time time, uint[] globalSequences, out int k0, out int k1, out float f) where T : struct
    {
        k0 = k1 = 0;
        f = 0;
        var ts = t.Times;
        int n = Math.Min(ts.Length, t.Values.Length);
        if (n == 0) return false;
        int lo, hi;
        if (t.Ranges.Length == 0)
        {
            lo = 0;
            hi = n - 1;
        }
        else
        {
            if (time.Sequence < 0 || time.Sequence >= t.Ranges.Length) return false;
            var r = t.Ranges[time.Sequence];
            lo = (int)Math.Min(r.First, int.MaxValue);
            hi = (int)Math.Min(r.Last, int.MaxValue);
        }
        if (lo >= n) return false;
        if (hi <= lo)
        {
            k0 = k1 = lo;
            return true;
        }
        if (hi >= n) hi = n - 1;
        uint now;
        if (t.GlobalSequence >= 0 && t.GlobalSequence < globalSequences.Length)
        {
            uint period = globalSequences[t.GlobalSequence];
            now = period == 0 ? 0 : (uint)(time.GlobalMs % period);
        }
        else now = time.SequenceStart + time.SequenceTime;
        // the last key in [lo, hi] at or before the time (lo when the time is before every key)
        int i = lo;
        if (now >= ts[lo])
        {
            int a = lo, b = hi;
            while (a < b)
            {
                int m = (a + b + 1) >> 1;
                if (ts[m] <= now) a = m;
                else b = m - 1;
            }
            i = a;
        }
        if (i + 1 >= n)
        {
            k0 = k1 = i;
            return true;
        }
        k0 = i;
        k1 = i + 1;
        long den = (long)ts[k1] - ts[k0];
        f = den <= 0 ? 0f : Math.Clamp((float)((double)((long)now - ts[k0]) / den), 0f, 1f);
        return true;
    }

    public static Vector3 Eval(M2Track<Vector3> t, in Time time, uint[] gs, Vector3 fallback)
    {
        if (!FindKeys(t, time, gs, out int a, out int b, out float f)) return fallback;
        if (t.Interpolation == 0 || a == b) return t.Values[a];
        return Vector3.Lerp(t.Values[a], t.Values[b], f);
    }

    /// <summary>Component-wise lerp, not normalised (like the client).</summary>
    public static Quaternion Eval(M2Track<Quaternion> t, in Time time, uint[] gs, Quaternion fallback)
    {
        if (!FindKeys(t, time, gs, out int a, out int b, out float f)) return fallback;
        var q0 = t.Values[a];
        if (t.Interpolation == 0 || a == b) return q0;
        var q1 = t.Values[b];
        return new Quaternion(q0.X + (q1.X - q0.X) * f, q0.Y + (q1.Y - q0.Y) * f, q0.Z + (q1.Z - q0.Z) * f, q0.W + (q1.W - q0.W) * f);
    }

    /// <summary>A fixed16 track (0x7fff = 1) as a float.</summary>
    public static float EvalFixed16(M2Track<short> t, in Time time, uint[] gs, float fallback)
    {
        if (!FindKeys(t, time, gs, out int a, out int b, out float f)) return fallback;
        float v0 = t.Values[a] / 32767f;
        if (t.Interpolation == 0 || a == b) return v0;
        return v0 + (t.Values[b] / 32767f - v0) * f;
    }

    /// <summary>The client's bone matrix (column vectors): T(pivot) T(t) R S T(-pivot), as a System.Numerics
    /// row-vector matrix (v * M).</summary>
    public static Matrix4x4 BoneLocal(Vector3 pivot, Vector3 translation, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
        Matrix4x4.CreateTranslation(translation) * Matrix4x4.CreateTranslation(pivot);

    /// <summary>Model-space bone matrices for one time (parents first; bones whose parent comes later are resolved
    /// recursively). <paramref name="adjust"/> may replace a bone's final matrix before its children use it (the
    /// renderer's billboards, which need the camera).</summary>
    public static void BoneMatrices(M2Model m, in Time time, Matrix4x4[] out_, bool[]? done = null, Func<int, Matrix4x4, Matrix4x4>? adjust = null)
    {
        int n = m.Bones.Length;
        done ??= new bool[n];
        System.Array.Clear(done, 0, n);
        for (int i = 0; i < n; i++) Bone(m, i, time, out_, done, 0, adjust);
    }

    private static void Bone(M2Model m, int i, in Time time, Matrix4x4[] out_, bool[] done, int depth, Func<int, Matrix4x4, Matrix4x4>? adjust)
    {
        if (done[i]) return;
        var b = m.Bones[i];
        var local = b.IsAnimated
            ? BoneLocal(b.Pivot, Eval(b.Translation, time, m.GlobalSequences, Vector3.Zero), Eval(b.Rotation, time, m.GlobalSequences, Quaternion.Identity),
                Eval(b.Scale, time, m.GlobalSequences, Vector3.One))
            : Matrix4x4.Identity;
        int p = b.Parent;
        if (p >= 0 && p < m.Bones.Length && p != i && depth < 512)
        {
            Bone(m, p, time, out_, done, depth + 1, adjust);
            local *= out_[p];
        }
        out_[i] = adjust != null ? adjust(i, local) : local;
        done[i] = true;
    }

    /// <summary>The texture-coordinate matrix of a texture transform (UV animation), row-vector form:
    /// T(-0.5) S R T(0.5) T(t) around the texture centre (the client's texAnimMatrices).</summary>
    public static Matrix4x4 TextureMatrix(M2TextureTransform tt, in Time time, uint[] gs)
    {
        var t = Eval(tt.Translation, time, gs, Vector3.Zero);
        var r = Eval(tt.Rotation, time, gs, Quaternion.Identity);
        var s = Eval(tt.Scale, time, gs, Vector3.One);
        var c = new Vector3(0.5f, 0.5f, 0);
        return Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(c) * Matrix4x4.CreateTranslation(t);
    }
}
