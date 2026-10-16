using System.Buffers.Binary;

namespace WoWData.Core.M2;

public sealed record M2Issue(string Level, string Where, string Message)
{
    public override string ToString() => $"{Level} {Where}: {Message}";
}

/// <summary>
/// M2 checks (port of wowdata/m2/validate.py): the client's own bounds rules (enforced by the parser) plus the
/// cross-reference and sanity checks the client does not make. Levels: "error" (the client would read out of bounds
/// or use a missing record), "warning" (legal but unusual), "note" (harmless oddities of Blizzard's own files).
/// </summary>
public static class M2Validator
{
    private const int None16 = 0xFFFF;

    public static List<M2Issue> Validate(M2Model m)
    {
        var o = new List<M2Issue>();
        void Err(string w, string s) => o.Add(new("error", w, s));
        void Warn(string w, string s) => o.Add(new("warning", w, s));
        void Note(string w, string s) => o.Add(new("note", w, s));

        var f = m.File;
        int nseq = m.Sequences.Length, nbones = m.Bones.Length, nverts = m.Vertices.Length, ngs = m.GlobalSequences.Length, ntex = m.Textures.Length;

        void Idx(string where, IEnumerable<long> values, int limit, bool allowNone = false, string what = "index", bool sixteen = true)
        {
            int bad = 0;
            long first = 0;
            foreach (var raw in values)
            {
                long v = sixteen ? raw & 0xFFFF : raw;
                if (allowNone && v == None16) continue;
                if (v < 0 || v >= limit)
                {
                    if (bad == 0) first = v;
                    bad++;
                }
            }
            if (bad > 0) Err(where, $"{bad} {what}(es) out of range 0..{limit - 1} (e.g. {first})");
        }

        void Track(string where, M2TrackView t)
        {
            int n = t.KeyCount;
            bool exclusive = t.Values == null;
            var vals = t.Values;
            if (vals != null && vals.Count != n) Err(where, $"{vals.Count} values for {n} timestamps");
            int interp = t.Interpolation;
            if (interp > 3) Err(where, $"interpolation type {interp}");
            else if (interp >= 2)
            {
                bool camera = vals != null && t.Field.Value is { Type: "3f4" or "9f4" } && where.StartsWith("cameras", StringComparison.Ordinal);
                if (!camera) Warn(where, $"interpolation type {interp} outside a camera track (the client lerps it)");
            }
            int gs = t.GlobalSequence;
            if (gs != -1 && !(gs >= 0 && gs < ngs)) Err(where, $"global sequence {gs} of {ngs}");
            var r = t.RangePairs();
            if (r.Length > 0)
            {
                if (gs == -1 && r.Length < nseq) Err(where, $"{r.Length} ranges for {nseq} sequences: the client reads ranges[sequence] past the end");
                else if (gs == -1 && r.Length > nseq) Note(where, $"{r.Length} ranges for {nseq} sequences (the extra ones are never read)");
                var used = gs == -1 ? r.Take(nseq) : r;
                if (n > 0 && used.Any(p => p.First < p.Last && p.Last > (exclusive ? (uint)n : (uint)(n - 1)))) Err(where, $"a key range ends past the {n} keys");
                if (n == 0 && r.Any(p => p.First != 0 || p.Last != 0)) Warn(where, "ranges without keys");
            }
            if (n > 1)
            {
                var ts = t.Timestamps.AsU32();
                if (gs != -1 || r.Length == 0)
                {
                    for (int i = 1; i < ts.Length; i++)
                        if (ts[i] < ts[i - 1]) { Warn(where, "timestamps not sorted"); break; }
                }
                else
                {
                    foreach (var (a, b) in r)
                    {
                        if (b <= a) continue;
                        bool unsorted = false;
                        for (long i = a + 1; i <= b && i < ts.Length; i++)
                            if (ts[i] < ts[i - 1]) { unsorted = true; break; }
                        if (b >= ts.Length && !unsorted)
                        {
                            // numpy slicing clamps; the check only covers the keys that exist
                        }
                        if (unsorted)
                        {
                            Warn(where, $"timestamps not sorted inside range {a}..{b}");
                            break;
                        }
                    }
                }
            }
            if (vals != null && vals.Count > 0 && t.Field.Value is { } ve && M2Elem.Shape(ve.Type).Kind == 'f')
            {
                var fl = vals.AsF32();
                if (fl.Any(x => !float.IsFinite(x))) Err(where, "non-finite values");
            }
        }

        void TracksOf(string name, M2Data arr)
        {
            for (int i = 0; i < arr.Count; i++)
                foreach (var t in arr.Tracks(i))
                    Track($"{name}[{i}].{t.Name}", t);
        }

        // ---------------------------------------------------------------- sequences
        for (int i = 0; i < nseq; i++)
        {
            var s = m.Sequences[i];
            if (s.End < s.Start) Err($"sequences[{i}]", $"end {s.End} before start {s.Start}");
            int nv = s.NextVariant;
            if (nv != -1 && !(nv >= 0 && nv < nseq)) Err($"sequences[{i}]", $"next_variant {nv} of {nseq}");
        }
        Idx("animation_lookup", m.AnimationLookup.Select(x => (long)x), nseq, true, "sequence");
        var pl = f["playable_lookup"];
        for (int i = 0; i < pl.Count; i++)
            if (pl.I16(i, "fallback_id") < 0) { Warn("playable_lookup", "negative fallback animation ids"); break; }

        // ---------------------------------------------------------------- bones
        if (nbones > 0)
        {
            for (int i = 0; i < nbones; i++)
            {
                int p = m.Bones[i].Parent;
                if (p != -1 && !(p >= 0 && p < nbones)) Err($"bones[{i}]", $"parent {p} of {nbones}");
                else if (p >= i) Warn($"bones[{i}]", $"parent {p} does not come before the bone");
            }
            if (m.Bones.Any(b => b.KeyBoneId < -1 || b.KeyBoneId > 26)) Warn("bones", "key bone ids outside -1..26");
            if (m.Bones.Any(b => !float.IsFinite(b.Pivot.X) || !float.IsFinite(b.Pivot.Y) || !float.IsFinite(b.Pivot.Z))) Err("bones", "non-finite pivots");
            TracksOf("bones", f["bones"]);
            for (int i = 0; i < nbones; i++)
            {
                var q = m.Bones[i].Rotation.Values;
                if (q.Length == 0) continue;
                double mn = double.MaxValue, mx = double.MinValue;
                bool bad = false;
                foreach (var v in q)
                {
                    double len = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z + (double)v.W * v.W);
                    mn = Math.Min(mn, len);
                    mx = Math.Max(mx, len);
                    if (Math.Abs(len - 1) > 0.05) bad = true;
                }
                if (bad) Warn($"bones[{i}].rotation", $"quaternions not normalised (|q| {mn:0.000}..{mx:0.000})");
            }
        }
        Idx("key_bone_lookup", m.KeyBoneLookup.Select(x => (long)x), Math.Max(nbones, 1), true, "bone");
        if (m.KeyBoneLookup.Length > 0 && nbones == 0) Err("key_bone_lookup", "entries but no bones");

