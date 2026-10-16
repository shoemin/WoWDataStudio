using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;
using WoWData.Core.Workspace;

namespace WoWData.Core.Designers;

/// <summary>How serious a designer's finding is.</summary>
public enum DesignSeverity
{
    /// <summary>The game or the server cannot handle it: saving is refused.</summary>
    Error,
    /// <summary>Probably not what you want (it saves).</summary>
    Warning,
    /// <summary>Good to know.</summary>
    Note,
}

/// <summary>One finding of a designer's check, in plain words, with the field it is about.</summary>
public sealed record DesignProblem(DesignSeverity Severity, string Field, string Message)
{
    public bool IsError => Severity == DesignSeverity.Error;
    public string Text => $"{(Severity == DesignSeverity.Error ? "Error" : Severity == DesignSeverity.Warning ? "Warning" : "Note")}: " + (Field.Length > 0 ? $"{Field}: " : "") + Message;
}

/// <summary>The result of a designer action: done or refused, with what it says.</summary>
public sealed record DesignResult(bool Ok, string Message, IReadOnlyList<DesignProblem>? Problems = null)
{
    public static DesignResult Done(string message, IReadOnlyList<DesignProblem>? problems = null) => new(true, message, problems);
    public static DesignResult Refused(string message, IReadOnlyList<DesignProblem>? problems = null) => new(false, message, problems);
}

/// <summary>What a designer's save wrote: the client tables, the server tables, and what is still to do.</summary>
public sealed class DesignSaveResult
{
    public List<string> ClientTables { get; } = new();
    /// <summary>Other game files saved (duplicated effect models, tinted textures).</summary>
    public List<string> Files { get; } = new();
    public List<string> ServerTables { get; } = new();
    public List<string> Notes { get; } = new();
    public string? ChangeSet { get; set; }
    public bool ServerSkipped { get; set; }
    public string Message => (ClientTables.Count + Files.Count + ServerTables.Count == 0 ? "Nothing to save." :
        "Saved into your workspace: " + string.Join(", ", ClientTables.Select(t => t + ".dbc").Concat(ServerTables.Select(t => t + " (server)"))) +
        (Files.Count > 0 ? $" and {Files.Count} file(s) ({string.Join(", ", Files.Take(3).Select(Path.GetFileName))}{(Files.Count > 3 ? ", ..." : "")})" : "") + ".") +
        (Notes.Count > 0 ? " " + string.Join(" ", Notes) : "");
}

/// <summary>
/// 0.12.0: what the spell designer and the class and talent designer share: the game's tables open for editing (one
/// <see cref="DbcEditSession"/> per table, opened on first use, so every edit is checked like the Data tables tab's),
/// the server's world database (a <see cref="ServerEditor"/>, or none when it is not connected), free IDs, and saving
/// everything in ONE change set of My changes: the edited client tables (Spell.dbc, SpellIcon.dbc, Talent.dbc...) and
/// the server rows (spell_template, npc_trainer, skill_line_ability, playercreateinfo...). Nothing reaches the game or
/// the server here: Test in game, the release patch and Apply to server do that, as for every other edit.
/// </summary>
public sealed class DesignSession
{
    private readonly Dictionary<string, DbcEditSession> _tables = new(StringComparer.OrdinalIgnoreCase);

    public DesignSession(DbcService dbc, ServerEditor? server, (long Start, long End) customIds, string? serverNote = null)
    {
        Dbc = dbc;
        Server = server;
        CustomIds = customIds;
        // the server's checks of links to the game's tables see the rows made here before they are saved
        if (server != null) server.GameTables = Lookup;
        ServerNote = serverNote ?? (server == null ? "The server database is not connected: only the game's tables are edited." : "");
    }

    public DbcService Dbc { get; }
    /// <summary>The server's world database (null: not connected; the designers then edit the game's tables only).</summary>
    public ServerEditor? Server { get; }
    public (long Start, long End) CustomIds { get; }
    public string ServerNote { get; }
    public bool HasServer => Server != null;

