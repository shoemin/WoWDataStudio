using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps;
using WoWDataStudio.Services;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// "Open terrain in Blender" (docs\manual\terrain-in-blender.md): which tiles go to Blender (presets 1 x 1, 3 x 3, 5 x 5
/// around the selected tile, or any tiles clicked on the minimap), the doodads and buildings as references, the folder.
/// </summary>
public sealed partial class TerrainExportViewModel : ObservableObject
{
    private readonly HashSet<(int X, int Y)> _chosen = new();

    public MapInfo Map { get; }
    public int CenterX { get; }
    public int CenterY { get; }
    public BitmapSource? Overview { get; }
    public int Cell { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public bool[] Tiles => Map.Tiles;
    public Point Center => new(CenterX, CenterY);
    public bool BlenderFound { get; }
    public string BlenderText => BlenderFound ? "Blender opens with the tiles when the export is done." : "Blender was not found: the package is written, and you can open it later in Blender (File > Import > WoW 1.12 terrain).";

    [ObservableProperty] private IReadOnlyCollection<Point> _chosenPoints = Array.Empty<Point>();
    [ObservableProperty] private bool _includeReferences;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string _summary = "";

    /// <summary>0.6.1: which ground texture files the Blender materials show (both are written into the package).</summary>
    public string GroundText { get; init; } = "";

    /// <summary>Set by the window: export and then open Blender (false: export only).</summary>
    public bool OpenAfterExport { get; set; } = true;

    public IReadOnlyCollection<(int X, int Y)> Chosen => _chosen;

    public TerrainExportViewModel(MapInfo map, int x, int y, int preset, string folder, BitmapSource? overview, int cell, int originX, int originY, bool blenderFound)
    {
        Map = map;
        CenterX = x;
        CenterY = y;
        Overview = overview;
        Cell = cell;
        OriginX = originX;
        OriginY = originY;
        BlenderFound = blenderFound;
        _folder = folder;
        Preset(preset);
    }

    [RelayCommand]
    private void Preset(object? size)
    {
        int n = size switch { int i => i, string s when int.TryParse(s, out var i) => i, _ => 1 };
        _chosen.Clear();
        foreach (var t in TerrainExporter.Square(Map, CenterX, CenterY, n)) _chosen.Add(t);
        Update();
    }

    /// <summary>A click on the minimap: add or remove that tile (the selected tile always stays).</summary>
    public void Toggle(int x, int y)
    {
        if (!Map.HasTile(x, y)) return;
        if (x == CenterX && y == CenterY) return;
        if (!_chosen.Remove((x, y))) _chosen.Add((x, y));
        Update();
    }

    private void Update()
    {
        _chosen.Add((CenterX, CenterY));
        ChosenPoints = _chosen.Select(t => new Point(t.X, t.Y)).ToList();
        int n = _chosen.Count;
        int minX = _chosen.Min(t => t.X), maxX = _chosen.Max(t => t.X), minY = _chosen.Min(t => t.Y), maxY = _chosen.Max(t => t.Y);
        Summary = $"{n} tile{(n == 1 ? "" : "s")} ({maxX - minX + 1} x {maxY - minY + 1} area), about {n * 1.6:0} MB. Each becomes its own Blender object \"Tile x_y\" with its own colour; " +
                  "the edges between them are kept apart so you can see them, and \"Sync shared edges\" in Blender makes them match.";
        OnPropertyChanged(nameof(Chosen));
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var f = Dialogs.PickFolder("Where to write the terrain package", Folder);
        if (f != null) Folder = f;
    }
}
