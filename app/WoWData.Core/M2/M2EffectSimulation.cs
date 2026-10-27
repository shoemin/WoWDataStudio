using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>One live particle.</summary>
public struct M2Particle
{
    /// <summary>In model space, or in the emitter's (bone's) space for an emitter whose particles move with it.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age, Life;
    public float Rotation, SpinRate;
    /// <summary>Its own size factor (the emitter's scale variation).</summary>
    public Vector2 SizeFactor;
    /// <summary>Its own first texture cell (random-cell emitters), added to the life track's cell.</summary>
    public int CellOffset;
}

/// <summary>One edge of a ribbon: the band's top and bottom point when it was dropped, and its age.</summary>
public struct M2RibbonEdge
{
    public Vector3 Top, Bottom;
    public float Age;
}

/// <summary>
/// M3: the viewer's own simulation of a model's particle emitters and ribbons (<see cref="M2EffectSet"/>), stepped with
/// the animation clock and the bone matrices the renderer computed. It follows what the records describe (emission
/// rate, speed and its variation, the emission angles, the plane / sphere / spline area, gravity, drag, life and its
/// variation, colour / opacity / size / texture cell over the life, spin, the emitter's bone) in a simple way of our own;
/// twinkling, wind, tails, model particles and the second and third textures of multi-texture particles are left out,
/// as the model statistics say. Deterministic for a seed (tests and pictures). Not thread-safe.
/// </summary>
public sealed class M2EffectSimulation
{
    /// <summary>Particles kept per emitter at most (the oldest go first).</summary>
    public const int MaxParticles = 1500;
    /// <summary>Edges kept per ribbon at most.</summary>
    public const int MaxEdges = 400;

    private readonly M2EffectSet _fx;
    private readonly uint[] _gs;
    private readonly int _seed;
    private Random _rng;
    private readonly List<M2Particle>[] _particles;
    private readonly float[] _accum;
    private readonly List<M2RibbonEdge>[] _edges;
    private readonly float[] _sinceEdge;

    public M2EffectSimulation(M2EffectSet fx, uint[] globalSequences, int seed = 20261027)
    {
        _fx = fx;
        _gs = globalSequences;
        _seed = seed;
        _rng = new Random(seed);
        _particles = fx.Particles.Select(_ => new List<M2Particle>()).ToArray();
        _accum = new float[fx.Particles.Count];
        _edges = fx.Ribbons.Select(_ => new List<M2RibbonEdge>()).ToArray();
        _sinceEdge = new float[fx.Ribbons.Count];
    }

    public M2EffectSet Effects => _fx;
    public IReadOnlyList<M2Particle> Particles(int emitter) => _particles[emitter];
    public IReadOnlyList<M2RibbonEdge> Edges(int ribbon) => _edges[ribbon];
    public int ParticleCount => _particles.Sum(p => p.Count);
    public int EdgeCount => _edges.Sum(e => e.Count);

    /// <summary>Every particle and edge gone, the random numbers from the start.</summary>
    public void Reset()
    {
        _rng = new Random(_seed);
        foreach (var p in _particles) p.Clear();
        foreach (var e in _edges) e.Clear();
        System.Array.Clear(_accum);
        System.Array.Clear(_sinceEdge);
    }

    private float R(float lo, float hi) => lo + (float)_rng.NextDouble() * (hi - lo);

    /// <summary>
    /// A float track's value at the time (linear between keys). An animation without keys of its own takes the track's
    /// first key, as the 1.12 client does for an empty range (M2Model_FindTrackKeys: key[first]); MEASURED on the 1.60
    /// copy, many emitters are keyed in one animation only (Taerar's 39 emitters: only in Run); a track without any key
    /// gives <paramref name="fallback"/>.
    /// </summary>
    public static float Eval(M2Track<float> t, in M2Anim.Time time, uint[] gs, float fallback)
    {
        if (!M2Anim.FindKeys(t, time, gs, out int a, out int b, out float f)) return t.HasKeys ? t.Values[0] : fallback;
        if (t.Interpolation == 0 || a == b) return t.Values[a];
        return t.Values[a] + (t.Values[b] - t.Values[a]) * f;
    }

    /// <summary>A vector track's value with the same rule as <see cref="Eval(M2Track{float}, in M2Anim.Time, uint[], float)"/>.</summary>
    public static Vector3 Eval(M2Track<Vector3> t, in M2Anim.Time time, uint[] gs, Vector3 fallback) =>
        M2Anim.FindKeys(t, time, gs, out _, out _, out _) ? M2Anim.Eval(t, time, gs, fallback) : t.HasKeys ? t.Values[0] : fallback;

