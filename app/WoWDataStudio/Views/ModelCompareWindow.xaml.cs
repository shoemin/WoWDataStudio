using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>Host window of the model comparison (the demo entry "Compare two skins" and, in 3c, the review screen).</summary>
public partial class ModelCompareWindow : Window
{
    public ModelCompareViewModel Vm { get; }

    /// <summary>Full screen (F11) and the 3D keys for the whole window (0.5.1).</summary>
    public Services.PopoutChrome Chrome { get; }

    public ModelCompareWindow(ModelCompareViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Chrome = new Services.PopoutChrome(this, () => vm.IsToggle ? Compare.ToggleView : Compare.LeftView);
        Chrome.FullScreenChanged += () => FullScreenButton.Content = Chrome.IsFullScreen ? "Leave full screen (Esc)" : "Full screen (F11)";
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => Chrome.ToggleFullScreen();

    private void SideBySide_Click(object sender, RoutedEventArgs e) => Vm.Mode = ModelCompareMode.SideBySide;
    private void ToggleMode_Click(object sender, RoutedEventArgs e) => Vm.Mode = ModelCompareMode.Toggle;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
