using System.Buffers.Binary;
using System.Text;
using WoWData.Core.Imaging;
using WoWData.Core.Mpq;

namespace WoWData.Core.M2;

/// <summary>How a material draws its texture, grouped for plain-language advice.</summary>
public enum BlendKind { Opaque, AlphaKey, Alpha, Additive, Modulate, Other }

/// <summary>One material (or particle / ribbon emitter) of a model or building that draws a texture.</summary>
public sealed record TextureMaterialUse(string Model, BlendKind Kind, string BlendName, string Where);

/// <summary>
/// The blend modes with which models and buildings draw a texture: the M2 texture units of view 0 (render-flag entry
/// +2 = blend: 0 opaque, 1 alpha key, 2 alpha, 3 no-alpha add, 4 add, 5 mod, 6 mod2x, as M2Scene_SetBatchBlendState
/// @0070c190 reads it), the ribbon emitters (their materials are render-flag entries too), the particle emitters
/// (blending field +0x28: 0 opaque, 1 additive, 2 alpha, 3 alpha key, 4 additive with alpha) and the WMO materials
/// (MOMT +8, the Gx blend: 0 opaque, 1 alpha key, 2 alpha, 3 add, 4 mod, 5 mod2x). Used for the additive-texture
/// advice and for deciding whether colour under fully transparent pixels can be seen.
/// </summary>
public static class MaterialBlends
{
    public static BlendKind OfM2(int blend) => blend switch
    {
        0 => BlendKind.Opaque, 1 => BlendKind.AlphaKey, 2 => BlendKind.Alpha, 3 or 4 => BlendKind.Additive, 5 or 6 => BlendKind.Modulate, _ => BlendKind.Other,
    };

    public static string M2Name(int blend) => blend >= 0 && blend < M2Schema.BlendModes.Length ? M2Schema.BlendModes[blend] : $"blend {blend}";

    private static (BlendKind, string) OfParticle(int b) => b switch
    {
        0 => (BlendKind.Opaque, "opaque"), 1 => (BlendKind.Additive, "add"), 2 => (BlendKind.Alpha, "alpha"), 3 => (BlendKind.AlphaKey, "alpha key"),
        4 => (BlendKind.Additive, "add (alpha)"), _ => (BlendKind.Other, $"blend {b}"),
    };

    private static (BlendKind, string) OfWmo(int b) => b switch
    {
        0 => (BlendKind.Opaque, "opaque"), 1 => (BlendKind.AlphaKey, "alpha key"), 2 => (BlendKind.Alpha, "alpha"), 3 => (BlendKind.Additive, "add"),
        4 => (BlendKind.Modulate, "mod"), 5 => (BlendKind.Modulate, "mod2x"), _ => (BlendKind.Other, $"blend {b}"),
    };

