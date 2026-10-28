using WoWData.Core.Dbc;
using WoWData.Core.Session;

namespace WoWData.Cli.Commands;

/// <summary>dbc: the Data tables tab. Every change is checked like in the app and saved into the workspace at once
/// (there is no unsaved state on the command line); --dry-run shows the change and saves nothing.</summary>
internal static class DbcCommands
{
    private static readonly OptionSpec Locale = new("locale", "language", "the language of localized texts: enUS, koKR, frFR, deDE, zhCN, zhTW, esES, esMX (default: the setting, else the game's)");
    private static readonly OptionSpec Where = new("where", "column=filter", "a column filter as in the grid: text (part), =exact, !not; numbers 5, >5, <=10, 5-10, !0; a choice or link by its name", true);
    private static readonly OptionSpec Search = new("search", "words", "\"Search all text...\": every word somewhere in the row");
    private static readonly OptionSpec Ids = new("ids", "list", "only these IDs (comma-separated)");

    private static int LocaleOf(Invocation inv, AppSession s)
    {
        if (inv.Opt("locale") is { } l)
        {
            int i = Array.FindIndex(DbcTable.LocaleNames, n => n.Equals(l, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : throw new CliError(ExitCode.Usage, $"--locale is one of {string.Join(", ", DbcTable.LocaleNames)}.");
        }
        return Math.Clamp(s.Settings.DbcLocale ?? s.GameLocale, 0, DbcFile.Locales - 1);
    }

    private static DbcEditSession Open(Invocation inv, AppSession s, string table)
    {
        var svc = s.Dbc ?? throw new CliError(ExitCode.Error, "The data tables could not be read.");
        var info = svc.List().FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
                   ?? throw new CliError(ExitCode.NotFound, $"no table \"{table}\" (\"wds dbc tables\" lists them).");
        if (info.DeletedInGame) throw new CliError(ExitCode.NotFound, $"{info.Name}.dbc: {info.SourceText}");
        if (info.Layout == null) throw new CliError(ExitCode.NotFound, $"{info.Name}.dbc has no known layout.");
        return new DbcEditSession(svc, info.Name, LocaleOf(inv, s));
    }

    private static DbcRow Row(DbcEditSession e, long id) =>
        e.Row(id) ?? throw new CliError(ExitCode.NotFound, $"{e.Name} has no row with {(e.Table.Layout.HasKey ? "ID" : "number")} {id}.");

    private static DbcColumn Col(DbcEditSession e, string name) =>
        e.Column(name) ?? throw new CliError(ExitCode.NotFound, $"{e.Name} has no column \"{name}\" (\"wds dbc info {e.Name}\" lists them).");

    /// <summary>The rows of a query: --ids, --search, --where (the grid's filters).</summary>
    private static List<DbcRow> Query(Invocation inv, DbcEditSession e, string? sortName = null, bool desc = false)
    {
        var filters = new List<(DbcColumn, DbcFilter)>();
        foreach (var w in inv.Opts("where"))
        {
            int eq = w.IndexOf('=');
            if (eq <= 0) throw new CliError(ExitCode.Usage, $"--where is column=filter (not {w}); for an exact text: Name==Fireball.");
            filters.Add((Col(e, w[..eq].Trim()), DbcFilter.Parse(w[(eq + 1)..])));
        }
        var sort = sortName == null ? null : Col(e, sortName);
        var idx = DbcEditSession.Query(e.Table, e.Locale, inv.Opt("search") ?? "", filters, sort, !desc);
        var rows = idx.Select(i => e.Table.Rows[i]).ToList();
        if (inv.Opt("ids") is { } ids)
        {
            var set = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, $"--ids are numbers (not {x}).")).ToHashSet();
            rows = rows.Where(r => e.Table.Key(r) is { } k && set.Contains(k)).ToList();
        }
        return rows;
    }

    private static Dictionary<string, object?> RowJson(DbcEditSession e, DbcRow r, IEnumerable<DbcColumn>? cols = null, bool raw = false)
    {
        var d = new Dictionary<string, object?> { ["id"] = e.Table.Key(r), ["label"] = e.Table.Label(r, e.Locale) };
        var values = new Dictionary<string, object?>();
        foreach (var c in cols ?? e.Table.Layout.Columns) values[c.Name] = raw ? e.CellText(r, c, true) : e.CellText(r, c, false);
        d["values"] = values;
        return d;
    }

