using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// Phase 3c end to end: "Export for Blender" -> (Blender 4.5 headless with the add-on: import, optional scripted edit,
/// "Export for WoW Data Studio") -> "Import edited model" analysis -> commit into a workspace of a FAKE client folder.
/// The Blender parts are skipped (with a message) when Blender is not installed. The real Client\Data is checked
/// unchanged afterwards.
/// </summary>
[Collection(IndexCollection.Name)]
public class ModelRoundTripTests : IDisposable
{
    public const string Boar = @"Creature\Boar\Boar.m2";
    public const string Tree = @"World\Kalimdor\Durotar\PassiveDoodads\Trees\DurotarPalm01.m2";
    public const string Axe = @"Item\ObjectComponents\Weapon\Axe_2H_Battle_A_01.m2";
    public const string Orc = @"Character\Orc\Male\OrcMale.m2";

    private readonly ITestOutputHelper _out;
    private readonly string _root = TestEnv.TempDir("model3c");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();
    private readonly Workspace _ws;
    private readonly ModelPackageService _svc;

    public ModelRoundTripTests(IndexCollection.Handle h, ITestOutputHelper output)
    {
        var f = h.Fixture;
        _out = output;
        var fakeClient = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fakeClient, "Data"));
        var paths = new AppPaths(Path.Combine(_root, "home")).ForClient(fakeClient);
        _ws = new Workspace(paths);
        var client = TestEnv.Client;
        var catalog = new Catalog(client);
        var ws = _ws;
        var resolver = new ModelResolver(catalog, p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p));
        _svc = new ModelPackageService(client, catalog, f.Index, _ws, resolver, new TextureService(client, _ws));
    }

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Log(ModelImportAnalysis a)
    {
        foreach (var r in a.General) _out.WriteLine($"  [{r.Level}] {r.Text}");
        foreach (var c in a.Changes)
        {
            _out.WriteLine($"  {(c.Unchanged ? "unchanged" : c.Include ? "INCLUDE" : "skip")}: {c.Title}: {c.Detail}");
            foreach (var r in c.Rules) _out.WriteLine($"      [{r.Level}] {r.Text}");
            foreach (var p in c.Parts.Where(p => p.Unmatched.Count > 0))
                foreach (var u in p.Unmatched) _out.WriteLine($"      part {p.Submesh}: {u}");
        }
    }

    // ---------------------------------------------------------------- without Blender

    [Fact]
    public void PackageHasVariantsIdsAndReadsBackUnchanged()
    {
        var res = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.AllVariants, Path.Combine(_root, "out"));
        var man = res.Manifest;
        _out.WriteLine($"{res.Folder}: {man.Variants.Count} variants, {man.Textures.Count} textures, {man.Materials.Count} materials");
        Assert.True(man.Variants.Count >= 9, $"{man.Variants.Count} variants");
        Assert.True(man.Textures.Count(t => t.Kind == "creature skin") >= 9);
        Assert.All(man.Textures.Where(t => t.Editable), t => Assert.NotNull(t.Original));
        Assert.Equal(2, man.Submeshes.Count);
        Assert.Equal(57, man.Bones.Count);
        Assert.Equal(new List<int> { 256, 75, 53, 21 }, man.Model.BoneLimits);
        var g = JsonNode.Parse(File.ReadAllText(res.GltfFiles.Single()))!.AsObject();
        Assert.Equal(man.Variants.Count, g["extensions"]!["KHR_materials_variants"]!["variants"]!.AsArray().Count);
        Assert.Contains("KHR_materials_variants", g["extensionsUsed"]!.AsArray().Select(x => x!.GetValue<string>()));
        foreach (var mesh in g["meshes"]!.AsArray().Where(m => m!["extras"]?["wow_submesh"] != null))
        {
            var maps = mesh!["primitives"]![0]!["extensions"]!["KHR_materials_variants"]!["mappings"]!.AsArray();
            Assert.Equal(man.Variants.Count, maps.Sum(x => x!["variants"]!.AsArray().Count));
        }
        Assert.All(g["materials"]!.AsArray(), m => Assert.NotNull(m!["extras"]!["wow_material_id"]));
        Assert.Contains("wow_manifest", g["nodes"]![0]!["extras"]!.AsObject().Select(kv => kv.Key));

        // read back: positions come out in M2 coordinates and nothing changed
        var em = EditedModelReader.Read(res.Folder);
        Assert.True(em.IsOriginalPackage);
        var m2 = M2Model.Load(TestEnv.Client.Read(Boar)!);
        foreach (var p in em.Primitives.Where(p => !p.IsCollision))
        {
            var sub = man.Submeshes.Single(s => s.Index == p.Submesh);
            for (int i = 0; i < p.Positions.Length; i++)
                Assert.True(Vector3.Distance(p.Positions[i], m2.Vertices[sub.ModelVertices[i]].Position) < 1e-5f);
        }
        var a = ModelImportAnalyzer.Analyze(em, _svc.ImportContext());
        Log(a);
        Assert.True(a.NothingChanged);
        Assert.False(a.AnythingToImport);

        // one per skin: N files, all with the same geometry
        var per = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.OnePerVariant, Path.Combine(_root, "per"));
        Assert.Equal(man.Variants.Count, per.GltfFiles.Count);
        // current look only: one variant, no extension
        var cur = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.CurrentLook, Path.Combine(_root, "cur"), currentDisplay: 503);
        Assert.Single(cur.Manifest.Variants);
        Assert.Contains(503L, cur.Manifest.Variants[0].Ids);
        Assert.DoesNotContain("KHR_materials_variants", File.ReadAllText(cur.GltfFiles[0]));
    }

    [Fact]
    public void EditedPngInThePackageImportsAsOneChangeSet()
    {
        var res = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.CurrentLook, Path.Combine(_root, "png"), currentDisplay: 503);
        var skin = res.Manifest.Textures.Single(t => t.GamePath != null && t.GamePath.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase));
        var file = Path.Combine(res.Folder, skin.Png.Replace('/', Path.DirectorySeparatorChar));
        var img = Png.Load(file);
        WoWDataStudio.Services.ScreenshotRunner.PaintTestEdit(img);
        Png.Save(img, file, true);
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Log(a);
        var tex = a.Changes.Single(c => c.Kind == ModelChangeKind.Texture && !c.Unchanged);
        Assert.Equal(skin.GamePath, tex.GamePath);
        Assert.True(tex.Include && tex.CanInclude);
        Assert.Contains(tex.Rules, r => r.Text.StartsWith("Shared", StringComparison.Ordinal));
        Assert.True(a.Changes.Single(c => c.Kind == ModelChangeKind.Mesh).Unchanged);
        var cs = ModelImportCommitter.Commit(a, _ws, null);
        Assert.True(_ws.IsModified(skin.GamePath!));
        Assert.Single(cs.Files);
        _ws.RevertChangeSet(cs.Id);
        Assert.False(_ws.IsModified(skin.GamePath!));
    }

    [Fact]
    public void QuantizedWeightsAreTop4Summing255()
    {
        var (b, w) = ModelImportAnalyzer.QuantizeWeights(new[] { (3, 0.5f), (7, 0.2f), (1, 0.15f), (9, 0.1f), (4, 0.05f) }, 0);
        Assert.Equal(255, w.Sum(x => x));
        Assert.Equal(new byte[] { 3, 7, 1, 9 }, b);
        Assert.True(w[0] >= w[1] && w[1] >= w[2] && w[2] >= w[3]);
        var (b2, w2) = ModelImportAnalyzer.QuantizeWeights(Array.Empty<(int, float)>(), 5);
        Assert.Equal(5, b2[0]);
        Assert.Equal(255, w2[0]);
        var (_, w3) = ModelImportAnalyzer.QuantizeWeights(new[] { (1, 1f / 3), (2, 1f / 3), (3, 1f / 3) }, 0);
        Assert.Equal(255, w3.Sum(x => x));
    }

    // ---------------------------------------------------------------- through Blender

    private static string AddonRoot => Path.Combine(Oracle.ToolDir, "blender_addon");

    /// <summary>Import + (edit) + export in headless Blender; returns the exported .gltf or null without Blender.</summary>
    private string? ThroughBlender(string gltf, string outGltf, object? edit = null)
    {
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            _out.WriteLine("Blender 4.5 not found: the Blender part of this test was skipped.");
            return null;
        }
        var report = Path.Combine(Path.GetDirectoryName(outGltf)!, "..", Path.GetFileNameWithoutExtension(outGltf) + "_report.json");
        string editFile = "";
        if (edit != null)
        {
            editFile = Path.Combine(Path.GetDirectoryName(outGltf)!, "..", "edit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(editFile))!);
            File.WriteAllText(editFile, JsonSerializer.Serialize(edit));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = BlenderTools.RunHeadless(blender, Path.Combine(AddonRoot, "tests", "studio_roundtrip.py"), new[] { report, gltf, outGltf, editFile }, 900_000);
        Assert.True(File.Exists(report), "Blender wrote no report: " + r.Output);
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        _out.WriteLine($"Blender {rep["blender"]} in {sw.Elapsed.TotalSeconds:0.0} s: import {rep["import"]?.ToJsonString()}, export {rep["export"]?.ToJsonString()}, moved {rep["moved_vertices"]}, tinted {rep["tinted_pixels"]}");
        Assert.True(rep["ok"]!.GetValue<bool>(), rep["error"]?.ToString());
        return outGltf;
    }

    private ModelImportAnalysis RoundTrip(string model, ModelSubject subject, ModelExportMode mode, object? edit, string tag, long? display = null)
    {
        var res = _svc.Export(model, subject, mode, Path.Combine(_root, tag), currentDisplay: display);
        _out.WriteLine($"{model}: exported {res.Manifest.Variants.Count} look(s), {res.Manifest.Textures.Count} textures to {res.Folder}");
        var back = ThroughBlender(res.GltfFiles[0], Path.Combine(res.Folder, "blender_export", Path.GetFileName(res.GltfFiles[0])), edit);
        if (back == null) return null!;
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());   // the package folder: finds blender_export\
        Assert.True(a.Edited.FromBlender, a.Edited.Description);
        Log(a);
        return a;
    }

    [Theory]
    [InlineData(Boar, ModelSubject.Creature, ModelExportMode.AllVariants)]
    [InlineData(Tree, ModelSubject.Plain, ModelExportMode.CurrentLook)]
    [InlineData(Axe, ModelSubject.Item, ModelExportMode.AllVariants)]
    [InlineData(Orc, ModelSubject.Character, ModelExportMode.CurrentLook)]
    public void UnchangedThroughBlenderIsNoChange(string model, ModelSubject subject, ModelExportMode mode)
    {
        var a = RoundTrip(model, subject, mode, null, "same_" + Path.GetFileNameWithoutExtension(model));
        if (a == null) return;
        Assert.True(a.NothingChanged, "a change was found in an unchanged round trip");
        Assert.False(a.AnythingToImport);
        if (subject == ModelSubject.Character) Assert.Contains("every orc male player and NPC", a.UsersWarning);
    }

    [Theory]
    [InlineData(Boar, ModelSubject.Creature, 1, 0, 0.9f)]
    [InlineData(Tree, ModelSubject.Plain, 0, 2, 12.0f)]
    [InlineData(Axe, ModelSubject.Item, 0, 0, 0.5f)]
    public void ScriptedEditThroughBlenderChangesOnlyTheEditedParts(string model, ModelSubject subject, int submesh, int axis, float above)
    {
        var res = _svc.Export(model, subject, ModelExportMode.CurrentLook, Path.Combine(_root, "edit_" + Path.GetFileNameWithoutExtension(model)));
        var tex = res.Manifest.Textures.First(t => t.Editable && res.Manifest.Materials.Any(mm => mm.Submesh == submesh && mm.Texture == t.Id));
        var edit = new
        {
            move = new[] { new { submesh, axis, above, offset = new[] { 0.0, 0.0, 0.12 } } },
            tint = new[] { new { texture_id = tex.Id, rect = new[] { 0.1, 0.1, 0.45, 0.35 }, color = new[] { 1.0, 0.1, 0.1 } } },
        };
        var back = ThroughBlender(res.GltfFiles[0], Path.Combine(res.Folder, "blender_export", Path.GetFileName(res.GltfFiles[0])), edit);
        if (back == null) return;
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Log(a);
        var mesh = a.Changes.Single(c => c.Kind == ModelChangeKind.Mesh);
        Assert.False(mesh.Unchanged);
        Assert.True(mesh.CanInclude, string.Join("; ", mesh.Rules.Where(r => r.Level == RuleLevel.Blocker)));
        Assert.Equal(new[] { submesh }, mesh.Parts.Where(p => p.Changed).Select(p => p.Submesh).ToArray());
        var texChanges = a.Changes.Where(c => c.Kind == ModelChangeKind.Texture && !c.Unchanged).ToList();
        Assert.Equal(tex.GamePath, texChanges.Single().GamePath);
        Assert.True(texChanges[0].CanInclude);

        // commit, then compare with the original: only the mesh blocks differ, untouched parts keep their bytes
        var cs = ModelImportCommitter.Commit(a, _ws, null);
        Assert.Equal(2, cs.Files.Count);
        var orig = M2Model.Load(TestEnv.Client.Read(model)!);
        var edited = M2Model.Load(_ws.ReadEdited(model)!);
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(edited)));
        var co = ModelExporter.PartChecksums(orig.File);
        var ce = ModelExporter.PartChecksums(edited.File);
        foreach (var k in new[] { "skeleton_and_animations", "materials", "attachments_events_effects", "collision" }) Assert.Equal(co[k], ce[k]);
        Assert.NotEqual(co["vertices"], ce["vertices"]);
        // sequences: everything but the recomputed bounds is unchanged
        Assert.Equal(orig.Sequences.Length, edited.Sequences.Length);
        for (int i = 0; i < orig.Sequences.Length; i++)
        {
            var (x, y) = (orig.Sequences[i], edited.Sequences[i]);
            Assert.Equal((x.AnimId, x.SubId, x.Start, x.End, x.MoveSpeed, x.Flags, x.Frequency, x.BlendTime, x.NextVariant, x.NextAlias),
                (y.AnimId, y.SubId, y.Start, y.End, y.MoveSpeed, y.Flags, y.Frequency, y.BlendTime, y.NextVariant, y.NextAlias));
        }
        // view 0 keeps one submesh per part: untouched parts have the same triangles and vertex bytes, the edited one differs
        Assert.Equal(orig.Views[0].Submeshes.Length, edited.Views[0].Submeshes.Length);
        for (int s = 0; s < orig.Views[0].Submeshes.Length; s++)
        {
            var a1 = ModelIOTests.SubmeshRecords(orig, s);
            var b1 = ModelIOTests.SubmeshRecords(edited, s);
            if (s == submesh) Assert.NotEqual(a1, b1);
            else Assert.Equal(a1, b1);
        }
        // the animations still play: every sequence evaluates to finite, moving vertices
        var mats = new Matrix4x4[edited.Bones.Length];
        for (int si = 0; si < edited.Sequences.Length; si++)
        {
            var s = edited.Sequences[si];
            M2Anim.BoneMatrices(edited, new M2Anim.Time(si, s.Start, s.Duration / 2, s.Duration / 2), mats);
            foreach (var v in edited.Vertices)
            {
                var p = M2Bounds.Skin(v, mats);
                Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
            }
        }
        // all four views carry the edit and fit their bone limits
        for (int vi = 0; vi < edited.Views.Length; vi++)
        {
            Assert.All(edited.Views[vi].Submeshes, s => Assert.True(s.BoneCount <= edited.Views[vi].BoneLimit));
            Assert.Equal(edited.Views[0].Triangles.Length, edited.Views[vi].Triangles.Length);
        }
        // the 3D viewer builds it
        var asset = WoWDataStudio.Rendering.ModelAsset.Build(model, _ws.ReadEdited(model)!, _ => null);
        Assert.Equal(4, asset.Views.Length);
        // revert the import: both files go back to the game's
        _ws.RevertChangeSet(cs.Id);
        Assert.False(_ws.IsModified(model));
        Assert.False(_ws.IsModified(tex.GamePath!));
    }

    [Fact]
    public void AddonZipInstallsIntoBlenderAndOpenInBlenderImports()
    {
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            _out.WriteLine("Blender 4.5 not found: skipped.");
            return;
        }
        // the zip, installed with Blender's own installer into a temporary Blender profile (the user's Blender is not touched)
        var zip = BlenderTools.BuildZip(AddonRoot, Path.Combine(_root, "addon", "wowdata_blender.zip"));
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            Assert.Contains(z.Entries, e => e.FullName == "wowdata_blender/__init__.py");
        var env = new Dictionary<string, string>
        {
            ["BLENDER_USER_SCRIPTS"] = Path.Combine(_root, "blender_profile", "scripts"),
            ["BLENDER_USER_CONFIG"] = Path.Combine(_root, "blender_profile", "config"),
        };
        var r = BlenderTools.Install(blender, AddonRoot, zip, Path.Combine(_root, "addon"), env);
        _out.WriteLine($"install: {r.Ok} {r.Message} {r.AddonFile}");
        Assert.True(r.Ok, r.Message + Environment.NewLine + r.Details);
        Assert.StartsWith(Path.Combine(_root, "blender_profile"), r.AddonFile!, StringComparison.OrdinalIgnoreCase);

        // "Open in Blender": Blender with its window, the package imported by the add-on (self-test mode closes it)
        var res = _svc.Export(Tree, ModelSubject.Plain, ModelExportMode.CurrentLook, Path.Combine(_root, "open"));
        var report = Path.Combine(_root, "open_report.json");
        var psi = new System.Diagnostics.ProcessStartInfo(blender) { UseShellExecute = false };
        foreach (var a in new[] { "--factory-startup", "--python", Path.Combine(AddonRoot, "studio", "studio_open.py"), "--", res.GltfFiles[0], AddonRoot, BlenderTools.AddonVersion(AddonRoot) })
            psi.ArgumentList.Add(a);
        psi.Environment["WOWDATA_OPEN_REPORT"] = report;
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(report) && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(250);
            Thread.Sleep(1500);
        }
        Assert.True(File.Exists(report), "Blender did not open the package");
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        _out.WriteLine(rep.ToJsonString());
        Assert.False(rep["background"]!.GetValue<bool>());
        Assert.True(rep["import"]!["manifest"]!.GetValue<bool>());
    }
}
