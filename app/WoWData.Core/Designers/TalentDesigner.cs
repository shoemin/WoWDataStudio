using System.Globalization;
using System.Text;
using WoWData.Core.Dbc;

namespace WoWData.Core.Designers;

/// <summary>One talent: its place in the tree, its rank spells, what must be learned first, and its name and icon (from
/// the rank 1 spell).</summary>
public sealed record TalentNode(long Id, long Tab, int Tier, int Column, IReadOnlyList<long> Ranks, IReadOnlyList<(long Talent, int Rank)> Prereqs, string Name, string Icon, long RequiredSpell)
{
    public int MaxRank => Ranks.Count;
}

/// <summary>One talent tree (a TalentTab row) and its talents.</summary>
public sealed record TalentTree(long Id, string Name, long ClassMask, long RaceMask, int Order, string Background, long IconId, string Icon, IReadOnlyList<TalentNode> Talents);

/// <summary>
/// 0.12.0: the talent designer (the app's Classes and talents tab, <c>wds talents ...</c>): a class's talent trees
/// (TalentTab rows: name, icon, background, order, class and race masks) and their talents (Talent rows: tier and column
/// on the grid, up to 5 rank spells, up to 3 prerequisites with the rank they need), checked against what the 1.12
/// client's talent frame and the server can show and use:
/// <list type="bullet">
/// <item>the client's Blizzard_TalentUI.lua: MAX_TALENT_TABS = 5 tabs per class, MAX_NUM_TALENTS = 20 talents per tab,
///   MAX_NUM_TALENT_TIERS = 8 rows, NUM_TALENT_COLUMNS = 4 columns, at most 30 branch and 30 arrow pieces per tab;</item>
/// <item>the server (VMaNGOS DBCStructure.h MAX_TALENT_RANK = 5): only the first 5 rank spells are read; a tier needs 5
///   points spent in the tree per row above it; trainers may not teach a talent's rank spell;</item>
/// <item>the client and the server both read Talent.dbc and TalentTab.dbc (the server its own copy: Copy tables to the
///   server after testing); the rank spells come from Spell.dbc and spell_template (the spell designer makes them).</item>
/// </list>
/// </summary>
public sealed class TalentDesigner
{
    public const int MaxTabs = 5, MaxTalents = 20, MaxTiers = 8, MaxColumns = 4, MaxRanks = 5, MaxArrows = 30;

    public TalentDesigner(DesignSession session, SpellDesigner spells)
    {
        Session = session;
        Spells = spells;
    }

    public DesignSession Session { get; }
    public SpellDesigner Spells { get; }
    private DbcTable Tabs => Session.Table("TalentTab");
    private DbcTable Talents => Session.Table("Talent");

    public static long ClassBit(long classId) => classId is >= 1 and <= 32 ? 1L << (int)(classId - 1) : throw new DesignException($"Class {classId} has no mask bit (classes 1-32).");

    // ------------------------------------------------------------------ reading

    public TalentTree Tree(long tab)
    {
        var t = Tabs;
        var r = t.Find(tab) ?? throw new DesignException($"There is no talent tree (TalentTab) {tab}.");
        long N(string c) => t.Number(r, t.Layout.Column(c)!);
        long icon = N("SpellIconID");
        var it = Session.Lookup("SpellIcon");
        var iconName = it?.Find(icon) is { } ir ? SpellForm.IconName(it.Text(ir, it.Layout.Column("TextureFilename")!, 0)) : "";
        return new TalentTree(tab, t.Text(r, t.Layout.Column("Name")!, 0), N("ClassMask"), N("RaceMask"), (int)N("OrderIndex"),
            t.Text(r, t.Layout.Column("BackgroundFile")!, 0), icon, iconName, TalentsOf(tab));
    }

    public List<TalentNode> TalentsOf(long tab)
    {
        var t = Talents;
        var tabCol = t.Layout.Column("TabID")!;
        return t.Rows.Where(r => t.Number(r, tabCol) == tab).Select(Node).OrderBy(n => n.Tier).ThenBy(n => n.Column).ThenBy(n => n.Id).ToList();
    }

