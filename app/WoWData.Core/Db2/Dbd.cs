using System.Globalization;

namespace WoWData.Core.Db2;

/// <summary>A <c>.dbd</c> text that cannot be read (with its line number).</summary>
public sealed class DbdFormatException : Exception
{
    public DbdFormatException(string message) : base(message) { }
}

/// <summary>A client version as WoWDBDefs writes it: major.minor.patch.build (1.60.1.70450). Ordered field by field.</summary>
public readonly record struct DbdBuild(int Major, int Minor, int Patch, int Build) : IComparable<DbdBuild>
{
    public int CompareTo(DbdBuild o)
    {
        int c = Major.CompareTo(o.Major);
        if (c == 0) c = Minor.CompareTo(o.Minor);
        if (c == 0) c = Patch.CompareTo(o.Patch);
        return c == 0 ? Build.CompareTo(o.Build) : c;
    }

    public static bool operator <(DbdBuild a, DbdBuild b) => a.CompareTo(b) < 0;
    public static bool operator >(DbdBuild a, DbdBuild b) => a.CompareTo(b) > 0;
    public static bool operator <=(DbdBuild a, DbdBuild b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DbdBuild a, DbdBuild b) => a.CompareTo(b) >= 0;

    /// <summary>"1.60.1.70450" (a letter after the patch, as in "3.3.5a.12340", is ignored).</summary>
    public static bool TryParse(string? text, out DbdBuild build)
    {
        build = default;
        var p = (text ?? "").Trim().Split('.');
        if (p.Length != 4) return false;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            var s = p[i].TrimEnd('a', 'b', 'c', 'd', 'e', 'f');
            if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        }
        build = new DbdBuild(n[0], n[1], n[2], n[3]);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
}

/// <summary>A column of the COLUMNS section: its type (int, float, string, locstring), the table it points to
/// (<c>int&lt;Map::ID&gt;</c>), whether its name is confirmed (no <c>?</c>), and the comment after <c>//</c>.</summary>
public sealed record DbdColumn(string Name, string Type, string? ForeignTable, string? ForeignColumn, bool Verified, string Comment)
{
    public bool IsString => Type is "string" or "locstring";
    public bool IsFloat => Type == "float";
}

/// <summary>
/// A field of one definition: its name (a column of the COLUMNS section), <c>$id$</c> / <c>$relation$</c> /
/// <c>$noninline$</c>, the integer size (<c>&lt;8&gt;</c> signed, <c>&lt;u8&gt;</c> unsigned; 0 = not given) and the array
/// length (<c>[2]</c>; 0 = not an array). A non-inline field (the ID list, the relationship map) is not in the record.
/// </summary>
public sealed record DbdField(string Name, bool IsId, bool IsRelation, bool NonInline, int Size, bool Unsigned, int ArrayLength, string Comment)
{
    /// <summary>The flattened columns this field gives in the record (an array of N elements is N columns).</summary>
    public int Columns => NonInline ? 0 : Math.Max(1, ArrayLength);
}

/// <summary>One definition block of a <c>.dbd</c> file: the layout hashes and builds it is for, and its fields in record order.</summary>
public sealed class DbdDefinition
{
    public List<uint> LayoutHashes { get; } = new();
    public List<DbdBuild> Builds { get; } = new();
    public List<(DbdBuild From, DbdBuild To)> Ranges { get; } = new();
    public List<DbdField> Fields { get; } = new();
    public string Comment { get; internal set; } = "";
    /// <summary>The line of the block's first line in the file (messages).</summary>
    public int Line { get; internal init; }

    public bool HasBuild(DbdBuild b) => Builds.Contains(b) || Ranges.Any(r => b >= r.From && b <= r.To);
    public bool HasLayout(uint hash) => LayoutHashes.Contains(hash);

    /// <summary>The fields stored in the record, in order (what <see cref="Db2Table.Columns"/> holds, flattened).</summary>
    public IEnumerable<DbdField> InlineFields => Fields.Where(f => !f.NonInline);

    /// <summary>The number of flattened columns in the record.</summary>
    public int ColumnCount => Fields.Sum(f => f.Columns);

    public override string ToString() =>
        (LayoutHashes.Count > 0 ? "LAYOUT " + string.Join(", ", LayoutHashes.Select(h => h.ToString("X8", CultureInfo.InvariantCulture))) + "; " : "") +
        (Builds.Count + Ranges.Count > 0 ? $"{Builds.Count} builds, {Ranges.Count} ranges" : "no build") + $" (line {Line})";
}

