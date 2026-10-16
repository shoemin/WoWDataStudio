using System.Globalization;
using System.Text;
using System.Text.Json;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>One quest of an export file: its fields (by field id, the stored values), givers and enders.</summary>
public sealed class QuestRecord
{
    public long Id { get; set; }
    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Givers { get; } = new();
    public List<string> Enders { get; } = new();
}

/// <summary>
/// 0.13.0: quests to and from files: JSON (<c>{"format":"wds-quests","quests":[{"id":..,"fields":{..},"givers":[..],"enders":[..]}]}</c>)
/// or CSV (one row per quest: id, the field ids of <see cref="QuestForm"/>, givers and enders as "creature:234;object:5").
/// Values are the stored numbers and texts (the same as the server's columns), so a file round-trips exactly. Importing
/// makes the quests that do not exist yet (with their IDs) and sets givers and enders to the file's lists.
/// </summary>
public static class QuestExchange
{
    public const string Format = "wds-quests";

    public static QuestRecord Record(QuestDesigner d, long id)
    {
        var row = d.Require(id);
        var rec = new QuestRecord { Id = id };
        foreach (var f in QuestForm.Fields)
            if (d.Quests.Has(f.Column)) rec.Fields[f.Id] = QuestDesigner.V(d.Quests, row, f.Column) ?? "";
        rec.Givers.AddRange(d.Givers(id).Where(g => g.Kind is "creature" or "object").Select(g => g.Key));
        rec.Enders.AddRange(d.Enders(id).Where(g => g.Kind is "creature" or "object").Select(g => g.Key));
        return rec;
    }

