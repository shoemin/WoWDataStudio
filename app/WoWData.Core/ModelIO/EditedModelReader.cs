using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WoWData.Core.ModelIO;

/// <summary>One drawn piece of the edited file (a glTF primitive), already in M2 coordinates.</summary>
public sealed class EditedPrimitive
{
    public int Node { get; init; }
    public string NodeName { get; init; } = "";
    public int Mesh { get; init; }
    public int PrimitiveIndex { get; init; }
    /// <summary>The view-0 submesh it belongs to, or -1 for a new object that could not be matched.</summary>
    public int Submesh { get; set; } = -1;
    public string MatchedBy { get; set; } = "";
    public string? MaterialId { get; init; }
    public string? MaterialName { get; init; }
    public bool IsCollision { get; init; }
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public Vector2[] Uvs { get; init; } = Array.Empty<Vector2>();
    /// <summary>Per vertex: (bone, weight) sorted by weight, weights summing to 1; bone -1 = a joint that is not a bone of
    /// the original skeleton (see <see cref="UnknownBones"/>).</summary>
    public (int Bone, float Weight)[][] Influences { get; init; } = Array.Empty<(int, float)[]>();
    public int[] Triangles { get; init; } = Array.Empty<int>();
    public bool HasWeights { get; init; }
    public List<string> UnknownBones { get; } = new();
}

/// <summary>An image of the edited file and the game texture it was matched to.</summary>
public sealed class EditedImage
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string? File { get; init; }
    public byte[]? Bytes { get; init; }
    public string? TextureId { get; set; }
    public string MatchedBy { get; set; } = "";
}

