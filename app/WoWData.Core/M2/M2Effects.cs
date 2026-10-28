using System.Numerics;
using WoWData.Core.M2.Modern;

namespace WoWData.Core.M2;

/// <summary>
/// A value over a particle's life (the "fake" tracks of the particle records): keys at fractions of the life (0 = born,
/// 1 = dies), linearly interpolated, the last value held.
/// </summary>
public sealed class M2LifeTrack<T> where T : struct
{
    public static readonly M2LifeTrack<T> Empty = new(System.Array.Empty<float>(), System.Array.Empty<T>());

    public float[] Times { get; }
    public T[] Values { get; }

    public M2LifeTrack(float[] times, T[] values)
    {
        Times = times;
        Values = values;
    }

    public bool HasKeys => Times.Length > 0 && Values.Length > 0;

    /// <summary>The value at life fraction <paramref name="f"/> (0..1): (key a, key b, fraction between them).</summary>
    public bool Keys(float f, out int a, out int b, out float t)
    {
        a = b = 0;
        t = 0;
        int n = Math.Min(Times.Length, Values.Length);
        if (n == 0) return false;
        if (f <= Times[0] || n == 1) return true;
        for (int i = 1; i < n; i++)
        {
            if (f > Times[i]) continue;
            a = i - 1;
            b = i;
            float den = Times[i] - Times[i - 1];
            t = den <= 0 ? 1f : (f - Times[i - 1]) / den;
            return true;
        }
        a = b = n - 1;
        return true;
    }
}

/// <summary>
/// M3: a particle emitter of a modern model (version 264 and later, record of 0x1EC bytes; FORMAT KNOWLEDGE, every
/// record of the 1.60 copy checked, see <see cref="Modern.M2ModernReader"/>), or of your 1.12 game's model (record of
/// 0x1F8 bytes, <see cref="Classic"/>, read by <see cref="M2ClassicEffects"/>). The tracks run on the model's timeline
/// (as the bones' do); the life tracks run over each particle's life.
/// </summary>
public sealed class M2ParticleEmitter
{
    public int Index;
    public uint Flags;
    public Vector3 Position;
    public int Bone;
    /// <summary>The texture(s): one index, or up to three with the multi-texture flag (0x10000000).</summary>
    public int[] Textures = System.Array.Empty<int>();
    /// <summary>The blend mode (the modern numbering, as the materials': 0 opaque ... 7 blend add; a 1.12 particle's is
    /// the 1.12 materials' 0-6, see <see cref="MaterialBlends"/>).</summary>
    public int Blend;
    /// <summary>1 plane, 2 sphere, 3 spline.</summary>
    public int EmitterType;
    public int Rows = 1, Cols = 1;
    public M2Track<float> Speed = M2Track<float>.Empty, SpeedVariation = M2Track<float>.Empty, VerticalRange = M2Track<float>.Empty,
        HorizontalRange = M2Track<float>.Empty, Lifespan = M2Track<float>.Empty, EmissionRate = M2Track<float>.Empty,
        AreaLength = M2Track<float>.Empty, AreaWidth = M2Track<float>.Empty, ZSource = M2Track<float>.Empty;
    /// <summary>Gravity as a vector in the emitter's space (a plain gravity pulls down the Z axis).</summary>
    public M2Track<Vector3> Gravity = M2Track<Vector3>.Empty;
    public M2Track<byte> Enabled = M2Track<byte>.Empty;
    public float LifespanVary, EmissionRateVary;
    /// <summary>Colour (0..255 per channel), opacity (fixed16), size (x, y) and texture cell over the life.</summary>
    public M2LifeTrack<Vector3> Color = M2LifeTrack<Vector3>.Empty;
    public M2LifeTrack<short> Alpha = M2LifeTrack<short>.Empty;
    public M2LifeTrack<Vector2> Scale = M2LifeTrack<Vector2>.Empty;
    public M2LifeTrack<ushort> HeadCell = M2LifeTrack<ushort>.Empty, TailCell = M2LifeTrack<ushort>.Empty;
    public Vector2 ScaleVary;
    public float TailLength, TwinkleSpeed, TwinklePercent, TwinkleMin, TwinkleMax, BurstMultiplier, Drag, BaseSpin, BaseSpinVary, Spin, SpinVary;
    public Vector3 WindVector;
    public float WindTime;
    public Vector3[] SplinePoints = System.Array.Empty<Vector3>();
    /// <summary>The emitter spawns models (a model name or reference), not drawn.</summary>
    public bool SpawnsModels;
    /// <summary>What of this emitter the viewer draws approximately or leaves out (empty: drawn as read).</summary>
    public List<string> Approximated = new();
    /// <summary>Read from your 1.12 game's model (M2 version 256/257, record of 0x1F8 bytes): what the 1.12 client draws
    /// differently from the modern records (<see cref="M2ClassicEffects"/>); null for another version's emitter.</summary>
    public M2ClassicParticle? Classic;

