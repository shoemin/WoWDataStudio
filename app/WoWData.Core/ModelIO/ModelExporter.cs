using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WoWData.Core.Imaging;
using WoWData.Core.M2;

namespace WoWData.Core.ModelIO;

/// <summary>One look (skin) of a model for the export: who uses it and the textures of its replaceable slots.</summary>
public sealed class ExportLook
{
    public string Name { get; init; } = "";
    /// <summary>"creature display", "item display", "character look", "model".</summary>
    public string Kind { get; init; } = "model";
    public List<long> Ids { get; init; } = new();
    public string Detail { get; init; } = "";
    public ModelLook Look { get; init; } = new();
}

public sealed class ModelExportRequest
{
    public string ModelPath { get; init; } = "";
    public byte[] ModelData { get; init; } = Array.Empty<byte>();
    /// <summary>The model already read (another version opened read only: the modern reader's model); null = read
    /// <see cref="ModelData"/> as a 1.12 model.</summary>
    public M2Model? Model { get; init; }
    /// <summary>"game" or "workspace" (the model is an earlier edit of yours).</summary>
    public string Source { get; init; } = "game";
    public string? Archive { get; init; }
    public string? GameSha256 { get; init; }
    public List<ExportLook> Looks { get; init; } = new();
    public int CurrentLook { get; init; }
    public ModelExportMode Mode { get; init; } = ModelExportMode.CurrentLook;
    public HashSet<int>? VisibleGeosets { get; init; }
    public Func<int, string> AnimationName { get; init; } = id => $"Anim{id}";
    /// <summary>Decoded texture by game path (your edit first, then the game's), or null when missing.</summary>
    public Func<string, RgbaImage?> LoadTexture { get; init; } = _ => null;
    /// <summary>Format and checksum of a texture file by game path (for the manifest).</summary>
    public Func<string, PackageTextureOriginal?> DescribeTexture { get; init; } = _ => null;
    public bool IncludeAnimations { get; init; } = true;
    /// <summary>The parent folder; the package is &lt;OutputDir&gt;\&lt;Model&gt;_export.</summary>
    public string OutputDir { get; init; } = "";
    public string? PackageFolder { get; init; }
    public string UsersSummary { get; init; } = "";
    public int UsersCount { get; init; }
    public string? UsersWarning { get; init; }
    public bool IsCharacter { get; init; }
    /// <summary>0.10.0: "Skeleton and animations (for editing)": the rig description in the manifest, the global sequences
    /// and the events as their own actions / empties, and the particles and ribbons as JSON.</summary>
    public bool IncludeSkeleton { get; init; }
    /// <summary>AnimationData (names, flags, fallbacks); null = names from <see cref="AnimationName"/> only.</summary>
    public AnimationCatalog? Animations { get; init; }
}

public sealed class ModelExportResult
{
    public string Folder { get; init; } = "";
    public List<string> GltfFiles { get; } = new();
    public string ManifestPath { get; init; } = "";
    public ModelPackageManifest Manifest { get; init; } = new();
    public List<string> Notes { get; } = new();
    public int TextureCount => Manifest.Textures.Count;
}

/// <summary>
/// "Export for Blender": an M2 model -> a package folder &lt;Model&gt;_export\ with &lt;Model&gt;.gltf + .bin, textures\*.png and
/// wowdata_manifest.json. A port of wowdata/gltf.py (same coordinates, skeleton, skinning, animations, materials) with
/// what the round trip needs on top:
///  * ids on every part, in the glTF extras (Blender keeps them as custom properties): wow_submesh / wow_mesh_id on
///    meshes, wow_bone on bones, wow_material_id / wow_submesh / wow_texture_id on materials, and the manifest itself
///    (wow_manifest) on the root node, so a lone .glb exported by Blender still carries everything;
///  * one material per submesh, so the material also tells which part of the model a triangle belongs to;
///  * all skins as KHR_materials_variants (or one file per skin), each PNG mapped to its game BLP in the manifest with
///    its original format and a pixel fingerprint.
/// Coordinates: glTF (x, y, z) = M2 (x, z, -y); Blender's importer converts back, so Blender shows M2 coordinates.
/// </summary>
public static class ModelExporter
{
    public const float AlphaKeyCutoff = 224f / 255f;
    private const int Repeat = 10497, Clamp = 33071;

