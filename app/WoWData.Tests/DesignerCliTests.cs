using System.IO;
using System.Linq;
using WoWData.Cli;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>
/// 0.12.0: "wds spells", "wds talents" and "wds classes" in an isolated home and fake game folder (CliTestEnv): every
/// command, its exit codes (0, 2, 3, 5, 6) and its --json; with the throwaway database (ServerTestDb) the server side; and
/// the app's designer tabs (their view models, headless) and the command line making byte-identical workspace files.
/// </summary>
[Collection(IndexCollection.Name)]
public class DesignerCliTests
{
    [Fact]
    public void EveryDesignerCommandWithItsExitCodes()
    {
        using var env = new CliTestEnv("designers");
        // spells: find, fields, explain, show, tooltip, icons
        var find = env.Json("spells", "find", "shadow", "bolt");
        Assert.True(find.Code == 0, find.ToString());
        Assert.Contains(find.Data.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetInt64() == 686);
        Assert.Equal(ExitCode.NotFound, env.Run("spells", "find", "zzznosuchspellzzz").Code);
        Assert.True(env.Json("spells", "fields").Data.GetArrayLength() > 100);
        Assert.Equal(ExitCode.Usage, env.Run("spells", "explain", "banana").Code);
        Assert.Contains("Apply aura", env.Json("spells", "explain", "effect", "6").Data[0].GetProperty("name").GetString());
        var show = env.Json("spells", "show", "686");
        Assert.True(show.Code == 0, show.ToString());
        Assert.Equal("Sends a shadowy bolt at the enemy, causing 12 to 16 Shadow damage.", show.Data.GetProperty("tooltip").GetString());
        Assert.Equal(ExitCode.NotFound, env.Run("spells", "show", "99999999").Code);
        Assert.Equal(ExitCode.Usage, env.Run("spells", "show", "abc").Code);
        Assert.Equal("Corrupts the target, causing 40 Shadow damage over 12 sec.", env.Json("spells", "tooltip", "172").Data.GetProperty("text").GetString());
        Assert.Contains(env.Json("spells", "icons", "shadowbolt").Data.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetInt64() == 213);

        // clone (dry run: nothing saved), clone, set, check, refusals
        var dry = env.Json("spells", "clone", "686", "--name", "Shadow Lance", "--dry-run");
        Assert.False(dry.Data.GetProperty("saved").GetBoolean());
        Assert.Empty(new Workspace(env.Paths).List());
        var clone = env.Json("spells", "clone", "686", "--name", "Shadow Lance", "--rank", "Rank 1", "--set", "cast-time=2", "--set", "effect1-value=40-48", "--set", "description=Deals $s1 Shadow damage.");
        Assert.True(clone.Code == 0, clone.ToString());
        Assert.Equal(35000, clone.Data.GetProperty("data").GetProperty("id").GetInt64());
        Assert.Equal("Deals 40 to 48 Shadow damage.", clone.Data.GetProperty("data").GetProperty("tooltip").GetString());
        Assert.True(clone.Data.GetProperty("serverSkipped").GetBoolean());
        Assert.Equal(ExitCode.ValidationFailed, env.Run("spells", "set", "35000", "school", "Banana").Code);
        Assert.Equal(ExitCode.Usage, env.Run("spells", "set", "35000", "no-such-field", "1").Code);
        Assert.Equal(ExitCode.ValidationFailed, env.Run("spells", "set", "35000", "effect1", "Apply aura").Code);   // refused: an aura without a type
        Assert.Equal(0, env.Run("spells", "set", "35000", "--set", "cooldown=6", "--set", "range=35").Code);
        Assert.Equal(0, env.Json("spells", "check", "35000").Code);
        var newSpell = env.Json("spells", "new", "Empty Thing");
        Assert.Equal(35001, newSpell.Data.GetProperty("data").GetProperty("id").GetInt64());

        // visual and learning (client side)
        var vis = env.Json("spells", "visual", "35000", "--missile", "spell:133");
        Assert.True(vis.Code == 0, vis.ToString());
        Assert.Equal(ExitCode.Usage, env.Run("spells", "visual", "35000", "--tint", "#ff0000").Code);   // --tint goes with --duplicate
        Assert.Equal(ExitCode.ValidationFailed, env.Run("spells", "visual", "35000", "--cast", "banana").Code);
        var learn = env.Json("spells", "learn", "35000", "--skill-line", "593", "--class", "Warlock");
        Assert.True(learn.Code == 0, learn.ToString());
        Assert.Equal(0x100, learn.Data.GetProperty("data").GetProperty("skillLines")[0].GetProperty("classMask").GetInt64());
        Assert.Equal(ExitCode.Error, env.Run("spells", "learn", "35000", "--trainer", "3156").Code);   // trainers need the server
        Assert.Equal(ExitCode.Error, env.Run("spells", "sync", "35000").Code);

        // 0.13.3: "Change ID" for a spell with an ID the server never casts (as 0.12.0-0.13.2 made them), and the setting
        Assert.Equal(0, env.Run("dbc", "duplicate", "Spell", "686", "--id", "900005").Code);
        var bad = env.Json("spells", "check", "900005");
        Assert.Contains(bad.Data[0].GetProperty("problems").EnumerateArray(), p => p.GetProperty("field").GetString() == "ID");
        var dryAll = env.Json("spells", "renumber", "--all", "--dry-run");
        Assert.True(dryAll.Code == 0, dryAll.ToString());
        Assert.False(dryAll.Data.GetProperty("saved").GetBoolean());
        Assert.Equal(900005, dryAll.Data.GetProperty("data").GetProperty("unusableBefore")[0].GetInt64());
        Assert.Equal(ExitCode.ValidationFailed, env.Run("spells", "renumber", "686").Code);                   // the game's own spell
        Assert.Equal(ExitCode.ValidationFailed, env.Run("spells", "renumber", "900005", "--to", "40001").Code);
        Assert.Equal(ExitCode.Usage, env.Run("spells", "renumber").Code);
        var ren = env.Json("spells", "renumber", "900005", "--to", "35100");
        Assert.True(ren.Code == 0, ren.ToString());
        Assert.Contains("is now spell 35100 (was 900005)", ren.Data.GetProperty("data").GetProperty("message").GetString());
        Assert.Equal(0, ren.Data.GetProperty("data").GetProperty("unusableAfter").GetArrayLength());
        Assert.Equal(ExitCode.NotFound, env.Run("spells", "show", "900005").Code);
        Assert.Equal(0, env.Run("spells", "show", "35100").Code);
        Assert.NotEqual(0, env.Run("settings", "set", "spell-ids", "35000-50000").Code);
        Assert.Equal(0, env.Run("settings", "set", "spell-ids", "36000-36999").Code);
        Assert.Equal(36000, env.Json("spells", "new", "Ranged Thing", "--dry-run").Data.GetProperty("data").GetProperty("id").GetInt64());
        Assert.Equal(0, env.Run("settings", "unset", "spell-ids").Code);

        // talents
        var trees = env.Json("talents", "trees", "warrior");
        Assert.Equal(3, trees.Data.GetProperty("trees").GetArrayLength());
        var addTree = env.Json("talents", "add-tree", "warrior", "Stance Disciplines", "--icon", "Ability_Warrior_DefensiveStance", "--background", "WarriorProtection");
        Assert.True(addTree.Code == 0, addTree.ToString());
        long tab = addTree.Data.GetProperty("data").GetProperty("id").GetInt64();
        var add = env.Json("talents", "add", tab.ToString(), "--tier", "0", "--column", "0", "--make-ranks", "12295", "--name", "Riposte Drill", "--count", "1");
        Assert.True(add.Code == 0, add.ToString());
        long tal = add.Data.GetProperty("data").GetProperty("talents")[0].GetProperty("id").GetInt64();
        Assert.Equal(ExitCode.ValidationFailed, env.Run("talents", "add", tab.ToString(), "--tier", "9", "--column", "0", "--ranks", "686").Code);
        Assert.Equal(ExitCode.Usage, env.Run("talents", "add", tab.ToString(), "--tier", "1").Code);
        Assert.Equal(0, env.Run("talents", "set", tal.ToString(), "--column", "1").Code);
        Assert.Contains("Riposte Drill", env.Run("talents", "show", tab.ToString()).Out);
        Assert.Equal(0, env.Run("talents", "check", "warrior").Code);
        Assert.Equal(ExitCode.NeedsYes, env.Run("talents", "remove", tal.ToString()).Code);
        Assert.Equal(0, env.Run("talents", "remove", tal.ToString(), "--yes").Code);
        Assert.Equal(ExitCode.NeedsYes, env.Run("talents", "remove-tree", tab.ToString()).Code);
        Assert.Equal(ExitCode.NotFound, env.Run("talents", "show", "99999").Code);

        // classes
        Assert.Equal(9, env.Json("classes", "list").Data.GetArrayLength());
        var cls = env.Json("classes", "show", "warlock", "--level", "1-4");
        Assert.Contains(cls.Data.GetProperty("spells").EnumerateArray(), s => s.GetProperty("spell").GetInt64() == 686);
        Assert.Equal(ExitCode.NotFound, env.Run("classes", "show", "banana").Code);
        Assert.Equal(0, env.Run("classes", "assign", "warlock", "35000", "--level", "20").Code);
        Assert.Equal(ExitCode.Usage, env.Run("classes", "mask", "SkillLineAbility", "1", "--class", "1").Code);   // --add or --remove
        Assert.Equal(ExitCode.Usage, env.Run("classes", "mask", "Spell", "133", "--class", "1", "--add").Code);
        var nc = env.Json("classes", "new", "Knight", "--token", "KNIGHT", "--copy-from", "Paladin", "--races", "Human", "--dry-run");
        Assert.True(nc.Code == 0, nc.ToString());
        Assert.False(nc.Data.GetProperty("saved").GetBoolean());
        Assert.Contains(nc.Warnings(), w => w.Contains("EXPERIMENTAL"));
        var report = env.Json("classes", "engine-report", "6");
        Assert.Contains(report.Data.EnumerateArray(), e => e.GetProperty("where").GetString()!.Contains("SharedDefines.h"));
        // the out-of-process program: real exit codes
        Assert.Equal(ExitCode.NeedsYes, env.RunExe("talents", "remove-tree", tab.ToString()).Code);
    }

