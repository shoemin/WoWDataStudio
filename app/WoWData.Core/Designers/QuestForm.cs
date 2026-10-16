using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>What kind of value a quest field holds (how it is shown and typed).</summary>
public enum QuestFieldKind
{
    /// <summary>One line of text.</summary>
    Line,
    /// <summary>A longer text with $N / $C / $R tokens.</summary>
    Text,
    Number,
    /// <summary>Copper, shown and typed as 1g 50s 25c.</summary>
    Money,
    Seconds,
    /// <summary>Classes by name ("Warrior, Paladin", "any").</summary>
    ClassMask,
    /// <summary>Races by name ("Alliance", "Horde", "Orc, Troll", "any").</summary>
    RaceMask,
    /// <summary>Named bits ("+Sharable").</summary>
    Flags,
    /// <summary>One of named values.</summary>
    Choice,
    /// <summary>An ID of another object (item, creature, spell, faction, quest...).</summary>
    Link,
    /// <summary>The quest log header: a zone, or a category (QuestSort) given as "sort:Warrior".</summary>
    Category,
    /// <summary>A creature or a game object to kill / use ("creature:3098", "object:1618", a negative ID = object).</summary>
    Target,
}

/// <summary>One field of the quest editor: its id (for the command line), label, group, the column of quest_template it
/// edits, its kind and a help text.</summary>
public sealed record QuestField(string Id, string Label, string Group, string Column, QuestFieldKind Kind, string Help, string? Link = null, string? Named = null)
{
    public bool IsText => Kind is QuestFieldKind.Line or QuestFieldKind.Text;
}

/// <summary>
/// 0.13.0: the quest editor's fields, grouped like a quest designer thinks (texts, levels, who may take it, the chain, the
/// objectives, the rewards), each mapped to a column of VMaNGOS's quest_template, with friendly values: class and race
/// names, money as 1g 50s, a creature or object target, the quest log category.
/// </summary>
public static class QuestForm
{
    public const string AllianceText = "Alliance";
    public const string HordeText = "Horde";
    /// <summary>The Alliance's races (Human, Dwarf, Night Elf, Gnome) and the Horde's (Orc, Undead, Tauren, Troll).</summary>
    public const long AllianceRaces = 0x1 | 0x4 | 0x8 | 0x40;
    public const long HordeRaces = 0x2 | 0x10 | 0x20 | 0x80;
    public const long AllRaces = AllianceRaces | HordeRaces;

    public static readonly IReadOnlyList<string> Groups = new[] { "Texts", "Levels", "Who can take it", "Quest log", "Chain", "Start", "Objectives", "Rewards" };

    public static readonly IReadOnlyList<QuestField> Fields = Build();