        // ---------------------------------------------------------------- vertices
        if (nverts > 0)
        {
            var v = m.Vertices;
            if (v.Any(x => !Finite(x.Position.X) || !Finite(x.Position.Y) || !Finite(x.Position.Z) || !Finite(x.Normal.X) || !Finite(x.Normal.Y) ||
                           !Finite(x.Normal.Z) || !Finite(x.Uv.X) || !Finite(x.Uv.Y)))
                Err("vertices", "non-finite position / normal / uv");
            var sums = v.Select(x => x.W0 + x.W1 + x.W2 + x.W3).ToArray();
            int badSums = sums.Count(s => s != 255);
            if (badSums > 0)
                Warn("vertices", $"{badSums} vertices whose weights do not sum to 255 ({string.Join(", ", sums.Where(s => s != 255).Distinct().OrderBy(s => s).Take(5))})");
            int maxBone = -1;
            foreach (var x in v)
            {
                if (x.W0 > 0) maxBone = Math.Max(maxBone, x.B0);
                if (x.W1 > 0) maxBone = Math.Max(maxBone, x.B1);
                if (x.W2 > 0) maxBone = Math.Max(maxBone, x.B2);
                if (x.W3 > 0) maxBone = Math.Max(maxBone, x.B3);
            }
            if (nbones > 0 && maxBone >= nbones) Err("vertices", $"bone index {maxBone} of {nbones} bones");
        }
        if (nverts > 0xFFFF) Err("vertices", $"{nverts} vertices: views index them with uint16");

