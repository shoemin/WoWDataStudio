using WoWData.Core.Dbc;
using WoWData.Core.HotReload;
using WoWData.Core.Maps;
using WoWData.Core.Session;
using WoWData.Core.Workspace;

namespace WoWData.Cli.Commands;

/// <summary>changes: the My changes tab, testing in the game (loose files) and releasing (patch-3.MPQ).</summary>
internal static class ChangeCommands
{
    private static readonly OptionSpec Force = new("force", null, "replace a file in the game folder that the app did not put there (it is backed up first and put back on remove)");

    private static string Resolve(AppSession s, string text)
    {
        var p = text.Replace('/', '\\');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        if (DbcService.TableOf(p) == null && !p.Contains('\\') && s.Dbc?.Layouts.Get(p) != null) p = DbcService.PathOf(p);   // a table name
        if (s.Workspace!.IsModified(p)) return s.Workspace.Get(p)!.Path;
        throw new CliError(ExitCode.NotFound, $"{text} is not in your workspace (\"wds changes list\" lists your changes).");
    }

    /// <summary>"Test in game" for some files (the action bar, My changes, the Maps tab, Data tables).</summary>
    public static CliResult Install(Invocation inv, AppSession s, List<string>? paths, string? note = null)
    {
        var ws = s.Workspace!;
        var r = new CliResult();
        List<DeliveryAction> plan;
        try { plan = ws.PlanLooseInstall(paths, inv.Flag("force")); }
        catch (WorkspaceException e) when (ChangeOperations.IsForeignFileRefusal(e))
        {
            return CliResult.Fail(ExitCode.Refused, e.Message + "\nRun it again with --force to replace it: the existing file is backed up first and restored when you remove your change from the game.");
        }
        r.Line($"Test in game: copy {Common.Plural(plan.Count, "file")} into the game folder ({ws.Paths.DataDir}):");
        foreach (var a in plan) r.Line($"  {a.Action}: {a.Dest}");
        r.Line(ChangeOperations.LooseFilesNote);
        if (note != null) r.Line(note);
        r.With(new { installed = false, files = plan.Select(a => new { path = a.Path, dest = a.Dest, action = a.Action }) });
        if (inv.DryRun) return r;
        if (!inv.Yes) return Common.NeedsYes(r, "This writes into the game folder.");
        var done = ChangeOperations.InstallLoose(ws, paths, inv.Flag("force"));
        var files = done.Select(a => new { path = a.Path, dest = a.Dest, action = a.Action }).ToList();
        if (inv.Flag("reload"))
        {
            r.Line($"Installed: {Common.Plural(done.Count, "file")} copied into the game folder.");
            var outcome = new HotReloadService(ws.Paths.ClientDir).ReloadAsync(done.Select(a => a.Path)).GetAwaiter().GetResult();
            AddReload(r, outcome);
            return r.With(new { installed = true, files, reload = ReloadJson(outcome) });
        }
        r.Line($"Installed: {Common.Plural(done.Count, "file")} copied into the game folder. Restart the game to see the change (or reload it in the running game: --reload, \"wds changes reload\").");
        return r.With(new { installed = true, files });
    }

    /// <summary>A path to reload: a change of the workspace (its recorded spelling), else any archive path as given.</summary>
    private static string ReloadPath(Workspace ws, string text)
    {
        var p = text.Replace('/', '\\');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return ws.Get(p)?.Path ?? p;
    }

    public static readonly OptionSpec ReloadOpt = new("reload", null, "then reload the files in the running game (hot reload; the game must run with it switched on)");

    /// <summary>The JSON of a hot reload (wds changes reload and the --reload of the other commands).</summary>
    public static object ReloadJson(HotReloadOutcome o) => new
    {
        done = o.Done, state = o.Probe.State, message = o.Probe.Ready ? o.Text : o.Message, pid = o.Probe.Game?.Pid, requestId = o.RequestId,
        live = o.Report?.Live.Select(f => f.Path), relog = o.Report?.Relog.Select(f => f.Path), restart = o.Report?.Restart.Select(f => f.Path),
        problems = o.Report?.Problems.Select(f => f.Path), report = o.Report,
    };

