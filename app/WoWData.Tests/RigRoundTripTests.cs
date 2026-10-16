using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using Xunit.Abstractions;

namespace WoWData.Tests;

/// <summary>
/// 0.10.0, the skeleton and animation round trip through export packages (into the workspace of a FAKE client folder; the
/// real Client\Data is checked unchanged):
///  * export with the skeleton -> import with no edit: nothing changes, over a broad sample of creatures, characters,
///    items, world models and spell effects; the import's plan writes every one of them back byte for byte;
///  * scripted edits of the rig (a new bone weighted to vertices, a new animation, a retimed walk, a moved attachment
///    point, a new event) through the import analysis, written, validated and read back;
///  * the limits and plain-language checks; the particles / ribbons JSON; "Used in game" for animations;
///  * the same through Blender 4.5 headless (skipped when Blender is not installed).
/// </summary>
[Collection(IndexCollection.Name)]
public class RigRoundTripTests : IDisposable
{
    public const string Boar = ModelRoundTripTests.Boar;
    private readonly ITestOutputHelper _out;
    private readonly string _root = TestEnv.TempDir("rig");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();
    private readonly Workspace _ws;
    private readonly ModelPackageService _svc;
    private readonly ModelResolver _res;

    public RigRoundTripTests(IndexCollection.Handle h, ITestOutputHelper output)
    {
        _out = output;
        var fakeClient = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fakeClient, "Data"));
        _ws = new Workspace(new AppPaths(Path.Combine(_root, "home")).ForClient(fakeClient));
        var client = TestEnv.Client;
        var catalog = new Catalog(client);
        var ws = _ws;
        _res = new ModelResolver(catalog, p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p));
        _svc = new ModelPackageService(client, catalog, h.Fixture.Index, _ws, _res, new TextureService(client, _ws));
    }

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Log(ModelImportAnalysis a)
    {
        foreach (var r in a.General) _out.WriteLine($"  [{r.Level}] {r.Text}");
        foreach (var c in a.Changes.Where(c => !c.Unchanged || c.HasBlocker))
        {
            _out.WriteLine($"  {(c.Include ? "INCLUDE" : "skip")}: {c.Title}: {c.Detail}");
            foreach (var l in c.Lines) _out.WriteLine($"      - {l}");
            foreach (var r in c.Rules) _out.WriteLine($"      [{r.Level}] {r.Text}");
        }
    }

    /// <summary>A broad sample: creatures, characters, items, world doodads, spell effects (with particles).</summary>
    public static readonly string[] Sample =
    {
        Boar, @"Creature\Wolf\Wolf.m2", @"Creature\Dragon\Dragon.m2", @"Creature\Murloc\Murloc.m2", @"Creature\Kobold\Kobold.m2", @"Creature\Ragnaros\Ragnaros.m2",
        @"Creature\Horse\Horse.m2", @"Creature\Felbat\Felbat.m2", @"Creature\Wisp\Wisp.m2", @"Creature\Gryphon\Gryphon.m2",
        @"Character\Orc\Male\OrcMale.m2", @"Character\Human\Female\HumanFemale.m2", @"Character\Tauren\Male\TaurenMale.m2", @"Character\Gnome\Female\GnomeFemale.m2",
        ModelRoundTripTests.Axe, @"Item\ObjectComponents\Weapon\Sword_2H_Claymore_A_01.m2", @"Item\ObjectComponents\Head\Helm_Plate_RaidWarrior_A_01_OrM.m2",
        @"Item\ObjectComponents\Shield\Shield_Crest_A_01.m2",
        ModelRoundTripTests.Tree, @"World\Generic\Human\Passive Doodads\Bannerwall\BannerWallAlliance01.m2", @"World\Generic\PVP\CTFFlags\HordeCTFFlag.m2",
        @"World\Kalimdor\Silithus\PassiveDoodads\DustDevil\AhnQirajDustDevil.m2",
        @"Spells\Fireball_Missile_High.m2", @"Spells\Holy_Precast_Uber_Hand.m2", @"Creature\Spells\ManaTotem.m2",
    };

    [Fact]
    public void ExportWithSkeletonReadsBackUnchangedAndWritesBackByteForByte()
    {
        int ok = 0;
        var rows = new List<string>();
        foreach (var model in Sample)
        {
            if (!TestEnv.Client.Exists(model)) { _out.WriteLine($"{model}: not in this client, skipped"); continue; }
            var res = _svc.Export(model, _svc.SubjectOf(model), ModelExportMode.CurrentLook, Path.Combine(_root, "same"), includeSkeleton: true);
            Assert.NotNull(res.Manifest.Rig);
            var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
            if (!a.NothingChanged) Log(a);
            Assert.True(a.NothingChanged, $"{model}: a change was found in an unchanged package");
            Assert.False(a.AnythingToImport);
            // the plan the import made writes the model back byte for byte
            var orig = TestEnv.Client.Read(model)!;
            var f = M2File.Parse(orig);
            Assert.NotNull(a.Rig);
            var w = RigWriter.Apply(f, a.Rig!.Plan, _res.Animations);
            Assert.False(w.Changed, model);
            Assert.Equal(orig, f.Serialize());
            var m = M2Model.Load(orig);
            rows.Add($"{model}: {m.Bones.Length} bones, {m.Sequences.Length} animations, {m.GlobalSequences.Length} global sequences, {m.Attachments.Length} attachments, {m.EventCount} events, {m.ParticleCount + m.RibbonCount} particles/ribbons: unchanged, byte-identical");
            ok++;
        }
        foreach (var r in rows) _out.WriteLine(r);
        Assert.True(ok >= 20, $"{ok} models");
    }

    [Fact]
    public void ManifestDescribesTheRigAndTheGltfHasItsParts()
    {
        var res = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.CurrentLook, Path.Combine(_root, "desc"), includeSkeleton: true);
        var rig = res.Manifest.Rig!;
        Assert.Equal(57, rig.Bones.Count);
        Assert.Equal(13, rig.Sequences.Count);
        Assert.Contains(rig.Sequences, s => s.Action == "Stand [3]" && s.AnimId == 0 && s.Variation == 2);
        Assert.Contains(rig.Sequences, s => s.Action == "Walk" && s.Loop && s.MoveSpeed > 0 && s.UsedFor.Any(u => u.StartsWith("moving", StringComparison.Ordinal)));
        Assert.Contains(rig.Sequences, s => s.Action == "Death" && !s.Loop);
        Assert.Equal(100.0, rig.Sequences.Where(s => s.AnimId == 0).Sum(s => s.Chance), 1);
        Assert.Equal(9, rig.Attachments.Count);
        // the boar's footstep sound: 8 keys, 7 of them inside an animation's range (the client's half-open ranges)
        Assert.Contains(rig.Events, e => e.Code == "$FSD" && e.Triggers.Count == 7);
        Assert.True(rig.AnimationNames.Count > 200);
        var g = JsonNode.Parse(File.ReadAllText(res.GltfFiles[0]))!;
        var nodes = g["nodes"]!.AsArray().Select(n => (string?)n!["name"]).ToList();
        Assert.Equal(18, nodes.Count(n => n != null && n.StartsWith("EVT_", StringComparison.Ordinal)));
        Assert.Equal(9, nodes.Count(n => n != null && n.StartsWith("ATT_", StringComparison.Ordinal)));
        var anims = g["animations"]!.AsArray().Select(x => (string?)x!["name"]).ToList();
        Assert.Contains("Stand [2]", anims);
        // global sequences as their own animations (a model that has them)
        var fx = _svc.Export(@"Spells\Fireball_Missile_High.m2", ModelSubject.Plain, ModelExportMode.CurrentLook, Path.Combine(_root, "fx"), includeSkeleton: true);
        Assert.NotNull(fx.Manifest.Rig!.EffectsFile);
        Assert.True(File.Exists(Path.Combine(fx.Folder, EffectsJson.FileName)));
    }

    /// <summary>The scripted rig edit of the boar, made on the rig read back from the package (what Blender would send).</summary>
    private static void EditRig(EditedModel em)
    {
        var rig = em.Rig!;
        var head = rig.Bones.First(b => b.Name.EndsWith("_Head", StringComparison.Ordinal));
        // a new bone under the head, and a new animation in which it turns
        rig.Bones.Add(new EditedBone { Name = "bone_tusk_new", Parent = head.Name, Head = head.Head + new Vector3(0.3f, 0, 0) });
        var stand = rig.Actions.First(x => x.Name == "Stand");
        var dance = new EditedAction { Name = "Stand [4]", WowSequence = stand.WowSequence, DurationMs = 1600 };
        foreach (var kv in stand.Props) dance.Props[kv.Key] = kv.Value?.DeepClone();
        var p = rig.Bones.Last().Head;
        var bs = new BoneSamples();
        foreach (var (t, ang) in new[] { (0.0, 0f), (800.0, 0.7f), (1600.0, 0f) })
        {
            bs.TimesMs.Add(t);
            bs.Local.Add(Matrix4x4.CreateTranslation(-p) * Matrix4x4.CreateRotationZ(ang) * Matrix4x4.CreateTranslation(p));
        }
        dance.Bones["bone_tusk_new"] = bs;
        dance.Markers.Add(("$CSD", 400));
        rig.Actions.Add(dance);
        // the walk retimed: 1.25 x as long
        var walk = rig.Actions.First(x => x.Name == "Walk");
        walk.DurationMs *= 1.25;
        foreach (var b in walk.Bones.Values)
            for (int i = 0; i < b.TimesMs.Count; i++) b.TimesMs[i] *= 1.25;
        for (int i = 0; i < walk.Markers.Count; i++) walk.Markers[i] = (walk.Markers[i].Marker, walk.Markers[i].TimeMs * 1.25);
        // the name-plate attachment point moved up
        var att = rig.Points.First(x => x.Kind == "attachment" && x.Id == 18);
        att.Position += new Vector3(0, 0, 0.15f);
    }

    [Fact]
    public void ScriptedRigEditIsPlannedWrittenAndReadBack()
    {
        var res = _svc.Export(Boar, ModelSubject.Creature, ModelExportMode.CurrentLook, Path.Combine(_root, "edit"), includeSkeleton: true);
        var em = EditedModelReader.Read(res.Folder);
        EditRig(em);
        var a = ModelImportAnalyzer.Analyze(em, _svc.ImportContext());
        Log(a);
        var row = a.Changes.Single(c => c.Kind == ModelChangeKind.Skeleton);
        Assert.False(row.Unchanged);
        Assert.True(row.CanInclude && row.Include, string.Join("; ", row.Rules.Where(r => r.Level == RuleLevel.Blocker)));
        Assert.Contains(row.Lines, l => l.Contains("new bone", StringComparison.Ordinal));
        Assert.Contains(row.Lines, l => l.Contains("Walk retimed", StringComparison.Ordinal));
        Assert.Contains(row.Lines, l => l.Contains("attachment point PlayerName moved", StringComparison.Ordinal));
        Assert.Contains(row.Rules, r => r.Text.Contains("move speed was changed from 2.5 to 2", StringComparison.Ordinal));
        Assert.Contains(row.Rules, r => r.Text.Contains("scaled to 100 %", StringComparison.Ordinal));
        Assert.True(a.Changes.Single(c => c.Kind == ModelChangeKind.Mesh).Unchanged);
        var cs = ModelImportCommitter.Commit(a, _ws, null);
        Assert.Single(cs.Files);
        var orig = M2Model.Load(TestEnv.Client.Read(Boar)!);
        var nm = M2Model.Load(_ws.ReadEdited(Boar)!);
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(nm)));
        var info = RigInfo.Read(nm, _res.Animations);
        Assert.Equal(58, nm.Bones.Length);
        Assert.True(info.Bones[57].Animated);
        Assert.Equal(14, nm.Sequences.Length);
        var walk = info.Sequences.Single(s => s.Name == "Walk");
        Assert.Equal(1250u, walk.Duration);
        Assert.Equal(2f, walk.MoveSpeed, 3);
        var st4 = info.Sequences.Single(s => s.Name == "Stand [4]");
        Assert.Equal(1600u, st4.Duration);
        Assert.InRange(info.Sequences.Where(s => s.AnimId == 0).Sum(s => (int)s.Frequency), 32763, 32767);
        Assert.Contains(info.Events, e => e.Code == "$CSD" && e.Triggers.Contains((st4.Index, 400u)));
        Assert.Equal(orig.Attachments[3].Position + new Vector3(0, 0, 0.15f), nm.Attachments[3].Position);
        // the unchanged animations kept their place and every key
        for (int s = 0; s < orig.Sequences.Length; s++)
        {
            if (orig.Sequences[s].AnimId == 4) continue;
            Assert.Equal(orig.Sequences[s].Start, nm.Sequences[s].Start);
            for (int b = 0; b < orig.Bones.Length; b++)
                foreach (var ch in new[] { "translation", "rotation", "scale" })
                {
                    var ro = RawTrack.Read(orig.File["bones"].Track(b, ch));
                    var rn = RawTrack.Read(nm.File["bones"].Track(b, ch));
                    var o = orig.Sequences[s];
                    Assert.True(ro.Window(s, o.Start, o.End).SameAs(rn.Window(s, o.Start, o.End)), $"bone {b} {ch} sequence {s}");
                }
        }
        // the new bone turns in the new animation and holds still elsewhere
        var mats = new Matrix4x4[nm.Bones.Length];
        M2Anim.BoneMatrices(nm, new M2Anim.Time(st4.Index, st4.Start, 800, 800), mats);
        Assert.False(mats[57].IsIdentity);
        // the 3D viewer builds it, and reverting restores the game's model
        var asset = WoWDataStudio.Rendering.ModelAsset.Build(Boar, _ws.ReadEdited(Boar)!, _ => null);
        Assert.Equal(4, asset.Views.Length);
        _ws.RevertChangeSet(cs.Id);
        Assert.False(_ws.IsModified(Boar));
    }

    private ModelImportAnalysis AnalyzeEdited(string tag, Action<EditedModel> edit, string model = Boar, ModelSubject subject = ModelSubject.Creature)
    {
        var res = _svc.Export(model, subject, ModelExportMode.CurrentLook, Path.Combine(_root, tag), includeSkeleton: true);
        var em = EditedModelReader.Read(res.Folder);
        edit(em);
        var a = ModelImportAnalyzer.Analyze(em, _svc.ImportContext());
        Log(a);
        return a;
    }

    private static ModelChange RigRow(ModelImportAnalysis a) => a.Changes.Single(c => c.Kind == ModelChangeKind.Skeleton);

    [Fact]
    public void LimitsAreCheckedInPlainWords()
    {
        // a key bone removed: the game finds the head by its key bone
        var a1 = AnalyzeEdited("nohead", em => em.Rig!.Bones.RemoveAll(b => b.Name.EndsWith("_Head", StringComparison.Ordinal)));
        Assert.Contains(RigRow(a1).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("key bone Head (6)", StringComparison.Ordinal));
        // Stand removed: every model needs it
        var a2 = AnalyzeEdited("nostand", em => em.Rig!.Actions.RemoveAll(x => RigImportAnalyzer.BaseName(x.Name) == "Stand"));
        Assert.Contains(RigRow(a2).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("Stand (animation 0)", StringComparison.Ordinal));
        // an action that is not a game animation
        var a3 = AnalyzeEdited("badname", em =>
        {
            var w = em.Rig!.Actions.First(x => x.Name == "Walk");
            em.Rig.Actions.Add(new EditedAction { Name = "MySpecialMove", DurationMs = 1000, Bones = { } });
        });
        Assert.Contains(RigRow(a3).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("not named after a game animation", StringComparison.Ordinal));
        // a key bone id the client does not know
        var a4 = AnalyzeEdited("badkey", em => em.Rig!.Bones[3].KeyBoneId = 40);
        Assert.Contains(RigRow(a4).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("key bone 40 does not exist", StringComparison.Ordinal));
        // a character model: removing an animation the game plays is a blocker, and the character warning is shown
        var a5 = AnalyzeEdited("orcwalk", em => em.Rig!.Actions.RemoveAll(x => x.Name == "Walk"), ModelRoundTripTests.Orc, ModelSubject.Character);
        Assert.Contains(RigRow(a5).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("removed from a character model", StringComparison.Ordinal));
        // removing an attachment point of a character model
        var a6 = AnalyzeEdited("orcatt", em => em.Rig!.Points.RemoveAll(p => p.Kind == "attachment" && p.Id == 1), ModelRoundTripTests.Orc, ModelSubject.Character);
        Assert.Contains(RigRow(a6).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("HandRight", StringComparison.Ordinal));
        // a bone loop is impossible from Blender but checked; a vertex on bone 300 is refused by the writer
        var m = M2Model.Load(TestEnv.Client.Read(Boar)!);
        var plan = RigPlan.Identity(m);
        for (int i = 0; i < 260; i++) plan.Bones.Insert(0, new PlanBone { Name = $"extra_{i}" });
        foreach (var b in plan.Bones.Skip(260)) if (b.Parent >= 0) b.Parent += 260;
        Assert.Throws<RigEditException>(() => RigWriter.Apply(M2File.Parse(TestEnv.Client.Read(Boar)!), plan, _res.Animations));
    }

    [Fact]
    public void ParticlesAndRibbonsJsonIsCheckedAndOnlyEditedRecordsAreWritten()
    {
        const string fxModel = @"Spells\Fireball_Missile_High.m2";
        var res = _svc.Export(fxModel, ModelSubject.Plain, ModelExportMode.CurrentLook, Path.Combine(_root, "fxjson"), includeSkeleton: true);
        var file = Path.Combine(res.Folder, EffectsJson.FileName);
        var j = JsonNode.Parse(File.ReadAllText(file))!;
        var p0 = j["particles"]![0]!;
        var gravity = p0["tracks"]!["gravity"]!;
        var keys0 = (gravity["sequences"]?[0]?["keys"] ?? gravity["keys"])!.AsArray();
        foreach (var k in keys0) k![1] = (double)k[1]! + 3.5;
        p0["fields"]!["texture_rows"] = 1;
        File.WriteAllText(file, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Log(a);
        var row = a.Changes.Single(c => c.Kind == ModelChangeKind.Effects);
        Assert.True(row.CanInclude && row.Include, string.Join("; ", row.Rules));
        Assert.Contains("gravity", row.Detail, StringComparison.Ordinal);
        var cs = ModelImportCommitter.Commit(a, _ws, null);
        var o = M2Model.Load(TestEnv.Client.Read(fxModel)!);
        var n = M2Model.Load(_ws.ReadEdited(fxModel)!);
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(n)));
        var go = RawTrack.Read(o.File["particles"].Track(0, "gravity"));
        var gn = RawTrack.Read(n.File["particles"].Track(0, "gravity"));
        var so = o.Sequences[0];
        Assert.Equal(TrackValues.Components("f4", go.Evaluate(0, so.Start)!)[0] + 3.5, TrackValues.Components("f4", gn.Evaluate(0, n.Sequences[0].Start)!)[0], 3);
        // the other emitters and every other part kept their bytes
        var co = ModelExporter.PartChecksums(o.File);
        var cn = ModelExporter.PartChecksums(n.File);
        foreach (var k in new[] { "vertices", "views", "skeleton_and_animations", "sequences", "materials", "collision" }) Assert.Equal(co[k], cn[k]);
        _ws.RevertChangeSet(cs.Id);
        // invalid values are refused in plain words
        p0["fields"]!["texture"] = 999;
        File.WriteAllText(file, j.ToJsonString());
        var bad = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Assert.Contains(bad.Changes.Single(c => c.Kind == ModelChangeKind.Effects).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("texture 999 does not exist", StringComparison.Ordinal));
        File.WriteAllText(file, "{ not json");
        var bad2 = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Assert.Contains(bad2.Changes.Single(c => c.Kind == ModelChangeKind.Effects).Rules, r => r.Level == RuleLevel.Blocker && r.Text.Contains("could not be read as JSON", StringComparison.Ordinal));
        var j2 = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "fxjson", Path.GetFileName(res.Folder), "wowdata_manifest.json")))!;
        Assert.NotNull(j2["rig"]!["effects_sha256"]);
    }

    [Fact]
    public void UsedInGameNamesWhatTheGamePlaysEachAnimationFor()
    {
        var au = new WoWData.Core.Usage.AnimationUsage(new Catalog(TestEnv.Client), _res.Animations);
        var dance = au.Of(_res.Animations.IdOfName("EmoteDance")!.Value);
        Assert.Contains("/dance", dance.Emotes);
        Assert.Contains(dance.Roles, r => r.Contains("emotes", StringComparison.Ordinal));
        var cast = au.Of(_res.Animations.IdOfName("SpellCastDirected")!.Value);
        Assert.True(cast.Spells.Count > 50, $"{cast.Spells.Count} spells");
        Assert.Contains(au.Of(4).Roles, r => r.StartsWith("moving", StringComparison.Ordinal));
        Assert.Equal("Walk played backwards", _res.Animations.FallbackText(_res.Animations.IdOfName("Walkbackwards")!.Value));
        Assert.Equal("Death held on its last frame", _res.Animations.FallbackText(6));
        var rep = new WoWData.Core.Usage.UsageReport();
        au.AddSection(rep, M2Model.Load(TestEnv.Client.Read(ModelRoundTripTests.Orc)!));
        var s = rep.Section("animations")!;
        Assert.Contains("used by emotes", s.Summary, StringComparison.Ordinal);
        _out.WriteLine(s.Summary);
    }

    // ---------------------------------------------------------------- through Blender

    private static string AddonRoot => Path.Combine(Oracle.ToolDir, "blender_addon");

    private ModelImportAnalysis? ThroughBlender(string model, ModelSubject subject, object? edit, string tag)
    {
        var blender = BlenderTools.FindBlender();
        if (blender == null)
        {
            _out.WriteLine("Blender 4.5 not found: the Blender part of this test was skipped.");
            return null;
        }
        var res = _svc.Export(model, subject, ModelExportMode.CurrentLook, Path.Combine(_root, tag), includeSkeleton: true);
        var outG = Path.Combine(res.Folder, "blender_export", Path.GetFileName(res.GltfFiles[0]));
        var report = Path.Combine(_root, tag + "_report.json");
        string editFile = "";
        if (edit != null)
        {
            editFile = Path.Combine(_root, tag + "_edit.json");
            File.WriteAllText(editFile, JsonSerializer.Serialize(edit));
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = BlenderTools.RunHeadless(blender, Path.Combine(AddonRoot, "tests", "studio_rig_edit.py"), new[] { report, res.GltfFiles[0], outG, editFile }, 900_000);
        Assert.True(File.Exists(report), "Blender wrote no report: " + r.Output);
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        _out.WriteLine($"Blender {rep["blender"]} in {sw.Elapsed.TotalSeconds:0.0} s: {rep["done"]?.ToJsonString()} rig {rep["export"]?["rig"]?.ToJsonString()}");
        Assert.True(rep["ok"]!.GetValue<bool>(), rep["error"]?.ToString());
        Assert.True(File.Exists(Path.Combine(res.Folder, "blender_export", RigSidecarFile.FileName)));
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        Assert.True(a.Edited.FromBlender);
        Log(a);
        return a;
    }

    [Theory]
    [InlineData(Boar, ModelSubject.Creature)]
    [InlineData(ModelRoundTripTests.Orc, ModelSubject.Character)]
    [InlineData(ModelRoundTripTests.Tree, ModelSubject.Plain)]
    [InlineData(ModelRoundTripTests.Axe, ModelSubject.Item)]
    [InlineData(@"Creature\Wisp\Wisp.m2", ModelSubject.Creature)]
    public void UnchangedRigThroughBlenderIsNoChange(string model, ModelSubject subject)
    {
        var a = ThroughBlender(model, subject, null, "bsame_" + Path.GetFileNameWithoutExtension(model));
        if (a == null) return;
        Assert.True(a.NothingChanged, "a change was found in an unchanged round trip");
        Assert.False(a.AnythingToImport);
        // what the import would write is the original model, byte for byte
        var orig = TestEnv.Client.Read(model)!;
        var f = M2File.Parse(orig);
        RigWriter.Apply(f, a.Rig!.Plan, _res.Animations);
        Assert.Equal(orig, f.Serialize());
    }

    [Fact]
    public void BlenderEditsAddABoneASequenceMoveAnAttachmentAndReadBack()
    {
        var edit = new
        {
            add_bone = new { name = "bone_tail_new", parent_wow_bone = 15, offset = new[] { -0.8, 0.0, -0.2 }, length = 0.2, weight_submesh = 0, weight_axis = 0, weight_below = -0.6, key_bone = -1 },
            new_action = new { from = "Stand", name = "Stand [4]", bone_wow = 21, degrees = 35.0, new_bone_degrees = 40.0 },
            retime = new { action = "Walk", factor = 1.25 },
            move_attachment = new { id = 18, offset = new[] { 0.0, 0.0, 0.15 } },
            add_marker = new { action = "Stand [4]", name = "$CSD", ms = 400 },
        };
        var a = ThroughBlender(Boar, ModelSubject.Creature, edit, "bedit");
        if (a == null) return;
        var rig = RigRow(a);
        var mesh = a.Changes.Single(c => c.Kind == ModelChangeKind.Mesh);
        Assert.True(rig.CanInclude && rig.Include, string.Join("; ", rig.Rules.Where(r => r.Level == RuleLevel.Blocker)));
        Assert.True(mesh.CanInclude && mesh.Include, string.Join("; ", mesh.Rules.Where(r => r.Level == RuleLevel.Blocker)));
        Assert.Equal(new[] { 0 }, mesh.Parts.Where(p => p.Changed).Select(p => p.Submesh).ToArray());
        var cs = ModelImportCommitter.Commit(a, _ws, null);
        Assert.Single(cs.Files);
        var orig = M2Model.Load(TestEnv.Client.Read(Boar)!);
        var nm = M2Model.Load(_ws.ReadEdited(Boar)!);
        Assert.Empty(M2Validator.Errors(M2Validator.Validate(nm)));
        Assert.Equal(orig.Bones.Length + 1, nm.Bones.Length);
        int nb = nm.Bones.Length - 1;
        // the weighted vertices use the new bone, the views stay within their bone limits
        Assert.Contains(nm.Vertices, v => v.B0 == nb && v.W0 > 0);
        for (int vi = 0; vi < nm.Views.Length; vi++) Assert.All(nm.Views[vi].Submeshes, s => Assert.True(s.BoneCount <= nm.Views[vi].BoneLimit));
        var info = RigInfo.Read(nm, _res.Animations);
        Assert.Equal(orig.Sequences.Length + 1, nm.Sequences.Length);
        var st4 = info.Sequences.Single(s => s.Name == "Stand [4]");
        Assert.Equal(orig.Sequences[RigWriter.DefaultTemplate(orig)].Duration, st4.Duration);
        Assert.Equal(1250u, info.Sequences.Single(s => s.Name == "Walk").Duration);
        Assert.Contains(info.Events, e => e.Code == "$CSD" && e.Triggers.Any(t => t.Sequence == st4.Index && Math.Abs((int)t.Time - 400) <= 42));
        Assert.True(Vector3.Distance(orig.Attachments[3].Position + new Vector3(0, 0, 0.15f), nm.Attachments[3].Position) < 1e-3f);
        // the new bone and the head move in the new animation
        var mats = new Matrix4x4[nm.Bones.Length];
        M2Anim.BoneMatrices(nm, new M2Anim.Time(st4.Index, st4.Start, st4.Duration / 2, st4.Duration / 2), mats);
        Assert.False(mats[nb].IsIdentity);
        Assert.False(mats[21].IsIdentity);
        _ws.RevertChangeSet(cs.Id);
        Assert.False(_ws.IsModified(Boar));
    }
}
