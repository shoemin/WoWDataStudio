using System.Globalization;
using System.Text;

namespace WoWData.Core.Dbc;

/// <summary>Bulk edit: set a column to a value, add to it or multiply it.</summary>
public enum BulkOperation { Set, Add, Multiply }

/// <summary>The result of an edit: refused (with the reason), or done (with a warning or a note).</summary>
public sealed record DbcEditResult(bool Ok, string Message, int Changed = 0);

/// <summary>
/// 0.8.0 (moved from the Data tables tab's view model into Core): one open table with its undo history, shared by the
/// desktop app and <c>wds dbc ...</c>: the cell texts (choices with their names, links with the linked row's label),
/// checked edits, bulk edits, new, duplicated and deleted rows (with the rows that link to them), search and column
/// filters, the rows that differ from the game's file, saving into the workspace, and CSV / JSON export and import plans.
/// </summary>
public sealed class DbcEditSession
{
    public DbcEditSession(DbcService service, string table, int locale = 0)
    {
        Service = service;
        Table = service.LoadCurrent(table);
        try { GameTable = service.LoadGame(table); }
        catch (DbcException) { GameTable = null; }
        Editor = new DbcEditor(Table);
        Locale = Math.Clamp(locale, 0, DbcFile.Locales - 1);
    }

    public DbcService Service { get; }
    public DbcTable Table { get; }
    /// <summary>The game's own file (null when the game has none).</summary>
    public DbcTable? GameTable { get; }
    public DbcEditor Editor { get; }
    public int Locale { get; set; }
    public string Name => Table.Name;

    /// <summary>0.12.0: the other tables open with this one (the designers edit several at once): links are checked
    /// against their unsaved rows too. Null: the saved versions.</summary>
    public DbcTableLookup? Others { get; set; }

    /// <summary>A table by name: this one (with its edits), another open one, or the current version of another.</summary>
    public DbcTable? Lookup(string name) => string.Equals(name, Table.Name, StringComparison.OrdinalIgnoreCase) ? Table : Others?.Invoke(name) ?? Service.Lookup(name);

    /// <summary>A column by name (case-insensitive) or by its position ("3"), or null.</summary>
    public DbcColumn? Column(string name)
    {
        var c = Table.Layout.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (c != null) return c;
        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < Table.Layout.Columns.Count ? Table.Layout.Columns[i] : null;
    }

    /// <summary>The row with this ID (or, for a table without an ID column, the 1-based row number), or null.</summary>
    public DbcRow? Row(long id) => Table.Layout.HasKey ? Table.Find(id) : id >= 1 && id <= Table.Rows.Count ? Table.Rows[(int)id - 1] : null;

    /// <summary>"ID 133" or "row 5".</summary>
    public string RowName(DbcRow r) => Table.Key(r) is { } k ? $"ID {k}" : $"row {Table.Rows.IndexOf(r) + 1}";

    // ------------------------------------------------------------------ cells

    /// <summary>0 and -1 mean "no link", unless the linked table has such a row (Map 0 is Eastern Kingdoms).</summary>
    public static bool IsNoLink(DbcTable? target, long v, uint raw) =>
        (v == 0 || v == -1 || raw == uint.MaxValue) && (target == null || target.Find(v) == null);

    /// <summary>The text of a cell: shown (with the name of a choice or the label of a link) or for editing (the raw value).</summary>
    public string CellText(DbcRow r, DbcColumn c, bool edit)
    {
        var t = Table;
        var s = t.Format(r, c, Locale);
        if (edit || c.IsString) return s;
        if (c.Semantic == DbcSemantic.Flags) return "0x" + t.Raw(r, c.Field).ToString("X", CultureInfo.InvariantCulture);
        if (c.Values != null && c.Semantic is DbcSemantic.Enum or DbcSemantic.Bool)
        {
            long v = t.Number(r, c);
            var n = c.Values.NameOf(v);
            return n == null ? s : $"{s} · {n}";
        }
        if (c.ForeignKey != null)
        {
            long v = t.Number(r, c);
            var target = Lookup(c.ForeignKey);
            if (IsNoLink(target, v, t.Raw(r, c.Field))) return s;
            var label = Service.LinkLabel(c.ForeignKey, v, Lookup, Locale);
            // a table without a name column labels its rows "#ID": the number alone says it
            return label == null ? $"{s} · (missing)" : label.StartsWith('#') ? s : $"{s} · {label}";
        }
        return s;
    }

