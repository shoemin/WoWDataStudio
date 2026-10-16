using WoWData.Core.Index;
using WoWData.Core.Mpq;
using WoWData.Core.Usage;

namespace WoWData.Cli.Commands;

/// <summary>find: the Browse tab (search, filters, details, Used in game) and where files come from.</summary>
internal static class FindCommands
{
    private static readonly string KindsText = "creature, character, item, gameobject, worldobject, spelleffect, texture";

    private static HashSet<AssetKind>? Kinds(Invocation inv)
    {
        var list = inv.Opts("type").SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (list.Count == 0) return null;
        var set = new HashSet<AssetKind>();
        foreach (var t in list)
            set.Add(EntrySearch.KindNames.TryGetValue(t, out var k) ? k : throw new CliError(ExitCode.Usage, $"unknown type \"{t}\": {KindsText}."));
        return set;
    }

    private static CliResult Hits(Invocation inv, List<EntryHit> hits, string what)
    {
        int limit = Common.LimitOf(inv);
        var shown = hits.Take(limit).ToList();
        var r = new CliResult().With(new { total = hits.Count, shown = shown.Count, results = shown.Select(h => Common.EntryJson(h.Entry, h.Badge)) });
        r.Line(hits.Count == 1 ? "1 result" + what : $"{hits.Count:N0} results{what}" + (shown.Count < hits.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
        foreach (var l in Common.Table(new[] { "Name", "Type", "ID", "Key", "Status" },
                     shown.Select(h => (IReadOnlyList<string>)new[] { h.Entry.Name, h.Entry.KindLabel, h.Entry.Id > 0 && h.Entry.Kind != AssetKind.Character ? h.Entry.Id.ToString() : "", h.Entry.Key, h.Badge }), 70))
            r.Line(l);
        return r;
    }

    private static (HashSet<string> Paths, HashSet<string> Owners) Modified(Core.Session.AppSession s)
    {
        var paths = s.Workspace!.ModifiedPaths();
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var owners = paths.Count == 0 || s.Index == null ? new HashSet<string>() : s.Index.OwnersOfTextures(paths);
        return (set, owners);
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "find search",
            Summary = "search like the Browse tab: every word must be in the name, path or id; type filters, only my changes, a zone",
            Args = new[] { "[words...]" },
            Options = new[]
            {
                new OptionSpec("type", "types", $"only these types (comma-separated): {KindsText}", true),
                new OptionSpec("only-changes", null, "only what you changed and what uses it (\"Show only my changes\")"),
                new OptionSpec("zone", "name", "only what is in a zone or subzone: creatures and game objects spawned there (needs the server database) and world objects placed there"),
                new OptionSpec("sort", "column", "name (default; best matches first), type, id or path"),
                new OptionSpec("desc", null, "the other direction"),
                Common.Limit,
            },
            Examples = new[] { "wds find search mottled boar", "wds find search orc male --type character", "wds find search --type creature --zone Durotar --limit 0 --json", "wds find search --only-changes" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var (paths, owners) = Modified(s);
                HashSet<string>? keys = null;
                if (inv.Opt("zone") is { } zone)
                {
                    keys = s.Index!.KeysInZone(zone);
                    if (keys.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"no zone or subzone \"{zone}\" with spawns or placements in the index (spawns need the server database: wds serverdb reload).");
                }
                var sort = (inv.Opt("sort") ?? "name").ToLowerInvariant() switch
                {
                    "name" => EntrySort.Name, "type" => EntrySort.Type, "id" => EntrySort.Id, "path" => EntrySort.Path,
                    var x => throw new CliError(ExitCode.Usage, $"--sort is name, type, id or path (not {x})."),
                };
                var q = new EntryQuery(string.Join(' ', inv.Args), Kinds(inv), inv.Flag("only-changes"), sort, !inv.Flag("desc"), keys);
                return Hits(inv, EntrySearch.Run(s.Entries, q, paths, owners), inv.Args.Count > 0 ? $" for \"{string.Join(' ', inv.Args)}\"" : "");
            },
        };
        yield return new CommandSpec
        {
            Path = "find list",
            Summary = "list everything of one or more types (\"Show only this type\")",
            Options = new[] { new OptionSpec("type", "types", $"the types (comma-separated): {KindsText}", true), Common.Limit },
            Examples = new[] { "wds find list --type character", "wds find list --type spelleffect --limit 0 --json" },
            Run = async inv =>
            {
                var kinds = Kinds(inv) ?? throw new CliError(ExitCode.Usage, $"give --type ({KindsText}).");
                var s = await inv.Ctx.SessionAsync();
                var (paths, owners) = Modified(s);
                return Hits(inv, EntrySearch.Run(s.Entries, new EntryQuery("", kinds), paths, owners), "");
            },
        };
        yield return new CommandSpec
        {
            Path = "find info",
            Summary = "the details panel of an entry: where it comes from (source chain), facts, models, the textures it uses, used by",
            Args = new[] { "<entry>" },
            Details = "The entry is a key (creature:3098, item:25, cdisplay:503, go:1731, file:<path>), a game file path, or a name (the best match).",
            Examples = new[] { "wds find info \"Mottled Boar\"", "wds find info creature:3098 --json", "wds find info Creature\\Boar\\BoarSkinIvory.blp" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Common.FindEntry(s, inv.Arg(0));
                var d = s.Index!.Details(e, s.Catalog);
                var facts = d.Facts.ToList();
                if (e.Kind == AssetKind.Texture && e.Path != null) facts.AddRange(s.TextureAdvice(e.Path));
                var ws = s.Workspace!;
                var r = new CliResult().With(new
                {
                    entry = Common.EntryJson(e), title = d.Title, subtitle = d.Subtitle, chain = d.Chain, facts, models = d.Models,
                    textures = d.Textures.Select(t => new { path = t.Path, role = t.Role, archive = t.Archive, note = t.Note, modified = ws.IsModified(t.Path) }),
                    usedBy = d.UsedBy,
                });
                r.Line($"{d.Title}  ({e.KindLabel}, {e.Key})");
                if (d.Subtitle.Length > 0) r.Line(d.Subtitle);
                if (d.Chain.Count > 0) r.Line("").Line("WHERE IT COMES FROM").Line("  " + string.Join(" -> ", d.Chain));
                if (facts.Count > 0) { r.Line(""); foreach (var f in facts) r.Line("* " + f); }
                if (d.Models.Count > 0) { r.Line("").Line("MODELS"); foreach (var m in d.Models) r.Line("  " + m); }
                if (d.Textures.Count > 0)
                {
                    r.Line("").Line("TEXTURES IT USES");
                    foreach (var t in d.Textures) r.Line($"  {t.Role,-16} {t.Path}{(t.Archive == null ? "  (not in the game archives)" : "")}{(ws.IsModified(t.Path) ? "  [Modified]" : "")}{(t.Note.Length > 0 ? "  " + t.Note : "")}");
                }
                if (d.UsedBy.Count > 0) { r.Line("").Line("Used by"); foreach (var u in d.UsedBy.Take(40)) r.Line("  " + u); if (d.UsedBy.Count > 40) r.Line($"  ... and {d.UsedBy.Count - 40} more"); }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "find used",
            Summary = "\"Used in game\": NPCs and their spawns, loot, vendors, quests, placements, spells, what a texture edit affects",
            Args = new[] { "<entry>" },
            Examples = new[] { "wds find used \"Mottled Boar\"", "wds find used Creature\\Boar\\BoarSkinIvory.blp --json", "wds find used item:2880" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var e = Common.FindEntry(s, inv.Arg(0));
                var rep = s.UsageReport(e.Key)!;
                var r = new CliResult().With(ReportJson(rep));
                r.Line($"Used in game: {e.Name} ({e.KindLabel}){(rep.Short.Length > 0 ? " · " + rep.Short : "")}");
                AppendReport(r, rep);
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "find where",
            Summary = "which archive serves a file and which copies it overrides (and your workspace edit, a loose copy in the game folder)",
            Args = new[] { "<path>" },
            Examples = new[] { "wds find where Creature\\Boar\\BoarSkinIvory.blp", "wds find where DBFilesClient\\Spell.dbc --json" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = ClientData.ResolveName(inv.Arg(0).Replace('/', '\\'));
                var sources = s.Data!.Where(path);
                var ws = s.Workspace!;
                var edited = ws.Get(path);
                var loose = Path.Combine(s.Client!.DataDir, path);
                bool looseExists = File.Exists(loose);
                if (sources.Count == 0 && edited == null && !looseExists) return CliResult.Fail(ExitCode.NotFound, $"{path} is in no archive of the game, not in your workspace and not in the game folder.");
                var serving = sources.FirstOrDefault(x => x.Kind == SourceKind.Archive);
                var r = new CliResult().With(new
                {
                    path,
                    serves = edited != null ? "workspace" : serving?.Name,
                    sources = sources.Select(x => new { kind = x.Kind, archive = x.Name }),
                    workspace = edited == null ? null : new { file = ws.WorkspaceFile(path), edited = edited.Edited, changeSet = edited.ChangeSet },
                    looseFile = looseExists ? loose : null,
                    looseInstalledByApp = edited?.Installed != null && looseExists,
                });
                r.Line(path);
                if (edited != null) r.Line($"  your edit (workspace, {edited.Edited:yyyy-MM-dd HH:mm}): what the app shows; the game uses it once you test or release it");
                if (looseExists) r.Line($"  loose file in the game folder{(edited?.Installed != null ? " (installed by WoW Data Studio)" : "")}: {loose} (read by the game only in its loose-file mode)");
                foreach (var x in sources)
                    r.Line($"  {x.Name,-14} " + x.Kind switch
                    {
                        SourceKind.Archive when ReferenceEquals(x, serving) => "serves the file" + (sources.Count(y => y.Kind is SourceKind.Hidden or SourceKind.Archive) > 1 ? " (overrides the copies below)" : ""),
                        SourceKind.Archive => "has a copy",
                        SourceKind.Hidden => "has a copy, hidden by a delete marker above",
                        SourceKind.Deleted => "deletes the file (a delete marker: the game cannot load it)",
                        _ => x.Kind.ToString(),
                    });
                if (Core.Dbc.DbcService.TableOf(path) is { } table && s.Dbc is { } dbc)
                {
                    var info = dbc.Info(table);
                    r.Line("  " + info.SourceText);
                    if (info.OverrideText.Length > 0) r.Line("  " + info.OverrideText);
                }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "find files",
            Summary = "list the game's files by wildcard (* and ?), with the archive that serves each",
            Args = new[] { "<pattern>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds find files Creature\\Boar\\*", "wds find files *.dbc --limit 0 --json", "wds find files Tileset\\Durotar\\*_s.blp" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var names = s.Data!.Names();
                var files = s.Data.Glob(inv.Arg(0).Replace('/', '\\'));
                int limit = Common.LimitOf(inv);
                var shown = files.Take(limit).ToList();
                var ws = s.Workspace!;
                var r = new CliResult().With(new { total = files.Count, files = shown.Select(f => new { path = f, archive = names.TryGetValue(f.ToUpperInvariant(), out var lf) ? lf.Archive : null, modified = ws.IsModified(f) }) });
                r.Line($"{files.Count:N0} files" + (shown.Count < files.Count ? $" (showing {shown.Count}; --limit 0 for all)" : ""));
                foreach (var f in shown) r.Line($"{(names.TryGetValue(f.ToUpperInvariant(), out var lf2) ? lf2.Archive : ""),-14} {f}{(ws.IsModified(f) ? "  [Modified]" : "")}");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "find extract",
            Summary = "copy a game file out as it is (your workspace version when you edited it; --original for the game's)",
            Args = new[] { "<path>" },
            Options = new[] { new OptionSpec("out", "file", "where to write it (default: the file name in the current folder; a folder: the file name in it)"), new OptionSpec("original", null, "the game's file, not your edit") },
            Examples = new[] { "wds find extract DBFilesClient\\Spell.dbc --out Spell.dbc", "wds find extract Creature\\Boar\\Boar.m2 --out models\\ --original" },
            Run = async inv =>
            {
                var s = await inv.Ctx.SessionAsync();
                var path = Common.GamePath(s, inv.Arg(0));
                bool edited = !inv.Flag("original") && s.Workspace!.IsModified(path);
                var data = (edited ? s.Workspace!.ReadEdited(path) : s.Data!.Read(path)) ?? throw new CliError(ExitCode.NotFound, $"{path} is not in the game archives.");
                var outPath = inv.Opt("out") ?? Path.GetFileName(path);
                if (Directory.Exists(outPath) || outPath.EndsWith('\\') || outPath.EndsWith('/')) outPath = Path.Combine(outPath, Path.GetFileName(path));
                outPath = Path.GetFullPath(outPath);
                if (IsInside(outPath, s.Client!.DataDir)) throw new CliError(ExitCode.Refused, "Refusing to write into the game's Data folder: extract somewhere else (\"wds changes install\" puts edits into the game).");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await File.WriteAllBytesAsync(outPath, data);
                return new CliResult().With(new { path, file = outPath, bytes = data.Length, source = edited ? "workspace" : "game" }).Line($"{path} ({(edited ? "your edit" : "the game's")}, {data.Length:N0} bytes) -> {outPath}");
            },
        };
    }

    public static bool IsInside(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static object ReportJson(UsageReport rep) => new
    {
        key = rep.Key,
        headline = rep.Headline.Text,
        summary = rep.Short,
        serverConnected = rep.ServerConnected,
        needsServer = rep.NeedsServer,
        sections = rep.Sections.Select(sec => new
        {
            id = sec.Id, title = sec.Title, summary = sec.Summary, more = sec.More,
            lines = sec.Lines.Select(l => new { text = l.Text, indent = l.Indent, links = l.Spans.Where(sp => sp.Key != null).Select(sp => new { text = sp.Text, key = sp.Key }) }),
            tail = sec.Tail.Select(l => l.Text),
        }),
        notes = rep.Notes,
        milliseconds = rep.Milliseconds,
    };

    public static void AppendReport(CliResult r, UsageReport rep)
    {
        if (rep.Headline.Text.Length > 0) r.Line(rep.Headline.Text);
        foreach (var sec in rep.Sections)
        {
            r.Line("").Line(sec.Title.ToUpperInvariant() + (sec.Summary.Length > 0 ? ": " + sec.Summary : ""));
            foreach (var l in sec.Lines) r.Line(new string(' ', 2 + 2 * l.Indent) + l.Text);
            if (sec.More > 0) r.Line($"  and {sec.More:N0} more");
            foreach (var l in sec.Tail) r.Line(new string(' ', 2 + 2 * l.Indent) + l.Text);
        }
        foreach (var n in rep.Notes) r.Line("").Line(n);
    }
}
