using System.Globalization;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;

namespace WoWData.Core.Db2;

/// <summary>
/// Column names for DB2 tables of one client build, from a text file in the same format as the DBC layouts
/// (<c>Dbc\Layouts\dbc_layouts_5875.txt</c>; parsed by <see cref="DbcLayouts.Parse"/>), with two extra keys per
/// section: <c>layout-hash:</c> (the file header's layout hash the names were measured on; a table with another hash
/// keeps its raw col_N names) and <c>fields:</c> (informational). One column line per <b>flattened</b> column of
/// <see cref="Db2Table.Columns"/> (an array field of N elements is N columns: write <c>Name[N]</c>); the record ID and
/// the parent ID are not columns unless the file stores the ID inside the record. Storage words are informational
/// (the types come from the client's field table, <see cref="Db2Catalog"/>) except that <c>string</c> and
/// <c>float</c> must agree with the table's types.
/// <para>
/// The built-in files are <c>Db2\Layouts\db2_layouts_&lt;build&gt;.txt</c>, read as the embedded resource
/// <c>WoWData.Core.Db2.Layouts.db2_layouts_&lt;build&gt;.txt</c> (<see cref="ForBuild"/>). <see cref="Load"/> reads one from disk.
/// </para>
/// </summary>
public sealed class Db2Layouts
{
    public const string ResourcePrefix = "WoWData.Core.Db2.Layouts.db2_layouts_";

    private readonly Dictionary<string, uint> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, Db2Layouts?> Cache = new();

    private Db2Layouts(DbcLayouts inner) => Inner = inner;

    /// <summary>The parsed sections (names, types, links, hints, evidence) in the DBC layout model.</summary>
    public DbcLayouts Inner { get; }
    public int Build => Inner.Build;
    public string Version => Inner.Version;
    public IReadOnlyCollection<DbcLayout> Tables => Inner.Tables;
    public DbcLayout? Get(string table) => Inner.Get(table);

    /// <summary>The layout hash a table's names were measured on, or null when the section does not say.</summary>
    public uint? LayoutHash(string table) => _hashes.TryGetValue(table, out var h) ? h : null;

    /// <summary>The built-in layouts of a build (embedded resource), or null when there are none.</summary>
    public static Db2Layouts? ForBuild(int build)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(build, out var l)) return l;
            using var s = typeof(Db2Layouts).Assembly.GetManifestResourceStream(ResourcePrefix + build.ToString(CultureInfo.InvariantCulture) + ".txt");
            if (s != null)
            {
                using var r = new StreamReader(s);
                l = Parse(r.ReadToEnd());
            }
            return Cache[build] = l;
        }
    }

    /// <summary>The builds with an embedded layout file.</summary>
    public static IReadOnlyList<int> KnownBuilds() =>
        typeof(Db2Layouts).Assembly.GetManifestResourceNames()
            .Select(n => Regex.Match(n, @"\.db2_layouts_(\d+)\.txt$")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).OrderBy(b => b).ToList();

    public static Db2Layouts Load(string path) => Parse(File.ReadAllText(path));

    private static readonly Regex Section = new(@"^\[(?<t>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex HashLine = new(@"^layout-hash:\s*(?:0x)?(?<h>[0-9A-Fa-f]{1,8})\s*$", RegexOptions.Compiled);

    public static Db2Layouts Parse(string text)
    {
        var res = new Db2Layouts(DbcLayouts.Parse(text));
        string? table = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.Trim();
            var m = Section.Match(l);
            if (m.Success) { table = m.Groups["t"].Value.Trim(); continue; }
            if (l.StartsWith('@')) { table = null; continue; }
            if (table == null) continue;
            var h = HashLine.Match(l);
            if (h.Success) res._hashes[table] = uint.Parse(h.Groups["h"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        foreach (var t in res.Inner.Tables)
            if (t.Columns.Any(c => c.Storage == DbcStorage.Loc))
                throw new DbcLayoutException($"[{t.Name}]: DB2 strings are not localized per row: use 'string', not 'loc'");
        return res;
    }

    /// <summary>
    /// Names the columns of a table read with <see cref="Db2Table.Read(byte[], Db2ReadOptions?)"/> when this file
    /// has a section for it, the layout hash matches (when the section gives one), the column count matches and the
    /// string/float columns agree. Returns why not otherwise (the table keeps col_N).
    /// </summary>
    public bool Apply(Db2Table table, out string reason)
    {
        var l = Get(table.Name);
        if (l == null) { reason = $"no {Version} layout for {table.Name}"; return false; }
        if (LayoutHash(table.Name) is { } h && table.Format >= Db2Format.Wdc3 && h != table.LayoutHash)
        {
            reason = $"{table.Name} has layout hash {table.LayoutHash:X8}, the {Version} layout was measured on {h:X8}";
            return false;
        }
        int cols = l.Columns.Sum(c => c.Fields);
        if (cols != table.Columns.Count) { reason = $"{table.Name} has {table.Columns.Count} columns, the layout {cols}"; return false; }
        foreach (var c in l.Columns)
        {
            var tc = table.Columns[c.Field];
            if (tc.TypeKnown && (c.IsString != (tc.Type == Db2ValueType.String) || c.IsFloat != (tc.Type == Db2ValueType.Float)))
            {
                reason = $"{table.Name}.{c.Name} is {c.Storage} in the layout but {tc.Type} in the file";
                return false;
            }
        }
        foreach (var c in l.Columns)
        {
            var tc = table.Columns[c.Field];
            tc.Name = c.Name;
            tc.NameVerified = c.Verified;
            if (!tc.TypeKnown)
            {
                if (c.IsString) tc.Type = Db2ValueType.String;
                else if (c.IsFloat) tc.Type = Db2ValueType.Float;
                else if (c.IsSigned && tc.Type == Db2ValueType.UInt32) tc.Type = Db2ValueType.Int32;
            }
        }
        table.Layout = l;
        table.RenameColumns();
        reason = "";
        return true;
    }
}
