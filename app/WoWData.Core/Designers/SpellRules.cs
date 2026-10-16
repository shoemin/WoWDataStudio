namespace WoWData.Core.Designers;

/// <summary>How the 1.12 world server (VMaNGOS) handles an effect or aura type.</summary>
public enum ServerSupport
{
    /// <summary>Handled by the server's own code.</summary>
    Works,
    /// <summary>Does something only with server script code written for this spell (Dummy, Script effect, class scripts).</summary>
    NeedsScript,
    /// <summary>The server's handler is empty: the effect does nothing ("unused", "obsolete", "NULL" in its tables).</summary>
    DoesNothing,
    /// <summary>Not a type of the 1.12 client and server at all.</summary>
    Unknown,
}

/// <summary>A spell effect or aura type in plain words: what it does, what its "misc value" means, how the server handles it.</summary>
public sealed record SpellTypeInfo(int Id, string Name, string Explanation, string MiscValue, ServerSupport Server, string ServerNote = "")
{
    public string Text => $"{Id} · {Name}";
    public string ServerText => Server switch
    {
        ServerSupport.Works => "The 1.12 server handles it.",
        ServerSupport.NeedsScript => "The server does nothing with it unless a script is written for this spell (server code).",
        ServerSupport.DoesNothing => "The 1.12 server does nothing with it (its handler is empty" + (ServerNote.Length > 0 ? ": " + ServerNote : "") + ").",
        _ => "Not a type the 1.12 client and server know.",
    };
}

/// <summary>
/// 0.12.0: the spell effect and aura types of 1.12 in plain words for the spell designer, with what their "misc value"
/// holds and whether the 1.12 world server does anything with them. The server facts come from VMaNGOS's handler tables
/// (SpellEffects.cpp: the 131 entries of SpellEffects[], EffectNULL / EffectUnused; SpellAuras.cpp: the 193 entries of
/// AuraHandler[], HandleNULL / HandleUnused), read for facts only. The names are the layouts' (dbc_layouts_5875.txt).
/// </summary>
public static class SpellRules
{
    /// <summary>Effects 0-130 exist (TOTAL_SPELL_EFFECTS = 131 in the 1.12 server); auras 0-192 (TOTAL_AURAS = 193).</summary>
    public const int MaxEffect = 130, MaxAura = 192;

    // effects the server leaves empty (EffectUnused / EffectNULL), with its own note
    private static readonly Dictionary<int, string> UnusedEffects = new()
    {
        [4] = "unused since before 1.2.1", [12] = "unused since before 1.2.1", [13] = "unused since before 1.2.1", [14] = "unused since before 1.2.1",
        [15] = "unused since before 1.2.1", [48] = "one spell: Base Stealth", [49] = "one spell: Detect", [51] = "unused since before 1.2.1",
        [52] = "unused since before 1.2.1", [65] = "unused", [66] = "unused since before 1.2.1", [78] = "unused", [82] = "not implemented",
        [91] = "one spell: zzOLDBrainwash", [93] = "unused", [120] = "one test spell", [122] = "unused", [126] = "one test spell", [127] = "no spells",
    };

    private static readonly Dictionary<int, string> UnusedAuras = new()
    {
        [46] = "parry skill", [48] = "dodge skill", [50] = "obsolete", [63] = "obsolete", [70] = "only a visual spell uses it", [90] = "useless",
        [111] = "unused", [119] = "useless", [145] = "obsolete", [146] = "obsolete", [157] = "one test spell", [163] = "unused in 1.12",
        [164] = "one test spell", [173] = "only Proclaim Champion", [181] = "unused", [189] = "not used in 1.12.1",
    };

    private static readonly HashSet<int> ScriptEffects = new() { 3, 61, 77 };
    private static readonly HashSet<int> ScriptAuras = new() { 4, 112 };

