using System.Text;
using WoWData.Core.Workspace;

namespace WoWData.Core.HotReload;

/// <summary>What the app can do with the game of a folder right now.</summary>
public enum HotReloadState
{
    /// <summary>No game runs from this folder.</summary>
    NoGame,
    /// <summary>The game runs and answers: "Reload in running game" works.</summary>
    Ready,
    /// <summary>The game runs, but hot reload is switched off for this folder (Settings).</summary>
    Off,
    /// <summary>Hot reload is switched on, but the game was started before (or before the DLL update): restart it.</summary>
    RestartGame,
    /// <summary>The running game's wowcode.dll has no hot reload (older than the HR1 update), or there is no wowcode.dll at all.</summary>
    NoSupport,
    /// <summary>The game runs with hot reload, but did not answer (frozen, a long loading screen, busy).</summary>
    NotResponding,
}

/// <summary>The answer of <see cref="HotReloadService.Probe"/>: the state, a sentence for people and, when the game answered,
/// its status.</summary>
public sealed record HotReloadProbe(HotReloadState State, string Message, GameProcess? Game, HotReloadReport? Status)
{
    public bool Ready => State == HotReloadState.Ready;

    /// <summary>A short line for the My changes tab.</summary>
    public string Short => State switch
    {
        HotReloadState.NoGame => "Hot reload: the game is not running.",
        HotReloadState.Ready => $"Hot reload: the game is running (process {Game!.Pid}{(Status?.InWorld == true ? ", in the world" : Status?.InWorld == false ? ", not in the world yet" : "")}) and takes reloads.",
        HotReloadState.Off => "Hot reload is off for this game folder (Settings).",
        HotReloadState.RestartGame => "Hot reload is on, but the running game was started before: restart it once.",
        HotReloadState.NoSupport => "The running game has no hot-reload support.",
        _ => "The game runs with hot reload but does not answer right now.",
    };
}

/// <summary>The outcome of a reload (or the reason there was none), with the texts the app and wds show.</summary>
public sealed record HotReloadOutcome(bool Done, HotReloadProbe Probe, HotReloadReport? Report, int RequestId, string Message)
{
    /// <summary>The report in words: what is live now, what needs a relog or a restart, what failed.</summary>
    public string Text => Report == null ? Message : HotReloadService.Describe(Report);
}

/// <summary>
/// 0.11.0: hot reload of changed files in the RUNNING game (research section 5, method C). The game side is wowcode.dll
/// (hooks\hotreload.c of the WoWDecomp project, staged as HR1): it listens on \\.\pipe\wowcode-hotreload-&lt;pid&gt; when the
/// game folder has <c>wowhotreload.txt</c> with "enabled 1" (<see cref="HotReloadFlag"/>), re-reads the loose-file map,
/// re-mounts a re-installed patch-3.MPQ and swaps the changed textures, models, buildings, terrain and tables at a safe point
/// of the game's main loop, and reports per file what is live and what needs a relog or a restart. The app and wds share
/// this class: "Reload in running game" and <c>wds changes reload</c>.
/// </summary>
public sealed class HotReloadService
{
    private static readonly AsyncLocal<IGameLocator?> TestLocator = new();

