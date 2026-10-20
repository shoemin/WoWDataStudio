using System.IO;
using System.Linq;
using WoWData.Core.Dbc;
using WoWData.Core.Designers;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.12.0: the designers with the server's world database: ONLY the isolated throwaway MySQL server of ServerTestDb (a
/// fresh copy of the fixture per test, never the live database). The game's Spell.dbc and the server's spell_template
/// stay in step (the client / server check finds no difference), trainers get their learn spell, skill lines exist on
/// both sides, the class view reads the trainers, a new class gets its server rows, class masks of server tables, and an
/// apply writes the designed rows into the throwaway server.
/// </summary>
public class DesignerServerTests
{
    private sealed class Ctx : IDisposable
    {
        private readonly List<(string, long, DateTime)> _client = TestEnv.DataSnapshot();

        public Ctx(string name)
        {
            Db = ServerTestDb.NewDatabase(name);
            Dir = TestEnv.TempDir("designer_server_" + name);
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

        /// <summary>A fresh designer session (as after a restart of the app): the saved workspace and the server.</summary>
        public (DesignSession S, SpellDesigner D) Open()
        {
            var svc = Service();
            var s = new DesignSession(svc.Dbc!, svc.NewEditor(), (900000, 999999));
            return (s, new SpellDesigner(s));
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

    [IsolatedDbFact]
    public void ClonedSpellsKeepTheGameAndTheServerInStep()
    {
        using var c = new Ctx("spells");
        var (s, d) = c.Open();
        Assert.True(s.HasServer);
        Assert.NotNull(d.ServerRow(695));                   // Shadow Bolt rank 2 is in the fixture's spell_template
        long lance = d.Clone(686, "Shadow Lance", rank: "Rank 1");
        long lance2 = d.Clone(695, "Shadow Lance", rank: "Rank 2");
        Assert.Equal((35000, 35001), (lance, lance2));   // 0.13.3: the spell ID range (VMaNGOS casts no spell above 40000)
        Assert.True(d.Set(lance, "cast-time", "2").Ok);
        Assert.True(d.Set(lance, "effect1-value", "40-48").Ok);
        Assert.True(d.Set(lance, "description", "Hurls a lance of shadow, causing $s1 Shadow damage.").Ok);
        Assert.True(d.Set(lance, "server-bonus1", "0.75").Ok);
        // a server spell already uses an ID: the next free one is taken
        ServerTestDb.Exec(c.Db, "INSERT INTO spell_template (entry, build, name) VALUES (35002, 5875, 'Taken')");
        long n = d.New("Fresh");
        Assert.Equal(35003, n);
        d.Set(n, "effect1", "Heal");
        d.Set(n, "effect1-target-a", "Unit caster");
        d.Set(n, "effect1-value", "50");
        var res = d.Save();
        Assert.Contains("spell_template", res.ServerTables);
        Assert.False(res.ServerSkipped);
        // one change set for both sides
        var spellRec = c.Ws.Get(@"DBFilesClient\Spell.dbc")!;
        var serverRec = c.Ws.Get(ServerChangeFiles.PathOf(c.Db.Database, "spell_template"))!;
        Assert.Equal(spellRec.ChangeSet, serverRec.ChangeSet);

        // a new session: the client / server check finds no difference for the designed spells
        var (s2, d2) = c.Open();
        Assert.Empty(ServerConsistency.Check(s2.Server!, "spells", new HashSet<long> { lance, lance2, n }));
        var st = s2.Server!.Table("spell_template");
        var row = s2.Server.Object(st, lance)!;
        Assert.Equal("5875", row[st.IndexOf("build")]);
        Assert.Equal("Hurls a lance of shadow, causing $s1 Shadow damage.", row[st.IndexOf("description")]);
        Assert.Equal(0.75, WorldValue.Number(row[st.IndexOf("effectBonusCoefficient1")]), 3);
        var src = s2.Server.Object(st, 695)!;
        var row2 = s2.Server.Object(st, lance2)!;
        Assert.Equal(src[st.IndexOf("effectBonusCoefficient1")], row2[st.IndexOf("effectBonusCoefficient1")]);   // copied from the source's server row

        // apply to the (throwaway) server: the rows are in its database afterwards
        var ap = new ServerApplier(c.Service());
        var plan = ap.Plan();
        Assert.Empty(plan.Conflicts);
        var (rec, _) = ap.Apply(plan);
        Assert.Equal(1L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM spell_template WHERE entry={lance} AND build=5875")));
        Assert.Equal("Shadow Lance", ServerTestDb.Scalar(c.Db, $"SELECT name FROM spell_template WHERE entry={lance2}"));
        ap.Undo(rec);
        Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, $"SELECT COUNT(*) FROM spell_template WHERE entry={lance}")));
    }

