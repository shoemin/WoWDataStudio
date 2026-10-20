using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ServerDbSettings _original;
    private readonly string _originalClientDir;

    [ObservableProperty] private string _clientDir;
    [ObservableProperty] private string _clientCheck = "";
    [ObservableProperty] private bool _clientOk = true;
    [ObservableProperty] private bool _serverEnabled;
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _user;
    [ObservableProperty] private string _password;
    [ObservableProperty] private string _database;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool? _testOk;
    [ObservableProperty] private bool _testing;

    public bool RefreshRequested { get; set; }

    // hot reload (0.11.0): a switch IN the game folder (wowhotreload.txt), not in settings.json
    [ObservableProperty] private bool _hotReload;
    [ObservableProperty] private string _hotReloadCheck = "";
    private bool _hotReloadOriginal;

    /// <summary>True when the hot-reload switch was changed (it is written into the game folder on Save).</summary>
    public bool HotReloadChanged => HotReload != _hotReloadOriginal;

    private void CheckHotReload()
    {
        var dir = ClientValidator.Check(ClientDir);
        if (!dir.Ok)
        {
            HotReloadCheck = "Set the World of Warcraft folder first.";
            return;
        }
        bool dll = WoWData.Core.HotReload.HotReloadFlag.DllPresent(dir.ClientDir), supports = WoWData.Core.HotReload.HotReloadFlag.DllSupports(dir.ClientDir);
        HotReloadCheck = !dll ? "This game folder has no wowcode.dll (a normal client): hot reload needs the project's own client."
            : !supports ? "This game folder's wowcode.dll is older than the hot-reload update (HR1): it comes with the next deployed wave."
            : HotReload ? "On: the game takes reloads once it is started (again)." : "Off: changes show after a restart of the game.";
    }

    partial void OnHotReloadChanged(bool value) => CheckHotReload();

    // server data (0.9.0)
    [ObservableProperty] private string _serverPatch = "";
    [ObservableProperty] private string _customIds = "";
    /// <summary>0.13.3: the IDs new spells get (spell-ids).</summary>
    [ObservableProperty] private string _spellIds = "";

    /// <summary>The server-data fields into <paramref name="s"/> (checked like "wds settings set"); the reason when one is
    /// not allowed.</summary>
    public string? ApplyServerData(AppSettings s)
    {
        try
        {
            SettingsCatalog.Set(s, "server.patch", ServerPatch);
            SettingsCatalog.Set(s, "server.custom-ids", CustomIds);
            SettingsCatalog.Set(s, "spell-ids", SpellIds);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    public bool ServerDataChanged(AppSettings s) => SettingsCatalog.Get(s, "server.patch") != ServerPatch.Trim() || SettingsCatalog.Get(s, "server.custom-ids") != CustomIds.Trim() || SettingsCatalog.Get(s, "spell-ids") != SpellIds.Trim();

    // maps and terrain
    [ObservableProperty] private string _noggitPath = "";
    [ObservableProperty] private string _noggitClientDir = "";
    [ObservableProperty] private string _serverToolsDir = "";
    [ObservableProperty] private string _serverDataDir = "";
    [ObservableProperty] private string _noggitCheck = "";
    [ObservableProperty] private string _serverToolsCheck = "";
    [ObservableProperty] private string _serverDataCheck = "";

    partial void OnNoggitPathChanged(string value) => CheckMaps();
    partial void OnServerToolsDirChanged(string value) => CheckMaps();
    partial void OnServerDataDirChanged(string value) => CheckMaps();

    private void CheckMaps() => (NoggitCheck, ServerToolsCheck, ServerDataCheck) = SettingsCatalog.MapsChecks(ToMapSettings(new AppSettings()), ClientDir);

    /// <summary>Copies the maps-and-terrain fields into <paramref name="s"/> (empty = automatic).</summary>
    public AppSettings ToMapSettings(AppSettings s)
    {
        static string? N(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        s.NoggitPath = N(NoggitPath);
        s.NoggitClientDir = N(NoggitClientDir);
        s.ServerToolsDir = N(ServerToolsDir);
        s.ServerDataDir = N(ServerDataDir);
        return s;
    }

    [RelayCommand]
    private void BrowseNoggit()
    {
        var f = Dialogs.PickOpenFile("Select noggit.exe (our fixed Noggit fork)", "noggit.exe|noggit.exe|Programs (*.exe)|*.exe", string.IsNullOrWhiteSpace(NoggitPath) ? null : Path.GetDirectoryName(NoggitPath));
        if (f != null) NoggitPath = f;
    }

    [RelayCommand]
    private void BrowseNoggitClient()
    {
        var d = Dialogs.PickFolder("Select a COPY of the game folder for Noggit (not the game folder itself)", string.IsNullOrWhiteSpace(NoggitClientDir) ? null : NoggitClientDir);
        if (d != null) NoggitClientDir = d;
    }

    [RelayCommand]
    private void BrowseServerTools()
    {
        var d = Dialogs.PickFolder("Select the server tools folder (with reextract.ps1 and swap_in.ps1)", string.IsNullOrWhiteSpace(ServerToolsDir) ? null : ServerToolsDir);
        if (d != null) ServerToolsDir = d;
    }

    [RelayCommand]
    private void BrowseServerData()
    {
        var d = Dialogs.PickFolder("Select the world server's data folder (with maps, vmaps, mmaps)", string.IsNullOrWhiteSpace(ServerDataDir) ? null : ServerDataDir);
        if (d != null) ServerDataDir = d;
    }

    public SettingsViewModel(AppSettings settings, string clientDir)
    {
        _original = settings.Server.Clone();
        _originalClientDir = clientDir;
        _clientDir = clientDir;
        _serverEnabled = settings.Server.Enabled;
        _host = settings.Server.Host;
        _port = settings.Server.Port.ToString();
        _user = settings.Server.User;
        _password = settings.Server.Password;
        _database = settings.Server.Database;
        _noggitPath = settings.NoggitPath ?? "";
        _noggitClientDir = settings.NoggitClientDir ?? "";
        _serverToolsDir = settings.ServerToolsDir ?? "";
        _serverDataDir = settings.ServerDataDir ?? "";
        _serverPatch = SettingsCatalog.Get(settings, "server.patch");
        _customIds = SettingsCatalog.Get(settings, "server.custom-ids");
        _spellIds = SettingsCatalog.Get(settings, "spell-ids");
        _hotReload = _hotReloadOriginal = SettingsCatalog.Get(settings, "hot-reload", clientDir) == "on";
        ValidateClient();
        CheckMaps();
        CheckHotReload();
    }

    public bool ClientDirChanged => !string.Equals(Path.GetFullPath(ClientDir), Path.GetFullPath(_originalClientDir), StringComparison.OrdinalIgnoreCase);

    public bool ServerChanged
    {
        get
        {
            var s = ToServerSettings();
            return s.Enabled != _original.Enabled || s.Host != _original.Host || s.Port != _original.Port || s.User != _original.User ||
                   s.Password != _original.Password || s.Database != _original.Database;
        }
    }

    public ServerDbSettings ToServerSettings() => new()
    {
        Enabled = ServerEnabled,
        Host = Host.Trim(),
        Port = int.TryParse(Port, out var p) && p > 0 && p < 65536 ? p : 3306,
        User = User.Trim(),
        Password = Password,
        Database = Database.Trim(),
    };

    partial void OnClientDirChanged(string value)
    {
        ValidateClient();
        CheckHotReload();
    }

    private void ValidateClient()
    {
        var c = ClientValidator.Check(ClientDir);
        ClientOk = c.Ok;
        ClientCheck = c.Message;
    }

    [RelayCommand]
    private void BrowseClient()
    {
        var p = Dialogs.PickFolder("Select your World of Warcraft folder (the one with WoW.exe)", Directory.Exists(ClientDir) ? ClientDir : null);
        if (p != null) ClientDir = ClientValidator.Check(p).ClientDir is { Length: > 0 } d ? d : p;
    }

    [RelayCommand]
    private void UseDefaults()
    {
        var d = new ServerDbSettings();
        ServerEnabled = true;
        Host = d.Host;
        Port = d.Port.ToString();
        User = d.User;
        Password = d.Password;
        Database = d.Database;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        Testing = true;
        TestResult = "Connecting...";
        TestOk = null;
        try
        {
            var s = ToServerSettings();
            s.Enabled = true;
            var (ok, msg) = await new ServerDb(s).TestAsync();
            TestOk = ok;
            TestResult = ok ? "Connected: " + msg : "Not connected: " + msg;
        }
        finally
        {
            Testing = false;
        }
    }
}
