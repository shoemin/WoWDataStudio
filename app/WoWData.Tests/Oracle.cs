using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;

namespace WoWData.Tests;

/// <summary>
/// Reference results from the Python implementation (the test oracle). <c>Ensure("x")</c> returns
/// <c>app\.testcache\oracle\x\</c>, generating it first with <c>python app\WoWData.Tests\oracle\make_x.py &lt;dir&gt;</c>
/// (working directory: the WoWDataTool folder) when its <c>done.txt</c> is missing. The cache holds Blizzard game
/// data, so it lives under app\.testcache\ which is not in source control. Delete a folder to regenerate it.
/// Environment overrides: WOWDATA_APP_DIR (the app folder), WOWDATA_PYTHON (the Python executable).
/// </summary>
public static class Oracle
{
    private static readonly object Gate = new();
    private static string? _appDir;

    /// <summary>The folder holding WoWDataStudio.sln (found by walking up from the test binaries).</summary>
    public static string AppDir
    {
        get
        {
            if (_appDir != null) return _appDir;
            string? env = Environment.GetEnvironmentVariable("WOWDATA_APP_DIR");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "WoWDataStudio.sln"))) return _appDir = Path.GetFullPath(env);
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "WoWDataStudio.sln"))) return _appDir = d.FullName;
            throw new InvalidOperationException(
                $"cannot find the app folder (WoWDataStudio.sln) above {AppContext.BaseDirectory}; set WOWDATA_APP_DIR");
        }
    }

    /// <summary>The WoWDataTool folder (the Python package's root): the parent of <see cref="AppDir"/>.</summary>
    public static string ToolDir => Path.GetDirectoryName(AppDir)!;

    public static string Python => Environment.GetEnvironmentVariable("WOWDATA_PYTHON") is { Length: > 0 } p ? p : "python";

    /// <summary>The oracle folder for <paramref name="name"/> (with a trailing separator), generated if needed.</summary>
    public static string Ensure(string name)
    {
        string dir = Path.Combine(AppDir, ".testcache", "oracle", name) + Path.DirectorySeparatorChar;
        string done = Path.Combine(dir, "done.txt");
        if (File.Exists(done)) return dir;
        lock (Gate)
        {
            using var mutex = new Mutex(false, @"Local\WoWDataTool.Oracle." + name);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromMinutes(30)); }
                catch (AbandonedMutexException) { owned = true; }
                if (File.Exists(done)) return dir;
                string script = Path.Combine(AppDir, "WoWData.Tests", "oracle", $"make_{name}.py");
                if (!File.Exists(script)) throw new InvalidOperationException($"no oracle generator {script}");
                Directory.CreateDirectory(dir);
                RunPython(TimeSpan.FromMinutes(30), script, dir.TrimEnd(Path.DirectorySeparatorChar));
                if (!File.Exists(done))
                    throw new InvalidOperationException($"the oracle generator {Path.GetFileName(script)} finished without writing {done}");
                return dir;
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>Runs Python (working directory: <see cref="ToolDir"/>) and returns its stdout; throws with stderr on failure.</summary>
    public static string RunPython(params string[] args) => RunPython(TimeSpan.FromMinutes(5), args);

    public static string RunPython(TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo(Python)
        {
            WorkingDirectory = ToolDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("Python did not start"); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException(
                $"the test oracle needs Python 3 with numpy and Pillow on PATH (or WOWDATA_PYTHON); starting '{Python}' failed: {e.Message}", e);
        }
        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException($"python {string.Join(' ', args)} did not finish within {timeout}");
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                string err = stderr.Result;
                if (err.Length > 4000) err = "..." + err[^4000..];
                throw new InvalidOperationException(
                    $"python {string.Join(' ', args.Select(Path.GetFileName))} failed (exit code {proc.ExitCode}). " +
                    $"It needs numpy, Pillow and (for game data) the WoW client configured in wowdata.json.\n{err}{stdout.Result}");
            }
            return stdout.Result;
        }
    }
}