/// <summary>What was read from an edited model: the manifest of its export, its meshes in M2 coordinates and its
/// images matched to the manifest's textures.</summary>
public sealed class EditedModel
{
    public string InputPath { get; init; } = "";
    public string GltfPath { get; init; } = "";
    public string Description { get; set; } = "";
    public bool FromBlender { get; set; }
    public bool IsOriginalPackage { get; set; }
    public ModelPackageManifest Manifest { get; set; } = new();
    public string ManifestSource { get; set; } = "";
    public string? PackageDir { get; set; }
    public List<EditedPrimitive> Primitives { get; } = new();
    public List<EditedImage> Images { get; } = new();
    public Dictionary<string, EditedImage> ImageOfTexture { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = new();
    public string Generator { get; set; } = "";
    /// <summary>0.10.0: the skeleton, animations, attachment points and events (when the export had them).</summary>
    public EditedRig? Rig { get; set; }
    /// <summary>0.10.0: the particles and ribbons file (advanced editing), when the package has one.</summary>
    public string? EffectsJsonText { get; set; }
    public string? EffectsJsonPath { get; set; }
}

/// <summary>
/// Reads what comes back for "Import edited model...": a folder (the export package with edited PNGs, or the
/// blender_export folder the add-on writes), a .gltf or a .glb. Undoes the axis mapping and whatever transform the
/// model's root got in Blender (the add-on's import rotation and scale, or a move): positions come out in M2 coordinates
/// as the rest pose of the skinned mesh (sum of weight x joint matrix x inverse bind matrix, then the root's inverse),
/// so bone orientations Blender adds on import do not matter. Joints are mapped back to bones by the wow_bone extras
/// (Blender custom properties) or the bone_NNN names; primitives to submeshes by their material's wow_submesh, the
/// mesh's wow_submesh, or the _sNN node name.
/// </summary>
public static class EditedModelReader
{
    private static readonly Regex BoneName = new(@"^bone_(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex SubmeshName = new(@"_s(\d+)(\.\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>The glTF file to read for a picked path (a file, a package folder, or a blender_export folder).</summary>
    public static string ResolveInput(string path, out string description)
    {
        if (File.Exists(path))
        {
            description = Path.GetFileName(path);
            return path;
        }
        if (!Directory.Exists(path)) throw new GltfException($"{path} does not exist.");
        static string? Newest(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        var be = Newest(Path.Combine(path, "blender_export"));
        if (be != null)
        {
            description = $"the Blender export in {Path.GetFileName(path)}\\blender_export ({Path.GetFileName(be)})";
            return be;
        }
        var own = Newest(path);
        if (own != null)
        {
            description = File.Exists(Path.Combine(path, ModelPackageManifest.FileName)) && !File.Exists(Path.Combine(path, "wowdata_blender.json"))
                ? $"the export package {Path.GetFileName(path)} (with your edited PNGs)"
                : $"{Path.GetFileName(own)} in {Path.GetFileName(path)}";
            return own;
        }
        throw new GltfException($"There is no .gltf or .glb file in {path}. Pick the folder made by \"Export for Blender\", the file Blender exported, or that file's folder.");
    }

    public static EditedModel Read(string inputPath)
    {
        var file = ResolveInput(inputPath, out var desc);
        var g = GltfFile.Load(file);
        var em = new EditedModel { InputPath = inputPath, GltfPath = file, Description = desc };
        em.Generator = GltfFile.StrOf(g.Json["asset"]?["generator"]) ?? "";
        em.FromBlender = em.Generator.Contains("Blender", StringComparison.OrdinalIgnoreCase);
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;

        // ------------------------------------------------ the manifest: embedded (root extras), next to the file, or one folder up
        int root = -1;
        for (int i = 0; i < g.NodeCount && root < 0; i++)
            if (GltfFile.Extras(g.Node(i)) is { } ex && (ex["wow_manifest"] != null || ex["wow_model"] != null)) root = i;
        ModelPackageManifest? manifest = null;
        if (root >= 0 && GltfFile.StrOf(GltfFile.Extras(g.Node(root))!["wow_manifest"]) is { } embedded)
        {
            manifest = ModelPackageManifest.FromJson(embedded);
            if (manifest != null) em.ManifestSource = "embedded in the glTF file";
        }
        foreach (var cand in new[] { Path.Combine(dir, ModelPackageManifest.FileName), Path.Combine(Path.GetDirectoryName(dir) ?? dir, ModelPackageManifest.FileName) })
        {
            if (File.Exists(cand))
            {
                var m = ModelPackageManifest.FromJson(File.ReadAllText(cand));
                if (m != null)
                {
                    em.PackageDir ??= Path.GetDirectoryName(cand);
                    if (manifest == null)
                    {
                        manifest = m;
                        em.ManifestSource = cand;
                    }
                }
            }
        }
        em.Manifest = manifest ?? throw new GltfException(
            "This file has no WoW Data Studio manifest (wowdata_manifest.json), so it is not known which game model it belongs to. " +
            "Import the folder made by \"Export for Blender\", or a file Blender exported from it with File > Export > WoW 1.12 model for WoW Data Studio.");
        em.IsOriginalPackage = !em.FromBlender && em.Manifest.GltfFiles.Any(f => string.Equals(f.File, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
        if (em.PackageDir == null && em.IsOriginalPackage) em.PackageDir = dir;

        // ------------------------------------------------ transforms
        var rootInv = Matrix4x4.Identity;
        if (root >= 0 && Matrix4x4.Invert(g.World(root), out var inv)) rootInv = inv;
        else if (root < 0) em.Notes.Add("The model's root object was not found in the file; positions are taken as they are.");

        // 0.10.0: the rig (the add-on's rig file next to the glTF, else the glTF's own joints and animations)
        Dictionary<string, int>? provisional = null;
        if (em.Manifest.Rig is { } mrig)
        {
            var side = Path.Combine(dir, RigSidecarFile.FileName);
            try
            {
                em.Rig = File.Exists(side) ? RigSidecarFile.Read(side) : g.Arr("skins").Count > 0 ? RigGltfReader.Read(g, root, em.Manifest) : null;
            }
            catch (Exception e) when (e is GltfException or System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException or IndexOutOfRangeException)
            {
                em.Notes.Add($"The skeleton and animations could not be read ({e.Message}); they are kept as they are.");
            }
            if (em.Rig != null) provisional = ModelIO.RigImportAnalyzer.ProvisionalIndices(em.Rig, em.Manifest.Model.Bones, mrig);
            if (mrig.EffectsFile is { } fx)
                foreach (var cand in new[] { Path.Combine(dir, fx), em.PackageDir == null ? "" : Path.Combine(em.PackageDir, fx), Path.Combine(Path.GetDirectoryName(dir) ?? dir, fx) })
                    if (cand.Length > 0 && File.Exists(cand))
                    {
                        em.EffectsJsonText = File.ReadAllText(cand);
                        em.EffectsJsonPath = cand;
                        break;
                    }
        }

        // joints -> bones
        var skins = g.Arr("skins");
        var skinData = new List<(int[] Bones, string[] Names, Matrix4x4[] JointMatrix)>();
        for (int s = 0; s < skins.Count; s++)
        {
            var sk = skins[s]!.AsObject();
            var joints = sk["joints"]!.AsArray().Select(j => GltfFile.IntOf(j) ?? 0).ToArray();
            var ibm = new Matrix4x4[joints.Length];
            if (GltfFile.IntOf(sk["inverseBindMatrices"]) is int ia)
            {
                var d = g.Read(ia, out _);
                for (int j = 0; j < joints.Length; j++)
                {
                    var f = d.AsSpan(j * 16, 16);
                    ibm[j] = new Matrix4x4((float)f[0], (float)f[1], (float)f[2], (float)f[3], (float)f[4], (float)f[5], (float)f[6], (float)f[7],
                        (float)f[8], (float)f[9], (float)f[10], (float)f[11], (float)f[12], (float)f[13], (float)f[14], (float)f[15]);
                }
            }
            else Array.Fill(ibm, Matrix4x4.Identity);
            var bones = new int[joints.Length];
            var names = new string[joints.Length];
            var jm = new Matrix4x4[joints.Length];
            for (int j = 0; j < joints.Length; j++)
            {
                var n = g.Node(joints[j]);
                names[j] = GltfFile.StrOf(n["name"]) ?? $"joint {j}";
                if (provisional != null)
                {
                    // the rig decides: original bones keep their index, new bones follow the original count
                    bones[j] = provisional.TryGetValue(names[j], out int pi) ? pi : -1;
                    jm[j] = ibm[j] * g.World(joints[j]);
                    continue;
                }
                int? b = GltfFile.IntOf(GltfFile.Extras(n)?["wow_bone"]);
                if (b == null && BoneName.Match(names[j]) is { Success: true } mm) b = int.Parse(mm.Groups[1].Value);
                bones[j] = b is int bi && bi >= 0 && bi < em.Manifest.Model.Bones ? bi : -1;
                jm[j] = ibm[j] * g.World(joints[j]);
            }
            skinData.Add((bones, names, jm));
        }

        // ------------------------------------------------ materials -> ids
        var mats = g.Arr("materials");
        var matSubmesh = new int?[mats.Count];
        var matId = new string?[mats.Count];
        var matTexture = new string?[mats.Count];
        for (int i = 0; i < mats.Count; i++)
        {
            var ex = GltfFile.Extras(mats[i]);
            matSubmesh[i] = GltfFile.IntOf(ex?["wow_submesh"]);
            matId[i] = GltfFile.StrOf(ex?["wow_material_id"]);
            matTexture[i] = GltfFile.StrOf(ex?["wow_texture_id"]);
        }

        // ------------------------------------------------ meshes
        foreach (int ni in g.NodesWithMesh())
        {
            var node = g.Node(ni);
            int mi = GltfFile.IntOf(node["mesh"])!.Value;
            var mesh = g.Arr("meshes")[mi]!.AsObject();
            string nodeName = GltfFile.StrOf(node["name"]) ?? GltfFile.StrOf(mesh["name"]) ?? $"node {ni}";
            var nex = GltfFile.Extras(node);
            var mex = GltfFile.Extras(mesh);
            bool collision = nex?["wow_collision"] != null || mex?["wow_collision"] != null || nodeName.StartsWith("COL_", StringComparison.OrdinalIgnoreCase);
            int? nodeSub = GltfFile.IntOf(nex?["wow_submesh"]) ?? GltfFile.IntOf(mex?["wow_submesh"]);
            int? nameSub = SubmeshName.Match(nodeName) is { Success: true } sm ? int.Parse(sm.Groups[1].Value) : null;
            int? skinIdx = GltfFile.IntOf(node["skin"]);
            var nodeWorld = g.World(ni);
            var prims = mesh["primitives"]!.AsArray();
            for (int pi = 0; pi < prims.Count; pi++)
            {
                var p = prims[pi]!.AsObject();
                int mode = GltfFile.IntOf(p["mode"]) ?? 4;
                var attrs = p["attributes"]!.AsObject();
                if (GltfFile.IntOf(attrs["POSITION"]) is not int pa) continue;
                if (mode != 4)
                {
                    em.Notes.Add($"{nodeName}: drawn as {(mode == 0 ? "points" : mode <= 3 ? "lines" : "triangle strips/fans")}, not triangles; it was skipped.");
                    continue;
                }
                var pos = g.Read(pa, out _);
                int n = pos.Length / 3;
                double[]? nrm = GltfFile.IntOf(attrs["NORMAL"]) is int na ? g.Read(na, out _) : null;
                double[]? uv = GltfFile.IntOf(attrs["TEXCOORD_0"]) is int ua ? g.Read(ua, out _) : null;
                var tri = GltfFile.IntOf(p["indices"]) is int ia ? g.ReadInts(ia) : Enumerable.Range(0, n).ToArray();
                // joints / weights (up to 8 influences)
                var infl = new (int Bone, float Weight)[n][];
                bool hasWeights = false;
                var unknown = new SortedSet<string>(StringComparer.Ordinal);
                var matrices = new Matrix4x4[n];
                if (skinIdx is int si && si < skinData.Count && attrs["JOINTS_0"] != null && attrs["WEIGHTS_0"] != null)
                {
                    hasWeights = true;
                    var (bones, names, jm) = skinData[si];
                    var sets = new List<(double[] J, double[] W)>();
                    for (int set = 0; set < 4; set++)
                    {
                        if (GltfFile.IntOf(attrs[$"JOINTS_{set}"]) is not int ja || GltfFile.IntOf(attrs[$"WEIGHTS_{set}"]) is not int wa) break;
                        sets.Add((g.Read(ja, out _), g.Read(wa, out _)));
                    }
                    for (int v = 0; v < n; v++)
                    {
                        var acc = new Dictionary<int, double>();
                        var m = new Matrix4x4();
                        double total = 0;
                        foreach (var (J, W) in sets)
                            for (int k = 0; k < 4; k++)
                            {
                                double w = W[v * 4 + k];
                                if (w <= 0) continue;
                                int j = (int)J[v * 4 + k];
                                if (j < 0 || j >= bones.Length) continue;
                                total += w;
                                m += jm[j] * (float)w;
                                int b = bones[j];
                                if (b < 0) unknown.Add(names[j]);
                                acc[b] = acc.GetValueOrDefault(b) + w;
                            }
                        if (total <= 0)
                        {
                            infl[v] = Array.Empty<(int, float)>();
                            matrices[v] = nodeWorld;
                        }
                        else
                        {
                            matrices[v] = m * (float)(1.0 / total);
                            infl[v] = acc.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, (float)(kv.Value / total))).ToArray();
                        }
                    }
                }
                else
                {
                    for (int v = 0; v < n; v++)
                    {
                        infl[v] = Array.Empty<(int, float)>();
                        matrices[v] = nodeWorld;
                    }
                }
                var P = new Vector3[n];
                var N = new Vector3[n];
                var U = new Vector2[n];
                for (int v = 0; v < n; v++)
                {
                    var world = matrices[v] * rootInv;
                    var gp = Vector3.Transform(new Vector3((float)pos[v * 3], (float)pos[v * 3 + 1], (float)pos[v * 3 + 2]), world);
                    P[v] = ModelExporter.FromGltf(gp);
                    if (nrm != null)
                    {
                        var gn = Vector3.TransformNormal(new Vector3((float)nrm[v * 3], (float)nrm[v * 3 + 1], (float)nrm[v * 3 + 2]), world);
                        float ln = gn.Length();
                        N[v] = ModelExporter.FromGltf(ln > 1e-9f ? gn / ln : new Vector3(0, 1, 0));
                    }
                    if (uv != null) U[v] = new Vector2((float)uv[v * 2], (float)uv[v * 2 + 1]);
                }
                int? mat = GltfFile.IntOf(p["material"]);
                var prim = new EditedPrimitive
                {
                    Node = ni, NodeName = nodeName, Mesh = mi, PrimitiveIndex = pi, IsCollision = collision,
                    MaterialId = mat is int m1 && m1 < mats.Count ? matId[m1] : null,
                    MaterialName = mat is int m2 && m2 < mats.Count ? GltfFile.StrOf(mats[m2]!["name"]) : null,
                    Positions = P, Normals = nrm != null ? N : ComputeNormals(P, tri), Uvs = U, Influences = infl, Triangles = tri, HasWeights = hasWeights,
                };
                prim.UnknownBones.AddRange(unknown);
                if (!collision)
                {
                    if (mat is int m3 && m3 < mats.Count && matSubmesh[m3] is int ms)
                    {
                        prim.Submesh = ms;
                        prim.MatchedBy = "its material (" + (prim.MaterialName ?? matId[m3]) + ")";
                    }
                    else if (nodeSub is int ns)
                    {
                        prim.Submesh = ns;
                        prim.MatchedBy = "the object's id (custom property wow_submesh)";
                    }
                    else if (nameSub is int nm)
                    {
                        prim.Submesh = nm;
                        prim.MatchedBy = "the object's name";
                    }
                    if (prim.Submesh >= 0 && !em.Manifest.Submeshes.Any(s => s.Index == prim.Submesh))
                    {
                        prim.MatchedBy = "";
                        prim.Submesh = -1;
                    }
                }
                em.Primitives.Add(prim);
            }
        }

        // ------------------------------------------------ images -> textures
        var images = g.Arr("images");
        var texArr = g.Arr("textures");
        Dictionary<string, JsonElement>? blenderMap = null;
        var mapFile = Path.Combine(dir, "wowdata_blender.json");
        if (File.Exists(mapFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mapFile));
                if (doc.RootElement.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Object)
                    blenderMap = im.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException) { }
        }
        for (int i = 0; i < images.Count; i++)
        {
            var io = images[i]!.AsObject();
            var img = new EditedImage
            {
                Index = i, Name = GltfFile.StrOf(io["name"]) ?? GltfFile.StrOf(io["uri"]) ?? $"image {i}", File = g.ImageFile(i), Bytes = g.ImageBytes(i),
            };
            if (GltfFile.StrOf(GltfFile.Extras(io)?["wow_texture_id"]) is { } tid)
            {
                img.TextureId = tid;
                img.MatchedBy = "its id";
            }
            else if (blenderMap != null && GltfFile.StrOf(io["uri"]) is { } uri && blenderMap.TryGetValue(uri, out var e) && e.TryGetProperty("texture_id", out var t2))
            {
                img.TextureId = t2.GetString();
                img.MatchedBy = "the add-on's image list";
            }
            em.Images.Add(img);
        }
        // by the materials that use them
        for (int mi = 0; mi < mats.Count; mi++)
        {
            if (matTexture[mi] is not { } tid) continue;
            if (GltfFile.IntOf(mats[mi]!["pbrMetallicRoughness"]?["baseColorTexture"]?["index"]) is not int ti || ti >= texArr.Count) continue;
            if (GltfFile.IntOf(texArr[ti]!["source"]) is not int src || src >= em.Images.Count) continue;
            var img = em.Images[src];
            if (img.TextureId == null)
            {
                img.TextureId = tid;
                img.MatchedBy = "the material that uses it";
            }
            else if (img.TextureId != tid)
                em.Notes.Add($"The image {img.Name} is used by materials of two different textures ({img.TextureId} and {tid}); it is taken as {img.TextureId}.");
        }
        // by name (an image named like the exported PNG)
        foreach (var img in em.Images.Where(x => x.TextureId == null))
        {
            var t = em.Manifest.Textures.FirstOrDefault(t => string.Equals(Path.GetFileNameWithoutExtension(t.Png), Path.GetFileNameWithoutExtension(img.Name), StringComparison.OrdinalIgnoreCase));
            if (t != null)
            {
                img.TextureId = t.Id;
                img.MatchedBy = "its name";
            }
        }
        foreach (var img in em.Images)
        {
            if (img.TextureId == null || em.Manifest.Texture(img.TextureId) == null) continue;
            if (!em.ImageOfTexture.ContainsKey(img.TextureId)) em.ImageOfTexture[img.TextureId] = img;
        }
        // the export package itself: the PNGs listed in the manifest (edited in an image editor)
        if (em.IsOriginalPackage && em.PackageDir != null)
            foreach (var t in em.Manifest.Textures)
            {
                var f = Path.Combine(em.PackageDir, t.Png.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) continue;
                var existing = em.ImageOfTexture.GetValueOrDefault(t.Id);
                if (existing != null && string.Equals(existing.File, f, StringComparison.OrdinalIgnoreCase)) continue;
                em.ImageOfTexture[t.Id] = new EditedImage { Name = Path.GetFileName(f), File = f, Bytes = File.ReadAllBytes(f), TextureId = t.Id, MatchedBy = "the manifest" };
            }
        return em;
    }

    /// <summary>Smooth vertex normals (when a file has none).</summary>
    public static Vector3[] ComputeNormals(Vector3[] p, int[] tri)
    {
        var n = new Vector3[p.Length];
        for (int k = 0; k + 2 < tri.Length; k += 3)
        {
            int a = tri[k], b = tri[k + 1], c = tri[k + 2];
            if (a >= p.Length || b >= p.Length || c >= p.Length) continue;
            var f = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            n[a] += f; n[b] += f; n[c] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 1e-20f ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }
}
