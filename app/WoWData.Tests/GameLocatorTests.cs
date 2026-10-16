using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using WoWData.Core.HotReload;

namespace WoWData.Tests;

/// <summary>
/// 0.13.1: finding the running game of a folder (hot reload's "is the game running?"). The in-game test of 2026-10-14 found
/// that 0.13.0 never found the game started by WoWDebug.exe: that 32-bit WoW.exe refuses OpenProcess (even the limited
/// query right), so its program path was null and the game was skipped ("The game is not running from ..."). These tests
/// start a real 32-bit process named WoW.exe whose security descriptor refuses everyone (as the game's does), and check the
/// query layer with a fake.
/// </summary>
public class GameLocatorTests
{
    // ------------------------------------------------------------------ the real thing: a 32-bit WoW.exe that cannot be opened

    private static string? Wow64Cmd()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "cmd.exe");
        return Environment.Is64BitOperatingSystem && File.Exists(p) ? p : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint securityInformation, byte[] securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    /// <summary>Starts a copy of the 32-bit cmd.exe as &lt;dir&gt;\WoW.exe (it waits for input), with an empty DACL: nobody
    /// may open it, like the game under WoWDebug.exe.</summary>
    private static Process StartLockedWow(string dir, string cmd32)
    {
        var exe = Path.Combine(dir, "WoW.exe");
        File.Copy(cmd32, exe);
        var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        try
        {
            Assert.True(IsWow64Process(p.Handle, out var wow64) && wow64, "the test process is not a 32-bit process");
            var sd = new RawSecurityDescriptor("D:");   // an empty DACL: no access for anyone (the owner keeps only READ_CONTROL / WRITE_DAC)
            var bin = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bin, 0);
            const uint DaclSecurityInformation = 4;
            Assert.True(SetKernelObjectSecurity(p.Handle, DaclSecurityInformation, bin), "could not lock the test process: " + Marshal.GetLastWin32Error());
            return p;
        }
        catch
        {
            Stop(p);
            throw;
        }
    }

    private static void Stop(Process p)
    {
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    [Fact]
    public async Task AThirtyTwoBitGameThatCannotBeOpenedIsFound()
    {
        var cmd32 = Wow64Cmd();
        if (cmd32 == null) return;   // a 32-bit Windows: no WOW64 (the app is 64-bit only)
        var dir = TestEnv.TempDir("locator_locked");
        var other = TestEnv.TempDir("locator_other");
        var p = StartLockedWow(dir, cmd32);
        try
        {
            var exe = Path.Combine(dir, "WoW.exe");
            // the 0.13.0 way gives nothing (unless the tests run with the debug privilege switched on)
            var byHandle = ProcessImage.ByHandle(p.Id);
            if (byHandle != null) Assert.Equal(exe, byHandle, ignoreCase: true);
            // the system's process list still knows the program
            var nt = ProcessImage.NtPath(p.Id);
            Assert.NotNull(nt);
            Assert.Equal(exe, ProcessImage.FromNtPath(nt!, ProcessImage.DriveDevices()), ignoreCase: true);
            Assert.Equal(exe, ProcessImage.PathOf(p.Id), ignoreCase: true);

            // the locator finds it in its folder (any spelling of the folder), with WoW.exe's own pid for the pipe
            foreach (var spelling in new[] { dir, dir + "\\", dir.ToUpperInvariant(), dir.ToLowerInvariant() + "/" })
            {
                var games = WindowsGameLocator.Instance.Find(spelling);
                var g = Assert.Single(games.Where(x => x.Pid == p.Id));
                Assert.Equal(exe, g.ExePath, ignoreCase: true);
            }
            Assert.Equal("wowcode-hotreload-" + p.Id, WindowsGameLocator.Instance.PipeName(p.Id));
            // and not in another folder
            Assert.DoesNotContain(WindowsGameLocator.Instance.Find(other), g => g.Pid == p.Id);

            // the service no longer says "the game is not running": this folder has no wowcode.dll, so it says why
            var probe = await new HotReloadService(dir).ProbeAsync(false);
            Assert.NotEqual(HotReloadState.NoGame, probe.State);
            Assert.Equal(p.Id, probe.Game!.Pid);
            Assert.Equal(HotReloadState.NoSupport, probe.State);
            Assert.Equal(HotReloadState.NoGame, (await new HotReloadService(other).ProbeAsync(false)).State);

            // WdbCache's "close the game first" check sees it too (and only in its folder)
            Assert.Contains(WoWData.Core.ServerData.WdbCache.RunningGames(dir), s => s.Contains($"process {p.Id}"));
            Assert.DoesNotContain(WoWData.Core.ServerData.WdbCache.RunningGames(other), s => s.Contains($"process {p.Id}"));
        }
        finally
        {
            Stop(p);
            try { Directory.Delete(dir, true); Directory.Delete(other, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ThisProcessIsReadTheSameWayBothWays()
    {
        using var me = Process.GetCurrentProcess();
        var byHandle = ProcessImage.ByHandle(me.Id);
        Assert.NotNull(byHandle);
        Assert.Equal(byHandle, ProcessImage.FromNtPath(ProcessImage.NtPath(me.Id)!, ProcessImage.DriveDevices()), ignoreCase: true);
        Assert.Null(ProcessImage.PathOf(0x7FFFFFFC));   // no such process
    }

    // ------------------------------------------------------------------ a second app talking to the same game

    private sealed class PipeLocator : IGameLocator
    {
        private readonly string _pipe;
        public PipeLocator(string pipe) => _pipe = pipe;
        public IReadOnlyList<GameProcess> Find(string clientDir) => new[] { new GameProcess(4242, Path.Combine(clientDir, "WoW.exe"), DateTime.Now) };
        public string PipeName(int pid) => _pipe;
        public bool PipeExists(int pid) => true;
    }

    [Fact]
    public async Task AnotherAppHoldingTheGamesConnectionIsNamedNotNoGame()
    {
        // the game's pipe has ONE instance (as wowcode.dll's); another WoW Data Studio / wds holds it
        var name = "wds-test-busy-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        using var other = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
        await other.ConnectAsync(5000);
        await accept;
        var dir = TestEnv.TempDir("locator_busy");
        var svc = new HotReloadService(dir, new PipeLocator(name)) { StatusTimeout = TimeSpan.FromSeconds(2) };
        var probe = await svc.ProbeAsync(true);
        Assert.Equal(HotReloadState.NotResponding, probe.State);
        Assert.Contains("another WoW Data Studio window or a wds command", probe.Message);
        var outcome = await svc.ReloadAsync(new[] { @"Creature\Boar\BoarSkinIvory.blp" }, timeout: TimeSpan.FromSeconds(2));
        Assert.False(outcome.Done);
        Assert.NotEqual(HotReloadState.NoGame, outcome.Probe.State);
        Assert.Contains("another WoW Data Studio window", outcome.Message);
        Directory.Delete(dir, true);
    }

    // ------------------------------------------------------------------ NT paths

    [Theory]
    [InlineData(@"\Device\HarddiskVolume4\Users\x\Client_visual\WoW.exe", @"C:\Users\x\Client_visual\WoW.exe")]
    [InlineData(@"\Device\HarddiskVolume10\Games\WoW.exe", @"D:\Games\WoW.exe")]   // not HarddiskVolume1 + "0"
    [InlineData(@"\Device\HarddiskVolume1\WoW.exe", @"E:\WoW.exe")]
    [InlineData(@"\Device\Mup\server\share\Client\WoW.exe", @"\\server\share\Client\WoW.exe")]
    [InlineData(@"\??\C:\Client\WoW.exe", @"C:\Client\WoW.exe")]
    [InlineData(@"C:\Client\WoW.exe", @"C:\Client\WoW.exe")]
    [InlineData(@"\Device\Unknown\WoW.exe", null)]
    public void NtPathsBecomeDrivePaths(string nt, string? expected)
    {
        var drives = new[] { ("E:", @"\Device\HarddiskVolume1"), ("C:", @"\Device\HarddiskVolume4"), ("D:", @"\Device\HarddiskVolume10") };
        Assert.Equal(expected, ProcessImage.FromNtPath(nt, drives));
    }

    // ------------------------------------------------------------------ the query layer, faked

    private sealed class FakeQuery : IProcessQuery
    {
        public readonly Dictionary<int, (string? Exe, DateTime? Started)> Wow = new();
        public IReadOnlyList<int> FindByName(string name) => name == "WoW" ? Wow.Keys.ToList() : Array.Empty<int>();
        public string? ImagePath(int pid) => Wow.TryGetValue(pid, out var w) ? w.Exe : null;
        public DateTime? StartTime(int pid) => Wow.TryGetValue(pid, out var w) ? w.Started : null;
    }

    [Fact]
    public void TheLocatorMatchesTheFolderAndPutsTheNewestGameFirst()
    {
        var dir = TestEnv.TempDir("locator_fake");
        var q = new FakeQuery();
        q.Wow[100] = (Path.Combine(dir, "WoW.exe"), new DateTime(2026, 10, 14, 21, 0, 0));
        q.Wow[200] = (Path.Combine(dir.ToUpperInvariant(), "WoW.exe"), new DateTime(2026, 10, 14, 22, 0, 0));
        q.Wow[300] = (null, null);                                                   // unreadable: not this folder's
        q.Wow[400] = (Path.Combine(dir, "sub", "WoW.exe"), DateTime.Now);            // another folder
        q.Wow[500] = (Path.Combine(dir, "WoW.exe"), null);                           // no start time: last
        var loc = new WindowsGameLocator(q);
        Assert.Equal(new[] { 200, 100, 500 }, loc.Find(dir + "\\").Select(g => g.Pid).ToArray());
        Assert.Empty(loc.Find(Path.Combine(dir, "other")));
        Assert.Equal("wowcode-hotreload-200", loc.PipeName(loc.Find(dir)[0].Pid));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ShortNamesAreTheSameFolder()
    {
        var parent = TestEnv.TempDir("locator_short");
        var dir = Path.Combine(parent, "WoW Custom Client long name");
        Directory.CreateDirectory(dir);
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            if (GetShortPathName(dir, sb, (uint)sb.Capacity) == 0 || !sb.ToString().Contains('~')) return;   // 8.3 names are off on this volume
            var q = new FakeQuery();
            q.Wow[42] = (Path.Combine(sb.ToString(), "WoW.exe"), DateTime.Now);
            Assert.Equal(42, Assert.Single(new WindowsGameLocator(q).Find(dir)).Pid);
        }
        finally { Directory.Delete(parent, true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint max);
}
