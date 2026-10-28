using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Text;
using WoWData.Core.M2;

namespace WoWData.Tests;

/// <summary>
/// 1.12 models (M2 version 256, MD20) made from our own bytes for the particle and ribbon tests, in the style of
/// <see cref="SyntheticClients"/>: no client bytes. The header and record offsets come from <see cref="M2Schema"/> (the
/// layout the decompile proves), the values are chosen by each test.
/// </summary>
public static class SyntheticClassicModels
{
    /// <summary>One animation track: interpolation 1 (linear), no global sequence, a key range per animation.</summary>
    public sealed record Keys(uint[] Times, byte[] Values, (uint First, uint Last)[] Ranges)
    {
        public static Keys Float(float v) => new(new uint[] { 0 }, F32s(v), new[] { (0u, 0u), (0u, 0u) });
        public static Keys Byte(byte v) => new(new uint[] { 0 }, new[] { v }, new[] { (0u, 0u), (0u, 0u) });
        public static Keys U16(ushort v) => new(new uint[] { 0 }, BitConverter.GetBytes(v), new[] { (0u, 0u), (0u, 0u) });
        public static Keys I16(short v) => new(new uint[] { 0 }, BitConverter.GetBytes(v), new[] { (0u, 0u), (0u, 0u) });
        public static Keys Vec3(Vector3 v) => new(new uint[] { 0 }, F32s(v.X, v.Y, v.Z), new[] { (0u, 0u), (0u, 0u) });
        /// <summary>Stand (animation 0, 0-1000 ms) uses the first value, Walk (1000-2000 ms) the second.</summary>
        public static Keys PerAnimation(float stand, float walk) => new(new uint[] { 0, 1000 }, F32s(stand, walk), new[] { (0u, 0u), (1u, 1u) });
    }

    /// <summary>A 1.12 particle emitter record (0x1F8 bytes), field by field.</summary>
    public sealed class Particle
    {
        public uint Flags = 0x1 | 0x8;
        public Vector3 Position;
        public ushort Bone, Texture, Blend = 4, EmitterType = 1, ParticleType, Rows = 1, Cols = 1;
        public Keys Speed = Keys.Float(1), SpeedVariation = Keys.Float(0), VerticalRange = Keys.Float(0), HorizontalRange = Keys.Float(0),
            Gravity = Keys.Float(0), Lifespan = Keys.Float(1), EmissionRate = Keys.Float(10), AreaLength = Keys.Float(0), AreaWidth = Keys.Float(0),
            ZSource = Keys.Float(0), Enabled = Keys.Byte(1);
        public float MidPoint = 0.5f;
        /// <summary>R, G, B, A (0..255) at birth, middle and death; written as the client's B, G, R, A.</summary>
        public (byte R, byte G, byte B, byte A)[] Colors = { (255, 255, 255, 255), (255, 255, 255, 255), (255, 255, 255, 255) };
        public float[] Sizes = { 1, 1, 1 };
        public ushort[] LifespanCells = { 0, 0, 1 }, DecayCells = { 0, 0, 1 };
        public short[] TailCells = { 0, 0 }, TailDecayCells = { 0, 0 };
        /// <summary>The 22 numbers at +0x17C: tail length, twinkle speed, percent, min, max, burst, drag, spin, tumble
        /// (6), wind (3), wind time, follow (4).</summary>
        public float[] Params = { 1, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        public Vector3[] SplinePoints = System.Array.Empty<Vector3>();
        public string GeometryModel = "", RecursionModel = "";
    }

    /// <summary>A 1.12 ribbon record (0xDC bytes).</summary>
    public sealed class Ribbon
    {
        public uint Bone;
        public Vector3 Position;
        public ushort[] Textures = { 0 }, Materials = { 0 };
        public Keys Color = Keys.Vec3(Vector3.One), Alpha = Keys.I16(32767), HeightAbove = Keys.Float(0.5f), HeightBelow = Keys.Float(0.5f),
            TextureSlot = Keys.U16(0), Visibility = Keys.Byte(1);
        public float EdgesPerSecond = 10, EdgeLifetime = 1, Gravity;
        public ushort Rows = 1, Cols = 1;
    }

