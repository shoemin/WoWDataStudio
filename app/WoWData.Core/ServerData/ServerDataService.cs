using System.Globalization;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;
using WoWData.Core.Index;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.ServerData;

/// <summary>
/// 0.9.0: the server data features of one open game folder, shared by the Server data tab and <c>wds server</c>: the
/// world database (<see cref="WorldDb"/>), what its tables mean (<see cref="ServerLayouts"/>), your workspace (where
/// server edits are kept), the game's data tables (for links, icons and the client / server checks), the content patch
/// the server runs, the custom ID range for new rows, and where backups and the applied-changes log go.
/// </summary>
public sealed class ServerDataService
{
    /// <summary>The client build of the game (1.12.1).</summary>
    public const int ClientBuild = 5875;

    public ServerDataService(ServerDbSettings db, Ws? workspace, DbcService? dbc, Workspace.AppSettings settings, string? serverFolder, string? stateDir,
                             Func<List<string>>? runningServers = null, ServerLayouts? layouts = null)
    {
        Db = new WorldDb(db);
        Workspace = workspace;
        Dbc = dbc;
        Settings = settings;
        ServerFolder = serverFolder;
        StateDir = stateDir;
        RunningServers = runningServers ?? Maps.ServerMapsService.RunningWorldServers;
        Layouts = layouts ?? ServerLayouts.Default;
    }

    public WorldDb Db { get; }
    public Ws? Workspace { get; }
    public DbcService? Dbc { get; }
    public Workspace.AppSettings Settings { get; }
    public ServerLayouts Layouts { get; }
    /// <summary>The server's folder (with mangosd.exe and mangosd.conf), or null.</summary>
    public string? ServerFolder { get; }
    /// <summary>Where backups and the applied-changes log go (the game folder's app folder), or null (no applying).</summary>
    public string? StateDir { get; }
    public Func<List<string>> RunningServers { get; }

    public string Database => Db.Settings.Database;
    public WorldSchema Schema => Db.Schema;

    public string BackupRoot => Path.Combine(StateDir ?? throw new ServerDataException("No game folder is open."), "backups", "server_db");
    public string ApplyLogFile => Path.Combine(StateDir ?? throw new ServerDataException("No game folder is open."), "server_db_applies.json");

    // ------------------------------------------------------------------ the server's patch and the custom ID range

    /// <summary>mangosd.conf of the server folder, or null.</summary>
    public string? MangosConf => ServerFolder != null && File.Exists(Path.Combine(ServerFolder, "mangosd.conf")) ? Path.Combine(ServerFolder, "mangosd.conf") : null;

    /// <summary>The content patch the server runs (0 = 1.2 ... 10 = 1.12): the setting, else WowPatch of mangosd.conf, else
    /// 10. Only that one line of the file is read.</summary>
    public int Patch => Settings.ServerPatch ?? ConfPatch ?? 10;

