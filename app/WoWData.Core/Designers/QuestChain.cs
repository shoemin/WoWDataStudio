using System.Globalization;
using System.Text;
using WoWData.Core.ServerData;

namespace WoWData.Core.Designers;

/// <summary>How two quests of a chain are linked.</summary>
[Flags]
public enum QuestLinkKind
{
    None = 0,
    /// <summary>B's PrevQuestId is A: A must be done first.</summary>
    Requires = 1,
    /// <summary>B's PrevQuestId is -A: A must be in the quest log.</summary>
    RequiresActive = 2,
    /// <summary>A's NextQuestId is B: A opens B (B needs A done).</summary>
    Opens = 4,
    /// <summary>A's NextQuestInChain is B: the NPC offers B right after A is handed in.</summary>
    NextInChain = 8,
    /// <summary>A's BreadcrumbForQuestId is B: A only leads to B.</summary>
    Breadcrumb = 16,
}

/// <summary>A quest in the chain view: where it is drawn (lane, column, row) and its facts.</summary>
public sealed class QuestNode
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public long Level { get; init; }
    public long MinLevel { get; init; }
    public long Races { get; init; }
    public long Classes { get; init; }
    public long Group { get; init; }
    public string Lane { get; set; } = "";
    public int LaneIndex { get; set; }
    public int Column { get; set; }
    public int Row { get; set; }
    public bool Edited { get; init; }
    public string Givers { get; init; } = "";
    public string Enders { get; init; } = "";
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public string Text => $"{Title} [{Id}]";
}

/// <summary>A link between two quests (several kinds may be set at once).</summary>
public sealed record QuestEdge(long From, long To, QuestLinkKind Kinds)
{
    public string Text => string.Join(" + ", new[]
    {
        (QuestLinkKind.Requires, "must be done first"), (QuestLinkKind.RequiresActive, "must be in the quest log"), (QuestLinkKind.Opens, "opens"),
        (QuestLinkKind.NextInChain, "next in chain"), (QuestLinkKind.Breadcrumb, "breadcrumb"),
    }.Where(k => (Kinds & k.Item1) != 0).Select(k => k.Item2));
}

/// <summary>An exclusive group: quests of a positive group exclude each other (a choice), of a negative group all must be
/// done before the quest they open.</summary>
public sealed record QuestGroup(long Id, IReadOnlyList<long> Quests)
{
    public bool IsChoice => Id > 0;
    public string Text => IsChoice ? $"Choice (exclusive group {Id}): taking one closes the others" : $"All of group {Id} must be done first";
}

/// <summary>One lane of the chain view: the quests of one faction (or race / class restriction).</summary>
public sealed record QuestLane(int Index, string Name, long Races, long Classes);

