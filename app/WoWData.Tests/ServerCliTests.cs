using System.IO;
using System.Linq;
using WoWData.Cli;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>
/// 0.9.0: "wds server" against the isolated throwaway world database (ServerTestDb) and a fake game folder (CliTestEnv):
/// every command of the group, its exit codes (0, 2, 3, 4, 5, 6) and its --json, and that the app's Server data tab and
/// the command line make byte-identical workspace files.
/// </summary>
[Collection(IndexCollection.Name)]
public class ServerCliTests
{
    /// <summary>Point an isolated home's settings at a throwaway database (before any session opens).</summary>
    public static void UseServer(CliTestEnv env, ServerDbSettings db)
    {
        ServerTestDb.AssertIsolated(db);
        var paths = new AppPaths(env.Home);
        var s = AppSettings.Load(paths);
        s.Server = db.Clone();
        s.Save(paths);
    }

    [IsolatedDbFact]
    public void EveryServerCommandWithItsExitCodes()
    {
        using var env = new CliTestEnv("server");
        var db = ServerTestDb.NewDatabase("cli");
        UseServer(env, db);
        try
        {
            var status = env.Json("server", "status");
            Assert.True(status.Code == 0, status.ToString());
            Assert.True(status.Data.GetProperty("vmangos").GetBoolean());
            Assert.Equal(db.Database, status.Data.GetProperty("database").GetString());
            Assert.Equal(10, status.Data.GetProperty("patch").GetInt32());
            Assert.True(env.Json("server", "views").Data.GetProperty("views").GetArrayLength() >= 15);

            // find, show, get
            var find = env.Json("server", "find", "items", "worn", "shortsword");
            Assert.Contains(find.Data.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetInt64() == 25);
            Assert.Equal(ExitCode.NotFound, env.Run("server", "find", "nosuchview").Code);
            var show = env.Json("server", "show", "creature_template", "3098");
            Assert.Equal("Mottled Boar", show.Data.GetProperty("title").GetString());
            Assert.Contains(show.Data.GetProperty("sections").EnumerateArray(), s => s.GetProperty("title").GetString() == "Spawns" && s.GetProperty("rows").GetArrayLength() > 20);
            Assert.Equal(ExitCode.NotFound, env.Run("server", "show", "no_such_table", "1").Code);
            Assert.Equal(ExitCode.NotFound, env.Run("server", "show", "item_template", "16000000").Code);
            Assert.Equal("Worn Shortsword", env.Json("server", "get", "item_template", "25", "name").Data.GetProperty("value").GetString());

            // set: refused (3), dry run (nothing saved), saved
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "set", "item_template", "25", "dmg_max1", "abc").Code);
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "set", "item_template", "25", "required_level", "300").Code);
            var dry = env.Json("server", "set", "item_template", "25", "dmg_max1", "12", "--dry-run");
            Assert.False(dry.Data.GetProperty("saved").GetBoolean());
            Assert.Empty(new Workspace(env.Paths).List());
            var set = env.Json("server", "set", "item_template", "25", "--set", "dmg_max1=12", "--set", "quality=Uncommon");
            Assert.True(set.Data.GetProperty("saved").GetBoolean(), set.ToString());
            Assert.Equal("12", env.Json("server", "get", "item_template", "25", "dmg_max1", "--raw").Data.GetProperty("value").GetString());
            var rec = new Workspace(env.Paths).Get(ServerChangeFiles.PathOf(db.Database, "item_template"))!;
            Assert.Equal("server", rec.Kind);
            // a row rule the server would refuse: level_min above level_max (exit 3, nothing saved)
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "set", "creature_template", "3098", "level_min", "50").Code);

            // add, duplicate, delete (needs --yes), bulk
            var dup = env.Json("server", "duplicate", "creature_template", "3158", "--set", "name=Grokk");
            Assert.True(dup.Code == 0, dup.ToString());
            Assert.Equal("entry=900000;patch=0", dup.Data.GetProperty("change").GetProperty("key").GetString());
            var add = env.Json("server", "add", "npc_vendor", "--set", "entry=900000", "--set", "item=25");
            Assert.True(add.Code == 0, add.ToString());
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "add", "npc_vendor", "--set", "entry=900000", "--set", "item=25").Code);   // the same key twice
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "add", "npc_vendor", "--set", "entry=900000", "--set", "item=16000000").Code);   // no such item
            Assert.Equal(ExitCode.NeedsYes, env.Run("server", "delete", "npc_vendor", "entry=3158,item=159").Code);
            var del = env.Json("server", "delete", "npc_vendor", "entry=3158,item=159", "--yes");
            Assert.True(del.Code == 0, del.ToString());
            Assert.Equal(ExitCode.Usage, env.Run("server", "bulk", "npc_vendor", "--column", "maxcount", "--op", "set", "--value", "1").Code);   // no rows chosen
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "bulk", "npc_vendor", "--where", "entry=3158", "--column", "maxcount", "--op", "set", "--value", "300").Code);
            var bulk = env.Json("server", "bulk", "npc_vendor", "--where", "entry=3158", "--column", "maxcount", "--op", "set", "--value", "5");
            Assert.True(bulk.Data.GetProperty("change").GetProperty("changed").GetInt32() >= 5, bulk.ToString());
            Assert.Contains(env.Json("server", "used-by", "item_template", "159").Data.GetProperty("usedBy").EnumerateArray(), u => u.GetProperty("table").GetString() == "npc_vendor");
            var changes = env.Json("server", "changes");
            Assert.True(changes.Data.GetArrayLength() >= 3);

            // client / server: a spell edited on the server differs from the game; make the game match
            Assert.Equal(0, env.Json("server", "check", "spells", "--ids", "133").Data.GetProperty("issues").GetArrayLength());
            Assert.Equal(0, env.Run("server", "set", "spell_template", "133", "manaCost", "25").Code);
            Assert.Equal(1, env.Json("server", "check", "spells", "--ids", "133").Data.GetProperty("issues").GetArrayLength());
            Assert.Equal(ExitCode.Usage, env.Run("server", "match", "spells", "133").Code);   // --to is required
            var match = env.Json("server", "match", "spells", "133", "--to", "client");
            Assert.True(match.Code == 0, match.ToString());
            Assert.True(new Workspace(env.Paths).IsModified(@"DBFilesClient\Spell.dbc"));
            Assert.Equal(0, env.Json("server", "check", "spells", "--ids", "133").Data.GetProperty("issues").GetArrayLength());
            Assert.Equal(ExitCode.NotFound, env.Run("server", "check", "nosucharea").Code);

            // the SQL patch and its revert
            var sql = Path.Combine(env.Work, "patch.sql");
            Assert.Equal(0, env.Run("server", "export-sql", "--out", sql).Code);
            Assert.Contains("DELETE FROM `item_template` WHERE `entry`=25 AND `patch`=0;", File.ReadAllText(sql));
            Assert.True(File.Exists(Path.Combine(env.Work, "patch.revert.sql")));

            // apply: needs --yes (5); then applied, logged; undo: needs --yes, then the server's rows are back
            var before = ServerTestDb.Checksums(db, "item_template", "creature_template", "npc_vendor", "spell_template");
            var plan = env.Json("server", "apply", "--dry-run", "--show-sql");
            Assert.True(plan.Data.GetProperty("statements").GetArrayLength() > 5);
            Assert.Equal(ExitCode.NeedsYes, env.Run("server", "apply").Code);
            var applied = env.Json("server", "apply", "--yes");
            Assert.True(applied.Code == 0, applied.ToString());
            Assert.True(applied.Data.GetProperty("applied").GetBoolean());
            Assert.Equal(12, Convert.ToInt32(ServerTestDb.Scalar(db, "SELECT dmg_max1 FROM item_template WHERE entry=25")));
            Assert.Equal(1, env.Json("server", "applies").Data.GetArrayLength());
            Assert.Contains("applied to the server", new Workspace(env.Paths).View(new Workspace(env.Paths).Get(ServerChangeFiles.PathOf(db.Database, "item_template"))!).StatusText);
            // the server changes a row afterwards: undo is refused (4) unless --overwrite
            ServerTestDb.Exec(db, "UPDATE npc_vendor SET incrtime=60 WHERE entry=900000");
            Assert.Equal(ExitCode.Refused, env.Run("server", "undo-apply", "--yes").Code);
            ServerTestDb.Exec(db, "UPDATE npc_vendor SET incrtime=0 WHERE entry=900000");
            Assert.Equal(ExitCode.NeedsYes, env.Run("server", "undo-apply").Code);
            Assert.Equal(0, env.Run("server", "undo-apply", "--yes").Code);
            Assert.Equal(before, ServerTestDb.Checksums(db, "item_template", "creature_template", "npc_vendor", "spell_template"));
            Assert.False(env.Json("server", "undo-apply").Data.GetProperty("undone").GetBoolean());   // nothing left to undo

            // CSV / JSON
            var csv = Path.Combine(env.Work, "duokna.csv");
            Assert.Equal(0, env.Run("server", "export", "npc_vendor", "--where", "entry=3158", "--out", csv).Code);
            var same = env.Json("server", "import", "npc_vendor", csv);
            Assert.Equal(0, same.Data.GetProperty("preview").GetProperty("changed").GetInt32());
            var lines = File.ReadAllLines(csv).ToList();
            lines.Add("3158,99,117,0,0,0,0");
            File.WriteAllLines(csv, lines);
            var preview = env.Json("server", "import", "npc_vendor", csv);
            Assert.Equal(1, preview.Data.GetProperty("preview").GetProperty("added").GetInt32());
            Assert.False(preview.Data.GetProperty("applied").GetBoolean());
            var imported = env.Json("server", "import", "npc_vendor", csv, "--apply");
            Assert.True(imported.Data.GetProperty("saved").GetBoolean(), imported.ToString());
            File.WriteAllLines(csv, new[] { "entry,slot,item,maxcount", "3158,1,159,abc" });
            Assert.Equal(ExitCode.ValidationFailed, env.Run("server", "import", "npc_vendor", csv).Code);
            var json = Path.Combine(env.Work, "boar.json");
            Assert.Equal(0, env.Run("server", "export", "creature_template", "--where", "entry=3098", "--out", json).Code);
            Assert.Equal(0, env.Json("server", "import", "creature_template", json).Data.GetProperty("preview").GetProperty("changed").GetInt32());

            // the game's cache (the fake game folder): dry run, needs --yes, moved to the backups
            Directory.CreateDirectory(Path.Combine(env.Client, "WDB"));
            File.WriteAllText(Path.Combine(env.Client, "WDB", "itemcache.wdb"), "cache");
            Assert.Equal("itemcache.wdb", env.Json("server", "clear-wdb", "--dry-run").Data.GetProperty("files")[0].GetString());
            bool gameRuns = WdbCache.RunningGames(env.Client).Count > 0;   // a game whose folder cannot be read counts (safety)
            Assert.Equal(gameRuns ? ExitCode.Refused : ExitCode.NeedsYes, env.Run("server", "clear-wdb").Code);
            if (!gameRuns)
            {
                Assert.Equal(0, env.Run("server", "clear-wdb", "--yes").Code);
                Assert.False(File.Exists(Path.Combine(env.Client, "WDB", "itemcache.wdb")));
            }

            // server edits are not game files: no release patch from them alone
            var only = env.Json("changes", "install");
            Assert.NotEqual(0, only.Code);
        }
        finally
        {
            ServerTestDb.Exec(db, $"DROP DATABASE `{db.Database}`");
        }
    }

    [IsolatedDbFact]
    public void TheServerDataTabAndTheCommandLineMakeTheSameWorkspaceFiles()
    {
        using var gui = new CliTestEnv("server_gui");
        using var cli = new CliTestEnv("server_cli");
        var db = ServerTestDb.NewDatabase("parity");   // one database for both: the same "before" rows
        UseServer(gui, db);
        UseServer(cli, db);
        try
        {
            // ---- the app: the Server data tab's view model
            UiTestsHelper.RunSta(() =>
            {
                var paths = new AppPaths(gui.Home);
                using var session = new AppSession(paths, AppSettings.Load(paths));
                session.OpenAsync(gui.Client, false, null).GetAwaiter().GetResult();
                session.ServerRunningProbe = () => new List<string>();
                var main = new MainViewModel(session);
                var browser = new BrowserViewModel(session, main);
                var s = browser.Server;
                s.EnsureLoadedAsync().GetAwaiter().GetResult();
                Assert.True(s.IsConnected, s.ConnectionText);
                s.OpenRecordAsync("item_template", "entry=25;patch=0", false).GetAwaiter().GetResult();
                Assert.True(s.TryEdit("dmg_max1", "12")!.Ok);
                Assert.True(s.TryEdit("quality", "Uncommon")!.Ok);
                Assert.True(s.SaveCore(interactive: false).GetAwaiter().GetResult(), s.Status);
                s.OpenRecordAsync("creature_template", "entry=3158;patch=0", false).GetAwaiter().GetResult();
                s.DuplicateAsNewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.Equal("entry=900000;patch=0", s.CurrentKey);
                Assert.True(s.TryEdit("name", "Grokk")!.Ok);
                Assert.True(s.SaveCore(interactive: false).GetAwaiter().GetResult(), s.Status);
            });

            // ---- the command line
            Assert.Equal(0, cli.Run("server", "set", "item_template", "25", "--set", "dmg_max1=12", "--set", "quality=Uncommon").Code);
            Assert.Equal(0, cli.Run("server", "duplicate", "creature_template", "3158", "--set", "name=Grokk").Code);

            // ---- identical workspace files and records
            var gw = new Workspace(gui.Paths);
            var cw = new Workspace(cli.Paths);
            var files = new[] { ServerChangeFiles.PathOf(db.Database, "item_template"), ServerChangeFiles.PathOf(db.Database, "creature_template") };
            Assert.Equal(files.OrderBy(f => f), gw.ModifiedPaths().OrderBy(f => f));
            Assert.Equal(files.OrderBy(f => f), cw.ModifiedPaths().OrderBy(f => f));
            foreach (var f in files)
            {
                Assert.Equal(gw.ReadEdited(f), cw.ReadEdited(f));
                Assert.Equal(gw.Get(f)!.Summary, cw.Get(f)!.Summary);
                Assert.Equal(gw.Get(f)!.Kind, cw.Get(f)!.Kind);
            }
            // and the same SQL patch
            var gs = ServerSql.Build(new ServerDataService(db, gw, null, new AppSettings { Server = db }, null, gui.Paths.Dir), new ServerDataService(db, gw, null, new AppSettings { Server = db }, null, gui.Paths.Dir).SavedChanges(), new DateTime(2026, 1, 1));
            var cs = ServerSql.Build(new ServerDataService(db, cw, null, new AppSettings { Server = db }, null, cli.Paths.Dir), new ServerDataService(db, cw, null, new AppSettings { Server = db }, null, cli.Paths.Dir).SavedChanges(), new DateTime(2026, 1, 1));
            Assert.Equal(gs.Patch, cs.Patch);
            Assert.Equal(gs.Revert, cs.Revert);
        }
        finally
        {
            ServerTestDb.Exec(db, $"DROP DATABASE `{db.Database}`");
        }
    }
}