    /// <summary>
    /// 0.13.3 (found in game): spells made by 0.12.0-0.13.2 got IDs from 900000, which VMaNGOS never casts (Spell::cast
    /// returns when the ID is above MAX_SPELL_ID 40000), so their learn spell taught nothing from a quest or trainer.
    /// "Change ID" / "Fix all spell IDs" moves them into the spell ID range with every reference: the learn spell's
    /// trigger, the skill line, the trainer row, the reward quest, on both sides; the apply writes it to the server.
    /// </summary>
    [IsolatedDbFact]
    public void SpellsAboveTheServersLimitMoveWithEveryReference()
    {
        using var c = new Ctx("renumber");
        var (s, d) = c.Open();
        // the state 0.13.2 left: S1 900000 (Shadow Bolt rank 2's copy), its learn spell 900001, a skill line row, all
        // in the workspace, and on the server (applied): the two spells, the skill line, a trainer row and a reward quest
        var ss = s.Session("Spell");
        ss.Duplicate(d.Row(695), 900000);
        var (lr, _) = ss.Duplicate(d.Row(1381), 900001);
        s.SetNumber("Spell", lr, "EffectTriggerSpell1", 900000);
        long slaId = s.NextId("SkillLineAbility", "skill_line_ability", "id", 65535);
        var (sr, _) = s.Session("SkillLineAbility").AddRow(slaId);
        s.SetNumber("SkillLineAbility", sr, "SkillLine", 593);
        s.SetNumber("SkillLineAbility", sr, "Spell", 900000);
        s.SetNumber("SkillLineAbility", sr, "ClassMask", 0x100);
        d.Save(force: true);
        foreach (var (e, trig) in new[] { (900000, 0), (900001, 900000) })
            ServerTestDb.Exec(c.Db, "DROP TEMPORARY TABLE IF EXISTS t; CREATE TEMPORARY TABLE t AS SELECT * FROM spell_template WHERE entry = 695 LIMIT 1; " +
                $"UPDATE t SET entry = {e}, build = 5875{(trig != 0 ? $", effect1 = 36, effectTriggerSpell1 = {trig}" : "")}; INSERT INTO spell_template SELECT * FROM t;");
        ServerTestDb.Exec(c.Db, $"INSERT INTO skill_line_ability (id, build, skill_id, spell_id, class_mask) VALUES ({slaId}, 5875, 593, 900000, 256)");
        ServerTestDb.Exec(c.Db, "INSERT INTO npc_trainer (entry, spell, spellcost, reqlevel, build_min, build_max) VALUES (3156, 900001, 100, 1, 0, 5875)");
        ServerTestDb.Exec(c.Db, "INSERT INTO quest_template (entry, Title, RewSpell, RewSpellCast) VALUES (900100, 'WDS old reward', 900000, 900001)");

        (s, d) = c.Open();
        var ed = s.Server!;
        Assert.Equal(new List<long> { 900000, 900001 }, d.UnusableSpells());
        Assert.Contains(d.Check(900000), p => p.IsError && p.Field == "ID" && p.Message.Contains("MAX_SPELL_ID") && p.Message.Contains("48032"));
        Assert.Contains(d.Check(900001), p => p.IsError && p.Message.Contains("trigger spell"));
        var st = ed.Table("spell_template");
        Assert.Contains(ServerValidation.CheckRow(ed, st, ed.Object(st, 900000)!), p => p.IsError && p.Column == "entry");
        var qt = ed.Table("quest_template");
        Assert.Contains(ServerValidation.CheckRow(ed, qt, ed.Object(qt, 900100)!), p => p.IsError && p.Column == "RewSpellCast");
        d.Touch(900000);
        Assert.Contains("MAX_SPELL_ID", Assert.Throws<DesignException>(() => d.Save()).Message);   // the errors block saving until the IDs are fixed
        Assert.False(d.ChangeId(695).Ok);                  // the game's own spells stay where they are
        Assert.False(d.ChangeId(900000, 40001).Ok);        // and nothing goes above 40000

        var r = d.ChangeIds();
        Assert.True(r.Ok, r.Message);
        Assert.Equal(35000, d.Moved[900000]);
        Assert.Equal(35001, d.Moved[900001]);
        Assert.Empty(d.UnusableSpells());
        Assert.Null(d.Spells.Find(900000));
        Assert.Equal(35000, d.Spells.Number(d.Row(35001), d.Spells.Layout.Column("EffectTriggerSpell1")!));
        Assert.Equal(new long[] { 35001 }, d.LearnSpellsOf(35000));
        Assert.Equal(slaId, d.SkillLines(35000).Single().Id);
        Assert.Empty(d.Check(35000).Where(p => p.IsError));
        Assert.Empty(d.Check(35001).Where(p => p.IsError));
        Assert.Empty(ed.Versions(st, 900000));
        Assert.Equal(35001, d.Trainers(35000).Single().LearnSpell);
        var q = ed.Object(qt, 900100)!;
        Assert.Equal(("35000", "35001"), (q[qt.IndexOf("RewSpell")], q[qt.IndexOf("RewSpellCast")]));
        var res = d.Save();
        Assert.Contains("spell_template", res.ServerTables);

        // a fresh session finds the moved rows, then the apply writes them to the (throwaway) server
        (s, d) = c.Open();
        Assert.Empty(d.UnusableSpells());
        Assert.Empty(ServerConsistency.Check(s.Server!, "spells", new HashSet<long> { 35000, 35001 }));
        var ap = new ServerApplier(c.Service());
        var plan = ap.Plan();
        Assert.Empty(plan.Conflicts);
        ap.Apply(plan);
        long Count(string sql) => Convert.ToInt64(ServerTestDb.Scalar(c.Db, sql));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM spell_template WHERE entry IN (900000, 900001)"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM spell_template WHERE entry = 35001 AND effect1 = 36 AND effectTriggerSpell1 = 35000"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM npc_trainer WHERE entry = 3156 AND spell = 35001"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM npc_trainer WHERE spell = 900001"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM quest_template WHERE entry = 900100 AND RewSpell = 35000 AND RewSpellCast = 35001"));
        Assert.Equal(1L, Count($"SELECT COUNT(*) FROM skill_line_ability WHERE id = {slaId} AND build = 5875 AND spell_id = 35000"));
        // a new spell_template row of the Server data tab takes the spell ID range too
        var (s3, _) = c.Open();
        Assert.Equal(35002, s3.Server!.SuggestId(s3.Server.Table("spell_template")));
    }

