using System.Diagnostics;
using System.Windows;
using WoWData.Core.Index;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot`: the "Used in game" panel (docs\manual\used-in-game.md) for each kind of entry, and its links.
///   40_used_in_game_creature      Mottled Boar: the NPC, spawns by zone, quests, drops, same look
///   41_used_in_game_item          Weak Flux: dropped by, sold by, quests, crafted by
///   42_used_in_game_texture       BoarSkinIvory.blp: "Changing this texture affects"
///   43_used_in_game_world_object  DurotarPalm01.m2: placements by zone and map tile
///   44_used_in_game_spell_effect  Fire Precast Hand: the spells that show it and who casts them
///   45_used_in_game_game_object   Peacebloom: herb, spawns, what it gives
/// Without the server database the creature shot shows the display and the note about the server.
/// </summary>
internal static class UsedInGameScreenshots
{
    private static async Task Until(Func<bool> cond, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("the Used in game panel did not load in time");
            await Task.Delay(40);
        }
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var u = b.UsedInGame;
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = true;
        b.UsedInGameExpanded = true;

        async Task<bool> Show(string query, Func<ResultRow, bool> pick, string shot, string section)
        {
            await ScreenshotRunner.Search(b, query);
            var row = b.Results.FirstOrDefault(pick);
            ScreenshotRunner.Expect(row != null, $"used in game: '{query}' found");
            if (row == null) return false;
            await ScreenshotRunner.Select(b, row);
            await Until(() => !u.IsLoading && u.Report != null && u.Report.Key == row.Entry.Key);
            var rep = u.Report!;
            ScreenshotRunner.Expect(rep.Section(section) != null, $"used in game: {row.Entry.Key} has the '{section}' section ({rep.Sections.Count} sections, {rep.Milliseconds:0.0} ms)");
            ScreenshotRunner.Log($"used in game: {row.Entry.Key} built in {rep.Milliseconds:0.0} ms");   // timing: PerformanceTests
            await ScreenshotRunner.Settle(400);
            var box = view.UsedInGameBox;
            box.BringIntoView(new Rect(0, -40, Math.Max(1, box.ActualWidth), Math.Min(box.ActualHeight + 40, 1100)));
            await ScreenshotRunner.Shot(win, outDir, shot);
            return true;
        }

        bool server = b.ServerConnected;
        await Show("mottled boar", r => r.Entry.Kind == AssetKind.Creature && (r.Name == "Mottled Boar" || !server), "40_used_in_game_creature", server ? "spawns" : "npcs");
        if (server) await Show("weak flux", r => r.Entry.Kind == AssetKind.Item && r.Name == "Weak Flux", "41_used_in_game_item", "vendors");
        await Show("boarskinivory", r => r.Entry.Kind == AssetKind.Texture && r.Path.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase), "42_used_in_game_texture", "affects");
        await Show("durotarpalm01", r => r.Entry.Kind == AssetKind.WorldObject && r.Path.EndsWith("DurotarPalm01.m2", StringComparison.OrdinalIgnoreCase), "43_used_in_game_world_object", "placements");
        await Show("fire precast hand", r => r.Entry.Kind == AssetKind.SpellEffect && r.Name == "Fire Precast Hand", "44_used_in_game_spell_effect", "spells");
        if (server) await Show("peacebloom", r => r.Entry.Kind == AssetKind.GameObject && r.Name == "Peacebloom", "45_used_in_game_game_object", "contains");

        // the links: from the texture to a creature that uses it, and back
        await ScreenshotRunner.Search(b, "boarskinivory");
        var tex = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Texture);
        if (tex != null)
        {
            await ScreenshotRunner.Select(b, tex);
            await Until(() => !u.IsLoading && u.Report?.Key == tex.Entry.Key);
            var link = u.Report!.Sections.SelectMany(s => s.Lines).SelectMany(l => l.Spans).FirstOrDefault(s => s.Key != null && u.CanNavigate(s.Key));
            ScreenshotRunner.Expect(link != null, "used in game: the texture's report has a link");
            if (link != null)
            {
                u.NavigateCommand.Execute(link.Key);
                await Until(() => b.SelectedRow?.Entry.Key == link.Key && !u.IsLoading && u.Report?.Key == link.Key);
                ScreenshotRunner.Expect(u.CanGoBack, $"used in game: the link selected {link.Key} and Back is offered");
                u.BackCommand.Execute(null);
                await Until(() => b.SelectedRow?.Entry.Key == tex.Entry.Key);
                ScreenshotRunner.Expect(true, "used in game: Back returned to the texture");
            }
        }
        b.UsedInGameExpanded = false;
        ScreenshotRunner.Place(win, 1440, 900);
    }
}