    /// <summary>Tests: use a fake game for the calls made in this async flow (the app and wds then talk to it).</summary>
    public static IDisposable UseLocator(IGameLocator locator)
    {
        var old = TestLocator.Value;
        TestLocator.Value = locator;
        return new Restore(() => TestLocator.Value = old);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    public string ClientDir { get; }
    private readonly IGameLocator _locator;

    public HotReloadService(string clientDir, IGameLocator? locator = null)
    {
        ClientDir = clientDir;
        _locator = locator ?? TestLocator.Value ?? WindowsGameLocator.Instance;
    }

    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

    /// <summary>How long a status question may take (the game answers within a frame; tests make it short).</summary>
    public TimeSpan StatusTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Is the game running from this folder, and can it reload? With <paramref name="askGame"/> the game is asked for
    /// its status (a second or so); without, only the process, the pipe and the files are looked at (instant, for a status line).</summary>
    public async Task<HotReloadProbe> ProbeAsync(bool askGame = true, CancellationToken cancel = default)
    {
        var games = _locator.Find(ClientDir);
        if (games.Count == 0) return new HotReloadProbe(HotReloadState.NoGame, $"The game is not running from {ClientDir}.", null, null);
        var g = games[0];
        bool enabled = HotReloadFlag.IsEnabled(ClientDir);
        if (_locator.PipeExists(g.Pid))
        {
            if (!askGame) return new HotReloadProbe(HotReloadState.Ready, "The game is running with hot reload.", g, null);
            try
            {
                var (acc, rep) = await HotReloadProtocol.SendAsync(_locator.PipeName(g.Pid), HotReloadProtocol.StatusRequest(), true, StatusTimeout, cancel);
                if (!acc.Accepted || rep == null)
                    return new HotReloadProbe(HotReloadState.NoSupport, $"The running game refused the request: {acc.Error}. Its wowcode.dll speaks another hot-reload version; update it and restart the game.", g, null);
                if (rep.MainThread == false)
                    return new HotReloadProbe(HotReloadState.NotResponding, "The game runs with hot reload, but its main loop does not answer (a long loading screen, or the game is stuck). Try again in a moment.", g, rep);
                return new HotReloadProbe(HotReloadState.Ready, "The game is running with hot reload" + (rep.InWorld == true ? " and is in the world." : " (at the login or character screen).") +
                                                                 (rep.LooseFiles == false ? " Note: its loose-file mode is OFF, so loose files are ignored; only patch-3.MPQ changes can be reloaded." : ""), g, rep);
            }
            catch (HotReloadException e)
            {
                return new HotReloadProbe(HotReloadState.NotResponding, $"The game runs with hot reload, but {e.Message}. Try again in a moment.", g, null);
            }
        }
        // no pipe: explain why
        if (!HotReloadFlag.DllPresent(ClientDir))
            return new HotReloadProbe(HotReloadState.NoSupport, "The running game has no wowcode.dll: it is a normal client. Hot reload needs the project's own client (with wowcode.dll from the HR1 update on); restart the game to see your changes.", g, null);
        var log = HotReloadFlag.LogLinesFor(ClientDir, g.Pid);
        if (!enabled)
            return new HotReloadProbe(HotReloadState.Off, "Hot reload is off for this game folder. Turn on \"Hot reload in the running game\" in Settings (or: wds settings set hot-reload on), then restart the game once. Until then, restart the game to see your changes.", g, null);
        if (log != null && log.Any(l => l.Contains("NOT started", StringComparison.Ordinal)))
            return new HotReloadProbe(HotReloadState.NoSupport, "The game's wowcode.dll could not start hot reload in this game (" + log.First(l => l.Contains("NOT started", StringComparison.Ordinal)) + "). Restart the game to see your changes.", g, null);
        if (log != null && log.Any(l => l.Contains("hotreload: off", StringComparison.Ordinal)))
            return new HotReloadProbe(HotReloadState.RestartGame, "Hot reload is on for this folder, but the running game was started before it was turned on. Restart the game once; from then on, changes can be reloaded without a restart.", g, null);
        if (log != null && log.Any(l => l.Contains("hotreload: on", StringComparison.Ordinal)))
            return new HotReloadProbe(HotReloadState.NotResponding, "The game started hot reload, but its connection is not there right now. Try again in a moment, or restart the game.", g, null);
        if (!HotReloadFlag.DllSupports(ClientDir))
            return new HotReloadProbe(HotReloadState.NoSupport, "The running game's wowcode.dll has no hot-reload support (it is older than the HR1 update). Update the client's wowcode.dll (it comes with the next deployed wave) and restart the game; until then, restart the game to see your changes.", g, null);
        return new HotReloadProbe(HotReloadState.RestartGame, "The game was started before its wowcode.dll was updated or before hot reload was turned on. Restart the game once.", g, null);
    }

    /// <summary>Reloads files (archive paths) in the running game, after optionally re-mounting a patch archive. With
    /// <paramref name="wait"/> false it returns once the game took the request.</summary>
    public async Task<HotReloadOutcome> ReloadAsync(IEnumerable<string> paths, string? remount = null, bool wait = true, TimeSpan? timeout = null,
        CancellationToken cancel = default)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var probe = await ProbeAsync(false, cancel);
        if (!probe.Ready) probe = await ProbeAsync(true, cancel);
        if (!probe.Ready) return new HotReloadOutcome(false, probe, null, 0, probe.Message);
        if (list.Count == 0 && remount == null) return new HotReloadOutcome(false, probe, null, 0, "There is nothing to reload.");
        var t = timeout ?? DefaultTimeout;
        try
        {
            var req = HotReloadProtocol.ReloadRequest(list, remount, (int)Math.Max(1000, t.TotalMilliseconds - 2000));
            var (acc, rep) = await HotReloadProtocol.SendAsync(_locator.PipeName(probe.Game!.Pid), req, wait, t, cancel);
            if (!acc.Accepted) return new HotReloadOutcome(false, probe, null, 0, "The game refused the request: " + acc.Error);
            if (!wait) return new HotReloadOutcome(true, probe, null, acc.Id, $"The game (process {probe.Game.Pid}) took reload request #{acc.Id} for {Plural(list.Count, "file")}; it runs at the next frame.");
            if (rep == null || !rep.Ok) return new HotReloadOutcome(false, probe, rep, acc.Id, "The game did not reload: " + (rep?.Error ?? "no report"));
            return new HotReloadOutcome(true, probe, rep, acc.Id, Describe(rep));
        }
        catch (HotReloadException e)
        {
            return new HotReloadOutcome(false, probe, null, 0, "The game could not be asked: " + e.Message);
        }
    }

