using System.Security.Cryptography;
using System.Text;

namespace WoWData.Core.Workspace;

/// <summary>
/// Where the app keeps its files: %APPDATA%\WoWDataStudio (or the folder in WOWDATASTUDIO_HOME, used by tests and the
/// screenshot mode). Per client folder: &lt;root&gt;\&lt;client-id&gt;\ holds the index, the workspace (edited files mirroring
/// archive paths), the change list, backups and built patches.
/// </summary>
public sealed class AppPaths
{
    public string Root { get; }

    public AppPaths(string? root = null)
    {
        Root = root
               ?? Environment.GetEnvironmentVariable("WOWDATASTUDIO_HOME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WoWDataStudio");
    }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>A stable id for a client folder: its folder name plus a hash of the full path.</summary>
    public static string ClientId(string clientDir)
    {
        var full = Path.GetFullPath(clientDir).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        var name = new string(Path.GetFileName(full).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return (name.Length == 0 ? "client" : name) + "-" + hash;
    }

    public ClientPaths ForClient(string clientDir) => new(Path.Combine(Root, ClientId(clientDir)), clientDir);
}

public sealed class ClientPaths
{
    public string Dir { get; }
    public string ClientDir { get; }

    public ClientPaths(string dir, string clientDir)
    {
        Dir = dir;
        ClientDir = clientDir;
    }

    public string DataDir => Path.Combine(ClientDir, "Data");
    public string IndexFile => Path.Combine(Dir, "index.sqlite");
    public string WorkspaceDir => Path.Combine(Dir, "workspace");
    public string ChangesFile => Path.Combine(Dir, "changes.json");
    public string BackupDir => Path.Combine(Dir, "backups");
    /// <summary>0.14.0: the versions of edited files that a later change set replaced ("Revert this import" puts them back).</summary>
    public string HistoryDir => Path.Combine(Dir, "history");
    public string BuildDir => Path.Combine(Dir, "build");
    public string TempDir => Path.Combine(Dir, "temp");
    /// <summary>Cached minimap overviews of the Maps tab (rebuilt when the archives change).</summary>
    public string MinimapCacheDir => Path.Combine(Dir, "cache", "minimap");
    /// <summary>The server updates done through the app (for "Undo server update").</summary>
    public string ServerHistoryFile => Path.Combine(Dir, "server_updates.json");
    /// <summary>0.7.0: the DBC tables copied to the server's dbc folder (for "Undo copy to server").</summary>
    public string ServerDbcHistoryFile => Path.Combine(Dir, "server_dbc_copies.json");
}
