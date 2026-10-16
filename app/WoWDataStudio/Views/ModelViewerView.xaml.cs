using System.Windows.Controls;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

public partial class ModelViewerView : UserControl
{
    public ModelViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ModelViewerViewModel old && old.Capture == Capture) old.Capture = null;
            if (e.NewValue is ModelViewerViewModel vm) vm.Capture = Capture;
        };
        Viewport.Rendered += (_, _) => (DataContext as ModelViewerViewModel)?.OnFrame();
    }

    private Task<WoWData.Core.Imaging.RgbaImage> Capture() => Viewport.CaptureAsync();
}
