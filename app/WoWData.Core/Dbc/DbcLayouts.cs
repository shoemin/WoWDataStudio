using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WoWData.Core.Dbc;

public sealed class DbcLayoutException : Exception
{
    public DbcLayoutException(string message) : base(message) { }
}

/// <summary>
/// The DBC layouts of the 1.12.1 client (build 5875), read from the text file <c>Dbc\Layouts\dbc_layouts_5875.txt</c>
/// (embedded in the library). The file is meant to be read and extended by people: one section per table with its
/// category, plain-language description, what the server does with it, the evidence for the layout, and one line per
/// column:
/// <code>
///   Name[N]  [storage] [semantic] [-> Table] [=> server thing] [| hint]
/// </code>
/// storage: int, uint, float, string, loc (8 locales + flags = 9 fields), byte, ushort. semantic: id, bool, enum(Name),
/// flags(Name), flags, icon, texture, model, path. "Name[3]" makes Name1..Name3. A name starting with "~" is a best
/// guess (shown as not verified); "col_N" names are unknown columns. "@enum Name" / "@flags Name" blocks name values.
/// </summary>
public sealed class DbcLayouts
{
    public const string ResourceName = "WoWData.Core.Dbc.Layouts.dbc_layouts_5875.txt";

    private static readonly Lazy<DbcLayouts> Builtin = new(() => Parse(ReadResource()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The built-in layouts of the 1.12.1 client.</summary>
    public static DbcLayouts Default => Builtin.Value;

    private readonly Dictionary<string, DbcLayout> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DbcNamedValues> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DbcLayout> Tables => _tables.Values;
    public IReadOnlyDictionary<string, DbcNamedValues> NamedValues => _values;

    public DbcLayout? Get(string table) => _tables.GetValueOrDefault(table);

    /// <summary>The layout for a file: the known one when the file's shape matches it, else a raw fallback.</summary>
    public DbcLayout For(string table, int fieldCount, int recordSize)
    {
        var l = Get(table);
        if (l != null && l.FieldCount == fieldCount && l.RecordSize == recordSize) return l;
        string why = l == null
            ? $"{table}.dbc has no known 1.12.1 layout: every column is shown as a raw number (col_N)."
            : $"This {table}.dbc has {fieldCount} fields / {recordSize} bytes per row, but the 1.12.1 layout has {l.FieldCount} / {l.RecordSize}: " +
              "it is shown with raw columns (col_N). Was it made for another game version?";
        var fb = DbcLayout.Fallback(table, fieldCount, recordSize, why);
        return l == null ? fb : new DbcLayout
        {
            Name = fb.Name, Category = l.Category, About = why, ServerUse = l.ServerUse, ServerTable = l.ServerTable, ServerNote = l.ServerNote,
            Evidence = why, Columns = fb.Columns, FieldCount = fieldCount, RecordSize = recordSize, IsFallback = true,
        };
    }

    public static string ReadResource()
    {
        using var s = typeof(DbcLayouts).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new DbcLayoutException($"the layout resource {ResourceName} is missing from {typeof(DbcLayouts).Assembly.GetName().Name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static readonly Regex ColumnRe = new(@"^(?<name>~?[A-Za-z_][A-Za-z0-9_]*)(\[(?<count>\d+)\])?\s+(?<rest>.*)$", RegexOptions.Compiled);

    public static DbcLayouts Parse(string text)
    {
        var res = new DbcLayouts();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // pass 1: named values (they may be defined after the tables that use them)
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (!l.StartsWith("@enum ") && !l.StartsWith("@flags ")) continue;
            bool flags = l.StartsWith("@flags ");
            var name = l[(flags ? 7 : 6)..].Trim();
            var vals = new List<(long, string)>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                var v = lines[j];
                if (v.Length == 0 || !char.IsWhiteSpace(v[0]))
                {
                    if (v.TrimStart().StartsWith('#')) continue;
                    break;
                }
                var t = v.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                int eq = t.IndexOf('=');
                if (eq < 0) throw new DbcLayoutException($"line {j + 1}: expected 'value = name' in {name}");
                vals.Add((ParseNumber(t[..eq].Trim(), j), t[(eq + 1)..].Trim()));
            }
            res._values[name] = new DbcNamedValues(name, flags, vals);
        }
        // pass 2: tables
        string? table = null;
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cols = new List<(string Line, int No)>();
        void Flush()
        {
            if (table == null) return;
            res._tables[table] = res.BuildTable(table, meta, cols);
            meta.Clear();
            cols.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.StartsWith('@'))
            {
                Flush();
                table = null;
                continue;
            }
            if (raw.Length > 0 && char.IsWhiteSpace(raw[0]) && table == null) continue;   // value lines of @enum / @flags
            if (l.StartsWith('[') && l.EndsWith(']'))
            {
                Flush();
                table = l[1..^1].Trim();
                if (res._tables.ContainsKey(table)) throw new DbcLayoutException($"line {i + 1}: [{table}] is defined twice");
                continue;
            }
            if (table == null) throw new DbcLayoutException($"line {i + 1}: '{l}' is outside a [table] section");
            int colon = l.IndexOf(':');
            if (colon > 0 && Regex.IsMatch(l[..colon], @"^[a-z][a-z-]*$"))
            {
                meta[l[..colon]] = l[(colon + 1)..].Trim();
                continue;
            }
            cols.Add((l, i + 1));
        }
        Flush();
        // links must point at known tables
        foreach (var t in res._tables.Values)
            foreach (var c in t.Columns)
                if (c.ForeignKey != null && !res._tables.ContainsKey(c.ForeignKey))
                    throw new DbcLayoutException($"{t.Name}.{c.Name} links to an unknown table {c.ForeignKey}");
        return res;
    }

    private static long ParseNumber(string s, int line)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return d;
        throw new DbcLayoutException($"line {line + 1}: '{s}' is not a number");
    }

