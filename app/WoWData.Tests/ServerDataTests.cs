using System.IO;
using System.Linq;
using WoWData.Core.Dbc;
using WoWData.Core.Index;
using WoWData.Core.ServerData;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.9.0: the server data features in Core (WoWData.Core.ServerData), against the isolated throwaway world database
/// (ServerTestDb: its own mysqld, data folder and port, a fresh copy of the fixture per test) and the real client's data
/// tables (read only): the meaning of the tables, views, links and Used by, checks, editing with undo, the workspace
/// files and their row diff, the SQL patch (idempotent) and its revert (the original rows exactly), apply with backup, log
/// and undo, refusals, the client / server checks and "make them match", CSV / JSON and the game's cache.
/// </summary>
public class ServerDataTests
{
    /// <summary>One test's world: a fresh database copy, a temporary workspace, the game's tables.</summary>
    private sealed class Ctx : IDisposable
    {
        private readonly List<(string, long, DateTime)> _client = TestEnv.DataSnapshot();

        public Ctx(string name)
        {
            Db = ServerTestDb.NewDatabase(name);
            Dir = TestEnv.TempDir("server_" + name);
            Paths = new ClientPaths(Path.Combine(Dir, "client"), TestEnv.ClientDir);
            Directory.CreateDirectory(Paths.Dir);
            Ws = new Workspace(Paths);
            Dbc = new DbcService(TestEnv.Client, Ws);
            Settings = new AppSettings { Server = Db };
            Svc = new ServerDataService(Db, Ws, Dbc, Settings, null, Paths.Dir, () => new List<string>());
        }

        public string Dir { get; }
        public ServerDbSettings Db { get; }
        public ClientPaths Paths { get; }
        public Workspace Ws { get; }
        public DbcService Dbc { get; }
        public AppSettings Settings { get; }
        public ServerDataService Svc { get; }

        public ServerEditor Editor() => Svc.NewEditor();

