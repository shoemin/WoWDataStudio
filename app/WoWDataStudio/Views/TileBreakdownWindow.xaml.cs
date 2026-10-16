using System.Windows;
using System.Windows.Input;
using WoWData.Core.Imaging;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Break down this tile": the grouped list of every piece of a map tile and its viewer (TileBreakdownViewModel).</summary>
public partial class TileBreakdownWindow : Window
{
    public TileBreakdownViewModel Vm { get; }

    /// <summary>Full screen (F11) and the 3D keys for the whole window.</summary>
    public Services.PopoutChrome Chrome { get; }

    public TileBreakdownWindow(TileBreakdownViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Chrome = new Services.PopoutChrome(this, () => Viewport);
        Closed += (_, _) => vm.Detach();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                vm.FocusCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void Pieces_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => Vm.Selected = e.NewValue as BreakdownNode;

    /// <summary>An offscreen render of the 3D view (screenshots, tests).</summary>
    public Task<RgbaImage> CaptureAsync(int width, int height) => Viewport.CaptureAsync(width, height);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