    public TalentNode Talent(long id) => Node(Talents.Find(id) ?? throw new DesignException($"There is no talent {id}."));

    private TalentNode Node(DbcRow r)
    {
        var t = Talents;
        long N(string c) => t.Number(r, t.Layout.Column(c)!);
        var ranks = new List<long>();
        for (int i = 1; i <= 9; i++)
        {
            long s = N($"SpellRank{i}");
            if (s == 0) break;
            ranks.Add(s);
        }
        var pre = new List<(long, int)>();
        for (int i = 1; i <= 3; i++)
            if (N($"PrereqTalent{i}") is var p and > 0) pre.Add((p, (int)N($"PrereqRank{i}")));
        string name = "", icon = "";
        if (ranks.Count > 0 && Spells.Spells.Find(ranks[0]) is { } sr)
        {
            name = Spells.Spells.Text(sr, Spells.Spells.Layout.Column("Name")!, 0);
            var it = Session.Lookup("SpellIcon");
            if (it?.Find(Spells.Spells.Number(sr, Spells.Spells.Layout.Column("SpellIconID")!)) is { } ir) icon = SpellForm.IconName(it.Text(ir, it.Layout.Column("TextureFilename")!, 0));
        }
        return new TalentNode(t.Key(r)!.Value, N("TabID"), (int)N("TierID"), (int)N("ColumnIndex"), ranks, pre, name.Length > 0 ? name : $"(talent {t.Key(r)})", icon, N("RequiredSpellID"));
    }

    /// <summary>The trees of a class (TalentTab rows whose class mask has the class), in their order.</summary>
    public List<TalentTree> Trees(long classId)
    {
        var bit = ClassBit(classId);
        var t = Tabs;
        var mc = t.Layout.Column("ClassMask")!;
        return t.Rows.Where(r => (t.Number(r, mc) & bit) != 0).Select(r => Tree(t.Key(r)!.Value)).OrderBy(x => x.Order).ThenBy(x => x.Id).ToList();
    }

    // ------------------------------------------------------------------ trees

    /// <summary>A new tree for a class: the next free order (0-4), an icon and a background (Interface\TalentFrame\&lt;name&gt;).</summary>
    public long AddTree(long classId, string name, string? icon = null, string? background = null, int? order = null, long raceMask = 0)
    {
        var existing = Trees(classId);
        if (existing.Count >= MaxTabs) throw new DesignException($"The class already has {existing.Count} trees: the 1.12 talent frame shows at most {MaxTabs} (MAX_TALENT_TABS).");
        int ord = order ?? Enumerable.Range(0, MaxTabs).First(o => existing.All(x => x.Order != o));
        var row = Session.AddRow("TalentTab");
        Session.Set("TalentTab", row, "Name", name);
        Session.SetNumber("TalentTab", row, "ClassMask", ClassBit(classId));
        Session.SetNumber("TalentTab", row, "RaceMask", raceMask);
        Session.SetNumber("TalentTab", row, "OrderIndex", ord);
        Session.Set("TalentTab", row, "BackgroundFile", background ?? existing.FirstOrDefault()?.Background ?? "WarriorArms");
        if (icon != null) Session.SetNumber("TalentTab", row, "SpellIconID", IconId(icon));
        return Tabs.Key(row)!.Value;
    }

    private long IconId(string icon) => long.TryParse(icon, out var n) ? n : SpellForm.IconRow(Session, icon, out _);

    /// <summary>Change a tree: name, icon, background, order, class-mask, race-mask.</summary>
    public DesignResult SetTree(long tab, string field, string value)
    {
        var row = Tabs.Find(tab) ?? throw new DesignException($"There is no talent tree {tab}.");
        try
        {
            switch (field.ToLowerInvariant())
            {
                case "name": Session.Set("TalentTab", row, "Name", value); break;
                case "icon": Session.SetNumber("TalentTab", row, "SpellIconID", IconId(value)); break;
                case "background": Session.Set("TalentTab", row, "BackgroundFile", value.Trim()); break;
                case "order":
                    if (!int.TryParse(value, out var o) || o < 0 || o >= MaxTabs) throw new DesignException($"The order is 0-{MaxTabs - 1} (the tab's place).");
                    Session.SetNumber("TalentTab", row, "OrderIndex", o);
                    break;
                case "class-mask": Session.Set("TalentTab", row, "ClassMask", value); break;
                case "race-mask": Session.Set("TalentTab", row, "RaceMask", value); break;
                default: throw new DesignException($"A tree has no field \"{field}\" (name, icon, background, order, class-mask, race-mask).");
            }
            return DesignResult.Done($"Tree {tab}: {field} = {value}.");
        }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
    }

