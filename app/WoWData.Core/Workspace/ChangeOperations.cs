using WoWData.Core.Dbc;
using WoWData.Core.HotReload;
using WoWData.Core.Maps;
using WoWData.Core.Session;

namespace WoWData.Core.Workspace;

/// <summary>What "Build release patch" will put in and the notes of its window.</summary>
public sealed record PatchPlan(IReadOnlyList<ChangeView> Files, string Destination, PatchState State, string BackupNote, string ServerNote)
{
    public string CountText => $"Included: {Files.Count} file{(Files.Count == 1 ? "" : "s")}";
}

/// <summary>The result of "Build release patch", with the text of the app's "Release patch built" message.</summary>
public sealed record PatchResult(PatchBuild Build, string Message);

/// <summary>
/// 0.8.0 (moved from the app's view models into Core): the operations of "My changes" and of the action bar, shared by
/// the desktop app and the command line (<c>wds changes ...</c>): revert a file or a whole import (also forgetting the
/// identical copy in a map's Noggit project), install and remove loose files, build and uninstall the release patch, with
/// the texts the app shows.
/// </summary>
public static class ChangeOperations
{
    public const string LooseFilesNote =
        "The game only reads such loose files when its loose-file mode is on. The project's own client has it built in from update wave185 on; a normal 1.12.1 client ignores loose files and keeps using its archives (use \"Build release patch\" there).";

    public const string ServerMapsNote = "The world server has its own copy of the map data (Server\\data: terrain, collision, pathing). Until you update it (\"Update server maps\" in the Maps tab), creatures walk on the old ground, line of sight goes through new walls, and pathing does not match what you see.";

    /// <summary>The paths of a change set (one import).</summary>
    public static List<string> FilesOf(Workspace ws, string changeSetId) =>
        ws.Manifest.ChangeSets.TryGetValue(changeSetId, out var set)
            ? set.Files.Select(k => ws.Manifest.Files.TryGetValue(k, out var r) ? r.Path : k).ToList()
            : new List<string>();

    /// <summary>The question before "Revert" / "Revert my change" (one file) or "Revert this import" (a change set), or, when
    /// the import cannot be reverted, the refusal (<c>Refused</c> true: the app shows it as an error instead of asking).</summary>
    public static (string Message, bool Refused) RevertQuestion(Workspace ws, string? path, string? changeSetId)
    {
        const string tail = " Your exported PNG files are not touched.";
        if (changeSetId != null)
        {
            var plan = ws.PlanRevertChangeSet(changeSetId);
            if (plan == null) return ("This import has no files left to revert.", true);
            if (!plan.CanRevert) return (plan.RefusalText, true);
            return ($"Undo all {plan.Steps.Count} file(s) of \"{plan.Set.Label}\"? {plan.Text} Loose copies of its files are removed from the game folder too." + tail, false);
        }
        var rec = path != null ? ws.Get(path) : null;
        var name = System.IO.Path.GetFileName(path ?? "");
        var earlier = rec?.Prior != null
            ? " Your earlier versions of it, from imports this one replaced, are thrown away too (\"Revert this import\" goes back one import instead)."
            : "";
        return ($"Undo your change to {name}? The edited copy is deleted from your workspace and the game's original is used again." + earlier + " If it is installed as a loose file, it is removed from the game folder too." + tail, false);
    }

    /// <summary>Revert one file or a whole change set: the workspace copy is deleted (and its loose install removed), and
    /// map files are forgotten in the map's Noggit project when the copy there is identical.</summary>
    public static List<string> Revert(AppSession session, string? path, string? changeSetId)
    {
        var ws = session.Workspace ?? throw new WorkspaceException("No game folder is open.");
        var files = changeSetId != null ? FilesOf(ws, changeSetId) : path != null ? new List<string> { path } : new();
        var mapFiles = files.Where(f => MapValidator.KindOf(f) != null).Select(f => (f, ws.Get(f)?.WorkspaceSha256 ?? "")).ToList();
        var done = changeSetId != null ? ws.RevertChangeSet(changeSetId) : ws.Revert(path!);
        if (mapFiles.Count > 0 && session.Noggit is { } noggit && session.MapCatalog is { } cat)
            foreach (var g in mapFiles.GroupBy(m => m.f.Split('\\').ElementAtOrDefault(2) ?? "", StringComparer.OrdinalIgnoreCase))
                if (cat.ByDirectory(g.Key) is { } map) noggit.ForgetReverted(map, g);
        foreach (var t in files.Select(DbcService.TableOf).Where(t => t != null)) session.Dbc?.Invalidate(t!);
        return done;
    }