    [IsolatedDbFact]
    public void TrainersAndSyncThroughTheCommandLine()
    {
        using var env = new CliTestEnv("designers_server");
        var db = ServerTestDb.NewDatabase("designcli");
        ServerCliTests.UseServer(env, db);
        ServerTestDb.Exec(db, "UPDATE creature_template SET npc_flags = npc_flags | 16, trainer_type = 0, trainer_class = 9, trainer_id = 0 WHERE entry = 3156");
        try
        {
            var clone = env.Json("spells", "clone", "686", "--name", "Shadow Lance");
            Assert.True(clone.Code == 0, clone.ToString());
            Assert.Contains("spell_template", clone.Data.GetProperty("serverTables").EnumerateArray().Select(x => x.GetString()));
            var learn = env.Json("spells", "learn", "35000", "--trainer", "3156", "--level", "20", "--cost", "25s");
            Assert.True(learn.Code == 0, learn.ToString());
            Assert.Equal(2500, learn.Data.GetProperty("data").GetProperty("trainers")[0].GetProperty("cost").GetInt64());
            Assert.Equal(ExitCode.NotFound, env.Run("spells", "learn", "35000", "--trainer", "99999999").Code);
            Assert.Equal(0, env.Json("server", "check", "spells", "--ids", "35000,35001").Data.GetProperty("issues").GetArrayLength());
            // the game's row edited alone (Data tables): the server differs until "spells sync"
            Assert.Equal(0, env.Run("dbc", "set", "Spell", "35000", "ManaCost", "99").Code);
            Assert.Equal(1, env.Json("server", "check", "spells", "--ids", "35000").Data.GetProperty("issues").GetArrayLength());
            Assert.Equal(0, env.Run("spells", "sync", "35000").Code);
            Assert.Equal(0, env.Json("server", "check", "spells", "--ids", "35000").Data.GetProperty("issues").GetArrayLength());
            var show = env.Json("classes", "show", "warlock", "--source", "trainer");
            Assert.Contains(show.Data.GetProperty("spells").EnumerateArray(), s => s.GetProperty("spell").GetInt64() == 35000 && s.GetProperty("level").GetInt64() == 20);
            // nothing reached the database: only "server apply --yes" writes it
            Assert.Equal(0L, Convert.ToInt64(ServerTestDb.Scalar(db, "SELECT COUNT(*) FROM spell_template WHERE entry=35000")));
        }
        finally
        {
            try { ServerTestDb.Exec(db, $"DROP DATABASE `{db.Database}`"); }
            catch (MySqlConnector.MySqlException) { }
        }
    }