        // ---------------------------------------------------------------- views
        int nbl = m.BoneLookup.Length, nrf = m.RenderFlags.Length, ntl = m.TextureLookup.Length;
        for (int vi = 0; vi < m.Views.Length; vi++)
        {
            var view = m.Views[vi];
            string where = $"views[{vi}]";
            var ind = view.Indices;
            var tri = view.Triangles;
            int nprops = view.Properties.Length / 4;
            Idx(where + ".indices", ind.Select(x => (long)x), Math.Max(nverts, 1), what: "vertex");
            if (tri.Length % 3 != 0) Err(where + ".triangles", $"{tri.Length} indices is not a multiple of 3");
            Idx(where + ".triangles", tri.Select(x => (long)x), Math.Max(ind.Length, 1), what: "view vertex");
            if (nprops != ind.Length) Err(where + ".properties", $"{nprops} entries for {ind.Length} view vertices");
            var subs = view.Submeshes;
            for (int si = 0; si < subs.Length; si++)
            {
                var s = subs[si];
                string w2 = $"{where}.submeshes[{si}]";
                int vs = s.VertexStart, vc = s.VertexCount, ts = s.IndexStart, tc = s.IndexCount;
                if (vs + vc > ind.Length) Err(w2, $"vertex range {vs}+{vc} past {ind.Length} view vertices");
                if (ts + tc > tri.Length) Err(w2, $"index range {ts}+{tc} past {tri.Length} triangle indices");
                if (tc % 3 != 0) Err(w2, $"index count {tc} not a multiple of 3");
                if (s.BoneStart + s.BoneCount > nbl) Err(w2, $"bone palette {s.BoneStart}+{s.BoneCount} past {nbl} bone_lookup entries");
                if (s.Level != 0) Warn(w2, $"level {s.Level}");
                if (tc > 0 && ts + tc <= tri.Length)
                {
                    int mn = int.MaxValue, mx = int.MinValue;
                    for (int k = ts; k < ts + tc; k++)
                    {
                        mn = Math.Min(mn, tri[k]);
                        mx = Math.Max(mx, tri[k]);
                    }
                    if (mn < vs || mx >= vs + vc) Warn(w2, "triangles use view vertices outside the submesh vertex range");
                }
                if (vc > 0 && vs + vc <= nprops && s.BoneCount > 0 && nverts > 0)
                {
                    bool bad = false;
                    for (int k = vs; k < vs + vc && !bad; k++)
                    {
                        int mv = ind[k];
                        if (mv >= nverts) continue;
                        var vx = m.Vertices[mv];
                        if ((vx.W0 > 0 && view.Properties[k * 4] >= s.BoneCount) || (vx.W1 > 0 && view.Properties[k * 4 + 1] >= s.BoneCount) ||
                            (vx.W2 > 0 && view.Properties[k * 4 + 2] >= s.BoneCount) || (vx.W3 > 0 && view.Properties[k * 4 + 3] >= s.BoneCount)) bad = true;
                    }
                    if (bad) Err(w2, "vertex properties index past the submesh bone palette");
                }
            }
            var tus = view.TexUnits;
            for (int ti = 0; ti < tus.Length; ti++)
            {
                var t = tus[ti];
                string w2 = $"{where}.texture_units[{ti}]";
                if (t.Submesh >= subs.Length) Err(w2, $"submesh {t.Submesh} of {subs.Length}");
                if (t.RenderFlags >= nrf) Err(w2, $"render flags {t.RenderFlags} of {nrf}");
                if (t.TextureLookup + Math.Max(t.TextureCount, 1) > ntl) Err(w2, $"texture lookup {t.TextureLookup}+{t.TextureCount} past {ntl}");
                if (t.TextureCount > 2) Warn(w2, $"{t.TextureCount} texture layers (the client supports 2)");
                int c = t.Color;
                if (c != -1 && !(c >= 0 && c < m.Colors.Length)) Err(w2, $"color {c} of {m.Colors.Length}");
                if (t.TransparencyLookup >= m.TransparencyLookup.Length) Err(w2, $"transparency lookup {t.TransparencyLookup} of {m.TransparencyLookup.Length}");
                int ta = t.TextureTransformLookup & 0xFFFF;
                if (ta != None16 && ta >= m.TextureTransformLookup.Length) Err(w2, $"texture transform lookup {ta} of {m.TextureTransformLookup.Length}");
                int tl = t.TexUnitLookup & 0xFFFF;
                if (tl != None16 && tl >= m.TexUnitLookup.Length) Err(w2, $"tex-unit lookup {tl} of {m.TexUnitLookup.Length}");
            }
        }
        if (m.Views.Length == 0) Warn("views", "no views (nothing to draw)");
        Idx("bone_lookup", m.BoneLookup.Select(x => (long)x), Math.Max(nbones, 1), what: "bone");
        Idx("texture_lookup", m.TextureLookup.Select(x => (long)x), Math.Max(ntex, 1), true, "texture");
        Idx("transparency_lookup", m.TransparencyLookup.Select(x => (long)x), Math.Max(m.Transparency.Length, 1), true, "transparency");
        Idx("texture_transform_lookup", m.TextureTransformLookup.Select(x => (long)x), Math.Max(m.TextureTransforms.Length, 1), true, "transform");
        Idx("replaceable_lookup", m.ReplaceableLookup.Select(x => (long)x), Math.Max(ntex, 1), true, "texture");
        if (m.RenderFlags.Any(r => r.Blend > 6)) Err("render_flags", "blend mode > 6");

