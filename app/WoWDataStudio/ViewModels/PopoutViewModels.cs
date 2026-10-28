using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWData.Core.Maps;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>What both pop-out 3D windows have: follow / pin, the controls shown or hidden, the side panel, full screen,
/// the navigation mode and the help overlay.</summary>
public abstract partial class PopoutViewModelBase : ObservableObject
{
    /// <summary>On: the window shows what is selected in the main window. Off ("Pinned"): it keeps its model or tile.</summary>
    [ObservableProperty] private bool _followSelection = true;
    /// <summary>The tool bar and the side panel (Ctrl+H hides both for an unobstructed view).</summary>
    [ObservableProperty] private bool _showControls = true;
    /// <summary>The side panel with the controls (a narrower 3D view when open).</summary>
    [ObservableProperty] private bool _showSidePanel = true;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private CameraNavigation _navigation;
    [ObservableProperty] private bool _showHelp;

    public bool ShowToolbar => ShowControls;
    public bool ShowPanel => ShowControls && ShowSidePanel;
    public bool ControlsHidden => !ShowControls;
    public string FollowText => FollowSelection ? "Follows the selection in the main window" : "Pinned: keeps showing this one";
    public string FullScreenText => IsFullScreen ? "Leave full screen (Esc)" : "Full screen (F11)";
    public string FullScreenGlyph => IsFullScreen ? "" : "";
    public string PanelText => ShowSidePanel ? "Hide side panel" : "Show side panel";

    /// <summary>The window title: what is shown, e.g. "Mottled Boar — Boar.m2", and "(pinned)" when not following.</summary>
    public string WindowTitle => Describe() + (FollowSelection ? "" : " (pinned)");
    protected abstract string Describe();

    partial void OnFollowSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(FollowText));
        OnPropertyChanged(nameof(WindowTitle));
        FollowChanged?.Invoke();
        if (value) CatchUp();
    }

    /// <summary>Following again: show what the main window shows now.</summary>
    protected abstract void CatchUp();

    /// <summary>Raised when follow / pin changes (the docked view pauses only while a following window is open).</summary>
    public event Action? FollowChanged;

    partial void OnShowControlsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowToolbar));
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(ControlsHidden));
    }

    partial void OnShowSidePanelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPanel));
        OnPropertyChanged(nameof(PanelText));
    }

    partial void OnIsFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(FullScreenText));
        OnPropertyChanged(nameof(FullScreenGlyph));
    }

    [RelayCommand]
    private void ToggleControls() => ShowControls = !ShowControls;

    [RelayCommand]
    private void ToggleSidePanel() => ShowSidePanel = !ShowSidePanel;

    /// <summary>Set by the window: F11.</summary>
    public IRelayCommand? FullScreenCommand { get; set; }
}

/// <summary>M2: what a model pop-out window follows: the main window's Browse tab (<see cref="BrowserViewModel"/>), for your
/// 1.12 game or another version opened read only.</summary>
public interface IModelViewerHost
{
    WoWData.Core.Session.AppSession Session { get; }
    /// <summary>The docked 3D viewer (a pop-out window opens on what it shows).</summary>
    ModelViewerViewModel ModelViewer { get; }
    /// <summary>A model entry's details were shown (following pop-out windows load it too).</summary>
    event Action<IndexEntry, EntryDetails>? ModelEntryShown;
}

/// <summary>
/// The model pop-out window (docs\manual\model-viewer.md#open-in-a-new-window): its own <see cref="ModelViewerViewModel"/>
/// (all the controls of the docked viewer), following the selection of the main window or pinned to its model.
/// </summary>
public sealed partial class ModelPopoutViewModel : PopoutViewModelBase
{
    private readonly IModelViewerHost _browser;
    public ModelViewerViewModel Viewer { get; }

