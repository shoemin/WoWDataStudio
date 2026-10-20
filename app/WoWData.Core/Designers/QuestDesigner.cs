using System.Globalization;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>A quest giver or ender (or what else starts or completes a quest): a creature, a game object, an item that
/// starts the quest, an area trigger that completes it.</summary>
public sealed record QuestRelation(string Kind, long Id, string Name, string Table, bool Exists, int Spawns, string Note = "")
{
    public string Key => $"{Kind}:{Id}";
    public string Text => $"{Name} ({Kind} {Id})" + (Kind is "creature" or "object" ? Spawns == 0 ? ", not spawned" : $", {Spawns} spawn{(Spawns == 1 ? "" : "s")}" : "") + (Note.Length > 0 ? $", {Note}" : "");
}

/// <summary>One objective of a quest in plain words.</summary>
public sealed record QuestObjective(string Kind, int Slot, long Id, string Name, long Count, string Text);

/// <summary>Where an item comes from (for "collect" objectives): a creature's loot, a chest, a container, a vendor...</summary>
public sealed record QuestDropSource(string Kind, long Id, string Name, string Detail, string Table, long LootEntry, int Spawns)
{
    public string Text => $"{Kind} {Name} ({Id}): {Detail}" + (Kind is "creature" or "object" && Spawns == 0 ? " (not spawned anywhere)" : "");
}

/// <summary>The short facts of a quest (the chain view's node, the lists).</summary>
public sealed record QuestSummary(long Id, string Title, long Level, long MinLevel, long MaxLevel, long Races, long Classes,
    long Prev, long Next, long Group, long NextInChain, long Breadcrumb, long Flags, long SpecialFlags, long Method, bool Edited)
{
    public string Text => $"{Title} [{Id}] level {Level}" + (MinLevel > 0 ? $" (from {MinLevel})" : "");
}

/// <summary>A creature or object spawn (where it stands).</summary>
public sealed record QuestSpawn(string Kind, long Guid, long Entry, long Map, double X, double Y, double Z, double O)
{
    public string Text => $"{Kind} spawn {Guid}: map {Map} at {X:0.#}, {Y:0.#}, {Z:0.#}";
    public string GoCommand => $".go xyz {X.ToString("0.##", CultureInfo.InvariantCulture)} {Y.ToString("0.##", CultureInfo.InvariantCulture)} {Z.ToString("0.##", CultureInfo.InvariantCulture)} {Map}";
}

/// <summary>
/// 0.13.0: the quest-chain designer: VMaNGOS quests (quest_template) and what they need around them (givers and enders,
/// objectives, drop sources, rewards, chain links, new quest-giver NPCs), edited as server rows of one design session,
/// with the checks of <see cref="QuestRules"/> and the chain graph of <see cref="QuestChains"/>. Quests live on the
/// server: without the server's world database the designer refuses (the game only caches them, WDB\questcache.wdb).
/// Client pieces are the few quest-related game tables (QuestSort.dbc categories, AreaTrigger.dbc exploration points,
/// Spell.dbc reward spells through the spell designer). Saving puts everything into ONE change set of My changes; "Apply
/// to server" writes it into the database and can be undone, as for every server edit.
/// </summary>
public sealed partial class QuestDesigner
{
    public const string NoServer = "The quest designer needs the server's world database (quests live there): turn it on in Settings > Server database and test the connection.";

    private readonly HashSet<long> _touched = new();
    private readonly List<string> _notes = new();

    public QuestDesigner(DesignSession session, SpellDesigner? spells = null)
    {
        Session = session;
        Spells = spells ?? new SpellDesigner(session);
    }

    public DesignSession Session { get; }
    public SpellDesigner Spells { get; }
    public bool HasServer => Session.Server != null;
    public ServerEditor Ed => Session.Server ?? throw new DesignException(NoServer);
    public WorldTable Quests => Ed.Table("quest_template");
    public IReadOnlyCollection<long> Touched => _touched;
    public IReadOnlyList<string> Notes => _notes;

    internal void Touch(long id) => _touched.Add(id);
    internal void Note(string text) { if (!_notes.Contains(text)) _notes.Add(text); }

    // ------------------------------------------------------------------ rows

    public WorldTable T(string name) => Ed.Table(name);
    public bool HasTable(string name) => Ed.Schema.Table(name) != null;

    public string?[]? Row(long id) => Ed.Object(Quests, id);

    public string?[] Require(long id) => Row(id) ?? throw new DesignException($"There is no quest {id} on the server (quest_template).");

    public bool Exists(long id) => id > 0 && Row(id) != null;

    public static string? V(WorldTable t, IReadOnlyList<string?> row, string col) => t.IndexOf(col) is var i and >= 0 ? row[i] : null;
    public static long L(WorldTable t, IReadOnlyList<string?> row, string col) => WorldValue.Long(V(t, row, col));
    public long Q(IReadOnlyList<string?> row, string col) => L(Quests, row, col);
    public string S(IReadOnlyList<string?> row, string col) => V(Quests, row, col) ?? "";

    public string Title(long id) => Row(id) is { } r ? S(r, "Title") : $"#{id}";
    public string Label(long id) => $"{Title(id)} [{id}]";