/// <summary>
/// One WoWDBDefs definition file (<c>&lt;Table&gt;.dbd</c>), read with our own parser written from the format: a
/// <c>COLUMNS</c> section (one line per column: type, optional <c>&lt;Table::Column&gt;</c> foreign key, name, optional
/// <c>?</c> when not confirmed, optional <c>// comment</c>), then definition blocks separated by empty lines, each made
/// of <c>LAYOUT</c> lines (layout hashes), <c>BUILD</c> lines (versions, comma-separated, or a range <c>a-b</c>), an
/// optional <c>COMMENT</c>, and one line per field (<c>$id$</c>, <c>$relation$</c>, <c>$noninline,...$</c>
/// annotations, the name, an optional <c>&lt;size&gt;</c> / <c>&lt;usize&gt;</c>, an optional <c>[N]</c>, an optional
/// <c>// comment</c>).
/// <para>The files are read at run time from a folder the user chose (setting other-clients.dbd); Data Studio never
/// downloads, bundles or converts them (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, section 4).</para>
/// </summary>
public sealed class DbdFile
{
    private readonly Dictionary<string, DbdColumn> _columns = new(StringComparer.OrdinalIgnoreCase);

    public string Table { get; }
    public IReadOnlyDictionary<string, DbdColumn> Columns => _columns;
    public List<DbdDefinition> Definitions { get; } = new();

    private DbdFile(string table) => Table = table;