    /// <summary>Effects that apply an aura (they need an aura type).</summary>
    public static readonly HashSet<int> AuraEffects = new() { 6, 27, 35, 119, 128, 129 };
    /// <summary>Auras that tick (they need a tick period).</summary>
    public static readonly HashSet<int> PeriodicAuras = new() { 3, 8, 20, 21, 23, 24, 53, 62, 63, 64, 89 };
    /// <summary>Auras that fire on an event (they need proc flags).</summary>
    public static readonly HashSet<int> ProcAuras = new() { 42, 43, 109 };
    /// <summary>Effects that summon a creature (misc value = the creature's entry).</summary>
    public static readonly HashSet<int> SummonEffects = new() { 28, 41, 42, 56, 73, 74, 87, 88, 89, 90, 97, 112 };
    /// <summary>Effects that place a game object (misc value = the object's entry).</summary>
    public static readonly HashSet<int> ObjectEffects = new() { 50, 76, 86, 104, 105, 106, 107 };
    /// <summary>Effects and auras that name another spell in their trigger spell.</summary>
    public static readonly HashSet<int> TriggerEffects = new() { 32, 36, 57, 64 };
    public static readonly HashSet<int> TriggerAuras = new() { 23, 42 };
    /// <summary>Implicit targets that hit an area (they need a radius).</summary>
    public static readonly HashSet<int> AreaTargets = new() { 2, 3, 4, 7, 8, 15, 16, 20, 24, 28, 29, 30, 31, 33, 34, 36, 37, 51, 52, 54, 56, 59, 60, 61 };

