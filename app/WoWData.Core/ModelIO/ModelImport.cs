using System.Buffers.Binary;
using System.Numerics;
using WoWData.Core.Blp;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace WoWData.Core.ModelIO;

public enum ModelChangeKind { Mesh, Texture, NewTexture, Skeleton, Effects }
public enum RuleLevel { Info, Warning, Blocker }

/// <summary>One plain-language check of a change: information, a warning, or a reason it cannot be imported.</summary>
public sealed record ModelRule(RuleLevel Level, string Text)
{
    public override string ToString() => Text;
}

/// <summary>What happened to one part of the model (view-0 submesh) in the edit.</summary>
public sealed class SubmeshChange
{
    public int Submesh { get; init; }
    public int Geoset { get; init; }
    public bool Changed { get; set; }
    public bool Missing { get; set; }
    public string MatchedBy { get; set; } = "";
    public int OriginalVertices { get; set; }
    public int OriginalTriangles { get; set; }
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int MovedOrNewVertices { get; set; }
    /// <summary>Back-side triangles (a second face over the same corners) Blender cannot hold, put back from the original.</summary>
    public int Restored { get; set; }
    /// <summary>Why the first unmatched vertices did not match the original (nearest original vertex), for diagnosis.</summary>
    public List<string> Unmatched { get; } = new();
    public string Summary => Missing ? $"part {Submesh} (geoset {Geoset}): not in your file, kept as it was"
        : !Changed ? $"part {Submesh} (geoset {Geoset}): unchanged"
        : $"part {Submesh} (geoset {Geoset}): {OriginalVertices:N0} -> {Vertices:N0} vertices, {OriginalTriangles:N0} -> {Triangles:N0} triangles, {MovedOrNewVertices:N0} vertices moved, re-weighted or new" +
          (Restored > 0 ? $" ({Restored} back-side triangles kept from the original)" : "");
}

/// <summary>One row of the import review: a mesh or texture change, whether it is included, and its checks.</summary>
public sealed class ModelChange
{
    public ModelChangeKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; set; } = "";
    /// <summary>The game file it writes (null for rows that write nothing).</summary>
    public string? GamePath { get; init; }
    public bool Unchanged { get; set; }
    public bool CanInclude { get; set; }
    public bool Include { get; set; }
    public List<ModelRule> Rules { get; } = new();
    public byte[]? OriginalBytes { get; set; }
    public byte[]? NewBytes { get; set; }

    // textures
    public PackageTexture? Texture { get; init; }
    public EditedImage? Image { get; init; }
    public RgbaImage? EditedImage { get; init; }
    public ImportPlan? Plan { get; set; }
    public bool NeedsResize { get; set; }
    public (int W, int H)? ResizeTo { get; set; }
    public bool AlphaQuestion { get; set; }
    public ImportFormatChoice Format { get; set; }
    /// <summary>0.6.1: the additive / multiply advice for this texture (null: drawn normally).</summary>
    public string? GlowNote { get; set; }

    // mesh
    public List<SubmeshChange> Parts { get; } = new();
    public RebuildReport? Rebuild { get; set; }
    /// <summary>0.10.0: the rows that write the model file itself (mesh, skeleton and animations, effects): they are
    /// written together as one file.</summary>
    public bool IsModelPart => Kind is ModelChangeKind.Mesh or ModelChangeKind.Skeleton or ModelChangeKind.Effects;
    /// <summary>The checks of the composed model (added by <see cref="ModelImportAnalyzer.Compose"/>).</summary>
    internal List<ModelRule> ComposeRules { get; } = new();
    /// <summary>The changes of the skeleton and animations, one line each (Skeleton rows).</summary>
    public List<string> Lines { get; } = new();

    public bool HasBlocker => Rules.Any(r => r.Level == RuleLevel.Blocker);
    public override string ToString() => Title;
}

/// <summary>Where the import reads the current state of the game data.</summary>
public sealed class ModelImportContext
{
    /// <summary>The model's bytes as the app uses them now (your edit first, else the game's).</summary>
    public Func<string, byte[]?> ReadCurrent { get; init; } = _ => null;
    /// <summary>The game's own copy of a file (null when the game has none) and its archive.</summary>
    public Func<string, (byte[] Data, string Archive)?> ReadGame { get; init; } = _ => null;
    public TextureService? Textures { get; init; }
    public AssetIndex? Index { get; init; }
    /// <summary>0.10.0: AnimationData (names, fallbacks) for the animation checks and the playable-animation table.</summary>
    public AnimationCatalog? Animations { get; init; }
}

public sealed class ModelImportAnalysis
{
    public EditedModel Edited { get; init; } = null!;
    public ModelPackageManifest Manifest => Edited.Manifest;
    public string ModelPath => Manifest.Model.ArchivePath;
    /// <summary>The model the export was made from (found by its checksum), or null.</summary>
    public byte[]? BaseModel { get; set; }
    public string BaseDescription { get; set; } = "";
    public List<ModelChange> Changes { get; } = new();
    public List<ModelRule> General { get; } = new();
    public bool NothingChanged => !Changes.Any(c => !c.Unchanged);
    public bool AnythingToImport => Changes.Any(c => c.Include && c.CanInclude && !c.Unchanged);
    public string UsersWarning { get; set; } = "";
    public ModelImportContext Context { get; init; } = null!;
    /// <summary>0.10.0: the skeleton / animation plan (null when the export had no rig).</summary>
    public RigAnalysis? Rig { get; set; }
    public EffectsEdit? Effects { get; set; }
    /// <summary>The mesh as it will be written (final bone numbers), when the mesh changed.</summary>
    public List<SubmeshInput>? MeshInputs { get; set; }
    /// <summary>The model file the included model rows write (mesh + skeleton + effects together).</summary>
    public byte[]? ModelBytes { get; set; }
}

/// <summary>
/// "Import edited model...": compares what came back (<see cref="EditedModelReader"/>) with the export's manifest and
/// the model it was made from, and lists the changes with plain-language checks. Nothing is written here; the review
/// screen lets the user include or leave out each change, then <see cref="ModelImportCommitter"/> saves them as one
/// change set.
///
/// Mesh: every vertex is matched to the original vertices of its part (position within 0.2 mm, the same UV, a normal
/// within 8 degrees, the same bones and weights after quantisation); a part is unchanged when every vertex matches and
/// the triangles are the same (Blender splits vertices on normals and drops loose vertices: neither counts as an edit).
/// Matched vertices keep their original bytes, so only what you edited changes; new or moved vertices get their
/// weights re-quantised to the strongest 4 summing to 255. Then <see cref="M2MeshRebuilder"/> rebuilds the four views
/// and the bounds, and the validator must pass.
/// Textures: the pixels are compared with what was exported (a checksum, then pixel by pixel ignoring colour under
/// fully transparent pixels); an edited one is converted like a texture import (original format, only changed blocks
/// re-encoded, size and transparency checks).
/// </summary>
public static class ModelImportAnalyzer
{
    public const float PositionTolerance = 2e-4f;
    /// <summary>0.10.0: the titles of the review's skeleton and effects rows.</summary>
    public const string SkeletonRowTitle = "Skeleton and animations";
    public const string EffectsRowTitle = "Particles and ribbons";
    public const float UvTolerance = 2e-4f;
    public const float NormalDotTolerance = 0.99f;