    private static List<QuestField> Build()
    {
        var f = new List<QuestField>
        {
            new("title", "Title", "Texts", "Title", QuestFieldKind.Line, "The quest's name in the quest log and the windows (the window shows 63 characters)."),
            new("details", "Details (when offered)", "Texts", "Details", QuestFieldKind.Text, "What the quest giver says when offering the quest. $N name, $C class, $R race, $G he:she; and $B new line."),
            new("objectives", "Objectives (quest log)", "Texts", "Objectives", QuestFieldKind.Text, "The short summary of what to do, in the quest log."),
            new("progress-text", "Progress text (not done yet)", "Texts", "RequestItemsText", QuestFieldKind.Text, "What the NPC says when you come back before you are done (or with the items to hand in)."),
            new("completion-text", "Completion text (reward)", "Texts", "OfferRewardText", QuestFieldKind.Text, "What the NPC says when you hand the quest in."),
            new("end-text", "End text", "Texts", "EndText", QuestFieldKind.Line, "An extra line under the objectives (rarely used)."),
        };
        for (int k = 1; k <= 4; k++)
            f.Add(new($"objective-text{k}", $"Objective {k} text", "Texts", $"ObjectiveText{k}", QuestFieldKind.Line, $"Text of objective {k} in the log instead of \"<creature> slain\" (\"Speak with Thork\")."));
        f.AddRange(new QuestField[]
        {
            new("level", "Quest level", "Levels", "QuestLevel", QuestFieldKind.Number, "The quest's level: its colour in the log and the experience it gives."),
            new("min-level", "Minimum level", "Levels", "MinLevel", QuestFieldKind.Number, "The lowest player level that can take it."),
            new("max-level", "Maximum level", "Levels", "MaxLevel", QuestFieldKind.Number, "The highest player level that can take it (0 = no limit)."),
            new("classes", "Classes", "Who can take it", "RequiredClasses", QuestFieldKind.ClassMask, "The classes that can take it: names (\"Warrior\", \"Warrior, Paladin\") or \"any\"."),
            new("races", "Races", "Who can take it", "RequiredRaces", QuestFieldKind.RaceMask, "The races: \"Alliance\", \"Horde\", names (\"Orc, Troll\") or \"any\"."),
            new("min-rep-faction", "Needs reputation with", "Who can take it", "RequiredMinRepFaction", QuestFieldKind.Link, "A faction the player needs reputation with (0 = none).", "faction"),
            new("min-rep-value", "Needs at least (reputation)", "Who can take it", "RequiredMinRepValue", QuestFieldKind.Number, "The reputation needed: 3000 friendly, 9000 honored, 21000 revered, 42000 exalted."),
            new("max-rep-faction", "Must stay below reputation with", "Who can take it", "RequiredMaxRepFaction", QuestFieldKind.Link, "A faction whose reputation must stay below a value (0 = none).", "faction"),
            new("max-rep-value", "Must stay below (reputation)", "Who can take it", "RequiredMaxRepValue", QuestFieldKind.Number, "The reputation the player must stay below."),
            new("skill", "Needs skill", "Who can take it", "RequiredSkill", QuestFieldKind.Link, "A profession or skill the player needs (0 = none).", "dbc:SkillLine"),
            new("skill-value", "Needs skill points", "Who can take it", "RequiredSkillValue", QuestFieldKind.Number, "The skill points needed."),
            new("condition", "Extra condition", "Who can take it", "RequiredCondition", QuestFieldKind.Link, "A row of the server's conditions table that must be true (0 = none).", "condition"),
            new("category", "Quest log category", "Quest log", "ZoneOrSort", QuestFieldKind.Category, "The header it is listed under in the quest log: a zone (\"Westfall\" or its ID) or a category (\"sort:Warrior\")."),
            new("type", "Kind", "Quest log", "Type", QuestFieldKind.Choice, "Normal, Group, PvP, Raid, Dungeon... (QuestInfo).", "dbc:QuestInfo"),
            new("flags", "Quest flags", "Quest log", "QuestFlags", QuestFieldKind.Flags, "Sharable, party accept (an escort or event the party joins)...", null, "QuestFlags"),
            new("special-flags", "Repeatable / completed by an event", "Quest log", "SpecialFlags", QuestFieldKind.Flags, "Repeatable; Explore or event: an area trigger or a script completes it (escorts, \"speak to\" scripts).", null, "QuestSpecialFlags"),
            new("method", "Method", "Quest log", "Method", QuestFieldKind.Choice, "Normal (2); Completes automatically (0); Disabled (1).", null, "QuestMethod"),
            new("time-limit", "Time limit", "Quest log", "LimitTime", QuestFieldKind.Seconds, "Seconds to finish it (0 = no limit)."),
            new("suggested-players", "Suggested players", "Quest log", "SuggestedPlayers", QuestFieldKind.Number, "A group quest's suggested group size (0 = alone)."),
            new("prev-quest", "Must be done first", "Chain", "PrevQuestId", QuestFieldKind.Link, "A quest that must be done (handed in) first; a negative ID: a quest that must be in the quest log.", "quest"),
            new("next-quest", "Opens", "Chain", "NextQuestId", QuestFieldKind.Link, "A quest that this one opens (that quest then needs this one done).", "quest"),
            new("exclusive-group", "Exclusive group", "Chain", "ExclusiveGroup", QuestFieldKind.Number, "Quests with the same positive number exclude each other: taking one closes the others (a choice). A negative number: all of the group must be done before the quest they open."),
            new("next-in-chain", "Next in chain", "Chain", "NextQuestInChain", QuestFieldKind.Link, "The quest the same NPC offers right after you hand this one in.", "quest"),
            new("breadcrumb-for", "Breadcrumb for", "Chain", "BreadcrumbForQuestId", QuestFieldKind.Link, "This quest only leads to that one: it closes when that quest is taken.", "quest"),
            new("start-item", "Item given at the start", "Start", "SrcItemId", QuestFieldKind.Link, "An item put into the player's bags when the quest is accepted (a letter, a tool).", "item"),
            new("start-item-count", "How many (start item)", "Start", "SrcItemCount", QuestFieldKind.Number, "How many of the start item."),
            new("start-spell", "Spell cast at the start", "Start", "SrcSpell", QuestFieldKind.Link, "A spell cast on the player when the quest is accepted.", "spell"),
            new("start-script", "Script at the start", "Start", "StartScript", QuestFieldKind.Number, "quest_start_scripts id run when the quest is accepted (0 = none)."),
            new("complete-script", "Script at the end", "Start", "CompleteScript", QuestFieldKind.Number, "quest_end_scripts id run when the quest is handed in (0 = none)."),
        });
        for (int k = 1; k <= 4; k++)
        {
            f.Add(new($"target{k}", $"Kill or use {k}", "Objectives", $"ReqCreatureOrGOId{k}", QuestFieldKind.Target, "A creature to kill (\"creature:3098\" or its ID) or a game object to use (\"object:1618\"); 0 = none."));
            f.Add(new($"target{k}-count", $"How many (kill or use {k})", "Objectives", $"ReqCreatureOrGOCount{k}", QuestFieldKind.Number, "How many times."));
            f.Add(new($"target{k}-spell", $"Cast on it instead {k}", "Objectives", $"ReqSpellCast{k}", QuestFieldKind.Link, "A spell to cast on the creature or object instead of killing it (0 = kill).", "spell"));
        }
        for (int k = 1; k <= 4; k++)
        {
            f.Add(new($"item{k}", $"Collect item {k}", "Objectives", $"ReqItemId{k}", QuestFieldKind.Link, "An item to bring (it needs a drop source: a creature's loot, a chest, a vendor).", "item"));
            f.Add(new($"item{k}-count", $"How many (item {k})", "Objectives", $"ReqItemCount{k}", QuestFieldKind.Number, "How many to bring."));
            f.Add(new($"source-item{k}", $"Item that makes it {k}", "Objectives", $"ReqSourceId{k}", QuestFieldKind.Link, "An item shown in the log that makes the needed item.", "item"));
            f.Add(new($"source-item{k}-count", $"How many (source item {k})", "Objectives", $"ReqSourceCount{k}", QuestFieldKind.Number, "How many of the source item."));
        }
        f.Add(new("rep-objective-faction", "Reach reputation with", "Objectives", "RepObjectiveFaction", QuestFieldKind.Link, "A faction to reach a reputation with as the goal.", "faction"));
        f.Add(new("rep-objective-value", "Reputation to reach", "Objectives", "RepObjectiveValue", QuestFieldKind.Number, "The reputation to reach."));
        for (int k = 1; k <= 6; k++)
        {
            f.Add(new($"choice{k}", $"Reward choice {k}", "Rewards", $"RewChoiceItemId{k}", QuestFieldKind.Link, "Items the player chooses one of.", "item"));
            f.Add(new($"choice{k}-count", $"How many (choice {k})", "Rewards", $"RewChoiceItemCount{k}", QuestFieldKind.Number, "How many of the chosen item."));
        }
        for (int k = 1; k <= 4; k++)
        {
            f.Add(new($"reward-item{k}", $"Reward item {k}", "Rewards", $"RewItemId{k}", QuestFieldKind.Link, "Items always given.", "item"));
            f.Add(new($"reward-item{k}-count", $"How many (reward item {k})", "Rewards", $"RewItemCount{k}", QuestFieldKind.Number, "How many."));
        }
        f.AddRange(new QuestField[]
        {
            new("money", "Money", "Rewards", "RewOrReqMoney", QuestFieldKind.Money, "Money given (1g 50s); a negative amount is money the player must pay."),
            new("money-at-max-level", "Money at level 60 (instead of XP)", "Rewards", "RewMoneyMaxLevel", QuestFieldKind.Money, "Money given instead of experience at the maximum level."),
            new("xp", "Experience", "Rewards", "RewXP", QuestFieldKind.Number, "Experience given (VMaNGOS gives exactly this, less to players 6+ levels above the quest; 0 = none)."),
        });
        for (int k = 1; k <= 5; k++)
        {
            f.Add(new($"rep{k}", $"Reputation {k} with", "Rewards", $"RewRepFaction{k}", QuestFieldKind.Link, "A faction that gains (or loses) reputation.", "faction"));
            f.Add(new($"rep{k}-value", $"Reputation {k} amount", "Rewards", $"RewRepValue{k}", QuestFieldKind.Number, "Reputation gained (negative: lost)."));
        }
        f.AddRange(new QuestField[]
        {
            new("spell", "Reward spell (shown)", "Rewards", "RewSpell", QuestFieldKind.Link, "The spell shown as the reward (\"You will learn\"); it is cast on the player when \"Spell cast on the player\" is 0.", "spell"),
            new("spell-cast", "Spell cast on the player", "Rewards", "RewSpellCast", QuestFieldKind.Link, "The spell actually cast on the player at the end: a learn spell teaches the reward spell.", "spell"),
            new("mail-template", "Letter sent later", "Rewards", "RewMailTemplateId", QuestFieldKind.Number, "mail_loot_template / mail template sent after the quest (0 = none)."),
            new("mail-delay", "Letter delay", "Rewards", "RewMailDelaySecs", QuestFieldKind.Seconds, "Seconds before the letter arrives."),
        });
        return f;
    }

