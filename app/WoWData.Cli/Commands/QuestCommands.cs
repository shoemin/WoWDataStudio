using System.Globalization;
using WoWData.Core.Designers;

namespace WoWData.Cli.Commands;

/// <summary>quests: the quest-chain designer of the Quest chains tab (0.13.0): find and show quests, the chain as a
/// flowchart (lanes, columns, links), the editor's fields, new quests before / after / as branches, joins, givers and
/// enders, objectives (kill, use, cast, collect with drop sources, explore, speak to), rewards (items, choices, money,
/// experience, reputation, a learned spell), new quest-giver NPCs placed on the map, the same chain for the other
/// faction, the "discipline chain" template, the checks, the text preview, CSV / JSON and the test-in-game helper.
/// Quests live on the server: every command needs the server's world database (Settings). Saved into the workspace at
/// once; --dry-run shows and saves nothing; "wds server apply" writes the rows into the database.</summary>
internal static class QuestCommands
{
    private static async Task<(DesignerCli.Ctx C, QuestDesigner Q)> Open(Invocation inv, CliResult r)
    {
        var c = await DesignerCli.Open(inv, r);
        if (!c.D.HasServer) throw new CliError(ExitCode.Error, QuestDesigner.NoServer + (c.D.ServerNote.Length > 0 ? " (" + c.D.ServerNote + ")" : ""));
        return (c, new QuestDesigner(c.D, c.Spells));
    }

    private static long QuestArg(Invocation inv, QuestDesigner q, int i)
    {
        long id = DesignerCli.Id(inv, i, "quest");
        if (!q.Exists(id)) throw new CliError(ExitCode.NotFound, $"There is no quest {id} on the server (quest_template).");
        return id;
    }

    private static void Do(CliResult r, DesignResult res)
    {
        if (!res.Ok) throw new CliError(res.Message.StartsWith("There is no", StringComparison.Ordinal) ? ExitCode.NotFound : ExitCode.ValidationFailed, res.Message);
        r.Line(res.Message);
        if (res.Problems != null) DesignerCli.AddProblems(r, res.Problems);
    }

    private static object ProblemsJson(IEnumerable<QuestProblem> p) => p.Select(x => new { quest = x.Quest, title = x.Title, severity = x.Problem.Severity, field = x.Problem.Field, message = x.Problem.Message });

    private static object QuestJson(QuestDesigner q, long id) => new
    {
        id,
        title = q.Title(id),
        fields = QuestForm.Fields.Where(f => q.Quests.Has(f.Column)).ToDictionary(f => f.Id, f => q.Show(id, f)),
        givers = q.Givers(id).Select(g => new { kind = g.Kind, id = g.Id, name = g.Name, spawns = g.Spawns, note = g.Note }),
        enders = q.Enders(id).Select(g => new { kind = g.Kind, id = g.Id, name = g.Name, spawns = g.Spawns, note = g.Note }),
        objectives = q.Objectives(id).Select(o => new { kind = o.Kind, slot = o.Slot, id = o.Id, name = o.Name, count = o.Count, text = o.Text }),
        problems = ProblemsJson(QuestRules.Check(q, new[] { id })),
    };

    private static object ChainJson(QuestChain c) => new
    {
        start = c.Start,
        truncated = c.Truncated,
        lanes = c.Lanes.Select(l => new { index = l.Index, name = l.Name, races = l.Races, classes = l.Classes }),
        quests = c.Nodes.Select(n => new { id = n.Id, title = n.Title, level = n.Level, minLevel = n.MinLevel, races = n.Races, classes = n.Classes, lane = n.LaneIndex, column = n.Column, row = n.Row, group = n.Group, givers = n.Givers, enders = n.Enders, errors = n.Errors, warnings = n.Warnings, edited = n.Edited }),
        links = c.Edges.Select(e => new { from = e.From, to = e.To, kinds = e.Kinds.ToString(), text = e.Text }),
        groups = c.Groups.Select(g => new { id = g.Id, choice = g.IsChoice, quests = g.Quests, text = g.Text }),
        outside = c.Outside,
    };

    private static CliResult Save(Invocation inv, CliResult r, DesignerCli.Ctx c, QuestDesigner q, Func<object?> data) =>
        DesignerCli.Finish(inv, r, () => q.Save(), data, c.D);

    private static void ApplySets(Invocation inv, CliResult r, QuestDesigner q, long id)
    {
        foreach (var kv in inv.Opts("set"))
        {
            var (f, v) = DesignerCli.Pair(kv);
            if (QuestForm.Find(f) == null) throw new CliError(ExitCode.Usage, $"there is no quest field \"{f}\" (\"wds quests fields\" lists them).");
            Do(r, q.Set(id, f, v));
        }
    }

    private static (long Id, long Count) IdCount(string text, string option, long defCount = 1)
    {
        var p = text.Split(':');
        var idText = p.Length >= 2 && !long.TryParse(p[0], out _) ? p[0] + ":" + p[1] : p[0];
        var countText = p.Length >= 2 && !long.TryParse(p[0], out _) ? p.ElementAtOrDefault(2) : p.ElementAtOrDefault(1);
        long id = idText.Contains(':') ? QuestForm.ParseTarget(idText) : long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new CliError(ExitCode.Usage, $"--{option} is an ID with an optional :count (\"{text}\").");
        long count = countText == null ? defCount : long.TryParse(countText, out var c) ? c : throw new CliError(ExitCode.Usage, $"--{option}: \"{countText}\" is not a count.");
        return (id, count);
    }

