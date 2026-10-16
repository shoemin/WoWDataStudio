using System.Globalization;
using WoWData.Core.Dbc;

namespace WoWData.Core.ServerData;

/// <summary>One view of the Server data tab: a kind of thing (items, creatures, quests...) and its tables.</summary>
public sealed record ServerViewDef(string Id, string Label, string About, IReadOnlyList<string> Tables);

/// <summary>One line of a view's list.</summary>
public sealed record ServerListItem(string Table, string Key, long Id, string Name, string Info, bool Loaded = true)
{
    public string Text => $"{Id} · {Name}" + (Info.Length > 0 ? $" ({Info})" : "");
}

/// <summary>One value of a record, with what it means and where it links.</summary>
public sealed class ServerFieldValue
{
    public WorldColumn Column { get; init; } = null!;
    public ServerColumnInfo? Info { get; init; }
    public string? Value { get; init; }
    /// <summary>The value for people: "3 · Rare", "2600 (2.6 s)", "25 · Worn Shortsword".</summary>
    public string Display { get; init; } = "";
    public string? LinkTarget { get; init; }
    public long LinkId { get; init; }
    public bool LinkMissing { get; init; }
    public string Problem { get; init; } = "";
    public bool IsKey { get; init; }
    public string Name => Column.Name;
    public string Group => Info?.Group is { Length: > 0 } g ? g : Info == null ? "Not described yet (shown as stored)" : "Values";
    public bool Described => Info != null;
    public bool Verified => Info?.Verified ?? false;
    public string Hint => Info?.Hint is { Length: > 0 } h ? h
        : Info == null ? "WoW Data Studio does not describe this column yet: the value is shown and edited as the database stores it." + (Column.Comment.Length > 0 ? " The database says: " + Column.Comment : "")
        : Column.Comment;
    public string TypeText => Column.ColumnType + (Column.Nullable ? ", may be NULL" : "");
}

/// <summary>A list of related rows in a record's details (its spawns, its loot, the quests it starts...).</summary>
public sealed class ServerSection
{
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public WorldTable Table { get; init; } = null!;
    public RowFilter Filter { get; init; } = RowFilter.All();
    public List<string?[]> Rows { get; init; } = new();
    /// <summary>The columns shown in the list (all columns are in the row).</summary>
    public List<string> Columns { get; init; } = new();
    /// <summary>The values a new row of this section gets ("Add row" here: entry = the creature...).</summary>
    public Dictionary<string, string?> AddDefaults { get; init; } = new();
    public bool Truncated { get; init; }
    public List<string> RowTexts { get; init; } = new();
}

/// <summary>A row of another table that links to a record.</summary>
public sealed record ServerUsedBy(string Table, string Column, string Key, string Label)
{
    public string Text => $"{Table} {Key} · {Label} (column {Column})";
}

/// <summary>A record of the server's database with everything the details panel shows.</summary>
public sealed class ServerRecord
{
    public WorldTable Table { get; init; } = null!;
    public ServerTableInfo? Info { get; init; }
    public string Key { get; init; } = "";
    public string?[] Row { get; init; } = Array.Empty<string?>();
    public string?[]? ServerRow { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string VersionText { get; init; } = "";
    public bool Loaded { get; init; } = true;
    public List<ServerListItem> Versions { get; init; } = new();
    public List<ServerFieldValue> Fields { get; init; } = new();
    public List<ServerSection> Sections { get; init; } = new();
    public List<ServerUsedBy> UsedBy { get; init; } = new();
    public List<ServerProblem> Problems { get; init; } = new();
    public List<ClientField> Client { get; init; } = new();
    public string ClientTable { get; init; } = "";
    /// <summary>A texture the record shows (an item's or spell's icon), or null.</summary>
    public string? Icon { get; init; }
    /// <summary>The Browse tab's entry with the 3D model ("creature:3098", "item:25", "go:1618"), or null.</summary>
    public string? BrowseKey { get; init; }
    public bool IsEdited => ServerRow == null || !Row.SequenceEqual(ServerRow);
    public string ReloadText => Info?.CanReload == true ? $"After applying: \"{Info.Reload}\" in the game as a GM, or restart the world server." : "After applying: restart the world server (it reads this table only when it starts).";
}

/// <summary>
/// 0.9.0: the friendly views of the server's world database: items, creatures, quests, game objects, spells, loot
/// tables, vendors, trainers, areas, maps, flight points, factions, languages and every other table raw. A record shows
/// every column (named, typed, with links and hints), the rows that belong to it (spawns, loot, vendor and trainer
/// lists, quest givers and enders, chains, equipment, gossip, languages...), the rows that link to it ("Used by") and,
/// where the game has the same data, the game's values side by side. Everything comes through the
/// <see cref="ServerEditor"/>, so your edits are shown.
/// </summary>
public sealed class ServerBrowser
{
    public ServerBrowser(ServerEditor editor) { Ed = editor; }

    public ServerEditor Ed { get; }
    private ServerDataService Svc => Ed.Service;

    public static readonly IReadOnlyList<string> LootTables = new[]
    {
        "creature_loot_template", "gameobject_loot_template", "item_loot_template", "pickpocketing_loot_template", "skinning_loot_template",
        "fishing_loot_template", "reference_loot_template", "disenchant_loot_template", "mail_loot_template",
    };