    /// <summary>A table open for editing (opened once; later edits see the earlier ones).</summary>
    public DbcEditSession Session(string table)
    {
        if (!_tables.TryGetValue(table, out var s))
        {
            _tables[table] = s = new DbcEditSession(Dbc, table);
            s.Others = name => _tables.TryGetValue(name, out var o) ? o.Table : null;
        }
        return s;
    }

    public DbcTable Table(string table) => Session(table).Table;

    /// <summary>Is a table already open here?</summary>
    public bool IsOpen(string table) => _tables.ContainsKey(table);

    /// <summary>A table for reading: the open one (with the unsaved edits), else the current version.</summary>
    public DbcTable? Lookup(string table) => _tables.TryGetValue(table, out var s) ? s.Table : Dbc.Lookup(table);

    public DbcTableLookup Lookups => Lookup;

    /// <summary>The tables with unsaved edits.</summary>
    public List<string> DirtyTables => _tables.Values.Where(s => s.Editor.IsDirty).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public bool IsDirty => DirtyTables.Count > 0 || _files.Count > 0 || (Server?.IsDirty ?? false);

    // ------------------------------------------------------------------ IDs

    /// <summary>The first ID of your custom range that neither the game's table nor the server's table uses (and none of
    /// <paramref name="taken"/>). For spells: Spell.dbc and spell_template.entry.</summary>
    public long FreeCustomId(string dbcTable, string? serverTable = null, string? serverColumn = null, IEnumerable<long>? taken = null)
    {
        var (start, end) = CustomIds;
        var t = Table(dbcTable);
        var used = new HashSet<long>(taken ?? Array.Empty<long>());
        foreach (var r in t.Rows)
            if (t.Key(r) is { } k && k >= start && k <= end) used.Add(k);
        if (Server != null && serverTable != null && Server.Schema.Table(serverTable) is { } st)
            used.UnionWith(Server.UsedIds(st, serverColumn ?? Server.Service.IdColumn(st), start, end));
        for (long v = start; v <= end; v++)
            if (!used.Contains(v)) return v;
        throw new DesignException($"Your custom ID range {start}-{end} is full for {dbcTable} (Settings: Custom ID range).");
    }

    /// <summary>One more than the highest ID of the game's table and the server's table (for small tables whose IDs
    /// must stay low: SkillLineAbility's server column holds 0-65535).</summary>
    public long NextId(string dbcTable, string? serverTable = null, string? serverColumn = null, long max = uint.MaxValue)
    {
        long top = Table(dbcTable).SuggestId() - 1;
        if (Server != null && serverTable != null && Server.Schema.Table(serverTable) is { } st)
            top = Math.Max(top, Server.MaxId(st, serverColumn ?? Server.Service.IdColumn(st)));
        if (top + 1 > max) throw new DesignException($"No free ID is left in {dbcTable} (the highest allowed is {max}).");
        return top + 1;
    }

    // ------------------------------------------------------------------ editing helpers

    /// <summary>Set a column of a row by name (checked like the Data tables tab). Throws with the reason when refused.</summary>
    public void Set(string table, DbcRow row, string column, string value)
    {
        var s = Session(table);
        var c = s.Column(column) ?? throw new DesignException($"{table} has no column {column}.");
        var r = s.Edit(row, c, value);
        if (!r.Ok) throw new DesignException($"{table} {column}: {r.Message}");
    }

    public void SetNumber(string table, DbcRow row, string column, long value) => Set(table, row, column, value.ToString(CultureInfo.InvariantCulture));

    public void SetFloat(string table, DbcRow row, string column, float value) => Set(table, row, column, value.ToString("R", CultureInfo.InvariantCulture));

    public long Get(string table, DbcRow row, string column)
    {
        var t = Table(table);
        return t.Number(row, t.Layout.Column(column) ?? throw new DesignException($"{table} has no column {column}."));
    }