    /// <summary>A fixed16 track (0x7fff = 1) with the same rule.</summary>
    public static float EvalFixed16(M2Track<short> t, in M2Anim.Time time, uint[] gs, float fallback) =>
        M2Anim.FindKeys(t, time, gs, out _, out _, out _) ? M2Anim.EvalFixed16(t, time, gs, fallback) : t.HasKeys ? t.Values[0] / 32767f : fallback;

    /// <summary>A step track's value at the time (the key at or before it), with the same rule.</summary>
    public static T Step<T>(M2Track<T> t, in M2Anim.Time time, uint[] gs, T fallback) where T : struct =>
        M2Anim.FindKeys(t, time, gs, out int a, out _, out _) ? t.Values[a] : t.HasKeys ? t.Values[0] : fallback;

    /// <summary>
    /// Advances every emitter and ribbon by <paramref name="dt"/> seconds at the animation time
    /// <paramref name="time"/> with the bones' model-space matrices (the renderer's, billboards included).
    /// </summary>
    public void Step(in M2Anim.Time time, Matrix4x4[] bones, float dt)
    {
        if (dt <= 0) return;
        dt = Math.Min(dt, 0.25f);
        for (int i = 0; i < _fx.Particles.Count; i++) StepEmitter(i, time, bones, dt);
        for (int i = 0; i < _fx.Ribbons.Count; i++) StepRibbon(i, time, bones, dt);
    }

    private static Matrix4x4 BoneOf(Matrix4x4[] bones, int b) => b >= 0 && b < bones.Length ? bones[b] : Matrix4x4.Identity;

    private void StepEmitter(int i, in M2Anim.Time time, Matrix4x4[] bones, float dt)
    {
        var e = _fx.Particles[i];
        var list = _particles[i];
        var bone = BoneOf(bones, e.Bone);
        var gravity = Eval(e.Gravity, time, _gs, Vector3.Zero);
        float drag = Math.Clamp(e.Drag, -1f, 10f);
        // move and age the live particles; the dead ones go
        int w = 0;
        for (int k = 0; k < list.Count; k++)
        {
            var p = list[k];
            p.Age += dt;
            if (p.Age >= p.Life) continue;
            p.Velocity += gravity * dt;
            if (drag != 0) p.Velocity *= Math.Max(0f, 1f - drag * dt);
            p.Position += p.Velocity * dt;
            p.Rotation += p.SpinRate * dt;
            list[w++] = p;
        }
        list.RemoveRange(w, list.Count - w);
        bool enabled = !e.Enabled.HasKeys || Step(e.Enabled, time, _gs, (byte)1) != 0;
        if (!enabled) return;
        float rate = Math.Max(0f, Eval(e.EmissionRate, time, _gs, 0f) + e.EmissionRateVary * R(-1, 1));
        _accum[i] += rate * dt;
        int n = (int)_accum[i];
        _accum[i] -= n;
        n = Math.Min(n, MaxParticles);
        if (list.Count + n > MaxParticles) list.RemoveRange(0, list.Count + n - MaxParticles);
        for (int k = 0; k < n; k++) list.Add(Spawn(e, time, bone));
    }

