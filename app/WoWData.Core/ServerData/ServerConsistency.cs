using System.Globalization;
using WoWData.Core.Dbc;

namespace WoWData.Core.ServerData;

/// <summary>One value on both sides: the server's column and value, the game's column and value.</summary>
public sealed record ClientField(string ServerColumn, string? ServerValue, string ClientColumn, string? ClientValue, bool Same)
{
    public string Text => Same ? $"{ServerColumn} = {ServerValue}" : $"{ServerColumn}: server {Show(ServerValue)}, game {ClientColumn} {Show(ClientValue)}";
    private static string Show(string? v) => v == null ? "(none)" : v.Length == 0 ? "(empty)" : v.Length > 50 ? v[..47] + "..." : v;
}

/// <summary>What kind of difference a check found.</summary>
public enum ConsistencyKind
{
    /// <summary>The row exists on both sides with different values.</summary>
    Differs,
    /// <summary>Only the server has the row.</summary>
    ServerOnly,
    /// <summary>Only the game has the row.</summary>
    ClientOnly,
    /// <summary>A server row names a game row that does not exist (an item's display, a creature's look).</summary>
    MissingInGame,
    /// <summary>The server lacks its own facts about a game row it uses (creature_display_info_addon).</summary>
    MissingOnServer,
}

/// <summary>One difference between the game and the server.</summary>
public sealed record ConsistencyIssue(string Area, ConsistencyKind Kind, string ServerTable, string? ServerKey, string ClientTable, long Id, string Name,
                                      IReadOnlyList<ClientField> Fields, bool CanMatchServer, bool CanMatchClient, string Advice)
{
    public string KindText => Kind switch
    {
        ConsistencyKind.Differs => $"{Fields.Count(f => !f.Same)} value(s) differ",
        ConsistencyKind.ServerOnly => "only on the server",
        ConsistencyKind.ClientOnly => "only in the game",
        ConsistencyKind.MissingInGame => "names a row the game does not have",
        _ => "the server lacks its own row",
    };

    public string Text => $"{Area}: {ClientTable} {Id} ({Name}): {KindText}" + (Kind == ConsistencyKind.Differs ? ": " + string.Join("; ", Fields.Where(f => !f.Same).Take(4).Select(f => f.Text)) : "") + (Advice.Length > 0 ? ". " + Advice : "");
}

/// <summary>
/// 0.9.0: the game and the server keep some data twice. VMaNGOS reads spells, maps, areas, factions, faction templates,
/// flight points and skill lines from its database while the game reads its DBC files; items name their look in the
/// game's ItemDisplayInfo and creatures theirs in CreatureDisplayInfo. These checks put both sides next to each other,
/// flag every difference and make them match in either direction, as edits in your workspace: "Make the server match
/// the game" edits the server's rows (save, then apply), "Make the game match the server" edits the game's tables (they
/// go into your workspace at once, like a Data tables edit; test them or build the release patch).
/// </summary>
public static class ServerConsistency
{
    /// <summary>How a server column and a game column correspond.</summary>
    public enum MapKind { Same, LocText, LocFlags, MaskLow, MaskHigh }

    /// <summary>A server column and its game column; <paramref name="Tolerance"/>: decimal numbers this close count as the
    /// same (positions: the server keeps them with fewer decimals).</summary>
    public sealed record FieldMap(string Server, string Client, MapKind Kind = MapKind.Same, double Tolerance = 0);

    public sealed record TableMap(string Area, string ServerTable, string ClientTable, string ServerId, IReadOnlyList<FieldMap> Fields, string Note = "");

    public static readonly IReadOnlyList<string> Areas = new[] { "spells", "items", "creatures", "maps", "areas", "factions", "faction-templates", "flight-paths", "skills" };

