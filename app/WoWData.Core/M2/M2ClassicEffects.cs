using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.M2;

/// <summary>
/// The parts of a 1.12 particle emitter that the 1.12 client draws differently from the modern records
/// (<see cref="M2ParticleEmitter.Classic"/>). Every rule is the client's own (build 5875, our decompile):
/// M2Model_InitFromShared @0070ebd0 builds the emitter from the record, ParticleKeyBlock_Evaluate @007b9b10 gives a
/// particle's colour, size and texture cell, CParticleEmitter2_BuildVertices @007b2a50 draws heads, tails, twinkling
/// and spin.
/// </summary>
public sealed class M2ClassicParticle
{
    /// <summary>The life fraction where the second key block starts (record +0x14C): the colours, opacities and sizes
    /// are keyed at 0, MidPoint and 1 and blended linearly inside each block.</summary>
    public float MidPoint = 0.5f;
    /// <summary>Colour (R, G, B 0..255) and opacity (0..255) at birth, at the middle point and at death (record +0x150,
    /// three B, G, R, A colours).</summary>
    public Vector4[] Colors = { new(255), new(255), new(255) };
    /// <summary>The size (half the quad's width) at birth, at the middle point and at death (record +0x15C).</summary>
    public float[] Sizes = { 1, 1, 1 };
    /// <summary>The head's texture cells of the two key blocks (first, last; record +0x168 and +0x16E).</summary>
    public (int First, int Last)[] HeadCells = { (0, 0), (0, 0) };
    /// <summary>The tail's texture cells of the two key blocks (record +0x174 and +0x178).</summary>
    public (int First, int Last)[] TailCells = { (0, 0), (0, 0) };
    /// <summary>How many times each key block runs through its cells (record +0x16C and +0x172; 1 = once).</summary>
    public float[] CellRepeat = { 1, 1 };
    /// <summary>The particle type (record +0x2C, read as 16 bits by the client): 0 head, 1 tail, 2 head and tail.</summary>
    public int ParticleType;
    public bool Head => ParticleType is 0 or 2;
    public bool Tail => ParticleType is 1 or 2;
    /// <summary>Flag 0x400: the tail grows with the particle's age up to its length.</summary>
    public bool TailGrows;
    /// <summary>Flag 0x200: each particle spins one way or the other.</summary>
    public bool RandomSpinDirection;
    /// <summary>Flag 0x8000: when the emitter is switched on it emits a burst of one second's particles at once.</summary>
    public bool Burst;
    /// <summary>Sphere emitters, flag 0x100: the particles fly straight up instead of outwards.</summary>
    public bool SphereUp;
}

/// <summary>
/// Reads the particle emitters and ribbons of your 1.12 game's model (M2 version 256/257) into <see cref="M2EffectSet"/>,
/// the form the 3D view's simulation (<see cref="M2EffectSimulation"/>) draws. The records are the 1.12 layout of
/// <see cref="M2Schema.Particle"/> (0x1F8 bytes) and <see cref="M2Schema.Ribbon"/> (0xDC bytes); what the 1.12 client does
/// with each field was read in our decompile (M2Model_InitFromShared @0070ebd0, M2Model_UpdateEmittersAndLights
/// @00718960, the CParticleEmitter2 and CRibbonEmitter classes) where it differs from what the modern records mean:
/// <list type="bullet">
/// <item>the tracks have per-animation key ranges, as the bones' (<see cref="M2Anim.FindKeys{T}"/>); the gravity is one
/// number pulling down Z;</item>
/// <item>the blend mode (+0x28) is the materials' numbering: the particle batch hands it to M2Scene_SetBatchBlendState as
/// a material's blend (M2Scene_DrawParticleBatch @0070d8b0), so 1 is alpha key, 3 add without alpha, 4 add;</item>
/// <item>the emitter type is 16 bits at +0x2A (1 plane, 2 sphere, 3 spline; any other value makes the client refuse the
/// whole model); the particle type (head, tail or both) 16 bits at +0x2C;</item>
/// <item>colour, opacity and size are three keys (birth, middle point, death) in two key blocks, the texture cells a
/// range per block with a repeat count; one spin; no life or rate variation;</item>
/// <item>the emitter's frame is the bone's turned 90 degrees about Z (Math_Mat4_Rotate in
/// M2Model_UpdateEmittersAndLights);</item>
/// <item>a ribbon spans its bone's Y axis.</item>
/// </list>
/// </summary>
public static class M2ClassicEffects
{
    /// <summary>The emitters and ribbons of a 1.12 model (null when it has none or none can be drawn) and the notes on the
    /// ones left out.</summary>
    public static (M2EffectSet? Effects, List<string> Notes) Read(M2Model m)
    {
        var notes = new List<string>();
        if (m.Modern != null) return (m.Modern.Effects, notes);
        var fx = new M2EffectSet();
        try
        {
            var parts = m.File["particles"];
            for (int i = 0; i < parts.Count; i++)
                if (Particle(m, parts, i, notes) is { } p) fx.Particles.Add(p);
            var ribs = m.File["ribbons"];
            for (int i = 0; i < ribs.Count; i++)
                if (Ribbon(m, ribs, i, notes) is { } r) fx.Ribbons.Add(r);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            notes.Add($"the particle emitters and ribbons could not be read ({e.Message}): not drawn");
            return (null, notes);
        }
        return (fx.Particles.Count + fx.Ribbons.Count > 0 ? fx : null, notes);
    }

