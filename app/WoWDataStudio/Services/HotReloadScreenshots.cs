using System.IO;
using System.Windows;
using WoWData.Core.HotReload;
using WoWData.Core.Imaging;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.11.0): hot reload ("Reload in running game"), against a stand-in for the running game
/// (<see cref="FakeGame"/>: the protocol of wowcode.dll over a real pipe; the game is never started and nothing is installed
/// into the game folder).
///   115_my_changes_hot_reload    My changes: the button and the line that says the game takes reloads
///   116_hot_reload_offer         after "Test in game": the offer to reload the files in the running game
///   117_hot_reload_result        the result: what is live now, what needs a relog or a restart, one line per file
///   118_settings_hot_reload      Settings: "Hot reload in the running game" for this game folder
/// (WDS_SCREENSHOT_ONLY=hotreload runs only these.)
/// </summary>
internal static class HotReloadScreenshots
{
    /// <summary>No game is running (the pictures of the other steps).</summary>
    internal static readonly IGameLocator NoGame = new NoGameLocator();

    private sealed class NoGameLocator : IGameLocator
    {
        public IReadOnlyList<GameProcess> Find(string clientDir) => Array.Empty<GameProcess>();
        public string PipeName(int pid) => "wds-no-game";
        public bool PipeExists(int pid) => false;
    }

    public static async Task RunAsync(Window win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var client = session.Client!.ClientDir;
        if (session.Workspace!.GameFiles().Count == 0)
        {
            // WDS_SCREENSHOT_ONLY=hotreload: one change of our own (this run's workspace only), as in the main run's step 7
            const string path = @"Creature\Boar\BoarSkinIvory.blp";
            var work = Path.Combine(outDir, "_work");
            Directory.CreateDirectory(work);
            var png = Path.Combine(work, "BoarSkinIvory_hr.png");
            session.Textures!.Export(path, png, new() { ["screenshot"] = "1" });
            var img = Png.Load(png);
            ScreenshotRunner.PaintTestEdit(img);
            Png.Save(img, png, true);
            ScreenshotRunner.Expect(await b.ImportPngAsync(png, path, interactive: false), "hot reload: a change in the workspace");
            await ScreenshotRunner.Settle(600);
        }
        using var game = new FakeGame(client);   // reads nothing in the game folder (holdPatch: false)
        b.HotReloadLocator = game;
        try
        {
            await b.RefreshHotReloadAsync();
            ScreenshotRunner.Expect(b.HotReloadReady, "hot reload: the stand-in game is found and takes reloads");
            b.SelectedTab = 1;
            await ScreenshotRunner.Shot(win, outDir, "115_my_changes_hot_reload");

            // the offer after "Test in game" (the dialog as BrowserViewModel.AfterGameFilesChangedAsync builds it)
            var dest = Path.Combine(client, "Data", "Creature", "Boar", "BoarSkinIvory.blp");
            var offer = Dialogs.Build("Installed",
                "1 file was copied into the game folder.\n\nThe game is running with hot reload: it can read the changed files now, without a restart.",
                DialogKind.Success, new[] { "Reload in running game", "Not now" }, "update: " + dest, "How hot reload works (HOT_RELOAD.md)", ScreenshotRunner.Main.HotReloadDoc, out _);
            Place(offer);
            offer.Show();
            await ScreenshotRunner.Shot(offer, outDir, "116_hot_reload_offer");
            offer.Close();

            // the result, from a real request to the stand-in game (a texture, a model, a map file, a table, an add-on file)
            var outcome = await new HotReloadService(client, game).ReloadAsync(new[]
            {
                @"Creature\Boar\BoarSkinIvory.blp", @"Creature\Boar\Boar.m2", @"World\Maps\Kalimdor\Kalimdor.wdt", @"DBFilesClient\Spell.dbc", @"Interface\AddOns\MyAddOn\MyAddOn.lua",
            });
            ScreenshotRunner.Expect(outcome.Done && outcome.Report!.Files.Count == 5, "hot reload: the stand-in game reloaded 5 files");
            var result = Dialogs.Build("Reloaded in the running game", outcome.Text, DialogKind.Info, new[] { "OK" }, HotReloadService.Details(outcome.Report!), null, null, out _);
            Place(result);
            result.Show();
            await ScreenshotRunner.Shot(result, outDir, "117_hot_reload_result");
            result.Close();

            // Settings: the switch of this game folder (the window is only shown: nothing is saved)
            var svm = new SettingsViewModel(session.Settings, client) { HotReload = true };
            var sw = new SettingsWindow(svm);
            ScreenshotRunner.Place(sw, 640, 980);
            sw.Show();
            sw.Place(640, 980);
            sw.HotReloadCard.BringIntoView();
            await ScreenshotRunner.Shot(sw, outDir, "118_settings_hot_reload");
            sw.Close();
        }
        finally
        {
            b.HotReloadLocator = NoGame;
            await b.RefreshHotReloadAsync();
        }
    }

    private static void Place(Window w)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -30000;
        w.ShowActivated = false;
    }
}