    private static List<FieldMap> SpellFields()
    {
        var list = new List<FieldMap>
        {
            new("school", "School"), new("category", "Category"), new("castUI", "CastUI"), new("dispel", "Dispel"), new("mechanic", "Mechanic"),
            new("attributes", "Attributes"), new("attributesEx", "AttributesEx"), new("attributesEx2", "AttributesEx2"), new("attributesEx3", "AttributesEx3"), new("attributesEx4", "AttributesEx4"),
            new("stances", "Stances"), new("stancesNot", "StancesNot"), new("targets", "Targets"), new("targetCreatureType", "TargetCreatureType"), new("requiresSpellFocus", "RequiresSpellFocus"),
            new("casterAuraState", "CasterAuraState"), new("targetAuraState", "TargetAuraState"), new("castingTimeIndex", "CastingTimeIndex"), new("recoveryTime", "RecoveryTime"),
            new("categoryRecoveryTime", "CategoryRecoveryTime"), new("interruptFlags", "InterruptFlags"), new("auraInterruptFlags", "AuraInterruptFlags"), new("channelInterruptFlags", "ChannelInterruptFlags"),
            new("procFlags", "ProcFlags"), new("procChance", "ProcChance"), new("procCharges", "ProcCharges"), new("maxLevel", "MaxLevel"), new("baseLevel", "BaseLevel"), new("spellLevel", "SpellLevel"),
            new("durationIndex", "DurationIndex"), new("powerType", "PowerType"), new("manaCost", "ManaCost"), new("manCostPerLevel", "ManaCostPerLevel"), new("manaPerSecond", "ManaPerSecond"),
            new("manaPerSecondPerLevel", "ManaPerSecondPerLevel"), new("rangeIndex", "RangeIndex"), new("speed", "Speed"), new("modelNextSpell", "ModalNextSpell"), new("stackAmount", "StackAmount"),
            new("equippedItemClass", "EquippedItemClass"), new("equippedItemSubClassMask", "EquippedItemSubClassMask"), new("equippedItemInventoryTypeMask", "EquippedItemInventoryTypeMask"),
            new("spellVisual1", "SpellVisual"), new("spellVisual2", "SpellVisual2"), new("spellIconId", "SpellIconID"), new("activeIconId", "ActiveIconID"), new("spellPriority", "SpellPriority"),
            // the *Flags columns (which languages have a text) are not compared: VMaNGOS keeps the masks of older builds
            new("name", "Name", MapKind.LocText), new("nameSubtext", "NameSubtext", MapKind.LocText),
            new("description", "Description", MapKind.LocText), new("auraDescription", "AuraDescription", MapKind.LocText),
            new("manaCostPercentage", "ManaCostPct"), new("startRecoveryCategory", "StartRecoveryCategory"), new("startRecoveryTime", "StartRecoveryTime"), new("maxTargetLevel", "MaxTargetLevel"),
            new("spellFamilyName", "SpellClassSet"), new("spellFamilyFlags", "SpellClassMask1", MapKind.MaskLow), new("spellFamilyFlags", "SpellClassMask2", MapKind.MaskHigh),
            new("maxAffectedTargets", "MaxTargets"), new("dmgClass", "DefenseType"), new("preventionType", "PreventionType"), new("stanceBarOrder", "StanceBarOrder"),
            new("minFactionId", "MinFactionID"), new("minReputation", "MinReputation"), new("requiredAuraVision", "RequiredAuraVision"),
        };
        for (int i = 1; i <= 2; i++) list.Add(new($"totem{i}", $"Totem{i}"));
        for (int i = 1; i <= 8; i++)
        {
            list.Add(new($"reagent{i}", $"Reagent{i}"));
            list.Add(new($"reagentCount{i}", $"ReagentCount{i}"));
        }
        for (int i = 1; i <= 3; i++)
        {
            list.Add(new($"effect{i}", $"Effect{i}"));
            list.Add(new($"effectDieSides{i}", $"EffectDieSides{i}"));
            list.Add(new($"effectBaseDice{i}", $"EffectBaseDice{i}"));
            list.Add(new($"effectDicePerLevel{i}", $"EffectDicePerLevel{i}"));
            list.Add(new($"effectRealPointsPerLevel{i}", $"EffectRealPointsPerLevel{i}"));
            list.Add(new($"effectBasePoints{i}", $"EffectBasePoints{i}"));
            list.Add(new($"effectMechanic{i}", $"EffectMechanic{i}"));
            list.Add(new($"effectImplicitTargetA{i}", $"EffectImplicitTargetA{i}"));
            list.Add(new($"effectImplicitTargetB{i}", $"EffectImplicitTargetB{i}"));
            list.Add(new($"effectRadiusIndex{i}", $"EffectRadiusIndex{i}"));
            list.Add(new($"effectApplyAuraName{i}", $"EffectAura{i}"));
            list.Add(new($"effectAmplitude{i}", $"EffectAuraPeriod{i}"));
            list.Add(new($"effectMultipleValue{i}", $"EffectMultipleValue{i}"));
            list.Add(new($"effectChainTarget{i}", $"EffectChainTargets{i}"));
            list.Add(new($"effectItemType{i}", $"EffectItemType{i}"));
            list.Add(new($"effectMiscValue{i}", $"EffectMiscValue{i}"));
            list.Add(new($"effectTriggerSpell{i}", $"EffectTriggerSpell{i}"));
            list.Add(new($"effectPointsPerComboPoint{i}", $"EffectPointsPerCombo{i}"));
            list.Add(new($"dmgMultiplier{i}", $"EffectChainAmplitude{i}"));
        }
        return list;
    }

