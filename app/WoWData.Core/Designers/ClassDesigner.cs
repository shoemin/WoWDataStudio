using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>A class of the game (a ChrClasses row).</summary>
public sealed record ClassInfo(long Id, string Name, string Token, long PowerType, string PowerText, long SpellFamily, IReadOnlyList<string> Races);

/// <summary>How a class gets a spell.</summary>
public enum SpellSource { Trainer, Taught, Automatic, Starting, Talent }

/// <summary>One spell of a class's list: the level it comes at, how, from whom, and its skill line.</summary>
public sealed record ClassSpell(long Spell, string Name, string Rank, long Level, SpellSource Source, string From, string SkillLine, long LearnSpell = 0, long Cost = 0)
{
    public string SourceText => Source switch
    {
        SpellSource.Trainer => "trainer",
        SpellSource.Taught => "taught (trainer data needs the server)",
        SpellSource.Automatic => "learned automatically",
        SpellSource.Starting => "known from the start",
        _ => "talent",
    };
}

/// <summary>A skill line of a class (SkillRaceClassInfo) with its category.</summary>
public sealed record ClassSkill(long SkillLine, string Name, string Category, long MinLevel, long RaceMask);

/// <summary>One thing a new class still needs outside the tables, with the evidence of where.</summary>
public sealed record EngineItem(string Side, string Area, string What, string Where, bool DoneByDesigner)
{
    /// <summary>"data" (the designers make it) or "code" (a change in the client project or the server source).</summary>
    public string Kind => DoneByDesigner ? "data" : "code";
}

/// <summary>
/// 0.12.0: the class designer (the Classes and talents tab, <c>wds classes ...</c>): a class's whole spell list by level
/// (trainers, skill lines, automatic spells, starting spells, talents), its skill lines and talent trees; assigning spells
/// to levels and trainers; class-mask edits across the tables; and, EXPERIMENTAL, a new class's data (ChrClasses,
/// CharBaseInfo, the shared skill lines, CharStartOutfit, playercreateinfo*, player_classlevelstats, player_levelstats)
/// with an honest report of what the 1.12 client and the server still need in code (<see cref="EngineReport"/>).
/// </summary>
public sealed class ClassDesigner
{
    public ClassDesigner(DesignSession session, SpellDesigner spells)
    {
        Session = session;
        Spells = spells;
    }

    public DesignSession Session { get; }
    public SpellDesigner Spells { get; }

    private readonly List<string> _notes = new();

    // ------------------------------------------------------------------ classes

    public List<ClassInfo> Classes()
    {
        var t = Session.Table("ChrClasses");
        return t.Rows.Select(r => Info(t.Key(r)!.Value)).OrderBy(c => c.Id).ToList();
    }

    public ClassInfo Info(long id)
    {
        var t = Session.Table("ChrClasses");
        var r = t.Find(id) ?? throw new DesignException($"There is no class {id} (ChrClasses).");
        long N(string c) => t.Number(r, t.Layout.Column(c)!);
        var power = N("DisplayPower");
        var pv = DbcLayouts.Default.NamedValues.GetValueOrDefault("PowerType")?.NameOf(power) ?? power.ToString(CultureInfo.InvariantCulture);
        var cb = Session.Table("CharBaseInfo");
        var races = cb.Rows.Where(x => cb.Number(x, cb.Layout.Column("Class")!) == id)
            .Select(x => DbcLayouts.Default.NamedValues.GetValueOrDefault("Race")?.NameOf(cb.Number(x, cb.Layout.Column("Race")!)) ?? cb.Number(x, cb.Layout.Column("Race")!).ToString(CultureInfo.InvariantCulture)).ToList();
        return new ClassInfo(id, t.Text(r, t.Layout.Column("Name")!, 0), t.Text(r, t.Layout.Column("Filename")!, 0), power, pv, N("SpellClassSet"), races);
    }

