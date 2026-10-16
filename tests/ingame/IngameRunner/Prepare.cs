using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Imaging;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace IngameRunner;

/// <summary>One test item's app-level record (written to app_report.json and shown in the gallery).</summary>
public sealed class ItemReport
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string How { get; set; } = "";
    public bool Ok { get; set; }
    public string Verdict { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public List<string> Validation { get; set; } = new();
    public List<string> Compare { get; set; } = new();
    public List<string> ChangeSets { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> Images { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// The 11 edits, each made the way the app makes it (the same Core calls as BrowserViewModel / MapsViewModel / the import windows):
/// texture: TextureService.Export -> PNG edited -> TextureService.Plan (the confirmation's checks and before/after) -> Commit (one change set);
/// model: ModelPackageService.Export -> Blender headless (add-on import, scripted edit, "Export for WoW Data Studio") -> ModelImportAnalyzer
/// (the review rows and rules) -> ModelImportCommitter; terrain: TerrainExporter (3 x 3) -> Blender (edit + Sync shared edges) ->
/// TerrainImporter.Analyze (review) -> MapImporter.Commit. Every workspace write goes through Workspace.SaveEdit (validators).
/// </summary>
public static class Prepare
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string _run = "", _app = "", _clientDir = "";
    static ClientData _client = null!;
    static Workspace _ws = null!;
    static TextureService _ts = null!;
    static AssetIndex? _index;
    static ModelPackageService _svc = null!;
    static string _blender = "";
    static string AddonRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\blender_addon"));
    static string ScriptDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\blender"));

    public static int Run(string clientDir, string home, string runDir, string[] only)
    {
        _run = runDir;
        _clientDir = clientDir;
        _app = Path.Combine(runDir, "app");
        Directory.CreateDirectory(_app);
        var paths = new AppPaths(home).ForClient(clientDir);
        Directory.CreateDirectory(paths.Dir);
        _client = new ClientData(paths.DataDir);
        if (!File.Exists(paths.IndexFile) || !AssetIndex.IsCurrent(paths.IndexFile, _client))
        {
            Console.WriteLine("building the index (as \"Organizing game data\")...");
            var r = IndexBuilder.BuildAsync(_client, paths.IndexFile, new ServerDbSettings()).GetAwaiter().GetResult();
            Console.WriteLine($"  index: {r}");
        }
        _index = new AssetIndex(paths.IndexFile);
        _ws = new Workspace(paths);
        _ts = new TextureService(_client, _ws);
        var catalog = new Catalog(_client);
        var ws = _ws;
        var client = _client;
        var resolver = new ModelResolver(catalog, p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p));
        _svc = new ModelPackageService(_client, catalog, _index, _ws, resolver, _ts);
        _blender = BlenderTools.FindBlender() ?? throw new InvalidOperationException("Blender not found");
        Console.WriteLine($"Blender: {_blender}; add-on {AddonRoot}");

        var reportPath = Path.Combine(runDir, "app_report.json");
        var all = File.Exists(reportPath) ? JsonSerializer.Deserialize<List<ItemReport>>(File.ReadAllText(reportPath)) ?? new() : new List<ItemReport>();
        var items = new (string Id, Func<ItemReport, ItemReport> Fn)[]
        {
            ("01", r => Textures(r, "Creature skin: Mottled Boar (BoarSkinIvory.blp) tinted bright green",
                new[] { (@"Creature\Boar\BoarSkinIvory.blp", Green) })),
            ("02", Scorpid),
            ("03", Tree),
            ("04", Bush),
            ("05", r => Textures(r, "Ground texture (tileset): Tileset\\Durotar\\DurotarDirt.blp tinted bright blue",
                new[] { (@"Tileset\Durotar\DurotarDirt.blp", Blue) })),
            ("05e", r => GroundEditBoth(r, @"Tileset\Durotar\DurotarDirt.blp", Blue)),
            ("05b", r => Textures(r, @"Ground texture, specular variant: Tileset\Durotar\DurotarDirt_s.blp tinted bright blue (what a client with terrain specular shading actually draws)",
                new[] { (@"Tileset\Durotar\DurotarDirt_s.blp", Blue) })),
            ("06", Terrain),
            ("07", r => Textures(r, "Building (WMO) texture: the Den's main exterior texture MM_OGRMR_TRIM_01.BLP tinted yellow",
                new[] { (@"DUNGEONS\TEXTURES\TRIM\MM_OGRMR_TRIM_01.BLP", Yellow) })),
            ("08", r => Textures(r, "Item textures: Acolyte's Robe (item 6129, display 12646) - the 5 male/unisex body textures tinted cyan",
                new[]
                {
                    (@"Item\TextureComponents\TorsoUpperTexture\Robe_Horde_A_02Red_Chest_TU_M.blp", Cyan),
                    (@"Item\TextureComponents\TorsoLowerTexture\Robe_Horde_A_02Red_Chest_TL_M.blp", Cyan),
                    (@"Item\TextureComponents\ArmUpperTexture\Robe_Horde_A_02Red_Sleeve_AU_U.blp", Cyan),
                    (@"Item\TextureComponents\LegUpperTexture\Robe_Horde_A_02Red_Pant_LU_U.blp", Cyan),
                    (@"Item\TextureComponents\LegLowerTexture\Robe_Horde_A_02Red_Pant_LL_U.blp", Cyan),
                })),
            ("09", r => Textures(r, "Spell effect: Shadow Bolt missile (Spells\\DeathCoil_Missile.mdx) - SKULL.BLP + both purple glows tinted bright green",
                new[] { (@"SPELLS\SKULL.BLP", Green), (@"Spells\Purple_Glow2.blp", Green), (@"SPELLS\PURPLE_GLOW.BLP", Green) })),
            ("10", r => Textures(r, "Interface: the Shadow Bolt icon (magenta) and the player/target frame UI-TargetingFrame.blp (green)",
                new[] { (@"Interface\Icons\Spell_Shadow_ShadowBolt.blp", Magenta), (@"Interface\TargetingFrame\UI-TargetingFrame.blp", Green) })),
            ("11", Character),
            // 0.11.0 hot reload test (tests\ingame\hotreload): a creature MODEL change that shows on the boars in view
            ("hr1", BoarMesh),
        };
        foreach (var (id, fn) in items)
        {
            if (only.Length > 0 && !only.Contains(id)) continue;
            var r = new ItemReport { Id = id };
            Console.WriteLine($"==== {id}");
            try { r = fn(r); }
            catch (Exception e) { r.Ok = false; r.Error = e.ToString(); r.Verdict = "FAIL (exception): " + e.Message; }
            Console.WriteLine($"  -> {(r.Ok ? "ok" : "PROBLEM")}: {r.Verdict}");
            all.RemoveAll(x => x.Id == id);
            all.Add(r);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(all.OrderBy(x => x.Id).ToList(), J));
        }
        // the change list as "My changes" shows it
        var list = _ws.List().Select(v => new { v.Record.Path, v.Record.Kind, v.Record.ChangeSet, Set = _ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet)?.Label, v.StatusText, v.Record.Summary }).ToList();
        File.WriteAllText(Path.Combine(runDir, "workspace_changes.json"), JsonSerializer.Serialize(list, J));
        Console.WriteLine($"workspace: {list.Count} files in {_ws.Manifest.ChangeSets.Count} change sets ({paths.ChangesFile})");
        _index?.Dispose();
        return all.All(x => x.Ok) ? 0 : 1;
    }

    // ------------------------------------------------------------------ colours and images

    static readonly (byte R, byte G, byte B) Green = (40, 255, 40), Blue = (30, 110, 255), Yellow = (255, 220, 0), Cyan = (0, 255, 255),
        Magenta = (255, 0, 255), SkinBlue = (40, 90, 255);

    /// <summary>Keeps the texture's light and dark (its detail) but in one vivid colour: out = colour x (0.45 + 0.55 x luminance).</summary>
    static void Tint(RgbaImage img, (byte R, byte G, byte B) c)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            double l = (0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2]) / 255.0, f = 0.45 + 0.55 * l;
            p[i] = (byte)Math.Min(255, c.R * f);
            p[i + 1] = (byte)Math.Min(255, c.G * f);
            p[i + 2] = (byte)Math.Min(255, c.B * f);
        }
    }

    // Blend-aware tints for the spell textures (first pass: the plain tint turned the missile into a coloured square, because these are
    // drawn additively (black = nothing added) or modulated (white = no change) by DeathCoil_Missile's materials)
    static readonly HashSet<string> Additive = new(StringComparer.OrdinalIgnoreCase) { @"Spells\Purple_Glow2.blp", @"SPELLS\PURPLE_GLOW.BLP" };
    // (SKULL.BLP is drawn opaque, blend 0, on the skull mesh: the plain tint is right for it; PURPLE_GLOW is blend 4 = additive, Purple_Glow2 a
    // particle texture, also added. Nothing in this test is modulated, the set stays for completeness.)
    static readonly HashSet<string> Modulate = new(StringComparer.OrdinalIgnoreCase);

    static void TintFor(string path, RgbaImage img, (byte R, byte G, byte B) c)
    {
        if (!Additive.Contains(path) && !Modulate.Contains(path)) { Tint(img, c); return; }
        var p = img.Pixels;
        bool add = Additive.Contains(path);
        for (int i = 0; i < p.Length; i += 4)
        {
            double l = Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])) / 255.0;
            // additive: out = colour x brightness (black stays black); modulate: black -> colour, white stays white
            p[i] = (byte)Math.Min(255, add ? c.R * l : 255 - (255 - c.R) * (1 - l));
            p[i + 1] = (byte)Math.Min(255, add ? c.G * l : 255 - (255 - c.G) * (1 - l));
            p[i + 2] = (byte)Math.Min(255, add ? c.B * l : 255 - (255 - c.B) * (1 - l));
        }
    }

    static string Short(string p) => Path.GetFileNameWithoutExtension(p);

    static string SaveImage(ItemReport r, string name, RgbaImage img)
    {
        var f = Path.Combine(_app, $"{r.Id}_{name}.png");
        Png.Save(img, f, !img.IsOpaque());
        r.Images.Add(Path.GetRelativePath(_run, f));
        return f;
    }

    // ------------------------------------------------------------------ textures (the texture viewer's Export / Import edited PNG)

    static ItemReport Textures(ItemReport r, string title, (string Path, (byte, byte, byte) Color)[] files)
    {
        r.Title = title;
        r.How = "Texture viewer path: TextureService.Export (PNG + .blp.json sidecar) -> PNG tinted by the test -> TextureService.Plan (the import " +
                "confirmation: size/alpha checks, encode like the original, before/after) -> Commit (one change set per texture).";
        bool ok = true;
        foreach (var (path, color) in files)
        {
            if (_ws.IsModified(path)) _ws.Revert(path);
            var png = Path.Combine(_run, "edits", $"{r.Id}_{Short(path)}.png");
            var (_, side) = _ts.Export(path, png, new Dictionary<string, string> { ["test"] = r.Id });
            var img = Png.Load(png);
            TintFor(path, img, color);
            Png.Save(img, png, side.PngMode == "RGBA");
            var plan = _ts.Plan(png, null);
            if (plan.NeedsResize) throw new InvalidOperationException($"{path}: unexpected size question");
            r.Files.Add(plan.TargetPath);
            r.Validation.Add($"{Short(path)}: will be saved as {plan.FormatName} {plan.EncodedBlp.Width}x{plan.EncodedBlp.Height}, {plan.EncodedBlp.LevelCount} mips " +
                             $"(original {plan.Original?.FormatName}); BLP validator: ok; warnings: {(plan.Warnings.Count == 0 ? "none" : string.Join(" | ", plan.Warnings))}" +
                             (plan.Notes.Count > 0 ? "; notes: " + string.Join(" | ", plan.Notes) : ""));
            var reenc = plan.Report != null && plan.Report.PerLevel.Count > 0 ? $"; re-encoded {plan.Report.PerLevel.Sum(l => l.ReEncoded):N0} of {plan.Report.PerLevel.Sum(l => l.Blocks ?? l.Pixels ?? 0):N0} blocks/pixels" : "";
            r.Compare.Add($"{Short(path)}: {plan.Diff?.Summary}{reenc}");
            // the test's own RGB-only count (alpha ignored): for textures whose alpha is not transparency in the game (opaque WMO / M2
            // materials use it as a mask or not at all) the colour under alpha 0 is visible in the game
            double rgbPct = 0;
            if (plan.OriginalImage != null && plan.OriginalImage.Width == plan.EditedImage.Width)
            {
                int n = 0, tot = plan.OriginalImage.Pixels.Length / 4;
                for (int i = 0; i < plan.OriginalImage.Pixels.Length; i += 4)
                    if (Math.Abs(plan.OriginalImage.Pixels[i] - plan.EditedImage.Pixels[i]) + Math.Abs(plan.OriginalImage.Pixels[i + 1] - plan.EditedImage.Pixels[i + 1]) + Math.Abs(plan.OriginalImage.Pixels[i + 2] - plan.EditedImage.Pixels[i + 2]) > 6) n++;
                rgbPct = 100.0 * n / tot;
                r.Compare.Add($"{Short(path)}: (test's own count, alpha ignored) {rgbPct:0.0}% of pixels changed colour");
            }
            if (rgbPct < 20) { ok = false; r.Notes.Add($"{Short(path)}: less than 20% of the pixels changed colour"); }
            if (plan.Identical) { ok = false; r.Notes.Add($"{Short(path)}: identical to the original"); }
            if (plan.OriginalImage != null)
            {
                SaveImage(r, Short(path) + "_original", plan.OriginalImage);
                SaveImage(r, Short(path) + "_edit", plan.EditedImage);
                SaveImage(r, Short(path) + "_difference", ImageCompare.Heatmap(plan.OriginalImage, plan.EditedImage));
            }
            var rec = _ts.Commit(plan, _index, $"Test {r.Id}: {Short(path)} tinted");
            r.ChangeSets.Add($"{rec.ChangeSet} \"{_ws.Manifest.ChangeSets[rec.ChangeSet].Label}\": {rec.Path} ({rec.Kind}, {rec.Original.Format}, used by {rec.Original.Models.Count} model(s): {string.Join(", ", rec.Original.Models.Take(4).Select(Path.GetFileName))}{(rec.Original.Models.Count > 4 ? ", ..." : "")}) summary: {rec.Summary}");
        }
        r.Ok = ok;
        r.Verdict = ok ? $"app steps passed ({files.Length} texture(s) imported, each its own change set)" : "app-level problem: " + string.Join("; ", r.Notes);
        return r;
    }

    /// <summary>0.6.1 re-test of item 05: the base ground texture (the file the tile breakdown names) imported the way
    /// BrowserViewModel.ImportPngAsync does it for a ground texture with "Edit both (recommended)": TextureService.Plan for the
    /// base -> AssetIndex.GroundPair -> TextureService.PlanPartner(main, "_s" partner, keepPartnerAlpha: true) -> one Commit of both.</summary>
    static ItemReport GroundEditBoth(ItemReport r, string path, (byte, byte, byte) color)
    {
        r.Title = $@"Ground texture (0.6.1 Edit both): {path} tinted bright blue, imported with ""Edit both (recommended)""";
        r.How = "Texture viewer path as in 0.6.1: TextureService.Export -> PNG tinted -> TextureService.Plan (base) -> the index's GroundPair -> " +
                "TextureService.PlanPartner(_s, keep its alpha = the shine map) -> one Commit of both (one change set, as the import window saves it).";
        var g = TerrainSpecularSettings.Read(_clientDir);
        r.Notes.Add("game settings: " + g.Describe() + $" -> draws the _s files: {g.UsesSpecularTextures}");
        var pair = _index!.GroundPair(path) ?? throw new InvalidOperationException($"{path}: the index does not know it as a ground texture");
        var partner = pair.Partner(path) ?? throw new InvalidOperationException($"{path}: no _s partner");
        r.Notes.Add($"pair: base {pair.Base}, specular {pair.Specular}; {GroundTextures.WhichIsDrawn(pair, g)}");
        foreach (var p in new[] { path, partner }) if (_ws.IsModified(p)) _ws.Revert(p);
        var png = Path.Combine(_run, "edits", $"{r.Id}_{Short(path)}.png");
        var (_, side) = _ts.Export(path, png, new Dictionary<string, string> { ["test"] = r.Id });
        var img = Png.Load(png);
        TintFor(path, img, color);
        Png.Save(img, png, side.PngMode == "RGBA");
        var main = _ts.Plan(png, null);
        if (main.NeedsResize) throw new InvalidOperationException($"{path}: unexpected size question");
        var p2 = _ts.PlanPartner(main, partner, true);
        var plans = new List<ImportPlan> { main };
        if (!p2.Identical) plans.Add(p2);
        bool ok = plans.Count == 2;
        if (!ok) r.Notes.Add("the _s partner plan is identical to the game's file");
        // the _s alpha (shine map) must be the game's own, byte for byte in the decoded top level
        var sOrig = BlpFileOf(_client.Read(partner)!);
        var alphaKept = !GroundTextures.AlphaDiffers(sOrig, p2.EditedImage);
        int maxA = 0;
        for (int i = 3; i < sOrig.Pixels.Length; i += 4) maxA = Math.Max(maxA, Math.Abs(sOrig.Pixels[i] - p2.EditedImage.Pixels[i]));
        r.Notes.Add($"{Short(partner)}: alpha (shine map) kept exactly after encoding: {alphaKept} (largest alpha difference {maxA})");
        if (maxA > 0) ok = false;
        foreach (var pl in plans)
        {
            var n = Short(pl.TargetPath);
            r.Files.Add(pl.TargetPath);
            r.Validation.Add($"{n}: will be saved as {pl.FormatName} {pl.EncodedBlp.Width}x{pl.EncodedBlp.Height}, {pl.EncodedBlp.LevelCount} mips " +
                             $"(original {pl.Original?.FormatName}); BLP validator: ok; warnings: {(pl.Warnings.Count == 0 ? "none" : string.Join(" | ", pl.Warnings))}" +
                             (pl.Notes.Count > 0 ? "; notes: " + string.Join(" | ", pl.Notes) : ""));
            r.Compare.Add($"{n}: {pl.Diff?.Summary}");
            if (pl.Diff == null || pl.Diff.ColourChangedPercent < 95) { ok = false; r.Notes.Add($"{n}: compare reports colour changed in {pl.Diff?.ColourChangedPercent:0.0}% (< 95%)"); }
            if (pl.OriginalImage != null)
            {
                SaveImage(r, n + "_original", pl.OriginalImage);
                SaveImage(r, n + "_edit", pl.EditedImage);
                SaveImage(r, n + "_difference", ImageCompare.Heatmap(pl.OriginalImage, pl.EditedImage));
            }
        }
        var recs = _ts.Commit(plans, _index);
        var sets = recs.Select(x => x.ChangeSet).Distinct().ToList();
        foreach (var rec in recs)
            r.ChangeSets.Add($"{rec.ChangeSet} \"{_ws.Manifest.ChangeSets[rec.ChangeSet].Label}\": {rec.Path} ({rec.Kind}, {rec.Original.Format}) summary: {rec.Summary}");
        if (sets.Count != 1) { ok = false; r.Notes.Add($"saved as {sets.Count} change sets, expected 1"); }
        var inSet = _ws.List().Where(v => v.Record.ChangeSet == sets[0]).Select(v => Path.GetFileName(v.Record.Path)).OrderBy(x => x).ToList();
        r.Notes.Add("change set contains: " + string.Join(", ", inSet));
        if (!inSet.Contains("DurotarDirt.blp", StringComparer.OrdinalIgnoreCase) || !inSet.Contains("DurotarDirt_s.blp", StringComparer.OrdinalIgnoreCase)) ok = false;
        // read back from the workspace: the saved _s keeps the game's alpha
        var saved = BlpFileOf(_ws.ReadEdited(partner)!);
        int maxSaved = 0;
        for (int i = 3; i < sOrig.Pixels.Length; i += 4) maxSaved = Math.Max(maxSaved, Math.Abs(sOrig.Pixels[i] - saved.Pixels[i]));
        r.Notes.Add($"{Short(partner)} as saved in the workspace: largest alpha difference to the game's file {maxSaved}");
        if (maxSaved > 0) ok = false;
        r.Ok = ok;
        r.Verdict = ok ? "app steps passed (Edit both: base + _s in one change set, shine map kept, ~100% colour changed)" : "app-level problem: " + string.Join("; ", r.Notes);
        return r;
    }

    static RgbaImage BlpFileOf(byte[] data) => WoWData.Core.Blp.BlpFile.Parse(data).Decode(0);

    // ------------------------------------------------------------------ models (Export for Blender / Import edited model)

    static JsonNode RunBlender(string script, string tag, string[] args, object edit)
    {
        var dir = Path.Combine(_run, "blender");
        Directory.CreateDirectory(dir);
        var editFile = Path.Combine(dir, tag + "_edit.json");
        File.WriteAllText(editFile, JsonSerializer.Serialize(edit, J));
        var report = Path.Combine(dir, tag + "_report.json");
        if (File.Exists(report)) File.Delete(report);
        var all = new List<string> { AddonRoot, report };
        all.AddRange(args);
        all.Add(editFile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = BlenderTools.RunHeadless(_blender, Path.Combine(ScriptDir, script), all, 900_000);
        File.WriteAllText(Path.Combine(dir, tag + "_blender_output.txt"), res.Output);
        if (!File.Exists(report)) throw new InvalidOperationException($"Blender wrote no report ({tag}): {res.Output[^Math.Min(2000, res.Output.Length)..]}");
        var rep = JsonNode.Parse(File.ReadAllText(report))!;
        Console.WriteLine($"  Blender {rep["blender"]} {tag}: {sw.Elapsed.TotalSeconds:0} s, ok={rep["ok"]}");
        if (rep["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"Blender failed ({tag}): {rep["error"]}");
        return rep;
    }

    static void LogAnalysis(ItemReport r, ModelImportAnalysis a)
    {
        r.Validation.Add($"import source: {a.Edited.Description}; base: {a.BaseDescription}");
        foreach (var g in a.General) r.Validation.Add($"[{g.Level}] {g.Text}");
        if (a.UsersWarning.Length > 0) r.Validation.Add("users: " + a.UsersWarning);
        foreach (var c in a.Changes)
        {
            r.Validation.Add($"row {c.Kind} \"{c.Title}\": {(c.Unchanged ? "unchanged" : c.Include ? "INCLUDED" : "not included")}, can include={c.CanInclude}; {c.Detail}");
            foreach (var x in c.Rules) r.Validation.Add($"    [{x.Level}] {x.Text}");
            if (c.Kind == ModelChangeKind.Mesh) foreach (var p in c.Parts) r.Compare.Add(p.Summary);
            if (c.Plan?.Diff != null) r.Compare.Add($"{c.Title}: {c.Plan.Diff.Summary}");
            if (c.Rebuild != null) r.Compare.Add($"rebuild: {c.Rebuild}");
        }
    }

    static ModelExportResult Export(string model, ModelSubject subject, string tag, long? display = null, CharacterLook? look = null)
    {
        if (_ws.IsModified(model)) _ws.Revert(model);
        var folder = Path.Combine(_run, "packages", tag);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        return _svc.Export(model, subject, ModelExportMode.CurrentLook, folder, currentDisplay: display, characterLook: look);
    }

    static string BlenderOut(ModelExportResult res) => Path.Combine(res.Folder, "blender_export", Path.GetFileName(res.GltfFiles[0]));

    /// <summary>Animation sanity of an edited model: every sequence evaluates to finite positions; the edited part really moves.</summary>
    static void AnimationChecks(ItemReport r, string model)
    {
        var orig = M2Model.Load(_client.Read(model)!);
        var ed = M2Model.Load(_ws.ReadEdited(model)!);
        var errs = M2Validator.Errors(M2Validator.Validate(ed));
        r.Validation.Add($"validator on the saved model: {(errs.Count == 0 ? "no errors" : string.Join("; ", errs))}");
        var mats = new Matrix4x4[ed.Bones.Length];
        int bad = 0;
        for (int si = 0; si < ed.Sequences.Length; si++)
        {
            var s = ed.Sequences[si];
            M2Anim.BoneMatrices(ed, new M2Anim.Time(si, s.Start, s.Duration / 2, s.Duration / 2), mats);
            foreach (var v in ed.Vertices) { var p = M2Bounds.Skin(v, mats); if (!float.IsFinite(p.X + p.Y + p.Z)) bad++; }
        }
        r.Validation.Add($"animations: {ed.Sequences.Length} sequences (ids {string.Join(" ", ed.Sequences.Select(s => s.AnimId).Distinct())}) evaluated at mid-time: {(bad == 0 ? "all vertices finite" : bad + " non-finite vertex positions")}");
        (Vector3, Vector3) Box(M2Model m) { var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue); foreach (var v in m.Vertices) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); } return (lo, hi); }
        var (a0, a1) = Box(orig); var (b0, b1) = Box(ed);
        r.Compare.Add($"rest-pose box: {Fmt(a0)}..{Fmt(a1)} -> {Fmt(b0)}..{Fmt(b1)}; vertices {orig.Vertices.Length} -> {ed.Vertices.Length}; bones {orig.Bones.Length} -> {ed.Bones.Length} (kept); sequences {orig.Sequences.Length} -> {ed.Sequences.Length}");
        var union = (Vector3.Min(a0, b0), Vector3.Max(a1, b1));
        SaveImage(r, Short(model) + "_mesh_original", MeshPicture.Render(orig, union));
        SaveImage(r, Short(model) + "_mesh_edit", MeshPicture.Render(ed, union));
        var co = ModelExporter.PartChecksums(orig.File); var ce = ModelExporter.PartChecksums(ed.File);
        r.Compare.Add("unchanged blocks: " + string.Join(", ", co.Keys.Where(k => ce.TryGetValue(k, out var v) && v == co[k])) + "; changed: " + string.Join(", ", co.Keys.Where(k => !ce.TryGetValue(k, out var v) || v != co[k])));
        if (errs.Count > 0 || bad > 0) { r.Ok = false; r.Notes.Add("the saved model fails the validator or its animations"); }
    }

    static string Fmt(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";

    static ItemReport CommitModel(ItemReport r, ModelImportAnalysis a, string label, Func<ModelChange, bool> expected)
    {
        var rows = a.Changes.Where(c => !c.Unchanged).ToList();
        var blocked = rows.Where(c => c.HasBlocker).ToList();
        r.Ok = rows.Count > 0 && rows.All(expected) && blocked.Count == 0 && a.AnythingToImport;
        if (!r.Ok) { r.Verdict = $"app-level problem: {rows.Count} changed rows, {blocked.Count} blocked"; return r; }
        var cs = ModelImportCommitter.Commit(a, _ws, _index, label);
        r.ChangeSets.Add($"{cs.Id} \"{cs.Label}\": {string.Join(", ", cs.Files.Select(k => _ws.Manifest.Files[k].Path))}");
        foreach (var k in cs.Files) { var rec = _ws.Manifest.Files[k]; r.Files.Add(rec.Path); r.Notes.Add($"{rec.Path}: {rec.Summary}"); }
        r.Verdict = $"app steps passed: {rows.Count} change row(s) included, committed as one change set ({cs.Files.Count} file(s))";
        return r;
    }

    static ItemReport Scorpid(ItemReport r)
    {
        const string model = @"Creature\Scorpion\Scorpion.m2";
        r.Title = "Creature mesh: Scorpid Worker (Scorpion.m2, display 2485) - the stinger and both pincers (claw + finger) scaled 2x about their own joints (Blender)";
        r.How = "Model path: Export for Blender (current look) -> Blender 4.5 headless (add-on import; script scales the vertices of the leaf bones " +
                "of the stinger and pincers about their own pivots; Export for WoW Data Studio) -> Import edited model (analysis rows/rules) -> commit.";
        var res = Export(model, ModelSubject.Creature, "02_scorpion", 2485);
        // 1. which bones are which: the add-on's skeleton in Blender (vertex groups with their mean position and bone head)
        var rep = RunBlender("ingame_model_edit.py", "02_scorpion_report", new[] { res.GltfFiles[0], BlenderOut(res) }, new { report_only = true });
        var groups = rep["groups"]!.AsObject().Select(kv => (Name: kv.Key, N: kv.Value!["vertices"]!.GetValue<int>(),
            Mean: kv.Value!["mean"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray(),
            Head: kv.Value!["head"]?.AsArray().Select(x => x!.GetValue<double>()).ToArray(), Parent: kv.Value!["parent"]?.GetValue<string>())).ToList();
        var parents = new HashSet<string>(groups.Where(g => g.Parent != null).Select(g => g.Parent!));
        var leaves = groups.Where(g => !parents.Contains(g.Name) && g.Head != null && g.N >= 8).ToList();
        // Blender axes after the add-on's import: Z up; the model faces +X in M2 space -> the front is the largest X after the import rotation?
        // pick by the data: the highest leaf (stinger) and the two leaves furthest from the body centre horizontally in the front half (pincers)
        // stinger = the highest leaf (end of the tail chain); claws = the two front-most leaves (the moving pincer fingers) and their parents
        // (the claws), each claw + finger scaled as ONE piece about the claw's joint so the finger stays attached
        r.Notes.Add("skeleton (leaf bones with >= 8 vertices): " + string.Join("; ", leaves.Select(g => $"{g.Name} n={g.N} mean=({g.Mean[0]:0.00},{g.Mean[1]:0.00},{g.Mean[2]:0.00})")));
        var allLeaves = groups.Where(g => !parents.Contains(g.Name) && g.Head != null).ToList();
        var stinger = new[] { allLeaves.OrderByDescending(g => g.Mean[2]).First() };
        var fingers = allLeaves.OrderByDescending(g => g.Mean[0]).Take(2).ToList();
        var claws = fingers.Select(f => groups.First(g => g.Name == f.Parent)).ToList();
        var chosen = stinger.Concat(fingers).Concat(claws).Select(g => g.Name).Distinct().ToList();
        var pivotOf = fingers.ToDictionary(f => f.Name, f => f.Parent!);
        r.Notes.Add($"scaled: stinger {stinger[0].Name} about its joint; claws {string.Join(",", claws.Select(g => g.Name))} with fingers {string.Join(",", fingers.Select(g => g.Name))} about the claw joints");
        // 2. the edit
        var rep2 = RunBlender("ingame_model_edit.py", "02_scorpion_edit", new[] { res.GltfFiles[0], BlenderOut(res) }, new { scale_groups = new { groups = chosen, factor = 2.0, pivot_of = pivotOf } });
        r.Notes.Add($"Blender: {rep2["scaled_vertices"]} vertices scaled; export {rep2["export"]?.ToJsonString()}");
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        LogAnalysis(r, a);
        CommitModel(r, a, "Test 02: scorpion stinger and pincers x2 (Blender)", c => c.Kind == ModelChangeKind.Mesh);
        if (r.Ok) AnimationChecks(r, model);
        return r;
    }

    static ItemReport Tree(ItemReport r)
    {
        const string model = @"World\Kalimdor\Durotar\PassiveDoodads\Trees\DurotarTree03.m2";
        r.Title = "Doodad texture: DurotarTree03 (49 yd NE of the start) - trunk and branch textures painted magenta in Blender";
        r.How = "Model path, textures only: Export for Blender -> Blender headless tints the trunk (DuratorFatTree02c) and branch (DuratorFatTree_branch02b) " +
                "images -> Export for WoW Data Studio -> Import edited model (texture rows: encoded like the original) -> commit.";
        var res = Export(model, ModelSubject.Plain, "03_tree");
        var tint = res.Manifest.Textures.Where(t => t.Editable && t.GamePath != null && (t.GamePath.Contains("FatTree02c", StringComparison.OrdinalIgnoreCase) || t.GamePath.Contains("FatTree_branch02b", StringComparison.OrdinalIgnoreCase)))
            .Select(t => new { texture_id = t.Id, rect = new[] { 0.0, 0.0, 1.0, 1.0 }, color = new[] { 1.0, 0.0, 1.0 } }).ToArray();
        r.Notes.Add($"tinted texture ids: {string.Join(", ", tint.Select(t => t.texture_id))}");
        var rep = RunBlender("ingame_model_edit.py", "03_tree_edit", new[] { res.GltfFiles[0], BlenderOut(res) }, new { tint });
        r.Notes.Add($"Blender: {rep["tinted_pixels"]} pixels tinted");
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        LogAnalysis(r, a);
        foreach (var c in a.Changes.Where(c => c.Plan?.OriginalImage != null && c.Plan.EditedImage != null && !c.Unchanged))
        {
            SaveImage(r, Short(c.GamePath!) + "_original", c.Plan!.OriginalImage!);
            SaveImage(r, Short(c.GamePath!) + "_edit", c.Plan.EditedImage);
        }
        return CommitModel(r, a, "Test 03: DurotarTree03 textures magenta (Blender)", c => c.Kind == ModelChangeKind.Texture);
    }

    static ItemReport Bush(ItemReport r)
    {
        const string model = @"World\Kalimdor\Durotar\PassiveDoodads\Bushes\DurotarBush01.m2";
        r.Title = "Doodad mesh: DurotarBush01 (29 yd W of the start, and every other placement) scaled 4x in Blender";
        r.How = "Model path, mesh: Export for Blender -> Blender headless scales every vertex 4x about the model origin -> Export for WoW Data Studio -> " +
                "Import edited model (mesh row, rebuilt views and bounds) -> commit.";
        var res = Export(model, ModelSubject.Plain, "04_bush");
        var rep = RunBlender("ingame_model_edit.py", "04_bush_edit", new[] { res.GltfFiles[0], BlenderOut(res) }, new { scale_all = new { factor = 4.0 } });
        r.Notes.Add($"Blender: {rep["scaled_vertices"]} vertices scaled");
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        LogAnalysis(r, a);
        CommitModel(r, a, "Test 04: DurotarBush01 x4 (Blender)", c => c.Kind == ModelChangeKind.Mesh);
        if (r.Ok) AnimationChecks(r, model);
        return r;
    }

    /// <summary>0.11.0 (tests\ingame\hotreload): the Mottled Boar's model scaled 1.5x, so a unit's model change shows after a reload in
    /// the running game (CGUnit_C_UpdateDisplayModel path of wowcode.dll HR1).</summary>
    static ItemReport BoarMesh(ItemReport r)
    {
        const string model = @"Creature\Boar\Boar.m2";
        r.Title = "Creature mesh: Mottled Boar (Boar.m2, display 503) scaled 1.5x in Blender (hot reload test)";
        r.How = "Model path, mesh: Export for Blender (current look) -> Blender headless scales every vertex 1.5x about the model origin -> " +
                "Export for WoW Data Studio -> Import edited model (mesh row, rebuilt views and bounds) -> commit.";
        var res = Export(model, ModelSubject.Creature, "hr1_boar", 503);
        var rep = RunBlender("ingame_model_edit.py", "hr1_boar_edit", new[] { res.GltfFiles[0], BlenderOut(res) }, new { scale_all = new { factor = 1.5 } });
        r.Notes.Add($"Blender: {rep["scaled_vertices"]} vertices scaled");
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        LogAnalysis(r, a);
        CommitModel(r, a, "Hot reload test: Boar.m2 x1.5 (Blender)", c => c.Kind == ModelChangeKind.Mesh);
        if (r.Ok) AnimationChecks(r, model);
        return r;
    }

    // ------------------------------------------------------------------ terrain (Open terrain in Blender / Import edited terrain)

    static ItemReport Terrain(ItemReport r)
    {
        r.Title = "Terrain heights: a 22 yd mound (radius 18 yd) 25 yd NE of the start, across the tile edge Kalimdor_39_33 | 40_33 (Blender, Sync shared edges)";
        r.How = "Maps path: TerrainExporter 3 x 3 around Kalimdor 39,33 -> Blender headless (add-on terrain import; mound raised on both tiles but the " +
                "neighbour's shared edge row left out, i.e. a seam; Sync shared edges; Export terrain for WoW Data Studio) -> TerrainImporter.Analyze " +
                "(review: seams, validator, diff) -> MapImporter.Commit (one change set).";
        var cat = MapCatalog.Load(_client, null);
        var map = cat.ByDirectory("Kalimdor")!;
        foreach (var x in new[] { 38, 39, 40 }) foreach (var y in new[] { 32, 33, 34 }) if (_ws.IsModified(map.TilePath(x, y))) _ws.Revert(map.TilePath(x, y));
        var folder = Path.Combine(_run, "packages", "06_terrain");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var ws = _ws; var client = _client;
        var res = TerrainExporter.Export(new TerrainExportRequest
        {
            Map = map, Tiles = TerrainExporter.Square(map, 39, 33, 3), Center = (39, 33),
            ReadCurrent = p => (ws.IsModified(p) ? ws.ReadEdited(p) : null) ?? client.Read(p), ReadGame = client.Read,
            IsModified = p => ws.IsModified(p), ArchiveOf = p => client.Find(p)?.Name, IncludeReferences = false, PackageFolder = folder,
            LoadTexture = p => client.Read(p) is { } d ? WoWData.Core.Blp.BlpFile.Parse(d).Decode(0) : null,
        });
        r.Notes.Add($"package: {res.Manifest.Tiles.Count} tiles, {res.Manifest.Textures.Count} ground textures; origin_world {string.Join(",", res.Manifest.OriginWorld)}");
        // the mound centre in package coordinates (X east, Y north, the centre tile's north-west corner at the origin)
        const float wx = -600f, wy = -4268f;
        double tileNorth = TerrainMath.MapHalf - 33 * TerrainMath.TileSize, tileWest = TerrainMath.MapHalf - 39 * TerrainMath.TileSize;
        double px = tileWest - wy, py = wx - tileNorth;
        r.Notes.Add($"mound centre world ({wx},{wy}) = package ({px:0.0},{py:0.0})");
        var rep = RunBlender("ingame_terrain_edit.py", "06_terrain_edit", new[] { res.ManifestPath },
            new { center = new[] { px, py }, radius = 18.0, height = 22.0, skip_edge = new { tile = "Tile 40_33", edge = "W" } });
        r.Validation.Add($"Blender: raised {rep["raised_vertices"]?.ToJsonString()}; ground at centre {rep["ground_at_center"]?.ToJsonString()}");
        r.Validation.Add($"Blender seams at import {rep["seams_at_import"]?.ToJsonString()}; after edit {rep["seams_after_edit"]?.ToJsonString()}; Sync shared edges {rep["sync"]?.ToJsonString()}; after sync {rep["seams_after_sync"]?.ToJsonString()}");
        var a = TerrainImporter.Analyze(folder, d => cat.ByDirectory(d), _client, _ws, false);
        foreach (var g in a.General) r.Validation.Add("review: " + g);
        foreach (var s in a.Seams) r.Validation.Add("review seam: " + s);
        foreach (var s in a.Ignored) r.Validation.Add("review ignored: " + s);
        foreach (var c in a.Changes)
        {
            r.Validation.Add($"row {c.FileName}: {(c.Unchanged ? "unchanged" : c.Summary)}; can include={c.CanInclude}; validator ok={c.Validation?.Ok}");
            foreach (var n in c.Notes) r.Validation.Add("    " + n);
        }
        var changed = a.Changes.Where(c => !c.Unchanged).ToList();
        foreach (var c in changed)
        {
            var before = TerrainTile.Load(_client.Read(map.TilePath(c.Tile!.Value.X, c.Tile!.Value.Y))!, "Kalimdor", c.Tile!.Value.X, c.Tile!.Value.Y);
            var after = TerrainTile.Load(c.NewBytes!, "Kalimdor", c.Tile!.Value.X, c.Tile!.Value.Y);
            var heat = TerrainTile.HeightDifference(before, after, 256, out var maxDelta, out var chunks);
            r.Compare.Add($"{c.FileName}: height change up to {maxDelta:0.0} yd in {chunks} chunk(s)");
            SaveImage(r, $"tile_{c.Tile!.Value.X}_{c.Tile!.Value.Y}_height_difference", heat);
            SaveImage(r, $"tile_{c.Tile!.Value.X}_{c.Tile!.Value.Y}_relief_after", after.Relief(256));
        }
        bool ok = a.Seams.Count == 0 && changed.Count == 2 && changed.All(c => c.CanInclude && c.Include && c.Validation.Ok);
        if (!ok) { r.Ok = false; r.Verdict = $"app-level problem: {changed.Count} changed tiles, {a.Seams.Count} seams"; return r; }
        var cs = MapImporter.Commit(a, _ws);
        r.ChangeSets.Add($"{cs.Id} \"{cs.Label}\": {string.Join(", ", cs.Files.Select(k => _ws.Manifest.Files[k].Path))}");
        foreach (var k in cs.Files) r.Files.Add(_ws.Manifest.Files[k].Path);
        r.Ok = true;
        r.Verdict = "app steps passed: seam opened by the edit was closed by Sync shared edges, no seam in the review, 2 tiles changed and validated, one change set";
        return r;
    }

    // ------------------------------------------------------------------ character skin

    static ItemReport Character(ItemReport r)
    {
        r.Title = "Character skin (Statclone: orc male, skin 3): (a) the composite body skin edited in an exported model package; (b) the CharSections file OrcMaleSkin00_03.blp tinted blue";
        r.How = "(a) Export for Blender of OrcMale.m2 with the character's look, the composite PNG tinted, Import edited model -> expected: a clear refusal. " +
                "(b) texture viewer path on the skin component file the game composites from (TextureService).";
        var look = new CharacterLook(3, 2, 6, 7, 5);
        var res = Export(@"Character\Orc\Male\OrcMale.m2", ModelSubject.Character, "11_orcmale", look: look);
        var comp = res.Manifest.Textures.FirstOrDefault(t => t.Kind == "composite");
        if (comp == null) { r.Ok = false; r.Verdict = "no composite texture in the package"; return r; }
        r.Notes.Add($"package composite texture: {comp.Png} editable={comp.Editable}; note: {comp.Note}");
        var file = Path.Combine(res.Folder, comp.Png.Replace('/', Path.DirectorySeparatorChar));
        var img = Png.Load(file);
        Tint(img, SkinBlue);
        Png.Save(img, file, !img.IsOpaque());
        var a = ModelImportAnalyzer.Analyze(res.Folder, _svc.ImportContext());
        LogAnalysis(r, a);
        var row = a.Changes.FirstOrDefault(c => c.Texture?.Kind == "composite" && !c.Unchanged);
        bool refused = row != null && row.HasBlocker && !row.CanInclude && !a.AnythingToImport;
        r.Validation.Add(refused ? $"(a) REFUSED as expected: \"{row!.Rules.First(x => x.Level == RuleLevel.Blocker).Text}\"" : "(a) NOT refused");
        // (b) the component file
        var b = Textures(new ItemReport { Id = r.Id }, "", new[] { (@"Character\Orc\Male\OrcMaleSkin00_03.blp", SkinBlue) });
        r.Files.AddRange(b.Files); r.Validation.AddRange(b.Validation.Select(v => "(b) " + v)); r.Compare.AddRange(b.Compare); r.ChangeSets.AddRange(b.ChangeSets); r.Images.AddRange(b.Images); r.Notes.AddRange(b.Notes);
        r.Ok = refused && b.Ok;
        r.Verdict = (refused ? "(a) composite edit clearly refused (PASS)" : "(a) composite edit NOT refused (FAIL)") + "; (b) " + b.Verdict;
        return r;
    }
}
