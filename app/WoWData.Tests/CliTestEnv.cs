using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using WoWData.Cli;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>The result of one run of wds: the exit code, stdout, stderr and (with --json) the parsed object.</summary>
public sealed record CliRun(int Code, string Out, string Err)
{
    public JsonElement Json => JsonDocument.Parse(Out).RootElement;
    public JsonElement Data => Json.GetProperty("data");
    public override string ToString() => $"exit {Code}\nstdout:\n{Out}\nstderr:\n{Err}";
}

/// <summary>
/// 0.8.0: an isolated world for the command line: a FAKE game folder whose WoW.exe, WTF\Config.wtf and stock archives are
/// hard links of the real client's (read only: the tests never write into them; patch-3.MPQ and loose files land in the
/// fake Data folder as new files), an isolated --home with a copy of the shared test index (no index build), and a fake
/// server data folder. The real Client\Data listing is compared before and after.
/// </summary>
public sealed class CliTestEnv : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();

    public string Root { get; }
    public string Client { get; }
    public string Home { get; }
    public string ServerData { get; }
    public string Work { get; }

    public CliTestEnv(string name, bool withIndex = true)
    {
        Root = TestEnv.TempDir("cli_" + name);
        Client = Path.Combine(Root, "Game");
        Home = Path.Combine(Root, "home");
        ServerData = Path.Combine(Root, "Server", "data");
        Work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(Client, "Data"));
        Link(Path.Combine(TestEnv.ClientDir, "WoW.exe"), Path.Combine(Client, "WoW.exe"));
        var cfg = Path.Combine(TestEnv.ClientDir, "WTF", "Config.wtf");
        if (File.Exists(cfg))
        {
            Directory.CreateDirectory(Path.Combine(Client, "WTF"));
            File.Copy(cfg, Path.Combine(Client, "WTF", "Config.wtf"));
        }
        foreach (var f in Directory.EnumerateFiles(TestEnv.DataDir, "*.MPQ"))
        {
            var n = Path.GetFileName(f);
            if (Workspace.IsAllowedPatchName(n)) continue;   // only the game's own archives (never a patch-3.MPQ)
            Link(f, Path.Combine(Client, "Data", n));
        }
        Directory.CreateDirectory(Path.Combine(ServerData, "5875", "dbc"));
        Directory.CreateDirectory(Path.Combine(ServerData, "maps"));
        var paths = new AppPaths(Home).ForClient(Client);
        Directory.CreateDirectory(paths.Dir);
        if (withIndex) File.Copy(IndexFixture.Instance.Path, paths.IndexFile);
        var s = new AppSettings { ClientDir = Client, ServerDataDir = ServerData };
        s.Server.Enabled = false;
        s.Server.Port = 1;   // never the real server
        s.ExportDir = Path.Combine(Root, "exports");
        s.Save(new AppPaths(Home));
    }

    private static void Link(string src, string dst)
    {
        if (!CreateHardLink(dst, src, IntPtr.Zero)) throw new IOException($"could not hard-link {src} (the temp folder must be on the client's drive): {Marshal.GetLastWin32Error()}");
    }

    public ClientPaths Paths => new AppPaths(Home).ForClient(Client);
    public string DataDir => Path.Combine(Client, "Data");

    /// <summary>Run wds in-process with this env's --home.</summary>
    public CliRun Run(params string[] args) => RunWith(null, args);

    public CliRun RunWith(string? stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.RunAsync(new[] { "--home", Home }.Concat(args).ToArray(), o, e, stdin == null ? null : new StringReader(stdin)).GetAwaiter().GetResult();
        return new CliRun(code, o.ToString(), e.ToString());
    }

    /// <summary>Run with --json and check the envelope every command prints.</summary>
    public CliRun Json(params string[] args)
    {
        var r = Run(args.Append("--json").ToArray());
        CheckEnvelope(r);
        return r;
    }

    public static void CheckEnvelope(CliRun r)
    {
        JsonElement j;
        try { j = r.Json; }
        catch (JsonException e) { throw new Xunit.Sdk.XunitException($"not one JSON object: {e.Message}\n{r}"); }
        foreach (var k in new[] { "command", "ok", "exitCode", "status", "version", "data", "messages", "warnings", "error" })
            Assert.True(j.TryGetProperty(k, out _), $"the JSON has no \"{k}\"\n{r}");
        Assert.Equal(r.Code, j.GetProperty("exitCode").GetInt32());
        Assert.Equal(r.Code == 0, j.GetProperty("ok").GetBoolean());
        Assert.Equal(WoWData.Core.StudioInfo.Version, j.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, j.GetProperty("messages").ValueKind);
        Assert.Equal(JsonValueKind.Array, j.GetProperty("warnings").ValueKind);
        if (r.Code != 0) Assert.Equal(JsonValueKind.String, j.GetProperty("error").ValueKind);
    }

    /// <summary>The key of a creature (or creature display) entry that wears BoarSkinIvory (with or without the server's names).</summary>
    public static string BoarEntry(CliTestEnv env)
    {
        var r = env.Json("find", "search", "boar", "--type", "creature", "--limit", "0");
        var hits = r.Data.GetProperty("results").EnumerateArray()
            .OrderBy(x => x.GetProperty("name").GetString()!.Contains("Ivory", StringComparison.OrdinalIgnoreCase) || x.GetProperty("name").GetString() == "Mottled Boar" ? 0 : 1).Take(15);
        foreach (var x in hits)
        {
            var key = x.GetProperty("key").GetString()!;
            if (env.Json("find", "info", key).Data.GetProperty("textures").EnumerateArray().Any(t => t.GetProperty("path").GetString()!.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase)))
                return key;
        }
        throw new InvalidOperationException("no boar with the Ivory skin in the index");
    }

    /// <summary>wds.exe of this build (for out-of-process runs).</summary>
    public static string Exe
    {
        get
        {
            var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            foreach (var c in new[] { cfg, "Release", "Debug" })
            {
                var p = Path.Combine(Oracle.AppDir, "WoWData.Cli", "bin", c, "net8.0-windows", "wds.exe");
                if (File.Exists(p)) return p;
            }
            throw new InvalidOperationException("wds.exe is not built");
        }
    }

    /// <summary>Run wds.exe as its own process (real exit codes and streams).</summary>
    public CliRun RunExe(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(Home);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(300_000)) { p.Kill(true); throw new TimeoutException("wds did not finish"); }
        return new CliRun(p.ExitCode, so.Result, se.Result);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(_before, TestEnv.DataSnapshot());    // the real Client\Data folder is unchanged
        for (int i = 0; i < 5; i++)
        {
            try { Directory.Delete(Root, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
