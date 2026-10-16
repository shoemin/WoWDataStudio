using System.Globalization;
using System.Text.RegularExpressions;
using WoWData.Core.Dbc;

namespace WoWData.Core.Designers;

/// <summary>How a spell field is shown and typed.</summary>
public enum SpellFieldKind
{
    /// <summary>Text (the English text of a localized column).</summary>
    Text,
    /// <summary>A whole number.</summary>
    Number,
    /// <summary>A decimal number.</summary>
    Decimal,
    /// <summary>A time typed in seconds ("2.5", "1500ms"), stored in milliseconds.</summary>
    Seconds,
    /// <summary>One of named values (by name or number).</summary>
    Choice,
    /// <summary>Named bits ("Passive, Is ability" or 0x50).</summary>
    Flags,
    /// <summary>The ID of a row of another game table.</summary>
    Link,
    /// <summary>A cast time in seconds ("instant", "2.5"): a row of SpellCastTimes (found, or added).</summary>
    CastTime,
    /// <summary>A duration in seconds ("until cancelled", "30", "none"): a row of SpellDuration.</summary>
    Duration,
    /// <summary>A range in yards ("self", "melee", "30", "8-30"): a row of SpellRange.</summary>
    Range,
    /// <summary>A radius in yards ("8"): a row of SpellRadius.</summary>
    Radius,
    /// <summary>An icon: its SpellIcon ID, or the icon's file name (Spell_Shadow_ShadowBolt): found, or added.</summary>
    Icon,
    /// <summary>A power cost (rage is typed as the game shows it; the table keeps rage x10).</summary>
    PowerCost,
    /// <summary>A server item entry (reagents, tools, created items).</summary>
    Item,
    /// <summary>The value an effect gives: "10" or "10-20" (base points, base dice and die sides are set for you).</summary>
    EffectValue,
    /// <summary>A server-only column of spell_template (no game counterpart).</summary>
    ServerOnly,
}

/// <summary>One field of the spell designer's form: its group, label, the column(s) it edits and help in plain words.</summary>
public sealed record SpellField(string Id, string Group, string Label, string Column, SpellFieldKind Kind, string Help)
{
    /// <summary>The effect (1-3) of a per-effect field, else 0.</summary>
    public int Effect => Group.StartsWith("Effect ", StringComparison.Ordinal) ? Group[7] - '0' : 0;
}

/// <summary>
/// 0.12.0: the spell designer's form: every field of a spell grouped like a player thinks (basics, casting, targeting,
/// the three effects, mechanics and flags, procs, duration and stacks, visuals, server-only), with friendly units. The
/// same list drives the app's form and <c>wds spells set &lt;id&gt; &lt;field&gt; &lt;value&gt;</c> / <c>wds spells fields</c>.
/// Friendly values are converted to what the tables keep: seconds to milliseconds, yards to a SpellRange / SpellRadius
/// row, a cast time or duration to a SpellCastTimes / SpellDuration row (an existing row with exactly that value, else a
/// new one), an icon file name to a SpellIcon row, rage to rage x10, "10-20" to base points / dice.
/// </summary>
public static class SpellForm
{
    public static readonly string[] Groups = { "Basics", "Casting", "Targeting", "Effect 1", "Effect 2", "Effect 3", "Mechanics and flags", "Procs", "Duration and stacks", "Visuals", "Server only" };

