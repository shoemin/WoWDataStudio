using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Maps;
using WoWData.Core.ModelIO;
using WoWData.Core.Workspace;
using WoWDataStudio.Services;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// 0.6.0: "Break down this tile" and terrain through Blender (docs\manual\maps-and-terrain.md#break-down-a-tile,
/// docs\manual\terrain-in-blender.md): the breakdown window, "Open terrain in Blender..." (the tile picker, the package,
/// Blender with the add-on) and "Import edited terrain..." (heights back, seam check and sync, the map import review).
/// </summary>
public sealed partial class MapsViewModel
{
    public List<TileBreakdownWindow> BreakdownWindows { get; } = new();

    public string TerrainExportDir => Path.Combine(_session.Settings.DefaultExportDir, "Terrain");

    // ------------------------------------------------------------------ breakdown

    [RelayCommand]
    private void BreakDownTile()
    {
        if (!RequireMap()) return;
        if (SelectedMap is not { } map || SelectedTile is not { } t)
        {
            Dialogs.Info("Select a tile first", "Click a tile on the minimap of the Maps tab, then break it down.");
            return;
        }
        OpenBreakdown(map, (int)t.X, (int)t.Y);
    }

    /// <summary>Open the breakdown window of a tile (from the Maps tab, the terrain pop-out or the Tools menu).</summary>
    public TileBreakdownWindow OpenBreakdown(MapInfo map, int x, int y, Action<TileBreakdownWindow>? beforeShow = null)
    {
        var vm = new TileBreakdownViewModel(this, Browser, map, x, y);
        var win = new TileBreakdownWindow(vm);
        win.Closed += (_, _) => BreakdownWindows.Remove(win);
        if (Application.Current?.MainWindow is { IsVisible: true } main && beforeShow == null) win.Owner = main;
        beforeShow?.Invoke(win);
        BreakdownWindows.Add(win);
        win.Show();
        _ = vm.LoadAsync();
        return win;
    }

    // ------------------------------------------------------------------ terrain to Blender

    [RelayCommand]
    private Task OpenTerrainInBlender()
    {
        if (!RequireMap()) return Task.CompletedTask;
        if (SelectedMap is not { } map || SelectedTile is not { } t)
        {
            Dialogs.Info("Select a tile first", "Click a tile on the minimap of the Maps tab, then open its terrain in Blender.");
            return Task.CompletedTask;
        }
        return OpenTerrainInBlenderAsync(map, (int)t.X, (int)t.Y, 1);
    }

    public TerrainExportViewModel MakeTerrainExportViewModel(MapInfo map, int x, int y, int preset) =>
        new(map, x, y, preset, TerrainExportDir, ReferenceEquals(SelectedMap, map) ? Overview : null, OverviewCell, OriginX, OriginY, BlenderTools.FindBlender() != null)
        {
            GroundText = MapOperations.GroundExportText(_session),
        };