    /// <summary>Change one value (checked first; a refused value is not stored). One undo step.</summary>
    public DbcEditResult Edit(DbcRow r, DbcColumn c, string text)
    {
        var t = Table;
        if (text == t.Format(r, c, Locale)) return new DbcEditResult(true, $"{c.Name} is already {text}.", 0);
        var problems = DbcValidation.Check(t, r, c, text, Lookup, out var cell);
        var errors = problems.Where(p => p.IsError).ToList();
        if (errors.Count > 0) return new DbcEditResult(false, errors[0].Message);
        int field = c.Storage == DbcStorage.Loc ? c.Field + Locale : c.Field;
        Editor.SetCell(r, field, cell, $"{c.Name} of {t.Label(r, Locale)}");
        return new DbcEditResult(true, problems.Count > 0 ? problems[0].Message : $"{c.Name} changed. Undo with Ctrl+Z; \"Save to my workspace\" keeps it.", 1);
    }

    /// <summary>Bulk edit of rows: every new value is checked first; if one is not allowed, nothing is changed. One undo step.</summary>
    public DbcEditResult BulkEdit(DbcColumn c, BulkOperation op, string value, IReadOnlyList<DbcRow> rows)
    {
        var t = Table;
        if (rows.Count == 0) return new DbcEditResult(false, "Select the rows to change first.");
        if (op != BulkOperation.Set && !c.IsNumber) return new DbcEditResult(false, "Add and multiply work on number columns only.");
        double factor = 0;
        if (op != BulkOperation.Set && !double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out factor))
            return new DbcEditResult(false, $"\"{value}\" is not a number.");
        var cells = new List<(DbcRow, int, DbcCell)>();
        var errors = new List<string>();
        int field = c.Storage == DbcStorage.Loc ? c.Field + Locale : c.Field;
        foreach (var r in rows)
        {
            string text = value;
            if (op != BulkOperation.Set)
            {
                double cur = c.IsFloat ? t.Float(r, c) : t.Number(r, c);
                double nv = op == BulkOperation.Add ? cur + factor : cur * factor;
                text = c.IsFloat ? ((float)nv).ToString("R", CultureInfo.InvariantCulture) : Math.Round(nv, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            }
            if (c.IsKey && op == BulkOperation.Set && rows.Count > 1) return new DbcEditResult(false, "Several rows cannot get the same ID.");
            var probs = DbcValidation.Check(t, r, c, text, Lookup, out var cell);
            var err = probs.FirstOrDefault(p => p.IsError);
            if (err != null)
            {
                errors.Add($"{t.Label(r, Locale)}: {err.Message}");
                continue;
            }
            cells.Add((r, field, cell));
        }
        if (errors.Count > 0) return new DbcEditResult(false, $"Nothing was changed: {errors.Count} row(s) would get a value that is not allowed.\n" + string.Join("\n", errors.Take(8)));
        int n = Editor.SetCells(cells, $"{op} {c.Name} of {rows.Count} rows");
        return new DbcEditResult(true, $"{n} of {rows.Count} rows changed.", n);
    }

    // ------------------------------------------------------------------ rows