    [IsolatedDbFact]
    public void TrainersSkillLinesAndTheClassView()
    {
        using var c = new Ctx("learn");
        // Nartok (3156) teaches warlocks from his own list here
        ServerTestDb.Exec(c.Db, "UPDATE creature_template SET npc_flags = npc_flags | 16, trainer_type = 0, trainer_class = 9, trainer_id = 0 WHERE entry = 3156");
        var (s, d) = c.Open();
        long lance = d.Clone(686, "Shadow Lance", rank: "Rank 1");
        var r = d.AddToTrainer(lance, "3156", 20, SpellDesigner.ParseMoney("25s"));
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Made the learn spell", r.Message);
        long learn = d.LearnSpellsOf(lance).Single();
        // 0.13.2 (found in game): the class trainer's window hides a spell without a skill line, and the trainer shows a
        // learn spell's own description: new learn spells have none (as the game's own), the checks say both
        Assert.Contains(d.Check(lance), p => p.Severity == DesignSeverity.Warning && p.Message.Contains("is a class trainer") && p.Message.Contains("hides"));
        Assert.Equal("", d.Spells.Text(d.Row(learn), d.Spells.Layout.Column("Description")!, 0));
        Assert.DoesNotContain(d.Check(learn), p => p.Field == "Description");
        Assert.True(d.Set(learn, "description", "Hurls a lance, causing $s1 Shadow damage.").Ok);
        Assert.Contains(d.Check(learn), p => p.Field == "Description" && p.Message.Contains("trainer window shows"));
        Assert.True(d.Set(learn, "description", "").Ok);
        Assert.True(d.AddSkillLine(lance, 593, 0x100).Ok);
        Assert.DoesNotContain(d.Check(lance), p => p.Message.Contains("is a class trainer"));
        Assert.False(d.AddToTrainer(lance, "99999999", 20, 0).Ok);
        Assert.DoesNotContain(d.Check(lance), p => p.IsError);
        var res = d.Save();
        Assert.Contains("npc_trainer", res.ServerTables);
        Assert.Contains("skill_line_ability", res.ServerTables);
        Assert.Contains("SkillLineAbility", res.ClientTables);

        var (s2, d2) = c.Open();
        var trainers = d2.Trainers(lance);
        var t = trainers.Single();
        Assert.Equal(("npc_trainer", 3156L, 20L, 2500L, learn), (t.Table, t.Trainer, t.Level, t.Cost, t.LearnSpell));
        Assert.Empty(ServerConsistency.Check(s2.Server!, "spells", new HashSet<long> { lance, learn }));
        long slaId = d2.SkillLines(lance).Single().Id;
        Assert.Empty(ServerConsistency.Check(s2.Server!, "skills", new HashSet<long> { slaId }));
        // the server's own trainer rule: the learn spell's effect 1 teaches the spell
        var st = s2.Server!.Table("spell_template");
        var lr = s2.Server.Object(st, learn)!;
        Assert.Equal(("36", lance.ToString()), (lr[st.IndexOf("effect1")], lr[st.IndexOf("effectTriggerSpell1")]));
        // the class view reads the trainer
        var k = new ClassDesigner(s2, d2);
        var item = k.SpellList(9).Single(x => x.Spell == lance && x.Source == SpellSource.Trainer);
        Assert.Equal((20L, "Nartok", learn), (item.Level, item.From, item.LearnSpell));
        // assign a new level: the trainer row follows
        Assert.True(k.Assign(9, lance, 22, cost: 3000).Ok);
        Assert.Equal((22L, 3000L), (d2.Trainers(lance).Single().Level, d2.Trainers(lance).Single().Cost));
        // a talent spell in a trainer list is refused by the server (GetTalentSpellCost): the talent check says so
        var td = new TalentDesigner(s2, d2);
        long tab = td.AddTree(9, "Shadowcraft");
        long tal = td.AddTalent(tab, 0, 0, new[] { learn });
        Assert.Contains(td.Check(tab), p => p.Message.Contains("trainer lists"));
        Assert.True(td.RemoveTalent(tal).Ok);
        Assert.True(d2.RemoveFromTrainer(lance, "3156").Ok);
        Assert.Empty(d2.Trainers(lance));
    }