    public QuestSummary Summary(long id)
    {
        var r = Require(id);
        var server = Ed.ServerRow(Quests, Quests.KeyText(r));
        return new QuestSummary(id, S(r, "Title"), Q(r, "QuestLevel"), Q(r, "MinLevel"), Q(r, "MaxLevel"), Q(r, "RequiredRaces"), Q(r, "RequiredClasses"),
            Q(r, "PrevQuestId"), Q(r, "NextQuestId"), Q(r, "ExclusiveGroup"), Q(r, "NextQuestInChain"), Quests.Has("BreadcrumbForQuestId") ? Q(r, "BreadcrumbForQuestId") : 0,
            Q(r, "QuestFlags"), Q(r, "SpecialFlags"), Q(r, "Method"), server == null || !server.SequenceEqual(r));
    }

    /// <summary>Quests by words of their title or by ID (your edits included).</summary>
    public List<QuestSummary> Find(string words, int max = 50)
    {
        var t = words.Trim();
        var res = new List<QuestSummary>();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && Exists(id)) res.Add(Summary(id));
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return res;
        var filter = RowFilter.Where(parts.Select(p => new FilterTerm("Title", FilterOp.Contains, p)).ToArray());
        foreach (var row in Ed.Rows(Quests, filter, 2000).GroupBy(r => Q(r, "entry")).Select(g => Ed.Service.Effective(Quests, g.ToList()) ?? g.First()).OrderBy(r => S(r, "Title").Length).ThenBy(r => Q(r, "entry")))
        {
            if (res.Count >= max) break;
            long e = Q(row, "entry");
            if (res.All(x => x.Id != e)) res.Add(Summary(e));
        }
        return res;
    }

    /// <summary>Change values of a quest (checked by the server's rules first; refused values change nothing).</summary>
    internal ServerEditResult SetColumns(long id, IEnumerable<(string Column, string? Text)> values, string? label = null)
    {
        var row = Require(id);
        var r = Ed.Set("quest_template", Quests.KeyText(row), values.Where(v => Quests.Has(v.Column)).ToList(), label);
        if (!r.Ok) throw new DesignException($"Quest {id}: {r.Message}");
        _touched.Add(id);
        return r;
    }

    internal void SetColumn(long id, string column, long value) => SetColumns(id, new[] { (column, (string?)value.ToString(CultureInfo.InvariantCulture)) });

    // ------------------------------------------------------------------ the form

    /// <summary>The classes of the game (with new ones), for class names.</summary>
    public IReadOnlyList<(long Id, string Name)> Classes()
    {
        var res = new List<(long, string)>();
        try
        {
            if (Session.Lookup("ChrClasses") is { } t && t.Layout.Column("Name") is { } n)
                foreach (var r in t.Rows) if (t.Key(r) is { } k and > 0) res.Add((k, t.Text(r, n, 0)));
        }
        catch (Exception e) when (e is DbcException or IOException) { }
        return res.Count > 0 ? res : QuestForm.DefaultClasses();
    }

    /// <summary>A field as friendly text ("Warrior", "Alliance", "1g 50s", "creature:3098 (Mottled Boar)").</summary>
    public string Show(long id, string field) => Show(id, QuestForm.Require(field));

    public string Show(long id, QuestField f)
    {
        var r = Require(id);
        if (!Quests.Has(f.Column)) return "";
        var raw = V(Quests, r, f.Column);
        long n = WorldValue.Long(raw);
        switch (f.Kind)
        {
            case QuestFieldKind.Line:
            case QuestFieldKind.Text:
                return raw ?? "";
            case QuestFieldKind.Money:
                return n == 0 ? "0" : QuestForm.MoneyText(n);
            case QuestFieldKind.ClassMask:
                return QuestForm.ClassText(n, Classes());
            case QuestFieldKind.RaceMask:
                return QuestForm.RaceText(n);
            case QuestFieldKind.Target:
                return n == 0 ? "0" : $"{QuestForm.TargetText(n)} ({(n > 0 ? CreatureName(n) : ObjectName(-n))})";
            case QuestFieldKind.Category:
                return n == 0 ? "0" : CategoryText(n);
            case QuestFieldKind.Flags when f.Named != null:
                return n == 0 ? "0" : QuestForm.Named(f.Named)?.Describe(n) ?? n.ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.Choice when f.Named != null:
                return QuestForm.Named(f.Named)?.NameOf(n) is { } nm ? nm : n.ToString(CultureInfo.InvariantCulture);
            case QuestFieldKind.Choice when f.Link == "dbc:QuestInfo":
                return n == 0 ? "0 (Normal)" : $"{n} ({Ed.Label("dbc:QuestInfo", n) ?? "?"})";
            case QuestFieldKind.Link when f.Link != null && n != 0:
            {
                var target = f.Link;
                long lid = Math.Abs(n);
                return $"{n} ({Ed.Label(target, lid) ?? "does not exist"})";
            }
            default:
                return raw ?? "";
        }
    }

    /// <summary>A field as the text to edit: numbers and IDs as they are (creature:N for targets), money as 1g 50s, class
    /// and race names, "sort:Warrior" for a category, texts as they are.</summary>
    public string EditValue(long id, QuestField f)
    {
        var r = Require(id);
        if (!Quests.Has(f.Column)) return "";
        var raw = V(Quests, r, f.Column);
        long n = WorldValue.Long(raw);
        return f.Kind switch
        {
            QuestFieldKind.Line or QuestFieldKind.Text => raw ?? "",
            QuestFieldKind.Money => n == 0 ? "0" : QuestForm.MoneyText(n),
            QuestFieldKind.ClassMask => QuestForm.ClassText(n, Classes()),
            QuestFieldKind.RaceMask => QuestForm.RaceText(n),
            QuestFieldKind.Target => QuestForm.TargetText(n),
            QuestFieldKind.Category => n < 0 && Categories().FirstOrDefault(c => c.Id == -n).Name is { } cn ? $"sort:{cn}" : n.ToString(CultureInfo.InvariantCulture),
            _ => raw ?? "",
        };
    }

    /// <summary>The named bits of a mask or flags field (checkboxes): classes, races, quest flags.</summary>
    public IReadOnlyList<(long Bit, string Name)> Bits(QuestField f) => f.Kind switch
    {
        QuestFieldKind.ClassMask => Classes().Where(c => c.Id is >= 1 and <= 32).Select(c => (1L << (int)(c.Id - 1), c.Name)).ToList(),
        QuestFieldKind.RaceMask => QuestForm.Named("RaceMask")?.Values.Where(v => v.Value is > 0 and <= 0x80).Select(v => (v.Value, v.Name)).ToList() ?? new List<(long, string)>(),
        QuestFieldKind.Flags when f.Named != null => QuestForm.Named(f.Named)?.Values.Where(v => v.Value != 0).Select(v => (v.Value, v.Name)).ToList() ?? new List<(long, string)>(),
        _ => Array.Empty<(long, string)>(),
    };

    /// <summary>The named values of a choice field (Method, the quest's kind).</summary>
    public IReadOnlyList<(long Value, string Name)> Choices(QuestField f)
    {
        if (f.Kind != QuestFieldKind.Choice) return Array.Empty<(long, string)>();
        if (f.Named != null) return QuestForm.Named(f.Named)?.Values.Select(v => (v.Value, v.Name)).ToList() ?? new List<(long, string)>();
        var res = new List<(long, string)> { (0, "Normal") };
        if (f.Link == "dbc:QuestInfo" && Session.Lookup("QuestInfo") is { } t)
            res.AddRange(t.Rows.Select(r => (t.Key(r) ?? 0, t.Text(r, t.Layout.Column("InfoName")!, 0))).Where(x => x.Item1 > 0));
        return res;
    }

    /// <summary>The raw stored number of a field (0 for texts).</summary>
    public long Raw(long id, QuestField f) => Quests.Has(f.Column) ? WorldValue.Long(V(Quests, Require(id), f.Column)) : 0;

    /// <summary>Set a field from friendly text. Flags take "+Name" / "-Name" to switch one bit.</summary>
    public DesignResult Set(long id, string field, string value)
    {
        var f = QuestForm.Find(field);
        if (f == null) return DesignResult.Refused($"There is no quest field \"{field}\" (\"wds quests fields\" lists them).");
        try
        {
            var r = Require(id);
            if (!Quests.Has(f.Column)) return DesignResult.Refused($"This server's quest_template has no column {f.Column}.");
            string stored;
            var t = value.Trim();
            if (f.Kind == QuestFieldKind.Flags && f.Named != null && (t.StartsWith('+') || t.StartsWith('-')))
            {
                long cur = Q(r, f.Column);
                var nv = QuestForm.Named(f.Named)!;
                foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    bool on = part[0] != '-';
                    var name = part.TrimStart('+', '-').Trim();
                    var bit = ServerValidation.ParseNamed(nv, false, name) ?? throw new DesignException($"\"{name}\" is not a {f.Label} name ({string.Join(", ", nv.Values.Select(v => v.Name))}).");
                    cur = on ? cur | bit : cur & ~bit;
                }
                stored = cur.ToString(CultureInfo.InvariantCulture);
            }
            else if (f.Kind == QuestFieldKind.Choice && f.Link == "dbc:QuestInfo" && !long.TryParse(t, out _))
                stored = ResolveQuestInfo(t).ToString(CultureInfo.InvariantCulture);
            else stored = QuestForm.ToStored(f, value, Classes(), ResolveCategory);
            var res = SetColumns(id, new[] { (f.Column, (string?)stored) }, $"{f.Label} of quest {id}");
            if (f.IsText && HasLocales(id)) Note($"Quest {id} has translations (locales_quest): they still show the old text in other languages.");
            var problems = res.Problems.Select(p => new DesignProblem(p.IsError ? DesignSeverity.Error : DesignSeverity.Warning, f.Id, p.Message)).ToList();
            if (f.IsText) problems.AddRange(QuestText.Check(f.Id, Quests.Has(f.Column) ? V(Quests, Require(id), f.Column) : null, Names.Races, Names.Classes));
            return DesignResult.Done($"{f.Label}: {Show(id, f)}", problems);
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    private (List<string> Races, List<string> Classes)? _names;
    public (List<string> Races, List<string> Classes) Names => _names ??= QuestText.Names(Session.Lookups);

    private long ResolveQuestInfo(string text)
    {
        var t = Session.Lookup("QuestInfo") ?? throw new DesignException("The game's QuestInfo table could not be read.");
        var c = t.Layout.Column("InfoName")!;
        foreach (var r in t.Rows)
            if (string.Equals(t.Text(r, c, 0), text, StringComparison.OrdinalIgnoreCase)) return t.Key(r) ?? 0;
        if (text.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0;
        throw new DesignException($"\"{text}\" is not a quest kind: {string.Join(", ", t.Rows.Select(r => t.Text(r, c, 0)))} or Normal.");
    }

    public bool HasLocales(long id) => HasTable("locales_quest") && Ed.Rows(T("locales_quest"), RowFilter.Eq("entry", id), 1).Count > 0;

    // ------------------------------------------------------------------ categories (QuestSort.dbc and zones)

    /// <summary>The quest log categories of the game (QuestSort.dbc): Warrior, Cooking, Seasonal...</summary>
    public List<(long Id, string Name)> Categories()
    {
        var t = Session.Lookup("QuestSort");
        if (t == null) return new();
        var c = t.Layout.Column("SortName")!;
        return t.Rows.Select(r => (t.Key(r) ?? 0, t.Text(r, c, 0))).Where(x => x.Item1 > 0).OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string CategoryText(long zoneOrSort)
    {
        if (zoneOrSort < 0)
        {
            var name = Categories().FirstOrDefault(c => c.Id == -zoneOrSort).Name;
            return $"sort:{name ?? (-zoneOrSort).ToString(CultureInfo.InvariantCulture)} ({zoneOrSort})";
        }
        return $"{ZoneName(zoneOrSort)} ({zoneOrSort})";
    }

    public string ZoneName(long zone) => Ed.Label("area", zone) ?? Ed.Label("dbc:AreaTable", zone) ?? $"zone {zone}";

    /// <summary>"sort:Warrior", "-81", "Westfall", "40" as the ZoneOrSort value.</summary>
    public long ResolveCategory(string text)
    {
        var t = text.Trim();
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (t.StartsWith("sort:", StringComparison.OrdinalIgnoreCase))
        {
            var name = t[5..].Trim();
            if (long.TryParse(name, out var sid)) return -Math.Abs(sid);
            var hit = Categories().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit.Name == null) throw new DesignException($"There is no quest category \"{name}\" in QuestSort.dbc (\"wds quests category list\"; \"wds quests category new\" makes one).");
            return -hit.Id;
        }
        var zone = t.StartsWith("zone:", StringComparison.OrdinalIgnoreCase) ? t[5..].Trim() : t;
        if (long.TryParse(zone, out var zid)) return zid;
        var at = HasTable("area_template") ? Ed.Rows(T("area_template"), RowFilter.Eq("name", zone), 5) : new List<string?[]>();
        if (at.Count > 0) return L(T("area_template"), at[0], "entry");
        var cat = Categories().FirstOrDefault(c => string.Equals(c.Name, zone, StringComparison.OrdinalIgnoreCase));
        if (cat.Name != null) return -cat.Id;
        throw new DesignException($"\"{zone}\" is neither a zone (area_template) nor a quest category (QuestSort.dbc).");
    }

    /// <summary>A new quest log category in QuestSort.dbc (a game table the server also reads: copy it to the server).</summary>
    public long NewCategory(string name)
    {
        if (Categories().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DesignException($"The category \"{name}\" exists already.");
        long id = Session.NextId("QuestSort");
        var row = Session.AddRow("QuestSort", id);
        Session.Set("QuestSort", row, "SortName", name);
        Note("QuestSort.dbc changed: the game shows the new category after \"Test in game\" or with the release patch; the server reads its own copy (\"Copy tables to the server...\").");
        return id;
    }

    // ------------------------------------------------------------------ names

    public string CreatureName(long entry) => Ed.Label("creature", entry) ?? $"creature {entry} (does not exist)";
    public string ObjectName(long entry) => Ed.Label("gameobject", entry) ?? $"object {entry} (does not exist)";
    public string ItemName(long entry) => Ed.Label("item", entry) ?? $"item {entry} (does not exist)";
    public string SpellName(long id) => Spells.Exists(id) ? Spells.Title(id) : Ed.Label("spell", id) ?? $"spell {id}";
    public string FactionName(long id) => Ed.Label("faction", id) ?? Ed.Label("dbc:Faction", id) ?? $"faction {id}";

    // ------------------------------------------------------------------ givers and enders

    private static readonly (string Kind, string Giver, string Ender)[] RelationTables =
    {
        ("creature", "creature_questrelation", "creature_involvedrelation"),
        ("object", "gameobject_questrelation", "gameobject_involvedrelation"),
    };

    /// <summary>Who offers a quest: creatures, game objects, items that start it.</summary>
    public List<QuestRelation> Givers(long quest)
    {
        var res = Relations(quest, giver: true);
        if (HasTable("item_template"))
        {
            var it = T("item_template");
            foreach (var row in Ed.Rows(it, RowFilter.Eq("start_quest", quest), 20).GroupBy(r => L(it, r, "entry")).Select(g => g.First()))
                res.Add(new QuestRelation("item", L(it, row, "entry"), V(it, row, "name") ?? "", "item_template", true, 0, "starts the quest when used"));
        }
        return res;
    }

    /// <summary>Who takes a quest back (and the area triggers that complete it).</summary>
    public List<QuestRelation> Enders(long quest) => Relations(quest, giver: false);

    public List<QuestRelation> AreaTriggers(long quest)
    {
        var res = new List<QuestRelation>();
        if (!HasTable("areatrigger_involvedrelation")) return res;
        var t = T("areatrigger_involvedrelation");
        foreach (var r in Ed.Rows(t, RowFilter.Eq("quest", quest)))
        {
            long id = L(t, r, "id");
            res.Add(new QuestRelation("trigger", id, $"area trigger {id}", t.Name, TriggerExists(id), 0, TriggerPlace(id)));
        }
        return res;
    }

    private List<QuestRelation> Relations(long quest, bool giver)
    {
        var res = new List<QuestRelation>();
        foreach (var (kind, g, e) in RelationTables)
        {
            var name = giver ? g : e;
            if (!HasTable(name)) continue;
            var t = T(name);
            foreach (var r in Ed.Rows(t, RowFilter.Eq("quest", quest)))
            {
                if (!Ed.Service.RowVersion(t, r).Loaded) continue;
                long id = L(t, r, "id");
                bool exists = kind == "creature" ? Ed.LinkExists("creature", id) : Ed.LinkExists("gameobject", id);
                string note = kind == "creature" && exists && ((Ed.CreatureFlags(id) ?? 0) & 0x2) == 0 ? "no Quest giver flag" : "";
                res.Add(new QuestRelation(kind, id, kind == "creature" ? CreatureName(id) : ObjectName(id), name, exists, Spawns(kind, id).Count, note));
            }
        }
        return res;
    }

    /// <summary>Quest-giver candidates by name or ID: creatures (quest givers first) and game objects, with how often they
    /// are placed in the world (the NPC picker).</summary>
    public List<QuestRelation> FindNpcs(string words, int max = 40)
    {
        var res = new List<QuestRelation>();
        var t = words.Trim();
        if (t.Length == 0) return res;
        var ct = T("creature_template");
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        RowFilter ByName(string col) => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? RowFilter.Eq(col == "name" ? "entry" : col, id)
            : RowFilter.Where(parts.Select(p => new FilterTerm("name", FilterOp.Contains, p)).ToArray());
        foreach (var row in Ed.Rows(ct, ByName("name"), 400).GroupBy(r => L(ct, r, "entry")).Select(g => Ed.Service.Effective(ct, g.ToList()) ?? g.First())
                     .OrderBy(r => (L(ct, r, "npc_flags") & 2) == 0).ThenBy(r => (V(ct, r, "name") ?? "").Length))
        {
            if (res.Count >= max) break;
            long e = L(ct, row, "entry");
            var sub = V(ct, row, "subname") ?? "";
            res.Add(new QuestRelation("creature", e, (V(ct, row, "name") ?? "") + (sub.Length > 0 ? $" <{sub}>" : ""), ct.Name, true, Spawns("creature", e, 50).Count,
                (L(ct, row, "npc_flags") & 2) != 0 ? "quest giver" : ""));
        }
        if (HasTable("gameobject_template") && res.Count < max)
        {
            var gt = T("gameobject_template");
            foreach (var row in Ed.Rows(gt, ByName("name"), 200).GroupBy(r => L(gt, r, "entry")).Select(g => g.First()).OrderBy(r => L(gt, r, "type") != 2))
            {
                if (res.Count >= max) break;
                long e = L(gt, row, "entry");
                res.Add(new QuestRelation("object", e, V(gt, row, "name") ?? "", gt.Name, true, Spawns("object", e, 50).Count, L(gt, row, "type") == 2 ? "quest giver object" : ""));
            }
        }
        return res;
    }

    /// <summary>"creature:234", "object:1618", "234" (a creature).</summary>
    public static (string Kind, long Id) ParseWho(string text)
    {
        var t = text.Trim();
        var parts = t.Split(':', 2);
        if (parts.Length == 2)
        {
            var kind = parts[0].ToLowerInvariant() switch
            {
                "creature" or "npc" or "c" => "creature",
                "object" or "gameobject" or "go" or "o" => "object",
                _ => throw new DesignException($"\"{parts[0]}\" is not creature or object."),
            };
            return long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? (kind, id) : throw new DesignException($"\"{parts[1]}\" is not an ID.");
        }
        return long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? ("creature", v) : throw new DesignException($"\"{t}\" is not a creature or object: creature:<ID>, object:<ID> or a creature ID.");
    }

    /// <summary>Add or remove a quest giver (<paramref name="giver"/>) or ender. A creature giver gets the Quest giver flag.</summary>
    public DesignResult SetRelation(long quest, string who, bool giver, bool add = true)
    {
        try
        {
            Require(quest);
            var (kind, id) = ParseWho(who);
            var table = kind == "creature" ? (giver ? "creature_questrelation" : "creature_involvedrelation") : (giver ? "gameobject_questrelation" : "gameobject_involvedrelation");
            var t = T(table);
            var existing = Ed.Rows(t, RowFilter.Where(new FilterTerm("id", FilterOp.Eq, id.ToString(CultureInfo.InvariantCulture)), new FilterTerm("quest", FilterOp.Eq, quest.ToString(CultureInfo.InvariantCulture))));
            string what = giver ? "giver" : "ender";
            string name = kind == "creature" ? CreatureName(id) : ObjectName(id);
            if (!add)
            {
                if (existing.Count == 0) return DesignResult.Refused($"{name} is not a quest {what} of quest {quest}.");
                Ed.Delete(table, existing.Select(t.KeyText));
                _touched.Add(quest);
                return DesignResult.Done($"{name} is no longer the quest {what} of {Label(quest)}.");
            }
            if (!(kind == "creature" ? Ed.LinkExists("creature", id) : Ed.LinkExists("gameobject", id)))
                return DesignResult.Refused($"There is no {kind} {id} on the server.");
            if (existing.Count > 0) return DesignResult.Done($"{name} is already the quest {what} of {Label(quest)}.");
            var values = new List<(string, string?)> { ("id", id.ToString(CultureInfo.InvariantCulture)), ("quest", quest.ToString(CultureInfo.InvariantCulture)) };
            if (t.Has("patch_min")) values.Add(("patch_min", "0"));
            if (t.Has("patch_max")) values.Add(("patch_max", "10"));
            if (kind == "creature") EnsureNpcFlag(id, 0x2);
            var (_, r) = Ed.Add(table, values, id, $"Quest {what} {name} for quest {quest}");
            if (!r.Ok) return DesignResult.Refused(r.Message);
            _touched.Add(quest);
            var problems = new List<DesignProblem>();
            if (Spawns(kind, id).Count == 0) problems.Add(new DesignProblem(DesignSeverity.Warning, what, $"{name} is not spawned anywhere: place it on the map (\"Place NPC...\", wds quests npc place)."));
            if (kind == "object" && GameObjectType(id) != 2) problems.Add(new DesignProblem(DesignSeverity.Error, what, $"{name} is not a quest giver object (type 2): the server ignores the row."));
            return DesignResult.Done($"{name} is now the quest {what} of {Label(quest)}.", problems);
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    public long GameObjectType(long entry)
    {
        if (!HasTable("gameobject_template")) return -1;
        var t = T("gameobject_template");
        return Ed.Object(t, entry) is { } r ? L(t, r, "type") : -1;
    }

    /// <summary>Give a creature an npc_flags bit (Quest giver 0x2, Gossip 0x1).</summary>
    public void EnsureNpcFlag(long creature, long bit)
    {
        var t = T("creature_template");
        var row = Ed.Object(t, creature);
        if (row == null) return;
        long flags = L(t, row, "npc_flags");
        if ((flags & bit) == bit) return;
        var r = Ed.Set(t.Name, t.KeyText(row), "npc_flags", (flags | bit).ToString(CultureInfo.InvariantCulture));
        if (!r.Ok) throw new DesignException(r.Message);
    }

    /// <summary>Where a creature or object stands (its spawns, at most 50).</summary>
    public List<QuestSpawn> Spawns(string kind, long entry, int max = 50)
    {
        var res = new List<QuestSpawn>();
        var table = kind == "creature" ? "creature" : "gameobject";
        if (!HasTable(table)) return res;
        var t = T(table);
        var filter = RowFilter.Eq("id", entry);
        if (kind == "creature")
            for (int k = 2; k <= 5; k++)
                if (t.Has("id" + k)) filter.Or(new FilterTerm("id" + k, FilterOp.Eq, entry.ToString(CultureInfo.InvariantCulture)));
        foreach (var r in Ed.Rows(t, filter, max))
            res.Add(new QuestSpawn(kind, L(t, r, "guid"), entry, L(t, r, "map"), WorldValue.Number(V(t, r, "position_x")), WorldValue.Number(V(t, r, "position_y")),
                WorldValue.Number(V(t, r, "position_z")), WorldValue.Number(V(t, r, "orientation"))));
        return res;
    }

    // ------------------------------------------------------------------ objectives in words

    public List<QuestObjective> Objectives(long quest)
    {
        var r = Require(quest);
        var res = new List<QuestObjective>();
        for (int k = 1; k <= 4; k++)
        {
            long target = Q(r, "ReqCreatureOrGOId" + k), count = Q(r, "ReqCreatureOrGOCount" + k), cast = Q(r, "ReqSpellCast" + k);
            if (target == 0 && count == 0) continue;
            string text = S(r, "ObjectiveText" + k);
            if (target > 0)
            {
                bool talk = IsTalkTarget(quest, target);
                res.Add(new QuestObjective(talk ? "talk" : cast != 0 ? "cast" : "kill", k, target, CreatureName(target), count,
                    text.Length > 0 ? text : talk ? $"Speak with {CreatureName(target)}" : cast != 0 ? $"Cast {SpellName(cast)} on {CreatureName(target)} x{count}" : $"{CreatureName(target)} slain: {count}"));
            }
            else res.Add(new QuestObjective(cast != 0 ? "cast" : "use", k, -target, ObjectName(-target), count, text.Length > 0 ? text : $"{ObjectName(-target)}: {count}"));
        }
        for (int k = 1; k <= 4; k++)
        {
            long item = Q(r, "ReqItemId" + k), count = Q(r, "ReqItemCount" + k);
            if (item == 0 && count == 0) continue;
            res.Add(new QuestObjective("item", k, item, ItemName(item), count, $"{ItemName(item)}: {count}"));
        }
        foreach (var at in AreaTriggers(quest)) res.Add(new QuestObjective("explore", 0, at.Id, at.Name, 1, $"Explore {at.Note}".Trim()));
        if (Q(r, "RepObjectiveFaction") != 0) res.Add(new QuestObjective("reputation", 0, Q(r, "RepObjectiveFaction"), FactionName(Q(r, "RepObjectiveFaction")), Q(r, "RepObjectiveValue"), $"Reach {Q(r, "RepObjectiveValue")} reputation with {FactionName(Q(r, "RepObjectiveFaction"))}"));
        if ((Q(r, "SpecialFlags") & 2) != 0 && res.All(o => o.Kind is not "explore")) res.Add(new QuestObjective("event", 0, 0, "", 1, "Completed by an event or script (escort, area, spell)"));
        return res;
    }

    /// <summary>Set an objective slot: a creature or object target with a count (and an optional spell to cast on it).</summary>
    public DesignResult SetTarget(long quest, int slot, string target, long count, long spell = 0, string? text = null)
    {
        if (slot is < 1 or > 4) return DesignResult.Refused("A quest has 4 kill / use objectives (1-4).");
        try
        {
            long v = QuestForm.ParseTarget(target);
            if (v > 0 && !Ed.LinkExists("creature", v)) return DesignResult.Refused($"There is no creature {v} on the server.");
            if (v < 0 && !Ed.LinkExists("gameobject", -v)) return DesignResult.Refused($"There is no game object {-v} on the server.");
            var values = new List<(string, string?)> { ($"ReqCreatureOrGOId{slot}", v.ToString(CultureInfo.InvariantCulture)), ($"ReqCreatureOrGOCount{slot}", (v == 0 ? 0 : Math.Max(1, count)).ToString(CultureInfo.InvariantCulture)), ($"ReqSpellCast{slot}", spell.ToString(CultureInfo.InvariantCulture)) };
            if (text != null) values.Add(($"ObjectiveText{slot}", text));
            SetColumns(quest, values, $"Objective {slot} of quest {quest}");
            var problems = new List<DesignProblem>();
            if (v != 0 && Spawns(v > 0 ? "creature" : "object", Math.Abs(v), 1).Count == 0)
                problems.Add(new DesignProblem(DesignSeverity.Warning, $"target{slot}", $"{(v > 0 ? CreatureName(v) : ObjectName(-v))} is not spawned anywhere: nobody can complete the objective unless a script summons it."));
            return DesignResult.Done(v == 0 ? $"Objective {slot} removed." : $"Objective {slot}: {(spell != 0 ? $"cast {SpellName(spell)} on" : v > 0 ? "kill" : "use")} {(v > 0 ? CreatureName(v) : ObjectName(-v))} x{Math.Max(1, count)}.", problems);
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    /// <summary>Set a "collect" objective slot.</summary>
    public DesignResult SetItem(long quest, int slot, long item, long count)
    {
        if (slot is < 1 or > 4) return DesignResult.Refused("A quest has 4 item objectives (1-4).");
        try
        {
            if (item != 0 && !Ed.LinkExists("item", item)) return DesignResult.Refused($"There is no item {item} on the server.");
            SetColumns(quest, new (string, string?)[] { ($"ReqItemId{slot}", item.ToString(CultureInfo.InvariantCulture)), ($"ReqItemCount{slot}", (item == 0 ? 0 : Math.Max(1, count)).ToString(CultureInfo.InvariantCulture)) }, $"Item objective {slot} of quest {quest}");
            var problems = new List<DesignProblem>();
            if (item != 0 && DropSources(item).Count == 0)
                problems.Add(new DesignProblem(DesignSeverity.Error, $"item{slot}", $"Nothing gives {ItemName(item)}: add a drop source (a creature's loot, \"Add drop source\")."));
            return DesignResult.Done(item == 0 ? $"Item objective {slot} removed." : $"Collect {ItemName(item)} x{Math.Max(1, count)}.", problems);
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    // ------------------------------------------------------------------ rewards

    /// <summary>The reward spell: shown in the reward window; with <paramref name="learn"/> the quest casts its learn spell
    /// so the player learns it (made with the spell designer when it has none).</summary>
    public DesignResult SetRewardSpell(long quest, long spell, bool learn = true)
    {
        try
        {
            Require(quest);
            if (spell == 0)
            {
                SetColumns(quest, new (string, string?)[] { ("RewSpell", "0"), ("RewSpellCast", "0") });
                return DesignResult.Done("The reward spell was removed.");
            }
            if (!Spells.Exists(spell)) return DesignResult.Refused($"There is no spell {spell} in the game's Spell.dbc.");
            if (!SpellIdLimits.Castable(spell)) return DesignResult.Refused(SpellIdLimits.Problem(spell));
            long cast = 0;
            string extra = "";
            if (learn)
            {
                cast = Spells.EnsureLearnSpell(spell, out bool created);
                Spells.SyncServer(new[] { spell, cast }.Where(s => Spells.Touched.Contains(s)));
                extra = created ? $" Made the learn spell {cast} (it teaches {Spells.Title(spell)}; saved with the quest)." : $" The quest casts the learn spell {cast}.";
            }
            else if (Spells.Touched.Contains(spell)) Spells.SyncServer(new[] { spell });
            if (Ed.Schema.Table("spell_template") is { } st)
                foreach (var sid in new[] { spell, cast }.Where(x => x != 0))
                    if (Ed.Object(st, sid) == null)
                        throw new DesignException(Spells.Touched.Contains(sid) ? $"The server's row of spell {sid} could not be made (spell_template)." : $"The server has no spell {sid} (spell_template): make it with the Spell designer (\"Sync with the server\") first.");
            SetColumns(quest, new (string, string?)[] { ("RewSpell", spell.ToString(CultureInfo.InvariantCulture)), ("RewSpellCast", cast.ToString(CultureInfo.InvariantCulture)) }, $"Reward spell of quest {quest}");
            return DesignResult.Done($"Reward: {(learn ? "learn" : "cast")} {Spells.Title(spell)}.{extra}");
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    // ------------------------------------------------------------------ preview

    /// <summary>The texts of a quest filled in for a player, as the game shows them.</summary>
    public List<(QuestField Field, QuestTextPreview Preview)> Preview(long quest, QuestTextPlayer player)
    {
        var r = Require(quest);
        return QuestForm.Fields.Where(f => f.IsText && Quests.Has(f.Column)).Select(f => (f, QuestText.Expand(V(Quests, r, f.Column), player))).ToList();
    }

    // ------------------------------------------------------------------ test in game

    /// <summary>How to try a quest in the game: the GM commands (quest add, teleport to the giver), after Apply to server.</summary>
    public List<string> TestInGame(long quest)
    {
        var s = Summary(quest);
        var lines = new List<string>
        {
            $"Trying \"{s.Title}\" ({quest}) in the game:",
            "1. Save to my workspace, then Apply to server (wds server apply --yes). The world server reads quests when it starts;",
            "   while it runs, a GM account can reload them: .reload all_quest (and .reload creature_questrelation / creature_involvedrelation).",
            "2. Clear the game's quest cache, or it keeps showing the old texts: Server data > Clear the game's cache (WDB)... (wds server clear-wdb --yes) while the game is closed.",
            "3. Log in with a GM character" + (s.Races != 0 ? $" of a race that can take it ({QuestForm.RaceText(s.Races)})" : "") + (s.Classes != 0 ? $" and class {QuestForm.ClassText(s.Classes, Classes())}" : "") + $", level {Math.Max(1, s.MinLevel)} or higher (.levelup N).",
        };
        var givers = Givers(quest);
        var g = givers.FirstOrDefault(x => x.Kind == "creature");
        if (g != null)
        {
            var sp = Spawns("creature", g.Id, 1).FirstOrDefault();
            lines.Add($"4. Go to the quest giver {g.Name}: .go creature id {g.Id}" + (sp != null ? $"   (or {sp.GoCommand})" : "   (it is not spawned: place it first)"));
        }
        else if (givers.FirstOrDefault(x => x.Kind == "object") is { } go)
        {
            var sp = Spawns("object", go.Id, 1).FirstOrDefault();
            lines.Add($"4. Go to the quest giver object {go.Name}" + (sp != null ? $": {sp.GoCommand}" : " (it is not spawned: place it first)"));
        }
        else lines.Add("4. The quest has no giver yet: give it one, or add it directly:");
        lines.Add($"5. Or add the quest directly to the selected player (or yourself): .quest add {quest}");
        lines.Add($"   Done at once: .quest complete {quest}   Start again: .quest remove {quest}   Find it: .lookup quest {s.Title}");
        // 0.13.2 (found in game): VMaNGOS gives .quest add / .quest complete to administrators only (SEC_BASIC_ADMIN = 4);
        // a game master (3) gets "There is no such subcommand" and sees only status and remove
        lines.Add("   (.quest add and .quest complete need an administrator account, GM level 4 or higher; a game master (level 3) has only .quest status and .quest remove: select each creature to kill and type .damage 100000, which counts as your kill.)");
        if (s.Prev != 0) lines.Add($"   It needs \"{Title(Math.Abs(s.Prev))}\" ({Math.Abs(s.Prev)}) {(s.Prev > 0 ? "done" : "in the quest log")} first: .quest add {Math.Abs(s.Prev)} then .quest complete {Math.Abs(s.Prev)}.");
        var ender = Enders(quest).FirstOrDefault(x => x.Kind == "creature");
        if (ender != null && ender.Id != g?.Id) lines.Add($"6. Hand it in to {ender.Name}: .go creature id {ender.Id}");
        return lines;
    }

    // ------------------------------------------------------------------ save

    /// <summary>Save every edited quest row, relation, NPC, spawn and loot row (and the game tables: categories, area
    /// triggers, reward spells) into the workspace in one change set. Refused while a text would overflow the game's
    /// quest cache (<see cref="QuestRules"/>).</summary>
    public DesignSaveResult Save(string? label = null)
    {
        var blocking = _touched.Where(Exists).SelectMany(id => QuestRules.TextProblems(this, id).Where(p => p.IsError).Select(p => $"{Label(id)}: {p.Text}")).ToList();
        if (blocking.Count > 0) throw new DesignException("Not saved: the game cannot hold these texts:\n" + string.Join("\n", blocking.Take(8)));
        var titles = _touched.Where(Exists).Select(Title).Take(3).ToList();
        var res = Session.Save(label ?? "Quest designer: " + (titles.Count == 0 ? "quests" : string.Join(", ", titles)) + (_touched.Count > 3 ? $" and {_touched.Count - 3} more" : ""), _ => Spells.SyncServer());
        if (res.ServerTables.Count > 0)
            res.Notes.Add("The game caches quests: after applying, clear WDB\\questcache.wdb (\"Clear the game's cache (WDB)...\", wds server clear-wdb) or it shows the old texts.");
        res.Notes.AddRange(_notes);
        _touched.Clear();
        _notes.Clear();
        return res;
    }

    /// <summary>Throw the unsaved edits away.</summary>
    public void Discard()
    {
        Session.Discard();
        _touched.Clear();
        _notes.Clear();
    }
}
