using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Open terrain in Blender": the tile picker. DialogResult true = export (Vm.OpenAfterExport: then open Blender).</summary>
public partial class TerrainExportWindow : Window
{
    public TerrainExportViewModel Vm { get; }

    public TerrainExportWindow(TerrainExportViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Picker.TileClicked += (x, y) => vm.Toggle(x, y);
        Loaded += (_, _) => Picker.CenterOn(vm.CenterX, vm.CenterY);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Vm.OpenAfterExport = true;
        DialogResult = true;
    }

    private void ExportOnly_Click(object sender, RoutedEventArgs e)
    {
        Vm.OpenAfterExport = false;
        DialogResult = true;
    }
}
