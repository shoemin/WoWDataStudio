using System.Windows;
using WoWData.Core.Workspace;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

public partial class BuildPatchWindow : Window
{
    public bool InstallIntoGame => InstallBox.IsChecked == true;

    public BuildPatchWindow(Workspace ws, List<ChangeRow> rows, string dataDir)
    {
        InitializeComponent();
        FileList.ItemsSource = rows;
        // 0.8.0: what goes in and the notes come from Core (ChangeOperations.PlanPatch, shared with "wds changes build-patch")
        var plan = ChangeOperations.PlanPatch(ws);
        CountText.Text = $"Included: {rows.Count} file{(rows.Count == 1 ? "" : "s")}";
        DestRun.Text = plan.Destination;
        BackupNote.Text = plan.BackupNote;
        if (plan.ServerNote.Length > 0)
        {
            ServerNoteText.Text = plan.ServerNote;
            ServerNoteBox.Visibility = Visibility.Visible;
        }
    }

    private void Build_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