        public void Dispose()
        {
            Assert.Equal(_client, TestEnv.DataSnapshot());   // the real game folder was only read
            try { ServerTestDb.Exec(Db, $"DROP DATABASE `{Db.Database}`"); }
            catch (MySqlConnector.MySqlException) { }
            try { Directory.Delete(Dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheLayoutsParseAndEveryLinkNamesAKnownKind()
    {
        var l = ServerLayouts.Default;
        Assert.True(l.Tables.Count >= 50, $"{l.Tables.Count} tables");
        foreach (var t in new[] { "item_template", "creature_template", "quest_template", "gameobject_template", "spell_template", "creature", "gameobject", "npc_vendor", "npc_trainer_template",
                     "creature_loot_template", "reference_loot_template", "area_template", "map_template", "taxi_nodes", "faction", "faction_template", "locales_item" })
            Assert.NotNull(l.Table(t));
        Assert.Equal(ServerVersioning.Patch, l.Table("item_template")!.Versioning);
        Assert.Equal(ServerVersioning.Build, l.Table("spell_template")!.Versioning);
        Assert.Equal(ServerVersioning.PatchRange, l.Table("creature")!.Versioning);
        Assert.Equal(".reload item_template", l.Table("item_template")!.Reload);
        Assert.Equal("restart", l.Table("spell_template")!.Reload);
        var q = l.Table("quest_template")!;
        Assert.Equal(ServerLinkMode.Sign, q.Column("ReqCreatureOrGOId1")!.Link!.Mode);
        Assert.Equal(("gameobject", 5L), q.Column("ReqCreatureOrGOId1")!.Link!.Resolve(-5));
        Assert.Equal(("creature", 5L), q.Column("ReqCreatureOrGOId1")!.Link!.Resolve(5));
        Assert.Equal(("quest", 7L), q.Column("PrevQuestId")!.Link!.Resolve(-7));
        Assert.Null(l.Table("creature_loot_template")!.Column("mincountOrRef")!.Link!.Resolve(3));
        Assert.Equal(("map", 0L), l.Table("creature")!.Column("map")!.Link!.Resolve(0));   // map 0 is a real map
        Assert.Equal(10, l.Table("item_template")!.Columns.Count(c => c.Name.StartsWith("stat_type", StringComparison.Ordinal)));
        Assert.Equal("Rare (blue)", l.Table("item_template")!.Column("quality")!.Values!.NameOf(3));
        Assert.True(l.Kind("creature_loot")!.IsList);
        Assert.Throws<FormatException>(() => ServerLayouts.Parse("[t]\nversion: patch\nx -> nosuchkind\n"));
    }

    [IsolatedDbFact]
    public void EveryDescribedColumnExistsInTheWorldSchemaAndValuesRoundTrip()
    {
        using var ctx = new Ctx("schema");
        var schema = ctx.Svc.Schema;
        Assert.True(schema.IsVmangos);
        Assert.Equal(176, schema.Tables.Count);
        Assert.StartsWith("2023", schema.Migration);
        var missing = new List<string>();
        foreach (var t in ctx.Svc.Layouts.Tables)
        {
            var wt = schema.Table(t.Name);
            if (wt == null) { missing.Add(t.Name + " (table)"); continue; }
            foreach (var c in t.Columns.Where(c => !wt.Has(c.Name))) missing.Add($"{t.Name}.{c.Name}");
        }
        Assert.True(missing.Count == 0, "described but not in the world database: " + string.Join(", ", missing));
        // keys from the schema: a primary key, a unique key (npc_trainer), or every column (item_display_info)
        Assert.Equal(new[] { "entry", "patch" }, schema.Require("item_template").Key);
        Assert.Equal(WorldKeyKind.Unique, schema.Require("npc_trainer").KeyKind);
        Assert.Equal(WorldKeyKind.AllColumns, schema.Require("item_display_info").KeyKind);
        // values: whole numbers, FLOATs (exact 32-bit), texts, NULL
        var it = schema.Require("item_template");
        var row = ctx.Svc.Db.Row(it, "entry=25;patch=0")!;
        Assert.Equal("Worn Shortsword", row[it.IndexOf("name")]);
        Assert.Equal("1542", row[it.IndexOf("display_id")]);
        var ct = schema.Require("creature_template");
        var boar = ctx.Svc.Db.Row(ct, "entry=3098;patch=0")!;
        Assert.Null(boar[ct.IndexOf("subname")]);
        var speed = ct.Column("speed_run")!;
        Assert.Equal(WorldType.Float, speed.Type);
        foreach (var f in new[] { 1.14286f, 0.857143f, 1e-7f, 123456.789f, -0.0f })
        {
            var canon = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Null(WorldValue.Normalize(speed, canon, out var c2));
            Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(float.Parse(WorldValue.Sql(speed, c2), System.Globalization.CultureInfo.InvariantCulture)));
        }
        Assert.Equal("'O\\'Brien\\\\x\\n'", WorldValue.Quote("O'Brien\\x\n"));
    }

    [IsolatedDbFact]
    public void ViewsListRecordsWithTheirRelatedRowsLinksAndUsedBy()
    {
        using var ctx = new Ctx("views");
        var ed = ctx.Editor();
        var b = new ServerBrowser(ed);
        // lists: by words, by ID, one line per object (the version the server uses)
        var swords = b.Search("items", "worn shortsword");
        Assert.Contains(swords, i => i.Id == 25 && i.Name == "Worn Shortsword");
        var boar = Assert.Single(b.Search("creatures", "3098"));
        Assert.Equal("entry=3098;patch=7", boar.Key);   // patches 0 and 7: the server (patch 10) uses 7
        Assert.Contains("2 versions", boar.Info);
        Assert.Contains(b.Search("vendors", "duokna"), i => i.Id == 3158);
        Assert.Contains(b.Search("trainers", ""), i => i.Id == 3153);
        Assert.Contains(b.Search("quests", "place in the world"), i => i.Id == 4641);
        Assert.Contains(b.Search("loot", "3098", "creature_loot_template"), i => i.Id == 3098 && i.Name.Contains("Mottled Boar"));
        Assert.Contains(b.Search("locales", "Mottled", "locales_creature"), i => i.Id == 3098);
        Assert.NotEmpty(b.Search("tables", "", "map_template"));
        Assert.Throws<ServerDataException>(() => b.Search("nosuchview", ""));

        // a creature: its values with names and links, its spawns, loot, the server's display facts, Used by
        var rec = b.Record("creature_template", "entry=3098;patch=7");
        Assert.Equal("Mottled Boar", rec.Title);
        Assert.Equal(2, rec.Versions.Count);
        Assert.Equal("creature:3098", rec.BrowseKey);
        var display = rec.Fields.First(f => f.Name == "display_id1");
        Assert.Equal("dbc:CreatureDisplayInfo", display.LinkTarget);
        Assert.Equal(503, display.LinkId);
        Assert.Contains("BoarSkinIvory", display.Display);
        Assert.Contains("Beast", rec.Fields.First(f => f.Name == "type").Display);
        Assert.All(rec.Fields, f => Assert.True(f.Described, f.Name));
        // a table WoW Data Studio does not describe: every column shown as stored, with a hint
        var mig = b.Search("tables", "", "migrations", 1).First();
        var raw = b.Record("migrations", mig.Key);
        Assert.All(raw.Fields, f => Assert.False(f.Described));
        Assert.Contains("does not describe this column yet", raw.Fields[0].Hint);
        Assert.True(rec.Sections.First(s => s.Title == "Spawns").Rows.Count > 20);
        Assert.Contains(rec.Sections.First(s => s.Title == "Loot when killed").RowTexts, t => t.Contains("Tough Jerky"));
        Assert.Contains(rec.Sections, s => s.Title.StartsWith("The server's facts about its looks") && s.Rows.Count == 1);
        Assert.Contains(rec.UsedBy, u => u.Table == "creature" && u.Column == "id");
        // a vendor sells, a trainer trains (from its shared template)
        Assert.Equal(6, b.Record("creature_template", "entry=3158;patch=0").Sections.First(s => s.Title == "Sells").Rows.Count);
        Assert.Equal(7, b.Record("creature_template", "entry=3153;patch=0").Sections.First(s => s.Title.StartsWith("Trains (shared")).Rows.Count);
        // a quest: who starts and ends it, its chain
        var q = b.Record("quest_template", "entry=4641;patch=0");
        Assert.Contains(q.Sections.First(s => s.Title == "Started by creatures").RowTexts, t => t.Contains("Kaltunk"));
        Assert.Contains(q.Sections.First(s => s.Title == "Ended by creatures").RowTexts, t => t.Contains("Gornek"));
        Assert.Contains(q.Fields, f => f.Name == "NextQuestInChain" && f.Display.Contains("Cutting Teeth"));
        Assert.Contains(b.Record("quest_template", "entry=788;patch=0").Sections.First(s => s.Title.StartsWith("Comes before")).RowTexts, t => t.Contains("Your Place In The World"));
        // an item: its icon from the game's tables, who sells it, the server's copy of its display
        var item = b.Record("item_template", "entry=25;patch=0");
        Assert.Equal(@"Interface\Icons\INV_Sword_04.blp", item.Icon);
        Assert.Equal("item:25", item.BrowseKey);
        Assert.Single(item.Sections.First(s => s.Title.StartsWith("The server's copy of its display")).Rows);
        // a spell: the game's Spell.dbc row next to it
        var spell = b.Record("spell_template", ed.ResolveKey(ed.Table("spell_template"), "133"));
        Assert.Equal("entry=133;build=5875", spell.Key);
        Assert.Equal("Spell", spell.ClientTable);
        Assert.True(spell.Client.Count > 100);
        Assert.All(spell.Client, c => Assert.True(c.Same, c.Text));
        Assert.Contains("restart", spell.ReloadText);
        // loot rows: a reference row's item is not an item
        var loot = ed.Table("creature_loot_template");
        var refRow = ed.Rows(loot, RowFilter.Eq("entry", 3098)).First(r => WorldValue.Long(r[loot.IndexOf("mincountOrRef")]) < 0);
        Assert.False(ServerValidation.LinkApplies(loot, refRow, "item"));
        // Used by: an item is sold by Duokna and dropped by creatures
        Assert.Contains(b.UsedBy("item", 159), u => u.Table == "npc_vendor");
        Assert.Contains(b.UsedBy("item", 117), u => u.Table == "creature_loot_template");
    }

    [IsolatedDbFact]
    public void ValuesAreCheckedAndRowRulesBlockSaving()
    {
        using var ctx = new Ctx("checks");
        var ed = ctx.Editor();
        const string Sword = "entry=25;patch=0";
        Assert.False(ed.Set("item_template", Sword, "required_level", "300").Ok);          // tinyint unsigned
        Assert.False(ed.Set("item_template", Sword, "dmg_max1", "abc").Ok);
        Assert.False(ed.Set("item_template", Sword, "name", new string('x', 256)).Ok);       // varchar(255)
        Assert.False(ed.Set("item_template", Sword, "name", WorldValue.NullText).Ok);       // NOT NULL
        Assert.False(ed.Set("item_template", Sword, "spellid_1", "16777000").Ok);           // a spell that does not exist
        Assert.False(ed.Set("item_template", Sword, "quality", "Legendaryish").Ok);         // not a name of the choice
        Assert.False(ed.Set("creature_template", "entry=3098;patch=7", "subname", "😀").Ok);   // utf8 (3 bytes) cannot store it
        Assert.False(ed.CanUndo);                                                              // refused values change nothing
        Assert.True(ed.Set("item_template", Sword, "quality", "Rare").Ok);                  // a choice by name
        Assert.Equal("3", ed.Current(ed.Table("item_template"), Sword)![ed.Table("item_template").IndexOf("quality")]);
        Assert.True(ed.Set("creature_template", "entry=3158;patch=0", "npc_flags", "Vendor|Repairs items").Ok);
        Assert.Equal("16388", ed.Current(ed.Table("creature_template"), "entry=3158;patch=0")![ed.Table("creature_template").IndexOf("npc_flags")]);
        Assert.True(ed.Set("item_template", Sword, "buy_price", "1g 2s 3c").Ok);
        Assert.Equal("10203", ed.Current(ed.Table("item_template"), Sword)![ed.Table("item_template").IndexOf("buy_price")]);
        var warn = ed.Set("item_template", Sword, "quality", "9");                           // an unknown choice: a warning
        Assert.True(warn.Ok);
        Assert.Contains(warn.Warnings, p => p.Column == "quality");
        Assert.True(ed.Set("creature_template", "entry=3098;patch=7", "subname", WorldValue.NullText).Ok);   // NULL allowed there
        // row rules: accepted while editing (with a warning), refused when saving
        var r = ed.Set("creature_template", "entry=3098;patch=7", "level_min", "50");
        Assert.True(r.Ok);
        Assert.Contains(r.Problems, p => p.Column == "level_min");
        var e = Assert.Throws<ServerDataException>(() => ed.Save());
        Assert.Contains("level_min is above level_max", e.Message);
        Assert.True(ed.Set("creature_template", "entry=3098;patch=7", "level_max", "52").Ok);
        Assert.True(ed.Set("creature_template", "entry=3098;patch=7", "display_id1", "16000000").Ok == false);   // not in the game's CreatureDisplayInfo
        var loot = ed.Table("creature_loot_template");
        var key = ed.Rows(loot, RowFilter.Eq("entry", 3098)).Select(loot.KeyText).First(k => k.Contains("item=117"));
        Assert.True(ed.Set(loot.Name, key, "ChanceOrQuestChance", "150").Ok);
        Assert.Contains("percentage", Assert.Throws<ServerDataException>(() => ed.Save()).Message);
        Assert.True(ed.Set(loot.Name, key, new[] { ("ChanceOrQuestChance", (string?)"20"), ("mincountOrRef", "0") }).Ok);
        Assert.Contains("mincountOrRef 0", Assert.Throws<ServerDataException>(() => ed.Save()).Message);
        Assert.True(ed.Set(loot.Name, key, "mincountOrRef", "1").Ok);
        var (recs, _) = ed.Save();
        Assert.Equal(3, recs.Count);
        // the patch columns: 0..10
        Assert.False(ed.Set("creature", ed.Rows(ed.Table("creature"), RowFilter.Eq("id", 3098), 1).Select(ed.Table("creature").KeyText).First(), "patch_max", "11").Ok);
    }

    [IsolatedDbFact]
    public void AddDuplicateDeleteBulkUndoRedoWithCustomIds()
    {
        using var ctx = new Ctx("edit");
        ctx.Settings.ServerCustomIdStart = 900000;
        ctx.Settings.ServerCustomIdEnd = 900005;
        var ed = ctx.Editor();
        var ct = ed.Table("creature_template");
        Assert.Equal(900000, ed.SuggestId(ct));
        var (k1, r1) = ed.Duplicate("creature_template", "entry=3158;patch=0", new[] { ("name", (string?)"Grokk the Merchant") });
        Assert.True(r1.Ok, r1.Message);
        Assert.Equal("entry=900000;patch=0", k1);
        Assert.Equal(900001, ed.SuggestId(ct));
        Assert.Equal("Grokk the Merchant", ed.Current(ct, k1!)![ct.IndexOf("name")]);
        var (k2, r2) = ed.Add("npc_vendor", new[] { ("entry", (string?)"900000"), ("item", "25") });
        Assert.True(r2.Ok, r2.Message);
        Assert.Equal("entry=900000;item=25", k2);
        Assert.False(ed.Add("npc_vendor", new[] { ("entry", (string?)"900000"), ("item", "25") }).Result.Ok);   // the same key twice
        // a column that cannot hold the range (smallint taxi ids): the next one after the highest
        Assert.True(ed.SuggestId(ed.Table("taxi_nodes")) < 1000);
        // Used by before delete
        var used = ed.UsedBy("item_template", new[] { "entry=159;patch=0" });
        Assert.Contains(used, u => u.Contains("npc_vendor"));
        // bulk: all checked first, nothing changes when one is refused
        var vt = ed.Table("npc_vendor");
        var keys = ed.Rows(vt, RowFilter.Eq("entry", 3158)).Select(vt.KeyText).ToList();
        Assert.False(ed.Bulk("npc_vendor", keys, "maxcount", BulkOperation.Set, "300").Ok);
        Assert.Equal(6, ed.Bulk("npc_vendor", keys, "maxcount", BulkOperation.Set, "5").Changed);
        Assert.True(ed.Bulk("npc_vendor", keys, "maxcount", BulkOperation.Multiply, "2").Ok);
        Assert.All(keys, k => Assert.Equal("10", ed.Current(vt, k)![vt.IndexOf("maxcount")]));
        Assert.True(ed.Delete("npc_vendor", keys.Take(2)).Ok);
        Assert.Null(ed.Current(vt, keys[0]));
        // undo / redo, one step each
        Assert.True(ed.Undo());
        Assert.NotNull(ed.Current(vt, keys[0]));
        Assert.True(ed.Redo());
        Assert.Null(ed.Current(vt, keys[0]));
        while (ed.Undo()) { }
        Assert.Empty(ed.Changes);
        Assert.False(ed.IsDirty);
        // a key column edit moves the row
        Assert.True(ed.Set("npc_vendor", keys[0], "item", "117").Ok);
        Assert.Null(ed.Current(vt, keys[0]));
        Assert.NotNull(ed.Current(vt, "entry=3158;item=117"));
        ed.Discard();
        Assert.NotNull(ed.Current(vt, keys[0]));
    }

    [IsolatedDbFact]
    public void SavedEditsAreWorkspaceChangesWithARowDiffAndNeverGameFiles()
    {
        using var ctx = new Ctx("workspace");
        var ed = ctx.Editor();
        Assert.True(ed.Set("item_template", "entry=25;patch=0", "dmg_max1", "12").Ok);
        Assert.True(ed.Add("npc_vendor", new[] { ("entry", (string?)"3158"), ("item", "25") }).Result.Ok);
        Assert.True(ed.IsDirty);
        var (recs, msg) = ed.Save();
        Assert.Contains("not changed yet", msg);
        Assert.False(ed.IsDirty);
        var path = ServerChangeFiles.PathOf(ctx.Db.Database, "item_template");
        var rec = ctx.Ws.Get(path)!;
        Assert.Equal("server", rec.Kind);
        Assert.Equal("server table", rec.Original.Format);
        Assert.Contains("not applied to the server yet", ctx.Ws.View(rec).StatusText);
        Assert.Equal(recs[0].ChangeSet, recs[1].ChangeSet);   // one save = one change set
        // the file: before and after of each row, deterministic
        var data = ctx.Ws.ReadEdited(path)!;
        var read = ServerChangeFiles.Read(data, ctx.Svc.Schema.Require("item_template"));
        var row = Assert.Single(read.Rows.Values);
        Assert.Equal("3", row.Before![read.Table.IndexOf("dmg_max1")]);
        Assert.Equal("12", row.After![read.Table.IndexOf("dmg_max1")]);
        Assert.Equal(data, ServerChangeFiles.Write(ctx.Db.Database, read));
        // a new editor sees the saved edits; the row diff for Compare
        var ed2 = ctx.Editor();
        Assert.Equal("12", ed2.Current(read.Table, "entry=25;patch=0")![read.Table.IndexOf("dmg_max1")]);
        var diff = ServerExchange.Diff(ed2, ed2.ChangesOf("item_template")!);
        Assert.Equal(1, diff.Changed);
        Assert.Equal("dmg_max1", Assert.Single(diff.Rows[0].Cells).Column);
        Assert.Equal(1, ServerExchange.Diff(ed2, ed2.ChangesOf("npc_vendor")!).Added);
        // server edits are not game files: no loose install, no patch-3.MPQ
        Assert.Empty(ctx.Ws.GameFiles());
        Assert.Contains("no changes", Assert.Throws<WorkspaceException>(() => ctx.Ws.PlanLooseInstall()).Message);
        Assert.Contains("no changes", Assert.Throws<WorkspaceException>(() => ctx.Ws.BuildPatch()).Message);
        // editing back to the server's value and saving removes the table's file
        Assert.True(ed2.Set("item_template", "entry=25;patch=0", "dmg_max1", "3").Ok);
        ed2.Save();
        Assert.False(ctx.Ws.IsModified(path));
        // reverting in My changes
        ctx.Ws.RevertChangeSet(recs[1].ChangeSet);
        Assert.Empty(ctx.Svc.SavedChanges());
    }

    private static List<ServerTableChanges> MakeEdits(Ctx ctx)
    {
        var ed = ctx.Editor();
        Assert.True(ed.Set("item_template", "entry=25;patch=0", new[] { ("dmg_max1", (string?)"12"), ("name", "Worn Shortsword of O'Neil"), ("description", "Line one\nline \"two\"") }).Ok);
        Assert.True(ed.Set("creature_template", "entry=3098;patch=7", new[] { ("speed_run", (string?)"1.14286"), ("subname", "Tusk & Co.") }).Ok);
        Assert.True(ed.Duplicate("creature_template", "entry=3158;patch=0", new[] { ("name", (string?)"Grokk the Merchant") }).Result.Ok);
        Assert.True(ed.Add("npc_vendor", new[] { ("entry", (string?)"900000"), ("item", "25") }).Result.Ok);
        var vt = ed.Table("npc_vendor");
        Assert.True(ed.Delete("npc_vendor", new[] { ed.Rows(vt, RowFilter.Eq("entry", 3158)).Select(vt.KeyText).First() }).Ok);
        Assert.True(ed.Set("faction_template", ed.ResolveKey(ed.Table("faction_template"), "29"), "friendly_mask", "12345").Ok);   // InnoDB
        ed.Save();
        return ctx.Svc.SavedChanges();
    }

    private static readonly string[] Tables = { "item_template", "creature_template", "npc_vendor", "faction_template" };

    [IsolatedDbFact]
    public void TheSqlPatchIsIdempotentAndItsRevertRestoresTheOriginalRows()
    {
        using var ctx = new Ctx("sql");
        var before = ServerTestDb.Checksums(ctx.Db, Tables);
        var dumps = Tables.ToDictionary(t => t, t => ServerTestDb.Dump(ctx.Db, t));
        var changes = MakeEdits(ctx);
        var script = ServerSql.Build(ctx.Svc, changes);
        Assert.Contains("-- WoW Data Studio", script.Patch);
        Assert.Contains("START TRANSACTION;", script.Patch);
        Assert.Contains(".reload item_template", script.Patch);
        Assert.DoesNotContain("127.0.0.1", script.Patch);                  // no host, user or password in a script
        Assert.DoesNotContain(ctx.Db.Port.ToString(), script.Patch);
        Assert.Equal(6, script.Rows);
        ServerTestDb.RunScript(ctx.Db, script.Patch);
        var once = ServerTestDb.Checksums(ctx.Db, Tables);
        Assert.NotEqual(before, once);
        var dumpOnce = Tables.ToDictionary(t => t, t => ServerTestDb.Dump(ctx.Db, t));
        ServerTestDb.RunScript(ctx.Db, script.Patch);                    // twice: the same database
        Assert.Equal(once, ServerTestDb.Checksums(ctx.Db, Tables));
        foreach (var t in Tables) Assert.Equal(dumpOnce[t], ServerTestDb.Dump(ctx.Db, t));
        Assert.Equal("Worn Shortsword of O'Neil", ServerTestDb.Scalar(ctx.Db, "SELECT name FROM item_template WHERE entry=25"));
        Assert.Equal("Line one\nline \"two\"", ServerTestDb.Scalar(ctx.Db, "SELECT description FROM item_template WHERE entry=25"));
        Assert.Equal(1.14286f, Convert.ToSingle(ServerTestDb.Scalar(ctx.Db, "SELECT speed_run FROM creature_template WHERE entry=3098 AND patch=7")));
        ServerTestDb.RunScript(ctx.Db, script.Revert);                   // revert: the original rows, byte for byte
        Assert.Equal(before, ServerTestDb.Checksums(ctx.Db, Tables));
        foreach (var t in Tables) Assert.Equal(dumps[t], ServerTestDb.Dump(ctx.Db, t));
        ServerTestDb.RunScript(ctx.Db, script.Revert);                   // the revert is idempotent too
        Assert.Equal(before, ServerTestDb.Checksums(ctx.Db, Tables));
    }

    [IsolatedDbFact]
    public void ApplyBacksUpVerifiesLogsAndUndoesExactly()
    {
        using var ctx = new Ctx("apply");
        var before = ServerTestDb.Checksums(ctx.Db, Tables);
        var dumps = Tables.ToDictionary(t => t, t => ServerTestDb.Dump(ctx.Db, t));
        MakeEdits(ctx);
        var ap = new ServerApplier(ctx.Svc);
        var plan = ap.Plan();
        Assert.Empty(plan.Conflicts);
        Assert.Equal(6, plan.Script.Rows);
        Assert.Contains("npc_vendor", plan.NonTransactional);
        Assert.DoesNotContain("faction_template", plan.NonTransactional);
        var (rec, done) = ap.Apply(plan);
        Assert.Contains(done, d => d.Contains("all as you edited them"));
        Assert.True(File.Exists(rec.BackupRowsFile) && File.Exists(rec.BackupTablesFile) && File.Exists(rec.PatchFile) && File.Exists(rec.RevertFile));
        Assert.Contains("INSERT INTO `item_template`", File.ReadAllText(rec.BackupTablesFile));
        Assert.Equal(12, Convert.ToInt32(ServerTestDb.Scalar(ctx.Db, "SELECT dmg_max1 FROM item_template WHERE entry=25")));
        Assert.Single(ap.History());
        Assert.True(File.ReadAllText(Path.Combine(ctx.Paths.Dir, "applied_changes.log")).Contains("APPLY"));
        Assert.All(ctx.Ws.List(), v => Assert.Contains("applied to the server", v.StatusText));
        // applying again changes nothing (every row is already as wanted)
        Assert.True(ap.Plan().NothingToDo);
        // undo: the rows exactly as they were
        Assert.Empty(ap.UndoConflicts(rec));
        var undone = ap.Undo(rec);
        Assert.Contains(undone, d => d.Contains("back as they were"));
        Assert.Equal(before, ServerTestDb.Checksums(ctx.Db, Tables));
        foreach (var t in Tables) Assert.Equal(dumps[t], ServerTestDb.Dump(ctx.Db, t));
        Assert.NotNull(ap.History().Single().Undone);
        Assert.Null(ap.Last());
        Assert.All(ctx.Ws.List(), v => Assert.Contains("not applied", v.StatusText));
        Assert.Throws<ServerDataException>(() => ap.Undo(rec));        // only once
    }

    [IsolatedDbFact]
    public void ApplyRefusesConflictsAndOtherDatabasesAndTakesBackAFailedApply()
    {
        using var ctx = new Ctx("refuse");
        MakeEdits(ctx);
        var ap = new ServerApplier(ctx.Svc);
        // somebody changed a row on the server meanwhile: shown, refused unless overwritten
        ServerTestDb.Exec(ctx.Db, "UPDATE item_template SET dmg_max1=7 WHERE entry=25 AND patch=0");
        var plan = ap.Plan();
        Assert.Contains(plan.Conflicts, c => c.Contains("item_template entry=25;patch=0"));
        Assert.StartsWith("Refused", Assert.Throws<ServerDataException>(() => ap.Apply(plan)).Message);
        var (rec, _) = ap.Apply(plan, overwriteConflicts: true);
        // the server changes a row after the apply: undo refuses unless overwritten
        ServerTestDb.Exec(ctx.Db, "UPDATE npc_vendor SET maxcount=3 WHERE entry=900000");
        Assert.NotEmpty(ap.UndoConflicts(rec));
        Assert.StartsWith("Refused", Assert.Throws<ServerDataException>(() => ap.Undo(rec)).Message);
        ap.Undo(rec, overwrite: true);
        Assert.Equal(7, Convert.ToInt32(ServerTestDb.Scalar(ctx.Db, "SELECT dmg_max1 FROM item_template WHERE entry=25")));

        // a failure in the middle (a MyISAM table cannot roll back): the backup rows are written back
        var before = ServerTestDb.Checksums(ctx.Db, Tables);
        var good = ctx.Svc.SavedChanges();
        var it = ctx.Svc.Schema.Require("item_template");
        var bad = new ServerTableChanges(it);
        var row = ctx.Svc.Db.Row(it, "entry=117;patch=0")!;
        var broken = (string?[])row.Clone();
        broken[it.IndexOf("quality")] = "not a number";     // something the database refuses in strict mode
        bad.Rows["entry=117;patch=0"] = new ServerRowChange("entry=117;patch=0", row, broken);
        var failing = good.Where(c => c.Table.Name != "item_template").Append(bad).ToList();
        var e = Assert.Throws<ServerDataException>(() => ap.Apply(ap.Plan(failing), overwriteConflicts: true));
        Assert.Contains("put back", e.Message);
        Assert.Equal(before, ServerTestDb.Checksums(ctx.Db, Tables));

        // never a database that is not the world database
        var charsName = "wds_characters_" + Guid.NewGuid().ToString("N")[..6];
        ServerTestDb.Server.CopyDatabase(ctx.Db.Database, charsName);
        var chars = ServerTestDb.Server.Settings(charsName);
        ServerTestDb.AssertIsolated(chars);
        var svc2 = new ServerDataService(chars, ctx.Ws, ctx.Dbc, new AppSettings { Server = chars }, null, ctx.Paths.Dir, () => new List<string>());
        Assert.False(svc2.Db.IsWorldDatabase(out var why));
        Assert.Contains("characters", why);
        var ap2 = new ServerApplier(svc2);
        Assert.StartsWith("Refused", Assert.Throws<ServerDataException>(() => ap2.Apply(ap2.Plan())).Message);
        ServerTestDb.Exec(chars, $"DROP DATABASE `{charsName}`");
    }

    [IsolatedDbFact]
    public void ClientAndServerChecksFlagDifferencesAndMakeThemMatchBothWays()
    {
        using var ctx = new Ctx("consistency");
        var ed = ctx.Editor();
        Assert.Empty(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }));
        // a spell's numbers changed on the server: one difference; make the game match the server
        var spellKey = ed.ResolveKey(ed.Table("spell_template"), "133");
        Assert.True(ed.Set("spell_template", spellKey, new[] { ("manaCost", (string?)"77"), ("effectBasePoints1", "41") }).Ok);
        var issue = Assert.Single(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }));
        Assert.Equal(ConsistencyKind.Differs, issue.Kind);
        Assert.Equal(2, issue.Fields.Count(f => !f.Same));
        var msg = ServerConsistency.MatchClient(ed, issue);
        Assert.Contains("Spell 133", msg);
        Assert.True(ctx.Ws.IsModified(@"DBFilesClient\Spell.dbc"));
        var spellDbc = new DbcEditSession(ctx.Dbc, "Spell");
        Assert.Equal("77", spellDbc.CellText(spellDbc.Row(133)!, spellDbc.Column("ManaCost")!, true));
        Assert.Empty(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }));
        // the other way: the server takes the game's values
        ed.Discard();
        var again = Assert.Single(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }));   // the game now has 77, the server 0... differs
        Assert.True(ServerConsistency.MatchServer(ed, again).Ok);
        Assert.Empty(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }));
        Assert.Equal("77", ed.Current(ed.Table("spell_template"), spellKey)![ed.Table("spell_template").IndexOf("manaCost")]);
        // an area renamed on the server: Map / AreaTable / Faction / TaxiNodes the same way
        Assert.True(ed.Set("area_template", "entry=363", "name", "Valley of Trials (test)").Ok);
        var area = Assert.Single(ServerConsistency.Check(ed, "areas", new HashSet<long> { 363 }));
        Assert.Contains(area.Fields, f => f.ServerColumn == "name" && !f.Same);
        Assert.True(ServerConsistency.MatchServer(ed, area).Ok);
        Assert.Empty(ServerConsistency.Check(ed, "areas", new HashSet<long> { 363 }));
        Assert.True(ed.Set("taxi_nodes", ed.ResolveKey(ed.Table("taxi_nodes"), "23"), "x", "1700").Ok);
        Assert.Single(ServerConsistency.Check(ed, "flight-paths", new HashSet<long> { 23 }));
        // item looks: a display the game does not have
        Assert.True(ed.Set("item_template", "entry=25;patch=0", "display_id", "16000000").Ok == false);   // refused when edited
        ServerTestDb.Exec(ctx.Db, "UPDATE item_template SET display_id=16000000 WHERE entry=117");
        var missing = Assert.Single(ServerConsistency.Check(ed, "items", new HashSet<long> { 117 }));
        Assert.Equal(ConsistencyKind.MissingInGame, missing.Kind);
        Assert.False(missing.CanMatchServer || missing.CanMatchClient);
        // creature looks: the server's own facts missing for a display it uses
        ServerTestDb.Exec(ctx.Db, "DELETE FROM creature_display_info_addon WHERE display_id=503");
        var addon = ServerConsistency.Check(ed, "creatures", new HashSet<long> { 3098 }).Single(i => i.Kind == ConsistencyKind.MissingOnServer);
        Assert.Equal(503, addon.Id);
        Assert.True(ServerConsistency.MatchServer(ed, addon).Ok);
        Assert.DoesNotContain(ServerConsistency.Check(ed, "creatures", new HashSet<long> { 3098 }), i => i.Kind == ConsistencyKind.MissingOnServer);
    }

    [IsolatedDbFact]
    public void CsvAndJsonExportsImportUnchangedAndTheImportShowsAPreview()
    {
        using var ctx = new Ctx("exchange");
        var ed = ctx.Editor();
        var vt = ed.Table("npc_vendor");
        var scope = RowFilter.Eq("entry", 3158);
        var rows = ed.Rows(vt, scope);
        foreach (var ext in new[] { "csv", "json" })
        {
            var file = Path.Combine(ctx.Dir, "duokna." + ext);
            ServerExchange.Export(vt, rows, file, ctx.Db.Database);
            var same = ServerExchange.Plan(ed, "npc_vendor", file, removeMissing: true, scope);
            Assert.Empty(same.Errors);
            Assert.True(same.IsEmpty, same.Preview.Summary);
        }
        // NULL and floats exactly, both formats
        var ct = ed.Table("creature_template");
        var boar = ed.Rows(ct, RowFilter.Eq("entry", 3098));
        foreach (var ext in new[] { "csv", "json" })
        {
            var file = Path.Combine(ctx.Dir, "boar." + ext);
            ServerExchange.Export(ct, boar, file, ctx.Db.Database);
            Assert.True(ServerExchange.Plan(ed, "creature_template", file).IsEmpty);
        }
        Assert.Contains("\\N", File.ReadAllText(Path.Combine(ctx.Dir, "boar.csv")));
        // a changed value, a new row, a missing row
        var csv = Path.Combine(ctx.Dir, "duokna.csv");
        var lines = File.ReadAllLines(csv).ToList();
        lines[1] = lines[1].Replace(",0,0,0,0", ",5,0,0,0");
        lines.RemoveAt(2);
        lines.Add("3158,99,25,0,0,0,0");
        File.WriteAllLines(csv, lines);
        var plan = ServerExchange.Plan(ed, "npc_vendor", csv, removeMissing: true, scope);
        Assert.Empty(plan.Errors);
        Assert.Equal(1, plan.Preview.Changed);
        Assert.Equal(1, plan.Preview.Added);
        Assert.Equal(1, plan.Preview.Removed);
        Assert.Contains("Imported", ServerExchange.Apply(ed, plan));
        Assert.Equal(1, ed.ChangesOf("npc_vendor")!.Added);
        Assert.True(ed.Undo());                                          // one undo step
        Assert.Empty(ed.Changes);
        // errors block the import
        File.WriteAllLines(csv, new[] { "entry,slot,item,maxcount,incrtime,itemflags,condition_id", "3158,1,159,abc,0,0,0", "3158,1,159,0,0,0,0" });
        var badPlan = ServerExchange.Plan(ed, "npc_vendor", csv);
        Assert.True(badPlan.Errors.Count >= 2, string.Join("\n", badPlan.Errors));   // a bad number, a key twice
        Assert.Throws<ServerDataException>(() => ServerExchange.Apply(ed, badPlan));
        File.WriteAllLines(csv, new[] { "entry,nosuchcolumn", "3158,1" });
        Assert.NotEmpty(ServerExchange.Plan(ed, "npc_vendor", csv).Errors);
    }

    [Fact]
    public void TheGamesCacheIsMovedToTheBackupsAndRefusedWhileTheGameRuns()
    {
        var root = TestEnv.TempDir("wdb");
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            File.WriteAllText(Path.Combine(game, "WoW.exe"), "not the game");
            Directory.CreateDirectory(Path.Combine(game, "WDB"));
            foreach (var n in new[] { "itemcache.wdb", "creaturecache.wdb", "questcache.wdb" }) File.WriteAllText(Path.Combine(game, "WDB", n), n);
            Assert.Equal(3, WdbCache.Files(game).Count);
            Assert.Contains("running", Assert.Throws<ServerDataException>(() => WdbCache.Clear(game, Path.Combine(root, "backups"), _ => new List<string> { "WoW.exe (process 1)" })).Message);
            Assert.Equal(3, WdbCache.Files(game).Count);
            var (backup, done) = WdbCache.Clear(game, Path.Combine(root, "backups"), _ => new List<string>());
            Assert.Empty(WdbCache.Files(game));
            Assert.Equal("itemcache.wdb", File.ReadAllText(Path.Combine(backup!, "itemcache.wdb")));   // moved, not deleted
            Assert.Contains(done, d => d.Contains("asks the server again"));
            Assert.Null(WdbCache.Clear(game, Path.Combine(root, "backups"), _ => new List<string>()).Backup);   // already empty
            Assert.Throws<ServerDataException>(() => WdbCache.Clear(root, Path.Combine(root, "backups"), _ => new List<string>()));   // not a game folder
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [IsolatedDbFact]
    public void TheTestDatabaseIsAThrowawayServerAndNeverTheLiveOne()
    {
        var s = ServerTestDb.Server;
        Assert.NotEqual(3306, s.Port);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), s.DataDir, StringComparison.OrdinalIgnoreCase);
        Assert.False(s.DataDir.StartsWith(ServerTestDb.ServerFolder, StringComparison.OrdinalIgnoreCase));
        var db = ServerTestDb.NewDatabase("iso");
        ServerTestDb.AssertIsolated(db);
        Assert.Equal(s.DataDir, ((string)ServerTestDb.Scalar(db, "SELECT @@datadir")!).TrimEnd('\\', '/').Replace('/', '\\'), StringComparer.OrdinalIgnoreCase);
        ServerTestDb.Exec(db, $"DROP DATABASE `{db.Database}`");
    }
}
