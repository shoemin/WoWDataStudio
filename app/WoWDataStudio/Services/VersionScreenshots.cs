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
///   141_other_versions_modern     the "1.60" Classic beta (CASC): a minimap tile of Azeroth found by its number
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

        // 141: the "1.60" Classic beta (CASC): a minimap tile of Azeroth found by its number, from the WDT's MAID table
        var beta = v.Clients.FirstOrDefault(c => c.Name.Equals("_classic_beta_", StringComparison.OrdinalIgnoreCase));
        if (beta == null)
        {
            ScreenshotRunner.Log("other versions: no _classic_beta_ client in the folder; picture 141 skipped");
            return;
        }
        v.SelectedClient = beta;
        await v.Opening;
        win.BrowserScreen.VersionsScreen.ShowInnerTab(0);
        uint minimap = MinimapOfAzeroth(v);
        Expect(minimap != 0, "other versions: a minimap tile number from Azeroth's WDT");
        v.FilePattern = "#" + minimap;
        v.SearchFilesCommand.Execute(null);
        await ScreenshotRunner.Settle(800);
        Expect(v.Preview != null, "other versions: a 1.60 minimap tile is previewed");
        await Shot(win, outDir, "141_other_versions_modern");
    }

    /// <summary>The minimap texture of the first Azeroth tile that has one: MAID holds 8 FileDataIDs per tile (root, obj0,
    /// obj1, tex0, lod, map texture, map normal texture, minimap texture).</summary>
    private static uint MinimapOfAzeroth(OtherVersionsViewModel v)
    {
        try
        {
            var wdt = v.ReadForScreenshots("#775971");
            if (wdt == null) return 0;
            for (int p = 0; p + 8 <= wdt.Length;)
            {
                var magic = System.Text.Encoding.ASCII.GetString(wdt, p, 4);
                int size = BitConverter.ToInt32(wdt, p + 4);
                if (magic == "DIAM")
                {
                    // Elwynn Forest (tile 32_48: entry 48 * 64 + 32), else the first tile with a minimap
                    int elwynn = p + 8 + (48 * 64 + 32) * 32;
                    if (elwynn + 32 <= p + 8 + size && BitConverter.ToUInt32(wdt, elwynn + 28) is var me && me != 0) return me;
                    for (int e = p + 8; e + 32 <= p + 8 + size; e += 32)
                        if (BitConverter.ToUInt32(wdt, e + 28) is var m && m != 0) return m;
                }
                p += 8 + size;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
        return 0;
    }
}
