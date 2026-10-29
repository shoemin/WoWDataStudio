using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

public enum Screen { Welcome, Organizing, Browser }

public enum StageState { Waiting, Running, Done }

public sealed partial class StageItem : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private StageState _state;
    public string Glyph => State switch { StageState.Done => "", StageState.Running => "", _ => "" };
    partial void OnStateChanged(StageState value) => OnPropertyChanged(nameof(Glyph));
}

public sealed partial class MainViewModel : ObservableObject
{
    public AppSession Session { get; }
    private readonly DispatcherTimer _toastTimer;

    [ObservableProperty] private Screen _screen = Screen.Welcome;
    [ObservableProperty] private BrowserViewModel? _browser;

    // welcome
    [ObservableProperty] private string _folderText = "";
    [ObservableProperty] private string _welcomeError = "";
    [ObservableProperty] private string _welcomeHint = "";

    // organizing
    public ObservableCollection<StageItem> Stages { get; } = new();
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private string _organizingTitle = "Organizing game data...";
    [ObservableProperty] private string _organizingSubtitle = "";

    // busy overlay and messages
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastVisible;

    public string WindowTitle => Session.Profile is { IsOwnClient: false } p ? $"WoW Data Studio - {p.Copy?.Name} {p.VersionBuild} (read only)"
        : Session.Client == null ? "WoW Data Studio" : $"WoW Data Studio - {Session.Client.ClientDir}";

    public MainViewModel(AppSession session)
    {
        Session = session;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
        foreach (var s in AppSession.StageNames) Stages.Add(new StageItem { Name = s });
        FolderText = session.Settings.ClientDir ?? GuessClientDir() ?? "";
    }

