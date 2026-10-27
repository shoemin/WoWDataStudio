using System.Windows;
using WoWData.Core.Versions;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>
/// M1: another version opened read only in a window of its own, next to the 1.12 window (design 2.4, the user's
/// choice). One window per client; closing the main window closes them.
/// </summary>
public partial class OtherVersionWindow : Window
{
    private static readonly List<OtherVersionWindow> Windows = new();

    public OtherVersionWindowViewModel Vm { get; }

    public OtherVersionWindow(OtherVersionWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = Vm = vm;
        Closed += (_, _) =>
        {
            Windows.Remove(this);
            Vm.Dispose();
        };
    }

    /// <summary>The read-only windows open now.</summary>
    public static IReadOnlyList<OtherVersionWindow> OpenWindows => Windows.ToList();

    /// <summary>Opens <paramref name="copy"/> read only in its window (or brings its window forward).</summary>
    public static OtherVersionWindow Open(MainViewModel main, ClientCopy copy, IReadOnlyList<ClientCopy>? found, bool show = true)
    {
        var existing = Windows.FirstOrDefault(w => string.Equals(w.Vm.Copy.Folder, copy.Folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (show)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
            }
            return existing;
        }
        var vm = new OtherVersionWindowViewModel(main.Session.Paths, main.Session.Settings, copy, () => main.Session.Data, found);
        var win = new OtherVersionWindow(vm);
        Windows.Add(win);
        if (show) win.Show();
        _ = vm.OpenAsync();
        return win;
    }

    /// <summary>Closes every read-only window (when the main window closes).</summary>
    public static void CloseAll()
    {
        foreach (var w in Windows.ToList()) w.Close();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => TexViewer.FitToView();

    private void Actual_Click(object sender, RoutedEventArgs e) => TexViewer.ActualSize();
}
