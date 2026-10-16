using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Core.Workspace;

namespace WoWData.Core.Maps;

public sealed class NoggitException : Exception
{
    public bool NeedsCopy { get; }
    public NoggitException(string message, bool needsCopy = false) : base(message) => NeedsCopy = needsCopy;
}

/// <summary>What "Edit in Noggit" prepared: the project to open and where Noggit will save.</summary>
public sealed record NoggitLaunchInfo(string ProjectFile, string ProjectDir, string SaveDir, string ClientDir, string BookmarkName, string ClientNote);

/// <summary>
/// "Edit in Noggit": our fixed Noggit fork (GPL-3) is only ever run as a separate program. This prepares what it needs and
/// keeps it away from the real game folder:
///  * a client for Noggit to read: by default the app's own <b>client view</b>, &lt;app data&gt;\&lt;client&gt;\noggit\client\Data\,
///    whose MPQs are hard links to the game's archives (no extra disk space; Noggit only opens them read-only and its
///    patch exporter creates new files, so nothing can reach the game folder's files). Where hard links are not possible
///    (another drive) the archives are copied after asking, or a client copy can be chosen in Settings;
///  * one Noggit project per map, &lt;app data&gt;\&lt;client&gt;\noggit\projects\&lt;map&gt;\WDS_&lt;map&gt;.noggitproj (ClientVersion
///    "Vanilla", the client above, the map pinned), with a bookmark at the selected tile: the fork has no command-line
///    option for a start location, but a double-click on a bookmark in its start window flies there;
///  * Noggit saves edited files into that project folder (World\Maps\&lt;map&gt;\...): the app's workspace for the map, which
///    "Import map changes" reads.
/// The project file format is the fork's (ApplicationProjectReader: Project.Client.ClientPath/ClientVersion,
/// Bookmarks[].{BookmarkName, MapId, Position{X,Y,Z}, CameraPitch, CameraYaw}, PinnedMaps[].{MapId, MapName}); the
/// bookmark position is in Noggit's coordinates (x = tile x * 533.33 + 266.67, y = height, z = tile y * 533.33 + 266.67).
/// </summary>
public sealed class NoggitService
{
    private readonly ClientPaths _client;
    private readonly AppSettings _settings;