    /// <summary>A class by ID, name or token ("8", "Mage", "MAGE").</summary>
    public long Resolve(string text)
    {
        if (long.TryParse(text, out var id)) return Info(id).Id;
        var hit = Classes().FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Token, text, StringComparison.OrdinalIgnoreCase));
        return hit?.Id ?? throw new DesignException($"There is no class \"{text}\". The classes: {string.Join(", ", Classes().Select(c => $"{c.Id} {c.Name}"))}.");
    }

    public static long Bit(long classId) => TalentDesigner.ClassBit(classId);

    // ------------------------------------------------------------------ the class view

    /// <summary>The skill lines of a class (SkillRaceClassInfo rows with its bit), by category.</summary>
    public List<ClassSkill> Skills(long classId)
    {
        var bit = Bit(classId);
        var t = Session.Table("SkillRaceClassInfo");
        var sl = Session.Lookup("SkillLine");
        var cat = Session.Lookup("SkillLineCategory");
        return t.Rows.Where(r => (t.Number(r, t.Layout.Column("ClassMask")!) & bit) != 0).Select(r =>
        {
            long skill = t.Number(r, t.Layout.Column("SkillID")!);
            var sr = sl?.Find(skill);
            string name = sr != null ? sl!.Label(sr) : $"skill {skill}";
            long c = sr != null ? sl!.Number(sr, sl.Layout.Column("CategoryID")!) : -1;
            string cn = cat?.Find(c) is { } cr ? cat.Label(cr) : "";
            return new ClassSkill(skill, name, cn, t.Number(r, t.Layout.Column("MinLevel")!), t.Number(r, t.Layout.Column("RaceMask")!));
        }).GroupBy(x => x.SkillLine).Select(g => g.First()).OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();
    }

    /// <summary>
    /// The class's spells by level: from its class trainers (the server's npc_trainer / npc_trainer_template rows of
    /// creatures with trainer_class = the class; the "learn" spell's taught spell, at the row's level), the skill lines
    /// (SkillLineAbility rows with the class: learned automatically, or taught), the starting spells (playercreateinfo_spell)
    /// and the talents.
    /// </summary>
    public List<ClassSpell> SpellList(long classId)
    {
        var res = new List<ClassSpell>();
        var st = Spells.Spells;
        var nameCol = st.Layout.Column("Name")!;
        var rankCol = st.Layout.Column("NameSubtext")!;
        string Nm(long s) => st.Find(s) is { } r ? st.Text(r, nameCol, 0) : $"spell {s}";
        string Rk(long s) => st.Find(s) is { } r ? st.Text(r, rankCol, 0) : "";
        long Lvl(long s) => st.Find(s) is { } r ? st.Number(r, st.Layout.Column("SpellLevel")!) : 0;
        var bit = Bit(classId);
        // skill lines of each spell for this class
        var sla = Session.Table("SkillLineAbility");
        var slName = Session.Lookup("SkillLine");
        var skillOf = new Dictionary<long, string>();
        var auto = new List<(long Spell, long Skill)>();
        var taught = new HashSet<long>();
        var classSkills = Skills(classId).ToDictionary(x => x.SkillLine);
        foreach (var r in sla.Rows)
        {
            long cm = sla.Number(r, sla.Layout.Column("ClassMask")!);
            long ex = sla.Number(r, sla.Layout.Column("ExcludeClass")!);
            long skill = sla.Number(r, sla.Layout.Column("SkillLine")!);
            if ((ex & bit) != 0) continue;
            bool forClass = cm != 0 ? (cm & bit) != 0 : classSkills.ContainsKey(skill) && slName?.Find(skill) is { } sr && slName.Number(sr, slName.Layout.Column("CategoryID")!) == 7;
            if (!forClass) continue;
            long spell = sla.Number(r, sla.Layout.Column("Spell")!);
            var sn = slName?.Find(skill) is { } srow ? slName.Label(srow) : $"skill {skill}";
            skillOf.TryAdd(spell, sn);
            if (sla.Number(r, sla.Layout.Column("AcquireMethod")!) == 1) auto.Add((spell, skill));
            else taught.Add(spell);
        }
        var fromTrainer = new HashSet<long>();
        if (Session.Server is { } ed && ed.Schema.Table("creature_template") is { } ct)
        {
            var trainers = ed.Rows(ct, RowFilter.Where(new FilterTerm("trainer_class", FilterOp.Eq, classId.ToString(CultureInfo.InvariantCulture)), new FilterTerm("trainer_type", FilterOp.Eq, "0")));
            var lists = new Dictionary<string, List<string>>();   // "npc_trainer:entry" / "npc_trainer_template:id" -> trainer names
            foreach (var g in trainers.GroupBy(r => WorldValue.Long(r[ct.IndexOf("entry")])))
            {
                var row = ed.Service.Effective(ct, g.ToList()) ?? g.First();
                if ((WorldValue.Long(row[ct.IndexOf("npc_flags")]) & 0x10) == 0) continue;
                var nm = row[ct.IndexOf("name")] ?? "";
                long tid = ct.Has("trainer_id") ? WorldValue.Long(row[ct.IndexOf("trainer_id")]) : 0;
                var key = tid > 0 ? $"npc_trainer_template:{tid}" : $"npc_trainer:{g.Key}";
                if (!lists.TryGetValue(key, out var l)) lists[key] = l = new List<string>();
                l.Add(nm);
            }
            foreach (var (key, names) in lists)
            {
                var parts = key.Split(':');
                var tt = ed.Schema.Table(parts[0]);
                if (tt == null) continue;
                foreach (var r in ed.Rows(tt, RowFilter.Eq("entry", long.Parse(parts[1], CultureInfo.InvariantCulture))))
                {
                    if (!ed.Service.RowVersion(tt, r).Loaded) continue;
                    long learn = WorldValue.Long(r[tt.IndexOf("spell")]);
                    long spell = learn;
                    if (st.Find(learn) is { } lr && st.Number(lr, st.Layout.Column("Effect1")!) == 36) spell = st.Number(lr, st.Layout.Column("EffectTriggerSpell1")!);
                    long lvl = WorldValue.Long(r[tt.IndexOf("reqlevel")]);
                    if (lvl == 0) lvl = Lvl(learn);
                    var who = names.Count <= 2 ? string.Join(", ", names.Distinct()) : $"{names.Distinct().Count()} trainers ({names[0]}...)";
                    if (res.Any(x => x.Spell == spell && x.Source == SpellSource.Trainer)) continue;
                    res.Add(new ClassSpell(spell, Nm(spell), Rk(spell), lvl, SpellSource.Trainer, who, skillOf.GetValueOrDefault(spell, ""), learn, WorldValue.Long(r[tt.IndexOf("spellcost")])));
                    fromTrainer.Add(spell);
                }
            }
            if (ed.Schema.Table("playercreateinfo_spell") is { } pcs)
                foreach (var r in ed.Rows(pcs, RowFilter.Eq("class", classId)).Where(r => ed.Service.RowVersion(pcs, r).Loaded).GroupBy(r => WorldValue.Long(r[pcs.IndexOf("spell")])))
                {
                    var races = string.Join(", ", r.Select(x => DbcLayouts.Default.NamedValues.GetValueOrDefault("Race")?.NameOf(WorldValue.Long(x[pcs.IndexOf("race")])) ?? x[pcs.IndexOf("race")]).Distinct());
                    res.Add(new ClassSpell(r.Key, Nm(r.Key), Rk(r.Key), 1, SpellSource.Starting, races, skillOf.GetValueOrDefault(r.Key, "")));
                }
        }
        foreach (var s in taught.Where(s => !fromTrainer.Contains(s) && st.Find(s) != null))
            if (Session.Server == null) res.Add(new ClassSpell(s, Nm(s), Rk(s), Lvl(s), SpellSource.Taught, "", skillOf.GetValueOrDefault(s, "")));
        foreach (var (s, skill) in auto.Where(x => st.Find(x.Spell) != null))
            if (!res.Any(x => x.Spell == s))
                res.Add(new ClassSpell(s, Nm(s), Rk(s), Math.Max(classSkills.GetValueOrDefault(skill)?.MinLevel ?? 0, Lvl(s)), SpellSource.Automatic, "with its skill", skillOf.GetValueOrDefault(s, "")));
        var talents = new TalentDesigner(Session, Spells);
        foreach (var tree in talents.Trees(classId))
            foreach (var n in tree.Talents)
                for (int i = 0; i < n.Ranks.Count; i++)
                    res.Add(new ClassSpell(n.Ranks[i], n.Name, $"Rank {i + 1}", 10 + n.Tier * 5 + i, SpellSource.Talent, tree.Name, tree.Name));
        return res.OrderBy(x => x.Level).ThenBy(x => x.Source).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Spell).ToList();
    }

    // ------------------------------------------------------------------ assigning spells to levels and trainers

    /// <summary>
    /// Give a class's spell a level: every trainer row of the class that teaches it gets the level (and the cost, when
    /// given), and the spell's own level follows; <paramref name="trainers"/> = "all" adds it to every class trainer of
    /// the class that lacks it, or a list of creature entries / "list:N".
    /// </summary>
    public DesignResult Assign(long classId, long spell, long level, string? trainers = null, long? cost = null)
    {
        if (level is < 1 or > 255) return DesignResult.Refused("The level is 1-255.");
        Spells.Row(spell);
        var msgs = new List<string>();
        if (Session.Server is { } ed)
        {
            var existing = Spells.Trainers(spell);
            var classLists = ClassTrainerLists(classId);
            foreach (var e in existing.Where(e => classLists.Contains($"{e.Table}:{e.Trainer}")))
            {
                var vals = new List<(string, string?)> { ("reqlevel", level.ToString(CultureInfo.InvariantCulture)) };
                if (cost != null) vals.Add(("spellcost", cost.Value.ToString(CultureInfo.InvariantCulture)));
                var r = ed.Set(e.Table, e.Key, vals, $"{Spells.Name(spell)}: level {level}");
                if (!r.Ok) return DesignResult.Refused(r.Message);
                msgs.Add($"{e.TrainerName}: level {level}");
            }
            IEnumerable<string> targets = trainers == null ? Array.Empty<string>()
                : trainers.Equals("all", StringComparison.OrdinalIgnoreCase) ? classLists.Where(k => !existing.Any(e => $"{e.Table}:{e.Trainer}" == k)).Select(k => k.StartsWith("npc_trainer_template:", StringComparison.Ordinal) ? "list:" + k.Split(':')[1] : k.Split(':')[1])
                : trainers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var target in targets)
            {
                var r = Spells.AddToTrainer(spell, target, level, cost ?? existing.FirstOrDefault()?.Cost ?? 0);
                if (!r.Ok) return r;
                msgs.Add(r.Message);
            }
        }
        else if (trainers != null) return DesignResult.Refused("Trainers live in the server's database, which is not connected.");
        var res = Spells.Set(spell, "level", level.ToString(CultureInfo.InvariantCulture));
        if (!res.Ok) return res;
        return DesignResult.Done($"{Spells.Title(spell)} comes at level {level}." + (msgs.Count > 0 ? " " + string.Join("; ", msgs) : Session.Server == null ? " (The spell's level only: the trainers need the server database.)" : " (No trainer of the class teaches it yet: add --trainer all.)"));
    }

    /// <summary>The trainer lists of a class's trainers: "npc_trainer:entry" or "npc_trainer_template:id".</summary>
    public HashSet<string> ClassTrainerLists(long classId)
    {
        var res = new HashSet<string>();
        if (Session.Server is not { } ed || ed.Schema.Table("creature_template") is not { } ct) return res;
        foreach (var g in ed.Rows(ct, RowFilter.Where(new FilterTerm("trainer_class", FilterOp.Eq, classId.ToString(CultureInfo.InvariantCulture)), new FilterTerm("trainer_type", FilterOp.Eq, "0"))).GroupBy(r => WorldValue.Long(r[ct.IndexOf("entry")])))
        {
            var row = ed.Service.Effective(ct, g.ToList()) ?? g.First();
            if ((WorldValue.Long(row[ct.IndexOf("npc_flags")]) & 0x10) == 0) continue;
            long tid = ct.Has("trainer_id") ? WorldValue.Long(row[ct.IndexOf("trainer_id")]) : 0;
            res.Add(tid > 0 ? $"npc_trainer_template:{tid}" : $"npc_trainer:{g.Key}");
        }
        return res;
    }

    // ------------------------------------------------------------------ class masks

    /// <summary>The tables with class masks the designer edits (game table.column, or server table.column).</summary>
    public static readonly (string Table, string Column, bool Server)[] MaskColumns =
    {
        ("SkillLineAbility", "ClassMask", false), ("SkillLineAbility", "ExcludeClass", false), ("SkillRaceClassInfo", "ClassMask", false), ("TalentTab", "ClassMask", false),
        ("item_template", "allowable_class", true), ("quest_template", "RequiredClasses", true), ("skill_line_ability", "class_mask", true),
    };

    /// <summary>Add or remove a class's bit in the class mask of rows (game tables by ID; server tables by key or ID).
    /// SkillLineAbility rows are mirrored to the server's skill_line_ability on save.</summary>
    public DesignResult EditMask(string table, string? column, IReadOnlyList<string> ids, long classId, bool add)
    {
        var bit = Bit(classId);
        var spec = MaskColumns.FirstOrDefault(m => string.Equals(m.Table, table, StringComparison.OrdinalIgnoreCase) && (column == null || string.Equals(m.Column, column, StringComparison.OrdinalIgnoreCase)));
        if (spec.Table == null) return DesignResult.Refused($"No class mask is known for {table}{(column != null ? "." + column : "")}. The masks: {string.Join(", ", MaskColumns.Select(m => $"{m.Table}.{m.Column}"))}.");
        int n = 0;
        if (!spec.Server)
        {
            var t = Session.Table(spec.Table);
            var c = t.Layout.Column(spec.Column)!;
            foreach (var idText in ids)
            {
                if (!long.TryParse(idText, out var id) || t.Find(id) is not { } row) return DesignResult.Refused($"{spec.Table} has no row {idText}.");
                long v = t.Number(row, c) & 0xFFFFFFFF;
                long nv = add ? v | bit : v & ~bit;
                if (nv == v) continue;
                Session.Set(spec.Table, row, spec.Column, "0x" + nv.ToString("X", CultureInfo.InvariantCulture));
                if (spec.Table == "SkillLineAbility") Spells.SyncSkillRowOnSave(id);
                n++;
            }
        }
        else
        {
            if (Session.Server is not { } ed) return DesignResult.Refused("The server database is not connected.");
            var t = ed.Table(spec.Table);
            foreach (var idText in ids)
            {
                string key;
                try { key = ed.ResolveKey(t, idText); }
                catch (ServerDataException e) { return DesignResult.Refused(e.Message); }
                var row = ed.Current(t, key) ?? throw new DesignException($"{t.Name} has no row {key}.");
                long v = WorldValue.Long(row[t.IndexOf(spec.Column)]);
                // a mask of 0 means "every class": removing one class makes the mask every other playable class
                if (v == 0 && !add) v = 0x5DF;
                long nv = add ? v | bit : v & ~bit;
                if (nv == v) continue;
                var r = ed.Set(t.Name, key, spec.Column, nv.ToString(CultureInfo.InvariantCulture));
                if (!r.Ok) return DesignResult.Refused(r.Message);
                n++;
            }
        }
        return DesignResult.Done($"{(add ? "Added" : "Removed")} class {classId} {(add ? "to" : "from")} {n} row(s) of {spec.Table}.{spec.Column}.");
    }

    // ------------------------------------------------------------------ a new class (EXPERIMENTAL)

    /// <summary>The first free class ID: 6 and 10 (free in 1.12 and below the server's MAX_CLASSES 12), then 12 and up.</summary>
    public long FreeClassId()
    {
        var used = Classes().Select(c => c.Id).ToHashSet();
        foreach (var id in new long[] { 6, 10 }.Concat(Enumerable.Range(12, 21).Select(i => (long)i)))
            if (!used.Contains(id)) return id;
        throw new DesignException("No class ID is free (class masks have 32 bits).");
    }

    /// <summary>
    /// EXPERIMENTAL: the data of a new class, copied from a template class: ChrClasses (name, token, power, its own spell
    /// family), CharBaseInfo (one row per race), the template's shared skill lines (weapons, armor, languages: the new bit
    /// in SkillRaceClassInfo; the template's own class skill lines are not shared), CharStartOutfit (the template's
    /// outfits), and on the server playercreateinfo, playercreateinfo_spell / _action / _item, player_classlevelstats and
    /// player_levelstats (the template's rows for those races). The game and the server still need code changes before
    /// such a class can be played: see <see cref="EngineReport"/>.
    /// </summary>
    public (long Id, List<string> Done) NewClass(string name, string token, long templateClass, IReadOnlyList<long> races, long? powerType = null, long? id = null)
    {
        var done = new List<string>();
        var tpl = Info(templateClass);
        long cid = id ?? FreeClassId();
        if (cid is < 1 or > 32) throw new DesignException("A class ID is 1-32 (class masks have 32 bits).");
        if (Session.Table("ChrClasses").Find(cid) != null) throw new DesignException($"Class {cid} already exists ({Info(cid).Name}).");
        token = token.Trim().ToUpperInvariant();
        if (token.Length == 0 || !token.All(ch => ch is >= 'A' and <= 'Z' or '_')) throw new DesignException("The token is the class's internal name in capitals (KNIGHT).");
        if (Classes().Any(c => string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase))) throw new DesignException($"The token {token} is taken.");
        if (races.Count == 0) throw new DesignException("Choose at least one race.");
        var chr = Session.Table("ChrClasses");
        var (row, _) = Session.Session("ChrClasses").Duplicate(chr.Find(templateClass)!, cid);
        Session.Set("ChrClasses", row, "Name", name);
        Session.Set("ChrClasses", row, "Filename", token);
        if (powerType != null) Session.SetNumber("ChrClasses", row, "DisplayPower", powerType.Value);
        long family = Enumerable.Range(14, 50).Select(i => (long)i).First(f => chr.Rows.All(r => chr.Number(r, chr.Layout.Column("SpellClassSet")!) != f));
        Session.SetNumber("ChrClasses", row, "SpellClassSet", family);
        done.Add($"ChrClasses {cid}: {name} ({token}), power {powerType ?? tpl.PowerType}, spell family {family} (its own, so other classes' talents do not change its spells).");

        var cb = Session.Table("CharBaseInfo");
        foreach (var race in races)
        {
            if (cb.Rows.Any(r => cb.Number(r, cb.Layout.Column("Race")!) == race && cb.Number(r, cb.Layout.Column("Class")!) == cid)) continue;
            var nr = Session.AddRow("CharBaseInfo");
            Session.SetNumber("CharBaseInfo", nr, "Race", race);
            Session.SetNumber("CharBaseInfo", nr, "Class", cid);
        }
        done.Add($"CharBaseInfo: {races.Count} race(s) can choose it on the character-creation screen.");

        var bit = Bit(cid);
        var tbit = Bit(templateClass);
        var srci = Session.Table("SkillRaceClassInfo");
        var sl = Session.Lookup("SkillLine");
        int shared = 0;
        foreach (var r in srci.Rows.ToList())
        {
            long mask = srci.Number(r, srci.Layout.Column("ClassMask")!) & 0xFFFFFFFF;
            if ((mask & tbit) == 0 || (mask & bit) != 0) continue;
            long skill = srci.Number(r, srci.Layout.Column("SkillID")!);
            if (sl?.Find(skill) is { } sr && sl.Number(sr, sl.Layout.Column("CategoryID")!) == 7) continue;   // the template's own class skill lines
            Session.Set("SkillRaceClassInfo", r, "ClassMask", "0x" + (mask | bit).ToString("X", CultureInfo.InvariantCulture));
            shared++;
        }
        done.Add($"SkillRaceClassInfo: the {shared} shared skill line row(s) of {tpl.Name} (weapons, armor, languages...) now include the new class. Its own class skill lines (talent tabs) come with the talent designer.");

        var cso = Session.Table("CharStartOutfit");
        int outfits = 0;
        foreach (var race in races)
            foreach (var r in cso.Rows.Where(r => cso.Number(r, cso.Layout.Column("Class")!) == templateClass).ToList())
            {
                long rr = cso.Number(r, cso.Layout.Column("Race")!);
                bool sameRace = rr == race;
                bool anyRace = !cso.Rows.Any(x => cso.Number(x, cso.Layout.Column("Class")!) == templateClass && cso.Number(x, cso.Layout.Column("Race")!) == race);
                if (!sameRace && !(anyRace && rr == cso.Rows.Where(x => cso.Number(x, cso.Layout.Column("Class")!) == templateClass).Select(x => cso.Number(x, cso.Layout.Column("Race")!)).Min())) continue;
                var (nr, _) = Session.Session("CharStartOutfit").Duplicate(r);
                Session.SetNumber("CharStartOutfit", nr, "Race", race);
                Session.SetNumber("CharStartOutfit", nr, "Class", cid);
                outfits++;
            }
        done.Add($"CharStartOutfit: {outfits} outfit row(s) copied from {tpl.Name} (what the creation screen shows).");

        if (Session.Server is { } ed)
        {
            int rows = 0;
            foreach (var table in new[] { "playercreateinfo", "playercreateinfo_spell", "playercreateinfo_action", "playercreateinfo_item", "player_levelstats" })
            {
                if (ed.Schema.Table(table) is not { } t) continue;
                foreach (var race in races)
                {
                    var src = ed.Rows(t, RowFilter.Where(new FilterTerm("race", FilterOp.Eq, race.ToString(CultureInfo.InvariantCulture)), new FilterTerm("class", FilterOp.Eq, templateClass.ToString(CultureInfo.InvariantCulture))));
                    if (src.Count == 0) src = ed.Rows(t, RowFilter.Eq("class", templateClass)).GroupBy(x => x[t.IndexOf("race")]).FirstOrDefault()?.ToList() ?? new();
                    var batch = new List<(WorldTable, string, string?[]?)>();
                    foreach (var r in src)
                    {
                        var copy = (string?[])r.Clone();
                        copy[t.IndexOf("race")] = race.ToString(CultureInfo.InvariantCulture);
                        copy[t.IndexOf("class")] = cid.ToString(CultureInfo.InvariantCulture);
                        var key = t.KeyText(copy);
                        if (ed.Current(t, key) != null) continue;
                        batch.Add((t, key, copy));
                    }
                    rows += ed.ApplyRows($"New class {name}: {table}", batch);
                }
            }
            if (ed.Schema.Table("player_classlevelstats") is { } cls)
            {
                var batch = new List<(WorldTable, string, string?[]?)>();
                foreach (var r in ed.Rows(cls, RowFilter.Eq("class", templateClass)))
                {
                    var copy = (string?[])r.Clone();
                    copy[cls.IndexOf("class")] = cid.ToString(CultureInfo.InvariantCulture);
                    if (ed.Current(cls, cls.KeyText(copy)) == null) batch.Add((cls, cls.KeyText(copy), copy));
                }
                rows += ed.ApplyRows($"New class {name}: player_classlevelstats", batch);
            }
            done.Add($"Server: {rows} row(s) of playercreateinfo, playercreateinfo_spell / _action / _item, player_classlevelstats and player_levelstats copied from {tpl.Name} (start position, starting spells, action bar, items, health / mana and stats per level).");
            if (rows == 0) done.Add($"The server's database has no playercreateinfo rows of {tpl.Name} to copy: add them in the Server data tab.");
        }
        else done.Add("The server database is not connected: playercreateinfo*, player_classlevelstats and player_levelstats were not made (run it again with the server connected).");
        _notes.Add("EXPERIMENTAL: a new class needs code changes in the game and the server before it can be played (wds classes engine-report).");
        return (cid, done);
    }

    /// <summary>
    /// What a new class still needs outside the tables, honestly: the client (WoW.exe, read in our decompile, and its
    /// interface files) and the server (VMaNGOS source). "Done by the designer" items are data it writes; everything else
    /// is code. Not exhaustive: these are the places found so far.
    /// </summary>
    public static List<EngineItem> EngineReport(long? classId = null)
    {
        string id = classId?.ToString(CultureInfo.InvariantCulture) ?? "N";
        bool low = classId is 6 or 10;
        return new List<EngineItem>
        {
            new("data", "Tables", "ChrClasses, CharBaseInfo, SkillRaceClassInfo (shared skills), CharStartOutfit; playercreateinfo, _spell, _action, _item, player_classlevelstats, player_levelstats", "the class designer (wds classes new)", true),
            new("data", "Tables", "Class skill lines, talent trees, spells, trainers", "the talent and spell designers (wds talents, wds spells)", true),
            new("data", "Server files", "The server reads its own ChrClasses.dbc, SkillRaceClassInfo.dbc, Talent.dbc and TalentTab.dbc", "Copy tables to the server... (wds dbc copy-to-server)", true),
            new("server", "Class list", $"Classes enum (IDs 1-11; 6 and 10 unused) and CLASSMASK_ALL_PLAYABLE (the nine classes): add class {id}. " + (low ? "Its ID is below MAX_CLASSES (12): no array grows." : "An ID of 12 or more also needs MAX_CLASSES raised (m_PlayerClassInfo[MAX_CLASSES], m_PlayerInfo[MAX_RACES][MAX_CLASSES])."), "src\\game\\SharedDefines.h (enum Classes, MAX_CLASSES, CLASSMASK_ALL_PLAYABLE)", false),
            new("server", "Character data", "ObjectMgr::LoadPlayerInfo skips playercreateinfo, _spell, _action, _item, player_classlevelstats and player_levelstats rows of a class outside CLASSMASK_ALL_PLAYABLE (\"Wrong class\"): without the code change the new rows are ignored and the class cannot be created.", "src\\game\\ObjectMgr.cpp (checks at lines 4434, 4508, 4581, 4642, 4694, 4801, 4848)", false),
            new("server", "Items and quests", "Item AllowableClass and quest RequiredClasses masks are checked against CLASSMASK_ALL_PLAYABLE.", "src\\game\\ObjectMgr.cpp (lines 3889, 5342); src\\game\\Conditions.cpp:984; src\\game\\Guild\\Guild.cpp (lines 242, 483: guild members of other classes are dropped)", false),
            new("server", "Stats and combat", "Per-class formulas: melee and spell critical chance from Agility / Intellect (crit_data[MAX_CLASSES]), attack power, mana regeneration, creature class handling.", "src\\game\\Objects\\Player.cpp (GetMeleeCritFromAgility :5597, GetSpellCritFromIntellect :5685, switch on class :5602, :5646, :21581); src\\game\\StatSystem.cpp (switch on class :199, :230, :542, :601); src\\game\\Objects\\Unit.cpp:10428; src\\game\\Objects\\Creature.cpp:1613", false),
            new("server", "Character creation", "HandleCharCreateOpcode needs the class in the server's ChrClasses.dbc and a playercreateinfo row for the race and class.", "src\\game\\Handlers\\CharacterHandler.cpp (class check around line 232); data: Copy tables to the server", false),
            new("server", "New power types", "A resource other than mana, rage, focus, energy or happiness (Resolve, Momentum, Heat...) needs its own regeneration and decay rules.", "src\\game\\Objects\\Player.cpp (Regenerate...), Unit power code", false),
            new("client", "Character creation screen", "GlueXML has 8 class buttons (CharacterCreateClassButton1-8; MAX_CLASSES_PER_RACE = 8 with the message \"Too many classes!\") and CLASS_ICON_TCOORDS has the 9 class tokens on UI-CharacterCreate-Classes.blp: a 9th class for a race, and an icon for the token, need interface changes.", "Interface\\GlueXML\\CharacterCreate.lua / CharacterCreate.xml (the client checks the signature of Blizzard's interface files: the project's client must allow the changed files)", false),
            new("client", "Class list per race", "The creation screen lists a race's classes from CharBaseInfo (data), but its code skips race 3 class 8 (dwarf mage) by hand.", "WoW.exe @004706b0 (CharCreate class list)", false),
            new("client", "ChrClasses", "The game's ChrClasses loader needs 17 fields per row (the designer keeps the layout); the local player's spell family comes from ChrClasses.SpellClassSet (data).", "WoW.exe @00542360 (loader), @006e6ca0 (spell family)", true),
            new("client", "Class-specific code", "Found so far: the pet UI is shown for class 3 (hunter) pets only; the soul shard count is for class 9 (warlock). A new class with a pet or a shard-like reagent needs these changed.", "WoW.exe @006116e0 (HasPetUI / GetPetLoyalty), @0072b0b0 (ammo / soul shard count)", false),
            new("client", "Power bar", "DisplayPower must be 0-4 (mana, rage, focus, energy, happiness): the unit frames colour the bar from ManaBarColor[0-4]. A new power type needs interface and client code.", "Interface\\FrameXML\\UnitFrame.lua (ManaBarColor); WoW.exe power fields", false),
            new("client", "Interface texts", "Stat tooltips per class use <TOKEN>_<STAT>_TOOLTIP strings (they fall back to DEFAULT_...); the talent frame shows up to 5 trees x 20 talents.", "Interface\\FrameXML\\PaperDollFrame.lua, GlobalStrings.lua; Blizzard_TalentUI", false),
        };
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Save into the workspace (the spell designer's save: the client tables and the server rows, one change set).</summary>
    public DesignSaveResult Save(string? label = null)
    {
        var res = Spells.Save(label ?? "Class designer", force: false);
        res.Notes.AddRange(_notes);
        _notes.Clear();
        return res;
    }

    public IReadOnlyList<string> Notes => _notes;
}
