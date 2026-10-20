using WoWData.Core.Dbc;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;

namespace WoWData.Cli.Commands;

/// <summary>versions (0.14.0, experimental): the Other versions tab. Reads other World of Warcraft clients, never changes them.</summary>
internal static class VersionCommands
{
    private static readonly OptionSpec Folder = new("folder", "folder", "search this folder for clients instead of the other-clients setting");
    private static readonly OptionSpec BlizzardOnly = new("blizzard-only", null, "leave out patch archives Blizzard did not ship (a private server's or a mod's patch-?.MPQ); also the other-clients.blizzard-only setting");
    private static readonly OptionSpec AllPatches = new("all-patches", null, "read every patch archive the client mounts, even when other-clients.blizzard-only is set");

    private static string Root(Invocation inv) =>
        inv.Opt("folder") ?? inv.Ctx.Settings.OtherClientsDir
        ?? throw new CliError(ExitCode.Usage, "no folder of other clients: give --folder <folder> or set it once with \"wds settings set other-clients <folder>\".");

    private static List<ClientCopy> Found(Invocation inv)
    {
        var root = Root(inv);
        if (!Directory.Exists(root)) throw new CliError(ExitCode.NotFound, $"the folder \"{root}\" does not exist.");
        inv.Ctx.Progress($"Looking for clients in {root}...");
        return ClientCopies.Find(root);
    }

