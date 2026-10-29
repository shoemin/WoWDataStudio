using WoWData.Core.Session;
using WoWData.Core.Versions;

namespace WoWData.Cli.Commands;

/// <summary>
/// "wds dbc tables / info / rows / get / export --version": the Data tables tab of another version opened read only
/// (the app's read-only table viewer): its DB2 tables with column names from the WoWDBDefs folder you chose (setting
/// other-clients.dbd), else the ones our layout file knows for the build, else col_N (M5), rows found by words and column filters, and exported as CSV or JSON to a folder outside the client. Every
/// other dbc command changes files and refuses --version.
/// </summary>
internal static class DbcCommandsOther
{
    private static OtherClient Client(AppSession s) => s.Other ?? throw new CliError(ExitCode.Error, "No other version is open.");

    private static VersionTable Table(AppSession s, string name)
    {
        VersionTable? t;
        try { t = Client(s).TableView(name, s.Profile!.Build); }
        catch (Exception e) when (e is WoWData.Core.Db2.Db2FormatException or ClientFileException or InvalidDataException)
        {
            throw new CliError(ExitCode.NotFound, $"{name} of {s.Profile!.VersionBuild} could not be read: {e.Message}");
        }
        return t ?? throw new CliError(ExitCode.NotFound, $"{s.Profile!.VersionBuild} has no table \"{name}\" on this computer (\"wds dbc tables --version ...\" lists them).");
    }

    private static int Col(VersionTable t, string name)
    {
        int c = VersionTableExport.Column(t, name);
        return c >= 0 ? c : throw new CliError(ExitCode.NotFound, $"{t.Name} has no column \"{name}\". Its columns: {string.Join(", ", t.Columns)}.");
    }