        // ---------------------------------------------------------------- textures, animated values
        for (int i = 0; i < ntex; i++)
            if (m.Textures[i].Type == 0 && m.Textures[i].FileName.Length == 0) Warn($"textures[{i}]", "type 0 (file) without a file name");
        TracksOf("colors", f["colors"]);
        TracksOf("transparency", f["transparency"]);
        TracksOf("track_6c", f["track_6c"]);
        TracksOf("texture_transforms", f["texture_transforms"]);

        // ---------------------------------------------------------------- collision
        var ct = f["collision_triangles"].AsU16();
        int ncv = f["collision_vertices"].Count;
        if (ct.Length % 3 != 0) Err("collision_triangles", $"{ct.Length} indices is not a multiple of 3");
        Idx("collision_triangles", ct.Select(x => (long)x), Math.Max(ncv, 1), what: "collision vertex");
        int ncn = f["collision_normals"].Count;
        if (ncn > 0 && ncn != ct.Length / 3) Warn("collision_normals", $"{ncn} normals for {ct.Length / 3} triangles");

        // ---------------------------------------------------------------- attachments, events, lights, cameras, emitters
        foreach (var name in new[] { "attachments", "events", "lights", "ribbons", "particles" })
        {
            var arr = f[name];
            if (arr.Count > 0 && nbones > 0)
            {
                var bf = arr.Rec.Field("bone");
                for (int i = 0; i < arr.Count; i++)
                {
                    long b = bf.Type switch
                    {
                        "u2" => arr.U16(i, "bone"),
                        "i2" => arr.I16(i, "bone"),
                        _ => arr.U32(i, "bone"),
                    };
                    if (name == "lights" && b == -1) continue;
                    if (b < 0 || b >= nbones)
                    {
                        Err(name, $"bone {b} of {nbones}");
                        break;
                    }
                }
            }
            TracksOf(name, arr);
        }
        Idx("attachment_lookup", f["attachment_lookup"].AsI16().Select(x => (long)x), Math.Max(m.Attachments.Length, 1), true, "attachment");
        TracksOf("cameras", f["cameras"]);
        Idx("camera_lookup", f["camera_lookup"].AsI16().Select(x => (long)x), Math.Max(m.CameraCount, 1), true, "camera");
        var parts = f["particles"];
        for (int i = 0; i < parts.Count; i++)
        {
            int tex = parts.U16(i, "texture");
            if (tex >= Math.Max(ntex, 1)) Err($"particles[{i}]", $"texture {tex} of {ntex}");
        }
        var ribs = f["ribbons"];
        for (int i = 0; i < ribs.Count; i++)
        {
            Idx($"ribbons[{i}].textures", ribs.Child(i, "textures").AsU16().Select(x => (long)x), Math.Max(ntex, 1), what: "texture");
            Idx($"ribbons[{i}].materials", ribs.Child(i, "materials").AsU16().Select(x => (long)x), Math.Max(nrf, 1), what: "render flags");
        }

        // ---------------------------------------------------------------- bounds
        foreach (var k in new[] { "bounds_min", "bounds_max", "collision_min", "collision_max" })
        {
            var v = f.Header.Vec3(0, k);
            if (!Finite(v.X) || !Finite(v.Y) || !Finite(v.Z)) Err(k, "not finite");
        }
        return o;
    }

    private static bool Finite(float x) => float.IsFinite(x);

    public static List<M2Issue> Errors(IEnumerable<M2Issue> issues) => issues.Where(i => i.Level == "error").ToList();
}
