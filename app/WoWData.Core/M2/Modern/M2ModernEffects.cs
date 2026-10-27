using System.Buffers.Binary;
using System.Numerics;

namespace WoWData.Core.M2.Modern;

/// <summary>
/// M3 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: the particle emitters and ribbons of a modern model, read into
/// <see cref="M2EffectSet"/> on the same timeline as the bones. Our own code, written from the format (FORMAT
/// KNOWLEDGE), every layout checked on the 1.60 copy before the viewer used it (MEASURED: 9,410 particle records of
/// 0x1EC bytes and 836 ribbon records of 0xB0 bytes in the creature, spell, item and object models, each with its bone,
/// textures, materials, tracks and life tracks inside the file; a stride of 0x1DC fails for most).
/// </summary>
internal sealed partial class Converter
{
    public const int ParticleSize = 0x1EC, RibbonSize = 0xB0;

    private static float F(M2ModernReader.Buf b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.B.AsSpan(o));
    private static Vector3 V3(M2ModernReader.Buf b, int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));

    /// <summary>A track read straight into the typed form (effects have no 1.12-shaped record here).</summary>
    private M2Track<T> Typed<T>(int at, int srcSize, ValueKind kind, Func<byte[], T[]> decode, string array, int index, string field) where T : struct
    {
        var locs = Own(at);
        var g = Gather(locs, srcSize, true);
        var v = decode(g.Values.ToArray());
        int n = Math.Min(g.Times.Count, v.Length);
        if (g.HasExternal) ExtRecords.Add(new ExtRecord(array, index, field, locs, srcSize, kind, true));
        if (n == 0) return new M2Track<T>(g.Interp, g.GlobalSeq, System.Array.Empty<(uint, uint)>(), System.Array.Empty<uint>(), System.Array.Empty<T>());
        return new M2Track<T>(g.Interp, g.GlobalSeq, g.Ranges, g.Times.Take(n).ToArray(), v.Length == n ? v : v[..n]);
    }

    /// <summary>A life track (timestamps fixed16 over the particle's life, MEASURED: 0, 19660, 32767...).</summary>
    private M2LifeTrack<T> Life<T>(int at, int size, Func<byte[], T[]> decode) where T : struct
    {
        uint tn = _b.U32(at), to = _b.U32(at + 4), vn = _b.U32(at + 8), vo = _b.U32(at + 12);
        int n = (int)Math.Min(tn, vn);
        if (n == 0 || !_b.Fits(tn, to, 2) || !_b.Fits(vn, vo, size)) { if (n > 0) _unreadable++; return M2LifeTrack<T>.Empty; }
        var times = new float[n];
        for (int i = 0; i < n; i++) times[i] = _b.U16((int)to + 2 * i) / 32767f;
        var values = decode(_b.B.AsSpan((int)vo, n * size).ToArray());
        return new M2LifeTrack<T>(times, values);
    }

    private static Vector2[] Vec2s(byte[] b)
    {
        var a = new Vector2[b.Length / 8];
        for (int i = 0; i < a.Length; i++) a[i] = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 8 + 4)));
        return a;
    }

    /// <summary>The particle emitters and ribbons; <paramref name="bones"/> and <paramref name="textures"/> bound the
    /// indices (a record pointing outside is left out and noted).</summary>
    public M2EffectSet Effects(int bones, int textures, int materials)
    {
        var fx = new M2EffectSet();
        uint np = _b.U32(HParticles), po = _b.U32(HParticles + 4);
        if (np > 0 && !_b.Fits(np, po, ParticleSize))
            _info.Notes.Add($"the {np} particle emitters run past the end of the model and were left out");
        else
            for (int i = 0; i < np; i++)
            {
                int r = (int)po + i * ParticleSize;
                var p = new M2ParticleEmitter
                {
                    Index = i, Flags = _b.U32(r + 4), Position = V3(_b, r + 8), Bone = _b.U16(r + 0x14), Blend = _b.B[r + 0x28], EmitterType = _b.B[r + 0x29],
                    Rows = Math.Max(1, (int)_b.U16(r + 0x30)), Cols = Math.Max(1, (int)_b.U16(r + 0x32)),
                };
                int tex = _b.U16(r + 0x16);
                p.Textures = p.MultiTexture ? new[] { tex & 0x1F, (tex >> 5) & 0x1F, (tex >> 10) & 0x1F } : new[] { tex };
                p.Speed = Typed(r + 0x34, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "speed");
                p.SpeedVariation = Typed(r + 0x48, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "speed_variation");
                p.VerticalRange = Typed(r + 0x5C, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "vertical_range");
                p.HorizontalRange = Typed(r + 0x70, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "horizontal_range");
                p.Gravity = p.PackedGravity
                    ? Typed(r + 0x84, 4, ValueKind.GravityPacked, ModernAnimations.Decode.GravityPacked, "particles", i, "gravity")
                    : Typed(r + 0x84, 4, ValueKind.GravityPlain, ModernAnimations.Decode.GravityPlain, "particles", i, "gravity");
                p.Lifespan = Typed(r + 0x98, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "lifespan");
                p.LifespanVary = F(_b, r + 0xAC);
                p.EmissionRate = Typed(r + 0xB0, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "emission_rate");
                p.EmissionRateVary = F(_b, r + 0xC4);
                p.AreaLength = Typed(r + 0xC8, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "area_length");
                p.AreaWidth = Typed(r + 0xDC, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "area_width");
                p.ZSource = Typed(r + 0xF0, 4, ValueKind.Float, ModernAnimations.Decode.Float, "particles", i, "z_source");
                p.Color = Life(r + 0x104, 12, ModernAnimations.Decode.Vec3);
                p.Alpha = Life(r + 0x114, 2, ModernAnimations.Decode.Fixed16);
                p.Scale = Life(r + 0x124, 8, Vec2s);
                p.ScaleVary = new Vector2(F(_b, r + 0x134), F(_b, r + 0x138));
                p.HeadCell = Life(r + 0x13C, 2, ModernAnimations.Decode.U16);
                p.TailCell = Life(r + 0x14C, 2, ModernAnimations.Decode.U16);
                p.TailLength = F(_b, r + 0x15C);
                p.TwinkleSpeed = F(_b, r + 0x160);
                p.TwinklePercent = F(_b, r + 0x164);
                p.TwinkleMin = F(_b, r + 0x168);
                p.TwinkleMax = F(_b, r + 0x16C);
                p.BurstMultiplier = F(_b, r + 0x170);
                p.Drag = F(_b, r + 0x174);
                p.BaseSpin = F(_b, r + 0x178);
                p.BaseSpinVary = F(_b, r + 0x17C);
                p.Spin = F(_b, r + 0x180);
                p.SpinVary = F(_b, r + 0x184);
                p.WindVector = V3(_b, r + 0x1A0);
                p.WindTime = F(_b, r + 0x1AC);
                uint sn = _b.U32(r + 0x1C0), so = _b.U32(r + 0x1C4);
                if (sn > 0 && _b.Fits(sn, so, 12)) p.SplinePoints = Enumerable.Range(0, (int)sn).Select(k => V3(_b, (int)so + 12 * k)).ToArray();
                p.Enabled = Typed(r + 0x1C8, 1, ValueKind.U8, ModernAnimations.Decode.U8, "particles", i, "enabled");
                p.SpawnsModels = _b.U32(r + 0x18) > 1 || _b.U32(r + 0x20) > 1;
                Approximate(p);
                if (p.Bone >= bones || p.Textures[0] >= Math.Max(1, textures))
                {
                    _info.Notes.Add($"particle emitter {i} names bone {p.Bone} or texture {p.Textures[0]}, which the model does not have: left out");
                    continue;
                }
                fx.Particles.Add(p);
            }
        uint nr = _b.U32(HRibbons), ro = _b.U32(HRibbons + 4);
        if (nr > 0 && !_b.Fits(nr, ro, RibbonSize))
            _info.Notes.Add($"the {nr} ribbons run past the end of the model and were left out");
        else
            for (int i = 0; i < nr; i++)
            {
                int r = (int)ro + i * RibbonSize;
                var x = new M2RibbonEmitter
                {
                    Index = i, Bone = (int)_b.U32(r + 4), Position = V3(_b, r + 8),
                    Textures = U16Array(r + 0x14), Materials = U16Array(r + 0x1C),
                    EdgesPerSecond = F(_b, r + 0x74), EdgeLifetime = F(_b, r + 0x78), Gravity = F(_b, r + 0x7C),
                    Rows = Math.Max(1, (int)_b.U16(r + 0x80)), Cols = Math.Max(1, (int)_b.U16(r + 0x82)), PriorityPlane = _b.I16(r + 0xAC),
                };
                x.Color = Typed(r + 0x24, 12, ValueKind.Vec3, ModernAnimations.Decode.Vec3, "ribbons", i, "color");
                x.Alpha = Typed(r + 0x38, 2, ValueKind.Fixed16, ModernAnimations.Decode.Fixed16, "ribbons", i, "alpha");
                x.HeightAbove = Typed(r + 0x4C, 4, ValueKind.Float, ModernAnimations.Decode.Float, "ribbons", i, "height_above");
                x.HeightBelow = Typed(r + 0x60, 4, ValueKind.Float, ModernAnimations.Decode.Float, "ribbons", i, "height_below");
                x.TextureSlot = Typed(r + 0x84, 2, ValueKind.U16, ModernAnimations.Decode.U16, "ribbons", i, "texture_slot");
                x.Visibility = Typed(r + 0x98, 1, ValueKind.U8, ModernAnimations.Decode.U8, "ribbons", i, "visibility");
                if (x.Textures.Length > 1) x.Approximated.Add("ribbons with several textures drawn with their first one");
                if (x.Rows * x.Cols > 1) x.Approximated.Add("ribbons with texture cells drawn with the whole texture");
                if (x.Bone >= bones || x.Textures.Length == 0 || x.Textures[0] >= Math.Max(1, textures) || x.Materials.Length == 0 || x.Materials[0] >= Math.Max(1, materials))
                {
                    _info.Notes.Add($"ribbon {i} names a bone, texture or material the model does not have: left out");
                    continue;
                }
                fx.Ribbons.Add(x);
            }
        return fx;
    }

    private int[] U16Array(int at)
    {
        uint n = _b.U32(at), o = _b.U32(at + 4);
        if (n == 0 || !_b.Fits(n, o, 2)) return System.Array.Empty<int>();
        return Enumerable.Range(0, (int)n).Select(k => (int)_b.U16((int)o + 2 * k)).ToArray();
    }

    /// <summary>What of a particle emitter the viewer draws approximately (the effect drawing's limits, in words).</summary>
    private static void Approximate(M2ParticleEmitter p)
    {
        if (p.MultiTexture) p.Approximated.Add("particles with several textures drawn with their first one");
        if (p.EmitterType == 3) p.Approximated.Add("spline emitters drawn from their points");
        if (p.EmitterType is not (1 or 2 or 3)) p.Approximated.Add($"emitter type {p.EmitterType} drawn as a plane");
        if (p.SpawnsModels) p.Approximated.Add("particles that are models drawn as quads");
        if (p.TwinklePercent > 0 && p.TwinkleSpeed > 0 && p.TwinklePercent < 1) p.Approximated.Add("twinkling left out");
        if (p.WindVector != Vector3.Zero && p.WindTime > 0) p.Approximated.Add("wind left out");
        if (p.Blend is < 0 or > 7) p.Approximated.Add($"particle blend mode {p.Blend} drawn as alpha");
    }
}
