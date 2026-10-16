using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WoWData.Core.HotReload;

/// <summary>
/// A stand-in for a running game with hot reload, for the tests and the manual's screenshots (never used by the app's own
/// flows): it is an <see cref="IGameLocator"/> that reports one game process in a game folder, and it serves that game's pipe
/// with the protocol of wowcode.dll (docs\HOT_RELOAD.md). Like the game, it can hold Data\patch-3.MPQ open (share read only)
/// until a "release" and open it again on a "remount". Its answers follow wowcode.dll's rules by file type.
/// </summary>
public sealed class FakeGame : IGameLocator, IDisposable
{
    private readonly string _clientDir;
    private readonly string _pipe = "wds-fakegame-" + Guid.NewGuid().ToString("N")[..12];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ManualResetEventSlim _listening = new();
    private FileStream? _patchLock;
    private bool _released;
    private int _id;

    private sealed record FileAnswer(string path, string kind, string result, string detail);

    /// <summary>The fake game's process id (any number; it is only used to name the pipe).</summary>
    public int Pid { get; } = 424242;
    /// <summary>Every request received, as sent.</summary>
    public List<string> Requests { get; } = new();
    /// <summary>The reply is delayed by this much (the game's main thread is busy).</summary>
    public int ReportDelayMs { get; set; }
    /// <summary>Accept the connection but never answer (a frozen game).</summary>
    public bool NeverAnswer { get; set; }
    /// <summary>False: the pipe is not there (a game without hot reload).</summary>
    public bool Serving { get; set; } = true;
    /// <summary>Status: in the world.</summary>
    public bool InWorld { get; set; } = true;
    /// <summary>Release requests are refused this many times ("in use").</summary>
    public int RefuseReleases { get; set; }
    /// <summary>A file type the game reports as "needs-restart" (for example "dbc").</summary>
    public string? RestartExtension { get; set; }

    public FakeGame(string clientDir, bool holdPatch = false)
    {
        _clientDir = Path.GetFullPath(clientDir);
        if (holdPatch) LockPatch();
        _loop = Task.Factory.StartNew(() => LoopAsync(_stop.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _listening.Wait(TimeSpan.FromSeconds(10));   // the pipe exists before the constructor returns (a busy thread pool must not race the first client)
    }

    public IReadOnlyList<GameProcess> Find(string clientDir) =>
        string.Equals(Path.GetFullPath(clientDir).TrimEnd('\\'), _clientDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? new[] { new GameProcess(Pid, Path.Combine(_clientDir, "WoW.exe"), DateTime.Now.AddMinutes(-5)) }
            : Array.Empty<GameProcess>();

    public string PipeName(int pid) => _pipe;
    public bool PipeExists(int pid) => Serving && pid == Pid;

    /// <summary>True while the fake game holds Data\patch-3.MPQ open like the real one does.</summary>
    public bool HoldsPatch => _patchLock != null;

    private void LockPatch()
    {
        var p = Path.Combine(_clientDir, "Data", "patch-3.MPQ");
        if (File.Exists(p)) _patchLock = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _listening.Set();
                await server.WaitForConnectionAsync(stop);
                var req = await ReadRequestAsync(server, stop);
                if (req == null) continue;
                lock (Requests) Requests.Add(req);
                if (NeverAnswer)
                {
                    await Task.Delay(Timeout.Infinite, stop).ContinueWith(_ => { });
                    continue;
                }
                var (first, second) = Answer(req);
                var w = new StreamWriter(server, new UTF8Encoding(false)) { NewLine = "\n" };
                await w.WriteLineAsync(first);
                await w.FlushAsync();
                if (second != null)
                {
                    if (ReportDelayMs > 0) await Task.Delay(ReportDelayMs, stop);
                    await w.WriteLineAsync(second);
                    await w.FlushAsync();
                }
                try { if (OperatingSystem.IsWindows()) server.WaitForPipeDrain(); }
                catch (IOException) { }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
        }
    }

    private static async Task<string?> ReadRequestAsync(Stream s, CancellationToken stop)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(buf, stop);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            var t = sb.ToString();
            if (t.Contains("\nend\n", StringComparison.Ordinal) || t.EndsWith("\nend", StringComparison.Ordinal)) return t;
        }
    }