/// <summary>A quest chain: its quests (nodes), links (edges), exclusive groups and lanes, laid out in columns.</summary>
public sealed class QuestChain
{
    public List<QuestNode> Nodes { get; } = new();
    public List<QuestEdge> Edges { get; } = new();
    public List<QuestGroup> Groups { get; } = new();
    public List<QuestLane> Lanes { get; } = new();
    /// <summary>Quests outside the chain that it links to (not drawn), with the reason.</summary>
    public List<string> Outside { get; } = new();
    public long Start { get; init; }
    public bool Truncated { get; set; }
    public int Columns => Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Column) + 1;

    public QuestNode? Node(long id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The chain as text: lane by lane, column by column, with the links.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var lane in Lanes)
        {
            sb.Append("== ").Append(lane.Name).Append('\n');
            foreach (var n in Nodes.Where(x => x.LaneIndex == lane.Index).OrderBy(x => x.Column).ThenBy(x => x.Row))
            {
                sb.Append($"  [{n.Column + 1}] {n.Title} ({n.Id}), level {n.Level}" + (n.MinLevel > 0 && n.MinLevel != n.Level ? $" (from {n.MinLevel})" : ""));
                if (n.Givers.Length > 0) sb.Append($", from {n.Givers}");
                if (n.Enders.Length > 0 && n.Enders != n.Givers) sb.Append($", to {n.Enders}");
                if (n.Errors + n.Warnings > 0) sb.Append($"  !{n.Errors} error(s), {n.Warnings} warning(s)");
                sb.Append('\n');
                foreach (var e in Edges.Where(x => x.From == n.Id))
                    sb.Append($"       -> {Node(e.To)?.Title ?? "#" + e.To} ({e.To}): {e.Text}\n");
            }
        }
        foreach (var g in Groups) sb.Append($"  {g.Text}: {string.Join(", ", g.Quests.Select(q => Node(q)?.Title ?? "#" + q))}\n");
        foreach (var o in Outside) sb.Append("  Outside the chain: ").Append(o).Append('\n');
        return sb.ToString();
    }

    /// <summary>A Mermaid flowchart of the chain (for wikis and design documents).</summary>
    public string Mermaid()
    {
        var sb = new StringBuilder("flowchart LR\n");
        foreach (var lane in Lanes)
        {
            sb.Append($"  subgraph L{lane.Index}[\"{lane.Name}\"]\n");
            foreach (var n in Nodes.Where(x => x.LaneIndex == lane.Index).OrderBy(x => x.Column))
                sb.Append($"    q{n.Id}[\"{n.Title.Replace("\"", "'")}<br/>{n.Id} · level {n.Level}\"]\n");
            sb.Append("  end\n");
        }
        foreach (var e in Edges)
            sb.Append($"  q{e.From} {((e.Kinds & QuestLinkKind.Breadcrumb) != 0 ? "-.->" : (e.Kinds & QuestLinkKind.NextInChain) != 0 ? "==>" : "-->")}|{e.Text}| q{e.To}\n");
        return sb.ToString();
    }
}

/// <summary>
/// 0.13.0: finds and lays out quest chains. A chain is every quest linked to the start quest by PrevQuestId, NextQuestId,
/// NextQuestInChain, BreadcrumbForQuestId or an exclusive group (followed both ways, as VMaNGOS's ObjectMgr::LoadQuests
/// builds prevQuests / prevChainQuests). Columns follow the "must come first" links (longest path; cycles are cut and
/// reported by the checks); lanes are the race / class restrictions (Alliance, Horde, both; a class).
/// </summary>
public static class QuestChains
{
    public const int MaxQuests = 150;

    /// <summary>The quests of the chain of <paramref name="start"/> (your edits included).</summary>
    public static List<long> Members(QuestDesigner d, long start, int max = MaxQuests)
    {
        var seen = new HashSet<long> { start };
        var queue = new Queue<long>();
        queue.Enqueue(start);
        var t = d.Quests;
        while (queue.Count > 0 && seen.Count < max)
        {
            long id = queue.Dequeue();
            var r = d.Row(id);
            if (r == null) continue;
            var linked = new List<long> { Math.Abs(d.Q(r, "PrevQuestId")), Math.Abs(d.Q(r, "NextQuestId")), d.Q(r, "NextQuestInChain") };
            if (t.Has("BreadcrumbForQuestId")) linked.Add(d.Q(r, "BreadcrumbForQuestId"));
            string s = id.ToString(CultureInfo.InvariantCulture), neg = (-id).ToString(CultureInfo.InvariantCulture);
            var f = RowFilter.Eq("PrevQuestId", id).Or(new FilterTerm("PrevQuestId", FilterOp.Eq, neg)).Or(new FilterTerm("NextQuestId", FilterOp.Eq, s))
                .Or(new FilterTerm("NextQuestId", FilterOp.Eq, neg)).Or(new FilterTerm("NextQuestInChain", FilterOp.Eq, s));
            if (t.Has("BreadcrumbForQuestId")) f.Or(new FilterTerm("BreadcrumbForQuestId", FilterOp.Eq, s));
            long g = d.Q(r, "ExclusiveGroup");
            if (g != 0) f.Or(new FilterTerm("ExclusiveGroup", FilterOp.Eq, g.ToString(CultureInfo.InvariantCulture)));
            foreach (var row in d.Ed.Rows(t, f, 400)) linked.Add(d.Q(row, "entry"));
            foreach (var l in linked)
                if (l > 0 && !seen.Contains(l) && d.Exists(l) && seen.Count < max)
                {
                    seen.Add(l);
                    queue.Enqueue(l);
                }
        }
        return seen.OrderBy(x => x).ToList();
    }

