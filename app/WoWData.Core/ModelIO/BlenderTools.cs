using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoWData.Core.ModelIO;

/// <summary>
/// Blender 4.5 as a separate program: finding it, building the add-on zip, installing the add-on (Blender's own
/// installer run headless), opening an export package in Blender, and running scripts headless (tests). Blender is GPL
/// and is only ever started as its own process; the add-on is our MIT code.
/// </summary>
public static class BlenderTools
{
    public const string AddonModule = "wowdata_blender";

    /// <summary>blender.exe or the Microsoft Store alias (blender-launcher.exe), or null when Blender is not installed.
    /// WOWDATA_BLENDER overrides.</summary>
    public static string? FindBlender()
    {
        var env = Environment.GetEnvironmentVariable("WOWDATA_BLENDER");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "blender-launcher.exe");
        if (File.Exists(alias)) return alias;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var bf = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(bf)) continue;
            var exe = Directory.EnumerateDirectories(bf, "Blender*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "blender.exe")).FirstOrDefault(File.Exists);
            if (exe != null) return exe;
        }
        return null;
    }

    /// <summary>The folder that contains wowdata_blender\ and studio\ (shipped next to the app as blender_addon\; in a
    /// source checkout, WoWDataTool\blender_addon).</summary>
    public static string? AddonRoot()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "blender_addon");
        if (File.Exists(Path.Combine(here, AddonModule, "__init__.py"))) return here;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "blender_addon");
            if (File.Exists(Path.Combine(cand, AddonModule, "__init__.py"))) return cand;
        }
        return null;
    }

    /// <summary>The add-on version in bl_info, e.g. "0.3.0".</summary>
    public static string AddonVersion(string addonRoot)
    {
        var text = File.ReadAllText(Path.Combine(addonRoot, AddonModule, "__init__.py"));
        var m = Regex.Match(text, @"'version'\s*:\s*\((\d+)\s*,\s*(\d+)\s*,\s*(\d+)\)");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : "0.0.0";
    }

    /// <summary>0.8.0: where "Install the Blender add-on" writes the zip: Documents\WoW Data Studio\Blender (next to the
    /// exports folder), the same for the app and "wds models addon".</summary>
    public static string DefaultZipPath(string exportDir) => Path.GetFullPath(Path.Combine(exportDir, "..", "Blender", "wowdata_blender.zip"));

    /// <summary>The text that explains how to install the add-on by hand.</summary>
    public const string ManualInstallText = "To install it yourself: start Blender 4.5, Edit > Preferences > Add-ons, the drop-down arrow at the top right, Install from Disk..., pick the zip, and tick \"Import-Export: WoW 1.12 model (wowdata)\".";

    /// <summary>Write the add-on zip for Blender's "Install from Disk..." (wowdata_blender\*.py, no caches).</summary>
    public static string BuildZip(string addonRoot, string zipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var src = Path.Combine(addonRoot, AddonModule);
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(addonRoot, f).Replace('\\', '/');
                if (rel.Contains("__pycache__") || rel.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
                z.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
            }
        }
        File.Move(tmp, zipPath, true);
        return zipPath;
    }

    /// <summary>Where Blender 4.5 keeps add-ons installed by the user (for the manual instructions).</summary>
    public static string UserAddonsDir(string version = "4.5") =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender", version, "scripts", "addons");

    public sealed record RunResult(int ExitCode, string Output, bool TimedOut);

    /// <summary>Run Blender in the background with a Python script; arguments after "--" go to the script.</summary>
    public static RunResult RunHeadless(string blender, string script, IEnumerable<string> args, int timeoutMs = 600_000, IDictionary<string, string>? env = null,
        bool factoryStartup = true)
    {
        var psi = new ProcessStartInfo(blender) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-b");
        if (factoryStartup) psi.ArgumentList.Add("--factory-startup");
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Blender could not be started.");
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return new RunResult(-1, sb.ToString(), true);
        }
        p.WaitForExit();
        return new RunResult(p.ExitCode, sb.ToString(), false);
    }

    public sealed record InstallResult(bool Ok, string Message, string? AddonFile, string? Details);

    /// <summary>Install and enable the add-on in the user's Blender (Blender's own installer, run headless), then save
    /// Blender's preferences so it stays enabled. <paramref name="env"/> can point BLENDER_USER_SCRIPTS / _CONFIG elsewhere
    /// (tests).</summary>
    public static InstallResult Install(string blender, string addonRoot, string zipPath, string workDir, IDictionary<string, string>? env = null)
    {
        Directory.CreateDirectory(workDir);
        var result = Path.Combine(workDir, "install_result.json");
        if (File.Exists(result)) File.Delete(result);
        var script = Path.Combine(addonRoot, "studio", "studio_install.py");
        var r = RunHeadless(blender, script, new[] { result, zipPath }, 180_000, env, factoryStartup: false);
        if (!File.Exists(result))
            return new InstallResult(false, r.TimedOut ? "Blender did not finish in time." : "Blender did not report back (is it Blender 4.2 or newer?).", null, Tail(r.Output));
        using var doc = JsonDocument.Parse(File.ReadAllText(result));
        var root = doc.RootElement;
        bool ok = root.TryGetProperty("ok", out var okp) && okp.GetBoolean();
        string? file = root.TryGetProperty("module_file", out var mf) ? mf.GetString() : null;
        string msg = root.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";
        return new InstallResult(ok, msg, file, Tail(r.Output));
    }

    /// <summary>Start Blender (with its window) and import the package's glTF with the add-on. The installed add-on
    /// is used when it is at least as new as the app's; otherwise the app's copy is loaded for this session.</summary>
    public static Process? OpenInBlender(string blender, string addonRoot, string gltf)
    {
        var psi = new ProcessStartInfo(blender) { UseShellExecute = false };
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(Path.Combine(addonRoot, "studio", "studio_open.py"));
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(gltf);
        psi.ArgumentList.Add(addonRoot);
        psi.ArgumentList.Add(AddonVersion(addonRoot));
        return Process.Start(psi);
    }

    private static string Tail(string s) => s.Length <= 4000 ? s : "..." + s[^4000..];
}
