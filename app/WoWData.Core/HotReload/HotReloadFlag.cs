using System.Text;

namespace WoWData.Core.HotReload;

/// <summary>
/// 0.11.0: the switch of hot reload in ONE game folder: <c>wowhotreload.txt</c> next to <c>wowcode.dll</c> (the project's hook
/// DLL reads it when the game starts; without <c>enabled 1</c> the DLL does nothing for hot reload, so a normal player never
/// runs it). Also what the game folder tells about the DLL: is it there, and does it know hot reload at all.
/// </summary>
public static class HotReloadFlag
{
    public const string FileName = "wowhotreload.txt";
    public const string DllName = "wowcode.dll";
    public const string LogName = "wowcode.log";

    /// <summary>The text wowcode.dll carries from the hot-reload update (HR1) on; an older DLL does not have it.</summary>
    public const string DllMarker = "WOWCODE_HOTRELOAD_PROTOCOL_1";

    public const int Protocol = 1;

    public static string PathFor(string clientDir) => Path.Combine(clientDir, FileName);

    /// <summary>True when the game folder has hot reload switched on (a line "enabled 1").</summary>
    public static bool IsEnabled(string clientDir)
    {
        try
        {
            var p = PathFor(clientDir);
            if (!File.Exists(p)) return false;
            foreach (var raw in File.ReadAllLines(p))
            {
                var line = raw.Split('#')[0].Trim();
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Equals("enabled", StringComparison.OrdinalIgnoreCase)) return parts[1] != "0";
            }
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Switches hot reload on (writes wowhotreload.txt) or off (deletes it) for one game folder. The game reads it when it
    /// starts: a running game keeps what it started with.</summary>
    public static void SetEnabled(string clientDir, bool on)
    {
        var p = PathFor(clientDir);
        if (on)
        {
            var text = new StringBuilder()
                .AppendLine("# wowhotreload.txt -- written by WoW Data Studio (Settings > Hot reload in the running game).")
                .AppendLine("# wowcode.dll reads it when the game starts. With \"enabled 1\" the running game takes reload requests from")
                .AppendLine("# WoW Data Studio (\"Reload in running game\", wds changes reload). Delete this file to switch it off.")
                .AppendLine("enabled 1")
                .AppendLine("# live updates, 1 = on (set one to 0 if it ever misbehaves): creatures and characters, game objects,")
                .AppendLine("# the terrain / buildings / doodads around you, data tables")
                .AppendLine("units 1")
                .AppendLine("gameobjects 1")
                .AppendLine("restream 1")
                .AppendLine("dbc 1")
                .ToString();
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, p, true);
        }
        else if (File.Exists(p)) File.Delete(p);
    }

    public static bool DllPresent(string clientDir) => File.Exists(Path.Combine(clientDir, DllName));

    /// <summary>True when the game folder's wowcode.dll knows hot reload (it carries <see cref="DllMarker"/>).</summary>
    public static bool DllSupports(string clientDir)
    {
        var p = Path.Combine(clientDir, DllName);
        try
        {
            var fi = new FileInfo(p);
            if (!fi.Exists) return false;
            var key = (Path.GetFullPath(p).ToLowerInvariant(), fi.LastWriteTimeUtc, fi.Length);
            lock (DllCache)
                if (DllCache.TryGetValue(key, out var known)) return known;   // the app's state line asks every few seconds
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool has = Contains(f, Encoding.ASCII.GetBytes(DllMarker));
            lock (DllCache) DllCache[key] = has;
            return has;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static readonly Dictionary<(string, DateTime, long), bool> DllCache = new();

    private static bool Contains(Stream s, byte[] pattern)
    {
        var buf = new byte[1 << 20];
        int keep = 0, n;
        while ((n = s.Read(buf, keep, buf.Length - keep)) > 0)
        {
            int len = keep + n;
            if (buf.AsSpan(0, len).IndexOf(pattern) >= 0) return true;
            keep = Math.Min(pattern.Length - 1, len);
            Array.Copy(buf, len - keep, buf, 0, keep);
        }
        return false;
    }

    /// <summary>The hot-reload lines of the game's wowcode.log, when that log belongs to process <paramref name="pid"/> (the DLL
    /// rewrites it at every start: "wowcode.dll loaded at ... into pid N"). Null when there is no log or it is another run's.</summary>
    public static List<string>? LogLinesFor(string clientDir, int pid)
    {
        var p = Path.Combine(clientDir, LogName);
        try
        {
            if (!File.Exists(p)) return null;
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f, Encoding.UTF8);
            var lines = new List<string>();
            bool ours = false;
            string? line;
            int n = 0;
            while ((line = r.ReadLine()) != null && n++ < 200000)
            {
                if (n == 1)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"into pid (\d+)");
                    ours = m.Success && int.Parse(m.Groups[1].Value) == pid;
                    if (!ours) return null;
                }
                if (line.Contains("hotreload:", StringComparison.Ordinal)) lines.Add(line[(line.IndexOf("hotreload:", StringComparison.Ordinal))..]);
            }
            return ours ? lines : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