    private static byte[] F32s(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    private sealed class Blob
    {
        public readonly List<byte> Data = new();

        public uint Put(byte[] b)
        {
            while (Data.Count % 16 != 0) Data.Add(0);
            uint at = (uint)Data.Count;
            Data.AddRange(b);
            return at;
        }
    }

    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void W16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    private static void WF(byte[] b, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);

    private static void Arr(Blob blob, byte[] rec, int at, int count, byte[] data)
    {
        W32(rec, at, (uint)count);
        W32(rec, at + 4, count == 0 ? 0 : blob.Put(data));
    }

    private static void Track(Blob blob, byte[] rec, M2Rec layout, string name, Keys k)
    {
        int at = layout.Track(name).Offset;
        W16(rec, at, 1);
        BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(at + 2), -1);
        Arr(blob, rec, at + 4, k.Ranges.Length, k.Ranges.SelectMany(r => BitConverter.GetBytes(r.First).Concat(BitConverter.GetBytes(r.Last))).ToArray());
        Arr(blob, rec, at + 12, k.Times.Length, k.Times.SelectMany(BitConverter.GetBytes).ToArray());
        Arr(blob, rec, at + 20, k.Times.Length, k.Values);
    }

    private static void EmptyTrack(byte[] rec, int at) => BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(at + 2), -1);

