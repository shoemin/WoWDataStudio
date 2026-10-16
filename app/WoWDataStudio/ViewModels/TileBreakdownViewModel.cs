using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Blp;
using WoWData.Core.Dbc;
using WoWData.Core.Maps;
using WoWDataStudio.Helpers;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>A row of the breakdown tree: a group heading (Terrain, Ground textures...) or one piece of the tile.</summary>
public sealed partial class BreakdownNode : ObservableObject
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public BreakdownItem? Item { get; init; }
    public BreakdownGroup? Group { get; init; }
    public ObservableCollection<BreakdownNode> Children { get; } = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public bool IsGroup => Group != null;
    public string Glyph => Group?.Key switch
    {
        "terrain" => "", "textures" => "", "doodads" => "", "buildings" => "", "water" => "", "other" => "",
        _ => Item?.Kind switch
        {
            BreakdownKind.Terrain or BreakdownKind.Chunk => "", BreakdownKind.Texture => "", BreakdownKind.ModelGroup or BreakdownKind.Doodad => "",
            BreakdownKind.Building or BreakdownKind.BuildingPart => "", BreakdownKind.Water => "", BreakdownKind.SoundEmitter => "",
            BreakdownKind.Shadows => "", BreakdownKind.Area => "", _ => "",
        },
    };
    public override string ToString() => Title;
}

/// <summary>
/// "Break down this tile" (docs\manual\maps-and-terrain.md#break-down-a-tile): every piece of one map tile in a grouped,
/// searchable list (<see cref="TileBreakdown"/>). Selecting a piece views it: "In the tile" highlights it in the tile's 3D
/// view and dims the rest; "On its own" shows a model in the model renderer, a building alone, a ground texture with its
/// coverage map, or the terrain as a height heat map. Each piece has "Show in Browse" (the model or texture entry with its
/// "Used in game") and "Open in Blender" (a model or building, or the tile's terrain with the neighbour picker).
/// </summary>
public sealed partial class TileBreakdownViewModel : ObservableObject
{
    private readonly MapsViewModel _maps;
    private readonly BrowserViewModel _browser;
    private int _ownToken;
    private static Dictionary<int, string>? _soundNames;

    public MapInfo Map { get; }
    public int TileX { get; }
    public int TileY { get; }
    public TileBreakdown? Breakdown { get; private set; }

    public TerrainScene TileScene { get; } = new();
    public OrbitCamera TileCamera { get; } = new();
    public TerrainScene ObjectScene { get; } = new();
    public OrbitCamera ObjectCamera { get; } = new();
    public ModelScene ModelScene { get; } = new();
    public OrbitCamera ModelCamera { get; } = new();