    /// <summary>Save the edit session into the workspace (or, with --dry-run, only show the difference with the game).</summary>
    private static CliResult SaveOrPreview(Invocation inv, DbcEditSession e, CliResult r, object data)
    {
        var diff = e.CompareWithGame();
        r.Line($"{e.Name}.dbc compared with the game's file: {diff.Summary}");
        if (inv.DryRun)
        {
            r.Line("Dry run: nothing was saved.");
            return r.With(new { table = e.Name, saved = false, diff = DiffJson(diff), change = data });
        }
        var (rec, message) = e.Save(diff);
        r.Line(message);
        if (e.ServerNote.Length > 0) r.Line(e.ServerNote);
        return r.With(new { table = e.Name, saved = rec != null, removedEdit = rec == null, changeSet = rec?.ChangeSet, diff = DiffJson(diff), change = data });
    }

    private static object DiffJson(DbcDiffResult d) => new
    {
        summary = d.Summary, changed = d.Changed, added = d.Added, removed = d.Removed, cellsChanged = d.CellsChanged,
        rows = d.Rows.Take(1000).Select(x => new { kind = x.Kind, key = x.Key, label = x.Label, cells = x.Cells.Select(c => new { column = c.Column, before = c.Before, after = c.After }) }),
        truncated = d.Rows.Count > 1000,
    };

    private static void DiffLines(CliResult r, DbcDiffResult d, int max = 60)
    {
        foreach (var row in d.Rows.Take(max)) r.Line($"  {row.KindText,-8} {row.Key} {row.Label}: {row.DetailsText}");
        if (d.Rows.Count > max) r.Line($"  ... and {d.Rows.Count - max} more rows (--json lists up to 1000)");
    }