    public bool MultiTexture => (Flags & 0x10000000) != 0;
    /// <summary>The gravity values are packed vectors (flag 0x800000).</summary>
    public bool PackedGravity => (Flags & 0x800000) != 0;
    /// <summary>Particles stay in the emitter's space and move with it (flag 0x10, "do not trail").</summary>
    public bool Local => (Flags & 0x10) != 0;
    /// <summary>Quads lying in the emitter's XY plane instead of facing the camera (flag 0x1000).</summary>
    public bool XyQuads => (Flags & 0x1000) != 0;
    /// <summary>A random texture cell for each particle (flag 0x10000).</summary>
    public bool RandomCell => (Flags & 0x10000) != 0;

    internal ModernAnimations.Bound? BindTrack(ExtRecord r)
    {
        ModernAnimations.Bound F(Func<M2Track<float>> g, Action<M2Track<float>> s) => new ModernAnimations.Bound<float>(r, g, s, ModernAnimations.Decode.Float);
        return r.Field switch
        {
            "speed" => F(() => Speed, t => Speed = t),
            "speed_variation" => F(() => SpeedVariation, t => SpeedVariation = t),
            "vertical_range" => F(() => VerticalRange, t => VerticalRange = t),
            "horizontal_range" => F(() => HorizontalRange, t => HorizontalRange = t),
            "lifespan" => F(() => Lifespan, t => Lifespan = t),
            "emission_rate" => F(() => EmissionRate, t => EmissionRate = t),
            "area_length" => F(() => AreaLength, t => AreaLength = t),
            "area_width" => F(() => AreaWidth, t => AreaWidth = t),
            "z_source" => F(() => ZSource, t => ZSource = t),
            "gravity" => new ModernAnimations.Bound<Vector3>(r, () => Gravity, t => Gravity = t, PackedGravity ? ModernAnimations.Decode.GravityPacked : ModernAnimations.Decode.GravityPlain),
            "enabled" => new ModernAnimations.Bound<byte>(r, () => Enabled, t => Enabled = t, ModernAnimations.Decode.U8),
            _ => null,
        };
    }
}

/// <summary>
/// M3: a ribbon emitter of a modern model (record of 0xB0 bytes; FORMAT KNOWLEDGE, every record of the 1.60 copy
/// checked), or of your 1.12 game's model (record of 0xDC bytes, <see cref="Classic"/>): a band trailing behind a point
/// of a bone, made of edges dropped over time.
/// </summary>
public sealed class M2RibbonEmitter
{
    public int Index;
    public int Bone;
    public Vector3 Position;
    public int[] Textures = System.Array.Empty<int>();
    /// <summary>Material (render-flag) entries; the first one's blend mode draws the ribbon.</summary>
    public int[] Materials = System.Array.Empty<int>();
    public M2Track<Vector3> Color = M2Track<Vector3>.Empty;
    public M2Track<short> Alpha = M2Track<short>.Empty;
    public M2Track<float> HeightAbove = M2Track<float>.Empty, HeightBelow = M2Track<float>.Empty;
    public float EdgesPerSecond, EdgeLifetime, Gravity;
    public int Rows = 1, Cols = 1;
    public M2Track<ushort> TextureSlot = M2Track<ushort>.Empty;
    public M2Track<byte> Visibility = M2Track<byte>.Empty;
    public int PriorityPlane;
    public List<string> Approximated = new();
    /// <summary>Read from your 1.12 game's model (record of 0xDC bytes): the band spans the bone's Y axis, gravity moves
    /// the edges along +Z by gravity x age squared, the texture slot picks a cell (<see cref="M2ClassicEffects"/>).</summary>
    public bool Classic;
    /// <summary>1.12: the blend mode of the first material (the materials' numbering), resolved when read.</summary>
    public int Blend = -1;

    internal ModernAnimations.Bound? BindTrack(ExtRecord r) => r.Field switch
    {
        "color" => new ModernAnimations.Bound<Vector3>(r, () => Color, t => Color = t, ModernAnimations.Decode.Vec3),
        "alpha" => new ModernAnimations.Bound<short>(r, () => Alpha, t => Alpha = t, ModernAnimations.Decode.Fixed16),
        "height_above" => new ModernAnimations.Bound<float>(r, () => HeightAbove, t => HeightAbove = t, ModernAnimations.Decode.Float),
        "height_below" => new ModernAnimations.Bound<float>(r, () => HeightBelow, t => HeightBelow = t, ModernAnimations.Decode.Float),
        "texture_slot" => new ModernAnimations.Bound<ushort>(r, () => TextureSlot, t => TextureSlot = t, ModernAnimations.Decode.U16),
        "visibility" => new ModernAnimations.Bound<byte>(r, () => Visibility, t => Visibility = t, ModernAnimations.Decode.U8),
        _ => null,
    };
}

/// <summary>M3: the particle emitters and ribbons of a model, and what of them is drawn approximately.</summary>
public sealed class M2EffectSet
{
    public List<M2ParticleEmitter> Particles { get; } = new();
    public List<M2RibbonEmitter> Ribbons { get; } = new();

    /// <summary>"12 particle emitters with several textures drawn with their first one", per kind, for the statistics.</summary>
    public List<string> Approximated()
    {
        var res = new List<string>();
        foreach (var g in Particles.SelectMany(p => p.Approximated).Concat(Ribbons.SelectMany(r => r.Approximated)).GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            res.Add($"{g.Key} ({g.Count()})");
        return res;
    }
}