    /// <summary>The text of a hot reload: the summary, then one line per file.</summary>
    public static void AddReload(CliResult r, HotReloadOutcome o)
    {
        if (!o.Done)
        {
            r.Line("Not reloaded in the running game: " + o.Message);
            return;
        }
        r.Line($"Reloaded in the running game (process {o.Probe.Game!.Pid}, request #{o.RequestId}):");
        if (o.Report == null)
        {
            r.Line("  " + o.Message);
            return;
        }
        foreach (var l in o.Text.Split('\n')) r.Line("  " + l.TrimEnd());
        foreach (var f in o.Report.Files) r.Line($"  {HotReloadService.Label(f.Result),-22} {f.Path}");
    }

    /// <summary>The exit code of a hot reload: 0 done (relog / restart items are information), 6 no game, 4 switched off, 1 otherwise or
    /// when files failed.</summary>
    public static int ReloadCode(HotReloadOutcome o) => o.Done
        ? (o.Report?.Problems.Any() == true ? ExitCode.Error : ExitCode.Ok)
        : o.Probe.State switch { HotReloadState.NoGame => ExitCode.NotFound, HotReloadState.Off => ExitCode.Refused, _ => ExitCode.Error };

    private static object ChangeJson(ChangeView v, Workspace ws)
    {
        var set = ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet);
        return new
        {
            path = v.Record.Path, kind = v.Record.Kind, format = v.Record.Original.Format ?? (v.Record.Original.IsNew ? "new file" : ""), status = v.StatusText,
            edited = v.Record.Edited, changeSet = v.Record.ChangeSet, import = set?.Label, summary = v.Record.Summary,
            installedLoose = v.InstalledLoose, installedCurrent = v.InstalledCurrent, packed = v.Packed, packedCurrent = v.PackedCurrent, workspaceFile = v.WorkspaceFile,
            server = DbcService.TableOf(v.Record.Path) is { } t ? DbcLayouts.Default.Get(t)?.ServerText : null,
        };
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "changes list",
            Summary = "\"My changes\": every edited file with its status (in workspace, installed as loose file, in patch-3.MPQ), format and import",
            Examples = new[] { "wds changes list", "wds changes list --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var list = ws.List();
                var st = ws.GetPatchState();
                var r = new CliResult().With(new { changes = list.Select(v => ChangeJson(v, ws)), patch = new { owner = st.Owner, text = st.Text }, looseInstalled = ws.HasLooseInstalls() });
                if (list.Count == 0) r.Line("No changes yet. Export a texture, edit the PNG and import it again (wds textures export / import): it will be listed here.");
                else
                    foreach (var l in Common.Table(new[] { "File", "Status", "Format", "Edited", "Import" },
                                 list.Select(v => (IReadOnlyList<string>)new[] { v.Record.Path, v.StatusText, v.Record.Original.Format ?? "", v.Record.Edited.ToString("yyyy-MM-dd HH:mm"), ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet)?.Label ?? "" }), 64))
                        r.Line(l);
                r.Line("").Line(st.Text);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "changes sets",
            Summary = "the change sets (one per import: \"Revert this import\" undoes a whole set) with their files",
            Examples = new[] { "wds changes sets", "wds changes sets --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var sets = ws.Manifest.ChangeSets.Values.OrderByDescending(c => c.Created).ToList();
                var r = new CliResult().With(sets.Select(c => new { id = c.Id, label = c.Label, created = c.Created, files = ChangeOperations.FilesOf(ws, c.Id) }));
                if (sets.Count == 0) r.Line("No changes yet.");
                foreach (var c in sets)
                {
                    r.Line($"{c.Id}  {c.Label} ({Common.Plural(c.Files.Count, "file")}, {c.Created:yyyy-MM-dd HH:mm})");
                    foreach (var f in ChangeOperations.FilesOf(ws, c.Id)) r.Line("    " + f);
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "changes diff",
            Summary = "\"Compare\": your edit of a file against the game's (a texture, a model, a map file or a data table)",
            Args = new[] { "<path>" },
            Options = new[] { new OptionSpec("out", "file.png", "a texture: the difference heat map; a tile: the height difference") },
            Examples = new[] { "wds changes diff Creature\\Boar\\BoarSkinIvory.blp --out diff.png", "wds changes diff DBFilesClient\\Spell.dbc --json", "wds changes diff Spell" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Resolve(s, inv.Arg(0));
                (string cmd, string arg, string? outOpt) = path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? ("textures compare", path, "heatmap")
                    : path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ? ("models compare", path, null)
                    : MapValidator.KindOf(path) != null ? ("maps compare", path, "out")
                    : DbcService.TableOf(path) is { } t ? ("dbc diff", t, null)
                    : throw new CliError(ExitCode.Error, $"{path}: no comparison for this kind of file.");
                var spec = CliApp.Find(cmd)!;
                var opts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (inv.Opt("out") is { } o && outOpt != null) opts[outOpt] = new List<string> { o };
                return await spec.Run(new Invocation(spec, inv.Ctx, new List<string> { arg }, opts));
            },
        };
        yield return new CommandSpec
        {
            Path = "changes revert",
            Summary = "\"Revert\" / \"Revert my change\": throw away your edit of one file (its loose copy is removed from the game too; needs --yes)",
            Args = new[] { "<path>" },
            Options = new[] { ReloadOpt, Common.DryRun },
            Details = "Your exported PNG files are not touched. An installed patch-3.MPQ is not changed: build it again (or uninstall it). With --reload the running game goes back to the original at once (hot reload).",
            Examples = new[] { "wds changes revert Creature\\Boar\\BoarSkinIvory.blp --yes", "wds changes revert Spell --dry-run" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Resolve(s, inv.Arg(0));
                var rec = s.Workspace!.Get(path)!;
                var r = new CliResult().With(new { path, reverted = false }).Line($"Revert {path}: the edited copy is deleted from your workspace and the game's original is used again" + (rec.Installed != null ? "; its loose copy is removed from the game folder." : "."));
                if (inv.DryRun) return r;
                if (!inv.Yes) return Common.NeedsYes(r, "This deletes your edit.");
                bool wasInstalled = rec.Installed != null;
                var done = ChangeOperations.Revert(s, path, null);
                foreach (var d in done) r.Line(d);
                if (inv.Flag("reload") && wasInstalled)
                {
                    var outcome = await new HotReloadService(s.Workspace!.Paths.ClientDir).ReloadAsync(new[] { path });
                    AddReload(r, outcome);
                    return r.With(new { path, reverted = true, done, reload = ReloadJson(outcome) });
                }
                return r.With(new { path, reverted = true, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "changes revert-set",
            Summary = "\"Revert this import\": put every file of one change set back to the version it had before that import (needs --yes)",
            Args = new[] { "<change-set>" },
            Options = new[] { ReloadOpt, Common.DryRun },
            Details = "A file the import replaced while it held an earlier edit of yours (a map conversion builds on your tables, an update import replaces your texture) goes back to that edit; the others go back to the game's original. " +
                      "Other imports are not changed. When a newer import changed one of its files again, nothing is reverted (exit code 4): revert the newer import first, or use \"wds changes revert\" on single files.",
            Examples = new[] { "wds changes revert-set cs-20261011-101500-123-ab12 --yes", "wds changes revert-set cs-20261011-101500-123-ab12 --dry-run" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var id = inv.Arg(0);
                if (!ws.Manifest.ChangeSets.TryGetValue(id, out var set)) throw new CliError(ExitCode.NotFound, $"no change set {id} (\"wds changes sets\" lists them).");
                var files = ChangeOperations.FilesOf(ws, id);
                var plan = ws.PlanRevertChangeSet(id)!;
                var planJson = new { toEarlier = plan.ToEarlier, toOriginal = plan.ToOriginal, steps = plan.Steps.Select(st => new { path = st.Path, backTo = st.EarlierLabel == null ? "original" : "earlier edit", earlierImport = st.EarlierLabel }), problems = plan.Problems };
                var r = new CliResult().With(new { changeSet = id, label = set.Label, files, plan = planJson, reverted = false }).Line($"Revert all {files.Count} file(s) of \"{set.Label}\". {plan.Text}");
                foreach (var st in plan.Steps) r.Line("  " + st.Path + (st.EarlierLabel != null ? $"  (back to your earlier edit from \"{st.EarlierLabel}\")" : ""));
                if (!plan.CanRevert) return CliResult.Fail(ExitCode.Refused, plan.RefusalText).With(new { changeSet = id, label = set.Label, files, plan = planJson, reverted = false });
                if (inv.DryRun) return r;
                if (!inv.Yes) return Common.NeedsYes(r, "This deletes your edits of this import.");
                var installed = files.Where(f => ws.Get(f)?.Installed != null).ToList();
                var done = ChangeOperations.Revert(s, null, id);
                foreach (var d in done) r.Line(d);
                if (inv.Flag("reload") && installed.Count > 0)
                {
                    var outcome = await new HotReloadService(ws.Paths.ClientDir).ReloadAsync(installed);
                    AddReload(r, outcome);
                    return r.With(new { changeSet = id, label = set.Label, files, plan = planJson, reverted = true, done, reload = ReloadJson(outcome) });
                }
                return r.With(new { changeSet = id, label = set.Label, files, plan = planJson, reverted = true, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "changes install",
            Summary = "\"Test in game (install as loose files)\": copy edited files (default: all) into the game's Data folder (needs --yes)",
            Args = new[] { "[paths...]" },
            Options = new[] { Force, ReloadOpt, Common.DryRun },
            Details = ChangeOperations.LooseFilesNote + " Restart the game to see the change (or, with hot reload, --reload them in the running game); \"wds changes remove\" takes them out again.",
            Examples = new[] { "wds changes install --dry-run", "wds changes install --yes", "wds changes install Creature\\Boar\\BoarSkinIvory.blp --yes --json", "wds changes install --yes --reload" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var paths = inv.Args.Count == 0 ? null : inv.Args.Select(a => Resolve(s, a)).ToList();
                if (s.Workspace!.GameFiles().Count == 0) return CliResult.Fail(ExitCode.NotFound, "There are no changes to install.");
                return Install(inv, s, paths);
            },
        };
        yield return new CommandSpec
        {
            Path = "changes remove",
            Summary = "\"Remove from game\" / \"Remove all from game\": take the loose files the app installed out of the game folder (needs --yes)",
            Args = new[] { "[paths...]" },
            Options = new[] { ReloadOpt, Common.DryRun },
            Details = "Only files the app installed and that nobody changed since are deleted; a file that was there before is put back from its backup. Loose files only: the release patch stays (\"wds changes uninstall-patch\"). With --reload the running game goes back to the originals at once (hot reload).",
            Examples = new[] { "wds changes remove --yes", "wds changes remove Creature\\Boar\\BoarSkinIvory.blp --yes", "wds changes remove --yes --reload" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var paths = inv.Args.Count == 0 ? null : inv.Args.Select(a => Resolve(s, a)).ToList();
                var installed = ws.List().Where(v => v.Record.Installed != null && (paths == null || paths.Contains(v.Record.Path, StringComparer.OrdinalIgnoreCase))).Select(v => v.Record.Installed!.Dest).ToList();
                var r = new CliResult().With(new { files = installed, removed = false });
                r.Line(installed.Count == 0 && !ws.HasLooseInstalls() ? "Nothing is installed as a loose file." : $"Remove {Common.Plural(installed.Count, "loose file")} from the game folder (your edits stay in your workspace):");
                foreach (var f in installed) r.Line("  " + f);
                var st = ws.GetPatchState();
                if (st.Exists) r.Line($"This removes loose files only. {st.Text} To take it out of the game: wds changes uninstall-patch.");
                if (inv.DryRun || installed.Count == 0 && !ws.HasLooseInstalls()) return r;
                if (!inv.Yes) return Common.NeedsYes(r, "This deletes files in the game folder.");
                var removedPaths = ws.List().Where(v => v.Record.Installed != null && (paths == null || paths.Contains(v.Record.Path, StringComparer.OrdinalIgnoreCase))).Select(v => v.Record.Path)
                    .Concat(paths == null ? ws.Manifest.OrphanInstalls.Keys : Enumerable.Empty<string>()).ToList();
                var done = ws.RemoveLoose(paths);
                foreach (var d in done) r.Line(d);
                if (inv.Flag("reload"))
                {
                    var outcome = await new HotReloadService(ws.Paths.ClientDir).ReloadAsync(removedPaths);
                    AddReload(r, outcome);
                    return r.With(new { files = installed, removed = true, done, reload = ReloadJson(outcome) });
                }
                r.Line("Restart the game to see the original again (or reload it in the running game: --reload).");
                return r.With(new { files = installed, removed = true, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "changes build-patch",
            Summary = "\"Build release patch (patch-3.MPQ)...\": every change in one archive, checked by reading it back; --install puts it into the game (needs --yes)",
            Details = "When the game runs from the folder with hot reload on, --install asks it to let go of its patch-3.MPQ, installs the new one and has the game open it again and reload the changed files (the result is printed).",
            Options = new[] { new OptionSpec("install", null, "also install it into the game's Data folder (\"Also install it into the game:\"); an existing patch-3.MPQ is backed up first"), Common.DryRun },
            Examples = new[] { "wds changes build-patch --dry-run", "wds changes build-patch", "wds changes build-patch --install --yes --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var plan = ChangeOperations.PlanPatch(ws);
                if (plan.Files.Count == 0) return CliResult.Fail(ExitCode.NotFound, "Nothing to build: you have no changes yet.");
                bool install = inv.Flag("install");
                var r = new CliResult();
                r.Line(plan.CountText + ":");
                foreach (var f in plan.Files) r.Line($"  {f.Record.Path}  ({f.Record.Original.Format}, {f.Record.Edited:yyyy-MM-dd HH:mm})");
                r.Line(install ? $"Also install it into the game: {plan.Destination}" : "Build only (not installed into the game; --install does).");
                r.Line(plan.BackupNote);
                if (plan.ServerNote.Length > 0) r.Line(plan.ServerNote);
                r.With(new { built = false, files = plan.Files.Select(f => f.Record.Path), destination = install ? plan.Destination : null, state = new { owner = plan.State.Owner, text = plan.State.Text }, serverNote = plan.ServerNote });
                if (inv.DryRun) return r;
                if (install && !inv.Yes) return Common.NeedsYes(r, "Installing writes Data\\patch-3.MPQ into the game folder.");
                var (res, reload) = await ChangeOperations.BuildPatchLiveAsync(ws, install);
                r.Lines.Clear();
                r.Line(res.Message);
                return r.With(new
                {
                    built = true, path = res.Build.Path, installedTo = res.Build.InstalledTo, backup = res.Build.Backup, sha256 = res.Build.Sha256, size = res.Build.Size, files = res.Build.Files,
                    serverNote = plan.ServerNote, reload = reload == null ? null : ReloadJson(reload),
                });
            },
        };
        yield return new CommandSpec
        {
            Path = "changes patch-state",
            Summary = "what patch-3.MPQ is in the game: none, the release patch the app built (proved by its checksum), or someone else's",
            Examples = new[] { "wds changes patch-state", "wds changes patch-state --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var st = s.Workspace!.GetPatchState();
                return new CliResult().With(new { path = st.Path, owner = st.Owner, exists = st.Exists, canUninstall = st.CanUninstall, files = st.Build?.Files.Count, built = st.Build?.Built, size = st.Size, restoreBackup = st.RestoreBackup, text = st.Text }).Line(st.Text);
            },
        };
        yield return new CommandSpec
        {
            Path = "changes uninstall-patch",
            Summary = "\"Uninstall release patch (patch-3.MPQ)\": remove the app's patch and put back the one that was there before (needs --yes)",
            Details = "When the game runs from the folder with hot reload on, the game lets go of patch-3.MPQ first and reloads the patch's files afterwards (the result is printed).",
            Options = new[] { new OptionSpec("force", null, "a patch-3.MPQ the app did not create (or cannot prove it built): move it out into the backups folder (never deleted)"), Common.DryRun },
            Examples = new[] { "wds changes uninstall-patch --dry-run", "wds changes uninstall-patch --yes", "wds changes uninstall-patch --force --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var st = ws.GetPatchState();
                var (title, message, _) = ChangeOperations.UninstallQuestion(st, ws.Paths.BackupDir);
                var r = new CliResult().With(new { owner = st.Owner, path = st.Path, uninstalled = false });
                if (!st.Exists) return r.Line(st.Text);
                r.Line(title).Line(message);
                bool ours = st.Owner == PatchOwner.Ours;
                if (inv.DryRun) return r;
                if (!ours && !inv.Flag("force"))
                {
                    r.Code = ExitCode.Refused;
                    r.Error = $"{st.Path} may not be yours: it was left alone. Run it again with --force --yes to move it into the backups folder (it is not deleted).";
                    return r;
                }
                if (!inv.Yes) return Common.NeedsYes(r, ours ? "This removes patch-3.MPQ from the game folder." : "This moves patch-3.MPQ out of the game folder.");
                var (done, reload) = await ChangeOperations.UninstallPatchLiveAsync(ws, confirmedNotOurs: !ours);
                r.Lines.Clear();
                foreach (var d in done) r.Line(d);
                return r.With(new { owner = st.Owner, path = st.Path, uninstalled = true, done, reload = reload == null ? null : ReloadJson(reload) });
            },
        };
        yield return new CommandSpec
        {
            Path = "changes reload",
            Summary = "\"Reload in running game\": the running game reads changed files again without a restart (hot reload); default: every change of your workspace",
            Args = new[] { "[paths...]" },
            Options = new[]
            {
                new OptionSpec("wait", null, "wait for the game's report and print what is live now and what needs a relog or a restart (without it: return once the game took the request)"),
                new OptionSpec("timeout", "seconds", "how long to wait for the game (default 30)"),
                new OptionSpec("status", null, "only say whether the running game can reload (and why not); nothing is reloaded"),
            },
            Details = "Needs the game running from the game folder with hot reload switched on for that folder (\"wds settings set hot-reload on --yes\", then start the game) and the project's own client (wowcode.dll from the HR1 update on). " +
                      "Install the files first (\"wds changes install\", \"wds changes build-patch --install\"); this command only tells the game to read them again. Nothing in the game folder is changed. " +
                      "Exit codes: 0 reloaded (files that need a relog or a restart are listed), 6 the game is not running, 4 hot reload is off for the folder, 1 the game cannot reload (no support, not answering) or a file failed.",
            Examples = new[] { "wds changes reload --wait", "wds changes reload Creature\\Boar\\BoarSkinIvory.blp --wait --json", "wds changes reload --status", "wds changes reload" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var ws = s.Workspace!;
                var hr = new HotReloadService(ws.Paths.ClientDir);
                var timeout = TimeSpan.FromSeconds(Math.Clamp(inv.IntOpt("timeout") ?? 30, 2, 300));
                if (inv.Flag("status"))
                {
                    var probe = await hr.ProbeAsync(true);
                    var rs = new CliResult().With(new { state = probe.State, ready = probe.Ready, message = probe.Message, pid = probe.Game?.Pid, enabled = HotReloadFlag.IsEnabled(ws.Paths.ClientDir), status = probe.Status });
                    rs.Line(probe.Message);
                    if (probe.Status is { } st && probe.Ready)
                        rs.Line($"In the world: {(st.InWorld == true ? "yes" : "no")}. Loose-file mode: {(st.LooseFiles == true ? "on" : "off")}." + (st.Released.Count > 0 ? $" Released archives: {string.Join(", ", st.Released)}." : ""));
                    if (!probe.Ready)
                    {
                        rs.Code = probe.State switch { HotReloadState.NoGame => ExitCode.NotFound, HotReloadState.Off => ExitCode.Refused, _ => ExitCode.Error };
                        rs.Error = probe.Message;
                    }
                    return rs;
                }
                var paths = inv.Args.Count == 0 ? HotReloadService.DefaultPaths(ws) : inv.Args.Select(a => ReloadPath(ws, a)).ToList();
                if (paths.Count == 0) return CliResult.Fail(ExitCode.NotFound, "There is nothing to reload: you have no changes yet (give the files to reload, or install a change first).");
                var outcome = await hr.ReloadAsync(paths, null, inv.Flag("wait"), timeout);
                var r = new CliResult().With(ReloadJson(outcome));
                AddReload(r, outcome);
                r.Code = ReloadCode(outcome);
                if (r.Code != ExitCode.Ok) r.Error = outcome.Done ? $"{outcome.Report!.Problems.Count()} file(s) could not be reloaded (see above)." : outcome.Message;
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "changes folder",
            Summary = "\"Open my workspace folder\": where your edited files are kept (--open opens it in Explorer)",
            Options = new[] { new OptionSpec("open", null, "open it in Windows Explorer") },
            Examples = new[] { "wds changes folder", "wds changes folder --open" },
            Run = inv =>
            {
                var cp = inv.Ctx.Paths.ForClient(inv.Ctx.ClientDir());
                if (inv.Flag("open"))
                {
                    Directory.CreateDirectory(cp.WorkspaceDir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cp.WorkspaceDir) { UseShellExecute = true });
                }
                return Task.FromResult(new CliResult().With(new { workspace = cp.WorkspaceDir, changes = cp.ChangesFile, backups = cp.BackupDir, build = cp.BuildDir, index = cp.IndexFile }).Line(cp.WorkspaceDir));
            },
        };
    }
}
