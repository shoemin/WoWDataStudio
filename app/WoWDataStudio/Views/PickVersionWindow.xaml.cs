using System.Windows;
using WoWData.Core.Versions;

namespace WoWDataStudio.Views;

/// <summary>M1: picks one of the modern clients found in the folder of other versions, to open it read only.</summary>
public partial class PickVersionWindow : Window
{
    public PickVersionWindow(IReadOnlyList<ClientCopy> clients)
    {
        InitializeComponent();
        List.ItemsSource = clients;
        // the newest "1.60" build first when there is one (the design's first target), else the first
        List.SelectedItem = clients.Where(c => c.Version.StartsWith("1.60", StringComparison.Ordinal)).OrderByDescending(c => c.Build).FirstOrDefault() ?? clients.FirstOrDefault();
    }

    public ClientCopy? Picked { get; private set; }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ClientCopy c) return;
        Picked = c;
        DialogResult = true;
    }
}
