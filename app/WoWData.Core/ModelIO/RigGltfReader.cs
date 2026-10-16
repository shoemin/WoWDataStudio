using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WoWData.Core.ModelIO;

/// <summary>
/// 0.10.0: reads the skeleton, animations and attachment / event empties straight from a glTF file (the export package
/// itself, or a glTF without the Blender add-on's rig file). Joints are the bones (wow_bone extras or their names), the
/// animations are sampled at their key times: the bone's animation matrix is its joint's world transform at that time
/// relative to its rest transform, taken relative to its parent's (so bone orientations a tool adds do not matter), then
/// converted from glTF axes to M2 axes.
/// </summary>
public static class RigGltfReader
{
    private static readonly Regex AttName = new(@"^ATT_(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex EvtName = new(@"^EVT_\d+_(.{1,4})", RegexOptions.CultureInvariant);

    /// <summary>M2 -> glTF axes (row-vector): (x, y, z) -> (x, z, -y).</summary>
    private static readonly Matrix4x4 C = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 CInv = Matrix4x4.Transpose(C);

    public static EditedRig Read(GltfFile g, int root, ModelPackageManifest man)
    {
        var rig = new EditedRig { Source = "the glTF file's skeleton and animations" };
        var jointSet = new HashSet<int>();
        foreach (var sk in g.Arr("skins"))
            foreach (var j in sk!["joints"]!.AsArray())
                if (GltfFile.IntOf(j) is int ji) jointSet.Add(ji);
        // node -> transform relative to the model's root node (the root's own transform is left out)
        Matrix4x4 RelWorld(int node, Func<int, Matrix4x4> local)
        {
            var mm = local(node);
            int guard = 0;
            for (int p = g.Parent(node); p >= 0 && p != root && guard < 1000; p = g.Parent(p), guard++) mm *= local(p);
            return mm;
        }
        Matrix4x4 Rest(int n) => g.Local(n);
        Vector3 ToM2(Vector3 v) => ModelExporter.FromGltf(v);
        var names = new Dictionary<int, string>();
        foreach (int j in jointSet.OrderBy(x => x))
        {
            var n = g.Node(j);
            string name = GltfFile.StrOf(n["name"]) ?? $"joint_{j}";
            names[j] = name;
            int p = g.Parent(j);
            rig.Bones.Add(new EditedBone
            {
                Name = name, WowBone = GltfFile.IntOf(GltfFile.Extras(n)?["wow_bone"]), Parent = p >= 0 && jointSet.Contains(p) ? GltfFile.StrOf(g.Node(p)["name"]) : null,
                Head = ToM2(RelWorld(j, Rest).Translation), KeyBoneId = GltfFile.IntOf(GltfFile.Extras(n)?["wow_key_bone_id"]),
                Flags = GltfFile.IntOf(GltfFile.Extras(n)?["wow_flags"]) is int fl ? (uint)fl : null,
            });
        }
        // animations
        foreach (var an in g.Arr("animations"))
        {
            var a = an!.AsObject();
            var ex = GltfFile.Extras(a);
            var act = new EditedAction
            {
                Name = GltfFile.StrOf(a["name"]) ?? "", WowSequence = GltfFile.IntOf(ex?["wow_sequence"]), GlobalSequence = GltfFile.IntOf(ex?["wow_global_sequence"]),
            };
            if (ex != null)
                foreach (var kv in ex)
                    if (kv.Key.StartsWith("wow_", StringComparison.Ordinal)) act.Props[kv.Key[4..]] = kv.Value?.DeepClone();
            var samplers = a["samplers"]!.AsArray();
            // node -> path -> (times, values, interpolation)
            var chans = new Dictionary<int, Dictionary<string, (double[] T, double[] V, int Comps, string Interp)>>();
            var allTimes = new SortedSet<double>();
            foreach (var ch in a["channels"]!.AsArray())
            {
                int node = GltfFile.IntOf(ch!["target"]?["node"]) ?? -1;
                string path = GltfFile.StrOf(ch["target"]?["path"]) ?? "";
                if (node < 0 || !jointSet.Contains(node) || path is not ("translation" or "rotation" or "scale")) continue;
                var s = samplers[GltfFile.IntOf(ch["sampler"]) ?? 0]!.AsObject();
                var t = g.Read(GltfFile.IntOf(s["input"])!.Value, out _);
                var v = g.Read(GltfFile.IntOf(s["output"])!.Value, out int comps);
                string interp = GltfFile.StrOf(s["interpolation"]) ?? "LINEAR";
                if (!chans.TryGetValue(node, out var d)) chans[node] = d = new();
                d[path] = (t, v, comps, interp);
                foreach (var x in t) allTimes.Add(x);
            }
            double durMs = (double?)(ex?["wow_duration_ms"] as JsonValue)?.GetValue<double>() ?? (allTimes.Count > 0 ? allTimes.Max * 1000 : 0);
            act.DurationMs = durMs;
            var times = allTimes.Where(x => x * 1000 <= durMs + 0.5).ToList();
            if (times.Count == 0 || times[0] > 0) times.Insert(0, 0);
            // the local transform of a node at a time (its animated channels, else its rest values)
            Matrix4x4 LocalAt(int node, double t)
            {
                if (!chans.TryGetValue(node, out var d)) return g.Local(node);
                var n = g.Node(node);
                var tr = d.TryGetValue("translation", out var tc) ? Vec3(Sample(tc, t)) : NodeVec(n["translation"], 3, 0);
                var ro = d.TryGetValue("rotation", out var rc) ? Sample(rc, t) : NodeVec4(n["rotation"]);
                var sc = d.TryGetValue("scale", out var sc2) ? Vec3(Sample(sc2, t)) : NodeVec(n["scale"], 3, 1);
                var q = Quaternion.Normalize(new Quaternion((float)ro[0], (float)ro[1], (float)ro[2], (float)ro[3]));
                return Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(tr);
            }
            foreach (var (node, d) in chans)
            {
                var bs = new BoneSamples();
                foreach (var p in d.Keys) bs.Channels.Add(p);
                // L = inverse(parent rest) * inverse(own rest local) * own local(t) * parent rest: only rest matrices are
                // inverted (a parent scaled to 0 in some frames would make its animated matrix singular)
                int parent = g.Parent(node);
                bool parentIsJoint = parent >= 0 && jointSet.Contains(parent);
                var parentRest = parentIsJoint ? RelWorld(parent, Rest) : Matrix4x4.Identity;
                Matrix4x4.Invert(parentRest, out var parentRestInv);
                Matrix4x4.Invert(g.Local(node), out var localRestInv);
                foreach (var t in times)
                {
                    var l = parentRestInv * localRestInv * LocalAt(node, t) * parentRest;
                    bs.TimesMs.Add(t * 1000);
                    bs.Local.Add(C * l * CInv);
                }
                act.Bones[names[node]] = bs;
            }
            if (act.GlobalSequence != null) rig.Globals.Add(act);
            else rig.Actions.Add(act);
        }
        // the events of the manifest (markers are not part of a glTF file)
        if (man.Rig != null)
            foreach (var e in man.Rig.Events)
                foreach (var tr in e.Triggers)
                {
                    var s = man.Rig.Sequences.FirstOrDefault(x => x.Index == tr[0]);
                    var act = s == null ? null : rig.Actions.FirstOrDefault(x => x.WowSequence == s.Index);
                    act?.Markers.Add((e.Marker, tr[1]));
                }
        // attachment and event empties
        for (int i = 0; i < g.NodeCount; i++)
        {
            var n = g.Node(i);
            var name = GltfFile.StrOf(n["name"]) ?? "";
            var ex = GltfFile.Extras(n);
            bool att = ex?["wow_attachment"] != null || AttName.IsMatch(name);
            bool evt = ex?["wow_event"] != null || name.StartsWith("EVT_", StringComparison.Ordinal);
            if (!att && !evt) continue;
            int p = g.Parent(i);
            var pt = new EditedPoint
            {
                Name = name, Kind = att ? "attachment" : "event", WowIndex = GltfFile.IntOf(ex?[att ? "wow_attachment" : "wow_event"]),
                Bone = p >= 0 && jointSet.Contains(p) ? names.GetValueOrDefault(p) : null, Position = ToM2(RelWorld(i, Rest).Translation),
            };
            if (att) pt.Id = GltfFile.IntOf(ex?["wow_attachment_id"]) ?? (AttName.Match(name) is { Success: true } mm ? int.Parse(mm.Groups[1].Value) : null);
            else pt.Code = GltfFile.StrOf(ex?["wow_event_code"]) ?? (EvtName.Match(name) is { Success: true } me ? me.Groups[1].Value : null);
            rig.Points.Add(pt);
        }
        return rig;
    }

    private static double[] Sample((double[] T, double[] V, int Comps, string Interp) c, double t)
    {
        int n = c.T.Length;
        int k = c.Comps;
        bool cubic = c.Interp == "CUBICSPLINE";
        double[] Val(int i) => cubic ? c.V.Skip((i * 3 + 1) * k).Take(k).ToArray() : c.V.Skip(i * k).Take(k).ToArray();
        if (n == 0) return new double[k];
        if (t <= c.T[0]) return Val(0);
        if (t >= c.T[n - 1]) return Val(n - 1);
        int i0 = Array.FindLastIndex(c.T, x => x <= t);
        if (c.Interp == "STEP") return Val(i0);
        double f = (t - c.T[i0]) / Math.Max(1e-12, c.T[i0 + 1] - c.T[i0]);
        var a = Val(i0);
        var b = Val(i0 + 1);
        if (k == 4)
        {
            double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            if (dot < 0) for (int q = 0; q < 4; q++) b[q] = -b[q];
        }
        var r = new double[k];
        for (int q = 0; q < k; q++) r[q] = a[q] + (b[q] - a[q]) * f;
        return r;
    }

    private static Vector3 Vec3(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);

    private static Vector3 NodeVec(JsonNode? n, int len, float def) => n is JsonArray a && a.Count >= 3
        ? new Vector3((float)(double)a[0]!, (float)(double)a[1]!, (float)(double)a[2]!) : new Vector3(def);

    private static double[] NodeVec4(JsonNode? n) => n is JsonArray a && a.Count >= 4
        ? new[] { (double)a[0]!, (double)a[1]!, (double)a[2]!, (double)a[3]! } : new double[] { 0, 0, 0, 1 };
}
