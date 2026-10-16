using System.Globalization;
using WoWData.Core.Designers;

namespace WoWData.Cli.Commands;

/// <summary>spells: the Spell designer tab (0.12.0). New and cloned spells, the form's fields with friendly units, the
/// tooltip preview, the checks against what the 1.12 client and server handle, visuals, trainers and skill lines. Every
/// change is saved into the workspace at once (Spell.dbc and its helper tables, and the server's spell_template etc. when
/// the server database is connected), in one change set; --dry-run shows it and saves nothing.</summary>
internal static class SpellCommands
{
    private static object FormJson(SpellDesigner sp, long id, int? level) => new
    {
        id,
        title = sp.Title(id),
        groups = SpellForm.Groups.Select(g => new
        {
            group = g,
            fields = SpellForm.Fields.Where(f => f.Group == g).Select(f => new { id = f.Id, label = f.Label, value = SpellForm.Show(sp.Session, sp.Row(id), f, sp.ServerExtras(id)) }),
        }),
        tooltip = sp.Tooltip(id, level).Text,
        auraTooltip = sp.Tooltip(id, level, aura: true).Text,
        effects = Enumerable.Range(1, 3).Select(e =>
        {
            long eff = sp.Spells.Number(sp.Row(id), sp.Spells.Layout.Column($"Effect{e}")!);
            long aura = sp.Spells.Number(sp.Row(id), sp.Spells.Layout.Column($"EffectAura{e}")!);
            var ei = SpellRules.Effect(eff);
            var ai = SpellRules.Aura(aura);
            return new { effect = e, type = ei.Text, explanation = ei.Explanation, misc = ei.MiscValue, server = ei.ServerText, aura = aura == 0 ? null : ai.Text, auraExplanation = aura == 0 ? null : ai.Explanation, auraMisc = aura == 0 ? null : ai.MiscValue };
        }),
        problems = DesignerCli.ProblemsJson(sp.Check(id)),
        trainers = sp.Trainers(id).Select(t => new { table = t.Table, key = t.Key, trainer = t.Trainer, name = t.TrainerName, learnSpell = t.LearnSpell, level = t.Level, cost = t.Cost }),
        skillLines = sp.SkillLines(id).Select(s => new { id = s.Id, skillLine = s.SkillLine, name = s.SkillName, classMask = s.ClassMask, raceMask = s.RaceMask, acquire = s.AcquireMethod }),
        learnSpells = sp.LearnSpellsOf(id),
    };

    private static void FormText(CliResult r, SpellDesigner sp, long id, string? group, int? level)
    {
        r.Line(sp.Title(id));
        foreach (var g in SpellForm.Groups.Where(g => group == null || g.StartsWith(group, StringComparison.OrdinalIgnoreCase)))
        {
            var fields = SpellForm.Fields.Where(f => f.Group == g).Select(f => (f, v: SpellForm.Show(sp.Session, sp.Row(id), f, sp.ServerExtras(id)))).ToList();
            if (g.StartsWith("Effect", StringComparison.Ordinal) && sp.Spells.Number(sp.Row(id), sp.Spells.Layout.Column($"Effect{g[7]}")!) == 0 && group == null)
            {
                r.Line($"== {g}: none");
                continue;
            }
            r.Line($"== {g}");
            foreach (var (f, v) in fields)
            {
                var col = sp.Spells.Layout.Column(f.Column);
                bool empty = f.Kind == SpellFieldKind.ServerOnly ? v == "(the server's value)"
                    : col == null ? false : col.IsString ? v.Length == 0 : sp.Spells.Number(sp.Row(id), col) is 0 or -1 && f.Kind != SpellFieldKind.EffectValue;
                if (group != null || !empty || f.Id is "name" or "cast-time" or "range" or "cost") r.Line($"  {f.Label,-30} {v}");
            }
        }
        r.Line("== Tooltip: " + sp.Tooltip(id, level).Text);
        if (sp.Spells.Text(sp.Row(id), sp.Spells.Layout.Column("AuraDescription")!, 0).Length > 0) r.Line("== Buff text: " + sp.Tooltip(id, level, aura: true).Text);
    }

