using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WoWData.Core.Index;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot`: screens the user manual (docs\manual) needs that the main run does not show. Runs after the
/// main steps, so the Mottled Boar's skin is already edited in this run's own workspace.
///   26_texture_viewer_controls  the texture viewer with its controls (Your edit / Original, transparency, mip level, Fit, 100%)
///   27_viewer_geosets           the 3D view's geoset list and model statistics (orc male)
/// </summary>
internal static class ManualScreenshots
{
    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;

        // the texture viewer and its controls (the window keeps the 3D steps' size: shrinking an offscreen window leaves
        // part of the capture empty)
        ScreenshotRunner.Place(win, 1500, 1240);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = c.Kind == AssetKind.Texture;
        await ScreenshotRunner.Search(b, "boarskinivory");
        var tex = b.Results.FirstOrDefault(r => r.Path.EndsWith("BoarSkinIvory.blp", StringComparison.OrdinalIgnoreCase));
        ScreenshotRunner.Expect(tex != null, "manual: BoarSkinIvory.blp found");
        if (tex != null)
        {
            await ScreenshotRunner.Select(b, tex);
            view.TextureViewerBox.BringIntoView();
            await ScreenshotRunner.Shot(win, outDir, "26_texture_viewer_controls");
        }

        // the geoset list and the model statistics of a character
        foreach (var c in b.Chips) c.IsChecked = true;
        await ScreenshotRunner.Search(b, "orc male");
        var orc = b.Results.FirstOrDefault(r => r.Entry.Kind == AssetKind.Character && r.Name.Contains("Orc", StringComparison.OrdinalIgnoreCase));
        ScreenshotRunner.Expect(orc != null, "manual: orc male found");
        if (orc != null)
        {
            await ScreenshotRunner.Select(b, orc);
            await ScreenshotRunner.WaitModel(b.ModelViewer);
            var geosets = Descendants<Expander>(view.ModelViewer)
                .FirstOrDefault(e => e.Header is TextBlock t && t.Text.StartsWith("Geosets", StringComparison.Ordinal));
            ScreenshotRunner.Expect(geosets != null, "manual: the geoset list is in the 3D viewer");
            if (geosets != null)
            {
                geosets.IsExpanded = true;
                await ScreenshotRunner.Settle(400);
                geosets.BringIntoView(new Rect(0, -60, Math.Max(1, geosets.ActualWidth), Math.Min(geosets.ActualHeight + 220, 900)));
                await ScreenshotRunner.Shot(win, outDir, "27_viewer_geosets");
                geosets.IsExpanded = false;
            }
        }
        ScreenshotRunner.Place(win, 1440, 900);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