    private static uint U32(byte[] b, long p) => p >= 0 && p + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)p)) : 0;
    private static ushort U16(byte[] b, long p) => p >= 0 && p + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p)) : (ushort)0;

    /// <summary>Every material of a model (.m2) or root building (.wmo) file that draws <paramref name="texture"/>. In an
    /// M2, a texture that is not named in the file (a creature / item skin) is matched to the replaceable texture slots.</summary>
    public static List<TextureMaterialUse> Find(string modelPath, byte[] data, string texture)
    {
        try
        {
            return modelPath.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) ? FindWmo(modelPath, data, texture) : FindM2(modelPath, data, texture);
        }
        catch (Exception e) when (e is M2Exception or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return new List<TextureMaterialUse>();
        }
    }

    private static List<TextureMaterialUse> FindM2(string model, byte[] d, string texture)
    {
        var res = new List<TextureMaterialUse>();
        var h = new M2Header(d);
        var tex = h.Textures();
        var want = ClientData.ResolveName(texture);
        var named = new HashSet<int>();
        for (int i = 0; i < tex.Count; i++)
            if (tex[i].Type == 0 && string.Equals(ClientData.ResolveName(tex[i].FileName), want, StringComparison.OrdinalIgnoreCase)) named.Add(i);
        if (named.Count == 0)
            for (int i = 0; i < tex.Count; i++)
                if (tex[i].Type != 0) named.Add(i);   // a skin: the replaceable slots
        if (named.Count == 0) return res;
        uint nRf = U32(d, 0x84), oRf = U32(d, 0x88);
        uint nLook = U32(d, 0x94), oLook = U32(d, 0x98);
        int RfBlend(int i) => i >= 0 && i < nRf ? U16(d, oRf + i * 4L + 2) : -1;
        // view 0's texture units
        uint nViews = U32(d, 0x4c), oViews = U32(d, 0x50);
        var seen = new HashSet<(int, string)>();
        if (nViews > 0)
        {
            uint nTu = U32(d, oViews + 0x20), oTu = U32(d, oViews + 0x24);
            for (uint k = 0; k < nTu && k < 4096; k++)
            {
                long u = oTu + k * 0x18L;
                if (u + 0x18 > d.Length) break;
                int rf = U16(d, u + 0xa), count = Math.Max(1, (int)U16(d, u + 0xe)), look = U16(d, u + 0x10);
                for (int layer = 0; layer < Math.Min(count, 2); layer++)
                {
                    int li = look + layer;
                    if (li >= nLook) continue;
                    int ti = U16(d, oLook + li * 2L);
                    if (!named.Contains(ti)) continue;
                    int blend = RfBlend(rf);
                    if (blend < 0) continue;
                    var where = layer == 0 ? "material" : "second texture layer";
                    if (seen.Add((blend, where))) res.Add(new TextureMaterialUse(model, OfM2(blend), M2Name(blend), where));
                }
            }
        }
        // ribbons: textures (indices into the texture table) + materials (render-flag entries)
        uint nRib = U32(d, 0x134), oRib = U32(d, 0x138);
        for (uint r = 0; r < nRib && r < 512; r++)
        {
            long rb = oRib + r * 0xdcL;
            uint nT = U32(d, rb + 0x14), oT = U32(d, rb + 0x18), nM = U32(d, rb + 0x1c), oM = U32(d, rb + 0x20);
            bool uses = false;
            for (uint i = 0; i < nT && i < 16; i++) if (named.Contains(U16(d, oT + i * 2L))) uses = true;
            if (!uses) continue;
            for (uint i = 0; i < Math.Max(1u, Math.Min(nM, 16u)); i++)
            {
                int blend = nM == 0 ? 0 : RfBlend(U16(d, oM + i * 2L));
                if (blend >= 0 && seen.Add((blend + 100, "ribbon"))) res.Add(new TextureMaterialUse(model, OfM2(blend), M2Name(blend), "ribbon"));
            }
        }
        // particles: texture index +0x16, blending +0x28
        uint nPart = U32(d, 0x13c), oPart = U32(d, 0x140);
        for (uint p = 0; p < nPart && p < 1024; p++)
        {
            long pb = oPart + p * 0x1f8L;
            if (pb + 0x2a > d.Length) break;
            if (!named.Contains(U16(d, pb + 0x16))) continue;
            var (kind, name) = OfParticle(U16(d, pb + 0x28));
            if (seen.Add(((int)kind + 200, "particle " + name))) res.Add(new TextureMaterialUse(model, kind, name, "particle"));
        }
        return res;
    }

    private static List<TextureMaterialUse> FindWmo(string model, byte[] d, string texture)
    {
        var res = new List<TextureMaterialUse>();
        var want = ClientData.ResolveName(texture);
        int motx = -1, motxSize = 0, momt = -1, momtSize = 0;
        int p = 0;
        while (p + 8 <= d.Length)
        {
            var id = Encoding.ASCII.GetString(d, p, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            if (size < 0 || p + 8 + size > d.Length) break;
            if (id == "XTOM") { motx = p + 8; motxSize = size; }
            else if (id == "TMOM") { momt = p + 8; momtSize = size; }
            p += 8 + size;
        }
        if (motx < 0 || momt < 0) return res;
        string Name(uint ofs)
        {
            if (ofs >= motxSize) return "";
            int s = motx + (int)ofs, e = s;
            while (e < motx + motxSize && d[e] != 0) e++;
            return Encoding.Latin1.GetString(d, s, e - s);
        }
        var seen = new HashSet<BlendKind>();
        for (int m = 0; m + 0x40 <= momtSize; m += 0x40)
        {
            int b = momt + m;
            bool t1 = string.Equals(ClientData.ResolveName(Name(U32(d, b + 0xc))), want, StringComparison.OrdinalIgnoreCase);
            bool t2 = string.Equals(ClientData.ResolveName(Name(U32(d, b + 0x18))), want, StringComparison.OrdinalIgnoreCase);
            if (!t1 && !t2) continue;
            var (kind, name) = OfWmo((int)U32(d, b + 8));
            if (seen.Add(kind)) res.Add(new TextureMaterialUse(model, kind, name, t1 ? "building material" : "building material (second texture)"));
        }
        return res;
    }

    /// <summary>The materials of all <paramref name="models"/> that draw a texture (each model read with <paramref name="read"/>).</summary>
    public static List<TextureMaterialUse> ForTexture(string texture, IEnumerable<string> models, Func<string, byte[]?> read, int maxModels = 80)
    {
        var res = new List<TextureMaterialUse>();
        foreach (var m in models.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxModels))
        {
            if (!m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) && !m.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase)) continue;
            byte[]? data;
            try { data = read(m); }
            catch (Exception e) when (e is IOException or MpqException) { data = null; }
            if (data != null) res.AddRange(Find(m, data, texture));
        }
        return res;
    }

    public static bool IsGlowLike(BlendKind k) => k is BlendKind.Additive or BlendKind.Modulate;

    /// <summary>The plain-language note for a texture drawn additively (or multiplied), or null.</summary>
    public static string? AdditiveNote(IReadOnlyCollection<TextureMaterialUse> uses)
    {
        var glow = uses.Where(u => IsGlowLike(u.Kind)).ToList();
        if (glow.Count == 0) return null;
        var models = glow.Select(u => Path.GetFileName(u.Model)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool add = glow.Any(u => u.Kind == BlendKind.Additive);
        var head = add
            ? "This texture is drawn additively: black is invisible, bright colours glow. Tinting the black background will show as a visible square."
            : "This texture is drawn as a multiply (mod / mod2x) layer: it darkens or brightens what is behind it, and mid-grey (mod2x) or white (mod) leaves it unchanged. Tinting its background will show as a coloured square.";
        return head + $" (Used this way by {string.Join(", ", models.Take(4))}{(models.Count > 4 ? $" and {models.Count - 4} more" : "")}: " +
               string.Join(", ", glow.Select(u => $"{u.Where} {u.BlendName}").Distinct().Take(4)) + ".)";
    }

    /// <summary>Pixels that are near black in the original (largest channel at most <paramref name="threshold"/>) and that
    /// your edit made brighter: the background an additive texture hides.</summary>
    public static int LiftedBlackPixels(RgbaImage original, RgbaImage edited, int threshold = 8)
    {
        if (original.Width != edited.Width || original.Height != edited.Height) return 0;
        int n = 0;
        var o = original.Pixels;
        var e = edited.Pixels;
        for (int i = 0; i < o.Length; i += 4)
        {
            int mo = Math.Max(o[i], Math.Max(o[i + 1], o[i + 2]));
            int me = Math.Max(e[i], Math.Max(e[i + 1], e[i + 2]));
            if (mo <= threshold && me > mo + 4) n++;
        }
        return n;
    }

    /// <summary>"Keep black areas black": your edit, faded back to the original where the original is dark (the original
    /// itself up to a brightness of <paramref name="black"/>, fully your edit from <paramref name="black"/> +
    /// <paramref name="fade"/>), so a tint keeps the invisible background of an additive texture black. Alpha is your edit's.</summary>
    public static RgbaImage KeepBlackBlack(RgbaImage original, RgbaImage edited, int black = 8, int fade = 32)
    {
        if (original.Width != edited.Width || original.Height != edited.Height) return edited;
        var res = edited.Clone();
        var o = original.Pixels;
        var r = res.Pixels;
        for (int i = 0; i < o.Length; i += 4)
        {
            int mo = Math.Max(o[i], Math.Max(o[i + 1], o[i + 2]));
            if (mo >= black + fade) continue;
            double w = mo <= black ? 0 : (mo - black) / (double)fade;
            for (int c = 0; c < 3; c++) r[i + c] = (byte)Math.Clamp((int)Math.Round(o[i + c] + (r[i + c] - o[i + c]) * w), 0, 255);
        }
        return res;
    }
}
