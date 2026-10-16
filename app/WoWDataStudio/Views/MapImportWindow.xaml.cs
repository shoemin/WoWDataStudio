using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The review screen of "Import map changes". DialogResult true = save the selected files.</summary>
public partial class MapImportWindow : Window
{
    public MapImportViewModel Vm { get; }

    public MapImportWindow(MapImportViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
