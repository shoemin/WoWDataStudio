using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WoWData.Cli;
using WoWData.Core.Designers;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>
/// 0.13.0: "wds quests" in an isolated home and fake game folder (CliTestEnv) with the throwaway database (ServerTestDb,
/// the fixture plus the quest-chain supplement; never the live server): every command, its exit codes (0, 1, 2, 3, 5, 6)
/// and its --json; and the Quest chains tab (its view model, headless) and the command line making the same rows.
/// </summary>
[Collection(IndexCollection.Name)]
public class QuestCliTests
{
    [Fact]
    public void WithoutTheServerTheQuestCommandsSayWhy()
    {
        using var env = new CliTestEnv("quests_noserver");
        var r = env.Json("quests", "find", "whirlwind");
        Assert.Equal(ExitCode.Error, r.Code);
        Assert.Contains("needs the server's world database", r.Json.GetProperty("error").GetString());
        var fields = env.Json("quests", "fields");
        Assert.True(fields.Code == 0, fields.ToString());
        Assert.True(fields.Data.GetArrayLength() > 80);
        Assert.Equal(ExitCode.Usage, env.Run("quests", "banana").Code);
    }

    [IsolatedDbFact]
    public void EveryQuestCommandWithItsExitCodes()
    {
        using var env = new CliTestEnv("quests");
        var db = ServerTestDb.NewDatabase("questcli", questChains: true);
        ServerCliTests.UseServer(env, db);
        try
        {
            // find, show, chain, check
            var find = env.Json("quests", "find", "whirlwind");
            Assert.True(find.Code == 0, find.ToString());
            Assert.Contains(find.Data.GetProperty("results").EnumerateArray(), x => x.GetProperty("id").GetInt64() == 1792);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "find", "zzznosuchquestzzz").Code);
            var show = env.Json("quests", "show", "1718");
            Assert.Equal("The Islander", show.Data.GetProperty("fields").GetProperty("title").GetString());
            Assert.Contains(show.Data.GetProperty("enders").EnumerateArray(), x => x.GetProperty("id").GetInt64() == 6236);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "show", "99999999").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "show", "abc").Code);
            var chain = env.Json("quests", "chain", "1718");
            Assert.Equal(7, chain.Data.GetProperty("quests").GetArrayLength());
            Assert.Equal("Both factions · Warrior", chain.Data.GetProperty("lanes")[0].GetProperty("name").GetString());
            Assert.Contains(chain.Data.GetProperty("links").EnumerateArray(), l => l.GetProperty("from").GetInt64() == 1712 && l.GetProperty("to").GetInt64() == 1714 && l.GetProperty("kinds").GetString() == "RequiresActive");
            Assert.Contains("flowchart LR", env.Run("quests", "chain", "1718", "--mermaid").Out);
            var dread = env.Json("quests", "check", "7562");
            Assert.Equal(ExitCode.ValidationFailed, dread.Code);
            Assert.Contains(dread.Data.GetProperty("problems").EnumerateArray(), p => p.GetProperty("message").GetString()!.Contains("Nothing gives"));
            Assert.Equal(0, env.Run("quests", "check", "1718", "--only").Code);

            // the NPC picker
            var npcs = env.Json("quests", "npcs", "gryan");
            Assert.True(npcs.Code == 0, npcs.ToString());
            Assert.Contains(npcs.Data.GetProperty("results").EnumerateArray(), x => x.GetProperty("key").GetString() == "creature:234" && x.GetProperty("spawns").GetInt32() >= 1);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "npcs", "zzznobodyzzz").Code);

            // new quest givers, the discipline template, the other faction
            var sgt = env.Json("quests", "npc", "Drill Sergeant Halvor", "--copy-from", "821", "--subname", "Drill Sergeant", "--map", "0", "--x", "-10508", "--y", "1053", "--z", "55.7");
            Assert.True(sgt.Code == 0, sgt.ToString());
            Assert.Equal(900000, sgt.Data.GetProperty("data").GetProperty("entry").GetInt64());
            Assert.Equal(0, env.Run("quests", "npc", "Champion Gorrak", "--copy-from", "3429", "--map", "1", "--x", "-470", "--y", "-2620", "--z", "92").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "npc", "Nobody").Code);   // --copy-from is needed
            var tpl = env.Json("quests", "template", "Riposte Drill", "--faction", "Alliance", "--giver", "900000", "--intro-from", "234", "--outpost", "Sentinel Hill", "--kill", "122:8", "--clone-spell", "12295", "--xp", "1450");
            Assert.True(tpl.Code == 0, tpl.ToString());
            var qs = tpl.Data.GetProperty("data").GetProperty("quests").EnumerateArray().Select(x => x.GetInt64()).ToList();
            Assert.Equal(2, qs.Count);
            long intro = qs[0], drill = qs[1];
            Assert.Equal(ExitCode.Usage, env.Run("quests", "template", "Nope", "--giver", "900000", "--clone-spell", "12295").Code);   // no --kill
            Assert.Equal(ExitCode.ValidationFailed, env.Run("quests", "template", "Nope", "--giver", "99999999", "--kill", "122", "--clone-spell", "12295").Code);
            var dup = env.Json("quests", "duplicate", intro.ToString(), "--faction", "Horde", "--swap", "900000=900001", "--swap", "234=3429", "--swap", "122=3258", "--swap", "text:Sentinel Hill=the Crossroads");
            Assert.True(dup.Code == 0, dup.ToString());
            var copies = dup.Data.GetProperty("data").GetProperty("copies").EnumerateArray().ToDictionary(x => x.GetProperty("from").GetInt64(), x => x.GetProperty("to").GetInt64());
            Assert.Equal(2, copies.Count);
            var hd = env.Json("quests", "show", copies[drill].ToString());
            Assert.Equal("Horde", hd.Data.GetProperty("fields").GetProperty("races").GetString());
            Assert.Contains(hd.Data.GetProperty("givers").EnumerateArray(), x => x.GetProperty("id").GetInt64() == 900001);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "duplicate", intro.ToString()).Code);   // --faction
            Assert.Equal(ExitCode.ValidationFailed, env.Run("quests", "duplicate", intro.ToString(), "--faction", "Horde", "--swap", "banana").Code);

            // branch, join, givers, objectives, rewards, drop sources
            var br = env.Json("quests", "branch", drill.ToString(), "--titles", "The Sword Path|The Axe Path|The Mace Path", "--rewards", "25,117,159");
            Assert.True(br.Code == 0, br.ToString());
            var branches = br.Data.GetProperty("data").GetProperty("branches").EnumerateArray().Select(x => x.GetInt64().ToString()).ToList();
            Assert.Equal(3, branches.Count);
            Assert.Equal(ExitCode.ValidationFailed, env.Run("quests", "join", string.Join(",", branches), "--title", "All", "--all").Code);   // a choice is never "all of"
            Assert.Equal(0, env.Run("quests", "join", string.Join(",", branches), "--title", "Report Back", "--any").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "join", string.Join(",", branches), "--title", "X").Code);
            Assert.Equal(0, env.Run("quests", "giver", intro.ToString(), "821").Code);
            Assert.Equal(0, env.Run("quests", "giver", intro.ToString(), "821", "--remove").Code);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "giver", intro.ToString(), "99999999").Code);
            Assert.Equal(0, env.Run("quests", "ender", drill.ToString(), "creature:900000").Code);
            var obj = env.Json("quests", "objective", drill.ToString(), "--item", "117:3", "--slot", "2");
            Assert.True(obj.Code == 0, obj.ToString());
            Assert.Contains(obj.Data.GetProperty("data").GetProperty("objectives").EnumerateArray(), o => o.GetProperty("kind").GetString() == "item" && o.GetProperty("id").GetInt64() == 117);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "objective", drill.ToString()).Code);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "objective", drill.ToString(), "--kill", "99999999:2").Code);
            var talk = env.Json("quests", "talk", drill.ToString(), "821", "--option", "Captain, the sergeant sent me.", "--slot", "3");
            Assert.True(talk.Code == 0, talk.ToString());
            Assert.Contains(talk.Data.GetProperty("data").GetProperty("objectives").EnumerateArray(), o => o.GetProperty("kind").GetString() == "talk");
            Assert.Equal(0, env.Run("quests", "explore", intro.ToString(), "--map", "0", "--x", "-10650", "--y", "1120", "--z", "40", "--radius", "15").Code);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "explore", intro.ToString(), "--trigger", "99999999").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "explore", intro.ToString(), "--map", "0").Code);
            var rw = env.Json("quests", "reward", drill.ToString(), "--money", "1g 50s", "--xp", "1500", "--choice", "25", "--choice", "117:2");
            Assert.True(rw.Code == 0, rw.ToString());
            Assert.Equal("1g 50s", rw.Data.GetProperty("data").GetProperty("fields").GetProperty("money").GetString());
            Assert.Equal(ExitCode.Usage, env.Run("quests", "reward", drill.ToString()).Code);
            var drops = env.Json("quests", "drops", "117", "--creature", "122", "--chance", "40");
            Assert.True(drops.Code == 0, drops.ToString());
            Assert.Contains(drops.Data.GetProperty("data").GetProperty("sources").EnumerateArray(), s => s.GetProperty("id").GetInt64() == 122);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "drops", "99999999").Code);
            Assert.Equal(0, env.Run("quests", "place", "900000", "--map", "0", "--x", "-10500", "--y", "1050", "--z", "55").Code);

            // set, new, preview, categories
            Assert.Equal(0, env.Run("quests", "set", drill.ToString(), "level", "21").Code);
            Assert.Equal(0, env.Run("quests", "set", drill.ToString(), "--set", "title=Riposte Drill", "--set", "flags=+Sharable").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "set", drill.ToString(), "banana", "1").Code);
            Assert.Equal(ExitCode.ValidationFailed, env.Run("quests", "set", drill.ToString(), "races", "Goblins").Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "set", drill.ToString()).Code);
            int before = new Workspace(env.Paths).List().Count;
            var dry = env.Json("quests", "new", "Loose", "--dry-run");
            Assert.False(dry.Data.GetProperty("saved").GetBoolean());
            Assert.Equal(before, new Workspace(env.Paths).List().Count);
            var after = env.Json("quests", "new", "Warm-down", "--after", drill.ToString(), "--set", "level=21");
            Assert.True(after.Code == 0, after.ToString());
            Assert.Equal(drill.ToString(), after.Data.GetProperty("data").GetProperty("fields").GetProperty("prev-quest").GetString()!.Split(' ')[0]);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "new", "X", "--after", "99999999").Code);
            var pv = env.Json("quests", "preview", "787", "--name", "Jaina", "--race", "Human", "--class", "Mage", "--female");
            Assert.StartsWith("Throm'ka, mage.", pv.Data.EnumerateArray().Single(x => x.GetProperty("field").GetString() == "details").GetProperty("text").GetString());
            Assert.Contains(env.Json("quests", "categories").Data.EnumerateArray(), c => c.GetProperty("name").GetString() == "Warrior");
            Assert.Equal(0, env.Run("quests", "categories", "--new", "Stance Disciplines").Code);
            Assert.Equal(ExitCode.ValidationFailed, env.Run("quests", "categories", "--new", "Warrior").Code);

            // export / import, delete, test in game
            var json = Path.Combine(env.Work, "drill.json");
            var csv = Path.Combine(env.Work, "drill.csv");
            Assert.Equal(0, env.Run("quests", "export", intro.ToString(), "--out", json).Code);
            Assert.Equal(0, env.Run("quests", "export", intro.ToString(), "--out", csv).Code);
            Assert.Equal(ExitCode.Usage, env.Run("quests", "export", intro.ToString()).Code);
            Assert.Contains("Nothing to change", env.Run("quests", "import", json).Out);
            Assert.Contains("Nothing to change", env.Run("quests", "import", csv).Out);
            File.WriteAllText(json, File.ReadAllText(json).Replace("\"Riposte Drill\"", "\"Riposte Drill (revised)\""));
            var imp = env.Json("quests", "import", json);
            Assert.True(imp.Code == 0, imp.ToString());
            Assert.Equal("Riposte Drill (revised)", env.Json("quests", "show", drill.ToString()).Data.GetProperty("title").GetString());
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "import", Path.Combine(env.Work, "none.json")).Code);
            long del = long.Parse(after.Data.GetProperty("data").GetProperty("id").GetRawText());
            Assert.Equal(ExitCode.NeedsYes, env.Run("quests", "delete", del.ToString()).Code);
            Assert.Equal(ExitCode.NeedsYes, env.RunExe("quests", "delete", del.ToString()).Code);   // the out-of-process program: real exit codes
            Assert.Equal(0, env.Run("quests", "delete", del.ToString(), "--yes").Code);
            Assert.Equal(ExitCode.NotFound, env.Run("quests", "show", del.ToString()).Code);
            var tig = env.Json("quests", "test-in-game", drill.ToString());
            Assert.Contains(tig.Data.GetProperty("steps").EnumerateArray(), l => l.GetString()!.Contains($".quest add {drill}"));
            // the chain and its copy have no errors left
            Assert.Equal(0, env.Run("quests", "check", copies[intro].ToString()).Code);
            // nothing reached the database: only "wds server apply --yes" writes it
            Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(db, "SELECT COUNT(*) FROM quest_template WHERE entry >= 900000")));
            Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(db, "SELECT COUNT(*) FROM creature_template WHERE entry >= 900000")));
        }
        finally
        {
            try { ServerTestDb.Exec(db, $"DROP DATABASE `{db.Database}`"); }
            catch (MySqlConnector.MySqlException) { }
        }
    }

    /// <summary>The server edits of a workspace, by table, without the database's name (two throwaway databases).</summary>
    private static Dictionary<string, string> ServerFiles(Workspace ws)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ws.ModifiedPaths().Where(ServerChangeFiles.IsServerPath))
        {
            var node = JsonNode.Parse(ws.ReadEdited(p)!)!.AsObject();
            node.Remove("database");
            res[ServerChangeFiles.Parse(p)!.Value.Table] = node.ToJsonString();
        }
        return res;
    }

    [IsolatedDbFact]
    public void TheQuestTabAndTheCommandLineMakeTheSameRows()
    {
        using var gui = new CliTestEnv("quests_gui");
        using var cli = new CliTestEnv("quests_cli");
        var gdb = ServerTestDb.NewDatabase("questgui", questChains: true);
        var cdb = ServerTestDb.NewDatabase("questcli2", questChains: true);
        ServerCliTests.UseServer(gui, gdb);
        ServerCliTests.UseServer(cli, cdb);
        try
        {
            UiTestsHelper.RunSta(() =>
            {
                var paths = new AppPaths(gui.Home);
                using var session = new AppSession(paths, AppSettings.Load(paths));
                session.OpenAsync(gui.Client, false, null).GetAwaiter().GetResult();
                var main = new MainViewModel(session);
                var browser = new BrowserViewModel(session, main);
                var q = browser.Quests;
                q.EnsureLoadedAsync().GetAwaiter().GetResult();
                Assert.True(q.HasServer, q.ServerText);
                q.SearchText = "islander";
                q.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                q.OpenChainCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.Equal(7, q.Chain!.Nodes.Count);
                // a quest giver at Sentinel Hill
                q.NpcName = "Drill Sergeant Halvor";
                q.NpcSubname = "Drill Sergeant";
                q.NpcCopyFrom = "821";
                q.NpcMap = "0";
                q.NpcX = "-10508";
                q.NpcY = "1053";
                q.MakeNpcCommand.Execute(null);
                Assert.False(q.StatusIsError, q.Status);
                // the template
                q.TplName = "Riposte Drill";
                q.TplFaction = "Alliance";
                q.TplGiver = "900000";
                q.TplIntro = "234";
                q.TplOutpost = "Sentinel Hill";
                q.TplKill = "122";
                q.TplCount = "8";
                q.TplCloneSpell = "12295";
                q.TplXp = "1450";
                q.MakeTemplateCommand.Execute(null);
                Assert.False(q.StatusIsError, q.Status);
                long drill = q.Chain!.Nodes.Single(n => n.Title == "Riposte Drill").Id;
                q.OpenQuest(drill);
                // a field through the form, a choice branch
                var level = q.Groups.SelectMany(g => g.Fields).First(f => f.F.Id == "level");
                level.Text = "21";
                level.Commit();
                Assert.False(q.StatusIsError, q.Status);
                var races = q.Groups.SelectMany(g => g.Fields).First(f => f.F.Id == "races");
                Assert.Contains(races.Bits, b => b.Name == "Human" && b.IsOn);
                q.BranchTitles = "The Sword Path\nThe Axe Path";
                q.BranchChoice = true;
                q.AddBranchCommand.Execute(null);
                Assert.False(q.StatusIsError, q.Status);
                Assert.True(q.Chain!.Groups.Single().IsChoice);
                q.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.False(q.StatusIsError, q.Status);
            });
            Assert.Equal(0, cli.Run("quests", "npc", "Drill Sergeant Halvor", "--copy-from", "821", "--subname", "Drill Sergeant", "--map", "0", "--x", "-10508", "--y", "1053").Code);
            var tpl = cli.Json("quests", "template", "Riposte Drill", "--faction", "Alliance", "--giver", "900000", "--intro-from", "234", "--outpost", "Sentinel Hill", "--kill", "122:8", "--clone-spell", "12295", "--xp", "1450");
            Assert.True(tpl.Code == 0, tpl.ToString());
            long cdrill = tpl.Data.GetProperty("data").GetProperty("quests")[1].GetInt64();
            Assert.Equal(0, cli.Run("quests", "set", cdrill.ToString(), "level", "21").Code);
            Assert.Equal(0, cli.Run("quests", "branch", cdrill.ToString(), "--titles", "The Sword Path|The Axe Path").Code);
            var gw = new Workspace(gui.Paths);
            var cw = new Workspace(cli.Paths);
            var gs = ServerFiles(gw);
            var cs = ServerFiles(cw);
            Assert.Equal(gs.Keys.OrderBy(k => k), cs.Keys.OrderBy(k => k));
            Assert.Contains("quest_template", gs.Keys);
            foreach (var k in gs.Keys) Assert.True(gs[k] == cs[k], $"{k} differs:\nGUI {gs[k]}\nCLI {cs[k]}");
            var gameFiles = gw.ModifiedPaths().Where(p => !ServerChangeFiles.IsServerPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(gameFiles, cw.ModifiedPaths().Where(p => !ServerChangeFiles.IsServerPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(@"DBFilesClient\Spell.dbc", gameFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var p in gameFiles) Assert.True(gw.ReadEdited(p)!.AsSpan().SequenceEqual(cw.ReadEdited(p)), p);
        }
        finally
        {
            foreach (var d in new[] { gdb, cdb })
                try { ServerTestDb.Exec(d, $"DROP DATABASE `{d.Database}`"); }
                catch (MySqlConnector.MySqlException) { }
        }
    }
}
