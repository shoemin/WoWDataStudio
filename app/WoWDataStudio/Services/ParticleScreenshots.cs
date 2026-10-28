using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WoWData.Core.Index;
using WoWData.Core.M2;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (`WDS_SCREENSHOT_ONLY=particles` makes only these): your game's particles and ribbons in the
/// docked 3D viewer.
///   155_viewer_particles_creature  the Fire Elemental with its fire and smoke, the Particles chip on
///   156_viewer_particles_spell     the Holy Precast High Hand spell effect (light rays as particle tails, ribbons) and
///                                  the model statistics saying what is drawn approximately
/// </summary>
internal static class ParticleScreenshots
{
    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var view = win.BrowserScreen;
        var mv = b.ModelViewer;
        ScreenshotRunner.Place(win, 1500, 1100);
        b.SelectedTab = 0;
        foreach (var c in b.Chips) c.IsChecked = true;

        async Task<bool> Show(string[] queries, AssetKind kind, string file, string name)
        {
            ResultRow? row = null;
            foreach (var q in queries)
            {
                await ScreenshotRunner.Search(b, q);
                row = b.Results.FirstOrDefault(r => r.Entry.Kind == kind && r.Path.EndsWith(file, StringComparison.OrdinalIgnoreCase));
                if (row != null) break;
            }
            ScreenshotRunner.Expect(row != null, $"particles: {name} ({file}) found");
            if (row == null) return false;
            await ScreenshotRunner.Select(b, row);
            await ScreenshotRunner.WaitModel(mv);
            var fx = mv.Scene.Asset?.Model.Effects;
            ScreenshotRunner.Expect(mv.HasModel && fx != null && mv.ShowParticles, $"particles: {name} shows its effects ({fx?.Particles.Count ?? 0} emitters, {fx?.Ribbons.Count ?? 0} ribbons)");
            mv.Playing = true;
            view.ScrollToModel();
            await ScreenshotRunner.Settle(2500);
            var (live, edges) = view.ModelViewport.EffectCounts;
            ScreenshotRunner.Expect(live + edges > 0, $"particles: {name} draws {live} particles and {edges} ribbon edges");
            return true;
        }

        if (await Show(new[] { "fire elemental", "fireelemental" }, AssetKind.Creature, "FireElemental.m2", "the Fire Elemental"))
            await ScreenshotRunner.Shot(win, outDir, "155_viewer_particles_creature");

        if (await Show(new[] { "holy precast high hand", "holy_precast_high" }, AssetKind.SpellEffect, "Holy_Precast_High_Hand.m2", "Holy Precast High Hand"))
        {
            var stats = Descendants<Expander>(view.ModelViewer)
                .FirstOrDefault(e => e.Header is TextBlock t && t.Text.StartsWith("Model statistics", StringComparison.Ordinal));
            ScreenshotRunner.Expect(stats != null, "particles: the model statistics are in the 3D viewer");
            ScreenshotRunner.Expect(mv.FormatNotes.Contains("drawn by the viewer's own simulation of the 1.12 client's rules", StringComparison.Ordinal),
                $"particles: the statistics' notes say how the effects are drawn ({mv.FormatNotes.Replace('\n', ' ')})");
            // a tall window, the texture list folded: the effect, the Particles chip and the statistics with their notes
            var textures = Descendants<Expander>(view.ModelViewer)
                .FirstOrDefault(e => e.Header is TextBlock t && t.Text.StartsWith("Textures shown", StringComparison.Ordinal));
            bool statsWas = stats?.IsExpanded ?? true, texturesWas = textures?.IsExpanded ?? true;
            if (stats != null) stats.IsExpanded = true;
            if (textures != null) textures.IsExpanded = false;
            ScreenshotRunner.Place(win, 1500, 1660);
            await ScreenshotRunner.Settle(800);
            view.ScrollToModel();
            await ScreenshotRunner.Settle(600);
            // just far enough down for the notes under the statistics
            Descendants<TextBlock>(view.ModelViewer).FirstOrDefault(t => t.Text.Length > 0 && t.Text == mv.FormatNotes)?.BringIntoView();
            await ScreenshotRunner.Settle(1200);
            await ScreenshotRunner.Shot(win, outDir, "156_viewer_particles_spell");
            if (stats != null) stats.IsExpanded = statsWas;
            if (textures != null) textures.IsExpanded = texturesWas;
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
