using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WoWData.Core.Dbc;

/// <summary>What happens to the table's rows that an imported file does not contain.</summary>
public enum DbcMissingRows
{
    /// <summary>Keep them (the file only changes and adds rows): the default.</summary>
    Keep,
    /// <summary>Remove them (the file is the whole table).</summary>
    Remove,
}

/// <summary>A row of the table that an import changes, with its new values per field.</summary>
public sealed record DbcImportChange(DbcRow Target, Dictionary<int, DbcCell> Values);

/// <summary>What an import would do: the rows it changes, adds and removes, its errors (which block it) and warnings,
/// and the preview of every change. Nothing is changed until <see cref="DbcEditor.Apply"/>.</summary>
public sealed class DbcImportPlan
{
    public List<DbcImportChange> Changed { get; } = new();
    public List<DbcRow> Added { get; } = new();
    public List<DbcRow> Removed { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public DbcDiffResult Preview { get; } = new();
    public int RowsRead { get; set; }
    public bool IsEmpty => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;
    public bool CanApply => Errors.Count == 0 && !IsEmpty;
}

/// <summary>
/// CSV and JSON export and import of a table. One column per field: a localized text becomes 9 columns
/// (Name_enUS ... Name_esMX and Name_flags), so an exported file holds everything and imports back with no change.
/// Numbers are written in decimal (decimal numbers with the shortest text that reads back to the same value), texts as
/// they are. CSV is UTF-8 with a byte-order mark (spreadsheet programs then read accents correctly), comma-separated, with
/// quotes where needed.
/// </summary>
public static class DbcExchange
{
    public sealed record Header(string Name, DbcColumn Column, int Field);

    /// <summary>The export columns of a layout, in file order.</summary>
    public static List<Header> Headers(DbcLayout layout)
    {
        var h = new List<Header>();
        foreach (var c in layout.Columns)
        {
            if (c.Storage == DbcStorage.Loc)
            {
                for (int l = 0; l < DbcFile.Locales; l++) h.Add(new Header($"{c.Name}_{DbcTable.LocaleNames[l]}", c, c.Field + l));
                h.Add(new Header($"{c.Name}_flags", c, DbcTable.LocFlagsField(c)));
            }
            else h.Add(new Header(c.Name, c, c.Field));
        }
        return h;
    }

    private static string Value(DbcTable t, DbcRow r, Header h) =>
        t.IsStringField(h.Field) ? t.Text(r, h.Field) : h.Column.Storage == DbcStorage.Loc ? t.Raw(r, h.Field).ToString(CultureInfo.InvariantCulture) : DbcTable.FormatRaw(h.Column, t.Raw(r, h.Field));

    // ------------------------------------------------------------------ CSV

    public static string ToCsv(DbcTable t, IEnumerable<DbcRow>? rows = null)
    {
        var hs = Headers(t.Layout);
        var sb = new StringBuilder();
        sb.AppendJoin(',', hs.Select(h => Quote(h.Name))).Append("\r\n");
        foreach (var r in rows ?? t.Rows)
            sb.AppendJoin(',', hs.Select(h => Quote(Value(t, r, h)))).Append("\r\n");
        return sb.ToString();
    }

