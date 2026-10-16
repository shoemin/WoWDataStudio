using System.IO;
using System.Windows;
using WoWData.Core.Designers;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.13.0, the Quest chains tab). Like the other server pictures these steps NEVER use the
/// user's server: a throwaway MySQL server (ThrowawayMySql) loaded from the tests' fixture and its quest-chain supplement
/// (real chains: the Warrior's Whirlwind weapon, the Warlock's Dreadsteed; the Riposte Drill's outposts).
///   127_quest_chain_whirlwind   the Warrior's Whirlwind weapon chain as a flowchart, The Islander in the editor
///   128_quest_chain_dreadsteed  the Dreadsteed: "all of" groups, the checks of a real chain
///   129_quest_giver_on_map      a new quest giver at Sentinel Hill, shown on the Maps tab
///   130_quest_template_riposte  the discipline template: the Riposte Drill chain for the Alliance
///   131_quest_text_preview      the drill's texts with $N / $C / $G filled in for a female Night Elf
///   132_quest_branch_choice     a choice of three follow-ups (one exclusive group)
///   133_quest_duplicate_horde   the same chain for the Horde at the Crossroads
///   134_quest_checks            a quest broken on purpose: the checks in plain words
///   135_quest_apply             "Apply to server...": the statements of the designed chain
///   136_quest_test_in_game      "Test in game": the GM commands
/// </summary>
internal static class QuestScreenshots
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

    private static string? Find(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "app", "WoWData.Tests", "serverdb", file);
            if (File.Exists(f)) return f;
            f = Path.Combine(d.FullName, "WoWData.Tests", "serverdb", file);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var mysqld = ThrowawayMySql.FindMysqld(session.ServerFolder);
        var fixture = Find("world_fixture.sql");
        var supplement = Find("quest_chains_fixture.sql");
        if (mysqld == null || fixture == null || supplement == null)
        {
            Expect(false, $"quests: no throwaway MySQL server for the pictures (mysqld: {mysqld ?? "not found"}, fixture: {fixture ?? "not found"}, supplement: {supplement ?? "not found"})");
            return;
        }
        var keep = session.Settings.Server;
        using var db = ThrowawayMySql.Start(mysqld);
        db.CreateDatabase("wds_quests", fixture);
        db.RunSqlFile("wds_quests", supplement);
        session.Settings.Server = db.Settings("wds_quests");
        session.ResetServerData();
        session.ServerRunningProbe = () => new List<string>();
        try
        {
            b.Spells.Reset();
            await Steps(win, b, outDir);
        }
        finally
        {
            session.Settings.Server = keep;
            session.ResetServerData();
            b.Spells.Reset();
        }
    }

    private static async Task Steps(MainWindow win, BrowserViewModel b, string outDir)
    {
        ScreenshotRunner.Place(win, 1780, 1050);
        win.UpdateLayout();
        var q = b.Quests;
        b.SelectedTab = BrowserViewModel.QuestsTab;
        await q.EnsureLoadedAsync();
        Expect(q.HasServer && q.Designer!.Ed.Service.Database == "wds_quests", "quests: the pictures use the throwaway database only");

        // ---- 127: the Whirlwind weapon chain
        q.SearchText = "islander";
        await q.SearchCommand.ExecuteAsync(null);
        await q.OpenChainCommand.ExecuteAsync(null);
        Expect(q.Chain?.Nodes.Count == 7, $"quests: the Whirlwind chain has 7 quests ({q.Chain?.Nodes.Count})");
        q.ExpandOnly("givers", "Texts", "objective-words");
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await ScreenshotRunner.Settle(300);
        await Shot(win, outDir, "127_quest_chain_whirlwind");

        // ---- 128: the Dreadsteed
        await q.OpenAsync(7562);
        Expect(q.Chain!.Groups.Any(g => g.Id == -7626), "quests: the Dreadsteed shows its \"all of\" group");
        q.OpenQuest(7627);
        q.ExpandOnly("checks", "objective-words", "Objectives");
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await ScreenshotRunner.Settle(300);
        await Shot(win, outDir, "128_quest_chain_dreadsteed");

        // ---- 129: a new quest giver at Sentinel Hill, on the map
        q.OpenLeft("npc");
        q.NpcName = "Drill Sergeant Halvor";
        q.NpcSubname = "Drill Sergeant";
        q.NpcCopyFrom = "821";
        q.NpcMap = "0";
        q.NpcX = "-10508";
        q.NpcY = "1053";
        q.MakeNpcCommand.Execute(null);
        Expect(!q.StatusIsError && q.NpcMade == "900000", $"quests: the drill sergeant is made ({q.Status})");
        q.NpcName = "Champion Gorrak";
        q.NpcSubname = "Crossroads Champion";
        q.NpcCopyFrom = "3429";
        q.NpcMap = "1";
        q.NpcX = "-470";
        q.NpcY = "-2620";
        q.MakeNpcCommand.Execute(null);
        Expect(!q.StatusIsError && q.NpcMade == "900001", $"quests: the champion is made ({q.Status})");
        var sp = q.Designer!.Spawns("creature", 900000).Single();
        var gryan = q.Designer.Spawns("creature", 234).First();
        b.SelectedTab = BrowserViewModel.MapsTab;
        await b.Maps.ShowPinsAsync(0, new List<(double, double, string, bool)> { (sp.X, sp.Y, "Drill Sergeant Halvor (new)", true), (gryan.X, gryan.Y, "Gryan Stoutmantle", false) }, "2 quest givers at Sentinel Hill, Westfall.");
        await ScreenshotRunner.Until(() => b.Maps.Overview != null && b.Maps.HasTile, 600000);
        await ScreenshotRunner.Settle(1500);
        Expect(b.Maps.HasPins, "quests: the pins are on the map");
        await Shot(win, outDir, "129_quest_giver_on_map");
        b.SelectedTab = BrowserViewModel.QuestsTab;

        // ---- 130: the discipline template
        q.OpenLeft("template");
        q.TplName = "Riposte Drill";
        q.TplFaction = "Alliance";
        q.TplLevel = "20";
        q.TplGiver = "900000";
        q.TplIntro = "234";
        q.TplOutpost = "Sentinel Hill";
        q.TplKill = "122";
        q.TplCount = "8";
        q.TplSpell = "";
        q.TplCloneSpell = "12295";
        q.TplXp = "1450";
        q.MakeTemplateCommand.Execute(null);
        Expect(!q.StatusIsError, $"quests: the template made the chain ({q.Status})");
        long intro = q.Chain!.Nodes.Single(n => n.Title.StartsWith("Riposte Drill:", StringComparison.Ordinal)).Id;
        long drill = q.Chain.Nodes.Single(n => n.Title == "Riposte Drill").Id;
        q.OpenQuest(drill);
        q.ExpandOnly("givers", "objective-words", "Rewards", "checks");
        Expect(!q.QuestProblems.Any(p => p.IsError), "quests: the Riposte Drill has no errors");
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await Shot(win, outDir, "130_quest_template_riposte");

        // ---- 131: the text preview
        q.ExpandOnly("Texts", "preview", "checks");
        q.PreviewName = "Shandris";
        q.PreviewRace = "Night Elf";
        q.PreviewClass = "Warrior";
        q.PreviewFemale = true;
        var details = q.Groups.SelectMany(g => g.Fields).First(f => f.F.Id == "details");
        details.Text = "The Riposte Drill is not learned on a training dummy, $N. Out there are Defias Highwaymen; face 8 of them and come back to me, $gson:daughter;.$B$BThen I will teach you the rest, $c.";
        details.Commit();
        Expect(q.PreviewText.Contains("daughter") && q.PreviewText.Contains("Shandris"), "quests: the preview fills in $N and $G");
        await ScreenshotRunner.Settle(600);
        await Shot(win, outDir, "131_quest_text_preview");

        // ---- 132: a choice of three follow-ups
        q.OpenLeft("branch");
        q.BranchTitles = "The Sword Path\nThe Axe Path\nThe Mace Path";
        q.BranchRewards = "25,117,159";
        q.BranchChoice = true;
        q.AddBranchCommand.Execute(null);
        Expect(!q.StatusIsError && q.Chain!.Groups.Count(g => g.IsChoice) == 1, $"quests: one choice group ({q.Status})");
        q.ExpandOnly("Chain", "Rewards");
        q.OpenQuest(q.Chain.Groups.Single(g => g.IsChoice).Quests[1]);
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await Shot(win, outDir, "132_quest_branch_choice");

        // ---- 133: the same chain for the Horde at the Crossroads
        q.OpenQuest(intro);
        q.OpenLeft("faction");
        q.DuplicateFaction = "Horde";
        q.DuplicateSwaps = "900000=900001\n234=3429\n122=3258\nzone:40=17\ntext:Sentinel Hill=the Crossroads\ntext:Gryan Stoutmantle=Thork\ntext:Defias Highwaymen=Bristleback Hunters";
        q.DuplicateChainCommand.Execute(null);
        Expect(!q.StatusIsError && q.Chain!.Lanes.Single().Name == "Horde · Warrior", $"quests: the copy is the Horde's ({q.Status})");
        Expect(!q.ChainProblems.Any(p => p.IsError), "quests: the Horde copy has no errors");
        q.ExpandOnly("givers", "Who can take it");
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await Shot(win, outDir, "133_quest_duplicate_horde");
        long hordeIntro = q.Chain!.Start;

        // ---- 134: a quest broken on purpose: the checks
        q.OpenLeft("add");
        q.NewTitle = "An Unfinished Errand";
        q.OpenQuest(intro);
        q.NewQuestCommand.Execute(null);
        long broken = q.CurrentId;
        var d = q.Designer!;
        d.Set(broken, "races", "Horde");
        d.Set(broken, "level", "20");
        d.Set(broken, "prev-quest", drill.ToString());
        d.SetRelation(broken, "234", giver: true);
        d.SetItem(broken, 1, 117, 5);
        d.Set(broken, "title", "An Unfinished Errand Across the Whole of Westfall and Beyond the Hills");
        q.OpenQuest(broken);
        q.RefreshChain();
        q.ExpandOnly("checks", "givers");
        Expect(q.QuestProblems.Count(p => p.IsError) >= 3, $"quests: the broken quest has errors ({q.QuestProblems.Count})");
        await ScreenshotRunner.Settle(700);
        win.BrowserScreen.QuestsScreen.FitChain();
        await Shot(win, outDir, "134_quest_checks");
        Expect(d.Delete(broken).Ok, "quests: the broken quest is deleted again");

        // ---- 135: save, and "Apply to server..." (the throwaway server)
        await q.SaveCommand.ExecuteAsync(null);
        Expect(!q.StatusIsError, $"quests: saved ({q.Status})");
        var ap = new ServerApplier(b.Quests.Designer!.Ed.Service);
        var plan = ap.Plan();
        Expect(plan.Conflicts.Count == 0 && plan.Script.Rows >= 10, $"quests: the apply plan has {plan.Script.Rows} rows");
        await ShotWindow(new ServerApplyWindow(plan), 1000, 700, outDir, "135_quest_apply");
        var (rec, done) = ap.Apply(plan);
        Expect(done.Any(x => x.Contains("all as you edited them")), "quests: applied to the throwaway server and read back");

        // ---- 136: "Test in game"
        await q.OpenAsync(drill);
        var lines = q.Designer!.TestInGame(drill);
        Expect(lines.Any(l => l.Contains($".quest add {drill}")), "quests: the test-in-game steps name the GM commands");
        var dlg = Dialogs.Build("Test in game", string.Join("\n", lines), DialogKind.Info, new[] { "OK" },
            "The game is never started by the app. GM commands need a GM account (account level 3 or more on your server).", null, null, out _);
        await ShotWindow(dlg, 900, 470, outDir, "136_quest_test_in_game");
        Expect(ap.UndoConflicts(rec).Count == 0, "quests: nothing changed on the server since the apply");
        ap.Undo(rec);
        _ = hordeIntro;
    }
}