    /// <summary>A likely client folder next to the app (the project layout) to pre-fill the welcome screen.</summary>
    private static string? GuessClientDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "Client");
            if (File.Exists(Path.Combine(c, "WoW.exe"))) return c;
        }
        return null;
    }

    public string LooseFilesDoc
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "docs", "LOOSE_FILES_PATCH.md");
                if (File.Exists(p)) return p;
            }
            return Path.Combine(AppContext.BaseDirectory, "docs", "LOOSE_FILES_PATCH.md");
        }
    }

    /// <summary>0.11.0: docs\HOT_RELOAD.md (how hot reload works; shipped like LOOSE_FILES_PATCH.md).</summary>
    public string HotReloadDoc
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "docs", "HOT_RELOAD.md");
                if (File.Exists(p)) return p;
            }
            return Path.Combine(AppContext.BaseDirectory, "docs", "HOT_RELOAD.md");
        }
    }

    public void SaveSettings()
    {
        try { Session.Settings.Save(Session.Paths); }
        catch (IOException) { }
    }

    public async Task StartAsync()
    {
        var dir = Session.Settings.ClientDir;
        if (dir != null && ClientValidator.Check(dir).Ok) await OpenClientAsync(dir, false);
        else Screen = Screen.Welcome;
    }

    // ------------------------------------------------------------------ welcome

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = Dialogs.PickFolder("Select your World of Warcraft folder (the one with WoW.exe)", Directory.Exists(FolderText) ? FolderText : null);
        if (picked == null) return;
        FolderText = picked;
        var check = ClientValidator.Check(picked);
        WelcomeError = check.Ok ? "" : check.Message;
        WelcomeHint = check.Ok ? check.Message + " Click \"Load game data\"." : "";
    }

    [RelayCommand]
    private async Task UseFolder()
    {
        var check = ClientValidator.Check(FolderText);
        if (!check.Ok)
        {
            WelcomeError = check.Message;
            WelcomeHint = "";
            return;
        }
        WelcomeError = "";
        FolderText = check.ClientDir;
        await OpenClientAsync(check.ClientDir, false);
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        if (Session.Workspace?.HasLooseInstalls() == true &&
            !Dialogs.Confirm("Change the game folder", "You have loose files installed in this game folder. They stay installed; open this folder again later to remove them.", "Continue")) return;
        WelcomeError = "";
        WelcomeHint = "";
        Screen = Screen.Welcome;
    }

    // ------------------------------------------------------------------ organizing

    public async Task OpenClientAsync(string clientDir, bool forceRebuild)
    {
        if (Session.IsReadOnly) ReleaseBrowser();   // back from another version: its pop-outs and tabs go
        SetStages(AppSession.StageNames);
        foreach (var s in Stages) s.State = StageState.Waiting;
        ProgressPercent = 0;
        ProgressDetail = "Opening the game archives...";
        OrganizingTitle = forceRebuild ? "Rebuilding the index..." : "Organizing game data...";
        OrganizingSubtitle = $"Reading {clientDir}. This happens once; next time the organized data loads instantly. The game files are only read, never changed.";
        Screen = Screen.Organizing;
        var progress = new Progress<BuildProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            ProgressDetail = p.Detail.Length > 0 ? $"{p.Stage}: {p.Detail}" : p.Stage;
            for (int i = 0; i < Stages.Count; i++)
                Stages[i].State = i + 1 < p.StageNumber ? StageState.Done : i + 1 == p.StageNumber ? (p.Stage == "Done" ? StageState.Done : StageState.Running) : StageState.Waiting;
        });
        try
        {
            await Session.OpenAsync(clientDir, forceRebuild, progress);
            foreach (var s in Stages) s.State = StageState.Done;
            ProgressPercent = 100;
            Session.Settings.ClientDir = clientDir;
            SaveSettings();
            Browser = new BrowserViewModel(Session, this);
            OnPropertyChanged(nameof(WindowTitle));
            Screen = Screen.Browser;
            await Browser.ApplyFilterNow();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WoWData.Core.Mpq.MpqException or WoWData.Core.Dbc.DbcException or Microsoft.Data.Sqlite.SqliteException)
        {
            WelcomeError = $"The game data could not be read: {e.Message}";
            Screen = Screen.Welcome;
        }
    }

    [RelayCommand]
    private async Task RebuildIndex()
    {
        if (Session.IsReadOnly && Session.Profile?.Copy is { } copy)
        {
            if (!Dialogs.Confirm("Rebuild the index", $"Read {copy.Name} again and rebuild its index (after you chose another listfile, for example)? The client is only read.", "Rebuild")) return;
            await OpenOtherClientAsync(copy, _otherFound, true);
            return;
        }
        if (Session.Client == null) return;
        if (!Dialogs.Confirm("Rebuild the index", "Read the game data again and rebuild the search index? This takes a few seconds. Your changes are kept.", "Rebuild")) return;
        await OpenClientAsync(Session.Client.ClientDir, true);
    }

    // ------------------------------------------------------------------ settings

    [RelayCommand]
    private async Task OpenSettings()
    {
        var vm = new SettingsViewModel(Session.Settings, Session.Client?.ClientDir ?? FolderText);
        var win = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        bool serverChanged = vm.ServerChanged;
        bool serverDataChanged = vm.ServerDataChanged(Session.Settings);
        Session.Settings.Server = vm.ToServerSettings();
        vm.ToMapSettings(Session.Settings);
        vm.ApplyServerData(Session.Settings);
        bool dbdChanged = vm.DbdChanged;
        vm.ApplyOtherVersions(Session.Settings);
        SaveSettings();
        if (dbdChanged && Session.Other != null)
        {
            // M5: the read-only Data tables name their columns from the new folder (or our measured names again)
            Session.Other.Dbd = WoWData.Core.Db2.DbdFolder.Open(Session.Settings.OtherClientsDbd);
            if (Browser?.VersionTables != null) _ = Browser.VersionTables.ReloadAsync();
        }
        if (vm.HotReloadChanged)
        {
            try
            {
                SettingsCatalog.Set(Session.Settings, "hot-reload", vm.HotReload ? "on" : "off", vm.ClientDir);
                Toast(vm.HotReload ? "Hot reload is on for this game folder. Restart the game once; then \"Reload in running game\" works." : "Hot reload is off for this game folder.");
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                Dialogs.Error("Hot reload could not be switched", e.Message);
            }
            if (Browser != null) _ = Browser.RefreshHotReloadAsync();
        }
        if ((serverChanged || serverDataChanged) && Browser != null && Session.IsOpen) _ = Browser.Server.ReconnectAsync();
        if (vm.ClientDirChanged)
        {
            await OpenClientAsync(vm.ClientDir, false);
            return;
        }
        if ((serverChanged || vm.RefreshRequested) && Session.IsOpen) await RefreshServerNames();
    }

    [RelayCommand]
    public async Task RefreshServerNames()
    {
        if (!Session.IsOpen) return;
        var r = await RunBusyAsync("Loading names from the server database...", () => Session.RefreshServerAsync());
        Browser?.NotifyServer();
        if (r.Ok) Toast("Names from the server database: connected. " + r.Message);
        else Dialogs.Show("Server database not connected", $"The names could not be loaded: {r.Message}\n\nCreatures, items and game objects are listed by their model and display id instead. Check that the server's database is running, then try again (Settings).",
            DialogKind.Warning, new[] { "OK" });
    }

    // ------------------------------------------------------------------ busy + toast

    public async Task<T> RunBusyAsync<T>(string text, Func<Task<T>> work)
    {
        BusyText = text;
        IsBusy = true;
        try { return await work(); }
        finally { IsBusy = false; }
    }

    public async Task RunBusyAsync(string text, Func<Task> work)
    {
        BusyText = text;
        IsBusy = true;
        try { await work(); }
        finally { IsBusy = false; }
    }

    public void Toast(string text)
    {
        ToastText = text;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void HideToast() => ToastVisible = false;

    [RelayCommand]
    private void OpenLooseFilesDoc() => Dialogs.OpenPath(LooseFilesDoc);

    // ------------------------------------------------------------------ another version, read only, in this window

    /// <summary>The stages of the organizing screen (your game's, or another version's).</summary>
    private void SetStages(IReadOnlyList<string> names)
    {
        if (Stages.Select(x => x.Name).SequenceEqual(names)) return;
        Stages.Clear();
        foreach (var n in names) Stages.Add(new StageItem { Name = n });
    }

    /// <summary>The clients found with the version that is open (to find a Classic Era copy again on a rebuild).</summary>
    private IReadOnlyList<WoWData.Core.Versions.ClientCopy>? _otherFound;

    /// <summary>Closes what belongs to the tabs of the version that is open now (pop-out windows, the Other versions
    /// tab's client) before another one opens in this window.</summary>
    private void ReleaseBrowser()
    {
        var b = Browser;
        if (b == null) return;
        b.CloseAllPopouts();
        b.Versions.Dispose();
        Browser = null;
    }

    /// <summary>
    /// Opens another version (a modern CASC client) read only IN THIS WINDOW, in place of your 1.12 game: the same
    /// organizing screen (its own index, built once into the app's folder of that client), then the same tabs. Every
    /// write is greyed out with the reason; File > Back to your 1.12 game opens your game again. The game folder of
    /// the settings is not changed, so the app still starts with your game.
    /// </summary>
    public async Task<bool> OpenOtherClientAsync(WoWData.Core.Versions.ClientCopy copy, IReadOnlyList<WoWData.Core.Versions.ClientCopy>? found, bool rebuild = false)
    {
        if (Browser?.Tables is { } tables && !Session.IsReadOnly && !tables.ConfirmLeave()) return false;
        if (Session.Workspace?.HasLooseInstalls() == true &&
            !Dialogs.Confirm("Open another version", "You have loose files installed in your game folder. They stay installed; open your game again later (File > Back to your 1.12 game) to remove them.", "Continue")) return false;
        ReleaseBrowser();
        _otherFound = found;
        SetStages(AppSession.OtherStageNames);
        foreach (var s in Stages) s.State = StageState.Waiting;
        ProgressPercent = 0;
        ProgressDetail = "Opening the client...";
        OrganizingTitle = rebuild ? "Rebuilding the index..." : "Organizing game data...";
        OrganizingSubtitle = $"Reading {copy.Name} ({copy.Version}{(copy.Build > 0 ? "." + copy.Build : "")}). Its tables and file names are read once into the app's folder of that client; " +
                             "next time it opens at once. The client is only read, never changed.";
        Screen = Screen.Organizing;
        var progress = new Progress<BuildProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            ProgressDetail = p.Detail.Length > 0 ? $"{p.Stage}: {p.Detail}" : p.Stage;
            for (int i = 0; i < Stages.Count; i++)
                Stages[i].State = i + 1 < p.StageNumber ? StageState.Done : i + 1 == p.StageNumber ? StageState.Running : StageState.Waiting;
        });
        var settings = Session.Settings;
        var lf = !string.IsNullOrWhiteSpace(settings.OtherClientsListfile) && File.Exists(settings.OtherClientsListfile) ? settings.OtherClientsListfile : null;
        // your 1.12 game's file names name the other version's files: the game folder of the settings, read only
        WoWData.Core.Mpq.ClientData? own = null;
        try
        {
            if (settings.ClientDir is { } dir && ClientValidator.Check(dir) is { Ok: true } check)
            {
                try { own = await Task.Run(() => new WoWData.Core.Mpq.ClientData(Path.Combine(check.ClientDir, "Data"))); }
                catch (Exception e) when (e is IOException or WoWData.Core.Mpq.MpqException) { own = null; }
            }
            await Session.OpenOtherAsync(copy, new OtherOpenOptions { Listfile = lf, Client112 = own, Found = found, ForceRebuild = rebuild }, progress);
            foreach (var s in Stages) s.State = StageState.Done;
            ProgressPercent = 100;
            Browser = new BrowserViewModel(Session, this);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(IsReadOnly));
            Screen = Screen.Browser;
            await Browser.ApplyFilterNow();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ArgumentException
                                       or WoWData.Core.Casc.CascMissingException or WoWData.Core.Db2.Db2FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            WelcomeError = $"{copy.Name} could not be opened: {e.Message}";
            WelcomeHint = "";
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(IsReadOnly));
            Screen = Screen.Welcome;
            return false;
        }
        finally
        {
            own?.Dispose();
        }
    }

    /// <summary>True while another version is open in this window (read only).</summary>
    public bool IsReadOnly => Session.IsReadOnly;

    /// <summary>"Back to your 1.12 game": opens the game folder of the settings again (the welcome screen when there is none).</summary>
    [RelayCommand]
    private async Task OpenOwnGame()
    {
        var dir = Session.Settings.ClientDir ?? FolderText;
        if (!string.IsNullOrWhiteSpace(dir) && ClientValidator.Check(dir) is { Ok: true } check)
        {
            await OpenClientAsync(check.ClientDir, false);
            OnPropertyChanged(nameof(IsReadOnly));
            return;
        }
        ReleaseBrowser();
        WelcomeError = "";
        WelcomeHint = "";
        Screen = Screen.Welcome;
    }

    /// <summary>"Open another version (read only)...": a modern client from the folder of other versions (asked for
    /// when it is not set), opened read only in this window, in place of your 1.12 game.</summary>
    [RelayCommand]
    private async Task OpenOtherVersion()
    {
        var folder = Session.Settings.OtherClientsDir;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            folder = Dialogs.PickFolder("Pick the folder of your copies of other World of Warcraft versions (or one modern client's folder)");
            if (folder == null) return;
        }
        var picked = folder;
        var (single, found) = await RunBusyAsync("Looking for clients...", () => Task.Run(() =>
        {
            var one = WoWData.Core.Versions.ClientCopies.Inspect(picked);
            return (one, one != null ? new List<WoWData.Core.Versions.ClientCopy> { one } : WoWData.Core.Versions.ClientCopies.Find(picked));
        }));
        if (single == null && Session.Settings.OtherClientsDir == null)
        {
            Session.Settings.OtherClientsDir = folder;
            SaveSettings();
        }
        var modern = found.Where(c => c.Format == WoWData.Core.Versions.ClientFormat.Casc).ToList();
        if (modern.Count == 0)
        {
            Dialogs.Info("Open another version (read only)",
                $"No modern World of Warcraft client (one that stores its data in CASC, such as the 1.60 Classic client) was found in {folder}. " +
                "Older clients (2.4.3, 3.3.5a...) open their files and tables in the Other versions tab; opening them like your game comes later.");
            return;
        }
        var copy = modern[0];
        if (modern.Count > 1)
        {
            var pick = new PickVersionWindow(modern) { Owner = Application.Current?.MainWindow };
            if (pick.ShowDialog() != true || pick.Picked == null) return;
            copy = pick.Picked;
        }
        await OpenOtherClientAsync(copy, found);
    }
}