    [IsolatedDbFact]
    public void TalentRankSpellsMustBeOnTheServer()
    {
        using var c = new Ctx("talents");
        var (s, d) = c.Open();
        var t = new TalentDesigner(s, d);
        long tab = t.AddTree(1, "Stance Disciplines", "Ability_Warrior_DefensiveStance", "WarriorProtection");
        var ranks = t.MakeRankSpells(12295, "Iron Line", 3, new[] { "1", "2", "3" });
        long tal = t.AddTalent(tab, 0, 1, ranks);
        Assert.DoesNotContain(t.Check(tab), p => p.IsError);   // made in this session: they go to the server on save
        var res = t.Save();
        Assert.Contains("Talent", res.ClientTables);
        Assert.Contains("TalentTab", res.ClientTables);
        Assert.Contains(res.Notes, x => x.Contains("Talent.dbc"));   // the server reads its own copy
        var (s2, d2) = c.Open();
        Assert.Empty(ServerConsistency.Check(s2.Server!, "spells", ranks.ToHashSet()));
        // a rank spell the server does not have: a new spell is an error, a spell of the game's own file a warning (a gap
        // of this database: the fixture has a sample of spell_template)
        var t2 = new TalentDesigner(s2, d2);
        var row = s2.Table("Talent").Find(tal)!;
        Assert.Null(d2.ServerRow(686));
        s2.SetNumber("Talent", row, "SpellRank1", 686);
        Assert.Contains(t2.Check(tab), p => p.Severity == DesignSeverity.Warning && p.Message.Contains("The server has no spell 686"));
        var newRow = s2.AddRow("Spell", 950000);   // only in the game's table (not through the spell designer)
        s2.Set("Spell", newRow, "Name", "Client Only");
        s2.SetNumber("Talent", row, "SpellRank1", 950000);
        Assert.Contains(t2.Check(tab), p => p.IsError && p.Message.Contains("The server has no spell 950000"));
    }

