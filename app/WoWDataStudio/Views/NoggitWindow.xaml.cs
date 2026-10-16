using System.Windows;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Edit in Noggit": the prepared project, the steps and the facts about Noggit (a separate GPL program).</summary>
public partial class NoggitWindow : Window
{
    public NoggitLaunchViewModel Vm { get; }

    public NoggitWindow(NoggitLaunchViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e) => Close();
}
