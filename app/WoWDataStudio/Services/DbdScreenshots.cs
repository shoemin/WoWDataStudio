using System.IO;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (M5, docs\manual\other-versions.md): column names from a WoWDBDefs folder. It needs the folder
/// of client copies in WDS_SCREENSHOT_OTHER_CLIENTS (with the "1.60" Classic beta) and a WoWDBDefs folder in
/// WDS_SCREENSHOT_DBD (the user's own copy, only read); without them these pictures are skipped.
/// WDS_SCREENSHOT_ONLY=dbd makes only them. The folder is set in the run's own settings and cleared at the end.
///   162_settings_wowdbdefs              the Settings window's "Other versions" card with the WoWDBDefs folder
///   163_versions_tables_wowdbdefs       the Other versions tab: the 1.60 client's tables with their name source, Map read
///   164_versions_open_tables_wowdbdefs  1.60 opened read only: the Data tables tab with "Column names: WoWDBDefs ..."
///   165_versions_open_tables_fallback   a table whose definition does not fit: raw columns, with the reason
/// </summary>
internal static class DbdScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    public static async Task RunAsync(MainWindow win, MainViewModel main, BrowserViewModel b, string outDir)
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        var dbd = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_DBD");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || WoWData.Core.Db2.DbdFolder.Resolve(dbd) == null)
        {
            ScreenshotRunner.Log("dbd: WDS_SCREENSHOT_OTHER_CLIENTS or WDS_SCREENSHOT_DBD is not set to a folder of client copies / of WoWDBDefs definitions; pictures 162-165 skipped");
            return;
        }
        var session = main.Session;
        var own = session.Client?.ClientDir;

        // the Settings window: the WoWDBDefs folder (not saved: the run sets its own settings below)
        var svm = new SettingsViewModel(session.Settings, own ?? "") { DbdFolder = dbd! };
        Expect(svm.DbdOk && svm.DbdCheck.StartsWith("Found ", StringComparison.Ordinal), "dbd: the Settings window finds the definitions: " + svm.DbdCheck);
        var sw = new SettingsWindow(svm);
        ScreenshotRunner.Place(sw, 640, 700);
        sw.Show();
        sw.Place(640, 980);
        sw.OtherVersionsCard.BringIntoView();
        await ScreenshotRunner.Shot(sw, outDir, "162_settings_wowdbdefs");
        sw.Close();

        session.Settings.OtherClientsDbd = dbd;
        try
        {
            // the Other versions tab: the tables of 1.60 with their name source
            var v = b.Versions;
            b.SelectedTab = BrowserViewModel.VersionsTab;
            v.Folder = folder;
            await v.FindClientsCommand.ExecuteAsync(null);
            var beta = v.Clients.Where(c => c.Copy.Format == WoWData.Core.Versions.ClientFormat.Casc && c.Copy.Version.StartsWith("1.60", StringComparison.Ordinal))
                .OrderByDescending(c => c.Build).FirstOrDefault();
            if (beta == null)
            {
                Expect(false, "dbd: no 1.60 client (_classic_beta_) in the folder");
                return;
            }
            v.SelectedClient = null;
            v.SelectedClient = beta;
            await v.Opening;
            win.BrowserScreen.VersionsScreen.ShowInnerTab(1);
            Expect(v.Tables.Count(t => t.Columns.StartsWith("WoWDBDefs", StringComparison.Ordinal)) > 1000, "dbd: the Other versions tab lists the tables named by WoWDBDefs");
            v.SelectedTable = v.Tables.FirstOrDefault(t => t.Name == "Map");
            Expect(v.TableNote.Contains("Column names: WoWDBDefs", StringComparison.Ordinal), "dbd: the Map table of the Other versions tab is named by WoWDBDefs: " + v.TableNote);
            main.ToastVisible = false;
            await ScreenshotRunner.Settle(600);
            await ScreenshotRunner.Shot(win, outDir, "163_versions_tables_wowdbdefs");

            // 1.60 opened read only: the Data tables tab
            var opened = await main.OpenOtherClientAsync(beta.Copy, null);
            try
            {
                Expect(opened && main.Browser is { IsReadOnly: true }, "dbd: the 1.60 client opened read only: " + main.WelcomeError);
                if (main.Browser is not { IsReadOnly: true } vm) return;
                vm.HotReloadLocator = HotReloadScreenshots.NoGame;
                main.ToastVisible = false;
                vm.SelectedTab = BrowserViewModel.TablesTab;
                var t = vm.VersionTables;
                await t.EnsureLoadedAsync();
                Expect(t.NamesSource.StartsWith("Column names: WoWDBDefs (CC BY-SA 4.0), from ", StringComparison.Ordinal), "dbd: the Data tables tab says where the names come from: " + t.NamesSource);
                t.SelectedTable = t.Tables.FirstOrDefault(x => x.Name == "Map");
                await ScreenshotRunner.Until(() => t.Rows != null || t.TableNote.Contains("could not", StringComparison.Ordinal), 60000);
                Expect(t.Table?.Naming.Source == WoWData.Core.Versions.ColumnNameSource.WoWDBDefs, "dbd: the Map table is named by WoWDBDefs: " + t.TableNote);
                await ScreenshotRunner.Settle(600);
                await ScreenshotRunner.Shot(win, outDir, "164_versions_open_tables_wowdbdefs");

                t.TableFilter = "ExteriorComponent";
                t.SelectedTable = t.Tables.FirstOrDefault(x => x.Name == "ExteriorComponentGroup");
                await ScreenshotRunner.Until(() => t.TableNote.Contains("Column names:", StringComparison.Ordinal) || t.TableNote.Contains("could not", StringComparison.Ordinal), 60000);
                Expect(t.TableNote.Contains("not WoWDBDefs: ", StringComparison.Ordinal), "dbd: a definition that does not fit says why: " + t.TableNote);
                await ScreenshotRunner.Settle(600);
                await ScreenshotRunner.Shot(win, outDir, "165_versions_open_tables_fallback");
                t.TableFilter = "";
                vm.SelectedTab = 0;
            }
            finally
            {
                if (own != null) await main.OpenClientAsync(own, false);
                Expect(main.Browser is { IsReadOnly: false }, "dbd: back to your 1.12 game: " + main.WelcomeError);
            }
        }
        finally
        {
            session.Settings.OtherClientsDbd = null;
        }
    }
}
