using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Update server maps": the guided server re-extraction and install (ServerUpdateViewModel).</summary>
public partial class ServerUpdateWindow : Window
{
    public ServerUpdateViewModel Vm { get; }

    public ServerUpdateWindow(ServerUpdateViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