    public static readonly IReadOnlyList<ServerViewDef> Views = new List<ServerViewDef>
    {
        new("items", "Items", "Every item: stats, price, look, spells; who sells it and where it drops.", new[] { "item_template" }),
        new("creatures", "Creatures (NPCs)", "Every creature and NPC: level, faction, looks, spawns, loot, what it sells and trains, its quests and gossip.", new[] { "creature_template" }),
        new("quests", "Quests", "Every quest: texts, levels, objectives, rewards, who starts and ends it, its chain.", new[] { "quest_template" }),
        new("gameobjects", "Game objects", "Chests, doors, herbs, ore, signs, mailboxes...: spawns, loot, quests.", new[] { "gameobject_template" }),
        new("spells", "Spells", "Every spell as the server computes it, next to the game's Spell.dbc row.", new[] { "spell_template" }),
        new("loot", "Loot tables", "Creature, game object, item, pickpocketing, skinning, fishing, disenchanting, mail and reference loot.", LootTables),
        new("vendors", "Vendors", "Creatures that sell items, with their lists (npc_vendor and the shared vendor templates).", new[] { "npc_vendor", "npc_vendor_template" }),
        new("trainers", "Trainers", "Creatures that teach spells, with their lists (npc_trainer and the shared trainer templates).", new[] { "npc_trainer", "npc_trainer_template" }),
        new("areas", "Areas", "The server's copy of the zones and subzones (AreaTable.dbc).", new[] { "area_template" }),
        new("maps", "Maps", "The server's copy of the maps (Map.dbc) with its own settings.", new[] { "map_template" }),
        new("taxis", "Flight paths", "The server's copy of the flight points (TaxiNodes.dbc).", new[] { "taxi_nodes" }),
        new("factions", "Factions", "The server's copy of the factions (Faction.dbc).", new[] { "faction" }),
        new("faction_templates", "Faction templates", "The server's copy of the faction templates (FactionTemplate.dbc): who is friendly or hostile.", new[] { "faction_template" }),
        new("locales", "Languages (locales)", "Names and texts in the other languages of the game.", new[] { "locales_item", "locales_creature", "locales_quest", "locales_gameobject", "locales_page_text", "locales_gossip_menu_option", "locales_broadcast_text", "locales_spell", "locales_area", "locales_taxi_node", "locales_faction" }),
        new("tables", "All tables", "Every table of the world database, row by row (columns WoW Data Studio does not describe are shown as stored).", Array.Empty<string>()),
    };

