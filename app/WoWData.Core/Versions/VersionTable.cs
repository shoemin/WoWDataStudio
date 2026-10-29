using WoWData.Core.Db2;
using WoWData.Core.Dbc;

namespace WoWData.Core.Versions;

/// <summary>
/// One data table of another client, read only, the same way for the old WDBC tables (<see cref="DbcTable"/>) and the
/// modern DB2 tables (<see cref="Db2Table"/>): column names (named where the version's layout is known, else col_N),
/// rows as text, rows by ID. What the Other versions tab, "wds versions" and <see cref="VersionCompare"/> show.
/// </summary>
public sealed class VersionTable
{
    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
    /// <summary>Per column: its meaning is known for this version (not col_N and not a best guess).</summary>
    public IReadOnlyList<bool> Named { get; }
    /// <summary>Per column: text (compared as text by "--where").</summary>
    public IReadOnlyList<bool> IsText { get; }
    /// <summary>True when no column besides the ID is named (the layout of this version is not known).</summary>
    public bool IsRaw { get; }
    /// <summary>"All columns named", "12 of 40 columns named" or "raw".</summary>
    public string Coverage { get; }
    /// <summary>A note about the table (encrypted records left out, a layout that does not match...), or "".</summary>
    public string Note { get; }
    public int RowCount => _rows.Count;
    public int FieldCount { get; }
    /// <summary>M5: where the column names come from (WoWDBDefs, our measured layout file, none) and, when WoWDBDefs
    /// could not name the table, why.</summary>
    public TableNaming Naming { get; private set; } = TableNaming.Raw;

    private readonly List<(long Id, Func<string[]> Values)> _rows;
    private Dictionary<long, int>? _byId;

    private VersionTable(string name, List<string> cols, List<bool> named, List<bool> text, bool raw, string coverage, string note, int fields,
        List<(long, Func<string[]>)> rows)
    {
        Name = name; Columns = cols; Named = named; IsText = text; IsRaw = raw; Coverage = coverage; Note = note; FieldCount = fields; _rows = rows;
    }

    public long IdAt(int i) => _rows[i].Id;
    public string[] RowAt(int i) => _rows[i].Values();
    public IEnumerable<string[]> Rows() => _rows.Select(r => r.Values());

    /// <summary>The row with this ID, or null.</summary>
    public string[]? Row(long id)
    {
        _byId ??= _rows.Select((r, i) => (r.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);
        return _byId.TryGetValue(id, out var i) ? _rows[i].Values() : null;
    }

    public static VersionTable FromDbc(DbcTable t, int locale = 0)
    {
        var cols = t.Layout.Columns;
        var key = t.Layout.KeyColumn ?? cols.FirstOrDefault();
        var rows = t.Rows.Select(r => (key == null ? 0L : t.Number(r, key), (Func<string[]>)(() => cols.Select(c => t.Format(r, c, locale)).ToArray()))).ToList();
        return new VersionTable(t.Name, cols.Select(c => c.Name).ToList(), cols.Select(c => c.Verified && !c.Name.StartsWith("col_", StringComparison.Ordinal)).ToList(),
            cols.Select(c => c.IsString).ToList(), t.Layout.IsFallback, t.Layout.IsFallback ? "raw" : t.Layout.CoverageText,
            t.Layout.IsFallback ? t.Layout.About : "", t.FieldCount, rows)
        { Naming = t.Layout.IsFallback ? TableNaming.Raw : new TableNaming(ColumnNameSource.Measured) };
    }

    /// <summary>A DB2 table whose columns were named (or not) by <paramref name="naming"/> (<see cref="ColumnNames.Apply"/>);
    /// without it, by our measured layout file when one named it.</summary>
    public static VersionTable FromDb2(Db2Table t, TableNaming? naming = null)
    {
        naming ??= t.Layout != null ? new TableNaming(ColumnNameSource.Measured) : TableNaming.Raw;
        var cols = t.Columns;
        var names = cols.Select(c => c.Name.Length > 0 ? c.Name : "col_" + c.Index).ToList();
        var named = cols.Select(c => c.NameVerified).ToList();
        int namedCount = named.Count(n => n);
        bool dbd = naming.Source == ColumnNameSource.WoWDBDefs;
        bool raw = !dbd && namedCount <= 1;
        // the ID: a column of the record (IdIndex) or the ID list; shown as the first column "ID" when it is not a column
        bool idIsColumn = !t.HasIdList && t.IdIndex >= 0 && t.IdIndex < cols.Count;
        if (!idIsColumn)
        {
            names.Insert(0, "ID");
            named.Insert(0, true);
        }
        var text = cols.Select(c => c.Type == Db2ValueType.String).ToList();
        if (!idIsColumn) text.Insert(0, false);
        var rows = t.Records.Select(r => ((long)r.Id, (Func<string[]>)(() =>
        {
            var v = new string[names.Count];
            int o = idIsColumn ? 0 : 1;
            if (!idIsColumn) v[0] = r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < cols.Count; i++) v[i + o] = r.Text(i);
            return v;
        }))).ToList();
        var notes = new List<string>();
        if (t.SkippedRecords > 0) notes.Add($"{t.SkippedRecords:N0} encrypted records left out");
        notes.AddRange(t.Notes);
        string coverage = raw ? "raw" : dbd ? (namedCount == cols.Count ? "All columns named (WoWDBDefs)" : $"All columns named (WoWDBDefs), {cols.Count - namedCount} not confirmed")
            : namedCount == cols.Count ? "All columns named" : $"{namedCount} of {cols.Count} columns named";
        return new VersionTable(t.Name, names, named, text, raw, coverage, string.Join("; ", notes), t.FieldCount, rows) { Naming = naming };
    }
}

