using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WoWData.Core.ModelIO;
using WoWData.Core.Session;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using WoWDataStudio.Helpers;
using WoWDataStudio.ViewModels;
using static WoWData.Tests.SyntheticClients;

namespace WoWData.Tests;

/// <summary>
/// Another version opened read only IN THE MAIN WINDOW (docs\manual\other-versions.md), through the app's own view
/// models on the synthetic "1.60" client (our own bytes) and a linked copy of your 1.12 game (never the user's settings):
/// the switch from your game to the other version and back, the same tabs, every write greyed out with the one reason
/// the command line prints too, "no server connected" from the version's server provider, the exports to a folder,
/// and your 1.12 game exactly as before.
/// </summary>
public class MainWindowVersionTests : IDisposable
{
    private readonly string _root = TestEnv.TempDir("mainver");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Runs async view-model code on an STA thread with a WPF dispatcher (continuations come back to it, as in the app).</summary>
    private static void OnDispatcher(Func<Task> work)
    {
        CompareTests.RunSta(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d));
            var t = work();
            var frame = new DispatcherFrame();
            t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            t.GetAwaiter().GetResult();
        });
    }

    private static async Task Until(Func<bool> ok, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ok() && sw.ElapsedMilliseconds < ms) await Task.Delay(20);
    }

    private (ClientCopy Copy, string Listfile) Beta()
    {
        var (flavor, lf) = WriteClassicBeta(Path.Combine(_root, "bnet"));
        return (ClientCopies.Inspect(flavor)!, lf);
    }

    [Fact]
    public void TheMainWindowSwitchesToAnotherVersionReadOnlyAndBackToYourGameUnchanged()
    {
        using var env = new CliTestEnv("mainver");
        var (copy, lf) = Beta();
        var before = Directory.EnumerateFiles(copy.Folder, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length)).OrderBy(x => x.f).ToList();
        OnDispatcher(async () =>
        {
            var paths = new AppPaths(env.Home);
            using var session = new AppSession(paths, AppSettings.Load(paths));
            session.Settings.OtherClientsListfile = lf;   // the cube's names (as with your listfile)
            var main = new MainViewModel(session);

            // ---- your 1.12 game: nothing is greyed out, nothing of the read-only parts shows
            await main.OpenClientAsync(env.Client, false);
            var own = main.Browser!;
            Assert.False(own.IsReadOnly || main.IsReadOnly);
            Assert.Null(own.WriteRefusal);
            Assert.Null(own.NotYetRefusal);
            Assert.Null(own.ServerRefusal);
            Assert.Equal("World of Warcraft 1.12 game data", own.GameTitle);
            Assert.Equal("", own.ReadOnlyBanner);
            Assert.Equal("", own.StatusLine);
            Assert.True(own.Maps.CanEdit);
            Assert.Null(own.Maps.WriteRefusal);
            Assert.Null(own.Maps.NotYetRefusal);
            Assert.Equal(own.ServerConnected ? "Names from server database: connected" : "Names from server database: not connected", own.ServerStatusText);
            Assert.IsType<WoWData.Core.Servers.Mangos112ServerProvider>(session.Server);
            Assert.Equal(AppSession.StageNames, main.Stages.Select(s => s.Name));
            Assert.Equal($"WoW Data Studio - {env.Client}", main.WindowTitle);

            // ---- "Open read only": the other version in the same window, with the same tabs
            Assert.True(await main.OpenOtherClientAsync(copy, null), main.WelcomeError);
            var b = main.Browser!;
            Assert.NotSame(own, b);
            Assert.True(b.IsReadOnly && main.IsReadOnly && session.IsReadOnly);
            Assert.Equal(Screen.Browser, main.Screen);
            Assert.Equal(AppSession.OtherStageNames, main.Stages.Select(s => s.Name));
            Assert.Contains("1.60.1.70450 (read only)", main.WindowTitle);
            Assert.Contains("1.60.1.70450", b.GameTitle);
            Assert.Null(session.Workspace);
            Assert.Equal(env.Client, session.Settings.ClientDir);   // the app still starts with your game
            // every write: the same reason as the command line
            var reason = "1.60.1.70450 is open read only. " + GameProfile.WriteReason;
            Assert.Equal(reason, b.WriteRefusal);
            Assert.Equal(reason, b.Maps.WriteRefusal);
            Assert.Equal(reason, b.VersionTables.WriteRefusal);
            Assert.False(b.Maps.CanEdit);
            Assert.NotNull(b.Maps.NotYetRefusal);
            Assert.Contains("1.60.1.70450", b.ReadOnlyBanner);
            Assert.Contains("creature displays readable", b.StatusLine);
            // the server provider: no server connected, nothing made up
            Assert.IsType<WoWData.Core.Servers.NoServerProvider>(session.Server);
            Assert.False(b.ServerConnected);
            Assert.Equal("No server connected for 1.60.1.70450", b.ServerStatusText);
            Assert.Equal(WoWData.Core.Servers.NoServerProvider.NoServerText("1.60.1.70450"), b.ServerMissingText);
            Assert.Equal(b.ServerMissingText, b.ServerRefusal);
            Assert.Contains(GameProfile.WriteReason, b.DesignerMissingText);
            var (ok, msg) = await session.RefreshServerAsync();
            Assert.False(ok);
            Assert.Equal(b.ServerMissingText, msg);

            // Browse: a creature display with its files (FileDataID, name source, state), its 3D view, Used in game
            b.SearchText = "testcube";
            await b.ApplyFilterNow();
            var row = b.Results.First(r => r.Entry.Key == "cdisplay:1");
            b.SelectedRow = row;
            await Until(() => b.DetailTitle.Length > 0 && !b.DetailsLoading && b.Files.Count > 0);
            Assert.Contains(b.Files, f => f.Role == "model" && f.FileDataId == CubeModel.ToString());
            Assert.True(b.IsModelEntry);
            Assert.False(b.IsBuildingEntry);
            await Until(() => !b.ModelViewer.IsLoading && b.ModelViewer.HasModel, 60000);
            Assert.True(b.ModelViewer.HasModel, b.ModelViewer.Message);
            b.UsedInGameExpanded = true;
            var u = b.UsedInGame;
            await Until(() => !u.IsLoading && u.Report != null);
            Assert.Contains(WoWData.Core.Servers.NoServerProvider.NoServerText("1.60.1.70450"), u.Report!.Notes);
            Assert.False(u.CanRefreshServer);
            b.UsedInGameExpanded = false;

            // exports still work: Export for Blender (outside the client)
            var pkgDir = Path.Combine(_root, "exports", "models");
            var pkg = await b.ExportOtherModelAsync(ModelExportMode.CurrentLook, pkgDir, true);
            Assert.NotNull(pkg);
            Assert.StartsWith(pkgDir, pkg!.Folder);
            Assert.True(File.Exists(pkg.ManifestPath));
            Assert.Contains(pkg.Notes, n => n.Contains("cannot be imported back", StringComparison.Ordinal));

            // a texture: Export works (a PNG, no .blp.json), Import is refused
            b.SearchText = "INV_Test_01";
            await b.ApplyFilterNow();
            b.SelectedRow = b.Results.First(r => r.Entry.Kind == WoWData.Core.Index.AssetKind.Texture);
            await Until(() => b.SelectedTexture != null && (b.Viewer.IsLoaded || b.Viewer.Error.Length > 0));
            Assert.True(b.CanExport);
            Assert.False(b.CanImport);
            var png = Path.Combine(_root, "exports", "icon.png");
            Assert.True(b.ExportOtherTextureTo(b.SelectedTexture!.Path, png));
            Assert.True(File.Exists(png));
            Assert.False(File.Exists(png + ".json") || File.Exists(Path.ChangeExtension(png, ".blp.json")));
            var editPng = Path.Combine(_root, "edit.png");
            File.Copy(png, editPng);
            Assert.False(await b.ImportPngAsync(editPng, b.SelectedTexture.Path, interactive: false));

            // the Data tables tab: the version's DB2 tables, read only, rows exported as CSV
            b.SelectedTab = BrowserViewModel.TablesTab;
            var t = b.VersionTables;
            await t.EnsureLoadedAsync();
            Assert.Contains(t.Tables, x => x.Name == "ItemSparse");
            await t.SelectTableAsync("ItemSparse");
            Assert.True(t.HasTable, t.TableNote);
            Assert.Equal(1, t.Table!.RowCount);   // 1 readable, 1 locked (never guessed)
            Assert.Contains("encrypted records left out", t.TableNote);
            t.Search = "sword";
            Assert.Single(t.Shown);
            var csv = Path.Combine(_root, "exports", "ItemSparse.csv");
            Assert.True(await t.ExportToAsync(csv));
            Assert.Contains("Test Sword", File.ReadAllText(csv));
            b.SelectedTab = 0;

            // ---- "Back to your 1.12 game": everything as it was
            await main.OpenOwnGameCommand.ExecuteAsync(null);
            var again = main.Browser!;
            Assert.False(again.IsReadOnly || main.IsReadOnly || session.IsReadOnly);
            Assert.NotNull(session.Workspace);
            Assert.Null(again.WriteRefusal);
            Assert.True(again.Maps.CanEdit);
            Assert.Equal("World of Warcraft 1.12 game data", again.GameTitle);
            Assert.Equal(AppSession.StageNames, main.Stages.Select(s => s.Name));
            Assert.Equal($"WoW Data Studio - {env.Client}", main.WindowTitle);
            Assert.IsType<WoWData.Core.Servers.Mangos112ServerProvider>(session.Server);
            Assert.Equal(own.Chips.Select(c => (c.Kind, c.Count, c.Tip)), again.Chips.Select(c => (c.Kind, c.Count, c.Tip)));
        });
        // the other version's folder is only read
        var after = Directory.EnumerateFiles(copy.Folder, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length)).OrderBy(x => x.f).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void ExportsRefuseTheClientsOwnFolder()
    {
        var (copy, lf) = Beta();
        var paths = new AppPaths(Path.Combine(_root, "home"));
        using var s = new AppSession(paths, AppSettings.Load(paths));
        s.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = lf }, null).GetAwaiter().GetResult();
        Assert.Throws<IOException>(() => WoWData.Core.M2.Modern.ModernModelExport.CheckOutsideClient(s, Path.Combine(copy.Folder, "out.png")));
        Assert.Throws<IOException>(() => WoWData.Core.M2.Modern.ModernModelExport.Export(s, CubeName, ModelExportMode.CurrentLook, Path.Combine(copy.Folder, "pkg")));
        WoWData.Core.M2.Modern.ModernModelExport.CheckOutsideClient(s, Path.Combine(_root, "elsewhere", "out.png"));
        // every look of the model: the creature displays and the item display using it
        var (looks, _) = WoWData.Core.M2.Modern.ModernModelExport.Looks(s, CubeName);
        Assert.Equal(new[] { "creature display", "creature display", "item display" }, looks.Select(l => l.Kind));
        var res = WoWData.Core.M2.Modern.ModernModelExport.Export(s, CubeName, ModelExportMode.AllVariants, Path.Combine(_root, "pkgs"));
        Assert.True(File.Exists(res.ManifestPath));
        Assert.True(res.Manifest.Variants.Count >= 2);
    }

    [Fact]
    public void WriteGuardGreysOutWithTheReasonAndGivesTheControlBack()
    {
        CompareTests.RunSta(() =>
        {
            var vm = new GuardVm();
            var b = new Button { DataContext = vm };
            b.SetBinding(System.Windows.UIElement.IsEnabledProperty, new Binding(nameof(GuardVm.Enabled)));
            b.ToolTip = "Imports your edited PNG.";
            Assert.True(b.IsEnabled);
            // your 1.12 game: no reason, nothing changes
            b.SetBinding(WriteGuard.ReasonProperty, new Binding(nameof(GuardVm.Reason)));
            Assert.True(b.IsEnabled);
            Assert.Equal("Imports your edited PNG.", b.ToolTip);
            // another version: greyed out, the reason on hover (also while disabled)
            vm.Reason = "1.60.1.70450 is open read only. " + GameProfile.WriteReason;
            Assert.False(b.IsEnabled);
            Assert.Equal(vm.Reason, b.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(b));
            // your game again: the control's own state and tooltip, and its binding still works
            vm.Reason = null;
            Assert.True(b.IsEnabled);
            Assert.Equal("Imports your edited PNG.", b.ToolTip);
            Assert.False(ToolTipService.GetShowOnDisabled(b));
            vm.Enabled = false;
            Assert.False(b.IsEnabled);
        });
    }

    private sealed class GuardVm : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private string? _reason;
        private bool _enabled = true;
        public string? Reason { get => _reason; set => SetProperty(ref _reason, value); }
        public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    }

    [Fact]
    public void TheServerProviderOfAnotherVersionCanBePluggedInLater()
    {
        var (copy, _) = Beta();
        var fake = new FakeServer();
        Func<GameProfile, WoWData.Core.Servers.IServerProvider?> factory = p => p.Copy?.Folder == copy.Folder ? fake : null;
        WoWData.Core.Servers.ServerProviders.Register(factory);
        try
        {
            var paths = new AppPaths(Path.Combine(_root, "home2"));
            using var s = new AppSession(paths, AppSettings.Load(paths));
            s.OpenOtherAsync(copy, new OtherOpenOptions(), null).GetAwaiter().GetResult();
            Assert.Same(fake, s.Server);
            Assert.True(s.ServerConnected);
            Assert.Equal("a test server", s.ServerMessage);
            // the writes stay greyed out: a server does not make the client writable
            Assert.NotNull(s.WriteRefusal);
        }
        finally
        {
            WoWData.Core.Servers.ServerProviders.Unregister(factory);
        }
    }

    private sealed class FakeServer : WoWData.Core.Servers.IServerProvider
    {
        public string Kind => "test";
        public bool IsConnected => true;
        public bool IsAvailable => true;
        public string StatusText => "Test server: connected";
        public string Detail => "a test server";
    }
}
