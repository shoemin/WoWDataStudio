namespace WoWData.Core.Casc;

/// <summary>
/// A pipe-separated table of the Battle.net launcher: <c>.build.info</c> (next to the shared Data folder),
/// <c>versions</c> / <c>cdns</c> (older local stores such as WoD 6.2.3) and <c>.flavor.info</c> (in a flavor folder).
/// The first line names the columns as <c>Name!TYPE:size</c>; lines starting with <c>##</c> are comments.
/// </summary>
public sealed class CascTable
{
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    private CascTable(List<string> columns, List<IReadOnlyDictionary<string, string>> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public static CascTable Load(string path) => Parse(ReadText(path));

    /// <summary>Reads a text file without blocking a launcher that has it open (read-only, shared).</summary>
    internal static string ReadText(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    public static CascTable Parse(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.StartsWith("##", StringComparison.Ordinal)).ToList();
        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (lines.Count == 0) return new CascTable(columns, rows);
        columns.AddRange(lines[0].Split('|').Select(h => h.Split('!')[0].Trim()));
        foreach (var l in lines.Skip(1))
        {
            var cells = l.Split('|');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++) row[columns[i]] = i < cells.Length ? cells[i] : "";
            rows.Add(row);
        }
        return new CascTable(columns, rows);
    }
}

/// <summary>A build or CDN configuration file (<c>key = value</c> lines, <c>#</c> comments). The first occurrence of a
/// key wins.</summary>
public sealed class CascConfig
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CascConfig Load(string path) => Parse(CascTable.ReadText(path));

    public static CascConfig Parse(string text)
    {
        var c = new CascConfig();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            c._values.TryAdd(l[..eq].Trim(), l[(eq + 1)..].Trim());
        }
        return c;
    }

    /// <summary>The raw value, or null.</summary>
    public string? this[string key] => _values.GetValueOrDefault(key);

    /// <summary>The space-separated parts of a value (empty when the key is missing).</summary>
    public string[] Parts(string key) => this[key]?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>Where a configuration file named by its hash lives: <c>Data\config\ab\cd\abcd...</c>.</summary>
    public static string PathOf(string dataDir, string hash) =>
        Path.Combine(dataDir, "config", hash[..2].ToLowerInvariant(), hash[2..4].ToLowerInvariant(), hash.ToLowerInvariant());
}
