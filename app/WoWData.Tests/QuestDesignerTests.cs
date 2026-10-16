using System.IO;
using System.Linq;
using WoWData.Core.Dbc;
using WoWData.Core.Designers;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.13.0: the quest-chain designer in Core. The server side runs ONLY on the isolated throwaway MySQL server of
/// ServerTestDb, on a copy of the fixture plus the quest-chain supplement (real chains: the Warrior's Whirlwind weapon,
/// the Warlock's Dreadsteed; the Riposte Drill's outposts), never the live database: the chain graphs of real quests,
/// creating / branching / joining / duplicating for the other faction, the discipline template, the validation rules
/// (each broken on purpose), drop sources, "speak to" and "explore" objectives, CSV / JSON round trips, save, apply and
/// undo. The text token preview is checked against the client's own rules without a database.
/// </summary>
public class QuestDesignerTests
{
    internal sealed class Ctx : IDisposable
    {
        private readonly List<(string, long, DateTime)> _client = TestEnv.DataSnapshot();

        public Ctx(string name)
        {
            Db = ServerTestDb.NewDatabase(name, questChains: true);
            Dir = TestEnv.TempDir("quests_" + name);
            Paths = new ClientPaths(Path.Combine(Dir, "client"), TestEnv.ClientDir);
            Directory.CreateDirectory(Paths.Dir);
            Ws = new Workspace(Paths);
            Settings = new AppSettings { Server = Db };
        }

        public string Dir { get; }
        public ServerDbSettings Db { get; }
        public ClientPaths Paths { get; }
        public Workspace Ws { get; }
        public AppSettings Settings { get; }

        public ServerDataService Service() => new(Db, Ws, new DbcService(TestEnv.Client, Ws), Settings, null, Paths.Dir, () => new List<string>());

        /// <summary>A fresh designer (as after a restart of the app): the saved workspace and the throwaway server.</summary>
        public QuestDesigner Open()
        {
            var svc = Service();
            var s = new DesignSession(svc.Dbc!, svc.NewEditor(), (900000, 999999));
            return new QuestDesigner(s);
        }

