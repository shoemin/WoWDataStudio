using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;
using WoWData.Core.Session;

namespace WoWData.Cli.Commands;

/// <summary>server: the Server data tab (0.9.0). The server's world database: browse, edit with checks (saved into your
/// workspace at once: there is no unsaved state on the command line; --dry-run shows a change and saves nothing), check
/// it against the game's tables and make them match, export an SQL patch, apply it to the server (backed up, logged,
/// undoable), import / export CSV and JSON, clear the game's cache. Nothing reaches the server without "server apply
/// --yes"; the characters database is never touched.</summary>
internal static class ServerCommands
{
    private static readonly OptionSpec SetOpt = new("set", "column=value", "a value (checked like the app: type, range, choices by name, links that exist; \\N = NULL)", true);
    private static readonly OptionSpec WhereOpt = new("where", "column=value", "a filter: column=value (also !=, >, >=, <, <=, ~ for \"contains\", a,b,c for one of)", true);

    private static async Task<(AppSession S, ServerDataService Svc)> Open(Invocation inv)
    {
        var s = await inv.Ctx.SessionAsync();
        var svc = s.ServerData ?? throw new CliError(ExitCode.Error, "No game folder is open.");
        if (!s.Settings.Server.Enabled) throw new CliError(ExitCode.Error, "The server database is turned off: wds settings set server.enabled true (and server.host, server.port, server.user, server.password, server.database).");
        _ = svc.Schema;   // connects (a clear error when it cannot)
        return (s, svc);
    }

    private static WorldTable Table(ServerDataService svc, string name) =>
        svc.Schema.Table(name) ?? throw new CliError(ExitCode.NotFound, $"the world database has no table \"{name}\" (\"wds server views\" lists the views and their tables).");

    private static List<(string, string?)> Sets(Invocation inv, string option = "set") => inv.Opts(option).Select(kv =>
    {
        int eq = kv.IndexOf('=');
        if (eq <= 0) throw new CliError(ExitCode.Usage, $"--{option} is column=value (not {kv}).");
        var v = kv[(eq + 1)..];
        return (kv[..eq].Trim(), v == WorldValue.NullText ? null : (string?)v);
    }).ToList();

    private static RowFilter Where(Invocation inv)
    {
        var f = RowFilter.Where(inv.Opts("where").Select(RowFilter.ParseTerm).ToArray());
        return inv.Opts("where").Count == 0 ? RowFilter.All() : f;
    }

    private static object RowJson(WorldTable t, IReadOnlyList<string?> row) => t.Columns.Select((c, i) => (c.Name, row[i])).ToDictionary(x => x.Name, x => x.Item2);

    private static object DiffJson(DbcDiffResult d) => new
    {
        summary = d.Summary, changed = d.Changed, added = d.Added, removed = d.Removed,
        rows = d.Rows.Take(1000).Select(x => new { kind = x.Kind, key = x.Key, label = x.Label, cells = x.Cells.Select(c => new { column = c.Column, before = c.Before, after = c.After }) }),
    };

    private static void Problems(CliResult r, IEnumerable<ServerProblem> problems)
    {
        foreach (var p in problems.Where(p => !p.IsError).Distinct()) r.Warnings.Add($"{p.Column}: {p.Message}");
    }

    /// <summary>Save the editor into the workspace (or, with --dry-run, show the edits and save nothing).</summary>
    private static CliResult SaveOrPreview(Invocation inv, ServerEditor ed, CliResult r, object change)
    {
        var dirty = ed.DirtyTables();
        foreach (var t in dirty)
            if (ed.ChangesOf(t) is { } ch && ch.Rows.Count > 0)
            {
                var d = ServerExchange.Diff(ed, ch);
                r.Line($"{t}: your edits compared with the server: {d.Summary}");
            }
        if (inv.DryRun)
        {
            var blocking = ed.BlockingProblems();
            foreach (var b in blocking) r.Warnings.Add("would be refused when saving: " + b);
            r.Line("Dry run: nothing was saved.");
            return r.With(new { saved = false, tables = dirty, change });
        }
        try
        {
            var (recs, msg) = ed.Save();
            r.Line(msg);
            return r.With(new { saved = recs.Count > 0, tables = dirty, changeSet = recs.FirstOrDefault()?.ChangeSet, files = recs.Select(x => x.Path), change });
        }
        catch (ServerDataException e)
        {
            return new CliResult { Code = ExitCode.ValidationFailed, Error = e.Message }.With(new { saved = false, change });
        }
    }

