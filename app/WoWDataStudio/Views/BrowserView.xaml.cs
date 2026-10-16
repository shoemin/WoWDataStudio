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
            if (e.OldValue is ViewModels.BrowserViewModel old) old.TextureFocusRequested -= OnTextureFocus;
            if (e.NewValue is ViewModels.BrowserViewModel vm) vm.TextureFocusRequested += OnTextureFocus;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
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
