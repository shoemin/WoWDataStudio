using WoWData.Core.Dbc;
using WoWData.Core.Maps.Conversion;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;
using WoWData.Core.Index;

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
            var oc = OtherClient.Open(c, Blizzard(inv), lf);
            oc.Dbd = WoWData.Core.Db2.DbdFolder.Open(inv.Ctx.Settings.OtherClientsDbd);   // M5: WoWDBDefs names (optional)
            return oc;
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

    /// <summary>M5: "Column names: WoWDBDefs (CC BY-SA 4.0), from &lt;folder&gt;." or "Column names: measured (our layout
    /// file); not WoWDBDefs: ...".</summary>
    internal static string NamesLine(VersionTable t, OtherClient oc) =>
        t.Naming.Source == ColumnNameSource.WoWDBDefs && oc.Dbd != null ? $"Column names: {oc.Dbd.Attribution}." : $"Column names: {t.Naming.Long}.";

    private static object CopyJson(ClientCopy c) => new
    {
        name = c.Name, folder = c.Folder, exe = Path.GetFileName(c.Exe), version = c.Version, build = c.Build, release = c.Release,
        format = c.Format.ToString().ToUpperInvariant(), layout = c.Layout.ToString(), locale = c.Locale, readable = c.Readable,
    };

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "versions open",
            Summary = "open another version read only (the app's \"Open read only\"): build or check its index and show what it holds: entries, file names and where they come from, locked records",
            Args = new[] { "[client]" },
            Options = new[] { Folder, Listfile, new OptionSpec("rebuild", null, "build the index again (\"Rebuild the index\")") },
            NeedsClient = false,
            Details = "<client> is the client's folder, its name in \"wds versions find\", a version (1.60) or a build (70450); or --version <client>. " +
                      "The first time its index is built from the client's own tables and file names, your 1.12 game's file names (when your game folder is set) and the listfile you chose. " +
                      "Then the commands that read take --version <client>: find search / list / info / files / extract, textures info / export / export-all. Nothing in the client is changed; its index is kept in the app's folder.",
            Examples = new[] { "wds versions open _classic_beta_", "wds versions open 1.60 --listfile \"G:\\listfile\\community-listfile.csv\"", "wds versions open _classic_beta_ --rebuild --json", "wds find search boar --version _classic_beta_" },
            Run = async inv =>
            {
                var text = inv.ArgOrNull(0) ?? inv.Ctx.VersionOverride ?? throw new CliError(ExitCode.Usage, "give the client: wds versions open <client> (or --version <client>).");
                if (inv.Opt("folder") is { } folder) inv.Ctx.Settings.OtherClientsDir = folder;
                var lf = inv.Opt("listfile");
                if (lf != null && !File.Exists(lf)) throw new CliError(ExitCode.NotFound, $"the listfile \"{lf}\" does not exist.");
                var s = await inv.Ctx.OtherSessionAsync(text, inv.Flag("rebuild"), lf);
                var st = OtherVersionStatus.Of(s);
                var copy = s.Profile!.Copy!;
                var r = new CliResult().With(new
                {
                    client = CopyJson(copy), version = st.Version, label = st.Label, readOnly = true, index = s.Index!.Path, built = st.Built, seconds = st.Seconds,
                    entries = st.Entries, entriesByKind = st.EntriesByKind, clientFiles = st.ClientFiles, names = st.Names, verifiedNames = st.VerifiedNames,
                    fileEntriesLeftOut = st.FileEntriesLeftOut, listfile = st.Listfile, era = st.Era,
                    locked = st.Locked.Select(l => new { table = l.Table, readable = l.Readable, locked = l.Locked, total = l.Total, copies = l.Copies }),
                    problems = st.Problems, statusLine = st.StatusLine,
                });
                r.Line($"{copy.Name}: {st.Label}");
                r.Line($"  index    {s.Index.Path} (built {st.Built}{(st.Seconds > 0 ? $" in {st.Seconds:0.#} s" : "")})");
                r.Line($"  entries  {st.Entries:N0}: " + string.Join(", ", st.EntriesByKind.OrderBy(k => k.Key).Select(k => $"{k.Value:N0} {k.Key.ToLowerInvariant()}")));
                if (st.FileEntriesLeftOut > 0) r.Line($"           {st.FileEntriesLeftOut:N0} more named textures and models are not Browse entries (the limit); \"wds find files <pattern> --version {text}\" lists them");
                r.Line("  " + st.NamesLine);
                if (st.Era.Length > 0) r.Line($"           Classic Era copy used for your 1.12 names: {st.Era}");
                r.Line("  locked   records in encrypted sections (no key): never guessed, left out");
                foreach (var l in st.Locked) r.Line($"    {l.Table,-28} {l.Readable,9:N0} readable  {l.Locked,6:N0} locked" + (l.Copies > 0 ? $"  ({l.Copies:N0} copy rows)" : ""));
                foreach (var p in st.Problems) r.Warnings.Add(p);
                r.Line($"Read it with --version {text}: wds find search <words> --version {text}");
                return r;
            },
        };
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
                if (found.Any(c => c.Format == ClientFormat.Casc)) r.Line("CASC clients (6.x and later, and Battle.net clients): their files and data tables can be read; \"wds versions open <client>\" opens one read only like your game (Browse, textures).");
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
            Summary = "a client's data tables: rows, fields, and where this version's column names come from (WoWDBDefs, our measured layout file, or raw col_N; a modern client: its DB2 tables from its executable)",
            Args = new[] { "<client>" },
            Options = new[] { Folder, BlizzardOnly, AllPatches, Listfile, new OptionSpec("read", null, "a modern (CASC) client: read every table to count its rows (slower)") },
            NeedsClient = false,
            Examples = new[] { "wds versions tables 3.3.5", "wds versions tables 2.4.3 --json", "wds versions tables _classic_beta_" },
            Run = inv =>
            {
                var c = Pick(inv, inv.Arg(0));
                using var oc = Open(inv, c);
                var list = new List<(string Name, string Rows, int Fields, string Columns, TableNaming Names)>();
                bool read = oc.Casc == null || inv.Flag("read");
                foreach (var n in oc.TableNames())
                {
                    if (!read)
                    {
                        var e = oc.Catalog.Get(n);
                        list.Add((n, "", e?.FieldCount ?? 0, $"#{e?.FileDataId}", oc.ExpectedNaming(n)));
                        continue;
                    }
                    VersionTable? t;
                    try { t = oc.TableView(n); }
                    catch (Exception ex) when (ex is DbcException or WoWData.Core.Db2.Db2FormatException) { list.Add((n, "", 0, "unreadable", TableNaming.Raw)); continue; }
                    if (t == null) { if (oc.Casc != null) list.Add((n, "", 0, "not on this computer", oc.ExpectedNaming(n))); continue; }
                    list.Add((n, t.RowCount.ToString("N0"), t.FieldCount, t.Coverage, t.Naming));
                }
                var r = new CliResult().With(new
                {
                    client = c.Name, dbd = oc.Dbd?.Folder,
                    tables = list.Select(x => new { name = x.Name, rows = x.Rows, fields = x.Fields, columns = x.Columns, names = x.Names.Key, namesFallback = x.Names.Fallback }),
                });
                r.Line(read ? $"{list.Count} tables in {c.Name} ({list.Count(x => x.Columns != "raw" && x.Columns != "unreadable" && x.Columns != "not on this computer")} with named columns)"
                            : $"{list.Count} tables in {c.Name} (DB2, from its executable; \"#\" is the file number; --read counts rows)");
                int byDbd = list.Count(x => x.Names.Source == ColumnNameSource.WoWDBDefs), byOurs = list.Count(x => x.Names.Source == ColumnNameSource.Measured);
                if (oc.Casc != null)
                    r.Line(oc.Dbd != null ? $"Column names: {byDbd} tables from {oc.Dbd.Attribution}; {byOurs} from our measured layout file; the rest raw (col_N)."
                                          : $"Column names: {byOurs} tables from our measured layout file, the rest raw (col_N). A WoWDBDefs folder names the others: wds settings set other-clients.dbd <folder>.");
                foreach (var l in Common.Table(new[] { "Table", "Rows", "Fields", read ? "Columns" : "File", "Names" }, list.Select(x => (IReadOnlyList<string>)new[] { x.Name, x.Rows, x.Fields.ToString(), x.Columns, x.Names.Text }), 50))
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
                    client = c.Name, table = t.Name, layout = t.IsRaw ? "raw" : "named", names = t.Naming.Key, namesFallback = t.Naming.Fallback, dbd = t.Naming.Source == ColumnNameSource.WoWDBDefs ? oc.Dbd?.Attribution : null,
                    total = t.RowCount, matched = all.Count, note = t.Note,
                    columns = cols.Select(x => t.Columns[x]), rows = shown.Select(r => cols.ToDictionary(x => t.Columns[x], x => (object)r[x])),
                });
                res.Line($"{c.Name} {t.Name}: {(all.Count == t.RowCount ? $"{t.RowCount:N0} rows" : $"{all.Count:N0} of {t.RowCount:N0} rows")}" + (shown.Count < all.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                if (t.IsRaw) res.Warnings.Add($"{t.Name} has no known layout for {c.Version} yet: raw columns.");
                res.Line(NamesLine(t, oc));
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
        yield return new CommandSpec
        {
            Path = "versions convert-map",
            Summary = "\"Convert map...\": a map of the 1.60 client (a zone with its subzones, tiles or all) converted into your 1.12 workspace as one change set, with the conversion report; --dry-run only reports",
            Args = new[] { "<client>", "<map>" },
            Options = new[]
            {
                Folder, Listfile,
                new OptionSpec("zone", "name-or-id", "only the tiles with ground of this zone (or its subzones): its name or AreaTable ID in that client"),
                new OptionSpec("tiles", "x_y,...", "exactly these tiles (for example 32_48,33_48)"),
                Common.DryRun,
            },
            Details = "<map> is the map's ID, folder or name in that client (wds versions rows <client> Map). Without --zone and --tiles the whole map is converted. " +
                      "Nothing is written without --yes (without it the report is shown and the exit code is 5). Names come only from your 1.12 game's files " +
                      "(found by their hashes in a Classic Era copy next to the client) and the listfile you chose; assets without a name go to World\\Converted\\<number>. " +
                      "\"wds changes revert-set <id> --yes\" takes the whole conversion back.",
            Examples = new[]
            {
                "wds versions convert-map _classic_beta_ Azeroth --zone \"Elwynn Forest\" --dry-run",
                "wds versions convert-map _classic_beta_ 0 --tiles 32_48,33_48 --yes",
                "wds versions convert-map _classic_beta_ \"Zephras Isle\" --yes --json",
            },
            Run = async inv =>
            {
                if (inv.Opt("zone") != null && inv.Opt("tiles") != null) throw new CliError(ExitCode.Usage, "give --zone or --tiles, not both.");
                List<(int X, int Y)>? tiles = null;
                if (inv.Opt("tiles") is { } tl)
                {
                    try { tiles = MapConverter.ParseTiles(tl); }
                    catch (ArgumentException e) { throw new CliError(ExitCode.Usage, "--tiles: " + e.Message); }
                }
                var s = await inv.Ctx.SessionAsync();
                if (s.Data == null || s.Workspace == null || s.Dbc == null) throw new CliError(ExitCode.Error, "Your 1.12 game is not open (wds setup client <folder>).");
                List<ClientCopy>? found = null;
                var text = inv.Arg(0).Trim().Trim('"');
                if (!Directory.Exists(text) || inv.Opt("folder") != null || inv.Ctx.Settings.OtherClientsDir != null)
                {
                    try { found = Found(inv); }
                    catch (CliError) when (Directory.Exists(text)) { }
                }
                var c = Pick(inv, inv.Arg(0), found);
                var lf = inv.Opt("listfile") ?? inv.Ctx.Settings.OtherClientsListfile;
                if (lf != null && !File.Exists(lf)) throw new CliError(ExitCode.NotFound, $"the listfile \"{lf}\" does not exist.");
                inv.Ctx.Progress($"Opening {c.Name} ({c.Label}) and the names of your 1.12 files...");
                MapConversionSource src;
                try { src = MapConversionSource.Open(c, lf, s.Data, found, Path.Combine(inv.Ctx.Paths.Root, "cache", "names")); }
                catch (NotSupportedException e) { throw new CliError(ExitCode.Refused, e.Message); }
                catch (Exception e) when (e is IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException) { throw new CliError(ExitCode.NotFound, $"{c.Name} could not be opened: {e.Message}"); }
                using (src)
                {
                    var req = new MapConvertRequest { Map = inv.Arg(1), Zone = inv.Opt("zone"), Tiles = tiles };
                    var target = new MapConvertTarget { Client = s.Data, Workspace = s.Workspace, Dbc = s.Dbc, Locale = s.GameLocale };
                    bool write = inv.Yes && !inv.DryRun;
                    string last = "";
                    var progress = new SyncProgress<string>(p =>
                    {
                        // one line per step (not one per tile)
                        var step = p.Split(':')[0];
                        if (step == last) return;
                        last = step;
                        inv.Ctx.Progress(p);
                    });
                    MapConversionResult res;
                    try { res = MapConverter.Run(src.Source, req, target, !write, progress); }
                    catch (ArgumentException e) { throw new CliError(ExitCode.NotFound, e.Message); }
                    catch (WoWData.Core.Workspace.WorkspaceException e) { return CliResult.Fail(ExitCode.ValidationFailed, e.Message); }
                    var r = new CliResult().With(ConversionJson(res, c));
                    foreach (var l in res.Lines()) r.Line(l);
                    r.Line("Then:");
                    foreach (var step in res.ServerSteps) r.Line("  " + step);
                    if (!res.Ok)
                    {
                        foreach (var f in res.Validation.Errors.Take(10)) r.Line("  " + f.Plain);
                        r.Code = ExitCode.ValidationFailed;
                        r.Error = "The 1.12 check refused converted files: nothing can be saved.";
                        return r;
                    }
                    if (res.ChangeSet is { } cs)
                        r.Line($"Saved {Common.Plural(cs.Files.Count, "file")} into your workspace as one change set (\"{cs.Label}\", {cs.Id}); \"wds changes revert-set {cs.Id} --yes\" takes it back.");
                    else if (!inv.DryRun) return Common.NeedsYes(r, "This writes the converted map into your workspace (one change set; your game folder is not changed).");
                    return r;
                }
            },
        };
        foreach (var c in UpdateCommands()) yield return c;
    }

    // ------------------------------------------------------------------ the update catalog (0.14.0)

    private static readonly OptionSpec Rescan = new("rescan", null, "scan both sides again even when the cached catalog is current");

    /// <summary>The user's 1.12 game, read only (no index needed): its archives as the app opens them.</summary>
    private static (ClientData Data, string Dir) Game(Invocation inv)
    {
        var dir = inv.Ctx.ClientDir();
        try { return (new ClientData(Path.Combine(dir, "Data")), dir); }
        catch (Exception e) when (e is IOException or MpqException or UnauthorizedAccessException) { throw new CliError(ExitCode.Error, $"Your game's archives could not be opened: {e.Message}"); }
    }

    private static ClientCopy PickForCatalog(Invocation inv, out List<ClientCopy>? found)
    {
        found = null;
        var text = inv.Arg(0).Trim().Trim('"');
        if (!Directory.Exists(text) || inv.Opt("folder") != null || inv.Ctx.Settings.OtherClientsDir != null)
        {
            try { found = Found(inv); }
            catch (CliError) when (Directory.Exists(text)) { }
        }
        return Pick(inv, inv.Arg(0), found);
    }

    private static WoWData.Core.Versions.Catalog.UpdateCatalogSource OpenCatalog(Invocation inv, ClientCopy c, List<ClientCopy>? found, ClientData game)
    {
        var lf = inv.Opt("listfile") ?? inv.Ctx.Settings.OtherClientsListfile;
        if (lf != null && !File.Exists(lf)) throw new CliError(ExitCode.NotFound, $"the listfile \"{lf}\" does not exist.");
        inv.Ctx.Progress($"Opening {c.Name} ({c.Label}) and the names of your 1.12 files...");
        try { return WoWData.Core.Versions.Catalog.UpdateCatalogSource.Open(c, lf, game, found, Path.Combine(inv.Ctx.Paths.Root, "cache")); }
        catch (NotSupportedException e) { throw new CliError(ExitCode.Refused, e.Message); }
        catch (Exception e) when (e is IOException or InvalidDataException or WoWData.Core.Casc.CascMissingException or UnauthorizedAccessException)
        {
            throw new CliError(ExitCode.NotFound, $"{c.Name} could not be opened: {e.Message}");
        }
    }

    private static WoWData.Core.Versions.Catalog.UpdateCatalog LoadCatalog(Invocation inv, WoWData.Core.Versions.Catalog.UpdateCatalogSource src)
    {
        bool rescan = inv.Flag("rescan");
        if (rescan || WoWData.Core.Versions.Catalog.UpdateCatalogStore.LoadCached(src) == null)
            inv.Ctx.Progress($"Scanning {src.Label} against your game (the first scan takes a minute or two; the result is kept)...");
        string last = "";
        var progress = new SyncProgress<string>(p =>
        {
            var step = p.Contains("] ") ? p[(p.IndexOf("] ", StringComparison.Ordinal) + 2)..].Split(':')[0] : p;
            if (step == last) return;
            last = step;
            inv.Ctx.Progress("  " + p);
        });
        return WoWData.Core.Versions.Catalog.UpdateCatalogStore.LoadOrScan(src, rescan, progress);
    }

    private static object ItemJson(WoWData.Core.Versions.Catalog.UpdateItem i, int files = 0) => new
    {
        id = i.Id,
        kind = WoWData.Core.Versions.Catalog.UpdateNames.Key(i.Kind),
        kindText = i.KindText,
        @class = WoWData.Core.Versions.Catalog.UpdateNames.Key(i.Class),
        classText = i.ClassText,
        title = i.Title,
        where = i.Where,
        files = i.Files,
        bytes = i.Bytes,
        importable = i.Importable,
        notYet = i.NotYet,
        classReason = files >= 0 ? i.ClassReason : null,
        changes = files >= 0 ? i.Changes : null,
        needs = files >= 0 ? i.Needs : null,
        facts = files >= 0 ? i.Facts : null,
        terrain = i.Terrain == null ? null : new { mapId = i.Terrain.MapId, map = i.Terrain.MapName, directory = i.Terrain.MapDirectory, zoneId = i.Terrain.ZoneId, zone = i.Terrain.ZoneName, tiles = i.Terrain.Tiles, wholeMap = i.Terrain.WholeMap },
        fileList = files > 0 ? i.FileList?.Take(files).Select(f => new { path = f.Path, fileDataId = f.FileDataId, highRes = f.HighRes, size = f.Size, note = f.Note }) : null,
        fileCount = i.FileList?.Count ?? 0,
    };

    private static IEnumerable<CommandSpec> UpdateCommands()
    {
        yield return new CommandSpec
        {
            Path = "versions updates",
            Summary = "the update catalog: every difference between another client (the 1.60 Classic client) and your 1.12 game, grouped into updates you can pick (zone terrain, new maps, lighting, textures, models, interface, sounds, tables, engine features), each with its portability class",
            Args = new[] { "<client>" },
            Options = new[]
            {
                Folder, Listfile,
                new OptionSpec("kind", "kind", "only one kind: zone-terrain, new-map, sky-lighting, skybox, weather, ground-textures, textures, models, buildings, interface-art, interface-strings, fonts, sounds, data-tables, engine-features, other"),
                new OptionSpec("class", "class", "only one portability class: ready, needs-converter, needs-server, needs-client-code, no-visible-change"),
                new OptionSpec("zone", "text", "only updates whose zone, map, folder or table contains this text"),
                new OptionSpec("search", "words", "only updates with these words in the title, place, ID or facts"),
                new OptionSpec("importable", null, "only what the app can import now"),
                new OptionSpec("hide-unchanged", null, "leave out the differences without a visible change"),
                Rescan, Common.Limit,
            },
            Details = "The first run scans both sides (a minute or two: every 1.12 file against the client, changed textures pixel by pixel, every map tile chunk by chunk, every data table); " +
                      "the catalog is kept in the app's folder and scanned again when either side changes. Names come only from your 1.12 game's files, the listfile you chose and the client's own name lists.",
            Examples = new[]
            {
                "wds versions updates _classic_beta_",
                "wds versions updates _classic_beta_ --kind zone-terrain --zone \"Elwynn\"",
                "wds versions updates _classic_beta_ --class ready --importable --limit 0 --json",
            },
            NeedsClient = false,
            Run = inv =>
            {
                WoWData.Core.Versions.Catalog.UpdateKind? kind = null;
                WoWData.Core.Versions.Catalog.Portability? cls = null;
                if (inv.Opt("kind") is { } k)
                    kind = WoWData.Core.Versions.Catalog.UpdateNames.ParseKind(k) ?? throw new CliError(ExitCode.Usage,
                        $"--kind: no kind \"{k}\". Kinds: {string.Join(", ", Enum.GetValues<WoWData.Core.Versions.Catalog.UpdateKind>().Select(WoWData.Core.Versions.Catalog.UpdateNames.Key))}.");
                if (inv.Opt("class") is { } cl)
                    cls = WoWData.Core.Versions.Catalog.UpdateNames.ParseClass(cl) ?? throw new CliError(ExitCode.Usage,
                        $"--class: no class \"{cl}\". Classes: {string.Join(", ", Enum.GetValues<WoWData.Core.Versions.Catalog.Portability>().Select(WoWData.Core.Versions.Catalog.UpdateNames.Key))}.");
                var c = PickForCatalog(inv, out var found);
                var (game, _) = Game(inv);
                using (game)
                using (var src = OpenCatalog(inv, c, found, game))
                {
                    var cat = LoadCatalog(inv, src);
                    var filter = new WoWData.Core.Versions.Catalog.UpdateFilter
                    {
                        Kind = kind, Class = cls, Where = inv.Opt("zone"), Search = inv.Opt("search"),
                        OnlyImportable = inv.Flag("importable"), HideNoChange = inv.Flag("hide-unchanged"),
                    };
                    var items = cat.Filter(filter).ToList();
                    var shown = items.Take(Common.LimitOf(inv)).ToList();
                    var r = new CliResult().With(new
                    {
                        client = c.Name, source = cat.Source, target = cat.Target, scanned = cat.Scanned, seconds = cat.Seconds, names = cat.NamesText,
                        total = cat.Items.Count, matched = items.Count, importable = cat.Items.Count(i => i.Importable),
                        counts = cat.Counts().SelectMany(kv => kv.Value.Select(x => new
                        {
                            kind = WoWData.Core.Versions.Catalog.UpdateNames.Key(kv.Key), @class = WoWData.Core.Versions.Catalog.UpdateNames.Key(x.Key), count = x.Value,
                        })),
                        notes = cat.Notes,
                        items = shown.Select(i => ItemJson(i, -1)),
                    });
                    foreach (var l in cat.SummaryLines()) r.Line(l);
                    r.Line($"{Common.Plural(items.Count, "update")} match{(items.Count == 1 ? "es" : "")}" + (shown.Count < items.Count ? $" (showing {shown.Count}; --limit 0 for all)" : "") + ":");
                    foreach (var l in Common.Table(new[] { "ID", "Kind", "Class", "Files", "Size", "Title" },
                                 shown.Select(i => (IReadOnlyList<string>)new[] { i.Id, i.KindText, i.ClassText, i.Files.ToString("N0"), i.SizeText, i.Title }), 60))
                        r.Line(l);
                    r.Line("\"wds versions update-info <client> <id>\" shows one update; \"wds versions import-updates <client> <id>...\" imports.");
                    return Task.FromResult(r);
                }
            },
        };
        yield return new CommandSpec
        {
            Path = "versions update-info",
            Summary = "one update of the catalog: what it changes in your game, what it needs, why it has its class, the measured facts and its files",
            Args = new[] { "<client>", "<id>" },
            Options = new[] { Folder, Listfile, Rescan, Common.Limit },
            Details = "<id> is the update's ID in \"wds versions updates\" (for example terrain:0:12 for Elwynn Forest's terrain). --limit is the number of files listed (default 50).",
            Examples = new[] { "wds versions update-info _classic_beta_ terrain:0:12", "wds versions update-info _classic_beta_ tex:changed:creature --limit 0 --json" },
            NeedsClient = false,
            Run = inv =>
            {
                var c = PickForCatalog(inv, out var found);
                var (game, _) = Game(inv);
                using (game)
                using (var src = OpenCatalog(inv, c, found, game))
                {
                    var cat = LoadCatalog(inv, src);
                    var item = cat.Find(inv.Arg(1)) ?? throw new CliError(ExitCode.NotFound, $"the catalog of {c.Name} has no update \"{inv.Arg(1)}\" (wds versions updates \"{c.Name}\" lists them).");
                    int limit = Common.LimitOf(inv);
                    var r = new CliResult().With(new { client = c.Name, update = ItemJson(item, limit == int.MaxValue ? int.MaxValue : limit) });
                    foreach (var l in item.Lines(limit == int.MaxValue ? int.MaxValue : limit)) r.Line(l);
                    return Task.FromResult(r);
                }
            },
        };
        yield return new CommandSpec
        {
            Path = "versions import-updates",
            Summary = "import picked updates of the catalog into your 1.12 workspace as one change set (terrain through the map conversion, textures, interface art, fonts and MP3 sounds made loadable by 1.12); --dry-run only reports",
            Args = new[] { "<client>", "<ids...>" },
            Options = new[] { Folder, Listfile, Common.DryRun },
            Details = "Nothing is written without --yes (without it the report is shown and the exit code is 5). Updates that cannot be imported yet are listed with why. " +
                      "\"wds changes revert-set <id> --yes\" takes the whole import back.",
            Examples = new[]
            {
                "wds versions import-updates _classic_beta_ terrain:0:12 --dry-run",
                "wds versions import-updates _classic_beta_ terrain:0:12 terrain:0:40 tex:changed:creature --yes",
                "wds versions import-updates _classic_beta_ fonts:changed --yes --json",
            },
            NeedsClient = false,
            Run = inv =>
            {
                var c = PickForCatalog(inv, out var found);
                var (game, dir) = Game(inv);
                using (game)
                using (var src = OpenCatalog(inv, c, found, game))
                {
                    var cat = LoadCatalog(inv, src);
                    var ws = new WoWData.Core.Workspace.Workspace(inv.Ctx.Paths.ForClient(dir));
                    var target = new MapConvertTarget { Client = game, Workspace = ws, Dbc = new DbcService(game, ws), Locale = WoWData.Core.Session.AppSession.LocaleOf(dir) };
                    bool write = inv.Yes && !inv.DryRun;
                    string last = "";
                    var progress = new SyncProgress<string>(p =>
                    {
                        var step = p.Split(':')[0];
                        if (step == last) return;
                        last = step;
                        inv.Ctx.Progress(p);
                    });
                    WoWData.Core.Versions.Catalog.UpdateImportResult res;
                    try { res = WoWData.Core.Versions.Catalog.UpdateImporter.Run(src, cat, inv.Args.Skip(1).ToList(), target, !write, progress); }
                    catch (ArgumentException e) { throw new CliError(ExitCode.NotFound, e.Message); }
                    catch (WoWData.Core.Workspace.WorkspaceException e) { return Task.FromResult(CliResult.Fail(ExitCode.ValidationFailed, e.Message)); }
                    var r = new CliResult().With(new
                    {
                        client = c.Name, dryRun = res.DryRun,
                        imported = res.Imported.Select(i => i.Id), notYet = res.NotYet.Select(x => new { id = x.Item.Id, why = x.Why }),
                        files = res.Files.Select(f => new { path = f.Path, update = f.Item, fileDataId = f.FileDataId, isNew = f.IsNew, size = f.Size, summary = f.Summary, replacesYourEdit = f.ReplacesYourEdit }),
                        fileProblems = res.FileProblems,
                        maps = res.Maps.Select(m => ConversionJson(m, c)),
                        warnings = res.Warnings,
                        changeSet = res.ChangeSet == null ? null : new { id = res.ChangeSet.Id, label = res.ChangeSet.Label, files = res.ChangeSet.Files.Count },
                        seconds = Math.Round(res.Elapsed.TotalSeconds, 1),
                    });
                    foreach (var l in res.Lines()) r.Line(l);
                    if (!res.Ok)
                    {
                        foreach (var f in res.Maps.SelectMany(m => m.Validation.Errors).Take(10)) r.Line("  " + f.Plain);
                        r.Code = ExitCode.ValidationFailed;
                        r.Error = "The 1.12 check refused converted files: nothing was saved.";
                        return Task.FromResult(r);
                    }
                    if (res.Imported.Count == 0) { r.Code = ExitCode.Refused; r.Error = "None of these updates can be imported yet (see why above); nothing was changed."; return Task.FromResult(r); }
                    if (res.ChangeSet is { } cs)
                        r.Line($"Saved {Common.Plural(cs.Files.Count, "file")} into your workspace as one change set (\"{cs.Label}\", {cs.Id}); \"wds changes revert-set {cs.Id} --yes\" takes it back.");
                    else if (!inv.DryRun) return Task.FromResult(Common.NeedsYes(r, "This writes the picked updates into your workspace (one change set; your game folder is not changed)."));
                    return Task.FromResult(r);
                }
            },
        };
    }

    private static object ConversionJson(MapConversionResult res, ClientCopy c) => new
    {
        client = c.Name,
        dryRun = res.DryRun,
        source = new { id = res.Source.Id, directory = res.Source.Directory, name = res.Source.Name },
        target = new { mapId = res.TargetMapId, directory = res.TargetDirectory, name = res.TargetName, newMap = res.NewMap },
        selection = res.SelectionText,
        tiles = res.Tiles.Select(t => $"{t.X}_{t.Y}"),
        newTiles = res.NewTiles,
        alsoCovers = res.AlsoCovers.Select(z => new { zone = z.Zone, chunks = z.Chunks }),
        names = res.NamesText,
        textures = new
        {
            reused = res.TexturesReused, reusedUses = res.TextureUsesReused,
            converted = res.TexturesConverted.Select(t => new { path = t.Path, fileDataId = t.FileDataId, width = t.Width, height = t.Height, sourceWidth = t.SourceWidth, sourceHeight = t.SourceHeight, uses = t.Uses }),
            missing = res.TexturesMissing,
        },
        doodads = new { kept = res.DoodadsKept, kept112 = res.DoodadsKept112, leftOut = res.DoodadsLeftOut },
        wmos = new { kept = res.WmosKept, kept112 = res.WmosKept112, leftOut = res.WmosLeftOut },
        leftOut = res.LeftOut.OrderByDescending(a => a.Placements).Select(a => new { name = a.Name, kind = a.Kind, placements = a.Placements }),
        areas = new
        {
            kept = res.AreasKept, unknownChunks = res.UnknownAreaChunks,
            added = res.NewAreas.Select(a => new { sourceId = a.SourceId, id = a.Id, name = a.Name, parentId = a.ParentId, mapId = a.MapId, areaBit = a.AreaBit, chunks = a.Chunks }),
        },
        shadows = new { reused = res.ShadowsReused, lostResculpted = res.ShadowsLostResculpted, source160Dropped = res.Shadows160Dropped },
        minimaps = new { converted = res.Minimaps, missing = res.MinimapsMissing },
        report = res.Report.Totals().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        tables = res.TableNotes,
        files = res.Files.Select(f => new { path = f.Path, kind = f.Kind, isNew = f.IsNew, size = f.Size, replacesYourEdit = f.ReplacesYourEdit }),
        validation = new { ok = res.Ok, errors = res.Validation.Errors.Select(f => f.Plain), warnings = res.Validation.Warnings.Select(f => f.Plain) },
        warnings = res.Warnings,
        serverSteps = res.ServerSteps,
        changeSet = res.ChangeSet == null ? null : new { id = res.ChangeSet.Id, label = res.ChangeSet.Label, files = res.ChangeSet.Files.Count },
        seconds = Math.Round(res.Elapsed.TotalSeconds, 1),
    };
}