    /// <summary>"Add row": a new row with the next free ID (or <paramref name="id"/>), every value 0 and every text empty.</summary>
    public (DbcRow Row, string Message) AddRow(long? id = null)
    {
        var t = Table;
        if (id != null && t.Layout.HasKey && t.Find(id.Value) != null) throw new DbcException($"ID {id} is already used by {t.Label(t.Find(id.Value)!, Locale)}. Each row needs its own ID (the next free one is {t.SuggestId()}).");
        long newId = id ?? t.SuggestId();
        var row = t.NewRow(t.Layout.HasKey ? newId : null);
        Editor.AddRow(row, t.Layout.HasKey ? $"Add row {newId}" : "Add a row");
        return (row, t.Layout.HasKey ? $"Added a new row with ID {newId}" + (id == null ? " (the next free ID)." : ".") : "Added a new row at the end.");
    }

    /// <summary>"Duplicate as new": a copy of a row with the next free ID (or <paramref name="id"/>).</summary>
    public (DbcRow Row, string Message) Duplicate(DbcRow source, long? id = null)
    {
        var t = Table;
        if (id != null && t.Layout.HasKey && t.Find(id.Value) != null) throw new DbcException($"ID {id} is already used by {t.Label(t.Find(id.Value)!, Locale)}. Each row needs its own ID (the next free one is {t.SuggestId()}).");
        long newId = id ?? t.SuggestId();
        var copy = Editor.Duplicate(source, newId);
        return (copy, t.Layout.HasKey ? $"Duplicated {t.Label(source, Locale)} as ID {newId}." : "Duplicated the row.");
    }

    /// <summary>Before "Delete rows": the rows of other tables (and of this one) that link to them, in words.</summary>
    public List<string> LinksTo(IReadOnlyList<DbcRow> rows, int maxRows = 50)
    {
        var t = Table;
        var used = new List<string>();
        if (!t.Layout.HasKey) return used;
        foreach (var r in rows.Take(maxRows))
        {
            var id = t.Key(r)!.Value;
            var list = Service.UsedBy(t.Name, id, Lookup, 5);
            list.RemoveAll(u => u.Table == t.Name && rows.Any(x => t.Key(x) == u.Id));
            if (list.Count > 0) used.Add($"ID {id} ({t.Label(r, Locale)}) is used by {string.Join(", ", list.Take(3).Select(u => u.Text))}{(list.Count > 3 ? ", ..." : "")}");
        }
        return used;
    }

    /// <summary>"Delete rows". One undo step.</summary>
    public string DeleteRows(IReadOnlyList<DbcRow> rows)
    {
        var t = Table;
        Editor.DeleteRows(rows, rows.Count == 1 ? $"Delete {t.Label(rows[0], Locale)}" : $"Delete {rows.Count} rows");
        return rows.Count == 1 ? "Row deleted." : $"{rows.Count} rows deleted.";
    }

    // ------------------------------------------------------------------ find rows

    /// <summary>The words "Search all text..." looks in: every shown value of the row (and the enUS text of a localized one).</summary>
    public static string RowSearchText(DbcTable t, DbcRow r, int locale)
    {
        var sb = new StringBuilder();
        foreach (var c in t.Layout.Columns)
        {
            sb.Append(t.Format(r, c, locale)).Append(' ');
            if (c.Storage == DbcStorage.Loc && locale != 0) sb.Append(t.Text(r, c, 0)).Append(' ');
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>The rows shown for a search, column filters and a sort (the grid of the Data tables tab), as indexes into
    /// <see cref="DbcTable.Rows"/>. <paramref name="rowText"/>: the cached search texts (built when null and needed).</summary>
    public static List<int> Query(DbcTable t, int locale, string search, IEnumerable<(DbcColumn Column, DbcFilter Filter)> filters, DbcColumn? sort, bool ascending, string[]? rowText = null)
    {
        var rows = t.Rows;
        var words = search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && (rowText == null || rowText.Length != rows.Count)) rowText = rows.Select(r => RowSearchText(t, r, locale)).ToArray();
        var fl = filters.ToList();
        var idx = new List<int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (words.Length > 0)
            {
                bool ok = true;
                foreach (var w in words)
                    if (!rowText![i].Contains(w, StringComparison.Ordinal)) { ok = false; break; }
                if (!ok) continue;
            }
            bool pass = true;
            foreach (var (col, f) in fl)
                if (!f.Matches(t, rows[i], col, locale)) { pass = false; break; }
            if (pass) idx.Add(i);
        }
        if (sort is { } c)
        {
            Comparison<int> cmp = c.IsString
                ? (a, b) => string.Compare(t.Text(rows[a], c, locale), t.Text(rows[b], c, locale), StringComparison.OrdinalIgnoreCase)
                : c.IsFloat ? (a, b) => t.Float(rows[a], c).CompareTo(t.Float(rows[b], c))
                : (a, b) => t.Number(rows[a], c).CompareTo(t.Number(rows[b], c));
            idx.Sort((a, b) =>
            {
                int x = cmp(a, b);
                return x != 0 ? (ascending ? x : -x) : a.CompareTo(b);
            });
        }
        return idx;
    }

