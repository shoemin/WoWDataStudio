using System.Windows;
using System.Windows.Controls;

namespace WoWDataStudio.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ViewModels.BrowserViewModel old)
            {
                old.TextureFocusRequested -= OnTextureFocus;
                old.PropertyChanged -= OnVmChanged;
            }
            if (e.NewValue is ViewModels.BrowserViewModel vm)
            {
                vm.TextureFocusRequested += OnTextureFocus;
                vm.PropertyChanged += OnVmChanged;
            }
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.BrowserViewModel.IsBuildingEntry) && sender is ViewModels.BrowserViewModel { IsBuildingEntry: true } && BuildingView == null)
            CreateBuildingView();
    }

    /// <summary>The building preview's 3D view of another version, made the first time a building is shown (null before):
    /// every 3D view has its own GL context, which a session that never shows a building does not need.</summary>
    public Controls.ModelViewport? BuildingView { get; private set; }

    private void CreateBuildingView()
    {
        var v = new Controls.ModelViewport
        {
            ToolTip = "The building on its own: every group with the first texture of each batch. Left-drag turns, right-drag moves, wheel zooms, H: all the controls.",
        };
        v.SetBinding(Controls.ModelViewport.TerrainProperty, new System.Windows.Data.Binding(nameof(ViewModels.BrowserViewModel.BuildingScene)));
        v.SetBinding(Controls.ModelViewport.CameraProperty, new System.Windows.Data.Binding(nameof(ViewModels.BrowserViewModel.BuildingCamera)));
        BuildingHost.Child = v;
        BuildingView = v;
    }

    private void OnTextureFocus()
    {
        Dispatcher.BeginInvoke(() => TextureViewerBox.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Scroll the details panel so the 3D viewer is in sight (screenshots).</summary>
    public void ScrollToModel()
    {
        ModelViewer.BringIntoView(new Rect(0, -40, Math.Max(1, ModelViewer.ActualWidth), Math.Min(ModelViewer.ActualHeight + 40, 980)));
    }

    public Controls.ModelViewport ModelViewport => ModelViewer.Viewport;

    private void Fit_Click(object sender, RoutedEventArgs e) => TexViewer.FitToView();

    private void Actual_Click(object sender, RoutedEventArgs e) => TexViewer.ActualSize();

    private void Exit_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
