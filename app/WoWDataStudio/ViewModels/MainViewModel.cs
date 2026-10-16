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

    public string WindowTitle => Session.Client == null ? "WoW Data Studio" : $"WoW Data Studio - {Session.Client.ClientDir}";

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
        SaveSettings();
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
}