    public static string SafeName(string s)
    {
        var r = Regex.Replace(s, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
        return r.Length == 0 ? "model" : r;
    }

    public static Vector3 ToGltf(Vector3 m) => new(m.X, m.Z, -m.Y);
    public static Vector3 FromGltf(Vector3 g) => new(g.X, -g.Z, g.Y);
    private static Quaternion ToGltf(Quaternion q) => new(q.X, q.Z, -q.Y, q.W);
    private static Vector3 ToGltfScale(Vector3 s) => new(s.X, s.Z, s.Y);

    /// <summary>The vertices of view-0 submesh <paramref name="s"/> as exported (sorted view vertices: its range plus
    /// any vertex its triangles use) and its triangles as indices into that list.</summary>
    public static (int[] ViewVerts, int[] Local) SubmeshVertices(M2View view, M2Submesh s)
    {
        var set = new SortedSet<int>();
        for (int i = s.VertexStart; i < s.VertexStart + s.VertexCount; i++) set.Add(i);
        int end = Math.Min(view.Triangles.Length, s.IndexStart + s.IndexCount);
        for (int k = s.IndexStart; k < end; k++) set.Add(view.Triangles[k]);
        var vv = set.ToArray();
        var pos = new Dictionary<int, int>(vv.Length);
        for (int i = 0; i < vv.Length; i++) pos[vv[i]] = i;
        var local = new int[Math.Max(0, end - s.IndexStart)];
        for (int k = s.IndexStart; k < end; k++) local[k - s.IndexStart] = pos[view.Triangles[k]];
        return (vv, local);
    }

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>SHA-256 of the pixels as read back from the PNG (alpha forced to 255 when written without alpha).</summary>
    public static string PixelSha(RgbaImage img, bool withAlpha)
    {
        if (withAlpha) return Sha(img.Pixels);
        var p = (byte[])img.Pixels.Clone();
        for (int i = 3; i < p.Length; i += 4) p[i] = 255;
        return Sha(p);
    }

    /// <summary>Checksums of the parts of a model (the manifest's model.checksums).</summary>
    public static Dictionary<string, string> PartChecksums(M2File f)
    {
        string Block(params string[] names)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var n in names) Hash(h, f[n]);
            return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }
        return new Dictionary<string, string>
        {
            ["vertices"] = Block("vertices"),
            ["views"] = Block("views", "bone_lookup"),
            ["skeleton_and_animations"] = Block("bones", "key_bone_lookup", "animation_lookup", "playable_lookup", "global_sequences"),
            ["sequences"] = Block("sequences"),
            ["materials"] = Block("textures", "render_flags", "texture_lookup", "tex_unit_lookup", "transparency_lookup", "texture_transform_lookup", "replaceable_lookup", "colors", "transparency", "texture_transforms"),
            ["attachments_events_effects"] = Block("attachments", "attachment_lookup", "events", "lights", "cameras", "camera_lookup", "ribbons", "particles"),
            ["collision"] = Block("collision_triangles", "collision_vertices", "collision_normals"),
        };
    }

    private static void Hash(IncrementalHash h, M2Data d)
    {
        h.AppendData(BitConverter.GetBytes(d.Count));
        // the bytes of a record array hold the original offsets of its sub-arrays: hash the contents instead
        if (d.Elem.Rec is { Arrays.Count: > 0 } rec)
        {
            var b = (byte[])d.Bytes.Clone();
            for (int i = 0; i < d.Count; i++)
                foreach (var a in rec.Arrays)
                {
                    Array.Clear(b, i * rec.Size + a.Offset, 8);
                    Hash(h, d.Sub[i][a.Index]);
                }
            h.AppendData(b);
        }
        else h.AppendData(d.Bytes);
    }

    // ---------------------------------------------------------------- export

    private sealed class TexEntry
    {
        public PackageTexture Info = null!;
        public RgbaImage Image = null!;
        public bool WithAlpha;
        public bool WrapU, WrapV;
    }

    public static ModelExportResult Export(ModelExportRequest req)
    {
        var m = req.Model ?? M2Model.Load(req.ModelData);
        var f = m.File;
        string name = SafeName(Path.GetFileNameWithoutExtension(req.ModelPath));
        string folder = req.PackageFolder ?? Path.Combine(req.OutputDir, name + "_export");
        Directory.CreateDirectory(Path.Combine(folder, "textures"));
        var notes = new List<string>();

        // ------------------------------------------------ looks -> variants (looks with the same textures are merged)
        var looks = req.Looks.Count > 0 ? req.Looks : new List<ExportLook> { new() { Name = name, Kind = "model" } };
        int current = Math.Clamp(req.CurrentLook, 0, looks.Count - 1);
        var used = req.Mode == ModelExportMode.CurrentLook ? new List<ExportLook> { looks[current] } : looks;
        var variants = new List<(PackageVariant Info, ExportLook Look)>();
        var bySig = new Dictionary<string, int>();
        var variantOfLook = new Dictionary<ExportLook, int>();
        foreach (var l in used)
        {
            string sig = string.Join("|", l.Look.Slots.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + (kv.Value.Path ?? kv.Value.Source)));
            if (bySig.TryGetValue(sig, out int vi))
            {
                variants[vi].Info.Ids.AddRange(l.Ids);
                variantOfLook[l] = vi;
                continue;
            }
            variantOfLook[l] = variants.Count;
            bySig[sig] = variants.Count;
            variants.Add((new PackageVariant { Index = variants.Count, Name = l.Name, Kind = l.Kind, Ids = new List<long>(l.Ids), Detail = l.Detail }, l));
        }
        foreach (var (info, _) in variants)
            if (info.Ids.Count > 1 && info.Kind != "character look") info.Detail = $"{info.Ids.Count} {info.Kind}s: {string.Join(", ", info.Ids.Take(8))}{(info.Ids.Count > 8 ? ", ..." : "")}";
        // unique variant names (the Blender variants panel lists them by name)
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (info, _) in variants)
        {
            var n = info.Name.Length == 0 ? $"Look {info.Index + 1}" : info.Name;
            string u = n;
            for (int k = 2; !seenNames.Add(u); k++) u = $"{n} #{k}";
            info.Name = u;
        }

        // ------------------------------------------------ textures (one entry per game file / composite / placeholder)
        var texEntries = new List<TexEntry>();
        var texByKey = new Dictionary<string, TexEntry>(StringComparer.OrdinalIgnoreCase);
        var pngNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string UniquePng(string stem)
        {
            stem = SafeName(stem);
            string s = stem;
            for (int k = 2; !pngNames.Add(s); k++) s = $"{stem}_{k}";
            return "textures/" + s + ".png";
        }
        TexEntry Entry(string key, Func<TexEntry> make)
        {
            if (texByKey.TryGetValue(key, out var e)) return e;
            e = make();
            e.Info.Id = $"t{texEntries.Count:00}";
            texByKey[key] = e;
            texEntries.Add(e);
            return e;
        }
        TexEntry Placeholder(int ti, int type, string why)
        {
            return Entry($"placeholder:{ti}:{type}", () =>
            {
                var img = new RgbaImage(8, 8);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int o = img.Offset(x, y);
                        bool missingFile = type == 0;
                        byte r = missingFile ? (byte)200 : (byte)128, g = missingFile ? (byte)0 : (byte)128, b = missingFile ? (byte)200 : (byte)128;
                        if (missingFile && (x % 2 == 0) && (y % 2 == 0)) r = g = b = 40;
                        img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; img.Pixels[o + 3] = 255;
                    }
                notes.Add($"texture {ti}: {why} ({(type == 0 ? "magenta" : "grey")} placeholder)");
                return new TexEntry
                {
                    Image = img,
                    Info = new PackageTexture { Png = UniquePng($"missing_{ti}_type{type}"), Kind = "placeholder", Slot = type, Editable = false, Note = why },
                };
            });
        }
        string KindOfSlot(int type) => type switch
        {
            0 => "file", 1 => "body", 2 => "item skin", 6 => "hair", 8 => "skin extra", 11 or 12 or 13 => "creature skin", _ => $"slot {type}",
        };

        // per variant: M2 texture index -> entry
        var texOf = new TexEntry[variants.Count, m.Textures.Length];
        for (int ti = 0; ti < m.Textures.Length; ti++)
        {
            var t = m.Textures[ti];
            for (int vi = 0; vi < variants.Count; vi++)
            {
                TexEntry e;
                if (t.Type == 0)
                {
                    var p = t.FileName;
                    var img = p.Length > 0 ? req.LoadTexture(p) : null;
                    e = img == null ? Placeholder(ti, 0, $"missing file {p}") : Entry("file:" + p, () => new TexEntry
                    {
                        Image = img,
                        Info = new PackageTexture { Png = UniquePng(Path.GetFileNameWithoutExtension(p)), GamePath = p, Kind = "file", Original = req.DescribeTexture(p) },
                    });
                }
                else if (variants[vi].Look.Look.Slots.TryGetValue(t.Type, out var rt))
                {
                    if (rt.Path != null && rt.Parts.Count <= 1)
                        e = Entry("file:" + rt.Path, () => new TexEntry
                        {
                            Image = rt.Image,
                            Info = new PackageTexture { Png = UniquePng(Path.GetFileNameWithoutExtension(rt.Path)), GamePath = rt.Path, Kind = KindOfSlot(t.Type), Slot = t.Type, Original = req.DescribeTexture(rt.Path) },
                        });
                    else
                        e = Entry($"composite:{t.Type}:{string.Join(",", rt.Parts)}", () => new TexEntry
                        {
                            Image = rt.Image,
                            Info = new PackageTexture
                            {
                                Png = UniquePng($"{name}_{KindOfSlot(t.Type).Replace(' ', '_')}_composite"), Kind = "composite", Slot = t.Type, Editable = false, Parts = rt.Parts.ToList(),
                                Note = $"The game builds this texture from {rt.Parts.Count} files ({string.Join(", ", rt.Parts.Select(Path.GetFileName))}). Edit those files one by one in the texture list instead; changes to this composite are not imported.",
                            },
                        });
                }
                else e = Placeholder(ti, t.Type, $"replaceable slot {t.Type} ({(M2Schema.TextureTypes.TryGetValue(t.Type, out var tn) ? tn : "?")}) is chosen by the game (e.g. a worn item)");
                if (!e.Info.M2Textures.Contains(ti)) e.Info.M2Textures.Add(ti);
                if (!e.Info.Variants.Contains(vi)) e.Info.Variants.Add(vi);
                if (e.Info.Slot == 0 && t.Type != 0) e.Info.Slot = t.Type;
                e.WrapU |= t.WrapU;
                e.WrapV |= t.WrapV;
                if (t.Type != 0) variants[vi].Info.Slots[t.Type.ToString()] = e.Info.Id;
                texOf[vi, ti] = e;
            }
        }
        foreach (var e in texEntries)
        {
            e.WithAlpha = !e.Image.IsOpaque();
            var png = Png.Encode(e.Image, e.WithAlpha);
            File.WriteAllBytes(Path.Combine(folder, e.Info.Png.Replace('/', Path.DirectorySeparatorChar)), png);
            e.Info.Width = e.Image.Width;
            e.Info.Height = e.Image.Height;
            e.Info.PixelSha256 = PixelSha(e.Image, e.WithAlpha);
            e.Info.PngSha256 = Sha(png);
        }

        // ------------------------------------------------ geometry, skeleton, attachments, collision, animations
        var b = new GltfBuilder();
        var manifest = new ModelPackageManifest
        {
            Created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Note = "Made by WoW Data Studio's \"Export for Blender\". Keep this file in the folder: \"Import edited model...\" uses it to match your edits to the game files.",
            Mode = req.Mode switch { ModelExportMode.AllVariants => "all skins (KHR_materials_variants)", ModelExportMode.OnePerVariant => "one file per skin", _ => "current look" },
            Model = new PackageModel
            {
                ArchivePath = req.ModelPath, Archive = req.Archive, Source = req.Source, Sha256 = Sha(req.ModelData), Size = req.ModelData.Length, GameSha256 = req.GameSha256,
                Name = m.Name, M2Version = (int)m.Version, Vertices = m.Vertices.Length, Triangles = m.Views.Length > 0 ? m.Views[0].Triangles.Length / 3 : 0,
                Bones = m.Bones.Length, Sequences = m.Sequences.Length, BoneLimits = m.Views.Select(v => (int)v.BoneLimit).ToList(), Checksums = PartChecksums(f),
                IsCharacter = req.IsCharacter,
            },
            Users = new PackageUsers { Summary = req.UsersSummary, Count = req.UsersCount, Warning = req.UsersWarning },
        };
        manifest.Variants.AddRange(variants.Select(v => v.Info));
        manifest.Textures.AddRange(texEntries.Select(e => e.Info));

        int root = b.AddNode(new JsonObject { ["name"] = name });
        b.Arr("scenes")[0]!["nodes"]!.AsArray().Add(root);
        b.Arr("scenes")[0]!["name"] = name;

        // skeleton: one node per bone; rest = pivot (node translation = pivot - parent pivot), IBM = T(-pivot)
        int nb = m.Bones.Length;
        var boneNodes = new int[nb];
        var restT = new Vector3[nb];
        for (int i = 0; i < nb; i++)
        {
            var bone = m.Bones[i];
            int p = bone.Parent;
            var local = bone.Pivot - (p >= 0 && p < nb ? m.Bones[p].Pivot : Vector3.Zero);
            restT[i] = ToGltf(local);
            string bn = $"bone_{i:000}" + (bone.KeyBoneId >= 0 && bone.KeyBoneId < M2Schema.KeyBones.Length ? "_" + M2Schema.KeyBones[bone.KeyBoneId] : "");
            boneNodes[i] = b.AddNode(new JsonObject
            {
                ["name"] = bn,
                ["translation"] = new JsonArray(restT[i].X, restT[i].Y, restT[i].Z),
                ["extras"] = new JsonObject
                {
                    ["wow_bone"] = i, ["wow_bone_id"] = $"b{i:000}", ["wow_key_bone_id"] = bone.KeyBoneId, ["wow_flags"] = bone.Flags, ["wow_parent"] = (int)bone.Parent,
                    ["wow_submesh_id"] = bone.SubmeshId, ["wow_pivot"] = new JsonArray(bone.Pivot.X, bone.Pivot.Y, bone.Pivot.Z),
                    ["wow_global_channels"] = GlobalChannels(f, i),
                },
            });
            manifest.Bones.Add(new PackageBone { Id = $"b{i:000}", Index = i, Node = bn, KeyBone = bone.KeyBoneId, Parent = bone.Parent });
        }
        for (int i = 0; i < nb; i++)
        {
            int p = m.Bones[i].Parent;
            b.AddChild(p >= 0 && p < nb && p != i ? boneNodes[p] : root, boneNodes[i]);
        }
        int? skin = null;
        if (nb > 0)
        {
            var ibm = new float[nb * 16];
            for (int i = 0; i < nb; i++)
            {
                var g = ToGltf(m.Bones[i].Pivot);
                ibm[i * 16 + 0] = 1; ibm[i * 16 + 5] = 1; ibm[i * 16 + 10] = 1; ibm[i * 16 + 15] = 1;
                ibm[i * 16 + 12] = -g.X; ibm[i * 16 + 13] = -g.Y; ibm[i * 16 + 14] = -g.Z;
            }
            b.Arr("skins").Add(new JsonObject
            {
                ["name"] = name + "_skeleton", ["joints"] = new JsonArray(boneNodes.Select(x => (JsonNode)x).ToArray()), ["skeleton"] = root,
                ["inverseBindMatrices"] = b.AccessorF(ibm, "MAT4"),
            });
            skin = 0;
        }

        // meshes: view 0, one mesh + node per submesh
        var primitives = new List<(int Submesh, JsonObject Prim, int[] Units)>();
        if (m.Views.Length == 0) notes.Add("no views: nothing to draw");
        else
        {
            var view = m.Views[0];
            var tusBySub = new Dictionary<int, List<int>>();
            for (int ti = 0; ti < view.TexUnits.Length; ti++)
            {
                if (!tusBySub.TryGetValue(view.TexUnits[ti].Submesh, out var l)) tusBySub[view.TexUnits[ti].Submesh] = l = new List<int>();
                l.Add(ti);
            }
            for (int si = 0; si < view.Submeshes.Length; si++)
            {
                var sm = view.Submeshes[si];
                if (sm.IndexCount == 0) continue;
                var (vv, local) = SubmeshVertices(view, sm);
                int n = vv.Length;
                var pos = new float[n * 3];
                var nrm = new float[n * 3];
                var uv = new float[n * 2];
                var joints = new byte[n * 4];
                var weights = new byte[n * 4];
                var modelVerts = new List<int>(n);
                for (int k = 0; k < n; k++)
                {
                    int mv = view.Indices[vv[k]];
                    modelVerts.Add(mv);
                    var v = m.Vertices[mv];
                    var gp = ToGltf(v.Position);
                    pos[k * 3] = gp.X; pos[k * 3 + 1] = gp.Y; pos[k * 3 + 2] = gp.Z;
                    double nx = v.Normal.X, ny = v.Normal.Z, nz = -v.Normal.Y;
                    double ln = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (ln < 1e-6) { nx = 0; ny = 1; nz = 0; }
                    else { nx /= ln; ny /= ln; nz /= ln; }
                    nrm[k * 3] = (float)nx; nrm[k * 3 + 1] = (float)ny; nrm[k * 3 + 2] = (float)nz;
                    uv[k * 2] = v.Uv.X; uv[k * 2 + 1] = v.Uv.Y;
                    var (jb, jw) = MergeInfluences(v);
                    jb.CopyTo(joints, k * 4);
                    jw.CopyTo(weights, k * 4);
                }
                var attrs = new JsonObject
                {
                    ["POSITION"] = b.AccessorF(pos, "VEC3", GltfBuilder.ArrayBuffer, minMax: true),
                    ["NORMAL"] = b.AccessorF(nrm, "VEC3", GltfBuilder.ArrayBuffer),
                    ["TEXCOORD_0"] = b.AccessorF(uv, "VEC2", GltfBuilder.ArrayBuffer),
                };
                if (skin != null)
                {
                    attrs["JOINTS_0"] = b.AccessorBytes(joints, "VEC4", GltfBuilder.UByte, n, GltfBuilder.ArrayBuffer);
                    attrs["WEIGHTS_0"] = b.AccessorBytes(weights, "VEC4", GltfBuilder.UByte, n, GltfBuilder.ArrayBuffer, normalized: true);
                }
                var prim = new JsonObject { ["attributes"] = attrs, ["mode"] = 4, ["indices"] = b.AccessorIndices(local, n) };
                var units = tusBySub.GetValueOrDefault(si) ?? new List<int>();
                bool visible = req.VisibleGeosets == null || req.VisibleGeosets.Contains(sm.GeosetId);
                string nodeName = $"{name}_g{sm.GeosetId:0000}_s{si:00}";
                var extras = new JsonObject
                {
                    ["wow_submesh"] = si, ["wow_mesh_id"] = $"s{si:00}", ["wow_geoset_id"] = sm.GeosetId, ["wow_view"] = 0,
                    ["wow_texture_units"] = new JsonArray(units.Select(u => (JsonNode)u).ToArray()), ["wow_default_visible"] = visible,
                    ["wow_center_bone"] = sm.CenterBone, ["wow_bone_count"] = sm.BoneCount,
                };
                b.Arr("meshes").Add(new JsonObject { ["name"] = nodeName, ["primitives"] = new JsonArray(prim), ["extras"] = extras });
                var node = new JsonObject { ["name"] = nodeName, ["mesh"] = b.Arr("meshes").Count - 1, ["extras"] = extras.DeepClone() };
                if (skin != null) node["skin"] = skin.Value;
                b.AddChild(root, b.AddNode(node));
                primitives.Add((si, prim, units.ToArray()));
                manifest.Submeshes.Add(new PackageSubmesh
                {
                    Id = $"s{si:00}", Index = si, Geoset = sm.GeosetId, Node = nodeName, Vertices = n, Triangles = local.Length / 3, TextureUnits = units,
                    DefaultVisible = visible, ModelVertices = modelVerts, Label = $"part {si} (geoset {sm.GeosetId})",
                });
            }
        }

        // collision mesh (hidden; not imported back)
        var cTri = f["collision_triangles"].AsU16();
        var cVert = f["collision_vertices"].AsVec3();
        if (cTri.Length > 0 && cVert.Length > 0)
        {
            var cp = new float[cVert.Length * 3];
            for (int i = 0; i < cVert.Length; i++)
            {
                var g = ToGltf(cVert[i]);
                cp[i * 3] = g.X; cp[i * 3 + 1] = g.Y; cp[i * 3 + 2] = g.Z;
            }
            var prim = new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = b.AccessorF(cp, "VEC3", GltfBuilder.ArrayBuffer, minMax: true) },
                ["indices"] = b.AccessorIndices(cTri.Select(x => (int)x).ToArray(), cVert.Length), ["mode"] = 4,
            };
            var extras = new JsonObject { ["wow_collision"] = true, ["wow_default_visible"] = false };
            b.Arr("meshes").Add(new JsonObject { ["name"] = "COL_" + name, ["primitives"] = new JsonArray(prim), ["extras"] = extras });
            b.AddChild(root, b.AddNode(new JsonObject { ["name"] = "COL_" + name, ["mesh"] = b.Arr("meshes").Count - 1, ["extras"] = extras.DeepClone() }));
        }

        // attachments: empties under their bone
        for (int i = 0; i < m.Attachments.Length; i++)
        {
            var a = m.Attachments[i];
            int parent = a.Bone < nb ? boneNodes[a.Bone] : root;
            var local = a.Bone < nb ? a.Position - m.Bones[a.Bone].Pivot : a.Position;
            var g = ToGltf(local);
            string an = $"ATT_{a.Id:00}_{(a.Id >= 0 && a.Id < M2Schema.AttachmentNames.Length ? M2Schema.AttachmentNames[a.Id] : "Attachment")}";
            b.AddChild(parent, b.AddNode(new JsonObject
            {
                ["name"] = an, ["translation"] = new JsonArray(g.X, g.Y, g.Z),
                ["extras"] = new JsonObject { ["wow_attachment"] = i, ["wow_attachment_id"] = a.Id, ["wow_bone"] = a.Bone },
            }));
        }

        if (req.IncludeAnimations) Animations(b, m, boneNodes, restT, req.AnimationName);
        if (req.IncludeSkeleton)
        {
            var rig = RigExport.Describe(m, req.Animations, req.AnimationName);
            // events: empties under their bone; global sequences: actions of their own
            var evArr = m.File["events"];
            foreach (var e in rig.Events)
            {
                int bi = e.Bone;
                var pos = evArr.Vec3(e.Index, "position");
                var local = bi < nb ? pos - m.Bones[bi].Pivot : pos;
                var g = ToGltf(local);
                b.AddChild(bi < nb ? boneNodes[bi] : root, b.AddNode(new JsonObject
                {
                    ["name"] = e.Node, ["translation"] = new JsonArray(g.X, g.Y, g.Z),
                    ["extras"] = new JsonObject { ["wow_event"] = e.Index, ["wow_event_code"] = e.Code, ["wow_bone"] = bi },
                }));
            }
            if (req.IncludeAnimations) GlobalAnimations(b, m, boneNodes, restT, rig);
            manifest.Rig = rig;
            var fx = EffectsJson.Export(m);
            if (fx != null)
            {
                var fxText = fx.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(folder, EffectsJson.FileName), fxText);
                rig.EffectsFile = EffectsJson.FileName;
                rig.EffectsSha256 = Sha(System.Text.Encoding.UTF8.GetBytes(fxText));
            }
        }

        // ------------------------------------------------ materials per output file, then write
        var bin = b.Bin();
        string binName = name + ".bin";
        File.WriteAllBytes(Path.Combine(folder, binName), bin);
        var result = new ModelExportResult { Folder = folder, ManifestPath = Path.Combine(folder, ModelPackageManifest.FileName), Manifest = manifest };
        result.Notes.AddRange(notes);

        var files = new List<(string File, int Variant, string Label)>();
        if (req.Mode == ModelExportMode.OnePerVariant && variants.Count > 1)
            for (int vi = 0; vi < variants.Count; vi++)
                files.Add(($"{name}_{SafeName(variants[vi].Info.Name)}.gltf", vi, variants[vi].Info.Name));
        else files.Add(($"{name}.gltf", -1, variants.Count > 1 ? $"all {variants.Count} skins (switch them in Blender's glTF Variants panel)" : variants[0].Info.Name));
        manifest.GltfFiles.AddRange(files.Select(x => new PackageGltfFile { File = x.File, Variant = x.Variant, Label = x.Label }));

        // the material list is the same for every file (ids stay stable); which ones a file uses depends on its variant
        int defaultVariant = variantOfLook.GetValueOrDefault(looks[current]);
        var docs = new List<(string File, JsonObject Json)>();
        foreach (var (file, fileVariant, _) in files)
        {
            var g = b.Finish(binName, bin.Length);
            var mats = new JsonArray();
            var texs = new JsonArray();
            var images = new JsonArray();
            var samplers = new JsonArray();
            var texIndex = new Dictionary<string, int>();
            var matIndex = new Dictionary<string, int>();
            int TexIdx(TexEntry e)
            {
                if (texIndex.TryGetValue(e.Info.Id, out int ix)) return ix;
                images.Add(new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(e.Info.Png), ["uri"] = e.Info.Png, ["extras"] = new JsonObject { ["wow_texture_id"] = e.Info.Id, ["wow_game_path"] = e.Info.GamePath } });
                var samp = new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = e.WrapU ? Repeat : Clamp, ["wrapT"] = e.WrapV ? Repeat : Clamp };
                int si = -1;
                for (int k = 0; k < samplers.Count; k++)
                    if (JsonNode.DeepEquals(samplers[k], samp)) si = k;
                if (si < 0)
                {
                    samplers.Add(samp);
                    si = samplers.Count - 1;
                }
                texs.Add(new JsonObject { ["source"] = images.Count - 1, ["sampler"] = si, ["name"] = Path.GetFileNameWithoutExtension(e.Info.Png) });
                return texIndex[e.Info.Id] = texs.Count - 1;
            }
            var variantList = fileVariant >= 0 ? new List<int> { fileVariant } : Enumerable.Range(0, variants.Count).ToList();
            bool useVariants = fileVariant < 0 && variants.Count > 1;
            foreach (var (si, prim, units) in primitives)
            {
                if (units.Length == 0) continue;
                var tu = m.Views[0].TexUnits[units[0]];
                // the material of each variant (the same one when the texture does not change between variants)
                var perVariant = new Dictionary<int, int>();
                foreach (var vi in variantList)
                {
                    var e = TexOfUnit(m, tu, texOf, vi);
                    bool shared = e == null || variants.Count == 1 || Enumerable.Range(0, variants.Count).All(o => TexOfUnit(m, tu, texOf, o) == e);
                    string mid = shared ? $"m{si:00}" : $"m{si:00}_v{vi:00}";
                    if (!matIndex.TryGetValue(mid, out int mi))
                    {
                        mats.Add(Material(m, tu, name, si, mid, shared ? -1 : vi, e, e == null ? null : TexIdx(e)));
                        mi = matIndex[mid] = mats.Count - 1;
                        if (!manifest.Materials.Any(x => x.Id == mid))
                        {
                            var rf = m.FlagsOf(tu);
                            manifest.Materials.Add(new PackageMaterial
                            {
                                Id = mid, Name = mats[mi]!["name"]!.GetValue<string>(), Submesh = si, Variant = shared ? -1 : vi, Texture = e?.Info.Id, Blend = rf.Blend, RenderFlags = rf.Flags,
                            });
                        }
                    }
                    perVariant[vi] = mi;
                }
                var gp = FindPrimitive(g, si);
                gp["material"] = perVariant[fileVariant >= 0 ? fileVariant : (perVariant.ContainsKey(defaultVariant) ? defaultVariant : variantList[0])];
                if (useVariants)
                {
                    var mappings = new JsonArray();
                    foreach (var grp in perVariant.GroupBy(kv => kv.Value))
                        mappings.Add(new JsonObject { ["material"] = grp.Key, ["variants"] = new JsonArray(grp.Select(kv => (JsonNode)kv.Key).ToArray()) });
                    gp["extensions"] = new JsonObject { ["KHR_materials_variants"] = new JsonObject { ["mappings"] = mappings } };
                }
            }
            if (mats.Count > 0) g["materials"] = mats;
            if (texs.Count > 0)
            {
                g["textures"] = texs;
                g["images"] = images;
                g["samplers"] = samplers;
            }
            var used2 = new SortedSet<string>(StringComparer.Ordinal);
            if (mats.Any(x => x!["extensions"]?["KHR_materials_unlit"] != null)) used2.Add("KHR_materials_unlit");
            if (useVariants)
            {
                used2.Add("KHR_materials_variants");
                g["extensions"] = new JsonObject
                {
                    ["KHR_materials_variants"] = new JsonObject { ["variants"] = new JsonArray(variants.Select(v => (JsonNode)new JsonObject { ["name"] = v.Info.Name }).ToArray()) },
                };
            }
            if (used2.Count > 0) g["extensionsUsed"] = new JsonArray(used2.Select(x => (JsonNode)x).ToArray());
            docs.Add((file, g));
        }
        manifest.Materials.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        foreach (var t in manifest.Textures) t.Variants.Sort();
        // root extras last: they embed the finished manifest, so a lone file exported by Blender still carries it
        var manifestJson = manifest.ToJson();
        foreach (var (file, g) in docs)
        {
            g["nodes"]![root]!["extras"] = new JsonObject
            {
                ["wow_model"] = req.ModelPath, ["wow_name"] = m.Name, ["wow_package"] = Path.GetFileName(folder), ["wow_source_sha256"] = manifest.Model.Sha256,
                ["wow_coordinates"] = manifest.Coordinates, ["wow_manifest"] = manifestJson,
            };
            g["extras"] = new JsonObject { ["wow_manifest_file"] = ModelPackageManifest.FileName, ["wow_model"] = req.ModelPath };
            File.WriteAllText(Path.Combine(folder, file), g.ToJsonString(GltfBuilder.Compact));
            result.GltfFiles.Add(Path.Combine(folder, file));
        }
        File.WriteAllText(result.ManifestPath, manifestJson);
        return result;
    }

    private static TexEntry? TexOfUnit(M2Model m, M2TexUnit tu, TexEntry[,] texOf, int vi)
    {
        int li = tu.TextureLookup;
        if (li >= m.TextureLookup.Length) return null;
        int ti = m.TextureLookup[li];
        return ti < m.Textures.Length ? texOf[vi, ti] : null;
    }

    private static JsonObject FindPrimitive(JsonObject g, int submesh)
    {
        foreach (var mesh in g["meshes"]!.AsArray())
            if (GltfFile.IntOf(mesh!["extras"]?["wow_submesh"]) == submesh)
                return mesh["primitives"]![0]!.AsObject();
        throw new InvalidOperationException($"submesh {submesh} has no mesh");
    }

    /// <summary>The vertex's influences for glTF: a bone listed twice (some client vertices do) becomes one entry with
    /// the summed weight (Blender keeps one weight per bone); weights sum to 255.</summary>
    public static (byte[] Bones, byte[] Weights) MergeInfluences(in M2Vertex v)
    {
        var w = FixWeights(v.W0, v.W1, v.W2, v.W3);
        var b = new[] { v.B0, v.B1, v.B2, v.B3 };
        var ob = new byte[4];
        var ow = new byte[4];
        int n = 0;
        for (int k = 0; k < 4; k++)
        {
            if (w[k] == 0) continue;
            int at = Array.IndexOf(ob, b[k], 0, n);
            if (at >= 0) ow[at] += w[k];
            else
            {
                ob[n] = b[k];
                ow[n] = w[k];
                n++;
            }
        }
        if (n == 0) { ob[0] = b[0]; ow[0] = 255; }
        return (ob, ow);
    }

    /// <summary>glTF wants weights summing to 1: rescale the rare ones that do not sum to 255 (none in 1.12 files).</summary>
    public static byte[] FixWeights(byte w0, byte w1, byte w2, byte w3)
    {
        int s = w0 + w1 + w2 + w3;
        if (s == 255) return new[] { w0, w1, w2, w3 };
        if (s == 0) return new byte[] { 255, 0, 0, 0 };
        var w = new[] { w0 * 255 / s, w1 * 255 / s, w2 * 255 / s, w3 * 255 / s };
        int max = Array.IndexOf(w, w.Max());
        w[max] += 255 - w.Sum();
        return w.Select(x => (byte)x).ToArray();
    }

    private static float StaticWeight(M2Model m, M2TexUnit tu)
    {
        float w = 1f;
        int tl = tu.TransparencyLookup;
        if (tl < m.TransparencyLookup.Length)
        {
            int ti = m.TransparencyLookup[tl];
            if (ti < m.Transparency.Length && m.Transparency[ti].Values.Length > 0) w *= m.Transparency[ti].Values[0] / 32767f;
        }
        if (tu.Color >= 0 && tu.Color < m.Colors.Length && m.Colors[tu.Color].Alpha.Values.Length > 0) w *= m.Colors[tu.Color].Alpha.Values[0] / 32767f;
        return Math.Clamp(w, 0f, 1f);
    }

    private static JsonObject Material(M2Model m, M2TexUnit tu, string modelName, int submesh, string id, int variant, TexEntry? tex, int? texIndex)
    {
        var rf = m.FlagsOf(tu);
        var texs = new List<int>();
        for (int k = 0; k < Math.Max(1, tu.TextureCount); k++)
            if (tu.TextureLookup + k < m.TextureLookup.Length) texs.Add(m.TextureLookup[tu.TextureLookup + k]);
        var pbr = new JsonObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 1.0 };
        if (texIndex != null) pbr["baseColorTexture"] = new JsonObject { ["index"] = texIndex.Value };
        float weight = StaticWeight(m, tu);
        var mat = new JsonObject { ["name"] = "", ["pbrMetallicRoughness"] = pbr };
        if (rf.Blend == 0) mat["alphaMode"] = "OPAQUE";
        else if (rf.Blend == 1)
        {
            mat["alphaMode"] = "MASK";
            mat["alphaCutoff"] = AlphaKeyCutoff;
        }
        else
        {
            mat["alphaMode"] = "BLEND";
            if (weight < 1f) pbr["baseColorFactor"] = new JsonArray(1.0, 1.0, 1.0, (double)weight);
        }
        if (rf.TwoSided) mat["doubleSided"] = true;
        if (rf.Unlit) mat["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() };
        string texName = tex == null ? "untextured" : Path.GetFileNameWithoutExtension(tex.Info.Png);
        mat["name"] = $"{modelName}_s{submesh:00}_{texName}" + (variant >= 0 ? $"_v{variant}" : "");
        mat["extras"] = new JsonObject
        {
            ["wow_material_id"] = id, ["wow_submesh"] = submesh, ["wow_texture_id"] = tex?.Info.Id, ["wow_variant"] = variant,
            ["wow_render_flags_index"] = tu.RenderFlags, ["wow_render_flags"] = rf.Flags,
            ["wow_render_flag_names"] = new JsonArray(M2Schema.RenderFlagBits.Where(kv => (rf.Flags & kv.Key) != 0).Select(kv => (JsonNode)kv.Value).ToArray()),
            ["wow_blend"] = rf.Blend, ["wow_blend_name"] = rf.Blend < M2Schema.BlendModes.Length ? M2Schema.BlendModes[rf.Blend] : "?",
            ["wow_textures"] = new JsonArray(texs.Select(x => (JsonNode)x).ToArray()), ["wow_static_opacity"] = Math.Round(weight, 4),
            ["wow_color"] = tu.Color, ["wow_transparency_lookup"] = tu.TransparencyLookup, ["wow_texture_transform_lookup"] = tu.TextureTransformLookup,
        };
        if (texs.Count > 1) mat["extras"]!["wow_note"] = $"second texture layer ({string.Join(", ", texs.Skip(1))}) not shown: glTF has one base colour texture";
        return mat;
    }

    // ---------------------------------------------------------------- animations (port of gltf.py animations + anim.sequence_keys)

    private static void Animations(GltfBuilder b, M2Model m, int[] boneNodes, Vector3[] restT, Func<int, string> animName)
    {
        int nb = m.Bones.Length;
        if (nb == 0 || m.Sequences.Length == 0) return;
        var animated = Enumerable.Range(0, nb).Where(i => m.Bones[i].Translation.Times.Length > 0 || m.Bones[i].Rotation.Times.Length > 0 || m.Bones[i].Scale.Times.Length > 0).ToList();
        var names = ActionNames(m, animName);
        for (int si = 0; si < m.Sequences.Length; si++)
        {
            var seq = m.Sequences[si];
            string baseName = animName(seq.AnimId);
            string n = names[si];
            var samplers = new JsonArray();
            var channels = new JsonArray();
            foreach (int bi in animated)
            {
                var bone = m.Bones[bi];
                for (int path = 0; path < 3; path++)
                {
                    int keyCount = path switch { 0 => bone.Translation.Times.Length, 1 => bone.Rotation.Times.Length, _ => bone.Scale.Times.Length };
                    if (keyCount == 0) continue;
                    int interp = path switch { 0 => bone.Translation.Interpolation, 1 => bone.Rotation.Interpolation, _ => bone.Scale.Interpolation };
                    double[] times;
                    double[][] vals;
                    bool step;
                    var keys = path switch
                    {
                        0 => SequenceKeys(bone.Translation, si, seq.Start, seq.End, m.GlobalSequences, v => new double[] { v.X, v.Y, v.Z }, false),
                        1 => SequenceKeys(bone.Rotation, si, seq.Start, seq.End, m.GlobalSequences, v => new double[] { v.X, v.Y, v.Z, v.W }, true),
                        _ => SequenceKeys(bone.Scale, si, seq.Start, seq.End, m.GlobalSequences, v => new double[] { v.X, v.Y, v.Z }, false),
                    };
                    if (keys == null)
                    {
                        times = new[] { 0.0 };
                        vals = new[] { path == 0 ? new double[] { 0, 0, 0 } : path == 1 ? new double[] { 0, 0, 0, 1 } : new double[] { 1, 1, 1 } };
                        step = true;
                    }
                    else
                    {
                        (times, vals) = keys.Value;
                        step = interp == 0;
                    }
                    float[] outp;
                    if (path == 0)
                    {
                        outp = new float[vals.Length * 3];
                        for (int k = 0; k < vals.Length; k++)
                        {
                            // gltf.py adds in float64: the same here
                            outp[k * 3] = (float)(vals[k][0] + restT[bi].X);
                            outp[k * 3 + 1] = (float)(vals[k][2] + restT[bi].Y);
                            outp[k * 3 + 2] = (float)(-vals[k][1] + restT[bi].Z);
                        }
                    }
                    else if (path == 1)
                    {
                        outp = new float[vals.Length * 4];
                        double[]? prev = null;
                        for (int k = 0; k < vals.Length; k++)
                        {
                            double x = vals[k][0], y = vals[k][2], z = -vals[k][1], w = vals[k][3];
                            double ln = Math.Sqrt(x * x + y * y + z * z + w * w);
                            if (ln > 1e-9) { x /= ln; y /= ln; z /= ln; w /= ln; }
                            else { x = 0; y = 0; z = 0; w = 1; }
                            var q = new[] { x, y, z, w };
                            if (prev != null && q[0] * prev[0] + q[1] * prev[1] + q[2] * prev[2] + q[3] * prev[3] < 0)
                                for (int c = 0; c < 4; c++) q[c] = -q[c];
                            prev = q;
                            for (int c = 0; c < 4; c++) outp[k * 4 + c] = (float)q[c];
                        }
                    }
                    else
                    {
                        outp = new float[vals.Length * 3];
                        for (int k = 0; k < vals.Length; k++)
                        {
                            outp[k * 3] = (float)vals[k][0];
                            outp[k * 3 + 1] = (float)vals[k][2];
                            outp[k * 3 + 2] = (float)vals[k][1];
                        }
                    }
                    var tt = times.Select(t => (float)(t / 1000.0)).ToArray();
                    int inp = b.AccessorF(tt, "SCALAR", minMax: true, dedupe: true);
                    int outAcc = b.AccessorF(outp, path == 1 ? "VEC4" : "VEC3", dedupe: true);
                    samplers.Add(new JsonObject { ["input"] = inp, ["output"] = outAcc, ["interpolation"] = step ? "STEP" : "LINEAR" });
                    channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = boneNodes[bi], ["path"] = path switch { 0 => "translation", 1 => "rotation", _ => "scale" } } });
                }
            }
            if (channels.Count == 0) continue;
            b.Arr("animations").Add(new JsonObject
            {
                ["name"] = n, ["samplers"] = samplers, ["channels"] = channels,
                ["extras"] = new JsonObject { ["wow_sequence"] = si, ["wow_anim_id"] = seq.AnimId, ["wow_sub_id"] = seq.SubId, ["wow_anim_name"] = baseName, ["wow_duration_ms"] = seq.Duration },
            });
        }
    }

    /// <summary>0.10.0: the action name of every sequence: the animation's name, with " [2]", " [3]"... for its further
    /// variations ("Stand", "Stand [2]"). Unique within the model.</summary>
    public static string[] ActionNames(M2Model m, Func<int, string> animName)
    {
        var r = new string[m.Sequences.Length];
        var rank = new Dictionary<int, int>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int si = 0; si < m.Sequences.Length; si++)
        {
            int id = m.Sequences[si].AnimId;
            int v = rank.GetValueOrDefault(id);
            rank[id] = v + 1;
            string n = RigInfo.SequenceName(animName(id), v);
            for (int k = 2; !used.Add(n); k++) n = $"{RigInfo.SequenceName(animName(id), v)} #{k}";
            r[si] = n;
        }
        return r;
    }

    /// <summary>The channels of a bone that run on a global sequence (channel -> global sequence).</summary>
    private static JsonObject GlobalChannels(M2File f, int bone)
    {
        var o = new JsonObject();
        foreach (var t in f["bones"].Tracks(bone))
            if (t.GlobalSequence >= 0 && t.KeyCount > 0) o[t.Name] = (int)t.GlobalSequence;
        return o;
    }

    /// <summary>0.10.0: one animation per global sequence ("Global sequence 1 (2.00 s)") with the channels that run on it,
    /// on its own clock (0 .. its length).</summary>
    private static void GlobalAnimations(GltfBuilder b, M2Model m, int[] boneNodes, Vector3[] restT, PackageRig rig)
    {
        var bones = m.File["bones"];
        foreach (var gsInfo in rig.GlobalSequences)
        {
            int gs = gsInfo.Index;
            uint period = gsInfo.DurationMs;
            var samplers = new JsonArray();
            var channels = new JsonArray();
            for (int bi = 0; bi < m.Bones.Length; bi++)
                foreach (var t in bones.Tracks(bi))
                {
                    if (t.GlobalSequence != gs || t.KeyCount == 0) continue;
                    var raw = RawTrack.Read(t);
                    var w = raw.GlobalWindow(period);
                    if (w.Count == 0) continue;
                    int path = t.Name switch { "translation" => 0, "rotation" => 1, _ => 2 };
                    var times = w.Times.Select(x => (double)x).ToArray();
                    double[][] vals = w.Values!.Select(v => TrackValues.Components(raw.ValueType!, v)).ToArray();
                    if (times[^1] < period)
                    {
                        // the loop closes at the end of the period with the last value
                        times = times.Append(period).ToArray();
                        vals = vals.Append(vals[^1]).ToArray();
                    }
                    AddChannel(b, samplers, channels, boneNodes[bi], restT[bi], path, times, vals, raw.Interpolation == 0);
                }
            if (channels.Count == 0) continue;
            b.Arr("animations").Add(new JsonObject
            {
                ["name"] = gsInfo.Action, ["samplers"] = samplers, ["channels"] = channels,
                ["extras"] = new JsonObject { ["wow_global_sequence"] = gs, ["wow_duration_ms"] = period },
            });
        }
    }

    /// <summary>One animated bone channel in glTF form (translation relative to the parent's pivot, rotation and scale in
    /// glTF axes).</summary>
    private static void AddChannel(GltfBuilder b, JsonArray samplers, JsonArray channels, int node, Vector3 restT, int path, double[] times, double[][] vals, bool step)
    {
        float[] outp;
        if (path == 0)
        {
            outp = new float[vals.Length * 3];
            for (int k = 0; k < vals.Length; k++)
            {
                outp[k * 3] = (float)(vals[k][0] + restT.X);
                outp[k * 3 + 1] = (float)(vals[k][2] + restT.Y);
                outp[k * 3 + 2] = (float)(-vals[k][1] + restT.Z);
            }
        }
        else if (path == 1)
        {
            outp = new float[vals.Length * 4];
            double[]? prev = null;
            for (int k = 0; k < vals.Length; k++)
            {
                double x = vals[k][0], y = vals[k][2], z = -vals[k][1], w = vals[k][3];
                double ln = Math.Sqrt(x * x + y * y + z * z + w * w);
                if (ln > 1e-9) { x /= ln; y /= ln; z /= ln; w /= ln; }
                else { x = 0; y = 0; z = 0; w = 1; }
                var q = new[] { x, y, z, w };
                if (prev != null && q[0] * prev[0] + q[1] * prev[1] + q[2] * prev[2] + q[3] * prev[3] < 0)
                    for (int c = 0; c < 4; c++) q[c] = -q[c];
                prev = q;
                for (int c = 0; c < 4; c++) outp[k * 4 + c] = (float)q[c];
            }
        }
        else
        {
            outp = new float[vals.Length * 3];
            for (int k = 0; k < vals.Length; k++)
            {
                outp[k * 3] = (float)vals[k][0];
                outp[k * 3 + 1] = (float)vals[k][2];
                outp[k * 3 + 2] = (float)vals[k][1];
            }
        }
        var tt = times.Select(t => (float)(t / 1000.0)).ToArray();
        int inp = b.AccessorF(tt, "SCALAR", minMax: true, dedupe: true);
        int outAcc = b.AccessorF(outp, path == 1 ? "VEC4" : "VEC3", dedupe: true);
        samplers.Add(new JsonObject { ["input"] = inp, ["output"] = outAcc, ["interpolation"] = step ? "STEP" : "LINEAR" });
        channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = node, ["path"] = path switch { 0 => "translation", 1 => "rotation", _ => "scale" } } });
    }

    private static double[] Evaluate(double[] kt, double[][] kv, double t, int interp, bool quat)
    {
        if (t <= kt[0] || kt.Length == 1) return (double[])kv[0].Clone();
        if (t >= kt[^1]) return (double[])kv[^1].Clone();
        int i = Array.FindLastIndex(kt, x => x <= t);
        if (interp == 0 || kt[i + 1] == kt[i]) return (double[])kv[i].Clone();
        double f = (t - kt[i]) / (kt[i + 1] - kt[i]);
        var v = new double[kv[i].Length];
        for (int c = 0; c < v.Length; c++) v[c] = kv[i][c] + (kv[i + 1][c] - kv[i][c]) * f;
        if (quat)
        {
            double n = Math.Sqrt(v.Sum(x => x * x));
            if (n > 0) for (int c = 0; c < v.Length; c++) v[c] /= n;
        }
        return v;
    }

    /// <summary>Port of wowdata.m2.anim.sequence_keys: (times in ms from the sequence start, values) of a track while
    /// sequence <paramref name="si"/> plays; keys outside the window are folded into interpolated keys at its edges,
    /// global-sequence tracks are unrolled over the sequence.</summary>
    private static (double[] Times, double[][] Values)? SequenceKeys<T>(M2Track<T> track, int si, uint seqStart, uint seqEnd, uint[] gseq, Func<T, double[]> conv, bool quat) where T : struct
    {
        var ts = track.Times;
        if (ts.Length == 0) return null;
        int a, bIdx;
        if (track.Ranges.Length > 0)
        {
            if (si >= track.Ranges.Length) return null;
            var r = track.Ranges[si];
            if (r.Last < r.First || r.First >= ts.Length) return null;
            a = (int)r.First;
            bIdx = (int)Math.Min(r.Last, (uint)ts.Length - 1);
        }
        else
        {
            a = 0;
            bIdx = ts.Length - 1;
        }
        int nvals = track.Values.Length;
        bIdx = Math.Min(bIdx, nvals - 1);
        if (bIdx < a) return null;
        var kt = new double[bIdx - a + 1];
        var kv = new double[bIdx - a + 1][];
        for (int k = a; k <= bIdx; k++)
        {
            kt[k - a] = ts[k];
            kv[k - a] = conv(track.Values[k]);
        }
        int interp = track.Interpolation;
        double length = (double)seqEnd - seqStart;
        int gs = track.GlobalSequence;
        var outT = new List<double>();
        var outV = new List<double[]>();
        if (gs != -1 && gs < gseq.Length)
        {
            double period = gseq[gs];
            if (period <= 0 || kt.Length == 1) return (new[] { 0.0 }, new[] { kv[0] });
            if (kt[0] < 0) { double k0 = kt[0]; for (int i = 0; i < kt.Length; i++) kt[i] -= k0; }
            for (int k = 0; k <= 10000; k++)
            {
                double baseT = k * period;
                if (baseT > length) break;
                for (int i = 0; i < kt.Length; i++)
                    if (kt[i] <= period && baseT + kt[i] <= length)
                    {
                        outT.Add(baseT + kt[i]);
                        outV.Add(kv[i]);
                    }
            }
            if (outT.Count == 0 || outT[0] > 0)
            {
                outT.Insert(0, 0.0);
                outV.Insert(0, Evaluate(kt, kv, 0.0, interp, quat));
            }
            if (outT[^1] < length)
            {
                outT.Add(length);
                outV.Add(Evaluate(kt, kv, period != 0 ? length % period : 0.0, interp, quat));
            }
            return Dedupe(outT, outV);
        }
        double start = seqStart, end = seqEnd;
        outT.Add(0.0);
        outV.Add(Evaluate(kt, kv, start, interp, quat));
        for (int i = 0; i < kt.Length; i++)
            if (kt[i] > start && kt[i] < end)
            {
                outT.Add(kt[i] - start);
                outV.Add(kv[i]);
            }
        if (end > start)
        {
            outT.Add(end - start);
            outV.Add(Evaluate(kt, kv, end, interp, quat));
        }
        return Dedupe(outT, outV);
    }

    private static (double[], double[][]) Dedupe(List<double> t, List<double[]> v)
    {
        if (t.Count < 2) return (t.ToArray(), v.ToArray());
        var rt = new List<double>();
        var rv = new List<double[]>();
        for (int i = 0; i < t.Count; i++)
        {
            if (i < t.Count - 1 && !(t[i + 1] - t[i] > 0)) continue;
            rt.Add(t[i]);
            rv.Add(v[i]);
        }
        return (rt.ToArray(), rv.ToArray());
    }
}