    /// <summary>"Test in game": copy workspace files into the game's Data folder. A file there that the app did not put
    /// there is only replaced with <paramref name="force"/> (it is backed up first and put back on remove).</summary>
    public static List<DeliveryAction> InstallLoose(Workspace ws, IEnumerable<string>? paths, bool force) => ws.InstallLoose(paths?.ToList(), force);

    /// <summary>True when the refusal of <see cref="InstallLoose"/> is "a file is already there" (the app's question).</summary>
    public static bool IsForeignFileRefusal(WorkspaceException e) => e.Message.Contains("not installed by WoW Data Studio", StringComparison.Ordinal);

    /// <summary>What "Build release patch" would include, where it goes, and the notes of the build window.</summary>
    public static PatchPlan PlanPatch(Workspace ws)
    {
        var rows = ws.GameFiles();
        var dest = Path.Combine(ws.Paths.DataDir, "patch-3.MPQ");
        var st = ws.GetPatchState();
        var backup = st.Owner switch
        {
            PatchOwner.None => "There is no patch-3.MPQ yet; it will be added. Restart the game to load it.",
            PatchOwner.Ours => $"Installed now: your earlier release patch ({st.Build!.Files.Count} files, built {st.Build.Built:yyyy-MM-dd HH:mm}); it is copied to the backup folder first, then replaced.",
            _ => st.Text + " It is copied to the backup folder first, then replaced; \"Uninstall release patch\" in My changes puts it back.",
        };
        backup += $" The archive is always saved in {ws.Paths.BuildDir} too.";
        var server = "";
        int maps = rows.Count(r => MapValidator.KindOf(r.Record.Path) != null);
        if (maps > 0)
            server = $"{maps} map file{(maps == 1 ? "" : "s")} included. The server needs its own update too (Maps tab, \"Update server maps...\"), or creatures walk on the old ground and pathing does not match; the server tools read this patch-3.MPQ, so install it into the game first.";
        var tables = rows.Select(r => DbcService.TableOf(r.Record.Path)).Where(t => t != null).Select(t => DbcLayouts.Default.Get(t!)).Where(l => l != null).ToList();
        var serverFile = tables.Where(l => l!.ServerUse == DbcServerUse.ServerFile).Select(l => l!.Name).ToList();
        var serverDb = tables.Where(l => l!.ServerUse == DbcServerUse.ServerDatabase).Select(l => $"{l!.Name} ({l.ServerTable})").ToList();
        if (serverFile.Count + serverDb.Count > 0)
            server = ((server.Length > 0 ? server + Environment.NewLine + Environment.NewLine : "") +
                      (serverFile.Count > 0 ? $"The server reads its own copy of {string.Join(", ", serverFile)}: copy them with \"Copy to server...\" in the Data tables tab. " : "") +
                      (serverDb.Count > 0 ? $"The server takes {string.Join(", ", serverDb)} from its database: edit those tables there to change what the server does." : "")).Trim();
        return new PatchPlan(rows, dest, st, backup, server);
    }

    /// <summary>"Build release patch": build (checked by reading it back) and optionally install into the game.</summary>
    public static PatchResult BuildPatch(Workspace ws, bool install)
    {
        var build = ws.BuildPatch("patch-3.MPQ", install);
        var msg = $"patch-3.MPQ was built with {build.Files.Count} file{(build.Files.Count == 1 ? "" : "s")} and checked by reading it back.\n\nSaved at: {build.Path}";
        if (build.InstalledTo != null)
            msg += $"\nInstalled into the game: {build.InstalledTo}" + (build.Backup != null ? $"\nThe previous patch-3.MPQ was backed up to: {build.Backup}" : "") +
                   "\nTo take it out again: My changes > \"Uninstall release patch (patch-3.MPQ)\".";
        if (build.InstalledTo != null && ws.HasLooseInstalls())
            msg += "\n\nNote: you also have loose files installed. With the loose-file mode on they win over every archive; use \"Remove all from game\" to test the archive alone.";
        msg += "\n\nRestart the game to load it.";
        if (build.Files.Any(f => MapValidator.KindOf(f) != null))
            msg += "\n\nIt contains map files: " + ServerMapsNote + " The server tools read this patch-3.MPQ.";
        return new PatchResult(build, msg);
    }

