using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WoWData.Cli;
using WoWData.Core;
using WoWData.Core.Features;
using WoWData.Core.Imaging;
using WoWData.Core.Mpq;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;

namespace WoWData.Tests;

/// <summary>
/// 0.8.0: the desktop app and the command line give the same results. The same edits are made once through the app's
/// view models (headless, on an STA thread, as the app runs them) and once through wds, each in its own isolated home and
/// fake game folder; the workspace files, the change records and the release patch's contents must be identical.
/// </summary>
[Collection(IndexCollection.Name)]
public class GuiCliParityTests
{
    private const string Boar = @"Creature\Boar\BoarSkinIvory.blp";
    private const string Spell = @"DBFilesClient\Spell.dbc";

    private static string RedStripe(string png)
    {
        var img = Png.Load(png);
        for (int y = 64; y < 128; y++)
            for (int x = 32; x < 160; x++)
            {
                int o = img.Offset(x, y);
                if (img.Pixels[o + 3] == 0) continue;
                img.Pixels[o] = 255; img.Pixels[o + 1] = 0; img.Pixels[o + 2] = 0;
            }
        Png.Save(img, png, true);
        return png;
    }

    private static Dictionary<string, byte[]> PatchContents(string archive, IEnumerable<string> files)
    {
        using var a = new MpqArchive(archive);
        return files.ToDictionary(f => f.ToUpperInvariant(), f => a.Read(f)!);
    }

    [Fact]
    public void TextureImportDbcEditAndPatchBuildAreTheSameInTheAppAndOnTheCommandLine()
    {
        using var gui = new CliTestEnv("parity_gui");
        using var cli = new CliTestEnv("parity_cli");
        // the same edited PNG for both (exported once, from the game)
        var png = Path.Combine(gui.Work, "boar.png");
        Assert.Equal(0, gui.Run("textures", "export", Boar, "--out", png).Code);
        RedStripe(png);
        var png2 = Path.Combine(cli.Work, "boar.png");
        File.Copy(png, png2);
        File.Copy(TextureService.SidecarFor(png), TextureService.SidecarFor(png2));

        // ---- the app: the Browse tab's import and the Data tables tab's edit, through their view models
        PatchBuild? guiBuild = null;
        UiTestsHelper.RunSta(() =>
        {
            var paths = new AppPaths(gui.Home);
            using var session = new AppSession(paths, AppSettings.Load(paths));
            session.OpenAsync(gui.Client, false, null).GetAwaiter().GetResult();
            var main = new MainViewModel(session);
            var browser = new BrowserViewModel(session, main);
            Assert.True(browser.ImportPngAsync(png, Boar, interactive: false).GetAwaiter().GetResult());
            var tables = browser.Tables;
            tables.EnsureLoadedAsync().GetAwaiter().GetResult();
            var item = tables.AllTables.First(t => t.Name == "Spell");
            tables.SelectTableAsync(item).GetAwaiter().GetResult();
            for (int i = 0; i < 100 && (tables.Table?.Name != "Spell" || tables.Rows.Count == 0); i++) Thread.Sleep(50);
            var rv = tables.Rows.First(r => tables.Table!.Key(r.Row) == 133);
            int col = tables.Table!.Layout.Columns.ToList().FindIndex(c => c.Name == "SpellIconID");
            Assert.True(tables.TryEditCell(rv, col, "184"), tables.Status);
            Assert.True(tables.SaveCore(interactive: false));
            guiBuild = ChangeOperations.BuildPatch(session.Workspace!, install: false).Build;   // what the build window's button runs
        });

        // ---- the command line
        Assert.Equal(0, cli.Run("textures", "import", png2).Code);
        Assert.Equal(0, cli.Run("dbc", "set", "Spell", "133", "SpellIconID", "184").Code);
        var built = cli.Json("changes", "build-patch");
        Assert.Equal(0, built.Code);

        // ---- identical workspaces
        var gw = new Workspace(gui.Paths);
        var cw = new Workspace(cli.Paths);
        Assert.Equal(gw.ModifiedPaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase), cw.ModifiedPaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        foreach (var p in new[] { Boar, Spell })
        {
            Assert.Equal(gw.ReadEdited(p), cw.ReadEdited(p));
            var (g, c) = (gw.Get(p)!, cw.Get(p)!);
            Assert.Equal(g.WorkspaceSha256, c.WorkspaceSha256);
            Assert.Equal(g.Kind, c.Kind);
            Assert.Equal(g.Summary, c.Summary);
            Assert.Equal(g.Original.Sha256, c.Original.Sha256);
            Assert.Equal(g.Original.Format, c.Original.Format);
            Assert.Equal(gw.Manifest.ChangeSets[g.ChangeSet].Label, cw.Manifest.ChangeSets[c.ChangeSet].Label);
        }
        // ---- identical release patches (the same files with the same bytes)
        var cliPatch = built.Data.GetProperty("path").GetString()!;
        Assert.Equal(guiBuild!.Files.OrderBy(f => f), built.Data.GetProperty("files").EnumerateArray().Select(x => x.GetString()!).OrderBy(f => f));
        var a = PatchContents(guiBuild.Path, guiBuild.Files);
        var b = PatchContents(cliPatch, guiBuild.Files);
        foreach (var (k, v) in a) Assert.Equal(v, b[k]);
        Assert.Equal(File.ReadAllBytes(guiBuild.Path), File.ReadAllBytes(cliPatch));   // the writer is deterministic: byte for byte
    }
}

