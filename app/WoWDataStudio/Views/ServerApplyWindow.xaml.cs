using System.Windows;
using WoWData.Core.ServerData;

namespace WoWDataStudio.Views;

/// <summary>"Apply to server...": the exact statements, the rows that changed on the server meanwhile, whether the world
/// server runs, what it needs afterwards (restart or .reload), then "Back up and apply".</summary>
public partial class ServerApplyWindow : Window
{
    private readonly ServerApplyPlan _plan;

    public ServerApplyWindow(ServerApplyPlan plan)
    {
        InitializeComponent();
        _plan = plan;
        TargetText.Text = $"{plan.Script.Rows} row(s) into {plan.Target}: {plan.Script.Summary}" +
                          (plan.AlreadyApplied.Count > 0 ? $" ({plan.AlreadyApplied.Count} of them are already as you want them on the server.)" : "");
        if (plan.Running.Count > 0)
        {
            RunningBox.Visibility = Visibility.Visible;
            RunningText.Text = $"The world server is running ({string.Join(", ", plan.Running)}). Applying is safe, but it keeps the old data in memory until you restart it or reload the tables (see below).";
        }
        if (plan.Conflicts.Count > 0)
        {
            ConflictBox.Visibility = Visibility.Visible;
            ConflictText.Text = string.Join("\n", plan.Conflicts);
        }
        NotesText.Text = string.Join("\n", plan.Notes) +
                         (plan.NonTransactional.Count > 0 ? $"\n{string.Join(", ", plan.NonTransactional)}: these tables cannot roll back (MyISAM); if a statement fails, the backup rows are written back." : "");
        SqlBox.Text = string.Join(Environment.NewLine, plan.Script.Statements);
        Update();
    }

    /// <summary>Apply although rows changed on the server meanwhile.</summary>
    public bool Overwrite => OverwriteBox.IsChecked == true;

    private void Update() => ApplyButton.IsEnabled = !_plan.NothingToDo && (_plan.Conflicts.Count == 0 || Overwrite);

    private void Overwrite_Changed(object sender, RoutedEventArgs e) => Update();

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
