using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps;
using WoWData.Core.Session;
using WoWData.Core.Workspace;
using WoWDataStudio.Helpers;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// The Maps tab (docs\manual\maps-and-terrain.md): the maps of Map.dbc (continents, dungeons, raids, battlegrounds), the
/// zones of the selected map (AreaTable), its minimap overview (click a tile), the tile's details with a "Used in game"
/// style list of what is placed on it, the 3D terrain preview, and the map workflow: Edit in Noggit, Import map
/// changes (review + compare), Test in game, Build release patch and Update server maps.
/// </summary>
public sealed partial class MapsViewModel : ObservableObject
{
    private readonly AppSession _session;
    public AppSession Session => _session;
    private readonly MainViewModel _main;
    private readonly BrowserViewModel _browser;
    private int _overviewToken, _tileToken, _previewToken;
    private bool _loaded;

    public MapsViewModel(AppSession session, MainViewModel main, BrowserViewModel browser)
    {
        _session = session;
        _main = main;
        _browser = browser;
        TileUsage = new UsedInGameViewModel(session) { CanNavigate = k => browser.FindEntryByKey(k) != null };
        TileUsage.NavigateRequested += k => _ = browser.NavigateToKeyAsync(k, false);
        TileUsage.SettingsRequested += () => main.OpenSettingsCommand.Execute(null);
        TileUsage.RefreshServerRequested += async () =>
        {
            await main.RefreshServerNames();
            await LoadTileAsync();
        };
        Scene.Background = new System.Numerics.Vector3(0.55f, 0.68f, 0.82f);
        if (session.Workspace != null) session.Workspace.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(RefreshChangedTiles);
    }

    public ObservableCollection<MapInfo> Maps { get; } = new();
    [ObservableProperty] private MapInfo? _selectedMap;
    [ObservableProperty] private List<ZoneInfo> _zones = new();
    [ObservableProperty] private ZoneInfo? _selectedZone;
    [ObservableProperty] private string _status = "";

    // overview
    [ObservableProperty] private BitmapSource? _overview;
    [ObservableProperty] private int _overviewCell = 32;
    [ObservableProperty] private int _originX;
    [ObservableProperty] private int _originY;
    [ObservableProperty] private bool[]? _tiles;
    [ObservableProperty] private string _overviewStatus = "";
    [ObservableProperty] private IReadOnlyCollection<Point>? _zoneTiles;
    [ObservableProperty] private IReadOnlyCollection<Point>? _changedTiles;
    [ObservableProperty] private IReadOnlyCollection<Point>? _pendingTiles;
    [ObservableProperty] private Point? _selectedTile;

    // tile
    [ObservableProperty] private bool _hasTile;
    [ObservableProperty] private string _tileTitle = "";
    [ObservableProperty] private string _tileSubtitle = "";
    [ObservableProperty] private List<string> _tileFacts = new();
    [ObservableProperty] private bool _isTileModified;
    [ObservableProperty] private bool _isTileLoading;
    public UsedInGameViewModel TileUsage { get; }
    public TerrainTile? CurrentTile { get; private set; }

    // 3D preview
    public TerrainScene Scene { get; } = new();
    public OrbitCamera Camera { get; } = new();
    [ObservableProperty] private string _previewStatus = "";
    [ObservableProperty] private bool _previewLoading;
    [ObservableProperty] private bool _includeNeighbours;
    [ObservableProperty] private bool _showDoodads = true;
    [ObservableProperty] private bool _showWmos = true;
    [ObservableProperty] private bool _showWater = true;
    [ObservableProperty] private bool _showTextures = true;
    [ObservableProperty] private bool _wireframe;
    /// <summary>Orbit or fly in the docked 3D preview (0.5.1).</summary>
    [ObservableProperty] private Rendering.CameraNavigation _navigation;
    /// <summary>The help overlay of the docked 3D preview (H).</summary>
    [ObservableProperty] private bool _showHelp;
    /// <summary>"Open in new window" over the 3D preview (set by the browser).</summary>
    [ObservableProperty] private System.Windows.Input.ICommand? _popOutCommand;

