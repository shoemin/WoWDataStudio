using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.Workspace;

namespace WoWData.Core.ServerData;

/// <summary>The result of an edit: refused (with the reason) or done, with the warnings of the value and the row.</summary>
public sealed record ServerEditResult(bool Ok, string Message, IReadOnlyList<ServerProblem> Problems, int Changed = 0)
{
    public static ServerEditResult Refused(string message, IReadOnlyList<ServerProblem>? problems = null) => new(false, message, problems ?? Array.Empty<ServerProblem>());
    public IEnumerable<ServerProblem> Warnings => Problems.Where(p => !p.IsError);
}

/// <summary>
/// 0.9.0: one editing session of the server's world database, shared by the Server data tab and <c>wds server</c>.
/// It shows the server's rows with your edits on top (your saved edits from the workspace plus the unsaved ones),
/// checks every value and row (<see cref="ServerValidation"/>), adds, duplicates (with a free ID of your custom ID
/// range), deletes (saying what refers to the rows) and bulk-edits rows, keeps an undo / redo history, and saves the
/// edits into the workspace (one file per table, one change set per save). Nothing is ever written into the database
/// here: that is <see cref="ServerApplier"/>'s job.
/// </summary>
public sealed class ServerEditor
{
    private sealed record StepItem(string Table, string Key, ServerRowChange? Old, ServerRowChange? New);
    private sealed record Step(string Label, List<StepItem> Items);

