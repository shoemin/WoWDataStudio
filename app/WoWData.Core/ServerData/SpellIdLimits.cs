namespace WoWData.Core.ServerData;

/// <summary>
/// 0.13.3 (found with the in-game test of the designers): the spell IDs the 1.12 game and its VMaNGOS server can use.
/// <list type="bullet">
/// <item>The server casts no spell above 40000: <c>Spell::cast()</c> returns at once, without a log line or a cast
/// result, when <c>m_spellInfo->Id &gt; MAX_SPELL_ID</c> (<c>#define MAX_SPELL_ID 40000</c>, src/game/Spells/Spell.h). So a
/// learn spell above 40000 teaches nothing (quest reward, trainer), and a spell above 40000 does nothing when a player,
/// a creature, an item or another spell casts it. Only the GM command <c>.learn</c> skips the cast.</item>
/// <item>The game knows no spell above 65535: SMSG_LEARNED_SPELL, SMSG_INITIAL_SPELLS, SMSG_SUPERCEDED_SPELL and
/// SMSG_REMOVED_SPELL carry 16-bit spell IDs in the 1.12 client (NetHandler_SMSG_LEARNED_SPELL @005e61c0 reads two
/// 16-bit values; VMaNGOS writes uint16 in Player::SendInitialSpells); spell 900000 arrives as 48032.</item>
/// <item>The game's own spells end at 33392 (Spell.dbc of 1.12.1 and VMaNGOS's spell_template, riding spells included).</item>
/// </list>
/// New spells therefore get their IDs from a range of their own (Settings: Spell ID range, 35000-39999 by default),
/// separate from the custom ID range of every other row (900000-999999).
/// </summary>
public static class SpellIdLimits
{
    /// <summary>VMaNGOS's MAX_SPELL_ID: no spell above it is ever cast.</summary>
    public const long MaxCastable = 40000;
    /// <summary>The 1.12 game's spell book and its spell packets hold 16-bit spell IDs.</summary>
    public const long MaxKnownByGame = 65535;
    /// <summary>The highest spell ID of the game and the server as shipped.</summary>
    public const long LastStockSpell = 33392;
    public const long DefaultStart = 35000, DefaultEnd = 39999;

    public static bool Castable(long id) => id > 0 && id <= MaxCastable;

    /// <summary>Why a spell ID cannot work, in plain words (empty when it can).</summary>
    public static string Problem(long id) => Castable(id) || id <= 0 ? "" :
        $"Spell {id} has an ID above {MaxCastable}: the VMaNGOS server never casts such a spell (Spell::cast returns at once when the ID is above MAX_SPELL_ID {MaxCastable}, " +
        "without a message), so a learn spell above it teaches nothing and the spell itself does nothing" +
        (id > MaxKnownByGame ? $"; and the 1.12 game cannot even know it (its spell book holds IDs up to {MaxKnownByGame}: spell {id} arrives as {id & 0xFFFF})" : "") +
        $". Give it an ID from {DefaultStart} to {MaxCastable} (\"Change ID\" in the Spell designer, wds spells renumber).";

    /// <summary>The server columns that hold a spell ID which the server casts or teaches: (table, column).</summary>
    public static readonly (string Table, string Column)[] SpellColumns =
    {
        ("spell_template", "entry"), ("spell_template", "effectTriggerSpell1"), ("spell_template", "effectTriggerSpell2"), ("spell_template", "effectTriggerSpell3"),
        ("quest_template", "RewSpell"), ("quest_template", "RewSpellCast"), ("quest_template", "SrcSpell"),
        ("npc_trainer", "spell"), ("npc_trainer_template", "spell"), ("skill_line_ability", "spell_id"), ("skill_line_ability", "superseded_by_spell"),
        ("playercreateinfo_spell", "spell"), ("spell_learn_spell", "entry"), ("spell_learn_spell", "SpellID"),
        ("item_template", "spellid_1"), ("item_template", "spellid_2"), ("item_template", "spellid_3"), ("item_template", "spellid_4"), ("item_template", "spellid_5"),
        ("creature_template", "spell_id1"), ("creature_template", "spell_id2"), ("creature_template", "spell_id3"), ("creature_template", "spell_id4"),
    };
}
