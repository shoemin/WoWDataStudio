using System.Windows;
using WoWData.Core.Dbc;

namespace WoWDataStudio.Views;

/// <summary>A table comparison in a window: "Compare with game" (it only shows), or the confirmation before saving
/// (<c>primary</c> = the button that confirms; DialogResult true).</summary>
public partial class DbcDiffWindow : Window
{
    public DbcDiffWindow(string title, DbcDiffResult diff, string intro, string? primary, string? beforeLabel = null, string? afterLabel = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        IntroText.Text = intro;
        Result = diff;
        if (beforeLabel != null && afterLabel != null) Diff.Show(diff, beforeLabel, afterLabel);
        else Diff.Show(diff);
        if (primary == null) PrimaryButton.Visibility = Visibility.Collapsed;
        else
        {
            PrimaryButton.Content = primary;
            CancelButton.Content = "Cancel";
        }
    }

    public DbcDiffResult Result { get; }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