    private (string First, string? Second) Answer(string req)
    {
        var lines = req.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "HOTRELOAD 1") return (Json(new { accepted = false, protocol = 1, error = "this game speaks hot-reload protocol 1" }), null);
        int id = Interlocked.Increment(ref _id);
        var accepted = Json(new { accepted = true, protocol = 1, id });
        var cmd = lines.Length > 1 ? lines[1] : "";
        if (cmd == "status")
            return (accepted, Json(new
            {
                protocol = 1, id, command = "status", ok = true, pid = Pid, enabled = true, marker = HotReloadFlag.DllMarker, frameHook = "chained to the recompiled function",
                mainThread = true, lastFrameMsAgo = 3, inWorld = InWorld, looseFiles = true, released = _released ? new[] { "patch-3.MPQ" } : Array.Empty<string>(),
                options = new { units = 1, gameobjects = 1, restream = 1, dbc = 1 },
            }));
        if (cmd.StartsWith("release ", StringComparison.Ordinal))
        {
            var name = cmd[8..];
            string result, detail;
            if (RefuseReleases > 0)
            {
                RefuseReleases--;
                (result, detail) = ("refused", "in use: the game has 1 file(s) of it open right now; try again in a moment");
            }
            else if (_patchLock != null)
            {
                _patchLock.Dispose();
                _patchLock = null;
                _released = true;
                (result, detail) = ("released", "released: the file can be replaced now");
            }
            else (result, detail) = ("not-mounted", "not mounted (it was not there when the game started)");
            return (accepted, Json(new { protocol = 1, id, command = "release", ok = result == "released", pid = Pid, archive = name, result, detail }));
        }
        var files = new List<FileAnswer>();
        object? archive = null;
        bool restream = false;
        foreach (var l in lines.Skip(2))
        {
            if (l.StartsWith("remount ", StringComparison.Ordinal))
            {
                var p = Path.Combine(_clientDir, "Data", l[8..]);
                _released = false;
                if (File.Exists(p))
                {
                    LockPatch();
                    archive = new { name = l[8..], result = "remounted", detail = "re-mounted: the game reads the new archive" };
                }
                else archive = new { name = l[8..], result = "removed", detail = "not there: the game runs without it now" };
            }
            else if (l.StartsWith("file ", StringComparison.Ordinal))
            {
                var path = l[5..];
                var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                var (kind, result, detail) = ext switch
                {
                    _ when ext == RestartExtension => (ext, "needs-restart", "read once at start-up: restart the game"),
                    "blp" => ("texture", "reloaded", "1 copy(ies) re-uploaded in place"),
                    "m2" or "mdx" => ("model", "reloaded", "everything in view that uses it has the new model now"),
                    "adt" => ("terrain", "reloaded", "the land around you was read again (as after a teleport)"),
                    "wmo" => ("building", "reloaded", "the buildings around you were placed again with the new file"),
                    "wdt" or "wdl" => ("map", "needs-relog", "the map's tile list (WDT) and its far view (WDL) are read when the map is loaded: relog"),
                    "dbc" => ("table", "reloaded", "1 rows updated in place"),
                    "lua" or "xml" or "toc" => ("interface", "needs-ui-reload", "interface files are read when the interface loads: type /reload"),
                    _ => ("other", "next-use", "the game reads the new file the next time it opens it"),
                };
                restream |= ext is "adt" or "wmo";
                files.Add(new FileAnswer(path, kind, result, detail));
            }
        }
        int Count(string r) => files.Count(f => f.result == r);
        var report = new
        {
            protocol = 1, id, command = "reload", ok = true, pid = Pid, elapsedMs = 12, frames = 1, inWorld = InWorld, looseFiles = true, archive, fileMap = "rescanned",
            restreamed = restream && InWorld, files,
            summary = new { reloaded = Count("reloaded"), freshOnNextUse = Count("next-use") + Count("not-in-memory"), needsRelog = Count("needs-relog") + Count("needs-ui-reload"), needsRestart = Count("needs-restart"), failed = Count("failed") },
        };
        return (accepted, Json(report));
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(2000); }
        catch (AggregateException) { }
        _patchLock?.Dispose();
        _patchLock = null;
    }
}