    private readonly Dictionary<string, ServerTableChanges> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Step> _undo = new();
    private readonly List<Step> _redo = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long), bool> _exists = new();
    // 0.13.0: bumped whenever the cache is cleared, so an answer computed before an edit is never stored after it
    private int _existsGen;

    private void ClearExists()
    {
        Interlocked.Increment(ref _existsGen);
        _exists.Clear();
    }
    // the app reads the editor on background threads (lists, records) while the UI thread edits: one lock for the edits
    private readonly object _gate = new();

    public ServerEditor(ServerDataService service)
    {
        Service = service;
        LoadSaved();
    }

    public ServerDataService Service { get; }
    public WorldSchema Schema => Service.Schema;

    private void LoadSaved()
    {
        var saved = Service.SavedChanges();
        lock (_gate) LoadSavedCore(saved);
    }

    private void LoadSavedCore(List<ServerTableChanges> saved)
    {
        _changes.Clear();
        _saved.Clear();
        foreach (var ch in saved)
        {
            _changes[ch.Table.Name] = ch;
            _saved[ch.Table.Name] = ServerChangeFiles.Write(Service.Database, ch);
        }
        ClearExists();
    }

    /// <summary>Every table with edits (saved or not).</summary>
    public IReadOnlyList<ServerTableChanges> Changes
    {
        get
        {
            lock (_gate) return _changes.Values.Where(c => c.Rows.Count > 0).OrderBy(c => c.Table.Name, StringComparer.Ordinal).Select(c => c.Clone()).ToList();
        }
    }

    /// <summary>A copy of the edits of one table (null: none).</summary>
    public ServerTableChanges? ChangesOf(string table)
    {
        lock (_gate) return _changes.GetValueOrDefault(table)?.Clone();
    }

    /// <summary>The tables whose edits differ from what is saved in the workspace.</summary>
    public List<string> DirtyTables()
    {
        lock (_gate) return DirtyTablesCore();
    }

    private List<string> DirtyTablesCore()
    {
        var res = new List<string>();
        foreach (var name in _changes.Keys.Concat(_saved.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ch = _changes.GetValueOrDefault(name);
            var now = ch == null || ch.Rows.Count == 0 ? null : ServerChangeFiles.Write(Service.Database, ch);
            var was = _saved.GetValueOrDefault(name);
            if (now == null ? was != null : was == null || !now.AsSpan().SequenceEqual(was)) res.Add(name);
        }
        return res;
    }

    public bool IsDirty => DirtyTables().Count > 0;
    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _redo.Count > 0; } }
    public string UndoText { get { lock (_gate) return _undo.Count > 0 ? "Undo: " + _undo[^1].Label : "Nothing to undo"; } }
    public string RedoText { get { lock (_gate) return _redo.Count > 0 ? "Redo: " + _redo[^1].Label : "Nothing to redo"; } }

    // ------------------------------------------------------------------ reading (the server's rows with your edits on top)

    public WorldTable Table(string name) => Service.Table(name);

    /// <summary>The row of a key as you see it (your edit, else the server's), or null.</summary>
    public string?[]? Current(WorldTable t, string key)
    {
        lock (_gate)
            if (_changes.TryGetValue(t.Name, out var ch) && ch.Rows.TryGetValue(key, out var c)) return c.After;
        return Service.Db.Row(t, key);
    }

    /// <summary>The server's own row of a key (without your edits), or null.</summary>
    public string?[]? ServerRow(WorldTable t, string key) => Service.Db.Row(t, key);

    /// <summary>The rows of a table that match a filter, as you see them, sorted by key.</summary>
    public List<string?[]> Rows(WorldTable t, RowFilter filter, int limit = 0)
    {
        var db = Service.Db.Rows(t, filter, limit);
        ServerTableChanges? ch;
        lock (_gate) ch = _changes.GetValueOrDefault(t.Name)?.Clone();
        if (ch == null || ch.Rows.Count == 0) return db;
        var res = db.Where(r => !ch.Rows.ContainsKey(t.KeyText(r))).ToList();
        foreach (var c in ch.Rows.Values)
            if (c.After != null && filter.Matches(t, c.After)) res.Add(c.After);
        res.Sort((a, b) => CompareKeys(t, a, b));
        return limit > 0 && res.Count > limit ? res.Take(limit).ToList() : res;
    }

    public static int CompareKeys(WorldTable t, IReadOnlyList<string?> a, IReadOnlyList<string?> b)
    {
        foreach (var i in t.KeyIndexes)
        {
            var c = t.Columns[i];
            int x = c.IsNumber ? WorldValue.Number(a[i]).CompareTo(WorldValue.Number(b[i])) : string.CompareOrdinal(a[i], b[i]);
            if (x != 0) return x;
        }
        return 0;
    }

    /// <summary>Every version of an object (rows with this ID), as you see them.</summary>
    public List<string?[]> Versions(WorldTable t, long id) => Rows(t, RowFilter.Eq(Service.IdColumn(t), id));

    /// <summary>The row the server uses of an object (its patch / build), as you see it.</summary>
    public string?[]? Object(WorldTable t, long id) => Service.Effective(t, Versions(t, id));

    /// <summary>A key given as "25", "entry=25", "entry=25;patch=0" or "entry=25,patch=0": the key text of the row it means
    /// (a bare ID of a versioned table means the row the server uses).</summary>
    public string ResolveKey(WorldTable t, string text)
    {
        text = text.Trim();
        if (text.Contains('='))
        {
            var parts = text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToList();
            var given = parts.ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "", StringComparer.OrdinalIgnoreCase);
            foreach (var k in given.Keys)
                if (!t.Has(k)) throw new ServerDataException($"{t.Name} has no column {k}. Its key: {string.Join(", ", t.Key)}.");
            if (t.Key.All(given.ContainsKey))
                return string.Join(";", t.Key.Select(k => k + "=" + Canon(t, k, given[k])));
            // part of the key: the rows that match; the one the server uses, else the first
            var f = RowFilter.Where(given.Select(kv => new FilterTerm(kv.Key, FilterOp.Eq, Canon(t, kv.Key, kv.Value))).ToArray());
            var rows = Rows(t, f);
            var pick = Service.Effective(t, rows) ?? rows.FirstOrDefault();
            return pick != null ? t.KeyText(pick) : throw new ServerDataException($"{t.Name} has no row with {text}.");
        }
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            throw new ServerDataException($"\"{text}\" is not a key of {t.Name}: give its ID, or column=value pairs ({string.Join(", ", t.Key.Select(k => k + "=..."))}).");
        if (t.Key.Count == 1) return t.Key[0] + "=" + id.ToString(CultureInfo.InvariantCulture);
        var row = Object(t, id) ?? Versions(t, id).FirstOrDefault();
        if (row == null) throw new ServerDataException($"{t.Name} has no row with {Service.IdColumn(t)} {id}.");
        return t.KeyText(row);
    }

    private static string? Canon(WorldTable t, string col, string v)
    {
        var c = t.Column(col)!;
        return WorldValue.Normalize(c, v, out var canon) == null ? canon : v;
    }

    // ------------------------------------------------------------------ links and names

    public string KindText(string target) =>
        target.StartsWith("dbc:", StringComparison.Ordinal) ? target[4..] : Service.Layouts.Kind(target)?.Plain is { } p ? char.ToUpperInvariant(p[0]) + p[1..] : target;

    /// <summary>0.12.0: the game's tables as the designers see them (with their unsaved rows); null = the saved ones.</summary>
    public DbcTableLookup? GameTables { get; set; }

    /// <summary>Does the row a link points at exist (on the server with your edits, or in the game's table)?</summary>
    public bool LinkExists(string target, long id)
    {
        bool live = GameTables != null && target.StartsWith("dbc:", StringComparison.Ordinal);   // the designers' tables change: no cache
        if (!live && _exists.TryGetValue((target, id), out var known)) return known;
        int gen = Volatile.Read(ref _existsGen);
        bool ok;
        if (target.StartsWith("dbc:", StringComparison.Ordinal))
        {
            var dbc = Service.Dbc;
            if (dbc == null) ok = true;   // the game's tables are not available: do not refuse
            else
            {
                DbcTable? table;
                try { table = GameTables?.Invoke(target[4..]) ?? dbc.Lookup(target[4..]); }
                catch (Exception e) when (e is DbcException or IOException) { table = null; }
                ok = table == null || table.Find(id) != null;
            }
        }
        else
        {
            var kind = Service.Layouts.Kind(target);
            var t = kind == null ? null : Schema.Table(kind.Table);
            ok = t == null || Rows(t, RowFilter.Eq(kind!.KeyColumn, id), 1).Count > 0;
        }
        if (gen == Volatile.Read(ref _existsGen)) _exists[(target, id)] = ok;
        return ok;
    }

    /// <summary>The name of the row a link points at (null: it does not exist).</summary>
    public string? Label(string target, long id) => Labels(target, new[] { id }).GetValueOrDefault(id);

    /// <summary>The names of many linked rows at once.</summary>
    public Dictionary<long, string> Labels(string target, IEnumerable<long> ids, int depth = 0)
    {
        var res = new Dictionary<long, string>();
        var list = ids.Distinct().ToList();
        int gen = Volatile.Read(ref _existsGen);
        if (list.Count == 0) return res;
        if (target.StartsWith("dbc:", StringComparison.Ordinal))
        {
            var dbc = Service.Dbc;
            if (dbc == null) return res;
            foreach (var id in list)
                try
                {
                    if (dbc.LinkLabel(target[4..], id, dbc.Lookup) is { } l) res[id] = l;
                }
                catch (Exception e) when (e is DbcException or IOException) { }
            return res;
        }
        var kind = Service.Layouts.Kind(target);
        var t = kind == null ? null : Schema.Table(kind.Table);
        if (t == null || kind == null || !t.Has(kind.KeyColumn)) return res;
        int ki = t.IndexOf(kind.KeyColumn), li = t.IndexOf(kind.LabelColumn);
        foreach (var chunk in list.Chunk(500))
        {
            var rows = Rows(t, RowFilter.Where(new FilterTerm(kind.KeyColumn, FilterOp.In, null, chunk.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList())));
            foreach (var g in rows.GroupBy(r => WorldValue.Long(r[ki])))
            {
                if (kind.IsList)
                {
                    res[g.Key] = $"{g.Count()} row{(g.Count() == 1 ? "" : "s")}";
                    continue;
                }
                var row = Service.Effective(t, g.ToList()) ?? g.First();
                string label = li >= 0 ? row[li] ?? "" : "";
                // a label that is itself a link (faction_template -> faction, npc_text -> broadcast_text): its name
                var lc = Service.Info(t.Name)?.Column(kind.LabelColumn);
                if (lc?.Link != null && depth < 3 && lc.Link.Resolve(WorldValue.Long(label)) is { } sub)
                    label = Labels(sub.Target, new[] { sub.Id }, depth + 1).GetValueOrDefault(sub.Id) ?? $"{KindText(sub.Target)} {sub.Id}";
                else if (label.Length == 0) label = $"#{g.Key}";
                res[g.Key] = Short(label);
            }
        }
        if (gen == Volatile.Read(ref _existsGen))
            foreach (var id in list.Where(i => res.ContainsKey(i))) _exists[(target, id)] = true;
        return res;
    }

    public static string Short(string s, int max = 70)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("$B", " ");
        return s.Length > max ? s[..(max - 3)] + "..." : s;
    }

    /// <summary>npc_flags of a creature as you see it (null: no such creature).</summary>
    public long? CreatureFlags(long entry)
    {
        var t = Schema.Table("creature_template");
        if (t == null) return null;
        var row = Object(t, entry);
        return row == null ? null : WorldValue.Long(row[t.IndexOf("npc_flags")]);
    }

    /// <summary>Has a creature display a row in creature_display_info_addon?</summary>
    public bool HasDisplayAddon(long display)
    {
        var t = Schema.Table("creature_display_info_addon");
        return t == null || Rows(t, RowFilter.Eq("display_id", display), 1).Count > 0;
    }

    // ------------------------------------------------------------------ editing

    private static bool SameRow(WorldTable t, string?[]? a, string?[]? b)
    {
        if (a == null || b == null) return a == b;
        for (int i = 0; i < t.Columns.Count; i++)
            if (!WorldValue.Same(t.Columns[i], a[i], b[i])) return false;
        return true;
    }

    /// <summary>Apply new rows as one undo step: (table, key, the row as it should be or null to delete it).</summary>
    private int Commit(string label, IEnumerable<(WorldTable Table, string Key, string?[]? After)> rows)
    {
        lock (_gate) return CommitCore(label, rows);
    }

    private int CommitCore(string label, IEnumerable<(WorldTable Table, string Key, string?[]? After)> rows)
    {
        var items = new List<StepItem>();
        foreach (var (t, key, after) in rows)
        {
            if (!_changes.TryGetValue(t.Name, out var ch)) _changes[t.Name] = ch = new ServerTableChanges(t);
            ch.Rows.TryGetValue(key, out var old);
            var before = old != null ? old.Before : Service.Db.Row(t, key);
            ServerRowChange? now = SameRow(t, before, after) ? null : new ServerRowChange(key, before, after);
            if (old == null && now == null) continue;
            if (old != null && now != null && SameRow(t, old.After, now.After)) continue;
            Put(t.Name, key, now);
            items.Add(new StepItem(t.Name, key, old, now));
        }
        if (items.Count == 0) return 0;
        _undo.Add(new Step(label, items));
        _redo.Clear();
        ClearExists();
        return items.Count;
    }

    /// <summary>Set many rows as one undo step (imports, "make them match"): (table, key, the row as it should be or null
    /// to delete it). The rows must have been checked already. Returns how many rows changed.</summary>
    public int ApplyRows(string label, IEnumerable<(WorldTable Table, string Key, string?[]? After)> rows) => Commit(label, rows);

    private void Put(string table, string key, ServerRowChange? change)
    {
        if (!_changes.TryGetValue(table, out var ch)) _changes[table] = ch = new ServerTableChanges(Table(table));
        if (change == null) ch.Rows.Remove(key);
        else ch.Rows[key] = change;
    }

    public bool Undo()
    {
        lock (_gate) return UndoCore();
    }

    private bool UndoCore()
    {
        if (_undo.Count == 0) return false;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        foreach (var i in Enumerable.Reverse(s.Items)) Put(i.Table, i.Key, i.Old);
        _redo.Add(s);
        ClearExists();
        return true;
    }

    public bool Redo()
    {
        lock (_gate) return RedoCore();
    }

    private bool RedoCore()
    {
        if (_redo.Count == 0) return false;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var i in s.Items) Put(i.Table, i.Key, i.New);
        _undo.Add(s);
        ClearExists();
        return true;
    }

    /// <summary>Throw away the unsaved edits (back to what is saved in the workspace).</summary>
    public void Discard()
    {
        var saved = Service.SavedChanges();
        lock (_gate)
        {
            LoadSavedCore(saved);
            _undo.Clear();
            _redo.Clear();
        }
    }

    /// <summary>Change values of one row (checked first; nothing changes when a value is refused). Changing a key column
    /// moves the row to the new key (the old key is deleted).</summary>
    public ServerEditResult Set(string table, string key, IEnumerable<(string Column, string? Text)> values, string? label = null)
    {
        var t = Table(table);
        var cur = Current(t, key) ?? throw new ServerDataException($"{t.Name} has no row {key}.");
        var row = (string?[])cur.Clone();
        var problems = new List<ServerProblem>();
        var names = new List<string>();
        foreach (var (column, text) in values)
        {
            var c = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column \"{column}\".");
            var p = ServerValidation.CheckValue(this, t, c.Name, text, out var canon);
            var err = p.FirstOrDefault(x => x.IsError);
            if (err != null) return ServerEditResult.Refused($"{c.Name}: {err.Message}", p);
            problems.AddRange(p);
            row[t.IndexOf(c.Name)] = canon;
            names.Add(c.Name);
        }
        var newKey = t.KeyText(row);
        if (newKey != key && Current(t, newKey) != null) return ServerEditResult.Refused($"{t.Name} already has a row {newKey}: each row needs its own key.");
        foreach (var p in ServerValidation.CheckRow(this, t, row).Where(p => !p.IsError || names.Contains(p.Column, StringComparer.OrdinalIgnoreCase)))
            if (!problems.Contains(p)) problems.Add(p.IsError ? p with { IsError = false } : p);
        var changedNames = names.Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !WorldValue.Same(t.Column(c)!, cur[t.IndexOf(c)], row[t.IndexOf(c)])).ToList();
        var lbl = label ?? (changedNames.Count == 1 ? $"{changedNames[0]} of {t.Name} {key}" : $"{changedNames.Count} values of {t.Name} {key}");
        int n = newKey == key ? Commit(lbl, new[] { (t, key, (string?[]?)row) }) : Commit(lbl, new[] { (t, key, (string?[]?)null), (t, newKey, row) });
        return new ServerEditResult(true, n == 0 ? "Nothing changed." : changedNames.Count == 1 ? $"{changedNames[0]} changed." : $"{changedNames.Count} values changed ({string.Join(", ", changedNames.Take(6))}).", problems, n);
    }

    public ServerEditResult Set(string table, string key, string column, string? text) => Set(table, key, new[] { (column, text) });

    /// <summary>A new row: every column its default (or 0 / empty), the key given or the next free ID of your custom ID
    /// range, then the given values (checked).</summary>
    public (string? Key, ServerEditResult Result) Add(string table, IEnumerable<(string Column, string? Text)>? values = null, long? id = null, string? label = null)
    {
        var t = Table(table);
        var row = new string?[t.Columns.Count];
        for (int i = 0; i < row.Length; i++)
        {
            var c = t.Columns[i];
            row[i] = c.Default ?? (c.Nullable ? null : c.IsNumber ? "0" : "");
            if (row[i] != null && WorldValue.Normalize(c, row[i], out var canon) == null) row[i] = canon;
        }
        var idCol = Service.IdColumn(t);
        if (id != null || t.Column(idCol)?.IsNumber == true) row[t.IndexOf(idCol)] = (id ?? SuggestId(t)).ToString(CultureInfo.InvariantCulture);
        if (t.Has("build") && Service.Info(t.Name)?.Versioning is ServerVersioning.Build or ServerVersioning.BuildExact) row[t.IndexOf("build")] = ServerDataService.ClientBuild.ToString(CultureInfo.InvariantCulture);
        return Insert(t, row, values, label ?? $"Add a row to {t.Name}");
    }

    /// <summary>"Duplicate as new": a copy of a row with the next free ID of your custom range (or the given one), then the
    /// given values.</summary>
    public (string? Key, ServerEditResult Result) Duplicate(string table, string key, IEnumerable<(string Column, string? Text)>? values = null, long? id = null)
    {
        var t = Table(table);
        var src = Current(t, key) ?? throw new ServerDataException($"{t.Name} has no row {key}.");
        var row = (string?[])src.Clone();
        var idCol = Service.IdColumn(t);
        var given = values?.ToList() ?? new();
        bool keyGiven = given.Any(v => t.Key.Contains(v.Column, StringComparer.OrdinalIgnoreCase));
        if (id != null || !keyGiven && t.Column(idCol)?.IsNumber == true) row[t.IndexOf(idCol)] = (id ?? SuggestId(t)).ToString(CultureInfo.InvariantCulture);
        var (k, r) = Insert(t, row, given, $"Duplicate {t.Name} {key}");
        return (k, r.Ok ? r with { Message = $"Duplicated {key} as {k}. " + r.Message } : r);
    }

    private (string? Key, ServerEditResult Result) Insert(WorldTable t, string?[] row, IEnumerable<(string Column, string? Text)>? values, string label)
    {
        var problems = new List<ServerProblem>();
        foreach (var (column, text) in values ?? Array.Empty<(string, string?)>())
        {
            var c = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column \"{column}\".");
            var p = ServerValidation.CheckValue(this, t, c.Name, text, out var canon);
            var err = p.FirstOrDefault(x => x.IsError);
            if (err != null) return (null, ServerEditResult.Refused($"{c.Name}: {err.Message}", p));
            problems.AddRange(p);
            row[t.IndexOf(c.Name)] = canon;
        }
        var key = t.KeyText(row);
        if (Current(t, key) != null) return (null, ServerEditResult.Refused($"{t.Name} already has a row {key}: each row needs its own key (a free ID: {SuggestId(t)})."));
        foreach (var p in ServerValidation.CheckRow(this, t, row)) if (!problems.Contains(p)) problems.Add(p);
        Commit(label, new[] { (t, key, (string?[]?)row) });
        return (key, new ServerEditResult(true, $"Added {t.Name} {key}.", problems, 1));
    }

    /// <summary>Before "Delete": what refers to the rows (in words).</summary>
    public List<string> UsedBy(string table, IEnumerable<string> keys, int max = 20)
    {
        var t = Table(table);
        var res = new List<string>();
        var kind = Service.KindOf(t.Name);
        if (kind == null) return res;
        var browser = new ServerBrowser(this);
        foreach (var key in keys.Take(50))
        {
            var row = Current(t, key);
            if (row == null) continue;
            long id = WorldValue.Long(row[t.IndexOf(kind.KeyColumn)]);
            // another version of the same object still exists: nothing loses its target
            if (Versions(t, id).Count(r => t.KeyText(r) != key) > 0) continue;
            foreach (var u in browser.UsedBy(kind.Name, id, max))
                res.Add($"{key} is used by {u.Text}");
            if (res.Count >= max) break;
        }
        return res;
    }

    public ServerEditResult Delete(string table, IEnumerable<string> keys)
    {
        var t = Table(table);
        var list = keys.Distinct().ToList();
        foreach (var k in list)
            if (Current(t, k) == null) return ServerEditResult.Refused($"{t.Name} has no row {k}.");
        int n = Commit(list.Count == 1 ? $"Delete {t.Name} {list[0]}" : $"Delete {list.Count} rows of {t.Name}", list.Select(k => (t, k, (string?[]?)null)));
        return new ServerEditResult(true, n == 1 ? "Row deleted." : $"{n} rows deleted.", Array.Empty<ServerProblem>(), n);
    }

    /// <summary>Set, add to or multiply one column of many rows: every new value is checked first; if one is refused,
    /// nothing changes. One undo step.</summary>
    public ServerEditResult Bulk(string table, IEnumerable<string> keys, string column, BulkOperation op, string value)
    {
        var t = Table(table);
        var c = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column \"{column}\".");
        var list = keys.Distinct().ToList();
        if (list.Count == 0) return ServerEditResult.Refused("Choose the rows to change first.");
        if (op != BulkOperation.Set && !c.IsNumber) return ServerEditResult.Refused("Add and multiply work on number columns only.");
        if (t.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase) && op == BulkOperation.Set && list.Count > 1) return ServerEditResult.Refused("Several rows cannot get the same key.");
        double factor = 0;
        if (op != BulkOperation.Set && !double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out factor)) return ServerEditResult.Refused($"\"{value}\" is not a number.");
        var rows = new List<(WorldTable, string, string?[]?)>();
        var errors = new List<string>();
        var warnings = new List<ServerProblem>();
        foreach (var k in list)
        {
            var cur = Current(t, k) ?? throw new ServerDataException($"{t.Name} has no row {k}.");
            string? text = value;
            if (op != BulkOperation.Set)
            {
                double v = WorldValue.Number(cur[t.IndexOf(c.Name)]);
                double nv = op == BulkOperation.Add ? v + factor : v * factor;
                text = c.Type == WorldType.Integer ? Math.Round(nv, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                    : c.Type == WorldType.Float ? ((float)nv).ToString("R", CultureInfo.InvariantCulture) : nv.ToString("R", CultureInfo.InvariantCulture);
            }
            var p = ServerValidation.CheckValue(this, t, c.Name, text, out var canon);
            var err = p.FirstOrDefault(x => x.IsError);
            if (err != null)
            {
                errors.Add($"{k}: {err.Message}");
                continue;
            }
            warnings.AddRange(p);
            var row = (string?[])cur.Clone();
            row[t.IndexOf(c.Name)] = canon;
            rows.Add((t, k, row));
        }
        if (errors.Count > 0) return ServerEditResult.Refused($"Nothing was changed: {errors.Count} row(s) would get a value that is not allowed.\n" + string.Join("\n", errors.Take(8)));
        int n = Commit($"{op} {c.Name} of {list.Count} rows of {t.Name}", rows);
        return new ServerEditResult(true, $"{n} of {list.Count} rows changed.", warnings.Distinct().ToList(), n);
    }

    /// <summary>A free ID for a new row: the first unused number of your custom ID range (Settings: server.custom-ids; for
    /// spell_template the spell ID range, spell-ids), or the next number after the highest when the column cannot hold that range.</summary>
    public long SuggestId(WorldTable t, string? column = null)
    {
        var col = t.Column(column ?? Service.IdColumn(t)) ?? throw new ServerDataException($"{t.Name} has no ID column.");
        // 0.13.3: a new spell gets an ID of the spell range (VMaNGOS casts no spell above 40000: SpellIdLimits)
        var (start, end) = t.Name.Equals("spell_template", StringComparison.OrdinalIgnoreCase) && col.Name.Equals("entry", StringComparison.OrdinalIgnoreCase) ? Service.SpellIds : Service.CustomIds;
        var (_, max) = col.IntegerRange;
        var used = new HashSet<long>();
        bool inRange = start <= end && max >= end;
        if (inRange)
        {
            foreach (var r in Service.Db.Query($"SELECT DISTINCT {WorldValue.Name(col.Name)} FROM {WorldValue.Name(t.Name)} WHERE {WorldValue.Name(col.Name)} BETWEEN @a AND @b", ("@a", start), ("@b", end)))
                used.Add(Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
            if (_changes.TryGetValue(t.Name, out var ch))
                foreach (var c in ch.Rows.Values.Where(c => c.After != null)) used.Add(WorldValue.Long(c.After![t.IndexOf(col.Name)]));
            for (long v = start; v <= end; v++)
                if (!used.Contains(v)) return v;
        }
        long top = Convert.ToInt64(Service.Db.Scalar($"SELECT IFNULL(MAX({WorldValue.Name(col.Name)}),0) FROM {WorldValue.Name(t.Name)}") ?? 0L, CultureInfo.InvariantCulture);
        if (_changes.TryGetValue(t.Name, out var ch2))
            foreach (var c in ch2.Rows.Values.Where(c => c.After != null)) top = Math.Max(top, WorldValue.Long(c.After![t.IndexOf(col.Name)]));
        return top + 1;
    }

    // ------------------------------------------------------------------ saving into the workspace

    /// <summary>The new problems (errors) of the changed rows: those their row did not have on the server before.</summary>
    public List<string> BlockingProblems()
    {
        var res = new List<string>();
        foreach (var ch in Changes)
            foreach (var r in ch.Rows.Values.Where(r => r.After != null))
            {
                var now = ServerValidation.CheckRow(this, ch.Table, r.After!).Where(p => p.IsError).ToList();
                var was = r.Before == null ? new List<ServerProblem>() : ServerValidation.CheckRow(this, ch.Table, r.Before).Where(p => p.IsError).ToList();
                foreach (var p in now.Where(p => !was.Contains(p))) res.Add($"{ch.Table.Name} {r.Key}: {p.Column}: {p.Message}");
            }
        return res;
    }

    /// <summary>Save the edits into the workspace: one file per changed table, all in one change set (My changes). Tables
    /// whose edits are all undone lose their file. Refused while a changed row has a new error.</summary>
    public (List<ChangeRecord> Records, string Message) Save(string? label = null, ChangeSet? changeSet = null)
    {
        var ws = Service.Workspace ?? throw new ServerDataException("No game folder is open (the workspace keeps your edits).");
        lock (_gate) return SaveCore(ws, label, changeSet);
    }

    /// <summary>0.12.0 (the designers): the IDs of a column already used in a range, on the server and in your edits.</summary>
    public HashSet<long> UsedIds(WorldTable t, string column, long start, long end)
    {
        var col = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column {column}.");
        var used = new HashSet<long>();
        foreach (var r in Service.Db.Query($"SELECT DISTINCT {WorldValue.Name(col.Name)} FROM {WorldValue.Name(t.Name)} WHERE {WorldValue.Name(col.Name)} BETWEEN @a AND @b", ("@a", start), ("@b", end)))
            used.Add(Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
        lock (_gate)
            if (_changes.TryGetValue(t.Name, out var ch))
                foreach (var c in ch.Rows.Values.Where(c => c.After != null)) used.Add(WorldValue.Long(c.After![t.IndexOf(col.Name)]));
        return used;
    }

    /// <summary>0.12.0: the highest value of a column on the server and in your edits (0 when empty).</summary>
    public long MaxId(WorldTable t, string column)
    {
        var col = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column {column}.");
        long top = Convert.ToInt64(Service.Db.Scalar($"SELECT IFNULL(MAX({WorldValue.Name(col.Name)}),0) FROM {WorldValue.Name(t.Name)}") ?? 0L, CultureInfo.InvariantCulture);
        lock (_gate)
            if (_changes.TryGetValue(t.Name, out var ch))
                foreach (var c in ch.Rows.Values.Where(c => c.After != null)) top = Math.Max(top, WorldValue.Long(c.After![t.IndexOf(col.Name)]));
        return top;
    }

    private (List<ChangeRecord> Records, string Message) SaveCore(Workspace.Workspace ws, string? label, ChangeSet? changeSet = null)
    {
        var dirty = DirtyTablesCore();
        if (dirty.Count == 0) return (new List<ChangeRecord>(), "Nothing to save: your server edits are already in your workspace.");
        var blocking = BlockingProblems();
        if (blocking.Count > 0)
            throw new ServerDataException($"Not saved: {blocking.Count} changed row(s) have a problem the server would refuse:\n" + string.Join("\n", blocking.Take(10)));
        var parts = dirty.Select(t => _changes.GetValueOrDefault(t)).Where(c => c != null && c.Rows.Count > 0).Select(c => c!.Summary).ToList();
        var set = parts.Count == 0 ? null : changeSet ?? ws.BeginChangeSet(label ?? "Server data: " + string.Join("; ", parts.Take(3)) + (parts.Count > 3 ? $"; and {parts.Count - 3} more" : ""));
        var recs = new List<ChangeRecord>();
        var removed = 0;
        foreach (var name in dirty)
        {
            var path = ServerChangeFiles.PathOf(Service.Database, Schema.Require(name).Name);
            var ch = _changes.GetValueOrDefault(name);
            if (ch == null || ch.Rows.Count == 0)
            {
                ws.Revert(path);
                _saved.Remove(name);
                removed++;
                continue;
            }
            var data = ServerChangeFiles.Write(Service.Database, ch);
            var origin = new OriginInfo
            {
                Archive = $"server database {Service.Database}",
                Format = "server table",
                IsNew = false,
                UsedBy = ch.Rows.Values.Take(5).Select(r => $"{r.KindText} {r.Key}").ToList(),
            };
            recs.Add(ws.SaveEdit(path, data, origin, set!, null, ch.Summary));
            _saved[name] = data;
        }
        var msg = recs.Count > 0
            ? $"Saved into your workspace: {string.Join("; ", recs.Select(r => r.Summary))}. The server is not changed yet: \"Apply to server\" (or an SQL patch) does that."
            : "Your server edits were all undone: they were removed from your workspace.";
        if (removed > 0 && recs.Count > 0) msg += $" {removed} table(s) without edits were removed from your workspace.";
        return (recs, msg);
    }
}
