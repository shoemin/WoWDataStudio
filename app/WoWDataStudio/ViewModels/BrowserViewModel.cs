using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

public sealed partial class FilterChip : ObservableObject
{
    public AssetKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private int _count;
    public string Text => $"{Label}  {Count:N0}";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Text));
}

/// <summary>A row of the results list.</summary>
public sealed class ResultRow
{
    public ResultRow(IndexEntry e, bool modified, bool usesModified)
    {
        Entry = e;
        IsModified = modified;
        UsesModified = usesModified;
    }

    public IndexEntry Entry { get; }
    public string Name => Entry.Name;
    public string Type => Entry.KindLabel;
    public string IdText => Entry.Id > 0 && Entry.Kind != AssetKind.Character ? Entry.Id.ToString() : "";
    public long SortId => Entry.Id;
    public string Path => Entry.Path ?? "";
    public string Info => Entry.Info;
    public bool IsModified { get; }
    public bool UsesModified { get; }
    public bool ShowBadge => IsModified || UsesModified;
    public string BadgeText => IsModified ? "Modified" : UsesModified ? "Uses edit" : "";
    public string BadgeTip => IsModified ? "You changed this file (it is in your workspace)." : "This uses a texture you changed.";
}

public sealed partial class TextureItem : ObservableObject
{
    public TextureItem(TextureRef t, bool modified)
    {
        Path = t.Path;
        Role = t.Role;
        Archive = t.Archive;
        Note = t.Note;
        _isModified = modified;
    }

    public string Path { get; }
    public string Role { get; }
    public string? Archive { get; }
    public string Note { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool Exists => Archive != null;
    public string Tip => $"{Path}\n{(Archive != null ? "in " + Archive : "not in the game archives")}{(Note.Length > 0 ? "\n" + Note : "")}";
    [ObservableProperty] private bool _isModified;
}

public sealed class ChangeRow
{
    public ChangeRow(ChangeView v, ChangeSet? set)
    {
        View = v;
        Set = set;
    }

    public ChangeView View { get; }
    public ChangeSet? Set { get; }
    public string Path => View.Record.Path;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Status => View.StatusText;
    public string Edited => View.Record.Edited.ToString("yyyy-MM-dd HH:mm");
    public string Summary => View.Record.Summary;
    public string Format => View.Record.Original.Format ?? (View.Record.Original.IsNew ? "new file" : "");
    public string SetLabel => Set == null ? "" : $"{Set.Label} ({Set.Files.Count} file{(Set.Files.Count == 1 ? "" : "s")})";
    public bool InstalledLoose => View.InstalledLoose;
    public bool Packed => View.Packed;
    public string UsedBy => View.Record.Original.Models.Count == 0 ? string.Join(", ", View.Record.Original.UsedBy.Take(3)) :
        "Used by " + string.Join(", ", View.Record.Original.Models.Take(4).Select(System.IO.Path.GetFileName)) + (View.Record.Original.Models.Count > 4 ? ", ..." : "");
}

public enum SortColumn { Name, Type, Id, Path }

/// <summary>The answers of the import questions when nobody is asked (tests, screenshots): "edit both" for a ground
/// texture, keep the "_s" file's shine map, keep the black background of an additive texture black.</summary>
public sealed record ImportChoices(bool EditBoth = true, bool KeepShineMap = true, bool KeepBlackBlack = true);

public sealed partial class BrowserViewModel : ObservableObject, IModelViewerHost
{
    private readonly AppSession _session;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchTimer;
    private int _filterToken, _detailsToken;
    private HashSet<string> _modifiedKeys = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _modifiedOwners = new(StringComparer.Ordinal);

    public ObservableCollection<FilterChip> Chips { get; } = new();
    public TextureViewerViewModel Viewer { get; }
    public ModelViewerViewModel ModelViewer { get; }

    /// <summary>The Maps tab (index 2): maps, zones, minimap, tiles, 3D terrain, Noggit, map import, server update.</summary>
    public MapsViewModel Maps { get; }

    /// <summary>The Data tables tab (index 3, 0.7.0): the DBC editor.</summary>
    public DbcEditorViewModel Tables { get; }

    /// <summary>The Server data tab (index 4, 0.9.0): the server's world database.</summary>
    public ServerDataViewModel Server { get; }

    /// <summary>The Spell designer tab (index 5, 0.12.0).</summary>
    public SpellDesignerViewModel Spells { get; }

    /// <summary>The Classes and talents tab (index 6, 0.12.0); shares the Spell designer's session.</summary>
    public ClassTalentViewModel Classes { get; }

    /// <summary>The Quest chains tab (index 7, 0.13.0); shares the Spell designer's session.</summary>
    public QuestDesignerViewModel Quests { get; }
    /// <summary>0.14.0 (experimental): the Other versions tab.</summary>
    public OtherVersionsViewModel Versions { get; }

    /// <summary>Tab indexes: 0 Browse, 1 My changes, 2 Maps, 3 Data tables, 4 Server data, 5 Spell designer, 6 Classes and
    /// talents, 7 Quest chains.</summary>
    public const int MapsTab = 2;
    public const int TablesTab = 3;
    public const int ServerTab = 4;
    public const int SpellsTab = 5;
    public const int ClassesTab = 6;
    public const int QuestsTab = 7;
    public const int VersionsTab = 8;