    public static QuestField? Find(string id) => Fields.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Column, id, StringComparison.OrdinalIgnoreCase));

    public static QuestField Require(string id) => Find(id) ?? throw new DesignException($"There is no quest field \"{id}\" (\"wds quests fields\" lists them).");

    // ------------------------------------------------------------------ friendly values

    /// <summary>Named values of the server's layouts (QuestFlags...) or of the game's (Race, Class...).</summary>
    public static DbcNamedValues? Named(string name) => ServerLayouts.Default.NamedValues.GetValueOrDefault(name) ?? DbcLayouts.Default.NamedValues.GetValueOrDefault(name);

    /// <summary>A class mask as names ("Warrior, Paladin"; "any" for 0).</summary>
    public static string ClassText(long mask, IReadOnlyList<(long Id, string Name)>? classes = null)
    {
        if (mask == 0) return "any";
        var names = new List<string>();
        foreach (var (id, name) in classes ?? DefaultClasses())
            if (id is >= 1 and <= 32 && (mask & (1L << (int)(id - 1))) != 0) names.Add(name);
        long known = (classes ?? DefaultClasses()).Where(c => c.Id is >= 1 and <= 32).Aggregate(0L, (m, c) => m | 1L << (int)(c.Id - 1));
        if ((mask & ~known) != 0) names.Add($"0x{mask & ~known:X}");
        return string.Join(", ", names);
    }

    /// <summary>A race mask as words: "Alliance", "Horde", "any" or names.</summary>
    public static string RaceText(long mask)
    {
        if (mask == 0 || (mask & AllRaces) == AllRaces && (mask & ~AllRaces) == 0) return "any";
        if (mask == AllianceRaces) return AllianceText;
        if (mask == HordeRaces) return HordeText;
        var named = Named("RaceMask");
        return named?.Describe(mask) ?? $"0x{mask:X}";
    }

    public static IReadOnlyList<(long Id, string Name)> DefaultClasses() =>
        Named("Class")?.Values.Where(v => v.Value > 0).Select(v => (v.Value, v.Name)).ToList() ?? new List<(long, string)>();

    /// <summary>Races by words: "Alliance", "Horde", "any", "Orc, Troll", "+Gnome" style lists or a number.</summary>
    public static long ParseRaces(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Equals("any", StringComparison.OrdinalIgnoreCase) || t == "0") return 0;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        long mask = 0;
        var races = Named("Race");
        foreach (var part in t.Split(new[] { ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("alliance", StringComparison.OrdinalIgnoreCase)) { mask |= AllianceRaces; continue; }
            if (part.Equals("horde", StringComparison.OrdinalIgnoreCase)) { mask |= HordeRaces; continue; }
            var hit = races?.Values.FirstOrDefault(v => v.Value > 0 && (string.Equals(v.Name, part, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Name.Replace(" ", ""), part.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)));
            if (hit?.Name == null) throw new DesignException($"\"{part}\" is not a race: Alliance, Horde, any, or Human, Orc, Dwarf, Night Elf, Undead, Tauren, Gnome, Troll.");
            mask |= 1L << (int)(hit.Value.Value - 1);
        }
        return mask;
    }

    /// <summary>Classes by names ("Warrior, Paladin"), "any" or a number; <paramref name="classes"/> adds new classes.</summary>
    public static long ParseClasses(string text, IReadOnlyList<(long Id, string Name)>? classes = null)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Equals("any", StringComparison.OrdinalIgnoreCase) || t == "0") return 0;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        long mask = 0;
        foreach (var part in t.Split(new[] { ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = (classes ?? DefaultClasses()).FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (hit.Name == null) throw new DesignException($"\"{part}\" is not a class ({string.Join(", ", (classes ?? DefaultClasses()).Select(c => c.Name))}, or any).");
            mask |= 1L << (int)(hit.Id - 1);
        }
        return mask;
    }

    /// <summary>The team(s) of a race mask: (Alliance, Horde) can take it.</summary>
    public static (bool Alliance, bool Horde) Teams(long races) => races == 0 ? (true, true) : ((races & AllianceRaces) != 0, (races & HordeRaces) != 0);

    /// <summary>"creature:3098", "object:1618", "-1618", "3098" as the ReqCreatureOrGOId value (negative = object).</summary>
    public static long ParseTarget(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 0;
        var parts = t.Split(':', 2);
        if (parts.Length == 2)
        {
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0) throw new DesignException($"\"{t}\" is not a target: creature:<ID> or object:<ID>.");
            return parts[0].ToLowerInvariant() switch
            {
                "creature" or "npc" or "c" => id,
                "object" or "gameobject" or "go" or "o" => -id,
                _ => throw new DesignException($"\"{parts[0]}\" is not a target kind: creature or object."),
            };
        }
        return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new DesignException($"\"{t}\" is not a target: creature:<ID>, object:<ID> or an ID (negative = object).");
    }

    public static string TargetText(long v) => v == 0 ? "0" : v > 0 ? $"creature:{v}" : $"object:{-v}";

    /// <summary>Money as "1g 50s" (negative amounts: "-1g").</summary>
    public static string MoneyText(long copper) => copper < 0 ? "-" + SpellDesigner.Money(-copper) : SpellDesigner.Money(copper);

    public static long ParseMoney(string text)
    {
        var t = text.Trim();
        return t.StartsWith('-') ? -SpellDesigner.ParseMoney(t[1..]) : SpellDesigner.ParseMoney(t);
    }

    /// <summary>The number to store for a field from the friendly text (texts: null).</summary>
    public static string ToStored(QuestField f, string text, IReadOnlyList<(long Id, string Name)>? classes, Func<string, long>? category)
    {
        var t = text.Trim();
        switch (f.Kind)
        {
            case QuestFieldKind.Line:
            case QuestFieldKind.Text:
                return text;
            case QuestFieldKind.Money:
                return ParseMoney(t).ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.Seconds:
                if (t.EndsWith("min", StringComparison.OrdinalIgnoreCase) && double.TryParse(t[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mins)) return ((long)(mins * 60)).ToString(CultureInfo.InvariantCulture);
                if (t.EndsWith('s') && long.TryParse(t[..^1].Trim(), out var secs)) return secs.ToString(CultureInfo.InvariantCulture);
                return t;
            case QuestFieldKind.ClassMask:
                return ParseClasses(t, classes).ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.RaceMask:
                return ParseRaces(t).ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.Target:
                return ParseTarget(t).ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.Category:
                return category != null ? category(t).ToString(CultureInfo.InvariantCulture) : t;
            case QuestFieldKind.Flags when f.Named != null && (t.StartsWith('+') || t.StartsWith('-')) && Named(f.Named) is { } nv:
                return t;   // +Name / -Name: resolved against the current value by the designer
            default:
                return t;
        }
    }
}
