using System.IO;
using System.Windows;
using WoWData.Core.Dbc;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.9.0, the Server data tab). These steps NEVER use the user's server: they start a throwaway
/// MySQL server (ThrowawayMySql: its own empty data folder in %TEMP%, its own port) loaded from the tests' fixture
/// (app\WoWData.Tests\serverdb\world_fixture.sql), point this run's settings at it, and stop and delete it at the end.
///   95_server_items          the Items view: Worn Shortsword with its icon, values, vendors and Used by
///   96_server_creature       the Creatures view: Mottled Boar with its spawns, loot and the server's look facts
///   97_server_save           "Save to my workspace": the item's new damage and its own new display
///   98_server_vendor         a new NPC vendor (a copy of Duokna) selling the item, with a spawn
///   99_server_quest          a new quest with its starter and ender NPCs, objective and reward
///   100_server_spell_client  Fireball edited on the server: the game's Spell.dbc values side by side
///   101_server_check         "Check client and server": the spell's differences
///   102_server_apply         "Apply to server": the statements, the notes
///   103_server_my_changes    My changes with the server edits applied to the (throwaway) server
///   104_server_import        importing a vendor list from CSV: the preview
///   105_server_dbc_link      the Data tables tab: Spell, "Edit it in the Server data tab"
///   106_server_wdb           "Clear the game's cache (WDB)"
/// </summary>
internal static class ServerDataScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static Task Shot(MainWindow win, string outDir, string name)
    {
        ScreenshotRunner.Main!.ToastVisible = false;
        return ScreenshotRunner.Shot(win, outDir, name);
    }

    private static async Task ShotWindow(Window w, double width, double height, string outDir, string name)
    {
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var host = new System.Windows.Controls.Border { Child = content, Width = width, Height = height };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBg");
        var hw = new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = -30000, Top = 0,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        hw.SetResourceReference(FrameworkElement.StyleProperty, "AppWindow");
        hw.Show();
        await ScreenshotRunner.Settle(400);
        await ScreenshotRunner.Shot(hw, outDir, name);
        hw.Close();
        host.Child = null;
        w.Content = content;
    }

    /// <summary>The tests' fixture (the schema and a sample of the world database), found above the program.</summary>
    private static string? Fixture()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "app", "WoWData.Tests", "serverdb", "world_fixture.sql");
            if (File.Exists(f)) return f;
            f = Path.Combine(d.FullName, "WoWData.Tests", "serverdb", "world_fixture.sql");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private static async Task Open(ServerDataViewModel s, string table, string key)
    {
        await s.OpenRecordAsync(table, key, false);
        await ScreenshotRunner.Until(() => !s.IsLoading && s.HasRecord && s.CurrentKey == key, 60000);
        await ScreenshotRunner.Settle(400);
    }

    private static async Task Search(ServerDataViewModel s, string view, string text, string? table = null)
    {
        s.SelectedView = s.Views.First(v => v.Def.Id == view);
        if (table != null) s.SelectedSubTable = table;
        s.SearchText = text;
        await s.SearchAsync();
        await ScreenshotRunner.Until(() => !s.IsLoading, 60000);
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var mysqld = ThrowawayMySql.FindMysqld(session.ServerFolder);
        var fixture = Fixture();
        if (mysqld == null || fixture == null)
        {
            Expect(false, $"server data: no throwaway MySQL server for the pictures (mysqld: {mysqld ?? "not found"}, fixture: {fixture ?? "not found"})");
            return;
        }
        using var db = ThrowawayMySql.Start(mysqld);
        db.CreateDatabase("wds_screens", fixture);
        var keep = session.Settings.Server;
        session.Settings.Server = db.Settings("wds_screens");
        session.ServerRunningProbe = () => new List<string>();
        try
        {
            await Steps(win, b, outDir, session);
        }
        finally
        {
            // the next steps of the run (if any) use the settings of the run again
            session.Settings.Server = keep;
            session.ResetServerData();
        }
    }

    private static async Task Steps(MainWindow win, BrowserViewModel b, string outDir, WoWData.Core.Session.AppSession session)
    {
        var s = b.Server;
        ScreenshotRunner.Place(win, 1680, 1050);
        win.UpdateLayout();
        b.SelectedTab = BrowserViewModel.ServerTab;
        await s.ReconnectAsync();
        await ScreenshotRunner.Until(() => s.IsLoaded && !s.IsLoading, 60000);
        Expect(s.IsConnected, $"server data: connected to the throwaway database ({s.ConnectionText})");
        if (!s.IsConnected) return;
        Expect(s.Service!.Db.Settings.Port != 3306 && s.Service.Database == "wds_screens", "server data: the pictures use the throwaway database only");

        // ---- 95: Items, Worn Shortsword
        await Search(s, "items", "worn shortsword");
        var sword = s.Results.FirstOrDefault(r => r.Id == 25);
        Expect(sword != null, "server data: the Items view finds Worn Shortsword");
        await Open(s, "item_template", "entry=25;patch=0");
        if (s.HasPreview) await ScreenshotRunner.Until(() => s.Preview.IsLoaded || s.Preview.Error.Length > 0, 30000);
        Expect(s.PreviewPath.EndsWith("INV_Sword_04.blp", StringComparison.OrdinalIgnoreCase), $"server data: the item's icon comes from the game's ItemDisplayInfo ({s.PreviewPath})");
        Expect(s.FieldGroups.SelectMany(g => g.Fields).Any(f => f.Name == "quality" && f.IsEnum), "server data: quality is a named choice");
        await Shot(win, outDir, "95_server_items");

        // ---- 96: Creatures, Mottled Boar
        await Search(s, "creatures", "mottled boar");
        await Open(s, "creature_template", "entry=3098;patch=7");
        Expect(s.Sections.Any(x => x.Section.Title == "Spawns" && x.Rows.Count > 20), "server data: the boar's spawns are listed");
        Expect(s.Sections.Any(x => x.Section.Title == "Loot when killed"), "server data: the boar's loot is listed");
        Expect(s.UsedBy.Count > 0, "server data: Used by lists the spawns");
        await Shot(win, outDir, "96_server_creature");

        // ---- 97: the item's stats, and its own new display (a copy of display 1542 with another icon, in the game's table)
        var dbc = session.Dbc!;
        var idi = new DbcEditSession(dbc, "ItemDisplayInfo");
        var (newDisplay, _) = idi.Duplicate(idi.Row(1542)!);
        long newDisplayId = idi.Table.Key(newDisplay)!.Value;
        Expect(idi.Edit(newDisplay, idi.Column("InventoryIcon")!, "INV_Sword_05").Ok, "server data: a new display in ItemDisplayInfo with another icon");
        idi.Save(idi.CompareWithGame());
        await Open(s, "item_template", "entry=25;patch=0");
        Expect(s.TryEdit("dmg_min1", "3")?.Ok == true && s.TryEdit("dmg_max1", "7")?.Ok == true, "server data: the sword's damage changed");
        Expect(s.TryEdit("display_id", newDisplayId.ToString())?.Ok == true, "server data: the sword uses its new display");
        Expect(s.TryEdit("display_id", "16000000")?.Ok == false, "server data: a display the game does not have is refused");
        var ed = s.Editor!;
        var diff = ServerExchange.Diff(ed, ed.ChangesOf("item_template")!);
        await ShotWindow(new DbcDiffWindow("Save server edits to my workspace", diff,
            $"Your edits compared with the server's rows: {diff.Summary} They go into your workspace (My changes); the server is changed only by \"Apply to server\" or an SQL patch.",
            "Save to my workspace", "On the server", "Your edit"), 1100, 560, outDir, "97_server_save");
        Expect(await s.SaveCore(false), "server data: saved into the workspace");

        // ---- 98: a new NPC vendor: a copy of Duokna, selling the sword, with a spawn
        await Open(s, "creature_template", "entry=3158;patch=0");
        await s.DuplicateAsNewCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => !s.IsLoading && s.CurrentKey != "entry=3158;patch=0", 30000);
        var grokk = s.CurrentKey!;
        Expect(grokk.StartsWith("entry=900000", StringComparison.Ordinal), $"server data: the copy gets the first free custom ID ({grokk})");
        Expect(s.TryEdit("name", "Grokk")?.Ok == true && s.TryEdit("subname", "Weapon Merchant")?.Ok == true, "server data: the new vendor's name");
        long grokkId = long.Parse(grokk.Split(';')[0].Split('=')[1]);
        var (vk, vr) = ed.Add("npc_vendor", new[] { ("entry", (string?)grokkId.ToString()), ("item", "25") });
        Expect(vr.Ok, "server data: the vendor sells the sword: " + vr.Message);
        var ct = ed.Table("creature");
        var duoknaSpawn = ed.Rows(ct, WoWData.Core.ServerData.RowFilter.Eq("id", 3158), 1).First();
        var (sk, sr) = ed.Duplicate("creature", ct.KeyText(duoknaSpawn), new[] { ("id", (string?)grokkId.ToString()), ("position_x", "-603.5"), ("position_y", "-4204.5") });
        Expect(sr.Ok, "server data: a spawn for the new vendor: " + sr.Message);
        await Open(s, "creature_template", grokk);
        Expect(s.Sections.Any(x => x.Section.Title == "Sells" && x.Rows.Count == 1) && s.Sections.Any(x => x.Section.Title == "Spawns" && x.Rows.Count == 1), "server data: the new vendor sells one item and has one spawn");
        await Shot(win, outDir, "98_server_vendor");

        // ---- 99: a small quest, started by the new vendor, ended by Gornek
        var (qk, qr) = ed.Add("quest_template", new (string, string?)[]
        {
            ("Title", "A Sharp Gift"), ("Details", "Bring me five Tough Jerky from the boars, and the blade is yours."), ("Objectives", "Bring 5 Tough Jerky to Gornek."),
            ("OfferRewardText", "Well done. Take this sword."), ("RequestItemsText", "Do you have the jerky?"), ("MinLevel", "1"), ("QuestLevel", "3"),
            ("ReqItemId1", "117"), ("ReqItemCount1", "5"), ("RewItemId1", "25"), ("RewItemCount1", "1"), ("ZoneOrSort", "363"),
        });
        Expect(qr.Ok, "server data: a new quest: " + qr.Message);
        long questId = long.Parse(qk!.Split(';')[0].Split('=')[1]);
        Expect(ed.Add("creature_questrelation", new[] { ("id", (string?)grokkId.ToString()), ("quest", questId.ToString()) }).Result.Ok, "server data: the new vendor starts the quest");
        Expect(ed.Add("creature_involvedrelation", new[] { ("id", (string?)"3143"), ("quest", questId.ToString()) }).Result.Ok, "server data: Gornek ends the quest");
        Expect(ed.Set("creature_template", grokk, "npc_flags", "Gossip|Quest giver|Vendor").Ok, "server data: the new vendor is a quest giver too");
        await Open(s, "quest_template", qk);
        Expect(s.Sections.Any(x => x.Section.Title == "Started by creatures" && x.Rows.Count == 1) && s.Sections.Any(x => x.Section.Title == "Ended by creatures" && x.Rows.Count == 1), "server data: the quest has its starter and ender");
        await Shot(win, outDir, "99_server_quest");

        // ---- 100: Fireball's numbers on the server: the game's values next to them (from the game's own Spell.dbc: the
        // Data tables pictures before changed its icon in the workspace)
        if (session.Workspace!.IsModified(@"DBFilesClient\Spell.dbc"))
            WoWData.Core.Workspace.ChangeOperations.Revert(session, @"DBFilesClient\Spell.dbc", null);
        var fireball = ed.ResolveKey(ed.Table("spell_template"), "133");
        await Open(s, "spell_template", fireball);
        Expect(s.HasClient && s.ClientText.Contains("same values"), "server data: Fireball agrees with the game before the edit");
        Expect(s.TryEdit("manaCost", "25")?.Ok == true && s.TryEdit("effectBasePoints1", "18")?.Ok == true && s.TryEdit("effectDieSides1", "6")?.Ok == true, "server data: Fireball's cost and damage edited");
        await Open(s, "spell_template", fireball);
        Expect(s.ClientFields.Count(c => !c.Same) == 3, $"server data: three of Fireball's values differ from the game ({s.ClientText})");
        await Shot(win, outDir, "100_server_spell_client");

        // ---- 101: Check client and server
        var cw = new ServerCheckWindow(s, ed, "spells");
        await cw.RunAsync("spells", "133");
        Expect(cw.Issues.Count == 1 && cw.Issues[0].Kind == ConsistencyKind.Differs, "server data: the check finds Fireball's difference");
        await ShotWindow(cw, 1150, 700, outDir, "101_server_check");
        Expect(cw.MatchClient(cw.Issues) == 1, "server data: the game's Spell.dbc takes the server's values");
        Expect(session.Workspace!.IsModified(@"DBFilesClient\Spell.dbc"), "server data: Spell.dbc is in the workspace");
        Expect(ServerConsistency.Check(ed, "spells", new HashSet<long> { 133 }).Count == 0, "server data: Fireball agrees again");

        // ---- 102: Apply to server (the throwaway one)
        Expect(await s.SaveCore(false), "server data: everything saved into the workspace");
        var ap = new ServerApplier(s.Service!);
        var plan = ap.Plan();
        Expect(plan.Conflicts.Count == 0 && plan.Script.Rows >= 8, $"server data: the apply plan has {plan.Script.Rows} rows");
        await ShotWindow(new ServerApplyWindow(plan), 1000, 700, outDir, "102_server_apply");
        var (rec, done) = ap.Apply(plan);
        Expect(done.Any(d => d.Contains("all as you edited them")), "server data: applied to the throwaway server and read back");

        // ---- 103: My changes with the server edits (applied) next to the game's tables
        b.SelectedTab = 1;
        await ScreenshotRunner.Settle(800);
        Expect(b.Changes.Any(c => c.View.Record.IsServerEdit && c.Status.Contains("applied to the server")), "server data: My changes says the server edits are applied");
        await Shot(win, outDir, "103_server_my_changes");
        b.SelectedTab = BrowserViewModel.ServerTab;

        // ---- 104: a vendor list through a spreadsheet: the import preview
        var vt = ed.Table("npc_vendor");
        var csv = Path.Combine(outDir, "_home", "duokna.csv");
        ServerExchange.Export(vt, ed.Rows(vt, WoWData.Core.ServerData.RowFilter.Eq("entry", 3158)), csv, s.Service!.Database);
        var lines = File.ReadAllLines(csv).ToList();
        lines[1] = lines[1].Replace(",0,0,0,0", ",5,3600,0,0");
        lines.Add("3158,99,117,0,0,0,0");
        File.WriteAllLines(csv, lines);
        var iw = new ServerImportWindow(s, ed, "npc_vendor", csv, WoWData.Core.ServerData.RowFilter.Eq("entry", 3158));
        Expect(iw.CurrentPlan?.Preview.Changed == 1 && iw.CurrentPlan.Preview.Added == 1, "server data: the import preview shows a changed and an added row");
        await ShotWindow(iw, 1100, 640, outDir, "104_server_import");

        // ---- 105: the Data tables tab links tables the server keeps in its database to this tab
        b.SelectedTab = BrowserViewModel.TablesTab;
        await b.Tables.EnsureLoadedAsync();
        await b.Tables.SelectTableAsync(b.Tables.AllTables.First(x => x.Name == "Spell"));
        await ScreenshotRunner.Until(() => b.Tables.Table?.Name == "Spell" && !b.Tables.IsLoading, 60000);
        b.Tables.GoTo(133);
        await ScreenshotRunner.Settle(600);
        Expect(b.Tables.IsServerDatabaseTable, "server data: Spell links to the Server data tab");
        await Shot(win, outDir, "105_server_dbc_link");
        b.SelectedTab = BrowserViewModel.ServerTab;

        // ---- 106: the game's cache
        var dlg = Dialogs.Build("Clear the game's cache (WDB)",
            "The game keeps what the server told it about items, creatures, quests and objects in its WDB folder and does not ask again: after a server edit of a name, a stat or a text, the game shows the old one until the cache is cleared.\n\nThe cache files are moved into the app's backup folder (not deleted). Close the game first.",
            DialogKind.Question, new[] { "Clear this game's cache", "Choose another game folder...", "Cancel" }, session.Client!.ClientDir, null, null, out _);
        await ShotWindow(dlg, 640, 330, outDir, "106_server_wdb");

        // undo the apply on the throwaway server (it is deleted anyway)
        ap.Undo(rec);
    }
}
