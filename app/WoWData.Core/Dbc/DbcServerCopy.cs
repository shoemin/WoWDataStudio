using System.Text.Json;
using System.Text.Json.Serialization;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Dbc;

/// <summary>One table of a "Copy to server": your edit and the server's copy.</summary>
public sealed record DbcServerItem(string Table, string WorkspaceFile, string ServerFile, bool ServerHasFile, bool Identical, DbcServerUse Use, string Note)
{
    /// <summary>Only tables the server reads from its dbc folder are ever copied.</summary>
    public bool CanCopy => Use == DbcServerUse.ServerFile && !Identical;

    public string StatusText => Use switch
    {
        DbcServerUse.ServerDatabase => "Not copied: the server reads this from its database. " + Note,
        DbcServerUse.ClientOnly => "Not copied: the server does not use this table.",
        _ => Identical ? "The server already has your version." : ServerHasFile ? "Will replace the server's copy (it is backed up first)." : "Will be added to the server's dbc folder.",
    };
}

/// <summary>A copy to the server's dbc folder, for "Undo copy to server".</summary>
public sealed class DbcServerCopyRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("server_dbc")] public string ServerDbc { get; set; } = "";
    [JsonPropertyName("backup")] public string Backup { get; set; } = "";
    /// <summary>Per file name: the SHA-256 written (to see later whether the server's copy is still ours).</summary>
    [JsonPropertyName("written")] public Dictionary<string, string> Written { get; set; } = new();
    /// <summary>File names that did not exist before (undo deletes them).</summary>
    [JsonPropertyName("added")] public List<string> Added { get; set; } = new();
    [JsonPropertyName("undone")] public DateTime? Undone { get; set; }
}

