using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Convert map..." of the Other versions tab. DialogResult true = a conversion was saved into the workspace.</summary>
public partial class MapConvertWindow : Window
{
    public MapConvertViewModel Vm { get; }

    public MapConvertWindow(MapConvertViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
        Closed += (_, _) => vm.Dispose();
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        await Vm.ConvertCommand.ExecuteAsync(null);
        if (Vm.Saved != null) DialogResult = true;
    }
}