    public static void Export(QuestDesigner d, IEnumerable<long> ids, string file)
    {
        var recs = ids.Distinct().OrderBy(x => x).Select(id => Record(d, id)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ToCsv(recs) : ToJson(recs), new UTF8Encoding(!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
    }

    public static string ToJson(IReadOnlyList<QuestRecord> recs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", 1);
            w.WriteStartArray("quests");
            foreach (var r in recs)
            {
                w.WriteStartObject();
                w.WriteNumber("id", r.Id);
                w.WriteStartObject("fields");
                foreach (var f in QuestForm.Fields)
                    if (r.Fields.TryGetValue(f.Id, out var v)) w.WriteString(f.Id, v);
                w.WriteEndObject();
                w.WriteStartArray("givers");
                foreach (var g in r.Givers) w.WriteStringValue(g);
                w.WriteEndArray();
                w.WriteStartArray("enders");
                foreach (var g in r.Enders) w.WriteStringValue(g);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Csv(string v) => v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v.Length > 0 && (v[0] == ' ' || v[^1] == ' ') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    public static string ToCsv(IReadOnlyList<QuestRecord> recs)
    {
        var fields = QuestForm.Fields.Where(f => recs.Any(r => r.Fields.ContainsKey(f.Id))).ToList();
        var sb = new StringBuilder();
        sb.Append("id,").Append(string.Join(",", fields.Select(f => f.Id))).Append(",givers,enders\r\n");
        foreach (var r in recs)
        {
            sb.Append(r.Id.ToString(CultureInfo.InvariantCulture));
            foreach (var f in fields) sb.Append(',').Append(Csv(r.Fields.GetValueOrDefault(f.Id) ?? ""));
            sb.Append(',').Append(Csv(string.Join(";", r.Givers))).Append(',').Append(Csv(string.Join(";", r.Enders))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>The quests of a JSON or CSV file.</summary>
    public static List<QuestRecord> Read(string file)
    {
        var text = File.ReadAllText(file);
        if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !text.TrimStart().StartsWith('{')) return FromCsv(text);
        return FromJson(text);
    }

    public static List<QuestRecord> FromJson(string text)
    {
        var res = new List<QuestRecord>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("quests", out var qs) || qs.ValueKind != JsonValueKind.Array) throw new DesignException("Not a quest file: it has no \"quests\" list.");
        foreach (var q in qs.EnumerateArray())
        {
            var r = new QuestRecord { Id = q.GetProperty("id").GetInt64() };
            if (q.TryGetProperty("fields", out var fs))
                foreach (var p in fs.EnumerateObject())
                {
                    if (QuestForm.Find(p.Name) is not { } f) throw new DesignException($"Quest {r.Id}: there is no quest field \"{p.Name}\".");
                    r.Fields[f.Id] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
                }
            if (q.TryGetProperty("givers", out var gs)) r.Givers.AddRange(gs.EnumerateArray().Select(x => x.GetString()!));
            if (q.TryGetProperty("enders", out var es)) r.Enders.AddRange(es.EnumerateArray().Select(x => x.GetString()!));
            res.Add(r);
        }
        return res;
    }

    public static List<QuestRecord> FromCsv(string text)
    {
        var rows = ServerExchange.ParseCsv(text.TrimStart('﻿'));
        if (rows.Count == 0) return new();
        var head = rows[0].Select(h => h.Trim()).ToList();
        int idCol = head.FindIndex(h => h.Equals("id", StringComparison.OrdinalIgnoreCase));
        if (idCol < 0) throw new DesignException("The CSV has no \"id\" column.");
        var res = new List<QuestRecord>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count <= idCol || row[idCol].Trim().Length == 0) continue;
            var r = new QuestRecord { Id = long.Parse(row[idCol].Trim(), CultureInfo.InvariantCulture) };
            for (int i = 0; i < head.Count && i < row.Count; i++)
            {
                if (i == idCol) continue;
                var h = head[i];
                if (h.Equals("givers", StringComparison.OrdinalIgnoreCase)) r.Givers.AddRange(row[i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else if (h.Equals("enders", StringComparison.OrdinalIgnoreCase)) r.Enders.AddRange(row[i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else if (QuestForm.Find(h) is { } f) r.Fields[f.Id] = row[i];
                else throw new DesignException($"The CSV column \"{h}\" is not a quest field.");
            }
            res.Add(r);
        }
        return res;
    }

    /// <summary>Import quests: new ones are made with their IDs, existing ones get the file's values; givers and enders
    /// become the file's lists. Returns what changed, in words.</summary>
    public static List<string> Import(QuestDesigner d, IReadOnlyList<QuestRecord> recs)
    {
        var msgs = new List<string>();
        foreach (var r in recs)
        {
            bool made = false;
            if (!d.Exists(r.Id))
            {
                d.New(r.Fields.GetValueOrDefault("title") is { Length: > 0 } t ? t : $"Quest {r.Id}", r.Id);
                made = true;
            }
            var row = d.Require(r.Id);
            var values = new List<(string, string?)>();
            foreach (var (fid, v) in r.Fields)
            {
                var f = QuestForm.Require(fid);
                if (!d.Quests.Has(f.Column)) continue;
                if ((QuestDesigner.V(d.Quests, row, f.Column) ?? "") != v) values.Add((f.Column, v));
            }
            if (values.Count > 0) d.SetColumns(r.Id, values, $"Import of quest {r.Id}");
            int rel = 0;
            foreach (var (list, giver) in new[] { (r.Givers, true), (r.Enders, false) })
            {
                var now = (giver ? d.Givers(r.Id) : d.Enders(r.Id)).Where(x => x.Kind is "creature" or "object").Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var want = list.Select(x => { var (k, id) = QuestDesigner.ParseWho(x); return $"{k}:{id}"; }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var add in want.Except(now)) { Must(d.SetRelation(r.Id, add, giver, true)); rel++; }
                foreach (var del in now.Except(want)) { Must(d.SetRelation(r.Id, del, giver, false)); rel++; }
            }
            if (made || values.Count > 0 || rel > 0)
                msgs.Add($"{(made ? "New" : "Changed")} quest {d.Label(r.Id)}: {values.Count} value(s), {rel} giver / ender change(s).");
        }
        return msgs;
    }

    private static void Must(DesignResult r)
    {
        if (!r.Ok) throw new DesignException(r.Message);
    }
}
