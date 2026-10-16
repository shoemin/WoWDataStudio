using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WoWData.Core.Workspace;
using WoWDataStudio.Rendering;
using WoWDataStudio.Services;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>
/// The 3D model view in a window of its own (0.5.1): resizable, maximizable, movable to another monitor, full screen with
/// F11; the same controls as the docked viewer in a side panel; follows the selection of the main window or is pinned.
/// Remembers its size, position, monitor, side panel and camera mode ("model" in settings.json).
/// </summary>
public partial class ModelPopoutWindow : Window
{
    public ModelPopoutViewModel Vm { get; }
    public PopoutChrome Chrome { get; }
    private double _panelWidth = 400;

    public ModelPopoutWindow(ModelPopoutViewModel vm, PopoutSettings? settings = null, Action? save = null)
    {
        InitializeComponent();
        Vm = vm;
        if (settings != null)
        {
            vm.ShowSidePanel = settings.SidePanel;
            vm.Navigation = settings.Navigation == nameof(CameraNavigation.Fly) ? CameraNavigation.Fly : CameraNavigation.Orbit;
            // remember the layout before the placement is saved (PopoutChrome saves on Closing too)
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
        vm.Viewer.Capture = () => Viewport.CaptureAsync();
        Viewport.Rendered += (_, _) => vm.Viewer.OnFrame();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PopoutViewModelBase.ShowPanel)) UpdatePanel();
        };
        UpdatePanel();
        Loaded += (_, _) => Viewport.Focus();
        Closed += (_, _) =>
        {
            vm.Detach();
            vm.Viewer.Capture = null;
        };
    }

    private void UpdatePanel()
    {
        if (Vm.ShowPanel)
        {
            PanelColumn.Width = new GridLength(_panelWidth);
        }
        else
        {
            if (PanelColumn.ActualWidth > 60) _panelWidth = PanelColumn.ActualWidth;
            PanelColumn.Width = new GridLength(0);
        }
    }
}
