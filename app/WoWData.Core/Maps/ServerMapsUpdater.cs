using WoWData.Core.Session;

namespace WoWData.Core.Maps;

/// <summary>The outcome of an install or an undo of "Update server maps".</summary>
public sealed record ServerStepResult(bool Ok, bool RefusedServerRuns, string Message, string? Backup, ScriptResult? Script);

/// <summary>
/// 0.8.0 (moved from the "Update server maps" window into Core): the guided update of the world server's map data, shared
/// by the desktop app and <c>wds server-maps ...</c>. 1. the map changes must be in the installed patch-3.MPQ (the server
/// tools read the archives, not loose files); 2. reextract.ps1 writes into the staging folder only; 3. swap_in.ps1 installs
/// the changed files after a verified backup and refuses while the world server (mangosd) runs; 4. undo puts the backup
/// back. The app never stops or starts the server.
/// </summary>
public sealed class ServerMapsUpdater
{
    public const string StopHowTo = "How to stop the world server: switch to its console window (\"mangosd\") and close it, or type  server shutdown 1  there and wait until it has quit. Leave the database (mysqld) and the login server (realmd) running. The app never stops or starts the server itself.";
    public const string MmapsCaveat = "About pathing (mmaps): a navigation mesh is rebuilt per tile, for the tiles you changed plus their 8 neighbours (1 to 3 minutes per tile). The rebuilt tiles join the old ones at their borders, which can connect a little less perfectly where the two meet; keeping the 8 neighbours keeps that seam away from your edit. Rebuilding a whole continent once makes it uniform but takes hours (see the server README). Without new mmaps, creatures keep walking along the old ground.";
    public static readonly string[] AllSteps = { "maps", "vmaps", "mmaps" };

    private readonly AppSession _session;
    private readonly MapCatalog _catalog;

    /// <summary>Which world servers run (tests pass a fake).</summary>
    public Func<List<string>> RunningServers { get; set; } = ServerMapsService.RunningWorldServers;

    public ServerMapsUpdater(AppSession session, MapCatalog catalog, ServerMapsService? service = null)
    {
        _session = session;
        _catalog = catalog;
        var client = session.Client?.ClientDir ?? throw new InvalidOperationException("No game folder is open.");
        ServerDataDir = ServerMapsService.DefaultServerData(session.Settings, client);
        if (service != null) Service = service;
        else if (ServerMapsService.FindTools(session.Settings, client) is { } tools) Service = new ServerMapsService(new ServerPaths(tools, ServerDataDir, client));
        ToolsDir = Service?.Paths.ToolsDir;
        MapIds = ServerMapsService.ChangedMapIds(session.Workspace!, catalog);
    }

    public ServerMapsService? Service { get; }
    public string? ToolsDir { get; }
    public string ServerDataDir { get; }
    public List<int> MapIds { get; }
    public string HistoryFile => _session.Client!.ServerHistoryFile;

    /// <summary>The changed maps in words ("Eastern Kingdoms (map 0): 2 file(s)").</summary>
    public string ChangedMapsText => MapIds.Count == 0
        ? "No map changes in your workspace: the server data already matches the game's maps (unless you changed them another way)."
        : string.Join("; ", MapIds.Select(id => $"{_catalog.ById(id)?.Name ?? "map " + id} (map {id}): {_session.Workspace!.ModifiedPaths().Count(p => MapValidator.KindOf(p) != null && p.Split('\\').ElementAtOrDefault(2)?.Equals(_catalog.ById(id)?.Directory, StringComparison.OrdinalIgnoreCase) == true)} file(s)"));

    /// <summary>The world servers that run now, and the sentence the window shows.</summary>
    public (bool Running, string Text) ServerState()
    {
        var running = RunningServers();
        return running.Count > 0
            ? (true, $"The world server is running ({string.Join(", ", running)}). Stop it before installing: the install refuses while it runs.")
            : (false, "The world server is not running: you can install now.");
    }

    /// <summary>Step 1: are the map changes in the installed patch-3.MPQ?</summary>
    public (bool Ok, string Text) PatchState()
    {
        try
        {
            var missing = ServerMapsService.MapChangesNotInArchives(_session.Workspace!, _session.Client!.DataDir);
            bool ok = missing.Count == 0;
            return (ok, MapIds.Count == 0 ? "Nothing to check." : ok
                ? "All your map changes are in the game's installed patch-3.MPQ: the server tools will see them."
                : $"{missing.Count} map file(s) are not in the installed patch-3.MPQ (or an older version is): {string.Join(", ", missing.Take(4).Select(Path.GetFileName))}{(missing.Count > 4 ? ", ..." : "")}. " +
                  "The server tools read the game's archives, not loose files: click \"Build release patch (patch-3.MPQ)...\" with \"Install into the game\" ticked first.");
        }
        catch (Exception e) when (e is IOException or Mpq.MpqException)
        {
            return (false, "The game's archives could not be read: " + e.Message);
        }
    }

    /// <summary>The report of the last extraction in the staging folder, or null.</summary>
    public ServerReport? Report() => Service == null ? null : ServerReport.Read(Path.Combine(Service.Paths.Staging, "changes.json"));

