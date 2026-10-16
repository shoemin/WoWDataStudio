using System.Numerics;
using WoWData.Core.Imaging;
using WoWData.Core.M2;

namespace IngameRunner;

/// <summary>A test-only picture of an M2's rest pose (view 0 triangles, flat shaded, z-buffered), side and top views next to each other,
/// for the gallery's "mesh before / after" (the app's own renderer needs a GL context and reads the archives only).</summary>
public static class MeshPicture
{
    public static RgbaImage Render(M2Model m, (Vector3 Min, Vector3 Max) box, int size = 360)
    {
        var img = new RgbaImage(size * 2, size);
        for (int i = 0; i < img.Pixels.Length; i += 4) { img.Pixels[i] = 34; img.Pixels[i + 1] = 38; img.Pixels[i + 2] = 46; img.Pixels[i + 3] = 255; }
        var tris = m.Views[0].Triangles;
        var idx = m.Views[0].Indices;
        float ext = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z)) * 1.08f;
        var c = (box.Min + box.Max) / 2;
        var light = Vector3.Normalize(new Vector3(0.4f, -0.6f, 0.7f));
        for (int view = 0; view < 2; view++)
        {
            var zbuf = new float[size * size];
            Array.Fill(zbuf, float.MinValue);
            // view 0: side (looking along -Y: x right, z up); view 1: top (x right, y up)
            Vector3 P(Vector3 v) => view == 0 ? new((v.X - c.X) / ext, (v.Z - c.Z) / ext, -(v.Y - c.Y)) : new((v.X - c.X) / ext, (v.Y - c.Y) / ext, v.Z - c.Z);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                var a = m.Vertices[idx[tris[t]]].Position; var b = m.Vertices[idx[tris[t + 1]]].Position; var d = m.Vertices[idx[tris[t + 2]]].Position;
                var n = Vector3.Cross(b - a, d - a);
                if (n.LengthSquared() < 1e-12f) continue;
                n = Vector3.Normalize(n);
                var vn = view == 0 ? new Vector3(n.X, n.Z, -n.Y) : n;
                float shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(vn, light));
                var pa = P(a); var pb = P(b); var pd = P(d);
                Fill(img, zbuf, size, view * size, pa, pb, pd, shade);
            }
        }
        return img;
    }

    private static void Fill(RgbaImage img, float[] zbuf, int size, int ox, Vector3 a, Vector3 b, Vector3 c, float shade)
    {
        Vector2 S(Vector3 p) => new((p.X + 0.5f) * size, (0.5f - p.Y) * size);
        var A = S(a); var B = S(b); var C = S(c);
        int x0 = (int)Math.Max(0, Math.Floor(Math.Min(A.X, Math.Min(B.X, C.X)))), x1 = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(A.X, Math.Max(B.X, C.X))));
        int y0 = (int)Math.Max(0, Math.Floor(Math.Min(A.Y, Math.Min(B.Y, C.Y)))), y1 = (int)Math.Min(size - 1, Math.Ceiling(Math.Max(A.Y, Math.Max(B.Y, C.Y))));
        float area = (B.X - A.X) * (C.Y - A.Y) - (B.Y - A.Y) * (C.X - A.X);
        if (Math.Abs(area) < 1e-6f) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((B.X - px) * (C.Y - py) - (B.Y - py) * (C.X - px)) / area;
                float w1 = ((C.X - px) * (A.Y - py) - (C.Y - py) * (A.X - px)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                if (z <= zbuf[y * size + x]) continue;
                zbuf[y * size + x] = z;
                int o = img.Offset(ox + x, y);
                img.Pixels[o] = (byte)(210 * shade); img.Pixels[o + 1] = (byte)(190 * shade); img.Pixels[o + 2] = (byte)(150 * shade);
            }
    }
}
