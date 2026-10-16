using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The review screen of "Import edited model...". DialogResult true = save the selected changes.</summary>
public partial class ModelImportWindow : Window
{
    public ModelImportViewModel Vm { get; }

    public ModelImportWindow(ModelImportViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
