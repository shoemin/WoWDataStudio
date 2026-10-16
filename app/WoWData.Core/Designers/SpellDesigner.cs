using System.Globalization;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>A trainer entry of a spell (the server's npc_trainer or npc_trainer_template row).</summary>
public sealed record TrainerEntry(string Table, string Key, long Trainer, string TrainerName, long LearnSpell, long Spell, long Level, long Cost, long ReqSkill, long ReqSkillValue);

/// <summary>A SkillLineAbility row of a spell (which skill line / talent-tree tab it belongs to, for which classes).</summary>
public sealed record SkillLineEntry(long Id, long SkillLine, string SkillName, long Spell, long RaceMask, long ClassMask, long AcquireMethod, long SupersededBy);

/// <summary>
/// 0.12.0: the spell designer (the app's Spell designer tab and <c>wds spells ...</c>). Create a new spell or clone an
/// existing one ("a spell like Shadow Bolt"), edit it through the form (<see cref="SpellForm"/>), see its tooltip as the
/// game fills it in (<see cref="SpellText"/>), check it against what the 1.12 client and server can handle
/// (<see cref="Check"/>), pick or duplicate its visual (<see cref="SpellVisuals"/>), make it learnable (trainers, skill
/// lines) and save everything in one change set: the game's Spell.dbc (and the helper tables it needed: SpellIcon,
/// SpellCastTimes, SpellDuration, SpellRange, SpellRadius, SpellVisual...) and the server's spell_template, kept in sync
/// field by field with the same mapping as the client / server check (<see cref="ServerConsistency"/>).
/// New spells take free IDs from your custom ID range (Settings: Custom ID range), free in both Spell.dbc and
/// spell_template.
/// </summary>
public sealed class SpellDesigner
{
    private readonly HashSet<long> _touched = new();
    private readonly Dictionary<long, Dictionary<string, string?>> _extras = new();

    public SpellDesigner(DesignSession session) => Session = session;

    public DesignSession Session { get; }
    public DbcTable Spells => Session.Table("Spell");
    public IReadOnlyCollection<long> Touched => _touched;

    public DbcRow Row(long id) => Spells.Find(id) ?? throw new DesignException($"There is no spell {id} in Spell.dbc.");

    public bool Exists(long id) => Spells.Find(id) != null;

    public string Name(long id) => Spells.Find(id) is { } r ? Spells.Text(r, Spells.Layout.Column("Name")!, 0) : $"#{id}";

    public string Title(long id)
    {
        var r = Row(id);
        var rank = Spells.Text(r, Spells.Layout.Column("NameSubtext")!, 0);
        return $"{Name(id)}{(rank.Length > 0 ? $" ({rank})" : "")} [{id}]";
    }

    /// <summary>The server-only values the designer will write for a spell (spell_template's effectBonusCoefficient1-3,
    /// customFlags), from the server's row of the spell or its clone source, and your edits.</summary>
    public IReadOnlyDictionary<string, string?> ServerExtras(long id) => Extras(id);

    private Dictionary<string, string?> Extras(long id)
    {
        if (_extras.TryGetValue(id, out var d)) return d;
        d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (ServerRow(id) is { } row && Session.Server!.Schema.Table("spell_template") is { } st)
            foreach (var c in ServerOnlyColumns)
                if (st.Has(c)) d[c] = row[st.IndexOf(c)];
        _extras[id] = d;
        return d;
    }

    public static readonly string[] ServerOnlyColumns = { "effectBonusCoefficient1", "effectBonusCoefficient2", "effectBonusCoefficient3", "customFlags" };

    /// <summary>The server's row of a spell (with your server edits), or null (no server, or the server has none).</summary>
    public string?[]? ServerRow(long id)
    {
        if (Session.Server is not { } ed || ed.Schema.Table("spell_template") is not { } st) return null;
        return ed.Object(st, id);
    }

    // ------------------------------------------------------------------ new and clone

    /// <summary>A new, empty spell: instant, self-cast, on the global cooldown, mana, with a name and nothing else.</summary>
    public long New(string name, long? id = null, string rank = "")
    {
        long nid = id ?? Session.FreeCustomId("Spell", "spell_template", "entry");
        if (Spells.Find(nid) != null) throw new DesignException($"Spell {nid} already exists ({Name(nid)}). Each spell needs its own ID.");
        if (Session.Server?.Schema.Table("spell_template") is { } st && Session.Server.Object(st, nid) != null)
            throw new DesignException($"The server already has a spell {nid} in spell_template. Choose another ID.");
        var row = Session.AddRow("Spell", nid);
        void N(string c, long v) => Session.SetNumber("Spell", row, c, v);
        N("CastingTimeIndex", SpellForm.CastTimeRow(Session, 0, out _));
        N("RangeIndex", SpellForm.RangeRow(Session, 0, 0, out _));
        N("PowerType", 0);
        N("StartRecoveryCategory", 133);
        N("StartRecoveryTime", 1500);
        N("DefenseType", 1);
        N("EquippedItemClass", -1);
        N("SpellLevel", 1);
        N("BaseLevel", 1);
        N("SpellIconID", Session.Lookup("SpellIcon")?.Find(1) != null ? 1 : 0);
        N("StanceBarOrder", -1);
        Session.Set("Spell", row, "Name", name);
        Session.Set("Spell", row, "NameSubtext", rank);
        _touched.Add(nid);
        _extras[nid] = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["effectBonusCoefficient1"] = "-1", ["effectBonusCoefficient2"] = "-1", ["effectBonusCoefficient3"] = "-1", ["customFlags"] = "0" };
        return nid;
    }

    /// <summary>A copy of an existing spell with a new ID (and a new name and rank when given): every value, the icon,
    /// the visual, the effects. The server-only values come from the server's row of the source.</summary>
    public long Clone(long source, string? name = null, long? id = null, string? rank = null)
    {
        var src = Row(source);
        long nid = id ?? Session.FreeCustomId("Spell", "spell_template", "entry");
        if (Spells.Find(nid) != null) throw new DesignException($"Spell {nid} already exists ({Name(nid)}). Each spell needs its own ID.");
        if (Session.Server?.Schema.Table("spell_template") is { } st && Session.Server.Object(st, nid) != null)
            throw new DesignException($"The server already has a spell {nid} in spell_template. Choose another ID.");
        var (row, _) = Session.Session("Spell").Duplicate(src, nid);
        if (name != null) Session.Set("Spell", row, "Name", name);
        if (rank != null) Session.Set("Spell", row, "NameSubtext", rank);
        _extras[nid] = new Dictionary<string, string?>(Extras(source), StringComparer.OrdinalIgnoreCase);
        if (_extras[nid].Count == 0)
            foreach (var c in ServerOnlyColumns) _extras[nid][c] = c == "customFlags" ? "0" : "-1";
        _touched.Add(nid);
        return nid;
    }

    /// <summary>Find spells by name (the designer's "clone from" picker): exact names first, then by rank.</summary>
    public List<(long Id, string Name, string Rank)> Search(string words, int max = 50)
    {
        var t = Spells;
        var nameCol = t.Layout.Column("Name")!;
        var rankCol = t.Layout.Column("NameSubtext")!;
        if (long.TryParse(words.Trim(), out var id) && t.Find(id) is { } byId) return new() { (id, t.Text(byId, nameCol, 0), t.Text(byId, rankCol, 0)) };
        var w = words.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return t.Rows.Select(r => (Id: t.Key(r)!.Value, Name: t.Text(r, nameCol, 0), Rank: t.Text(r, rankCol, 0)))
            .Where(x => w.All(x.Name.ToLowerInvariant().Contains))
            .OrderBy(x => string.Equals(x.Name, words.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            .Take(max).ToList();
    }

    // ------------------------------------------------------------------ editing

    public string Show(long id, string field) => SpellForm.Show(Session, Row(id), SpellForm.Require(field), Extras(id));

    /// <summary>Set a field (friendly text, see <see cref="SpellForm"/>). Refused values change nothing.</summary>
    public DesignResult Set(long id, string field, string value)
    {
        var f = SpellForm.Require(field);
        var row = Row(id);
        try
        {
            var note = SpellForm.Set(Session, row, f, value, Extras(id));
            _touched.Add(id);
            var shown = SpellForm.Show(Session, row, f, Extras(id));
            return DesignResult.Done($"{f.Label}: {shown}" + (shown.EndsWith('.') ? "" : ".") + (note.Length > 0 ? " " + note : ""));
        }
        catch (DesignException e)
        {
            return DesignResult.Refused(e.Message);
        }
    }

    /// <summary>Mark a spell for the server sync on save (after an edit made directly in its Spell.dbc row).</summary>
    public void Touch(long id) => _touched.Add(id);

    // ------------------------------------------------------------------ tooltip

    public SpellTextResult Tooltip(long id, int? level = null, bool aura = false) => SpellText.Describe(Spells, Row(id), Session.Lookups, level, aura);

    // ------------------------------------------------------------------ checks

    /// <summary>Everything about a spell that the 1.12 client or server cannot handle, or that is probably a mistake, in
    /// plain words. Errors block saving.</summary>
    public List<DesignProblem> Check(long id)
    {
        var res = new List<DesignProblem>();
        var t = Spells;
        var row = Row(id);
        long N(string c) => t.Number(row, t.Layout.Column(c)!);
        float Fl(string c) => t.Float(row, t.Layout.Column(c)!);
        void Add(DesignSeverity s, string field, string msg) => res.Add(new DesignProblem(s, field, msg));
        var lookup = Session.Lookups;

        if (t.Text(row, t.Layout.Column("Name")!, 0).Trim().Length == 0) Add(DesignSeverity.Error, "Name", "The spell has no name: the game shows an empty spell book entry and cast bar.");
        if (N("SpellIconID") != 0 && lookup("SpellIcon")?.Find(N("SpellIconID")) == null) Add(DesignSeverity.Warning, "Icon", $"SpellIcon {N("SpellIconID")} does not exist: the game shows a question mark.");
        foreach (var (col, table) in new[] { ("CastingTimeIndex", "SpellCastTimes"), ("RangeIndex", "SpellRange"), ("DurationIndex", "SpellDuration") })
            if (N(col) != 0 && lookup(table)?.Find(N(col)) == null) Add(DesignSeverity.Error, col, $"{table} has no row {N(col)}: the game and the server cannot read this spell's {col[..^5].ToLowerInvariant()}.");
        if (N("RangeIndex") == 0) Add(DesignSeverity.Warning, "Range", "No range row (0): the game treats it as self-cast (\"self\" is row 1).");
        if (N("SpellVisual") != 0 && lookup("SpellVisual")?.Find(N("SpellVisual")) == null) Add(DesignSeverity.Warning, "Spell visual", $"SpellVisual {N("SpellVisual")} does not exist: the spell plays no visual.");
        if (N("PowerType") is 1 or 3 && N("ManaCost") > 1000) Add(DesignSeverity.Warning, "Cost", N("PowerType") == 1 ? $"{N("ManaCost") / 10} rage is more than a warrior ever has (100)." : $"{N("ManaCost")} energy is more than a rogue ever has (100).");
        if (N("StackAmount") > 1 && !Enumerable.Range(1, 3).Any(e => SpellRules.AuraEffects.Contains((int)N($"Effect{e}")))) Add(DesignSeverity.Warning, "Stacks up to", "Only auras stack, and this spell applies none.");
        if ((N("AttributesEx") & 0x44) != 0 && N("DurationIndex") == 0) Add(DesignSeverity.Error, "Duration", "A channelled spell needs a duration (how long it channels).");
        if (N("ProcFlags") != 0 && !Enumerable.Range(1, 3).Any(e => N($"Effect{e}") is 6 or 35 or 119 or 128 or 129 && SpellRules.ProcAuras.Contains((int)N($"EffectAura{e}"))))
            Add(DesignSeverity.Warning, "Proc on", "Proc flags only matter for a proc aura (Proc trigger spell, Proc trigger damage, Add target trigger).");
        if (N("SpellClassSet") == 0 && (N("SpellClassMask1") != 0 || N("SpellClassMask2") != 0)) Add(DesignSeverity.Warning, "Family mask", "A family mask without a class (spell family Generic) is matched by no talent.");

        bool any = false;
        for (int e = 1; e <= 3; e++)
        {
            string g = $"Effect {e}";
            long eff = N($"Effect{e}"), aura = N($"EffectAura{e}");
            if (eff == 0)
            {
                if (aura != 0) Add(DesignSeverity.Warning, g, "An aura type is set but the effect is empty.");
                continue;
            }
            any = true;
            if (e > 1 && N($"Effect{e - 1}") == 0 && e == 2) Add(DesignSeverity.Note, g, "Effect 1 is empty: the server's trainer and talent code look at effect 1 first.");
            var ei = SpellRules.Effect(eff);
            if (ei.Server == ServerSupport.Unknown) Add(DesignSeverity.Error, g, ei.Explanation);
            else if (ei.Server == ServerSupport.DoesNothing) Add(DesignSeverity.Error, g, $"\"{ei.Name}\": {ei.ServerText}");
            else if (ei.Server == ServerSupport.NeedsScript) Add(DesignSeverity.Warning, g, $"\"{ei.Name}\": {ei.ServerText}");
            bool isAura = SpellRules.AuraEffects.Contains((int)eff);
            if (isAura && aura == 0) Add(DesignSeverity.Error, g, $"\"{ei.Name}\" needs an aura type (which buff or debuff).");
            if (!isAura && aura != 0) Add(DesignSeverity.Warning, g, $"The aura type is ignored: \"{ei.Name}\" applies no aura.");
            if (isAura && aura != 0)
            {
                var ai = SpellRules.Aura(aura);
                if (ai.Server == ServerSupport.Unknown) Add(DesignSeverity.Error, g, ai.Explanation);
                else if (ai.Server == ServerSupport.DoesNothing) Add(DesignSeverity.Error, g, $"Aura \"{ai.Name}\": {ai.ServerText}");
                else if (ai.Server == ServerSupport.NeedsScript) Add(DesignSeverity.Warning, g, $"Aura \"{ai.Name}\": {ai.ServerText}");
                if (SpellRules.PeriodicAuras.Contains((int)aura) && N($"EffectAuraPeriod{e}") <= 0) Add(DesignSeverity.Error, g, $"\"{ai.Name}\" ticks, but \"Tick every\" is 0: the server never ticks it.");
                if (SpellRules.PeriodicAuras.Contains((int)aura) && N("DurationIndex") == 0) Add(DesignSeverity.Warning, g, $"\"{ai.Name}\" ticks, but the spell has no duration.");
                if (SpellRules.ProcAuras.Contains((int)aura) && N("ProcFlags") == 0) Add(DesignSeverity.Error, g, $"\"{ai.Name}\" fires on events, but \"Proc on\" is empty: it never fires.");
                if (SpellRules.ProcAuras.Contains((int)aura) && N("ProcChance") == 0) Add(DesignSeverity.Warning, g, "The proc chance is 0%.");
                if (SpellRules.TriggerAuras.Contains((int)aura)) CheckTrigger(e, g, N($"EffectTriggerSpell{e}"));
                if (aura == 36 && N($"EffectMiscValue{e}") == 0) Add(DesignSeverity.Warning, g, "Mod shapeshift needs the form in the misc value.");
                if (aura is 56 or 78 && N($"EffectMiscValue{e}") > 0) CheckCreature(g, N($"EffectMiscValue{e}"));
            }
            if (SpellRules.TriggerEffects.Contains((int)eff)) CheckTrigger(e, g, N($"EffectTriggerSpell{e}"));
            if (SpellRules.SummonEffects.Contains((int)eff))
            {
                if (N($"EffectMiscValue{e}") <= 0) Add(DesignSeverity.Error, g, $"\"{ei.Name}\" needs the creature to summon in the misc value.");
                else CheckCreature(g, N($"EffectMiscValue{e}"));
            }
            if (SpellRules.ObjectEffects.Contains((int)eff) && N($"EffectMiscValue{e}") <= 0) Add(DesignSeverity.Error, g, $"\"{ei.Name}\" needs the game object in the misc value.");
            if (eff == 24)
            {
                if (N($"EffectItemType{e}") <= 0) Add(DesignSeverity.Error, g, "\"Create item\" needs the item to create.");
                else CheckItem(g, N($"EffectItemType{e}"));
            }
            long ta = N($"EffectImplicitTargetA{e}"), tb = N($"EffectImplicitTargetB{e}");
            if (ta == 0 && eff is not (36 or 3 or 77 or 61 or 47 or 60 or 118 or 44 or 25 or 26))
                Add(DesignSeverity.Warning, g, "No target: the effect hits nobody. Choose \"Unit caster\" for yourself or \"Unit enemy\" for your target.");
            if ((SpellRules.AreaTargets.Contains((int)ta) || SpellRules.AreaTargets.Contains((int)tb)) && N($"EffectRadiusIndex{e}") == 0)
                Add(DesignSeverity.Error, g, "An area target needs a radius (in yards).");
            if (N($"EffectRadiusIndex{e}") != 0 && lookup("SpellRadius")?.Find(N($"EffectRadiusIndex{e}")) == null)
                Add(DesignSeverity.Error, g, $"SpellRadius has no row {N($"EffectRadiusIndex{e}")}.");
            if (N($"EffectChainTargets{e}") > 0 && Fl($"EffectChainAmplitude{e}") == 0) Add(DesignSeverity.Note, g, "Chain targets with a chain multiplier of 0: the jumps do no damage (set it to 1 for full value).");
            if (eff is 2 or 9 or 10 && N($"EffectBasePoints{e}") + N($"EffectBaseDice{e}") <= 0 && N($"EffectDieSides{e}") <= 1 && Fl($"EffectRealPointsPerLevel{e}") == 0)
                Add(DesignSeverity.Warning, g, "The value is 0: it does no damage or healing.");
        }
        if (!any) Add(DesignSeverity.Warning, "Effect 1", "The spell has no effect: casting it does nothing.");
        if (Fl("Speed") > 0 && N("SpellVisual") is var vis and > 0 && lookup("SpellVisual")?.Find(vis) is { } vr && lookup("SpellVisual")!.Number(vr, lookup("SpellVisual")!.Layout.Column("HasMissile")!) == 0)
            Add(DesignSeverity.Note, "Missile speed", "The spell has a missile speed but its visual has no missile: the delay happens without a projectile.");

        foreach (var (field, col) in new[] { ("Description", false), ("Buff text", true) })
        {
            var text = SpellText.Describe(t, row, lookup, null, col);
            foreach (var p in text.Problems) Add(DesignSeverity.Warning, field, $"{p.Token}: {p.Message}");
        }
        for (int i = 1; i <= 8; i++)
            if (N($"Reagent{i}") > 0)
            {
                CheckItem($"Reagent {i}", N($"Reagent{i}"));
                if (N($"ReagentCount{i}") <= 0) Add(DesignSeverity.Warning, $"Reagent {i} count", "A reagent with a count of 0 is not used up.");
            }
        if (Session.Server == null) Add(DesignSeverity.Note, "Server", Session.ServerNote + " Items, creatures and the trigger spells on the server were not checked.");
        return res;

        void CheckTrigger(int e, string g, long trig)
        {
            if (trig <= 0) Add(DesignSeverity.Error, g, "This effect needs a trigger spell (the spell it casts or teaches).");
            else if (Spells.Find(trig) == null) Add(DesignSeverity.Error, g, $"The trigger spell {trig} does not exist in Spell.dbc.");
            else if (Session.Server?.Schema.Table("spell_template") is { } st && Session.Server.Object(st, trig) == null && !_touched.Contains(trig))
                Add(DesignSeverity.Error, g, $"The server has no spell {trig} (spell_template): casting this spell would fail there.");
        }

        void CheckItem(string g, long item)
        {
            if (Session.Server is { } ed && !ed.LinkExists("item", item)) Add(DesignSeverity.Error, g, $"The server has no item {item} (item_template).");
        }

        void CheckCreature(string g, long entry)
        {
            if (Session.Server is { } ed && !ed.LinkExists("creature", entry)) Add(DesignSeverity.Error, g, $"The server has no creature {entry} (creature_template).");
        }
    }

    // ------------------------------------------------------------------ learning: trainers

    /// <summary>The "learn" spells that teach a spell (Effect 1 = Learn spell, trigger = the spell): trainers list those.</summary>
    public List<long> LearnSpellsOf(long spell)
    {
        var t = Spells;
        var e1 = t.Layout.Column("Effect1")!;
        var tr = t.Layout.Column("EffectTriggerSpell1")!;
        return t.Rows.Where(r => t.Number(r, e1) == 36 && t.Number(r, tr) == spell).Select(r => t.Key(r)!.Value).OrderBy(x => x).ToList();
    }

    /// <summary>The learn spell of a spell: an existing one (game and server), else a new one with the spell's name, rank
    /// and icon, effect 1 = Learn spell on the target (the server only accepts such spells in trainer lists).</summary>
    public long EnsureLearnSpell(long spell, out bool created)
    {
        var existing = LearnSpellsOf(spell).FirstOrDefault(l => Session.Server?.Schema.Table("spell_template") is not { } st || Session.Server.Object(st, l) != null || _touched.Contains(l));
        if (existing != 0)
        {
            created = false;
            return existing;
        }
        var src = Row(spell);
        var t = Spells;
        long id = Session.FreeCustomId("Spell", "spell_template", "entry");
        var row = Session.AddRow("Spell", id);
        void N(string c, long v) => Session.SetNumber("Spell", row, c, v);
        Session.Set("Spell", row, "Name", t.Text(src, t.Layout.Column("Name")!, 0));
        Session.Set("Spell", row, "NameSubtext", t.Text(src, t.Layout.Column("NameSubtext")!, 0));
        N("SpellIconID", t.Number(src, t.Layout.Column("SpellIconID")!));
        N("CastingTimeIndex", SpellForm.CastTimeRow(Session, 0, out _));
        N("RangeIndex", SpellForm.RangeRow(Session, 0, 0, out _));
        N("SpellLevel", t.Number(src, t.Layout.Column("SpellLevel")!));
        N("BaseLevel", t.Number(src, t.Layout.Column("BaseLevel")!));
        N("EquippedItemClass", -1);
        N("DefenseType", 0);
        N("StanceBarOrder", -1);
        N("Effect1", 36);
        N("EffectImplicitTargetA1", 25);   // Unit: the player learning it
        N("EffectTriggerSpell1", spell);
        N("EffectBaseDice1", 1);
        Session.Set("Spell", row, "Description", t.Text(src, t.Layout.Column("Description")!, 0));
        _extras[id] = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["effectBonusCoefficient1"] = "-1", ["effectBonusCoefficient2"] = "-1", ["effectBonusCoefficient3"] = "-1", ["customFlags"] = "0" };
        _touched.Add(id);
        created = true;
        return id;
    }

    /// <summary>The trainer entries that teach a spell (through any of its learn spells), with your server edits.</summary>
    public List<TrainerEntry> Trainers(long spell)
    {
        var res = new List<TrainerEntry>();
        if (Session.Server is not { } ed) return res;
        var learn = LearnSpellsOf(spell);
        if (learn.Count == 0) return res;
        foreach (var table in new[] { "npc_trainer", "npc_trainer_template" })
        {
            var t = ed.Schema.Table(table);
            if (t == null) continue;
            var rows = ed.Rows(t, RowFilter.Where(new FilterTerm("spell", FilterOp.In, null, learn.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList())));
            foreach (var r in rows)
            {
                long Num(string c) => WorldValue.Long(r[t.IndexOf(c)]);
                long entry = Num("entry");
                var name = table == "npc_trainer" ? ed.Label("creature", entry) ?? $"creature {entry}" : $"trainer list {entry}";
                res.Add(new TrainerEntry(table, t.KeyText(r), entry, name, Num("spell"), spell, Num("reqlevel"), Num("spellcost"), Num("reqskill"), Num("reqskillvalue")));
            }
        }
        return res;
    }

    /// <summary>
    /// Add a spell to a trainer at a level for a price: makes (or reuses) its learn spell and adds the server row. A
    /// creature with a shared trainer list (creature_template.trainer_id) gets the row in that list (npc_trainer_template),
    /// as the server refuses a spell listed in both; <paramref name="trainer"/> can also be "list:N" for a list directly.
    /// </summary>
    public DesignResult AddToTrainer(long spell, string trainer, long level, long cost, long reqSkill = 0, long reqSkillValue = 0)
    {
        if (Session.Server is not { } ed) return DesignResult.Refused("Trainers live in the server's database (npc_trainer), which is not connected. Turn it on in Settings > Server database.");
        Row(spell);
        string table;
        long entry;
        var notes = new List<string>();
        if (trainer.StartsWith("list:", StringComparison.OrdinalIgnoreCase) && long.TryParse(trainer[5..], out var list))
        {
            table = "npc_trainer_template";
            entry = list;
        }
        else if (long.TryParse(trainer, out var creature))
        {
            var ct = ed.Schema.Table("creature_template") ?? throw new DesignException("The server has no creature_template table.");
            var cr = ed.Object(ct, creature);
            if (cr == null) return DesignResult.Refused($"The server has no creature {creature}.");
            long flags = WorldValue.Long(cr[ct.IndexOf("npc_flags")]);
            if ((flags & 0x10) == 0) notes.Add($"{cr[ct.IndexOf("name")]} has no Trainer flag (npc_flags 0x10): the server ignores its trainer rows until it has one.");
            long tid = ct.Has("trainer_id") ? WorldValue.Long(cr[ct.IndexOf("trainer_id")]) : 0;
            if (tid > 0)
            {
                table = "npc_trainer_template";
                entry = tid;
                notes.Add($"{cr[ct.IndexOf("name")]} uses the shared trainer list {tid}: the spell was added to that list (every trainer using it teaches it).");
            }
            else
            {
                table = "npc_trainer";
                entry = creature;
            }
        }
        else return DesignResult.Refused($"\"{trainer}\" is not a trainer: give the creature's entry, or list:N for a shared trainer list.");
        var learn = EnsureLearnSpell(spell, out bool created);
        if (created) notes.Insert(0, $"Made the learn spell {learn} (trainers teach a spell through a \"Learn spell\" spell; the server refuses any other).");
        // the server's row links to the learn spell: both spells go to the server's side first (again on save)
        try { SyncServer(new[] { spell, learn }); }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
        var t = ed.Schema.Table(table) ?? throw new DesignException($"The server has no {table} table.");
        var existing = ed.Rows(t, RowFilter.Where(new FilterTerm("entry", FilterOp.Eq, entry.ToString(CultureInfo.InvariantCulture)), new FilterTerm("spell", FilterOp.Eq, learn.ToString(CultureInfo.InvariantCulture)))).FirstOrDefault();
        var values = new List<(string, string?)>
        {
            ("spellcost", cost.ToString(CultureInfo.InvariantCulture)), ("reqlevel", level.ToString(CultureInfo.InvariantCulture)),
            ("reqskill", reqSkill.ToString(CultureInfo.InvariantCulture)), ("reqskillvalue", reqSkillValue.ToString(CultureInfo.InvariantCulture)),
        };
        ServerEditResult r;
        if (existing != null) r = ed.Set(t.Name, t.KeyText(existing), values, $"Trainer {entry}: {Name(spell)} at level {level}");
        else
        {
            values.InsertRange(0, new[] { ("entry", (string?)entry.ToString(CultureInfo.InvariantCulture)), ("spell", learn.ToString(CultureInfo.InvariantCulture)), ("build_min", "0"), ("build_max", "5875") });
            (_, r) = ed.Add(t.Name, values, label: $"Trainer {entry}: {Name(spell)} at level {level}");
        }
        if (!r.Ok) return DesignResult.Refused(r.Message);
        _touched.Add(spell);
        long spellLevel = Spells.Number(Row(spell), Spells.Layout.Column("SpellLevel")!);
        if (level > 0 && spellLevel > level) notes.Add($"The spell's own level is {spellLevel}; the trainer row says {level} (the trainer row wins).");
        if (SkillLines(spell).Count == 0) notes.Add("The spell has no skill line yet (SkillLineAbility): class trainers show only spells of the player's class skill lines. Add one (\"Skill line\").");
        return DesignResult.Done($"{Name(spell)} is taught by {(table == "npc_trainer" ? ed.Label("creature", entry) ?? $"creature {entry}" : $"trainer list {entry}")} at level {level} for {Money(cost)}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""));
    }

    /// <summary>Remove a spell from a trainer (its learn spells' rows of that creature or list).</summary>
    public DesignResult RemoveFromTrainer(long spell, string trainer)
    {
        if (Session.Server is not { } ed) return DesignResult.Refused("The server database is not connected.");
        var rows = Trainers(spell).Where(x => trainer.StartsWith("list:", StringComparison.OrdinalIgnoreCase) ? x.Table == "npc_trainer_template" && x.Trainer.ToString(CultureInfo.InvariantCulture) == trainer[5..] : x.Trainer.ToString(CultureInfo.InvariantCulture) == trainer).ToList();
        if (rows.Count == 0) return DesignResult.Refused($"{Name(spell)} is not taught by {trainer}.");
        foreach (var g in rows.GroupBy(x => x.Table))
        {
            var r = ed.Delete(g.Key, g.Select(x => x.Key));
            if (!r.Ok) return DesignResult.Refused(r.Message);
        }
        return DesignResult.Done($"{Name(spell)} removed from {trainer} ({rows.Count} row(s)).");
    }

    public static string Money(long copper)
    {
        long g = copper / 10000, s = copper / 100 % 100, c = copper % 100;
        var parts = new List<string>();
        if (g > 0) parts.Add($"{g}g");
        if (s > 0) parts.Add($"{s}s");
        if (c > 0 || parts.Count == 0) parts.Add($"{c}c");
        return string.Join(" ", parts);
    }

    /// <summary>"1g 50s", "150s", "75c" or a plain number of copper.</summary>
    public static long ParseMoney(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;
        long total = 0;
        var ms = Regex.Matches(t, @"(\d+)\s*([gsc])");
        if (ms.Count == 0) throw new DesignException($"\"{text}\" is not a price: copper (150), or 1g 50s 25c.");
        foreach (Match m in ms) total += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value == "g" ? 10000 : m.Groups[2].Value == "s" ? 100 : 1);
        return total;
    }

    // ------------------------------------------------------------------ learning: skill lines (SkillLineAbility)

    public List<SkillLineEntry> SkillLines(long spell)
    {
        var t = Session.Table("SkillLineAbility");
        var sc = t.Layout.Column("Spell")!;
        var sl = Session.Lookup("SkillLine");
        return t.Rows.Where(r => t.Number(r, sc) == spell).Select(r =>
        {
            long N(string c) => t.Number(r, t.Layout.Column(c)!);
            var skill = N("SkillLine");
            var name = sl?.Find(skill) is { } sr ? sl.Label(sr) : $"skill {skill}";
            return new SkillLineEntry(t.Key(r)!.Value, skill, name, spell, N("RaceMask"), N("ClassMask"), N("AcquireMethod"), N("SupercededBySpell"));
        }).ToList();
    }

    /// <summary>
    /// Put a spell into a skill line (a class's talent-tree tab like "Destruction", a profession...) for some classes and
    /// races: a SkillLineAbility row in the game's table and the same row (same ID) in the server's skill_line_ability.
    /// <paramref name="acquire"/>: 0 = taught (trainer), 1 = learned with the skill (automatically).
    /// </summary>
    public DesignResult AddSkillLine(long spell, long skillLine, long classMask, long raceMask = 0, long acquire = 0, long supersededBy = 0)
    {
        Row(spell);
        if (Session.Lookup("SkillLine")?.Find(skillLine) == null) return DesignResult.Refused($"SkillLine has no row {skillLine}.");
        var existing = SkillLines(spell).FirstOrDefault(x => x.SkillLine == skillLine);
        long id = existing?.Id ?? Session.NextId("SkillLineAbility", "skill_line_ability", "id", 65535);
        var row = existing == null ? Session.AddRow("SkillLineAbility", id) : Session.Table("SkillLineAbility").Find(id)!;
        void N(string c, long v) => Session.SetNumber("SkillLineAbility", row, c, v);
        N("SkillLine", skillLine);
        N("Spell", spell);
        N("ClassMask", classMask);
        N("RaceMask", raceMask);
        N("AcquireMethod", acquire);
        N("SupercededBySpell", supersededBy);
        _skillRows.Add(id);
        _touched.Add(spell);
        var name = Session.Lookup("SkillLine")!.Label(Session.Lookup("SkillLine")!.Find(skillLine)!);
        return DesignResult.Done($"{Name(spell)} is in the skill line {name} (SkillLineAbility {id}) for {ClassNames(classMask)}" + (acquire == 1 ? ", learned automatically with the skill." : ", taught by trainers."));
    }

    public DesignResult RemoveSkillLine(long spell, long skillLine)
    {
        var e = SkillLines(spell).FirstOrDefault(x => x.SkillLine == skillLine);
        if (e == null) return DesignResult.Refused($"{Name(spell)} is not in skill line {skillLine}.");
        var t = Session.Table("SkillLineAbility");
        Session.Session("SkillLineAbility").DeleteRows(new[] { t.Find(e.Id)! });
        _skillDeleted.Add(e.Id);
        return DesignResult.Done($"Removed {Name(spell)} from skill line {e.SkillName}.");
    }

    private readonly HashSet<long> _skillRows = new();
    private readonly HashSet<long> _skillDeleted = new();

    /// <summary>Mirror a SkillLineAbility row to the server's skill_line_ability on save (after a class-mask edit).</summary>
    public void SyncSkillRowOnSave(long id) => _skillRows.Add(id);

    public static string ClassNames(long mask)
    {
        if (mask == 0) return "every class";
        var names = DbcLayouts.Default.NamedValues.GetValueOrDefault("ClassMask");
        return names?.Describe(mask) is { Length: > 0 } d ? d : $"0x{mask:X}";
    }

    // ------------------------------------------------------------------ saving

    /// <summary>The server rows of the touched spells and skill-line rows, from the client rows (no save).</summary>
    public List<string> SyncServer(IEnumerable<long>? spells = null)
    {
        var msgs = new List<string>();
        if (Session.Server is not { } ed) return msgs;
        var t = Spells;
        foreach (var id in (spells ?? _touched).Distinct().OrderBy(x => x))
        {
            if (t.Find(id) == null) continue;
            var extra = Extras(id).Select(kv => (kv.Key, kv.Value)).ToList();
            var r = ServerConsistency.SyncFromClient(ed, "spell_template", t, id, extra);
            if (!r.Ok) throw new DesignException($"The server refused spell {id}: {r.Message}");
        }
        if (_skillRows.Count > 0 || _skillDeleted.Count > 0)
        {
            var sla = Session.Table("SkillLineAbility");
            foreach (var id in _skillRows)
                if (sla.Find(id) != null)
                {
                    var r = ServerConsistency.SyncFromClient(ed, "skill_line_ability", sla, id);
                    if (!r.Ok) throw new DesignException($"The server refused skill_line_ability {id}: {r.Message}");
                }
            if (ed.Schema.Table("skill_line_ability") is { } st)
                foreach (var id in _skillDeleted)
                    if (ed.Object(st, id) is { } row)
                        ed.Delete(st.Name, new[] { st.KeyText(row) });
        }
        return msgs;
    }

    /// <summary>Save the spells into the workspace: Spell.dbc and the helper tables, and (when the server database is
    /// connected) spell_template, npc_trainer and skill_line_ability in sync, in one change set. Refused while a spell has
    /// an error (see <see cref="Check"/>).</summary>
    public DesignSaveResult Save(string? label = null, bool force = false)
    {
        if (!force)
        {
            var errors = _touched.Where(Exists).SelectMany(id => Check(id).Where(p => p.IsError).Select(p => $"{Title(id)}: {p.Text}")).ToList();
            if (errors.Count > 0) throw new DesignException($"Not saved: {errors.Count} problem(s) the game or the server cannot handle:\n" + string.Join("\n", errors.Take(10)));
        }
        var names = _touched.Where(Exists).Select(Title).Take(3).ToList();
        var res = Session.Save(label ?? "Spell designer: " + (names.Count == 0 ? "spells" : string.Join(", ", names)) + (_touched.Count > 3 ? $" and {_touched.Count - 3} more" : ""), _ => SyncServer());
        _touched.Clear();
        _skillRows.Clear();
        _skillDeleted.Clear();
        return res;
    }
}
