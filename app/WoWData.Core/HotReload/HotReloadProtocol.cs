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
    public static async Task<(HotReloadAccepted Accepted, HotReloadReport? Report)> SendAsync(string pipeName, string request, bool wantReport, TimeSpan timeout,
        CancellationToken cancel = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)Math.Min(timeout.TotalMilliseconds, 5000), cts.Token);
            var bytes = Encoding.UTF8.GetBytes(request);
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var first = await reader.ReadLineAsync(cts.Token) ?? throw new HotReloadException("the game closed the connection without an answer");
            var accepted = ParseAccepted(first);
            if (!accepted.Accepted || !wantReport) return (accepted, null);
            var second = await reader.ReadLineAsync(cts.Token) ?? throw new HotReloadException("the game took the request but closed the connection before its report");
            return (accepted, ParseReport(second));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new HotReloadException($"the game did not answer within {timeout.TotalSeconds:0} seconds");
        }
        catch (TimeoutException)
        {
            // the game serves ONE connection at a time (wowcode.dll: one pipe instance): a second client waits here. 0.13.1:
            // say so plainly -- another WoW Data Studio window or a wds command talking to the same game is the usual reason
            throw new HotReloadException("its hot-reload connection is in use: another WoW Data Studio window or a wds command is talking to this game right now (it takes one at a time), or the game just closed it");
        }
        catch (IOException e)
        {
            throw new HotReloadException("the connection to the game broke: " + e.Message);
        }
    }
}
