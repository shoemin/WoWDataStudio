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

    private static readonly OptionSpec Listfile = new("listfile", "file", "names for a modern (CASC) client's files: a listfile you chose (FileDataID;path lines); also the other-clients.listfile setting");

    private static bool Blizzard(Invocation inv) => !inv.Flag("all-patches") && (inv.Flag("blizzard-only") || inv.Ctx.Settings.OtherClientsBlizzardOnly);

    private static OtherClient Open(Invocation inv, ClientCopy c)
    {
        try
        {
            inv.Ctx.Progress($"Opening {c.Name} ({c.Label})...");
            var lf = c.Format == ClientFormat.Casc ? inv.Opt("listfile") ?? inv.Ctx.Settings.OtherClientsListfile : null;
            if (lf != null && !File.Exists(lf)) throw new CliError(ExitCode.NotFound, $"the listfile \"{lf}\" does not exist.");
            return OtherClient.Open(c, Blizzard(inv), lf);
        }
        catch (Exception e) when (e is NotSupportedException or IOException or WoWData.Core.Casc.CascMissingException)
        {
            throw new CliError(ExitCode.NotFound, $"{c.Name} could not be opened: {e.Message}");
        }
    }

    private static byte[] ReadFile(OtherClient oc, string path)
    {
        try { return oc.Files.Read(path) ?? throw new CliError(ExitCode.NotFound, $"{path} is not in {oc.Copy.Name}" + (oc.Casc != null && !path.TrimStart('#').All(char.IsDigit) ? " (or its name is not known: give its FileDataID as #number, or a listfile)." : ".")); }
        catch (ClientFileException e) { throw new CliError(ExitCode.NotFound, e.Message); }
    }

    private static object CopyJson(ClientCopy c) => new
    {
        name = c.Name, folder = c.Folder, exe = Path.GetFileName(c.Exe), version = c.Version, build = c.Build, release = c.Release,
        format = c.Format.ToString().ToUpperInvariant(), layout = c.Layout.ToString(), locale = c.Locale, readable = c.Readable,
    };

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
                if (found.Any(c => c.Format == ClientFormat.Casc)) r.Line("CASC clients (6.x and later, and Battle.net clients): their files can be read, their data tables not yet.");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions info",
            Summary = "one client: version, release, locale, its archives in the order the game reads them (patches that are not Blizzard's marked), files and tables",
            Args = new[] { "<client>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile },
            NeedsClient = false,
            Details = "<client> is the client's folder, its name in \"wds versions find\", a version (3.3.5, 2.4) or a build (12340).",
            Examples = new[] { "wds versions info 3.3.5", "wds versions info \"2.4.3.8606 - The Burning Crusade\" --json", "wds versions info _classic_beta_" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                if (oc.Casc is { } casc)
                {
                    inv.Ctx.Progress("Counting the files that are on this computer...");
                    var ids = casc.FileDataIds().ToList();
                    var states = ids.GroupBy(casc.State).ToDictionary(g => g.Key, g => g.Count());
                    int St(WoWData.Core.Casc.CascFileState s) => states.GetValueOrDefault(s);
                    var cr = new CliResult().With(new
                    {
                        client = CopyJson(c), product = casc.Product, buildName = casc.BuildName, files = ids.Count,
                        local = St(WoWData.Core.Casc.CascFileState.Local), notDownloaded = St(WoWData.Core.Casc.CascFileState.NotLocal),
                        encrypted = St(WoWData.Core.Casc.CascFileState.Encrypted), nameHashes = casc.Root.NameHashCount, listfileNames = oc.ListfileNames,
                    });
                    cr.Line($"{c.Name}: World of Warcraft {c.Version} (build {c.Build}), {c.Release}; CASC product {casc.Product} ({casc.BuildName})");
                    cr.Line($"  folder     {c.Folder}");
                    cr.Line($"  data       {casc.DataDirectory}");
                    cr.Line($"  files      {ids.Count:N0} (English or all languages): {St(WoWData.Core.Casc.CascFileState.Local):N0} on this computer, {St(WoWData.Core.Casc.CascFileState.NotLocal):N0} not downloaded, {St(WoWData.Core.Casc.CascFileState.Encrypted):N0} encrypted");
                    cr.Line($"  names      {casc.Root.NameHashCount:N0} name hashes in the build (a path can be looked up), {oc.ListfileNames:N0} names from your listfile");
                    cr.Line("  tables     DB2 tables cannot be read yet");
                    if (oc.ListfileNames == 0) cr.Line("Without a listfile, files are found by their full path (when the build hashes it) or by number: #775971.");
                    return Task.FromResult(cr);
                }
                var names = oc.Data!.Names();
                var tables = oc.TableNames();
                var named = tables.Count(t => oc.Layouts.Get(t) != null);
                var archives = ClientData.ClientArchiveOrder(c.DataDir);
                var r = new CliResult().With(new
                {
                    client = CopyJson(c), blizzardOnly = oc.BlizzardOnly, archives = archives.Select(a => new { name = a, mounted = oc.Data!.Archives.Any(m => m.Name.Equals(a, StringComparison.OrdinalIgnoreCase)), blizzard = !oc.CustomArchives.Contains(a, StringComparer.OrdinalIgnoreCase) }),
                    files = names.Count, deleted = oc.Data!.DeletedNames().Count, tables = tables.Count, tablesWithLayout = named, layoutBuild = oc.Layouts.Build,
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
                r.Line($"  files    {names.Count:N0} (plus {oc.Data!.DeletedNames().Count:N0} removed by patches)");
                r.Line($"  tables   {tables.Count} ({named} with named columns for this version, the others raw)");
                if (oc.CustomArchives.Count > 0 && !oc.BlizzardOnly) r.Warnings.Add($"{string.Join(", ", oc.CustomArchives)} {(oc.CustomArchives.Count == 1 ? "is" : "are")} not Blizzard's and replace{(oc.CustomArchives.Count == 1 ? "s" : "")} some of the game's files: --blizzard-only reads the game as Blizzard shipped it.");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions files",
            Summary = "a client's files by wildcard (* and ?) with where each is read from; a modern (CASC) client: a full path or #FileDataID, wildcards with a listfile",
            Args = new[] { "<client>", "<pattern>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile, Common.Limit },
            NeedsClient = false,
            Examples = new[] { "wds versions files 3.3.5 World\\Maps\\Northrend\\*.adt", "wds versions files 2.4.3 Interface\\Icons\\INV_Sword_* --limit 0 --json", "wds versions files _classic_beta_ #775971" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var pattern = inv.Arg(1).Replace('/', '\\');
                bool wild = pattern.Contains('*') || pattern.Contains('?');
                List<ClientFileHit> files;
                if (wild)
                {
                    if (!oc.Files.CanList)
                        throw new CliError(ExitCode.Usage, $"{c.Name} has no file names to search: give a full path or #FileDataID, or a listfile (--listfile, or the other-clients.listfile setting).");
                    files = oc.Files.Glob(pattern).ToList();
                }
                else
                {
                    var src = oc.Files.SourceOf(pattern);
                    files = src == null ? new List<ClientFileHit>() : new List<ClientFileHit> { new(pattern, src) };
                }
                var shown = files.Take(Common.LimitOf(inv)).ToList();
                var r = new CliResult().With(new { client = c.Name, total = files.Count, files = shown.Select(f => new { path = f.Path, source = f.Source }) });
                r.Line($"{files.Count:N0} files in {c.Name}" + (shown.Count < files.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                foreach (var f in shown) r.Line($"{f.Source,-26} {f.Path}");
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions extract",
            Summary = "copy files out of a client as they are (one path or #FileDataID, or a wildcard into a folder, keeping the folders)",
            Args = new[] { "<client>", "<path-or-pattern>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile, new OptionSpec("out", "file-or-folder", "where to write (default: the current folder)") },
            NeedsClient = false,
            Details = "A file known only by its number (#FileDataID) is written as <number> with an extension from its first bytes (.blp, .m2, .adt, .wdt, .db2...).",
            Examples = new[] { "wds versions extract 3.3.5 DBFilesClient\\Map.dbc --out wotlk\\", "wds versions extract 2.4.3 \"World\\Maps\\Expansion01\\*.adt\" --out tbc_maps\\", "wds versions extract _classic_beta_ #775971 --out beta\\" },
            Run = async inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var pattern = inv.Arg(1).Replace('/', '\\');
                bool many = pattern.Contains('*') || pattern.Contains('?');
                if (many && !oc.Files.CanList)
                    throw new CliError(ExitCode.Usage, $"{c.Name} has no file names to search: give a full path or #FileDataID, or a listfile (--listfile, or the other-clients.listfile setting).");
                var paths = many ? oc.Files.Glob(pattern).Select(h => h.Path).ToList() : new List<string> { oc.Casc != null ? pattern : ClientData.ResolveName(pattern) };
                if (paths.Count == 0) throw new CliError(ExitCode.NotFound, $"no file matches {pattern} in {c.Name}.");
                var outArg = inv.Opt("out") ?? ".";
                var outRoot = Path.GetFullPath(outArg);
                if (FindCommands.IsInside(outRoot + "\\", c.Folder) || string.Equals(outRoot.TrimEnd('\\'), c.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || FindCommands.IsInside(outRoot + "\\", c.DataDir) || string.Equals(outRoot.TrimEnd('\\'), c.DataDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new CliError(ExitCode.Refused, $"Refusing to write into the client's folder ({c.Folder}): other versions are only read. Extract somewhere else.");
                var written = new List<object>();
                string lastFile = "";
                long bytes = 0;
                foreach (var p in paths)
                {
                    var data = ReadFile(oc, p);
                    // a file known only by its number: <number><extension from its first bytes>
                    var name = p.StartsWith('#') || p.All(char.IsDigit) ? p.TrimStart('#') + ClientFileKinds.GuessExtension(data) : p;
                    bool toFolder = many || inv.Opt("out") == null || Directory.Exists(outRoot) || outArg.EndsWith('\\') || outArg.EndsWith('/');
                    string dest = many ? Path.Combine(outRoot, name) : toFolder ? Path.Combine(outRoot, Path.GetFileName(name)) : outRoot;
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
            Summary = "a client's data tables: rows, fields, and whether this version's columns are named or shown raw (a modern client: its DB2 tables from its executable)",
            Args = new[] { "<client>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile, new OptionSpec("read", null, "a modern (CASC) client: read every table to count its rows (slower)") },
            NeedsClient = false,
            Examples = new[] { "wds versions tables 3.3.5", "wds versions tables 2.4.3 --json", "wds versions tables _classic_beta_" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var list = new List<(string Name, string Rows, int Fields, string Columns)>();
                bool read = oc.Casc == null || inv.Flag("read");
                foreach (var n in oc.TableNames())
                {
                    if (!read)
                    {
                        var e = oc.Catalog.Get(n);
                        list.Add((n, "", e?.FieldCount ?? 0, $"#{e?.FileDataId}" + (WoWData.Core.Db2.Db2Layouts.ForBuild(c.Build)?.Get(n) != null ? ", layout" : "")));
                        continue;
                    }
                    VersionTable? t;
                    try { t = oc.TableView(n); }
                    catch (Exception ex) when (ex is DbcException or WoWData.Core.Db2.Db2FormatException) { list.Add((n, "", 0, "unreadable")); continue; }
                    if (t == null) { if (oc.Casc != null) list.Add((n, "", 0, "not on this computer")); continue; }
                    list.Add((n, t.RowCount.ToString("N0"), t.FieldCount, t.Coverage));
                }
                var r = new CliResult().With(new { client = c.Name, tables = list.Select(x => new { name = x.Name, rows = x.Rows, fields = x.Fields, columns = x.Columns }) });
                r.Line(read ? $"{list.Count} tables in {c.Name} ({list.Count(x => x.Columns != "raw" && x.Columns != "unreadable" && x.Columns != "not on this computer")} with named columns)"
                            : $"{list.Count} tables in {c.Name} (DB2, from its executable; \"#\" is the file number; --read counts rows)");
                foreach (var l in Common.Table(new[] { "Table", "Rows", "Fields", read ? "Columns" : "File" }, list.Select(x => (IReadOnlyList<string>)new[] { x.Name, x.Rows, x.Fields.ToString(), x.Columns }), 50))
                    r.Line(l);
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions rows",
            Summary = "rows of a client's data table, old (DBC) or modern (DB2): columns named where this version's layout is known, else col_N",
            Args = new[] { "<client>", "<table>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile, new OptionSpec("columns", "list", "only these columns (comma-separated)"), new OptionSpec("where", "column=value", "only rows whose column has this value (text: contains, case-insensitive)", true), Common.Limit },
            NeedsClient = false,
            Examples = new[] { "wds versions rows 3.3.5 Map --columns ID,Directory,MapName", "wds versions rows 2.4.3 AreaTable --where MapID=530 --limit 0 --json", "wds versions rows _classic_beta_ Map --limit 0" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var t = oc.TableView(inv.Arg(1)) ?? throw new CliError(ExitCode.NotFound, $"{c.Name} has no table {inv.Arg(1)} (wds versions tables \"{c.Name}\").");
                int Col(string name)
                {
                    for (int i = 0; i < t.Columns.Count; i++) if (string.Equals(t.Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
                    throw new CliError(ExitCode.NotFound, $"{t.Name} has no column {name}. Its columns: {string.Join(", ", t.Columns)}.");
                }
                var cols = inv.Opt("columns") is { } cl ? cl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Col).ToList() : Enumerable.Range(0, t.Columns.Count).ToList();
                IEnumerable<string[]> rows = t.Rows();
                foreach (var w in inv.Opts("where"))
                {
                    int eq = w.IndexOf('=');
                    if (eq <= 0) throw new CliError(ExitCode.Usage, $"--where is column=value (not \"{w}\").");
                    int wc = Col(w[..eq].Trim());
                    var val = w[(eq + 1)..].Trim();
                    rows = rows.Where(r => t.IsText[wc] ? r[wc].Contains(val, StringComparison.OrdinalIgnoreCase) : r[wc] == val);
                }
                var all = rows.ToList();
                var shown = all.Take(Common.LimitOf(inv)).ToList();
                var res = new CliResult().With(new
                {
                    client = c.Name, table = t.Name, layout = t.IsRaw ? "raw" : "named", total = t.RowCount, matched = all.Count, note = t.Note,
                    columns = cols.Select(x => t.Columns[x]), rows = shown.Select(r => cols.ToDictionary(x => t.Columns[x], x => (object)r[x])),
                });
                res.Line($"{c.Name} {t.Name}: {(all.Count == t.RowCount ? $"{t.RowCount:N0} rows" : $"{all.Count:N0} of {t.RowCount:N0} rows")}" + (shown.Count < all.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                if (t.IsRaw) res.Warnings.Add($"{t.Name} has no known layout for {c.Version} yet: raw columns.");
                if (t.Note.Length > 0) res.Warnings.Add(t.Note);
                foreach (var l in Common.Table(cols.Select(x => t.Columns[x]).ToList(), shown.Select(r => (IReadOnlyList<string>)cols.Select(x => r[x]).ToList()), 40)) res.Line(l);
                return Task.FromResult(res);
            },
        };
        yield return new CommandSpec
        {
            Path = "versions compare",
            Summary = "one data-table row side by side in several versions, old (DBC) or modern (DB2) tables (columns matched by name)",
            Args = new[] { "<table>", "<id>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, new OptionSpec("clients", "list", "these clients (comma-separated names, versions or builds; default: one of each MPQ version)"), new OptionSpec("raw", null, "also the unnamed columns (col_N)"), new OptionSpec("diff", null, "only the columns whose values differ") },
            NeedsClient = false,
            Examples = new[] { "wds versions compare Spell 133", "wds versions compare AreaTable 1519 --clients 1.12,2.4,3.3.5 --diff", "wds versions compare Map 0 --clients 1.12,_classic_beta_" },
            Run = inv =>
            {
                var found = Found(inv);
                var picks = inv.Opt("clients") is { } list
                    ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Pick(inv, x, found)).ToList()
                    : found.Where(x => x.Format == ClientFormat.Mpq).GroupBy(x => (x.Version, x.Build)).Select(g => g.First()).OrderBy(x => x.Build).ToList();
                if (picks.Count == 0) throw new CliError(ExitCode.NotFound, "no readable client to compare.");
                var opened = new List<OtherClient>();
                try
                {
                    foreach (var p in picks) opened.Add(Open(inv, p));
                    var cmp = VersionCompare.Row(inv.Arg(0), inv.Long(1), opened.Select(o => (o.Copy.Version, o.TableView(inv.Arg(0)))).ToList(), inv.Flag("raw"));
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