    partial void OnSelectedTabChanged(int value)
    {
        // 0.8.0: changes made meanwhile with the command line (wds) show up when you switch tabs
        try { _session.Workspace?.Refresh(); }
        catch (IOException) { }
        if (value == MapsTab) _ = Maps.EnsureLoadedAsync();
        if (value == TablesTab) _ = Tables.EnsureLoadedAsync();
        if (value == ServerTab) _ = Server.EnsureLoadedAsync();
        // 0.12.0: the designers read the tables again when nothing is unsaved (another tab or wds may have changed them)
        if (value is SpellsTab or ClassesTab or QuestsTab && !Spells.IsDirty && Spells.IsLoaded && Spells.CurrentId == 0) Spells.Reset();
        if (value == SpellsTab) _ = Spells.EnsureLoadedAsync();
        if (value == ClassesTab) _ = Classes.EnsureLoadedAsync();
        if (value == QuestsTab) _ = Quests.EnsureLoadedAsync();
        if (value == VersionsTab) _ = Versions.EnsureLoadedAsync();
    }

    /// <summary>The view scrolls the texture viewer into sight (after a texture was opened from the 3D viewer).</summary>
    public event Action? TextureFocusRequested;
    public ObservableCollection<ChangeRow> Changes { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyMyChanges;
    [ObservableProperty] private IReadOnlyList<ResultRow> _results = Array.Empty<ResultRow>();
    [ObservableProperty] private string _resultCountText = "";
    [ObservableProperty] private ResultRow? _selectedRow;
    [ObservableProperty] private SortColumn _sortBy = SortColumn.Name;
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _helpOpen;

    // details
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailSubtitle = "";
    [ObservableProperty] private List<string> _chain = new();
    [ObservableProperty] private List<string> _facts = new();
    [ObservableProperty] private List<string> _usedBy = new();
    [ObservableProperty] private List<string> _models = new();
    [ObservableProperty] private ObservableCollection<TextureItem> _textures = new();
    [ObservableProperty] private TextureItem? _selectedTexture;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isModelEntry;
    [ObservableProperty] private bool _detailsLoading;

    public bool ServerConnected => _session.ServerConnected;
    public string ServerStatusText => ServerConnected ? "Names from server database: connected" : "Names from server database: not connected";
    public string ServerTip => ServerConnected
        ? $"Creature, item and game object names come from the server database ({_session.Settings.Server.Describe()}): {_session.ServerMessage}. Index built {_session.IndexBuilt}. Click to change the settings."
        : $"Without the server database, creatures, items and game objects are listed by their model and display id instead of their names. Reason: {_session.ServerMessage}. Click to open the settings.";
    public int ChangeCount => Changes.Count;

    // ---- the installed release patch (0.6.1)
    /// <summary>"A release patch built by WoW Data Studio is installed: N files, date" (or who made the one that is there).</summary>
    public string PatchStateText => SafePatchState()?.Text ?? "";
    public bool HasInstalledPatch => SafePatchState()?.Exists == true;

    /// <summary>Screenshots only: show the state of another (fake) game folder's patch.</summary>
    internal Func<PatchState?>? PatchStateProvider { get; set; }

    internal void NotifyPatchState()
    {
        OnPropertyChanged(nameof(PatchStateText));
        OnPropertyChanged(nameof(HasInstalledPatch));
    }

    private PatchState? SafePatchState()
    {
        try { return PatchStateProvider != null ? PatchStateProvider() : _session.Workspace?.GetPatchState(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    public string ChangesTabText => Changes.Count == 0 ? "My changes" : $"My changes ({Changes.Count})";
    public bool HasChanges => Changes.Count > 0;
    public string ClientDir => _session.Client?.ClientDir ?? "";

    // texture actions (enabled state)
    public bool CanExport => SelectedTexture is { Exists: true } || SelectedTexture?.IsModified == true;
    public bool CanImport => SelectedTexture != null;
    public bool CanCompare => SelectedTexture?.IsModified == true;
    public bool CanTestInGame => SelectedTexture?.IsModified == true;
    public bool CanRemoveFromGame => SelectedTexture != null && _session.Workspace?.Get(SelectedTexture.Path)?.Installed != null;
    public bool CanRevert => SelectedTexture?.IsModified == true;

    public BrowserViewModel(AppSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        Viewer = new TextureViewerViewModel(session);
        ModelViewer = new ModelViewerViewModel(session, ModelCache);
        ModelViewer.OpenTextureRequested += OpenTextureFromModel;
        Maps = new MapsViewModel(session, main, this);
        Tables = new DbcEditorViewModel(session, main, this);
        Server = new ServerDataViewModel(session, main, this);
        Spells = new SpellDesignerViewModel(session, main, this);
        Classes = new ClassTalentViewModel(session, main, Spells);
        Quests = new QuestDesignerViewModel(session, main, this, Spells);
        Versions = new OtherVersionsViewModel(session, main);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = ApplyFilterAsync(); };
        var defs = new (AssetKind Kind, string Label, string Tip)[]
        {
            (AssetKind.Creature, "Creatures", "Monsters and NPCs (by name when the server database is connected)."),
            (AssetKind.Character, "Characters", "Player races and their character-creation textures (skin, face, hair...)."),
            (AssetKind.Item, "Items", "Weapons, armour and other items, with their models, textures and icons."),
            (AssetKind.GameObject, "Game objects", "Chests, doors, plants, signs and other clickable objects."),
            (AssetKind.WorldObject, "World objects", "Trees, rocks, buildings and other models placed in the world (listed by file for now)."),
            (AssetKind.SpellEffect, "Spell effects", "Spell visual models (from the spell visual tables) and the spells that use them."),
            (AssetKind.Texture, "Textures", "Every texture (.blp) in the game."),
        };
        foreach (var d in defs)
        {
            var chip = new FilterChip { Kind = d.Kind, Label = d.Label, Tip = d.Tip };
            chip.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FilterChip.IsChecked)) _ = ApplyFilterAsync(); };
            Chips.Add(chip);
        }
        if (_session.Workspace != null) _session.Workspace.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnWorkspaceChanged);
        RefreshModifiedSets();
        RefreshChanges();
        UpdateCounts();
        InitPopouts();
        InitHotReload();
    }