    /// <summary>What the 3D preview's asset shows: map, tile, with or without the neighbours (a pop-out window following
    /// the selection takes the same asset instead of building it again).</summary>
    public (MapInfo Map, int X, int Y, bool Neighbours)? PreviewKey { get; private set; }

    /// <summary>A tile was selected (map, x, y): a following pop-out window starts showing it.</summary>
    public event Action<MapInfo, int, int>? TileSelected;
    /// <summary>The 3D preview's asset was built: (map, x, y, neighbours, asset).</summary>
    public event Action<MapInfo, int, int, bool, TerrainAsset>? PreviewShown;

    public string ChangedSummary
    {
        get
        {
            int n = MapChangeCount;
            return n == 0 ? "No map changes in your workspace yet." : $"{n} map file{(n == 1 ? "" : "s")} changed in your workspace (orange tiles).";
        }
    }

    public int MapChangeCount => _session.Workspace?.ModifiedPaths().Count(p => MapValidator.KindOf(p) != null) ?? 0;
    public bool HasMapChanges => MapChangeCount > 0;

    partial void OnShowDoodadsChanged(bool value) => Scene.ShowDoodads = value;
    partial void OnShowWmosChanged(bool value) => Scene.ShowWmos = value;
    partial void OnShowWaterChanged(bool value) => Scene.ShowWater = value;
    partial void OnShowTexturesChanged(bool value) => Scene.ShowTextures = value;
    partial void OnWireframeChanged(bool value) => Scene.Wireframe = value;
    partial void OnIncludeNeighboursChanged(bool value) => _ = BuildPreviewAsync();

    // ------------------------------------------------------------------ 0.13.0: pins and "Pick on the map" (Quest chains tab)

    [ObservableProperty] private IReadOnlyList<Controls.MapPin>? _pins;
    [ObservableProperty] private string _pinsText = "";
    [ObservableProperty] private bool _isPicking;
    [ObservableProperty] private string _pickBanner = "";
    private Action<long, double, double>? _pick;
    public bool HasPins => Pins is { Count: > 0 };
    partial void OnPinsChanged(IReadOnlyList<Controls.MapPin>? value) => OnPropertyChanged(nameof(HasPins));

    /// <summary>Show positions on a map (quest givers, their spawns): the map is selected, pins drawn, the first one centred.</summary>
    public async Task ShowPinsAsync(long mapId, IReadOnlyList<(double X, double Y, string Label, bool IsNew)> pins, string text)
    {
        await MapsListedAsync();
        var map = Maps.FirstOrDefault(m => m.Id == mapId);
        if (map == null) { Status = $"Map {mapId} is not one of the game's maps with terrain."; return; }
        if (SelectedMap != map) SelectedMap = map;
        Pins = pins.Select(p => Controls.MapPin.FromWorld(p.X, p.Y, p.Label, p.IsNew)).ToList();
        PinsText = text;
        if (Pins.Count > 0)
        {
            int tx = (int)Math.Floor(Pins[0].TileX), ty = (int)Math.Floor(Pins[0].TileY);
            // the map's overview is built the first time (and cached): wait for it, up to three minutes
            for (int i = 0; i < 1800 && Overview == null; i++) await Task.Delay(100);
            SelectTile(tx, ty);
            ZoomRequested?.Invoke(Pins[0].TileX, Pins[0].TileY);
        }
    }

    /// <summary>The view zooms in on a point of the overview (tile units), after "Show on map".</summary>
    public event Action<double, double>? ZoomRequested;

    /// <summary>The map list is read (the tab may have started reading it a moment ago).</summary>
    private async Task MapsListedAsync()
    {
        await EnsureLoadedAsync();
        for (int i = 0; i < 600 && Maps.Count == 0; i++) await Task.Delay(100);
    }

