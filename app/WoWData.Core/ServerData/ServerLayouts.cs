using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;

namespace WoWData.Core.ServerData;

/// <summary>How the rows of a server table are versioned (VMaNGOS keeps several versions of a row per content patch or
/// client build; the server loads one of them).</summary>
public enum ServerVersioning
{
    /// <summary>No versions: every row is loaded.</summary>
    None,
    /// <summary>One row per entry and `patch`: the server uses the highest patch &lt;= its WowPatch.</summary>
    Patch,
    /// <summary>One row per entry and `build`: the server uses the highest build &lt;= 5875.</summary>
    Build,
    /// <summary>Only the rows of build 5875 are loaded.</summary>
    BuildExact,
    /// <summary>`patch_min`..`patch_max`: loaded when the server's patch is in the range.</summary>
    PatchRange,
    /// <summary>`build_min`..`build_max`: loaded when 5875 is in the range.</summary>
    BuildRange,
}

/// <summary>What a column's number means (beyond its SQL type).</summary>
public enum ServerSemantic { Plain, Id, Patch, Text, Bool, Enum, Flags, Money, Ms, Seconds, Percent, Coords }

/// <summary>How a value links: as it is, its absolute value, only negative values (as -value), or negative values to
/// another kind.</summary>
public enum ServerLinkMode { Plain, Abs, Neg, Sign }

/// <summary>An object other columns link to: "item" = item_template.entry, named by its name column ("*": a list of rows,
/// named by how many rows it has: a loot table, a vendor template...).</summary>
public sealed record ServerKind(string Name, string Table, string KeyColumn, string LabelColumn, string Plain)
{
    public bool IsList => LabelColumn == "*";
}

/// <summary>A link of a column: to a server object kind ("item") or to a client table ("dbc:ItemDisplayInfo").</summary>
public sealed record ServerLink(string Target, ServerLinkMode Mode = ServerLinkMode.Plain, string? NegativeTarget = null, bool ZeroIsValue = false)
{
    public bool IsDbc => Target.StartsWith("dbc:", StringComparison.Ordinal);
    public string? DbcTable => IsDbc ? Target[4..] : null;

    /// <summary>The target and the linked ID of a value (null: the value links to nothing, e.g. 0).</summary>
    public (string Target, long Id)? Resolve(long v)
    {
        switch (Mode)
        {
            case ServerLinkMode.Abs:
                return v == 0 ? null : (Target, Math.Abs(v));
            case ServerLinkMode.Neg:
                return v < 0 ? (Target, -v) : null;
            case ServerLinkMode.Sign:
                return v == 0 ? null : v > 0 ? (Target, v) : (NegativeTarget ?? Target, -v);
            default:
                // 0 (and -1) mean "none", except where 0 is a real row ("zero": map 0 is Eastern Kingdoms)
                return v == -1 || v == 0 && !ZeroIsValue ? null : (Target, v);
        }
    }

    public override string ToString() => Mode switch
    {
        ServerLinkMode.Abs => Target + " (either sign)",
        ServerLinkMode.Neg => Target + " (negative values)",
        ServerLinkMode.Sign => $"{Target} (negative: {NegativeTarget})",
        _ => Target,
    };
}

/// <summary>What WoW Data Studio knows about one column of a server table (its SQL type comes from the live schema).</summary>
public sealed class ServerColumnInfo
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public ServerSemantic Semantic { get; init; }
    public DbcNamedValues? Values { get; init; }
    public ServerLink? Link { get; init; }
    public string Hint { get; init; } = "";
    /// <summary>False: the meaning is a best guess ("~" in the layout file).</summary>
    public bool Verified { get; init; } = true;
}

/// <summary>What WoW Data Studio knows about one server table.</summary>
public sealed class ServerTableInfo
{
    public string Name { get; init; } = "";
    public string View { get; init; } = "";
    public string About { get; init; } = "";
    public string? Label { get; init; }
    public ServerVersioning Versioning { get; init; }
    /// <summary>".reload item_template" or "restart".</summary>
    public string Reload { get; init; } = "restart";
    public List<ServerColumnInfo> Columns { get; } = new();
    private Dictionary<string, ServerColumnInfo>? _by;

    public ServerColumnInfo? Column(string name)
    {
        _by ??= Columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _by.GetValueOrDefault(name);
    }

    public bool CanReload => Reload.StartsWith(".reload", StringComparison.Ordinal);

    public string VersioningText => Versioning switch
    {
        ServerVersioning.Patch => "one row per content patch (the server uses the highest patch up to its own)",
        ServerVersioning.Build => "one row per client build (the server uses the highest build up to 5875)",
        ServerVersioning.BuildExact => "only the rows of build 5875 are loaded",
        ServerVersioning.PatchRange => "each row has a patch range (patch_min to patch_max)",
        ServerVersioning.BuildRange => "each row has a build range (build_min to build_max)",
        _ => "every row is loaded",
    };
}