    private static readonly OptionSpec SetOpt = new("set", "field=value", "a field of the quest (\"wds quests fields\" lists them; class and race names, 1g 50s, creature:<ID>)", true);

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "quests find",
            Summary = "quests by words of their title or by ID (the server's quests with your edits)",
            Args = new[] { "<words...>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds quests find whirlwind", "wds quests find 1718 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                var hits = q.Find(string.Join(" ", inv.Args), Common.LimitOf(inv));
                foreach (var h in hits) r.Line($"{h.Id,7}  {h.Title}  (level {h.Level}{(h.MinLevel > 0 ? $", from {h.MinLevel}" : "")}{(h.Races != 0 ? ", " + QuestForm.RaceText(h.Races) : "")}{(h.Classes != 0 ? ", " + QuestForm.ClassText(h.Classes, q.Classes()) : "")})");
                if (hits.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"no quest matches \"{string.Join(" ", inv.Args)}\".");
                return r.With(new { results = hits.Select(h => new { id = h.Id, title = h.Title, level = h.Level, minLevel = h.MinLevel, races = h.Races, classes = h.Classes }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests show",
            Summary = "a quest in plain fields: texts, levels, who may take it, chain links, objectives, rewards, givers and enders, and its checks",
            Args = new[] { "<quest>" },
            Examples = new[] { "wds quests show 1718", "wds quests show 7562 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                r.Line($"{q.Title(id)} ({id})");
                foreach (var g in QuestForm.Groups)
                {
                    var fs = QuestForm.Fields.Where(f => f.Group == g && q.Quests.Has(f.Column)).Select(f => (f, v: q.Show(id, f))).Where(x => x.v.Length > 0 && x.v != "0" && x.v != "any").ToList();
                    if (fs.Count == 0) continue;
                    r.Line($"== {g}");
                    foreach (var (f, v) in fs) r.Line($"  {f.Label}: {(v.Length > 200 ? v[..197] + "..." : v).Replace("\n", " / ")}");
                }
                r.Line("Givers: " + string.Join("; ", q.Givers(id).Select(x => x.Text)));
                r.Line("Enders: " + string.Join("; ", q.Enders(id).Select(x => x.Text)));
                foreach (var o in q.Objectives(id)) r.Line($"Objective: {o.Text}");
                var p = QuestRules.Check(q, new[] { id });
                r.Line(p.Count == 0 ? "Checks: no problems." : "Checks:");
                foreach (var x in p) r.Line("  " + x.Problem.Text);
                return r.With(QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests fields",
            Summary = "the quest editor's fields: id, label, group, the quest_template column and what it holds",
            Examples = new[] { "wds quests fields", "wds quests fields --json" },
            NeedsClient = false,
            Run = inv =>
            {
                var r = new CliResult();
                foreach (var g in QuestForm.Groups)
                {
                    r.Line($"== {g}");
                    foreach (var f in QuestForm.Fields.Where(f => f.Group == g)) r.Line($"  {f.Id,-24} {f.Label} ({f.Column}): {f.Help}");
                }
                return Task.FromResult(r.With(QuestForm.Fields.Select(f => new { id = f.Id, label = f.Label, group = f.Group, column = f.Column, kind = f.Kind.ToString(), help = f.Help })));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests chain",
            Summary = "the chain of a quest as a flowchart: lanes (faction, race / class), columns in order, links (must be done first, opens, next in chain, breadcrumb), exclusive groups, problems per quest",
            Args = new[] { "<quest>" },
            Options = new[] { new OptionSpec("mermaid", null, "print the chain as a Mermaid flowchart (for wikis and design documents)") },
            Examples = new[] { "wds quests chain 1718", "wds quests chain 7562 --json", "wds quests chain 1718 --mermaid" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var c = QuestChains.Open(q, id);
                r.Line($"Chain of {q.Label(id)}: {c.Nodes.Count} quest(s), {c.Lanes.Count} lane(s), {c.Columns} step(s).");
                r.Line(inv.Flag("mermaid") ? c.Mermaid() : c.Describe());
                if (c.Truncated) r.Warnings.Add($"The chain has more than {QuestChains.MaxQuests} quests: only the first are shown.");
                return r.With(ChainJson(c));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests check",
            Summary = "check a quest's chain (or only the quest): unreachable quests and cycles, level gaps, missing givers and enders, objectives that can't be completed, faction mismatches, the game's text limits",
            Args = new[] { "<quest>" },
            Options = new[] { new OptionSpec("only", null, "check only this quest, not its whole chain") },
            Examples = new[] { "wds quests check 1718", "wds quests check 900000 --only --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var ids = inv.Flag("only") ? new List<long> { id } : QuestChains.Members(q, id);
                var p = QuestRules.Check(q, ids);
                int errors = p.Count(x => x.Problem.IsError);
                r.Line($"{ids.Count} quest(s) checked: {errors} error(s), {p.Count(x => x.Problem.Severity == DesignSeverity.Warning)} warning(s), {p.Count(x => x.Problem.Severity == DesignSeverity.Note)} note(s).");
                foreach (var x in p) r.Line("  " + x.Text);
                r.With(new { quests = ids, problems = ProblemsJson(p) });
                if (errors > 0) { r.Code = ExitCode.ValidationFailed; r.Error = $"{errors} error(s): the chain cannot be played as it is."; }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "quests new",
            Summary = "a new quest (the first free ID of your custom range): alone, after or before another quest (linked in the chain), with its givers, enders and fields",
            Args = new[] { "<title>" },
            Options = new[]
            {
                new OptionSpec("after", "quest", "after this quest: it needs that one done, and the NPC that takes that one back offers it next"),
                new OptionSpec("before", "quest", "before this quest: that quest then needs this one first"),
                new OptionSpec("like", "quest", "copy levels, races, classes and log category from this quest"),
                new OptionSpec("id", "n", "this quest ID instead of the first free one"),
                new OptionSpec("giver", "who", "a quest giver: creature:<ID>, object:<ID> or a creature ID", true),
                new OptionSpec("ender", "who", "who takes it back", true),
                SetOpt, Common.DryRun,
            },
            Examples = new[] { "wds quests new \"Riposte Drill\" --giver 234 --ender 234 --set level=20 --set races=Alliance --set classes=Warrior", "wds quests new \"Report to the Sergeant\" --before 900001" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                var title = inv.Arg(0);
                long id;
                if (inv.Opt("after") is { } a) id = DesignerCli.Guard(() => q.AddAfter(ParseId(a, q), title));
                else if (inv.Opt("before") is { } b) id = DesignerCli.Guard(() => q.AddBefore(ParseId(b, q), title));
                else id = DesignerCli.Guard(() => q.New(title, inv.Opt("id") is { } i ? long.Parse(i, CultureInfo.InvariantCulture) : null, inv.Opt("like") is { } l ? ParseId(l, q) : null));
                r.Line($"New quest {q.Label(id)}.");
                foreach (var g in inv.Opts("giver")) Do(r, q.SetRelation(id, g, giver: true));
                foreach (var g in inv.Opts("ender")) Do(r, q.SetRelation(id, g, giver: false));
                ApplySets(inv, r, q, id);
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests set",
            Summary = "change fields of a quest: \"<field> <value>\" or --set field=value (texts, levels, classes, races, chain links, objectives, rewards...)",
            Args = new[] { "<quest>", "[field]", "[value]" },
            Options = new[] { SetOpt, Common.DryRun },
            Examples = new[] { "wds quests set 900000 level 22", "wds quests set 900000 --set races=Horde --set \"details=Well met, $N.\"", "wds quests set 900000 flags +Sharable" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                if (inv.ArgOrNull(1) is { } field)
                {
                    if (QuestForm.Find(field) == null) throw new CliError(ExitCode.Usage, $"there is no quest field \"{field}\" (\"wds quests fields\" lists them).");
                    Do(r, q.Set(id, field, inv.Arg(2)));
                }
                if (inv.ArgOrNull(1) == null && inv.Opts("set").Count == 0) throw new CliError(ExitCode.Usage, "give a field and a value, or --set field=value.");
                ApplySets(inv, r, q, id);
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests branch",
            Summary = "follow-ups of a quest: a choice (taking one closes the others: \"pick one of three = one of three follow-ups\") or parallel quests",
            Args = new[] { "<quest>" },
            Options = new[]
            {
                new OptionSpec("titles", "a|b|c", "the follow-ups' titles, separated by |"),
                new OptionSpec("parallel", null, "no choice: all of them can be done"),
                new OptionSpec("rewards", "items", "each follow-up's reward item, comma-separated, in order"),
                Common.DryRun,
            },
            Examples = new[] { "wds quests branch 900001 --titles \"The Sword Path|The Axe Path|The Mace Path\" --rewards 2488,2490,2491" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var titles = (inv.Opt("titles") ?? throw new CliError(ExitCode.Usage, "--titles is needed (a|b|c).")).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var rewards = inv.Opt("rewards") is { } rw ? DesignerCli.Ids(rw) : null;
                var ids = DesignerCli.Guard(() => q.Branch(id, titles, !inv.Flag("parallel"), rewards));
                r.Line($"{(inv.Flag("parallel") ? "Parallel follow-ups" : $"A choice (exclusive group {ids[0]})")} after {q.Label(id)}: {string.Join(", ", ids.Select(q.Label))}.");
                return Save(inv, r, c, q, () => new { quest = id, branches = ids, choice = !inv.Flag("parallel") });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests join",
            Summary = "a quest after several: --all (every one must be done: an \"all of\" group) or --any (any one of them, e.g. the branches of a choice)",
            Args = new[] { "<quests>" },
            Options = new[] { new OptionSpec("title", "text", "the new quest's title"), new OptionSpec("all", null, "all of them must be done"), new OptionSpec("any", null, "any one of them is enough"), Common.DryRun },
            Examples = new[] { "wds quests join 900002,900003,900004 --title \"Back to the Sergeant\" --any" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                var ids = DesignerCli.Ids(inv.Arg(0));
                foreach (var i in ids) if (!q.Exists(i)) throw new CliError(ExitCode.NotFound, $"There is no quest {i}.");
                if (inv.Flag("all") == inv.Flag("any")) throw new CliError(ExitCode.Usage, "give --all or --any.");
                var title = inv.Opt("title") ?? throw new CliError(ExitCode.Usage, "--title is needed.");
                long m = DesignerCli.Guard(() => q.Join(ids, title, inv.Flag("all")));
                r.Line($"New quest {q.Label(m)} after {(inv.Flag("all") ? "all" : "any one")} of {string.Join(", ", ids)}.");
                return Save(inv, r, c, q, () => QuestJson(q, m));
            },
        };
        foreach (var giver in new[] { true, false })
        {
            string what = giver ? "giver" : "ender";
            yield return new CommandSpec
            {
                Path = $"quests {what}",
                Summary = giver ? "add or remove a quest giver (a creature gets the Quest giver flag)" : "add or remove who takes the quest back (a creature or a game object)",
                Args = new[] { "<quest>", "<who>" },
                Options = new[] { new OptionSpec("remove", null, $"remove this {what}"), Common.DryRun },
                Examples = giver ? new[] { "wds quests giver 900000 234", "wds quests giver 900000 object:1618 --remove" } : new[] { "wds quests ender 900000 creature:234" },
                Run = async inv =>
                {
                    var r = new CliResult();
                    var (c, q) = await Open(inv, r);
                    long id = QuestArg(inv, q, 0);
                    Do(r, q.SetRelation(id, inv.Arg(1), giver, !inv.Flag("remove")));
                    return Save(inv, r, c, q, () => QuestJson(q, id));
                },
            };
        }
        yield return new CommandSpec
        {
            Path = "quests objective",
            Summary = "set an objective: kill a creature, use an object, cast a spell on it, collect an item (shows its drop sources); --clear empties a slot",
            Args = new[] { "<quest>" },
            Options = new[]
            {
                new OptionSpec("kill", "creature[:count]", "a creature to kill (count default 1)"),
                new OptionSpec("use", "object[:count]", "a game object to use"),
                new OptionSpec("cast", "spell", "with --kill / --use: cast this spell on it instead of killing it"),
                new OptionSpec("item", "item[:count]", "an item to collect"),
                new OptionSpec("slot", "n", "the objective slot 1-4 (default 1)"),
                new OptionSpec("text", "text", "the objective's line in the quest log (instead of \"<creature> slain\")"),
                new OptionSpec("clear", null, "empty the slot (with --kill or --item: which kind)"),
                Common.DryRun,
            },
            Examples = new[] { "wds quests objective 900001 --kill 122:8", "wds quests objective 900001 --item 2589:10 --slot 2", "wds quests objective 900001 --use 1618:1 --cast 8593" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                int slot = inv.IntOpt("slot") ?? 1;
                bool any = false;
                if (inv.Opt("kill") is { } k)
                {
                    var (t, n) = inv.Flag("clear") ? (0, 0) : IdCount(k, "kill");
                    Do(r, q.SetTarget(id, slot, t.ToString(CultureInfo.InvariantCulture), n, inv.Opt("cast") is { } cs ? long.Parse(cs, CultureInfo.InvariantCulture) : 0, inv.Opt("text")));
                    any = true;
                }
                if (inv.Opt("use") is { } u)
                {
                    var (t, n) = inv.Flag("clear") ? (0, 0) : IdCount(u, "use");
                    Do(r, q.SetTarget(id, slot, (-Math.Abs(t)).ToString(CultureInfo.InvariantCulture), n, inv.Opt("cast") is { } cs ? long.Parse(cs, CultureInfo.InvariantCulture) : 0, inv.Opt("text")));
                    any = true;
                }
                if (inv.Opt("item") is { } it)
                {
                    var (t, n) = inv.Flag("clear") ? (0, 0) : IdCount(it, "item");
                    Do(r, q.SetItem(id, slot, t, n));
                    if (t != 0) foreach (var s in q.DropSources(t).Take(8)) r.Line("  from: " + s.Text);
                    any = true;
                }
                if (!any) throw new CliError(ExitCode.Usage, "give --kill, --use or --item (\"wds quests talk\" and \"wds quests explore\" for the other kinds).");
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests talk",
            Summary = "a \"speak to\" objective: the NPC gets a gossip option, shown while the quest is in the log, that gives the talk credit (VMaNGOS TalkedToCreature)",
            Args = new[] { "<quest>", "<npc>" },
            Options = new[] { new OptionSpec("option", "text", "the option's text (default \"I am ready.\")"), new OptionSpec("greeting", "text", "the NPC's greeting when it has no gossip menu yet"), new OptionSpec("slot", "n", "the objective slot 1-4 (default 1)"), Common.DryRun },
            Examples = new[] { "wds quests talk 900001 821 --option \"Captain, the sergeant sent me.\"" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                long npc = DesignerCli.Id(inv, 1, "NPC");
                Do(r, q.AddTalkObjective(id, npc, inv.IntOpt("slot") ?? 1, inv.Opt("option"), inv.Opt("greeting")));
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests explore",
            Summary = "an \"explore\" objective: entering an area trigger completes the quest (an existing trigger, or a new one at a position: AreaTrigger.dbc and the server's areatrigger_template)",
            Args = new[] { "<quest>" },
            Options = new[]
            {
                new OptionSpec("trigger", "id", "an existing area trigger (AreaTrigger.dbc)"),
                new OptionSpec("map", "id", "a new trigger's map"), new OptionSpec("x", "x", "its position"), new OptionSpec("y", "y", "its position"), new OptionSpec("z", "z", "its height (default: the ground)"),
                new OptionSpec("radius", "yards", "its radius (default 10)"), Common.DryRun,
            },
            Examples = new[] { "wds quests explore 900001 --map 0 --x -10650 --y 1120 --radius 15", "wds quests explore 900001 --trigger 87" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                long trigger;
                if (inv.Opt("trigger") is { } t) trigger = long.Parse(t, CultureInfo.InvariantCulture);
                else
                {
                    var (map, x, y) = Position(inv);
                    double z = inv.Opt("z") is { } zs ? double.Parse(zs, CultureInfo.InvariantCulture) : q.GroundHeight(map, x, y) ?? throw new CliError(ExitCode.ValidationFailed, "the ground height is not known here: give --z.");
                    trigger = DesignerCli.Guard(() => q.NewAreaTrigger(map, x, y, z, inv.Opt("radius") is { } rs ? double.Parse(rs, CultureInfo.InvariantCulture) : 10));
                    r.Line($"New area trigger {trigger} on map {map} at {x:0.#}, {y:0.#}, {z:0.#}.");
                }
                Do(r, q.AddExplore(id, trigger));
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests reward",
            Summary = "set rewards: items, choices (pick one), money, experience, reputation, a spell the player learns (made through the spell designer when it has no learn spell)",
            Args = new[] { "<quest>" },
            Options = new[]
            {
                new OptionSpec("item", "item[:count]", "an item always given", true), new OptionSpec("choice", "item[:count]", "an item to choose from", true),
                new OptionSpec("money", "amount", "money (1g 50s; negative: money required)"), new OptionSpec("xp", "n", "experience"),
                new OptionSpec("rep", "faction:value", "reputation with a faction", true),
                new OptionSpec("spell", "spell", "a spell the player learns (its learn spell is cast; 0 removes it)"), new OptionSpec("cast-only", null, "with --spell: cast the spell on the player instead of teaching it"),
                new OptionSpec("clear", null, "remove the item, choice and reputation rewards first"), Common.DryRun,
            },
            Examples = new[] { "wds quests reward 900001 --spell 35010 --xp 1200 --money 1g", "wds quests reward 900001 --choice 2488 --choice 2490 --choice 2491" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                bool any = false;
                if (inv.Flag("clear"))
                {
                    foreach (var f in QuestForm.Fields.Where(f => f.Id.StartsWith("choice", StringComparison.Ordinal) || f.Id.StartsWith("reward-item", StringComparison.Ordinal) || f.Id.StartsWith("rep", StringComparison.Ordinal) && !f.Id.StartsWith("rep-", StringComparison.Ordinal)))
                        Do(r, q.Set(id, f.Id, "0"));
                    any = true;
                }
                void List(string opt, string field, int max)
                {
                    var list = inv.Opts(opt);
                    if (list.Count > max) throw new CliError(ExitCode.Usage, $"at most {max} --{opt}.");
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (it, n) = IdCount(list[i], opt);
                        Do(r, q.Set(id, $"{field}{i + 1}", it.ToString(CultureInfo.InvariantCulture)));
                        Do(r, q.Set(id, $"{field}{i + 1}-count", n.ToString(CultureInfo.InvariantCulture)));
                        any = true;
                    }
                }
                List("item", "reward-item", 4);
                List("choice", "choice", 6);
                var reps = inv.Opts("rep");
                if (reps.Count > 5) throw new CliError(ExitCode.Usage, "at most 5 --rep.");
                for (int i = 0; i < reps.Count; i++)
                {
                    var p = reps[i].Split(':');
                    if (p.Length != 2) throw new CliError(ExitCode.Usage, "--rep is faction:value (72:250).");
                    Do(r, q.Set(id, $"rep{i + 1}", p[0]));
                    Do(r, q.Set(id, $"rep{i + 1}-value", p[1]));
                    any = true;
                }
                if (inv.Opt("money") is { } m) { Do(r, q.Set(id, "money", m)); any = true; }
                if (inv.Opt("xp") is { } x) { Do(r, q.Set(id, "xp", x)); any = true; }
                if (inv.Opt("spell") is { } sp)
                {
                    if (!long.TryParse(sp, out var spell)) throw new CliError(ExitCode.Usage, "--spell is a spell ID.");
                    Do(r, q.SetRewardSpell(id, spell, !inv.Flag("cast-only")));
                    any = true;
                }
                if (!any) throw new CliError(ExitCode.Usage, "nothing to change: --item, --choice, --money, --xp, --rep, --spell or --clear.");
                r.Line("Titles: the 1.12 game and VMaNGOS have no quest title rewards (the hero titles are a client feature still to come).");
                return Save(inv, r, c, q, () => QuestJson(q, id));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests drops",
            Summary = "where an item comes from (creatures' loot, chests, containers, pickpocketing, skinning, fishing, vendors, quests, spells), and --creature adds a creature that drops it",
            Args = new[] { "<item>" },
            Options = new[]
            {
                new OptionSpec("creature", "id", "add this creature as a drop source (its loot table; made when it has none)"),
                new OptionSpec("chance", "percent", "the drop chance (default 35)"), new OptionSpec("always", null, "drop it for everyone, not only for players on the quest"),
                Common.DryRun,
            },
            Examples = new[] { "wds quests drops 2589", "wds quests drops 900100 --creature 122 --chance 40" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long item = DesignerCli.Id(inv, 0, "item");
                if (!q.Ed.LinkExists("item", item)) throw new CliError(ExitCode.NotFound, $"There is no item {item} on the server.");
                if (inv.Opt("creature") is { } cr)
                {
                    double chance = inv.Opt("chance") is { } ch ? double.Parse(ch, CultureInfo.InvariantCulture) : 35;
                    Do(r, q.AddDropSource(item, long.Parse(cr, CultureInfo.InvariantCulture), chance, !inv.Flag("always")));
                }
                var src = q.DropSources(item);
                r.Line($"{q.ItemName(item)} ({item}): {(src.Count == 0 ? "nothing gives it." : $"{src.Count} source(s)")}");
                foreach (var s in src) r.Line("  " + s.Text);
                object Data() => new { item, name = q.ItemName(item), sources = q.DropSources(item).Select(s => new { kind = s.Kind, id = s.Id, name = s.Name, detail = s.Detail, table = s.Table, lootEntry = s.LootEntry, spawns = s.Spawns }) };
                return inv.Opt("creature") != null ? Save(inv, r, c, q, Data) : r.With(Data());
            },
        };
        yield return new CommandSpec
        {
            Path = "quests npcs",
            Summary = "find quest givers by name or ID: creatures (quest givers first) and game objects, with how often each is placed in the world",
            Args = new[] { "<words...>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds quests npcs gryan", "wds quests npcs thork --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                var hits = q.FindNpcs(string.Join(" ", inv.Args), Common.LimitOf(inv, 40));
                if (hits.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"no creature or object matches \"{string.Join(" ", inv.Args)}\".");
                foreach (var h in hits) r.Line($"{h.Key,-16} {h.Name}  ({h.Spawns} spawn{(h.Spawns == 1 ? "" : "s")}{(h.Note.Length > 0 ? ", " + h.Note : "")})");
                return r.With(new { results = hits.Select(h => new { kind = h.Kind, id = h.Id, key = h.Key, name = h.Name, spawns = h.Spawns, note = h.Note }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests npc",
            Summary = "a new quest-giver NPC: a copy of an existing creature's look, faction and level with its own name (custom ID), the Quest giver flag, placed at a map position",
            Args = new[] { "<name>" },
            Options = new[]
            {
                new OptionSpec("copy-from", "creature", "the creature whose look, faction and level it gets"),
                new OptionSpec("subname", "text", "the line under its name (<Drill Sergeant>)"), new OptionSpec("level", "n", "its level"), new OptionSpec("faction", "template", "its faction template"),
                new OptionSpec("map", "id", "the map it stands on (0 Eastern Kingdoms, 1 Kalimdor)"), new OptionSpec("x", "x", "its position"), new OptionSpec("y", "y", "its position"),
                new OptionSpec("z", "z", "its height (default: the ground there)"), new OptionSpec("o", "radians", "the way it faces"), Common.DryRun,
            },
            Examples = new[] { "wds quests npc \"Drill Sergeant Halvor\" --copy-from 821 --subname \"Drill Sergeant\" --map 0 --x -10508 --y 1053" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long from = inv.Opt("copy-from") is { } cf ? long.Parse(cf, CultureInfo.InvariantCulture) : throw new CliError(ExitCode.Usage, "--copy-from is needed (the creature it looks like).");
                long? map = null; double? x = null, y = null, z = null;
                if (inv.Opt("map") != null) { var (m, xx, yy) = Position(inv); map = m; x = xx; y = yy; z = inv.Opt("z") is { } zs ? double.Parse(zs, CultureInfo.InvariantCulture) : null; }
                var (entry, guid, msg) = DesignerCli.Guard(() => q.NewNpc(inv.Arg(0), from, inv.Opt("subname"), inv.IntOpt("level"), inv.IntOpt("faction"), map, x, y, z, inv.Opt("o") is { } o ? double.Parse(o, CultureInfo.InvariantCulture) : 0));
                r.Line(msg);
                return Save(inv, r, c, q, () => new { entry, guid, name = inv.Arg(0), spawns = q.Spawns("creature", entry).Select(s => new { guid = s.Guid, map = s.Map, x = s.X, y = s.Y, z = s.Z }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests place",
            Summary = "place a creature at a map position (a new spawn; the height from the terrain when --z is not given)",
            Args = new[] { "<creature>" },
            Options = new[] { new OptionSpec("map", "id", "the map"), new OptionSpec("x", "x", "the position"), new OptionSpec("y", "y", "the position"), new OptionSpec("z", "z", "the height (default: the ground)"), new OptionSpec("o", "radians", "the way it faces"), Common.DryRun },
            Examples = new[] { "wds quests place 900020 --map 1 --x -470 --y -2620" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long entry = DesignerCli.Id(inv, 0, "creature");
                var (map, x, y) = Position(inv);
                var (guid, msg) = DesignerCli.Guard(() => q.PlaceNpc(entry, map, x, y, inv.Opt("z") is { } zs ? double.Parse(zs, CultureInfo.InvariantCulture) : null, inv.Opt("o") is { } o ? double.Parse(o, CultureInfo.InvariantCulture) : 0));
                r.Line(msg);
                return Save(inv, r, c, q, () => new { entry, guid });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests duplicate",
            Summary = "the same chain for the other faction: a copy of every quest of the chain (new IDs) for Alliance or Horde, its links between the copies, NPCs, objects, items, zones, factions and words of the texts swapped",
            Args = new[] { "<quest>" },
            Options = new[]
            {
                new OptionSpec("faction", "faction", "Alliance, Horde or races of the copy"),
                new OptionSpec("swap", "old=new", "234=3429 (creature), object:a=b, item:a=b, zone:40=17, faction:72=76, spell:a=b, \"text:Sentinel Hill=the Crossroads\"", true),
                new OptionSpec("only", "quests", "copy only these quests of the chain (comma-separated)"), new OptionSpec("suffix", "text", "add this to the copies' titles"), Common.DryRun,
            },
            Examples = new[] { "wds quests duplicate 900000 --faction Horde --swap 900020=900021 --swap 122=3258 --swap zone:40=17 --swap \"text:Sentinel Hill=the Crossroads\"" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var faction = inv.Opt("faction") ?? throw new CliError(ExitCode.Usage, "--faction is needed (Alliance or Horde).");
                var swaps = new QuestSwaps();
                foreach (var s in inv.Opts("swap")) DesignerCli.Guard(() => { swaps.Add(s); return 0; });
                var ids = inv.Opt("only") is { } only ? DesignerCli.Ids(only) : QuestChains.Members(q, id);
                var res = DesignerCli.Guard(() => q.DuplicateForFaction(ids, faction, swaps, inv.Opt("suffix")));
                r.Line(res.Message);
                foreach (var n in res.Notes) r.Warnings.Add(n);
                return Save(inv, r, c, q, () => new { copies = res.Map!.Select(kv => new { from = kv.Key, to = kv.Value }), notes = res.Notes });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests template",
            Summary = "the \"discipline chain\" template (the Warrior's Stance Disciplines): talk -> kill N -> return -> learn the discipline, for one faction",
            Args = new[] { "<discipline>" },
            Options = new[]
            {
                new OptionSpec("faction", "faction", "Alliance, Horde or any (default any)"), new OptionSpec("classes", "classes", "the classes (default Warrior)"), new OptionSpec("level", "n", "the level (default 20)"),
                new OptionSpec("giver", "creature", "the NPC who gives and takes back the drill (the drill sergeant, the champion)"),
                new OptionSpec("intro-from", "creature", "the NPC of the talk step who sends the player to the giver (none: no talk step)"),
                new OptionSpec("outpost", "name", "the outpost's name for the texts"),
                new OptionSpec("kill", "creature[:count]", "what to fight (default count 8)"),
                new OptionSpec("spell", "spell", "the discipline's spell (an existing one)"), new OptionSpec("clone-spell", "spell", "make the discipline's spell as a clone of this one"),
                new OptionSpec("spell-text", "text", "the made spell's description"), new OptionSpec("category", "category", "the quest log category (default sort:Warrior)"),
                new OptionSpec("xp", "n", "experience of the drill"), new OptionSpec("money", "amount", "money of the drill"), Common.DryRun,
            },
            Examples = new[] { "wds quests template \"Riposte Drill\" --faction Alliance --giver 900020 --intro-from 234 --outpost \"Sentinel Hill\" --kill 122:8 --clone-spell 12295 --xp 1450" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                var o = new DisciplineOptions
                {
                    Discipline = inv.Arg(0), Faction = inv.Opt("faction") ?? "any", Classes = inv.Opt("classes") ?? "Warrior", Level = inv.IntOpt("level") ?? 20,
                    Giver = inv.Opt("giver") ?? throw new CliError(ExitCode.Usage, "--giver is needed."), IntroGiver = inv.Opt("intro-from"), Outpost = inv.Opt("outpost") ?? "",
                    Category = inv.Opt("category") ?? "sort:Warrior", Xp = inv.IntOpt("xp") ?? 0, Money = inv.Opt("money") is { } m ? QuestForm.ParseMoney(m) : 0,
                    SpellDescription = inv.Opt("spell-text"),
                };
                var (kt, kc) = IdCount(inv.Opt("kill") ?? throw new CliError(ExitCode.Usage, "--kill is needed."), "kill", 8);
                o.KillTarget = kt.ToString(CultureInfo.InvariantCulture);
                o.KillCount = kc;
                if (inv.Opt("spell") is { } sp) o.RewardSpell = long.Parse(sp, CultureInfo.InvariantCulture);
                else if (inv.Opt("clone-spell") is { } cs) o.SpellTemplate = long.Parse(cs, CultureInfo.InvariantCulture);
                else throw new CliError(ExitCode.Usage, "give --spell or --clone-spell.");
                var res = DesignerCli.Guard(() => q.Discipline(o));
                r.Line(res.Message);
                foreach (var n in res.Notes) r.Warnings.Add(n);
                DesignerCli.AddProblems(r, QuestRules.Check(q, res.Quests).Select(p => p.Problem with { Message = $"{p.Title}: {p.Problem.Message}" }));
                return Save(inv, r, c, q, () => new { quests = res.Quests, spell = res.Spell });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests preview",
            Summary = "the quest's texts as the game shows them: $N, $C, $R, $G filled in for a player (the client's own rules)",
            Args = new[] { "<quest>" },
            Options = new[] { new OptionSpec("name", "text", "the player's name (default Thrall)"), new OptionSpec("race", "race", "the race (default Orc)"), new OptionSpec("class", "class", "the class (default Warrior)"), new OptionSpec("female", null, "a female character ($G)") },
            Examples = new[] { "wds quests preview 1718 --name Jaina --race Human --class Mage --female" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var p = new QuestTextPlayer(inv.Opt("name") ?? "Thrall", inv.Opt("race") ?? "Orc", inv.Opt("class") ?? "Warrior", inv.Flag("female"));
                var list = q.Preview(id, p).Where(x => x.Preview.Text.Length > 0).ToList();
                foreach (var (f, pv) in list)
                {
                    r.Line($"== {f.Label}");
                    r.Line(Common.Indent(pv.Text));
                    if (pv.UnknownTokens.Count > 0) r.Warnings.Add($"{f.Label}: the game does not know {string.Join(", ", pv.UnknownTokens)}.");
                }
                return r.With(list.Select(x => new { field = x.Field.Id, label = x.Field.Label, text = x.Preview.Text, unknownTokens = x.Preview.UnknownTokens }));
            },
        };
        yield return new CommandSpec
        {
            Path = "quests categories",
            Summary = "the quest log categories of the game (QuestSort.dbc: Warrior, Cooking, Seasonal...); --new adds one (a game table the server also reads)",
            Options = new[] { new OptionSpec("new", "name", "add a category"), Common.DryRun },
            Examples = new[] { "wds quests categories", "wds quests categories --new \"Stance Disciplines\"" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long made = 0;
                if (inv.Opt("new") is { } name)
                {
                    made = DesignerCli.Guard(() => q.NewCategory(name));
                    r.Line($"New category {made}: {name} (use it as \"sort:{name}\").");
                }
                var list = q.Categories();
                if (made == 0) foreach (var (id, n) in list) r.Line($"{id,5}  {n}");
                object Data() => q.Categories().Select(x => new { id = x.Id, name = x.Name, zoneOrSort = -x.Id });
                return made != 0 ? Save(inv, r, c, q, Data) : r.With(Data());
            },
        };
        yield return new CommandSpec
        {
            Path = "quests export",
            Summary = "a quest's chain (or quests) to a JSON or CSV file: every field, givers and enders (a file that \"wds quests import\" reads back exactly)",
            Args = new[] { "<quest>" },
            Options = new[] { new OptionSpec("out", "file", "the .json or .csv file"), new OptionSpec("only", "quests", "these quests instead of the chain (comma-separated)") },
            Examples = new[] { "wds quests export 1718 --out whirlwind.json", "wds quests export 900000 --only 900000,900001 --out drill.csv" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var file = inv.Opt("out") ?? throw new CliError(ExitCode.Usage, "--out is needed (.json or .csv).");
                var ids = inv.Opt("only") is { } only ? DesignerCli.Ids(only) : QuestChains.Members(q, id);
                QuestExchange.Export(q, ids, file);
                r.Line($"Wrote {ids.Count} quest(s) to {Path.GetFullPath(file)}.");
                return r.With(new { file = Path.GetFullPath(file), quests = ids });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests import",
            Summary = "quests from a JSON or CSV file (\"wds quests export\"): new quests are made with their IDs, existing ones get the file's values, givers and enders",
            Args = new[] { "<file>" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds quests import drill.json --dry-run", "wds quests import drill.csv" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                var file = inv.Arg(0);
                if (!File.Exists(file)) throw new CliError(ExitCode.NotFound, $"{file} does not exist.");
                var recs = DesignerCli.Guard(() => QuestExchange.Read(file));
                var msgs = DesignerCli.Guard(() => QuestExchange.Import(q, recs));
                foreach (var m in msgs) r.Line(m);
                if (msgs.Count == 0) r.Line("Nothing to change: the quests are already as in the file.");
                return Save(inv, r, c, q, () => new { quests = recs.Select(x => x.Id), changes = msgs });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests delete",
            Summary = "delete a quest (its givers, enders, triggers, translations); the quests around it are linked to each other (--keep-links: only cleared); needs --yes",
            Args = new[] { "<quest>" },
            Options = new[] { new OptionSpec("keep-links", null, "clear the other quests' links to it instead of linking them to each other"), Common.DryRun },
            Examples = new[] { "wds quests delete 900003 --yes" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                if (!inv.Yes && !inv.DryRun) return Common.NeedsYes(r, $"This deletes the quest {q.Label(id)} from your workspace's server edits (with its givers, enders and links).");
                Do(r, q.Delete(id, !inv.Flag("keep-links")));
                foreach (var n in q.Notes) r.Line("  " + n);
                return Save(inv, r, c, q, () => new { deleted = id });
            },
        };
        yield return new CommandSpec
        {
            Path = "quests test-in-game",
            Summary = "how to try a quest in the game: apply, reload, clear the quest cache, GM commands (.quest add, .go creature id <giver>, .quest complete)",
            Args = new[] { "<quest>" },
            Examples = new[] { "wds quests test-in-game 900001" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, q) = await Open(inv, r);
                long id = QuestArg(inv, q, 0);
                var lines = q.TestInGame(id);
                foreach (var l in lines) r.Line(l);
                return r.With(new { quest = id, steps = lines });
            },
        };
    }

    private static long ParseId(string text, QuestDesigner q)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) throw new CliError(ExitCode.Usage, $"\"{text}\" is not a quest ID.");
        if (!q.Exists(id)) throw new CliError(ExitCode.NotFound, $"There is no quest {id} on the server (quest_template).");
        return id;
    }

    private static (long Map, double X, double Y) Position(Invocation inv)
    {
        double D(string name) => inv.Opt(name) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new CliError(ExitCode.Usage, $"--{name} is needed (a number).");
        long map = inv.Opt("map") is { } m && long.TryParse(m, out var mm) ? mm : throw new CliError(ExitCode.Usage, "--map is needed (0 Eastern Kingdoms, 1 Kalimdor...).");
        return (map, D("x"), D("y"));
    }
}