    private static string Key(ServerEditor ed, WorldTable t, string text)
    {
        try { return ed.ResolveKey(t, text); }
        catch (ServerDataException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "server status",
            Summary = "the server's world database: connection, schema, the content patch, the custom ID range, whether mangosd runs, your server edits and the last apply",
            Examples = new[] { "wds server status", "wds server status --json" },
            Run = async inv =>
            {
                var (s, svc) = await Open(inv);
                var schema = svc.Schema;
                var ed = svc.NewEditor();
                var running = svc.Running();
                var last = new ServerApplier(svc).Last();
                var r = new CliResult().With(new
                {
                    database = svc.Database, server = $"{svc.Db.Settings.Host}:{svc.Db.Settings.Port}", schema = schema.Describe, vmangos = schema.IsVmangos, migration = schema.Migration, mysql = schema.ServerVersion,
                    tables = schema.Tables.Count, described = svc.Layouts.Tables.Count(t => schema.Table(t.Name) != null), patch = svc.Patch, patchSource = svc.PatchText,
                    customIds = new { start = svc.CustomIds.Start, end = svc.CustomIds.End }, serverFolder = svc.ServerFolder, mangosConf = svc.MangosConf, serverRunning = running,
                    edits = ed.Changes.Select(c => new { table = c.Table.Name, changed = c.Changed, added = c.Added, deleted = c.Deleted }),
                    lastApply = last == null ? null : new { time = last.Time, rows = last.Rows, summary = last.Summary, folder = last.Folder },
                });
                r.Line($"World database: {svc.Db.Describe} · {schema.Describe}");
                r.Line($"{schema.Tables.Count} tables, {svc.Layouts.Tables.Count(t => schema.Table(t.Name) != null)} of them described by WoW Data Studio (the others are shown as stored).");
                r.Line($"The server runs content {svc.PatchText}; new rows get IDs from {svc.CustomIds.Start} to {svc.CustomIds.End} (server.custom-ids).");
                r.Line(running.Count > 0 ? $"The world server is running ({string.Join(", ", running)})." : "The world server is not running.");
                r.Line(ed.Changes.Count == 0 ? "You have no server edits." : "Your server edits (in your workspace): " + string.Join("; ", ed.Changes.Select(c => c.Summary)));
                if (last != null) r.Line($"Last apply: {last.Time:yyyy-MM-dd HH:mm}, {last.Rows} row(s): {last.Summary}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server views",
            Summary = "the views of the Server data tab (items, creatures, quests...) and their tables, with how much WoW Data Studio describes",
            Examples = new[] { "wds server views", "wds server views --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var r = new CliResult().With(new
                {
                    views = ServerBrowser.Views.Select(v => new { id = v.Id, label = v.Label, about = v.About, tables = v.Tables }),
                    tables = svc.Schema.Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new
                    {
                        name = t.Name, engine = t.Engine, rows = t.RowsApprox, key = t.Key, keyKind = t.KeyKind, columns = t.Columns.Count,
                        described = svc.Info(t.Name)?.Columns.Count(c => t.Has(c.Name)) ?? 0, about = svc.Info(t.Name)?.About, versioning = svc.Info(t.Name)?.Versioning, reload = svc.Info(t.Name)?.Reload,
                    }),
                });
                foreach (var v in ServerBrowser.Views) r.Line($"{v.Id,-18} {v.Label}: {v.About}" + (v.Tables.Count > 0 ? $" [{string.Join(", ", v.Tables)}]" : ""));
                int full = 0, part = 0, raw = 0;
                foreach (var t in svc.Schema.Tables.Values)
                {
                    int d = svc.Info(t.Name)?.Columns.Count(c => t.Has(c.Name)) ?? 0;
                    if (d == 0) raw++;
                    else if (d >= t.Columns.Count) full++;
                    else part++;
                }
                r.Line($"Tables: {full} fully described, {part} partly, {raw} shown as stored (wds server views --json lists them).");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server find",
            Summary = "a view's list: everything, an ID, or names with these words (the search box of the Server data tab)",
            Args = new[] { "<view>", "[words...]" },
            Options = new[] { new OptionSpec("table", "name", "the table of a view with several (loot: creature_loot_template..., locales: locales_item..., tables: any table)"), Common.Limit },
            Examples = new[] { "wds server find items shortsword", "wds server find creatures 3098", "wds server find loot --table skinning_loot_template", "wds server find vendors duokna --json", "wds server find tables --table map_template" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var b = new ServerBrowser(ed);
                List<ServerListItem> list;
                try { list = b.Search(inv.Arg(0), string.Join(" ", inv.Args.Skip(1)), inv.Opt("table"), Common.LimitOf(inv) == int.MaxValue ? 100000 : Common.LimitOf(inv)); }
                catch (ServerDataException e) { return CliResult.Fail(ExitCode.NotFound, e.Message); }
                var r = new CliResult().With(new { view = inv.Arg(0), results = list.Select(x => new { table = x.Table, key = x.Key, id = x.Id, name = x.Name, info = x.Info, loaded = x.Loaded }) });
                foreach (var l in Common.Table(new[] { "ID", "Name", "Info", "Table", "Key" }, list.Select(x => (IReadOnlyList<string>)new[] { x.Id.ToString(CultureInfo.InvariantCulture), x.Name, x.Info, x.Table, x.Key }), 50)) r.Line(l);
                r.Line($"{list.Count} result(s).");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server show",
            Summary = "one record with everything the details panel shows: its values (named, typed, links), related rows (spawns, loot, vendor lists, quest givers...), Used by and the game's values",
            Args = new[] { "<table>", "<key>" },
            Options = new[] { new OptionSpec("raw", null, "the stored values only"), new OptionSpec("no-sections", null, "only the values"), Common.Limit },
            Details = "<key> is an ID (for a table with versions: the row the server uses), or column=value pairs: entry=25,patch=0.",
            Examples = new[] { "wds server show creature_template 3098", "wds server show item_template entry=25,patch=0 --json", "wds server show spell_template 133" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var key = Key(ed, t, inv.Arg(1));
                var rec = new ServerBrowser(ed).Record(t.Name, key);
                bool raw = inv.Flag("raw");
                int limit = Common.LimitOf(inv, 30);
                var r = new CliResult().With(new
                {
                    table = t.Name, key, title = rec.Title, subtitle = rec.Subtitle, loaded = rec.Loaded, edited = rec.IsEdited, icon = rec.Icon, browse = rec.BrowseKey, reload = rec.ReloadText,
                    versions = rec.Versions.Select(v => new { key = v.Key, text = v.Info, loaded = v.Loaded }),
                    values = rec.Fields.Select(f => new { column = f.Name, type = f.TypeText, value = f.Value, shown = f.Display, group = f.Group, described = f.Described, hint = f.Hint, link = f.LinkTarget, linkId = f.LinkId, linkMissing = f.LinkMissing, problem = f.Problem }),
                    sections = inv.Flag("no-sections") ? null : rec.Sections.Select(s => new { title = s.Title, table = s.Table.Name, note = s.Note, rows = s.Rows.Select(x => s.Table.KeyText(x)), lines = s.RowTexts, truncated = s.Truncated }),
                    usedBy = rec.UsedBy.Select(u => new { table = u.Table, column = u.Column, key = u.Key, label = u.Label }),
                    client = rec.Client.Count == 0 ? null : new { table = rec.ClientTable, fields = rec.Client.Select(c => new { server = c.ServerColumn, serverValue = c.ServerValue, client = c.ClientColumn, clientValue = c.ClientValue, same = c.Same }) },
                });
                r.Line($"{rec.Title}  ({rec.Subtitle})");
                if (rec.Versions.Count > 1) r.Line("Versions: " + string.Join("; ", rec.Versions.Select(v => $"{v.Key} {v.Info}")));
                string group = "";
                foreach (var f in rec.Fields)
                {
                    if (f.Group != group) r.Line($"== {group = f.Group}");
                    r.Line($"  {f.Name,-28} {(raw ? f.Value ?? "NULL" : f.Display)}" + (f.Problem.Length > 0 ? $"   <- {f.Problem}" : ""));
                }
                if (rec.Client.Count > 0)
                {
                    int diff = rec.Client.Count(c => !c.Same);
                    r.Line($"== In the game ({rec.ClientTable}.dbc): " + (diff == 0 ? "the same values" : $"{diff} value(s) differ"));
                    foreach (var c in rec.Client.Where(c => !c.Same)) r.Line("  " + c.Text);
                }
                if (!inv.Flag("no-sections"))
                    foreach (var s in rec.Sections.Where(s => s.Rows.Count > 0 || s.Note.Length > 0))
                    {
                        r.Line($"== {s.Title} ({s.Table.Name}, {s.Rows.Count}{(s.Truncated ? "+" : "")} row(s))" + (s.Note.Length > 0 ? " - " + s.Note : ""));
                        foreach (var l in s.RowTexts.Take(limit)) r.Line("  " + l);
                        if (s.RowTexts.Count > limit) r.Line($"  ... and {s.RowTexts.Count - limit} more (--limit 0 for all)");
                    }
                if (rec.UsedBy.Count > 0)
                {
                    r.Line($"== Used by ({rec.UsedBy.Count})");
                    foreach (var u in rec.UsedBy.Take(limit)) r.Line("  " + u.Text);
                }
                r.Line(rec.ReloadText);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server get",
            Summary = "one row's values (or one value), with your edits",
            Args = new[] { "<table>", "<key>", "[column]" },
            Options = new[] { new OptionSpec("raw", null, "the stored value only (no names of choices and links)") },
            Examples = new[] { "wds server get item_template 25 name", "wds server get creature_template 3098 --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var key = Key(ed, t, inv.Arg(1));
                var row = ed.Current(t, key) ?? throw new CliError(ExitCode.NotFound, $"{t.Name} has no row {key}.");
                var b = new ServerBrowser(ed);
                var fields = b.Fields(t, row);
                if (inv.ArgOrNull(2) is { } col)
                {
                    var f = fields.FirstOrDefault(x => string.Equals(x.Name, col, StringComparison.OrdinalIgnoreCase)) ?? throw new CliError(ExitCode.NotFound, $"{t.Name} has no column \"{col}\".");
                    var text = inv.Flag("raw") ? f.Value ?? WorldValue.NullText : f.Display;
                    return new CliResult().With(new { table = t.Name, key, column = f.Name, value = f.Value, shown = f.Display, type = f.TypeText }).Line(text);
                }
                var r = new CliResult().With(new { table = t.Name, key, values = RowJson(t, row) });
                foreach (var f in fields) r.Line($"{f.Name,-28} {(inv.Flag("raw") ? f.Value ?? WorldValue.NullText : f.Display)}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server set",
            Summary = "change values of one row (checked like the app) and save it into your workspace (the server is not changed: \"server apply\" does that)",
            Args = new[] { "<table>", "<key>", "[column]", "[value]" },
            Options = new[] { SetOpt, Common.DryRun },
            Details = "Give one column and value, or several with --set column=value. Choices and flags may be given by name (quality=Rare, npc_flags=\"Vendor|Repairs items\"), money as \"1g 20s\", NULL as \\N. A refused value is not stored (exit code 3).",
            Examples = new[] { "wds server set item_template 25 dmg_max1 12", "wds server set creature_template 3098 --set level_min=3 --set level_max=4", "wds server set item_template 25 quality Rare --dry-run --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var key = Key(ed, t, inv.Arg(1));
                var values = Sets(inv);
                if (inv.ArgOrNull(2) is { } col)
                {
                    var v = inv.ArgOrNull(3) ?? throw new CliError(ExitCode.Usage, "give the value after the column (or use --set column=value).");
                    values.Insert(0, (col, v == WorldValue.NullText ? null : v));
                }
                if (values.Count == 0) throw new CliError(ExitCode.Usage, "give a column and a value, or --set column=value.");
                var before = ed.Current(t, key)!;
                ServerEditResult res;
                try { res = ed.Set(t.Name, key, values); }
                catch (ServerDataException e) { return CliResult.Fail(ExitCode.NotFound, e.Message); }
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, $"{t.Name} {key}: {res.Message}");
                var r = new CliResult();
                foreach (var (c, _) in values)
                {
                    var i = t.IndexOf(c);
                    var nowRow = ed.Current(t, t.KeyText(ApplyKey(t, before, values)));
                    r.Line($"{t.Name} {key}: {t.Columns[i].Name} {before[i] ?? "NULL"} -> {nowRow?[i] ?? "NULL"}");
                }
                Problems(r, res.Problems);
                if (res.Changed == 0) return r.Line("Nothing changed.").With(new { saved = false, unchanged = true });
                return SaveOrPreview(inv, ed, r, new { table = t.Name, key, values = values.Select(v => new { column = v.Item1, value = v.Item2 }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "server add",
            Summary = "\"Add new\": a new row with the next free ID of your custom ID range (or --id), values from --set, saved into your workspace",
            Args = new[] { "<table>" },
            Options = new[] { new OptionSpec("id", "n", "the new row's ID (default: the first free one of server.custom-ids)"), SetOpt, Common.DryRun },
            Examples = new[] { "wds server add npc_vendor --set entry=900000 --set item=25", "wds server add creature_template --set \"name=Grokk the Merchant\" --set display_id1=1879 --set npc_flags=Vendor --dry-run" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                long? id = inv.Opt("id") is { } x ? long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, "--id is a number.") : null;
                var (key, res) = ed.Add(t.Name, Sets(inv), id);
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                var r = new CliResult().Line(res.Message);
                Problems(r, res.Problems);
                return SaveOrPreview(inv, ed, r, new { table = t.Name, key, row = RowJson(t, ed.Current(t, key!)!) });
            },
        };
        yield return new CommandSpec
        {
            Path = "server duplicate",
            Summary = "\"Duplicate as new\": a copy of a row with the next free ID of your custom ID range (or --id), changed by --set, saved into your workspace",
            Args = new[] { "<table>", "<key>" },
            Options = new[] { new OptionSpec("id", "n", "the copy's ID (default: the first free one of server.custom-ids)"), SetOpt, Common.DryRun },
            Examples = new[] { "wds server duplicate creature_template 3158 --set \"name=Grokk\" --set subname=\"Trade Goods\"", "wds server duplicate item_template 25 --id 900100 --set \"name=Sharp Shortsword\" --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var src = Key(ed, t, inv.Arg(1));
                long? id = inv.Opt("id") is { } x ? long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, "--id is a number.") : null;
                var (key, res) = ed.Duplicate(t.Name, src, Sets(inv), id);
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                var r = new CliResult().Line(res.Message);
                Problems(r, res.Problems);
                return SaveOrPreview(inv, ed, r, new { table = t.Name, source = src, key, row = RowJson(t, ed.Current(t, key!)!) });
            },
        };
        yield return new CommandSpec
        {
            Path = "server delete",
            Summary = "\"Delete\": delete rows (says first what refers to them; needs --yes)",
            Args = new[] { "<table>", "<keys...>" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds server delete npc_vendor entry=3158,item=159 --dry-run", "wds server delete creature_template 900000 --yes" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var keys = inv.Args.Skip(1).Select(k => Key(ed, t, k)).Distinct().ToList();
                var used = ed.UsedBy(t.Name, keys);
                var r = new CliResult().Line(keys.Count == 1 ? $"Delete {t.Name} {keys[0]}" : $"Delete {keys.Count} rows of {t.Name}");
                if (used.Count > 0)
                {
                    r.Line("These rows refer to them (they would point at nothing):");
                    foreach (var u in used) r.Line("  " + u);
                    r.Warnings.Add($"{used.Count} row(s) of other tables refer to them.");
                }
                if (!inv.DryRun && !inv.Yes) return Common.NeedsYes(r.With(new { table = t.Name, keys, usedBy = used, saved = false }), "This deletes rows (in your workspace; the server is changed only by \"wds server apply\").");
                r.Line(ed.Delete(t.Name, keys).Message);
                return SaveOrPreview(inv, ed, r, new { table = t.Name, keys, usedBy = used });
            },
        };
        yield return new CommandSpec
        {
            Path = "server bulk",
            Summary = "\"Bulk edit...\": set, add to or multiply one column of many rows (all checked first; nothing changes if one is refused)",
            Args = new[] { "<table>" },
            Options = new[]
            {
                new OptionSpec("column", "name", "the column to change (required)"), new OptionSpec("op", "set|add|multiply", "what to do (required)"), new OptionSpec("value", "v", "the value or the number (required)"),
                WhereOpt, new OptionSpec("keys", "list", "only these keys (separated by spaces or |; e.g. \"25|117\")"), Common.DryRun,
            },
            Details = "The rows are chosen with --where (and/or --keys); without either nothing is changed.",
            Examples = new[] { "wds server bulk item_template --where quality=0 --where sell_price>0 --column sell_price --op multiply --value 2 --dry-run", "wds server bulk creature_loot_template --where entry=3098 --column ChanceOrQuestChance --op multiply --value 1.5" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                if (inv.Opts("where").Count == 0 && inv.Opt("keys") == null) throw new CliError(ExitCode.Usage, "choose the rows with --where or --keys.");
                var col = inv.Opt("column") ?? throw new CliError(ExitCode.Usage, "give --column.");
                var op = (inv.Opt("op") ?? throw new CliError(ExitCode.Usage, "give --op set|add|multiply.")).ToLowerInvariant() switch
                {
                    "set" => BulkOperation.Set, "add" => BulkOperation.Add, "multiply" => BulkOperation.Multiply,
                    var x => throw new CliError(ExitCode.Usage, $"--op is set, add or multiply (not {x})."),
                };
                var value = inv.Opt("value") ?? throw new CliError(ExitCode.Usage, "give --value.");
                var keys = new List<string>();
                if (inv.Opts("where").Count > 0) keys.AddRange(ed.Rows(t, Where(inv)).Select(t.KeyText));
                if (inv.Opt("keys") is { } kl) keys.AddRange(kl.Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(k => Key(ed, t, k)));
                if (keys.Count == 0) return CliResult.Fail(ExitCode.NotFound, "No row matches: nothing was changed.");
                ServerEditResult res;
                try { res = ed.Bulk(t.Name, keys, col, op, value); }
                catch (ServerDataException e) { return CliResult.Fail(ExitCode.NotFound, e.Message); }
                if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                var r = new CliResult().Line($"{op} {col} of {Common.Plural(keys.Distinct().Count(), "row")}: {res.Message}");
                Problems(r, res.Problems);
                return SaveOrPreview(inv, ed, r, new { table = t.Name, column = col, op, value, rows = keys.Distinct().Count(), changed = res.Changed });
            },
        };
        yield return new CommandSpec
        {
            Path = "server used-by",
            Summary = "\"Used by\": the rows of every table that refer to an item, creature, quest, spell, game object... (from the meaning of every described column)",
            Args = new[] { "<table>", "<key>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds server used-by item_template 25", "wds server used-by creature_template 3098 --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var kind = svc.KindOf(t.Name) ?? throw new CliError(ExitCode.NotFound, $"Nothing refers to rows of {t.Name} by an ID.");
                var key = Key(ed, t, inv.Arg(1));
                var row = ed.Current(t, key)!;
                long id = WorldValue.Long(row[t.IndexOf(kind.KeyColumn)]);
                var list = new ServerBrowser(ed).UsedBy(kind.Name, id, Common.LimitOf(inv, 300) == int.MaxValue ? 100000 : Common.LimitOf(inv, 300));
                var r = new CliResult().With(new { table = t.Name, kind = kind.Name, id, usedBy = list.Select(u => new { table = u.Table, column = u.Column, key = u.Key, label = u.Label }) });
                r.Line(list.Count == 0 ? $"Nothing refers to {kind.Plain} {id}." : $"{list.Count} row(s) refer to {kind.Plain} {id}:");
                foreach (var u in list) r.Line("  " + u.Text);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server changes",
            Summary = "\"Compare with the server\": your saved server edits, row by row (before on the server, after in your workspace)",
            Args = new[] { "[table]" },
            Examples = new[] { "wds server changes", "wds server changes item_template --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var list = ed.Changes.Where(c => inv.ArgOrNull(0) == null || string.Equals(c.Table.Name, inv.Arg(0), StringComparison.OrdinalIgnoreCase)).ToList();
                var r = new CliResult().With(list.Select(c => new { table = c.Table.Name, file = ServerChangeFiles.PathOf(svc.Database, c.Table.Name), diff = DiffJson(ServerExchange.Diff(ed, c)) }));
                if (list.Count == 0) r.Line("You have no server edits" + (inv.ArgOrNull(0) != null ? $" of {inv.Arg(0)}." : "."));
                foreach (var c in list)
                {
                    var d = ServerExchange.Diff(ed, c);
                    r.Line($"{c.Table.Name}: {d.Summary}");
                    foreach (var row in d.Rows.Take(100)) r.Line($"  {row.KindText,-8} {row.Key} {row.Label}: {row.DetailsText}");
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server check",
            Summary = "\"Check client and server...\": the game's tables next to the server's (spells, item and creature looks, maps, areas, factions, faction templates, flight paths, skills), every difference",
            Args = new[] { "[area]" },
            Options = new[] { new OptionSpec("ids", "list", "only these IDs (comma-separated)"), Common.Limit },
            Details = "Areas: " + string.Join(", ", ServerConsistency.Areas) + ". Fix a difference with \"wds server match <area> <id> --to server|client\".",
            Examples = new[] { "wds server check spells --ids 133,143", "wds server check creatures", "wds server check --limit 0 --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var ids = inv.Opt("ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, $"--ids are numbers (not {x}).")).ToHashSet();
                List<ConsistencyIssue> issues;
                try { issues = ServerConsistency.Check(ed, inv.ArgOrNull(0), ids, Common.LimitOf(inv, 200) == int.MaxValue ? int.MaxValue : Common.LimitOf(inv, 200)); }
                catch (ServerDataException e) { return CliResult.Fail(ExitCode.NotFound, e.Message); }
                var r = new CliResult().With(new
                {
                    issues = issues.Select(i => new
                    {
                        area = i.Area, kind = i.Kind, serverTable = i.ServerTable, serverKey = i.ServerKey, clientTable = i.ClientTable, id = i.Id, name = i.Name, canMatchServer = i.CanMatchServer, canMatchClient = i.CanMatchClient, advice = i.Advice,
                        fields = i.Fields.Where(f => !f.Same).Select(f => new { server = f.ServerColumn, serverValue = f.ServerValue, client = f.ClientColumn, clientValue = f.ClientValue }),
                    }),
                });
                r.Line(issues.Count == 0 ? "The game and the server agree." : $"{issues.Count} difference(s):");
                foreach (var i in issues) r.Line("  " + i.Text);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server match",
            Summary = "\"Make the server match the game\" / \"Make the game match the server\": fix one difference found by \"server check\" (an edit in your workspace)",
            Args = new[] { "<area>", "<id>" },
            Options = new[] { new OptionSpec("to", "server|client", "which side changes: server (it takes the game's values) or client (the game's table takes the server's values) (required)"), Common.DryRun },
            Examples = new[] { "wds server match spells 133 --to client", "wds server match creatures 1879 --to server --dry-run" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                long id = inv.Long(1);
                var to = (inv.Opt("to") ?? throw new CliError(ExitCode.Usage, "give --to server or --to client.")).ToLowerInvariant();
                if (to is not ("server" or "client" or "game")) throw new CliError(ExitCode.Usage, "--to is server or client.");
                var issue = ServerConsistency.Check(ed, inv.Arg(0), new HashSet<long> { id }).FirstOrDefault(i => to == "server" ? i.CanMatchServer : i.CanMatchClient)
                            ?? ServerConsistency.Check(ed, inv.Arg(0), new HashSet<long> { id }).FirstOrDefault();
                if (issue == null) return new CliResult().Line($"{inv.Arg(0)} {id}: the game and the server already agree.").With(new { changed = false });
                var r = new CliResult().Line(issue.Text);
                if (to == "server")
                {
                    var res = ServerConsistency.MatchServer(ed, issue);
                    if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                    r.Line("The server's row takes the game's values: " + res.Message);
                    Problems(r, res.Problems);
                    return SaveOrPreview(inv, ed, r, new { area = issue.Area, id, to = "server" });
                }
                if (inv.DryRun) return r.Line($"Dry run: the game's {issue.ClientTable} {id} would take the server's values. Nothing was saved.").With(new { area = issue.Area, id, to = "client", saved = false });
                var msg = ServerConsistency.MatchClient(ed, issue);
                return r.Line(msg).With(new { area = issue.Area, id, to = "client", saved = true });
            },
        };
        yield return new CommandSpec
        {
            Path = "server export-sql",
            Summary = "\"Export SQL patch...\": your server edits as an idempotent .sql file (with a header) and its revert script (.revert.sql)",
            Options = new[] { new OptionSpec("out", "file", "the .sql file to write (required); the revert script is written next to it") },
            Examples = new[] { "wds server export-sql --out my_changes.sql" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var file = Path.GetFullPath(inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "give --out <file.sql>."));
                var changes = svc.SavedChanges();
                if (changes.Count == 0) return CliResult.Fail(ExitCode.NotFound, "You have no server edits to export.");
                var script = ServerSql.Build(svc, changes);
                var revert = RevertName(file);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, script.Patch, new System.Text.UTF8Encoding(false));
                File.WriteAllText(revert, script.Revert, new System.Text.UTF8Encoding(false));
                return new CliResult().With(new { file, revert, rows = script.Rows, tables = script.Tables, statements = script.Statements.Count })
                    .Line($"Wrote {file} ({script.Rows} row(s): {script.Summary}) and its revert script {revert}.");
            },
        };
        yield return new CommandSpec
        {
            Path = "server apply",
            Summary = "\"Apply to server...\": write your server edits into the world database (shows the statements; backs up every affected row and table first; one transaction; logged; needs --yes)",
            Options = new[] { new OptionSpec("overwrite", null, "apply although rows changed on the server since you started editing them"), new OptionSpec("show-sql", null, "print every statement"), Common.DryRun },
            Details = "Only the world database is written (never the characters database). The world server reads most tables when it starts: restart it, or use the .reload commands listed. Undo with \"wds server undo-apply\".",
            Examples = new[] { "wds server apply --dry-run --show-sql", "wds server apply --yes", "wds server apply --yes --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ap = new ServerApplier(svc);
                var plan = ap.Plan();
                var r = new CliResult();
                if (plan.IsEmpty) return r.Line("You have no server edits to apply.").With(new { applied = false });
                r.Line($"Apply {plan.Script.Rows} row(s) to {plan.Target}: {plan.Script.Summary}");
                if (inv.Flag("show-sql")) foreach (var s in plan.Script.Statements) r.Line("  " + s);
                else r.Line($"  {plan.Script.Statements.Count} statement(s) (--show-sql prints them).");
                foreach (var c in plan.Conflicts) r.Warnings.Add(c);
                if (plan.AlreadyApplied.Count > 0) r.Line($"{plan.AlreadyApplied.Count} row(s) are already as you want them on the server.");
                if (plan.Running.Count > 0) r.Warnings.Add($"The world server is running ({string.Join(", ", plan.Running)}): it keeps the old data until it restarts or reloads.");
                if (plan.NonTransactional.Count > 0) r.Line($"Note: {string.Join(", ", plan.NonTransactional)} cannot roll back (MyISAM): on a failure the backup rows are written back.");
                foreach (var n in plan.Notes) r.Line(n);
                var data = new { target = plan.Target, rows = plan.Script.Rows, tables = plan.Script.Tables, statements = plan.Script.Statements, conflicts = plan.Conflicts, alreadyApplied = plan.AlreadyApplied, serverRunning = plan.Running, notes = plan.Notes, applied = false };
                if (inv.DryRun) return r.Line("Dry run: nothing was written.").With(data);
                if (plan.Conflicts.Count > 0 && !inv.Flag("overwrite")) return new CliResult { Code = ExitCode.Refused, Error = $"Refused: {plan.Conflicts.Count} row(s) changed on the server since you started editing them (--overwrite applies anyway)." }.With(data);
                if (!inv.Yes) return Common.NeedsYes(r.With(data), "This writes into the server's world database (after a backup).");
                var (rec, done) = ap.Apply(plan, inv.Flag("overwrite"));
                foreach (var d in done) r.Line(d);
                return r.With(new { target = plan.Target, rows = rec.Rows, tables = rec.Tables, applied = true, backup = rec.Folder, log = svc.ApplyLogFile, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "server undo-apply",
            Summary = "\"Undo last apply\": put the rows of the last apply back as they were before it (from its backup; needs --yes)",
            Options = new[] { new OptionSpec("overwrite", null, "undo although rows changed on the server since the apply"), Common.DryRun },
            Examples = new[] { "wds server undo-apply --dry-run", "wds server undo-apply --yes" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ap = new ServerApplier(svc);
                var last = ap.Last();
                if (last == null) return new CliResult().Line("No apply to undo.").With(new { undone = false });
                var conflicts = ap.UndoConflicts(last);
                var r = new CliResult().Line($"Undo the apply of {last.Time:yyyy-MM-dd HH:mm} ({last.Rows} row(s): {last.Summary}) from its backup {last.BackupRowsFile}.");
                foreach (var c in conflicts) r.Warnings.Add(c);
                var data = new { time = last.Time, rows = last.Rows, backup = last.Folder, conflicts, undone = false };
                if (inv.DryRun) return r.With(data);
                if (conflicts.Count > 0 && !inv.Flag("overwrite")) return new CliResult { Code = ExitCode.Refused, Error = $"Refused: {conflicts.Count} row(s) changed on the server since the apply (--overwrite undoes anyway)." }.With(data);
                if (!inv.Yes) return Common.NeedsYes(r.With(data), "This writes into the server's world database (after a backup).");
                var done = ap.Undo(last, inv.Flag("overwrite"));
                foreach (var d in done) r.Line(d);
                return r.With(new { time = last.Time, rows = last.Rows, undone = true, done });
            },
        };
        yield return new CommandSpec
        {
            Path = "server applies",
            Summary = "the applied-changes log: every apply to the server and its undo, with its backup folder",
            Examples = new[] { "wds server applies", "wds server applies --json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var hist = new ServerApplier(svc).History();
                var r = new CliResult().With(hist.Select(h => new { time = h.Time, database = h.Database, server = h.Server, rows = h.Rows, summary = h.Summary, folder = h.Folder, undone = h.Undone, result = h.Result }));
                if (hist.Count == 0) r.Line("Nothing was applied to the server yet.");
                foreach (var h in hist) r.Line($"{h.Time:yyyy-MM-dd HH:mm}  {h.Database} on {h.Server}: {h.Rows} row(s): {h.Summary}" + (h.Undone != null ? $"  (undone {h.Undone:yyyy-MM-dd HH:mm})" : "") + $"  [{h.Folder}]");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "server export",
            Summary = "export rows of a table as CSV (for spreadsheets) or JSON (by the file's extension), with your edits",
            Args = new[] { "<table>" },
            Options = new[] { new OptionSpec("out", "file", "the .csv or .json file to write (required)"), WhereOpt, Common.Limit },
            Details = "Every column is exported with its exact value (NULL is \\N in CSV, null in JSON): importing the file unchanged changes nothing.",
            Examples = new[] { "wds server export npc_vendor --where entry=3158 --out duokna.csv", "wds server export item_template --where name~Shortsword --out swords.json" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var file = Path.GetFullPath(inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "give --out <file.csv|file.json>."));
                var rows = ed.Rows(t, Where(inv), Common.LimitOf(inv, 0) == int.MaxValue ? 0 : Common.LimitOf(inv, 0));
                if (rows.Count == 0) return CliResult.Fail(ExitCode.NotFound, "There are no rows to export.");
                ServerExchange.Export(t, rows, file, svc.Database);
                return new CliResult().With(new { table = t.Name, file, rows = rows.Count }).Line($"Exported {rows.Count:N0} row(s) of {t.Name} to {file}.");
            },
        };
        yield return new CommandSpec
        {
            Path = "server import",
            Summary = "import a CSV or JSON file into a table: a preview of every changed, added and removed row; --apply imports it and saves it into your workspace",
            Args = new[] { "<table>", "<file>" },
            Options = new[]
            {
                new OptionSpec("remove-missing", null, "the file is the whole set: rows of --where (or of the table) that it does not have are removed"), WhereOpt,
                new OptionSpec("apply", null, "apply the import and save it (without it: the preview only)"),
            },
            Details = "Rows are matched by the table's key. Errors (a value that does not fit, a link to nothing, a key twice) block the import (exit code 3).",
            Examples = new[] { "wds server import npc_vendor duokna.csv", "wds server import npc_vendor duokna.csv --where entry=3158 --remove-missing --apply" },
            Run = async inv =>
            {
                var (_, svc) = await Open(inv);
                var ed = svc.NewEditor();
                var t = Table(svc, inv.Arg(0));
                var file = Path.GetFullPath(inv.Arg(1));
                if (!File.Exists(file)) throw new CliError(ExitCode.NotFound, $"{file} does not exist.");
                if (inv.Flag("remove-missing") && inv.Opts("where").Count == 0 && t.RowsApprox > 5000)
                    throw new CliError(ExitCode.Usage, $"{t.Name} has {t.RowsApprox:N0} rows: with --remove-missing say which rows the file stands for (--where entry=...).");
                var plan = ServerExchange.Plan(ed, t.Name, file, inv.Flag("remove-missing"), Where(inv));
                var r = new CliResult();
                r.Line($"Import {Path.GetFileName(file)} into {t.Name}: {plan.RowsRead:N0} row(s) read. " + (plan.IsEmpty ? "The file changes nothing." : plan.Preview.Summary));
                foreach (var row in plan.Preview.Rows.Take(60)) r.Line($"  {row.KindText,-8} {row.Key} {row.Label}: {row.DetailsText}");
                foreach (var e in plan.Errors.Take(60)) r.Line("  error: " + e);
                r.Warnings.AddRange(plan.Warnings.Take(100));
                var data = new { table = t.Name, file, rowsRead = plan.RowsRead, errors = plan.Errors, warnings = plan.Warnings, preview = DiffJson(plan.Preview), applied = false };
                if (plan.Errors.Count > 0) return new CliResult { Code = ExitCode.ValidationFailed, Error = $"{Common.Plural(plan.Errors.Count, "error")} block the import: fix the file and import again." }.With(data);
                if (!inv.Flag("apply") || plan.IsEmpty)
                {
                    if (!plan.IsEmpty) r.Line("Preview only: nothing was changed. --apply imports it and saves it.");
                    return r.With(data);
                }
                r.Line(ServerExchange.Apply(ed, plan));
                return SaveOrPreview(inv, ed, r, data);
            },
        };
        yield return new CommandSpec
        {
            Path = "server clear-wdb",
            Summary = "\"Clear the game's cache (WDB)...\": move the game's cached items, creatures, quests and objects out of its WDB folder (backed up; refused while the game runs; needs --yes)",
            Options = new[] { new OptionSpec("game", "folder", "the game folder whose cache to clear (default: the game folder of WoW Data Studio)"), Common.DryRun },
            Details = "The 1.12 game keeps what the server told it in WDB\\*.wdb and does not ask again, so server edits of names, stats or texts only show after the cache is cleared. The files are moved into the app's backup folder, not deleted.",
            Examples = new[] { "wds server clear-wdb --dry-run", "wds server clear-wdb --yes", "wds server clear-wdb --game \"C:\\Games\\WoW 1.12 test\" --yes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var game = Path.GetFullPath(inv.Opt("game") ?? s.Client!.ClientDir);
                var check = Core.Workspace.ClientValidator.Check(game);
                if (!check.Ok) return CliResult.Fail(ExitCode.NotFound, check.Message);
                var files = WdbCache.Files(game);
                var running = WdbCache.RunningGames(game);
                var r = new CliResult().Line($"The cache of {game}: " + (files.Count == 0 ? "empty." : string.Join(", ", files.Select(f => $"{f.Name} ({f.Length / 1024} KB)"))));
                var data = new { game, files = files.Select(f => f.Name), running, cleared = false };
                if (inv.DryRun || files.Count == 0) return r.With(data);
                if (running.Count > 0) return new CliResult { Code = ExitCode.Refused, Error = $"Refused: the game is running from this folder ({string.Join(", ", running)}). Close it first." }.With(data);
                if (!inv.Yes) return Common.NeedsYes(r.With(data), "This moves the game's cache files out of its WDB folder (into the app's backups).");
                var (backup, done) = WdbCache.Clear(game, Path.Combine(s.Client!.BackupDir, "wdb"));
                foreach (var d in done) r.Line(d);
                return r.With(new { game, files = files.Select(f => f.Name), cleared = true, backup });
            },
        };
    }

    /// <summary>The key of a row after values were set (a key column may have changed).</summary>
    private static string?[] ApplyKey(WorldTable t, string?[] before, List<(string, string?)> values)
    {
        var row = (string?[])before.Clone();
        foreach (var (c, v) in values)
            if (t.Key.Contains(c, StringComparer.OrdinalIgnoreCase))
            {
                var col = t.Column(c)!;
                row[t.IndexOf(c)] = WorldValue.Normalize(col, v, out var canon) == null ? canon : v;
            }
        return row;
    }

    public static string RevertName(string file) => Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".revert.sql");
}