    /// <summary>Remove a tree and its talents (their rank spells stay).</summary>
    public DesignResult RemoveTree(long tab)
    {
        var row = Tabs.Find(tab) ?? throw new DesignException($"There is no talent tree {tab}.");
        var talents = TalentsOf(tab).Select(n => Talents.Find(n.Id)!).ToList();
        if (talents.Count > 0) Session.Session("Talent").DeleteRows(talents);
        Session.Session("TalentTab").DeleteRows(new[] { row });
        return DesignResult.Done($"Removed tree {tab} and its {talents.Count} talent(s) (their rank spells stay in Spell.dbc).");
    }

    // ------------------------------------------------------------------ talents

    public long AddTalent(long tab, int tier, int column, IReadOnlyList<long> ranks)
    {
        if (Tabs.Find(tab) == null) throw new DesignException($"There is no talent tree {tab}.");
        if (TalentsOf(tab).Any(n => n.Tier == tier && n.Column == column)) throw new DesignException($"Tier {tier}, column {column} of tree {tab} already has a talent.");
        var row = Session.AddRow("Talent");
        Session.SetNumber("Talent", row, "TabID", tab);
        long id = Talents.Key(row)!.Value;
        Place(row, tier, column);
        SetRanksCore(row, ranks);
        return id;
    }

    private void Place(DbcRow row, int tier, int column)
    {
        if (tier < 0 || tier >= MaxTiers) throw new DesignException($"The tier is 0-{MaxTiers - 1} (the 1.12 talent frame has {MaxTiers} rows).");
        if (column < 0 || column >= MaxColumns) throw new DesignException($"The column is 0-{MaxColumns - 1} (the 1.12 talent frame has {MaxColumns} columns).");
        Session.SetNumber("Talent", row, "TierID", tier);
        Session.SetNumber("Talent", row, "ColumnIndex", column);
    }

    private void SetRanksCore(DbcRow row, IReadOnlyList<long> ranks)
    {
        if (ranks.Count > MaxRanks) throw new DesignException($"A talent has at most {MaxRanks} ranks (the server reads only 5: MAX_TALENT_RANK).");
        foreach (var s in ranks)
            if (Spells.Spells.Find(s) == null) throw new DesignException($"Rank spell {s} does not exist in Spell.dbc (make it with the spell designer first).");
        for (int i = 1; i <= 9; i++) Session.SetNumber("Talent", row, $"SpellRank{i}", i <= ranks.Count ? ranks[i - 1] : 0);
    }

    public DesignResult Move(long talent, int tier, int column)
    {
        var row = Talents.Find(talent) ?? throw new DesignException($"There is no talent {talent}.");
        var n = Node(row);
        if (TalentsOf(n.Tab).Any(x => x.Id != talent && x.Tier == tier && x.Column == column)) return DesignResult.Refused($"Tier {tier}, column {column} already has a talent.");
        try { Place(row, tier, column); }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
        return DesignResult.Done($"{n.Name} moved to tier {tier}, column {column}.");
    }

    public DesignResult SetRanks(long talent, IReadOnlyList<long> ranks)
    {
        var row = Talents.Find(talent) ?? throw new DesignException($"There is no talent {talent}.");
        try { SetRanksCore(row, ranks); }
        catch (DesignException e) { return DesignResult.Refused(e.Message); }
        return DesignResult.Done($"Talent {talent} has {ranks.Count} rank(s): {string.Join(", ", ranks)}.");
    }

