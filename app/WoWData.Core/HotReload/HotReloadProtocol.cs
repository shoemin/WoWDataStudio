using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWData.Core.HotReload;

/// <summary>What one file's reload did in the game (wowcode.dll's words; see docs\HOT_RELOAD.md).</summary>
public static class ReloadResult
{
    public const string Reloaded = "reloaded";            // live now
    public const string NotInMemory = "not-in-memory";    // the game had not loaded it: it reads the new file when it needs it
    public const string NextUse = "next-use";             // read the next time the game opens it
    public const string NeedsRelog = "needs-relog";
    public const string NeedsUiReload = "needs-ui-reload"; // /reload in the game
    public const string NeedsRestart = "needs-restart";
    public const string Busy = "busy";                    // still loading: reload again
    public const string Failed = "failed";

    /// <summary>Live: the change is in the game now, or the game reads it at its next use.</summary>
    public static bool IsLive(string r) => r is Reloaded or NotInMemory or NextUse;
}

public sealed class HotReloadFileResult
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

public sealed class HotReloadArchiveResult
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

public sealed class HotReloadSummary
{
    [JsonPropertyName("reloaded")] public int Reloaded { get; set; }
    [JsonPropertyName("freshOnNextUse")] public int FreshOnNextUse { get; set; }
    [JsonPropertyName("needsRelog")] public int NeedsRelog { get; set; }
    [JsonPropertyName("needsRestart")] public int NeedsRestart { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
}

/// <summary>The game's answer to a request (status, reload or release): the second line of the reply.</summary>
public sealed class HotReloadReport
{
    [JsonPropertyName("protocol")] public int Protocol { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    // status
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("frameHook")] public string? FrameHook { get; set; }
    [JsonPropertyName("mainThread")] public bool? MainThread { get; set; }
    [JsonPropertyName("inWorld")] public bool? InWorld { get; set; }
    [JsonPropertyName("looseFiles")] public bool? LooseFiles { get; set; }
    [JsonPropertyName("released")] public List<string> Released { get; set; } = new();
    [JsonPropertyName("options")] public Dictionary<string, int>? Options { get; set; }
    [JsonPropertyName("kinds")] public Dictionary<string, string>? Kinds { get; set; }
    // reload
    [JsonPropertyName("elapsedMs")] public int ElapsedMs { get; set; }
    [JsonPropertyName("frames")] public int Frames { get; set; }
    [JsonPropertyName("fileMap")] public string? FileMap { get; set; }
    [JsonPropertyName("restreamed")] public bool Restreamed { get; set; }
    [JsonPropertyName("restreamReason")] public string? RestreamReason { get; set; }
    [JsonPropertyName("archive")] public JsonElement? ArchiveRaw { get; set; }
    [JsonPropertyName("files")] public List<HotReloadFileResult> Files { get; set; } = new();
    [JsonPropertyName("summary")] public HotReloadSummary? Summary { get; set; }
    // release ("archive" is then the name)
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }

    /// <summary>reload with a remount: what happened to the archive.</summary>
    [JsonIgnore]
    public HotReloadArchiveResult? Archive => ArchiveRaw is { ValueKind: JsonValueKind.Object } a ? a.Deserialize<HotReloadArchiveResult>() : null;

    [JsonIgnore] public IEnumerable<HotReloadFileResult> Live => Files.Where(f => ReloadResult.IsLive(f.Result));
    [JsonIgnore] public IEnumerable<HotReloadFileResult> Relog => Files.Where(f => f.Result is ReloadResult.NeedsRelog or ReloadResult.NeedsUiReload);
    [JsonIgnore] public IEnumerable<HotReloadFileResult> Restart => Files.Where(f => f.Result == ReloadResult.NeedsRestart);
    [JsonIgnore] public IEnumerable<HotReloadFileResult> Problems => Files.Where(f => f.Result is ReloadResult.Failed or ReloadResult.Busy);
}

