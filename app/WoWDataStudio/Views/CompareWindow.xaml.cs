using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Compare original vs. edited": the compare view in a window. As the last step of an import it also shows the
/// checks and the "Keep this change" / "Cancel" buttons.</summary>
public partial class CompareWindow : Window
{
    public bool Kept { get; private set; }

    public CompareWindow(CompareViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Compare.DataContext = vm;
        Title = "Compare original vs. edited - " + vm.Title;
        Closed += (_, _) => vm.Dispose();
    }

    /// <summary>Turn the window into the import confirmation step.</summary>
    public void AsImportConfirmation(string heading, IEnumerable<string> warnings, IEnumerable<string> notes, IEnumerable<string> facts)
    {
        Title = "Import edited PNG - check the result";
        Heading.Text = heading;
        SubHeading.Text = "Step 3 of 3: check the result. Left: the texture as it is in the game. Right: your edit, as the game will show it. Nothing is saved until you click \"Keep this change\".";
        ConfirmBar.Visibility = Visibility.Visible;
        CloseBar.Visibility = Visibility.Collapsed;
        foreach (var w in warnings) ConfirmText.Children.Add(Line("", w, (Brush)FindResource("Warn")));
        foreach (var n in notes) ConfirmText.Children.Add(Line("", n, (Brush)FindResource("Accent")));
        foreach (var f in facts) ConfirmText.Children.Add(Line("", f, (Brush)FindResource("Good")));
    }

    private static UIElement Line(string glyph, string text, Brush brush)
    {
        var p = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = brush, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        p.Children.Add(icon);
        p.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Kept = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmBar.Visibility == Visibility.Visible) DialogResult = false;
        else Close();
    }
}