    private static IEnumerable<SpellField> Build()
    {
        SpellField F(string id, string g, string l, string c, SpellFieldKind k, string h) => new(id, g, l, c, k, h);
        yield return F("name", "Basics", "Name", "Name", SpellFieldKind.Text, "The spell's name in the spell book, on the cast bar and in tooltips.");
        yield return F("rank", "Basics", "Rank", "NameSubtext", SpellFieldKind.Text, "The text under the name (\"Rank 1\"); empty for spells without ranks.");
        yield return F("description", "Basics", "Description", "Description", SpellFieldKind.Text, "The spell book tooltip. $s1 = effect 1's value, $d = duration, $o1 = total over the duration, $t1 = tick time, $a1 = radius (the preview shows the result).");
        yield return F("aura-text", "Basics", "Buff text", "AuraDescription", SpellFieldKind.Text, "The tooltip of the buff or debuff the spell puts on its target (the same $ variables).");
        yield return F("icon", "Basics", "Icon", "SpellIconID", SpellFieldKind.Icon, "The icon: a SpellIcon ID, or the file name of an icon in Interface\\Icons (Spell_Shadow_ShadowBolt).");
        yield return F("active-icon", "Basics", "Icon while active", "ActiveIconID", SpellFieldKind.Icon, "The icon while a form or stance is active (0 = the normal icon).");
        yield return F("school", "Basics", "School", "School", SpellFieldKind.Choice, "Physical, Holy, Fire, Nature, Frost, Shadow or Arcane: which resistance and immunity applies.");
        yield return F("level", "Basics", "Spell level", "SpellLevel", SpellFieldKind.Number, "The level the spell is for (trainers teach it from this level unless the trainer row says otherwise).");
        yield return F("base-level", "Basics", "Base level", "BaseLevel", SpellFieldKind.Number, "The level from which \"per level\" values grow.");
        yield return F("max-level", "Basics", "Max level", "MaxLevel", SpellFieldKind.Number, "The level at which \"per level\" values stop growing (0 = no limit).");
        yield return F("family", "Basics", "Class (spell family)", "SpellClassSet", SpellFieldKind.Choice, "The class the spell belongs to; talents of that class can only change spells of their family.");
        yield return F("family-mask", "Basics", "Family mask (low)", "SpellClassMask1", SpellFieldKind.Flags, "Which talents and set bonuses of the class change this spell (bits shared with them; the server keeps 64 bits).");
        yield return F("family-mask-high", "Basics", "Family mask (high)", "SpellClassMask2", SpellFieldKind.Flags, "The upper 32 bits of the family mask.");

        yield return F("cast-time", "Casting", "Cast time", "CastingTimeIndex", SpellFieldKind.CastTime, "Seconds (\"instant\", \"1.5\", \"3\"). Uses an existing SpellCastTimes row with that time, or adds one.");
        yield return F("cooldown", "Casting", "Cooldown", "RecoveryTime", SpellFieldKind.Seconds, "Seconds before the spell can be cast again (0 = none).");
        yield return F("category", "Casting", "Cooldown category", "Category", SpellFieldKind.Link, "Spells of the same SpellCategory share a cooldown (0 = none).");
        yield return F("category-cooldown", "Casting", "Category cooldown", "CategoryRecoveryTime", SpellFieldKind.Seconds, "Seconds of the shared category cooldown.");
        yield return F("gcd", "Casting", "Global cooldown", "StartRecoveryTime", SpellFieldKind.Seconds, "The global cooldown it starts (1.5 for most spells, 1 for rogue abilities, 0 = none).");
        yield return F("gcd-category", "Casting", "Global cooldown category", "StartRecoveryCategory", SpellFieldKind.Number, "133 for spells on the global cooldown, 0 for spells off it.");
        yield return F("range", "Casting", "Range", "RangeIndex", SpellFieldKind.Range, "Yards: \"self\", \"melee\", \"30\" or \"8-30\" (min-max). Uses an existing SpellRange row, or adds one.");
        yield return F("power-type", "Casting", "Power type", "PowerType", SpellFieldKind.Choice, "Mana, Rage, Focus, Energy, Happiness or Health.");
        yield return F("cost", "Casting", "Cost", "ManaCost", SpellFieldKind.PowerCost, "The power it costs (rage as the game shows it: 15 rage).");
        yield return F("cost-per-level", "Casting", "Cost per level", "ManaCostPerLevel", SpellFieldKind.Number, "Extra cost per caster level.");
        yield return F("cost-percent", "Casting", "Cost (percent of base mana)", "ManaCostPct", SpellFieldKind.Number, "Cost in percent of the caster's base mana (used instead of the fixed cost when set).");
        yield return F("cost-per-second", "Casting", "Cost per second", "ManaPerSecond", SpellFieldKind.Number, "Power per second while channelling or while the aura lasts.");
        yield return F("speed", "Casting", "Missile speed", "Speed", SpellFieldKind.Decimal, "Yards per second of the projectile (0 = hits at once).");
        for (int i = 1; i <= 8; i++)
        {
            yield return F($"reagent{i}", "Casting", $"Reagent {i}", $"Reagent{i}", SpellFieldKind.Item, "An item used up by the spell (the server's item entry; 0 = none).");
            yield return F($"reagent{i}-count", "Casting", $"Reagent {i} count", $"ReagentCount{i}", SpellFieldKind.Number, "How many of the reagent.");
        }
        yield return F("tool1", "Casting", "Tool 1", "Totem1", SpellFieldKind.Item, "An item that must be in the bags (not used up).");
        yield return F("tool2", "Casting", "Tool 2", "Totem2", SpellFieldKind.Item, "A second tool.");
        yield return F("interrupt", "Casting", "Interrupted by", "InterruptFlags", SpellFieldKind.Flags, "What interrupts or delays the cast (movement, damage pushback...).");
        yield return F("channel-interrupt", "Casting", "Channel interrupted by", "ChannelInterruptFlags", SpellFieldKind.Flags, "What stops a channelled spell.");
        yield return F("focus", "Casting", "Needs a spell focus", "RequiresSpellFocus", SpellFieldKind.Link, "An object that must be near (Anvil, Forge, Campfire...).");
        yield return F("equipped-item-class", "Casting", "Needs an equipped item class", "EquippedItemClass", SpellFieldKind.Choice, "-1 = none; Weapon or Armor with the subclass mask below.");
        yield return F("equipped-subclass-mask", "Casting", "Equipped item subclasses", "EquippedItemSubClassMask", SpellFieldKind.Flags, "Bit n = item subclass n of that class.");

        yield return F("targets", "Targeting", "Target flags", "Targets", SpellFieldKind.Flags, "What the player picks when casting (a unit, a location, an item...). 0 = decided by the effects' targets.");
        yield return F("creature-types", "Targeting", "Creature types", "TargetCreatureType", SpellFieldKind.Flags, "Only these creature types can be targeted (0 = any).");
        yield return F("max-targets", "Targeting", "Max targets", "MaxTargets", SpellFieldKind.Number, "At most this many targets of an area effect (0 = no limit).");
        yield return F("max-target-level", "Targeting", "Max target level", "MaxTargetLevel", SpellFieldKind.Number, "Targets above this level are not affected (0 = any).");
        yield return F("caster-aura-state", "Targeting", "Caster aura state", "CasterAuraState", SpellFieldKind.Choice, "A state the caster must be in (after a dodge, below 20% health...).");
        yield return F("target-aura-state", "Targeting", "Target aura state", "TargetAuraState", SpellFieldKind.Choice, "A state the target must be in.");
        yield return F("stances", "Targeting", "Needs form or stance", "Stances", SpellFieldKind.Flags, "Bit n-1 = SpellShapeshiftForm n (Battle Stance = 0x10000 for form 17...).");
        yield return F("stances-not", "Targeting", "Not in form or stance", "StancesNot", SpellFieldKind.Flags, "Forms in which it cannot be cast.");

        for (int e = 1; e <= 3; e++)
        {
            string g = $"Effect {e}";
            yield return F($"effect{e}", g, "Effect type", $"Effect{e}", SpellFieldKind.Choice, "What this effect does (see the explanation below the choice).");
            yield return F($"effect{e}-aura", g, "Aura type", $"EffectAura{e}", SpellFieldKind.Choice, "For \"Apply aura\" effects: the buff or debuff.");
            yield return F($"effect{e}-value", g, "Value", $"EffectBasePoints{e}", SpellFieldKind.EffectValue, "The amount: \"10\" or a random \"10-20\" (damage, healing, percent...). Sets base points, base dice and die sides.");
            yield return F($"effect{e}-base-points", g, "Base points", $"EffectBasePoints{e}", SpellFieldKind.Number, "Advanced: the stored number (the value minus 1; the game adds the dice).");
            yield return F($"effect{e}-die-sides", g, "Die sides", $"EffectDieSides{e}", SpellFieldKind.Number, "Advanced: the random part.");
            yield return F($"effect{e}-base-dice", g, "Base dice", $"EffectBaseDice{e}", SpellFieldKind.Number, "Advanced: usually 1.");
            yield return F($"effect{e}-dice-per-level", g, "Dice per level", $"EffectDicePerLevel{e}", SpellFieldKind.Number, "Advanced: extra dice per level.");
            yield return F($"effect{e}-per-level", g, "Value per level", $"EffectRealPointsPerLevel{e}", SpellFieldKind.Decimal, "Extra value per caster level above the base level.");
            yield return F($"effect{e}-combo", g, "Value per combo point", $"EffectPointsPerCombo{e}", SpellFieldKind.Decimal, "Extra value per combo point (finishing moves).");
            yield return F($"effect{e}-target-a", g, "Target", $"EffectImplicitTargetA{e}", SpellFieldKind.Choice, "Who the effect hits (Unit caster = yourself, Unit enemy = your target, area targets need a radius).");
            yield return F($"effect{e}-target-b", g, "Second target", $"EffectImplicitTargetB{e}", SpellFieldKind.Choice, "Usually an area around the first target (with a radius).");
            yield return F($"effect{e}-radius", g, "Radius", $"EffectRadiusIndex{e}", SpellFieldKind.Radius, "Yards of an area effect (\"8\"; \"none\" = 0). Uses an existing SpellRadius row, or adds one.");
            yield return F($"effect{e}-period", g, "Tick every", $"EffectAuraPeriod{e}", SpellFieldKind.Seconds, "Seconds between the ticks of a periodic aura (damage or healing over time).");
            yield return F($"effect{e}-chain-targets", g, "Chain targets", $"EffectChainTargets{e}", SpellFieldKind.Number, "How many targets it jumps to (Chain Lightning: 3).");
            yield return F($"effect{e}-chain-amplitude", g, "Chain multiplier", $"EffectChainAmplitude{e}", SpellFieldKind.Decimal, "The value of each jump compared with the last (0.7 = 70%).");
            yield return F($"effect{e}-misc", g, "Misc value", $"EffectMiscValue{e}", SpellFieldKind.Number, "Depends on the effect or aura (the explanation below says what).");
            yield return F($"effect{e}-trigger", g, "Trigger spell", $"EffectTriggerSpell{e}", SpellFieldKind.Link, "The spell this effect casts, teaches or triggers.");
            yield return F($"effect{e}-item", g, "Item created", $"EffectItemType{e}", SpellFieldKind.Item, "The item a \"Create item\" effect makes (server item entry).");
            yield return F($"effect{e}-mechanic", g, "Mechanic", $"EffectMechanic{e}", SpellFieldKind.Link, "The mechanic of this effect alone (Stun, Root...).");
            yield return F($"effect{e}-multiple-value", g, "Multiple value", $"EffectMultipleValue{e}", SpellFieldKind.Decimal, "A multiplier some effects use (mana drained into health...).");
        }

        yield return F("mechanic", "Mechanics and flags", "Mechanic", "Mechanic", SpellFieldKind.Link, "Stun, Root, Fear, Polymorph...: immunities and diminishing returns use it.");
        yield return F("dispel", "Mechanics and flags", "Dispel type", "Dispel", SpellFieldKind.Link, "Magic, Curse, Disease, Poison: what can remove the aura.");
        yield return F("defense-type", "Mechanics and flags", "Defense type", "DefenseType", SpellFieldKind.Choice, "Magic, Melee or Ranged: how the hit is rolled (resists or dodge / parry / block).");
        yield return F("prevention", "Mechanics and flags", "Prevented by", "PreventionType", SpellFieldKind.Choice, "Silence or Pacify stops it.");
        yield return F("attributes", "Mechanics and flags", "Attributes", "Attributes", SpellFieldKind.Flags, "Passive, Is ability, On next swing, Only outdoors...");
        yield return F("attributes-ex", "Mechanics and flags", "Attributes (2)", "AttributesEx", SpellFieldKind.Flags, "Is channeled, No threat, Aura unique...");
        yield return F("attributes-ex2", "Mechanics and flags", "Attributes (3)", "AttributesEx2", SpellFieldKind.Flags, "Auto repeat, Ignore line of sight...");
        yield return F("attributes-ex3", "Mechanics and flags", "Attributes (4)", "AttributesEx3", SpellFieldKind.Flags, "Requires main hand weapon...");
        yield return F("attributes-ex4", "Mechanics and flags", "Attributes (5)", "AttributesEx4", SpellFieldKind.Flags, "Ignore resistances...");
        yield return F("aura-interrupt", "Mechanics and flags", "Aura removed by", "AuraInterruptFlags", SpellFieldKind.Flags, "What removes the aura (taking damage, moving, attacking...).");
        yield return F("priority", "Mechanics and flags", "Spell priority", "SpellPriority", SpellFieldKind.Number, "Which of two auras wins.");
        yield return F("min-faction", "Mechanics and flags", "Needs reputation with", "MinFactionID", SpellFieldKind.Link, "A faction whose reputation the caster needs.");
        yield return F("min-reputation", "Mechanics and flags", "Reputation needed", "MinReputation", SpellFieldKind.Choice, "The rank needed with that faction.");

        yield return F("proc-flags", "Procs", "Proc on", "ProcFlags", SpellFieldKind.Flags, "The events that fire a proc aura (dealing a melee swing, taking damage...).");
        yield return F("proc-chance", "Procs", "Proc chance", "ProcChance", SpellFieldKind.Number, "Chance in percent (101 = always).");
        yield return F("proc-charges", "Procs", "Proc charges", "ProcCharges", SpellFieldKind.Number, "How many procs before the aura ends (0 = no limit).");

        yield return F("duration", "Duration and stacks", "Duration", "DurationIndex", SpellFieldKind.Duration, "Seconds the aura lasts (\"until cancelled\", \"none\" = an instant spell). Uses an existing SpellDuration row, or adds one.");
        yield return F("stack", "Duration and stacks", "Stacks up to", "StackAmount", SpellFieldKind.Number, "How many times the aura stacks (0 or 1 = no stacking).");

        yield return F("visual", "Visuals", "Spell visual", "SpellVisual", SpellFieldKind.Link, "The visual effects (precast, cast, missile, impact, channel). \"Pick visual...\" and \"wds spells visual\" compose one from other spells.");

        for (int e = 1; e <= 3; e++)
            yield return F($"server-bonus{e}", "Server only", $"Spell power coefficient {e}", $"effectBonusCoefficient{e}", SpellFieldKind.ServerOnly, "VMaNGOS only: how much spell power adds to effect " + e + " (-1 = the server's own formula).");
        yield return F("server-custom-flags", "Server only", "VMaNGOS custom flags", "customFlags", SpellFieldKind.ServerOnly, "VMaNGOS only: extra handling of the spell on the server.");
    }

