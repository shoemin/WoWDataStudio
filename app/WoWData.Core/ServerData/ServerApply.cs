using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWData.Core.ServerData;

/// <summary>An SQL patch of your server edits and the script that takes it back.</summary>
public sealed class ServerSqlScript
{
    public string Patch { get; init; } = "";
    public string Revert { get; init; } = "";
    /// <summary>The statements of the patch (one per line, without comments).</summary>
    public List<string> Statements { get; init; } = new();
    public List<string> RevertStatements { get; init; } = new();
    public List<string> Tables { get; init; } = new();
    public int Rows { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// 0.9.0: SQL scripts of server edits. The patch is idempotent: every changed or added row is first deleted by its key
/// and then inserted exactly as it should be, every deleted row is deleted by its key, so running it twice gives the
/// same database. The revert script puts the rows back as they were on the server before your edits (deleted by key,
/// then inserted as they were). One statement per line; FLOAT values are written with 9 digits (MySQL reads back exactly
/// the same number); texts are escaped. No host, user or password is ever written into a script.
/// </summary>
public static class ServerSql
{
    private static string Where(WorldTable t, string keyText)
    {
        var k = WorldTable.ParseKeyText(keyText);
        return string.Join(" AND ", t.Key.Select(c =>
        {
            var col = t.Column(c)!;
            var v = k.GetValueOrDefault(c);
            return v == null ? $"{WorldValue.Name(c)} IS NULL" : $"{WorldValue.Name(c)}={WorldValue.Sql(col, v)}";
        }));
    }

    public static string Delete(WorldTable t, string keyText) => $"DELETE FROM {WorldValue.Name(t.Name)} WHERE {Where(t, keyText)};";

    public static string Insert(WorldTable t, IReadOnlyList<string?> row) =>
        $"INSERT INTO {WorldValue.Name(t.Name)} ({string.Join(",", t.Columns.Select(c => WorldValue.Name(c.Name)))}) VALUES ({string.Join(",", t.Columns.Select((c, i) => WorldValue.Sql(c, row[i])))});";

    /// <summary>The comment line that says what a row change is.</summary>
    private static string Describe(ServerRowChange r, WorldTable t)
    {
        if (r.IsAdded) return $"-- {t.Name} {r.Key}: new row";
        if (r.IsDeleted) return $"-- {t.Name} {r.Key}: deleted";
        var diffs = t.Columns.Select((c, i) => (c, i)).Where(x => !WorldValue.Same(x.c, r.Before![x.i], r.After![x.i]))
            .Select(x => $"{x.c.Name} {Short(r.Before![x.i])} -> {Short(r.After![x.i])}").ToList();
        return $"-- {t.Name} {r.Key}: " + string.Join(", ", diffs.Take(8)) + (diffs.Count > 8 ? $", and {diffs.Count - 8} more" : "");
    }

    private static string Short(string? v) => v == null ? "NULL" : (v.Length > 30 ? v[..27] + "..." : v).Replace("\n", " ").Replace("\r", " ");

    /// <summary>The SQL patch and its revert script for the given edits.</summary>
    public static ServerSqlScript Build(ServerDataService svc, IReadOnlyList<ServerTableChanges> changes, DateTime? when = null)
    {
        var now = when ?? DateTime.Now;
        var tables = changes.Where(c => c.Rows.Count > 0).OrderBy(c => c.Table.Name, StringComparer.Ordinal).ToList();
        int rows = tables.Sum(c => c.Rows.Count);
        var summary = string.Join("; ", tables.Select(c => c.Summary));
        var schema = svc.Schema;
        var reload = ReloadNotes(svc, tables.Select(c => c.Table.Name));
        string Header(string what, string other) =>
            $"-- WoW Data Studio {StudioInfo.Display}: {what}\n" +
            $"-- Made {now:yyyy-MM-dd HH:mm} from the workspace's server edits for the world database \"{svc.Database}\"" +
            (schema.Migration.Length > 0 ? $" (VMaNGOS, schema migration {schema.Migration})" : "") + ".\n" +
            $"-- {rows} row{(rows == 1 ? "" : "s")}: {summary}\n" +
            "--\n" +
            "-- It can be run more than once: every row is first deleted by its key and then written as it should be.\n" +
            $"-- The matching script: {other}\n" +
            "-- Run it on the WORLD database only, while you can restart the world server, for example:\n" +
            $"--     mysql -u <user> -p {svc.Database} < this-file.sql\n" +
            "-- or use \"Apply to server\" in WoW Data Studio (it backs up the rows first and keeps a log).\n" +
            string.Join("", reload.Select(r => "-- " + r + "\n")) +
            "SET NAMES utf8;\n" +
            "SET @WDS_OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='STRICT_ALL_TABLES';\n" +
            "START TRANSACTION;\n";
        const string footer = "COMMIT;\nSET SQL_MODE=@WDS_OLD_SQL_MODE;\n";
        var patch = new StringBuilder();
        var revert = new StringBuilder();
        var stmts = new List<string>();
        var rstmts = new List<string>();
        foreach (var ch in tables)
        {
            var t = ch.Table;
            patch.Append($"\n-- ---- {t.Name}: {ch.Summary.Substring(t.Name.Length + 2)}\n");
            revert.Append($"\n-- ---- {t.Name}\n");
            foreach (var r in ch.Rows.Values)
            {
                patch.Append(Describe(r, t)).Append('\n');
                var d = Delete(t, r.Key);
                patch.Append(d).Append('\n');
                stmts.Add(d);
                if (r.After != null)
                {
                    var ins = Insert(t, r.After);
                    patch.Append(ins).Append('\n');
                    stmts.Add(ins);
                }
                revert.Append($"-- {t.Name} {r.Key}: back to {(r.Before == null ? "not existing" : "the server's row")}\n");
                revert.Append(d).Append('\n');
                rstmts.Add(d);
                if (r.Before != null)
                {
                    var ins = Insert(t, r.Before);
                    revert.Append(ins).Append('\n');
                    rstmts.Add(ins);
                }
            }
        }
        return new ServerSqlScript
        {
            Patch = Header("server data patch", "the revert script (same name, .revert.sql) puts the rows back as they were before these edits.") + patch + "\n" + footer,
            Revert = Header("revert script of a server data patch", "the patch (same name without .revert) applies the edits again.") + revert + "\n" + footer,
            Statements = stmts,
            RevertStatements = rstmts,
            Tables = tables.Select(c => c.Table.Name).ToList(),
            Rows = rows,
            Summary = summary,
        };
    }

    /// <summary>What the server needs after the tables changed: the GM ".reload" commands VMaNGOS has, or a restart; and
    /// the game's cache.</summary>
    public static List<string> ReloadNotes(ServerDataService svc, IEnumerable<string> tables)
    {
        var list = tables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var res = new List<string>();
        var infos = list.Select(t => (t, svc.Info(t))).ToList();
        var restart = infos.Where(x => x.Item2 == null || !x.Item2.CanReload).Select(x => x.t).ToList();
        var reload = infos.Where(x => x.Item2?.CanReload == true).Select(x => x.Item2!.Reload).Distinct().ToList();
        res.Add("The world server (mangosd) reads these tables when it starts.");
        if (restart.Count > 0) res.Add($"Restart it to use {string.Join(", ", restart)} (VMaNGOS cannot reload {(restart.Count == 1 ? "this table" : "these tables")} while it runs).");
        if (reload.Count > 0) res.Add((restart.Count > 0 ? "The others can also be reloaded" : "Restart it, or reload them") + $" while it runs, as a GM in the game: {string.Join(", ", reload)} (objects already in the world may keep their old values until they respawn).");
        if (list.Any(t => t is "item_template" or "creature_template" or "quest_template" or "gameobject_template" or "page_text" or "npc_text" or "locales_item" or "locales_creature" or "locales_quest" or "locales_gameobject"))
            res.Add("The game caches items, creatures, quests and game objects (its WDB folder): clear the cache (\"Clear the game's cache (WDB)...\") so it asks the server again.");
        return res;
    }
}

/// <summary>One "Apply to server", for the log and "Undo last apply".</summary>
public sealed class ServerApplyRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("database")] public string Database { get; set; } = "";
    /// <summary>host:port (never the user or the password).</summary>
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("tables")] public List<string> Tables { get; set; } = new();
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    /// <summary>Per table, the keys the apply touched and how each row is after it (null: deleted).</summary>
    [JsonPropertyName("applied")] public Dictionary<string, Dictionary<string, Dictionary<string, string?>?>> Applied { get; set; } = new();
    [JsonPropertyName("workspace_files")] public List<string> WorkspaceFiles { get; set; } = new();
    [JsonPropertyName("undone")] public DateTime? Undone { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = "";

    [JsonIgnore] public string PatchFile => Path.Combine(Folder, "patch.sql");
    [JsonIgnore] public string RevertFile => Path.Combine(Folder, "patch.revert.sql");
    [JsonIgnore] public string BackupRowsFile => Path.Combine(Folder, "backup_rows.sql");
    [JsonIgnore] public string BackupTablesFile => Path.Combine(Folder, "backup_tables.sql");
}

/// <summary>What "Apply to server" will do, before it does it.</summary>
public sealed class ServerApplyPlan
{
    public ServerSqlScript Script { get; init; } = new();
    public List<ServerTableChanges> Changes { get; init; } = new();
    /// <summary>Rows the server changed since your first edit (applying overwrites them).</summary>
    public List<string> Conflicts { get; init; } = new();
    /// <summary>Rows that are already as you want them on the server.</summary>
    public List<string> AlreadyApplied { get; init; } = new();
    public List<string> Running { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    /// <summary>Tables that cannot be rolled back by a transaction (MyISAM): a failure is taken back from the backup.</summary>
    public List<string> NonTransactional { get; init; } = new();
    public string Target { get; init; } = "";
    public bool IsEmpty => Changes.All(c => c.Rows.Count == 0);
    public bool NothingToDo => IsEmpty || AlreadyApplied.Count == Changes.Sum(c => c.Rows.Count);
}

/// <summary>
/// 0.9.0: "Apply to server" and "Undo last apply". Guided and careful:
///  * only into the world database (never the characters or realm database; <see cref="WorldDb.IsWorldDatabase"/>);
///  * the exact statements are shown first (the plan), with the rows the server changed since your first edit;
///  * before anything is written, every row the apply touches is saved (backup_rows.sql, which is also what "Undo last
///    apply" runs) and every affected table is dumped whole (backup_tables.sql), and both files are read back;
///  * the statements run in one transaction in strict SQL mode; VMaNGOS keeps most tables in MyISAM, which cannot roll
///    back, so on a failure the backup rows are written back and the result says so;
///  * every apply is logged (server_db_applies.json and applied_changes.log, with the scripts in its folder);
///  * the world server is not stopped or started by the app. It only reads these tables at start (or with .reload):
///    the result says what to do. Applying while it runs is allowed after a warning (nothing breaks; it just keeps the
///    old data until it reloads).
/// </summary>
public sealed class ServerApplier
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Regex WriteStatement = new(@"^(INSERT INTO `|DELETE FROM `|SET |START TRANSACTION;|COMMIT;)", RegexOptions.CultureInvariant);