    /// <summary>Set prerequisite <paramref name="slot"/> (1-3): the talent that must be learned first and the rank it
    /// needs (1-based as players count; stored 0-based). <paramref name="prereq"/> 0 removes it.</summary>
    public DesignResult SetPrereq(long talent, long prereq, int rank = 0, int slot = 1)
    {
        var row = Talents.Find(talent) ?? throw new DesignException($"There is no talent {talent}.");
        if (slot is < 1 or > 3) return DesignResult.Refused("A talent has prerequisite slots 1-3.");
        if (prereq != 0)
        {
            var p = Talents.Find(prereq);
            if (p == null) return DesignResult.Refused($"There is no talent {prereq}.");
            var pn = Node(p);
            if (rank <= 0) rank = Math.Max(1, pn.MaxRank);
            if (rank > pn.MaxRank && pn.MaxRank > 0) return DesignResult.Refused($"{pn.Name} has only {pn.MaxRank} rank(s).");
        }
        Session.SetNumber("Talent", row, $"PrereqTalent{slot}", prereq);
        Session.SetNumber("Talent", row, $"PrereqRank{slot}", prereq == 0 ? 0 : rank - 1);
        return DesignResult.Done(prereq == 0 ? $"Prerequisite {slot} of talent {talent} removed." : $"Talent {talent} needs {Node(Talents.Find(prereq)!).Name} rank {rank} first.");
    }

    public DesignResult SetRequiredSpell(long talent, long spell)
    {
        var row = Talents.Find(talent) ?? throw new DesignException($"There is no talent {talent}.");
        if (spell != 0 && Spells.Spells.Find(spell) == null) return DesignResult.Refused($"There is no spell {spell}.");
        Session.SetNumber("Talent", row, "RequiredSpellID", spell);
        return DesignResult.Done($"Talent {talent} needs spell {spell}.");
    }

    public DesignResult RemoveTalent(long talent)
    {
        var row = Talents.Find(talent) ?? throw new DesignException($"There is no talent {talent}.");
        var dependents = Talents.Rows.Where(r => Enumerable.Range(1, 3).Any(i => Talents.Number(r, Talents.Layout.Column($"PrereqTalent{i}")!) == talent)).Select(r => Talents.Key(r)!.Value).ToList();
        Session.Session("Talent").DeleteRows(new[] { row });
        return DesignResult.Done($"Talent {talent} removed." + (dependents.Count > 0 ? $" Talents {string.Join(", ", dependents)} named it as a prerequisite: fix them (the check lists them)." : ""));
    }

    /// <summary>
    /// Make the rank spells of a talent with the spell designer: <paramref name="count"/> clones of a template spell
    /// (a passive talent spell of the class, say) named <paramref name="name"/>, "Rank 1".."Rank n", with the value of
    /// effect <paramref name="effect"/> set per rank from <paramref name="values"/> (one per rank, "2" or "2-4").
    /// </summary>
    public List<long> MakeRankSpells(long template, string name, int count, IReadOnlyList<string>? values = null, int effect = 1, string? description = null)
    {
        if (count is < 1 or > MaxRanks) throw new DesignException($"A talent has 1-{MaxRanks} ranks.");
        if (values != null && values.Count != count) throw new DesignException($"Give one value per rank ({count}).");
        var res = new List<long>();
        for (int i = 1; i <= count; i++)
        {
            long id = Spells.Clone(template, name, rank: $"Rank {i}");
            if (values != null)
            {
                var r = Spells.Set(id, $"effect{effect}-value", values[i - 1]);
                if (!r.Ok) throw new DesignException(r.Message);
            }
            if (description != null)
            {
                var r = Spells.Set(id, "description", description);
                if (!r.Ok) throw new DesignException(r.Message);
            }
            res.Add(id);
        }
        return res;
    }

    // ------------------------------------------------------------------ checks

