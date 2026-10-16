using System.IO;
using System.Windows;
using WoWData.Core.Dbc;
using WoWDataStudio.Services;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>"Copy to server...": the edited tables, what a copy would do with each, and Copy / Undo (DbcServerCopy).</summary>
public partial class DbcServerCopyWindow : Window
{
    private readonly DbcEditorViewModel _vm;
    private readonly DbcServerCopy? _copy;

    public DbcServerCopyWindow(DbcEditorViewModel vm, string? table, Func<List<string>>? runningServers = null)
    {
        InitializeComponent();
        _vm = vm;
        _copy = vm.ServerCopy(runningServers);
        Refresh();
    }

    public DbcServerCopy? Service => _copy;

    public void Refresh()
    {
        if (_copy == null || _vm.Workspace == null)
        {
            FolderRun.Text = "(no game folder is open)";
            CopyButton.IsEnabled = UndoButton.IsEnabled = false;
            return;
        }
        FolderRun.Text = _copy.ServerDbcDir + (Directory.Exists(_copy.ServerDbcDir) ? "" : "  (this folder does not exist)");
        var running = _copy.RunningServers();
        ServerState.Text = running.Count > 0 ? $"The world server is running ({string.Join(", ", running)}): stop it before copying." : "The world server is not running.";
        ServerState.Foreground = (System.Windows.Media.Brush)FindResource(running.Count > 0 ? "Bad" : "Good");
        var plan = _copy.Plan(_vm.Workspace);
        ItemList.ItemsSource = plan;
        CopyButton.IsEnabled = running.Count == 0 && plan.Any(i => i.CanCopy) && Directory.Exists(_copy.ServerDbcDir);
        UndoButton.IsEnabled = _copy.LoadHistory().Any(h => h.Undone == null);
        if (plan.Count == 0) ResultText.Text = "You have no edited tables yet. Edit a table and save it to your workspace first.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyNow();

    /// <summary>What "Copy to server" does (also used by the screenshots).</summary>
    public void CopyNow()
    {
        if (_copy == null || _vm.Workspace == null) return;
        try
        {
            var (_, done) = _copy.Copy(_vm.Workspace);
            ResultText.Text = string.Join("\n", done);
            _vm.Toast("Copied to the server. Restart the world server (mangosd).");
        }
        catch (Exception ex) when (ex is DbcException or IOException or UnauthorizedAccessException)
        {
            ResultText.Text = ex.Message;
        }
        Refresh();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_copy == null) return;
        var last = _copy.LoadHistory().LastOrDefault(h => h.Undone == null);
        if (last == null) return;
        if (!ScreenshotRunner.Running && !Dialogs.Confirm("Undo copy to server", $"Put back the server's files from before the copy of {last.Time:yyyy-MM-dd HH:mm} ({string.Join(", ", last.Written.Keys)})?", "Put them back"))
            return;
        try
        {
            ResultText.Text = string.Join("\n", _copy.Undo(last));
        }
        catch (Exception ex) when (ex is DbcException or IOException or UnauthorizedAccessException)
        {
            ResultText.Text = ex.Message;
        }
        Refresh();
    }
}
