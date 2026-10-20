using System.IO;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.14.0, the Other versions tab). It needs copies of other clients: the folder comes from the
/// environment variable WDS_SCREENSHOT_OTHER_CLIENTS (with a 2.4.3 and a 3.3.5a copy, as on the developer's machine);
/// without it these pictures are skipped. The clients are only read.
///   137_other_versions_archives   the clients found, the 3.3.5a archives in their order (a server's patch-7 marked)
///   138_other_versions_files      Northrend ground textures of 3.3.5a, one in the preview
///   139_other_versions_tables     the Map table of 3.3.5a with its named columns
///   140_other_versions_compare    one spell compared in 1.12.1, 2.4.3 and 3.3.5a, only the differences
/// </summary>
internal static class VersionScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static Task Shot(MainWindow win, string outDir, string name)
    {
        ScreenshotRunner.Main!.ToastVisible = false;
        return ScreenshotRunner.Shot(win, outDir, name);
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ScreenshotRunner.Log("other versions: WDS_SCREENSHOT_OTHER_CLIENTS is not set to a folder of client copies; pictures 137-140 skipped");
            return;
        }
        var v = b.Versions;
        b.SelectedTab = BrowserViewModel.VersionsTab;
        v.Folder = folder;
        await v.FindClientsCommand.ExecuteAsync(null);
        await ScreenshotRunner.Settle(400);
        var wotlk = v.Clients.FirstOrDefault(c => c.Build == 12340 && c.Copy.Readable);
        Expect(v.Clients.Count >= 3, $"other versions: clients found ({v.Clients.Count})");
        if (wotlk == null)
        {
            Expect(false, "other versions: no readable 3.3.5a (12340) client in the folder");
            return;
        }
        v.BlizzardOnly = false;
        v.SelectedClient = wotlk;
        await v.Opening;
        win.BrowserScreen.VersionsScreen.ShowInnerTab(2);
        await ScreenshotRunner.Settle(500);
        Expect(v.Archives.Count >= 10, "other versions: the 3.3.5a archives are listed");
        await Shot(win, outDir, "137_other_versions_archives");

        win.BrowserScreen.VersionsScreen.ShowInnerTab(0);
        v.FilePattern = @"TILESET\Expansion02\StormPeaks\*.blp";
        v.SearchFilesCommand.Execute(null);
        v.SelectedFile = v.Files.FirstOrDefault(f => f.Path.EndsWith("SP_SnowA.blp", StringComparison.OrdinalIgnoreCase)) ?? v.Files.FirstOrDefault();
        await ScreenshotRunner.Settle(600);
        Expect(v.Preview != null, "other versions: a Storm Peaks texture is previewed");
        await Shot(win, outDir, "138_other_versions_files");

        win.BrowserScreen.VersionsScreen.ShowInnerTab(1);
        v.SelectedTable = v.Tables.FirstOrDefault(t => t.Name.Equals("Map", StringComparison.OrdinalIgnoreCase));
        await ScreenshotRunner.Settle(700);
        Expect(v.TableRows != null, "other versions: the Map table of 3.3.5a is shown");
        await Shot(win, outDir, "139_other_versions_tables");

        win.BrowserScreen.VersionsScreen.ShowInnerTab(3);
        v.CompareTable = "Spell";
        v.CompareId = "133";
        v.CompareOnlyDifferences = true;
        await v.CompareCommand.ExecuteAsync(null);
        await ScreenshotRunner.Settle(600);
        Expect(v.CompareRows != null, "other versions: Spell 133 is compared");
        await Shot(win, outDir, "140_other_versions_compare");
    }
}