        public void Dispose()
        {
            Assert.Equal(_client, TestEnv.DataSnapshot());
            try { ServerTestDb.Exec(Db, $"DROP DATABASE `{Db.Database}`"); }
            catch (MySqlConnector.MySqlException) { }
            try { Directory.Delete(Dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static List<QuestProblem> Errors(QuestDesigner d, params long[] ids) => QuestRules.Check(d, ids).Where(p => p.Problem.IsError).ToList();

    private static bool Has(IEnumerable<QuestProblem> p, long quest, DesignSeverity sev, string text) =>
        p.Any(x => x.Quest == quest && x.Problem.Severity == sev && x.Problem.Message.Contains(text, StringComparison.Ordinal));

    private static void AssertHas(IEnumerable<QuestProblem> p, long quest, DesignSeverity sev, string text) =>
        Assert.True(Has(p, quest, sev, text), $"no {sev} \"{text}\" for quest {quest} in:" + Environment.NewLine + string.Join(Environment.NewLine, p.Select(x => x.Text)));

    // ------------------------------------------------------------------ chains of real quests

    [IsolatedDbFact]
    public void ChainGraphsOfRealQuests()
    {
        using var c = new Ctx("graph");
        var d = c.Open();
        // the Warrior's Whirlwind weapon (The Islander, Klannoc Macleod, Bath'rah the Windwatcher)
        var ww = QuestChains.Open(d, 1718);
        Assert.Equal(new long[] { 1712, 1713, 1714, 1718, 1719, 1791, 1792 }, ww.Nodes.Select(n => n.Id).OrderBy(x => x));
        Assert.Single(ww.Lanes);
        Assert.Equal("Both factions · Warrior", ww.Lanes[0].Name);
        int Col(QuestChain ch, long id) => ch.Node(id)!.Column;
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new long[] { 1718, 1719, 1791, 1712, 1713, 1792 }.Select(id => Col(ww, id)));
        Assert.Equal(QuestLinkKind.Requires | QuestLinkKind.NextInChain, ww.Edges.Single(e => e.From == 1718 && e.To == 1719).Kinds);
        Assert.Equal(QuestLinkKind.RequiresActive, ww.Edges.Single(e => e.From == 1712 && e.To == 1714).Kinds);   // Essence of the Exile needs Cyclonian in the log
        Assert.Contains("Klannoc Macleod", ww.Node(1719)!.Givers);
        // the Horde's chain: breadcrumbs to the three armour quests
        var hz = QuestChains.Open(d, 1823);
        Assert.Equal(14, hz.Nodes.Count);
        Assert.Equal(QuestLinkKind.NextInChain | QuestLinkKind.Breadcrumb, hz.Edges.Single(e => e.From == 1839 && e.To == 1842).Kinds);
        Assert.Equal(4, hz.Edges.Count(e => e.From == 1838));
        // the Dreadsteed: "all of" groups (NextQuestId + a negative exclusive group)
        var ds = QuestChains.Open(d, 7562);
        Assert.Equal(new long[] { 7626, 7627, 7628 }, ds.Groups.Single(g => g.Id == -7626).Quests);
        Assert.False(ds.Groups.Single(g => g.Id == -7626).IsChoice);
        Assert.Equal(QuestLinkKind.Opens, ds.Edges.Single(e => e.From == 7626 && e.To == 7630).Kinds);
        Assert.Contains(ds.Lanes, l => l.Name == "Both factions · Warlock");
        Assert.True(Col(ds, 7631) > Col(ds, 7629) && Col(ds, 7629) > Col(ds, 7625));
        // Valley of Trials: a lane per race and class
        var vt = QuestChains.Open(d, 788);
        Assert.Contains(vt.Lanes, l => l.Name == "Orc · Warrior");
        Assert.Contains(vt.Lanes, l => l.Name == "Troll · Warrior");
        Assert.Equal(Col(vt, 788) + 1, Col(vt, 2383));
        Assert.Contains("flowchart LR", ww.Mermaid());
        Assert.Contains("The Islander (1718)", ww.Describe());
        // the checks of a real chain: Big Will is summoned by a script (a warning, not an error)
        var ww2 = QuestRules.Check(d, ww.Nodes.Select(n => n.Id).ToList());
        AssertHas(ww2, 1719, DesignSeverity.Warning, "Big Will is not spawned anywhere");
        Assert.DoesNotContain(ww2, p => p.Quest == 1718 && p.Problem.IsError);
    }

    // ------------------------------------------------------------------ building: template, NPCs, branch, the other faction

    [IsolatedDbFact]
    public void TheRiposteDrillForBothFactions()
    {
        using var c = new Ctx("drill");
        var d = c.Open();
        // the drill's NPCs: a sergeant at Sentinel Hill (looks like Captain Danuvin), a champion at the Crossroads (like Thork)
        double? z = d.GroundHeight(0, -10508, 1053);
        Assert.NotNull(z);
        Assert.InRange(z!.Value, 40, 80);
        var (sergeant, sguid, msg) = d.NewNpc("Drill Sergeant Halvor", 821, "Drill Sergeant", map: 0, x: -10508, y: 1053);
        Assert.Equal(900000, sergeant);
        Assert.NotNull(sguid);
        Assert.Contains("Placed on map 0", msg);
        var (champion, _, _) = d.NewNpc("Champion Gorrak", 3429, "Crossroads Champion", map: 1, x: -470, y: -2620, z: 92);
        Assert.Equal(sergeant + 1, champion);
        Assert.Equal(2, d.Ed.CreatureFlags(sergeant)!.Value & 0x2);
        // the NPC picker: by name, quest givers first, with their spawns
        var thork = d.FindNpcs("thork");
        Assert.Equal(("creature", 3429L, "quest giver"), (thork[0].Kind, thork[0].Id, thork[0].Note));
        Assert.True(thork[0].Spawns >= 1);
        Assert.Contains(d.FindNpcs("Drill Sergeant"), x => x.Id == sergeant);
        // the template: talk to Gryan Stoutmantle -> the drill (kill 8 Defias Highwaymen) -> learn Riposte Drill
        var res = d.Discipline(new DisciplineOptions
        {
            Discipline = "Riposte Drill", Faction = "Alliance", Level = 20, Giver = sergeant.ToString(), IntroGiver = "234", Outpost = "Sentinel Hill",
            KillTarget = "122", KillCount = 8, SpellTemplate = 12295, SpellDescription = "A dodged attack also makes your next Overpower cost no rage.", Xp = 1450,
        });
        Assert.Equal(2, res.Quests.Count);
        long intro = res.Quests[0], drill = res.Quests[1];
        Assert.True(res.Spell >= 900000);
        var s = d.Summary(drill);
        Assert.Equal((intro, 0L, QuestForm.AllianceRaces, 1L, 20L), (s.Prev, s.NextInChain, s.Races, s.Classes, s.Level));
        Assert.Equal(drill, d.Summary(intro).NextInChain);
        Assert.Equal("234", d.Givers(intro).Single().Id.ToString());
        Assert.Equal(sergeant, d.Enders(intro).Single().Id);
        Assert.Equal(sergeant, d.Givers(drill).Single().Id);
        Assert.Equal(res.Spell, d.Raw(drill, QuestForm.Require("spell")));
        long learn = d.Raw(drill, QuestForm.Require("spell-cast"));
        Assert.Equal(new[] { learn }, d.Spells.LearnSpellsOf(res.Spell));
        Assert.Contains(d.Objectives(drill), o => o.Kind == "kill" && o.Id == 122 && o.Count == 8);
        Assert.Contains("-81", d.Show(drill, "category"));   // sort:Warrior
        Assert.Empty(Errors(d, intro, drill));
        // a branch: a choice of three follow-ups, each with its reward
        var br = d.Branch(drill, new[] { "The Sword Path", "The Axe Path", "The Mace Path" }, exclusive: true, rewards: new long[] { 25, 117, 159 });
        Assert.Equal(3, br.Count);
        foreach (var b in br) Assert.Equal((drill, br[0]), (d.Summary(b).Prev, d.Summary(b).Group));
        var ch = QuestChains.Open(d, intro);
        Assert.True(ch.Groups.Single(g => g.Id == br[0]).IsChoice);
        Assert.Single(br.Select(b => ch.Node(b)!.Column).Distinct());
        // a join after the choice: any one of the three
        long back = d.Join(br, "Report Back", all: false);
        Assert.All(br, b => Assert.Equal(back, d.Summary(b).Next));
        Assert.Throws<DesignException>(() => d.Join(br, "Impossible", all: true));   // a choice cannot be "all of"
        // add before / after keep the chain linked
        long warmup = d.AddBefore(drill, "Warm-up");
        Assert.Equal((intro, drill), (d.Summary(warmup).Prev, d.Summary(warmup).NextInChain));
        Assert.Equal((warmup, warmup), (d.Summary(drill).Prev, d.Summary(intro).NextInChain));
        Assert.Empty(Errors(d, QuestChains.Members(d, intro).ToArray()));

        // the same chain for the Horde: the champion at the Crossroads, Thork, Bristleback Hunters, the Barrens
        var sw = new QuestSwaps();
        foreach (var x in new[] { $"{sergeant}={champion}", "234=3429", "122=3258", "zone:40=17", "text:Sentinel Hill=the Crossroads", "text:Gryan Stoutmantle=Thork" }) sw.Add(x);
        var members = QuestChains.Members(d, intro);
        var dup = d.DuplicateForFaction(members, "Horde", sw);
        Assert.Equal(members.Count, dup.Map!.Count);
        long hIntro = dup.Map[intro], hDrill = dup.Map[drill], hWarm = dup.Map[warmup];
        Assert.Equal(QuestForm.HordeRaces, d.Summary(hDrill).Races);
        Assert.Equal(hWarm, d.Summary(hDrill).Prev);
        Assert.Equal(hWarm, d.Summary(hIntro).NextInChain);
        Assert.Equal(3429, d.Givers(hIntro).Single().Id);
        Assert.Equal(champion, d.Givers(hDrill).Single().Id);
        Assert.Contains(d.Objectives(hDrill), o => o.Id == 3258);
        Assert.Contains("the Crossroads", d.Show(hDrill, "objectives"));
        long hGroup = d.Summary(dup.Map[br[0]]).Group;
        Assert.Equal(dup.Map[br[0]], hGroup);
        Assert.All(br, b => Assert.Equal(hGroup, d.Summary(dup.Map[b]).Group));
        Assert.All(br, b => Assert.Equal(dup.Map[back], d.Summary(dup.Map[b]).Next));
        Assert.Empty(Errors(d, QuestChains.Members(d, hIntro).ToArray()));
        // the two chains are separate (no link between them), each in its faction's lane
        Assert.DoesNotContain(hIntro, QuestChains.Members(d, intro));
        Assert.Equal("Horde · Warrior", QuestChains.Open(d, hIntro).Lanes.Single().Name);

        // save: one change set with the quests, relations, NPCs, spawns, the spells (game and server)
        var saved = d.Save();
        Assert.Contains("quest_template", saved.ServerTables);
        Assert.Contains("creature_questrelation", saved.ServerTables);
        Assert.Contains("creature_template", saved.ServerTables);
        Assert.Contains("creature", saved.ServerTables);
        Assert.Contains("spell_template", saved.ServerTables);
        Assert.Contains("Spell", saved.ClientTables);
        Assert.Contains(saved.Notes, n => n.Contains("questcache.wdb"));
        var recs = new[] { @"DBFilesClient\Spell.dbc", ServerChangeFiles.PathOf(c.Db.Database, "quest_template"), ServerChangeFiles.PathOf(c.Db.Database, "creature") }.Select(p => c.Ws.Get(p)!.ChangeSet).Distinct();
        Assert.Single(recs);
        // a new session sees everything; nothing is in the database until "Apply to server"
        var d2 = c.Open();
        Assert.Equal("Riposte Drill", d2.Title(drill));
        Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM quest_template WHERE entry={drill}")));
        var ap = new ServerApplier(c.Service());
        var plan = ap.Plan();
        Assert.Empty(plan.Conflicts);
        var (rec, _) = ap.Apply(plan);
        Assert.Equal(1L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM quest_template WHERE entry={hDrill}")));
        Assert.Equal(1L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM creature_questrelation WHERE id={champion} AND quest={hDrill}")));
        Assert.Equal("Drill Sergeant Halvor", ServerTestDb.Scalar(c.Db, $"SELECT name FROM creature_template WHERE entry={sergeant}"));
        Assert.True(ap.UndoConflicts(rec).Count == 0, string.Join(Environment.NewLine, ap.UndoConflicts(rec)) + Environment.NewLine +
            string.Join(Environment.NewLine, rec.Applied["creature"].Values.SelectMany(v => v!.Select(kv => kv.Key + "=" + kv.Value))) + Environment.NewLine +
            string.Join(Environment.NewLine, ServerTestDb.Dump(c.Db, "creature").Where(r => r.StartsWith("900000|"))));
        ap.Undo(rec);
        Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM quest_template WHERE entry>=900000")));
        Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM creature WHERE id={sergeant}")));
    }

    // ------------------------------------------------------------------ objectives: drop sources, speak to, explore

    [IsolatedDbFact]
    public void DropSourcesSpeakToAndExplore()
    {
        using var c = new Ctx("objectives");
        var d = c.Open();
        long q = d.New("Proof of the Drill");
        Assert.True(d.Set(q, "races", "Alliance").Ok);
        Assert.True(d.Set(q, "level", "20").Ok);
        Assert.True(d.SetRelation(q, "234", giver: true).Ok);
        Assert.True(d.SetRelation(q, "234", giver: false).Ok);
        // an item nothing gives: a new item (a copy of the Worn Shortsword)
        var it = d.T("item_template");
        var (ik, ir) = d.Ed.Duplicate("item_template", it.KeyText(d.Ed.Object(it, 25)!), new (string, string?)[] { ("name", "Highwayman's Bandana") });
        Assert.True(ir.Ok, ir.Message);
        long item = d.Ed.Object(it, 900000) != null ? 900000 : throw new Exception(ik);
        var r = d.SetItem(q, 1, item, 6);
        Assert.True(r.Ok);
        Assert.Contains(r.Problems!, p => p.IsError && p.Message.Contains("Nothing gives"));
        AssertHas(QuestRules.Check(d, new[] { q }), q, DesignSeverity.Error, "Nothing gives Highwayman's Bandana");
        var add = d.AddDropSource(item, 122, 40, questOnly: true);
        Assert.True(add.Ok, add.Message);
        var src = d.DropSources(item).Single();
        Assert.Equal(("creature", 122L, "creature_loot_template"), (src.Kind, src.Id, src.Table));
        Assert.Contains("only while on the quest", src.Detail);
        Assert.False(Has(QuestRules.Check(d, new[] { q }), q, DesignSeverity.Error, "Nothing gives"));
        var lt = d.T("creature_loot_template");
        var lootRow = d.Ed.Rows(lt, RowFilter.Where(new FilterTerm("entry", FilterOp.Eq, src.LootEntry.ToString()), new FilterTerm("item", FilterOp.Eq, item.ToString()))).Single();
        Assert.Equal(-40.0, WorldValue.Number(lootRow[lt.IndexOf("ChanceOrQuestChance")]), 3);
        // a creature without a loot table gets one (loot_id = its entry)
        var (npc, _, _) = d.NewNpc("Dummy", 821);
        var add2 = d.AddDropSource(item, npc, 100, false);
        Assert.True(add2.Ok, add2.Message);
        Assert.True(d.DropSources(item).Any(x => x.Id == npc && x.LootEntry == npc), string.Join("; ", d.DropSources(item).Select(x => x.Text + " loot " + x.LootEntry)) + " npc " + npc);
        // speak to Captain Danuvin: a gossip option shown while on the quest that gives the talk credit
        var talk = d.AddTalkObjective(q, 821, 2, "Captain, the sergeant sent me.");
        Assert.True(talk.Ok, talk.Message);
        Assert.Contains("gossip menu", talk.Message);
        Assert.True(d.IsTalkTarget(q, 821));
        Assert.Contains(d.Objectives(q), o => o.Kind == "talk" && o.Id == 821 && o.Slot == 2);
        var ct = d.T("creature_template");
        Assert.Equal(1, QuestDesigner.L(ct, d.Ed.Object(ct, 821)!, "npc_flags") & 1);
        var ot = d.T("gossip_menu_option");
        var opt = d.Ed.Rows(ot, RowFilter.Eq("menu_id", QuestDesigner.L(ct, d.Ed.Object(ct, 821)!, "gossip_menu_id"))).Single(o => QuestDesigner.L(ot, o, "action_menu_id") == -1);
        Assert.Equal("Captain, the sergeant sent me.", QuestDesigner.V(ot, opt, "option_text"));
        // explore: a new area trigger near Sentinel Hill (the game's AreaTrigger.dbc and the server's table)
        long trig = d.NewAreaTrigger(0, -10650, 1120, 40, 15);
        var ex = d.AddExplore(q, trig);
        Assert.True(ex.Ok, ex.Message);
        Assert.Equal(2, d.Summary(q).SpecialFlags & 2);
        Assert.True(d.TriggerExists(trig));
        Assert.Contains(d.Objectives(q), o => o.Kind == "explore" && o.Id == trig);
        // the event flag now forbids the talk credit: the check says so, and a second talk objective is refused
        AssertHas(QuestRules.Check(d, new[] { q }), q, DesignSeverity.Error, "VMaNGOS gives no talk credit then");
        Assert.False(d.AddTalkObjective(q, 234, 3).Ok);
        // another quest cannot use the same trigger
        long q2 = d.New("Second");
        Assert.False(d.AddExplore(q2, trig).Ok);
        var saved = d.Save();
        Assert.Contains("AreaTrigger", saved.ClientTables);
        Assert.Contains("areatrigger_involvedrelation", saved.ServerTables);
        Assert.Contains("gossip_menu_option", saved.ServerTables);
        Assert.Contains("creature_loot_template", saved.ServerTables);
        Assert.Contains(saved.Notes, n => n.Contains("AreaTrigger.dbc"));
    }

    // ------------------------------------------------------------------ the checks, each broken on purpose

    [IsolatedDbFact]
    public void EveryCheckBrokenOnPurpose()
    {
        using var c = new Ctx("rules");
        var d = c.Open();
        long a = d.New("Alpha"), b = d.New("Bravo");
        foreach (var q in new[] { a, b })
        {
            Assert.True(d.Set(q, "level", "10").Ok);
            Assert.True(d.Set(q, "races", "Alliance").Ok);
            Assert.True(d.Set(q, "objectives", "Do it.").Ok);
        }
        // nobody offers it, nobody takes it back
        var p = QuestRules.Check(d, new[] { a });
        AssertHas(p, a, DesignSeverity.Error, "Nobody offers it");
        AssertHas(p, a, DesignSeverity.Error, "Nobody takes it back");
        foreach (var q in new[] { a, b }) { d.SetRelation(q, "234", true); d.SetRelation(q, "234", false); }
        Assert.Empty(Errors(d, a, b));
        // a circle of prerequisites
        d.Set(a, "prev-quest", b.ToString());
        d.Set(b, "prev-quest", a.ToString());
        p = QuestRules.Check(d, new[] { a, b });
        Assert.True(Has(p, a, DesignSeverity.Error, "circle") && Has(p, b, DesignSeverity.Error, "circle"));
        d.Set(a, "prev-quest", "0");
        // a level gap
        d.Set(b, "min-level", "18");
        d.Set(b, "level", "18");
        AssertHas(QuestRules.Check(d, new[] { a, b }), b, DesignSeverity.Warning, "Level gap");
        d.Set(b, "min-level", "10");
        d.Set(b, "level", "11");
        // above the highest level, min above max
        d.Set(b, "max-level", "5");
        AssertHas(QuestRules.Check(d, new[] { b }), b, DesignSeverity.Error, "maximum level is below its minimum level");
        d.Set(b, "max-level", "0");
        // a prerequisite only the other faction can take; a Horde Paladin
        d.Set(a, "races", "Horde");
        p = QuestRules.Check(d, new[] { a, b });
        AssertHas(p, b, DesignSeverity.Error, "nobody can do both");
        AssertHas(p, a, DesignSeverity.Error, "hostile to the Horde");   // Gryan Stoutmantle
        d.Set(a, "classes", "Paladin");
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Error, "No race of Horde can be a Paladin");
        d.Set(a, "classes", "any");
        d.Set(a, "races", "Alliance");
        // a giver without the Quest giver flag
        var ct = d.T("creature_template");
        Assert.True(d.Ed.Set(ct.Name, ct.KeyText(d.Ed.Object(ct, 234)!), "npc_flags", "0").Ok);
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Error, "no Quest giver flag");
        d.EnsureNpcFlag(234, 2);
        // an unreachable prerequisite makes what follows unreachable
        long lonely = d.New("Lonely");
        d.Set(lonely, "races", "Alliance");
        d.SetRelation(lonely, "234", false);
        d.Set(a, "prev-quest", lonely.ToString());
        p = QuestRules.Check(d, new[] { lonely, a, b });
        AssertHas(p, a, DesignSeverity.Error, "which nobody can do");
        AssertHas(p, b, DesignSeverity.Error, "which nobody can do");
        d.Set(a, "prev-quest", "0");
        // the same choice group; a breadcrumb as prerequisite
        d.Set(a, "exclusive-group", a.ToString());
        d.Set(b, "exclusive-group", a.ToString());
        AssertHas(QuestRules.Check(d, new[] { a, b }), b, DesignSeverity.Error, "choice group");
        d.Set(a, "exclusive-group", "0");
        d.Set(b, "exclusive-group", "0");
        d.Set(a, "breadcrumb-for", lonely.ToString());
        AssertHas(QuestRules.Check(d, new[] { a, b }), b, DesignSeverity.Error, "breadcrumb");
        d.Set(a, "breadcrumb-for", "0");
        // objectives: a creature that is nowhere, a friendly target, a count of 0, a unique item asked twice
        var (ghost, _, _) = d.NewNpc("Ghost", 3098);
        Assert.True(d.SetTarget(a, 1, ghost.ToString(), 3).Ok);
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Warning, "Ghost is not spawned anywhere");
        Assert.True(d.SetTarget(a, 1, "821", 1).Ok);   // Captain Danuvin: friendly to the Alliance
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Error, "friendly to the Alliance");
        Assert.True(d.SetTarget(a, 1, "0", 0).Ok);
        // the event flag with nothing that completes it
        d.Set(a, "special-flags", "+Explore or event");
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Warning, "no area trigger or script completes it");
        d.Set(a, "special-flags", "-Explore or event");
        // text limits of the game: a long title is cut, a text too long for the quest cache blocks saving
        d.Set(a, "title", new string('T', 70));
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Warning, "shows only the first 63");
        d.Set(a, "details", "Hello $N, $X marks the spot.");
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Warning, "does not know $X");
        d.Set(a, "details", string.Concat(Enumerable.Repeat("The drill never ends. ", 100)));
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Error, "overwrites the rest of the quest");
        var e = Assert.Throws<DesignException>(() => d.Save());
        Assert.Contains("cannot hold these texts", e.Message);
        d.Set(a, "details", string.Concat(Enumerable.Repeat("$N, the drill never ends. ", 42)));
        AssertHas(QuestRules.Check(d, new[] { a }), a, DesignSeverity.Warning, "cuts it after 1023");
        d.Set(a, "details", "Short.");
        Assert.Contains("Saved into your workspace", d.Save().Message);
        // a field the server refuses: nothing changes
        Assert.False(d.Set(b, "level", "banana").Ok);
        Assert.False(d.Set(b, "races", "Goblins").Ok);
        Assert.False(d.Set(b, "no-such-field", "1").Ok);
    }

    // ------------------------------------------------------------------ the text preview: the client's own rules

    [Fact]
    public void TokenPreviewFollowsTheClient()
    {
        var p = new QuestTextPlayer("Varian", "Human", "Warrior", false);
        var f = p with { Name = "Jaina", Class = "Mage", Female = true };
        Assert.Equal("Well met, Varian the Human Warrior.", QuestText.Expand("Well met, $N the $R $C.", p).Text);
        Assert.Equal("a young human warrior", QuestText.Expand("a young $r $c", p).Text);
        Assert.Equal("line one\nline two", QuestText.Expand("line one$Bline two", p).Text);
        Assert.Equal("Thank you, brother.", QuestText.Expand("Thank you, $gbrother:sister;.", p).Text);
        Assert.Equal("Thank you, sister.", QuestText.Expand("Thank you, $gbrother:sister;.", f).Text);
        Assert.Equal("Go, lad!", QuestText.Expand("Go, $G lad : lass ;!", p).Text);           // spaces trimmed as the client does
        Assert.Equal("Go, lass!", QuestText.Expand("Go, $G lad : lass ;!", f).Text);
        var unknown = QuestText.Expand("Find $X now, $N.", p);
        Assert.Equal("Find $X now, Varian.", unknown.Text);
        Assert.Equal(new[] { "$X" }, unknown.UnknownTokens);
        Assert.Equal("Cost: 50$", QuestText.Expand("Cost: 50$", p).Text);
        Assert.Equal("Day 0", QuestText.Expand("Day $123w", p).Text);
        Assert.Equal("Day 0123", QuestText.Expand("Day $w123", p).Text);   // the number comes before the letter
        Assert.Contains("without", QuestText.Expand("$G no colon", p).UnknownTokens.Single());
        // the limits from the client's code
        Assert.Equal(0x200, QuestText.Limits.Single(l => l.Field == "title").HardBytes);
        Assert.Equal(63, QuestText.Limits.Single(l => l.Field == "title").ShownChars);
        Assert.Equal(0x800, QuestText.Limits.Single(l => l.Field == "details").HardBytes);
        Assert.Equal(0x100, QuestText.Limits.Single(l => l.Field == "objective-text1").HardBytes);
        var races = new[] { "Human", "Night Elf" };
        var classes = new[] { "Warrior" };
        Assert.Equal(12 + 9 + 2, QuestText.LongestExpansion("$N, $R", races, classes));
        Assert.Empty(QuestText.Check("details", "Short text, $N.", races, classes));
        Assert.Contains(QuestText.Check("title", new string('x', 600), races, classes), x => x.IsError);
        Assert.Contains(QuestText.Check("objective-text1", new string('x', 300), races, classes), x => x.IsError);
        Assert.Equal(3, QuestText.Bytes("é1"));
    }

    [IsolatedDbFact]
    public void PreviewOfARealQuest()
    {
        using var c = new Ctx("preview");
        var d = c.Open();
        var details = d.Preview(787, new QuestTextPlayer("Thrall", "Orc", "Warrior", false)).Single(x => x.Field.Id == "details").Preview;
        Assert.StartsWith("Throm'ka, warrior.", details.Text);   // "Throm'ka, $c." with the class in lower case
        Assert.Empty(details.UnknownTokens);
        Assert.Contains(d.Show(787, "races"), new[] { "any", "Horde", "Orc, Troll" });
    }

    // ------------------------------------------------------------------ CSV / JSON

    [IsolatedDbFact]
    public void CsvAndJsonRoundTrips()
    {
        using var c = new Ctx("exchange");
        var d = c.Open();
        var ids = QuestChains.Members(d, 1718);
        var json = Path.Combine(c.Dir, "ww.json");
        var csv = Path.Combine(c.Dir, "ww.csv");
        QuestExchange.Export(d, ids, json);
        QuestExchange.Export(d, ids, csv);
        var fromJson = QuestExchange.Read(json);
        var fromCsv = QuestExchange.Read(csv);
        Assert.Equal(ids, fromJson.Select(r => r.Id));
        Assert.Equal(QuestExchange.ToJson(fromJson), QuestExchange.ToJson(fromCsv));   // the same content either way
        Assert.Equal(File.ReadAllText(json), QuestExchange.ToJson(ids.Select(i => QuestExchange.Record(d, i)).ToList()));
        Assert.Empty(QuestExchange.Import(d, fromJson));   // nothing changes when importing what is there
        Assert.Empty(d.Ed.DirtyTables());
        // a changed title, another giver, and a new quest come in
        var text = File.ReadAllText(json).Replace("\"The Islander\"", "\"The Islander (revised)\"");
        var recs = QuestExchange.FromJson(text);
        var islander = recs.Single(r => r.Id == 1718);
        islander.Givers.Clear();
        islander.Givers.Add("creature:234");
        var extra = new QuestRecord { Id = 900500 };
        extra.Fields["title"] = "Imported Quest";
        extra.Fields["level"] = "22";
        extra.Givers.Add("creature:234");
        extra.Enders.Add("creature:234");
        recs.Add(extra);
        var msgs = QuestExchange.Import(d, recs);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("The Islander (revised)", d.Title(1718));
        Assert.Equal(new long[] { 234 }, d.Givers(1718).Where(g => g.Kind == "creature").Select(g => g.Id));
        Assert.Equal(22, d.Summary(900500).Level);
        Assert.Throws<DesignException>(() => QuestExchange.FromJson("{\"quests\":[{\"id\":1,\"fields\":{\"banana\":\"1\"}}]}"));
        // CSV with a text that has commas, quotes and new lines
        Assert.True(d.Set(900500, "details", "Line one, \"quoted\"$BLine two\nThird").Ok);
        QuestExchange.Export(d, new long[] { 900500 }, csv);
        Assert.Equal("Line one, \"quoted\"$BLine two\nThird", QuestExchange.Read(csv).Single().Fields["details"]);
    }

    [IsolatedDbFact]
    public void DeleteRelinksTheChainAndTestInGameExplains()
    {
        using var c = new Ctx("delete");
        var d = c.Open();
        long a = d.New("One");
        d.SetRelation(a, "234", true);
        d.SetRelation(a, "234", false);
        long b = d.AddAfter(a, "Two");
        long c3 = d.AddAfter(b, "Three");
        Assert.Equal((a, b), (d.Summary(b).Prev, d.Summary(a).NextInChain));
        Assert.True(d.Delete(b).Ok);
        Assert.False(d.Exists(b));
        Assert.Equal((a, c3), (d.Summary(c3).Prev, d.Summary(a).NextInChain));
        Assert.Empty(d.Givers(b));
        var lines = d.TestInGame(c3);
        Assert.Contains(lines, l => l.Contains($".quest add {c3}"));
        Assert.Contains(lines, l => l.Contains(".go creature id 234"));
        Assert.Contains(lines, l => l.Contains(".reload all_quest"));
        Assert.Contains(lines, l => l.Contains($".quest add {a} then .quest complete {a}"));
        // categories (QuestSort.dbc): the Warrior's, a new one
        Assert.Contains(d.Categories(), x => x.Name == "Warrior" && x.Id == 81);
        Assert.Equal(-81, d.ResolveCategory("sort:Warrior"));
        Assert.Equal(40, d.ResolveCategory("Westfall"));
        long sort = d.NewCategory("Stance Disciplines");
        Assert.Equal(-sort, d.ResolveCategory("sort:Stance Disciplines"));
        Assert.True(d.Set(c3, "category", "sort:Stance Disciplines").Ok);
        var res = d.Save();
        Assert.Contains("QuestSort", res.ClientTables);
        Assert.Contains(res.Notes, n => n.Contains("QuestSort.dbc"));
    }

    [Fact]
    public void WithoutTheServerTheDesignerSaysWhy()
    {
        var dir = TestEnv.TempDir("quests_noserver");
        try
        {
            var ws = new Workspace(new ClientPaths(Path.Combine(dir, "client"), TestEnv.ClientDir));
            var d = new QuestDesigner(new DesignSession(new DbcService(TestEnv.Client, ws), null, (900000, 999999)));
            Assert.False(d.HasServer);
            var e = Assert.Throws<DesignException>(() => d.Find("whirlwind"));
            Assert.Contains("needs the server's world database", e.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
