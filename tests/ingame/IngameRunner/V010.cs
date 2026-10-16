using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace IngameRunner;

/// <summary>
/// The in-game visual test of WoW Data Studio 0.9.0 (server data) and 0.10.0 (skeleton and animations), README in
/// tests\ingame\run_v010.ps1. Everything goes through the app's own Core (the same calls as the app and wds), in an
/// isolated Data Studio home (RUNDIR\studio_home):
///   IngameRunner v010 prepare CLIENT HOME RUNDIR [A1 A4 A5 S ...]   the client edits (Blender headless) and the server edits (workspace + SQL)
///   IngameRunner v010 server  CLIENT HOME RUNDIR plan|apply|undo|status [--port N --user U --no-password --state DIR]
///   IngameRunner v010 wdb     CLIENT HOME RUNDIR                     "Clear the game's cache (WDB)" of CLIENT
///   IngameRunner v010 dryrun  CLIENT HOME RUNDIR DUMP.sql            apply + undo against a throwaway MySQL loaded from DUMP.sql
/// Never writes the live database except "server apply" / "server undo" (the run script's two steps).
/// </summary>
public static class V010
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    const string Boar = @"Creature\Boar\Boar.m2", Quil = @"Creature\Quillboar\QuillBoar.m2", Scorp = @"Creature\Scorpion\Scorpion.m2";
    // custom IDs (the app's default custom ID range 900000-999999; asserted below)
    public const long ItemBlade = 900000, ItemDraught = 900001, SpellDraught = 900000, Vendor = 900000;
    // where things are (Kalimdor): the start point of the test character and the vendor 3.5 yd north of it, facing it
    public const float StartX = -618.5f, StartY = -4251.7f, VendorX = -615.0f, VendorY = -4251.7f, VendorO = 3.14159f;

    static string AddonRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\blender_addon"));
    static string ScriptDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\blender"));
    static string ServerDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\..\Server"));

    public static int Run(string[] a)
    {
        if (a.Length < 5) { Console.Error.WriteLine("usage: IngameRunner v010 prepare|server|wdb|dryrun CLIENT HOME RUNDIR ..."); return 2; }
        string sub = a[1], client = a[2], home = a[3], run = a[4];
        Directory.CreateDirectory(run);
        return sub switch
        {
            "prepare" => Prepare(client, home, run, a.Skip(5).ToArray()),
            "server" => Server(client, home, run, a.Skip(5).ToArray()),
            "wdb" => Wdb(client, home, run),
            "dryrun" => DryRun(client, home, run, a[5]),
            _ => throw new ArgumentException("unknown v010 command " + sub),
        };
    }

    internal static AppSession Open(string client, string home)
    {
        var paths = new AppPaths(home);
        var settings = AppSettings.Load(paths);
        var s = new AppSession(paths, settings);
        s.OpenAsync(client, false, null).GetAwaiter().GetResult();
        return s;
    }

    // ================================================================== prepare

    static string _run = "", _app = "";
    static AppSession _s = null!;
    static Workspace Ws => _s.Workspace!;

    static int Prepare(string client, string home, string run, string[] only)
    {
        _run = run;
        _app = Path.Combine(run, "app");
        Directory.CreateDirectory(_app);
        _s = Open(client, home);
        var reportPath = Path.Combine(run, "app_report.json");
        var all = File.Exists(reportPath) ? JsonSerializer.Deserialize<List<ItemReport>>(File.ReadAllText(reportPath)) ?? new() : new List<ItemReport>();
        var groups = new (string Id, Func<List<ItemReport>> Fn)[]
        {
            ("A1", BoarRig),          // A1 + A2 + A3 (one Blender session and one import, as a user would do them together)
            ("A4", QuilAttachment),
            ("A5", ScorpionAttack),
            ("S", ServerEdits),       // S1 + S2 + S3
        };
        foreach (var (id, fn) in groups)
        {
            if (only.Length > 0 && !only.Contains(id)) continue;
            Console.WriteLine($"==== {id}");
            List<ItemReport> reps;
            try { reps = fn(); }
            catch (Exception e)
            {
                reps = new() { new ItemReport { Id = id, Ok = false, Error = e.ToString(), Verdict = "FAIL (exception): " + e.Message } };
            }
            foreach (var r in reps)
            {
                Console.WriteLine($"  {r.Id} -> {(r.Ok ? "ok" : "PROBLEM")}: {r.Verdict}");
                all.RemoveAll(x => x.Id == r.Id);
                all.Add(r);
            }
            File.WriteAllText(reportPath, JsonSerializer.Serialize(all.OrderBy(x => x.Id).ToList(), J));
        }
        var list = Ws.List().Select(v => new { v.Record.Path, v.Record.Kind, v.Record.ChangeSet, Set = Ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet)?.Label, v.StatusText, v.Record.Summary }).ToList();
        File.WriteAllText(Path.Combine(run, "workspace_changes.json"), JsonSerializer.Serialize(list, J));
        Console.WriteLine($"workspace: {list.Count} files in {Ws.Manifest.ChangeSets.Count} change sets");
        WriteViewpoints(run);
        _s.Dispose();
        return all.All(x => x.Ok) ? 0 : 1;
    }

    // ------------------------------------------------------------------ Blender

    static JsonNode Blender(string tag, string gltfIn, string gltfOut, object edit)
    {
        var blender = BlenderTools.FindBlender() ?? throw new InvalidOperationException("Blender not found");
        var dir = Path.Combine(_run, "blender");
        Directory.CreateDirectory(dir);
        var editFile = Path.Combine(dir, tag + "_edit.json");
        File.WriteAllText(editFile, JsonSerializer.Serialize(edit, J));
        var report = Path.Combine(dir, tag + "_report.json");
        if (File.Exists(report)) File.Delete(report);
        var sw = Stopwatch.StartNew();
        var res = BlenderTools.RunHeadless(blender, Path.Combine(ScriptDir, "ingame_rig_edit.py"), new[] { AddonRoot, report, gltfIn, gltfOut, editFile }, 900_000);
        File.WriteAllText(Path.Combine(dir, tag + "_blender_output.txt"), res.Output);
        if (!File.Exists(report)) throw new InvalidOperationException($"Blender wrote no report ({tag}): {res.Output[^Math.Min(2000, res.Output.Length)..]}");
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        Console.WriteLine($"  Blender {rep["blender"]} {tag}: {sw.Elapsed.TotalSeconds:0} s, ok={rep["ok"]}");
        if (rep["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"Blender failed ({tag}): {rep["error"]}");
        return rep;
    }

    static ModelExportResult ExportRig(string model, long display, string tag)
    {
        if (Ws.IsModified(model)) Ws.Revert(model);
        var folder = Path.Combine(_run, "packages", tag);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        return _s.ModelPackages!.Export(model, ModelSubject.Creature, ModelExportMode.CurrentLook, folder, currentDisplay: display, includeSkeleton: true);
    }

    static string BlenderOut(ModelExportResult res) => Path.Combine(res.Folder, "blender_export", Path.GetFileName(res.GltfFiles[0]));

    static void LogAnalysis(ItemReport r, ModelImportAnalysis a)
    {
        r.Validation.Add($"import source: {a.Edited.Description}; base: {a.BaseDescription}");
        foreach (var g in a.General) r.Validation.Add($"[{g.Level}] {g.Text}");
        if (a.UsersWarning.Length > 0) r.Validation.Add("users: " + a.UsersWarning);
        foreach (var c in a.Changes)
        {
            r.Validation.Add($"row {c.Kind} \"{c.Title}\": {(c.Unchanged ? "unchanged" : c.Include ? "INCLUDED" : "not included")}, can include={c.CanInclude}; {c.Detail}");
            foreach (var x in c.Rules) r.Validation.Add($"    [{x.Level}] {x.Text}");
            foreach (var l in c.Lines) r.Validation.Add("    " + l);
            if (c.Kind == ModelChangeKind.Mesh) foreach (var p in c.Parts) r.Compare.Add(p.Summary);
            if (c.Rebuild != null) r.Compare.Add($"rebuild: {c.Rebuild}");
        }
        if (a.Rig != null)
        {
            r.Compare.Add($"skeleton row: {a.Rig.Summary}");
            foreach (var l in a.Rig.Changes) r.Compare.Add("  " + l);
            foreach (var x in a.Rig.Rules) r.Validation.Add($"  rig [{x.Level}] {x.Text}");
        }
    }

    static ChangeSet Commit(ItemReport r, ModelImportAnalysis a, string label, params ModelChangeKind[] expected)
    {
        var rows = a.Changes.Where(c => !c.Unchanged).ToList();
        var blocked = rows.Where(c => c.HasBlocker || !c.CanInclude).ToList();
        var kinds = rows.Select(c => c.Kind).Distinct().OrderBy(k => k).ToList();
        if (blocked.Count > 0 || !a.AnythingToImport || !expected.All(kinds.Contains) || kinds.Any(k => !expected.Contains(k)))
            throw new InvalidOperationException($"app-level problem: changed rows {string.Join(", ", kinds)} (expected {string.Join(", ", expected)}), {blocked.Count} blocked: " +
                string.Join("; ", blocked.SelectMany(b => b.Rules.Where(x => x.Level == RuleLevel.Blocker).Select(x => x.Text))));
        var cs = ModelImportCommitter.Commit(a, Ws, _s.Index, label);
        r.ChangeSets.Add($"{cs.Id} \"{cs.Label}\": {string.Join(", ", cs.Files.Select(k => Ws.Manifest.Files[k].Path))}");
        foreach (var k in cs.Files) { var rec = Ws.Manifest.Files[k]; r.Files.Add(rec.Path); r.Notes.Add($"{rec.Path}: {rec.Summary}"); }
        return cs;
    }

    static (M2Model Orig, M2Model Ed, RigInfo OI, RigInfo EI) Saved(ItemReport r, string model)
    {
        var orig = M2Model.Load(_s.Data!.Read(model)!);
        var ed = M2Model.Load(Ws.ReadEdited(model)!);
        var errs = M2Validator.Errors(M2Validator.Validate(ed));
        r.Validation.Add($"validator on the saved model: {(errs.Count == 0 ? "no errors" : string.Join("; ", errs))}");
        var mats = new Matrix4x4[ed.Bones.Length];
        int bad = 0;
        for (int si = 0; si < ed.Sequences.Length; si++)
        {
            var sq = ed.Sequences[si];
            foreach (var t in new[] { 0u, sq.Duration / 2, sq.Duration > 0 ? sq.Duration - 1 : 0 })
            {
                M2Anim.BoneMatrices(ed, new M2Anim.Time(si, sq.Start, t, t), mats);
                foreach (var v in ed.Vertices) { var p = M2Bounds.Skin(v, mats); if (!float.IsFinite(p.X + p.Y + p.Z)) bad++; }
            }
        }
        r.Validation.Add($"every animation evaluated at start / middle / end: {(bad == 0 ? "all vertices finite" : bad + " non-finite vertex positions")}");
        if (errs.Count > 0 || bad > 0) r.Notes.Add("PROBLEM: the saved model fails the validator or its animations");
        r.Compare.Add($"bones {orig.Bones.Length} -> {ed.Bones.Length}; vertices {orig.Vertices.Length} -> {ed.Vertices.Length}; sequences {orig.Sequences.Length} -> {ed.Sequences.Length}; attachments {orig.Attachments.Length} -> {ed.Attachments.Length}");
        var cat = _s.Models!.Animations;
        return (orig, ed, RigInfo.Read(orig, cat), RigInfo.Read(ed, cat));
    }

    static bool SavedOk(ItemReport r) => !r.Notes.Any(n => n.StartsWith("PROBLEM", StringComparison.Ordinal));

    static Matrix4x4[] Pose(M2Model m, int seq, uint t)
    {
        var mats = new Matrix4x4[m.Bones.Length];
        M2Anim.BoneMatrices(m, new M2Anim.Time(seq, m.Sequences[seq].Start, t, t), mats);
        return mats;
    }

    static float MaxDiff(Matrix4x4 a, Matrix4x4 b)
    {
        var x = a - b;
        return new[] { x.M11, x.M12, x.M13, x.M14, x.M21, x.M22, x.M23, x.M24, x.M31, x.M32, x.M33, x.M34, x.M41, x.M42, x.M43, x.M44 }.Max(Math.Abs);
    }

    // ------------------------------------------------------------------ A1 + A2 + A3: the Mottled Boar

    static List<ItemReport> BoarRig()
    {
        var a1 = new ItemReport { Id = "A1", Title = "Walk cycle retimed: Mottled Boar (Boar.m2, display 503) Walk 1.0 s -> 1.5 s (Blender: keys and markers scaled x1.5)" };
        var a2 = new ItemReport { Id = "A2", Title = "New idle animation: Boar.m2 \"Stand [4]\" made in Blender from Stand (head turned 35 deg in the middle, the new spike swings), chance 100 % before scaling" };
        var a3 = new ItemReport { Id = "A3", Title = "New bone and part: a spike (8-sided cone) on the boar's head, weighted 1.0 to a new bone under bone 21 (Head), swinging in Stand [4]" };
        const string how = "Model path with the skeleton (0.10.0): Export for Blender with \"Skeleton and animations (for editing)\" (ModelPackageService.Export, " +
                           "includeSkeleton) -> Blender 4.5 headless with the app's add-on (import, scripted edits, File > Export > WoW 1.12 model for WoW Data Studio, " +
                           "wowdata_rig.json) -> Import edited model (ModelImportAnalyzer: the mesh row and the 'Skeleton and animations' row) -> one commit " +
                           "(ModelImportCommitter). The three boar items are one Blender session and one import, as the manual's examples would be done together.";
        foreach (var r in new[] { a1, a2, a3 }) r.How = how;
        var res = ExportRig(Boar, 503, "A1_boar");
        foreach (var r in new[] { a1, a2, a3 }) r.Notes.Add($"package {res.Folder}: rig in manifest: {res.Manifest.Rig != null}");
        var info = Blender("A1_boar_report", res.GltfFiles[0], BlenderOut(res), new { report_only = true });
        var head = info["bones"]!.AsArray().FirstOrDefault(b => b!["wow_bone"]?.GetValue<int>() == 21);
        a3.Notes.Add($"head bone in Blender: {head?["name"]} with {head?["vertices"]} vertices, top z {head?["top"]}");
        var edit = new
        {
            retime = new[] { new { action = "Walk", factor = 1.5 } },
            spike = new { name = "bone_spike", parent_wow_bone = 21, height_factor = 1.6, radius_factor = 0.15, sides = 8 },
            new_action = new { from = "Stand", name = "Stand [4]", chance = 100.0, rotate = new[] { new { bone_wow = 21, axis = "X", degrees = 35.0 } }, spike_degrees = 45.0 },
        };
        var rep = Blender("A1_boar_edit", res.GltfFiles[0], BlenderOut(res), edit);
        foreach (var r in new[] { a1, a2, a3 }) r.Notes.Add("Blender: " + string.Join(" | ", rep["done"]!.AsArray().Select(x => x!.ToString())));
        a3.Notes.Add("spike: " + rep["spike"]?.ToJsonString());
        var an = ModelImportAnalyzer.Analyze(res.Folder, _s.ModelPackages!.ImportContext());
        foreach (var r in new[] { a1, a2, a3 }) LogAnalysis(r, an);
        var cs = Commit(a1, an, "Test A1-A3: boar walk x1.5, new idle Stand [4], spike bone and part (Blender)", ModelChangeKind.Mesh, ModelChangeKind.Skeleton);
        a2.ChangeSets.AddRange(a1.ChangeSets); a3.ChangeSets.AddRange(a1.ChangeSets);
        a2.Files.AddRange(a1.Files); a3.Files.AddRange(a1.Files);
        var (orig, ed, oi, ei) = Saved(a1, Boar);
        a2.Validation.AddRange(a1.Validation.Where(v => v.StartsWith("validator", StringComparison.Ordinal) || v.StartsWith("every animation", StringComparison.Ordinal)));
        a3.Validation.AddRange(a2.Validation.Where(v => v.StartsWith("validator", StringComparison.Ordinal) || v.StartsWith("every animation", StringComparison.Ordinal)));
        bool baseOk = SavedOk(a1);

        // A1: Walk retimed, move speed recomputed: the stride per cycle (speed x duration) stays the same, so the feet do not slide
        var ow = oi.Sequences.First(x => x.AnimId == 4 && x.Variation == 0);
        var nw = ei.Sequences.First(x => x.AnimId == 4 && x.Variation == 0);
        double strideO = ow.MoveSpeed * ow.Duration / 1000.0, strideN = nw.MoveSpeed * nw.Duration / 1000.0;
        a1.Compare.Add($"Walk: {ow.Duration} ms at {ow.MoveSpeed:0.###} yd/s -> {nw.Duration} ms at {nw.MoveSpeed:0.###} yd/s; distance per cycle {strideO:0.###} -> {strideN:0.###} yd");
        // the poses of the retimed walk at the same phase are the original's
        float worst = 0;
        for (int k = 0; k < 8; k++)
        {
            var po = Pose(orig, ow.Index, (uint)(ow.Duration * k / 8));
            var pn = Pose(ed, nw.Index, (uint)(nw.Duration * k / 8));
            for (int b = 0; b < orig.Bones.Length; b++) worst = Math.Max(worst, MaxDiff(po[b], pn[b]));
        }
        a1.Compare.Add($"Walk poses at 8 equal phases, original vs retimed: largest bone matrix difference {worst:0.0000}");
        a1.Ok = baseOk && nw.Duration == 1500 && Math.Abs(strideN - strideO) < 0.01 * strideO && worst < 0.02f;
        a1.Verdict = a1.Ok ? $"app steps passed: Walk 1.0 s -> 1.5 s, move speed {ow.MoveSpeed:0.##} -> {nw.MoveSpeed:0.##} yd/s (same distance per cycle: no sliding), same poses per phase" : "app-level problem: see compare";

        // A2: the new idle
        var s4 = ei.Sequences.FirstOrDefault(x => x.Name == "Stand [4]");
        var stands = ei.Sequences.Where(x => x.AnimId == 0).ToList();
        a2.Compare.Add("Stand variations after the import: " + string.Join(", ", stands.Select(x => $"{x.Name} {x.Duration} ms {x.ChancePercent:0.#} %")) + $" (sum {stands.Sum(x => x.ChancePercent):0.#} %)");
        bool headMoves = false;
        if (s4 != null)
        {
            var st = ei.Sequences.First(x => x.AnimId == 0 && x.Variation == 0);
            var pm = Pose(ed, s4.Index, s4.Duration / 2);
            var ps = Pose(ed, st.Index, st.Duration / 2);
            float d = MaxDiff(pm[21], ps[21]);
            headMoves = d > 0.05f;
            a2.Compare.Add($"head bone (21) at mid-time, Stand [4] vs Stand: matrix difference {d:0.000}");
        }
        a2.Ok = baseOk && s4 != null && headMoves && Math.Abs(stands.Sum(x => x.ChancePercent) - 100) < 1.5;
        a2.Verdict = a2.Ok ? $"app steps passed: new animation Stand [4] ({s4!.Duration} ms, {s4.ChancePercent:0.#} % after scaling), head moves, chances add up to 100 %" : "app-level problem: see compare";

        // A3: the new bone and its part
        int nb = ed.Bones.Length - 1;
        int weighted = ed.Vertices.Count(v => (v.B0 == nb && v.W0 > 0) || (v.B1 == nb && v.W1 > 0) || (v.B2 == nb && v.W2 > 0) || (v.B3 == nb && v.W3 > 0));
        var newBones = ei.Bones.Where(b => b.Index >= orig.Bones.Length).Select(b => $"{b.Index} {b.Name} parent {b.Parent}").ToList();
        a3.Compare.Add($"new bones: {string.Join(", ", newBones)}; vertices weighted to bone {nb}: {weighted}; bone {nb} parent {ed.Bones[nb].Parent} (head = 21)");
        int kept = orig.Vertices.Count(v => ed.Vertices.Any(w => Same(v, w)));
        a3.Compare.Add($"original vertices found unchanged (position, normal, UV, weights) in the edit: {kept} of {orig.Vertices.Length}; exact duplicates among the original's vertices: {Dups(orig)}, in the edit: {Dups(ed)}");
        bool spikeSwings = false;
        if (s4 != null)
        {
            var p1 = Pose(ed, s4.Index, s4.Duration / 4);
            var p3 = Pose(ed, s4.Index, s4.Duration * 3 / 4);
            spikeSwings = MaxDiff(p1[nb], p3[nb]) > 0.05f;
            a3.Compare.Add($"spike bone at 1/4 vs 3/4 of Stand [4]: matrix difference {MaxDiff(p1[nb], p3[nb]):0.000}");
        }
        a3.Ok = baseOk && ed.Bones.Length == orig.Bones.Length + 1 && ed.Bones[nb].Parent == 21 && weighted >= 8 && ed.Vertices.Length > orig.Vertices.Length && spikeSwings;
        a3.Verdict = a3.Ok ? $"app steps passed: 1 new bone ({nb}, child of the head), {ed.Vertices.Length - orig.Vertices.Length} new vertices weighted to it, it swings in Stand [4]" : "app-level problem: see compare";
        foreach (var r in new[] { a1, a2, a3 })
        {
            var union = Box(orig, ed);
            r.Images.Add(Save(r, "Boar_rest_original", MeshPicture.Render(orig, union)));
            r.Images.Add(Save(r, "Boar_rest_edit", MeshPicture.Render(ed, union)));
        }
        return new() { a1, a2, a3 };
    }

    static bool Same(M2Vertex a, M2Vertex b) =>
        Vector3.Distance(a.Position, b.Position) < 1e-4f && Vector3.Distance(a.Normal, b.Normal) < 1e-3f && Vector2.Distance(a.Uv, b.Uv) < 1e-4f &&
        a.B0 == b.B0 && a.B1 == b.B1 && a.B2 == b.B2 && a.B3 == b.B3 && a.W0 == b.W0 && a.W1 == b.W1 && a.W2 == b.W2 && a.W3 == b.W3;

    static int Dups(M2Model m)
    {
        int n = 0;
        for (int i = 0; i < m.Vertices.Length; i++)
            for (int j = 0; j < i; j++)
                if (Same(m.Vertices[i], m.Vertices[j])) { n++; break; }
        return n;
    }

    static (Vector3, Vector3) Box(M2Model a, M2Model b)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var m in new[] { a, b }) foreach (var v in m.Vertices) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
        return (lo, hi);
    }

    static string Save(ItemReport r, string name, WoWData.Core.Imaging.RgbaImage img)
    {
        var f = Path.Combine(_app, $"{r.Id}_{name}.png");
        WoWData.Core.Imaging.Png.Save(img, f, !img.IsOpaque());
        return Path.GetRelativePath(_run, f);
    }

    // ------------------------------------------------------------------ A4: the Razormane Quilboar's weapon attachment

    static List<ItemReport> QuilAttachment()
    {
        var r = new ItemReport
        {
            Id = "A4",
            Title = "Weapon attachment point moved: Razormane Quilboar (QuillBoar.m2, display 1218, holds item 1901) - HandRight (id 1) 0.6 yd up at the rest pose",
            How = "Export for Blender with the skeleton -> Blender headless moves the empty ATT_01_HandRight (rest pose, world +Z 0.6) -> Import edited model " +
                  "(the skeleton row says 'attachment point HandRight moved by ...') -> commit. The game attaches the creature's weapon there.",
        };
        var res = ExportRig(Quil, 1218, "A4_quilboar");
        var rep = Blender("A4_quilboar_edit", res.GltfFiles[0], BlenderOut(res), new { move_attachment = new { id = 1, world_offset = new[] { 0.0, 0.0, 0.6 } } });
        r.Notes.Add("Blender: " + string.Join(" | ", rep["done"]!.AsArray().Select(x => x!.ToString())) + "; " + rep["attachment"]?.ToJsonString());
        var an = ModelImportAnalyzer.Analyze(res.Folder, _s.ModelPackages!.ImportContext());
        LogAnalysis(r, an);
        Commit(r, an, "Test A4: QuillBoar HandRight attachment 0.6 yd up (Blender)", ModelChangeKind.Skeleton);
        var (orig, ed, _, _) = Saved(r, Quil);
        var o1 = orig.Attachments.First(x => x.Id == 1);
        var e1 = ed.Attachments.First(x => x.Id == 1);
        var d = e1.Position - o1.Position;
        r.Compare.Add($"HandRight (id 1) on bone {o1.Bone} -> {e1.Bone}: position ({o1.Position.X:0.000},{o1.Position.Y:0.000},{o1.Position.Z:0.000}) -> ({e1.Position.X:0.000},{e1.Position.Y:0.000},{e1.Position.Z:0.000}), moved {d.Length():0.000} (dz {d.Z:0.000})");
        int others = orig.Attachments.Count(x => x.Id != 1 && ed.Attachments.First(y => y.Id == x.Id).Position != x.Position);
        r.Compare.Add($"other attachment points moved: {others}");
        r.Ok = SavedOk(r) && Math.Abs(d.Z - 0.6f) < 0.02f && Math.Abs(d.X) + Math.Abs(d.Y) < 0.02f && others == 0 && o1.Bone == e1.Bone;
        r.Verdict = r.Ok ? $"app steps passed: HandRight moved {d.Z:0.00} yd up (same bone), nothing else moved, validator clean" : "app-level problem: see compare";
        var union = Box(orig, ed);
        r.Images.Add(Save(r, "QuillBoar_rest", MeshPicture.Render(ed, union)));
        return new() { r };
    }

    // ------------------------------------------------------------------ A5: the scorpid's attack animations

    static List<ItemReport> ScorpionAttack()
    {
        var r = new ItemReport
        {
            Id = "A5",
            Title = "Combat animation edited: Scorpion.m2 (Scorpid Worker 2485; also the Clattering / Armored Scorpids south and east of Razor Hill) - in AttackUnarmed and AttackUnarmed [2] the stinger swells 1 -> 2.5x -> 1",
            How = "Export for Blender with the skeleton -> Blender headless adds scale keys (start 1, middle 2.5, end 1) on the stinger bone (the leaf bone whose " +
                  "vertices are highest) in both attack actions -> Import edited model (two edited animations, re-encoded at the end of the timeline) -> commit.",
        };
        var res = ExportRig(Scorp, 2485, "A5_scorpion");
        var rep = Blender("A5_scorpion_edit", res.GltfFiles[0], BlenderOut(res),
            new { scale_keys = new[] { new { actions = new[] { "AttackUnarmed", "AttackUnarmed [2]" }, bone = "auto_stinger", factor = 2.5 } } });
        r.Notes.Add("Blender: " + string.Join(" | ", rep["done"]!.AsArray().Select(x => x!.ToString())));
        var an = ModelImportAnalyzer.Analyze(res.Folder, _s.ModelPackages!.ImportContext());
        LogAnalysis(r, an);
        Commit(r, an, "Test A5: scorpion attacks - stinger swells x2.5 (Blender)", ModelChangeKind.Skeleton);
        var (orig, ed, oi, ei) = Saved(r, Scorp);
        var bname = rep["scaled_bone"]?.ToString() ?? "";
        int sb = int.TryParse(System.Text.RegularExpressions.Regex.Match(bname, @"bone_(\d+)").Groups[1].Value, out var v) ? v : -1;
        int edited = 0;
        foreach (var o in oi.Sequences.Where(x => x.AnimId == 16))
        {
            var e = ei.Sequences.First(x => x.AnimId == 16 && x.Variation == o.Variation);
            var po = Pose(orig, o.Index, o.Duration / 2);
            var pe = Pose(ed, e.Index, e.Duration / 2);
            float d = sb >= 0 ? MaxDiff(po[sb], pe[sb]) : 0;
            r.Compare.Add($"{e.Name}: {o.Duration} -> {e.Duration} ms; stinger bone {sb} at mid-time: matrix difference {d:0.000}; placed at {o.Start} -> {e.Start}");
            if (d > 0.3f) edited++;
        }
        int same = 0;
        foreach (var o in oi.Sequences.Where(x => x.AnimId != 16))
        {
            var e = ei.Sequences.First(x => x.AnimId == o.AnimId && x.Variation == o.Variation);
            var po = Pose(orig, o.Index, o.Duration / 2);
            var pe = Pose(ed, e.Index, e.Duration / 2);
            if (Enumerable.Range(0, orig.Bones.Length).All(b => MaxDiff(po[b], pe[b]) < 1e-4f)) same++;
        }
        r.Compare.Add($"the other {oi.Sequences.Count(x => x.AnimId != 16)} animations: {same} play exactly as before");
        r.Ok = SavedOk(r) && edited == 2 && same == oi.Sequences.Count(x => x.AnimId != 16);
        r.Verdict = r.Ok ? "app steps passed: both attack animations edited (stinger x2.5 at mid-time), every other animation unchanged" : "app-level problem: see compare";
        return new() { r };
    }

    // ------------------------------------------------------------------ S1 + S2 + S3: the server edits (workspace + SQL only)

    static List<ItemReport> ServerEdits()
    {
        var s1 = new ItemReport { Id = "S1", Title = $"New item {ItemBlade} \"WDS Test Blade\" (copy of Worn Shortsword 25): epic, +25 Strength, +20 Stamina, 30-55 damage, display 30606 (Thunderfury's look, icon INV_Sword_39)" };
        var s2 = new ItemReport { Id = "S2", Title = $"New vendor NPC {Vendor} \"WDS Test Vendor\" <WoW Data Studio> (copy of Duokna 3158, vendor flag only), spawned 3.5 yd north of the test character's start point, selling {ItemBlade} and {ItemDraught}" };
        var s3 = new ItemReport { Id = "S3", Title = $"Client <-> server: new spell {SpellDraught} \"WDS Test Draught\" (copy of 439 Healing Potion, heals 900-1000) on new item {ItemDraught}; Check client and server finds the game lacks it -> Make the game match the server (new Spell.dbc row)" };
        const string how = "Server data tab path (0.9.0) through Core: ServerEditor.Duplicate / Add / Set (the app's checks), Save to my workspace (one change set, " +
                           "ServerData\\mangos\\<table>.json), Check client and server (ServerConsistency.Check) and Make the game match the server (MatchClient: a " +
                           "Data tables edit of Spell.dbc), Export SQL patch (ServerSql.Build) and the Apply to server plan (ServerApplier.Plan). NOTHING is applied here.";
        foreach (var r in new[] { s1, s2, s3 }) r.How = how;
        var svc = _s.ServerData ?? throw new InvalidOperationException("no server data service");
        var (cs, ce) = svc.CustomIds;
        if (cs != 900000 || ce < 900001) throw new InvalidOperationException($"custom ID range is {cs}-{ce}, expected 900000-999999");
        // a fresh start in this (isolated) workspace: revert earlier server edits and the Spell.dbc edit of an earlier prepare
        foreach (var p in svc.WorkspaceFiles()) Ws.Revert(p);
        if (Ws.IsModified(@"DBFilesClient\Spell.dbc")) Ws.Revert(@"DBFilesClient\Spell.dbc");
        var ed = svc.NewEditor();
        string K(string table, string id) => ed.ResolveKey(ed.Table(table), id);
        void Need(ItemReport r, ServerEditResult res, string what)
        {
            r.Validation.Add($"{what}: {res.Message}" + (res.Problems.Count > 0 ? " [" + string.Join(" | ", res.Problems.Select(p => (p.IsError ? "error " : "warning ") + p.Column + ": " + p.Message)) + "]" : ""));
            if (!res.Ok) throw new InvalidOperationException($"{what} refused: {res.Message}");
        }
        // free IDs (the first free number of the custom range must be the planned one: nothing of an earlier test is left on the server)
        foreach (var (t, want) in new[] { ("item_template", ItemBlade), ("spell_template", SpellDraught), ("creature_template", Vendor) })
        {
            var got = ed.SuggestId(ed.Table(t));
            if (got != want) throw new InvalidOperationException($"{t}: the first free custom ID is {got}, expected {want} (rows of an earlier test on the server?)");
        }
        // S1
        var (kBlade, r1) = ed.Duplicate("item_template", K("item_template", "25"), new (string, string?)[]
        {
            ("name", "WDS Test Blade"), ("description", "WoW Data Studio 0.9.0 in-game test item"), ("quality", "4"), ("display_id", "30606"),
            ("item_level", "60"), ("required_level", "1"), ("buy_price", "1"), ("sell_price", "0"),
            ("stat_type1", "4"), ("stat_value1", "25"), ("stat_type2", "7"), ("stat_value2", "20"),
            ("dmg_min1", "30"), ("dmg_max1", "55"),
        }, ItemBlade);
        Need(s1, r1, $"Duplicate item 25 as {ItemBlade}");
        // S3 (the server side first: the item needs the spell)
        var (kSpell, r3a) = ed.Duplicate("spell_template", K("spell_template", "439"), new (string, string?)[]
        {
            ("name", "WDS Test Draught"), ("description", "Restores $s1 health. (WoW Data Studio test)"), ("effectBasePoints1", "899"), ("effectDieSides1", "101"),
        }, SpellDraught);
        Need(s3, r3a, $"Duplicate spell 439 as {SpellDraught}");
        var (kDraught, r3b) = ed.Duplicate("item_template", K("item_template", "118"), new (string, string?)[]
        {
            ("name", "WDS Test Draught"), ("description", "WoW Data Studio 0.9.0 in-game test item"), ("quality", "3"), ("spellid_1", SpellDraught.ToString(CultureInfo.InvariantCulture)),
            ("buy_price", "1"), ("sell_price", "0"), ("required_level", "1"),
        }, ItemDraught);
        Need(s3, r3b, $"Duplicate item 118 as {ItemDraught}");
        // S2
        var src = K("creature_template", "3158");
        var (kVendor, r2) = ed.Duplicate("creature_template", src, new (string, string?)[]
        {
            ("name", "WDS Test Vendor"), ("subname", "WoW Data Studio"), ("npc_flags", "4"), ("vendor_id", "0"), ("gossip_menu_id", "0"),
            ("script_name", ""), ("ai_name", ""),
        }, Vendor);
        Need(s2, r2, $"Duplicate creature 3158 (Duokna) as {Vendor}");
        foreach (var (item, slot) in new[] { (ItemBlade, 0), (ItemDraught, 1) })
        {
            var (_, rv) = ed.Add("npc_vendor", new (string, string?)[] { ("entry", Vendor.ToString(CultureInfo.InvariantCulture)), ("item", item.ToString(CultureInfo.InvariantCulture)), ("slot", slot.ToString(CultureInfo.InvariantCulture)), ("maxcount", "0"), ("incrtime", "0") });
            Need(s2, rv, $"Add npc_vendor {Vendor} item {item}");
        }
        var ct = ed.Table("creature");
        var spawn = ed.Rows(ct, RowFilter.Eq("id", 3158), 1).FirstOrDefault() ?? throw new InvalidOperationException("Duokna has no spawn to copy");
        var z = GroundZ(VendorX, VendorY);
        var (kSpawn, r2b) = ed.Duplicate("creature", ct.KeyText(spawn), new (string, string?)[]
        {
            ("id", Vendor.ToString(CultureInfo.InvariantCulture)), ("map", "1"),
            ("position_x", VendorX.ToString("0.###", CultureInfo.InvariantCulture)), ("position_y", VendorY.ToString("0.###", CultureInfo.InvariantCulture)),
            ("position_z", z.ToString("0.###", CultureInfo.InvariantCulture)), ("orientation", VendorO.ToString("0.#####", CultureInfo.InvariantCulture)),
            ("wander_distance", "0"), ("movement_type", "0"), ("spawntimesecsmin", "60"), ("spawntimesecsmax", "60"),
        });
        Need(s2, r2b, $"Duplicate Duokna's spawn {ct.KeyText(spawn)} as a spawn of {Vendor} at ({VendorX}, {VendorY}, {z:0.##}) (ground height from the game's map)");
        var blocking = ed.BlockingProblems();
        if (blocking.Count > 0) throw new InvalidOperationException("blocking problems: " + string.Join("; ", blocking));
        var (recs, msg) = ed.Save("Test S1-S3: test items, spell and vendor (new rows only)");
        foreach (var r in new[] { s1, s2, s3 }) { r.ChangeSets.Add(msg); r.Files.AddRange(recs.Select(x => x.Path)); }
        // the details panel's view of the new rows (what the app shows)
        var b = new ServerBrowser(ed);
        void Show(ItemReport r, string table, string key)
        {
            var rec = b.Record(table, key);
            r.Notes.Add($"record {rec.Title} ({rec.Subtitle}); reload: {rec.ReloadText}");
            foreach (var f in rec.Fields.Where(f => !string.IsNullOrEmpty(f.Problem) || f.LinkMissing)) r.Notes.Add($"  {f.Name}: {f.Display} problem {f.Problem} link missing {f.LinkMissing}");
            foreach (var sct in rec.Sections.Where(sc => sc.Rows.Count > 0 || sc.Note != null).Take(8)) r.Notes.Add($"  section {sct.Title}: {sct.Rows.Count} row(s){(sct.Note != null ? " - " + sct.Note : "")}");
        }
        Show(s1, "item_template", kBlade!);
        Show(s3, "item_template", kDraught!);
        Show(s3, "spell_template", kSpell!);
        Show(s2, "creature_template", kVendor!);
        var disp = _s.Catalog!.CreatureDisplay(1879);
        s2.Notes.Add($"the vendor's look: display 1879 -> {disp?.Model}");
        // S3: Check client and server, then Make the game match the server
        var issues = ServerConsistency.Check(ed, "spells", new[] { SpellDraught });
        foreach (var i in issues) s3.Validation.Add("check spells (before): " + i.Text);
        var miss = issues.FirstOrDefault(i => i.Id == SpellDraught && i.Kind == ConsistencyKind.ServerOnly && i.CanMatchClient);
        if (miss == null) throw new InvalidOperationException($"Check client and server did not report spell {SpellDraught} as missing in the game");
        if (Ws.IsModified(@"DBFilesClient\Spell.dbc")) throw new InvalidOperationException("Spell.dbc already edited in the workspace");
        var done = ServerConsistency.MatchClient(ed, miss);
        s3.Validation.Add("Make the game match the server: " + done);
        var after = ServerConsistency.Check(ed, "spells", new[] { SpellDraught });
        s3.Validation.Add($"check spells (after): {after.Count} difference(s)" + string.Concat(after.Select(i => "; " + i.Text)));
        var items = ServerConsistency.Check(ed, "items", new[] { ItemBlade, ItemDraught });
        s1.Validation.Add($"check items {ItemBlade},{ItemDraught}: {items.Count} difference(s)" + string.Concat(items.Select(i => "; " + i.Text)));
        var crs = ServerConsistency.Check(ed, "creatures", new[] { Vendor });
        s2.Validation.Add($"check creatures {Vendor}: {crs.Count} difference(s)" + string.Concat(crs.Select(i => "; " + i.Text)));
        var spellRec = Ws.List().FirstOrDefault(v => v.Record.Path.EndsWith("Spell.dbc", StringComparison.OrdinalIgnoreCase));
        s3.ChangeSets.Add($"Spell.dbc in the workspace: {spellRec?.Record.Summary} ({spellRec?.StatusText})");
        // the Spell.dbc row the game will get (the tooltip reads Description with $s1 = base points + 1 .. base points + die sides)
        var dbc = _s.Dbc!.Lookup("Spell")!;
        var row = dbc.Find(SpellDraught);
        if (row != null) s3.Compare.Add($"workspace Spell.dbc row {SpellDraught}: Name '{dbc.Text(row, dbc.Layout.Columns.First(c => c.Name == "Name"), 0)}', Description '{dbc.Text(row, dbc.Layout.Columns.First(c => c.Name == "Description"), 0)}', " +
                                        $"EffectBasePoints1 {dbc.Format(row, dbc.Layout.Columns.First(c => c.Name == "EffectBasePoints1"), 0)}, EffectDieSides1 {dbc.Format(row, dbc.Layout.Columns.First(c => c.Name == "EffectDieSides1"), 0)} -> the game's tooltip: 'Use: Restores 900 to 1000 health.'");
        // deliverables: the SQL patch and its revert script, the apply plan (NOT applied)
        var sdir = Path.Combine(_run, "server");
        Directory.CreateDirectory(sdir);
        var saved = svc.SavedChanges();
        var script = ServerSql.Build(svc, saved);
        File.WriteAllText(Path.Combine(sdir, "v010_server.sql"), script.Patch, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sdir, "v010_server.revert.sql"), script.Revert, new UTF8Encoding(false));
        var plan = new ServerApplier(svc).Plan();
        var safety = Safety(svc, plan);
        File.WriteAllText(Path.Combine(sdir, "apply_plan.txt"), PlanText(plan, safety), new UTF8Encoding(false));
        foreach (var r in new[] { s1, s2, s3 })
        {
            r.Compare.Add($"SQL patch: {script.Summary} ({script.Rows} rows, tables {string.Join(", ", script.Tables)}) -> server\\v010_server.sql (+ .revert.sql)");
            r.Compare.Add($"apply plan: {plan.Changes.Sum(c => c.Rows.Count)} rows, conflicts {plan.Conflicts.Count}, already applied {plan.AlreadyApplied.Count}, non-transactional {string.Join(", ", plan.NonTransactional)}; notes: {string.Join(" | ", plan.Notes)}");
            r.Compare.Add("safety: " + (safety.Count == 0 ? "every row is a NEW row with a custom ID; no existing row is changed or deleted" : string.Join("; ", safety)));
        }
        bool ok = safety.Count == 0 && plan.Conflicts.Count == 0;
        s1.Ok = ok && items.Count == 0;
        s1.Verdict = s1.Ok ? "app steps passed (saved in the workspace, SQL patch exported, NOT applied)" : "app-level problem: see validation";
        s2.Ok = ok && crs.Count == 0;
        s2.Verdict = s2.Ok ? "app steps passed (template, vendor list, spawn saved in the workspace, NOT applied)" : "app-level problem: see validation";
        s3.Ok = ok && after.Count == 0 && spellRec != null;
        s3.Verdict = s3.Ok ? "app steps passed: the check found the game lacks the spell, Make the game match the server added the Spell.dbc row; check clean after" : "app-level problem: see validation";
        return new() { s1, s2, s3 };
    }

    /// <summary>The run's rule for the server: only NEW rows with custom IDs (900000+), never a change or delete of an existing row.</summary>
    static List<string> Safety(ServerDataService svc, ServerApplyPlan plan)
    {
        var bad = new List<string>();
        foreach (var ch in plan.Changes)
            foreach (var r in ch.Rows.Values)
            {
                if (!r.IsAdded) { bad.Add($"{ch.Table.Name} {r.Key}: {r.KindText} (only new rows are allowed)"); continue; }
                var ids = ch.Table.Key.Where(k => !string.Equals(k, "patch", StringComparison.OrdinalIgnoreCase) && !string.Equals(k, "build", StringComparison.OrdinalIgnoreCase) && !string.Equals(k, "slot", StringComparison.OrdinalIgnoreCase))
                    .Select(k => WorldValue.Long(r.After![ch.Table.IndexOf(k)])).ToList();
                if (ids.Count == 0 || ids.Any(i => i < 900000 || i > 999999)) bad.Add($"{ch.Table.Name} {r.Key}: key outside the custom ID range 900000-999999");
            }
        foreach (var c in plan.Conflicts) bad.Add("conflict: " + c);
        foreach (var x in plan.AlreadyApplied) bad.Add("already on the server: " + x);
        if (plan.Changes.Any(c => c.Table.Name is "characters" or "account")) bad.Add("a non-world table");
        return bad;
    }

    internal static string PlanText(ServerApplyPlan plan, List<string> safety)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Apply to server plan (target {plan.Target}), made {DateTime.Now:s}.");
        sb.AppendLine("Safety: " + (safety.Count == 0 ? "OK (new rows with custom IDs only)" : string.Join("; ", safety)));
        foreach (var c in plan.Changes) sb.AppendLine($"  {c.Summary}" + string.Concat(c.Rows.Values.Select(r => $"\n    {r.KindText} {r.Key}")));
        sb.AppendLine("World server running: " + (plan.Running.Count > 0 ? string.Join(", ", plan.Running) : "no"));
        sb.AppendLine("Conflicts: " + plan.Conflicts.Count + "; already applied: " + plan.AlreadyApplied.Count + "; non-transactional: " + string.Join(", ", plan.NonTransactional));
        sb.AppendLine("What the server needs afterwards:");
        foreach (var n in plan.Notes) sb.AppendLine("  " + n);
        sb.AppendLine("The statements:");
        foreach (var s in plan.Script.Statements) sb.AppendLine("  " + s);
        return sb.ToString();
    }

    static float GroundZ(float wx, float wy)
    {
        int tx = (int)MathF.Floor(32 - wy / TerrainMath.TileSize), ty = (int)MathF.Floor(32 - wx / TerrainMath.TileSize);
        var data = _s.Data!.Read($@"World\Maps\Kalimdor\Kalimdor_{tx}_{ty}.adt") ?? throw new InvalidOperationException($"no tile {tx}_{ty}");
        return TerrainTile.Load(data, "Kalimdor", tx, ty).HeightAt(wx, wy) ?? throw new InvalidOperationException($"no ground at {wx},{wy}");
    }

    /// <summary>sessions_v010.json: every viewpoint of the run (position on the ground from the game's map, facing, camera, scenario).</summary>
    static void WriteViewpoints(string run)
    {
        var vps = new List<object>();
        void Add(string key, string lua, float x, float y, float facingX, float facingY, string camera, string why)
        {
            float z = GroundZ(x, y) + 0.5f;
            double o = Math.Atan2(facingY - y, facingX - x);
            if (o < 0) o += 2 * Math.PI;
            vps.Add(new { key, lua, pos = string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2:0.0},{3:0.000}", x, y, z, o), camera, why });
        }
        // A1-A3: the Mottled Boars of the Valley of Trials (run 20261010's S2 viewpoint: 16 yd west of boar spawn 4691, facing east)
        Add("b1", "v1_boars.lua", -682.7f, -4271f, -682.7f, -4287f, "7,12", "Mottled Boars (Boar.m2): walk cycle, idle variations, the head spike");
        // A4: Razormane Quilboars north-west of Razor Hill (spawn 12318 at 110.6,-4572.9), 8 yd south of it
        Add("q1", "v2_quilboar.lua", 102.6f, -4572.9f, 110.6f, -4572.9f, "7,10", "Razormane Quilboar (QuillBoar.m2): the weapon at the moved HandRight point, idle and attacking");
        // A5: the Scorpid Worker of run 20261010's S3 (spawn 8552) and a Clattering Scorpid south of Razor Hill (spawn 12988 at 208.7,-4882.4)
        Add("c1", "v3_scorpid.lua", -688f, -4151f, -680.6f, -4145.6f, "7,15", "Scorpid Worker (Scorpion.m2): attack animations, stinger swelling");
        Add("c2", "v3_scorpid_rh.lua", 202.3f, -4876.0f, 208.7f, -4882.4f, "7,15", "Clattering Scorpid near Razor Hill (Scorpion.m2): attack animations");
        // S1-S3: the start point, facing the new vendor 3.5 yd north
        Add("v1", "v4_vendor.lua", StartX, StartY, VendorX, VendorY, "6,10", "the new vendor: model, name, vendor window, item tooltips and icons");
        File.WriteAllText(Path.Combine(run, "sessions_v010.json"), JsonSerializer.Serialize(vps, J));
        Console.WriteLine($"viewpoints: {Path.Combine(run, "sessions_v010.json")}");
    }

    // ================================================================== server apply / undo (the run script's steps)

    static ServerDataService ServiceFor(AppSession s, string[] opts, out string target)
    {
        string? Opt(string n) { int i = Array.IndexOf(opts, n); return i >= 0 && i + 1 < opts.Length ? opts[i + 1] : null; }
        if (Opt("--port") is { } port)
        {
            // the dry run: a throwaway server, its own state folder (never the live database or the run's log)
            s.Settings.Server = new ServerDbSettings { Enabled = true, Host = "127.0.0.1", Port = int.Parse(port, CultureInfo.InvariantCulture), User = Opt("--user") ?? "root", Password = opts.Contains("--no-password") ? "" : s.Settings.Server.Password, Database = "mangos" };
            var state = Opt("--state") ?? throw new ArgumentException("--port needs --state DIR");
            Directory.CreateDirectory(state);
            target = $"throwaway 127.0.0.1:{port}";
            return new ServerDataService(s.Settings.Server, s.Workspace, s.Dbc, s.Settings, s.ServerFolder, state);
        }
        target = $"live {s.Settings.Server.Host}:{s.Settings.Server.Port}/{s.Settings.Server.Database}";
        return s.ServerData!;
    }

    /// <summary>plan|apply|undo|status. <paramref name="safety"/>: the run's rule for the plan (default: the 0.9.0 test's, <see cref="Safety"/>);
    /// the designers test (Designers.cs) passes its own.</summary>
    internal static int Server(string client, string home, string run, string[] rest, Func<ServerDataService, ServerApplyPlan, List<string>>? safety = null)
    {
        safety ??= Safety;
        var verb = rest.Length > 0 ? rest[0] : "status";
        using var s = Open(client, home);
        var svc = ServiceFor(s, rest, out var target);
        var ap = new ServerApplier(svc);
        var log = new List<string> { $"{DateTime.Now:s} server {verb} ({target})" };
        Directory.CreateDirectory(Path.Combine(run, "server"));
        int code = 0;
        switch (verb)
        {
            case "plan":
            case "apply":
            {
                var plan = ap.Plan();
                var bad = safety(svc, plan);
                log.Add(PlanText(plan, bad));
                if (bad.Count > 0) { log.Add("REFUSED by the run's safety rule."); code = 4; break; }
                if (verb == "plan") break;
                var (rec, done) = ap.Apply(plan, overwriteConflicts: false);
                log.AddRange(done);
                log.Add($"backup folder: {rec.Folder}");
                File.WriteAllText(Path.Combine(run, "server", $"apply_result_{DateTime.Now:HHmmss}.json"), JsonSerializer.Serialize(rec, J));
                break;
            }
            case "undo":
            {
                var last = ap.Last();
                if (last == null) { log.Add("nothing to undo (no apply in the log that was not undone)"); code = 6; break; }
                var conflicts = ap.UndoConflicts(last);
                if (conflicts.Count > 0) { log.Add("REFUSED: rows changed since the apply: " + string.Join("; ", conflicts)); code = 4; break; }
                log.AddRange(ap.Undo(last, overwrite: false));
                break;
            }
            case "status":
                foreach (var h in ap.History()) log.Add($"{h.Time:s} {h.Rows} row(s) {h.Summary} undone {h.Undone?.ToString("s") ?? "no"} backup {h.Folder}");
                foreach (var c in svc.SavedChanges()) log.Add("workspace: " + c.Summary);
                break;
            default: throw new ArgumentException(verb);
        }
        foreach (var l in log) Console.WriteLine(l);
        Directory.CreateDirectory(Path.Combine(run, "server"));
        File.AppendAllLines(Path.Combine(run, "server", "server_log.txt"), log);
        return code;
    }

    internal static int Wdb(string client, string home, string run)
    {
        var cp = new AppPaths(home).ForClient(client);
        var (backup, done) = WdbCache.Clear(client, cp.BackupDir);
        var log = new List<string> { $"{DateTime.Now:s} clear WDB of {client}" };
        log.AddRange(done);
        foreach (var l in log) Console.WriteLine(l);
        File.AppendAllLines(Path.Combine(run, "delivery_log.txt"), log);
        return 0;
    }

    // ================================================================== dry run against a throwaway MySQL

    /// <summary><paramref name="present"/>: the rows that must be on the throwaway server after the apply (default: the 0.9.0 test's);
    /// <paramref name="safety"/>: the plan's rule (see <see cref="Server"/>).</summary>
    internal static int DryRun(string client, string home, string run, string dump, IReadOnlyList<(string Table, long Id)>? present = null, Func<ServerDataService, ServerApplyPlan, List<string>>? safety = null)
    {
        present ??= new[] { ("item_template", ItemBlade), ("item_template", ItemDraught), ("spell_template", SpellDraught), ("creature_template", Vendor) };
        var dr = Path.Combine(run, "dryrun");
        Directory.CreateDirectory(dr);
        var bin = Path.Combine(ServerDir, "mysql5", "bin");
        var mysqld = ThrowawayMySql.FindMysqld(ServerDir) ?? throw new InvalidOperationException("no mysqld.exe");
        var log = new List<string>();
        void L(string s) { Console.WriteLine(s); log.Add(s); }
        // a copy of the prepared home (settings, index, workspace, change list): the apply marks workspace files "applied", the
        // undo unmarks them; the prepared home itself is not touched by the dry run
        var copy = Path.Combine(dr, "home_copy");
        if (Directory.Exists(copy)) Directory.Delete(copy, true);
        foreach (var f in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(home, f);
            if (rel.Split(Path.DirectorySeparatorChar).Contains("backups")) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, rel))!);
            File.Copy(f, Path.Combine(copy, rel));
        }
        home = copy;
        if (Directory.Exists(Path.Combine(dr, "state"))) Directory.Delete(Path.Combine(dr, "state"), true);
        if (Directory.Exists(Path.Combine(dr, "server"))) Directory.Delete(Path.Combine(dr, "server"), true);
        using var tm = ThrowawayMySql.Start(mysqld);
        L($"throwaway MySQL on 127.0.0.1:{tm.Port} ({tm.Root})");
        Exec(Path.Combine(bin, "mysql.exe"), $"--host=127.0.0.1 --port={tm.Port} -uroot -e \"CREATE DATABASE mangos DEFAULT CHARACTER SET utf8\"", null);
        var sw = Stopwatch.StartNew();
        Exec(Path.Combine(bin, "mysql.exe"), $"--host=127.0.0.1 --port={tm.Port} -uroot --max_allowed_packet=128M mangos", dump);
        L($"loaded {dump} ({new FileInfo(dump).Length / 1048576.0:0} MB) in {sw.Elapsed.TotalSeconds:0} s");
        string Dump(string name)
        {
            var f = Path.Combine(dr, name);
            Exec(Path.Combine(bin, "mysqldump.exe"), $"--host=127.0.0.1 --port={tm.Port} -uroot --skip-lock-tables --single-transaction --skip-dump-date --skip-comments --order-by-primary --skip-extended-insert --hex-blob --result-file=\"{f}\" mangos", null);
            return f;
        }
        var d0 = Dump("dump0_loaded.sql");
        var opts = new[] { "--port", tm.Port.ToString(CultureInfo.InvariantCulture), "--no-password", "--state", Path.Combine(dr, "state") };
        int c1 = Server(client, home, dr, new[] { "apply" }.Concat(opts).ToArray(), safety);
        L($"apply on the throwaway: exit {c1}");
        var d1 = Dump("dump1_applied.sql");
        // the applied rows are there (read back through the same app code)
        using (var s = Open(client, home))
        {
            var svc = ServiceFor(s, opts, out _);
            var ed = svc.NewEditor();
            foreach (var (t, id) in present)
                L($"  {t} {id} on the throwaway server: {(svc.Db.Row(ed.Table(t), ed.ResolveKey(ed.Table(t), id.ToString(CultureInfo.InvariantCulture))) != null ? "present" : "MISSING")}");
        }
        int c2 = Server(client, home, dr, new[] { "undo" }.Concat(opts).ToArray(), safety);
        L($"undo on the throwaway: exit {c2}");
        var d2 = Dump("dump2_undone.sql");
        string H(string f) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        L($"dump0 (loaded)  {H(d0)}");
        L($"dump1 (applied) {H(d1)}  {DiffLines(d0, d1)}");
        L($"dump2 (undone)  {H(d2)}  {DiffLines(d0, d2)}");
        bool same = H(d0) == H(d2);
        L(same ? "RESULT: after Undo last apply the database dump is byte-identical to the one before the apply" : "RESULT: the dump after the undo DIFFERS from the one before the apply");
        File.WriteAllLines(Path.Combine(dr, "dryrun_report.txt"), log);
        // the workspace copy of the dry run's state stays in RUNDIR\dryrun\state; the live run uses the home's own state folder
        File.Delete(d1);
        if (same) File.Delete(d2);
        return c1 == 0 && c2 == 0 && same ? 0 : 1;
    }

    internal static string DiffLines(string a, string b)
    {
        var x = new HashSet<string>(File.ReadLines(a));
        var y = new HashSet<string>(File.ReadLines(b));
        var add = y.Where(l => !x.Contains(l)).ToList();
        var del = x.Where(l => !y.Contains(l)).ToList();
        return $"lines only after: {add.Count}{(add.Count > 0 ? " (" + string.Join(" | ", add.Take(8).Select(l => l.Length > 90 ? l[..90] + "..." : l)) + ")" : "")}; lines only before: {del.Count}{(del.Count > 0 ? " (" + string.Join(" | ", del.Take(4).Select(l => l.Length > 90 ? l[..90] + "..." : l)) + ")" : "")}";
    }

    static void Exec(string exe, string args, string? stdinFile)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = stdinFile != null, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        if (stdinFile != null)
        {
            using (var fs = File.OpenRead(stdinFile)) fs.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        }
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err}");
    }
}