    /// <summary>Everything about a tree the 1.12 talent frame or server cannot show or use, in plain words.</summary>
    public List<DesignProblem> Check(long tab)
    {
        var res = new List<DesignProblem>();
        var tree = Tree(tab);
        void Add(DesignSeverity s, string f, string m) => res.Add(new DesignProblem(s, f, m));
        if (tree.Name.Trim().Length == 0) Add(DesignSeverity.Error, "Tree", "The tree has no name (the tab shows nothing).");
        if (tree.ClassMask == 0) Add(DesignSeverity.Error, "Tree", "The tree belongs to no class (class mask 0).");
        if (tree.Order is < 0 or >= MaxTabs) Add(DesignSeverity.Error, "Tree", $"The order {tree.Order} is outside 0-{MaxTabs - 1}.");
        for (long c = 1; c <= 32; c++)
        {
            if ((tree.ClassMask & ClassBit(c)) == 0) continue;
            var trees = Trees(c);
            if (trees.Count > MaxTabs) Add(DesignSeverity.Error, "Tree", $"Class {c} has {trees.Count} trees: the 1.12 talent frame shows at most {MaxTabs}.");
            foreach (var g in trees.GroupBy(x => x.Order).Where(g => g.Count() > 1))
                Add(DesignSeverity.Note, "Tree", $"Class {c}: trees {string.Join(", ", g.Select(x => x.Id))} have the same order {g.Key}; the frame then shows them in the file's order (Blizzard's Mage trees do this too).");
        }
        if (tree.IconId != 0 && Session.Lookup("SpellIcon")?.Find(tree.IconId) == null) Add(DesignSeverity.Warning, "Tree", $"The icon {tree.IconId} does not exist.");
        if (tree.Background.Length > 0)
        {
            var bg = tree.Background.Contains('\\') ? tree.Background : "Interface\\TalentFrame\\" + tree.Background;
            foreach (var part in new[] { "-TopLeft", "-TopRight", "-BottomLeft", "-BottomRight" })
                if (Session.ReadFile(bg + part + ".blp") == null) { Add(DesignSeverity.Warning, "Tree", $"The background {bg}{part}.blp does not exist: the tree shows an empty background."); break; }
        }
        else Add(DesignSeverity.Warning, "Tree", "No background picture (Interface\\TalentFrame\\<name>).");
        if (tree.Talents.Count > MaxTalents) Add(DesignSeverity.Error, "Talents", $"{tree.Talents.Count} talents: the 1.12 talent frame shows at most {MaxTalents} per tree (MAX_NUM_TALENTS).");

        var byId = tree.Talents.ToDictionary(n => n.Id);
        var cells = new Dictionary<(int, int), TalentNode>();
        var allRanks = Talents.Rows.SelectMany(r => Enumerable.Range(1, 9).Select(i => (Talent: Talents.Key(r)!.Value, Spell: Talents.Number(r, Talents.Layout.Column($"SpellRank{i}")!)))).Where(x => x.Spell != 0).ToList();
        int arrows = 0, branches = 0;
        foreach (var n in tree.Talents)
        {
            string F = $"{n.Name} [{n.Id}]";
            if (n.Tier is < 0 or >= MaxTiers || n.Column is < 0 or >= MaxColumns) Add(DesignSeverity.Error, F, $"Tier {n.Tier}, column {n.Column} is outside the grid (tiers 0-{MaxTiers - 1}, columns 0-{MaxColumns - 1}).");
            if (cells.TryGetValue((n.Tier, n.Column), out var other)) Add(DesignSeverity.Error, F, $"Shares tier {n.Tier}, column {n.Column} with {other.Name} [{other.Id}]: the frame shows only one.");
            else cells[(n.Tier, n.Column)] = n;
            if (n.Ranks.Count == 0) Add(DesignSeverity.Error, F, "No rank spell: the talent cannot be learned.");
            if (n.Ranks.Count > MaxRanks) Add(DesignSeverity.Error, F, $"{n.Ranks.Count} ranks: the server reads only {MaxRanks}.");
            var t = Talents;
            var row = t.Find(n.Id)!;
            for (int i = 1; i <= 9; i++)
                if (t.Number(row, t.Layout.Column($"SpellRank{i}")!) == 0 && Enumerable.Range(i + 1, 9 - i).Any(j => t.Number(row, t.Layout.Column($"SpellRank{j}")!) != 0))
                { Add(DesignSeverity.Error, F, $"Rank {i} is empty but a later rank is set: ranks must follow each other."); break; }
            foreach (var s in n.Ranks)
            {
                if (Spells.Spells.Find(s) == null) Add(DesignSeverity.Error, F, $"Rank spell {s} does not exist in Spell.dbc.");
                else if (Session.Server?.Schema.Table("spell_template") is { } st && Session.Server.Object(st, s) == null && !Spells.Touched.Contains(s))
                {
                    // a spell of the game's own file is on every complete server (a missing one is a gap of this database);
                    // a new spell must be synced
                    bool stock = Session.Session("Spell").GameTable?.Find(s) != null;
                    Add(stock ? DesignSeverity.Warning : DesignSeverity.Error, F, $"The server has no spell {s} (spell_template): it refuses the talent. " +
                        (stock ? "It is a spell of the game's own file: this server database lacks it." : $"Save the spell with the spell designer (or wds spells sync {s})."));
                }
                foreach (var dup in allRanks.Where(x => x.Spell == s && x.Talent != n.Id).Select(x => x.Talent).Distinct())
                    Add(DesignSeverity.Error, F, $"Rank spell {s} is also a rank of talent {dup}.");
            }
            if (n.Ranks.Count > 1 && n.Ranks.Distinct().Count() != n.Ranks.Count) Add(DesignSeverity.Error, F, "The same spell is used for two ranks.");
            foreach (var (pre, rank) in n.Prereqs)
            {
                if (!byId.TryGetValue(pre, out var p))
                {
                    Add(DesignSeverity.Error, F, Talents.Find(pre) == null ? $"Prerequisite talent {pre} does not exist." : $"Prerequisite talent {pre} is in another tree: the client can only draw arrows inside one tree.");
                    continue;
                }
                if (rank >= p.MaxRank && p.MaxRank > 0) Add(DesignSeverity.Error, F, $"Needs rank {rank + 1} of {p.Name}, which has only {p.MaxRank}.");
                if (p.Tier > n.Tier) Add(DesignSeverity.Error, F, $"Its prerequisite {p.Name} is in a lower row (tier {p.Tier} > {n.Tier}): it can never be learned first in time.");
                else if (p.Tier == n.Tier)
                {
                    if (Math.Abs(p.Column - n.Column) != 1) Add(DesignSeverity.Warning, F, $"Its prerequisite {p.Name} is in the same row but not next to it: the arrow crosses other cells.");
                    arrows++;
                }
                else
                {
                    arrows++;
                    if (p.Column == n.Column)
                    {
                        branches += n.Tier - p.Tier - 1;
                        foreach (var between in tree.Talents.Where(x => x.Column == n.Column && x.Tier > p.Tier && x.Tier < n.Tier))
                            Add(DesignSeverity.Warning, F, $"The arrow from {p.Name} passes under {between.Name} (tier {between.Tier}).");
                    }
                    else
                    {
                        branches += Math.Abs(p.Column - n.Column) + (n.Tier - p.Tier);
                        Add(DesignSeverity.Note, F, $"Its prerequisite {p.Name} is in another column: the client draws a bent arrow (vanilla trees use straight ones).");
                    }
                }
                if (Reaches(byId, pre, n.Id)) Add(DesignSeverity.Error, F, "The prerequisites go in a circle: neither talent can be learned.");
            }
            if (n.Tier > 0)
            {
                int above = tree.Talents.Where(x => x.Tier < n.Tier).Sum(x => x.MaxRank);
                if (above < n.Tier * 5) Add(DesignSeverity.Warning, F, $"Tier {n.Tier} needs {n.Tier * 5} points spent in the tree, but the rows above offer only {above}: it cannot be reached.");
            }
            if (Session.Server is { } ed && n.Ranks.Count > 0)
                foreach (var tr in new[] { "npc_trainer", "npc_trainer_template" })
                    if (ed.Schema.Table(tr) is { } tt && ed.Rows(tt, ServerData.RowFilter.Where(new ServerData.FilterTerm("spell", ServerData.FilterOp.In, null, n.Ranks.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList())), 1).Count > 0)
                        Add(DesignSeverity.Warning, F, $"A rank spell is in {tr}: the server refuses talent spells in trainer lists.");
        }
        if (arrows > MaxArrows || branches > MaxArrows) Add(DesignSeverity.Warning, "Arrows", $"The tree needs {arrows} arrows and {branches} branch pieces: the 1.12 frame draws at most {MaxArrows} of each.");
        return res;
    }

