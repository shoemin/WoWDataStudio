using System.IO;
using System.Windows;
using WoWData.Core.Dbc;
using WoWDataStudio.ViewModels;

namespace WoWDataStudio.Views;

/// <summary>The import preview: what a CSV or JSON file would change (changed / added / removed rows), its errors and
/// warnings, and "Apply import" (one undo step).</summary>
public partial class DbcImportWindow : Window
{
    private readonly DbcEditorViewModel _vm;
    private readonly string _file;
    private DbcImportPlan? _plan;
    private readonly bool _ready;

    public DbcImportWindow(DbcEditorViewModel vm, string file)
    {
        InitializeComponent();
        _vm = vm;
        _file = file;
        TitleText.Text = $"Import {Path.GetFileName(file)} into {vm.Table?.Name}";
        _ready = true;
        Plan();
    }

    public DbcImportPlan? CurrentPlan => _plan;

    public DbcMissingRows Missing
    {
        get => RemoveRadio.IsChecked == true ? DbcMissingRows.Remove : DbcMissingRows.Keep;
        set
        {
            RemoveRadio.IsChecked = value == DbcMissingRows.Remove;
            KeepRadio.IsChecked = value == DbcMissingRows.Keep;
        }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Plan();
    }

    private void Plan()
    {
        try
        {
            _plan = _vm.PlanImport(_file, Missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DbcException)
        {
            _plan = new DbcImportPlan();
            _plan.Errors.Add("The file cannot be read: " + e.Message);
        }
        var p = _plan;
        SummaryText.Text = $"{p.RowsRead:N0} rows read. " + (p.IsEmpty ? "The file changes nothing." : p.Preview.Summary);
        ErrorBox.Visibility = p.Errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = string.Join("\n", p.Errors.Take(60)) + (p.Errors.Count > 60 ? $"\n... and {p.Errors.Count - 60} more." : "");
        WarnBox.Visibility = p.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = string.Join("\n", p.Warnings);
        ApplyButton.IsEnabled = p.CanApply;
        Diff.Show(p.Preview, "Now (in the editor)", "From the file");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || !_plan.CanApply) return;
        _vm.ApplyImport(_plan, _file);
        DialogResult = true;
    }
}
