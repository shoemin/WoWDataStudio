using System.Globalization;
using System.Text.RegularExpressions;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>What to swap when a chain is copied for the other faction: NPCs, objects, items, zones, factions (by ID) and
/// words of the texts ("Sentinel Hill" -> "the Crossroads").</summary>
public sealed class QuestSwaps
{
    public Dictionary<(string Kind, long Id), long> Ids { get; } = new();
    public List<(string From, string To)> Texts { get; } = new();

    public static readonly IReadOnlyList<string> Kinds = new[] { "creature", "object", "item", "zone", "faction", "spell" };

    /// <summary>"234=3429" (creatures), "creature:234=3429", "object:1=2", "item:..", "zone:40=17", "faction:72=76",
    /// "spell:a=b", "text:Sentinel Hill=the Crossroads".</summary>
    public void Add(string text)
    {
        int eq = text.IndexOf('=');
        if (eq <= 0) throw new DesignException($"A swap is old=new (\"234=3429\", \"zone:40=17\", \"text:Sentinel Hill=the Crossroads\"), not \"{text}\".");
        var left = text[..eq].Trim();
        var right = text[(eq + 1)..].Trim();
        if (left.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
        {
            Texts.Add((left[5..], right));
            return;
        }
        string kind = "creature";
        var lp = left.Split(':', 2);
        if (lp.Length == 2)
        {
            kind = lp[0].ToLowerInvariant() switch { "npc" or "c" => "creature", "go" or "gameobject" or "o" => "object", var k => k };
            left = lp[1];
        }
        if (!Kinds.Contains(kind)) throw new DesignException($"\"{kind}\" cannot be swapped ({string.Join(", ", Kinds)}, text).");
        var rp = right.Split(':', 2);
        if (rp.Length == 2) right = rp[1];
        if (!long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) || !long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            throw new DesignException($"\"{text}\": both sides of a swap are IDs.");
        Ids[(kind, a)] = b;
    }

    public long Map(string kind, long id) => id != 0 && Ids.TryGetValue((kind, id), out var v) ? v : id;

    public string MapText(string text)
    {
        foreach (var (f, t) in Texts)
            if (f.Length > 0) text = Regex.Replace(text, Regex.Escape(f), t.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return text;
    }
}

/// <summary>The options of the "discipline chain" template (the Warrior's Stance Disciplines: talk, kill N, return,
/// learn the discipline).</summary>
public sealed class DisciplineOptions
{
    public string Discipline { get; set; } = "";
    /// <summary>"Alliance", "Horde" or "any".</summary>
    public string Faction { get; set; } = "any";
    public long Level { get; set; } = 20;
    public string Classes { get; set; } = "Warrior";
    /// <summary>The NPC who sends the player to the trainer (the "talk" step); null: no intro quest.</summary>
    public string? IntroGiver { get; set; }
    /// <summary>The NPC who gives the drill and takes it back (the drill sergeant, the champion).</summary>
    public string Giver { get; set; } = "";
    /// <summary>The outpost's name for the texts ("Sentinel Hill").</summary>
    public string Outpost { get; set; } = "";
    public string KillTarget { get; set; } = "";
    public long KillCount { get; set; } = 8;
    /// <summary>The reward spell (the discipline); 0: made by cloning <see cref="SpellTemplate"/>.</summary>
    public long RewardSpell { get; set; }
    public long SpellTemplate { get; set; }
    public string? SpellDescription { get; set; }
    public string Category { get; set; } = "sort:Warrior";
    public long Xp { get; set; }
    public long Money { get; set; }
}

/// <summary>What a chain operation made.</summary>
public sealed record QuestBuildResult(IReadOnlyList<long> Quests, string Message, IReadOnlyList<string> Notes, IReadOnlyDictionary<long, long>? Map = null, long Spell = 0);

public sealed partial class QuestDesigner
{
    private static readonly string[] CopyLike = { "QuestLevel", "MinLevel", "MaxLevel", "RequiredRaces", "RequiredClasses", "ZoneOrSort", "Type", "QuestFlags" };

    /// <summary>A new quest (the first free ID of your custom range), with the levels, races, classes and log category of
    /// <paramref name="like"/> when given.</summary>
    public long New(string title, long? id = null, long? like = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DesignException("A quest needs a title.");
        long qid = id ?? Ed.SuggestId(Quests);
        if (Exists(qid)) throw new DesignException($"Quest {qid} exists already.");
        var values = new List<(string, string?)> { ("Title", title), ("Method", "2"), ("QuestFlags", "8"), ("QuestLevel", "1"), ("MinLevel", "1") };
        if (Quests.Has("patch")) values.Add(("patch", "0"));
        if (like is { } l && Row(l) is { } lr)
            foreach (var c in CopyLike.Where(Quests.Has))
            {
                values.RemoveAll(v => v.Item1 == c);
                values.Add((c, V(Quests, lr, c)));
            }
        var (key, r) = Ed.Add("quest_template", values, qid, $"New quest {title}");
        if (!r.Ok || key == null) throw new DesignException(r.Message);
        _touched.Add(qid);
        return qid;
    }

    private void CopyRelations(long from, long to, bool fromEnders, bool giver)
    {
        foreach (var rel in fromEnders ? Enders(from) : Givers(from))
            if (rel.Kind is "creature" or "object")
            {
                var r = SetRelation(to, $"{rel.Kind}:{rel.Id}", giver);
                if (!r.Ok) throw new DesignException(r.Message);
            }
    }

    /// <summary>"Add a quest after": a new quest that needs <paramref name="after"/> done, offered by the NPC that takes
    /// <paramref name="after"/> back (right after it, as next in chain) and handed in to the same NPC.</summary>
    public long AddAfter(long after, string title)
    {
        var a = Summary(after);
        long b = New(title, like: after);
        var values = new List<(string, string?)> { ("PrevQuestId", after.ToString(CultureInfo.InvariantCulture)) };
        if (a.NextInChain != 0)
        {
            long c = a.NextInChain;
            values.Add(("NextQuestInChain", c.ToString(CultureInfo.InvariantCulture)));
            if (Row(c) is { } cr && Q(cr, "PrevQuestId") == after) SetColumn(c, "PrevQuestId", b);
        }
        SetColumns(b, values);
        SetColumn(after, "NextQuestInChain", b);
        CopyRelations(after, b, fromEnders: true, giver: true);
        CopyRelations(after, b, fromEnders: true, giver: false);
        return b;
    }

    /// <summary>"Add a quest before": a new quest that <paramref name="before"/> then needs done first; the NPC that offers
    /// <paramref name="before"/> offers the new one first and takes it back, then offers <paramref name="before"/>.</summary>
    public long AddBefore(long before, string title)
    {
        var a = Summary(before);
        long b = New(title, like: before);
        var values = new List<(string, string?)> { ("NextQuestInChain", before.ToString(CultureInfo.InvariantCulture)) };
        if (a.Prev != 0) values.Add(("PrevQuestId", a.Prev.ToString(CultureInfo.InvariantCulture)));
        SetColumns(b, values);
        foreach (var p in Ed.Rows(Quests, RowFilter.Eq("NextQuestInChain", before), 20).Select(r => Q(r, "entry")).Distinct().ToList())
            if (p != b) SetColumn(p, "NextQuestInChain", b);
        SetColumn(before, "PrevQuestId", b);
        CopyRelations(before, b, fromEnders: false, giver: true);
        foreach (var rel in Givers(before).Where(g => g.Kind is "creature" or "object"))
        {
            var r = SetRelation(b, $"{rel.Kind}:{rel.Id}", giver: false);
            if (!r.Ok) throw new DesignException(r.Message);
        }
        return b;
    }

    /// <summary>"Branch": follow-ups of a quest, each needing it done, offered by the NPC that takes it back. With
    /// <paramref name="exclusive"/> they form a choice group (taking one closes the others: "pick one of three rewards =
    /// one of three follow-ups"); <paramref name="rewards"/> gives each its reward item.</summary>
    public List<long> Branch(long from, IReadOnlyList<string> titles, bool exclusive = true, IReadOnlyList<long>? rewards = null)
    {
        if (titles.Count < 2) throw new DesignException("A branch has at least two follow-ups.");
        Require(from);
        var ids = new List<long>();
        long group = 0;
        for (int i = 0; i < titles.Count; i++)
        {
            long b = New(titles[i], like: from);
            if (group == 0 && exclusive) group = b;
            var values = new List<(string, string?)> { ("PrevQuestId", from.ToString(CultureInfo.InvariantCulture)) };
            if (exclusive) values.Add(("ExclusiveGroup", group.ToString(CultureInfo.InvariantCulture)));
            if (rewards != null && i < rewards.Count && rewards[i] != 0)
            {
                if (!Ed.LinkExists("item", rewards[i])) throw new DesignException($"There is no item {rewards[i]} on the server.");
                values.Add(("RewItemId1", rewards[i].ToString(CultureInfo.InvariantCulture)));
                values.Add(("RewItemCount1", "1"));
            }
            SetColumns(b, values);
            CopyRelations(from, b, fromEnders: true, giver: true);
            CopyRelations(from, b, fromEnders: true, giver: false);
            ids.Add(b);
        }
        // "next in chain" would favour one branch: the NPC lists them all instead
        if (Summary(from).NextInChain is var nic and not 0 && !ids.Contains(nic)) Note($"{Label(from)} still offers {Label(nic)} next in chain; the branches are offered by the same NPC.");
        return ids;
    }

    /// <summary>"Join": a quest after several: with <paramref name="all"/> every one of them must be done (an "all of"
    /// group), else any one of them (the branches of a choice).</summary>
    public long Join(IReadOnlyList<long> from, string title, bool all)
    {
        if (from.Count < 2) throw new DesignException("Join needs at least two quests.");
        var sums = from.Select(Summary).ToList();
        if (all && sums.Any(s => s.Group > 0)) throw new DesignException($"{string.Join(", ", sums.Where(s => s.Group > 0).Select(s => s.Title))} {(sums.Count(s => s.Group > 0) == 1 ? "is" : "are")} in a choice group: a player can do only one of them, so \"all of them\" can never be done. Join them with \"any one of\".");
        long m = New(title, like: from[0]);
        foreach (var s in sums)
        {
            var values = new List<(string, string?)> { ("NextQuestId", m.ToString(CultureInfo.InvariantCulture)) };
            if (all) values.Add(("ExclusiveGroup", (-m).ToString(CultureInfo.InvariantCulture)));
            SetColumns(s.Id, values);
        }
        CopyRelations(from[0], m, fromEnders: true, giver: true);
        CopyRelations(from[0], m, fromEnders: true, giver: false);
        return m;
    }

    /// <summary>Delete a quest (every version), its givers, enders, area triggers and translations; the links of other
    /// quests to it are cleared or (<paramref name="reconnect"/>) passed on to the quests around it.</summary>
    public DesignResult Delete(long id, bool reconnect = true)
    {
        try
        {
            var s = Summary(id);
            var msgs = new List<string>();
            foreach (var r in Ed.Rows(Quests, RowFilter.Eq("NextQuestInChain", id), 50).Select(r => Q(r, "entry")).Distinct().ToList())
                if (r != id) { SetColumn(r, "NextQuestInChain", reconnect ? s.NextInChain : 0); msgs.Add($"{Label(r)} next in chain: {(reconnect && s.NextInChain != 0 ? Label(s.NextInChain) : "none")}"); }
            foreach (var col in new[] { "PrevQuestId", "NextQuestId", "BreadcrumbForQuestId" })
            {
                if (!Quests.Has(col)) continue;
                foreach (var r in Ed.Rows(Quests, RowFilter.Eq(col, id).Or(new FilterTerm(col, FilterOp.Eq, (-id).ToString(CultureInfo.InvariantCulture))), 50).Select(r => Q(r, "entry")).Distinct().ToList())
                {
                    if (r == id) continue;
                    long nv = col == "PrevQuestId" && reconnect ? s.Prev : 0;
                    SetColumn(r, col, nv);
                    msgs.Add($"{Label(r)} {col}: {nv}");
                }
            }
            foreach (var table in new[] { "creature_questrelation", "creature_involvedrelation", "gameobject_questrelation", "gameobject_involvedrelation", "areatrigger_involvedrelation" })
                if (HasTable(table))
                {
                    var t = T(table);
                    var rows = Ed.Rows(t, RowFilter.Eq("quest", id));
                    if (rows.Count > 0) Ed.Delete(table, rows.Select(t.KeyText));
                }
            if (HasTable("locales_quest"))
            {
                var lt = T("locales_quest");
                var rows = Ed.Rows(lt, RowFilter.Eq("entry", id));
                if (rows.Count > 0) Ed.Delete(lt.Name, rows.Select(lt.KeyText));
            }
            var versions = Ed.Versions(Quests, id);
            Ed.Delete("quest_template", versions.Select(Quests.KeyText));
            _touched.Remove(id);
            foreach (var r in msgs) Note(r);
            return DesignResult.Done($"Deleted {s.Title} ({id})" + (msgs.Count > 0 ? $"; {msgs.Count} link(s) of other quests changed." : "."));
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    // ------------------------------------------------------------------ the same chain for the other faction

    private static readonly string[] TextColumns = { "Title", "Details", "Objectives", "OfferRewardText", "RequestItemsText", "EndText", "ObjectiveText1", "ObjectiveText2", "ObjectiveText3", "ObjectiveText4" };

    /// <summary>
    /// "Duplicate for the other faction": a copy of every quest of a chain (new IDs of your custom range) for
    /// <paramref name="faction"/> (Alliance, Horde or a race list), with its links between the copies, its givers and
    /// enders swapped by <paramref name="swaps"/> (NPCs and objects, the objective's creatures, items, the log's zone,
    /// reputation factions, words of the texts). What is not swapped stays (the checks say when the new faction cannot
    /// talk to an NPC). Area-trigger objectives are not copied (a trigger serves one quest).
    /// </summary>
    public QuestBuildResult DuplicateForFaction(IReadOnlyList<long> quests, string faction, QuestSwaps swaps, string? titleSuffix = null)
    {
        if (quests.Count == 0) throw new DesignException("Nothing to duplicate.");
        long races = QuestForm.ParseRaces(faction);
        if (races == 0) throw new DesignException("Give the faction of the copy: Alliance, Horde or races.");
        var map = new Dictionary<long, long>();
        var notes = new List<string>();
        var ordered = quests.Distinct().OrderBy(x => x).ToList();
        foreach (var id in ordered) Require(id);
        // new IDs first (links between the copies need them)
        foreach (var id in ordered)
        {
            long nid = Ed.SuggestId(Quests);
            while (map.ContainsValue(nid) || Exists(nid)) nid++;
            var row = Require(id);
            var values = new List<(string, string?)> { ("RequiredRaces", races.ToString(CultureInfo.InvariantCulture)) };
            if (Quests.Has("patch")) values.Add(("patch", "0"));
            foreach (var c in TextColumns.Where(Quests.Has))
            {
                var tx = S(row, c);
                var nt = swaps.MapText(tx);
                if (c == "Title" && titleSuffix != null) nt += titleSuffix;
                if (nt != tx) values.Add((c, nt));
            }
            void Swap(string col, string kind)
            {
                if (!Quests.Has(col)) return;
                long v = Q(row, col);
                long nv = swaps.Map(kind, v);
                if (nv != v) values.Add((col, nv.ToString(CultureInfo.InvariantCulture)));
            }
            for (int k = 1; k <= 4; k++)
            {
                long tgt = Q(row, "ReqCreatureOrGOId" + k);
                long nt = tgt > 0 ? swaps.Map("creature", tgt) : tgt < 0 ? -swaps.Map("object", -tgt) : 0;
                if (nt != tgt) values.Add(("ReqCreatureOrGOId" + k, nt.ToString(CultureInfo.InvariantCulture)));
                Swap("ReqItemId" + k, "item");
                Swap("ReqSourceId" + k, "item");
                Swap("RewItemId" + k, "item");
                Swap("ReqSpellCast" + k, "spell");
            }
            for (int k = 1; k <= 6; k++) Swap("RewChoiceItemId" + k, "item");
            for (int k = 1; k <= 5; k++) Swap("RewRepFaction" + k, "faction");
            foreach (var c in new[] { "RequiredMinRepFaction", "RequiredMaxRepFaction", "RepObjectiveFaction" }) Swap(c, "faction");
            Swap("SrcItemId", "item");
            foreach (var c in new[] { "RewSpell", "RewSpellCast", "SrcSpell" }) Swap(c, "spell");
            long zone = Q(row, "ZoneOrSort");
            if (zone > 0 && swaps.Map("zone", zone) is var nz && nz != zone) values.Add(("ZoneOrSort", nz.ToString(CultureInfo.InvariantCulture)));
            var (key, r) = Ed.Duplicate("quest_template", Quests.KeyText(row), values, nid);
            if (!r.Ok || key == null) throw new DesignException($"Quest {id} could not be copied: {r.Message}");
            map[id] = nid;
            _touched.Add(nid);
            if (Q(row, "StartScript") != 0 || Q(row, "CompleteScript") != 0)
                notes.Add($"{Label(nid)} uses the same start / end scripts as {id}: check that they suit the other faction.");
            if (AreaTriggers(id).Count > 0) notes.Add($"{Label(id)} is completed by an area trigger; a trigger serves one quest, so the copy {nid} needs its own (\"Explore\").");
            if (HasLocales(id)) notes.Add($"{Label(id)} has translations (locales_quest); the copy {nid} has none yet.");
        }
        // the links between the copies, and exclusive groups
        var groupMap = new Dictionary<long, long>();
        foreach (var (old, nid) in map)
        {
            var row = Require(nid);
            var values = new List<(string, string?)>();
            void Link(string col)
            {
                if (!Quests.Has(col)) return;
                long v = Q(row, col);
                if (v != 0 && map.TryGetValue(Math.Abs(v), out var n)) values.Add((col, (Math.Sign(v) * n).ToString(CultureInfo.InvariantCulture)));
            }
            Link("PrevQuestId");
            Link("NextQuestId");
            Link("NextQuestInChain");
            Link("BreadcrumbForQuestId");
            long g = Q(row, "ExclusiveGroup");
            if (g != 0)
            {
                if (!groupMap.TryGetValue(g, out var ng))
                    groupMap[g] = ng = Math.Sign(g) * (map.TryGetValue(Math.Abs(g), out var byId) ? byId : map.Where(kv => Q(Require(kv.Key), "ExclusiveGroup") == g).Select(kv => kv.Value).DefaultIfEmpty(nid).Min());
                values.Add(("ExclusiveGroup", ng.ToString(CultureInfo.InvariantCulture)));
            }
            if (values.Count > 0) SetColumns(nid, values, $"Links of the copy {nid}");
            foreach (var (rel, giver) in Givers(old).Select(x => (x, true)).Concat(Enders(old).Select(x => (x, false))))
            {
                if (rel.Kind is not ("creature" or "object")) continue;
                long nidRel = swaps.Map(rel.Kind, rel.Id);
                var res = SetRelation(nid, $"{rel.Kind}:{nidRel}", giver);
                if (!res.Ok) throw new DesignException(res.Message);
                if (nidRel == rel.Id) notes.Add($"{Label(nid)}: {(giver ? "giver" : "ender")} {rel.Name} kept (no swap given).");
            }
        }
        foreach (var (kind, id) in swaps.Ids.Keys.Where(k => k.Kind is "creature" or "object"))
            if (!(kind == "creature" ? Ed.LinkExists("creature", swaps.Ids[(kind, id)]) : Ed.LinkExists("gameobject", swaps.Ids[(kind, id)])))
                notes.Add($"The swap {kind}:{id}={swaps.Ids[(kind, id)]} names a {kind} that does not exist.");
        foreach (var n in notes) Note(n);
        return new QuestBuildResult(map.Values.ToList(), $"Copied {map.Count} quest(s) for {QuestForm.RaceText(races)}: " + string.Join(", ", map.Select(kv => $"{kv.Key} -> {kv.Value}")) + ".", notes, map);
    }

    // ------------------------------------------------------------------ template: a discipline chain

    /// <summary>
    /// The "discipline chain" template of the Warrior's Stance Disciplines (VANILLA_HERO_PROGRESSION.md W.3 / W.4): an
    /// optional "talk" step (the outpost's commander sends the player to the drill sergeant or champion), then the drill:
    /// kill N, return to the same NPC, learn the discipline (a reward spell taught through its learn spell; cloned from a
    /// template spell with the spell designer when none is given). Texts are filled with the names and $N / $C tokens.
    /// </summary>
    public QuestBuildResult Discipline(DisciplineOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Discipline)) throw new DesignException("Name the discipline.");
        var (gk, giver) = ParseWho(o.Giver);
        if (gk != "creature" || !Ed.LinkExists("creature", giver)) throw new DesignException($"The drill's NPC must be an existing creature (\"{o.Giver}\").");
        long target = QuestForm.ParseTarget(o.KillTarget);
        if (target <= 0 || !Ed.LinkExists("creature", target)) throw new DesignException($"The creature to fight must exist (\"{o.KillTarget}\").");
        long races = QuestForm.ParseRaces(o.Faction);
        long classes = QuestForm.ParseClasses(o.Classes, Classes());
        long cat = ResolveCategory(o.Category);
        string giverName = CreatureName(giver), foe = CreatureName(target), foes = Plural(foe);
        string outpost = o.Outpost.Length > 0 ? o.Outpost : "the outpost";
        var notes = new List<string>();
        // the reward spell
        long spell = o.RewardSpell;
        if (spell == 0)
        {
            if (o.SpellTemplate == 0) throw new DesignException("Give the discipline's spell (an existing one) or a template spell to clone it from.");
            spell = Spells.Clone(o.SpellTemplate, o.Discipline);
            if (o.SpellDescription != null) { var r0 = Spells.Set(spell, "description", o.SpellDescription); if (!r0.Ok) throw new DesignException(r0.Message); }
            notes.Add($"Made the discipline's spell {spell} ({o.Discipline}) from spell {o.SpellTemplate}: shape it in the Spell designer.");
        }
        else if (!Spells.Exists(spell)) throw new DesignException($"There is no spell {spell}.");
        var ids = new List<long>();
        long intro = 0;
        if (o.IntroGiver is { Length: > 0 } ig)
        {
            var (ik, iid) = ParseWho(ig);
            if (ik != "creature" || !Ed.LinkExists("creature", iid)) throw new DesignException($"The NPC of the talk step must be an existing creature (\"{ig}\").");
            intro = New($"{o.Discipline}: Report to {giverName}");
            SetColumns(intro, new (string, string?)[]
            {
                ("QuestLevel", o.Level.ToString(CultureInfo.InvariantCulture)), ("MinLevel", o.Level.ToString(CultureInfo.InvariantCulture)),
                ("RequiredRaces", races.ToString(CultureInfo.InvariantCulture)), ("RequiredClasses", classes.ToString(CultureInfo.InvariantCulture)), ("ZoneOrSort", cat.ToString(CultureInfo.InvariantCulture)),
                ("Details", $"You have the look of a $c who has stood in a few battle lines, $N. {giverName} at {outpost} trains warriors in the {o.Discipline}. Go and report to {giverName}; tell them I sent you."),
                ("Objectives", $"Report to {giverName} at {outpost}."),
                ("OfferRewardText", $"So you are the $c they sent. Good. We start at once."),
                ("RewXP", Math.Max(0, o.Xp / 4).ToString(CultureInfo.InvariantCulture)),
            });
            Must(SetRelation(intro, $"creature:{iid}", giver: true));
            Must(SetRelation(intro, $"creature:{giver}", giver: false));
            ids.Add(intro);
        }
        long drill = New(o.Discipline);
        var values = new List<(string, string?)>
        {
            ("QuestLevel", o.Level.ToString(CultureInfo.InvariantCulture)), ("MinLevel", o.Level.ToString(CultureInfo.InvariantCulture)),
            ("RequiredRaces", races.ToString(CultureInfo.InvariantCulture)), ("RequiredClasses", classes.ToString(CultureInfo.InvariantCulture)), ("ZoneOrSort", cat.ToString(CultureInfo.InvariantCulture)),
            ("Details", $"The {o.Discipline} is not learned on a training dummy, $N. Out there are {foes}; face {o.KillCount} of them and come back to me. Then I will teach you the rest."),
            ("Objectives", $"Defeat {o.KillCount} {(o.KillCount == 1 ? foe : foes)}, then return to {giverName} at {outpost}."),
            ("RequestItemsText", $"Back already, $N? The drill is not finished."),
            ("OfferRewardText", $"Well fought, $c. You have earned the {o.Discipline}: set it in your stance and it will serve you."),
            ("ReqCreatureOrGOId1", target.ToString(CultureInfo.InvariantCulture)), ("ReqCreatureOrGOCount1", Math.Max(1, o.KillCount).ToString(CultureInfo.InvariantCulture)),
            ("RewXP", o.Xp.ToString(CultureInfo.InvariantCulture)), ("RewOrReqMoney", o.Money.ToString(CultureInfo.InvariantCulture)),
        };
        if (intro != 0) values.Add(("PrevQuestId", intro.ToString(CultureInfo.InvariantCulture)));
        SetColumns(drill, values);
        if (intro != 0) SetColumn(intro, "NextQuestInChain", drill);
        Must(SetRelation(drill, $"creature:{giver}", giver: true));
        Must(SetRelation(drill, $"creature:{giver}", giver: false));
        Must(SetRewardSpell(drill, spell, learn: true));
        ids.Add(drill);
        foreach (var n in notes) Note(n);
        return new QuestBuildResult(ids, $"Made the {o.Discipline} chain for {QuestForm.RaceText(races)}: " + string.Join(", ", ids.Select(Label)) + $"; reward spell {Spells.Title(spell)}.", notes, null, spell);
    }

    private static void Must(DesignResult r)
    {
        if (!r.Ok) throw new DesignException(r.Message);
    }

    /// <summary>A creature's name in the plural for the template's texts ("Defias Highwayman" -> "Defias Highwaymen").</summary>
    public static string Plural(string name)
    {
        if (name.EndsWith("man", StringComparison.Ordinal)) return name[..^3] + "men";
        if (name.EndsWith("wolf", StringComparison.OrdinalIgnoreCase)) return name[..^1] + "ves";
        if (name.EndsWith('s') || name.EndsWith('x') || name.EndsWith("ch", StringComparison.Ordinal) || name.EndsWith("sh", StringComparison.Ordinal)) return name + "es";
        if (name.Length > 1 && name.EndsWith('y') && !"aeiou".Contains(name[^2])) return name[..^1] + "ies";
        return name + "s";
    }
}
