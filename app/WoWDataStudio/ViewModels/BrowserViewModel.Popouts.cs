using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Index;
using WoWDataStudio.Views;

namespace WoWDataStudio.ViewModels;

/// <summary>
/// 0.5.1: the pop-out 3D windows ("Open in new window", Ctrl+Shift+O). Any number can be open: model windows
/// (<see cref="ModelPopoutWindow"/>) and terrain windows (<see cref="TerrainPopoutWindow"/>), each following the
/// selection or pinned. While a following model window is open, the docked viewer stops redrawing every frame (the
/// animation plays in the pop-out; the docked view still redraws when it is moved or changed). The docked terrain
/// preview needs no pause: it only redraws when something changes.
/// </summary>
public sealed partial class BrowserViewModel
{
    /// <summary>The built models, shared by the docked viewer and every pop-out window (read and decoded once).</summary>
    public ModelAssetCache ModelCache { get; } = new();
    public List<ModelPopoutWindow> ModelPopouts { get; } = new();
    public List<TerrainPopoutWindow> TerrainPopouts { get; } = new();

    /// <summary>A model entry's details were shown in the main window (following pop-out windows load it too).</summary>
    public event Action<IndexEntry, EntryDetails>? ModelEntryShown;

    private bool _closeHooked;

    private void InitPopouts()
    {
        ModelViewer.PopOutCommand = OpenModelPopoutCommand;
        ModelViewer.ShowPopoutCommand = ShowFollowingPopoutCommand;
        Maps.PopOutCommand = OpenTerrainPopoutCommand;
    }

    private void HookMainWindowClose()
    {
        if (_closeHooked || Application.Current?.MainWindow is not { } main) return;
        _closeHooked = true;
        main.Closing += (_, _) => CloseAllPopouts();
    }

    public void CloseAllPopouts()
    {
        foreach (var w in ModelPopouts.ToList()) w.Close();
        foreach (var w in TerrainPopouts.ToList()) w.Close();
    }

    /// <summary>Ctrl+Shift+O and Tools > Open the 3D view in a new window: the Browse tab's model or the Maps tab's tile.</summary>
    [RelayCommand]
    private async Task PopOut()
    {
        if (SelectedTab == MapsTab && Maps.SelectedTile != null) OpenTerrainPopoutWindow();
        else if (SelectedTab == 0 && IsModelEntry && ModelViewer.Loaded != null) await OpenModelPopoutAsync();
        else _main.Toast("Select a creature, item or other model (Browse tab) or a map tile (Maps tab) first: its 3D view opens in a new window.");
    }

    [RelayCommand]
    private Task OpenModelPopout() => OpenModelPopoutAsync();

    /// <summary>Open a model pop-out window on what the docked viewer shows (same look, animation and camera).</summary>
    public async Task<ModelPopoutWindow?> OpenModelPopoutAsync(Action<ModelPopoutWindow>? beforeShow = null)
    {
        if (ModelViewer.Loaded == null) return null;
        HookMainWindowClose();
        var settings = _session.Settings.Popout("model");
        var vm = new ModelPopoutViewModel(this, ModelCache);
        vm.Viewer.OpenTextureRequested += (path, role) =>
        {
            OpenTextureFromModel(path, role);
            Application.Current?.MainWindow?.Activate();
        };
        var win = new ModelPopoutWindow(vm, settings, _main.SaveSettings);
        if (settings.Placement == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
    private void OpenTerrainPopout() => OpenTerrainPopoutWindow();

    /// <summary>Open a terrain pop-out window on the tile selected in the Maps tab (with its display switches).</summary>
    public TerrainPopoutWindow? OpenTerrainPopoutWindow(Action<TerrainPopoutWindow>? beforeShow = null)
    {
        var map = Maps.SelectedMap;
        if (map == null || Maps.SelectedTile is not { } t) return null;
        HookMainWindowClose();
        var settings = _session.Settings.Popout("terrain");
        var vm = new TerrainPopoutViewModel(Maps);
        var win = new TerrainPopoutWindow(vm, settings, _main.SaveSettings);
        if (settings.Placement == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Closed += (_, _) => TerrainPopouts.Remove(win);
        beforeShow?.Invoke(win);
        TerrainPopouts.Add(win);
        win.Show();
        vm.ShowTile(map, (int)t.X, (int)t.Y);
        if (vm.Scene.Asset != null && ReferenceEquals(vm.Scene.Asset, Maps.Scene.Asset)) vm.Camera.CopyFrom(Maps.Camera);
        return win;
    }

    /// <summary>"Bring it to front" in the docked viewer: the (first) following model window.</summary>
    [RelayCommand]
    private void ShowFollowingPopout()
    {
        var w = ModelPopouts.FirstOrDefault(p => p.Vm.FollowSelection) ?? ModelPopouts.FirstOrDefault();
        if (w == null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private void UpdateDockedPause() => ModelViewer.PausedForPopout = ModelPopouts.Any(w => w.Vm.FollowSelection);

    /// <summary>After an import or a revert: the pop-out windows reload their textures and meshes too.</summary>
    private void RefreshPopouts()
    {
        foreach (var w in ModelPopouts)
        {
            w.Vm.Viewer.RefreshModified();
            if (w.Vm.Viewer.HasModel) _ = w.Vm.Viewer.ReloadLookAsync();
        }
    }
}
