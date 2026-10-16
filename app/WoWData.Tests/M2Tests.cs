using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WoWData.Core.M2;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>The Python reference results written by oracle/make_m2.py.</summary>
internal static class M2Oracle
{
    private static readonly Lazy<string> DirLazy = new(() => Oracle.Ensure("m2"));
    public static string Dir => DirLazy.Value;
    public static JsonArray All => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "all.json")))!.AsArray();
    public static JsonObject Sample => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "sample.json")))!.AsObject();
    public static JsonObject Edits => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "edits.json")))!.AsObject();
}

/// <summary>
/// The C# M2 port (WoWData.Core.M2) against the client's models and the Python reference: every model parses,
/// re-serialises byte-identically and validates exactly like the Python; a sample of 259 models has the same parsed
/// structures (counts and key values) and the same evaluated animation values.
/// </summary>
public class M2Tests
{
    private readonly ITestOutputHelper _out;
    public M2Tests(ITestOutputHelper output) { _out = output; }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void EveryModelParsesRoundTripsAndValidatesLikePython()
    {
        var client = TestEnv.Client;
        var mine = client.Names().Values.Where(f => f.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)).Select(f => f.Path).ToList();
        var py = M2Oracle.All;
        Assert.Equal(9691, py.Count);
        Assert.Equal(py.Select(r => r![0]!.GetValue<string>().ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal),
            mine.Select(p => p.ToUpperInvariant()).OrderBy(x => x, StringComparer.Ordinal));
        var sw = Stopwatch.StartNew();
        int parsed = 0, identical = 0, validatorMatches = 0;
        long bytes = 0;
        var failures = new List<string>();
        foreach (var row in py)
        {
            var path = row![0]!.GetValue<string>();
            var data = client.Read(path)!;
            bytes += data.Length;
            Assert.Equal(row[2]!.GetValue<string>(), Hex(data));
            M2Model m;
            try
            {
                m = M2Model.Load(data);
                parsed++;
            }
            catch (Exception e)
            {
                failures.Add($"{path}: {e.Message}");
                continue;
            }
            var back = m.File.Serialize();
            if (back.AsSpan().SequenceEqual(data)) identical++;
            else failures.Add($"{path}: does not re-serialise byte-identically");
            var issues = M2Validator.Validate(m);
            var counts = new[] { issues.Count(i => i.Level == "error"), issues.Count(i => i.Level == "warning"), issues.Count(i => i.Level == "note") };
            var keys = issues.Where(i => i.Level != "note").Select(i => $"{i.Level}|{i.Where}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            var pyKeys = row[7]!.AsArray().Select(k => k!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (counts[0] == row[4]!.GetValue<int>() && counts[1] == row[5]!.GetValue<int>() && counts[2] == row[6]!.GetValue<int>() && keys.SequenceEqual(pyKeys))
                validatorMatches++;
            else failures.Add($"{path}: validator {string.Join("/", counts)} vs Python {row[4]}/{row[5]}/{row[6]}; first difference " +
                              (keys.Except(pyKeys).Concat(pyKeys.Except(keys)).FirstOrDefault() ?? "(order)"));
        }
        _out.WriteLine($"{parsed} parsed, {identical} byte-identical, {validatorMatches} validator results equal to Python; {bytes / 1048576.0:0} MB in {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.Equal(9691, identical);
        Assert.All(py, r => Assert.True(r![3]!.GetValue<bool>()));     // the Python round trip agrees
        Assert.Equal(0, py.Sum(r => r![4]!.GetValue<int>()));        // and no client model has a validation error
    }

    [Fact]
    public void NoBytesOutsideTheParsedBlocks()
    {
        foreach (var path in new[] { @"Creature\Boar\Boar.m2", @"Character\Orc\Male\OrcMale.m2", @"Spells\FireBall_Missile_High.m2", @"Creature\Cupid\Cupid.m2" })
        {
            var m = M2File.Parse(TestEnv.Client.Read(path)!);
            var (covered, stray) = m.Coverage();
            Assert.Equal(0, stray);
            Assert.True(covered > 0x144);
        }
    }

    private static void Near(double expected, double actual, string what, double tol = 1e-4)
    {
        if (double.IsNaN(expected) && double.IsNaN(actual)) return;
        Assert.True(Math.Abs(expected - actual) <= tol * Math.Max(1, Math.Abs(expected)), $"{what}: expected {expected}, got {actual}");
    }

    private static int[] Ints(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
    private static double[] Dbl(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<double>()).ToArray();

    private static int[] TrackInfo<T>(M2Track<T> t) where T : struct => new[] { t.Interpolation, t.GlobalSequence, t.Ranges.Length, t.Times.Length };

    [Fact]
    public void SampleStructuresEqualPython()
    {
        var client = TestEnv.Client;
        var sample = M2Oracle.Sample;
        Assert.True(sample.Count >= 250, $"{sample.Count} sampled models");
        int animChecks = 0;
        foreach (var (path, node) in sample)
        {
            var d = node!.AsObject();
            var data = client.Read(path)!;
            var m = M2Model.Load(data);
            string w = path;
            Assert.Equal(d["version"]!.GetValue<int>(), (int)m.Version);
            Assert.Equal(d["name"]!.GetValue<string>(), m.Name);
            Assert.Equal(d["global_flags"]!.GetValue<long>(), m.GlobalFlags);
            var counts = m.File.Counts();
            foreach (var (k, v) in d["counts"]!.AsObject()) Assert.True(v!.GetValue<int>() == counts[k], $"{w}: count of {k}");
            var bounds = Dbl(d["bounds"]);
            var mine = new[] { m.BoundsMin.X, m.BoundsMin.Y, m.BoundsMin.Z, m.BoundsMax.X, m.BoundsMax.Y, m.BoundsMax.Z, m.BoundsRadius };
            for (int i = 0; i < 7; i++) Near(bounds[i], mine[i], $"{w} bounds");
            Assert.Equal(Ints(d["global_sequences"]), m.GlobalSequences.Select(x => (int)x).ToArray());
            var seqs = d["sequences"]!.AsArray();
            Assert.Equal(seqs.Count, m.Sequences.Length);
            for (int i = 0; i < seqs.Count; i++)
            {
                var s = seqs[i]!.AsArray();
                var q = m.Sequences[i];
                Assert.Equal(new long[] { s[0]!.GetValue<int>(), s[1]!.GetValue<int>(), s[2]!.GetValue<long>(), s[3]!.GetValue<long>(), s[4]!.GetValue<long>(), s[5]!.GetValue<int>() },
                    new long[] { q.AnimId, q.SubId, q.Start, q.End, q.Flags, q.NextVariant });
                Near(s[6]!.GetValue<double>(), q.MoveSpeed, $"{w} move speed");
            }
            var bones = d["bones"]!.AsArray();
            Assert.Equal(bones.Count, m.Bones.Length);
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i]!.AsArray();
                var mb = m.Bones[i];
                Assert.Equal(new long[] { b[0]!.GetValue<int>(), b[1]!.GetValue<long>(), b[2]!.GetValue<int>(), b[3]!.GetValue<int>() },
                    new long[] { mb.KeyBoneId, mb.Flags, mb.Parent, mb.SubmeshId });
                Near(b[4]!.GetValue<double>(), mb.Pivot.X, "pivot");
                Near(b[5]!.GetValue<double>(), mb.Pivot.Y, "pivot");
                Near(b[6]!.GetValue<double>(), mb.Pivot.Z, "pivot");
                var tracks = b.Skip(7).Select(x => x!.GetValue<int>()).ToArray();
                Assert.Equal(tracks, TrackInfo(mb.Translation).Concat(TrackInfo(mb.Rotation)).Concat(TrackInfo(mb.Scale)).ToArray());
            }
            if (d["vertex_sums"] is JsonArray vsum)
            {
                var v = m.Vertices;
                double[] sums = new double[8];
                long wsum = 0, bsum = 0;
                foreach (var x in v)
                {
                    sums[0] += x.Position.X; sums[1] += x.Position.Y; sums[2] += x.Position.Z;
                    sums[3] += x.Normal.X; sums[4] += x.Normal.Y; sums[5] += x.Normal.Z;
                    sums[6] += x.Uv.X; sums[7] += x.Uv.Y;
                    wsum += x.W0 + x.W1 + x.W2 + x.W3;
                    bsum += x.B0 + x.B1 + x.B2 + x.B3;
                }
                var ps = Dbl(vsum);
                for (int i = 0; i < 8; i++) Near(ps[i], sums[i], $"{w} vertex sum {i}", 1e-6);
                Assert.Equal((long)ps[8], wsum);
                Assert.Equal((long)ps[9], bsum);
                var f0 = Dbl(d["vertex_first"]);
                var v0 = v[0];
                var mine0 = new double[] { v0.Position.X, v0.Position.Y, v0.Position.Z, v0.W0, v0.W1, v0.W2, v0.W3, v0.B0, v0.B1, v0.B2, v0.B3,
                    v0.Normal.X, v0.Normal.Y, v0.Normal.Z, v0.Uv.X, v0.Uv.Y, v0.Uv2.X, v0.Uv2.Y };
                for (int i = 0; i < mine0.Length; i++) Near(f0[i], mine0[i], $"{w} first vertex [{i}]", 1e-7);
            }
            var views = d["views"]!.AsArray();
            Assert.Equal(views.Count, m.Views.Length);
            for (int vi = 0; vi < views.Count; vi++)
            {
                var pv = views[vi]!.AsObject();
                var mv = m.Views[vi];
                Assert.Equal(Ints(pv["counts"]), new[] { mv.Indices.Length, mv.Triangles.Length, mv.Properties.Length / 4, mv.Submeshes.Length, mv.TexUnits.Length, (int)mv.BoneLimit });
                Assert.Equal(pv["index_sum"]!.GetValue<long>(), mv.Indices.Sum(x => (long)x));
                Assert.Equal(pv["triangle_sum"]!.GetValue<long>(), mv.Triangles.Sum(x => (long)x));
                var subs = pv["submeshes"]!.AsArray();
                for (int k = 0; k < subs.Count; k++)
                {
                    var s = mv.Submeshes[k];
                    Assert.Equal(Ints(subs[k]), new[] { s.GeosetId, s.VertexStart, s.VertexCount, s.IndexStart, s.IndexCount, s.BoneCount, s.BoneStart, s.BoneInfluences, s.CenterBone });
                }
                var tus = pv["texunits"]!.AsArray();
                for (int k = 0; k < tus.Count; k++)
                {
                    var t = mv.TexUnits[k];
                    var e = Ints(tus[k]);
                    var got = new[] { t.Flags, t.Priority, t.ShaderId, t.Submesh, t.GeosetIndex, t.Color, t.RenderFlags, t.MaterialLayer, t.TextureCount,
                        t.TextureLookup, t.TexUnitLookup, t.TransparencyLookup, t.TextureTransformLookup };
                    // the Python reads color as int16 and the lookups as uint16, like the C# port
                    Assert.Equal(e, got);
                }
            }
            var tex = d["textures"]!.AsArray();
            Assert.Equal(tex.Count, m.Textures.Length);
            for (int i = 0; i < tex.Count; i++)
            {
                var t = tex[i]!.AsArray();
                Assert.Equal(t[0]!.GetValue<int>(), m.Textures[i].Type);
                Assert.Equal(t[1]!.GetValue<long>(), m.Textures[i].Flags);
                Assert.Equal(t[2]!.GetValue<string>(), m.Textures[i].FileName);
            }
            Assert.Equal(d["render_flags"]!.AsArray().Select(r => (Ints(r)[0], Ints(r)[1])).ToArray(), m.RenderFlags.Select(r => (r.Flags, r.Blend)).ToArray());
            Assert.Equal(Ints(d["animation_lookup"]), m.AnimationLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["key_bone_lookup"]), m.KeyBoneLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["replaceable_lookup"]), m.ReplaceableLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["bone_lookup"]), m.BoneLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["texture_lookup"]), m.TextureLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["tex_unit_lookup"]), m.TexUnitLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["transparency_lookup"]), m.TransparencyLookup.Select(x => (int)x).ToArray());
            Assert.Equal(Ints(d["texture_transform_lookup"]), m.TextureTransformLookup.Select(x => (int)x).ToArray());
            var colors = d["colors"]!.AsArray();
            Assert.Equal(colors.Count, m.Colors.Length);
            for (int i = 0; i < colors.Count; i++) Assert.Equal(Ints(colors[i]), TrackInfo(m.Colors[i].Color).Concat(TrackInfo(m.Colors[i].Alpha)).ToArray());
            var tr = d["transparency"]!.AsArray();
            for (int i = 0; i < tr.Count; i++) Assert.Equal(Ints(tr[i]), TrackInfo(m.Transparency[i]));
            var att = d["attachments"]!.AsArray();
            Assert.Equal(att.Count, m.Attachments.Length);
            for (int i = 0; i < att.Count; i++)
            {
                var a = Dbl(att[i]);
                Assert.Equal((int)a[0], m.Attachments[i].Id);
                Assert.Equal((int)a[1], m.Attachments[i].Bone);
                Near(a[2], m.Attachments[i].Position.X, "attachment");
            }
            Assert.Equal(d["serialize_sha256"]!.GetValue<string>(), Hex(m.File.Serialize()));
            var issues = M2Validator.Validate(m).Select(i => $"{i.Level}|{i.Where}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(d["issues"]!.AsArray().Select(x => x!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList(), issues);

            // animation: the C# evaluator at times inside a sequence's key range equals the Python evaluator
            foreach (var s in d["anim_samples"]!.AsArray())
            {
                var a = s!.AsArray();
                int bi = a[0]!.GetValue<int>(), si = a[2]!.GetValue<int>();
                string trackName = a[1]!.GetValue<string>();
                uint t = a[4]!.GetValue<uint>();
                var expect = Dbl(a[5]);
                var time = new M2Anim.Time(si, 0, t, 0);
                var bone = m.Bones[bi];
                float[] got = trackName switch
                {
                    "translation" => V(M2Anim.Eval(bone.Translation, time, m.GlobalSequences, Vector3.Zero)),
                    "scale" => V(M2Anim.Eval(bone.Scale, time, m.GlobalSequences, Vector3.One)),
                    _ => Q(M2Anim.Eval(bone.Rotation, time, m.GlobalSequences, Quaternion.Identity)),
                };
                for (int i = 0; i < expect.Length; i++) Near(expect[i], got[i], $"{w} bone {bi} {trackName} seq {si} t {t}", 1e-5);
                animChecks++;
            }
        }
        _out.WriteLine($"{sample.Count} models compared, {animChecks} animation samples");
        Assert.True(animChecks > 500, $"{animChecks} animation samples");
    }

    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };

    [Fact]
    public void EditedModelIsLaidOutLikeThePythonWriter()
    {
        var data = TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!;
        var f = M2File.Parse(data);
        var name = System.Text.Encoding.ASCII.GetBytes("BoarEditedByTheTests\0");
        f["name"].Set(name.Length, name);
        var verts = f["vertices"];
        var bytes = (byte[])verts.Bytes.Clone();
        BitConverter.GetBytes(1.5f).CopyTo(bytes, 0);
        BitConverter.GetBytes(-2.25f).CopyTo(bytes, 4);
        BitConverter.GetBytes(3.0f).CopyTo(bytes, 8);
        verts.Set(verts.Count, bytes);
        var outBytes = f.Serialize();
        Assert.Equal(M2Oracle.Edits["rename_and_move_vertex"]!.GetValue<string>(), Hex(outBytes));
        var back = M2Model.Load(outBytes);
        Assert.Equal("BoarEditedByTheTests", back.Name);
        Assert.Equal(new Vector3(1.5f, -2.25f, 3f), back.Vertices[0].Position);
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(back)));
    }

    [Fact]
    public void NamedModelContents()
    {
        var boar = M2Model.Load(TestEnv.Client.Read(@"Creature\Boar\Boar.m2")!);
        Assert.Equal((13, 57, 401), (boar.Sequences.Length, boar.Bones.Length, boar.Vertices.Length));
        Assert.Equal(new uint[] { 256, 75, 53, 21 }, boar.Views.Select(v => v.BoneLimit).ToArray());
        Assert.Equal(11, boar.Textures[0].Type);
        var orc = M2Model.Load(TestEnv.Client.Read(@"Character\Orc\Male\OrcMale.m2")!);
        Assert.Equal((128, 2728, 134, 34), (orc.Bones.Length, orc.Vertices.Length, orc.Sequences.Length, orc.Attachments.Length));
        Assert.Equal(41, orc.Views[0].Submeshes.Select(s => s.GeosetId).Distinct().Count());
        // bone matrices: a non-animated pose is the identity; an animated one keeps every pivot finite
        var mats = new Matrix4x4[orc.Bones.Length];
        M2Anim.BoneMatrices(orc, new M2Anim.Time(0, orc.Sequences[0].Start, 200, 0), mats);
        Assert.All(mats, mm => Assert.True(float.IsFinite(mm.M41) && float.IsFinite(mm.M11)));
        Assert.Contains(mats, mm => !mm.IsIdentity);
    }

    [Fact]
    public void ClientMatrixFormulaMatchesTheDefinition()
    {
        // T(pivot) T(t) R S T(-pivot) applied to a point, written out by hand (column vectors)
        var pivot = new Vector3(1, 2, 3);
        var t = new Vector3(0.5f, -1, 2);
        var q = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
        var s = new Vector3(1.5f, 1, 0.5f);
        var m = M2Anim.BoneLocal(pivot, t, q, s);
        var p = new Vector3(4, -2, 7);
        var expected = Vector3.Transform((p - pivot) * s, q) + t + pivot;
        var got = Vector3.Transform(p, m);
        Assert.True(Vector3.Distance(expected, got) < 1e-4f, $"{expected} vs {got}");
    }
}
