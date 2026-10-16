using System.Globalization;
using WoWData.Core.Designers;

namespace WoWData.Cli.Commands;

/// <summary>classes: the class view of the Classes and talents tab (0.12.0): the classes, a class's spells by level,
/// skill lines and trees, assigning spells to levels and trainers, class masks, and (EXPERIMENTAL) a new class's data
/// with the report of what the game and the server still need in code.</summary>
internal static class ClassCommands
{
    private static async Task<(DesignerCli.Ctx C, ClassDesigner K)> Open(Invocation inv, CliResult r)
    {
        var c = await DesignerCli.Open(inv, r);
        return (c, new ClassDesigner(c.D, c.Spells));
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "classes list",
            Summary = "the classes (ChrClasses): ID, name, token, power, spell family and races",
            Examples = new[] { "wds classes list", "wds classes list --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (_, k) = await Open(inv, r);
                var list = k.Classes();
                foreach (var x in list) r.Line($"{x.Id,3}  {x.Name,-10} {x.Token,-9} {x.PowerText,-8} family {x.SpellFamily,-3} {string.Join(", ", x.Races)}");
                return r.With(list.Select(x => new { id = x.Id, name = x.Name, token = x.Token, power = x.PowerType, powerText = x.PowerText, spellFamily = x.SpellFamily, races = x.Races }));
            },
        };
        yield return new CommandSpec
        {
            Path = "classes show",
            Summary = "a class's full spell list by level (trainers, skill lines, automatic, starting spells, talents), its skill lines and its talent trees",
            Args = new[] { "<class>" },
            Options = new[] { new OptionSpec("level", "range", "only these levels: 20 or 10-30"), new OptionSpec("source", "kind", "only trainer, taught, automatic, starting or talent") },
            Examples = new[] { "wds classes show warlock", "wds classes show 1 --level 20-30 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, k) = await Open(inv, r);
                long cls = DesignerCli.Guard(() => k.Resolve(inv.Arg(0)));
                var info = k.Info(cls);
                var spells = k.SpellList(cls);
                if (inv.Opt("level") is { } lv)
                {
                    var p = lv.Split('-');
                    if (!long.TryParse(p[0], out var lo)) throw new CliError(ExitCode.Usage, "--level is n or n-m.");
                    long hi = p.Length > 1 && long.TryParse(p[1], out var h) ? h : lo;
                    spells = spells.Where(s => s.Level >= lo && s.Level <= hi).ToList();
                }
                if (inv.Opt("source") is { } src)
                {
                    if (!Enum.TryParse<SpellSource>(src, true, out var ss)) throw new CliError(ExitCode.Usage, "--source is trainer, taught, automatic, starting or talent.");
                    spells = spells.Where(s => s.Source == ss).ToList();
                }
                var skills = k.Skills(cls);
                var trees = new TalentDesigner(c.D, c.Spells).Trees(cls);
                r.Line($"{info.Name} ({info.Token}, class {info.Id}): {info.PowerText}, spell family {info.SpellFamily}; races: {string.Join(", ", info.Races)}");
                r.Line($"Talent trees: {(trees.Count == 0 ? "none" : string.Join(", ", trees.Select(t => $"{t.Name} ({t.Id}, {t.Talents.Count} talents)")))}");
                r.Line($"Skill lines: {string.Join(", ", skills.Select(s => s.Name))}");
                foreach (var l in Common.Table(new[] { "Level", "Spell", "Name", "Rank", "How", "From", "Skill line" },
                    spells.Select(s => (IReadOnlyList<string>)new[] { s.Level.ToString(CultureInfo.InvariantCulture), s.Spell.ToString(CultureInfo.InvariantCulture), s.Name, s.Rank, s.SourceText, s.From, s.SkillLine }), 40)) r.Line(l);
                if (!c.D.HasServer) r.Warnings.Add("Without the server database the trainers and starting spells are not known: the list shows the skill lines' spells at their spell level.");
                return r.With(new
                {
                    id = info.Id, name = info.Name, token = info.Token, power = info.PowerText, spellFamily = info.SpellFamily, races = info.Races,
                    trees = trees.Select(t => new { id = t.Id, name = t.Name, order = t.Order, talents = t.Talents.Count }),
                    skills = skills.Select(s => new { skillLine = s.SkillLine, name = s.Name, category = s.Category, minLevel = s.MinLevel }),
                    spells = spells.Select(s => new { level = s.Level, spell = s.Spell, name = s.Name, rank = s.Rank, source = s.Source, from = s.From, skillLine = s.SkillLine, learnSpell = s.LearnSpell, cost = s.Cost }),
                });
            },
        };
        yield return new CommandSpec
        {
            Path = "classes assign",
            Summary = "give a class's spell a level: its trainer rows of the class and its own level follow; --trainer all adds it to every class trainer that lacks it",
            Args = new[] { "<class>", "<spell>" },
            Options = new[] { new OptionSpec("level", "n", "the level (1-255)"), new OptionSpec("trainer", "who", "all (every trainer of the class), or creature entries / list:N, comma-separated"), new OptionSpec("cost", "price", "the price: copper or 25s / 1g 50s"), Common.DryRun },
            Examples = new[] { "wds classes assign warlock 900000 --level 20 --trainer all --cost 25s", "wds classes assign 9 686 --level 2" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, k) = await Open(inv, r);
                long cls = DesignerCli.Guard(() => k.Resolve(inv.Arg(0)));
                long spell = DesignerCli.Id(inv, 1);
                long level = inv.IntOpt("level") ?? throw new CliError(ExitCode.Usage, "--level is needed.");
                long? cost = inv.Opt("cost") is { } cs ? DesignerCli.Guard(() => SpellDesigner.ParseMoney(cs)) : null;
                var res = DesignerCli.Guard(() => k.Assign(cls, spell, level, inv.Opt("trainer"), cost));
                if (!res.Ok) return CliResult.Fail(res.Message.Contains("not connected", StringComparison.Ordinal) ? ExitCode.Error : ExitCode.ValidationFailed, res.Message);
                r.Line(res.Message);
                return DesignerCli.Finish(inv, r, () => k.Save(), () => new { classId = cls, spell, level, trainers = c.Spells.Trainers(spell).Select(t => new { table = t.Table, trainer = t.Trainer, name = t.TrainerName, level = t.Level, cost = t.Cost }) }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "classes mask",
            Summary = "add or remove a class in the class mask of rows: SkillLineAbility, SkillRaceClassInfo, TalentTab (game) or item_template, quest_template, skill_line_ability (server)",
            Args = new[] { "<table>", "<ids...>" },
            Options = new[] { new OptionSpec("class", "class", "the class (name or ID)"), new OptionSpec("add", null, "add the class's bit"), new OptionSpec("remove", null, "remove the class's bit"), new OptionSpec("column", "name", "the mask column when a table has two (SkillLineAbility: ClassMask or ExcludeClass)"), Common.DryRun },
            Examples = new[] { "wds classes mask SkillRaceClassInfo 75 76 --class 10 --add", "wds classes mask item_template 25 --class Warlock --remove" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, k) = await Open(inv, r);
                if (inv.Flag("add") == inv.Flag("remove")) throw new CliError(ExitCode.Usage, "give --add or --remove.");
                long cls = DesignerCli.Guard(() => k.Resolve(inv.Opt("class") ?? throw new CliError(ExitCode.Usage, "--class is needed.")));
                var res = DesignerCli.Guard(() => k.EditMask(inv.Arg(0), inv.Opt("column"), inv.Args.Skip(1).ToList(), cls, inv.Flag("add")));
                if (!res.Ok) return CliResult.Fail(res.Message.StartsWith("No class mask", StringComparison.Ordinal) ? ExitCode.Usage : res.Message.Contains("has no row", StringComparison.Ordinal) ? ExitCode.NotFound : ExitCode.ValidationFailed, res.Message);
                r.Line(res.Message);
                return DesignerCli.Finish(inv, r, () => k.Save(), () => new { table = inv.Arg(0), classId = cls, add = inv.Flag("add") }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "classes new",
            Summary = "EXPERIMENTAL: the data of a new class copied from a template class (ChrClasses, CharBaseInfo, shared skill lines, outfits; playercreateinfo*, level stats on the server) and the report of the code it still needs",
            Args = new[] { "<name>" },
            Options = new[]
            {
                new OptionSpec("token", "TOKEN", "the internal name in capitals (KNIGHT)"), new OptionSpec("copy-from", "class", "the template class (name or ID)"),
                new OptionSpec("races", "races", "the races that can be it (names or IDs, comma-separated)"), new OptionSpec("power", "type", "Mana, Rage, Focus, Energy or a number (default: the template's)"),
                new OptionSpec("id", "n", "the class ID (default: 6 or 10, then 12 and up)"), Common.DryRun,
            },
            Examples = new[] { "wds classes new Knight --token KNIGHT --copy-from Paladin --races Human --dry-run", "wds classes new Knight --token KNIGHT --copy-from Paladin --races Human" },
            Run = async inv =>
            {
                var r = new CliResult();
                var (c, k) = await Open(inv, r);
                var token = inv.Opt("token") ?? throw new CliError(ExitCode.Usage, "--token is needed.");
                long tpl = DesignerCli.Guard(() => k.Resolve(inv.Opt("copy-from") ?? throw new CliError(ExitCode.Usage, "--copy-from is needed.")));
                var raceMask = SpellCommands.Masks(inv.Opt("races") ?? throw new CliError(ExitCode.Usage, "--races is needed."), "RaceMask", "Race");
                var races = Enumerable.Range(1, 32).Where(i => (raceMask & (1L << (i - 1))) != 0).Select(i => (long)i).ToList();
                long? power = null;
                if (inv.Opt("power") is { } p)
                {
                    var pv = Core.Dbc.DbcLayouts.Default.NamedValues.GetValueOrDefault("PowerType")?.Values.FirstOrDefault(v => string.Equals(v.Name, p, StringComparison.OrdinalIgnoreCase));
                    power = long.TryParse(p, out var pn) ? pn : pv?.Name != null ? pv.Value.Value : throw new CliError(ExitCode.Usage, "--power is Mana, Rage, Focus, Energy or a number.");
                }
                r.Warnings.Add("EXPERIMENTAL: the tables alone do not make a playable class; see the report below.");
                var (id, done) = DesignerCli.Guard(() => k.NewClass(inv.Arg(0), token, tpl, races, power, inv.IntOpt("id")));
                r.Line($"New class {id}: {inv.Arg(0)} ({token.ToUpperInvariant()}), copied from {k.Info(tpl).Name}.");
                foreach (var d in done) r.Line("  " + d);
                var report = ClassDesigner.EngineReport(id);
                r.Line("Still needed outside the tables (code):");
                foreach (var e in report.Where(e => !e.DoneByDesigner)) r.Line($"  [{e.Side}] {e.Area}: {e.What} ({e.Where})");
                return DesignerCli.Finish(inv, r, () => k.Save($"Class designer (experimental): new class {inv.Arg(0)}"), () => new
                {
                    id, name = inv.Arg(0), token = token.ToUpperInvariant(), template = tpl, races, done,
                    engine = report.Select(e => new { side = e.Side, area = e.Area, what = e.What, where = e.Where, doneByDesigner = e.DoneByDesigner }),
                }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "classes engine-report",
            Summary = "what a new class still needs outside the tables: the 1.12 client (decompile and interface files) and the server (VMaNGOS source), with where",
            Args = new[] { "[class]" },
            NeedsClient = false,
            Examples = new[] { "wds classes engine-report", "wds classes engine-report 10 --json" },
            Run = inv =>
            {
                long? id = inv.ArgOrNull(0) is { } a ? long.TryParse(a, out var v) ? v : throw new CliError(ExitCode.Usage, "the class is an ID here.") : null;
                var report = ClassDesigner.EngineReport(id);
                var r = new CliResult().With(report.Select(e => new { side = e.Side, area = e.Area, what = e.What, where = e.Where, doneByDesigner = e.DoneByDesigner }));
                foreach (var g in report.GroupBy(e => e.Side))
                {
                    r.Line(g.Key switch { "data" => "== Data (the designers make it)", "server" => "== Server code (VMaNGOS source)", _ => "== Game client code and interface" });
                    foreach (var e in g) r.Line($"  {(e.DoneByDesigner ? "[data]" : "[code]")} {e.Area}: {e.What}\n         where: {e.Where}");
                }
                return Task.FromResult(r);
            },
        };
    }
}