    public static readonly IReadOnlyList<SpellField> Fields = Build().ToList();

    public static SpellField? Find(string id) => Fields.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Fields.FirstOrDefault(f => f.Kind != SpellFieldKind.EffectValue && string.Equals(f.Column, id, StringComparison.OrdinalIgnoreCase));

    public static SpellField Require(string id) => Find(id) ?? throw new DesignException($"There is no spell field \"{id}\". The fields: {string.Join(", ", Fields.Select(f => f.Id))} (or a column of Spell.dbc).");

    // ------------------------------------------------------------------ showing

    /// <summary>The value of a field as the form shows it ("2.5 sec", "30 yd (row 5)", "Shadow", "until cancelled").</summary>
    public static string Show(DesignSession s, DbcRow row, SpellField f, IReadOnlyDictionary<string, string?>? serverExtras = null)
    {
        var t = s.Table("Spell");
        if (f.Kind == SpellFieldKind.ServerOnly)
            return serverExtras != null && serverExtras.TryGetValue(f.Column, out var v) ? v ?? "" : "(the server's value)";
        var c = t.Layout.Column(f.Column)!;
        long n = c.IsNumber ? t.Number(row, c) : 0;
        switch (f.Kind)
        {
            case SpellFieldKind.Text: return t.Text(row, c, 0);
            case SpellFieldKind.Seconds: return Seconds(n);
            case SpellFieldKind.CastTime:
            {
                var ct = s.Lookup("SpellCastTimes")?.Find(n);
                if (ct == null) return $"(row {n} is missing)";
                long ms = s.Lookup("SpellCastTimes")!.Number(ct, s.Lookup("SpellCastTimes")!.Layout.Column("Base")!);
                return (ms <= 0 ? "instant" : Seconds(ms)) + $" (row {n})";
            }
            case SpellFieldKind.Duration:
            {
                if (n == 0) return "none";
                var dt = s.Lookup("SpellDuration");
                var dr = dt?.Find(n);
                if (dr == null) return $"(row {n} is missing)";
                long ms = dt!.Number(dr, dt.Layout.Column("Duration")!);
                return (ms < 0 ? "until cancelled" : Seconds(ms)) + $" (row {n})";
            }
            case SpellFieldKind.Range:
            {
                var rt = s.Lookup("SpellRange");
                var rr = rt?.Find(n);
                if (rr == null) return $"(row {n} is missing)";
                float a = rt!.Float(rr, rt.Layout.Column("RangeMin")!), b = rt.Float(rr, rt.Layout.Column("RangeMax")!);
                var label = rt.Text(rr, rt.Layout.Column("DisplayName")!, 0);
                return (b == 0 ? "self" : a > 0 ? $"{Yd(a)}-{Yd(b)} yd" : $"{Yd(b)} yd") + (label.Length > 0 ? $" \"{label}\"" : "") + $" (row {n})";
            }
            case SpellFieldKind.Radius:
            {
                if (n == 0) return "none";
                var rt = s.Lookup("SpellRadius");
                var rr = rt?.Find(n);
                return rr == null ? $"(row {n} is missing)" : $"{Yd(rt!.Float(rr, rt.Layout.Column("Radius")!))} yd (row {n})";
            }
            case SpellFieldKind.Icon:
            {
                if (n == 0) return "none";
                var it = s.Lookup("SpellIcon");
                var ir = it?.Find(n);
                return ir == null ? $"{n} (missing)" : $"{n} · {IconName(it!.Text(ir, it.Layout.Column("TextureFilename")!))}";
            }
            case SpellFieldKind.PowerCost:
                return t.Number(row, t.Layout.Column("PowerType")!) == 1 ? (n / 10.0).ToString("0.#", CultureInfo.InvariantCulture) + " rage" : n.ToString(CultureInfo.InvariantCulture);
            case SpellFieldKind.EffectValue:
            {
                var (min, max) = SpellText.PointsRange(t, row, f.Effect - 1, (int)Math.Max(1, t.Number(row, t.Layout.Column("BaseLevel")!)));
                return Math.Abs(min - max) < 1e-9 ? Num(min) : $"{Num(min)}-{Num(max)}";
            }
            case SpellFieldKind.Decimal: return t.Format(row, c, 0);
            default: return s.Session("Spell").CellText(row, c, false);
        }
    }