    private static List<FieldMap> Numbered(string server, string client, int n) => Enumerable.Range(1, n).Select(i => new FieldMap(server + i, client + i)).ToList();

    public static readonly IReadOnlyList<TableMap> Maps = new List<TableMap>
    {
        new("spells", "spell_template", "Spell", "entry", SpellFields(),
            "VMaNGOS computes spells from spell_template; the game shows names, icons, tooltips and cast bars from Spell.dbc. The server's extra columns (effectBonusCoefficient, customFlags) have no game counterpart."),
        // player_limit is the server's own setting (the 1.12 Map.dbc has 0 for most instances): not compared
        new("maps", "map_template", "Map", "entry", new FieldMap[] { new("map_name", "MapName", MapKind.LocText), new("map_type", "InstanceType"), new("linked_zone", "AreaTableID") }),
        new("areas", "area_template", "AreaTable", "entry", new FieldMap[]
        {
            new("map_id", "MapID"), new("zone_id", "ParentAreaID"), new("explore_flag", "AreaBit"), new("flags", "Flags"), new("area_level", "ExplorationLevel"),
            new("name", "AreaName", MapKind.LocText), new("team", "FactionGroupMask"),
        }),
        new("factions", "faction", "Faction", "id", new FieldMap[] { new("reputation_list_id", "ReputationIndex"), new("team", "ParentFactionID"), new("name", "Name", MapKind.LocText), new("description", "Description", MapKind.LocText) }
            .Concat(Numbered("base_rep_race_mask", "ReputationRaceMask", 4)).Concat(Numbered("base_rep_class_mask", "ReputationClassMask", 4))
            .Concat(Numbered("base_rep_value", "ReputationBase", 4)).Concat(Numbered("reputation_flags", "ReputationFlags", 4)).ToList()),
        new("faction-templates", "faction_template", "FactionTemplate", "id", new FieldMap[]
            {
                new("faction_id", "Faction"), new("faction_flags", "Flags"), new("our_mask", "FactionGroup"), new("friendly_mask", "FriendGroup"), new("hostile_mask", "EnemyGroup"),
            }.Concat(Numbered("enemy_faction", "Enemies", 4)).Concat(Numbered("friend_faction", "Friend", 4)).ToList()),
        new("flight-paths", "taxi_nodes", "TaxiNodes", "id", new FieldMap[]
        {
            new("map_id", "ContinentID"), new("x", "X", Tolerance: 0.05), new("y", "Y", Tolerance: 0.05), new("z", "Z", Tolerance: 0.05), new("name", "Name", MapKind.LocText), new("mount_creature_id1", "MountCreatureID1"), new("mount_creature_id2", "MountCreatureID2"),
        }),
        new("skills", "skill_line_ability", "SkillLineAbility", "id", new FieldMap[]
        {
            new("skill_id", "SkillLine"), new("spell_id", "Spell"), new("race_mask", "RaceMask"), new("class_mask", "ClassMask"), new("req_skill_value", "MinSkillLineRank"),
            new("superseded_by_spell", "SupercededBySpell"), new("learn_on_get_skill", "AcquireMethod"), new("max_value", "TrivialSkillLineRankHigh"), new("min_value", "TrivialSkillLineRankLow"),
            new("req_train_points", "ReqTrainPoints"),
        }),
        new("items", "item_display_info", "ItemDisplayInfo", "ID", new FieldMap[] { new("icon", "InventoryIcon") },
            "item_display_info is the repack's copy of the display icons (its tools read it, mangosd does not)."),
    };

