using System.Windows;
using System.Windows.Controls;
using WoWDataStudio.Controls;
using WoWDataStudio.Rendering;

namespace WoWDataStudio.Views;

/// <summary>Orbit / Fly, Front / Side / Top / Reset view and the help overlay toggle for one <see cref="ModelViewport"/>.</summary>
public partial class CameraBar : UserControl
{
    public static readonly DependencyProperty ViewportProperty = DependencyProperty.Register(nameof(Viewport), typeof(ModelViewport), typeof(CameraBar));

    public ModelViewport? Viewport { get => (ModelViewport?)GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }

    /// <summary>A radio group of its own per bar (two bars in one window must not uncheck each other).</summary>
    public string GroupName { get; } = "camera_" + Guid.NewGuid().ToString("N");

    public CameraBar()
    {
        InitializeComponent();
    }

    private void Preset(CameraPreset p)
    {
        Viewport?.ApplyPreset(p);
        Viewport?.Focus();
    }

    private void Front_Click(object sender, RoutedEventArgs e) => Preset(CameraPreset.Front);
    private void Side_Click(object sender, RoutedEventArgs e) => Preset(CameraPreset.Side);
    private void Top_Click(object sender, RoutedEventArgs e) => Preset(CameraPreset.Top);
    private void Reset_Click(object sender, RoutedEventArgs e) => Preset(CameraPreset.Reset);
}
