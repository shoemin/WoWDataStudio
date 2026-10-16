using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WoWData.Core.Mpq;
using WoWData.Core.Workspace;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Maps;

/// <summary>Where the server tools and the server's data are.</summary>
public sealed record ServerPaths(string ToolsDir, string ServerDataDir, string ClientDir, string? StagingDir = null, string? BackupRoot = null)
{
    public string Reextract => Path.Combine(ToolsDir, "reextract.ps1");
    public string SwapIn => Path.Combine(ToolsDir, "swap_in.ps1");
    public string Staging => StagingDir ?? Path.Combine(ToolsDir, "staging");
    public string Backups => BackupRoot ?? Path.Combine(ToolsDir, "backups");
}

/// <summary>A line of a running script, with how far the run is (0..1; -1 = unknown).</summary>
public readonly record struct ServerProgress(string Stage, string Line, double Fraction);

public sealed record ScriptResult(int ExitCode, List<string> Output, TimeSpan Time)
{
    public bool Ok => ExitCode == 0;
    public string Text => string.Join(Environment.NewLine, Output);
}

/// <summary>One kind's part of the change report (staging\changes.json written by tools\compare_data.py).</summary>
public sealed class ServerKindReport
{
    public string Kind { get; init; } = "";
    public List<string> Changed { get; init; } = new();
    public List<string> New { get; init; } = new();
    public List<string> OnlyInServer { get; init; } = new();
    public int Identical { get; init; }
    public List<string> Edit { get; init; } = new();
    public List<string> Drift { get; init; } = new();
}

public sealed class ServerReport
{
    public List<ServerKindReport> Kinds { get; } = new();
    public List<string> TouchedTiles { get; } = new();
    public List<string> RebuildTiles { get; } = new();
    public string Reference { get; set; } = "";

    /// <summary>The report in a few plain lines.</summary>
    public List<string> Lines()
    {
        var l = new List<string>();
        foreach (var k in Kinds)
        {
            string what = k.Kind switch { "maps" => "Terrain (maps)", "vmaps" => "Collision and line of sight (vmaps)", "mmaps" => "Pathing (mmaps)", "dbc" => "Data tables (dbc)", _ => k.Kind };
            string extra = k.Edit.Count + k.Drift.Count > 0 ? $"; {k.Edit.Count} from your changes, {k.Drift.Count} only from a newer extractor (safe)" : "";
            l.Add($"{what}: {k.Changed.Count} changed, {k.New.Count} new, {k.Identical} unchanged{extra}.");
        }
        if (TouchedTiles.Count > 0) l.Add($"Tiles whose ground or objects changed: {string.Join(" ", TouchedTiles.Take(12))}{(TouchedTiles.Count > 12 ? " ..." : "")} (compared with {Reference}).");
        if (RebuildTiles.Count > 0) l.Add($"Pathing tiles to rebuild (with their 8 neighbours): {RebuildTiles.Count}.");
        return l;
    }

    public int ToInstall => Kinds.Where(k => k.Kind != "dbc").Sum(k => k.Changed.Count + k.New.Count);