    /// <summary>--set Column=value pairs onto a row (each checked; the first refused value stops with exit code 3).</summary>
    private static void ApplySets(Invocation inv, DbcEditSession e, DbcRow row, CliResult r)
    {
        foreach (var kv in inv.Opts("set"))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) throw new CliError(ExitCode.Usage, $"--set is Column=value (not {kv}).");
            var c = Col(e, kv[..eq].Trim());
            var res = e.Edit(row, c, kv[(eq + 1)..]);
            if (!res.Ok) throw new CliError(ExitCode.ValidationFailed, $"{c.Name}: {res.Message} Nothing was saved.");
            r.Line($"  {c.Name} = {e.CellText(row, c, false)}" + (res.Message.Contains("changed.", StringComparison.Ordinal) ? "" : $"  ({res.Message})"));
        }
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "dbc tables",
            Summary = "every data table of the game: group, what the server does with it, how much is named, your edit",
            Options = new[] { new OptionSpec("filter", "words", "find a table by its name or what it is for (icon, mount, flight...)"), new OptionSpec("changed", null, "only the tables in your workspace (\"Only tables I changed\")") },
            Examples = new[] { "wds dbc tables", "wds dbc tables --filter flight", "wds dbc tables --changed --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return DbcCommandsOther.Tables(inv, s);   // another version: its DB2 tables, read only
                var words = (inv.Opt("filter") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var list = s.Dbc!.List().OrderBy(i => i.Layout?.Category ?? "~", StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(i => !inv.Flag("changed") || i.InWorkspace)
                    .Where(i => words.All(w => (i.Name + " " + i.Layout?.Category + " " + i.Layout?.About).ToLowerInvariant().Contains(w))).ToList();
                var r = new CliResult().With(list.Select(i => new
                {
                    name = i.Name, category = i.Layout?.Category, about = i.Layout?.About, server = i.Layout?.ServerUse, serverText = i.Layout?.ServerText, serverTable = i.Layout?.ServerTable,
                    coverage = i.Layout?.CoverageText, archive = i.Archive, deletedInGame = i.DeletedInGame, modified = i.InWorkspace, looseFile = i.LooseFile,
                }));
                foreach (var l in Common.Table(new[] { "Table", "Group", "Server", "Columns", "" },
                             list.Select(i => (IReadOnlyList<string>)new[] { i.Name, i.Layout?.Category ?? "", i.DeletedInGame ? "Server only · deleted by patch.MPQ" : i.Layout?.ServerText ?? "", i.Layout?.CoverageText ?? "", i.InWorkspace ? "Modified" : "" }), 50))
                    r.Line(l);
                r.Line($"{list.Count} tables.");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc info",
            Summary = "a table: what it controls, where the file comes from, what the server does with it, and every column with its type and hint",
            Args = new[] { "<table>" },
            Examples = new[] { "wds dbc info Spell", "wds dbc info TaxiNodes --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return DbcCommandsOther.Info(inv, s);   // another version: its DB2 tables, read only
                var e = Open(inv, s, inv.Arg(0));
                var l = e.Table.Layout;
                var info = s.Dbc!.Info(e.Name);
                var r = new CliResult().With(new
                {
                    name = e.Name, about = l.About, category = l.Category, rows = e.Table.Rows.Count, source = info.SourceText, overrides = info.OverrideText, modified = info.InWorkspace,
                    server = l.ServerUse, serverText = l.ServerText, serverExplanation = l.ServerExplanation, serverTable = l.ServerTable, coverage = l.CoverageText, evidence = l.Evidence,
                    columns = l.Columns.Select(c => new { index = c.Index, name = c.Name, type = c.TypeText, field = c.Field, hint = c.Hint, verified = c.Verified, link = c.ForeignKey, values = c.Values?.Values.Select(v => new { value = v.Value, name = v.Name }) }),
                });
                r.Line($"{e.Name}.dbc: {l.About}").Line($"{e.Table.Rows.Count:N0} rows. {info.SourceText}");
                if (info.OverrideText.Length > 0) r.Line(info.OverrideText);
                r.Line($"{l.ServerText}: {l.ServerExplanation}").Line(l.CoverageText + (l.UnverifiedColumns > 0 ? $" ({l.UnverifiedColumns} not verified)" : ""));
                foreach (var t in Common.Table(new[] { "#", "Column", "Type", "Hint" }, l.Columns.Select(c => (IReadOnlyList<string>)new[] { c.Index.ToString(), c.Name + (c.Verified ? "" : " (not verified)"), c.TypeText, c.Hint }), 80)) r.Line(t);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc rows",
            Summary = "rows of a table, like the grid: search, column filters, sorting, chosen columns (choices and links shown with their names)",
            Args = new[] { "<table>" },
            Options = new[] { Search, Where, Ids, new OptionSpec("columns", "list", "only these columns (comma-separated names)"), new OptionSpec("sort", "column", "sort by a column"), new OptionSpec("desc", null, "the other direction"), new OptionSpec("raw", null, "the stored values (no names of choices and links)"), Locale, Common.Limit },
            Examples = new[] { "wds dbc rows Spell --where Name==Fireball --columns ID,Name,NameSubtext,SpellIconID", "wds dbc rows ItemDisplayInfo --where InventoryIcon=INV_Sword_04 --limit 0 --json", "wds dbc rows Spell --search \"frost bolt\" --sort ID --desc" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return DbcCommandsOther.Rows(inv, s);   // another version: its DB2 tables, read only
                var e = Open(inv, s, inv.Arg(0));
                var rows = Query(inv, e, inv.Opt("sort"), inv.Flag("desc"));
                var cols = inv.Opt("columns") is { } cl ? cl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => Col(e, n)).ToList() : e.Table.Layout.Columns.ToList();
                int limit = Common.LimitOf(inv);
                var shown = rows.Take(limit).ToList();
                var r = new CliResult().With(new { table = e.Name, total = e.Table.Rows.Count, matched = rows.Count, shown = shown.Count, columns = cols.Select(c => c.Name), rows = shown.Select(x => RowJson(e, x, cols, inv.Flag("raw"))) });
                r.Line(rows.Count == e.Table.Rows.Count ? $"{e.Table.Rows.Count:N0} rows" : $"{rows.Count:N0} of {e.Table.Rows.Count:N0} rows" + (shown.Count < rows.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                foreach (var l in Common.Table(cols.Select(c => c.Name).ToList(), shown.Select(x => (IReadOnlyList<string>)cols.Select(c => e.CellText(x, c, inv.Flag("raw"))).ToList()), 40)) r.Line(l);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc get",
            Summary = "one row (\"Go to ID\"): every value with its type, links with the linked row's name; or one value",
            Args = new[] { "<table>", "<id>", "[column]" },
            Options = new[] { new OptionSpec("raw", null, "the stored values"), Locale },
            Examples = new[] { "wds dbc get Spell 133", "wds dbc get Spell 133 SpellIconID", "wds dbc get CreatureDisplayInfo 503 --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return DbcCommandsOther.Get(inv, s);   // another version: its DB2 tables, read only
                var e = Open(inv, s, inv.Arg(0));
                var row = Row(e, inv.Long(1));
                if (inv.ArgOrNull(2) is { } colName)
                {
                    var c = Col(e, colName);
                    var v = e.CellText(row, c, inv.Flag("raw"));
                    return new CliResult().With(new { table = e.Name, id = e.Table.Key(row), column = c.Name, value = v, raw = e.CellText(row, c, true), type = c.TypeText }).Line(v);
                }
                var r = new CliResult().With(RowJson(e, row, null, inv.Flag("raw")));
                r.Line($"{e.Table.Label(row, e.Locale)}  ({e.Name} · {e.RowName(row)})");
                foreach (var c in e.Table.Layout.Columns) r.Line($"  {c.Name,-28} {e.CellText(row, c, inv.Flag("raw"))}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc set",
            Summary = "change one value (checked like the app: type, range, links, unique IDs) and save the table into your workspace",
            Args = new[] { "<table>", "<id>", "<column>", "<value>" },
            Options = new[] { Locale, Common.DryRun },
            Details = "Localized texts change only the chosen language (--locale). A refused value is not stored (exit code 3) and the reason is printed. " +
                      "The table becomes one change set in My changes; then \"wds changes install\" or \"wds changes build-patch\", and \"wds dbc copy-to-server\" for tables the server reads.",
            Examples = new[] { "wds dbc set Spell 133 SpellIconID 184", "wds dbc set TaxiNodes 88 Name \"Stormwind, Old Town\" --locale enUS", "wds dbc set CreatureDisplayInfo 503 CreatureModelScale 1.5 --dry-run --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                var row = Row(e, inv.Long(1));
                var c = Col(e, inv.Arg(2));
                var before = e.CellText(row, c, false);
                var res = e.Edit(row, c, inv.Arg(3));
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, $"{c.Name} of {e.Table.Label(row, e.Locale)}: {res.Message}");
                var r = new CliResult().Line($"{e.Name} {e.RowName(row)} ({e.Table.Label(row, e.Locale)}): {c.Name} {before} -> {e.CellText(row, c, false)}");
                if (!res.Message.Contains("changed.", StringComparison.Ordinal)) r.Warnings.Add(res.Message);
                if (res.Changed == 0) return r.With(new { table = e.Name, saved = false, unchanged = true });
                return SaveOrPreview(inv, e, r, new { id = e.Table.Key(row), column = c.Name, before, after = e.CellText(row, c, false) });
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc add",
            Summary = "\"Add row\": a new row with the next free ID (or --id), values from --set, saved into your workspace",
            Args = new[] { "<table>" },
            Options = new[] { new OptionSpec("id", "n", "the new row's ID (default: the next free one)"), new OptionSpec("set", "column=value", "a value of the new row (checked)", true), Locale, Common.DryRun },
            Examples = new[] { "wds dbc add SpellIcon --set TextureFilename=Interface\\Icons\\INV_Misc_QuestionMark", "wds dbc add TaxiPathNode --set PathID=500 --set NodeIndex=0 --dry-run" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                long? id = inv.Opt("id") is { } x ? long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, "--id is a number.") : null;
                DbcRow row;
                string msg;
                try { (row, msg) = e.AddRow(id); }
                catch (DbcException ex) { return CliResult.Fail(ExitCode.ValidationFailed, ex.Message); }
                var r = new CliResult().Line(msg);
                ApplySets(inv, e, row, r);
                return SaveOrPreview(inv, e, r, RowJson(e, row));
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc duplicate",
            Summary = "\"Duplicate as new\": a copy of a row with the next free ID (or --id), changed by --set, saved into your workspace",
            Args = new[] { "<table>", "<id>" },
            Options = new[] { new OptionSpec("id", "n", "the copy's ID (default: the next free one)"), new OptionSpec("set", "column=value", "a value of the copy (checked)", true), Locale, Common.DryRun },
            Examples = new[] { "wds dbc duplicate CreatureDisplayInfo 503 --set CreatureModelScale=1.5", "wds dbc duplicate TaxiNodes 2 --set \"Name=Stormwind, Old Town\" --set X=-8800 --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                var src = Row(e, inv.Long(1));
                long? id = inv.Opt("id") is { } x ? long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, "--id is a number.") : null;
                DbcRow row;
                string msg;
                try { (row, msg) = e.Duplicate(src, id); }
                catch (DbcException ex) { return CliResult.Fail(ExitCode.ValidationFailed, ex.Message); }
                var r = new CliResult().Line(msg);
                ApplySets(inv, e, row, r);
                return SaveOrPreview(inv, e, r, RowJson(e, row));
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc delete",
            Summary = "\"Delete rows\": delete rows by ID (says which other rows link to them first; needs --yes)",
            Args = new[] { "<table>", "<ids...>" },
            Options = new[] { Locale, Common.DryRun },
            Examples = new[] { "wds dbc delete SpellIcon 4000 --dry-run", "wds dbc delete TaxiPathNode 9000 9001 --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                var rows = inv.Args.Skip(1).Select(a => long.TryParse(a, out var v) ? Row(e, v) : throw new CliError(ExitCode.Usage, $"IDs are numbers (not {a}).")).Distinct().ToList();
                var used = e.LinksTo(rows);
                var r = new CliResult().Line(rows.Count == 1 ? $"Delete {e.Table.Label(rows[0], e.Locale)} ({e.RowName(rows[0])})" : $"Delete {rows.Count} rows of {e.Name}");
                if (used.Count > 0)
                {
                    r.Line("Other rows link to " + (rows.Count == 1 ? "it" : "some of them") + " (those links will point at nothing):");
                    foreach (var u in used) r.Line("  " + u);
                    r.Warnings.Add($"{used.Count} of the rows are used by other rows.");
                }
                if (!inv.DryRun && !inv.Yes) return Common.NeedsYes(r.With(new { table = e.Name, ids = rows.Select(x => e.Table.Key(x)), usedBy = used, saved = false }), "This deletes rows of the table (in your workspace).");
                r.Line(e.DeleteRows(rows));
                return SaveOrPreview(inv, e, r, new { ids = rows.Select(x => e.Table.Key(x)), usedBy = used });
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc bulk",
            Summary = "\"Bulk edit...\": set, add to or multiply one column of many rows (all checked first; nothing changes if one is refused)",
            Args = new[] { "<table>" },
            Options = new[]
            {
                new OptionSpec("column", "name", "the column to change (required)"), new OptionSpec("op", "set|add|multiply", "what to do (required)"), new OptionSpec("value", "v", "the value or the number (required)"),
                Ids, Search, Where, Locale, Common.DryRun,
            },
            Details = "The rows are chosen like in the grid: --ids, --search and --where (at least one; no filter changes nothing).",
            Examples = new[] { "wds dbc bulk Spell --where Name==Fireball --where NameSubtext=Rank --column SpellIconID --op set --value 184", "wds dbc bulk SpellCastTimes --ids 4,5,6,7 --column Base --op multiply --value 0.5 --dry-run" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                if (inv.Opt("ids") == null && inv.Opt("search") == null && inv.Opts("where").Count == 0) throw new CliError(ExitCode.Usage, "choose the rows with --ids, --search or --where.");
                var c = Col(e, inv.Opt("column") ?? throw new CliError(ExitCode.Usage, "give --column."));
                var op = (inv.Opt("op") ?? throw new CliError(ExitCode.Usage, "give --op set|add|multiply.")).ToLowerInvariant() switch
                {
                    "set" => BulkOperation.Set, "add" => BulkOperation.Add, "multiply" => BulkOperation.Multiply,
                    var x => throw new CliError(ExitCode.Usage, $"--op is set, add or multiply (not {x})."),
                };
                var value = inv.Opt("value") ?? throw new CliError(ExitCode.Usage, "give --value.");
                var rows = Query(inv, e);
                if (rows.Count == 0) return CliResult.Fail(ExitCode.NotFound, "No row matches: nothing was changed.");
                var res = e.BulkEdit(c, op, value, rows);
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                var r = new CliResult().Line($"{op} {c.Name} of {Common.Plural(rows.Count, "row")}: {res.Message}");
                return SaveOrPreview(inv, e, r, new { column = c.Name, op, value, rows = rows.Count, changed = res.Changed });
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc export",
            Summary = "export rows as CSV (for spreadsheets) or JSON (by the file's extension): all rows, or the ones a filter shows",
            Args = new[] { "<table>" },
            Options = new[] { new OptionSpec("out", "file", "the .csv or .json file to write (required)"), Ids, Search, Where, Locale },
            Details = "A localized text becomes nine columns (Name_enUS ... Name_esMX, Name_flags). An exported file holds every value exactly: importing it unchanged changes nothing.",
            Examples = new[] { "wds dbc export Spell --out Spell.csv", "wds dbc export ItemDisplayInfo --where InventoryIcon=INV_Sword --out swords.json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                if (s.IsReadOnly) return DbcCommandsOther.Export(inv, s);   // another version: its DB2 tables, read only
                var e = Open(inv, s, inv.Arg(0));
                var file = Path.GetFullPath(inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "give --out <file.csv|file.json>."));
                var rows = Query(inv, e);
                if (rows.Count == 0) return CliResult.Fail(ExitCode.NotFound, "There are no rows to export.");
                e.ExportTo(file, rows);
                return new CliResult().With(new { table = e.Name, file, rows = rows.Count, format = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv" }).Line($"Exported {rows.Count:N0} rows of {e.Name} to {file}.");
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc import",
            Summary = "import a CSV or JSON file: a preview of every changed, added and removed row; --apply imports it and saves the table",
            Args = new[] { "<table>", "<file>" },
            Options = new[] { new OptionSpec("remove-missing", null, "the file is the whole table: rows it does not have are removed (\"Remove them\"; default: keep them)"), new OptionSpec("apply", null, "apply the import and save the table into your workspace (without it: the preview only)"), Locale },
            Details = "Rows are matched by their ID. Errors (a bad number, a link to a row that does not exist, an ID twice) block the import (exit code 3); warnings do not.",
            Examples = new[] { "wds dbc import Spell Spell.csv", "wds dbc import Spell Spell.csv --apply", "wds dbc import TaxiNodes nodes.json --remove-missing --apply --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                var file = Path.GetFullPath(inv.Arg(1));
                if (!File.Exists(file)) throw new CliError(ExitCode.NotFound, $"{file} does not exist.");
                var plan = e.PlanImport(file, inv.Flag("remove-missing") ? DbcMissingRows.Remove : DbcMissingRows.Keep);
                var r = new CliResult();
                r.Line($"Import {Path.GetFileName(file)} into {e.Name}: {plan.RowsRead:N0} rows read. " + (plan.IsEmpty ? "The file changes nothing." : plan.Preview.Summary));
                DiffLines(r, plan.Preview);
                foreach (var er in plan.Errors.Take(60)) r.Line("  error: " + er);
                r.Warnings.AddRange(plan.Warnings);
                object data = new { table = e.Name, file, rowsRead = plan.RowsRead, errors = plan.Errors, warnings = plan.Warnings, preview = DiffJson(plan.Preview), applied = false };
                if (plan.Errors.Count > 0) return new CliResult { Code = ExitCode.ValidationFailed, Error = $"{Common.Plural(plan.Errors.Count, "error")} block the import: fix the file and import again." }.With(data);
                if (!inv.Flag("apply") || plan.IsEmpty)
                {
                    if (!plan.IsEmpty) r.Line("Preview only: nothing was changed. --apply imports it and saves the table.");
                    return r.With(data);
                }
                r.Line(e.ApplyImport(plan, file));
                return SaveOrPreview(inv, e, r, data);
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc used-by",
            Summary = "\"Used by\": the rows of other tables that link to a row",
            Args = new[] { "<table>", "<id>" },
            Options = new[] { Locale, Common.Limit },
            Examples = new[] { "wds dbc used-by SpellIcon 185", "wds dbc used-by CreatureModelData 145 --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                var row = Row(e, inv.Long(1));
                var id = e.Table.Key(row) ?? throw new CliError(ExitCode.NotFound, "Other tables cannot link to this table (it has no ID column).");
                var cols = s.Dbc!.LinkingColumns(e.Name);
                int limit = Common.LimitOf(inv, 300);
                var list = s.Dbc.UsedBy(e.Name, id, e.Lookup, limit == int.MaxValue ? 100000 : limit, e.Locale);
                var r = new CliResult().With(new { table = e.Name, id, label = e.Table.Label(row, e.Locale), checkedColumns = cols, usedBy = list.Select(u => new { table = u.Table, column = u.Column, id = u.Id, label = u.Label }) });
                r.Line(list.Count == 0 ? $"Nothing links to {e.Name} {id} (checked {string.Join(", ", cols.Take(8))}{(cols.Count > 8 ? ", ..." : "")})." : $"{list.Count} row(s) use {e.Name} {id} ({e.Table.Label(row, e.Locale)}):");
                foreach (var u in list) r.Line("  " + u.Text);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc diff",
            Summary = "\"Compare with game\": your version of a table row by row against the game's file",
            Args = new[] { "<table>" },
            Options = new[] { Locale },
            Examples = new[] { "wds dbc diff Spell", "wds dbc diff TaxiNodes --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Open(inv, s, inv.Arg(0));
                if (e.GameTable == null) return CliResult.Fail(ExitCode.NotFound, "The game has no such file to compare with.");
                var d = e.CompareWithGame();
                var r = new CliResult().With(new { table = e.Name, modified = s.Workspace!.IsModified(DbcService.PathOf(e.Name)), diff = DiffJson(d) });
                r.Line($"{e.Name}.dbc: your version and the game's: {d.Summary}");
                DiffLines(r, d, 200);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc copy-to-server",
            Summary = "\"Copy to server...\": copy your edited tables that the server reads from its dbc folder (backed up first; refused while mangosd runs; needs --yes)",
            Options = new[] { new OptionSpec("tables", "list", "only these tables (comma-separated; default: every edited table the server reads)"), Common.DryRun },
            Details = "Tables the server takes from its database (Spell, Map, AreaTable, ...) are never copied: edit the server's database table instead. Restart the world server afterwards.",
            Examples = new[] { "wds dbc copy-to-server --dry-run", "wds dbc copy-to-server --yes", "wds dbc copy-to-server --tables TaxiPath,TaxiPathNode --yes --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var copy = s.DbcServerCopy()!;
                var plan = copy.Plan(s.Workspace!);
                var running = copy.RunningServers();
                var r = new CliResult();
                r.Line($"The server's dbc folder: {copy.ServerDbcDir}" + (Directory.Exists(copy.ServerDbcDir) ? "" : "  (this folder does not exist)"));
                r.Line(running.Count > 0 ? $"The world server is running ({string.Join(", ", running)}): stop it before copying." : "The world server is not running.");
                foreach (var i in plan) r.Line($"  {i.Table}: {i.StatusText}{(i.Note.Length > 0 ? " " + i.Note : "")}");
                if (plan.Count == 0) r.Line("You have no edited tables yet. Edit a table first (wds dbc set ...).");
                var tables = inv.Opt("tables")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                r.With(new { serverDbc = copy.ServerDbcDir, serverRunning = running, items = plan.Select(i => new { table = i.Table, use = i.Use, canCopy = i.CanCopy, identical = i.Identical, serverHasFile = i.ServerHasFile, status = i.StatusText, note = i.Note }), copied = false });
                if (inv.DryRun || plan.Count == 0) return r;
                if (running.Count > 0) { r.Code = ExitCode.Refused; r.Error = "Refused: the world server is running. Stop it first: it reads its dbc files only when it starts, and they must not change under it."; return r; }
                if (!inv.Yes) return Common.NeedsYes(r, "This writes into the server's dbc folder (after a backup).");
                var (rec, done) = copy.Copy(s.Workspace!, tables);
                foreach (var d in done) r.Line(d);
                return r.With(new { serverDbc = copy.ServerDbcDir, copied = true, backup = rec.Backup, written = rec.Written.Keys, added = rec.Added, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "dbc undo-server-copy",
            Summary = "\"Undo last copy to server\": put the server's previous files back (refused while mangosd runs; needs --yes)",
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds dbc undo-server-copy --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var copy = s.DbcServerCopy()!;
                var last = copy.LoadHistory().LastOrDefault(h => h.Undone == null);
                if (last == null) return new CliResult().Line("No copy to the server to undo.").With(new { undone = false });
                var r = new CliResult().Line($"Put back the server's files from before the copy of {last.Time:yyyy-MM-dd HH:mm} ({string.Join(", ", last.Written.Keys)}) from {last.Backup}.");
                if (inv.DryRun) return r.With(new { undone = false, backup = last.Backup, files = last.Written.Keys });
                var running = copy.RunningServers();
                if (running.Count > 0) return CliResult.Fail(ExitCode.Refused, $"Refused: the world server is running ({string.Join(", ", running)}). Stop it first.");
                if (!inv.Yes) return Common.NeedsYes(r, "This changes the server's dbc folder.");
                var done = copy.Undo(last);
                foreach (var d in done) r.Line(d);
                return r.With(new { undone = true, backup = last.Backup, files = last.Written.Keys, done });
            },
        };
    }
}
