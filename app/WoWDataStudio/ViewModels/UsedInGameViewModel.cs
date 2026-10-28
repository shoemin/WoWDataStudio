using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Usage;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// The "Used in game" part of the details panel (docs\manual\used-in-game.md): how the selected entry is used by the
/// game (NPCs and their spawns, loot, vendors, quests, placements on the map, spells, what a texture change affects).
/// The report is built on a background thread by <see cref="UsageService"/> from tables precomputed in the index.
/// Names in it are links: <see cref="NavigateRequested"/> asks the main list to select that entry.
/// </summary>
public sealed partial class UsedInGameViewModel : ObservableObject
{
    private readonly AppSession _session;
    private UsageService? _service;
    private AssetIndex? _serviceFor;
    private int _token;
    private readonly object _gate = new();

    public UsedInGameViewModel(AppSession session)
    {
        _session = session;
    }

    [ObservableProperty] private UsageReport? _report;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _timingText = "";
    [ObservableProperty] private bool _canGoBack;

    /// <summary>The panel header's summary after "Used in game" (e.g. " · 65 spawns in 1 zone · drops 65 items").</summary>
    public string ShortText => IsLoading && Report == null ? " · reading..." : Report?.Short ?? "";

    partial void OnReportChanged(UsageReport? value) => OnPropertyChanged(nameof(ShortText));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShortText));

    /// <summary>True when a link's key names an entry of the list (set by the browser).</summary>
    public Func<string, bool> CanNavigate { get; set; } = _ => false;

    /// <summary>A link was clicked: select that entry (the browser handles it).</summary>
    public event Action<string>? NavigateRequested;

    /// <summary>"Refresh server data" (the browser runs the main window's reload of the server part).</summary>
    public event Func<Task>? RefreshServerRequested;
    /// <summary>M4: the open game has a server part (your 1.12 game); false in the read-only window of another version.</summary>
    public bool CanRefreshServer => ServerParts && (_session.Profile?.Has(WoWData.Core.Versions.GameCaps.Server) ?? true);
    /// <summary>M4: false for a report of another version opened read only (no server).</summary>
    public bool ServerParts { get; init; } = true;

    /// <summary>"Back" after following a link.</summary>
    public event Action? BackRequested;

    /// <summary>"Settings..." in the note about the server database.</summary>
    public event Action? SettingsRequested;

    public bool ServerConnected => _session.ServerConnected;

    /// <summary>The service for the open index (recreated when the index is reopened, e.g. after "Refresh server data").</summary>
    private UsageService? Service
    {
        get
        {
            lock (_gate)
            {
                var index = _session.Index;
                if (index == null) return null;
                if (_service == null || !ReferenceEquals(_serviceFor, index))
                {
                    _service?.Dispose();
                    _service = new UsageService(index.Path);
                    _serviceFor = index;
                }
                return _service;
            }
        }
    }

    /// <summary>Loads the name tables ahead (so the first selection is as fast as the others).</summary>
    public void Warm()
    {
        var svc = Service;
        if (svc != null) _ = Task.Run(svc.Warm);
    }

    public async Task LoadAsync(IndexEntry? entry)
    {
        int token = ++_token;
        if (entry == null)
        {
            Clear();
            return;
        }
        var svc = Service;
        if (svc == null) return;
        IsLoading = true;
        Error = "";
        try
        {
            var sw = Stopwatch.StartNew();
            var rep = await Task.Run(() => _session.UsageReport(entry.Key) ?? svc.Report(entry.Key));
            if (token != _token) return;
            Report = rep;
            TimingText = $"Loaded in {sw.Elapsed.TotalMilliseconds:0} ms";
            OnPropertyChanged(nameof(ServerConnected));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            if (token != _token) return;
            Report = null;
            Error = "The \"Used in game\" information could not be read: " + e.Message + " (Rebuild the index from the File menu.)";
        }
        finally
        {
            if (token == _token) IsLoading = false;
        }
    }

    public void Clear()
    {
        ++_token;
        Report = null;
        Error = "";
        TimingText = "";
        IsLoading = false;
    }

    /// <summary>The service is reopened on the next load (after the index file was replaced).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _service?.Dispose();
            _service = null;
            _serviceFor = null;
        }
        OnPropertyChanged(nameof(ServerConnected));
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (key != null && CanNavigate(key)) NavigateRequested?.Invoke(key);
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke();

    [RelayCommand]
    private async Task RefreshServer()
    {
        if (RefreshServerRequested != null) await RefreshServerRequested();
    }
}