    public NoggitService(ClientPaths client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public string Root => Path.Combine(_client.Dir, "noggit");
    public string ViewDir => Path.Combine(Root, "client");
    public string ProjectsDir => Path.Combine(Root, "projects");
    public string ProjectDir(MapInfo map) => Path.Combine(ProjectsDir, map.Directory);
    public static string ProjectName(MapInfo map) => "WDS_" + new string(map.Directory.Where(char.IsLetterOrDigit).ToArray());
    public string ProjectFile(MapInfo map) => Path.Combine(ProjectDir(map), ProjectName(map) + ".noggitproj");
    /// <summary>Where Noggit saves the map's files.</summary>
    public string SaveDir(MapInfo map) => Path.Combine(ProjectDir(map), "World", "Maps", map.Directory);

    /// <summary>noggit.exe: the path in the settings, else the fork's build (third_party\noggit-build\bin\noggit.exe)
    /// found next to the app or the game folder.</summary>
    public static string? FindNoggit(AppSettings settings, string? clientDir)
    {
        if (!string.IsNullOrEmpty(settings.NoggitPath)) return File.Exists(settings.NoggitPath) ? settings.NoggitPath : null;
        return DefaultNoggitPath(clientDir);
    }

    public static string? DefaultNoggitPath(string? clientDir)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, clientDir })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "third_party", "noggit-build", "bin", "noggit.exe");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>The source folder of the fork (its GPL licence and source pointer), next to the build.</summary>
    public static string? ForkSourceDir(string? noggitExe)
    {
        if (noggitExe == null) return null;
        for (var d = new DirectoryInfo(Path.GetDirectoryName(noggitExe)!); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "noggit-cinder-fork");
            if (File.Exists(Path.Combine(p, "COPYING"))) return p;
        }
        return null;
    }

    public static bool IsSameOrInside(string child, string parent)
    {
        var c = Path.GetFullPath(child).TrimEnd('\\', '/') + "\\";
        var p = Path.GetFullPath(parent).TrimEnd('\\', '/') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The client folder Noggit reads (the client view, or the copy chosen in Settings).</summary>
    public string NoggitClientDir => string.IsNullOrEmpty(_settings.NoggitClientDir) ? ViewDir : _settings.NoggitClientDir!;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    /// <summary>(volume, file index) of a file: equal for two hard links of one file.</summary>
    private static (uint, uint, uint)? FileId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h == new IntPtr(-1)) return null;
        try { return GetFileInformationByHandle(h, out var i) ? (i.VolumeSerialNumber, i.FileIndexHigh, i.FileIndexLow) : null; }
        finally { CloseHandle(h); }
    }

    /// <summary>True when both paths are the same file on disk (hard links of one file).</summary>
    public static bool SameFile(string a, string b) => File.Exists(a) && File.Exists(b) && FileId(a) is { } x && FileId(b) is { } y && x == y;

    /// <summary>Prepare the client Noggit reads. The client view gets a hard link to every .MPQ of the game's Data
    /// folder (refreshed each time: a rebuilt patch-3.MPQ is a new file); with <paramref name="allowCopy"/> the archives
    /// are copied where linking fails. Returns the client folder and a note in words.</summary>
    public (string ClientDir, string Note) PrepareClient(bool allowCopy, Action<string>? progress = null)
    {
        var gameData = Path.GetFullPath(_client.DataDir);
        if (!string.IsNullOrEmpty(_settings.NoggitClientDir))
        {
            var dir = _settings.NoggitClientDir!;
            CheckNotGame(dir);
            if (!File.Exists(Path.Combine(dir, "Data", "terrain.MPQ")))
                throw new NoggitException($"The client copy for Noggit ({dir}) has no Data\\terrain.MPQ. Pick a copy of the game folder in Settings, or clear the setting to let the app make its own.");
            return (dir, $"Noggit reads the client copy chosen in Settings: {dir}");
        }
        CheckNotGame(ViewDir);
        var viewData = Path.Combine(ViewDir, "Data");
        Directory.CreateDirectory(viewData);
        var wanted = Directory.EnumerateFiles(gameData, "*.MPQ").Select(Path.GetFileName).Cast<string>().ToList();
        int linked = 0, copied = 0;
        foreach (var name in wanted)
        {
            var src = Path.Combine(gameData, name);
            var dst = Path.Combine(viewData, name);
            if (SameFile(src, dst)) { linked++; continue; }
            if (File.Exists(dst))
            {
                // a copy is current when size and time match; a stale link or copy is replaced (only inside the view)
                var fs = new FileInfo(src);
                var fd = new FileInfo(dst);
                if (fd.Length == fs.Length && fd.LastWriteTimeUtc == fs.LastWriteTimeUtc) { copied++; continue; }
                File.Delete(dst);
            }
            progress?.Invoke($"Linking {name}...");
            if (OperatingSystem.IsWindows() && CreateHardLink(dst, src, IntPtr.Zero)) { linked++; continue; }
            if (!allowCopy)
                throw new NoggitException($"The app could not link the game's archives into its client view for Noggit ({ViewDir}): the app's data folder is probably on another drive than the game. " +
                                          "The archives can be copied there instead (about 5 GB), or you can pick an existing copy of the game folder in Settings.", needsCopy: true);
            progress?.Invoke($"Copying {name} ({new FileInfo(src).Length / 1048576} MB)...");
            File.Copy(src, dst + ".tmp", true);
            File.Move(dst + ".tmp", dst, true);
            File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
            copied++;
        }
        foreach (var stale in Directory.EnumerateFiles(viewData, "*.MPQ").Where(f => !wanted.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList())
            File.Delete(stale);   // only the view's own entry (a link or a copy), never the game's file
        return (ViewDir, copied == 0
            ? $"Noggit reads the app's client view ({ViewDir}): {linked} archives linked to the game's (no extra disk space). It contains no WoW.exe, so Noggit's \"Start WoW\" does nothing there."
            : $"Noggit reads the app's client view ({ViewDir}): {linked} archives linked, {copied} copied.");
    }

    private void CheckNotGame(string dir)
    {
        if (IsSameOrInside(dir, _client.ClientDir) || IsSameOrInside(_client.ClientDir, dir))
            throw new NoggitException($"Noggit must not use the game folder itself ({_client.ClientDir}): it could write into it (patch export). Use the app's client view or a copy of the game folder.");
    }

    /// <summary>The bookmark name of a tile.</summary>
    public static string BookmarkName(MapInfo map, int x, int y, string? zone) => $"WoW Data Studio: {(string.IsNullOrEmpty(zone) ? map.Name : zone)}, tile {x},{y}";

    /// <summary>Write (or update) the map's Noggit project with a bookmark at the tile. User bookmarks, pinned maps and
    /// patch records already in the file are kept; the app's older bookmarks are replaced.</summary>
    public NoggitLaunchInfo WriteProject(MapInfo map, int x, int y, float height, string? zone, string clientDir, string clientNote)
    {
        var dir = ProjectDir(map);
        Directory.CreateDirectory(SaveDir(map));
        var file = ProjectFile(map);
        JsonObject root;
        try { root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? new JsonObject() : new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }
        var proj = root["Project"] as JsonObject ?? new JsonObject();
        root["Project"] = proj;
        proj["ProjectName"] = ProjectName(map);
        proj["Client"] = new JsonObject { ["ClientPath"] = Path.GetFullPath(clientDir).Replace('\\', '/'), ["ClientVersion"] = "Vanilla" };
        var marks = proj["Bookmarks"] as JsonArray ?? new JsonArray();
        for (int i = marks.Count - 1; i >= 0; i--)
            if ((marks[i]?["BookmarkName"]?.GetValue<string>() ?? "").StartsWith("WoW Data Studio:", StringComparison.Ordinal)) marks.RemoveAt(i);
        string name = BookmarkName(map, x, y, zone);
        marks.Insert(0, new JsonObject
        {
            ["BookmarkName"] = name, ["MapId"] = map.Id, ["CameraPitch"] = 30.0, ["CameraYaw"] = 90.0,
            ["Position"] = new JsonObject
            {
                ["X"] = (double)(x * TerrainMath.TileSize + TerrainMath.TileSize / 2),
                ["Y"] = (double)(height + 60f),
                ["Z"] = (double)(y * TerrainMath.TileSize + TerrainMath.TileSize / 2),
            },
        });
        proj["Bookmarks"] = marks;
        var pinned = proj["PinnedMaps"] as JsonArray ?? new JsonArray();
        if (!pinned.Any(p => p?["MapId"]?.GetValue<int>() == map.Id)) pinned.Add(new JsonObject { ["MapId"] = map.Id, ["MapName"] = map.Name });
        proj["PinnedMaps"] = pinned;
        proj["Patches"] ??= new JsonArray();
        // any other .noggitproj in the folder would be read first by Noggit: there must be only ours
        foreach (var other in Directory.EnumerateFiles(dir, "*.noggitproj").Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)).ToList()) File.Delete(other);
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new NoggitLaunchInfo(file, dir, SaveDir(map), clientDir, name, clientNote);
    }

    /// <summary>Copy the map files already in the app's workspace (earlier imports) into the project where it has none,
    /// so Noggit continues from them. Files Noggit saved that differ are left alone (work not imported yet).</summary>
    public int SyncFromWorkspace(MapInfo map, Workspace.Workspace ws)
    {
        int n = 0;
        var save = SaveDir(map);
        Directory.CreateDirectory(save);
        var prefix = $@"WORLD\MAPS\{map.Directory.ToUpperInvariant()}\";
        foreach (var path in ws.ModifiedPaths())
        {
            if (!path.ToUpperInvariant().StartsWith(prefix, StringComparison.Ordinal) || MapValidator.KindOf(path) == null) continue;
            var dst = Path.Combine(save, Path.GetFileName(path));
            if (File.Exists(dst)) continue;
            var data = ws.ReadEdited(path);
            if (data == null) continue;
            File.WriteAllBytes(dst, data);
            n++;
        }
        return n;
    }

    /// <summary>After "Revert": the project copies that are exactly the reverted files are removed, so Noggit shows the
    /// game's version again. Returns how many were removed.</summary>
    public int ForgetReverted(MapInfo map, IEnumerable<(string Path, string Sha256)> reverted)
    {
        int n = 0;
        foreach (var (path, sha) in reverted)
        {
            var f = Path.Combine(SaveDir(map), Path.GetFileName(path));
            if (File.Exists(f) && Workspace.Workspace.Sha256File(f) == sha)
            {
                File.Delete(f);
                n++;
            }
        }
        return n;
    }

    /// <summary>Start Noggit (working folder: its own, where it keeps noggit.json and log.txt).</summary>
    public static Process? Launch(string noggitExe)
    {
        var psi = new ProcessStartInfo(noggitExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(noggitExe)! };
        return Process.Start(psi);
    }

    public static bool IsRunning() => Process.GetProcessesByName("noggit").Length > 0;
}
