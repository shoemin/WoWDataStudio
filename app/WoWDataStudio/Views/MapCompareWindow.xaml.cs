using System.Windows;
using WoWData.Core.Imaging;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>Before / after of a terrain tile: two synchronized 3D views, the height difference and the changes.</summary>
public partial class MapCompareWindow : Window
{
    public MapCompareViewModel Vm { get; }

    /// <summary>Full screen (F11) and the 3D keys for the whole window (0.5.1).</summary>
    public Services.PopoutChrome Chrome { get; }

    public MapCompareWindow(MapCompareViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Chrome = new Services.PopoutChrome(this, () => LeftView);
        Chrome.FullScreenChanged += () => FullScreenButton.Content = Chrome.IsFullScreen ? "Leave full screen (Esc)" : "Full screen (F11)";
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => Chrome.ToggleFullScreen();

    /// <summary>Offscreen renders of both sides (screenshots, tests).</summary>
    public async Task<(RgbaImage Left, RgbaImage Right)> CaptureBothAsync(int width, int height) =>
        (await LeftView.CaptureAsync(width, height), await RightView.CaptureAsync(width, height));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