    [Fact]
    public void TheDesignerTabsAndTheCommandLineMakeTheSameFiles()
    {
        using var gui = new CliTestEnv("designer_gui");
        using var cli = new CliTestEnv("designer_cli");
        UiTestsHelper.RunSta(() =>
        {
            var paths = new AppPaths(gui.Home);
            using var session = new AppSession(paths, AppSettings.Load(paths));
            session.OpenAsync(gui.Client, false, null).GetAwaiter().GetResult();
            var main = new MainViewModel(session);
            var browser = new BrowserViewModel(session, main);
            var sp = browser.Spells;
            sp.SearchText = "686";
            sp.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(686, sp.SelectedResult!.Id);
            sp.NewName = "Shadow Lance";
            sp.NewRank = "Rank 1";
            sp.CloneCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(35000, sp.CurrentId);
            SpellFieldView F(string id) => sp.Groups.SelectMany(g => g.Fields).First(f => f.F.Id == id);
            sp.SetField(F("cast-time"), "2");
            sp.SetField(F("effect1-value"), "40-48");
            sp.SetField(F("icon"), "Spell_Shadow_ScourgeBuild");
            // a flags field's checkboxes: tick "Passive" in the attributes
            F("attributes").Flags.First(x => x.Name == "Passive").IsOn = true;
            Assert.False(sp.StatusIsError, sp.Status);
            Assert.Contains(F("attributes").Flags, x => x.Name == "Passive" && x.IsOn);
            Assert.Equal("Sends a shadowy bolt at the enemy, causing 40 to 48 Shadow damage.", sp.TooltipText);
            sp.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.False(sp.StatusIsError, sp.Status);
            // 0.13.3: "Change ID" (wds spells renumber --to)
            Assert.False(sp.HasUnusable);
            sp.NewId = "35100";
            sp.ChangeIdCommand.Execute(null);
            Assert.False(sp.StatusIsError, sp.Status);
            Assert.Equal(35100, sp.CurrentId);
            sp.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.False(sp.StatusIsError, sp.Status);
            var ct = browser.Classes;
            ct.EnsureLoadedAsync().GetAwaiter().GetResult();
            ct.SelectedClass = ct.Classes.First(c => c.Id == 1);
            ct.NewTreeName = "Stance Disciplines";
            ct.AddTreeCommand.Execute(null);
            ct.TreeIcon = "Ability_Warrior_DefensiveStance";
            ct.TreeBackground = "WarriorProtection";
            ct.ApplyTreeCommand.Execute(null);
            ct.SelectCellCommand.Execute(ct.Cells.First(c => c.Tier == 0 && c.Column == 0));
            ct.RankTemplate = "12295";
            ct.RankName = "Riposte Drill";
            ct.RankCount = "1";
            ct.MakeRankSpellsCommand.Execute(null);
            ct.AddTalentHereCommand.Execute(null);
            Assert.False(ct.StatusIsError, ct.Status);
            Assert.Single(ct.Cells.Where(c => c.HasTalent));
            ct.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.False(ct.StatusIsError, ct.Status);
        });
        Assert.Equal(0, cli.Run("spells", "clone", "686", "--name", "Shadow Lance", "--rank", "Rank 1", "--set", "cast-time=2", "--set", "effect1-value=40-48", "--set", "icon=Spell_Shadow_ScourgeBuild", "--set", "attributes=+Passive").Code);
        Assert.Equal(0, cli.Run("spells", "renumber", "35000", "--to", "35100").Code);
        var tree = cli.Json("talents", "add-tree", "warrior", "Stance Disciplines", "--icon", "Ability_Warrior_DefensiveStance", "--background", "WarriorProtection");
        long tab = tree.Data.GetProperty("data").GetProperty("id").GetInt64();
        Assert.Equal(0, cli.Run("talents", "add", tab.ToString(), "--tier", "0", "--column", "0", "--make-ranks", "12295", "--name", "Riposte Drill", "--count", "1").Code);
        var gw = new Workspace(gui.Paths);
        var cw = new Workspace(cli.Paths);
        var files = gw.ModifiedPaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(files, cw.ModifiedPaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(@"DBFilesClient\Spell.dbc", files, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"DBFilesClient\Talent.dbc", files, StringComparer.OrdinalIgnoreCase);
        foreach (var p in files) Assert.True(gw.ReadEdited(p)!.AsSpan().SequenceEqual(cw.ReadEdited(p)), p);
    }
}

public class SpellDesignerLoadTests
{
    /// <summary>0.13.3 (seen in the screenshot run): two loads at once (a tab switch and a command) opened two design
    /// sessions, and the designer of one could be paired with the session of the other; now one load serves both.</summary>
    [Fact]
    public void TwoLoadsAtOnceGiveOneSession()
    {
        using var gui = new CliTestEnv("designer_load");
        UiTestsHelper.RunSta(() =>
        {
            var paths = new AppPaths(gui.Home);
            using var session = new AppSession(paths, AppSettings.Load(paths));
            session.OpenAsync(gui.Client, false, null).GetAwaiter().GetResult();
            var main = new MainViewModel(session);
            var sp = new BrowserViewModel(session, main).Spells;
            var a = sp.EnsureLoadedAsync();
            var b = sp.EnsureLoadedAsync();
            Assert.Same(a, b);
            a.GetAwaiter().GetResult();
            Assert.NotNull(sp.Session);
            Assert.Same(sp.Session, sp.Designer!.Session);
            var first = sp.Session;
            sp.EnsureLoadedAsync().GetAwaiter().GetResult();
            Assert.Same(first, sp.Session);
        });
    }
}

internal static class CliRunExtensions
{
    public static IEnumerable<string> Warnings(this CliRun r) => r.Json.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!);
}
