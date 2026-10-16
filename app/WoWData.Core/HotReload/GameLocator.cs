using System.Diagnostics;

namespace WoWData.Core.HotReload;

/// <summary>A game process started from a game folder.</summary>
public sealed record GameProcess(int Pid, string ExePath, DateTime? Started);

/// <summary>Finds the running game of a game folder and its hot-reload pipe. The tests replace it with a fake game
/// (<see cref="HotReloadService.UseLocator"/>).</summary>
public interface IGameLocator
{
    /// <summary>The WoW.exe processes whose program file is in <paramref name="clientDir"/>, newest first.</summary>
    IReadOnlyList<GameProcess> Find(string clientDir);

    /// <summary>The name of the game's hot-reload pipe (without \\.\pipe\).</summary>
    string PipeName(int pid);

    /// <summary>True when the pipe exists (the game's wowcode.dll serves it).</summary>
    bool PipeExists(int pid);
}

/// <summary>What <see cref="WindowsGameLocator"/> asks Windows about processes. The tests replace it (a game that refuses
/// to be opened, a path with short names).</summary>
public interface IProcessQuery
{
    /// <summary>The ids of the running processes with this program name (without .exe).</summary>
    IReadOnlyList<int> FindByName(string name);

    /// <summary>The program file of a process as a drive path, or null.</summary>
    string? ImagePath(int pid);

    /// <summary>When the process started, or null when that cannot be read.</summary>
    DateTime? StartTime(int pid);
}

/// <summary>The real query layer: the process list, <see cref="ProcessImage"/>.</summary>
public sealed class WindowsProcessQuery : IProcessQuery
{
    public static readonly WindowsProcessQuery Instance = new();

    public IReadOnlyList<int> FindByName(string name)
    {
        var ids = new List<int>();
        foreach (var p in Process.GetProcessesByName(name)) using (p) ids.Add(p.Id);
        return ids;
    }

    public string? ImagePath(int pid) => ProcessImage.PathOf(pid);
    public DateTime? StartTime(int pid) => ProcessImage.StartTime(pid);
}

/// <summary>The real one: WoW.exe processes (also the one WoWDebug.exe starts) and \\.\pipe\wowcode-hotreload-&lt;pid&gt;.
/// The pid is WoW.exe's own (the process wowcode.dll runs in), never WoWDebug.exe's.</summary>
public sealed class WindowsGameLocator : IGameLocator
{
    public static readonly WindowsGameLocator Instance = new();

    private readonly IProcessQuery _query;

    public WindowsGameLocator(IProcessQuery? query = null) => _query = query ?? WindowsProcessQuery.Instance;

    public IReadOnlyList<GameProcess> Find(string clientDir)
    {
        // 0.13.1: the game started by WoWDebug.exe cannot be opened at all (see ProcessImage); 0.13.0 read its path with
        // OpenProcess only, got null and skipped it, so the app always said "The game is not running from ..."
        var dir = ProcessImage.NormalizeDir(clientDir);
        var list = new List<GameProcess>();
        foreach (var pid in _query.FindByName("WoW"))
        {
            var exe = _query.ImagePath(pid);
            var exeDir = exe == null ? null : Path.GetDirectoryName(exe);
            if (exeDir == null || !string.Equals(ProcessImage.NormalizeDir(exeDir), dir, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new GameProcess(pid, exe!, _query.StartTime(pid)));
        }
        return list.OrderByDescending(g => g.Started ?? DateTime.MinValue).ThenByDescending(g => g.Pid).ToList();
    }

    public string PipeName(int pid) => "wowcode-hotreload-" + pid;

    /// <summary>Lists the pipe folder (FindFirstFile): unlike opening the pipe, listing never takes the game's one connection.</summary>
    public bool PipeExists(int pid)
    {
        var name = PipeName(pid);
        try { return Directory.EnumerateFiles(@"\\.\pipe\").Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The program file of a process (works for a 32-bit game seen from the 64-bit app, without admin rights,
    /// and for a game that refuses to be opened).</summary>
    public static string? ImagePath(int pid) => ProcessImage.PathOf(pid);
}
