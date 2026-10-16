using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WoWData.Core.HotReload;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.11.0: hot reload ("Reload in running game", wds changes reload). The game side (wowcode.dll, staged as HR1 in WoWDecomp) is
/// replaced by <see cref="FakeGame"/> (the protocol over a real named pipe) and by the replies the real native code gave in its
/// offline harness (hotreload\native_replies.jsonl); the game is never started and the real game folder is never written.
/// </summary>
public class HotReloadTests
{
    /// <summary>A game process with no pipe (a game without hot reload, or switched off).</summary>
    private sealed class NoPipeLocator : IGameLocator
    {
        private readonly string _dir;
        public NoPipeLocator(string dir) => _dir = Path.GetFullPath(dir);
        public int Pid => 31337;
        public IReadOnlyList<GameProcess> Find(string clientDir) =>
            string.Equals(Path.GetFullPath(clientDir), _dir, StringComparison.OrdinalIgnoreCase) ? new[] { new GameProcess(Pid, Path.Combine(_dir, "WoW.exe"), DateTime.Now) } : Array.Empty<GameProcess>();
        public string PipeName(int pid) => "wds-no-such-pipe-" + pid;
        public bool PipeExists(int pid) => false;
    }

    private static string GameDir(string name)
    {
        var d = TestEnv.TempDir("hr_" + name);
        Directory.CreateDirectory(Path.Combine(d, "Data"));
        return d;
    }

    /// <summary>A change in the workspace: BoarSkinIvory.blp exported, tinted red and imported (as in CliTests).</summary>
    private static void ImportEditedBoar(CliTestEnv env)
    {
        var png = Path.Combine(env.Work, "boar.png");
        Assert.Equal(0, env.Run("textures", "export", @"Creature\Boar\BoarSkinIvory.blp", "--out", png).Code);
        var img = WoWData.Core.Imaging.Png.Load(png);
        for (int i = 0; i < img.Pixels.Length; i += 4) if (img.Pixels[i + 3] > 0) img.Pixels[i] = 255;
        WoWData.Core.Imaging.Png.Save(img, png, true);
        Assert.Equal(0, env.Run("textures", "import", png).Code);
    }

    private static string[] NativeReplies() =>
        File.ReadAllLines(Path.Combine(Oracle.AppDir, "WoWData.Tests", "hotreload", "native_replies.jsonl")).Where(l => l.Length > 0).ToArray();

    // ------------------------------------------------------------------ the protocol, against what the native code really sends

    [Fact]
    public void TheNativeRepliesParse()
    {
        var r = NativeReplies();
        Assert.Equal(8, r.Length);
        Assert.True(HotReloadProtocol.ParseAccepted(r[0]).Accepted);
        var status = HotReloadProtocol.ParseReport(r[1]);
        Assert.Equal("status", status.Command);
        Assert.True(status.Ok && status.Enabled && status.MainThread == true && status.InWorld == true && status.LooseFiles == true);
        Assert.Equal(1, status.Options!["units"]);

        var reload = HotReloadProtocol.ParseReport(r[3]);
        Assert.Equal("reload", reload.Command);
        Assert.True(reload.Restreamed);
        Assert.Equal(7, reload.Files.Count);
        Assert.Equal(ReloadResult.Reloaded, reload.Files.Single(f => f.Path == @"Creature\Boar\BoarSkinIvory.blp").Result);
        Assert.Equal(ReloadResult.NeedsRelog, reload.Files.Single(f => f.Path == @"Creature\Raptor\RaptorSkin.blp").Result);
        Assert.Equal(ReloadResult.NeedsRestart, reload.Files.Single(f => f.Path == @"DBFilesClient\Light.dbc").Result);
        Assert.Equal(ReloadResult.NeedsUiReload, reload.Files.Single(f => f.Path.EndsWith(".lua")).Result);
        Assert.Equal(reload.Files.Count(f => f.Result == ReloadResult.Reloaded), reload.Summary!.Reloaded);

        var release = HotReloadProtocol.ParseReport(r[5]);
        Assert.Equal("released", release.Result);
        var remount = HotReloadProtocol.ParseReport(r[7]);
        Assert.Equal("remounted", remount.Archive!.Result);
        Assert.Equal("patch-3.MPQ", remount.Archive.Name);
    }