    // ------------------------------------------------------------------ search and filters

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnOnlyMyChangesChanged(bool value) => _ = ApplyFilterAsync();

    private void UpdateCounts()
    {
        var counts = _session.Entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var c in Chips) c.Count = counts.GetValueOrDefault(c.Kind);
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

    public Task ApplyFilterNow() => ApplyFilterAsync();

    private async Task ApplyFilterAsync()
    {
        int token = ++_filterToken;
        var kinds = Chips.Where(c => c.IsChecked).Select(c => c.Kind).ToHashSet();
        bool only = OnlyMyChanges;
        var entries = _session.Entries;
        var modified = _modifiedKeys;
        var owners = _modifiedOwners;
        var sortBy = SortBy;
        bool asc = SortAscending;
        var query = new EntryQuery(SearchText, kinds, only, sortBy switch
        {
            SortColumn.Type => EntrySort.Type, SortColumn.Id => EntrySort.Id, SortColumn.Path => EntrySort.Path, _ => EntrySort.Name,
        }, asc);
        var rows = await Task.Run(() => EntrySearch.Run(entries, query, modified, owners).Select(h => new ResultRow(h.Entry, h.IsModified, h.UsesModified)).ToList());
        if (token != _filterToken) return;
        // keep the selected entry (and its details) when it is still in the new list
        var keep = SelectedRow?.Entry;
        _keepDetails = true;
        try
        {
            Results = rows;
            SelectedRow = keep == null ? null : rows.FirstOrDefault(r => ReferenceEquals(r.Entry, keep));
        }
        finally
        {
            _keepDetails = false;
        }
        if (keep != null && SelectedRow == null) _ = LoadDetailsAsync(null);
        ResultCountText = rows.Count == 1 ? "1 result" : $"{rows.Count:N0} results";
    }

    public static int Relevance(string name, string query, string[] words) => EntrySearch.Relevance(name, query, words);

    // ------------------------------------------------------------------ details

    private bool _keepDetails;

    partial void OnSelectedRowChanged(ResultRow? value)
    {
        if (!_keepDetails) _ = LoadDetailsAsync(value);
    }

    public async Task LoadDetailsAsync(ResultRow? row, string? preferTexture = null)
    {
        int token = ++_detailsToken;
        HasSelection = row != null;
        if (row == null || _session.Index == null)
        {
            Textures = new();
            Viewer.Clear();
            IsModelEntry = false;
            ModelViewer.Clear();
            return;
        }
        DetailsLoading = true;
        try
        {
            var index = _session.Index;
            var catalog = _session.Catalog;
            var d = await Task.Run(() => index.Details(row.Entry, catalog));
            if (token != _detailsToken) return;
            DetailTitle = d.Title;
            DetailSubtitle = d.Subtitle;
            Chain = d.Chain;
            Facts = d.Facts;
            UsedBy = d.UsedBy;
            Models = d.Models;
            IsModelEntry = row.Entry.Kind != AssetKind.Texture && d.Models.Count > 0;
            if (IsModelEntry)
            {
                _ = ModelViewer.LoadAsync(row.Entry, d);
                ModelEntryShown?.Invoke(row.Entry, d);
            }
            else ModelViewer.Clear();
            var ws = _session.Workspace!;
            if (row.Entry.Kind == AssetKind.Texture && row.Entry.Path != null)
            {
                preferTexture ??= row.Entry.Path;
                var path = row.Entry.Path;
                var extra = await Task.Run(() => TextureAdvice(path));
                if (token != _detailsToken) return;
                if (extra.Count > 0) Facts = d.Facts.Concat(extra).ToList();
            }
            Textures = new ObservableCollection<TextureItem>(d.Textures.Select(t => new TextureItem(t, ws.IsModified(t.Path))));
            SelectedTexture = Textures.FirstOrDefault(t => preferTexture != null && string.Equals(t.Path, preferTexture, StringComparison.OrdinalIgnoreCase))
                              ?? Textures.FirstOrDefault(t => t.Exists) ?? Textures.FirstOrDefault();
            if (SelectedTexture == null) Viewer.Clear();
        }
        finally
        {
            if (token == _detailsToken) DetailsLoading = false;
        }
    }

    partial void OnSelectedTextureChanged(TextureItem? value)
    {
        NotifyActions();
        if (value == null) Viewer.Clear();
        else _ = Viewer.LoadAsync(value.Path);
    }

    /// <summary>0.6.1: the advice for a texture: which ground file the game draws with the user's settings, and the
    /// additive / multiply note when models draw it that way.</summary>
    public List<string> TextureAdvice(string path) => _session.TextureAdvice(path);