    /// <summary>Asks the game to close Data\&lt;archive&gt; so the file can be replaced (it is re-mounted by the next reload with
    /// remount, or by the game itself after 60 s). Returns the game's report; Result "released", "not-mounted" or "refused".</summary>
    public async Task<HotReloadReport> ReleaseAsync(string archive, CancellationToken cancel = default)
    {
        var probe = await ProbeAsync(false, cancel);
        if (!probe.Ready) throw new HotReloadException(probe.Message);
        var (acc, rep) = await HotReloadProtocol.SendAsync(_locator.PipeName(probe.Game!.Pid), HotReloadProtocol.ReleaseRequest(archive), true, TimeSpan.FromSeconds(20), cancel);
        if (!acc.Accepted || rep == null) throw new HotReloadException("the game refused: " + acc.Error);
        return rep;
    }

    /// <summary>
    /// Installs or uninstalls the release patch while the game runs with hot reload: the game closes its patch-3.MPQ (only when
    /// none of its files is open; asked a few times), the file is written by <paramref name="change"/>, then the game opens it
    /// again and reloads <paramref name="files"/> (the old and the new patch's files). When the game cannot close it, nothing is
    /// written (the error says why). Returns the change's result and the reload outcome.
    /// </summary>
    public async Task<(T Result, HotReloadOutcome Reload)> ChangePatchLiveAsync<T>(string archive, Func<T> change, Func<T, IEnumerable<string>> files,
        CancellationToken cancel = default)
    {
        HotReloadReport? rel = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { rel = await ReleaseAsync(archive, cancel); }
            catch (HotReloadException e) { throw new WorkspaceException($"The running game could not be asked to let go of {archive} ({e.Message}). Nothing was changed; close the game and try again."); }
            if (rel.Result != "refused") break;
            await Task.Delay(500, cancel);
        }
        if (rel!.Result == "refused") throw new WorkspaceException($"The running game could not let go of {archive}: {rel.Detail}");
        T result;
        try
        {
            result = change();
        }
        catch
        {
            await ReloadAsync(Array.Empty<string>(), archive, true, null, cancel);   // put the old one back in the game
            throw;
        }
        var outcome = await ReloadAsync(files(result), archive, true, null, cancel);
        return (result, outcome);
    }

    /// <summary>The game files of the workspace to reload by default: every changed game file, and the loose files that were
    /// removed but are still recorded (orphans).</summary>
    public static List<string> DefaultPaths(Workspace.Workspace ws) =>
        ws.GameFiles().Select(v => v.Record.Path).Concat(ws.Manifest.OrphanInstalls.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The report in words (the app's dialog and wds print the same text).</summary>
    public static string Describe(HotReloadReport r)
    {
        var sb = new StringBuilder();
        var live = r.Live.ToList();
        var now = r.Files.Where(f => f.Result == ReloadResult.Reloaded).ToList();
        if (r.Archive is { } a) sb.AppendLine($"{a.Name}: {a.Result} ({a.Detail}).");
        if (live.Count > 0) sb.AppendLine($"Live in the game now: {Plural(live.Count, "file")}" + (now.Count < live.Count ? $" ({now.Count} reloaded, {live.Count - now.Count} read fresh when the game next needs {(live.Count - now.Count == 1 ? "it" : "them")})." : "."));
        var relog = r.Relog.ToList();
        if (relog.Count > 0) sb.AppendLine($"Needs a relog (or /reload for interface files): {string.Join(", ", relog.Select(f => Path.GetFileName(f.Path)))}.");
        var restart = r.Restart.ToList();
        if (restart.Count > 0) sb.AppendLine($"Needs a restart of the game: {string.Join(", ", restart.Select(f => Path.GetFileName(f.Path)))}.");
        var bad = r.Problems.ToList();
        if (bad.Count > 0) sb.AppendLine($"Not reloaded: {string.Join(", ", bad.Select(f => Path.GetFileName(f.Path)))} (see the details).");
        if (r.Restreamed) sb.AppendLine("The world around your character was reloaded (like after a teleport).");
        if (r.LooseFiles == false) sb.AppendLine("Note: the game's loose-file mode is OFF: loose files are ignored by this game.");
        if (r.Files.Count == 0 && r.Archive == null) sb.AppendLine("Nothing to reload.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>One line per file: result and the game's explanation.</summary>
    public static string Details(HotReloadReport r) =>
        string.Join("\n", r.Files.Select(f => $"{Label(f.Result)}: {f.Path}\n    {f.Detail}"));

    public static string Label(string result) => result switch
    {
        ReloadResult.Reloaded => "live now",
        ReloadResult.NotInMemory => "live (not loaded yet)",
        ReloadResult.NextUse => "next time it is used",
        ReloadResult.NeedsRelog => "relog",
        ReloadResult.NeedsUiReload => "/reload",
        ReloadResult.NeedsRestart => "restart",
        ReloadResult.Busy => "still loading",
        _ => "failed",
    };

    private static string Plural(int n, string one) => $"{n} {(n == 1 ? one : one + "s")}";
}
