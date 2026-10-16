using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WoWData.Core.ServerData;

/// <summary>One row of your server edits: how it was on the server before your first edit (null: you added it) and how
/// it is now (null: you deleted it). Both have the same key.</summary>
public sealed class ServerRowChange
{
    public ServerRowChange(string key, string?[]? before, string?[]? after)
    {
        Key = key;
        Before = before;
        After = after;
    }

    public string Key { get; }
    public string?[]? Before { get; }
    public string?[]? After { get; }

    public bool IsAdded => Before == null && After != null;
    public bool IsDeleted => Before != null && After == null;
    public bool IsChanged => Before != null && After != null;
    public string KindText => IsAdded ? "Added" : IsDeleted ? "Deleted" : "Changed";
}

/// <summary>Your edits of one server table (the content of one workspace file).</summary>
public sealed class ServerTableChanges
{
    public ServerTableChanges(WorldTable table) { Table = table; }

    public WorldTable Table { get; }
    public SortedDictionary<string, ServerRowChange> Rows { get; } = new(StringComparer.Ordinal);

    public int Changed => Rows.Values.Count(r => r.IsChanged);
    public int Added => Rows.Values.Count(r => r.IsAdded);
    public int Deleted => Rows.Values.Count(r => r.IsDeleted);

    public string Summary => $"{Table.Name}: " + string.Join(", ", new[]
    {
        Changed > 0 ? $"{Changed} row{(Changed == 1 ? "" : "s")} changed" : null,
        Added > 0 ? $"{Added} added" : null,
        Deleted > 0 ? $"{Deleted} deleted" : null,
    }.Where(x => x != null));

    public ServerTableChanges Clone()
    {
        var c = new ServerTableChanges(Table);
        foreach (var (k, v) in Rows) c.Rows[k] = v;
        return c;
    }
}

/// <summary>
/// 0.9.0: your edits of the server's world database live in the workspace like every other edit: one file per table,
/// <c>ServerData\&lt;database&gt;\&lt;table&gt;.json</c>, listed in My changes (kind "server"), with the rows before and after.
/// The file holds, per changed row, the key, the row as it was on the server before your first edit and the row as you
/// want it (or null for an added / deleted row), every value as its exact text. From it come the row diff (Compare),
/// the SQL patch and its revert script, "Apply to server" and its backup check.
/// </summary>
public static class ServerChangeFiles
{
    public const string Folder = "ServerData";
    public const string Format = "wds-server-changes";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The workspace path of a table's edits: ServerData\mangos\item_template.json.</summary>
    public static string PathOf(string database, string table) => $"{Folder}\\{database}\\{table}.json";

    /// <summary>True for the workspace path of server edits (they are not game files: never installed or packed).</summary>
    public static bool IsServerPath(string path) => path.Replace('/', '\\').StartsWith(Folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>(database, table) of a server-edit path, or null.</summary>
    public static (string Database, string Table)? Parse(string path)
    {
        if (!IsServerPath(path)) return null;
        var parts = path.Replace('/', '\\').Split('\\');
        if (parts.Length != 3 || !parts[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        return (parts[1], parts[2][..^5]);
    }

    /// <summary>The file's bytes (deterministic: the same edits always give the same bytes).</summary>
    public static byte[] Write(string database, ServerTableChanges ch)
    {
        var t = ch.Table;
        var o = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["database"] = database,
            ["table"] = t.Name,
            ["key"] = new JsonArray(t.Key.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["summary"] = ch.Summary,
        };
        var rows = new JsonArray();
        foreach (var (k, r) in ch.Rows)
        {
            rows.Add(new JsonObject
            {
                ["key"] = k,
                ["change"] = r.KindText.ToLowerInvariant(),
                ["before"] = RowObject(t, r.Before),
                ["after"] = RowObject(t, r.After),
            });
        }
        o["rows"] = rows;
        return Encoding.UTF8.GetBytes(o.ToJsonString(Json).Replace("\r\n", "\n") + "\n");
    }

    private static JsonObject? RowObject(WorldTable t, string?[]? row)
    {
        if (row == null) return null;
        var o = new JsonObject();
        for (int i = 0; i < t.Columns.Count; i++) o[t.Columns[i].Name] = row[i] == null ? null : JsonValue.Create(row[i]);
        return o;
    }

    /// <summary>Read a file into the edits of a table of the live schema (columns matched by name; a column the file does
    /// not have gets its default). Throws when the file is not a server-edit file.</summary>
    public static ServerTableChanges Read(byte[] data, WorldTable t)
    {
        var root = JsonNode.Parse(data) as JsonObject ?? throw new ServerDataException("not a server-edit file");
        if ((string?)root["format"] != Format) throw new ServerDataException("not a server-edit file (format)");
        var ch = new ServerTableChanges(t);
        foreach (var n in root["rows"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject r) continue;
            var before = Row(t, r["before"] as JsonObject);
            var after = Row(t, r["after"] as JsonObject);
            var key = before != null ? t.KeyText(before) : after != null ? t.KeyText(after) : (string?)r["key"] ?? "";
            ch.Rows[key] = new ServerRowChange(key, before, after);
        }
        return ch;
    }

    private static string?[]? Row(WorldTable t, JsonObject? o)
    {
        if (o == null) return null;
        var row = new string?[t.Columns.Count];
        for (int i = 0; i < row.Length; i++)
            row[i] = o.TryGetPropertyValue(t.Columns[i].Name, out var v) ? v == null ? null : v.GetValue<string>() : t.Columns[i].Default;
        return row;
    }
}