    public static string IconName(string path)
    {
        var p = path.Replace('/', '\\');
        int i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    private static string Num(double v) => Math.Abs(v - Math.Round(v)) < 1e-6 ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Yd(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Seconds(long ms) => ms == 0 ? "0" : (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " sec";

    // ------------------------------------------------------------------ setting

    /// <summary>Parse "2.5", "2.5 sec", "2.5s", "1500ms", "2 min" into milliseconds.</summary>
    public static long ParseMs(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(',', '.');
        var m = Regex.Match(t, @"^(-?[\d.]+)\s*(ms|s|sec|secs|seconds?|m|min|mins|minutes?|h|hours?|hrs)?$");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new DesignException($"\"{text}\" is not a time: type seconds (2.5), or 1500ms, or 2 min.");
        double ms = m.Groups[2].Value switch
        {
            "ms" => v,
            "m" or "min" or "mins" or "minute" or "minutes" => v * 60000,
            "h" or "hour" or "hours" or "hrs" => v * 3600000,
            _ => v * 1000,
        };
        return (long)Math.Round(ms, MidpointRounding.AwayFromZero);
    }

    private static float ParseYards(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace("yards", "").Replace("yard", "").Replace("yd", "").Trim().Replace(',', '.');
        if (!float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) throw new DesignException($"\"{text}\" is not a distance in yards.");
        return v;
    }

    /// <summary>A "#5" or "row 5" text: the row ID given directly.</summary>
    private static long? RowRef(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(?:#|row\s+)(\d+)$", RegexOptions.IgnoreCase);
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Flags typed as names ("Passive, Is ability"), with "+" / "-" to add or remove, or as a number.</summary>
    public static string ParseFlags(DbcColumn c, long current, string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return "0";
        if (Regex.IsMatch(t, @"^(0x[0-9a-fA-F]+|-?\d+)$")) return t;
        var values = c.Values;
        if (values == null) throw new DesignException($"{c.Name} has no named bits: type a number (0x10).");
        long v = 0;
        bool relative = t.StartsWith('+') || t.StartsWith('-');
        if (relative) v = current;
        foreach (var raw in Regex.Split(t, @"\s*[,|;]\s*").Where(x => x.Length > 0))
        {
            var part = raw.Trim();
            bool remove = part.StartsWith('-');
            if (part.StartsWith('+') || part.StartsWith('-')) part = part[1..].Trim();
            var hit = values.Values.FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            long bit;
            if (hit.Name != null) bit = hit.Value;
            else if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(part[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) bit = h;
            else throw new DesignException($"\"{part}\" is not a bit of {values.Name}. Its bits: {string.Join(", ", values.Values.Where(x => x.Value != 0).Select(x => x.Name))}.");
            v = remove ? v & ~bit : v | bit;
        }
        return "0x" + (v & 0xFFFFFFFF).ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>Set a field from friendly text. Returns a note ("" when nothing to say). Throws DesignException when refused.</summary>
    public static string Set(DesignSession s, DbcRow row, SpellField f, string text, IDictionary<string, string?> serverExtras)
    {
        var t = s.Table("Spell");
        string note = "";
        switch (f.Kind)
        {
            case SpellFieldKind.ServerOnly:
            {
                var v = text.Trim();
                if (!double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    throw new DesignException($"{f.Label} must be a number.");
                serverExtras[f.Column] = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? long.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : v.Replace(',', '.');
                return s.HasServer ? "" : "The server database is not connected: this value is kept for when the spell is synced to the server (wds spells sync).";
            }
            case SpellFieldKind.Seconds:
                s.SetNumber("Spell", row, f.Column, ParseMs(text));
                return "";
            case SpellFieldKind.CastTime:
            {
                long id = RowRef(text) ?? CastTimeRow(s, text.Trim().Equals("instant", StringComparison.OrdinalIgnoreCase) ? 0 : ParseMs(text), out note);
                s.SetNumber("Spell", row, f.Column, id);
                return note;
            }
            case SpellFieldKind.Duration:
            {
                var x = text.Trim().ToLowerInvariant();
                long id = RowRef(text) ?? (x is "none" or "0" ? 0 : DurationRow(s, x is "until cancelled" or "forever" or "-1" ? -1 : ParseMs(text), out note));
                s.SetNumber("Spell", row, f.Column, id);
                return note;
            }
            case SpellFieldKind.Range:
            {
                var x = text.Trim().ToLowerInvariant();
                long id;
                if (RowRef(text) is { } rr) id = rr;
                else if (x is "self" or "self only" or "0") id = RangeRow(s, 0, 0, out note);
                else if (x is "melee" or "combat") id = RangeRow(s, 0, 5, out note);
                else
                {
                    var m = Regex.Match(x, @"^([\d.,]+)\s*(?:-|\.\.|to)\s*([\d.,]+)");
                    id = m.Success ? RangeRow(s, ParseYards(m.Groups[1].Value), ParseYards(m.Groups[2].Value), out note) : RangeRow(s, 0, ParseYards(x), out note);
                }
                s.SetNumber("Spell", row, f.Column, id);
                return note;
            }
            case SpellFieldKind.Radius:
            {
                var x = text.Trim().ToLowerInvariant();
                long id = RowRef(text) ?? (x is "none" or "0" ? 0 : RadiusRow(s, ParseYards(x), out note));
                s.SetNumber("Spell", row, f.Column, id);
                return note;
            }
            case SpellFieldKind.Icon:
            {
                long id = long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : IconRow(s, text, out note);
                if (id != 0 && s.Lookup("SpellIcon")?.Find(id) == null) throw new DesignException($"SpellIcon has no row {id}. Type an icon's file name (Spell_Shadow_ShadowBolt) to find or add it.");
                s.SetNumber("Spell", row, f.Column, id);
                return note;
            }
            case SpellFieldKind.PowerCost:
            {
                if (!double.TryParse(text.Trim().Replace(',', '.').Replace("rage", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) throw new DesignException($"\"{text}\" is not a cost.");
                bool rage = t.Number(row, t.Layout.Column("PowerType")!) == 1;
                s.SetNumber("Spell", row, f.Column, (long)Math.Round(rage ? v * 10 : v));
                return rage ? $"Rage costs are kept x10 in the table: {v} rage = {(long)Math.Round(v * 10)}." : "";
            }
            case SpellFieldKind.EffectValue:
            {
                var m = Regex.Match(text.Trim(), @"^(-?\d+)\s*(?:(?:-|\.\.|to)\s*(-?\d+))?$");
                if (!m.Success) throw new DesignException($"\"{text}\" is not a value: type a whole number (10) or a range (10-20).");
                long min = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                long max = m.Groups[2].Success ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : min;
                if (max < min) (min, max) = (max, min);
                int e = f.Effect;
                // value = BasePoints + BaseDice .. BasePoints + DieSides x BaseDice (BaseDice 1): min - 1 and max - min + 1
                s.SetNumber("Spell", row, $"EffectBaseDice{e}", 1);
                s.SetNumber("Spell", row, $"EffectBasePoints{e}", min - 1);
                s.SetNumber("Spell", row, $"EffectDieSides{e}", max - min + 1);
                s.SetNumber("Spell", row, $"EffectDicePerLevel{e}", 0);
                return "";
            }
            case SpellFieldKind.Flags:
            {
                var c = t.Layout.Column(f.Column)!;
                s.Set("Spell", row, f.Column, ParseFlags(c, t.Number(row, c), text));
                return "";
            }
            default:
                s.Set("Spell", row, f.Column, text.Trim());
                return "";
        }
    }

    // ------------------------------------------------------------------ the helper tables: find a row with the value, or add one

    private static long FindOrAdd(DesignSession s, string table, Func<DbcTable, DbcRow, bool> match, Action<DbcRow> fill, string what, out string note)
    {
        var t = s.Table(table);
        var hit = t.Rows.Where(r => t.Key(r) > 0 && match(t, r)).OrderBy(r => t.Key(r)).FirstOrDefault();
        if (hit != null)
        {
            note = "";
            return t.Key(hit)!.Value;
        }
        var row = s.AddRow(table);
        fill(row);
        long id = t.Key(row)!.Value;
        note = $"Added {table} row {id} ({what})." + (t.Layout.ServerUse == DbcServerUse.ServerFile ? $" The server reads its own {table}.dbc: copy it to the server after testing (\"Copy tables to the server...\")." : "");
        return id;
    }

    public static long CastTimeRow(DesignSession s, long ms, out string note) => FindOrAdd(s, "SpellCastTimes",
        (t, r) => t.Number(r, t.Layout.Column("Base")!) == ms && t.Number(r, t.Layout.Column("PerLevel")!) == 0 && t.Number(r, t.Layout.Column("Minimum")!) == ms,
        r =>
        {
            s.SetNumber("SpellCastTimes", r, "Base", ms);
            s.SetNumber("SpellCastTimes", r, "PerLevel", 0);
            s.SetNumber("SpellCastTimes", r, "Minimum", ms);
        }, $"a cast time of {Seconds(ms)}", out note);

    public static long DurationRow(DesignSession s, long ms, out string note) => FindOrAdd(s, "SpellDuration",
        (t, r) => t.Number(r, t.Layout.Column("Duration")!) == ms && t.Number(r, t.Layout.Column("DurationPerLevel")!) == 0 && t.Number(r, t.Layout.Column("MaxDuration")!) == ms,
        r =>
        {
            s.SetNumber("SpellDuration", r, "Duration", ms);
            s.SetNumber("SpellDuration", r, "DurationPerLevel", 0);
            s.SetNumber("SpellDuration", r, "MaxDuration", ms);
        }, ms < 0 ? "until cancelled" : $"a duration of {Seconds(ms)}", out note);

    public static long RangeRow(DesignSession s, float min, float max, out string note) => FindOrAdd(s, "SpellRange",
        (t, r) => t.Float(r, t.Layout.Column("RangeMin")!) == min && t.Float(r, t.Layout.Column("RangeMax")!) == max && (max > 5 || t.Number(r, t.Layout.Column("Flags")!) == (max == 5 && min == 0 ? 1 : 0)),
        r =>
        {
            s.SetFloat("SpellRange", r, "RangeMin", min);
            s.SetFloat("SpellRange", r, "RangeMax", max);
            s.SetNumber("SpellRange", r, "Flags", max == 5 && min == 0 ? 1 : 0);
            var t = s.Table("SpellRange");
            var name = max == 0 ? "Self Only" : min > 0 ? $"{Yd(min)}-{Yd(max)} yards" : $"{Yd(max)} yards";
            var shortName = max == 0 ? "Self" : $"{Yd(max)} yd";
            s.Set("SpellRange", r, "DisplayName", name);
            s.Set("SpellRange", r, "DisplayNameShort", shortName);
        }, max == 0 ? "self" : $"{(min > 0 ? Yd(min) + "-" : "")}{Yd(max)} yards", out note);

    public static long RadiusRow(DesignSession s, float yards, out string note) => FindOrAdd(s, "SpellRadius",
        (t, r) => t.Float(r, t.Layout.Column("Radius")!) == yards && t.Float(r, t.Layout.Column("RadiusPerLevel")!) == 0,
        r =>
        {
            s.SetFloat("SpellRadius", r, "Radius", yards);
            s.SetFloat("SpellRadius", r, "RadiusPerLevel", 0);
            s.SetFloat("SpellRadius", r, "RadiusMax", yards);
        }, $"a radius of {Yd(yards)} yards", out note);

    /// <summary>The SpellIcon row of an icon file ("Spell_Shadow_ShadowBolt" or "Interface\Icons\..."), added when the
    /// game (or your workspace) has the file and no row names it yet.</summary>
    public static long IconRow(DesignSession s, string text, out string note)
    {
        var name = text.Trim().Replace('/', '\\');
        if (name.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var path = name.Contains('\\') ? name : "Interface\\Icons\\" + name;
        bool exists = s.Dbc.Data.Exists(path + ".blp") || s.Dbc.Workspace?.IsModified(path + ".blp") == true;
        if (!exists) throw new DesignException($"There is no icon file {path}.blp in the game or your workspace. \"wds spells icons <words>\" lists the icons.");
        return FindOrAdd(s, "SpellIcon",
            (t, r) => string.Equals(t.Text(r, t.Layout.Column("TextureFilename")!, 0).Replace('/', '\\'), path, StringComparison.OrdinalIgnoreCase),
            r => s.Set("SpellIcon", r, "TextureFilename", path), $"the icon {IconName(path)}", out note);
    }
}