    public static void WriteCsv(DbcTable t, string path, IEnumerable<DbcRow>? rows = null) =>
        File.WriteAllText(path, ToCsv(t, rows), new UTF8Encoding(true));

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || s.StartsWith(' ') || s.EndsWith(' ') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Parse CSV text (RFC 4180: quoted fields may hold commas, quotes and line breaks) into records with the line
    /// each starts on.</summary>
    public static List<(int Line, List<string> Fields)> ParseCsv(string text)
    {
        var res = new List<(int, List<string>)>();
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        int i = 0, line = 1;
        while (i < text.Length)
        {
            int start = line;
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, any = false;
            for (; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else
                    {
                        if (ch == '\n') line++;
                        sb.Append(ch);
                    }
                    continue;
                }
                if (ch == '"') { inQuotes = true; any = true; continue; }
                if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); any = true; continue; }
                if (ch == '\r') continue;
                if (ch == '\n') { line++; i++; break; }
                sb.Append(ch);
                any = true;
            }
            fields.Add(sb.ToString());
            if (any || fields.Count > 1 || fields[0].Length > 0) res.Add((start, fields));
        }
        return res;
    }

    public static DbcImportPlan PlanCsv(DbcTable t, string text, DbcMissingRows missing, DbcTableLookup? lookup)
    {
        var recs = ParseCsv(text);
        if (recs.Count == 0)
        {
            var p = new DbcImportPlan();
            p.Errors.Add("The file is empty.");
            return p;
        }
        var header = recs[0].Fields;
        var rows = recs.Skip(1).Select(r => (r.Line, Values: (IReadOnlyList<string?>)r.Fields.Cast<string?>().ToList())).ToList();
        return Plan(t, header, rows, missing, lookup, "line");
    }

    // ------------------------------------------------------------------ JSON

    public static string ToJson(DbcTable t, IEnumerable<DbcRow>? rows = null)
    {
        var hs = Headers(t.Layout);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("table", t.Name);
            w.WriteString("layout", "1.12.1 (build 5875)");
            w.WriteStartArray("rows");
            foreach (var r in rows ?? t.Rows)
            {
                w.WriteStartObject();
                foreach (var h in hs)
                {
                    if (t.IsStringField(h.Field))
                    {
                        w.WriteString(h.Name, t.Text(r, h.Field));
                        continue;
                    }
                    var s = Value(t, r, h);
                    if (s.StartsWith("raw:", StringComparison.Ordinal)) w.WriteString(h.Name, s);   // a NaN keeps its exact bits as text
                    else
                    {
                        w.WritePropertyName(h.Name);
                        w.WriteRawValue(s, skipInputValidation: false);
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static DbcImportPlan PlanJson(DbcTable t, string text, DbcMissingRows missing, DbcTableLookup? lookup)
    {
        var p0 = new DbcImportPlan();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException e)
        {
            p0.Errors.Add("The file is not valid JSON: " + e.Message);
            return p0;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var r) ? r : default;
            if (arr.ValueKind != JsonValueKind.Array)
            {
                p0.Errors.Add("The JSON file has no list of rows (expected {\"rows\": [ ... ]} or [ ... ]).");
                return p0;
            }
            var header = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<(int, IReadOnlyList<string?>)>();
            int n = 0;
            foreach (var o in arr.EnumerateArray())
            {
                n++;
                if (o.ValueKind != JsonValueKind.Object)
                {
                    p0.Errors.Add($"row {n}: not an object.");
                    continue;
                }
                var vals = new List<string?>(new string?[header.Count]);
                foreach (var prop in o.EnumerateObject())
                {
                    if (!index.TryGetValue(prop.Name, out int k))
                    {
                        k = header.Count;
                        index[prop.Name] = k;
                        header.Add(prop.Name);
                    }
                    while (vals.Count <= k) vals.Add(null);
                    vals[k] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText(),
                    };
                }
                rows.Add((n, vals));
            }
            if (p0.Errors.Count > 0) return p0;
            // rows read before a later property appeared are padded with "not given"
            var padded = rows.Select(r => (r.Item1, (IReadOnlyList<string?>)r.Item2.Concat(Enumerable.Repeat<string?>(null, Math.Max(0, header.Count - r.Item2.Count))).ToList())).ToList();
            return Plan(t, header, padded, missing, lookup, "row");
        }
    }

    // ------------------------------------------------------------------ the import plan

    private static DbcImportPlan Plan(DbcTable t, List<string> header, List<(int Line, IReadOnlyList<string?> Values)> rows, DbcMissingRows missing,
        DbcTableLookup? lookup, string lineWord)
    {
        var plan = new DbcImportPlan { RowsRead = rows.Count };
        var all = Headers(t.Layout);
        var byName = new Dictionary<string, Header>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in all) byName[h.Name] = h;
        // a localized column may also be given by its bare name (= the first language, enUS)
        foreach (var c in t.Layout.Columns.Where(c => c.Storage == DbcStorage.Loc)) byName.TryAdd(c.Name, all.First(h => h.Field == c.Field));
        var map = new Header?[header.Count];
        var unknown = new List<string>();
        var fieldsGiven = new HashSet<int>();
        for (int i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (byName.TryGetValue(name, out var h))
            {
                if (!fieldsGiven.Add(h.Field)) plan.Errors.Add($"The column {name} appears twice.");
                map[i] = h;
            }
            else if (name.Length > 0) unknown.Add(name);
        }
        if (unknown.Count > 0) plan.Warnings.Add($"Ignored {unknown.Count} column(s) that {t.Name} does not have: {string.Join(", ", unknown.Take(8))}{(unknown.Count > 8 ? ", ..." : "")}.");
        if (fieldsGiven.Count == 0)
        {
            plan.Errors.Add($"None of the file's columns belongs to {t.Name}. Is it the export of another table?");
            return plan;
        }
        var notGiven = all.Where(h => !fieldsGiven.Contains(h.Field)).Select(h => h.Name).ToList();
        if (notGiven.Count > 0)
            plan.Warnings.Add($"The file has no column {string.Join(", ", notGiven.Take(8))}{(notGiven.Count > 8 ? $" and {notGiven.Count - 8} more" : "")}: those values are kept (new rows get 0 / empty).");
        var key = t.Layout.KeyColumn;
        bool byKey = key != null && fieldsGiven.Contains(key.Field);
        if (key != null && !byKey) plan.Warnings.Add($"The file has no {key.Name} column: rows are matched by their position.");
        int keyIdx = byKey ? Array.FindIndex(map, h => h?.Field == key!.Field) : -1;
        // the IDs the file uses (so links between new rows of the same table are allowed)
        var fileKeys = new HashSet<long>();
        var matched = new HashSet<DbcRow>();
        int pos = 0;
        foreach (var (line, vals) in rows)
        {
            pos++;
            DbcRow? target = null;
            if (byKey)
            {
                var kt = keyIdx < vals.Count ? vals[keyIdx] : null;
                if (kt == null)
                {
                    plan.Errors.Add($"{lineWord} {line}: {key!.Name} is missing.");
                    continue;
                }
                if (!DbcTable.TryParse(key!, kt, out uint kraw, out var kerr))
                {
                    plan.Errors.Add($"{lineWord} {line}: {kerr}");
                    continue;
                }
                long kv = key!.Storage == DbcStorage.Int32 ? unchecked((int)kraw) : kraw;
                if (!fileKeys.Add(kv))
                {
                    plan.Errors.Add($"{lineWord} {line}: ID {kv} appears twice in the file.");
                    continue;
                }
                target = t.Find(kv);
            }
            else if (pos <= t.Rows.Count) target = t.Rows[pos - 1];
            if (target != null && !matched.Add(target))
            {
                plan.Errors.Add($"{lineWord} {line}: matches a row that an earlier line already changed.");
                continue;
            }
            var values = new Dictionary<int, DbcCell>();
            bool bad = false;
            for (int i = 0; i < map.Length && i < vals.Count; i++)
            {
                var h = map[i];
                var text = vals[i];
                if (h == null || text == null) continue;
                if (t.IsStringField(h.Field))
                {
                    if (text.Contains('\0')) { plan.Errors.Add($"{lineWord} {line}, {h.Name}: a text cannot contain a zero character."); bad = true; continue; }
                    values[h.Field] = DbcCell.Of(text);
                    continue;
                }
                // the flags field of a localized text, or a number
                var col = h.Column.Storage == DbcStorage.Loc ? new DbcColumn { Name = h.Name, Storage = DbcStorage.UInt32 } : h.Column;
                if (!DbcTable.TryParse(col, text, out uint raw, out var err))
                {
                    plan.Errors.Add($"{lineWord} {line}: {err}");
                    bad = true;
                    continue;
                }
                values[h.Field] = DbcCell.Of(raw);
            }
            if (bad) continue;
            if (target == null)
            {
                var row = t.NewRow();
                foreach (var (f, v) in values) SetRaw(t, row, f, v);
                plan.Added.Add(row);
                plan.Preview.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Added, byKey ? (t.Key(row) ?? 0).ToString() : $"row {pos}", Label(t, row), Array.Empty<DbcCellDiff>()));
            }
            else
            {
                var changed = values.Where(kv => !t.GetCell(target, kv.Key).SameAs(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (changed.Count == 0) continue;
                plan.Changed.Add(new DbcImportChange(target, changed));
                var after = target.Clone();
                foreach (var (f, v) in changed) SetRaw(t, after, f, v);
                plan.Preview.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Changed, byKey ? t.Key(target)!.Value.ToString() : $"row {pos}", t.Label(target), DbcDiff.Cells(t, target, t, after)));
            }
        }
        if (missing == DbcMissingRows.Remove)
            foreach (var r in t.Rows)
                if (!matched.Contains(r) && !(byKey && fileKeys.Contains(t.Key(r)!.Value)))
                {
                    plan.Removed.Add(r);
                    plan.Preview.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Removed, t.Key(r)?.ToString() ?? "", t.Label(r), Array.Empty<DbcCellDiff>()));
                }
        // validation of the new values: types were checked above; now links, yes/no, choices
        var check = new List<(DbcRow Row, Dictionary<int, DbcCell> Values, bool IsNew)>();
        check.AddRange(plan.Changed.Select(c => (c.Target, c.Values, false)));
        check.AddRange(plan.Added.Select(r => (r, t.Layout.Columns.Where(c => !c.IsString).ToDictionary(c => c.Field, c => DbcCell.Of(t.Raw(r, c.Field))), true)));
        int warn = 0;
        foreach (var (row, vals, isNew) in check)
            foreach (var (f, v) in vals)
            {
                var c = t.Layout.Columns.FirstOrDefault(x => x.Field == f && !x.IsString);
                if (c == null || c.IsKey) continue;
                foreach (var p in DbcValidation.CheckNumber(t, row, c, v.Raw, lookup, fileKeys))
                {
                    var where = $"{(t.Key(row) is { } k ? "ID " + k : "a row")}: ";
                    if (p.IsError) plan.Errors.Add(where + p.Message);
                    else if (warn++ < 50) plan.Warnings.Add(where + p.Message);
                }
            }
        if (warn > 50) plan.Warnings.Add($"... and {warn - 50} more warnings.");
        return plan;
    }

    private static void SetRaw(DbcTable t, DbcRow row, int field, DbcCell v)
    {
        if (t.IsStringField(field)) row.S = SetS(row, field, v.Bytes ?? Array.Empty<byte>());
        else row.F[field] = v.Raw;
    }

    private static byte[]?[] SetS(DbcRow row, int field, byte[] bytes)
    {
        var s = row.S ?? new byte[]?[row.F.Length];
        s[field] = bytes;
        return s;
    }

    private static string Label(DbcTable t, DbcRow r)
    {
        var lc = t.Layout.LabelColumn;
        if (lc != null)
        {
            var s = t.Text(r, lc);
            if (s.Length > 0) return s;
        }
        return t.Key(r) is { } k ? $"#{k}" : "new row";
    }
}
