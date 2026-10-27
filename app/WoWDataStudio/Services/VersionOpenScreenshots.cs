using System.IO;
using WoWData.Core.Index;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (M1: another version opened read only, docs\research\OPEN_OTHER_VERSIONS_DESIGN.md). It needs
/// the folder of client copies in WDS_SCREENSHOT_OTHER_CLIENTS with the "1.60" Classic beta (_classic_beta_); without it
/// these pictures are skipped. WDS_SCREENSHOT_ONLY=versions-open makes only them. The client is only read; its index is
/// built in the run's own temporary app folder.
///   144_versions_open_tab        the Other versions tab with the 1.60 client selected: "Open read only"
///   145_versions_open_creature   the read-only window: a creature display, its files with FileDataID, name source and
///                                state, a skin in the texture viewer, the locked counts in the status line
///   146_versions_open_item       an item the client names: its models per race and sex, its textures
///   147_versions_open_texture    an interface icon of 1.60 in the texture viewer, and what uses it
/// </summary>
internal static class VersionOpenScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static async Task Select(OtherVersionWindowViewModel v, string search, Func<IndexEntry, bool> pick, string what)
    {
        v.SearchText = search;
        await v.ApplyFilterAsync();
        var row = v.Results.FirstOrDefault(r => pick(r.Entry));
        Expect(row != null, $"read-only window: {what} is found by \"{search}\"");
        if (row == null) return;
        v.SelectedRow = row;
        await ScreenshotRunner.Until(() => v.HasSelection && v.DetailTitle.Length > 0, 30000);
        if (v.SelectedTexture is { Exists: true })
            await ScreenshotRunner.Until(() => v.Viewer.IsLoaded || v.Viewer.Error.Length > 0, 30000);
        await ScreenshotRunner.Settle(500);
    }

    public static async Task RunAsync(MainWindow win, MainViewModel main, BrowserViewModel b, string outDir)
    {
        var folder = Environment.GetEnvironmentVariable("WDS_SCREENSHOT_OTHER_CLIENTS");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            ScreenshotRunner.Log("versions-open: WDS_SCREENSHOT_OTHER_CLIENTS is not set to a folder of client copies; pictures 144-147 skipped");
            return;
        }
        var v = b.Versions;
        b.SelectedTab = BrowserViewModel.VersionsTab;
        v.Folder = folder;
        await v.FindClientsCommand.ExecuteAsync(null);
        var beta = v.Clients.Where(c => c.Copy.Format == WoWData.Core.Versions.ClientFormat.Casc && c.Copy.Version.StartsWith("1.60", StringComparison.Ordinal))
            .OrderByDescending(c => c.Build).FirstOrDefault();
        if (beta == null)
        {
            Expect(false, "versions-open: no 1.60 client (_classic_beta_) in the folder");
            return;
        }
        v.SelectedClient = beta;
        await v.Opening;
        win.BrowserScreen.VersionsScreen.ShowInnerTab(0);
        await ScreenshotRunner.Settle(500);
        Expect(v.CanConvertMaps, "versions-open: \"Open read only\" is shown for the 1.60 client");
        ScreenshotRunner.Main!.ToastVisible = false;
        await ScreenshotRunner.Shot(win, outDir, "144_versions_open_tab");

        var w = OtherVersionWindow.Open(main, beta.Copy, null, show: false);
        ScreenshotRunner.Place(w, 1440, 900);
        w.Show();
        try
        {
            var vm = w.Vm;
            await ScreenshotRunner.Until(() => vm.IsReady || vm.Error.Length > 0, 600000);
            Expect(vm.IsReady, "versions-open: the 1.60 client opened read only: " + vm.Error);
            if (!vm.IsReady) return;
            ScreenshotRunner.Log($"versions-open: {vm.Session.Entries.Count} entries; {vm.StatusLine}; {vm.NamesLine}");
            Expect(vm.Session.IsReadOnly && vm.Session.Workspace == null, "versions-open: the session is read only, without a workspace");
            Expect(vm.StatusLine.Contains("creature displays readable", StringComparison.Ordinal), "versions-open: the status line shows the locked creature displays");

            await Select(vm, "boar", e => e.Key.StartsWith("cdisplay:", StringComparison.Ordinal) && e.Path != null && !e.Path.StartsWith('#')
                                          && e.Name.Contains(" - ", StringComparison.Ordinal), "a boar creature display");
            Expect(vm.Files.Any(f => f.Role == "model"), "versions-open: the creature display lists its model with its FileDataID");
            await ScreenshotRunner.Shot(w, outDir, "145_versions_open_creature");

            await Select(vm, "worn shortsword", e => e.Key == "item:25", "Worn Shortsword (item 25)");
            Expect(vm.Textures.Count > 0, "versions-open: the item lists its textures");
            await ScreenshotRunner.Shot(w, outDir, "146_versions_open_item");

            await Select(vm, "inv_sword_04", e => e.Kind == AssetKind.Texture, "the INV_Sword_04 icon");
            Expect(vm.Viewer.IsLoaded, "versions-open: the icon is shown in the texture viewer");
            Expect(vm.UsedBy.Count > 0, "versions-open: the icon's users are listed");
            var png = Path.Combine(outDir, "_export", "INV_Sword_04.png");
            Expect(vm.ExportTo(vm.SelectedTexture!.Path, png) && File.Exists(png), "versions-open: Export texture as PNG writes the PNG");
            await ScreenshotRunner.Shot(w, outDir, "147_versions_open_texture");
        }
        finally
        {
            w.Close();
        }
    }
}
