using System.ComponentModel;
using WoWData.Core.Index;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// The "Used in game" hook of the browser (kept in its own file): the panel's view model, loading it for the selected
/// entry, and following its links (select that entry in the list, with a Back history).
/// </summary>
public sealed partial class BrowserViewModel
{
    private UsedInGameViewModel? _usedInGame;
    private readonly Stack<string> _usageHistory = new();
    private IndexEntry? _usageEntry;
    private bool _usageNavigating;

    /// <summary>Whether the "Used in game" expander is open (kept while the app runs).</summary>
    private static bool _usedInGameExpandedDefault;

    public bool UsedInGameExpanded
    {
        get => _usedInGameExpandedDefault;
        set
        {
            if (_usedInGameExpandedDefault == value) return;
            _usedInGameExpandedDefault = value;
            OnPropertyChanged();
        }
    }

    public UsedInGameViewModel UsedInGame
    {
        get
        {
            if (_usedInGame != null) return _usedInGame;
            var vm = new UsedInGameViewModel(_session) { CanNavigate = k => FindEntryByKey(k) != null };
            vm.NavigateRequested += k => _ = NavigateToKeyAsync(k, true);
            vm.BackRequested += () => _ = UsageBackAsync();
            vm.SettingsRequested += () => _main.OpenSettingsCommand.Execute(null);
            vm.RefreshServerRequested += RefreshUsageServerAsync;
            PropertyChanged += OnSelectionForUsage;
            _usedInGame = vm;
            vm.Warm();
            if (SelectedRow != null) _ = LoadUsageAsync(SelectedRow.Entry);
            return vm;
        }
    }

    private void OnSelectionForUsage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectedRow)) return;
        var entry = SelectedRow?.Entry;
        if (ReferenceEquals(entry, _usageEntry)) return;   // the list was refiltered, same entry
        if (!_usageNavigating && entry != null && _usageEntry != null) _usageHistory.Clear();
        _ = LoadUsageAsync(entry);
    }

    private Task LoadUsageAsync(IndexEntry? entry)
    {
        _usageEntry = entry;
        if (_usedInGame == null) return Task.CompletedTask;
        _usedInGame.CanGoBack = _usageHistory.Count > 0;
        return _usedInGame.LoadAsync(entry);
    }

    /// <summary>The entry with this key (creature:3098, item:2880, file:&lt;path&gt;...; paths case-insensitive), or null.</summary>
    public IndexEntry? FindEntryByKey(string key) => _session.FindEntry(key);

    /// <summary>Selects the entry with this key in the list (its type filter on, searched by name or path).</summary>
    public async Task<bool> NavigateToKeyAsync(string key, bool remember)
    {
        var entry = FindEntryByKey(key);
        if (entry == null) return false;
        if (remember && SelectedRow != null && !ReferenceEquals(SelectedRow.Entry, entry)) _usageHistory.Push(SelectedRow.Entry.Key);
        _usageNavigating = true;
        try
        {
            SelectedTab = 0;
            OnlyMyChanges = false;
            foreach (var c in Chips.Where(c => c.Kind == entry.Kind && !c.IsChecked)) c.IsChecked = true;
            SearchText = entry.Key.StartsWith("file:", StringComparison.Ordinal) ? entry.Path ?? entry.Name : entry.Name;
            _searchTimer.Stop();
            await ApplyFilterAsync();
            var row = Results.FirstOrDefault(r => ReferenceEquals(r.Entry, entry));
            if (row == null)
            {
                SearchText = "";
                _searchTimer.Stop();
                await ApplyFilterAsync();
                row = Results.FirstOrDefault(r => ReferenceEquals(r.Entry, entry));
            }
            if (row == null) return false;
            SelectedRow = row;
            if (_usedInGame != null) _usedInGame.CanGoBack = _usageHistory.Count > 0;
            return true;
        }
        finally
        {
            _usageNavigating = false;
        }
    }

    private async Task UsageBackAsync()
    {
        while (_usageHistory.Count > 0)
            if (await NavigateToKeyAsync(_usageHistory.Pop(), false)) return;
        if (_usedInGame != null) _usedInGame.CanGoBack = false;
    }

    /// <summary>"Refresh server data": reload the server part of the index, then show the same entry again.</summary>
    private async Task RefreshUsageServerAsync()
    {
        var key = SelectedRow?.Entry.Key;
        _usedInGame?.Reset();
        await _main.RefreshServerNames();
        _usageEntry = null;
        if (key != null && !await NavigateToKeyAsync(key, false))
        {
            // a creature listed by display id offline gets its server entries (and the other way round): keep the search
            _ = LoadUsageAsync(SelectedRow?.Entry);
        }
    }
}
