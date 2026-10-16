using WoWData.Core.Mpq;
using Ws = WoWData.Core.Workspace.Workspace;

namespace WoWData.Core.Dbc;

/// <summary>A table of the game, where it comes from and whether you changed it.</summary>
public sealed class DbcTableInfo
{
    public string Name { get; init; } = "";
    /// <summary>DBFilesClient\&lt;Name&gt;.dbc</summary>
    public string Path { get; init; } = "";
    public DbcLayout? Layout { get; init; }
    /// <summary>Every archive that has the file, highest priority first (patch order respected).</summary>
    public IReadOnlyList<FileSource> Sources { get; init; } = Array.Empty<FileSource>();
    /// <summary>The archive the game reads it from, or null (deleted by a patch, or only in your workspace).</summary>
    public string? Archive => Sources.FirstOrDefault(s => s.Kind == SourceKind.Archive)?.Name;
    /// <summary>A patch archive deletes the file: the game cannot load it.</summary>
    public bool DeletedInGame => Sources.Count > 0 && Sources[0].Kind == SourceKind.Deleted;
    /// <summary>A loose copy in the game's Data folder (read by the loose-file mode instead of the archives).</summary>
    public string? LooseFile { get; init; }
    /// <summary>The loose copy was installed by "Test in game" and is your current edit.</summary>
    public bool LooseIsCurrentEdit { get; init; }
    public bool InWorkspace { get; init; }

    /// <summary>"patch-2.MPQ (over patch.MPQ, dbc.MPQ)".</summary>
    public string SourceText
    {
        get
        {
            if (DeletedInGame) return $"Deleted by {Sources[0].Name}: the game cannot load this file (only the server uses it).";
            var arch = Sources.Where(s => s.Kind is SourceKind.Archive or SourceKind.Hidden).Select(s => s.Name).ToList();
            if (arch.Count == 0) return InWorkspace ? "Only in your workspace (a new file)." : "Not in the game's archives.";
            var s = arch[0] + (arch.Count > 1 ? $" (overrides {string.Join(", ", arch.Skip(1))})" : "");
            return "From " + s + ".";
        }
    }

    public string OverrideText
    {
        get
        {
            var parts = new List<string>();
            if (InWorkspace) parts.Add("Your edit is in your workspace.");
            if (LooseFile != null)
                parts.Add(LooseIsCurrentEdit ? "Installed as a loose file in the game folder (Test in game)."
                    : $"A loose copy is in the game folder ({LooseFile}); with the loose-file mode on, the game reads it instead of the archives.");
            return string.Join(" ", parts);
        }
    }
}

/// <summary>A row of another table that points at a row ("Used by").</summary>
public sealed record DbcUsedBy(string Table, string Column, long? Id, int RowIndex, string Label)
{
    public string Text => $"{Table} {(Id is { } i ? "#" + i : $"row {RowIndex + 1}")} · {Label} (column {Column})";
}

/// <summary>
/// The DBC tables of the open game folder: the list (patch order respected, the archives each file is in, loose copies and
/// workspace edits shown), loading a table (your workspace version first), saving an edited table into the workspace (one
/// change set, so My changes, Compare, Test in game and the release patch work as for textures), following links, the
/// reverse "Used by" lookup, and the game files a row names (icons, textures, models).
/// </summary>
public sealed class DbcService
{
    public const string Folder = "DBFilesClient";

    private readonly ClientData _data;
    private readonly Ws? _ws;
    private readonly Dictionary<string, (DbcTable Table, string Stamp)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Dictionary<string, List<string>>? _byFileName;

    public DbcService(ClientData data, Ws? workspace, DbcLayouts? layouts = null)
    {
        _data = data;
        _ws = workspace;
        Layouts = layouts ?? DbcLayouts.Default;
    }

    public DbcLayouts Layouts { get; }
    public ClientData Data => _data;
    public Ws? Workspace => _ws;

    public static string PathOf(string table) => $"{Folder}\\{table}.dbc";

