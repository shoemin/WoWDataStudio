using System.Globalization;
using WoWData.Core.Designers;

namespace WoWData.Cli.Commands;

/// <summary>talents: the talent designer of the Classes and talents tab (0.12.0): trees (TalentTab) and talents
/// (Talent) of a class, checked against the 1.12 talent frame and server; the rank spells are made with the spell
/// designer. Saved into the workspace at once; --dry-run shows and saves nothing.</summary>
internal static class TalentCommands
{
    private static async Task<(DesignerCli.Ctx C, TalentDesigner T, ClassDesigner K)> Open(Invocation inv, CliResult r)
    {
        var c = await DesignerCli.Open(inv, r);
        return (c, new TalentDesigner(c.D, c.Spells), new ClassDesigner(c.D, c.Spells));
    }

    private static object TreeJson(TalentDesigner t, long tab)
    {
        var tree = t.Tree(tab);
        return new
        {
            id = tree.Id, name = tree.Name, classMask = tree.ClassMask, raceMask = tree.RaceMask, order = tree.Order, background = tree.Background, icon = tree.IconId, iconName = tree.Icon,
            talents = tree.Talents.Select(n => new { id = n.Id, name = n.Name, tier = n.Tier, column = n.Column, ranks = n.Ranks, prereqs = n.Prereqs.Select(p => new { talent = p.Talent, rank = p.Rank + 1 }), icon = n.Icon, requiredSpell = n.RequiredSpell }),
            problems = DesignerCli.ProblemsJson(t.Check(tab)),
        };
    }

    private static (long Id, int Rank) Prereq(string text)
    {
        var parts = text.Split(':');
        if (!long.TryParse(parts[0], out var id)) throw new CliError(ExitCode.Usage, "--prereq is a talent ID, with :rank (\"1234:5\").");
        int rank = parts.Length > 1 && int.TryParse(parts[1], out var rk) ? rk : 0;
        return (id, rank);
    }

    private static void Do(CliResult r, DesignResult res)
    {
        if (!res.Ok) throw new CliError(res.Message.StartsWith("There is no", StringComparison.Ordinal) ? ExitCode.NotFound : ExitCode.ValidationFailed, res.Message);
        r.Line(res.Message);
    }