    public int? ConfPatch
    {
        get
        {
            var conf = MangosConf;
            if (conf == null) return null;
            try
            {
                foreach (var line in File.ReadLines(conf))
                {
                    var m = Regex.Match(line, @"^\s*WowPatch\s*=\s*""?(\d+)""?");
                    if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    public string PatchText => Settings.ServerPatch != null ? $"patch {Patch} (your setting)" : ConfPatch != null ? $"patch {Patch} (WowPatch in mangosd.conf)" : $"patch {Patch} (1.12, the default)";

    public (long Start, long End) CustomIds => (Settings.ServerCustomIdStart, Settings.ServerCustomIdEnd);

    // ------------------------------------------------------------------ tables, kinds, versions

    public ServerTableInfo? Info(string table) => Layouts.Table(table);

    public WorldTable Table(string name) => Schema.Require(name);

    /// <summary>The kind of a table's objects (item for item_template), or null.</summary>
    public ServerKind? KindOf(string table) => Layouts.KindOfTable(table);

    /// <summary>The column that holds the object's ID (entry, id, guid...): the key without its version column.</summary>
    public string IdColumn(WorldTable t)
    {
        var info = Info(t.Name);
        var k = KindOf(t.Name);
        if (k != null && t.Has(k.KeyColumn)) return k.KeyColumn;
        var version = VersionColumn(t, info);
        return t.Key.FirstOrDefault(c => !string.Equals(c, version, StringComparison.OrdinalIgnoreCase)) ?? t.Key.FirstOrDefault() ?? t.Columns[0].Name;
    }

    public static string? VersionColumn(WorldTable t, ServerTableInfo? info) => info?.Versioning switch
    {
        ServerVersioning.Patch => t.Has("patch") ? "patch" : null,
        ServerVersioning.Build or ServerVersioning.BuildExact => t.Has("build") ? "build" : null,
        _ => null,
    };

    /// <summary>Does the server load this row (its patch / build range)? With the row's version text.</summary>
    public (bool Loaded, string Text) RowVersion(WorldTable t, IReadOnlyList<string?> row, IReadOnlyList<string?[]>? siblings = null)
    {
        var info = Info(t.Name);
        string? V(string c) => t.IndexOf(c) is var i and >= 0 ? row[i] : null;
        switch (info?.Versioning)
        {
            case ServerVersioning.Patch:
            {
                long p = WorldValue.Long(V("patch"));
                if (siblings == null) return (p <= Patch, $"patch {p}");
                var best = siblings.Select(s => WorldValue.Long(s[t.IndexOf("patch")])).Where(x => x <= Patch).DefaultIfEmpty(-1).Max();
                return (p == best, p == best ? $"patch {p} (the server uses this row)" : p > Patch ? $"patch {p} (later than the server's patch {Patch}: not used)" : $"patch {p} (older: the server uses patch {best})");
            }
            case ServerVersioning.Build:
            {
                long b = WorldValue.Long(V("build"));
                if (siblings == null) return (b <= ClientBuild, $"build {b}");
                var best = siblings.Select(s => WorldValue.Long(s[t.IndexOf("build")])).Where(x => x <= ClientBuild).DefaultIfEmpty(-1).Max();
                return (b == best, b == best ? $"build {b} (the server uses this row)" : b > ClientBuild ? $"build {b} (a later client: not used)" : $"build {b} (older: the server uses build {best})");
            }
            case ServerVersioning.BuildExact:
            {
                long b = WorldValue.Long(V("build"));
                return (b == ClientBuild, b == ClientBuild ? "build 5875 (used)" : $"build {b} (not used: only 5875 rows are loaded)");
            }
            case ServerVersioning.PatchRange:
            {
                long a = WorldValue.Long(V("patch_min")), b = WorldValue.Long(V("patch_max"));
                bool ok = Patch >= a && Patch <= b;
                return (ok, $"patches {a}-{b}" + (ok ? "" : $" (not loaded at patch {Patch})"));
            }
            case ServerVersioning.BuildRange:
            {
                long a = WorldValue.Long(V("build_min")), b = WorldValue.Long(V("build_max"));
                bool ok = ClientBuild >= a && ClientBuild <= b;
                return (ok, $"builds {a}-{b}" + (ok ? "" : " (not loaded for 1.12.1)"));
            }
            default:
                return (true, "");
        }
    }

    /// <summary>Of several versions of one object, the row the server uses (null: none is used).</summary>
    public string?[]? Effective(WorldTable t, IReadOnlyList<string?[]> versions)
    {
        if (versions.Count == 0) return null;
        var info = Info(t.Name);
        switch (info?.Versioning)
        {
            case ServerVersioning.Patch when t.Has("patch"):
            {
                int i = t.IndexOf("patch");
                return versions.Where(r => WorldValue.Long(r[i]) <= Patch).OrderByDescending(r => WorldValue.Long(r[i])).FirstOrDefault();
            }
            case ServerVersioning.Build when t.Has("build"):
            {
                int i = t.IndexOf("build");
                return versions.Where(r => WorldValue.Long(r[i]) <= ClientBuild).OrderByDescending(r => WorldValue.Long(r[i])).FirstOrDefault();
            }
            case ServerVersioning.BuildExact when t.Has("build"):
                return versions.FirstOrDefault(r => WorldValue.Long(r[t.IndexOf("build")]) == ClientBuild);
            case ServerVersioning.PatchRange:
            case ServerVersioning.BuildRange:
                return versions.FirstOrDefault(r => RowVersion(t, r).Loaded) ?? versions[0];
            default:
                return versions[0];
        }
    }

    /// <summary>A new editing session (your saved server edits loaded from the workspace).</summary>
    public ServerEditor NewEditor() => new(this);

    /// <summary>Is the world server running (mangosd.exe)?</summary>
    public List<string> Running() => RunningServers();

    /// <summary>The server-edit files of the workspace for this database.</summary>
    public List<string> WorkspaceFiles() =>
        Workspace == null ? new() : Workspace.ModifiedPaths().Where(p => ServerChangeFiles.Parse(p) is { } x && string.Equals(x.Database, Database, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The tables of the workspace's saved server edits.</summary>
    public List<ServerTableChanges> SavedChanges()
    {
        var res = new List<ServerTableChanges>();
        if (Workspace == null) return res;
        foreach (var p in WorkspaceFiles())
        {
            var (_, table) = ServerChangeFiles.Parse(p)!.Value;
            var t = Schema.Table(table);
            var data = Workspace.ReadEdited(p);
            if (t == null || data == null) continue;
            try { res.Add(ServerChangeFiles.Read(data, t)); }
            catch (Exception e) when (e is ServerDataException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return res.OrderBy(c => c.Table.Name, StringComparer.Ordinal).ToList();
    }
}