    public ModelPopoutViewModel(IModelViewerHost browser, ModelAssetCache cache)
    {
        _browser = browser;
        Viewer = new ModelViewerViewModel(browser.Session, cache);
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelViewerViewModel.EntryName) or nameof(ModelViewerViewModel.ModelPath) or nameof(ModelViewerViewModel.HasModel))
                OnPropertyChanged(nameof(WindowTitle));
        };
        browser.ModelEntryShown += OnModelEntryShown;
    }

    protected override string Describe()
    {
        var file = Viewer.HasModel ? Path.GetFileName(Viewer.ModelPath) : "";
        var name = Viewer.EntryName;
        if (name.Length > 0 && file.Length > 0 && !string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) return $"{name} — {file}";
        return name.Length > 0 ? name : file.Length > 0 ? file : "3D view";
    }

    private void OnModelEntryShown(IndexEntry entry, EntryDetails details)
    {
        if (FollowSelection) _ = Viewer.LoadAsync(entry, details);
    }

    protected override void CatchUp()
    {
        if (_browser.ModelViewer.Loaded is { } l && !ReferenceEquals(l.Entry, Viewer.Loaded?.Entry)) _ = Viewer.LoadAsync(l.Entry, l.Details);
    }

    /// <summary>The window closed: stop following.</summary>
    public void Detach() => _browser.ModelEntryShown -= OnModelEntryShown;
}

/// <summary>
/// The terrain pop-out window (docs\manual\maps-and-terrain.md#open-in-a-new-window): its own scene and camera of a map
/// tile (with the neighbours, doodads, buildings, water... switches), following the tile selected in the Maps tab or
/// pinned to its tile. A following window takes the Maps tab's preview when it is the same (no second build).
/// </summary>
public sealed partial class TerrainPopoutViewModel : PopoutViewModelBase
{
    private readonly MapsViewModel _maps;
    private int _token;
    private CancellationTokenSource? _cts;
    private (MapInfo Map, int X, int Y, bool Neighbours)? _pending;
    private (MapInfo Map, int X, int Y)? _framed;

    public TerrainScene Scene { get; } = new();
    public OrbitCamera Camera { get; } = new();

    [ObservableProperty] private MapInfo? _map;
    [ObservableProperty] private int _tileX = -1;
    [ObservableProperty] private int _tileY = -1;
    [ObservableProperty] private bool _includeNeighbours;
    [ObservableProperty] private bool _showDoodads = true;
    [ObservableProperty] private bool _showWmos = true;
    [ObservableProperty] private bool _showWater = true;
    [ObservableProperty] private bool _showTextures = true;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loading;
    /// <summary>How the shown asset was obtained: "shared with the Maps tab" or "built for this window" (tests, statistics).</summary>
    public string Source { get; private set; } = "";
    /// <summary>Builds this window made itself (a following window with the Maps tab's settings makes none).</summary>
    public int OwnBuilds { get; private set; }

    public TerrainPopoutViewModel(MapsViewModel maps)
    {
        _maps = maps;
        Navigation = CameraNavigation.Fly;
        Scene.Background = maps.Scene.Background;
        IncludeNeighbours = maps.IncludeNeighbours;
        ShowDoodads = maps.ShowDoodads;
        ShowWmos = maps.ShowWmos;
        ShowWater = maps.ShowWater;
        ShowTextures = maps.ShowTextures;
        Wireframe = maps.Wireframe;
        maps.TileSelected += OnTileSelected;
        maps.PreviewShown += OnPreviewShown;
        maps.PropertyChanged += OnMapsChanged;
    }

    protected override string Describe() =>
        Map == null ? "Terrain" : $"{Map.Name}, tile {TileX},{TileY} — {Map.Directory}_{TileX}_{TileY}.adt";

