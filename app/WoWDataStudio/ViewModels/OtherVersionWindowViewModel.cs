using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Mpq;
using WoWData.Core.Versions;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A file of the entry in the read-only window: name (or #FileDataID), role, FileDataID, name source, state.</summary>
public sealed class OtherFileRow
{
    public OtherFileRow(ModernFileRef f) => File = f;
    public ModernFileRef File { get; }
    public string Path => File.Path;
    public string FileName => System.IO.Path.GetFileName(File.Path);
    public string Role => File.Role;
    public string FileDataId => File.FileDataId.ToString();
    public string NameSource => NameSources.Short(File.Source);
    public string State => File.State;
    public bool IsLocked => File.State != "local";
    public string Tip => $"{File.Path}\nFileDataID {File.FileDataId}\n{NameSources.Describe(File.Source)}\n{File.State}";
}

/// <summary>
/// M1 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 2.4): another version opened read only in a window of its own,
/// next to your 1.12 window: its own <see cref="AppSession"/> (no workspace), Browse (search, type filters, details
/// with FileDataIDs, name sources and lock states) and the texture viewer with PNG export. Nothing here can change the
/// client: there is no edit, import, install or server command in this window.
/// </summary>
public sealed partial class OtherVersionWindowViewModel : ObservableObject, IDisposable, IModelViewerHost
{
    private readonly ClientCopy _copy;
    private readonly Func<ClientData?> _client112;
    private readonly IReadOnlyList<ClientCopy>? _found;
    private readonly DispatcherTimer _searchTimer;
    private int _filterToken, _detailsToken;
    private bool _disposed;

    public AppSession Session { get; }
    public TextureViewerViewModel Viewer { get; }
    /// <summary>M2: the 3D view of the selected entry (the same viewer as your game's, read by the modern reader).</summary>
    public ModelViewerViewModel ModelViewer { get; }
    /// <summary>The built models, shared by the 3D view and this window's pop-out windows.</summary>
    public ModelAssetCache ModelCache { get; } = new();
    /// <summary>This window's model pop-out windows (closed with it).</summary>
    public List<Views.ModelPopoutWindow> ModelPopouts { get; } = new();
    /// <summary>A model entry's details were shown (following pop-out windows load it too).</summary>
    public event Action<IndexEntry, EntryDetails>? ModelEntryShown;
    private readonly AppPaths _paths;
    public ObservableCollection<FilterChip> Chips { get; } = new();
    public ObservableCollection<StageItem> Stages { get; } = new();

    public OtherVersionWindowViewModel(AppPaths paths, AppSettings settings, ClientCopy copy, Func<ClientData?> client112, IReadOnlyList<ClientCopy>? found)
    {
        _copy = copy;
        _client112 = client112;
        _found = found;
        _paths = paths;
        Session = new AppSession(paths, settings);
        Viewer = new TextureViewerViewModel(Session);
        ModelViewer = new ModelViewerViewModel(Session, ModelCache) { PopOutCommand = OpenModelPopoutCommand, ShowPopoutCommand = ShowFollowingPopoutCommand };
        ModelViewer.OpenTextureRequested += (path, _) => ShowTexture(path);
        foreach (var s in AppSession.OtherStageNames) Stages.Add(new StageItem { Name = s });
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = ApplyFilterAsync(); };
        var defs = new (AssetKind Kind, string Label, string Tip)[]
        {
            (AssetKind.Creature, "Creatures", "Creature displays (by model and skin), and the few creatures the client names itself."),
            (AssetKind.Item, "Items", "Items the client names, and item displays with their models, textures and icons."),
            (AssetKind.GameObject, "Game objects", "Game object displays, and the objects the client places itself (signposts...)."),
            (AssetKind.WorldObject, "World objects", "Named models and buildings under World\\."),
            (AssetKind.SpellEffect, "Spell effects", "Spell effect models (SpellVisualEffectName) and the spells that show them."),
            (AssetKind.Texture, "Textures", "Named textures (.blp)."),
        };
        foreach (var d in defs)
        {
            var chip = new FilterChip { Kind = d.Kind, Label = d.Label, Tip = d.Tip };
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FilterChip.IsChecked)) _ = ApplyFilterAsync(); };
            Chips.Add(chip);
        }
        Title = $"{copy.Name} - read only";
    }

    public ClientCopy Copy => _copy;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isOrganizing = true;
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressDetail = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _namesLine = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private IReadOnlyList<ResultRow> _results = Array.Empty<ResultRow>();
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private ResultRow? _selectedRow;
    [ObservableProperty] private SortColumn _sortBy = SortColumn.Name;
    [ObservableProperty] private bool _sortAscending = true;

    // details
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailSubtitle = "";
    [ObservableProperty] private List<string> _chain = new();
    [ObservableProperty] private List<string> _facts = new();
    [ObservableProperty] private List<string> _usedBy = new();
    [ObservableProperty] private List<OtherFileRow> _files = new();
    [ObservableProperty] private ObservableCollection<TextureItem> _textures = new();
    [ObservableProperty] private TextureItem? _selectedTexture;
    /// <summary>M2: the selected entry has a model (the 3D view is shown).</summary>
    [ObservableProperty] private bool _isModelEntry;

    public bool CanExport => SelectedTexture is { Exists: true } && Viewer.IsLoaded;

    /// <summary>Opens the client (builds its index the first time; the stages show on the organizing screen).</summary>
    public async Task OpenAsync(bool rebuild = false)
    {
        IsOrganizing = true;
        IsReady = false;
        Error = "";
        foreach (var s in Stages) s.State = StageState.Waiting;
        var progress = new Progress<BuildProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            ProgressDetail = p.Detail.Length > 0 ? $"{p.Stage}: {p.Detail}" : p.Stage;
            for (int i = 0; i < Stages.Count; i++)
                Stages[i].State = i + 1 < p.StageNumber ? StageState.Done : i + 1 == p.StageNumber ? StageState.Running : StageState.Waiting;
        });
        var settings = Session.Settings;
        var lf = !string.IsNullOrWhiteSpace(settings.OtherClientsListfile) && File.Exists(settings.OtherClientsListfile) ? settings.OtherClientsListfile : null;
        ProgressDetail = "Opening the client...";
        // your 1.12 game's file names name the other version's files: the open game's, else the game folder of the settings
        ClientData? own = null;
        var names112 = _client112();
        if (names112 == null && settings.ClientDir is { } dir && ClientValidator.Check(dir) is { Ok: true } check)
        {
            try { names112 = own = await Task.Run(() => new ClientData(Path.Combine(check.ClientDir, "Data"))); }
            catch (Exception e) when (e is IOException or MpqException) { names112 = null; }
        }
        try
        {
            await Session.OpenOtherAsync(_copy, new OtherOpenOptions { Listfile = lf, Client112 = names112, Found = _found, ForceRebuild = rebuild }, progress);
            if (_disposed) return;
            foreach (var s in Stages) s.State = StageState.Done;
            var st = OtherVersionStatus.Of(Session);
            Title = $"{_copy.Name} {Session.Profile!.VersionBuild} - read only";
            Subtitle = $"World of Warcraft {Session.Profile.VersionBuild} ({_copy.Release}), {_copy.Folder}";
            StatusLine = st.StatusLine;
            NamesLine = st.NamesLine + (st.FileEntriesLeftOut > 0 ? $"; {st.FileEntriesLeftOut:N0} more named files are left out of the list (\"wds find files ... --version\" lists them)" : "");
            var counts = Session.Entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
            foreach (var c in Chips) c.Count = counts.GetValueOrDefault(c.Kind);
            IsOrganizing = false;
            IsReady = true;
            await ApplyFilterAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ArgumentException
                                       or WoWData.Core.Casc.CascMissingException or WoWData.Core.Db2.Db2FormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = $"{_copy.Name} could not be opened: {e.Message}";
        }
        finally
        {
            own?.Dispose();
        }
    }

    [RelayCommand]
    private async Task Rebuild()
    {
        if (!IsReady) return;
        if (!Dialogs.Confirm("Rebuild the index", $"Read {_copy.Name} again and rebuild its index? The client is only read.", "Rebuild")) return;
        await OpenAsync(rebuild: true);
    }

    // ------------------------------------------------------------------ search

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    [RelayCommand]
    private void SelectAllKinds()
    {
        foreach (var c in Chips) c.IsChecked = true;
    }

    [RelayCommand]
    private void OnlyKind(FilterChip? chip)
    {
        if (chip == null) return;
        foreach (var c in Chips) c.IsChecked = c == chip;
    }

    [RelayCommand]
    private void Sort(string column)
    {
        var col = Enum.Parse<SortColumn>(column);
        if (SortBy == col) SortAscending = !SortAscending;
        else
        {
            SortBy = col;
            SortAscending = true;
        }
        _ = ApplyFilterAsync();
    }

    public async Task ApplyFilterAsync()
    {
        if (!IsReady) return;
        int token = ++_filterToken;
        var kinds = Chips.Where(c => c.IsChecked).Select(c => c.Kind).ToHashSet();
        var entries = Session.Entries;
        var query = new EntryQuery(SearchText, kinds, false, SortBy switch
        {
            SortColumn.Type => EntrySort.Type, SortColumn.Id => EntrySort.Id, SortColumn.Path => EntrySort.Path, _ => EntrySort.Name,
        }, SortAscending);
        var empty = new HashSet<string>();
        var rows = await Task.Run(() => EntrySearch.Run(entries, query, empty, empty).Select(h => new ResultRow(h.Entry, false, false)).ToList());
        if (token != _filterToken) return;
        var keep = SelectedRow?.Entry;
        Results = rows;
        SelectedRow = keep == null ? null : rows.FirstOrDefault(r => ReferenceEquals(r.Entry, keep));
        ResultCountText = rows.Count == 1 ? "1 result" : $"{rows.Count:N0} results";
    }

    // ------------------------------------------------------------------ details

    partial void OnSelectedRowChanged(ResultRow? value) => _ = LoadDetailsAsync(value);

    public async Task LoadDetailsAsync(ResultRow? row)
    {
        int token = ++_detailsToken;
        HasSelection = row != null;
        if (row == null || Session.Index == null)
        {
            Textures = new();
            Files = new();
            Viewer.Clear();
            IsModelEntry = false;
            ModelViewer.Clear();
            return;
        }
        var entry = row.Entry;
        var d = await Task.Run(() => Session.Details(entry));
        if (token != _detailsToken) return;
        DetailTitle = d.Title;
        DetailSubtitle = d.Subtitle;
        Chain = d.Chain;
        Facts = d.Facts;
        UsedBy = d.UsedBy;
        Files = d.Files.Select(f => new OtherFileRow(f)).ToList();
        IsModelEntry = entry.Kind != AssetKind.Texture && d.Models.Count > 0;
        if (IsModelEntry)
        {
            _ = ModelViewer.LoadAsync(entry, d);
            ModelEntryShown?.Invoke(entry, d);
        }
        else ModelViewer.Clear();
        Textures = new ObservableCollection<TextureItem>(d.Textures.Select(t => new TextureItem(t, false)));
        SelectedTexture = Textures.FirstOrDefault(t => entry.Path != null && string.Equals(t.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                          ?? Textures.FirstOrDefault(t => t.Exists) ?? Textures.FirstOrDefault();
        if (SelectedTexture == null) Viewer.Clear();
    }

    partial void OnSelectedTextureChanged(TextureItem? value)
    {
        OnPropertyChanged(nameof(CanExport));
        if (value == null) Viewer.Clear();
        else _ = LoadTextureAsync(value.Path);
    }

    private async Task LoadTextureAsync(string path)
    {
        await Viewer.LoadAsync(path);
        OnPropertyChanged(nameof(CanExport));
    }

    /// <summary>"Export texture as PNG...": the full-size texture as a PNG (no .blp.json: it is not imported back).</summary>
    [RelayCommand]
    private void ExportTexture()
    {
        if (SelectedTexture is not { Exists: true } t) return;
        var dir = Path.Combine(Session.Settings.DefaultExportDir, Session.Profile?.Id ?? _copy.Name);
        Directory.CreateDirectory(dir);
        var png = Dialogs.PickSaveFile("Export texture as PNG", "PNG image|*.png", OtherTextures.PngName(t.Path), dir);
        if (png == null) return;
        ExportTo(t.Path, png);
    }

    /// <summary>Exports a texture to a PNG; refuses the client's own folders (also used by the screenshots and tests).</summary>
    public bool ExportTo(string path, string png)
    {
        var full = Path.GetFullPath(png);
        bool inside = new[] { _copy.Folder, Session.Files?.Storage.DataDirectory }.Any(d => d != null && full.StartsWith(Path.GetFullPath(d).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (inside)
        {
            Dialogs.Error("Export texture as PNG", $"Other versions are only read: pick a folder outside {_copy.Name}.");
            return false;
        }
        try
        {
            OtherTextures.ExportPng(Session, path, full);
            return true;
        }
        catch (Exception e) when (e is ClientFileException or IOException or UnauthorizedAccessException or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Export texture as PNG", $"{path} could not be exported: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ M2: 3D view, pop-out, compare skins

    /// <summary>A texture clicked in the 3D view's texture list: shown in the texture viewer below.</summary>
    private void ShowTexture(string path)
    {
        var t = Textures.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            t = new TextureItem(new TextureRef(path, "model texture", "CASC", ""), false);
            Textures.Add(t);
        }
        SelectedTexture = t;
    }

    [RelayCommand]
    private Task OpenModelPopout() => OpenModelPopoutAsync();

    /// <summary>"Open in new window": a pop-out 3D window on what this window's 3D view shows (same look, animation,
    /// camera), following the selection here or pinned.</summary>
    public async Task<Views.ModelPopoutWindow?> OpenModelPopoutAsync(Action<Views.ModelPopoutWindow>? beforeShow = null)
    {
        if (ModelViewer.Loaded == null) return null;
        var settings = Session.Settings.Popout("model");
        var vm = new ModelPopoutViewModel(this, ModelCache);
        vm.Viewer.OpenTextureRequested += (path, _) => ShowTexture(path);
        var win = new Views.ModelPopoutWindow(vm, settings, () => Session.Settings.Save(_paths));
        if (settings.Placement == null) win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        vm.FollowChanged += UpdateDockedPause;
        win.Closed += (_, _) =>
        {
            ModelPopouts.Remove(win);
            UpdateDockedPause();
        };
        beforeShow?.Invoke(win);
        ModelPopouts.Add(win);
        win.Show();
        UpdateDockedPause();
        await vm.Viewer.CopyFromAsync(ModelViewer);
        return win;
    }

    [RelayCommand]
    private void ShowFollowingPopout()
    {
        var w = ModelPopouts.FirstOrDefault(p => p.Vm.FollowSelection) ?? ModelPopouts.FirstOrDefault();
        if (w == null) return;
        if (w.WindowState == System.Windows.WindowState.Minimized) w.WindowState = System.Windows.WindowState.Normal;
        w.Activate();
    }

    private void UpdateDockedPause() => ModelViewer.PausedForPopout = ModelPopouts.Any(w => w.Vm.FollowSelection);

    /// <summary>"Compare two skins (3D)": two skins of the shown model side by side (synchronized cameras).</summary>
    [RelayCommand]
    private async Task CompareSkins()
    {
        var vm = IsModelEntry ? await MakeCompareAsync() : null;
        if (vm == null)
        {
            Dialogs.Info("Compare two skins (3D)", "Select a model with at least two skins first, for example a creature display whose model other displays use too.");
            return;
        }
        var owner = System.Windows.Application.Current?.Windows.OfType<Views.OtherVersionWindow>().FirstOrDefault(w => ReferenceEquals(w.Vm, this));
        new Views.ModelCompareWindow(vm) { Owner = owner }.Show();
    }

    /// <summary>The compare window's view model for the shown model (screenshots, tests), or null.</summary>
    public Task<ModelCompareViewModel?> MakeCompareAsync() => ModelViewer.MakeCompareAsync();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchTimer.Stop();
        foreach (var w in ModelPopouts.ToList()) w.Close();
        ModelViewer.Clear();
        ModelCache.Clear();
        Viewer.Clear();
        Session.Dispose();
    }
}