    private static readonly (int Id, string Text, string Misc)[] EffectTexts =
    {
        (0, "No effect: this slot is empty.", ""),
        (1, "Kills the target at once.", ""),
        (2, "Deals damage of the spell's school (Fireball, Shadow Bolt). The points are the damage.", ""),
        (3, "Does nothing by itself: the server must have script code for this spell (many class abilities use it).", ""),
        (5, "Teleports the target to a fixed place (from the server's spell_target_position table).", ""),
        (6, "Puts an aura (a buff or debuff) on the target: choose the aura type; the duration comes from the spell's duration.", ""),
        (7, "Environmental damage (fire, lava, falling).", ""),
        (8, "Drains power (mana, rage, energy) from the target.", "the power type drained (0 mana, 1 rage, 3 energy)"),
        (9, "Deals damage and heals the caster for part of it.", ""),
        (10, "Heals the target. The points are the healing.", ""),
        (11, "Binds the target's hearthstone here (innkeepers).", ""),
        (16, "Completes a quest for the target.", "the quest ID"),
        (17, "Weapon damage without school (special attacks).", ""),
        (18, "Brings a dead player back to life with the points as health.", ""),
        (19, "Gives extra melee attacks.", ""),
        (20, "Lets the target dodge (a skill).", ""),
        (21, "Evade.", ""),
        (22, "Lets the target parry (a skill).", ""),
        (23, "Lets the target block (a skill).", ""),
        (24, "Creates an item in the caster's bags (Conjure Water, crafted goods).", "(the item is in Item type)"),
        (25, "Lets the target use a weapon type (a weapon skill).", ""),
        (26, "Defense skill.", ""),
        (27, "Creates an area on the ground that puts its aura on everyone inside (Consecration, Blizzard needs the area's visual).", ""),
        (28, "Summons a creature (a pet, a guardian or a totem depending on the creature).", "the creature's entry (creature_template)"),
        (29, "Leaps the caster forward.", ""),
        (30, "Gives power (mana, rage, energy) to the target.", "the power type (0 mana, 1 rage, 3 energy)"),
        (31, "Deals a percentage of weapon damage.", ""),
        (32, "Launches another spell as a missile at the target (the trigger spell).", ""),
        (33, "Opens a lock (chests, doors, lockpicking).", "the lock type"),
        (35, "Puts its aura on the caster's party members in range (Paladin auras, Trueshot Aura).", ""),
        (36, "Teaches the target the trigger spell (trainers use these \"learn\" spells).", ""),
        (38, "Removes buffs or debuffs of a dispel type (Dispel Magic, Cleanse).", "the dispel type (1 Magic, 2 Curse, 3 Disease, 4 Poison)"),
        (39, "Teaches a language.", "the language"),
        (40, "Lets the target dual wield.", ""),
        (41, "Summons a creature that is not your pet (wild).", "the creature's entry"),
        (42, "Summons a guardian that fights for you.", "the creature's entry"),
        (43, "Teleports the target to face the caster.", ""),
        (44, "Raises a skill's maximum (profession ranks).", "the skill (SkillLine)"),
        (45, "Gives honor points.", ""),
        (46, "Plays a spawn animation (creatures appearing).", ""),
        (47, "A trade skill (profession) spell.", ""),
        (50, "Places a trap or other game object.", "the game object's entry"),
        (53, "Enchants an item permanently.", "the enchantment (SpellItemEnchantment)"),
        (54, "Enchants an item for a while (poisons, oils).", "the enchantment (SpellItemEnchantment)"),
        (55, "Tames a beast (hunters).", ""),
        (56, "Summons your pet (warlock demons, hunter pets).", "the creature's entry"),
        (57, "Teaches your pet the trigger spell.", ""),
        (58, "Deals weapon damage plus the points (Heroic Strike, Sinister Strike).", ""),
        (59, "Opens a lock with an item (keys).", ""),
        (60, "Lets the target use an item class (armor or weapon proficiency).", ""),
        (61, "Sends an event to the server's scripts.", "the event ID"),
        (62, "Burns the target's power and deals damage for it.", "the power type"),
        (63, "Adds threat (Taunt-like threat without taunting).", ""),
        (64, "Casts another spell (the trigger spell) at once.", ""),
        (67, "Heals to full health.", ""),
        (68, "Interrupts the target's spell casting (Pummel, Kick, Counterspell).", ""),
        (69, "Distracts the target (it turns to look).", ""),
        (70, "Pulls the target.", ""),
        (71, "Pickpockets the target (rogues).", ""),
        (72, "Lets the caster see through a far point (Far Sight).", ""),
        (73, "Takes control of a creature (Eye of Kilrogg).", "the creature's entry"),
        (74, "Summons a totem in a free slot.", "the creature's entry"),
        (75, "Heals a mechanical target.", ""),
        (76, "Places a game object (not linked to the caster).", "the game object's entry"),
        (77, "Does something the server's script code decides for this spell.", ""),
        (79, "Makes the target leave combat (Vanish-like sanctuary).", ""),
        (80, "Adds combo points (rogues, druids in cat form).", ""),
        (83, "Starts a duel.", ""),
        (84, "Unstuck.", ""),
        (85, "Summons a player (meeting stones, ritual of summoning).", ""),
        (86, "Activates a game object.", "the action"),
        (87, "Summons a totem into the fire slot.", "the creature's entry"),
        (88, "Summons a totem into the earth slot.", "the creature's entry"),
        (89, "Summons a totem into the water slot.", "the creature's entry"),
        (90, "Summons a totem into the air slot.", "the creature's entry"),
        (92, "Enchants the item in your hand.", "the enchantment"),
        (94, "Lets the target come back to life later (Soulstone, Reincarnation).", ""),
        (95, "Skins a creature.", ""),
        (96, "Charges at the target.", ""),
        (97, "Summons a critter (a companion).", "the creature's entry"),
        (98, "Knocks the target back.", "the vertical speed x10"),
        (99, "Disenchants an item.", ""),
        (100, "Makes the target drunk.", ""),
        (101, "Feeds your pet.", ""),
        (102, "Dismisses your pet.", ""),
        (103, "Gives reputation.", "the faction"),
        (104, "Places a game object in slot 1 (fishing bobbers, rituals).", "the game object's entry"),
        (105, "Places a game object in slot 2.", "the game object's entry"),
        (106, "Places a game object in slot 3.", "the game object's entry"),
        (107, "Places a game object in slot 4.", "the game object's entry"),
        (108, "Removes effects of a mechanic (Will of the Forsaken, Escape Artist).", "the mechanic (SpellMechanic)"),
        (109, "Brings your dead pet back.", ""),
        (110, "Destroys all your totems.", ""),
        (111, "Damages the target's items' durability.", ""),
        (112, "Summons a demon (warlocks).", "the creature's entry"),
        (113, "Resurrects with a percentage of health and mana.", ""),
        (114, "Forces the target to attack the caster (Taunt).", ""),
        (115, "Damages durability by a percentage.", ""),
        (116, "Skins a player's corpse (battlegrounds).", ""),
        (117, "Spirit heal (spirit healers).", ""),
        (118, "Teaches a skill.", "the skill (SkillLine)"),
        (119, "Puts its aura on the caster's pet.", ""),
        (121, "Normalized weapon damage plus the points.", ""),
        (123, "Sends the player on a flight path.", "the flight path (TaxiPath)"),
        (124, "Pulls the player.", ""),
        (125, "Changes the target's threat by a percentage.", ""),
        (128, "Puts its aura on friendly units around.", ""),
        (129, "Puts its aura on enemies around.", ""),
        (130, "Despawns a game object.", ""),
    };

