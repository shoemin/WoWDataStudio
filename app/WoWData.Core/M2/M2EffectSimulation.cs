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
    /// <summary>1.12: its own random number (the client uses the particle's address): its twinkle phase and, with flag
    /// 0x200, its spin direction.</summary>
    public uint Seed;
}

/// <summary>What a particle looks like at its age: colour (0..1, with opacity), size (half the quad's width and
/// height), the head's and the tail's texture cell, and whether it shows (1.12 twinkling hides some).</summary>
public readonly record struct M2ParticleLook(Vector4 Color, Vector2 Size, int Cell, int TailCell, bool Visible);

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
/// Your 1.12 game's emitters (<see cref="M2ParticleEmitter.Classic"/>) and ribbons (<see cref="M2RibbonEmitter.Classic"/>)
/// follow the 1.12 client's own rules instead (<see cref="M2ClassicEffects"/>): its spawn shapes and frame, a life of
/// exactly the current lifespan, wind, bursts, twinkling, tails, two key blocks and the ribbon's Y axis; another
/// version's emitters are simulated exactly as before.
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
    private readonly bool[] _burstOn;

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
        _burstOn = new bool[fx.Particles.Count];
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
        System.Array.Clear(_burstOn);
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
        if (e.Classic is { } classic)
        {
            // CParticleEmitter2_StepSimulation: a long frame is simulated in steps of 0.1 s
            while (dt > 0.1f + 1e-6f)
            {
                StepClassic(i, e, classic, time, bones, 0.1f);
                dt -= 0.1f;
            }
            StepClassic(i, e, classic, time, bones, dt);
            return;
        }
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

    // ---------------------------------------------------------------- 1.12 emitters (the client's own rules)

    /// <summary>The 1.12 emitter's frame is its bone's turned 90 degrees about Z (M2Model_UpdateEmittersAndLights:
    /// Math_Mat4_Rotate(pi/2, Z) before CParticleEmitter2_UpdateFrame): local X becomes the bone's Y.</summary>
    public static readonly Matrix4x4 ClassicTurn = Matrix4x4.CreateRotationZ(MathF.PI / 2);

    /// <summary>One step of a 1.12 emitter (CParticleEmitter2_SimulateStep): emit (EmitParticles), then age every
    /// particle, kill the ones as old as the current lifespan and move the others (IntegrateParticle).</summary>
    private void StepClassic(int i, M2ParticleEmitter e, M2ClassicParticle c, in M2Anim.Time time, Matrix4x4[] bones, float dt)
    {
        if (dt <= 0) return;
        var list = _particles[i];
        var bone = BoneOf(bones, e.Bone);
        float lifespan = Eval(e.Lifespan, time, _gs, 1f);
        // the enabled track switches the emission off (SetEmissionRate(enabled ? rate : 0)); a spline emits rate x its
        // largest parameter (CParticleEmitter2Spline vf7/vf10); flag 0x8000 adds a burst when the emitter comes on
        bool enabled = !e.Enabled.HasKeys || Step(e.Enabled, time, _gs, (byte)1) != 0;
        float raw = Eval(e.EmissionRate, time, _gs, 0f);
        float rate = enabled ? Math.Max(0f, raw) : 0f;
        if (e.EmitterType == 3) rate *= Math.Clamp(Eval(e.AreaWidth, time, _gs, 0f), 0f, 1f);
        int burst = 0;
        if (c.Burst)
        {
            bool on = enabled && raw > 0;
            if (on && !_burstOn[i]) burst = (int)rate;
            _burstOn[i] = on;
        }
        _accum[i] += rate * dt;
        int n = Math.Max(0, (int)MathF.Floor(_accum[i] + 0.5f));
        _accum[i] -= n;
        n = Math.Min(n + burst, MaxParticles);
        if (list.Count + n > MaxParticles) list.RemoveRange(0, list.Count + n - MaxParticles);
        for (int k = 0; k < n; k++) list.Add(SpawnClassic(e, c, time, bone, dt, lifespan));
        float g = -Eval(e.Gravity, time, _gs, Vector3.Zero).Z;
        int w = 0;
        for (int k = 0; k < list.Count; k++)
        {
            var p = list[k];
            p.Age += dt;
            p.Life = lifespan;
            if (!(p.Age < lifespan)) continue;
            // wind while young, the move with gravity (exact over the step), then drag
            if (p.Age < e.WindTime) p.Velocity += e.WindVector * dt;
            p.Position += p.Velocity * dt;
            p.Position.Z -= g * dt * dt * 0.5f;
            p.Velocity.Z -= g * dt;
            if (e.Drag != 0) p.Velocity -= p.Velocity * Math.Min(1f, dt * e.Drag);
            p.Rotation += p.SpinRate * dt;
            list[w++] = p;
        }
        list.RemoveRange(w, list.Count - w);
    }

    /// <summary>A new 1.12 particle (the InitParticle of CParticleEmitter2Plane / Sphere / Spline).</summary>
    private M2Particle SpawnClassic(M2ParticleEmitter e, M2ClassicParticle c, in M2Anim.Time time, Matrix4x4 bone, float dt, float lifespan)
    {
        float speed = Eval(e.Speed, time, _gs, 0f) * (1f + R(-1, 1) * Eval(e.SpeedVariation, time, _gs, 0f));
        float vr = Eval(e.VerticalRange, time, _gs, 0f), hr = Eval(e.HorizontalRange, time, _gs, 0f);
        float len = Eval(e.AreaLength, time, _gs, 0f), wid = Eval(e.AreaWidth, time, _gs, 0f);
        Vector3 pos, dir;
        switch (e.EmitterType)
        {
            case 2:
            {
                // sphere: a radius between the length and the width, an elevation within +-vertical range from the XY
                // plane and an azimuth within +-horizontal range; the particle flies outwards (or up, flag 0x100)
                float radius = len + (wid - len) * R(0, 1);
                float el = R(-1, 1) * vr, az = R(-1, 1) * hr;
                var nrm = new Vector3(MathF.Cos(az) * MathF.Cos(el), MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el));
                pos = nrm * radius;
                dir = c.SphereUp ? Vector3.UnitZ : nrm;
                break;
            }
            case 3:
            {
                // spline: a point between the parameters length..width (0..1) of the curve; up, or turned about the
                // curve's direction by up to the vertical range and pushed out by up to the horizontal range
                float lo = Math.Clamp(len, 0f, 1f), hi = Math.Clamp(wid, 0f, 1f);
                pos = SplinePoint(e.SplinePoints, lo + R(0, 1) * (hi - lo), out var tangent);
                dir = Vector3.UnitZ;
                if (vr != 0 && tangent.LengthSquared() > 1e-12f)
                {
                    dir = Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(tangent), R(-1, 1) * vr));
                    if (hr != 0) pos += dir * hr * R(0, 1);
                }
                break;
            }
            default:
            {
                // plane: a point of the length x width rectangle; a direction tilted from Z by +-vertical range at an
                // azimuth within +-horizontal range
                pos = new Vector3(R(-1, 1) * len * 0.5f, R(-1, 1) * wid * 0.5f, 0);
                float polar = R(-1, 1) * vr, az = R(-1, 1) * hr;
                dir = new Vector3(MathF.Sin(polar) * MathF.Cos(az), MathF.Sin(polar) * MathF.Sin(az), MathF.Cos(polar));
                break;
            }
        }
        float z = Eval(e.ZSource, time, _gs, 0f);
        if (z != 0)
        {
            var d = pos - new Vector3(0, 0, z);
            if (d.LengthSquared() > 1e-12f) dir = Vector3.Normalize(d);
        }
        var vel = Vector3.TransformNormal(dir * speed, ClassicTurn);
        pos = Vector3.TransformNormal(pos, ClassicTurn) + e.Position;
        if (!e.Local)
        {
            pos = Vector3.Transform(pos, bone);
            vel = Vector3.TransformNormal(vel, bone);
        }
        uint seed = (uint)_rng.Next();
        float spin = e.Spin * (c.RandomSpinDirection && (seed & 0x20) != 0 ? -1f : 1f);
        float age = R(0, 1) * dt;
        return new M2Particle
        {
            Position = pos, Velocity = vel, Age = age, Life = lifespan, SizeFactor = Vector2.One, Rotation = spin * age, SpinRate = spin, Seed = seed,
        };
    }

    /// <summary>The point at parameter <paramref name="u"/> (0..1, by length) of a 1.12 spline emitter's points and the
    /// direction there. Like the client's CCRSpline the curve runs from the second point to the one before the last
    /// (the end points steer it) when there are four or more; we join the points with straight lines.</summary>
    public static Vector3 SplinePoint(Vector3[] pts, float u, out Vector3 tangent)
    {
        tangent = Vector3.Zero;
        if (pts.Length == 0) return Vector3.Zero;
        if (pts.Length == 1) return pts[0];
        if (u <= 0)
        {
            tangent = pts[1] - pts[0];
            return pts[0];
        }
        if (u >= 1)
        {
            tangent = pts[^1] - pts[^2];
            return pts[^1];
        }
        int a = pts.Length >= 4 ? 1 : 0, b = pts.Length >= 4 ? pts.Length - 2 : pts.Length - 1;
        float total = 0;
        for (int k = a; k < b; k++) total += Vector3.Distance(pts[k], pts[k + 1]);
        float want = u * total;
        for (int k = a; k < b; k++)
        {
            float l = Vector3.Distance(pts[k], pts[k + 1]);
            if (want <= l || k == b - 1)
            {
                tangent = pts[k + 1] - pts[k];
                return Vector3.Lerp(pts[k], pts[k + 1], l > 0 ? Math.Clamp(want / l, 0f, 1f) : 0f);
            }
            want -= l;
        }
        return pts[a];
    }

    /// <summary>Our own stand-in for the client's table of 128 random numbers (g_ParticleRandomTable) that twinkling
    /// reads: fixed values in 0..1, so pictures repeat.</summary>
    private static readonly float[] TwinkleTable = Enumerable.Range(0, 128).Select(i =>
    {
        uint x = (uint)i * 2654435761u + 0x9E3779B9u;
        x ^= x >> 15;
        x *= 0x2C1B3C6Du;
        x ^= x >> 12;
        return (x & 0xFFFFFF) / 16777216f;
    }).ToArray();

    /// <summary>
    /// What a particle looks like at its age. A 1.12 particle (ParticleKeyBlock_Evaluate, CParticleEmitter2_BuildVertices):
    /// the key block of its age (birth..middle point, middle point..death), the fraction in it scaled by 0.99 and moved
    /// by 0.005, colour, opacity and size blended between the block's two keys, the head's and tail's cells counted
    /// through the block's range (repeated as many times as the block says); twinkling hides it while the twinkle
    /// percentage is below its random number and scales its size between the twinkle minimum and maximum. Another
    /// version's particle: <see cref="Look"/>.
    /// </summary>
    public static M2ParticleLook Appearance(M2ParticleEmitter e, in M2Particle p)
    {
        if (e.Classic is not { } c)
        {
            var (col, size, cell) = Look(e, p);
            return new M2ParticleLook(col, size, cell, cell, true);
        }
        float life = Math.Max(0.001f, p.Life), mid = life * c.MidPoint;
        int k = p.Age <= mid ? 0 : 1;
        float t0 = k == 0 ? 0 : mid, t1 = k == 0 ? mid : life;
        float f = Math.Abs(t1 - t0) > 1e-9f ? (p.Age - t0) / (t1 - t0) : 0f;
        f = f * 0.99f + 0.005f;
        var rgba = Vector4.Lerp(c.Colors[k], c.Colors[k + 1], f) / 255f;
        float s = c.Sizes[k] + (c.Sizes[k + 1] - c.Sizes[k]) * f;
        float rep = c.CellRepeat[k];
        float cf = rep == 1f ? f : (f * rep) % 1f;
        bool visible = true;
        float range = e.TwinkleMax - e.TwinkleMin;
        if (e.TwinklePercent < 1f || range != 0)
        {
            int idx = (int)(((uint)Math.Clamp(e.TwinkleSpeed * p.Age, 0f, 255f) + p.Seed) & 0x7f);
            float rnd = TwinkleTable[idx];
            if (e.TwinklePercent < 1f && e.TwinklePercent < rnd) visible = false;
            if (range != 0) s *= rnd * range + e.TwinkleMin;
        }
        var color = new Vector4(Math.Clamp(rgba.X, 0, 1), Math.Clamp(rgba.Y, 0, 1), Math.Clamp(rgba.Z, 0, 1), Math.Clamp(rgba.W, 0, 1));
        return new M2ParticleLook(color, new Vector2(s), Cell(c.HeadCells[k], cf), Cell(c.TailCells[k], cf), visible);
    }

    /// <summary>A texture cell counted through a 1.12 key block's range (ParticleKeyBlock_SetTileRange): first..last, or
    /// down from first when first is larger; the client keeps the low 8 bits.</summary>
    public static int Cell((int First, int Last) r, float f)
    {
        int start, count;
        if (r.First <= r.Last)
        {
            start = r.First;
            count = r.Last - r.First + 1;
        }
        else
        {
            start = r.First + 1;
            count = r.Last - r.First - 1;
        }
        return (int)MathF.Floor(start + count * f) & 0xff;
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
            if (r.Classic && r.Gravity != 0 && k > 0)
            {
                // 1.12 (CRibbonEmitter_Update): z += gravity x (2 x age + dt) x dt, so z moves by gravity x age squared
                float rise = r.Gravity * (2 * x.Age + dt) * dt;
                x.Top.Z += rise;
                x.Bottom.Z += rise;
            }
            x.Age += dt;
            if (k > 0 && x.Age > life) continue;
            if (!r.Classic && r.Gravity != 0 && k > 0)
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
        // 1.12 (CRibbonEmitter_ComputeEdgePoints): the band spans the frame's Y axis
        var axis = r.Classic ? Vector3.UnitY : Vector3.UnitZ;
        var up = Vector3.TransformNormal(axis, bone);
        up = up.LengthSquared() > 1e-10f ? Vector3.Normalize(up) : axis;
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