    /// <summary>0.11.0: "Build release patch" with "Also install it into the game" while the game runs with hot reload: the game
    /// lets go of its patch-3.MPQ, the new one is installed, the game opens it again and reloads the files of the old and the new
    /// patch (<see cref="HotReloadService.ChangePatchLiveAsync{T}"/>). Without a running game (or without hot reload) it is
    /// <see cref="BuildPatch"/>: a running game without hot reload keeps the file locked and the install fails as before.</summary>
    public static async Task<(PatchResult Result, HotReloadOutcome? Reload)> BuildPatchLiveAsync(Workspace ws, bool install, IGameLocator? locator = null)
    {
        if (!install) return (BuildPatch(ws, false), null);
        var hr = new HotReloadService(ws.Paths.ClientDir, locator);
        var probe = await hr.ProbeAsync(false);
        if (!probe.Ready) return (BuildPatch(ws, true), null);
        var before = ws.GetPatchState();
        var oldFiles = before.Build?.Files ?? new List<string>();
        var (res, outcome) = await hr.ChangePatchLiveAsync("patch-3.MPQ", () => BuildPatch(ws, true), r => r.Build.Files.Concat(oldFiles));
        return (res with { Message = LiveMessage(res.Message, outcome) }, outcome);
    }

    /// <summary>0.11.0: "Uninstall release patch" while the game runs with hot reload (see <see cref="BuildPatchLiveAsync"/>).</summary>
    public static async Task<(List<string> Done, HotReloadOutcome? Reload)> UninstallPatchLiveAsync(Workspace ws, bool confirmedNotOurs, IGameLocator? locator = null)
    {
        var hr = new HotReloadService(ws.Paths.ClientDir, locator);
        var probe = await hr.ProbeAsync(false);
        if (!probe.Ready) return (ws.UninstallPatch("patch-3.MPQ", confirmedNotOurs), null);
        var files = ws.GetPatchState().Build?.Files ?? new List<string>();
        var (done, outcome) = await hr.ChangePatchLiveAsync("patch-3.MPQ", () => ws.UninstallPatch("patch-3.MPQ", confirmedNotOurs), _ => files);
        if (outcome.Done && done.Count > 0 && done[^1].StartsWith("Restart the game", StringComparison.Ordinal)) done[^1] = "The running game was updated (hot reload):\n" + outcome.Text;
        else if (!outcome.Done) done.Add("Hot reload: " + outcome.Message);
        return (done, outcome);
    }

    private static string LiveMessage(string message, HotReloadOutcome o)
    {
        const string restart = "\n\nRestart the game to load it.";
        var text = o.Done ? "\n\nThe running game was updated (hot reload):\n" + o.Text : "\n\nHot reload: " + o.Message + " Restart the game to load it.";
        return message.Contains(restart, StringComparison.Ordinal) ? message.Replace(restart, text) : message + text;
    }

    /// <summary>The question before uninstalling the release patch (the app's dialog, the screenshots, the command line).</summary>
    public static (string Title, string Message, string Yes) UninstallQuestion(PatchState st, string backupDir) => st.Owner == PatchOwner.Ours
        ? ("Uninstall release patch (patch-3.MPQ)", st.Text + "\n\nYour changes stay in your workspace: build the patch again at any time.", "Uninstall it")
        : ("This patch-3.MPQ may not be yours",
           st.Text + "\n\nIt may belong to another mod or to the server you play on. If you continue, it is not deleted: it is moved out of the game into the backup folder\n" +
           backupDir + "\nand you can put it back from there." + (st.RestoreBackup != null ? $"\n\nThe patch-3.MPQ that was there before WoW Data Studio's first install is then put back from {st.RestoreBackup}." : ""),
           "Move it out of the game");

    /// <summary>The changed map files of one map in the workspace.</summary>
    public static List<string> MapFiles(Workspace ws, MapInfo map)
    {
        var prefix = $@"WORLD\MAPS\{map.Directory.ToUpperInvariant()}\";
        return ws.ModifiedPaths().Where(p => p.ToUpperInvariant().StartsWith(prefix, StringComparison.Ordinal) && MapValidator.KindOf(p) != null).ToList();
    }
}
