namespace WoWData.Core.Dbc;

public enum DbcRowChangeKind { Changed, Added, Removed }

/// <summary>One changed value: the column (with the language for a localized text) and the value before and after.</summary>
public sealed record DbcCellDiff(string Column, string Before, string After);

/// <summary>One row of a comparison: changed (with its changed values), added or removed.</summary>
public sealed record DbcRowDiff(DbcRowChangeKind Kind, string Key, string Label, IReadOnlyList<DbcCellDiff> Cells)
{
    public string KindText => Kind switch { DbcRowChangeKind.Added => "Added", DbcRowChangeKind.Removed => "Removed", _ => "Changed" };

    /// <summary>What changed, for lists: the first changed values, or "new row" / "row removed".</summary>
    public string DetailsText => Kind switch { DbcRowChangeKind.Added => "new row", DbcRowChangeKind.Removed => "row removed", _ => Details() };

    /// <summary>"Name: Fireball → Firebolt; SpellIconID: 185 → 186" (first values).</summary>
    public string Details(int max = 6) => Kind == DbcRowChangeKind.Changed
        ? string.Join("; ", Cells.Take(max).Select(c => $"{c.Column}: {Short(c.Before)} → {Short(c.After)}")) + (Cells.Count > max ? $"; and {Cells.Count - max} more" : "")
        : "";

    private static string Short(string s) => s.Length == 0 ? "(empty)" : s.Length > 60 ? s[..57] + "..." : s;
}

/// <summary>The row-by-row difference of two versions of a table (the game's and yours).</summary>
public sealed class DbcDiffResult
{
    public List<DbcRowDiff> Rows { get; } = new();
    public int Changed => Rows.Count(r => r.Kind == DbcRowChangeKind.Changed);
    public int Added => Rows.Count(r => r.Kind == DbcRowChangeKind.Added);
    public int Removed => Rows.Count(r => r.Kind == DbcRowChangeKind.Removed);
    public int CellsChanged => Rows.Where(r => r.Kind == DbcRowChangeKind.Changed).Sum(r => r.Cells.Count);
    public bool IsEmpty => Rows.Count == 0;

    public string Summary => IsEmpty ? "No differences."
        : $"{Changed:N0} row{(Changed == 1 ? "" : "s")} changed ({CellsChanged:N0} value{(CellsChanged == 1 ? "" : "s")}), {Added:N0} added, {Removed:N0} removed.";
}

public static class DbcDiff
{
    /// <summary>Compare two versions of a table. Rows are matched by ID when both have unique IDs, else by position.</summary>
    public static DbcDiffResult Compare(DbcTable before, DbcTable after, int locale = 0)
    {
        var res = new DbcDiffResult();
        var key = after.Layout.KeyColumn;
        bool keyed = key != null && before.Layout.KeyColumn != null && Unique(before) && Unique(after) && SameShape(before, after);
        if (keyed)
        {
            var old = new Dictionary<long, DbcRow>();
            foreach (var r in before.Rows) old[before.Key(r)!.Value] = r;
            var seen = new HashSet<long>();
            foreach (var r in after.Rows)
            {
                long k = after.Key(r)!.Value;
                seen.Add(k);
                if (old.TryGetValue(k, out var o))
                {
                    var cells = Cells(before, o, after, r);
                    if (cells.Count > 0) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Changed, k.ToString(), after.Label(r, locale), cells));
                }
                else res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Added, k.ToString(), after.Label(r, locale), Array.Empty<DbcCellDiff>()));
            }
            foreach (var o in before.Rows)
            {
                long k = before.Key(o)!.Value;
                if (!seen.Contains(k)) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Removed, k.ToString(), before.Label(o, locale), Array.Empty<DbcCellDiff>()));
            }
            return res;
        }
        int n = Math.Max(before.Rows.Count, after.Rows.Count);
        bool same = SameShape(before, after);
        for (int i = 0; i < n; i++)
        {
            string k = $"row {i + 1}";
            if (i >= before.Rows.Count) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Added, k, after.Label(after.Rows[i], locale), Array.Empty<DbcCellDiff>()));
            else if (i >= after.Rows.Count) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Removed, k, before.Label(before.Rows[i], locale), Array.Empty<DbcCellDiff>()));
            else if (same)
            {
                var cells = Cells(before, before.Rows[i], after, after.Rows[i]);
                if (cells.Count > 0) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Changed, k, after.Label(after.Rows[i], locale), cells));
            }
        }
        return res;
    }

    private static bool SameShape(DbcTable a, DbcTable b) => a.FieldCount == b.FieldCount && a.RecordSize == b.RecordSize;

    private static bool Unique(DbcTable t)
    {
        var s = new HashSet<long>();
        foreach (var r in t.Rows)
            if (!s.Add(t.Key(r)!.Value)) return false;
        return true;
    }

    /// <summary>The changed values of a row (every field: all languages of a localized text and its flags).</summary>
    public static List<DbcCellDiff> Cells(DbcTable ta, DbcRow a, DbcTable tb, DbcRow b)
    {
        var res = new List<DbcCellDiff>();
        foreach (var c in tb.Layout.Columns)
        {
            if (c.Storage == DbcStorage.Loc)
            {
                for (int l = 0; l < DbcFile.Locales; l++)
                {
                    var x = ta.Text(a, c.Field + l);
                    var y = tb.Text(b, c.Field + l);
                    if (x != y) res.Add(new DbcCellDiff(l == 0 ? c.Name : $"{c.Name} ({DbcTable.LocaleNames[l]})", x, y));
                }
                int ff = DbcTable.LocFlagsField(c);
                if (ta.Raw(a, ff) != tb.Raw(b, ff)) res.Add(new DbcCellDiff($"{c.Name} (flags)", ta.Raw(a, ff).ToString(), tb.Raw(b, ff).ToString()));
            }
            else if (c.IsString)
            {
                var x = ta.Text(a, c.Field);
                var y = tb.Text(b, c.Field);
                if (x != y) res.Add(new DbcCellDiff(c.Name, x, y));
            }
            else if (ta.Raw(a, c.Field) != tb.Raw(b, c.Field))
                res.Add(new DbcCellDiff(c.Name, Describe(c, ta.Raw(a, c.Field)), Describe(c, tb.Raw(b, c.Field))));
        }
        return res;
    }

    /// <summary>A number with its name when the column has named values ("2 (Fire)").</summary>
    public static string Describe(DbcColumn c, uint raw)
    {
        var s = DbcTable.FormatRaw(c, raw);
        if (c.Values != null)
        {
            long v = c.Storage == DbcStorage.Int32 ? unchecked((int)raw) : raw;
            var n = c.Values.Describe(v);
            if (n.Length > 0) return $"{s} ({n})";
        }
        return s;
    }
}