    public ServerApplier(ServerDataService svc) { Svc = svc; }

    public ServerDataService Svc { get; }

    private string Target => $"the world database \"{Svc.Database}\" on {Svc.Db.Settings.Host}:{Svc.Db.Settings.Port}";

    /// <summary>What applying the given edits (by default: everything saved in the workspace) would do.</summary>
    public ServerApplyPlan Plan(IReadOnlyList<ServerTableChanges>? changes = null)
    {
        var list = (changes ?? Svc.SavedChanges()).Where(c => c.Rows.Count > 0).ToList();
        var conflicts = new List<string>();
        var already = new List<string>();
        using var conn = Svc.Db.Open();
        foreach (var ch in list)
        {
            var t = ch.Table;
            foreach (var r in ch.Rows.Values)
            {
                var now = Svc.Db.Row(t, r.Key, conn);
                if (Same(t, now, r.After)) already.Add($"{t.Name} {r.Key}");
                else if (!Same(t, now, r.Before))
                    conflicts.Add($"{t.Name} {r.Key}: the server's row " + (now == null ? "was deleted" : r.Before == null ? "exists now" : "changed") + " since you started editing it; applying replaces it with yours.");
            }
        }
        return new ServerApplyPlan
        {
            Script = ServerSql.Build(Svc, list),
            Changes = list,
            Conflicts = conflicts,
            AlreadyApplied = already,
            Running = Svc.Running(),
            Notes = ServerSql.ReloadNotes(Svc, list.Select(c => c.Table.Name)),
            NonTransactional = list.Where(c => !c.Table.Transactional).Select(c => c.Table.Name).ToList(),
            Target = Target,
        };
    }