    public string Text(string table, DbcRow row, string column)
    {
        var t = Table(table);
        return t.Format(row, t.Layout.Column(column) ?? throw new DesignException($"{table} has no column {column}."), 0);
    }

    /// <summary>Add a row with a given ID (or the next free one) to a table.</summary>
    public DbcRow AddRow(string table, long? id = null)
    {
        var (row, _) = Session(table).AddRow(id);
        return row;
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// Save every edited client table and the server edits into the workspace, in one change set named
    /// <paramref name="label"/>. <paramref name="beforeServer"/> runs between the two (the designers bring the server's
    /// rows in line with the saved client rows there). Refused (nothing saved) while a server row has an error.
    /// </summary>
    public DesignSaveResult Save(string label, Action<DesignSaveResult>? beforeServer = null)
    {
        var res = new DesignSaveResult();
        var ws = Dbc.Workspace ?? throw new DesignException("No game folder is open (the workspace keeps your edits).");
        ChangeSet? set = null;
        ChangeSet Set() => set ??= ws.BeginChangeSet(label);
        // the server rows follow the client rows first: when the server refuses one, nothing is saved
        beforeServer?.Invoke(res);
        if (Server != null)
        {
            var blocking = Server.BlockingProblems();
            if (blocking.Count > 0)
                throw new DesignException("Not saved: the server would refuse these rows:\n" + string.Join("\n", blocking.Take(8)));
        }
        var serverFileTables = new List<string>();
        foreach (var name in DirtyTables)
        {
            var s = _tables[name];
            var (rec, _) = s.Save(null, Set());
            res.ClientTables.Add(s.Name);
            if (rec != null && s.Table.Layout.ServerUse == DbcServerUse.ServerFile) serverFileTables.Add(s.Name + ".dbc");
        }
        if (serverFileTables.Count > 0)
            res.Notes.Add($"The server reads its own copy of {string.Join(", ", serverFileTables)}: after testing, use \"Copy tables to the server...\" (wds dbc copy-to-server).");
        foreach (var f in _files)
        {
            ws.SaveEdit(f.Path, f.Data, f.Origin, Set(), null, f.Summary);
            res.Files.Add(f.Path);
        }
        _files.Clear();
        if (Server != null)
        {
            var dirty = Server.DirtyTables();
            if (dirty.Count > 0)
            {
                Server.Save(label, Set());
                res.ServerTables.AddRange(dirty);
                res.Notes.Add("The server is not changed yet: \"Apply to server...\" (wds server apply) writes the rows into its database.");
            }
        }
        else
        {
            res.ServerSkipped = true;
            if (res.ClientTables.Count > 0) res.Notes.Add(ServerNote);
        }
        res.ChangeSet = set?.Id;
        return res;
    }

    /// <summary>Throw away every unsaved edit (the tables are opened again on next use).</summary>
    public void Discard()
    {
        _tables.Clear();
        _files.Clear();
        Server?.Discard();
    }

    private sealed record PendingFile(string Path, byte[] Data, OriginInfo Origin, string Summary);
    private readonly List<PendingFile> _files = new();

    /// <summary>A new or changed game file (a duplicated effect model, a tinted texture) saved with the next save.</summary>
    public void AddFile(string path, byte[] data, OriginInfo origin, string summary)
    {
        _files.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        _files.Add(new PendingFile(path, data, origin, summary));
    }

    /// <summary>A file as it will be after the next save: pending, else the workspace's, else the game's.</summary>
    public byte[]? ReadFile(string path) => _files.LastOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Data
        ?? (Dbc.Workspace?.IsModified(path) == true ? Dbc.Workspace.ReadEdited(path) : null) ?? Dbc.Data.Read(path);

    public IReadOnlyList<string> PendingFiles => _files.Select(f => f.Path).ToList();
}

/// <summary>A designer action that cannot be done (the message says why, in plain words).</summary>
public sealed class DesignException : Exception
{
    public DesignException(string message) : base(message) { }
}