/// <summary>
/// 0.9.0: the meaning of the VMaNGOS world database's tables and columns (an embedded text file, like the DBC layouts):
/// the objects other columns link to (@kind), named values (@enum / @flags, plus those of the DBC layouts), and per table
/// what it is for, how its rows are versioned (patch / build), how the server reloads it, and per column a group, a
/// meaning (choices, flags, money, times...), a link and a hint. The column names and types always come from the live
/// database (<see cref="WorldSchema"/>); a column that is not described here is shown raw.
/// </summary>
public sealed class ServerLayouts
{
    public const string ResourceName = "WoWData.Core.ServerData.Layouts.server_layouts_vmangos.txt";

    private static readonly Lazy<ServerLayouts> Builtin = new(() => Parse(ReadResource(), DbcLayouts.Default));

    public static ServerLayouts Default => Builtin.Value;

    private readonly Dictionary<string, ServerTableInfo> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerKind> _kinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DbcNamedValues> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ServerTableInfo> Tables => _tables.Values;
    public IReadOnlyCollection<ServerKind> Kinds => _kinds.Values;
    public IReadOnlyDictionary<string, DbcNamedValues> NamedValues => _values;

    public ServerTableInfo? Table(string name) => _tables.GetValueOrDefault(name);
    public ServerKind? Kind(string name) => _kinds.GetValueOrDefault(name);

    /// <summary>The kind whose objects live in this table (item for item_template).</summary>
    public ServerKind? KindOfTable(string table) => _kinds.Values.FirstOrDefault(k => string.Equals(k.Table, table, StringComparison.OrdinalIgnoreCase));