    private bool Reaches(Dictionary<long, TalentNode> byId, long from, long target, int depth = 0)
    {
        if (depth > 25 || !byId.TryGetValue(from, out var n)) return false;
        foreach (var (p, _) in n.Prereqs)
            if (p == target || Reaches(byId, p, target, depth + 1)) return true;
        return false;
    }

    // ------------------------------------------------------------------ the grid as text

    /// <summary>The tree as a text grid (rows = tiers, 4 columns), with arrows written next to the talents.</summary>
    public string Grid(long tab)
    {
        var tree = Tree(tab);
        var sb = new StringBuilder();
        sb.AppendLine($"{tree.Name} (tree {tree.Id}, order {tree.Order}, classes {SpellDesigner.ClassNames(tree.ClassMask)})");
        int maxTier = tree.Talents.Count == 0 ? 0 : tree.Talents.Max(t => t.Tier);
        for (int tier = 0; tier <= Math.Max(maxTier, 0); tier++)
        {
            var cells = new string[MaxColumns];
            for (int c = 0; c < MaxColumns; c++)
            {
                var n = tree.Talents.FirstOrDefault(x => x.Tier == tier && x.Column == c);
                cells[c] = n == null ? "." : $"{Short(n.Name)} 0/{n.MaxRank}" + (n.Prereqs.Count > 0 ? " ^" + string.Join(",", n.Prereqs.Select(p => p.Talent)) : "");
            }
            sb.AppendLine($"{tier * 5,2} pts | " + string.Join(" | ", cells.Select(x => x.PadRight(24))));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Short(string s) => s.Length > 16 ? s[..15] + "." : s;

    // ------------------------------------------------------------------ saving

    public DesignSaveResult Save(string? label = null, bool force = false)
    {
        if (!force)
        {
            var tabs = Session.IsOpen("TalentTab") ? Tabs.Rows.Select(r => Tabs.Key(r)!.Value) : Enumerable.Empty<long>();
            var errors = new List<string>();
            var touchedTabs = Session.IsOpen("Talent") ? Talents.Rows.Select(r => Talents.Number(r, Talents.Layout.Column("TabID")!)).ToHashSet() : new HashSet<long>();
            foreach (var tab in tabs.Where(t => DirtyTab(t, touchedTabs)))
                errors.AddRange(Check(tab).Where(p => p.IsError).Select(p => $"tree {tab}: {p.Text}"));
            errors.AddRange(Spells.Touched.Where(Spells.Exists).SelectMany(id => Spells.Check(id).Where(p => p.IsError).Select(p => $"{Spells.Title(id)}: {p.Text}")));
            if (errors.Count > 0) throw new DesignException($"Not saved: {errors.Count} problem(s):\n" + string.Join("\n", errors.Take(10)));
        }
        return Spells.Save(label ?? "Talent designer", force: true);
    }

    /// <summary>Is the tree new or changed in this session (compared with the game's tables)?</summary>
    private bool DirtyTab(long tab, HashSet<long> withTalents)
    {
        if (!Session.Session("TalentTab").Editor.IsDirty && !(Session.IsOpen("Talent") && Session.Session("Talent").Editor.IsDirty)) return false;
        var game = Session.Session("TalentTab").GameTable;
        if (game?.Find(tab) == null) return true;
        var changed = Session.Session("TalentTab").ChangedRows();
        if (changed.Any(r => Tabs.Key(r) == tab)) return true;
        if (Session.IsOpen("Talent"))
            foreach (var r in Session.Session("Talent").ChangedRows())
                if (Talents.Number(r, Talents.Layout.Column("TabID")!) == tab) return true;
        return false;
    }
}