/// <summary>
/// The read-only Data tables tab of another version and "wds dbc tables / rows / get / export --version": finding rows
/// of a <see cref="VersionTable"/> and exporting them as CSV or JSON to a folder you choose (never into the client).
/// </summary>
public static class VersionTableExport
{
    /// <summary>The indexes of the rows that have every word of <paramref name="search"/> in some value and match every
    /// column filter (text: contains, case-insensitive; numbers: equal).</summary>
    public static List<int> Find(VersionTable t, string search, IEnumerable<(int Column, string Value)>? where = null)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filters = where?.ToList() ?? new();
        var res = new List<int>();
        for (int i = 0; i < t.RowCount; i++)
        {
            if (words.Length == 0 && filters.Count == 0)
            {
                res.Add(i);
                continue;
            }
            var r = t.RowAt(i);
            bool ok = filters.All(f => t.IsText[f.Column] ? r[f.Column].Contains(f.Value, StringComparison.OrdinalIgnoreCase) : r[f.Column] == f.Value)
                      && words.All(w => r.Any(v => v.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (ok) res.Add(i);
        }
        return res;
    }

    /// <summary>The column with this name (case-insensitive), or -1. A WoWDBDefs name is also found without its
    /// "_lang" ending (MapName finds MapName_lang).</summary>
    public static int Column(VersionTable t, string name)
    {
        for (int i = 0; i < t.Columns.Count; i++) if (string.Equals(t.Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < t.Columns.Count; i++) if (string.Equals(LogicalName(t.Columns[i]), LogicalName(name), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>A column name without WoWDBDefs' "_lang" ending (DB2 strings are one language): how names of
    /// different sources are matched (<see cref="VersionCompare"/>).</summary>
    public static string LogicalName(string column) =>
        column.EndsWith("_lang", StringComparison.OrdinalIgnoreCase) && column.Length > 5 ? column[..^5] : column;

    /// <summary>Writes rows as CSV (the header is the column names) or, for a .json file, as an array of objects.</summary>
    public static void Write(VersionTable t, IReadOnlyList<int> rows, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir != null) Directory.CreateDirectory(dir);
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var list = rows.Select(i =>
            {
                var r = t.RowAt(i);
                var o = new System.Text.Json.Nodes.JsonObject();
                for (int c = 0; c < t.Columns.Count; c++)
                    o[t.Columns[c]] = !t.IsText[c] && long.TryParse(r[c], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
                        : !t.IsText[c] && double.TryParse(r[c], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
                        : (System.Text.Json.Nodes.JsonNode?)r[c];
                return (System.Text.Json.Nodes.JsonNode?)o;
            }).ToArray();
            File.WriteAllText(file, new System.Text.Json.Nodes.JsonArray(list).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        using var w = new StreamWriter(file, false, new System.Text.UTF8Encoding(true));
        w.WriteLine(string.Join(",", t.Columns.Select(Csv)));
        foreach (var i in rows) w.WriteLine(string.Join(",", t.RowAt(i).Select(Csv)));
    }

    private static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
}