/// <summary>
/// "Copy to server": the world server (VMaNGOS mangosd) reads about 45 tables from its own dbc folder
/// (Server\data\5875\dbc) when it starts. After you change one of them, the server needs the same file or the server and
/// the game disagree (a mount model, a talent, a flight route...). This copies your edited tables there, like "Update
/// server maps":
///  * only tables the server reads from that folder (never Spell.dbc and the other tables the server takes from its
///    database: their server copies are the server's own edits);
///  * refused while mangosd.exe runs (it only reads the files at start; it must be restarted afterwards anyway);
///  * every file it replaces is backed up first (and the copy checked), files are written through a temporary name;
///  * each copy is recorded, and "Undo copy to server" puts the backups back (also refused while mangosd runs).
/// The app never starts or stops the server.
/// </summary>
public sealed class DbcServerCopy
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public DbcServerCopy(string serverDbcDir, string backupRoot, string historyFile, Func<List<string>>? runningServers = null)
    {
        ServerDbcDir = serverDbcDir;
        BackupRoot = backupRoot;
        HistoryFile = historyFile;
        RunningServers = runningServers ?? Maps.ServerMapsService.RunningWorldServers;
    }

    public string ServerDbcDir { get; }
    public string BackupRoot { get; }
    public string HistoryFile { get; }
    public Func<List<string>> RunningServers { get; }

    /// <summary>The server's dbc folder: &lt;server data&gt;\5875\dbc (VMaNGOS keeps one folder per client build), else
    /// &lt;server data&gt;\dbc.</summary>
    public static string DefaultServerDbcDir(string serverDataDir)
    {
        var build = Path.Combine(serverDataDir, "5875", "dbc");
        if (Directory.Exists(build)) return build;
        var flat = Path.Combine(serverDataDir, "dbc");
        return Directory.Exists(flat) ? flat : build;
    }

    /// <summary>Every edited table of the workspace with what a copy would do (nothing is written).</summary>
    public List<DbcServerItem> Plan(Ws ws, DbcLayouts? layouts = null)
    {
        layouts ??= DbcLayouts.Default;
        var res = new List<DbcServerItem>();
        foreach (var p in ws.ModifiedPaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var table = DbcService.TableOf(p);
            if (table == null) continue;
            var l = layouts.Get(table);
            var use = l?.ServerUse ?? DbcServerUse.ClientOnly;
            var wf = ws.WorkspaceFile(p);
            var sf = Path.Combine(ServerDbcDir, (l?.Name ?? table) + ".dbc");
            bool has = File.Exists(sf);
            bool same = has && File.Exists(wf) && Ws.Sha256File(sf) == Ws.Sha256File(wf);
            var note = use == DbcServerUse.ServerDatabase ? $"Edit the server's {l!.ServerTable} table instead." : "";
            res.Add(new DbcServerItem(l?.Name ?? table, wf, sf, has, same, use, note));
        }
        return res;
    }

    /// <summary>Copy the given tables (or every copyable one) into the server's dbc folder. Returns what was done.</summary>
    public (DbcServerCopyRecord Record, List<string> Done) Copy(Ws ws, IEnumerable<string>? tables = null, DbcLayouts? layouts = null)
    {
        var running = RunningServers();
        if (running.Count > 0)
            throw new DbcException($"The world server is running ({string.Join(", ", running)}). Stop it first: it reads its dbc files only when it starts, and they must not change under it.");
        if (!Directory.Exists(ServerDbcDir)) throw new DbcException($"The server's dbc folder does not exist: {ServerDbcDir}. Set the server data folder in Settings > Maps and terrain.");
        var want = tables?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = Plan(ws, layouts).Where(i => want == null || want.Contains(i.Table)).ToList();
        foreach (var i in plan.Where(i => want != null && i.Use != DbcServerUse.ServerFile))
            throw new DbcException($"{i.Table}.dbc is not copied to the server: {i.StatusText}");
        var items = plan.Where(i => i.CanCopy).ToList();
        var done = new List<string>();
        var rec = new DbcServerCopyRecord { Time = DateTime.Now, ServerDbc = ServerDbcDir };
        if (items.Count == 0)
        {
            done.Add("Nothing to copy: the server already has every edited table it reads.");
            return (rec, done);
        }
        // back up everything first, and check the copies, before touching the server
        var backup = Path.Combine(BackupRoot, "server_dbc_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        for (int k = 2; Directory.Exists(backup); k++) backup = Path.Combine(BackupRoot, $"server_dbc_{DateTime.Now:yyyyMMdd_HHmmss}_{k}");
        Directory.CreateDirectory(backup);
        rec.Backup = backup;
        foreach (var i in items.Where(i => i.ServerHasFile))
        {
            var b = Path.Combine(backup, Path.GetFileName(i.ServerFile));
            File.Copy(i.ServerFile, b, false);
            if (Ws.Sha256File(b) != Ws.Sha256File(i.ServerFile)) throw new DbcException($"The backup of {i.ServerFile} could not be verified; nothing was changed.");
        }
        if (RunningServers().Count > 0) throw new DbcException("The world server was started meanwhile; nothing was changed.");
        foreach (var i in items)
        {
            var name = Path.GetFileName(i.ServerFile);
            var tmp = i.ServerFile + ".wowdata_tmp";
            File.Copy(i.WorkspaceFile, tmp, true);
            var sha = Ws.Sha256File(tmp);
            if (sha != Ws.Sha256File(i.WorkspaceFile)) throw new DbcException($"Copying {name} failed (the copy differs).");
            File.Move(tmp, i.ServerFile, true);
            rec.Written[name] = sha;
            if (!i.ServerHasFile) rec.Added.Add(name);
            done.Add(i.ServerHasFile ? $"{name}: replaced (the server's copy is backed up in {backup})." : $"{name}: added to the server.");
        }
        var hist = LoadHistory();
        hist.Add(rec);
        SaveHistory(hist);
        done.Add("Restart the world server (mangosd) so it reads the new files.");
        return (rec, done);
    }

    /// <summary>Put the server's files of a copy back (refused while mangosd runs). A file changed since the copy is
    /// left alone and reported.</summary>
    public List<string> Undo(DbcServerCopyRecord rec)
    {
        var running = RunningServers();
        if (running.Count > 0) throw new DbcException($"The world server is running ({string.Join(", ", running)}). Stop it first.");
        var done = new List<string>();
        foreach (var (name, sha) in rec.Written)
        {
            var f = Path.Combine(rec.ServerDbc, name);
            if (File.Exists(f) && Ws.Sha256File(f) != sha)
            {
                done.Add($"{name}: changed since the copy, left as it is.");
                continue;
            }
            var b = Path.Combine(rec.Backup, name);
            if (rec.Added.Contains(name))
            {
                if (File.Exists(f)) File.Delete(f);
                done.Add($"{name}: removed (the server did not have it before).");
            }
            else if (File.Exists(b))
            {
                File.Copy(b, f + ".wowdata_tmp", true);
                File.Move(f + ".wowdata_tmp", f, true);
                done.Add($"{name}: the server's previous copy was put back.");
            }
            else done.Add($"{name}: no backup found in {rec.Backup}.");
        }
        var hist = LoadHistory();
        foreach (var h in hist.Where(h => h.Time == rec.Time && h.Backup == rec.Backup)) h.Undone = DateTime.Now;
        SaveHistory(hist);
        rec.Undone = DateTime.Now;
        done.Add("Restart the world server (mangosd) so it reads the files again.");
        return done;
    }

    public List<DbcServerCopyRecord> LoadHistory()
    {
        try { return File.Exists(HistoryFile) ? JsonSerializer.Deserialize<List<DbcServerCopyRecord>>(File.ReadAllText(HistoryFile)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveHistory(List<DbcServerCopyRecord> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        File.WriteAllText(HistoryFile + ".tmp", JsonSerializer.Serialize(list, Json));
        File.Move(HistoryFile + ".tmp", HistoryFile, true);
    }
}
