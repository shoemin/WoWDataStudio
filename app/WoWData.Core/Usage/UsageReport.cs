namespace WoWData.Core.Usage;

/// <summary>A piece of a line: plain text, or a link (<see cref="Key"/> = an entry key of the app's list, e.g.
/// creature:3098, item:2880, go:1618, spell:365, race:2:0, file:&lt;path&gt;), optionally coloured (#AARRGGBB).</summary>
public sealed record UsageSpan(string Text, string? Key = null, string? Color = null, bool Muted = false, bool Bold = false);

public sealed class UsageLine
{
    public List<UsageSpan> Spans { get; } = new();
    public int Indent { get; set; }

    public UsageLine Add(string text, string? key = null, string? color = null, bool muted = false, bool bold = false)
    {
        Spans.Add(new UsageSpan(text, key, color, muted, bold));
        return this;
    }

    public UsageLine Muted(string text) => Add(text, muted: true);

    public string Text => string.Concat(Spans.Select(s => s.Text));
    public override string ToString() => Text;
}

/// <summary>One labelled block of the panel ("Where it spawns", "Dropped by"...). <see cref="Id"/> is stable (used by
/// tests and the screenshots): npc, spawns, quests, drops, loot, vendors, sells, teaches, abilities, crafted, set, look,
/// vanity, placements, users, spells, casters, character, affects, creatures, items, gameobjects, models, effects, interface.</summary>
public sealed class UsageSection
{
    public UsageSection(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; set; } = "";
    public List<UsageLine> Lines { get; } = new();
    /// <summary>How many more rows exist than <see cref="Lines"/> shows.</summary>
    public int More { get; set; }
    /// <summary>Lines shown after the "and N more" line (e.g. example positions under the zone list).</summary>
    public List<UsageLine> Tail { get; } = new();

    public UsageLine TailLine(int indent = 0)
    {
        var l = new UsageLine { Indent = indent };
        Tail.Add(l);
        return l;
    }

    public UsageLine Line(int indent = 0)
    {
        var l = new UsageLine { Indent = indent };
        Lines.Add(l);
        return l;
    }

    public override string ToString() => $"{Title}: {Summary}";
}

public sealed class UsageReport
{
    public string Key { get; set; } = "";
    public UsageLine Headline { get; } = new();
    public List<UsageSection> Sections { get; } = new();
    /// <summary>Plain notes shown under the sections, e.g. what needs the server database.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>True when sections that need the server database were left out because it is not connected.</summary>
    public bool NeedsServer { get; set; }
    public bool ServerConnected { get; set; }
    public double Milliseconds { get; set; }
    /// <summary>One line for the collapsed panel header, e.g. "65 spawns in 1 zone · 1 quest link · drops 65 items".</summary>
    public string Short { get; set; } = "";

    public UsageSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    public UsageSection Add(string id, string title)
    {
        var s = new UsageSection(id, title);
        Sections.Add(s);
        return s;
    }

    public const string ServerNote = "Connect the server database in Settings to see spawns, loot and vendors.";
}

/// <summary>Display names of the game's enumerations (1.12 values; VMaNGOS and CMaNGOS use the same numbers).</summary>
public static class UsageNames
{
    public static string Rank(int r) => r switch { 1 => "Elite", 2 => "Rare elite", 3 => "Boss", 4 => "Rare", _ => "Normal" };

    public static string CreatureType(int t) => t switch
    {
        1 => "Beast", 2 => "Dragonkin", 3 => "Demon", 4 => "Elemental", 5 => "Giant", 6 => "Undead", 7 => "Humanoid",
        8 => "Critter", 9 => "Mechanical", 11 => "Totem", _ => "",
    };

    public static List<string> NpcRoles(int flags)
    {
        var l = new List<string>();
        void F(int bit, string s) { if ((flags & bit) != 0) l.Add(s); }
        F(0x2, "quest giver"); F(0x4, "vendor"); F(0x8, "flight master"); F(0x10, "trainer"); F(0x20, "spirit healer");
        F(0x80, "innkeeper"); F(0x100, "banker"); F(0x200, "guild charters"); F(0x400, "tabard designer"); F(0x800, "battlemaster");
        F(0x1000, "auctioneer"); F(0x2000, "stable master"); F(0x4000, "repairs");
        return l;
    }