    public static string? TableOf(string path)
    {
        var p = ClientData.ResolveName(path);
        if (!p.StartsWith(Folder + "\\", StringComparison.OrdinalIgnoreCase) || !p.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)) return null;
        return System.IO.Path.GetFileNameWithoutExtension(p);
    }

    /// <summary>Every table: the client's DBFilesClient\*.dbc files, the ones a patch deletes, and new ones in the workspace.</summary>
    public List<DbcTableInfo> List()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _data.Glob(Folder + "\\*.dbc")) names.Add(System.IO.Path.GetFileNameWithoutExtension(p));
        foreach (var kv in _data.DeletedNames())
            if (TableOf(kv.Value.Path) is { } t) names.Add(t);
        if (_ws != null)
            foreach (var p in _ws.ModifiedPaths())
                if (TableOf(p) is { } t) names.Add(t);
        return names.Select(Info).ToList();
    }

    public DbcTableInfo Info(string table)
    {
        var path = PathOf(table);
        var name = Layouts.Get(table)?.Name ?? table;
        var loose = System.IO.Path.Combine(_ws?.Paths.DataDir ?? _data.DataDir, Folder, name + ".dbc");
        bool looseExists = File.Exists(loose);
        var rec = _ws?.Get(path);
        bool current = looseExists && rec?.Installed != null && rec.Installed.Sha256 == rec.WorkspaceSha256 && SafeSha(loose) == rec.Installed.Sha256;
        return new DbcTableInfo
        {
            Name = name, Path = PathOf(name), Layout = Layouts.Get(table), Sources = _data.Where(path), LooseFile = looseExists ? loose : null,
            LooseIsCurrentEdit = current, InWorkspace = rec != null,
        };
    }

    private static string? SafeSha(string f)
    {
        try { return Ws.Sha256File(f); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The game's file (from the archives), or null.</summary>
    public byte[]? ReadGame(string table) => _data.Read(PathOf(table));

    /// <summary>Your workspace version, else the game's.</summary>
    public byte[]? ReadCurrent(string table) => (_ws != null && _ws.IsModified(PathOf(table)) ? _ws.ReadEdited(PathOf(table)) : null) ?? ReadGame(table);

    public DbcTable LoadGame(string table) =>
        DbcTable.Load(ReadGame(table) ?? throw new DbcException($"{table}.dbc is not in the game's archives"), Layouts.Get(table)?.Name ?? table, Layouts);

    /// <summary>A fresh copy of the current version (for editing).</summary>
    public DbcTable LoadCurrent(string table) =>
        DbcTable.Load(ReadCurrent(table) ?? throw new DbcException($"{table}.dbc was not found"), Layouts.Get(table)?.Name ?? table, Layouts);

    private string Stamp(string table) => _ws?.Get(PathOf(table))?.WorkspaceSha256 ?? "game";

    /// <summary>The current version for reading only (links, labels, "Used by"): cached until your edit of it changes.
    /// Null when the table does not exist or cannot be read.</summary>
    public DbcTable? Lookup(string table)
    {
        lock (_gate)
        {
            var stamp = Stamp(table);
            if (_cache.TryGetValue(table, out var c) && c.Stamp == stamp) return c.Table;
            try
            {
                var t = LoadCurrent(table);
                _cache[table] = (t, stamp);
                return t;
            }
            catch (DbcException) { return null; }
        }
    }

    public void Invalidate(string table)
    {
        lock (_gate) _cache.Remove(table);
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Save an edited table into the workspace as one change set. When it is identical to the game's file, your
    /// edit is removed instead (returns null).</summary>
    public WoWData.Core.Workspace.ChangeRecord? Save(DbcTable t, string summary, WoWData.Core.Workspace.ChangeSet? changeSet = null)
    {
        if (_ws == null) throw new DbcException("No workspace is open.");
        var bytes = t.Write();
        var problems = DbcValidation.FileProblems(bytes, t.Name, Layouts);
        if (problems.Count > 0) throw new DbcException("The table would not load correctly in the game: " + string.Join("; ", problems));
        var path = PathOf(t.Name);
        var game = ReadGame(t.Name);
        Invalidate(t.Name);
        if (game != null && game.AsSpan().SequenceEqual(bytes))
        {
            if (_ws.IsModified(path)) _ws.Revert(path);
            return null;
        }
        var origin = new WoWData.Core.Workspace.OriginInfo
        {
            Archive = _data.Find(path)?.Name, Sha256 = game != null ? Ws.Sha256(game) : null, Size = game?.Length ?? 0,
            Format = $"DBC, {t.Rows.Count:N0} rows", IsNew = game == null,
        };
        origin.UsedBy.Add(t.Layout.ServerText);
        var cs = changeSet ?? _ws.BeginChangeSet($"Edited {t.Name}.dbc");
        return _ws.SaveEdit(path, bytes, origin, cs, null, summary);
    }

    /// <summary>Your version compared with the game's (row by row).</summary>
    public DbcDiffResult CompareWithGame(string table, int locale = 0)
    {
        var game = LoadGame(table);
        var mine = LoadCurrent(table);
        return DbcDiff.Compare(game, mine, locale);
    }

    // ------------------------------------------------------------------ links

    /// <summary>The label of the row a link points at ("Fireball"), or null when there is no such row.</summary>
    public string? LinkLabel(string table, long id, DbcTableLookup lookup, int locale = 0)
    {
        var t = lookup(table);
        var r = t?.Find(id);
        return r == null ? null : t!.Label(r, locale);
    }

    /// <summary>Rows of every table that link to row <paramref name="id"/> of <paramref name="table"/>.</summary>
    public List<DbcUsedBy> UsedBy(string table, long id, DbcTableLookup lookup, int max = 500, int locale = 0)
    {
        var res = new List<DbcUsedBy>();
        foreach (var l in Layouts.Tables.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cols = l.Columns.Where(c => string.Equals(c.ForeignKey, table, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cols.Count == 0) continue;
            var t = lookup(l.Name);
            if (t == null || t.Layout.IsFallback) continue;
            for (int i = 0; i < t.Rows.Count && res.Count < max; i++)
            {
                var r = t.Rows[i];
                foreach (var c in cols)
                    if (t.Number(r, c) == id)
                    {
                        res.Add(new DbcUsedBy(t.Name, c.Name, t.Key(r), i, t.Label(r, locale)));
                        break;
                    }
            }
            if (res.Count >= max) break;
        }
        return res;
    }

    /// <summary>The columns of all tables that link to <paramref name="table"/> ("Spell.SpellIconID").</summary>
    public List<string> LinkingColumns(string table) =>
        Layouts.Tables.SelectMany(l => l.Columns.Where(c => string.Equals(c.ForeignKey, table, StringComparison.OrdinalIgnoreCase)).Select(c => $"{l.Name}.{c.Name}"))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    // ------------------------------------------------------------------ the files a row names

    /// <summary>The game files a text cell names that exist (icon, texture, model), best match first.</summary>
    public List<string> FilesOf(DbcTable t, DbcRow row, DbcColumn c, int locale = 0)
    {
        var res = new List<string>();
        if (!c.IsString || c.Semantic == DbcSemantic.Plain) return res;
        var v = t.Text(row, c, locale).Trim();
        if (v.Length == 0) return res;
        void Try(string p)
        {
            if (!res.Contains(p, StringComparer.OrdinalIgnoreCase) && Exists(p)) res.Add(ClientData.ResolveName(p));
        }
        string ext = c.Semantic switch { DbcSemantic.Icon or DbcSemantic.Texture => ".blp", DbcSemantic.Model => ".m2", _ => "" };
        bool hasExt = System.IO.Path.HasExtension(v) && v.LastIndexOf('.') > v.LastIndexOf('\\');
        if (c.Semantic == DbcSemantic.Icon)
        {
            Try(v.Contains('\\') ? v + ".blp" : $"Interface\\Icons\\{v}.blp");
            if (hasExt) Try(v.Contains('\\') ? v : $"Interface\\Icons\\{v}");
            return res;
        }
        if (v.Contains('\\'))
        {
            Try(hasExt ? v : v + ext);
            if (!hasExt && c.Semantic == DbcSemantic.Model) Try(v + ".mdx");
        }
        // a bare name: next to the creature's model, else any file of that name (item textures have _U / _M / _F variants)
        if (!v.Contains('\\') || res.Count == 0)
        {
            if (t.Name.Equals("CreatureDisplayInfo", StringComparison.OrdinalIgnoreCase) && c.Name.StartsWith("TextureVariation", StringComparison.Ordinal))
            {
                var model = ModelOfDisplay(t, row);
                if (model != null) Try(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(model) ?? "", v + ".blp"));
            }
            var stem = System.IO.Path.GetFileName(hasExt ? v[..v.LastIndexOf('.')] : v);
            foreach (var cand in new[] { stem, stem + "_U", stem + "_M", stem + "_F" })
                foreach (var p in ByFileName(cand))
                    if (ext.Length == 0 || p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) Try(p);
        }
        return res;
    }

    private string? ModelOfDisplay(DbcTable t, DbcRow row)
    {
        var mc = t.Layout.Column("ModelID");
        if (mc == null) return null;
        var models = Lookup("CreatureModelData");
        var mr = models?.Find(t.Number(row, mc));
        var nameCol = models?.Layout.Column("ModelName");
        return mr == null || nameCol == null ? null : ClientData.ResolveName(models!.Text(mr, nameCol));
    }

    private bool Exists(string path) => _data.Exists(path) || (_ws?.IsModified(path) == true);

    private List<string> ByFileName(string stem)
    {
        if (_byFileName == null)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _data.Names())
            {
                var p = kv.Value.Path;
                var s = System.IO.Path.GetFileNameWithoutExtension(p);
                if (!d.TryGetValue(s, out var l)) d[s] = l = new List<string>(1);
                l.Add(p);
            }
            _byFileName = d;
        }
        return _byFileName.GetValueOrDefault(stem) ?? new List<string>();
    }
}