    public static DbdFile Load(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>Reads the text of a <c>.dbd</c> file; throws <see cref="DbdFormatException"/> with the line on a line it cannot read.</summary>
    public static DbdFile Parse(string text, string table)
    {
        var f = new DbdFile(table);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        // the COLUMNS section (the first non-empty line)
        while (i < lines.Length && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Length || lines[i].Trim() != "COLUMNS") throw new DbdFormatException($"{table}.dbd line {i + 1}: the file must start with COLUMNS");
        i++;
        for (; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) break;
            f.AddColumn(l, i + 1);
        }
        // the definitions
        DbdDefinition? d = null;
        bool inFields = false;
        for (; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0)
            {
                d = null;
                inFields = false;
                continue;
            }
            if (d == null)
            {
                d = new DbdDefinition { Line = i + 1 };
                f.Definitions.Add(d);
            }
            if (!inFields && l.StartsWith("LAYOUT ", StringComparison.Ordinal))
            {
                foreach (var h in Items(l[7..]))
                    d.LayoutHashes.Add(uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : throw new DbdFormatException($"{table}.dbd line {i + 1}: \"{h}\" is not a layout hash"));
                continue;
            }
            if (!inFields && l.StartsWith("BUILD ", StringComparison.Ordinal))
            {
                foreach (var b in Items(l[6..]))
                {
                    int dash = b.IndexOf('-');
                    if (dash > 0)
                    {
                        if (!DbdBuild.TryParse(b[..dash], out var from) || !DbdBuild.TryParse(b[(dash + 1)..], out var to))
                            throw new DbdFormatException($"{table}.dbd line {i + 1}: \"{b}\" is not a build range");
                        d.Ranges.Add((from, to));
                    }
                    else d.Builds.Add(DbdBuild.TryParse(b, out var one) ? one : throw new DbdFormatException($"{table}.dbd line {i + 1}: \"{b}\" is not a build"));
                }
                continue;
            }
            if (!inFields && l.StartsWith("COMMENT ", StringComparison.Ordinal))
            {
                d.Comment = (d.Comment.Length > 0 ? d.Comment + " " : "") + l[8..].Trim();
                continue;
            }
            inFields = true;
            d.Fields.Add(f.ParseField(l, i + 1));
        }
        return f;
    }

    private static IEnumerable<string> Items(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Body, string Comment) SplitComment(string line)
    {
        int c = line.IndexOf("//", StringComparison.Ordinal);
        return c < 0 ? (line.Trim(), "") : (line[..c].Trim(), line[(c + 2)..].Trim());
    }

    private void AddColumn(string line, int no)
    {
        var (body, comment) = SplitComment(line);
        int sp = body.IndexOf(' ');
        if (sp <= 0) throw new DbdFormatException($"{Table}.dbd line {no}: a column is \"type name\", not \"{line}\"");
        var type = body[..sp].Trim();
        var name = body[(sp + 1)..].Trim();
        string? ft = null, fc = null;
        int lt = type.IndexOf('<');
        if (lt >= 0)
        {
            if (!type.EndsWith('>')) throw new DbdFormatException($"{Table}.dbd line {no}: \"{type}\" is not type<Table::Column>");
            var fk = type[(lt + 1)..^1];
            int cc = fk.IndexOf("::", StringComparison.Ordinal);
            if (cc <= 0) throw new DbdFormatException($"{Table}.dbd line {no}: \"{type}\" is not type<Table::Column>");
            ft = fk[..cc];
            fc = fk[(cc + 2)..];
            type = type[..lt];
        }
        if (type is not ("int" or "float" or "string" or "locstring"))
            throw new DbdFormatException($"{Table}.dbd line {no}: unknown column type \"{type}\" (int, float, string or locstring)");
        bool verified = !name.EndsWith('?');
        name = name.TrimEnd('?');
        if (name.Length == 0 || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
            throw new DbdFormatException($"{Table}.dbd line {no}: \"{name}\" is not a column name");
        _columns[name] = new DbdColumn(name, type, ft, fc, verified, comment);
    }

    private DbdField ParseField(string line, int no)
    {
        var (body, comment) = SplitComment(line);
        bool id = false, relation = false, noninline = false;
        if (body.StartsWith('$'))
        {
            int end = body.IndexOf('$', 1);
            if (end < 0) throw new DbdFormatException($"{Table}.dbd line {no}: the annotation of \"{line}\" is not closed with $");
            foreach (var a in body[1..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (a)
                {
                    case "id": id = true; break;
                    case "relation": relation = true; break;
                    case "noninline": noninline = true; break;
                    // other annotations do not change where the field is: ignored
                }
            }
            body = body[(end + 1)..].Trim();
        }
        int array = 0;
        if (body.EndsWith(']'))
        {
            int lb = body.LastIndexOf('[');
            if (lb < 0 || !int.TryParse(body[(lb + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out array) || array < 1)
                throw new DbdFormatException($"{Table}.dbd line {no}: \"{line}\" has an array length that is not a number");
            body = body[..lb].Trim();
        }
        int size = 0;
        bool unsigned = false;
        if (body.EndsWith('>'))
        {
            int lt = body.LastIndexOf('<');
            var s = lt < 0 ? "" : body[(lt + 1)..^1];
            unsigned = s.StartsWith('u');
            if (lt < 0 || !int.TryParse(unsigned ? s[1..] : s, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is not (8 or 16 or 32 or 64))
                throw new DbdFormatException($"{Table}.dbd line {no}: \"{line}\" has a size that is not 8, 16, 32 or 64 (u for unsigned)");
            body = body[..lt].Trim();
        }
        var name = body.TrimEnd('?');
        if (name.Length == 0 || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
            throw new DbdFormatException($"{Table}.dbd line {no}: \"{line}\" is not a field");
        if (!_columns.ContainsKey(name)) throw new DbdFormatException($"{Table}.dbd line {no}: the field {name} is not in COLUMNS");
        return new DbdField(_columns[name].Name, id, relation, noninline, size, unsigned, array, comment);
    }

    /// <summary>How <see cref="Find"/> chose a definition.</summary>
    public enum MatchKind { None, BuildAndLayout, Build, Layout }

    /// <summary>
    /// The definition of a table file: the one that lists <paramref name="build"/> (and, when the file has one,
    /// <paramref name="layoutHash"/> among its LAYOUT hashes); else, when no definition lists the build, the one with
    /// that layout hash. <paramref name="why"/> says why none fits (the build is listed with another layout hash, or
    /// neither the build nor the hash is there).
    /// </summary>
    public DbdDefinition? Find(DbdBuild? build, uint? layoutHash, out MatchKind kind, out string why)
    {
        kind = MatchKind.None;
        why = "";
        var byBuild = build is { } b ? Definitions.Where(d => d.HasBuild(b)).ToList() : new List<DbdDefinition>();
        if (byBuild.Count > 0)
        {
            if (layoutHash is not { } h)
            {
                kind = MatchKind.Build;
                return byBuild[0];
            }
            var same = byBuild.FirstOrDefault(d => d.HasLayout(h));
            if (same != null)
            {
                kind = MatchKind.BuildAndLayout;
                return same;
            }
            var noHash = byBuild.FirstOrDefault(d => d.LayoutHashes.Count == 0);
            if (noHash != null)
            {
                kind = MatchKind.Build;
                return noHash;
            }
            why = $"{Table}.dbd's definition for {build} is for layout {string.Join(", ", byBuild.SelectMany(d => d.LayoutHashes).Distinct().Select(x => x.ToString("X8", CultureInfo.InvariantCulture)))}, the file has layout {h:X8}";
            return null;
        }
        if (layoutHash is { } lh)
        {
            var byLayout = Definitions.FirstOrDefault(d => d.HasLayout(lh));
            if (byLayout != null)
            {
                kind = MatchKind.Layout;
                return byLayout;
            }
            why = $"{Table}.dbd has no definition for {(build is { } bb ? bb.ToString() : "this build")} or layout {lh:X8}";
            return null;
        }
        why = $"{Table}.dbd has no definition for {(build is { } b2 ? b2.ToString() : "this build")}";
        return null;
    }
}