    public static ModelImportAnalysis Analyze(string inputPath, ModelImportContext ctx) => Analyze(EditedModelReader.Read(inputPath), ctx);

    public static ModelImportAnalysis Analyze(EditedModel em, ModelImportContext ctx)
    {
        var a = new ModelImportAnalysis { Edited = em, Context = ctx };
        var man = em.Manifest;
        var path = man.Model.ArchivePath;
        a.UsersWarning = man.Users.Warning ?? (man.Users.Count > 1 ? $"A mesh change affects {man.Users.Summary}." : "");
        foreach (var n in em.Notes) a.General.Add(new ModelRule(RuleLevel.Warning, n));

        // the model the export was made from: your current edit, or the game's
        var current = ctx.ReadCurrent(path);
        var game = ctx.ReadGame(path);
        if (current != null && ModelExporter.Sha(current) == man.Model.Sha256)
        {
            a.BaseModel = current;
            a.BaseDescription = man.Model.Source == "workspace" ? "your current edit of the model" : "the game's model";
        }
        else if (game != null && ModelExporter.Sha(game.Value.Data) == man.Model.Sha256)
        {
            a.BaseModel = game.Value.Data;
            a.BaseDescription = "the game's model";
            if (current != null && ModelExporter.Sha(current) != man.Model.Sha256)
                a.General.Add(new ModelRule(RuleLevel.Warning, "The model was changed in your workspace since this export. A mesh change from this file replaces that change."));
        }
        else
        {
            a.General.Add(new ModelRule(RuleLevel.Warning,
                $"The model this file was exported from ({Path.GetFileName(path)}, checksum {man.Model.Sha256[..12]}...) is neither the game's nor your current version, so mesh changes cannot be matched. Export the model again and redo the mesh edit. Texture changes can still be imported."));
        }

        AnalyzeRig(a, em, ctx);
        AnalyzeMesh(a, em, ctx);
        AnalyzeEffects(a, em);
        AnalyzeTextures(a, em, ctx);
        Compose(a);
        if (a.NothingChanged)
            a.General.Insert(0, new ModelRule(RuleLevel.Info, "No changes found: the mesh and every texture are the same as in the export. Nothing will be written."));
        return a;
    }

    // ================================================================= mesh

    private readonly struct Influence
    {
        public readonly byte B0, B1, B2, B3, W0, W1, W2, W3;
        public Influence(byte[] b, byte[] w)
        {
            B0 = b[0]; B1 = b[1]; B2 = b[2]; B3 = b[3];
            W0 = w[0]; W1 = w[1]; W2 = w[2]; W3 = w[3];
        }
    }

    /// <summary>Top 4 influences quantised to 8 bits summing to 255 (largest remainder), as (bones, weights).</summary>
    public static (byte[] Bones, byte[] Weights) QuantizeWeights((int Bone, float Weight)[] infl, int fallbackBone)
    {
        var top = infl.Where(x => x.Weight > 0 && x.Bone >= 0).OrderByDescending(x => x.Weight).ThenBy(x => x.Bone).Take(4).ToArray();
        var bones = new byte[4];
        var w = new byte[4];
        if (top.Length == 0)
        {
            bones[0] = (byte)Math.Max(0, fallbackBone);
            w[0] = 255;
            return (bones, w);
        }
        double sum = top.Sum(x => (double)x.Weight);
        var exact = top.Select(x => x.Weight / sum * 255.0).ToArray();
        var q = exact.Select(x => (int)Math.Floor(x)).ToArray();
        int rest = 255 - q.Sum();
        foreach (int i in Enumerable.Range(0, q.Length).OrderByDescending(i => exact[i] - q[i]).ThenBy(i => i).Take(rest)) q[i]++;
        // drop influences that rounded to zero, keep the order by weight
        var kept = Enumerable.Range(0, top.Length).Where(i => q[i] > 0).OrderByDescending(i => q[i]).ThenBy(i => top[i].Bone).ToArray();
        for (int k = 0; k < kept.Length; k++)
        {
            bones[k] = (byte)top[kept[k]].Bone;
            w[k] = (byte)q[kept[k]];
        }
        return (bones, w);
    }

    /// <summary>What the export writes of a vertex record: position, normal, UV and the merged bone weights.</summary>
    private static string ExportKey(byte[] rec)
    {
        var v = new M2Vertex
        {
            W0 = rec[12], W1 = rec[13], W2 = rec[14], W3 = rec[15], B0 = rec[16], B1 = rec[17], B2 = rec[18], B3 = rec[19],
        };
        var (b, w) = ModelExporter.MergeInfluences(v);
        // the normal as the export writes it (normalised; float noise of the last bit does not count: ManaTotem)
        var n = Nrm(rec);
        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero;
        string nk = FormattableString.Invariant($"{Math.Round(n.X, 4)},{Math.Round(n.Y, 4)},{Math.Round(n.Z, 4)}");
        // positions and UVs by value (-0 and +0 are the same in the glTF: UI_RS_Scourge)
        var p = Pos(rec);
        var uv = Uv(rec);
        string pk = FormattableString.Invariant($"{p.X + 0f:R},{p.Y + 0f:R},{p.Z + 0f:R}|{uv.X + 0f:R},{uv.Y + 0f:R}");
        return pk + "|" + nk + "|" + string.Join(",", Enumerable.Range(0, 4).Where(k => w[k] > 0).Select(k => $"{b[k]}:{w[k]}").OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string WeightKey(ReadOnlySpan<byte> rec)
    {
        var l = new List<(int B, int W)>(4);
        for (int k = 0; k < 4; k++)
            if (rec[12 + k] > 0) l.Add((rec[16 + k], rec[12 + k]));
        l.Sort();
        return string.Join(",", l.Select(x => $"{x.B}:{x.W}"));
    }

    /// <summary>The same bones and weights within 2/255. Some client vertices list a bone twice (weights 85/85/85 on
    /// bones a, a, b); the export sums them, but Blender keeps only one entry per bone when it reads such a file, so the
    /// "one entry per bone, renormalised" reading is accepted too.</summary>
    private static bool WeightsClose(ReadOnlySpan<byte> rec, byte[] bones, byte[] w)
    {
        var b = new Dictionary<int, double>();
        for (int k = 0; k < 4; k++) if (w[k] > 0) b[bones[k]] = b.GetValueOrDefault(bones[k]) + w[k];
        var sum = new Dictionary<int, double>();
        var last = new Dictionary<int, double>();
        for (int k = 0; k < 4; k++)
            if (rec[12 + k] > 0)
            {
                sum[rec[16 + k]] = sum.GetValueOrDefault(rec[16 + k]) + rec[12 + k];
                last[rec[16 + k]] = rec[12 + k];
            }
        bool Close(Dictionary<int, double> a)
        {
            if (a.Count != b.Count) return false;
            double total = a.Values.Sum();
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out double x) || Math.Abs(x - kv.Value * 255.0 / total) > 2.01) return false;
            return true;
        }
        return Close(sum) || Close(last);
    }