    private static CliResult Save(Invocation inv, CliResult r, DesignerCli.Ctx c, TalentDesigner t, long tab) =>
        DesignerCli.Finish(inv, r, () => t.Save(), () => c.D.Table("TalentTab").Find(tab) != null ? TreeJson(t, tab) : new { id = tab, removed = true }, c.D);

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "talents trees",
            Summary = "a class's talent trees (TalentTab rows), in their order, with their talent counts",
            Args = new[] { "<class>" },
            Examples = new[] { "wds talents trees warrior", "wds talents trees 9 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, k) = await Open(inv, r);
                long cls = DesignerCli.Guard(() => k.Resolve(inv.Arg(0)));
                var trees = t.Trees(cls);
                foreach (var tr in trees) r.Line($"{tr.Id,5}  {tr.Order}  {tr.Name,-20} {tr.Talents.Count} talents, background {tr.Background}");
                if (trees.Count == 0) r.Line($"{k.Info(cls).Name} has no talent tree.");
                return r.With(new { classId = cls, trees = trees.Select(x => new { id = x.Id, name = x.Name, order = x.Order, talents = x.Talents.Count, background = x.Background, icon = x.Icon }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "talents show",
            Summary = "a talent tree as a grid (tiers x 4 columns, ranks, prerequisites), its talents and the checks",
            Args = new[] { "<tree>" },
            Examples = new[] { "wds talents show 161", "wds talents show 161 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long tab = DesignerCli.Id(inv, 0, "tree");
                r.Line(DesignerCli.Guard(() => t.Grid(tab)));
                foreach (var n in t.Tree(tab).Talents) r.Line($"  {n.Id,5} tier {n.Tier} col {n.Column}: {n.Name} ({n.MaxRank} ranks: {string.Join(", ", n.Ranks)})" + (n.Prereqs.Count > 0 ? " needs " + string.Join(", ", n.Prereqs.Select(p => $"{p.Talent} rank {p.Rank + 1}")) : ""));
                var problems = t.Check(tab);
                r.Line(problems.Count == 0 ? "Checks: no problems." : "Checks:");
                foreach (var p in problems) r.Line("  " + p.Text);
                return r.With(TreeJson(t, tab));
            },
        };
        yield return new CommandSpec
        {
            Path = "talents add-tree",
            Summary = "a new talent tree for a class (at most 5 per class in the 1.12 talent frame)",
            Args = new[] { "<class>", "<name>" },
            Options = new[] { new OptionSpec("icon", "icon", "the tab's icon: a SpellIcon ID or an icon file name"), new OptionSpec("background", "name", "the background picture (Interface\\TalentFrame\\<name>-TopLeft.blp ...)"), new OptionSpec("order", "n", "the tab's place 0-4 (default: the next free)"), new OptionSpec("races", "races", "only these races (names or IDs; default every race)"), Common.DryRun },
            Examples = new[] { "wds talents add-tree warrior \"Stance Disciplines\" --icon Ability_Warrior_DefensiveStance --background WarriorProtection" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, k) = await Open(inv, r);
                long cls = DesignerCli.Guard(() => k.Resolve(inv.Arg(0)));
                long rm = inv.Opt("races") is { } rc ? SpellCommands.Masks(rc, "RaceMask", "Race") : 0;
                long tab = DesignerCli.Guard(() => t.AddTree(cls, inv.Arg(1), inv.Opt("icon"), inv.Opt("background"), inv.IntOpt("order"), rm));
                r.Line($"New tree {tab}: {inv.Arg(1)} for {k.Info(cls).Name}.");
                DesignerCli.AddProblems(r, t.Check(tab));
                return Save(inv, r, c, t, tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents set-tree",
            Summary = "change a tree: name, icon, background, order, class-mask, race-mask",
            Args = new[] { "<tree>" },
            Options = new[] { new OptionSpec("set", "field=value", "name=..., icon=..., background=..., order=0-4, class-mask=..., race-mask=...", true), Common.DryRun },
            Examples = new[] { "wds talents set-tree 410 --set name=\"Disciplines\" --set order=3" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long tab = DesignerCli.Id(inv, 0, "tree");
                DesignerCli.Guard(() => t.Tree(tab));
                if (inv.Opts("set").Count == 0) throw new CliError(ExitCode.Usage, "give --set field=value.");
                foreach (var kv in inv.Opts("set"))
                {
                    var (f, v) = DesignerCli.Pair(kv);
                    Do(r, t.SetTree(tab, f, v));
                }
                return Save(inv, r, c, t, tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents remove-tree",
            Summary = "remove a tree and its talents (their rank spells stay); needs --yes",
            Args = new[] { "<tree>" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds talents remove-tree 410 --yes" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long tab = DesignerCli.Id(inv, 0, "tree");
                var tree = DesignerCli.Guard(() => t.Tree(tab));
                if (!inv.Yes && !inv.DryRun) return Common.NeedsYes(r, $"This removes the tree {tree.Name} ({tab}) and its {tree.Talents.Count} talent(s) from your workspace's Talent.dbc and TalentTab.dbc.");
                Do(r, t.RemoveTree(tab));
                return Save(inv, r, c, t, tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents add",
            Summary = "a talent in a tree at a tier and column, with its rank spells (existing, or made from a template spell with --make-ranks)",
            Args = new[] { "<tree>" },
            Options = new[]
            {
                new OptionSpec("tier", "n", "the row 0-7 (row n needs 5 x n points in the tree)"), new OptionSpec("column", "n", "the column 0-3"),
                new OptionSpec("ranks", "spells", "the rank spells, comma-separated (1-5)"),
                new OptionSpec("make-ranks", "spell", "make the rank spells: clones of this template spell"),
                new OptionSpec("name", "text", "the name of the made rank spells"), new OptionSpec("count", "n", "how many ranks to make (default 5)"),
                new OptionSpec("values", "list", "effect values per rank: 1,2,3,4,5 or 2-4,4-6,..."), new OptionSpec("effect", "n", "the effect whose value --values sets (default 1)"),
                new OptionSpec("description", "text", "the description of the made rank spells ($s1 = the rank's value)"),
                new OptionSpec("prereq", "talent[:rank]", "a talent that must be learned first (and the rank it needs; default its last)"),
                Common.DryRun,
            },
            Examples = new[] { "wds talents add 410 --tier 0 --column 1 --make-ranks 12295 --name \"Iron Line\" --count 5 --values 1,2,3,4,5", "wds talents add 410 --tier 1 --column 1 --ranks 900010 --prereq 2000:5" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long tab = DesignerCli.Id(inv, 0, "tree");
                DesignerCli.Guard(() => t.Tree(tab));
                int tier = inv.IntOpt("tier") ?? throw new CliError(ExitCode.Usage, "--tier is needed.");
                int col = inv.IntOpt("column") ?? throw new CliError(ExitCode.Usage, "--column is needed.");
                List<long> ranks;
                if (inv.Opt("make-ranks") is { } tpl)
                {
                    if (!long.TryParse(tpl, out var tplId)) throw new CliError(ExitCode.Usage, "--make-ranks is a spell ID.");
                    int count = inv.IntOpt("count") ?? 5;
                    var values = inv.Opt("values")?.Split(',', StringSplitOptions.TrimEntries).ToList();
                    var name = inv.Opt("name") ?? throw new CliError(ExitCode.Usage, "--name is needed with --make-ranks.");
                    ranks = DesignerCli.Guard(() => t.MakeRankSpells(tplId, name, count, values, inv.IntOpt("effect") ?? 1, inv.Opt("description")));
                    r.Line($"Made {ranks.Count} rank spell(s): {string.Join(", ", ranks)}.");
                }
                else ranks = inv.Opt("ranks") is { } rs ? DesignerCli.Ids(rs) : throw new CliError(ExitCode.Usage, "give --ranks or --make-ranks.");
                long id = DesignerCli.Guard(() => t.AddTalent(tab, tier, col, ranks));
                r.Line($"New talent {id} at tier {tier}, column {col}.");
                if (inv.Opt("prereq") is { } pre)
                {
                    var (pid, prank) = Prereq(pre);
                    Do(r, t.SetPrereq(id, pid, prank));
                }
                DesignerCli.AddProblems(r, t.Check(tab));
                return Save(inv, r, c, t, tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents set",
            Summary = "change a talent: move it (--tier, --column), its rank spells, its prerequisites, its required spell",
            Args = new[] { "<talent>" },
            Options = new[]
            {
                new OptionSpec("tier", "n", "the row 0-7"), new OptionSpec("column", "n", "the column 0-3"), new OptionSpec("ranks", "spells", "the rank spells, comma-separated"),
                new OptionSpec("prereq", "talent[:rank]", "prerequisite 1 (0 removes it)"), new OptionSpec("prereq2", "talent[:rank]", "prerequisite 2"), new OptionSpec("prereq3", "talent[:rank]", "prerequisite 3"),
                new OptionSpec("required-spell", "spell", "a spell the player must know"), Common.DryRun,
            },
            Examples = new[] { "wds talents set 2000 --tier 1 --column 2", "wds talents set 2001 --prereq 2000:5" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long id = DesignerCli.Id(inv, 0, "talent");
                var n = DesignerCli.Guard(() => t.Talent(id));
                bool any = false;
                if (inv.IntOpt("tier") != null || inv.IntOpt("column") != null) { Do(r, t.Move(id, inv.IntOpt("tier") ?? n.Tier, inv.IntOpt("column") ?? n.Column)); any = true; }
                if (inv.Opt("ranks") is { } rs) { Do(r, t.SetRanks(id, DesignerCli.Ids(rs))); any = true; }
                for (int slot = 1; slot <= 3; slot++)
                    if (inv.Opt(slot == 1 ? "prereq" : $"prereq{slot}") is { } pre)
                    {
                        var (pid, prank) = Prereq(pre);
                        Do(r, t.SetPrereq(id, pid, prank, slot));
                        any = true;
                    }
                if (inv.IntOpt("required-spell") is { } rq) { Do(r, t.SetRequiredSpell(id, rq)); any = true; }
                if (!any) throw new CliError(ExitCode.Usage, "nothing to change: --tier, --column, --ranks, --prereq, --required-spell.");
                DesignerCli.AddProblems(r, t.Check(n.Tab));
                return Save(inv, r, c, t, n.Tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents remove",
            Summary = "remove a talent from its tree (its rank spells stay); needs --yes",
            Args = new[] { "<talent>" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds talents remove 2001 --yes" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, _) = await Open(inv, r);
                long id = DesignerCli.Id(inv, 0, "talent");
                var n = DesignerCli.Guard(() => t.Talent(id));
                if (!inv.Yes && !inv.DryRun) return Common.NeedsYes(r, $"This removes the talent {n.Name} ({id}) from your workspace's Talent.dbc.");
                Do(r, t.RemoveTalent(id));
                return Save(inv, r, c, t, n.Tab);
            },
        };
        yield return new CommandSpec
        {
            Path = "talents check",
            Summary = "check talent trees (a tree ID, or every tree of a class) against the 1.12 talent frame and server: grid, ranks, prerequisites, arrows, tiers",
            Args = new[] { "<tree-or-class>" },
            Examples = new[] { "wds talents check 161", "wds talents check warrior --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, t, k) = await Open(inv, r);
                var arg = inv.Arg(0);
                List<long> tabs = long.TryParse(arg, out var tab) && c.D.Table("TalentTab").Find(tab) != null ? new() { tab } : t.Trees(DesignerCli.Guard(() => k.Resolve(arg))).Select(x => x.Id).ToList();
                int errors = 0;
                var all = new List<object>();
                foreach (var id in tabs)
                {
                    var p = t.Check(id);
                    errors += p.Count(x => x.IsError);
                    r.Line($"{t.Tree(id).Name} ({id}): " + (p.Count == 0 ? "no problems." : $"{p.Count(x => x.IsError)} error(s), {p.Count(x => !x.IsError)} warning(s) or note(s)"));
                    foreach (var x in p) r.Line("  " + x.Text);
                    all.Add(new { tree = id, problems = DesignerCli.ProblemsJson(p) });
                }
                r.With(all);
                if (errors > 0) { r.Code = ExitCode.ValidationFailed; r.Error = $"{errors} error(s) the talent frame or the server cannot handle."; }
                return r;
            },
        };
    }
}