    /// <summary>
    /// What the 3D view does with a 1.12 model's emitters and ribbons, in words, for the model statistics and
    /// "wds models info": the drawn counts, what is drawn approximately (grouped, with how many emitters) and what is
    /// left out. Empty for a model without any, and for another version's model (its report is in
    /// <see cref="M2Model.Modern"/>).
    /// </summary>
    public static (string Summary, List<string> Approximated, IReadOnlyList<string> LeftOut) Describe(M2Model m)
    {
        if (m.Modern != null || m.ParticleCount + m.RibbonCount == 0) return ("", new List<string>(), System.Array.Empty<string>());
        var fx = m.Effects;
        int p = fx?.Particles.Count ?? 0, r = fx?.Ribbons.Count ?? 0;
        var summary = $"{Plural(p, "particle emitter")} and {Plural(r, "ribbon")} drawn by the viewer's own simulation of the 1.12 client's rules (close to the game's look, not exact)";
        return (summary, fx?.Approximated() ?? new List<string>(), m.EffectNotes);
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// Where a 1.12 model's effects are when it has no mesh to frame (many spell effects are only particles and ribbons
    /// and have empty bounds in their header): the bone pivots and, around each emitter's and ribbon's place, how far its
    /// particles fly in their first animation (speed x lifespan, the area, the size) or how tall the ribbon is. Null
    /// when the model has no effects or they reach nowhere.
    /// </summary>
    public static (Vector3 Min, Vector3 Max)? Bounds(M2Model m)
    {
        if (m.Modern != null || m.Effects is not { } fx) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        void Add(Vector3 c, float r)
        {
            if (!float.IsFinite(c.X + c.Y + c.Z + r)) return;
            min = Vector3.Min(min, c - new Vector3(r));
            max = Vector3.Max(max, c + new Vector3(r));
        }
        var t = new M2Anim.Time(0, m.Sequences.Length > 0 ? m.Sequences[0].Start : 0, 0, 0);
        var gs = m.GlobalSequences;
        foreach (var b in m.Bones) Add(b.Pivot, 0);
        foreach (var p in fx.Particles)
        {
            float speed = Math.Abs(M2EffectSimulation.Eval(p.Speed, t, gs, 0f)), life = Math.Max(0f, M2EffectSimulation.Eval(p.Lifespan, t, gs, 1f));
            float area = Math.Max(Math.Abs(M2EffectSimulation.Eval(p.AreaLength, t, gs, 0f)), Math.Abs(M2EffectSimulation.Eval(p.AreaWidth, t, gs, 0f)));
            float size = p.Classic?.Sizes.Max() ?? 0f;
            float fall = Math.Abs(M2EffectSimulation.Eval(p.Gravity, t, gs, Vector3.Zero).Z) * life * life * 0.5f;
            Add(p.Position, Math.Min(50f, speed * life + area + size + fall));
        }
        foreach (var r in fx.Ribbons)
            Add(r.Position, Math.Min(50f, Math.Max(M2EffectSimulation.Eval(r.HeightAbove, t, gs, 0f), M2EffectSimulation.Eval(r.HeightBelow, t, gs, 0f)) * 2));
        if (!float.IsFinite(min.X) || (max - min).Length() < 1e-4f) return null;
        return (min, max);
    }

    private static float F(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private static M2Track<float> Floats(M2TrackView t) => M2Model.Track(t, (b, o) => F(b, o), 4);
    private static M2Track<ushort> U16s(M2TrackView t) => M2Model.Track(t, (b, o) => U16(b, o), 2);
    private static M2Track<byte> U8s(M2TrackView t) => M2Model.Track(t, (b, o) => b[o], 1);
    /// <summary>The 1.12 gravity (one number, CParticleEmitter2_IntegrateParticle: velocity.z -= gravity x dt) as the
    /// vector the simulation adds.</summary>
    private static M2Track<Vector3> Gravity(M2TrackView t) => M2Model.Track(t, (b, o) => new Vector3(0, 0, -F(b, o)), 4);

    private static M2ParticleEmitter? Particle(M2Model m, M2Data d, int i, List<string> notes)
    {
        var b = d.Bytes;
        int r = i * d.Elem.Size;
        uint flags = U32(b, r + 4);
        int type = U16(b, r + 0x2A);
        var p = new M2ParticleEmitter
        {
            Index = i, Flags = flags, Position = d.Vec3(i, "position"), Bone = U16(b, r + 0x14), Textures = new[] { (int)U16(b, r + 0x16) },
            Blend = U16(b, r + 0x28), EmitterType = type, Rows = Math.Max(1, (int)U16(b, r + 0x30)), Cols = Math.Max(1, (int)U16(b, r + 0x32)),
            Speed = Floats(d.Track(i, "emission_speed")), SpeedVariation = Floats(d.Track(i, "speed_variation")),
            VerticalRange = Floats(d.Track(i, "vertical_range")), HorizontalRange = Floats(d.Track(i, "horizontal_range")),
            Gravity = Gravity(d.Track(i, "gravity")), Lifespan = Floats(d.Track(i, "lifespan")), EmissionRate = Floats(d.Track(i, "emission_rate")),
            AreaLength = Floats(d.Track(i, "emission_area_length")), AreaWidth = Floats(d.Track(i, "emission_area_width")),
            ZSource = Floats(d.Track(i, "z_source")), Enabled = U8s(d.Track(i, "enabled")),
        };
        var c = new M2ClassicParticle
        {
            MidPoint = F(b, r + 0x14C), ParticleType = U16(b, r + 0x2C), TailGrows = (flags & 0x400) != 0, RandomSpinDirection = (flags & 0x200) != 0,
            Burst = (flags & 0x8000) != 0, SphereUp = type == 2 && (flags & 0x100) != 0,
        };
        for (int k = 0; k < 3; k++)
        {
            int o = r + 0x150 + 4 * k;   // a CImVector: B, G, R, A
            c.Colors[k] = new Vector4(b[o + 2], b[o + 1], b[o], b[o + 3]);
            c.Sizes[k] = F(b, r + 0x15C + 4 * k);
        }
        c.HeadCells[0] = (U16(b, r + 0x168), U16(b, r + 0x16A));
        c.CellRepeat[0] = U16(b, r + 0x16C);
        c.HeadCells[1] = (U16(b, r + 0x16E), U16(b, r + 0x170));
        c.CellRepeat[1] = U16(b, r + 0x172);
        c.TailCells[0] = (BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(r + 0x174)), BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(r + 0x176)));
        c.TailCells[1] = (BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(r + 0x178)), BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(r + 0x17A)));
        p.Classic = c;
        // the 22 numbers at +0x17C (M2Model_InitFromShared copies them into the emitter)
        p.TailLength = F(b, r + 0x17C);
        p.TwinkleSpeed = F(b, r + 0x180);
        p.TwinklePercent = F(b, r + 0x184);
        p.TwinkleMin = F(b, r + 0x188);
        p.TwinkleMax = F(b, r + 0x18C);
        p.BurstMultiplier = F(b, r + 0x190);
        p.Drag = F(b, r + 0x194);
        p.Spin = F(b, r + 0x198);
        p.WindVector = new Vector3(F(b, r + 0x1B4), F(b, r + 0x1B8), F(b, r + 0x1BC));
        p.WindTime = F(b, r + 0x1C0);
        p.SplinePoints = d.Child(i, "spline_points").AsVec3();
        // the life tracks (for the statistics and tests; the drawing uses the key blocks above)
        var times = new[] { 0f, Math.Clamp(c.MidPoint, 0, 1), 1f };
        p.Color = new M2LifeTrack<Vector3>(times, c.Colors.Select(x => new Vector3(x.X, x.Y, x.Z)).ToArray());
        p.Alpha = new M2LifeTrack<short>(times, c.Colors.Select(x => (short)Math.Round(x.W * 32767 / 255)).ToArray());
        p.Scale = new M2LifeTrack<Vector2>(times, c.Sizes.Select(x => new Vector2(x)).ToArray());
        string geometry = d.Child(i, "geometry_model").AsString(), recursion = d.Child(i, "recursion_model").AsString();
        p.SpawnsModels = geometry.Length > 0;
        Approximate(p, c, recursion.Length > 0);
        if (type is not (1 or 2 or 3))
        {
            notes.Add($"particle emitter {i} has emitter type {type}, which the 1.12 client does not know (the game refuses to draw this model): left out");
            return null;
        }
        if (p.Bone >= m.Bones.Length || p.Textures[0] >= m.Textures.Length)
        {
            notes.Add($"particle emitter {i} names bone {p.Bone} or texture {p.Textures[0]}, which the model does not have: left out");
            return null;
        }
        return p;
    }

    /// <summary>What of a 1.12 emitter the viewer draws approximately (in words, grouped by the statistics).</summary>
    private static void Approximate(M2ParticleEmitter p, M2ClassicParticle c, bool recursion)
    {
        if (p.SpawnsModels) p.Approximated.Add("particles that are models drawn as quads");
        if (recursion) p.Approximated.Add("effects emitted by each particle left out");
        if (p.EmitterType == 3) p.Approximated.Add("spline emitters drawn along straight lines between their points");
        if ((p.Flags & 0x2000) != 0) p.Approximated.Add("particles placed on the ground drawn where they are born");
        if ((p.Flags & 0x4040) != 0) p.Approximated.Add("particles following the emitter's movement drawn without it");
        if (p.EmitterType == 2 && (p.Flags & 0x80) != 0) p.Approximated.Add("sphere particles that stop at the centre drawn to the end of their life");
        if (p.Blend is < 0 or > 6) p.Approximated.Add($"particle blend mode {p.Blend} drawn as alpha");
        if (!IsPow2(p.Rows) || !IsPow2(p.Cols)) p.Approximated.Add("texture cells in rows or columns that are not a power of two drawn as the game's whole texture");
        if (c.ParticleType is not (0 or 1 or 2)) p.Approximated.Add($"particle type {c.ParticleType} drawn as heads");
    }

    private static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    private static M2RibbonEmitter? Ribbon(M2Model m, M2Data d, int i, List<string> notes)
    {
        var b = d.Bytes;
        int r = i * d.Elem.Size;
        var x = new M2RibbonEmitter
        {
            Index = i, Classic = true, Bone = (int)U32(b, r + 4), Position = d.Vec3(i, "position"),
            Textures = d.Child(i, "textures").AsU16().Select(v => (int)v).ToArray(), Materials = d.Child(i, "materials").AsU16().Select(v => (int)v).ToArray(),
            Color = M2Model.Vec3Track(d.Track(i, "color")), Alpha = M2Model.I16Track(d.Track(i, "alpha")),
            HeightAbove = Floats(d.Track(i, "height_above")), HeightBelow = Floats(d.Track(i, "height_below")),
            EdgesPerSecond = F(b, r + 0x94), EdgeLifetime = F(b, r + 0x98), Gravity = F(b, r + 0x9C),
            Rows = Math.Max(1, (int)U16(b, r + 0xA0)), Cols = Math.Max(1, (int)U16(b, r + 0xA2)),
            TextureSlot = U16s(d.Track(i, "texture_slot")), Visibility = U8s(d.Track(i, "visibility")),
        };
        if (x.Textures.Length > 1) x.Approximated.Add("ribbons with several textures drawn with their first one");
        if (x.Bone >= m.Bones.Length || x.Textures.Length == 0 || x.Textures[0] >= m.Textures.Length || x.Materials.Length == 0 || x.Materials[0] >= m.RenderFlags.Length)
        {
            notes.Add($"ribbon {i} names a bone, texture or material the model does not have: left out");
            return null;
        }
        x.Blend = m.RenderFlags[x.Materials[0]].Blend;
        if (x.Blend is < 0 or > 6) x.Approximated.Add($"ribbon blend mode {x.Blend} drawn as alpha");
        return x;
    }
}