    private static Vector3 Pos(ReadOnlySpan<byte> r) => new(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..]));
    private static Vector3 Nrm(ReadOnlySpan<byte> r) => new(BinaryPrimitives.ReadSingleLittleEndian(r[20..]), BinaryPrimitives.ReadSingleLittleEndian(r[24..]), BinaryPrimitives.ReadSingleLittleEndian(r[28..]));
    private static Vector2 Uv(ReadOnlySpan<byte> r) => new(BinaryPrimitives.ReadSingleLittleEndian(r[32..]), BinaryPrimitives.ReadSingleLittleEndian(r[36..]));

    private static (long, long, long) Cell(Vector3 p) => ((long)Math.Floor(p.X / 1e-3), (long)Math.Floor(p.Y / 1e-3), (long)Math.Floor(p.Z / 1e-3));

    private static byte[] NewRecord(Vector3 p, Vector3 n, Vector2 uv, Vector2 uv2, byte[] bones, byte[] weights)
    {
        var r = new byte[0x30];
        void F(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(o), v);
        F(0, p.X); F(4, p.Y); F(8, p.Z);
        for (int k = 0; k < 4; k++)
        {
            r[12 + k] = weights[k];
            r[16 + k] = bones[k];
        }
        var nn = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        F(20, nn.X); F(24, nn.Y); F(28, nn.Z);
        F(32, uv.X); F(36, uv.Y);
        F(40, uv2.X); F(44, uv2.Y);
        return r;
    }

    private static void AnalyzeMesh(ModelImportAnalysis a, EditedModel em, ModelImportContext ctx)
    {
        var man = em.Manifest;
        string model = Path.GetFileName(man.Model.ArchivePath);
        var change = new ModelChange
        {
            Kind = ModelChangeKind.Mesh, Title = $"Mesh of {model}", GamePath = man.Model.ArchivePath, OriginalBytes = a.BaseModel,
        };
        var prims = em.Primitives.Where(p => !p.IsCollision).ToList();
        var newObjects = prims.Where(p => p.Submesh < 0).ToList();
        if (a.BaseModel == null)
        {
            change.Unchanged = true;
            change.Detail = "Mesh changes cannot be matched to this model version (see the note above).";
            change.Rules.Add(new ModelRule(RuleLevel.Blocker, "The model changed since the export: export it again to edit the mesh."));
            a.Changes.Add(change);
            return;
        }
        var m = M2Model.Load(a.BaseModel);
        // 0.10.0: weights come in "provisional" bone numbers (kept bones: their original number, new bones after them);
        // the mesh is stored with the final numbers of the edited skeleton
        var rigA = a.Rig;
        int ToFinalOld(int old)
        {
            if (rigA == null) return old;
            for (int guard = 0; old >= 0 && old < m.Bones.Length && guard < 1024; guard++)
            {
                if (rigA.ProvisionalToFinal.TryGetValue(old, out int f) && f >= 0) return f;
                old = m.Bones[old].Parent;
            }
            return 0;
        }
        int ToFinalProv(int prov) => rigA == null ? prov : prov < m.Bones.Length ? ToFinalOld(prov) : rigA.ProvisionalToFinal.TryGetValue(prov, out int f) && f >= 0 ? f : 0;
        byte[] FinalRecord(byte[] rec)
        {
            if (rigA == null || !rigA.Changed) return rec;
            var r = (byte[])rec.Clone();
            for (int k = 0; k < 4; k++)
                if (r[12 + k] > 0 || r[16 + k] > 0)
                {
                    int fb = ToFinalOld(r[16 + k]);
                    if (fb > 255) throw new M2RebuildException($"a vertex would use bone {fb}: vertices can only use the first 256 bones.");
                    r[16 + k] = (byte)fb;
                }
            return r;
        }
        if (m.Views.Length == 0)
        {
            change.Unchanged = true;
            change.Detail = "The model draws nothing (no views).";
            a.Changes.Add(change);
            return;
        }
        var v0 = m.Views[0];
        var raw = m.File["vertices"].Bytes;
        int nbones = m.Bones.Length;
        var inputs = new List<SubmeshInput>();
        bool anyChanged = false;
        var unknownBones = new SortedSet<string>(StringComparer.Ordinal);
        int moreThan4 = 0, noWeights = 0, bonesBeyond255 = 0;
        for (int s = 0; s < v0.Submeshes.Length; s++)
        {
            var sm = v0.Submeshes[s];
            var (vv, local) = ModelExporter.SubmeshVertices(v0, sm);
            var origRecs = vv.Select(x => raw.AsSpan(v0.Indices[x] * 0x30, 0x30).ToArray()).ToArray();
            var part = new SubmeshChange { Submesh = s, Geoset = sm.GeosetId, OriginalVertices = origRecs.Length, OriginalTriangles = local.Length / 3 };
            change.Parts.Add(part);
            var mine = prims.Where(p => p.Submesh == s).ToList();
            var keep = new SubmeshInput { Vertices = origRecs.Select(FinalRecord).ToArray(), Triangles = local };
            if (mine.Count == 0)
            {
                if (sm.IndexCount > 0) part.Missing = true;
                part.Vertices = origRecs.Length;
                part.Triangles = local.Length / 3;
                inputs.Add(keep);
                continue;
            }
            part.MatchedBy = string.Join(", ", mine.Select(p => p.MatchedBy).Distinct());

            // classes of original vertices that the export cannot tell apart (0.10.0: the same position, normal, UV and
            // weights; bytes the glTF does not carry, such as a zero-weight bone number or the unused second UV, do not
            // count: Creature\Kobold has such twins)
            var cls = new int[origRecs.Length];
            var byBytes = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < origRecs.Length; i++)
            {
                var k = ExportKey(origRecs[i]);
                if (!byBytes.TryGetValue(k, out int c)) byBytes[k] = c = i;
                cls[i] = c;
            }
            var grid = new Dictionary<(long, long, long), List<int>>();
            for (int i = 0; i < origRecs.Length; i++)
            {
                var c = Cell(Pos(origRecs[i]));
                if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
                l.Add(i);
            }
            int centreBone = sm.CenterBone < nbones ? sm.CenterBone : 0;

            // the imported vertices: match each one to an original vertex
            var newRecs = new List<byte[]>();
            var newIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var tris = new List<int>();
            var matchedTris = new HashSet<(int, int, int)>();
            bool allMatched = true;
            int moved = 0;
            foreach (var p in mine)
            {
                if (!p.HasWeights && nbones > 0 && p.Positions.Length > 0) noWeights += p.Positions.Length;
                foreach (var u in p.UnknownBones) unknownBones.Add(u);
                var map = new int[p.Positions.Length];
                var origOf = new int[p.Positions.Length];
                for (int v = 0; v < p.Positions.Length; v++)
                {
                    var infl = p.Influences[v];
                    if (infl.Count(x => x.Weight > 0) > 4) moreThan4++;
                    var (qbp, qw) = QuantizeWeightsInt(infl, centreBone);
                    var qb = qbp.Select(x => (byte)Math.Min(255, Math.Max(0, x))).ToArray();
                    bool beyond = qbp.Any(x => x > 255);
                    var pos = p.Positions[v];
                    var uv = p.Uvs.Length > v ? p.Uvs[v] : Vector2.Zero;
                    var nrm = p.Normals.Length > v ? p.Normals[v] : Vector3.UnitZ;
                    int best = -1;
                    float bestD = float.MaxValue;
                    var (cx, cy, cz) = Cell(pos);
                    for (long dx = -1; dx <= 1; dx++)
                        for (long dy = -1; dy <= 1; dy++)
                            for (long dz = -1; dz <= 1; dz++)
                            {
                                if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                                foreach (int i in l)
                                {
                                    var r = origRecs[i];
                                    float d = Vector3.Distance(Pos(r), pos);
                                    if (d > PositionTolerance) continue;
                                    if (Vector2.Distance(Uv(r), uv) > UvTolerance) continue;
                                    var on = Nrm(r);
                                    if (on.LengthSquared() > 1e-12f && Vector3.Dot(Vector3.Normalize(on), nrm) < NormalDotTolerance) continue;
                                    if (p.HasWeights && (beyond || !WeightsClose(r, qb, qw))) continue;
                                    // 0.10.0: the normal's distance (not 1 - dot) tells near-twins apart (Kobold: twins whose normals differ by 1e-4)
                                    float score = d + Vector2.Distance(Uv(r), uv) + Vector3.Distance(on.LengthSquared() > 1e-12f ? Vector3.Normalize(on) : nrm, nrm);
                                    if (score < bestD)
                                    {
                                        bestD = score;
                                        best = i;
                                    }
                                }
                            }
                    origOf[v] = best >= 0 ? cls[best] : -1;
                    string key;
                    byte[] rec;
                    if (best >= 0)
                    {
                        key = "o" + cls[best];
                        rec = FinalRecord(origRecs[cls[best]]);
                    }
                    else
                    {
                        allMatched = false;
                        moved++;
                        if (part.Unmatched.Count < 5)
                        {
                            int near = -1;
                            float nd0 = float.MaxValue;
                            for (int i = 0; i < origRecs.Length; i++)
                            {
                                float d = Vector3.Distance(Pos(origRecs[i]), pos);
                                if (d < nd0) { nd0 = d; near = i; }
                            }
                            if (near >= 0)
                            {
                                var r = origRecs[near];
                                var on = Nrm(r);
                                part.Unmatched.Add($"vertex at {pos}: nearest original {near} at {nd0:G3} yd, uv {Uv(r)} vs {uv}, normal dot {(on.LengthSquared() > 0 ? Vector3.Dot(Vector3.Normalize(on), nrm) : 1):0.0000}, weights {WeightKey(r)} vs {string.Join(",", Enumerable.Range(0, 4).Where(k => qw[k] > 0).Select(k => $"{qb[k]}:{qw[k]}"))}");
                            }
                        }
                        // uv2 from the nearest original vertex (it is unused by the 1.12 renderer, but keep it plausible)
                        var uv2 = Vector2.Zero;
                        float nd = float.MaxValue;
                        foreach (var r in origRecs)
                        {
                            float d = Vector3.DistanceSquared(Pos(r), pos);
                            if (d < nd)
                            {
                                nd = d;
                                uv2 = new Vector2(BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(40)), BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(44)));
                            }
                        }
                        var fbones = qbp.Select((x, k) => qw[k] > 0 ? ToFinalProv(x) : 0).ToArray();
                        if (fbones.Any(x => x > 255)) bonesBeyond255++;
                        rec = NewRecord(pos, nrm, uv, uv2, fbones.Select(x => (byte)Math.Min(255, x)).ToArray(), qw);
                        // Blender splits a vertex when its loop normals differ slightly: merge them back
                        var qn = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                        key = $"n{BitConverter.SingleToInt32Bits(pos.X)},{BitConverter.SingleToInt32Bits(pos.Y)},{BitConverter.SingleToInt32Bits(pos.Z)}|{BitConverter.SingleToInt32Bits(uv.X)},{BitConverter.SingleToInt32Bits(uv.Y)}|{WeightKey(rec)}|{Math.Round(qn.X, 2)},{Math.Round(qn.Y, 2)},{Math.Round(qn.Z, 2)}";
                    }
                    if (!newIndex.TryGetValue(key, out int ni))
                    {
                        ni = newRecs.Count;
                        newIndex[key] = ni;
                        newRecs.Add(rec);
                    }
                    map[v] = ni;
                }
                for (int k = 0; k + 2 < p.Triangles.Length; k += 3)
                {
                    int ia = p.Triangles[k], ib = p.Triangles[k + 1], ic = p.Triangles[k + 2];
                    if (ia >= map.Length || ib >= map.Length || ic >= map.Length) continue;
                    int x = map[ia], y = map[ib], z = map[ic];
                    if (x == y || y == z || x == z) continue;
                    tris.Add(x); tris.Add(y); tris.Add(z);
                    if (origOf[ia] >= 0 && origOf[ib] >= 0 && origOf[ic] >= 0) matchedTris.Add(Canon(origOf[ia], origOf[ib], origOf[ic]));
                    else allMatched = false;
                }
            }
            // unchanged: every vertex matched and the same (non-degenerate) triangles. Blender holds one face per set
            // of corners: a second triangle over the same corners (a back side) comes back once, which is not an edit.
            var origTris = new HashSet<(int, int, int)>();
            var origByCorners = new Dictionary<(int, int, int), List<(int, int, int)>>();
            for (int k = 0; k + 2 < local.Length; k += 3)
            {
                int x = cls[local[k]], y = cls[local[k + 1]], z = cls[local[k + 2]];
                if (x == y || y == z || x == z) continue;
                var c = Canon(x, y, z);
                if (!origTris.Add(c)) continue;
                var u = Corners(x, y, z);
                if (!origByCorners.TryGetValue(u, out var l)) origByCorners[u] = l = new List<(int, int, int)>();
                l.Add(c);
            }
            var matchedCorners = matchedTris.Select(t => Corners(t.Item1, t.Item2, t.Item3)).ToHashSet();
            bool same = allMatched && matchedTris.IsSubsetOf(origTris) && origByCorners.Keys.All(matchedCorners.Contains);
            if (same)
            {
                part.Vertices = origRecs.Length;
                part.Triangles = local.Length / 3;
                inputs.Add(keep);
                continue;
            }
            // put back the second faces over the same corners that Blender cannot hold (when their corners are all kept)
            int restored = 0;
            foreach (var (corners, faces) in origByCorners)
            {
                if (faces.Count < 2 || !matchedCorners.Contains(corners)) continue;
                foreach (var f in faces)
                {
                    if (matchedTris.Contains(f)) continue;
                    if (!newIndex.TryGetValue("o" + f.Item1, out int ia) || !newIndex.TryGetValue("o" + f.Item2, out int ib) || !newIndex.TryGetValue("o" + f.Item3, out int ic)) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    restored++;
                }
            }
            part.Restored = restored;
            anyChanged = true;
            part.Changed = true;
            part.Vertices = newRecs.Count;
            part.Triangles = tris.Count / 3;
            part.MovedOrNewVertices = moved;
            inputs.Add(new SubmeshInput { Vertices = newRecs.ToArray(), Triangles = tris.ToArray() });
        }

        // plain-language checks
        foreach (var p in newObjects.GroupBy(p => p.NodeName))
            change.Rules.Add(new ModelRule(RuleLevel.Warning,
                $"\"{p.Key}\" is a new object: the game model has no part for it, so it is not imported. To add it to the model, join it into one of the model's parts in Blender (select it, then the part, Ctrl+J), or give it one of the model's materials (for example {man.Materials.FirstOrDefault()?.Name ?? "the part's material"}): it then belongs to that part (geoset)."));
        if (em.Primitives.Any(p => p.IsCollision && p.Positions.Length > 0))
            change.Rules.Add(new ModelRule(RuleLevel.Info, "The collision mesh (COL_...) is not imported: what the player bumps into stays as it was."));
        foreach (var part in change.Parts.Where(p => p.Missing))
            change.Rules.Add(new ModelRule(RuleLevel.Warning, $"Part {part.Submesh} (geoset {part.Geoset}) is not in your file (deleted, or exported without it). It is kept as it was; to remove it, delete its faces but keep the object."));

        if (!anyChanged)
        {
            change.Unchanged = true;
            change.Detail = newObjects.Count > 0 ? "The model's own parts are unchanged; your new objects cannot be imported (see below)." : $"Unchanged: all {change.Parts.Count} parts have the same vertices and triangles as in the export.";
            if (em.FromBlender) change.Detail += " (Blender splits vertices along sharp normals and stores floats with tiny differences; that is not counted as a change.)";
            a.Changes.Add(change);
            return;
        }
        if (unknownBones.Count > 0)
            change.Rules.Add(new ModelRule(RuleLevel.Blocker,
                $"Some vertices are weighted to bones that are not in the model's skeleton ({string.Join(", ", unknownBones.Take(6))}{(unknownBones.Count > 6 ? ", ..." : "")}). To add bones, export with \"Skeleton and animations\" ticked (wds models export --with-skeleton), add them to that armature in Blender and export again; otherwise weight those vertices to the model's own bones (bone_000 ...)."));
        if (moreThan4 > 0)
            change.Rules.Add(new ModelRule(RuleLevel.Info, $"{moreThan4:N0} vertices had more than 4 bone weights: the 4 strongest were kept and scaled to sum to 255 (the game uses at most 4 per vertex)."));
        if (noWeights > 0)
            change.Rules.Add(new ModelRule(RuleLevel.Warning, $"{noWeights:N0} vertices have no bone weights (an object without the armature modifier): they were bound to their part's main bone and move with it."));
        change.Rules.Add(new ModelRule(RuleLevel.Info, "Weights are stored with 8 bits each, the strongest 4 per vertex, summing to 255 (as in the game's files)."));

        if (bonesBeyond255 > 0)
            change.Rules.Add(new ModelRule(RuleLevel.Blocker, $"{bonesBeyond255:N0} vertices are weighted to bones numbered above 255: a vertex can only use the first 256 bones of the skeleton. Weight them to other bones, or remove unused bones."));
        int total = inputs.Sum(i => i.Vertices.Length);
        change.Rules.Add(new ModelRule(total > M2MeshRebuilder.MaxVertices ? RuleLevel.Blocker : RuleLevel.Info,
            $"The model has {total:N0} vertices (the game's limit for one model is {M2MeshRebuilder.MaxVertices:N0}, and about {M2MeshRebuilder.MaxIndices / 3:N0} triangles)."));
        change.Detail = string.Join("; ", change.Parts.Where(p => p.Changed).Select(p => p.Summary));
        a.MeshInputs = inputs;
        change.OriginalBytes = a.BaseModel;
        change.CanInclude = !change.HasBlocker;
        change.Include = change.CanInclude;
        a.Changes.Add(change);
    }

    /// <summary>Weights as in <see cref="QuantizeWeights"/> but with bone numbers that may exceed 255 (new bones).</summary>
    public static (int[] Bones, byte[] Weights) QuantizeWeightsInt((int Bone, float Weight)[] infl, int fallbackBone)
    {
        var top = infl.Where(x => x.Weight > 0 && x.Bone >= 0).OrderByDescending(x => x.Weight).ThenBy(x => x.Bone).Take(4).ToArray();
        var bones = new int[4];
        var w = new byte[4];
        if (top.Length == 0)
        {
            bones[0] = Math.Max(0, fallbackBone);
            w[0] = 255;
            return (bones, w);
        }
        double sum = top.Sum(x => (double)x.Weight);
        var exact = top.Select(x => x.Weight / sum * 255.0).ToArray();
        var q = exact.Select(x => (int)Math.Floor(x)).ToArray();
        int rest = 255 - q.Sum();
        foreach (int i in Enumerable.Range(0, q.Length).OrderByDescending(i => exact[i] - q[i]).ThenBy(i => i).Take(rest)) q[i]++;
        var kept = Enumerable.Range(0, top.Length).Where(i => q[i] > 0).OrderByDescending(i => q[i]).ThenBy(i => top[i].Bone).ToArray();
        for (int k = 0; k < kept.Length; k++)
        {
            bones[k] = top[kept[k]].Bone;
            w[k] = (byte)q[kept[k]];
        }
        return (bones, w);
    }

    // ================================================================= skeleton, animations, events (0.10.0)

    private static void AnalyzeRig(ModelImportAnalysis a, EditedModel em, ModelImportContext ctx)
    {
        var man = em.Manifest;
        if (man.Rig == null || em.Rig == null) return;
        string model = Path.GetFileName(man.Model.ArchivePath);
        var change = new ModelChange { Kind = ModelChangeKind.Skeleton, Title = $"{SkeletonRowTitle} of {model}", GamePath = man.Model.ArchivePath, OriginalBytes = a.BaseModel };
        a.Changes.Add(change);
        if (a.BaseModel == null)
        {
            change.Unchanged = true;
            change.Detail = "Skeleton and animation changes cannot be matched to this model version (see the note above).";
            return;
        }
        var m = M2Model.Load(a.BaseModel);
        RigAnalysis ra;
        try { ra = RigImportAnalyzer.Analyze(m, man, em.Rig, ctx.Animations ?? AnimationCatalog.Empty, man.Model.IsCharacter); }
        catch (Exception e) when (e is RigEditException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            change.Detail = "The skeleton and animations could not be compared: " + e.Message;
            change.Rules.Add(new ModelRule(RuleLevel.Blocker, change.Detail));
            return;
        }
        a.Rig = ra;
        foreach (var n in em.Rig.Notes) change.Rules.Add(new ModelRule(RuleLevel.Info, n));
        change.Rules.AddRange(ra.Rules);
        change.Lines.AddRange(ra.Changes);
        if (!ra.Changed)
        {
            change.Unchanged = !change.HasBlocker;
            change.Detail = $"Unchanged: {m.Bones.Length} bones, {m.Sequences.Length} animations, {m.Attachments.Length} attachment points and {m.EventCount} events as in the export." +
                            (em.FromBlender ? " (Animations whose keys you did not touch are recognised and keep their exact data.)" : "");
            change.Rules.RemoveAll(r => r.Level == RuleLevel.Info && r.Text.StartsWith("Unchanged animations keep", StringComparison.Ordinal));
            return;
        }
        var parts = new List<string>();
        if (ra.NewBones + ra.RemovedBones > 0) parts.Add($"{ra.NewBones} bone(s) added, {ra.RemovedBones} removed");
        if (ra.NewSequences + ra.EditedSequences + ra.RemovedSequences > 0) parts.Add($"{ra.NewSequences} animation(s) added, {ra.EditedSequences} edited, {ra.RemovedSequences} removed");
        change.Detail = parts.Count > 0 ? string.Join("; ", parts) + "." : ra.Changes.Count == 1 ? ra.Changes[0] : $"{ra.Changes.Count} changes (listed below).";
        change.CanInclude = !change.HasBlocker;
        change.Include = change.CanInclude;
    }

    private static void AnalyzeEffects(ModelImportAnalysis a, EditedModel em)
    {
        var man = em.Manifest;
        if (man.Rig?.EffectsSha256 == null || em.EffectsJsonText == null || a.BaseModel == null) return;
        if (ModelExporter.Sha(System.Text.Encoding.UTF8.GetBytes(em.EffectsJsonText)) == man.Rig.EffectsSha256) return;
        string model = Path.GetFileName(man.Model.ArchivePath);
        var change = new ModelChange { Kind = ModelChangeKind.Effects, Title = $"{EffectsRowTitle} of {model} (advanced)", GamePath = man.Model.ArchivePath, OriginalBytes = a.BaseModel };
        var fx = EffectsJson.Analyze(M2Model.Load(a.BaseModel), em.EffectsJsonText);
        foreach (var p in fx.Problems) change.Rules.Add(new ModelRule(p.Level, p.Text));
        if (!fx.Changed && !fx.HasBlocker)
        {
            change.Unchanged = true;
            change.Detail = $"{EffectsJson.FileName} was saved again but no value differs from the model.";
        }
        else
        {
            change.Detail = fx.Changed ? string.Join("; ", fx.Changes.Take(8)) + (fx.Changes.Count > 8 ? $"; and {fx.Changes.Count - 8} more" : "") : "The file has problems.";
            change.Rules.Add(new ModelRule(RuleLevel.Info, "Only the emitters and ribbons you edited are written; an edited animated value is written again for every animation from its keys. Everything else keeps its bytes."));
            a.Effects = fx;
            change.CanInclude = !change.HasBlocker;
            change.Include = change.CanInclude;
        }
        a.Changes.Add(change);
    }

    /// <summary>
    /// 0.10.0: build the model file from the included model rows (particles and ribbons, then skeleton and animations,
    /// then the mesh), validate it and give every included model row the result. Call again after changing which rows
    /// are included.
    /// </summary>
    public static void Compose(ModelImportAnalysis a)
    {
        var rows = a.Changes.Where(c => c.IsModelPart).ToList();
        foreach (var c in rows)
        {
            foreach (var r in c.ComposeRules) c.Rules.Remove(r);
            c.ComposeRules.Clear();
            if (c.Unchanged) continue;
            c.NewBytes = null;
        }
        a.ModelBytes = null;
        var inc = rows.Where(c => c.Include && c.CanInclude && !c.Unchanged).ToList();
        if (inc.Count == 0 || a.BaseModel == null) return;
        var mesh = inc.FirstOrDefault(c => c.Kind == ModelChangeKind.Mesh);
        var rig = inc.FirstOrDefault(c => c.Kind == ModelChangeKind.Skeleton);
        var fx = inc.FirstOrDefault(c => c.Kind == ModelChangeKind.Effects);
        void All(RuleLevel l, string t)
        {
            foreach (var c in inc)
            {
                var r = new ModelRule(l, t);
                c.ComposeRules.Add(r);
                c.Rules.Add(r);
            }
        }
        if (mesh != null && rig == null && a.Rig is { Changed: true })
        {
            var r = new ModelRule(RuleLevel.Blocker, "The mesh was matched to your edited skeleton: include the skeleton and animations row too, or export again for a mesh-only edit.");
            mesh.ComposeRules.Add(r);
            mesh.Rules.Add(r);
            return;
        }
        try
        {
            var f = M2File.Parse(a.BaseModel);
            var m0 = M2Model.FromFile(f);
            if (fx != null && a.Effects != null) a.Effects.Apply(f);
            RigWriteResult? rr = null;
            if (rig != null && a.Rig != null) rr = RigWriter.Apply(f, a.Rig.Plan, a.Context?.Animations);
            byte[] bytes;
            if (mesh != null && a.MeshInputs != null)
            {
                var (b, rep) = M2MeshRebuilder.Rebuild(f.Serialize(), a.MeshInputs);
                mesh.Rebuild = rep;
                bytes = b;
                var m = M2Model.FromFile(f);
                var mr = new ModelRule(RuleLevel.Info,
                    $"All 4 views were rebuilt for the game's bone limits ({string.Join(" / ", m.Views.Select(v => v.BoneLimit))} bones per draw): " +
                    (rep.Splits.Count == 0 ? "every part fits in every view." : string.Join(" ", rep.Splits)));
                mesh.ComposeRules.Add(mr);
                mesh.Rules.Add(mr);
                var br = new ModelRule(RuleLevel.Info, rig != null
                    ? "The bounds of the model and of each animation were recomputed. Attachments, events, particles, ribbons and the collision mesh follow the skeleton."
                    : "The bounds of the model and of each animation were recomputed. Bones, animations, attachments, events, particles, ribbons and the collision mesh are copied unchanged.");
                mesh.ComposeRules.Add(br);
                mesh.Rules.Add(br);
            }
            else
            {
                if (rr != null) RigWriter.RecomputeBounds(f, rr.Relocated);
                bytes = f.Serialize();
            }
            var nm = M2Model.Load(bytes);
            var before = M2Validator.Errors(M2Validator.Validate(m0)).Select(e => e.ToString()).ToHashSet();
            var errors = M2Validator.Errors(M2Validator.Validate(nm)).Where(e => !before.Contains(e.ToString())).ToList();
            foreach (var e in errors) All(RuleLevel.Blocker, "The game would not load this model: " + e);
            if (errors.Count == 0) All(RuleLevel.Info, "The model validator found no errors.");
            if (errors.Count == 0)
            {
                a.ModelBytes = bytes;
                foreach (var c in inc) c.NewBytes = bytes;
            }
        }
        catch (Exception e) when (e is M2RebuildException or RigEditException or M2Exception or InvalidOperationException or ArgumentException)
        {
            All(RuleLevel.Blocker, e.Message);
        }
    }

    private static (int, int, int) Corners(int a, int b, int c)
    {
        Span<int> v = stackalloc int[] { a, b, c };
        v.Sort();
        return (v[0], v[1], v[2]);
    }

    private static (int, int, int) Canon(int a, int b, int c)
    {
        if (a <= b && a <= c) return (a, b, c);
        if (b <= a && b <= c) return (b, c, a);
        return (c, a, b);
    }

    // ================================================================= textures

    private static RgbaImage? Decode(EditedImage? img, out string? problem)
    {
        problem = null;
        if (img?.Bytes == null)
        {
            problem = "the image file is missing";
            return null;
        }
        if (img.Bytes.Length >= 3 && img.Bytes[0] == 0xFF && img.Bytes[1] == 0xD8)
        {
            problem = "it was saved as JPEG; WoW Data Studio reads PNG only (in Blender, set the image's file format to PNG, or export with the image format \"Automatic\")";
            return null;
        }
        try { return Png.Decode(img.Bytes); }
        catch (ImageFormatException e)
        {
            problem = "it could not be read as a PNG image: " + e.Message;
            return null;
        }
    }

    /// <summary>The pixels that were exported for a texture: the package's PNG when it is still the exported one, or the
    /// texture decoded again (the PNG was its decode).</summary>
    private static RgbaImage? Exported(PackageTexture t, EditedModel em, ModelImportContext ctx)
    {
        if (em.PackageDir != null)
        {
            var f = Path.Combine(em.PackageDir, t.Png.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(f))
            {
                try
                {
                    var img = Png.Load(f);
                    if (ModelExporter.Sha(img.Pixels) == t.PixelSha256) return img;
                }
                catch (ImageFormatException) { }
            }
        }
        if (t.GamePath == null) return null;
        var data = ctx.Textures?.ReadCurrent(t.GamePath);
        if (data == null) return null;
        try
        {
            var img = BlpFile.Parse(data).Decode(0);
            if (ModelExporter.Sha(img.Pixels) != t.PixelSha256)
            {
                var opaque = img.Clone();
                for (int i = 3; i < opaque.Pixels.Length; i += 4) opaque.Pixels[i] = 255;
                if (ModelExporter.Sha(opaque.Pixels) == t.PixelSha256) return opaque;
            }
            return img;
        }
        catch (BlpException) { return null; }
    }

    private static void AnalyzeTextures(ModelImportAnalysis a, EditedModel em, ModelImportContext ctx)
    {
        var man = em.Manifest;
        foreach (var t in man.Textures)
        {
            var img = em.ImageOfTexture.GetValueOrDefault(t.Id);
            string title = t.GamePath != null ? $"Texture {Path.GetFileName(t.GamePath)}" : $"Texture {Path.GetFileName(t.Png)} ({t.Kind})";
            var change = new ModelChange { Kind = ModelChangeKind.Texture, Title = title, GamePath = t.GamePath, Texture = t, Image = img };
            if (img == null)
            {
                change.Unchanged = true;
                change.Detail = "Not in your file: unchanged.";
                a.Changes.Add(change);
                continue;
            }
            var edited = Decode(img, out var problem);
            if (edited == null)
            {
                change.Detail = $"{img.Name}: {problem}.";
                change.Rules.Add(new ModelRule(RuleLevel.Blocker, $"{img.Name} cannot be imported: {problem}."));
                a.Changes.Add(change);
                continue;
            }
            var withEdit = new ModelChange { Kind = change.Kind, Title = change.Title, GamePath = change.GamePath, Texture = t, Image = img, EditedImage = edited };
            change = withEdit;
            bool same = ModelExporter.Sha(edited.Pixels) == t.PixelSha256 || ModelExporter.PixelSha(edited, false) == t.PixelSha256 && edited.IsOpaque();
            if (!same)
            {
                var exported = Exported(t, em, ctx);
                if (exported != null && exported.Width == edited.Width && exported.Height == edited.Height)
                {
                    var (unchanged, rule) = CompareWithExport(exported, edited, man, t);
                    same = unchanged;
                    if (rule != null) change.Rules.Add(rule);
                }
            }
            if (same)
            {
                change.Unchanged = true;
                change.Detail = $"Unchanged ({img.Name}, matched by {img.MatchedBy}).";
                a.Changes.Add(change);
                continue;
            }
            if (!t.Editable || t.GamePath == null)
            {
                change.Detail = t.Kind == "composite" ? "You edited the composite skin." : $"You edited {Path.GetFileName(t.Png)} ({t.Kind}).";
                change.Rules.Add(new ModelRule(RuleLevel.Blocker, t.Note ?? "This picture is not a game file of its own (a placeholder for a slot the game fills at run time), so it cannot be imported."));
                a.Changes.Add(change);
                continue;
            }
            change.Format = ImportFormatChoice.SameAsOriginal;
            change.GlowNote = AdditiveNote(man, t, ctx);
            PlanTexture(change, ctx);
            a.Changes.Add(change);
        }
        // images that belong to no texture of the export
        foreach (var img in em.Images.Where(i => i.TextureId == null || man.Texture(i.TextureId) == null))
        {
            if (img.Bytes == null) continue;
            var c = new ModelChange
            {
                Kind = ModelChangeKind.NewTexture, Title = $"New texture {img.Name}", Image = img,
                Detail = "A picture that is not one of the model's textures.",
            };
            c.Rules.Add(new ModelRule(RuleLevel.Blocker,
                "New textures are not supported yet: the game model has a fixed list of textures (and creature skins come from the display table). Paint onto one of the model's existing textures instead (in Blender: Texture Paint on the existing image), then export again."));
            a.Changes.Add(c);
        }
    }

    /// <summary>Is an edited texture image the same as what was exported? Only colour under pixels that are fully
    /// transparent in both may differ without counting (Blender rewrites it), and only when every material that draws the
    /// texture uses its alpha as transparency: an opaque material (blend mode 0) shows that colour, so there it is a real
    /// edit. Returns (unchanged, the plain-language rule to show or null).</summary>
    public static (bool Unchanged, ModelRule? Rule) CompareWithExport(RgbaImage exported, RgbaImage edited, ModelPackageManifest man, PackageTexture t)
    {
        var st = ImageCompare.Compare(exported, edited, compareAlpha: true);
        if (st.ChangedPixels == 0) return (true, null);
        if (st.AlphaChangedPixels != 0 || st.ColourChangedPixels != st.HiddenColourPixels) return (false, null);
        var opaque = OpaqueMaterials(man, t);
        if (opaque.Count == 0)
            return (true, new ModelRule(RuleLevel.Info, "Only invisible colour under fully transparent pixels differs from the export (Blender rewrites it); every material that draws this texture uses its alpha as transparency, so it is not counted as a change."));
        return (false, new ModelRule(RuleLevel.Info, $"Only colour under fully transparent pixels changed ({st.HiddenColourPercent:0.##} % of the pixels), but {string.Join(", ", opaque)} draws this texture opaque (its alpha is not transparency), so that colour shows in the game: counted as a change."));
    }

    /// <summary>The materials of the export that draw a texture without using its alpha as transparency (blend mode 0,
    /// opaque), by name.</summary>
    public static List<string> OpaqueMaterials(ModelPackageManifest man, PackageTexture t) =>
        man.Materials.Where(m => m.Texture == t.Id && m.Blend == 0).Select(m => m.Name).Distinct().ToList();

    /// <summary>The additive / multiply advice for a texture: its materials in this model and, through the index, in the
    /// other models that draw it.</summary>
    public static string? AdditiveNote(ModelPackageManifest man, PackageTexture t, ModelImportContext ctx)
    {
        var uses = man.Materials.Where(m => m.Texture == t.Id)
            .Select(m => new M2.TextureMaterialUse(man.Model.ArchivePath, M2.MaterialBlends.OfM2(m.Blend), M2.MaterialBlends.M2Name(m.Blend), "material")).ToList();
        if (ctx.Index != null && t.GamePath != null)
        {
            var others = ctx.Index.ModelsDrawingTexture(t.GamePath).Where(m => !string.Equals(m, man.Model.ArchivePath, StringComparison.OrdinalIgnoreCase));
            uses.AddRange(M2.MaterialBlends.ForTexture(t.GamePath, others, p => ctx.ReadCurrent(p) ?? ctx.ReadGame(p)?.Data, 30));
        }
        return M2.MaterialBlends.AdditiveNote(uses);
    }

    /// <summary>(Re)plan the conversion of an edited texture with the row's options (resize, format).</summary>
    public static void PlanTexture(ModelChange change, ModelImportContext ctx)
    {
        var t = change.Texture!;
        var edited = change.EditedImage!;
        change.Rules.Clear();
        change.Plan = null;
        change.NewBytes = null;
        if (ctx.Textures == null)
        {
            change.Rules.Add(new ModelRule(RuleLevel.Blocker, "No game folder is open."));
            change.CanInclude = change.Include = false;
            return;
        }
        try
        {
            var plan = ctx.Textures.PlanImage(edited, change.Image?.File ?? change.Image?.Name ?? t.Png, t.GamePath!, change.ResizeTo, change.Format);
            change.OriginalBytes = plan.OriginalData;
            if (plan.NeedsResize && change.ResizeTo == null)
            {
                change.NeedsResize = true;
                var (sw, sh) = plan.SuggestedSize;
                bool pow2 = ImageOps.IsPow2(edited.Width) && ImageOps.IsPow2(edited.Height) && edited.Width <= 512 && edited.Height <= 512;
                change.Rules.Add(new ModelRule(RuleLevel.Warning, pow2
                    ? $"Size: your image is {edited.Width}x{edited.Height}, the game's texture is {sw}x{sh}. It will be resized to {sw}x{sh} (choose \"Keep my size\" to keep yours: it stays a power of two, but the file size changes)."
                    : $"Size: your image is {edited.Width}x{edited.Height}. The game only loads textures whose sides are powers of two (2, 4, 8 ... 256, 512): it will be resized to {sw}x{sh}, the original size."));
                change.ResizeTo = (sw, sh);
                plan = ctx.Textures.PlanImage(edited, change.Image?.File ?? t.Png, t.GamePath!, change.ResizeTo, change.Format);
            }
            change.Plan = plan;
            change.NewBytes = plan.Encoded;
            change.AlphaQuestion = plan.Original is { AlphaDepth: 0 } && !plan.Png.IsOpaque();
            foreach (var w in plan.Warnings) change.Rules.Add(new ModelRule(RuleLevel.Warning, w));
            foreach (var n in plan.Notes) change.Rules.Add(new ModelRule(RuleLevel.Info, n));
            change.Rules.Add(new ModelRule(RuleLevel.Info, $"Will be saved as {plan.FormatName}, {plan.EncodedBlp.Width}x{plan.EncodedBlp.Height}, {plan.EncodedBlp.LevelCount} mip levels" +
                (plan.Original != null && plan.FormatName == plan.Original.FormatName ? " (the original's format)." : " (a new format).")));
            if (plan.Report != null && plan.Report.PerLevel.Count > 0 && !plan.Report.Resized)
                change.Rules.Add(new ModelRule(RuleLevel.Info, $"Only the changed blocks are re-encoded: {plan.Report.PerLevel.Sum(l => l.ReEncoded):N0} of {plan.Report.PerLevel.Sum(l => l.Blocks ?? l.Pixels ?? 0):N0}; the rest is kept byte for byte."));
            if (ctx.Index != null)
            {
                var users = ctx.Index.TextureUsers(t.GamePath!, 400);
                var models = ctx.Index.ModelsUsingTexture(t.GamePath!);
                if (users.Count > 1 || models.Count > 1)
                    change.Rules.Add(new ModelRule(RuleLevel.Warning,
                        $"Shared: this texture has {users.Count} uses ({string.Join(", ", models.Take(4).Select(Path.GetFileName))}{(models.Count > 4 ? ", ..." : "")}{(users.Count > 0 ? "; " + string.Join("; ", users.Take(3)) : "")}). All of them change."));
            }
            if (!plan.Identical && change.GlowNote != null) change.Rules.Add(new ModelRule(RuleLevel.Warning, change.GlowNote));
            change.Detail = plan.Identical ? "Converts back to the identical file: nothing to change." : plan.Diff?.Summary ?? "Edited.";
            if (plan.Identical) change.Unchanged = true;
            change.CanInclude = !plan.Identical && !change.HasBlocker;
            change.Include = change.CanInclude;
        }
        catch (Exception e) when (e is TextureException or BlpException or WorkspaceException)
        {
            change.Rules.Add(new ModelRule(RuleLevel.Blocker, e.Message));
            change.CanInclude = change.Include = false;
        }
    }
}

