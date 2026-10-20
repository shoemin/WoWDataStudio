using System.IO;
using WoWData.Core.ServerData;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.12.0, the designers). Like the Server data pictures these steps NEVER use the user's server:
/// a throwaway MySQL server (ThrowawayMySql) loaded from the tests' fixture, the run's settings pointed at it.
///   119_spell_designer_clone     Shadow Bolt cloned into "Shadow Lance": the form, the tooltip preview
///   120_spell_designer_effects   effect 2 (a slow) with its explanation, and the checks
///   121_spell_designer_visual    the visual composed from other spells' parts, an effect model in 3D
///   122_spell_designer_learning  taught by a trainer at level 20 (its learn spell) and in a skill line
///   123_talent_tree_stock        the Warrior's Arms tree on the grid, with arrows
///   124_talent_tree_disciplines  the worked example: a "Stance Disciplines" tree for the Warrior
///   125_class_spells_by_level    the Warlock's spells by level (trainers, skill lines, talents)
///   126_new_class_experimental   a new class (experimental) and what it still needs in code
/// </summary>
internal static class DesignerScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static Task Shot(MainWindow win, string outDir, string name)
    {
        ScreenshotRunner.Main!.ToastVisible = false;
        return ScreenshotRunner.Shot(win, outDir, name);
    }

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

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var mysqld = ThrowawayMySql.FindMysqld(session.ServerFolder);
        var fixture = Fixture();
        if (mysqld == null || fixture == null)
        {
            Expect(false, $"designers: no throwaway MySQL server for the pictures (mysqld: {mysqld ?? "not found"}, fixture: {fixture ?? "not found"})");
            return;
        }
        var keep = session.Settings.Server;
        // 123 first, without a server database: the stock tree as the game's tables have it (the tests' fixture holds only a
        // sample of spell_template, so with it the stock talents' spells would be reported missing on the server)
        session.Settings.Server = keep.Clone();
        session.Settings.Server.Enabled = false;
        session.ResetServerData();
        b.Spells.Reset();
        try { await StockTree(win, b, outDir); }
        finally { session.Settings.Server = keep; }
        using var db = ThrowawayMySql.Start(mysqld);
        db.CreateDatabase("wds_designers", fixture);
        session.Settings.Server = db.Settings("wds_designers");
        session.ResetServerData();
        session.ServerRunningProbe = () => new List<string>();
        try
        {
            // Nartok of the Valley of Trials teaches warlocks from his own list in these pictures
            using (var c = new MySqlConnector.MySqlConnection($"Server=127.0.0.1;Port={db.Port};User ID=root;Password=;Database=wds_designers;SslMode=None;Pooling=False"))
            {
                c.Open();
                using var cmd = new MySqlConnector.MySqlCommand("UPDATE creature_template SET npc_flags = npc_flags | 16, trainer_type = 0, trainer_class = 9, trainer_id = 0 WHERE entry = 3156", c);
                cmd.ExecuteNonQuery();
            }
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

    /// <summary>123: the Warrior's Arms tree on the grid (no server database).</summary>
    private static async Task StockTree(MainWindow win, BrowserViewModel b, string outDir)
    {
        ScreenshotRunner.Place(win, 1680, 1050);
        win.UpdateLayout();
        var ct = b.Classes;
        b.SelectedTab = BrowserViewModel.ClassesTab;
        await ct.EnsureLoadedAsync();
        ct.SelectedClass = ct.Classes.First(c => c.Id == 1);
        ct.SelectTree(161);
        ct.SelectCellCommand.Execute(ct.Cells.First(c => c.Talent?.Id == 135));
        Expect(ct.Arrows.Count >= 4 && ct.Cells.Count(c => c.HasTalent) == 18, $"designers: Arms shows 18 talents and its arrows ({ct.Arrows.Count})");
        Expect(!ct.TreeProblems.Any(p => p.IsError), "designers: the stock Arms tree has no errors");
        win.BrowserScreen.ClassesScreen.ShowInnerTab(0);
        await ScreenshotRunner.Settle(800);
        await Shot(win, outDir, "123_talent_tree_stock");
    }

    private static async Task Steps(MainWindow win, BrowserViewModel b, string outDir)
    {
        ScreenshotRunner.Place(win, 1680, 1050);
        win.UpdateLayout();
        var sp = b.Spells;
        b.SelectedTab = BrowserViewModel.SpellsTab;
        await sp.EnsureLoadedAsync();
        Expect(sp.Session!.HasServer && sp.Session.Server!.Service.Database == "wds_designers", "designers: the pictures use the throwaway database only");

        // ---- 119: clone Shadow Bolt into Shadow Lance
        sp.SearchText = "shadow bolt";
        await sp.SearchCommand.ExecuteAsync(null);
        sp.SelectedResult = sp.Results.First(r => r.Id == 686);
        sp.NewName = "Shadow Lance";
        sp.NewRank = "Rank 1";
        await sp.CloneCommand.ExecuteAsync(null);
        Expect(sp.CurrentId == 35000, $"designers: the clone gets the first free ID of the spell ID range ({sp.CurrentId})");
        SpellFieldView F(string id) => sp.Groups.SelectMany(g => g.Fields).First(f => f.F.Id == id);
        sp.SetField(F("cast-time"), "2");
        sp.SetField(F("cooldown"), "6");
        sp.SetField(F("range"), "35");
        sp.SetField(F("effect1-value"), "40-48");
        sp.SetField(F("description"), "Hurls a lance of shadow at the enemy, causing $s1 Shadow damage.");
        Expect(sp.TooltipText.Contains("40 to 48"), $"designers: the tooltip shows the new damage ({sp.TooltipText})");
        await ScreenshotRunner.Settle(500);
        await Shot(win, outDir, "119_spell_designer_clone");

        // ---- 120: a second effect (a slow), explained, and the checks
        sp.SetField(F("effect2"), "6");
        Expect(sp.Problems.Any(p => p.IsError && p.Message.Contains("needs an aura type")), "designers: an aura effect without an aura type is an error");
        sp.SetField(F("effect2-aura"), "33");
        sp.SetField(F("effect2-value"), "-30");
        sp.SetField(F("effect2-target-a"), "6");
        sp.SetField(F("duration"), "4");
        sp.SetField(F("description"), "Hurls a lance of shadow at the enemy, causing $s1 Shadow damage and slowing its movement by $s2% for $d.");
        sp.SetField(F("aura-text"), "Movement slowed by $s2%.");
        foreach (var g in sp.Groups) g.IsExpanded = g.Name is "Effect 2";
        sp.Refresh();
        Expect(sp.TooltipText.Contains("slowing its movement by 30% for 4 sec"), $"designers: the slow is in the tooltip ({sp.TooltipText})");
        Expect(!sp.Problems.Any(p => p.IsError), "designers: Shadow Lance has no errors");
        await ScreenshotRunner.Settle(500);
        await Shot(win, outDir, "120_spell_designer_effects");

        // ---- 121: the visual: Fireball's missile and impact on Shadow Bolt's cast, an effect model in 3D
        sp.VisualSearch = "fireball";
        await sp.FindVisualsCommand.ExecuteAsync(null);
        sp.SelectedVisual = sp.VisualResults.First();
        sp.ComposeSlot = "missile";
        sp.UseVisualPartCommand.Execute(null);
        sp.ComposeSlot = "impact";
        sp.UseVisualPartCommand.Execute(null);
        Expect(sp.VisualParts.Any(p => p.StartsWith("Missile", StringComparison.Ordinal)), "designers: the visual has a missile");
        sp.SelectedModel = sp.VisualModels.FirstOrDefault(m => m.Slot == "missile");
        await sp.ShowModelCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => !sp.Viewer.IsLoading, 30000);
        Expect(sp.Viewer.HasModel, $"designers: the missile model shows in 3D ({sp.Viewer.Message})");
        win.BrowserScreen.SpellsScreen.EffectViewer.BringIntoView();
        await ScreenshotRunner.Settle(1200);
        await Shot(win, outDir, "121_spell_designer_visual");
        win.BrowserScreen.SpellsScreen.TooltipBox.BringIntoView();

        // ---- 122: learning: Nartok at level 20 for 25 silver, the Destruction skill line for warlocks
        sp.TrainerText = "3156";
        sp.TrainerLevel = "20";
        sp.TrainerCost = "25s";
        sp.AddToTrainerCommand.Execute(null);
        Expect(!sp.StatusIsError && sp.Trainers.Any(t => t.Contains("Nartok")), $"designers: Nartok teaches it ({sp.Status})");
        sp.SkillLineText = "593";
        sp.SkillClasses = "Warlock";
        sp.AddToSkillLineCommand.Execute(null);
        Expect(!sp.StatusIsError, "designers: in the Destruction skill line: " + sp.Status);
        win.BrowserScreen.SpellsScreen.LearningBox.BringIntoView();
        await ScreenshotRunner.Settle(700);
        await Shot(win, outDir, "122_spell_designer_learning");
        await sp.SaveCommand.ExecuteAsync(null);
        Expect(!sp.StatusIsError, "designers: saved: " + sp.Status);

        // ---- 124: the worked example: Stance Disciplines (a 4th tree, one column per stance)
        var ct = b.Classes;
        b.SelectedTab = BrowserViewModel.ClassesTab;
        await ct.EnsureLoadedAsync();
        ct.SelectedClass = ct.Classes.First(c => c.Id == 1);
        win.BrowserScreen.ClassesScreen.ShowInnerTab(0);
        ct.NewTreeName = "Stance Disciplines";
        ct.AddTreeCommand.Execute(null);
        ct.TreeIcon = "Ability_Warrior_DefensiveStance";
        ct.TreeBackground = "WarriorProtection";
        ct.ApplyTreeCommand.Execute(null);
        var made = new List<(string Name, int Tier, int Col)> { ("Riposte Drill", 0, 0), ("Iron Line", 0, 1), ("Red Mist", 0, 2), ("Rending Cut", 1, 0), ("Bulwark Drill", 1, 1), ("Interceptor's Drill", 1, 2), ("Stance Mastery", 2, 3) };
        foreach (var (name, tier, col) in made)
        {
            ct.SelectCellCommand.Execute(ct.Cells.First(c => c.Tier == tier && c.Column == col));
            ct.RankTemplate = "12295";
            ct.RankName = name;
            ct.RankCount = "1";
            ct.RankValues = "";
            ct.MakeRankSpellsCommand.Execute(null);
            ct.AddTalentHereCommand.Execute(null);
            Expect(!ct.StatusIsError, $"designers: {name} placed: {ct.Status}");
        }
        long riposte = ct.Cells.First(c => c.Tier == 0 && c.Column == 0).Talent!.Id;
        long iron = ct.Cells.First(c => c.Tier == 0 && c.Column == 1).Talent!.Id;
        ct.SelectCellCommand.Execute(ct.Cells.First(c => c.Tier == 1 && c.Column == 0));
        ct.TalentPrereq = $"{riposte}:1";
        ct.ApplyTalentCommand.Execute(null);
        ct.SelectCellCommand.Execute(ct.Cells.First(c => c.Tier == 1 && c.Column == 1));
        ct.TalentPrereq = $"{iron}:1";
        ct.ApplyTalentCommand.Execute(null);
        Expect(ct.Arrows.Count >= 2, "designers: the disciplines show their arrows");
        await ScreenshotRunner.Settle(800);
        await Shot(win, outDir, "124_talent_tree_disciplines");
        await ct.SaveCommand.ExecuteAsync(null);
        Expect(!ct.StatusIsError, "designers: the tree saved: " + ct.Status);

        // ---- 125: the Warlock's spells by level
        ct.SelectedClass = ct.Classes.First(c => c.Id == 9);
        win.BrowserScreen.ClassesScreen.ShowInnerTab(1);
        ct.SelectedSpell = ct.Spells.FirstOrDefault(s => s.Spell == 35000);
        Expect(ct.Spells.Any(s => s.Spell == 35000 && s.Level == 20 && s.Source == WoWData.Core.Designers.SpellSource.Trainer), "designers: Shadow Lance comes from Nartok at level 20");
        await ScreenshotRunner.Settle(800);
        await Shot(win, outDir, "125_class_spells_by_level");

        // ---- 126: a new class (experimental) and the engine report
        win.BrowserScreen.ClassesScreen.ShowInnerTab(3);
        ct.NewClassName = "Knight";
        ct.NewClassToken = "KNIGHT";
        ct.CopyFrom = ct.Classes.First(c => c.Id == 2);
        foreach (var r in ct.Races) r.IsChecked = r.Id == 1;
        ct.CreateClassCommand.Execute(null);
        Expect(ct.NewClassDone.Count >= 4 && ct.EngineItems.Any(e => e.Where.Contains("SharedDefines.h")), "designers: the new class's data and the report");
        await ScreenshotRunner.Settle(800);
        await Shot(win, outDir, "126_new_class_experimental");
        await ct.DiscardCommand.ExecuteAsync(null);
        win.BrowserScreen.ClassesScreen.ShowInnerTab(0);
    }
}
