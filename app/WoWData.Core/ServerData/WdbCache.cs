using System.Diagnostics;

namespace WoWData.Core.ServerData;

/// <summary>
/// 0.9.0: the game's cache of server data. The 1.12 game keeps what the server told it about items, creatures, quests,
/// game objects, texts and pages in the WDB folder of the game (itemcache.wdb, creaturecache.wdb, questcache.wdb ...)
/// and does not ask again: after a server edit of a name, a stat or a quest text, the game keeps showing the old one until
/// the cache is cleared. "Clear the game's cache" moves the .wdb files of a chosen game folder into the app's backup
/// folder (nothing is deleted; the game makes new ones), and is refused while that game runs.
/// </summary>
public static class WdbCache
{
    /// <summary>The cache files of a game folder (WDB\*.wdb).</summary>
    public static List<FileInfo> Files(string gameDir)
    {
        var dir = Path.Combine(gameDir, "WDB");
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.wdb", SearchOption.AllDirectories).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() : new();
    }

    /// <summary>The games running from this folder (WoW.exe processes whose program is in it; a game whose program cannot
    /// be read counts as running from it, to be safe).</summary>
    public static List<string> RunningGames(string gameDir)
    {
        var res = new List<string>();
        var root = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "WoW", "WoWDebug" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = WoWData.Core.HotReload.ProcessImage.PathOf(p.Id);   // 0.13.1: also for a game that refuses to be opened
                    if (exe == null || exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) res.Add($"{name}.exe (process {p.Id})");
                }
                finally { p.Dispose(); }
            }
        return res;
    }

    /// <summary>Move the cache files into <paramref name="backupRoot"/>\wdb_&lt;time&gt; (refused while the game runs).</summary>
    public static (string? Backup, List<string> Done) Clear(string gameDir, string backupRoot, Func<string, List<string>>? running = null)
    {
        if (!File.Exists(Path.Combine(gameDir, "WoW.exe"))) throw new ServerDataException($"{gameDir} is not a World of Warcraft folder (no WoW.exe).");
        var games = (running ?? RunningGames)(gameDir);
        if (games.Count > 0) throw new ServerDataException($"The game is running from this folder ({string.Join(", ", games)}). Close it first: it writes its cache when it quits.");
        var files = Files(gameDir);
        var done = new List<string>();
        if (files.Count == 0)
        {
            done.Add($"The cache of {gameDir} is already empty (no WDB\\*.wdb files).");
            return (null, done);
        }
        var backup = Path.Combine(backupRoot, "wdb_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        for (int k = 2; Directory.Exists(backup); k++) backup = Path.Combine(backupRoot, $"wdb_{DateTime.Now:yyyyMMdd_HHmmss}_{k}");
        var wdb = Path.Combine(gameDir, "WDB");
        foreach (var f in files)
        {
            var dest = Path.Combine(backup, Path.GetRelativePath(wdb, f.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(f.FullName, dest);
        }
        done.Add($"Moved {files.Count} cache file(s) ({string.Join(", ", files.Select(f => f.Name))}) out of {wdb} into {backup}.");
        done.Add("The game asks the server again for every item, creature, quest and object it meets: names, stats and texts are the server's current ones.");
        return (backup, done);
    }
}