    [RelayCommand]
    private void ClearPins()
    {
        Pins = null;
        PinsText = "";
    }

    /// <summary>"Pick on the map": the next click on the map overview gives a world position to <paramref name="done"/>.</summary>
    public async Task BeginPickAsync(string what, Action<long, double, double> done, long? mapId = null)
    {
        await MapsListedAsync();
        if (mapId != null && Maps.FirstOrDefault(m => m.Id == mapId) is { } map && SelectedMap != map) SelectedMap = map;
        _pick = done;
        PickBanner = $"Click on the map where {what} should stand (pick another map on the left first if needed). The height comes from the terrain.";
        IsPicking = true;
    }

    [RelayCommand]
    private void CancelPick()
    {
        _pick = null;
        IsPicking = false;
    }

    /// <summary>The map overview was clicked at a point (tile units).</summary>
    public void PointPicked(double tileX, double tileY)
    {
        if (!IsPicking || _pick == null || SelectedMap == null) return;
        double x = (32 - tileY) * WoWData.Core.Maps.TerrainMath.TileSize, y = (32 - tileX) * WoWData.Core.Maps.TerrainMath.TileSize;
        var done = _pick;
        _pick = null;
        IsPicking = false;
        Pins = new List<Controls.MapPin> { new(tileX, tileY, "picked", true) };
        PinsText = $"Picked: map {SelectedMap.Id} at {x:0.#}, {y:0.#}.";
        done(SelectedMap.Id, x, y);
    }