    private static ClientCopy Pick(Invocation inv, string text, List<ClientCopy>? found = null)
    {
        try
        {
            var t = text.Trim().Trim('"');
            return ClientPick.Resolve(t, Directory.Exists(t) || File.Exists(t) ? new List<ClientCopy>() : found ?? Found(inv));
        }
        catch (ArgumentException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    private static bool Blizzard(Invocation inv) => !inv.Flag("all-patches") && (inv.Flag("blizzard-only") || inv.Ctx.Settings.OtherClientsBlizzardOnly);

    private static OtherClient Open(Invocation inv, ClientCopy c)
    {
        try
        {
            inv.Ctx.Progress($"Opening {c.Name} ({c.Label})...");
            return OtherClient.Open(c, Blizzard(inv));
        }
        catch (NotSupportedException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    private static object CopyJson(ClientCopy c) => new
    {
        name = c.Name, folder = c.Folder, exe = Path.GetFileName(c.Exe), version = c.Version, build = c.Build, release = c.Release,
        format = c.Format.ToString().ToUpperInvariant(), layout = c.Layout.ToString(), locale = c.Locale, readable = c.Readable,
    };

    private static DbcTable Table(OtherClient oc, string name) =>
        oc.Table(name) ?? throw new CliError(ExitCode.NotFound, $"{oc.Copy.Name} has no table {name} (wds versions tables \"{oc.Copy.Name}\").");

    private static DbcColumn Col(DbcTable t, string name) =>
        t.Layout.Column(name) ?? throw new CliError(ExitCode.NotFound, $"{t.Name} has no column {name}. Its columns: {string.Join(", ", t.Layout.Columns.Select(c => c.Name))}.");

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "versions find",
            Summary = "the World of Warcraft clients in the folder of other versions: version, release, data format, readable or not",
            Options = new[] { Folder },
            NeedsClient = false,
            Examples = new[] { "wds versions find", "wds versions find --folder \"G:\\WoW Clients\" --json" },
            Run = inv =>
            {
                var found = Found(inv);
                var r = new CliResult().With(new { folder = Root(inv), clients = found.Select(CopyJson) });
                r.Line($"{Common.Plural(found.Count, "client")} in {Root(inv)}");
                foreach (var l in Common.Table(new[] { "Name", "Version", "Build", "Release", "Data", "Readable" },
                             found.Select(c => (IReadOnlyList<string>)new[] { c.Name, c.Version, c.Build.ToString(), c.Release, c.Format.ToString().ToUpperInvariant(), c.Readable ? "yes" : "not yet" }), 50))
                    r.Line(l);
                if (found.Any(c => c.Format == ClientFormat.Casc)) r.Line("CASC clients (6.x and later, and Battle.net clients) are found but cannot be read yet.");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions info",
            Summary = "one client: version, release, locale, its archives in the order the game reads them (patches that are not Blizzard's marked), files and tables",
            Args = new[] { "<client>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches },
            NeedsClient = false,
            Details = "<client> is the client's folder, its name in \"wds versions find\", a version (3.3.5, 2.4) or a build (12340).",
            Examples = new[] { "wds versions info 3.3.5", "wds versions info \"2.4.3.8606 - The Burning Crusade\" --json" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var names = oc.Data.Names();
                var tables = oc.TableNames();
                var named = tables.Count(t => oc.Layouts.Get(t) != null);
                var archives = ClientData.ClientArchiveOrder(c.DataDir);
                var r = new CliResult().With(new
                {
                    client = CopyJson(c), blizzardOnly = oc.BlizzardOnly, archives = archives.Select(a => new { name = a, mounted = oc.Data.Archives.Any(m => m.Name.Equals(a, StringComparison.OrdinalIgnoreCase)), blizzard = !oc.CustomArchives.Contains(a, StringComparer.OrdinalIgnoreCase) }),
                    files = names.Count, deleted = oc.Data.DeletedNames().Count, tables = tables.Count, tablesWithLayout = named, layoutBuild = oc.Layouts.Build,
                });
                r.Line($"{c.Name}: World of Warcraft {c.Version} (build {c.Build}), {c.Release}{(c.Locale != null ? $", {c.Locale}" : "")}");
                r.Line($"  folder   {c.Folder}");
                r.Line($"  archives (the game reads the first one that has a file):");
                int i = 1;
                foreach (var a in archives)
                {
                    bool custom = oc.CustomArchives.Contains(a, StringComparer.OrdinalIgnoreCase);
                    r.Line($"    {i++,2} {a}{(custom ? oc.BlizzardOnly ? "   [not Blizzard's: left out]" : "   [not Blizzard's: a server's or a mod's patch]" : "")}");
                }
                r.Line($"  files    {names.Count:N0} (plus {oc.Data.DeletedNames().Count:N0} removed by patches)");
                r.Line($"  tables   {tables.Count} ({named} with named columns for this version, the others raw)");
                if (oc.CustomArchives.Count > 0 && !oc.BlizzardOnly) r.Warnings.Add($"{string.Join(", ", oc.CustomArchives)} {(oc.CustomArchives.Count == 1 ? "is" : "are")} not Blizzard's and replace{(oc.CustomArchives.Count == 1 ? "s" : "")} some of the game's files: --blizzard-only reads the game as Blizzard shipped it.");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions files",
            Summary = "a client's files by wildcard (* and ?), with the archive each one is read from",
            Args = new[] { "<client>", "<pattern>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Common.Limit },
            NeedsClient = false,
            Examples = new[] { "wds versions files 3.3.5 World\\Maps\\Northrend\\*.adt", "wds versions files 2.4.3 Interface\\Icons\\INV_Sword_* --limit 0 --json" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var names = oc.Data.Names();
                var files = oc.Data.Glob(inv.Arg(1).Replace('/', '\\'));
                var shown = files.Take(Common.LimitOf(inv)).ToList();
                var r = new CliResult().With(new { client = c.Name, total = files.Count, files = shown.Select(f => new { path = f, archive = names.TryGetValue(f.ToUpperInvariant(), out var lf) ? lf.Archive : null }) });
                r.Line($"{files.Count:N0} files in {c.Name}" + (shown.Count < files.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                foreach (var f in shown) r.Line($"{(names.TryGetValue(f.ToUpperInvariant(), out var lf2) ? lf2.Archive : ""),-26} {f}");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions extract",
            Summary = "copy files out of a client as they are (one path, or a wildcard into a folder, keeping the folders)",
            Args = new[] { "<client>", "<path-or-pattern>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, new OptionSpec("out", "file-or-folder", "where to write (default: the current folder)") },
            NeedsClient = false,
            Examples = new[] { "wds versions extract 3.3.5 DBFilesClient\\Map.dbc --out wotlk\\", "wds versions extract 2.4.3 \"World\\Maps\\Expansion01\\*.adt\" --out tbc_maps\\" },
            Run = async inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var pattern = inv.Arg(1).Replace('/', '\\');
                bool many = pattern.Contains('*') || pattern.Contains('?');
                var paths = many ? oc.Data.Glob(pattern) : new List<string> { ClientData.ResolveName(pattern) };
                if (paths.Count == 0) throw new CliError(ExitCode.NotFound, $"no file matches {pattern} in {c.Name}.");
                var outArg = inv.Opt("out") ?? (many ? "." : Path.GetFileName(paths[0]));
                var outRoot = Path.GetFullPath(outArg);
                if (FindCommands.IsInside(outRoot + "\\", c.Folder) || string.Equals(outRoot.TrimEnd('\\'), c.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new CliError(ExitCode.Refused, $"Refusing to write into the client's folder ({c.Folder}): other versions are only read. Extract somewhere else.");
                var written = new List<object>();
                string lastFile = "";
                long bytes = 0;
                foreach (var p in paths)
                {
                    var data = oc.Data.Read(p) ?? throw new CliError(ExitCode.NotFound, $"{p} is not in {c.Name}.");
                    string dest = many ? Path.Combine(outRoot, p) : Directory.Exists(outRoot) || outArg.EndsWith('\\') || outArg.EndsWith('/') ? Path.Combine(outRoot, Path.GetFileName(p)) : outRoot;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, data);
                    written.Add(new { path = p, file = dest, bytes = data.Length });
                    lastFile = dest;
                    bytes += data.Length;
                }
                var r = new CliResult().With(new { client = c.Name, files = written });
                r.Line(paths.Count == 1 ? $"{paths[0]} ({bytes:N0} bytes) -> {lastFile}" : $"{paths.Count:N0} files ({bytes:N0} bytes) -> {outRoot}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "versions tables",
            Summary = "a client's data tables: rows, fields, and whether this version's columns are named or shown raw",
            Args = new[] { "<client>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches },
            NeedsClient = false,
            Examples = new[] { "wds versions tables 3.3.5", "wds versions tables 2.4.3 --json" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var list = new List<(string Name, int Rows, int Fields, string Columns)>();
                foreach (var n in oc.TableNames())
                {
                    DbcTable? t;
                    try { t = oc.Table(n); }
                    catch (DbcException) { list.Add((n, 0, 0, "unreadable")); continue; }
                    if (t == null) continue;
                    list.Add((n, t.Rows.Count, t.FieldCount, t.Layout.IsFallback ? "raw" : t.Layout.CoverageText));
                }
                var r = new CliResult().With(new { client = c.Name, tables = list.Select(x => new { name = x.Name, rows = x.Rows, fields = x.Fields, columns = x.Columns }) });
                r.Line($"{list.Count} tables in {c.Name} ({list.Count(x => x.Columns != "raw")} with named columns)");
                foreach (var l in Common.Table(new[] { "Table", "Rows", "Fields", "Columns" }, list.Select(x => (IReadOnlyList<string>)new[] { x.Name, x.Rows.ToString("N0"), x.Fields.ToString(), x.Columns }), 50))
                    r.Line(l);
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions rows",
            Summary = "rows of a client's data table (columns named where this version's layout is known, else col_N)",
            Args = new[] { "<client>", "<table>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, new OptionSpec("columns", "list", "only these columns (comma-separated)"), new OptionSpec("where", "column=value", "only rows whose column has this value (text: contains, case-insensitive)", true), Common.Limit },
            NeedsClient = false,
            Examples = new[] { "wds versions rows 3.3.5 Map --columns ID,Directory,MapName", "wds versions rows 2.4.3 AreaTable --where MapID=530 --limit 0 --json" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var t = Table(oc, inv.Arg(1));
                var cols = inv.Opt("columns") is { } cl ? cl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => Col(t, n)).ToList() : t.Layout.Columns.ToList();
                IEnumerable<DbcRow> rows = t.Rows;
                foreach (var w in inv.Opts("where"))
                {
                    int eq = w.IndexOf('=');
                    if (eq <= 0) throw new CliError(ExitCode.Usage, $"--where is column=value (not \"{w}\").");
                    var wc = Col(t, w[..eq].Trim());
                    var val = w[(eq + 1)..].Trim();
                    rows = rows.Where(r => wc.IsString ? t.Format(r, wc).Contains(val, StringComparison.OrdinalIgnoreCase) : t.Format(r, wc) == val);
                }
                var all = rows.ToList();
                var shown = all.Take(Common.LimitOf(inv)).ToList();
                var res = new CliResult().With(new
                {
                    client = c.Name, table = t.Name, layout = t.Layout.IsFallback ? "raw" : "named", total = t.Rows.Count, matched = all.Count,
                    columns = cols.Select(x => x.Name), rows = shown.Select(r => cols.ToDictionary(x => x.Name, x => (object)t.Format(r, x))),
                });
                res.Line($"{c.Name} {t.Name}: {(all.Count == t.Rows.Count ? $"{t.Rows.Count:N0} rows" : $"{all.Count:N0} of {t.Rows.Count:N0} rows")}" + (shown.Count < all.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                if (t.Layout.IsFallback) res.Warnings.Add($"{t.Name} has no known layout for {c.Version} yet: raw columns.");
                foreach (var l in Common.Table(cols.Select(x => x.Name).ToList(), shown.Select(r => (IReadOnlyList<string>)cols.Select(x => t.Format(r, x)).ToList()), 40)) res.Line(l);
                return Task.FromResult(res);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions compare",
            Summary = "one data-table row side by side in several versions (columns matched by name)",
            Args = new[] { "<table>", "<id>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, new OptionSpec("clients", "list", "these clients (comma-separated names, versions or builds; default: every readable one)"), new OptionSpec("raw", null, "also the unnamed columns (col_N)"), new OptionSpec("diff", null, "only the columns whose values differ") },
            NeedsClient = false,
            Examples = new[] { "wds versions compare Spell 133", "wds versions compare AreaTable 1519 --clients 1.12,2.4,3.3.5 --diff", "wds versions compare Map 0 --json" },
            Run = inv =>
            {
                var found = Found(inv);
                var picks = inv.Opt("clients") is { } list
                    ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Pick(inv, x, found)).ToList()
                    : found.Where(x => x.Readable).GroupBy(x => (x.Version, x.Build)).Select(g => g.First()).OrderBy(x => x.Build).ToList();
                if (picks.Count == 0) throw new CliError(ExitCode.NotFound, "no readable client to compare.");
                var opened = new List<OtherClient>();
                try
                {
                    foreach (var p in picks) opened.Add(Open(inv, p));
                    var cmp = VersionCompare.Row(inv.Arg(0), inv.Long(1), opened.Select(o => (o.Copy.Version, o.Table(inv.Arg(0)))).ToList(), 0, inv.Flag("raw"));
                    var cols = inv.Flag("diff") ? cmp.Columns.Where(x => x.Values.Distinct().Count() > 1).ToList() : cmp.Columns.ToList();
                    var r = new CliResult().With(new
                    {
                        table = cmp.Table, id = cmp.Id, clients = picks.Select(CopyJson), notes = cmp.Notes,
                        columns = cols.Select(x => new { name = x.Column, values = x.Values }),
                    });
                    r.Line($"{cmp.Table} {cmp.Id} in {string.Join(", ", picks.Select(p => p.Version))}");
                    foreach (var l in Common.Table(new[] { "Column" }.Concat(cmp.Clients).ToList(), cols.Select(x => (IReadOnlyList<string>)new[] { x.Column }.Concat(x.Values.Select(v => v ?? "-")).ToList()), 40))
                        r.Line(l);
                    foreach (var n in cmp.Notes) r.Warnings.Add(n);
                    return Task.FromResult(r);
                }
                finally
                {
                    foreach (var o in opened) o.Dispose();
                }
            },
        };
    }
}