    public ObservableCollection<BreakdownNode> Tree { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private BreakdownNode? _selected;
    [ObservableProperty] private bool _onItsOwn;
    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private string _status = "Reading the tile...";
    [ObservableProperty] private string _viewNote = "";
    // what the viewer shows
    [ObservableProperty] private TerrainScene? _activeTerrain;
    [ObservableProperty] private OrbitCamera? _activeCamera;
    [ObservableProperty] private bool _showViewport = true;
    [ObservableProperty] private BitmapSource? _imageA;
    [ObservableProperty] private string _imageALabel = "";
    [ObservableProperty] private BitmapSource? _imageB;
    [ObservableProperty] private string _imageBLabel = "";
    [ObservableProperty] private Rendering.CameraNavigation _navigation;
    [ObservableProperty] private bool _showHelp;

    public string Title => $"Break down: {Map.Name}, tile {TileX},{TileY} — {Map.Directory}_{TileX}_{TileY}.adt";
    public string SelectedTitle => Selected?.Item == null ? Selected?.Title ?? "Pick a piece of the tile on the left" : Selected.Item.Title;
    public string SelectedDetail => Selected?.Item?.Detail ?? Selected?.Group?.Summary ?? "Every piece of the tile is listed by kind. Select one to see it highlighted in the tile, or on its own.";
    public bool HasImages => !ShowViewport;
    public bool HasImageB => ImageB != null;
    public bool CanShowInBrowse => BrowseKey != null && _browser.FindEntryByKey(BrowseKey) != null;
    public string BrowseHint => Selected?.Item == null ? "Select a model, building or ground texture first." :
        BrowseKey == null ? "Only models, buildings and ground textures have an entry in the Browse list." :
        CanShowInBrowse ? "Opens this model or texture in the Browse tab, with its details and \"Used in game\"." : "This file has no entry in the Browse list.";
    public bool CanOpenInBlender => Selected?.Item is { } i && i.Kind != BreakdownKind.SoundEmitter;
    public string BlenderHint => Selected?.Item?.Kind switch
    {
        null => "Select a piece of the tile first.",
        BreakdownKind.SoundEmitter => "A sound emitter has no shape to edit in Blender.",
        BreakdownKind.ModelGroup or BreakdownKind.Doodad => "Exports this model (with its textures and animations) and opens it in Blender, like the Browse tab's button.",
        BreakdownKind.Building or BreakdownKind.BuildingPart => "Opens the building's shape in Blender to look at (buildings cannot be imported back yet).",
        _ => "Opens this tile's terrain in Blender (choose how many neighbour tiles to take along). Only heights come back.",
    };
    public string OpenInBlenderText => Selected?.Item?.Kind switch
    {
        BreakdownKind.ModelGroup or BreakdownKind.Doodad => "Open model in Blender",
        BreakdownKind.Building or BreakdownKind.BuildingPart => "Open building in Blender",
        _ => "Open terrain in Blender...",
    };

    private string? BrowseKey => Selected?.Item?.BrowseKey ?? (Selected?.Item?.Kind == BreakdownKind.BuildingPart && Selected.Item.Path != null ? "file:" + Selected.Item.Path : null);

    public TileBreakdownViewModel(MapsViewModel maps, BrowserViewModel browser, MapInfo map, int x, int y)
    {
        _maps = maps;
        _browser = browser;
        Map = map;
        TileX = x;
        TileY = y;
        TileScene.Background = maps.Scene.Background;
        ObjectScene.Background = new Vector3(0.20f, 0.22f, 0.26f);
        ModelScene.Background = new Vector3(0.20f, 0.22f, 0.26f);
        ActiveTerrain = TileScene;
        ActiveCamera = TileCamera;
    }

    partial void OnSelectedChanged(BreakdownNode? value)
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(CanShowInBrowse));
        OnPropertyChanged(nameof(BrowseHint));
        OnPropertyChanged(nameof(CanOpenInBlender));
        OnPropertyChanged(nameof(BlenderHint));
        OnPropertyChanged(nameof(OpenInBlenderText));
        ShowInBrowseCommand.NotifyCanExecuteChanged();
        OpenInBlenderCommand.NotifyCanExecuteChanged();
        _ = ShowAsync();
    }

    partial void OnOnItsOwnChanged(bool value) => _ = ShowAsync();
    partial void OnSearchChanged(string value) => BuildTree();
    partial void OnShowViewportChanged(bool value) => OnPropertyChanged(nameof(HasImages));
    partial void OnImageBChanged(BitmapSource? value) => OnPropertyChanged(nameof(HasImageB));

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync()
    {
        var session = _maps.Session;
        var path = Map.TilePath(TileX, TileY);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bd = await Task.Run(() => MapOperations.Breakdown(session, Map, TileX, TileY));
            Breakdown = bd;
            BuildTree();
            Status = $"{bd.All().Count()} pieces listed in {sw.ElapsedMilliseconds} ms. Building the 3D view...";
            // the 3D view of the tile: the Maps tab's preview when it shows exactly this tile, else our own
            TerrainAsset? asset = _maps.PreviewKey is { } k && ReferenceEquals(k.Map, Map) && k.X == TileX && k.Y == TileY && !k.Neighbours ? _maps.Scene.Asset : null;
            asset ??= await Task.Run(() => _maps.BuildAsset(new[] { bd.Tile }));
            TileScene.Asset = asset;
            TerrainScene.FrameTile(TileCamera, asset);
            Status = $"{bd.All().Count()} pieces. 3D view: {asset.Summary}.";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            Status = "The tile could not be read: " + e.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    private string? SoundName(int id)
    {
        if (_soundNames == null)
        {
            var d = new Dictionary<int, string>();
            try
            {
                var bytes = _maps.Session.Data?.Read(@"DBFilesClient\SoundEntries.dbc");
                if (bytes != null)
                {
                    var dbc = new DbcFile(bytes, "SoundEntries.dbc");
                    for (int r = 0; r < dbc.Count; r++) d[dbc.Int(r, 0)] = dbc.String(r, 2);
                }
            }
            catch (DbcException) { }
            _soundNames = d;
        }
        return _soundNames.GetValueOrDefault(id);
    }

    private void BuildTree()
    {
        var bd = Breakdown;
        Tree.Clear();
        if (bd == null) return;
        var q = Search.Trim();
        foreach (var g in bd.Groups)
        {
            var gn = new BreakdownNode { Title = g.Title, Detail = g.Summary, Group = g, IsExpanded = q.Length > 0 || g.Key is "textures" or "buildings" };
            foreach (var i in g.Items)
            {
                bool self = i.Matches(q);
                var kids = q.Length == 0 || self ? i.Children : i.Children.Where(c => c.Matches(q)).ToList();
                if (!self && kids.Count == 0) continue;
                var n = new BreakdownNode { Title = i.Title, Detail = i.Detail, Item = i, IsExpanded = q.Length > 0 && !self };
                foreach (var c in kids) n.Children.Add(new BreakdownNode { Title = c.Title, Detail = c.Detail, Item = c });
                gn.Children.Add(n);
            }
            if (gn.Children.Count > 0 || q.Length == 0) Tree.Add(gn);
        }
    }

    /// <summary>The first node whose item matches (tests, screenshots).</summary>
    public BreakdownNode? Find(Func<BreakdownItem, bool> pred)
    {
        foreach (var g in Tree)
            foreach (var n in g.Children)
            {
                if (n.Item != null && pred(n.Item)) return n;
                foreach (var c in n.Children) if (c.Item != null && pred(c.Item)) return c;
            }
        return null;
    }

    // ------------------------------------------------------------------ viewing

    /// <summary>The 3D highlight of an item in the tile.</summary>
    public static TerrainHighlight? HighlightOf(BreakdownItem? item)
    {
        if (item == null) return null;
        var h = new TerrainHighlight { Texture = item.Kind == BreakdownKind.Texture ? item.LayerPath ?? item.Path : null };
        if (item.Kind != BreakdownKind.Texture && item.Kind != BreakdownKind.Terrain) h.Chunks.UnionWith(item.Chunks);
        h.Doodads.UnionWith(item.DoodadUids);
        h.Wmos.UnionWith(item.WmoUids);
        if (item.Kind is BreakdownKind.ModelGroup or BreakdownKind.Doodad or BreakdownKind.Building) h.Chunks.Clear();
        return h.IsEmpty ? null : h;
    }

    private async Task ShowAsync()
    {
        int token = ++_ownToken;
        var item = Selected?.Item;
        var tile = Breakdown?.Tile;
        TileScene.Highlight = HighlightOf(item);
        if (!OnItsOwn || item == null || tile == null)
        {
            ShowViewport = true;
            ActiveTerrain = TileScene;
            ActiveCamera = TileCamera;
            ViewNote = item == null ? "" : item.OnNeighbour ? "This placement stands on a neighbouring tile (the file lists it because it reaches into this one)." :
                item.Kind == BreakdownKind.Texture ? "Lit where the texture is painted; the rest of the tile is dimmed." : "Highlighted in yellow; the rest of the tile is dimmed.";
            FocusOn(item);
            return;
        }
        var session = _maps.Session;
        try
        {
            switch (item.Kind)
            {
                case BreakdownKind.ModelGroup or BreakdownKind.Doodad when item.Path != null:
                {
                    ViewNote = "Loading the model...";
                    var res = session.Models!;
                    var asset = await Task.Run(() =>
                    {
                        if (_browser.ModelCache.TryGet(item.Path, out var a)) return a;
                        var data = session.ReadCurrent(item.Path) ?? throw new IOException($"{item.Path} is not in the game's files.");
                        a = ModelAsset.Build(item.Path, data, res.LoadBlp);
                        _browser.ModelCache.Add(item.Path, a);
                        return a;
                    });
                    if (token != _ownToken) return;
                    ModelScene.Asset = asset;
                    ModelScene.Look = res.DefaultFor(item.Path);
                    ModelCamera.Frame(asset.BoundsMin, asset.BoundsMax);
                    ShowViewport = true;
                    ActiveTerrain = null;
                    ActiveCamera = ModelCamera;
                    ViewNote = $"{Path.GetFileName(item.Path)} on its own, in the model viewer's renderer (animated).";
                    break;
                }
                case BreakdownKind.Building or BreakdownKind.BuildingPart when item.Path != null:
                {
                    ViewNote = "Loading the building...";
                    var res = session.Models!;
                    var asset = await Task.Run(() => TerrainAsset.BuildBuilding(item.Path, p => session.ReadCurrent(p), res.LoadBlp));
                    if (token != _ownToken) return;
                    ObjectScene.Asset = asset;
                    ObjectCamera.HomeYaw = 30;
                    ObjectCamera.HomePitch = 25;
                    ObjectCamera.Frame(asset.BoundsMin, asset.BoundsMax);
                    ShowViewport = true;
                    ActiveTerrain = ObjectScene;
                    ActiveCamera = ObjectCamera;
                    ViewNote = $"{Path.GetFileName(item.Path)} on its own: every group with its first texture (no furniture, lights or portals).";
                    break;
                }
                case BreakdownKind.Texture when item.Path != null:
                {
                    var res = session.Models!;
                    var layer = item.LayerPath ?? item.Path;
                    // the parent entry of a ground texture with an "_s" file shows the file the game draws (0.6.1)
                    bool spec = session.SpecularGround;
                    var file = item.IsSpecularFile || item.Path != layer ? item.Path
                        : item.Children.Count > 0 && item.SpecularPath != null && spec ? item.SpecularPath : item.Path;
                    var (texture, cover) = await Task.Run(() => (res.LoadBlp(file), TileBreakdown.CoverageImage(tile, layer, 512)));
                    if (token != _ownToken) return;
                    ImageA = texture == null ? null : ImageSources.ToBitmap(texture, AlphaView.IgnoreAlpha);
                    ImageALabel = texture == null ? "The texture file was not found." : $"{Path.GetFileName(file)}, {texture.Width} x {texture.Height}" +
                        (item.SpecularPath == null ? "" : file == item.SpecularPath ? " (the _s file: drawn with terrain specular on; colours shown)" : " (the base file: drawn with terrain specular off)");
                    if (item.IsSpecularFile && texture != null)
                    {
                        ImageB = ImageSources.ToBitmap(texture, AlphaView.AlphaOnly);
                        ImageBLabel = "Its alpha channel: the shine (specular) strength, white = shiny. Not transparency: keep it when you edit the colours.";
                    }
                    else
                    {
                        ImageB = ImageSources.ToBitmap(cover, AlphaView.IgnoreAlpha);
                        ImageBLabel = $"Coverage map: where it is painted on the tile (north up, bright = fully painted), {item.Coverage * 100:0.#}% of the ground";
                    }
                    ShowViewport = false;
                    ViewNote = item.SpecularPath == null ? "The texture as the game stores it, and where it is painted on this tile."
                        : "Two files: " + GroundTextures.WhichIsDrawn(new GroundTexturePair(layer, item.SpecularPath, true), session.GameSpecular);
                    break;
                }
                default:
                {
                    var chunks = item.Kind == BreakdownKind.Terrain ? new List<int>() : item.Chunks.ToList();
                    var img = await Task.Run(() => TileBreakdown.HeightMap(tile, chunks, 512));
                    if (token != _ownToken) return;
                    var (lo, hi) = tile.HeightRange;
                    ImageA = ImageSources.ToBitmap(img, AlphaView.IgnoreAlpha);
                    ImageALabel = $"Height heat map of the tile (north up): blue {lo:0} yards, red {hi:0} yards; holes dark" + (chunks.Count > 0 ? "; the selection is outlined, the rest dimmed" : "");
                    ImageB = null;
                    ImageBLabel = "";
                    ShowViewport = false;
                    ViewNote = item.Kind == BreakdownKind.SoundEmitter ? "Sound emitters have no shape: the heat map shows the chunk it is in." : "";
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or WoWData.Core.M2.M2Exception or BlpException or IndexOutOfRangeException or ArgumentException)
        {
            if (token == _ownToken) ViewNote = "Could not show it on its own: " + e.Message;
        }
    }

    /// <summary>Point the tile camera at an item (a placement, chunk or the chunks of an entry); keep the direction.</summary>
    private void FocusOn(BreakdownItem? item)
    {
        var asset = TileScene.Asset;
        if (asset == null || item == null) return;
        var origin = asset.Origin;
        Vector3? target = null;
        float size = 60f;
        if (item.WorldPosition is { } w && !item.OnNeighbour) target = w - origin;
        else if (item.Kind == BreakdownKind.ModelGroup && item.Children.Any(c => c.WorldPosition != null && !c.OnNeighbour))
        {
            // a model's placements: frame them all
            var pts = item.Children.Where(c => c.WorldPosition != null && !c.OnNeighbour).Select(c => c.WorldPosition!.Value - origin).ToList();
            var lo = pts.Aggregate(Vector3.Min);
            var hi = pts.Aggregate(Vector3.Max);
            target = (lo + hi) * 0.5f;
            size = Math.Max(60f, (hi - lo).Length() * 0.6f);
        }
        else if (item.Chunks.Count > 0 && item.Kind is not (BreakdownKind.Texture or BreakdownKind.Terrain or BreakdownKind.Area or BreakdownKind.Shadows))
        {
            var tile = Breakdown!.Tile;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var ci in item.Chunks)
                if (tile.Chunks[ci] is { } c)
                {
                    var p = new Vector3(c.Position.X, c.Position.Y, c.MinHeight) - origin;
                    lo = Vector3.Min(lo, p - new Vector3(TerrainMath.ChunkSize, TerrainMath.ChunkSize, 0));
                    hi = Vector3.Max(hi, p + new Vector3(0, 0, c.MaxHeight - c.MinHeight));
                }
            if (lo.X <= hi.X)
            {
                target = (lo + hi) * 0.5f;
                size = Math.Max(40f, (hi - lo).Length());
            }
        }
        if (target == null)
        {
            if (item.Kind is BreakdownKind.Terrain or BreakdownKind.Texture or BreakdownKind.Area or BreakdownKind.Shadows) TileCamera.ResetView();
            return;
        }
        TileCamera.Set(TileCamera.Yaw, Math.Max(55f, TileCamera.Pitch), Math.Max(150f, size * 2.5f), target.Value);
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand(CanExecute = nameof(CanShowInBrowse))]
    private async Task ShowInBrowse()
    {
        var key = BrowseKey;
        if (key == null) return;
        _browser.UsedInGameExpanded = true;
        if (await _browser.NavigateToKeyAsync(key, false)) System.Windows.Application.Current?.MainWindow?.Activate();
        else Dialogs.Info("Not in the Browse list", "This file has no entry in the Browse list.");
    }

    [RelayCommand(CanExecute = nameof(CanOpenInBlender))]
    private async Task OpenInBlender()
    {
        var item = Selected?.Item;
        if (item == null) return;
        switch (item.Kind)
        {
            case BreakdownKind.ModelGroup or BreakdownKind.Doodad when item.Path != null:
                await _browser.OpenModelFileInBlenderAsync(item.Path);
                break;
            case BreakdownKind.Building or BreakdownKind.BuildingPart when item.Path != null:
                await _maps.OpenBuildingInBlenderAsync(item.Path);
                break;
            case BreakdownKind.SoundEmitter:
                break;
            default:
                await _maps.OpenTerrainInBlenderAsync(Map, TileX, TileY, item.Kind == BreakdownKind.Chunk && item.Chunk >= 0 ? EdgeNeighbours(item.Chunk) : 1);
                break;
        }
    }

    /// <summary>A chunk on the tile's border: suggest 3 x 3 (the neighbour that shares its edge comes along).</summary>
    private static int EdgeNeighbours(int chunk) => chunk % 16 is 0 or 15 || chunk / 16 is 0 or 15 ? 3 : 1;

    [RelayCommand]
    private void Focus()
    {
        if (OnItsOwn) (ActiveCamera ?? TileCamera).ResetView();
        else FocusOn(Selected?.Item);
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var g in Tree) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in Tree)
        {
            g.IsExpanded = false;
            foreach (var c in g.Children) c.IsExpanded = false;
        }
    }

    public void Detach()
    {
        _ownToken++;
    }
}