    /// <summary>The tile picker, then the export and Blender.</summary>
    public async Task OpenTerrainInBlenderAsync(MapInfo map, int x, int y, int preset)
    {
        if (map.IsGlobalWmo) return;
        var vm = MakeTerrainExportViewModel(map, x, y, preset);
        var win = new TerrainExportWindow(vm) { Owner = Dialogs.Owner };
        if (win.ShowDialog() != true) return;
        try
        {
            var res = await ExportTerrainAsync(vm);
            if (vm.OpenAfterExport && vm.BlenderFound)
                Browser.OpenFileInBlender(res.ManifestPath, $"Blender is starting with {res.Manifest.Tiles.Count} tile(s). When you are done: WoW Terrain panel > Export terrain for WoW Data Studio, then \"Import edited terrain...\" here.");
            else
            {
                int pick = Dialogs.Show("Terrain exported for Blender",
                    $"The package is in\n{res.Folder}\n\n{res.Manifest.Tiles.Count} tile(s), {res.Manifest.Textures.Count} ground textures and wowdata_terrain.json. In Blender: File > Import > WoW 1.12 terrain, edit the heights, " +
                    "then Export terrain for WoW Data Studio (WoW Terrain panel). Then \"Import edited terrain...\" here." + (res.Notes.Count > 0 ? "\n\nNotes: " + string.Join("; ", res.Notes) : ""),
                    DialogKind.Success, new[] { "Open the folder", "OK" });
                if (pick == 0) Dialogs.OpenPath(res.Folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.Error("Could not export the terrain", e.Message);
        }
    }

    public Task<TerrainExportResult> ExportTerrainAsync(TerrainExportViewModel vm)
    {
        var req = MapOperations.TerrainExport(_session, vm.Map, vm.Chosen.ToList(), (vm.CenterX, vm.CenterY), vm.Folder, vm.IncludeReferences);
        return Main.RunBusyAsync($"Exporting {req.Tiles.Count} tile(s) for Blender...", () => Task.Run(() => TerrainExporter.Export(req)));
    }

    /// <summary>"Open in Blender" on a building of the breakdown: its shape as a glTF to look at (not importable yet).</summary>
    public async Task OpenBuildingInBlenderAsync(string path)
    {
        try
        {
            var folder = Path.Combine(Browser.ModelsExportDir, ModelExporter.SafeName(Path.GetFileNameWithoutExtension(path)) + "_building");
            var gltf = await Main.RunBusyAsync("Exporting the building...", () => Task.Run(() => TerrainExporter.ExportBuilding(path, p => _session.ReadCurrent(p), folder)));
            Browser.OpenFileInBlender(gltf, "Blender is starting with the building (to look at: buildings cannot be imported back yet).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Dialogs.Error("Could not export the building", e.Message);
        }
    }

    // ------------------------------------------------------------------ import edited terrain

    [RelayCommand]
    private async Task ImportEditedTerrain()
    {
        if (_session.Data == null || _session.Workspace == null) return;
        int pick = Dialogs.Show("Import edited terrain",
            "Pick the terrain package folder that \"Open terrain in Blender\" made (the app finds what Blender exported into its blender_export folder).\n\n" +
            "Only heights come back: the game's ground is a fixed grid of 145 vertices per chunk. Vertices you moved sideways are put back on the grid, added vertices are ignored and deleted ones keep their height; the review lists what was ignored. " +
            "The edges shared with the neighbouring tiles are checked, and you can sync them.",
            DialogKind.Question, new[] { "Pick the folder...", "Cancel" });
        if (pick != 0) return;
        var path = Dialogs.PickFolder("The terrain package folder (or its blender_export folder)", _session.Settings.LastImportDir ?? TerrainExportDir);
        if (path == null) return;
        _session.Settings.LastImportDir = path;
        Main.SaveSettings();
        await ImportTerrainAsync(path);
    }

    public MapImportAnalysis AnalyzeTerrain(string path, bool sync) => MapOperations.AnalyzeTerrain(_session, path, sync);

    /// <summary>Analyze, offer to sync mismatching edges, review, save as one change set.</summary>
    public async Task<bool> ImportTerrainAsync(string path, bool interactive = true)
    {
        MapImportAnalysis a;
        try
        {
            a = await Main.RunBusyAsync("Reading the terrain Blender exported...", () => Task.Run(() => AnalyzeTerrain(path, false)));
            if (a.Seams.Count > 0)
            {
                int sync = interactive ? Dialogs.Show("Edges do not match their neighbours", MapOperations.SeamText(a),
                    DialogKind.Warning, new[] { "Sync the edges (copy from the edited side)", "Cancel" }) : 0;
                if (sync != 0) return false;
                a = await Main.RunBusyAsync("Syncing the edges...", () => Task.Run(() => AnalyzeTerrain(path, true)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Dialogs.Error("Could not read the edited terrain", e.Message);
            return false;
        }
        if (a.Changes.Count == 0 || a.NothingChanged)
        {
            if (interactive) Dialogs.Info("Import edited terrain", string.Join("\n\n", a.General.DefaultIfEmpty("No changes found.")));
            return false;
        }
        if (!interactive)
        {
            MapImporter.Commit(a, _session.Workspace!);
            RefreshChangedTiles();
            return true;
        }
        await ReviewAndCommitAsync(a);
        return true;
    }
}
