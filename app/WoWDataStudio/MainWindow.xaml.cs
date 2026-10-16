using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // 0.7.0: unsaved changes of the Data tables tab are not lost silently
        Closing += (_, e) =>
        {
            if (vm.Browser?.Tables is { } tables && !tables.ConfirmLeave()) e.Cancel = true;
        };
    }
}