    private static readonly (int Id, string Text, string Misc)[] AuraTexts =
    {
        (0, "No aura.", ""),
        (2, "Mind control (the caster controls the target).", ""),
        (3, "Deals damage every tick (damage over time: Corruption, Rend). The points are the damage per tick.", ""),
        (4, "Does nothing by itself: the server must have script code for it.", ""),
        (5, "Confuses the target (it wanders).", ""),
        (6, "Charms the target (it fights for you).", ""),
        (7, "Fears the target (it runs away).", ""),
        (8, "Heals every tick (heal over time: Renew, Rejuvenation). The points are the healing per tick.", ""),
        (9, "Changes attack speed by a percentage.", ""),
        (10, "Changes threat caused by a percentage.", "a school mask (1 physical, 2 holy, 4 fire, 8 nature, 16 frost, 32 shadow, 64 arcane; 127 all)"),
        (11, "Taunts: the target must attack the caster.", ""),
        (12, "Stuns the target.", ""),
        (13, "Changes damage done by a fixed amount.", "a school mask (127 = all schools)"),
        (14, "Changes damage taken by a fixed amount.", "a school mask"),
        (15, "Damages attackers when they hit (Thorns).", "a school mask"),
        (16, "Stealth.", ""),
        (17, "Better stealth detection.", ""),
        (18, "Invisibility.", ""),
        (19, "Detects invisibility.", ""),
        (20, "Restores a percentage of health every tick.", ""),
        (21, "Restores a percentage of mana every tick.", ""),
        (22, "Changes resistances (armor for the physical school).", "a school mask"),
        (23, "Casts the trigger spell every tick (Blizzard, Hurricane, Tranquility).", ""),
        (24, "Gives power every tick.", "the power type (0 mana, 1 rage, 3 energy)"),
        (25, "Pacifies the target (cannot attack).", ""),
        (26, "Roots the target (cannot move).", ""),
        (27, "Silences the target (cannot cast).", ""),
        (28, "Reflects spells back at the caster.", ""),
        (29, "Changes a stat (Strength, Agility...).", "the stat: 0 Strength, 1 Agility, 2 Stamina, 3 Intellect, 4 Spirit, -1 all"),
        (30, "Changes a skill.", "the skill (SkillLine)"),
        (31, "Faster movement (a percentage).", ""),
        (32, "Faster mounted movement (mounts).", ""),
        (33, "Slower movement (a negative percentage: Hamstring, Frostbolt).", ""),
        (34, "More maximum health.", ""),
        (35, "More maximum power.", "the power type"),
        (36, "Changes the shape (druid forms, warrior stances).", "the form (SpellShapeshiftForm)"),
        (37, "Immunity to an effect type.", "the effect type"),
        (38, "Immunity to an aura type.", "the aura type"),
        (39, "Immunity to schools of damage.", "a school mask"),
        (40, "Immunity to damage of schools.", "a school mask"),
        (41, "Immunity to a dispel type.", "the dispel type"),
        (42, "Casts the trigger spell when the proc event happens (Proc flags, Proc chance).", ""),
        (43, "Deals damage when the proc event happens.", ""),
        (44, "Tracks creatures of a type on the minimap.", "the creature type"),
        (45, "Tracks resources (herbs, minerals) on the minimap.", "the lock type"),
        (47, "Changes parry chance (percent).", ""),
        (49, "Changes dodge chance (percent).", ""),
        (51, "Changes block chance (percent).", ""),
        (52, "Changes critical strike chance (percent).", ""),
        (53, "Drains health every tick and gives it to the caster.", ""),
        (54, "Changes hit chance (percent).", ""),
        (55, "Changes spell hit chance (percent).", ""),
        (56, "Turns the target into a creature's look (Polymorph-like).", "the creature's entry"),
        (57, "Changes spell critical chance (percent).", ""),
        (58, "Faster swimming.", ""),
        (59, "More damage against a creature type.", "a creature type mask"),
        (60, "Pacify and silence.", ""),
        (61, "Changes the size of the target (percent).", ""),
        (64, "Drains mana every tick.", "the power type"),
        (65, "Changes casting speed (percent).", ""),
        (66, "Feign death.", ""),
        (67, "Disarms the target.", ""),
        (69, "Absorbs damage (Power Word: Shield, Ice Barrier).", "a school mask"),
        (71, "Changes spell critical chance of schools.", "a school mask"),
        (72, "Changes power cost of schools by a percentage.", "a school mask"),
        (73, "Changes power cost of schools by a fixed amount.", "a school mask"),
        (74, "Reflects spells of schools.", "a school mask"),
        (75, "Changes the spoken language.", "the language"),
        (76, "Far sight.", ""),
        (77, "Immunity to a mechanic (Stun, Fear...).", "the mechanic (SpellMechanic)"),
        (78, "A mount: the target rides the creature's look.", "the creature's entry (its display is the mount)"),
        (79, "Changes damage done by a percentage.", "a school mask"),
        (80, "Changes a stat by a percentage.", "the stat (-1 = all)"),
        (81, "Shares damage taken with the caster (Blessing of Sacrifice-like).", ""),
        (82, "Water breathing.", ""),
        (83, "Changes base resistances.", "a school mask"),
        (84, "Restores health over time (regeneration while resting).", ""),
        (85, "Restores power over time.", "the power type"),
        (86, "Creates an item when the target dies while channelling (Drain Soul).", ""),
        (87, "Changes damage taken by a percentage.", "a school mask"),
        (88, "Changes health regeneration by a percentage.", ""),
        (89, "Deals a percentage of health as damage every tick.", ""),
        (91, "Changes how far creatures notice you.", ""),
        (92, "Prevents fleeing.", ""),
        (93, "Unattackable.", ""),
        (94, "Stops regeneration.", ""),
        (95, "Ghost (dead players).", ""),
        (96, "Redirects spells to the caster (Grounding Totem).", ""),
        (97, "Mana shield: damage is taken from mana.", "a school mask"),
        (98, "Changes a skill (talent version).", "the skill"),
        (99, "Changes attack power.", ""),
        (101, "Changes resistances by a percentage.", "a school mask"),
        (102, "More melee attack power against a creature type.", "a creature type mask"),
        (103, "Changes total threat.", ""),
        (104, "Walk on water.", ""),
        (105, "Feather fall (slow fall).", ""),
        (106, "Hover.", ""),
        (107, "Talent-style flat change of other spells (their cost, damage, cast time...). The spells changed are the ones of the class whose spell family mask matches.", "what is changed (0 damage, 1 duration, 10 cast time, 11 cooldown, 14 cost...)"),
        (108, "Talent-style percentage change of other spells (see Add flat modifier).", "what is changed"),
        (109, "Casts the trigger spell on the target when you hit it.", ""),
        (110, "Changes power regeneration by a percentage.", "the power type"),
        (112, "Changes other spells through server class scripts.", "the class script ID"),
        (113, "Changes ranged damage taken.", ""),
        (115, "Changes healing taken.", "a school mask"),
        (116, "Health regeneration continues in combat (percent).", ""),
        (117, "Resistance to a mechanic (percent).", "the mechanic"),
        (118, "Changes healing taken by a percentage.", ""),
        (121, "Empathy (beast lore).", ""),
        (122, "Changes off-hand damage (percent).", ""),
        (123, "Ignores part of the target's resistances (spell penetration).", "a school mask"),
        (124, "Changes ranged attack power.", ""),
        (125, "Changes melee damage taken.", ""),
        (126, "Changes melee damage taken by a percentage.", ""),
        (127, "More ranged attack power for attackers (Hunter's Mark).", ""),
        (129, "Faster movement that always applies.", ""),
        (130, "Faster mounted movement that always applies.", ""),
        (131, "More ranged attack power against a creature type.", "a creature type mask"),
        (132, "More maximum power by a percentage.", "the power type"),
        (133, "More maximum health by a percentage.", ""),
        (135, "Changes healing done (spell power for healing).", "a school mask"),
        (136, "Changes healing done by a percentage.", "a school mask"),
        (137, "Changes all stats by a percentage.", "the stat (-1 = all)"),
        (138, "Changes melee attack speed (haste).", ""),
        (139, "Forces how a faction reacts to you.", "the faction"),
        (140, "Changes ranged attack speed.", ""),
        (141, "Changes ranged attack speed of ammunition.", ""),
        (142, "Changes base resistances by a percentage.", "a school mask"),
        (143, "Resistance that does not stack.", "a school mask"),
        (144, "Safe fall.", ""),
        (147, "Immunity to mechanics (a mask).", "a mechanic mask"),
        (148, "Keeps combo points.", ""),
        (149, "Resists spell pushback.", ""),
        (150, "Changes shield block value by a percentage.", ""),
        (151, "Tracks stealthed units.", ""),
        (152, "Changes how far creatures detect you.", ""),
        (153, "Shares a fixed amount of damage.", ""),
        (154, "Changes stealth level.", ""),
        (155, "Longer water breathing.", ""),
        (156, "Changes reputation gain.", ""),
        (158, "Changes shield block value.", ""),
        (159, "No PvP credit (Honorless Target).", ""),
        (160, "Less damage from area spells.", ""),
        (161, "Health regeneration in combat.", ""),
        (162, "Burns mana every tick.", "the power type"),
        (165, "More melee attack power for attackers.", ""),
        (166, "Changes attack power by a percentage.", ""),
        (167, "Changes ranged attack power by a percentage.", ""),
        (168, "More damage against a creature type.", "a creature type mask"),
        (169, "More critical chance against a creature type.", "a creature type mask"),
        (170, "Detect Amore (an event).", ""),
        (171, "Faster movement that does not stack.", ""),
        (172, "Faster mounted movement that does not stack.", ""),
        (174, "Spell damage from a percentage of a stat.", "a school mask"),
        (175, "Spell healing from a percentage of a stat.", "a school mask"),
        (176, "Spirit of Redemption.", ""),
        (177, "Area charm.", ""),
        (178, "Resistance to debuffs of a dispel type.", "the dispel type"),
        (179, "Attackers' spell critical chance.", "a school mask"),
        (180, "More spell damage against a creature type.", "a creature type mask"),
        (182, "Resistance from a percentage of a stat.", "a school mask"),
        (183, "Threat of critical hits.", ""),
        (184, "Attackers' melee hit chance.", ""),
        (185, "Attackers' ranged hit chance.", ""),
        (186, "Attackers' spell hit chance.", ""),
        (187, "Attackers' melee critical chance.", ""),
        (188, "Attackers' ranged critical chance.", ""),
        (190, "Reputation gain of a faction.", "the faction"),
        (191, "Uses the normal movement speed.", ""),
    };