    private M2Particle Spawn(M2ParticleEmitter e, in M2Anim.Time time, Matrix4x4 bone)
    {
        float speed = Eval(e.Speed, time, _gs, 0f);
        speed *= 1f + R(-1, 1) * Eval(e.SpeedVariation, time, _gs, 0f);
        float life = Eval(e.Lifespan, time, _gs, 1f) + e.LifespanVary * R(-1, 1);
        float vr = Eval(e.VerticalRange, time, _gs, 0f), hr = Eval(e.HorizontalRange, time, _gs, 0f);
        float len = Eval(e.AreaLength, time, _gs, 0f), wid = Eval(e.AreaWidth, time, _gs, 0f);
        Vector3 pos, dir;
        if (e.EmitterType == 2)
        {
            // sphere: a direction within the angles, at a radius between the length (inner) and the width (outer)
            float polar = R(-1, 1) * vr, azimuth = R(-1, 1) * hr;
            dir = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Sin(polar) * MathF.Sin(azimuth), MathF.Cos(polar));
            float radius = len + (wid - len) * R(0, 1);
            pos = dir * radius;
        }
        else
        {
            // plane (and the spline and unknown types): a point of the area, up within the angles
            pos = new Vector3(R(-1, 1) * len * 0.5f, R(-1, 1) * wid * 0.5f, 0);
            if (e.EmitterType == 3 && e.SplinePoints.Length > 0)
            {
                var sp = e.SplinePoints;
                float u = R(0, Math.Max(0, sp.Length - 1));
                int a = Math.Min((int)u, sp.Length - 1), b = Math.Min(a + 1, sp.Length - 1);
                pos = Vector3.Lerp(sp[a], sp[b], u - a);
            }
            float polar = R(0, 1) * vr, azimuth = R(-1, 1) * hr;
            dir = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Sin(polar) * MathF.Sin(azimuth), MathF.Cos(polar));
        }
        float z = Eval(e.ZSource, time, _gs, 0f);
        if (z > 0)
        {
            var d = pos - new Vector3(0, 0, z);
            if (d.LengthSquared() > 1e-8f) dir = Vector3.Normalize(d);
        }
        pos += e.Position;
        var vel = dir * speed;
        if (!e.Local)
        {
            pos = Vector3.Transform(pos, bone);
            vel = Vector3.TransformNormal(vel, bone);
        }
        var vary = e.ScaleVary;
        float sx = 1 + R(-1, 1) * vary.X;
        var size = (e.Flags & 0x80000) != 0 ? new Vector2(sx, 1 + R(-1, 1) * vary.Y) : new Vector2(sx, sx);
        int cells = Math.Max(1, e.Rows * e.Cols);
        return new M2Particle
        {
            Position = pos, Velocity = vel, Life = Math.Max(0.02f, life), SizeFactor = size,
            Rotation = e.BaseSpin + R(-1, 1) * e.BaseSpinVary, SpinRate = e.Spin + R(-1, 1) * e.SpinVary,
            CellOffset = e.RandomCell ? _rng.Next(cells) : 0,
        };
    }

    private void StepRibbon(int i, in M2Anim.Time time, Matrix4x4[] bones, float dt)
    {
        var r = _fx.Ribbons[i];
        var edges = _edges[i];
        float life = r.EdgeLifetime > 0 ? r.EdgeLifetime : 0.5f;
        int w = 0;
        for (int k = 0; k < edges.Count; k++)
        {
            var x = edges[k];
            x.Age += dt;
            if (k > 0 && x.Age > life) continue;
            if (r.Gravity != 0 && k > 0)
            {
                var drop = new Vector3(0, 0, -r.Gravity * x.Age * dt);
                x.Top += drop;
                x.Bottom += drop;
            }
            edges[w++] = x;
        }
        edges.RemoveRange(w, edges.Count - w);
        bool visible = !r.Visibility.HasKeys || Step(r.Visibility, time, _gs, (byte)1) != 0;
        if (!visible)
        {
            // a hidden ribbon stops growing; its edges fade out with their age
            if (edges.Count > 0 && edges[0].Age > life) edges.Clear();
            return;
        }
        var bone = BoneOf(bones, r.Bone);
        var center = Vector3.Transform(r.Position, bone);
        var up = Vector3.TransformNormal(Vector3.UnitZ, bone);
        up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : Vector3.UnitZ;
        float above = Eval(r.HeightAbove, time, _gs, 0f), below = Eval(r.HeightBelow, time, _gs, 0f);
        var head = new M2RibbonEdge { Top = center + up * above, Bottom = center - up * below, Age = 0 };
        float interval = r.EdgesPerSecond > 0 ? 1f / r.EdgesPerSecond : 1f / 30f;
        _sinceEdge[i] += dt;
        if (edges.Count == 0) edges.Add(head);
        if (_sinceEdge[i] >= interval)
        {
            // the head stays behind as an edge; a new head follows the emitter
            _sinceEdge[i] = 0;
            edges.Insert(0, head);
            if (edges.Count > MaxEdges) edges.RemoveRange(MaxEdges, edges.Count - MaxEdges);
        }
        else edges[0] = head;
    }

    /// <summary>A particle's colour (0..1, with opacity), size (half the quad's width and height) and texture cell at
    /// its age.</summary>
    public static (Vector4 Color, Vector2 Size, int Cell) Look(M2ParticleEmitter e, in M2Particle p)
    {
        float f = p.Life > 0 ? Math.Clamp(p.Age / p.Life, 0f, 1f) : 1f;
        var c = Lerp(e.Color, f, new Vector3(255f), Vector3.Lerp) / 255f;
        float a = Lerp(e.Alpha, f, (short)32767, (x, y, t) => (short)(x + (y - x) * t)) / 32767f;
        var s = Lerp(e.Scale, f, Vector2.One, Vector2.Lerp) * p.SizeFactor;
        int cells = Math.Max(1, e.Rows * e.Cols);
        int cell = (Lerp(e.HeadCell, f, (ushort)0, (x, y, t) => (ushort)MathF.Round(x + (y - x) * t)) + p.CellOffset) % cells;
        return (new Vector4(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1), Math.Clamp(c.Z, 0, 1), Math.Clamp(a, 0, 1)), s, cell);
    }

    private static T Lerp<T>(M2LifeTrack<T> t, float f, T fallback, Func<T, T, float, T> lerp) where T : struct
    {
        if (!t.Keys(f, out int a, out int b, out float x)) return fallback;
        return a == b ? t.Values[a] : lerp(t.Values[a], t.Values[b], x);
    }
}
