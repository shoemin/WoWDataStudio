using System.Windows;
using WoWData.Core.Versions;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>
/// M1: another version opened read only in a window of its own, next to the 1.12 window (design 2.4, the user's
/// choice). One window per client; closing the main window closes them.
/// </summary>
public partial class OtherVersionWindow : Window
{
    private static readonly List<OtherVersionWindow> Windows = new();

    public OtherVersionWindowViewModel Vm { get; }

    public OtherVersionWindow(OtherVersionWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = Vm = vm;
        vm.ScrollRequested += () => { if (vm.SelectedRow != null) ResultsList.ScrollIntoView(vm.SelectedRow); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OtherVersionWindowViewModel.IsBuildingEntry) && vm.IsBuildingEntry && BuildingView == null) CreateBuildingView();
            if (e.PropertyName == nameof(OtherVersionWindowViewModel.SelectedTab) && vm.SelectedTab == OtherVersionWindowViewModel.MapsTab) _ = MapsScreen;
        };
        Closed += (_, _) =>
        {
            Windows.Remove(this);
            Vm.Dispose();
        };
    }

    /// <summary>The read-only windows open now.</summary>
    public static IReadOnlyList<OtherVersionWindow> OpenWindows => Windows.ToList();

    /// <summary>Opens <paramref name="copy"/> read only in its window (or brings its window forward).</summary>
    public static OtherVersionWindow Open(MainViewModel main, ClientCopy copy, IReadOnlyList<ClientCopy>? found, bool show = true)
    {
        var existing = Windows.FirstOrDefault(w => string.Equals(w.Vm.Copy.Folder, copy.Folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (show)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
            }
            return existing;
        }
        var vm = new OtherVersionWindowViewModel(main.Session.Paths, main.Session.Settings, copy, () => main.Session.Data, found);
        var win = new OtherVersionWindow(vm);
        Windows.Add(win);
        if (show) win.Show();
        _ = vm.OpenAsync();
        return win;
    }

    /// <summary>Closes every read-only window (when the main window closes).</summary>
    public static void CloseAll()
    {
        foreach (var w in Windows.ToList()) w.Close();
    }

    private MapsView? _mapsScreen;

    /// <summary>M4: the Maps tab's view, made the first time the tab is shown (or asked for): a window that never shows
    /// its maps does not need the GL context of its 3D view.</summary>
    public MapsView MapsScreen
    {
        get
        {
            if (_mapsScreen != null) return _mapsScreen;
            _mapsScreen = new MapsView { DataContext = Vm.Maps, Margin = new Thickness(12, 0, 12, 10) };
            MapsTabItem.Content = _mapsScreen;
            return _mapsScreen;
        }
    }

    /// <summary>M4: the building preview's 3D view, made the first time this window shows a building (null before):
    /// every 3D view has its own GL context, which a window that never shows a building does not need.</summary>
    public Controls.ModelViewport? BuildingView { get; private set; }

    private void CreateBuildingView()
    {
        var v = new Controls.ModelViewport
        {
            ToolTip = "The building on its own: every group with the first texture of each batch. Left-drag turns, right-drag moves, wheel zooms, H: all the controls.",
        };
        v.SetBinding(Controls.ModelViewport.TerrainProperty, new System.Windows.Data.Binding(nameof(OtherVersionWindowViewModel.BuildingScene)));
        v.SetBinding(Controls.ModelViewport.CameraProperty, new System.Windows.Data.Binding(nameof(OtherVersionWindowViewModel.BuildingCamera)));
        BuildingHost.Child = v;
        BuildingView = v;
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => TexViewer.FitToView();

    private void Actual_Click(object sender, RoutedEventArgs e) => TexViewer.ActualSize();
}
