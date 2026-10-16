using System.Text.Json;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;

namespace IngameRunner;

/// <summary>The two delivery paths, as the app runs them: "Test all in game (install as loose files)" = Workspace.InstallLoose(null),
/// "Remove all from game" = RemoveLoose(null), "Build release patch" with "install into the game" = BuildPatch("patch-3.MPQ", true).
/// "unpatch" removes the installed patch-3.MPQ again (the app has no button for it; the manual says to delete or replace the file).</summary>
public static class Deliver
{
    public static int Run(string clientDir, string home, string runDir, string mode)
    {
        var paths = new AppPaths(home).ForClient(clientDir);
        var ws = new Workspace(paths);
        var log = new List<string> { $"{DateTime.Now:s} deliver {mode}" };
        switch (mode)
        {
            case "loose":
                foreach (var a in ws.PlanLooseInstall(null)) log.Add($"plan: {a.Action} {a.Dest}");
                foreach (var a in ws.InstallLoose(null)) log.Add($"installed: {a.Action} {a.Path} -> {a.Dest}");
                break;
            case "unloose":
                log.AddRange(ws.RemoveLoose(null));
                if (ws.HasLooseInstalls()) throw new InvalidOperationException("loose installs remain");
                break;
            case "patch":
                if (ws.HasLooseInstalls()) throw new InvalidOperationException("loose files are installed: remove them first (they win over patch-3.MPQ)");
                var b = ws.BuildPatch("patch-3.MPQ", install: true);
                log.Add($"built {b.Path} ({new FileInfo(b.Path).Length:N0} bytes, {b.Files.Count} files, read-back verified), installed to {b.InstalledTo}, backup {b.Backup ?? "none (no earlier patch-3.MPQ)"}");
                foreach (var f in b.Files) log.Add("  in archive: " + f);
                // the game's view of it: the archive is mounted first (patch-?.MPQ, descending) and serves every edited file
                using (var cd = new ClientData(paths.DataDir))
                {
                    log.Add("mount order: " + string.Join(", ", ClientData.ClientArchiveOrder(paths.DataDir).Select(Path.GetFileName)));
                    foreach (var f in b.Files) log.Add($"  {f}: served by {cd.Find(f)?.Name}");
                }
                break;
            case "uninstall":
            {
                // 0.6.1 "Uninstall release patch (patch-3.MPQ)" in My changes: Workspace.GetPatchState + UninstallPatch (never "not ours" confirmed here)
                var before = ws.GetPatchState();
                log.Add($"patch state before: {before.Owner}: {before.Text} restore backup: {before.RestoreBackup ?? "none"}");
                log.AddRange(ws.UninstallPatch());
                var after = ws.GetPatchState();
                log.Add($"patch state after: {after.Owner}: {after.Text}");
                if (after.Owner != PatchOwner.None && before.RestoreBackup == null) throw new InvalidOperationException("patch-3.MPQ still installed after the uninstall");
                break;
            }
            case "patchstate":
            {
                var st = ws.GetPatchState();
                log.Add($"patch state: {st.Owner}: {st.Text}");
                break;
            }
            case "unpatch":
                var dest = Path.Combine(paths.DataDir, "patch-3.MPQ");
                if (File.Exists(dest)) { File.Delete(dest); log.Add($"deleted {dest}"); }
                break;
            default: throw new ArgumentException(mode);
        }
        log.Add("Data folder now: " + string.Join(", ", Directory.EnumerateFileSystemEntries(paths.DataDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("patch-3", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.Exists(f) && !f.Contains("Cinematics")).Select(f => Path.GetRelativePath(paths.DataDir, f))));
        log.Add("My changes: " + string.Join(" | ", ws.List().Select(v => $"{Path.GetFileName(v.Record.Path)}: {v.StatusText}")));
        foreach (var l in log) Console.WriteLine(l);
        File.AppendAllLines(Path.Combine(runDir, "delivery_log.txt"), log);
        return 0;
    }
}
