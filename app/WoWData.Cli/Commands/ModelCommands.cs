using System.Diagnostics;
using WoWData.Core;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWData.Core.ModelIO;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace WoWData.Cli.Commands;

/// <summary>models: info, export to Blender (glTF), open in Blender, import back, validate, compare, the add-on, render.</summary>
internal static class ModelCommands
{
    /// <summary>The model file of an entry or a path (.m2).</summary>
    public static string ModelPath(AppSession s, string text)
    {
        var p = text.Replace('/', '\\');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if ((p.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)) && s.FileExists(p)) return s.Catalog!.Canonical(p);
        var e = Common.FindEntry(s, text);
        if (e.Path != null && e.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) return s.Catalog!.Canonical(e.Path);
        var d = s.Index!.Details(e, s.Catalog);
        return d.Models.FirstOrDefault(m => m.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) ?? throw new CliError(ExitCode.NotFound, $"{e.Name} ({e.KindLabel}) has no model (.m2).");
    }

    /// <summary>The creature display of an entry (for "the look shown"), or null.</summary>
    private static long? DisplayOf(AppSession s, string text)
    {
        var e = s.FindEntry(text);
        if (e == null) return null;
        if (e.Key.StartsWith("cdisplay:", StringComparison.Ordinal)) return e.Id;
        if (e.Key.StartsWith("creature:", StringComparison.Ordinal)) return s.Index!.CreatureDisplayIds(e.Id).Cast<long?>().FirstOrDefault();
        return null;
    }

    /// <summary>WoWDataStudio.exe: next to wds.exe (installed), WDS_APP_EXE, or the developer build of the app.</summary>
    public static string? AppExe()
    {
        var env = Environment.GetEnvironmentVariable("WDS_APP_EXE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var here = Path.Combine(AppContext.BaseDirectory, StudioInfo.GuiExe);
        if (File.Exists(here)) return here;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var p = Path.Combine(d.FullName, "WoWDataStudio", "bin", cfg, "net8.0-windows", StudioInfo.GuiExe);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    private static object Stats(M2Model m) => new
    {
        vertices = m.Vertices.Length,
        triangles = m.Views.Length > 0 ? m.Views[0].Triangles.Length / 3 : 0,
        views = m.Views.Length,
        submeshes = m.Views.Length > 0 ? m.Views[0].Submeshes.Length : 0,
        bones = m.Bones.Length,
        sequences = m.Sequences.Length,
        textures = m.Textures.Length,
        attachments = m.Attachments.Length,
        geosets = m.Views.Length > 0 ? m.Views[0].Submeshes.Select(x => x.GeosetId).Distinct().Count() : 0,
    };

    private static void StatsLines(CliResult r, string label, M2Model m) =>
        r.Line($"  {label}: {m.Vertices.Length:N0} vertices, {(m.Views.Length > 0 ? m.Views[0].Triangles.Length / 3 : 0):N0} triangles, {m.Bones.Length} bones, {m.Sequences.Length} animations, " +
               $"{m.Textures.Length} textures, {(m.Views.Length > 0 ? m.Views[0].Submeshes.Length : 0)} parts, {m.Attachments.Length} attachment points");

    private static async Task<int> RunApp(CliContext ctx, IEnumerable<string> args, List<string> output)
    {
        var exe = AppExe() ?? throw new CliError(ExitCode.NotFound, $"{StudioInfo.GuiExe} was not found next to wds.exe: rendering uses the app's 3D renderer. Repair the installation.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new CliError(ExitCode.Error, $"{exe} could not be started.");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(300_000))
        {
            p.Kill(true);
            throw new CliError(ExitCode.Error, "The renderer did not finish in 5 minutes.");
        }
        output.AddRange((await so).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        output.AddRange((await se).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20));
        return p.ExitCode;
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "models info",
            Summary = "a model's statistics, its displays and skins, who a mesh change affects, your edit and its checks",
            Args = new[] { "<model>" },
            Details = "The model is a path (Creature\\Boar\\Boar.m2) or an entry (\"Mottled Boar\", creature:3098, cdisplay:503). With --version: another version's model (a path, #FileDataID or an entry), the file format read, its chunks, skin, texture, skeleton and animation files, its particle emitters and ribbons, and what is drawn approximately or not shown.",
            Examples = new[] { "wds models info Creature\\Boar\\Boar.m2", "wds models info \"Mottled Boar\" --json", "wds models info cdisplay:503 --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return ModelCommandsOther.Info(inv.Ctx, s, ModelCommandsOther.ModelPath(s, inv.Arg(0)));
                var model = ModelPath(s, inv.Arg(0));
                var svc = s.ModelPackages!;
                var data = svc.ReadModel(model) ?? throw new CliError(ExitCode.NotFound, $"{model} is not in the game archives.");
                var m = M2Model.Load(data);
                var subject = svc.SubjectOf(model);
                var (looks, _) = svc.Looks(model, subject);
                var (summary, count, warning) = svc.Users(model, subject);
                var errors = M2Validator.Errors(M2Validator.Validate(m));
                bool edited = s.Workspace!.IsModified(model);
                var (fxSummary, fxApprox, fxLeftOut) = M2ClassicEffects.Describe(m);
                var r = new CliResult().With(new
                {
                    model, subject, edited, stats = Stats(m),
                    animations = m.Sequences.Select(q => new { name = svc.Resolver.AnimationName(q.AnimId), variant = q.SubId, seconds = q.Duration / 1000.0 }),
                    effects = new { particles = m.ParticleCount, ribbons = m.RibbonCount, drawnParticles = m.Effects?.Particles.Count ?? 0, drawnRibbons = m.Effects?.Ribbons.Count ?? 0, approximated = fxApprox, leftOut = fxLeftOut },
                    looks = looks.Select(l => new { name = l.Name, kind = l.Kind }), users = summary, userCount = count, warning, validatorErrors = errors.Select(e => e.ToString()),
                });
                r.Line($"{model}{(edited ? "  [Modified: your workspace version]" : "")}");
                StatsLines(r, "statistics", m);
                r.Line($"  used by: {summary}");
                if (warning != null) r.Line("  " + warning);
                r.Line($"  looks ({looks.Count}):");
                foreach (var l in looks.Take(30)) r.Line("    " + l.Name);
                if (looks.Count > 30) r.Line($"    ... and {looks.Count - 30} more");
                r.Line("  animations: " + string.Join(", ", m.Sequences.Take(25).Select(q => $"{svc.Resolver.AnimationName(q.AnimId)}{(q.SubId > 0 ? $" ({q.SubId + 1})" : "")} {q.Duration / 1000.0:0.##} s")) + (m.Sequences.Length > 25 ? ", ..." : ""));
                if (fxSummary.Length > 0)
                {
                    r.Line($"  effects: {fxSummary}");
                    if (fxApprox.Count > 0) r.Line("    drawn approximately: " + string.Join("; ", fxApprox));
                    foreach (var n in fxLeftOut) r.Line("    left out: " + n);
                }
                r.Line(errors.Count == 0 ? "  the model validator finds no errors" : $"  the model validator finds {errors.Count} error(s): " + string.Join("; ", errors.Take(5)));
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models export",
            Summary = "\"Export for Blender...\": a package folder with the glTF, the textures as PNG and wowdata_manifest.json",
            Args = new[] { "<model>" },
            Options = new[]
            {
                new OptionSpec("out", "folder", "where the package folder goes (default: <exports>\\Models; a new <Model>_export folder, never overwritten)"),
                new OptionSpec("mode", "current|all|per-skin", "current: the current look (default); all: every skin in one file (glTF variants); per-skin: one file per skin"),
                new OptionSpec("display", "id", "the creature display that is the current look (default: the entry's, or the first)"),
                new OptionSpec("no-animations", null, "leave the animations out (faster to open when you only paint textures)"),
                new OptionSpec("with-skeleton", null, "\"Skeleton and animations (for editing)\": bones, every animation as a named action, events as markers, attachment points and events as empties, the particles and ribbons as JSON"),
                new OptionSpec("open", null, "start Blender with it afterwards (\"Open in Blender afterwards\")"),
            },
            Examples = new[] { "wds models export Creature\\Boar\\Boar.m2 --out exports", "wds models export \"Mottled Boar\" --mode all --no-animations --json", "wds models export Creature\\Boar\\Boar.m2 --with-skeleton" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return ModelCommandsOther.Export(inv, s, inv.Flag("open"));
                var model = ModelPath(s, inv.Arg(0));
                var svc = s.ModelPackages!;
                var mode = (inv.Opt("mode") ?? "current").ToLowerInvariant() switch
                {
                    "current" => ModelExportMode.CurrentLook, "all" => ModelExportMode.AllVariants, "per-skin" => ModelExportMode.OnePerVariant,
                    var x => throw new CliError(ExitCode.Usage, $"--mode is current, all or per-skin (not {x})."),
                };
                var dir = inv.Opt("out") is { } o ? Path.GetFullPath(o) : Path.Combine(s.Settings.DefaultExportDir, "Models");
                Directory.CreateDirectory(dir);
                long? display = inv.IntOpt("display") ?? DisplayOf(s, inv.Arg(0));
                inv.Ctx.Progress($"Exporting {model} for Blender...");
                bool skel = inv.Flag("with-skeleton");
                if (skel && inv.Flag("no-animations")) throw new CliError(ExitCode.Usage, "--with-skeleton needs the animations: leave out --no-animations.");
                var subject = svc.SubjectOf(model);
                var res = svc.Export(model, subject, mode, dir, display, includeAnimations: !inv.Flag("no-animations"), packageFolder: ModelPackageService.NewPackageFolder(dir, model), includeSkeleton: skel);
                var rig = res.Manifest.Rig;
                var r = new CliResult().With(new
                {
                    model, folder = res.Folder, gltf = res.GltfFiles, manifest = res.ManifestPath, textures = res.TextureCount, notes = res.Notes,
                    skeleton = rig == null ? null : new { bones = rig.Bones.Count, animations = rig.Sequences.Count, globalSequences = rig.GlobalSequences.Count, attachments = rig.Attachments.Count, events = rig.Events.Count, effects = rig.EffectsFile },
                });
                r.Line($"Exported {model} -> {res.Folder}: {Common.Plural(res.GltfFiles.Count, "glTF file")}, {Common.Plural(res.TextureCount, "texture")} and wowdata_manifest.json.");
                if (rig != null)
                {
                    r.Line($"  skeleton and animations for editing: {Common.Plural(rig.Bones.Count, "bone")}, {Common.Plural(rig.Sequences.Count, "animation")} (actions named Stand, Walk, \"Stand [2]\"...), {Common.Plural(rig.GlobalSequences.Count, "global sequence")}, {Common.Plural(rig.Attachments.Count, "attachment point")}, {Common.Plural(rig.Events.Count, "event")}" +
                           (rig.EffectsFile != null ? $"; particles and ribbons in {rig.EffectsFile} (advanced)" : ""));
                    if (subject == ModelSubject.Character) r.Warnings.Add("This is a character model: every player and NPC of its race and sex uses this skeleton and its animations. Keep its key bones and attachment points, and test any change in game.");
                }
                foreach (var n in res.Notes) r.Line("  note: " + n);
                r.Line("In Blender: File > Import > WoW 1.12 model; edit; File > Export > WoW 1.12 model for WoW Data Studio. Then \"wds models import <folder>\".");
                if (inv.Flag("open")) OpenBlender(r, res.GltfFiles[0]);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models open",
            Summary = "\"Open in Blender\": export the current look (with the animations) and start Blender with it",
            Args = new[] { "<model>" },
            Options = new[] { new OptionSpec("display", "id", "the creature display to show"), new OptionSpec("out", "folder", "where the package folder goes (default: <exports>\\Models)") },
            Examples = new[] { "wds models open \"Mottled Boar\"", "wds models open World\\Kalimdor\\Durotar\\PassiveDoodads\\Trees\\DurotarPalm01.m2" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var model = ModelPath(s, inv.Arg(0));
                if (BlenderTools.FindBlender() == null) return CliResult.Fail(ExitCode.NotFound, "Blender was not found: install Blender 4.5 LTS (blender.org or the Microsoft Store), or use \"wds models export\" and open the file later.");
                var svc = s.ModelPackages!;
                var dir = inv.Opt("out") is { } o ? Path.GetFullPath(o) : Path.Combine(s.Settings.DefaultExportDir, "Models");
                Directory.CreateDirectory(dir);
                var res = svc.Export(model, svc.SubjectOf(model), ModelExportMode.CurrentLook, dir, inv.IntOpt("display") ?? DisplayOf(s, inv.Arg(0)), packageFolder: ModelPackageService.NewPackageFolder(dir, model));
                var r = new CliResult().With(new { model, folder = res.Folder, gltf = res.GltfFiles });
                OpenBlender(r, res.GltfFiles[0]);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models import",
            Summary = "\"Import edited model...\": review what came back from Blender (mesh and textures, every check) and save it as one import",
            Args = new[] { "<package>" },
            Options = new[]
            {
                new OptionSpec("exclude", "row", "leave out the rows whose title contains this text (\"mesh\", a texture's file name)", true),
                new OptionSpec("keep-size", null, "textures of another power-of-two size keep their size (default: resize to the original)"),
                new OptionSpec("format", "same|dxt3|dxt5", "textures that gained transparency: keep the original format (default) or save as DXT3 / DXT5"),
                Common.DryRun,
            },
            Details = "The package is the folder \"wds models export\" made (the Blender export in its blender_export folder, or edited PNGs) or a .gltf / .glb file. " +
                      "With a package exported --with-skeleton, the skeleton and animations (bones, animations, attachment points, events) are one row and the advanced particles and ribbons file another; " +
                      "the mesh, skeleton and effects rows are written into the model together. --exclude skeleton / mesh / particles leaves one out. " +
                      "--dry-run shows the review and saves nothing. Rows that cannot be imported are listed with the reason (exit code 3 when nothing can be saved because of them).",
            Examples = new[] { "wds models import exports\\Boar_export --dry-run", "wds models import exports\\Boar_export", "wds models import exports\\Boar_export --exclude mesh --json", "wds models import exports\\Boar_export --exclude skeleton" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Path.GetFullPath(inv.Arg(0));
                if (!File.Exists(path) && !Directory.Exists(path)) throw new CliError(ExitCode.NotFound, $"{path} does not exist.");
                var svc = s.ModelPackages!;
                inv.Ctx.Progress("Reading your edited model...");
                var a = ModelImportAnalyzer.Analyze(path, svc.ImportContext());
                var format = (inv.Opt("format") ?? "same").ToLowerInvariant() switch
                {
                    "same" => ImportFormatChoice.SameAsOriginal, "dxt5" => ImportFormatChoice.Dxt5KeepAlpha, "dxt3" => ImportFormatChoice.Dxt3KeepAlpha,
                    var x => throw new CliError(ExitCode.Usage, $"--format is same, dxt3 or dxt5 (not {x})."),
                };
                foreach (var c in a.Changes)
                {
                    if (inv.Opts("exclude").Any(x => c.Title.Contains(x, StringComparison.OrdinalIgnoreCase))) c.Include = false;
                    if (c.Kind != ModelChangeKind.Texture || c.EditedImage == null || c.Unchanged) continue;
                    bool replan = false;
                    if (inv.Flag("keep-size") && c.NeedsResize) { c.ResizeTo = (c.EditedImage.Width, c.EditedImage.Height); replan = true; }
                    if (format != ImportFormatChoice.SameAsOriginal && c.AlphaQuestion) { c.Format = format; replan = true; }
                    if (replan) ModelImportAnalyzer.PlanTexture(c, a.Context);
                }
                ModelImportAnalyzer.Compose(a);    // the model rows (mesh, skeleton, effects) are one file
                string Status(ModelChange c) => c.Unchanged && !c.HasBlocker ? "Unchanged" : c.HasBlocker ? "Cannot be imported" : c.Include && c.CanInclude ? "Will be saved" : "Left out";
                var r = new CliResult();
                r.Line($"Import edited model: {a.ModelPath}" + (inv.DryRun ? " (dry run)" : ""));
                r.Line($"From {a.Edited.Description} ({a.Edited.GltfPath}).");
                foreach (var g in a.General) r.Line($"  {g.Level}: {g.Text}");
                if (!string.IsNullOrEmpty(a.UsersWarning) && a.Changes.Any(c => c.Kind == ModelChangeKind.Mesh && !c.Unchanged)) r.Warnings.Add(a.UsersWarning);
                foreach (var c in a.Changes)
                {
                    r.Line($"  [{Status(c)}] {c.Title}" + (c.Detail.Length > 0 ? ": " + c.Detail : ""));
                    if (!c.Unchanged) foreach (var line in c.Lines) r.Line($"      - {line}");
                    foreach (var rule in c.Rules) r.Line($"      {rule.Level}: {rule.Text}");
                }
                ChangeSet? cs = null;
                int code = ExitCode.Ok;
                if (a.NothingChanged) r.Line("No changes found: nothing will be written.");
                else if (!a.AnythingToImport)
                {
                    r.Line("Nothing is selected that can be imported.");
                    if (a.Changes.Any(c => c.HasBlocker)) code = ExitCode.ValidationFailed;
                }
                else if (!inv.DryRun)
                {
                    cs = ModelImportCommitter.Commit(a, s.Workspace!, s.Index);
                    r.Line($"Saved {Common.Plural(cs.Files.Count, "file")} as one import (\"{cs.Label}\", change set {cs.Id}). \"wds changes revert-set {cs.Id}\" undoes it.");
                }
                r.Code = code;
                if (code != ExitCode.Ok) r.Error = "The edited model has changes that cannot be imported (see the rows above).";
                r.With(new
                {
                    model = a.ModelPath, source = a.Edited.GltfPath, nothingChanged = a.NothingChanged, general = a.General.Select(g => new { level = g.Level, text = g.Text }),
                    rows = a.Changes.Select(c => new { kind = c.Kind, title = c.Title, detail = c.Detail, path = c.GamePath, status = Status(c), unchanged = c.Unchanged, included = c.Include && c.CanInclude && !c.Unchanged, changes = c.Lines, rules = c.Rules.Select(x => new { level = x.Level, text = x.Text }) }),
                    saved = cs == null ? null : new { changeSet = cs.Id, label = cs.Label, files = cs.Files },
                });
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models validate",
            Summary = "run the model validator (the check every model import must pass) on a model of the game, your edit or an .m2 file",
            Args = new[] { "<model>" },
            Options = new[] { new OptionSpec("original", null, "the game's model even when you edited it") },
            Examples = new[] { "wds models validate Creature\\Boar\\Boar.m2", "wds models validate D:\\work\\Boar.m2 --json" },
            Run = async inv =>
            {
                byte[] data;
                string what;
                var arg = inv.Arg(0);
                if (File.Exists(arg))   // a model file on disk
                {
                    data = await File.ReadAllBytesAsync(arg);
                    what = Path.GetFullPath(arg);
                }
                else
                {
                    var s = await inv.Ctx.SessionAsync();
                    what = ModelPath(s, arg);
                    data = (!inv.Flag("original") ? s.Workspace!.ReadEdited(what) : null) ?? s.Data!.Read(what) ?? throw new CliError(ExitCode.NotFound, $"{what} is not in the game archives.");
                }
                List<M2Issue> issues;
                try { issues = M2Validator.Validate(M2Model.Load(data)); }
                catch (M2Exception e) { return CliResult.Fail(ExitCode.ValidationFailed, $"{what} cannot be read as a 1.12 model: {e.Message}"); }
                var errors = M2Validator.Errors(issues);
                var r = new CliResult { Code = errors.Count == 0 ? ExitCode.Ok : ExitCode.ValidationFailed }.With(new { model = what, ok = errors.Count == 0, errors = errors.Count, issues = issues.Select(i => new { level = i.Level, where = i.Where, message = i.Message }) });
                r.Line(errors.Count == 0 ? $"{what}: the model validator finds no errors ({Common.Plural(issues.Count, "note")})." : $"{what}: {Common.Plural(errors.Count, "error")}.");
                foreach (var i in issues.Take(60)) r.Line($"  {i.Level}: {i.Where}: {i.Message}");
                if (errors.Count > 0) r.Error = "The model would not load correctly in the game.";
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models compare",
            Summary = "\"Compare original vs. edited model\": the game's model and your edit side by side (numbers; --images renders both)",
            Args = new[] { "<model>" },
            Options = new[]
            {
                new OptionSpec("images", "prefix", "also render both to <prefix>_original.png and <prefix>_edited.png (the app's renderer)"),
                new OptionSpec("sequence", "name|n", "with --images: the animation to draw on both (its name, e.g. Walk)"),
                new OptionSpec("time", "ms", "with --images: the moment of the animation"),
            },
            Examples = new[] { "wds models compare Creature\\Boar\\Boar.m2", "wds models compare Creature\\Boar\\Boar.m2 --images boar --json", "wds models compare Creature\\Boar\\Boar.m2 --images boar --sequence Walk --time 400" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var model = ModelPath(s, inv.Arg(0));
                var edited = s.Workspace!.ReadEdited(model) ?? throw new CliError(ExitCode.NotFound, $"{model} has not been changed: nothing to compare.");
                var orig = s.Data!.Read(model) ?? throw new CliError(ExitCode.NotFound, $"The game has no {model} to compare with.");
                var a = M2Model.Load(orig);
                var b = M2Model.Load(edited);
                var r = new CliResult();
                r.Line($"Compare original vs. edited: {model}");
                StatsLines(r, "original (from game)", a);
                StatsLines(r, "your edit          ", b);
                var parts = new List<object>();
                if (a.Views.Length > 0 && b.Views.Length > 0)
                    foreach (var g in a.Views[0].Submeshes.Select(x => x.GeosetId).Union(b.Views[0].Submeshes.Select(x => x.GeosetId)).Distinct().OrderBy(x => x))
                    {
                        int va = a.Views[0].Submeshes.Where(x => x.GeosetId == g).Sum(x => x.VertexCount), vb = b.Views[0].Submeshes.Where(x => x.GeosetId == g).Sum(x => x.VertexCount);
                        int ta = a.Views[0].Submeshes.Where(x => x.GeosetId == g).Sum(x => x.IndexCount) / 3, tb = b.Views[0].Submeshes.Where(x => x.GeosetId == g).Sum(x => x.IndexCount) / 3;
                        parts.Add(new { geoset = g, verticesBefore = va, verticesAfter = vb, trianglesBefore = ta, trianglesAfter = tb });
                        if (va != vb || ta != tb) r.Line($"  geoset {g}: {va:N0} -> {vb:N0} vertices, {ta:N0} -> {tb:N0} triangles");
                    }
                var images = new List<string>();
                if (inv.Opt("images") is { } prefix)
                {
                    var outp = new List<string>();
                    foreach (var (suffix, original) in new[] { ("original", true), ("edited", false) })
                    {
                        var png = Path.GetFullPath($"{prefix}_{suffix}.png");
                        var args = new List<string> { "--render", model, png, "--home", inv.Ctx.Paths.Root, "--client", s.Client!.ClientDir };
                        if (original) args.Add("--original");
                        foreach (var o in new[] { "sequence", "time" })
                            if (inv.Opt(o) is { } v) { args.Add("--" + o); args.Add(v); }
                        if (await RunApp(inv.Ctx, args, outp) != 0) throw new CliError(ExitCode.Error, "The renderer failed: " + string.Join(" ", outp.TakeLast(3)));
                        images.Add(png);
                        r.Line($"  rendered {suffix}: {png}");
                    }
                }
                // 0.10.0: animations and skeleton
                var an = ModelExporter.ActionNames(a, s.Models!.AnimationName);
                var bn = ModelExporter.ActionNames(b, s.Models!.AnimationName);
                var added = bn.Except(an, StringComparer.OrdinalIgnoreCase).ToList();
                var removed = an.Except(bn, StringComparer.OrdinalIgnoreCase).ToList();
                var retimed = an.Select((n, i) => (n, i)).Where(x => Array.FindIndex(bn, y => string.Equals(y, x.n, StringComparison.OrdinalIgnoreCase)) is int j && j >= 0 && b.Sequences[j].Duration != a.Sequences[x.i].Duration)
                    .Select(x => $"{x.n} {a.Sequences[x.i].Duration} -> {b.Sequences[Array.FindIndex(bn, y => string.Equals(y, x.n, StringComparison.OrdinalIgnoreCase))].Duration} ms").ToList();
                if (a.Bones.Length != b.Bones.Length) r.Line($"  bones: {a.Bones.Length} -> {b.Bones.Length}");
                if (added.Count > 0) r.Line("  new animations: " + string.Join(", ", added));
                if (removed.Count > 0) r.Line("  removed animations: " + string.Join(", ", removed));
                if (retimed.Count > 0) r.Line("  retimed: " + string.Join("; ", retimed));
                if (a.Attachments.Length != b.Attachments.Length) r.Line($"  attachment points: {a.Attachments.Length} -> {b.Attachments.Length}");
                return r.With(new { model, original = Stats(a), edited = Stats(b), geosets = parts, images, animations = new { added, removed, retimed } });
            },
        };
        yield return new CommandSpec
        {
            Path = "models addon",
            Summary = "\"Install the Blender add-on...\": write the add-on zip; --install installs it into Blender 4.5 (needs --yes)",
            Options = new[]
            {
                new OptionSpec("zip", "file", "where to write the zip (default: Documents\\WoW Data Studio\\Blender\\wowdata_blender.zip)"),
                new OptionSpec("install", null, "install and enable it in your Blender (runs Blender in the background; changes Blender's preferences: needs --yes)"),
            },
            NeedsClient = false,
            Examples = new[] { "wds models addon", "wds models addon --zip addon.zip --json", "wds models addon --install --yes" },
            Run = inv =>
            {
                var root = BlenderTools.AddonRoot() ?? throw new CliError(ExitCode.NotFound, "The Blender add-on (the blender_addon folder next to wds.exe) was not found. Repair the installation.");
                var zipPath = inv.Opt("zip") is { } z ? Path.GetFullPath(z) : BlenderTools.DefaultZipPath(inv.Ctx.Settings.DefaultExportDir);
                var zip = BlenderTools.BuildZip(root, zipPath);
                var version = BlenderTools.AddonVersion(root);
                var blender = BlenderTools.FindBlender();
                var r = new CliResult().Line($"The add-on \"WoW 1.12 model (wowdata)\" version {version}: {zip}");
                if (!inv.Flag("install"))
                {
                    r.Line(BlenderTools.ManualInstallText).Line(blender == null ? "Blender was not found on this computer." : $"Or: wds models addon --install --yes (Blender: {blender}).");
                    return Task.FromResult(r.With(new { zip, version, blender, installed = false }));
                }
                if (blender == null) return Task.FromResult(new CliResult { Code = ExitCode.NotFound, Error = "Blender was not found: install Blender 4.5 LTS first (blender.org or the Microsoft Store). " + BlenderTools.ManualInstallText }.With(new { zip, version, blender, installed = false }));
                if (!inv.Yes) return Task.FromResult(Common.NeedsYes(r.With(new { zip, version, blender, installed = false }), $"This installs the add-on into your Blender ({blender}) and saves Blender's preferences (into {BlenderTools.UserAddonsDir()}). Close Blender first."));
                inv.Ctx.Progress("Installing the add-on into Blender...");
                var res = BlenderTools.Install(blender, root, zip, Path.Combine(inv.Ctx.Paths.Root, "temp"));
                r.Code = res.Ok ? ExitCode.Ok : ExitCode.Error;
                if (res.Ok) r.Line(res.Message).Line("In Blender: File > Import > WoW 1.12 model, and File > Export > WoW 1.12 model for WoW Data Studio.");
                else r.Error = "The add-on was not installed: " + res.Message + "\n" + BlenderTools.ManualInstallText;
                return Task.FromResult(r.With(new { zip, version, blender, installed = res.Ok, addonFile = res.AddonFile }));
            },
        };
        yield return new CommandSpec
        {
            Path = "models anims",
            Summary = "a model's animations: names, variations, loop, chance, timing, blend, move speed, events and what the game plays them for (emotes, spells, combat, mounts)",
            Args = new[] { "<model>" },
            Options = new[]
            {
                new OptionSpec("sequence", "name|n", "only this animation, with its events and every use"),
                new OptionSpec("original", null, "the game's model even when you edited it"),
            },
            Details = "The names are the actions of \"wds models export --with-skeleton\" (\"Stand [2]\" is the second Stand variation). " +
                      "\"Used for\" comes from the client's own animation tests; emotes from Emotes / EmotesText, spells from Spell -> SpellVisual -> SpellVisualKit. " +
                      "With --version: where each animation's keys are (the model, its skeleton file, or an .anim file by FileDataID) and, for the ones in .anim files, whether they load and play.",
            Examples = new[] { "wds models anims Creature\\Boar\\Boar.m2", "wds models anims \"Mottled Boar\" --json", "wds models anims Character\\Orc\\Male\\OrcMale.m2 --sequence EmoteDance", "wds models anims Creature\\Boar\\Boar.m2 --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return ModelCommandsOther.Anims(s, ModelCommandsOther.ModelPath(s, inv.Arg(0)), inv.Opt("sequence"));
                var model = ModelPath(s, inv.Arg(0));
                var data = (!inv.Flag("original") ? s.Workspace!.ReadEdited(model) : null) ?? s.Data!.Read(model) ?? throw new CliError(ExitCode.NotFound, $"{model} is not in the game archives.");
                var m = M2Model.Load(data);
                var au = s.AnimationUsage!;
                var info = RigInfo.Read(m, au.Animations);
                var names = ModelExporter.ActionNames(m, s.Models!.AnimationName);
                var seqs = info.Sequences.AsEnumerable();
                if (inv.Opt("sequence") is { } want)
                {
                    int idx = int.TryParse(want, out int n) ? n : Array.FindIndex(names, x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0 || idx >= info.Sequences.Count) throw new CliError(ExitCode.NotFound, $"{model} has no animation \"{want}\". It has: {string.Join(", ", names.Take(40))}{(names.Length > 40 ? ", ..." : "")}.");
                    seqs = new[] { info.Sequences[idx] };
                }
                var list = seqs.Select(q =>
                {
                    var u = au.Of(q.AnimId);
                    var ev = info.Events.SelectMany(e => e.Triggers.Where(t => t.Sequence == q.Index).Select(t => new { code = e.Code, meaning = e.Meaning, ms = t.Time })).OrderBy(x => x.ms).ToList();
                    return new
                    {
                        index = q.Index, name = names[q.Index], animId = q.AnimId, animName = q.AnimName, variation = q.Variation + 1, variations = q.Variations, subId = q.SubId,
                        durationMs = q.Duration, loops = q.Loops, flags = q.Flags, chancePercent = Math.Round(q.ChancePercent, 2), blendMs = q.BlendTime, moveSpeed = q.MoveSpeed,
                        repeatMin = q.ReplayMin, repeatMax = q.ReplayMax, start = q.Start, end = q.End, usedFor = u.Roles, emotes = u.Emotes,
                        spells = u.Spells.Select(x => new { spell = x.Spell, when = x.When }), fallback = u.Fallback, events = ev,
                    };
                }).ToList();
                var r = new CliResult().With(new
                {
                    model, animations = list, globalSequences = info.GlobalSequences,
                    missing = new[] { 0, 1, 4, 5, 16, 26 }.Where(id => !info.Sequences.Any(x => x.AnimId == id) && au.Animations.Exists(id)).Select(id => new { animId = id, name = au.Animations.Name(id), fallback = au.Animations.FallbackText(id) }),
                });
                r.Line($"{model}: {Common.Plural(info.Sequences.Count, "animation")}" + (info.GlobalSequences.Count > 0 ? $", {Common.Plural(info.GlobalSequences.Count, "global sequence")} ({string.Join(", ", info.GlobalSequences.Select(x => $"{x} ms"))})" : ""));
                foreach (var l in Common.Table(new[] { "#", "Name", "Id", "Length", "Plays", "Chance", "Blend", "Speed", "Events", "Used for" },
                             list.Select(x => (IReadOnlyList<string>)new[]
                             {
                                 x.index.ToString(), x.name, x.animId.ToString(), $"{x.durationMs / 1000.0:0.###} s", x.loops ? "loops" : "once",
                                 x.variations > 1 ? $"{x.chancePercent:0.#} %" : "", $"{x.blendMs} ms", x.moveSpeed != 0 ? $"{x.moveSpeed:0.##}" : "", x.events.Count.ToString(),
                                 string.Join("; ", x.usedFor.Select(u => u.Split('(')[0].Trim()).Concat(x.emotes.Count > 0 ? new[] { "emotes " + string.Join(" ", x.emotes.Take(4)) } : Array.Empty<string>())
                                     .Concat(x.spells.Any() ? new[] { $"{x.spells.Select(y => y.spell).Distinct().Count()} spells" } : Array.Empty<string>())),
                             }), 70))
                    r.Line("  " + l);
                if (list.Count == 1)
                {
                    var x = list[0];
                    r.Line($"  {x.name}: animation {x.animId} ({x.animName}), variation {x.variation} of {x.variations}, sub id {x.subId}, timeline {x.start}-{x.end} ms, flags 0x{x.flags:x}, repeats {x.repeatMin}-{x.repeatMax}.");
                    foreach (var u in x.usedFor) r.Line("    used for: " + u);
                    if (x.emotes.Count > 0) r.Line("    emotes: " + string.Join(", ", x.emotes));
                    foreach (var sp in x.spells.Take(40)) r.Line($"    spell: {sp.spell} ({sp.when})");
                    if (x.fallback != null) r.Line($"    without it the game plays {x.fallback}");
                    foreach (var e in x.events) r.Line($"    event {e.code} at {e.ms / 1000.0:0.###} s: {e.meaning}");
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models skeleton",
            Summary = "a model's skeleton: bones (key bones, parents, pivots, weighted vertices), attachment points, events, particles and ribbons, and the client's limits",
            Args = new[] { "<model>" },
            Options = new[] { new OptionSpec("original", null, "the game's model even when you edited it") },
            Details = "The bone names are those of the export (bone_015_Root: bone 15, key bone Root). Key bones are how the game finds the head, the hands, the jaw... by name.",
            Examples = new[] { "wds models skeleton Creature\\Boar\\Boar.m2", "wds models skeleton Character\\Orc\\Male\\OrcMale.m2 --json", "wds models skeleton Creature\\Boar\\Boar.m2 --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                string model;
                M2Model m;
                if (s.IsReadOnly)
                {
                    // M2: another version, read by the modern reader
                    model = ModelCommandsOther.ModelPath(s, inv.Arg(0));
                    m = ModelCommandsOther.Load(s, model);
                }
                else
                {
                    model = ModelPath(s, inv.Arg(0));
                    var data = (!inv.Flag("original") ? s.Workspace!.ReadEdited(model) : null) ?? s.Data!.Read(model) ?? throw new CliError(ExitCode.NotFound, $"{model} is not in the game archives.");
                    m = M2Model.Load(data);
                }
                var info = RigInfo.Read(m, s.Looks!.Animations);
                int maxBone = m.Views.Length == 0 ? 0 : m.Views.SelectMany(v => v.Submeshes).DefaultIfEmpty().Max(x => x?.BoneCount ?? 0);
                var r = new CliResult().With(new
                {
                    model,
                    bones = info.Bones.Select(b => new { index = b.Index, name = b.Name, keyBone = b.KeyBoneId, keyBoneName = b.KeyBoneName, parent = b.Parent, pivot = new[] { b.Pivot.X, b.Pivot.Y, b.Pivot.Z }, flags = b.Flags, vertices = b.Vertices, animated = b.Animated, globalChannels = b.GlobalChannels }),
                    attachments = info.Attachments.Select(a => new { index = a.Index, id = a.Id, name = a.Name, bone = a.Bone, position = new[] { a.Position.X, a.Position.Y, a.Position.Z } }),
                    events = info.Events.Select(e => new { index = e.Index, code = e.Code, meaning = e.Meaning, bone = e.Bone, triggers = e.Triggers.Count }),
                    particles = info.Particles, ribbons = info.Ribbons,
                    limits = new { bones = info.Bones.Count, vertexBoneLimit = 256, viewBoneLimits = m.Views.Select(v => (int)v.BoneLimit), largestPalette = maxBone, keyBones = M2Schema.KeyBones.Length },
                });
                r.Line($"{model}: {Common.Plural(info.Bones.Count, "bone")} ({info.Bones.Count(b => b.Animated)} animated, {info.Bones.Count(b => b.Vertices > 0)} move vertices), {Common.Plural(info.Attachments.Count, "attachment point")}, {Common.Plural(info.Events.Count, "event")}, {info.Particles} particle emitter(s), {info.Ribbons} ribbon(s).");
                r.Line($"  limits: vertices can use bones 0-255; the views draw {string.Join(" / ", m.Views.Select(v => v.BoneLimit))} bones at a time (largest part now: {maxBone}); key bones 0-{M2Schema.KeyBones.Length - 1}.");
                foreach (var l in Common.Table(new[] { "#", "Name", "Key bone", "Parent", "Vertices", "Animated" },
                             info.Bones.Select(b => (IReadOnlyList<string>)new[] { b.Index.ToString(), b.Name, b.KeyBoneId >= 0 ? $"{b.KeyBoneName} ({b.KeyBoneId})" : "", b.Parent.ToString(), b.Vertices.ToString(), b.Animated ? (b.GlobalChannels.Count > 0 ? "yes (global)" : "yes") : "" })))
                    r.Line("  " + l);
                if (info.Attachments.Count > 0) r.Line("  attachment points: " + string.Join(", ", info.Attachments.Select(a => $"{a.Name} ({a.Id}) on bone {a.Bone}")));
                if (info.Events.Count > 0) r.Line("  events: " + string.Join(", ", info.Events.Select(e => $"{e.Code} on bone {e.Bone}")));
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "models render",
            Summary = "draw a model to a PNG with the app's 3D renderer (your workspace version, or --original), like \"Save image...\"",
            Args = new[] { "<model>", "<png>" },
            Options = new[]
            {
                new OptionSpec("display", "id", "a creature display's skins"),
                new OptionSpec("sequence", "name|n", "the animation: its name (Walk, \"Stand [2]\", see wds models anims) or its number"),
                new OptionSpec("time", "ms", "the moment of the animation (ms from its start)"),
                new OptionSpec("yaw", "degrees", "turn the camera"),
                new OptionSpec("size", "WxH", "the picture size (default 900x700)"),
                new OptionSpec("original", null, "the game's files only, not your edits"),
                new OptionSpec("no-particles", null, "leave the particles and ribbons out (like switching off Particles in the 3D view)"),
            },
            Details = "Runs WoWDataStudio.exe --render (installed next to wds.exe): the 3D renderer is part of the app. It needs a graphics driver with OpenGL 3.3. The particles and ribbons are drawn (after two seconds of their simulation, so a still picture shows them running) unless --no-particles. With --version: another version's model with its display's skin or item texture (--original does not apply).",
            Examples = new[] { "wds models render Creature\\Boar\\Boar.m2 boar.png --display 503", "wds models render \"Mottled Boar\" boar.png --size 1920x1080 --original", "wds models render Creature\\Boar\\Boar.m2 walk.png --sequence Walk --time 500", "wds models render cdisplay:503 boar160.png --version _classic_beta_" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return await RenderOther(inv, s);
                var model = ModelPath(s, inv.Arg(0));
                var png = Path.GetFullPath(inv.Arg(1));
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);   // M2: the renderer does not create it (it waited forever)
                var args = new List<string> { "--render", model, png, "--home", inv.Ctx.Paths.Root, "--client", s.Client!.ClientDir };
                if ((inv.IntOpt("display") ?? DisplayOf(s, inv.Arg(0))) is { } d) { args.Add("--display"); args.Add(d.ToString()); }
                foreach (var o in new[] { "sequence", "time", "yaw", "size" })
                    if (inv.Opt(o) is { } v) { args.Add("--" + o); args.Add(v); }
                if (inv.Flag("original")) args.Add("--original");
                if (inv.Flag("no-particles")) args.Add("--no-particles");
                s.Dispose();   // the app opens the archives itself
                var output = new List<string>();
                int code = await RunApp(inv.Ctx, args, output);
                if (code != 0 || !File.Exists(png))
                    return new CliResult { Code = ExitCode.Error, Error = "The renderer failed: " + string.Join(" | ", output.TakeLast(4)) }.With(new { model, png, output });
                return new CliResult().With(new { model, png, output }).Line(output.FirstOrDefault() ?? png);
            },
        };
    }

    /// <summary>M2: "wds models render --version": the app's renderer on another version's model (by FileDataID), its
    /// creature or item display's look; the animation is resolved here (by name or number) and passed by number.</summary>
    private static async Task<CliResult> RenderOther(Invocation inv, AppSession s)
    {
        var model = ModelCommandsOther.ModelPath(s, inv.Arg(0));
        var m = ModelCommandsOther.Load(s, model);
        var png = Path.GetFullPath(inv.Arg(1));
        var copy = s.Profile!.Copy!;
        if (png.StartsWith(Path.GetFullPath(copy.Folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new CliError(ExitCode.Refused, $"Refusing to write into the folder of {copy.Name}: other versions are only read. Save the picture somewhere else.");
        var id = s.Files!.IdOf(model) ?? throw new CliError(ExitCode.NotFound, $"{model} is not a file of {s.Profile.VersionBuild}.");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        var args = new List<string> { "--render", "#" + id, png, "--home", inv.Ctx.Paths.Root, "--version", copy.Folder };
        if ((inv.IntOpt("display") ?? ModelCommandsOther.DisplayOf(s, inv.Arg(0))) is { } d) { args.Add("--display"); args.Add(d.ToString()); }
        else if (ModelCommandsOther.ItemDisplayOf(s, inv.Arg(0)) is { } item) { args.Add("--item-display"); args.Add(item.ToString()); }
        if (inv.Opt("sequence") is { } want)
        {
            var names = ModelExporter.ActionNames(m, s.Looks!.AnimationName);
            int idx = int.TryParse(want, out int n) ? n : Array.FindIndex(names, x => string.Equals(x, want, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx >= m.Sequences.Length) throw new CliError(ExitCode.NotFound, $"{model} has no animation \"{want}\". It has: {string.Join(", ", names.Take(30))}{(names.Length > 30 ? ", ..." : "")}.");
            args.Add("--sequence");
            args.Add(idx.ToString());
        }
        foreach (var o in new[] { "time", "yaw", "size" })
            if (inv.Opt(o) is { } v) { args.Add("--" + o); args.Add(v); }
        if (inv.Flag("no-particles")) args.Add("--no-particles");
        s.Dispose();   // the app opens the client itself
        var output = new List<string>();
        int code = await RunApp(inv.Ctx, args, output);
        if (code != 0 || !File.Exists(png))
            return new CliResult { Code = ExitCode.Error, Error = "The renderer failed: " + string.Join(" | ", output.TakeLast(4)) }.With(new { model, png, output, readOnly = true });
        return new CliResult().With(new { model, png, output, readOnly = true }).Line(output.FirstOrDefault() ?? png);
    }

    internal static void OpenBlender(CliResult r, string gltf)
    {
        var blender = BlenderTools.FindBlender();
        var root = BlenderTools.AddonRoot();
        if (blender == null) { r.Warnings.Add("Blender was not found: install Blender 4.5 LTS, then open the .gltf with File > Import > WoW 1.12 model."); return; }
        if (root == null) { r.Warnings.Add("The app's copy of the Blender add-on (blender_addon next to wds.exe) was not found. Repair the installation."); return; }
        BlenderTools.OpenInBlender(blender, root, gltf);
        r.Line("Blender is starting with it. When you are done: File > Export > WoW 1.12 model for WoW Data Studio, then \"wds models import\".");
    }
}