    private static bool Same(WorldTable t, string?[]? a, string?[]? b)
    {
        if (a == null || b == null) return a == b;
        for (int i = 0; i < t.Columns.Count; i++)
            if (!WorldValue.Same(t.Columns[i], a[i], b[i])) return false;
        return true;
    }

    private void CheckTarget()
    {
        if (!Svc.Db.IsWorldDatabase(out var why)) throw new ServerDataException("Refused: " + why);
        if (Svc.StateDir == null) throw new ServerDataException("No game folder is open (the backups and the log are kept in its folder).");
    }

    private static string NewFolder(string root, string prefix)
    {
        var dir = Path.Combine(root, prefix + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        for (int k = 2; Directory.Exists(dir); k++) dir = Path.Combine(root, $"{prefix}{DateTime.Now:yyyyMMdd_HHmmss}_{k}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteChecked(string file, string text)
    {
        File.WriteAllText(file, text, new UTF8Encoding(false));
        if (File.ReadAllText(file, Encoding.UTF8) != text) throw new ServerDataException($"The backup file {file} could not be verified; nothing was changed.");
    }

    /// <summary>The current rows of every key the edits touch, as statements that put them back.</summary>
    private string BackupRows(IEnumerable<(WorldTable Table, string Key)> keys, MySqlConnection conn, out List<string> statements)
    {
        var sb = new StringBuilder("-- WoW Data Studio: the rows as they were on the server before the apply (\"Undo last apply\" runs this)\nSET NAMES utf8;\nSTART TRANSACTION;\n");
        statements = new List<string>();
        foreach (var (t, key) in keys)
        {
            var now = Svc.Db.Row(t, key, conn);
            var d = ServerSql.Delete(t, key);
            sb.Append(d).Append('\n');
            statements.Add(d);
            if (now != null)
            {
                var ins = ServerSql.Insert(t, now);
                sb.Append(ins).Append('\n');
                statements.Add(ins);
            }
        }
        sb.Append("COMMIT;\n");
        return sb.ToString();
    }

    /// <summary>Run statements in one transaction (strict SQL mode). On a failure: roll back, and write the backup rows
    /// back (MyISAM tables cannot roll back).</summary>
    private void Run(MySqlConnection conn, IReadOnlyList<string> statements, IReadOnlyList<string> restore, List<string> done)
    {
        void Exec(string sql, MySqlTransaction? tx)
        {
            if (!WriteStatement.IsMatch(sql)) throw new ServerDataException($"Refused to run an unexpected statement: {sql[..Math.Min(60, sql.Length)]}");
            using var cmd = new MySqlCommand(sql, conn, tx);
            cmd.ExecuteNonQuery();
        }
        using (var mode = new MySqlCommand("SET NAMES utf8; SET SESSION SQL_MODE='STRICT_ALL_TABLES'", conn)) mode.ExecuteNonQuery();
        var tx = conn.BeginTransaction();
        try
        {
            foreach (var s in statements) Exec(s, tx);
            tx.Commit();
        }
        catch (Exception e) when (e is MySqlException or ServerDataException)
        {
            try { tx.Rollback(); }
            catch (MySqlException) { }
            // tables that are not transactional keep what was written: put the backup rows back
            try
            {
                var tx2 = conn.BeginTransaction();
                foreach (var s in restore) Exec(s, tx2);
                tx2.Commit();
                done.Add("The rows were put back from the backup: the server's database is as it was before.");
            }
            catch (Exception e2) when (e2 is MySqlException or ServerDataException)
            {
                done.Add($"Putting the rows back failed too ({e2.Message}). The backup files are kept; run backup_rows.sql of the backup folder to restore them.");
            }
            throw new ServerDataException($"Applying failed: {e.Message}. " + string.Join(" ", done), e);
        }
        finally
        {
            tx.Dispose();
        }
    }

    /// <summary>Apply the plan: backup, run, verify, log. <paramref name="overwriteConflicts"/>: apply although rows changed
    /// on the server since your first edit.</summary>
    public (ServerApplyRecord Record, List<string> Done) Apply(ServerApplyPlan plan, bool overwriteConflicts = false)
    {
        CheckTarget();
        if (plan.IsEmpty) throw new ServerDataException("There are no server edits to apply. Edit something in the Server data tab and save it into your workspace first.");
        if (plan.Conflicts.Count > 0 && !overwriteConflicts)
            throw new ServerDataException($"Refused: {plan.Conflicts.Count} row(s) changed on the server since you started editing them:\n" + string.Join("\n", plan.Conflicts.Take(10)) + "\nLook at them first; apply anyway to overwrite them with yours.");
        var done = new List<string>();
        var folder = NewFolder(Svc.BackupRoot, "apply_");
        var keys = plan.Changes.SelectMany(c => c.Rows.Keys.Select(k => (c.Table, k))).ToList();
        using var conn = Svc.Db.Open();
        // 1. the backups, read back before anything is written
        var rowsText = BackupRows(keys, conn, out var restore);
        WriteChecked(Path.Combine(folder, "backup_rows.sql"), rowsText);
        using (var w = new StreamWriter(Path.Combine(folder, "backup_tables.sql"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("-- WoW Data Studio: the whole tables as they were before the apply (for a manual restore). SET NAMES utf8 first.");
            w.WriteLine("SET NAMES utf8;");
            foreach (var t in plan.Changes.Select(c => c.Table)) Svc.Db.DumpTable(t, w);
        }
        WriteChecked(Path.Combine(folder, "patch.sql"), plan.Script.Patch);
        WriteChecked(Path.Combine(folder, "patch.revert.sql"), plan.Script.Revert);
        done.Add($"Backed up {keys.Count} row(s) and {plan.Changes.Count} whole table(s) into {folder}.");
        // 2. the statements, in one transaction
        Run(conn, plan.Script.Statements, restore, done);
        // 3. read back
        var rec = new ServerApplyRecord
        {
            Time = DateTime.Now,
            Database = Svc.Database,
            Server = $"{Svc.Db.Settings.Host}:{Svc.Db.Settings.Port}",
            Tables = plan.Script.Tables,
            Rows = plan.Script.Rows,
            Summary = plan.Script.Summary,
            Folder = folder,
            WorkspaceFiles = plan.Changes.Select(c => ServerChangeFiles.PathOf(Svc.Database, c.Table.Name)).ToList(),
        };
        int wrong = 0;
        foreach (var ch in plan.Changes)
        {
            var map = new Dictionary<string, Dictionary<string, string?>?>();
            foreach (var r in ch.Rows.Values)
            {
                var now = Svc.Db.Row(ch.Table, r.Key, conn);
                if (!Same(ch.Table, now, r.After)) wrong++;
                map[r.Key] = r.After == null ? null : ch.Table.Columns.Select((c, i) => (c.Name, r.After[i])).ToDictionary(x => x.Name, x => x.Item2);
            }
            rec.Applied[ch.Table.Name] = map;
        }
        done.Add(wrong == 0 ? $"Applied {plan.Script.Rows} row(s) to {Target} and read them back: all as you edited them." : $"Applied, but {wrong} row(s) read back differently (a trigger or the server changed them?).");
        done.AddRange(plan.Notes);
        rec.Result = string.Join(" ", done);
        var hist = History();
        hist.Add(rec);
        SaveHistory(hist);
        File.AppendAllText(Path.Combine(Svc.StateDir!, "applied_changes.log"),
            $"{rec.Time:yyyy-MM-dd HH:mm:ss}  APPLY  {Target}: {rec.Rows} row(s): {rec.Summary}. Backup: {folder}\n", new UTF8Encoding(false));
        Svc.Workspace?.MarkServerApplied(rec.WorkspaceFiles, rec.Time, Target);
        return (rec, done);
    }

    public List<ServerApplyRecord> History()
    {
        try { return File.Exists(Svc.ApplyLogFile) ? JsonSerializer.Deserialize<List<ServerApplyRecord>>(File.ReadAllText(Svc.ApplyLogFile)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveHistory(List<ServerApplyRecord> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Svc.ApplyLogFile)!);
        File.WriteAllText(Svc.ApplyLogFile + ".tmp", JsonSerializer.Serialize(list, Json));
        File.Move(Svc.ApplyLogFile + ".tmp", Svc.ApplyLogFile, true);
    }

    /// <summary>The last apply that was not undone (for this database), or null.</summary>
    public ServerApplyRecord? Last() => History().LastOrDefault(h => h.Undone == null && string.Equals(h.Database, Svc.Database, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rows that changed on the server since the apply (undoing would overwrite them).</summary>
    public List<string> UndoConflicts(ServerApplyRecord rec)
    {
        var res = new List<string>();
        using var conn = Svc.Db.Open();
        foreach (var (table, rows) in rec.Applied)
        {
            var t = Svc.Schema.Table(table);
            if (t == null) continue;
            foreach (var (key, applied) in rows)
            {
                var now = Svc.Db.Row(t, key, conn);
                var want = applied == null ? null : t.Columns.Select(c => applied.GetValueOrDefault(c.Name)).ToArray();
                if (!Same(t, now, want)) res.Add($"{table} {key}: changed on the server since the apply.");
            }
        }
        return res;
    }

    /// <summary>"Undo last apply": the rows are put back as they were before it (from its backup). Refused when rows
    /// changed since, unless <paramref name="overwrite"/>.</summary>
    public List<string> Undo(ServerApplyRecord rec, bool overwrite = false)
    {
        CheckTarget();
        if (rec.Undone != null) throw new ServerDataException("This apply was already undone.");
        if (!File.Exists(rec.BackupRowsFile)) throw new ServerDataException($"The backup of this apply is missing: {rec.BackupRowsFile}.");
        var conflicts = UndoConflicts(rec);
        if (conflicts.Count > 0 && !overwrite)
            throw new ServerDataException($"Refused: {conflicts.Count} row(s) changed on the server since the apply:\n" + string.Join("\n", conflicts.Take(10)) + "\nUndo anyway to overwrite them.");
        var statements = File.ReadAllLines(rec.BackupRowsFile, Encoding.UTF8).Where(l => l.StartsWith("INSERT INTO `", StringComparison.Ordinal) || l.StartsWith("DELETE FROM `", StringComparison.Ordinal)).ToList();
        var done = new List<string>();
        var folder = NewFolder(Svc.BackupRoot, "undo_");
        using var conn = Svc.Db.Open();
        var keys = rec.Applied.SelectMany(kv => kv.Value.Keys.Select(k => (Svc.Schema.Require(kv.Key), k))).ToList();
        WriteChecked(Path.Combine(folder, "backup_rows.sql"), BackupRows(keys, conn, out var restore));
        done.Add($"Backed up the {keys.Count} row(s) as they are now into {folder}.");
        Run(conn, statements, restore, done);
        done.Add($"The rows of the apply of {rec.Time:yyyy-MM-dd HH:mm} are back as they were before it ({rec.Rows} row(s): {rec.Summary}).");
        done.AddRange(ServerSql.ReloadNotes(Svc, rec.Tables));
        var hist = History();
        foreach (var h in hist.Where(h => h.Time == rec.Time && h.Folder == rec.Folder)) h.Undone = DateTime.Now;
        SaveHistory(hist);
        rec.Undone = DateTime.Now;
        File.AppendAllText(Path.Combine(Svc.StateDir!, "applied_changes.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  UNDO   {Target}: the apply of {rec.Time:yyyy-MM-dd HH:mm:ss} ({rec.Rows} row(s)). Backup of the rows before the undo: {folder}\n", new UTF8Encoding(false));
        Svc.Workspace?.MarkServerApplied(rec.WorkspaceFiles, null, null);
        done.Add("Your edits are still in your workspace: apply them again at any time, or revert them in My changes.");
        return done;
    }
}