    public static ServerReport? Read(string jsonFile)
    {
        if (!File.Exists(jsonFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            var r = new ServerReport();
            var root = doc.RootElement;
            static List<string> L(JsonElement e, string name) => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
            if (root.TryGetProperty("kinds", out var kinds))
                foreach (var k in kinds.EnumerateObject())
                    r.Kinds.Add(new ServerKindReport
                    {
                        Kind = k.Name, Changed = L(k.Value, "changed"), New = L(k.Value, "new"), OnlyInServer = L(k.Value, "only_in_server"),
                        Identical = k.Value.TryGetProperty("identical", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0,
                        Edit = L(k.Value, "edit"), Drift = L(k.Value, "drift"),
                    });
            r.TouchedTiles.AddRange(L(root, "touched_tiles"));
            r.RebuildTiles.AddRange(L(root, "mmap_rebuild_tiles"));
            r.Reference = root.TryGetProperty("touched_tiles_reference", out var refe) ? refe.GetString() ?? "" : "";
            return r;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>One server update done through the app ("Undo server update" restores it).</summary>
public sealed class ServerSwapRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("backup")] public string Backup { get; set; } = "";
    [JsonPropertyName("server_data")] public string ServerData { get; set; } = "";
    [JsonPropertyName("maps")] public List<int> Maps { get; set; } = new();
    [JsonPropertyName("kinds")] public List<string> Kinds { get; set; } = new();
    [JsonPropertyName("restored")] public DateTime? Restored { get; set; }
}

/// <summary>
/// "Update server maps": the world server reads its own extracted data (Server\data: maps = terrain heights, area and
/// liquids; vmaps = collision and line of sight of buildings and big doodads; mmaps = the pathing navmesh), never the
/// client's archives. This runs the project's server tools (WoWDataTool\server, see its README) as separate programs:
///  1. reextract.ps1 extracts from the game's archives (including patch-3.MPQ, not loose files) into a staging folder and
///     writes a change report (staging\changes.json); it never writes into Server\data;
///  2. after the user stopped the world server, swap_in.ps1 copies only the changed files into Server\data with a
///     verified backup; it refuses while mangosd.exe runs; swap_in.ps1 -Restore &lt;backup&gt; undoes it.
/// The app never stops or starts the server.
/// </summary>
public sealed class ServerMapsService
{
    public ServerPaths Paths { get; }
    /// <summary>The PowerShell to run the scripts with (tests may set another).</summary>
    public string PowerShell { get; set; } = "powershell.exe";

    public ServerMapsService(ServerPaths paths) => Paths = paths;

    /// <summary>WoWDataTool\server next to the app or the game folder (the folder with reextract.ps1).</summary>
    public static string? FindTools(AppSettings settings, string? clientDir)
    {
        if (!string.IsNullOrEmpty(settings.ServerToolsDir)) return File.Exists(Path.Combine(settings.ServerToolsDir, "reextract.ps1")) ? settings.ServerToolsDir : null;
        foreach (var start in new[] { AppContext.BaseDirectory, clientDir })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                foreach (var c in new[] { Path.Combine(d.FullName, "WoWDataTool", "server"), Path.Combine(d.FullName, "server") })
                    if (File.Exists(Path.Combine(c, "reextract.ps1")) && File.Exists(Path.Combine(c, "swap_in.ps1"))) return c;
            }
        }
        return null;
    }

    /// <summary>The server's data folder: the setting, else &lt;game folder&gt;\..\Server\data (the scripts' default).</summary>
    public static string DefaultServerData(AppSettings settings, string clientDir) =>
        !string.IsNullOrEmpty(settings.ServerDataDir) ? settings.ServerDataDir! : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clientDir).TrimEnd('\\'))!, "Server", "data");

    /// <summary>Running world servers ("mangosd.exe pid 1234").</summary>
    public static List<string> RunningWorldServers() =>
        Process.GetProcessesByName("mangosd").Select(p => { try { return $"mangosd.exe (process {p.Id})"; } finally { p.Dispose(); } }).ToList();

    /// <summary>The changed map files of the workspace that the extractors would NOT see: they read the game's
    /// archives (patch-3.MPQ), not loose files. Empty = the installed patch has every map change.</summary>
    public static List<string> MapChangesNotInArchives(Ws ws, string dataDir)
    {
        var missing = new List<string>();
        var maps = ws.ModifiedPaths().Where(p => MapValidator.KindOf(p) != null).ToList();
        if (maps.Count == 0) return missing;
        using var fresh = new ClientData(dataDir);
        foreach (var p in maps)
        {
            var have = fresh.Read(p);
            var mine = ws.ReadEdited(p);
            if (mine == null) continue;
            if (have == null || !have.AsSpan().SequenceEqual(mine)) missing.Add(p);
        }
        return missing;
    }

    /// <summary>The map ids of the workspace's changed map files.</summary>
    public static List<int> ChangedMapIds(Ws ws, MapCatalog catalog)
    {
        var ids = new SortedSet<int>();
        foreach (var p in ws.ModifiedPaths())
        {
            if (MapValidator.KindOf(p) == null) continue;
            var parts = p.Split('\\');
            if (parts.Length >= 3 && catalog.ByDirectory(parts[2]) is { } m) ids.Add(m.Id);
        }
        return ids.ToList();
    }

    // ------------------------------------------------------------------ running the scripts

    private static readonly Regex StageRe = new(@"^\[(dbc|maps|vmaps|mmaps|report)\]", RegexOptions.Compiled);
    private static readonly Regex TileRe = new(@"^\s*\[(\d+)/(\d+)\] map", RegexOptions.Compiled);

    public Task<ScriptResult> ReextractAsync(IReadOnlyCollection<int> maps, IReadOnlyCollection<string> steps, IProgress<ServerProgress>? progress, CancellationToken ct = default)
    {
        var args = new List<string> { "-Client", Paths.ClientDir, "-Staging", Paths.Staging, "-ServerData", Paths.ServerDataDir, "-Steps", string.Join(",", steps) };
        if (maps.Count > 0) { args.Add("-Maps"); args.Add(string.Join(",", maps)); }
        // weights of the stages for the progress bar (measured: maps ~20 s, vmaps ~65 s, mmaps 1-3 min per tile)
        var weights = new Dictionary<string, double> { ["dbc"] = 0.02, ["maps"] = 0.15, ["vmaps"] = 0.4, ["mmaps"] = 0.4, ["report"] = 0.03 };
        var order = new[] { "dbc", "maps", "vmaps", "mmaps", "report" }.Where(s => s == "report" || steps.Contains(s)).ToList();
        double total = order.Sum(s => weights[s]);
        string stage = "starting";
        double done = 0, last = 0;
        return RunAsync(Paths.Reextract, args, line =>
        {
            var m = StageRe.Match(line);
            if (m.Success)
            {
                int i = order.IndexOf(m.Groups[1].Value);
                if (i >= 0)
                {
                    done = order.Take(i).Sum(s => weights[s]) / total;
                    stage = m.Groups[1].Value;
                }
            }
            double frac = done;
            var t = TileRe.Match(line);
            if (t.Success && stage == "mmaps" && int.TryParse(t.Groups[1].Value, out var a) && int.TryParse(t.Groups[2].Value, out var b) && b > 0)
                frac = done + weights["mmaps"] / total * a / b;
            last = Math.Max(last, Math.Clamp(frac, 0, 1));   // never goes back (lines between the tiles of one stage)
            progress?.Report(new ServerProgress(StageName(stage), line, last));
        }, ct);
    }

    public static string StageName(string stage) => stage switch
    {
        "dbc" => "Data tables", "maps" => "Terrain (maps)", "vmaps" => "Collision (vmaps)", "mmaps" => "Pathing (mmaps)", "report" => "Change report", _ => "Starting",
    };

    /// <summary>swap_in.ps1: install the staged changes (or only show the plan with <paramref name="dryRun"/>).</summary>
    public Task<ScriptResult> SwapAsync(IReadOnlyCollection<int> maps, IReadOnlyCollection<string> kinds, bool dryRun, IProgress<ServerProgress>? progress, CancellationToken ct = default)
    {
        var args = new List<string> { "-Staging", Paths.Staging, "-ServerData", Paths.ServerDataDir, "-BackupRoot", Paths.Backups };
        if (kinds.Count > 0) { args.Add("-Kinds"); args.Add(string.Join(",", kinds)); }
        if (maps.Count > 0) { args.Add("-Maps"); args.Add(string.Join(",", maps)); }
        if (dryRun) args.Add("-DryRun");
        return RunAsync(Paths.SwapIn, args, line => progress?.Report(new ServerProgress(dryRun ? "Checking" : "Installing", line, -1)), ct);
    }

    public Task<ScriptResult> RestoreAsync(string backupDir, IProgress<ServerProgress>? progress, CancellationToken ct = default) =>
        RunAsync(Paths.SwapIn, new List<string> { "-ServerData", Paths.ServerDataDir, "-Restore", backupDir }, line => progress?.Report(new ServerProgress("Restoring", line, -1)), ct);

    /// <summary>The backup folder a swap reported ("backup: N entries in &lt;folder&gt;").</summary>
    public static string? BackupOf(ScriptResult r)
    {
        foreach (var l in r.Output)
        {
            var m = Regex.Match(l, @"^backup: \d+ entries in (.+)$");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>Exit code 2 with the refusal text: the world server was running.</summary>
    public static bool RefusedBecauseServerRuns(ScriptResult r) => r.ExitCode == 2 && r.Output.Any(l => l.Contains("REFUSING", StringComparison.Ordinal));

    private async Task<ScriptResult> RunAsync(string script, List<string> args, Action<string> onLine, CancellationToken ct)
    {
        if (!File.Exists(script)) throw new FileNotFoundException($"The server tool {script} was not found. Set the server tools folder in Settings.", script);
        var psi = new ProcessStartInfo(PowerShell)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var output = new List<string>();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Got(string? l)
        {
            if (l == null) return;
            lock (output)
            {
                // stdout and stderr arrive on two threads: one line at a time keeps the stages and the progress in order
                output.Add(l);
                onLine(l);
            }
        }
        p.OutputDataReceived += (_, e) => Got(e.Data);
        p.ErrorDataReceived += (_, e) => Got(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        p.WaitForExit();
        lock (output) return new ScriptResult(p.ExitCode, output.ToList(), sw.Elapsed);
    }

    // ------------------------------------------------------------------ history (for "Undo server update")

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<ServerSwapRecord> LoadHistory(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<ServerSwapRecord>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public static void SaveHistory(string file, List<ServerSwapRecord> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(list, Json));
        File.Move(file + ".tmp", file, true);
    }
}