    public static string ReadResource()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidOperationException("the server layout resource is missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static readonly Regex KindLine = new(@"^@kind\s+(\w+)\s*=\s*(\w+)\.(\w+)\s+(\w+|\*)\s*(?:\|\s*(.*))?$");
    private static readonly Regex ValueLine = new(@"^\s+(-?(?:0x[0-9A-Fa-f]+|\d+))\s*=\s*(.+)$");

    /// <summary>Parse the layout text. <paramref name="dbc"/> provides the named values of the DBC layouts.</summary>
    public static ServerLayouts Parse(string text, DbcLayouts? dbc = null)
    {
        var res = new ServerLayouts();
        if (dbc != null)
            foreach (var (k, v) in dbc.NamedValues) res._values[k] = v;
        var lines = text.Replace("\r", "").Split('\n');
        ServerTableInfo? table = null;
        string group = "";
        string? valuesName = null;
        bool valuesFlags = false;
        var values = new List<(long, string)>();
        var props = new Dictionary<string, string>();
        var pendingColumns = new List<(string Line, string Group)>();

        void FlushValues()
        {
            if (valuesName != null) res._values[valuesName] = new DbcNamedValues(valuesName, valuesFlags, values.ToList());
            valuesName = null;
            values.Clear();
        }

        void FlushTable()
        {
            if (table == null) return;
            var t = new ServerTableInfo
            {
                Name = table.Name,
                View = props.GetValueOrDefault("view", ""),
                About = props.GetValueOrDefault("about", ""),
                Label = props.GetValueOrDefault("label"),
                Versioning = props.GetValueOrDefault("version", "none") switch
                {
                    "patch" => ServerVersioning.Patch,
                    "build" => ServerVersioning.Build,
                    "build=" => ServerVersioning.BuildExact,
                    "patch-range" => ServerVersioning.PatchRange,
                    "build-range" => ServerVersioning.BuildRange,
                    "none" => ServerVersioning.None,
                    var x => throw new FormatException($"[{table.Name}]: unknown version \"{x}\""),
                },
                Reload = props.GetValueOrDefault("reload", "restart"),
            };
            foreach (var (line, g) in pendingColumns) t.Columns.AddRange(ParseColumn(res, t.Name, line, g));
            res._tables[t.Name] = t;
            table = null;
            props.Clear();
            pendingColumns.Clear();
        }

        for (int n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (valuesName != null && ValueLine.Match(line) is { Success: true } vm)
            {
                var vs = vm.Groups[1].Value;
                long v = vs.Contains("0x", StringComparison.OrdinalIgnoreCase)
                    ? (vs.StartsWith('-') ? -long.Parse(vs[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(vs[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    : long.Parse(vs, CultureInfo.InvariantCulture);
                values.Add((v, vm.Groups[2].Value.Trim()));
                continue;
            }
            FlushValues();
            if (line.StartsWith("@kind", StringComparison.Ordinal))
            {
                var m = KindLine.Match(line);
                if (!m.Success) throw new FormatException($"line {n + 1}: bad @kind line: {line}");
                res._kinds[m.Groups[1].Value] = new ServerKind(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value,
                    m.Groups[5].Success && m.Groups[5].Value.Trim().Length > 0 ? m.Groups[5].Value.Trim() : m.Groups[1].Value);
                continue;
            }
            if (line.StartsWith("@enum ", StringComparison.Ordinal) || line.StartsWith("@flags ", StringComparison.Ordinal))
            {
                FlushTable();
                valuesFlags = line.StartsWith("@flags", StringComparison.Ordinal);
                valuesName = line[(line.IndexOf(' ') + 1)..].Trim();
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                FlushTable();
                table = new ServerTableInfo { Name = line[1..^1].Trim() };
                group = "";
                continue;
            }
            if (table == null) throw new FormatException($"line {n + 1}: text outside a table: {line}");
            if (line.StartsWith("== ", StringComparison.Ordinal))
            {
                group = line[3..].Trim();
                continue;
            }
            var pm = Regex.Match(line, @"^(view|about|label|version|reload|evidence):\s*(.*)$");
            if (pm.Success)
            {
                props[pm.Groups[1].Value] = pm.Groups[2].Value.Trim();
                continue;
            }
            pendingColumns.Add((line, group));
        }
        FlushValues();
        FlushTable();
        // every link names a known kind
        foreach (var t in res._tables.Values)
            foreach (var c in t.Columns.Where(c => c.Link != null))
                foreach (var target in new[] { c.Link!.Target, c.Link.NegativeTarget }.Where(x => x != null && !x.StartsWith("dbc:", StringComparison.Ordinal)))
                    if (!res._kinds.ContainsKey(target!)) throw new FormatException($"[{t.Name}] {c.Name}: links to unknown kind \"{target}\"");
        return res;
    }

    private static IEnumerable<ServerColumnInfo> ParseColumn(ServerLayouts res, string table, string line, string group)
    {
        string hint = "";
        int bar = line.IndexOf('|');
        if (bar >= 0)
        {
            hint = line[(bar + 1)..].Trim();
            line = line[..bar];
        }
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var name = tokens[0];
        bool verified = true;
        if (name.StartsWith('~'))
        {
            verified = false;
            name = name[1..];
        }
        var sem = ServerSemantic.Plain;
        DbcNamedValues? values = null;
        ServerLink? link = null;
        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "->")
            {
                if (i + 1 >= tokens.Count) throw new FormatException($"[{table}] {name}: -> without a target");
                var target = tokens[++i];
                var mode = ServerLinkMode.Plain;
                string? neg = null;
                bool zero = false;
                if (i + 1 < tokens.Count && tokens[i + 1] == "zero")
                {
                    zero = true;
                    i++;
                }
                else if (i + 1 < tokens.Count && tokens[i + 1] is "abs" or "neg")
                {
                    mode = tokens[++i] == "abs" ? ServerLinkMode.Abs : ServerLinkMode.Neg;
                }
                else if (i + 1 < tokens.Count && tokens[i + 1].StartsWith("sign(", StringComparison.Ordinal))
                {
                    mode = ServerLinkMode.Sign;
                    neg = tokens[++i][5..^1];
                }
                link = new ServerLink(target, mode, neg, zero);
                continue;
            }
            var m = Regex.Match(t, @"^(enum|flags)(?:\((\w+)\))?$");
            if (m.Success)
            {
                sem = m.Groups[1].Value == "enum" ? ServerSemantic.Enum : ServerSemantic.Flags;
                if (m.Groups[2].Success)
                    values = res._values.GetValueOrDefault(m.Groups[2].Value) ?? throw new FormatException($"[{table}] {name}: unknown named values {m.Groups[2].Value}");
                continue;
            }
            sem = t switch
            {
                "id" => ServerSemantic.Id,
                "patch" => ServerSemantic.Patch,
                "text" => ServerSemantic.Text,
                "bool" => ServerSemantic.Bool,
                "money" => ServerSemantic.Money,
                "ms" => ServerSemantic.Ms,
                "seconds" => ServerSemantic.Seconds,
                "percent" => ServerSemantic.Percent,
                "coords" => ServerSemantic.Coords,
                _ => throw new FormatException($"[{table}] {name}: unknown word \"{t}\""),
            };
        }
        if (sem == ServerSemantic.Bool) values ??= res._values.GetValueOrDefault("Bool");
        // Name[N] -> Name1..NameN; Name_[N] -> Name_1..Name_N
        var arr = Regex.Match(name, @"^(\w+?)\[(\d+)\]$");
        var names = arr.Success ? Enumerable.Range(1, int.Parse(arr.Groups[2].Value, CultureInfo.InvariantCulture)).Select(k => arr.Groups[1].Value + k).ToList() : new List<string> { name };
        foreach (var n in names)
            yield return new ServerColumnInfo { Name = n, Group = group, Semantic = sem, Values = values, Link = link, Hint = hint, Verified = verified };
    }
}