    /// <summary>
    /// A model with one bone (not animated), two animations (Stand 0-1000 ms, Walk 1000-2000 ms), one texture
    /// (<c>Spells\Synthetic_Fx.blp</c>), one material (blend <paramref name="materialBlend"/>), no mesh, and the given
    /// particle emitters and ribbons.
    /// </summary>
    public static byte[] Model(IReadOnlyList<Particle> particles, IReadOnlyList<Ribbon>? ribbons = null, ushort materialBlend = 4, int textures = 1)
    {
        ribbons ??= System.Array.Empty<Ribbon>();
        var h = M2Schema.Header256;
        var head = new byte[h.Size];
        var blob = new Blob();
        blob.Data.AddRange(new byte[h.Size]);
        Encoding.ASCII.GetBytes("MD20").CopyTo(head, 0);
        W32(head, 4, 256);
        int A(string n) => h.Array(n).Offset;
        Arr(blob, head, A("name"), 10, Encoding.ASCII.GetBytes("Synthetic\0"));
        // two animations
        var seqs = new byte[2 * M2Schema.Sequence.Size];
        for (int s = 0; s < 2; s++)
        {
            int o = s * M2Schema.Sequence.Size;
            W16(seqs, o + M2Schema.Sequence.Field("anim_id").Offset, (ushort)(s == 0 ? 0 : 4));
            W32(seqs, o + M2Schema.Sequence.Field("start").Offset, (uint)(s * 1000));
            W32(seqs, o + M2Schema.Sequence.Field("end").Offset, (uint)(s * 1000 + 1000));
            W32(seqs, o + M2Schema.Sequence.Field("flags").Offset, 0x20);
            BinaryPrimitives.WriteInt16LittleEndian(seqs.AsSpan(o + M2Schema.Sequence.Field("next_variant").Offset), -1);
            W16(seqs, o + M2Schema.Sequence.Field("next_alias").Offset, (ushort)s);
        }
        Arr(blob, head, A("sequences"), 2, seqs);
        // one bone that does not move
        var bone = new byte[M2Schema.Bone.Size];
        BinaryPrimitives.WriteInt32LittleEndian(bone.AsSpan(0), -1);
        BinaryPrimitives.WriteInt16LittleEndian(bone.AsSpan(M2Schema.Bone.Field("parent").Offset), -1);
        foreach (var t in new[] { "translation", "rotation", "scale" }) EmptyTrack(bone, M2Schema.Bone.Track(t).Offset);
        Arr(blob, head, A("bones"), 1, bone);
        // textures and one material
        var tex = new byte[textures * M2Schema.Texture.Size];
        for (int t = 0; t < textures; t++)
        {
            var name = Encoding.ASCII.GetBytes($"Spells\\Synthetic_Fx{(t == 0 ? "" : t.ToString())}.blp\0");
            Arr(blob, tex, t * M2Schema.Texture.Size + M2Schema.Texture.Array("filename").Offset, name.Length, name);
        }
        Arr(blob, head, A("textures"), textures, tex);
        var rf = new byte[4];
        W16(rf, 2, materialBlend);
        Arr(blob, head, A("render_flags"), 1, rf);
        // particles
        var pl = M2Schema.Particle;
        var prec = new byte[particles.Count * pl.Size];
        for (int i = 0; i < particles.Count; i++)
        {
            var p = particles[i];
            var r = new byte[pl.Size];
            W32(r, pl.Field("flags").Offset, p.Flags);
            WF(r, 8, p.Position.X); WF(r, 12, p.Position.Y); WF(r, 16, p.Position.Z);
            W16(r, 0x14, p.Bone);
            W16(r, 0x16, p.Texture);
            var geo = Encoding.ASCII.GetBytes(p.GeometryModel + "\0");
            if (p.GeometryModel.Length > 0) Arr(blob, r, pl.Array("geometry_model").Offset, geo.Length, geo);
            var rec = Encoding.ASCII.GetBytes(p.RecursionModel + "\0");
            if (p.RecursionModel.Length > 0) Arr(blob, r, pl.Array("recursion_model").Offset, rec.Length, rec);
            W16(r, 0x28, p.Blend);
            W16(r, 0x2A, p.EmitterType);
            W16(r, 0x2C, p.ParticleType);
            W16(r, 0x30, p.Rows);
            W16(r, 0x32, p.Cols);
            Track(blob, r, pl, "emission_speed", p.Speed);
            Track(blob, r, pl, "speed_variation", p.SpeedVariation);
            Track(blob, r, pl, "vertical_range", p.VerticalRange);
            Track(blob, r, pl, "horizontal_range", p.HorizontalRange);
            Track(blob, r, pl, "gravity", p.Gravity);
            Track(blob, r, pl, "lifespan", p.Lifespan);
            Track(blob, r, pl, "emission_rate", p.EmissionRate);
            Track(blob, r, pl, "emission_area_length", p.AreaLength);
            Track(blob, r, pl, "emission_area_width", p.AreaWidth);
            Track(blob, r, pl, "z_source", p.ZSource);
            WF(r, 0x14C, p.MidPoint);
            for (int k = 0; k < 3; k++)
            {
                var c = p.Colors[k];
                r[0x150 + 4 * k] = c.B; r[0x151 + 4 * k] = c.G; r[0x152 + 4 * k] = c.R; r[0x153 + 4 * k] = c.A;
                WF(r, 0x15C + 4 * k, p.Sizes[k]);
                W16(r, 0x168 + 2 * k, p.LifespanCells[k]);
                W16(r, 0x16E + 2 * k, p.DecayCells[k]);
            }
            for (int k = 0; k < 2; k++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(0x174 + 2 * k), p.TailCells[k]);
                BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(0x178 + 2 * k), p.TailDecayCells[k]);
            }
            for (int k = 0; k < 22; k++) WF(r, 0x17C + 4 * k, p.Params[k]);
            if (p.SplinePoints.Length > 0) Arr(blob, r, pl.Array("spline_points").Offset, p.SplinePoints.Length, F32s(p.SplinePoints.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray()));
            Track(blob, r, pl, "enabled", p.Enabled);
            r.CopyTo(prec, i * pl.Size);
        }
        Arr(blob, head, A("particles"), particles.Count, prec);
        // ribbons
        var rl = M2Schema.Ribbon;
        var rrec = new byte[ribbons.Count * rl.Size];
        for (int i = 0; i < ribbons.Count; i++)
        {
            var x = ribbons[i];
            var r = new byte[rl.Size];
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(0), -1);
            W32(r, 4, x.Bone);
            WF(r, 8, x.Position.X); WF(r, 12, x.Position.Y); WF(r, 16, x.Position.Z);
            Arr(blob, r, rl.Array("textures").Offset, x.Textures.Length, x.Textures.SelectMany(BitConverter.GetBytes).ToArray());
            Arr(blob, r, rl.Array("materials").Offset, x.Materials.Length, x.Materials.SelectMany(BitConverter.GetBytes).ToArray());
            Track(blob, r, rl, "color", x.Color);
            Track(blob, r, rl, "alpha", x.Alpha);
            Track(blob, r, rl, "height_above", x.HeightAbove);
            Track(blob, r, rl, "height_below", x.HeightBelow);
            WF(r, 0x94, x.EdgesPerSecond);
            WF(r, 0x98, x.EdgeLifetime);
            WF(r, 0x9C, x.Gravity);
            W16(r, 0xA0, x.Rows);
            W16(r, 0xA2, x.Cols);
            Track(blob, r, rl, "texture_slot", x.TextureSlot);
            Track(blob, r, rl, "visibility", x.Visibility);
            r.CopyTo(rrec, i * rl.Size);
        }
        Arr(blob, head, A("ribbons"), ribbons.Count, rrec);
        var all = blob.Data.ToArray();
        head.CopyTo(all, 0);
        return all;
    }
}