    private static readonly Dictionary<int, (string Text, string Misc)> _effects = EffectTexts.ToDictionary(x => x.Id, x => (x.Text, x.Misc));
    private static readonly Dictionary<int, (string Text, string Misc)> _auras = AuraTexts.ToDictionary(x => x.Id, x => (x.Text, x.Misc));

    /// <summary>The names of the effect and aura types (from the layouts' SpellEffect and AuraType).</summary>
    private static string Name(string values, long id) => Dbc.DbcLayouts.Default.NamedValues.GetValueOrDefault(values)?.NameOf(id) ?? $"Unknown ({id})";

    public static SpellTypeInfo Effect(long id)
    {
        if (id < 0 || id > MaxEffect) return new SpellTypeInfo((int)id, $"Unknown ({id})", $"Effect {id} does not exist in 1.12 (the effects are 0-{MaxEffect}).", "", ServerSupport.Unknown);
        var name = Name("SpellEffect", id);
        var (text, misc) = _effects.TryGetValue((int)id, out var t) ? t : ($"{name}.", "");
        var support = UnusedEffects.TryGetValue((int)id, out var note) ? ServerSupport.DoesNothing : ScriptEffects.Contains((int)id) ? ServerSupport.NeedsScript : ServerSupport.Works;
        if (id == 0) support = ServerSupport.Works;
        return new SpellTypeInfo((int)id, name, text, misc, support, note ?? "");
    }

    public static SpellTypeInfo Aura(long id)
    {
        if (id < 0 || id > MaxAura) return new SpellTypeInfo((int)id, $"Unknown ({id})", $"Aura {id} does not exist in 1.12 (the auras are 0-{MaxAura}).", "", ServerSupport.Unknown);
        var name = Name("AuraType", id);
        var (text, misc) = _auras.TryGetValue((int)id, out var t) ? t : ($"{name}.", "");
        var support = UnusedAuras.TryGetValue((int)id, out var note) ? ServerSupport.DoesNothing : ScriptAuras.Contains((int)id) ? ServerSupport.NeedsScript : ServerSupport.Works;
        if (id == 0) support = ServerSupport.Works;
        return new SpellTypeInfo((int)id, name, text, misc, support, note ?? "");
    }

    public static IEnumerable<SpellTypeInfo> AllEffects() => Enumerable.Range(0, MaxEffect + 1).Select(i => Effect(i));
    public static IEnumerable<SpellTypeInfo> AllAuras() => Enumerable.Range(0, MaxAura + 1).Select(i => Aura(i));
}