    public static string ClassName(int c) => c switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest", 7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "",
    };

    public static string Reaction(int r) => r switch { 1 => "friendly", -1 => "hostile", _ => "neutral" };

    public static string GameObjectType(int t, string? lockKind) => (t, lockKind) switch
    {
        (3, "herb") => "Herb (Herbalism)",
        (3, "mining") => "Mining node (Mining)",
        (3, "lockpicking") => "Locked chest (Lockpicking)",
        (3, "key") => "Locked chest (needs a key)",
        (3, "fishing") => "Fishing node",
        _ => t switch
        {
            0 => "Door", 1 => "Button / lever", 2 => "Quest giver", 3 => "Chest / container", 4 => "Binder", 5 => "Decoration (generic)", 6 => "Trap",
            7 => "Chair", 8 => "Spell focus (e.g. anvil, forge, campfire)", 9 => "Book / plaque (readable text)", 10 => "Usable object",
            11 => "Elevator / transport", 12 => "Area damage", 13 => "Camera", 14 => "Map object", 15 => "Ship / zeppelin", 16 => "Duel flag",
            17 => "Fishing bobber", 18 => "Summoning ritual", 19 => "Mailbox", 20 => "Auction house", 21 => "Guard post", 22 => "Spell caster",
            23 => "Meeting stone", 24 => "Flag stand", 25 => "Fishing hole (school of fish)", 26 => "Flag drop", 27 => "Mini game",
            28 => "Lottery kiosk", 29 => "Capture point", 30 => "Aura generator", 31 => "Dungeon difficulty", _ => $"type {t}",
        },
    };

    public static string Quality(int q) => q switch
    {
        0 => "Poor", 1 => "Common", 2 => "Uncommon", 3 => "Rare", 4 => "Epic", 5 => "Legendary", 6 => "Artifact", _ => "",
    };

    /// <summary>Item quality colours, darkened where the game's colour is unreadable on the light panel (white, green).</summary>
    public static string QualityColor(int q) => q switch
    {
        0 => "#FF7D7D7D", 1 => "#FF2B2B2B", 2 => "#FF1A8A00", 3 => "#FF0062C4", 4 => "#FF8A2BD8", 5 => "#FFD06A00", 6 => "#FFB89500", _ => "#FF2B2B2B",
    };

    public static string InventoryType(int t) => t switch
    {
        1 => "Head", 2 => "Neck", 3 => "Shoulder", 4 => "Shirt", 5 => "Chest", 6 => "Waist", 7 => "Legs", 8 => "Feet", 9 => "Wrist",
        10 => "Hands", 11 => "Finger", 12 => "Trinket", 13 => "One-hand", 14 => "Off hand (shield)", 15 => "Ranged", 16 => "Back",
        17 => "Two-hand", 18 => "Bag", 19 => "Tabard", 20 => "Chest (robe)", 21 => "Main hand", 22 => "Off hand", 23 => "Held in off hand",
        24 => "Ammo", 25 => "Thrown", 26 => "Ranged (wand / gun / crossbow)", 27 => "Quiver", 28 => "Relic", _ => "",
    };

    public static string ItemClass(int c) => c switch
    {
        0 => "Consumable", 1 => "Container", 2 => "Weapon", 4 => "Armor", 5 => "Reagent", 6 => "Projectile", 7 => "Trade goods",
        9 => "Recipe", 11 => "Quiver", 12 => "Quest item", 13 => "Key", 15 => "Miscellaneous", _ => $"class {c}",
    };

    public static string ItemSubClass(int c, int s) => (c, s) switch
    {
        (2, 0) => "One-handed axe", (2, 1) => "Two-handed axe", (2, 2) => "Bow", (2, 3) => "Gun", (2, 4) => "One-handed mace",
        (2, 5) => "Two-handed mace", (2, 6) => "Polearm", (2, 7) => "One-handed sword", (2, 8) => "Two-handed sword", (2, 10) => "Staff",
        (2, 13) => "Fist weapon", (2, 14) => "Miscellaneous weapon", (2, 15) => "Dagger", (2, 16) => "Thrown", (2, 18) => "Crossbow",
        (2, 19) => "Wand", (2, 20) => "Fishing pole",
        (4, 0) => "Miscellaneous armor", (4, 1) => "Cloth", (4, 2) => "Leather", (4, 3) => "Mail", (4, 4) => "Plate", (4, 6) => "Shield",
        (4, 7) => "Libram", (4, 8) => "Idol", (4, 9) => "Totem",
        (1, 0) => "Bag", (1, 1) => "Soul bag", (1, 2) => "Herb bag", (1, 3) => "Enchanting bag",
        (6, 2) => "Arrow", (6, 3) => "Bullet", (11, 2) => "Quiver", (11, 3) => "Ammo pouch",
        (9, 0) => "Book", (9, 1) => "Leatherworking recipe", (9, 2) => "Tailoring pattern", (9, 3) => "Engineering schematic",
        (9, 4) => "Blacksmithing plans", (9, 5) => "Cooking recipe", (9, 6) => "Alchemy recipe", (9, 7) => "First Aid manual",
        (9, 8) => "Enchanting formula", (9, 9) => "Fishing book",
        (7, 1) => "Parts", (7, 2) => "Explosives", (7, 3) => "Devices",
        _ => "",
    };

    public static string Section(int s) => s switch { 0 => "skin", 1 => "face", 2 => "facial hair", 3 => "hair", 4 => "underwear", _ => $"section {s}" };

    /// <summary>"35%", "0.4%", "0.02%".</summary>
    public static string Chance(double c) => c >= 10 ? $"{c:0}%" : c >= 1 ? $"{c:0.#}%" : c >= 0.1 ? $"{c:0.##}%" : c > 0 ? $"{c:0.###}%" : "rare";

    public static string Plural(int n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";
}