    partial void OnMapChanged(MapInfo? value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnTileXChanged(int value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnTileYChanged(int value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnShowDoodadsChanged(bool value) => Scene.ShowDoodads = value;
    partial void OnShowWmosChanged(bool value) => Scene.ShowWmos = value;
    partial void OnShowWaterChanged(bool value) => Scene.ShowWater = value;
    partial void OnShowTexturesChanged(bool value) => Scene.ShowTextures = value;
    partial void OnWireframeChanged(bool value) => Scene.Wireframe = value;
    partial void OnIncludeNeighboursChanged(bool value)
    {
        if (Map != null) ShowTile(Map, TileX, TileY);
    }

    private void OnTileSelected(MapInfo map, int x, int y)
    {
        if (FollowSelection) ShowTile(map, x, y);
    }

    private void OnPreviewShown(MapInfo map, int x, int y, bool neighbours, TerrainAsset asset)
    {
        if (_pending is { } p && ReferenceEquals(p.Map, map) && p.X == x && p.Y == y && p.Neighbours == neighbours) Adopt(asset, "shared with the Maps tab");
    }

    private void OnMapsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the Maps tab gave up on the tile this window waits for (another tile, an error): build it here
        if (e.PropertyName == nameof(MapsViewModel.PreviewLoading) && !_maps.PreviewLoading && _pending is { } p && _maps.PreviewKey != p)
        {
            _pending = null;
            _ = BuildAsync(p.Map, p.X, p.Y, p.Neighbours);
        }
    }

    protected override void CatchUp()
    {
        if (_maps.SelectedMap is { } m && _maps.SelectedTile is { } t && (!ReferenceEquals(m, Map) || (int)t.X != TileX || (int)t.Y != TileY))
            ShowTile(m, (int)t.X, (int)t.Y);
    }

    /// <summary>Show a tile: the Maps tab's preview when it is the same (or is being built), else a build of our own.</summary>
    public void ShowTile(MapInfo map, int x, int y)
    {
        Map = map;
        TileX = x;
        TileY = y;
        _token++;
        _cts?.Cancel();
        var key = (map, x, y, IncludeNeighbours);
        if (_maps.PreviewKey == key && _maps.Scene.Asset != null && !_maps.PreviewLoading)
        {
            _pending = null;
            Adopt(_maps.Scene.Asset, "shared with the Maps tab");
            return;
        }
        if (FollowSelection && ReferenceEquals(_maps.SelectedMap, map) && _maps.SelectedTile is { } t && (int)t.X == x && (int)t.Y == y && _maps.IncludeNeighbours == IncludeNeighbours)
        {
            // the Maps tab is building this very preview: wait for it
            _pending = key;
            Loading = true;
            Status = IncludeNeighbours ? "Loading the tile and its neighbours in 3D..." : "Loading the tile in 3D...";
            return;
        }
        _pending = null;
        _ = BuildAsync(map, x, y, IncludeNeighbours);
    }

    private void Adopt(TerrainAsset asset, string source)
    {
        _pending = null;
        var keep = _framed is { } f && ReferenceEquals(f.Map, Map) && f.X == TileX && f.Y == TileY && Scene.Asset != null;
        var (yaw, pitch, dist, target) = (Camera.Yaw, Camera.Pitch, Camera.Distance, Camera.Target);
        Scene.Asset = asset;
        TerrainScene.FrameTile(Camera, asset);
        if (keep) Camera.Set(yaw, pitch, dist, target);     // same tile (neighbours switched): stay where you are
        _framed = (Map!, TileX, TileY);
        Source = source;
        Status = asset.Summary;
        Loading = false;
    }

    private async Task BuildAsync(MapInfo map, int x, int y, bool neighbours)
    {
        if (_maps.Session.Looks == null) return;
        int token = ++_token;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Loading = true;
        Status = neighbours ? "Loading the tile and its neighbours in 3D..." : "Loading the tile in 3D...";
        try
        {
            var asset = await Task.Run(() =>
            {
                var tiles = _maps.PreviewTiles(map, x, y, neighbours);
                return tiles.Count == 0 ? null : _maps.BuildAsset(tiles, cts.Token);
            }, cts.Token);
            if (token != _token || asset == null) return;
            OwnBuilds++;
            Adopt(asset, "built for this window");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            if (token == _token) Status = "The 3D view could not be built: " + e.Message;
        }
        finally
        {
            if (token == _token) Loading = false;
        }
    }

    /// <summary>M4: the breakdown is your 1.12 game's (hidden in the window of another version opened read only).</summary>
    public bool CanBreakDown => _maps.CanEdit;

    /// <summary>"Break down this tile" for the tile this window shows (0.6.0).</summary>
    [RelayCommand]
    private void BreakDown()
    {
        if (Map != null && TileX >= 0 && CanBreakDown) _maps.OpenBreakdown(Map, TileX, TileY);
    }

    /// <summary>The window closed: stop following, cancel a build.</summary>
    public void Detach()
    {
        _maps.TileSelected -= OnTileSelected;
        _maps.PreviewShown -= OnPreviewShown;
        _maps.PropertyChanged -= OnMapsChanged;
        _cts?.Cancel();
        _token++;
    }
}
