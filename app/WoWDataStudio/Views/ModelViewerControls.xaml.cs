using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The 3D viewer's controls (animation, display options, character creation, textures, geosets, statistics):
/// under the docked view, and in the side panel of the pop-out window.</summary>
public partial class ModelViewerControls : UserControl
{
    public ModelViewerControls()
    {
        InitializeComponent();
    }

    private void Timeline_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ModelViewerViewModel vm) vm.Playing = false;
    }
}
