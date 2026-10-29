using System.Windows;
using WoWDataStudio.Services;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Pwd.Password = vm.Password;
    }

    /// <summary>Screenshots: a fixed size, so the maps card can be scrolled into view.</summary>
    public void Place(double width, double height)
    {
        SizeToContent = SizeToContent.Manual;
        Width = width;
        Height = height;
        UpdateLayout();
    }

    private void Pwd_PasswordChanged(object sender, RoutedEventArgs e) => _vm.Password = Pwd.Password;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.ClientOk)
        {
            Dialogs.Error("Check the game folder", _vm.ClientCheck);
            return;
        }
        if (_vm.ApplyServerData(new WoWData.Core.Workspace.AppSettings()) is { } why)
        {
            Dialogs.Error("Check the server data settings", why);
            return;
        }
        if (_vm.ApplyOtherVersions(new WoWData.Core.Workspace.AppSettings()) is { } dbdWhy)
        {
            Dialogs.Error("Check the WoWDBDefs folder", dbdWhy);
            return;
        }
        _vm.RefreshRequested = RefreshBox.IsChecked == true;
        DialogResult = true;
    }
}
