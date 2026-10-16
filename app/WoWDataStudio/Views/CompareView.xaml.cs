using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

public partial class CompareView : UserControl
{
    public CompareView()
    {
        InitializeComponent();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Loaded += (_, _) => Focus();
    }

    private CompareViewModel? Vm => DataContext as CompareViewModel;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Vm is { Mode: CompareMode.Flicker } vm && !e.IsRepeat)
        {
            vm.AutoFlicker = false;
            vm.FlickerShowsOriginal = true;
            e.Handled = true;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Vm is { Mode: CompareMode.Flicker } vm)
        {
            vm.FlickerShowsOriginal = false;
            e.Handled = true;
        }
    }

    private Controls.ZoomPanViewer ActiveViewer => Vm?.Mode switch
    {
        CompareMode.Wipe => WipeViewer,
        CompareMode.Flicker => FlickerViewer,
        CompareMode.Difference => DiffViewer,
        _ => SideLeft,
    };

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        var v = ActiveViewer;
        if (v.ActualWidth > 0) v.FitToView();
        else if (Vm != null) Vm.View.FitPending = true;
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        var v = ActiveViewer;
        if (v.ActualWidth > 0) v.ActualSize();
    }
}
