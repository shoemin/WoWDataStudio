using System.IO;
using System.Linq;
using WoWData.Core.Maps;

namespace WoWData.Tests;

/// <summary>
/// "Update server maps" against FAKE server data folders only (the live Server\data is never passed to a script):
/// * the progress and the change report of a run (a fake reextract.ps1 that prints the real script's stage lines);
/// * the real swap_in.ps1: it refuses while a world server (mangosd.exe) runs, and leaves the fake data alone;
/// * a copy of swap_in.ps1 with only its mangosd check stubbed (as the server tools' own self-test does): installs the
///   changed files with a verified backup, and "Undo server update" (-Restore) puts everything back;
/// * the real reextract.ps1 extracting terrain from the real client (read only) into a temporary staging folder and
///   comparing with a fake server data folder (made from the stock-client baseline, one tile altered).
/// </summary>
public class ServerUpdateTests
{
    private static string ToolsDir => Path.Combine(Oracle.ToolDir, "server");
    private static bool ToolsPresent => File.Exists(Path.Combine(ToolsDir, "reextract.ps1")) && File.Exists(Path.Combine(ToolsDir, "bin", "mapextractor.exe"));

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), WoWData.Core.Workspace.Workspace.Sha256File)
            : new();

    [Fact]
    public async Task ProgressAndReportOfARun()
    {
        var root = TestEnv.TempDir("srvfake");
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var staging = Path.Combine(root, "staging");
            // a stand-in for reextract.ps1 with the real script's stage lines and report file
            File.WriteAllText(Path.Combine(tools, "reextract.ps1"), """
                param([string]$Client, [string]$Staging, [string]$ServerData, [string[]]$Steps, [string[]]$Maps)
                New-Item -ItemType Directory -Force $Staging | Out-Null
                Write-Host "client     : $Client"
                Write-Host "`n[maps] mapextractor -e 1 -f 0"
                Write-Host "  2429 .map files, 20 s"
                Write-Host "`n[mmaps] MoveMapGen"
                Write-Host "  [1/2] map 0 tile 32,48 done in 61 s"
                Write-Host "  [2/2] map 0 tile 33,48 done in 70 s"
                Write-Host "`n[report]"
                $json = '{"kinds": {"maps": {"changed": ["0004832.map"], "new": [], "only_in_server": [], "identical": 686, "edit": ["0004832.map"], "drift": []},
                          "mmaps": {"changed": ["0004832.mmtile", "0004833.mmtile"], "new": [], "only_in_server": [], "identical": 0}},
                          "touched_tiles_reference": "baseline", "touched_tiles": ["0:32,48"], "mmap_rebuild_tiles": ["0:31,47", "0:32,48"]}'
                Set-Content -LiteralPath (Join-Path $Staging 'changes.json') -Value $json -Encoding UTF8
                Write-Host "Done. Maps=$($Maps -join ',') Steps=$($Steps -join ',')"
                exit 0
                """);
            File.WriteAllText(Path.Combine(tools, "swap_in.ps1"), "exit 0");
            var svc = new ServerMapsService(new ServerPaths(tools, Path.Combine(root, "serverdata"), TestEnv.ClientDir, staging));
            var seen = new List<ServerProgress>();
            var progress = new SyncProgress<ServerProgress>(seen.Add);
            var r = await svc.ReextractAsync(new[] { 0 }, new[] { "maps", "mmaps" }, progress);
            Assert.True(r.Ok, r.Text);
            Assert.Contains(r.Output, l => l.Contains("Done. Maps=0 Steps=maps,mmaps", StringComparison.Ordinal));
            Assert.Contains(seen, p => p.Stage == "Terrain (maps)");
            Assert.Contains(seen, p => p.Stage == "Pathing (mmaps)" && p.Line.Contains("[2/2]"));
            var fr = seen.Select(p => p.Fraction).ToList();
            Assert.True(fr.Zip(fr.Skip(1)).All(p => p.Second >= p.First - 1e-9), "progress never goes back: " + string.Join("\n", seen.Select(p => $"{p.Fraction:0.000} {p.Stage} | {p.Line}")));
            Assert.True(fr.Last() > 0.9, $"last fraction {fr.Last()}");
            var rep = ServerReport.Read(Path.Combine(staging, "changes.json"))!;
            Assert.Equal(3, rep.ToInstall);
            Assert.Equal(new[] { "0:32,48" }, rep.TouchedTiles);
            var lines = rep.Lines();
            Assert.Equal("Terrain (maps): 1 changed, 0 new, 686 unchanged; 1 from your changes, 0 only from a newer extractor (safe).", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("Tiles whose ground or objects changed: 0:32,48", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value)
        {
            lock (this) _a(value);
        }
    }

    /// <summary>A fake server data folder for map 489 (Warsong Gulch) made from the stock baseline, and a staging folder
    /// with one changed and one new tile; returns (serverData, staging).</summary>
    private static (string ServerData, string Staging) FakeServer(string root)
    {
        var baseline = Path.Combine(ToolsDir, "baseline", "maps");
        var server = Directory.CreateDirectory(Path.Combine(root, "serverdata", "maps")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(root, "staging", "maps")).FullName;
        var files = Directory.GetFiles(baseline, "489*.map").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(files.Count >= 4, "the stock-client baseline has the map 489 tiles");
        foreach (var f in files) File.Copy(f, Path.Combine(server, Path.GetFileName(f)));
        foreach (var f in files) File.Copy(f, Path.Combine(staging, Path.GetFileName(f)));
        var changed = Path.Combine(staging, Path.GetFileName(files[1]));
        var bytes = File.ReadAllBytes(changed);
        bytes[^5] ^= 0x55;
        File.WriteAllBytes(changed, bytes);
        File.Delete(Path.Combine(server, Path.GetFileName(files[2])));                 // "new" in staging
        File.Copy(files[0], Path.Combine(server, "4890000.map"));                         // only in the server: removed by the swap
        return (Path.GetDirectoryName(server)!, Path.GetDirectoryName(staging)!);
    }

    [Fact]
    public async Task RealSwapScriptRefusesWhileTheWorldServerRuns()
    {
        if (!ToolsPresent) return;
        var root = TestEnv.TempDir("srvswap");
        try
        {
            var (server, staging) = FakeServer(root);
            var before = Snapshot(server);
            var svc = new ServerMapsService(new ServerPaths(ToolsDir, server, TestEnv.ClientDir, staging, Path.Combine(root, "backups")));
            var dry = await svc.SwapAsync(new[] { 489 }, new[] { "maps" }, true, null);
            Assert.True(dry.Ok, dry.Text);
            Assert.Contains(dry.Output, l => l.Contains("[maps] replace 1, add 1, remove 1", StringComparison.Ordinal));
            Assert.Contains(dry.Output, l => l.StartsWith("DRY RUN: nothing written", StringComparison.Ordinal));
            Assert.Equal(before, Snapshot(server));
            if (ServerMapsService.RunningWorldServers().Count > 0)
            {
                // this machine's world server runs: the real script must refuse and touch nothing
                var r = await svc.SwapAsync(new[] { 489 }, new[] { "maps" }, false, null);
                Assert.Equal(2, r.ExitCode);
                Assert.True(ServerMapsService.RefusedBecauseServerRuns(r), r.Text);
                Assert.Equal(before, Snapshot(server));
                Assert.False(Directory.Exists(Path.Combine(root, "backups")));
                var undo = await svc.RestoreAsync(Path.Combine(root, "nothing"), null);
                Assert.True(ServerMapsService.RefusedBecauseServerRuns(undo), undo.Text);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SwapInstallsWithBackupAndUndoRestores()
    {
        if (!ToolsPresent) return;
        var root = TestEnv.TempDir("srvstub");
        try
        {
            var (server, staging) = FakeServer(root);
            var before = Snapshot(server);
            // a copy of swap_in.ps1 whose mangosd check is stubbed (the real script is unchanged), reading the real tools
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var src = File.ReadAllText(Path.Combine(ToolsDir, "swap_in.ps1"));
            Assert.Equal(1, src.Split("function Test-MangosRunning {").Length - 1);
            src = src.Replace("function Test-MangosRunning {", "function Test-MangosRunning {\n    return ''   # STUB for the test copy only\n}\nfunction Test-MangosRunning-Real {")
                     .Replace("$here = $PSScriptRoot", $"$here = '{ToolsDir}'");
            File.WriteAllText(Path.Combine(tools, "swap_in.ps1"), src);
            File.WriteAllText(Path.Combine(tools, "reextract.ps1"), "exit 0");
            var backups = Path.Combine(root, "backups");
            var svc = new ServerMapsService(new ServerPaths(tools, server, TestEnv.ClientDir, staging, backups));
            var r = await svc.SwapAsync(new[] { 489 }, new[] { "maps" }, false, null);
            Assert.True(r.Ok, r.Text);
            var backup = ServerMapsService.BackupOf(r);
            Assert.NotNull(backup);
            Assert.StartsWith(backups, backup!);
            Assert.True(File.Exists(Path.Combine(backup!, "manifest.json")));
            var after = Snapshot(server);
            foreach (var f in Directory.GetFiles(Path.Combine(staging, "maps")))
                Assert.Equal(WoWData.Core.Workspace.Workspace.Sha256File(f), after[Path.Combine("maps", Path.GetFileName(f))]);
            Assert.False(after.ContainsKey(Path.Combine("maps", "4890000.map")));
            // undo
            var u = await svc.RestoreAsync(backup!, null);
            Assert.True(u.Ok, u.Text);
            Assert.Equal(before, Snapshot(server));
            // the history file of the app
            var hist = Path.Combine(root, "server_updates.json");
            ServerMapsService.SaveHistory(hist, new List<ServerSwapRecord> { new() { Time = DateTime.Now, Backup = backup!, Maps = { 489 }, Kinds = { "maps" } } });
            Assert.Equal(489, ServerMapsService.LoadHistory(hist)[0].Maps[0]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RealReextractIntoStagingAgainstFakeServerData()
    {
        if (!ToolsPresent) return;
        var before = TestEnv.DataSnapshot();
        var root = TestEnv.TempDir("srvreal");
        try
        {
            var (server, _) = FakeServer(root);
            var staging = Path.Combine(root, "staging_real");
            var svc = new ServerMapsService(new ServerPaths(ToolsDir, server, TestEnv.ClientDir, staging));
            var stages = new List<string>();
            var r = await svc.ReextractAsync(new[] { 489 }, new[] { "maps" }, new SyncProgress<ServerProgress>(p => stages.Add(p.Stage)));
            Assert.True(r.Ok, r.Text);
            Assert.Contains("Terrain (maps)", stages);
            Assert.Contains("Change report", stages);
            Assert.True(Directory.GetFiles(Path.Combine(staging, "maps"), "*.map").Length > 2000);
            var rep = ServerReport.Read(Path.Combine(staging, "changes.json"))!;
            var maps = rep.Kinds.Single(k => k.Kind == "maps");
            // the fake server lacks one tile (new), and has one extra (only in the server); the rest match the stock client
            Assert.Single(maps.New);
            Assert.Contains("4890000.map", maps.OnlyInServer);
            Assert.Empty(maps.Changed);
            Assert.True(maps.Identical >= 3);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Assert.Equal(before, TestEnv.DataSnapshot());
    }
}