    /// <summary>The rows that differ from the game's file (marked in the grid).</summary>
    public HashSet<DbcRow> ChangedRows()
    {
        var res = new HashSet<DbcRow>();
        var t = Table;
        var g = GameTable;
        if (g == null || t.Layout.KeyColumn == null || g.FieldCount != t.FieldCount) return res;
        foreach (var r in t.Rows.ToArray())
        {
            var o = g.Find(t.Key(r)!.Value);
            if (o == null) { res.Add(r); continue; }
            for (int f = 0; f < t.FieldCount; f++)
            {
                bool diff = t.IsStringField(f) ? !t.StringBytes(r, f).AsSpan().SequenceEqual(g.StringBytes(o, f)) : t.Raw(r, f) != g.Raw(o, f);
                if (diff) { res.Add(r); break; }
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ save, compare, export, import

    /// <summary>The table compared with the game's file (empty when the game has none).</summary>
    public DbcDiffResult CompareWithGame()
    {
        try { return GameTable != null ? DbcDiff.Compare(GameTable, Table, Locale) : new DbcDiffResult(); }
        catch (DbcException) { return new DbcDiffResult(); }
    }

    /// <summary>What saving means for the server, in words ("" for a client-only table).</summary>
    public string ServerNote => Table.Layout.ServerUse == DbcServerUse.ServerFile ? "The server reads this table too: after saving, use \"Copy to server...\"." :
        Table.Layout.ServerUse == DbcServerUse.ServerDatabase ? $"The server reads this data from its database ({Table.Layout.ServerTable}), not from this file." : "";

    /// <summary>"Save to my workspace": the table becomes one change set in My changes (a table identical to the game's
    /// removes the edit instead: null). Returns the record and the message the app shows.</summary>
    public (WoWData.Core.Workspace.ChangeRecord? Record, string Message) Save(DbcDiffResult? diff = null, WoWData.Core.Workspace.ChangeSet? changeSet = null)
    {
        diff ??= CompareWithGame();
        var rec = Service.Save(Table, diff.Summary, changeSet);
        Editor.MarkSaved();
        var t = Table;
        return (rec, rec == null ? $"{t.Name}.dbc is the same as the game's file again: your edit was removed from your workspace."
            : $"Saved {t.Name}.dbc to your workspace ({diff.Summary}) Next: \"Test in game\" or \"Build release patch\"" +
              (t.Layout.ServerUse == DbcServerUse.ServerFile ? ", and \"Copy to server...\"." : "."));
    }

    /// <summary>Export rows as CSV (for spreadsheets) or JSON (by the file's extension).</summary>
    public void ExportTo(string file, IReadOnlyList<DbcRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(file, DbcExchange.ToJson(Table, rows), new UTF8Encoding(false));
        else DbcExchange.WriteCsv(Table, file, rows);
    }

    /// <summary>What importing a CSV or JSON file would do (nothing is changed).</summary>
    public DbcImportPlan PlanImport(string file, DbcMissingRows missing)
    {
        var text = File.ReadAllText(file);
        return file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? DbcExchange.PlanJson(Table, text, missing, Lookup) : DbcExchange.PlanCsv(Table, text, missing, Lookup);
    }

    /// <summary>Apply an import plan (one undo step).</summary>
    public string ApplyImport(DbcImportPlan plan, string file)
    {
        Editor.Apply(plan, $"Import {Path.GetFileName(file)}");
        return $"Imported {Path.GetFileName(file)}: {plan.Preview.Summary} Save to my workspace to keep it.";
    }
}

/// <summary>A column filter: text (contains, =exact, !not) or numbers (5, &gt;5, &lt;=10, 5-10, !0). 0.8.0: moved to Core
/// (the Data tables tab and <c>wds dbc rows --where</c>).</summary>
public sealed class DbcFilter
{
    private enum Op { Contains, Equals, NotContains, Eq, Ne, Gt, Ge, Lt, Le, Range }

    private Op _op;
    private string _text = "";
    private double _a, _b;

    public static DbcFilter Parse(string s)
    {
        var f = new DbcFilter();
        s = s.Trim();
        double Num(string x) => double.TryParse(x.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : x.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(x.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : double.NaN;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(>=|<=|>|<|!=|!|=)?\s*(.*)$");
        string op = m.Groups[1].Value, rest = m.Groups[2].Value;
        var range = System.Text.RegularExpressions.Regex.Match(s, @"^(-?[\d.]+)\s*(?:-|\.\.)\s*(-?[\d.]+)$");
        if (range.Success && !double.IsNaN(Num(range.Groups[1].Value)) && !double.IsNaN(Num(range.Groups[2].Value)))
        {
            f._op = Op.Range;
            f._a = Num(range.Groups[1].Value);
            f._b = Num(range.Groups[2].Value);
            return f;
        }
        double n = Num(rest);
        f._text = rest.ToLowerInvariant();
        f._a = n;
        f._op = op switch
        {
            ">" => Op.Gt, ">=" => Op.Ge, "<" => Op.Lt, "<=" => Op.Le,
            "!" or "!=" => double.IsNaN(n) ? Op.NotContains : Op.Ne,
            "=" => double.IsNaN(n) ? Op.Equals : Op.Eq,
            _ => double.IsNaN(n) ? Op.Contains : Op.Eq,
        };
        return f;
    }

    public bool Matches(DbcTable t, DbcRow r, DbcColumn c, int locale)
    {
        if (c.IsString)
        {
            var s = t.Text(r, c, locale).ToLowerInvariant();
            return _op switch
            {
                Op.Equals or Op.Eq => s == _text,
                Op.NotContains or Op.Ne => !s.Contains(_text, StringComparison.Ordinal),
                _ => s.Contains(_text, StringComparison.Ordinal),
            };
        }
        double v = c.IsFloat ? t.Float(r, c) : t.Number(r, c);
        switch (_op)
        {
            case Op.Eq: return Math.Abs(v - _a) < 1e-6 || (c.Semantic == DbcSemantic.Flags && ((long)v & (long)_a) == (long)_a && _a != 0);
            case Op.Ne: return Math.Abs(v - _a) >= 1e-6;
            case Op.Gt: return v > _a;
            case Op.Ge: return v >= _a;
            case Op.Lt: return v < _a;
            case Op.Le: return v <= _a;
            case Op.Range: return v >= Math.Min(_a, _b) && v <= Math.Max(_a, _b);
        }
        // text typed for a number column: the shown text (a choice's name, a link's label)
        var shown = DbcDiff.Describe(c, t.Raw(r, c.Field)).ToLowerInvariant();
        return _op == Op.NotContains ? !shown.Contains(_text, StringComparison.Ordinal) : shown.Contains(_text, StringComparison.Ordinal);
    }
}