internal static class UiTestsHelper
{
    public static void RunSta(Action a) => CompareTests.RunSta(a);
}

/// <summary>
/// 0.8.0: the feature registry (WoWData.Core.Features.FeatureRegistry) keeps the two front ends in step: every feature
/// has its command (or a reason it is app-only), every command belongs to a feature, every app label exists in the app,
/// the command line references no WPF, and both programs carry the same version.
/// </summary>
public class FeatureParityTests
{
    [Fact]
    public void EveryFeatureHasItsCommandAndEveryCommandItsFeature()
    {
        var problems = new List<string>();
        var ids = FeatureRegistry.All.Select(f => f.Id).ToList();
        foreach (var d in ids.GroupBy(i => i).Where(g => g.Count() > 1)) problems.Add($"feature id {d.Key} is listed twice");
        foreach (var f in FeatureRegistry.All)
        {
            if (f.HasCli && CliApp.Find(f.Cli) == null) problems.Add($"{f.Id}: the command \"wds {f.Cli}\" does not exist");
            if (!f.HasCli && f.Cli.Length > 0) problems.Add($"{f.Id}: an app-only feature names a command");
            if (f.HasGui && f.Gui.Length == 0) problems.Add($"{f.Id}: no label in the app");
            if (f.Front != FeatureFront.Both && f.Reason.Length < 10) problems.Add($"{f.Id}: a one-sided feature needs its reason");
            if (f.Front == FeatureFront.GuiOnly && !f.Reason.Contains("viewing", StringComparison.Ordinal) && !f.Reason.Contains("navigation", StringComparison.Ordinal) && !f.Reason.Contains("unsaved", StringComparison.Ordinal))
                problems.Add($"{f.Id}: app-only features are viewing, navigation or the editor's unsaved state (say which)");
        }
        foreach (var c in CliApp.Tree)
            if (!FeatureRegistry.ForCommand(c.Path).Any()) problems.Add($"the command \"wds {c.Path}\" belongs to no feature of the registry (add it, or mark it CliOnly with a reason)");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void EveryAppLabelOfTheRegistryExistsInTheApp()
    {
        var labels = ManualTests.Labels.Value;
        var missing = FeatureRegistry.All.Where(f => f.HasGui)
            .SelectMany(f => f.Gui.Split(" > ").Select(p => (f.Id, Part: p.Trim())))
            .Where(x => !labels.Has(x.Part)).Select(x => $"{x.Id}: \"{x.Part}\"").ToList();
        Assert.True(missing.Count == 0, "These app labels of the feature registry are not in the app's XAML or C#:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TheCommandLineHasNoWpfAndTheSameVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(Oracle.AppDir, "WoWData.Cli", "WoWData.Cli.csproj"));
        Assert.DoesNotMatch(@"<UseWPF>\s*true", csproj);
        Assert.DoesNotContain("WoWDataStudio.csproj", csproj);
        var cli = typeof(CliApp).Assembly;
        Assert.DoesNotContain(cli.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "WoWDataStudio");
        string Info(System.Reflection.Assembly a) => a.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        Assert.Equal(Info(typeof(StudioInfo).Assembly), Info(cli));
        Assert.Equal(Info(typeof(StudioInfo).Assembly), Info(typeof(MainViewModel).Assembly));
        foreach (var proj in new[] { "WoWDataStudio", "WoWData.Core", "WoWData.Cli" })
            Assert.DoesNotMatch(@"<Version>", Regex.Replace(File.ReadAllText(Path.Combine(Oracle.AppDir, proj, proj + ".csproj")), @"<PackageReference[^>]*>", ""));
        Assert.Contains(StudioInfo.Version, BrowserViewModel.AboutText());
    }
}