    [Fact]
    public void TheReportIsDescribedInWords()
    {
        var reload = HotReloadProtocol.ParseReport(NativeReplies()[3]);
        var text = HotReloadService.Describe(reload);
        Assert.Contains("Live in the game now", text);
        Assert.Contains("Needs a relog", text);
        Assert.Contains("RaptorSkin.blp", text);
        Assert.Contains("Needs a restart of the game: Light.dbc", text);
        Assert.Contains("reloaded (like after a teleport)", text);
        var details = HotReloadService.Details(reload);
        Assert.Contains(@"live now: Creature\Boar\BoarSkinIvory.blp", details);
        Assert.Contains(@"restart: DBFilesClient\Light.dbc", details);
    }

    [Fact]
    public void RequestsAreTheTextTheGameReads()
    {
        var req = HotReloadProtocol.ReloadRequest(new[] { "Creature/Boar/Boar.m2", @"Creature\Boar\Boar.m2", @"Creature\Boar\BoarSkinIvory.blp" }, "patch-3.MPQ", 5000);
        Assert.Equal("HOTRELOAD 1\nreload\nremount patch-3.MPQ\nfile Creature\\Boar\\Boar.m2\nfile Creature\\Boar\\BoarSkinIvory.blp\nwait 5000\nend\n", req);
        Assert.Equal("HOTRELOAD 1\nstatus\nend\n", HotReloadProtocol.StatusRequest());
        Assert.Throws<ArgumentException>(() => HotReloadProtocol.ReloadRequest(new[] { "a\nrelease patch-3.MPQ" }));
        Assert.Throws<HotReloadException>(() => HotReloadProtocol.ParseReport("not json"));
    }

    // ------------------------------------------------------------------ the switch in the game folder

    [Fact]
    public void TheSwitchIsAFileInTheGameFolder()
    {
        var dir = GameDir("flag");
        Assert.False(HotReloadFlag.IsEnabled(dir));
        HotReloadFlag.SetEnabled(dir, true);
        Assert.True(HotReloadFlag.IsEnabled(dir));
        Assert.Contains("enabled 1", File.ReadAllText(Path.Combine(dir, "wowhotreload.txt")));
        File.WriteAllText(Path.Combine(dir, "wowhotreload.txt"), "# off\nenabled 0\n");
        Assert.False(HotReloadFlag.IsEnabled(dir));
        HotReloadFlag.SetEnabled(dir, false);
        Assert.False(File.Exists(Path.Combine(dir, "wowhotreload.txt")));

        Assert.False(HotReloadFlag.DllPresent(dir));
        var dll = new byte[3_000_000];
        new Random(1).NextBytes(dll);
        File.WriteAllBytes(Path.Combine(dir, "wowcode.dll"), dll);
        Assert.False(HotReloadFlag.DllSupports(dir));
        var marker = Encoding.ASCII.GetBytes(HotReloadFlag.DllMarker);
        Array.Copy(marker, 0, dll, (1 << 20) - 5, marker.Length);   // across a read boundary
        File.WriteAllBytes(Path.Combine(dir, "wowcode.dll"), dll.Append((byte)0).ToArray());   // another size: the answer is cached per file version
        Assert.True(HotReloadFlag.DllSupports(dir));

        File.WriteAllText(Path.Combine(dir, "wowcode.log"), "[     0] wowcode.dll loaded at 10000000 into pid 777; 25073 functions can be hooked\n[    20] hotreload: off (no wowhotreload.txt)\n");
        Assert.Null(HotReloadFlag.LogLinesFor(dir, 778));
        Assert.Equal(new[] { "hotreload: off (no wowhotreload.txt)" }, HotReloadFlag.LogLinesFor(dir, 777));
    }

