using System.Text;
using System.Text.Json;
using WoWData.Core.Dbc;

namespace WoWData.Core.ServerData;

/// <summary>An import of a CSV or JSON file into a server table, before it is applied: what changes, and what blocks it.</summary>
public sealed class ServerImportPlan
{
    public WorldTable Table { get; init; } = null!;
    public string File { get; init; } = "";
    public int RowsRead { get; set; }
    public List<(string Key, string?[]? After)> Rows { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public DbcDiffResult Preview { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// 0.9.0: CSV (for spreadsheets) and JSON of server rows, and imports with a preview. A file holds every column with its
/// exact value (NULL is \N in CSV and null in JSON), so exporting and importing a file unchanged changes nothing. Rows
/// are matched by the table's key; the preview lists every changed, added and (with "remove the rows the file does not
/// have") removed row before anything changes; an import is one undo step of the editor, saved like any edit.
/// </summary>
public static class ServerExchange
{
    public static void Export(WorldTable t, IReadOnlyList<string?[]> rows, string file, string database)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = System.IO.File.Create(file);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            w.WriteStartObject();
            w.WriteString("table", t.Name);
            w.WriteString("database", database);
            w.WriteStartArray("key");
            foreach (var k in t.Key) w.WriteStringValue(k);
            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (var r in rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < t.Columns.Count; i++)
                {
                    var c = t.Columns[i];
                    w.WritePropertyName(c.Name);
                    if (r[i] == null) w.WriteNullValue();
                    else if (c.IsNumber) w.WriteRawValue(r[i]!);
                    else w.WriteStringValue(r[i]);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", t.Columns.Select(c => Csv(c.Name))));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(v => v == null ? WorldValue.NullText : Csv(v))));
        System.IO.File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v.Length > 0 && (v[0] == ' ' || v[^1] == ' ') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    /// <summary>RFC 4180 records of a CSV text.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    if (any || row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    any = false;
                    break;
                default: cell.Append(ch); any = true; break;
            }
        }
        if (any || cell.Length > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The rows of a file as column -> value (null = NULL).</summary>
    public static List<Dictionary<string, string?>> Read(string file)
    {
        var res = new List<Dictionary<string, string?>>();
        if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(file));
            var rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("rows");
            foreach (var r in rows.EnumerateArray())
            {
                var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in r.EnumerateObject())
                    d[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.True => "1",
                        JsonValueKind.False => "0",
                        _ => p.Value.GetRawText(),
                    };
                res.Add(d);
            }
            return res;
        }
        var text = System.IO.File.ReadAllText(file, Encoding.UTF8).TrimStart('﻿');
        var csv = ParseCsv(text);
        if (csv.Count == 0) return res;
        var header = csv[0].Select(h => h.Trim()).ToList();
        foreach (var r in csv.Skip(1))
        {
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++) d[header[i]] = i < r.Count ? r[i] == WorldValue.NullText ? null : r[i] : null;
            res.Add(d);
        }
        return res;
    }

    /// <summary>Plan the import of a file into a table. <paramref name="removeMissing"/>: rows of <paramref name="scope"/>
    /// (the rows the file stands for; all rows when null) that the file does not have are removed.</summary>
    public static ServerImportPlan Plan(ServerEditor ed, string table, string file, bool removeMissing = false, RowFilter? scope = null)
    {
        var t = ed.Table(table);
        var plan = new ServerImportPlan { Table = t, File = file };
        var rows = Read(file);
        plan.RowsRead = rows.Count;
        if (rows.Count > 0)
            foreach (var col in rows[0].Keys.Where(k => !t.Has(k)))
                plan.Errors.Add($"The file has a column \"{col}\" that {t.Name} does not have.");
        if (plan.Errors.Count > 0) return plan;
        var seen = new HashSet<string>();
        var browser = new ServerBrowser(ed);
        int n = 0;
        foreach (var r in rows)
        {
            n++;
            // start from the current row of the key (columns the file lacks keep their values) or a new row's defaults
            var keyVals = new Dictionary<string, string?>();
            bool keyOk = true;
            foreach (var k in t.Key)
            {
                if (!r.TryGetValue(k, out var kv)) { plan.Errors.Add($"Row {n}: the key column {k} is missing."); keyOk = false; break; }
                var problem = WorldValue.Normalize(t.Column(k)!, kv, out var canon);
                if (problem != null) { plan.Errors.Add($"Row {n}: {k}: {problem}"); keyOk = false; break; }
                keyVals[k] = canon;
            }
            if (!keyOk) continue;
            var key = string.Join(";", t.Key.Select(k => k + "=" + (keyVals[k] ?? "\\N")));
            if (!seen.Add(key)) { plan.Errors.Add($"Row {n}: the key {key} is in the file twice."); continue; }
            var cur = ed.Current(t, key);
            var row = cur != null ? (string?[])cur.Clone() : t.Columns.Select(c => c.Default ?? (c.Nullable ? null : c.IsNumber ? "0" : "")).ToArray();
            bool bad = false;
            foreach (var (col, v) in r)
            {
                var problems = ServerValidation.CheckValue(ed, t, col, v, out var canon);
                var err = problems.FirstOrDefault(p => p.IsError);
                if (err != null && !(err.Message.Contains("does not exist", StringComparison.Ordinal) && LinksToFileRow(t, col, v, rows)))
                {
                    plan.Errors.Add($"Row {n} ({key}): {col}: {err.Message}");
                    bad = true;
                    break;
                }
                foreach (var p in problems.Where(p => !p.IsError)) plan.Warnings.Add($"Row {n} ({key}): {col}: {p.Message}");
                row[t.IndexOf(col)] = canon;
            }
            if (bad) continue;
            if (cur != null && t.Columns.Select((c, i) => WorldValue.Same(c, cur[i], row[i])).All(x => x)) continue;
            plan.Rows.Add((key, row));
            plan.Preview.Rows.Add(cur == null
                ? new DbcRowDiff(DbcRowChangeKind.Added, key, browser.NameOf(t, row), Array.Empty<DbcCellDiff>())
                : new DbcRowDiff(DbcRowChangeKind.Changed, key, browser.NameOf(t, row), t.Columns.Select((c, i) => (c, i)).Where(x => !WorldValue.Same(x.c, cur[x.i], row[x.i]))
                    .Select(x => new DbcCellDiff(x.c.Name, cur[x.i] ?? "NULL", row[x.i] ?? "NULL")).ToList()));
        }
        if (removeMissing && plan.Errors.Count == 0)
        {
            foreach (var cur in ed.Rows(t, scope ?? RowFilter.All()))
            {
                var key = t.KeyText(cur);
                if (seen.Contains(key)) continue;
                plan.Rows.Add((key, null));
                plan.Preview.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Removed, key, browser.NameOf(t, cur), Array.Empty<DbcCellDiff>()));
            }
        }
        return plan;
    }

    /// <summary>A link to a row that the same file adds (allowed).</summary>
    private static bool LinksToFileRow(WorldTable t, string col, string? v, List<Dictionary<string, string?>> rows)
    {
        if (v == null || t.Key.Count != 1) return false;
        return rows.Any(r => r.TryGetValue(t.Key[0], out var k) && k == v);
    }

    /// <summary>Apply a checked plan as one undo step of the editor.</summary>
    public static string Apply(ServerEditor ed, ServerImportPlan plan)
    {
        if (plan.Errors.Count > 0) throw new ServerDataException($"{plan.Errors.Count} error(s) block the import: fix the file and import again.");
        int n = ed.ApplyRows($"Import {Path.GetFileName(plan.File)} into {plan.Table.Name}", plan.Rows.Select(r => (plan.Table, r.Key, r.After)));
        return $"Imported {Path.GetFileName(plan.File)}: {plan.Preview.Summary} ({n} row(s) in your edits).";
    }

    /// <summary>The row-by-row difference of a table's server edits (for Compare and the save confirmation).</summary>
    public static DbcDiffResult Diff(ServerEditor ed, ServerTableChanges ch)
    {
        var res = new DbcDiffResult();
        var browser = new ServerBrowser(ed);
        var t = ch.Table;
        foreach (var r in ch.Rows.Values)
        {
            var label = browser.NameOf(t, r.After ?? r.Before!);
            if (r.IsAdded) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Added, r.Key, label, Array.Empty<DbcCellDiff>()));
            else if (r.IsDeleted) res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Removed, r.Key, label, Array.Empty<DbcCellDiff>()));
            else
                res.Rows.Add(new DbcRowDiff(DbcRowChangeKind.Changed, r.Key, label, t.Columns.Select((c, i) => (c, i)).Where(x => !WorldValue.Same(x.c, r.Before![x.i], r.After![x.i]))
                    .Select(x => new DbcCellDiff(x.c.Name, r.Before![x.i] ?? "NULL", r.After![x.i] ?? "NULL")).ToList()));
        }
        return res;
    }
}