    public static IEnumerable<CommandSpec> All()
    {
        yield return new CommandSpec
        {
            Path = "spells find",
            Summary = "find spells by name or ID (to open or clone one): ID, name, rank",
            Args = new[] { "<words...>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds spells find shadow bolt", "wds spells find 686 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv);
                var hits = c.Spells.Search(string.Join(" ", inv.Args), Common.LimitOf(inv));
                r.With(new { results = hits.Select(h => new { id = h.Id, name = h.Name, rank = h.Rank }) });
                foreach (var l in Common.Table(new[] { "ID", "Name", "Rank" }, hits.Select(h => (IReadOnlyList<string>)new[] { h.Id.ToString(CultureInfo.InvariantCulture), h.Name, h.Rank }))) r.Line(l);
                if (hits.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"no spell matches \"{string.Join(" ", inv.Args)}\".");
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "spells show",
            Summary = "a spell in the designer's form: every group of fields, the tooltip as the game fills it in, the effects explained, the checks, trainers and skill lines",
            Args = new[] { "<spell>" },
            Options = new[] { new OptionSpec("group", "name", "only one group (Basics, Casting, Targeting, Effect 1...), with every field"), new OptionSpec("level", "n", "the caster level of the tooltip preview (default: the spell's level)") },
            Examples = new[] { "wds spells show 686", "wds spells show 686 --group Effect --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long id = DesignerCli.Id(inv, 0);
                DesignerCli.Guard(() => c.Spells.Row(id));
                int? level = inv.IntOpt("level");
                FormText(r, c.Spells, id, inv.Opt("group"), level);
                foreach (var e in Enumerable.Range(1, 3))
                {
                    long eff = c.Spells.Spells.Number(c.Spells.Row(id), c.Spells.Spells.Layout.Column($"Effect{e}")!);
                    if (eff == 0) continue;
                    var ei = SpellRules.Effect(eff);
                    r.Line($"== Effect {e} explained: {ei.Text}: {ei.Explanation}" + (ei.MiscValue.Length > 0 ? $" Misc value: {ei.MiscValue}." : "") + " " + ei.ServerText);
                }
                var trainers = c.Spells.Trainers(id);
                var skills = c.Spells.SkillLines(id);
                r.Line("== Learning: " + (trainers.Count == 0 ? "no trainer" : string.Join("; ", trainers.Select(t => $"{t.TrainerName} level {t.Level} for {SpellDesigner.Money(t.Cost)}"))) +
                       " | " + (skills.Count == 0 ? "no skill line" : string.Join("; ", skills.Select(s => $"{s.SkillName} ({SpellDesigner.ClassNames(s.ClassMask)})"))));
                var problems = c.Spells.Check(id);
                r.Line(problems.Count == 0 ? "== Checks: no problems." : $"== Checks: {problems.Count(p => p.IsError)} error(s), {problems.Count(p => !p.IsError)} warning(s) or note(s)");
                foreach (var p in problems) r.Line("  " + p.Text);
                return r.With(FormJson(c.Spells, id, level));
            },
        };
        yield return new CommandSpec
        {
            Path = "spells fields",
            Summary = "the fields of the spell designer's form: their group, label, the column they edit, the kind of value and help",
            Options = new[] { new OptionSpec("group", "name", "only one group") },
            NeedsClient = false,
            Examples = new[] { "wds spells fields", "wds spells fields --group Casting --json" },
            Run = inv =>
            {
                var list = SpellForm.Fields.Where(f => inv.Opt("group") is not { } g || f.Group.StartsWith(g, StringComparison.OrdinalIgnoreCase)).ToList();
                var r = new CliResult().With(list.Select(f => new { id = f.Id, group = f.Group, label = f.Label, column = f.Column, kind = f.Kind, help = f.Help }));
                foreach (var g in list.GroupBy(f => f.Group))
                {
                    r.Line($"== {g.Key}");
                    foreach (var f in g) r.Line($"  {f.Id,-26} {f.Label}: {f.Help}");
                }
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells new",
            Summary = "a new spell (instant, self, mana, on the global cooldown) with a free ID of your custom range, then --set fields; saved into the workspace (and the server's spell_template)",
            Args = new[] { "<name>" },
            Options = new[] { new OptionSpec("rank", "text", "the rank text (\"Rank 1\")"), new OptionSpec("id", "n", "this ID instead of the first free one of your custom range"), DesignerCli.SetOpt, Common.DryRun },
            Examples = new[] { "wds spells new \"Shadow Lance\" --rank \"Rank 1\" --set school=Shadow --set cast-time=2.5 --set range=30", "wds spells new Test --dry-run --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long id = DesignerCli.Guard(() => c.Spells.New(inv.Arg(0), inv.IntOpt("id"), inv.Opt("rank") ?? ""));
                r.Line($"New spell {id}: {inv.Arg(0)}.");
                foreach (var m in DesignerCli.ApplySets(inv, c.Spells, id)) r.Line("  " + m);
                DesignerCli.AddProblems(r, c.Spells.Check(id));
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), () => new { id, title = c.Spells.Title(id), tooltip = c.Spells.Tooltip(id).Text, problems = DesignerCli.ProblemsJson(c.Spells.Check(id)) }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells clone",
            Summary = "a copy of an existing spell (\"a spell like Shadow Bolt\") with a free ID of your custom range, a new name and rank, then --set fields; saved like spells new",
            Args = new[] { "<source>" },
            Options = new[] { new OptionSpec("name", "text", "the new spell's name"), new OptionSpec("rank", "text", "its rank text"), new OptionSpec("id", "n", "this ID instead of the first free one"), DesignerCli.SetOpt, Common.DryRun },
            Examples = new[] { "wds spells clone 686 --name \"Shadow Lance\" --set cast-time=2 --set effect1-value=40-48", "wds spells clone 133 --name \"Blue Fireball\" --dry-run" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long src = DesignerCli.Id(inv, 0);
                long id = DesignerCli.Guard(() => c.Spells.Clone(src, inv.Opt("name"), inv.IntOpt("id"), inv.Opt("rank")));
                r.Line($"Cloned {c.Spells.Title(src)} as {c.Spells.Title(id)}.");
                foreach (var m in DesignerCli.ApplySets(inv, c.Spells, id)) r.Line("  " + m);
                DesignerCli.AddProblems(r, c.Spells.Check(id));
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), () => new { id, source = src, title = c.Spells.Title(id), tooltip = c.Spells.Tooltip(id).Text, problems = DesignerCli.ProblemsJson(c.Spells.Check(id)) }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells set",
            Summary = "change fields of a spell (friendly units, checked) and save it with the server's spell_template in sync",
            Args = new[] { "<spell>", "[field]", "[value]" },
            Options = new[] { DesignerCli.SetOpt, Common.DryRun },
            Details = "Fields are listed by \"wds spells fields\": cast-time 2.5 (seconds; \"instant\"), cooldown 8, range 30 (yards; \"self\", \"melee\", \"8-30\"), duration 18 (\"until cancelled\"), icon Spell_Shadow_ShadowBolt (a file name or a SpellIcon ID), effect1-value 10-20, effect1 \"School damage\", school Shadow, attributes \"+Passive\" (add a flag) ...",
            Examples = new[] { "wds spells set 900000 cast-time 2", "wds spells set 900000 --set effect1-value=40-48 --set icon=Spell_Shadow_ShadowBolt" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long id = DesignerCli.Id(inv, 0);
                DesignerCli.Guard(() => c.Spells.Row(id));
                if (inv.ArgOrNull(1) is { } field)
                {
                    var value = inv.ArgOrNull(2) ?? throw new CliError(ExitCode.Usage, "give the value after the field (or use --set field=value).");
                    if (SpellForm.Find(field) == null) throw new CliError(ExitCode.Usage, $"there is no spell field \"{field}\" (\"wds spells fields\" lists them).");
                    var res = c.Spells.Set(id, field, value);
                    if (!res.Ok) return CliResult.Fail(SpellForm.Find(field) == null ? ExitCode.Usage : ExitCode.ValidationFailed, res.Message);
                    r.Line(res.Message);
                }
                foreach (var m in DesignerCli.ApplySets(inv, c.Spells, id)) r.Line(m);
                if (inv.ArgOrNull(1) == null && inv.Opts("set").Count == 0) throw new CliError(ExitCode.Usage, "give a field and a value, or --set field=value.");
                DesignerCli.AddProblems(r, c.Spells.Check(id));
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), () => new { id, title = c.Spells.Title(id), tooltip = c.Spells.Tooltip(id).Text }, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells check",
            Summary = "what the 1.12 client or server cannot handle in a spell (errors), and what is probably a mistake (warnings), in plain words",
            Args = new[] { "<spells...>" },
            Examples = new[] { "wds spells check 900000", "wds spells check 686 133 --json" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                var all = new List<object>();
                int errors = 0;
                for (int i = 0; i < inv.Args.Count; i++)
                {
                    long id = DesignerCli.Id(inv, i);
                    DesignerCli.Guard(() => c.Spells.Row(id));
                    var p = c.Spells.Check(id);
                    errors += p.Count(x => x.IsError);
                    r.Line($"{c.Spells.Title(id)}: " + (p.Count == 0 ? "no problems." : $"{p.Count(x => x.IsError)} error(s), {p.Count(x => !x.IsError)} warning(s) or note(s)"));
                    foreach (var x in p) r.Line("  " + x.Text);
                    all.Add(new { id, title = c.Spells.Title(id), problems = DesignerCli.ProblemsJson(p) });
                }
                r.With(all);
                if (errors > 0) { r.Code = ExitCode.ValidationFailed; r.Error = $"{errors} error(s): the game or the server cannot handle these spells as they are."; }
                return r;
            },
        };
        yield return new CommandSpec
        {
            Path = "spells tooltip",
            Summary = "the spell book tooltip and the buff text as the game fills in $s1, $d, $o1... (or any text with --text)",
            Args = new[] { "<spell>" },
            Options = new[] { new OptionSpec("level", "n", "the caster level (default: the spell's level)"), new OptionSpec("text", "text", "expand this text with the spell's values instead of its description") },
            Examples = new[] { "wds spells tooltip 686", "wds spells tooltip 172 --level 20", "wds spells tooltip 686 --text \"Deals $s1 damage over $d.\"" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv);
                long id = DesignerCli.Id(inv, 0);
                var row = DesignerCli.Guard(() => c.Spells.Row(id));
                int level = inv.IntOpt("level") ?? SpellText.DefaultLevel(c.Spells.Spells, row);
                SpellTextResult desc = inv.Opt("text") is { } t ? SpellText.Expand(t, c.Spells.Spells, row, c.D.Lookups, level) : c.Spells.Tooltip(id, level);
                bool hasAura = c.Spells.Spells.Text(row, c.Spells.Spells.Layout.Column("AuraDescription")!, 0).Length > 0;
                var aura = inv.Opt("text") == null && hasAura ? c.Spells.Tooltip(id, level, aura: true) : new SpellTextResult("", Array.Empty<SpellTextProblem>());
                r.Line(desc.Text);
                if (aura.Text.Length > 0) r.Line("Buff: " + aura.Text);
                foreach (var p in desc.Problems.Concat(aura.Problems)) r.Warnings.Add($"{p.Token}: {p.Message}");
                return r.With(new { id, level, text = desc.Text, auraText = aura.Text, problems = desc.Problems.Concat(aura.Problems).Select(p => new { token = p.Token, message = p.Message }) });
            },
        };
        yield return new CommandSpec
        {
            Path = "spells explain",
            Summary = "the spell effect and aura types in plain words: what each does, what its misc value holds, and whether the 1.12 server handles it",
            Args = new[] { "[kind]", "[id]" },
            NeedsClient = false,
            Details = "kind: effect or aura (default both).",
            Examples = new[] { "wds spells explain effect 6", "wds spells explain aura", "wds spells explain --json" },
            Run = inv =>
            {
                var kind = inv.ArgOrNull(0)?.ToLowerInvariant();
                if (kind is not (null or "effect" or "effects" or "aura" or "auras")) throw new CliError(ExitCode.Usage, "the kind is effect or aura.");
                long? id = inv.ArgOrNull(1) is { } s ? long.TryParse(s, out var v) ? v : throw new CliError(ExitCode.Usage, "the id is a number.") : null;
                var list = new List<(string Kind, SpellTypeInfo Info)>();
                if (kind is null or "effect" or "effects") list.AddRange((id is { } i ? new[] { SpellRules.Effect(i) } : SpellRules.AllEffects()).Select(x => ("effect", x)));
                if (kind is null or "aura" or "auras") list.AddRange((id is { } i2 ? new[] { SpellRules.Aura(i2) } : SpellRules.AllAuras()).Select(x => ("aura", x)));
                var r = new CliResult().With(list.Select(x => new { kind = x.Kind, id = x.Info.Id, name = x.Info.Name, explanation = x.Info.Explanation, misc = x.Info.MiscValue, server = x.Info.Server, serverText = x.Info.ServerText }));
                foreach (var (k, x) in list) r.Line($"{k} {x.Text}: {x.Explanation}" + (x.MiscValue.Length > 0 ? $" Misc value: {x.MiscValue}." : "") + (x.Server != ServerSupport.Works ? " " + x.ServerText : ""));
                return Task.FromResult(r);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells icons",
            Summary = "the icon picker: SpellIcon rows and icon files (Interface\\Icons) whose name has these words",
            Args = new[] { "<words...>" },
            Options = new[] { Common.Limit },
            Examples = new[] { "wds spells icons shadow bolt", "wds spells icons frost --json" },
            Run = async inv =>
            {
                var c = await DesignerCli.Open(inv);
                var w = inv.Args.Select(a => a.ToLowerInvariant()).ToList();
                var it = c.D.Table("SpellIcon");
                var col = it.Layout.Column("TextureFilename")!;
                var rows = it.Rows.Select(r => (Id: it.Key(r)!.Value, Path: it.Text(r, col, 0))).Where(x => w.All(x.Path.ToLowerInvariant().Contains)).ToList();
                var known = rows.Select(x => x.Path.ToUpperInvariant()).ToHashSet();
                var files = c.S.Data!.Glob("Interface\\Icons\\*.blp").Where(p => w.All(p.ToLowerInvariant().Contains)).Select(p => p[..^4]).Where(p => !known.Contains(p.ToUpperInvariant())).ToList();
                int limit = Common.LimitOf(inv);
                var r2 = new CliResult().With(new { rows = rows.Take(limit).Select(x => new { id = x.Id, path = x.Path }), files = files.Take(limit) });
                foreach (var x in rows.Take(limit)) r2.Line($"{x.Id,6}  {x.Path}");
                foreach (var f in files.Take(limit)) r2.Line($"   new  {f} (no SpellIcon row yet: \"icon {SpellForm.IconName(f)}\" adds one)");
                if (rows.Count + files.Count == 0) return CliResult.Fail(ExitCode.NotFound, $"no icon matches \"{string.Join(" ", inv.Args)}\".");
                return r2;
            },
        };
        yield return new CommandSpec
        {
            Path = "spells visual",
            Summary = "a spell's visual: describe its parts, use another spell's visual, compose it from parts of others (precast, cast, missile, impact, aura, channel, area), or duplicate it tinted and scaled",
            Args = new[] { "<spell>" },
            Options = new[]
            {
                new OptionSpec("use", "visual", "use this SpellVisual (or spell:N for another spell's visual)"),
                new OptionSpec("precast", "part", "visual:N, spell:N, kit:N or none"), new OptionSpec("cast", "part", "visual:N, spell:N, kit:N or none"),
                new OptionSpec("missile", "part", "visual:N, spell:N, model:N or none"), new OptionSpec("impact", "part", "visual:N, spell:N, kit:N or none"),
                new OptionSpec("aura", "part", "visual:N, spell:N, kit:N or none"), new OptionSpec("channel", "part", "visual:N, spell:N, kit:N or none"),
                new OptionSpec("area", "part", "visual:N, spell:N, kit:N, model:N or none"),
                new OptionSpec("duplicate", null, "copy the visual with its own kits and effect models (tint and size below)"),
                new OptionSpec("tint", "color", "#RRGGBB multiplied into the copied models' colours and textures"),
                new OptionSpec("scale", "x", "size of the copied models (1 = unchanged)"),
                new OptionSpec("find", "words", "list the visuals of spells with these words in their name (the picker)"),
                Common.DryRun,
            },
            Examples = new[] { "wds spells visual 686", "wds spells visual 900000 --use spell:686", "wds spells visual 900000 --missile spell:133 --impact spell:686", "wds spells visual 900000 --duplicate --tint #8040FF --scale 1.5", "wds spells visual 900000 --find frostbolt" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long id = DesignerCli.Id(inv, 0);
                var row = DesignerCli.Guard(() => c.Spells.Row(id));
                if (inv.Opt("find") is { } words)
                {
                    var found = SpellVisuals.Find(c.D, words, Common.LimitOf(inv));
                    foreach (var (v, sp) in found) r.Line($"{v,6}  {sp}");
                    return r.With(found.Select(x => new { visual = x.Visual, spells = x.Spells }));
                }
                bool changed = false;
                if (inv.Opt("use") is { } use)
                {
                    long v = use.StartsWith("spell:", StringComparison.OrdinalIgnoreCase) ? c.Spells.Spells.Number(DesignerCli.Guard(() => c.Spells.Row(long.Parse(use[6..], CultureInfo.InvariantCulture))), c.Spells.Spells.Layout.Column("SpellVisual")!)
                        : long.TryParse(use, out var n) ? n : throw new CliError(ExitCode.Usage, "--use is a SpellVisual ID or spell:N.");
                    var res = c.Spells.Set(id, "visual", v.ToString(CultureInfo.InvariantCulture));
                    if (!res.Ok) return CliResult.Fail(ExitCode.ValidationFailed, res.Message);
                    r.Line(res.Message);
                    changed = true;
                }
                var parts = new Dictionary<string, string>();
                foreach (var slot in new[] { "precast", "cast", "missile", "impact", "channel", "area" })
                    if (inv.Opt(slot) is { } p) parts[slot] = p;
                if (inv.Opt("aura") is { } a) parts["state"] = a;
                if (parts.Count > 0)
                {
                    r.Line(DesignerCli.Guard(() => SpellVisuals.Compose(c.D, c.Spells, id, parts)));
                    changed = true;
                }
                if (inv.Flag("duplicate"))
                {
                    float scale = inv.Opt("scale") is { } sc ? float.TryParse(sc, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw new CliError(ExitCode.Usage, "--scale is a number (1.5).") : 1f;
                    System.Numerics.Vector3? tint = inv.Opt("tint") is { } t ? DesignerCli.Guard(() => SpellVisuals.ParseTint(t)) : null;
                    r.Line(DesignerCli.Guard(() => SpellVisuals.Duplicate(c.D, c.Spells, id, scale, tint, $"V{id}")));
                    changed = true;
                }
                else if (inv.Opt("tint") != null || inv.Opt("scale") != null) throw new CliError(ExitCode.Usage, "--tint and --scale go with --duplicate.");
                long visual = c.Spells.Spells.Number(row, c.Spells.Spells.Layout.Column("SpellVisual")!);
                object Info()
                {
                    if (visual <= 0) return new { visual };
                    var vi = SpellVisuals.Describe(c.D, visual);
                    return new { visual, parts = vi.Slots.Select(s => new { slot = s.Slot.Id, label = s.Slot.Label, value = s.Value, text = s.Text }), models = vi.Models.Select(m => new { slot = m.Slot, where = m.Where, effectName = m.EffectNameId, name = m.Name, file = m.File }), usedBy = vi.UsedBy };
                }
                visual = c.Spells.Spells.Number(row, c.Spells.Spells.Layout.Column("SpellVisual")!);
                if (visual > 0)
                {
                    var vi = SpellVisuals.Describe(c.D, visual);
                    r.Line($"SpellVisual {visual}:");
                    foreach (var s in vi.Slots) r.Line($"  {s.Slot.Label,-12} {s.Text}");
                    if (vi.UsedBy.Count > 0) r.Line("  Used by: " + string.Join(", ", vi.UsedBy));
                }
                else r.Line($"{c.Spells.Title(id)} has no visual.");
                if (!changed) return r.With(Info());
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), Info, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells learn",
            Summary = "how a spell is learned: list its trainers and skill lines, add it to a trainer at a level for a price (with its \"learn\" spell), put it in a skill line (SkillLineAbility) for classes, or remove either",
            Args = new[] { "<spell>" },
            Options = new[]
            {
                new OptionSpec("trainer", "entry", "add to this trainer (a creature entry, or list:N for a shared trainer list)"),
                new OptionSpec("level", "n", "the level the trainer teaches it at (default: the spell's level)"),
                new OptionSpec("cost", "price", "the price: copper (2500) or 25s, 1g 50s"),
                new OptionSpec("remove-trainer", "entry", "remove it from this trainer (entry or list:N)"),
                new OptionSpec("skill-line", "id", "put it into this SkillLine (a class's talent-tree tab, a profession...)"),
                new OptionSpec("class", "classes", "the classes of the skill line row: names or IDs, comma-separated (default: every class)"),
                new OptionSpec("race", "races", "the races of the skill line row (default: every race)"),
                new OptionSpec("automatic", null, "learned automatically with the skill (instead of from a trainer)"),
                new OptionSpec("remove-skill-line", "id", "take it out of this skill line"),
                Common.DryRun,
            },
            Examples = new[] { "wds spells learn 900000", "wds spells learn 900000 --trainer 3156 --level 20 --cost 25s", "wds spells learn 900000 --skill-line 355 --class Warlock" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                long id = DesignerCli.Id(inv, 0);
                var row = DesignerCli.Guard(() => c.Spells.Row(id));
                bool changed = false;
                void Do(DesignResult res)
                {
                    if (!res.Ok) throw new CliError(res.Message.Contains("not connected", StringComparison.Ordinal) ? ExitCode.Error : res.Message.StartsWith("The server has no", StringComparison.Ordinal) || res.Message.Contains(" has no row ", StringComparison.Ordinal) ? ExitCode.NotFound : ExitCode.ValidationFailed, res.Message);
                    r.Line(res.Message);
                    changed = true;
                }
                if (inv.Opt("trainer") is { } tr)
                {
                    long level = inv.IntOpt("level") ?? c.Spells.Spells.Number(row, c.Spells.Spells.Layout.Column("SpellLevel")!);
                    long cost = inv.Opt("cost") is { } cs ? DesignerCli.Guard(() => SpellDesigner.ParseMoney(cs)) : 0;
                    Do(DesignerCli.Guard(() => c.Spells.AddToTrainer(id, tr, level, cost)));
                }
                if (inv.Opt("remove-trainer") is { } rt) Do(c.Spells.RemoveFromTrainer(id, rt));
                if (inv.Opt("skill-line") is { } sl)
                {
                    if (!long.TryParse(sl, out var skill)) throw new CliError(ExitCode.Usage, "--skill-line is a SkillLine ID.");
                    long cm = inv.Opt("class") is { } cls ? Masks(cls, "ClassMask", "Class") : 0;
                    long rm = inv.Opt("race") is { } rc ? Masks(rc, "RaceMask", "Race") : 0;
                    Do(DesignerCli.Guard(() => c.Spells.AddSkillLine(id, skill, cm, rm, inv.Flag("automatic") ? 1 : 0)));
                }
                if (inv.Opt("remove-skill-line") is { } rsl && long.TryParse(rsl, out var rs)) Do(c.Spells.RemoveSkillLine(id, rs));
                var trainers = c.Spells.Trainers(id);
                var skills = c.Spells.SkillLines(id);
                object Data() => new
                {
                    id,
                    trainers = c.Spells.Trainers(id).Select(t => new { table = t.Table, trainer = t.Trainer, name = t.TrainerName, learnSpell = t.LearnSpell, level = t.Level, cost = t.Cost }),
                    skillLines = c.Spells.SkillLines(id).Select(s => new { id = s.Id, skillLine = s.SkillLine, name = s.SkillName, classMask = s.ClassMask, raceMask = s.RaceMask, acquire = s.AcquireMethod }),
                    learnSpells = c.Spells.LearnSpellsOf(id),
                };
                if (!changed)
                {
                    r.Line(trainers.Count == 0 ? (c.D.HasServer ? "No trainer teaches it." : "Trainers: the server database is not connected.") : "Trainers:");
                    foreach (var t in trainers) r.Line($"  {t.TrainerName} ({t.Table} {t.Trainer}): level {t.Level}, {SpellDesigner.Money(t.Cost)}, learn spell {t.LearnSpell}");
                    r.Line(skills.Count == 0 ? "No skill line." : "Skill lines:");
                    foreach (var s in skills) r.Line($"  {s.SkillName} (SkillLineAbility {s.Id}): {SpellDesigner.ClassNames(s.ClassMask)}, {(s.AcquireMethod == 1 ? "automatic" : "taught")}");
                    return r.With(Data());
                }
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), Data, c.D);
            },
        };
        yield return new CommandSpec
        {
            Path = "spells sync",
            Summary = "make the server's spell_template rows of spells match the game's Spell.dbc rows (after editing the game's side alone), saved into the workspace",
            Args = new[] { "<spells...>" },
            Options = new[] { Common.DryRun },
            Examples = new[] { "wds spells sync 900000 900001", "wds spells sync 900000 --dry-run" },
            Run = async inv =>
            {
                var r = new CliResult();
                var c = await DesignerCli.Open(inv, r);
                if (!c.D.HasServer) return CliResult.Fail(ExitCode.Error, "The server database is not connected (Settings: server.enabled, host, port...).");
                var ids = new List<long>();
                for (int i = 0; i < inv.Args.Count; i++)
                {
                    long id = DesignerCli.Id(inv, i);
                    DesignerCli.Guard(() => c.Spells.Row(id));
                    c.Spells.Touch(id);
                    ids.Add(id);
                }
                r.Line($"spell_template of {string.Join(", ", ids.Select(c.Spells.Title))} from the game's rows.");
                if (inv.DryRun) c.Spells.SyncServer(ids);
                return DesignerCli.Finish(inv, r, () => c.Spells.Save(), () => new { ids }, c.D);
            },
        };
    }

    /// <summary>"Warlock, Mage" or "256,128" or "0x180" as a mask of the layouts' ClassMask / RaceMask.</summary>
    public static long Masks(string text, string flags, string single)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        long mask = 0;
        var names = Core.Dbc.DbcLayouts.Default.NamedValues.GetValueOrDefault(single);
        foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            long id;
            if (long.TryParse(part, out var n)) id = n;
            else
            {
                var hit = names?.Values.FirstOrDefault(v => string.Equals(v.Name, part, StringComparison.OrdinalIgnoreCase));
                if (hit?.Name == null) throw new CliError(ExitCode.Usage, $"\"{part}\" is not a {single.ToLowerInvariant()} (a name or an ID).");
                id = hit.Value.Value;
            }
            if (id is < 1 or > 32) throw new CliError(ExitCode.Usage, $"{single} {id} has no mask bit.");
            mask |= 1L << (int)(id - 1);
        }
        return mask;
    }
}