    private DbcLayout BuildTable(string table, Dictionary<string, string> meta, List<(string Line, int No)> lines)
    {
        var cols = new List<DbcColumn>();
        int field = 0, offset = 0;
        foreach (var (line, no) in lines)
        {
            string body = line, hint = "";
            int bar = line.IndexOf('|');
            if (bar >= 0)
            {
                body = line[..bar];
                hint = line[(bar + 1)..].Trim();
            }
            int hash = body.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) body = body[..hash];
            var m = ColumnRe.Match(body.Trim());
            if (!m.Success) throw new DbcLayoutException($"line {no}: cannot read the column '{line}'");
            var name = m.Groups["name"].Value;
            bool verified = !name.StartsWith('~') && !name.StartsWith("col_", StringComparison.Ordinal);
            name = name.TrimStart('~');
            int count = m.Groups["count"].Success ? int.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture) : 1;
            DbcStorage? storage = null;
            DbcSemantic sem = DbcSemantic.Plain;
            DbcNamedValues? values = null;
            string? fk = null, serverRef = null;
            var tokens = m.Groups["rest"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int k = 0; k < tokens.Length; k++)
            {
                var t = tokens[k];
                if (t == "->" && k + 1 < tokens.Length) { fk = tokens[++k]; continue; }
                if (t == "=>" && k + 1 < tokens.Length) { serverRef = tokens[++k]; continue; }
                var tm = Regex.Match(t, @"^(enum|flags)\((\w+)\)$");
                if (tm.Success)
                {
                    sem = tm.Groups[1].Value == "enum" ? (tm.Groups[2].Value == "Bool" ? DbcSemantic.Bool : DbcSemantic.Enum) : DbcSemantic.Flags;
                    values = _values.GetValueOrDefault(tm.Groups[2].Value) ?? throw new DbcLayoutException($"line {no}: unknown @{tm.Groups[1].Value} {tm.Groups[2].Value}");
                    continue;
                }
                switch (t)
                {
                    case "int": storage = DbcStorage.Int32; break;
                    case "uint": storage = DbcStorage.UInt32; break;
                    case "float": storage = DbcStorage.Float; break;
                    case "string": storage = DbcStorage.String; break;
                    case "loc": storage = DbcStorage.Loc; break;
                    case "byte": storage = DbcStorage.UInt8; break;
                    case "ushort": storage = DbcStorage.UInt16; break;
                    case "id": sem = DbcSemantic.Id; break;
                    case "bool":
                        sem = DbcSemantic.Bool;
                        values = _values.GetValueOrDefault("Bool");
                        break;
                    case "flags": sem = DbcSemantic.Flags; break;
                    case "icon": sem = DbcSemantic.Icon; break;
                    case "texture": sem = DbcSemantic.Texture; break;
                    case "model": sem = DbcSemantic.Model; break;
                    case "path": sem = DbcSemantic.Path; break;
                    default: throw new DbcLayoutException($"line {no}: unknown type word '{t}' in '{line}'");
                }
            }
            storage ??= sem switch
            {
                DbcSemantic.Enum => DbcStorage.Int32,
                DbcSemantic.Icon or DbcSemantic.Texture or DbcSemantic.Model or DbcSemantic.Path => DbcStorage.String,
                _ => DbcStorage.UInt32,
            };
            if (storage is DbcStorage.String or DbcStorage.Loc && sem is not (DbcSemantic.Plain or DbcSemantic.Icon or DbcSemantic.Texture or DbcSemantic.Model or DbcSemantic.Path))
                throw new DbcLayoutException($"line {no}: a text column cannot be '{sem}'");
            int size = storage switch { DbcStorage.UInt8 => 1, DbcStorage.UInt16 => 2, _ => 4 };
            if (!verified && hint.Length == 0) hint = "Unknown column (not verified): shown as stored.";
            for (int i = 0; i < count; i++)
            {
                var c = new DbcColumn
                {
                    Name = count == 1 ? name : name + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Index = cols.Count, Field = field, Offset = offset, Size = size, Storage = storage.Value, Semantic = sem, Values = values,
                    ForeignKey = fk, ServerRef = serverRef, Hint = hint, Verified = verified,
                };
                if (cols.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new DbcLayoutException($"line {no}: {table} has two columns named {c.Name}");
                cols.Add(c);
                field += c.Fields;
                offset += size * c.Fields;
            }
        }
        var server = meta.GetValueOrDefault("server") ?? "client";
        DbcServerUse use;
        string? serverTable = null;
        if (server == "client") use = DbcServerUse.ClientOnly;
        else if (server == "file") use = DbcServerUse.ServerFile;
        else if (server.StartsWith("db ", StringComparison.Ordinal))
        {
            use = DbcServerUse.ServerDatabase;
            serverTable = server[3..].Trim();
        }
        else throw new DbcLayoutException($"[{table}]: server must be client, file or db <table>, not '{server}'");
        DbcColumn? label = null;
        if (meta.TryGetValue("label", out var ln))
            label = cols.FirstOrDefault(c => string.Equals(c.Name, ln, StringComparison.OrdinalIgnoreCase)) ?? throw new DbcLayoutException($"[{table}]: label column {ln} does not exist");
        else label = cols.FirstOrDefault(c => c.IsString && c.Verified);
        if (cols.Count(c => c.IsKey) > 1) throw new DbcLayoutException($"[{table}] has more than one id column");
        return new DbcLayout
        {
            Name = table, Category = meta.GetValueOrDefault("category") is { Length: > 0 } cat ? cat : "Other",
            About = meta.GetValueOrDefault("about") ?? "", ServerUse = use, ServerTable = serverTable, ServerNote = meta.GetValueOrDefault("server-note") ?? "",
            Evidence = meta.GetValueOrDefault("evidence") ?? "", Columns = cols, FieldCount = field, RecordSize = offset, LabelColumn = label,
        };
    }
}