    // ------------------------------------------------------------------ what the app says about the running game

    [Fact]
    public async Task TheStateOfTheGameIsExplained()
    {
        var dir = GameDir("probe");
        var other = GameDir("other");
        using (var game = new FakeGame(other))
            Assert.Equal(HotReloadState.NoGame, (await new HotReloadService(dir, game).ProbeAsync()).State);

        var noPipe = new NoPipeLocator(dir);
        var p = await new HotReloadService(dir, noPipe).ProbeAsync();
        Assert.Equal(HotReloadState.NoSupport, p.State);   // no wowcode.dll: a normal client
        Assert.Contains("normal client", p.Message);

        File.WriteAllBytes(Path.Combine(dir, "wowcode.dll"), Encoding.ASCII.GetBytes("MZ ... " + HotReloadFlag.DllMarker + " ..."));
        p = await new HotReloadService(dir, noPipe).ProbeAsync();
        Assert.Equal(HotReloadState.Off, p.State);
        Assert.Contains("Settings", p.Message);

        HotReloadFlag.SetEnabled(dir, true);
        File.WriteAllText(Path.Combine(dir, "wowcode.log"), $"[ 0] wowcode.dll loaded at 10000000 into pid {noPipe.Pid}; 1 functions can be hooked\n[ 9] hotreload: off (no wowhotreload.txt with `enabled 1` next to wowcode.dll)\n");
        Assert.Equal(HotReloadState.RestartGame, (await new HotReloadService(dir, noPipe).ProbeAsync()).State);

        File.WriteAllText(Path.Combine(dir, "wowcode.log"), $"[ 0] wowcode.dll loaded at 10000000 into pid {noPipe.Pid}; 1 functions can be hooked\n");
        File.WriteAllBytes(Path.Combine(dir, "wowcode.dll"), Encoding.ASCII.GetBytes("MZ an older DLL"));
        p = await new HotReloadService(dir, noPipe).ProbeAsync();
        Assert.Equal(HotReloadState.NoSupport, p.State);
        Assert.Contains("older than the HR1 update", p.Message);

        using (var game = new FakeGame(dir))
        {
            p = await new HotReloadService(dir, game).ProbeAsync();
            Assert.Equal(HotReloadState.Ready, p.State);
            Assert.True(p.Ready);
            Assert.Equal(game.Pid, p.Game!.Pid);
            Assert.True(p.Status!.InWorld);
            Assert.Contains("takes reloads", p.Short);
            game.NeverAnswer = true;
            p = await new HotReloadService(dir, game) { StatusTimeout = TimeSpan.FromSeconds(1) }.ProbeAsync();
            Assert.Equal(HotReloadState.NotResponding, p.State);
        }
    }

    // ------------------------------------------------------------------ reloads

    [Fact]
    public async Task AReloadReportsWhatIsLiveAndWhatNeedsARelog()
    {
        var dir = GameDir("reload");
        using var game = new FakeGame(dir) { RestartExtension = "ttf" };
        var hr = new HotReloadService(dir, game);
        var o = await hr.ReloadAsync(new[] { @"Creature\Boar\BoarSkinIvory.blp", "World/Maps/Kalimdor/Kalimdor.wdt", @"Fonts\FRIZQT__.TTF", @"Interface\AddOns\A\A.lua" });
        Assert.True(o.Done);
        Assert.Equal(4, o.Report!.Files.Count);
        Assert.Single(o.Report.Live);
        Assert.Equal(2, o.Report.Relog.Count());
        Assert.Single(o.Report.Restart);
        Assert.Contains(@"file World\Maps\Kalimdor\Kalimdor.wdt", game.Requests.Last());
        Assert.Contains("Needs a restart of the game: FRIZQT__.TTF", o.Text);

        var quick = await hr.ReloadAsync(new[] { @"Creature\Boar\Boar.m2" }, wait: false);
        Assert.True(quick.Done);
        Assert.Null(quick.Report);
        Assert.Contains("took reload request", quick.Message);
    }