    /// <summary>Called when the tab is shown: loads the map list once.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Status = "Reading the maps...";
            var cat = await Task.Run(() => _session.MapCatalog);
            if (cat == null) return;
            foreach (var m in MapOperations.ListedMaps(cat)) Maps.Add(m);
            Status = $"{Maps.Count} maps: {Maps.Count(m => m.Kind == "Continent")} continents, {Maps.Count(m => m.InstanceType == 1)} dungeons, {Maps.Count(m => m.InstanceType == 2)} raids, {Maps.Count(m => m.InstanceType == 3)} battlegrounds.";
            SelectedMap ??= Maps.FirstOrDefault(m => m.Directory.Equals("Azeroth", StringComparison.OrdinalIgnoreCase)) ?? Maps.FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or WoWData.Core.Dbc.DbcException)
        {
            Status = "The maps could not be read: " + e.Message;
        }
    }

    partial void OnSelectedMapChanged(MapInfo? value)
    {
        Zones = new();
        SelectedZone = null;
        SelectedTile = null;
        HasTile = false;
        CurrentTile = null;
        Scene.Asset = null;
        PreviewKey = null;
        TileUsage.Clear();
        if (value != null) _ = LoadMapAsync(value);
    }

    private async Task LoadMapAsync(MapInfo map)
    {
        int token = ++_overviewToken;
        var cat = _session.MapCatalog!;
        Tiles = map.Tiles;
        Overview = null;
        RefreshChangedTiles();
        if (map.IsGlobalWmo)
        {
            OverviewStatus = $"{map.Name} is a single building ({map.GlobalWmo}): it has no terrain tiles to show or edit here. Its building can be looked at in the Browse tab.";
            return;
        }
        OverviewStatus = "Building the minimap overview...";
        int cell = map.TileCount > 400 ? 32 : map.TileCount > 60 ? 64 : 128;
        var progress = new Progress<(int, int)>(p => { if (token == _overviewToken) OverviewStatus = $"Building the minimap overview... {p.Item1} of {p.Item2} tiles"; });
        try
        {
            var (ov, zones) = await Task.Run(() =>
            {
                var o = cat.Overview(map, cell, _session.Client?.MinimapCacheDir, (a, b) => ((IProgress<(int, int)>)progress).Report((a, b)));
                var z = cat.Zones(map);
                return (o, z);
            });
            if (token != _overviewToken) return;
            OverviewCell = ov.Cell;
            OriginX = ov.MinX;
            OriginY = ov.MinY;
            Overview = ImageSources.ToBitmap(ov.Image, AlphaView.IgnoreAlpha);
            Zones = zones;
            OverviewStatus = $"{map.TileCount} tiles, {zones.Count} zones{(ov.FromCache ? "" : $" (minimap overview built and cached)")}. Click a tile.";
            _ = RefreshPendingAsync(map);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            if (token == _overviewToken) OverviewStatus = "The minimap could not be built: " + e.Message;
        }
    }

    partial void OnSelectedZoneChanged(ZoneInfo? value)
    {
        ZoneTiles = value?.Tiles.Select(t => new Point(t.X, t.Y)).ToList();
        if (value != null && value.Tiles.Count > 0 && (SelectedTile == null || !value.Tiles.Contains(((int)SelectedTile.Value.X, (int)SelectedTile.Value.Y))))
        {
            // the tile nearest the zone's centre
            double cx = value.Tiles.Average(t => t.X), cy = value.Tiles.Average(t => t.Y);
            var best = value.Tiles.OrderBy(t => (t.X - cx) * (t.X - cx) + (t.Y - cy) * (t.Y - cy)).First();
            SelectTile(best.X, best.Y);
            CenterRequested?.Invoke(best.X, best.Y);
        }
    }

    /// <summary>The view centres the overview on a tile (after picking a zone).</summary>
    public event Action<int, int>? CenterRequested;

    public void SelectTile(int x, int y)
    {
        if (SelectedMap == null || !SelectedMap.HasTile(x, y)) return;
        SelectedTile = new Point(x, y);
        TileSelected?.Invoke(SelectedMap, x, y);
        _ = LoadTileAsync();
    }

    public int TileX => SelectedTile is { } p ? (int)p.X : -1;
    public int TileY => SelectedTile is { } p ? (int)p.Y : -1;
    public string? TilePath => SelectedMap != null && SelectedTile != null ? SelectedMap.TilePath(TileX, TileY) : null;

    private async Task LoadTileAsync()
    {
        var map = SelectedMap;
        if (map == null || SelectedTile == null) return;
        int x = TileX, y = TileY;
        int token = ++_tileToken;
        IsTileLoading = true;
        HasTile = true;
        TileTitle = $"{map.Name}, tile {x},{y}";
        TileSubtitle = map.TilePath(x, y);
        try
        {
            var summary = await Task.Run(() => MapOperations.Summarize(_session, map, x, y));
            if (token != _tileToken) return;
            var report = summary.Report;
            CurrentTile = summary.Tile;
            IsTileModified = summary.Modified;
            TileFacts = summary.Facts;
            TileUsage.Report = report;
            TileUsage.TimingText = $"Read in {report.Milliseconds:0} ms";
            OnPropertyChanged(nameof(TileX));
            OnPropertyChanged(nameof(TileY));
            OnPropertyChanged(nameof(TilePath));
            _ = BuildPreviewAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (token == _tileToken) TileFacts = new List<string> { "The tile could not be read: " + e.Message };
        }
        finally
        {
            if (token == _tileToken) IsTileLoading = false;
        }
    }

    /// <summary>The tiles of the 3D preview: the selected one first, then (optionally) its 8 neighbours.</summary>
    public List<TerrainTile> PreviewTiles(MapInfo map, int x, int y, bool neighbours)
    {
        var list = new List<TerrainTile>();
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dy != 0) && !neighbours) continue;
                int tx = x + dx, ty = y + dy;
                if (!map.HasTile(tx, ty)) continue;
                var data = _session.ReadCurrent(map.TilePath(tx, ty));
                if (data == null) continue;
                var t = TerrainTile.Load(data, map.Directory, tx, ty);
                if (dx == 0 && dy == 0) list.Insert(0, t);
                else list.Add(t);
            }
        return list;
    }

    public TerrainAsset BuildAsset(IReadOnlyList<TerrainTile> tiles, CancellationToken ct = default)
    {
        var res = _session.Models!;
        return TerrainAsset.Build(tiles, p => _session.ReadCurrent(p), res.LoadBlp, new TerrainBuildOptions(SpecularGround: _session.SpecularGround, Exists: _session.FileExists), ct);
    }

    // ------------------------------------------------------------------ ground textures: base or "_s" (0.6.1)

    /// <summary>The terrain previews draw the "_s" ground textures (as the game does with terrain specular on). Starts as
    /// the game's own setting (Config.wtf); ticking it differently is remembered.</summary>
    public bool SpecularGround
    {
        get => _session.SpecularGround;
        set
        {
            if (value == SpecularGround) return;
            _session.Settings.GroundTextures = value == _session.GameSpecular.UsesSpecularTextures ? GroundTextureMode.AsTheGame
                : value ? GroundTextureMode.Specular : GroundTextureMode.Base;
            _main.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroundRuleText));
            _ = BuildPreviewAsync();
        }
    }

    /// <summary>What the game draws with the user's settings, and what the preview shows.</summary>
    public string GroundRuleText => _session.GroundRuleText + (SpecularGround == _session.GameSpecular.UsesSpecularTextures
        ? " The 3D view shows the same."
        : SpecularGround ? " The 3D view shows the _s files anyway (your choice)." : " The 3D view shows the base files anyway (your choice).");

    private CancellationTokenSource? _previewCts;

    public async Task BuildPreviewAsync()
    {
        var map = SelectedMap;
        if (map == null || SelectedTile == null || _session.Models == null) return;
        int x = TileX, y = TileY;
        int token = ++_previewToken;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        bool neighbours = IncludeNeighbours;
        PreviewLoading = true;
        PreviewStatus = neighbours ? "Loading the tile and its neighbours in 3D..." : "Loading the tile in 3D...";
        try
        {
            var asset = await Task.Run(() =>
            {
                var tiles = PreviewTiles(map, x, y, neighbours);
                return tiles.Count == 0 ? null : BuildAsset(tiles, cts.Token);
            }, cts.Token);
            if (token != _previewToken || asset == null) return;
            Scene.Asset = asset;
            PreviewKey = (map, x, y, neighbours);
            TerrainScene.FrameTile(Camera, asset);
            PreviewStatus = asset.Summary;
            PreviewShown?.Invoke(map, x, y, neighbours, asset);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            if (token == _previewToken) PreviewStatus = "The 3D preview could not be built: " + e.Message;
        }
        finally
        {
            if (token == _previewToken) PreviewLoading = false;
        }
    }

    [RelayCommand]
    private void ResetView() => Camera.ResetView();

    // ------------------------------------------------------------------ changed / pending tiles

    private string MapPrefix(MapInfo map) => $@"WORLD\MAPS\{map.Directory.ToUpperInvariant()}\";

    public void RefreshChangedTiles()
    {
        var map = SelectedMap;
        var ws = _session.Workspace;
        OnPropertyChanged(nameof(ChangedSummary));
        OnPropertyChanged(nameof(MapChangeCount));
        OnPropertyChanged(nameof(HasMapChanges));
        if (map == null || ws == null)
        {
            ChangedTiles = null;
            return;
        }
        ChangedTiles = MapOperations.ChangedTiles(ws, map).Select(t => new Point(t.X, t.Y)).ToList();
        if (TilePath != null) IsTileModified = ws.IsModified(TilePath);
        if (map != null) _ = RefreshPendingAsync(map);
    }

    /// <summary>Tiles Noggit saved that differ from what the app has (not imported yet).</summary>
    private async Task RefreshPendingAsync(MapInfo map)
    {
        if (_session.Noggit == null) return;
        var pending = await Task.Run(() => MapOperations.PendingTiles(_session, map).Select(t => new Point(t.X, t.Y)).ToList());
        if (SelectedMap == map) PendingTiles = pending;
    }

    // ------------------------------------------------------------------ Edit in Noggit

    /// <summary>The map commands need a selected map: from the Tools menu, open the Maps tab and say so.</summary>
    private bool RequireMap()
    {
        if (SelectedMap != null) return true;
        _browser.SelectedTab = BrowserViewModel.MapsTab;
        Dialogs.Info("Select a map first", "In the Maps tab, pick a map on the left and click a tile on the minimap, then try again.");
        return false;
    }

    [RelayCommand]
    private async Task EditInNoggit()
    {
        if (!RequireMap()) return;
        var map = SelectedMap;
        var noggit = _session.Noggit;
        if (map == null || noggit == null) return;
        if (map.IsGlobalWmo)
        {
            Dialogs.Info("Edit in Noggit", $"{map.Name} is a single building without terrain; Noggit edits terrain maps.");
            return;
        }
        int x = TileX >= 0 ? TileX : map.Bounds.MinX, y = TileY >= 0 ? TileY : map.Bounds.MinY;
        try
        {
            NoggitPrepared prepared;
            try
            {
                prepared = await _main.RunBusyAsync("Preparing Noggit's client view...", () => Task.Run(() => MapOperations.PrepareNoggit(_session, map, x, y, false)));
            }
            catch (NoggitException e) when (e.NeedsCopy)
            {
                if (!Dialogs.Confirm("Copy the game's archives for Noggit?", e.Message, "Copy the archives (about 5 GB)", "Cancel", DialogKind.Question)) return;
                prepared = await _main.RunBusyAsync("Copying the game's archives for Noggit (this takes a few minutes)...", () => Task.Run(() => MapOperations.PrepareNoggit(_session, map, x, y, true)));
            }
            var vm = new NoggitLaunchViewModel(prepared.Info, prepared.NoggitExe, prepared.Synced, prepared.AlreadyRunning);
            var win = new NoggitWindow(vm) { Owner = Application.Current.MainWindow };
            win.ShowDialog();
            if (vm.SettingsRequested) _main.OpenSettingsCommand.Execute(null);
        }
        catch (Exception e) when (e is NoggitException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Dialogs.Error("Could not prepare Noggit", e.Message);
        }
    }

    // ------------------------------------------------------------------ Import map changes

    [RelayCommand]
    private async Task ImportMapChanges()
    {
        if (!RequireMap()) return;
        var map = SelectedMap;
        var noggit = _session.Noggit;
        if (map == null || noggit == null || _session.Data == null || _session.Workspace == null) return;
        try
        {
            var analysis = await _main.RunBusyAsync("Checking the files Noggit saved...", () => Task.Run(() => MapOperations.AnalyzeNoggit(_session, map)));
            if (analysis.Changes.Count == 0 || analysis.NothingChanged)
            {
                Dialogs.Info("Import map changes", string.Join("\n\n", analysis.General.DefaultIfEmpty("No changes found.")), $"Noggit's save folder for this map: {noggit.SaveDir(map)}");
                return;
            }
            await ReviewAndCommitAsync(analysis);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceException)
        {
            Dialogs.Error("Could not import the map changes", e.Message);
        }
    }

    public MapImportViewModel MakeImportViewModel(MapImportAnalysis analysis)
    {
        var vm = new MapImportViewModel(analysis);
        vm.CompareRequested = row => _ = OpenCompareAsync(row.Change.BaseBytes, row.Change.NewBytes, row.Change.GamePath, row.Change.BaseLabel == "the game's file" ? CompareViewModel.OriginalLabel : "Your earlier import", row.Change.NewLabel + " (to import)");
        return vm;
    }

    private async Task ReviewAndCommitAsync(MapImportAnalysis analysis)
    {
        var vm = MakeImportViewModel(analysis);
        var win = new MapImportWindow(vm) { Owner = Application.Current.MainWindow };
        if (vm.FromBlender) Manual.SetTopic(win, "terrain-in-blender");     // F1: the terrain-in-Blender chapter
        if (win.ShowDialog() != true) return;
        var ws = _session.Workspace!;
        var cs = await _main.RunBusyAsync("Saving the map changes to your workspace...", () => Task.Run(() => MapImporter.Commit(analysis, ws)));
        _main.Toast($"Saved {cs.Files.Count} map file{(cs.Files.Count == 1 ? "" : "s")} into your workspace (\"{cs.Label}\"). Next: Test in game or Build release patch, then Update server maps.");
        RefreshChangedTiles();
        await LoadTileAsync();
    }

    // ------------------------------------------------------------------ compare

    [RelayCommand]
    private Task CompareTile() => TilePath == null ? Task.CompletedTask : OpenCompareForPathAsync(TilePath);

    public async Task OpenCompareForPathAsync(string path)
    {
        var ws = _session.Workspace;
        if (ws == null) return;
        var edited = ws.ReadEdited(path);
        if (edited == null)
        {
            Dialogs.Info("Nothing to compare", "This tile has not been changed.");
            return;
        }
        await OpenCompareAsync(_session.Data?.Read(path), edited, path, CompareViewModel.OriginalLabel, CompareViewModel.EditLabel);
    }

    public async Task<MapCompareViewModel?> MakeCompareAsync(byte[]? before, byte[] after, string path, string leftLabel, string rightLabel)
    {
        var map = _session.MapCatalog?.ByDirectory(path.Split('\\').ElementAtOrDefault(2) ?? "");
        var xy = MapValidator.TileXY(path);
        if (map == null || xy == null || MapValidator.KindOf(path) != "adt") return null;
        return await _main.RunBusyAsync("Building the two 3D views...", () => Task.Run(() =>
        {
            var b = before != null ? TerrainTile.Load(before, map.Directory, xy.Value.X, xy.Value.Y) : null;
            var a = TerrainTile.Load(after, map.Directory, xy.Value.X, xy.Value.Y);
            var diff = before != null ? MapDiff.DiffAdt(before, after, leftLabel, rightLabel) : null;
            var leftAsset = BuildAsset(new[] { b ?? a });
            var rightAsset = BuildAsset(new[] { a });
            var img = b != null ? TerrainTile.HeightDifference(b, a, 512, out var maxD, out var chunks) : a.Relief(512);
            return new MapCompareViewModel($"{map.Name}, tile {xy.Value.X},{xy.Value.Y}", leftLabel, rightLabel, leftAsset, rightAsset, img, diff,
                b == null ? "The game has no such tile: a new tile." : null);
        }));
    }

    private async Task OpenCompareAsync(byte[]? before, byte[] after, string path, string leftLabel, string rightLabel)
    {
        if (MapValidator.KindOf(path) != "adt")
        {
            var (d, _, note) = MapOperations.Compare(before, after, path, leftLabel, rightLabel);
            Dialogs.Info($"Compare {Path.GetFileName(path)}", note, d == null ? null : string.Join("\n", d.Lines.Select(l => $"{l.Section}: {l.Message}")));
            return;
        }
        try
        {
            var vm = await MakeCompareAsync(before, after, path, leftLabel, rightLabel);
            if (vm == null) return;
            new MapCompareWindow(vm) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            Dialogs.Error("Could not open the comparison", e.Message);
        }
    }

    // ------------------------------------------------------------------ revert / test / release / server

    [RelayCommand]
    private async Task RevertTile()
    {
        var ws = _session.Workspace;
        var map = SelectedMap;
        if (ws == null || map == null || TilePath == null || !ws.IsModified(TilePath)) return;
        if (!Dialogs.Confirm("Revert the tile", $"Undo your change to {Path.GetFileName(TilePath)}? The edited copy is deleted from your workspace and the game's tile is used again (also removed from the game folder if it was installed as a loose file).",
                "Revert", "Keep my change", DialogKind.Warning)) return;
        await RevertPathsAsync(map, new[] { TilePath });
    }

    /// <summary>Revert map files and forget their copies in the Noggit project (only copies identical to them).</summary>
    public async Task RevertPathsAsync(MapInfo map, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var done = await _main.RunBusyAsync("Reverting...", () => Task.Run(() => list.SelectMany(p => ChangeOperations.Revert(_session, p, null)).ToList()));
        _main.Toast(done.LastOrDefault() ?? "Reverted.");
        RefreshChangedTiles();
        await LoadTileAsync();
    }

    public List<string> MapPathsOfSelectedMap()
    {
        var map = SelectedMap;
        var ws = _session.Workspace;
        return map == null || ws == null ? new() : ChangeOperations.MapFiles(ws, map);
    }

    public const string ServerNote = ChangeOperations.ServerMapsNote;

    [RelayCommand]
    private async Task TestMapInGame()
    {
        var ws = _session.Workspace;
        var paths = MapPathsOfSelectedMap();
        if (ws == null) return;
        if (paths.Count == 0)
        {
            Dialogs.Info("Test in game", "This map has no changes in your workspace yet. Edit it in Noggit, then click \"Import map changes...\".");
            return;
        }
        int pick = Dialogs.Show("Test in game (install map files as loose files)",
            $"This copies your {paths.Count} changed map file{(paths.Count == 1 ? "" : "s")} of {SelectedMap!.Name} into the game folder:\n{ws.Paths.DataDir}\\World\\Maps\\{SelectedMap.Directory}\\\n\n" +
            "The game reads loose files only with its loose-file mode on (the project's own client from update wave185 on; a normal 1.12.1 client ignores them: use \"Build release patch\" there). Restart the game, or teleport away and back, to see the change.\n\n" + ServerNote,
            DialogKind.Info, new[] { "Install into the game folder", "Cancel" }, string.Join("\n", paths), "How the loose-file mode works (LOOSE_FILES_PATCH.md)", _main.LooseFilesDoc);
        if (pick != 0) return;
        try
        {
            List<DeliveryAction> actions;
            try { actions = await _main.RunBusyAsync("Installing...", () => Task.Run(() => ws.InstallLoose(paths))); }
            catch (WorkspaceException e) when (ChangeOperations.IsForeignFileRefusal(e))
            {
                if (!Dialogs.Confirm("A file is already there", e.Message + "\n\nReplace it? The existing file is backed up first and restored when you remove your change from the game.", "Back it up and replace it", "Cancel", DialogKind.Warning)) return;
                actions = await _main.RunBusyAsync("Installing...", () => Task.Run(() => ws.InstallLoose(paths, force: true)));
            }
            await _browser.AfterGameFilesChangedAsync("Installed", $"{actions.Count} map file{(actions.Count == 1 ? " was" : "s were")} copied into the game folder. Restart the game to see the change.\n\nRemember: " + ServerNote,
                string.Join("\n", actions.Select(a => $"{a.Action}: {a.Dest}")), actions.Select(a => a.Path).ToList());
        }
        catch (Exception e) when (e is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Dialogs.Error("Could not install into the game folder", e.Message);
        }
    }

    [RelayCommand]
    private void BuildPatch() => _browser.BuildPatchCommand.Execute(null);

    [RelayCommand]
    private void UpdateServer()
    {
        var cat = _session.MapCatalog;
        if (_session.Workspace == null || cat == null || _session.Client == null) return;
        var vm = new ServerUpdateViewModel(_session, _main, cat);
        var win = new ServerUpdateWindow(vm) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    [RelayCommand]
    private void OpenNoggitFolder()
    {
        var map = SelectedMap;
        var noggit = _session.Noggit;
        if (map == null || noggit == null) return;
        var dir = noggit.ProjectDir(map);
        if (!Directory.Exists(dir))
        {
            Dialogs.Info("No Noggit project yet", $"Click \"Edit in Noggit...\" first: it creates the project for {map.Name} in\n{dir}");
            return;
        }
        Dialogs.OpenPath(dir);
    }
}