    /// <summary>--search, --where (column=value: text contains, numbers equal) and --ids.</summary>
    private static List<int> Query(Invocation inv, VersionTable t)
    {
        var where = new List<(int, string)>();
        foreach (var w in inv.Opts("where"))
        {
            int eq = w.IndexOf('=');
            if (eq <= 0) throw new CliError(ExitCode.Usage, $"--where is column=value (not {w}).");
            where.Add((Col(t, w[..eq].Trim()), w[(eq + 1)..].Trim().TrimStart('=')));
        }
        var rows = VersionTableExport.Find(t, inv.Opt("search") ?? "", where);
        if (inv.Opt("ids") is { } ids)
        {
            var set = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => long.TryParse(x, out var v) ? v : throw new CliError(ExitCode.Usage, $"--ids are numbers (not {x}).")).ToHashSet();
            rows = rows.Where(i => set.Contains(t.IdAt(i))).ToList();
        }
        return rows;
    }

    public static CliResult Tables(Invocation inv, AppSession s)
    {
        var oc = Client(s);
        var words = (inv.Opt("filter") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = oc.TableNames().Where(n => words.All(w => n.ToLowerInvariant().Contains(w)))
            .Select(n => (Name: n, Fields: oc.Catalog.Get(n)?.FieldCount ?? 0, Names: oc.ExpectedNaming(n, s.Profile!.Build))).ToList();
        if (inv.Flag("changed")) list.Clear();   // another version has no workspace: nothing is changed
        var r = new CliResult().With(new
        {
            version = s.Profile!.VersionBuild, readOnly = true, dbd = oc.Dbd?.Folder,
            tables = list.Select(x => new { name = x.Name, fields = x.Fields, columns = x.Names.Source == ColumnNameSource.None ? "raw" : "named", names = x.Names.Key, namesFallback = x.Names.Fallback }),
        });
        foreach (var l in Common.Table(new[] { "Table", "Fields", "Column names" }, list.Select(x => (IReadOnlyList<string>)new[] { x.Name, x.Fields.ToString(), x.Names.Text }), 50))
            r.Line(l);
        r.Line($"{list.Count} tables of {s.Profile.VersionBuild} (read only).");
        r.Line(oc.Dbd != null
            ? $"Column names: {list.Count(x => x.Names.Source == ColumnNameSource.WoWDBDefs)} tables from {oc.Dbd.Attribution} (checked when a table is read); {list.Count(x => x.Names.Source == ColumnNameSource.Measured)} from our measured layout file."
            : $"Column names: {list.Count(x => x.Names.Source == ColumnNameSource.Measured)} tables from our measured layout file, the rest raw (col_N). A WoWDBDefs folder names the others: wds settings set other-clients.dbd <folder>.");
        return r;
    }

    public static CliResult Info(Invocation inv, AppSession s)
    {
        var t = Table(s, inv.Arg(0));
        var r = new CliResult().With(new
        {
            name = t.Name, version = s.Profile!.VersionBuild, readOnly = true, rows = t.RowCount, fields = t.FieldCount, coverage = t.Coverage, raw = t.IsRaw, note = t.Note,
            names = t.Naming.Key, namesFallback = t.Naming.Fallback, dbd = t.Naming.Source == ColumnNameSource.WoWDBDefs ? Client(s).Dbd?.Attribution : null,
            columns = t.Columns.Select((c, i) => new { index = i, name = c, named = t.Named[i], text = t.IsText[i] }),
        });
        r.Line($"{t.Name} ({s.Profile.VersionBuild}, read only): {t.RowCount:N0} rows, {t.FieldCount} fields, {t.Coverage}." + (t.Note.Length > 0 ? " " + t.Note + "." : ""));
        r.Line(VersionCommands.NamesLine(t, Client(s)));
        bool dbd = t.Naming.Source == ColumnNameSource.WoWDBDefs;
        foreach (var l in Common.Table(new[] { "#", "Column", "Type" }, t.Columns.Select((c, i) => (IReadOnlyList<string>)new[] { i.ToString(), c + (t.Named[i] ? "" : c.StartsWith("col_", StringComparison.Ordinal) || !dbd ? " (not named)" : " (not confirmed)"), t.IsText[i] ? "text" : "number" }), 80))
            r.Line(l);
        return r;
    }

    public static CliResult Rows(Invocation inv, AppSession s)
    {
        var t = Table(s, inv.Arg(0));
        var rows = Query(inv, t);
        var cols = inv.Opt("columns") is { } cl ? cl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => Col(t, n)).ToList() : Enumerable.Range(0, t.Columns.Count).ToList();
        if (inv.Opt("sort") is { } sort)
        {
            int sc = Col(t, sort);
            Func<int, IComparable> key = t.IsText[sc] ? i => t.RowAt(i)[sc] : i => double.TryParse(t.RowAt(i)[sc], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            rows = (inv.Flag("desc") ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();
        }
        var shown = rows.Take(Common.LimitOf(inv)).ToList();
        var r = new CliResult().With(new
        {
            table = t.Name, version = s.Profile!.VersionBuild, readOnly = true, total = t.RowCount, matched = rows.Count, shown = shown.Count, names = t.Naming.Key, columns = cols.Select(c => t.Columns[c]),
            rows = shown.Select(i => { var v = t.RowAt(i); return cols.ToDictionary(c => t.Columns[c], c => (object)v[c]); }),
        });
        r.Line((rows.Count == t.RowCount ? $"{t.RowCount:N0} rows" : $"{rows.Count:N0} of {t.RowCount:N0} rows") + $" of {t.Name} ({s.Profile.VersionBuild}, read only)" +
               (shown.Count < rows.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
        if (t.IsRaw) r.Warnings.Add($"{t.Name} has no known layout for {s.Profile.VersionBuild} yet: raw columns.");
        if (t.Naming.Fallback.Length > 0) r.Warnings.Add($"column names {t.Naming.Text}, not WoWDBDefs: {t.Naming.Fallback}");
        if (t.Note.Length > 0) r.Warnings.Add(t.Note);
        foreach (var l in Common.Table(cols.Select(c => t.Columns[c]).ToList(), shown.Select(i => { var v = t.RowAt(i); return (IReadOnlyList<string>)cols.Select(c => v[c]).ToList(); }), 40)) r.Line(l);
        return r;
    }

    public static CliResult Get(Invocation inv, AppSession s)
    {
        var t = Table(s, inv.Arg(0));
        long id = inv.Long(1);
        var row = t.Row(id) ?? throw new CliError(ExitCode.NotFound, $"{t.Name} of {s.Profile!.VersionBuild} has no row with ID {id} (or it is locked: encrypted, no key).");
        if (inv.ArgOrNull(2) is { } colName)
        {
            int c = Col(t, colName);
            return new CliResult().With(new { table = t.Name, id, column = t.Columns[c], value = row[c], readOnly = true }).Line(row[c]);
        }
        var r = new CliResult().With(new { table = t.Name, id, version = s.Profile!.VersionBuild, readOnly = true, values = t.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => (object)row[x.i]) });
        r.Line($"{t.Name} {id} ({s.Profile.VersionBuild}, read only)");
        for (int i = 0; i < t.Columns.Count; i++) r.Line($"  {t.Columns[i],-28} {row[i]}");
        return r;
    }

    public static CliResult Export(Invocation inv, AppSession s)
    {
        var t = Table(s, inv.Arg(0));
        var file = Path.GetFullPath(inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "give --out <file.csv|file.json>."));
        try { WoWData.Core.M2.Modern.ModernModelExport.CheckOutsideClient(s, file); }
        catch (IOException e) { throw new CliError(ExitCode.Refused, e.Message); }
        var rows = Query(inv, t);
        if (rows.Count == 0) return CliResult.Fail(ExitCode.NotFound, "There are no rows to export.");
        VersionTableExport.Write(t, rows, file);
        return new CliResult().With(new { table = t.Name, version = s.Profile!.VersionBuild, readOnly = true, file, rows = rows.Count, format = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv" })
            .Line($"Exported {rows.Count:N0} rows of {t.Name} ({s.Profile.VersionBuild}, read only) to {file}.");
    }
}
