using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>
/// The 3D terrain view of a map tile in a window of its own (0.5.1): fly camera by default, full screen with F11, the
/// display switches in a side panel, following the tile selected in the Maps tab or pinned. Remembers its size,
/// position, monitor, side panel and camera mode ("terrain" in settings.json).
/// </summary>
public partial class TerrainPopoutWindow : Window
{
    public TerrainPopoutViewModel Vm { get; }
    public PopoutChrome Chrome { get; }
    private double _panelWidth = 300;

    public TerrainPopoutWindow(TerrainPopoutViewModel vm, PopoutSettings? settings = null, Action? save = null)
    {
        InitializeComponent();
        Vm = vm;
        if (settings != null)
        {
            vm.ShowSidePanel = settings.SidePanel;
            vm.Navigation = settings.Navigation == nameof(CameraNavigation.Orbit) ? CameraNavigation.Orbit : CameraNavigation.Fly;
            Closing += (_, _) =>
            {
                settings.SidePanel = vm.ShowSidePanel;
                settings.Navigation = vm.Navigation.ToString();
            };
        }
        Chrome = new PopoutChrome(this, () => Viewport, settings, save);
        Chrome.FullScreenChanged += () => vm.IsFullScreen = Chrome.IsFullScreen;
        Chrome.ToggleControlsRequested += () => vm.ShowControls = !vm.ShowControls;
        vm.FullScreenCommand = new RelayCommand(Chrome.ToggleFullScreen);
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PopoutViewModelBase.ShowPanel)) UpdatePanel();
        };
        UpdatePanel();
        Loaded += (_, _) => Viewport.Focus();
        Closed += (_, _) => vm.Detach();
    }

    private void UpdatePanel()
    {
        if (Vm.ShowPanel) PanelColumn.Width = new GridLength(_panelWidth);
        else
        {
            if (PanelColumn.ActualWidth > 60) _panelWidth = PanelColumn.ActualWidth;
            PanelColumn.Width = new GridLength(0);
        }
    }
}