    /// <summary>The chain of a quest, laid out.</summary>
    public static QuestChain Open(QuestDesigner d, long start, bool withChecks = true)
    {
        d.Require(start);
        var ids = Members(d, start);
        var chain = Build(d, ids, start);
        chain.Truncated = ids.Count >= MaxQuests;
        if (withChecks)
            foreach (var p in QuestRules.Check(d, ids))
                if (chain.Node(p.Quest) is { } n)
                {
                    if (p.Problem.Severity == DesignSeverity.Error) n.Errors++;
                    else if (p.Problem.Severity == DesignSeverity.Warning) n.Warnings++;
                }
        return chain;
    }

    /// <summary>Lay out a set of quests (the edges between them, groups, lanes, columns).</summary>
    public static QuestChain Build(QuestDesigner d, IReadOnlyCollection<long> ids, long start)
    {
        var chain = new QuestChain { Start = start };
        var set = ids.ToHashSet();
        var sums = ids.Where(d.Exists).ToDictionary(id => id, d.Summary);
        var edges = new Dictionary<(long, long), QuestLinkKind>();
        void Link(long a, long b, QuestLinkKind k)
        {
            if (a == b || a == 0 || b == 0) return;
            if (!set.Contains(a) || !set.Contains(b))
            {
                chain.Outside.Add($"{(set.Contains(a) ? d.Label(a) : $"quest {a}")} {k switch { QuestLinkKind.Requires => "is needed by", QuestLinkKind.RequiresActive => "must be active for", QuestLinkKind.Opens => "opens", QuestLinkKind.NextInChain => "leads to", _ => "is a breadcrumb for" }} {(set.Contains(b) ? d.Label(b) : $"quest {b}{(d.Exists(b) ? "" : " (does not exist)")}")}");
                return;
            }
            edges[(a, b)] = edges.GetValueOrDefault((a, b)) | k;
        }
        foreach (var s in sums.Values)
        {
            if (s.Prev > 0) Link(s.Prev, s.Id, QuestLinkKind.Requires);
            if (s.Prev < 0) Link(-s.Prev, s.Id, QuestLinkKind.RequiresActive);
            if (s.Next != 0) Link(s.Id, Math.Abs(s.Next), QuestLinkKind.Opens);
            if (s.NextInChain != 0) Link(s.Id, s.NextInChain, QuestLinkKind.NextInChain);
            if (s.Breadcrumb != 0) Link(s.Id, s.Breadcrumb, QuestLinkKind.Breadcrumb);
        }
        chain.Edges.AddRange(edges.Select(kv => new QuestEdge(kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderBy(e => e.From).ThenBy(e => e.To));
        foreach (var g in sums.Values.Where(s => s.Group != 0).GroupBy(s => s.Group).OrderBy(g => g.Key))
            chain.Groups.Add(new QuestGroup(g.Key, g.Select(s => s.Id).OrderBy(x => x).ToList()));

        // columns: longest path over the links (a link inside a cycle is ignored for the layout)
        var col = sums.Keys.ToDictionary(k => k, _ => 0);
        var incoming = chain.Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Select(e => e.From).ToList());
        var order = TopoOrder(sums.Keys, chain.Edges);
        foreach (var id in order)
            if (incoming.TryGetValue(id, out var from))
                foreach (var f in from)
                    if (order.IndexOf(f) < order.IndexOf(id)) col[id] = Math.Max(col[id], col[f] + 1);
        // quests of a choice group share their column
        foreach (var g in chain.Groups.Where(g => g.IsChoice))
        {
            int c = g.Quests.Max(q => col.GetValueOrDefault(q));
            foreach (var q in g.Quests) col[q] = c;
        }

        // lanes: races (Alliance, both, Horde, others) and classes
        string LaneName(QuestSummary s)
        {
            string races = QuestForm.RaceText(s.Races) switch { "any" => "Both factions", var x => x };
            return s.Classes == 0 ? races : $"{races} · {QuestForm.ClassText(s.Classes, d.Classes())}";
        }
        int LaneOrder(QuestSummary s) => QuestForm.Teams(s.Races) switch { (true, false) => 0, (true, true) => 1, (false, true) => 2, _ => 3 };
        var lanes = sums.Values.GroupBy(LaneName).OrderBy(g => LaneOrder(g.First())).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        for (int i = 0; i < lanes.Count; i++)
        {
            var first = lanes[i].First();
            chain.Lanes.Add(new QuestLane(i, lanes[i].Key, first.Races, first.Classes));
        }
        var laneOf = lanes.SelectMany((g, i) => g.Select(s => (s.Id, i))).ToDictionary(x => x.Id, x => x.i);
        foreach (var s in sums.Values.OrderBy(s => col[s.Id]).ThenBy(s => s.Group == 0 ? long.MaxValue : s.Group).ThenBy(s => s.Id))
        {
            int lane = laneOf[s.Id];
            int row = chain.Nodes.Count(n => n.LaneIndex == lane && n.Column == col[s.Id]);
            chain.Nodes.Add(new QuestNode
            {
                Id = s.Id, Title = s.Title, Level = s.Level, MinLevel = s.MinLevel, Races = s.Races, Classes = s.Classes, Group = s.Group, Edited = s.Edited,
                Lane = chain.Lanes[lane].Name, LaneIndex = lane, Column = col[s.Id], Row = row,
                Givers = string.Join(", ", d.Givers(s.Id).Select(g => g.Name).Distinct().Take(3)),
                Enders = string.Join(", ", d.Enders(s.Id).Select(g => g.Name).Distinct().Take(3)),
            });
        }
        return chain;
    }

    /// <summary>Quests ordered so that every link goes forward where possible (Kahn; quests in a cycle come last, by ID).</summary>
    public static List<long> TopoOrder(IEnumerable<long> ids, IEnumerable<QuestEdge> edges)
    {
        var nodes = ids.ToList();
        var e = edges.Where(x => nodes.Contains(x.From) && nodes.Contains(x.To)).ToList();
        var indeg = nodes.ToDictionary(n => n, n => e.Count(x => x.To == n));
        var ready = new SortedSet<long>(nodes.Where(n => indeg[n] == 0));
        var res = new List<long>();
        while (ready.Count > 0)
        {
            long n = ready.Min;
            ready.Remove(n);
            res.Add(n);
            foreach (var x in e.Where(x => x.From == n))
                if (--indeg[x.To] == 0) ready.Add(x.To);
        }
        res.AddRange(nodes.Where(n => !res.Contains(n)).OrderBy(n => n));
        return res;
    }

    /// <summary>The cycles of "must come first" links (each as its quests), for the checks.</summary>
    public static List<List<long>> Cycles(IEnumerable<long> ids, IEnumerable<QuestEdge> edges)
    {
        var nodes = ids.ToList();
        var adj = nodes.ToDictionary(n => n, n => edges.Where(x => x.From == n && nodes.Contains(x.To) && (x.Kinds & ~QuestLinkKind.Breadcrumb) != 0).Select(x => x.To).ToList());
        var res = new List<List<long>>();
        var state = new Dictionary<long, int>();
        var stack = new List<long>();
        void Visit(long n)
        {
            state[n] = 1;
            stack.Add(n);
            foreach (var m in adj[n])
            {
                if (state.GetValueOrDefault(m) == 1)
                {
                    var cyc = stack.Skip(stack.IndexOf(m)).ToList();
                    if (!res.Any(c => c.Count == cyc.Count && c.All(cyc.Contains))) res.Add(cyc);
                }
                else if (state.GetValueOrDefault(m) == 0) Visit(m);
            }
            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
        }
        foreach (var n in nodes.OrderBy(x => x)) if (state.GetValueOrDefault(n) == 0) Visit(n);
        return res;
    }
}
