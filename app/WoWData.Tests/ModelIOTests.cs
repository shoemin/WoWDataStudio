using System.IO;
using System.Linq;
using System.Numerics;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>Phase 3c core: the glTF package export, the reader, the view regenerator and the bounds.</summary>
public class ModelIOTests
{
    private readonly ITestOutputHelper _out;
    public ModelIOTests(ITestOutputHelper output) { _out = output; }

    internal static List<SubmeshInput> OriginalGeometry(M2Model m)
    {
        var v0 = m.Views[0];
        var raw = m.File["vertices"].Bytes;
        var list = new List<SubmeshInput>();
        foreach (var s in v0.Submeshes)
        {
            var (vv, local) = ModelExporter.SubmeshVertices(v0, s);
            list.Add(new SubmeshInput
            {
                Vertices = vv.Select(x => raw.AsSpan(v0.Indices[x] * 0x30, 0x30).ToArray()).ToArray(),
                Triangles = local,
            });
        }
        return list;
    }

    /// <summary>The triangles of view-0 submesh <paramref name="s"/> as the 48-byte records of their corners (hex).</summary>
    internal static List<string> SubmeshRecords(M2Model m, int s)
    {
        var v = m.Views[0];
        var sm = v.Submeshes[s];
        var raw = m.File["vertices"].Bytes;
        var l = new List<string>();
        for (int k = sm.IndexStart; k + 2 < sm.IndexStart + sm.IndexCount; k += 3)
            l.Add(string.Join("|", Enumerable.Range(0, 3).Select(c => Convert.ToHexString(raw.AsSpan(v.Indices[v.Triangles[k + c]] * 0x30, 0x30)))));
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    /// <summary>Per view: the triangles (as model-vertex positions) of each geoset, as a sorted list of strings.</summary>
    internal static Dictionary<int, List<string>> TrianglesByGeoset(M2Model m, int view)
    {
        var v = m.Views[view];
        var d = new Dictionary<int, List<string>>();
        foreach (var s in v.Submeshes)
        {
            if (!d.TryGetValue(s.GeosetId, out var l)) d[s.GeosetId] = l = new List<string>();
            for (int k = s.IndexStart; k + 2 < s.IndexStart + s.IndexCount; k += 3)
            {
                var p = new[] { v.Triangles[k], v.Triangles[k + 1], v.Triangles[k + 2] }.Select(t => m.Vertices[v.Indices[t]].Position).ToArray();
                // rotate so the smallest corner comes first (winding kept)
                int first = Enumerable.Range(0, 3).OrderBy(i => p[i].X).ThenBy(i => p[i].Y).ThenBy(i => p[i].Z).First();
                l.Add(string.Join(";", Enumerable.Range(0, 3).Select(i => p[(first + i) % 3]).Select(x => $"{x.X:R},{x.Y:R},{x.Z:R}")));
            }
        }
        foreach (var l in d.Values) l.Sort(StringComparer.Ordinal);
        return d;
    }

    [Fact]
    public void RegeneratedViewsKeepTheTrianglesAndValidate()
    {
        var client = TestEnv.Client;
        var paths = client.Names().Values.Where(f => f.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var pick = paths.Where((p, i) => i % 7 == 0).Concat(new[] { @"Creature\Boar\Boar.m2", @"Character\Orc\Male\OrcMale.m2", @"World\Kalimdor\Durotar\PassiveDoodads\Trees\DurotarPalm01.m2" }).Distinct().ToList();
        int done = 0, split = 0;
        var failures = new List<string>();
        foreach (var p in pick)
        {
            var data = client.Read(p)!;
            var m = M2Model.Load(data);
            if (m.Views.Length == 0) continue;
            byte[] outp;
            RebuildReport rep;
            try { (outp, rep) = M2MeshRebuilder.Rebuild(data, OriginalGeometry(m), recomputeBounds: false); }
            catch (M2RebuildException e) { failures.Add($"{p}: {e.Message}"); continue; }
            var n = M2Model.Load(outp);
            var errors = M2Validator.Errors(M2Validator.Validate(n));
            if (errors.Count > 0) failures.Add($"{p}: {string.Join("; ", errors.Take(3))}");
            for (int vi = 0; vi < m.Views.Length; vi++)
            {
                var a = TrianglesByGeoset(m, vi);
                var b = TrianglesByGeoset(n, vi);
                if (a.Count != b.Count || a.Any(kv => !b.TryGetValue(kv.Key, out var l) || !l.SequenceEqual(kv.Value))) failures.Add($"{p}: view {vi} triangles differ");
                foreach (var s in n.Views[vi].Submeshes)
                    if (s.BoneCount > n.Views[vi].BoneLimit) failures.Add($"{p}: view {vi} palette {s.BoneCount} > {n.Views[vi].BoneLimit}");
            }
            // everything outside the mesh is byte-identical
            foreach (var arr in new[] { "bones", "sequences", "textures", "colors", "transparency", "texture_transforms", "attachments", "events", "particles", "ribbons", "collision_vertices" })
                if (!m.File[arr].Bytes.AsSpan().SequenceEqual(n.File[arr].Bytes) && m.File[arr].Elem.Rec is not { Arrays.Count: > 0 })
                    failures.Add($"{p}: {arr} changed");
            Assert.Equal(ModelExporter.PartChecksums(m.File)["skeleton_and_animations"], ModelExporter.PartChecksums(n.File)["skeleton_and_animations"]);
            Assert.Equal(ModelExporter.PartChecksums(m.File)["attachments_events_effects"], ModelExporter.PartChecksums(n.File)["attachments_events_effects"]);
            if (rep.Splits.Count > 0) split++;
            done++;
        }
        _out.WriteLine($"{done} models rebuilt, {split} with split parts; {failures.Count} failures");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.True(done > 1000, $"{done} models");
    }

    /// <summary>The recomputed bounds (used after a mesh edit) are close to the values Blizzard stored.</summary>
    [Fact]
    public void RecomputedBoundsAreCloseToTheGames()
    {
        var client = TestEnv.Client;
        foreach (var p in new[] { @"Creature\Boar\Boar.m2", @"Character\Orc\Male\OrcMale.m2", @"Creature\Wolf\Wolf.m2" })
        {
            var m = M2Model.Load(client.Read(p)!);
            for (int si = 0; si < 3; si++)
            {
                var s = m.Sequences[si];
                var c = M2Bounds.Sequence(m, si);
                _out.WriteLine($"{p} seq {si}: file r {s.BoundsRadius:0.000} {s.BoundsMin} {s.BoundsMax} | computed r {c.Radius:0.000} {c.Min} {c.Max}");
                Assert.InRange(c.Radius / s.BoundsRadius, 0.9, 1.12);
                Assert.True(Vector3.Distance(c.Min, s.BoundsMin) < 0.2f && Vector3.Distance(c.Max, s.BoundsMax) < 0.2f);
            }
        }
        var axe = M2Model.Load(client.Read(@"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01.m2")!);
        var rest = M2Bounds.Rest(axe);
        Assert.Equal(axe.BoundsMin, rest.Min);
        Assert.Equal(axe.BoundsMax, rest.Max);
        Assert.Equal(axe.BoundsRadius, rest.Radius, 5);
    }
}