    [IsolatedDbFact]
    public void ANewClassGetsItsServerRowsAndMasks()
    {
        using var c = new Ctx("newclass");
        // the fixture has no character-creation rows: give the template class (Paladin, Human) some
        ServerTestDb.Exec(c.Db, "INSERT INTO playercreateinfo VALUES (1,2,0,12,-8949.95,-132.493,83.5312,0)");
        ServerTestDb.Exec(c.Db, "INSERT INTO playercreateinfo_spell (race,class,spell,build_min,build_max,note) VALUES (1,2,635,0,5875,'Holy Light'),(1,2,21084,0,5875,'Seal of Righteousness')");
        ServerTestDb.Exec(c.Db, "INSERT INTO playercreateinfo_action VALUES (1,2,0,21084,0),(1,2,1,635,0)");
        ServerTestDb.Exec(c.Db, "INSERT INTO playercreateinfo_item VALUES (1,2,2361,1),(1,2,45,1)");
        ServerTestDb.Exec(c.Db, "INSERT INTO player_classlevelstats VALUES (2,1,28,60),(2,2,36,78)");
        ServerTestDb.Exec(c.Db, "INSERT INTO player_levelstats VALUES (1,2,1,22,20,22,20,21),(1,2,2,23,21,23,20,22)");
        var (s, d) = c.Open();
        var k = new ClassDesigner(s, d);
        var (id, done) = k.NewClass("Knight", "KNIGHT", 2, new long[] { 1 });
        Assert.Equal(6, id);
        Assert.Contains(done, x => x.Contains("row(s) of playercreateinfo"));
        // a class mask on the server: Worn Shortsword for everyone but warlocks
        Assert.True(k.EditMask("item_template", null, new[] { "25" }, 9, false).Ok);
        var res = k.Save();
        Assert.Contains("playercreateinfo", res.ServerTables);
        Assert.Contains("player_classlevelstats", res.ServerTables);
        var (s2, _) = c.Open();
        var ed = s2.Server!;
        foreach (var (table, count) in new[] { ("playercreateinfo", 1), ("playercreateinfo_spell", 2), ("playercreateinfo_action", 2), ("playercreateinfo_item", 2), ("player_levelstats", 2) })
            Assert.Equal(count, ed.Rows(ed.Table(table), RowFilter.Where(new FilterTerm("race", FilterOp.Eq, "1"), new FilterTerm("class", FilterOp.Eq, "6"))).Count);
        Assert.Equal(2, ed.Rows(ed.Table("player_classlevelstats"), RowFilter.Eq("class", 6)).Count);
        var it = ed.Table("item_template");
        long mask = WorldValue.Long(ed.Object(it, 25)![it.IndexOf("allowable_class")]);
        Assert.Equal(0, mask & 0x100);
        Assert.NotEqual(0, mask & 0x1);
        // nothing reached the database itself: the rows are only in the workspace until "Apply to server"
        Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(c.Db, "SELECT COUNT(*) FROM playercreateinfo WHERE class=6")));
    }
}