    /// <summary>The additive / multiply note of a texture, from the blend modes of every model that draws it.</summary>
    public string? GlowNote(string path) => _session.GlowNote(path);

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CanTestInGame));
        OnPropertyChanged(nameof(CanRemoveFromGame));
        OnPropertyChanged(nameof(CanRevert));
    }

    /// <summary>A texture clicked in the 3D viewer: select it in the texture list (adding it when the list does not have
    /// it, e.g. a part of a character's composite skin) so the texture viewer and its actions show it.</summary>
    public void OpenTextureFromModel(string path, string role)
    {
        var item = Textures.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            var ws = _session.Workspace;
            item = new TextureItem(new TextureRef(path, role, _session.Catalog?.ArchiveOf(path)), ws?.IsModified(path) == true);
            Textures.Add(item);
        }
        SelectedTexture = item;
        TextureFocusRequested?.Invoke();
    }

    [RelayCommand]
    private void ShowTexture(TextureItem? t)
    {
        if (t != null) SelectedTexture = t;
    }

    /// <summary>Jump to a texture in the list (from "My changes").</summary>
    [RelayCommand]
    private async Task Locate(ChangeRow? row)
    {
        if (row == null) return;
        if (WoWData.Core.Maps.MapValidator.KindOf(row.Path) != null)
        {
            await ShowMapFileAsync(row.Path);
            return;
        }
        if (WoWData.Core.Dbc.DbcService.TableOf(row.Path) is { } table)
        {
            await ShowTableAsync(table);
            return;
        }
        if (WoWData.Core.ServerData.ServerChangeFiles.Parse(row.Path) is { } server)
        {
            await ShowServerTableAsync(server.Table);
            return;
        }
        SelectedTab = 0;
        bool model = row.Path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase);
        foreach (var c in Chips) c.IsChecked = model ? c.Kind != AssetKind.Texture : c.Kind == AssetKind.Texture;
        SearchText = model ? System.IO.Path.GetFileNameWithoutExtension(row.Path) : row.Path;
        _searchTimer.Stop();
        await ApplyFilterAsync();
        SelectedRow = Results.FirstOrDefault(r => string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase)) ?? Results.FirstOrDefault();
    }

    /// <summary>Show a map file of "My changes" in the Maps tab (its map, and its tile for an ADT).</summary>
    public async Task ShowMapFileAsync(string path)
    {
        SelectedTab = MapsTab;
        await Maps.EnsureLoadedAsync();
        var dir = path.Split('\\').ElementAtOrDefault(2);
        var map = Maps.Maps.FirstOrDefault(m => string.Equals(m.Directory, dir, StringComparison.OrdinalIgnoreCase));
        if (map == null) return;
        Maps.SelectedMap = map;
        if (WoWData.Core.Maps.MapValidator.TileXY(path) is { } xy) Maps.SelectTile(xy.X, xy.Y);
    }

    /// <summary>Show a table of "My changes" in the Data tables tab.</summary>
    public async Task ShowTableAsync(string table)
    {
        SelectedTab = TablesTab;
        await Tables.EnsureLoadedAsync();
        var item = Tables.AllTables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (item != null) await Tables.SelectTableAsync(item);
    }

    /// <summary>0.9.0: show a server table of "My changes" (or the Data tables tab's link) in the Server data tab.</summary>
    public async Task ShowServerTableAsync(string table, long? id = null)
    {
        SelectedTab = ServerTab;
        await Server.ShowTableAsync(table, id);
    }

    // ------------------------------------------------------------------ workspace state

    private void RefreshModifiedSets()
    {
        var ws = _session.Workspace;
        if (ws == null || _session.Index == null) return;
        var paths = ws.ModifiedPaths();
        _modifiedKeys = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        _modifiedOwners = paths.Count == 0 ? new HashSet<string>() : _session.Index.OwnersOfTextures(paths);
    }

    private void RefreshChanges()
    {
        Changes.Clear();
        var ws = _session.Workspace;
        if (ws == null) return;
        foreach (var v in ws.List()) Changes.Add(new ChangeRow(v, ws.Manifest.ChangeSets.GetValueOrDefault(v.Record.ChangeSet)));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ChangesTabText));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(PatchStateText));
        OnPropertyChanged(nameof(HasInstalledPatch));
    }

    private void OnWorkspaceChanged()
    {
        ModelViewer.InvalidateTextures();
        ModelViewer.RefreshModified();
        if (IsModelEntry && SelectedRow != null) _ = ModelViewer.ReloadLookAsync();
        RefreshPopouts();
        RefreshModifiedSets();
        RefreshChanges();
        foreach (var t in Textures) t.IsModified = _modifiedKeys.Contains(t.Path);
        NotifyActions();
        if (SelectedTexture != null) _ = Viewer.LoadAsync(SelectedTexture.Path);
        _ = ApplyFilterAsync();
    }

    public void NotifyServer()
    {
        OnPropertyChanged(nameof(ServerConnected));
        OnPropertyChanged(nameof(ServerStatusText));
        OnPropertyChanged(nameof(ServerTip));
        UpdateCounts();
        RefreshModifiedSets();
        _ = ApplyFilterAsync();
    }

    // ------------------------------------------------------------------ texture actions

    private string? CurrentTexturePath => SelectedTexture?.Path;

    [RelayCommand]
    private async Task ExportTexture()
    {
        var path = CurrentTexturePath;
        var ts = _session.Textures;
        if (path == null || ts == null) return;
        bool edited = Viewer.IsModified && Viewer.ShowEdited;
        var suggested = TextureService.SuggestedPng(_session.Settings.DefaultExportDir, path, edited);
        var file = Dialogs.PickSaveFile("Export texture as PNG", "PNG image (*.png)|*.png", System.IO.Path.GetFileName(suggested), System.IO.Path.GetDirectoryName(suggested));
        if (file == null) return;
        try
        {
            var ctx = new Dictionary<string, string> { ["selected"] = SelectedRow?.Entry.Key ?? "", ["name"] = DetailTitle };
            await _main.RunBusyAsync("Exporting the texture...", () => Task.Run(() => ts.Export(path, file, ctx, edited)));
            _session.Settings.LastImportDir = System.IO.Path.GetDirectoryName(file);
            _main.SaveSettings();
            _main.Toast($"Exported {System.IO.Path.GetFileName(file)} (plus {System.IO.Path.GetFileName(TextureService.SidecarFor(file))}, keep it next to the PNG).");
            if (_session.GroundPair(path) is { Specular: { } sp } pair && pair.IsSpecular(path))
                Dialogs.Info("A specular ground texture",
                    $"{System.IO.Path.GetFileName(sp)} is the _s variant of the ground texture {System.IO.Path.GetFileName(pair.Base)}. " + WoWData.Core.Maps.GroundTextures.SpecularAlphaNote +
                    $"\n\nEasier: export {System.IO.Path.GetFileName(pair.Base)} (no alpha), edit it, and import it with \"Edit both\": {System.IO.Path.GetFileName(sp)} then gets your colours and keeps its shine map.");
            Dialogs.ShowInExplorer(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TextureException or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Could not export the texture", e.Message);
        }
    }

    [RelayCommand]
    private async Task ImportTexture()
    {
        var target = CurrentTexturePath;
        if (target == null) return;
        var png = Dialogs.PickOpenFile($"Import edited PNG for {System.IO.Path.GetFileName(target)}", "PNG image (*.png)|*.png",
            _session.Settings.LastImportDir ?? System.IO.Path.Combine(_session.Settings.DefaultExportDir, System.IO.Path.GetDirectoryName(target) ?? ""));
        if (png == null) return;
        _session.Settings.LastImportDir = System.IO.Path.GetDirectoryName(png);
        _main.SaveSettings();
        await ImportPngAsync(png, target);
    }

    /// <summary>The whole import flow (Core: <see cref="TextureImportFlow"/>): read and check the PNG, the size / format /
    /// glow / shine / ground questions as dialogs, the before/after check window, then save into the workspace.</summary>
    public async Task<bool> ImportPngAsync(string png, string target, bool interactive = true, ImportChoices? choices = null)
    {
        choices ??= new ImportChoices();
        ITextureImportDecider decider = interactive ? new DialogImportDecider(png)
            : new FixedImportAnswers { EditBothFiles = choices.EditBoth, KeepShineMap = choices.KeepShineMap, KeepBlackBlack = choices.KeepBlackBlack };
        try
        {
            var r = await TextureImportFlow.RunAsync(_session, png, target, decider, (text, step) => _main.RunBusyAsync(text, () => Task.Run(step)));
            switch (r.Outcome)
            {
                case TextureImportOutcome.Saved:
                    _main.Toast(r.Message);
                    return true;
                case TextureImportOutcome.NothingToImport:
                    if (interactive) Dialogs.Info("Nothing to import", r.Message);
                    return false;
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TextureException or WorkspaceException or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Could not import the PNG", e.Message);
            return false;
        }
    }

    /// <summary>The import questions as the app's dialogs, and the check window (CompareWindow) before saving.</summary>
    private sealed class DialogImportDecider : ITextureImportDecider
    {
        private readonly string _png;

        public DialogImportDecider(string png) => _png = png;

        public string? WhichTarget(string exportedFrom, string selected)
        {
            int pick = Dialogs.Show("Which texture should this replace?",
                $"This PNG was exported from\n{exportedFrom}\nbut the selected texture is\n{selected}.",
                DialogKind.Question, new[] { $"Replace {System.IO.Path.GetFileName(exportedFrom)} (where it came from)", $"Replace {System.IO.Path.GetFileName(selected)} (selected)", "Cancel" });
            return pick == 0 ? exportedFrom : pick == 1 ? selected : null;
        }

        public (int W, int H)? Resize(WoWData.Core.Workspace.ImportPlan plan, (int W, int H) suggested, bool pow2)
        {
            var (sw, sh) = suggested;
            string msg = pow2
                ? $"Your image is {plan.Png.Width}x{plan.Png.Height}, the game's texture is {sw}x{sh}.\n\nA different size works if it stays a power of two (the model's texture coordinates are relative), but the file gets bigger or smaller and every mip level is re-encoded. Resizing to the original size is the safe choice."
                : $"Your image is {plan.Png.Width}x{plan.Png.Height}. The game only loads textures whose width and height are powers of two (2, 4, 8, ... 256, 512), at most 512.\n\nThe image can be resized to {sw}x{sh} (the original size). Resizing smooths the image a little; for the best result, resize it in your image editor and import again.";
            var buttons = pow2 ? new[] { $"Resize to {sw}x{sh}", $"Keep {plan.Png.Width}x{plan.Png.Height}", "Cancel" } : new[] { $"Resize to {sw}x{sh}", "Cancel" };
            int pick = Dialogs.Show("The image size does not match", msg, DialogKind.Warning, buttons);
            if (pick == 0) return suggested;
            if (pow2 && pick == 1) return (plan.Png.Width, plan.Png.Height);
            return null;
        }

        public ImportFormatChoice? Transparency(WoWData.Core.Workspace.ImportPlan plan)
        {
            int pick = Dialogs.Show("Your PNG has transparency",
                $"The game's texture has no transparency ({plan.Original?.FormatName}), but your PNG has transparent pixels.\n\n" +
                "Keep the original format: transparent pixels are shown opaque in game (the safe choice if you did not mean to add transparency).\n" +
                "Save as DXT5: transparency is kept, but the model's material may not use it, and the file is re-encoded completely.",
                DialogKind.Question, new[] { "Keep the original format", "Save as DXT5 (keeps transparency)", "Cancel" });
            return pick switch { 0 => ImportFormatChoice.SameAsOriginal, 1 => ImportFormatChoice.Dxt5KeepAlpha, _ => null };
        }

        public bool? KeepBlack(string glowNote, int liftedPixels, int pixels)
        {
            var (title, message, buttons) = KeepBlackDialog(glowNote, liftedPixels, pixels);
            int pick = Dialogs.Show(title, message, DialogKind.Question, buttons);
            return pick == 0 ? true : pick == 1 ? false : null;
        }

        public bool? KeepShine(string targetPath)
        {
            int pick = Dialogs.Show("Keep the shine map?", WoWData.Core.Maps.GroundTextures.SpecularAlphaNote +
                $"\n\nThe alpha channel of your PNG differs from {System.IO.Path.GetFileName(targetPath)}'s (image editors often change it).",
                DialogKind.Question, new[] { "Keep the original shine map (recommended)", "Use the alpha of my PNG", "Cancel" });
            return pick == 0 ? true : pick == 1 ? false : null;
        }

        public bool? EditBoth(WoWData.Core.Maps.GroundTexturePair pair, WoWData.Core.Maps.TerrainSpecularSettings game, string target)
        {
            var (title, message, buttons) = EditBothDialog(pair, game, target);
            int pick = Dialogs.Show(title, message, DialogKind.Question, buttons);
            return pick == 0 ? true : pick == 1 ? false : null;
        }

        public bool Confirm(TextureImportResult preview)
        {
            var plan = preview.Main!;
            var left = plan.OriginalData != null ? new TextureCompareSide(CompareViewModel.OriginalLabel, plan.OriginalData)
                : (ICompareSide)new TextureCompareSide(CompareViewModel.OriginalLabel, plan.Png, "the game has no such texture");
            var vm = new CompareViewModel(System.IO.Path.GetFileName(plan.TargetPath), left, new TextureCompareSide(CompareViewModel.EditLabel, plan.Encoded));
            var win = new CompareWindow(vm) { Owner = Application.Current.MainWindow };
            win.AsImportConfirmation($"Import {System.IO.Path.GetFileName(_png)} as {plan.TargetPath}", preview.Warnings, preview.Notes, preview.Facts);
            return win.ShowDialog() == true && win.Kept;
        }
    }

    /// <summary>The "edit both" question for a ground texture (also used by the screenshots).</summary>
    public static (string Title, string Message, string[] Buttons) EditBothDialog(WoWData.Core.Maps.GroundTexturePair pair, WoWData.Core.Maps.TerrainSpecularSettings game, string target) =>
        TextureImportFlow.EditBothQuestion(pair, game, target);

    /// <summary>The "keep black areas black" question for an additive texture (also used by the screenshots).</summary>
    public static (string Title, string Message, string[] Buttons) KeepBlackDialog(string glowNote, int lifted, int pixels) =>
        TextureImportFlow.KeepBlackQuestion(glowNote, lifted, pixels);

    public CompareViewModel? CompareFor(string path)
    {
        var ts = _session.Textures;
        var ws = _session.Workspace;
        if (ts == null || ws == null) return null;
        var edited = ws.ReadEdited(path);
        if (edited == null) return null;
        var orig = ts.ReadOriginal(path);
        ICompareSide left = orig != null ? new TextureCompareSide(CompareViewModel.OriginalLabel, orig.Value.Data)
            : new TextureCompareSide(CompareViewModel.OriginalLabel, WoWData.Core.Blp.BlpFile.Parse(edited).Decode(0), "the game has no such texture (new file)");
        return new CompareViewModel(System.IO.Path.GetFileName(path), left, new TextureCompareSide(CompareViewModel.EditLabel, edited));
    }

    [RelayCommand]
    private void Compare() => OpenCompare(CurrentTexturePath);

    [RelayCommand]
    private void CompareChange(ChangeRow? row) => OpenCompare(row?.Path);

    private void OpenCompare(string? path)
    {
        if (path == null) return;
        if (path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenModelCompareAsync(path);
            return;
        }
        if (WoWData.Core.Maps.MapValidator.KindOf(path) != null)
        {
            _ = Maps.OpenCompareForPathAsync(path);
            return;
        }
        if (WoWData.Core.ServerData.ServerChangeFiles.IsServerPath(path))
        {
            Server.CompareFile(path);
            return;
        }
        if (WoWData.Core.Dbc.DbcService.TableOf(path) is { } table && _session.Dbc is { } dbc)
        {
            try
            {
                var diff = dbc.CompareWithGame(table, Tables.Locale);
                new DbcDiffWindow($"{table}.dbc: your version and the game's", diff, $"Your version of {table}.dbc (in your workspace) compared with the game's file: {diff.Summary}", null)
                    { Owner = Application.Current.MainWindow }.Show();
            }
            catch (Exception e) when (e is WoWData.Core.Dbc.DbcException or IOException)
            {
                Dialogs.Error("Could not open the comparison", e.Message);
            }
            return;
        }
        try
        {
            var vm = CompareFor(path);
            if (vm == null)
            {
                Dialogs.Info("Nothing to compare", "This texture has not been changed.");
                return;
            }
            new CompareWindow(vm) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception e) when (e is IOException or WoWData.Core.Blp.BlpException)
        {
            Dialogs.Error("Could not open the comparison", e.Message);
        }
    }

    private async Task OpenModelCompareAsync(string path)
    {
        try
        {
            var vm = await ModelCompareForAsync(path);
            if (vm == null)
            {
                Dialogs.Info("Nothing to compare", "This model has not been changed.");
                return;
            }
            new ModelCompareWindow(vm) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception e) when (e is IOException or WoWData.Core.M2.M2Exception)
        {
            Dialogs.Error("Could not open the comparison", e.Message);
        }
    }

    [RelayCommand]
    private Task TestInGame() => InstallLooseAsync(CurrentTexturePath == null ? null : new[] { CurrentTexturePath });

    [RelayCommand]
    private Task InstallAllLoose() => InstallLooseAsync(null);

    internal async Task InstallLooseAsync(string[]? paths)
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        var doc = _main.LooseFilesDoc;
        int pick = Dialogs.Show("Test in game (install as loose files)",
            "This copies your edited " + (paths == null ? $"files ({Changes.Count})" : paths.Length == 1 && paths[0].EndsWith(".dbc", StringComparison.OrdinalIgnoreCase) ? "table" : "texture") + $" into the game folder:\n{ws.Paths.DataDir}\\<the file's game path>\n\n" +
            ChangeOperations.LooseFilesNote + "\n\n" +
            "Textures and tables are read when the game starts: restart the game to see the change, or, when the game runs with hot reload, reload them in the running game. Use \"Remove from game\" to undo.",
            DialogKind.Info, new[] { "Install into the game folder", "Cancel" }, null, "How the loose-file mode works (LOOSE_FILES_PATCH.md)", doc);
        if (pick != 0) return;
        try
        {
            List<DeliveryAction> actions;
            try
            {
                actions = await _main.RunBusyAsync("Installing...", () => Task.Run(() => ChangeOperations.InstallLoose(ws, paths, force: false)));
            }
            catch (WorkspaceException e) when (ChangeOperations.IsForeignFileRefusal(e))
            {
                if (!Dialogs.Confirm("A file is already there", e.Message + "\n\nReplace it? The existing file is backed up first and restored when you remove your change from the game.",
                        "Back it up and replace it", "Cancel", DialogKind.Warning)) return;
                actions = await _main.RunBusyAsync("Installing...", () => Task.Run(() => ChangeOperations.InstallLoose(ws, paths, force: true)));
            }
            await AfterGameFilesChangedAsync("Installed", $"{actions.Count} file{(actions.Count == 1 ? " was" : "s were")} copied into the game folder. Restart the game to see the change.",
                string.Join("\n", actions.Select(a => $"{a.Action}: {a.Dest}")), actions.Select(a => a.Path).ToList());
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not install into the game folder", e.Message);
        }
    }

    [RelayCommand]
    private Task RemoveFromGame() => RemoveLooseAsync(CurrentTexturePath == null ? null : new[] { CurrentTexturePath });

    [RelayCommand]
    private Task RemoveAllFromGame() => RemoveLooseAsync(null);

    private async Task RemoveLooseAsync(string[]? paths)
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        var patch = SafePatchState();
        if (!Dialogs.Confirm("Remove from game", "Remove " + (paths == null ? "all your loose files" : "this texture's loose file") +
                             " from the game folder? Your edit stays in your workspace, so you can install it again later." +
                             (patch?.Exists == true ? $"\n\nThis removes loose files only. {patch.Text} To take it out of the game, use \"Uninstall release patch (patch-3.MPQ)\" in My changes." : ""),
                "Remove from game")) return;
        try
        {
            var removed = ws.List().Where(v => v.Record.Installed != null && (paths == null || paths.Contains(v.Record.Path, StringComparer.OrdinalIgnoreCase))).Select(v => v.Record.Path)
                .Concat(paths == null ? ws.Manifest.OrphanInstalls.Keys : Enumerable.Empty<string>()).ToList();
            var done = await _main.RunBusyAsync("Removing...", () => Task.Run(() => ws.RemoveLoose(paths)));
            if (done.Count == 0) Dialogs.Info("Removed from the game folder", "Nothing was installed.");
            else await AfterGameFilesChangedAsync("Removed from the game folder", "Restart the game to see the original again.", string.Join("\n", done), removed);
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not remove the files", e.Message);
        }
    }

    [RelayCommand]
    private Task Revert() => RevertAsync(CurrentTexturePath, null);

    [RelayCommand]
    private Task RevertChange(ChangeRow? row) => RevertAsync(row?.Path, null);

    [RelayCommand]
    private Task RevertChangeSet(ChangeRow? row) => RevertAsync(null, row?.Set);

    private async Task RevertAsync(string? path, ChangeSet? set)
    {
        var ws = _session.Workspace;
        if (ws == null || (path == null && set == null)) return;
        var (question, refused) = ChangeOperations.RevertQuestion(ws, set == null ? path : null, set?.Id);
        if (refused)
        {
            Dialogs.Error("Could not revert", question);   // 0.14.0: a newer import changed one of its files again
            return;
        }
        if (!Dialogs.Confirm("Revert my change", question, "Revert", "Keep my change", DialogKind.Warning)) return;
        try
        {
            var reverted = set != null ? ChangeOperations.FilesOf(ws, set.Id) : new List<string> { path! };
            var installed = reverted.Where(p => ws.Get(p)?.Installed != null).ToList();
            var done = await _main.RunBusyAsync("Reverting...", () => Task.Run(() => ChangeOperations.Revert(_session, set == null ? path : null, set?.Id)));
            _main.Toast(done.LastOrDefault() ?? "Reverted.");
            await OfferReloadIfReadyAsync(installed, "Reverted");   // 0.11.0: the running game can go back to the original at once
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not revert", e.Message);
        }
    }

    /// <summary>The question before uninstalling (also used by the screenshots).</summary>
    public static (string Title, string Message, string Yes) UninstallDialog(PatchState st, string backupDir) => ChangeOperations.UninstallQuestion(st, backupDir);

    /// <summary>"Uninstall release patch (patch-3.MPQ)": takes the app's patch out of the game and puts back the one that was
    /// there before. A patch the app did not create is only moved out (to the backup folder) after a clear question.</summary>
    [RelayCommand]
    private async Task UninstallPatch()
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        var st = ws.GetPatchState();
        if (!st.Exists)
        {
            Dialogs.Info("Uninstall release patch (patch-3.MPQ)", st.Text);
            return;
        }
        bool notOurs = st.Owner != PatchOwner.Ours;
        var (title, message, yes) = UninstallDialog(st, ws.Paths.BackupDir);
        if (!Dialogs.Confirm(title, message, yes, "Cancel", notOurs ? DialogKind.Warning : DialogKind.Question)) return;
        try
        {
            var (done, reload) = await _main.RunBusyAsync("Uninstalling patch-3.MPQ...", () => Task.Run(() => ChangeOperations.UninstallPatchLiveAsync(ws, notOurs, HotReloadLocator)));
            RefreshChanges();
            Dialogs.Success("Release patch uninstalled", string.Join("\n\n", done), reload?.Report == null ? null : WoWData.Core.HotReload.HotReloadService.Details(reload.Report));
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not uninstall the patch", e.Message);
        }
    }

    [RelayCommand]
    private async Task BuildPatch()
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        var gameFiles = Changes.Where(c => !c.View.Record.IsServerEdit).ToList();
        if (gameFiles.Count == 0)
        {
            Dialogs.Info("Nothing to build", Changes.Count > 0
                ? "You only have server edits: they go to the server with \"Apply to server...\" or an SQL patch (Server data tab), not into patch-3.MPQ."
                : "You have no changes yet. Import an edited texture first.");
            return;
        }
        var win = new BuildPatchWindow(ws, gameFiles, _session.Client!.DataDir) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        try
        {
            bool install = win.InstallIntoGame;
            var (result, reload) = await _main.RunBusyAsync("Building patch-3.MPQ...", () => Task.Run(() => ChangeOperations.BuildPatchLiveAsync(ws, install, HotReloadLocator)));
            var build = result.Build;
            Dialogs.Success("Release patch built", result.Message,
                string.Join("\n", build.Files) + (reload?.Report == null ? "" : "\n\nIn the running game:\n" + WoWData.Core.HotReload.HotReloadService.Details(reload.Report)));
            Dialogs.ShowInExplorer(build.InstalledTo ?? build.Path);
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException or WoWData.Core.Mpq.MpqWriteException)
        {
            Dialogs.Error("Could not build the patch", e.Message);
        }
    }

    [RelayCommand]
    private void OpenWorkspaceFolder()
    {
        var dir = _session.Client?.WorkspaceDir;
        if (dir == null) return;
        Directory.CreateDirectory(dir);
        Dialogs.OpenPath(dir);
    }

    /// <summary>Tools > Compare two skins: the dual 3D view (synchronized cameras) that Phase 3c uses for original vs. edited.</summary>
    [RelayCommand]
    private async Task CompareSkins()
    {
        var vm = IsModelEntry ? await ModelViewer.MakeCompareAsync() : null;
        if (vm == null)
        {
            Dialogs.Info("Compare two skins", "Select a model with at least two skins first, for example the Mottled Boar (creature) or a character race (compares your chosen look with the default one).");
            return;
        }
        new ModelCompareWindow(vm) { Owner = Application.Current.MainWindow }.Show();
    }

    [RelayCommand]
    private void ToggleHelp() => HelpOpen = !HelpOpen;

    /// <summary>Help > About WoW Data Studio: the version and build (the same as "wds version": both read
    /// WoWData.Core.StudioInfo) and where the command line is.</summary>
    public static string AboutText()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, WoWData.Core.StudioInfo.CliName + ".exe");
        return $"WoW Data Studio {WoWData.Core.StudioInfo.Display}\n\n" +
               "Find, view, export and re-import World of Warcraft 1.12.1 game data: textures, models, maps and data tables.\n\n" +
               "The command line: every feature of the app is also a command of wds" + (File.Exists(cli) ? $" ({cli})" : "") +
               ", the same version as the app. Run \"wds --help\" in the \"WoW Data Studio command prompt\" of the Start menu (Help > The command line (wds)).";
    }

    [RelayCommand]
    private void About() => Dialogs.Info("About WoW Data Studio", AboutText());

    [RelayCommand]
    private void ShowMaps() => SelectedTab = MapsTab;

    [RelayCommand]
    private void ShowTables() => SelectedTab = TablesTab;

    [RelayCommand]
    private void ShowServer() => SelectedTab = ServerTab;

    [RelayCommand]
    private void ShowSpellDesigner() => SelectedTab = SpellsTab;

    [RelayCommand]
    private void ShowClasses() => SelectedTab = ClassesTab;

    [RelayCommand]
    private void ShowQuests() => SelectedTab = QuestsTab;

    [RelayCommand]
    private void ShowVersions() => SelectedTab = VersionsTab;

    [RelayCommand]
    private void OpenSettings() => _main.OpenSettingsCommand.Execute(null);
}
