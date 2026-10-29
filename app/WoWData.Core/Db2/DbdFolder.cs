using System.Globalization;

namespace WoWData.Core.Db2;

/// <summary>
/// M5 of docs\research\OPEN_OTHER_VERSIONS_DESIGN.md: a folder of WoWDBDefs definition files (<c>&lt;Table&gt;.dbd</c>)
/// the user chose (setting other-clients.dbd), read at run time to name the columns of another version's DB2 tables.
/// Optional: without it every table keeps our own measured names (<see cref="Db2Layouts"/>) or col_N. Data Studio never
/// downloads, bundles or converts these files (they are CC BY-SA 4.0); it only reads them on the user's computer.
/// Files are parsed when a table asks for them and kept for the life of the process (a changed file is read again).
/// </summary>
public sealed class DbdFolder
{
    /// <summary>The licence of the definition files, said wherever their names are shown.</summary>
    public const string Licence = "CC BY-SA 4.0";

    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Time, DbdFile? File, string Error)> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime Time, int Count, DbdFolder Folder)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder that holds the <c>.dbd</c> files (the chosen folder, or its <c>definitions</c> subfolder).</summary>
    public string Folder { get; }
    /// <summary>The number of <c>.dbd</c> files.</summary>
    public int Count => _paths.Count;
    /// <summary>The table names that have a file, sorted.</summary>
    public IReadOnlyList<string> Tables => _paths.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>"WoWDBDefs (CC BY-SA 4.0), from &lt;folder&gt;": what the app and wds say where these names are shown.</summary>
    public string Attribution => $"WoWDBDefs ({Licence}), from {Folder}";

    private DbdFolder(string folder, IEnumerable<string> files)
    {
        Folder = folder;
        foreach (var f in files) _paths[Path.GetFileNameWithoutExtension(f)] = f;
    }

    /// <summary>The folder with the <c>.dbd</c> files for a chosen folder: itself when it has some, else its
    /// <c>definitions</c> subfolder (a copy of the whole WoWDBDefs repository); null when neither has any.</summary>
    public static string? Resolve(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = folder.Trim().Trim('"');
        if (!Directory.Exists(f)) return null;
        if (Directory.EnumerateFiles(f, "*.dbd").Any()) return Path.GetFullPath(f);
        var d = Path.Combine(f, "definitions");
        return Directory.Exists(d) && Directory.EnumerateFiles(d, "*.dbd").Any() ? Path.GetFullPath(d) : null;
    }

    /// <summary>What the Settings window and "wds settings set other-clients.dbd" say about a folder: null when it is
    /// usable (<paramref name="found"/> says how many files it has), else why not.</summary>
    public static string? Check(string? folder, out string found)
    {
        found = "";
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = folder.Trim().Trim('"');
        if (!Directory.Exists(f)) return $"the folder \"{f}\" does not exist.";
        var r = Resolve(f);
        if (r == null) return $"the folder \"{f}\" has no WoWDBDefs definition files (.dbd), neither in it nor in its definitions folder.";
        int n = Directory.EnumerateFiles(r, "*.dbd").Count();
        found = $"{n:N0} WoWDBDefs definition files (.dbd) in {r}.";
        return null;
    }

    /// <summary>The definitions of a chosen folder (null when the setting is empty or the folder has no .dbd files).
    /// The same object is returned while the folder is unchanged.</summary>
    public static DbdFolder? Open(string? folder)
    {
        var r = Resolve(folder);
        if (r == null) return null;
        var time = Directory.GetLastWriteTimeUtc(r);
        var files = Directory.EnumerateFiles(r, "*.dbd").ToList();
        lock (Cache)
        {
            if (Cache.TryGetValue(r, out var c) && c.Time == time && c.Count == files.Count) return c.Folder;
            var d = new DbdFolder(r, files);
            Cache[r] = (time, files.Count, d);
            return d;
        }
    }

    /// <summary>True when the folder has a file for this table.</summary>
    public bool Has(string table) => _paths.ContainsKey(table);

    /// <summary>The parsed file of a table, or null (no file, or it could not be read: <paramref name="error"/> says why).</summary>
    public DbdFile? Get(string table, out string error)
    {
        error = "";
        if (!_paths.TryGetValue(table, out var path)) return null;
        DateTime t;
        try { t = File.GetLastWriteTimeUtc(path); }
        catch (IOException e) { error = e.Message; return null; }
        lock (_parsed)
        {
            if (_parsed.TryGetValue(table, out var p) && p.Time == t)
            {
                error = p.Error;
                return p.File;
            }
        }
        DbdFile? file = null;
        try { file = DbdFile.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)); }
        catch (Exception e) when (e is DbdFormatException or IOException or UnauthorizedAccessException) { error = e.Message; }
        lock (_parsed) _parsed[table] = (t, file, error);
        return file;
    }

    public DbdFile? Get(string table) => Get(table, out _);

    /// <summary>
    /// Whether this folder would name a table of a build without reading the table (the lists of tables): true when its
    /// file has a definition for <paramref name="version"/> (and for <paramref name="layoutHash"/> when the client's
    /// executable gives one). The table itself is checked when it is read (<see cref="Apply"/>).
    /// </summary>
    public bool Knows(string table, string version, uint? layoutHash, out string why)
    {
        var f = Get(table, out var err);
        if (f == null)
        {
            why = err.Length > 0 ? $"{table}.dbd could not be read: {err}" : $"no {table}.dbd in the WoWDBDefs folder";
            return false;
        }
        var b = DbdBuild.TryParse(version, out var bb) ? bb : (DbdBuild?)null;
        return f.Find(b, layoutHash is 0 ? null : layoutHash, out _, out why) != null;
    }

    /// <summary>
    /// Names the columns of a table read with <see cref="Db2Table.Read(byte[], Db2ReadOptions?)"/> from this folder's
    /// definition for <paramref name="version"/> ("1.60.1.70450"): the definition that lists the build and the file's
    /// layout hash (WDC3 and later), else the one with that layout hash. The definition's columns in the record (not
    /// the <c>$noninline$</c> ID or relation) must be as many as the table's, and its strings and floats must agree
    /// with the types of the client's executable. Arrays are named Name1..NameN, as in our layout files. Returns why
    /// not otherwise; the table is then unchanged.
    /// </summary>
    public bool Apply(Db2Table table, string version, out string reason)
    {
        var f = Get(table.Name, out var err);
        if (f == null)
        {
            reason = err.Length > 0 ? $"{table.Name}.dbd could not be read: {err}" : $"no {table.Name}.dbd in the WoWDBDefs folder";
            return false;
        }
        var build = DbdBuild.TryParse(version, out var b) ? b : (DbdBuild?)null;
        uint? hash = table.Format >= Db2Format.Wdc3 ? table.LayoutHash : null;
        var d = f.Find(build, hash, out _, out reason);
        if (d == null) return false;
        if (d.ColumnCount != table.Columns.Count)
        {
            reason = $"{table.Name}.dbd's definition for {version} has {d.ColumnCount} columns in the record, the file {table.Columns.Count}";
            return false;
        }
        // one entry per flattened column: name, type, size, verified
        var plan = new List<(string Name, DbdColumn Col, DbdField Field)>();
        foreach (var fld in d.InlineFields)
        {
            var col = f.Columns[fld.Name];
            int n = Math.Max(1, fld.ArrayLength);
            for (int i = 0; i < n; i++)
                plan.Add((fld.ArrayLength > 0 ? fld.Name + (i + 1).ToString(CultureInfo.InvariantCulture) : fld.Name, col, fld));
        }
        for (int i = 0; i < plan.Count; i++)
        {
            var tc = table.Columns[i];
            var (name, col, _) = plan[i];
            if (tc.TypeKnown && (col.IsString != (tc.Type == Db2ValueType.String) || col.IsFloat != (tc.Type == Db2ValueType.Float)))
            {
                reason = $"{table.Name}.{name} is {col.Type} in {table.Name}.dbd but {tc.Type} in the file";
                return false;
            }
        }
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < plan.Count; i++)
        {
            var tc = table.Columns[i];
            var (name, col, fld) = plan[i];
            var n = name;
            while (!used.Add(n)) n += "_";
            tc.Name = n;
            tc.NameVerified = col.Verified;
            if (!tc.TypeKnown)
            {
                if (col.IsString) tc.Type = Db2ValueType.String;
                else if (col.IsFloat) tc.Type = Db2ValueType.Float;
                else if (fld.Size > 0 && !fld.Unsigned)
                    // signed at the width the reader found (never narrower: a wrong size must not cut values)
                    tc.Type = (tc.Type, fld.Size) switch
                    {
                        (Db2ValueType.UInt8, 8) => Db2ValueType.Int8,
                        (Db2ValueType.UInt16, 16) => Db2ValueType.Int16,
                        (Db2ValueType.UInt32, 32) => Db2ValueType.Int32,
                        (Db2ValueType.UInt64, 64) => Db2ValueType.Int64,
                        _ => tc.Type,
                    };
            }
        }
        table.Layout = null;
        table.RenameColumns();
        reason = "";
        return true;
    }
}