/// <summary>Saves the included changes of an analysis into the workspace as one change set ("Revert this import").</summary>
public static class ModelImportCommitter
{
    public static ChangeSet Commit(ModelImportAnalysis a, Workspace.Workspace ws, AssetIndex? index, string? label = null)
    {
        var rows = a.Changes.Where(c => c.Include && c.CanInclude && !c.Unchanged && c.NewBytes != null && c.GamePath != null).ToList();
        if (rows.Count == 0) throw new WorkspaceException("Nothing is selected to import.");
        // the model rows (mesh, skeleton and animations, effects) are one file: written once
        var modelRows = rows.Where(c => c.IsModelPart).ToList();
        if (modelRows.Count > 1)
        {
            var first = modelRows[0];
            rows = rows.Where(c => !c.IsModelPart || ReferenceEquals(c, first)).ToList();
        }
        var cs = ws.BeginChangeSet(label ?? $"Model import: {Path.GetFileName(a.ModelPath)} ({rows.Count} file{(rows.Count == 1 ? "" : "s")})");
        foreach (var c in rows)
        {
            if (c.Kind == ModelChangeKind.Texture && c.Plan != null)
            {
                var p = c.Plan;
                var origin = new OriginInfo
                {
                    Archive = p.OriginalArchive, Sha256 = p.OriginalData == null ? null : Workspace.Workspace.Sha256(p.OriginalData), Size = p.OriginalData?.LongLength ?? 0,
                    Format = p.Original?.FormatName, Width = p.Original?.Width ?? 0, Height = p.Original?.Height ?? 0, Levels = p.Original?.LevelCount ?? 0, IsNew = p.Original == null,
                };
                if (index != null)
                {
                    origin.Models = index.ModelsUsingTexture(p.TargetPath);
                    origin.UsedBy = index.TextureUsers(p.TargetPath, 50);
                }
                string summary = (p.Diff?.Summary ?? "") + $" From the model package {a.Edited.Description}.";
                ws.SaveEdit(p.TargetPath, p.Encoded, origin, cs, c.Image?.File, summary.Trim());
            }
            else if (c.IsModelPart)
            {
                var game = a.Context.ReadGame(c.GamePath!);
                var origin = new OriginInfo
                {
                    Archive = game?.Archive, Sha256 = game == null ? null : Workspace.Workspace.Sha256(game.Value.Data), Size = game?.Data.LongLength ?? 0,
                    Format = $"M2 version 0x{M2File.Parse(c.NewBytes!).Version:x}", IsNew = game == null,
                    Models = new List<string> { c.GamePath! },
                    UsedBy = string.IsNullOrEmpty(a.Manifest.Users.Summary) ? new List<string>() : new List<string> { a.Manifest.Users.Summary },
                };
                var what = a.Changes.Where(x => x.IsModelPart && x.Include && x.CanInclude && !x.Unchanged && x.NewBytes != null)
                    .Select(x => (x.Kind switch { ModelChangeKind.Mesh => "Mesh: ", ModelChangeKind.Skeleton => "Skeleton and animations: ", _ => "Particles and ribbons: " }) + x.Detail);
                ws.SaveEdit(c.GamePath!, c.NewBytes!, origin, cs, a.Edited.GltfPath, string.Join(" | ", what));
            }
        }
        return cs;
    }
}
