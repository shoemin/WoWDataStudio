using System.Globalization;

namespace WoWData.Core.ServerData;

/// <summary>A problem of a value or a row: an error blocks the edit, a warning only says so.</summary>
public sealed record ServerProblem(string Column, string Message, bool IsError)
{
    public override string ToString() => (IsError ? "error: " : "warning: ") + (Column.Length > 0 ? Column + ": " : "") + Message;
}

/// <summary>
/// 0.9.0: the checks of server edits. Per value: the SQL type (range, decimals, length, NULL, character set), a choice
/// that is not a known value (warning) and a link to a row that does not exist (an error for the column you edit, a
/// warning for the others). Per row: the rules VMaNGOS applies when it loads the data (read from its loaders for facts:
/// levels, display IDs that "can crash the client", loot chances and counts, patch ranges...), so an edit that the server
/// would refuse or "correct" at start is caught here.
/// </summary>
public static class ServerValidation
{
    /// <summary>Check one typed value of a column; <paramref name="canonical"/> is the value to store.</summary>
    public static List<ServerProblem> CheckValue(ServerEditor ed, WorldTable t, string column, string? text, out string? canonical)
    {
        var res = new List<ServerProblem>();
        canonical = null;
        var c = t.Column(column) ?? throw new ServerDataException($"{t.Name} has no column {column}.");
        var info = ed.Service.Info(t.Name)?.Column(c.Name);
        // a choice or flags may be typed by name ("Rare", "Vendor|Repairs items")
        if (text != null && info?.Values != null && c.IsNumber && !LooksNumeric(text))
        {
            var byName = ParseNamed(info.Values, info.Semantic == ServerSemantic.Flags, text);
            if (byName == null)
            {
                res.Add(new ServerProblem(c.Name, $"\"{text}\" is not one of the names of {c.Name} ({string.Join(", ", info.Values.Values.Take(12).Select(v => v.Name))}{(info.Values.Values.Count > 12 ? ", ..." : "")}).", true));
                return res;
            }
            text = byName.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (text != null && info?.Semantic == ServerSemantic.Money && !LooksNumeric(text) && ParseMoney(text) is { } copper) text = copper.ToString(CultureInfo.InvariantCulture);
        var problem = WorldValue.Normalize(c, text, out canonical);
        if (problem != null)
        {
            res.Add(new ServerProblem(c.Name, problem, true));
            return res;
        }
        if (info != null) res.AddRange(Meaning(ed, t, c, info, canonical, edited: true));
        return res;
    }

    private static bool LooksNumeric(string s)
    {
        var t = s.Trim();
        return t == WorldValue.NullText || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>A value by its name ("Rare") or names joined with "|" for flags.</summary>
    public static long? ParseNamed(Dbc.DbcNamedValues values, bool flags, string text)
    {
        long total = 0;
        foreach (var part in flags ? text.Split('|', ',', '+') : new[] { text })
        {
            var p = part.Trim();
            long? hit = null;
            foreach (var (val, name) in values.Values)
                if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)) { hit = val; break; }
            if (hit == null)
                foreach (var (val, name) in values.Values)
                    if (p.Length > 0 && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { hit = val; break; }
            if (hit == null) return null;
            total = flags ? total | hit.Value : hit.Value;
        }
        return total;
    }

    /// <summary>"1g 20s 5c" in copper (null: not that form).</summary>
    public static long? ParseMoney(string text)
    {
        long total = 0;
        bool any = false;
        foreach (var part in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2 || !long.TryParse(part[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
            total += part[^1] switch { 'g' => n * 10000, 's' => n * 100, 'c' => n, _ => long.MinValue / 4 };
            if (total < 0 && n >= 0) return null;
            any = true;
        }
        return any ? total : null;
    }

    /// <summary>Does a column's link apply to this row? (A loot row with a negative mincountOrRef includes reference loot:
    /// its item column is then not an item.)</summary>
    public static bool LinkApplies(WorldTable t, IReadOnlyList<string?> row, string column)
    {
        if (t.Name.EndsWith("_loot_template", StringComparison.OrdinalIgnoreCase) && column.Equals("item", StringComparison.OrdinalIgnoreCase) && t.IndexOf("mincountOrRef") is var i and >= 0)
            return WorldValue.Long(row[i]) > 0;
        // 0.12.0: spell_template.effectItemTypeN is an item only for "Create item" (24); for auras it holds the spell
        // family mask the aura changes (Tactical Mastery: 8388608)
        if (IsSpellItemType(t, column) && t.IndexOf("effect" + column[^1]) is var e and >= 0)
            return WorldValue.Long(row[e]) == 24;
        return true;
    }

    private static bool IsSpellItemType(WorldTable t, string column) =>
        t.Name.Equals("spell_template", StringComparison.OrdinalIgnoreCase) && column.StartsWith("effectItemType", StringComparison.OrdinalIgnoreCase) && column.Length == 15;

    /// <summary>The meaning checks of a value: a choice that is not a known value, a link to nothing.</summary>
    public static IEnumerable<ServerProblem> Meaning(ServerEditor ed, WorldTable t, WorldColumn c, ServerColumnInfo info, string? v, bool edited)
    {
        if (v == null) yield break;
        if (info.Semantic == ServerSemantic.Enum && info.Values != null && c.IsNumber && info.Values.NameOf(WorldValue.Long(v)) == null)
            yield return new ServerProblem(c.Name, $"{v} is not a known value of {c.Name}.", false);
        if (info.Semantic == ServerSemantic.Patch && c.IsNumber && (WorldValue.Long(v) < 0 || WorldValue.Long(v) > 10))
            yield return new ServerProblem(c.Name, "Content patches go from 0 (1.2) to 10 (1.12).", true);
        if (info.Link != null && c.IsNumber && info.Link.Resolve(WorldValue.Long(v)) is { } target && !ed.LinkExists(target.Target, target.Id))
            yield return new ServerProblem(c.Name, $"{ed.KindText(target.Target)} {target.Id} does not exist" + (target.Target.StartsWith("dbc:", StringComparison.Ordinal) ? " in the game's table." : " on the server.") +
                (IsSpellItemType(t, c.Name) ? " (Only a \"Create item\" effect names an item here; for an aura it is a spell family mask.)" : ""), edited && !IsSpellItemType(t, c.Name));
    }

    /// <summary>The rules VMaNGOS applies to a whole row.</summary>
    public static List<ServerProblem> CheckRow(ServerEditor ed, WorldTable t, string?[] row)
    {
        var res = new List<ServerProblem>();
        string? V(string col) => t.IndexOf(col) is var i and >= 0 ? row[i] : null;
        long L(string col) => WorldValue.Long(V(col));
        double D(string col) => WorldValue.Number(V(col));
        void Err(string col, string msg) => res.Add(new ServerProblem(col, msg, true));
        void Warn(string col, string msg) => res.Add(new ServerProblem(col, msg, false));

        if (t.Has("patch_min") && t.Has("patch_max"))
        {
            if (L("patch_min") > L("patch_max")) Err("patch_min", "patch_min is above patch_max: the server resets both to 0-10 when it loads the row.");
            if (L("patch_max") > 10) Err("patch_max", "patch_max is at most 10 (1.12).");
        }
        if (t.Has("build_min") && t.Has("build_max") && L("build_min") > L("build_max")) Err("build_min", "build_min is above build_max: the row would never be loaded.");

        switch (t.Name.ToLowerInvariant())
        {
            case "item_template":
                if (string.IsNullOrWhiteSpace(V("name"))) Err("name", "An item needs a name.");
                if (L("stackable") == 0) Warn("stackable", "stackable 0: the item cannot be put in a bag slot (1 = not stackable).");
                if (L("buy_count") == 0) Warn("buy_count", "buy_count 0: vendors sell it in stacks of 1 anyway.");
                if (L("sell_price") > L("buy_price") && L("buy_price") > 0) Warn("sell_price", "It sells to a vendor for more than it costs: players can buy and sell it for money.");
                if (L("required_level") > 63) Warn("required_level", "No player can reach this level (the maximum is 60).");
                for (int k = 1; k <= 5; k++)
                    if (t.Has("dmg_min" + k) && D("dmg_min" + k) > D("dmg_max" + k)) Err("dmg_min" + k, $"dmg_min{k} is above dmg_max{k}.");
                for (int k = 1; k <= 10; k++)
                    if (t.Has("stat_value" + k) && L("stat_value" + k) != 0 && L("stat_type" + k) == 0 && t.Has("stat_type" + k))
                        Warn("stat_type" + k, $"stat_value{k} has no stat type (0 = Mana); pick the stat it raises.");
                if (L("display_id") != 0 && !ed.LinkExists("dbc:ItemDisplayInfo", L("display_id"))) Err("display_id", $"Display {L("display_id")} is not in the game's ItemDisplayInfo: the game shows no model or icon.");
                break;
            case "creature_template":
                if (string.IsNullOrWhiteSpace(V("name"))) Err("name", "A creature needs a name.");
                if (L("level_min") > L("level_max")) Err("level_min", "level_min is above level_max.");
                if (L("level_min") < 1) Err("level_min", "The lowest level is 1.");
                if (L("level_max") > 63) Warn("level_max", "Creature levels above 63 are not shown correctly by the 1.12 game.");
                if (Enumerable.Range(1, 4).All(k => L("display_id" + k) == 0)) Err("display_id1", "A creature needs at least one display (display_id1..4): without one it is invisible.");
                for (int k = 1; k <= 4; k++)
                {
                    long d = L("display_id" + k);
                    if (d == 0) continue;
                    if (!ed.LinkExists("dbc:CreatureDisplayInfo", d)) Err("display_id" + k, $"Display {d} is not in the game's CreatureDisplayInfo: VMaNGOS removes it at start (\"can crash client\").");
                    else if (!ed.HasDisplayAddon(d)) Warn("display_id" + k, $"Display {d} has no row in creature_display_info_addon (reach and size): the server logs an error and uses defaults.");
                    if (L("display_probability" + k) == 0 && L("display_total_probability") != 0) Warn("display_probability" + k, $"display_probability{k} is 0 while display_total_probability is set: VMaNGOS drops display_id{k}.");
                }
                if (L("gold_min") > L("gold_max")) Err("gold_min", "gold_min is above gold_max.");
                if ((L("npc_flags") & 0x10) != 0 && L("trainer_type") == 0 && L("trainer_class") == 0) Warn("trainer_class", "A class trainer (trainer_type 0) needs trainer_class.");
                if (L("faction") != 0 && !ed.LinkExists("faction_template", L("faction"))) Err("faction", $"Faction template {L("faction")} does not exist.");
                break;
            case "quest_template":
                if (string.IsNullOrWhiteSpace(V("Title"))) Err("Title", "A quest needs a title.");
                if (L("MinLevel") > L("QuestLevel") && L("QuestLevel") > 0) Warn("MinLevel", "MinLevel is above the quest's level.");
                for (int k = 1; k <= 4; k++)
                {
                    if (L("ReqItemId" + k) != 0 && L("ReqItemCount" + k) == 0) Err("ReqItemCount" + k, $"ReqItemId{k} is set but ReqItemCount{k} is 0: the server ignores the objective.");
                    if (L("ReqItemId" + k) == 0 && L("ReqItemCount" + k) != 0) Warn("ReqItemId" + k, $"ReqItemCount{k} without ReqItemId{k}.");
                    if (L("ReqCreatureOrGOId" + k) != 0 && L("ReqCreatureOrGOCount" + k) == 0) Err("ReqCreatureOrGOCount" + k, $"ReqCreatureOrGOId{k} is set but its count is 0.");
                }
                for (int k = 1; k <= 6; k++)
                    if (L("RewChoiceItemId" + k) != 0 && L("RewChoiceItemCount" + k) == 0) Err("RewChoiceItemCount" + k, $"RewChoiceItemId{k} needs a count.");
                for (int k = 1; k <= 4; k++)
                    if (L("RewItemId" + k) != 0 && L("RewItemCount" + k) == 0) Err("RewItemCount" + k, $"RewItemId{k} needs a count.");
                break;
            case "creature":
                if (L("spawntimesecsmin") > L("spawntimesecsmax")) Err("spawntimesecsmin", "The shortest respawn time is above the longest.");
                break;
            case "gameobject":
                if (L("spawntimesecsmin") > L("spawntimesecsmax") && L("spawntimesecsmin") > 0) Err("spawntimesecsmin", "The shortest respawn time is above the longest.");
                break;
            case "npc_vendor":
            case "npc_vendor_template":
                if (L("maxcount") > 0 && L("incrtime") == 0) Warn("incrtime", "A limited item (maxcount) needs a restock time (incrtime).");
                if (t.Name == "npc_vendor" && ed.CreatureFlags(L("entry")) is { } vf && (vf & 0x4) == 0) Warn("entry", "This creature has no Vendor flag (npc_flags 0x4): players cannot open its shop.");
                break;
            case "npc_trainer":
                if (ed.CreatureFlags(L("entry")) is { } tf && (tf & 0x10) == 0) Warn("entry", "This creature has no Trainer flag (npc_flags 0x10).");
                break;
            case "creature_questrelation":
            case "creature_involvedrelation":
                if (ed.CreatureFlags(L("id")) is { } qf && (qf & 0x2) == 0) Warn("id", "This creature has no Quest giver flag (npc_flags 0x2): players cannot talk to it about quests.");
                break;
            case "spell_template":
                if (string.IsNullOrWhiteSpace(V("name"))) Warn("name", "The spell has no name.");
                for (int k = 1; k <= 3; k++)
                    if (L("effect" + k) == 24 && L("effectItemType" + k) != 0 && !ed.LinkExists("item", L("effectItemType" + k)))
                        Err("effectItemType" + k, $"Item {L("effectItemType" + k)} does not exist: \"Create item\" would create nothing.");
                break;
            case "map_template":
            case "area_template":
            case "taxi_nodes":
            case "faction":
                if (t.Has("name") && string.IsNullOrWhiteSpace(V("name")) || t.Has("map_name") && string.IsNullOrWhiteSpace(V("map_name"))) Warn(t.Has("name") ? "name" : "map_name", "It has no name.");
                break;
        }
        // 0.13.3: VMaNGOS casts no spell above 40000 (MAX_SPELL_ID), the 1.12 game knows none above 65535
        foreach (var (table, col) in SpellIdLimits.SpellColumns)
            if (t.Name.Equals(table, StringComparison.OrdinalIgnoreCase) && t.Has(col) && L(col) is var sid && !SpellIdLimits.Castable(sid) && sid > 0)
                Err(col, SpellIdLimits.Problem(sid));
        if (t.Name.EndsWith("_loot_template", StringComparison.OrdinalIgnoreCase))
        {
            var chance = D("ChanceOrQuestChance");
            var mc = L("mincountOrRef");
            if (chance < -100 || chance > 100) Err("ChanceOrQuestChance", "The chance is a percentage from -100 to 100 (negative: quest drops).");
            if (mc == 0) Err("mincountOrRef", "mincountOrRef 0: VMaNGOS skips the row (use 1 for one item, or -N to include reference loot N).");
            if (mc > 0 && L("maxcount") < mc) Err("maxcount", "maxcount is below the lowest count (mincountOrRef).");
            if (mc < 0 && !ed.LinkExists("reference_loot", -mc)) Err("mincountOrRef", $"Reference loot {-mc} does not exist.");
            if (mc > 0 && L("item") != 0 && !ed.LinkExists("item", L("item"))) Err("item", $"Item {L("item")} does not exist.");
            if (L("groupid") > 0 && chance < 0) Err("groupid", "A quest drop (negative chance) cannot be in a group.");
        }
        // every link of the row (warnings: the server's own data has some dangling ones)
        var info = ed.Service.Info(t.Name);
        if (info != null)
            foreach (var ci in info.Columns.Where(x => x.Link != null))
                if (t.Column(ci.Name) is { } col && LinkApplies(t, row, ci.Name))
                    foreach (var p in Meaning(ed, t, col, ci, V(ci.Name), edited: false))
                        if (!res.Any(x => x.Column == p.Column)) res.Add(p);
        return res;
    }
}
