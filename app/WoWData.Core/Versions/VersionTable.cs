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
            t.Layout.IsFallback ? t.Layout.About : "", t.FieldCount, rows);
    }

    public static VersionTable FromDb2(Db2Table t)
    {
        var cols = t.Columns;
        var names = cols.Select(c => c.Name.Length > 0 ? c.Name : "col_" + c.Index).ToList();
        var named = cols.Select(c => c.NameVerified).ToList();
        int namedCount = named.Count(n => n);
        bool raw = namedCount <= 1;
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
        string coverage = raw ? "raw" : namedCount == cols.Count ? "All columns named" : $"{namedCount} of {cols.Count} columns named";
        return new VersionTable(t.Name, names, named, text, raw, coverage, string.Join("; ", notes), t.FieldCount, rows);
    }
}