    /// <summary>The last update done through the app that was not undone (and whose backup still exists), or null.</summary>
    public ServerSwapRecord? LastUpdate() => ServerMapsService.LoadHistory(HistoryFile).LastOrDefault(h => h.Restored == null && Directory.Exists(h.Backup));

    /// <summary>The last update in words (the window's undo line).</summary>
    public string UndoText => LastUpdate() is { } last
        ? $"Last update: {last.Time:yyyy-MM-dd HH:mm}, maps {string.Join(",", last.Maps)} ({string.Join(", ", last.Kinds)}). Backup: {last.Backup}"
        : "No server update was done through the app yet (or it was already undone).";

    private ServerMapsService Tools => Service ?? throw new InvalidOperationException("The server tools (reextract.ps1, swap_in.ps1) were not found: set \"Server tools folder\" in the settings.");

    /// <summary>Step 2: extract into the staging folder (allowed while the server runs; only the staging folder is written).</summary>
    public async Task<(ScriptResult Result, ServerReport? Report, string Text)> ExtractAsync(IReadOnlyCollection<string> steps, IProgress<ServerProgress>? progress, CancellationToken ct = default)
    {
        var r = await Tools.ReextractAsync(MapIds, steps, progress, ct);
        var rep = Report();
        string text;
        if (r.Ok)
            text = $"Done in {r.Time.TotalSeconds:0} s. Nothing was written outside the staging folder." + (rep == null ? "" : rep.ToInstall == 0
                ? " The staging folder matches the server's data: there is nothing to install."
                : $" {rep.ToInstall} file(s) differ from the server's data. Next: stop the world server, then \"Install into the server\".");
        else text = $"The extraction stopped with an error (exit code {r.ExitCode}). The log says why; the server's data was not touched.";
        return (r, rep, text);
    }

    /// <summary>Step 3: install (or with <paramref name="dryRun"/> show what an install would do). The install refuses while
    /// the world server runs (checked here and again by swap_in.ps1) and records the backup for "Undo server update".</summary>
    public async Task<ServerStepResult> InstallAsync(IReadOnlyCollection<string> kinds, bool dryRun, IProgress<ServerProgress>? progress, CancellationToken ct = default)
    {
        var tools = Tools;
        var (running, state) = ServerState();
        if (!dryRun && running) return new ServerStepResult(false, true, "Refused: " + state + " " + StopHowTo, null, null);
        var r = await tools.SwapAsync(MapIds, kinds, dryRun, progress, ct);
        if (ServerMapsService.RefusedBecauseServerRuns(r)) return new ServerStepResult(false, true, "Refused: the world server is running. " + StopHowTo, null, r);
        if (!r.Ok) return new ServerStepResult(false, false, $"The install stopped with an error (exit code {r.ExitCode}); see the log. If files were already replaced, \"Undo server update\" restores them from the backup.", null, r);
        if (dryRun) return new ServerStepResult(true, false, "This is what an install would do (nothing was written):", null, r);
        var b = ServerMapsService.BackupOf(r);
        if (b != null)
        {
            var hist = ServerMapsService.LoadHistory(HistoryFile);
            hist.Add(new ServerSwapRecord { Time = DateTime.Now, Backup = b, ServerData = ServerDataDir, Maps = MapIds, Kinds = kinds.ToList() });
            ServerMapsService.SaveHistory(HistoryFile, hist);
        }
        var msg = b == null ? "Installed: " + (r.Output.LastOrDefault(l => l.StartsWith("installed:", StringComparison.Ordinal)) ?? "nothing to install (staging matches the server's data).")
            : $"Installed. A verified backup of every replaced file is in {b}. Start the world server again (Server\\Start mangosd 16bit.bat): it loads the new maps when a grid is first used.";
        return new ServerStepResult(true, false, msg, b, r);
    }

    /// <summary>Undo the last update done through the app (refused while the world server runs).</summary>
    public async Task<ServerStepResult> UndoAsync(IProgress<ServerProgress>? progress, CancellationToken ct = default)
    {
        var tools = Tools;
        var hist = ServerMapsService.LoadHistory(HistoryFile);
        var last = hist.LastOrDefault(h => h.Restored == null && Directory.Exists(h.Backup));
        if (last == null) return new ServerStepResult(true, false, "No server update was done through the app yet (or it was already undone): nothing to undo.", null, null);
        var (running, _) = ServerState();
        if (running) return new ServerStepResult(false, true, "Refused: undoing also needs the world server stopped. " + StopHowTo, last.Backup, null);
        var r = await tools.RestoreAsync(last.Backup, progress, ct);
        if (ServerMapsService.RefusedBecauseServerRuns(r)) return new ServerStepResult(false, true, "Refused: the world server is running. " + StopHowTo, last.Backup, r);
        if (!r.Ok) return new ServerStepResult(false, false, $"The restore stopped with an error (exit code {r.ExitCode}); see the log.", last.Backup, r);
        last.Restored = DateTime.Now;
        ServerMapsService.SaveHistory(HistoryFile, hist);
        return new ServerStepResult(true, false, "The server's data is back as it was. Start the world server again.", last.Backup, r);
    }
}