    public static ServerViewDef View(string id) => Views.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))
                                                   ?? throw new ServerDataException($"no view \"{id}\". The views: {string.Join(", ", Views.Select(v => v.Id))}.");

    /// <summary>The view that shows a table first (items for item_template), or "tables".</summary>
    public static string ViewOf(string table) => Views.FirstOrDefault(v => v.Tables.Contains(table, StringComparer.OrdinalIgnoreCase))?.Id ?? "tables";

    // ------------------------------------------------------------------ lists

    /// <summary>The list of a view: everything, an ID, or names containing the words. <paramref name="table"/> picks the
    /// table of a view with several (loot, languages, all tables).</summary>
    public List<ServerListItem> Search(string viewId, string text, string? table = null, int limit = 200)
    {
        var view = View(viewId);
        text = text.Trim();
        bool numeric = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);
        switch (view.Id)
        {
            case "loot": return SearchLoot(table ?? "creature_loot_template", text, numeric, number, limit);
            case "vendors": return SearchFlagged(0x4, text, numeric, number, limit, "npc_vendor", "npc_vendor_template", "vendor_id");
            case "trainers": return SearchFlagged(0x10, text, numeric, number, limit, "npc_trainer", "npc_trainer_template", "trainer_id");
            case "locales": return SearchLocales(table ?? "locales_item", text, numeric, number, limit);
            case "tables": return SearchTable(Svc.Table(table ?? throw new ServerDataException("Choose a table.")), text, numeric, number, limit);
            default: return SearchObjects(Svc.Table(view.Tables[0]), text, numeric, number, limit);
        }
    }

    private string LabelColumn(WorldTable t)
    {
        var k = Svc.KindOf(t.Name);
        var c = k is { IsList: false } ? k.LabelColumn : Svc.Info(t.Name)?.Label;
        return c != null && t.Has(c) ? c : t.Columns.FirstOrDefault(x => x.IsText)?.Name ?? t.Key[0];
    }

    private List<ServerListItem> SearchObjects(WorldTable t, string text, bool numeric, long number, int limit)
    {
        var idCol = Svc.IdColumn(t);
        var label = LabelColumn(t);
        RowFilter f = text.Length == 0 ? RowFilter.All()
            : numeric ? RowFilter.Eq(idCol, number)
            : RowFilter.Where(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new FilterTerm(label, FilterOp.Contains, w)).ToArray());
        if (numeric && t.Column(label)?.IsText == true) f.Or(new FilterTerm(label, FilterOp.Contains, text));
        var rows = Ed.Rows(t, f, limit * 4);
        return Group(t, rows, limit);
    }

    /// <summary>One line per object (the row the server uses of its versions).</summary>
    private List<ServerListItem> Group(WorldTable t, List<string?[]> rows, int limit)
    {
        var idCol = Svc.IdColumn(t);
        int ii = t.IndexOf(idCol);
        var res = new List<ServerListItem>();
        foreach (var g in rows.GroupBy(r => WorldValue.Long(r[ii])).Take(limit))
        {
            var list = g.ToList();
            var row = Svc.Effective(t, list) ?? list[0];
            var (loaded, vtext) = Svc.RowVersion(t, row, list);
            var info = Summary(t, row);
            if (list.Count > 1) info = (info.Length > 0 ? info + ", " : "") + $"{list.Count} versions";
            else if (!loaded && vtext.Length > 0) info = (info.Length > 0 ? info + ", " : "") + vtext;
            res.Add(new ServerListItem(t.Name, t.KeyText(row), g.Key, NameOf(t, row), info, loaded || list.Count > 1));
        }
        return res;
    }

    public string NameOf(WorldTable t, IReadOnlyList<string?> row)
    {
        var kind = Svc.KindOf(t.Name);
        var lc = LabelColumn(t);
        var v = t.IndexOf(lc) is var i and >= 0 ? row[i] : null;
        var info = Svc.Info(t.Name)?.Column(lc);
        if (info?.Link != null && info.Link.Resolve(WorldValue.Long(v)) is { } target)
            return Ed.Label(target.Target, target.Id) ?? $"{Ed.KindText(target.Target)} {target.Id}";
        return string.IsNullOrEmpty(v) ? (kind != null ? $"{kind.Plain} {WorldValue.Long(row[t.IndexOf(Svc.IdColumn(t))])}" : t.KeyText(row)) : ServerEditor.Short(v);
    }

    /// <summary>A few words about a row for lists (level, quality, rank...).</summary>
    private static string Summary(WorldTable t, IReadOnlyList<string?> r)
    {
        string? V(string c) => t.IndexOf(c) is var i and >= 0 ? r[i] : null;
        switch (t.Name.ToLowerInvariant())
        {
            case "item_template":
                return $"{ServerLayouts.Default.NamedValues.GetValueOrDefault("ItemQuality")?.NameOf(WorldValue.Long(V("quality")))?.Split(' ')[0]}, level {V("item_level")}";
            case "creature_template":
                return (V("level_min") == V("level_max") ? $"level {V("level_min")}" : $"level {V("level_min")}-{V("level_max")}") + (string.IsNullOrEmpty(V("subname")) ? "" : $", <{V("subname")}>");
            case "quest_template":
                return $"level {V("QuestLevel")}";
            case "spell_template":
                return string.IsNullOrEmpty(V("nameSubtext")) ? "" : V("nameSubtext")!;
            case "gameobject_template":
                return ServerLayouts.Default.NamedValues.GetValueOrDefault("GameObjectType")?.NameOf(WorldValue.Long(V("type"))) ?? "";
            default:
                return "";
        }
    }

    private List<ServerListItem> SearchTable(WorldTable t, string text, bool numeric, long number, int limit)
    {
        var label = LabelColumn(t);
        RowFilter f = text.Length == 0 ? RowFilter.All()
            : numeric ? RowFilter.Eq(t.Key[0], number)
            : t.Column(label)?.IsText == true ? RowFilter.Where(new FilterTerm(label, FilterOp.Contains, text)) : RowFilter.Where(new FilterTerm(t.Key[0], FilterOp.Eq, text));
        var rows = Ed.Rows(t, f, limit);
        return rows.Select(r => new ServerListItem(t.Name, t.KeyText(r), WorldValue.Long(r[t.IndexOf(t.Key[0])]), NameOf(t, r), Svc.RowVersion(t, r).Text, Svc.RowVersion(t, r).Loaded)).ToList();
    }

    private List<ServerListItem> SearchLocales(string table, string text, bool numeric, long number, int limit)
    {
        var t = Svc.Table(table);
        var keyCol = t.Key[0];
        var info = Svc.Info(t.Name)?.Column(keyCol);
        RowFilter f;
        if (text.Length == 0) f = RowFilter.All();
        else if (numeric) f = RowFilter.Eq(keyCol, number);
        else
        {
            // by the object's English name, or by a text of the row
            var ids = new List<string>();
            if (info?.Link != null && Svc.Layouts.Kind(info.Link.Target) is { } kind && Schema.Table(kind.Table) is { } kt)
                ids = Ed.Rows(kt, RowFilter.Where(new FilterTerm(kind.LabelColumn, FilterOp.Contains, text)), 200).Select(r => r[kt.IndexOf(kind.KeyColumn)] ?? "").Distinct().ToList();
            f = RowFilter.Where(new FilterTerm(keyCol, FilterOp.In, null, ids));
            var lc = LabelColumn(t);
            if (t.Column(lc)?.IsText == true) f.Or(new FilterTerm(lc, FilterOp.Contains, text));
        }
        var rows = Ed.Rows(t, f, limit);
        var labels = info?.Link != null ? Ed.Labels(info.Link.Target, rows.Select(r => WorldValue.Long(r[t.IndexOf(keyCol)]))) : new();
        return rows.Select(r =>
        {
            long id = WorldValue.Long(r[t.IndexOf(keyCol)]);
            return new ServerListItem(t.Name, t.KeyText(r), id, labels.GetValueOrDefault(id) ?? NameOf(t, r), NameOf(t, r));
        }).ToList();
    }

    private WorldSchema Schema => Svc.Schema;

    private List<ServerListItem> SearchFlagged(long flag, string text, bool numeric, long number, int limit, string rowsTable, string templateTable, string templateColumn)
    {
        var ct = Svc.Table("creature_template");
        RowFilter f;
        if (numeric) f = RowFilter.Eq("entry", number);
        else
        {
            var terms = new List<FilterTerm> { new("npc_flags", FilterOp.BitsSet, flag.ToString(CultureInfo.InvariantCulture)) };
            terms.AddRange(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new FilterTerm("name", FilterOp.Contains, w)));
            f = RowFilter.Where(terms.ToArray());
        }
        var items = Group(ct, Ed.Rows(ct, f, limit * 2), limit);
        // the size of each list (own rows + the shared template)
        var rt = Svc.Table(rowsTable);
        var counts = Ed.Rows(rt, RowFilter.Where(new FilterTerm("entry", FilterOp.In, null, items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)).ToList())))
            .GroupBy(r => WorldValue.Long(r[rt.IndexOf("entry")])).ToDictionary(g => g.Key, g => g.Count());
        return items.Select(i => i with { Info = i.Info + (counts.TryGetValue(i.Id, out var n) ? $", {n} own row{(n == 1 ? "" : "s")}" : "") }).ToList();
    }

    private List<ServerListItem> SearchLoot(string table, string text, bool numeric, long number, int limit)
    {
        var t = Svc.Table(table);
        if (!LootTables.Contains(t.Name, StringComparer.OrdinalIgnoreCase)) throw new ServerDataException($"{table} is not a loot table ({string.Join(", ", LootTables)}).");
        RowFilter f;
        if (text.Length == 0) f = RowFilter.All();
        else if (numeric) f = RowFilter.Eq("entry", number).Or(new FilterTerm("item", FilterOp.Eq, text));
        else
        {
            var it = Svc.Table("item_template");
            var ids = Ed.Rows(it, RowFilter.Where(new FilterTerm("name", FilterOp.Contains, text)), 300).Select(r => r[it.IndexOf("entry")] ?? "").Distinct().ToList();
            f = RowFilter.Where(new FilterTerm("item", FilterOp.In, null, ids));
        }
        var rows = Ed.Rows(t, f, 5000);
        var entries = rows.GroupBy(r => WorldValue.Long(r[t.IndexOf("entry")])).Take(limit).ToList();
        var owners = LootOwners(t.Name, entries.Select(g => g.Key).ToList());
        return entries.Select(g =>
        {
            var first = g.First();
            return new ServerListItem(t.Name, t.KeyText(first), g.Key, owners.GetValueOrDefault(g.Key) ?? $"loot {g.Key}", $"{g.Count()} row{(g.Count() == 1 ? "" : "s")}");
        }).ToList();
    }

    /// <summary>Who uses loot entries (names for the list): the creatures, game objects, items... with that loot ID.</summary>
    public Dictionary<long, string> LootOwners(string table, List<long> entries)
    {
        var res = new Dictionary<long, string>();
        if (entries.Count == 0) return res;
        var ids = entries.Select(e => e.ToString(CultureInfo.InvariantCulture)).ToList();
        void From(string ownerTable, string column, string nameColumn, string? extra = null)
        {
            var ot = Schema.Table(ownerTable);
            if (ot == null || !ot.Has(column)) return;
            foreach (var r in Ed.Rows(ot, RowFilter.Where(new FilterTerm(column, FilterOp.In, null, ids)), 2000))
            {
                long e = WorldValue.Long(r[ot.IndexOf(column)]);
                if (!res.ContainsKey(e)) res[e] = ServerEditor.Short(r[ot.IndexOf(nameColumn)] ?? "") + (extra ?? "");
                else if (!res[e].EndsWith("...", StringComparison.Ordinal)) res[e] += ", ...";
            }
        }
        switch (table.ToLowerInvariant())
        {
            case "creature_loot_template": From("creature_template", "loot_id", "name"); break;
            case "pickpocketing_loot_template": From("creature_template", "pickpocket_loot_id", "name"); break;
            case "skinning_loot_template": From("creature_template", "skinning_loot_id", "name"); break;
            case "gameobject_loot_template": From("gameobject_template", "data1", "name"); break;
            case "item_loot_template": From("item_template", "entry", "name"); break;
            case "disenchant_loot_template": From("item_template", "disenchant_id", "name", " (disenchanted)"); break;
            case "fishing_loot_template":
                foreach (var (k, v) in Ed.Labels("area", entries)) res[k] = "fishing in " + v;
                break;
            case "reference_loot_template":
                foreach (var e in entries) res[e] = $"reference loot {e}";
                break;
        }
        return res;
    }

    // ------------------------------------------------------------------ one record

    /// <summary>Everything about one row: its values, its related rows, what links to it and the game's side.</summary>
    public ServerRecord Record(string table, string key)
    {
        var t = Svc.Table(table);
        var row = Ed.Current(t, key) ?? throw new ServerDataException($"{t.Name} has no row {key}" + (Ed.ChangesOf(t.Name)?.Rows.GetValueOrDefault(key)?.IsDeleted == true ? " (you deleted it)." : "."));
        var info = Svc.Info(t.Name);
        var kind = Svc.KindOf(t.Name);
        string idCol = Svc.IdColumn(t);
        long id = t.Has(idCol) ? WorldValue.Long(row[t.IndexOf(idCol)]) : 0;
        var versions = new List<ServerListItem>();
        var verCol = ServerDataService.VersionColumn(t, info);
        string vtext;
        bool loaded;
        if (verCol != null)
        {
            var all = Ed.Versions(t, id);
            (loaded, vtext) = Svc.RowVersion(t, row, all);
            foreach (var v in all) versions.Add(new ServerListItem(t.Name, t.KeyText(v), id, NameOf(t, v), Svc.RowVersion(t, v, all).Text, Svc.RowVersion(t, v, all).Loaded));
        }
        else (loaded, vtext) = Svc.RowVersion(t, row);

        var fields = Fields(t, row);
        var problems = ServerValidation.CheckRow(Ed, t, row);
        foreach (var pr in problems) fields = fields.Select(f => f.Name.Equals(pr.Column, StringComparison.OrdinalIgnoreCase) && f.Problem.Length == 0 ? With(f, pr.Message) : f).ToList();
        var sections = Sections(t, row, id);
        var usedBy = kind != null ? UsedBy(kind.Name, id, 200) : new List<ServerUsedBy>();
        var (client, clientTable) = ServerConsistency.ClientSide(Ed, t, row);
        return new ServerRecord
        {
            Table = t,
            Info = info,
            Key = key,
            Row = row,
            ServerRow = Ed.ServerRow(t, key),
            Title = NameOf(t, row),
            Subtitle = $"{(kind?.Plain is { } p ? char.ToUpperInvariant(p[0]) + p[1..] : t.Name)} · {t.Name} {key}" + (vtext.Length > 0 ? " · " + vtext : ""),
            VersionText = vtext,
            Loaded = loaded,
            Versions = versions,
            Fields = fields,
            Sections = sections,
            UsedBy = usedBy,
            Problems = problems,
            Client = client,
            ClientTable = clientTable,
            Icon = IconOf(t, row),
            BrowseKey = BrowseKeyOf(t, row),
        };
    }

    private static ServerFieldValue With(ServerFieldValue f, string problem) => new()
    {
        Column = f.Column, Info = f.Info, Value = f.Value, Display = f.Display, LinkTarget = f.LinkTarget, LinkId = f.LinkId, LinkMissing = f.LinkMissing, Problem = problem, IsKey = f.IsKey,
    };

    /// <summary>Every column of a row with its meaning, in the layout's order (columns the layout does not describe last).</summary>
    public List<ServerFieldValue> Fields(WorldTable t, string?[] row)
    {
        var info = Svc.Info(t.Name);
        var order = new List<WorldColumn>();
        if (info != null)
            foreach (var ci in info.Columns)
                if (t.Column(ci.Name) is { } c && !order.Contains(c)) order.Add(c);
        order.AddRange(t.Columns.Where(c => !order.Contains(c)));
        // the names of every link at once
        var wanted = new Dictionary<string, HashSet<long>>();
        foreach (var c in order)
            if (info?.Column(c.Name)?.Link is { } link && c.IsNumber && ServerValidation.LinkApplies(t, row, c.Name) && link.Resolve(WorldValue.Long(row[t.IndexOf(c.Name)])) is { } target)
            {
                if (!wanted.TryGetValue(target.Target, out var set)) wanted[target.Target] = set = new();
                set.Add(target.Id);
            }
        var labels = wanted.ToDictionary(kv => kv.Key, kv => Ed.Labels(kv.Key, kv.Value));
        var res = new List<ServerFieldValue>();
        foreach (var c in order)
        {
            var ci = info?.Column(c.Name);
            var v = row[t.IndexOf(c.Name)];
            string? target = null;
            long linkId = 0;
            string? label = null;
            if (ci?.Link is { } link && c.IsNumber && ServerValidation.LinkApplies(t, row, c.Name) && link.Resolve(WorldValue.Long(v)) is { } tg)
            {
                target = tg.Target;
                linkId = tg.Id;
                label = labels.GetValueOrDefault(tg.Target)?.GetValueOrDefault(tg.Id);
                if (label == null && Ed.LinkExists(tg.Target, tg.Id)) label = tg.Target.StartsWith("dbc:", StringComparison.Ordinal) ? $"#{tg.Id}" : $"{Ed.KindText(tg.Target)} {tg.Id}";
            }
            res.Add(new ServerFieldValue
            {
                Column = c,
                Info = ci,
                Value = v,
                Display = Display(c, ci, v, label, target != null),
                LinkTarget = target,
                LinkId = linkId,
                LinkMissing = target != null && label == null,
                IsKey = t.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase),
            });
        }
        return res;
    }

    /// <summary>A value for people: names of choices and flags, money, times, links.</summary>
    public static string Display(WorldColumn c, ServerColumnInfo? ci, string? v, string? linkLabel = null, bool linked = false)
    {
        if (v == null) return "NULL";
        if (ci == null || !c.IsNumber) return v;
        long n = WorldValue.Long(v);
        string extra = ci.Semantic switch
        {
            ServerSemantic.Enum or ServerSemantic.Bool when ci.Values != null => ci.Values.NameOf(n) ?? "",
            ServerSemantic.Flags when ci.Values != null && n != 0 => $"0x{n:X} · " + ci.Values.Describe(n),
            ServerSemantic.Flags when n != 0 => $"0x{n:X}",
            ServerSemantic.Money when n != 0 => Money(n),
            ServerSemantic.Ms when n > 0 => $"{n / 1000.0:0.###} s",
            ServerSemantic.Seconds when n > 0 => Duration(n),
            ServerSemantic.Percent when ci.Values == null => "%",
            _ => "",
        };
        if (linked) extra = linkLabel ?? "(missing)";
        if (extra.Length == 0) return v;
        return extra == "%" ? v + " %" : $"{v} · {extra}";
    }

    public static string Money(long copper)
    {
        var a = Math.Abs(copper);
        var parts = new List<string>();
        if (a >= 10000) parts.Add($"{a / 10000} g");
        if (a % 10000 >= 100) parts.Add($"{a % 10000 / 100} s");
        if (a % 100 > 0 || parts.Count == 0) parts.Add($"{a % 100} c");
        return (copper < 0 ? "-" : "") + string.Join(" ", parts);
    }

    private static string Duration(long s) => s < 120 ? $"{s} s" : s < 7200 ? $"{s / 60.0:0.#} min" : s < 172800 ? $"{s / 3600.0:0.#} h" : $"{s / 86400.0:0.#} days";

    /// <summary>The icon texture of an item or a spell (from the game's tables), or null.</summary>
    public string? IconOf(WorldTable t, string?[] row)
    {
        var dbc = Svc.Dbc;
        if (dbc == null) return null;
        try
        {
            if (t.Name.Equals("item_template", StringComparison.OrdinalIgnoreCase))
            {
                var tab = dbc.Lookup("ItemDisplayInfo");
                var r = tab?.Find(WorldValue.Long(row[t.IndexOf("display_id")]));
                var col = tab?.Layout.Columns.FirstOrDefault(c => c.Name == "InventoryIcon");
                if (r == null || col == null) return null;
                var name = tab!.Text(r, col);
                return name.Length == 0 ? null : $"Interface\\Icons\\{name}.blp";
            }
            if (t.Name.Equals("spell_template", StringComparison.OrdinalIgnoreCase))
            {
                var tab = dbc.Lookup("SpellIcon");
                var r = tab?.Find(WorldValue.Long(row[t.IndexOf("spellIconId")]));
                var col = tab?.Layout.Columns.FirstOrDefault(c => c.IsString);
                if (r == null || col == null) return null;
                var name = tab!.Text(r, col);
                return name.Length == 0 ? null : name + ".blp";
            }
        }
        catch (Exception e) when (e is DbcException or IOException) { }
        return null;
    }

    private string? BrowseKeyOf(WorldTable t, string?[] row)
    {
        string? V(string c) => t.IndexOf(c) is var i and >= 0 ? row[i] : null;
        return t.Name.ToLowerInvariant() switch
        {
            "item_template" => "item:" + V("entry"),
            "creature_template" => "creature:" + V("entry"),
            "gameobject_template" => "go:" + V("entry"),
            "creature" => "creature:" + V("id"),
            "gameobject" => "go:" + V("id"),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ related rows

    private ServerSection? Section(string title, string table, RowFilter filter, string[]? columns = null, Dictionary<string, string?>? add = null, string note = "", int limit = 300)
    {
        var t = Schema.Table(table);
        if (t == null) return null;
        foreach (var g in filter.Any)
            foreach (var term in g)
                if (!t.Has(term.Column)) return null;
        var rows = Ed.Rows(t, filter, limit + 1);
        var cols = columns?.Where(t.Has).ToList() ?? DefaultColumns(t);
        return new ServerSection
        {
            Title = title, Table = t, Filter = filter, Rows = rows.Take(limit).ToList(), Columns = cols, AddDefaults = add ?? new(), Note = note, Truncated = rows.Count > limit,
            RowTexts = RowTexts(t, rows.Take(limit).ToList(), cols),
        };
    }

    private List<string> DefaultColumns(WorldTable t)
    {
        var cols = new List<string>(t.Key);
        var lc = LabelColumn(t);
        if (!cols.Contains(lc, StringComparer.OrdinalIgnoreCase)) cols.Add(lc);
        foreach (var c in Svc.Info(t.Name)?.Columns ?? new List<ServerColumnInfo>())
        {
            if (cols.Count >= 6) break;
            if (!cols.Contains(c.Name, StringComparer.OrdinalIgnoreCase) && t.Has(c.Name)) cols.Add(c.Name);
        }
        return cols;
    }

    /// <summary>The rows of a section as one line each ("item 2589 · Linen Cloth; ChanceOrQuestChance 35 %; ...").</summary>
    public List<string> RowTexts(WorldTable t, List<string?[]> rows, List<string> cols)
    {
        var info = Svc.Info(t.Name);
        var wanted = new Dictionary<string, HashSet<long>>();
        foreach (var r in rows)
            foreach (var c in cols)
                if (info?.Column(c)?.Link is { } link && t.Column(c)!.IsNumber && ServerValidation.LinkApplies(t, r, c) && link.Resolve(WorldValue.Long(r[t.IndexOf(c)])) is { } tg)
                {
                    if (!wanted.TryGetValue(tg.Target, out var set)) wanted[tg.Target] = set = new();
                    set.Add(tg.Id);
                }
        var labels = wanted.ToDictionary(kv => kv.Key, kv => Ed.Labels(kv.Key, kv.Value));
        return rows.Select(r => string.Join("; ", cols.Select(c =>
        {
            var col = t.Column(c)!;
            var ci = info?.Column(c);
            var v = r[t.IndexOf(c)];
            string? label = null;
            bool linked = false;
            if (ci?.Link is { } link && col.IsNumber && ServerValidation.LinkApplies(t, r, c) && link.Resolve(WorldValue.Long(v)) is { } tg)
            {
                linked = true;
                label = labels.GetValueOrDefault(tg.Target)?.GetValueOrDefault(tg.Id);
            }
            return $"{c} {Display(col, ci, v, label, linked)}";
        })) + (Svc.RowVersion(t, r).Loaded ? "" : " [not loaded at the server's patch]")).ToList();
    }

    private static Dictionary<string, string?> D(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2 == null ? null : Convert.ToString(x.Item2, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);

    private List<ServerSection> Sections(WorldTable t, string?[] row, long id)
    {
        var s = new List<ServerSection?>();
        long V(string c) => t.IndexOf(c) is var i and >= 0 ? WorldValue.Long(row[i]) : 0;
        string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
        switch (t.Name.ToLowerInvariant())
        {
            case "item_template":
                s.Add(Section("Sold by", "npc_vendor", RowFilter.Eq("item", id), new[] { "entry", "item", "maxcount", "incrtime" }, D(("item", id))));
                s.Add(Section("Sold by vendor templates", "npc_vendor_template", RowFilter.Eq("item", id), new[] { "entry", "item", "maxcount" }, D(("item", id))));
                foreach (var lt in LootTables)
                    s.Add(Section("Drops from: " + lt.Replace("_loot_template", "").Replace('_', ' ') + " loot", lt, RowFilter.Eq("item", id), new[] { "entry", "item", "ChanceOrQuestChance", "groupid", "mincountOrRef", "maxcount" }, D(("item", id), ("ChanceOrQuestChance", 100), ("mincountOrRef", 1), ("maxcount", 1))));
                s.Add(Section("Contents when opened", "item_loot_template", RowFilter.Eq("entry", id), new[] { "entry", "item", "ChanceOrQuestChance", "groupid", "mincountOrRef", "maxcount" }, D(("entry", id), ("ChanceOrQuestChance", 100), ("mincountOrRef", 1), ("maxcount", 1))));
                if (V("disenchant_id") != 0) s.Add(Section("Disenchanting gives", "disenchant_loot_template", RowFilter.Eq("entry", V("disenchant_id")), null, D(("entry", V("disenchant_id")))));
                s.Add(Section("Held by creatures", "creature_equip_template", RowFilter.Eq("item1", id).Or(new FilterTerm("item2", FilterOp.Eq, Id(id))).Or(new FilterTerm("item3", FilterOp.Eq, Id(id)))));
                s.Add(Section("The server's copy of its display", "item_display_info", RowFilter.Eq("ID", V("display_id")), null, D(("ID", V("display_id")))));
                s.Add(Section("Languages", "locales_item", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "creature_template":
                s.Add(Section("Spawns", "creature", RowFilter.Eq("id", id).Or(new FilterTerm("id2", FilterOp.Eq, Id(id))).Or(new FilterTerm("id3", FilterOp.Eq, Id(id))).Or(new FilterTerm("id4", FilterOp.Eq, Id(id))).Or(new FilterTerm("id5", FilterOp.Eq, Id(id))),
                    new[] { "guid", "id", "map", "position_x", "position_y", "position_z", "spawntimesecsmin" }, D(("id", id)), limit: 200));
                if (V("loot_id") != 0) s.Add(Section("Loot when killed", "creature_loot_template", RowFilter.Eq("entry", V("loot_id")), new[] { "entry", "item", "ChanceOrQuestChance", "groupid", "mincountOrRef", "maxcount" }, D(("entry", V("loot_id")), ("ChanceOrQuestChance", 100), ("mincountOrRef", 1), ("maxcount", 1))));
                if (V("pickpocket_loot_id") != 0) s.Add(Section("Pickpocketing loot", "pickpocketing_loot_template", RowFilter.Eq("entry", V("pickpocket_loot_id")), null, D(("entry", V("pickpocket_loot_id")))));
                if (V("skinning_loot_id") != 0) s.Add(Section("Skinning loot", "skinning_loot_template", RowFilter.Eq("entry", V("skinning_loot_id")), null, D(("entry", V("skinning_loot_id")))));
                s.Add(Section("Sells", "npc_vendor", RowFilter.Eq("entry", id), new[] { "entry", "slot", "item", "maxcount", "incrtime" }, D(("entry", id)),
                    (V("npc_flags") & 0x4) == 0 ? "It has no Vendor flag (npc_flags 0x4): players cannot open its shop." : ""));
                if (V("vendor_id") != 0) s.Add(Section("Sells (shared vendor template)", "npc_vendor_template", RowFilter.Eq("entry", V("vendor_id")), new[] { "entry", "item", "maxcount" }, D(("entry", V("vendor_id")))));
                s.Add(Section("Trains", "npc_trainer", RowFilter.Eq("entry", id), new[] { "entry", "spell", "spellcost", "reqlevel" }, D(("entry", id)),
                    (V("npc_flags") & 0x10) == 0 ? "It has no Trainer flag (npc_flags 0x10)." : ""));
                if (V("trainer_id") != 0) s.Add(Section("Trains (shared trainer template)", "npc_trainer_template", RowFilter.Eq("entry", V("trainer_id")), new[] { "entry", "spell", "spellcost", "reqlevel" }, D(("entry", V("trainer_id")))));
                s.Add(Section("Starts quests", "creature_questrelation", RowFilter.Eq("id", id), new[] { "id", "quest", "patch_min", "patch_max" }, D(("id", id)),
                    (V("npc_flags") & 0x2) == 0 ? "It has no Quest giver flag (npc_flags 0x2)." : ""));
                s.Add(Section("Ends quests", "creature_involvedrelation", RowFilter.Eq("id", id), new[] { "id", "quest", "patch_min", "patch_max" }, D(("id", id))));
                if (V("equipment_id") != 0) s.Add(Section("Equipment", "creature_equip_template", RowFilter.Eq("entry", V("equipment_id")), null, D(("entry", V("equipment_id")))));
                if (V("gossip_menu_id") != 0)
                {
                    s.Add(Section("Gossip (what it says)", "gossip_menu", RowFilter.Eq("entry", V("gossip_menu_id")), null, D(("entry", V("gossip_menu_id")))));
                    s.Add(Section("Gossip options", "gossip_menu_option", RowFilter.Eq("menu_id", V("gossip_menu_id")), new[] { "menu_id", "id", "option_text", "option_id" }, D(("menu_id", V("gossip_menu_id")))));
                }
                var displays = Enumerable.Range(1, 4).Select(k => V("display_id" + k)).Where(d => d != 0).Select(Id).ToList();
                if (displays.Count > 0) s.Add(Section("The server's facts about its looks", "creature_display_info_addon", RowFilter.Where(new FilterTerm("display_id", FilterOp.In, null, displays))));
                s.Add(Section("Reputation when killed", "creature_onkill_reputation", RowFilter.Eq("creature_id", id), null, D(("creature_id", id))));
                s.Add(Section("Languages", "locales_creature", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "quest_template":
                s.Add(Section("Started by creatures", "creature_questrelation", RowFilter.Eq("quest", id), new[] { "id", "quest" }, D(("quest", id))));
                s.Add(Section("Ended by creatures", "creature_involvedrelation", RowFilter.Eq("quest", id), new[] { "id", "quest" }, D(("quest", id))));
                s.Add(Section("Started by game objects", "gameobject_questrelation", RowFilter.Eq("quest", id), new[] { "id", "quest" }, D(("quest", id))));
                s.Add(Section("Ended by game objects", "gameobject_involvedrelation", RowFilter.Eq("quest", id), new[] { "id", "quest" }, D(("quest", id))));
                s.Add(Section("Started by items", "item_template", RowFilter.Eq("start_quest", id), new[] { "entry", "patch", "name" }));
                s.Add(Section("Comes before this one (its NextQuestInChain is this quest)", "quest_template", RowFilter.Eq("NextQuestInChain", id), new[] { "entry", "patch", "Title" }));
                s.Add(Section("Opened by this one (their PrevQuestId is this quest)", "quest_template", RowFilter.Eq("PrevQuestId", id).Or(new FilterTerm("PrevQuestId", FilterOp.Eq, Id(-id))), new[] { "entry", "patch", "Title", "PrevQuestId" }));
                s.Add(Section("Languages", "locales_quest", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "gameobject_template":
                s.Add(Section("Spawns", "gameobject", RowFilter.Eq("id", id), new[] { "guid", "id", "map", "position_x", "position_y", "position_z", "spawntimesecsmin" }, D(("id", id)), limit: 200));
                if (V("type") is 3 or 25 && V("data1") != 0) s.Add(Section("Loot", "gameobject_loot_template", RowFilter.Eq("entry", V("data1")), new[] { "entry", "item", "ChanceOrQuestChance", "groupid", "mincountOrRef", "maxcount" }, D(("entry", V("data1")), ("ChanceOrQuestChance", 100), ("mincountOrRef", 1), ("maxcount", 1))));
                s.Add(Section("Starts quests", "gameobject_questrelation", RowFilter.Eq("id", id), new[] { "id", "quest" }, D(("id", id))));
                s.Add(Section("Ends quests", "gameobject_involvedrelation", RowFilter.Eq("id", id), new[] { "id", "quest" }, D(("id", id))));
                s.Add(Section("Languages", "locales_gameobject", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "spell_template":
                s.Add(Section("Taught by trainers", "npc_trainer", RowFilter.Eq("spell", id), new[] { "entry", "spell", "spellcost", "reqlevel" }, D(("spell", id))));
                s.Add(Section("Taught by trainer templates", "npc_trainer_template", RowFilter.Eq("spell", id), new[] { "entry", "spell", "spellcost", "reqlevel" }, D(("spell", id))));
                s.Add(Section("Skills (skill_line_ability)", "skill_line_ability", RowFilter.Eq("spell_id", id), null, D(("spell_id", id))));
                s.Add(Section("Languages", "locales_spell", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "area_template":
                s.Add(Section("Subzones", "area_template", RowFilter.Eq("zone_id", id), new[] { "entry", "name", "area_level" }, D(("zone_id", id), ("map_id", V("map_id")))));
                s.Add(Section("Languages", "locales_area", RowFilter.Eq("Entry", id), null, D(("Entry", id))));
                break;
            case "map_template":
                s.Add(Section("Zones of the map", "area_template", RowFilter.Where(new FilterTerm("map_id", FilterOp.Eq, Id(id)), new FilterTerm("zone_id", FilterOp.Eq, "0")), new[] { "entry", "name" }, D(("map_id", id))));
                break;
            case "taxi_nodes":
                s.Add(Section("Languages", "locales_taxi_node", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "faction":
                s.Add(Section("Faction templates", "faction_template", RowFilter.Eq("faction_id", id), null, D(("faction_id", id))));
                s.Add(Section("Factions under it", "faction", RowFilter.Eq("team", id), new[] { "id", "build", "name" }));
                s.Add(Section("Languages", "locales_faction", RowFilter.Eq("entry", id), null, D(("entry", id))));
                break;
            case "creature":
                s.Add(Section("Extras of this spawn", "creature_addon", RowFilter.Eq("guid", id), null, D(("guid", id))));
                break;
            case "gossip_menu":
                s.Add(Section("Options", "gossip_menu_option", RowFilter.Eq("menu_id", id), new[] { "menu_id", "id", "option_text", "option_id" }, D(("menu_id", id))));
                break;
            case "npc_vendor":
            case "npc_trainer":
            case "npc_vendor_template":
            case "npc_trainer_template":
                s.Add(Section("The whole list", t.Name, RowFilter.Eq("entry", V("entry")), DefaultColumns(t).ToArray(), D(("entry", V("entry")))));
                break;
            default:
                if (LootTables.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                    s.Add(Section("Every row of this loot", t.Name, RowFilter.Eq("entry", V("entry")), new[] { "entry", "item", "ChanceOrQuestChance", "groupid", "mincountOrRef", "maxcount" }, D(("entry", V("entry")), ("ChanceOrQuestChance", 100), ("mincountOrRef", 1), ("maxcount", 1)),
                        GroupNote(t, V("entry"))));
                break;
        }
        return s.Where(x => x != null).Cast<ServerSection>().ToList();
    }

    /// <summary>The chances of a loot's groups (a group above 100 % is a mistake).</summary>
    private string GroupNote(WorldTable t, long entry)
    {
        var rows = Ed.Rows(t, RowFilter.Eq("entry", entry));
        var notes = rows.Where(r => WorldValue.Long(r[t.IndexOf("groupid")]) > 0 && WorldValue.Number(r[t.IndexOf("ChanceOrQuestChance")]) > 0)
            .GroupBy(r => WorldValue.Long(r[t.IndexOf("groupid")]))
            .Select(g => (g.Key, Sum: g.Sum(r => WorldValue.Number(r[t.IndexOf("ChanceOrQuestChance")]))))
            .Where(g => g.Sum > 100.001).Select(g => $"group {g.Key} adds up to {g.Sum:0.##} % (more than 100 %: some items never drop)").ToList();
        return string.Join("; ", notes);
    }

    // ------------------------------------------------------------------ used by

    /// <summary>The rows of every table whose described columns link to an object ("Used by").</summary>
    public List<ServerUsedBy> UsedBy(string kind, long id, int max = 200)
    {
        var res = new List<ServerUsedBy>();
        foreach (var ti in Svc.Layouts.Tables)
        {
            var t = Schema.Table(ti.Name);
            if (t == null) continue;
            var cols = ti.Columns.Where(c => c.Link != null && t.Has(c.Name) && (c.Link.Target == kind || c.Link.NegativeTarget == kind)).ToList();
            foreach (var c in cols)
            {
                if (res.Count >= max) return res;
                // skip the object's own key column
                var k = Svc.KindOf(t.Name);
                if (k?.Name == kind && string.Equals(k.KeyColumn, c.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var values = new List<string>();
                switch (c.Link!.Mode)
                {
                    case ServerLinkMode.Abs: values.Add(id.ToString(CultureInfo.InvariantCulture)); values.Add((-id).ToString(CultureInfo.InvariantCulture)); break;
                    case ServerLinkMode.Neg: values.Add((-id).ToString(CultureInfo.InvariantCulture)); break;
                    case ServerLinkMode.Sign: values.Add((c.Link.Target == kind ? id : -id).ToString(CultureInfo.InvariantCulture)); break;
                    default: values.Add(id.ToString(CultureInfo.InvariantCulture)); break;
                }
                List<string?[]> rows;
                try { rows = Ed.Rows(t, RowFilter.Where(new FilterTerm(c.Name, FilterOp.In, null, values)), max - res.Count); }
                catch (ServerDataException) { continue; }
                foreach (var r in rows) res.Add(new ServerUsedBy(t.Name, c.Name, t.KeyText(r), NameOf(t, r)));
            }
        }
        return res;
    }
}