/// <summary>The first reply line: the game took the request (or refused it).</summary>
public sealed class HotReloadAccepted
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("protocol")] public int Protocol { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The game could not be asked (no pipe, no answer in time, a garbled answer).</summary>
public sealed class HotReloadException : Exception
{
    public HotReloadException(string message) : base(message) { }
}

/// <summary>
/// The wire protocol of wowcode.dll's hot reload (docs\HOT_RELOAD.md): a request is text lines ending with "end"
/// ("HOTRELOAD 1", the command, "file ..." / "remount ..." / "wait ..."); the reply is two JSON lines, "accepted" and the report.
/// </summary>
public static class HotReloadProtocol
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string StatusRequest() => $"HOTRELOAD {HotReloadFlag.Protocol}\nstatus\nend\n";

    public static string ReleaseRequest(string archive) => $"HOTRELOAD {HotReloadFlag.Protocol}\nrelease {Check(archive)}\nend\n";

    /// <summary>A reload of game files (archive paths like Creature\Boar\BoarSkinIvory.blp), optionally re-mounting a
    /// patch archive first. <paramref name="waitMs"/>: how long the game's pipe waits for its main thread.</summary>
    public static string ReloadRequest(IEnumerable<string> paths, string? remount = null, int waitMs = 15000)
    {
        var sb = new StringBuilder();
        sb.Append("HOTRELOAD ").Append(HotReloadFlag.Protocol).Append('\n').Append("reload\n");
        if (!string.IsNullOrEmpty(remount)) sb.Append("remount ").Append(Check(remount)).Append('\n');
        foreach (var p in paths.Select(p => Check(p.Replace('/', '\\'))).Distinct(StringComparer.OrdinalIgnoreCase)) sb.Append("file ").Append(p).Append('\n');
        sb.Append("wait ").Append(Math.Clamp(waitMs, 1000, 120000)).Append('\n').Append("end\n");
        return sb.ToString();
    }

    private static string Check(string s)
    {
        if (s.Length == 0 || s.Length > 255 || s.Any(c => c < 0x20)) throw new ArgumentException($"not a file name the game can take: \"{s}\"");
        return s;
    }

    public static HotReloadAccepted ParseAccepted(string line)
    {
        try { return JsonSerializer.Deserialize<HotReloadAccepted>(line, Json) ?? throw new HotReloadException("the game sent an empty answer"); }
        catch (JsonException e) { throw new HotReloadException("the game's answer is not readable: " + e.Message); }
    }

    public static HotReloadReport ParseReport(string line)
    {
        try { return JsonSerializer.Deserialize<HotReloadReport>(line, Json) ?? throw new HotReloadException("the game sent an empty report"); }
        catch (JsonException e) { throw new HotReloadException("the game's report is not readable: " + e.Message); }
    }

    /// <summary>Sends one request over the game's pipe. With <paramref name="wantReport"/> false it returns after the
    /// "accepted" line (the game still does the work). Throws <see cref="HotReloadException"/> when the game cannot be reached
    /// or does not answer within <paramref name="timeout"/>.</summary>
    /// <remarks>
    /// Unreleased: the exchange runs on a thread of its own with blocking calls, and the time limit is kept by a second thread
    /// of its own, never by the thread pool. Before, every step (the connect, the write, each line read) was an await whose
    /// continuation needed a free thread-pool thread, while the limit was a timer: when the pool was busy (the app building its
    /// index, the test suite under load) a game that had answered at once was reported as "did not answer within 8 seconds",
    /// because the steps waited in the pool's queue until the timer ran out. Also, when the limit was not above the connect's
    /// own 5 seconds, a busy connection gave either "in use" or "did not answer" depending on which of two timers fired first;
    /// a connection that cannot be had in time is now always "in use".
    /// </remarks>
    public static Task<(HotReloadAccepted Accepted, HotReloadReport? Report)> SendAsync(string pipeName, string request, bool wantReport, TimeSpan timeout,
        CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<(HotReloadAccepted, HotReloadReport?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { done.SetResult(Exchange(pipeName, request, wantReport, timeout, cancel)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { done.SetCanceled(cancel); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Name = "Hot reload request" };
        worker.Start();
        return done.Task;
    }

    private const string InUse = "its hot-reload connection is in use: another WoW Data Studio window or a wds command is talking to this game right now (it takes one at a time), or the game just closed it";

    /// <summary>One request with blocking calls on the calling thread (a thread of its own, see <see cref="SendAsync"/>); a
    /// watchdog thread cancels the blocking call that is running when the time is up or <paramref name="cancel"/> is set.</summary>
    private static (HotReloadAccepted, HotReloadReport?) Exchange(string pipeName, string request, bool wantReport, TimeSpan timeout, CancellationToken cancel)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool connected = false;
        int stopped = 0;   // 1: the time is up or the caller cancelled
        using var finished = new ManualResetEventSlim();
        var self = OperatingSystem.IsWindows() ? OpenThread(ThreadTerminate, false, GetCurrentThreadId()) : IntPtr.Zero;
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
        var watchdog = new Thread(() =>
        {
            var left = timeout - clock.Elapsed;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            if (WaitHandle.WaitAny(new[] { finished.WaitHandle, cancel.WaitHandle }, left) == 0) return;
            Volatile.Write(ref stopped, 1);
            // the worker checks `stopped` before each blocking call; this cancels the call it is in (again until it is out)
            while (!finished.Wait(10))
            {
                if (self != IntPtr.Zero) CancelSynchronousIo(self);
                else pipe.Dispose();
            }
        }) { IsBackground = true, Name = "Hot reload time limit" };
        watchdog.Start();
        void Check()
        {
            if (Volatile.Read(ref stopped) == 0) return;
            cancel.ThrowIfCancellationRequested();
            throw new OperationCanceledException();
        }
        try
        {
            // the connect, in short steps: the game's one pipe instance may be taken by another client for a while
            int connectMs = (int)Math.Min(timeout.TotalMilliseconds, 5000);
            while (true)
            {
                Check();
                int left = connectMs - (int)clock.ElapsedMilliseconds;
                if (left <= 0) throw new TimeoutException();
                try
                {
                    pipe.Connect(Math.Min(left, 100));
                    break;
                }
                catch (TimeoutException) { }
            }
            connected = true;
            Check();
            var bytes = Encoding.UTF8.GetBytes(request);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            Check();
            var first = reader.ReadLine() ?? throw new HotReloadException("the game closed the connection without an answer");
            var accepted = ParseAccepted(first);
            if (!accepted.Accepted || !wantReport) return (accepted, null);
            Check();
            var second = reader.ReadLine() ?? throw new HotReloadException("the game took the request but closed the connection before its report");
            return (accepted, ParseReport(second));
        }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException or IOException or ObjectDisposedException && Volatile.Read(ref stopped) == 1 && !cancel.IsCancellationRequested))
        {
            // the game serves ONE connection at a time (wowcode.dll: one pipe instance): a second client waits for it. 0.13.1:
            // say so plainly -- another WoW Data Studio window or a wds command talking to the same game is the usual reason
            if (!connected) throw new HotReloadException(InUse);
            throw new HotReloadException($"the game did not answer within {timeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancel);
        }
        catch (IOException e)
        {
            throw new HotReloadException("the connection to the game broke: " + e.Message);
        }
        finally
        {
            finished.Set();
            watchdog.Join();
            pipe.Dispose();
            if (self != IntPtr.Zero) CloseHandle(self);
        }
    }

    private const uint ThreadTerminate = 0x0001;   // the right CancelSynchronousIo needs

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelSynchronousIo(IntPtr thread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