    [Fact]
    public async Task AGameThatDoesNotAnswerIsReportedNotWaitedForever()
    {
        var dir = GameDir("frozen");
        using var game = new FakeGame(dir);
        var hr = new HotReloadService(dir, game);
        Assert.True((await hr.ProbeAsync(false)).Ready);
        game.NeverAnswer = true;
        var sw = Stopwatch.StartNew();
        var o = await hr.ReloadAsync(new[] { @"Creature\Boar\Boar.m2" }, timeout: TimeSpan.FromSeconds(4));
        Assert.False(o.Done);
        Assert.Contains("did not answer", o.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    // ------------------------------------------------------------------ the release patch while the game runs

    [Fact]
    public void ThePatchIsReplacedWhileTheGameRuns()
    {
        using var env = new CliTestEnv("hr_patch");
        ImportEditedBoar(env);
        Assert.Equal(0, env.Run("changes", "build-patch", "--install", "--yes", "--quiet").Code);
        var patch = Path.Combine(env.DataDir, "patch-3.MPQ");
        var first = File.ReadAllBytes(patch);

        using var game = new FakeGame(env.Client, holdPatch: true);
        Assert.True(game.HoldsPatch);
        // without hot reload the game's lock makes the install fail, and nothing is changed
        using (HotReloadService.UseLocator(new NoPipeLocator(env.Client)))
        {
            var r = env.Json("changes", "build-patch", "--install", "--yes");
            Assert.NotEqual(0, r.Code);
            Assert.Contains("Is the game running", r.Json.GetProperty("error").GetString());
        }
        // with hot reload: released, installed, re-mounted, reloaded
        using (HotReloadService.UseLocator(game))
        {
            game.RefuseReleases = 1;   // "in use" once: asked again
            var r = env.Json("changes", "build-patch", "--install", "--yes");
            Assert.True(r.Code == 0, r.ToString());
            var reload = r.Data.GetProperty("reload");
            Assert.True(reload.GetProperty("done").GetBoolean());
            Assert.Equal("remounted", reload.GetProperty("report").GetProperty("archive").GetProperty("result").GetString());
            Assert.Contains("BoarSkinIvory.blp", reload.GetProperty("live").EnumerateArray().Select(e => e.GetString()).Single());
            Assert.Contains("The running game was updated (hot reload)", r.Out);
            Assert.True(game.HoldsPatch);
            Assert.Contains(game.Requests, q => q.Contains("release patch-3.MPQ"));
            Assert.Contains(game.Requests, q => q.Contains("remount patch-3.MPQ"));

            game.RefuseReleases = 100;   // a file of it stays open: nothing is written
            var before = File.ReadAllBytes(patch);
            r = env.Json("changes", "build-patch", "--install", "--yes");
            Assert.NotEqual(0, r.Code);
            Assert.Contains("could not let go of patch-3.MPQ", r.Json.GetProperty("error").GetString());
            Assert.Equal(before, File.ReadAllBytes(patch));
            game.RefuseReleases = 0;

            r = env.Json("changes", "uninstall-patch", "--yes");
            Assert.True(r.Code == 0, r.ToString());
            Assert.False(File.Exists(patch));
            Assert.Equal("removed", r.Data.GetProperty("reload").GetProperty("report").GetProperty("archive").GetProperty("result").GetString());
        }
        Assert.NotEmpty(first);
    }

    // ------------------------------------------------------------------ the command line

    [Fact]
    public void TheCommandLineReloadsAndExplains()
    {
        using var env = new CliTestEnv("hr_cli");
        ImportEditedBoar(env);

        // no game running from the folder
        using (HotReloadService.UseLocator(new FakeGame(GameDir("elsewhere"))))
        {
            var r = env.Json("changes", "reload", "--wait");
            Assert.Equal(6, r.Code);
            Assert.Contains("not running", r.Json.GetProperty("error").GetString());
        }
        // a game, switched off
        File.WriteAllBytes(Path.Combine(env.Client, "wowcode.dll"), Encoding.ASCII.GetBytes("MZ " + HotReloadFlag.DllMarker));
        using (HotReloadService.UseLocator(new NoPipeLocator(env.Client)))
        {
            var r = env.Json("changes", "reload", "--status");
            Assert.Equal(4, r.Code);
            Assert.Equal("off", r.Data.GetProperty("state").GetString());
        }
        // the switch (in the fake game folder; --yes because it writes there)
        Assert.Equal(5, env.Run("settings", "set", "hot-reload", "on").Code);
        Assert.False(File.Exists(Path.Combine(env.Client, "wowhotreload.txt")));
        Assert.Equal(0, env.Run("settings", "set", "hot-reload", "on", "--yes").Code);
        Assert.True(HotReloadFlag.IsEnabled(env.Client));
        Assert.Equal("on", env.Json("settings", "get", "hot-reload").Data.GetProperty("value").GetString());
        Assert.Contains(env.Json("settings", "list").Data.GetProperty("settings").EnumerateArray(), s => s.GetProperty("key").GetString() == "hot-reload" && s.GetProperty("value").GetString() == "on");

        using var game = new FakeGame(env.Client);
        using (HotReloadService.UseLocator(game))
        {
            var st = env.Json("changes", "reload", "--status");
            Assert.True(st.Code == 0, st.ToString());
            Assert.Equal("ready", st.Data.GetProperty("state").GetString());

            var r = env.Json("changes", "reload", "--wait");   // default: the workspace's changes
            Assert.True(r.Code == 0, r.ToString());
            Assert.True(r.Data.GetProperty("done").GetBoolean());
            Assert.Equal(@"Creature\Boar\BoarSkinIvory.blp", r.Data.GetProperty("live").EnumerateArray().Single().GetString());
            Assert.Contains(@"file Creature\Boar\BoarSkinIvory.blp", game.Requests.Last());

            var quick = env.Run("changes", "reload", @"World\Maps\Kalimdor\Kalimdor.wdt");
            Assert.Equal(0, quick.Code);
            Assert.Contains("took reload request", quick.Out);

            var text = env.Run("changes", "reload", "--wait", @"Interface\AddOns\A\A.lua");
            Assert.Equal(0, text.Code);
            Assert.Contains("/reload", text.Out);

            var inst = env.Json("changes", "install", "--yes", "--reload");
            Assert.True(inst.Code == 0, inst.ToString());
            Assert.True(inst.Data.GetProperty("reload").GetProperty("done").GetBoolean());
            Assert.True(File.Exists(Path.Combine(env.DataDir, "Creature", "Boar", "BoarSkinIvory.blp")));

            var rem = env.Json("changes", "remove", "--yes", "--reload");
            Assert.True(rem.Code == 0, rem.ToString());
            Assert.Contains(@"file Creature\Boar\BoarSkinIvory.blp", game.Requests.Last());

            Assert.Equal(0, env.Run("changes", "install", "--yes").Code);
            int before = game.Requests.Count;
            var rv = env.Json("changes", "revert", @"Creature\Boar\BoarSkinIvory.blp", "--yes", "--reload");
            Assert.True(rv.Code == 0, rv.ToString());
            Assert.True(rv.Data.GetProperty("reload").GetProperty("done").GetBoolean());
            Assert.True(game.Requests.Count > before);
            Assert.False(File.Exists(Path.Combine(env.DataDir, "Creature", "Boar", "BoarSkinIvory.blp")));
        }
        Assert.Equal(0, env.Run("settings", "unset", "hot-reload", "--yes").Code);
        Assert.False(HotReloadFlag.IsEnabled(env.Client));
    }

    /// <summary>Parity: the app's result dialog (HotReloadOutcome.Text / HotReloadService.Details, as BrowserViewModel shows them) and
    /// wds print the same words for the same reload.</summary>
    [Fact]
    public async Task TheAppAndTheCommandLineSayTheSame()
    {
        using var env = new CliTestEnv("hr_parity");
        ImportEditedBoar(env);
        using var game = new FakeGame(env.Client);
        var paths = new[] { @"Creature\Boar\BoarSkinIvory.blp", @"World\Maps\Kalimdor\Kalimdor.wdt" };
        var app = await new HotReloadService(env.Client, game).ReloadAsync(paths);   // what "Reload in running game" does
        Assert.True(app.Done);
        using (HotReloadService.UseLocator(game))
        {
            var cli = env.Run(new[] { "changes", "reload", "--wait" }.Concat(paths).ToArray());
            Assert.Equal(0, cli.Code);
            foreach (var line in app.Text.Split('\n')) Assert.Contains(line.Trim(), cli.Out);
            foreach (var f in app.Report!.Files) Assert.Contains($"{HotReloadService.Label(f.Result),-22} {f.Path}", cli.Out);
        }
    }

    // ------------------------------------------------------------------ the real native code (when its offline harness is built)

    /// <summary>Runs only with WDS_HR_HARNESS = the path of hr_harness.exe (WoWDecomp\work\staged\HR1-hotreload\test\build_harness.bat):
    /// the app's client against the real hooks\hotreload.c in its fake game.</summary>
    [HarnessFact]
    public async Task TheAppTalksToTheRealNativeCode()
    {
        var exe = Environment.GetEnvironmentVariable("WDS_HR_HARNESS")!;
        var psi = new ProcessStartInfo(exe, "serve 30") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        try
        {
            var line = await p.StandardOutput.ReadLineAsync() ?? "";
            Assert.StartsWith("SERVING", line);
            var dir = GameDir("native");
            var locator = new NativeLocator(dir, p.Id);
            var hr = new HotReloadService(dir, locator);
            var probe = await hr.ProbeAsync();
            Assert.True(probe.Ready, probe.Message);
            var o = await hr.ReloadAsync(new[] { @"Creature\Boar\BoarSkinIvory.blp", @"Creature\Raptor\RaptorSkin.blp", @"Creature\Boar\Boar.m2" });
            Assert.True(o.Done, o.Message);
            Assert.Equal(ReloadResult.Reloaded, o.Report!.Files[0].Result);
            Assert.Equal(ReloadResult.NeedsRelog, o.Report.Files[1].Result);
            Assert.Equal(ReloadResult.Reloaded, o.Report.Files[2].Result);
            var rel = await hr.ReleaseAsync("patch-3.MPQ");
            Assert.Equal("released", rel.Result);
            var back = await hr.ReloadAsync(Array.Empty<string>(), "patch-3.MPQ");
            Assert.Equal("remounted", back.Report!.Archive!.Result);
        }
        finally
        {
            if (!p.HasExited) p.Kill();
        }
    }

    private sealed class NativeLocator : IGameLocator
    {
        private readonly string _dir;
        private readonly int _pid;
        public NativeLocator(string dir, int pid) { _dir = Path.GetFullPath(dir); _pid = pid; }
        public IReadOnlyList<GameProcess> Find(string clientDir) => new[] { new GameProcess(_pid, Path.Combine(_dir, "WoW.exe"), DateTime.Now) };
        public string PipeName(int pid) => "wowcode-hotreload-" + pid;
        public bool PipeExists(int pid) => WindowsGameLocator.Instance.PipeExists(pid);
    }
}

/// <summary>A test that needs the native offline harness of HR1 (skipped without WDS_HR_HARNESS).</summary>
public sealed class HarnessFactAttribute : FactAttribute
{
    public HarnessFactAttribute()
    {
        var exe = Environment.GetEnvironmentVariable("WDS_HR_HARNESS");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) Skip = "the HR1 native harness is not built (set WDS_HR_HARNESS to hr_harness.exe)";
    }
}