    public static TableMap? MapOf(string serverTable) => Maps.FirstOrDefault(m => string.Equals(m.ServerTable, serverTable, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ values

    /// <summary>The game's value of a mapped column as the server would store it.</summary>
    private static string? ClientValue(DbcTable t, DbcRow r, DbcColumn c, MapKind kind)
    {
        switch (kind)
        {
            case MapKind.LocText: return t.Text(r, c, 0);
            case MapKind.LocFlags: return t.Raw(r, DbcTable.LocFlagsField(c)).ToString(CultureInfo.InvariantCulture);
            case MapKind.MaskLow:
            case MapKind.MaskHigh: return t.Raw(r, c.Field).ToString(CultureInfo.InvariantCulture);
            default: return t.Format(r, c, 0);
        }
    }

    private static bool SameValue(WorldColumn sc, string? server, DbcColumn cc, string? client, MapKind kind, string?[] serverRow, WorldTable st, double tolerance = 0)
    {
        if (tolerance > 0 && server != null && client != null) return Math.Abs(WorldValue.Number(server) - WorldValue.Number(client)) <= tolerance;
        if (server == null || client == null) return server == client;
        if (kind is MapKind.MaskLow or MapKind.MaskHigh)
        {
            ulong v = ulong.TryParse(server, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
            uint part = kind == MapKind.MaskLow ? (uint)(v & 0xFFFFFFFF) : (uint)(v >> 32);
            return part.ToString(CultureInfo.InvariantCulture) == client;
        }
        if (cc.IsString) return string.Equals(server, client, StringComparison.Ordinal);
        if (cc.IsFloat || sc.Type is WorldType.Float or WorldType.Double)
            return float.TryParse(server, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && float.TryParse(client, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
        // whole numbers: the same 32 bits (a signed -1 and an unsigned 4294967295 are the same value)
        return (WorldValue.Long(server) & 0xFFFFFFFFL) == (WorldValue.Long(client) & 0xFFFFFFFFL);
    }

    /// <summary>The server's value for a game value (for "Make the server match the game").</summary>
    private static string? ToServer(WorldColumn sc, string? client, MapKind kind, string? currentServer)
    {
        if (client == null) return null;
        if (kind is MapKind.MaskLow or MapKind.MaskHigh)
        {
            ulong v = ulong.TryParse(currentServer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
            ulong part = uint.Parse(client, CultureInfo.InvariantCulture);
            v = kind == MapKind.MaskLow ? (v & 0xFFFFFFFF00000000UL) | part : (v & 0xFFFFFFFFUL) | (part << 32);
            return v.ToString(CultureInfo.InvariantCulture);
        }
        if (sc.Type == WorldType.Integer)
        {
            long l = WorldValue.Long(client);
            var (min, max) = sc.IntegerRange;
            if (l < (long)min && sc.Unsigned) l &= 0xFFFFFFFFL;
            if (l > (long)max && !sc.Unsigned && max == int.MaxValue) l = unchecked((int)l);
            return l.ToString(CultureInfo.InvariantCulture);
        }
        return client;
    }

    /// <summary>The game's value for a server value (for "Make the game match the server").</summary>
    private static string ToClient(DbcColumn cc, string? server, MapKind kind)
    {
        if (server == null) return "";
        if (kind is MapKind.MaskLow or MapKind.MaskHigh)
        {
            ulong v = ulong.TryParse(server, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
            return (kind == MapKind.MaskLow ? (uint)(v & 0xFFFFFFFF) : (uint)(v >> 32)).ToString(CultureInfo.InvariantCulture);
        }
        if (cc.IsNumber && !cc.IsFloat)
        {
            long l = WorldValue.Long(server);
            if (cc.IsSigned && l > int.MaxValue) l = unchecked((int)(uint)l);
            if (!cc.IsSigned && l < 0) l &= 0xFFFFFFFFL;
            return l.ToString(CultureInfo.InvariantCulture);
        }
        return server;
    }

    private static DbcTable? Client(ServerEditor ed, string table)
    {
        try { return ed.Service.Dbc?.Lookup(table); }
        catch (Exception e) when (e is DbcException or IOException) { return null; }
    }

    private static List<ClientField> Compare(TableMap map, WorldTable st, string?[] srow, DbcTable ct, DbcRow? crow)
    {
        var res = new List<ClientField>();
        foreach (var f in map.Fields)
        {
            var sc = st.Column(f.Server);
            var cc = ct.Layout.Columns.FirstOrDefault(c => string.Equals(c.Name, f.Client, StringComparison.OrdinalIgnoreCase));
            if (sc == null || cc == null) continue;
            var sv = srow[st.IndexOf(sc.Name)];
            var cv = crow == null ? null : ClientValue(ct, crow, cc, f.Kind);
            string shown = f.Kind switch
            {
                MapKind.MaskLow => sc.Name + " (low 32 bits)",
                MapKind.MaskHigh => sc.Name + " (high 32 bits)",
                _ => sc.Name,
            };
            string? svShown = f.Kind switch
            {
                MapKind.MaskLow => ((ulong.TryParse(sv, out var u) ? u : 0) & 0xFFFFFFFF).ToString(CultureInfo.InvariantCulture),
                MapKind.MaskHigh => ((ulong.TryParse(sv, out var u2) ? u2 : 0) >> 32).ToString(CultureInfo.InvariantCulture),
                _ => sv,
            };
            res.Add(new ClientField(shown, svShown, f.Kind == MapKind.LocFlags ? cc.Name + " (flags)" : cc.Name, cv, crow != null && SameValue(sc, sv, cc, cv, f.Kind, srow, st, f.Tolerance)));
        }
        return res;
    }

    /// <summary>The game's side of one server row (for the details panel), or an empty list when the game has no such
    /// table or the row has no counterpart there.</summary>
    public static (List<ClientField> Fields, string ClientTable) ClientSide(ServerEditor ed, WorldTable t, string?[] row)
    {
        var map = MapOf(t.Name);
        if (map == null) return (new(), "");
        var ct = Client(ed, map.ClientTable);
        if (ct == null) return (new(), "");
        var id = WorldValue.Long(row[t.IndexOf(map.ServerId)]);
        return (Compare(map, t, row, ct, ct.Find(id)), map.ClientTable);
    }

    // ------------------------------------------------------------------ the checks

    /// <summary>Every difference of an area ("spells", "items", ...; null = all), optionally only for some IDs.</summary>
    public static List<ConsistencyIssue> Check(ServerEditor ed, string? area = null, IReadOnlyCollection<long>? ids = null, int max = 5000)
    {
        var res = new List<ConsistencyIssue>();
        foreach (var a in area == null ? Areas : new[] { area })
        {
            if (!Areas.Contains(a)) throw new ServerDataException($"no check \"{a}\". The checks: {string.Join(", ", Areas)}.");
            if (a == "creatures") res.AddRange(CheckCreatures(ed, ids, max));
            if (a == "items") res.AddRange(CheckItems(ed, ids, max));
            var map = Maps.FirstOrDefault(m => m.Area == a);
            if (map != null) res.AddRange(CheckTable(ed, map, ids, max));
            if (res.Count >= max) break;
        }
        return res.Take(max).ToList();
    }

    private static List<ConsistencyIssue> CheckTable(ServerEditor ed, TableMap map, IReadOnlyCollection<long>? ids, int max)
    {
        var res = new List<ConsistencyIssue>();
        var st = ed.Schema.Table(map.ServerTable);
        var ct = Client(ed, map.ClientTable);
        if (st == null || ct == null) return res;
        var svc = ed.Service;
        var filter = ids == null ? RowFilter.All() : RowFilter.Where(new FilterTerm(map.ServerId, FilterOp.In, null, ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList()));
        var rows = ed.Rows(st, filter);
        int ii = st.IndexOf(map.ServerId);
        var effective = rows.GroupBy(r => WorldValue.Long(r[ii])).Select(g => svc.Effective(st, g.ToList())).Where(r => r != null).Cast<string?[]>().ToDictionary(r => WorldValue.Long(r[ii]));
        string Name(string?[]? srow, DbcRow? crow)
        {
            var info = svc.Info(st.Name);
            var lc = info?.Label;
            if (srow != null && lc != null && st.Has(lc) && srow[st.IndexOf(lc)] is { Length: > 0 } n) return ServerEditor.Short(n, 50);
            return crow != null ? ServerEditor.Short(ct.Label(crow), 50) : "";
        }
        foreach (var (id, srow) in effective.OrderBy(kv => kv.Key))
        {
            if (res.Count >= max) return res;
            var crow = ct.Find(id);
            if (crow == null)
            {
                res.Add(new ConsistencyIssue(map.Area, ConsistencyKind.ServerOnly, st.Name, st.KeyText(srow), ct.Name, id, Name(srow, null), Compare(map, st, srow, ct, null),
                    false, ct.Layout.HasKey, $"The game's {ct.Name} has no row {id}: the game does not know it" + (map.Area == "spells" ? " (no name, icon or tooltip)." : ".")));
                continue;
            }
            var fields = Compare(map, st, srow, ct, crow);
            if (fields.Any(f => !f.Same))
                res.Add(new ConsistencyIssue(map.Area, ConsistencyKind.Differs, st.Name, st.KeyText(srow), ct.Name, id, Name(srow, crow), fields, true, true, ""));
        }
        // item_display_info is only a copy for the repack's tools: the displays it lacks are not differences
        if ((ids == null || ids.Count > 0) && map.Area != "items")
            foreach (var crow in ct.Rows)
            {
                if (res.Count >= max) return res;
                var id = ct.Key(crow) ?? 0;
                if (ids != null && !ids.Contains(id)) continue;
                if (effective.ContainsKey(id)) continue;
                res.Add(new ConsistencyIssue(map.Area, ConsistencyKind.ClientOnly, st.Name, null, ct.Name, id, Name(null, crow), Compare(map, st, new string?[st.Columns.Count], ct, crow).Select(f => f with { ServerValue = null, Same = false }).ToList(),
                    true, false, $"The server has no {st.Name} row {id}" + (map.Area == "spells" ? ": casting it does nothing on the server." : ".")));
            }
        return res;
    }

    private static List<ConsistencyIssue> CheckItems(ServerEditor ed, IReadOnlyCollection<long>? ids, int max)
    {
        var res = new List<ConsistencyIssue>();
        var it = ed.Schema.Table("item_template");
        var ct = Client(ed, "ItemDisplayInfo");
        if (it == null || ct == null) return res;
        var filter = ids == null ? RowFilter.Where(new FilterTerm("display_id", FilterOp.Ne, "0")) : RowFilter.Where(new FilterTerm("entry", FilterOp.In, null, ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList()));
        foreach (var g in ed.Rows(it, filter).GroupBy(r => WorldValue.Long(r[it.IndexOf("entry")])))
        {
            if (res.Count >= max) break;
            var row = ed.Service.Effective(it, g.ToList());
            if (row == null) continue;
            long d = WorldValue.Long(row[it.IndexOf("display_id")]);
            if (d != 0 && ct.Find(d) == null)
                res.Add(new ConsistencyIssue("items", ConsistencyKind.MissingInGame, it.Name, it.KeyText(row), ct.Name, g.Key, ServerEditor.Short(row[it.IndexOf("name")] ?? "", 50),
                    new List<ClientField> { new("display_id", d.ToString(CultureInfo.InvariantCulture), "ID", null, false) }, false, false,
                    $"Display {d} is not in the game's ItemDisplayInfo: the item has no model or icon in the game. Choose an existing display (display_id), or add display {d} in the Data tables tab."));
        }
        return res;
    }

    private static List<ConsistencyIssue> CheckCreatures(ServerEditor ed, IReadOnlyCollection<long>? ids, int max)
    {
        var res = new List<ConsistencyIssue>();
        var t = ed.Schema.Table("creature_template");
        var ct = Client(ed, "CreatureDisplayInfo");
        if (t == null || ct == null) return res;
        var addon = ed.Schema.Table("creature_display_info_addon");
        var filter = ids == null ? RowFilter.All() : RowFilter.Where(new FilterTerm("entry", FilterOp.In, null, ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList()));
        var rows = ed.Rows(t, filter);
        HashSet<long>? addons = null;
        if (addon != null)
        {
            var wanted = rows.SelectMany(r => Enumerable.Range(1, 4).Select(k => WorldValue.Long(r[t.IndexOf("display_id" + k)]))).Where(d => d != 0).Distinct().Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
            addons = new HashSet<long>();
            foreach (var chunk in wanted.Chunk(2000))
                foreach (var a in ed.Rows(addon, RowFilter.Where(new FilterTerm("display_id", FilterOp.In, null, chunk.ToList()))))
                    addons.Add(WorldValue.Long(a[addon.IndexOf("display_id")]));
        }
        var missingAddon = new HashSet<long>();
        foreach (var g in rows.GroupBy(r => WorldValue.Long(r[t.IndexOf("entry")])))
        {
            if (res.Count >= max) break;
            var row = ed.Service.Effective(t, g.ToList());
            if (row == null) continue;
            var name = ServerEditor.Short(row[t.IndexOf("name")] ?? "", 50);
            foreach (var col in new[] { "display_id1", "display_id2", "display_id3", "display_id4", "mount_display_id" })
            {
                long d = WorldValue.Long(row[t.IndexOf(col)]);
                if (d == 0) continue;
                if (ct.Find(d) == null)
                    res.Add(new ConsistencyIssue("creatures", ConsistencyKind.MissingInGame, t.Name, t.KeyText(row), ct.Name, g.Key, name,
                        new List<ClientField> { new(col, d.ToString(CultureInfo.InvariantCulture), "ID", null, false) }, false, false,
                        $"Display {d} is not in the game's CreatureDisplayInfo: VMaNGOS removes it (\"can crash client\"). Choose an existing display, or add display {d} in the Data tables tab (and copy the table to the server)."));
                else if (addons != null && col != "mount_display_id" && !addons.Contains(d) && missingAddon.Add(d))
                    res.Add(new ConsistencyIssue("creatures", ConsistencyKind.MissingOnServer, "creature_display_info_addon", null, ct.Name, d, $"display of {name}",
                        new List<ClientField> { new("display_id", null, "ID", d.ToString(CultureInfo.InvariantCulture), false) }, true, false,
                        "The server has no creature_display_info_addon row for this look (reach, size, gender): it logs an error at start and uses defaults. \"Make the server match the game\" adds one."));
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ make them match

    /// <summary>"Make the server match the game": edits the server's row (in the editor; save it afterwards).</summary>
    public static ServerEditResult MatchServer(ServerEditor ed, ConsistencyIssue issue)
    {
        if (!issue.CanMatchServer) return ServerEditResult.Refused("This difference cannot be fixed on the server side automatically: " + issue.Advice);
        if (issue.Kind == ConsistencyKind.MissingOnServer)
        {
            var (k, r) = ed.Add("creature_display_info_addon", new (string, string?)[]
            {
                ("display_id", issue.Id.ToString(CultureInfo.InvariantCulture)), ("build", "0"), ("bounding_radius", "0.306"), ("combat_reach", "1.5"), ("gender", "2"),
            }, issue.Id, $"Add the server's facts about display {issue.Id}");
            return r;
        }
        var map = MapOf(issue.ServerTable) ?? throw new ServerDataException($"{issue.ServerTable} has no game counterpart.");
        var st = ed.Table(map.ServerTable);
        var ct = Client(ed, map.ClientTable) ?? throw new ServerDataException($"The game's {map.ClientTable} cannot be read.");
        var crow = ct.Find(issue.Id) ?? throw new ServerDataException($"The game's {map.ClientTable} has no row {issue.Id}.");
        var values = new List<(string, string?)>();
        string?[] current = issue.ServerKey != null ? ed.Current(st, issue.ServerKey) ?? new string?[st.Columns.Count] : new string?[st.Columns.Count];
        foreach (var f in map.Fields)
        {
            var sc = st.Column(f.Server);
            var cc = ct.Layout.Columns.FirstOrDefault(c => string.Equals(c.Name, f.Client, StringComparison.OrdinalIgnoreCase));
            if (sc == null || cc == null) continue;
            var cur = current[st.IndexOf(sc.Name)];
            var v = ToServer(sc, ClientValue(ct, crow, cc, f.Kind), f.Kind, cur);
            current[st.IndexOf(sc.Name)] = v;
            if (!values.Any(x => x.Item1 == sc.Name)) values.Add((sc.Name, v));
            else values[values.FindIndex(x => x.Item1 == sc.Name)] = (sc.Name, v);
        }
        if (issue.ServerKey != null) return ed.Set(st.Name, issue.ServerKey, values, $"Make {st.Name} {issue.Id} match the game");
        values.Insert(0, (map.ServerId, issue.Id.ToString(CultureInfo.InvariantCulture)));
        var (_, res) = ed.Add(st.Name, values, issue.Id, $"Add {st.Name} {issue.Id} from the game");
        return res;
    }

    /// <summary>
    /// 0.12.0 (the designers): make the server's row of a client row match it, from a given client table (the designers'
    /// table with its unsaved edits, or the saved one). Adds the row (with the same ID) when the server has none. Extra
    /// server-only values (spell_template's effectBonusCoefficient1-3 and customFlags...) are set too. The edit goes into
    /// the server editor (save it afterwards).
    /// </summary>
    public static ServerEditResult SyncFromClient(ServerEditor ed, string serverTable, DbcTable ct, long id, IEnumerable<(string Column, string? Text)>? extra = null)
    {
        var map = MapOf(serverTable) ?? throw new ServerDataException($"{serverTable} has no game counterpart.");
        var st = ed.Table(map.ServerTable);
        var crow = ct.Find(id) ?? throw new ServerDataException($"The game's {ct.Name} has no row {id}.");
        var existing = ed.Object(st, id);
        string?[] current = existing != null ? (string?[])existing.Clone() : new string?[st.Columns.Count];
        var values = new List<(string, string?)>();
        foreach (var f in map.Fields)
        {
            var sc = st.Column(f.Server);
            var cc = ct.Layout.Columns.FirstOrDefault(c => string.Equals(c.Name, f.Client, StringComparison.OrdinalIgnoreCase));
            if (sc == null || cc == null) continue;
            var v = ToServer(sc, ClientValue(ct, crow, cc, f.Kind), f.Kind, current[st.IndexOf(sc.Name)]);
            current[st.IndexOf(sc.Name)] = v;
            int at = values.FindIndex(x => x.Item1 == sc.Name);
            if (at < 0) values.Add((sc.Name, v));
            else values[at] = (sc.Name, v);
        }
        foreach (var (c, v) in extra ?? Array.Empty<(string, string?)>())
        {
            if (st.Column(c) == null) continue;
            int at = values.FindIndex(x => string.Equals(x.Item1, c, StringComparison.OrdinalIgnoreCase));
            if (at < 0) values.Add((c, v));
            else values[at] = (c, v);
        }
        if (existing != null) return ed.Set(st.Name, st.KeyText(existing), values, $"Make {st.Name} {id} match the game");
        values.RemoveAll(x => string.Equals(x.Item1, map.ServerId, StringComparison.OrdinalIgnoreCase));
        values.Insert(0, (map.ServerId, id.ToString(CultureInfo.InvariantCulture)));
        var (_, res) = ed.Add(st.Name, values, id, $"Add {st.Name} {id} from the game");
        return res;
    }

    /// <summary>"Make the game match the server": edits the game's table and saves it into the workspace (one Data tables
    /// edit). Returns what was done.</summary>
    public static string MatchClient(ServerEditor ed, ConsistencyIssue issue)
    {
        if (!issue.CanMatchClient) throw new ServerDataException("This difference cannot be fixed on the game's side automatically: " + issue.Advice);
        var dbc = ed.Service.Dbc ?? throw new ServerDataException("The game's tables are not available (no game folder is open).");
        var map = MapOf(issue.ServerTable) ?? throw new ServerDataException($"{issue.ServerTable} has no game counterpart.");
        var st = ed.Table(map.ServerTable);
        var srow = issue.ServerKey != null ? ed.Current(st, issue.ServerKey) : null;
        if (srow == null) throw new ServerDataException($"The server has no {st.Name} row {issue.Id}.");
        // the server's texts are the English ones: the game's table is edited in enUS
        var session = new DbcEditSession(dbc, map.ClientTable, 0);
        var row = session.Row(issue.Id);
        string added = "";
        if (row == null)
        {
            (row, added) = session.AddRow(issue.Id);
            added += " ";
        }
        var problems = new List<string>();
        int changed = 0;
        foreach (var f in map.Fields)
        {
            var sc = st.Column(f.Server);
            var cc = session.Column(f.Client);
            if (sc == null || cc == null || f.Kind == MapKind.LocFlags) continue;
            var r = session.Edit(row, cc, ToClient(cc, srow[st.IndexOf(sc.Name)], f.Kind));
            if (!r.Ok) problems.Add($"{cc.Name}: {r.Message}");
            else if (r.Changed > 0) changed++;
        }
        if (problems.Count > 0) throw new ServerDataException($"The game's {map.ClientTable} refused {problems.Count} value(s): " + string.Join("; ", problems.Take(5)) + ". Nothing was saved.");
        var (rec, msg) = session.Save(session.CompareWithGame());
        return $"{added}{map.ClientTable} {issue.Id}: {changed} value(s) set from the server's {st.Name}. {msg}";
    }
}
