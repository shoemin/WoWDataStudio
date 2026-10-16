using WoWData.Core.Dbc;

namespace WoWData.Core.Usage;

// Typed 1.12 (build 5875) layouts used by the "Used in game" tables. Field positions checked on the client files:
//  AreaTable (25 fields): id, map, parent area (the zone of a subzone), area bit, ..., name (11, localized).
//  FactionTemplate (14): id, faction, flags, our group mask, friendly mask, hostile mask, enemies[4], friends[4]
//    (group masks: 1 player, 2 Alliance, 4 Horde, 8 monster).
//  Faction (37): id, ..., name (19, localized).
//  SkillLineAbility (15): id, skill line, spell.   SkillLine (22): id, category, ?, name (3, localized).
//  ItemSet (45): id, name (1, localized), item ids 10-26.    SpellIcon (2): id, texture path.
//  Lock (33): id, type[8] (1-8), index[8] (9-16), skill[8] (17-24), action[8] (25-32); type 2 = needs a skill, whose
//    index is the LockType (1 lockpicking, 2 herbalism, 3 mining, 20 fishing...); type 1 = needs an item (a key).
//  Spell (173): effect[3] 61-63, effect aura[3] 91-93, effect item type[3] 103-105, effect misc value[3] 106-108,
//    effect trigger spell[3] 109-111, visual[2] 115-116, icon 117, name 120, rank 129 (Fireball 133: effects 2 / 6,
//    aura 3; Large Timber Wolf 580: aura 78 mounted, misc 358 = the creature; Linen Bandage 3275: effect 24 creates
//    item 1251; Mechanical Squirrel 4055: effect 97 summons critter 2671).

public sealed record AreaRec(int Id, int Map, int Parent, string Name);
public sealed record FactionTemplateRec(int Id, int Faction, int OurMask, int FriendlyMask, int HostileMask, int[] Enemies, int[] Friends);
public sealed record LockRec(int Id, int[] Type, int[] Index, int[] Skill);
public sealed record SpellFullRec(int Id, string Name, string Rank, int[] Effect, int[] Aura, int[] ItemType, int[] Misc, int[] Trigger, int[] Visuals, int Icon);

public static class UsageDbc
{
    public static List<AreaRec> AreaTable(DbcFile d)
    {
        d.RequireFields(20);
        var l = new List<AreaRec>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 2), d.LocString(r, 11)));
        return l;
    }

    public static List<FactionTemplateRec> FactionTemplate(DbcFile d)
    {
        d.RequireFields(14);
        var l = new List<FactionTemplateRec>(d.Count);
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.Int(r, 1), d.Int(r, 3), d.Int(r, 4), d.Int(r, 5),
                new[] { d.Int(r, 6), d.Int(r, 7), d.Int(r, 8), d.Int(r, 9) }, new[] { d.Int(r, 10), d.Int(r, 11), d.Int(r, 12), d.Int(r, 13) }));
        return l;
    }

    public static Dictionary<int, string> FactionNames(DbcFile d)
    {
        d.RequireFields(20);
        var m = new Dictionary<int, string>();
        for (int r = 0; r < d.Count; r++) m[d.Int(r, 0)] = d.LocString(r, 19);
        return m;
    }

    public static List<(int Skill, int Spell)> SkillLineAbility(DbcFile d)
    {
        d.RequireFields(3);
        var l = new List<(int, int)>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add((d.Int(r, 1), d.Int(r, 2)));
        return l;
    }

    public static List<(int Id, int Category, string Name)> SkillLine(DbcFile d)
    {
        d.RequireFields(4);
        var l = new List<(int, int, string)>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add((d.Int(r, 0), d.Int(r, 1), d.LocString(r, 3)));
        return l;
    }

    public static List<(int Id, string Name)> ItemSet(DbcFile d)
    {
        d.RequireFields(10);
        var l = new List<(int, string)>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add((d.Int(r, 0), d.LocString(r, 1)));
        return l;
    }

    public static List<(int Id, string Path)> SpellIcon(DbcFile d)
    {
        d.RequireFields(2);
        var l = new List<(int, string)>(d.Count);
        for (int r = 0; r < d.Count; r++) l.Add((d.Int(r, 0), d.String(r, 1)));
        return l;
    }

    public static List<LockRec> Lock(DbcFile d)
    {
        d.RequireFields(25);
        var l = new List<LockRec>(d.Count);
        int[] Ints(int r, int c) => Enumerable.Range(c, 8).Select(i => d.Int(r, i)).ToArray();
        for (int r = 0; r < d.Count; r++) l.Add(new(d.Int(r, 0), Ints(r, 1), Ints(r, 9), Ints(r, 17)));
        return l;
    }

    public static List<SpellFullRec> Spell(DbcFile d)
    {
        d.RequireFields(130);
        var l = new List<SpellFullRec>(d.Count);
        int[] Three(int r, int c) => new[] { d.Int(r, c), d.Int(r, c + 1), d.Int(r, c + 2) };
        for (int r = 0; r < d.Count; r++)
            l.Add(new(d.Int(r, 0), d.LocString(r, 120), d.LocString(r, 129), Three(r, 61), Three(r, 91), Three(r, 103), Three(r, 106), Three(r, 109),
                new[] { d.Int(r, 115), d.Int(r, 116) }, d.Int(r, 117)));
        return l;
    }
}
